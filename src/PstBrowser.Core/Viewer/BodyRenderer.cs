using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using PstBrowser.Core.Mail;
using PstBrowser.Core.Text;

namespace PstBrowser.Core.Viewer
{
    /// <summary>
    /// Produces the HTML document shown in the reading pane.
    /// Security model (defence in depth): the host disables scripts and blocks every network request;
    /// this renderer additionally removes active content, neutralises remote resources and adds a strict CSP.
    /// </summary>
    public static class BodyRenderer
    {
        public const string Csp = "default-src 'none'; img-src https://" + MessageService.VirtualHost + " data:; style-src 'unsafe-inline'; font-src data:; form-action 'none'; base-uri 'none'";

        private const RegexOptions O = RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled;
        private static readonly Regex ActiveBlocks = new Regex(@"<(script|iframe|object|embed|applet|frameset|noscript)\b.*?</\1\s*>", O);
        private static readonly Regex ActiveSingles = new Regex(@"<(script|iframe|object|embed|applet|frame|base|link|meta)\b[^>]*>", O);
        private static readonly Regex EventAttrs = new Regex(@"\son[a-z]+\s*=\s*(""[^""]*""|'[^']*'|[^\s>]+)", O);
        private static readonly Regex JsUrls = new Regex(@"(href|src|action)\s*=\s*([""']?)\s*(javascript|vbscript|data:text/html)[^""'\s>]*\2", O);
        private static readonly Regex RemoteSrc = new Regex(@"\b(src|background)\s*=\s*([""'])\s*(https?:|//|file:|ftp:)[^""']*\2", O);
        private static readonly Regex RemoteCssUrl = new Regex(@"url\(\s*([""']?)\s*(https?:|//|file:)[^)]*\)", O);
        private static readonly Regex CidRef = new Regex(@"cid:([^""'\s)>]+)", O);
        private static readonly Regex HeadOpen = new Regex(@"<head\b[^>]*>", O);
        private static readonly Regex HtmlOpen = new Regex(@"<html\b[^>]*>", O);

        private const string BaseStyle =
            "html,body{background:#fff;color:#1f1f1f;}" +
            "body{font-family:'Segoe UI',Calibri,Arial,sans-serif;font-size:14px;margin:12px 16px;word-wrap:break-word;}" +
            "mark.pl-hit{background:#ffe066;color:inherit;padding:0 1px;border-radius:2px;}" +
            ".pl-text{white-space:pre-wrap;font-family:Calibri,'Segoe UI',sans-serif;font-size:15px;line-height:1.4;}" +
            ".pl-note{color:#666;font-style:italic;font-family:'Segoe UI',sans-serif;font-size:12px;margin-bottom:8px;}" +
            "img[data-pl-blocked]{outline:1px dashed #bbb;min-width:16px;min-height:16px;}";

        public static string Render(IMailItem m, MessageRef mref, IList<string> terms, Dictionary<string, int> inlineImages, out string format)
        {
            string html = null;
            string note = null;
            try { html = m.HtmlBody; } catch { }
            if (!string.IsNullOrWhiteSpace(html)) format = "HTML";
            else
            {
                string rtf = null;
                try { rtf = m.RtfBody; } catch { }
                html = RtfConverter.ToHtml(rtf);
                if (!string.IsNullOrWhiteSpace(html)) format = "HTML (RTF)";
                else
                {
                    string text = null;
                    try { text = m.PlainBody; } catch { }
                    format = "Texte";
                    if (string.IsNullOrWhiteSpace(text) && !string.IsNullOrWhiteSpace(rtf))
                    {
                        text = RtfConverter.ToText(rtf);
                        format = "RTF (converti en texte)";
                        note = "Message au format RTF : la mise en forme d'origine n'est pas restituée.";
                    }
                    html = "<div class=\"pl-text\">" + WebUtility.HtmlEncode(text ?? "") + "</div>";
                    if (string.IsNullOrWhiteSpace(text)) html = "<div class=\"pl-note\">(Aucun contenu texte)</div>";
                }
            }

            html = Sanitize(html, out bool blockedRemote);
            html = MapInlineImages(html, m, mref, inlineImages);
            if (terms != null && terms.Count > 0) html = Highlight(html, terms);

            var head = new StringBuilder();
            head.Append("<meta charset=\"utf-8\"><meta http-equiv=\"Content-Security-Policy\" content=\"").Append(Csp).Append("\">");
            head.Append("<style>").Append(BaseStyle).Append("</style>");
            string banner = "";
            if (blockedRemote) banner += "<div class=\"pl-note\">Images distantes bloquées (aucune connexion réseau n'est effectuée).</div>";
            if (note != null) banner += "<div class=\"pl-note\">" + WebUtility.HtmlEncode(note) + "</div>";

            var hm = HeadOpen.Match(html);
            if (hm.Success)
                html = html.Insert(hm.Index + hm.Length, head.ToString());
            else
            {
                var htm = HtmlOpen.Match(html);
                if (htm.Success) html = html.Insert(htm.Index + htm.Length, "<head>" + head + "</head>");
                else html = "<!DOCTYPE html><html><head>" + head + "</head><body>" + html + "</body></html>";
            }
            if (banner.Length > 0)
            {
                var bm = Regex.Match(html, @"<body\b[^>]*>", RegexOptions.IgnoreCase);
                html = bm.Success ? html.Insert(bm.Index + bm.Length, banner) : banner + html;
            }
            return html;
        }

        public static string Sanitize(string html, out bool blockedRemote)
        {
            var s = ActiveBlocks.Replace(html, "");
            s = ActiveSingles.Replace(s, "");
            s = EventAttrs.Replace(s, " ");
            s = JsUrls.Replace(s, "$1=\"#\"");
            bool blocked = false;
            s = RemoteSrc.Replace(s, mm => { blocked = true; return $"{mm.Groups[1].Value}=\"\" data-pl-blocked=\"1\""; });
            s = RemoteCssUrl.Replace(s, mm => { blocked = true; return "url()"; });
            blockedRemote = blocked;
            return s;
        }

        private static string MapInlineImages(string html, IMailItem m, MessageRef mref, Dictionary<string, int> inlineImages)
        {
            IReadOnlyList<AttachmentInfo> atts;
            try { atts = m.Attachments; } catch { return html; }
            if (atts.Count == 0) return html;
            var byCid = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in atts)
            {
                if (!string.IsNullOrEmpty(a.ContentId) && !byCid.ContainsKey(a.ContentId)) byCid[a.ContentId] = a.Index;
                if (!string.IsNullOrEmpty(a.FileName) && !byCid.ContainsKey(a.FileName)) byCid[a.FileName] = a.Index;
            }
            string Url(int idx)
            {
                var token = mref.Key + "/" + idx;
                inlineImages[token] = idx;
                return $"https://{MessageService.VirtualHost}/inline/{token}";
            }
            html = CidRef.Replace(html, mm =>
            {
                var cid = WebUtility.UrlDecode(mm.Groups[1].Value);
                if (!byCid.TryGetValue(cid, out int idx))
                {
                    var at = cid.IndexOf('@');
                    if (at <= 0 || !byCid.TryGetValue(cid.Substring(0, at), out idx)) return "";
                }
                return Url(idx);
            });
            // Images referenced by a bare file name (Content-Location)
            return RelativeSrc.Replace(html, mm =>
            {
                var name = WebUtility.UrlDecode(mm.Groups[2].Value);
                return byCid.TryGetValue(name, out int idx) ? $"src={mm.Groups[1].Value}{Url(idx)}{mm.Groups[1].Value}" : mm.Value;
            });
        }

        private static readonly Regex RelativeSrc = new Regex(@"\bsrc\s*=\s*([""'])([^""':/\\]+)\1", O);

        // ------------------------------------------------------------------ highlighting

        private static readonly Dictionary<char, string> Variants = new Dictionary<char, string>
        {
            ['a'] = "aàâäáãåæ", ['c'] = "cç", ['e'] = "eéèêë", ['i'] = "iîïíì", ['o'] = "oôöóòõœø",
            ['u'] = "uùûüú", ['y'] = "yÿý", ['n'] = "nñ",
        };

        public static Regex BuildTermsRegex(IEnumerable<string> terms)
        {
            var parts = new List<string>();
            foreach (var raw in terms.Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(t => t.Length))
            {
                bool prefix = raw.EndsWith("*");
                var t = RemoveDiacritics(raw.TrimEnd('*')).ToLowerInvariant();
                // The FTS tokenizer splits on punctuation: highlight each alphanumeric run
                foreach (var w in Regex.Split(t, @"[^\p{L}\p{N}]+").Where(w => w.Length > 0))
                {
                    var sb = new StringBuilder();
                    foreach (var c in w)
                        sb.Append(Variants.TryGetValue(c, out var v) ? "[" + v + "]" : Regex.Escape(c.ToString()));
                    parts.Add(@"(?<![\p{L}\p{N}])" + sb + (prefix ? @"[\p{L}\p{N}]*" : @"(?![\p{L}\p{N}])"));
                }
            }
            if (parts.Count == 0) return null;
            return new Regex(string.Join("|", parts), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        public static string RemoveDiacritics(string s)
        {
            var norm = s.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(norm.Length);
            foreach (var c in norm)
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
            return sb.ToString().Normalize(NormalizationForm.FormC);
        }

        private static readonly Regex TagSplit = new Regex(@"(<[^>]*>)", RegexOptions.Singleline | RegexOptions.Compiled);

        public static string Highlight(string html, IEnumerable<string> terms)
        {
            var rx = BuildTermsRegex(terms);
            if (rx == null) return html;
            var parts = TagSplit.Split(html);
            var sb = new StringBuilder(html.Length + 256);
            bool skip = false; // inside <style>, <title>, <head>
            int hits = 0;
            foreach (var p in parts)
            {
                if (p.Length == 0) continue;
                if (p[0] == '<')
                {
                    var lower = p.ToLowerInvariant();
                    if (lower.StartsWith("<style") || lower.StartsWith("<title") || lower.StartsWith("<head")) skip = true;
                    else if (lower.StartsWith("</style") || lower.StartsWith("</title") || lower.StartsWith("</head")) skip = false;
                    sb.Append(p);
                    continue;
                }
                if (skip || hits > 5000) { sb.Append(p); continue; }
                var seg = p;
                sb.Append(rx.Replace(seg, mm =>
                {
                    if (mm.Index > 0 && seg[mm.Index - 1] == '&') return mm.Value; // inside an entity
                    hits++;
                    return "<mark class=\"pl-hit\">" + mm.Value + "</mark>";
                }));
            }
            return sb.ToString();
        }
    }
}
