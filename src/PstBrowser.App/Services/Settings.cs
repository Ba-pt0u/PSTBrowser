using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace PstBrowser.App.Services
{
    /// <summary>User preferences, stored in %LOCALAPPDATA%\PstBrowser\settings.json (paths only, never message content).</summary>
    public sealed class Settings
    {
        public List<string> RecentWorkspaces { get; set; } = new List<string>();
        public double? WindowWidth { get; set; }
        public double? WindowHeight { get; set; }
        public bool Maximized { get; set; }
        public double TreeWidth { get; set; } = 270;
        public double ListWidthRatio { get; set; } = 0.5;
        public bool HideEmptyFolders { get; set; } = true;
        /// <summary>Reading pane below the list (null = automatic: below on small screens).</summary>
        public bool? ReaderBottom { get; set; }
        /// <summary>
        /// Columns of the message list, in display order (see <see cref="ColumnCatalog"/>). Empty: the view named by
        /// <see cref="ColumnPreset"/> is used ("Standard" or "Investigation").
        /// </summary>
        public List<ColumnState> ListColumns { get; set; } = new List<ColumnState>();
        public string ColumnPreset { get; set; } = "Standard";

        private static string FilePath => Path.Combine(App.LocalDataFolder, "settings.json");

        public static Settings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
            }
            catch { }
            return new Settings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        public void AddRecent(string folder)
        {
            RecentWorkspaces.RemoveAll(p => string.Equals(p, folder, StringComparison.OrdinalIgnoreCase));
            RecentWorkspaces.Insert(0, folder);
            RecentWorkspaces = RecentWorkspaces.Take(8).ToList();
            Save();
        }
    }
}
