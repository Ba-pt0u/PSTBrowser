using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using PstBrowser.Core.Text;

namespace PstBrowser.App
{
    public partial class App : Application
    {
        /// <summary>Per-user folder for settings and the WebView2 profile (no message content is stored there).</summary>
        public static string LocalDataFolder { get; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PstBrowser");

        /// <summary>Temporary folder for attachments opened with an external application; emptied on exit.</summary>
        public static string TempFolder { get; } = Path.Combine(Path.GetTempPath(), "PstBrowser-" + Environment.ProcessId);

        protected override void OnStartup(StartupEventArgs e)
        {
            Charsets.EnsureRegistered();
            Directory.CreateDirectory(LocalDataFolder);

            DispatcherUnhandledException += OnDispatcherUnhandledException;
            TaskScheduler.UnobservedTaskException += (_, args) => args.SetObserved();
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                try { MessageBox.Show("Erreur fatale : " + (args.ExceptionObject as Exception)?.Message, "PST Browser", MessageBoxButton.OK, MessageBoxImage.Error); } catch { }
            };

            base.OnStartup(e);
            var main = new MainWindow();
            MainWindow = main;
            main.Show();
            main.OpenInitialWorkspace(e.Args.Length > 0 ? e.Args[0] : null);
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            e.Handled = true;
            MessageBox.Show(MainWindow, "Une erreur est survenue :\n\n" + e.Exception.Message, "PST Browser", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try { if (Directory.Exists(TempFolder)) Directory.Delete(TempFolder, true); } catch { /* files still open in another application */ }
            base.OnExit(e);
        }
    }
}
