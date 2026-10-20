using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PstBrowser.Core.Index
{
    public enum RelocationOutcome { Relocated, NotFound, SizeMismatch, PathInUse, MsgFilesMissing }

    public sealed class RelocatedSource
    {
        public SourceInfo Source { get; set; }
        public string NewPath { get; set; }
        public RelocationOutcome Outcome { get; set; }

        public string OutcomeLabel => Outcome switch
        {
            RelocationOutcome.Relocated => "retrouvé",
            RelocationOutcome.NotFound => "absent du nouvel emplacement",
            RelocationOutcome.SizeMismatch => "taille différente : ce n'est pas le même fichier",
            RelocationOutcome.MsgFilesMissing => "les fichiers MSG indexés ne sont pas dans ce dossier",
            _ => "déjà présent dans le dossier d'affaire",
        };
    }

    public sealed class RelocationResult
    {
        public List<RelocatedSource> Items { get; } = new List<RelocatedSource>();
        public int Relocated => Items.Count(i => i.Outcome == RelocationOutcome.Relocated);
        public int Failed => Items.Count(i => i.Outcome != RelocationOutcome.Relocated);
    }

    /// <summary>
    /// Portable case folders: the index stores absolute paths of the sources (PST/OST files, MSG folders and files).
    /// When the sources or the case folder itself move (another drive letter, a copy to another computer, a Mac or a
    /// Linux workstation), the paths are rewritten here. A source is only moved to a file that exists and has the size
    /// recorded when it was added, so a wrong mapping can never attach another file to the index.
    /// </summary>
    public sealed partial class Workspace
    {
        private const string LastFolderKey = "last_folder";

        /// <summary>Sources found again automatically when the case folder was opened from a new location (null if none moved).</summary>
        public RelocationResult AutoRelocation { get; private set; }

        /// <summary>
        /// Rewrites the paths of the sources under <paramref name="oldPrefix"/> (a folder, or the full path of one source)
        /// to <paramref name="newPrefix"/>. Separators of both systems are accepted (a case built on Windows can be opened
        /// on a Mac) and compared without regard to case. Only sources found at the new location with the same size are moved.
        /// </summary>
        public RelocationResult RelocateSources(string oldPrefix, string newPrefix, bool onlyMissing = false, bool dryRun = false)
        {
            var result = new RelocationResult();
            string oldKey = Normalize(oldPrefix);
            if (oldKey.Length == 0 || string.IsNullOrWhiteSpace(newPrefix)) return result;
            // "E:" alone means the current directory of drive E: for Windows, not its root
            if (newPrefix.Length == 2 && newPrefix[1] == ':' && OperatingSystem.IsWindows()) newPrefix += "\\";
            string newRoot = System.IO.Path.GetFullPath(newPrefix);
            var used = new HashSet<string>(ListSources().Select(s => Normalize(s.Path)), StringComparer.OrdinalIgnoreCase);

            foreach (var s in ListSources())
            {
                string key = Normalize(s.Path);
                if (!IsUnder(key, oldKey) || (onlyMissing && !s.FileMissing)) continue;
                string target = Combine(newRoot, key.Substring(oldKey.Length));
                var item = new RelocatedSource { Source = s, NewPath = target };
                if (string.Equals(Normalize(target), key, StringComparison.OrdinalIgnoreCase)) continue;
                if (s.Kind == SourceKind.MsgFolder ? !Directory.Exists(target) : !File.Exists(target)) item.Outcome = RelocationOutcome.NotFound;
                else if (s.Kind != SourceKind.MsgFolder && s.Size > 0 && new FileInfo(target).Length != s.Size) item.Outcome = RelocationOutcome.SizeMismatch;
                else if (s.Kind == SourceKind.MsgFolder && !MsgFilesPresent(s, target)) item.Outcome = RelocationOutcome.MsgFilesMissing;
                else if (used.Contains(Normalize(target))) item.Outcome = RelocationOutcome.PathInUse;
                else item.Outcome = RelocationOutcome.Relocated;
                result.Items.Add(item);
            }

            var moves = result.Items.Where(i => i.Outcome == RelocationOutcome.Relocated).ToList();
            if (dryRun || moves.Count == 0) return result;
            lock (DbLock)
            {
                _db.Begin();
                try
                {
                    var upd = _db.Prepare("UPDATE messages SET file_path=? WHERE id=?");
                    foreach (var m in moves)
                    {
                        _db.Exec("UPDATE sources SET path=?, error=NULL WHERE id=?", m.NewPath, m.Source.Id);
                        // MSG files of the source: same place below the source path
                        var files = new List<(long id, string path)>();
                        var st = _db.Prepare("SELECT id, file_path FROM messages WHERE source_id=? AND file_path IS NOT NULL");
                        st.Bind(1, m.Source.Id);
                        while (st.Step()) files.Add((st.GetLong(0), st.GetString(1)));
                        st.Reset();
                        string oldSource = Normalize(m.Source.Path);
                        foreach (var (id, path) in files)
                        {
                            string key = Normalize(path);
                            if (IsUnder(key, oldSource)) upd.Reset().BindAll(Combine(m.NewPath, key.Substring(oldSource.Length)), id).Run();
                        }
                    }
                    _db.Commit();
                }
                catch { _db.Rollback(); throw; }
            }
            return result;
        }

        /// <summary>A folder of MSG files is the same if the indexed files are found below the new folder (sample of 50).</summary>
        private bool MsgFilesPresent(SourceInfo s, string target)
        {
            var files = new List<string>();
            lock (DbLock)
            {
                var st = _db.Prepare("SELECT file_path FROM messages WHERE source_id=? AND file_path IS NOT NULL ORDER BY random() LIMIT 50");
                st.Bind(1, s.Id);
                while (st.Step()) files.Add(st.GetString(0));
                st.Reset();
            }
            string oldKey = Normalize(s.Path);
            return files.All(f => { var k = Normalize(f); return IsUnder(k, oldKey) && File.Exists(Combine(target, k.Substring(oldKey.Length))); });
        }

        /// <summary>
        /// Deduces, from the new location of one source, the folder that moved: the trailing folder names shared by the
        /// old and the new path are kept (E:\Export\Dupont\a.pst → /Volumes/USB/Export/Dupont/a.pst gives E:\ → /Volumes/USB),
        /// so that the other sources of the same tree are found again at the same time.
        /// </summary>
        public static (string oldPrefix, string newPrefix) InferRelocation(string oldPath, string newPath)
        {
            string oldKey = Normalize(oldPath), newKey = Normalize(System.IO.Path.GetFullPath(newPath));
            var o = oldKey.Split('/'); var n = newKey.Split('/');
            int common = 0;
            // the first segment (drive, server, empty root) is never shared, so that both prefixes stay non-empty
            while (common < Math.Min(o.Length, n.Length) - 1 && string.Equals(o[o.Length - 1 - common], n[n.Length - 1 - common], StringComparison.OrdinalIgnoreCase))
                common++;
            return (CutTail(oldKey, common), CutTail(newKey, common));
        }

        /// <summary>Applies <see cref="InferRelocation"/> then <see cref="RelocateSources"/> to the missing sources.</summary>
        public RelocationResult RelocateFrom(SourceInfo source, string newPath, bool dryRun = false)
        {
            var (oldPrefix, newPrefix) = InferRelocation(source.Path, newPath);
            // the other sources of the moved folder are only taken along if they are missing from their recorded place
            var result = RelocateSources(oldPrefix, newPrefix, onlyMissing: true, dryRun: dryRun);
            // the folder deduction did not lead to this source (renamed file): move this one only
            if (!result.Items.Any(i => i.Source.Id == source.Id && i.Outcome == RelocationOutcome.Relocated))
            {
                var single = RelocateSources(source.Path, newPath, dryRun: dryRun);
                if (single.Relocated > 0 || result.Items.Count == 0) return single;
            }
            return result;
        }

        /// <summary>
        /// The case folder was moved or copied (another drive, another computer): sources stored inside it or next to it
        /// moved with it. Missing sources are looked for at the same place relative to the new location of the case folder.
        /// </summary>
        private void AutoRelocate()
        {
            string current = System.IO.Path.GetFullPath(Folder);
            string last = GetMeta(LastFolderKey);
            if (last != null && !string.Equals(Normalize(last), Normalize(current), StringComparison.OrdinalIgnoreCase)
                && ListSources().Any(s => s.FileMissing))
            {
                var total = new RelocationResult();
                string o = Normalize(last), n = Normalize(current);
                // the case folder itself, then its parents as long as their names are the same (…\Affaire1 → …/Affaire1)
                for (int up = 0; up <= 3; up++)
                {
                    var (op, np) = (CutTail(o, up), CutTail(n, up));
                    if (op.Length == 0 || np.Length == 0) break;
                    if (up > 0 && !string.Equals(LastSegment(CutTail(o, up - 1)), LastSegment(CutTail(n, up - 1)), StringComparison.OrdinalIgnoreCase)) break;
                    foreach (var i in RelocateSources(op, np, onlyMissing: true).Items.Where(i => i.Outcome == RelocationOutcome.Relocated))
                        total.Items.Add(i);
                    if (!ListSources().Any(s => s.FileMissing)) break;
                }
                if (total.Items.Count > 0) AutoRelocation = total;
            }
            SetMeta(LastFolderKey, current);
        }

        /// <summary>Path with '/' separators and no trailing separator (the root "/" becomes "").</summary>
        public static string Normalize(string path) => (path ?? "").Trim().Replace('\\', '/').TrimEnd('/');

        private static bool IsUnder(string key, string prefixKey)
            => key.Length >= prefixKey.Length && key.StartsWith(prefixKey, StringComparison.OrdinalIgnoreCase)
               && (key.Length == prefixKey.Length || key[prefixKey.Length] == '/');

        private static string Combine(string root, string remainder)
        {
            var parts = remainder.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 0 ? root : System.IO.Path.Combine(new[] { root }.Concat(parts).ToArray());
        }

        /// <summary>Removes the last <paramref name="segments"/> segments of a normalized path.</summary>
        private static string CutTail(string key, int segments)
        {
            for (int i = 0; i < segments; i++)
            {
                int slash = key.LastIndexOf('/');
                if (slash < 0) return "";
                key = key.Substring(0, slash);
            }
            return key.Length == 0 && segments > 0 ? "/" : key;
        }

        private static string LastSegment(string key) => key.Substring(key.LastIndexOf('/') + 1);
    }
}
