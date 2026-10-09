using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace PstBrowser.Core.Analysis
{
    /// <summary>One server the message went through ("Received:" header), oldest first.</summary>
    public sealed class ReceivedHop
    {
        public int Index { get; set; }
        public string From { get; set; }
        public string FromIp { get; set; }
        public string By { get; set; }
        public string With { get; set; }
        public DateTime? Time { get; set; }
        /// <summary>Time since the previous hop.</summary>
        public TimeSpan? Delay { get; set; }
        public string Raw { get; set; }
    }

    /// <summary>Authentication verdicts: pass, fail, softfail, neutral, none, temperror, permerror (null = not reported).</summary>
    public sealed class AuthResults
    {
        public string Spf { get; set; }
        public string Dkim { get; set; }
        public string Dmarc { get; set; }
        /// <summary>Domains the verdicts refer to (smtp.mailfrom, header.d, header.from).</summary>
        public string SpfDomain { get; set; }
        public string DkimDomain { get; set; }
        public string DmarcDomain { get; set; }
        public string Source { get; set; }
        public bool Any => Spf != null || Dkim != null || Dmarc != null;
        public bool AnyFailure => IsFailure(Spf) || IsFailure(Dkim) || IsFailure(Dmarc);
        public static bool IsFailure(string v) => v == "fail" || v == "softfail" || v == "permerror";
    }

    [Flags]
    public enum SpoofFlags
    {
        None = 0,
        ReplyToMismatch = 1,
        ReturnPathMismatch = 2,
        MisleadingName = 4,
        LookAlikeDomain = 8,
        AuthFailure = 16,
        /// <summary>The same display name is used with a much more frequent address of another organisation (case-wide analysis).</summary>
        NameImpersonation = 32,
        /// <summary>Every bit but the authentication one: what "indice:usurpation" looks for.</summary>
        Any = ReplyToMismatch | ReturnPathMismatch | MisleadingName | LookAlikeDomain | AuthFailure | NameImpersonation,
    }

    public sealed class Finding
    {
        public string Code { get; set; }
        public string Text { get; set; }
        public override string ToString() => Text;
    }

    public sealed class HeaderAnalysis
    {
        public bool HasHeaders { get; set; }
        public List<ReceivedHop> Hops { get; } = new List<ReceivedHop>();
        public AuthResults Auth { get; set; } = new AuthResults();
        public string From { get; set; }
        public string ReplyTo { get; set; }
        public string ReturnPath { get; set; }
        public string MessageId { get; set; }
        public string Mailer { get; set; }
        public DateTime? HeaderDate { get; set; }
        public SpoofFlags Flags { get; set; }
        public List<Finding> Findings { get; } = new List<Finding>();
    }

    /// <summary>
    /// Reads the transport headers of a message: path of the servers, SPF / DKIM / DMARC verdicts, and spoofing indications.
    /// Everything here is an indication, never a proof: mailing lists and sending services legitimately differ.
    /// </summary>
    public static class HeaderAnalyzer
    {
        private static readonly Regex Ip4 = new Regex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b", RegexOptions.Compiled);
        private static readonly Regex Ip6 = new Regex(@"\[(?:IPv6:)?([0-9a-fA-F:]{6,})\]", RegexOptions.Compiled);
        private static readonly Regex Ip6Paren = new Regex(@"\(([0-9a-fA-F]{1,4}(?::[0-9a-fA-F]{0,4}){3,7})\)", RegexOptions.Compiled);
        private static readonly Regex MethodRx = new Regex(@"\b(spf|dkim|dmarc)\s*=\s*([a-z]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static HeaderAnalysis Analyze(string transportHeaders, string senderName, string senderEmail)
        {
            var a = new HeaderAnalysis { From = senderEmail };
            var h = MailHeaders.Parse(transportHeaders);
            a.HasHeaders = !h.IsEmpty;
            AddNameFinding(a, senderName, senderEmail); // needs no header: sent items and messages without transport headers are checked too
            if (!a.HasHeaders) return a;

            a.MessageId = h.First("Message-ID");
            a.Mailer = h.First("X-Mailer") ?? h.First("User-Agent");
            a.HeaderDate = MailHeaders.ParseDate(h.First("Date"));
            ReadHops(h, a);
            a.Auth = ReadAuth(h);

            string fromHeader = MailHeaders.Addresses(h.First("From")).FirstOrDefault() ?? senderEmail?.ToLowerInvariant();
            a.From = fromHeader ?? senderEmail;
            a.ReplyTo = string.Join(", ", MailHeaders.Addresses(h.First("Reply-To")));
            a.ReturnPath = MailHeaders.Addresses(h.First("Return-Path")).FirstOrDefault();
            if (a.ReplyTo.Length == 0) a.ReplyTo = null;

            string fromOrg = DomainUtil.Organisational(MailHeaders.DomainOf(fromHeader));
            if (fromOrg != null)
            {
                var replyDomains = MailHeaders.Addresses(h.First("Reply-To")).Select(x => DomainUtil.Organisational(MailHeaders.DomainOf(x))).Where(x => x != null).Distinct().ToList();
                if (replyDomains.Count > 0 && replyDomains.All(d => d != fromOrg))
                {
                    a.Flags |= SpoofFlags.ReplyToMismatch;
                    a.Findings.Add(new Finding { Code = "reply-to", Text = $"L'adresse de réponse (Reply-To : {a.ReplyTo}) est d'un autre domaine que l'expéditeur ({fromHeader}) : une réponse partirait vers un autre domaine." });
                }
                var rp = DomainUtil.Organisational(MailHeaders.DomainOf(a.ReturnPath));
                if (rp != null && rp != fromOrg)
                {
                    a.Flags |= SpoofFlags.ReturnPathMismatch;
                    a.Findings.Add(new Finding { Code = "return-path", Text = $"Le Return-Path ({a.ReturnPath}) est d'un autre domaine que l'expéditeur ({fromHeader}). Fréquent avec les listes de diffusion et les services d'envoi en masse, suspect sinon." });
                }
            }
            if (a.Auth.AnyFailure)
            {
                a.Flags |= SpoofFlags.AuthFailure;
                var bad = new List<string>();
                if (AuthResults.IsFailure(a.Auth.Spf)) bad.Add("SPF " + a.Auth.Spf);
                if (AuthResults.IsFailure(a.Auth.Dkim)) bad.Add("DKIM " + a.Auth.Dkim);
                if (AuthResults.IsFailure(a.Auth.Dmarc)) bad.Add("DMARC " + a.Auth.Dmarc);
                a.Findings.Add(new Finding { Code = "auth", Text = "Authentification en échec : " + string.Join(", ", bad) + ". Le serveur récepteur n'a pas pu confirmer que l'expéditeur est autorisé à utiliser ce domaine." });
            }
            else if (!a.Auth.Any)
                a.Findings.Add(new Finding { Code = "auth-absent", Text = "Aucun résultat SPF / DKIM / DMARC dans les en-têtes (message interne, envoyé, ou en-têtes d'authentification supprimés)." });
            return a;
        }

        /// <summary>Display name containing an address of another domain than the real sender (<c>"paul@banque.fr" &lt;x@autre.ru&gt;</c>).</summary>
        public static bool HasMisleadingDisplayName(string senderName, string senderEmail, out string shown)
        {
            shown = null;
            if (string.IsNullOrWhiteSpace(senderName) || string.IsNullOrWhiteSpace(senderEmail)) return false;
            var realOrg = DomainUtil.Organisational(MailHeaders.DomainOf(senderEmail));
            foreach (var addr in MailHeaders.Addresses(senderName))
            {
                var org = DomainUtil.Organisational(MailHeaders.DomainOf(addr));
                if (org != null && realOrg != null && org != realOrg) { shown = addr; return true; }
            }
            return false;
        }

        private static void AddNameFinding(HeaderAnalysis a, string name, string email)
        {
            if (HasMisleadingDisplayName(name, email, out var shown))
            {
                a.Flags |= SpoofFlags.MisleadingName;
                a.Findings.Add(new Finding { Code = "display-name", Text = $"Le nom affiché (« {name} ») contient une adresse ({shown}) qui n'est pas celle de l'expéditeur réel ({email}) : le lecteur de messagerie peut faire croire à un autre expéditeur." });
            }
        }

        // ------------------------------------------------------------ Received

        private static void ReadHops(MailHeaders h, HeaderAnalysis a)
        {
            var raw = h.All("Received").ToList();
            raw.Reverse(); // the first Received header is the last server: oldest first
            DateTime? previous = null;
            int i = 1;
            foreach (var r in raw)
            {
                var hop = new ReceivedHop { Index = i++, Raw = r };
                int semi = r.LastIndexOf(';');
                if (semi >= 0) hop.Time = MailHeaders.ParseDate(r.Substring(semi + 1));
                string body = semi >= 0 ? r.Substring(0, semi) : r;
                hop.From = Between(body, "from", new[] { "by", "with", "id", "for", "via" });
                hop.By = Between(body, "by", new[] { "with", "id", "for", "via", "from" });
                hop.With = Between(body, "with", new[] { "id", "for", "by", "from", "via" });
                var fromPart = Regex.Match(body, @"\bfrom\b(.*?)(?=\bby\b|$)", RegexOptions.IgnoreCase | RegexOptions.Singleline).Value;
                var m6 = Ip6.Match(fromPart);
                if (!m6.Success) m6 = Ip6Paren.Match(fromPart);
                var ips = Ip4.Matches(fromPart).Select(x => x.Value).Where(IsRoutableText).ToList();
                hop.FromIp = m6.Success ? m6.Groups[1].Value : ips.LastOrDefault() ?? Ip4.Matches(fromPart).Select(x => x.Value).LastOrDefault();
                if (hop.Time.HasValue && previous.HasValue) hop.Delay = hop.Time.Value - previous.Value;
                if (hop.Time.HasValue) previous = hop.Time;
                a.Hops.Add(hop);
            }
        }

        private static bool IsRoutableText(string ip)
            => !(ip.StartsWith("10.") || ip.StartsWith("127.") || ip.StartsWith("192.168.") || ip.StartsWith("169.254.") || Regex.IsMatch(ip, @"^172\.(1[6-9]|2\d|3[01])\."));

        private static string Between(string text, string keyword, string[] stops)
        {
            var m = Regex.Match(text, @"(?:^|\s)" + keyword + @"\s+", RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            int start = m.Index + m.Length;
            int end = text.Length;
            foreach (var s in stops)
            {
                var sm = Regex.Match(text.Substring(start), @"\s" + s + @"\s", RegexOptions.IgnoreCase);
                if (sm.Success && start + sm.Index < end) end = start + sm.Index;
            }
            var value = text.Substring(start, end - start).Trim();
            return value.Length == 0 ? null : (value.Length > 200 ? value.Substring(0, 200) : value);
        }

        // ------------------------------------------------------------ SPF / DKIM / DMARC

        private static AuthResults ReadAuth(MailHeaders h)
        {
            var r = new AuthResults();
            // the topmost Authentication-Results header was added by the server that delivered the message: the one to trust
            foreach (var header in h.All("Authentication-Results"))
            {
                Apply(r, header, "Authentication-Results");
                if (r.Any) break;
            }
            if (r.Spf == null)
            {
                var rs = h.First("Received-SPF");
                if (rs != null)
                {
                    var m = Regex.Match(rs, @"^\s*([a-z]+)", RegexOptions.IgnoreCase);
                    if (m.Success) { r.Spf = m.Groups[1].Value.ToLowerInvariant(); r.Source = r.Source ?? "Received-SPF"; }
                }
            }
            if (r.Dkim == null && h.First("DKIM-Signature") != null && r.Source == null) { /* signed, but not verified by this server */ }
            return r;
        }

        private static void Apply(AuthResults r, string header, string source)
        {
            foreach (Match m in MethodRx.Matches(header))
            {
                string method = m.Groups[1].Value.ToLowerInvariant(), verdict = m.Groups[2].Value.ToLowerInvariant();
                switch (method)
                {
                    case "spf": r.Spf ??= verdict; break;
                    case "dkim": r.Dkim = Combine(r.Dkim, verdict); break; // several signatures: pass wins
                    case "dmarc": r.Dmarc ??= verdict; break;
                }
            }
            if (!r.Any) return;
            r.Source = source;
            r.SpfDomain = Domain(header, @"smtp\.mailfrom\s*=\s*([^\s;]+)");
            r.DkimDomain = Domain(header, @"header\.d\s*=\s*([^\s;]+)") ?? Domain(header, @"header\.i\s*=\s*([^\s;]+)");
            r.DmarcDomain = Domain(header, @"header\.from\s*=\s*([^\s;]+)");
        }

        private static string Combine(string current, string verdict) => current == "pass" || verdict == "pass" ? "pass" : current ?? verdict;

        private static string Domain(string header, string pattern)
        {
            var m = Regex.Match(header, pattern, RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            var v = m.Groups[1].Value.Trim('"', '<', '>', '@');
            int at = v.LastIndexOf('@');
            return (at >= 0 ? v.Substring(at + 1) : v).ToLowerInvariant();
        }
    }
}
