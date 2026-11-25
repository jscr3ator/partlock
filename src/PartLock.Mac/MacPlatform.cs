// PartLock for macOS - everything that differs from Windows: where the bundled tools live,
// Git/sign-in setup, Finder, opening files and the "run from Applications" check.
//
// App bundle layout (built by mac/build-mac.sh):
//   PartLock.app/Contents/MacOS/PartLock                 launcher script (picks arm64 or x64)
//   PartLock.app/Contents/MacOS/arm64|x64/PartLock       the .NET app for that CPU
//   PartLock.app/Contents/Resources/tools/arm64|x64/git-lfs
//   PartLock.app/Contents/Resources/tools/arm64|x64/gcm/git-credential-manager
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using PartLock.Core;

namespace PartLock.Mac
{
    internal static class MacPlatform
    {
        private const string GitHubHelperKey = "credential.https://github.com.helper";

        /// <summary>The PartLock.app folder, or null when not running from an app bundle (dev / Linux test runs).</summary>
        public static string BundlePath
        {
            get
            {
                string dir = AppContext.BaseDirectory.TrimEnd('/');
                int i = dir.IndexOf(".app/Contents/", StringComparison.Ordinal);
                return i < 0 ? null : dir.Substring(0, i + 4);
            }
        }

        public static string Arch
        {
            get { return RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64"; }
        }

        public static string ToolsDir
        {
            get
            {
                string bundle = BundlePath;
                return bundle == null ? null : Path.Combine(bundle, "Contents", "Resources", "tools", Arch);
            }
        }

        public static string BundledCredentialManager
        {
            get
            {
                string tools = ToolsDir;
                if (tools == null) return null;
                string gcm = Path.Combine(tools, "gcm", "git-credential-manager");
                return File.Exists(gcm) ? gcm : null;
            }
        }

        /// <summary>
        /// macOS runs apps that weren't moved out of Downloads (or that run straight off the
        /// .dmg) from a hidden read-only copy. They can't update themselves, so PartLock asks to
        /// be moved to Applications first. Returns a message to show, or null when all is well.
        /// </summary>
        public static string WrongLocationMessage()
        {
            string bundle = BundlePath;
            if (bundle == null) return null;
            if (bundle.Contains("/AppTranslocation/"))
                return "Please move PartLock to your Applications folder first.\n\nDrag PartLock from Downloads (or from the PartLock disk) "
                     + "into Applications, then open it from there. That lets PartLock keep itself up to date.";
            if (bundle.StartsWith("/Volumes/", StringComparison.Ordinal))
                return "You're running PartLock straight from the download.\n\nDrag the PartLock icon onto the Applications folder "
                     + "in that window, then open PartLock from Applications.";
            return null;
        }

        /// <summary>Puts the bundled git-lfs and the usual Git locations on PATH for every git PartLock runs.</summary>
        public static void PrepareEnvironment()
        {
            var parts = new List<string>();
            string tools = ToolsDir;
            if (tools != null && Directory.Exists(tools)) parts.Add(tools);
            parts.AddRange(new[] { "/usr/bin", "/bin", "/usr/sbin", "/sbin", "/opt/homebrew/bin", "/usr/local/bin" });
            string current = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (string p in current.Split(':')) if (p.Length > 0 && !parts.Contains(p)) parts.Add(p);
            Environment.SetEnvironmentVariable("PATH", string.Join(":", parts));

            // Apps opened from a downloaded .dmg are "quarantined", which makes macOS block the
            // helper tools inside the app. The user already chose to open PartLock, so clear it.
            string bundle = BundlePath;
            if (bundle != null && OperatingSystem.IsMacOS())
            {
                try { Run("/usr/bin/xattr", "-dr", "com.apple.quarantine", bundle); } catch (Exception) { }
            }
        }

        /// <summary>True when Apple's Command Line Tools (which include git) are installed.</summary>
        public static bool CommandLineToolsInstalled()
        {
            if (!OperatingSystem.IsMacOS()) return true;
            try
            {
                if (File.Exists("/opt/homebrew/bin/git") || File.Exists("/usr/local/bin/git")) return true;
                return Run("/usr/bin/xcode-select", "-p").Item1 == 0;
            }
            catch (Exception) { return false; }
        }

        /// <summary>Shows Apple's "install Command Line Tools" prompt.</summary>
        public static void StartCommandLineToolsInstall()
        {
            try { Process.Start(new ProcessStartInfo("/usr/bin/xcode-select", "--install") { UseShellExecute = false }); }
            catch (Exception) { }
        }

        /// <summary>
        /// Makes Git use the bundled Git Credential Manager for github.com (browser sign-in, saved
        /// in the macOS Keychain), the same thing Git for Windows does on Windows. Idempotent.
        /// </summary>
        public static async Task ConfigureCredentialManagerAsync()
        {
            string gcm = BundledCredentialManager;
            if (gcm == null) return;
            var runner = new GitRunner(null);
            CancellationToken none = CancellationToken.None;
            GitResult current = await runner.RunAsync(null, new[] { "config", "--global", "--get-all", GitHubHelperKey }, none, null, false, 30000);
            string[] lines = (current.StdOut ?? string.Empty).Replace("\r", string.Empty).Split('\n');
            List<string> values = lines.Take(Math.Max(0, lines.Length - 1)).ToList(); // trailing newline
            if (values.Count == 2 && values[0] == string.Empty && values[1] == gcm) return;

            await runner.RunAsync(null, new[] { "config", "--global", "--unset-all", GitHubHelperKey }, none, null, false, 30000);
            // An empty entry first resets Apple's default "osxkeychain" helper for github.com only.
            await runner.RunAsync(null, new[] { "config", "--global", "--add", GitHubHelperKey, string.Empty }, none, null, false, 30000);
            await runner.RunAsync(null, new[] { "config", "--global", "--add", GitHubHelperKey, gcm }, none, null, false, 30000);
        }

        public static void OpenUrl(string target)
        {
            if (string.IsNullOrEmpty(target)) return;
            try
            {
                if (OperatingSystem.IsMacOS()) Process.Start(new ProcessStartInfo("/usr/bin/open", Quote(target)) { UseShellExecute = false });
                else Process.Start(new ProcessStartInfo("xdg-open", Quote(target)) { UseShellExecute = false });
            }
            catch (Exception) { }
        }

        /// <summary>Opens the file with its default app (SolidWorks files have none on a Mac, so Finder asks).</summary>
        public static void OpenFile(string path) { OpenUrl(path); }

        public static void RevealInFinder(string path)
        {
            try
            {
                if (OperatingSystem.IsMacOS()) Process.Start(new ProcessStartInfo("/usr/bin/open", "-R " + Quote(path)) { UseShellExecute = false });
                else OpenUrl(Path.GetDirectoryName(path));
            }
            catch (Exception) { }
        }

        public static string DocumentsFolder
        {
            get
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string docs = Path.Combine(home, "Documents");
                return Directory.Exists(docs) ? docs : home;
            }
        }

        public static Tuple<int, string> Run(string file, params string[] args)
        {
            var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string a in args) psi.ArgumentList.Add(a);
            using (Process p = Process.Start(psi))
            {
                string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit(60000);
                return Tuple.Create(p.HasExited ? p.ExitCode : -1, output);
            }
        }

        private static string Quote(string s) { return "\"" + s.Replace("\"", "\\\"") + "\""; }
    }
}
