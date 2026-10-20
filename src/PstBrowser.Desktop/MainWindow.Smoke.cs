using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PstBrowser.Core.Viewer;
using PstBrowser.Desktop.Services;
using PstBrowser.Desktop.Windows;

namespace PstBrowser.Desktop
{
    /// <summary>
    /// Interface self-test (<c>PstBrowser --ui-smoke &lt;case&gt; &lt;output folder&gt; &lt;query&gt;</c>), run by the CI on macOS and
    /// Linux: opens the case, searches, displays a message in the native reader, opens the analysis and sources windows.
    /// At each step it writes « SMOKE &lt;name&gt; » on its output and waits for &lt;name&gt;.png in the output folder: the test
    /// script takes the screenshot (the native view cannot be captured from inside the application).
    /// </summary>
    public partial class MainWindow
    {
        private int _smokeFailures;
        private StreamWriter _smokeLog;

        public async Task<int> RunSmoke(string caseDir, string outDir, string query)
        {
            Directory.CreateDirectory(outDir);
            using (_smokeLog = new StreamWriter(Path.Combine(outDir, "ui-smoke.log")) { AutoFlush = true })
            {
                try
                {
                    Log($"PST Browser {typeof(MainWindow).Assembly.GetName().Version} — {Environment.OSVersion}");
                    await OpenWorkspace(caseDir);
                    Check(AppServices.Workspace != null, "dossier d'affaire ouvert");
                    await WaitFor(() => AppServices.Indexer?.IsRunning != true, 180);
                    await WaitFor(() => _rows.Count > 0, 20);
                    Check(_treeRoots.Count == 1 && _treeRoots[0].Children.Count > 0, $"arborescence : {_treeRoots.FirstOrDefault()?.Children.Count} boîte(s)");
                    await Snapshot(outDir, "1-ouverture");

                    TxtQuery.Text = query;
                    Search_Click(null, null);
                    await WaitFor(() => _rows.Count > 0 && TxtResults.Text?.Contains("élément") == true, 20);
                    Check(_rows.Count > 0, $"recherche « {query} » : {TxtResults.Text}");
                    if (_rows.Count > 0) MessageList.SelectedItem = _rows[0];
                    await WaitFor(() => Viewer.CurrentRef != null && Viewer.DocumentsLoaded > 0, 30);
                    await Task.Delay(2500);
                    Check(Viewer.CurrentRef != null, "message affiché (en-tête, pièces jointes)");
                    Check(Viewer.DocumentsLoaded > 0 && Viewer.ReaderError == null,
                          $"corps du message chargé par le lecteur natif ({Viewer.DocumentsLoaded} document(s)){(Viewer.ReaderError != null ? " — " + Viewer.ReaderError : "")}");
                    await Snapshot(outDir, "2-message");

                    if (_rows.Count > 0)
                    {
                        var row = _rows[0];
                        AnalysisWindow.Open(new MessageRef(row.Id), row.Subject, this);
                        await Task.Delay(3000);
                        var analysis = OwnedWindows.OfType<AnalysisWindow>().FirstOrDefault();
                        Check(analysis != null, "fenêtre d'analyse ouverte");
                        await Snapshot(outDir, "3-analyse");
                        analysis?.Close();
                    }

                    var sources = new SourcesWindow();
                    sources.Show(this);
                    await Task.Delay(1500);
                    Check(sources.IsVisible, "fenêtre des sources ouverte");
                    await Snapshot(outDir, "4-sources");
                    sources.Close();

                    ApplyReaderLayout(!_readerBottom);
                    await Task.Delay(2500);
                    Check(Viewer.DocumentsLoaded > 0, "lecteur recréé après le changement de disposition");
                    await Snapshot(outDir, "5-disposition");
                }
                catch (Exception ex)
                {
                    Check(false, "exception : " + ex);
                }
                Log(_smokeFailures == 0 ? "SUCCÈS" : $"ÉCHEC : {_smokeFailures} vérification(s)");
            }
            return _smokeFailures == 0 ? 0 : 1;
        }

        private void Log(string text)
        {
            Console.WriteLine(text);
            _smokeLog?.WriteLine(text);
        }

        private void Check(bool ok, string what)
        {
            Log((ok ? "  ok : " : "  ÉCHEC : ") + what);
            if (!ok) _smokeFailures++;
        }

        private static async Task WaitFor(Func<bool> condition, int seconds)
        {
            var sw = Stopwatch.StartNew();
            while (!condition() && sw.Elapsed.TotalSeconds < seconds) await Task.Delay(200);
        }

        private async Task Snapshot(string outDir, string name)
        {
            Console.WriteLine("SMOKE " + name);
            var file = Path.Combine(outDir, name + ".png");
            await WaitFor(() => File.Exists(file), 30);
            if (!File.Exists(file)) Log($"  (pas de capture {name}.png)");
        }
    }
}
