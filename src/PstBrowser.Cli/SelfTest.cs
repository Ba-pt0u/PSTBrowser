using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using PstBrowser.Core.Data;
using PstBrowser.Core.Index;
using PstBrowser.Core.Search;
using PstBrowser.Core.Viewer;

namespace PstBrowser.Cli
{
    /// <summary>
    /// End-to-end test of the engine on public sample files (run by the CI):
    ///   selftest &lt;work-dir&gt; &lt;dir-with-pst-files&gt; &lt;dir-with-msg-files&gt;
    /// The PST directory is expected to contain enron.pst (from github.com/epfromer/pst-extractor).
    /// </summary>
    internal static partial class SelfTest
    {
        private static int _failures;

        private static void Check(bool ok, string what)
        {
            Console.WriteLine((ok ? "  OK    " : "  ÉCHEC ") + what);
            if (!ok) _failures++;
        }

        public static int Run(string work, string pstDir, string msgDir)
        {
            if (Directory.Exists(work)) Directory.Delete(work, true);
            var src = Path.Combine(work, "sources");
            Directory.CreateDirectory(src);
            // Simulate a split export of one mailbox (two parts) plus other mailboxes
            var enron = Directory.GetFiles(pstDir, "enron.pst", SearchOption.AllDirectories).First();
            File.Copy(enron, Path.Combine(src, "michelle.lokay@enron.com.pst"));
            File.Copy(enron, Path.Combine(src, "michelle.lokay@enron.com_1.pst"));
            foreach (var ost in Directory.GetFiles(pstDir, "*.ost", SearchOption.AllDirectories).Take(2))
                if (!File.Exists(Path.Combine(src, Path.GetFileName(ost)))) File.Copy(ost, Path.Combine(src, Path.GetFileName(ost)));
            var msgs = Path.Combine(src, "export-msg");
            foreach (var m in Directory.GetFiles(msgDir, "*.msg"))
            {
                var sub = Path.GetFileName(m).StartsWith("Email", StringComparison.OrdinalIgnoreCase) ? "Boîte de réception" : "Archives/2017";
                Directory.CreateDirectory(Path.Combine(msgs, sub));
                File.Copy(m, Path.Combine(msgs, sub, Path.GetFileName(m)));
            }

            Console.WriteLine("SQLite " + SqliteDb.Version);
            Console.WriteLine("1. Ajout et indexation");
            using (var ws = Workspace.OpenOrCreate(Path.Combine(work, "affaire")))
            {
                var added = ws.AddSources(new[] { src });
                Check(added.Count >= 3, $"{added.Count} sources ajoutées");
                var michelle = added.Where(a => a.MailboxName == "michelle.lokay@enron.com").ToList();
                Check(michelle.Count == 2, "les 2 parties de la boîte découpée sont regroupées");
                var ix = new Indexer(ws, new IndexerOptions { Parallelism = 2, ComputeSha256 = true });
                ix.Start().Wait();
                var p = ix.Progress;
                Check(p.Errors == 0, $"indexation sans erreur ({p.Errors} erreur(s) {p.LastError})");
                var (total, indexed) = ws.GetCounts();
                Check(total > 300 && total == indexed, $"{indexed}/{total} éléments indexés");
                Check(ws.ListSources().All(s => s.Status == "done"), "toutes les sources sont indexées");
                Check(ws.ListSources().Where(s => s.Kind == SourceKind.Pst).All(s => s.Sha256?.Length == 64), "empreintes SHA-256 calculées");

                Console.WriteLine("2. Arborescence");
                var mb = ws.ListMailboxes();
                var mich = mb.First(m => m.Name == "michelle.lokay@enron.com");
                var tree = ws.GetFolderTree(mich.Id, hideEmpty: true);
                Check(tree.Count > 0 && tree.Sum(Total) == 142, $"arborescence fusionnée de la boîte découpée ({tree.Sum(Total)} éléments)");
                var msgBox = mb.First(m => m.Name == "sources"); // the added folder holds the MSG files: it becomes their mailbox
                Check(ws.GetFolderTree(msgBox.Id).SelectMany(Flatten).Any(f => f.Path.EndsWith("Archives/2017")), "arborescence des MSG conservée");

                Console.WriteLine("3. Recherche");
                using var search = new SearchService(ws);
                SearchResult S(string q, bool dedup = false) => search.Search(new SearchRequest { Query = q, HideDuplicates = dedup });
                Check(S("tahoe").Total == 2, "« tahoe » trouvé dans les 2 parties");
                Check(S("tahoe", dedup: true).Total == 1, "dédoublonnage par Message-ID");
                Check(S("TAHOE").Total == 2, "insensible à la casse");
                Check(S("aeroport electricite").Total >= 1, "insensible aux accents (objet « Àéroport … électricité »)");
                Check(S("\"long awaited trip\"").Total == 2, "expression exacte");
                Check(S("awai*").Total >= 2, "préfixe");
                Check(S("pj:xls").Total >= 2, "nom de pièce jointe");
                Check(S("de:cbulf").Total == 2 && S("de:tahoe").Total == 0, "champ expéditeur");
                Check(S("objet:HtmlSampleEmail").Total == 1, "champ objet");
                Check(S("tahoe -trip").Total == 0, "exclusion");
                Check(S("tahoe OR neverland").Total == 3, "OU");
                Check(S("ввести").Total == 1, "texte cyrillique (corps RTF)");
                Check(S("\"inner\" pj:pdf").Total >= 1, "contenu et pièces jointes des messages joints");
                Check(search.Search(new SearchRequest { Query = "apres:2021-01-01" }).Total > 0, "filtre de date dans la requête");
                Check(search.Search(new SearchRequest { MailboxId = mich.Id, OnlyWithAttachments = true }).Rows.All(r => r.HasAttachments), "filtre « avec PJ »");
                var bad = S("-seulement");
                Check(bad.Error != null, "requête uniquement négative refusée proprement");
                var odd = S("a\"b (c* OR) NEAR/2 : ^x");
                Check(odd.Error == null, "caractères spéciaux sans erreur");

                SortTests(search);

                Console.WriteLine("4. Lecture, rendu et exports");
                using var msgSvc = new MessageService(ws);
                var hit = S("tahoe").Rows.First();
                var view = msgSvc.GetView(new MessageRef(hit.Id), QueryParser.Parse("tahoe").HighlightTerms);
                Check(view.Subject == "TRIP INFO" && view.Attachments.Count == 2, "message PST ouvert avec ses 2 pièces jointes");
                Check(view.BodyHtml.Contains("pl-hit"), "termes surlignés dans le corps");
                Check(view.BodyHtml.Contains("Content-Security-Policy"), "politique de sécurité présente");
                var attPath = Path.Combine(work, "pj.doc");
                msgSvc.SaveAttachment(new MessageRef(hit.Id), 0, attPath);
                var head = File.ReadAllBytes(attPath).Take(4).ToArray();
                // PidTagAttachSize includes MAPI overhead: the file is slightly smaller than the announced size
                Check(new FileInfo(attPath).Length > 20000 && head.SequenceEqual(new byte[] { 0xD0, 0xCF, 0x11, 0xE0 }), "pièce jointe extraite (document Word valide)");

                var outer = S("objet:\"Outer mail\"").Rows.First();
                var ov = msgSvc.GetView(new MessageRef(outer.Id));
                var emb = ov.Attachments.First(a => a.IsEmbeddedMessage);
                var inner = msgSvc.GetView(new MessageRef(outer.Id).Child(emb.Index));
                Check(inner.Subject == "Inner mail" && inner.Attachments.Count == 2, "message joint ouvert (avec ses propres pièces jointes)");

                var html = S("objet:HtmlSampleEmail").Rows.First();
                var hv = msgSvc.GetView(new MessageRef(html.Id));
                Check(hv.BodyFormat == "HTML (RTF)" && hv.BodyHtml.Contains("Prince"), "HTML encapsulé dans du RTF restitué");
                var inlineMsg = S("objet:\"This is the subject\"").Rows.First();
                var iv = msgSvc.GetView(new MessageRef(inlineMsg.Id));
                Check(iv.InlineImages.Count == 1 && iv.BodyHtml.Contains("https://" + MessageService.VirtualHost + "/inline/"), "image intégrée servie localement");
                var img = msgSvc.GetAttachmentBytes(new MessageRef(inlineMsg.Id), iv.InlineImages.Values.First());
                Check(img != null && img.Length > 1000 && img[0] == 0xFF && img[1] == 0xD8, "octets de l'image intégrée (JPEG)");

                RtfTests(search, msgSvc);
                AttachmentSearchTests(ws, search, msgSvc);
                AnalysisSampleTests(ws, search, msgSvc, work);

                var eml = Path.Combine(work, "outer.eml");
                msgSvc.ExportEml(new MessageRef(outer.Id), eml);
                var emlText = File.ReadAllText(eml);
                Check(emlText.Contains("MIME-Version: 1.0") && emlText.Contains("message/rfc822") && emlText.Contains("OUTER 1.pdf"), "export .eml (avec message joint et PJ)");
                var csv = Path.Combine(work, "export.csv");
                int n = SearchService.ExportCsv(search.SearchAll(new SearchRequest { Query = "meeting" }, pageSize: 7), csv);
                Check(n == S("meeting").Total && File.ReadAllLines(csv).Length == n + 1, $"export CSV paginé ({n} lignes)");

                var unsafeHtml = BodyRenderer.Sanitize("<img src=\"https://tracker.example/p.gif\"><script>alert(1)</script><a href=\"javascript:x()\" onclick=\"y()\">l</a><div style=\"background:url(http://x/y)\">", out bool blocked);
                Check(blocked && !unsafeHtml.Contains("tracker.example") && !unsafeHtml.Contains("<script") && !unsafeHtml.Contains("onclick") &&
                      !unsafeHtml.Contains("javascript:") && !unsafeHtml.Contains("http://x"), "contenu actif et ressources distantes neutralisés");

                Console.WriteLine("5. Gestion des sources");
                var before = ws.GetCounts().total;
                var part2 = ws.ListSources().First(s => s.DisplayName == "michelle.lokay@enron.com_1.pst");
                ws.RemoveSource(part2.Id);
                Check(ws.GetCounts().total == before - 71 && S("tahoe").Total == 1, "retrait d'une source (fichier intact)");
                Check(File.Exists(Path.Combine(src, "michelle.lokay@enron.com_1.pst")), "le fichier source n'est pas supprimé");
            }

            Console.WriteLine("6. Reprise après interruption");
            using (var ws = Workspace.OpenOrCreate(Path.Combine(work, "reprise")))
            {
                var many = Path.Combine(work, "many");
                Directory.CreateDirectory(many);
                for (int i = 0; i < 40; i++) File.Copy(Path.Combine(src, "michelle.lokay@enron.com.pst"), Path.Combine(many, $"custodian{i:00}.pst"));
                ws.AddSources(new[] { many });
                for (int round = 0; round < 3; round++)
                {
                    var ix = new Indexer(ws, new IndexerOptions { Parallelism = 2, BatchSize = 20, IndexAttachments = false });
                    var t = ix.Start();
                    Thread.Sleep(150 + round * 100);
                    ix.Stop();
                    t.Wait();
                }
                new Indexer(ws, new IndexerOptions { IndexAttachments = false }).Start().Wait();
                var (total, indexed) = ws.GetCounts();
                long fts, dup;
                using (var db = ws.Open())
                {
                    fts = db.ExecScalarLong("SELECT COUNT(*) FROM fts");
                    dup = db.ExecScalarLong("SELECT COUNT(*) FROM (SELECT source_id, nid, COUNT(*) c FROM messages GROUP BY 1,2 HAVING c>1)");
                }
                Check(total == 40 * 71 && indexed == total, $"tous les éléments indexés après 3 interruptions ({indexed}/{total})");
                Check(fts == total, "index plein texte cohérent (ni doublon ni manque)");
                Check(dup == 0, "aucun message en double");
            }

            MigrationTests(work, src);
            MigrationV3Tests(work);
            WorkerTests(work);
            AttachmentResumeTests(work, src);
            SyntheticCaseTests(work);

            Console.WriteLine(_failures == 0 ? "\nTOUS LES TESTS SONT PASSÉS" : $"\n{_failures} TEST(S) EN ÉCHEC");
            return _failures == 0 ? 0 : 3;
        }

        private static long Total(FolderNode n) => n.Total;
        private static IEnumerable<FolderNode> Flatten(FolderNode n) => new[] { n }.Concat(n.Children.SelectMany(Flatten));
    }
}
