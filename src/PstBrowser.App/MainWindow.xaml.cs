using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using PstBrowser.App.Services;
using PstBrowser.App.Windows;
using PstBrowser.Core.Index;
using PstBrowser.Core.Search;
using PstBrowser.Core.Viewer;

namespace PstBrowser.App
{
    public partial class MainWindow : Window
    {
        public static readonly RoutedCommand FocusSearchCommand = new RoutedCommand();

        private readonly Settings _settings = Settings.Load();
        private readonly ObservableCollection<TreeNode> _treeRoots = new ObservableCollection<TreeNode>();
        private readonly ObservableCollection<RowItem> _rows = new ObservableCollection<RowItem>();
        private readonly IndexerOptions _indexOptions = new IndexerOptions();
        private readonly DispatcherTimer _timer;
        private readonly DispatcherTimer _selectionTimer;

        private SearchRequest _lastRequest;
        private string _lastDescription;
        private List<string> _lastTerms = new List<string>();
        private string _lastQuery = "";
        private int _searchSeq;
        private bool _wasIndexing;
        private DateTime _lastTreeRefresh = DateTime.MinValue;
        private bool _suppressSortEvent;
        private bool _suppressTreeBrowse;

        public MainWindow()
        {
            InitializeComponent();
            InitColumns();
            Tree.ItemsSource = _treeRoots;
            MessageList.ItemsSource = _rows;
            TxtQuery.TextChanged += (_, __) => QueryHint.Visibility = string.IsNullOrEmpty(TxtQuery.Text) ? Visibility.Visible : Visibility.Collapsed;
            ChkHideEmpty.IsChecked = _settings.HideEmptyFolders;

            if (_settings.WindowWidth.HasValue && _settings.WindowHeight.HasValue)
            {
                Width = Math.Max(MinWidth, Math.Min(_settings.WindowWidth.Value, SystemParameters.VirtualScreenWidth));
                Height = Math.Max(MinHeight, Math.Min(_settings.WindowHeight.Value, SystemParameters.VirtualScreenHeight));
            }
            if (_settings.Maximized) WindowState = WindowState.Maximized;
            ColTree.Width = new GridLength(Math.Max(150, _settings.TreeWidth));

            ApplyReaderLayout(_settings.ReaderBottom ?? SystemParameters.PrimaryScreenWidth < 1600);
            DateFrom.Loaded += (_, __) => SetDateWatermark(DateFrom);
            DateTo.Loaded += (_, __) => SetDateWatermark(DateTo);

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _timer.Tick += Timer_Tick;
            _timer.Start();

            _selectionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            _selectionTimer.Tick += (_, __) => { _selectionTimer.Stop(); ShowSelectedMessage(); };

            FillRecent();
        }

        // ================================================================== layout

        private bool _readerBottom;

        /// <summary>Places the reading pane to the right of the message list, or below it.</summary>
        private void ApplyReaderLayout(bool bottom)
        {
            _readerBottom = bottom;
            ContentGrid.RowDefinitions.Clear();
            ContentGrid.ColumnDefinitions.Clear();
            if (bottom)
            {
                ContentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star), MinHeight = 120 });
                ContentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(5) });
                ContentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star), MinHeight = 150 });
                Grid.SetRow(ListPanel, 0); Grid.SetRow(ReaderSplitter, 1); Grid.SetRow(Viewer, 2);
                Grid.SetColumn(ListPanel, 0); Grid.SetColumn(ReaderSplitter, 0); Grid.SetColumn(Viewer, 0);
                ReaderSplitter.ResizeDirection = GridResizeDirection.Rows;
                ReaderSplitter.Width = double.NaN;
                ReaderSplitter.Height = 5;
                BtnLayout.Content = "Lecture à droite";
                BtnLayout.ToolTip = "Afficher le volet de lecture à droite de la liste";
            }
            else
            {
                ContentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 300 });
                ContentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
                ContentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 300 });
                Grid.SetColumn(ListPanel, 0); Grid.SetColumn(ReaderSplitter, 1); Grid.SetColumn(Viewer, 2);
                Grid.SetRow(ListPanel, 0); Grid.SetRow(ReaderSplitter, 0); Grid.SetRow(Viewer, 0);
                ReaderSplitter.ResizeDirection = GridResizeDirection.Columns;
                ReaderSplitter.Height = double.NaN;
                ReaderSplitter.Width = 5;
                BtnLayout.Content = "Lecture en bas";
                BtnLayout.ToolTip = "Afficher le volet de lecture sous la liste";
            }
        }

        private void Layout_Click(object sender, RoutedEventArgs e)
        {
            ApplyReaderLayout(!_readerBottom);
            _settings.ReaderBottom = _readerBottom;
            _settings.Save();
        }

        /// <summary>The DatePicker placeholder is not localised by WPF: replace it.</summary>
        private static void SetDateWatermark(DatePicker dp)
        {
            try
            {
                if (dp.Template.FindName("PART_TextBox", dp) is System.Windows.Controls.Primitives.DatePickerTextBox tb)
                {
                    tb.ApplyTemplate();
                    if (tb.Template.FindName("PART_Watermark", tb) is ContentControl wm) wm.Content = "jj/mm/aaaa";
                }
            }
            catch { }
        }

        // ================================================================== workspace

        public void OpenInitialWorkspace(string arg)
        {
            if (!string.IsNullOrEmpty(arg))
            {
                if (Directory.Exists(arg) && Workspace.Exists(arg)) { OpenWorkspace(arg); return; }
            }
            var last = _settings.RecentWorkspaces.FirstOrDefault(p => Workspace.Exists(p));
            if (last != null) OpenWorkspace(last);
        }

        private void FillRecent()
        {
            RecentList.Children.Clear();
            foreach (var p in _settings.RecentWorkspaces.Where(Directory.Exists).Take(6))
            {
                var path = p;
                var b = new Button { Content = path, Style = (Style)FindResource("LinkButton"), Margin = new Thickness(0, 2, 0, 2), HorizontalAlignment = HorizontalAlignment.Left };
                b.Click += (_, __) => OpenWorkspace(path);
                RecentList.Children.Add(b);
            }
            RecentTitle.Visibility = RecentList.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OpenWorkspace(string folder)
        {
            Workspace ws;
            try
            {
                ws = Workspace.OpenOrCreate(folder);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Impossible d'ouvrir le dossier d'affaire :\n" + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            AppServices.Open(ws, _indexOptions);
            _settings.AddRecent(folder);
            RunCaseName.Text = Path.GetFileName(folder.TrimEnd('\\', '/'));
            Title = $"{RunCaseName.Text} — PST Browser";
            Welcome.Visibility = Visibility.Collapsed;
            MainArea.Visibility = Visibility.Visible;
            SearchBar.IsEnabled = BtnAddFiles.IsEnabled = BtnAddFolder.IsEnabled = BtnSources.IsEnabled = BtnCsv.IsEnabled = true;
            _treeRoots.Clear();
            _rows.Clear();
            Viewer.Clear();
            RefreshTree();
            if (_treeRoots.Count > 0) _treeRoots[0].IsSelected = true; // browse everything
            if (!ws.ListSources().Any())
            {
                Viewer.Clear("Ce dossier d'affaire est vide.\nAjoutez des fichiers PST/MSG (boutons en haut, ou glisser-déposer).");
                SetStatus("Dossier d'affaire prêt : " + folder);
            }
            else SetStatus("Dossier d'affaire : " + folder);
            if (ws.HasPendingWork()) StartIndexing();
        }

        private void CloseWorkspace()
        {
            AppServices.Close();
            _treeRoots.Clear();
            _rows.Clear();
            Viewer.Clear();
            MainArea.Visibility = Visibility.Collapsed;
            Welcome.Visibility = Visibility.Visible;
            SearchBar.IsEnabled = BtnAddFiles.IsEnabled = BtnAddFolder.IsEnabled = BtnSources.IsEnabled = BtnCsv.IsEnabled = BtnEmlSel.IsEnabled = false;
            RunCaseName.Text = "Dossier d'affaire";
            Title = "PST Browser";
            TxtResults.Text = "";
            SetStatus("");
            FillRecent();
        }

        private void NewCase_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog { Title = "Choisissez (ou créez) un dossier vide pour le nouveau dossier d'affaire" };
            if (dlg.ShowDialog(this) != true) return;
            var folder = dlg.FolderName;
            if (!Workspace.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
            {
                if (MessageBox.Show(this, "Ce dossier n'est pas vide. Y créer quand même l'index du dossier d'affaire ?\n(Les fichiers existants ne seront pas modifiés.)",
                        Title, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            }
            OpenWorkspace(folder);
        }

        private void OpenCase_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog { Title = "Ouvrir un dossier d'affaire (dossier contenant " + Workspace.DbFileName + ")" };
            if (dlg.ShowDialog(this) != true) return;
            if (!Workspace.Exists(dlg.FolderName))
            {
                MessageBox.Show(this, "Ce dossier ne contient pas d'index PST Browser (" + Workspace.DbFileName + ").\nUtilisez « Nouveau dossier d'affaire » pour en créer un.",
                    Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            OpenWorkspace(dlg.FolderName);
        }

        private void BtnCase_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu { PlacementTarget = BtnCase, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            void Add(string header, Action action, bool enabled = true)
            {
                var mi = new MenuItem { Header = header, IsEnabled = enabled };
                mi.Click += (_, __) => action();
                menu.Items.Add(mi);
            }
            Add("Nouveau dossier d'affaire…", () => NewCase_Click(null, null));
            Add("Ouvrir un dossier d'affaire…", () => OpenCase_Click(null, null));
            var recents = _settings.RecentWorkspaces.Where(p => Workspace.Exists(p)).Take(6).ToList();
            if (recents.Count > 0)
            {
                menu.Items.Add(new Separator());
                foreach (var r in recents) { var path = r; Add(path, () => OpenWorkspace(path)); }
            }
            menu.Items.Add(new Separator());
            Add("Ouvrir l'emplacement dans l'Explorateur", () =>
            {
                try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + AppServices.Workspace.Folder + "\"") { UseShellExecute = true }); } catch { }
            }, AppServices.Workspace != null);
            Add("Fermer le dossier d'affaire", CloseWorkspace, AppServices.Workspace != null);
            menu.IsOpen = true;
        }

        // ================================================================== sources

        private void AddFiles_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Ajouter des fichiers PST, OST ou MSG",
                Multiselect = true,
                Filter = "Fichiers Outlook (*.pst;*.ost;*.msg)|*.pst;*.ost;*.msg|Tous les fichiers (*.*)|*.*",
            };
            if (dlg.ShowDialog(this) == true) AddSources(dlg.FileNames);
        }

        private void AddFolder_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog { Title = "Ajouter un dossier (PST/OST et MSG, sous-dossiers compris)", Multiselect = true };
            if (dlg.ShowDialog(this) == true) AddSources(dlg.FolderNames);
        }

        private async void AddSources(IEnumerable<string> paths)
        {
            var ws = AppServices.Workspace;
            if (ws == null) return;
            var list = paths.ToList();
            SetStatus("Recherche des fichiers PST/MSG…");
            Cursor = Cursors.Wait;
            List<SourceInfo> added;
            try
            {
                added = await Task.Run(() => ws.AddSources(list));
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Ajout impossible :\n" + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            finally { Cursor = null; }
            if (added.Count == 0)
            {
                SetStatus("Aucun nouveau fichier PST, OST ou MSG trouvé (ou déjà présents dans ce dossier d'affaire).");
                return;
            }
            var mailboxes = added.Select(a => a.MailboxName).Distinct().ToList();
            SetStatus($"{added.Count} source(s) ajoutée(s) dans {mailboxes.Count} boîte(s) : {string.Join(", ", mailboxes.Take(5))}{(mailboxes.Count > 5 ? "…" : "")}");
            RefreshTree();
            StartIndexing();
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && AppServices.Workspace != null ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (!(e.Data.GetData(DataFormats.FileDrop) is string[] files) || files.Length == 0) return;
            if (AppServices.Workspace == null)
            {
                MessageBox.Show(this, "Créez ou ouvrez d'abord un dossier d'affaire.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            AddSources(files);
        }

        private void Sources_Click(object sender, RoutedEventArgs e)
        {
            if (AppServices.Workspace == null) return;
            var w = new SourcesWindow { Owner = this };
            w.ShowDialog();
            if (w.Changed)
            {
                RefreshTree();
                if (AppServices.Workspace.HasPendingWork()) StartIndexing();
                if (_lastRequest != null) RunSearch(_lastRequest, _lastDescription, _lastTerms);
            }
        }

        // ================================================================== indexing

        private void StartIndexing()
        {
            var ws = AppServices.Workspace;
            var ix = AppServices.Indexer;
            if (ws == null || ix == null || ix.IsRunning) return;
            _indexOptions.Parallelism = int.TryParse(ws.GetMeta("parallelism"), out var p) ? Math.Max(1, Math.Min(4, p)) : 2;
            _indexOptions.ComputeSha256 = ws.GetMeta("sha256") == "1";
            ix.Start();
            BtnPause.Content = "Pause";
            _wasIndexing = true;
        }

        private async void Pause_Click(object sender, RoutedEventArgs e)
        {
            var ix = AppServices.Indexer;
            if (ix == null) return;
            if (ix.IsRunning)
            {
                BtnPause.IsEnabled = false;
                await ix.StopAndWait();
                BtnPause.IsEnabled = true;
                BtnPause.Content = "Reprendre";
            }
            else StartIndexing();
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            var ix = AppServices.Indexer;
            if (ix == null) { IndexPanel.Visibility = Visibility.Collapsed; return; }
            var p = ix.Progress;
            if (p.Running)
            {
                IndexPanel.Visibility = Visibility.Visible;
                IndexProgress.IsIndeterminate = p.Total == 0;
                IndexProgress.Maximum = Math.Max(1, p.Total);
                IndexProgress.Value = Math.Min(p.Done, p.Total);
                var rate = p.MessagesPerSecond > 0 ? $" — {p.MessagesPerSecond:N0} éléments/s" : "";
                var eta = "";
                if (p.MessagesPerSecond > 1 && p.Total > p.Done && (p.Phase.StartsWith("Indexation") || p.Phase.StartsWith("Extraction")))
                {
                    var secs = (p.Total - p.Done) / p.MessagesPerSecond;
                    eta = secs < 90 ? " — moins de 2 min restantes" : $" — environ {Math.Ceiling(secs / 60):N0} min restantes";
                }
                var unit = p.Phase.StartsWith("Lecture") ? "sources" : p.Phase.StartsWith("Extraction") ? "messages avec pièces jointes" : "éléments";
                TxtStatus.Text = $"{p.Phase} : {p.CurrentSource} — {p.Done:N0} / {p.Total:N0} {unit}{rate}{eta}" +
                                 (p.Errors > 0 ? $" — {p.Errors} erreur(s)" : "") +
                                 "  (la recherche est utilisable pendant l'indexation, résultats partiels)";
                if ((DateTime.Now - _lastTreeRefresh).TotalSeconds > 4) RefreshTree();
                _wasIndexing = true;
            }
            else if (_wasIndexing)
            {
                _wasIndexing = false;
                IndexPanel.Visibility = AppServices.Workspace?.HasPendingWork() == true ? Visibility.Visible : Visibility.Collapsed;
                IndexProgress.IsIndeterminate = false;
                TxtStatus.Text = p.Phase + (p.LastError != null ? " — dernière erreur : " + p.LastError : "");
                RefreshTree();
                if (_lastRequest != null && MessageList.SelectedItems.Count == 0) RunSearch(_lastRequest, _lastDescription, _lastTerms);
            }
        }

        // ================================================================== tree

        private void RefreshTree()
        {
            var ws = AppServices.Workspace;
            if (ws == null) return;
            _lastTreeRefresh = DateTime.Now;
            bool hideEmpty = ChkHideEmpty.IsChecked == true;
            var mailboxes = ws.ListMailboxes();
            var all = new TreeNode { Key = "all", Kind = NodeKind.All, Name = "Toutes les boîtes", Count = mailboxes.Sum(m => m.MessageCount), IsExpanded = true };
            foreach (var mb in mailboxes)
            {
                var mbNode = new TreeNode { Key = "mb:" + mb.Id, Kind = NodeKind.Mailbox, MailboxId = mb.Id, Name = mb.Name, Count = mb.MessageCount, IsExpanded = true };
                var roots = ws.GetFolderTree(mb.Id, hideEmpty);
                // Skip technical wrapper levels ("Top of Personal Folders", "Root - Mailbox/IPM_SUBTREE"…):
                // an empty folder that is the only top-level folder is replaced by its sub-folders.
                while (roots.Count == 1 && roots[0].Count == 0 && roots[0].Children.Count > 0) roots = roots[0].Children;
                foreach (var f in roots) mbNode.Children.Add(ToNode(f, mb.Id));
                all.Children.Add(mbNode);
            }
            TreeNode.Merge(_treeRoots, new List<TreeNode> { all }, null);
        }

        private static TreeNode ToNode(FolderNode f, long mailboxId)
        {
            var n = new TreeNode { Key = $"f:{mailboxId}:{f.Path}", Kind = NodeKind.Folder, MailboxId = mailboxId, FolderPath = f.Path, Name = f.Name, Count = f.Count };
            foreach (var c in f.Children) n.Children.Add(ToNode(c, mailboxId));
            return n;
        }

        private void HideEmpty_Click(object sender, RoutedEventArgs e)
        {
            _settings.HideEmptyFolders = ChkHideEmpty.IsChecked == true;
            _settings.Save();
            RefreshTree();
        }

        private TreeNode SelectedNode => Tree.SelectedItem as TreeNode;

        private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (_suppressTreeBrowse) return;
            if (!(e.NewValue is TreeNode node)) return;
            Browse(node);
        }

        /// <summary>Shows the content of a tree node (no text query), applying the date / attachment filters.</summary>
        private void Browse(TreeNode node)
        {
            var req = new SearchRequest
            {
                MailboxId = node.MailboxId,
                FolderPath = node.FolderPath,
                IncludeSubfolders = node.Kind != NodeKind.Folder,
                DateFrom = DateFrom.SelectedDate,
                DateTo = DateTo.SelectedDate,
                OnlyWithAttachments = ChkAtt.IsChecked == true,
                HideDuplicates = node.Kind != NodeKind.Folder && ChkDedup.IsChecked == true,
                SortBy = _sortBy, SortDescending = _sortDesc,
            };
            var desc = node.Kind == NodeKind.Folder ? $"{node.Name}" : node.Kind == NodeKind.Mailbox ? $"Boîte {node.Name}" : "Toutes les boîtes";
            RunSearch(req, desc, new List<string>());
        }

        // ================================================================== search

        private void FocusSearch_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (!SearchBar.IsEnabled) return;
            TxtQuery.Focus();
            TxtQuery.SelectAll();
        }

        private void TxtQuery_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { Search_Click(null, null); e.Handled = true; }
            else if (e.Key == Key.Escape) { TxtQuery.Clear(); e.Handled = true; }
        }

        private void Search_Click(object sender, RoutedEventArgs e)
        {
            if (AppServices.Workspace == null) return;
            var q = TxtQuery.Text.Trim();
            var node = SelectedNode;
            var req = new SearchRequest
            {
                Query = q,
                DateFrom = DateFrom.SelectedDate,
                DateTo = DateTo.SelectedDate,
                OnlyWithAttachments = ChkAtt.IsChecked == true,
                HideDuplicates = ChkDedup.IsChecked == true,
                SortBy = _sortBy, SortDescending = _sortDesc,
            };
            string where = "toutes les boîtes";
            if (CmbScope.SelectedIndex >= 1 && node != null && node.MailboxId.HasValue)
            {
                req.MailboxId = node.MailboxId;
                var mbName = node.Kind == NodeKind.Mailbox ? node.Name : FindMailboxNode(node)?.Name;
                where = "boîte " + mbName;
                if (CmbScope.SelectedIndex == 2 && node.Kind == NodeKind.Folder)
                {
                    req.FolderPath = node.FolderPath;
                    req.IncludeSubfolders = true;
                    where = $"dossier « {node.Name} » ({mbName})";
                }
            }
            var parsed = QueryParser.Parse(q);
            var desc = string.IsNullOrEmpty(q) ? $"Filtre sur {where}" : $"« {q} » dans {where}";
            RunSearch(req, desc, parsed.HighlightTerms);
        }

        private static TreeNode FindMailboxNode(TreeNode n)
        {
            while (n != null && n.Kind != NodeKind.Mailbox) n = n.Parent;
            return n;
        }

        private void ClearSearch_Click(object sender, RoutedEventArgs e)
        {
            TxtQuery.Clear();
            DateFrom.SelectedDate = null;
            DateTo.SelectedDate = null;
            ChkAtt.IsChecked = false;
            var node = SelectedNode;
            if (node != null) Browse(node);
        }

        private async void RunSearch(SearchRequest req, string description, List<string> terms, bool append = false)
        {
            var svc = AppServices.Search;
            if (svc == null) return;
            int seq = ++_searchSeq;
            _lastRequest = req;
            _lastDescription = description;
            _lastTerms = terms ?? new List<string>();
            if (!append) _lastQuery = req.Query ?? "";
            if (!append) TxtResults.Text = description + " — recherche…";
            BtnMore.Visibility = Visibility.Collapsed;
            SearchResult res;
            try
            {
                res = await Task.Run(() => svc.Search(req));
            }
            catch (Exception ex)
            {
                if (seq == _searchSeq) TxtResults.Text = "Erreur : " + ex.Message;
                return;
            }
            if (seq != _searchSeq) return; // superseded
            if (res.Error != null)
            {
                TxtResults.Text = res.Error;
                if (!append) _rows.Clear();
                return;
            }
            long? keepId = append ? null : (MessageList.SelectedItem as RowItem)?.Id;
            if (!append) _rows.Clear();
            foreach (var r in res.Rows) _rows.Add(new RowItem(r));
            var shown = _rows.Count;
            TxtResults.Text = $"{description} — {res.Total:N0} élément(s)" + (shown < res.Total ? $" ({shown:N0} affichés)" : "") +
                              (req.HideDuplicates ? ", doublons masqués" : "") + $"  ·  {res.ElapsedMs} ms";
            TxtResults.ToolTip = TxtResults.Text;
            BtnMore.Visibility = res.HasMore ? Visibility.Visible : Visibility.Collapsed;
            if (!append)
            {
                var keep = keepId.HasValue ? _rows.FirstOrDefault(r => r.Id == keepId.Value) : null;
                if (keep != null) { MessageList.SelectedItem = keep; MessageList.ScrollIntoView(keep); }
                else
                {
                    Viewer.Clear(res.Total == 0 ? "Aucun résultat." : null);
                    if (_rows.Count > 0) MessageList.ScrollIntoView(_rows[0]);
                }
            }
        }

        private void More_Click(object sender, RoutedEventArgs e)
        {
            if (_lastRequest == null) return;
            var r = _lastRequest;
            var next = new SearchRequest
            {
                Query = r.Query, MailboxId = r.MailboxId, FolderPath = r.FolderPath, IncludeSubfolders = r.IncludeSubfolders,
                DateFrom = r.DateFrom, DateTo = r.DateTo, OnlyWithAttachments = r.OnlyWithAttachments, HideDuplicates = r.HideDuplicates,
                SortBy = r.SortBy, SortDescending = r.SortDescending, Limit = 2000, Offset = _rows.Count,
            };
            var desc = _lastDescription;
            var terms = _lastTerms;
            RunSearch(next, desc, terms, append: true);
            _lastRequest = r; // keep the original request for exports and refreshes
        }

        // ================================================================== list & reader

        private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            BtnEmlSel.IsEnabled = MessageList.SelectedItems.Count > 0;
            _selectionTimer.Stop();
            _selectionTimer.Start();
        }

        private void ShowSelectedMessage()
        {
            if (MessageList.SelectedItems.Count != 1 || !(MessageList.SelectedItem is RowItem row))
            {
                if (MessageList.SelectedItems.Count > 1) Viewer.Clear($"{MessageList.SelectedItems.Count} messages sélectionnés");
                return;
            }
            if (Viewer.CurrentRef != null && Viewer.CurrentRef.Id == row.Id && !Viewer.CurrentRef.IsEmbedded) return;
            _ = Viewer.ShowAsync(new MessageRef(row.Id), _lastTerms, _lastQuery);
        }

        private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject d && FindParent<DataGridRow>(d) != null && MessageList.SelectedItem is RowItem row)
                MessageWindow.Open(new MessageRef(row.Id), _lastTerms, this, _lastQuery);
        }

        private void List_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && MessageList.SelectedItem is RowItem row)
            {
                MessageWindow.Open(new MessageRef(row.Id), _lastTerms, this, _lastQuery);
                e.Handled = true;
            }
        }

        private static T FindParent<T>(DependencyObject d) where T : DependencyObject
        {
            while (d != null && !(d is T)) d = System.Windows.Media.VisualTreeHelper.GetParent(d);
            return d as T;
        }

        private void OpenInWindow_Click(object sender, RoutedEventArgs e)
        {
            foreach (var row in MessageList.SelectedItems.OfType<RowItem>().Take(10))
                MessageWindow.Open(new MessageRef(row.Id), _lastTerms, this, _lastQuery);
        }

        private void ShowFolder_Click(object sender, RoutedEventArgs e)
        {
            if (!(MessageList.SelectedItem is RowItem row)) return;
            var key = $"f:{row.Row.MailboxId}:{row.Row.FolderPath}";
            var node = _treeRoots.SelectMany(r => new[] { r }.Concat(r.Descendants())).FirstOrDefault(n => n.Key == key);
            if (node == null) return;
            for (var p = node.Parent; p != null; p = p.Parent) p.IsExpanded = true;
            node.IsSelected = true; // browses the folder
        }

        private void CopyRows_Click(object sender, RoutedEventArgs e)
        {
            var sb = new StringBuilder();
            foreach (var r in MessageList.SelectedItems.OfType<RowItem>())
                sb.Append($"{r.Date:dd/MM/yyyy HH:mm}\t{r.From}\t{r.Subject}\t{r.Mailbox}\t{r.Row.FolderPath}\r\n");
            try { Clipboard.SetText(sb.ToString()); } catch { }
        }

        // ================================================================== exports

        private async void ExportCsv_Click(object sender, RoutedEventArgs e)
        {
            if (_lastRequest == null || AppServices.Search == null) return;
            var dlg = new SaveFileDialog
            {
                Title = "Exporter la liste des résultats",
                Filter = "CSV (Excel) (*.csv)|*.csv",
                FileName = $"resultats-{DateTime.Now:yyyyMMdd-HHmm}.csv",
            };
            if (dlg.ShowDialog(this) != true) return;
            var req = _lastRequest;
            var svc = AppServices.Search;
            SetStatus("Export CSV en cours…");
            try
            {
                int n = await Task.Run(() => SearchService.ExportCsv(svc.SearchAll(req), dlg.FileName));
                SetStatus($"{n:N0} ligne(s) exportée(s) vers {dlg.FileName}");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Export impossible :\n" + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void ExportEmlSelection_Click(object sender, RoutedEventArgs e)
        {
            var rows = MessageList.SelectedItems.OfType<RowItem>().ToList();
            if (rows.Count == 0 || AppServices.Messages == null) return;
            var dlg = new OpenFolderDialog { Title = $"Dossier de destination pour {rows.Count} message(s) (.eml)" };
            if (dlg.ShowDialog(this) != true) return;
            var folder = dlg.FolderName;
            var svc = AppServices.Messages;
            int ok = 0, failed = 0;
            SetStatus($"Export de {rows.Count} message(s)…");
            await Task.Run(() =>
            {
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in rows)
                {
                    try
                    {
                        var name = (r.Date.HasValue ? r.Date.Value.ToString("yyyy-MM-dd_HHmm") + " - " : "") + EmlWriter.SafeFileName(r.Subject ?? "message") + ".eml";
                        svc.ExportEml(new MessageRef(r.Id), Controls.MessageViewer.UniquePath(folder, name, used));
                        ok++;
                    }
                    catch { failed++; }
                }
            });
            SetStatus($"{ok} message(s) exporté(s) vers {folder}" + (failed > 0 ? $" — {failed} échec(s)" : ""));
        }

        // ================================================================== misc

        private void Help_Click(object sender, RoutedEventArgs e)
        {
            const string help =
@"PST BROWSER — AIDE

DOSSIER D'AFFAIRE
  Un dossier d'affaire contient l'index de recherche (pstbrowser-index.db) d'un ensemble de fichiers.
  Les fichiers PST, OST et MSG restent à leur emplacement, ouverts en lecture seule, jamais modifiés.
  L'index contient le texte des messages : placez le dossier d'affaire sur un support protégé
  (volume chiffré BitLocker par exemple) et supprimez-le en fin d'affaire.

AJOUTER DES SOURCES
  « Ajouter des fichiers » : PST, OST ou MSG. « Ajouter un dossier » : tous les PST/OST du dossier
  et de ses sous-dossiers, et tous les MSG (l'arborescence des MSG est conservée). Glisser-déposer possible.
  Les parties d'une boîte découpée (ex. dupont@x.fr.pst, dupont@x.fr_1.pst) sont regroupées automatiquement
  dans une même boîte ; le regroupement se corrige dans « Sources… ».

RECHERCHE (Ctrl+F)
  mots simples          tous les mots doivent être présents (casse et accents ignorés)
  ""expression exacte""   expression exacte
  contra*               mots commençant par « contra »
  devis OR facture      l'un ou l'autre (OU fonctionne aussi)
  -newsletter           exclut les messages contenant ce mot
  de:dupont             expéditeur (nom ou adresse)
  à:martin              destinataires (À, Cc, Cci)
  objet:contrat         objet
  corps:confidentiel    corps du message
  pj:pdf                nom des pièces jointes
  pjtexte:avenant       contenu des pièces jointes uniquement (PDF, Word, Excel, PowerPoint, texte…)
  apres:2023-01-01  avant:2023-12-31   période (ou utilisez les champs « du / au »)

  La recherche porte sur l'objet, l'expéditeur, les destinataires, le corps, les noms et le contenu des pièces jointes
  et le contenu des messages joints (mails transférés en pièce jointe).
  Sans champ, chaque mot doit se trouver dans le message OU dans l'une de ses pièces jointes :
  « budget signature » trouve un message qui parle du budget dont la pièce jointe contient « signature ».
  de: à: objet: corps: ne regardent que le message ; pj: que les noms de fichiers ; pjtexte: que le contenu des fichiers.
  Un -mot exclut le message si le mot figure dans le message ou dans l'une de ses pièces jointes.
  Dans la liste, un extrait « 📎 nom : extrait » indique la pièce jointe qui correspond à la recherche.
  Portée : partout (par défaut), boîte sélectionnée, ou dossier sélectionné et ses sous-dossiers.
  « Sans doublons » masque les copies d'un même message (même Message-ID) dans une même boîte.

CONTENU DES PIÈCES JOINTES
  Après les messages, une étape supplémentaire lit les pièces jointes (PDF, Word, Excel, PowerPoint, OpenDocument,
  ZIP, texte, e-mails…) dans un processus séparé et calcule leur empreinte SHA-256. Case à cocher dans « Sources… ».
  Les fichiers de plus de 50 Mo, chiffrés ou d'un format non pris en charge sont signalés mais non lus ;
  les images scannées ne sont pas reconnues (pas d'OCR). L'étape est reprenable après une interruption.
  Dans le lecteur, les pièces jointes qui contiennent les termes recherchés sont signalées (🔎) ;
  clic sur une pièce jointe puis « Aperçu du texte » : texte extrait, termes surlignés.

COLONNES DE LA LISTE
  Clic droit sur un en-tête (ou bouton « Colonnes… ») : afficher ou masquer des colonnes, vues « Standard »
  et « Investigation », « Réinitialiser ». Glisser un en-tête pour changer l'ordre, tirer son bord pour la largeur.
  Un clic sur un en-tête trie la liste (second clic : sens inverse) ; la liste déroulante permet aussi de choisir le tri.
  Les préférences sont enregistrées par utilisateur Windows.

LECTURE
  Les images distantes, scripts et liens sont bloqués : aucune connexion réseau n'est établie.
  Les messages au format RTF sont affichés avec leur mise en forme (polices, couleurs, liens).
  Un clic sur un lien demande confirmation avant de l'ouvrir dans votre navigateur.
  Double-clic ou Entrée : ouvrir le message dans une fenêtre. Les messages joints s'ouvrent d'un clic.
  Ctrl+F dans le message : rechercher dans la page. Clic droit : imprimer.

EXPORTS
  Liste des résultats en CSV (Excel), messages sélectionnés en .eml, pièces jointes une par une ou toutes.";
            new TextWindow("Aide — PST Browser", help) { Owner = this }.Show();
        }

        private void SetStatus(string text) => TxtStatus.Text = text;

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            _settings.Maximized = WindowState == WindowState.Maximized;
            if (WindowState == WindowState.Normal)
            {
                _settings.WindowWidth = Width;
                _settings.WindowHeight = Height;
            }
            _settings.TreeWidth = ColTree.ActualWidth;
            PersistLayoutIfChanged();
            _settings.Save();
            _timer.Stop();
            AppServices.Close();
        }
    }
}
