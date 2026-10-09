// pstbrowser-cli: command-line access to the PstBrowser engine (indexing, search, export).
// Useful for scripting, for very large batches, and for testing the engine without the Windows UI.
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using PstBrowser.Core.Analysis;
using PstBrowser.Core.Data;
using PstBrowser.Core.Index;
using PstBrowser.Core.Search;
using PstBrowser.Core.Text;
using PstBrowser.Core.Viewer;

namespace PstBrowser.Cli
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            // Text extraction worker: parses attachments in a separate process (see Indexer, pass 3)
            if (PstBrowser.Core.Extraction.ExtractWorker.IsWorkerInvocation(args)) return PstBrowser.Core.Extraction.ExtractWorker.Run();
            Charsets.EnsureRegistered();
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            if (args.Length == 1 && args[0] == "selftest-unit") return SelfTest.RunUnit();
            if (args.Length == 2 && args[0] == "extract-file") return ExtractFile(args[1]);
            if (args.Length < 2) return Usage();
            if (args[0] == "selftest" && args.Length >= 4) return SelfTest.Run(args[1], args[2], args[3]);
            string cmd = args[0].ToLowerInvariant();
            string wsDir = args[1];
            try
            {
                switch (cmd)
                {
                    case "add":
                        {
                            using var ws = Workspace.OpenOrCreate(wsDir);
                            var added = ws.AddSources(args.Skip(2));
                            foreach (var s in added) Console.WriteLine($"+ {s.DisplayName}  →  boîte « {s.MailboxName} »");
                            Console.WriteLine($"{added.Count} source(s) ajoutée(s).");
                            return 0;
                        }
                    case "index":
                        {
                            using var ws = Workspace.OpenOrCreate(wsDir);
                            var opt = new IndexerOptions();
                            if (args.Contains("--sha256")) opt.ComputeSha256 = true;
                            if (args.Contains("--no-attachments")) opt.IndexAttachments = false;
                            var ti = Array.IndexOf(args, "--attachment-timeout");
                            if (ti > 0 && ti + 1 < args.Length) opt.AttachmentTimeoutSeconds = int.Parse(args[ti + 1]);
                            var pi = Array.IndexOf(args, "--parallel");
                            if (pi > 0 && pi + 1 < args.Length) opt.Parallelism = int.Parse(args[pi + 1]);
                            var ix = new Indexer(ws, opt);
                            var sw = Stopwatch.StartNew();
                            var t = ix.Start();
                            using var cts = new CancellationTokenSource();
                            Console.CancelKeyPress += (_, e) => { e.Cancel = true; ix.Stop(); };
                            string last = "";
                            while (!t.Wait(1000))
                            {
                                var p = ix.Progress;
                                var line = $"{p.Phase} {p.CurrentSource}  {p.Done:N0}/{p.Total:N0}  {p.MessagesPerSecond:N0} msg/s  erreurs: {p.Errors}";
                                if (line != last) Console.Error.WriteLine(line);
                                last = line;
                            }
                            var f = ix.Progress;
                            Console.WriteLine($"{f.Phase} — {sw.Elapsed:hh\\:mm\\:ss} — erreurs: {f.Errors}{(f.LastError != null ? " (dernière : " + f.LastError + ")" : "")}");
                            var (total, indexed) = ws.GetCounts();
                            Console.WriteLine($"{indexed:N0}/{total:N0} éléments indexés. Index : {Format.Size(new FileInfo(ws.DbPath).Length)}");
                            return 0;
                        }
                    case "sources":
                        {
                            using var ws = Workspace.OpenOrCreate(wsDir);
                            foreach (var s in ws.ListSources())
                                Console.WriteLine($"[{s.Id}] {s.MailboxName} | {s.DisplayName} | {Format.Size(s.Size)} | {s.Status} | {s.IndexedCount}/{s.MessageCount} {(s.Sha256 != null ? "| SHA256 " + s.Sha256 : "")} {s.Error}");
                            return 0;
                        }
                    case "tree":
                        {
                            using var ws = Workspace.OpenOrCreate(wsDir);
                            foreach (var mb in ws.ListMailboxes())
                            {
                                Console.WriteLine($"# {mb.Name} ({mb.MessageCount})");
                                void Print(FolderNode n, int d) { Console.WriteLine($"{new string(' ', d * 2)}{n.Name} ({n.Count})"); foreach (var c in n.Children) Print(c, d + 1); }
                                foreach (var n in ws.GetFolderTree(mb.Id)) Print(n, 1);
                            }
                            return 0;
                        }
                    case "search":
                        {
                            using var ws = Workspace.OpenOrCreate(wsDir);
                            var svc = new SearchService(ws);
                            var req = new SearchRequest { Query = args.Length > 2 ? args[2] : "", Limit = 50 };
                            for (int i = 3; i < args.Length; i++)
                            {
                                if (args[i] == "--sort" && i + 1 < args.Length)
                                {
                                    if (!SortFields.TryParse(args[++i], out var key, out var desc)) { Console.WriteLine("Tri inconnu : " + args[i] + " (champs : " + string.Join(", ", SortFields.Keys) + ")"); return 2; }
                                    req.SortBy = key; req.SortDescending = desc;
                                }
                                else if (args[i] == "--dedup") req.HideDuplicates = true;
                                else if (args[i] == "--att") req.OnlyWithAttachments = true;
                                else if (args[i] == "--limit") req.Limit = int.Parse(args[++i]);
                                else if (args[i] == "--csv") { var csv = args[++i]; req.Limit = int.MaxValue; var all = svc.Search(req); SearchService.ExportCsv(all.Rows, csv); Console.WriteLine($"{all.Rows.Count} lignes → {csv}"); return 0; }
                            }
                            var r = svc.Search(req);
                            if (r.Error != null) { Console.WriteLine("ERREUR : " + r.Error); return 2; }
                            Console.WriteLine($"{r.Total:N0} résultat(s) en {r.ElapsedMs} ms");
                            foreach (var row in r.Rows)
                            {
                                Console.WriteLine($"[{row.Id}] {row.Date:yyyy-MM-dd HH:mm} | {row.From} | {row.Subject} | {row.Mailbox}/{row.FolderPath}{(row.HasAttachments ? " 📎" + row.AttachmentCount : "")}{(row.IsDuplicate ? " (doublon)" : "")}");
                                if (!string.IsNullOrEmpty(row.Snippet)) Console.WriteLine("      " + row.Snippet);
                            }
                            return 0;
                        }
                    case "show":
                        {
                            using var ws = Workspace.OpenOrCreate(wsDir);
                            using var ms = new MessageService(ws);
                            string query = args.Length > 3 && !args[3].StartsWith("--") ? args[3] : null;
                            var v = ms.GetView(MessageRef.Parse(args[2]), query != null ? QueryParser.Parse(query).HighlightTerms : null, query);
                            Console.WriteLine($"Objet : {v.Subject}\nDe : {v.From}\nÀ : {v.To}\nCc : {v.Cc}\nDate : {v.Date}\nType : {v.Kind} ({v.MessageClass})\nBoîte : {v.Mailbox} / {v.FolderPath}\nFormat : {v.BodyFormat}");
                            foreach (var a in v.Attachments)
                                Console.WriteLine($"  PJ [{a.Index}] {a.FileName} {a.SizeText}{(a.IsEmbeddedMessage ? " (message)" : "")}" +
                                                  (a.TextStatus != null ? $" — texte : {PstBrowser.Core.Extraction.AttachmentStatus.Label(a.TextStatus)}" + (a.HasText ? $" ({a.TextLength:N0} car.)" : "") : "") +
                                                  (a.HasHit ? " — CONTIENT LES TERMES RECHERCHÉS" : "") + (a.Sha256 != null ? " — SHA-256 " + a.Sha256 : ""));
                            if (args.Contains("--headers")) Console.WriteLine("--- en-têtes Internet ---\n" + (v.TransportHeaders ?? "(aucun)").Replace("\r", "\\r"));
                            if (args.Contains("--att-text"))
                            {
                                var at = ms.GetAttachmentText(MessageRef.Parse(args[2]).Id, args[Array.IndexOf(args, "--att-text") + 1]);
                                Console.WriteLine(at == null ? "(pièce jointe absente de l'index)" : $"--- texte de {at.Name} ({at.Status}) ---\n{at.Text}");
                            }
                            if (args.Contains("--html")) { var path = args[Array.IndexOf(args, "--html") + 1]; File.WriteAllText(path, v.BodyHtml); Console.WriteLine("HTML → " + path); }
                            return 0;
                        }
                    case "attachments":
                        {
                            // Inventory of the attachments indexed in pass 3: status and SHA-256 of each file
                            using var ws = Workspace.OpenOrCreate(wsDir);
                            using var db = ws.Open();
                            var st = db.Prepare(@"SELECT a.message_id, a.idx_path, a.name, a.size, a.status, a.sha256, a.text_len, a.error FROM attachments a ORDER BY a.message_id, a.idx_path");
                            var rows = new System.Collections.Generic.List<string[]>();
                            while (st.Step())
                                rows.Add(new[] { st.GetString(0), st.GetString(1), st.GetString(2), st.GetString(3), st.GetString(4), st.GetString(5), st.GetString(6), st.GetString(7) });
                            st.Reset();
                            int ci = Array.IndexOf(args, "--csv");
                            if (ci > 0 && ci + 1 < args.Length)
                            {
                                using var w = new StreamWriter(args[ci + 1], false, new System.Text.UTF8Encoding(true));
                                w.WriteLine("Message;Position;Nom;Taille;Statut;SHA-256;Caractères;Erreur");
                                foreach (var r in rows) w.WriteLine(string.Join(";", r.Select(x => "\"" + (x ?? "").Replace("\"", "\"\"") + "\"")));
                                Console.WriteLine($"{rows.Count} pièce(s) jointe(s) → {args[ci + 1]}");
                            }
                            else
                            {
                                foreach (var g in rows.GroupBy(r => r[4])) Console.WriteLine($"{PstBrowser.Core.Extraction.AttachmentStatus.Label(g.Key)} : {g.Count():N0}");
                                Console.WriteLine($"{rows.Count:N0} pièce(s) jointe(s) au total.");
                            }
                            return 0;
                        }
                    case "analyze":
                        {
                            // Header path, authentication, spoofing indications, dates, sensitive data and thread of one message
                            using var ws = Workspace.OpenOrCreate(wsDir);
                            using var ms = new MessageService(ws);
                            var a = ms.GetAnalysis(MessageRef.Parse(args[2]));
                            var h = a.Live.Header;
                            Console.WriteLine($"Expéditeur : {h.From}   Reply-To : {h.ReplyTo ?? "-"}   Return-Path : {h.ReturnPath ?? "-"}");
                            Console.WriteLine($"SPF : {h.Auth.Spf ?? "-"}   DKIM : {h.Auth.Dkim ?? "-"}   DMARC : {h.Auth.Dmarc ?? "-"}");
                            Console.WriteLine($"Chemin ({h.Hops.Count} serveur(s), du plus ancien au plus récent) :");
                            foreach (var hop in h.Hops)
                                Console.WriteLine($"  {hop.Index}. {hop.From} [{hop.FromIp}] → {hop.By}  {hop.Time?.ToLocalTime():yyyy-MM-dd HH:mm:ss}  {(hop.Delay.HasValue ? "+" + hop.Delay.Value.TotalSeconds.ToString("0") + " s" : "")}");
                            if (args.Contains("--raw")) foreach (var rawHop in h.Hops) Console.WriteLine($"  Received {rawHop.Index} : {rawHop.Raw}");
                            foreach (var f in h.Findings.Concat(a.CaseFindings)) Console.WriteLine("  ! " + f.Text);
                            Console.WriteLine("Dates :");
                            foreach (var d in a.Live.Dates.Dates.Where(d => d.Value.HasValue)) Console.WriteLine($"  {d.Label} : {d.Value.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
                            foreach (var f in a.Live.Dates.Findings) Console.WriteLine("  ! " + f.Text);
                            Console.WriteLine($"Fil : {(a.ThreadId.HasValue ? a.ThreadId + " (" + a.ThreadCount + " message(s))" : "non calculé")}");
                            foreach (var sIt in a.Sensitive) Console.WriteLine($"  Sensible : {sIt.KindLabel} {sIt.Masked}  — {sIt.Location}");
                            return 0;
                        }
                    case "sensitive":
                        {
                            // Sensitive data found in messages and attachments, masked values only
                            using var ws = Workspace.OpenOrCreate(wsDir);
                            using var db = ws.Open();
                            var st = db.Prepare(@"SELECT s.message_id, s.kind, s.masked, s.att_path, a.name, m.subject, m.date
                                                  FROM sensitive s JOIN messages m ON m.id=s.message_id LEFT JOIN attachments a ON a.message_id=s.message_id AND a.idx_path=s.att_path
                                                  ORDER BY s.message_id, s.id");
                            var rows = new System.Collections.Generic.List<string[]>();
                            while (st.Step())
                                rows.Add(new[] { st.GetString(0), SensitiveScanner.Label((SensitiveKind)st.GetInt(1)), st.GetString(2), st.GetString(3) == null ? "corps" : "PJ : " + st.GetString(4), st.GetString(5) ?? "" });
                            st.Reset();
                            int ci = Array.IndexOf(args, "--csv");
                            if (ci > 0 && ci + 1 < args.Length)
                            {
                                using var w = new StreamWriter(args[ci + 1], false, new System.Text.UTF8Encoding(true));
                                w.WriteLine("Message;Type;Valeur masquée;Emplacement;Objet");
                                foreach (var r in rows) w.WriteLine(string.Join(";", r.Select(x => "\"" + (x ?? "").Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + "\"")));
                                Console.WriteLine($"{rows.Count} détection(s) → {args[ci + 1]}");
                            }
                            else
                            {
                                foreach (var g in rows.GroupBy(r => r[1])) Console.WriteLine($"{g.Key} : {g.Count():N0} détection(s) dans {g.Select(r => r[0]).Distinct().Count():N0} message(s)");
                                Console.WriteLine($"{rows.Count:N0} détection(s) au total (valeurs masquées).");
                            }
                            return 0;
                        }
                    case "lookalikes":
                        {
                            using var ws = Workspace.OpenOrCreate(wsDir);
                            using var db = ws.Open();
                            var st = db.Prepare("SELECT domain, resembles, kind, messages FROM lookalikes ORDER BY messages DESC");
                            while (st.Step()) Console.WriteLine($"{st.GetString(0)}  ressemble à  {st.GetString(1)}  ({st.GetString(2)}, {st.GetLong(3)} message(s))");
                            st.Reset();
                            return 0;
                        }
                    case "save-att":
                        {
                            using var ws = Workspace.OpenOrCreate(wsDir);
                            using var ms = new MessageService(ws);
                            ms.SaveAttachment(MessageRef.Parse(args[2]), int.Parse(args[3]), args[4]);
                            Console.WriteLine($"→ {args[4]} ({new FileInfo(args[4]).Length} octets)");
                            return 0;
                        }
                    case "eml":
                        {
                            using var ws = Workspace.OpenOrCreate(wsDir);
                            using var ms = new MessageService(ws);
                            ms.ExportEml(MessageRef.Parse(args[2]), args[3]);
                            Console.WriteLine($"→ {args[3]}");
                            return 0;
                        }
                    case "remove":
                        {
                            using var ws = Workspace.OpenOrCreate(wsDir);
                            ws.RemoveSource(long.Parse(args[2]));
                            Console.WriteLine("Source retirée de l'index (fichier non modifié).");
                            return 0;
                        }
                    default:
                        return Usage();
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("ERREUR : " + ex.Message);
                return 1;
            }
        }

        /// <summary>Diagnostics: shows what the attachment extractor reads from a file (in this process, without the worker).</summary>
        private static int ExtractFile(string path)
        {
            var sw = Stopwatch.StartNew();
            var r = PstBrowser.Core.Extraction.TextExtractor.Extract(Path.GetFileName(path), File.ReadAllBytes(path));
            Console.WriteLine($"{PstBrowser.Core.Extraction.AttachmentStatus.Label(r.Status)} — {r.Text.Length:N0} caractères — {sw.ElapsedMilliseconds} ms {r.Error}");
            Console.WriteLine(r.Text.Length > 3000 ? r.Text.Substring(0, 3000) + "…" : r.Text);
            return 0;
        }

        private static int Usage()
        {
            Console.WriteLine(@"pstbrowser-cli — moteur PstBrowser en ligne de commande (SQLite " + SqliteDb.Version + @")

  add      <dossier-affaire> <fichier.pst|fichier.msg|dossier>...   ajoute des sources
  index    <dossier-affaire> [--parallel N] [--sha256] [--no-attachments] [--attachment-timeout S]
                                                                     indexe (reprend là où il s'était arrêté) ; les pièces jointes sont lues dans un processus séparé
  analyze  <dossier-affaire> <id[-pj]>                                en-têtes (chemin, SPF/DKIM/DMARC, usurpation), dates, données sensibles, fil
  sensitive <dossier-affaire> [--csv fichier.csv]                     IBAN, cartes, n° de sécurité sociale, téléphones détectés (valeurs masquées)
  lookalikes <dossier-affaire>                                        domaines d'expéditeurs ressemblant à des domaines plus fréquents
  attachments <dossier-affaire> [--csv fichier.csv]                  inventaire des pièces jointes (statut, SHA-256)
  extract-file <fichier>                                             affiche le texte que l'extracteur lit dans un fichier
  sources  <dossier-affaire>                                         liste les sources
  tree     <dossier-affaire>                                         arborescence des boîtes
  search   <dossier-affaire> ""requête"" [--sort champ[:asc|:desc]] [--dedup] [--att] [--limit N] [--csv fichier.csv]
  show     <dossier-affaire> <id[-pj-pj]> [""termes""] [--html fichier.html]
  save-att <dossier-affaire> <id[-pj]> <index-pj> <fichier>
  eml      <dossier-affaire> <id[-pj]> <fichier.eml>
  remove   <dossier-affaire> <id-source>

Syntaxe de recherche : mots (ET implicite), ""expression exacte"", mot*, a OR b, -exclu,
  de: à: objet: corps: pj: (noms de pièces jointes)  pjtexte: (contenu des pièces jointes)  avant:AAAA-MM-JJ  apres:AAAA-MM-JJ
  indice:usurpation|replyto|returnpath|nomtrompeur|domaine|auth|dates|sensible|iban|carte|secu|tel   spf:|dkim:|dmarc:pass|fail|softfail|none|absent   fil:N (fil de conversation)
Chaque mot doit se trouver dans le message OU dans l'une de ses pièces jointes ; de: à: objet: corps: ne regardent que le message.");
            return 1;
        }
    }
}
