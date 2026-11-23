// PartLock - the real git-backed vault.
//
// All repo paths coming in or going out are repo-relative and use forward slashes.
// Commands that take paths put them after "--" so a file called "-f" cannot be
// mistaken for a flag.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
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

        private static readonly string[] LockableExtensions = new string[]
        {
            "sldprt", "sldasm", "slddrw", "step", "stp", "iges", "igs",
            "stl", "x_t", "dxf", "dwg", "pdf", "3mf"
        };

        private readonly string _repoRoot;
        private readonly GitRunner _git;
        private readonly object _cacheLock = new object();

        private IProgress<string> _log;
        private List<LockInfo> _locks = new List<LockInfo>();
        private HashSet<string> _behindRemote = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private string _serverUserName;

        public VaultService(string repoRoot, IProgress<string> log = null)
        {
            if (string.IsNullOrWhiteSpace(repoRoot))
                throw new ArgumentException("A repository folder is required.", "repoRoot");

            _repoRoot = Path.GetFullPath(repoRoot);
            _log = log;
            _git = new GitRunner(new DelegateProgress(LogLine));
        }

        public string RepoRoot
        {
            get { return _repoRoot; }
        }

        public IProgress<string> Log
        {
            get { return _log; }
            set { _log = value; }
        }

        public string ServerUserName
        {
            get
            {
                lock (_cacheLock) { return _serverUserName; }
            }
        }

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
                    null, new[] { "--version" }, CancellationToken.None).ConfigureAwait(false);
                check.GitInstalled = true;
                check.GitVersion = (version.StdOut ?? string.Empty).Trim();
            }
            catch (Win32Exception)
            {
                check.GitInstalled = false;
                check.Problem = GitMissingMessage;
                return check;
            }
            catch (VaultException)
            {
                check.GitInstalled = false;
                check.Problem = GitMissingMessage;
                return check;
            }

            try
            {
                GitResult version = await runner.RunAsync(
                    null, new[] { "lfs", "version" }, CancellationToken.None).ConfigureAwait(false);
                check.LfsInstalled = true;
                check.LfsVersion = (version.StdOut ?? string.Empty).Trim();
            }
            catch (Win32Exception)
            {
                check.LfsInstalled = false;
            }
            catch (VaultException)
            {
                check.LfsInstalled = false;
            }

            if (!check.LfsInstalled)
                check.Problem = LfsMissingMessage;

            return check;
        }

        public static string FindRepoRoot(string anyPath)
        {
            if (string.IsNullOrWhiteSpace(anyPath)) return null;

            string dir;
            try
            {
                string full = Path.GetFullPath(anyPath);
                if (Directory.Exists(full))
                    dir = full;
                else if (File.Exists(full))
                    dir = Path.GetDirectoryName(full);
                else
                    dir = Path.HasExtension(full) ? Path.GetDirectoryName(full) : full;

                if (string.IsNullOrEmpty(dir)) dir = Path.GetPathRoot(full);
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
                throw new VaultException("Choose a folder to clone the repository into.");

            var runner = new GitRunner(log);
            try
            {
                Report(log, "Setting up Git LFS…");
                await runner.RunAsync(
                    null, new[] { "lfs", "install", "--skip-repo" }, ct, null, false).ConfigureAwait(false);

                Report(log, "Cloning the repository…");
                await runner.RunAsync(null, new[] { "clone", url, targetDir }, ct).ConfigureAwait(false);

                string repo = Path.GetFullPath(targetDir);
                await runner.RunAsync(
                    repo, new[] { "config", "lfs.locksverify", "true" }, ct).ConfigureAwait(false);

                Report(log, "Downloading CAD files…");
                await runner.RunAsync(repo, new[] { "lfs", "pull" }, ct).ConfigureAwait(false);
            }
            catch (Win32Exception ex)
            {
                throw new VaultException(GitMissingMessage, ex.Message, ex);
            }
        }

        public static bool IsFileOpenElsewhere(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath)) return false;

            try
            {
                using (var stream = new FileStream(
                    fullPath, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    return false;
                }
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (IOException)
            {
                return true;
            }
        }

        // ------------------------------------------------------------------
        // Locking configuration
        // ------------------------------------------------------------------

        public async Task<bool> IsLockingConfiguredAsync(
            CancellationToken ct = default(CancellationToken))
        {
            ct.ThrowIfCancellationRequested();

            string path = Path.Combine(_repoRoot, ".gitattributes");
            if (!File.Exists(path)) return false;

            string text;
            try
            {
                text = await ReadAllTextAsync(path).ConfigureAwait(false);
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }

            foreach (string line in SplitLines(text))
            {
                if (line.IndexOf("lockable", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                // Accept both a plain "*.sldprt" rule and the letter-class form that
                // EnableLockingAsync writes: *.[sS][lL][dD][pP][rR][tT].
                if (line.IndexOf("sldprt", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    line.IndexOf("[sS][lL][dD][pP][rR][tT]", StringComparison.Ordinal) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        public async Task EnableLockingAsync(CancellationToken ct = default(CancellationToken))
        {
            ct.ThrowIfCancellationRequested();
            LogLine("Setting up file locking for CAD files…");

            var attributeLines = new List<string>();
            foreach (string extension in LockableExtensions)
            {
                attributeLines.Add(
                    MakeLockablePattern(extension) + " filter=lfs diff=lfs merge=lfs -text lockable");
            }

            await AppendMissingLinesAsync(
                Path.Combine(_repoRoot, ".gitattributes"), attributeLines, ct).ConfigureAwait(false);

            await AppendMissingLinesAsync(
                Path.Combine(_repoRoot, ".gitignore"),
                new[] { "~$*", "*.tmp" }, ct).ConfigureAwait(false);

            LogLine("Telling Git LFS to lock CAD files…");
            await RunGitAsync(new[] { "lfs", "install", "--local" }, ct).ConfigureAwait(false);
            await RunGitAsync(new[] { "config", "lfs.locksverify", "true" }, ct).ConfigureAwait(false);

            await RunGitAsync(new[] { "add", ".gitattributes", ".gitignore" }, ct).ConfigureAwait(false);
            await RunGitAsync(new[] { "add", "--renormalize", "." }, ct).ConfigureAwait(false);

            GitResult staged = await RunGitAsync(
                new[] { "diff", "--cached", "--quiet" }, ct, null, false).ConfigureAwait(false);

            if (staged.ExitCode == 1)
            {
                LogLine("Saving the locking setup…");
                await RunGitAsync(
                    new[] { "commit", "-m", "Enable PartLock file locking (Git LFS)" }, ct).ConfigureAwait(false);
            }
            else if (staged.ExitCode != 0)
            {
                throw ErrorFrom(staged);
            }

            string upstream = await GetUpstreamAsync(ct).ConfigureAwait(false);
            if (upstream == null)
            {
                throw new VaultException(
                    "This branch isn't connected to the server yet. Publish it first " +
                    "(for example, git push -u origin HEAD), then run Enable Locking again.");
            }

            LogLine("Uploading the locking setup…");
            await RunGitAsync(new[] { "push" }, ct).ConfigureAwait(false);
        }

        // ------------------------------------------------------------------
        // git plumbing helpers
        // ------------------------------------------------------------------

        private async Task<GitResult> RunGitAsync(
            IEnumerable<string> args,
            CancellationToken ct,
            string stdin = null,
            bool throwOnError = true,
            int timeoutMs = 600000)
        {
            try
            {
                return await _git.RunAsync(
                    _repoRoot, args, ct, stdin, throwOnError, timeoutMs).ConfigureAwait(false);
            }
            catch (Win32Exception ex)
            {
                throw new VaultException(GitMissingMessage, ex.Message, ex);
            }
        }

        /// <summary>Short alias used by the file-list and action halves of the class.</summary>
        private Task<GitResult> GitAsync(
            IEnumerable<string> args,
            CancellationToken ct,
            string stdin = null,
            bool throwOnError = true)
        {
            return RunGitAsync(args, ct, stdin, throwOnError);
        }

        private async Task<List<string>> GetTrackedFilesAsync(CancellationToken ct)
        {
            GitResult result = await RunGitAsync(
                new[] { "ls-files", "-z" }, ct).ConfigureAwait(false);
            return SplitNul(result.StdOut);
        }

        private async Task<List<string>> GetUntrackedFilesAsync(CancellationToken ct)
        {
            GitResult result = await RunGitAsync(
                new[] { "ls-files", "-z", "--others", "--exclude-standard" }, ct).ConfigureAwait(false);
            return SplitNul(result.StdOut);
        }

        private async Task<HashSet<string>> GetTrackedSetAsync(CancellationToken ct)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in await GetTrackedFilesAsync(ct).ConfigureAwait(false))
            {
                string normalized = NormalizePath(path);
                if (!string.IsNullOrEmpty(normalized)) set.Add(normalized);
            }
            return set;
        }

        private async Task<HashSet<string>> GetLockableAsync(
            List<string> candidates, CancellationToken ct)
        {
            var lockable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (candidates == null || candidates.Count == 0) return lockable;

            var input = new StringBuilder();
            foreach (string path in candidates)
                input.Append(path).Append('\0');

            GitResult result = await RunGitAsync(
                new[] { "check-attr", "--stdin", "-z", "lockable" },
                ct,
                input.ToString(),
                false).ConfigureAwait(false);

            if (result.ExitCode == 0)
            {
                List<string> parts = SplitNul(result.StdOut);
                for (int i = 0; i + 2 < parts.Count; i += 3)
                {
                    string path = NormalizePath(parts[i]);
                    string attribute = parts[i + 1];
                    string value = parts[i + 2];

                    if (string.Equals(attribute, "lockable", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(value, "set", StringComparison.OrdinalIgnoreCase))
                    {
                        lockable.Add(path);
                    }
                }
                return lockable;
            }

            LogLine("Couldn't read the lockable-file rules; using the CAD file extensions instead. " +
                    GitRunner.FriendlyError(result.StdErr));

            foreach (string path in candidates)
            {
                if (HasLockableExtension(path)) lockable.Add(path);
            }
            return lockable;
        }

        private async Task<string> GetUpstreamAsync(CancellationToken ct)
        {
            GitResult result = await RunGitAsync(
                new[] { "rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}" },
                ct,
                null,
                false).ConfigureAwait(false);

            if (result.ExitCode != 0) return null;

            string name = (result.StdOut ?? string.Empty).Trim();
            return name.Length == 0 ? null : name;
        }

        private async Task EnsureUpstreamAsync(CancellationToken ct)
        {
            string upstream = await GetUpstreamAsync(ct).ConfigureAwait(false);
            if (upstream == null)
            {
                throw new VaultException(
                    "This folder isn't connected to a server branch yet. Publish the branch first, then try again.");
            }
        }

        private async Task<bool> HasUpstreamAsync(CancellationToken ct)
        {
            string upstream = await GetUpstreamAsync(ct).ConfigureAwait(false);
            return upstream != null;
        }

        private async Task<HashSet<string>> ComputeBehindAsync(CancellationToken ct)
        {
            var behind = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string upstream = await GetUpstreamAsync(ct).ConfigureAwait(false);
            if (upstream == null) return behind;

            GitResult diff = await RunGitAsync(
                new[] { "diff", "--name-only", "-z", "HEAD...@{u}" },
                ct,
                null,
                false).ConfigureAwait(false);

            if (diff.ExitCode != 0) return null;

            foreach (string raw in SplitNul(diff.StdOut))
            {
                string path = NormalizePath(raw);
                if (!string.IsNullOrEmpty(path)) behind.Add(path);
            }
            return behind;
        }

        private void SetLocks(List<LockInfo> locks)
        {
            var clean = new List<LockInfo>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (locks != null)
            {
                foreach (LockInfo item in locks)
                {
                    if (item == null || string.IsNullOrEmpty(item.Path)) continue;
                    item.Path = NormalizePath(item.Path);
                    if (seen.Add(item.Path)) clean.Add(item);
                }
            }

            lock (_cacheLock) { _locks = clean; }
        }

        private List<LockInfo> SnapshotLocks()
        {
            lock (_cacheLock) { return new List<LockInfo>(_locks); }
        }

        private Dictionary<string, LockInfo> GetLockMap()
        {
            var map = new Dictionary<string, LockInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (LockInfo item in SnapshotLocks())
            {
                if (item != null && !string.IsNullOrEmpty(item.Path))
                    map[NormalizePath(item.Path)] = item;
            }
            return map;
        }

        private void SetServerUserName(string name)
        {
            lock (_cacheLock) { _serverUserName = name; }
        }

        private void AddCachedLock(string path, string id)
        {
            path = NormalizePath(path);
            lock (_cacheLock)
            {
                var list = new List<LockInfo>(_locks);
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    if (string.Equals(list[i].Path, path, StringComparison.OrdinalIgnoreCase))
                        list.RemoveAt(i);
                }

                var info = new LockInfo();
                info.Id = id;
                info.Path = path;
                info.Owner = _serverUserName;
                info.LockedAtUtc = DateTime.UtcNow;
                info.IsMine = true;
                list.Add(info);
                _locks = list;
            }
        }

        private void RemoveCachedLock(string path)
        {
            path = NormalizePath(path);
            lock (_cacheLock)
            {
                var list = new List<LockInfo>();
                foreach (LockInfo item in _locks)
                {
                    if (!string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase))
                        list.Add(item);
                }
                _locks = list;
            }
        }

        private HashSet<string> SnapshotBehind()
        {
            lock (_cacheLock)
            {
                return new HashSet<string>(_behindRemote, StringComparer.OrdinalIgnoreCase);
            }
        }

        // ------------------------------------------------------------------
        // Push / unlock helpers
        // ------------------------------------------------------------------

        private async Task TryUnlockAsync(
            string path, LockInfo info, bool force, CancellationToken ct)
        {
            var byPathArgs = new List<string> { "lfs", "unlock" };
            if (force) byPathArgs.Add("--force");
            byPathArgs.Add("--");
            byPathArgs.Add(path);

            GitResult byPath = await RunGitAsync(
                byPathArgs, ct, null, false).ConfigureAwait(false);

            if (byPath.ExitCode == 0)
            {
                RemoveCachedLock(path);
                return;
            }

            string id = info != null ? info.Id : null;
            if (!string.IsNullOrEmpty(id))
            {
                var byIdArgs = new List<string> { "lfs", "unlock" };
                if (force) byIdArgs.Add("--force");
                byIdArgs.Add("--id=" + id);

                GitResult byId = await RunGitAsync(
                    byIdArgs, ct, null, false).ConfigureAwait(false);

                if (byId.ExitCode == 0)
                {
                    RemoveCachedLock(path);
                    return;
                }

                LogLine("Couldn't release the check-out on " + path + ". " +
                        GitRunner.FriendlyError(byId.StdErr));
                return;
            }

            LogLine("Couldn't release the check-out on " + path + ". " +
                    GitRunner.FriendlyError(byPath.StdErr));
        }

        private async Task RollbackLocksAsync(
            List<string> paths, List<string> ids, CancellationToken ct)
        {
            for (int i = paths.Count - 1; i >= 0; i--)
            {
                string path = paths[i];
                string id = i < ids.Count ? ids[i] : null;

                try
                {
                    if (!string.IsNullOrEmpty(id))
                    {
                        await RunGitAsync(
                            new[] { "lfs", "unlock", "--force", "--id=" + id },
                            ct,
                            null,
                            false).ConfigureAwait(false);
                    }
                    else
                    {
                        await RunGitAsync(
                            new[] { "lfs", "unlock", "--force", "--", path },
                            ct,
                            null,
                            false).ConfigureAwait(false);
                    }
                }
                catch (Exception)
                {
                    // Best effort only: the original lock failure is what the user needs to see.
                }
            }
        }

        private async Task AddPathsAsync(List<string> paths, CancellationToken ct)
        {
            for (int i = 0; i < paths.Count; i += 50)
            {
                var args = new List<string> { "add", "-A", "--" };
                for (int j = i; j < paths.Count && j < i + 50; j++)
                    args.Add(paths[j]);

                await RunGitAsync(args, ct).ConfigureAwait(false);
            }
        }

        private async Task RestorePathsAsync(List<string> paths, CancellationToken ct)
        {
            for (int i = 0; i < paths.Count; i += 50)
            {
                var args = new List<string>
                {
                    "restore", "--source=HEAD", "--staged", "--worktree", "--"
                };
                for (int j = i; j < paths.Count && j < i + 50; j++)
                    args.Add(paths[j]);

                await RunGitAsync(args, ct).ConfigureAwait(false);
            }
        }

        // ------------------------------------------------------------------
        // Parsing helpers
        // ------------------------------------------------------------------

        private static Dictionary<string, StatusEntry> ParseStatus(string text)
        {
            var map = new Dictionary<string, StatusEntry>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(text)) return map;

            string[] fields = text.Split(new char[] { '\0' }, StringSplitOptions.None);

            for (int i = 0; i < fields.Length; i++)
            {
                string record = fields[i];
                if (record.Length < 4) continue;

                char x = record[0];
                char y = record[1];
                if (x == '!') continue;

                string path = NormalizePath(record.Substring(3));
                if (string.IsNullOrEmpty(path)) continue;

                // Rename / copy entries carry the original path as the next NUL field.
                bool renamed = x == 'R' || y == 'R' || x == 'C' || y == 'C';
                if (renamed && i + 1 < fields.Length) i++;

                var entry = new StatusEntry();
                if (x == '?' && y == '?')
                {
                    entry.Untracked = true;
                }
                else
                {
                    entry.Deleted = x == 'D' || y == 'D';
                    entry.Modified = true;
                }

                map[path] = entry;
            }

            return map;
        }

        private static VaultFile MakeFile(
            string path,
            bool lockable,
            bool tracked,
            StatusEntry status,
            Dictionary<string, LockInfo> locks,
            HashSet<string> behind)
        {
            var file = new VaultFile();
            file.Path = path;
            file.IsLockable = lockable;
            file.IsTracked = tracked;

            if (status != null)
            {
                file.Deleted = status.Deleted;
                file.Modified = !status.Untracked;
            }

            LockInfo info;
            if (locks.TryGetValue(path, out info)) file.Lock = info;

            file.BehindRemote = tracked && behind.Contains(path);
            file.State = ComputeState(file);
            return file;
        }

        private static FileState ComputeState(VaultFile file)
        {
            if (file.Lock != null && !file.Lock.IsMine && (file.Modified || file.Deleted))
                return FileState.ConflictLockedByOther;

            if (file.Lock != null && !file.Lock.IsMine)
                return FileState.LockedByOther;

            if (file.Lock != null && file.Lock.IsMine)
                return FileState.CheckedOutByMe;

            if (file.IsLockable && file.IsTracked && file.Modified)
                return FileState.ModifiedNotCheckedOut;

            if (!file.IsTracked && !file.Deleted)
                return FileState.NewFile;

            if (file.Deleted)
                return FileState.Deleted;

            if (file.Modified)
                return FileState.Changed;

            if (file.BehindRemote)
                return FileState.OutOfDate;

            return FileState.Available;
        }

        private static VaultException ErrorFrom(GitResult result)
        {
            string stderr = result != null ? result.StdErr : null;
            string stdout = result != null ? result.StdOut : null;
            return new VaultException(
                GitRunner.FriendlyError(stderr),
                GitRunner.CombineOutput(stderr, stdout));
        }

        private static string ReadConfigValue(GitResult result)
        {
            if (result == null || result.ExitCode != 0) return null;
            string value = (result.StdOut ?? string.Empty).Trim();
            return value.Length == 0 ? null : value;
        }

        private static Dictionary<string, VaultFile> BuildIndex(IReadOnlyList<VaultFile> files)
        {
            var index = new Dictionary<string, VaultFile>(StringComparer.OrdinalIgnoreCase);
            if (files == null) return index;

            foreach (VaultFile file in files)
            {
                if (file != null && !string.IsNullOrEmpty(file.Path))
                    index[NormalizePath(file.Path)] = file;
            }
            return index;
        }

        private static VaultFile FindFile(Dictionary<string, VaultFile> index, string path)
        {
            VaultFile file;
            return index.TryGetValue(path, out file) ? file : null;
        }

        private static string FormatLockLine(LockInfo info)
        {
            string owner = string.IsNullOrEmpty(info.Owner) ? "someone" : info.Owner;
            return info.Path + " is checked out by " + owner + " since " + FormatDate(info.LockedAtUtc);
        }

        private static string FormatDate(DateTime utc)
        {
            if (utc == DateTime.MinValue) return "an unknown time";
            return utc.ToLocalTime().ToString("d MMM yyyy HH:mm");
        }

        private static string DescribeLock(LockInfo info)
        {
            if (info == null) return "by someone";
            string owner = string.IsNullOrEmpty(info.Owner) ? "someone" : info.Owner;
            return "by " + owner + " since " + FormatDate(info.LockedAtUtc);
        }

        private static string Files(int count)
        {
            return count + (count == 1 ? " file" : " files");
        }

        // ------------------------------------------------------------------
        // Small utilities
        // ------------------------------------------------------------------

        private static List<string> NormalizeDistinct(IReadOnlyList<string> paths)
        {
            var result = new List<string>();
            if (paths == null) return result;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in paths)
            {
                if (string.IsNullOrEmpty(raw)) continue;
                string path = NormalizePath(raw);
                if (path.Length == 0) continue;
                if (seen.Add(path)) result.Add(path);
            }
            return result;
        }

        private static string NormalizePath(string path)
        {
            if (path == null) return null;

            string result = path.Replace('\\', '/');
            while (result.StartsWith("./", StringComparison.Ordinal))
                result = result.Substring(2);
            return result;
        }

        /// <summary>Short alias used by the file-list half of the class.</summary>
        private static string Norm(string path)
        {
            return NormalizePath(path) ?? string.Empty;
        }

        private string FullPath(string repoRelative)
        {
            string relative = (repoRelative ?? string.Empty).Replace('\\', '/').TrimStart('/');
            return Path.Combine(_repoRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        }

        private static List<string> SplitNul(string text)
        {
            var parts = new List<string>();
            if (string.IsNullOrEmpty(text)) return parts;

            foreach (string part in text.Split(new char[] { '\0' }, StringSplitOptions.RemoveEmptyEntries))
                parts.Add(part);
            return parts;
        }

        private static string[] SplitLines(string text)
        {
            if (string.IsNullOrEmpty(text)) return new string[0];
            return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        }

        private static bool HasLockableExtension(string path)
        {
            string extension = Path.GetExtension(path);
            if (string.IsNullOrEmpty(extension)) return false;

            extension = extension.Substring(1);
            foreach (string known in LockableExtensions)
            {
                if (string.Equals(known, extension, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static string MakeLockablePattern(string extension)
        {
            var sb = new StringBuilder("*.");
            foreach (char c in extension)
            {
                if (char.IsLetter(c))
                {
                    sb.Append('[');
                    sb.Append(char.ToLowerInvariant(c));
                    sb.Append(char.ToUpperInvariant(c));
                    sb.Append(']');
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        private static async Task<string> ReadAllTextAsync(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream, new UTF8Encoding(false), true))
            {
                return await reader.ReadToEndAsync().ConfigureAwait(false);
            }
        }

        private static async Task AppendMissingLinesAsync(
            string path, IList<string> lines, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            string existing = string.Empty;
            if (File.Exists(path))
                existing = await ReadAllTextAsync(path).ConfigureAwait(false);

            var present = new HashSet<string>(StringComparer.Ordinal);
            foreach (string line in SplitLines(existing))
                present.Add(line.Trim());

            var sb = new StringBuilder(existing);
            bool changed = false;

            foreach (string line in lines)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(line) || present.Contains(line)) continue;

                if (sb.Length > 0)
                {
                    char last = sb[sb.Length - 1];
                    if (last != '\n' && last != '\r') sb.Append(Environment.NewLine);
                }

                sb.Append(line);
                sb.Append(Environment.NewLine);
                present.Add(line);
                changed = true;
            }

            if (!changed) return;

            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

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

            public DelegateProgress(Action<string> action)
            {
                _action = action;
            }

            public void Report(string value)
            {
                _action(value);
            }
        }

        private sealed class StatusEntry
        {
            public bool Untracked;
            public bool Modified;
            public bool Deleted;
        }
    }
}
