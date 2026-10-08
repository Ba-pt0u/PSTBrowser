using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PstBrowser.Core.Data;
using PstBrowser.Core.Mail;
using PstBrowser.Core.Text;

namespace PstBrowser.Core.Index
{
    public sealed class IndexProgress
    {
        public bool Running { get; set; }
        public string Phase { get; set; } = "";
        public string CurrentSource { get; set; } = "";
        public long Total { get; set; }
        public long Done { get; set; }
        public double MessagesPerSecond { get; set; }
        public int Errors { get; set; }
        public string LastError { get; set; }
    }

    public sealed class IndexerOptions
    {
        /// <summary>Number of sources indexed in parallel (1 is best on a single hard disk, 2-4 on SSD).</summary>
        public int Parallelism { get; set; } = Math.Max(1, Math.Min(4, Environment.ProcessorCount / 2));
        public bool ComputeSha256 { get; set; } = false;
        public int MaxBodyChars { get; set; } = 1_000_000;
        public bool IndexEmbeddedMessages { get; set; } = true;
        public int BatchSize { get; set; } = 250;
        /// <summary>Re-open the PST periodically to release XstReader caches on very large files.</summary>
        public int ReopenEvery { get; set; } = 25_000;
    }

    /// <summary>
    /// Indexes all pending sources of a workspace in the background.
    /// Pass 1 lists folders and message headers from the PST contents tables (fast: browsing is available quickly),
    /// pass 2 reads each message (recipients, bodies, attachment names, embedded messages) into the full-text index.
    /// Both passes are resumable: progress is committed in small transactions.
    /// </summary>
    public sealed class Indexer
    {
        private readonly Workspace _ws;
        private readonly IndexerOptions _opt;
        private CancellationTokenSource _cts;
        private Task _task;
        private readonly object _progLock = new object();
        private readonly IndexProgress _progress = new IndexProgress();
        private long _doneCounter, _totalCounter;
        private int _errors;
        private readonly Stopwatch _rateWatch = new Stopwatch();
        private long _rateBase;

        public event Action Changed;

        public Indexer(Workspace ws, IndexerOptions options = null)
        {
            _ws = ws;
            _opt = options ?? new IndexerOptions();
        }

        public bool IsRunning => _task != null && !_task.IsCompleted;

        public IndexProgress Progress
        {
            get
            {
                lock (_progLock)
                {
                    _progress.Done = Interlocked.Read(ref _doneCounter);
                    _progress.Total = Interlocked.Read(ref _totalCounter);
                    _progress.Errors = _errors;
                    _progress.Running = IsRunning;
                    double secs = _rateWatch.Elapsed.TotalSeconds;
                    _progress.MessagesPerSecond = secs > 1 ? (_progress.Done - _rateBase) / secs : 0;
                    return new IndexProgress
                    {
                        Running = _progress.Running, Phase = _progress.Phase, CurrentSource = _progress.CurrentSource,
                        Total = _progress.Total, Done = _progress.Done, MessagesPerSecond = _progress.MessagesPerSecond,
                        Errors = _progress.Errors, LastError = _progress.LastError,
                    };
                }
            }
        }

        private void SetPhase(string phase, string source = null)
        {
            lock (_progLock) { _progress.Phase = phase; if (source != null) _progress.CurrentSource = source; }
            Changed?.Invoke();
        }

        private void ReportError(string msg)
        {
            Interlocked.Increment(ref _errors);
            lock (_progLock) _progress.LastError = msg;
        }

        public Task Start()
        {
            if (IsRunning) return _task;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _task = Task.Factory.StartNew(() => Run(token), token, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
            return _task;
        }

        public void Stop() => _cts?.Cancel();

        public async Task StopAndWait()
        {
            Stop();
            try { if (_task != null) await _task.ConfigureAwait(false); } catch { }
        }

        private async Task Run(CancellationToken ct)
        {
            Charsets.EnsureRegistered();
            _errors = 0;
            try
            {
                var sources = _ws.ListSources().Where(s => s.Status != "done").ToList();
                if (sources.Count == 0) { SetPhase("Index à jour"); return; }

                // ---- pass 1: structure (all sources)
                SetPhase("Lecture de la structure");
                Interlocked.Exchange(ref _totalCounter, sources.Count);
                Interlocked.Exchange(ref _doneCounter, 0);
                await ForEachParallel(sources.Where(s => s.Kind == SourceKind.Pst), ct, s => ListPst(s, ct));
                if (ct.IsCancellationRequested) return;

                // ---- pass 2: content
                long pending;
                using (var db = _ws.Open())
                {
                    pending = db.ExecScalarLong("SELECT COUNT(*) FROM messages WHERE indexed=0");
                    pending += sources.Where(s => s.Kind != SourceKind.Pst).Sum(s => EstimateMsgCount(s));
                }
                Interlocked.Exchange(ref _totalCounter, pending);
                Interlocked.Exchange(ref _doneCounter, 0);
                _rateBase = 0;
                _rateWatch.Restart();
                SetPhase("Indexation du contenu");
                await ForEachParallel(sources, ct, s =>
                {
                    if (s.Kind == SourceKind.Pst) IndexPstContent(s, ct);
                    else IndexMsgSource(s, ct);
                });
                if (ct.IsCancellationRequested) { SetPhase("Indexation suspendue"); return; }

                // ---- integrity hashes (optional, after indexing so that browsing is not delayed)
                if (_opt.ComputeSha256)
                {
                    SetPhase("Calcul des empreintes SHA-256");
                    foreach (var s in sources.Where(s => s.Kind == SourceKind.Pst && string.IsNullOrEmpty(s.Sha256)))
                    {
                        if (ct.IsCancellationRequested) break;
                        ComputeHash(s, ct);
                    }
                }
                SetPhase("Optimisation de l'index");
                try
                {
                    using var db = _ws.Open();
                    db.Exec("INSERT INTO fts(fts) VALUES('optimize')");
                    db.Exec("PRAGMA optimize");
                    db.Exec("PRAGMA wal_checkpoint(TRUNCATE)");
                }
                catch (Exception ex) { ReportError("Optimisation : " + ex.Message); }
                SetPhase(_errors > 0 ? $"Indexation terminée ({_errors} erreur(s))" : "Indexation terminée");
            }
            catch (OperationCanceledException) { SetPhase("Indexation suspendue"); }
            catch (Exception ex)
            {
                ReportError(ex.Message);
                SetPhase("Erreur : " + ex.Message);
            }
            finally
            {
                _rateWatch.Stop();
                Changed?.Invoke();
            }
        }

        private Task ForEachParallel(IEnumerable<SourceInfo> items, CancellationToken ct, Action<SourceInfo> body)
        {
            var po = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, _opt.Parallelism), CancellationToken = ct };
            return Task.Run(() =>
            {
                try
                {
                    Parallel.ForEach(items, po, s =>
                    {
                        try { body(s); }
                        catch (OperationCanceledException) { }
                        catch (Exception ex)
                        {
                            ReportError($"{s.DisplayName}: {ex.Message}");
                            try { using var db = _ws.Open(); db.Exec("UPDATE sources SET status='error', error=? WHERE id=?", ex.Message, s.Id); } catch { }
                        }
                    });
                }
                catch (OperationCanceledException) { }
            });
        }

        // ================================================================== PST pass 1

        private void ListPst(SourceInfo s, CancellationToken ct)
        {
            using var db = _ws.Open();
            if (!File.Exists(s.Path)) throw new FileNotFoundException("Fichier introuvable : " + s.Path);
            SetPhase("Lecture de la structure", s.DisplayName);
            db.Exec("UPDATE sources SET status='listing', error=NULL WHERE id=?", s.Id);

            using var pst = new PstReader(s.Path);
            var folders = pst.EnumerateFolders().ToList();

            // Existing folders (resume)
            var existing = new Dictionary<uint, (long id, bool listed)>();
            var st = db.Prepare("SELECT id, nid, listed FROM folders WHERE source_id=?");
            st.Bind(1, s.Id);
            while (st.Step()) existing[(uint)st.GetLong(1)] = (st.GetLong(0), st.GetInt(2) != 0);
            st.Reset();

            foreach (var f in folders)
            {
                ct.ThrowIfCancellationRequested();
                long folderId;
                if (existing.TryGetValue(f.Nid, out var ex))
                {
                    if (ex.listed) continue;
                    folderId = ex.id;
                }
                else
                {
                    db.Exec("INSERT INTO folders(source_id,nid,path,name,depth) VALUES(?,?,?,?,?)", s.Id, (long)f.Nid, f.Path, f.Name, f.Depth);
                    folderId = db.LastInsertRowId;
                }

                db.Begin();
                try
                {
                    // a folder interrupted half-way is listed again from scratch
                    db.Exec("DELETE FROM fts WHERE rowid IN (SELECT id FROM messages WHERE folder_id=?)", folderId);
                    db.Exec("DELETE FROM messages WHERE folder_id=?", folderId);
                    var ins = db.Prepare(@"INSERT INTO messages(source_id,folder_id,mailbox_id,nid,subject,sender_name,to_text,cc_text,date,size,has_att,msg_class,importance,indexed)
                                           VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,0)");
                    int n = 0;
                    foreach (var r in pst.ListMessages(f.Nid))
                    {
                        ins.Reset().BindAll(s.Id, folderId, s.MailboxId, (long)r.Nid, r.Subject, r.SenderName, r.DisplayTo, r.DisplayCc,
                            r.Date.HasValue ? SqliteStmt.ToUnixMs(r.Date.Value) : (object)null, r.Size, r.HasAttachments, r.MessageClass, r.Importance);
                        ins.Run();
                        n++;
                        if ((n & 1023) == 0) ct.ThrowIfCancellationRequested();
                    }
                    db.Exec("UPDATE folders SET listed=1, item_count=? WHERE id=?", n, folderId);
                    db.Exec("UPDATE sources SET msg_count=(SELECT COUNT(*) FROM messages WHERE source_id=?) WHERE id=?", s.Id, s.Id);
                    db.Commit();
                }
                catch { db.Rollback(); throw; }
            }
            db.Exec("UPDATE sources SET status='listed' WHERE id=?", s.Id);
            Interlocked.Increment(ref _doneCounter);
            Changed?.Invoke();
        }

        // ================================================================== PST pass 2

        private sealed class Extracted
        {
            public long Id;
            public string Subject, SenderName, SenderEmail, To, Cc, MessageId, Class, AttNames;
            public string FtsSender, FtsRecipients, Body, Error;
            public long? Date; public long Size; public bool HasAtt; public int AttCount; public int Importance;
        }

        private void IndexPstContent(SourceInfo s, CancellationToken ct)
        {
            using var db = _ws.Open();
            SetPhase("Indexation du contenu", s.DisplayName);
            db.Exec("UPDATE sources SET status='indexing' WHERE id=?", s.Id);

            var todo = new List<(long id, uint nid, uint folderNid)>();
            var st = db.Prepare("SELECT m.id, m.nid, f.nid FROM messages m JOIN folders f ON f.id=m.folder_id WHERE m.source_id=? AND m.indexed=0 ORDER BY m.id");
            st.Bind(1, s.Id);
            while (st.Step()) todo.Add((st.GetLong(0), (uint)st.GetLong(1), (uint)st.GetLong(2)));
            st.Reset();

            PstReader pst = null;
            var batch = new List<Extracted>(_opt.BatchSize);
            int sinceOpen = 0;
            try
            {
                foreach (var (id, nid, folderNid) in todo)
                {
                    ct.ThrowIfCancellationRequested();
                    if (pst == null || sinceOpen >= _opt.ReopenEvery)
                    {
                        pst?.Dispose();
                        pst = new PstReader(s.Path);
                        sinceOpen = 0;
                    }
                    sinceOpen++;
                    Extracted e;
                    try
                    {
                        var item = pst.OpenMessage(folderNid, nid);
                        e = Extract(item);
                    }
                    catch (Exception ex)
                    {
                        e = new Extracted { Error = ex.Message };
                        ReportError($"{s.DisplayName}: message {nid}: {ex.Message}");
                    }
                    e.Id = id;
                    batch.Add(e);
                    if (batch.Count >= _opt.BatchSize) { Flush(db, s.Id, batch); batch.Clear(); }
                }
                Flush(db, s.Id, batch);
                batch.Clear();
                db.Exec("UPDATE sources SET status='done' WHERE id=?", s.Id);
                _ws.RecomputeDuplicates(s.MailboxId, db);
            }
            finally
            {
                if (batch.Count > 0) { try { Flush(db, s.Id, batch); } catch { } }
                pst?.Dispose();
                Changed?.Invoke();
            }
        }

        private Extracted Extract(IMailItem m)
        {
            var e = new Extracted
            {
                Subject = m.Subject,
                SenderName = m.SenderName,
                SenderEmail = m.SenderEmail,
                MessageId = m.InternetMessageId,
                Class = m.MessageClass,
                Importance = m.Importance,
                Size = m.Size,
            };
            var d = m.Date;
            e.Date = d.HasValue ? SqliteStmt.ToUnixMs(d.Value) : (long?)null;
            var recips = m.Recipients;
            e.To = Join(recips.Where(r => r.Kind == RecipientKind.To));
            e.Cc = Join(recips.Where(r => r.Kind == RecipientKind.Cc));
            e.FtsSender = $"{m.SenderName} {m.SenderEmail}".Trim();
            e.FtsRecipients = string.Join(" ", recips.Select(r => $"{r.Name} {r.Email}"));

            var sb = new StringBuilder(GetBodyText(m));
            var atts = m.Attachments;
            var visible = atts.Where(IsVisibleAttachment).ToList();
            e.AttCount = visible.Count;
            e.HasAtt = visible.Count > 0;
            e.AttNames = string.Join(" | ", visible.Select(a => a.FileName));
            if (_opt.IndexEmbeddedMessages)
            {
                foreach (var a in atts.Where(a => a.IsEmbeddedMessage).Take(20))
                {
                    if (sb.Length > _opt.MaxBodyChars) break;
                    try
                    {
                        var em = m.OpenEmbeddedMessage(a.Index);
                        sb.Append("\n\n--- ").Append(em.Subject).Append(" ---\n");
                        sb.Append(em.SenderName).Append(' ').Append(em.SenderEmail).Append('\n');
                        sb.Append(GetBodyText(em));
                        var inner = em.Attachments.Where(x => !x.IsHidden).Select(x => x.FileName).ToList();
                        if (inner.Count > 0) e.AttNames += " | " + string.Join(" | ", inner);
                    }
                    catch { }
                }
            }
            e.Body = sb.Length > _opt.MaxBodyChars ? sb.ToString(0, _opt.MaxBodyChars) : sb.ToString();
            return e;
        }

        /// <summary>Real attachments, excluding images embedded in the HTML body.</summary>
        public static bool IsVisibleAttachment(AttachmentInfo a)
            => !a.IsHidden && !(a.IsInline && !string.IsNullOrEmpty(a.ContentId));

        private static string Join(IEnumerable<RecipientInfo> r) => string.Join("; ", r.Select(x => x.ToString()));

        public static string GetBodyText(IMailItem m)
        {
            try
            {
                var plain = m.PlainBody;
                if (!string.IsNullOrWhiteSpace(plain)) return plain;
                var html = m.HtmlBody;
                if (!string.IsNullOrWhiteSpace(html)) return HtmlText.ToText(html);
                var rtf = m.RtfBody;
                if (!string.IsNullOrWhiteSpace(rtf)) return RtfConverter.ToText(rtf);
            }
            catch { }
            return "";
        }

        private void Flush(SqliteDb db, long sourceId, List<Extracted> batch)
        {
            if (batch.Count == 0) return;
            db.Begin();
            try
            {
                var upd = db.Prepare(@"UPDATE messages SET subject=COALESCE(?,subject), sender_name=COALESCE(?,sender_name), sender_email=?, to_text=COALESCE(?,to_text),
                                       cc_text=COALESCE(?,cc_text), date=COALESCE(?,date), size=CASE WHEN ?>0 THEN ? ELSE size END, has_att=?, att_count=?, att_names=?,
                                       msg_class=COALESCE(?,msg_class), importance=?, message_id=?, indexed=1, error=? WHERE id=?");
                var del = db.Prepare("DELETE FROM fts WHERE rowid=?");
                var ins = db.Prepare("INSERT INTO fts(rowid,subject,sender,recipients,body,attachments) VALUES(?,?,?,?,?,?)");
                foreach (var e in batch)
                {
                    if (e.Error != null)
                    {
                        db.Exec("UPDATE messages SET indexed=1, error=? WHERE id=?", e.Error, e.Id);
                        continue;
                    }
                    upd.Reset().BindAll(e.Subject, e.SenderName, e.SenderEmail, NullIfEmpty(e.To), NullIfEmpty(e.Cc), e.Date, e.Size, e.Size, e.HasAtt, e.AttCount,
                        NullIfEmpty(e.AttNames), e.Class, e.Importance, e.MessageId, null, e.Id);
                    upd.Run();
                    del.Reset().Bind(1, e.Id).Run();
                    ins.Reset().BindAll(e.Id, e.Subject, e.FtsSender, e.FtsRecipients, e.Body, e.AttNames);
                    ins.Run();
                }
                db.Exec("UPDATE sources SET indexed_count=(SELECT COUNT(*) FROM messages WHERE source_id=? AND indexed=1) WHERE id=?", sourceId, sourceId);
                db.Commit();
            }
            catch { db.Rollback(); throw; }
            Interlocked.Add(ref _doneCounter, batch.Count);
            Changed?.Invoke();
        }

        private static string NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

        // ================================================================== MSG sources (single pass)

        private static IEnumerable<string> MsgFiles(SourceInfo s)
        {
            if (s.Kind == SourceKind.MsgFile) return File.Exists(s.Path) ? new[] { s.Path } : Array.Empty<string>();
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive };
            return Directory.EnumerateFiles(s.Path, "*.msg", opts);
        }

        private long EstimateMsgCount(SourceInfo s)
        {
            try { return MsgFiles(s).LongCount(); } catch { return 0; }
        }

        private void IndexMsgSource(SourceInfo s, CancellationToken ct)
        {
            using var db = _ws.Open();
            SetPhase("Indexation du contenu", s.DisplayName);
            db.Exec("UPDATE sources SET status='indexing', error=NULL WHERE id=?", s.Id);
            string root = s.Kind == SourceKind.MsgFolder ? s.Path : Path.GetDirectoryName(s.Path);
            string rootName = s.Kind == SourceKind.MsgFolder ? Path.GetFileName(s.Path.TrimEnd('\\', '/')) : "Fichiers MSG";

            var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var st = db.Prepare("SELECT file_path FROM messages WHERE source_id=? AND indexed=1");
            st.Bind(1, s.Id);
            while (st.Step()) done.Add(st.GetString(0));
            st.Reset();
            db.Exec("DELETE FROM messages WHERE source_id=? AND indexed=0", s.Id);

            var folderIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            st = db.Prepare("SELECT id, path FROM folders WHERE source_id=?");
            st.Bind(1, s.Id);
            while (st.Step()) folderIds[st.GetString(1)] = st.GetLong(0);
            st.Reset();

            long FolderFor(string file)
            {
                string rel = Path.GetRelativePath(root, Path.GetDirectoryName(file) ?? root).Replace('\\', '/');
                string path = rel == "." ? rootName : rootName + "/" + rel;
                if (folderIds.TryGetValue(path, out var id)) return id;
                // make sure parents exist so the tree is complete
                int slash = path.LastIndexOf('/');
                string name = slash < 0 ? path : path.Substring(slash + 1);
                db.Exec("INSERT INTO folders(source_id,nid,path,name,depth,listed) VALUES(?,NULL,?,?,?,1)", s.Id, path, name, path.Count(c => c == '/') + 1);
                id = db.LastInsertRowId;
                folderIds[path] = id;
                return id;
            }

            var batch = new List<(Extracted e, long folderId, string file)>();
            void FlushMsg()
            {
                if (batch.Count == 0) return;
                db.Begin();
                try
                {
                    var ins = db.Prepare(@"INSERT INTO messages(source_id,folder_id,mailbox_id,file_path,subject,sender_name,sender_email,to_text,cc_text,date,size,has_att,att_count,att_names,msg_class,importance,message_id,indexed,error)
                                           VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,1,?)");
                    var fts = db.Prepare("INSERT INTO fts(rowid,subject,sender,recipients,body,attachments) VALUES(?,?,?,?,?,?)");
                    foreach (var (e, folderId, file) in batch)
                    {
                        ins.Reset().BindAll(s.Id, folderId, s.MailboxId, file, e.Subject ?? (e.Error != null ? Path.GetFileName(file) : null), e.SenderName, e.SenderEmail,
                            NullIfEmpty(e.To), NullIfEmpty(e.Cc), e.Date, e.Size, e.HasAtt, e.AttCount, NullIfEmpty(e.AttNames), e.Class, e.Importance, e.MessageId, e.Error);
                        ins.Run();
                        long id = db.LastInsertRowId;
                        if (e.Error == null)
                        {
                            fts.Reset().BindAll(id, e.Subject, e.FtsSender, e.FtsRecipients, e.Body, e.AttNames);
                            fts.Run();
                        }
                    }
                    db.Exec("UPDATE folders SET item_count=(SELECT COUNT(*) FROM messages WHERE folder_id=folders.id) WHERE source_id=?", s.Id);
                    db.Exec("UPDATE sources SET msg_count=(SELECT COUNT(*) FROM messages WHERE source_id=?), indexed_count=(SELECT COUNT(*) FROM messages WHERE source_id=?) WHERE id=?", s.Id, s.Id, s.Id);
                    db.Commit();
                }
                catch { db.Rollback(); throw; }
                Interlocked.Add(ref _doneCounter, batch.Count);
                batch.Clear();
                Changed?.Invoke();
            }

            foreach (var file in MsgFiles(s))
            {
                ct.ThrowIfCancellationRequested();
                if (done.Contains(file)) { Interlocked.Increment(ref _doneCounter); continue; }
                Extracted e;
                try
                {
                    using var m = new MsgMailItem(file);
                    e = Extract(m);
                    if (e.Size == 0) try { e.Size = new FileInfo(file).Length; } catch { }
                }
                catch (Exception ex)
                {
                    e = new Extracted { Error = ex.Message };
                    ReportError($"{Path.GetFileName(file)}: {ex.Message}");
                }
                batch.Add((e, FolderFor(file), file));
                if (batch.Count >= _opt.BatchSize) FlushMsg();
            }
            FlushMsg();
            db.Exec("UPDATE sources SET status='done' WHERE id=?", s.Id);
            _ws.RecomputeDuplicates(s.MailboxId, db);
        }

        // ================================================================== integrity

        private void ComputeHash(SourceInfo s, CancellationToken ct)
        {
            SetPhase("Calcul des empreintes SHA-256", s.DisplayName);
            using var sha = SHA256.Create();
            using var fs = new FileStream(s.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            var buf = new byte[1 << 20];
            int n;
            while ((n = fs.Read(buf, 0, buf.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                sha.TransformBlock(buf, 0, n, null, 0);
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            var hex = Convert.ToHexString(sha.Hash);
            using var db = _ws.Open();
            db.Exec("UPDATE sources SET sha256=? WHERE id=?", hex, s.Id);
        }
    }
}
