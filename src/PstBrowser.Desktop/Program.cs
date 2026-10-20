using System;
using Avalonia;
using PstBrowser.Core.Extraction;

namespace PstBrowser.Desktop
{
    /// <summary>
    /// Entry point. <c>PstBrowser --extract-worker</c> runs the attachment text-extraction worker (no window): the indexer
    /// starts it as a separate process so that a damaged document can only cost that process.
    /// </summary>
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            if (ExtractWorker.IsWorkerInvocation(args)) return ExtractWorker.Run();
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                // Linux: the interface runs on the GLib loop, which also runs the WebKitGTK reader
                .With(new X11PlatformOptions { UseGLibMainLoop = true })
                .LogToTrace();
    }
}
