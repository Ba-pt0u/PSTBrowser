using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using PstBrowser.Core.Index;
using PstBrowser.Core.Viewer;
using PstBrowser.Desktop.Services;

namespace PstBrowser.Desktop.Windows
{
    /// <summary>Sources of the case: mailbox grouping, indexing options, re-indexing, removal, sources moved elsewhere.</summary>
    public partial class SourcesWindow : Window
    {
        public sealed class Row
        {
            public long Id { get; set; }
            public string MailboxName { get; set; }
            public string OriginalMailbox { get; set; }
            public string DisplayName { get; set; }
            public string Path { get; set; }
            public string SizeText { get; set; }
            public string StatusText { get; set; }
            public string CountText { get; set; }
            public string Sha256 { get; set; }
            public bool Missing { get; set; }
            public SourceKind Kind { get; set; }
            public IBrush RowForeground => Missing ? MissingBrush : Brushes.Black;
            private static readonly IBrush MissingBrush = new SolidColorBrush(Color.Parse("#B3261E"));
        }

        private bool _loading;
        /// <summary>True when something changed that requires refreshing the tree or restarting indexing.</summary>
        public bool Changed { get; private set; }

        public SourcesWindow()
        {
            InitializeComponent();
            _loading = true;
            var ws = AppServices.Workspace;
            if (ws != null)
            {
                int par = int.TryParse(ws.GetMeta("parallelism"), out var p) ? p : 2;
                CmbParallel.SelectedIndex = Math.Max(0, Math.Min(3, par - 1));
                ChkSha.IsChecked = ws.GetMeta("sha256") == "1";
                ChkAttachments.IsChecked = ws.AttachmentIndexingEnabled;
            }
            _loading = false;
            Reload();
        }

        private List<Row> Rows => (SourcesGrid.ItemsSource as List<Row>) ?? new List<Row>();

        private void Reload()
        {
            var ws = AppServices.Workspace;
            if (ws == null) return;
            SourcesGrid.ItemsSource = ws.ListSources().Select(s => new Row
            {
                Id = s.Id,
                MailboxName = s.MailboxName,
                OriginalMailbox = s.MailboxName,
                DisplayName = s.DisplayName,
                Path = s.Path,
                SizeText = Format.Size(s.Size),
                StatusText = s.FileMissing ? "introuvable" : StatusLabel(s.Status) + (string.IsNullOrEmpty(s.Error) ? "" : " : " + s.Error),
                CountText = s.Status == "done" ? s.MessageCount.ToString("N0") : $"{s.IndexedCount:N0} / {s.MessageCount:N0}",
                Sha256 = s.Sha256,
                Missing = s.FileMissing,
                Kind = s.Kind,
            }).ToList();
            BtnRelocate.IsEnabled = Rows.Any(r => r.Missing);
        }

        public static string StatusLabel(string status) => status switch
        {
            "pending" => "en attente",
            "listing" => "lecture…",
            "listed" => "structure lue",
            "indexing" => "indexation…",
            "done" => "indexé",
            "error" => "erreur",
            _ => status,
        };

        private async void Grid_CellEditEnded(object sender, DataGridCellEditEndedEventArgs e)
        {
            if (e.EditAction != DataGridEditAction.Commit || !(e.Row.DataContext is Row row)) return;
            var name = (row.MailboxName ?? "").Trim();
            if (name.Length == 0 || name == row.OriginalMailbox) { row.MailboxName = row.OriginalMailbox; return; }
            // The indexer must not be writing rows of this source with the old mailbox meanwhile
            await RestartingIndexer(() =>
            {
                AppServices.Workspace.SetSourceMailbox(row.Id, name);
                row.OriginalMailbox = name;
            }, reload: false);
        }

        private List<Row> Selected() => SourcesGrid.SelectedItems.OfType<Row>().ToList();

        private async void Remove_Click(object sender, RoutedEventArgs e)
        {
            var rows = Selected();
            if (rows.Count == 0) return;
            if (!await Dialogs.Confirm(this, Title, $"Retirer {rows.Count} source(s) de l'index ?\nLes fichiers eux-mêmes ne sont pas modifiés.")) return;
            await RestartingIndexer(() => { foreach (var r in rows) AppServices.Workspace.RemoveSource(r.Id); });
        }

        private async void Reindex_Click(object sender, RoutedEventArgs e)
        {
            var rows = Selected();
            if (rows.Count == 0) return;
            await RestartingIndexer(() => { foreach (var r in rows) AppServices.Workspace.ResetSource(r.Id); });
        }

        private async void Relocate_Click(object sender, RoutedEventArgs e)
        {
            var row = Selected().FirstOrDefault(r => r.Missing) ?? Rows.FirstOrDefault(r => r.Missing);
            if (row == null) return;
            var ws = AppServices.Workspace;
            var source = ws.GetSource(row.Id);
            string newPath;
            if (row.Kind == SourceKind.MsgFolder)
            {
                var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = $"Nouvel emplacement du dossier « {row.DisplayName} »" });
                newPath = folders.FirstOrDefault()?.TryGetLocalPath();
            }
            else
            {
                var ext = System.IO.Path.GetExtension(row.DisplayName);
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = $"Nouvel emplacement de « {row.DisplayName} »",
                    FileTypeFilter = new[]
                    {
                        new FilePickerFileType(row.DisplayName) { Patterns = new[] { row.DisplayName } },
                        new FilePickerFileType($"Fichiers {ext}") { Patterns = new[] { "*" + ext } },
                        FilePickerFileTypes.All,
                    },
                });
                newPath = files.FirstOrDefault()?.TryGetLocalPath();
            }
            if (newPath == null) return;

            // what would move, before changing anything
            RelocationResult preview;
            try { preview = ws.RelocateFrom(source, newPath, dryRun: true); }
            catch (Exception ex) { await Dialogs.Info(this, Title, ex.Message); return; }
            var found = preview.Items.Where(i => i.Outcome == RelocationOutcome.Relocated).ToList();
            var notFound = preview.Items.Where(i => i.Outcome != RelocationOutcome.Relocated).ToList();
            var sb = new StringBuilder();
            if (found.Count == 0) sb.Append("Aucune source retrouvée à cet emplacement.");
            else sb.Append($"{found.Count} source(s) retrouvée(s) :\n").Append(string.Join("\n", found.Take(12).Select(i => "  • " + i.Source.DisplayName))).Append(found.Count > 12 ? "\n  …" : "");
            if (notFound.Count > 0)
                sb.Append("\n\nNon retrouvée(s) :\n").Append(string.Join("\n", notFound.Take(8).Select(i => $"  • {i.Source.DisplayName} — {i.OutcomeLabel}")));
            if (found.Count == 0) { await Dialogs.Info(this, Title, sb.ToString()); return; }
            sb.Append("\n\nLes chemins enregistrés dans le dossier d'affaire seront mis à jour (les fichiers ne sont pas modifiés).");
            if (!await Dialogs.Confirm(this, Title, sb.ToString())) return;
            await RestartingIndexer(() => ws.RelocateFrom(source, newPath));
        }

        private async Task RestartingIndexer(Action action, bool reload = true)
        {
            try
            {
                Cursor = new Cursor(StandardCursorType.Wait);
                var ix = AppServices.Indexer;
                if (ix != null) await ix.StopAndWait();
                action();
                Changed = true;
                if (reload) Reload();
            }
            catch (Exception ex) { await Dialogs.Info(this, Title, ex.Message); }
            finally { Cursor = null; }
        }

        private void Options_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading || AppServices.Workspace == null) return;
            AppServices.Workspace.SetMeta("parallelism", (CmbParallel.SelectedIndex + 1).ToString());
            AppServices.Workspace.SetMeta("sha256", ChkSha.IsChecked == true ? "1" : "0");
            AppServices.Workspace.AttachmentIndexingEnabled = ChkAttachments.IsChecked == true;
            Changed = true;
        }

        private async void Copy_Click(object sender, RoutedEventArgs e)
        {
            var sb = new StringBuilder("Boîte\tFichier\tTaille\tÉtat\tÉléments\tSHA-256\tChemin\n");
            foreach (var r in Rows) sb.Append($"{r.MailboxName}\t{r.DisplayName}\t{r.SizeText}\t{r.StatusText}\t{r.CountText}\t{r.Sha256}\t{r.Path}\n");
            try { await Clipboard.SetTextAsync(sb.ToString()); } catch { }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
