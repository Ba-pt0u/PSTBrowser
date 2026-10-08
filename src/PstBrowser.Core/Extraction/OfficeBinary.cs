using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using PstBrowser.Core.Mail;
using PstBrowser.Core.Text;

namespace PstBrowser.Core.Extraction
{
    /// <summary>
    /// Binary Office formats stored in a compound file: Word 97-2003 (.doc), Excel 97-2003 (.xls), PowerPoint 97-2003 (.ppt).
    /// Only the text is read: no macro or object is executed or interpreted.
    /// </summary>
    internal static class OfficeBinary
    {
        private const long MaxStream = 100L * 1024 * 1024;

        public static void Extract(byte[] data, ExtractContext ctx)
        {
            CompoundFile cf;
            try { cf = new CompoundFile(new MemoryStream(data, false), true); }
            catch (Exception ex) when (ex is InvalidDataException || ex is ArgumentException || ex is IndexOutOfRangeException)
            { throw new UnsupportedFormatException("Fichier Office illisible"); }
            using (cf)
            {
                var root = cf.Root;
                if (CompoundFile.Find(root, "EncryptedPackage") != null) throw new EncryptedException();
                var word = CompoundFile.Find(root, "WordDocument");
                if (word != null) { Doc(cf, word, ctx.Sink); return; }
                var wb = CompoundFile.Find(root, "Workbook") ?? CompoundFile.Find(root, "Book");
                if (wb != null) { Xls(cf, wb, ctx.Sink); return; }
                var ppt = CompoundFile.Find(root, "PowerPoint Document");
                if (ppt != null) { Ppt(cf, ppt, ctx.Sink); return; }
                throw new UnsupportedFormatException("Fichier OLE de type inconnu");
            }
        }

        private static Encoding Cp1252 => Charsets.TryGet(1252) ?? Encoding.Latin1;

        // ------------------------------------------------------------ Word (.doc)

        private static void Doc(CompoundFile cf, CompoundFile.Entry wordEntry, TextSink sink)
        {
            var w = cf.ReadStream(wordEntry, MaxStream);
            if (w == null || w.Length < 0x01AA) throw new InvalidDataException("Flux WordDocument invalide");
            if (BitConverter.ToUInt16(w, 0) != 0xA5EC) throw new InvalidDataException("En-tête Word invalide");
            ushort nFib = BitConverter.ToUInt16(w, 2);
            ushort flags = BitConverter.ToUInt16(w, 0x0A);
            if ((flags & 0x0100) != 0) throw new EncryptedException();

            var text = new StringBuilder();
            bool done = false;
            if (nFib >= 0x00C1)
            {
                var table = CompoundFile.Find(cf.Root, (flags & 0x0200) != 0 ? "1Table" : "0Table");
                var t = table != null ? cf.ReadStream(table, MaxStream) : null;
                uint fcClx = BitConverter.ToUInt32(w, 0x01A2), lcbClx = BitConverter.ToUInt32(w, 0x01A6);
                if (t != null && lcbClx > 0 && fcClx < t.Length) done = ReadPieces(w, t, (int)fcClx, (int)Math.Min(lcbClx, (uint)(t.Length - fcClx)), text);
            }
            if (!done)
            {
                // Word 6/95 (or damaged piece table): the text is stored contiguously between fcMin and fcMac
                int fcMin = BitConverter.ToInt32(w, 0x18), fcMac = BitConverter.ToInt32(w, 0x1C);
                if (fcMin > 0 && fcMac > fcMin && fcMac <= w.Length) text.Append(Cp1252.GetString(w, fcMin, fcMac - fcMin));
            }
            Clean(text.ToString(), sink);
        }

        /// <summary>Reads the piece table (Clx → Pcdt → PlcPcd) and appends the text of every piece.</summary>
        private static bool ReadPieces(byte[] w, byte[] t, int clxOffset, int clxLength, StringBuilder text)
        {
            int pos = clxOffset, end = clxOffset + clxLength;
            while (pos < end && t[pos] == 0x01) // Prc: skip the property modifiers
            {
                if (pos + 3 > end) return false;
                pos += 3 + BitConverter.ToUInt16(t, pos + 1);
            }
            if (pos + 5 > end || t[pos] != 0x02) return false;
            int lcb = BitConverter.ToInt32(t, pos + 1);
            int plc = pos + 5;
            if (lcb < 4 || plc + lcb > t.Length) return false;
            int n = (lcb - 4) / 12;
            if (n <= 0 || n > 2_000_000) return false;
            int pcdBase = plc + 4 * (n + 1);
            for (int i = 0; i < n; i++)
            {
                int cpStart = BitConverter.ToInt32(t, plc + 4 * i), cpEnd = BitConverter.ToInt32(t, plc + 4 * (i + 1));
                long count = (long)cpEnd - cpStart;
                if (count <= 0 || count > 100_000_000) continue;
                uint fc = BitConverter.ToUInt32(t, pcdBase + 8 * i + 2);
                if ((fc & 0x40000000) != 0)
                {
                    long off = (fc & ~0x40000000u) / 2;
                    if (off >= w.Length) continue;
                    int len = (int)Math.Min(count, w.Length - off);
                    text.Append(Cp1252.GetString(w, (int)off, len));
                }
                else
                {
                    long off = fc;
                    if (off >= w.Length) continue;
                    int len = (int)Math.Min(count * 2, (w.Length - off) & ~1L);
                    text.Append(Encoding.Unicode.GetString(w, (int)off, len));
                }
                if (text.Length > 50_000_000) break;
            }
            return true;
        }

        /// <summary>Word control characters → text: paragraph / line breaks, cell marks, field codes dropped.</summary>
        private static void Clean(string raw, TextSink sink)
        {
            int fieldDepth = 0;
            var instruction = new Stack<bool>();
            var sb = new StringBuilder(raw.Length);
            foreach (var c in raw)
            {
                switch (c)
                {
                    case '\r': case '\v': case '\f': if (instruction.Count == 0 || !instruction.Peek()) sb.Append('\n'); break;
                    case '\a': sb.Append('\t'); break;
                    case '\u0013': instruction.Push(true); fieldDepth++; break;           // field begin: instruction follows
                    case '\u0014': if (instruction.Count > 0) { instruction.Pop(); instruction.Push(false); } break; // separator: result follows
                    case '\u0015': if (instruction.Count > 0) instruction.Pop(); break;   // field end
                    default:
                        if (c < ' ' && c != '\t' && c != '\n') break;
                        if (instruction.Count > 0 && instruction.Peek()) break;
                        sb.Append(c);
                        break;
                }
            }
            sink.Append(sb.ToString());
        }

        // ------------------------------------------------------------ Excel (.xls)

        private static void Xls(CompoundFile cf, CompoundFile.Entry entry, TextSink sink)
        {
            var d = cf.ReadStream(entry, MaxStream);
            if (d == null || d.Length < 8) throw new InvalidDataException("Flux Workbook invalide");
            var sstSegments = new List<(int start, int len)>();
            var sheetNames = new List<string>();
            int pos = 0;
            bool inSst = false;
            while (pos + 4 <= d.Length)
            {
                ushort id = BitConverter.ToUInt16(d, pos), size = BitConverter.ToUInt16(d, pos + 2);
                int body = pos + 4;
                if (body + size > d.Length) break;
                if (id == 0x002F) throw new EncryptedException(); // FILEPASS
                if (id == 0x0085 && size >= 8)                    // BOUNDSHEET
                {
                    int cch = d[body + 6], opt = d[body + 7];
                    int bytes = (opt & 1) != 0 ? cch * 2 : cch;
                    if (body + 8 + bytes <= body + size)
                        sheetNames.Add(((opt & 1) != 0 ? Encoding.Unicode : Cp1252).GetString(d, body + 8, bytes));
                }
                if (id == 0x00FC) { sstSegments.Clear(); sstSegments.Add((body, size)); inSst = true; }
                else if (id == 0x003C && inSst) sstSegments.Add((body, size));  // CONTINUE
                else inSst = false;
                pos = body + size;
            }
            foreach (var n in sheetNames) { sink.Append(n); sink.Append('\n'); }
            if (sstSegments.Count > 0) ReadSst(d, sstSegments, sink);
        }

        private sealed class SegReader
        {
            private readonly byte[] _d; private readonly List<(int start, int len)> _segs;
            public int Seg; public int Pos; // offset inside the segment

            public SegReader(byte[] d, List<(int, int)> segs) { _d = d; _segs = segs; }
            public bool Eof => Seg >= _segs.Count;
            public int Available => Eof ? 0 : _segs[Seg].len - Pos;

            public void NextSegment() { Seg++; Pos = 0; }

            public int Byte()
            {
                while (!Eof && Pos >= _segs[Seg].len) NextSegment();
                if (Eof) return -1;
                return _d[_segs[Seg].start + Pos++];
            }

            public int U16() { int a = Byte(), b = Byte(); return a < 0 || b < 0 ? -1 : a | (b << 8); }
            public long U32() { long a = U16(), b = U16(); return a < 0 || b < 0 ? -1 : a | (b << 16); }

            public void Skip(long n)
            {
                while (n > 0 && !Eof)
                {
                    int take = (int)Math.Min(n, Available);
                    if (take == 0) { NextSegment(); continue; }
                    Pos += take; n -= take;
                }
            }

            public byte[] Chars(int bytes) // up to the end of the current segment
            {
                var b = new byte[bytes];
                Buffer.BlockCopy(_d, _segs[Seg].start + Pos, b, 0, bytes);
                Pos += bytes;
                return b;
            }
        }

        /// <summary>Shared string table; strings may straddle CONTINUE records, which repeat the "wide" option byte.</summary>
        private static void ReadSst(byte[] d, List<(int start, int len)> segs, TextSink sink)
        {
            var r = new SegReader(d, segs);
            r.Skip(4); // cstTotal
            long unique = r.U32();
            if (unique < 0 || unique > 20_000_000) return;
            for (long i = 0; i < unique && !r.Eof && !sink.Full; i++)
            {
                int cch = r.U16(), flags = r.Byte();
                if (cch < 0 || flags < 0) break;
                bool wide = (flags & 1) != 0, rich = (flags & 8) != 0, ext = (flags & 4) != 0;
                int runs = rich ? r.U16() : 0;
                long extSize = ext ? r.U32() : 0;
                if (runs < 0 || extSize < 0) break;
                var sb = new StringBuilder(cch);
                int remaining = cch;
                bool first = true;
                while (remaining > 0 && !r.Eof)
                {
                    if (r.Available == 0)
                    {
                        r.NextSegment();
                        if (r.Eof) break;
                        wide = (r.Byte() & 1) != 0; // option byte at the start of a continuation
                        if (r.Available == 0) continue;
                    }
                    first = false;
                    int per = wide ? 2 : 1;
                    int take = Math.Min(remaining, r.Available / per);
                    if (take == 0) { r.Skip(r.Available); continue; }
                    var bytes = r.Chars(take * per);
                    sb.Append(wide ? Encoding.Unicode.GetString(bytes) : Cp1252.GetString(bytes));
                    remaining -= take;
                }
                r.Skip(runs * 4L + extSize);
                if (sb.Length > 0) { sink.Append(sb.ToString()); sink.Append('\n'); }
            }
        }

        // ------------------------------------------------------------ PowerPoint (.ppt)

        private static void Ppt(CompoundFile cf, CompoundFile.Entry entry, TextSink sink)
        {
            var cur = CompoundFile.Find(cf.Root, "Current User");
            if (cur != null)
            {
                var c = cf.ReadStream(cur, 1 << 20);
                if (c != null && c.Length >= 16 && BitConverter.ToUInt32(c, 12) == 0xF3D1C4DF) throw new EncryptedException();
            }
            var d = cf.ReadStream(entry, MaxStream);
            if (d == null) return;
            Walk(d, 0, d.Length, sink, 0);
        }

        private static void Walk(byte[] d, int pos, int end, TextSink sink, int level)
        {
            while (pos + 8 <= end && !sink.Full)
            {
                int verInst = BitConverter.ToUInt16(d, pos);
                ushort type = BitConverter.ToUInt16(d, pos + 2);
                uint len = BitConverter.ToUInt32(d, pos + 4);
                int body = pos + 8;
                long bodyEnd = (long)body + len;
                if (bodyEnd > end) bodyEnd = end;
                if ((verInst & 0xF) == 0xF) { if (level < 40) Walk(d, body, (int)bodyEnd, sink, level + 1); }
                else if (type == 0x0FA0 && bodyEnd > body) { sink.Append(Encoding.Unicode.GetString(d, body, (int)((bodyEnd - body) & ~1L)).Replace('\r', '\n').Replace('\v', '\n')); sink.Append('\n'); }
                else if (type == 0x0FA8 && bodyEnd > body) { sink.Append(Cp1252.GetString(d, body, (int)(bodyEnd - body)).Replace('\r', '\n').Replace('\v', '\n')); sink.Append('\n'); }
                pos = (int)bodyEnd;
            }
        }
    }
}
