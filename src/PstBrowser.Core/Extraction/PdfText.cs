using System;
using System.Linq;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Exceptions;

namespace PstBrowser.Core.Extraction
{
    /// <summary>PDF text with PdfPig (Apache-2.0).</summary>
    internal static class PdfText
    {
        public static void Extract(byte[] data, TextSink sink)
        {
            try
            {
                using var doc = PdfDocument.Open(data, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
                foreach (var page in doc.GetPages())
                {
                    if (sink.Full) break;
                    sink.Append(string.Join(" ", page.GetWords().Select(w => w.Text)));
                    sink.Append('\n');
                }
            }
            catch (PdfDocumentEncryptedException)
            {
                throw new EncryptedException();
            }
        }
    }
}
