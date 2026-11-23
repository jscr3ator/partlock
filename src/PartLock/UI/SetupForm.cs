using System;
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
    /// First-run screen: clone the team vault, or point PartLock at a folder that is already a clone.
    /// </summary>
    public class SetupForm : Form
    {
        private TextBox _urlBox;
        private TextBox _folderBox;
        private Button _browseButton;
        private Button _downloadButton;
        private Button _openButton;
        private readonly Button _cancelButton;
        private readonly TextBox _logBox;
        private readonly Label _logLabel;
        private readonly Panel _logHost;
        private readonly RowStyle _logRow;

        private bool _busy;
        private bool _folderAuto = true;
        private bool _settingFolder;
        private CancellationTokenSource _cts;

        public SetupForm()
        {
            Text = "PartLock \u2013 Welcome";
            Font = Theme.Base;
            BackColor = Theme.Background;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96f, 96f);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = true;
            MaximizeBox = true;
            if (Theme.AppIcon != null) Icon = Theme.AppIcon;
            ClientSize = new Size(920, 640);
            MinimumSize = new Size(760, 540);

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 5,
                BackColor = Theme.Background,
                Padding = new Padding(28, 20, 28, 16)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _logRow = new RowStyle(SizeType.Absolute, 0f);
            root.RowStyles.Add(_logRow);
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var header = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                AutoSize = true,
                BackColor = Theme.Background,
                Margin = new Padding(0, 0, 0, 4)
            };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            header.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var title = new Label
            {
                Text = "Welcome to PartLock",
                Font = Theme.Title,
                ForeColor = Theme.Text,
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 4)
            };
            header.Controls.Add(title, 0, 0);

            var subtitle = new Label
            {
                Text = "PartLock keeps your SolidWorks files safe when several people edit them. "
                     + "Pick one of these to get started.",
                Font = Theme.Base,
                ForeColor = Theme.Muted,
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 4)
            };
            TrackWrap(subtitle, root);
            header.Controls.Add(subtitle, 0, 1);
            root.Controls.Add(header, 0, 0);

            var cards = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = Theme.Background,
                Padding = new Padding(0, 18, 0, 8),
                Margin = new Padding(0)
            };
            cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            cards.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            Panel cloneCard = BuildCloneCard();
            cloneCard.Margin = new Padding(0, 0, 8, 0);
            Panel openCard = BuildOpenCard();
            openCard.Margin = new Padding(8, 0, 0, 0);
            cards.Controls.Add(cloneCard, 0, 0);
            cards.Controls.Add(openCard, 1, 0);
            root.Controls.Add(cards, 0, 1);

            _logLabel = new Label
            {
                Text = "Activity",
                Font = Theme.Bold,
                ForeColor = Theme.Muted,
                AutoSize = true,
                Visible = false,
                Margin = new Padding(0, 4, 0, 4)
            };
            root.Controls.Add(_logLabel, 0, 2);

            _logHost = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Panel,
                Padding = new Padding(1),
                Visible = false,
                Margin = new Padding(0, 0, 0, 6)
            };
            _logHost.Paint += delegate(object sender, PaintEventArgs e)
            {
                Theme.DrawBorder(e.Graphics, _logHost.ClientRectangle, Theme.Border);
            };

            _logBox = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                WordWrap = false,
                ScrollBars = ScrollBars.Both,
                BorderStyle = BorderStyle.None,
                Dock = DockStyle.Fill,
                Font = Theme.Mono,
                BackColor = Theme.LogBackground,
                ForeColor = Theme.Text
            };
            _logHost.Controls.Add(_logBox);
            root.Controls.Add(_logHost, 0, 3);

            var bottom = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                AutoSize = true,
                Margin = new Padding(0, 8, 0, 0)
            };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var bottomHint = new Label
            {
                Text = "You can change folders later from the main window.",
                Font = Theme.Small,
                ForeColor = Theme.Muted,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0)
            };
            bottom.Controls.Add(bottomHint, 0, 0);

            var bottomButtons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                AutoSize = true,
                Anchor = AnchorStyles.Right,
                Margin = new Padding(0)
            };

            _cancelButton = Theme.CreateButton("Cancel", ButtonKind.Outline);
            _cancelButton.DialogResult = DialogResult.Cancel;
            _cancelButton.Margin = new Padding(0, 0, 0, 0);
            bottomButtons.Controls.Add(_cancelButton);
            bottom.Controls.Add(bottomButtons, 1, 0);
            root.Controls.Add(bottom, 0, 4);

            Controls.Add(root);
            CancelButton = _cancelButton;

            _urlBox.TextChanged += Url_TextChanged;
            _folderBox.TextChanged += Folder_TextChanged;
            _browseButton.Click += Browse_Click;
            _downloadButton.Click += Download_Click;
            _openButton.Click += OpenExisting_Click;

            Shown += delegate { _urlBox.Focus(); };
        }

        public string RepoRoot { get; private set; }

        private Panel BuildCloneCard()
        {
            Panel card = NewCard();
            var content = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 11,
                BackColor = Theme.Panel
            };
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 14f));
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 12f));
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            content.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var heading = new Label
            {
                Text = "Get the team vault",
                Font = Theme.Heading,
                ForeColor = Theme.Text,
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 6)
            };
            content.Controls.Add(heading, 0, 0);

            var description = new Label
            {
                Text = "Paste the repo link your team lead sent you. PartLock downloads the whole vault, "
                     + "including every part and drawing.",
                Font = Theme.Base,
                ForeColor = Theme.Muted,
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 0)
            };
            TrackWrap(description, content);
            content.Controls.Add(description, 0, 1);

            var urlLabel = new Label
            {
                Text = "Repository URL",
                Font = Theme.Small,
                ForeColor = Theme.Text,
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 5)
            };
            content.Controls.Add(urlLabel, 0, 3);

            _urlBox = new TextBox { Font = Theme.Base };
            Theme.SetCueText(_urlBox, "https://github.com/your-team/cad.git");
            Panel urlHost = MakeInputHost(_urlBox);
            content.Controls.Add(urlHost, 0, 4);

            var folderLabel = new Label
            {
                Text = "Save it in",
                Font = Theme.Small,
                ForeColor = Theme.Text,
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 5)
            };
            content.Controls.Add(folderLabel, 0, 6);

            var folderRow = new TableLayoutPanel
            {
                ColumnCount = 2,
                RowCount = 1,
                Dock = DockStyle.Fill,
                AutoSize = true,
                BackColor = Theme.Panel,
                Margin = new Padding(0)
            };
            folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            folderRow.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            _folderBox = new TextBox { Font = Theme.Base };
            Theme.SetCueText(_folderBox, @"Documents\<repo name>");
            Panel folderHost = MakeInputHost(_folderBox);
            folderHost.Margin = new Padding(0, 0, 8, 0);
            folderRow.Controls.Add(folderHost, 0, 0);

            _browseButton = Theme.CreateButton("Browse\u2026", ButtonKind.Outline);
            _browseButton.MinimumSize = new Size(0, 34);
            _browseButton.Margin = new Padding(0);
            folderRow.Controls.Add(_browseButton, 1, 0);
            content.Controls.Add(folderRow, 0, 7);

            var folderHint = new Label
            {
                Text = "The folder can be new. PartLock creates it for you.",
                Font = Theme.Small,
                ForeColor = Theme.Muted,
                AutoSize = true,
                Margin = new Padding(0, 6, 0, 0)
            };
            TrackWrap(folderHint, content);
            content.Controls.Add(folderHint, 0, 8);

            var downloadRow = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 10, 0, 0)
            };
            _downloadButton = Theme.CreateButton("Download", ButtonKind.Primary);
            _downloadButton.MinimumSize = new Size(0, 38);
            _downloadButton.Margin = new Padding(0);
            downloadRow.Controls.Add(_downloadButton);
            content.Controls.Add(downloadRow, 0, 10);

            card.Controls.Add(content);
            return card;
        }

        private Panel BuildOpenCard()
        {
            Panel card = NewCard();
            var content = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 6,
                BackColor = Theme.Panel
            };
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 14f));
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            content.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            var heading = new Label
            {
                Text = "I already have it",
                Font = Theme.Heading,
                ForeColor = Theme.Text,
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 6)
            };
            content.Controls.Add(heading, 0, 0);

            var description = new Label
            {
                Text = "Already have the vault on this computer, maybe from GitHub Desktop or a "
                     + "teammate's USB stick? Point PartLock at that folder.",
                Font = Theme.Base,
                ForeColor = Theme.Muted,
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 0)
            };
            TrackWrap(description, content);
            content.Controls.Add(description, 0, 1);

            var note = new Label
            {
                Text = "Pick the top folder \u2013 the one that contains the .git folder.",
                Font = Theme.Small,
                ForeColor = Theme.Muted,
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 10)
            };
            TrackWrap(note, content);
            content.Controls.Add(note, 0, 3);

            var openRow = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0)
            };
            _openButton = Theme.CreateButton("Open folder\u2026", ButtonKind.Outline);
            _openButton.MinimumSize = new Size(0, 38);
            _openButton.Margin = new Padding(0);
            openRow.Controls.Add(_openButton);
            content.Controls.Add(openRow, 0, 4);

            var footer = new Label
            {
                Text = "PartLock checks that the folder really is a git vault before using it.",
                Font = Theme.Small,
                ForeColor = Theme.Muted,
                AutoSize = true,
                Anchor = AnchorStyles.Left | AnchorStyles.Bottom,
                Margin = new Padding(0, 10, 0, 0)
            };
            TrackWrap(footer, content);
            content.Controls.Add(footer, 0, 5);

            card.Controls.Add(content);
            return card;
        }

        private static Panel NewCard()
        {
            var card = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Panel,
                Padding = new Padding(20, 18, 20, 18)
            };
            card.Paint += delegate(object sender, PaintEventArgs e)
            {
                Theme.DrawBorder(e.Graphics, card.ClientRectangle, Theme.Border);
            };
            return card;
        }

        private static Panel MakeInputHost(TextBox box)
        {
            var host = new Panel
            {
                Dock = DockStyle.Fill,
                Height = 34,
                BackColor = Theme.Panel,
                Padding = new Padding(9, 6, 9, 4),
                Margin = new Padding(0)
            };
            box.BorderStyle = BorderStyle.None;
            box.Dock = DockStyle.Fill;
            box.BackColor = Theme.Panel;
            host.Controls.Add(box);

            bool focused = false;
            host.Paint += delegate(object sender, PaintEventArgs e)
            {
                Theme.DrawBorder(e.Graphics, host.ClientRectangle, focused ? Theme.Accent : Theme.Border);
            };
            box.Enter += delegate { focused = true; host.Invalidate(); };
            box.Leave += delegate { focused = false; host.Invalidate(); };
            return host;
        }

        private static void TrackWrap(Label label, Control host)
        {
            EventHandler resize = delegate
            {
                int width = host.ClientSize.Width - host.Padding.Horizontal - 6;
                if (width < 120)
                {
                    width = 120;
                }

                if (label.MaximumSize.Width != width)
                {
                    label.MaximumSize = new Size(width, 0);
                }
            };
            host.SizeChanged += resize;
            resize(host, EventArgs.Empty);
        }

        private void Url_TextChanged(object sender, EventArgs e)
        {
            if (!_folderAuto)
            {
                return;
            }

            string documents = GetDocumentsFolder();
            string name = RepoNameFromUrl(_urlBox.Text);
            string folder = string.IsNullOrEmpty(name) ? documents : Path.Combine(documents, name);
            SetFolderText(folder);
        }

        private void Folder_TextChanged(object sender, EventArgs e)
        {
            if (!_settingFolder)
            {
                _folderAuto = false;
            }
        }

        private void Browse_Click(object sender, EventArgs e)
        {
            if (_busy)
            {
                return;
            }

            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Where should PartLock save the vault?";
                dialog.ShowNewFolderButton = true;
                string current = _folderBox.Text.Trim();
                dialog.SelectedPath = current.Length > 0 && Directory.Exists(current)
                    ? current
                    : GetDocumentsFolder();

                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    _folderAuto = false;
                    SetFolderText(dialog.SelectedPath);
                }
            }
        }

        private async void Download_Click(object sender, EventArgs e)
        {
            if (_busy)
            {
                return;
            }

            string url = _urlBox.Text.Trim();
            if (url.Length == 0)
            {
                FocusProblem(_urlBox);
                return;
            }

            string target = _folderBox.Text.Trim();
            if (target.Length == 0)
            {
                FocusProblem(_folderBox);
                return;
            }

            string fullTarget;
            try
            {
                fullTarget = Path.GetFullPath(target);
            }
            catch (Exception)
            {
                MessageBox.Show(this, "That folder path doesn't look right. Pick another folder.",
                    "PartLock", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _folderBox.Focus();
                return;
            }

            if (File.Exists(fullTarget))
            {
                MessageBox.Show(this, "That path is a file, not a folder. Pick another folder.",
                    "PartLock", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _folderBox.Focus();
                return;
            }

            try
            {
                if (Directory.Exists(fullTarget) && Directory.EnumerateFileSystemEntries(fullTarget).Any())
                {
                    MessageBox.Show(this,
                        "That folder already has something in it. Pick a new or empty folder so "
                        + "PartLock doesn't overwrite anything.",
                        "PartLock", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    _folderBox.Focus();
                    return;
                }
            }
            catch (Exception ex)
            {
                ErrorDialog.ShowException(this, ex);
                return;
            }

            SetBusy(true);
            ShowLog();
            AppendLog("Cloning " + url);
            _cts = new CancellationTokenSource();

            try
            {
                var progress = new Progress<string>(AppendLog);
                await VaultService.CloneAsync(url, fullTarget, progress, _cts.Token);

                string root = VaultService.FindRepoRoot(fullTarget) ?? fullTarget;
                RepoRoot = root;
                AppendLog("Download complete: " + root);
                SetBusy(false);
                this.DialogResult = DialogResult.OK;
                Close();
            }
            catch (OperationCanceledException)
            {
                AppendLog("Download cancelled.");
            }
            catch (Exception ex)
            {
                ErrorDialog.ShowException(this, ex);
            }
            finally
            {
                SetBusy(false);
                if (_cts != null)
                {
                    _cts.Dispose();
                    _cts = null;
                }
            }
        }

        private void OpenExisting_Click(object sender, EventArgs e)
        {
            if (_busy)
            {
                return;
            }

            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Pick the folder that contains the team's .git folder";
                dialog.ShowNewFolderButton = false;
                dialog.SelectedPath = GetDocumentsFolder();

                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                string root = VaultService.FindRepoRoot(dialog.SelectedPath);
                if (root == null)
                {
                    MessageBox.Show(this,
                        "That folder isn't a Git vault yet. Pick the folder that contains the "
                        + "team's .git folder, or use \"Get the team vault\" on the left.",
                        "PartLock", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                RepoRoot = root;
                this.DialogResult = DialogResult.OK;
                Close();
            }
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

        private void SetBusy(bool busy)
        {
            _busy = busy;
            _urlBox.Enabled = !busy;
            _folderBox.Enabled = !busy;
            _browseButton.Enabled = !busy;
            _downloadButton.Enabled = !busy;
            _openButton.Enabled = !busy;
            _cancelButton.Enabled = !busy;
            _downloadButton.Text = busy ? "Downloading\u2026" : "Download";
        }

        private void ShowLog()
        {
            _logLabel.Visible = true;
            _logHost.Visible = true;
            _logRow.Height = LogicalToDeviceUnits(120);
        }

        private void AppendLog(string line)
        {
            if (IsDisposed || line == null)
            {
                return;
            }

            if (InvokeRequired)
            {
                try
                {
                    BeginInvoke(new Action<string>(AppendLog), line);
                }
                catch (InvalidOperationException)
                {
                }

                return;
            }

            _logBox.AppendText(line + Environment.NewLine);
            _logBox.SelectionStart = _logBox.TextLength;
            _logBox.ScrollToCaret();
        }

        private void SetFolderText(string folder)
        {
            _settingFolder = true;
            _folderBox.Text = folder ?? string.Empty;
            _settingFolder = false;
        }

        private void FocusProblem(TextBox box)
        {
            MessageBox.Show(this, "Fill in this field first.",
                "PartLock", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            if (box != null)
            {
                box.Focus();
            }
        }

        private static string RepoNameFromUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            string text = url.Trim().TrimEnd('/', '\\');
            if (text.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            {
                text = text.Substring(0, text.Length - 4);
            }

            int slash = Math.Max(text.LastIndexOf('/'), text.LastIndexOf('\\'));
            if (slash >= 0)
            {
                text = text.Substring(slash + 1);
            }

            text = text.Trim();
            return text.Length == 0 ? null : text;
        }

        private static string GetDocumentsFolder()
        {
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrEmpty(documents))
            {
                string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                documents = Path.Combine(profile, "Documents");
            }

            return documents;
        }
    }
}
