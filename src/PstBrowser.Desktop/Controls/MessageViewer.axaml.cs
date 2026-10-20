using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PstBrowser.Core.Viewer;
using PstBrowser.Desktop.Services;
using PstBrowser.Desktop.Windows;
using PstBrowser.Reader;

namespace PstBrowser.Desktop.Controls
{
    /// <summary>Reading pane: header, alerts, attachments and the body in the secure reader.</summary>
    public partial class MessageViewer : UserControl
    {
        private MessageView _view;
        private IList<string> _terms;
        private string _query;
        private int _loadId;
        private readonly Stack<MessageRef> _history = new Stack<MessageRef>();

        public MessageRef CurrentRef => _view?.Ref;

        /// <summary>Interface self-test: documents loaded by the native view, and why it could not be shown.</summary>
        public int DocumentsLoaded => Web.DocumentsLoaded;
        public string ReaderError => TxtWebError.IsVisible ? TxtWebError.Text : null;

        /// <summary>The user asks to see the whole thread of the displayed message (argument: thread id).</summary>
        public event Action<long> ThreadRequested;
        /// <summary>Analysis window of the displayed message (reference, subject).</summary>
        public event Action<MessageRef, string> AnalysisRequested;
        /// <summary>Displayed message in a window of its own.</summary>
        public event Action<MessageRef, IList<string>> PopOutRequested;

        public MessageViewer()
        {
            InitializeComponent();
            Web.ResourceProvider = ProvideResource;
            Web.ExternalLinkRequested += uri => Dispatcher.UIThread.Post(() => _ = ExternalLink(uri));
            Web.InitFailed += () =>
            {
                Web.IsVisible = false;
                TxtWebError.Text = Web.InitError;
                TxtWebError.IsVisible = true;
            };
            if (!SecureMessageView.IsSupported)
            {
                Web.IsVisible = false;
                TxtWebError.Text = "Sous Windows, utilisez l'application PST Browser pour Windows (PstBrowser.exe).";
                TxtWebError.IsVisible = true;
            }
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            BtnAnalysis.IsVisible = AnalysisRequested != null;
            BtnPopOut.IsVisible = PopOutRequested != null;
        }

        /// <summary>Inline images of the message: pstb://pstbrowser.invalid/inline/&lt;message key&gt;/&lt;attachment index&gt;.</summary>
        private static ReaderResource ProvideResource(string path)
        {
            const string Prefix = "inline/";
            var svc = AppServices.Messages;
            if (svc == null || !path.StartsWith(Prefix, StringComparison.Ordinal)) return null;
            var token = Uri.UnescapeDataString(path.Substring(Prefix.Length));
            int slash = token.LastIndexOf('/');
            if (slash <= 0 || !int.TryParse(token.Substring(slash + 1), out int index)) return null;
            var data = svc.GetAttachmentBytes(MessageRef.Parse(token.Substring(0, slash)), index);
            return data == null ? null : new ReaderResource { Data = data, MimeType = SniffImageType(data) };
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

        public void Clear(string placeholder = null)
        {
            _loadId++;
            _view = null;
            _history.Clear();
            Body.IsVisible = false;
            TxtPlaceholder.Text = placeholder ?? "Sélectionnez un message pour l'afficher";
            TxtPlaceholder.IsVisible = true;
        }

        public Task ShowAsync(MessageRef mref, IList<string> highlightTerms, string query = null)
        {
            _history.Clear();
            _query = query;
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
                view = await Task.Run(() => svc.GetView(mref, highlightTerms, _query));
            }
            catch (Exception ex)
            {
                if (id != _loadId) return;
                Body.IsVisible = false;
                TxtPlaceholder.Text = "Impossible d'afficher ce message :\n" + ex.Message;
                TxtPlaceholder.IsVisible = true;
                return;
            }
            if (id != _loadId) return; // a newer selection superseded this one
            _view = view;
            Fill(view);
            TxtPlaceholder.IsVisible = false;
            Body.IsVisible = true;
            Web.ShowHtml(view.BodyHtml);
        }

        private void Fill(MessageView v)
        {
            TxtSubject.Text = (v.Importance == 2 ? "❗ " : "") + (string.IsNullOrWhiteSpace(v.Subject) ? "(sans objet)" : v.Subject);
            SetRow(LblFrom, TxtFrom, v.From);
            SetRow(LblTo, TxtTo, v.To);
            SetRow(LblCc, TxtCc, v.Cc);
            SetRow(LblBcc, TxtBcc, v.Bcc);
            TxtDate.Text = v.Date.HasValue ? v.Date.Value.ToString("dddd d MMMM yyyy HH:mm") : "";
            var kind = v.Kind != "Message" ? v.Kind + " · " : "";
            TxtLocation.Text = $"{kind}{v.Mailbox} › {v.FolderPath?.Replace("/", " › ")}   ({v.SourceName})" + (v.Ref.IsEmbedded ? "   · message joint" : "");
            ToolTip.SetTip(TxtLocation, v.SourcePath);
            BtnBack.IsVisible = _history.Count > 0;
            var alert = v.AlertText;
            AlertBar.IsVisible = alert.Length > 0;
            TxtAlert.Text = "⚠ " + alert;
            BtnThread.IsVisible = v.ThreadId.HasValue && v.ThreadCount > 1 && ThreadRequested != null;
            BtnThread.Content = $"Fil ({v.ThreadCount})";

            AttachmentsList.Children.Clear();
            foreach (var a in v.Attachments)
            {
                var tip = a.IsEmbeddedMessage ? "Message joint : cliquer pour l'ouvrir" : $"{a.FileName} ({a.SizeText})";
                if (a.TextStatus != null && !a.IsEmbeddedMessage) tip += "\nTexte : " + PstBrowser.Core.Extraction.AttachmentStatus.Label(a.TextStatus) + (a.HasText ? $" ({a.TextLength:N0} caractères)" : "");
                if (a.HasHit) tip += "\nContient les termes recherchés";
                var btn = new Button
                {
                    Margin = new Thickness(0, 0, 6, 6),
                    Tag = a,
                    Content = new TextBlock
                    {
                        Text = (a.HasHit ? "🔎 " : "") + (a.IsEmbeddedMessage ? "✉  " : "📄  ") + a.FileName + (a.IsEmbeddedMessage ? "" : "   " + a.SizeText),
                        MaxWidth = 340,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        FontWeight = a.HasHit ? FontWeight.SemiBold : FontWeight.Normal,
                    },
                };
                btn.Classes.Add("tool");
                ToolTip.SetTip(btn, tip);
                if (a.HasHit)
                {
                    btn.BorderBrush = (IBrush)Application.Current.FindResource("AccentBrush");
                    btn.BorderThickness = new Thickness(2);
                    btn.Background = (IBrush)Application.Current.FindResource("AccentLightBrush");
                }
                Avalonia.Automation.AutomationProperties.SetName(btn, (a.HasHit ? "Contient les termes recherchés : " : "") + a.FileName);
                btn.Click += Attachment_Click;
                AttachmentsList.Children.Add(btn);
            }
            AttachmentsBar.IsVisible = v.Attachments.Count > 0;
        }

        private static void SetRow(TextBlock label, SelectableTextBlock value, string text)
        {
            var visible = !string.IsNullOrWhiteSpace(text);
            label.IsVisible = visible;
            value.IsVisible = visible;
            value.Text = text ?? "";
        }

        private Window Owner => TopLevel.GetTopLevel(this) as Window;
        private IStorageProvider Storage => TopLevel.GetTopLevel(this)?.StorageProvider;

        // ------------------------------------------------------------------ links

        private async Task ExternalLink(string uri)
        {
            if (string.IsNullOrEmpty(uri) || uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return;
            var choice = await Dialogs.Ask(Owner, "Lien externe",
                "Ce lien pointe vers une adresse externe :\n\n" + uri +
                "\n\nL'ouvrir dans votre navigateur établit une connexion Internet, ce qui peut signaler à l'expéditeur que le message a été lu.",
                "Copier le lien", "Ouvrir dans le navigateur", "Annuler");
            try
            {
                if (choice == "Copier le lien") await TopLevel.GetTopLevel(this).Clipboard.SetTextAsync(uri);
                else if (choice == "Ouvrir dans le navigateur")
                {
                    if (uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                        uri.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                        Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
                    else await Dialogs.Info(Owner, "Lien externe", "Seuls les liens http, https et mailto peuvent être ouverts.");
                }
            }
            catch { }
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
            var menu = new MenuFlyout();
            var preview = new MenuItem { Header = "Aperçu du texte", IsEnabled = a.HasText };
            ToolTip.SetTip(preview, a.HasText ? "Texte extrait de la pièce jointe, avec les termes recherchés surlignés"
                : "Aucun texte extrait : " + (a.TextStatus != null ? PstBrowser.Core.Extraction.AttachmentStatus.Label(a.TextStatus) : "pièce jointe non indexée"));
            preview.Click += (_, __) => PreviewAttachmentText(a);
            var open = new MenuItem { Header = "Ouvrir avec l'application associée" };
            open.Click += (_, __) => _ = OpenAttachment(a);
            var save = new MenuItem { Header = "Enregistrer sous…" };
            save.Click += (_, __) => _ = SaveAttachment(a);
            menu.Items.Add(preview);
            menu.Items.Add(open);
            menu.Items.Add(save);
            if (!string.IsNullOrEmpty(a.Sha256))
            {
                var sha = new MenuItem { Header = "Copier l'empreinte SHA-256" };
                sha.Click += async (_, __) => { try { await TopLevel.GetTopLevel(this).Clipboard.SetTextAsync(a.Sha256); } catch { } };
                menu.Items.Add(sha);
            }
            menu.ShowAt(btn);
        }

        private void PreviewAttachmentText(AttachmentView a)
        {
            var mref = _view.Ref;
            var terms = _terms;
            var owner = Owner;
            Task.Run(() => AppServices.Messages.GetAttachmentText(mref.Id, a.IdxPath)).ContinueWith(t =>
                Dispatcher.UIThread.Post(() =>
                {
                    if (t.IsFaulted || t.Result == null)
                    {
                        _ = Dialogs.Info(owner, "Aperçu du texte", "Le texte de cette pièce jointe n'est pas disponible dans l'index.");
                        return;
                    }
                    var at = t.Result;
                    var note = $"Texte extrait de « {at.Name} » ({at.Text.Length:N0} caractères) — ce n'est pas le document d'origine : la mise en page, les images et les formules ne sont pas reprises.";
                    HtmlWindow.Open("Aperçu du texte — " + at.Name, BodyRenderer.RenderText(at.Name, note, at.Text, terms), owner);
                }));
        }

        private async Task SaveAttachment(AttachmentView a)
        {
            var file = await Storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Enregistrer la pièce jointe",
                SuggestedFileName = EmlWriter.SafeFileName(a.FileName),
                ShowOverwritePrompt = true,
            });
            var path = file?.TryGetLocalPath();
            if (path == null) return;
            var mref = _view.Ref;
            await Run(() => AppServices.Messages.SaveAttachment(mref, a.Index, path), "Enregistrement");
        }

        private async Task OpenAttachment(AttachmentView a)
        {
            if (AttachmentSafety.IsDangerous(a.FileName))
            {
                await Dialogs.Info(Owner, "Pièce jointe",
                    $"Par sécurité, les fichiers « {Path.GetExtension(a.FileName)} » ne peuvent pas être ouverts directement.\nEnregistrez-le puis faites-le analyser si nécessaire.");
                return;
            }
            var mref = _view.Ref;
            try
            {
                var dir = Path.Combine(App.TempFolder, Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, EmlWriter.SafeFileName(a.FileName));
                await Task.Run(() => AppServices.Messages.SaveAttachment(mref, a.Index, path));
                // read-only copy, removed when the application closes
                try { File.SetUnixFileMode(path, UnixFileMode.UserRead); } catch { }
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                await Dialogs.Info(Owner, "Pièce jointe", "Impossible d'ouvrir la pièce jointe :\n" + ex.Message);
            }
        }

        private async void SaveAll_Click(object sender, RoutedEventArgs e)
        {
            if (_view == null) return;
            var folders = await Storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Dossier où enregistrer les pièces jointes" });
            var folder = folders.FirstOrDefault()?.TryGetLocalPath();
            if (folder == null) return;
            var mref = _view.Ref;
            var atts = _view.Attachments.ToList();
            await Run(() =>
            {
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var a in atts)
                {
                    var name = EmlWriter.SafeFileName(a.FileName) + (a.IsEmbeddedMessage ? ".eml" : "");
                    var path = UniquePath(folder, name, used);
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

        private async Task Run(Action action, string what)
        {
            try { await Task.Run(action); }
            catch (Exception ex) { await Dialogs.Info(Owner, "PST Browser", $"{what} impossible :\n{ex.Message}"); }
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
                $"Objet : {v.Subject}\nDe : {v.From}\nÀ : {v.To}\nCc : {v.Cc}\nCci : {v.Bcc}\n" +
                $"Date de réception : {v.Date}\nDate d'envoi : {v.SentDate}\nClasse MAPI : {v.MessageClass}\n" +
                $"Message-ID : {v.InternetMessageId}\nFormat du corps : {v.BodyFormat}\n" +
                $"Boîte : {v.Mailbox}\nDossier : {v.FolderPath}\nFichier source : {v.SourcePath}\nRéférence interne : {v.Ref.Key}\n" +
                "\n----- En-têtes Internet -----\n" + (string.IsNullOrEmpty(v.TransportHeaders) ? "(non disponibles pour ce message)" : v.TransportHeaders.Replace("\r\n", "\n"));
            TextWindow.Open("Propriétés du message", text, Owner);
        }

        private async void ExportEml_Click(object sender, RoutedEventArgs e)
        {
            if (_view == null) return;
            var name = (_view.Date.HasValue ? _view.Date.Value.ToString("yyyy-MM-dd_HHmm") + " - " : "") + EmlWriter.SafeFileName(_view.Subject ?? "message");
            var file = await Storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Exporter le message",
                SuggestedFileName = name + ".eml",
                DefaultExtension = "eml",
                FileTypeChoices = new[] { new FilePickerFileType("Message (.eml)") { Patterns = new[] { "*.eml" } } },
            });
            var path = file?.TryGetLocalPath();
            if (path == null) return;
            var mref = _view.Ref;
            await Run(() => AppServices.Messages.ExportEml(mref, path), "Export");
        }

        private void Analysis_Click(object sender, RoutedEventArgs e)
        {
            if (_view != null) AnalysisRequested?.Invoke(_view.Ref, _view.Subject);
        }

        private void Thread_Click(object sender, RoutedEventArgs e)
        {
            if (_view?.ThreadId is long id) ThreadRequested?.Invoke(id);
        }

        private void PopOut_Click(object sender, RoutedEventArgs e)
        {
            if (_view != null) PopOutRequested?.Invoke(_view.Ref, _terms);
        }
    }
}
