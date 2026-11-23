// PartLock - shared contracts. DO NOT CHANGE signatures in this file without the lead's OK:
// the Core, UI and test-harness code are all written against it.
//
// Must compile on BOTH .NET Framework 4.8 (the Windows app) and .NET 8 (the Linux test harness).
// So: no records, no `init`, no ranges/indices, no default interface methods, no System.Text.Json,
// no ProcessStartInfo.ArgumentList. Async/await and Task are fine.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PartLock.Core
{
    /// <summary>What the user sees in the Status column. Ordered by priority (first match wins).</summary>
    public enum FileState
    {
        /// <summary>Someone else holds the lock AND this user also changed the file locally (their edits can't be checked in).</summary>
        ConflictLockedByOther,
        /// <summary>Someone else holds the lock.</summary>
        LockedByOther,
        /// <summary>This user holds the lock (may or may not have edits; see VaultFile.Modified).</summary>
        CheckedOutByMe,
        /// <summary>Lockable file changed locally without a lock (made writable by hand). Risky.</summary>
        ModifiedNotCheckedOut,
        /// <summary>Untracked file that is not in the repo yet.</summary>
        NewFile,
        /// <summary>Non-lockable tracked file with local edits (code, docs, etc.).</summary>
        Changed,
        /// <summary>Tracked file deleted locally.</summary>
        Deleted,
        /// <summary>The server has a newer version than this computer (after fetch).</summary>
        OutOfDate,
        /// <summary>Up to date and nobody has it.</summary>
        Available
    }

    public class LockInfo
    {
        public string Id { get; set; }
        /// <summary>Repo-relative path, forward slashes.</summary>
        public string Path { get; set; }
        /// <summary>Server account name of the lock owner (e.g. GitHub login).</summary>
        public string Owner { get; set; }
        public DateTime LockedAtUtc { get; set; }
        public bool IsMine { get; set; }
    }

    public class VaultFile
    {
        /// <summary>Repo-relative path with forward slashes, e.g. "wing/spar.SLDPRT".</summary>
        public string Path { get; set; }
        public bool IsLockable { get; set; }
        public bool IsTracked { get; set; }
        /// <summary>Has local, uncommitted changes (content edit, or deletion).</summary>
        public bool Modified { get; set; }
        public bool Deleted { get; set; }
        /// <summary>The server has a newer version (only known after a fetch).</summary>
        public bool BehindRemote { get; set; }
        /// <summary>null when nobody holds a lock.</summary>
        public LockInfo Lock { get; set; }
        public FileState State { get; set; }

        public string FileName { get { int i = Path.LastIndexOf('/'); return i < 0 ? Path : Path.Substring(i + 1); } }
        public string Folder { get { int i = Path.LastIndexOf('/'); return i < 0 ? "" : Path.Substring(0, i); } }
    }

    public class HistoryEntry
    {
        public string Commit { get; set; }
        public string Author { get; set; }
        public DateTime Date { get; set; }
        public string Message { get; set; }
    }

    public class EnvironmentCheck
    {
        public bool GitInstalled { get; set; }
        public bool LfsInstalled { get; set; }
        public string GitVersion { get; set; }
        public string LfsVersion { get; set; }
        /// <summary>Human-readable problem, or null when everything is fine.</summary>
        public string Problem { get; set; }
    }

    /// <summary>
    /// Thrown for anything the user should see. Message = one plain-English sentence or two,
    /// no git jargon where avoidable. Details = raw git output for the "Show details" area.
    /// </summary>
    public class VaultException : Exception
    {
        public string Details { get; private set; }
        public VaultException(string message, string details = null, Exception inner = null) : base(message, inner) { Details = details; }
    }

    /// <summary>
    /// Everything the UI needs. Implemented by VaultService (real git) and DemoVaultService (fake data for screenshots).
    /// All methods are safe to await from the UI thread; implementations use ConfigureAwait(false) internally.
    /// Every git command line and its output is reported through Log (if set).
    /// </summary>
    public interface IVaultService
    {
        /// <summary>Absolute path of the working copy root.</summary>
        string RepoRoot { get; }
        /// <summary>Receives activity lines ("> git pull ...", output lines, friendly step messages). May be null.</summary>
        IProgress<string> Log { get; set; }

        /// <summary>True when .gitattributes marks the CAD extensions as lockable.</summary>
        Task<bool> IsLockingConfiguredAsync(CancellationToken ct = default(CancellationToken));
        /// <summary>Admin one-time setup: writes .gitattributes/.gitignore lines, converts current CAD files to LFS
        /// with `git add --renormalize`, commits and pushes. Does NOT rewrite history.</summary>
        Task EnableLockingAsync(CancellationToken ct = default(CancellationToken));

        /// <summary>Lists every lockable file plus any other changed/new/deleted file. When fetchRemote is true it
        /// also runs `git fetch` (for BehindRemote) and asks the server for locks; when false it reuses the last
        /// known locks/remote state (fast, offline).</summary>
        Task<IReadOnlyList<VaultFile>> GetFilesAsync(bool fetchRemote, CancellationToken ct = default(CancellationToken));

        /// <summary>Downloads everyone's latest check-ins. Keeps the user's own edits.</summary>
        Task GetLatestAsync(CancellationToken ct = default(CancellationToken));
        /// <summary>Gets the latest version of each file if needed, then locks it on the server and makes it writable.</summary>
        Task CheckOutAsync(IReadOnlyList<string> paths, CancellationToken ct = default(CancellationToken));
        /// <summary>Commits the given files (edits, new files, deletions) with the message, pushes, then unlocks them unless keepCheckedOut.</summary>
        Task CheckInAsync(IReadOnlyList<string> paths, string message, bool keepCheckedOut, CancellationToken ct = default(CancellationToken));
        /// <summary>Throws away local edits to the files and releases this user's locks on them.</summary>
        Task UndoCheckOutAsync(IReadOnlyList<string> paths, CancellationToken ct = default(CancellationToken));
        /// <summary>Admin: breaks someone else's lock (their unsaved work is not touched on their PC, but they can no longer check in).</summary>
        Task ForceUnlockAsync(LockInfo lockInfo, CancellationToken ct = default(CancellationToken));
        Task<IReadOnlyList<HistoryEntry>> GetHistoryAsync(string path, CancellationToken ct = default(CancellationToken));

        /// <summary>git config user.name / user.email (null when unset).</summary>
        Task<Tuple<string, string>> GetCommitIdentityAsync(CancellationToken ct = default(CancellationToken));
        /// <summary>Sets user.name / user.email globally.</summary>
        Task SetCommitIdentityAsync(string name, string email, CancellationToken ct = default(CancellationToken));
        /// <summary>Server account name used for locks, once known from a lock query (null before that).</summary>
        string ServerUserName { get; }
    }
}
