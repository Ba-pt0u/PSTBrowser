using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using PstBrowser.Core.Analysis;
using PstBrowser.Core.Data;
using PstBrowser.Core.Index;
using PstBrowser.Core.Mail;
using PstBrowser.Core.Text;

namespace PstBrowser.Core.Viewer
{
    /// <summary>Identifies a message: an indexed message, optionally followed by a path of embedded-message attachment indexes.</summary>
    public sealed class MessageRef : IEquatable<MessageRef>
    {
        public long Id { get; }
        public int[] EmbeddedPath { get; }
        public MessageRef(long id, params int[] embeddedPath) { Id = id; EmbeddedPath = embeddedPath ?? Array.Empty<int>(); }
        public MessageRef Child(int attachmentIndex) => new MessageRef(Id, EmbeddedPath.Concat(new[] { attachmentIndex }).ToArray());
        public bool IsEmbedded => EmbeddedPath.Length > 0;
        public string Key => Id + (EmbeddedPath.Length > 0 ? "-" + string.Join("-", EmbeddedPath) : "");
        public static MessageRef Parse(string key)
        {
            var p = key.Split('-');
            return new MessageRef(long.Parse(p[0]), p.Skip(1).Select(int.Parse).ToArray());
        }
        public bool Equals(MessageRef o) => o != null && o.Key == Key;
        public override bool Equals(object obj) => Equals(obj as MessageRef);
        public override int GetHashCode() => Key.GetHashCode();
        public override string ToString() => Key;
    }

    public sealed class AttachmentView
    {
        public int Index { get; set; }
        public string FileName { get; set; }
        public long Size { get; set; }
        public bool IsEmbeddedMessage { get; set; }
        public bool IsInline { get; set; }
        public string SizeText => IsEmbeddedMessage ? "message" : Format.Size(Size);
        /// <summary>Position in the attachment tree of the indexed message: "3", or "2/1" for attachment 1 of embedded message 2.</summary>
        public string IdxPath { get; set; }
        /// <summary>The attachment contains the searched terms.</summary>
        public bool HasHit { get; set; }
        /// <summary>Status of the text extraction (<see cref="Extraction.AttachmentStatus"/>), null when not indexed.</summary>
        public string TextStatus { get; set; }
        public long TextLength { get; set; }
        public string Sha256 { get; set; }
        public bool HasText => TextStatus == Extraction.AttachmentStatus.Ok && TextLength > 0;
    }

    /// <summary>Text extracted from an attachment, as stored in the index.</summary>
    public sealed class AttachmentText
    {
        public string Name { get; set; }
        public string Status { get; set; }
        public string Error { get; set; }
        public string Sha256 { get; set; }
        public long Size { get; set; }
        public string Text { get; set; }
    }

    public sealed class MessageView
    {
        public MessageRef Ref { get; set; }
        public string Subject { get; set; }
        public string From { get; set; }
        public string To { get; set; }
        public string Cc { get; set; }
        public string Bcc { get; set; }
        public DateTime? Date { get; set; }
        public DateTime? SentDate { get; set; }
        public string MessageClass { get; set; }
        public string Kind { get; set; }
        public string InternetMessageId { get; set; }
        public string TransportHeaders { get; set; }
        public string Mailbox { get; set; }
        public string FolderPath { get; set; }
        public string SourceName { get; set; }
        public string SourcePath { get; set; }
        public int Importance { get; set; }
        public List<AttachmentView> Attachments { get; set; } = new List<AttachmentView>();
        /// <summary>Complete HTML document of the body, safe to render with scripts disabled and network blocked.</summary>
        public string BodyHtml { get; set; }
        public string BodyFormat { get; set; }
        /// <summary>Inline images referenced by the body: url token → attachment index.</summary>
        public Dictionary<string, int> InlineImages { get; set; } = new Dictionary<string, int>();
        /// <summary>Thread of the message (null when the case-wide analysis has not run yet) and its size.</summary>
        public long? ThreadId { get; set; }
        public int ThreadCount { get; set; }
        /// <summary><see cref="Analysis.SpoofFlags"/>, <see cref="Analysis.DateFlags"/> and <see cref="Analysis.SensitiveKind"/> bits stored for the message.</summary>
        public int SpoofFlags { get; set; }
        public int DateFlags { get; set; }
        public int SensitiveFlags { get; set; }
        public int SensitiveCount { get; set; }
        /// <summary>"Reply-To différent, …" / "IBAN, Téléphone": the alerts shown above the body.</summary>
        public string AlertText
        {
            get
            {
                var parts = new List<string>();
                var spoof = Search.SearchService.SpoofLabels(SpoofFlags);
                if (spoof.Length > 0) parts.Add("Indices d'usurpation : " + spoof);
                var dates = Search.SearchService.DateLabels(DateFlags);
                if (dates.Length > 0) parts.Add("Dates incohérentes : " + dates);
                if (SensitiveFlags != 0) parts.Add("Données sensibles : " + SensitiveScanner.Labels(SensitiveFlags));
                return string.Join("   ·   ", parts);
            }
        }
    }

    /// <summary>One sensitive value found in a message or an attachment (the index keeps only the masked form).</summary>
    public sealed class SensitiveItem
    {
        public long Id { get; set; }
        public SensitiveKind Kind { get; set; }
        public string KindLabel => SensitiveScanner.Label(Kind);
        public string Masked { get; set; }
        /// <summary>"Corps du message" or "Pièce jointe : name".</summary>
        public string Location { get; set; }
    }

    /// <summary>Everything the "Analyse" window shows about one message.</summary>
    public sealed class AnalysisView
    {
        public MessageAnalysis Live { get; set; }
        /// <summary>Case-wide indications (look-alike domain, display name of a regular correspondent), in addition to <c>Live.Header.Findings</c>.</summary>
        public List<Finding> CaseFindings { get; } = new List<Finding>();
        public List<SensitiveItem> Sensitive { get; } = new List<SensitiveItem>();
        public long? ThreadId { get; set; }
        public int ThreadCount { get; set; }
    }

    public static class Format
    {
        public static string Size(long b)
        {
            if (b <= 0) return "";
            if (b < 1024) return b + " o";
            if (b < 1024 * 1024) return (b / 1024.0).ToString("0.#") + " Ko";
            if (b < 1024L * 1024 * 1024) return (b / 1024.0 / 1024).ToString("0.#") + " Mo";
            return (b / 1024.0 / 1024 / 1024).ToString("0.##") + " Go";
        }
    }

    /// <summary>
    /// Opens indexed messages on demand from their PST/MSG source (read-only), renders them safely,
    /// and extracts attachments. Thread-safe: each PST reader is used under its own lock.
    /// </summary>
    public sealed class MessageService : IDisposable
    {
        public const string VirtualHost = "pstbrowser.invalid";
        private readonly Workspace _ws;
        private readonly Dictionary<long, PstReader> _readers = new Dictionary<long, PstReader>();
        private readonly object _readersLock = new object();

        public MessageService(Workspace ws) { _ws = ws; }

        private sealed class Location
        {
            public long SourceId; public SourceKind Kind; public string SourcePath; public string SourceName;
            public uint FolderNid; public uint Nid; public string FilePath; public string Mailbox; public string FolderPath;
        }

        private Location Locate(long id)
        {
            lock (_ws.DbLock)
            {
                var st = _ws.Db.Prepare(@"SELECT s.id, s.kind, s.path, s.display_name, f.nid, m.nid, m.file_path, b.name, f.path
                                          FROM messages m JOIN sources s ON s.id=m.source_id JOIN folders f ON f.id=m.folder_id JOIN mailboxes b ON b.id=m.mailbox_id
                                          WHERE m.id=?");
                st.Bind(1, id);
                try
                {
                    if (!st.Step()) throw new KeyNotFoundException("Message introuvable dans l'index : " + id);
                    return new Location
                    {
                        SourceId = st.GetLong(0), Kind = (SourceKind)st.GetInt(1), SourcePath = st.GetString(2), SourceName = st.GetString(3),
                        FolderNid = (uint)st.GetLong(4), Nid = (uint)st.GetLong(5), FilePath = st.GetString(6), Mailbox = st.GetString(7), FolderPath = st.GetString(8),
                    };
                }
                finally { st.Reset(); }
            }
        }

        private PstReader Reader(Location loc)
        {
            lock (_readersLock)
            {
                if (!_readers.TryGetValue(loc.SourceId, out var r))
                {
                    if (!File.Exists(loc.SourcePath)) throw new FileNotFoundException("Fichier source introuvable : " + loc.SourcePath);
                    r = new PstReader(loc.SourcePath);
                    _readers[loc.SourceId] = r;
                }
                return r;
            }
        }

        /// <summary>Runs <paramref name="action"/> on the opened message (under the reader's lock).</summary>
        public T With<T>(MessageRef mref, Func<IMailItem, Location2, T> action)
        {
            var loc = Locate(mref.Id);
            var info = new Location2 { Mailbox = loc.Mailbox, FolderPath = loc.FolderPath, SourceName = loc.SourceName, SourcePath = loc.FilePath ?? loc.SourcePath };
            if (loc.Kind == SourceKind.Pst)
            {
                var r = Reader(loc);
                lock (r.Lock)
                {
                    IMailItem m = r.OpenMessage(loc.FolderNid, loc.Nid);
                    foreach (var idx in mref.EmbeddedPath) m = m.OpenEmbeddedMessage(idx);
                    return action(m, info);
                }
            }
            else
            {
                if (!File.Exists(loc.FilePath)) throw new FileNotFoundException("Fichier MSG introuvable : " + loc.FilePath);
                using var msg = new MsgMailItem(loc.FilePath);
                IMailItem m = msg;
                foreach (var idx in mref.EmbeddedPath) m = m.OpenEmbeddedMessage(idx);
                return action(m, info);
            }
        }

        public sealed class Location2 { public string Mailbox, FolderPath, SourceName, SourcePath; }

        /// <param name="query">The search text, used to flag the attachments that contain the searched terms.</param>
        public MessageView GetView(MessageRef mref, IList<string> highlightTerms = null, string query = null)
        {
            return With(mref, (m, loc) =>
            {
                var v = new MessageView
                {
                    Ref = mref,
                    Subject = m.Subject,
                    From = string.IsNullOrEmpty(m.SenderEmail) ? m.SenderName : (string.IsNullOrEmpty(m.SenderName) ? m.SenderEmail : $"{m.SenderName} <{m.SenderEmail}>"),
                    Date = m.Date?.ToLocalTime(),
                    SentDate = m.SentDate?.ToLocalTime(),
                    MessageClass = m.MessageClass,
                    Kind = Search.MessageRow.ItemKind(m.MessageClass),
                    InternetMessageId = m.InternetMessageId,
                    TransportHeaders = m.TransportHeaders,
                    Importance = m.Importance,
                    Mailbox = loc.Mailbox,
                    FolderPath = loc.FolderPath,
                    SourceName = loc.SourceName,
                    SourcePath = loc.SourcePath,
                };
                var rec = m.Recipients;
                v.To = string.Join("; ", rec.Where(r => r.Kind == RecipientKind.To));
                v.Cc = string.Join("; ", rec.Where(r => r.Kind == RecipientKind.Cc));
                v.Bcc = string.Join("; ", rec.Where(r => r.Kind == RecipientKind.Bcc));
                var atts = m.Attachments;
                v.Attachments = atts.Where(Indexer.IsVisibleAttachment)
                                    .Select(a => new AttachmentView { Index = a.Index, FileName = a.FileName, Size = a.Size, IsEmbeddedMessage = a.IsEmbeddedMessage, IsInline = a.IsInline })
                                    .ToList();
                MarkIndexedAttachments(v, mref, query, highlightTerms);
                LoadStoredAnalysis(v, mref.Id);
                v.BodyHtml = BodyRenderer.Render(m, mref, highlightTerms, v.InlineImages, out var fmt);
                v.BodyFormat = fmt;
                return v;
            });
        }

        private void LoadStoredAnalysis(MessageView v, long messageId)
        {
            try
            {
                lock (_ws.DbLock)
                {
                    var st = _ws.Db.Prepare("SELECT thread_id, thread_count, spoof_flags, date_flags, sens_flags, sens_count FROM messages WHERE id=?");
                    st.Bind(1, messageId);
                    try
                    {
                        if (!st.Step()) return;
                        v.ThreadId = st.GetLongOrNull(0); v.ThreadCount = st.GetInt(1);
                        v.SpoofFlags = st.GetInt(2); v.DateFlags = st.GetInt(3); v.SensitiveFlags = st.GetInt(4); v.SensitiveCount = st.GetInt(5);
                    }
                    finally { st.Reset(); }
                }
            }
            catch (Exception) { /* index without analysis columns */ }
        }

        /// <summary>Headers, dates, case-wide indications and sensitive data of a message, for the "Analyse" window.</summary>
        public AnalysisView GetAnalysis(MessageRef mref)
        {
            var av = new AnalysisView();
            string senderName = null, senderEmail = null;
            av.Live = With(mref, (m, _) => { senderName = m.SenderName; senderEmail = m.SenderEmail; return MessageAnalyzer.Analyze(m); });
            lock (_ws.DbLock)
            {
                var db = _ws.Db;
                var st = db.Prepare("SELECT thread_id, thread_count, spoof_flags FROM messages WHERE id=?");
                st.Bind(1, mref.Id);
                int flags = 0;
                try { if (st.Step()) { av.ThreadId = st.GetLongOrNull(0); av.ThreadCount = st.GetInt(1); flags = st.GetInt(2); } } finally { st.Reset(); }

                if ((flags & (int)SpoofFlags.LookAlikeDomain) != 0 && senderEmail != null)
                {
                    var org = DomainUtil.Organisational(MailHeaders.DomainOf(senderEmail));
                    st = db.Prepare("SELECT resembles, kind, messages FROM lookalikes WHERE domain=?");
                    st.Bind(1, org);
                    try
                    {
                        if (st.Step())
                        {
                            string how = st.GetString(1) switch
                            {
                                "homoglyph" => "caractères d'apparence identique (par exemple « rn » pour « m », « 0 » pour « o »)",
                                "typo" => "une lettre de différence",
                                "suffix" => "même nom avec une autre extension",
                                "punycode" => "nom international (punycode) imitant un autre",
                                _ => "ressemblance",
                            };
                            av.CaseFindings.Add(new Finding { Code = "look-alike", Text = $"Le domaine de l'expéditeur ({org}, {st.GetLong(2)} message(s) dans le dossier d'affaire) ressemble au domaine bien plus fréquent {st.GetString(0)} : {how}." });
                        }
                    }
                    finally { st.Reset(); }
                }
                if ((flags & (int)SpoofFlags.NameImpersonation) != 0 && !string.IsNullOrEmpty(senderName))
                {
                    st = db.Prepare("SELECT lower(sender_email), COUNT(*) FROM messages WHERE lower(sender_name)=? GROUP BY 1 ORDER BY 2 DESC LIMIT 1");
                    st.Bind(1, senderName.ToLowerInvariant());
                    try
                    {
                        if (st.Step())
                            av.CaseFindings.Add(new Finding { Code = "name-reuse", Text = $"Le nom « {senderName} » est utilisé ici avec l'adresse {senderEmail}, alors que dans le reste du dossier d'affaire il est associé à {st.GetString(0)} ({st.GetLong(1)} message(s)), d'un autre organisme." });
                    }
                    finally { st.Reset(); }
                }

                st = db.Prepare("SELECT id, kind, masked, att_path FROM sensitive WHERE message_id=? ORDER BY att_path IS NOT NULL, att_path, pos");
                st.Bind(1, mref.Id);
                var rows = new List<(long id, int kind, string masked, string path)>();
                try { while (st.Step()) rows.Add((st.GetLong(0), st.GetInt(1), st.GetString(2), st.GetString(3))); } finally { st.Reset(); }
                var names = new Dictionary<string, string>();
                var ast = db.Prepare("SELECT idx_path, name FROM attachments WHERE message_id=?");
                ast.Bind(1, mref.Id);
                try { while (ast.Step()) names[ast.GetString(0) ?? ""] = ast.GetString(1); } finally { ast.Reset(); }
                foreach (var r in rows)
                    av.Sensitive.Add(new SensitiveItem
                    {
                        Id = r.id, Kind = (SensitiveKind)r.kind, Masked = r.masked,
                        Location = r.path == null ? "Corps du message" : "Pièce jointe : " + (names.TryGetValue(r.path, out var n) ? n : r.path),
                    });
            }
            return av;
        }

        /// <summary>The complete value behind a masked detection, read back from the indexed text (null when it is no longer there).</summary>
        public string RevealSensitive(long sensitiveId)
        {
            lock (_ws.DbLock)
            {
                var db = _ws.Db;
                var st = db.Prepare("SELECT message_id, att_path, pos, len FROM sensitive WHERE id=?");
                st.Bind(1, sensitiveId);
                long messageId; string path; int pos, len;
                try
                {
                    if (!st.Step()) return null;
                    messageId = st.GetLong(0); path = st.GetString(1); pos = st.GetInt(2); len = st.GetInt(3);
                }
                finally { st.Reset(); }
                string text;
                if (path == null) text = db.ExecScalarString("SELECT body FROM fts WHERE rowid=?", messageId);
                else
                {
                    long attId = db.ExecScalarLong("SELECT id FROM attachments WHERE message_id=? AND idx_path=?", messageId, path);
                    text = attId == 0 ? null : db.ExecScalarString("SELECT content FROM att_fts WHERE rowid=?", attId);
                }
                return text != null && pos >= 0 && pos + len <= text.Length ? text.Substring(pos, len) : null;
            }
        }

        /// <summary>Adds the extraction status, hash and search hits of pass 3 to the attachments of a view.</summary>
        private void MarkIndexedAttachments(MessageView v, MessageRef mref, string query, IList<string> terms)
        {
            foreach (var a in v.Attachments) a.IdxPath = string.Join("/", mref.EmbeddedPath.Concat(new[] { a.Index }));
            if (v.Attachments.Count == 0) return;
            try
            {
                lock (_ws.DbLock)
                {
                    var db = _ws.Db;
                    var byPath = v.Attachments.ToDictionary(a => a.IdxPath);
                    var st = db.Prepare("SELECT idx_path, status, text_len, sha256 FROM attachments WHERE message_id=?");
                    st.Bind(1, mref.Id);
                    while (st.Step())
                        if (byPath.TryGetValue(st.GetString(0) ?? "", out var a)) { a.TextStatus = st.GetString(1); a.TextLength = st.GetLong(2); a.Sha256 = st.GetString(3); }
                    st.Reset();

                    string expr = AttachmentHitExpression(query, terms);
                    if (expr == null) return;
                    st = db.Prepare("SELECT a.idx_path FROM attachments a JOIN att_fts ON att_fts.rowid=a.id WHERE a.message_id=? AND att_fts MATCH ?");
                    st.Bind(1, mref.Id); st.Bind(2, expr);
                    try
                    {
                        while (st.Step())
                        {
                            var hit = st.GetString(0) ?? "";
                            if (byPath.TryGetValue(hit, out var a)) a.HasHit = true;
                            // a message attached to this one is flagged when one of its own attachments matches
                            foreach (var emb in v.Attachments.Where(x => x.IsEmbeddedMessage && hit.StartsWith(x.IdxPath + "/", StringComparison.Ordinal))) emb.HasHit = true;
                        }
                    }
                    finally { st.Reset(); }
                }
            }
            catch (Exception) { /* index without attachment tables or invalid expression: no marks */ }
        }

        private static string AttachmentHitExpression(string query, IList<string> terms)
        {
            if (!string.IsNullOrWhiteSpace(query))
            {
                var parts = Search.QueryParser.Parse(query).Positive.Select(c => c.AttachmentExpression()).Where(e => e != null).ToList();
                return parts.Count == 0 ? null : string.Join(" OR ", parts);
            }
            if (terms == null || terms.Count == 0) return null;
            var phrases = terms.Select(t => (t.TrimEnd('*').Replace("\"", " ").Trim(), t.EndsWith("*"))).Where(t => t.Item1.Length > 0 && t.Item1.Any(char.IsLetterOrDigit))
                               .Select(t => "\"" + t.Item1 + "\"" + (t.Item2 ? "*" : "")).ToList();
            return phrases.Count == 0 ? null : string.Join(" OR ", phrases);
        }

        /// <summary>The text extracted from one attachment (null when the attachment is not in the index).</summary>
        public AttachmentText GetAttachmentText(long messageId, string idxPath)
        {
            lock (_ws.DbLock)
            {
                var db = _ws.Db;
                var st = db.Prepare("SELECT id, name, status, error, sha256, size FROM attachments WHERE message_id=? AND idx_path=?");
                st.Bind(1, messageId); st.Bind(2, idxPath);
                long id;
                AttachmentText r;
                try
                {
                    if (!st.Step()) return null;
                    id = st.GetLong(0);
                    r = new AttachmentText { Name = st.GetString(1), Status = st.GetString(2), Error = st.GetString(3), Sha256 = st.GetString(4), Size = st.GetLong(5) };
                }
                finally { st.Reset(); }
                r.Text = db.ExecScalarString("SELECT content FROM att_fts WHERE rowid=?", id) ?? "";
                return r;
            }
        }

        public void SaveAttachment(MessageRef mref, int index, string targetPath)
        {
            With(mref, (m, _) =>
            {
                var info = m.Attachments[index];
                if (info.IsEmbeddedMessage) throw new InvalidOperationException("Ce message joint ne peut être enregistré que dans PstBrowser (ouvrez-le).");
                using var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
                m.SaveAttachment(index, fs);
                return 0;
            });
        }

        /// <summary>Returns attachment bytes (used for inline images); null if larger than maxBytes.</summary>
        public byte[] GetAttachmentBytes(MessageRef mref, int index, long maxBytes = 25 * 1024 * 1024)
        {
            return With(mref, (m, _) =>
            {
                var atts = m.Attachments;
                if (index < 0 || index >= atts.Count || atts[index].IsEmbeddedMessage) return null;
                if (atts[index].Size > maxBytes) return null;
                using var ms = new MemoryStream();
                m.SaveAttachment(index, ms);
                return ms.Length > maxBytes ? null : ms.ToArray();
            });
        }

        public string GetAttachmentFileName(MessageRef mref, int index) => With(mref, (m, _) => m.Attachments[index].FileName);

        /// <summary>Saves the message as an .eml file (RFC 5322 with MIME parts) — portable and openable by any mail client.</summary>
        public void ExportEml(MessageRef mref, string targetPath)
        {
            With(mref, (m, _) =>
            {
                using var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
                EmlWriter.Write(m, fs);
                return 0;
            });
        }

        public void Dispose()
        {
            lock (_readersLock)
            {
                foreach (var r in _readers.Values) r.Dispose();
                _readers.Clear();
            }
        }
    }
}
