using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using PstBrowser.Core.Text;
using XstReader.Common;

namespace PstBrowser.Core.Mail
{
    /// <summary>MAPI properties of one storage of a .msg file ([MS-OXMSG]).</summary>
    internal sealed class MsgPropertyBag
    {
        private readonly CompoundFile _cf;
        private readonly CompoundFile.Entry _storage;
        private readonly Dictionary<ushort, object> _fixed = new Dictionary<ushort, object>();
        private readonly Dictionary<ushort, (ushort type, CompoundFile.Entry entry)> _variable = new Dictionary<ushort, (ushort, CompoundFile.Entry)>();
        public Encoding String8Encoding { get; set; }

        /// <param name="headerSize">32 for the top-level message, 24 for an embedded message, 8 for recipients/attachments.</param>
        public MsgPropertyBag(CompoundFile cf, CompoundFile.Entry storage, int headerSize)
        {
            _cf = cf;
            _storage = storage;
            foreach (var c in storage.Children)
            {
                if (!c.Name.StartsWith("__substg1.0_", StringComparison.OrdinalIgnoreCase) || c.Name.Length < 20) continue;
                var hex = c.Name.Substring(12, 8);
                if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var tag)) continue;
                ushort id = (ushort)(tag >> 16), type = (ushort)(tag & 0xFFFF);
                if ((type & 0x1000) != 0) continue; // multi-valued: not needed
                if (!_variable.ContainsKey(id) || type == 0x001F) _variable[id] = (type, c);
            }
            var props = CompoundFile.Find(storage, "__properties_version1.0");
            var bytes = props != null ? cf.ReadStream(props, 16 * 1024 * 1024) : null;
            if (bytes != null)
            {
                for (int off = headerSize; off + 16 <= bytes.Length; off += 16)
                {
                    uint tag = BitConverter.ToUInt32(bytes, off);
                    ushort id = (ushort)(tag >> 16), type = (ushort)(tag & 0xFFFF);
                    object v = type switch
                    {
                        0x0002 => (object)(int)BitConverter.ToInt16(bytes, off + 8),
                        0x0003 => BitConverter.ToInt32(bytes, off + 8),
                        0x000B => bytes[off + 8] != 0,
                        0x0014 => BitConverter.ToInt64(bytes, off + 8),
                        0x0005 => BitConverter.ToDouble(bytes, off + 8),
                        0x0040 => SafeFileTime(BitConverter.ToInt64(bytes, off + 8)),
                        _ => null,
                    };
                    if (v != null) _fixed[id] = v;
                }
            }
        }

        private static object SafeFileTime(long ft)
        {
            try { return ft <= 0 ? null : (object)DateTime.FromFileTimeUtc(ft); } catch { return null; }
        }

        public CompoundFile.Entry Storage => _storage;

        public object Fixed(ushort id) => _fixed.TryGetValue(id, out var v) ? v : null;
        public int? Int(ushort id) => Fixed(id) is int i ? i : (int?)null;
        public DateTime? Time(ushort id) => Fixed(id) as DateTime?;

        public string String(ushort id, int maxBytes = 64 * 1024 * 1024)
        {
            if (!_variable.TryGetValue(id, out var v)) return null;
            if (v.type != 0x001F && v.type != 0x001E) return null;
            var b = _cf.ReadStream(v.entry, maxBytes);
            if (b == null) return null;
            var s = v.type == 0x001F ? Encoding.Unicode.GetString(b) : (String8Encoding ?? Encoding.Latin1).GetString(b);
            return s.TrimEnd('\0');
        }

        public byte[] Binary(ushort id, int maxBytes = 256 * 1024 * 1024)
        {
            if (!_variable.TryGetValue(id, out var v) || v.type != 0x0102) return null;
            return _cf.ReadStream(v.entry, maxBytes);
        }

        public object Raw(ushort id, out ushort type)
        {
            type = 0;
            if (_variable.TryGetValue(id, out var v)) { type = v.type; return v.entry; }
            return null;
        }
    }

    /// <summary>IMailItem over a .msg file (or a message embedded in one).</summary>
    public sealed class MsgMailItem : IMailItem, IDisposable
    {
        private readonly CompoundFile _cf;
        private readonly bool _ownsFile;
        private readonly MsgPropertyBag _p;
        private List<RecipientInfo> _recipients;
        private List<(AttachmentInfo info, MsgPropertyBag bag)> _atts;
        private string _html, _rtf; private bool _htmlDone, _rtfDone;

        public string FilePath { get; }

        public MsgMailItem(string path)
        {
            FilePath = path;
            _cf = new CompoundFile(path);
            _ownsFile = true;
            _p = new MsgPropertyBag(_cf, _cf.Root, 32);
            InitEncoding();
        }

        /// <summary>Opens a .msg held in memory or in any seekable stream (the stream is disposed with the item).</summary>
        public MsgMailItem(Stream stream)
        {
            _cf = new CompoundFile(stream, true);
            _ownsFile = true;
            _p = new MsgPropertyBag(_cf, _cf.Root, 32);
            InitEncoding();
        }

        private MsgMailItem(CompoundFile cf, CompoundFile.Entry storage)
        {
            _cf = cf;
            _p = new MsgPropertyBag(cf, storage, 24);
            InitEncoding();
        }

        private void InitEncoding()
        {
            int? cp = _p.Int(0x3FFD) ?? _p.Int(PstMailItem.TagCodepage);
            _p.String8Encoding = (cp.HasValue ? Charsets.TryGet(cp.Value) : null) ?? Charsets.TryGet(1252);
        }

        private string S(ushort id) => PstMailItem.Clean(_p.String(id));

        public string Subject => S(PstMailItem.TagSubject);
        public string SenderName => S(PstMailItem.TagSentReprName) ?? S(PstMailItem.TagSenderName);
        public string SenderEmail => PstMailItem.FirstSmtp(S(PstMailItem.TagSentReprSmtp), S(PstMailItem.TagSenderSmtp),
                                                          S(PstMailItem.TagSentReprEmail), S(PstMailItem.TagSenderEmail));
        public DateTime? Date => _p.Time(PstMailItem.TagDelivery) ?? _p.Time(PstMailItem.TagSubmit) ?? _p.Time(PstMailItem.TagCreation);
        public DateTime? SentDate => _p.Time(PstMailItem.TagSubmit);
        public long Size => _p.Int(PstMailItem.TagSize) ?? 0;
        public string MessageClass => S(PstMailItem.TagClass);
        public string InternetMessageId => S(PstMailItem.TagMessageId);
        public string InReplyTo => S(PstMailItem.TagInReplyTo);
        public int Importance => _p.Int(PstMailItem.TagImportance) ?? 1;
        public bool HasAttachmentsFlag => (_p.Int(PstMailItem.TagFlags) is int f && (f & 0x10) != 0) || AttachmentsRaw.Count > 0;
        public string TransportHeaders => _p.String(PstMailItem.TagHeaders);
        public string ConversationTopic => S(PstMailItem.TagConversationTopic);
        public bool IsRead => _p.Int(PstMailItem.TagFlags) is int f && (f & 0x1) != 0;
        public int FlagStatus => _p.Int(PstMailItem.TagFlagStatus) ?? 0;

        public IReadOnlyList<RecipientInfo> Recipients
        {
            get
            {
                if (_recipients != null) return _recipients;
                _recipients = new List<RecipientInfo>();
                foreach (var st in _p.Storage.Children.Where(c => c.IsStorage && c.Name.StartsWith("__recip_version1.0_", StringComparison.OrdinalIgnoreCase)).OrderBy(c => c.Name))
                {
                    var bag = new MsgPropertyBag(_cf, st, 8) { String8Encoding = _p.String8Encoding };
                    int t = bag.Int(PstMailItem.TagRecipType) ?? 1;
                    if (t < 1 || t > 3) continue;
                    string name = PstMailItem.Clean(bag.String(PstMailItem.TagDisplayName)) ?? PstMailItem.Clean(bag.String(PstMailItem.TagRecipDisplayName));
                    string email = PstMailItem.FirstSmtp(PstMailItem.Clean(bag.String(PstMailItem.TagSmtpAddress)), PstMailItem.Clean(bag.String(PstMailItem.TagEmailAddress)));
                    if (name == null && email == null) continue;
                    _recipients.Add(new RecipientInfo { Kind = (RecipientKind)t, Name = name, Email = email });
                }
                return _recipients;
            }
        }

        public string PlainBody => _p.String(PstMailItem.TagBody);

        public string HtmlBody
        {
            get
            {
                if (_htmlDone) return _html;
                _htmlDone = true;
                var b = _p.Binary(PstMailItem.TagHtml);
                if (b != null && b.Length > 0)
                    _html = Charsets.DecodeHtml(b, _p.Int(PstMailItem.TagCodepage));
                else
                    _html = _p.String(PstMailItem.TagHtml);
                return _html;
            }
        }

        public string RtfBody
        {
            get
            {
                if (_rtfDone) return _rtf;
                _rtfDone = true;
                var b = _p.Binary(PstMailItem.TagRtf);
                if (b != null && b.Length > 16)
                {
                    try
                    {
                        using var ms = new RtfDecompressor().Decompress(b, true);
                        _rtf = Encoding.Latin1.GetString(ms.ToArray());
                    }
                    catch { _rtf = null; }
                }
                return _rtf;
            }
        }

        private List<(AttachmentInfo info, MsgPropertyBag bag)> AttachmentsRaw
        {
            get
            {
                if (_atts != null) return _atts;
                _atts = new List<(AttachmentInfo, MsgPropertyBag)>();
                int idx = 0;
                foreach (var st in _p.Storage.Children.Where(c => c.IsStorage && c.Name.StartsWith("__attach_version1.0_", StringComparison.OrdinalIgnoreCase)).OrderBy(c => c.Name))
                {
                    var bag = new MsgPropertyBag(_cf, st, 8) { String8Encoding = _p.String8Encoding };
                    int method = bag.Int(PstMailItem.TagAttMethod) ?? 1;
                    bag.Raw(0x3701, out var dataType);
                    bool embedded = method == 5 || dataType == 0x000D;
                    string name = PstMailItem.Clean(bag.String(PstMailItem.TagAttLongFilename)) ?? PstMailItem.Clean(bag.String(PstMailItem.TagAttFilename))
                                  ?? PstMailItem.Clean(bag.String(PstMailItem.TagAttDisplayName));
                    if (string.IsNullOrEmpty(name)) name = embedded ? "message.msg" : $"piece-jointe-{idx + 1}";
                    long size = bag.Int(PstMailItem.TagAttSize) ?? 0;
                    if (!embedded && bag.Raw(0x3701, out _) is CompoundFile.Entry de) size = de.Size;
                    int flags = bag.Int(PstMailItem.TagAttFlags) ?? 0;
                    _atts.Add((new AttachmentInfo
                    {
                        Index = idx,
                        FileName = name,
                        Size = size,
                        IsEmbeddedMessage = embedded,
                        ContentId = PstMailItem.Clean(bag.String(PstMailItem.TagAttContentId))?.Trim('<', '>'),
                        MimeType = PstMailItem.Clean(bag.String(PstMailItem.TagAttMime)),
                        IsHidden = bag.Fixed(PstMailItem.TagAttHidden) is bool h && h,
                        IsInline = (flags & 0x4) != 0,
                    }, bag));
                    idx++;
                }
                return _atts;
            }
        }

        public IReadOnlyList<AttachmentInfo> Attachments => AttachmentsRaw.Select(a => a.info).ToList();

        public void SaveAttachment(int index, Stream target)
        {
            var (info, bag) = AttachmentsRaw[index];
            if (info.IsEmbeddedMessage) throw new InvalidOperationException("Embedded message: use OpenEmbeddedMessage");
            if (bag.Raw(0x3701, out var type) is CompoundFile.Entry e && type == 0x0102)
                _cf.CopyStream(e, target);
        }

        public IMailItem OpenEmbeddedMessage(int index)
        {
            var (info, bag) = AttachmentsRaw[index];
            if (!(bag.Raw(0x3701, out var type) is CompoundFile.Entry e) || type != 0x000D || !e.IsStorage)
                throw new InvalidOperationException("Not an embedded message");
            return new MsgMailItem(_cf, e);
        }

        public void Dispose()
        {
            if (_ownsFile) _cf.Dispose();
        }
    }
}
