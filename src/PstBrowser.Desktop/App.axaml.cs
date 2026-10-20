using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using PstBrowser.Core.Text;

namespace PstBrowser.Desktop
{
    public partial class App : Application
    {
        /// <summary>
        /// Per-user folder for the settings (no message content is stored there): ~/Library/Application Support/PstBrowser
        /// on macOS, ~/.local/share/PstBrowser on Linux.
        /// </summary>
        public static string LocalDataFolder { get; } =
            // Create: on a new account the folder may not exist yet, and an empty path would mean the current directory
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "PstBrowser");

        /// <summary>Temporary folder for attachments opened with an external application; emptied on exit.</summary>
        public static string TempFolder { get; } = Path.Combine(Path.GetTempPath(), "PstBrowser-" + Environment.ProcessId);

        public override void Initialize() => AvaloniaXamlLoader.Load(this);

        public override void OnFrameworkInitializationCompleted()
        {
            Charsets.EnsureRegistered();
            // The interface is in French: French formats (dates, numbers) whatever the system language
            var fr = new CultureInfo("fr-FR");
            CultureInfo.DefaultThreadCurrentCulture = fr;
            CultureInfo.DefaultThreadCurrentUICulture = fr;
            Thread.CurrentThread.CurrentCulture = fr;
            Thread.CurrentThread.CurrentUICulture = fr;
            Directory.CreateDirectory(LocalDataFolder);
            TaskScheduler.UnobservedTaskException += (_, e) => e.SetObserved();

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var main = new MainWindow();
                desktop.MainWindow = main;
                var args = desktop.Args ?? Array.Empty<string>();
                if (args.Length >= 4 && args[0] == "--ui-smoke")
                    main.Opened += async (_, __) => desktop.Shutdown(await main.RunSmoke(args[1], args[2], args[3]));
                else
                    main.Opened += (_, __) => main.OpenInitialWorkspace(args.FirstOrDefault());
                desktop.Exit += (_, __) =>
                {
                    Services.AppServices.Close();
                    try { if (Directory.Exists(TempFolder)) Directory.Delete(TempFolder, true); } catch { /* files still open in another application */ }
                };
            }
            base.OnFrameworkInitializationCompleted();
        }
    }
}
