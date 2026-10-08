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
    /// <summary>
    /// Fields a result list can be sorted on. Keys come from a white list: they are never concatenated into SQL as received.
    /// </summary>
    public static class SortFields
    {
        public const string Relevance = "relevance";

        private static readonly Dictionary<string, (string sql, bool descending)> Map = new Dictionary<string, (string, bool)>(StringComparer.OrdinalIgnoreCase)
        {
            ["date"] = ("m.date", true),
            ["sent"] = ("m.sent_date", true),
            ["sender"] = ("COALESCE(m.sender_name, m.sender_email) COLLATE NOCASE", false),
            ["senderemail"] = ("m.sender_email COLLATE NOCASE", false),
            ["to"] = ("m.to_text COLLATE NOCASE", false),
            ["cc"] = ("m.cc_text COLLATE NOCASE", false),
            ["bcc"] = ("m.bcc_text COLLATE NOCASE", false),
            ["subject"] = ("m.subject COLLATE NOCASE", false),
            ["mailbox"] = ("b.name COLLATE NOCASE", false),
            ["folder"] = ("f.path COLLATE NOCASE", false),
            ["source"] = ("s.display_name COLLATE NOCASE", false),
            ["size"] = ("m.size", true),
            ["attcount"] = ("m.att_count", true),
            ["attnames"] = ("m.att_names COLLATE NOCASE", false),
            ["hasatt"] = ("m.has_att", true),
            ["kind"] = ("m.msg_class COLLATE NOCASE", false),
            ["importance"] = ("m.importance", true),
            ["dup"] = ("(m.dup_of IS NOT NULL)", true),
            ["msgid"] = ("m.message_id COLLATE NOCASE", false),
            ["conversation"] = ("m.conversation_topic COLLATE NOCASE", false),
            ["read"] = ("m.is_read", false),
            ["flag"] = ("m.flag_status", true),
        };

        public static IEnumerable<string> Keys => Map.Keys.Concat(new[] { Relevance });

        public static bool IsValid(string key) => key != null && (Map.ContainsKey(key) || string.Equals(key, Relevance, StringComparison.OrdinalIgnoreCase));

        /// <summary>Natural direction when a column is first clicked: newest / largest first, text A→Z.</summary>
        public static bool DefaultDescending(string key) => key != null && Map.TryGetValue(key, out var v) && v.descending;

        internal static string Sql(string key) => Map.TryGetValue(key ?? "", out var v) ? v.sql : Map["date"].sql;

        /// <summary>Parses "field", "field:asc" or "field:desc" (and the former names DateDesc, DateAsc, SenderAsc, SubjectAsc, SizeDesc).</summary>
        public static bool TryParse(string text, out string key, out bool descending)
        {
            key = "date"; descending = true;
            if (string.IsNullOrWhiteSpace(text)) return false;
            switch (text.Trim().ToLowerInvariant())
            {
                case "datedesc": return true;
                case "dateasc": descending = false; return true;
                case "senderasc": key = "sender"; descending = false; return true;
                case "subjectasc": key = "subject"; descending = false; return true;
                case "sizedesc": key = "size"; return true;
            }
            var parts = text.Trim().Split(':');
            if (!IsValid(parts[0])) return false;
            key = parts[0].ToLowerInvariant();
            descending = DefaultDescending(key);
            if (parts.Length > 1)
            {
                if (parts[1].Equals("asc", StringComparison.OrdinalIgnoreCase)) descending = false;
                else if (parts[1].Equals("desc", StringComparison.OrdinalIgnoreCase)) descending = true;
                else return false;
            }
            return true;
        }
    }

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
        /// <summary>Sort field (a <see cref="SortFields"/> key; unknown keys fall back to the date).</summary>
        public string SortBy { get; set; } = "date";
        public bool SortDescending { get; set; } = true;
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
        public string SourcePath { get; set; }
        public long MailboxId { get; set; }
        public string Cc { get; set; }
        public string Bcc { get; set; }
        public DateTime? SentDate { get; set; }
        public string ConversationTopic { get; set; }
        public string MessageId { get; set; }
        /// <summary>Null while unknown (message not yet read by the indexer).</summary>
        public bool? IsRead { get; set; }
        /// <summary>0 none, 1 complete, 2 flagged.</summary>
        public int FlagStatus { get; set; }

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
            // Family search: every positive clause must be found in the message OR in one of its attachments.
            foreach (var clause in pq.Positive)
                where.Add(FamilyCondition(clause, "IN", args));
            foreach (var clause in pq.Negative)
                where.Add(FamilyCondition(clause, "NOT IN", args));
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

            string whereSql = where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "";

            // Relevance: bm25 of the message-side query; messages found only through their attachments come after.
            string sortBy = SortFields.IsValid(req.SortBy) ? req.SortBy.ToLowerInvariant() : "date";
            string rankJoin = "";
            var joinArgs = new List<object>();
            string order;
            string messageRank = fts ? string.Join(" AND ", pq.Positive.Select(c => c.MessageExpression()).Where(e => e != null)) : "";
            if (sortBy == SortFields.Relevance && messageRank.Length > 0)
            {
                rankJoin = " LEFT JOIN (SELECT rowid AS rid, bm25(fts, 5.0, 3.0, 2.0, 1.0, 2.0) AS rk FROM fts WHERE fts MATCH ?) r ON r.rid=m.id";
                joinArgs.Add(messageRank);
                order = "(r.rk IS NULL), r.rk, m.date DESC, m.id DESC";
            }
            else
            {
                if (sortBy == SortFields.Relevance) { sortBy = "date"; }
                bool desc = req.SortDescending;
                string dir = desc ? "DESC" : "ASC";
                order = sortBy == "date"
                    ? $"m.date {dir}, m.id {dir}"
                    : $"{SortFields.Sql(sortBy)} {dir}, m.date DESC, m.id DESC";
            }
            string sql = $@"SELECT m.id, m.subject, m.sender_name, m.sender_email, m.to_text, m.date, m.size, m.has_att, m.att_count, m.att_names,
                                   b.name, f.path, m.msg_class, m.importance, m.dup_of, m.indexed, m.error, s.display_name, m.mailbox_id,
                                   m.cc_text, m.bcc_text, m.sent_date, m.conversation_topic, m.is_read, m.flag_status, m.message_id, s.path
                            FROM messages m
                            JOIN folders f ON f.id=m.folder_id
                            JOIN sources s ON s.id=m.source_id
                            JOIN mailboxes b ON b.id=m.mailbox_id{rankJoin}
                            {whereSql}
                            ORDER BY {order}
                            LIMIT ? OFFSET ?";
            string countSql = $"SELECT COUNT(*) FROM messages m{whereSql}";

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
                        var all = new List<object>(joinArgs);
                        all.AddRange(args);
                        all.Add(req.Limit); all.Add(req.Offset);
                        st.BindAll(all.ToArray());
                        while (st.Step())
                        {
                            long? date = st.GetLongOrNull(5);
                            long? sent = st.GetLongOrNull(21);
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
                                SourceName = st.GetString(17),
                                MailboxId = st.GetLong(18),
                                Cc = st.GetString(19),
                                Bcc = st.GetString(20),
                                SentDate = sent.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(sent.Value).LocalDateTime : (DateTime?)null,
                                ConversationTopic = st.GetString(22),
                                IsRead = st.IsNull(23) ? (bool?)null : st.GetInt(23) != 0,
                                FlagStatus = st.GetInt(24),
                                MessageId = st.GetString(25),
                                SourcePath = st.GetString(26),
                            });
                        }
                    }
                    if (fts) FillSnippets(res.Rows, pq);
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

        /// <summary>
        /// SQL condition "the message, or one of its attachments, matches the clause" (or the opposite with NOT IN).
        /// Message-only fields (de:, à:, objet:, corps:) look at the message index only, pjtexte: at the attachment contents only.
        /// </summary>
        private static string FamilyCondition(QueryClause clause, string op, List<object> args)
        {
            string msg = clause.MessageExpression(), att = clause.AttachmentExpression();
            var parts = new List<string>();
            if (msg != null) { parts.Add("SELECT rowid FROM fts WHERE fts MATCH ?"); args.Add(msg); }
            if (att != null) { parts.Add("SELECT a.message_id FROM att_fts JOIN attachments a ON a.id=att_fts.rowid WHERE att_fts MATCH ?"); args.Add(att); }
            return $"m.id {op} ({string.Join(" UNION ", parts)})";
        }

        private const string SnippetMarks = "'«', '»', '…', 16";

        /// <summary>
        /// Snippets for the displayed page only: from the body when it matches, otherwise from the text of the attachment
        /// that matches ("📎 name : excerpt"), otherwise the start of the body.
        /// </summary>
        private void FillSnippets(List<MessageRow> rows, ParsedQuery pq)
        {
            if (rows.Count == 0) return;
            var byId = rows.ToDictionary(r => r.Id);
            var terms = pq.Positive.SelectMany(c => c.Alternatives).ToList();
            string ids = string.Join(",", rows.Select(r => r.Id));

            string bodyExpr = string.Join(" OR ", terms.Where(t => t.Field == QueryField.Any || t.Field == QueryField.Body).Select(t => "body:" + t.FtsPhrase).Distinct());
            if (bodyExpr.Length > 0)
            {
                using var st = _db.PrepareOnce($"SELECT rowid, snippet(fts, 3, {SnippetMarks}) FROM fts WHERE fts MATCH ? AND rowid IN ({ids})");
                st.Bind(1, bodyExpr);
                while (st.Step()) byId[st.GetLong(0)].Snippet = CleanSnippet(st.GetString(1));
            }

            var missing = rows.Where(r => string.IsNullOrEmpty(r.Snippet)).Select(r => r.Id).ToList();
            if (missing.Count == 0) return;
            string attExpr = string.Join(" OR ", terms.Where(t => t.Field == QueryField.Any || t.Field == QueryField.AttachmentText).Select(t => "content:" + t.FtsPhrase).Distinct());
            if (attExpr.Length > 0)
            {
                using var st = _db.PrepareOnce($@"SELECT a.message_id, a.name, snippet(att_fts, 1, {SnippetMarks})
                                                  FROM att_fts JOIN attachments a ON a.id=att_fts.rowid
                                                  WHERE att_fts MATCH ? AND a.message_id IN ({string.Join(",", missing)}) ORDER BY a.message_id, a.idx_path");
                st.Bind(1, attExpr);
                while (st.Step())
                {
                    var row = byId[st.GetLong(0)];
                    if (string.IsNullOrEmpty(row.Snippet)) row.Snippet = "📎 " + st.GetString(1) + " : " + CleanSnippet(st.GetString(2));
                }
            }

            missing = rows.Where(r => string.IsNullOrEmpty(r.Snippet)).Select(r => r.Id).ToList();
            string anyExpr = string.Join(" OR ", pq.Positive.Select(c => c.MessageExpression()).Where(e => e != null));
            if (missing.Count > 0 && anyExpr.Length > 0)
            {
                using var st = _db.PrepareOnce($"SELECT rowid, snippet(fts, 3, {SnippetMarks}) FROM fts WHERE fts MATCH ? AND rowid IN ({string.Join(",", missing)})");
                st.Bind(1, anyExpr);
                while (st.Step()) byId[st.GetLong(0)].Snippet = CleanSnippet(st.GetString(1));
            }
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
                HideDuplicates = req.HideDuplicates, SortBy = req.SortBy, SortDescending = req.SortDescending, Limit = pageSize, Offset = 0,
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
            w.WriteLine("Date;Expéditeur;Adresse expéditeur;Destinataires;Objet;Boîte;Dossier;Type;Taille (octets);Pièces jointes;Noms des pièces jointes;Doublon;Id interne;Date d'envoi;Cc;Cci;Message-ID;Conversation;Lu;Suivi;Importance;Fichier source");
            foreach (var r in rows)
            {
                w.WriteLine(string.Join(";", new[]
                {
                    r.Date?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "",
                    Csv(r.SenderName), Csv(r.SenderEmail), Csv(r.To), Csv(r.Subject), Csv(r.Mailbox), Csv(r.FolderPath), Csv(r.Kind),
                    r.Size.ToString(CultureInfo.InvariantCulture), r.AttachmentCount.ToString(CultureInfo.InvariantCulture), Csv(r.AttachmentNames),
                    r.IsDuplicate ? "oui" : "", r.Id.ToString(CultureInfo.InvariantCulture),
                    r.SentDate?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "", Csv(r.Cc), Csv(r.Bcc), Csv(r.MessageId), Csv(r.ConversationTopic),
                    r.IsRead == true ? "lu" : r.IsRead == false ? "non lu" : "", r.FlagStatus == 2 ? "à suivre" : r.FlagStatus == 1 ? "terminé" : "",
                    r.Importance == 2 ? "haute" : r.Importance == 0 ? "basse" : "normale", Csv(r.SourceName),
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
