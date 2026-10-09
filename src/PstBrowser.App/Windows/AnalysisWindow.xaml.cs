using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PstBrowser.App.Services;
using PstBrowser.Core.Analysis;
using PstBrowser.Core.Search;
using PstBrowser.Core.Viewer;

namespace PstBrowser.App.Windows
{
    /// <summary>Header path, authentication, spoofing indications, dates, sensitive data and thread of one message.</summary>
    public partial class AnalysisWindow : Window
    {
        public sealed class SensitiveRow
        {
            public SensitiveItem Item { get; set; }
            public string KindLabel => Item.KindLabel;
            public string Location => Item.Location;
            public string Shown { get; set; }
        }

        private MessageRef _ref;
        private bool _revealed;
        private readonly List<SensitiveRow> _sensitive = new List<SensitiveRow>();

        public AnalysisWindow() { InitializeComponent(); }

        public static async void Open(MessageRef mref, string subject, Window owner)
        {
            var w = new AnalysisWindow { Owner = owner, _ref = mref };
            w.TxtTitle.Text = string.IsNullOrWhiteSpace(subject) ? "(sans objet)" : subject;
            w.Show();
            w.Cursor = Cursors.Wait;
            try
            {
                var svc = AppServices.Messages;
                var view = await Task.Run(() => svc.GetAnalysis(mref));
                List<MessageRow> thread = null;
                if (view.ThreadId.HasValue && view.ThreadCount > 1)
                    thread = await Task.Run(() => AppServices.Search.Search(new SearchRequest { Query = "fil:" + view.ThreadId, SortBy = "date", SortDescending = false, Limit = 500 }).Rows);
                w.Fill(view, thread);
            }
            catch (Exception ex)
            {
                w.TxtAuth.Text = "Analyse impossible : " + ex.Message;
            }
            finally { w.Cursor = null; }
        }

        private static string Verdict(string v) => v == null ? "non indiqué" : v == "pass" ? "réussi ✔" : v == "fail" ? "échec ✘" : v == "softfail" ? "échec léger ⚠" : v;

        private void Fill(AnalysisView a, List<MessageRow> thread)
        {
            var h = a.Live.Header;
            // ---- headers
            if (!h.HasHeaders)
                TxtAuth.Text = "Ce message ne contient pas d'en-têtes Internet (message envoyé ou interne, ou en-têtes non conservés dans l'export).";
            else
                TxtAuth.Text = $"SPF : {Verdict(h.Auth.Spf)}{Domain(h.Auth.SpfDomain)}     DKIM : {Verdict(h.Auth.Dkim)}{Domain(h.Auth.DkimDomain)}     DMARC : {Verdict(h.Auth.Dmarc)}{Domain(h.Auth.DmarcDomain)}";
            TxtAddresses.Text = h.HasHeaders ? $"Expéditeur : {h.From}     Reply-To : {h.ReplyTo ?? "—"}     Return-Path : {h.ReturnPath ?? "—"}" + (h.Mailer != null ? $"     Logiciel : {h.Mailer}" : "") : "";
            var findings = h.Findings.Where(f => f.Code != "auth-absent").Concat(a.CaseFindings).Select(f => "⚠ " + f.Text).ToList();
            if (findings.Count == 0) findings.Add(h.HasHeaders ? "Aucun indice d'usurpation relevé dans les en-têtes." : "Aucun indice relevé (le nom affiché est cohérent avec l'adresse).");
            ListFindings.ItemsSource = findings;
            GridHops.ItemsSource = h.Hops.Select(x => new
            {
                x.Index, TimeText = x.Time?.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss") ?? "", DelayText = x.Delay.HasValue ? Delay(x.Delay.Value) : "",
                From = x.From, x.FromIp, By = x.By, With = x.With,
            }).ToList();
            TxtHopsTitle.Text = h.Hops.Count == 0 ? "Chemin des serveurs : aucun en-tête « Received »" : $"Chemin des serveurs : {h.Hops.Count} saut(s), du plus ancien au plus récent";

            // ---- dates
            GridDates.ItemsSource = a.Live.Dates.Dates.Select(d => new { d.Label, ValueText = d.Value?.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss") ?? "—", d.Source }).ToList();
            var dateFindings = a.Live.Dates.Findings.Select(f => "⚠ " + f.Text).ToList();
            if (dateFindings.Count == 0) dateFindings.Add("Les dates sont cohérentes entre elles (tolérance d'horloge : 5 minutes).");
            ListDateFindings.ItemsSource = dateFindings;

            // ---- sensitive data
            _sensitive.Clear();
            _sensitive.AddRange(a.Sensitive.Select(i => new SensitiveRow { Item = i, Shown = i.Masked }));
            GridSensitive.ItemsSource = null;
            GridSensitive.ItemsSource = _sensitive;
            TxtSensitiveNote.Text = _sensitive.Count == 0 ? "Aucune donnée sensible détectée dans le texte du message et de ses pièces jointes." : $"{_sensitive.Count} détection(s). Les IBAN et cartes sont validés par leur clé de contrôle ; les téléphones sont reconnus par leur format.";
            BtnReveal.IsEnabled = _sensitive.Count > 0;

            // ---- thread
            if (thread == null || thread.Count < 2)
            {
                TxtThread.Text = a.ThreadId.HasValue ? "Ce message est seul dans son fil." : "Les fils de conversation ne sont pas encore calculés (indexation en cours).";
                GridThread.ItemsSource = null;
            }
            else
            {
                TxtThread.Text = $"{thread.Count} message(s) dans ce fil, toutes boîtes confondues. Double-clic : ouvrir un message.";
                GridThread.ItemsSource = thread.Select(r => new ThreadRow(r)).ToList();
            }

            // open on the most interesting tab
            if (findings.Any(f => !f.StartsWith("Aucun"))) Tabs.SelectedItem = TabHeaders;
            else if (a.Live.Dates.Findings.Count > 0) Tabs.SelectedItem = TabDates;
            else if (_sensitive.Count > 0) Tabs.SelectedItem = TabSensitive;
        }

        private static string Domain(string d) => string.IsNullOrEmpty(d) ? "" : $" ({d})";

        private static string Delay(TimeSpan t)
            => Math.Abs(t.TotalSeconds) < 90 ? $"{t.TotalSeconds:0} s" : Math.Abs(t.TotalMinutes) < 90 ? $"{t.TotalMinutes:0} min" : $"{t.TotalHours:0.#} h";

        private sealed class ThreadRow
        {
            public MessageRow Row { get; }
            public ThreadRow(MessageRow r) { Row = r; }
            public string DateText => Row.Date?.ToString("dd/MM/yy HH:mm") ?? "";
            public string From => Row.From;
            public string Subject => Row.Subject;
            public string Mailbox => Row.Mailbox;
            public string FolderName { get { var p = Row.FolderPath ?? ""; int i = p.LastIndexOf('/'); return i >= 0 ? p.Substring(i + 1) : p; } }
        }

        private void Thread_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (GridThread.SelectedItem is ThreadRow r) MessageWindow.Open(new MessageRef(r.Row.Id), null, this);
        }

        private async void Reveal_Click(object sender, RoutedEventArgs e)
        {
            _revealed = !_revealed;
            BtnReveal.Content = _revealed ? "Masquer les valeurs" : "Afficher les valeurs";
            var svc = AppServices.Messages;
            var rows = _sensitive.ToList();
            var shown = await Task.Run(() => rows.Select(r => _revealed ? (svc.RevealSensitive(r.Item.Id) ?? r.Item.Masked) : r.Item.Masked).ToList());
            for (int i = 0; i < rows.Count; i++) rows[i].Shown = shown[i];
            GridSensitive.ItemsSource = null;
            GridSensitive.ItemsSource = _sensitive;
        }

        private void CopySensitive_Click(object sender, RoutedEventArgs e)
        {
            var sb = new StringBuilder("Type\tValeur\tEmplacement\r\n");
            foreach (var r in _sensitive) sb.Append($"{r.KindLabel}\t{r.Shown}\t{r.Location}\r\n");
            try { Clipboard.SetText(sb.ToString()); } catch { }
        }
    }
}
