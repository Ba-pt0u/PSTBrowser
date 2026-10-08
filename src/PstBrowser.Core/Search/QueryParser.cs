using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace PstBrowser.Core.Search
{
    /// <summary>
    /// Parses the user's search syntax into an FTS5 MATCH expression.
    ///   mots simples            → all words must match (AND), accents and case ignored
    ///   "expression exacte"     → phrase
    ///   mot*                    → prefix
    ///   a OR b  (ou a OU b)     → either
    ///   -mot                    → exclusion
    ///   de:/from:  à:/to:  objet:/subject:  corps:/body:  pj:/attachment:   → restricts to a field
    ///   avant:/before:AAAA-MM-JJ   apres:/after:AAAA-MM-JJ                     → date filters
    /// </summary>
    public sealed class ParsedQuery
    {
        public string Fts { get; set; }
        public List<string> HighlightTerms { get; } = new List<string>();
        public DateTime? After { get; set; }
        public DateTime? Before { get; set; }
        public bool IsEmpty => string.IsNullOrEmpty(Fts);
    }

    public static class QueryParser
    {
        private static readonly Dictionary<string, string> Fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["from"] = "sender", ["de"] = "sender", ["expediteur"] = "sender", ["expéditeur"] = "sender",
            ["to"] = "recipients", ["a"] = "recipients", ["à"] = "recipients", ["cc"] = "recipients", ["dest"] = "recipients", ["destinataire"] = "recipients",
            ["subject"] = "subject", ["objet"] = "subject", ["sujet"] = "subject",
            ["body"] = "body", ["corps"] = "body", ["texte"] = "body",
            ["attachment"] = "attachments", ["attach"] = "attachments", ["pj"] = "attachments", ["fichier"] = "attachments",
        };

        private sealed class Tok
        {
            public string Field; public string Text; public bool Phrase; public bool Prefix; public bool Negated; public bool IsOr;
        }

        public static ParsedQuery Parse(string input)
        {
            var result = new ParsedQuery();
            if (string.IsNullOrWhiteSpace(input)) return result;
            var toks = Tokenize(input, result);

            var positives = new List<string>(); // already combined with ORs
            var negatives = new List<string>();
            bool pendingOr = false;
            foreach (var t in toks)
            {
                if (t.IsOr) { pendingOr = positives.Count > 0; continue; }
                var expr = ToFts(t);
                if (expr == null) continue;
                if (t.Negated) { negatives.Add(expr); pendingOr = false; continue; }
                if (pendingOr && positives.Count > 0)
                {
                    positives[positives.Count - 1] = "(" + Unwrap(positives[positives.Count - 1]) + " OR " + expr + ")";
                    pendingOr = false;
                }
                else positives.Add(expr);
                foreach (var w in t.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    result.HighlightTerms.Add(w + (t.Prefix ? "*" : ""));
            }
            if (positives.Count == 0) return result; // FTS5 cannot evaluate a purely negative query
            var sb = new StringBuilder(string.Join(" AND ", positives));
            foreach (var n in negatives) sb.Append(" NOT ").Append(n);
            result.Fts = sb.ToString();
            return result;
        }

        private static string Unwrap(string s) => s.StartsWith("(") && s.EndsWith(")") ? s.Substring(1, s.Length - 2) : s;

        private static string ToFts(Tok t)
        {
            // Keep only characters that matter to the tokenizer; quotes are escaped by doubling.
            var text = t.Text.Replace("\"", " ").Trim();
            if (text.Length == 0) return null;
            if (!text.Any(char.IsLetterOrDigit)) return null;
            string phrase = "\"" + text + "\"" + (t.Prefix ? "*" : "");
            return t.Field != null ? t.Field + ":" + phrase : phrase;
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
                    else if (Fields.TryGetValue(name, out var col))
                    {
                        t.Field = col;
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
                    if (!t.Negated && t.Field == null && (w == "OR" || w == "OU" || w == "|"))
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
