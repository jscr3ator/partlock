using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using PartLock.Core;

namespace PartLock.UI
{
    /// <summary>
    /// "Other files" tab - the GitHub Desktop replacement for everything that isn't CAD (code, .txt,
    /// MATLAB, spreadsheets...). No check-out: edits are picked up automatically and uploaded with
    /// a message. Also shows what teammates uploaded (Incoming) and the vault's History, with a
    /// line-by-line view of each change.
    /// </summary>
    public class OtherFilesPanel : UserControl
    {
        private enum View { Changes, Incoming, History }

        private readonly IVaultService _vault;
        private readonly Func<string, Func<Task>, Task> _runBusy;
        private readonly Func<Task<bool>> _ensureIdentity;
        private readonly Func<Task> _reload;
        private readonly Action<IReadOnlyList<string>, string> _afterUpload;

        private readonly Button _changesTab, _incomingTab, _historyTab;
        private readonly ListView _changes;
        private readonly ListView _commits;
        private readonly ListView _commitFiles;
        private readonly RichTextBox _diff;
        private readonly Label _hint;
        private readonly Button _upload, _discard, _selectAll;
        private readonly TextBox _message;
        private readonly Panel _changesActions;
        private readonly SplitContainer _commitSplit;

        private View _view = View.Changes;
        private List<VaultFile> _changed = new List<VaultFile>();
        private IReadOnlyList<CheckIn> _incoming = new List<CheckIn>();
        private IReadOnlyList<CheckIn> _history = new List<CheckIn>();
        private string _query = string.Empty;
        private HashSet<string> _overlap = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _filling;

        public OtherFilesPanel(IVaultService vault, Func<string, Func<Task>, Task> runBusy, Func<Task<bool>> ensureIdentity,
            Func<Task> reload, Action<IReadOnlyList<string>, string> afterUpload)
        {
            _vault = vault;
            _runBusy = runBusy;
            _ensureIdentity = ensureIdentity;
            _reload = reload;
            _afterUpload = afterUpload;
            Dock = DockStyle.Fill;
            BackColor = Theme.Panel;
            Font = Theme.Base;

            // ---- view switcher
            var tabs = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, BackColor = Theme.Panel, Padding = new Padding(8, 8, 8, 4), WrapContents = false };
            _changesTab = MakeTab("My changes", View.Changes);
            _incomingTab = MakeTab("Incoming", View.Incoming);
            _historyTab = MakeTab("History", View.History);
            tabs.Controls.AddRange(new Control[] { _changesTab, _incomingTab, _historyTab });

            // ---- left side: changes list OR commits + their files
            _changes = new ListView
            {
                View = System.Windows.Forms.View.Details, CheckBoxes = true, FullRowSelect = true, HideSelection = false,
                MultiSelect = false, BorderStyle = BorderStyle.None, Dock = DockStyle.Fill, Font = Theme.Base
            };
            _changes.Columns.Add("File", 230);
            _changes.Columns.Add("Folder", 150);
            _changes.Columns.Add("", 80);
            _changes.SelectedIndexChanged += async delegate { await ShowSelectedChangeAsync(); };
            _changes.ItemChecked += delegate(object sender, ItemCheckedEventArgs e)
            {
                if (_filling) return;
                var f = e.Item.Tag as VaultFile;
                if (f != null && e.Item.Checked && _overlap.Contains(f.Path)) { _filling = true; e.Item.Checked = false; _filling = false; }
                UpdateButtons();
            };

            _message = new TextBox { Dock = DockStyle.Top, Font = Theme.Base, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0) };
            Theme.SetCueText(_message, "What did you change? (required)");
            _message.TextChanged += delegate { UpdateButtons(); };
            _upload = Theme.CreateButton("Upload changes", ButtonKind.Primary);
            _upload.Click += async delegate { await UploadAsync(); };
            _discard = Theme.CreateButton("Discard…", ButtonKind.Outline);
            _discard.Click += async delegate { await DiscardAsync(); };
            _selectAll = Theme.CreateButton("Tick all", ButtonKind.Outline);
            _selectAll.Click += delegate
            {
                bool all = _changes.Items.Cast<ListViewItem>().All(i => i.Checked);
                _filling = true;
                foreach (ListViewItem i in _changes.Items) i.Checked = !all;
                _filling = false;
                UpdateButtons();
            };
            var buttonRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(0, 6, 0, 0) };
            _upload.Margin = new Padding(0, 0, 8, 0);
            buttonRow.Controls.AddRange(new Control[] { _upload, _discard, _selectAll });
            _changesActions = new Panel { Dock = DockStyle.Bottom, Height = 96, Padding = new Padding(8, 6, 8, 8), BackColor = Theme.Panel };
            _changesActions.Controls.Add(buttonRow);
            _changesActions.Controls.Add(_message);

            var changesHost = new Panel { Dock = DockStyle.Fill };
            changesHost.Controls.Add(_changes);
            changesHost.Controls.Add(_changesActions);

            _commits = new ListView
            {
                View = System.Windows.Forms.View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false,
                BorderStyle = BorderStyle.None, Dock = DockStyle.Fill, Font = Theme.Base
            };
            _commits.Columns.Add("Check-in", 250);
            _commits.Columns.Add("By", 110);
            _commits.Columns.Add("When", 90);
            _commits.SelectedIndexChanged += delegate { ShowCommitFiles(); };
            _commitFiles = new ListView
            {
                View = System.Windows.Forms.View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false,
                BorderStyle = BorderStyle.None, Dock = DockStyle.Fill, Font = Theme.Base
            };
            _commitFiles.Columns.Add("File", 300);
            _commitFiles.Columns.Add("", 80);
            _commitFiles.SelectedIndexChanged += async delegate { await ShowCommitDiffAsync(); };
            _commitSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, Visible = false };
            _commitSplit.Panel1.Controls.Add(_commits);
            _commitSplit.Panel2.Controls.Add(_commitFiles);

            var left = new Panel { Dock = DockStyle.Fill };
            left.Controls.Add(changesHost);
            left.Controls.Add(_commitSplit);

            // ---- right side: line-by-line changes
            _diff = new RichTextBox
            {
                Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, WordWrap = false,
                Font = Theme.Mono, BackColor = Theme.LogBackground, DetectUrls = false
            };
            _hint = new Label { Dock = DockStyle.Top, AutoSize = false, Height = 26, ForeColor = Theme.Muted, Font = Theme.Small, Padding = new Padding(6, 6, 6, 0), BackColor = Theme.LogBackground };
            var right = new Panel { Dock = DockStyle.Fill, BackColor = Theme.LogBackground };
            right.Controls.Add(_diff);
            right.Controls.Add(_hint);

            var split = new SplitContainer { Dock = DockStyle.Fill, SplitterWidth = 6, BackColor = Theme.Border };
            split.Panel1.BackColor = Theme.Panel;
            split.Panel1.Controls.Add(left);
            split.Panel2.Controls.Add(right);

            Controls.Add(split);
            Controls.Add(tabs);
            Load += delegate
            {
                try
                {
                    split.SplitterDistance = Math.Max(300, Width * 45 / 100);
                    _commitSplit.SplitterDistance = Math.Max(120, _commitSplit.Height / 2);
                }
                catch (Exception) { }
            };
            SetView(View.Changes);
        }

        /// <summary>Number of changed/new/deleted other files (for the tab title and the blue bar).</summary>
        public int ChangeCount { get { return _changed.Count; } }

        public event EventHandler CountsChanged;

        public static bool IsOther(VaultFile f)
        {
            return f != null && !f.IsLockable &&
                   (f.State == FileState.Changed || f.State == FileState.NewFile || f.State == FileState.Deleted);
        }

        /// <summary>Called after every refresh of the file list.</summary>
        public void SetFiles(IReadOnlyList<VaultFile> files)
        {
            var keepChecked = new HashSet<string>(_changes.Items.Cast<ListViewItem>().Where(i => i.Checked).Select(i => ((VaultFile)i.Tag).Path), StringComparer.OrdinalIgnoreCase);
            var known = new HashSet<string>(_changed.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
            _changed = (files ?? new List<VaultFile>()).Where(IsOther).OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
            _overlap = new HashSet<string>(_changed.Where(f => f.State != FileState.Deleted &&
                VaultService.HasConflictMarkers(Path.Combine(_vault.RepoRoot, f.Path.Replace('/', Path.DirectorySeparatorChar)))).Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
            // New changes start ticked; ones the user unticked stay unticked.
            foreach (VaultFile f in _changed) if (!known.Contains(f.Path)) keepChecked.Add(f.Path);
            FillChanges(keepChecked);
            UpdateTabTitles();
            CountsChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Reloads Incoming and History (after a fetch / Get Latest).</summary>
        public async Task RefreshRemoteAsync()
        {
            var real = _vault as VaultService;
            if (real == null) return;
            try
            {
                _incoming = await real.GetIncomingAsync();
                _history = await real.GetRecentCheckInsAsync(60);
            }
            catch (Exception ex)
            {
                ErrorDialog.LogToFile("Loading incoming/history failed.", ex.ToString());
            }
            UpdateTabTitles();
            if (_view != View.Changes) FillCommits();
        }

        public void SetQuery(string query)
        {
            _query = (query ?? string.Empty).Trim();
            FillChanges(null);
            if (_view != View.Changes) FillCommits();
        }

        // ------------------------------------------------------------------ views

        private Button MakeTab(string text, View view)
        {
            Button b = Theme.CreateButton(text, ButtonKind.Outline);
            b.MinimumSize = new Size(0, 32);
            b.Margin = new Padding(0, 0, 6, 0);
            b.Click += delegate { SetView(view); };
            return b;
        }

        private void SetView(View view)
        {
            _view = view;
            foreach (Tuple<Button, View> t in new[] { Tuple.Create(_changesTab, View.Changes), Tuple.Create(_incomingTab, View.Incoming), Tuple.Create(_historyTab, View.History) })
            {
                bool on = t.Item2 == view;
                t.Item1.BackColor = on ? Theme.AccentColor : Theme.White;
                t.Item1.ForeColor = on ? Theme.White : Theme.TextColor;
            }
            bool changes = view == View.Changes;
            _changes.Parent.Visible = changes;
            _commitSplit.Visible = !changes;
            _diff.Clear();
            _hint.Text = changes
                ? "Click a file to see what changed. Green lines were added, red lines removed."
                : view == View.Incoming ? "Uploaded by teammates, not on this computer yet. Get Latest brings them down."
                : "Recent check-ins and uploads in this vault.";
            if (!changes) FillCommits();
            UpdateButtons();
        }

        private void UpdateTabTitles()
        {
            _changesTab.Text = "My changes" + (_changed.Count > 0 ? " (" + _changed.Count + ")" : string.Empty);
            _incomingTab.Text = "Incoming" + (_incoming.Count > 0 ? " (" + _incoming.Count + ")" : string.Empty);
        }

        private bool MatchesQuery(string path)
        {
            return _query.Length == 0 || Path.GetFileName(path).IndexOf(_query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   (_query.Contains("/") && path.IndexOf(_query, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private void FillChanges(HashSet<string> keepChecked)
        {
            if (keepChecked == null)
                keepChecked = new HashSet<string>(_changes.Items.Cast<ListViewItem>().Where(i => i.Checked).Select(i => ((VaultFile)i.Tag).Path), StringComparer.OrdinalIgnoreCase);
            string selected = _changes.SelectedItems.Count > 0 ? ((VaultFile)_changes.SelectedItems[0].Tag).Path : null;
            _filling = true;
            _changes.BeginUpdate();
            _changes.Items.Clear();
            foreach (VaultFile f in _changed.Where(f => MatchesQuery(f.Path)))
            {
                bool overlap = _overlap.Contains(f.Path);
                var item = new ListViewItem(f.FileName) { Tag = f, Checked = !overlap && keepChecked.Contains(f.Path), ToolTipText = f.Path };
                item.SubItems.Add(f.Folder);
                item.SubItems.Add(overlap ? "Fix overlap" : f.State == FileState.NewFile ? "New" : f.State == FileState.Deleted ? "Deleted" : "Changed");
                item.ForeColor = overlap ? Theme.Danger : f.State == FileState.NewFile ? Theme.Purple : f.State == FileState.Deleted ? Theme.Slate : Theme.Text;
                if (overlap) item.ToolTipText = f.Path + " \u2013 a teammate changed the same lines. Open the file, keep what you want between <<<<<<< and >>>>>>>, delete the marker lines, save.";
                _changes.Items.Add(item);
                if (string.Equals(f.Path, selected, StringComparison.OrdinalIgnoreCase)) item.Selected = true;
            }
            _changes.EndUpdate();
            _filling = false;
            if (_changes.Items.Count == 0 && _view == View.Changes)
            {
                _diff.Clear();
                _hint.Text = _changed.Count == 0 ? "No changes. Edit or add any file in the vault folder and it shows up here." : "No changed files match the search.";
            }
            UpdateButtons();
        }

        private void FillCommits()
        {
            IReadOnlyList<CheckIn> source = _view == View.Incoming ? _incoming : _history;
            _commits.BeginUpdate();
            _commits.Items.Clear();
            foreach (CheckIn c in source.Where(c => _query.Length == 0 || c.Files.Any(f => MatchesQuery(f.Path)) ||
                                                   (c.Message ?? string.Empty).IndexOf(_query, StringComparison.OrdinalIgnoreCase) >= 0))
            {
                var item = new ListViewItem(c.Message ?? string.Empty) { Tag = c, ToolTipText = c.Message };
                item.SubItems.Add(c.Author ?? string.Empty);
                item.SubItems.Add(c.Date == DateTime.MinValue ? string.Empty : Theme.RelativeTime(c.Date));
                _commits.Items.Add(item);
            }
            _commits.EndUpdate();
            _commitFiles.Items.Clear();
            if (_commits.Items.Count == 0)
                _hint.Text = _view == View.Incoming ? "You're up to date – nothing new from teammates." : "No check-ins yet.";
            else _commits.Items[0].Selected = true;
        }

        private void ShowCommitFiles()
        {
            _commitFiles.BeginUpdate();
            _commitFiles.Items.Clear();
            var c = _commits.SelectedItems.Count > 0 ? _commits.SelectedItems[0].Tag as CheckIn : null;
            if (c != null)
            {
                foreach (ChangedPath f in c.Files)
                {
                    var item = new ListViewItem(f.Path) { Tag = f };
                    item.SubItems.Add(f.KindText);
                    _commitFiles.Items.Add(item);
                }
            }
            _commitFiles.EndUpdate();
            if (_commitFiles.Items.Count > 0) _commitFiles.Items[0].Selected = true;
            else _diff.Clear();
        }

        private async Task ShowSelectedChangeAsync()
        {
            var f = _changes.SelectedItems.Count > 0 ? _changes.SelectedItems[0].Tag as VaultFile : null;
            var real = _vault as VaultService;
            if (f == null) return;
            _hint.Text = f.Path;
            if (real == null) { ShowDiff("(line-by-line view isn't available in demo mode)"); return; }
            try { ShowDiff(await real.GetLocalDiffAsync(f.Path)); }
            catch (Exception ex) { ShowDiff("(couldn't show the changes: " + ex.Message + ")"); }
        }

        private async Task ShowCommitDiffAsync()
        {
            var c = _commits.SelectedItems.Count > 0 ? _commits.SelectedItems[0].Tag as CheckIn : null;
            var f = _commitFiles.SelectedItems.Count > 0 ? _commitFiles.SelectedItems[0].Tag as ChangedPath : null;
            var real = _vault as VaultService;
            if (c == null || f == null || real == null) return;
            _hint.Text = f.Path + "  ·  " + (c.Author ?? string.Empty) + "  ·  " + (c.Message ?? string.Empty);
            try { ShowDiff(await real.GetCommitDiffAsync(c.Commit, f.Path)); }
            catch (Exception ex) { ShowDiff("(couldn't show the changes: " + ex.Message + ")"); }
        }

        /// <summary>Shows unified-diff text with green (+) / red (-) / blue (@@) lines; skips git's header lines.</summary>
        private void ShowDiff(string text)
        {
            _diff.SuspendLayout();
            _diff.Clear();
            foreach (string raw in (text ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
            {
                if (raw.StartsWith("diff --git") || raw.StartsWith("index ") || raw.StartsWith("--- ") || raw.StartsWith("+++ ") ||
                    raw.StartsWith("new file mode") || raw.StartsWith("deleted file mode")) continue;
                Color back = Theme.LogBackground, fore = Theme.TextColor;
                if (raw.StartsWith("+")) { back = Color.FromArgb(220, 252, 231); fore = Color.FromArgb(22, 101, 52); }
                else if (raw.StartsWith("-")) { back = Color.FromArgb(254, 226, 226); fore = Color.FromArgb(153, 27, 27); }
                else if (raw.StartsWith("@@")) { fore = Theme.AccentColor; }
                _diff.SelectionStart = _diff.TextLength;
                _diff.SelectionBackColor = back;
                _diff.SelectionColor = fore;
                _diff.AppendText(raw + "\n");
            }
            _diff.SelectionStart = 0;
            _diff.ScrollToCaret();
            _diff.ResumeLayout();
        }

        // ------------------------------------------------------------------ actions

        private List<VaultFile> Ticked()
        {
            return _changes.Items.Cast<ListViewItem>().Where(i => i.Checked).Select(i => (VaultFile)i.Tag).ToList();
        }

        private void UpdateButtons()
        {
            bool changes = _view == View.Changes;
            _changesActions.Visible = changes;
            int ticked = Ticked().Count;
            _upload.Enabled = ticked > 0 && (_message.Text ?? string.Empty).Trim().Length > 0;
            _upload.Text = ticked > 0 ? "Upload " + ticked + (ticked == 1 ? " file" : " files") : "Upload changes";
            _discard.Enabled = ticked > 0;
            _selectAll.Visible = _changes.Items.Count > 1;
        }

        private async Task UploadAsync()
        {
            List<VaultFile> files = Ticked();
            string message = (_message.Text ?? string.Empty).Trim();
            if (files.Count == 0 || message.Length == 0) return;
            if (!await _ensureIdentity()) return;
            List<string> paths = files.Select(f => f.Path).ToList();
            bool done = false;
            await _runBusy("Uploading " + paths.Count + (paths.Count == 1 ? " file" : " files") + "…", async delegate
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
            string text = "Throw away your changes to " + files.Count + (files.Count == 1 ? " file" : " files") + "?" +
                          (newFiles > 0 ? " " + newFiles + " new " + (newFiles == 1 ? "file is" : "files are") + " deleted." : string.Empty) +
                          " This can't be undone.";
            if (MessageBox.Show(FindForm(), text, "PartLock", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            await _runBusy("Discarding changes…", async delegate
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
