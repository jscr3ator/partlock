using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using PartLock.Core;
using PartLock.Platform;

namespace PartLock.Mac
{
    /// <summary>Pick the team vault: every GitHub repo the account can open, downloaded on demand.</summary>
    internal sealed class RepoPickerWindow : Window
    {
        private sealed class Row
        {
            public GitHubRepo Repo;
            public string Local;
            public bool Current;
        }

        private readonly GitHubAccount _account;
        private readonly string _currentRoot;
        private readonly TextBox _search;
        private readonly ListBox _list;
        private readonly TextBlock _status;
        private readonly TextBox _folder;
        private readonly TextBox _log;
        private readonly Button _open;
        private readonly List<GitHubRepo> _repos = new List<GitHubRepo>();
        private readonly Dictionary<string, string> _local = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private bool _busy;

        public string RepoRoot { get; private set; }

        public RepoPickerWindow(GitHubAccount account, string currentRoot)
        {
            _account = account;
            _currentRoot = currentRoot;
            Title = "PartLock – Choose a repository";
            Width = 800; Height = 620; MinWidth = 600; MinHeight = 460;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Ui.Panel;

            _search = new TextBox { Watermark = "Search repositories…" };
            _search.TextChanged += (s, e) => Populate();
            _list = new ListBox { SelectionMode = SelectionMode.Single, Background = Ui.Panel };
            _list.ItemTemplate = new FuncDataTemplate<Row>((r, ns) =>
            {
                if (r == null) return new TextBlock();
                var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,140,80,110"), Margin = new Thickness(2) };
                var name = new TextBlock { Text = r.Repo.FullName + (r.Current ? "  (open now)" : string.Empty), FontSize = 13, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
                var here = new TextBlock { Text = r.Local != null ? "✓ Downloaded" : "–", Foreground = r.Local != null ? Ui.Success : Ui.Muted, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
                var vis = new TextBlock { Text = r.Repo.PushedAtUtc == DateTime.MinValue ? string.Empty : (r.Repo.Private ? "Private" : "Public"), Foreground = Ui.Muted, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
                var when = new TextBlock { Text = Ui.RelativeTime(r.Repo.PushedAtUtc), Foreground = Ui.Muted, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(here, 1); Grid.SetColumn(vis, 2); Grid.SetColumn(when, 3);
                grid.Children.Add(name); grid.Children.Add(here); grid.Children.Add(vis); grid.Children.Add(when);
                if (!string.IsNullOrEmpty(r.Repo.Description)) ToolTip.SetTip(grid, r.Repo.Description);
                return grid;
            }, true);
            _list.SelectionChanged += (s, e) => UpdateButtons();
            _list.DoubleTapped += async (s, e) => await OpenSelectedAsync();

            _status = Ui.Label("Loading your repositories from GitHub…", 12, Ui.Muted);
            _folder = new TextBox { Text = MacPlatform.DocumentsFolder };
            _log = new TextBox
            {
                IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, IsVisible = false, Height = 130,
                FontFamily = new FontFamily("Menlo, Consolas, monospace"), FontSize = 11, Background = Ui.LogBg
            };
            _open = Ui.Button("Open", true, async () => await OpenSelectedAsync());

            string who = account != null && !string.IsNullOrEmpty(account.Login)
                ? "These are the GitHub repositories @" + account.Login + " can open. "
                : "These are the GitHub repositories you can open. ";
            var top = Ui.Column(8,
                Ui.Heading("Which repository do you want to work in?"),
                Ui.Label(who + "Pick your team's vault. A ✓ means it's already downloaded on this Mac.", 13, Ui.Muted),
                _search);

            var folderRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            var folderLabel = Ui.Label("Download new ones to:", 12);
            folderLabel.VerticalAlignment = VerticalAlignment.Center;
            folderLabel.Margin = new Thickness(0, 0, 8, 0);
            Button browse = Ui.Button("Choose…", false, async () => await BrowseAsync());
            browse.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(_folder, 1); Grid.SetColumn(browse, 2);
            folderRow.Children.Add(folderLabel); folderRow.Children.Add(_folder); folderRow.Children.Add(browse);

            var buttons = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var left = Ui.Row(8, Ui.Button("Paste a link…", false, async () => await PasteLinkAsync()),
                                 Ui.Button("Open a folder…", false, async () => await OpenFolderAsync()));
            var right = Ui.Row(8, Ui.Button("Cancel", false, () => { if (!_busy) Close(); }), _open);
            Grid.SetColumn(right, 1);
            buttons.Children.Add(left); buttons.Children.Add(right);

            var bottom = Ui.Column(8, _status, folderRow, _log, buttons);
            var dock = new DockPanel { Margin = new Thickness(24, 18, 24, 14) };
            DockPanel.SetDock(top, Dock.Top);
            DockPanel.SetDock(bottom, Dock.Bottom);
            top.Margin = new Thickness(0, 0, 0, 8);
            bottom.Margin = new Thickness(0, 8, 0, 0);
            dock.Children.Add(top);
            dock.Children.Add(bottom);
            dock.Children.Add(new Border { BorderBrush = Ui.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Child = _list });
            Content = dock;

            UpdateButtons();
            Opened += async (s, e) => await LoadAsync();
            Closing += (s, e) => { if (_busy) e.Cancel = true; };
        }

        private async Task LoadAsync()
        {
            AppSettings settings = AppSettings.Load();
            var folders = new List<string>(settings.KnownRepos);
            if (!string.IsNullOrEmpty(settings.RepoPath)) folders.Add(settings.RepoPath);
            if (!string.IsNullOrEmpty(_currentRoot)) folders.Add(_currentRoot);
            foreach (string folder in folders.Distinct())
            {
                if (!Directory.Exists(folder)) continue;
                string name = GitSetup.ParseGitHubRepo(await GitSetup.GetOriginUrlAsync(folder, CancellationToken.None));
                if (name != null && !_local.ContainsKey(name)) _local[name] = folder;
            }

            try
            {
                List<GitHubRepo> repos = await GitSetup.ListRepositoriesAsync(CancellationToken.None);
                _repos.Clear();
                _repos.AddRange(repos);
                _status.Text = repos.Count == 0
                    ? "Your GitHub account can't see any repositories yet. Ask your team lead to add you, then use Paste a link…"
                    : repos.Count + " repositories. Can't find yours? Ask your team lead to add your GitHub account to it.";
            }
            catch (Exception ex)
            {
                _status.Text = "Couldn't load your repositories from GitHub (" + ex.Message + "). Use Paste a link… instead.";
                ErrorLog.Write("Listing GitHub repositories failed.", ex.ToString());
            }
            foreach (KeyValuePair<string, string> local in _local)
            {
                if (_repos.Any(r => string.Equals(r.FullName, local.Key, StringComparison.OrdinalIgnoreCase))) continue;
                _repos.Add(new GitHubRepo { FullName = local.Key, Name = local.Key.Substring(local.Key.IndexOf('/') + 1), CloneUrl = "https://github.com/" + local.Key + ".git" });
            }
            Populate();
        }

        private void Populate()
        {
            string q = (_search.Text ?? string.Empty).Trim();
            string currentName = _local.Where(l => string.Equals(l.Value, _currentRoot, StringComparison.Ordinal)).Select(l => l.Key).FirstOrDefault();
            var rows = _repos
                .Where(r => q.Length == 0 || (r.FullName ?? string.Empty).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderByDescending(r => _local.ContainsKey(r.FullName ?? string.Empty))
                .ThenByDescending(r => r.PushedAtUtc)
                .Select(r =>
                {
                    string local;
                    _local.TryGetValue(r.FullName ?? string.Empty, out local);
                    return new Row { Repo = r, Local = local, Current = string.Equals(r.FullName, currentName, StringComparison.OrdinalIgnoreCase) };
                }).ToList();
            _list.ItemsSource = rows;
            UpdateButtons();
        }

        private Row Selected { get { return _list.SelectedItem as Row; } }

        private void UpdateButtons()
        {
            Row r = Selected;
            _open.IsEnabled = !_busy && r != null;
            _open.Content = r != null && r.Local == null ? "Download & open" : "Open";
        }

        private async Task OpenSelectedAsync()
        {
            Row r = Selected;
            if (_busy || r == null) return;
            if (r.Local != null && Directory.Exists(r.Local)) { Finish(r.Local); return; }
            await DownloadAsync(r.Repo.CloneUrl, r.Repo.Name);
        }

        private async Task PasteLinkAsync()
        {
            if (_busy) return;
            Window w = Ui.Dialog("Paste a link", 520, 180);
            w.SizeToContent = SizeToContent.Height;
            var box = new TextBox { Watermark = "https://github.com/your-team/cad" };
            string url = null;
            var buttons = Ui.Row(8, Ui.Button("Cancel", false, () => w.Close()), Ui.Button("Continue", true, () => { url = box.Text; w.Close(); }));
            buttons.HorizontalAlignment = HorizontalAlignment.Right;
            var c = Ui.Column(12, Ui.Label("Paste the repository link your team lead sent you:"), box, buttons);
            c.Margin = new Thickness(20);
            w.Content = c;
            await w.ShowDialog(this);
            if (string.IsNullOrWhiteSpace(url)) return;
            url = url.Trim();
            string name = GitSetup.ParseGitHubRepo(url);
            string local;
            if (name != null && _local.TryGetValue(name, out local) && Directory.Exists(local)) { Finish(local); return; }
            string folderName = name != null ? name.Substring(name.IndexOf('/') + 1) : Path.GetFileNameWithoutExtension(url.TrimEnd('/'));
            await DownloadAsync(url, folderName);
        }

        private async Task OpenFolderAsync()
        {
            if (_busy) return;
            IReadOnlyList<IStorageFolder> picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Pick the vault folder (the one that contains the .git folder)",
                AllowMultiple = false
            });
            if (picked == null || picked.Count == 0) return;
            string path = picked[0].TryGetLocalPath();
            string root = path == null ? null : VaultService.FindRepoRoot(path);
            if (root == null)
            {
                await Ui.Info(this, "That folder isn't a downloaded repository. Pick the folder that contains the .git folder, or choose a repository from the list.");
                return;
            }
            Finish(root);
        }

        private async Task BrowseAsync()
        {
            IReadOnlyList<IStorageFolder> picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Where should PartLock download repositories?",
                AllowMultiple = false
            });
            if (picked != null && picked.Count > 0 && picked[0].TryGetLocalPath() != null) _folder.Text = picked[0].TryGetLocalPath();
        }

        private async Task DownloadAsync(string url, string folderName)
        {
            string parent = (_folder.Text ?? string.Empty).Trim();
            if (parent.Length == 0) parent = MacPlatform.DocumentsFolder;
            string target = Path.GetFullPath(Path.Combine(parent, string.IsNullOrEmpty(folderName) ? "vault" : folderName));
            for (int i = 2; Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any() && i < 100; i++)
                target = Path.GetFullPath(Path.Combine(parent, folderName)) + " (" + i + ")";

            _busy = true;
            UpdateButtons();
            _log.IsVisible = true;
            _log.Text = "Downloading " + url + " to " + target + "\n";
            try
            {
                await VaultService.CloneAsync(url, target, new Progress<string>(l => { _log.Text += l + "\n"; _log.CaretIndex = _log.Text.Length; }), CancellationToken.None);
                _busy = false;
                Finish(VaultService.FindRepoRoot(target) ?? target);
            }
            catch (Exception ex)
            {
                _busy = false;
                await Ui.Error(this, ex);
            }
            finally
            {
                _busy = false;
                UpdateButtons();
            }
        }

        private void Finish(string root)
        {
            RepoRoot = root;
            _busy = false;
            Close();
        }
    }
}
