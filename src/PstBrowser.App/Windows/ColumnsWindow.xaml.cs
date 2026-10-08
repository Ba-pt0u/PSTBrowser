using System.Collections.Generic;
using System.Linq;
using System.Windows;
using PstBrowser.App.Services;

namespace PstBrowser.App.Windows
{
    /// <summary>Column chooser: visibility, order, preset views and reset.</summary>
    public partial class ColumnsWindow : Window
    {
        public sealed class Item : System.ComponentModel.INotifyPropertyChanged
        {
            private bool _visible;
            public string Key { get; set; }
            public string Title { get; set; }
            public double? Width { get; set; }
            public bool Visible
            {
                get => _visible;
                set { _visible = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Visible))); }
            }
            public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        }

        private readonly List<Item> _items;

        /// <summary>The layout chosen by the user (valid after the dialog returned true).</summary>
        public List<ColumnState> Result { get; private set; }

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

        private void Preset_Click(object sender, RoutedEventArgs e)
            => Refresh(Build(ColumnCatalog.Preset((string)((FrameworkElement)sender).Tag)));

        private void Reset_Click(object sender, RoutedEventArgs e)
            => Refresh(Build(ColumnCatalog.Preset(ColumnCatalog.Standard)));

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

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (!_items.Any(i => i.Visible))
            {
                MessageBox.Show(this, "Affichez au moins une colonne.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            Result = _items.Select(i => new ColumnState { Key = i.Key, Visible = i.Visible, Width = i.Width }).ToList();
            DialogResult = true;
        }
    }
}
