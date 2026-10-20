using System.Collections.Generic;
using Avalonia.Controls;
using PstBrowser.Core.Viewer;
using PstBrowser.Desktop.Controls;

namespace PstBrowser.Desktop.Windows
{
    /// <summary>A message in a window of its own.</summary>
    public static class MessageWindow
    {
        public static void Open(MessageRef mref, IList<string> terms, Window owner, string query = null)
        {
            var viewer = new MessageViewer();
            viewer.AnalysisRequested += (r, subject) => AnalysisWindow.Open(r, subject, owner);
            var w = new Window { Title = "Message — PST Browser", Width = 980, Height = 760, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = viewer };
            if (owner != null) w.Show(owner); else w.Show();
            _ = viewer.ShowAsync(mref, terms, query);
        }
    }
}
