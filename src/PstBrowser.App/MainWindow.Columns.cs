using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using PstBrowser.App.Services;
using PstBrowser.App.Windows;
using PstBrowser.Core.Search;

namespace PstBrowser.App
{
    /// <summary>Configurable columns of the message list and the sort state (header click or sort box).</summary>
    public partial class MainWindow
    {
        private sealed class SortChoice
        {
            public string Label { get; set; }
            public string Field { get; set; }
            public bool Descending { get; set; }
        }

        private static readonly SortChoice[] FixedSorts =
        {
            new SortChoice { Label = "Plus récents d'abord", Field = "date", Descending = true },
            new SortChoice { Label = "Plus anciens d'abord", Field = "date", Descending = false },
            new SortChoice { Label = "Pertinence", Field = SortFields.Relevance, Descending = false },
            new SortChoice { Label = "Expéditeur (A→Z)", Field = "sender", Descending = false },
            new SortChoice { Label = "Objet (A→Z)", Field = "subject", Descending = false },
            new SortChoice { Label = "Taille (décroissante)", Field = "size", Descending = true },
        };

        private List<ColumnState> _layout = new List<ColumnState>();
        private readonly Dictionary<DataGridColumn, string> _columnKeys = new Dictionary<DataGridColumn, string>();
        private string _savedLayoutJson = "";
        private string _sortBy = "date";
        private bool _sortDesc = true;
        private bool _buildingColumns;

        private void InitColumns()
        {
            _layout = ColumnCatalog.Normalize(_settings.ListColumns, _settings.ColumnPreset);
            BuildColumns();
            _savedLayoutJson = JsonSerializer.Serialize(_layout);
            FillSortBox();
        }

        // ================================================================== columns

        private void BuildColumns()
        {
            _buildingColumns = true;
            try
            {
                MessageList.Columns.Clear();
                _columnKeys.Clear();
                foreach (var state in _layout.Where(l => l.Visible))
                {
                    var def = ColumnCatalog.Find(state.Key);
                    if (def == null) continue;
                    var col = CreateColumn(def);
                    col.Width = state.Width.HasValue ? new DataGridLength(Math.Max(def.MinWidth, state.Width.Value))
                              : def.Stretch ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(def.Width);
                    MessageList.Columns.Add(col);
                    _columnKeys[col] = def.Key;
                }
            }
            finally { _buildingColumns = false; }
            UpdateSortGlyphs();
        }

        private static DataGridColumn CreateColumn(ColumnDef def)
        {
            DataGridColumn col;
            if (def.SubjectWithSnippet)
            {
                var stack = new FrameworkElementFactory(typeof(StackPanel));
                var subject = new FrameworkElementFactory(typeof(TextBlock));
                subject.SetBinding(TextBlock.TextProperty, new Binding("SubjectText"));
                subject.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
                var snippet = new FrameworkElementFactory(typeof(TextBlock));
                snippet.SetBinding(TextBlock.TextProperty, new Binding("Snippet"));
                snippet.SetBinding(UIElement.VisibilityProperty, new Binding("SnippetVisibility"));
                snippet.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
                snippet.SetValue(TextBlock.FontSizeProperty, 11.0);
                snippet.SetValue(TextBlock.ForegroundProperty, new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x6B, 0x6F, 0x76)));
                stack.AppendChild(subject);
                stack.AppendChild(snippet);
                col = new DataGridTemplateColumn { CellTemplate = new DataTemplate { VisualTree = stack } };
            }
            else
            {
                var text = new DataGridTextColumn { Binding = new Binding(def.Binding) };
                var style = new Style(typeof(TextBlock));
                style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
                text.ElementStyle = style;
                col = text;
            }
            col.Header = def.Header;
            col.MinWidth = def.MinWidth;
            col.SortMemberPath = def.SortKey;
            col.CanUserSort = def.SortKey != null;
            return col;
        }

        /// <summary>Reads order and widths back from the grid into the layout (hidden columns keep their place at the end).</summary>
        private void CaptureLayout()
        {
            if (_buildingColumns) return;
            var shown = MessageList.Columns.OrderBy(c => c.DisplayIndex).Where(c => _columnKeys.ContainsKey(c)).ToList();
            var byKey = _layout.ToDictionary(l => l.Key);
            var result = new List<ColumnState>();
            foreach (var c in shown)
            {
                var st = byKey[_columnKeys[c]];
                st.Visible = true;
                st.Width = c.Width.IsStar ? (double?)null : Math.Round(c.ActualWidth);
                result.Add(st);
            }
            result.AddRange(_layout.Where(l => !result.Contains(l)).Select(l => { l.Visible = false; return l; }));
            _layout = result;
        }

        private void PersistLayoutIfChanged()
        {
            CaptureLayout();
            var json = JsonSerializer.Serialize(_layout);
            if (json == _savedLayoutJson) return;
            _savedLayoutJson = json;
            _settings.ListColumns = _layout.Select(l => new ColumnState { Key = l.Key, Visible = l.Visible, Width = l.Width }).ToList();
            _settings.ColumnPreset = ColumnCatalog.MatchingPreset(_layout) ?? "Personnalisé";
            _settings.Save();
        }

        private void ApplyLayout(List<ColumnState> layout)
        {
            _layout = ColumnCatalog.Normalize(layout, ColumnCatalog.Standard);
            BuildColumns();
            PersistLayoutIfChanged();
        }

        private void List_ColumnReordered(object sender, DataGridColumnEventArgs e) => PersistLayoutIfChanged();

        // The width of a column is final when the mouse button is released after dragging a header edge
        private void List_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject d && FindParent<System.Windows.Controls.Primitives.DataGridColumnHeader>(d) != null)
                Dispatcher.BeginInvoke(new Action(PersistLayoutIfChanged), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void Header_RightClick(object sender, MouseButtonEventArgs e)
        {
            CaptureLayout();
            var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            foreach (var l in _layout)
            {
                var def = ColumnCatalog.Find(l.Key);
                var item = new MenuItem { Header = def.Title, IsCheckable = true, IsChecked = l.Visible, StaysOpenOnClick = true };
                var key = l.Key;
                item.Click += (_, __) => ToggleColumn(key, item.IsChecked);
                menu.Items.Add(item);
            }
            menu.Items.Add(new Separator());
            foreach (var name in ColumnCatalog.PresetNames)
            {
                var n = name;
                var mi = new MenuItem { Header = "Vue " + name };
                mi.Click += (_, __) => ApplyLayout(ColumnCatalog.Preset(n));
                menu.Items.Add(mi);
            }
            var reset = new MenuItem { Header = "Réinitialiser" };
            reset.Click += (_, __) => ApplyLayout(ColumnCatalog.Preset(ColumnCatalog.Standard));
            menu.Items.Add(reset);
            var more = new MenuItem { Header = "Colonnes…" };
            more.Click += Columns_Click;
            menu.Items.Add(more);
            menu.IsOpen = true;
            e.Handled = true;
        }

        private void ToggleColumn(string key, bool visible)
        {
            CaptureLayout();
            var st = _layout.First(l => l.Key == key);
            if (!visible && _layout.Count(l => l.Visible) <= 1) return; // keep at least one column
            st.Visible = visible;
            BuildColumns();
            PersistLayoutIfChanged();
        }

        private void Columns_Click(object sender, RoutedEventArgs e)
        {
            CaptureLayout();
            var dlg = new ColumnsWindow(_layout) { Owner = this };
            if (dlg.ShowDialog() == true) ApplyLayout(dlg.Result);
        }

        // ================================================================== sort

        private void FillSortBox()
        {
            CmbSort.ItemsSource = FixedSorts.ToList();
            CmbSort.SelectedIndex = 0;
        }

        /// <summary>Selects the sort in the box (adding a "Colonne : …" entry when it is not one of the fixed sorts).</summary>
        private void SelectSort(string field, bool descending)
        {
            _sortBy = field; _sortDesc = descending;
            var list = FixedSorts.ToList();
            var match = list.FirstOrDefault(c => c.Field == field && (c.Descending == descending || field == SortFields.Relevance));
            if (match == null)
            {
                var def = ColumnCatalog.BySortKey(field);
                match = new SortChoice { Label = $"Tri : {def?.Title ?? field} {(descending ? "↓" : "↑")}", Field = field, Descending = descending };
                list.Add(match);
            }
            _suppressSortEvent = true;
            try
            {
                CmbSort.ItemsSource = list;
                CmbSort.SelectedItem = match;
            }
            finally { _suppressSortEvent = false; }
            UpdateSortGlyphs();
        }

        private void UpdateSortGlyphs()
        {
            foreach (var c in MessageList.Columns)
                c.SortDirection = c.SortMemberPath == _sortBy ? (_sortDesc ? ListSortDirection.Descending : ListSortDirection.Ascending) : (ListSortDirection?)null;
        }

        private void CmbSort_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSortEvent || !IsLoaded || !(CmbSort.SelectedItem is SortChoice choice)) return;
            _sortBy = choice.Field; _sortDesc = choice.Descending;
            UpdateSortGlyphs();
            RerunLastSearch();
        }

        private void List_Sorting(object sender, DataGridSortingEventArgs e)
        {
            e.Handled = true;
            var key = e.Column.SortMemberPath;
            if (string.IsNullOrEmpty(key)) return;
            bool desc = _sortBy == key ? !_sortDesc : SortFields.DefaultDescending(key);
            SelectSort(key, desc);
            RerunLastSearch();
        }

        private void RerunLastSearch()
        {
            if (_lastRequest == null) return;
            var r = _lastRequest;
            r.SortBy = _sortBy;
            r.SortDescending = _sortDesc;
            r.Offset = 0;
            r.Limit = 1000;
            RunSearch(r, _lastDescription, _lastTerms);
        }
    }
}
