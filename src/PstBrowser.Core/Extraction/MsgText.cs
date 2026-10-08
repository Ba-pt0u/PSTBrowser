using System;
using System.IO;
using System.Linq;
using PstBrowser.Core.Mail;
using PstBrowser.Core.Text;

namespace PstBrowser.Core.Extraction
{
    /// <summary>Text of a .msg file kept as an attachment (or inside an archive): headers, body, then its own attachments.</summary>
    internal static class MsgText
    {
        public static void Extract(byte[] data, int depth, ExtractContext ctx)
        {
            using var m = new MsgMailItem(new MemoryStream(data, false));
            AppendMessage(m, depth, ctx);
        }

        internal static void AppendMessage(IMailItem m, int depth, ExtractContext ctx)
        {
            var sink = ctx.Sink;
            sink.Append(m.Subject + "\n" + m.SenderName + " " + m.SenderEmail + "\n");
            sink.Append(string.Join("; ", m.Recipients.Select(r => r.ToString())) + "\n");
            sink.Append(Index.Indexer.GetBodyText(m));
            sink.Append('\n');
            if (depth + 1 >= ctx.Limits.MaxDepth) return;
            foreach (var a in m.Attachments)
            {
                if (sink.Full) break;
                try
                {
                    if (a.IsEmbeddedMessage)
                    {
                        sink.Append("\n--- " + a.FileName + " ---\n");
                        AppendMessage(m.OpenEmbeddedMessage(a.Index), depth + 1, ctx);
                    }
                    else
                    {
                        if (a.Size > ctx.Limits.MaxEntryBytes) continue;
                        using var ms = new MemoryStream();
                        m.SaveAttachment(a.Index, ms);
                        ctx.Charge(ms.Length);
                        TextExtractor.ExtractNested(a.FileName, ms.ToArray(), depth + 1, ctx);
                    }
                }
                catch (InvalidDataException) when (ctx.TotalBytes > ctx.Limits.MaxTotalBytes) { throw; }
                catch (Exception) { ctx.EntryErrors++; }
            }
        }
    }
}
