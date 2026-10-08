using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PstBrowser.Core.Data;
using PstBrowser.Core.Extraction;
using PstBrowser.Core.Mail;

namespace PstBrowser.Core.Index
{
    /// <summary>Pass 3: text, hash and status of every attachment, stored in <c>attachments</c> and <c>att_fts</c>.</summary>
    public sealed partial class Indexer
    {
        private const int AttachmentBatch = 25;
        private const int MaxEmbeddedDepth = 5;

        private sealed class AttRecord
        {
            public string IdxPath, Name, Ext, Sha256, Status, Error, Text;
            public long Size;
        }

        private async Task AttachmentPass(CancellationToken ct)
        {
            long pending;
            List<SourceInfo> sources;
            using (var db = _ws.Open())
            {
                pending = db.ExecScalarLong("SELECT COUNT(*) FROM messages WHERE indexed=1 AND has_att=1 AND att_indexed=0");
                var ids = new HashSet<long>();
                var st = db.Prepare("SELECT DISTINCT source_id FROM messages WHERE indexed=1 AND has_att=1 AND att_indexed=0");
                while (st.Step()) ids.Add(st.GetLong(0));
                st.Reset();
                sources = _ws.ListSources().Where(s => ids.Contains(s.Id)).ToList();
            }
            if (pending == 0) return;
            Interlocked.Exchange(ref _totalCounter, pending);
            Interlocked.Exchange(ref _doneCounter, 0);
            _rateBase = 0;
            _rateWatch.Restart();
            SetPhase("Extraction des pièces jointes");
            await ForEachParallel(sources, ct, s => ExtractAttachments(s, ct), markSourceError: false);
        }

        private void ExtractAttachments(SourceInfo s, CancellationToken ct)
        {
            if (s.FileMissing) throw new FileNotFoundException("Fichier introuvable : " + s.Path);
            using var db = _ws.Open();
            SetPhase("Extraction des pièces jointes", s.DisplayName);

            var todo = new List<(long id, uint nid, uint folderNid, string file)>();
            var st = db.Prepare(@"SELECT m.id, m.nid, f.nid, m.file_path FROM messages m JOIN folders f ON f.id=m.folder_id
                                  WHERE m.source_id=? AND m.indexed=1 AND m.has_att=1 AND m.att_indexed=0 ORDER BY m.id");
            st.Bind(1, s.Id);
            while (st.Step()) todo.Add((st.GetLong(0), (uint)st.GetLong(1), (uint)st.GetLong(2), st.GetString(3)));
            st.Reset();

            using var client = new ExtractorClient(_opt.Worker);
            PstReader pst = null;
            int sinceOpen = 0;
            var batch = new List<(long id, List<AttRecord> recs)>();
            try
            {
                foreach (var (id, nid, folderNid, file) in todo)
                {
                    ct.ThrowIfCancellationRequested();
                    List<AttRecord> recs = new List<AttRecord>();
                    try
                    {
                        if (s.Kind == SourceKind.Pst)
                        {
                            if (pst == null || sinceOpen >= _opt.ReopenEvery) { pst?.Dispose(); pst = new PstReader(s.Path); sinceOpen = 0; }
                            sinceOpen++;
                            Collect(pst.OpenMessage(folderNid, nid), "", 0, recs, client, ct);
                        }
                        else
                        {
                            using var m = new MsgMailItem(file);
                            Collect(m, "", 0, recs, client, ct);
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        recs.Add(new AttRecord { IdxPath = "", Status = AttachmentStatus.Error, Error = "Lecture du message : " + ex.Message });
                        ReportError($"{s.DisplayName}: pièces jointes du message {id}: {ex.Message}");
                    }
                    batch.Add((id, recs));
                    if (batch.Count >= AttachmentBatch) { FlushAttachments(db, batch); batch.Clear(); }
                }
            }
            finally
            {
                if (batch.Count > 0) { try { FlushAttachments(db, batch); } catch { } }
                pst?.Dispose();
                Changed?.Invoke();
            }
        }

        private void Collect(IMailItem m, string prefix, int depth, List<AttRecord> recs, ExtractorClient client, CancellationToken ct)
        {
            foreach (var a in m.Attachments)
            {
                ct.ThrowIfCancellationRequested();
                string path = prefix + a.Index;
                if (a.IsEmbeddedMessage)
                {
                    if (depth >= MaxEmbeddedDepth) continue;
                    try { Collect(m.OpenEmbeddedMessage(a.Index), path + "/", depth + 1, recs, client, ct); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { recs.Add(new AttRecord { IdxPath = path, Name = a.FileName, Status = AttachmentStatus.Error, Error = "Message joint illisible : " + ex.Message }); }
                    continue;
                }
                recs.Add(ProcessAttachment(m, a, path, client, ct));
            }
        }

        private AttRecord ProcessAttachment(IMailItem m, AttachmentInfo a, string path, ExtractorClient client, CancellationToken ct)
        {
            var rec = new AttRecord { IdxPath = path, Name = a.FileName, Ext = TextExtractor.ExtensionOf(a.FileName), Status = AttachmentStatus.Error };
            try
            {
                using var capture = new CaptureStream(_opt.AttachmentLimits.MaxFileBytes);
                m.SaveAttachment(a.Index, capture);
                rec.Size = capture.Total;
                rec.Sha256 = capture.Sha256Hex();
                if (capture.Overflow) { rec.Status = AttachmentStatus.TooLarge; return rec; }
                var bytes = capture.Bytes;
                if (bytes.Length == 0) { rec.Status = AttachmentStatus.Empty; return rec; }
                if (!TextExtractor.IsSupported(a.FileName, bytes)) { rec.Status = AttachmentStatus.Unsupported; return rec; }
                var r = client.Extract(a.FileName, bytes, _opt.AttachmentLimits.MaxChars, TimeSpan.FromSeconds(Math.Max(1, _opt.AttachmentTimeoutSeconds)), ct);
                rec.Status = r.Status;
                rec.Error = r.Error;
                rec.Text = r.Status == AttachmentStatus.Ok ? r.Text : null;
                if (r.Status == AttachmentStatus.Error) ReportError($"Pièce jointe « {a.FileName} » : {r.Error}");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                rec.Status = AttachmentStatus.Error;
                rec.Error = ex.Message;
            }
            return rec;
        }

        private void FlushAttachments(SqliteDb db, List<(long id, List<AttRecord> recs)> batch)
        {
            if (batch.Count == 0) return;
            db.Begin();
            try
            {
                var ins = db.Prepare("INSERT INTO attachments(message_id,idx_path,name,size,ext,sha256,status,error,text_len) VALUES(?,?,?,?,?,?,?,?,?)");
                var fts = db.Prepare("INSERT INTO att_fts(rowid,name,content) VALUES(?,?,?)");
                foreach (var (id, recs) in batch)
                {
                    // a message interrupted before is processed again from scratch
                    db.Exec("DELETE FROM att_fts WHERE rowid IN (SELECT id FROM attachments WHERE message_id=?)", id);
                    db.Exec("DELETE FROM attachments WHERE message_id=?", id);
                    foreach (var r in recs)
                    {
                        ins.Reset().BindAll(id, r.IdxPath, r.Name, r.Size, r.Ext, r.Sha256, r.Status, r.Error, r.Text?.Length ?? 0);
                        ins.Run();
                        long attId = db.LastInsertRowId;
                        fts.Reset().BindAll(attId, r.Name, r.Text ?? "");
                        fts.Run();
                    }
                    db.Exec("UPDATE messages SET att_indexed=1 WHERE id=?", id);
                }
                db.Commit();
            }
            catch { db.Rollback(); throw; }
            Interlocked.Add(ref _doneCounter, batch.Count);
            Changed?.Invoke();
        }
    }
}
