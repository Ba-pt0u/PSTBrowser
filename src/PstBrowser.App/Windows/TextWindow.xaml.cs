using System.Windows;

namespace PstBrowser.App.Windows
{
    public partial class TextWindow : Window
    {
        public TextWindow(string title, string text)
        {
            InitializeComponent();
            Title = title;
            Text.Text = text;
        }

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            try { Clipboard.SetText(Text.Text); } catch { }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
