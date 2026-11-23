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
                File.WriteAllText(file, text.ToString(), Encoding.UTF8);
            }
            catch
            {
                // Same as above: a failed save must not break anything.
            }
        }
    }
}
