using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PstBrowser.Core.Index;
using PstBrowser.Core.Search;
using PstBrowser.Core.Viewer;
using PstBrowser.Desktop.Services;
using PstBrowser.Desktop.Windows;

namespace PstBrowser.Desktop
{
    public partial class MainWindow : Window
    {
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

        public MainWindow()
        {
            InitializeComponent();
            InitColumns();
            Viewer.ThreadRequested += ShowThread;
            Viewer.AnalysisRequested += (mref, subject) => AnalysisWindow.Open(mref, subject, this);
            Viewer.PopOutRequested += (mref, terms) => MessageWindow.Open(mref, terms, this, _lastQuery);
            Tree.ItemsSource = _treeRoots;
            MessageList.ItemsSource = _rows;
            ChkHideEmpty.IsChecked = _settings.HideEmptyFolders;

            if (_settings.WindowWidth.HasValue && _settings.WindowHeight.HasValue)
            {
                Width = Math.Max(MinWidth, _settings.WindowWidth.Value);
                Height = Math.Max(MinHeight, _settings.WindowHeight.Value);
            }
            if (_settings.Maximized) WindowState = WindowState.Maximized;
            MainArea.ColumnDefinitions[0].Width = new GridLength(Math.Max(150, _settings.TreeWidth));
            ApplyReaderLayout(_settings.ReaderBottom ?? false);
            // automatic layout: reading pane below the list on small screens (the screen is known once the window is open)
            if (_settings.ReaderBottom == null)
                Opened += (_, __) => { if ((Screens.ScreenFromWindow(this) ?? Screens.Primary)?.WorkingArea.Width < 1600 * (Screens.Primary?.Scaling ?? 1)) ApplyReaderLayout(true); };

            AddHandler(DragDrop.DragOverEvent, Window_DragOver);
            AddHandler(DragDrop.DropEvent, Window_Drop);
            Closing += Window_Closing;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _timer.Tick += Timer_Tick;
            _timer.Start();
            _selectionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            _selectionTimer.Tick += (_, __) => { _selectionTimer.Stop(); ShowSelectedMessage(); };
            FillRecent();
        }

        /// <summary>Ctrl+F (⌘F on macOS) or Ctrl+E: search box.</summary>
        protected override void OnKeyDown(KeyEventArgs e)
        {
            bool command = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
            if (command && (e.Key == Key.F || e.Key == Key.E) && SearchBar.IsEnabled)
            {
                TxtQuery.Focus();
                TxtQuery.SelectAll();
                e.Handled = true;
                return;
            }
            base.OnKeyDown(e);
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
                ContentGrid.RowDefinitions.Add(new RowDefinition(new GridLength(2, GridUnitType.Star)) { MinHeight = 120 });
                ContentGrid.RowDefinitions.Add(new RowDefinition(new GridLength(5)));
                ContentGrid.RowDefinitions.Add(new RowDefinition(new GridLength(3, GridUnitType.Star)) { MinHeight = 150 });
                Grid.SetRow(ListPanel, 0); Grid.SetRow(ReaderSplitter, 1); Grid.SetRow(Viewer, 2);
                Grid.SetColumn(ListPanel, 0); Grid.SetColumn(ReaderSplitter, 0); Grid.SetColumn(Viewer, 0);
                ReaderSplitter.ResizeDirection = GridResizeDirection.Rows;
                BtnLayout.Content = "Lecture à droite";
                ToolTip.SetTip(BtnLayout, "Afficher le volet de lecture à droite de la liste");
            }
            else
            {
                ContentGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)) { MinWidth = 300 });
                ContentGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(5)));
                ContentGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)) { MinWidth = 300 });
                Grid.SetColumn(ListPanel, 0); Grid.SetColumn(ReaderSplitter, 1); Grid.SetColumn(Viewer, 2);
                Grid.SetRow(ListPanel, 0); Grid.SetRow(ReaderSplitter, 0); Grid.SetRow(Viewer, 0);
                ReaderSplitter.ResizeDirection = GridResizeDirection.Columns;
                BtnLayout.Content = "Lecture en bas";
                ToolTip.SetTip(BtnLayout, "Afficher le volet de lecture sous la liste");
            }
        }

        private void Layout_Click(object sender, RoutedEventArgs e)
        {
            ApplyReaderLayout(!_readerBottom);
            _settings.ReaderBottom = _readerBottom;
            _settings.Save();
        }

        // ================================================================== workspace

        public void OpenInitialWorkspace(string arg)
        {
            if (!string.IsNullOrEmpty(arg) && Directory.Exists(arg) && Workspace.Exists(arg)) { _ = OpenWorkspace(arg); return; }
            var last = _settings.RecentWorkspaces.FirstOrDefault(Workspace.Exists);
            if (last != null) _ = OpenWorkspace(last);
        }

        private void FillRecent()
        {
            RecentList.Children.Clear();
            foreach (var p in _settings.RecentWorkspaces.Where(Directory.Exists).Take(6))
            {
                var path = p;
                var b = new Button { Content = path, Margin = new Thickness(0, 2), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
                b.Classes.Add("link");
                b.Click += (_, __) => _ = OpenWorkspace(path);
                RecentList.Children.Add(b);
            }
            RecentTitle.IsVisible = RecentList.Children.Count > 0;
        }

        private async Task OpenWorkspace(string folder)
        {
            Workspace ws;
            try { ws = Workspace.OpenOrCreate(folder); }
            catch (Exception ex)
            {
                await Dialogs.Info(this, Title, "Impossible d'ouvrir le dossier d'affaire :\n" + ex.Message);
                return;
            }
            AppServices.Open(ws, _indexOptions);
            _settings.AddRecent(folder);
            TxtCaseName.Text = Path.GetFileName(folder.TrimEnd('/', '\\'));
            Title = $"{TxtCaseName.Text} — PST Browser";
            Welcome.IsVisible = false;
            MainArea.IsVisible = true;
            SearchBar.IsEnabled = BtnAddFiles.IsEnabled = BtnAddFolder.IsEnabled = BtnSources.IsEnabled = BtnCsv.IsEnabled = true;
            _treeRoots.Clear();
            _rows.Clear();
            Viewer.Clear();
            RefreshTree();
            if (_treeRoots.Count > 0) Tree.SelectedItem = _treeRoots[0]; // browse everything
            if (!ws.ListSources().Any())
            {
                Viewer.Clear("Ce dossier d'affaire est vide.\nAjoutez des fichiers PST/MSG (boutons en haut, ou glisser-déposer).");
                SetStatus("Dossier d'affaire prêt : " + folder);
            }
            else SetStatus("Dossier d'affaire : " + folder);
            if (ws.AutoRelocation != null)
                SetStatus($"Dossier d'affaire déplacé : {ws.AutoRelocation.Relocated} source(s) retrouvée(s) automatiquement à son nouvel emplacement.");
            int missing = ws.ListSources().Count(s => s.FileMissing);
            if (missing > 0 && await Dialogs.Confirm(this, Title,
                    $"{missing} source(s) de ce dossier d'affaire sont introuvables (déplacées, autre disque, disque non branché…).\n\n" +
                    "Indiquer leur nouvel emplacement maintenant ? Il suffit d'en retrouver une : les sources déplacées avec elle sont retrouvées en même temps.",
                    "Oui", "Non"))
                await ShowSources();
            if (ws.HasPendingWork()) StartIndexing();
        }

        private void CloseWorkspace()
        {
            AppServices.Close();
            _treeRoots.Clear();
            _rows.Clear();
            Viewer.Clear();
            MainArea.IsVisible = false;
            Welcome.IsVisible = true;
            SearchBar.IsEnabled = BtnAddFiles.IsEnabled = BtnAddFolder.IsEnabled = BtnSources.IsEnabled = BtnCsv.IsEnabled = BtnEmlSel.IsEnabled = false;
            TxtCaseName.Text = "Dossier d'affaire";
            Title = "PST Browser";
            TxtResults.Text = "";
            SetStatus("");
            FillRecent();
        }

        private async Task<string> PickFolder(string title)
        {
            var r = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title });
            return r.FirstOrDefault()?.TryGetLocalPath();
        }

        private async void NewCase_Click(object sender, RoutedEventArgs e)
        {
            var folder = await PickFolder("Choisissez (ou créez) un dossier vide pour le nouveau dossier d'affaire");
            if (folder == null) return;
            if (!Workspace.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any() &&
                !await Dialogs.Confirm(this, Title, "Ce dossier n'est pas vide. Y créer quand même l'index du dossier d'affaire ?\n(Les fichiers existants ne seront pas modifiés.)"))
                return;
            await OpenWorkspace(folder);
        }

        private async void OpenCase_Click(object sender, RoutedEventArgs e)
        {
            var folder = await PickFolder("Ouvrir un dossier d'affaire (dossier contenant " + Workspace.DbFileName + ")");
            if (folder == null) return;
            if (!Workspace.Exists(folder))
            {
                await Dialogs.Info(this, Title, "Ce dossier ne contient pas d'index PST Browser (" + Workspace.DbFileName + ").\nUtilisez « Nouveau dossier d'affaire » pour en créer un.");
                return;
            }
            await OpenWorkspace(folder);
        }

        private void BtnCase_Click(object sender, RoutedEventArgs e)
        {
            var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
            void Add(string header, Action action, bool enabled = true)
            {
                var mi = new MenuItem { Header = header, IsEnabled = enabled };
                mi.Click += (_, __) => action();
                menu.Items.Add(mi);
            }
            Add("Nouveau dossier d'affaire…", () => NewCase_Click(null, null));
            Add("Ouvrir un dossier d'affaire…", () => OpenCase_Click(null, null));
            var recents = _settings.RecentWorkspaces.Where(Workspace.Exists).Take(6).ToList();
            if (recents.Count > 0)
            {
                menu.Items.Add(new Separator());
                foreach (var r in recents) { var path = r; Add(path, () => _ = OpenWorkspace(path)); }
            }
            menu.Items.Add(new Separator());
            Add(OperatingSystem.IsMacOS() ? "Afficher dans le Finder" : "Ouvrir l'emplacement", () =>
            {
                try { Process.Start(new ProcessStartInfo(AppServices.Workspace.Folder) { UseShellExecute = true }); } catch { }
            }, AppServices.Workspace != null);
            Add("Fermer le dossier d'affaire", CloseWorkspace, AppServices.Workspace != null);
            menu.ShowAt(BtnCase);
        }

        // ================================================================== sources

        private async void AddFiles_Click(object sender, RoutedEventArgs e)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Ajouter des fichiers PST, OST ou MSG",
                AllowMultiple = true,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Fichiers Outlook (.pst, .ost, .msg)") { Patterns = new[] { "*.pst", "*.ost", "*.msg", "*.PST", "*.OST", "*.MSG" } },
                    FilePickerFileTypes.All,
                },
            });
            var paths = files.Select(f => f.TryGetLocalPath()).Where(p => p != null).ToList();
            if (paths.Count > 0) await AddSources(paths);
        }

        private async void AddFolder_Click(object sender, RoutedEventArgs e)
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Ajouter un dossier (PST/OST et MSG, sous-dossiers compris)", AllowMultiple = true });
            var paths = folders.Select(f => f.TryGetLocalPath()).Where(p => p != null).ToList();
            if (paths.Count > 0) await AddSources(paths);
        }

        private async Task AddSources(IEnumerable<string> paths)
        {
            var ws = AppServices.Workspace;
            if (ws == null) return;
            var list = paths.ToList();
            SetStatus("Recherche des fichiers PST/MSG…");
            Cursor = new Cursor(StandardCursorType.Wait);
            List<SourceInfo> added;
            try { added = await Task.Run(() => ws.AddSources(list)); }
            catch (Exception ex)
            {
                await Dialogs.Info(this, Title, "Ajout impossible :\n" + ex.Message);
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
            e.DragEffects = e.DataTransfer.Contains(DataFormat.File) && AppServices.Workspace != null ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private async void Window_Drop(object sender, DragEventArgs e)
        {
            var files = e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).Where(p => p != null).ToList();
            if (files == null || files.Count == 0) return;
            if (AppServices.Workspace == null)
            {
                await Dialogs.Info(this, Title, "Créez ou ouvrez d'abord un dossier d'affaire.");
                return;
            }
            await AddSources(files);
        }

        private async void Sources_Click(object sender, RoutedEventArgs e) => await ShowSources();

        private async Task ShowSources()
        {
            if (AppServices.Workspace == null) return;
            var w = new SourcesWindow();
            await w.ShowDialog(this);
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
            if (ix == null) { IndexPanel.IsVisible = false; return; }
            var p = ix.Progress;
            if (p.Running)
            {
                IndexPanel.IsVisible = true;
                IndexProgress.IsIndeterminate = p.Total == 0;
                IndexProgress.Maximum = Math.Max(1, p.Total);
                IndexProgress.Value = Math.Min(p.Done, p.Total);
                var rate = p.MessagesPerSecond > 0 ? $" — {p.MessagesPerSecond:N0} éléments/s" : "";
                var eta = "";
                if (p.MessagesPerSecond > 1 && p.Total > p.Done && (p.Phase.StartsWith("Indexation") || p.Phase.StartsWith("Extraction") || p.Phase.StartsWith("Analyse") || p.Phase.StartsWith("Détection")))
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
                IndexPanel.IsVisible = AppServices.Workspace?.HasPendingWork() == true;
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

        private void Tree_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SelectedNode is TreeNode node) Browse(node);
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
            var desc = node.Kind == NodeKind.Folder ? node.Name : node.Kind == NodeKind.Mailbox ? $"Boîte {node.Name}" : "Toutes les boîtes";
            RunSearch(req, desc, new List<string>());
        }

        // ================================================================== search

        private void TxtQuery_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { Search_Click(null, null); e.Handled = true; }
            else if (e.Key == Key.Escape) { TxtQuery.Text = ""; e.Handled = true; }
        }

        private void Search_Click(object sender, RoutedEventArgs e)
        {
            if (AppServices.Workspace == null) return;
            var q = (TxtQuery.Text ?? "").Trim();
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
            TxtQuery.Text = "";
            DateFrom.SelectedDate = null;
            DateTo.SelectedDate = null;
            ChkAtt.IsChecked = false;
            if (SelectedNode is TreeNode node) Browse(node);
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
            BtnMore.IsVisible = false;
            SearchResult res;
            try { res = await Task.Run(() => svc.Search(req)); }
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
            ToolTip.SetTip(TxtResults, TxtResults.Text);
            BtnMore.IsVisible = res.HasMore;
            if (!append)
            {
                var keep = keepId.HasValue ? _rows.FirstOrDefault(r => r.Id == keepId.Value) : null;
                if (keep != null) { MessageList.SelectedItem = keep; MessageList.ScrollIntoView(keep, null); }
                else
                {
                    Viewer.Clear(res.Total == 0 ? "Aucun résultat." : null);
                    if (_rows.Count > 0) MessageList.ScrollIntoView(_rows[0], null);
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
            RunSearch(next, _lastDescription, _lastTerms, append: true);
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

        private void List_DoubleTapped(object sender, TappedEventArgs e)
        {
            if (MessageList.SelectedItem is RowItem row) MessageWindow.Open(new MessageRef(row.Id), _lastTerms, this, _lastQuery);
        }

        private void List_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && MessageList.SelectedItem is RowItem row)
            {
                MessageWindow.Open(new MessageRef(row.Id), _lastTerms, this, _lastQuery);
                e.Handled = true;
            }
        }

        private void OpenInWindow_Click(object sender, RoutedEventArgs e)
        {
            foreach (var row in MessageList.SelectedItems.OfType<RowItem>().Take(10))
                MessageWindow.Open(new MessageRef(row.Id), _lastTerms, this, _lastQuery);
        }

        /// <summary>Lists every message of a conversation thread (all mailboxes), oldest first.</summary>
        private void ShowThread(long threadId)
        {
            if (AppServices.Workspace == null) return;
            CmbScope.SelectedIndex = 0;
            TxtQuery.Text = "fil:" + threadId;
            SelectSort("date", false);
            Search_Click(null, null);
        }

        private void ShowThreadRow_Click(object sender, RoutedEventArgs e)
        {
            if (!(MessageList.SelectedItem is RowItem row)) return;
            if (row.Row.ThreadId.HasValue && row.Row.ThreadCount > 1) ShowThread(row.Row.ThreadId.Value);
            else SetStatus("Ce message est seul dans son fil de conversation (ou les fils ne sont pas encore calculés).");
        }

        private void AnalyseRow_Click(object sender, RoutedEventArgs e)
        {
            if (MessageList.SelectedItem is RowItem row) AnalysisWindow.Open(new MessageRef(row.Id), row.Subject, this);
        }

        private void ShowFolder_Click(object sender, RoutedEventArgs e)
        {
            if (!(MessageList.SelectedItem is RowItem row)) return;
            var key = $"f:{row.Row.MailboxId}:{row.Row.FolderPath}";
            var node = _treeRoots.SelectMany(r => new[] { r }.Concat(r.Descendants())).FirstOrDefault(n => n.Key == key);
            if (node == null) return;
            for (var p = node.Parent; p != null; p = p.Parent) p.IsExpanded = true;
            Tree.SelectedItem = node; // browses the folder
        }

        private async void CopyRows_Click(object sender, RoutedEventArgs e)
        {
            var sb = new StringBuilder();
            foreach (var r in MessageList.SelectedItems.OfType<RowItem>())
                sb.Append($"{r.Date:dd/MM/yyyy HH:mm}\t{r.From}\t{r.Subject}\t{r.Mailbox}\t{r.Row.FolderPath}\n");
            try { await Clipboard.SetTextAsync(sb.ToString()); } catch { }
        }

        // ================================================================== exports

        private async void ExportCsv_Click(object sender, RoutedEventArgs e)
        {
            if (_lastRequest == null || AppServices.Search == null) return;
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Exporter la liste des résultats",
                SuggestedFileName = $"resultats-{DateTime.Now:yyyyMMdd-HHmm}.csv",
                DefaultExtension = "csv",
                FileTypeChoices = new[] { new FilePickerFileType("CSV (Excel, LibreOffice, Numbers)") { Patterns = new[] { "*.csv" } } },
            });
            var path = file?.TryGetLocalPath();
            if (path == null) return;
            var req = _lastRequest;
            var svc = AppServices.Search;
            SetStatus("Export CSV en cours…");
            try
            {
                int n = await Task.Run(() => SearchService.ExportCsv(svc.SearchAll(req), path));
                SetStatus($"{n:N0} ligne(s) exportée(s) vers {path}");
            }
            catch (Exception ex) { await Dialogs.Info(this, Title, "Export impossible :\n" + ex.Message); }
        }

        private async void ExportEmlSelection_Click(object sender, RoutedEventArgs e)
        {
            var rows = MessageList.SelectedItems.OfType<RowItem>().ToList();
            if (rows.Count == 0 || AppServices.Messages == null) return;
            var folder = await PickFolder($"Dossier de destination pour {rows.Count} message(s) (.eml)");
            if (folder == null) return;
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

        private void Help_Click(object sender, RoutedEventArgs e) => TextWindow.Open("Aide — PST Browser", HelpText.Text, this);

        private void SetStatus(string text) => TxtStatus.Text = text;

        private void Window_Closing(object sender, WindowClosingEventArgs e)
        {
            _settings.Maximized = WindowState == WindowState.Maximized;
            if (WindowState == WindowState.Normal)
            {
                _settings.WindowWidth = Width;
                _settings.WindowHeight = Height;
            }
            _settings.TreeWidth = MainArea.ColumnDefinitions[0].ActualWidth;
            PersistLayoutIfChanged();
            _settings.Save();
            _timer.Stop();
        }
    }
}
