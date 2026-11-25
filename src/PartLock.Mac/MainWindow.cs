using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using PartLock.Core;
using PartLock.Platform;

namespace PartLock.Mac
{
    /// <summary>The Mac main window: same actions and rules as the Windows app.</summary>
    internal sealed class MainWindow : Window
    {
        private readonly IVaultService _vault;
        private readonly GitHubAccount _account;
        private readonly string _repoName;
        private readonly List<VaultFile> _files = new List<VaultFile>();

        private Button _getLatest, _checkOut, _checkIn, _undo, _repoButton, _accountButton;
        private TextBox _search;
        private ComboBox _filter;
        private ListBox _list;
        private TextBlock _empty, _statusText, _changesText, _identityText;
        private Border _changesBanner, _lockingBanner;
        private Button _updateLink;
        private ProgressBar _progress;
        private TextBox _activity;
        private Border _activityHost;
        private Button _activityToggle;
        private DispatcherTimer _timer, _watchDebounce;
        private FileSystemWatcher _watcher;

        private bool _busy, _initialized, _identityConfirmed;
        private string _commitName, _commitEmail, _repoFullName, _repoWebUrl;
        private DateTime _lastRefreshUtc = DateTime.MinValue;
        private string _sort = "name";
        private bool _sortAscending = true;
        private MacUpdate _update;

        public MainWindow(IVaultService vault, GitHubAccount account)
        {
            _vault = vault;
            _account = account;
            string trimmed = (vault.RepoRoot ?? "vault").TrimEnd('/', '\\');
            _repoName = trimmed.Substring(Math.Max(trimmed.LastIndexOf('/'), trimmed.LastIndexOf('\\')) + 1);
            Title = "PartLock – " + _repoName;
            Width = 1120; Height = 720; MinWidth = 900; MinHeight = 560;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Ui.Background;

            Content = BuildUi();
            _vault.Log = new Progress<string>(AppendLog);

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
            _timer.Tick += async (s, e) => { if (_initialized && !_busy) await RefreshAsync("Checking for updates…", true); };
            Opened += async (s, e) => await InitializeAsync();
            Activated += async (s, e) =>
            {
                if (_initialized && !_busy && (DateTime.UtcNow - _lastRefreshUtc).TotalSeconds > 20)
                    await RefreshAsync("Refreshing…", false);
            };
            Closing += OnClosing;
        }

        // ================================================================== layout

        private Control BuildUi()
        {
            var root = new DockPanel();

            Control header = BuildHeader();
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var tip = Ui.Label("① Get Latest   →   ② Check Out the part   →   ③ Edit & save   →   ④ Check In to upload it to GitHub", 12, Ui.Muted);
            tip.Margin = new Thickness(22, 8, 22, 0);
            DockPanel.SetDock(tip, Dock.Top);
            root.Children.Add(tip);

            Control actions = BuildActions();
            DockPanel.SetDock(actions, Dock.Top);
            root.Children.Add(actions);

            _lockingBanner = Banner(Ui.BannerBg, Ui.BannerText, "This folder isn't set up for check-out locking yet.", "Set it up (team lead)",
                async () => await EnableLockingAsync(), out _);
            _lockingBanner.IsVisible = false;
            DockPanel.SetDock(_lockingBanner, Dock.Top);
            root.Children.Add(_lockingBanner);

            _changesBanner = Banner(Ui.InfoBg, Ui.InfoText, string.Empty, "Check In now", async () => await CheckInFlowAsync(), out _changesText);
            _changesBanner.IsVisible = false;
            DockPanel.SetDock(_changesBanner, Dock.Top);
            root.Children.Add(_changesBanner);

            Control status = BuildStatusBar();
            DockPanel.SetDock(status, Dock.Bottom);
            root.Children.Add(status);

            _activity = new TextBox
            {
                IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
                FontFamily = new FontFamily("Menlo, Consolas, monospace"), FontSize = 11, Background = Ui.LogBg, Height = 150
            };
            _activityHost = new Border { Child = _activity, Margin = new Thickness(20, 0, 20, 6), IsVisible = false };
            DockPanel.SetDock(_activityHost, Dock.Bottom);
            root.Children.Add(_activityHost);

            _activityToggle = new Button { Content = "Show activity ▾", Background = Brushes.Transparent, Foreground = Ui.Muted, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 16, 0) };
            _activityToggle.Click += (s, e) =>
            {
                _activityHost.IsVisible = !_activityHost.IsVisible;
                _activityToggle.Content = _activityHost.IsVisible ? "Hide activity ▴" : "Show activity ▾";
            };
            DockPanel.SetDock(_activityToggle, Dock.Bottom);
            root.Children.Add(_activityToggle);

            root.Children.Add(BuildList());
            return root;
        }

        private Border Banner(IBrush bg, IBrush fg, string text, string button, Func<Task> onClick, out TextBlock label)
        {
            label = Ui.Label(text, 13, fg);
            label.VerticalAlignment = VerticalAlignment.Center;
            Button b = Ui.Button(button, true, async () => await onClick());
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Grid.SetColumn(b, 1);
            grid.Children.Add(label);
            grid.Children.Add(b);
            return new Border { Background = bg, Padding = new Thickness(20, 8), Child = grid, Margin = new Thickness(0, 6, 0, 0) };
        }

        private Control BuildHeader()
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var brand = Ui.Row(12);
            if (Ui.Logo != null) brand.Children.Add(new Image { Source = Ui.Logo, Width = 52, Height = 52 });
            brand.Children.Add(Ui.Column(0, Ui.Label("PartLock", 24, Ui.Text, FontWeight.SemiBold), Ui.Label(_repoName, 12, Ui.Muted)));
            grid.Children.Add(brand);

            _repoButton = Ui.Button("\U0001F4C1  " + _repoName + "  ▾");
            _repoButton.Flyout = BuildRepoMenu();
            _accountButton = Ui.Button(AccountText());
            _accountButton.Flyout = BuildAccountMenu();
            var right = Ui.Row(8, _repoButton, _accountButton);
            right.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(right, 1);
            grid.Children.Add(right);
            return new Border
            {
                Background = Ui.Panel, BorderBrush = Ui.Border, BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(20, 10), Child = grid
            };
        }

        private string AccountText()
        {
            return _account == null || string.IsNullOrEmpty(_account.Login) ? "GitHub: not signed in  ▾" : "@" + _account.Login + "  ▾";
        }

        private MenuFlyout BuildRepoMenu()
        {
            var menu = new MenuFlyout();
            menu.Items.Add(Item("Open this repository on GitHub", () =>
            {
                if (!string.IsNullOrEmpty(_repoWebUrl)) MacPlatform.OpenUrl(_repoWebUrl);
            }));
            menu.Items.Add(Item("Show the folder in Finder", () => MacPlatform.OpenUrl(_vault.RepoRoot)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Switch to another repository…", async () => await SwitchRepoAsync()));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Team notifications (Discord / Slack / Teams)…", async () => await TeamNotifyAsync()));
            return menu;
        }

        private MenuFlyout BuildAccountMenu()
        {
            var menu = new MenuFlyout();
            menu.Items.Add(new MenuItem
            {
                Header = _account == null || string.IsNullOrEmpty(_account.Login)
                    ? "Not signed in to GitHub"
                    : "Signed in to GitHub as " + _account.DisplayName + " (@" + _account.Login + ")",
                IsEnabled = false
            });
            _identityText = new TextBlock { Text = "Check-ins are signed as …" };
            menu.Items.Add(new MenuItem { Header = _identityText, IsEnabled = false });
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Open my GitHub profile", () =>
            {
                if (_account != null && !string.IsNullOrEmpty(_account.Login)) MacPlatform.OpenUrl("https://github.com/" + _account.Login);
            }));
            menu.Items.Add(Item("Change name & email on check-ins…", async () => await ChangeIdentityAsync()));
            menu.Items.Add(Item("Sign out / switch GitHub account…", async () => await SignOutAsync()));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Check for PartLock updates…  (you have v" + MacUpdater.Current.ToString(3) + ")", async () => await CheckForUpdatesAsync(true)));
            return menu;
        }

        private static MenuItem Item(string header, Action onClick)
        {
            var item = new MenuItem { Header = header };
            item.Click += (s, e) => onClick();
            return item;
        }

        private Control BuildActions()
        {
            _getLatest = Ui.Button("Get Latest", true, async () => await GetLatestAsync());
            _checkOut = Ui.Button("Check Out", false, async () => await CheckOutSelectedAsync());
            _checkIn = Ui.Button("Check In…", false, async () => await CheckInFlowAsync());
            _undo = Ui.Button("Undo Check Out", false, async () => await UndoSelectedAsync());
            foreach (Button b in new[] { _getLatest, _checkOut, _checkIn, _undo }) b.MinHeight = 38;

            _search = new TextBox { Watermark = "Search file names…", Width = 230, VerticalAlignment = VerticalAlignment.Center };
            _search.TextChanged += (s, e) => ApplyFilter();
            _filter = new ComboBox
            {
                Width = 190, VerticalAlignment = VerticalAlignment.Center,
                ItemsSource = new[] { "All files", "My check-outs", "Checked out by others", "Changed / new", "Needs update" },
                SelectedIndex = 0
            };
            _filter.SelectionChanged += (s, e) => ApplyFilter();

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(20, 10, 20, 6) };
            grid.Children.Add(Ui.Row(8, _getLatest, _checkOut, _checkIn, _undo));
            var right = Ui.Row(8, _search, _filter);
            Grid.SetColumn(right, 1);
            grid.Children.Add(right);
            return grid;
        }

        private const string Columns = "260,220,210,150,110";

        private Control BuildList()
        {
            var header = new Grid { ColumnDefinitions = new ColumnDefinitions(Columns), Margin = new Thickness(12, 6, 12, 4) };
            string[] names = { "Name", "Folder", "Status", "Checked out by", "Since" };
            string[] keys = { "name", "folder", "status", "owner", "since" };
            for (int i = 0; i < names.Length; i++)
            {
                string key = keys[i];
                var b = new Button
                {
                    Content = names[i], Background = Brushes.Transparent, Padding = new Thickness(0),
                    FontWeight = FontWeight.SemiBold, Foreground = Ui.Muted, FontSize = 12
                };
                b.Click += (s, e) =>
                {
                    if (_sort == key) _sortAscending = !_sortAscending;
                    else { _sort = key; _sortAscending = true; }
                    ApplyFilter();
                };
                Grid.SetColumn(b, i);
                header.Children.Add(b);
            }

            _list = new ListBox { SelectionMode = SelectionMode.Multiple, Background = Ui.Panel };
            _list.ItemTemplate = new FuncDataTemplate<VaultFile>((f, ns) => FileRow(f), true);
            _list.SelectionChanged += (s, e) => UpdateButtons();
            _list.DoubleTapped += async (s, e) => await OpenSelectedAsync();
            _list.ContextMenu = BuildContextMenu();

            _empty = Ui.Label("No files yet.", 13, Ui.Muted);
            _empty.HorizontalAlignment = HorizontalAlignment.Center;
            _empty.VerticalAlignment = VerticalAlignment.Center;
            _empty.IsVisible = false;

            var body = new Grid();
            body.Children.Add(_list);
            body.Children.Add(_empty);

            var dock = new DockPanel();
            DockPanel.SetDock(header, Dock.Top);
            dock.Children.Add(header);
            dock.Children.Add(body);
            return new Border
            {
                Background = Ui.Panel, BorderBrush = Ui.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
                Margin = new Thickness(20, 4, 20, 4), Child = dock
            };
        }

        private Control FileRow(VaultFile f)
        {
            if (f == null) return new TextBlock();
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(Columns), Height = 26 };
            var name = new TextBlock { Text = f.FileName, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            var folder = new TextBlock { Text = f.Folder, FontSize = 12, Foreground = Ui.Muted, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            IBrush color = Ui.StatusColor(f.State);
            var pill = new Border
            {
                Background = color, Opacity = 0.95, CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 2),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = Ui.StatusText(f, _vault.ServerUserName), FontSize = 11, Foreground = Brushes.White }
            };
            var owner = new TextBlock { Text = f.Lock != null ? (f.Lock.IsMine ? "You" : f.Lock.Owner ?? string.Empty) : "—", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            var since = new TextBlock { Text = f.Lock != null ? Ui.RelativeTime(f.Lock.LockedAtUtc) : string.Empty, FontSize = 12, Foreground = Ui.Muted, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(folder, 1); Grid.SetColumn(pill, 2); Grid.SetColumn(owner, 3); Grid.SetColumn(since, 4);
            grid.Children.Add(name); grid.Children.Add(folder); grid.Children.Add(pill); grid.Children.Add(owner); grid.Children.Add(since);
            ToolTip.SetTip(grid, f.Path);
            return grid;
        }

        private ContextMenu BuildContextMenu()
        {
            var menu = new ContextMenu();
            menu.Items.Add(Item("Open", async () => await OpenSelectedAsync()));
            menu.Items.Add(Item("Show in Finder", () =>
            {
                VaultFile f = Selected().FirstOrDefault();
                if (f != null) MacPlatform.RevealInFinder(FullPath(f));
            }));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Check Out", async () => await CheckOutSelectedAsync()));
            menu.Items.Add(Item("Check In…", async () => await CheckInFlowAsync()));
            menu.Items.Add(Item("Undo Check Out", async () => await UndoSelectedAsync()));
            menu.Items.Add(Item("View History…", async () => await HistoryAsync()));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Force Unlock (admin)…", async () => await ForceUnlockAsync()));
            return menu;
        }

        private Control BuildStatusBar()
        {
            _statusText = Ui.Label("Loading…", 12, Ui.Muted);
            _statusText.VerticalAlignment = VerticalAlignment.Center;
            _progress = new ProgressBar { IsIndeterminate = true, Width = 140, Height = 6, IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
            _updateLink = new Button { Background = Brushes.Transparent, Foreground = Ui.Accent, FontSize = 12, IsVisible = false, Padding = new Thickness(6, 0) };
            _updateLink.Click += async (s, e) => await InstallUpdateAsync();
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
            Grid.SetColumn(_updateLink, 1);
            Grid.SetColumn(_progress, 2);
            grid.Children.Add(_statusText); grid.Children.Add(_updateLink); grid.Children.Add(_progress);
            return new Border { Background = Ui.Panel, BorderBrush = Ui.Border, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(18, 6), Child = grid };
        }

        // ================================================================== start-up / refresh

        private async Task InitializeAsync()
        {
            if (_initialized) return;
            SetBusy(true, "Opening the vault…");
            StartWatcher();
            try
            {
                string origin = await GitSetup.GetOriginUrlAsync(_vault.RepoRoot, CancellationToken.None);
                _repoFullName = GitSetup.ParseGitHubRepo(origin);
                _repoWebUrl = _repoFullName != null ? "https://github.com/" + _repoFullName : null;
                string label = _repoFullName ?? _repoName;
                _repoButton.Content = "\U0001F4C1  " + label + "  ▾";
                Title = "PartLock – " + label;
                ToolTip.SetTip(_repoButton, "Folder: " + _vault.RepoRoot + (origin != null ? "\nServer: " + origin : string.Empty));

                await LoadIdentityAsync();
                _lockingBanner.IsVisible = !await _vault.IsLockingConfiguredAsync();
                await ReloadAsync(true);
            }
            catch (Exception ex) { await Ui.Error(this, ex); }
            finally
            {
                _initialized = true;
                _timer.Start();
                SetBusy(false);
            }
            await CheckForUpdatesAsync(false);
        }

        private async Task LoadIdentityAsync()
        {
            try
            {
                Tuple<string, string> id = await _vault.GetCommitIdentityAsync();
                if (id != null) { _commitName = id.Item1; _commitEmail = id.Item2; }
            }
            catch (VaultException) { }
            UpdateIdentityText();
        }

        private void UpdateIdentityText()
        {
            if (_identityText == null) return;
            _identityText.Text = string.IsNullOrWhiteSpace(_commitName)
                ? "Check-ins are signed as: (not set yet)"
                : "Check-ins are signed as " + _commitName + (string.IsNullOrWhiteSpace(_commitEmail) ? string.Empty : " <" + _commitEmail + ">");
        }

        private async Task ReloadAsync(bool fetchRemote)
        {
            HashSet<string> keep = new HashSet<string>(Selected().Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
            IReadOnlyList<VaultFile> files = await _vault.GetFilesAsync(fetchRemote);
            _files.Clear();
            if (files != null) _files.AddRange(files);
            _lastRefreshUtc = DateTime.UtcNow;
            ApplyFilter(keep);
            if (!_busy) UpdateSummary();
        }

        private async Task RefreshAsync(string status, bool fetchRemote)
        {
            if (_busy) return;
            SetBusy(true, status);
            try { await ReloadAsync(fetchRemote); }
            catch (Exception ex) { await Ui.Error(this, ex); }
            finally { SetBusy(false); }
        }

        private void StartWatcher()
        {
            if (!(_vault is VaultService) || !Directory.Exists(_vault.RepoRoot)) return;
            try
            {
                _watchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
                _watchDebounce.Tick += async (s, e) =>
                {
                    if (_busy) return;
                    _watchDebounce.Stop();
                    await RefreshAsync("Refreshing…", false);
                };
                _watcher = new FileSystemWatcher(_vault.RepoRoot)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
                };
                FileSystemEventHandler changed = (s, e) => OnRepoFileChanged(e.FullPath);
                _watcher.Changed += changed; _watcher.Created += changed; _watcher.Deleted += changed;
                _watcher.Renamed += (s, e) => OnRepoFileChanged(e.FullPath);
                _watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) { ErrorLog.Write("Couldn't watch the vault folder.", ex.ToString()); }
        }

        private void OnRepoFileChanged(string fullPath)
        {
            string rel = (fullPath ?? string.Empty);
            string root = _vault.RepoRoot.TrimEnd('/');
            if (rel.StartsWith(root, StringComparison.Ordinal)) rel = rel.Substring(root.Length);
            rel = rel.TrimStart('/');
            if (rel == ".git" || rel.StartsWith(".git/", StringComparison.Ordinal)) return;
            string name = Path.GetFileName(rel);
            if (name.StartsWith("~$", StringComparison.Ordinal) || name == ".DS_Store") return;
            Dispatcher.UIThread.Post(() =>
            {
                if (_watchDebounce == null || !_initialized) return;
                _watchDebounce.Stop();
                _watchDebounce.Start();
            });
        }

        // ================================================================== actions

        private async Task RunBusyAsync(string status, Func<Task> action)
        {
            if (_busy) return;
            SetBusy(true, status);
            try { await action(); }
            catch (Exception ex) { await Ui.Error(this, ex); }
            finally { SetBusy(false); }
        }

        private async Task GetLatestAsync()
        {
            await RunBusyAsync("Getting the latest…", async () =>
            {
                await _vault.GetLatestAsync();
                await ReloadAsync(true);
            });
        }

        private TeamSender WhoAmI() { return TeamSender.From(_account, _commitName, _vault.ServerUserName); }

        private async Task CheckOutSelectedAsync()
        {
            if (_busy) return;
            List<VaultFile> targets = Selected().Where(CanCheckOut).ToList();
            if (targets.Count == 0) return;
            List<string> paths = targets.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            if (paths.Any(SolidWorksRefs.IsAssembly))
            {
                List<string> expanded = await ExpandWithSavedListsAsync(paths);
                if (expanded == null) return;
                paths = expanded;
            }

            bool done = false;
            await RunBusyAsync("Checking out " + Ui.FileCount(paths.Count) + "…", async () =>
            {
                await _vault.CheckOutAsync(paths);
                await ReloadAsync(false);
                done = true;
            });
            if (done) TeamNotify.Send(_vault.RepoRoot, TeamEvent.CheckOut, WhoAmI(), paths);
        }

        /// <summary>
        /// Macs can't ask SolidWorks, so they use the sub-assembly/part lists that Windows teammates
        /// save in the vault (.partlock/refs) when they check in an assembly.
        /// </summary>
        private async Task<List<string>> ExpandWithSavedListsAsync(List<string> paths)
        {
            var byPath = _files.ToDictionary(f => f.Path, f => f, StringComparer.OrdinalIgnoreCase);
            var chosen = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
            var offer = new List<string>();
            var taken = new List<string>();
            List<string> assemblies = paths.Where(SolidWorksRefs.IsAssembly).ToList();
            bool anyList = false;
            foreach (string assembly in assemblies)
            {
                List<string> saved = SolidWorksRefs.ReadCache(_vault.RepoRoot, assembly);
                if (saved == null)
                {
                    AppendLog("No saved part list for " + Path.GetFileName(assembly) + " yet, so only the assembly is checked out. "
                              + "(The list is saved when someone checks this assembly in from Windows with SolidWorks open.)");
                    continue;
                }
                anyList = true;
                foreach (string p in saved)
                {
                    if (chosen.Contains(p) || offer.Contains(p, StringComparer.OrdinalIgnoreCase)) continue;
                    VaultFile f;
                    if (!byPath.TryGetValue(p, out f) || !f.IsLockable) continue;
                    if (f.Lock != null && f.Lock.IsMine) continue;
                    if (f.Lock != null) { taken.Add(f.FileName + " (checked out by " + (f.Lock.Owner ?? "someone else") + ")"); continue; }
                    if (CanCheckOut(f)) offer.Add(p);
                }
            }
            if (!anyList || (offer.Count == 0 && taken.Count == 0)) return paths;
            List<string> extra = await Dialogs.AskParts(this, assemblies, offer, taken);
            if (extra == null) return null;
            var result = new List<string>(paths);
            result.AddRange(extra);
            return result;
        }

        private async Task CheckInFlowAsync()
        {
            if (_busy) return;
            List<VaultFile> mine = _files.Where(IsMineToCheckIn).OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
            if (mine.Count == 0)
            {
                await Ui.Info(this, "You have nothing to check in. Check Out a file before editing it.");
                return;
            }

            bool identityOk = false;
            await RunBusyAsync("Preparing check-in…", async () => { identityOk = await EnsureIdentityAsync(); });
            if (!identityOk) return;

            var selected = new HashSet<string>(Selected().Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
            List<string> pre = mine.Where(f => selected.Contains(f.Path)).Select(f => f.Path).ToList();
            if (pre.Count == 0) pre = mine.Select(f => f.Path).ToList();

            Dialogs.CheckInResult r = await Dialogs.AskCheckIn(this, mine, new HashSet<string>(pre), _vault.ServerUserName);
            if (r == null || r.Paths.Count == 0) return;

            var editedWithoutCheckOut = new HashSet<string>(mine.Where(f => f.State == FileState.ModifiedNotCheckedOut).Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
            List<string> needCheckOut = r.Paths.Where(editedWithoutCheckOut.Contains).ToList();

            await RunBusyAsync("Checking in " + Ui.FileCount(r.Paths.Count) + "…", async () =>
            {
                if (needCheckOut.Count > 0)
                {
                    AppendLog("Checking out " + Ui.FileCount(needCheckOut.Count) + " you edited without checking out first…");
                    await _vault.CheckOutAsync(needCheckOut);
                }
                await _vault.CheckInAsync(r.Paths, r.Message, r.KeepCheckedOut);
                TeamNotify.Send(_vault.RepoRoot, TeamEvent.CheckIn, WhoAmI(), r.Paths, r.Message);
                await ReloadAsync(false);
            });
        }

        private async Task<bool> EnsureIdentityAsync()
        {
            if (_identityConfirmed) return true;
            Tuple<string, string> id = await _vault.GetCommitIdentityAsync();
            _commitName = id == null ? null : id.Item1;
            _commitEmail = id == null ? null : id.Item2;
            if (string.IsNullOrWhiteSpace(_commitName) || string.IsNullOrWhiteSpace(_commitEmail))
            {
                Tuple<string, string> picked = await Dialogs.AskIdentity(this, _commitName, _commitEmail);
                if (picked == null) return false;
                await _vault.SetCommitIdentityAsync(picked.Item1, picked.Item2);
                _commitName = picked.Item1;
                _commitEmail = picked.Item2;
                UpdateIdentityText();
            }
            _identityConfirmed = true;
            return true;
        }

        private async Task UndoSelectedAsync()
        {
            if (_busy) return;
            List<VaultFile> targets = Selected().Where(CanUndo).ToList();
            if (targets.Count == 0) return;
            int answer = await Ui.Ask(this, "PartLock", "Throw away your changes to " + Ui.FileCount(targets.Count) + "? This can't be undone.", "Throw away", "Cancel");
            if (answer != 0) return;
            List<string> paths = targets.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            await RunBusyAsync("Undoing check-out…", async () =>
            {
                await _vault.UndoCheckOutAsync(paths);
                TeamNotify.Send(_vault.RepoRoot, TeamEvent.Undo, WhoAmI(), paths);
                await ReloadAsync(false);
            });
        }

        private async Task HistoryAsync()
        {
            VaultFile f = Selected().FirstOrDefault();
            if (f == null || _busy) return;
            IReadOnlyList<HistoryEntry> entries = null;
            await RunBusyAsync("Loading history…", async () => { entries = await _vault.GetHistoryAsync(f.Path); });
            if (entries != null) await Dialogs.ShowHistory(this, f.Path, entries);
        }

        private async Task ForceUnlockAsync()
        {
            if (_busy) return;
            List<VaultFile> targets = Selected().Where(f => f.Lock != null && !f.Lock.IsMine).ToList();
            if (targets.Count == 0) return;
            int answer = await Ui.Ask(this, "Force unlock",
                "Release " + Ui.FileCount(targets.Count) + " checked out by someone else?\n\nTheir copy isn't touched, but they won't be able to check it in any more. Only do this if they're unreachable.",
                "Force unlock", "Cancel");
            if (answer != 0) return;
            List<LockInfo> locks = targets.Select(f => f.Lock).ToList();
            await RunBusyAsync("Force unlocking…", async () =>
            {
                foreach (LockInfo l in locks) await _vault.ForceUnlockAsync(l);
                TeamNotify.Send(_vault.RepoRoot, TeamEvent.ForceUnlock, WhoAmI(),
                    locks.Select(l => l.Path + (string.IsNullOrEmpty(l.Owner) ? string.Empty : " (was " + l.Owner + "'s)")).ToList());
                await ReloadAsync(false);
            });
        }

        private async Task OpenSelectedAsync()
        {
            VaultFile f = Selected().FirstOrDefault();
            if (f == null) return;
            string full = FullPath(f);
            if (f.IsLockable && f.State == FileState.Available)
            {
                int choice = await Ui.Ask(this, "Open", f.FileName + " isn't checked out, so it opens read-only. Check it out first so you can save changes?",
                    "Check out & open", "Open read-only", "Cancel");
                if (choice == 2 || choice < 0) return;
                if (choice == 0)
                {
                    await CheckOutSelectedAsync();
                }
            }
            if (File.Exists(full)) MacPlatform.OpenFile(full);
        }

        private async Task EnableLockingAsync()
        {
            int answer = await Ui.Ask(this, "Set up locking",
                "This turns on check-out locking for the whole team: CAD files are stored with Git LFS and become lockable. "
                + "It makes one check-in and doesn't rewrite history. Only the team lead needs to do this, once.", "Set it up", "Cancel");
            if (answer != 0) return;
            await RunBusyAsync("Setting up locking…", async () =>
            {
                await _vault.EnableLockingAsync();
                _lockingBanner.IsVisible = !await _vault.IsLockingConfiguredAsync();
                await ReloadAsync(true);
            });
        }

        private async Task SwitchRepoAsync()
        {
            if (_busy) return;
            var picker = new RepoPickerWindow(_account, _vault.RepoRoot);
            await picker.ShowDialog(this);
            if (string.IsNullOrEmpty(picker.RepoRoot) || picker.RepoRoot == _vault.RepoRoot) return;
            AppSettings settings = AppSettings.Load();
            settings.UseRepo(picker.RepoRoot);
            settings.Save();
            var next = new MainWindow(new VaultService(picker.RepoRoot, null), _account);
            App.SetMainWindow(next);
            next.Show();
            _switching = true;
            Close();
        }

        private bool _switching;

        private async Task ChangeIdentityAsync()
        {
            if (_busy) return;
            Tuple<string, string> picked = await Dialogs.AskIdentity(this, _commitName, _commitEmail);
            if (picked == null) return;
            await RunBusyAsync("Saving your name…", async () =>
            {
                await _vault.SetCommitIdentityAsync(picked.Item1, picked.Item2);
                _commitName = picked.Item1; _commitEmail = picked.Item2; _identityConfirmed = true;
                UpdateIdentityText();
            });
        }

        private async Task SignOutAsync()
        {
            if (_busy) return;
            int answer = await Ui.Ask(this, "Sign out", "Sign out of GitHub on this Mac? PartLock will close; open it again to sign in (you can use a different account).", "Sign out", "Cancel");
            if (answer != 0) return;
            await RunBusyAsync("Signing out…", async () => await GitSetup.SignOutAsync(CancellationToken.None));
            _switching = true;
            Close();
        }

        private async Task TeamNotifyAsync()
        {
            if (_busy || !(_vault is VaultService)) return;
            TeamNotifyConfig config = await Dialogs.AskTeamNotify(this, _vault.RepoRoot, WhoAmI());
            if (config == null) return;
            bool identityOk = false;
            await RunBusyAsync("Preparing…", async () => { identityOk = await EnsureIdentityAsync(); });
            if (!identityOk) return;
            await RunBusyAsync("Sharing team notification settings…", async () =>
            {
                string rel = TeamNotify.Save(_vault.RepoRoot, config);
                IReadOnlyList<VaultFile> files = await _vault.GetFilesAsync(true);
                VaultFile file = files.FirstOrDefault(f => string.Equals(f.Path, rel, StringComparison.OrdinalIgnoreCase));
                if (file != null && file.Lock != null && !file.Lock.IsMine)
                    throw new VaultException("Someone has the team settings file checked out. Try again later.");
                if (file != null && file.IsTracked && file.IsLockable && file.Lock == null)
                    await _vault.CheckOutAsync(new[] { rel });
                if (file == null || file.Modified || file.State == FileState.NewFile || file.State == FileState.Changed || file.State == FileState.ModifiedNotCheckedOut)
                    await _vault.CheckInAsync(new[] { rel }, config.Enabled ? "Set up PartLock team notifications" : "Turn off PartLock team notifications", false);
                await ReloadAsync(false);
            });
        }

        // ================================================================== updates

        private async Task CheckForUpdatesAsync(bool userAsked)
        {
            try
            {
                MacUpdate update = await MacUpdater.CheckAsync(CancellationToken.None);
                _update = update;
                if (update == null)
                {
                    _updateLink.IsVisible = false;
                    if (userAsked) await Ui.Info(this, "You have the latest PartLock (v" + MacUpdater.Current.ToString(3) + ").");
                    return;
                }
                _updateLink.Content = "PartLock " + update.Tag + " is available – click to update";
                _updateLink.IsVisible = true;
                if (userAsked) await InstallUpdateAsync();
            }
            catch (Exception ex)
            {
                ErrorLog.Write("Checking for updates failed.", ex.ToString());
                if (userAsked) await Ui.Info(this, "Couldn't check for updates right now. Check your internet connection and try again.");
            }
        }

        private async Task InstallUpdateAsync()
        {
            MacUpdate update = _update;
            if (update == null || _busy) return;
            string notes = (update.Notes ?? string.Empty).Trim();
            if (notes.Length > 600) notes = notes.Substring(0, 600) + "…";
            int answer = await Ui.Ask(this, "PartLock update",
                "Update PartLock from v" + MacUpdater.Current.ToString(3) + " to " + update.Tag + " now?\n\nPartLock restarts. Your files and check-outs aren't touched."
                + (notes.Length > 0 ? "\n\nWhat's new:\n" + notes : string.Empty), "Update", "Later");
            if (answer != 0) return;

            string wrong = MacPlatform.WrongLocationMessage();
            if (wrong != null) { await Ui.Info(this, wrong); return; }

            bool restart = false, cannot = false;
            await RunBusyAsync("Updating PartLock…", async () =>
            {
                bool ok = await MacUpdater.InstallAsync(update, new Progress<string>(AppendLog), CancellationToken.None);
                restart = ok; cannot = !ok;
            });
            if (restart)
            {
                _switching = true;
                App.Quit();
            }
            else if (cannot)
            {
                await Ui.Info(this, "PartLock can't update itself where it is. The download page will open; replace PartLock in Applications with the new one.");
                MacPlatform.OpenUrl(update.PageUrl);
            }
        }

        // ================================================================== list helpers

        private void SetBusy(bool busy, string status = null)
        {
            _busy = busy;
            _progress.IsVisible = busy;
            if (busy) _statusText.Text = status ?? "Working…";
            else UpdateSummary();
            _search.IsEnabled = !busy;
            _filter.IsEnabled = !busy;
            UpdateButtons();
        }

        private void UpdateSummary()
        {
            int mine = _files.Count(f => f.State == FileState.CheckedOutByMe);
            int others = _files.Count(f => f.State == FileState.LockedByOther || f.State == FileState.ConflictLockedByOther);
            string refreshed = _lastRefreshUtc == DateTime.MinValue ? string.Empty : " · refreshed " + _lastRefreshUtc.ToLocalTime().ToString("HH:mm");
            _statusText.Text = mine + " checked out by you · " + others + " by others" + refreshed;
        }

        private void UpdateButtons()
        {
            if (_getLatest == null) return;
            List<VaultFile> sel = Selected();
            _getLatest.IsEnabled = !_busy;
            _checkOut.IsEnabled = !_busy && sel.Any(CanCheckOut);
            _undo.IsEnabled = !_busy && sel.Any(CanUndo);
            int pending = _files.Count(IsMineToCheckIn);
            int edited = _files.Count(f => IsMineToCheckIn(f) && (f.Modified || f.State == FileState.NewFile || f.State == FileState.Deleted));
            _checkIn.IsEnabled = !_busy && pending > 0;
            _checkIn.Content = pending > 0 ? "Check In (" + pending + ")…" : "Check In…";
            Ui.SetPrimary(_checkIn, edited > 0);
            Ui.SetPrimary(_getLatest, edited == 0);
            _changesBanner.IsVisible = edited > 0;
            if (edited > 0)
                _changesText.Text = "You have " + Ui.FileCount(edited) + " with changes that aren't on GitHub yet. Check In to upload them so the team gets them.";
        }

        private static bool CanCheckOut(VaultFile f)
        {
            return f != null && f.IsLockable && (f.State == FileState.Available || f.State == FileState.OutOfDate || f.State == FileState.ModifiedNotCheckedOut);
        }

        private static bool CanUndo(VaultFile f)
        {
            return f != null && (f.State == FileState.CheckedOutByMe || f.State == FileState.ModifiedNotCheckedOut || f.State == FileState.Changed || f.State == FileState.Deleted);
        }

        private static bool IsMineToCheckIn(VaultFile f)
        {
            return f != null && (f.State == FileState.CheckedOutByMe ||
                (f.State == FileState.ModifiedNotCheckedOut && (f.Lock == null || f.Lock.IsMine)) ||
                f.State == FileState.NewFile || f.State == FileState.Changed || f.State == FileState.Deleted);
        }

        private List<VaultFile> Selected()
        {
            var result = new List<VaultFile>();
            if (_list == null || _list.SelectedItems == null) return result;
            foreach (object o in _list.SelectedItems) if (o is VaultFile f) result.Add(f);
            return result;
        }

        private string FullPath(VaultFile f) { return Path.Combine(_vault.RepoRoot, f.Path.Replace('/', Path.DirectorySeparatorChar)); }

        private void ApplyFilter(ICollection<string> keep = null)
        {
            if (_list == null) return;
            if (keep == null) keep = new HashSet<string>(Selected().Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
            IEnumerable<VaultFile> view = _files;
            switch (_filter.SelectedIndex)
            {
                case 1: view = view.Where(f => f.State == FileState.CheckedOutByMe); break;
                case 2: view = view.Where(f => f.State == FileState.LockedByOther || f.State == FileState.ConflictLockedByOther); break;
                case 3: view = view.Where(f => f.State == FileState.NewFile || f.State == FileState.Changed || f.State == FileState.Deleted || f.State == FileState.ModifiedNotCheckedOut); break;
                case 4: view = view.Where(f => f.State == FileState.OutOfDate); break;
            }

            string q = (_search.Text ?? string.Empty).Trim();
            List<VaultFile> visible;
            if (q.Length > 0)
            {
                // File names first; folders/owners only when no name matches or the search is a path.
                bool pathQuery = q.Contains('/');
                List<VaultFile> all = view.ToList();
                List<VaultFile> byName = all.Where(f => NameRank(f, q) < 2).ToList();
                if (byName.Count > 0 && !pathQuery)
                    visible = Sort(byName).OrderBy(f => NameRank(f, q)).ToList();
                else
                    visible = Sort(all.Where(f => f.Path.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        (f.Lock != null && (f.Lock.Owner ?? string.Empty).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)).ToList());
            }
            else visible = Sort(view.ToList());

            _list.ItemsSource = visible;
            foreach (VaultFile f in visible) if (keep.Contains(f.Path)) _list.SelectedItems.Add(f);
            _empty.IsVisible = visible.Count == 0;
            _empty.Text = _files.Count == 0 ? "No files yet. Copy CAD files into the vault folder, then Check In." : "No files match.";
            UpdateButtons();
        }

        private static int NameRank(VaultFile f, string q)
        {
            if (f.FileName.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return 0;
            if (f.FileName.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) return 1;
            return 2;
        }

        private List<VaultFile> Sort(List<VaultFile> files)
        {
            Func<VaultFile, string> key;
            switch (_sort)
            {
                case "folder": key = f => f.Folder + "/" + f.FileName; break;
                case "status": key = f => ((int)f.State).ToString("00") + f.FileName; break;
                case "owner": key = f => (f.Lock != null ? f.Lock.Owner ?? string.Empty : "~") + f.FileName; break;
                case "since": key = f => (f.Lock != null ? f.Lock.LockedAtUtc.ToString("o") : "~") + f.FileName; break;
                default: key = f => f.FileName; break;
            }
            return (_sortAscending ? files.OrderBy(key, StringComparer.OrdinalIgnoreCase) : files.OrderByDescending(key, StringComparer.OrdinalIgnoreCase)).ToList();
        }

        private void AppendLog(string line)
        {
            if (line == null || _activity == null) return;
            string text = (_activity.Text ?? string.Empty) + line + "\n";
            if (text.Length > 60000) text = text.Substring(text.Length - 50000);
            _activity.Text = text;
            _activity.CaretIndex = text.Length;
        }

        private async void OnClosing(object sender, WindowClosingEventArgs e)
        {
            if (_busy && !_switching) { e.Cancel = true; return; }
            _timer.Stop();
            if (_watcher != null) { _watcher.EnableRaisingEvents = false; _watcher.Dispose(); _watcher = null; }
            if (_switching) return;
            int mine = _files.Count(f => f.State == FileState.CheckedOutByMe);
            if (mine > 0 && !_closingConfirmed)
            {
                e.Cancel = true;
                await Ui.Info(this, "You still have " + Ui.FileCount(mine) + " checked out. Others can't edit them until you check in.");
                _closingConfirmed = true;
                Close();
            }
        }

        private bool _closingConfirmed;
    }
}
