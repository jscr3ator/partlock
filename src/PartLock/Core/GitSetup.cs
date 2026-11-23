// PartLock - first-run prerequisites: installing Git for Windows and signing in to GitHub.
//
// Git install: tries winget first (built into Windows 10/11), then falls back to downloading
// the official Git for Windows installer from GitHub and running it silently. Both ask for
// admin rights through the normal Windows UAC prompt.
//
// GitHub sign-in: uses Git Credential Manager (bundled with Git for Windows). PartLock asks
// git for a stored github.com credential without any UI; when there is none (or it no longer
// works) it runs the interactive GCM sign-in (browser / device code), checks the token
// against the GitHub API and stores it. PartLock never sees or saves the password itself
// beyond passing it back to git.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PartLock.Core
{
    /// <summary>Who is signed in to GitHub on this computer.</summary>
    public class GitHubAccount
    {
        public string Login { get; set; }
        public string Name { get; set; }
        public long Id { get; set; }
        public string Email { get; set; }
        public string AvatarUrl { get; set; }

        /// <summary>Real email when public, otherwise GitHub's private no-reply address.</summary>
        public string CommitEmail
        {
            get
            {
                if (!string.IsNullOrEmpty(Email)) return Email;
                return Id + "+" + Login + "@users.noreply.github.com";
            }
        }

        public string DisplayName
        {
            get { return string.IsNullOrWhiteSpace(Name) ? Login : Name; }
        }
    }

    [DataContract]
    internal class GitHubUserDto
    {
        [DataMember(Name = "login")] public string Login { get; set; }
        [DataMember(Name = "name")] public string Name { get; set; }
        [DataMember(Name = "id")] public long Id { get; set; }
        [DataMember(Name = "email")] public string Email { get; set; }
        [DataMember(Name = "avatar_url")] public string AvatarUrl { get; set; }
    }

    [DataContract]
    internal class GitHubOwnerDto
    {
        [DataMember(Name = "login")] public string Login { get; set; }
    }

    [DataContract]
    internal class GitHubRepoDto
    {
        [DataMember(Name = "full_name")] public string FullName { get; set; }
        [DataMember(Name = "name")] public string Name { get; set; }
        [DataMember(Name = "clone_url")] public string CloneUrl { get; set; }
        [DataMember(Name = "html_url")] public string HtmlUrl { get; set; }
        [DataMember(Name = "description")] public string Description { get; set; }
        [DataMember(Name = "private")] public bool Private { get; set; }
        [DataMember(Name = "pushed_at")] public string PushedAt { get; set; }
        [DataMember(Name = "owner")] public GitHubOwnerDto Owner { get; set; }
    }

    /// <summary>A GitHub repository the signed-in user can open.</summary>
    public class GitHubRepo
    {
        public string FullName { get; set; }
        public string Name { get; set; }
        public string CloneUrl { get; set; }
        public string HtmlUrl { get; set; }
        public string Description { get; set; }
        public bool Private { get; set; }
        public DateTime PushedAtUtc { get; set; }
    }

    [DataContract]
    internal class GitHubAssetDto
    {
        [DataMember(Name = "name")] public string Name { get; set; }
        [DataMember(Name = "browser_download_url")] public string Url { get; set; }
    }

    [DataContract]
    internal class GitHubReleaseDto
    {
        [DataMember(Name = "tag_name")] public string Tag { get; set; }
        [DataMember(Name = "assets")] public List<GitHubAssetDto> Assets { get; set; }
    }

    public static class GitSetup
    {
        private const string GitHubHost = "github.com";
        private const string UserAgent = "PartLock";

        static GitSetup()
        {
            // .NET Framework 4.8 on older Windows builds may not offer TLS 1.2 by default.
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }
            catch (Exception) { }
        }

        // ------------------------------------------------------------------
        // Git for Windows install
        // ------------------------------------------------------------------

        /// <summary>
        /// Installs (or upgrades) Git for Windows, which includes Git LFS and Git Credential
        /// Manager. Returns when the installer has finished; the caller should re-run
        /// VaultService.CheckEnvironmentAsync afterwards.
        /// </summary>
        public static async Task InstallGitAsync(IProgress<string> log, CancellationToken ct)
        {
            bool done = false;
            try
            {
                done = await TryWingetAsync(log, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Report(log, "winget didn't work (" + ex.Message + "). Downloading the installer instead…");
            }

            if (!done)
            {
                await DownloadAndRunInstallerAsync(log, ct).ConfigureAwait(false);
            }

            GitRunner.ResolveExecutable();
            Report(log, "Git for Windows is installed.");
        }

        private static async Task<bool> TryWingetAsync(IProgress<string> log, CancellationToken ct)
        {
            Report(log, "Installing Git for Windows with winget (Windows may ask for permission)…");
            var psi = new ProcessStartInfo
            {
                FileName = "winget",
                Arguments = "install --id Git.Git -e --source winget --silent " +
                            "--accept-package-agreements --accept-source-agreements --disable-interactivity",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            int exit;
            string output;
            try
            {
                var result = await RunProcessAsync(psi, ct, TimeSpan.FromMinutes(20)).ConfigureAwait(false);
                exit = result.Item1;
                output = result.Item2;
            }
            catch (Win32Exception)
            {
                Report(log, "winget isn't available on this computer. Downloading the installer instead…");
                return false;
            }

            // 0 = installed; -1978335189 (0x8A15002B) = already installed / no newer version.
            if (exit == 0 || exit == unchecked((int)0x8A15002B) ||
                output.IndexOf("already installed", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            Report(log, "winget couldn't install Git (exit code " + exit + "). Downloading the installer instead…");
            return false;
        }

        private static async Task DownloadAndRunInstallerAsync(IProgress<string> log, CancellationToken ct)
        {
            Report(log, "Finding the latest Git for Windows…");
            string json = await GetStringAsync(
                "https://api.github.com/repos/git-for-windows/git/releases/latest", null, ct).ConfigureAwait(false);
            GitHubReleaseDto release = Deserialize<GitHubReleaseDto>(json);

            string arch = Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit";
            var pattern = new Regex(@"^Git-[\d.]+(-rc\d+)?-" + Regex.Escape(arch) + @"\.exe$", RegexOptions.IgnoreCase);
            GitHubAssetDto asset = null;
            if (release != null && release.Assets != null)
            {
                foreach (GitHubAssetDto a in release.Assets)
                {
                    if (a != null && a.Name != null && pattern.IsMatch(a.Name)) { asset = a; break; }
                }
            }
            if (asset == null || string.IsNullOrEmpty(asset.Url))
                throw new VaultException("Couldn't find the Git for Windows installer. Install it from https://git-scm.com/download/win, then restart PartLock.");

            string dir = Path.Combine(Path.GetTempPath(), "PartLock");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, asset.Name);

            Report(log, "Downloading " + asset.Name + "…");
            using (var client = new WebClient())
            using (ct.Register(client.CancelAsync))
            {
                client.Headers[HttpRequestHeader.UserAgent] = UserAgent;
                int lastPercent = -1;
                client.DownloadProgressChanged += delegate(object s, DownloadProgressChangedEventArgs e)
                {
                    if (e.ProgressPercentage / 10 != lastPercent / 10)
                    {
                        lastPercent = e.ProgressPercentage;
                        Report(log, "  " + e.ProgressPercentage + "%");
                    }
                };
                try
                {
                    await client.DownloadFileTaskAsync(new Uri(asset.Url), file).ConfigureAwait(false);
                }
                catch (WebException) when (ct.IsCancellationRequested)
                {
                    throw new OperationCanceledException(ct);
                }
            }

            Report(log, "Running the Git installer (click Yes if Windows asks for permission)…");
            var psi = new ProcessStartInfo
            {
                FileName = file,
                Arguments = "/VERYSILENT /NORESTART /NOCANCEL /SP- /SUPPRESSMSGBOXES /CLOSEAPPLICATIONS",
                UseShellExecute = true,
                Verb = "runas"
            };

            int exit;
            try
            {
                exit = (await RunProcessAsync(psi, ct, TimeSpan.FromMinutes(20)).ConfigureAwait(false)).Item1;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                throw new VaultException("Git wasn't installed because the Windows permission prompt was declined. Try again and click Yes.");
            }

            try { File.Delete(file); } catch (Exception) { }

            if (exit != 0)
                throw new VaultException("The Git installer stopped with an error (code " + exit + "). Install it from https://git-scm.com/download/win, then restart PartLock.");
        }

        // ------------------------------------------------------------------
        // GitHub sign-in
        // ------------------------------------------------------------------

        /// <summary>
        /// Returns the signed-in GitHub account using a credential git already has, without
        /// showing any UI. Null when there is no stored credential or it was rejected by GitHub.
        /// When GitHub can't be reached the stored login is trusted (returned with Login from git).
        /// </summary>
        public static async Task<GitHubAccount> GetSignedInAccountAsync(CancellationToken ct)
        {
            var runner = new GitRunner(null);
            Credential cred = await FillAsync(runner, false, ct).ConfigureAwait(false);
            if (cred == null) return null;

            try
            {
                GitHubAccount account = await GetAccountAsync(cred.Password, ct).ConfigureAwait(false);
                if (account == null)
                {
                    // Token revoked or expired: forget it so the next sign-in starts clean.
                    await RejectAsync(runner, cred, ct).ConfigureAwait(false);
                }
                return account;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                // Offline: let them work with what they have; git will complain later if needed.
                return new GitHubAccount { Login = cred.UserName };
            }
        }

        /// <summary>
        /// Opens the Git Credential Manager sign-in (browser) for github.com, checks the
        /// result against the GitHub API and stores it for git.
        /// </summary>
        public static async Task<GitHubAccount> SignInAsync(IProgress<string> log, CancellationToken ct)
        {
            var runner = new GitRunner(null);
            await EnsureCredentialHelperAsync(runner, log, ct).ConfigureAwait(false);

            Report(log, "Opening the GitHub sign-in window…");
            Credential cred = await FillAsync(runner, true, ct).ConfigureAwait(false);
            if (cred == null)
                throw new VaultException("GitHub sign-in didn't finish. Click \"Sign in with GitHub\" to try again.");

            Report(log, "Checking your GitHub account…");
            GitHubAccount account = await GetAccountAsync(cred.Password, ct).ConfigureAwait(false);
            if (account == null)
            {
                await RejectAsync(runner, cred, ct).ConfigureAwait(false);
                throw new VaultException("GitHub didn't accept that sign-in. Try again.");
            }

            await runner.RunAsync(null, new[] { "credential", "approve" }, ct, cred.ToInput(), false, 60000)
                .ConfigureAwait(false);
            Report(log, "Signed in as " + account.Login + ".");
            return account;
        }

        /// <summary>Sets git's global user.name / user.email from the GitHub account when they are empty.</summary>
        public static async Task FillCommitIdentityAsync(GitHubAccount account, CancellationToken ct)
        {
            if (account == null || string.IsNullOrEmpty(account.Login) || account.Id == 0) return;
            var runner = new GitRunner(null);
            GitResult name = await runner.RunAsync(null, new[] { "config", "--global", "user.name" }, ct, null, false, 30000).ConfigureAwait(false);
            GitResult email = await runner.RunAsync(null, new[] { "config", "--global", "user.email" }, ct, null, false, 30000).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(name.StdOut))
                await runner.RunAsync(null, new[] { "config", "--global", "user.name", account.DisplayName }, ct, null, false, 30000).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(email.StdOut))
                await runner.RunAsync(null, new[] { "config", "--global", "user.email", account.CommitEmail }, ct, null, false, 30000).ConfigureAwait(false);
        }

        /// <summary>True when the repo's origin is on github.com (or the URL can't be read).</summary>
        public static async Task<bool> RepoUsesGitHubAsync(string repoRoot, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(repoRoot)) return true;
            try
            {
                GitResult url = await new GitRunner(null).RunAsync(
                    repoRoot, new[] { "remote", "get-url", "origin" }, ct, null, false, 30000).ConfigureAwait(false);
                string text = (url.StdOut ?? string.Empty).Trim();
                if (!url.Success || text.Length == 0) return true;
                return text.IndexOf(GitHubHost, StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch (Exception)
            {
                return true;
            }
        }

        /// <summary>Every repository the signed-in user can push to or read, most recently changed first.</summary>
        public static async Task<List<GitHubRepo>> ListRepositoriesAsync(CancellationToken ct)
        {
            var runner = new GitRunner(null);
            Credential cred = await FillAsync(runner, false, ct).ConfigureAwait(false);
            if (cred == null)
                throw new VaultException("You're not signed in to GitHub. Restart PartLock to sign in.");

            var repos = new List<GitHubRepo>();
            for (int page = 1; page <= 5; page++)
            {
                string json = await GetStringAsync(
                    "https://api.github.com/user/repos?per_page=100&sort=pushed&affiliation=owner,collaborator,organization_member&page=" + page,
                    cred.Password, ct).ConfigureAwait(false);
                List<GitHubRepoDto> batch = Deserialize<List<GitHubRepoDto>>(json);
                if (batch == null || batch.Count == 0) break;
                foreach (GitHubRepoDto dto in batch)
                {
                    if (dto == null || string.IsNullOrEmpty(dto.CloneUrl)) continue;
                    DateTime pushed;
                    DateTime.TryParse(dto.PushedAt, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                        out pushed);
                    repos.Add(new GitHubRepo
                    {
                        FullName = dto.FullName,
                        Name = dto.Name,
                        CloneUrl = dto.CloneUrl,
                        HtmlUrl = dto.HtmlUrl,
                        Description = dto.Description,
                        Private = dto.Private,
                        PushedAtUtc = pushed
                    });
                }
                if (batch.Count < 100) break;
            }
            return repos;
        }

        /// <summary>Forgets the saved GitHub sign-in on this computer (the next start asks again).</summary>
        public static async Task SignOutAsync(CancellationToken ct)
        {
            var runner = new GitRunner(null);
            Credential cred = await FillAsync(runner, false, ct).ConfigureAwait(false);
            if (cred == null) cred = new Credential();
            await RejectAsync(runner, cred, ct).ConfigureAwait(false);
        }

        /// <summary>The URL of the repo's "origin" remote, or null.</summary>
        public static async Task<string> GetOriginUrlAsync(string repoRoot, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(repoRoot)) return null;
            try
            {
                GitResult url = await new GitRunner(null).RunAsync(
                    repoRoot, new[] { "remote", "get-url", "origin" }, ct, null, false, 30000).ConfigureAwait(false);
                string text = (url.StdOut ?? string.Empty).Trim();
                return url.Success && text.Length > 0 ? GitRunner.Redact(text) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>"owner/name" for a github.com remote URL (https or ssh), otherwise null.</summary>
        public static string ParseGitHubRepo(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            Match m = Regex.Match(url.Trim(), @"github\.com[:/]+([^/\s]+)/([^/\s]+?)(\.git)?/?$", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value + "/" + m.Groups[2].Value : null;
        }

        /// <summary>Downloads a small image (e.g. an avatar). Null on any failure.</summary>
        public static async Task<byte[]> DownloadBytesAsync(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            try
            {
                using (var client = new WebClient())
                {
                    client.Headers[HttpRequestHeader.UserAgent] = UserAgent;
                    return await client.DownloadDataTaskAsync(new Uri(url)).ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static async Task EnsureCredentialHelperAsync(GitRunner runner, IProgress<string> log, CancellationToken ct)
        {
            GitResult helper = await runner.RunAsync(
                null, new[] { "config", "--get-all", "credential.helper" }, ct, null, false, 30000).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(helper.StdOut)) return;

            Report(log, "Turning on Git Credential Manager…");
            await runner.RunAsync(null, new[] { "config", "--global", "credential.helper", "manager" }, ct, null, false, 30000)
                .ConfigureAwait(false);
        }

        private sealed class Credential
        {
            public string UserName;
            public string Password;

            public string ToInput()
            {
                var sb = new StringBuilder();
                sb.Append("protocol=https\n");
                sb.Append("host=").Append(GitHubHost).Append('\n');
                if (!string.IsNullOrEmpty(UserName)) sb.Append("username=").Append(UserName).Append('\n');
                if (!string.IsNullOrEmpty(Password)) sb.Append("password=").Append(Password).Append('\n');
                sb.Append('\n');
                return sb.ToString();
            }
        }

        private static async Task<Credential> FillAsync(GitRunner runner, bool interactive, CancellationToken ct)
        {
            var args = new List<string>();
            if (!interactive)
            {
                // Git Credential Manager: answer from the store only, never pop up a window.
                args.Add("-c");
                args.Add("credential.interactive=false");
            }
            args.Add("credential");
            args.Add("fill");

            string input = "protocol=https\nhost=" + GitHubHost + "\n\n";
            GitResult result;
            try
            {
                result = await runner.RunAsync(null, args, ct, input, false,
                    interactive ? 900000 : 60000).ConfigureAwait(false);
            }
            catch (VaultException)
            {
                return null; // timed out
            }
            if (!result.Success) return null;

            var cred = new Credential();
            foreach (string raw in (result.StdOut ?? string.Empty).Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq);
                string value = line.Substring(eq + 1);
                if (key == "username") cred.UserName = value;
                else if (key == "password") cred.Password = value;
            }
            return string.IsNullOrEmpty(cred.Password) ? null : cred;
        }

        private static async Task RejectAsync(GitRunner runner, Credential cred, CancellationToken ct)
        {
            try
            {
                await runner.RunAsync(null, new[] { "credential", "reject" }, ct, cred.ToInput(), false, 30000)
                    .ConfigureAwait(false);
            }
            catch (Exception) { }
        }

        /// <summary>The account behind a token, or null when GitHub says 401 (bad / expired token).</summary>
        private static async Task<GitHubAccount> GetAccountAsync(string token, CancellationToken ct)
        {
            string json;
            try
            {
                json = await GetStringAsync("https://api.github.com/user", token, ct).ConfigureAwait(false);
            }
            catch (WebException ex)
            {
                var response = ex.Response as HttpWebResponse;
                if (response != null && response.StatusCode == HttpStatusCode.Unauthorized) return null;
                throw;
            }

            GitHubUserDto dto = Deserialize<GitHubUserDto>(json);
            if (dto == null || string.IsNullOrEmpty(dto.Login)) return null;
            return new GitHubAccount
            {
                Login = dto.Login, Name = dto.Name, Id = dto.Id, Email = dto.Email, AvatarUrl = dto.AvatarUrl
            };
        }

        // ------------------------------------------------------------------
        // Small helpers
        // ------------------------------------------------------------------

        private static async Task<string> GetStringAsync(string url, string token, CancellationToken ct)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = UserAgent;
            request.Accept = "application/vnd.github+json";
            request.Timeout = 30000;
            if (!string.IsNullOrEmpty(token)) request.Headers[HttpRequestHeader.Authorization] = "token " + token;

            using (ct.Register(request.Abort))
            {
                try
                {
                    using (WebResponse response = await request.GetResponseAsync().ConfigureAwait(false))
                    using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                    {
                        return await reader.ReadToEndAsync().ConfigureAwait(false);
                    }
                }
                catch (WebException) when (ct.IsCancellationRequested)
                {
                    throw new OperationCanceledException(ct);
                }
            }
        }

        private static T Deserialize<T>(string json) where T : class
        {
            if (string.IsNullOrEmpty(json)) return null;
            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                return serializer.ReadObject(stream) as T;
            }
        }

        private static Task<Tuple<int, string>> RunProcessAsync(ProcessStartInfo psi, CancellationToken ct, TimeSpan timeout)
        {
            return Task.Run(() =>
            {
                using (var process = Process.Start(psi))
                {
                    if (process == null) throw new Win32Exception("The program didn't start.");
                    Task<string> stdout = psi.RedirectStandardOutput ? process.StandardOutput.ReadToEndAsync() : Task.FromResult(string.Empty);
                    Task<string> stderr = psi.RedirectStandardError ? process.StandardError.ReadToEndAsync() : Task.FromResult(string.Empty);
                    DateTime deadline = DateTime.UtcNow + timeout;
                    while (!process.WaitForExit(500))
                    {
                        if (ct.IsCancellationRequested || DateTime.UtcNow > deadline)
                        {
                            try { process.Kill(); } catch (Exception) { }
                            ct.ThrowIfCancellationRequested();
                            throw new VaultException("The Git installer took too long.");
                        }
                    }
                    process.WaitForExit();
                    string text = string.Empty;
                    try { text = stdout.Result + stderr.Result; } catch (Exception) { }
                    return Tuple.Create(process.ExitCode, text);
                }
            }, ct);
        }

        private static void Report(IProgress<string> log, string line)
        {
            if (log == null) return;
            try { log.Report(line); } catch (Exception) { }
        }
    }
}
