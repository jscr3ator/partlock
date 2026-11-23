using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using PartLock.Core;

namespace PartLock.UI
{
    /// <summary>
    /// Pick which team vault (GitHub repository) to work in. Lists every repository the signed-in
    /// GitHub account can access, marks the ones already on this computer, and downloads the
    /// chosen one if needed. Also accepts a pasted link or an existing folder.
    /// </summary>
    public class RepoPickerDialog : Form
    {
        private readonly GitHubAccount _account;
        private readonly string _currentRoot;
        private readonly TextBox _search;
        private readonly ListView _list;
        private readonly Label _listStatus;
        private readonly TextBox _folderBox;
        private readonly Button _openButton;
        private readonly Button _cancelButton;
        private readonly Button _linkButton;
        private readonly Button _folderButton;
        private readonly Button _browseButton;
        private readonly TextBox _log;

        private readonly List<GitHubRepo> _repos = new List<GitHubRepo>();
        // "owner/name" (lower case) -> local folder that is already a clone of it.
        private readonly Dictionary<string, string> _local = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private CancellationTokenSource _cts;
        private bool _busy;

        public RepoPickerDialog(GitHubAccount account, string currentRoot)
        {
            _account = account;
            _currentRoot = currentRoot;

            Text = "PartLock – Choose a repository";
            Font = Theme.Base;
            BackColor = Theme.Panel;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96f, 96f);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            if (Theme.AppIcon != null) Icon = Theme.AppIcon;
            ClientSize = new Size(780, 600);
            MinimumSize = new Size(640, 480);

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 8,
                BackColor = Theme.Panel,
                Padding = new Padding(24, 18, 24, 14)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // heading
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // blurb
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // search
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 70f));   // list
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // list status
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // folder
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 30f));   // log
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // buttons

            root.Controls.Add(new Label
            {
                Text = "Which repository do you want to work in?",
                Font = Theme.Heading,
                ForeColor = Theme.Text,
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 4)
            }, 0, 0);

            string who = account != null && !string.IsNullOrEmpty(account.Login)
                ? "These are the GitHub repositories @" + account.Login + " can open. "
                : "These are the GitHub repositories you can open. ";
            root.Controls.Add(new Label
            {
                Text = who + "Pick your team's CAD vault. A ✓ means it's already downloaded on this computer.",
                Font = Theme.Base,
                ForeColor = Theme.Muted,
                AutoSize = true,
                MaximumSize = new Size(720, 0),
                Margin = new Padding(0, 0, 0, 10)
            }, 0, 1);

            _search = new TextBox
            {
                Dock = DockStyle.Fill,
                Font = Theme.Base,
                BorderStyle = BorderStyle.FixedSingle,
                Margin = new Padding(0, 0, 0, 8)
            };
            Theme.SetCueText(_search, "Search repositories…");
            _search.TextChanged += delegate { Populate(); };
            root.Controls.Add(_search, 0, 2);

            _list = new ListView
            {
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false,
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.FixedSingle,
                Font = Theme.Base,
                ShowItemToolTips = true,
                Margin = new Padding(0)
            };
            _list.Columns.Add("Repository", 300);
            _list.Columns.Add("On this computer", 130);
            _list.Columns.Add("Visibility", 90);
            _list.Columns.Add("Last change", 150);
            _list.SelectedIndexChanged += delegate { UpdateButtons(); };
            _list.DoubleClick += delegate { Open_Click(this, EventArgs.Empty); };
            root.Controls.Add(_list, 0, 3);

            _listStatus = new Label
            {
                Text = "Loading your repositories from GitHub…",
                Font = Theme.Small,
                ForeColor = Theme.Muted,
                AutoSize = true,
                Margin = new Padding(0, 4, 0, 8)
            };
            root.Controls.Add(_listStatus, 0, 4);

            var folderRow = new TableLayoutPanel
            {
                ColumnCount = 3,
                RowCount = 1,
                Dock = DockStyle.Fill,
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 8)
            };
            folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            folderRow.Controls.Add(new Label
            {
                Text = "Download new ones to:",
                Font = Theme.Small,
                ForeColor = Theme.Text,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 0, 8, 0)
            }, 0, 0);
            _folderBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Font = Theme.Base,
                BorderStyle = BorderStyle.FixedSingle,
                Text = DocumentsFolder(),
                Margin = new Padding(0, 0, 8, 0)
            };
            folderRow.Controls.Add(_folderBox, 1, 0);
            _browseButton = Theme.CreateButton("Browse…", ButtonKind.Outline);
            _browseButton.Margin = new Padding(0);
            _browseButton.Click += Browse_Click;
            folderRow.Controls.Add(_browseButton, 2, 0);
            root.Controls.Add(folderRow, 0, 5);

            _log = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                WordWrap = false,
                ScrollBars = ScrollBars.Both,
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.FixedSingle,
                Font = Theme.Mono,
                BackColor = Theme.LogBackground,
                ForeColor = Theme.Text,
                Visible = false,
                Margin = new Padding(0, 0, 0, 8)
            };
            root.Controls.Add(_log, 0, 6);

            var buttons = new TableLayoutPanel
            {
                ColumnCount = 2,
                RowCount = 1,
                Dock = DockStyle.Fill,
                AutoSize = true,
                Margin = new Padding(0)
            };
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var left = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0), Anchor = AnchorStyles.Left };
            _linkButton = Theme.CreateButton("Paste a link…", ButtonKind.Outline);
            _linkButton.Margin = new Padding(0, 0, 8, 0);
            _linkButton.Click += PasteLink_Click;
            left.Controls.Add(_linkButton);
            _folderButton = Theme.CreateButton("Open a folder…", ButtonKind.Outline);
            _folderButton.Margin = new Padding(0);
            _folderButton.Click += OpenFolder_Click;
            left.Controls.Add(_folderButton);
            buttons.Controls.Add(left, 0, 0);

            var right = new FlowLayoutPanel { AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(0), Anchor = AnchorStyles.Right };
            _openButton = Theme.CreateButton("Open", ButtonKind.Primary);
            _openButton.Margin = new Padding(0);
            _openButton.Click += Open_Click;
            right.Controls.Add(_openButton);
            _cancelButton = Theme.CreateButton("Cancel", ButtonKind.Outline);
            _cancelButton.DialogResult = DialogResult.Cancel;
            _cancelButton.Margin = new Padding(0, 0, 8, 0);
            right.Controls.Add(_cancelButton);
            buttons.Controls.Add(right, 1, 0);
            root.Controls.Add(buttons, 0, 7);

            Controls.Add(root);
            AcceptButton = _openButton;
            CancelButton = _cancelButton;

            UpdateButtons();
            Shown += async delegate { await LoadAsync(); };
        }

        /// <summary>The vault folder to open (set when DialogResult is OK).</summary>
        public string RepoRoot { get; private set; }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            _list.Columns[0].Width = LogicalToDeviceUnits(300);
            _list.Columns[1].Width = LogicalToDeviceUnits(130);
            _list.Columns[2].Width = LogicalToDeviceUnits(90);
            _list.Columns[3].Width = LogicalToDeviceUnits(150);
        }

        private async Task LoadAsync()
        {
            // Which repos are already on this computer?
            AppSettings settings = AppSettings.Load();
            var folders = new List<string>(settings.KnownRepos);
            if (!string.IsNullOrEmpty(settings.RepoPath)) folders.Add(settings.RepoPath);
            if (!string.IsNullOrEmpty(_currentRoot)) folders.Add(_currentRoot);
            foreach (string folder in folders.Distinct(StringComparer.OrdinalIgnoreCase))
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
                _listStatus.Text = repos.Count == 0
                    ? "Your GitHub account can't see any repositories yet. Ask your team lead to add you, then click Paste a link…"
                    : repos.Count + " repositories. Can't find yours? Ask your team lead to add your GitHub account to it.";
            }
            catch (Exception ex)
            {
                _listStatus.Text = "Couldn't load your repositories from GitHub (" + ex.Message + "). Use Paste a link… instead.";
                ErrorDialog.LogToFile("Listing GitHub repositories failed.", ex.ToString());
            }

            // Local clones that GitHub didn't list (e.g. offline) still show up.
            foreach (KeyValuePair<string, string> local in _local)
            {
                if (_repos.Any(r => string.Equals(r.FullName, local.Key, StringComparison.OrdinalIgnoreCase))) continue;
                _repos.Add(new GitHubRepo
                {
                    FullName = local.Key,
                    Name = local.Key.Substring(local.Key.IndexOf('/') + 1),
                    CloneUrl = "https://github.com/" + local.Key + ".git"
                });
            }

            Populate();
        }

        private void Populate()
        {
            string query = (_search.Text ?? string.Empty).Trim();
            string selected = SelectedRepo() != null ? SelectedRepo().FullName : null;
            string current = CurrentRepoName();

            _list.BeginUpdate();
            _list.Items.Clear();
            IEnumerable<GitHubRepo> ordered = _repos
                .OrderByDescending(r => _local.ContainsKey(r.FullName ?? string.Empty))
                .ThenByDescending(r => r.PushedAtUtc);
            foreach (GitHubRepo repo in ordered)
            {
                if (query.Length > 0 && (repo.FullName ?? string.Empty).IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                bool here = _local.ContainsKey(repo.FullName ?? string.Empty);
                bool isCurrent = string.Equals(repo.FullName, current, StringComparison.OrdinalIgnoreCase);
                var item = new ListViewItem(repo.FullName + (isCurrent ? "  (open now)" : string.Empty));
                item.SubItems.Add(here ? "✓ Downloaded" : "–");
                item.SubItems.Add(repo.PushedAtUtc == DateTime.MinValue ? string.Empty : (repo.Private ? "Private" : "Public"));
                item.SubItems.Add(repo.PushedAtUtc == DateTime.MinValue ? string.Empty : Theme.RelativeTime(repo.PushedAtUtc));
                item.Tag = repo;
                if (!string.IsNullOrEmpty(repo.Description)) item.ToolTipText = repo.Description;
                _list.Items.Add(item);
                if (string.Equals(repo.FullName, selected, StringComparison.OrdinalIgnoreCase)) item.Selected = true;
            }
            _list.EndUpdate();
            UpdateButtons();
        }

        private string CurrentRepoName()
        {
            if (string.IsNullOrEmpty(_currentRoot)) return null;
            foreach (KeyValuePair<string, string> local in _local)
                if (string.Equals(local.Value, _currentRoot, StringComparison.OrdinalIgnoreCase)) return local.Key;
            return null;
        }

        private GitHubRepo SelectedRepo()
        {
            return _list.SelectedItems.Count == 0 ? null : _list.SelectedItems[0].Tag as GitHubRepo;
        }

        private void UpdateButtons()
        {
            GitHubRepo repo = SelectedRepo();
            _openButton.Enabled = !_busy && repo != null;
            _openButton.Text = repo != null && !_local.ContainsKey(repo.FullName ?? string.Empty) ? "Download && open" : "Open";
            _linkButton.Enabled = !_busy;
            _folderButton.Enabled = !_busy;
            _browseButton.Enabled = !_busy;
            _folderBox.Enabled = !_busy;
            _search.Enabled = !_busy;
            _list.Enabled = !_busy;
        }

        private async void Open_Click(object sender, EventArgs e)
        {
            GitHubRepo repo = SelectedRepo();
            if (_busy || repo == null) return;

            string local;
            if (_local.TryGetValue(repo.FullName ?? string.Empty, out local) && Directory.Exists(local))
            {
                Finish(local);
                return;
            }
            await DownloadAsync(repo.CloneUrl, repo.Name);
        }

        private async void PasteLink_Click(object sender, EventArgs e)
        {
            if (_busy) return;
            string url = AskForLink();
            if (string.IsNullOrWhiteSpace(url)) return;
            url = url.Trim();

            string name = GitSetup.ParseGitHubRepo(url);
            string local;
            if (name != null && _local.TryGetValue(name, out local) && Directory.Exists(local))
            {
                Finish(local);
                return;
            }

            string folderName = name != null ? name.Substring(name.IndexOf('/') + 1) : NameFromUrl(url);
            await DownloadAsync(url, folderName);
        }

        private void OpenFolder_Click(object sender, EventArgs e)
        {
            if (_busy) return;
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Pick the vault folder (the one that contains the .git folder)";
                dialog.ShowNewFolderButton = false;
                dialog.SelectedPath = DocumentsFolder();
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                string root = VaultService.FindRepoRoot(dialog.SelectedPath);
                if (root == null)
                {
                    MessageBox.Show(this, "That folder isn't a downloaded repository. Pick the folder that contains the .git folder, or choose a repository from the list.",
                        "PartLock", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                Finish(root);
            }
        }

        private void Browse_Click(object sender, EventArgs e)
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Where should PartLock download repositories?";
                dialog.ShowNewFolderButton = true;
                dialog.SelectedPath = Directory.Exists(_folderBox.Text) ? _folderBox.Text : DocumentsFolder();
                if (dialog.ShowDialog(this) == DialogResult.OK) _folderBox.Text = dialog.SelectedPath;
            }
        }

        private async Task DownloadAsync(string url, string folderName)
        {
            string parent = (_folderBox.Text ?? string.Empty).Trim();
            if (parent.Length == 0) parent = DocumentsFolder();

            string target;
            try
            {
                target = UniqueEmptyFolder(Path.GetFullPath(Path.Combine(parent, string.IsNullOrEmpty(folderName) ? "vault" : folderName)));
            }
            catch (Exception)
            {
                MessageBox.Show(this, "That download folder doesn't look right. Pick another one.", "PartLock",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _busy = true;
            UpdateButtons();
            _cancelButton.Enabled = false;
            _log.Visible = true;
            _log.Clear();
            AppendLog("Downloading " + url + " to " + target);
            _cts = new CancellationTokenSource();
            try
            {
                await VaultService.CloneAsync(url, target, new Progress<string>(AppendLog), _cts.Token);
                _busy = false;
                Finish(VaultService.FindRepoRoot(target) ?? target);
            }
            catch (Exception ex)
            {
                ErrorDialog.ShowException(this, ex);
            }
            finally
            {
                _busy = false;
                _cancelButton.Enabled = true;
                UpdateButtons();
                if (_cts != null) { _cts.Dispose(); _cts = null; }
            }
        }

        private void Finish(string root)
        {
            RepoRoot = root;
            DialogResult = DialogResult.OK;
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_busy)
            {
                e.Cancel = true;
                MessageBox.Show(this, "PartLock is still downloading. Please wait for it to finish.",
                    "PartLock", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            base.OnFormClosing(e);
        }

        private void AppendLog(string line)
        {
            if (IsDisposed || line == null) return;
            _log.AppendText(line + Environment.NewLine);
        }

        private string AskForLink()
        {
            using (var form = new Form())
            {
                form.Text = "PartLock – Paste a link";
                form.Font = Theme.Base;
                form.BackColor = Theme.Panel;
                form.AutoScaleMode = AutoScaleMode.Dpi;
                form.AutoScaleDimensions = new SizeF(96f, 96f);
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.StartPosition = FormStartPosition.CenterParent;
                form.MinimizeBox = false;
                form.MaximizeBox = false;
                form.ShowIcon = false;
                form.ShowInTaskbar = false;
                form.ClientSize = new Size(520, 150);

                var label = new Label
                {
                    Text = "Paste the repository link your team lead sent you:",
                    AutoSize = true,
                    Location = new Point(20, 18)
                };
                var box = new TextBox
                {
                    Location = new Point(20, 46),
                    Width = 480,
                    BorderStyle = BorderStyle.FixedSingle
                };
                Theme.SetCueText(box, "https://github.com/your-team/cad");
                var ok = Theme.CreateButton("Continue", ButtonKind.Primary);
                ok.DialogResult = DialogResult.OK;
                ok.Location = new Point(400, 96);
                var cancel = Theme.CreateButton("Cancel", ButtonKind.Outline);
                cancel.DialogResult = DialogResult.Cancel;
                cancel.Location = new Point(300, 96);
                form.Controls.AddRange(new Control[] { label, box, ok, cancel });
                form.AcceptButton = ok;
                form.CancelButton = cancel;
                return form.ShowDialog(this) == DialogResult.OK ? box.Text : null;
            }
        }

        private static string UniqueEmptyFolder(string path)
        {
            string candidate = path;
            for (int i = 2; i < 100; i++)
            {
                if (!Directory.Exists(candidate) || !Directory.EnumerateFileSystemEntries(candidate).Any()) return candidate;
                candidate = path + " (" + i + ")";
            }
            return candidate;
        }

        private static string NameFromUrl(string url)
        {
            string text = (url ?? string.Empty).Trim().TrimEnd('/', '\\');
            if (text.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) text = text.Substring(0, text.Length - 4);
            int slash = Math.Max(text.LastIndexOf('/'), text.LastIndexOf('\\'));
            if (slash >= 0) text = text.Substring(slash + 1);
            return text.Length == 0 ? "vault" : text;
        }

        private static string DocumentsFolder()
        {
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrEmpty(documents))
                documents = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents");
            return documents;
        }
    }
}
