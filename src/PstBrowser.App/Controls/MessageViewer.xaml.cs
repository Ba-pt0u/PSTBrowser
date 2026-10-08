using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using PstBrowser.App.Services;
using PstBrowser.App.Windows;
using PstBrowser.Core.Viewer;

namespace PstBrowser.App.Controls
{
    public partial class MessageViewer : UserControl
    {
        private readonly SecureWebView _secure;
        private MessageView _view;
        private IList<string> _terms;
        private int _loadId;
        private readonly Stack<MessageRef> _history = new Stack<MessageRef>();

        private static readonly HashSet<string> DangerousExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".com", ".scr", ".pif", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta",
            ".msi", ".msp", ".lnk", ".reg", ".jar", ".cpl", ".dll", ".sys", ".inf", ".scf", ".url", ".appx", ".msix", ".application",
            ".gadget", ".iso", ".img", ".vhd", ".vhdx", ".chm", ".docm", ".xlsm", ".pptm", ".xlam", ".settingcontent-ms",
        };

        /// <summary>Hides the "new window" button (used inside a message window).</summary>
        public bool ShowPopOutButton
        {
            get => BtnPopOut.Visibility == Visibility.Visible;
            set => BtnPopOut.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }

        public MessageRef CurrentRef => _view?.Ref;

        public MessageViewer()
        {
            InitializeComponent();
            _secure = new SecureWebView(Web)
            {
                InlineImageProvider = token =>
                {
                    var svc = AppServices.Messages;
                    if (svc == null) return null;
                    int slash = token.LastIndexOf('/');
                    if (slash <= 0) return null;
                    var mref = MessageRef.Parse(token.Substring(0, slash));
                    return svc.GetAttachmentBytes(mref, int.Parse(token.Substring(slash + 1)));
                },
            };
        }

        public void Clear(string placeholder = null)
        {
            _loadId++;
            _view = null;
            _history.Clear();
            Body.Visibility = Visibility.Collapsed;
            TxtPlaceholder.Text = placeholder ?? "Sélectionnez un message pour l'afficher";
            TxtPlaceholder.Visibility = Visibility.Visible;
        }

        public Task ShowAsync(MessageRef mref, IList<string> highlightTerms)
        {
            _history.Clear();
            return LoadAsync(mref, highlightTerms);
        }

        private async Task LoadAsync(MessageRef mref, IList<string> highlightTerms)
        {
            var svc = AppServices.Messages;
            if (svc == null) return;
            int id = ++_loadId;
            _terms = highlightTerms;
            MessageView view;
            try
            {
                view = await Task.Run(() => svc.GetView(mref, highlightTerms));
            }
            catch (Exception ex)
            {
                if (id != _loadId) return;
                Body.Visibility = Visibility.Collapsed;
                TxtPlaceholder.Text = "Impossible d'afficher ce message :\n" + ex.Message;
                TxtPlaceholder.Visibility = Visibility.Visible;
                return;
            }
            if (id != _loadId) return; // a newer selection superseded this one
            _view = view;
            Fill(view);
            TxtPlaceholder.Visibility = Visibility.Collapsed;
            Body.Visibility = Visibility.Visible;
            await _secure.EnsureInitializedAsync();
            if (_secure.InitError != null)
            {
                Web.Visibility = Visibility.Collapsed;
                TxtWebError.Text = _secure.InitError;
                TxtWebError.Visibility = Visibility.Visible;
                return;
            }
            if (id != _loadId) return;
            await _secure.ShowHtmlAsync(view.BodyHtml);
        }

        private void Fill(MessageView v)
        {
            TxtSubject.Text = string.IsNullOrWhiteSpace(v.Subject) ? "(sans objet)" : v.Subject;
            if (v.Importance == 2) TxtSubject.Text = "❗ " + TxtSubject.Text;
            SetRow(LblFrom, TxtFrom, v.From);
            SetRow(LblTo, TxtTo, v.To);
            SetRow(LblCc, TxtCc, v.Cc);
            SetRow(LblBcc, TxtBcc, v.Bcc);
            TxtDate.Text = v.Date.HasValue ? v.Date.Value.ToString("dddd d MMMM yyyy HH:mm") : "";
            var kind = v.Kind != "Message" ? v.Kind + " · " : "";
            TxtLocation.Text = $"{kind}{v.Mailbox} › {v.FolderPath?.Replace("/", " › ")}   ({v.SourceName})" + (v.Ref.IsEmbedded ? "   · message joint" : "");
            TxtLocation.ToolTip = v.SourcePath;
            BtnBack.Visibility = _history.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            AttachmentsList.Items.Clear();
            foreach (var a in v.Attachments)
            {
                var btn = new Button
                {
                    Style = (Style)FindResource("ToolButton"),
                    Margin = new Thickness(0, 0, 6, 6),
                    Tag = a,
                    ToolTip = a.IsEmbeddedMessage ? "Message joint : cliquer pour l'ouvrir" : $"{a.FileName} ({a.SizeText})",
                    Content = new TextBlock
                    {
                        Text = (a.IsEmbeddedMessage ? "✉  " : "📄  ") + a.FileName + (a.IsEmbeddedMessage ? "" : "   " + a.SizeText),
                        MaxWidth = 320,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    },
                };
                btn.Click += Attachment_Click;
                AttachmentsList.Items.Add(btn);
            }
            AttachmentsBar.Visibility = v.Attachments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private static void SetRow(TextBlock label, TextBox value, string text)
        {
            var vis = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
            label.Visibility = vis;
            value.Visibility = vis;
            value.Text = text ?? "";
        }

        // ------------------------------------------------------------------ attachments

        private void Attachment_Click(object sender, RoutedEventArgs e)
        {
            var btn = (Button)sender;
            var a = (AttachmentView)btn.Tag;
            if (a.IsEmbeddedMessage)
            {
                _history.Push(_view.Ref);
                _ = LoadAsync(_view.Ref.Child(a.Index), _terms);
                return;
            }
            var menu = new ContextMenu { PlacementTarget = btn };
            var open = new MenuItem { Header = "Ouvrir avec l'application associée" };
            open.Click += (_, __) => OpenAttachment(a);
            var save = new MenuItem { Header = "Enregistrer sous…" };
            save.Click += (_, __) => SaveAttachment(a);
            menu.Items.Add(open);
            menu.Items.Add(save);
            menu.IsOpen = true;
        }

        private void SaveAttachment(AttachmentView a)
        {
            var dlg = new SaveFileDialog { FileName = EmlWriter.SafeFileName(a.FileName), Title = "Enregistrer la pièce jointe" };
            var ext = Path.GetExtension(a.FileName);
            if (!string.IsNullOrEmpty(ext)) dlg.Filter = $"Fichier {ext}|*{ext}|Tous les fichiers|*.*";
            if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
            var mref = _view.Ref;
            Run(() => AppServices.Messages.SaveAttachment(mref, a.Index, dlg.FileName), "Enregistrement");
        }

        private void OpenAttachment(AttachmentView a)
        {
            var ext = Path.GetExtension(a.FileName ?? "");
            if (DangerousExtensions.Contains(ext))
            {
                MessageBox.Show(Window.GetWindow(this),
                    $"Par sécurité, les fichiers « {ext} » ne peuvent pas être ouverts directement.\nEnregistrez-le puis faites-le analyser si nécessaire.",
                    "Pièce jointe", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var mref = _view.Ref;
            try
            {
                var dir = Path.Combine(App.TempFolder, Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, EmlWriter.SafeFileName(a.FileName));
                AppServices.Messages.SaveAttachment(mref, a.Index, path);
                try { File.SetAttributes(path, FileAttributes.ReadOnly); } catch { }
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(this), "Impossible d'ouvrir la pièce jointe :\n" + ex.Message, "Pièce jointe", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void SaveAll_Click(object sender, RoutedEventArgs e)
        {
            if (_view == null) return;
            var dlg = new OpenFolderDialog { Title = "Dossier où enregistrer les pièces jointes" };
            if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
            var mref = _view.Ref;
            var atts = _view.Attachments.ToList();
            Run(() =>
            {
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var a in atts)
                {
                    var name = EmlWriter.SafeFileName(a.FileName) + (a.IsEmbeddedMessage ? ".eml" : "");
                    var path = UniquePath(dlg.FolderName, name, used);
                    if (a.IsEmbeddedMessage) AppServices.Messages.ExportEml(mref.Child(a.Index), path);
                    else AppServices.Messages.SaveAttachment(mref, a.Index, path);
                }
            }, "Enregistrement des pièces jointes");
        }

        internal static string UniquePath(string folder, string name, HashSet<string> used)
        {
            var path = Path.Combine(folder, name);
            int i = 2;
            while (used.Contains(path) || File.Exists(path))
                path = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(name)} ({i++}){Path.GetExtension(name)}");
            used.Add(path);
            return path;
        }

        private async void Run(Action action, string what)
        {
            try
            {
                Cursor = System.Windows.Input.Cursors.Wait;
                await Task.Run(action);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(this), $"{what} impossible :\n{ex.Message}", "PST Browser", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally { Cursor = null; }
        }

        // ------------------------------------------------------------------ toolbar

        private void Back_Click(object sender, RoutedEventArgs e)
        {
            if (_history.Count == 0) return;
            _ = LoadAsync(_history.Pop(), _terms);
        }

        private void Headers_Click(object sender, RoutedEventArgs e)
        {
            if (_view == null) return;
            var v = _view;
            var text =
                $"Objet : {v.Subject}\r\nDe : {v.From}\r\nÀ : {v.To}\r\nCc : {v.Cc}\r\nCci : {v.Bcc}\r\n" +
                $"Date de réception : {v.Date}\r\nDate d'envoi : {v.SentDate}\r\nClasse MAPI : {v.MessageClass}\r\n" +
                $"Message-ID : {v.InternetMessageId}\r\nFormat du corps : {v.BodyFormat}\r\n" +
                $"Boîte : {v.Mailbox}\r\nDossier : {v.FolderPath}\r\nFichier source : {v.SourcePath}\r\nRéférence interne : {v.Ref.Key}\r\n" +
                "\r\n----- En-têtes Internet -----\r\n" + (string.IsNullOrEmpty(v.TransportHeaders) ? "(non disponibles pour ce message)" : v.TransportHeaders.Replace("\r\n", "\n").Replace("\n", "\r\n"));
            new TextWindow("Propriétés du message", text) { Owner = Window.GetWindow(this) }.Show();
        }

        private void ExportEml_Click(object sender, RoutedEventArgs e)
        {
            if (_view == null) return;
            var name = (_view.Date.HasValue ? _view.Date.Value.ToString("yyyy-MM-dd_HHmm") + " - " : "") + EmlWriter.SafeFileName(_view.Subject ?? "message");
            var dlg = new SaveFileDialog { FileName = name + ".eml", Filter = "Message (*.eml)|*.eml", Title = "Exporter le message" };
            if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
            var mref = _view.Ref;
            Run(() => AppServices.Messages.ExportEml(mref, dlg.FileName), "Export");
        }

        private void PopOut_Click(object sender, RoutedEventArgs e)
        {
            if (_view == null) return;
            MessageWindow.Open(_view.Ref, _terms, Window.GetWindow(this));
        }
    }
}
