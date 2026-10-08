using System;
using System.Collections.Generic;
using System.Text;

namespace PstBrowser.Core.Text
{
    /// <summary>
    /// Converts Outlook RTF bodies:
    ///  - RTF with encapsulated HTML (\fromhtml1, see [MS-OXRTFEX]) is de-encapsulated back to the original HTML;
    ///  - any other RTF (including \fromtext) is converted to plain text.
    /// This is not a full RTF renderer: formatting of native RTF mails is lost, the text is preserved.
    /// </summary>
    public static class RtfConverter
    {
        public static bool IsEncapsulatedHtml(string rtf)
            => rtf != null && rtf.IndexOf(@"\fromhtml", 0, Math.Min(rtf.Length, 2048), StringComparison.Ordinal) >= 0;

        /// <summary>Returns the HTML if the RTF encapsulates HTML, otherwise null.</summary>
        public static string ToHtml(string rtf)
            => IsEncapsulatedHtml(rtf) ? Convert(rtf, htmlMode: true) : null;

        public static string ToText(string rtf)
        {
            if (string.IsNullOrEmpty(rtf)) return rtf;
            if (IsEncapsulatedHtml(rtf)) return HtmlText.ToText(Convert(rtf, htmlMode: true));
            return Convert(rtf, htmlMode: false);
        }

        private sealed class State
        {
            public bool Skip;          // inside an ignored destination
            public bool HtmlRtf;       // inside \htmlrtf ... \htmlrtf0 (RTF-only content, not part of the HTML)
            public bool HtmlTag;       // inside {\*\htmltag ...} (pure HTML)
            public int UcSkip = 1;     // \ucN
            public State Clone() => (State)MemberwiseClone();
        }

        // Destinations whose content is never text
        private static readonly HashSet<string> SkipDestinations = new HashSet<string>(StringComparer.Ordinal)
        {
            "fonttbl","colortbl","stylesheet","info","pict","object","header","footer","headerl","headerr","headerf",
            "footerl","footerr","footerf","listtable","listoverridetable","revtbl","rsidtbl","generator","xmlnstbl",
            "themedata","colorschememapping","latentstyles","datastore","fldinst","filetbl","pgdsctbl","mmathPr",
            "mhtmltag","bkmkstart","bkmkend","pn","pntext","fldrslt_ignore"
        };

        private static string Convert(string rtf, bool htmlMode)
        {
            var output = new StringBuilder(rtf.Length / 2);
            var pendingBytes = new List<byte>();
            Encoding enc = Charsets.TryGet(1252) ?? Encoding.Latin1;

            var stack = new Stack<State>();
            var st = new State();
            int skipChars = 0; // chars to skip after \uN
            int i = 0, n = rtf.Length;

            void FlushBytes()
            {
                if (pendingBytes.Count > 0)
                {
                    output.Append(enc.GetString(pendingBytes.ToArray()));
                    pendingBytes.Clear();
                }
            }
            bool Visible() => !st.Skip && (htmlMode ? (st.HtmlTag || !st.HtmlRtf) : true);
            void Emit(string s) { if (Visible()) { FlushBytes(); output.Append(s); } }
            void EmitChar(char c) { if (Visible()) { FlushBytes(); output.Append(c); } }

            bool destinationPending = false; // after "{\*"

            while (i < n)
            {
                char c = rtf[i];
                if (c == '{')
                {
                    stack.Push(st);
                    st = st.Clone();
                    destinationPending = false;
                    i++;
                    // Is this the start of a destination group?
                    continue;
                }
                if (c == '}')
                {
                    FlushBytes();
                    st = stack.Count > 0 ? stack.Pop() : new State();
                    destinationPending = false;
                    i++;
                    continue;
                }
                if (c == '\\')
                {
                    if (i + 1 >= n) break;
                    char d = rtf[i + 1];
                    // Control symbols
                    if (d == '\\' || d == '{' || d == '}')
                    {
                        if (skipChars > 0) skipChars--; else EmitChar(d);
                        i += 2; continue;
                    }
                    if (d == '*') { destinationPending = true; i += 2; continue; }
                    if (d == '\'')
                    {
                        if (i + 3 < n)
                        {
                            string hex = rtf.Substring(i + 2, 2);
                            i += 4;
                            if (skipChars > 0) { skipChars--; continue; }
                            if (Visible() && byte.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var b))
                                pendingBytes.Add(b);
                            continue;
                        }
                        i = n; continue;
                    }
                    if (d == '~') { EmitChar(' '); i += 2; continue; }
                    if (d == '-' || d == '_') { i += 2; continue; }
                    if (d == '\r' || d == '\n') { Emit("\n"); i += 2; continue; }
                    if (!char.IsLetter(d)) { i += 2; continue; }

                    // Control word
                    int j = i + 1;
                    while (j < n && char.IsLetter(rtf[j])) j++;
                    string word = rtf.Substring(i + 1, j - i - 1);
                    int? param = null;
                    int k = j;
                    if (k < n && (rtf[k] == '-' || char.IsDigit(rtf[k])))
                    {
                        int s0 = k; k++;
                        while (k < n && char.IsDigit(rtf[k])) k++;
                        if (int.TryParse(rtf.AsSpan(s0, k - s0), out var pv)) param = pv;
                    }
                    if (k < n && rtf[k] == ' ') k++; // delimiter space is part of the control word
                    i = k;

                    if (destinationPending)
                    {
                        destinationPending = false;
                        if (word == "htmltag") { FlushBytes(); st.HtmlTag = true; st.Skip = false; continue; }
                        // any other unknown \* destination is ignorable
                        FlushBytes(); st.Skip = true; continue;
                    }

                    switch (word)
                    {
                        case "ansicpg":
                            if (param.HasValue) { FlushBytes(); enc = Charsets.TryGet(param.Value) ?? enc; }
                            break;
                        case "htmlrtf":
                            FlushBytes();
                            st.HtmlRtf = param != 0;
                            break;
                        case "par": case "line": case "sect": case "page":
                            Emit(htmlMode ? "\r\n" : "\n");
                            break;
                        case "row":
                            if (!htmlMode) Emit("\n");
                            break;
                        case "cell":
                            if (!htmlMode) Emit("\t");
                            break;
                        case "tab": Emit("\t"); break;
                        case "emdash": Emit("—"); break;
                        case "endash": Emit("–"); break;
                        case "bullet": Emit("•"); break;
                        case "lquote": Emit("‘"); break;
                        case "rquote": Emit("’"); break;
                        case "ldblquote": Emit("“"); break;
                        case "rdblquote": Emit("”"); break;
                        case "uc": st.UcSkip = param ?? 1; break;
                        case "u":
                            if (param.HasValue)
                            {
                                int v = param.Value; if (v < 0) v += 65536;
                                EmitChar((char)v);
                                skipChars = st.UcSkip;
                            }
                            break;
                        default:
                            if (SkipDestinations.Contains(word)) { FlushBytes(); st.Skip = true; }
                            break;
                    }
                    continue;
                }
                if (c == '\r' || c == '\n') { i++; continue; }
                if (skipChars > 0) { skipChars--; i++; continue; }
                destinationPending = false;
                if (Visible())
                {
                    // Plain ASCII text characters
                    FlushBytes();
                    output.Append(c);
                }
                i++;
            }
            FlushBytes();
            var result = output.ToString();
            if (!htmlMode) result = result.Replace("\u0000", "").Trim();
            return result;
        }
    }
}
