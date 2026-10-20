using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using static PstBrowser.Reader.MacOS.ObjC;

namespace PstBrowser.Reader.MacOS
{
    /// <summary>Secure reader on macOS: WKWebView (WebKit of the system), see <see cref="SecureReader"/>.</summary>
    public sealed unsafe class WKWebViewReader : SecureReader
    {
        private const long PolicyCancel = 0, PolicyAllow = 1, NavigationLinkActivated = 0;
        private const string DelegateClass = "PstBrowserReaderDelegate";

        private static readonly Dictionary<IntPtr, WKWebViewReader> Instances = new Dictionary<IntPtr, WKWebViewReader>();
        private static IntPtr _runLoopMode, _compiledBlock;
        private static IntPtr _pendingRuleList, _pendingError;
        private static bool _compileDone;

        private IntPtr _delegate, _config, _view, _window, _ruleList;
        private bool _loaded;
        private string _rulesDir;

        /// <summary>The WKWebView (an NSView, to be embedded in a window by the application).</summary>
        public IntPtr View => _view;

        public WKWebViewReader(ReaderProtections protections = ReaderProtections.All) : base(protections)
        {
            NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
            NativeLibrary.Load("/System/Library/Frameworks/WebKit.framework/WebKit");
            var pool = objc_autoreleasePoolPush();
            try
            {
                var app = Msg(Class("NSApplication"), "sharedApplication");
                SendVoid(app, Sel("setActivationPolicy:"), 2L); // prohibited: no Dock icon for a hidden view

                _delegate = New(DefineDelegateClass());
                Instances[_delegate] = this;

                _config = New("WKWebViewConfiguration");
                // ephemeral profile: nothing is written on disk (cache, cookies, local storage)
                var store = Msg(Class("WKWebsiteDataStore"), "nonPersistentDataStore");
                if (protections.HasFlag(ReaderProtections.DeadProxy)) SetDeadProxy(store);
                Msg(_config, "setWebsiteDataStore:", store);
                Send(_config, Sel("setURLSchemeHandler:forURLScheme:"), _delegate, NSString(Scheme));
                ApplySettings(protections.HasFlag(ReaderProtections.NoActiveContent));
                if (protections.HasFlag(ReaderProtections.ContentBlocker)) InstallContentBlocker();

                _view = Send(Msg(Class("WKWebView"), "alloc"), Sel("initWithFrame:configuration:"), new CGRect(0, 0, 900, 700), _config);
                Msg(_view, "setNavigationDelegate:", _delegate);
                Msg(_view, "setUIDelegate:", _delegate);
                // a window that is never shown: the page is laid out and loaded as in the application
                _window = Send(Msg(Class("NSWindow"), "alloc"), Sel("initWithContentRect:styleMask:backing:defer:"),
                    new CGRect(0, 0, 900, 700), 0UL, 2UL, false);
                Msg(_window, "setContentView:", _view);
            }
            finally { objc_autoreleasePoolPop(pool); }
        }

        private void ApplySettings(bool noActiveContent)
        {
            var prefs = Msg(_config, "preferences");
            SendVoid(prefs, Sel("setJavaScriptCanOpenWindowsAutomatically:"), false);
            if (noActiveContent) SendVoid(prefs, Sel("setJavaScriptEnabled:"), false);
            // Safe Browsing would contact external servers for every address
            if (Responds(prefs, "setFraudulentWebsiteWarningEnabled:")) SendVoid(prefs, Sel("setFraudulentWebsiteWarningEnabled:"), false);
            if (Responds(prefs, "setElementFullscreenEnabled:")) SendVoid(prefs, Sel("setElementFullscreenEnabled:"), false);
            var page = Msg(_config, "defaultWebpagePreferences");
            if (Responds(page, "setAllowsContentJavaScript:")) SendVoid(page, Sel("setAllowsContentJavaScript:"), !noActiveContent);
            SendVoid(_config, Sel("setMediaTypesRequiringUserActionForPlayback:"), -1L); // all media
            if (Responds(_config, "setAllowsAirPlayForMediaPlayback:")) SendVoid(_config, Sel("setAllowsAirPlayForMediaPlayback:"), false);
        }

        // ------------------------------------------------------------------ dead proxy (macOS 14+)

        [DllImport("/System/Library/Frameworks/Network.framework/Network")] private static extern IntPtr nw_endpoint_create_host(string host, string port);
        [DllImport("/System/Library/Frameworks/Network.framework/Network")] private static extern IntPtr nw_proxy_config_create_socksv5(IntPtr endpoint);
        [DllImport("/System/Library/Frameworks/Network.framework/Network")] private static extern void nw_proxy_config_set_failover_allowed(IntPtr config, [MarshalAs(UnmanagedType.I1)] bool allowed);

        private void SetDeadProxy(IntPtr store)
        {
            if (!Responds(store, "setProxyConfigurations:")) { LastError = "proxy non disponible avant macOS 14"; return; }
            var endpoint = nw_endpoint_create_host(DeadProxyHost, DeadProxyPort.ToString());
            // SOCKS: every TCP connection goes through it (an HTTP CONNECT proxy may only be used for some of them)
            var proxy = nw_proxy_config_create_socksv5(endpoint);
            // by default a failing proxy is bypassed (direct connection): never here
            nw_proxy_config_set_failover_allowed(proxy, false);
            Msg(store, "setProxyConfigurations:", Send(Class("NSArray"), Sel("arrayWithObject:"), proxy));
        }

        // ------------------------------------------------------------------ content blocker

        private void InstallContentBlocker()
        {
            _rulesDir = Path.Combine(Path.GetTempPath(), "pstbrowser-rules-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_rulesDir);
            var url = Send(Class("NSURL"), Sel("fileURLWithPath:"), NSString(_rulesDir));
            var store = Send(Class("WKContentRuleListStore"), Sel("storeWithURL:"), url);
            if (_compiledBlock == IntPtr.Zero)
                _compiledBlock = Block((IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&OnRulesCompiled, IntPtr.Zero);
            _compileDone = false;
            _pendingRuleList = _pendingError = IntPtr.Zero;
            Send(store, Sel("compileContentRuleListForIdentifier:encodedContentRuleList:completionHandler:"), NSString("pstbrowser"), NSString(ContentRules), _compiledBlock);
            var sw = Stopwatch.StartNew();
            while (!_compileDone && sw.Elapsed < TimeSpan.FromSeconds(30)) RunLoop(0.05);
            if (_pendingRuleList == IntPtr.Zero)
                throw new InvalidOperationException("Le filtre de contenu WebKit n'a pas pu être compilé : " +
                    (_pendingError != IntPtr.Zero ? FromNSString(Msg(_pendingError, "localizedDescription")) : "délai dépassé"));
            _ruleList = _pendingRuleList;
            Msg(Msg(_config, "userContentController"), "addContentRuleList:", _ruleList);
        }

        [UnmanagedCallersOnly]
        private static void OnRulesCompiled(IntPtr block, IntPtr ruleList, IntPtr error)
        {
            _pendingRuleList = ruleList == IntPtr.Zero ? IntPtr.Zero : Msg(ruleList, "retain");
            _pendingError = error == IntPtr.Zero ? IntPtr.Zero : Msg(error, "retain");
            _compileDone = true;
        }

        // ------------------------------------------------------------------ SecureReader

        public override void Navigate(string uri)
        {
            _loaded = false;
            var pool = objc_autoreleasePoolPush();
            try { Msg(_view, "loadRequest:", Send(Class("NSURLRequest"), Sel("requestWithURL:"), NSUrl(uri))); }
            finally { objc_autoreleasePoolPop(pool); }
        }

        public override string Title
        {
            get
            {
                var pool = objc_autoreleasePoolPush();
                try { return FromNSString(Msg(_view, "title")); }
                finally { objc_autoreleasePoolPop(pool); }
            }
        }

        protected override bool IsLoaded => _loaded;

        public override void Pump(TimeSpan duration)
        {
            var sw = Stopwatch.StartNew();
            do RunLoop(0.02); while (sw.Elapsed < duration);
        }

        [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
        private static extern int CFRunLoopRunInMode(IntPtr mode, double seconds, [MarshalAs(UnmanagedType.I1)] bool returnAfterSourceHandled);

        private static void RunLoop(double seconds)
        {
            if (_runLoopMode == IntPtr.Zero)
            {
                var cf = NativeLibrary.Load("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation");
                _runLoopMode = Marshal.ReadIntPtr(NativeLibrary.GetExport(cf, "kCFRunLoopDefaultMode"));
            }
            var pool = objc_autoreleasePoolPush();
            try { CFRunLoopRunInMode(_runLoopMode, seconds, false); }
            finally { objc_autoreleasePoolPop(pool); }
        }

        public override void Dispose()
        {
            if (_view == IntPtr.Zero) return;
            var pool = objc_autoreleasePoolPush();
            try
            {
                Msg(_view, "stopLoading");
                Msg(_view, "setNavigationDelegate:", IntPtr.Zero);
                Msg(_view, "setUIDelegate:", IntPtr.Zero);
                Msg(_window, "setContentView:", IntPtr.Zero);
                Msg(_window, "close");
                foreach (var o in new[] { _view, _config, _ruleList }) if (o != IntPtr.Zero) Msg(o, "release");
            }
            finally { objc_autoreleasePoolPop(pool); }
            Pump(TimeSpan.FromMilliseconds(50));
            Instances.Remove(_delegate);
            Msg(_delegate, "release");
            _view = _window = _config = _ruleList = _delegate = IntPtr.Zero;
            if (_rulesDir != null) try { Directory.Delete(_rulesDir, true); } catch { }
        }

        // ------------------------------------------------------------------ delegate (navigation, UI, pstb: scheme)

        private static string DefineDelegateClass()
        {
            DefineClass(DelegateClass, "NSObject", new[] { "WKNavigationDelegate", "WKUIDelegate", "WKURLSchemeHandler" },
                ("webView:decidePolicyForNavigationAction:decisionHandler:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void>)&DecidePolicy, "v@:@@@?"),
                ("webView:didFinishNavigation:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, void>)&DidFinish, "v@:@@"),
                ("webView:didFailNavigation:withError:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void>)&DidFail, "v@:@@@"),
                ("webView:didFailProvisionalNavigation:withError:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void>)&DidFail, "v@:@@@"),
                ("webViewWebContentProcessDidTerminate:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&ProcessTerminated, "v@:@"),
                ("webView:createWebViewWithConfiguration:forNavigationAction:windowFeatures:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>)&CreateWebView, "@@:@@@@"),
                ("webView:startURLSchemeTask:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, void>)&StartSchemeTask, "v@:@@"),
                ("webView:stopURLSchemeTask:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, void>)&StopSchemeTask, "v@:@@"));
            return DelegateClass;
        }

        private static WKWebViewReader From(IntPtr self) => Instances.TryGetValue(self, out var r) ? r : null;

        [UnmanagedCallersOnly]
        private static void DecidePolicy(IntPtr self, IntPtr cmd, IntPtr webView, IntPtr action, IntPtr decisionHandler)
        {
            bool allow = false;
            try
            {
                var me = From(self);
                var uri = RequestUrl(Msg(action, "request"));
                bool click = SendLong(action, Sel("navigationType")) == NavigationLinkActivated;
                bool newWindow = Msg(action, "targetFrame") == IntPtr.Zero;
                allow = me != null && me.AllowNavigation(uri, click) && !newWindow;
            }
            catch { allow = false; }
            finally { InvokeBlock(decisionHandler, allow ? PolicyAllow : PolicyCancel); } // the decision must always be given
        }

        [UnmanagedCallersOnly]
        private static void DidFinish(IntPtr self, IntPtr cmd, IntPtr webView, IntPtr navigation)
        {
            var me = From(self);
            if (me != null) me._loaded = true;
        }

        [UnmanagedCallersOnly]
        private static void DidFail(IntPtr self, IntPtr cmd, IntPtr webView, IntPtr navigation, IntPtr error)
        {
            var me = From(self);
            if (me == null) return;
            me.LastError = FromNSString(Msg(error, "localizedDescription"));
            me._loaded = true;
        }

        [UnmanagedCallersOnly]
        private static void ProcessTerminated(IntPtr self, IntPtr cmd, IntPtr webView)
        {
            var me = From(self);
            if (me != null) { me.LastError = "processus d'affichage arrêté"; me._loaded = true; }
        }

        [UnmanagedCallersOnly]
        private static IntPtr CreateWebView(IntPtr self, IntPtr cmd, IntPtr webView, IntPtr configuration, IntPtr action, IntPtr features)
        {
            var me = From(self);
            me?.AllowNavigation(RequestUrl(Msg(action, "request")), SendLong(action, Sel("navigationType")) == NavigationLinkActivated);
            return IntPtr.Zero; // never a new window
        }

        [UnmanagedCallersOnly]
        private static void StartSchemeTask(IntPtr self, IntPtr cmd, IntPtr webView, IntPtr task)
        {
            var me = From(self);
            var request = Msg(task, "request");
            var url = Msg(request, "URL");
            ReaderResource res = null;
            try { res = me?.Resolve(FromNSString(Msg(url, "absoluteString"))); } catch { }
            if (res == null)
            {
                var error = Send(Class("NSError"), Sel("errorWithDomain:code:userInfo:"), NSString("pstbrowser"), 404L, IntPtr.Zero);
                Msg(task, "didFailWithError:", error);
                return;
            }
            var mime = res.MimeType ?? "application/octet-stream";
            var response = Send(Msg(Class("NSURLResponse"), "alloc"), Sel("initWithURL:MIMEType:expectedContentLength:textEncodingName:"),
                url, NSString(mime), res.Data.Length, mime.StartsWith("text/") ? NSString("utf-8") : IntPtr.Zero);
            Msg(task, "didReceiveResponse:", response);
            Msg(response, "release");
            Msg(task, "didReceiveData:", NSData(res.Data));
            Msg(task, "didFinish");
        }

        [UnmanagedCallersOnly]
        private static void StopSchemeTask(IntPtr self, IntPtr cmd, IntPtr webView, IntPtr task) { }
    }
}
