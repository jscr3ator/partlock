// PartLock - the real git-backed vault: setup, repo housekeeping and shared helpers.
// File list lives in VaultService.Part2.cs, user actions in VaultService.Part3.cs.
//
// All repo paths coming in or going out are repo-relative and use forward slashes.
// Commands that take paths put them after "--" so a file called "-f" cannot be
// mistaken for a flag.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PartLock.Core
{
    public partial class VaultService : IVaultService
    {
        private const string GitMissingMessage =
            "Git for Windows is not installed. Download it from https://git-scm.com/download/win " +
            "(it includes Git LFS), then restart PartLock.";

        private const string LfsMissingMessage =
            "Git LFS is not installed. Reinstall Git for Windows from https://git-scm.com/download/win " +
            "(it includes Git LFS), then restart PartLock.";

        internal static readonly string[] LockableExtensions = new string[]
        {
            "sldprt", "sldasm", "slddrw", "step", "stp", "iges", "igs",
            "stl", "x_t", "dxf", "dwg", "pdf", "3mf"
        };

        private readonly string _repoRoot;
        private readonly GitRunner _git;

        private IProgress<string> _log;
        // Only touched from one logical operation at a time (the UI serializes actions).
        private List<LockInfo> _locks = new List<LockInfo>();
        private HashSet<string> _behindRemote = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private string _serverUserName;
        private bool _repoReady;

        public VaultService(string repoRoot, IProgress<string> log = null)
        {
            if (string.IsNullOrWhiteSpace(repoRoot))
                throw new ArgumentException("A repository folder is required.", "repoRoot");

            _repoRoot = Path.GetFullPath(repoRoot);
            _log = log;
            _git = new GitRunner(new DelegateProgress(LogLine));
        }

        public string RepoRoot { get { return _repoRoot; } }

        public IProgress<string> Log
        {
            get { return _log; }
            set { _log = value; }
        }

        public string ServerUserName { get { return _serverUserName; } }

        // ------------------------------------------------------------------
        // Environment / repo discovery helpers
        // ------------------------------------------------------------------

        public static async Task<EnvironmentCheck> CheckEnvironmentAsync()
        {
            var check = new EnvironmentCheck();
            var runner = new GitRunner(null);

            try
            {
                GitResult version = await runner.RunAsync(
                    null, new[] { "--version" }, CancellationToken.None, null, true, 30000).ConfigureAwait(false);
                check.GitInstalled = true;
                check.GitVersion = (version.StdOut ?? string.Empty).Trim();
            }
            catch (Exception)
            {
                check.Problem = GitMissingMessage;
                return check;
            }

            try
            {
                GitResult version = await runner.RunAsync(
                    null, new[] { "lfs", "version" }, CancellationToken.None, null, true, 30000).ConfigureAwait(false);
                check.LfsInstalled = true;
                check.LfsVersion = (version.StdOut ?? string.Empty).Trim();
            }
            catch (Exception)
            {
                check.Problem = LfsMissingMessage;
            }

            return check;
        }

        public static string FindRepoRoot(string anyPath)
        {
            if (string.IsNullOrWhiteSpace(anyPath)) return null;

            string dir;
            try
            {
                string full = Path.GetFullPath(anyPath);
                dir = File.Exists(full) ? Path.GetDirectoryName(full) : full;
            }
            catch (Exception)
            {
                return null;
            }

            while (!string.IsNullOrEmpty(dir))
            {
                string dotGit = Path.Combine(dir, ".git");
                if (Directory.Exists(dotGit) || File.Exists(dotGit)) return dir;

                string parent = Path.GetDirectoryName(dir);
                if (string.IsNullOrEmpty(parent) ||
                    string.Equals(parent, dir, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                dir = parent;
            }

            return null;
        }

        public static async Task CloneAsync(
            string url, string targetDir, IProgress<string> log, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(url))
                throw new VaultException("Enter the address of the team's repository.");
            if (string.IsNullOrWhiteSpace(targetDir))
                throw new VaultException("Choose a folder to download the vault into.");

            string repo = Path.GetFullPath(targetDir);
            if (Directory.Exists(repo) && Directory.GetFileSystemEntries(repo).Length > 0)
                throw new VaultException("That folder isn't empty. Pick a new, empty folder for the vault.");

            var runner = new GitRunner(log);
            try
            {
                Report(log, "Step 1 of 3: Setting up Git LFS…");
                await runner.RunAsync(
                    null, new[] { "lfs", "install", "--skip-repo" }, ct, null, false).ConfigureAwait(false);

                Report(log, "Step 2 of 3: Downloading the vault (this can take a few minutes the first time)…");
                await runner.RunAsync(null, new[] { "clone", url.Trim(), repo }, ct, null, true, 3600000).ConfigureAwait(false);

                Report(log, "Step 3 of 3: Downloading CAD files and turning on check-out locking…");
                await PrepareRepoAsync(runner, repo, log, ct).ConfigureAwait(false);
                await runner.RunAsync(repo, new[] { "lfs", "pull" }, ct, null, true, 3600000).ConfigureAwait(false);
                Report(log, "Done. The vault is ready.");
            }
            catch (Win32Exception ex)
            {
                throw new VaultException(GitMissingMessage, ex.Message, ex);
            }
        }

        /// <summary>
        /// Per-clone settings that make locking actually enforced on this computer:
        /// the Git LFS hooks (the pre-push hook is what refuses to upload files someone else
        /// has checked out; GitHub itself does not block it) and strict lock verification.
        /// </summary>
        private static async Task PrepareRepoAsync(GitRunner runner, string repo, IProgress<string> log, CancellationToken ct)
        {
            GitResult install = await runner.RunAsync(
                repo, new[] { "lfs", "install", "--local" }, ct, null, false).ConfigureAwait(false);
            if (install.ExitCode != 0)
            {
                // Usually an existing non-LFS hook. --force keeps locking working; log so an admin can see it.
                Report(log, "Git LFS hooks were not installed cleanly; retrying with --force.");
                await runner.RunAsync(repo, new[] { "lfs", "install", "--local", "--force" }, ct, null, false).ConfigureAwait(false);
            }
            await runner.RunAsync(repo, new[] { "config", "lfs.locksverify", "true" }, ct, null, false).ConfigureAwait(false);
            await runner.RunAsync(repo, new[] { "config", "lfs.setlockablereadonly", "true" }, ct, null, false).ConfigureAwait(false);
        }

        /// <summary>Runs once per session before the first file listing, and before every update.</summary>
        private async Task EnsureRepoReadyAsync(CancellationToken ct)
        {
            if (!_repoReady)
            {
                await PrepareRepoAsync(_git, _repoRoot, _log, ct).ConfigureAwait(false);
                _repoReady = true;
            }
            await RecoverInterruptedUpdateAsync(ct).ConfigureAwait(false);
        }

        /// <summary>
        /// If an earlier Get Latest / Check In was cut off mid-rebase (power cut, killed app), the
        /// working copy is stuck. Abort the rebase: autostash puts the user's edits back.
        /// </summary>
        private async Task RecoverInterruptedUpdateAsync(CancellationToken ct)
        {
            foreach (string marker in new[] { "rebase-merge", "rebase-apply" })
            {
                GitResult where = await GitAsync(new[] { "rev-parse", "--git-path", marker }, ct, null, false).ConfigureAwait(false);
                if (where.ExitCode != 0) continue;
                string path = where.StdOut.Trim();
                if (path.Length == 0) continue;
                if (!Path.IsPathRooted(path)) path = Path.Combine(_repoRoot, path);
                if (!Directory.Exists(path)) continue;

                LogLine("An earlier update was interrupted. Putting things back the way they were…");
                await GitAsync(new[] { "rebase", "--abort" }, ct, null, false).ConfigureAwait(false);
                return;
            }
        }

        public static bool IsFileOpenElsewhere(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath)) return false;

            try
            {
                using (new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    return false;
                }
            }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (IOException) { return true; }
        }

        // ------------------------------------------------------------------
        // Locking configuration (one-time, team lead)
        // ------------------------------------------------------------------

        public Task<bool> IsLockingConfiguredAsync(CancellationToken ct = default(CancellationToken))
        {
            ct.ThrowIfCancellationRequested();
            string path = Path.Combine(_repoRoot, ".gitattributes");
            if (!File.Exists(path)) return Task.FromResult(false);

            string text;
            try { text = ReadAllText(path); }
            catch (IOException) { return Task.FromResult(false); }
            catch (UnauthorizedAccessException) { return Task.FromResult(false); }

            foreach (string line in SplitLines(text))
            {
                if (line.IndexOf("lockable", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (line.IndexOf("sldprt", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    line.IndexOf("[sS][lL][dD][pP][rR][tT]", StringComparison.Ordinal) >= 0)
                {
                    return Task.FromResult(true);
                }
            }
            return Task.FromResult(false);
        }

        /// <summary>
        /// Adopts an existing repo without touching its history: one normal commit that adds the
        /// locking rules and re-stores the current CAD files in Git LFS. Every old commit stays as is.
        /// </summary>
        public async Task EnableLockingAsync(CancellationToken ct = default(CancellationToken))
        {
            ct.ThrowIfCancellationRequested();
            await EnsureRepoReadyAsync(ct).ConfigureAwait(false);

            LogLine("Step 1 of 5: Checking this folder is clean and connected to the server…");
            await EnsureUpstreamAsync(ct).ConfigureAwait(false);

            GitResult status = await GitAsync(
                new[] { "status", "--porcelain=v1", "-z", "--untracked-files=no" }, ct).ConfigureAwait(false);
            if (SplitNul(status.StdOut).Count > 0)
            {
                throw new VaultException(
                    "You have changes that aren't checked in yet. Check them in (or undo them) first, " +
                    "so the locking setup commit contains only the setup.",
                    status.StdOut.Replace('\0', '\n'));
            }

            LogLine("Step 2 of 5: Getting the latest version from the server…");
            await GitAsync(new[] { "pull", "--rebase" }, ct, null, true, 3600000).ConfigureAwait(false);

            LogLine("Step 3 of 5: Writing the locking rules (.gitattributes, .gitignore)…");
            var attributeLines = new List<string>();
            foreach (string extension in LockableExtensions)
                attributeLines.Add(MakeLockablePattern(extension) + " filter=lfs diff=lfs merge=lfs -text lockable");

            AppendMissingLines(Path.Combine(_repoRoot, ".gitattributes"), attributeLines);
            AppendMissingLines(Path.Combine(_repoRoot, ".gitignore"), new[] { "~$*", "*.tmp" });

            LogLine("Step 4 of 5: Storing the current CAD files in Git LFS (history is not changed)…");
            await GitAsync(new[] { "add", "--", ".gitattributes", ".gitignore" }, ct).ConfigureAwait(false);
            await GitAsync(new[] { "add", "--renormalize", "--", "." }, ct, null, true, 3600000).ConfigureAwait(false);

            GitResult staged = await GitAsync(new[] { "diff", "--cached", "--quiet" }, ct, null, false).ConfigureAwait(false);
            if (staged.ExitCode == 1)
            {
                await GitAsync(new[] { "commit", "-m", "Enable PartLock file locking (Git LFS)" }, ct).ConfigureAwait(false);
            }
            else if (staged.ExitCode != 0)
            {
                throw new VaultException(GitRunner.FriendlyError(staged.StdErr), staged.StdErr);
            }
            else
            {
                LogLine("Locking was already set up; nothing new to save.");
            }

            LogLine("Step 5 of 5: Uploading to the server…");
            await PushWithRetryAsync(ct).ConfigureAwait(false);

            // Files are writable from before the setup; make the vault behave like everyone else's.
            await GetFilesAsync(false, ct).ConfigureAwait(false);
            LogLine("Locking is on. Teammates get it automatically next time they Get Latest.");
        }

        // ------------------------------------------------------------------
        // git plumbing helpers
        // ------------------------------------------------------------------

        private async Task<GitResult> GitAsync(
            IEnumerable<string> args,
            CancellationToken ct,
            string stdin = null,
            bool throwOnError = true,
            int timeoutMs = 600000)
        {
            try
            {
                return await _git.RunAsync(_repoRoot, args, ct, stdin, throwOnError, timeoutMs).ConfigureAwait(false);
            }
            catch (Win32Exception ex)
            {
                throw new VaultException(GitMissingMessage, ex.Message, ex);
            }
        }

        private async Task<string> GetUpstreamAsync(CancellationToken ct)
        {
            GitResult result = await GitAsync(
                new[] { "rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}" }, ct, null, false).ConfigureAwait(false);
            if (result.ExitCode != 0) return null;
            string name = (result.StdOut ?? string.Empty).Trim();
            return name.Length == 0 ? null : name;
        }

        private async Task EnsureUpstreamAsync(CancellationToken ct)
        {
            if (await GetUpstreamAsync(ct).ConfigureAwait(false) == null)
            {
                throw new VaultException(
                    "This folder isn't connected to a branch on the server yet. " +
                    "Ask your team lead to push the main branch first, then try again.");
            }
        }

        private async Task<bool> HasUpstreamAsync(CancellationToken ct)
        {
            return await GetUpstreamAsync(ct).ConfigureAwait(false) != null;
        }

        // ------------------------------------------------------------------
        // Small utilities
        // ------------------------------------------------------------------

        private static string DescribeLock(LockInfo info)
        {
            if (info == null) return "by someone";
            string owner = string.IsNullOrEmpty(info.Owner) ? "someone" : info.Owner;
            return "by " + owner + " since " + FormatDate(info.LockedAtUtc);
        }

        private static string FormatDate(DateTime utc)
        {
            if (utc == DateTime.MinValue) return "an unknown time";
            return utc.ToLocalTime().ToString("d MMM yyyy HH:mm");
        }

        private static string Files(int count)
        {
            return count + (count == 1 ? " file" : " files");
        }

        private static string Norm(string path)
        {
            if (path == null) return string.Empty;
            string result = path.Replace('\\', '/');
            while (result.StartsWith("./", StringComparison.Ordinal)) result = result.Substring(2);
            return result;
        }

        private string FullPath(string repoRelative)
        {
            string relative = Norm(repoRelative).TrimStart('/');
            return Path.Combine(_repoRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        }

        private static List<string> SplitNul(string text)
        {
            var parts = new List<string>();
            if (string.IsNullOrEmpty(text)) return parts;
            foreach (string part in text.Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (part.Trim().Length > 0) parts.Add(part);
            }
            return parts;
        }

        private static string[] SplitLines(string text)
        {
            if (string.IsNullOrEmpty(text)) return new string[0];
            return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        }

        private static string MakeLockablePattern(string extension)
        {
            var sb = new StringBuilder("*.");
            foreach (char c in extension)
            {
                if (char.IsLetter(c))
                    sb.Append('[').Append(char.ToLowerInvariant(c)).Append(char.ToUpperInvariant(c)).Append(']');
                else
                    sb.Append(c);
            }
            return sb.ToString();
        }

        private static string ReadAllText(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream, new UTF8Encoding(false), true))
            {
                return reader.ReadToEnd();
            }
        }

        private static void AppendMissingLines(string path, IList<string> lines)
        {
            string existing = File.Exists(path) ? ReadAllText(path) : string.Empty;

            var present = new HashSet<string>(StringComparer.Ordinal);
            foreach (string line in SplitLines(existing)) present.Add(line.Trim());

            var sb = new StringBuilder(existing);
            bool changed = false;
            foreach (string line in lines)
            {
                if (string.IsNullOrEmpty(line) || present.Contains(line)) continue;
                if (sb.Length > 0 && sb[sb.Length - 1] != '\n') sb.Append('\n');
                sb.Append(line).Append('\n');
                present.Add(line);
                changed = true;
            }
            if (!changed) return;

            // .gitattributes may be read-only if a previous tool marked it; make sure we can write.
            if (File.Exists(path)) File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        private void LogLine(string line)
        {
            Report(_log, line);
        }

        private static void Report(IProgress<string> log, string line)
        {
            if (log == null || line == null) return;
            try { log.Report(line); }
            catch (Exception) { }
        }

        private sealed class DelegateProgress : IProgress<string>
        {
            private readonly Action<string> _action;
            public DelegateProgress(Action<string> action) { _action = action; }
            public void Report(string value) { _action(value); }
        }
    }
}
