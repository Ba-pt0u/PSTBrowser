using System.Collections.Generic;
using System.Windows;
using PstBrowser.Core.Viewer;

namespace PstBrowser.App.Windows
{
    public partial class MessageWindow : Window
    {
        public MessageWindow()
        {
            InitializeComponent();
            Viewer.ShowPopOutButton = false;
        }

        public static void Open(MessageRef mref, IList<string> terms, Window owner)
        {
            var w = new MessageWindow { Owner = owner };
            w.Show();
            _ = w.Viewer.ShowAsync(mref, terms);
        }
    }
}
