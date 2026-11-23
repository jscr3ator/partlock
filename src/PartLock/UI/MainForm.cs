using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using PartLock.Core;

namespace PartLock.UI
{
    /// <summary>
    /// The whole app: three big actions, one list, obvious colours. Nothing here blocks the UI thread;
    /// every git call is awaited and errors land in ErrorDialog.
    /// </summary>
    public class MainForm : Form
    {
        private readonly IVaultService _vault;
        private readonly string _repoName;
        private readonly List<VaultFile> _files = new List<VaultFile>();
        private readonly Queue<string> _logLines = new Queue<string>();

        private BufferedListView _list;
        private Label _emptyLabel;
        private TextBox _search;
        private ComboBox _filter;
        private Button _getLatestButton;
        private Button _checkOutButton;
        private Button _checkInButton;
        private Button _undoButton;
        private ToolStripStatusLabel _statusText;
        private ToolStripProgressBar _progress;
        private StatusStrip _statusStrip;
        private TextBox _activityBox;
        private LinkLabel _activityToggle;
        private RowStyle _activityRow;
        private Panel _activityPanel;
        private Label _identityLabel;
        private Panel _banner;
        private Timer _timer;
        private ImageList _rowImages;

        private ContextMenuStrip _menu;
        private ToolStripMenuItem _menuOpen;
        private ToolStripMenuItem _menuExplorer;
        private ToolStripMenuItem _menuCheckOut;
        private ToolStripMenuItem _menuCheckIn;
        private ToolStripMenuItem _menuUndo;
        private ToolStripMenuItem _menuHistory;
        private ToolStripMenuItem _menuForceUnlock;

        private DateTime _lastRefreshUtc = DateTime.MinValue;
        private bool _busy;
        private bool _initialized;
        private bool _identityConfirmed;
        private bool _activityVisible;
        private bool _lockingConfigured;
        private string _commitName;
        private string _commitEmail;
        private int _sortColumn;
        private bool _sortAscending = true;

        private readonly GitHubAccount _account;
        private readonly ToolTip _toolTip = new ToolTip();
        private Button _accountButton;
        private Button _repoButton;
        private ContextMenuStrip _accountMenu;
        private ContextMenuStrip _repoMenu;
        private ToolStripMenuItem _accountHeaderItem;
        private ToolStripMenuItem _identityItem;
        private Panel _changesBanner;
        private Label _changesText;
        private FileSystemWatcher _watcher;
        private Timer _watchDebounce;
        private string _repoFullName;
        private string _repoWebUrl;
        private ToolStripStatusLabel _updateLink;
        private PartLock.Platform.UpdateInfo _update;

        public MainForm(IVaultService vault, GitHubAccount account = null)
        {
            if (vault == null)
            {
                throw new ArgumentNullException("vault");
            }

            _vault = vault;
            _account = account;
            _repoName = GetRepoName(vault.RepoRoot);

            Text = "PartLock \u2013 " + _repoName;
            Font = Theme.Base;
            BackColor = Theme.Background;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96f, 96f);
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(900, 560);
            Size = new Size(1100, 680);
            DoubleBuffered = true;
            if (Theme.AppIcon != null) Icon = Theme.AppIcon;

            BuildUi();
            BuildMenu();
            _vault.Log = new Progress<string>(AppendLog);

            _timer = new Timer { Interval = 120000 };
            _timer.Tick += Timer_Tick;

            Shown += MainForm_Shown;
            Activated += MainForm_Activated;
            FormClosing += MainForm_FormClosing;
        }

        // ---------------------------------------------------------------- layout

        private void BuildUi()
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 9,
                BackColor = Theme.Background,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // header
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // tip
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // actions
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // locking banner
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // changes banner
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f)); // file list
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // activity toggle
            _activityRow = new RowStyle(SizeType.Absolute, 0f);
            root.RowStyles.Add(_activityRow);                          // activity log
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // status bar

            root.Controls.Add(BuildHeader(), 0, 0);

            var tip = new Label
            {
                Text = "\u2460 Get Latest   \u2192   \u2461 Check Out the part   \u2192   \u2462 Edit & save in SolidWorks   \u2192   "
                     + "\u2463 Check In to upload it to GitHub",
                Font = Theme.Small,
                ForeColor = Theme.Muted,
                AutoSize = true,
                Margin = new Padding(22, 6, 0, 2)
            };
            root.Controls.Add(tip, 0, 1);

            root.Controls.Add(BuildActionBar(), 0, 2);
            root.Controls.Add(BuildLockingBanner(), 0, 3);
            root.Controls.Add(BuildChangesBanner(), 0, 4);
            root.Controls.Add(BuildListArea(), 0, 5);
            root.Controls.Add(BuildActivityToggle(), 0, 6);
            root.Controls.Add(BuildActivityPanel(), 0, 7);
            root.Controls.Add(BuildStatusStrip(), 0, 8);

            Controls.Add(root);
        }

        private Control BuildHeader()
        {
            var header = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Panel,
                AutoSize = true,
                Padding = new Padding(20, 10, 20, 10),
                Margin = new Padding(0)
            };
            header.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (var pen = new Pen(Theme.Border))
                {
                    e.Graphics.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1);
                }
            };

            var table = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                AutoSize = true,
                BackColor = Theme.Panel,
                Margin = new Padding(0)
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var brandRow = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                BackColor = Theme.Panel,
                Margin = new Padding(0)
            };
            if (Theme.Logo != null)
            {
                brandRow.Controls.Add(new PictureBox
                {
                    Image = Theme.Logo,
                    SizeMode = PictureBoxSizeMode.Zoom,
                    Size = new Size(52, 52),
                    BackColor = Theme.Panel,
                    Margin = new Padding(0, 0, 12, 0)
                });
            }

            var brand = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                BackColor = Theme.Panel,
                Margin = new Padding(0)
            };
            brand.Controls.Add(new Label
            {
                Text = "PartLock",
                Font = Theme.Brand,
                ForeColor = Theme.Text,
                AutoSize = true,
                BackColor = Theme.Panel,
                Margin = new Padding(0, 0, 0, 1)
            });
            brand.Controls.Add(new Label
            {
                Text = _repoName,
                Font = Theme.Small,
                ForeColor = Theme.Muted,
                AutoSize = true,
                BackColor = Theme.Panel,
                Margin = new Padding(0)
            });
            brandRow.Controls.Add(brand);
            table.Controls.Add(brandRow, 0, 0);

            var right = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true,
                Anchor = AnchorStyles.Right,
                BackColor = Theme.Panel,
                Margin = new Padding(0)
            };

            _repoButton = Theme.CreateButton("\U0001F4C1  " + _repoName + "  \u25BE", ButtonKind.Outline);
            _repoButton.MinimumSize = new Size(0, 36);
            _repoButton.Margin = new Padding(0, 0, 8, 0);
            _repoButton.Click += delegate { _repoMenu.Show(_repoButton, new Point(0, _repoButton.Height)); };
            right.Controls.Add(_repoButton);

            _accountButton = Theme.CreateButton(AccountButtonText(), ButtonKind.Outline);
            _accountButton.MinimumSize = new Size(0, 36);
            _accountButton.Margin = new Padding(0);
            _accountButton.TextImageRelation = TextImageRelation.ImageBeforeText;
            _accountButton.ImageAlign = ContentAlignment.MiddleLeft;
            _accountButton.Click += delegate { _accountMenu.Show(_accountButton, new Point(0, _accountButton.Height)); };
            right.Controls.Add(_accountButton);

            // Kept for the commit-name text; shown inside the account menu.
            _identityLabel = new Label { Visible = false };

            BuildHeaderMenus();

            table.Controls.Add(right, 1, 0);
            header.Controls.Add(table);
            return header;
        }

        private string AccountButtonText()
        {
            if (_account == null || string.IsNullOrEmpty(_account.Login)) return "GitHub: not signed in  \u25BE";
            return "@" + _account.Login + "  \u25BE";
        }

        private void BuildHeaderMenus()
        {
            _accountMenu = new ContextMenuStrip { Font = Theme.Base, ShowImageMargin = false };
            _accountHeaderItem = new ToolStripMenuItem(_account == null || string.IsNullOrEmpty(_account.Login)
                ? "Not signed in to GitHub"
                : "Signed in to GitHub as " + _account.DisplayName + " (@" + _account.Login + ")") { Enabled = false };
            _accountMenu.Items.Add(_accountHeaderItem);
            _identityItem = new ToolStripMenuItem("Check-ins are signed as \u2026") { Enabled = false };
            _accountMenu.Items.Add(_identityItem);
            _accountMenu.Items.Add(new ToolStripSeparator());
            var profile = new ToolStripMenuItem("Open my GitHub profile");
            profile.Click += delegate
            {
                if (_account != null && !string.IsNullOrEmpty(_account.Login)) OpenUrl("https://github.com/" + _account.Login);
            };
            _accountMenu.Items.Add(profile);
            var identity = new ToolStripMenuItem("Change name && email on check-ins\u2026");
            identity.Click += ChangeIdentity_Click;
            _accountMenu.Items.Add(identity);
            var switchAccount = new ToolStripMenuItem("Sign out / switch GitHub account\u2026");
            switchAccount.Click += SwitchAccount_Click;
            _accountMenu.Items.Add(switchAccount);
            _accountMenu.Items.Add(new ToolStripSeparator());
            var updates = new ToolStripMenuItem("Check for PartLock updates\u2026  (you have v" +
                PartLock.Platform.Updater.CurrentVersion.ToString(3) + ")");
            updates.Click += async delegate { await CheckForUpdatesAsync(true); };
            _accountMenu.Items.Add(updates);

            _repoMenu = new ContextMenuStrip { Font = Theme.Base, ShowImageMargin = false };
            var openWeb = new ToolStripMenuItem("Open this repository on GitHub");
            openWeb.Click += delegate
            {
                if (!string.IsNullOrEmpty(_repoWebUrl)) OpenUrl(_repoWebUrl);
                else MessageBox.Show(this, "This vault isn't on GitHub.", "PartLock", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            _repoMenu.Items.Add(openWeb);
            var openFolder = new ToolStripMenuItem("Show the folder in Explorer");
            openFolder.Click += delegate { OpenUrl(_vault.RepoRoot); };
            _repoMenu.Items.Add(openFolder);
            _repoMenu.Items.Add(new ToolStripSeparator());
            var switchRepo = new ToolStripMenuItem("Switch to another repository\u2026");
            switchRepo.Click += ChangeFolder_LinkClicked;
            _repoMenu.Items.Add(switchRepo);
            _repoMenu.Items.Add(new ToolStripSeparator());
            var explorerMenu = new ToolStripMenuItem("Right-click menu in File Explorer") { CheckOnClick = true };
            explorerMenu.Checked = AppSettings.Load().ExplorerMenu;
            explorerMenu.ToolTipText = "Check Out / Check In from File Explorer: right-click a CAD file \u2192 PartLock "
                                     + "(on Windows 11: Show more options).";
            explorerMenu.CheckedChanged += delegate
            {
                AppSettings settings = AppSettings.Load();
                settings.ExplorerMenu = explorerMenu.Checked;
                settings.Save();
                if (explorerMenu.Checked) PartLock.Platform.ShellIntegration.Register(Application.ExecutablePath);
                else PartLock.Platform.ShellIntegration.Unregister();
            };
            _repoMenu.Items.Add(explorerMenu);
            if (PartLock.Platform.ShellIntegration.IsWindows11)
            {
                var fullMenu = new ToolStripMenuItem("Show PartLock right away (skip \u201CShow more options\u201D)")
                {
                    Checked = PartLock.Platform.ShellIntegration.FullMenuEnabled
                };
                fullMenu.Click += delegate { fullMenu.Checked = AskFullMenu(!fullMenu.Checked); };
                _repoMenu.Items.Add(fullMenu);
            }
        }

        private Control BuildChangesBanner()
        {
            _changesBanner = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.FromHex("#DBEAFE"),
                AutoSize = true,
                Visible = false,
                Padding = new Padding(20, 8, 20, 8),
                Margin = new Padding(0)
            };
            var table = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                AutoSize = true,
                BackColor = _changesBanner.BackColor,
                Margin = new Padding(0)
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _changesText = new Label
            {
                Font = Theme.Base,
                ForeColor = Theme.FromHex("#1E3A8A"),
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                BackColor = _changesBanner.BackColor,
                Margin = new Padding(0)
            };
            table.Controls.Add(_changesText, 0, 0);
            var go = Theme.CreateButton("Check In now", ButtonKind.Primary);
            go.MinimumSize = new Size(0, 30);
            go.Margin = new Padding(8, 0, 0, 0);
            go.Click += CheckIn_Click;
            table.Controls.Add(go, 1, 0);
            _changesBanner.Controls.Add(table);
            return _changesBanner;
        }

        private static void OpenUrl(string target)
        {
            if (string.IsNullOrEmpty(target)) return;
            try { Process.Start(target); }
            catch (Exception) { }
        }

        private Control BuildActionBar()
        {
            var bar = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Background,
                AutoSize = true,
                Padding = new Padding(20, 10, 20, 8),
                Margin = new Padding(0)
            };

            var table = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                AutoSize = true,
                BackColor = Theme.Background,
                Margin = new Padding(0)
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var actions = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                BackColor = Theme.Background,
                Margin = new Padding(0)
            };

            _getLatestButton = Theme.CreateButton("Get Latest", ButtonKind.Primary);
            _getLatestButton.MinimumSize = new Size(0, 40);
            _getLatestButton.Margin = new Padding(0, 0, 8, 0);
            _getLatestButton.Click += GetLatest_Click;
            actions.Controls.Add(_getLatestButton);

            _checkOutButton = Theme.CreateButton("Check Out", ButtonKind.Outline);
            _checkOutButton.MinimumSize = new Size(0, 40);
            _checkOutButton.Click += CheckOut_Click;
            actions.Controls.Add(_checkOutButton);

            _checkInButton = Theme.CreateButton("Check In\u2026", ButtonKind.Outline);
            _checkInButton.MinimumSize = new Size(0, 40);
            _checkInButton.Click += CheckIn_Click;
            actions.Controls.Add(_checkInButton);

            _undoButton = Theme.CreateButton("Undo Check Out", ButtonKind.Outline);
            _undoButton.MinimumSize = new Size(0, 40);
            _undoButton.Click += Undo_Click;
            actions.Controls.Add(_undoButton);

            table.Controls.Add(actions, 0, 0);

            var filters = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true,
                Anchor = AnchorStyles.Right,
                BackColor = Theme.Background,
                Margin = new Padding(0)
            };

            _search = new TextBox
            {
                Width = 230,
                Font = Theme.Base,
                BorderStyle = BorderStyle.FixedSingle,
                Margin = new Padding(8, 10, 10, 0)
            };
            Theme.SetCueText(_search, "Search file names\u2026");
            _search.TextChanged += delegate { ApplyFilter(GetSelectedPaths()); };
            filters.Controls.Add(_search);

            _filter = new ComboBox
            {
                Width = 180,
                Font = Theme.Base,
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat,
                Margin = new Padding(0, 9, 0, 0)
            };
            _filter.Items.AddRange(new object[]
            {
                "All files",
                "My check-outs",
                "Checked out by others",
                "Changed / new",
                "Needs update"
            });
            _filter.SelectedIndex = 0;
            _filter.SelectedIndexChanged += delegate { ApplyFilter(GetSelectedPaths()); };
            filters.Controls.Add(_filter);

            table.Controls.Add(filters, 1, 0);
            bar.Controls.Add(table);
            return bar;
        }

        private Control BuildLockingBanner()
        {
            _banner = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Banner,
                AutoSize = true,
                Visible = false,
                Padding = new Padding(20, 8, 20, 8),
                Margin = new Padding(0)
            };

            var table = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                AutoSize = true,
                BackColor = Theme.Banner,
                Margin = new Padding(0)
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var text = new Label
            {
                Text = "This folder isn't set up for check-out locking yet.",
                Font = Theme.Base,
                ForeColor = Theme.BannerText,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                BackColor = Theme.Banner,
                Margin = new Padding(0)
            };
            table.Controls.Add(text, 0, 0);

            var setup = Theme.CreateButton("Set it up (team lead)", ButtonKind.Outline);
            setup.MinimumSize = new Size(0, 30);
            setup.Font = Theme.Small;
            setup.BackColor = Color.White;
            setup.Margin = new Padding(8, 0, 0, 0);
            setup.Click += LockingSetup_Click;
            table.Controls.Add(setup, 1, 0);

            _banner.Controls.Add(table);
            return _banner;
        }

        private Control BuildListArea()
        {
            var host = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Panel,
                Padding = new Padding(1),
                Margin = new Padding(20, 2, 20, 4)
            };
            host.Paint += delegate(object sender, PaintEventArgs e)
            {
                Theme.DrawBorder(e.Graphics, host.ClientRectangle, Theme.Border);
            };

            _list = new BufferedListView
            {
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = true,
                HideSelection = false,
                BorderStyle = BorderStyle.None,
                Dock = DockStyle.Fill,
                BackColor = Theme.Panel,
                ForeColor = Theme.Text,
                Font = Theme.Base,
                OwnerDraw = true,
                ShowGroups = false,
                ShowItemToolTips = true
            };
            _list.Columns.Add("Name", 240);
            _list.Columns.Add("Folder", 220);
            _list.Columns.Add("Status", 230);
            _list.Columns.Add("Checked out by", 140);
            _list.Columns.Add("Since", 130);
            _list.ColumnClick += List_ColumnClick;
            _list.DrawColumnHeader += List_DrawColumnHeader;
            _list.DrawItem += List_DrawItem;
            _list.DrawSubItem += List_DrawSubItem;
            _list.DoubleClick += List_DoubleClick;
            _list.MouseDown += List_MouseDown;
            _list.ItemSelectionChanged += delegate { UpdateActionButtons(); };
            host.Controls.Add(_list);

            _emptyLabel = new Label
            {
                Text = "No files yet.",
                Font = Theme.Base,
                ForeColor = Theme.Muted,
                BackColor = Theme.Panel,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Padding = new Padding(40),
                Visible = false
            };
            host.Controls.Add(_emptyLabel);
            _emptyLabel.BringToFront();
            return host;
        }

        private Control BuildActivityToggle()
        {
            var flow = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                AutoSize = true,
                Dock = DockStyle.Fill,
                BackColor = Theme.Background,
                Padding = new Padding(0, 0, 20, 0),
                Margin = new Padding(0)
            };

            _activityToggle = new LinkLabel
            {
                Text = "Show activity \u25BC",
                Font = Theme.Small,
                LinkColor = Theme.Muted,
                ActiveLinkColor = Theme.Accent,
                VisitedLinkColor = Theme.Muted,
                LinkBehavior = LinkBehavior.HoverUnderline,
                AutoSize = true,
                Anchor = AnchorStyles.Right,
                Margin = new Padding(0, 2, 2, 2)
            };
            _activityToggle.LinkClicked += ActivityToggle_LinkClicked;
            flow.Controls.Add(_activityToggle);
            return flow;
        }

        private Control BuildActivityPanel()
        {
            _activityPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Background,
                Padding = new Padding(20, 0, 20, 6),
                Visible = false,
                Margin = new Padding(0)
            };

            _activityBox = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                WordWrap = false,
                ScrollBars = ScrollBars.Both,
                BorderStyle = BorderStyle.FixedSingle,
                Dock = DockStyle.Fill,
                Font = Theme.Mono,
                BackColor = Theme.LogBackground,
                ForeColor = Theme.Text
            };
            _activityPanel.Controls.Add(_activityBox);
            return _activityPanel;
        }

        private Control BuildStatusStrip()
        {
            _statusStrip = new StatusStrip
            {
                Dock = DockStyle.Fill,
                SizingGrip = false,
                BackColor = Theme.Panel,
                ForeColor = Theme.Muted,
                AutoSize = true,
                Padding = new Padding(16, 2, 16, 2),
                Margin = new Padding(0)
            };

            _statusText = new ToolStripStatusLabel
            {
                Spring = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = Theme.Small,
                ForeColor = Theme.Muted,
                Text = "Loading\u2026"
            };
            _statusStrip.Items.Add(_statusText);

            _updateLink = new ToolStripStatusLabel
            {
                IsLink = true,
                Visible = false,
                Font = Theme.Small,
                LinkColor = Theme.Accent,
                ActiveLinkColor = Theme.AccentHover,
                Alignment = ToolStripItemAlignment.Right
            };
            _updateLink.Click += async delegate { await InstallUpdateAsync(); };
            _statusStrip.Items.Add(_updateLink);

            _progress = new ToolStripProgressBar
            {
                Style = ProgressBarStyle.Marquee,
                MarqueeAnimationSpeed = 30,
                AutoSize = false,
                Size = new Size(140, 14),
                Visible = false,
                Alignment = ToolStripItemAlignment.Right
            };
            _statusStrip.Items.Add(_progress);
            return _statusStrip;
        }

        private void BuildMenu()
        {
            _menu = new ContextMenuStrip { Font = Theme.Base, ShowImageMargin = false };

            _menuOpen = new ToolStripMenuItem("Open");
            _menuOpen.Click += Open_Click;
            _menu.Items.Add(_menuOpen);

            _menuExplorer = new ToolStripMenuItem("Show in Explorer");
            _menuExplorer.Click += Explorer_Click;
            _menu.Items.Add(_menuExplorer);

            _menu.Items.Add(new ToolStripSeparator());

            _menuCheckOut = new ToolStripMenuItem("Check Out");
            _menuCheckOut.Click += MenuCheckOut_Click;
            _menu.Items.Add(_menuCheckOut);

            _menuCheckIn = new ToolStripMenuItem("Check In\u2026");
            _menuCheckIn.Click += MenuCheckIn_Click;
            _menu.Items.Add(_menuCheckIn);

            _menuUndo = new ToolStripMenuItem("Undo Check Out");
            _menuUndo.Click += MenuUndo_Click;
            _menu.Items.Add(_menuUndo);

            _menuHistory = new ToolStripMenuItem("View History\u2026");
            _menuHistory.Click += MenuHistory_Click;
            _menu.Items.Add(_menuHistory);

            _menu.Items.Add(new ToolStripSeparator());

            _menuForceUnlock = new ToolStripMenuItem("Force Unlock (admin)\u2026");
            _menuForceUnlock.Click += MenuForceUnlock_Click;
            _menu.Items.Add(_menuForceUnlock);

            _menu.Opening += Menu_Opening;
            _list.ContextMenuStrip = _menu;
        }

        // ---------------------------------------------------------- start-up / refresh

        private async void MainForm_Shown(object sender, EventArgs e)
        {
            try
            {
                await InitializeAsync();
            }
            catch (Exception ex)
            {
                ErrorDialog.ShowException(this, ex);
            }
        }

        private async Task InitializeAsync()
        {
            if (_initialized)
            {
                return;
            }

            SetBusy(true, "Opening the vault\u2026");
            StartWatcher();
            LoadAvatarAsync();
            try
            {
                await LoadRepoInfoAsync();
                await LoadIdentityAsync();
                _lockingConfigured = await _vault.IsLockingConfiguredAsync();
                UpdateLockingBanner();
                await ReloadAsync(true);
            }
            catch (VaultException ex)
            {
                ErrorDialog.ShowException(this, ex);
            }
            catch (Exception ex)
            {
                ErrorDialog.ShowException(this, ex);
            }
            finally
            {
                _initialized = true;
                _timer.Start();
                SetBusy(false);
            }

            if (_vault is VaultService) OfferFullMenuOnce();
            await CheckForUpdatesAsync(false);
        }

        // ------------------------------------------------------------------ Windows 11 menu

        /// <summary>Asks, then turns the full right-click menu on/off. Returns the resulting state.</summary>
        private bool AskFullMenu(bool enable)
        {
            string text = enable
                ? "Windows 11 hides PartLock (and most apps) under \u201CShow more options\u201D when you right-click a file.\n\n"
                  + "PartLock can switch File Explorer to the full right-click menu so PartLock \u2192 Check Out / Check In "
                  + "shows up straight away. This affects every right-click menu for your Windows account, and you can switch "
                  + "it back any time from the \U0001F4C1 menu.\n\nFile Explorer restarts for a moment (open Explorer windows close). Switch now?"
                : "Switch File Explorer back to the Windows 11 short right-click menu? PartLock will be under \u201CShow more options\u201D again.\n\n"
                  + "File Explorer restarts for a moment.";
            DialogResult answer = MessageBox.Show(this, text, "PartLock", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer == DialogResult.Yes)
            {
                try { PartLock.Platform.ShellIntegration.SetFullMenu(enable); }
                catch (Exception ex) { ErrorDialog.ShowException(this, ex); }
            }
            return PartLock.Platform.ShellIntegration.FullMenuEnabled;
        }

        private void OfferFullMenuOnce()
        {
            try
            {
                if (!PartLock.Platform.ShellIntegration.IsWindows11 || PartLock.Platform.ShellIntegration.FullMenuEnabled) return;
                AppSettings settings = AppSettings.Load();
                if (!settings.ExplorerMenu || settings.FullMenuAsked) return;
                settings.FullMenuAsked = true;
                settings.Save();
                AskFullMenu(true);
            }
            catch (Exception ex)
            {
                ErrorDialog.LogToFile("Offering the full right-click menu failed.", ex.ToString());
            }
        }

        // ------------------------------------------------------------------ updates

        private async Task CheckForUpdatesAsync(bool userAsked)
        {
            try
            {
                PartLock.Platform.UpdateInfo update =
                    await PartLock.Platform.Updater.CheckAsync(System.Threading.CancellationToken.None);
                if (IsDisposed) return;
                _update = update;
                if (update == null)
                {
                    _updateLink.Visible = false;
                    if (userAsked)
                    {
                        MessageBox.Show(this, "You have the latest PartLock (v" +
                            PartLock.Platform.Updater.CurrentVersion.ToString(3) + ").",
                            "PartLock", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    return;
                }

                _updateLink.Text = "PartLock " + update.Tag + " is available \u2013 click to update";
                _updateLink.Visible = true;
                if (userAsked) await InstallUpdateAsync();
            }
            catch (Exception ex)
            {
                ErrorDialog.LogToFile("Checking for updates failed.", ex.ToString());
                if (userAsked)
                {
                    MessageBox.Show(this, "Couldn't check for updates right now. Check your internet connection and try again.",
                        "PartLock", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
        }

        private async Task InstallUpdateAsync()
        {
            PartLock.Platform.UpdateInfo update = _update;
            if (update == null || _busy) return;

            string notes = (update.Notes ?? string.Empty).Trim();
            if (notes.Length > 700) notes = notes.Substring(0, 700) + "\u2026";
            DialogResult answer = MessageBox.Show(this,
                "Update PartLock from v" + PartLock.Platform.Updater.CurrentVersion.ToString(3) + " to " + update.Tag + " now?\n\n"
                + "PartLock restarts. Your files and check-outs aren't touched."
                + (notes.Length > 0 ? "\n\nWhat's new:\n" + notes : string.Empty),
                "PartLock update", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes) return;

            bool restart = false;
            bool notWritable = false;
            await RunBusyAsync("Updating PartLock\u2026", async delegate
            {
                bool ok = await PartLock.Platform.Updater.InstallAsync(update, new Progress<string>(AppendLog),
                    System.Threading.CancellationToken.None);
                restart = ok;
                notWritable = !ok;
            });

            if (restart)
            {
                _timer.Stop();
                Application.Exit();
            }
            else if (notWritable)
            {
                MessageBox.Show(this, "PartLock can't update itself in the folder it's in (no permission). "
                    + "The download page will open; replace PartLock.exe with the new one.",
                    "PartLock", MessageBoxButtons.OK, MessageBoxIcon.Information);
                OpenUrl(update.PageUrl);
            }
        }

        private async Task LoadRepoInfoAsync()
        {
            string origin = await GitSetup.GetOriginUrlAsync(_vault.RepoRoot, System.Threading.CancellationToken.None);
            _repoFullName = GitSetup.ParseGitHubRepo(origin);
            _repoWebUrl = _repoFullName != null ? "https://github.com/" + _repoFullName : null;
            string label = _repoFullName ?? _repoName;
            _repoButton.Text = "\U0001F4C1  " + label + "  \u25BE";
            Text = "PartLock \u2013 " + label;
            _toolTip.SetToolTip(_repoButton, "Folder: " + _vault.RepoRoot + (origin != null ? "\nServer: " + origin : string.Empty));
        }

        private async void LoadAvatarAsync()
        {
            if (_account == null || string.IsNullOrEmpty(_account.AvatarUrl)) return;
            byte[] data = await GitSetup.DownloadBytesAsync(_account.AvatarUrl + (_account.AvatarUrl.Contains("?") ? "&" : "?") + "s=64");
            if (data == null || IsDisposed) return;
            try
            {
                using (var stream = new MemoryStream(data))
                using (var source = Image.FromStream(stream))
                {
                    int size = LogicalToDeviceUnits(22);
                    var round = new Bitmap(size, size);
                    using (Graphics g = Graphics.FromImage(round))
                    using (var path = new System.Drawing.Drawing2D.GraphicsPath())
                    {
                        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        path.AddEllipse(0, 0, size - 1, size - 1);
                        g.SetClip(path);
                        g.DrawImage(source, 0, 0, size, size);
                    }
                    _accountButton.Image = round;
                    _accountButton.Text = " " + AccountButtonText();
                }
            }
            catch (Exception) { }
        }

        private void StartWatcher()
        {
            if (!(_vault is VaultService) || string.IsNullOrEmpty(_vault.RepoRoot) || !Directory.Exists(_vault.RepoRoot)) return;
            try
            {
                _watchDebounce = new Timer { Interval = 1500 };
                _watchDebounce.Tick += async delegate
                {
                    if (_busy) return; // try again on the next tick
                    _watchDebounce.Stop();
                    try { await RefreshWithBusyAsync("Refreshing\u2026", false); }
                    catch (Exception) { }
                };

                _watcher = new FileSystemWatcher(_vault.RepoRoot)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                                   NotifyFilters.LastWrite | NotifyFilters.Size
                };
                FileSystemEventHandler changed = delegate(object sender, FileSystemEventArgs e) { OnRepoFileChanged(e.FullPath); };
                _watcher.Changed += changed;
                _watcher.Created += changed;
                _watcher.Deleted += changed;
                _watcher.Renamed += delegate(object sender, RenamedEventArgs e) { OnRepoFileChanged(e.FullPath); };
                _watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                ErrorDialog.LogToFile("Couldn't watch the vault folder for changes.", ex.ToString());
            }
        }

        private void OnRepoFileChanged(string fullPath)
        {
            string relative = fullPath ?? string.Empty;
            string rootPath = (_vault.RepoRoot ?? string.Empty).TrimEnd('\\', '/');
            if (relative.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase)) relative = relative.Substring(rootPath.Length);
            relative = relative.TrimStart('\\', '/');
            if (relative.StartsWith(".git", StringComparison.OrdinalIgnoreCase) &&
                (relative.Length == 4 || relative[4] == '\\' || relative[4] == '/')) return;
            string name = Path.GetFileName(relative);
            if (name.StartsWith("~$", StringComparison.Ordinal)) return; // SolidWorks lock/temp files

            try
            {
                BeginInvoke(new Action(delegate
                {
                    if (_watchDebounce == null || !_initialized) return;
                    _watchDebounce.Stop();
                    _watchDebounce.Start();
                }));
            }
            catch (InvalidOperationException) { }
        }

        private async Task LoadIdentityAsync()
        {
            try
            {
                Tuple<string, string> identity = await _vault.GetCommitIdentityAsync();
                if (identity != null)
                {
                    _commitName = identity.Item1;
                    _commitEmail = identity.Item2;
                }
            }
            catch (VaultException)
            {
                // Not fatal here. Check In will ask again and show the real error.
            }

            UpdateIdentityLabel();
        }

        private void UpdateIdentityLabel()
        {
            if (_identityLabel == null)
            {
                return;
            }

            _identityLabel.Text = string.IsNullOrWhiteSpace(_commitName) ? "No git name set" : _commitName;
            if (_identityItem != null)
            {
                _identityItem.Text = string.IsNullOrWhiteSpace(_commitName)
                    ? "Check-ins are signed as: (not set yet)"
                    : "Check-ins are signed as " + _commitName +
                      (string.IsNullOrWhiteSpace(_commitEmail) ? string.Empty : " <" + _commitEmail + ">");
            }
        }

        private async Task ReloadAsync(bool fetchRemote)
        {
            List<string> keepSelection = GetSelectedPaths();
            IReadOnlyList<VaultFile> files = await _vault.GetFilesAsync(fetchRemote);

            _files.Clear();
            if (files != null)
            {
                _files.AddRange(files);
            }

            _lastRefreshUtc = DateTime.UtcNow;
            ApplyFilter(keepSelection);

            if (!_busy)
            {
                UpdateSummary();
            }
        }

        private async Task RefreshWithBusyAsync(string status, bool fetchRemote)
        {
            if (_busy)
            {
                return;
            }

            SetBusy(true, status);
            try
            {
                await ReloadAsync(fetchRemote);
            }
            catch (VaultException ex)
            {
                ErrorDialog.ShowException(this, ex);
            }
            catch (Exception ex)
            {
                ErrorDialog.ShowException(this, ex);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async void MainForm_Activated(object sender, EventArgs e)
        {
            try
            {
                if (!_initialized || _busy)
                {
                    return;
                }

                if ((DateTime.UtcNow - _lastRefreshUtc).TotalSeconds < 20)
                {
                    return;
                }

                await RefreshWithBusyAsync("Refreshing\u2026", false);
            }
            catch (Exception ex)
            {
                ErrorDialog.ShowException(this, ex);
            }
        }

        private async void Timer_Tick(object sender, EventArgs e)
        {
            try
            {
                if (!_initialized || _busy)
                {
                    return;
                }

                await RefreshWithBusyAsync("Checking for updates\u2026", true);
            }
            catch (Exception ex)
            {
                ErrorDialog.ShowException(this, ex);
            }
        }

        // ------------------------------------------------------------------ actions

        private async void GetLatest_Click(object sender, EventArgs e)
        {
            try
            {
                if (_busy)
                {
                    return;
                }

                await RunBusyAsync("Getting the latest\u2026", async delegate
                {
                    await _vault.GetLatestAsync();
                    await ReloadAsync(true);
                });
            }
            catch (Exception ex)
            {
                ErrorDialog.ShowException(this, ex);
            }
        }

        private async void CheckOut_Click(object sender, EventArgs e)
        {
            try
            {
                await CheckOutSelectedAsync();
            }
            catch (Exception ex)
            {
                ErrorDialog.ShowException(this, ex);
            }
        }

        private async Task CheckOutSelectedAsync()
        {
            if (_busy)
            {
                return;
            }

            List<VaultFile> targets = SelectedFiles().Where(CanCheckOut).ToList();
            if (targets.Count == 0)
            {
                return;
            }

            List<string> paths = targets.Select(file => file.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            if (paths.Any(PartLock.Platform.SolidWorksRefs.IsAssembly))
            {
                List<string> expanded = null;
                bool cancelled = false;
                SetBusy(true, "Finding sub-assemblies\u2026");
                try
                {
                    expanded = await SubAssemblyPrompt.ExpandAsync(this, _vault, _files.ToList(), paths, AppendLog);
                    cancelled = expanded == null;
                }
                catch (Exception ex)
                {
                    // Never block a check-out over this; just check out what was picked.
                    ErrorDialog.LogToFile("Sub-assembly lookup failed.", ex.ToString());
                    expanded = paths;
                }
                finally
                {
                    SetBusy(false);
                }
                if (cancelled) return;
                paths = expanded;
            }

            bool done = false;
            await RunBusyAsync("Checking out " + FileCountText(paths.Count) + "\u2026", async delegate
            {
                await _vault.CheckOutAsync(paths);
                await ReloadAsync(false);
                done = true;
            });
            if (done) WarnIfOpenReadOnly(paths);
        }

        /// <summary>
        /// SolidWorks keeps a file read-only if it was opened before the check-out. Tell the user to
        /// close and reopen it, otherwise "Save" fails and they think PartLock is broken.
        /// </summary>
        private void WarnIfOpenReadOnly(IEnumerable<string> paths)
        {
            List<string> open = new List<string>();
            foreach (string path in paths)
            {
                string full = Path.Combine(_vault.RepoRoot ?? string.Empty, path.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(full) && VaultService.IsFileOpenElsewhere(full))
                    open.Add(Path.GetFileName(path));
            }
            if (open.Count == 0) return;
            MessageBox.Show(this,
                "Checked out. " + string.Join(", ", open.ToArray()) + (open.Count == 1 ? " is" : " are") +
                " already open, probably read-only in SolidWorks.\n\nClose " + (open.Count == 1 ? "it" : "them") +
                " in SolidWorks and open again so you can save your changes.",
                "PartLock", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private async void CheckIn_Click(object sender, EventArgs e)
        {
            try
            {
                await CheckInFlowAsync();
            }
            catch (Exception ex)
            {
                ErrorDialog.ShowException(this, ex);
            }
        }

        private async Task CheckInFlowAsync()
        {
            if (_busy)
            {
                return;
            }

            List<VaultFile> mine = _files.Where(IsMineToCheckIn)
                .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToList();
            if (mine.Count == 0)
            {
                MessageBox.Show(this, "You have nothing to check in. Check Out a file before editing it.",
                    "PartLock", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            bool identityOk = false;
            await RunBusyAsync("Preparing check-in\u2026", async delegate
            {
                identityOk = await EnsureIdentityAsync();
            });

            if (!identityOk || _busy)
            {
                return;
            }

            var selectedPaths = new HashSet<string>(GetSelectedPaths(), StringComparer.OrdinalIgnoreCase);
            List<string> preselected = mine.Where(file => selectedPaths.Contains(file.Path))
                .Select(file => file.Path).ToList();
            if (preselected.Count == 0)
            {
                preselected = mine.Select(file => file.Path).ToList();
            }

            List<string> paths;
            string message;
            bool keepCheckedOut;

            using (var dialog = new CheckInDialog(mine, preselected, _vault.ServerUserName))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                paths = new List<string>(dialog.SelectedPaths);
                message = dialog.Message;
                keepCheckedOut = dialog.KeepCheckedOut;
            }

            if (paths.Count == 0)
            {
                return;
            }

            // Files edited without pressing Check Out first: check them out now (if nobody else
            // has them and the server has no newer version), so the check-in can go through.
            var editedWithoutCheckOut = new HashSet<string>(
                mine.Where(file => file.State == FileState.ModifiedNotCheckedOut).Select(file => file.Path),
                StringComparer.OrdinalIgnoreCase);
            List<string> needCheckOut = paths.Where(editedWithoutCheckOut.Contains).ToList();

            await RunBusyAsync("Checking in " + FileCountText(paths.Count) + "\u2026", async delegate
            {
                if (needCheckOut.Count > 0)
                {
                    AppendLog("Checking out " + FileCountText(needCheckOut.Count) + " you edited without checking out first\u2026");
                    await _vault.CheckOutAsync(needCheckOut);
                }
                List<string> toCommit = await SubAssemblyPrompt.AddSavedListsAsync(_vault, paths);
                await _vault.CheckInAsync(toCommit, message, keepCheckedOut);
                await ReloadAsync(false);
            });
        }

        private async Task<bool> EnsureIdentityAsync()
        {
            if (_identityConfirmed)
            {
                return true;
            }

            Tuple<string, string> identity = await _vault.GetCommitIdentityAsync();
            if (identity == null)
            {
                _commitName = null;
                _commitEmail = null;
            }
            else
            {
                _commitName = identity.Item1;
                _commitEmail = identity.Item2;
            }

            if (string.IsNullOrWhiteSpace(_commitName) || string.IsNullOrWhiteSpace(_commitEmail))
            {
                using (var dialog = new IdentityDialog(_commitName, _commitEmail))
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK)
                    {
                        return false;
                    }

                    await _vault.SetCommitIdentityAsync(dialog.PersonName, dialog.PersonEmail);
                    _commitName = dialog.PersonName;
                    _commitEmail = dialog.PersonEmail;
                    UpdateIdentityLabel();
                }
            }

            _identityConfirmed = true;
            return true;
        }

        private async void Undo_Click(object sender, EventArgs e)
        {
            try
            {
                await UndoSelectedAsync();
            }
            catch (Exception ex)
            {
                ErrorDialog.ShowException(this, ex);
            }
        }

        private async Task UndoSelectedAsync()
        {
            if (_busy)
            {
                return;
            }

            List<VaultFile> targets = SelectedFiles().Where(CanUndo).ToList();
            if (targets.Count == 0)
            {
                return;
            }

            string question = "Throw away your changes to " + FileCountText(targets.Count)
                            + "? This can't be undone.";
            DialogResult answer = MessageBox.Show(this, question, "PartLock",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes)
            {
                return;
            }

            List<string> paths = targets.Select(file => file.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            await RunBusyAsync("Undoing check-out\u2026", async delegate
            {
                await _vault.UndoCheckOutAsync(paths);
                await ReloadAsync(false);
            });
        }

        private async Task RunBusyAsync(string status, Func<Task> action)
        {
            if (_busy)
            {
                return;
            }

            SetBusy(true, status);
            try
            {
                await action();
            }
            catch (VaultException ex)
            {
                ErrorDialog.ShowException(this, ex);
            }
            catch (Exception ex)
            {
                ErrorDialog.ShowException(this, ex);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void SetBusy(bool busy, string status = null)
        {
            _busy = busy;
            _progress.Visible = busy;

            if (busy)
            {
                _statusText.Text = status ?? "Working\u2026";
            }
            else
            {
                UpdateSummary();
            }

            _search.Enabled = !busy;
            _filter.Enabled = !busy;
            UpdateActionButtons();
        }

        private void UpdateSummary()
        {
            if (_statusText == null)
            {
                return;
            }

            int mine = _files.Count(file => file.State == FileState.CheckedOutByMe);
            int others = _files.Count(file => file.State == FileState.LockedByOther ||
                                               file.State == FileState.ConflictLockedByOther);
            string refreshed = _lastRefreshUtc == DateTime.MinValue
                ? string.Empty
                : " \u00B7 refreshed " + _lastRefreshUtc.ToLocalTime().ToString("HH:mm");

            _statusText.Text = mine + " checked out by you \u00B7 " + others + " by others" + refreshed;
        }

        private void UpdateActionButtons()
        {
            if (_getLatestButton == null)
            {
                return;
            }

            List<VaultFile> selected = SelectedFiles();
            _getLatestButton.Enabled = !_busy;
            _checkOutButton.Enabled = !_busy && selected.Any(CanCheckOut);
            int pending = _files.Count(IsMineToCheckIn);
            int edited = _files.Count(file => IsMineToCheckIn(file) &&
                (file.Modified || file.State == FileState.NewFile || file.State == FileState.Deleted));
            _checkInButton.Enabled = !_busy && pending > 0;
            _checkInButton.Text = pending > 0 ? "Check In (" + pending + ")\u2026" : "Check In\u2026";
            StylePrimary(_checkInButton, edited > 0);
            StylePrimary(_getLatestButton, edited == 0);
            if (_changesBanner != null)
            {
                bool show = edited > 0;
                if (show)
                {
                    _changesText.Text = "You have " + FileCountText(edited) + " with changes that aren't on GitHub yet. "
                                      + "Check In to upload them so the team gets them.";
                }
                if (_changesBanner.Visible != show) _changesBanner.Visible = show;
            }
            _undoButton.Enabled = !_busy && selected.Any(CanUndo);
        }

        private static void StylePrimary(Button button, bool primary)
        {
            Color back = primary ? Theme.AccentColor : Theme.White;
            if (button.BackColor == back) return;
            button.BackColor = back;
            button.ForeColor = primary ? Theme.White : Theme.TextColor;
            button.FlatAppearance.BorderSize = primary ? 0 : 1;
            button.FlatAppearance.MouseOverBackColor = primary ? Theme.AccentHover : Theme.HoverColor;
            button.FlatAppearance.MouseDownBackColor = primary ? Theme.AccentPressed : Theme.PressedColor;
        }

        private static bool CanCheckOut(VaultFile file)
        {
            return file != null && file.IsLockable &&
                   (file.State == FileState.Available ||
                    file.State == FileState.OutOfDate ||
                    file.State == FileState.ModifiedNotCheckedOut);
        }

        private static bool CanUndo(VaultFile file)
        {
            return file != null &&
                   (file.State == FileState.CheckedOutByMe ||
                    file.State == FileState.ModifiedNotCheckedOut ||
                    file.State == FileState.Changed ||
                    file.State == FileState.Deleted);
        }

        private static bool IsMineToCheckIn(VaultFile file)
        {
            return file != null &&
                   (file.State == FileState.CheckedOutByMe ||
                    (file.State == FileState.ModifiedNotCheckedOut && (file.Lock == null || file.Lock.IsMine)) ||
                    file.State == FileState.NewFile ||
                    file.State == FileState.Changed ||
                    file.State == FileState.Deleted);
        }

        // ------------------------------------------------------------ list contents

        private void ApplyFilter(ICollection<string> keepSelection)
        {
            if (_list == null)
            {
                return;
            }

            var keep = keepSelection == null
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(keepSelection, StringComparer.OrdinalIgnoreCase);

            IEnumerable<VaultFile> view = _files;
            string filter = _filter == null ? "All files" : (_filter.SelectedItem as string ?? "All files");

            if (filter == "My check-outs")
            {
                view = view.Where(file => file.State == FileState.CheckedOutByMe);
            }
            else if (filter == "Checked out by others")
            {
                view = view.Where(file => file.State == FileState.LockedByOther ||
                                          file.State == FileState.ConflictLockedByOther);
            }
            else if (filter == "Changed / new")
            {
                view = view.Where(file => file.State == FileState.NewFile ||
                                          file.State == FileState.Changed ||
                                          file.State == FileState.Deleted ||
                                          file.State == FileState.ModifiedNotCheckedOut);
            }
            else if (filter == "Needs update")
            {
                view = view.Where(file => file.State == FileState.OutOfDate);
            }

            string query = _search == null ? string.Empty : _search.Text.Trim();
            List<VaultFile> visible;
            if (query.Length > 0)
            {
                // File names come first. Folders / owners only count when no file name matches,
                // or when the search itself looks like a path ("cad/").
                bool pathQuery = query.IndexOf('/') >= 0 || query.IndexOf('\\') >= 0;
                string pathText = query.Replace('\\', '/');
                List<VaultFile> candidates = view.ToList();
                List<VaultFile> byName = candidates.Where(file => NameRank(file, query) < 2).ToList();
                if (byName.Count > 0 && !pathQuery)
                {
                    visible = byName;
                    SortFiles(visible);
                    visible = visible.OrderBy(file => NameRank(file, query)).ToList(); // stable: keeps column sort within a rank
                }
                else
                {
                    visible = candidates.Where(file => Matches(file, pathQuery ? pathText : query)).ToList();
                    SortFiles(visible);
                }
            }
            else
            {
                visible = view.ToList();
                SortFiles(visible);
            }

            var reselect = new List<ListViewItem>();
            _list.BeginUpdate();
            try
            {
                _list.Items.Clear();
                foreach (VaultFile file in visible)
                {
                    var item = new ListViewItem(file.FileName)
                    {
                        Tag = file,
                        ImageIndex = 0,
                        ToolTipText = file.Path
                    };
                    item.SubItems.Add(file.Folder);
                    item.SubItems.Add(Theme.StatusText(file, _vault.ServerUserName));
                    item.SubItems.Add(LockOwnerColumn(file));
                    item.SubItems.Add(SinceColumn(file));
                    _list.Items.Add(item);

                    if (keep.Contains(file.Path))
                    {
                        reselect.Add(item);
                    }
                }
            }
            finally
            {
                _list.EndUpdate();
            }

            foreach (ListViewItem item in reselect)
            {
                item.Selected = true;
            }

            UpdateEmptyState(visible.Count);
            UpdateActionButtons();
        }

        /// <summary>0 = name starts with the search, 1 = name contains it, 2 = no name match.</summary>
        private static int NameRank(VaultFile file, string query)
        {
            string name = file == null ? null : file.FileName;
            if (string.IsNullOrEmpty(name)) return 2;
            if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 0;
            if (name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) return 1;
            return 2;
        }

        private static bool Matches(VaultFile file, string query)
        {
            if (file == null)
            {
                return false;
            }

            if (file.Path != null && file.Path.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            return file.Lock != null && file.Lock.Owner != null &&
                   file.Lock.Owner.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void SortFiles(List<VaultFile> files)
        {
            Comparison<VaultFile> compare;
            switch (_sortColumn)
            {
                case 1:
                    compare = delegate(VaultFile a, VaultFile b)
                    {
                        int result = string.Compare(a.Folder, b.Folder, StringComparison.OrdinalIgnoreCase);
                        return result != 0
                            ? result
                            : string.Compare(a.FileName, b.FileName, StringComparison.OrdinalIgnoreCase);
                    };
                    break;

                case 2:
                    compare = delegate(VaultFile a, VaultFile b)
                    {
                        return string.Compare(
                            Theme.StatusText(a, _vault.ServerUserName),
                            Theme.StatusText(b, _vault.ServerUserName),
                            StringComparison.OrdinalIgnoreCase);
                    };
                    break;

                case 3:
                    compare = delegate(VaultFile a, VaultFile b)
                    {
                        return string.Compare(OwnerForSort(a), OwnerForSort(b),
                            StringComparison.OrdinalIgnoreCase);
                    };
                    break;

                case 4:
                    compare = delegate(VaultFile a, VaultFile b)
                    {
                        return LockDate(a).CompareTo(LockDate(b));
                    };
                    break;

                default:
                    compare = delegate(VaultFile a, VaultFile b)
                    {
                        return string.Compare(a.FileName, b.FileName, StringComparison.OrdinalIgnoreCase);
                    };
                    break;
            }

            files.Sort(delegate(VaultFile a, VaultFile b)
            {
                int result = compare(a, b);
                if (result == 0)
                {
                    result = string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
                }

                return _sortAscending ? result : -result;
            });
        }

        private void List_ColumnClick(object sender, ColumnClickEventArgs e)
        {
            if (e.Column == _sortColumn)
            {
                _sortAscending = !_sortAscending;
            }
            else
            {
                _sortColumn = e.Column;
                _sortAscending = true;
            }

            UpdateHeaderArrows();
            ApplyFilter(GetSelectedPaths());
        }

        private void UpdateHeaderArrows()
        {
            if (_list == null || _list.Columns.Count < 5)
            {
                return;
            }

            string[] names = { "Name", "Folder", "Status", "Checked out by", "Since" };
            for (int index = 0; index < names.Length; index++)
            {
                string arrow = index == _sortColumn ? (_sortAscending ? "  \u25B2" : "  \u25BC") : string.Empty;
                _list.Columns[index].Text = names[index] + arrow;
            }
        }

        private void UpdateEmptyState(int visibleCount)
        {
            if (_emptyLabel == null)
            {
                return;
            }

            bool show = visibleCount == 0;
            _emptyLabel.Visible = show;
            if (!show)
            {
                return;
            }

            _emptyLabel.Text = _files.Count == 0
                ? "No files yet. Save SolidWorks files inside " + _repoName + " and they'll show up here."
                : "No files match your search or filter.";
            _emptyLabel.BringToFront();
        }

        private List<VaultFile> SelectedFiles()
        {
            var result = new List<VaultFile>();
            if (_list == null)
            {
                return result;
            }

            foreach (ListViewItem item in _list.SelectedItems)
            {
                var file = item.Tag as VaultFile;
                if (file != null)
                {
                    result.Add(file);
                }
            }

            return result;
        }

        private List<string> GetSelectedPaths()
        {
            return SelectedFiles().Select(file => file.Path).ToList();
        }

        private string LockOwnerColumn(VaultFile file)
        {
            if (file.Lock == null)
            {
                return "\u2014";
            }

            return Theme.OwnerText(file, _vault.ServerUserName);
        }

        private static string SinceColumn(VaultFile file)
        {
            return file.Lock == null ? string.Empty : Theme.RelativeTime(file.Lock.LockedAtUtc);
        }

        private string OwnerForSort(VaultFile file)
        {
            return file.Lock == null ? string.Empty : Theme.OwnerText(file, _vault.ServerUserName);
        }

        private static DateTime LockDate(VaultFile file)
        {
            return file.Lock == null ? DateTime.MinValue : file.Lock.LockedAtUtc;
        }

        // ------------------------------------------------------------- status pills

        private void List_DrawColumnHeader(object sender, DrawListViewColumnHeaderEventArgs e)
        {
            e.DrawDefault = true;
        }

        private void List_DrawItem(object sender, DrawListViewItemEventArgs e)
        {
            // Details view: cells are painted in DrawSubItem so only the Status cell is custom.
        }

        private void List_DrawSubItem(object sender, DrawListViewSubItemEventArgs e)
        {
            if (e.ColumnIndex != 2)
            {
                e.DrawDefault = true;
                return;
            }

            var file = e.Item.Tag as VaultFile;
            if (file == null)
            {
                e.DrawDefault = true;
                return;
            }

            Graphics graphics = e.Graphics;
            Color rowBack = e.Item.Selected ? SystemColors.Highlight : _list.BackColor;
            using (var backBrush = new SolidBrush(rowBack))
            {
                graphics.FillRectangle(backBrush, e.Bounds);
            }

            string text = Theme.StatusText(file, _vault.ServerUserName);
            Color color = Theme.StatusColor(file.State);
            int pad = _list.LogicalToDeviceUnits(7);
            int dot = _list.LogicalToDeviceUnits(6);
            int gap = _list.LogicalToDeviceUnits(6);
            int pillHeight = _list.LogicalToDeviceUnits(23);
            int availableHeight = e.Bounds.Height - _list.LogicalToDeviceUnits(6);
            if (pillHeight > availableHeight)
            {
                pillHeight = Math.Max(_list.LogicalToDeviceUnits(16), availableHeight);
            }

            Size textSize = TextRenderer.MeasureText(text, Theme.Pill,
                new Size(int.MaxValue, pillHeight),
                TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            int maxWidth = e.Bounds.Width - pad * 2;
            int pillWidth = Math.Min(maxWidth, pad + dot + gap + textSize.Width + pad);

            if (pillWidth < _list.LogicalToDeviceUnits(28))
            {
                e.DrawDefault = true;
                return;
            }

            var pill = new Rectangle(
                e.Bounds.X + pad,
                e.Bounds.Y + (e.Bounds.Height - pillHeight) / 2,
                pillWidth,
                pillHeight);

            Color fill = Theme.Lighten(color, 0.90);
            Color edge = Theme.Lighten(color, 0.55);

            SmoothingMode oldMode = graphics.SmoothingMode;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = Theme.RoundedRect(pill, pillHeight / 2))
            {
                using (var brush = new SolidBrush(fill))
                {
                    graphics.FillPath(brush, path);
                }

                using (var pen = new Pen(edge))
                {
                    graphics.DrawPath(pen, path);
                }
            }

            int dotX = pill.X + pad;
            int dotY = pill.Y + (pill.Height - dot) / 2;
            using (var dotBrush = new SolidBrush(color))
            {
                graphics.FillEllipse(dotBrush, dotX, dotY, dot, dot);
            }

            var textRect = new Rectangle(
                dotX + dot + gap,
                pill.Y,
                pill.Right - (dotX + dot + gap) - pad,
                pill.Height);
            TextRenderer.DrawText(graphics, text, Theme.Pill, textRect, color,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            graphics.SmoothingMode = oldMode;
        }

        // ------------------------------------------------------------------ opening

        private void List_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right)
            {
                return;
            }

            ListViewItem item = _list.GetItemAt(e.X, e.Y);
            if (item != null && !item.Selected)
            {
                _list.SelectedItems.Clear();
                item.Selected = true;
                item.Focused = true;
            }
        }

        private async void List_DoubleClick(object sender, EventArgs e)
        {
            try
            {
                await OpenSelectedFileAsync();
            }
            catch (Exception ex)
            {
                ErrorDialog.ShowException(this, ex);
            }
        }

        private async Task OpenSelectedFileAsync()
        {
            if (_busy)
            {
                return;
            }

            VaultFile file = SelectedFiles().FirstOrDefault();
            if (file == null)
            {
                return;
            }

            bool needsPrompt = file.IsLockable && file.IsTracked &&
                               file.State != FileState.CheckedOutByMe &&
                               file.State != FileState.Deleted;
            if (!needsPrompt)
            {
                OpenFile(file);
                return;
            }

            if (file.State == FileState.LockedByOther || file.State == FileState.ConflictLockedByOther)
            {
                DialogResult ro = MessageBox.Show(this,
                    Theme.OwnerText(file, _vault.ServerUserName) + " has " + file.FileName + " checked out, " +
                    "so you can only look at it.\n\nOpen it read-only?",
                    "PartLock", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
                if (ro == DialogResult.OK) OpenFile(file);
                return;
            }

            OpenChoice choice = OpenPromptDialog.Ask(this, file, _vault.ServerUserName);
            if (choice == OpenChoice.Cancel)
            {
                return;
            }

            if (choice == OpenChoice.ReadOnly)
            {
                OpenFile(file);
                return;
            }

            bool opened = false;
            await RunBusyAsync("Checking out " + FileCountText(1) + "\u2026", async delegate
            {
                await _vault.CheckOutAsync(new List<string> { file.Path });
                await ReloadAsync(false);
                opened = true;
            });

            if (opened)
            {
                OpenFile(file);
            }
        }

        private void OpenFile(VaultFile file)
        {
            if (file == null)
            {
                return;
            }

            string fullPath = FullPath(file);
            if (!File.Exists(fullPath))
            {
                MessageBox.Show(this,
                    "That file isn't on this computer yet. Use \"Get Latest\", then try again.",
                    "PartLock", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo { FileName = fullPath, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                ErrorDialog.ShowException(this, ex);
            }
        }

        private string FullPath(VaultFile file)
        {
            string root = _vault.RepoRoot ?? string.Empty;
            string relative = (file.Path ?? string.Empty).Replace('/', Path.DirectorySeparatorChar);
            return Path.Combine(root, relative);
        }

        private void Open_Click(object sender, EventArgs e)
        {
            VaultFile file = SelectedFiles().FirstOrDefault();
            if (file != null)
            {
                OpenFile(file);
            }
        }

        private void Explorer_Click(object sender, EventArgs e)
        {
            VaultFile file = SelectedFiles().FirstOrDefault();
            if (file == null)
            {
                return;
            }

            try
            {
                string arguments = "/select,\"" + FullPath(file) + "\"";
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = arguments,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                ErrorDialog.ShowException(this, ex);
            }
        }

        // -------------------------------------------------------------------- menu

        private void Menu_Opening(object sender, CancelEventArgs e)
        {
            List<VaultFile> selected = SelectedFiles();
            bool one = selected.Count == 1;
            bool free = !_busy;

            _menuOpen.Enabled = free && one;
            _menuExplorer.Enabled = free && one;
            _menuCheckOut.Enabled = free && selected.Any(CanCheckOut);
            _menuCheckIn.Enabled = free && _files.Any(IsMineToCheckIn);
            _menuUndo.Enabled = free && selected.Any(CanUndo);
            _menuHistory.Enabled = free && one;
            _menuForceUnlock.Enabled = free && selected.Any(file => file.State == FileState.LockedByOther);
        }

        private async void MenuCheckOut_Click(object sender, EventArgs e)
        {
            try
            {
                await CheckOutSelectedAsync();
            }
            catch (Exception ex)
            {
                ErrorDialog.ShowException(this, ex);
            }
        }

        private async void MenuCheckIn_Click(object sender, EventArgs e)
        {
            try
            {
                await CheckInFlowAsync();
            }
            catch (Exception ex)
            {
                ErrorDialog.ShowException(this, ex);
            }
        }

        private async void MenuUndo_Click(object sender, EventArgs e)
        {
            try
            {
                await UndoSelectedAsync();
            }
            catch (Exception ex)
            {
                ErrorDialog.ShowException(this, ex);
            }
        }

        private async void MenuHistory_Click(object sender, EventArgs e)
        {
            try
            {
                await ShowHistoryAsync();
            }
            catch (Exception ex)
            {
                ErrorDialog.ShowException(this, ex);
            }
        }

        private async Task ShowHistoryAsync()
        {
            if (_busy)
            {
                return;
            }

            VaultFile file = SelectedFiles().FirstOrDefault();
            if (file == null)
            {
                return;
            }

            IReadOnlyList<HistoryEntry> entries = null;
            await RunBusyAsync("Loading history\u2026", async delegate
            {
                entries = await _vault.GetHistoryAsync(file.Path);
            });

            if (entries == null)
            {
                return;
            }

            using (var dialog = new HistoryDialog(file.Path, entries))
            {
                dialog.ShowDialog(this);
            }
        }

        private async void MenuForceUnlock_Click(object sender, EventArgs e)
        {
            try
            {
                await ForceUnlockSelectedAsync();
            }
            catch (Exception ex)
            {
                ErrorDialog.ShowException(this, ex);
            }
        }

        private async Task ForceUnlockSelectedAsync()
        {
            if (_busy)
            {
                return;
            }

            List<VaultFile> targets = SelectedFiles()
                .Where(file => file.State == FileState.LockedByOther && file.Lock != null).ToList();
            if (targets.Count == 0)
            {
                return;
            }

            List<string> owners = targets
                .Select(file => Theme.OwnerText(file, _vault.ServerUserName))
                .Where(owner => !string.IsNullOrWhiteSpace(owner))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            string who = owners.Count == 0 ? "Someone" : string.Join(", ", owners.ToArray());
            string what = targets.Count == 1 ? targets[0].FileName : targets.Count + " files";

            string warning = "Force unlock " + what + "?\n\n" + who +
                " has it checked out. They won't be able to check in their work. " +
                "Only do this if they are no longer working on it.";
            DialogResult answer = MessageBox.Show(this, warning, "Force unlock",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes)
            {
                return;
            }

            List<LockInfo> locks = targets.Select(file => file.Lock)
                .Where(lockInfo => lockInfo != null).ToList();

            await RunBusyAsync("Force unlocking\u2026", async delegate
            {
                foreach (LockInfo lockInfo in locks)
                {
                    await _vault.ForceUnlockAsync(lockInfo);
                }

                await ReloadAsync(false);
            });
        }

        // ------------------------------------------------------------------ chrome

        private void ActivityToggle_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            _activityVisible = !_activityVisible;
            _activityPanel.Visible = _activityVisible;
            _activityRow.Height = _activityVisible ? LogicalToDeviceUnits(150) : 0f;
            _activityToggle.Text = _activityVisible ? "Hide activity \u25B2" : "Show activity \u25BC";

            if (_activityVisible)
            {
                _activityBox.SelectionStart = _activityBox.TextLength;
                _activityBox.ScrollToCaret();
            }
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

            if (_logLines.Count >= 2200)
            {
                TrimLog();
            }

            _logLines.Enqueue(line);
            _activityBox.AppendText(line + Environment.NewLine);

            if (_activityVisible)
            {
                _activityBox.SelectionStart = _activityBox.TextLength;
                _activityBox.ScrollToCaret();
            }
        }

        private void TrimLog()
        {
            while (_logLines.Count > 2000)
            {
                _logLines.Dequeue();
            }

            _activityBox.Text = string.Join(Environment.NewLine, _logLines.ToArray()) +
                                Environment.NewLine;
        }

        private void UpdateLockingBanner()
        {
            if (_banner != null)
            {
                _banner.Visible = !_lockingConfigured;
            }
        }

        private async void LockingSetup_Click(object sender, EventArgs e)
        {
            try
            {
                if (_busy)
                {
                    return;
                }

                DialogResult answer = MessageBox.Show(this,
                    "Set up check-out locking for this folder?\n\n"
                    + "PartLock adds lock rules to the repo and stores the CAD files in Git LFS from now on. "
                    + "Your existing history is kept exactly as it is (nothing is rewritten or deleted).\n\n"
                    + "Everyone on the team gets this change the next time they Get Latest.",
                    "Set up locking", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2);
                if (answer != DialogResult.Yes)
                {
                    return;
                }

                await RunBusyAsync("Setting up locking\u2026", async delegate
                {
                    await _vault.EnableLockingAsync();
                    _lockingConfigured = await _vault.IsLockingConfiguredAsync();
                    UpdateLockingBanner();
                    await ReloadAsync(false);
                });
            }
            catch (Exception ex)
            {
                ErrorDialog.ShowException(this, ex);
            }
        }

        private void ChangeFolder_LinkClicked(object sender, EventArgs e)
        {
            if (_busy)
            {
                return;
            }

            try
            {
                using (var picker = new RepoPickerDialog(_account, _vault.RepoRoot))
                {
                    if (picker.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(picker.RepoRoot))
                    {
                        return;
                    }

                    if (string.Equals(picker.RepoRoot, _vault.RepoRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    AppSettings settings = AppSettings.Load();
                    settings.UseRepo(picker.RepoRoot);
                    settings.Save();
                }

                Application.Restart();
            }
            catch (Exception ex)
            {
                ErrorDialog.ShowException(this, ex);
            }
        }

        private async void ChangeIdentity_Click(object sender, EventArgs e)
        {
            if (_busy) return;
            using (var dialog = new IdentityDialog(_commitName, _commitEmail))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                await RunBusyAsync("Saving your name\u2026", async delegate
                {
                    await _vault.SetCommitIdentityAsync(dialog.PersonName, dialog.PersonEmail);
                    _commitName = dialog.PersonName;
                    _commitEmail = dialog.PersonEmail;
                    _identityConfirmed = true;
                    UpdateIdentityLabel();
                });
            }
        }

        private async void SwitchAccount_Click(object sender, EventArgs e)
        {
            if (_busy) return;
            DialogResult answer = MessageBox.Show(this,
                "Sign out of GitHub on this computer? PartLock will restart and ask you to sign in again "
                + "(you can use a different account).",
                "PartLock", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (answer != DialogResult.OK) return;

            bool done = false;
            await RunBusyAsync("Signing out\u2026", async delegate
            {
                await GitSetup.SignOutAsync(System.Threading.CancellationToken.None);
                done = true;
            });
            if (done) Application.Restart();
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (_busy && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                MessageBox.Show(this, "PartLock is still working. Try again in a moment.",
                    "PartLock", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _timer.Stop();
            if (_watcher != null) { _watcher.EnableRaisingEvents = false; _watcher.Dispose(); _watcher = null; }
            if (_watchDebounce != null) _watchDebounce.Stop();

            int mine = _files.Count(file => file.State == FileState.CheckedOutByMe);
            if (mine > 0 && e.CloseReason == CloseReason.UserClosing)
            {
                MessageBox.Show(this,
                    "You still have " + FileCountText(mine) +
                    " checked out. Others can't edit them until you check in.",
                    "PartLock", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        // ------------------------------------------------------------------ helpers

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            _list.Columns[0].Width = LogicalToDeviceUnits(240);
            _list.Columns[1].Width = LogicalToDeviceUnits(220);
            _list.Columns[2].Width = LogicalToDeviceUnits(230);
            _list.Columns[3].Width = LogicalToDeviceUnits(140);
            _list.Columns[4].Width = LogicalToDeviceUnits(130);
            SetupRowHeight();
            UpdateHeaderArrows();
        }

        private void SetupRowHeight()
        {
            int height = _list.Font.Height + _list.LogicalToDeviceUnits(12);
            if (height < 24)
            {
                height = 24;
            }

            _rowImages = new ImageList
            {
                ImageSize = new Size(1, height),
                ColorDepth = ColorDepth.Depth32Bit
            };

            // The ImageList keeps a reference and reads the bitmap later (when its handle is
            // created), so it must NOT be disposed here; the ImageList owns it from now on.
            var bitmap = new Bitmap(1, height);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Transparent);
            }
            _rowImages.Images.Add(bitmap);

            _list.SmallImageList = _rowImages;
            foreach (ListViewItem item in _list.Items)
            {
                item.ImageIndex = 0;
            }
        }

        private static string FileCountText(int count)
        {
            return count + (count == 1 ? " file" : " files");
        }

        private static string GetRepoName(string repoRoot)
        {
            if (string.IsNullOrWhiteSpace(repoRoot))
            {
                return "vault";
            }

            string trimmed = repoRoot.TrimEnd('\\', '/');
            if (trimmed.Length == 0)
            {
                return repoRoot;
            }

            int slash = Math.Max(trimmed.LastIndexOf('\\'), trimmed.LastIndexOf('/'));
            string name = slash >= 0 ? trimmed.Substring(slash + 1) : trimmed;
            return string.IsNullOrEmpty(name) ? "vault" : name;
        }

        private enum OpenChoice
        {
            CheckOut,
            ReadOnly,
            Cancel
        }

        private sealed class BufferedListView : ListView
        {
            public BufferedListView()
            {
                DoubleBuffered = true;
                SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            }
        }

        /// <summary>Small three-way prompt for double-clicking a lockable file that isn't checked out yet.</summary>
        private sealed class OpenPromptDialog : Form
        {
            private OpenChoice _choice = OpenChoice.Cancel;

            private OpenPromptDialog(VaultFile file, string serverUserName)
            {
                Text = "PartLock \u2013 Open file";
                Font = Theme.Base;
                BackColor = Theme.Panel;
                AutoScaleMode = AutoScaleMode.Dpi;
                AutoScaleDimensions = new SizeF(96f, 96f);
                StartPosition = FormStartPosition.CenterParent;
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MinimizeBox = false;
                MaximizeBox = false;
                ShowInTaskbar = false;
                ShowIcon = false;
                ClientSize = new Size(560, 265);

                var root = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    ColumnCount = 1,
                    RowCount = 4,
                    BackColor = Theme.Panel,
                    Padding = new Padding(24, 20, 24, 16)
                };
                root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

                var heading = new Label
                {
                    Text = "Check it out so you can edit it?",
                    Font = Theme.Heading,
                    ForeColor = Theme.Text,
                    AutoSize = true,
                    Margin = new Padding(0, 0, 0, 8)
                };
                root.Controls.Add(heading, 0, 0);

                var fileLabel = new Label
                {
                    Text = file == null ? string.Empty : file.Path,
                    Font = Theme.Small,
                    ForeColor = Theme.Muted,
                    AutoSize = true,
                    MaximumSize = new Size(500, 0),
                    Margin = new Padding(0, 0, 0, 10)
                };
                root.Controls.Add(fileLabel, 0, 1);

                var explanation = new Label
                {
                    Text = BuildExplanation(file, serverUserName),
                    Font = Theme.Base,
                    ForeColor = Theme.Text,
                    Dock = DockStyle.Fill,
                    AutoSize = false,
                    TextAlign = ContentAlignment.TopLeft,
                    Margin = new Padding(0, 0, 0, 10)
                };
                root.Controls.Add(explanation, 0, 2);

                var buttons = new FlowLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    FlowDirection = FlowDirection.RightToLeft,
                    WrapContents = false,
                    AutoSize = true,
                    Margin = new Padding(0)
                };

                var checkOut = Theme.CreateButton("Check out & open", ButtonKind.Primary);
                checkOut.Margin = new Padding(0, 0, 0, 0);
                checkOut.Click += delegate
                {
                    _choice = OpenChoice.CheckOut;
                    this.DialogResult = DialogResult.OK;
                    Close();
                };
                buttons.Controls.Add(checkOut);

                var readOnly = Theme.CreateButton("Open read-only", ButtonKind.Outline);
                readOnly.Margin = new Padding(0, 0, 8, 0);
                readOnly.Click += delegate
                {
                    _choice = OpenChoice.ReadOnly;
                    this.DialogResult = DialogResult.OK;
                    Close();
                };
                buttons.Controls.Add(readOnly);

                var cancel = Theme.CreateButton("Cancel", ButtonKind.Ghost);
                cancel.Margin = new Padding(0, 0, 8, 0);
                cancel.DialogResult = DialogResult.Cancel;
                buttons.Controls.Add(cancel);

                root.Controls.Add(buttons, 0, 3);
                Controls.Add(root);
                AcceptButton = checkOut;
                CancelButton = cancel;
            }

            public static OpenChoice Ask(IWin32Window owner, VaultFile file, string serverUserName)
            {
                using (var dialog = new OpenPromptDialog(file, serverUserName))
                {
                    DialogResult result = owner == null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
                    return result == DialogResult.OK ? dialog._choice : OpenChoice.Cancel;
                }
            }

            private static string BuildExplanation(VaultFile file, string serverUserName)
            {
                if (file == null)
                {
                    return string.Empty;
                }

                string owner = Theme.OwnerText(file, serverUserName);
                switch (file.State)
                {
                    case FileState.LockedByOther:
                        return owner + " has this checked out. Checking out may not work until they check in.";
                    case FileState.ConflictLockedByOther:
                        return "You have edits, but " + owner +
                               " has this checked out. Checking out won't work until they check in.";
                    case FileState.OutOfDate:
                        return "A newer version is on the server. Check-out will grab it first, "
                             + "then reserve the file for you.";
                    case FileState.ModifiedNotCheckedOut:
                        return "This file was edited without a check-out. Checking out will reserve it; "
                             + "your edits stay.";
                    default:
                        return "Check-out reserves the file so nobody else can edit it. "
                             + "You'll get the latest version first.";
                }
            }
        }
    }
}
