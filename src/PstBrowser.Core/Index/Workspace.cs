using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PstBrowser.Core.Data;

namespace PstBrowser.Core.Index
{
    public enum SourceKind { Pst, MsgFolder, MsgFile }

    public sealed class SourceInfo
    {
        public long Id { get; set; }
        public long MailboxId { get; set; }
        public string MailboxName { get; set; }
        public SourceKind Kind { get; set; }
        public string Path { get; set; }
        public string DisplayName { get; set; }
        public long Size { get; set; }
        public string Status { get; set; }
        public string Error { get; set; }
        public long MessageCount { get; set; }
        public long IndexedCount { get; set; }
        public string Sha256 { get; set; }
        public bool FileMissing => Kind == SourceKind.MsgFolder ? !Directory.Exists(Path) : !File.Exists(Path);
    }

    public sealed class MailboxInfo
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public long MessageCount { get; set; }
    }

    /// <summary>A folder of the merged tree of a mailbox (split PST parts with the same path are merged).</summary>
    public sealed class FolderNode
    {
        public long MailboxId { get; set; }
        public string Path { get; set; }
        public string Name { get; set; }
        public int Depth { get; set; }
        public long Count { get; set; }
        /// <summary>Items in this folder and all its sub-folders.</summary>
        public long Total { get; set; }
        public List<FolderNode> Children { get; } = new List<FolderNode>();
    }

    /// <summary>
    /// A case workspace: a folder holding the SQLite index (metadata + full-text) of a set of PST/MSG sources.
    /// The sources themselves are only ever opened read-only.
    /// </summary>
    public sealed class Workspace : IDisposable
    {
        public const string DbFileName = "pstbrowser-index.db";
        public const int SchemaVersion = 1;

        public string Folder { get; }
        public string DbPath => System.IO.Path.Combine(Folder, DbFileName);
        private readonly SqliteDb _db; // connection for the UI thread / API calls
        public object DbLock { get; } = new object();

        private Workspace(string folder)
        {
            Folder = folder;
            _db = Open();
        }

        public static Workspace OpenOrCreate(string folder)
        {
            Directory.CreateDirectory(folder);
            var ws = new Workspace(folder);
            ws.EnsureSchema();
            return ws;
        }

        public static bool Exists(string folder) => File.Exists(System.IO.Path.Combine(folder, DbFileName));

        /// <summary>Opens a new connection on the index (one per thread).</summary>
        public SqliteDb Open()
        {
            var db = new SqliteDb(DbPath);
            db.Exec("PRAGMA journal_mode=WAL");
            db.Exec("PRAGMA synchronous=NORMAL");
            db.Exec("PRAGMA temp_store=MEMORY");
            db.Exec("PRAGMA cache_size=-65536");
            db.Exec("PRAGMA foreign_keys=OFF");
            return db;
        }

        public SqliteDb Db => _db;

        private void EnsureSchema()
        {
            lock (DbLock)
            {
                _db.Exec(@"
CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT);
CREATE TABLE IF NOT EXISTS mailboxes(id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE COLLATE NOCASE);
CREATE TABLE IF NOT EXISTS sources(
  id INTEGER PRIMARY KEY,
  mailbox_id INTEGER NOT NULL,
  kind INTEGER NOT NULL,
  path TEXT NOT NULL UNIQUE,
  display_name TEXT,
  size INTEGER,
  mtime INTEGER,
  sha256 TEXT,
  status TEXT NOT NULL DEFAULT 'pending',
  error TEXT,
  msg_count INTEGER NOT NULL DEFAULT 0,
  indexed_count INTEGER NOT NULL DEFAULT 0,
  added_at INTEGER);
CREATE TABLE IF NOT EXISTS folders(
  id INTEGER PRIMARY KEY,
  source_id INTEGER NOT NULL,
  nid INTEGER,
  path TEXT NOT NULL,
  name TEXT NOT NULL,
  depth INTEGER NOT NULL DEFAULT 1,
  item_count INTEGER NOT NULL DEFAULT 0,
  listed INTEGER NOT NULL DEFAULT 0);
CREATE INDEX IF NOT EXISTS folders_source ON folders(source_id, path);
CREATE TABLE IF NOT EXISTS messages(
  id INTEGER PRIMARY KEY,
  source_id INTEGER NOT NULL,
  folder_id INTEGER NOT NULL,
  mailbox_id INTEGER NOT NULL,
  nid INTEGER,
  file_path TEXT,
  subject TEXT,
  sender_name TEXT,
  sender_email TEXT,
  to_text TEXT,
  cc_text TEXT,
  date INTEGER,
  size INTEGER,
  has_att INTEGER NOT NULL DEFAULT 0,
  att_count INTEGER NOT NULL DEFAULT 0,
  att_names TEXT,
  msg_class TEXT,
  importance INTEGER,
  message_id TEXT,
  dup_of INTEGER,
  indexed INTEGER NOT NULL DEFAULT 0,
  error TEXT);
CREATE INDEX IF NOT EXISTS messages_folder_date ON messages(folder_id, date);
CREATE INDEX IF NOT EXISTS messages_mailbox_date ON messages(mailbox_id, date);
CREATE INDEX IF NOT EXISTS messages_date ON messages(date);
CREATE INDEX IF NOT EXISTS messages_msgid ON messages(mailbox_id, message_id);
CREATE INDEX IF NOT EXISTS messages_pending ON messages(source_id, indexed);
CREATE VIRTUAL TABLE IF NOT EXISTS fts USING fts5(subject, sender, recipients, body, attachments, tokenize='unicode61 remove_diacritics 2')");
                var v = _db.ExecScalarString("SELECT value FROM meta WHERE key='schema'");
                if (v == null)
                {
                    _db.Exec("INSERT INTO meta(key,value) VALUES('schema',?)", SchemaVersion.ToString());
                    _db.Exec("INSERT INTO meta(key,value) VALUES('created',?)", DateTime.UtcNow.ToString("o"));
                }
                else if (int.Parse(v) > SchemaVersion)
                    throw new InvalidOperationException("Cet index a été créé par une version plus récente de PstBrowser.");
            }
        }

        // ---------------------------------------------------------------- sources & mailboxes

        private static readonly Regex SplitSuffix = new Regex(@"^(?<base>.+?)(?:[ _.\-]\(?\d{1,4}\)?|\(\d{1,4}\))$", RegexOptions.Compiled);

        /// <summary>Mailbox name guessed from a file name: "jdupont@x.com_2.pst" and "jdupont@x.com.pst" → "jdupont@x.com".</summary>
        public static string GuessMailboxName(string path, IEnumerable<string> siblings = null)
        {
            string stem = System.IO.Path.GetFileNameWithoutExtension(path.TrimEnd('\\', '/'));
            if (Directory.Exists(path)) stem = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
            var m = SplitSuffix.Match(stem);
            if (!m.Success) return stem;
            string b = m.Groups["base"].Value.Trim();
            // Only strip the numeric suffix if another file of the batch shares the same base (or the base is an e-mail address)
            if (b.Contains('@')) return b;
            if (siblings != null && siblings.Any(s => !string.Equals(s, path, StringComparison.OrdinalIgnoreCase) &&
                                                     string.Equals(GuessBaseOnly(s), b, StringComparison.OrdinalIgnoreCase)))
                return b;
            return stem;
        }

        private static string GuessBaseOnly(string path)
        {
            string stem = System.IO.Path.GetFileNameWithoutExtension(path.TrimEnd('\\', '/'));
            var m = SplitSuffix.Match(stem);
            return m.Success ? m.Groups["base"].Value.Trim() : stem;
        }

        public long GetOrCreateMailbox(string name)
        {
            lock (DbLock)
            {
                var id = _db.ExecScalarLong("SELECT id FROM mailboxes WHERE name=?", name);
                if (id != 0) return id;
                _db.Exec("INSERT INTO mailboxes(name) VALUES(?)", name);
                return _db.LastInsertRowId;
            }
        }

        /// <summary>Adds files (.pst/.ost/.msg) and folders (scanned for PST files and MSG files). Returns the new sources.</summary>
        public List<SourceInfo> AddSources(IEnumerable<string> paths)
        {
            var expanded = new List<(string path, SourceKind kind)>();
            foreach (var p0 in paths)
            {
                var p = System.IO.Path.GetFullPath(p0);
                if (Directory.Exists(p))
                {
                    foreach (var f in SafeEnumerate(p, "*.pst").Concat(SafeEnumerate(p, "*.ost")))
                        expanded.Add((f, SourceKind.Pst));
                    if (SafeEnumerate(p, "*.msg").Any())
                        expanded.Add((p, SourceKind.MsgFolder));
                }
                else if (File.Exists(p))
                {
                    var ext = System.IO.Path.GetExtension(p).ToLowerInvariant();
                    if (ext == ".pst" || ext == ".ost") expanded.Add((p, SourceKind.Pst));
                    else if (ext == ".msg") expanded.Add((p, SourceKind.MsgFile));
                }
            }
            var all = expanded.Select(e => e.path).ToList();
            var added = new List<SourceInfo>();
            foreach (var (path, kind) in expanded)
            {
                lock (DbLock)
                {
                    if (_db.ExecScalarLong("SELECT COUNT(*) FROM sources WHERE path=?", path) > 0) continue;
                }
                string mbName = kind == SourceKind.MsgFile
                    ? System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path)) ?? "Fichiers MSG"
                    : GuessMailboxName(path, all);
                long mb = GetOrCreateMailbox(mbName);
                long size = 0, mtime = 0;
                try
                {
                    if (kind == SourceKind.MsgFolder) { mtime = Directory.GetLastWriteTimeUtc(path).Ticks; }
                    else { var fi = new FileInfo(path); size = fi.Length; mtime = fi.LastWriteTimeUtc.Ticks; }
                }
                catch { }
                lock (DbLock)
                {
                    _db.Exec("INSERT INTO sources(mailbox_id,kind,path,display_name,size,mtime,status,added_at) VALUES(?,?,?,?,?,?,'pending',?)",
                        mb, (int)kind, path, System.IO.Path.GetFileName(path.TrimEnd('\\', '/')), size, mtime, DateTime.UtcNow.Ticks);
                    added.Add(GetSource(_db.LastInsertRowId));
                }
            }
            return added;
        }

        private static IEnumerable<string> SafeEnumerate(string dir, string pattern)
        {
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive };
            try { return Directory.EnumerateFiles(dir, pattern, opts).ToList(); } catch { return new List<string>(); }
        }

        public SourceInfo GetSource(long id) => ListSources().FirstOrDefault(s => s.Id == id);

        public List<SourceInfo> ListSources()
        {
            lock (DbLock)
            {
                var list = new List<SourceInfo>();
                var st = _db.Prepare(@"SELECT s.id,s.mailbox_id,b.name,s.kind,s.path,s.display_name,s.size,s.status,s.error,s.msg_count,s.indexed_count,s.sha256
                                       FROM sources s JOIN mailboxes b ON b.id=s.mailbox_id ORDER BY b.name, s.display_name");
                while (st.Step())
                    list.Add(new SourceInfo
                    {
                        Id = st.GetLong(0), MailboxId = st.GetLong(1), MailboxName = st.GetString(2), Kind = (SourceKind)st.GetInt(3),
                        Path = st.GetString(4), DisplayName = st.GetString(5), Size = st.GetLong(6), Status = st.GetString(7),
                        Error = st.GetString(8), MessageCount = st.GetLong(9), IndexedCount = st.GetLong(10), Sha256 = st.GetString(11),
                    });
                st.Reset();
                return list;
            }
        }

        public List<MailboxInfo> ListMailboxes()
        {
            lock (DbLock)
            {
                var list = new List<MailboxInfo>();
                var st = _db.Prepare(@"SELECT b.id, b.name, COALESCE((SELECT SUM(msg_count) FROM sources s WHERE s.mailbox_id=b.id),0)
                                       FROM mailboxes b WHERE EXISTS(SELECT 1 FROM sources s WHERE s.mailbox_id=b.id) ORDER BY b.name");
                while (st.Step()) list.Add(new MailboxInfo { Id = st.GetLong(0), Name = st.GetString(1), MessageCount = st.GetLong(2) });
                st.Reset();
                return list;
            }
        }

        /// <summary>Moves a source to another (possibly new) mailbox.</summary>
        public void SetSourceMailbox(long sourceId, string mailboxName)
        {
            long mb = GetOrCreateMailbox(mailboxName.Trim());
            lock (DbLock)
            {
                _db.Begin();
                try
                {
                    _db.Exec("UPDATE sources SET mailbox_id=? WHERE id=?", mb, sourceId);
                    _db.Exec("UPDATE messages SET mailbox_id=? WHERE source_id=?", mb, sourceId);
                    _db.Commit();
                }
                catch { _db.Rollback(); throw; }
                CleanupMailboxes();
            }
            RecomputeDuplicates(mb);
        }

        public void RenameMailbox(long mailboxId, string newName)
        {
            newName = newName.Trim();
            if (newName.Length == 0) return;
            lock (DbLock)
            {
                long existing = _db.ExecScalarLong("SELECT id FROM mailboxes WHERE name=? AND id<>?", newName, mailboxId);
                if (existing != 0)
                {
                    // merge into the existing mailbox
                    _db.Exec("UPDATE sources SET mailbox_id=? WHERE mailbox_id=?", existing, mailboxId);
                    _db.Exec("UPDATE messages SET mailbox_id=? WHERE mailbox_id=?", existing, mailboxId);
                    CleanupMailboxes();
                }
                else _db.Exec("UPDATE mailboxes SET name=? WHERE id=?", newName, mailboxId);
            }
        }

        private void CleanupMailboxes()
            => _db.Exec("DELETE FROM mailboxes WHERE NOT EXISTS(SELECT 1 FROM sources s WHERE s.mailbox_id=mailboxes.id)");

        /// <summary>Removes a source and everything indexed from it (the source file itself is untouched).</summary>
        public void RemoveSource(long sourceId)
        {
            lock (DbLock)
            {
                _db.Begin();
                try
                {
                    _db.Exec("DELETE FROM fts WHERE rowid IN (SELECT id FROM messages WHERE source_id=?)", sourceId);
                    _db.Exec("DELETE FROM messages WHERE source_id=?", sourceId);
                    _db.Exec("DELETE FROM folders WHERE source_id=?", sourceId);
                    _db.Exec("DELETE FROM sources WHERE id=?", sourceId);
                    CleanupMailboxes();
                    _db.Commit();
                }
                catch { _db.Rollback(); throw; }
            }
        }

        /// <summary>Marks duplicates (same Internet Message-ID within a mailbox): every copy but the first gets dup_of = first id.</summary>
        public void RecomputeDuplicates(long mailboxId, SqliteDb db = null)
        {
            void Run(SqliteDb d)
            {
                d.Exec("UPDATE messages SET dup_of=NULL WHERE mailbox_id=? AND dup_of IS NOT NULL", mailboxId);
                d.Exec(@"UPDATE messages SET dup_of=(SELECT MIN(m2.id) FROM messages m2 WHERE m2.mailbox_id=messages.mailbox_id AND m2.message_id=messages.message_id)
                         WHERE mailbox_id=? AND message_id IS NOT NULL AND message_id<>''
                           AND id > (SELECT MIN(m3.id) FROM messages m3 WHERE m3.mailbox_id=messages.mailbox_id AND m3.message_id=messages.message_id)", mailboxId);
            }
            if (db != null) Run(db);
            else lock (DbLock) Run(_db);
        }

        // ---------------------------------------------------------------- folder tree

        public List<FolderNode> GetFolderTree(long mailboxId, bool hideEmpty = false)
        {
            var flat = new List<FolderNode>();
            lock (DbLock)
            {
                var st = _db.Prepare(@"SELECT f.path, MIN(f.name), MIN(f.depth), SUM(f.item_count) FROM folders f JOIN sources s ON s.id=f.source_id
                                       WHERE s.mailbox_id=? GROUP BY f.path ORDER BY f.path");
                st.Bind(1, mailboxId);
                while (st.Step())
                    flat.Add(new FolderNode { MailboxId = mailboxId, Path = st.GetString(0), Name = st.GetString(1), Depth = st.GetInt(2), Count = st.GetLong(3) });
                st.Reset();
            }
            // Build the hierarchy from paths (parents may be missing if a split part only contains sub-folders)
            var byPath = new Dictionary<string, FolderNode>(StringComparer.Ordinal);
            var roots = new List<FolderNode>();
            foreach (var n in flat.OrderBy(n => n.Path.Count(c => c == '/')).ThenBy(n => n.Path, StringComparer.CurrentCultureIgnoreCase))
            {
                byPath[n.Path] = n;
                int slash = n.Path.LastIndexOf('/');
                if (slash < 0) { roots.Add(n); continue; }
                var parentPath = n.Path.Substring(0, slash);
                var parent = EnsureNode(parentPath, byPath, roots, mailboxId);
                parent.Children.Add(n);
            }
            long ComputeTotal(FolderNode n) { n.Total = n.Count + n.Children.Sum(ComputeTotal); return n.Total; }
            foreach (var r in roots) ComputeTotal(r);
            if (hideEmpty)
            {
                void Prune(List<FolderNode> list) { list.RemoveAll(x => x.Total == 0); foreach (var x in list) Prune(x.Children); }
                Prune(roots);
            }
            return roots;
        }

        private static FolderNode EnsureNode(string path, Dictionary<string, FolderNode> byPath, List<FolderNode> roots, long mb)
        {
            if (byPath.TryGetValue(path, out var n)) return n;
            int slash = path.LastIndexOf('/');
            n = new FolderNode { MailboxId = mb, Path = path, Name = slash < 0 ? path : path.Substring(slash + 1), Depth = path.Count(c => c == '/') + 1 };
            byPath[path] = n;
            if (slash < 0) roots.Add(n);
            else EnsureNode(path.Substring(0, slash), byPath, roots, mb).Children.Add(n);
            return n;
        }

        public string GetMeta(string key)
        {
            lock (DbLock) return _db.ExecScalarString("SELECT value FROM meta WHERE key=?", key);
        }

        public void SetMeta(string key, string value)
        {
            lock (DbLock) _db.Exec("INSERT INTO meta(key,value) VALUES(?,?) ON CONFLICT(key) DO UPDATE SET value=excluded.value", key, value);
        }

        public bool HasPendingSources()
        {
            lock (DbLock) return _db.ExecScalarLong("SELECT COUNT(*) FROM sources WHERE status<>'done'") > 0;
        }

        /// <summary>Marks a source to be indexed again from scratch (e.g. after the file was replaced).</summary>
        public void ResetSource(long sourceId)
        {
            lock (DbLock)
            {
                _db.Begin();
                try
                {
                    _db.Exec("DELETE FROM fts WHERE rowid IN (SELECT id FROM messages WHERE source_id=?)", sourceId);
                    _db.Exec("DELETE FROM messages WHERE source_id=?", sourceId);
                    _db.Exec("DELETE FROM folders WHERE source_id=?", sourceId);
                    _db.Exec("UPDATE sources SET status='pending', error=NULL, msg_count=0, indexed_count=0, sha256=NULL WHERE id=?", sourceId);
                    _db.Commit();
                }
                catch { _db.Rollback(); throw; }
            }
        }

        public (long total, long indexed) GetCounts()
        {
            lock (DbLock)
            {
                return (_db.ExecScalarLong("SELECT COUNT(*) FROM messages"), _db.ExecScalarLong("SELECT COUNT(*) FROM messages WHERE indexed=1"));
            }
        }

        public void Dispose()
        {
            lock (DbLock) _db.Dispose();
        }
    }
}
