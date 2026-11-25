// PartLock - the user actions: get latest, check out, check in, undo, admin unlock, history, identity.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PartLock.Core
{
    public partial class VaultService
    {
        public async Task GetLatestAsync(CancellationToken ct = default(CancellationToken))
        {
            await EnsureRepoReadyAsync(ct).ConfigureAwait(false);
            await EnsureUpstreamAsync(ct).ConfigureAwait(false);

            // Binary CAD files can't be merged. If you edited a file that someone else has since
            // checked in a newer version of, updating would leave a broken half-merged file, so stop
            // and say exactly which files, before touching anything.
            LogLine("Checking for conflicts with your edits…");
            await GitAsync(new string[] { "fetch", "--quiet" }, ct).ConfigureAwait(false);
            await RefreshBehindRemoteAsync(ct).ConfigureAwait(false);
            GitResult status = await GitAsync(
                new string[] { "status", "--porcelain=v1", "-z", "--untracked-files=no" }, ct).ConfigureAwait(false);
            HashSet<string> untrackedNone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> deleted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ParseStatus(status.StdOut, untrackedNone, changed, deleted);
            changed.UnionWith(deleted);

            List<string> clashes = new List<string>();
            foreach (string path in changed)
                if (_behindRemote.Contains(path)) clashes.Add(path);
            if (clashes.Count > 0)
            {
                clashes.Sort(StringComparer.OrdinalIgnoreCase);
                throw new VaultException(
                    "You changed " + string.Join(", ", clashes.ToArray()) + " on this computer, but someone has " +
                    "checked in a newer version. Save a copy of your file somewhere else if you need it, " +
                    "then use Undo Check Out on it and Get Latest again.");
            }

            LogLine("Getting everyone's latest changes…");
            await GitAsync(new string[] { "pull", "--rebase", "--autostash" }, ct, null, true, 3600000).ConfigureAwait(false);

            LogLine("Downloading the latest CAD files…");
            await GitAsync(new string[] { "lfs", "pull" }, ct, null, true, 3600000).ConfigureAwait(false);

            _behindRemote = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            LogLine("You're up to date.");
        }

        public async Task CheckOutAsync(IReadOnlyList<string> paths,
            CancellationToken ct = default(CancellationToken))
        {
            List<string> requested = NormalizePaths(paths);
            if (requested.Count == 0) return;

            LogLine("Checking out " + PathsSummary(requested) + "…");

            IReadOnlyList<VaultFile> files = await GetFilesAsync(true, ct).ConfigureAwait(false);
            Dictionary<string, VaultFile> byPath = IndexFiles(files);

            List<string> heldByOthers = new List<string>();
            List<string> risky = new List<string>();
            bool anyBehind = false;

            foreach (string path in requested)
            {
                VaultFile file;
                if (!byPath.TryGetValue(path, out file)) continue;

                if (file.Lock != null && !file.Lock.IsMine)
                    heldByOthers.Add(file.Path + " is checked out " + DescribeLock(file.Lock) + ".");

                if (file.BehindRemote)
                {
                    anyBehind = true;
                    if (file.State == FileState.ModifiedNotCheckedOut) risky.Add(file.Path);
                }
            }

            if (heldByOthers.Count > 0)
                throw new VaultException(string.Join("\n", heldByOthers.ToArray()));

            if (risky.Count > 0)
            {
                throw new VaultException(
                    "You edited " + string.Join(", ", risky.ToArray()) +
                    " without checking it out, and someone has checked in a newer version. " +
                    "Use Undo Check Out to throw away your copy first.");
            }

            if (anyBehind) await GetLatestAsync(ct).ConfigureAwait(false);

            List<string> lockedNow = new List<string>();
            try
            {
                foreach (string path in requested)
                {
                    ct.ThrowIfCancellationRequested();

                    VaultFile alreadyMine;
                    if (byPath.TryGetValue(path, out alreadyMine) &&
                        alreadyMine.Lock != null && alreadyMine.Lock.IsMine)
                    {
                        MakeWritable(path);
                        continue;
                    }

                    GitResult result = await GitAsync(new string[] { "lfs", "lock", "--json", "--", path }, ct)
                        .ConfigureAwait(false);
                    lockedNow.Add(path);

                    LockInfo info = LfsJson.ParseSingle(result.StdOut);
                    if (info == null) info = new LockInfo();
                    if (string.IsNullOrEmpty(info.Path)) info.Path = path;
                    if (string.IsNullOrEmpty(info.Owner)) info.Owner = _serverUserName;
                    if (info.LockedAtUtc == DateTime.MinValue) info.LockedAtUtc = DateTime.UtcNow;
                    info.IsMine = true;
                    CacheLock(info);

                    MakeWritable(path);
                }
            }
            catch (Exception)
            {
                // Undo the partial check-out so the server isn't left half-locked.
                foreach (string path in lockedNow)
                {
                    await GitAsync(new string[] { "lfs", "unlock", "--force", "--", path },
                        CancellationToken.None, null, false).ConfigureAwait(false);
                    ForgetLock(path);
                }
                throw;
            }

            LogLine("Checked out " + Files(requested.Count) + ".");
        }

        public async Task CheckInAsync(IReadOnlyList<string> paths, string message, bool keepCheckedOut,
            CancellationToken ct = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(message))
                throw new VaultException("Type a short description of what you changed before checking in.");

            List<string> requested = NormalizePaths(paths);
            if (requested.Count == 0)
                throw new VaultException("Select at least one file to check in.");

            LogLine("Checking in " + PathsSummary(requested) + "…");

            IReadOnlyList<VaultFile> files = await GetFilesAsync(true, ct).ConfigureAwait(false);
            Dictionary<string, VaultFile> byPath = IndexFiles(files);

            List<string> heldByOthers = new List<string>();
            List<string> needCheckOut = new List<string>();

            foreach (string path in requested)
            {
                VaultFile file;
                if (!byPath.TryGetValue(path, out file)) continue;

                if (file.Lock != null && !file.Lock.IsMine)
                    heldByOthers.Add(file.Path + " is checked out " + DescribeLock(file.Lock) + ".");

                if (file.IsTracked && file.IsLockable && file.State != FileState.CheckedOutByMe)
                    needCheckOut.Add(file.Path);
            }

            if (heldByOthers.Count > 0)
                throw new VaultException(string.Join("\n", heldByOthers.ToArray()));

            if (needCheckOut.Count > 0)
            {
                throw new VaultException("You need to check out " +
                    string.Join(", ", needCheckOut.ToArray()) + " before checking it in.");
            }

            List<string> unresolved = requested.Where(p => HasConflictMarkers(FullPath(p))).ToList();
            if (unresolved.Count > 0)
            {
                throw new VaultException("Fix the overlapping changes in " + string.Join(", ", unresolved.ToArray()) + " first: " +
                    "open the file, keep what you want between the <<<<<<< and >>>>>>> lines, delete the marker lines, and save.");
            }

            foreach (string path in requested)
            {
                ct.ThrowIfCancellationRequested();
                string fullPath = Path.Combine(RepoRoot, path);
                if (File.Exists(fullPath) && IsFileOpenElsewhere(fullPath))
                {
                    throw new VaultException(
                        path + " is still open (probably in SolidWorks). Save and close it, then check in.");
                }
            }

            await GitAsync(new string[] { "reset", "-q" }, ct).ConfigureAwait(false);

            foreach (List<string> chunk in Chunks(requested, 50))
            {
                ct.ThrowIfCancellationRequested();
                List<string> args = new List<string> { "add", "-A", "--" };
                args.AddRange(chunk);
                await GitAsync(args, ct).ConfigureAwait(false);
            }

            GitResult staged = await GitAsync(new string[] { "diff", "--cached", "--quiet" }, ct, null, false)
                .ConfigureAwait(false);
            if (staged.ExitCode != 0)
            {
                LogLine("Saving your changes…");
                await GitAsync(new string[] { "commit", "-m", message }, ct).ConfigureAwait(false);
            }
            else
            {
                LogLine("No changes to save.");
            }

            await PushWithRetryAsync(ct).ConfigureAwait(false);

            int released = 0;
            if (!keepCheckedOut)
            {
                foreach (string path in requested)
                {
                    ct.ThrowIfCancellationRequested();
                    VaultFile file;
                    if (!byPath.TryGetValue(path, out file)) continue;
                    if (file.Lock == null || !file.Lock.IsMine) continue;

                    await UnlockAsync(file, false, ct).ConfigureAwait(false);
                    released++;
                }
            }

            LogLine("Checked in " + Files(requested.Count) + "." +
                    (released > 0 ? " Released " + Files(released) + "." : string.Empty));
        }

        public async Task UndoCheckOutAsync(IReadOnlyList<string> paths,
            CancellationToken ct = default(CancellationToken))
        {
            List<string> requested = NormalizePaths(paths);
            if (requested.Count == 0) return;

            LogLine("Undoing check out for " + PathsSummary(requested) + "…");

            IReadOnlyList<VaultFile> files = await GetFilesAsync(true, ct).ConfigureAwait(false);
            Dictionary<string, VaultFile> byPath = IndexFiles(files);

            foreach (string path in requested)
            {
                string fullPath = Path.Combine(RepoRoot, path);
                if (File.Exists(fullPath) && IsFileOpenElsewhere(fullPath))
                {
                    throw new VaultException(
                        path + " is still open (probably in SolidWorks). Save and close it, then try again.");
                }
            }

            List<string> tracked = new List<string>();
            foreach (string path in requested)
            {
                VaultFile file;
                if (byPath.TryGetValue(path, out file) && file.IsTracked)
                {
                    tracked.Add(path);
                }
                else if (File.Exists(Path.Combine(RepoRoot, path)))
                {
                    LogLine("Kept new file " + path + " (not in the vault yet).");
                }
            }

            foreach (List<string> chunk in Chunks(tracked, 50))
            {
                ct.ThrowIfCancellationRequested();
                List<string> args = new List<string>
                {
                    "restore", "--source=HEAD", "--staged", "--worktree", "--"
                };
                args.AddRange(chunk);

                GitResult restore = await GitAsync(args, ct, null, false).ConfigureAwait(false);
                if (restore.ExitCode != 0)
                {
                    string error = restore.StdErr ?? string.Empty;
                    if (error.IndexOf("did not match any file", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        error.IndexOf("pathspec", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        LogLine("Skipped " + string.Join(", ", chunk.ToArray()) + " (not in the vault yet).");
                    }
                    else
                    {
                        throw new VaultException(GitRunner.FriendlyError(error),
                            (error + "\n" + restore.StdOut).Trim());
                    }
                }
            }

            foreach (string path in requested)
            {
                ct.ThrowIfCancellationRequested();
                VaultFile file;
                if (!byPath.TryGetValue(path, out file)) continue;
                if (file.Lock == null || !file.Lock.IsMine) continue;

                await UnlockAsync(file, true, ct).ConfigureAwait(false);
            }

            LogLine("Threw away your changes to " + Files(requested.Count) + ".");
        }

        public async Task ForceUnlockAsync(LockInfo lockInfo, CancellationToken ct = default(CancellationToken))
        {
            if (lockInfo == null)
                throw new VaultException("That check-out can't be found any more. Refresh and try again.");

            LogLine("Releasing " + (string.IsNullOrEmpty(lockInfo.Path) ? "that check-out" : lockInfo.Path) + "…");

            if (!string.IsNullOrEmpty(lockInfo.Id))
            {
                await GitAsync(new string[] { "lfs", "unlock", "--force", "--id=" + lockInfo.Id }, ct)
                    .ConfigureAwait(false);
            }
            else if (!string.IsNullOrEmpty(lockInfo.Path))
            {
                await GitAsync(new string[] { "lfs", "unlock", "--force", "--", lockInfo.Path }, ct)
                    .ConfigureAwait(false);
            }
            else
            {
                throw new VaultException("That check-out has no file or ID, so it can't be released.");
            }

            if (!string.IsNullOrEmpty(lockInfo.Path)) ForgetLock(lockInfo.Path);
            LogLine("Check-out released.");
        }

        public async Task<IReadOnlyList<HistoryEntry>> GetHistoryAsync(string path,
            CancellationToken ct = default(CancellationToken))
        {
            List<HistoryEntry> entries = new List<HistoryEntry>();
            string normalized = Norm(path);
            if (normalized.Length == 0) return entries;

            GitResult result = await GitAsync(new string[]
            {
                "log", "--follow", "-n", "100", "--format=%H%x1f%an%x1f%aI%x1f%s", "--", normalized
            }, ct, null, false).ConfigureAwait(false);

            if (result.ExitCode != 0)
            {
                string error = result.StdErr ?? string.Empty;
                if (error.IndexOf("does not have any commits", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    error.IndexOf("unknown revision", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    error.IndexOf("bad revision", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return entries;
                }

                throw new VaultException(GitRunner.FriendlyError(error),
                    (error + "\n" + result.StdOut).Trim());
            }

            foreach (string raw in result.StdOut.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.Length == 0) continue;

                string[] parts = line.Split('\x1f');
                if (parts.Length < 4) continue;

                HistoryEntry entry = new HistoryEntry();
                entry.Commit = parts[0];
                entry.Author = parts[1];

                DateTime date;
                if (DateTime.TryParse(parts[2], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out date))
                    entry.Date = date;
                else
                    entry.Date = DateTime.MinValue;

                entry.Message = string.Join("\x1f", parts.Skip(3).ToArray());
                entries.Add(entry);
            }

            return entries;
        }

        public async Task<Tuple<string, string>> GetCommitIdentityAsync(
            CancellationToken ct = default(CancellationToken))
        {
            GitResult name = await GitAsync(new string[] { "config", "user.name" }, ct, null, false)
                .ConfigureAwait(false);
            GitResult email = await GitAsync(new string[] { "config", "user.email" }, ct, null, false)
                .ConfigureAwait(false);

            string nameText = name.ExitCode == 0 ? name.StdOut.Trim() : null;
            string emailText = email.ExitCode == 0 ? email.StdOut.Trim() : null;
            if (string.IsNullOrEmpty(nameText)) nameText = null;
            if (string.IsNullOrEmpty(emailText)) emailText = null;

            return Tuple.Create(nameText, emailText);
        }

        public async Task SetCommitIdentityAsync(string name, string email,
            CancellationToken ct = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(email))
                throw new VaultException("Enter both your name and your email address.");

            await GitAsync(new string[] { "config", "--global", "user.name", name.Trim() }, ct)
                .ConfigureAwait(false);
            await GitAsync(new string[] { "config", "--global", "user.email", email.Trim() }, ct)
                .ConfigureAwait(false);

            LogLine("Saved your name and email.");
        }

        // ---------------------------------------------------------------- action helpers

        private async Task PushWithRetryAsync(CancellationToken ct)
        {
            bool hasUpstream = await HasUpstreamAsync(ct).ConfigureAwait(false);
            int tries = 0;

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                tries++;

                LogLine("Uploading your changes…");
                string[] pushArgs = hasUpstream
                    ? new string[] { "push" }
                    : new string[] { "push", "-u", "origin", "HEAD" };

                GitResult push = await GitAsync(pushArgs, ct, null, false).ConfigureAwait(false);
                if (push.ExitCode == 0) return;

                bool retryable = GitRunner.IsRejectedPush(push.StdErr);
                if (!retryable || tries >= 3)
                {
                    throw new VaultException(GitRunner.FriendlyError(push.StdErr),
                        ((push.StdErr ?? string.Empty) + "\n" + (push.StdOut ?? string.Empty)).Trim());
                }

                LogLine("Someone else checked in first. Getting their changes and trying again…");
                GitResult pull = await GitAsync(new string[] { "pull", "--rebase", "--autostash" }, ct, null, false).ConfigureAwait(false);
                if (pull.ExitCode != 0) await HandleOverlapAsync(pull, ct).ConfigureAwait(false); // always throws
                hasUpstream = true;
            }
        }

        /// <summary>
        /// A teammate uploaded changes to the same lines of the same file(s). For text files this
        /// works like GitHub Desktop: take their version, put both versions in each file between
        /// conflict markers, and leave the edits unuploaded so the user can fix and upload again.
        /// Binary/lockable files keep the old behaviour (stop, nothing changed). Always throws.
        /// </summary>
        private async Task HandleOverlapAsync(GitResult pull, CancellationToken ct)
        {
            string details = ((pull.StdErr ?? string.Empty) + "\n" + (pull.StdOut ?? string.Empty)).Trim();
            GitResult unmerged = await GitAsync(new[] { "diff", "--name-only", "--diff-filter=U", "-z" }, ct, null, false).ConfigureAwait(false);
            List<string> conflicted = SplitNul(unmerged.StdOut).Select(Norm).Where(p => p.Length > 0).ToList();
            await GitAsync(new[] { "rebase", "--abort" }, ct, null, false).ConfigureAwait(false);

            if (conflicted.Count == 0) throw new VaultException(GitRunner.FriendlyError(pull.StdErr), details);

            Dictionary<string, bool> lockable = await GetLockableMapAsync(conflicted, ct).ConfigureAwait(false);
            bool anyBinary = conflicted.Any(p => (lockable.ContainsKey(p) && lockable[p]) || LooksLockableByExtension(p) || !IsTextFile(FullPath(p)));
            if (anyBinary)
            {
                throw new VaultException(
                    "Someone uploaded a newer version of " + string.Join(", ", conflicted.ToArray()) + " while you were working on it. " +
                    "Nothing was uploaded. Use Undo on your copy (save it elsewhere first if you need it), Get Latest, and try again.", details);
            }

            // Undo the local (unpushed) commit(s) but keep the edits, park them, update, put them back.
            GitResult baseRev = await GitAsync(new[] { "merge-base", "HEAD", "@{u}" }, ct, null, false).ConfigureAwait(false);
            string mergeBase = (baseRev.StdOut ?? string.Empty).Trim();
            if (baseRev.ExitCode != 0 || mergeBase.Length == 0) throw new VaultException(GitRunner.FriendlyError(pull.StdErr), details);
            await GitAsync(new[] { "reset", "-q", "--mixed", mergeBase }, ct).ConfigureAwait(false);
            await GitAsync(new[] { "stash", "push", "--include-untracked", "-m", "PartLock: your edits while getting a teammate's version" }, ct).ConfigureAwait(false);
            await GitAsync(new[] { "merge", "--ff-only", "@{u}" }, ct, null, true, 3600000).ConfigureAwait(false);
            GitResult pop = await GitAsync(new[] { "stash", "pop" }, ct, null, false).ConfigureAwait(false);
            if (pop.ExitCode != 0)
            {
                // Markers are in the files now; clear git's "unmerged" state so the files just show
                // as Changed, and drop the parked copy (its content is in the files).
                await GitAsync(new[] { "reset", "-q" }, ct, null, false).ConfigureAwait(false);
                GitResult stashes = await GitAsync(new[] { "stash", "list" }, ct, null, false).ConfigureAwait(false);
                if ((stashes.StdOut ?? string.Empty).Contains("PartLock: your edits"))
                {
                    foreach (string p in conflicted) RelabelConflictMarkers(FullPath(p));
                    bool applied = conflicted.Any(p => File.Exists(FullPath(p)) && HasConflictMarkers(FullPath(p)));
                    if (applied) await GitAsync(new[] { "stash", "drop" }, ct, null, false).ConfigureAwait(false);
                    else
                        throw new VaultException("A teammate changed the same files at the same time, and PartLock couldn't combine them automatically. " +
                            "Your edits are saved in Git's stash (\"PartLock: your edits\"). Ask a programmer to help, or contact your team lead.", details + "\n" + pop.StdErr);
                }
            }

            throw new VaultException(
                "A teammate changed the same part of " + string.Join(", ", conflicted.ToArray()) + " at the same time as you. " +
                "PartLock got their version and put both versions in the file, between lines starting with <<<<<<< and >>>>>>>. " +
                "Open the file, keep what you want, delete those marker lines, save, then upload again. Nothing was uploaded yet.", details);
        }

        /// <summary>Replaces git's marker labels ("Updated upstream" / "Stashed changes") with plain words.</summary>
        private static void RelabelConflictMarkers(string fullPath)
        {
            try
            {
                if (!File.Exists(fullPath) || !IsTextFile(fullPath)) return;
                string text = File.ReadAllText(fullPath);
                string relabeled = text
                    .Replace("<<<<<<< Updated upstream", "<<<<<<< teammate's version (from GitHub)")
                    .Replace(">>>>>>> Stashed changes", ">>>>>>> your version");
                if (relabeled != text) File.WriteAllText(fullPath, relabeled, new System.Text.UTF8Encoding(false));
            }
            catch (Exception) { }
        }

        /// <summary>True when a text file still contains merge conflict markers.</summary>
        public static bool HasConflictMarkers(string fullPath)
        {
            try
            {
                if (!File.Exists(fullPath) || new FileInfo(fullPath).Length > 20 * 1024 * 1024 || !IsTextFile(fullPath)) return false;
                bool start = false, mid = false;
                foreach (string line in File.ReadLines(fullPath))
                {
                    if (line.StartsWith("<<<<<<< ", StringComparison.Ordinal)) start = true;
                    else if (start && line == "=======") mid = true;
                    else if (mid && line.StartsWith(">>>>>>> ", StringComparison.Ordinal)) return true;
                }
            }
            catch (Exception) { }
            return false;
        }

        private static bool IsTextFile(string fullPath)
        {
            try
            {
                if (!File.Exists(fullPath)) return true;
                byte[] head = new byte[8000];
                int n;
                using (FileStream s = File.OpenRead(fullPath)) n = s.Read(head, 0, head.Length);
                for (int i = 0; i < n; i++) if (head[i] == 0) return false;
                return true;
            }
            catch (Exception) { return false; }
        }

        private async Task UnlockAsync(VaultFile file, bool force, CancellationToken ct)
        {
            List<string> args = new List<string> { "lfs", "unlock" };
            if (force) args.Add("--force");
            args.Add("--");
            args.Add(file.Path);

            GitResult result = await GitAsync(args, ct, null, false).ConfigureAwait(false);
            if (result.ExitCode == 0)
            {
                ForgetLock(file.Path);
                return;
            }

            if (file.Lock != null && !string.IsNullOrEmpty(file.Lock.Id))
            {
                List<string> byId = new List<string> { "lfs", "unlock" };
                if (force) byId.Add("--force");
                byId.Add("--id=" + file.Lock.Id);

                GitResult idResult = await GitAsync(byId, ct, null, false).ConfigureAwait(false);
                if (idResult.ExitCode == 0)
                {
                    ForgetLock(file.Path);
                    return;
                }

                throw new VaultException(GitRunner.FriendlyError(idResult.StdErr),
                    ((idResult.StdErr ?? string.Empty) + "\n" + (idResult.StdOut ?? string.Empty)).Trim());
            }

            // The lock is gone on the server already; don't fail an otherwise good check-in.
            LogLine("Couldn't release the check-out on " + file.Path +
                    ", but the file itself was saved.");
            ForgetLock(file.Path);
        }

        private void MakeWritable(string path)
        {
            try
            {
                string fullPath = FullPath(path);
                if (!File.Exists(fullPath)) return;

                FileAttributes attributes = File.GetAttributes(fullPath);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(fullPath, attributes & ~FileAttributes.ReadOnly);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private void CacheLock(LockInfo info)
        {
            _locks.RemoveAll(delegate (LockInfo l)
            {
                return l != null && string.Equals(l.Path, info.Path, StringComparison.OrdinalIgnoreCase);
            });
            _locks.Add(info);
        }

        private void ForgetLock(string path)
        {
            string normalized = Norm(path);
            _locks.RemoveAll(delegate (LockInfo l)
            {
                return l != null && string.Equals(l.Path, normalized, StringComparison.OrdinalIgnoreCase);
            });
        }

        private static List<string> NormalizePaths(IReadOnlyList<string> paths)
        {
            List<string> result = new List<string>();
            if (paths == null) return result;

            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths)
            {
                string normalized = Norm(path);
                if (normalized.Length == 0) continue;
                if (seen.Add(normalized)) result.Add(normalized);
            }
            return result;
        }

        private static Dictionary<string, VaultFile> IndexFiles(IReadOnlyList<VaultFile> files)
        {
            Dictionary<string, VaultFile> map = new Dictionary<string, VaultFile>(StringComparer.OrdinalIgnoreCase);
            if (files == null) return map;

            foreach (VaultFile file in files)
            {
                if (file != null && !string.IsNullOrEmpty(file.Path)) map[file.Path] = file;
            }
            return map;
        }

        private static string PathsSummary(List<string> paths)
        {
            if (paths.Count == 1) return paths[0];
            if (paths.Count == 2) return paths[0] + " and " + paths[1];
            return Files(paths.Count);
        }

        private static IEnumerable<List<string>> Chunks(List<string> items, int size)
        {
            for (int i = 0; i < items.Count; i += size)
            {
                List<string> chunk = new List<string>();
                int end = Math.Min(i + size, items.Count);
                for (int j = i; j < end; j++) chunk.Add(items[j]);
                yield return chunk;
            }
        }
    }
}
