using System;
using System.IO;
using System.Text;

namespace PartLock
{
    /// <summary>
    /// Tiny key=value settings file at %APPDATA%\PartLock\settings.txt.
    /// Only remembers the vault folder for now; never throws if the file is unreadable.
    /// </summary>
    public sealed class AppSettings
    {
        public string RepoPath { get; set; }

        /// <summary>Show the "PartLock" right-click menu on CAD files in File Explorer (default on).</summary>
        public bool ExplorerMenu { get; set; } = true;

        /// <summary>Whether PartLock already offered the Windows 11 full right-click menu.</summary>
        public bool FullMenuAsked { get; set; }

        /// <summary>Every vault folder PartLock has opened on this computer (for the repo switcher).</summary>
        public System.Collections.Generic.List<string> KnownRepos { get; } =
            new System.Collections.Generic.List<string>();

        /// <summary>Makes <paramref name="path"/> the current vault and remembers it.</summary>
        public void UseRepo(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            RepoPath = path;
            KnownRepos.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            KnownRepos.Insert(0, path);
        }

        private static string SettingsFile
        {
            get
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(appData, "PartLock", "settings.txt");
            }
        }

        public static AppSettings Load()
        {
            var settings = new AppSettings();
            try
            {
                string file = SettingsFile;
                if (!File.Exists(file)) return settings;

                foreach (string raw in File.ReadAllLines(file))
                {
                    string line = (raw ?? "").Trim();
                    if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;

                    int equals = line.IndexOf('=');
                    if (equals <= 0) continue;

                    string key = line.Substring(0, equals).Trim();
                    string value = line.Substring(equals + 1).Trim();
                    if (string.Equals(key, "repoPath", StringComparison.OrdinalIgnoreCase))
                    {
                        settings.RepoPath = value;
                    }
                    else if (string.Equals(key, "explorerMenu", StringComparison.OrdinalIgnoreCase))
                    {
                        settings.ExplorerMenu = !string.Equals(value, "off", StringComparison.OrdinalIgnoreCase);
                    }
                    else if (string.Equals(key, "fullMenuAsked", StringComparison.OrdinalIgnoreCase))
                    {
                        settings.FullMenuAsked = string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
                    }
                    else if (string.Equals(key, "knownRepo", StringComparison.OrdinalIgnoreCase) && value.Length > 0 &&
                             !settings.KnownRepos.Exists(p => string.Equals(p, value, StringComparison.OrdinalIgnoreCase)))
                    {
                        settings.KnownRepos.Add(value);
                    }
                }
            }
            catch
            {
                // Settings are a convenience; never stop the app over them.
            }
            return settings;
        }

        public void Save()
        {
            try
            {
                string file = SettingsFile;
                string dir = Path.GetDirectoryName(file);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var text = new StringBuilder();
                text.AppendLine("# PartLock settings");
                if (!string.IsNullOrEmpty(RepoPath))
                {
                    text.AppendLine("repoPath=" + RepoPath);
                }
                if (!ExplorerMenu) text.AppendLine("explorerMenu=off");
                if (FullMenuAsked) text.AppendLine("fullMenuAsked=yes");
                foreach (string known in KnownRepos)
                {
                    if (!string.IsNullOrEmpty(known)) text.AppendLine("knownRepo=" + known);
                }
                File.WriteAllText(file, text.ToString(), Encoding.UTF8);
            }
            catch
            {
                // Same as above: a failed save must not break anything.
            }
        }
    }
}
