using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using static PstBrowser.Reader.Linux.Native;

namespace PstBrowser.Reader.Linux
{
    /// <summary>Secure reader on Linux: WebKitGTK (libwebkit2gtk-4.1, GTK 3), see <see cref="SecureReader"/>.</summary>
    public sealed unsafe class WebKitGtkReader : SecureReader
    {
        private const int LoadFinished = 3, PolicyNavigation = 0, PolicyNewWindow = 1, NavigationLinkClicked = 0;
        private const int CacheModelDocumentViewer = 0, ProxyModeCustom = 2;

        private readonly GCHandle _self;
        private IntPtr _context, _manager, _contentManager, _settings, _view, _window, _filter;
        private bool _loaded, _filterDone;
        private string _filterDir;

        /// <summary>The WebKitWebView widget (to be embedded in a window by the application).</summary>
        public IntPtr Widget => _view;

        public WebKitGtkReader(ReaderProtections protections = ReaderProtections.All) : base(protections)
        {
            if (gtk_init_check(IntPtr.Zero, IntPtr.Zero) == 0)
                throw new InvalidOperationException("GTK ne peut pas s'initialiser (aucun affichage disponible).");
            _self = GCHandle.Alloc(this);
            var me = GCHandle.ToIntPtr(_self);

            // ephemeral profile: nothing is written on disk (cache, cookies, local storage)
            _manager = webkit_website_data_manager_new_ephemeral();
            if (protections.HasFlag(ReaderProtections.DeadProxy))
            {
                var proxy = webkit_network_proxy_settings_new($"http://{DeadProxyHost}:{DeadProxyPort}", IntPtr.Zero);
                webkit_website_data_manager_set_network_proxy_settings(_manager, ProxyModeCustom, proxy);
                webkit_network_proxy_settings_free(proxy);
            }
            _context = webkit_web_context_new_with_website_data_manager(_manager);
            webkit_web_context_set_cache_model(_context, CacheModelDocumentViewer);
            webkit_web_context_register_uri_scheme(_context, Scheme, (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, void>)&OnSchemeRequest, me, IntPtr.Zero);
            g_signal_connect_data(_context, "download-started", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&OnDownload, me, IntPtr.Zero, 0);

            _contentManager = webkit_user_content_manager_new();
            if (protections.HasFlag(ReaderProtections.ContentBlocker)) InstallContentBlocker(me);

            _settings = webkit_settings_new();
            ApplySettings(_settings, protections.HasFlag(ReaderProtections.NoActiveContent));

            _view = NewObject(webkit_web_view_get_type(),
                ("web-context", webkit_web_context_get_type(), _context),
                ("user-content-manager", webkit_user_content_manager_get_type(), _contentManager),
                ("settings", webkit_settings_get_type(), _settings));
            g_object_ref_sink(_view);
            Connect("load-changed", (IntPtr)(delegate* unmanaged<IntPtr, int, IntPtr, void>)&OnLoadChanged, me);
            Connect("load-failed", (IntPtr)(delegate* unmanaged<IntPtr, int, IntPtr, IntPtr, IntPtr, int>)&OnLoadFailed, me);
            Connect("decide-policy", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, int, IntPtr, int>)&OnDecidePolicy, me);
            Connect("context-menu", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, int>)&OnContextMenu, me);
            Connect("permission-request", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, int>)&OnPermission, me);
            Connect("resource-load-started", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, void>)&OnResourceLoad, me);

            _window = gtk_offscreen_window_new();
            gtk_widget_set_size_request(_view, 900, 700);
            gtk_container_add(_window, _view);
            gtk_widget_show_all(_window);
        }

        private void Connect(string signal, IntPtr handler, IntPtr me) => g_signal_connect_data(_view, signal, handler, me, IntPtr.Zero, 0);

        private static void ApplySettings(IntPtr s, bool noActiveContent)
        {
            int on = noActiveContent ? 0 : 1;
            webkit_settings_set_enable_javascript(s, on);
            webkit_settings_set_enable_javascript_markup(s, on);
            webkit_settings_set_javascript_can_open_windows_automatically(s, 0);
            webkit_settings_set_auto_load_images(s, 1); // our inline images (pstb:) and data: images
            webkit_settings_set_enable_html5_local_storage(s, 0);
            webkit_settings_set_enable_html5_database(s, 0);
            webkit_settings_set_enable_page_cache(s, 0);
            webkit_settings_set_enable_dns_prefetching(s, 0);
            webkit_settings_set_enable_webgl(s, 0);
            webkit_settings_set_enable_webaudio(s, 0);
            webkit_settings_set_enable_media(s, on);
            webkit_settings_set_enable_mediasource(s, 0);
            webkit_settings_set_enable_encrypted_media(s, 0);
            webkit_settings_set_enable_media_stream(s, 0);
            webkit_settings_set_enable_webrtc(s, 0);
            webkit_settings_set_enable_hyperlink_auditing(s, 0);
            webkit_settings_set_enable_developer_extras(s, 0);
            webkit_settings_set_enable_fullscreen(s, 0);
            webkit_settings_set_enable_site_specific_quirks(s, 0);
            webkit_settings_set_media_playback_requires_user_gesture(s, 1);
            webkit_settings_set_allow_file_access_from_file_urls(s, 0);
            webkit_settings_set_allow_universal_access_from_file_urls(s, 0);
        }

        /// <summary>Compiles the content blocker (asynchronous in WebKitGTK) and installs it before the first page.</summary>
        private void InstallContentBlocker(IntPtr me)
        {
            _filterDir = Path.Combine(Path.GetTempPath(), "pstbrowser-filters-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_filterDir);
            var store = webkit_user_content_filter_store_new(_filterDir);
            var json = Encoding.UTF8.GetBytes(ContentRules);
            var bytes = g_bytes_new(json, (nuint)json.Length);
            webkit_user_content_filter_store_save(store, "pstbrowser", bytes, IntPtr.Zero,
                (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&OnFilterSaved, me);
            var sw = Stopwatch.StartNew();
            while (!_filterDone && sw.Elapsed < TimeSpan.FromSeconds(30)) g_main_context_iteration(IntPtr.Zero, 1);
            g_bytes_unref(bytes);
            g_object_unref(store);
            if (_filter == IntPtr.Zero)
                throw new InvalidOperationException("Le filtre de contenu WebKit n'a pas pu être compilé : " + LastError);
            webkit_user_content_manager_add_filter(_contentManager, _filter);
        }

        public override void Navigate(string uri)
        {
            _loaded = false;
            webkit_web_view_load_uri(_view, uri);
        }

        public override string Title => Str(webkit_web_view_get_title(_view));

        protected override bool IsLoaded => _loaded;

        public override void Pump(TimeSpan duration)
        {
            var sw = Stopwatch.StartNew();
            do
            {
                if (g_main_context_iteration(IntPtr.Zero, 0) == 0) System.Threading.Thread.Sleep(5);
            } while (sw.Elapsed < duration);
        }

        public override void Dispose()
        {
            if (_window != IntPtr.Zero) { gtk_widget_destroy(_window); _window = IntPtr.Zero; }
            if (_view != IntPtr.Zero) { g_object_unref(_view); _view = IntPtr.Zero; }
            foreach (var o in new[] { _settings, _contentManager, _context, _manager })
                if (o != IntPtr.Zero) g_object_unref(o);
            _settings = _contentManager = _context = _manager = IntPtr.Zero;
            if (_filter != IntPtr.Zero) { webkit_user_content_filter_unref(_filter); _filter = IntPtr.Zero; }
            Pump(TimeSpan.FromMilliseconds(50));
            if (_filterDir != null) try { Directory.Delete(_filterDir, true); } catch { }
            if (_self.IsAllocated) _self.Free();
        }

        private static WebKitGtkReader From(IntPtr data) => (WebKitGtkReader)GCHandle.FromIntPtr(data).Target;

        // ------------------------------------------------------------------ callbacks (GTK main thread)

        [UnmanagedCallersOnly]
        private static void OnFilterSaved(IntPtr store, IntPtr result, IntPtr data)
        {
            var me = From(data);
            me._filter = webkit_user_content_filter_store_save_finish(store, result, out var error);
            if (error != IntPtr.Zero) { me.LastError = GErrorMessage(error); g_error_free(error); }
            me._filterDone = true;
        }

        [UnmanagedCallersOnly]
        private static void OnSchemeRequest(IntPtr request, IntPtr data)
        {
            var me = From(data);
            var uri = Str(webkit_uri_scheme_request_get_uri(request));
            var res = me.Resolve(uri);
            if (res == null)
            {
                var err = g_error_new_literal(g_quark_from_string("pstbrowser"), 404, "Ressource absente");
                webkit_uri_scheme_request_finish_error(request, err);
                g_error_free(err);
                return;
            }
            // the stream owns a copy of the bytes, released by g_free
            var copy = g_malloc((nuint)Math.Max(1, res.Data.Length));
            Marshal.Copy(res.Data, 0, copy, res.Data.Length);
            var stream = g_memory_input_stream_new_from_data(copy, res.Data.Length, (IntPtr)(delegate* unmanaged<IntPtr, void>)&GFree);
            webkit_uri_scheme_request_finish(request, stream, res.Data.Length, res.MimeType ?? "application/octet-stream");
            g_object_unref(stream);
        }

        [UnmanagedCallersOnly]
        private static void GFree(IntPtr mem) => g_free(mem);

        [UnmanagedCallersOnly]
        private static void OnLoadChanged(IntPtr view, int loadEvent, IntPtr data)
        {
            if (loadEvent == LoadFinished) From(data)._loaded = true;
        }

        [UnmanagedCallersOnly]
        private static int OnLoadFailed(IntPtr view, int loadEvent, IntPtr uri, IntPtr error, IntPtr data)
        {
            var me = From(data);
            me.LastError = GErrorMessage(error) + " (" + Str(uri) + ")";
            return 0; // WebKit shows its error page, then "load-changed" FINISHED follows
        }

        [UnmanagedCallersOnly]
        private static int OnDecidePolicy(IntPtr view, IntPtr decision, int type, IntPtr data)
        {
            if (type != PolicyNavigation && type != PolicyNewWindow) return 0; // responses: default handling
            var me = From(data);
            var action = webkit_navigation_policy_decision_get_navigation_action(decision);
            var uri = Str(webkit_uri_request_get_uri(webkit_navigation_action_get_request(action)));
            bool click = webkit_navigation_action_get_navigation_type(action) == NavigationLinkClicked || webkit_navigation_action_is_user_gesture(action) != 0;
            if (type == PolicyNavigation && me.AllowNavigation(uri, click)) webkit_policy_decision_use(decision);
            else
            {
                if (type == PolicyNewWindow) me.AllowNavigation(uri, click); // never a new window: reported as an external link
                webkit_policy_decision_ignore(decision);
            }
            return 1;
        }

        [UnmanagedCallersOnly]
        private static int OnContextMenu(IntPtr view, IntPtr menu, IntPtr ev, IntPtr hit, IntPtr data) => 1; // no "open link", "download image"…

        [UnmanagedCallersOnly]
        private static int OnPermission(IntPtr view, IntPtr request, IntPtr data)
        {
            webkit_permission_request_deny(request);
            return 1;
        }

        [UnmanagedCallersOnly]
        private static void OnResourceLoad(IntPtr view, IntPtr resource, IntPtr request, IntPtr data)
        {
            var uri = Str(webkit_uri_request_get_uri(request));
            if (uri != null && !uri.StartsWith(Origin, StringComparison.OrdinalIgnoreCase)) From(data).RequestedResources.Add(uri);
        }

        [UnmanagedCallersOnly]
        private static void OnDownload(IntPtr context, IntPtr download, IntPtr data) => webkit_download_cancel(download);

        private static string GErrorMessage(IntPtr error)
            => error == IntPtr.Zero ? null : Str(Marshal.ReadIntPtr(error, 8)); // GError { GQuark domain; gint code; gchar *message; }
    }
}
