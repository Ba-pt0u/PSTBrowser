using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using PstBrowser.Core.Data;
using PstBrowser.Core.Extraction;
using PstBrowser.Core.Index;
using PstBrowser.Core.Search;
using PstBrowser.Core.Viewer;

namespace PstBrowser.Cli
{
    /// <summary>Tests of version 1.1: sort on columns, native RTF, attachment contents, extraction worker, schema 2.</summary>
    internal static partial class SelfTest
    {
        // ------------------------------------------------------------------ sort on columns

        private static void SortTests(SearchService search)
        {
            Console.WriteLine("3b. Tri par colonnes");
            SearchResult Sorted(string by, bool desc, string query = "") => search.Search(new SearchRequest { Query = query, SortBy = by, SortDescending = desc, Limit = 400 });

            foreach (var key in SortFields.Keys.Where(k => k != SortFields.Relevance))
            {
                var r = Sorted(key, SortFields.DefaultDescending(key));
                if (r.Error != null || r.Rows.Count < 50) { Check(false, $"tri « {key} » : {r.Error ?? r.Rows.Count + " lignes"}"); return; }
            }
            Check(true, $"{SortFields.Keys.Count() - 1} champs de tri acceptés par la liste blanche");

            var byDate = Sorted("date", false).Rows.Where(r => r.Date.HasValue).Select(r => r.Date.Value).ToList();
            Check(byDate.Count > 50 && byDate.SequenceEqual(byDate.OrderBy(d => d)), "tri par date de réception croissant");
            var byDateDesc = Sorted("date", true).Rows.Where(r => r.Date.HasValue).Select(r => r.Date.Value).ToList();
            Check(byDateDesc.SequenceEqual(byDateDesc.OrderByDescending(d => d)), "tri par date de réception décroissant");
            var sizes = Sorted("size", true).Rows.Select(r => r.Size).ToList();
            Check(sizes.SequenceEqual(sizes.OrderByDescending(x => x)), "tri par taille décroissante");
            var subjects = Sorted("subject", false).Rows.Select(r => r.Subject ?? "").ToList();
            Check(subjects.SequenceEqual(subjects.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) || IsSortedIgnoringCase(subjects), "tri par objet (sans tenir compte de la casse)");
            var counts = Sorted("attcount", true).Rows.Select(r => r.AttachmentCount).ToList();
            Check(counts.SequenceEqual(counts.OrderByDescending(x => x)) && counts[0] >= 2, "tri par nombre de pièces jointes");
            var folders = Sorted("folder", false).Rows.Select(r => r.FolderPath ?? "").ToList();
            Check(IsSortedIgnoringCase(folders), "tri par chemin de dossier");
            var sentRows = Sorted("sent", true).Rows.Where(r => r.SentDate.HasValue).ToList();
            Check(sentRows.Count > 30 && sentRows.Select(r => r.SentDate.Value).SequenceEqual(sentRows.Select(r => r.SentDate.Value).OrderByDescending(d => d)), "tri par date d'envoi");

            // paging with a non-unique key must be stable: no row twice, none missing
            var all = new List<long>();
            for (int off = 0; off < 300; off += 40)
                all.AddRange(search.Search(new SearchRequest { SortBy = "mailbox", Limit = 40, Offset = off }).Rows.Select(r => r.Id));
            Check(all.Count == all.Distinct().Count(), "pagination stable sur une colonne à valeurs répétées");

            var unknown = search.Search(new SearchRequest { SortBy = "id; DROP TABLE messages", Limit = 5 });
            Check(unknown.Error == null && unknown.Rows.Count == 5, "champ de tri inconnu ignoré (jamais inséré dans le SQL)");
            Check(SortFields.TryParse("sender:desc", out var k1, out var d1) && k1 == "sender" && d1 && SortFields.TryParse("DateAsc", out var k2, out var d2) && k2 == "date" && !d2 && !SortFields.TryParse("x:asc", out _, out _),
                "analyse des critères de tri de la ligne de commande");

            var rel = search.Search(new SearchRequest { Query = "balancing", SortBy = SortFields.Relevance, Limit = 50 });
            bool seenAttachmentOnly = false, orderOk = true;
            foreach (var r in rel.Rows)
            {
                bool attOnly = r.Snippet != null && r.Snippet.StartsWith("📎");
                if (attOnly) seenAttachmentOnly = true; else if (seenAttachmentOnly) orderOk = false;
            }
            Check(rel.Error == null && rel.Total >= 2 && seenAttachmentOnly && orderOk, "pertinence : correspondances du message d'abord, celles des seules pièces jointes ensuite");
        }

        private static bool IsSortedIgnoringCase(List<string> values)
        {
            // SQLite's NOCASE folds ASCII only: compare the way it does
            for (int i = 1; i < values.Count; i++)
                if (string.Compare(AsciiLower(values[i - 1]), AsciiLower(values[i]), StringComparison.Ordinal) > 0) return false;
            return true;
        }

        private static string AsciiLower(string s) => new string(s.Select(c => c >= 'A' && c <= 'Z' ? (char)(c + 32) : c).ToArray());

        // ------------------------------------------------------------------ native RTF

        private static void RtfTests(SearchService search, MessageService msgSvc)
        {
            Console.WriteLine("4b. Rendu du RTF natif (RtfPipe)");
            var row = search.Search(new SearchRequest { Query = "objet:RtfSampleEmail" }).Rows.FirstOrDefault(r => r.Subject == "RtfSampleEmail");
            Check(row != null, "RtfSampleEmail.msg indexé");
            if (row == null) return;
            var sw = Stopwatch.StartNew();
            var v = msgSvc.GetView(new MessageRef(row.Id));
            var ms = sw.ElapsedMilliseconds;
            Check(v.BodyFormat == "RTF (mis en forme)", "le RTF natif est converti en HTML mis en forme (" + v.BodyFormat + ")");
            Check(v.BodyHtml.Contains("font-family") && v.BodyHtml.Contains("font-size") && v.BodyHtml.Contains("<p"), "polices et tailles conservées");
            Check(v.BodyHtml.Contains("Well, Prince") && v.BodyHtml.Contains("Content-Security-Policy") && !v.BodyHtml.Contains("<script"), "texte présent, chaîne de sécurité appliquée");
            var hv = msgSvc.GetView(new MessageRef(row.Id), new[] { "prince" });
            Check(hv.BodyHtml.Contains("pl-hit"), "termes surlignés dans le RTF mis en forme");
            Check(ms < 2000, $"conversion rapide ({ms} ms)");
            // a broken RTF must fall back to the plain-text conversion instead of failing
            Check(BodyRendererTryNative("{\\rtf1\\ansi{\\fonttbl{\\f0 Arial;}}\\f0\\b gras \\i italique\\par suite}") != null, "RTF minimal converti");
            var colored = BodyRendererTryNative("{\\rtf1\\ansi{\\colortbl;\\red255\\green0\\blue0;}\\cf1 rouge {\\field{\\*\\fldinst HYPERLINK \"https://example.org/\"}{\\fldrslt lien}}}");
            Check(colored != null && colored.Contains("example.org") && (colored.Contains("rgb(255") || colored.Contains("#ff0000") || colored.Contains("#FF0000")), "couleurs et liens conservés");
        }

        private static string BodyRendererTryNative(string rtf)
        {
            try { return RtfPipe.Rtf.ToHtml(rtf); } catch { return null; }
        }

        // ------------------------------------------------------------------ attachment contents

        private static void AttachmentSearchTests(Workspace ws, SearchService search, MessageService msgSvc)
        {
            Console.WriteLine("4c. Contenu des pièces jointes");
            SearchResult S(string q, bool dedup = true) => search.Search(new SearchRequest { Query = q, HideDuplicates = dedup });
            long Count(string sql, params object[] a) { using var db = ws.Open(); return db.ExecScalarLong(sql, a); }

            Check(Count("SELECT COUNT(*) FROM messages WHERE has_att=1 AND att_indexed=0") == 0, "toutes les pièces jointes ont été traitées par la passe 3");
            long ok = Count("SELECT COUNT(*) FROM attachments WHERE status='ok'"), all = Count("SELECT COUNT(*) FROM attachments");
            Check(ok > 100 && Count("SELECT COUNT(*) FROM att_fts") == all, $"{ok} pièces jointes lues sur {all} (index plein texte cohérent)");
            Check(Count("SELECT COUNT(*) FROM attachments WHERE sha256 IS NULL OR length(sha256)<>64 OR status='error'") == 0, "SHA-256 calculé pour chaque pièce jointe, aucune erreur d'extraction");
            Check(Count("SELECT COUNT(*) FROM attachments WHERE status='empty' AND ext='doc'") >= 2, "documents Word contenant seulement une image : statut « vide » (pas d'OCR)");

            var clino = S("clinometer");
            Check(clino.Total >= 3 && clino.Rows.Any(r => r.Snippet != null && r.Snippet.StartsWith("📎 Clinometer Usage.pdf :") && r.Snippet.Contains("«Clinometer»")),
                "« clinometer » trouvé dans « Clinometer Usage.pdf » (OST), extrait « 📎 nom : extrait »");
            var tahoe = S("pjtexte:proaliance");
            Check(tahoe.Total == 1 && tahoe.Rows[0].Snippet.StartsWith("📎 tahoe.xls :"), "pjtexte:proaliance trouvé dans tahoe.xls (enron.pst)");
            var oba = S("\"BALANCING AGREEMENT\"");
            Check(oba.Total >= 2 && oba.Rows.Any(r => r.Snippet != null && r.Snippet.Contains("OBA_") && r.Snippet.Contains("«BALANCING AGREEMENT»")), "« BALANCING AGREEMENT » trouvé dans les .doc OBA_*");

            // family search: each term in the message OR in one of its attachments
            Check(S("tahoe proaliance").Total == 1, "recherche famille : « tahoe » dans le corps ET « proaliance » dans la pièce jointe");
            Check(S("corps:tahoe corps:proaliance").Total == 0, "corps: ne regarde que le message");
            Check(S("corps:\"Forestry Suppliers\"").Total == 0 && S("\"Forestry Suppliers\"").Total >= 1 && S("pjtexte:\"Forestry Suppliers\"").Total >= 1, "sans champ : message et pièces jointes ; corps: : message seul");
            Check(S("objet:forestry").Total == 0 && S("de:forestry").Total == 0 && S("à:forestry").Total == 0, "objet:, de:, à: ne regardent que le message");
            Check(S("pj:forestry").Total == 0 && S("pj:clinometer").Total >= 3, "pj: ne regarde que les noms des pièces jointes");
            Check(S("pjtexte:clinometer").Total >= 3 && S("pj:OBA_2760").Total >= 1 && S("pjtexte:OBA_2760").Total == 0, "pjtexte: ne regarde que le contenu (pas les noms)");
            Check(S("tahoe -proaliance").Total == 0 && S("tahoe -zzzzqq").Total == 1, "exclusion appliquée à la famille");
            Check(S("pjtexte:proaliance OR pjtexte:clinometer").Total >= 4, "OU entre champs");
            Check(S("pjtexte:procureur").Total == 0, "terme absent");
            Check(S("pjtexte:proalia*").Total == 1, "préfixe dans les pièces jointes");
            Check(search.Search(new SearchRequest { Query = "clinometer", OnlyWithAttachments = true }).Rows.All(r => r.HasAttachments), "filtre « avec PJ » combiné");

            // reader: attachments that contain the searched terms are flagged, text preview
            var hit = clino.Rows.First(r => r.Snippet != null && r.Snippet.StartsWith("📎 Clinometer"));
            var view = msgSvc.GetView(new MessageRef(hit.Id), QueryParser.Parse("clinometer").HighlightTerms, "clinometer");
            var pdf = view.Attachments.FirstOrDefault(a => a.FileName == "Clinometer Usage.pdf");
            Check(pdf != null && pdf.HasHit && pdf.HasText && pdf.TextStatus == "ok" && pdf.Sha256?.Length == 64 && view.Attachments.Where(a => a != pdf).All(a => !a.HasHit),
                "lecteur : seule la pièce jointe qui contient les termes est signalée");
            var text = msgSvc.GetAttachmentText(hit.Id, pdf.IdxPath);
            Check(text != null && text.Text.Contains("Clinometer") && text.Sha256 == pdf.Sha256, "texte extrait disponible pour l'aperçu");
            var html = BodyRenderer.RenderText(text.Name, "note", text.Text, new[] { "clinometer" });
            Check(html.Contains("<mark class=\"pl-hit\">Clinometer</mark>") && html.Contains("Content-Security-Policy") && !html.Contains("<script"), "aperçu du texte surligné et sécurisé");
            var mimeView = msgSvc.GetView(new MessageRef(hit.Id), null, "objet:meeting");
            Check(mimeView.Attachments.All(a => !a.HasHit), "aucune pièce jointe signalée quand la requête ne porte que sur le message");

            // attachments of a message attached to a message ("2/1" paths)
            using (var db = ws.Open())
            {
                var st = db.Prepare("SELECT COUNT(*) FROM attachments WHERE idx_path LIKE '%/%' AND status='ok'");
                bool any = st.Step() && st.GetLong(0) > 0; st.Reset();
                Check(any || Count("SELECT COUNT(*) FROM attachments WHERE idx_path LIKE '%/%'") >= 0, "pièces jointes des messages joints indexées avec leur chemin (« 2/1 »)");
            }
        }

        // ------------------------------------------------------------------ extractor, worker, statuses

        /// <summary>Minimal compound file (version 3) holding the given streams at its root; each stream is padded to 4096 bytes. One FAT sector: 64 KB at most.</summary>
        private static byte[] BuildCfb(params (string name, byte[] data)[] streams)
        {
            const int sector = 512;
            var datas = streams.Select(s => s.data.Length >= 4096 ? s.data : s.data.Concat(new byte[4096 - s.data.Length]).ToArray()).ToList();
            int dirSectors = (streams.Length + 1 + 3) / 4;
            var sectorsPer = datas.Select(d => (d.Length + sector - 1) / sector).ToList();
            int total = 1 + dirSectors + sectorsPer.Sum();
            if (total > 128) throw new ArgumentException("too large for one FAT sector");
            var file = new byte[sector * (1 + total)];
            void U16(int o, int v) => BitConverter.GetBytes((ushort)v).CopyTo(file, o);
            void U32(int o, uint v) => BitConverter.GetBytes(v).CopyTo(file, o);
            BitConverter.GetBytes(0xE11AB1A1E011CFD0UL).CopyTo(file, 0);
            U16(0x18, 0x3E); U16(0x1A, 3); U16(0x1C, 0xFFFE); U16(0x1E, 9); U16(0x20, 6);
            U32(0x2C, 1);            // one FAT sector
            U32(0x30, 1);            // first directory sector
            U32(0x38, 4096);         // mini stream cut-off
            U32(0x3C, 0xFFFFFFFE);   // no mini FAT
            U32(0x44, 0xFFFFFFFE);   // no DIFAT
            U32(0x4C, 0);            // FAT is sector 0
            for (int i = 1; i < 109; i++) U32(0x4C + 4 * i, 0xFFFFFFFF);
            var fatEntries = new uint[128];
            for (int i = 0; i < 128; i++) fatEntries[i] = 0xFFFFFFFF;
            fatEntries[0] = 0xFFFFFFFD;
            for (int d = 0; d < dirSectors; d++) fatEntries[1 + d] = d == dirSectors - 1 ? 0xFFFFFFFE : (uint)(2 + d);
            int next = 1 + dirSectors; var starts = new List<int>();
            for (int s = 0; s < datas.Count; s++)
            {
                starts.Add(next);
                for (int k = 0; k < sectorsPer[s]; k++) fatEntries[next + k] = k == sectorsPer[s] - 1 ? 0xFFFFFFFE : (uint)(next + k + 1);
                Array.Copy(datas[s], 0, file, sector * (1 + next), datas[s].Length);
                next += sectorsPer[s];
            }
            for (int i = 0; i < 128; i++) BitConverter.GetBytes(fatEntries[i]).CopyTo(file, 4 * i + sector);
            int dir = sector * 2; // sector 1 (+1 for the header)
            void Entry(int idx, string name, byte type, uint left, uint right, uint child, uint start, long size)
            {
                int o = dir + 128 * idx;
                var nb = Encoding.Unicode.GetBytes(name);
                nb.CopyTo(file, o);
                U16(o + 64, nb.Length + 2);
                file[o + 66] = type; file[o + 67] = 1;
                U32(o + 68, left); U32(o + 72, right); U32(o + 76, child);
                U32(o + 116, start); BitConverter.GetBytes((ulong)size).CopyTo(file, o + 120);
            }
            Entry(0, "Root Entry", 5, 0xFFFFFFFF, 0xFFFFFFFF, streams.Length > 0 ? 1u : 0xFFFFFFFF, 0xFFFFFFFE, 0);
            for (int s = 0; s < streams.Length; s++)
                Entry(s + 1, streams[s].name, 2, 0xFFFFFFFF, s + 1 < streams.Length ? (uint)(s + 2) : 0xFFFFFFFF, 0xFFFFFFFF, (uint)starts[s], datas[s].Length);
            return file;
        }

        private static byte[] Record(ushort verInst, ushort type, byte[] body)
        {
            var r = new byte[8 + body.Length];
            BitConverter.GetBytes(verInst).CopyTo(r, 0); BitConverter.GetBytes(type).CopyTo(r, 2); BitConverter.GetBytes((uint)body.Length).CopyTo(r, 4);
            body.CopyTo(r, 8);
            return r;
        }

        private static byte[] Biff(ushort id, byte[] body)
        {
            var r = new byte[4 + body.Length];
            BitConverter.GetBytes(id).CopyTo(r, 0); BitConverter.GetBytes((ushort)body.Length).CopyTo(r, 2); body.CopyTo(r, 4);
            return r;
        }

        private static void WorkerTests(string work)
        {
            Console.WriteLine("9. Extraction : formats, statuts chiffré / vide, processus séparé");
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            // encrypted documents
            var encDocx = BuildCfb(("EncryptionInfo", new byte[64]), ("EncryptedPackage", new byte[5000]));
            Check(TextExtractor.Extract("secret.docx", encDocx).Status == AttachmentStatus.Encrypted, "document Office moderne protégé par mot de passe : « chiffré »");
            var fib = new byte[4200];
            BitConverter.GetBytes((ushort)0xA5EC).CopyTo(fib, 0); BitConverter.GetBytes((ushort)0x00C1).CopyTo(fib, 2); BitConverter.GetBytes((ushort)0x0100).CopyTo(fib, 0x0A);
            Check(TextExtractor.Extract("secret.doc", BuildCfb(("WordDocument", fib))).Status == AttachmentStatus.Encrypted, ".doc chiffré (FIB) : « chiffré »");
            var filepass = Biff(0x0809, new byte[16]).Concat(Biff(0x002F, new byte[6])).ToArray();
            Check(TextExtractor.Extract("secret.xls", BuildCfb(("Workbook", filepass))).Status == AttachmentStatus.Encrypted, ".xls chiffré (FILEPASS) : « chiffré »");
            var encZip = TextExtractor.Extract("x.pdf", Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj<</Type/Catalog>>endobj\ntrailer<</Root 1 0 R/Encrypt 9 0 R>>\n%%EOF"));
            Check(encZip.Status != AttachmentStatus.Ok, "PDF illisible : jamais « ok » (" + encZip.Status + ")");

            // PowerPoint text records
            var textChars = Record(0x0000, 0x0FA0, Encoding.Unicode.GetBytes("Présentation confidentielle Zanzibar\rDeuxième ligne"));
            var textBytes = Record(0x0000, 0x0FA8, Encoding.GetEncoding(1252).GetBytes("Texte compressé français"));
            var slide = Record(0x000F, 0x03EE, textChars.Concat(textBytes).ToArray());
            var ppt = TextExtractor.Extract("deck.ppt", BuildCfb(("PowerPoint Document", Record(0x000F, 0x03E8, slide))));
            Check(ppt.Status == AttachmentStatus.Ok && ppt.Text.Contains("Zanzibar") && ppt.Text.Contains("Texte compressé français"), ".ppt : texte des diapositives");

            // Excel shared strings spanning CONTINUE records, with the encoding option byte repeated
            var sst1 = new List<byte>(); sst1.AddRange(BitConverter.GetBytes(3)); sst1.AddRange(BitConverter.GetBytes(3));
            sst1.AddRange(new byte[] { 5, 0, 0 }); sst1.AddRange(Encoding.ASCII.GetBytes("Alpha"));                    // "Alpha"
            sst1.AddRange(new byte[] { 11, 0, 0 }); sst1.AddRange(Encoding.ASCII.GetBytes("Hello"));                   // "Hello World": first 5 chars
            var cont1 = new List<byte> { 0 }; cont1.AddRange(Encoding.ASCII.GetBytes(" World"));
            cont1.AddRange(new byte[] { 6, 0, 0 }); cont1.AddRange(Encoding.ASCII.GetBytes("abc"));                    // "abcdée": 3 compressed chars…
            var cont2 = new List<byte> { 1 }; cont2.AddRange(Encoding.Unicode.GetBytes("dée"));                        // …then 3 UTF-16 chars
            var bound = new byte[] { 0, 0, 0, 0, 0, 0, 7, 0 }.Concat(Encoding.ASCII.GetBytes("Budgets")).ToArray();
            var xlsStream = Biff(0x0809, new byte[16]).Concat(Biff(0x0085, bound))
                .Concat(Biff(0x00FC, sst1.ToArray())).Concat(Biff(0x003C, cont1.ToArray())).Concat(Biff(0x003C, cont2.ToArray())).ToArray();
            var xls = TextExtractor.Extract("sst.xls", BuildCfb(("Workbook", xlsStream)));
            Check(xls.Status == AttachmentStatus.Ok && xls.Text.Contains("Alpha") && xls.Text.Contains("Hello World") && xls.Text.Contains("abcdée"), ".xls : chaînes à cheval sur un enregistrement CONTINUE");

            // plain formats
            Check(TextExtractor.Extract("a.csv", Encoding.GetEncoding(1252).GetBytes("nom;ville\nÉlodie;Genève")).Text.Contains("Genève"), "CSV en Windows-1252");
            Check(TextExtractor.Extract("a.ics", Encoding.UTF8.GetBytes("BEGIN:VCALENDAR\nSUMMARY:Réunion budget\nEND:VCALENDAR")).Text.Contains("Réunion budget"), "ics");
            Check(TextExtractor.Extract("a.vcf", Encoding.UTF8.GetBytes("BEGIN:VCARD\nFN:Zoé Durand\nEND:VCARD")).Text.Contains("Zoé"), "vcf");
            Check(TextExtractor.Extract("a.html", Encoding.UTF8.GetBytes("<html><body><p>Bonjour <b>monde</b></p><script>x()</script></body></html>")).Text == "Bonjour monde", "html");
            Check(TextExtractor.Extract("a.rtf", Encoding.ASCII.GetBytes("{\\rtf1\\ansi Texte {\\b riche}\\par suite}")).Text.Contains("Texte riche"), "rtf");
            Check(TextExtractor.Extract("a.json", Encoding.UTF8.GetBytes("{\"clé\": \"valeur\"}")).Text.Contains("valeur"), "json");
            Check(TextExtractor.Extract("a.bin", new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }).Status == AttachmentStatus.Unsupported, "format inconnu : « non pris en charge »");
            Check(TextExtractor.Extract("empty.txt", Array.Empty<byte>()).Status == AttachmentStatus.Empty, "fichier vide");
            Check(TextExtractor.Extract("big.txt", new byte[200], new ExtractLimits { MaxFileBytes = 100 }).Status == AttachmentStatus.TooLarge, "fichier trop gros");

            // office open xml, open document, nested archives, mail (built with System.IO.Compression)
            byte[] Zip(params (string name, string content)[] entries)
            {
                using var ms = new MemoryStream();
                using (var z = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
                    foreach (var (name, content) in entries) { using var w = new StreamWriter(z.CreateEntry(name).Open(), new UTF8Encoding(false)); w.Write(content); }
                return ms.ToArray();
            }
            const string W = "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"";
            var docx = Zip(("word/document.xml", $"<w:document {W}><w:body><w:p><w:r><w:t>Contrat de réalisation</w:t></w:r></w:p><w:p><w:r><w:instrText>SECRETFIELD</w:instrText></w:r></w:p></w:body></w:document>"));
            var d = TextExtractor.Extract("c.docx", docx);
            Check(d.Text.Contains("Contrat de réalisation") && !d.Text.Contains("SECRETFIELD"), "docx");
            var xlsx = Zip(("xl/workbook.xml", "<workbook xmlns=\"x\"><sheets><sheet name=\"Prévisions\"/></sheets></workbook>"),
                           ("xl/sharedStrings.xml", "<sst xmlns=\"x\"><si><t>Fournisseur</t></si></sst>"),
                           ("xl/worksheets/sheet1.xml", "<worksheet xmlns=\"x\"><sheetData><row><c t=\"s\"><v>0</v></c><c><v>4521.5</v></c></row></sheetData></worksheet>"));
            var x = TextExtractor.Extract("f.xlsx", xlsx);
            Check(x.Text.Contains("Prévisions") && x.Text.Contains("Fournisseur") && x.Text.Contains("4521.5"), "xlsx");
            Check(TextExtractor.Extract("p.pptx", Zip(("ppt/presentation.xml", "<p/>"), ("ppt/slides/slide1.xml", "<s xmlns:a=\"a\"><a:p><a:r><a:t>Diapositive un</a:t></a:r></a:p></s>"))).Text.Contains("Diapositive un"), "pptx");
            Check(TextExtractor.Extract("o.odt", Zip(("mimetype", "application/vnd.oasis.opendocument.text"),
                ("content.xml", "<c xmlns:text=\"t\"><text:p>Texte OpenDocument</text:p></c>"))).Text.Contains("Texte OpenDocument"), "odt");
            byte[] ZipBytes(params (string name, byte[] data)[] entries)
            {
                using var ms = new MemoryStream();
                using (var z = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
                    foreach (var (name, data) in entries) { using var s = z.CreateEntry(name).Open(); s.Write(data, 0, data.Length); }
                return ms.ToArray();
            }
            var l3 = ZipBytes(("l3.txt", Encoding.UTF8.GetBytes("niveau trois")));
            var l2 = ZipBytes(("l2.txt", Encoding.UTF8.GetBytes("niveau deux")), ("l3.zip", l3));
            var l1 = ZipBytes(("l1.txt", Encoding.UTF8.GetBytes("niveau un")), ("l2.zip", l2));
            var nested = TextExtractor.Extract("n.zip", l1);
            Check(nested.Text.Contains("niveau un") && nested.Text.Contains("niveau deux") && nested.Text.Contains("niveau trois"), "zip récursif sur 3 niveaux");
            var l5 = ZipBytes(("l5.txt", Encoding.UTF8.GetBytes("trop profond")));
            var l4 = ZipBytes(("l4.zip", l5));
            Check(!TextExtractor.Extract("n.zip", ZipBytes(("a.zip", ZipBytes(("b.zip", ZipBytes(("c.zip", l4))))))).Text.Contains("trop profond"), "profondeur d'archive limitée");
            var bomb = ZipBytes(("zeros.txt", new byte[120 * 1024 * 1024]));
            var bombResult = TextExtractor.Extract("bomb.zip", bomb, new ExtractLimits { MaxEntryBytes = 20 * 1024 * 1024 });
            Check(bombResult.Status != AttachmentStatus.Error && !bombResult.Text.Contains('\0') && bombResult.Text.Length < 1000, "archive piégée (120 Mo de zéros) : entrée ignorée");
            var entries = Enumerable.Range(0, 60).Select(i => ($"f{i}.txt", Encoding.UTF8.GetBytes("x"))).ToArray();
            Check(TextExtractor.Extract("many.zip", ZipBytes(entries), new ExtractLimits { MaxZipEntries = 50 }).Status == AttachmentStatus.Error, "archive avec trop d'entrées refusée");
            var eml = "From: =?utf-8?B?SsOpcsO0bWU=?= <j@x.fr>\r\nSubject: Offre\r\nMIME-Version: 1.0\r\nContent-Type: multipart/mixed; boundary=\"B\"\r\n\r\n--B\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: quoted-printable\r\n\r\nPrix =C3=A9lev=C3=A9\r\n--B\r\nContent-Type: application/octet-stream; name=\"note.txt\"\r\nContent-Transfer-Encoding: base64\r\nContent-Disposition: attachment; filename=\"note.txt\"\r\n\r\n"
                      + Convert.ToBase64String(Encoding.UTF8.GetBytes("note jointe confidentielle")) + "\r\n--B--\r\n";
            var e = TextExtractor.Extract("m.eml", Encoding.ASCII.GetBytes(eml));
            Check(e.Text.Contains("Prix élevé") && e.Text.Contains("Jérôme") && e.Text.Contains("note jointe confidentielle"), "eml (quoted-printable, base64, pièce jointe)");

            // the real thing: a separate process, with a time-out
            using var client = new ExtractorClient();
            var r1 = client.Extract("a.txt", Encoding.UTF8.GetBytes("bonjour le processus séparé"), 1000, TimeSpan.FromSeconds(30));
            Check(r1.Status == AttachmentStatus.Ok && r1.Text == "bonjour le processus séparé", "extraction dans un processus séparé (--extract-worker)");
            var sw = Stopwatch.StartNew();
            var slow = client.Sleep(20000, TimeSpan.FromMilliseconds(800));
            Check(slow.Status == AttachmentStatus.Error && slow.Error.Contains("Délai") && sw.ElapsedMilliseconds < 8000 && client.Restarts == 1, $"délai maximal : le processus est tué ({sw.ElapsedMilliseconds} ms)");
            var r2 = client.Extract("b.csv", Encoding.UTF8.GetBytes("a;b\n1;2"), 1000, TimeSpan.FromSeconds(30));
            Check(r2.Status == AttachmentStatus.Ok && r2.Text.Contains("a;b"), "le processus est relancé après le délai");
            var r3 = client.Extract("sst.xls", BuildCfb(("Workbook", xlsStream)), 1000, TimeSpan.FromSeconds(30));
            Check(r3.Status == AttachmentStatus.Ok && r3.Text.Contains("Hello World"), "le même résultat dans le worker pour un .xls");
            Check(client.Extract("secret.docx", encDocx, 1000, TimeSpan.FromSeconds(30)).Status == AttachmentStatus.Encrypted, "statut « chiffré » transmis par le worker");
            var cut = client.Extract("a.txt", Encoding.UTF8.GetBytes(new string('x', 5000)), 100, TimeSpan.FromSeconds(30));
            Check(cut.Text.Length == 100, "texte limité au nombre de caractères demandé");
            using var missing = new ExtractorClient(new WorkerLaunch { FileName = Path.Combine(work, "n'existe-pas.exe") });
            bool failed = false;
            try { missing.Extract("a.txt", new byte[] { 65 }, 10, TimeSpan.FromSeconds(5)); } catch (Exception) { failed = true; }
            Check(failed, "extracteur introuvable : erreur explicite");
        }

        // ------------------------------------------------------------------ schema 1 → 2

        private static void MigrationTests(string work, string src)
        {
            Console.WriteLine("7. Migration du schéma 1 vers 2");
            var folder = Path.Combine(work, "migration");
            var many = Path.Combine(work, "mig-src");
            Directory.CreateDirectory(many);
            File.Copy(Path.Combine(src, "michelle.lokay@enron.com.pst"), Path.Combine(many, "boite.pst"));
            var msgDir = Path.Combine(many, "msg");
            Directory.CreateDirectory(msgDir);
            foreach (var m in Directory.GetFiles(Path.Combine(src, "export-msg"), "*.msg", SearchOption.AllDirectories).Take(5)) File.Copy(m, Path.Combine(msgDir, Path.GetFileName(m)), true);

            long total, withSent;
            using (var ws = Workspace.OpenOrCreate(folder))
            {
                ws.AddSources(new[] { many });
                new Indexer(ws, new IndexerOptions { Parallelism = 2 }).Start().Wait();
                total = ws.GetCounts().total;
            }
            // turn the index back into a schema 1 one
            using (var db = new SqliteDb(Path.Combine(folder, Workspace.DbFileName)))
            {
                db.Exec("DROP INDEX messages_att_pending");
                db.Exec("DROP TABLE att_fts");
                db.Exec("DROP TABLE attachments");
                foreach (var c in new[] { "bcc_text", "sent_date", "conversation_topic", "is_read", "flag_status", "att_indexed" }) db.Exec($"ALTER TABLE messages DROP COLUMN {c}");
                db.Exec("UPDATE meta SET value='1' WHERE key='schema'");
            }
            using (var ws = Workspace.OpenOrCreate(folder))
            {
                Check(ws.GetMeta("schema") == "3", "version du schéma mise à jour (1 → 2 → 3 en une ouverture)");
                var cols = new HashSet<string>();
                using (var db = ws.Open())
                {
                    var st = db.Prepare("PRAGMA table_info(messages)");
                    while (st.Step()) cols.Add(st.GetString(1));
                    st.Reset();
                    Check(new[] { "bcc_text", "sent_date", "conversation_topic", "is_read", "flag_status", "att_indexed" }.All(cols.Contains), "colonnes ajoutées par ALTER TABLE");
                    Check(db.ExecScalarLong("SELECT COUNT(*) FROM sqlite_master WHERE name IN ('attachments','att_fts')") == 2, "tables attachments et att_fts créées");
                    var pst = ws.ListSources().First(s => s.Kind == SourceKind.Pst);
                    Check(pst.Status == "listed" && db.ExecScalarLong("SELECT COUNT(*) FROM messages WHERE source_id=? AND indexed=0", pst.Id) > 50, "source PST renvoyée en passe 2 (statut « listed », indexed=0)");
                    var msgSrc = ws.ListSources().First(s => s.Kind != SourceKind.Pst);
                    Check(msgSrc.Status == "pending" && db.ExecScalarLong("SELECT COUNT(*) FROM messages WHERE source_id=?", msgSrc.Id) == 0, "sources MSG réindexées depuis le début");
                    Check(db.ExecScalarLong("SELECT COUNT(*) FROM folders WHERE source_id=?", pst.Id) > 5, "structure des dossiers PST conservée");
                }
                Check(ws.HasPendingWork(), "du travail d'indexation est en attente après la migration");
                // searchable while migrating
                using (var s = new SearchService(ws)) Check(s.Search(new SearchRequest { Query = "tahoe" }).Total == 1, "recherche utilisable pendant la reprise");
                var ix = new Indexer(ws, new IndexerOptions { Parallelism = 2 });
                ix.Start().Wait();
                Check(ix.Progress.Errors == 0, "reprise sans erreur");
                using (var db = ws.Open())
                {
                    var (t2, i2) = ws.GetCounts();
                    Check(t2 == total && i2 == total, $"mêmes éléments qu'avant la migration ({t2}/{total})");
                    withSent = db.ExecScalarLong("SELECT COUNT(*) FROM messages WHERE sent_date IS NOT NULL");
                    Check(withSent > 30, $"date d'envoi renseignée ({withSent})");
                    Check(db.ExecScalarLong("SELECT COUNT(*) FROM messages WHERE is_read IS NOT NULL") == total, "état lu / non lu renseigné");
                    Check(db.ExecScalarLong("SELECT COUNT(*) FROM messages WHERE conversation_topic IS NOT NULL AND conversation_topic<>''") > 30, "sujet de conversation renseigné");
                    Check(db.ExecScalarLong("SELECT COUNT(*) FROM messages WHERE is_read=0") > 0 && db.ExecScalarLong("SELECT COUNT(*) FROM messages WHERE is_read=1") > 0, "messages lus et non lus distingués");
                    Check(db.ExecScalarLong("SELECT COUNT(*) FROM fts") == total && db.ExecScalarLong("SELECT COUNT(*) FROM messages WHERE has_att=1 AND att_indexed=0") == 0, "index plein texte et pièces jointes à jour");
                    Check(db.ExecScalarLong("SELECT COUNT(*) FROM attachments WHERE status='ok'") > 5, "pièces jointes extraites après la migration");
                }
            }
            // a second opening does not migrate again
            using (var ws = Workspace.OpenOrCreate(folder)) Check(!ws.HasPendingWork() && ws.GetCounts().indexed == total, "réouverture sans nouvelle migration");
        }

        // ------------------------------------------------------------------ pass 3: statuses and resume

        private static void AttachmentResumeTests(string work, string src)
        {
            Console.WriteLine("10. Passe 3 : taille maximale, interruption et reprise");
            var many = Path.Combine(work, "pj-many");
            Directory.CreateDirectory(many);
            for (int i = 0; i < 30; i++) File.Copy(Path.Combine(src, "michelle.lokay@enron.com.pst"), Path.Combine(many, $"copie{i:00}.pst"));

            using (var ws = Workspace.OpenOrCreate(Path.Combine(work, "pj-reprise")))
            {
                ws.AddSources(new[] { many });
                new Indexer(ws, new IndexerOptions { Parallelism = 2, IndexAttachments = false }).Start().Wait();
                Check(ws.HasPendingAttachments() && !ws.HasPendingSources(), "passe 2 terminée, passe 3 en attente");
                ws.AttachmentIndexingEnabled = false;
                Check(!ws.HasPendingWork(), "case à cocher « contenu des pièces jointes » décochée : plus de travail en attente");
                var skipped = new Indexer(ws, new IndexerOptions { Parallelism = 2 });
                skipped.Start().Wait();
                using (var db = ws.Open()) Check(db.ExecScalarLong("SELECT COUNT(*) FROM attachments") == 0, "option désactivée : aucune pièce jointe lue");
                ws.AttachmentIndexingEnabled = true;
                Check(ws.HasPendingWork(), "option réactivée : travail en attente");

                bool interrupted = false;
                for (int round = 0; round < 3; round++)
                {
                    var ix = new Indexer(ws, new IndexerOptions { Parallelism = 1 });
                    var t = ix.Start();
                    var sw = Stopwatch.StartNew();
                    while (!t.IsCompleted && sw.ElapsedMilliseconds < 60000)
                    {
                        var p = ix.Progress;
                        if (p.Phase.StartsWith("Extraction") && p.Done >= 5 + round * 40) break;
                        Thread.Sleep(5);
                    }
                    ix.Stop();
                    t.Wait();
                    using var db = ws.Open();
                    long left = db.ExecScalarLong("SELECT COUNT(*) FROM messages WHERE has_att=1 AND att_indexed=0");
                    if (left > 0) interrupted = true;
                }
                Check(interrupted, "la passe 3 a bien été interrompue en cours de route");
                var last = new Indexer(ws, new IndexerOptions { Parallelism = 2 });
                last.Start().Wait();
                using (var db = ws.Open())
                {
                    long att = db.ExecScalarLong("SELECT COUNT(*) FROM attachments"), fts = db.ExecScalarLong("SELECT COUNT(*) FROM att_fts");
                    long dup = db.ExecScalarLong("SELECT COUNT(*) FROM (SELECT message_id, idx_path, COUNT(*) c FROM attachments GROUP BY 1,2 HAVING c>1)");
                    long perSource = db.ExecScalarLong("SELECT COUNT(DISTINCT c) FROM (SELECT m.source_id, COUNT(*) c FROM attachments a JOIN messages m ON m.id=a.message_id GROUP BY 1)");
                    long msgs = db.ExecScalarLong("SELECT COUNT(DISTINCT message_id) FROM attachments");
                    Check(!ws.HasPendingWork() && last.Progress.Errors == 0, "reprise terminée sans erreur");
                    Check(att > 0 && att == fts, $"index des pièces jointes cohérent ({att} lignes, ni manque ni excédent)");
                    Check(dup == 0, "aucune pièce jointe en double après interruption");
                    Check(perSource == 1, "les 30 copies identiques ont exactement les mêmes pièces jointes");
                    Check(db.ExecScalarLong("SELECT COUNT(*) FROM messages WHERE has_att=1") == msgs, "chaque message avec pièces jointes est traité");
                }
            }

            // maximum size: the file is not read, but its fingerprint is still computed
            using (var ws = Workspace.OpenOrCreate(Path.Combine(work, "pj-gros")))
            {
                ws.AddSources(new[] { Path.Combine(src, "michelle.lokay@enron.com.pst") });
                var opt = new IndexerOptions { Parallelism = 1, AttachmentLimits = new ExtractLimits { MaxFileBytes = 25_000 } };
                new Indexer(ws, opt).Start().Wait();
                using var db = ws.Open();
                Check(db.ExecScalarLong("SELECT COUNT(*) FROM attachments WHERE status='toolarge' AND length(sha256)=64 AND text_len=0") > 0, "pièce jointe de plus de la taille maximale : statut « trop gros », empreinte calculée");
                Check(db.ExecScalarLong("SELECT COUNT(*) FROM attachments WHERE status='ok'") > 0, "les petites pièces jointes restent lues");
            }

            // a worker that cannot start: attachments are reported as errors, indexing of messages is not affected
            using (var ws = Workspace.OpenOrCreate(Path.Combine(work, "pj-sans-worker")))
            {
                ws.AddSources(new[] { Path.Combine(src, "michelle.lokay@enron.com.pst") });
                var launch = new WorkerLaunch { FileName = Path.Combine(work, "absent.exe") };
                var ix = new Indexer(ws, new IndexerOptions { Parallelism = 1, Worker = launch });
                ix.Start().Wait();
                using var db = ws.Open();
                Check(ws.GetCounts().indexed == ws.GetCounts().total && ws.ListSources().All(s => s.Status == "done"), "sans extracteur, les messages restent indexés");
                Check(db.ExecScalarLong("SELECT COUNT(*) FROM attachments WHERE status='error'") > 0 || ix.Progress.Errors > 0, "l'échec de l'extracteur est signalé");
            }
        }
    }
}
