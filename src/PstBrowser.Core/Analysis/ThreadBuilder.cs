using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace PstBrowser.Core.Analysis
{
    public sealed class ThreadItem
    {
        public long Id { get; set; }
        public string MessageId { get; set; }
        public string InReplyTo { get; set; }
        public string References { get; set; }
        /// <summary>First 22 bytes of the Outlook conversation index (hex): identical for every message of a conversation.</summary>
        public string ConversationKey { get; set; }
        public string Topic { get; set; }
        public long? Date { get; set; }
        /// <summary>Lower-case addresses of the sender and recipients (weak links only).</summary>
        public HashSet<string> Parties
        {
            get => _parties ??= PartyText == null ? null : new HashSet<string>(MailHeaders.Addresses(PartyText));
            set => _parties = value;
        }
        private HashSet<string> _parties;
        /// <summary>Sender and recipients as text; parsed only when a weak link has to be checked.</summary>
        public string PartyText { get; set; }
    }

    /// <summary>
    /// Rebuilds conversation threads across all the mailboxes of a case. Strong links: Message-ID / In-Reply-To / References, the
    /// Outlook conversation index. Weak link: same normalised subject, within 120 days, with a participant in common.
    /// </summary>
    public static class ThreadBuilder
    {
        public static readonly TimeSpan WeakLinkGap = TimeSpan.FromDays(120);
        private static readonly Regex Prefix = new Regex(@"^\s*(?:(?:re|tr|fw|fwd|rv|aw|wg|sv|vs|antw|res|enc)\s*(?:\[\d+\])?\s*:\s*)+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Ids = new Regex(@"<([^<>\s]+)>", RegexOptions.Compiled);

        public static string NormalizeSubject(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            return Regex.Replace(Prefix.Replace(s, ""), @"\s+", " ").Trim().ToLowerInvariant();
        }

        public static string NormalizeId(string id) => string.IsNullOrWhiteSpace(id) ? null : id.Trim().Trim('<', '>').ToLowerInvariant();

        public static IEnumerable<string> ReferencedIds(ThreadItem t)
        {
            var ids = new List<string>();
            var irt = NormalizeId(t.InReplyTo);
            if (irt != null) ids.Add(irt);
            if (!string.IsNullOrEmpty(t.References))
            {
                var found = Ids.Matches(t.References).Select(m => NormalizeId(m.Groups[1].Value)).ToList();
                ids.AddRange(found.Count > 0 ? found : t.References.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(NormalizeId));
            }
            return ids.Where(x => x != null);
        }

        /// <summary>Returns, for each message id, the thread it belongs to (smallest message id of the thread) and the thread size.</summary>
        public static Dictionary<long, (long thread, int count)> Build(IList<ThreadItem> items)
        {
            var parent = new Dictionary<long, long>(items.Count);
            long Find(long x)
            {
                while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
                return x;
            }
            void Union(long a, long b)
            {
                long ra = Find(a), rb = Find(b);
                if (ra == rb) return;
                if (ra < rb) parent[rb] = ra; else parent[ra] = rb; // the smallest id is the root
            }
            foreach (var t in items) parent[t.Id] = t.Id;

            var byMessageId = new Dictionary<string, long>();
            var byConversation = new Dictionary<string, long>();
            foreach (var t in items)
            {
                var mid = NormalizeId(t.MessageId);
                if (mid != null) { if (byMessageId.TryGetValue(mid, out var other)) Union(t.Id, other); else byMessageId[mid] = t.Id; }
                if (!string.IsNullOrEmpty(t.ConversationKey)) { if (byConversation.TryGetValue(t.ConversationKey, out var o2)) Union(t.Id, o2); else byConversation[t.ConversationKey] = t.Id; }
            }
            foreach (var t in items)
                foreach (var rid in ReferencedIds(t))
                    if (byMessageId.TryGetValue(rid, out var target)) Union(t.Id, target);

            // weak link: same subject, close in time, a participant in common
            var gap = (long)WeakLinkGap.TotalMilliseconds;
            foreach (var group in items.Where(t => t.Date.HasValue).GroupBy(t => NormalizeSubject(t.Topic)).Where(g => g.Key.Length >= 6))
            {
                var ordered = group.OrderBy(t => t.Date.Value).ToList();
                for (int i = 1; i < ordered.Count; i++)
                    for (int j = i - 1; j >= 0 && j >= i - 8; j--)
                    {
                        if (ordered[i].Date.Value - ordered[j].Date.Value > gap) break;
                        if (Find(ordered[i].Id) == Find(ordered[j].Id)) break;
                        if (Shares(ordered[i].Parties, ordered[j].Parties)) { Union(ordered[i].Id, ordered[j].Id); break; }
                    }
            }

            var sizes = new Dictionary<long, int>();
            foreach (var t in items) { var r = Find(t.Id); sizes[r] = sizes.TryGetValue(r, out var c) ? c + 1 : 1; }
            var result = new Dictionary<long, (long, int)>(items.Count);
            foreach (var t in items) { var r = Find(t.Id); result[t.Id] = (r, sizes[r]); }
            return result;
        }

        private static bool Shares(HashSet<string> a, HashSet<string> b)
            => a != null && b != null && a.Count > 0 && b.Count > 0 && (a.Count < b.Count ? a.Any(b.Contains) : b.Any(a.Contains));
    }
}
