using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace PstBrowser.Core.Analysis
{
    [Flags]
    public enum SensitiveKind
    {
        None = 0,
        Iban = 1,
        Card = 2,
        /// <summary>French social security number (NIR).</summary>
        Nir = 4,
        Phone = 8,
        All = Iban | Card | Nir | Phone,
    }

    public sealed class SensitiveHit
    {
        public SensitiveKind Kind { get; set; }
        public int Start { get; set; }
        public int Length { get; set; }
        /// <summary>The value as found (only kept in memory: the index stores <see cref="Masked"/>).</summary>
        public string Value { get; set; }
        public string Masked { get; set; }
    }

    /// <summary>
    /// Detects bank account numbers (IBAN, mod-97), payment card numbers (Luhn + issuer ranges), French social security numbers
    /// (NIR with its control key) and telephone numbers. Checksums keep false positives low; telephone numbers are format-based.
    /// </summary>
    public static class SensitiveScanner
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);
        private const RegexOptions Opt = RegexOptions.Compiled | RegexOptions.CultureInvariant;

        private static readonly Regex IbanRx = new Regex(@"(?<![A-Za-z0-9])[A-Za-z]{2}\d{2}(?:[ \-.]?[A-Za-z0-9]{4}){2,7}(?:[ \-.]?[A-Za-z0-9]{1,4})?(?![A-Za-z0-9])", Opt, Timeout);
        private static readonly Regex CardRx = new Regex(@"(?<![\d.\-])\d(?:[ \-]?\d){12,18}(?![\d])(?![ \-]?\d)", Opt, Timeout);
        private static readonly Regex NirRx = new Regex(@"(?<![\d])[12][ .\-]?\d{2}[ .\-]?(?:0[1-9]|1[0-2]|[2-9]\d)[ .\-]?(?:\d{2}|2[AaBb])[ .\-]?\d{3}[ .\-]?\d{3}[ .\-]?\d{2}(?![\d])", Opt, Timeout);
        private static readonly Regex PhoneFrRx = new Regex(@"(?<![\d+])(?:(?:\+|00)33[ .\-]?\(?0?\)?[ .\-]?|0)[1-9](?:[ .\-]?\d{2}){4}(?![\d])", Opt, Timeout);
        private static readonly Regex PhoneIntlRx = new Regex(@"(?<![\w+])\+(?!33)[1-9]\d{0,2}(?:[ .\-]?\(?\d{1,4}\)?){2,5}(?![\d])", Opt, Timeout);

        private static readonly Dictionary<string, int> IbanLength = new Dictionary<string, int>
        {
            ["AD"] = 24, ["AE"] = 23, ["AL"] = 28, ["AT"] = 20, ["AZ"] = 28, ["BA"] = 20, ["BE"] = 16, ["BG"] = 22, ["BH"] = 22, ["BR"] = 29,
            ["CH"] = 21, ["CR"] = 22, ["CY"] = 28, ["CZ"] = 24, ["DE"] = 22, ["DK"] = 18, ["DO"] = 28, ["EE"] = 20, ["ES"] = 24, ["FI"] = 18,
            ["FO"] = 18, ["FR"] = 27, ["GB"] = 22, ["GE"] = 22, ["GI"] = 23, ["GL"] = 18, ["GR"] = 27, ["GT"] = 28, ["HR"] = 21, ["HU"] = 28,
            ["IE"] = 22, ["IL"] = 23, ["IQ"] = 23, ["IS"] = 26, ["IT"] = 27, ["JO"] = 30, ["KW"] = 30, ["KZ"] = 20, ["LB"] = 28, ["LC"] = 32,
            ["LI"] = 21, ["LT"] = 20, ["LU"] = 20, ["LV"] = 21, ["MC"] = 27, ["MD"] = 24, ["ME"] = 22, ["MK"] = 19, ["MR"] = 27, ["MT"] = 31,
            ["MU"] = 30, ["NL"] = 18, ["NO"] = 15, ["PK"] = 24, ["PL"] = 28, ["PS"] = 29, ["PT"] = 25, ["QA"] = 29, ["RO"] = 24, ["RS"] = 22,
            ["SA"] = 24, ["SE"] = 24, ["SI"] = 19, ["SK"] = 24, ["SM"] = 27, ["TL"] = 23, ["TN"] = 24, ["TR"] = 26, ["UA"] = 29, ["VA"] = 22,
            ["VG"] = 24, ["XK"] = 20,
        };

        /// <summary>Finds sensitive values in a text. Overlaps are resolved by priority: IBAN, NIR, card, phone.</summary>
        public static List<SensitiveHit> Scan(string text, SensitiveKind kinds = SensitiveKind.All, int maxHits = 500)
        {
            var hits = new List<SensitiveHit>();
            if (string.IsNullOrEmpty(text) || text.Length < 8) return hits;
            try
            {
                if (kinds.HasFlag(SensitiveKind.Iban) && HasLetterDigitPair(text)) foreach (Match m in IbanRx.Matches(text)) if (IsIban(m.Value)) hits.Add(Make(SensitiveKind.Iban, m));
                if (kinds.HasFlag(SensitiveKind.Nir)) foreach (Match m in NirRx.Matches(text)) if (IsNir(m.Value)) hits.Add(Make(SensitiveKind.Nir, m));
                if (kinds.HasFlag(SensitiveKind.Card)) foreach (Match m in CardRx.Matches(text)) if (IsCard(m.Value)) hits.Add(Make(SensitiveKind.Card, m));
                if (kinds.HasFlag(SensitiveKind.Phone))
                {
                    foreach (Match m in PhoneFrRx.Matches(text)) hits.Add(Make(SensitiveKind.Phone, m));
                    foreach (Match m in PhoneIntlRx.Matches(text)) if (Digits(m.Value).Length is >= 8 and <= 15) hits.Add(Make(SensitiveKind.Phone, m));
                }
            }
            catch (RegexMatchTimeoutException) { /* pathological text: keep what was found */ }

            // overlaps: the most specific kind wins (enum order is the priority)
            var kept = new List<SensitiveHit>();
            foreach (var h in hits.OrderBy(h => Priority(h.Kind)).ThenBy(h => h.Start))
                if (!kept.Any(k => h.Start < k.Start + k.Length && k.Start < h.Start + h.Length)) kept.Add(h);
            return kept.OrderBy(h => h.Start).Take(maxHits).ToList();
        }

        private static int Priority(SensitiveKind k) => k == SensitiveKind.Iban ? 0 : k == SensitiveKind.Nir ? 1 : k == SensitiveKind.Card ? 2 : 3;

        private static bool HasLetterDigitPair(string text) => true;

        private static SensitiveHit Make(SensitiveKind kind, Match m) => new SensitiveHit
        {
            Kind = kind, Start = m.Index, Length = m.Length, Value = m.Value, Masked = Mask(kind, m.Value),
        };

        private static string Digits(string s) => new string(s.Where(char.IsDigit).ToArray());

        // ------------------------------------------------------------ validators

        public static bool IsIban(string raw)
        {
            var s = new string(raw.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
            if (s.Length < 15 || s.Length > 34) return false;
            if (!IbanLength.TryGetValue(s.Substring(0, 2), out var len) || len != s.Length) return false;
            if (!char.IsDigit(s[2]) || !char.IsDigit(s[3])) return false;
            // mod 97 on the rearranged string, letters = 10..35
            int rem = 0;
            foreach (var c in s.Substring(4) + s.Substring(0, 4))
            {
                int v = char.IsDigit(c) ? c - '0' : c - 'A' + 10;
                rem = v < 10 ? (rem * 10 + v) % 97 : (rem * 100 + v) % 97;
            }
            return rem == 1;
        }

        public static bool IsCard(string raw)
        {
            var d = Digits(raw);
            if (d.Length < 13 || d.Length > 19) return false;
            if (d.Distinct().Count() < 2) return false;                       // 0000 0000 0000 0000
            if (!Luhn(d)) return false;
            return IssuerOk(d);
        }

        private static bool Luhn(string d)
        {
            int sum = 0; bool alt = false;
            for (int i = d.Length - 1; i >= 0; i--)
            {
                int n = d[i] - '0';
                if (alt) { n *= 2; if (n > 9) n -= 9; }
                sum += n; alt = !alt;
            }
            return sum % 10 == 0;
        }

        private static bool IssuerOk(string d)
        {
            int p2 = int.Parse(d.Substring(0, 2)), p4 = int.Parse(d.Substring(0, 4)), p3 = int.Parse(d.Substring(0, 3)), n = d.Length;
            if (d[0] == '4') return n == 13 || n == 16 || n == 19;                         // Visa, CB (Visa)
            if ((p2 >= 51 && p2 <= 55) || (p4 >= 2221 && p4 <= 2720)) return n == 16;      // Mastercard, CB (Mastercard)
            if (p2 == 34 || p2 == 37) return n == 15;                                      // American Express
            if (p4 == 6011 || p2 == 65 || (p3 >= 644 && p3 <= 649)) return n >= 16 && n <= 19; // Discover
            if ((p3 >= 300 && p3 <= 305) || p2 == 36 || p2 == 38 || p2 == 39) return n >= 14 && n <= 19; // Diners
            if (p4 >= 3528 && p4 <= 3589) return n >= 16 && n <= 19;                       // JCB
            if (p2 == 62) return n >= 16 && n <= 19;                                       // UnionPay
            return false;
        }

        /// <summary>French NIR: 13 digits + 2-digit key = 97 − (number mod 97); Corsica 2A / 2B count as 19 / 18.</summary>
        public static bool IsNir(string raw)
        {
            var s = new string(raw.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
            if (s.Length != 15) return false;
            string body = s.Substring(0, 13);
            if (!int.TryParse(s.Substring(13), out int key)) return false;
            long n;
            string dep = body.Substring(5, 2);
            if (dep == "2A") n = long.Parse(body.Substring(0, 5) + "19" + body.Substring(7));
            else if (dep == "2B") n = long.Parse(body.Substring(0, 5) + "18" + body.Substring(7));
            else if (!long.TryParse(body, out n)) return false;
            return key == 97 - (int)(n % 97);
        }

        // ------------------------------------------------------------ masking

        /// <summary>Masked form kept in the index: the type of data stays recognisable, the value does not.</summary>
        public static string Mask(SensitiveKind kind, string value)
        {
            var compact = new string(value.Where(char.IsLetterOrDigit).ToArray());
            switch (kind)
            {
                case SensitiveKind.Iban: return compact.Substring(0, Math.Min(4, compact.Length)).ToUpperInvariant() + " **** **** " + compact.Substring(Math.Max(0, compact.Length - 4)).ToUpperInvariant();
                case SensitiveKind.Card: return "**** **** **** " + compact.Substring(Math.Max(0, compact.Length - 4));
                case SensitiveKind.Nir: return compact.Substring(0, 1) + " ** ** ** *** *** " + compact.Substring(compact.Length - 2);
                default:
                    var d = Digits(value);
                    bool plus = value.TrimStart().StartsWith("+") || value.TrimStart().StartsWith("00");
                    return (plus ? "+" : "") + d.Substring(0, Math.Min(plus ? 4 : 2, d.Length)) + " ** ** ** " + d.Substring(Math.Max(0, d.Length - 2));
            }
        }

        public static string Label(SensitiveKind kind) => kind switch
        {
            SensitiveKind.Iban => "IBAN",
            SensitiveKind.Card => "Carte bancaire",
            SensitiveKind.Nir => "N° de sécurité sociale",
            SensitiveKind.Phone => "Téléphone",
            _ => kind.ToString(),
        };

        /// <summary>"IBAN, Carte bancaire" for the kinds set in a flag value.</summary>
        public static string Labels(int flags)
            => string.Join(", ", new[] { SensitiveKind.Iban, SensitiveKind.Card, SensitiveKind.Nir, SensitiveKind.Phone }.Where(k => (flags & (int)k) != 0).Select(Label));
    }
}
