using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PstBrowser.Core.Text;
using XstReader;
using XstReader.Common;
using XstReader.ElementProperties;

namespace PstBrowser.Core.Mail
{
    /// <summary>
    /// IMailItem over an XstReader message. All properties of the message node are loaded in one read,
    /// then accessed with Peek() to avoid XstReader re-reading the node for each absent property.
    /// </summary>
    internal sealed class PstMailItem : IMailItem
    {
        private readonly XstMessage _m;
        private List<RecipientInfo> _recipients;
        private List<XstAttachment> _xatts;
        private List<AttachmentInfo> _atts;
        private string _html; private bool _htmlDone;
        private string _rtf; private bool _rtfDone;

        // MAPI property tags used here
        internal const ushort TagSubject = 0x0037, TagSentReprName = 0x0042, TagSenderName = 0x0C1A,
            TagSentReprSmtp = 0x5D02, TagSenderSmtp = 0x5D01, TagSentReprEmail = 0x0065, TagSenderEmail = 0x0C1F,
            TagDelivery = 0x0E06, TagSubmit = 0x0039, TagCreation = 0x3007, TagSize = 0x0E08, TagFlags = 0x0E07,
            TagClass = 0x001A, TagImportance = 0x0017, TagMessageId = 0x1035, TagInReplyTo = 0x1042,
            TagHeaders = 0x007D, TagBody = 0x1000, TagHtml = 0x1013, TagRtf = 0x1009, TagCodepage = 0x3FDE,
            TagConversationTopic = 0x0070, TagFlagStatus = 0x1090, TagModification = 0x3008, TagReferences = 0x1039, TagConversationIndex = 0x0071,
            TagDisplayTo = 0x0E04, TagDisplayCc = 0x0E03, TagDisplayBcc = 0x0E02,
            // recipients
            TagRecipType = 0x0C15, TagDisplayName = 0x3001, TagEmailAddress = 0x3003, TagSmtpAddress = 0x39FE,
            TagRecipDisplayName = 0x5FF6,
            // attachments
            TagAttFilename = 0x3704, TagAttLongFilename = 0x3707, TagAttSize = 0x0E20, TagAttMethod = 0x3705,
            TagAttContentId = 0x3712, TagAttMime = 0x370E, TagAttHidden = 0x7FFE, TagAttFlags = 0x3714, TagAttDisplayName = 0x3001;

        public PstMailItem(XstMessage m)
        {
            _m = m;
            // Force the single read of the message's property context (and its sub-node tree)
            _ = m.SubNodeTreeProperties;
        }

        internal XstMessage Message => _m;

        private XstProperty P(ushort tag) => _m.Properties.Peek((PropertyCanonicalName)tag);
        private object V(ushort tag)
        {
            try { return P(tag)?.Value; } catch { return null; }
        }
        private string S(ushort tag) => Clean(V(tag) as string);
        internal static string Clean(string s) => s?.SanitizeControlCharsPublic();

        public string Subject => S(TagSubject);
        public string SenderName => S(TagSentReprName) ?? S(TagSenderName);
        public string SenderEmail => FirstSmtp(S(TagSentReprSmtp), S(TagSenderSmtp), S(TagSentReprEmail), S(TagSenderEmail));
        public DateTime? Date => (V(TagDelivery) as DateTime?) ?? (V(TagSubmit) as DateTime?) ?? (V(TagCreation) as DateTime?);
        public DateTime? SentDate => V(TagSubmit) as DateTime?;
        public long Size => V(TagSize) is int i ? i : (V(TagSize) is long l ? l : 0);
        public string MessageClass => S(TagClass);
        public string InternetMessageId => S(TagMessageId);
        public string InReplyTo => S(TagInReplyTo);
        public int Importance => V(TagImportance) is int i ? i : 1;
        public bool HasAttachmentsFlag => V(TagFlags) is int f && (f & 0x10) != 0;
        public string TransportHeaders => V(TagHeaders) as string;
        public string ConversationTopic => S(TagConversationTopic);
        public bool IsRead => V(TagFlags) is int f && (f & 0x1) != 0;
        public int FlagStatus => V(TagFlagStatus) is int i ? i : 0;
        public DateTime? DeliveryDate => V(TagDelivery) as DateTime?;
        public DateTime? CreatedDate => V(TagCreation) as DateTime?;
        public DateTime? ModifiedDate => V(TagModification) as DateTime?;
        public string InternetReferences => S(TagReferences);
        public string ConversationKey => V(TagConversationIndex) is byte[] b && b.Length >= 22 ? Convert.ToHexString(b, 0, 22) : null;

        internal static string FirstSmtp(params string[] candidates)
        {
            foreach (var c in candidates)
                if (!string.IsNullOrWhiteSpace(c) && c.Contains('@') && !c.StartsWith("/o=", StringComparison.OrdinalIgnoreCase))
                    return c.Trim();
            return null;
        }

        public IReadOnlyList<RecipientInfo> Recipients
        {
            get
            {
                if (_recipients != null) return _recipients;
                _recipients = new List<RecipientInfo>();
                try
                {
                    var nid = new NID(EnidSpecial.NID_RECIPIENT_TABLE);
                    var tree = _m.SubNodeTreeProperties;
                    if (tree != null && _m.XstFile.Ltp.IsTablePresent(tree, nid))
                    {
                        foreach (var r in _m.XstFile.Ltp.ReadTable<XstRecipient>(tree, nid, (r, id) => r.Nid = new NID(id), r => r.Initialize(_m)))
                        {
                            object type = Peek(r, TagRecipType);
                            int t = type is int ti ? ti : 1;
                            if (t < 1 || t > 3) continue; // To / Cc / Bcc only
                            string name = Clean(Peek(r, TagDisplayName) as string) ?? Clean(Peek(r, TagRecipDisplayName) as string);
                            string email = FirstSmtp(Clean(Peek(r, TagSmtpAddress) as string), Clean(Peek(r, TagEmailAddress) as string));
                            if (name == null && email == null) continue;
                            _recipients.Add(new RecipientInfo { Kind = (RecipientKind)t, Name = name, Email = email });
                        }
                    }
                }
                catch { /* damaged recipient table: fall back to display strings */ }
                if (_recipients.Count == 0)
                {
                    AddDisplay(S(TagDisplayTo), RecipientKind.To);
                    AddDisplay(S(TagDisplayCc), RecipientKind.Cc);
                    AddDisplay(S(TagDisplayBcc), RecipientKind.Bcc);
                }
                return _recipients;
            }
        }

        private void AddDisplay(string display, RecipientKind kind)
        {
            if (string.IsNullOrWhiteSpace(display)) return;
            foreach (var part in display.Split(';'))
            {
                var p = part.Trim();
                if (p.Length > 0) _recipients.Add(new RecipientInfo { Kind = kind, Name = p });
            }
        }

        private static object Peek(XstElement e, ushort tag)
        {
            try { return e.Properties.Peek((PropertyCanonicalName)tag)?.Value; } catch { return null; }
        }

        public string PlainBody => V(TagBody) as string;

        public string HtmlBody
        {
            get
            {
                if (_htmlDone) return _html;
                _htmlDone = true;
                var v = V(TagHtml);
                if (v is string s) _html = s;
                else if (v is byte[] b && b.Length > 0)
                {
                    int? cp = V(TagCodepage) is int c ? c : (int?)null;
                    _html = Charsets.DecodeHtml(b, cp);
                }
                return _html;
            }
        }

        public string RtfBody
        {
            get
            {
                if (_rtfDone) return _rtf;
                _rtfDone = true;
                if (V(TagRtf) is byte[] b && b.Length > 16)
                {
                    try
                    {
                        using var ms = new RtfDecompressor().Decompress(b, true);
                        var bytes = ms.ToArray();
                        // RTF is 7-bit ASCII with escapes; Latin1 keeps every byte intact
                        _rtf = System.Text.Encoding.Latin1.GetString(bytes);
                    }
                    catch { _rtf = null; }
                }
                return _rtf;
            }
        }

        private List<XstAttachment> XAttachments
        {
            get
            {
                if (_xatts != null) return _xatts;
                _xatts = new List<XstAttachment>();
                if (!HasAttachmentsFlag) return _xatts;
                try
                {
                    var tree = _m.SubNodeTreeProperties;
                    var nid = new NID(EnidSpecial.NID_ATTACHMENT_TABLE);
                    if (tree == null || !_m.XstFile.Ltp.IsTablePresent(tree, nid)) return _xatts;
                    bool attached = _m.IsAttached;
                    foreach (var a in _m.XstFile.Ltp.ReadTable<XstAttachment>(tree, nid, (a, id) => a.Nid = new NID(id), a => a.Initialize(_m, attached)))
                    {
                        try { _m.XstFile.Ltp.ReadProperties(tree, a.Nid, a.Properties, true); } catch { }
                        _xatts.Add(a);
                    }
                }
                catch { }
                return _xatts;
            }
        }

        public IReadOnlyList<AttachmentInfo> Attachments
        {
            get
            {
                if (_atts != null) return _atts;
                _atts = new List<AttachmentInfo>();
                int idx = 0;
                foreach (var a in XAttachments)
                {
                    int method = Peek(a, TagAttMethod) is int mm ? mm : 1;
                    bool embedded = method == 5;
                    string name = Clean(Peek(a, TagAttLongFilename) as string) ?? Clean(Peek(a, TagAttFilename) as string)
                                  ?? Clean(Peek(a, TagAttDisplayName) as string);
                    if (string.IsNullOrEmpty(name)) name = embedded ? "message.msg" : $"piece-jointe-{idx + 1}";
                    long size = Peek(a, TagAttSize) is int sz ? sz : 0;
                    int flags = Peek(a, TagAttFlags) is int fl ? fl : 0;
                    _atts.Add(new AttachmentInfo
                    {
                        Index = idx,
                        FileName = name,
                        Size = size,
                        IsEmbeddedMessage = embedded,
                        ContentId = Clean(Peek(a, TagAttContentId) as string)?.Trim('<', '>'),
                        MimeType = Clean(Peek(a, TagAttMime) as string),
                        IsHidden = Peek(a, TagAttHidden) is bool h && h,
                        IsInline = (flags & 0x4) != 0,
                    });
                    idx++;
                }
                return _atts;
            }
        }

        public void SaveAttachment(int index, Stream target)
        {
            var a = XAttachments[index];
            if (Attachments[index].IsEmbeddedMessage)
                throw new InvalidOperationException("Embedded message: use OpenEmbeddedMessage");
            a.SaveToStream(target);
        }

        public IMailItem OpenEmbeddedMessage(int index)
        {
            var a = XAttachments[index];
            var m = a.AttachedEmailMessage ?? throw new InvalidOperationException("Not an embedded message");
            return new PstMailItem(m);
        }
    }

    internal static class XstStringExt
    {
        public static string SanitizeControlCharsPublic(this string value)
        {
            if (value == null) return null;
            var sb = new System.Text.StringBuilder(value.Length);
            foreach (var c in value) if (!char.IsControl(c)) sb.Append(c);
            var s = sb.ToString().Trim();
            return s.Length == 0 ? null : s;
        }
    }
}
