using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using PartLock.Core;

namespace PartLock.Mac
{
    /// <summary>
    /// "Other files" tab - the GitHub Desktop replacement for everything that isn't CAD. Changes are
    /// picked up automatically; tick, write a message, Upload. Also Incoming and History with a
    /// line-by-line view.
    /// </summary>
    internal sealed class OtherFilesView : DockPanel
    {
        private enum Mode { Changes, Incoming, History }

        private sealed class ChangeRow
        {
            public VaultFile File;
            public CheckBox Box;
        }

        private readonly IVaultService _vault;
        private readonly Func<string, Func<Task>, Task> _runBusy;
        private readonly Func<Task<bool>> _ensureIdentity;
        private readonly Func<Task> _reload;
        private readonly Action<IReadOnlyList<string>, string> _afterUpload;
        private readonly Func<Window> _owner;

        private readonly Button _changesTab, _incomingTab, _historyTab, _upload, _discard, _tickAll;
        private readonly StackPanel _changeList;
        private readonly ListBox _commits, _commitFiles;
        private readonly Grid _commitsPane;
        private readonly DockPanel _changesPane;
        private readonly TextBox _message;
        private readonly SelectableTextBlock _diff;
        private readonly TextBlock _hint;

        private Mode _mode = Mode.Changes;
        private List<VaultFile> _changed = new List<VaultFile>();
        private readonly List<ChangeRow> _rows = new List<ChangeRow>();
        private readonly HashSet<string> _unticked = new HashSet<string>(StringComparer.Ordinal);
        private IReadOnlyList<CheckIn> _incoming = new List<CheckIn>();
        private IReadOnlyList<CheckIn> _history = new List<CheckIn>();
        private string _query = string.Empty;
        private string _selected;
        private HashSet<string> _overlap = new HashSet<string>(StringComparer.Ordinal);

        public event Action CountsChanged;
        public int ChangeCount { get { return _changed.Count; } }

        public OtherFilesView(IVaultService vault, Func<string, Func<Task>, Task> runBusy, Func<Task<bool>> ensureIdentity,
            Func<Task> reload, Action<IReadOnlyList<string>, string> afterUpload, Func<Window> owner)
        {
            _vault = vault; _runBusy = runBusy; _ensureIdentity = ensureIdentity; _reload = reload; _afterUpload = afterUpload; _owner = owner;

            _changesTab = Ui.Button("My changes", true, () => SetMode(Mode.Changes));
            _incomingTab = Ui.Button("Incoming", false, () => SetMode(Mode.Incoming));
            _historyTab = Ui.Button("History", false, () => SetMode(Mode.History));
            var tabs = Ui.Row(6, _changesTab, _incomingTab, _historyTab);
            tabs.Margin = new Thickness(10, 10, 10, 6);
            DockPanel.SetDock(tabs, Dock.Top);
            Children.Add(tabs);

            // left: changes
            _changeList = new StackPanel { Spacing = 2, Margin = new Thickness(6) };
            _message = new TextBox { Watermark = "What did you change? (required)" };
            _message.TextChanged += (s, e) => UpdateButtons();
            _upload = Ui.Button("Upload changes", true, async () => await UploadAsync());
            _discard = Ui.Button("Discard…", false, async () => await DiscardAsync());
            _tickAll = Ui.Button("Tick all", false, () =>
            {
                bool all = _rows.All(r => r.Box.IsChecked == true);
                foreach (ChangeRow r in _rows) r.Box.IsChecked = !all;
            });
            var actions = Ui.Column(6, _message, Ui.Row(8, _upload, _discard, _tickAll));
            actions.Margin = new Thickness(8);
            _changesPane = new DockPanel();
            DockPanel.SetDock(actions, Dock.Bottom);
            _changesPane.Children.Add(actions);
            _changesPane.Children.Add(new ScrollViewer { Content = _changeList });

            // left: commits
            _commits = new ListBox { SelectionMode = SelectionMode.Single };
            _commits.ItemTemplate = new FuncDataTemplate<CheckIn>(CommitRow, true);
            _commits.SelectionChanged += (s, e) => ShowCommitFiles();
            _commitFiles = new ListBox { SelectionMode = SelectionMode.Single };
            _commitFiles.ItemTemplate = new FuncDataTemplate<ChangedPath>(FileRow, true);
            _commitFiles.SelectionChanged += async (s, e) => await ShowCommitDiffAsync();
            _commitsPane = new Grid { RowDefinitions = new RowDefinitions("*,4,*"), IsVisible = false };
            _commitsPane.Children.Add(_commits);
            var splitter = new GridSplitter { Background = Ui.Border, ResizeDirection = GridResizeDirection.Rows };
            Grid.SetRow(splitter, 1);
            Grid.SetRow(_commitFiles, 2);
            _commitsPane.Children.Add(splitter);
            _commitsPane.Children.Add(_commitFiles);

            var left = new Grid();
            left.Children.Add(_changesPane);
            left.Children.Add(_commitsPane);

            // right: diff
            _hint = Ui.Label(string.Empty, 12, Ui.Muted);
            _hint.Margin = new Thickness(10, 8, 10, 4);
            _diff = new SelectableTextBlock { FontFamily = new FontFamily("Menlo, Consolas, monospace"), FontSize = 12, Margin = new Thickness(10, 0, 10, 10), TextWrapping = TextWrapping.NoWrap };
            var right = new DockPanel { Background = Ui.LogBg };
            DockPanel.SetDock(_hint, Dock.Top);
            right.Children.Add(_hint);
            right.Children.Add(new ScrollViewer { Content = _diff, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });

            var body = new Grid { ColumnDefinitions = new ColumnDefinitions("440,5,*") };
            body.Children.Add(new Border { BorderBrush = Ui.Border, BorderThickness = new Thickness(0, 1, 0, 0), Child = left });
            var vsplit = new GridSplitter { Background = Ui.Border, ResizeDirection = GridResizeDirection.Columns };
            Grid.SetColumn(vsplit, 1);
            Grid.SetColumn(right, 2);
            body.Children.Add(vsplit);
            body.Children.Add(right);
            Children.Add(body);

            SetMode(Mode.Changes);
        }

        private static Control CommitRow(CheckIn c, INameScope scope)
        {
            if (c == null) return new TextBlock();
            string files = c.Files.Count + (c.Files.Count == 1 ? " file" : " files");
            return Ui.Column(0,
                new TextBlock { Text = c.Message, FontSize = 13, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = (c.Author ?? string.Empty) + " \u00B7 " + Ui.RelativeTime(c.Date) + " \u00B7 " + files, FontSize = 11, Foreground = Ui.Muted });
        }

        private static Control FileRow(ChangedPath f, INameScope scope)
        {
            if (f == null) return new TextBlock();
            return Ui.Row(8,
                new TextBlock { Text = f.KindText, Width = 64, FontSize = 11, Foreground = Ui.Muted, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = f.Path, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        }

        public static bool IsOther(VaultFile f)
        {
            return f != null && !f.IsLockable && (f.State == FileState.Changed || f.State == FileState.NewFile || f.State == FileState.Deleted);
        }

        public void SetFiles(IReadOnlyList<VaultFile> files)
        {
            _changed = (files ?? new List<VaultFile>()).Where(IsOther).OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
            _overlap = new HashSet<string>(_changed.Where(f => f.State != FileState.Deleted &&
                VaultService.HasConflictMarkers(Path.Combine(_vault.RepoRoot, f.Path.Replace('/', Path.DirectorySeparatorChar)))).Select(f => f.Path), StringComparer.Ordinal);
            FillChanges();
            UpdateTabTitles();
            CountsChanged?.Invoke();
        }

        public async Task RefreshRemoteAsync()
        {
            var real = _vault as VaultService;
            if (real == null) return;
            try
            {
                _incoming = await real.GetIncomingAsync();
                _history = await real.GetRecentCheckInsAsync(60);
            }
            catch (Exception ex) { ErrorLog.Write("Loading incoming/history failed.", ex.ToString()); }
            UpdateTabTitles();
            if (_mode != Mode.Changes) FillCommits();
        }

        public void SetQuery(string q)
        {
            _query = (q ?? string.Empty).Trim();
            FillChanges();
            if (_mode != Mode.Changes) FillCommits();
        }

        private void SetMode(Mode mode)
        {
            _mode = mode;
            Ui.SetPrimary(_changesTab, mode == Mode.Changes);
            Ui.SetPrimary(_incomingTab, mode == Mode.Incoming);
            Ui.SetPrimary(_historyTab, mode == Mode.History);
            _changesPane.IsVisible = mode == Mode.Changes;
            _commitsPane.IsVisible = mode != Mode.Changes;
            _diff.Inlines?.Clear();
            _diff.Text = string.Empty;
            _hint.Text = mode == Mode.Changes ? "Click a file to see what changed. Green lines were added, red lines removed."
                : mode == Mode.Incoming ? "Uploaded by teammates, not on this computer yet. Get Latest brings them down."
                : "Recent check-ins and uploads in this vault.";
            if (mode != Mode.Changes) FillCommits();
            else if (_selected != null) _ = ShowChangeAsync(_selected);
        }

        private void UpdateTabTitles()
        {
            _changesTab.Content = "My changes" + (_changed.Count > 0 ? " (" + _changed.Count + ")" : string.Empty);
            _incomingTab.Content = "Incoming" + (_incoming.Count > 0 ? " (" + _incoming.Count + ")" : string.Empty);
        }

        private bool Matches(string path)
        {
            return _query.Length == 0 || Path.GetFileName(path).IndexOf(_query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   (_query.Contains('/') && path.IndexOf(_query, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private void FillChanges()
        {
            _changeList.Children.Clear();
            _rows.Clear();
            foreach (VaultFile f in _changed.Where(f => Matches(f.Path)))
            {
                bool overlap = _overlap.Contains(f.Path);
                string status = overlap ? "Fix overlap \u2013 open the file" : f.State == FileState.NewFile ? "New" : f.State == FileState.Deleted ? "Deleted" : "Changed";
                var box = new CheckBox { IsChecked = !overlap && !_unticked.Contains(f.Path), IsEnabled = !overlap, VerticalAlignment = VerticalAlignment.Center };
                VaultFile file = f;
                box.IsCheckedChanged += (s, e) =>
                {
                    if (box.IsChecked == true) _unticked.Remove(file.Path); else _unticked.Add(file.Path);
                    UpdateButtons();
                };
                var name = new Button
                {
                    Background = file.Path == _selected ? Ui.InfoBg : Brushes.Transparent, Padding = new Thickness(4, 2), HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Content = Ui.Row(8,
                        new TextBlock { Text = f.FileName, FontSize = 13, VerticalAlignment = VerticalAlignment.Center },
                        new TextBlock { Text = f.Folder, FontSize = 11, Foreground = Ui.Muted, VerticalAlignment = VerticalAlignment.Center },
                        new TextBlock { Text = status, FontSize = 11, Foreground = overlap ? Ui.Danger : Ui.StatusColor(f.State), FontWeight = overlap ? FontWeight.SemiBold : FontWeight.Normal, VerticalAlignment = VerticalAlignment.Center })
                };
                name.Click += async (s, e) => { _selected = file.Path; FillChanges(); await ShowChangeAsync(file.Path); };
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
                Grid.SetColumn(name, 1);
                row.Children.Add(box);
                row.Children.Add(name);
                _changeList.Children.Add(row);
                _rows.Add(new ChangeRow { File = f, Box = box });
            }
            if (_rows.Count == 0)
                _changeList.Children.Add(Ui.Label(_changed.Count == 0 ? "No changes. Edit or add any file in the vault folder and it shows up here automatically."
                                                                      : "No changed files match the search.", 13, Ui.Muted));
            UpdateButtons();
        }

        private void FillCommits()
        {
            IReadOnlyList<CheckIn> source = _mode == Mode.Incoming ? _incoming : _history;
            var list = source.Where(c => _query.Length == 0 || c.Files.Any(f => Matches(f.Path)) ||
                                         (c.Message ?? string.Empty).IndexOf(_query, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            _commits.ItemsSource = list;
            _commitFiles.ItemsSource = null;
            if (list.Count > 0) _commits.SelectedIndex = 0;
            else _hint.Text = _mode == Mode.Incoming ? "You're up to date – nothing new from teammates." : "No check-ins yet.";
        }

        private void ShowCommitFiles()
        {
            var c = _commits.SelectedItem as CheckIn;
            _commitFiles.ItemsSource = c == null ? null : c.Files;
            if (c != null && c.Files.Count > 0) _commitFiles.SelectedIndex = 0;
        }

        private async Task ShowChangeAsync(string path)
        {
            _hint.Text = path;
            var real = _vault as VaultService;
            if (real == null) { ShowDiff("(line-by-line view isn't available in demo mode)"); return; }
            try { ShowDiff(await real.GetLocalDiffAsync(path)); }
            catch (Exception ex) { ShowDiff("(couldn't show the changes: " + ex.Message + ")"); }
        }

        private async Task ShowCommitDiffAsync()
        {
            var c = _commits.SelectedItem as CheckIn;
            var f = _commitFiles.SelectedItem as ChangedPath;
            var real = _vault as VaultService;
            if (c == null || f == null || real == null) return;
            _hint.Text = f.Path + "  ·  " + c.Author + "  ·  " + c.Message;
            try { ShowDiff(await real.GetCommitDiffAsync(c.Commit, f.Path)); }
            catch (Exception ex) { ShowDiff("(couldn't show the changes: " + ex.Message + ")"); }
        }

        private void ShowDiff(string text)
        {
            _diff.Text = null;
            var inlines = new Avalonia.Controls.Documents.InlineCollection();
            foreach (string raw in (text ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
            {
                if (raw.StartsWith("diff --git") || raw.StartsWith("index ") || raw.StartsWith("--- ") || raw.StartsWith("+++ ") ||
                    raw.StartsWith("new file mode") || raw.StartsWith("deleted file mode")) continue;
                var run = new Avalonia.Controls.Documents.Run(raw + "\n");
                if (raw.StartsWith("+")) { run.Background = Ui.Brush("#DCFCE7"); run.Foreground = Ui.Brush("#166534"); }
                else if (raw.StartsWith("-")) { run.Background = Ui.Brush("#FEE2E2"); run.Foreground = Ui.Brush("#991B1B"); }
                else if (raw.StartsWith("@@")) run.Foreground = Ui.Accent;
                inlines.Add(run);
            }
            _diff.Inlines = inlines;
        }

        private List<VaultFile> Ticked() { return _rows.Where(r => r.Box.IsChecked == true).Select(r => r.File).ToList(); }

        private void UpdateButtons()
        {
            int ticked = Ticked().Count;
            _upload.IsEnabled = ticked > 0 && (_message.Text ?? string.Empty).Trim().Length > 0;
            _upload.Content = ticked > 0 ? "Upload " + Ui.FileCount(ticked) : "Upload changes";
            _discard.IsEnabled = ticked > 0;
            _tickAll.IsVisible = _rows.Count > 1;
        }

        private async Task UploadAsync()
        {
            List<VaultFile> files = Ticked();
            string message = (_message.Text ?? string.Empty).Trim();
            if (files.Count == 0 || message.Length == 0) return;
            if (!await _ensureIdentity()) return;
            List<string> paths = files.Select(f => f.Path).ToList();
            bool done = false;
            await _runBusy("Uploading " + Ui.FileCount(paths.Count) + "…", async () =>
            {
                await _vault.CheckInAsync(paths, message, false);
                done = true;
                await _reload();
            });
            if (done)
            {
                _message.Text = string.Empty;
                _afterUpload?.Invoke(paths, message);
                await RefreshRemoteAsync();
            }
        }

        private async Task DiscardAsync()
        {
            List<VaultFile> files = Ticked();
            if (files.Count == 0) return;
            int newFiles = files.Count(f => f.State == FileState.NewFile);
            int answer = await Ui.Ask(_owner(), "Discard changes", "Throw away your changes to " + Ui.FileCount(files.Count) + "?" +
                (newFiles > 0 ? " " + newFiles + " new " + (newFiles == 1 ? "file is" : "files are") + " deleted." : string.Empty) + " This can't be undone.",
                "Throw away", "Cancel");
            if (answer != 0) return;
            await _runBusy("Discarding changes…", async () =>
            {
                List<string> tracked = files.Where(f => f.State != FileState.NewFile).Select(f => f.Path).ToList();
                if (tracked.Count > 0) await _vault.UndoCheckOutAsync(tracked);
                foreach (VaultFile f in files.Where(f => f.State == FileState.NewFile))
                {
                    string full = Path.Combine(_vault.RepoRoot, f.Path.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(full)) File.Delete(full);
                }
                await _reload();
            });
        }
    }
}
