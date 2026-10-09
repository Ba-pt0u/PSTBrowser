using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PstBrowser.Core.Analysis;
using PstBrowser.Core.Data;
using PstBrowser.Core.Mail;

namespace PstBrowser.Core.Index
{
    /// <summary>
    /// Investigation data: headers and dates read from each message (pass 2, or the header pass for messages indexed by an earlier
    /// version), then the case-wide analysis: conversation threads, look-alike domains, sensitive data.
    /// </summary>
    public sealed partial class Indexer
    {
        private static long? Ms(DateTime? d) => d.HasValue ? SqliteStmt.ToUnixMs(d.Value) : (long?)null;

        /// <summary>Fills the header, date and thread fields of an extracted message.</summary>
        private static void FillAnalysis(Extracted e, IMailItem m)
        {
            MessageAnalysis an;
            try { an = MessageAnalyzer.Analyze(m); }
            catch (Exception) { return; } // unreadable headers: the message stays "not analysed" for the flags, nothing else is lost
            e.InReplyTo ??= an.InReplyTo;
            e.Refs = an.References;
            e.ConvKey = an.ConversationKey;
            e.Created = Ms(m.CreatedDate);
            e.Modified = Ms(m.ModifiedDate);
            e.ReplyTo = an.Header.ReplyTo;
            e.ReturnPath = an.Header.ReturnPath;
            e.Spf = an.Header.Auth.Spf; e.Dkim = an.Header.Auth.Dkim; e.Dmarc = an.Header.Auth.Dmarc;
            e.Spoof = (int)an.Header.Flags;
            e.DateFlags = (int)an.Dates.Flags;
        }

        // ================================================================== header pass (messages indexed before schema 3)

        private async Task HeaderPass(CancellationToken ct)
        {
            long pending;
            List<SourceInfo> sources;
            using (var db = _ws.Open())
            {
                pending = db.ExecScalarLong("SELECT COUNT(*) FROM messages WHERE indexed=1 AND hdr_done=0");
                if (pending == 0) return;
                var ids = new HashSet<long>();
                var st = db.Prepare("SELECT DISTINCT source_id FROM messages WHERE indexed=1 AND hdr_done=0");
                while (st.Step()) ids.Add(st.GetLong(0));
                st.Reset();
                sources = _ws.ListSources().Where(s => ids.Contains(s.Id)).ToList();
            }
            Interlocked.Exchange(ref _totalCounter, pending);
            Interlocked.Exchange(ref _doneCounter, 0);
            _rateBase = 0;
            _rateWatch.Restart();
            SetPhase("Analyse des en-têtes");
            await ForEachParallel(sources, ct, s => AnalyzeHeaders(s, ct), markSourceError: false);
        }

        private void AnalyzeHeaders(SourceInfo s, CancellationToken ct)
        {
            if (s.FileMissing) throw new System.IO.FileNotFoundException("Fichier introuvable : " + s.Path);
            using var db = _ws.Open();
            SetPhase("Analyse des en-têtes", s.DisplayName);
            var todo = new List<(long id, uint nid, uint folderNid, string file)>();
            var st = db.Prepare(@"SELECT m.id, m.nid, f.nid, m.file_path FROM messages m JOIN folders f ON f.id=m.folder_id
                                  WHERE m.source_id=? AND m.indexed=1 AND m.hdr_done=0 ORDER BY m.id");
            st.Bind(1, s.Id);
            while (st.Step()) todo.Add((st.GetLong(0), (uint)st.GetLong(1), (uint)st.GetLong(2), st.GetString(3)));
            st.Reset();

            PstReader pst = null;
            int sinceOpen = 0;
            var batch = new List<Extracted>();
            try
            {
                foreach (var (id, nid, folderNid, file) in todo)
                {
                    ct.ThrowIfCancellationRequested();
                    var e = new Extracted { Id = id };
                    try
                    {
                        if (s.Kind == SourceKind.Pst)
                        {
                            if (pst == null || sinceOpen >= _opt.ReopenEvery) { pst?.Dispose(); pst = new PstReader(s.Path); sinceOpen = 0; }
                            sinceOpen++;
                            FillAnalysis(e, pst.OpenMessage(folderNid, nid));
                        }
                        else
                        {
                            using var m = new MsgMailItem(file);
                            FillAnalysis(e, m);
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { ReportError($"{s.DisplayName}: en-têtes du message {id}: {ex.Message}"); }
                    batch.Add(e);
                    if (batch.Count >= 250) { FlushHeaders(db, batch); batch.Clear(); }
                }
            }
            finally
            {
                if (batch.Count > 0) { try { FlushHeaders(db, batch); } catch { } }
                pst?.Dispose();
                Changed?.Invoke();
            }
        }

        private void FlushHeaders(SqliteDb db, List<Extracted> batch)
        {
            db.Begin();
            try
            {
                var upd = db.Prepare(@"UPDATE messages SET in_reply_to=COALESCE(in_reply_to,?), refs=?, conv_key=?, created_date=?, modified_date=?, reply_to=?, return_path=?,
                                       auth_spf=?, auth_dkim=?, auth_dmarc=?, spoof_flags=(spoof_flags & 40) | ?, date_flags=?, hdr_done=1 WHERE id=?");
                foreach (var e in batch)
                    upd.Reset().BindAll(e.InReplyTo, e.Refs, e.ConvKey, e.Created, e.Modified, e.ReplyTo, e.ReturnPath, e.Spf, e.Dkim, e.Dmarc, e.Spoof, e.DateFlags, e.Id).Run();
                db.Commit();
            }
            catch { db.Rollback(); throw; }
            Interlocked.Add(ref _doneCounter, batch.Count);
            Changed?.Invoke();
        }

        // ================================================================== case-wide analysis

        private void DerivedPass(CancellationToken ct)
        {
            using var db = _ws.Open();
            SetPhase("Reconstitution des fils de conversation");
            BuildThreads(db, ct);
            ct.ThrowIfCancellationRequested();
            SetPhase("Recherche des domaines ressemblants");
            FindImpersonation(db, ct);
            ct.ThrowIfCancellationRequested();
            SetPhase("Détection des données sensibles");
            ScanSensitive(db, ct);
            ct.ThrowIfCancellationRequested();
            if (!ct.IsCancellationRequested) _ws.DerivedDirty = false;
        }

        private void BuildThreads(SqliteDb db, CancellationToken ct)
        {
            var items = new List<ThreadItem>();
            var current = new Dictionary<long, (long thread, int count)>();
            var st = db.Prepare(@"SELECT id, message_id, in_reply_to, refs, conv_key, COALESCE(NULLIF(conversation_topic,''), subject), date, sender_email, to_text, thread_id, thread_count
                                  FROM messages WHERE indexed=1");
            while (st.Step())
            {
                var to = st.GetString(8);
                items.Add(new ThreadItem
                {
                    Id = st.GetLong(0), MessageId = st.GetString(1), InReplyTo = st.GetString(2), References = st.GetString(3), ConversationKey = st.GetString(4),
                    Topic = st.GetString(5), Date = st.GetLongOrNull(6),
                    PartyText = st.GetString(7) + " " + (to != null && to.Length > 600 ? to.Substring(0, 600) : to),
                });
                if (!st.IsNull(9)) current[st.GetLong(0)] = (st.GetLong(9), st.GetInt(10));
            }
            st.Reset();
            Interlocked.Exchange(ref _totalCounter, items.Count);
            Interlocked.Exchange(ref _doneCounter, 0);
            var result = ThreadBuilder.Build(items);
            var changed = items.Where(t => !current.TryGetValue(t.Id, out var c) || c != result[t.Id]).ToList();
            for (int i = 0; i < changed.Count; i += 5000)
            {
                ct.ThrowIfCancellationRequested();
                db.Begin();
                try
                {
                    var upd = db.Prepare("UPDATE messages SET thread_id=?, thread_count=? WHERE id=?");
                    foreach (var t in changed.Skip(i).Take(5000)) upd.Reset().BindAll(result[t.Id].thread, result[t.Id].count, t.Id).Run();
                    db.Commit();
                }
                catch { db.Rollback(); throw; }
                Interlocked.Add(ref _doneCounter, Math.Min(5000, changed.Count - i));
                Changed?.Invoke();
            }
            Interlocked.Exchange(ref _doneCounter, items.Count);
        }

        /// <summary>Look-alike sender domains and display names reused with an unusual address (bits 8 and 32 of spoof_flags).</summary>
        private void FindImpersonation(SqliteDb db, CancellationToken ct)
        {
            var domainCounts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var st = db.Prepare(@"SELECT lower(substr(sender_email, instr(sender_email,'@')+1)), COUNT(*) FROM messages
                                  WHERE indexed=1 AND sender_email LIKE '%@%' GROUP BY 1");
            while (st.Step()) if (st.GetString(0) != null) domainCounts[st.GetString(0)] = st.GetLong(1);
            st.Reset();
            var looks = DomainUtil.FindLookAlikes(domainCounts);
            var orgCounts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in domainCounts) { var o = DomainUtil.Organisational(kv.Key); if (o != null) orgCounts[o] = orgCounts.GetValueOrDefault(o) + kv.Value; }
            var lookSet = new HashSet<string>(looks.Select(l => l.Domain), StringComparer.OrdinalIgnoreCase);

            // a display name used with several addresses: the rare ones of another organisation are suspicious
            var perName = new Dictionary<string, List<(string email, long count)>>();
            st = db.Prepare(@"SELECT lower(sender_name), lower(sender_email), COUNT(*) FROM messages
                              WHERE indexed=1 AND sender_name IS NOT NULL AND length(sender_name)>=5 AND sender_email LIKE '%@%' AND instr(sender_name,'@')=0 GROUP BY 1,2");
            while (st.Step())
            {
                var name = st.GetString(0);
                if (!perName.TryGetValue(name, out var list)) perName[name] = list = new List<(string, long)>();
                list.Add((st.GetString(1), st.GetLong(2)));
            }
            st.Reset();
            var impersonating = new HashSet<(string, string)>();
            foreach (var kv in perName.Where(kv => kv.Value.Count > 1))
            {
                var dominant = kv.Value.OrderByDescending(x => x.count).First();
                string domOrg = DomainUtil.Organisational(MailHeaders.DomainOf(dominant.email));
                foreach (var other in kv.Value)
                {
                    if (other.email == dominant.email || dominant.count < 5 || dominant.count < other.count * 5) continue;
                    if (DomainUtil.Organisational(MailHeaders.DomainOf(other.email)) != domOrg) impersonating.Add((kv.Key, other.email));
                }
            }

            db.Begin();
            try
            {
                db.Exec("DELETE FROM lookalikes");
                foreach (var l in looks) db.Exec("INSERT INTO lookalikes(domain,resembles,kind,messages) VALUES(?,?,?,?)", l.Domain, l.Resembles, l.Kind, orgCounts.GetValueOrDefault(l.Domain));
                db.Commit();
            }
            catch { db.Rollback(); throw; }

            var orgCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var updates = new List<(long id, int flags)>();
            st = db.Prepare("SELECT id, sender_name, sender_email, spoof_flags FROM messages WHERE indexed=1 AND sender_email LIKE '%@%'");
            while (st.Step())
            {
                var email = st.GetString(2).ToLowerInvariant();
                if (!orgCache.TryGetValue(email, out var org)) orgCache[email] = org = DomainUtil.Organisational(MailHeaders.DomainOf(email));
                int want = (org != null && lookSet.Contains(org) ? (int)SpoofFlags.LookAlikeDomain : 0)
                         | (st.GetString(1) != null && impersonating.Contains((st.GetString(1).ToLowerInvariant(), email)) ? (int)SpoofFlags.NameImpersonation : 0);
                int flags = st.GetInt(3);
                if ((flags & 40) != want) updates.Add((st.GetLong(0), (flags & ~40) | want));
            }
            st.Reset();
            for (int i = 0; i < updates.Count; i += 5000)
            {
                ct.ThrowIfCancellationRequested();
                db.Begin();
                try
                {
                    var upd = db.Prepare("UPDATE messages SET spoof_flags=? WHERE id=?");
                    foreach (var (id, flags) in updates.Skip(i).Take(5000)) upd.Reset().BindAll(flags, id).Run();
                    db.Commit();
                }
                catch { db.Rollback(); throw; }
            }
        }

        // ------------------------------------------------------------------ sensitive data

        private const int MaxSensitiveChars = 3_000_000, MaxSensitiveRows = 200;

        /// <summary>
        /// Scans the indexed text of bodies and attachments (nothing is read again from the sources). The index keeps the type, a masked value
        /// and the position of each find; the full value is read back from the indexed text on demand.
        /// </summary>
        private void ScanSensitive(SqliteDb db, CancellationToken ct)
        {
            var todo = new List<(long id, bool hasAtt, bool attDone)>();
            var st = db.Prepare(@"SELECT id, has_att, att_indexed FROM messages WHERE indexed=1 AND (sens_state=0 OR (sens_state=1 AND has_att=1 AND att_indexed=1)) ORDER BY id");
            while (st.Step()) todo.Add((st.GetLong(0), st.GetInt(1) != 0, st.GetInt(2) != 0));
            st.Reset();
            Interlocked.Exchange(ref _totalCounter, todo.Count);
            Interlocked.Exchange(ref _doneCounter, 0);
            _rateBase = 0;
            _rateWatch.Restart();

            var pending = new List<(long id, int flags, int count, int state, List<(string path, SensitiveHit hit)> hits)>();
            foreach (var (id, hasAtt, attDone) in todo)
            {
                ct.ThrowIfCancellationRequested();
                var hits = new List<(string, SensitiveHit)>();
                int flags = 0;
                void Add(string path, string text)
                {
                    if (string.IsNullOrEmpty(text)) return;
                    if (text.Length > MaxSensitiveChars) text = text.Substring(0, MaxSensitiveChars);
                    foreach (var h in SensitiveScanner.Scan(text)) { if (hits.Count < MaxSensitiveRows) hits.Add((path, h)); flags |= (int)h.Kind; }
                }
                var body = db.Prepare("SELECT body FROM fts WHERE rowid=?");
                body.Bind(1, id);
                if (body.Step()) Add(null, body.GetString(0));
                body.Reset();
                if (hasAtt && attDone)
                {
                    var atts = db.Prepare("SELECT a.idx_path, f.content FROM attachments a JOIN att_fts f ON f.rowid=a.id WHERE a.message_id=? AND a.status='ok'");
                    atts.Bind(1, id);
                    while (atts.Step()) Add(atts.GetString(0), atts.GetString(1));
                    atts.Reset();
                }
                pending.Add((id, flags, hits.Count, !hasAtt || attDone ? 2 : 1, hits));
                if (pending.Count >= 200) { FlushSensitive(db, pending); pending.Clear(); }
            }
            FlushSensitive(db, pending);
        }

        private void FlushSensitive(SqliteDb db, List<(long id, int flags, int count, int state, List<(string path, SensitiveHit hit)> hits)> pending)
        {
            if (pending.Count == 0) return;
            db.Begin();
            try
            {
                var ins = db.Prepare("INSERT INTO sensitive(message_id,att_path,kind,masked,pos,len) VALUES(?,?,?,?,?,?)");
                var upd = db.Prepare("UPDATE messages SET sens_flags=?, sens_count=?, sens_state=? WHERE id=?");
                foreach (var p in pending)
                {
                    db.Exec("DELETE FROM sensitive WHERE message_id=?", p.id);
                    foreach (var (path, h) in p.hits) ins.Reset().BindAll(p.id, path, (int)h.Kind, h.Masked, h.Start, h.Length).Run();
                    upd.Reset().BindAll(p.flags, p.count, p.state, p.id).Run();
                }
                db.Commit();
            }
            catch { db.Rollback(); throw; }
            Interlocked.Add(ref _doneCounter, pending.Count);
            Changed?.Invoke();
        }
    }
}
