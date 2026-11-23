using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using PartLock.Core;
using PartLock.UI;

namespace PartLock
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += ThreadException;
            AppDomain.CurrentDomain.UnhandledException += UnhandledException;

            bool demo = args != null &&
                        args.Any(a => string.Equals(a, "--demo", StringComparison.OrdinalIgnoreCase));

            try
            {
                if (demo)
                {
                    RunDemo();
                }
                else
                {
                    RunReal();
                }
            }
            catch (Exception ex)
            {
                ShowError(null, "PartLock couldn't start.", ex.ToString());
            }
        }

        private static void RunDemo()
        {
            // Hidden options for docs/screenshots:  --demo --screenshot out.png [--dialog checkin|setup]
            string[] args = Environment.GetCommandLineArgs();
            string shot = ArgValue(args, "--screenshot");
            string dialog = ArgValue(args, "--dialog");
            var demo = new DemoVaultService();
            var main = new MainForm(demo, new GitHubAccount { Login = "sam-demo", Name = "Sam Demo", Id = 1 });
            if (shot != null)
            {
                var timer = new Timer { Interval = 3500 };
                timer.Tick += async delegate
                {
                    timer.Stop();
                    try
                    {
                        if (dialog == "checkin")
                        {
                            var files = (await demo.GetFilesAsync(false)).Where(f =>
                                f.State == FileState.CheckedOutByMe || f.State == FileState.NewFile ||
                                f.State == FileState.Changed || f.State == FileState.Deleted).ToList();
                            var dlg = new CheckInDialog(files, files.Select(f => f.Path).ToList(), demo.ServerUserName);
                            dlg.StartPosition = FormStartPosition.Manual;
                            dlg.Location = new System.Drawing.Point(40, 40);
                            dlg.Shown += delegate { Capture(dlg, shot, 1500, true); };
                            dlg.Show(main);
                        }
                        else if (dialog == "setup")
                        {
                            var setup = new SetupForm();
                            setup.StartPosition = FormStartPosition.Manual;
                            setup.Location = new System.Drawing.Point(40, 40);
                            setup.Shown += delegate { Capture(setup, shot, 1500, true); };
                            setup.Show(main);
                        }
                        else
                        {
                            Capture(main, shot, 0, true);
                        }
                    }
                    catch (Exception ex) { File.WriteAllText(shot + ".error.txt", ex.ToString()); Application.Exit(); }
                };
                main.Shown += delegate { timer.Start(); };
            }
            Application.Run(main);
        }

        private static string ArgValue(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }

        private static void Capture(Form form, string path, int delayMs, bool exit)
        {
            var t = new Timer { Interval = Math.Max(1, delayMs) };
            t.Tick += delegate
            {
                t.Stop();
                form.Activate();
                form.Refresh();
                Application.DoEvents();
                System.Drawing.Rectangle r = form.RectangleToScreen(form.ClientRectangle);
                using (var bmp = new System.Drawing.Bitmap(r.Width, r.Height))
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(r.Location, System.Drawing.Point.Empty, r.Size);
                    bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                }
                if (exit) Application.Exit();
            };
            t.Start();
        }

        private static void RunReal()
        {
            EnvironmentCheck environment = null;
            try
            {
                environment = VaultService.CheckEnvironmentAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                ShowError(null, "PartLock couldn't check whether Git is installed.", ex.ToString());
                return;
            }

            if (environment != null && !string.IsNullOrEmpty(environment.Problem))
            {
                // Installed but not on PATH (e.g. installed while Windows Explorer was open)?
                GitRunner.ResolveExecutable();
                try { environment = VaultService.CheckEnvironmentAsync().GetAwaiter().GetResult(); }
                catch (Exception) { }
            }

            if (environment != null && !string.IsNullOrEmpty(environment.Problem))
            {
                if (!InstallGit(environment)) return;
            }

            AppSettings settings = AppSettings.Load();
            string root = NormalizeRepo(settings.RepoPath);

            GitHubAccount account;
            if (!EnsureGitHubSignIn(root, out account)) return;

            if (root == null)
            {
                root = AskForRepo(account);
                if (root == null) return;
            }
            settings.UseRepo(root);
            settings.Save();

            Application.Run(new MainForm(new VaultService(root, null), account));
        }

        /// <summary>Installs Git for Windows (with Git LFS) for the user. False = quit.</summary>
        private static bool InstallGit(EnvironmentCheck environment)
        {
            bool gitMissing = !environment.GitInstalled;
            EnvironmentCheck after = null;
            using (var dialog = new SetupStepDialog(
                gitMissing ? "Install Git" : "Update Git",
                gitMissing ? "PartLock needs Git for Windows" : "PartLock needs Git LFS",
                (gitMissing
                    ? "Git for Windows isn't installed on this computer yet. "
                    : "Your Git for Windows is missing Git LFS, which PartLock uses to lock files. ")
                + "PartLock can install it for you now – it's free, takes a minute or two, and "
                + "includes everything PartLock needs (Git LFS and the GitHub sign-in helper).",
                gitMissing ? "Install Git" : "Update Git",
                async delegate(IProgress<string> log, System.Threading.CancellationToken ct)
                {
                    await GitSetup.InstallGitAsync(log, ct);
                    after = await VaultService.CheckEnvironmentAsync();
                    if (after != null && !string.IsNullOrEmpty(after.Problem))
                    {
                        GitRunner.ResolveExecutable();
                        after = await VaultService.CheckEnvironmentAsync();
                    }
                    if (after != null && !string.IsNullOrEmpty(after.Problem))
                        throw new VaultException("Git was installed but PartLock still can't find it. Restart PartLock (or your computer) and try again.");
                    return true;
                },
                "Windows will ask for permission to install. Prefer to do it yourself? "
                + "Get it from https://git-scm.com/download/win, then restart PartLock."))
            {
                return dialog.ShowDialog() == DialogResult.OK;
            }
        }

        /// <summary>
        /// Makes sure this computer is signed in to GitHub (via Git Credential Manager) before
        /// anything talks to the server. Skipped for vaults hosted somewhere other than GitHub.
        /// False = the user quit.
        /// </summary>
        private static bool EnsureGitHubSignIn(string repoRoot, out GitHubAccount signedIn)
        {
            signedIn = null;
            var none = System.Threading.CancellationToken.None;
            GitHubAccount account = null;
            try
            {
                if (!GitSetup.RepoUsesGitHubAsync(repoRoot, none).GetAwaiter().GetResult()) return true;
                account = GitSetup.GetSignedInAccountAsync(none).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                ErrorDialog.LogToFile("Checking GitHub sign-in failed.", ex.ToString());
            }

            if (account == null)
            {
                using (var dialog = new SetupStepDialog(
                    "Sign in",
                    "Sign in to GitHub",
                    "Your team's vault lives on GitHub, so PartLock needs you signed in before it can "
                    + "download files or check them out. Click the button below – a GitHub window or "
                    + "your browser opens; sign in there and approve access.",
                    "Sign in with GitHub",
                    async delegate(IProgress<string> log, System.Threading.CancellationToken ct)
                    {
                        account = await GitSetup.SignInAsync(log, ct);
                        return account != null;
                    },
                    "No GitHub account yet? Create a free one at https://github.com/signup and ask your "
                    + "team lead to add you to the vault repository."))
                {
                    if (dialog.ShowDialog() != DialogResult.OK || account == null) return false;
                }
            }

            try
            {
                GitSetup.FillCommitIdentityAsync(account, none).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                ErrorDialog.LogToFile("Setting name/email from GitHub failed.", ex.ToString());
            }
            signedIn = account;
            return true;
        }

        private static string AskForRepo(GitHubAccount account)
        {
            using (var picker = new RepoPickerDialog(account, null))
            {
                if (picker.ShowDialog() != DialogResult.OK) return null;
                return string.IsNullOrEmpty(picker.RepoRoot) ? null : picker.RepoRoot;
            }
        }

        /// <summary>Returns the repository root for a saved path, or null when the path is gone / not a repo.</summary>
        private static string NormalizeRepo(string path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return null;
            try
            {
                string root = VaultService.FindRepoRoot(path);
                return string.IsNullOrEmpty(root) ? null : root;
            }
            catch
            {
                return null;
            }
        }

        private static void ShowError(IWin32Window owner, string message, string details)
        {
            using (var dialog = new ErrorDialog(message, details))
            {
                if (owner == null) dialog.ShowDialog();
                else dialog.ShowDialog(owner);
            }
        }

        private static void ThreadException(object sender, System.Threading.ThreadExceptionEventArgs e)
        {
            ShowError(null, "Something went wrong.",
                e.Exception == null ? "Unknown error." : e.Exception.ToString());
        }

        private static void UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var ex = e.ExceptionObject as Exception;
            ShowError(null, "Something went wrong.",
                ex != null ? ex.ToString() : Convert.ToString(e.ExceptionObject));
        }
    }
}
