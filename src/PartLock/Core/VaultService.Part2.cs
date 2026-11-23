// PartLock - reading local status, lockable attributes and server locks into VaultFile rows.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PartLock.Core
{
    public partial class VaultService
    {
        public async Task<IReadOnlyList<VaultFile>> GetFilesAsync(bool fetchRemote,
            CancellationToken ct = default(CancellationToken))
        {
            ct.ThrowIfCancellationRequested();

            if (fetchRemote)
            {
                LogLine("Checking the server for checked-out files and new versions…");

                GitResult fetch = await GitAsync(new string[] { "fetch", "--quiet" }, ct, null, false)
                    .ConfigureAwait(false);
                if (fetch.ExitCode != 0)
                    LogLine("Couldn't reach the server. Showing what's already on this computer.");

                await RefreshLocksAsync(ct).ConfigureAwait(false);
                await RefreshBehindRemoteAsync(ct).ConfigureAwait(false);
            }

            GitResult trackedResult = await GitAsync(new string[] { "ls-files", "-z" }, ct, null, false)
                .ConfigureAwait(false);
            GitResult untrackedResult = await GitAsync(
                    new string[] { "ls-files", "-z", "--others", "--exclude-standard" }, ct, null, false)
                .ConfigureAwait(false);
            GitResult statusResult = await GitAsync(
                    new string[] { "status", "--porcelain=v1", "-z", "--untracked-files=all" }, ct, null, false)
                .ConfigureAwait(false);

            if (!trackedResult.Success || !statusResult.Success)
            {
                throw new VaultException(
                    "PartLock couldn't read this folder as a Git working copy. Open your vault folder again.",
                    (trackedResult.StdErr + "\n" + statusResult.StdErr).Trim());
            }

            List<string> trackedPaths = SplitNul(trackedResult.StdOut);
            List<string> untrackedPaths = SplitNul(untrackedResult.StdOut);

            HashSet<string> trackedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string p in trackedPaths) trackedSet.Add(Norm(p));

            HashSet<string> untrackedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string p in untrackedPaths) untrackedSet.Add(Norm(p));

            HashSet<string> changedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> deletedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ParseStatus(statusResult.StdOut, untrackedSet, changedSet, deletedSet);

            List<string> candidates = new List<string>();
            HashSet<string> candidateSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string p in trackedPaths)
            {
                string path = Norm(p);
                if (path.Length > 0 && candidateSeen.Add(path)) candidates.Add(path);
            }
            foreach (string p in untrackedPaths)
            {
                string path = Norm(p);
                if (path.Length > 0 && candidateSeen.Add(path)) candidates.Add(path);
            }

            Dictionary<string, bool> lockable = await GetLockableMapAsync(candidates, ct).ConfigureAwait(false);

            Dictionary<string, LockInfo> lockByPath = new Dictionary<string, LockInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (LockInfo existing in _locks)
            {
                if (existing == null || string.IsNullOrEmpty(existing.Path)) continue;
                lockByPath[existing.Path] = existing;
            }

            HashSet<string> allPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string p in candidates) allPaths.Add(p);
            foreach (string p in changedSet) allPaths.Add(p);
            foreach (string p in deletedSet) allPaths.Add(p);
            foreach (string p in lockByPath.Keys) allPaths.Add(p);

            List<VaultFile> files = new List<VaultFile>();
            foreach (string path in allPaths)
            {
                bool isTracked = trackedSet.Contains(path);
                bool isUntracked = untrackedSet.Contains(path);
                bool isChanged = changedSet.Contains(path);
                bool isDeleted = deletedSet.Contains(path);
                bool isLockable = LookupLockable(lockable, path, candidateSeen);

                LockInfo lockInfo;
                lockByPath.TryGetValue(path, out lockInfo);

                // Lockable files always show. Other files only when they need attention or are locked.
                if (!isLockable && !isChanged && !isUntracked && !isDeleted && lockInfo == null) continue;

                VaultFile file = new VaultFile();
                file.Path = path;
                file.IsLockable = isLockable;
                file.IsTracked = isTracked;
                file.Deleted = isDeleted;
                file.Modified = isChanged || isDeleted;
                file.BehindRemote = _behindRemote.Contains(path);
                file.Lock = lockInfo;
                file.State = ComputeState(file, isUntracked);

                files.Add(file);
            }

            files.Sort(delegate (VaultFile a, VaultFile b)
            {
                return string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
            });

            return files;
        }

        // ---------------------------------------------------------------- server snapshot

        private async Task RefreshLocksAsync(CancellationToken ct)
        {
            GitResult result = await GitAsync(new string[] { "lfs", "locks", "--verify", "--json" }, ct, null, false)
                .ConfigureAwait(false);

            if (result.ExitCode != 0)
            {
                // Older servers reject --verify; plain --json still lists the locks.
                result = await GitAsync(new string[] { "lfs", "locks", "--json" }, ct, null, false)
                    .ConfigureAwait(false);
            }

            if (result.ExitCode != 0)
            {
                LogLine("Couldn't get the list of checked-out files. Showing the last known list.");
                return;
            }

            string json = result.StdOut == null ? string.Empty : result.StdOut.Trim();
            if (json.Length == 0 || (json[0] != '{' && json[0] != '['))
            {
                LogLine("The server sent an unexpected reply about checked-out files. Showing the last known list.");
                return;
            }

            LfsLockSet parsed = LfsJson.Parse(json, _serverUserName);

            if (parsed.VerifyFormat && !string.IsNullOrEmpty(parsed.FirstOursOwner))
                _serverUserName = parsed.FirstOursOwner;

            _locks = parsed.All();
        }

        private async Task RefreshBehindRemoteAsync(CancellationToken ct)
        {
            HashSet<string> behind = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            GitResult upstream = await GitAsync(
                new string[] { "rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}" }, ct, null, false)
                .ConfigureAwait(false);

            if (upstream.ExitCode == 0 && !string.IsNullOrWhiteSpace(upstream.StdOut))
            {
                GitResult diff = await GitAsync(
                    new string[] { "diff", "--name-only", "-z", "HEAD...@{u}" }, ct, null, false)
                    .ConfigureAwait(false);

                if (diff.ExitCode == 0)
                {
                    foreach (string p in SplitNul(diff.StdOut)) behind.Add(Norm(p));
                }
            }

            _behindRemote = behind;
        }

        // ---------------------------------------------------------------- attribute + status parsing

        private async Task<Dictionary<string, bool>> GetLockableMapAsync(List<string> candidates,
            CancellationToken ct)
        {
            Dictionary<string, bool> map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            if (candidates.Count == 0) return map;

            // -z makes both stdin and stdout NUL-separated: path\0attr\0value\0 per candidate.
            string stdin = string.Join("\0", candidates.ToArray()) + "\0";
            GitResult result = await GitAsync(new string[] { "check-attr", "--stdin", "-z", "lockable" }, ct,
                stdin, false).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                // Older git, or something odd about the working tree. Fall back to extensions.
                foreach (string candidate in candidates)
                    map[candidate] = LooksLockableByExtension(candidate);
                return map;
            }

            List<string> parts = SplitNul(result.StdOut);
            for (int i = 0; i + 2 < parts.Count; i += 3)
            {
                string path = Norm(parts[i]);
                if (path.Length == 0) continue;
                map[path] = IsLockableValue(parts[i + 2]);
            }

            return map;
        }

        private static void ParseStatus(string porcelain, HashSet<string> untracked,
            HashSet<string> changed, HashSet<string> deleted)
        {
            List<string> tokens = SplitNul(porcelain);
            for (int i = 0; i < tokens.Count; i++)
            {
                string token = tokens[i];
                if (token.Length < 4) continue;

                char index = token[0];
                char work = token[1];
                string path = Norm(token.Substring(3));
                if (path.Length == 0) continue;

                bool isRename = index == 'R' || index == 'C';

                if (index == '?' && work == '?')
                {
                    untracked.Add(path);
                }
                else if (index == 'D' || work == 'D')
                {
                    deleted.Add(path);
                }
                else
                {
                    changed.Add(path);
                }

                // In -z mode a rename entry is followed by the original path; consume it
                // so the next record stays aligned. The new path is the one that matters.
                if (isRename && i + 1 < tokens.Count)
                {
                    i++;
                }
            }
        }

        private static bool LookupLockable(Dictionary<string, bool> map, string path, HashSet<string> known)
        {
            bool value;
            if (map.TryGetValue(path, out value)) return value;
            if (!known.Contains(path)) return LooksLockableByExtension(path);
            return false;
        }

        private static bool IsLockableValue(string value)
        {
            return string.Equals(value, "set", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
        }

        private static bool LooksLockableByExtension(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            int dot = path.LastIndexOf('.');
            if (dot < 0 || dot == path.Length - 1) return false;
            string extension = path.Substring(dot + 1);

            foreach (string candidate in LockableExtensions)
            {
                if (string.Equals(extension, candidate, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static FileState ComputeState(VaultFile file, bool isUntracked)
        {
            bool lockedByOther = file.Lock != null && !file.Lock.IsMine;
            bool mine = file.Lock != null && file.Lock.IsMine;

            if (lockedByOther && (file.Modified || file.Deleted)) return FileState.ConflictLockedByOther;
            if (lockedByOther) return FileState.LockedByOther;
            if (mine) return FileState.CheckedOutByMe;

            if (file.IsLockable && file.IsTracked && file.Modified) return FileState.ModifiedNotCheckedOut;
            if (isUntracked) return FileState.NewFile;
            if (file.Deleted) return FileState.Deleted;
            if (!file.IsLockable && file.IsTracked && file.Modified) return FileState.Changed;
            if (file.BehindRemote) return FileState.OutOfDate;
            return FileState.Available;
        }
    }
}
