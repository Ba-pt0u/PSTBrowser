using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;

namespace PstBrowser.Core.Extraction
{
    /// <summary>ZIP containers: Office Open XML, OpenDocument, and plain archives (walked recursively with anti-bomb limits).</summary>
    internal static class ZipText
    {
        public static void Extract(byte[] data, int depth, ExtractContext ctx)
        {
            ZipArchive zip;
            try { zip = new ZipArchive(new MemoryStream(data, false), ZipArchiveMode.Read, false, Encoding.UTF8); }
            catch (InvalidDataException) { throw new UnsupportedFormatException("Archive illisible"); }
            using (zip)
            {
                if (zip.Entries.Count > ctx.Limits.MaxZipEntries) throw new InvalidDataException("Archive avec trop d'entrées");
                var byName = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in zip.Entries) byName[e.FullName.Replace('\\', '/')] = e;

                if (byName.ContainsKey("word/document.xml")) { Word(byName, ctx); return; }
                if (byName.ContainsKey("xl/workbook.xml")) { Excel(byName, ctx); return; }
                if (byName.ContainsKey("ppt/presentation.xml")) { PowerPoint(byName, ctx); return; }
                if (byName.ContainsKey("content.xml") && byName.ContainsKey("mimetype")) { OpenDocument(byName, ctx); return; }
                Generic(zip, depth, ctx);
            }
        }

        private static Stream Open(ZipArchiveEntry e, ExtractContext ctx)
        {
            if (e.Length > ctx.Limits.MaxEntryBytes) throw new InvalidDataException("Entrée d'archive trop volumineuse");
            return new BoundedReadStream(e.Open(), ctx.Limits.MaxEntryBytes, ctx);
        }

        private static IEnumerable<ZipArchiveEntry> Parts(Dictionary<string, ZipArchiveEntry> byName, string prefix, string suffix = ".xml")
            => byName.Where(kv => kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && kv.Key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && kv.Key.IndexOf('/', prefix.Length) < 0)
                     .OrderBy(kv => NaturalKey(kv.Key), StringComparer.Ordinal).Select(kv => kv.Value);

        /// <summary>"slide10.xml" after "slide2.xml".</summary>
        private static string NaturalKey(string s)
        {
            var sb = new StringBuilder();
            int i = 0;
            while (i < s.Length)
            {
                if (char.IsDigit(s[i]))
                {
                    int j = i; while (j < s.Length && char.IsDigit(s[j])) j++;
                    sb.Append(s.Substring(i, j - i).PadLeft(8, '0'));
                    i = j;
                }
                else sb.Append(s[i++]);
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------ Office Open XML

        private static void Word(Dictionary<string, ZipArchiveEntry> byName, ExtractContext ctx)
        {
            var parts = new List<ZipArchiveEntry> { byName["word/document.xml"] };
            parts.AddRange(Parts(byName, "word/header"));
            parts.AddRange(Parts(byName, "word/footer"));
            foreach (var n in new[] { "word/footnotes.xml", "word/endnotes.xml", "word/comments.xml" })
                if (byName.TryGetValue(n, out var e)) parts.Add(e);
            foreach (var part in parts)
            {
                if (ctx.Sink.Full) break;
                using var s = Open(part, ctx);
                XmlRuns(s, ctx.Sink);
            }
        }

        private static void PowerPoint(Dictionary<string, ZipArchiveEntry> byName, ExtractContext ctx)
        {
            foreach (var part in Parts(byName, "ppt/slides/slide").Concat(Parts(byName, "ppt/notesSlides/notesSlide")))
            {
                if (ctx.Sink.Full) break;
                using var s = Open(part, ctx);
                XmlRuns(s, ctx.Sink);
            }
        }

        /// <summary>Text of WordprocessingML / DrawingML: text runs, paragraph ends become new lines.</summary>
        private static void XmlRuns(Stream s, TextSink sink)
        {
            using var r = XmlReader.Create(s, PlainText.SafeXml());
            while (r.Read() && !sink.Full)
            {
                switch (r.NodeType)
                {
                    case XmlNodeType.Element:
                        switch (r.LocalName)
                        {
                            case "instrText": case "delText": case "fldChar": if (!r.IsEmptyElement) r.Skip(); break;
                            case "tab": sink.Append('\t'); break;
                            case "br": case "cr": sink.Append('\n'); break;
                        }
                        break;
                    case XmlNodeType.Text:
                    case XmlNodeType.CDATA:
                    case XmlNodeType.SignificantWhitespace:
                        sink.Append(r.Value);
                        break;
                    case XmlNodeType.EndElement:
                        if (r.LocalName == "p") sink.Append('\n');
                        break;
                }
            }
        }

        private static void Excel(Dictionary<string, ZipArchiveEntry> byName, ExtractContext ctx)
        {
            var shared = new List<string>();
            if (byName.TryGetValue("xl/sharedStrings.xml", out var ssEntry))
            {
                using var s = Open(ssEntry, ctx);
                using var r = XmlReader.Create(s, PlainText.SafeXml());
                long chars = 0;
                var cur = new StringBuilder();
                while (r.Read())
                {
                    if (r.NodeType == XmlNodeType.Element && r.LocalName == "si") cur.Clear();
                    else if (r.NodeType == XmlNodeType.Element && r.LocalName == "rPh") r.Skip(); // phonetic runs
                    else if (r.NodeType == XmlNodeType.Text || r.NodeType == XmlNodeType.SignificantWhitespace) cur.Append(r.Value);
                    else if (r.NodeType == XmlNodeType.EndElement && r.LocalName == "si")
                    {
                        shared.Add(cur.ToString());
                        chars += cur.Length;
                        if (chars > ctx.Limits.MaxChars * 4L) break;
                    }
                }
            }
            if (byName.TryGetValue("xl/workbook.xml", out var wb))
            {
                using var s = Open(wb, ctx);
                using var r = XmlReader.Create(s, PlainText.SafeXml());
                while (r.Read())
                    if (r.NodeType == XmlNodeType.Element && r.LocalName == "sheet")
                    {
                        var name = r.GetAttribute("name");
                        if (!string.IsNullOrEmpty(name)) { ctx.Sink.Append(name); ctx.Sink.Append('\n'); }
                    }
            }
            foreach (var part in Parts(byName, "xl/worksheets/sheet"))
            {
                if (ctx.Sink.Full) break;
                using var s = Open(part, ctx);
                using var r = XmlReader.Create(s, PlainText.SafeXml());
                string type = null; bool inValue = false, inInline = false, firstCell = true;
                while (r.Read() && !ctx.Sink.Full)
                {
                    if (r.NodeType == XmlNodeType.Element)
                    {
                        switch (r.LocalName)
                        {
                            case "row": firstCell = true; break;
                            case "c": type = r.GetAttribute("t"); if (!firstCell) ctx.Sink.Append('\t'); firstCell = false; break;
                            case "v": inValue = true; break;
                            case "is": inInline = true; break;
                            case "rPh": r.Skip(); break;
                        }
                    }
                    else if (r.NodeType == XmlNodeType.Text || r.NodeType == XmlNodeType.SignificantWhitespace)
                    {
                        if (inValue)
                        {
                            if (type == "s" && int.TryParse(r.Value, out var idx)) ctx.Sink.Append(idx >= 0 && idx < shared.Count ? shared[idx] : "");
                            else ctx.Sink.Append(r.Value);
                        }
                        else if (inInline) ctx.Sink.Append(r.Value);
                    }
                    else if (r.NodeType == XmlNodeType.EndElement)
                    {
                        if (r.LocalName == "v") inValue = false;
                        else if (r.LocalName == "is") inInline = false;
                        else if (r.LocalName == "row") ctx.Sink.Append('\n');
                    }
                }
            }
        }

        // ------------------------------------------------------------ OpenDocument

        private static void OpenDocument(Dictionary<string, ZipArchiveEntry> byName, ExtractContext ctx)
        {
            foreach (var n in new[] { "content.xml", "styles.xml" })
            {
                if (!byName.TryGetValue(n, out var e) || ctx.Sink.Full) continue;
                using var s = Open(e, ctx);
                using var r = XmlReader.Create(s, PlainText.SafeXml());
                while (r.Read() && !ctx.Sink.Full)
                {
                    switch (r.NodeType)
                    {
                        case XmlNodeType.Element:
                            if (r.LocalName == "tab") ctx.Sink.Append('\t');
                            else if (r.LocalName == "line-break") ctx.Sink.Append('\n');
                            else if (r.LocalName == "s") ctx.Sink.Append(' ');
                            break;
                        case XmlNodeType.Text:
                        case XmlNodeType.CDATA:
                        case XmlNodeType.SignificantWhitespace:
                            ctx.Sink.Append(r.Value);
                            break;
                        case XmlNodeType.EndElement:
                            if (r.LocalName == "p" || r.LocalName == "h" || r.LocalName == "table-row") ctx.Sink.Append('\n');
                            else if (r.LocalName == "table-cell") ctx.Sink.Append('\t');
                            break;
                    }
                }
            }
        }

        // ------------------------------------------------------------ plain archives

        private static void Generic(ZipArchive zip, int depth, ExtractContext ctx)
        {
            foreach (var e in zip.Entries)
            {
                if (ctx.Sink.Full) break;
                if (string.IsNullOrEmpty(e.Name)) continue; // directory
                string name = e.FullName.Replace('\\', '/');
                ctx.Sink.Append("\n--- " + name + " ---\n");
                if (depth + 1 >= ctx.Limits.MaxDepth && IsContainerName(e.Name)) { continue; }
                try
                {
                    if (e.Length > ctx.Limits.MaxEntryBytes) { ctx.SkippedEntries++; continue; }
                    byte[] bytes;
                    using (var s = Open(e, ctx))
                    using (var ms = new MemoryStream())
                    {
                        s.CopyTo(ms);
                        bytes = ms.ToArray();
                    }
                    if (bytes.Length == 0 || !TextExtractor.IsSupported(e.Name, bytes)) continue;
                    try { TextExtractor.ExtractInto(e.Name, bytes, depth + 1, ctx); }
                    catch (EncryptedException) { ctx.EntryErrors++; }
                    catch (UnsupportedFormatException) { }
                    catch (InvalidDataException) when (ctx.TotalBytes > ctx.Limits.MaxTotalBytes) { throw; }
                    catch (Exception) { ctx.EntryErrors++; }
                }
                catch (InvalidDataException) when (ctx.TotalBytes > ctx.Limits.MaxTotalBytes) { throw; }
                catch (Exception) { ctx.EntryErrors++; } // entry protected with a password or damaged
            }
        }

        private static bool IsContainerName(string name)
        {
            var ext = TextExtractor.ExtensionOf(name);
            return ext == "zip" || ext == "eml" || ext == "msg";
        }
    }
}
