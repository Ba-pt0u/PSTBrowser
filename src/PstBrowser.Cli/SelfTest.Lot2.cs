using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PstBrowser.Core.Analysis;
using PstBrowser.Core.Data;
using PstBrowser.Core.Index;
using PstBrowser.Core.Search;
using PstBrowser.Core.Viewer;

namespace PstBrowser.Cli
{
    /// <summary>Tests of version 1.2: header analysis, date anomalies, sensitive data, look-alike domains, threads.</summary>
    internal static partial class SelfTest
    {
        /// <summary>Unit tests that need no sample file (selftest-unit).</summary>
        public static int RunUnit()
        {
            Console.WriteLine("Analyse : tests unitaires");
            AnalysisUnitTests();
            Console.WriteLine(_failures == 0 ? "\nTOUS LES TESTS SONT PASSÉS" : $"\n{_failures} TEST(S) EN ÉCHEC");
            return _failures == 0 ? 0 : 3;
        }


        // ------------------------------------------------------------------ synthetic messages (.msg built in memory)

        private sealed class Synth
        {
            public string Subject = "Sujet", Body = "Corps", SenderName = "Expéditeur", SenderEmail = "exp@example.fr", Headers, MessageId, InReplyTo, References, Topic;
            public DateTime? Delivery, Submit, Created, Modified;
        }

        /// <summary>A minimal .msg file (compound file with MAPI property streams) so that the whole pipeline can be tested without sample files.</summary>
        private static byte[] BuildMsg(Synth m)
        {
            var streams = new List<(string, byte[])>();
            void Str(int id, string value) { if (value != null) streams.Add(($"__substg1.0_{id:X4}001F", Encoding.Unicode.GetBytes(value))); }
            Str(0x0037, m.Subject); Str(0x1000, m.Body); Str(0x001A, "IPM.Note"); Str(0x0042, m.SenderName); Str(0x5D02, m.SenderEmail);
            Str(0x007D, m.Headers); Str(0x1035, m.MessageId); Str(0x1042, m.InReplyTo); Str(0x1039, m.References); Str(0x0070, m.Topic);
            var props = new List<byte>(new byte[32]);
            void Time(uint tag, DateTime? t)
            {
                if (!t.HasValue) return;
                props.AddRange(BitConverter.GetBytes(tag)); props.AddRange(BitConverter.GetBytes(6));
                props.AddRange(BitConverter.GetBytes(DateTime.SpecifyKind(t.Value, DateTimeKind.Utc).ToFileTimeUtc()));
            }
            Time(0x0E060040, m.Delivery); Time(0x00390040, m.Submit); Time(0x30070040, m.Created); Time(0x30080040, m.Modified);
            streams.Add(("__properties_version1.0", props.ToArray()));
            return BuildCfb(streams.ToArray());
        }

        private static void SyntheticCaseTests(string work)
        {
            Console.WriteLine("11. Enquête : en-têtes, dates, fils, usurpation, données sensibles (messages de synthèse)");
            var dir = Path.Combine(work, "synth-src");
            Directory.CreateDirectory(dir);
            int n = 0;
            void Write(Synth m) => File.WriteAllBytes(Path.Combine(dir, $"m{++n:00}.msg"), BuildMsg(m));
            var t0 = new DateTime(2025, 10, 7, 8, 0, 0, DateTimeKind.Utc);

            Write(new Synth
            {
                Subject = "Virement urgent", SenderName = "Paul Durand", SenderEmail = "paul@banque.fr", Headers = SpoofedHeaders, MessageId = "<abc@banque.fr>", Topic = "Virement urgent",
                Body = "Merci de virer 12 000 EUR sur FR14 2004 1010 0505 0001 3M02 606. Carte 4111 1111 1111 1111. Sécu 1 85 05 78 006 084 91. Tél 06 12 34 56 78.",
                Submit = t0.AddHours(3), Delivery = t0, Created = t0.AddDays(2), Modified = t0.AddDays(-1),
            });
            Write(new Synth { Subject = "Contrat Alpha", MessageId = "<t1@x>", Topic = "Contrat Alpha", SenderEmail = "a@alpha.fr", SenderName = "Alice", Submit = t0, Delivery = t0.AddSeconds(3), Body = "Premier message" });
            Write(new Synth { Subject = "RE: Contrat Alpha", MessageId = "<t2@x>", InReplyTo = "<t1@x>", Topic = "Contrat Alpha", SenderEmail = "b@beta.fr", SenderName = "Bob", Submit = t0.AddHours(1), Delivery = t0.AddHours(1), Body = "Réponse" });
            Write(new Synth { Subject = "RE: RE: Contrat Alpha", MessageId = "<t3@x>", References = "<t1@x> <t2@x>", Topic = "Contrat Alpha", SenderEmail = "a@alpha.fr", SenderName = "Alice", Submit = t0.AddHours(2), Delivery = t0.AddHours(2), Body = "Suite" });
            Write(new Synth { Subject = "Réunion ordinaire", MessageId = "<n1@x>", Topic = "Réunion ordinaire", SenderEmail = "c@gamma.fr", SenderName = "Claire", Submit = t0, Delivery = t0, Body = "Rien de particulier : appelez le service." });
            for (int i = 0; i < 6; i++)
                Write(new Synth { Subject = "Point " + i, MessageId = $"<jm{i}@societe.fr>", SenderEmail = "jean.martin@societe.fr", SenderName = "Jean Martin", Submit = t0.AddDays(i), Delivery = t0.AddDays(i), Body = "Point d'avancement" });
            Write(new Synth { Subject = "Changement de RIB", MessageId = "<x1@evil.ru>", SenderEmail = "jean.martin@societe-secure.ru", SenderName = "Jean Martin", Submit = t0, Delivery = t0, Body = "Voici le nouveau RIB." });
            Write(new Synth { Subject = "Facture", MessageId = "<x2@soc1ete.fr>", SenderEmail = "compta@soc1ete.fr", SenderName = "Comptabilité", Submit = t0, Delivery = t0, Body = "Facture jointe" });
            Write(new Synth { Subject = "Support", MessageId = "<x3@evil.ru>", SenderEmail = "x@evil.ru", SenderName = "support@paypal.com", Submit = t0, Delivery = t0, Body = "Votre compte" });

            using var ws = Workspace.OpenOrCreate(Path.Combine(work, "synth"));
            ws.AddSources(new[] { dir });
            var ix = new Indexer(ws, new IndexerOptions { Parallelism = 1 });
            ix.Start().Wait();
            Check(ix.Progress.Errors == 0 && !ws.HasPendingWork(), $"indexation sans erreur, aucune analyse en attente ({ix.Progress.LastError})");
            using var search = new SearchService(ws);
            using var msgs = new MessageService(ws);
            SearchResult S(string q) => search.Search(new SearchRequest { Query = q, Limit = 100 });

            // header analysis and search filters
            var spoofed = S("objet:\"Virement urgent\"").Rows.Single();
            Check(spoofed.Analysed && spoofed.Spf == "fail" && spoofed.Dmarc == "fail" && spoofed.ReplyTo == "paul.durand@gmail.com" && spoofed.ReturnPath == "bounce@evil.ru", "en-têtes lus à l'indexation (SPF, DMARC, Reply-To, Return-Path)");
            Check(S("indice:usurpation").Rows.Any(r => r.Id == spoofed.Id) && S("indice:replyto").Total == 1 && S("indice:returnpath").Total == 1 && S("indice:auth").Total == 1, "filtres indice:usurpation, replyto, returnpath, auth");
            Check(S("spf:fail").Total == 1 && S("dmarc:echec").Total == 1 && S("dkim:none").Total == 1 && S("spf:absent").Total == 13, "filtres spf:, dkim:, dmarc: (pass / fail / none / absent)");
            Check(S("-indice:usurpation virement").Total == 0 && S("virement -indice:replyto").Total == 0 && S("indice:usurpation").Total >= 4, "filtres combinables et négatifs");
            Check(S("indice:nomtrompeur").Rows.Any(r => r.Subject == "Support"), "nom affiché trompeur (adresse d'un autre domaine dans le nom)");
            var rib = S("objet:\"Changement de RIB\"").Rows.Single();
            Check((rib.SpoofFlags & (int)SpoofFlags.NameImpersonation) != 0 && S("indice:nomtrompeur").Rows.Any(r => r.Id == rib.Id), "même nom affiché qu'un correspondant habituel, adresse d'un autre organisme");
            var fact = S("objet:Facture").Rows.Single();
            Check((fact.SpoofFlags & (int)SpoofFlags.LookAlikeDomain) != 0 && S("indice:domaine").Total == 1, "domaine ressemblant (soc1ete.fr / societe.fr)");
            Check(S("objet:\"Réunion ordinaire\"").Rows.Single().SpoofFlags == 0, "message sans particularité : aucun indice");
            Check(S("indice:inconnu").Error != null && S("fil:abc").Error != null && S("spf:bizarre").Error != null, "valeur de filtre inconnue : erreur explicite");

            // analysis window data
            var an = msgs.GetAnalysis(new MessageRef(spoofed.Id));
            Check(an.Live.Header.Hops.Count == 2 && an.Live.Header.Auth.Spf == "fail" && an.Live.Header.Findings.Count == 3, "fenêtre d'analyse : chemin des serveurs, authentification, 3 indices");
            Check(an.Live.Dates.Flags.HasFlag(DateFlags.DeliveredBeforeSent) && an.Live.Dates.Flags.HasFlag(DateFlags.ModifiedBeforeCreated), "anomalies de dates détectées à partir des propriétés du message");
            Check(S("indice:dates").Total == 1 && (spoofed.DateFlags & (int)DateFlags.DeliveredBeforeSent) != 0, "filtre indice:dates");
            var anRib = msgs.GetAnalysis(new MessageRef(rib.Id));
            Check(anRib.CaseFindings.Any(f => f.Code == "name-reuse" && f.Text.Contains("jean.martin@societe.fr")), "explication : le nom est habituellement associé à une autre adresse");
            Check(msgs.GetAnalysis(new MessageRef(fact.Id)).CaseFindings.Any(f => f.Code == "look-alike" && f.Text.Contains("societe.fr")), "explication : domaine ressemblant à un domaine fréquent");
            var view = msgs.GetView(new MessageRef(spoofed.Id));
            Check(view.AlertText.Contains("Reply-To") && view.AlertText.Contains("Données sensibles") && view.AlertText.Contains("IBAN") && view.AlertText.Contains("Dates incohérentes"), "bandeau d'alerte du lecteur");

            // threads
            var a1 = S("objet:\"Contrat Alpha\"").Rows.OrderBy(r => r.Date).ToList();
            var fil = S("fil:" + a1[0].ThreadId);
            Check(a1.Count == 3 && a1.All(r => r.ThreadId == a1[0].ThreadId && r.ThreadCount == 3) && fil.Total == 3, "fil de conversation reconstitué (In-Reply-To, References) et filtre fil:N");
            Check(S("objet:\"Réunion ordinaire\"").Rows.Single().ThreadCount == 1, "message isolé : fil d'un message");

            // sensitive data
            Check(S("indice:iban").Total == 1 && S("indice:carte").Total == 1 && S("indice:secu").Total == 1 && S("indice:tel").Total == 1 && S("indice:sensible").Total == 1, "données sensibles : IBAN, carte, sécurité sociale, téléphone");
            Check(an.Sensitive.Count == 4 && an.Sensitive.All(i => !i.Masked.Contains("2004") && !i.Masked.Contains("4111 1111") && i.Location == "Corps du message"), "valeurs masquées dans l'index");
            var iban = an.Sensitive.Single(i => i.Kind == SensitiveKind.Iban);
            Check(msgs.RevealSensitive(iban.Id) == "FR14 2004 1010 0505 0001 3M02 606", "valeur complète relue à la demande depuis le texte indexé");
            using (var db = ws.Open())
            {
                var raw = new StringBuilder();
                var st = db.Prepare("SELECT masked FROM sensitive");
                while (st.Step()) raw.Append(st.GetString(0)).Append('|');
                st.Reset();
                Check(!raw.ToString().Contains("3M02") && !raw.ToString().Contains("006 084"), "l'index ne contient aucune valeur sensible en clair dans la table des détections");
            }
            var csv = Path.Combine(work, "synth.csv");
            SearchService.ExportCsv(search.SearchAll(new SearchRequest { Query = "indice:usurpation" }), csv);
            var csvText = File.ReadAllText(csv);
            Check(csvText.Contains("Reply-To différent") && csvText.Contains("Authentification en échec") && csvText.Contains("SPF;DKIM;DMARC"), "export CSV : indices d'usurpation et authentification");

            // re-running does nothing, a new message updates the case-wide analysis
            var before = ws.HasPendingWork();
            Write(new Synth { Subject = "Re: Contrat Alpha", MessageId = "<t4@x>", InReplyTo = "<t3@x>", Topic = "Contrat Alpha", SenderEmail = "b@beta.fr", SenderName = "Bob", Submit = t0.AddHours(5), Delivery = t0.AddHours(5), Body = "Encore" });
            ws.AddSources(new[] { dir });
            Check(!before, "rien à faire sur un index à jour");
        }

        /// <summary>Schema 2 → 3: the existing messages are kept, only their headers are analysed again.</summary>
        private static void MigrationV3Tests(string work)
        {
            Console.WriteLine("8. Migration du schéma 2 vers 3");
            var folder = Path.Combine(work, "migration");
            long total;
            using (var ws = Workspace.OpenOrCreate(folder)) total = ws.GetCounts().total;
            using (var db = new SqliteDb(Path.Combine(folder, Workspace.DbFileName)))
            {
                db.Exec("DROP INDEX messages_thread");
                db.Exec("DROP TABLE sensitive");
                db.Exec("DROP TABLE lookalikes");
                foreach (var c in new[] { "in_reply_to", "refs", "conv_key", "created_date", "modified_date", "reply_to", "return_path", "auth_spf", "auth_dkim", "auth_dmarc", "spoof_flags", "date_flags", "thread_id", "thread_count", "sens_flags", "sens_count", "sens_state", "hdr_done" })
                    db.Exec($"ALTER TABLE messages DROP COLUMN {c}");
                db.Exec("DELETE FROM meta WHERE key='derived_dirty'");
                db.Exec("UPDATE meta SET value='2' WHERE key='schema'");
            }
            using (var ws = Workspace.OpenOrCreate(folder))
            {
                Check(ws.GetMeta("schema") == "3", "version du schéma passée à 3");
                using (var db = ws.Open())
                {
                    Check(db.ExecScalarLong("SELECT COUNT(*) FROM messages WHERE indexed=1") == total && !ws.HasPendingSources(), "les messages déjà indexés restent indexés (aucune réindexation complète)");
                    Check(db.ExecScalarLong("SELECT COUNT(*) FROM messages WHERE hdr_done=0") == total && ws.HasPendingHeaderAnalysis() && ws.DerivedDirty, "analyse des en-têtes et analyse transversale en attente");
                    Check(db.ExecScalarLong("SELECT COUNT(*) FROM sqlite_master WHERE name IN ('sensitive','lookalikes')") == 2, "tables des données sensibles et des domaines créées");
                }
                using (var s = new SearchService(ws)) Check(s.Search(new SearchRequest { Query = "tahoe" }).Total == 1, "recherche utilisable avant l'analyse");
                var ix = new Indexer(ws, new IndexerOptions { Parallelism = 2 });
                ix.Start().Wait();
                Check(ix.Progress.Errors == 0 && !ws.HasPendingWork(), "analyse terminée sans erreur");
                using (var db = ws.Open())
                {
                    Check(db.ExecScalarLong("SELECT COUNT(*) FROM messages WHERE hdr_done=1") == total, "en-têtes de tous les messages analysés");
                    Check(db.ExecScalarLong("SELECT COUNT(*) FROM messages WHERE thread_id IS NOT NULL") == total, "fil calculé pour chaque message");
                    Check(db.ExecScalarLong("SELECT COUNT(*) FROM messages WHERE conv_key IS NOT NULL") > 30, "index de conversation Outlook conservé");
                }
            }
        }

        /// <summary>Header analysis, threads and sensitive data on the sample files of the main workspace.</summary>
        private static void AnalysisSampleTests(Workspace ws, SearchService search, MessageService msgs, string work)
        {
            Console.WriteLine("4d. Enquête sur les fichiers d'exemple");
            SearchResult S(string q) => search.Search(new SearchRequest { Query = q, HideDuplicates = false, Limit = 500 });
            using (var db = ws.Open())
            {
                Check(db.ExecScalarLong("SELECT COUNT(*) FROM messages WHERE indexed=1 AND hdr_done=0") == 0 && !ws.DerivedDirty, "tous les messages analysés, analyse transversale terminée");
                Check(db.ExecScalarLong("SELECT COUNT(*) FROM messages WHERE thread_id IS NULL AND indexed=1") == 0, "chaque message appartient à un fil");
            }
            Check(S("spf:pass").Total > 5 && S("dmarc:pass").Total > 5 && S("dkim:pass").Total > 5, "SPF / DKIM / DMARC « pass » lus dans les en-têtes réels d'Outlook.com");
            var invite = S("objet:Accepted meeting").Rows.FirstOrDefault(r => r.Subject != null && r.Subject.StartsWith("Accepted: meeting with attachments"));
            Check(invite != null, "invitation Google Agenda retrouvée");
            if (invite != null)
            {
                var an = msgs.GetAnalysis(new MessageRef(invite.Id));
                Check(an.Live.Header.Hops.Count >= 4 && an.Live.Header.Hops.All(h => h.Time.HasValue) && an.Live.Header.Auth.Spf == "pass", "chemin des serveurs lu (au moins 4 sauts datés)");
                Check(an.Live.Header.Flags.HasFlag(SpoofFlags.ReturnPathMismatch) && S("indice:returnpath").Rows.Any(r => r.Id == invite.Id), "Return-Path d'un autre domaine signalé (typique d'un service d'envoi)");
                Check(an.Live.Dates.Dates.Count(d => d.Value.HasValue) >= 5, "dates du message et des serveurs rapprochées");
            }
            var multi = S("objet:\"Thanksgiving Day\"").Rows.FirstOrDefault(r => r.ThreadCount > 1);
            Check(multi != null && S("fil:" + multi.ThreadId).Total == multi.ThreadCount, "fil : filtre fil:N renvoie tous les messages du fil");
            Check(S("indice:tel").Total >= 1 && S("indice:iban").Error == null, "données sensibles détectées dans le texte indexé");
            var tel = S("indice:tel").Rows.First();
            var items = msgs.GetAnalysis(new MessageRef(tel.Id)).Sensitive;
            Check(items.Count >= 1 && items.All(i => i.Masked.Contains("**")) && msgs.RevealSensitive(items[0].Id).Count(char.IsDigit) >= 8, "valeur masquée dans la liste, complète à la demande");
            var csv = Path.Combine(work, "sensible.csv");
            var s2 = new SearchService(ws);
            Check(s2.Search(new SearchRequest { Query = "indice:sensible", SortBy = "sensitive" }).Error == null, "tri sur le nombre de données sensibles");
        }

        private const string SpoofedHeaders =
            "Received: from mail-out.example.com (mail-out.example.com [203.0.113.5])\r\n\tby mx.dest.fr (Postfix) with ESMTPS id ABC123;\r\n\tTue, 7 Oct 2025 10:00:05 +0200 (CEST)\r\n" +
            "Received: from [10.0.0.4] (unknown [198.51.100.7])\r\n\tby mail-out.example.com with ESMTP; Tue, 7 Oct 2025 08:00:01 +0000 (UTC)\r\n" +
            "Authentication-Results: mx.dest.fr; spf=fail smtp.mailfrom=evil.ru; dkim=none; dmarc=fail header.from=banque.fr\r\n" +
            "From: \"Paul Durand\" <paul@banque.fr>\r\nReply-To: paul.durand@gmail.com\r\nReturn-Path: <bounce@evil.ru>\r\nDate: Tue, 7 Oct 2025 08:00:00 +0000\r\n" +
            "Message-ID: <abc@banque.fr>\r\nSubject: Virement\r\n\r\ncorps ignoré";

        private static void AnalysisUnitTests()
        {
            // ---- headers
            var a = HeaderAnalyzer.Analyze(SpoofedHeaders, "Paul Durand", "paul@banque.fr");
            Check(a.HasHeaders && a.Hops.Count == 2, "chemin des serveurs : 2 sauts lus dans les Received");
            Check(a.Hops[0].By == "mail-out.example.com" && a.Hops[0].FromIp == "198.51.100.7" && a.Hops[1].FromIp == "203.0.113.5", "plus ancien d'abord, adresses IP (les adresses privées ne sont pas retenues)");
            Check(a.Hops[0].Time == new DateTime(2025, 10, 7, 8, 0, 1, DateTimeKind.Utc) && a.Hops[1].Time == new DateTime(2025, 10, 7, 8, 0, 5, DateTimeKind.Utc) && a.Hops[1].Delay == TimeSpan.FromSeconds(4), "dates des serveurs en UTC (fuseau +0200 et commentaires), délai entre sauts");
            Check(a.Auth.Spf == "fail" && a.Auth.Dkim == "none" && a.Auth.Dmarc == "fail" && a.Auth.SpfDomain == "evil.ru" && a.Auth.DmarcDomain == "banque.fr", "résultats SPF / DKIM / DMARC");
            Check(a.Flags.HasFlag(SpoofFlags.ReplyToMismatch) && a.Flags.HasFlag(SpoofFlags.ReturnPathMismatch) && a.Flags.HasFlag(SpoofFlags.AuthFailure) && !a.Flags.HasFlag(SpoofFlags.MisleadingName), "indices : Reply-To, Return-Path et authentification");
            Check(a.Findings.Count == 3 && a.Findings.All(f => f.Text.Length > 40), "chaque indice est expliqué en français");
            var legit = HeaderAnalyzer.Analyze("Received: from a.example.fr by b.example.fr; Tue, 7 Oct 2025 10:00:00 +0200\r\nAuthentication-Results: b.example.fr; spf=pass smtp.mailfrom=example.fr; dkim=pass header.d=example.fr; dmarc=pass header.from=example.fr\r\nFrom: Zoé <zoe@example.fr>\r\nReply-To: zoe@mail.example.fr\r\nReturn-Path: <zoe@example.fr>\r\n\r\n", "Zoé", "zoe@example.fr");
            Check(legit.Flags == SpoofFlags.None && legit.Auth.Spf == "pass" && legit.Auth.Dkim == "pass" && legit.Auth.Dmarc == "pass", "message légitime : aucun indice (sous-domaine du même organisme accepté)");
            var exo = HeaderAnalyzer.Analyze("Authentication-Results: spf=pass (sender IP is 1.2.3.4) smtp.mailfrom=a.fr; dkim=pass (signature was verified) header.d=a.fr; dmarc=pass action=none header.from=a.fr;compauth=pass reason=100\r\nAuthentication-Results: spf=fail smtp.mailfrom=autre.fr\r\n\r\n", null, "x@a.fr");
            Check(exo.Auth.Spf == "pass" && exo.Auth.Dmarc == "pass", "plusieurs Authentication-Results : le premier (serveur récepteur) fait foi");
            Check(HeaderAnalyzer.Analyze("Received-SPF: Pass (protection.outlook.com: domain of a.fr designates 1.2.3.4)\r\n\r\n", null, "x@a.fr").Auth.Spf == "pass", "repli sur Received-SPF");
            Check(!HeaderAnalyzer.Analyze("", "x", "x@a.fr").HasHeaders && HeaderAnalyzer.Analyze(null, "x", "x@a.fr").Flags == SpoofFlags.None, "sans en-têtes : pas d'analyse, pas d'indice");
            Check(HeaderAnalyzer.HasMisleadingDisplayName("support@paypal.com", "x@evil.ru", out var shown) && shown == "support@paypal.com", "nom affiché trompeur : adresse d'un autre domaine");
            Check(!HeaderAnalyzer.HasMisleadingDisplayName("Paul Durand", "paul@banque.fr", out _) && !HeaderAnalyzer.HasMisleadingDisplayName("paul@banque.fr", "paul@mail.banque.fr", out _), "nom affiché normal ou du même organisme : pas d'indice");
            Check(DomainUtil.Organisational("smtp.mail.example.co.uk") == "example.co.uk" && DomainUtil.Organisational("a.b.example.fr") == "example.fr" && DomainUtil.Organisational("example.fr") == "example.fr", "domaine de l'organisme (co.uk, fr)");
            Check(MailHeaders.ParseDate("Tue, 7 Oct 2025 10:00:05 +0200 (CEST)") == new DateTime(2025, 10, 7, 8, 0, 5, DateTimeKind.Utc) &&
                  MailHeaders.ParseDate("7 Oct 25 10:00 EST") == new DateTime(2025, 10, 7, 15, 0, 0, DateTimeKind.Utc) && MailHeaders.ParseDate("n'importe quoi") == null, "dates RFC 5322 (fuseaux numériques et nommés)");

            // ---- look-alike domains
            var counts = new Dictionary<string, long>
            {
                ["paypal.com"] = 50, ["paypa1.com"] = 1, ["paypal.co"] = 2, ["paypall.com"] = 1, ["microsoft.com"] = 200, ["rnicrosoft.com"] = 1,
                ["example.org"] = 100, ["examp1e.org"] = 1, ["unrelated.net"] = 3, ["xn--pypal-4ve.com"] = 1, ["autre-societe.fr"] = 2, ["mail.paypal.com"] = 5,
            };
            var look = DomainUtil.FindLookAlikes(counts).ToDictionary(l => l.Domain, l => l);
            Check(look.TryGetValue("paypa1.com", out var l1) && l1.Resembles == "paypal.com" && l1.Kind == "homoglyph", "domaine ressemblant : paypa1.com (chiffre 1 pour la lettre l)");
            Check(look.TryGetValue("rnicrosoft.com", out var l2) && l2.Resembles == "microsoft.com", "domaine ressemblant : rnicrosoft.com (« rn » pour « m »)");
            Check(look.TryGetValue("paypal.co", out var l3) && l3.Kind == "suffix" && look.TryGetValue("paypall.com", out var l4) && l4.Kind == "typo", "même nom avec une autre extension, faute de frappe d'une lettre");
            Check(look.TryGetValue("xn--pypal-4ve.com", out var l5) && l5.Kind != null && look.ContainsKey("examp1e.org"), "domaine en punycode, autre chiffre pour lettre");
            Check(!look.ContainsKey("unrelated.net") && !look.ContainsKey("autre-societe.fr") && !look.ContainsKey("paypal.com") && !look.ContainsKey("mail.paypal.com"), "domaines sans rapport ou fréquents : pas d'indice");

            // ---- dates
            var d0 = new DateTime(2025, 10, 7, 8, 0, 0, DateTimeKind.Utc);
            var now = new DateTime(2025, 10, 20, 0, 0, 0, DateTimeKind.Utc);
            var ok = DateAnalyzer.Analyze(d0.AddSeconds(5), d0, d0.AddSeconds(5), d0.AddMinutes(30), d0, a.Hops, now);
            Check(ok.Flags == DateFlags.None, "dates cohérentes : aucun indice");
            var backwards = new List<ReceivedHop> { new ReceivedHop { Index = 1, Time = d0 }, new ReceivedHop { Index = 2, Time = d0.AddHours(-1) } };
            var bad = DateAnalyzer.Analyze(d0, d0.AddHours(3), d0.AddDays(2), d0.AddDays(-1), d0.AddDays(-3), backwards, now);
            Check(bad.Flags.HasFlag(DateFlags.DeliveredBeforeSent) && bad.Flags.HasFlag(DateFlags.ModifiedBeforeCreated) && bad.Flags.HasFlag(DateFlags.HeaderDateSkew) && bad.Flags.HasFlag(DateFlags.ModifiedBeforeDelivered) && bad.Flags.HasFlag(DateFlags.ReceivedChainBackwards),
                "anomalies : reçu avant envoyé, modifié avant créé, en-tête décalé, chaîne de serveurs à rebours");
            Check(DateAnalyzer.Analyze(now.AddDays(30), null, null, null, null, null, now).Flags.HasFlag(DateFlags.FutureDate) && DateAnalyzer.Analyze(new DateTime(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc), null, null, null, null, null, now).Flags.HasFlag(DateFlags.ImplausiblyOld), "dates futures et antérieures à 1995");
            Check(DateAnalyzer.Analyze(d0.AddMinutes(-3), d0, null, null, null, null, now).Flags == DateFlags.None, "tolérance de 5 minutes sur les horloges");

            // ---- sensitive data
            var text = "Merci de virer sur FR14 2004 1010 0505 0001 3M02 606 (ou DE89370400440532013000), carte 4111 1111 1111 1111, " +
                       "sécu 1 85 05 78 006 084 91 / corse 1 85 05 2A 006 084 35, tél 06 12 34 56 78 ou +33 6 12 34 56 78 ou +1 (415) 555-2671.";
            var hits = SensitiveScanner.Scan(text);
            int Count(SensitiveKind k) => hits.Count(h => h.Kind == k);
            Check(Count(SensitiveKind.Iban) == 2 && hits.Any(h => h.Value.StartsWith("FR14")), "IBAN français et allemand (clé mod 97)");
            Check(Count(SensitiveKind.Card) == 1 && Count(SensitiveKind.Nir) == 2, "carte bancaire (Luhn) et deux numéros de sécurité sociale (clé, Corse 2A)");
            Check(Count(SensitiveKind.Phone) == 3, "trois téléphones (français, +33, international)");
            Check(hits.All(h => h.Masked.Length > 0 && !h.Masked.Contains("2004 1010") && !h.Masked.Contains("4111 1111 1111") && !h.Masked.Contains("006 084")) &&
                  hits.First(h => h.Kind == SensitiveKind.Iban).Masked == "FR14 **** **** 2606" && hits.First(h => h.Kind == SensitiveKind.Card).Masked == "**** **** **** 1111", "valeurs masquées (IBAN, carte, sécurité sociale, téléphone)");
            Check(SensitiveScanner.Scan("IBAN FR14 2004 1010 0505 0001 3M02 607").Count == 0 && SensitiveScanner.Scan("carte 4111 1111 1111 1112").Count == 0 && SensitiveScanner.Scan("sécu 1 85 05 78 006 084 92").Count == 0,
                "clés de contrôle fausses : rien n'est signalé");
            Check(SensitiveScanner.Scan("Commande 20241007123456 du 07/10/2025 à 10:00:00, ref 12 34 56 78 90, version 1.2.3.4 prix 1 234,56 €").Count == 0, "numéros de commande, dates, versions : pas de faux positif");
            Check(SensitiveScanner.Labels(5) == "IBAN, N° de sécurité sociale" && SensitiveScanner.Scan("").Count == 0 && SensitiveScanner.Scan(null).Count == 0, "libellés des types");
            Check(SensitiveScanner.Scan(string.Concat(Enumerable.Repeat("0123 4567 ", 30000))).Count >= 0, "texte volumineux sans blocage");

            // ---- threads
            long ts(int day) => new DateTimeOffset(2025, 10, day, 8, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
            var items = new List<ThreadItem>
            {
                new ThreadItem { Id = 10, MessageId = "<a@x>", Topic = "Projet", Date = ts(1) },
                new ThreadItem { Id = 11, MessageId = "<b@x>", InReplyTo = "<a@x>", Topic = "RE: Projet", Date = ts(2) },
                new ThreadItem { Id = 12, MessageId = "<c@x>", References = "<a@x> <b@x>", Topic = "RE: RE: Projet", Date = ts(3) },
                new ThreadItem { Id = 13, MessageId = "<a@x>", Topic = "Projet", Date = ts(1) },                       // copy in another mailbox
                new ThreadItem { Id = 20, MessageId = "<z@y>", Topic = "Autre", Date = ts(1) },
                new ThreadItem { Id = 30, ConversationKey = "0123456789", Topic = "Réunion", Date = ts(5) },
                new ThreadItem { Id = 31, ConversationKey = "0123456789", Topic = "Réunion", Date = ts(6) },            // Outlook conversation index
                new ThreadItem { Id = 40, Topic = "Facture d'octobre", Date = ts(2), Parties = new HashSet<string> { "a@x.fr", "b@y.fr" } },
                new ThreadItem { Id = 41, Topic = "TR: Facture d'octobre", Date = ts(4), Parties = new HashSet<string> { "b@y.fr", "c@z.fr" } },   // same subject, shared participant
                new ThreadItem { Id = 42, Topic = "Facture d'octobre", Date = ts(4), Parties = new HashSet<string> { "d@q.fr" } },                 // same subject, nobody in common
                new ThreadItem { Id = 50, Topic = "Facture d'octobre", Date = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), Parties = new HashSet<string> { "a@x.fr" } }, // too late
            };
            var th = ThreadBuilder.Build(items);
            Check(th[10].thread == 10 && th[11].thread == 10 && th[12].thread == 10 && th[13].thread == 10 && th[10].count == 4, "fil par Message-ID / In-Reply-To / References (copies de plusieurs boîtes réunies)");
            Check(th[20].thread == 20 && th[20].count == 1, "message isolé");
            Check(th[30].thread == 30 && th[31].thread == 30, "fil par index de conversation Outlook");
            Check(th[40].thread == 40 && th[41].thread == 40 && th[42].thread == 42 && th[50].thread == 50, "même objet : rapproché seulement avec un participant commun et dans les 120 jours");
            Check(ThreadBuilder.NormalizeSubject("RE: TR : Fwd: Réunion  budget") == "réunion budget", "objet normalisé (RE:, TR:, FW:)");
        }
    }
}
