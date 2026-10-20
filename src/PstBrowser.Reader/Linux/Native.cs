using System;
using System.Runtime.InteropServices;

namespace PstBrowser.Reader.Linux
{
    /// <summary>GLib, GTK 3 and WebKitGTK (API 4.1) functions used by <see cref="WebKitGtkReader"/>.</summary>
    internal static unsafe class Native
    {
        private const string GLib = "libglib-2.0.so.0", GObject = "libgobject-2.0.so.0", Gio = "libgio-2.0.so.0";
        private const string Gtk = "libgtk-3.so.0", WebKit = "libwebkit2gtk-4.1.so.0";

        // ---- GLib / GObject / GIO
        [DllImport(GLib)] public static extern int g_main_context_iteration(IntPtr context, int mayBlock);
        [DllImport(GLib)] public static extern IntPtr g_malloc(nuint size);
        [DllImport(GLib)] public static extern void g_free(IntPtr mem);
        [DllImport(GLib)] public static extern IntPtr g_bytes_new(byte[] data, nuint size);
        [DllImport(GLib)] public static extern void g_bytes_unref(IntPtr bytes);
        [DllImport(GLib)] public static extern uint g_quark_from_string(string s);
        [DllImport(GLib)] public static extern IntPtr g_error_new_literal(uint domain, int code, string message);
        [DllImport(GLib)] public static extern void g_error_free(IntPtr error);
        [DllImport(GObject)] public static extern void g_object_unref(IntPtr obj);
        [DllImport(GObject)] public static extern IntPtr g_object_ref_sink(IntPtr obj);
        [DllImport(GObject)] public static extern ulong g_signal_connect_data(IntPtr instance, string signal, IntPtr handler, IntPtr data, IntPtr destroy, int flags);
        [DllImport(GObject)] public static extern IntPtr g_object_new_with_properties(nuint type, uint n, IntPtr[] names, IntPtr values);
        [DllImport(GObject)] public static extern IntPtr g_value_init(IntPtr value, nuint type);
        [DllImport(GObject)] public static extern void g_value_set_object(IntPtr value, IntPtr obj);
        [DllImport(GObject)] public static extern void g_value_unset(IntPtr value);
        [DllImport(Gio)] public static extern IntPtr g_memory_input_stream_new_from_data(IntPtr data, nint len, IntPtr destroy);

        // ---- GTK
        [DllImport(Gtk)] public static extern int gtk_init_check(IntPtr argc, IntPtr argv);
        [DllImport(Gtk)] public static extern IntPtr gtk_offscreen_window_new();
        [DllImport(Gtk)] public static extern void gtk_container_add(IntPtr container, IntPtr widget);
        [DllImport(Gtk)] public static extern void gtk_widget_set_size_request(IntPtr widget, int width, int height);
        [DllImport(Gtk)] public static extern void gtk_widget_show_all(IntPtr widget);
        [DllImport(Gtk)] public static extern void gtk_widget_destroy(IntPtr widget);

        // ---- WebKitGTK: context, data manager, scheme
        [DllImport(WebKit)] public static extern IntPtr webkit_website_data_manager_new_ephemeral();
        [DllImport(WebKit)] public static extern IntPtr webkit_web_context_new_with_website_data_manager(IntPtr manager);
        [DllImport(WebKit)] public static extern void webkit_web_context_set_cache_model(IntPtr context, int model);
        [DllImport(WebKit)] public static extern void webkit_web_context_register_uri_scheme(IntPtr context, string scheme, IntPtr callback, IntPtr data, IntPtr destroy);
        [DllImport(WebKit)] public static extern IntPtr webkit_uri_scheme_request_get_uri(IntPtr request);
        [DllImport(WebKit)] public static extern void webkit_uri_scheme_request_finish(IntPtr request, IntPtr stream, long length, string contentType);
        [DllImport(WebKit)] public static extern void webkit_uri_scheme_request_finish_error(IntPtr request, IntPtr error);
        [DllImport(WebKit)] public static extern IntPtr webkit_network_proxy_settings_new(string defaultProxyUri, IntPtr ignoreHosts);
        [DllImport(WebKit)] public static extern void webkit_website_data_manager_set_network_proxy_settings(IntPtr manager, int mode, IntPtr settings);
        [DllImport(WebKit)] public static extern void webkit_network_proxy_settings_free(IntPtr settings);

        // ---- content blocker
        [DllImport(WebKit)] public static extern IntPtr webkit_user_content_filter_store_new(string storagePath);
        [DllImport(WebKit)] public static extern void webkit_user_content_filter_store_save(IntPtr store, string identifier, IntPtr source, IntPtr cancellable, IntPtr callback, IntPtr data);
        [DllImport(WebKit)] public static extern IntPtr webkit_user_content_filter_store_save_finish(IntPtr store, IntPtr result, out IntPtr error);
        [DllImport(WebKit)] public static extern IntPtr webkit_user_content_manager_new();
        [DllImport(WebKit)] public static extern void webkit_user_content_manager_add_filter(IntPtr manager, IntPtr filter);
        [DllImport(WebKit)] public static extern void webkit_user_content_filter_unref(IntPtr filter);

        // ---- settings
        [DllImport(WebKit)] public static extern IntPtr webkit_settings_new();
        [DllImport(WebKit)] public static extern void webkit_settings_set_enable_javascript(IntPtr s, int v);
        [DllImport(WebKit)] public static extern void webkit_settings_set_enable_javascript_markup(IntPtr s, int v);
        [DllImport(WebKit)] public static extern void webkit_settings_set_javascript_can_open_windows_automatically(IntPtr s, int v);
        [DllImport(WebKit)] public static extern void webkit_settings_set_auto_load_images(IntPtr s, int v);
        [DllImport(WebKit)] public static extern void webkit_settings_set_enable_html5_local_storage(IntPtr s, int v);
        [DllImport(WebKit)] public static extern void webkit_settings_set_enable_html5_database(IntPtr s, int v);
        [DllImport(WebKit)] public static extern void webkit_settings_set_enable_page_cache(IntPtr s, int v);
        [DllImport(WebKit)] public static extern void webkit_settings_set_enable_dns_prefetching(IntPtr s, int v);
        [DllImport(WebKit)] public static extern void webkit_settings_set_enable_webgl(IntPtr s, int v);
        [DllImport(WebKit)] public static extern void webkit_settings_set_enable_media(IntPtr s, int v);
        [DllImport(WebKit)] public static extern void webkit_settings_set_enable_webaudio(IntPtr s, int v);
        [DllImport(WebKit)] public static extern void webkit_settings_set_enable_mediasource(IntPtr s, int v);
        [DllImport(WebKit)] public static extern void webkit_settings_set_enable_encrypted_media(IntPtr s, int v);
        [DllImport(WebKit)] public static extern void webkit_settings_set_enable_media_stream(IntPtr s, int v);
        [DllImport(WebKit)] public static extern void webkit_settings_set_enable_webrtc(IntPtr s, int v);
        [DllImport(WebKit)] public static extern void webkit_settings_set_enable_hyperlink_auditing(IntPtr s, int v);
        [DllImport(WebKit)] public static extern void webkit_settings_set_enable_developer_extras(IntPtr s, int v);
        [DllImport(WebKit)] public static extern void webkit_settings_set_enable_fullscreen(IntPtr s, int v);
        [DllImport(WebKit)] public static extern void webkit_settings_set_enable_site_specific_quirks(IntPtr s, int v);
        [DllImport(WebKit)] public static extern void webkit_settings_set_media_playback_requires_user_gesture(IntPtr s, int v);
        [DllImport(WebKit)] public static extern void webkit_settings_set_allow_file_access_from_file_urls(IntPtr s, int v);
        [DllImport(WebKit)] public static extern void webkit_settings_set_allow_universal_access_from_file_urls(IntPtr s, int v);

        // ---- view, navigation
        [DllImport(WebKit)] public static extern nuint webkit_web_view_get_type();
        [DllImport(WebKit)] public static extern nuint webkit_web_context_get_type();
        [DllImport(WebKit)] public static extern nuint webkit_user_content_manager_get_type();
        [DllImport(WebKit)] public static extern nuint webkit_settings_get_type();
        [DllImport(WebKit)] public static extern void webkit_web_view_load_uri(IntPtr view, string uri);
        [DllImport(WebKit)] public static extern IntPtr webkit_web_view_get_title(IntPtr view);
        [DllImport(WebKit)] public static extern void webkit_policy_decision_ignore(IntPtr decision);
        [DllImport(WebKit)] public static extern void webkit_policy_decision_use(IntPtr decision);
        [DllImport(WebKit)] public static extern IntPtr webkit_navigation_policy_decision_get_navigation_action(IntPtr decision);
        [DllImport(WebKit)] public static extern IntPtr webkit_navigation_action_get_request(IntPtr action);
        [DllImport(WebKit)] public static extern int webkit_navigation_action_get_navigation_type(IntPtr action);
        [DllImport(WebKit)] public static extern int webkit_navigation_action_is_user_gesture(IntPtr action);
        [DllImport(WebKit)] public static extern IntPtr webkit_uri_request_get_uri(IntPtr request);
        [DllImport(WebKit)] public static extern void webkit_permission_request_deny(IntPtr request);
        [DllImport(WebKit)] public static extern void webkit_download_cancel(IntPtr download);

        public static string Str(IntPtr utf8) => utf8 == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(utf8);

        /// <summary>GObject construction with properties of object type (non-variadic, so that it works on every ABI).</summary>
        public static IntPtr NewObject(nuint type, params (string name, nuint type, IntPtr value)[] props)
        {
            const int GValueSize = 24;
            var names = new IntPtr[props.Length];
            var values = Marshal.AllocHGlobal(GValueSize * Math.Max(1, props.Length));
            try
            {
                new Span<byte>((void*)values, GValueSize * Math.Max(1, props.Length)).Clear();
                for (int i = 0; i < props.Length; i++)
                {
                    names[i] = Marshal.StringToCoTaskMemUTF8(props[i].name);
                    var v = values + i * GValueSize;
                    g_value_init(v, props[i].type);
                    g_value_set_object(v, props[i].value);
                }
                return g_object_new_with_properties(type, (uint)props.Length, names, values);
            }
            finally
            {
                for (int i = 0; i < props.Length; i++)
                {
                    g_value_unset(values + i * GValueSize);
                    Marshal.FreeCoTaskMem(names[i]);
                }
                Marshal.FreeHGlobal(values);
            }
        }
    }
}
