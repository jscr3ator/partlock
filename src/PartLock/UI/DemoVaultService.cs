using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PartLock.Core;

namespace PartLock.UI
{
    /// <summary>
    /// Fake vault for screenshots and demos (`PartLock.exe --demo`).
    /// Pretends to be a team's DBF aircraft repo, complete with locks, conflicts and a slow network.
    /// </summary>
    public sealed class DemoVaultService : IVaultService
    {
        private readonly List<VaultFile> _files = new List<VaultFile>();
        private string _userName = "Sam Rivera";
        private string _userEmail = "sam@example.edu";
        private bool _lockingConfigured = true;

        public DemoVaultService()
        {
            BuildFiles();
        }

        public string RepoRoot
        {
            get { return @"C:\Users\you\Documents\dbf-aircraft"; }
        }

        public IProgress<string> Log { get; set; }

        public string ServerUserName
        {
            get { return "you"; }
        }

        // ------------------------------------------------------------- sample data

        private void BuildFiles()
        {
            _files.Clear();

            Add("wing/main-spar.SLDPRT", FileState.CheckedOutByMe, "you", true, 2.2);
            Add("wing/wing-assembly.SLDASM", FileState.LockedByOther, "maya-chen", false, 5.5);
            Add("wing/rib-W1.SLDPRT", FileState.Available);
            Add("wing/rib-W2.SLDPRT", FileState.Available);
            Add("wing/rib-W3.SLDPRT", FileState.OutOfDate);
            Add("wing/load-cases.csv", FileState.Changed);
            Add("wing/wing-skin-panel.SLDPRT", FileState.Deleted);

            Add("fuselage/bulkhead-F3.SLDPRT", FileState.LockedByOther, "alex-r", false, 26);
            Add("fuselage/bulkhead-F2.SLDPRT", FileState.Available);
            Add("fuselage/fuselage-assembly.SLDASM", FileState.ConflictLockedByOther, "jordan-p", true, 3.4);
            Add("fuselage/skin-panel-01.SLDPRT", FileState.ModifiedNotCheckedOut, null, true, 0);
            Add("fuselage/skin-panel-02.SLDPRT", FileState.Available);

            Add("tail/hstab.SLDASM", FileState.Available);
            Add("tail/vstab.SLDPRT", FileState.OutOfDate);
            Add("tail/rudder-hinge.SLDPRT", FileState.CheckedOutByMe, "you", false, 0.4);
            Add("tail/tail-assembly.SLDASM", FileState.Available);

            Add("landing-gear/main-strut.SLDPRT", FileState.Available);
            Add("landing-gear/wheel-fork.SLDPRT", FileState.OutOfDate);
            Add("landing-gear/axle.SLDPRT", FileState.Available);
            Add("landing-gear/gear-assembly.SLDASM", FileState.Available);

            Add("payload/release-mechanism.SLDASM", FileState.CheckedOutByMe, "you", true, 1.6);
            Add("payload/release-servo.SLDPRT", FileState.LockedByOther, "maya-chen", false, 50);
            Add("payload/payload-bay.SLDPRT", FileState.NewFile);
            Add("payload/servo-bracket.SLDPRT", FileState.Available);

            Add("electronics/avionics-tray.SLDPRT", FileState.Available);
            Add("electronics/sensor-mount.SLDPRT", FileState.Deleted);
            Add("electronics/wiring-notes.md", FileState.Changed);
            Add("electronics/power-board.kicad_pcb", FileState.Changed);

            Add("drawings/wing-rib.SLDDRW", FileState.OutOfDate);
            Add("drawings/fuselage-general-arrangement.SLDDRW", FileState.Available);
        }

        private void Add(string path, FileState state, string owner = null, bool modified = false, double hoursAgo = 0)
        {
            var file = new VaultFile
            {
                Path = path,
                IsLockable = IsCadFile(path),
                IsTracked = state != FileState.NewFile,
                Modified = modified,
                Deleted = state == FileState.Deleted,
                BehindRemote = state == FileState.OutOfDate,
                State = state
            };

            if (!string.IsNullOrEmpty(owner))
            {
                file.Lock = NewLock(path, owner, hoursAgo);
            }
            _files.Add(file);
        }

        private static bool IsCadFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string lower = path.ToLowerInvariant();
            return lower.EndsWith(".sldprt") || lower.EndsWith(".sldasm") ||
                   lower.EndsWith(".slddrw") || lower.EndsWith(".step") || lower.EndsWith(".stp");
        }

        private static LockInfo NewLock(string path, string owner, double hoursAgo)
        {
            double hours = Math.Max(0.05, hoursAgo);
            return new LockInfo
            {
                Id = "lock-" + Math.Abs(path.GetHashCode()).ToString("x"),
                Path = path,
                Owner = owner,
                IsMine = string.Equals(owner, "you", StringComparison.OrdinalIgnoreCase),
                LockedAtUtc = DateTime.UtcNow.AddHours(-hours)
            };
        }

        private static VaultFile Clone(VaultFile file)
        {
            var copy = new VaultFile
            {
                Path = file.Path,
                IsLockable = file.IsLockable,
                IsTracked = file.IsTracked,
                Modified = file.Modified,
                Deleted = file.Deleted,
                BehindRemote = file.BehindRemote,
                State = file.State
            };
            if (file.Lock != null)
            {
                copy.Lock = new LockInfo
                {
                    Id = file.Lock.Id,
                    Path = file.Lock.Path,
                    Owner = file.Lock.Owner,
                    IsMine = file.Lock.IsMine,
                    LockedAtUtc = file.Lock.LockedAtUtc
                };
            }
            return copy;
        }

        private VaultFile Find(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            return _files.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
        }

        private void LogLine(string line)
        {
            IProgress<string> log = Log;
            if (log != null) log.Report(line);
        }

        private static string ShortCommit()
        {
            return Guid.NewGuid().ToString("N").Substring(0, 7);
        }

        // ------------------------------------------------------------- interface

        public async Task<bool> IsLockingConfiguredAsync(CancellationToken ct = default(CancellationToken))
        {
            await Task.Delay(250, ct).ConfigureAwait(false);
            return _lockingConfigured;
        }

        public async Task EnableLockingAsync(CancellationToken ct = default(CancellationToken))
        {
            await Task.Delay(900, ct).ConfigureAwait(false);
            LogLine("> git lfs install --local");
            LogLine("> writing .gitattributes and .gitignore");
            LogLine("> git add --renormalize .");
            LogLine("> git commit -m \"Enable PartLock check-out locking\"");
            LogLine("> git push origin main");
            _lockingConfigured = true;
            LogLine("Locking is on. Check out files before you edit them.");
        }

        public async Task<IReadOnlyList<VaultFile>> GetFilesAsync(bool fetchRemote, CancellationToken ct = default(CancellationToken))
        {
            await Task.Delay(600, ct).ConfigureAwait(false);
            if (fetchRemote)
            {
                LogLine("> git fetch --prune origin");
                LogLine("> git lfs locks --json");
            }
            LogLine("> git status --porcelain=v1");

            var snapshot = new List<VaultFile>();
            foreach (VaultFile file in _files) snapshot.Add(Clone(file));
            return snapshot;
        }

        public async Task GetLatestAsync(CancellationToken ct = default(CancellationToken))
        {
            await Task.Delay(750, ct).ConfigureAwait(false);
            LogLine("> git pull --rebase --autostash origin main");

            int updated = 0;
            foreach (VaultFile file in _files)
            {
                if (!file.BehindRemote) continue;
                file.BehindRemote = false;
                file.State = StateWithLock(file);
                LogLine("Updating " + file.Path);
                updated++;
            }

            LogLine(updated == 0
                ? "Already up to date."
                : "Fast-forward: " + updated + " file" + (updated == 1 ? "" : "s") + " updated.");
        }

        public async Task CheckOutAsync(IReadOnlyList<string> paths, CancellationToken ct = default(CancellationToken))
        {
            if (paths == null || paths.Count == 0)
            {
                throw new VaultException("Pick at least one file to check out.");
            }

            await Task.Delay(700, ct).ConfigureAwait(false);

            var targets = new List<VaultFile>();
            foreach (string path in paths)
            {
                VaultFile file = Find(path);
                if (file == null) continue;
                if (file.Lock != null && !file.Lock.IsMine)
                {
                    throw new VaultException(
                        file.FileName + " is checked out by " + file.Lock.Owner +
                        ". Ask them to check it in before you edit it.",
                        "git lfs lock " + file.Path + "\n" +
                        "lock failed: already locked by " + file.Lock.Owner);
                }
                targets.Add(file);
            }

            foreach (VaultFile file in targets)
            {
                if (file.Lock == null)
                {
                    LogLine("> git lfs lock \"" + file.Path + "\"");
                }
                file.Lock = NewLock(file.Path, "you", 0);
                file.IsTracked = true;
                file.State = FileState.CheckedOutByMe;
            }

            LogLine("Locked " + targets.Count + " file" + (targets.Count == 1 ? "" : "s") + ".");
        }

        public async Task CheckInAsync(IReadOnlyList<string> paths, string message, bool keepCheckedOut,
            CancellationToken ct = default(CancellationToken))
        {
            if (paths == null || paths.Count == 0)
            {
                throw new VaultException("Pick at least one file to check in.");
            }

            string text = (message ?? "").Trim();
            if (text.Length == 0)
            {
                throw new VaultException("Write a short message about what you changed.");
            }

            await Task.Delay(900, ct).ConfigureAwait(false);

            var targets = new List<VaultFile>();
            foreach (string path in paths)
            {
                VaultFile file = Find(path);
                if (file == null) continue;
                if (file.State == FileState.ModifiedNotCheckedOut || file.State == FileState.ConflictLockedByOther)
                {
                    throw new VaultException(
                        file.FileName + " was edited without being checked out. Undo your edit or ask the team lead for help.",
                        "git add -- " + file.Path + "\n" +
                        "error: " + file.Path + " is not locked by you");
                }
                targets.Add(file);
            }

            if (targets.Count == 0) return;

            string commit = ShortCommit();
            LogLine("> git add -- " + string.Join(" ", targets.Select(f => "\"" + f.Path + "\"").ToArray()));
            LogLine("> git commit -m \"" + text.Replace("\"", "'") + "\"");
            LogLine("[main " + commit + "] " + text);
            LogLine(" " + targets.Count + " file" + (targets.Count == 1 ? " changed" : "s changed"));
            LogLine("> git push origin main");
            LogLine("To https://github.com/dbf-aircraft/cad.git");
            LogLine("   9c1d2f0.." + commit + "  main -> main");

            var removed = new List<VaultFile>();
            foreach (VaultFile file in targets)
            {
                bool wasDeleted = file.Deleted;
                file.Modified = false;
                file.Deleted = false;
                file.IsTracked = true;
                file.BehindRemote = false;

                if (wasDeleted)
                {
                    removed.Add(file);
                    LogLine("Removed " + file.Path + " from the vault.");
                    continue;
                }

                if (keepCheckedOut)
                {
                    file.Lock = NewLock(file.Path, "you", 0);
                    file.State = FileState.CheckedOutByMe;
                    LogLine("Keeping your lock on " + file.Path + ".");
                }
                else
                {
                    file.Lock = null;
                    file.State = FileState.Available;
                    LogLine("> git lfs unlock \"" + file.Path + "\"");
                }
            }

            foreach (VaultFile file in removed) _files.Remove(file);
        }

        public async Task UndoCheckOutAsync(IReadOnlyList<string> paths, CancellationToken ct = default(CancellationToken))
        {
            if (paths == null || paths.Count == 0) return;
            await Task.Delay(600, ct).ConfigureAwait(false);

            var removed = new List<VaultFile>();
            foreach (string path in paths)
            {
                VaultFile file = Find(path);
                if (file == null) continue;

                if (file.State == FileState.NewFile)
                {
                    removed.Add(file);
                    LogLine("Deleted new file " + file.Path + ".");
                    continue;
                }

                if (file.Modified || file.Deleted)
                {
                    LogLine("> git restore --source=HEAD --staged --worktree -- \"" + file.Path + "\"");
                }
                if (file.Lock != null && file.Lock.IsMine)
                {
                    LogLine("> git lfs unlock \"" + file.Path + "\"");
                }

                file.Modified = false;
                file.Deleted = false;
                if (file.Lock != null && file.Lock.IsMine) file.Lock = null;
                file.State = StateWithLock(file);
            }

            foreach (VaultFile file in removed) _files.Remove(file);
        }

        public async Task ForceUnlockAsync(LockInfo lockInfo, CancellationToken ct = default(CancellationToken))
        {
            if (lockInfo == null) return;
            await Task.Delay(500, ct).ConfigureAwait(false);

            LogLine("> git lfs unlock --force \"" + lockInfo.Path + "\"");
            VaultFile file = Find(lockInfo.Path);
            if (file != null && file.Lock != null &&
                string.Equals(file.Lock.Owner, lockInfo.Owner, StringComparison.OrdinalIgnoreCase))
            {
                file.Lock = null;
                file.State = StateWithLock(file);
            }
            LogLine("Forced unlock of " + lockInfo.Path + " (was " + (lockInfo.Owner ?? "someone") + ").");
        }

        public async Task<IReadOnlyList<HistoryEntry>> GetHistoryAsync(string path,
            CancellationToken ct = default(CancellationToken))
        {
            await Task.Delay(650, ct).ConfigureAwait(false);
            LogLine("> git log --follow --format=%h%x09%an%x09%ad%x09%s -- " + (path ?? ""));

            DateTime now = DateTime.Now;
            string name = path ?? "file";
            var entries = new List<HistoryEntry>
            {
                new HistoryEntry
                {
                    Commit = ShortCommit(),
                    Author = "Maya Chen",
                    Date = now.AddHours(-3),
                    Message = "Thicken " + ShortName(name) + " to 3 mm after the load test"
                },
                new HistoryEntry
                {
                    Commit = ShortCommit(),
                    Author = "Sam Rivera",
                    Date = now.AddDays(-2),
                    Message = "Add lightening holes, save with new mass properties"
                },
                new HistoryEntry
                {
                    Commit = ShortCommit(),
                    Author = "Alex R.",
                    Date = now.AddDays(-6),
                    Message = "Fix mate errors in the assembly"
                },
                new HistoryEntry
                {
                    Commit = ShortCommit(),
                    Author = "Jordan P.",
                    Date = now.AddDays(-14),
                    Message = "Initial layout for the 2026 aircraft"
                }
            };
            return entries;
        }

        public async Task<Tuple<string, string>> GetCommitIdentityAsync(CancellationToken ct = default(CancellationToken))
        {
            await Task.Delay(120, ct).ConfigureAwait(false);
            return Tuple.Create(_userName, _userEmail);
        }

        public async Task SetCommitIdentityAsync(string name, string email,
            CancellationToken ct = default(CancellationToken))
        {
            await Task.Delay(150, ct).ConfigureAwait(false);
            _userName = name;
            _userEmail = email;
            LogLine("> git config --global user.name " + (name ?? ""));
            LogLine("> git config --global user.email " + (email ?? ""));
        }

        // ------------------------------------------------------------- helpers

        private static FileState StateWithLock(VaultFile file)
        {
            if (file.Lock != null)
            {
                if (file.Lock.IsMine) return FileState.CheckedOutByMe;
                return file.Modified ? FileState.ConflictLockedByOther : FileState.LockedByOther;
            }
            if (file.Deleted) return FileState.Deleted;
            if (file.Modified)
            {
                return file.IsLockable ? FileState.ModifiedNotCheckedOut : FileState.Changed;
            }
            if (!file.IsTracked) return FileState.NewFile;
            if (file.BehindRemote) return FileState.OutOfDate;
            return FileState.Available;
        }

        private static string ShortName(string path)
        {
            if (string.IsNullOrEmpty(path)) return "file";
            int slash = path.LastIndexOf('/');
            return slash < 0 ? path : path.Substring(slash + 1);
        }
    }
}
