using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using PstBrowser.Core.Search;
using PstBrowser.Desktop.Services;
using PstBrowser.Desktop.Windows;

namespace PstBrowser.Desktop
{
    /// <summary>Configurable columns of the message list and the sort state (header click or sort box).</summary>
    public partial class MainWindow
    {
        private sealed class SortChoice
        {
            public string Label { get; set; }
            public string Field { get; set; }
            public bool Descending { get; set; }
            public override string ToString() => Label;
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
        private bool _buildingColumns, _suppressSortEvent;

        private void InitColumns()
        {
            _layout = ColumnCatalog.Normalize(_settings.ListColumns, _settings.ColumnPreset);
            BuildColumns();
            _savedLayoutJson = JsonSerializer.Serialize(_layout);
            CmbSort.ItemsSource = FixedSorts.ToList();
            CmbSort.SelectedIndex = 0;
        }

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

        private static readonly IBrush SnippetBrush = new SolidColorBrush(Color.FromRgb(0x6B, 0x6F, 0x76));

        private static DataGridColumn CreateColumn(ColumnDef def)
        {
            DataGridColumn col;
            if (def.SubjectWithSnippet)
            {
                col = new DataGridTemplateColumn
                {
                    CellTemplate = new FuncDataTemplate<RowItem>((row, _) => new StackPanel
                    {
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Avalonia.Thickness(4, 3),
                        Children =
                        {
                            new TextBlock { [!TextBlock.TextProperty] = new Binding(nameof(RowItem.SubjectText)), TextTrimming = TextTrimming.CharacterEllipsis },
                            new TextBlock
                            {
                                [!TextBlock.TextProperty] = new Binding(nameof(RowItem.Snippet)),
                                [!Avalonia.Visual.IsVisibleProperty] = new Binding(nameof(RowItem.HasSnippet)),
                                TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 11, Foreground = SnippetBrush,
                            },
                        },
                    }),
                };
            }
            else col = new DataGridTextColumn { Binding = new Binding(def.Binding) };
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
                st.Width = c.Width.IsStar || c.ActualWidth <= 0 ? (double?)null : Math.Round(c.ActualWidth);
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

        private async void Columns_Click(object sender, RoutedEventArgs e)
        {
            CaptureLayout();
            var dlg = new ColumnsWindow(_layout);
            await dlg.ShowDialog(this);
            if (dlg.Result != null) ApplyLayout(dlg.Result);
        }

        // ================================================================== sort

        /// <summary>Selects the sort in the box (adding a "Tri : …" entry when it is not one of the fixed sorts).</summary>
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

        /// <summary>Arrow in the header of the sorted column (the list is sorted by the engine, not by the grid).</summary>
        private void UpdateSortGlyphs()
        {
            foreach (var c in MessageList.Columns)
            {
                if (!_columnKeys.TryGetValue(c, out var key)) continue;
                var def = ColumnCatalog.Find(key);
                c.Header = def.SortKey != null && def.SortKey == _sortBy ? def.Header + (_sortDesc ? " ▼" : " ▲") : def.Header;
            }
        }

        private void CmbSort_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSortEvent || !IsLoaded || !(CmbSort.SelectedItem is SortChoice choice)) return;
            _sortBy = choice.Field; _sortDesc = choice.Descending;
            UpdateSortGlyphs();
            RerunLastSearch();
        }

        private void List_Sorting(object sender, DataGridColumnEventArgs e)
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
