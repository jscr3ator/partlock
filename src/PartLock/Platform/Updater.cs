// PartLock - in-app updates from the GitHub releases of the PartLock repository.
//
// Check: GET /repos/{owner}/{repo}/releases/latest (anonymously, then with the user's saved
// GitHub sign-in in case the repository is private) and compare the tag with this exe's version.
//
// Install: download the new PartLock.exe next to the running one as PartLock.exe.new, check it
// looks like a Windows program, then rename the running exe to PartLock.exe.old (Windows allows
// renaming a running exe), move the new one into place and start it. The .old file is deleted
// on the next start. Any failure leaves the current exe in place.
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using PartLock.Core;

namespace PartLock.Platform
{
    public class UpdateInfo
    {
        public Version Version { get; set; }
        public string Tag { get; set; }
        public string Notes { get; set; }
        public string PageUrl { get; set; }
        internal string AssetApiUrl { get; set; }
        internal string AssetDownloadUrl { get; set; }
        internal long AssetSize { get; set; }
    }

    public static class Updater
    {
        public const string Repository = "jscr3ator/partlock";
        private const string AssetName = "PartLock.exe";

        public static Version CurrentVersion
        {
            get
            {
                Version v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
                return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
            }
        }

        /// <summary>The newer release, or null when this is the latest (or GitHub can't be reached).</summary>
        public static async Task<UpdateInfo> CheckAsync(CancellationToken ct)
        {
            string url = "https://api.github.com/repos/" + Repository + "/releases/latest";
            string json = null;
            try
            {
                json = await GitSetup.GetStringAsync(url, null, ct).ConfigureAwait(false);
            }
            catch (WebException)
            {
                // Private repository (404 without sign-in) or offline: try with the saved sign-in.
                string token = await GitSetup.GetStoredTokenAsync(ct).ConfigureAwait(false);
                if (token == null) return null;
                try { json = await GitSetup.GetStringAsync(url, token, ct).ConfigureAwait(false); }
                catch (WebException) { return null; }
            }

            GitHubReleaseDto release = GitSetup.Deserialize<GitHubReleaseDto>(json);
            if (release == null || string.IsNullOrEmpty(release.Tag)) return null;

            Version latest;
            if (!Version.TryParse(release.Tag.Trim().TrimStart('v', 'V'), out latest)) return null;
            latest = new Version(latest.Major, latest.Minor, Math.Max(0, latest.Build));
            if (latest <= CurrentVersion) return null;

            var info = new UpdateInfo
            {
                Version = latest,
                Tag = release.Tag,
                Notes = release.Body,
                PageUrl = release.HtmlUrl
            };
            if (release.Assets != null)
            {
                foreach (GitHubAssetDto asset in release.Assets)
                {
                    if (asset != null && string.Equals(asset.Name, AssetName, StringComparison.OrdinalIgnoreCase))
                    {
                        info.AssetApiUrl = asset.ApiUrl;
                        info.AssetDownloadUrl = asset.Url;
                        info.AssetSize = asset.Size;
                        break;
                    }
                }
            }
            return info;
        }

        /// <summary>
        /// Downloads and swaps in the new exe, then starts it. The caller should exit right after
        /// this returns true. Returns false (with nothing changed) when the folder isn't writable;
        /// the caller can then open <see cref="UpdateInfo.PageUrl"/> instead.
        /// </summary>
        public static async Task<bool> InstallAsync(UpdateInfo info, IProgress<string> log, CancellationToken ct)
        {
            if (info == null || (info.AssetApiUrl == null && info.AssetDownloadUrl == null))
                throw new VaultException("This release has no PartLock.exe to download. Get it from the release page instead.");

            string exe = Process.GetCurrentProcess().MainModule.FileName;
            string newExe = exe + ".new";
            string oldExe = exe + ".old";

            try
            {
                File.Delete(newExe);
                using (File.Create(newExe)) { }
            }
            catch (UnauthorizedAccessException) { return false; }
            catch (IOException) { return false; }

            Report(log, "Downloading PartLock " + info.Tag + "…");
            try
            {
                await DownloadAsync(info, newExe, ct).ConfigureAwait(false);

                var file = new FileInfo(newExe);
                byte[] head = new byte[2];
                using (FileStream s = file.OpenRead()) s.Read(head, 0, 2);
                if (file.Length < 50 * 1024 || head[0] != (byte)'M' || head[1] != (byte)'Z' ||
                    (info.AssetSize > 0 && file.Length != info.AssetSize))
                {
                    throw new VaultException("The download didn't look right, so PartLock wasn't changed. Try again later.");
                }

                Report(log, "Installing…");
                try { File.Delete(oldExe); } catch (Exception) { }
                File.Move(exe, oldExe);
                try
                {
                    File.Move(newExe, exe);
                }
                catch (Exception)
                {
                    File.Move(oldExe, exe); // put the working version back
                    throw;
                }
            }
            catch (Exception)
            {
                try { File.Delete(newExe); } catch (Exception) { }
                throw;
            }

            Report(log, "Restarting PartLock…");
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) });
            return true;
        }

        /// <summary>Removes the previous exe left behind by an update (call at start-up).</summary>
        public static void CleanUpOldVersion()
        {
            try
            {
                string old = Process.GetCurrentProcess().MainModule.FileName + ".old";
                if (File.Exists(old)) File.Delete(old);
            }
            catch (Exception) { }
        }

        private static async Task DownloadAsync(UpdateInfo info, string target, CancellationToken ct)
        {
            string token = await GitSetup.GetStoredTokenAsync(ct).ConfigureAwait(false);
            string url;

            if (token != null && info.AssetApiUrl != null)
            {
                // Works for private repositories. GitHub answers with a redirect to a signed
                // download URL that must be fetched WITHOUT the Authorization header.
                var request = (HttpWebRequest)WebRequest.Create(info.AssetApiUrl);
                request.UserAgent = "PartLock";
                request.Accept = "application/octet-stream";
                request.Headers[HttpRequestHeader.Authorization] = "token " + token;
                request.AllowAutoRedirect = false;
                request.Timeout = 30000;
                using (var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
                {
                    if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400)
                    {
                        url = response.Headers[HttpResponseHeader.Location];
                    }
                    else
                    {
                        using (Stream body = response.GetResponseStream())
                        using (FileStream file = File.Create(target))
                        {
                            await body.CopyToAsync(file, 81920, ct).ConfigureAwait(false);
                        }
                        return;
                    }
                }
            }
            else
            {
                url = info.AssetDownloadUrl;
            }

            using (var client = new WebClient())
            using (ct.Register(client.CancelAsync))
            {
                client.Headers[HttpRequestHeader.UserAgent] = "PartLock";
                await client.DownloadFileTaskAsync(new Uri(url), target).ConfigureAwait(false);
            }
        }

        private static void Report(IProgress<string> log, string line)
        {
            if (log == null) return;
            try { log.Report(line); } catch (Exception) { }
        }
    }
}
