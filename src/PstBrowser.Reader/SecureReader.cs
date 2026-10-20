using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace PstBrowser.Reader
{
    /// <summary>Protections of the view (all of them in the application; the tests check each one on its own).</summary>
    [Flags]
    public enum ReaderProtections
    {
        None = 0,
        /// <summary>WebKit content blocker: every URL refused except pstb: and data:.</summary>
        ContentBlocker = 1,
        /// <summary>All connections through a proxy that does not exist.</summary>
        DeadProxy = 2,
        /// <summary>JavaScript, media, WebGL, WebRTC… disabled.</summary>
        NoActiveContent = 4,
        All = ContentBlocker | DeadProxy | NoActiveContent,
    }

    /// <summary>Bytes served to the page for one of its URLs (null: not found).</summary>
    public sealed class ReaderResource
    {
        public byte[] Data { get; set; }
        public string MimeType { get; set; }
    }

    /// <summary>
    /// Displays an e-mail body with the web engine of the system (WKWebView on macOS, WebKitGTK on Linux) so that
    /// nothing leaves the computer, whatever the message contains. Protection layers, each sufficient on its own:
    ///  1. the HTML is sanitized by PstBrowser.Core (scripts, event handlers, remote URLs removed; strict CSP);
    ///  2. the page and its images are served from memory under pstb://pstbrowser.invalid/ (a scheme of our own);
    ///  3. a content blocker refuses every other URL before any connection (http, https, ws, ftp, file…);
    ///  4. the network goes through a proxy that does not exist (connection refused, no DNS lookup);
    ///  5. JavaScript is disabled, the profile is ephemeral (no cache, cookie or storage on disk), plugins, media,
    ///     WebGL, WebRTC and DNS prefetching are off;
    ///  6. navigation is refused: a click on a link only raises <see cref="ExternalLinkRequested"/>.
    /// All members must be called on the thread that created the reader (the main thread on macOS).
    /// </summary>
    public abstract class SecureReader : IDisposable
    {
        /// <summary>Custom scheme and host of the content served from memory.</summary>
        public const string Scheme = "pstb";
        public const string Host = "pstbrowser.invalid";
        public const string Origin = Scheme + "://" + Host + "/";

        /// <summary>
        /// WebKit content blocker (same JSON format for WKContentRuleList and WebKitUserContentFilter): everything is
        /// blocked, except our own scheme and data: URLs (embedded images).
        /// </summary>
        public const string ContentRules =
            "[{\"trigger\":{\"url-filter\":\".*\"},\"action\":{\"type\":\"block\"}}," +
            "{\"trigger\":{\"url-filter\":\"^pstb:\"},\"action\":{\"type\":\"ignore-previous-rules\"}}," +
            "{\"trigger\":{\"url-filter\":\"^data:\"},\"action\":{\"type\":\"ignore-previous-rules\"}}]";

        /// <summary>Proxy that refuses every connection (discard port of the loopback address, never listened on).</summary>
        public const string DeadProxyHost = "127.0.0.1";
        public const int DeadProxyPort = 9;

        private string _html = "";
        private int _counter;

        /// <summary>All protections in the application; fewer only in the tests (control run, one layer at a time).</summary>
        public ReaderProtections Protections { get; }

        /// <summary>Provides the bytes of pstb://pstbrowser.invalid/&lt;path&gt; for paths other than the page itself.</summary>
        public Func<string, ReaderResource> ResourceProvider { get; set; }

        /// <summary>The user clicked a link: the page does not navigate, the application decides what to do.</summary>
        public event Action<string> ExternalLinkRequested;

        /// <summary>Diagnostics: navigations refused and resources requested by the page.</summary>
        public List<string> RefusedNavigations { get; } = new List<string>();
        public List<string> RequestedResources { get; } = new List<string>();
        public int ServedResources { get; protected set; }
        public string LastError { get; protected set; }

        protected SecureReader(ReaderProtections protections) { Protections = protections; }

        /// <summary>Creates a reader whose view is not shown on screen (tests, rendering without a window).</summary>
        public static SecureReader CreateOffscreen(ReaderProtections protections = ReaderProtections.All)
        {
            if (OperatingSystem.IsLinux()) return new Linux.WebKitGtkReader(protections);
            if (OperatingSystem.IsMacOS()) return new MacOS.WKWebViewReader(protections);
            throw new PlatformNotSupportedException("Sous Windows, les messages sont affichés par WebView2 (application PST Browser).");
        }

        /// <summary>Displays a document (the HTML produced by PstBrowser.Core, or any HTML).</summary>
        public void ShowHtml(string html)
        {
            _html = AdaptUrls(html ?? "");
            Navigate($"{Origin}msg/{++_counter}");
        }

        /// <summary>Asks the view to go to an address, as a link or a redirection would: only our own pages are allowed.</summary>
        public abstract void Navigate(string uri);

        /// <summary>Title of the document (the tests check that no script could change it).</summary>
        public abstract string Title { get; }

        /// <summary>Runs the event loop until the current page is loaded, then for <paramref name="settle"/> more (late loads).</summary>
        public bool WaitForLoad(TimeSpan timeout, TimeSpan settle)
        {
            var sw = Stopwatch.StartNew();
            while (!IsLoaded && sw.Elapsed < timeout) Pump(TimeSpan.FromMilliseconds(20));
            if (!IsLoaded) return false;
            Pump(settle);
            return true;
        }

        /// <summary>Processes the native events for a while.</summary>
        public abstract void Pump(TimeSpan duration);

        protected abstract bool IsLoaded { get; }

        public abstract void Dispose();

        /// <summary>The view asks for one of our URLs: the page, or a resource of the provider.</summary>
        protected ReaderResource Resolve(string uri)
        {
            RequestedResources.Add(uri);
            if (!uri.StartsWith(Origin, StringComparison.OrdinalIgnoreCase)) return null;
            var path = uri.Substring(Origin.Length);
            if (path.StartsWith("msg/", StringComparison.Ordinal))
                return new ReaderResource { Data = Encoding.UTF8.GetBytes(_html), MimeType = "text/html" };
            try
            {
                var r = ResourceProvider?.Invoke(path);
                if (r?.Data != null) ServedResources++;
                return r?.Data == null ? null : r;
            }
            catch { return null; }
        }

        /// <summary>Navigation requested by the page or the user: true if allowed (our own pages only).</summary>
        protected bool AllowNavigation(string uri, bool userClick)
        {
            if (uri != null && uri.StartsWith(Origin + "msg/", StringComparison.OrdinalIgnoreCase)) return true;
            if (uri == "about:blank") return true;
            RefusedNavigations.Add(uri);
            if (userClick && !string.IsNullOrEmpty(uri)) ExternalLinkRequested?.Invoke(uri);
            return false;
        }

        /// <summary>
        /// PstBrowser.Core serves inline images under https://pstbrowser.invalid/ (WebView2 intercepts https);
        /// WebKit only lets an application serve its own scheme, so the same addresses are rewritten to pstb://.
        /// </summary>
        public static string AdaptUrls(string html) => html.Replace("https://" + Host + "/", Origin).Replace("https://" + Host, Scheme + "://" + Host);
    }
}
