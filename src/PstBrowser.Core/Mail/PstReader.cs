using System;
using System.Collections.Generic;
using System.IO;
using XstReader;
using XstReader.ElementProperties;

namespace PstBrowser.Core.Mail
{
    public sealed class PstFolderInfo
    {
        public uint Nid { get; set; }
        public uint ParentNid { get; set; }
        public string Name { get; set; }
        /// <summary>Path relative to the PST root, segments separated by '/'.</summary>
        public string Path { get; set; }
        public int Depth { get; set; }
        public int ContentCount { get; set; }
        public string ContainerClass { get; set; }
    }

    /// <summary>Lightweight row from a folder contents table (no read of the message node).</summary>
    public sealed class PstMessageRow
    {
        public uint Nid { get; set; }
        public string Subject { get; set; }
        public string SenderName { get; set; }
        public string DisplayTo { get; set; }
        public string DisplayCc { get; set; }
        public DateTime? Date { get; set; }
        public long Size { get; set; }
        public bool HasAttachments { get; set; }
        public string MessageClass { get; set; }
        public int Importance { get; set; }
    }

    /// <summary>
    /// Read-only access to one PST/OST file. Not thread-safe: use one instance per thread,
    /// or serialize calls (see <see cref="Lock"/>).
    /// </summary>
    public sealed class PstReader : IDisposable
    {
        private readonly XstFile _file;
        private readonly Dictionary<uint, XstFolder> _folders = new Dictionary<uint, XstFolder>();
        public object Lock { get; } = new object();
        public string Path { get; }

        public PstReader(string path)
        {
            Path = path;
            // XstFile opens the file with FileAccess.Read only: the PST is never modified.
            _file = new XstFile(path);
            _ = _file.RootFolder; // validates header, throws on non-PST files
        }

        public IEnumerable<PstFolderInfo> EnumerateFolders()
        {
            var root = _file.RootFolder;
            _folders[root.Nid.dwValue] = root;
            var stack = new Stack<(XstFolder f, string path, int depth)>();
            stack.Push((root, "", 0));
            var result = new List<PstFolderInfo>();
            while (stack.Count > 0)
            {
                var (f, path, depth) = stack.Pop();
                IEnumerable<XstFolder> children;
                try { children = f.GetFolders(); } catch { continue; }
                var list = new List<XstFolder>();
                try { foreach (var c in children) list.Add(c); } catch { }
                // push in reverse so that the output is in display order
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    var c = list[i];
                    string name = SafeName(c);
                    string childPath = path.Length == 0 ? name : path + "/" + name;
                    stack.Push((c, childPath, depth + 1));
                }
                if (f != root)
                {
                    _folders[f.Nid.dwValue] = f;
                    result.Add(new PstFolderInfo
                    {
                        Nid = f.Nid.dwValue,
                        ParentNid = f.ParentFolder?.Nid.dwValue ?? 0,
                        Name = SafeName(f),
                        Path = path,
                        Depth = depth,
                        ContentCount = SafeCount(f),
                        ContainerClass = f.Properties.Peek(PropertyCanonicalName.PidTagContainerClass)?.Value as string,
                    });
                }
            }
            return result;
        }

        private static string SafeName(XstFolder f)
        {
            string n = null;
            try { n = PstMailItem.Clean(f.DisplayName); } catch { }
            if (string.IsNullOrEmpty(n)) n = "(sans nom)";
            return n.Replace('/', '∕');
        }

        private static int SafeCount(XstFolder f)
        {
            try { return f.ContentCount; } catch { return 0; }
        }

        private XstFolder GetFolder(uint folderNid)
        {
            if (_folders.TryGetValue(folderNid, out var f)) return f;
            f = new XstFolder(_file, new NID(folderNid));
            _folders[folderNid] = f;
            return f;
        }

        /// <summary>Reads the contents table of a folder (fast: no message node is read).</summary>
        public IEnumerable<PstMessageRow> ListMessages(uint folderNid)
        {
            var folder = GetFolder(folderNid);
            var contentsNid = NID.TypedNID(EnidType.CONTENTS_TABLE, folder.Nid);
            IEnumerable<XstMessage> rows;
            try
            {
                rows = _file.Ltp.ReadTable<XstMessage>(contentsNid, (m, id) => m.Initialize(new NID(id), folder), null);
            }
            catch (Exception) { yield break; }

            using var e = rows.GetEnumerator();
            while (true)
            {
                XstMessage m;
                try
                {
                    if (!e.MoveNext()) break;
                    m = e.Current;
                }
                catch (Exception) { yield break; }
                if (m == null) continue;
                var row = new PstMessageRow { Nid = m.Nid.dwValue };
                try
                {
                    row.Subject = Str(m, PstMailItem.TagSubject);
                    row.SenderName = Str(m, PstMailItem.TagSentReprName) ?? Str(m, PstMailItem.TagSenderName);
                    row.DisplayTo = Str(m, PstMailItem.TagDisplayTo);
                    row.DisplayCc = Str(m, PstMailItem.TagDisplayCc);
                    row.Date = (Val(m, PstMailItem.TagDelivery) as DateTime?) ?? (Val(m, PstMailItem.TagSubmit) as DateTime?);
                    var size = Val(m, PstMailItem.TagSize);
                    row.Size = size is int si ? si : 0;
                    row.HasAttachments = Val(m, PstMailItem.TagFlags) is int fl && (fl & 0x10) != 0;
                    row.MessageClass = Str(m, PstMailItem.TagClass);
                    row.Importance = Val(m, PstMailItem.TagImportance) is int imp ? imp : 1;
                }
                catch { }
                yield return row;
            }
        }

        private static object Val(XstMessage m, ushort tag)
        {
            try { return m.Properties.Peek((PropertyCanonicalName)tag)?.Value; } catch { return null; }
        }
        private static string Str(XstMessage m, ushort tag) => PstMailItem.Clean(Val(m, tag) as string);

        /// <summary>Opens a message by its node id (as stored in the index).</summary>
        public IMailItem OpenMessage(uint folderNid, uint messageNid)
        {
            var folder = GetFolder(folderNid);
            var m = new XstMessage(folder, null);
            m.Initialize(new NID(messageNid), folder);
            return new PstMailItem(m);
        }

        public void Dispose()
        {
            try { _file.Dispose(); } catch { }
        }
    }
}
