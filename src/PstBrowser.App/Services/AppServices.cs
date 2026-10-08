using PstBrowser.Core.Index;
using PstBrowser.Core.Search;
using PstBrowser.Core.Viewer;

namespace PstBrowser.App.Services
{
    /// <summary>Engine objects of the currently open case workspace.</summary>
    public static class AppServices
    {
        public static Workspace Workspace { get; private set; }
        public static MessageService Messages { get; private set; }
        public static SearchService Search { get; private set; }
        public static Indexer Indexer { get; private set; }

        public static void Open(Workspace ws, IndexerOptions options)
        {
            Close();
            Workspace = ws;
            Messages = new MessageService(ws);
            Search = new SearchService(ws);
            Indexer = new Indexer(ws, options);
        }

        public static void Close()
        {
            try { Indexer?.StopAndWait().Wait(5000); } catch { }
            try { Messages?.Dispose(); } catch { }
            try { Search?.Dispose(); } catch { }
            try { Workspace?.Dispose(); } catch { }
            Indexer = null; Messages = null; Search = null; Workspace = null;
        }
    }
}
