using System;
using System.Collections.Generic;
using System.IO;

namespace PstBrowser.Core.Mail
{
    public enum RecipientKind { To = 1, Cc = 2, Bcc = 3 }

    public sealed class RecipientInfo
    {
        public RecipientKind Kind { get; set; }
        public string Name { get; set; }
        public string Email { get; set; }

        public override string ToString()
        {
            if (string.IsNullOrEmpty(Email) || string.Equals(Name, Email, StringComparison.OrdinalIgnoreCase))
                return Name ?? Email ?? "";
            if (string.IsNullOrEmpty(Name)) return Email;
            return $"{Name} <{Email}>";
        }
    }

    public sealed class AttachmentInfo
    {
        public int Index { get; set; }
        public string FileName { get; set; }
        public long Size { get; set; }
        public bool IsEmbeddedMessage { get; set; }
        public string ContentId { get; set; }
        public string MimeType { get; set; }
        public bool IsHidden { get; set; }
        /// <summary>True when the attachment is an image referenced by the HTML body (cid:).</summary>
        public bool IsInline { get; set; }
    }

    /// <summary>
    /// Format-independent view of an e-mail (or any Outlook item) read from a PST/OST or an MSG file.
    /// Implementations are not thread-safe.
    /// </summary>
    public interface IMailItem
    {
        string Subject { get; }
        string SenderName { get; }
        string SenderEmail { get; }
        /// <summary>Delivery time, or submit time when not delivered (sent items, drafts).</summary>
        DateTime? Date { get; }
        DateTime? SentDate { get; }
        long Size { get; }
        string MessageClass { get; }
        string InternetMessageId { get; }
        string InReplyTo { get; }
        /// <summary>0 low, 1 normal, 2 high</summary>
        int Importance { get; }
        bool HasAttachmentsFlag { get; }
        string TransportHeaders { get; }
        IReadOnlyList<RecipientInfo> Recipients { get; }

        string PlainBody { get; }
        string HtmlBody { get; }
        /// <summary>Decompressed RTF body, if any.</summary>
        string RtfBody { get; }

        IReadOnlyList<AttachmentInfo> Attachments { get; }
        void SaveAttachment(int index, Stream target);
        /// <summary>Opens an attachment that is itself an e-mail (embedded message).</summary>
        IMailItem OpenEmbeddedMessage(int index);
    }
}
