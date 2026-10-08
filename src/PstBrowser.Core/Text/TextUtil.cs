using System;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace PstBrowser.Core.Text
{
    public static class Charsets
    {
        static Charsets()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        public static void EnsureRegistered() { /* triggers static ctor */ }

        public static Encoding TryGet(int codepage)
        {
            EnsureRegistered();
            if (codepage <= 0) return null;
            try { return Encoding.GetEncoding(codepage); } catch { return null; }
        }

        public static Encoding TryGet(string name)
        {
            EnsureRegistered();
            if (string.IsNullOrWhiteSpace(name)) return null;
            name = name.Trim().Trim('"', '\'');
            try { return Encoding.GetEncoding(name); } catch { return null; }
        }

        private static readonly Regex MetaCharset = new Regex(@"<meta[^>]+charset\s*=\s*[""']?\s*([A-Za-z0-9_\-:.]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Decodes HTML bytes: BOM, then declared code page, then &lt;meta charset&gt;, then UTF-8 validity, then Windows-1252.</summary>
        public static string DecodeHtml(byte[] bytes, int? codepage)
        {
            if (bytes == null) return null;
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);

            Encoding enc = codepage.HasValue ? TryGet(codepage.Value) : null;
            if (enc == null)
            {
                var head = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 4096));
                var m = MetaCharset.Match(head);
                if (m.Success) enc = TryGet(m.Groups[1].Value);
            }
            if (enc == null)
                enc = IsValidUtf8(bytes) ? Encoding.UTF8 : (TryGet(1252) ?? Encoding.Latin1);
            return enc.GetString(bytes);
        }

        public static bool IsValidUtf8(byte[] b)
        {
            try
            {
                new UTF8Encoding(false, true).GetCharCount(b);
                return true;
            }
            catch (DecoderFallbackException) { return false; }
        }
    }

    public static class HtmlText
    {
        private static readonly RegexOptions O = RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled;
        private static readonly Regex Comments = new Regex(@"<!--.*?-->", O);
        private static readonly Regex Blocks = new Regex(@"<(script|style|head|title|xml|o:p)\b[^>]*>.*?</\1\s*>", O);
        private static readonly Regex LineBreaks = new Regex(@"<\s*(br|/p|/div|/tr|/li|/h[1-6]|/table|/blockquote)\b[^>]*>", O);
        private static readonly Regex Cells = new Regex(@"<\s*/t[dh]\s*>", O);
        private static readonly Regex Tags = new Regex(@"<[^>]*>", O);
        private static readonly Regex Spaces = new Regex(@"[ \t ]+", RegexOptions.Compiled);
        private static readonly Regex ManyLines = new Regex(@"(\s*\n){3,}", RegexOptions.Compiled);

        /// <summary>Crude but robust HTML to plain text conversion, for indexing and previews.</summary>
        public static string ToText(string html, int maxChars = 2_000_000)
        {
            if (string.IsNullOrEmpty(html)) return html;
            if (html.Length > maxChars * 2) html = html.Substring(0, maxChars * 2);
            var s = Comments.Replace(html, " ");
            s = Blocks.Replace(s, " ");
            s = LineBreaks.Replace(s, "\n");
            s = Cells.Replace(s, " \t");
            s = Tags.Replace(s, " ");
            s = WebUtility.HtmlDecode(s);
            s = Spaces.Replace(s, " ");
            s = s.Replace("\r", "");
            s = ManyLines.Replace(s, "\n\n");
            s = s.Trim();
            return s.Length > maxChars ? s.Substring(0, maxChars) : s;
        }

        public static string Escape(string s) => WebUtility.HtmlEncode(s ?? "");
    }
}
