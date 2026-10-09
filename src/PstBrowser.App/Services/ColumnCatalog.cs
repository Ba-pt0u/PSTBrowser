using System;
using System.Collections.Generic;
using System.Linq;

namespace PstBrowser.App.Services
{
    /// <summary>One column the message list can show. The catalogue is defined here, not in XAML.</summary>
    public sealed class ColumnDef
    {
        public string Key { get; set; }
        /// <summary>Header shown in the list.</summary>
        public string Header { get; set; }
        /// <summary>Name in the column chooser and in tooltips.</summary>
        public string Title { get; set; }
        /// <summary>Property of <see cref="RowItem"/> displayed by the column.</summary>
        public string Binding { get; set; }
        /// <summary>Key of <c>SortFields</c>; null when the list cannot be sorted on this column.</summary>
        public string SortKey { get; set; }
        public double Width { get; set; } = 120;
        public double MinWidth { get; set; } = 40;
        /// <summary>Takes the remaining width (subject).</summary>
        public bool Stretch { get; set; }
        /// <summary>Subject with the search excerpt below it.</summary>
        public bool SubjectWithSnippet { get; set; }
    }

    /// <summary>Saved state of a column: position in the list is the display order.</summary>
    public sealed class ColumnState
    {
        public string Key { get; set; }
        public bool Visible { get; set; } = true;
        /// <summary>Width in pixels; null = default (or remaining width for the subject).</summary>
        public double? Width { get; set; }
    }

    public static class ColumnCatalog
    {
        public const string Standard = "Standard";
        public const string Investigation = "Investigation";

        public static readonly IReadOnlyList<ColumnDef> All = new List<ColumnDef>
        {
            new ColumnDef { Key = "att", Header = "📎", Title = "Pièce jointe (icône)", Binding = "AttachmentMark", SortKey = "hasatt", Width = 28, MinWidth = 28 },
            new ColumnDef { Key = "date", Header = "Reçu le", Title = "Date de réception", Binding = "DateText", SortKey = "date", Width = 100, MinWidth = 90 },
            new ColumnDef { Key = "sent", Header = "Envoyé le", Title = "Date d'envoi", Binding = "SentText", SortKey = "sent", Width = 100, MinWidth = 90 },
            new ColumnDef { Key = "from", Header = "De", Title = "Expéditeur (nom)", Binding = "From", SortKey = "sender", Width = 150, MinWidth = 60 },
            new ColumnDef { Key = "fromaddr", Header = "Adresse expéditeur", Title = "Adresse de l'expéditeur", Binding = "SenderEmail", SortKey = "senderemail", Width = 180 },
            new ColumnDef { Key = "to", Header = "À", Title = "Destinataires (À)", Binding = "To", SortKey = "to", Width = 170 },
            new ColumnDef { Key = "cc", Header = "Cc", Title = "Copie (Cc)", Binding = "Cc", SortKey = "cc", Width = 150 },
            new ColumnDef { Key = "bcc", Header = "Cci", Title = "Copie cachée (Cci)", Binding = "Bcc", SortKey = "bcc", Width = 150 },
            new ColumnDef { Key = "subject", Header = "Objet", Title = "Objet et extrait", Binding = "SubjectText", SortKey = "subject", Width = 300, MinWidth = 150, Stretch = true, SubjectWithSnippet = true },
            new ColumnDef { Key = "mailbox", Header = "Boîte", Title = "Boîte", Binding = "Mailbox", SortKey = "mailbox", Width = 120, MinWidth = 40 },
            new ColumnDef { Key = "folder", Header = "Dossier", Title = "Dossier", Binding = "FolderName", SortKey = "folder", Width = 100, MinWidth = 40 },
            new ColumnDef { Key = "folderpath", Header = "Chemin du dossier", Title = "Chemin complet du dossier", Binding = "FolderPathText", SortKey = "folder", Width = 240 },
            new ColumnDef { Key = "source", Header = "Fichier source", Title = "Fichier source (PST/OST/MSG)", Binding = "SourceName", SortKey = "source", Width = 170 },
            new ColumnDef { Key = "size", Header = "Taille", Title = "Taille", Binding = "SizeText", SortKey = "size", Width = 66, MinWidth = 50 },
            new ColumnDef { Key = "attcount", Header = "Nb PJ", Title = "Nombre de pièces jointes", Binding = "AttachmentCountText", SortKey = "attcount", Width = 52, MinWidth = 40 },
            new ColumnDef { Key = "attnames", Header = "Pièces jointes", Title = "Noms des pièces jointes", Binding = "AttachmentNames", SortKey = "attnames", Width = 240 },
            new ColumnDef { Key = "kind", Header = "Type", Title = "Type d'élément", Binding = "KindText", SortKey = "kind", Width = 80 },
            new ColumnDef { Key = "importance", Header = "Importance", Title = "Importance", Binding = "ImportanceText", SortKey = "importance", Width = 80 },
            new ColumnDef { Key = "dup", Header = "Doublon", Title = "Doublon", Binding = "DuplicateText", SortKey = "dup", Width = 62 },
            new ColumnDef { Key = "msgid", Header = "Message-ID", Title = "Message-ID Internet", Binding = "MessageId", SortKey = "msgid", Width = 240 },
            new ColumnDef { Key = "conversation", Header = "Conversation", Title = "Conversation (sujet de la conversation)", Binding = "ConversationTopic", SortKey = "conversation", Width = 200 },
            new ColumnDef { Key = "read", Header = "Lu", Title = "Lu / non lu", Binding = "ReadText", SortKey = "read", Width = 58, MinWidth = 40 },
            new ColumnDef { Key = "thread", Header = "Fil", Title = "Fil de conversation (nombre de messages)", Binding = "ThreadText", SortKey = "thread", Width = 44, MinWidth = 36 },
            new ColumnDef { Key = "auth", Header = "SPF / DKIM / DMARC", Title = "Authentification (SPF, DKIM, DMARC)", Binding = "AuthText", SortKey = "spf", Width = 170 },
            new ColumnDef { Key = "spoof", Header = "Usurpation ?", Title = "Indices d'usurpation", Binding = "SpoofText", SortKey = "spoof", Width = 200 },
            new ColumnDef { Key = "dateflags", Header = "Dates", Title = "Anomalies de dates", Binding = "DateFlagsText", SortKey = "dateflags", Width = 180 },
            new ColumnDef { Key = "sensitive", Header = "Sensible", Title = "Données sensibles détectées", Binding = "SensitiveText", SortKey = "sensitive", Width = 150 },
            new ColumnDef { Key = "flag", Header = "Suivi", Title = "Indicateur de suivi", Binding = "FlagText", SortKey = "flag", Width = 72 },
        };

        private static readonly Dictionary<string, ColumnDef> ByKey = All.ToDictionary(c => c.Key);

        public static ColumnDef Find(string key) => key != null && ByKey.TryGetValue(key, out var d) ? d : null;

        public static ColumnDef BySortKey(string sortKey) => All.FirstOrDefault(c => c.SortKey == sortKey);

        /// <summary>Everyday reading: what the list showed before columns became configurable.</summary>
        private static readonly string[] StandardKeys = { "att", "date", "from", "subject", "mailbox", "folder", "size" };

        /// <summary>Investigation: who, when, where from, and the identifiers needed to cite a message.</summary>
        private static readonly string[] InvestigationKeys =
        {
            "att", "date", "sent", "from", "fromaddr", "to", "cc", "bcc", "subject", "mailbox", "folderpath", "source",
            "attcount", "attnames", "dup", "msgid", "thread", "auth", "spoof", "dateflags", "sensitive", "read", "size",
        };

        public static IReadOnlyList<string> PresetNames { get; } = new[] { Standard, Investigation };

        /// <summary>Layout of a preset: its columns first, in order, then every other column hidden.</summary>
        public static List<ColumnState> Preset(string name)
        {
            var keys = string.Equals(name, Investigation, StringComparison.OrdinalIgnoreCase) ? InvestigationKeys : StandardKeys;
            var list = keys.Select(k => new ColumnState { Key = k, Visible = true }).ToList();
            list.AddRange(All.Where(c => !keys.Contains(c.Key)).Select(c => new ColumnState { Key = c.Key, Visible = false }));
            return list;
        }

        /// <summary>
        /// Makes a saved layout usable: unknown keys dropped, duplicates removed, columns added since missing (hidden),
        /// at least one visible column. An empty layout becomes the preset.
        /// </summary>
        public static List<ColumnState> Normalize(IEnumerable<ColumnState> saved, string presetName)
        {
            var result = new List<ColumnState>();
            var seen = new HashSet<string>();
            foreach (var s in saved ?? Enumerable.Empty<ColumnState>())
                if (s != null && Find(s.Key) != null && seen.Add(s.Key))
                    result.Add(new ColumnState { Key = s.Key, Visible = s.Visible, Width = s.Width is double w && w >= 20 && w < 3000 ? w : (double?)null });
            if (result.Count == 0) return Preset(presetName);
            foreach (var c in All.Where(c => !seen.Contains(c.Key))) result.Add(new ColumnState { Key = c.Key, Visible = false });
            if (!result.Any(r => r.Visible)) result.First(r => r.Key == "subject").Visible = true;
            return result;
        }

        /// <summary>Name of the preset a layout matches exactly (order and visibility), or null for a custom view.</summary>
        public static string MatchingPreset(IEnumerable<ColumnState> layout)
        {
            var visible = layout.Where(l => l.Visible).Select(l => l.Key).ToList();
            foreach (var name in PresetNames)
                if (visible.SequenceEqual(Preset(name).Where(p => p.Visible).Select(p => p.Key))) return name;
            return null;
        }
    }
}
