using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PstBrowser.Core.Index;
using PstBrowser.Core.Viewer;

namespace PstBrowser.Cli
{
    internal static partial class SelfTest
    {
        /// <summary>Case folders and sources moved to another place (drive, computer, operating system).</summary>
        private static void PortableCaseTests(string work, string src)
        {
            Console.WriteLine("13. Dossiers d'affaire portables : sources déplacées, dossier d'affaire déplacé");
            var root = Path.Combine(work, "portable");
            var pst = Path.Combine(src, "michelle.lokay@enron.com.pst");
            var msgDir = Path.Combine(src, "export-msg");

            // ---- sources outside the case folder, moved together (other drive, other computer)
            var export = Path.Combine(root, "E", "Export");
            Directory.CreateDirectory(Path.Combine(export, "Dupont"));
            File.Copy(pst, Path.Combine(export, "Dupont", "dupont.pst"));
            File.Copy(pst, Path.Combine(export, "martin.pst"));
            CopyDir(msgDir, Path.Combine(export, "Dupont", "msg"));
            var caseDir = Path.Combine(root, "affaire");
            long total;
            Dictionary<long, long> sample;
            using (var ws = Workspace.OpenOrCreate(caseDir))
            {
                ws.AddSources(new[] { export });
                new Indexer(ws, new IndexerOptions { Parallelism = 2, IndexAttachments = false }).Start().Wait();
                total = ws.GetCounts().total;
                sample = SampleMessages(ws);
                Check(ws.ListSources().Count == 3 && ws.AutoRelocation == null, "3 sources indexées hors du dossier d'affaire");
            }
            var moved = Path.Combine(root, "USB", "Scelle-42", "Export");
            Directory.CreateDirectory(Path.GetDirectoryName(moved));
            Directory.Move(export, moved);
            using (var ws = Workspace.OpenOrCreate(caseDir))
            {
                var sources = ws.ListSources();
                Check(sources.All(s => s.FileMissing), "sources déplacées : signalées introuvables");
                var dupont = sources.Single(s => s.DisplayName == "dupont.pst");
                var (o, n) = Workspace.InferRelocation(dupont.Path, Path.Combine(moved, "Dupont", "dupont.pst"));
                Check(Workspace.Normalize(o) == Workspace.Normalize(Path.Combine(root, "E")) && Workspace.Normalize(n) == Workspace.Normalize(Path.Combine(root, "USB", "Scelle-42")),
                    $"dossier déplacé déduit d'une seule source ({Path.GetFileName(o)} → {Path.GetFileName(n)})");
                var dry = ws.RelocateFrom(dupont, Path.Combine(moved, "Dupont", "dupont.pst"), dryRun: true);
                Check(dry.Relocated == 3 && ws.ListSources().All(s => s.FileMissing), "essai : 3 sources retrouvables, rien n'est modifié");
                var r = ws.RelocateFrom(dupont, Path.Combine(moved, "Dupont", "dupont.pst"));
                Check(r.Relocated == 3 && r.Failed == 0 && !ws.ListSources().Any(s => s.FileMissing), "les 3 sources retrouvées en indiquant l'emplacement d'une seule");
                CheckMessages(ws, sample, "messages PST et MSG relus au nouvel emplacement");
                var ix = new Indexer(ws, new IndexerOptions { Parallelism = 2, IndexAttachments = false });
                ix.Start().Wait();
                Check(ws.GetCounts().total == total && ix.Progress.Errors == 0, "aucune réindexation ni doublon après le déplacement");
            }

            // ---- safety: a file with the same name but another size is never attached to the index
            var fake = Path.Combine(root, "faux");
            Directory.CreateDirectory(Path.Combine(fake, "Export", "Dupont"));
            File.WriteAllText(Path.Combine(fake, "Export", "Dupont", "dupont.pst"), "pas un PST");
            using (var ws = Workspace.OpenOrCreate(caseDir))
            {
                var dupont = ws.ListSources().Single(s => s.DisplayName == "dupont.pst");
                var r = ws.RelocateSources(Path.Combine(moved), Path.Combine(fake, "Export"));
                Check(r.Relocated == 0 && r.Items.Any(i => i.Outcome == RelocationOutcome.SizeMismatch) && r.Items.Any(i => i.Outcome == RelocationOutcome.NotFound)
                      && r.Items.Any(i => i.Outcome == RelocationOutcome.MsgFilesMissing),
                    "refusés : PST de même nom mais d'une autre taille, dossier sans les fichiers MSG indexés ; fichier absent signalé");
                Check(ws.ListSources().All(s => !s.FileMissing && Workspace.Normalize(s.Path).StartsWith(Workspace.Normalize(moved))), "chemins inchangés après un refus");
            }

            // ---- a case built on Windows, opened on macOS / Linux (and the reverse): drive letters and separators
            using (var ws = Workspace.OpenOrCreate(caseDir))
            {
                var (wo, wn) = Workspace.InferRelocation(@"E:\Export\Dupont\a.pst", Path.Combine(root, "Scelle", "Export", "Dupont", "a.pst"));
                var (ro, rn) = Workspace.InferRelocation(@"E:\Export\a.pst", Path.Combine(root, "Scelle", "b.pst"));
                Check(wo == "E:" && Workspace.Normalize(wn) == Workspace.Normalize(Path.Combine(root, "Scelle"))
                      && ro == "E:/Export/a.pst" && Workspace.Normalize(rn) == Workspace.Normalize(Path.Combine(root, "Scelle", "b.pst")),
                    "déduction : lecteur Windows entier (E:) ; fichier renommé : cette source seule");
                var winRoot = @"E:\Scelles\42\Export";
                foreach (var s in ws.ListSources())
                {
                    string Win(string p) => winRoot + Workspace.Normalize(p).Substring(Workspace.Normalize(moved).Length).Replace('/', '\\');
                    ws.Db.Exec("UPDATE messages SET file_path=? || substr(file_path, ?) WHERE source_id=? AND file_path IS NOT NULL", Win(s.Path), s.Path.Length + 1, s.Id);
                    ws.Db.Exec("UPDATE messages SET file_path=replace(file_path, '/', '\\') WHERE source_id=? AND file_path IS NOT NULL", s.Id);
                    ws.Db.Exec("UPDATE sources SET path=? WHERE id=?", Win(s.Path), s.Id);
                }
                Check(ws.ListSources().All(s => s.FileMissing && s.Path.StartsWith(@"E:\")), "chemins Windows (E:\\…) introuvables sur ce poste");
                var r = ws.RelocateSources(@"E:\Scelles\42", Path.Combine(moved, ".."));
                Check(r.Relocated == 3 && !ws.ListSources().Any(s => s.FileMissing), "lettre de lecteur et séparateurs Windows convertis");
                CheckMessages(ws, sample, "messages relus après conversion des chemins");
            }

            // ---- the case folder moved with its sources (inside it, or next to it)
            var bundle = Path.Combine(root, "Affaires");
            var inner = Path.Combine(bundle, "Affaire-7");
            Directory.CreateDirectory(Path.Combine(inner, "pst"));
            File.Copy(pst, Path.Combine(inner, "pst", "dedans.pst"));
            Directory.CreateDirectory(Path.Combine(bundle, "Export"));
            File.Copy(pst, Path.Combine(bundle, "Export", "a-cote.pst"));
            using (var ws = Workspace.OpenOrCreate(inner))
            {
                ws.AddSources(new[] { Path.Combine(inner, "pst"), Path.Combine(bundle, "Export") });
                new Indexer(ws, new IndexerOptions { Parallelism = 2, IndexAttachments = false }).Start().Wait();
                sample = SampleMessages(ws);
                total = ws.GetCounts().total;
            }
            var bundle2 = Path.Combine(root, "Mac", "Volumes", "Disque", "Affaires");
            Directory.CreateDirectory(Path.GetDirectoryName(bundle2));
            Directory.Move(bundle, bundle2);
            using (var ws = Workspace.OpenOrCreate(Path.Combine(bundle2, "Affaire-7")))
            {
                Check(ws.AutoRelocation != null && ws.AutoRelocation.Relocated == 2 && !ws.ListSources().Any(s => s.FileMissing),
                    "dossier d'affaire déplacé : sources dedans et à côté retrouvées automatiquement");
                CheckMessages(ws, sample, "messages relus après le déplacement du dossier d'affaire");
                Check(ws.GetCounts().total == total, "index intact");
            }
            using (var ws = Workspace.OpenOrCreate(Path.Combine(bundle2, "Affaire-7")))
                Check(ws.AutoRelocation == null, "réouverture au même endroit : rien à faire");
        }

        /// <summary>One message id per source, with its subject.</summary>
        private static Dictionary<long, long> SampleMessages(Workspace ws)
        {
            var ids = new Dictionary<long, long>();
            foreach (var s in ws.ListSources())
                ids[s.Id] = ws.Db.ExecScalarLong("SELECT id FROM messages WHERE source_id=? AND indexed=1 AND error IS NULL ORDER BY id LIMIT 1", s.Id);
            return ids;
        }

        private static void CheckMessages(Workspace ws, Dictionary<long, long> sample, string what)
        {
            using var msgs = new MessageService(ws);
            bool ok = sample.Values.All(id =>
            {
                try { return !string.IsNullOrEmpty(msgs.GetView(new MessageRef(id)).BodyHtml); }
                catch (Exception ex) { Console.WriteLine("      " + ex.Message); return false; }
            });
            Check(ok, what);
        }

        private static void CopyDir(string from, string to)
        {
            foreach (var d in Directory.GetDirectories(from, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, d)));
            Directory.CreateDirectory(to);
            foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories)) File.Copy(f, Path.Combine(to, Path.GetRelativePath(from, f)));
        }
    }
}
