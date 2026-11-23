// PartLock end-to-end harness.
//
// Runs against a throwaway Gitea server (see tests/run-e2e.sh) and exercises
// VaultService exactly the way the WinForms app does. Every check prints
// "PASS <name>" or "FAIL <name>: reason"; the process exits non-zero if any
// check failed. It keeps going after a failure where that still makes sense.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PartLock.Core;

namespace PartLock.E2E
{
    internal static class Program
    {
        private const string Password = "PartLock-test-1";
        private const string WorkRoot = "/work";
        private const string Owner = "alice";
        private const string Repo = "cad";

        private const string Spar = "wing/main-spar.SLDPRT";
        private const string Bulkhead = "fuselage/bulkhead.SLDASM";
        private const string Release = "payload/release.SLDPRT";

        private static string _giteaUrl;
        private static int _total;
        private static int _failed;

        private static string _aliceRoot;
        private static string _bobRoot;
        private static IVaultService _alice;
        private static IVaultService _bob;
        private static byte[] _bulkheadBeforeEdit;
        private static readonly List<string> _oldCommits = new List<string>();
        private static readonly Dictionary<string, string> _oldBlobs = new Dictionary<string, string>();
        private static int _commitsBeforeSetup;

        private static readonly IProgress<string> Log = new CollectingProgress();

        private static async Task<int> Main()
        {
            _giteaUrl = (Environment.GetEnvironmentVariable("GITEA_URL") ?? "").TrimEnd('/');
            if (_giteaUrl.Length == 0)
            {
                Console.Error.WriteLine("GITEA_URL is not set (e.g. GITEA_URL=http://gitea:3000).");
                return 2;
            }

            PrepareWorkRoot();
            _aliceRoot = Path.Combine(WorkRoot, "alice");
            _bobRoot = Path.Combine(WorkRoot, "bob");

            await Step("a1. alice seeds the repository", async () =>
            {
                var clone = Git(WorkRoot, "clone " + CloneUrl("alice") + " " + _aliceRoot);
                Require(clone.Code == 0, "git clone failed: " + clone.Error.Trim());
                ConfigureIdentity(_aliceRoot, "alice", "alice@example.com");

                WriteRandom(Path.Combine(_aliceRoot, "wing/main-spar.SLDPRT"), 64 * 1024, 11);
                WriteRandom(Path.Combine(_aliceRoot, "fuselage/bulkhead.SLDASM"), 32 * 1024, 12);
                File.WriteAllText(Path.Combine(_aliceRoot, "notes.txt"), "Wing team notes\n");

                GitOk(_aliceRoot, "add -A");
                GitOk(_aliceRoot, "commit -m \"Seed CAD files\"");
                // A pre-existing team repo: several revisions of CAD files stored in plain git (no LFS).
                for (int rev = 0; rev < 3; rev++)
                {
                    AppendBytes(Path.Combine(_aliceRoot, Spar), 2048);
                    GitOk(_aliceRoot, "commit -am \"Spar revision " + (char)('B' + rev) + "\"");
                    string sha = GitOk(_aliceRoot, "rev-parse HEAD").Trim();
                    _oldCommits.Add(sha);
                    _oldBlobs[sha] = GitOk(_aliceRoot, "rev-parse " + sha + ":" + Spar).Trim();
                }
                GitOk(_aliceRoot, "branch -M main");
                GitOk(_aliceRoot, "push -u origin HEAD:main");
                _commitsBeforeSetup = int.Parse(GitOk(_aliceRoot, "rev-list --count HEAD").Trim());
            });

            await Step("a2. locking is off before setup", async () =>
            {
                Require(Directory.Exists(_aliceRoot), "alice's clone is missing");
                _alice = new VaultService(_aliceRoot, Log);
                Require(!await _alice.IsLockingConfiguredAsync(),
                    "locking should be disabled before the one-time setup");
            });

            await Step("a3. EnableLockingAsync turns on LFS locking", async () =>
            {
                Require(_alice != null, "alice's vault is not available (earlier step failed)");
                await _alice.EnableLockingAsync();
                Require(await _alice.IsLockingConfiguredAsync(),
                    "locking should be enabled after the one-time setup");

                string listed = GitOk(_aliceRoot, "lfs ls-files");
                Require(listed.Contains(Spar), "git lfs ls-files should list " + Spar);
                Require(listed.Contains(Bulkhead), "git lfs ls-files should list " + Bulkhead);
            });

            await Step("a4. existing history is untouched after setup (no rewrite, no data loss)", async () =>
            {
                GitOk(_aliceRoot, "fetch origin");
                int after = int.Parse(GitOk(_aliceRoot, "rev-list --count origin/main").Trim());
                Require(after == _commitsBeforeSetup + 1,
                    "setup should add exactly one commit on the server, had " + _commitsBeforeSetup + " now " + after);
                foreach (string sha in _oldCommits)
                {
                    var anc = Git(_aliceRoot, "merge-base --is-ancestor " + sha + " origin/main");
                    Require(anc.Code == 0, "old commit " + sha + " is no longer in the server history");
                    string blob = GitOk(_aliceRoot, "rev-parse " + sha + ":" + Spar).Trim();
                    Require(blob == _oldBlobs[sha], "old file version in " + sha + " changed");
                }
                // The old full-size bytes are still retrievable from an old commit.
                string size = GitOk(_aliceRoot, "cat-file -s " + _oldCommits[0] + ":" + Spar).Trim();
                Require(long.Parse(size) > 60 * 1024, "old revision should still be the real file, size " + size);
                await Task.CompletedTask;
            });

            await Step("b1. bob clones the vault", async () =>
            {
                await VaultService.CloneAsync(CloneUrl("bob"), _bobRoot, Log, CancellationToken.None);
                ConfigureIdentity(_bobRoot, "bob", "bob@example.com");
                _bob = new VaultService(_bobRoot, Log);
            });

            await Step("b2. bob sees both CAD files as available", async () =>
            {
                Require(_bob != null, "bob's vault is not available (earlier step failed)");
                var files = await _bob.GetFilesAsync(true);

                var spar = Find(files, Spar);
                Require(spar.IsLockable, Spar + " should be lockable");
                Require(spar.State == FileState.Available,
                    Spar + " should be Available, was " + spar.State);

                var bulkhead = Find(files, Bulkhead);
                Require(bulkhead.IsLockable, Bulkhead + " should be lockable");
                Require(bulkhead.State == FileState.Available,
                    Bulkhead + " should be Available, was " + bulkhead.State);

                Require(files.All(f => !string.Equals(f.Path, "notes.txt", StringComparison.OrdinalIgnoreCase)),
                    "notes.txt must not be listed (not lockable and unchanged)");
            });

            await Step("b3. unchecked-out CAD files are read-only", async () =>
            {
                Require(IsReadOnly(BobFile(Spar)), Spar + " should be read-only before checkout");
            });

            await Step("c1. alice checks out the spar", async () =>
            {
                Require(_alice != null && _bob != null, "vaults are not available (earlier step failed)");
                await _alice.CheckOutAsync(new[] { Spar });

                var files = await _alice.GetFilesAsync(true);
                var spar = Find(files, Spar);
                Require(spar.State == FileState.CheckedOutByMe,
                    "alice should see CheckedOutByMe, saw " + spar.State);
                Require(!IsReadOnly(Path.Combine(_aliceRoot, Spar)),
                    "alice's spar should be writable after checkout");
            });

            await Step("c2. bob sees the lock as LockedByOther", async () =>
            {
                var files = await _bob.GetFilesAsync(true);
                var spar = Find(files, Spar);
                Require(spar.State == FileState.LockedByOther,
                    "bob should see LockedByOther, saw " + spar.State);
                Require(spar.Lock != null, "bob should see the lock details");
                Require(string.Equals(spar.Lock.Owner, "alice", StringComparison.OrdinalIgnoreCase),
                    "lock owner should be alice, was " + spar.Lock.Owner);
            });

            await Step("c3. bob cannot check out alice's file", async () =>
            {
                await ExpectVaultException(() => _bob.CheckOutAsync(new[] { Spar }),
                    "bob checking out a file locked by alice");
            });

            await Step("d. git-lfs refuses to push a locked file", async () =>
            {
                string path = BobFile(Spar);
                File.SetAttributes(path, FileAttributes.Normal);
                Require(!IsReadOnly(path), "test setup: bob's copy could not be made writable");
                AppendBytes(path, 64);

                var commit = Git(_bobRoot, "commit -am x");
                Require(commit.Code == 0, "bob's local commit failed: " + commit.Error.Trim());

                var push = Git(_bobRoot, "push");
                Require(push.Code != 0,
                    "bob's push must fail while alice holds the lock (lfs.locksverify=true)");
                Require((push.Error + push.Output).ToLowerInvariant().Contains("lock"),
                    "push failed, but not because of the lock: " + push.Error.Trim());
                Require(GitRunner.FriendlyError(push.Error).Contains("someone else has checked out"),
                    "friendly message for a locked push was: " + GitRunner.FriendlyError(push.Error));

                GitOk(_bobRoot, "reset --hard origin/main");
            });

            await Step("e1. alice edits and checks in the spar", async () =>
            {
                AppendBytes(Path.Combine(_aliceRoot, Spar), 1024);
                await _alice.CheckInAsync(new[] { Spar }, "Thicker spar", false);

                var files = await _alice.GetFilesAsync(true);
                var spar = Find(files, Spar);
                Require(spar.State == FileState.Available,
                    "alice should see Available after check-in, saw " + spar.State);
                Require(spar.Lock == null, "alice's lock should be released after check-in");
            });

            await Step("e2. bob is told his spar is out of date", async () =>
            {
                var files = await _bob.GetFilesAsync(true);
                var spar = Find(files, Spar);
                Require(spar.State == FileState.OutOfDate,
                    "bob should see OutOfDate, saw " + spar.State);
            });

            await Step("e3. bob gets the new spar", async () =>
            {
                await _bob.GetLatestAsync();

                Require(SameBytes(Path.Combine(_aliceRoot, Spar), BobFile(Spar)),
                    "bob's spar should have the same bytes as alice's");

                var files = await _bob.GetFilesAsync(true);
                var spar = Find(files, Spar);
                Require(spar.State == FileState.Available,
                    "bob's spar should be Available after GetLatest, was " + spar.State);
                Require(IsReadOnly(BobFile(Spar)),
                    "bob's spar should be read-only again after GetLatest");
            });

            await Step("f. bob checks out, edits, then undoes", async () =>
            {
                await _bob.CheckOutAsync(new[] { Spar });
                string path = BobFile(Spar);
                byte[] before = File.ReadAllBytes(path);
                AppendBytes(path, 512);

                await _bob.UndoCheckOutAsync(new[] { Spar });

                Require(SameBytes(before, File.ReadAllBytes(path)),
                    "undo should restore the checked-in bytes");

                var files = await _bob.GetFilesAsync(true);
                var spar = Find(files, Spar);
                Require(spar.Lock == null, "bob's lock should be released after undo");
                Require(spar.State == FileState.Available,
                    "bob's spar should be Available after undo, was " + spar.State);
            });

            await Step("g1. bob locks the bulkhead", async () =>
            {
                await _bob.CheckOutAsync(new[] { Bulkhead });
            });

            await Step("g2. alice sees bob's bulkhead lock", async () =>
            {
                var files = await _alice.GetFilesAsync(true);
                var bulkhead = Find(files, Bulkhead);
                Require(bulkhead.State == FileState.LockedByOther,
                    "alice should see LockedByOther, saw " + bulkhead.State);
                Require(bulkhead.Lock != null &&
                        string.Equals(bulkhead.Lock.Owner, "bob", StringComparison.OrdinalIgnoreCase),
                    "lock owner should be bob");
            });

            await Step("g3. alice force-unlocks bob's bulkhead", async () =>
            {
                var files = await _alice.GetFilesAsync(true);
                var bulkhead = Find(files, Bulkhead);
                Require(bulkhead.Lock != null, "alice needs the lock details to force-unlock");

                await _alice.ForceUnlockAsync(bulkhead.Lock);

                var after = await _bob.GetFilesAsync(true);
                var bobView = Find(after, Bulkhead);
                Require(bobView.Lock == null, "bulkhead should have no lock after force unlock");
            });

            await Step("h1. bob creates a new part", async () =>
            {
                string path = BobFile(Release);
                WriteRandom(path, 16 * 1024, 21);

                var files = await _bob.GetFilesAsync(false);
                var release = Find(files, Release);
                Require(release.State == FileState.NewFile,
                    "bob should see NewFile, saw " + release.State);
            });

            await Step("h2. bob checks in the new part", async () =>
            {
                await _bob.CheckInAsync(new[] { Release }, "Add release", false);
                await _alice.GetLatestAsync();

                string aliceRelease = Path.Combine(_aliceRoot, Release);
                Require(File.Exists(aliceRelease), "alice should have " + Release + " after GetLatest");
                Require(SameBytes(aliceRelease, BobFile(Release)),
                    "alice's release should match bob's bytes");
                Require(GitOk(_aliceRoot, "lfs ls-files").Contains(Release),
                    "git lfs ls-files in alice's clone should list " + Release);
            });

            await Step("i1. alice edits without checking out", async () =>
            {
                // Refresh the lock cache first: bob's lock was force-unlocked in g3.
                await _alice.GetFilesAsync(true);
                await _alice.GetLatestAsync();

                string path = Path.Combine(_aliceRoot, Bulkhead);
                _bulkheadBeforeEdit = File.ReadAllBytes(path);
                File.SetAttributes(path, FileAttributes.Normal);
                Require(!IsReadOnly(path), "test setup: alice's bulkhead could not be made writable");
                AppendBytes(path, 128);

                var files = await _alice.GetFilesAsync(false);
                var bulkhead = Find(files, Bulkhead);
                Require(bulkhead.State == FileState.ModifiedNotCheckedOut,
                    "alice should see ModifiedNotCheckedOut, saw " + bulkhead.State);
            });

            await Step("i2. checking in an un-checked-out edit fails", async () =>
            {
                await ExpectVaultException(
                    () => _alice.CheckInAsync(new[] { Bulkhead }, "should not work", false),
                    "checking in a file that was edited without checkout");
            });

            await Step("i3. undo restores the bulkhead", async () =>
            {
                Require(_bulkheadBeforeEdit != null, "no reference bytes were captured (earlier step failed)");
                await _alice.UndoCheckOutAsync(new[] { Bulkhead });
                Require(SameBytes(_bulkheadBeforeEdit, File.ReadAllBytes(Path.Combine(_aliceRoot, Bulkhead))),
                    "undo should restore the checked-in bytes");
            });

            await Step("j1. both check out different parts and edit", async () =>
            {
                await _alice.CheckOutAsync(new[] { Spar });
                await _bob.CheckOutAsync(new[] { Bulkhead });
                AppendBytes(Path.Combine(_aliceRoot, Spar), 256);
                AppendBytes(BobFile(Bulkhead), 256);
            });

            await Step("j2. alice checks in first", async () =>
            {
                await _alice.CheckInAsync(new[] { Spar }, "Spar tweak", false);
            });

            await Step("j3. bob checks in on a stale branch", async () =>
            {
                await _bob.CheckInAsync(new[] { Bulkhead }, "Bulkhead tweak", false);
            });

            await Step("j4. both have identical bytes for both files", async () =>
            {
                await _alice.GetLatestAsync();
                await _bob.GetLatestAsync();

                Require(SameBytes(Path.Combine(_aliceRoot, Spar), BobFile(Spar)),
                    "spar bytes differ between alice and bob");
                Require(SameBytes(Path.Combine(_aliceRoot, Bulkhead), BobFile(Bulkhead)),
                    "bulkhead bytes differ between alice and bob");
            });

            await Step("k1. alice checks in but keeps the lock", async () =>
            {
                await _alice.CheckOutAsync(new[] { Spar });
                AppendBytes(Path.Combine(_aliceRoot, Spar), 64);
                await _alice.CheckInAsync(new[] { Spar }, "Spar polish", true);

                var files = await _alice.GetFilesAsync(true);
                var spar = Find(files, Spar);
                Require(spar.State == FileState.CheckedOutByMe,
                    "alice should still see CheckedOutByMe, saw " + spar.State);
            });

            await Step("k2. bob still sees alice's lock", async () =>
            {
                await _bob.GetLatestAsync();

                var files = await _bob.GetFilesAsync(true);
                var spar = Find(files, Spar);
                Require(spar.State == FileState.LockedByOther,
                    "bob should see LockedByOther, saw " + spar.State);
                Require(spar.Lock != null &&
                        string.Equals(spar.Lock.Owner, "alice", StringComparison.OrdinalIgnoreCase),
                    "lock owner should be alice");
            });

            await Step("k3. alice releases the kept lock", async () =>
            {
                await _alice.UndoCheckOutAsync(new[] { Spar });

                var files = await _bob.GetFilesAsync(true);
                var spar = Find(files, Spar);
                Require(spar.Lock == null, "bob should see no lock after alice's undo");
                Require(spar.State == FileState.Available,
                    "bob should see Available, saw " + spar.State);
            });

            await Step("m. Get Latest refuses to clobber an un-checked-out edit that someone superseded", async () =>
            {
                string bobSpar = BobFile(Spar);
                File.SetAttributes(bobSpar, FileAttributes.Normal);
                AppendBytes(bobSpar, 99);
                byte[] bobBytes = File.ReadAllBytes(bobSpar);

                await _alice.CheckOutAsync(new[] { Spar });
                AppendBytes(Path.Combine(_aliceRoot, Spar), 500);
                await _alice.CheckInAsync(new[] { Spar }, "Spar fillet", false);

                await ExpectVaultException(() => _bob.GetLatestAsync(), "GetLatest over a superseded local edit");
                Require(SameBytes(File.ReadAllBytes(bobSpar), bobBytes), "bob's local edit must be left untouched");

                await _bob.UndoCheckOutAsync(new[] { Spar });
                await _bob.GetLatestAsync();
                Require(SameBytes(bobSpar, Path.Combine(_aliceRoot, Spar)), "after undo + get latest bob has alice's spar");
                var files = await _bob.GetFilesAsync(true);
                Require(Find(files, Spar).State == FileState.Available, "spar should be Available, was " + Find(files, Spar).State);
                Require(IsReadOnly(bobSpar), "spar should be read-only again after undo");
            });

            await Step("l. history is newest first and mentions the check-in", async () =>
            {
                var history = await _alice.GetHistoryAsync(Spar);
                Require(history.Count >= 3,
                    "expected at least 3 history entries, got " + history.Count);
                Require(history.Any(h => (h.Message ?? "").IndexOf("Thicker spar", StringComparison.OrdinalIgnoreCase) >= 0),
                    "history should include the 'Thicker spar' check-in");
                for (int i = 1; i < history.Count; i++)
                {
                    Require(history[i - 1].Date >= history[i].Date,
                        "history should be newest first (entry " + (i - 1) + " is older than " + i + ")");
                }
            });

            Console.WriteLine();
            Console.WriteLine((_total - _failed) + "/" + _total + " checks passed.");
            return _failed == 0 ? 0 : 1;
        }

        // ---------------------------------------------------------------- steps

        private static async Task Step(string name, Func<Task> body)
        {
            _total++;
            try
            {
                await body();
                Console.WriteLine("PASS " + name);
            }
            catch (Exception ex)
            {
                _failed++;
                Console.WriteLine("FAIL " + name + ": " + OneLine(ex));
            }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        private static async Task ExpectVaultException(Func<Task> action, string what)
        {
            try
            {
                await action();
            }
            catch (VaultException)
            {
                return;
            }
            throw new Exception(what + " should have thrown VaultException, but it did not");
        }

        private static VaultFile Find(IReadOnlyList<VaultFile> files, string path)
        {
            var match = files.FirstOrDefault(f =>
                string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                throw new Exception("'" + path + "' was not listed (listed: " +
                                    string.Join(", ", files.Select(f => f.Path)) + ")");
            }
            return match;
        }

        // -------------------------------------------------------------- helpers

        private static string CloneUrl(string user)
        {
            var uri = new Uri(_giteaUrl);
            return uri.Scheme + "://" + user + ":" + Password + "@" + uri.Authority +
                   "/" + Owner + "/" + Repo + ".git";
        }

        private static void PrepareWorkRoot()
        {
            if (!Directory.Exists(WorkRoot))
            {
                Directory.CreateDirectory(WorkRoot);
                return;
            }

            foreach (string entry in Directory.EnumerateFileSystemEntries(WorkRoot))
            {
                try
                {
                    if (Directory.Exists(entry)) Directory.Delete(entry, true);
                    else File.Delete(entry);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("WARN could not remove " + entry + ": " + ex.Message);
                }
            }
        }

        private static void ConfigureIdentity(string repoRoot, string name, string email)
        {
            GitOk(repoRoot, "config user.name " + name);
            GitOk(repoRoot, "config user.email " + email);
            // Hooks are per-clone; install them so lfs verifies locks on push.
            GitOk(repoRoot, "lfs install --local");
            // Make git-lfs check the server's locks before every push.
            GitOk(repoRoot, "config lfs.locksverify true");
        }

        private static (int Code, string Output, string Error) Git(string workingDirectory, string arguments)
        {
            var psi = new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

            using (var process = Process.Start(psi))
            {
                Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
                Task<string> stderrTask = process.StandardError.ReadToEndAsync();
                process.WaitForExit();
                return (process.ExitCode, stdoutTask.Result, stderrTask.Result);
            }
        }

        private static string GitOk(string workingDirectory, string arguments)
        {
            var result = Git(workingDirectory, arguments);
            if (result.Code != 0)
            {
                throw new Exception("git " + arguments + " failed (exit " + result.Code + "): " +
                                    result.Error.Trim() + " " + result.Output.Trim());
            }
            return result.Output;
        }

        private static void WriteRandom(string path, int byteCount, int seed)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var bytes = new byte[byteCount];
            new Random(seed).NextBytes(bytes);
            File.WriteAllBytes(path, bytes);
        }

        private static void AppendBytes(string path, int byteCount)
        {
            var bytes = new byte[byteCount];
            new Random(path.Length * 31 + byteCount).NextBytes(bytes);
            using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write))
            {
                stream.Write(bytes, 0, bytes.Length);
            }
        }

        private static bool SameBytes(string first, string second)
        {
            return SameBytes(File.ReadAllBytes(first), File.ReadAllBytes(second));
        }

        private static bool SameBytes(byte[] first, byte[] second)
        {
            if (first.Length != second.Length) return false;
            for (int i = 0; i < first.Length; i++)
            {
                if (first[i] != second[i]) return false;
            }
            return true;
        }

        private static bool IsReadOnly(string path)
        {
            return (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0;
        }

        private static string BobFile(string relativePath)
        {
            return Path.Combine(_bobRoot, relativePath);
        }

        private static string OneLine(Exception ex)
        {
            string text = (ex is VaultException ? "" : ex.GetType().Name + ": ") + (ex.Message ?? "");
            if (ex is VaultException)
            {
                var details = ((VaultException)ex).Details;
                if (!string.IsNullOrEmpty(details)) text += " | " + details;
            }
            text = text.Replace("\r", " ").Replace("\n", " ").Trim();
            if (text.Length > 600) text = text.Substring(0, 600) + "...";
            return text;
        }

        private sealed class CollectingProgress : IProgress<string>
        {
            public void Report(string value)
            {
                // Deliberately quiet: the PASS/FAIL lines are the useful output.
                // Kept as a real sink so VaultService logging never blocks.
            }
        }
    }
}
