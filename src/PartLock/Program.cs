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
            Application.Run(new MainForm(new DemoVaultService()));
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
                DialogResult answer = MessageBox.Show(null,
                    environment.Problem + "\n\nOpen the Git download page now?",
                    "PartLock needs Git", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (answer == DialogResult.Yes)
                {
                    try
                    {
                        Process.Start("https://git-scm.com/download/win");
                    }
                    catch
                    {
                        // The user can find the page themselves.
                    }
                }
                return;
            }

            AppSettings settings = AppSettings.Load();
            string root = NormalizeRepo(settings.RepoPath);

            if (root == null)
            {
                root = AskForRepo(null);
                if (root == null) return;
                settings.RepoPath = root;
                settings.Save();
            }
            else if (!string.Equals(root, settings.RepoPath, StringComparison.OrdinalIgnoreCase))
            {
                settings.RepoPath = root;
                settings.Save();
            }

            Application.Run(new MainForm(new VaultService(root, null)));
        }

        private static string AskForRepo(IWin32Window owner)
        {
            using (var setup = new SetupForm())
            {
                DialogResult result = owner == null ? setup.ShowDialog() : setup.ShowDialog(owner);
                if (result != DialogResult.OK) return null;
                if (string.IsNullOrEmpty(setup.RepoRoot)) return null;
                return setup.RepoRoot;
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
