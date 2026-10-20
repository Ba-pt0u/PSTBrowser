using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace PstBrowser.Desktop.Windows
{
    /// <summary>Read-only text that can be selected and copied (message properties, Internet headers).</summary>
    public static class TextWindow
    {
        public static void Open(string title, string text, Window owner)
        {
            var copy = new Button { Content = "Copier", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
            copy.Classes.Add("tool");
            var box = new TextBox
            {
                Text = text,
                IsReadOnly = true,
                TextWrapping = TextWrapping.NoWrap,
                FontFamily = new FontFamily("Menlo, DejaVu Sans Mono, Consolas, monospace"),
                FontSize = 12,
                AcceptsReturn = true,
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(box, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
            var win = new Window { Title = title, Width = 900, Height = 620, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            copy.Click += async (_, __) => { try { await win.Clipboard.SetTextAsync(text); } catch { } };
            var dock = new DockPanel { Margin = new Thickness(12) };
            DockPanel.SetDock(copy, Dock.Bottom);
            dock.Children.Add(copy);
            dock.Children.Add(box);
            win.Content = dock;
            if (owner != null) win.Show(owner); else win.Show();
        }
    }
}
