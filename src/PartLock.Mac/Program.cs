using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Styling;
using PartLock.Core;

namespace PartLock.Mac
{
    internal static class Program
    {
        public static string[] Args = new string[0];

        [STAThread]
        public static int Main(string[] args)
        {
            Args = args ?? new string[0];
            AppDomain.CurrentDomain.UnhandledException += (s, e) => ErrorLog.Write("Unhandled error", Convert.ToString(e.ExceptionObject));
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(Args, ShutdownMode.OnExplicitShutdown);
        }

        public static AppBuilder BuildAvaloniaApp()
        {
            return AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .With(new MacOSPlatformOptions { ShowInDock = true });
        }
    }

    public sealed class App : Application
    {
        private static IClassicDesktopStyleApplicationLifetime Desktop
        {
            get { return Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime; }
        }

        public override void Initialize()
        {
            Name = "PartLock";
            RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
            Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
            // Compact rows in lists (Fluent's default is touch-sized).
            Styles.Add(new Avalonia.Styling.Style(x => x.OfType<ListBoxItem>())
            {
                Setters = { new Avalonia.Styling.Setter(ListBoxItem.PaddingProperty, new Thickness(12, 3)) }
            });
        }

        public override void OnFrameworkInitializationCompleted()
        {
            base.OnFrameworkInitializationCompleted();
            Dispatcher.UIThread.Post(async () =>
            {
                try { await StartAsync(); }
                catch (Exception ex)
                {
                    await Ui.Error(null, ex);
                    Quit();
                }
            });
        }

        internal static void SetMainWindow(Window w)
        {
            IClassicDesktopStyleApplicationLifetime d = Desktop;
            if (d != null) d.MainWindow = w;
        }

        internal static void Quit()
        {
            IClassicDesktopStyleApplicationLifetime d = Desktop;
            if (d != null) d.Shutdown();
        }

        private static string Arg(string name)
        {
            string[] a = Program.Args;
            for (int i = 0; i < a.Length - 1; i++) if (a[i] == name) return a[i + 1];
            return null;
        }

        private async Task StartAsync()
        {
            if (Program.Args.Contains("--demo"))
            {
                await RunDemoAsync();
                return;
            }

            // 1. Must run from Applications (not the .dmg or a translocated Downloads copy).
            string wrong = MacPlatform.WrongLocationMessage();
            if (wrong != null)
            {
                await Ui.Ask(null, "Move PartLock to Applications", wrong, "OK");
                Quit();
                return;
            }

            MacPlatform.PrepareEnvironment();

            // 2. Git (Apple's Command Line Tools). Git LFS + Credential Manager come inside the app.
            if (!await EnsureGitAsync()) { Quit(); return; }
            try { await MacPlatform.ConfigureCredentialManagerAsync(); }
            catch (Exception ex) { ErrorLog.Write("Setting up Git Credential Manager failed.", ex.ToString()); }

            // 3. Vault + GitHub sign-in.
            AppSettings settings = AppSettings.Load();
            string root = NormalizeRepo(settings.RepoPath);
            GitHubAccount account = await EnsureSignInAsync(root);
            if (account == null && await NeedsGitHubAsync(root)) { Quit(); return; }

            if (root == null)
            {
                var picker = new RepoPickerWindow(account, null);
                await Ui.ShowStandalone(picker);
                root = picker.RepoRoot;
                if (root == null) { Quit(); return; }
            }
            settings.UseRepo(root);
            settings.Save();

            var main = new MainWindow(new VaultService(root, null), account);
            main.Closed += (s, e) => { if (Desktop != null && Desktop.MainWindow == main) Quit(); };
            SetMainWindow(main);
            main.Show();
        }

        private static async Task<bool> EnsureGitAsync()
        {
            EnvironmentCheck env = await SafeCheckAsync();
            if (env != null && string.IsNullOrEmpty(env.Problem)) return true;

            if (env != null && env.GitInstalled && !env.LfsInstalled)
            {
                await Ui.Ask(null, "PartLock", "Git LFS is missing from this copy of PartLock. Download PartLock again from the releases page.", "OK");
                MacPlatform.OpenUrl("https://github.com/" + MacUpdater.Repository + "/releases/latest");
                return false;
            }

            var step = new StepWindow("Install Git", "PartLock needs Apple's Command Line Tools",
                "These include Git, which PartLock uses to talk to GitHub. They're free from Apple and take a few minutes to install.\n\n"
                + "Click Install, then click Install in the window macOS shows. When it's done, PartLock continues by itself.",
                "Install",
                async (log, ct) =>
                {
                    MacPlatform.StartCommandLineToolsInstall();
                    log.Report("Waiting for Apple's installer to finish (this window keeps checking)…");
                    for (int i = 0; i < 1800; i++) // up to 30 minutes
                    {
                        await Task.Delay(1000, ct);
                        if (MacPlatform.CommandLineToolsInstalled())
                        {
                            EnvironmentCheck after = await SafeCheckAsync();
                            if (after != null && string.IsNullOrEmpty(after.Problem)) return true;
                        }
                    }
                    throw new VaultException("The Command Line Tools still aren't installed. Run the installer again, or restart PartLock when it's done.");
                });
            await Ui.ShowStandalone(step);
            return step.Succeeded;
        }

        private static async Task<EnvironmentCheck> SafeCheckAsync()
        {
            try { return await VaultService.CheckEnvironmentAsync(); }
            catch (Exception) { return null; }
        }

        private static async Task<bool> NeedsGitHubAsync(string root)
        {
            try { return await GitSetup.RepoUsesGitHubAsync(root, CancellationToken.None); }
            catch (Exception) { return true; }
        }

        private static async Task<GitHubAccount> EnsureSignInAsync(string root)
        {
            if (!await NeedsGitHubAsync(root)) return null;
            GitHubAccount account = null;
            try { account = await GitSetup.GetSignedInAccountAsync(CancellationToken.None); }
            catch (Exception ex) { ErrorLog.Write("Checking GitHub sign-in failed.", ex.ToString()); }

            if (account == null)
            {
                var step = new StepWindow("Sign in", "Sign in to GitHub",
                    "Your team's vault lives on GitHub, so PartLock needs you signed in before it can download files or check them out. "
                    + "Click the button below – your browser opens; sign in there and approve access. PartLock saves it in your Keychain.",
                    "Sign in with GitHub",
                    async (log, ct) => { account = await GitSetup.SignInAsync(log, ct); return account != null; },
                    "No GitHub account yet? Create a free one at github.com/signup and ask your team lead to add you to the vault repository.");
                await Ui.ShowStandalone(step);
                if (!step.Succeeded) return null;
            }

            try { await GitSetup.FillCommitIdentityAsync(account, CancellationToken.None); }
            catch (Exception ex) { ErrorLog.Write("Setting name/email from GitHub failed.", ex.ToString()); }
            return account;
        }

        private static string NormalizeRepo(string path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return null;
            try { return VaultService.FindRepoRoot(path); }
            catch (Exception) { return null; }
        }

        // ------------------------------------------------------------------ demo / screenshots

        /// <summary>--demo [--screenshot out.png] [--dialog checkin|parts|picker|signin|notify]: fake data, for docs and testing.</summary>
        private async Task RunDemoAsync()
        {
            var demo = new PartLock.UI.DemoVaultService();
            var main = new MainWindow(demo, new GitHubAccount { Login = "sam-demo", Name = "Sam Demo", Id = 1 });
            main.Closed += (s, e) => Quit();
            SetMainWindow(main);
            main.Show();

            string shot = Arg("--screenshot");
            if (shot == null) return;
            await Task.Delay(4000);
            string dialog = Arg("--dialog");
            Window extra = null;
            switch (dialog)
            {
                case "checkin":
                {
                    var files = (await demo.GetFilesAsync(false)).Where(f => f.State == FileState.CheckedOutByMe || f.State == FileState.NewFile || f.State == FileState.Changed).ToList();
                    _ = Dialogs.AskCheckIn(main, files, new System.Collections.Generic.HashSet<string>(files.Select(f => f.Path)), "sam-demo");
                    break;
                }
                case "parts":
                    _ = Dialogs.AskParts(main, new System.Collections.Generic.List<string> { "CAD/Aircraft Test Assembly.SLDASM" },
                        new System.Collections.Generic.List<string> { "CAD/Wing/Wing_Assembly.SLDASM", "CAD/Wing/WING_SKIN.SLDPRT", "CAD/Tail/Tail Structure.SLDPRT" },
                        new System.Collections.Generic.List<string>());
                    break;
                case "notify":
                    _ = Dialogs.AskTeamNotify(main, Path.GetTempPath(), new PartLock.Platform.TeamSender { Name = "Sam Demo", Login = "sam-demo" });
                    break;
                case "signin":
                    extra = new StepWindow("Sign in", "Sign in to GitHub", "Your team's vault lives on GitHub, so PartLock needs you signed in before it can download files or check them out.",
                        "Sign in with GitHub", (l, c) => Task.FromResult(false), "No GitHub account yet? Create a free one at github.com/signup.");
                    extra.Show();
                    break;
            }
            await Task.Delay(2500);
            await Screenshot.CaptureAsync(shot);
            Quit();
        }
    }

    internal static class Screenshot
    {
        /// <summary>Saves every open window (topmost last) by rendering it; used for docs and tests.</summary>
        public static Task CaptureAsync(string path)
        {
            var d = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
            if (d == null) return Task.CompletedTask;
            Window top = d.Windows.LastOrDefault(w => w.IsVisible) ?? d.MainWindow;
            int i = 0;
            foreach (Window w in d.Windows.Where(w => w.IsVisible))
            {
                var size = new PixelSize((int)Math.Max(1, w.Bounds.Width), (int)Math.Max(1, w.Bounds.Height));
                using (var bmp = new Avalonia.Media.Imaging.RenderTargetBitmap(size))
                {
                    bmp.Render(w);
                    string file = w == top ? path : Path.Combine(Path.GetDirectoryName(path) ?? ".", Path.GetFileNameWithoutExtension(path) + "-" + (i++) + ".png");
                    bmp.Save(file);
                }
            }
            return Task.CompletedTask;
        }
    }
}
