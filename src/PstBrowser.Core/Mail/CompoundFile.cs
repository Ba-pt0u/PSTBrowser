using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PstBrowser.Core.Mail
{
    /// <summary>
    /// Minimal read-only reader for the Compound File Binary format ([MS-CFB]) used by .msg files.
    /// Streams are read on demand from the underlying file.
    /// </summary>
    public sealed class CompoundFile : IDisposable
    {
        public sealed class Entry
        {
            public string Name;
            public byte Type;          // 1 storage, 2 stream, 5 root
            public uint Left, Right, Child;
            public uint StartSector;
            public long Size;
            public readonly List<Entry> Children = new List<Entry>();
            public bool IsStorage => Type == 1 || Type == 5;
            public bool IsStream => Type == 2;
            public override string ToString() => Name;
        }

        private const uint ENDOFCHAIN = 0xFFFFFFFE, FREESECT = 0xFFFFFFFF, NOSTREAM = 0xFFFFFFFF;
        private readonly Stream _s;
        private readonly bool _ownsStream;
        private readonly int _sectorSize, _miniSectorSize;
        private readonly uint _miniCutoff;
        private readonly uint[] _fat;
        private uint[] _miniFat;
        private readonly List<Entry> _entries = new List<Entry>();
        private byte[] _miniStream; // loaded lazily (bounded by the mini stream size, usually small)

        public Entry Root { get; }

        public CompoundFile(string path) : this(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536), true) { }

        public CompoundFile(Stream s, bool ownsStream)
        {
            _s = s;
            _ownsStream = ownsStream;
            var h = new byte[512];
            ReadAt(0, h, 0, 512);
            if (BitConverter.ToUInt64(h, 0) != 0xE11AB1A1E011CFD0UL)
                throw new InvalidDataException("Not a compound file (.msg)");
            int sectorShift = BitConverter.ToUInt16(h, 0x1E);
            int miniShift = BitConverter.ToUInt16(h, 0x20);
            if (sectorShift != 9 && sectorShift != 12) throw new InvalidDataException("Invalid sector size");
            _sectorSize = 1 << sectorShift;
            _miniSectorSize = 1 << miniShift;
            uint numFatSectors = BitConverter.ToUInt32(h, 0x2C);
            uint firstDir = BitConverter.ToUInt32(h, 0x30);
            _miniCutoff = BitConverter.ToUInt32(h, 0x38);
            uint firstMiniFat = BitConverter.ToUInt32(h, 0x3C);
            uint firstDifat = BitConverter.ToUInt32(h, 0x44);
            uint numDifat = BitConverter.ToUInt32(h, 0x48);

            // DIFAT: list of FAT sectors
            var fatSectors = new List<uint>();
            for (int i = 0; i < 109; i++)
            {
                uint v = BitConverter.ToUInt32(h, 0x4C + i * 4);
                if (v == FREESECT || v == ENDOFCHAIN) continue;
                fatSectors.Add(v);
            }
            uint difat = firstDifat;
            var sec = new byte[_sectorSize];
            int guard = 0;
            while (difat != ENDOFCHAIN && difat != FREESECT && guard++ < numDifat + 1)
            {
                ReadSector(difat, sec);
                int per = _sectorSize / 4 - 1;
                for (int i = 0; i < per; i++)
                {
                    uint v = BitConverter.ToUInt32(sec, i * 4);
                    if (v != FREESECT && v != ENDOFCHAIN) fatSectors.Add(v);
                }
                difat = BitConverter.ToUInt32(sec, per * 4);
            }
            if (fatSectors.Count > numFatSectors && numFatSectors > 0) fatSectors.RemoveRange((int)numFatSectors, fatSectors.Count - (int)numFatSectors);

            int perFat = _sectorSize / 4;
            _fat = new uint[fatSectors.Count * perFat];
            for (int f = 0; f < fatSectors.Count; f++)
            {
                ReadSector(fatSectors[f], sec);
                Buffer.BlockCopy(sec, 0, _fat, f * _sectorSize, _sectorSize);
            }

            // Directory
            var dirBytes = ReadChain(firstDir, -1);
            for (int off = 0; off + 128 <= dirBytes.Length; off += 128)
            {
                int nameLen = BitConverter.ToUInt16(dirBytes, off + 64);
                var e = new Entry
                {
                    Name = nameLen >= 2 ? Encoding.Unicode.GetString(dirBytes, off, Math.Min(64, nameLen) - 2) : "",
                    Type = dirBytes[off + 66],
                    Left = BitConverter.ToUInt32(dirBytes, off + 68),
                    Right = BitConverter.ToUInt32(dirBytes, off + 72),
                    Child = BitConverter.ToUInt32(dirBytes, off + 76),
                    StartSector = BitConverter.ToUInt32(dirBytes, off + 116),
                    Size = _sectorSize == 512 ? BitConverter.ToUInt32(dirBytes, off + 120) : (long)BitConverter.ToUInt64(dirBytes, off + 120),
                };
                _entries.Add(e);
            }
            if (_entries.Count == 0 || _entries[0].Type != 5) throw new InvalidDataException("Compound file has no root entry");
            Root = _entries[0];
            var visited = new HashSet<uint>();
            BuildTree(Root, visited);

            if (firstMiniFat != ENDOFCHAIN && firstMiniFat != FREESECT)
            {
                var mf = ReadChain(firstMiniFat, -1);
                _miniFat = new uint[mf.Length / 4];
                Buffer.BlockCopy(mf, 0, _miniFat, 0, _miniFat.Length * 4);
            }
            else _miniFat = Array.Empty<uint>();
        }

        private void BuildTree(Entry storage, HashSet<uint> visited)
        {
            if (storage.Child == NOSTREAM) return;
            var stack = new Stack<uint>();
            stack.Push(storage.Child);
            while (stack.Count > 0)
            {
                uint id = stack.Pop();
                if (id == NOSTREAM || id >= _entries.Count || !visited.Add(id)) continue;
                var e = _entries[(int)id];
                storage.Children.Add(e);
                stack.Push(e.Left);
                stack.Push(e.Right);
                if (e.IsStorage) BuildTree(e, visited);
            }
        }

        private void ReadAt(long pos, byte[] buf, int off, int len)
        {
            lock (_s)
            {
                _s.Seek(pos, SeekOrigin.Begin);
                int read = 0;
                while (read < len)
                {
                    int r = _s.Read(buf, off + read, len - read);
                    if (r <= 0) { Array.Clear(buf, off + read, len - read); break; }
                    read += r;
                }
            }
        }

        private void ReadSector(uint sector, byte[] buf) => ReadAt((long)(sector + 1) * _sectorSize, buf, 0, _sectorSize);

        private byte[] ReadChain(uint start, long size)
        {
            var ms = new MemoryStream();
            var sec = new byte[_sectorSize];
            uint cur = start;
            long guard = 0, maxSectors = _fat.Length + 1;
            while (cur != ENDOFCHAIN && cur != FREESECT && cur < _fat.Length + 1 && guard++ < maxSectors)
            {
                ReadSector(cur, sec);
                ms.Write(sec, 0, _sectorSize);
                if (size >= 0 && ms.Length >= size) break;
                if (cur >= _fat.Length) break;
                cur = _fat[cur];
            }
            var bytes = ms.ToArray();
            if (size >= 0 && bytes.Length > size) Array.Resize(ref bytes, (int)size);
            return bytes;
        }

        private void CopyChain(uint start, long size, Stream target)
        {
            var sec = new byte[_sectorSize];
            uint cur = start;
            long remaining = size, guard = 0, maxSectors = _fat.Length + 1;
            while (remaining > 0 && cur != ENDOFCHAIN && cur != FREESECT && cur < _fat.Length && guard++ < maxSectors)
            {
                ReadSector(cur, sec);
                int n = (int)Math.Min(remaining, _sectorSize);
                target.Write(sec, 0, n);
                remaining -= n;
                cur = _fat[cur];
            }
        }

        private byte[] MiniStream => _miniStream ??= ReadChain(Root.StartSector, Root.Size);

        public byte[] ReadStream(Entry e, long maxBytes = long.MaxValue)
        {
            if (e == null || !e.IsStream) return null;
            if (e.Size > maxBytes) return null;
            if (e.Size < _miniCutoff)
            {
                var ms = MiniStream;
                var result = new byte[e.Size];
                uint cur = e.StartSector;
                long pos = 0; int guard = 0;
                while (pos < e.Size && cur < _miniFat.Length + 1 && guard++ < _miniFat.Length + 1)
                {
                    long src = (long)cur * _miniSectorSize;
                    int n = (int)Math.Min(_miniSectorSize, e.Size - pos);
                    if (src + n > ms.Length) break;
                    Buffer.BlockCopy(ms, (int)src, result, (int)pos, n);
                    pos += n;
                    if (cur >= _miniFat.Length) break;
                    cur = _miniFat[cur];
                }
                return result;
            }
            return ReadChain(e.StartSector, e.Size);
        }

        public void CopyStream(Entry e, Stream target)
        {
            if (e.Size < _miniCutoff)
            {
                var b = ReadStream(e);
                target.Write(b, 0, b.Length);
            }
            else CopyChain(e.StartSector, e.Size, target);
        }

        public static Entry Find(Entry storage, string name)
        {
            foreach (var c in storage.Children)
                if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) return c;
            return null;
        }

        public void Dispose()
        {
            if (_ownsStream) _s.Dispose();
        }
    }
}
