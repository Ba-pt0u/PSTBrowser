using System;
using PstBrowser.Core.Extraction;

namespace PstBrowser.App
{
    /// <summary>
    /// Entry point. <c>PstBrowser.exe --extract-worker</c> runs the attachment text-extraction worker (no window): the indexer
    /// starts it as a separate process so that a damaged document can only cost that process.
    /// </summary>
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            if (ExtractWorker.IsWorkerInvocation(args)) return ExtractWorker.Run();
            var app = new App();
            app.InitializeComponent();
            return app.Run();
        }
    }
}
