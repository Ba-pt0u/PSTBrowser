using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using PstBrowser.Core.Data;
using PstBrowser.Core.Index;

namespace PstBrowser.Core.Search
{
    public enum SortOrder { DateDesc, DateAsc, Relevance, SenderAsc, SubjectAsc, SizeDesc }

    public sealed class SearchRequest
    {
        public string Query { get; set; }
        /// <summary>Restrict to one mailbox (null = all mailboxes of the workspace).</summary>
        public long? MailboxId { get; set; }
        /// <summary>Restrict to a folder path of the mailbox (merged across split PST parts).</summary>
        public string FolderPath { get; set; }
        public bool IncludeSubfolders { get; set; } = true;
        public DateTime? DateFrom { get; set; }
        /// <summary>Inclusive day.</summary>
        public DateTime? DateTo { get; set; }
        public bool OnlyWithAttachments { get; set; }
        public bool HideDuplicates { get; set; }
        public SortOrder Sort { get; set; } = SortOrder.DateDesc;
        public int Limit { get; set; } = 1000;
        public int Offset { get; set; }
    }

    public sealed class MessageRow
    {
        public long Id { get; set; }
        public string Subject { get; set; }
        public string SenderName { get; set; }
        public string SenderEmail { get; set; }
        public string To { get; set; }
        public DateTime? Date { get; set; }
        public long Size { get; set; }
        public bool HasAttachments { get; set; }
        public int AttachmentCount { get; set; }
        public string AttachmentNames { get; set; }
        public string Mailbox { get; set; }
        public string FolderPath { get; set; }
        public string MessageClass { get; set; }
        public int Importance { get; set; }
        public bool IsDuplicate { get; set; }
        public bool Indexed { get; set; }
        public string Error { get; set; }
        public string Snippet { get; set; }
        public string SourceName { get; set; }
        public long MailboxId { get; set; }

        public string From => string.IsNullOrEmpty(SenderName) ? SenderEmail : SenderName;
        public string Kind => ItemKind(MessageClass);

        public static string ItemKind(string cls)
        {
            if (string.IsNullOrEmpty(cls)) return "Message";
            cls = cls.ToUpperInvariant();
            if (cls.StartsWith("IPM.APPOINTMENT") || cls.StartsWith("IPM.SCHEDULE.MEETING")) return cls.StartsWith("IPM.SCHEDULE") ? "Réunion" : "Rendez-vous";
            if (cls.StartsWith("IPM.CONTACT") || cls.StartsWith("IPM.DISTLIST")) return "Contact";
            if (cls.StartsWith("IPM.TASK")) return "Tâche";
            if (cls.StartsWith("IPM.STICKYNOTE")) return "Note";
            if (cls.StartsWith("IPM.ACTIVITY")) return "Journal";
            if (cls.StartsWith("REPORT.")) return "Accusé";
            if (cls.Contains("SMIME")) return "Chiffré";
            return "Message";
        }
    }

    public sealed class SearchResult
    {
        public long Total { get; set; }
        public List<MessageRow> Rows { get; set; } = new List<MessageRow>();
        public List<string> HighlightTerms { get; set; } = new List<string>();
        public string Error { get; set; }
        public long ElapsedMs { get; set; }
        public bool HasMore { get; set; }
    }

    public sealed class SearchService : IDisposable
    {
        public void Dispose()
        {
            lock (_lock) _db.Dispose();
        }

        private readonly Workspace _ws;
        private readonly SqliteDb _db;
        private readonly object _lock = new object();

        public SearchService(Workspace ws)
        {
            _ws = ws;
            _db = ws.Open(); // dedicated connection so that searches never wait for the UI connection
        }

        /// <summary>Aborts a running search (called from another thread).</summary>
        public void Cancel() => _db.Interrupt();

        public SearchResult Search(SearchRequest req)
        {
            var sw = Stopwatch.StartNew();
            var res = new SearchResult();
            var pq = QueryParser.Parse(req.Query);
            res.HighlightTerms = pq.HighlightTerms;
            bool fts = !pq.IsEmpty;
            if (!fts && !string.IsNullOrWhiteSpace(req.Query) && pq.After == null && pq.Before == null)
            {
                res.Error = "Requête vide ou uniquement négative : ajoutez au moins un mot à rechercher.";
                return res;
            }

            var where = new List<string>();
            var args = new List<object>();
            if (fts) { where.Add("fts MATCH ?"); args.Add(pq.Fts); }
            if (req.MailboxId.HasValue)
            {
                if (!string.IsNullOrEmpty(req.FolderPath))
                {
                    if (req.IncludeSubfolders)
                    {
                        where.Add("m.folder_id IN (SELECT f.id FROM folders f JOIN sources s ON s.id=f.source_id WHERE s.mailbox_id=? AND (f.path=? OR substr(f.path,1,?)=?))");
                        args.Add(req.MailboxId.Value); args.Add(req.FolderPath); args.Add(req.FolderPath.Length + 1); args.Add(req.FolderPath + "/");
                    }
                    else
                    {
                        where.Add("m.folder_id IN (SELECT f.id FROM folders f JOIN sources s ON s.id=f.source_id WHERE s.mailbox_id=? AND f.path=?)");
                        args.Add(req.MailboxId.Value); args.Add(req.FolderPath);
                    }
                }
                else { where.Add("m.mailbox_id=?"); args.Add(req.MailboxId.Value); }
            }
            var from = req.DateFrom ?? pq.After;
            var to = req.DateTo ?? pq.Before;
            if (from.HasValue) { where.Add("m.date>=?"); args.Add(SqliteStmt.ToUnixMs(from.Value.Date.ToUniversalTime())); }
            if (to.HasValue) { where.Add("m.date<?"); args.Add(SqliteStmt.ToUnixMs(to.Value.Date.AddDays(1).ToUniversalTime())); }
            if (req.OnlyWithAttachments) where.Add("m.has_att=1");
            if (req.HideDuplicates) where.Add("m.dup_of IS NULL");

            string fromSql = fts ? "fts JOIN messages m ON m.id=fts.rowid" : "messages m";
            string whereSql = where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "";
            string order = req.Sort switch
            {
                SortOrder.DateAsc => "m.date ASC, m.id ASC",
                SortOrder.Relevance when fts => "bm25(fts, 5.0, 3.0, 2.0, 1.0, 2.0), m.date DESC",
                SortOrder.SenderAsc => "COALESCE(m.sender_name, m.sender_email) COLLATE NOCASE ASC, m.date DESC",
                SortOrder.SubjectAsc => "m.subject COLLATE NOCASE ASC, m.date DESC",
                SortOrder.SizeDesc => "m.size DESC",
                _ => "m.date DESC, m.id DESC",
            };
            string snippet = fts ? "snippet(fts, 3, '«', '»', '…', 16)" : "NULL";
            string sql = $@"SELECT m.id, m.subject, m.sender_name, m.sender_email, m.to_text, m.date, m.size, m.has_att, m.att_count, m.att_names,
                                   b.name, f.path, m.msg_class, m.importance, m.dup_of, m.indexed, m.error, {snippet}, s.display_name, m.mailbox_id
                            FROM {fromSql}
                            JOIN folders f ON f.id=m.folder_id
                            JOIN sources s ON s.id=m.source_id
                            JOIN mailboxes b ON b.id=m.mailbox_id
                            {whereSql}
                            ORDER BY {order}
                            LIMIT ? OFFSET ?";
            string countSql = $"SELECT COUNT(*) FROM {fromSql}{whereSql}";

            lock (_lock)
            {
                try
                {
                    using (var st = _db.PrepareOnce(countSql))
                    {
                        st.BindAll(args.ToArray());
                        res.Total = st.Step() ? st.GetLong(0) : 0;
                    }
                    using (var st = _db.PrepareOnce(sql))
                    {
                        var all = new List<object>(args) { req.Limit, req.Offset };
                        st.BindAll(all.ToArray());
                        while (st.Step())
                        {
                            long? date = st.GetLongOrNull(5);
                            res.Rows.Add(new MessageRow
                            {
                                Id = st.GetLong(0),
                                Subject = st.GetString(1),
                                SenderName = st.GetString(2),
                                SenderEmail = st.GetString(3),
                                To = st.GetString(4),
                                Date = date.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(date.Value).LocalDateTime : (DateTime?)null,
                                Size = st.GetLong(6),
                                HasAttachments = st.GetInt(7) != 0,
                                AttachmentCount = st.GetInt(8),
                                AttachmentNames = st.GetString(9),
                                Mailbox = st.GetString(10),
                                FolderPath = st.GetString(11),
                                MessageClass = st.GetString(12),
                                Importance = st.GetInt(13),
                                IsDuplicate = !st.IsNull(14),
                                Indexed = st.GetInt(15) != 0,
                                Error = st.GetString(16),
                                Snippet = CleanSnippet(st.GetString(17)),
                                SourceName = st.GetString(18),
                                MailboxId = st.GetLong(19),
                            });
                        }
                    }
                }
                catch (SqliteException ex)
                {
                    res.Error = ex.Code == 9 ? "Recherche interrompue." : "Requête invalide : " + ex.Message.Split(" — ")[0];
                }
            }
            res.HasMore = req.Offset + res.Rows.Count < res.Total;
            res.ElapsedMs = sw.ElapsedMilliseconds;
            return res;
        }

        private static string CleanSnippet(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var sb = new StringBuilder(s.Length);
            bool space = false;
            foreach (var c in s)
            {
                if (char.IsWhiteSpace(c)) { if (!space) sb.Append(' '); space = true; }
                else { sb.Append(c); space = false; }
            }
            return sb.ToString().Trim();
        }

        /// <summary>Enumerates every result of a request, page by page (for exports).</summary>
        public IEnumerable<MessageRow> SearchAll(SearchRequest req, int pageSize = 5000)
        {
            var copy = new SearchRequest
            {
                Query = req.Query, MailboxId = req.MailboxId, FolderPath = req.FolderPath, IncludeSubfolders = req.IncludeSubfolders,
                DateFrom = req.DateFrom, DateTo = req.DateTo, OnlyWithAttachments = req.OnlyWithAttachments,
                HideDuplicates = req.HideDuplicates, Sort = req.Sort, Limit = pageSize, Offset = 0,
            };
            while (true)
            {
                var page = Search(copy);
                if (page.Error != null) throw new InvalidOperationException(page.Error);
                foreach (var r in page.Rows) yield return r;
                if (!page.HasMore || page.Rows.Count == 0) yield break;
                copy.Offset += page.Rows.Count;
            }
        }

        /// <summary>Writes rows to a CSV file (UTF-8 with BOM, ';' separator: opens directly in a French Excel).</summary>
        public static int ExportCsv(IEnumerable<MessageRow> rows, string path)
        {
            int count = 0;
            using var w = new StreamWriter(path, false, new UTF8Encoding(true));
            w.WriteLine("Date;Expéditeur;Adresse expéditeur;Destinataires;Objet;Boîte;Dossier;Type;Taille (octets);Pièces jointes;Noms des pièces jointes;Doublon;Id interne");
            foreach (var r in rows)
            {
                w.WriteLine(string.Join(";", new[]
                {
                    r.Date?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "",
                    Csv(r.SenderName), Csv(r.SenderEmail), Csv(r.To), Csv(r.Subject), Csv(r.Mailbox), Csv(r.FolderPath), Csv(r.Kind),
                    r.Size.ToString(CultureInfo.InvariantCulture), r.AttachmentCount.ToString(CultureInfo.InvariantCulture), Csv(r.AttachmentNames),
                    r.IsDuplicate ? "oui" : "", r.Id.ToString(CultureInfo.InvariantCulture),
                }));
                count++;
            }
            return count;
        }

        private static string Csv(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ");
            // Neutralise spreadsheet formula injection
            if ("=+-@\t".IndexOf(s[0]) >= 0) s = "'" + s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }
    }
}
