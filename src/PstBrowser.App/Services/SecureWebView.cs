using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using PstBrowser.Core.Viewer;

namespace PstBrowser.App.Services
{
    /// <summary>
    /// Configures a WebView2 control to display e-mail bodies offline and safely:
    ///  - JavaScript disabled, no dev tools, no downloads, no autofill, InPrivate profile (nothing cached on disk);
    ///  - every request is intercepted: content is served from memory under https://pstbrowser.invalid/,
    ///    any other request (http, https, file…) is refused before reaching the network;
    ///  - clicking a link never navigates: the user is asked whether to open it in the default browser.
    /// </summary>
    public sealed class SecureWebView
    {
        private readonly WebView2 _web;
        private string _currentHtml;
        private int _counter;
        private bool _ready;
        private Task _init;

        /// <summary>Provides inline image bytes for a token "msgKey/attIndex".</summary>
        public Func<string, byte[]> InlineImageProvider { get; set; }

        public string InitError { get; private set; }

        public SecureWebView(WebView2 web)
        {
            _web = web;
            _web.CreationProperties = new CoreWebView2CreationProperties
            {
                UserDataFolder = Path.Combine(App.LocalDataFolder, "WebView2"),
                IsInPrivateModeEnabled = true,
                AdditionalBrowserArguments = "--disable-background-networking --disable-component-update --disable-domain-reliability --disable-sync --no-pings --dns-prefetch-disable",
            };
        }

        public Task EnsureInitializedAsync() => _init ??= InitAsync();

        private async Task InitAsync()
        {
            try
            {
                await _web.EnsureCoreWebView2Async();
            }
            catch (Exception ex)
            {
                InitError = ex is WebView2RuntimeNotFoundException
                    ? "Le composant Microsoft Edge WebView2 est introuvable. Il est installé d'office sur Windows 10 et 11 ; sinon, demandez son installation à votre service informatique."
                    : "Impossible d'initialiser l'affichage des messages : " + ex.Message;
                return;
            }
            var core = _web.CoreWebView2;
            var s = core.Settings;
            s.IsScriptEnabled = false;
            s.IsWebMessageEnabled = false;
            s.AreDevToolsEnabled = false;
            s.AreHostObjectsAllowed = false;
            s.IsStatusBarEnabled = false;
            s.IsGeneralAutofillEnabled = false;
            s.IsPasswordAutosaveEnabled = false;
            s.AreDefaultScriptDialogsEnabled = false;
            s.IsZoomControlEnabled = true;
            s.AreBrowserAcceleratorKeysEnabled = true; // Ctrl+F (rechercher), Ctrl+P (imprimer)

            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += OnWebResourceRequested;
            core.NavigationStarting += OnNavigationStarting;
            core.NewWindowRequested += OnNewWindowRequested;
            core.DownloadStarting += (_, e) => { e.Cancel = true; };
            core.PermissionRequested += (_, e) => { e.State = CoreWebView2PermissionState.Deny; };
            _ready = true;
        }

        public async Task ShowHtmlAsync(string html)
        {
            await EnsureInitializedAsync();
            if (!_ready) return;
            _currentHtml = html ?? "";
            _counter++;
            _web.CoreWebView2.Navigate($"https://{MessageService.VirtualHost}/msg/{_counter}");
        }

        public Task ClearAsync() => ShowHtmlAsync("<!DOCTYPE html><html><body style=\"background:#fff\"></body></html>");

        private static bool IsOurs(string uri)
            => Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.Scheme == "https" &&
               string.Equals(u.Host, MessageService.VirtualHost, StringComparison.OrdinalIgnoreCase);

        private void OnWebResourceRequested(object sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            var env = _web.CoreWebView2.Environment;
            if (!IsOurs(e.Request.Uri))
            {
                e.Response = env.CreateWebResourceResponse(null, 403, "Blocked", "Cache-Control: no-store");
                return;
            }
            var path = new Uri(e.Request.Uri).AbsolutePath;
            if (path.StartsWith("/msg/", StringComparison.Ordinal))
            {
                var bytes = Encoding.UTF8.GetBytes(_currentHtml ?? "");
                e.Response = env.CreateWebResourceResponse(new MemoryStream(bytes), 200, "OK",
                    "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\n" +
                    "Content-Security-Policy: " + BodyRenderer.Csp);
                return;
            }
            if (path.StartsWith("/inline/", StringComparison.Ordinal) && InlineImageProvider != null)
            {
                var token = Uri.UnescapeDataString(path.Substring("/inline/".Length));
                var deferral = e.GetDeferral();
                var provider = InlineImageProvider;
                Task.Run(() =>
                {
                    try { return provider(token); } catch { return null; }
                }).ContinueWith(t =>
                {
                    _web.Dispatcher.InvokeAsync(() =>
                    {
                        try
                        {
                            var data = t.Result;
                            e.Response = data == null
                                ? env.CreateWebResourceResponse(null, 404, "Not found", "Cache-Control: no-store")
                                : env.CreateWebResourceResponse(new MemoryStream(data), 200, "OK",
                                    "Content-Type: " + SniffImageType(data) + "\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff");
                        }
                        catch { }
                        finally { deferral.Complete(); }
                    });
                });
                return;
            }
            e.Response = env.CreateWebResourceResponse(null, 404, "Not found", "Cache-Control: no-store");
        }

        private static string SniffImageType(byte[] b)
        {
            if (b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return "image/png";
            if (b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8) return "image/jpeg";
            if (b.Length > 6 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F') return "image/gif";
            if (b.Length > 2 && b[0] == 'B' && b[1] == 'M') return "image/bmp";
            if (b.Length > 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P') return "image/webp";
            return "application/octet-stream";
        }

        private void OnNavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            if (IsOurs(e.Uri)) return;
            e.Cancel = true;
            if (e.IsUserInitiated) ExternalLink(e.Uri);
        }

        private void OnNewWindowRequested(object sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            e.Handled = true;
            if (e.IsUserInitiated) ExternalLink(e.Uri);
        }

        private void ExternalLink(string uri)
        {
            if (string.IsNullOrEmpty(uri) || uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return;
            var owner = Window.GetWindow(_web);
            var r = MessageBox.Show(owner,
                "Ce lien pointe vers une adresse externe :\n\n" + uri +
                "\n\nOui : l'ouvrir dans votre navigateur (une connexion Internet sera établie, ce qui peut signaler à l'expéditeur que le message a été lu).\n" +
                "Non : copier le lien dans le presse-papiers.\nAnnuler : ne rien faire.",
                "Lien externe", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
            try
            {
                if (r == MessageBoxResult.Yes)
                {
                    if (uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                        uri.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true });
                    else
                        MessageBox.Show(owner, "Seuls les liens http, https et mailto peuvent être ouverts.", "Lien externe", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else if (r == MessageBoxResult.No) Clipboard.SetText(uri);
            }
            catch { }
        }
    }
}
