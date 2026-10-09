using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PstBrowser.Core.Search
{
    /// <summary>Part of a message a search term is restricted to.</summary>
    public enum QueryField
    {
        /// <summary>No field: message (subject, people, body, attachment names) and attachments (names, contents).</summary>
        Any,
        From,
        Recipients,
        Subject,
        Body,
        /// <summary>Names of the attachments (message side and attachment side).</summary>
        AttachmentName,
        /// <summary>Extracted contents of the attachments only (<c>pjtexte:</c>).</summary>
        AttachmentText,
    }

    /// <summary>One search term: a word or a phrase, optionally a prefix, optionally restricted to a field.</summary>
    public sealed class QueryTerm
    {
        public string Text { get; set; }
        public bool Phrase { get; set; }
        public bool Prefix { get; set; }
        public QueryField Field { get; set; }

        /// <summary>The quoted FTS5 phrase (without any column filter).</summary>
        public string FtsPhrase => "\"" + Text + "\"" + (Prefix ? "*" : "");

        /// <summary>Expression for the message full-text index, or null when the term does not apply to messages.</summary>
        public string MessageExpression()
        {
            switch (Field)
            {
                case QueryField.Any: return FtsPhrase;
                case QueryField.From: return "sender:" + FtsPhrase;
                case QueryField.Recipients: return "recipients:" + FtsPhrase;
                case QueryField.Subject: return "subject:" + FtsPhrase;
                case QueryField.Body: return "body:" + FtsPhrase;
                case QueryField.AttachmentName: return "attachments:" + FtsPhrase;
                default: return null;
            }
        }

        /// <summary>Expression for the attachment full-text index (name, content), or null when the term only concerns the message.</summary>
        public string AttachmentExpression()
        {
            switch (Field)
            {
                case QueryField.Any: return FtsPhrase;
                case QueryField.AttachmentName: return "name:" + FtsPhrase;
                case QueryField.AttachmentText: return "content:" + FtsPhrase;
                default: return null;
            }
        }
    }

    /// <summary>
    /// A condition every result must satisfy: one of the alternatives (<c>a OR b</c>) matches.
    /// A negated clause (<c>-word</c>) excludes the messages it matches.
    /// </summary>
    public sealed class QueryClause
    {
        public List<QueryTerm> Alternatives { get; } = new List<QueryTerm>();
        public bool Negated { get; set; }

        private static string Join(IEnumerable<string> parts)
        {
            var list = parts.Where(p => p != null).ToList();
            if (list.Count == 0) return null;
            return list.Count == 1 ? list[0] : "(" + string.Join(" OR ", list) + ")";
        }

        /// <summary>FTS5 expression on the message index (alternatives that do not apply to messages are left out), or null.</summary>
        public string MessageExpression() => Join(Alternatives.Select(a => a.MessageExpression()));

        /// <summary>FTS5 expression on the attachment index, or null.</summary>
        public string AttachmentExpression() => Join(Alternatives.Select(a => a.AttachmentExpression()));
    }

    /// <summary>
    /// Structured form of the user's search syntax.
    ///   mots simples            → every word is required (AND), accents and case ignored
    ///   "expression exacte"     → phrase
    ///   mot*                    → prefix
    ///   a OR b  (ou a OU b)     → either
    ///   -mot                    → exclusion
    ///   de:/from:  à:/to:  objet:/subject:  corps:/body:      → restricts the term to that part of the message
    ///   pj:/attachment:                                       → attachment names
    ///   pjtexte:/attachmenttext:                              → contents of the attachments only
    ///   avant:/before:AAAA-MM-JJ   apres:/after:AAAA-MM-JJ    → date filters
    /// Each positive clause must be found in the message OR in one of its attachments (family search).
    /// </summary>
    /// <summary>A condition on the analysis of a message rather than on its text: <c>indice:</c>, <c>spf:</c>, <c>dkim:</c>, <c>dmarc:</c>, <c>fil:</c>.</summary>
    public sealed class QueryFilter
    {
        /// <summary>indice, spf, dkim, dmarc or fil.</summary>
        public string Field { get; set; }
        public string Value { get; set; }
        public bool Negated { get; set; }

        /// <summary>Values of <c>indice:</c> and the bits they test (spoof_flags, date_flags, sens_flags).</summary>
        internal static readonly Dictionary<string, (string column, int mask)> Indices = new Dictionary<string, (string, int)>(StringComparer.OrdinalIgnoreCase)
        {
            ["usurpation"] = ("spoof_flags", 63), ["replyto"] = ("spoof_flags", 1), ["returnpath"] = ("spoof_flags", 2), ["nomtrompeur"] = ("spoof_flags", 4 | 32),
            ["domaine"] = ("spoof_flags", 8), ["auth"] = ("spoof_flags", 16),
            ["dates"] = ("date_flags", 127),
            ["sensible"] = ("sens_flags", 15), ["iban"] = ("sens_flags", 1), ["carte"] = ("sens_flags", 2), ["secu"] = ("sens_flags", 4), ["tel"] = ("sens_flags", 8),
        };

        internal static readonly HashSet<string> AuthValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "pass", "fail", "softfail", "neutral", "none", "temperror", "permerror", "absent" };
    }

    public sealed class ParsedQuery
    {
        /// <summary>Conditions on the analysis of messages (indices, authentication, thread).</summary>
        public List<QueryFilter> Filters { get; } = new List<QueryFilter>();
        /// <summary>Problems found in the query (unknown value of a filter…).</summary>
        public List<string> Errors { get; } = new List<string>();
        /// <summary>Clauses that must all be satisfied.</summary>
        public List<QueryClause> Positive { get; } = new List<QueryClause>();
        /// <summary>Exclusions; each one removes the messages (and families) it matches.</summary>
        public List<QueryClause> Negative { get; } = new List<QueryClause>();
        public List<string> HighlightTerms { get; } = new List<string>();
        public DateTime? After { get; set; }
        public DateTime? Before { get; set; }
        /// <summary>No positive term: FTS5 cannot evaluate a purely negative query.</summary>
        public bool IsEmpty => Positive.Count == 0;
    }

    public static class QueryParser
    {
        private static readonly Dictionary<string, QueryField> Fields = new Dictionary<string, QueryField>(StringComparer.OrdinalIgnoreCase)
        {
            ["from"] = QueryField.From, ["de"] = QueryField.From, ["expediteur"] = QueryField.From, ["expéditeur"] = QueryField.From,
            ["to"] = QueryField.Recipients, ["a"] = QueryField.Recipients, ["à"] = QueryField.Recipients, ["cc"] = QueryField.Recipients,
            ["dest"] = QueryField.Recipients, ["destinataire"] = QueryField.Recipients,
            ["subject"] = QueryField.Subject, ["objet"] = QueryField.Subject, ["sujet"] = QueryField.Subject,
            ["body"] = QueryField.Body, ["corps"] = QueryField.Body, ["texte"] = QueryField.Body,
            ["attachment"] = QueryField.AttachmentName, ["attach"] = QueryField.AttachmentName, ["pj"] = QueryField.AttachmentName, ["fichier"] = QueryField.AttachmentName,
            ["attachmenttext"] = QueryField.AttachmentText, ["pjtexte"] = QueryField.AttachmentText, ["pjcontenu"] = QueryField.AttachmentText,
            ["contenupj"] = QueryField.AttachmentText,
        };

        private static readonly HashSet<string> FilterFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "indice", "spf", "dkim", "dmarc", "fil" };

        private sealed class Tok
        {
            public string Filter; public QueryField Field; public string Text; public bool Phrase; public bool Prefix; public bool Negated; public bool IsOr;
        }

        public static ParsedQuery Parse(string input)
        {
            var result = new ParsedQuery();
            if (string.IsNullOrWhiteSpace(input)) return result;
            var toks = Tokenize(input, result);

            bool pendingOr = false;
            foreach (var t in toks)
            {
                if (t.IsOr) { pendingOr = result.Positive.Count > 0; continue; }
                if (t.Filter != null) { AddFilter(result, t); continue; }
                var term = ToTerm(t);
                if (term == null) continue;
                if (t.Negated)
                {
                    var neg = new QueryClause { Negated = true };
                    neg.Alternatives.Add(term);
                    result.Negative.Add(neg);
                    pendingOr = false;
                    continue;
                }
                if (pendingOr && result.Positive.Count > 0)
                {
                    result.Positive[result.Positive.Count - 1].Alternatives.Add(term);
                    pendingOr = false;
                }
                else
                {
                    var c = new QueryClause();
                    c.Alternatives.Add(term);
                    result.Positive.Add(c);
                }
                foreach (var w in term.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    result.HighlightTerms.Add(w + (term.Prefix ? "*" : ""));
            }
            return result;
        }

        private static void AddFilter(ParsedQuery q, Tok t)
        {
            var f = new QueryFilter { Field = t.Filter.ToLowerInvariant(), Value = t.Text.Trim().ToLowerInvariant(), Negated = t.Negated };
            switch (f.Field)
            {
                case "indice":
                    if (!QueryFilter.Indices.ContainsKey(f.Value))
                    { q.Errors.Add($"Indice inconnu « {t.Text} » (valeurs : {string.Join(", ", QueryFilter.Indices.Keys)})."); return; }
                    break;
                case "fil":
                    if (!long.TryParse(f.Value, out _)) { q.Errors.Add($"fil: attend le numéro d'un fil (« {t.Text} » n'est pas un nombre)."); return; }
                    break;
                default: // spf, dkim, dmarc
                    if (f.Value == "echec" || f.Value == "échec") f.Value = "fail";
                    if (!QueryFilter.AuthValues.Contains(f.Value))
                    { q.Errors.Add($"Résultat « {t.Text} » inconnu pour {f.Field}: (valeurs : {string.Join(", ", QueryFilter.AuthValues)})."); return; }
                    break;
            }
            q.Filters.Add(f);
        }

        private static QueryTerm ToTerm(Tok t)
        {
            // Keep only characters that matter to the tokenizer; the phrase is quoted, so FTS5 operators are harmless.
            var text = t.Text.Replace("\"", " ").Trim();
            if (text.Length == 0 || !text.Any(char.IsLetterOrDigit)) return null;
            return new QueryTerm { Text = text, Phrase = t.Phrase, Prefix = t.Prefix, Field = t.Field };
        }

        private static List<Tok> Tokenize(string s, ParsedQuery q)
        {
            var list = new List<Tok>();
            int i = 0;
            while (i < s.Length)
            {
                while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
                if (i >= s.Length) break;
                var t = new Tok();
                if (s[i] == '-' && i + 1 < s.Length && !char.IsWhiteSpace(s[i + 1])) { t.Negated = true; i++; }
                // field prefix?
                int colon = -1;
                for (int j = i; j < s.Length && !char.IsWhiteSpace(s[j]) && s[j] != '"'; j++)
                    if (s[j] == ':') { colon = j; break; }
                if (colon > i)
                {
                    var name = s.Substring(i, colon - i);
                    if (name.Equals("avant", StringComparison.OrdinalIgnoreCase) || name.Equals("before", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("apres", StringComparison.OrdinalIgnoreCase) || name.Equals("après", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("after", StringComparison.OrdinalIgnoreCase))
                    {
                        int k = colon + 1;
                        while (k < s.Length && !char.IsWhiteSpace(s[k])) k++;
                        var date = ParseDate(s.Substring(colon + 1, k - colon - 1));
                        if (date.HasValue)
                        {
                            if (name.StartsWith("av", StringComparison.OrdinalIgnoreCase) || name.StartsWith("be", StringComparison.OrdinalIgnoreCase)) q.Before = date;
                            else q.After = date;
                            i = k;
                            continue;
                        }
                    }
                    else if (FilterFields.Contains(name))
                    {
                        int k = colon + 1;
                        while (k < s.Length && !char.IsWhiteSpace(s[k])) k++;
                        list.Add(new Tok { Filter = name, Text = s.Substring(colon + 1, k - colon - 1), Negated = t.Negated });
                        i = k;
                        continue;
                    }
                    else if (Fields.TryGetValue(name, out var field))
                    {
                        t.Field = field;
                        i = colon + 1;
                    }
                }
                if (i < s.Length && s[i] == '"')
                {
                    int end = s.IndexOf('"', i + 1);
                    if (end < 0) end = s.Length;
                    t.Text = s.Substring(i + 1, end - i - 1);
                    t.Phrase = true;
                    i = Math.Min(s.Length, end + 1);
                    if (i < s.Length && s[i] == '*') { t.Prefix = true; i++; }
                }
                else
                {
                    int start = i;
                    while (i < s.Length && !char.IsWhiteSpace(s[i])) i++;
                    var w = s.Substring(start, i - start);
                    if (!t.Negated && t.Field == QueryField.Any && (w == "OR" || w == "OU" || w == "|"))
                    {
                        list.Add(new Tok { IsOr = true });
                        continue;
                    }
                    if (w.EndsWith("*")) { t.Prefix = true; w = w.TrimEnd('*'); }
                    t.Text = w;
                }
                if (!string.IsNullOrWhiteSpace(t.Text)) list.Add(t);
            }
            return list;
        }

        public static DateTime? ParseDate(string s)
        {
            string[] formats = { "yyyy-MM-dd", "yyyy-M-d", "dd/MM/yyyy", "d/M/yyyy", "yyyy-MM", "yyyy", "dd.MM.yyyy" };
            if (DateTime.TryParseExact(s, formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d)) return d;
            return null;
        }
    }
}
