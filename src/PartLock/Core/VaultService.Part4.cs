// PartLock - the "Other files" tab: line-by-line changes, incoming check-ins and repo history.
// Works for every file (code, .txt, MATLAB, spreadsheets...), not only lockable CAD files.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PartLock.Core
{
    /// <summary>One file in a check-in: A(dded), M(odified), D(eleted) or R(enamed).</summary>
    public class ChangedPath
    {
        public string Path { get; set; }
        public char Kind { get; set; }

        public string KindText
        {
            get
            {
                switch (Kind)
                {
                    case 'A': return "Added";
                    case 'D': return "Deleted";
                    case 'R': return "Renamed";
                    default: return "Changed";
                }
            }
        }
    }

    /// <summary>A check-in with its files (for Incoming and History).</summary>
    public class CheckIn
    {
        public string Commit { get; set; }
        public string Author { get; set; }
        public DateTime Date { get; set; }
        public string Message { get; set; }
        public List<ChangedPath> Files { get; set; } = new List<ChangedPath>();
    }

    public partial class VaultService
    {
        private const int MaxDiffBytes = 400 * 1024;

        /// <summary>
        /// What changed in one file on this computer, as unified-diff text ("+" added, "-" removed
        /// lines). New files show all their lines as added. Binary files (CAD, images) get a
        /// one-line note instead.
        /// </summary>
        public async Task<string> GetLocalDiffAsync(string path, CancellationToken ct = default(CancellationToken))
        {
            string rel = Norm(path);
            string full = FullPath(rel);

            GitResult tracked = await GitAsync(new[] { "ls-files", "--error-unmatch", "--", rel }, ct, null, false).ConfigureAwait(false);
            if (tracked.ExitCode != 0)
            {
                if (!File.Exists(full)) return "(file not found)";
                return NewFileAsDiff(full);
            }

            GitResult diff = await GitAsync(new[] { "diff", "HEAD", "--no-color", "--no-ext-diff", "--", rel }, ct, null, false).ConfigureAwait(false);
            string text = diff.StdOut ?? string.Empty;
            if (text.Length == 0) return "(no changes)";
            if (text.Contains("Binary files ") || text.Contains("version https://git-lfs.github.com/spec"))
                return "(this is a binary file like a CAD model or image, so there are no lines to compare – it will be uploaded as a whole)";
            if (text.Length > MaxDiffBytes) text = text.Substring(0, MaxDiffBytes) + "\n… (too long to show the rest)";
            return text;
        }

        /// <summary>What changed in one file in a given check-in (for Incoming / History).</summary>
        public async Task<string> GetCommitDiffAsync(string commit, string path, CancellationToken ct = default(CancellationToken))
        {
            GitResult diff = await GitAsync(new[] { "show", "--no-color", "--no-ext-diff", "--format=", commit, "--", Norm(path) }, ct, null, false).ConfigureAwait(false);
            string text = diff.StdOut ?? string.Empty;
            if (text.Length == 0) return "(no line changes to show)";
            if (text.Contains("Binary files ") || text.Contains("version https://git-lfs.github.com/spec"))
                return "(binary file – no lines to compare)";
            if (text.Length > MaxDiffBytes) text = text.Substring(0, MaxDiffBytes) + "\n… (too long to show the rest)";
            return text;
        }

        /// <summary>
        /// Check-ins on the server that this computer doesn't have yet (newest first). Uses the
        /// last fetch; call GetFilesAsync(true) or FetchAsync first for an up-to-date answer.
        /// </summary>
        public async Task<IReadOnlyList<CheckIn>> GetIncomingAsync(CancellationToken ct = default(CancellationToken))
        {
            if (await GetUpstreamAsync(ct).ConfigureAwait(false) == null) return new List<CheckIn>();
            return await LogAsync(new[] { "HEAD..@{u}" }, 200, ct).ConfigureAwait(false);
        }

        /// <summary>The most recent check-ins in the whole vault (newest first).</summary>
        public Task<IReadOnlyList<CheckIn>> GetRecentCheckInsAsync(int count = 60, CancellationToken ct = default(CancellationToken))
        {
            return LogAsync(new[] { "HEAD" }, count, ct);
        }

        /// <summary>Downloads the server's latest state without changing any files here.</summary>
        public async Task FetchAsync(CancellationToken ct = default(CancellationToken))
        {
            await GitAsync(new[] { "fetch", "--quiet" }, ct, null, false).ConfigureAwait(false);
            await RefreshBehindRemoteAsync(ct).ConfigureAwait(false);
        }

        private async Task<IReadOnlyList<CheckIn>> LogAsync(string[] range, int count, CancellationToken ct)
        {
            var args = new List<string> { "log", "-n", count.ToString(CultureInfo.InvariantCulture), "--no-color",
                "--format=%x1e%H%x1f%an%x1f%aI%x1f%s", "--name-status", "-z" };
            args.AddRange(range);
            args.Add("--");
            GitResult result = await GitAsync(args, ct, null, false).ConfigureAwait(false);
            var list = new List<CheckIn>();
            if (result.ExitCode != 0) return list;

            foreach (string record in (result.StdOut ?? string.Empty).Split('\x1e'))
            {
                if (record.Trim('\0', '\n', '\r').Length == 0) continue;
                // header line, then NUL-separated: status, path[, path2 for renames]...
                // With -z, git ends the header with a NUL (older versions: a newline).
                int end = record.IndexOfAny(new[] { '\n', '\0' });
                string header = end < 0 ? record : record.Substring(0, end);
                string rest = end < 0 ? string.Empty : record.Substring(end + 1);
                string[] h = header.Split('\x1f');
                if (h.Length < 4) continue;

                var checkIn = new CheckIn { Commit = h[0].Trim(), Author = h[1], Message = h[3].Trim('\0', '\n', '\r', ' ') };
                DateTime date;
                checkIn.Date = DateTime.TryParse(h[2], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out date)
                    ? date.ToUniversalTime() : DateTime.MinValue;

                string[] parts = rest.Trim('\n', '\0').Split('\0');
                for (int i = 0; i < parts.Length; i++)
                {
                    string status = parts[i].Trim('\n', '\r');
                    if (status.Length == 0) continue;
                    char kind = status[0];
                    if (kind == 'R' || kind == 'C')
                    {
                        if (i + 2 < parts.Length) { checkIn.Files.Add(new ChangedPath { Kind = 'R', Path = Norm(parts[i + 2]) }); }
                        i += 2;
                    }
                    else
                    {
                        if (i + 1 < parts.Length) checkIn.Files.Add(new ChangedPath { Kind = kind == 'A' || kind == 'D' ? kind : 'M', Path = Norm(parts[i + 1]) });
                        i += 1;
                    }
                }
                list.Add(checkIn);
            }
            return list;
        }

        private static string NewFileAsDiff(string full)
        {
            try
            {
                var info = new FileInfo(full);
                byte[] head = new byte[Math.Min(8000, (int)Math.Min(info.Length, 8000))];
                using (FileStream s = info.OpenRead()) s.Read(head, 0, head.Length);
                if (Array.IndexOf(head, (byte)0) >= 0)
                    return "(new binary file like a CAD model or image – it will be uploaded as a whole)";
                if (info.Length > MaxDiffBytes) return "(new file, too big to show)";

                var sb = new StringBuilder("New file\n");
                foreach (string line in File.ReadAllLines(full)) sb.Append('+').Append(line).Append('\n');
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return "(couldn't read the file: " + ex.Message + ")";
            }
        }
    }
}
