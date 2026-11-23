// PartLock - runs git child processes.
//
// Deliberately uses ProcessStartInfo.Arguments (not ArgumentList) so this works on
// .NET Framework 4.8 as well as .NET 8. Arguments are quoted with the Windows
// (MSVCRT) rules, which also happen to work for the Unix process launcher.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PartLock.Core
{
    /// <summary>Raw outcome of one git command.</summary>
    public class GitResult
    {
        public int ExitCode { get; set; }
        public string StdOut { get; set; }
        public string StdErr { get; set; }

        public bool Success
        {
            get { return ExitCode == 0; }
        }
    }

    /// <summary>
    /// Runs the git executable and turns its output into a GitResult. Every command is
    /// reported to the optional IProgress&lt;string&gt; before it starts, and every stderr
    /// line is reported while it runs.
    /// </summary>
    public class GitRunner
    {
        private static readonly string[] PrefixArguments = new string[]
        {
            "-c", "core.quotepath=off",
            "-c", "color.ui=never"
        };

        private readonly IProgress<string> _log;

        public GitRunner(IProgress<string> log = null)
        {
            _log = log;
        }

        /// <summary>
        /// Runs git in <paramref name="workDir"/>. When <paramref name="throwOnError"/> is true a
        /// non-zero exit code throws a VaultException with a friendly message; when it is false the
        /// caller gets the result and can inspect stderr. A missing git executable lets the
        /// Win32Exception from Process.Start escape.
        /// </summary>
        public async Task<GitResult> RunAsync(
            string workDir,
            IEnumerable<string> args,
            CancellationToken ct,
            string stdin = null,
            bool throwOnError = true,
            int timeoutMs = 600000)
        {
            if (args == null) throw new ArgumentNullException("args");
            ct.ThrowIfCancellationRequested();

            var userArgs = new List<string>(args);
            Report(BuildDisplayCommand(userArgs));

            var psi = new ProcessStartInfo();
            psi.FileName = "git";
            psi.Arguments = BuildCommandLine(userArgs);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardInput = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = new UTF8Encoding(false);
            psi.StandardErrorEncoding = new UTF8Encoding(false);

            if (!string.IsNullOrEmpty(workDir))
                psi.WorkingDirectory = workDir;

            // Never block on a hidden console prompt. Git Credential Manager can still show
            // its own sign-in window because it is a separate GUI process.
            psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
            psi.EnvironmentVariables["LC_ALL"] = "C";
            psi.EnvironmentVariables["LANG"] = "C";
            psi.EnvironmentVariables.Remove("GIT_LFS_PROGRESS");

            using (var process = new Process())
            {
                process.StartInfo = psi;
                try
                {
                    process.Start();
                }
                catch (Win32Exception)
                {
                    throw;
                }
                catch (InvalidOperationException ex)
                {
                    throw new VaultException("Couldn't start Git. Reinstall Git for Windows and try again.", ex.ToString(), ex);
                }

                Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
                Task<string> stderrTask = ReadStderrAsync(process.StandardError);
                Task stdinTask = WriteStdinAsync(process, stdin);
                Task<int> exitTask = Task.Run(() =>
                {
                    process.WaitForExit();
                    return process.ExitCode;
                });

                Task delayTask = Task.Delay(timeoutMs > 0 ? timeoutMs : Timeout.Infinite, ct);

                await Task.WhenAny(exitTask, delayTask).ConfigureAwait(false);

                bool killed = !exitTask.IsCompleted;
                bool timedOut = false;
                if (killed)
                {
                    timedOut = !ct.IsCancellationRequested;
                    TryKill(process);
                }

                // Let the readers finish, but never wait forever: a grandchild process such as
                // git-lfs can keep the pipes open after the main git process has exited or been
                // killed, and the UI must not hang on a cancelled or timed-out command.
                Task completion = Task.WhenAll(stdoutTask, stderrTask, stdinTask, exitTask);
                Task drained = await Task.WhenAny(
                    completion, Task.Delay(killed ? 3000 : 10000)).ConfigureAwait(false);
                if (drained == completion)
                {
                    try { await completion.ConfigureAwait(false); }
                    catch (Exception) { }
                }

                string stdout = stdoutTask.Status == TaskStatus.RanToCompletion
                    ? stdoutTask.Result
                    : string.Empty;
                string stderr = stderrTask.Status == TaskStatus.RanToCompletion
                    ? stderrTask.Result
                    : string.Empty;

                int exitCode = -1;
                if (exitTask.Status == TaskStatus.RanToCompletion) exitCode = exitTask.Result;

                ct.ThrowIfCancellationRequested();

                if (timedOut)
                {
                    throw new VaultException(
                        "Git took too long to finish. Check your internet connection and try again.",
                        CombineOutput(stderr, stdout));
                }

                var result = new GitResult
                {
                    ExitCode = exitCode,
                    StdOut = stdout ?? string.Empty,
                    StdErr = stderr ?? string.Empty
                };

                if (result.ExitCode != 0 && throwOnError)
                {
                    throw new VaultException(
                        FriendlyError(result.StdErr),
                        CombineOutput(result.StdErr, result.StdOut));
                }

                return result;
            }
        }

        /// <summary>Builds the full command line, including the mandatory -c overrides.</summary>
        public static string BuildCommandLine(IEnumerable<string> args)
        {
            var all = new List<string>();
            all.AddRange(PrefixArguments);
            if (args != null) all.AddRange(args);

            var sb = new StringBuilder();
            for (int i = 0; i < all.Count; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(QuoteArgument(all[i]));
            }
            return sb.ToString();
        }

        /// <summary>Quotes one argument using the Windows C runtime rules.</summary>
        public static string QuoteArgument(string argument)
        {
            if (argument == null) argument = string.Empty;

            bool needsQuotes = argument.Length == 0;
            if (!needsQuotes)
            {
                foreach (char c in argument)
                {
                    if (c == ' ' || c == '\t' || c == '"' || c == '\n' || c == '\r' || c == '\v')
                    {
                        needsQuotes = true;
                        break;
                    }
                }
            }

            if (!needsQuotes) return argument;

            var sb = new StringBuilder();
            sb.Append('"');
            int backslashes = 0;
            foreach (char c in argument)
            {
                if (c == '\\')
                {
                    backslashes++;
                }
                else if (c == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1);
                    sb.Append('"');
                    backslashes = 0;
                }
                else
                {
                    if (backslashes > 0)
                    {
                        sb.Append('\\', backslashes);
                        backslashes = 0;
                    }
                    sb.Append(c);
                }
            }
            if (backslashes > 0) sb.Append('\\', backslashes * 2);
            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>Full stderr + stdout, for VaultException.Details.</summary>
        public static string CombineOutput(string stderr, string stdout)
        {
            stderr = stderr ?? string.Empty;
            stdout = stdout ?? string.Empty;
            stderr = Redact(stderr); stdout = Redact(stdout);
            if (stdout.Length == 0) return stderr.Trim();
            if (stderr.Length == 0) return stdout.Trim();
            return stderr.TrimEnd() + Environment.NewLine + stdout;
        }

        /// <summary>Turns git's stderr into one plain-English sentence for the user.</summary>
        public static string FriendlyError(string stderr)
        {
            string text = stderr ?? string.Empty;
            string s = text.ToLowerInvariant();

            if (s.Contains("authentication failed") ||
                s.Contains("could not read username") ||
                s.Contains("could not read password") ||
                s.Contains("permission denied (publickey)") ||
                s.Contains("remote: permission to") ||
                s.Contains("403") ||
                s.Contains("access denied"))
            {
                return "Git couldn't sign in to the server. Open Git Credential Manager or check you have access to the repo.";
            }

            if (s.Contains("could not resolve host") ||
                s.Contains("could not resolve proxy") ||
                s.Contains("unable to access") ||
                s.Contains("failed to connect") ||
                s.Contains("connection timed out") ||
                s.Contains("connection refused") ||
                s.Contains("network is unreachable"))
            {
                return "Can't reach the server. Check your internet connection.";
            }

            if (s.Contains("unable to unlink") ||
                s.Contains("permission denied") ||
                s.Contains("deletion of directory") ||
                s.Contains("being used by another process") ||
                s.Contains("cannot access the file"))
            {
                return "A file is open in another program (probably SolidWorks). Close it and try again.";
            }

            // git-lfs pre-push hook refusing to upload files someone else holds the lock on.
            if (IsLockedFilePush(s))
            {
                return "You're trying to check in a file that someone else has checked out. " +
                       "Undo your changes to it (or ask them to check it in), then try again.";
            }

            if (s.Contains("rejected") ||
                s.Contains("non-fast-forward") ||
                s.Contains("fetch first") ||
                s.Contains("failed to push some refs"))
            {
                return "Someone checked in at the same time. Try again.";
            }

            if (s.Contains("lock") &&
                (s.Contains("already created") || s.Contains("already locked")))
            {
                return "That file is already checked out by someone else.";
            }

            if (s.Contains("could not apply") ||
                s.Contains("you have not concluded your merge") ||
                s.Contains("resolve all conflicts"))
            {
                return "Your changes overlap with someone else's. Undo your check out, get the latest version, and try again.";
            }

            if (s.Contains("please tell me who you are") ||
                s.Contains("unable to auto-detect email address") ||
                s.Contains("author identity unknown"))
            {
                return "Git doesn't know your name and email yet. Set them in PartLock, then try again.";
            }

            if (s.Contains("'lfs' is not a git command") || s.Contains("git: 'lfs'"))
            {
                return "Git LFS isn't installed. Reinstall Git for Windows (it includes Git LFS), then restart PartLock.";
            }

            if (s.Contains("not a git repository"))
            {
                return "This folder isn't a Git repository. Open the folder that holds the team's vault.";
            }

            if (s.Contains("no upstream branch") || s.Contains("has no upstream branch"))
            {
                return "This branch isn't connected to the server yet. Publish it first, then try again.";
            }

            string first = FirstMeaningfulLine(text);
            if (!string.IsNullOrEmpty(first)) return first;

            return "Git couldn't complete the command. See the details for more information.";
        }

        /// <summary>True when the stderr of a failed push means we should pull and retry.</summary>
        public static bool IsRejectedPush(string stderr)
        {
            string s = (stderr ?? string.Empty).ToLowerInvariant();
            if (IsLockedFilePush(s)) return false;
            return s.Contains("rejected") ||
                   s.Contains("non-fast-forward") ||
                   s.Contains("fetch first") ||
                   s.Contains("failed to push some refs");
        }

        private static bool IsLockedFilePush(string lower)
        {
            return lower.Contains("unable to push locked files") ||
                   lower.Contains("cannot update locked files") ||
                   (lower.Contains("locked") && lower.Contains("lock verification"));
        }

        /// <summary>Hides passwords/tokens embedded in URLs (https://user:token@host) before logging.</summary>
        public static string Redact(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return System.Text.RegularExpressions.Regex.Replace(text, @"(://)[^/@\s]+@", "$1***@");
        }

        private static string FirstMeaningfulLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;

            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("hint:", StringComparison.OrdinalIgnoreCase)) continue;

                if (line.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase))
                    line = line.Substring(6).Trim();
                else if (line.StartsWith("error:", StringComparison.OrdinalIgnoreCase))
                    line = line.Substring(6).Trim();

                if (line.Length > 0) return line;
            }
            return null;
        }

        private void Report(string line)
        {
            var log = _log;
            if (log == null || line == null) return;
            try { log.Report(Redact(line)); }
            catch (Exception) { }
        }

        private async Task<string> ReadStderrAsync(StreamReader reader)
        {
            var sb = new StringBuilder();
            try
            {
                string line;
                while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    sb.AppendLine(line);
                    Report(line);
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            return sb.ToString();
        }

        private static async Task WriteStdinAsync(Process process, string stdin)
        {
            if (string.IsNullOrEmpty(stdin))
            {
                try { process.StandardInput.Close(); }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
                return;
            }

            var writer = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false));
            try
            {
                await writer.WriteAsync(stdin).ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            finally
            {
                try { writer.Dispose(); }
                catch (Exception) { }
            }
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited) process.Kill();
            }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
            catch (NotSupportedException) { }
        }

        private static string BuildDisplayCommand(List<string> userArgs)
        {
            var sb = new StringBuilder("> git");
            foreach (string arg in userArgs)
            {
                sb.Append(' ');
                if (string.IsNullOrEmpty(arg))
                {
                    sb.Append("\"\"");
                }
                else if (arg.IndexOf(' ') >= 0 || arg.IndexOf('\t') >= 0 || arg.IndexOf('"') >= 0)
                {
                    sb.Append('"').Append(arg.Replace("\"", "\\\"")).Append('"');
                }
                else
                {
                    sb.Append(arg);
                }
            }
            return sb.ToString();
        }
    }
}
