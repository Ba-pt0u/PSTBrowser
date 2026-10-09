using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace PstBrowser.Core.Analysis
{
    /// <summary>Internet message headers (RFC 5322): unfolded, case-insensitive, repeated headers kept in order.</summary>
    public sealed class MailHeaders
    {
        private readonly List<KeyValuePair<string, string>> _all = new List<KeyValuePair<string, string>>();

        public static MailHeaders Parse(string raw)
        {
            var h = new MailHeaders();
            if (string.IsNullOrEmpty(raw)) return h;
            string name = null; var value = new StringBuilder();
            void Flush() { if (name != null) h._all.Add(new KeyValuePair<string, string>(name, value.ToString().Trim())); name = null; value.Clear(); }
            foreach (var line0 in raw.Split('\n'))
            {
                var line = line0.TrimEnd('\r');
                if (line.Length == 0) { if (name != null || h._all.Count > 0) break; continue; } // end of the header block
                if (line[0] == ' ' || line[0] == '\t') { if (name != null) value.Append(' ').Append(line.Trim()); continue; }
                Flush();
                int c = line.IndexOf(':');
                if (c > 0 && line.IndexOf(' ') is int sp && (sp < 0 || sp > c)) { name = line.Substring(0, c).Trim(); value.Append(line.Substring(c + 1).Trim()); }
            }
            Flush();
            return h;
        }

        public bool IsEmpty => _all.Count == 0;
        public string First(string name) => _all.FirstOrDefault(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
        public IReadOnlyList<string> All(string name) => _all.Where(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Value).ToList();

        /// <summary>Mailbox addresses found in an address header (<c>Name &lt;a@b&gt;, c@d</c>).</summary>
        public static List<string> Addresses(string value)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(value)) return list;
            foreach (Match m in AddressRx.Matches(value)) list.Add(m.Value.Trim('<', '>').ToLowerInvariant());
            return list;
        }

        private static readonly Regex AddressRx = new Regex(@"[A-Za-z0-9._%+\-'!#$&*/=?^`{|}~]+@[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)+", RegexOptions.Compiled);

        public static string DomainOf(string address)
        {
            if (string.IsNullOrEmpty(address)) return null;
            int at = address.LastIndexOf('@');
            return at < 0 || at == address.Length - 1 ? null : address.Substring(at + 1).Trim().TrimEnd('>', '.').ToLowerInvariant();
        }

        // ------------------------------------------------------------ dates

        private static readonly Dictionary<string, int> Zones = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["UT"] = 0, ["UTC"] = 0, ["GMT"] = 0, ["Z"] = 0, ["EST"] = -300, ["EDT"] = -240, ["CST"] = -360, ["CDT"] = -300,
            ["MST"] = -420, ["MDT"] = -360, ["PST"] = -480, ["PDT"] = -420, ["CET"] = 60, ["CEST"] = 120, ["BST"] = 60, ["EET"] = 120, ["EEST"] = 180,
        };

        private static readonly Regex DateRx = new Regex(
            @"(?:(?:Mon|Tue|Wed|Thu|Fri|Sat|Sun)[a-z]*,?\s+)?(?<d>\d{1,2})\s+(?<m>Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-z]*\s+(?<y>\d{2,4})\s+(?<h>\d{1,2}):(?<mi>\d{2})(?::(?<s>\d{2}))?(?:\s*(?<z>[+\-]\d{4}|[A-Za-z]{1,5}))?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Parses an RFC 5322 date (comments and unusual zone names tolerated). Returns UTC.</summary>
        public static DateTime? ParseDate(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var cleaned = Regex.Replace(text, @"\([^)]*\)", " ");
            var m = DateRx.Match(cleaned);
            if (!m.Success) return null;
            try
            {
                int day = int.Parse(m.Groups["d"].Value), year = int.Parse(m.Groups["y"].Value);
                if (year < 100) year += year < 70 ? 2000 : 1900;
                int month = Array.IndexOf(new[] { "jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec" }, m.Groups["m"].Value.Substring(0, 3).ToLowerInvariant()) + 1;
                var local = new DateTime(year, month, day, int.Parse(m.Groups["h"].Value), int.Parse(m.Groups["mi"].Value), m.Groups["s"].Success ? int.Parse(m.Groups["s"].Value) : 0, DateTimeKind.Unspecified);
                int offsetMinutes = 0;
                var z = m.Groups["z"].Value;
                if (z.Length == 5 && (z[0] == '+' || z[0] == '-')) offsetMinutes = (z[0] == '-' ? -1 : 1) * (int.Parse(z.Substring(1, 2)) * 60 + int.Parse(z.Substring(3, 2)));
                else if (z.Length > 0 && Zones.TryGetValue(z, out var zm)) offsetMinutes = zm;
                return DateTime.SpecifyKind(local.AddMinutes(-offsetMinutes), DateTimeKind.Utc);
            }
            catch (ArgumentException) { return null; }
            catch (FormatException) { return null; }
        }
    }
}
