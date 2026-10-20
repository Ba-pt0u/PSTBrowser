using System;

namespace PstBrowser.Reader.MacOS
{
    /// <summary>Secure reader on macOS: WKWebView, see <see cref="SecureReader"/>.</summary>
    public sealed class WKWebViewReader : SecureReader
    {
        public WKWebViewReader(ReaderProtections protections = ReaderProtections.All) : base(protections) => throw new PlatformNotSupportedException();
        public override void Navigate(string uri) { }
        public override string Title => null;
        protected override bool IsLoaded => false;
        public override void Pump(TimeSpan duration) { }
        public override void Dispose() { }
    }
}
