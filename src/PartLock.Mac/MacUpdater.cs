// PartLock for macOS - in-app updates from the same GitHub releases as Windows.
//
// The release carries PartLock-mac.zip (PartLock.app for both Apple Silicon and Intel). Updating
// downloads it, unpacks it with ditto (keeps permissions and signatures), swaps the app bundle
// and reopens PartLock once this copy has quit. Files PartLock downloads itself aren't
// quarantined, so the update opens without the "unidentified developer" prompt.
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using PartLock.Core;

namespace PartLock.Mac
{
    internal sealed class MacUpdate
    {
        public Version Version;
        public string Tag;
        public string Notes;
        public string PageUrl;
        public string ZipUrl;
        public long Size;
    }

    internal static class MacUpdater
    {
        public const string Repository = "jscr3ator/partlock";
        public const string AssetName = "PartLock-mac.zip";
        /// <summary>Text asset with the Mac build ("0.8.0.12"), so Mac-only fixes reach Macs without a new release.</summary>
        public const string VersionAssetName = "PartLock-mac.version";

        public static Version Current
        {
            get
            {
                Version v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
                return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
            }
        }

        /// <summary>Release version plus the Mac build number (e.g. 0.8.0.12).</summary>
        public static Version CurrentBuild
        {
            get
            {
                Version v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);
                return new Version(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
            }
        }

        public static async Task<MacUpdate> CheckAsync(CancellationToken ct)
        {
            string url = "https://api.github.com/repos/" + Repository + "/releases/latest";
            string json;
            try { json = await GitSetup.GetStringAsync(url, null, ct); }
            catch (WebException)
            {
                string token = await GitSetup.GetStoredTokenAsync(ct);
                if (token == null) return null;
                try { json = await GitSetup.GetStringAsync(url, token, ct); }
                catch (WebException) { return null; }
            }

            GitHubReleaseDto release = GitSetup.Deserialize<GitHubReleaseDto>(json);
            if (release == null || string.IsNullOrEmpty(release.Tag)) return null;
            Version latest;
            if (!Version.TryParse(release.Tag.Trim().TrimStart('v', 'V'), out latest)) return null;
            latest = new Version(latest.Major, latest.Minor, Math.Max(0, latest.Build));
            if (latest < Current) return null;

            var update = new MacUpdate { Version = latest, Tag = release.Tag, Notes = release.Body, PageUrl = release.HtmlUrl };
            string versionUrl = null;
            if (release.Assets != null)
                foreach (GitHubAssetDto a in release.Assets)
                {
                    if (a == null) continue;
                    if (string.Equals(a.Name, AssetName, StringComparison.OrdinalIgnoreCase)) { update.ZipUrl = a.Url; update.Size = a.Size; }
                    if (string.Equals(a.Name, VersionAssetName, StringComparison.OrdinalIgnoreCase)) versionUrl = a.Url;
                }
            if (update.ZipUrl == null) return null; // a Windows-only release isn't an update for Mac

            if (latest == Current)
            {
                // Same release: only an update if the release carries a newer Mac build.
                if (versionUrl == null) return null;
                Version build;
                try
                {
                    string text = (await DownloadTextAsync(versionUrl, ct)).Trim();
                    if (!Version.TryParse(text, out build)) return null;
                }
                catch (Exception) { return null; }
                build = new Version(build.Major, build.Minor, Math.Max(0, build.Build), Math.Max(0, build.Revision));
                if (build <= CurrentBuild) return null;
                update.Tag = release.Tag + " (Mac build " + build.Revision + ")";
            }
            return update;
        }

        /// <summary>
        /// Downloads and stages the new app, then schedules the swap + relaunch for when this
        /// process exits. Returns false (nothing changed) when PartLock can't update itself here;
        /// the caller should then open the release page.
        /// </summary>
        public static async Task<bool> InstallAsync(MacUpdate update, IProgress<string> log, CancellationToken ct)
        {
            string bundle = MacPlatform.BundlePath;
            if (bundle == null || !OperatingSystem.IsMacOS() || MacPlatform.WrongLocationMessage() != null) return false;
            string parent = Path.GetDirectoryName(bundle);
            if (!CanWrite(parent)) return false;

            string work = Path.Combine(Path.GetTempPath(), "PartLock-update-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(work);
            string zip = Path.Combine(work, AssetName);

            Report(log, "Downloading PartLock " + update.Tag + "…");
            using (var client = new WebClient())
            using (ct.Register(client.CancelAsync))
            {
                client.Headers[HttpRequestHeader.UserAgent] = "PartLock";
                await client.DownloadFileTaskAsync(new Uri(update.ZipUrl), zip);
            }
            if (update.Size > 0 && new FileInfo(zip).Length != update.Size)
                throw new VaultException("The download didn't finish properly, so PartLock wasn't changed. Try again.");

            Report(log, "Unpacking…");
            Tuple<int, string> unzip = MacPlatform.Run("/usr/bin/ditto", "-x", "-k", zip, work);
            string staged = Path.Combine(work, "PartLock.app");
            if (unzip.Item1 != 0 || !File.Exists(Path.Combine(staged, "Contents", "MacOS", "PartLock")))
                throw new VaultException("The update couldn't be unpacked, so PartLock wasn't changed.", unzip.Item2);

            // Move the new app next to the old one now (same disk, so the final swap is instant).
            string incoming = bundle + ".new";
            if (Directory.Exists(incoming)) Directory.Delete(incoming, true);
            Tuple<int, string> move = MacPlatform.Run("/bin/mv", staged, incoming);
            if (move.Item1 != 0) return false;

            Report(log, "Restarting PartLock…");
            int pid = Environment.ProcessId;
            string old = bundle + ".old";
            string script =
                "while kill -0 " + pid + " 2>/dev/null; do sleep 0.3; done; " +
                "rm -rf " + Sh(old) + "; " +
                "mv " + Sh(bundle) + " " + Sh(old) + " && mv " + Sh(incoming) + " " + Sh(bundle) + " || mv " + Sh(old) + " " + Sh(bundle) + "; " +
                "/usr/bin/xattr -dr com.apple.quarantine " + Sh(bundle) + " 2>/dev/null; " +
                "open " + Sh(bundle) + "; sleep 5; rm -rf " + Sh(old) + " " + Sh(work);
            var psi = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("nohup sh -c " + Sh(script) + " >/dev/null 2>&1 &");
            Process.Start(psi);
            return true;
        }

        private static async Task<string> DownloadTextAsync(string url, CancellationToken ct)
        {
            using (var client = new WebClient())
            using (ct.Register(client.CancelAsync))
            {
                client.Headers[HttpRequestHeader.UserAgent] = "PartLock";
                return await client.DownloadStringTaskAsync(new Uri(url));
            }
        }

        private static bool CanWrite(string dir)
        {
            try
            {
                string probe = Path.Combine(dir, ".partlock-write-test-" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(probe, "x");
                File.Delete(probe);
                return true;
            }
            catch (Exception) { return false; }
        }

        private static string Sh(string s) { return "'" + s.Replace("'", "'\\''") + "'"; }

        private static void Report(IProgress<string> log, string line)
        {
            if (log == null) return;
            try { log.Report(line); } catch (Exception) { }
        }
    }
}
