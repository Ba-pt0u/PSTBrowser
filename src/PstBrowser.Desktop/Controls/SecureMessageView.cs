using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Platform;
using PstBrowser.Reader;

namespace PstBrowser.Desktop.Controls
{
    /// <summary>
    /// Hosts the secure reader of PstBrowser.Reader (WKWebView on macOS, WebKitGTK on Linux) in the Avalonia window.
    /// The native view is created when the control is shown and destroyed when it leaves the window; the current
    /// document is displayed again when it is recreated.
    /// </summary>
    public sealed class SecureMessageView : NativeControlHost
    {
        private SecureReader _reader;
        private string _html;

        public static bool IsSupported => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

        /// <summary>Bytes of pstb://pstbrowser.invalid/&lt;path&gt; (inline images).</summary>
        public Func<string, ReaderResource> ResourceProvider { get; set; }

        /// <summary>The user clicked a link of the message: nothing is opened without the application's decision.</summary>
        public event Action<string> ExternalLinkRequested;

        /// <summary>Why the reader could not be created (missing system component…), shown instead of the message.</summary>
        public string InitError { get; private set; }

        public event Action InitFailed;

        /// <summary>Number of documents the native view has requested from memory (interface self-test).</summary>
        public int DocumentsLoaded => _reader?.RequestedResources.Count(u => u.Contains("/msg/")) ?? 0;

        public void ShowHtml(string html)
        {
            _html = html ?? "";
            _reader?.ShowHtml(_html);
        }

        public void Clear() => ShowHtml("<!DOCTYPE html><html><body style=\"background:#fff\"></body></html>");

        protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
        {
            if (!IsSupported) return base.CreateNativeControlCore(parent);
            try
            {
                _reader = SecureReader.CreateEmbedded();
                _reader.ResourceProvider = path => ResourceProvider?.Invoke(path);
                _reader.ExternalLinkRequested += uri => ExternalLinkRequested?.Invoke(uri);
                if (_html != null) _reader.ShowHtml(_html);
                return new PlatformHandle(_reader.NativeHandle, _reader.NativeHandleKind);
            }
            catch (Exception ex)
            {
                _reader = null;
                InitError = OperatingSystem.IsLinux()
                    ? "Le composant d'affichage WebKitGTK est introuvable (paquet libwebkit2gtk-4.1). Demandez son installation à votre service informatique.\n(" + ex.Message + ")"
                    : "Impossible d'initialiser l'affichage des messages : " + ex.Message;
                InitFailed?.Invoke();
                return base.CreateNativeControlCore(parent);
            }
        }

        protected override void DestroyNativeControlCore(IPlatformHandle control)
        {
            if (_reader != null)
            {
                _reader.Dispose();
                _reader = null;
            }
            else base.DestroyNativeControlCore(control);
        }
    }
}
