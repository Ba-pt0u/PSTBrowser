using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using PstBrowser.Core.Viewer;
using PstBrowser.Reader;

namespace PstBrowser.ReaderTest
{
    /// <summary>
    /// "Zero network request" test of the secure reader (macOS: WKWebView, Linux: WebKitGTK).
    /// A canary server on the loopback address records every connection. A hostile message references it in every way
    /// a web page can load something (images, CSS, fonts, frames, media, SVG, prefetch, redirection, scripts, forms…).
    ///  - control: an unprotected view must reach the canary (otherwise the test proves nothing);
    ///  - the hardened view alone, the sanitizer alone, then both together must not reach it at all;
    ///  - inline images served from memory must still be displayed, and scripts must not run.
    /// </summary>
    internal static class Program
    {
        private static int _failures;

        private static int Main()
        {
            Console.OutputEncoding = Encoding.UTF8;
            using var canary = new Canary();
            string hostile = HostileMessage($"http://127.0.0.1:{canary.Port}");
            // PstBrowser.Core maps cid: images to its own address after sanitizing the body
            const string Inline = "https://pstbrowser.invalid/inline/logo";
            Console.WriteLine($"Lecteur sécurisé ({Environment.OSVersion}) — serveur témoin sur 127.0.0.1:{canary.Port}");

            // the document as PstBrowser.Core produces it: sanitized body + strict Content-Security-Policy
            string sanitized = BodyRenderer.Sanitize(hostile, out bool blockedRemote).Replace("cid:logo", Inline);
            string full = sanitized.Replace("<head>", $"<head><meta http-equiv=\"Content-Security-Policy\" content=\"{BodyRenderer.Csp}\">");
            Check(blockedRemote, "le nettoyage du message signale des contenus distants bloqués");

            Console.WriteLine("1. Témoin : vue sans protection, message brut");
            var control = Render(canary, hostile.Replace("cid:logo", Inline), ReaderProtections.None);
            Check(control.hits.Count >= 5, $"le serveur témoin reçoit les requêtes d'une vue non protégée ({control.hits.Count} : {string.Join(", ", control.hits.Take(12))}…)");
            Check(control.title == "JS", "les scripts s'exécutent dans une vue non protégée (le test peut les détecter)");

            Console.WriteLine("2. Vue protégée seule, message brut (non nettoyé)");
            var raw = Render(canary, hostile.Replace("cid:logo", Inline), ReaderProtections.All);
            Check(raw.loaded, "message affiché");
            Check(raw.hits.Count == 0, Hits("aucune requête réseau", raw.hits));
            Check(raw.title != "JS", "aucun script exécuté");
            Check(raw.served >= 1, $"image intégrée servie depuis la mémoire ({raw.served})");

            // each network barrier of the view on its own, scripts enabled (worst case)
            foreach (var (layer, label) in new[] { (ReaderProtections.ContentBlocker, "bloqueur de contenu"), (ReaderProtections.DeadProxy, "proxy inexistant") })
            {
                Console.WriteLine($"2{(layer == ReaderProtections.ContentBlocker ? "b" : "c")}. Vue avec le seul {label}, scripts actifs, message brut");
                var one = Render(canary, hostile.Replace("cid:logo", Inline), layer);
                Check(one.loaded && one.hits.Count == 0, Hits($"{label} seul : aucune requête réseau", one.hits));
                Check(one.served >= 1, $"image intégrée servie depuis la mémoire ({one.served})");
            }

            Console.WriteLine("3. Nettoyage seul (vue non protégée)");
            var clean = Render(canary, sanitized, ReaderProtections.None);
            Check(clean.hits.Count == 0, Hits("aucune requête réseau", clean.hits));
            Check(clean.title != "JS", "aucun script exécuté");
            Check(clean.served >= 1, $"image intégrée conservée par le nettoyage ({clean.served})");

            Console.WriteLine("4. Configuration de l'application : message nettoyé, CSP, vue protégée");
            var app = Render(canary, full, ReaderProtections.All);
            Check(app.loaded, "message affiché");
            Check(app.hits.Count == 0, Hits("aucune requête réseau", app.hits));
            Check(app.served >= 1, $"image intégrée servie depuis la mémoire ({app.served})");

            Console.WriteLine("5. Navigation vers une adresse externe (lien, redirection)");
            using (var reader = SecureReader.CreateOffscreen(ReaderProtections.All))
            {
                string clicked = null;
                reader.ExternalLinkRequested += u => clicked = u;
                reader.ShowHtml("<!DOCTYPE html><html><body><p>Message</p></body></html>");
                reader.WaitForLoad(TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(200));
                canary.Clear();
                reader.Navigate($"http://127.0.0.1:{canary.Port}/v/navigation");
                reader.Pump(TimeSpan.FromSeconds(2));
                Check(canary.Hits.Count == 0 && reader.RefusedNavigations.Any(u => u.Contains("/v/navigation")), "navigation refusée, aucune requête");
                Check(clicked == null, "une navigation qui ne vient pas d'un clic n'est pas proposée à l'utilisateur");
            }

            Console.WriteLine(_failures == 0 ? "\nTOUS LES TESTS SONT PASSÉS" : $"\n{_failures} TEST(S) EN ÉCHEC");
            return _failures == 0 ? 0 : 3;
        }

        private static (bool loaded, List<string> hits, string title, int served) Render(Canary canary, string html, ReaderProtections protections)
        {
            canary.Clear();
            using var reader = SecureReader.CreateOffscreen(protections);
            reader.ResourceProvider = path => path == "inline/logo" ? new ReaderResource { Data = Png, MimeType = "image/png" } : null;
            reader.ShowHtml(html);
            bool loaded = reader.WaitForLoad(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(3));
            var hits = canary.Hits;
            var external = reader.RequestedResources.Where(u => !u.StartsWith(SecureReader.Origin) && !u.StartsWith("data:")).Distinct().ToList();
            Console.WriteLine($"      chargé : {loaded} ; titre : {reader.Title} ; ressources demandées hors pstb: {external.Count} ; navigations refusées : {reader.RefusedNavigations.Count}"
                              + (reader.LastError != null ? " ; " + reader.LastError : ""));
            return (loaded, hits, reader.Title, reader.ServedResources);
        }

        private static string Hits(string what, List<string> hits) => hits.Count == 0 ? what : $"{what} — reçues : {string.Join(", ", hits)}";

        private static void Check(bool ok, string what)
        {
            Console.WriteLine((ok ? "  OK    " : "  ÉCHEC ") + what);
            if (!ok) _failures++;
        }

        /// <summary>Every way for a page to load something from the network. Each vector has its own path (/v/name).</summary>
        private static string HostileMessage(string h) => $@"<!DOCTYPE html><html><head>
<title>Message</title>
<base href=""{h}/v/base/"">
<meta http-equiv=""refresh"" content=""1;url={h}/v/meta-refresh"">
<link rel=""stylesheet"" href=""{h}/v/link-css"">
<link rel=""preload"" as=""image"" href=""{h}/v/preload"">
<link rel=""prefetch"" href=""{h}/v/prefetch"">
<link rel=""preconnect"" href=""{h}/v/preconnect"">
<link rel=""dns-prefetch"" href=""{h}/v/dns-prefetch"">
<link rel=""icon"" href=""{h}/v/favicon"">
<style>
@import url(""{h}/v/css-import"");
@import ""{h}/v/css-import-string"";
@font-face {{ font-family: X; src: url(""{h}/v/font""); }}
body {{ background-image: url(""{h}/v/css-background""); font-family: X; }}
.c {{ list-style-image: url(""{h}/v/css-list""); cursor: url(""{h}/v/css-cursor""), auto; border: 10px solid; border-image: url(""{h}/v/css-border"") 30 round; }}
.d::before {{ content: url(""{h}/v/css-content""); }}
.e {{ background-image: image-set(""{h}/v/css-image-set"" 1x); width: 10px; height: 10px; }}
.f {{ background-image: url( {h}/v/css-unquoted ); width: 10px; height: 10px; }}
</style>
<script src=""{h}/v/script-src""></script>
<script>document.title = 'JS'; new Image().src = '{h}/v/script-inline'; try {{ new WebSocket('{h.Replace("http:", "ws:")}/v/websocket'); fetch('{h}/v/fetch'); }} catch (e) {{ }}</script>
</head>
<body background=""{h}/v/body-background"" onload=""new Image().src='{h}/v/onload'"">
<p>Bonjour, voici le document.</p>
<img src=""{h}/v/img"" width=""10"" height=""10"">
<img src={h}/v/img-unquoted width=10 height=10>
<img SRC = '  {h}/v/img-spaces' width=""10"" height=""10"">
<img src=""//127.0.0.1:{h.Substring(h.LastIndexOf(':') + 1)}/v/protocol-relative"" width=""10"" height=""10"">
<div class=""e"">x</div><div class=""f"">x</div>
<img src=""relative.png"" width=""10"" height=""10"">
<img srcset=""{h}/v/srcset 1x, {h}/v/srcset-2x 2x"" width=""10"" height=""10"">
<picture><source srcset=""{h}/v/picture""><img alt="""" width=""10"" height=""10""></picture>
<img src=""x"" onerror=""new Image().src='{h}/v/onerror'"">
<input type=""image"" src=""{h}/v/input-image"">
<table background=""{h}/v/table-background""><tr><td background=""{h}/v/td-background"">x</td></tr></table>
<div style=""background:url('{h}/v/style-attribute')"">x</div>
<ul class=""c""><li>x</li></ul><div class=""d"">x</div>
<iframe src=""{h}/v/iframe""></iframe>
<iframe srcdoc=""&lt;img src='{h}/v/srcdoc'&gt;""></iframe>
<object data=""{h}/v/object""></object>
<embed src=""{h}/v/embed"">
<video poster=""{h}/v/video-poster"" src=""{h}/v/video"" preload=""auto""></video>
<audio src=""{h}/v/audio"" preload=""auto"" autoplay></audio>
<video preload=""auto""><source src=""{h}/v/video-source""><track default src=""{h}/v/track""></video>
<svg width=""10"" height=""10""><image href=""{h}/v/svg-image"" width=""10"" height=""10""/><use href=""{h}/v/svg-use#a""/></svg>
<svg xmlns:xlink=""http://www.w3.org/1999/xlink"" width=""10"" height=""10""><image xlink:href=""{h}/v/svg-xlink"" width=""10"" height=""10""/></svg>
<svg width=""10"" height=""10""><filter id=""f""><feImage href=""{h}/v/svg-feimage""/></filter><rect width=""10"" height=""10"" filter=""url(#f)""/></svg>
<form action=""{h}/v/form""><input name=""a"" value=""b""></form>
<a href=""{h}/v/link"" ping=""{h}/v/ping"">lien</a>
<img src=""cid:logo"" alt=""logo intégré"">
<img src=""data:image/gif;base64,R0lGODlhAQABAIAAAP///wAAACwAAAAAAQABAAACAkQBADs="" alt=""image data:"">
<img src=""{h}/v/lazy"" loading=""lazy"" style=""margin-top:3000px"">
</body></html>";

        /// <summary>1×1 PNG.</summary>
        private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

        /// <summary>Loopback TCP server recording every connection (and the request line, when there is one).</summary>
        private sealed class Canary : IDisposable
        {
            private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
            private readonly ConcurrentQueue<string> _hits = new ConcurrentQueue<string>();
            private readonly Thread _thread;
            private volatile bool _stop;

            public int Port { get; }
            public List<string> Hits => _hits.Distinct().ToList();
            public void Clear() { while (_hits.TryDequeue(out _)) { } }

            public Canary()
            {
                _listener.Start();
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                _thread = new Thread(Run) { IsBackground = true };
                _thread.Start();
            }

            private void Run()
            {
                while (!_stop)
                {
                    TcpClient c;
                    try { c = _listener.AcceptTcpClient(); } catch { return; }
                    ThreadPool.QueueUserWorkItem(_ => Serve(c));
                }
            }

            private void Serve(TcpClient c)
            {
                using (c)
                {
                    string what = "connexion";
                    try
                    {
                        c.ReceiveTimeout = 2000;
                        var s = c.GetStream();
                        var buf = new byte[2048];
                        int n = s.Read(buf, 0, buf.Length);
                        var line = Encoding.ASCII.GetString(buf, 0, Math.Max(0, n)).Split('\n')[0].Trim();
                        var parts = line.Split(' ');
                        what = parts.Length > 1 ? parts[1] : line;
                        var gif = Convert.FromBase64String("R0lGODlhAQABAIAAAP///wAAACwAAAAAAQABAAACAkQBADs=");
                        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: image/gif\r\nContent-Length: {gif.Length}\r\nConnection: close\r\n\r\n");
                        s.Write(head); s.Write(gif);
                    }
                    catch { }
                    _hits.Enqueue(what);
                }
            }

            public void Dispose() { _stop = true; _listener.Stop(); }
        }
    }
}
