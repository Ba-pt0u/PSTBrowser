using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using PstBrowser.Core.Mail;
using PstBrowser.Core.Text;

namespace PstBrowser.Core.Extraction
{
    /// <summary>Outcome of the text extraction of one attachment. Codes are what the index stores.</summary>
    public static class AttachmentStatus
    {
        public const string Ok = "ok";
        public const string Empty = "empty";
        public const string Unsupported = "unsupported";
        public const string Encrypted = "encrypted";
        public const string TooLarge = "toolarge";
        public const string Error = "error";

        public static string Label(string status) => status switch
        {
            Ok => "texte extrait",
            Empty => "vide (aucun texte)",
            Unsupported => "non pris en charge",
            Encrypted => "chiffré",
            TooLarge => "trop gros",
            Error => "erreur",
            _ => status,
        };

        internal static byte ToByte(string status) => status switch
        {
            Ok => 0, Empty => 1, Unsupported => 2, Encrypted => 3, TooLarge => 4, _ => 5,
        };

        internal static string FromByte(byte b) => b switch
        {
            0 => Ok, 1 => Empty, 2 => Unsupported, 3 => Encrypted, 4 => TooLarge, _ => Error,
        };
    }

    public sealed class ExtractResult
    {
        public string Status { get; set; } = AttachmentStatus.Ok;
        public string Text { get; set; } = "";
        public string Error { get; set; }
    }

    /// <summary>Limits applied to one extraction (anti-bomb protections included).</summary>
    public sealed class ExtractLimits
    {
        /// <summary>Largest attachment that is read (50 MB).</summary>
        public long MaxFileBytes { get; set; } = 50L * 1024 * 1024;
        /// <summary>Largest amount of text kept per attachment.</summary>
        public int MaxChars { get; set; } = 4_000_000;
        /// <summary>Nesting depth of archives, messages in messages, and mails attached to mails.</summary>
        public int MaxDepth { get; set; } = 3;
        public int MaxZipEntries { get; set; } = 5000;
        /// <summary>Largest uncompressed size of one archive entry.</summary>
        public long MaxEntryBytes { get; set; } = 50L * 1024 * 1024;
        /// <summary>Largest total uncompressed size read from one attachment (all nesting levels).</summary>
        public long MaxTotalBytes { get; set; } = 200L * 1024 * 1024;
    }

    internal sealed class EncryptedException : Exception
    {
        public EncryptedException(string message = "Document protégé par un mot de passe") : base(message) { }
    }

    internal sealed class UnsupportedFormatException : Exception
    {
        public UnsupportedFormatException(string message = "Format non pris en charge") : base(message) { }
    }

    /// <summary>Bounded text accumulator shared by all the readers of one extraction.</summary>
    internal sealed class TextSink
    {
        private readonly StringBuilder _sb = new StringBuilder();
        public int Max { get; }
        public TextSink(int max) { Max = max; }
        public bool Full => _sb.Length >= Max;
        public int Length => _sb.Length;

        public void Append(string s)
        {
            if (string.IsNullOrEmpty(s) || Full) return;
            int room = Max - _sb.Length;
            _sb.Append(s, 0, Math.Min(room, s.Length));
        }

        public void Append(char c) { if (!Full) _sb.Append(c); }
        public override string ToString() => _sb.ToString();
    }

    internal sealed class ExtractContext
    {
        public ExtractLimits Limits;
        public TextSink Sink;
        public long TotalBytes;
        public int SkippedEntries;
        public int EntryErrors;
        public int ReadableParts;

        public void Charge(long bytes)
        {
            TotalBytes += bytes;
            if (TotalBytes > Limits.MaxTotalBytes) throw new InvalidDataException("Contenu décompressé trop volumineux (protection contre les archives piégées)");
        }
    }

    /// <summary>Stream that fails when more than <c>max</c> bytes are read from it (guards against lying archive headers).</summary>
    internal sealed class BoundedReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _max;
        private readonly ExtractContext _ctx;
        private long _read;

        public BoundedReadStream(Stream inner, long max, ExtractContext ctx) { _inner = inner; _max = max; _ctx = ctx; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = _inner.Read(buffer, offset, count);
            if (n > 0)
            {
                _read += n;
                if (_read > _max) throw new InvalidDataException("Entrée d'archive trop volumineuse");
                _ctx.Charge(n);
            }
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }

    /// <summary>
    /// Extracts the searchable text of an attachment: PDF, Office (OOXML and binary), OpenDocument, archives (recursively),
    /// mails, and plain-text formats. Runs inside the extraction worker process (see <see cref="ExtractWorker"/>).
    /// </summary>
    public static class TextExtractor
    {
        internal enum Format
        {
            Unknown, Pdf, Zip, Ole, Text, Html, Xml, Rtf, Eml, Msg,
        }

        private static readonly HashSet<string> TextExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "txt", "csv", "tsv", "ics", "vcf", "url", "log", "json", "md", "ini", "cfg", "conf", "yaml", "yml", "sql", "bat", "cmd", "ps1", "vbs", "js", "css", "properties", "reg", "lst", "asc", "nfo",
        };
        private static readonly HashSet<string> ZipExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "zip", "docx", "docm", "dotx", "dotm", "xlsx", "xlsm", "xltx", "xltm", "pptx", "pptm", "ppsx", "potx", "odt", "ods", "odp", "ott", "ots", "otp", "odg",
        };
        private static readonly HashSet<string> OleExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "doc", "dot", "xls", "xlt", "ppt", "pps", "pot",
        };

        public static string ExtensionOf(string name)
        {
            var ext = Path.GetExtension(name ?? "");
            return string.IsNullOrEmpty(ext) ? "" : ext.TrimStart('.').ToLowerInvariant();
        }

        /// <summary>Cheap check done by the indexer before shipping the bytes to the worker.</summary>
        public static bool IsSupported(string name, byte[] data) => Detect(name, data) != Format.Unknown;

        internal static Format Detect(string name, byte[] d)
        {
            string ext = ExtensionOf(name);
            if (d == null || d.Length == 0) return Format.Unknown;
            // Content first: files are often named wrongly (a .doc that is an RTF or HTML file…)
            if (d.Length >= 5 && d[0] == '%' && d[1] == 'P' && d[2] == 'D' && d[3] == 'F' && d[4] == '-') return Format.Pdf;
            if (d.Length >= 4 && d[0] == 'P' && d[1] == 'K' && (d[2] == 3 || d[2] == 5) && (d[3] == 4 || d[3] == 6)) return Format.Zip;
            if (d.Length >= 8 && BitConverter.ToUInt64(d, 0) == 0xE11AB1A1E011CFD0UL) return ext == "msg" ? Format.Msg : Format.Ole;
            if (StartsWithAscii(d, "{\\rtf")) return Format.Rtf;
            switch (ext)
            {
                case "pdf": return Format.Pdf;
                case "rtf": return Format.Rtf;
                case "eml": return Format.Eml;
                case "html": case "htm": case "xhtml": case "mht": case "mhtml": return Format.Html;
                case "xml": case "xsd": case "xsl": case "xslt": case "config": case "svg": case "rels": case "kml": return Format.Xml;
                case "msg": return Format.Unknown; // not a compound file: damaged
            }
            if (TextExtensions.Contains(ext)) return Format.Text;
            if (ZipExtensions.Contains(ext) || OleExtensions.Contains(ext)) return Format.Unknown; // wrong magic: damaged or unsupported variant
            return Format.Unknown;
        }

        private static bool StartsWithAscii(byte[] d, string prefix)
        {
            // tolerate a UTF-8 BOM or leading blanks
            int i = 0;
            while (i < d.Length && i < 8 && (d[i] <= 32 || d[i] >= 0xEF)) i++;
            if (d.Length - i < prefix.Length) return false;
            for (int k = 0; k < prefix.Length; k++) if (d[i + k] != prefix[k]) return false;
            return true;
        }

        /// <summary>Extracts the text of one attachment. Never throws: failures are reported in the result.</summary>
        public static ExtractResult Extract(string name, byte[] data, ExtractLimits limits = null)
        {
            limits ??= new ExtractLimits();
            var result = new ExtractResult();
            if (data == null || data.Length == 0) { result.Status = AttachmentStatus.Empty; return result; }
            if (data.LongLength > limits.MaxFileBytes) { result.Status = AttachmentStatus.TooLarge; return result; }
            var ctx = new ExtractContext { Limits = limits, Sink = new TextSink(limits.MaxChars) };
            try
            {
                Charsets.EnsureRegistered();
                if (!ExtractInto(name, data, 0, ctx, strictTop: true))
                {
                    result.Status = AttachmentStatus.Unsupported;
                    return result;
                }
                var text = Normalize(ctx.Sink.ToString());
                result.Text = text;
                if (text.Length == 0)
                {
                    // an archive none of whose entries could be read is most likely protected
                    result.Status = ctx.EntryErrors > 0 && ctx.ReadableParts == 0 ? AttachmentStatus.Encrypted : AttachmentStatus.Empty;
                }
            }
            catch (EncryptedException)
            {
                result.Status = AttachmentStatus.Encrypted;
                result.Text = "";
            }
            catch (UnsupportedFormatException)
            {
                result.Status = AttachmentStatus.Unsupported;
                result.Text = "";
            }
            catch (Exception ex)
            {
                result.Status = AttachmentStatus.Error;
                result.Error = ex.Message.Length > 300 ? ex.Message.Substring(0, 300) : ex.Message;
                result.Text = "";
            }
            return result;
        }

        /// <summary>Collapses runs of blanks and empty lines; removes control characters.</summary>
        internal static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            int newlines = 0; bool space = false;
            foreach (var c in s)
            {
                if (c == '\n' || c == '\r') { newlines++; space = false; if (newlines <= 2) sb.Append('\n'); continue; }
                if (c == ' ' || c == '\t' || c == ' ') { if (!space && newlines == 0) sb.Append(' '); else if (!space && sb.Length > 0 && sb[sb.Length - 1] != '\n') sb.Append(' '); space = true; continue; }
                if (char.IsControl(c)) continue;
                newlines = 0; space = false;
                sb.Append(c);
            }
            return sb.ToString().Trim();
        }

        /// <summary>Returns false when the format is not supported (only meaningful for the top-level call).</summary>
        internal static bool ExtractInto(string name, byte[] data, int depth, ExtractContext ctx, bool strictTop = false)
        {
            if (ctx.Sink.Full || depth > ctx.Limits.MaxDepth) return true;
            var fmt = Detect(name, data);
            switch (fmt)
            {
                case Format.Pdf: PdfText.Extract(data, ctx.Sink); break;
                case Format.Zip: ZipText.Extract(data, depth, ctx); break;
                case Format.Ole: OfficeBinary.Extract(data, ctx); break;
                case Format.Text: ctx.Sink.Append(PlainText.Decode(data)); break;
                case Format.Html: ctx.Sink.Append(HtmlText.ToText(Charsets.DecodeHtml(data, null) ?? PlainText.Decode(data))); break;
                case Format.Xml: PlainText.ExtractXml(data, ctx.Sink); break;
                case Format.Rtf: ctx.Sink.Append(RtfConverter.ToText(Encoding.Latin1.GetString(data))); break;
                case Format.Eml: MimeText.Extract(data, depth, ctx); break;
                case Format.Msg: MsgText.Extract(data, depth, ctx); break;
                default: return false;
            }
            ctx.ReadableParts++;
            return true;
        }

        /// <summary>Extracts a nested file (archive entry, mail attachment). Unsupported or damaged files are skipped.</summary>
        internal static void ExtractNested(string name, byte[] data, int depth, ExtractContext ctx)
        {
            if (depth >= ctx.Limits.MaxDepth || ctx.Sink.Full) return;
            if (data.Length == 0 || !IsSupported(name, data)) return;
            ctx.Sink.Append("\n--- " + name + " ---\n");
            try { ExtractInto(name, data, depth, ctx); }
            catch (EncryptedException) { ctx.EntryErrors++; }
            catch (UnsupportedFormatException) { }
            catch (InvalidDataException) when (ctx.TotalBytes > ctx.Limits.MaxTotalBytes) { throw; }
            catch (Exception) { ctx.EntryErrors++; }
        }
    }
}
