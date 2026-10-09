using System;
using System.Linq;
using PstBrowser.Core.Mail;

namespace PstBrowser.Core.Analysis
{
    public sealed class MessageAnalysis
    {
        public HeaderAnalysis Header { get; set; }
        public DateAnalysis Dates { get; set; }
        public string ConversationKey { get; set; }
        public string References { get; set; }
        public string InReplyTo { get; set; }
    }

    /// <summary>Header and date analysis of one opened message (used when indexing and by the reader's "Analyse" window).</summary>
    public static class MessageAnalyzer
    {
        public static DateTime? Utc(DateTime? d)
            => !d.HasValue ? null : (DateTime?)(d.Value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(d.Value, DateTimeKind.Utc) : d.Value.ToUniversalTime());

        public static MessageAnalysis Analyze(IMailItem m, DateTime? nowUtc = null)
        {
            string headers = null;
            try { headers = m.TransportHeaders; } catch { }
            var header = HeaderAnalyzer.Analyze(headers, m.SenderName, m.SenderEmail);
            var dates = DateAnalyzer.Analyze(Utc(m.DeliveryDate), Utc(m.SentDate), Utc(m.CreatedDate), Utc(m.ModifiedDate), header.HeaderDate, header.Hops, nowUtc ?? DateTime.UtcNow);
            string refs = null, conv = null;
            try { refs = m.InternetReferences; conv = m.ConversationKey; } catch { }
            string inReplyTo = null;
            if (!string.IsNullOrEmpty(headers))
            {
                var h = MailHeaders.Parse(headers);
                if (string.IsNullOrWhiteSpace(refs)) refs = h.First("References");
                inReplyTo = h.First("In-Reply-To");
            }
            if (refs != null && refs.Length > 4000) refs = refs.Substring(refs.Length - 4000);
            return new MessageAnalysis { Header = header, Dates = dates, ConversationKey = conv, References = refs, InReplyTo = inReplyTo };
        }
    }
}
