using Avalonia.Controls;
using PstBrowser.Desktop.Controls;

namespace PstBrowser.Desktop.Windows
{
    /// <summary>A safe HTML document (e.g. the text extracted from an attachment) in a window of its own, through the secure reader.</summary>
    public static class HtmlWindow
    {
        public static void Open(string title, string html, Window owner)
        {
            var view = new SecureMessageView();
            view.ShowHtml(html);
            var win = new Window { Title = title, Width = 900, Height = 700, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = view };
            if (owner != null) win.Show(owner); else win.Show();
        }
    }
}
