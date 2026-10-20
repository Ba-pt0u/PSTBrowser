using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace PstBrowser.Desktop.Services
{
    /// <summary>Message boxes (Avalonia has none): a question with buttons, or an information.</summary>
    public static class Dialogs
    {
        /// <summary>Shows <paramref name="text"/> with the given buttons; returns the label clicked, or null (Échap, fermeture).</summary>
        public static async Task<string> Ask(Window owner, string title, string text, params string[] buttons)
        {
            string result = null;
            var win = new Window
            {
                Title = title,
                SizeToContent = SizeToContent.WidthAndHeight,
                CanResize = false,
                WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                ShowInTaskbar = false,
                MinWidth = 340,
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 18, 0, 0) };
            for (int i = 0; i < buttons.Length; i++)
            {
                var label = buttons[i];
                var b = new Button { Content = label, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center, IsDefault = i == 0 };
                if (i == 0) b.Classes.Add("accent"); else b.Classes.Add("tool");
                b.Click += (_, __) => { result = label; win.Close(); };
                row.Children.Add(b);
            }
            win.Content = new StackPanel
            {
                Margin = new Thickness(22, 18),
                Children =
                {
                    new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 560, LineHeight = 20 },
                    row,
                },
            };
            win.KeyDown += (_, e) => { if (e.Key == Key.Escape) win.Close(); };
            if (owner != null) await win.ShowDialog(owner);
            else
            {
                var done = new TaskCompletionSource<bool>();
                win.Closed += (_, __) => done.TrySetResult(true);
                win.Show();
                await done.Task;
            }
            return result;
        }

        public static Task Info(Window owner, string title, string text) => Ask(owner, title, text, "OK");

        public static async Task<bool> Confirm(Window owner, string title, string text, string ok = "OK", string cancel = "Annuler")
            => await Ask(owner, title, text, ok, cancel) == ok;
    }
}
