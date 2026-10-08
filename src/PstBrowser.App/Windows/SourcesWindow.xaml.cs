using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using PstBrowser.App.Services;
using PstBrowser.Core.Index;
using PstBrowser.Core.Viewer;

namespace PstBrowser.App.Windows
{
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
        }

        private bool _loading;
        /// <summary>True when something changed that requires refreshing the tree or restarting indexing.</summary>
        public bool Changed { get; private set; }

        public SourcesWindow()
        {
            InitializeComponent();
            _loading = true;
            var ws = AppServices.Workspace;
            int par = int.TryParse(ws.GetMeta("parallelism"), out var p) ? p : 2;
            CmbParallel.SelectedIndex = Math.Max(0, Math.Min(3, par - 1));
            ChkSha.IsChecked = ws.GetMeta("sha256") == "1";
            _loading = false;
            Reload();
        }

        private void Reload()
        {
            SourcesGrid.ItemsSource = AppServices.Workspace.ListSources().Select(s => new Row
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
            }).ToList();
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

        private void Grid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            if (e.EditAction != DataGridEditAction.Commit || !(e.Row.Item is Row row)) return;
            if (!(e.EditingElement is TextBox tb)) return;
            var name = tb.Text.Trim();
            if (name.Length == 0 || name == row.OriginalMailbox) return;
            try
            {
                AppServices.Workspace.SetSourceMailbox(row.Id, name);
                row.OriginalMailbox = name;
                Changed = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private List<Row> Selected() => SourcesGrid.SelectedItems.OfType<Row>().ToList();

        private void Remove_Click(object sender, RoutedEventArgs e)
        {
            var rows = Selected();
            if (rows.Count == 0) return;
            if (MessageBox.Show(this, $"Retirer {rows.Count} source(s) de l'index ?\nLes fichiers eux-mêmes ne sont pas modifiés.", Title,
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            RestartingIndexer(() => { foreach (var r in rows) AppServices.Workspace.RemoveSource(r.Id); });
        }

        private void Reindex_Click(object sender, RoutedEventArgs e)
        {
            var rows = Selected();
            if (rows.Count == 0) return;
            RestartingIndexer(() => { foreach (var r in rows) AppServices.Workspace.ResetSource(r.Id); });
        }

        private void RestartingIndexer(Action action)
        {
            try
            {
                Cursor = System.Windows.Input.Cursors.Wait;
                AppServices.Indexer?.StopAndWait().Wait();
                action();
                Changed = true;
                Reload();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally { Cursor = null; }
        }

        private void Options_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading || AppServices.Workspace == null) return;
            AppServices.Workspace.SetMeta("parallelism", (CmbParallel.SelectedIndex + 1).ToString());
            AppServices.Workspace.SetMeta("sha256", ChkSha.IsChecked == true ? "1" : "0");
            Changed = true;
        }

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            var sb = new StringBuilder("Boîte\tFichier\tTaille\tÉtat\tÉléments\tSHA-256\tChemin\r\n");
            foreach (var r in (IEnumerable<Row>)SourcesGrid.ItemsSource)
                sb.Append($"{r.MailboxName}\t{r.DisplayName}\t{r.SizeText}\t{r.StatusText}\t{r.CountText}\t{r.Sha256}\t{r.Path}\r\n");
            try { Clipboard.SetText(sb.ToString()); } catch { }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
