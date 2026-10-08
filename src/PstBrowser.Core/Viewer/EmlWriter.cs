using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using PstBrowser.Core.Mail;
using PstBrowser.Core.Text;

namespace PstBrowser.Core.Viewer
{
    /// <summary>Writes an IMailItem as a MIME (.eml) message, including attachments and embedded messages.</summary>
    public static class EmlWriter
    {
        public static void Write(IMailItem m, Stream output)
        {
            var w = new StreamWriter(output, new UTF8Encoding(false)) { NewLine = "\r\n" };
            WriteMessage(m, w, 0);
            w.Flush();
        }

        private static void WriteMessage(IMailItem m, StreamWriter w, int depth)
        {
            string Addr(string name, string email)
            {
                if (string.IsNullOrEmpty(email)) return EncodeWord(name ?? "");
                return string.IsNullOrEmpty(name) ? $"<{email}>" : $"{EncodeWord(name)} <{email}>";
            }
            if (!string.IsNullOrEmpty(m.SenderName) || !string.IsNullOrEmpty(m.SenderEmail))
                w.WriteLine("From: " + Addr(m.SenderName, m.SenderEmail));
            var rec = m.Recipients;
            var to = rec.Where(r => r.Kind == RecipientKind.To).Select(r => Addr(r.Name, r.Email)).ToList();
            var cc = rec.Where(r => r.Kind == RecipientKind.Cc).Select(r => Addr(r.Name, r.Email)).ToList();
            var bcc = rec.Where(r => r.Kind == RecipientKind.Bcc).Select(r => Addr(r.Name, r.Email)).ToList();
            if (to.Count > 0) w.WriteLine("To: " + string.Join(",\r\n ", to));
            if (cc.Count > 0) w.WriteLine("Cc: " + string.Join(",\r\n ", cc));
            if (bcc.Count > 0) w.WriteLine("Bcc: " + string.Join(",\r\n ", bcc));
            w.WriteLine("Subject: " + EncodeWord(m.Subject ?? ""));
            var d = m.SentDate ?? m.Date;
            if (d.HasValue) w.WriteLine("Date: " + d.Value.ToUniversalTime().ToString("ddd, dd MMM yyyy HH:mm:ss +0000", CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(m.InternetMessageId)) w.WriteLine("Message-ID: " + m.InternetMessageId);
            if (!string.IsNullOrEmpty(m.InReplyTo)) w.WriteLine("In-Reply-To: " + m.InReplyTo);
            w.WriteLine("X-PstBrowser-Message-Class: " + (m.MessageClass ?? ""));
            w.WriteLine("MIME-Version: 1.0");

            string boundaryMixed = "=_pstbrowser_mixed_" + depth + "_" + Guid.NewGuid().ToString("N");
            string boundaryAlt = "=_pstbrowser_alt_" + depth + "_" + Guid.NewGuid().ToString("N");
            w.WriteLine($"Content-Type: multipart/mixed; boundary=\"{boundaryMixed}\"");
            w.WriteLine();
            w.WriteLine("This is a multi-part message in MIME format.");
            w.WriteLine("--" + boundaryMixed);
            w.WriteLine($"Content-Type: multipart/alternative; boundary=\"{boundaryAlt}\"");
            w.WriteLine();

            string html = null, text = null;
            try { html = m.HtmlBody; } catch { }
            if (string.IsNullOrWhiteSpace(html)) { try { html = RtfConverter.ToHtml(m.RtfBody); } catch { } }
            try { text = m.PlainBody; } catch { }
            if (string.IsNullOrWhiteSpace(text)) text = !string.IsNullOrWhiteSpace(html) ? HtmlText.ToText(html) : RtfConverter.ToText(m.RtfBody) ?? "";

            WriteTextPart(w, boundaryAlt, "text/plain", text);
            if (!string.IsNullOrWhiteSpace(html)) WriteTextPart(w, boundaryAlt, "text/html", html);
            w.WriteLine("--" + boundaryAlt + "--");
            w.WriteLine();

            foreach (var a in m.Attachments)
            {
                w.WriteLine("--" + boundaryMixed);
                if (a.IsEmbeddedMessage)
                {
                    w.WriteLine("Content-Type: message/rfc822");
                    w.WriteLine("Content-Disposition: attachment; filename=\"" + SafeFileName(a.FileName) + ".eml\"");
                    w.WriteLine();
                    try { WriteMessage(m.OpenEmbeddedMessage(a.Index), w, depth + 1); }
                    catch { w.WriteLine("(message joint illisible)"); }
                    w.WriteLine();
                    continue;
                }
                string mime = string.IsNullOrEmpty(a.MimeType) ? "application/octet-stream" : a.MimeType;
                w.WriteLine($"Content-Type: {mime}; name=\"{EncodeParam(a.FileName)}\"");
                w.WriteLine("Content-Transfer-Encoding: base64");
                w.WriteLine((a.IsInline && !string.IsNullOrEmpty(a.ContentId) ? "Content-Disposition: inline" : "Content-Disposition: attachment") +
                            $"; filename=\"{EncodeParam(a.FileName)}\"");
                if (!string.IsNullOrEmpty(a.ContentId)) w.WriteLine($"Content-ID: <{a.ContentId}>");
                w.WriteLine();
                w.Flush();
                using (var ms = new MemoryStream())
                {
                    try { m.SaveAttachment(a.Index, ms); } catch { }
                    WriteBase64(w, ms.ToArray());
                }
            }
            w.WriteLine("--" + boundaryMixed + "--");
        }

        private static void WriteTextPart(StreamWriter w, string boundary, string type, string content)
        {
            w.WriteLine("--" + boundary);
            w.WriteLine($"Content-Type: {type}; charset=\"utf-8\"");
            w.WriteLine("Content-Transfer-Encoding: base64");
            w.WriteLine();
            WriteBase64(w, Encoding.UTF8.GetBytes(content ?? ""));
        }

        private static void WriteBase64(StreamWriter w, byte[] data)
        {
            var b64 = Convert.ToBase64String(data);
            for (int i = 0; i < b64.Length; i += 76)
                w.WriteLine(b64.Substring(i, Math.Min(76, b64.Length - i)));
        }

        private static string EncodeWord(string s)
        {
            if (s.All(c => c >= 32 && c < 127)) return s;
            return "=?utf-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(s)) + "?=";
        }

        private static string EncodeParam(string s) => EncodeWord(s ?? "").Replace("\"", "'");

        public static string SafeFileName(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "message";
            var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }).ToHashSet();
            var sb = new StringBuilder();
            foreach (var c in s.Trim()) sb.Append(invalid.Contains(c) || char.IsControl(c) ? '_' : c);
            var r = sb.ToString().Trim(' ', '.');
            if (r.Length > 120) r = r.Substring(0, 120);
            return r.Length == 0 ? "message" : r;
        }
    }
}
