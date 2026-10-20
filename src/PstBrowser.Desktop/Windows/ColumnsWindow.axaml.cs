using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using PstBrowser.Desktop.Services;

namespace PstBrowser.Desktop.Windows
{
    /// <summary>Column chooser: visibility, order, preset views and reset.</summary>
    public partial class ColumnsWindow : Window
    {
        public sealed class Item : INotifyPropertyChanged
        {
            private bool _visible;
            public string Key { get; set; }
            public string Title { get; set; }
            public double? Width { get; set; }
            public bool Visible
            {
                get => _visible;
                set { _visible = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Visible))); }
            }
            public event PropertyChangedEventHandler PropertyChanged;
        }

        private readonly List<Item> _items;

        /// <summary>The layout chosen by the user (null if the dialog was cancelled).</summary>
        public List<ColumnState> Result { get; private set; }

        public ColumnsWindow() : this(ColumnCatalog.Preset(ColumnCatalog.Standard)) { }

        public ColumnsWindow(IEnumerable<ColumnState> current)
        {
            InitializeComponent();
            _items = Build(current);
            ColumnList.ItemsSource = _items;
        }

        private static List<Item> Build(IEnumerable<ColumnState> layout)
            => layout.Select(c => new Item { Key = c.Key, Title = ColumnCatalog.Find(c.Key)?.Title ?? c.Key, Visible = c.Visible, Width = c.Width }).ToList();

        private void Refresh(List<Item> items, int select = -1)
        {
            _items.Clear();
            _items.AddRange(items);
            ColumnList.ItemsSource = null;
            ColumnList.ItemsSource = _items;
            if (select >= 0) ColumnList.SelectedIndex = select;
        }

        private void Preset_Click(object sender, RoutedEventArgs e) => Refresh(Build(ColumnCatalog.Preset((string)((Control)sender).Tag)));
        private void Reset_Click(object sender, RoutedEventArgs e) => Refresh(Build(ColumnCatalog.Preset(ColumnCatalog.Standard)));
        private void Up_Click(object sender, RoutedEventArgs e) => Move(-1);
        private void Down_Click(object sender, RoutedEventArgs e) => Move(1);

        private void Move(int delta)
        {
            int i = ColumnList.SelectedIndex, j = i + delta;
            if (i < 0 || j < 0 || j >= _items.Count) return;
            var item = _items[i];
            _items.RemoveAt(i);
            _items.Insert(j, item);
            Refresh(new List<Item>(_items), j);
        }

        private async void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (!_items.Any(i => i.Visible))
            {
                await Dialogs.Info(this, Title, "Affichez au moins une colonne.");
                return;
            }
            Result = _items.Select(i => new ColumnState { Key = i.Key, Visible = i.Visible, Width = i.Width }).ToList();
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    }
}
