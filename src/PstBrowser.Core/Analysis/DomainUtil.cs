using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace PstBrowser.Core.Analysis
{
    /// <summary>Domain helpers for spoofing indications: organisational domain and look-alike detection.</summary>
    public static class DomainUtil
    {
        private static readonly HashSet<string> SecondLevel = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "co", "com", "org", "net", "gov", "ac", "edu", "gouv", "asso", "or", "ne", "go" };

        /// <summary>"mail.paypal.co.uk" → "paypal.co.uk"; "smtp.example.fr" → "example.fr".</summary>
        public static string Organisational(string domain)
        {
            if (string.IsNullOrWhiteSpace(domain)) return null;
            var parts = domain.Trim().ToLowerInvariant().TrimEnd('.').Split('.');
            if (parts.Length <= 2) return string.Join(".", parts);
            int keep = parts[parts.Length - 1].Length == 2 && SecondLevel.Contains(parts[parts.Length - 2]) ? 3 : 2;
            return string.Join(".", parts.Skip(parts.Length - keep));
        }

        /// <summary>The part of the organisational domain before the public suffix ("paypal" in "paypal.co.uk").</summary>
        public static string Label(string organisational)
        {
            if (string.IsNullOrEmpty(organisational)) return null;
            int dot = organisational.IndexOf('.');
            return dot < 0 ? organisational : organisational.Substring(0, dot);
        }

        public static string Suffix(string organisational)
        {
            if (string.IsNullOrEmpty(organisational)) return "";
            int dot = organisational.IndexOf('.');
            return dot < 0 ? "" : organisational.Substring(dot + 1);
        }

        /// <summary>Visual skeleton: characters that look alike are folded together (rn→m, 0→o, 1/i/|→l, vv→w, Cyrillic/Greek homoglyphs…).</summary>
        public static string Skeleton(string label)
        {
            if (string.IsNullOrEmpty(label)) return "";
            var s = IdnToUnicode(label).Normalize(NormalizationForm.FormKC).ToLowerInvariant();
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
            {
                switch (c)
                {
                    case '0': case 'о': case 'ο': sb.Append('o'); break;           // zero, Cyrillic о, Greek ο
                    case '1': case 'i': case 'l': case '|': case 'і': case 'ı': case 'ӏ': sb.Append('l'); break;
                    case '5': sb.Append('s'); break;
                    case 'а': sb.Append('a'); break;                                 // Cyrillic а
                    case 'е': sb.Append('e'); break;
                    case 'р': sb.Append('p'); break;
                    case 'с': sb.Append('c'); break;
                    case 'х': sb.Append('x'); break;
                    case 'у': sb.Append('y'); break;
                    case 'ν': sb.Append('v'); break;
                    case '-': case '_': break;                                       // hyphens are ignored: "pay-pal" ≈ "paypal"
                    default:
                        if (char.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(RemoveAccent(c));
                        break;
                }
            }
            return sb.ToString().Replace("rn", "m").Replace("vv", "w").Replace("cl", "d");
        }

        private static char RemoveAccent(char c)
        {
            var d = c.ToString().Normalize(NormalizationForm.FormD);
            return d.Length > 0 ? d[0] : c;
        }

        public static string IdnToUnicode(string label)
        {
            if (label == null || !label.Contains("xn--", StringComparison.OrdinalIgnoreCase)) return label ?? "";
            try { return new IdnMapping().GetUnicode(label); } catch (ArgumentException) { return label; }
        }

        public static bool IsPunycode(string domain) => domain != null && domain.Contains("xn--", StringComparison.OrdinalIgnoreCase);

        /// <summary>Optimal string alignment distance, stopping early above <paramref name="max"/>.</summary>
        public static int Distance(string a, string b, int max)
        {
            if (a == b) return 0;
            if (Math.Abs(a.Length - b.Length) > max) return max + 1;
            var d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                int rowMin = int.MaxValue;
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    int v = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                    if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) v = Math.Min(v, d[i - 2, j - 2] + 1);
                    d[i, j] = v;
                    if (v < rowMin) rowMin = v;
                }
                if (rowMin > max) return max + 1;
            }
            return d[a.Length, b.Length];
        }

        public sealed class LookAlike
        {
            public string Domain { get; set; }
            public string Resembles { get; set; }
            /// <summary>"homoglyph" (same visual skeleton), "typo" (one edit), "suffix" (same name, other extension), "punycode".</summary>
            public string Kind { get; set; }
        }

        /// <summary>
        /// Rare domains that resemble much more frequent ones of the same case. Frequent = at least <paramref name="minFrequent"/>
        /// messages and at least <paramref name="ratio"/> times more than the look-alike.
        /// </summary>
        public static List<LookAlike> FindLookAlikes(IDictionary<string, long> domainCounts, int minFrequent = 5, int ratio = 5)
        {
            var org = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in domainCounts)
            {
                var o = Organisational(kv.Key);
                if (string.IsNullOrEmpty(o) || !o.Contains('.')) continue; // internal names such as "enron" are not Internet domains
                org[o] = org.TryGetValue(o, out var c) ? c + kv.Value : kv.Value;
            }
            var frequent = org.Where(kv => kv.Value >= minFrequent).Select(kv => (domain: kv.Key, count: kv.Value, label: Label(kv.Key), skel: Skeleton(Label(kv.Key)))).ToList();
            var result = new List<LookAlike>();
            foreach (var kv in org)
            {
                string d = kv.Key, label = Label(d), skel = Skeleton(label);
                if (label.Length < 4) continue;               // too short to be meaningful ("ab.fr")
                foreach (var f in frequent)
                {
                    if (f.domain == d || f.count < kv.Value * ratio) continue;
                    string kind = null;
                    if (label == f.label && Suffix(d) != Suffix(f.domain)) kind = "suffix";
                    else if (label != f.label && skel == f.skel) kind = IsPunycode(d) ? "punycode" : "homoglyph";
                    else if (label != f.label && f.label.Length >= 5 && Distance(label, f.label, 1) <= 1) kind = "typo";
                    if (kind == null) continue;
                    result.Add(new LookAlike { Domain = d, Resembles = f.domain, Kind = kind });
                    break;
                }
            }
            return result;
        }
    }
}
