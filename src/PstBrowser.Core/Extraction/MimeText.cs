using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using PstBrowser.Core.Text;

namespace PstBrowser.Core.Extraction
{
    /// <summary>Minimal MIME reader for .eml attachments: headers, text and HTML parts, and nested attachments.</summary>
    internal static class MimeText
    {
        public static void Extract(byte[] data, int depth, ExtractContext ctx)
        {
            Part(Encoding.Latin1.GetString(data), depth, ctx, top: true);
        }

        private sealed class Headers
        {
            private readonly Dictionary<string, string> _h = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public string this[string name] => _h.TryGetValue(name, out var v) ? v : null;
            public void Set(string name, string value) { if (!_h.ContainsKey(name)) _h[name] = value; }
        }

        private static (Headers headers, string body) Split(string raw)
        {
            var h = new Headers();
            int end = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal), sep = 4;
            int end2 = raw.IndexOf("\n\n", StringComparison.Ordinal);
            if (end < 0 || (end2 >= 0 && end2 < end)) { end = end2; sep = 2; }
            string head = end < 0 ? raw : raw.Substring(0, end);
            string body = end < 0 ? "" : raw.Substring(end + sep);
            string name = null; var value = new StringBuilder();
            void Flush() { if (name != null) h.Set(name, value.ToString().Trim()); name = null; value.Clear(); }
            foreach (var line in head.Split('\n'))
            {
                var l = line.TrimEnd('\r');
                if (l.Length > 0 && (l[0] == ' ' || l[0] == '\t')) { value.Append(' ').Append(l.Trim()); continue; }
                Flush();
                int c = l.IndexOf(':');
                if (c > 0) { name = l.Substring(0, c).Trim(); value.Append(l.Substring(c + 1).Trim()); }
            }
            Flush();
            return (h, body);
        }

        private static void Part(string raw, int depth, ExtractContext ctx, bool top = false)
        {
            if (ctx.Sink.Full || depth > ctx.Limits.MaxDepth) return;
            var (h, body) = Split(raw);
            if (top)
                foreach (var n in new[] { "Subject", "From", "To", "Cc", "Date" })
                    if (h[n] != null) ctx.Sink.Append(DecodeWords(h[n]) + "\n");
            string ctype = (h["Content-Type"] ?? "text/plain").ToLowerInvariant();
            string main = ctype.Split(';')[0].Trim();
            if (main.StartsWith("multipart/"))
            {
                string boundary = Param(h["Content-Type"], "boundary");
                if (string.IsNullOrEmpty(boundary)) return;
                foreach (var part in body.Split(new[] { "--" + boundary }, StringSplitOptions.None))
                {
                    if (part.StartsWith("--")) break; // closing delimiter
                    if (part.Trim().Length == 0) continue;
                    Part(part.TrimStart('\r', '\n'), depth, ctx);
                }
                return;
            }
            var bytes = DecodeTransfer(body, h["Content-Transfer-Encoding"]);
            string disp = h["Content-Disposition"] ?? "";
            string fileName = DecodeWords(Param(disp, "filename") ?? Param(h["Content-Type"], "name"));
            if (main == "message/rfc822") { Part(Encoding.Latin1.GetString(bytes), depth + 1, ctx, top: true); return; }
            bool attachment = disp.StartsWith("attachment", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrEmpty(fileName);
            if ((main == "text/plain" || main == "text/html") && !attachment)
            {
                var enc = Charsets.TryGet(Param(h["Content-Type"], "charset")) ?? Encoding.UTF8;
                var text = enc.GetString(bytes);
                ctx.Sink.Append(main == "text/html" ? HtmlText.ToText(text) : text);
                ctx.Sink.Append('\n');
                return;
            }
            if (!string.IsNullOrEmpty(fileName)) { ctx.Charge(bytes.Length); TextExtractor.ExtractNested(fileName, bytes, depth + 1, ctx); }
        }

        private static string Param(string header, string name)
        {
            if (header == null) return null;
            var m = Regex.Match(header, @"(?:^|[;\s])" + name + @"\*?\s*=\s*(?:""([^""]*)""|([^;\s]+))", RegexOptions.IgnoreCase);
            return m.Success ? (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) : null;
        }

        private static byte[] DecodeTransfer(string body, string encoding)
        {
            switch ((encoding ?? "").Trim().ToLowerInvariant())
            {
                case "base64":
                    try { return Convert.FromBase64String(Regex.Replace(body, @"\s+", "")); } catch (FormatException) { return Array.Empty<byte>(); }
                case "quoted-printable":
                    return DecodeQuotedPrintable(body);
                default:
                    return Encoding.Latin1.GetBytes(body);
            }
        }

        private static byte[] DecodeQuotedPrintable(string s)
        {
            var ms = new MemoryStream(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '=' && i + 1 < s.Length)
                {
                    if (s[i + 1] == '\r' || s[i + 1] == '\n') { i += s[i + 1] == '\r' && i + 2 < s.Length && s[i + 2] == '\n' ? 2 : 1; continue; }
                    if (i + 2 < s.Length && Uri.IsHexDigit(s[i + 1]) && Uri.IsHexDigit(s[i + 2])) { ms.WriteByte(Convert.ToByte(s.Substring(i + 1, 2), 16)); i += 2; continue; }
                }
                ms.WriteByte((byte)c);
            }
            return ms.ToArray();
        }

        /// <summary>RFC 2047 encoded words in headers (=?charset?B?...?=).</summary>
        internal static string DecodeWords(string s)
        {
            if (string.IsNullOrEmpty(s) || !s.Contains("=?")) return s;
            return Regex.Replace(s, @"=\?([^?]+)\?([bBqQ])\?([^?]*)\?=\s*", m =>
            {
                try
                {
                    var enc = Charsets.TryGet(m.Groups[1].Value) ?? Encoding.UTF8;
                    byte[] b = char.ToUpperInvariant(m.Groups[2].Value[0]) == 'B'
                        ? Convert.FromBase64String(m.Groups[3].Value)
                        : DecodeQuotedPrintable(m.Groups[3].Value.Replace('_', ' '));
                    return enc.GetString(b);
                }
                catch { return m.Value; }
            });
        }
    }
}
