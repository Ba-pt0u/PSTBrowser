using System.Windows;
using PstBrowser.App.Services;

namespace PstBrowser.App.Windows
{
    /// <summary>Text extracted from an attachment, shown in the same locked-down WebView2 as the messages.</summary>
    public partial class AttachmentTextWindow : Window
    {
        private readonly SecureWebView _secure;

        public AttachmentTextWindow()
        {
            InitializeComponent();
            _secure = new SecureWebView(Web);
        }

        public static async void Open(string name, string html, Window owner)
        {
            var w = new AttachmentTextWindow { Owner = owner, Title = "Aperçu du texte — " + name };
            w.Show();
            await w._secure.EnsureInitializedAsync();
            if (w._secure.InitError != null)
            {
                w.Web.Visibility = Visibility.Collapsed;
                w.TxtError.Text = w._secure.InitError;
                w.TxtError.Visibility = Visibility.Visible;
                return;
            }
            await w._secure.ShowHtmlAsync(html);
        }
    }
}
