using System;
using System.IO;
using System.Text;
using System.Xml;
using PstBrowser.Core.Text;

namespace PstBrowser.Core.Extraction
{
    /// <summary>Decoding of plain-text files and XML text.</summary>
    internal static class PlainText
    {
        public static string Decode(byte[] d)
        {
            if (d.Length >= 3 && d[0] == 0xEF && d[1] == 0xBB && d[2] == 0xBF) return Encoding.UTF8.GetString(d, 3, d.Length - 3);
            if (d.Length >= 2 && d[0] == 0xFF && d[1] == 0xFE) return Encoding.Unicode.GetString(d, 2, d.Length - 2);
            if (d.Length >= 2 && d[0] == 0xFE && d[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(d, 2, d.Length - 2);
            if (LooksUtf16(d)) return Encoding.Unicode.GetString(d);
            try { return new UTF8Encoding(false, true).GetString(d); }
            catch (DecoderFallbackException) { return (Charsets.TryGet(1252) ?? Encoding.Latin1).GetString(d); }
        }

        /// <summary>UTF-16LE text without BOM: ASCII text has a zero byte every other byte.</summary>
        private static bool LooksUtf16(byte[] d)
        {
            int n = Math.Min(d.Length, 400) & ~1;
            if (n < 8) return false;
            int zeros = 0;
            for (int i = 1; i < n; i += 2) if (d[i] == 0) zeros++;
            return zeros > n / 2 * 0.6;
        }

        public static XmlReaderSettings SafeXml() => new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            CheckCharacters = false,
        };

        /// <summary>Text nodes of an XML document, one per line; falls back to the raw text when the XML is not well formed.</summary>
        public static void ExtractXml(byte[] data, TextSink sink)
        {
            try
            {
                using var r = XmlReader.Create(new MemoryStream(data), SafeXml());
                while (r.Read() && !sink.Full)
                {
                    if (r.NodeType == XmlNodeType.Text || r.NodeType == XmlNodeType.CDATA)
                    {
                        sink.Append(r.Value);
                        sink.Append('\n');
                    }
                }
            }
            catch (XmlException)
            {
                sink.Append(Decode(data));
            }
        }
    }
}
