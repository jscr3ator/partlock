using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using PartLock.Core;

namespace PartLock.UI
{
    /// <summary>Pick which of my changes go to the team, plus the check-in message.</summary>
    public class CheckInDialog : Form
    {
        private readonly ListView _list;
        private readonly TextBox _message;
        private readonly Label _messageCue;
        private readonly CheckBox _keep;
        private readonly Button _checkIn;
        private ImageList _rowImages;
        private bool _checkInReady;

        public CheckInDialog(IReadOnlyList<VaultFile> files, IReadOnlyList<string> preselected, string serverUserName)
        {
            Text = "PartLock \u2013 Check in";
            Font = Theme.Base;
            BackColor = Theme.Background;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96f, 96f);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ShowIcon = false;
            ClientSize = new Size(780, 610);
            MinimumSize = new Size(620, 500);

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 7,
                BackColor = Theme.Background,
                Padding = new Padding(24, 18, 24, 16)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 92f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var heading = new Label
            {
                Text = "Check in your changes",
                Font = Theme.Heading,
                ForeColor = Theme.Text,
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 4)
            };
            root.Controls.Add(heading, 0, 0);

            var blurb = new Label
            {
                Text = "Pick the files to send to the team, then say what changed.",
                Font = Theme.Small,
                ForeColor = Theme.Muted,
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 12)
            };
            root.Controls.Add(blurb, 0, 1);

            var listHost = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Panel,
                Padding = new Padding(1),
                Margin = new Padding(0, 0, 0, 12)
            };
            listHost.Paint += delegate(object sender, PaintEventArgs e)
            {
                Theme.DrawBorder(e.Graphics, listHost.ClientRectangle, Theme.Border);
            };

            _list = new ListView
            {
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = true,
                CheckBoxes = true,
                HideSelection = false,
                BorderStyle = BorderStyle.None,
                Dock = DockStyle.Fill,
                BackColor = Theme.Panel,
                ForeColor = Theme.Text,
                Font = Theme.Base
            };
            _list.Columns.Add("File", Scale(400));
            _list.Columns.Add("Status", Scale(240));

            var checkedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (preselected != null)
            {
                foreach (string path in preselected)
                {
                    if (!string.IsNullOrEmpty(path))
                    {
                        checkedPaths.Add(path);
                    }
                }
            }

            bool checkAll = checkedPaths.Count == 0;
            if (files != null)
            {
                foreach (VaultFile file in files)
                {
                    var item = new ListViewItem(file.Path)
                    {
                        Tag = file,
                        ToolTipText = file.Path
                    };
                    item.SubItems.Add(Theme.StatusText(file, serverUserName));
                    item.SubItems[0].ForeColor = Theme.Text;
                    item.SubItems[1].ForeColor = Theme.StatusColor(file.State);
                    item.UseItemStyleForSubItems = false;
                    item.Checked = checkAll || checkedPaths.Contains(file.Path);
                    _list.Items.Add(item);
                }
            }

            listHost.Controls.Add(_list);
            root.Controls.Add(listHost, 0, 2);

            var messageLabel = new Label
            {
                Text = "What did you change?",
                Font = Theme.Bold,
                ForeColor = Theme.Text,
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 6)
            };
            root.Controls.Add(messageLabel, 0, 3);

            var messageHost = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Panel,
                Padding = new Padding(9, 7, 9, 7),
                Margin = new Padding(0, 0, 0, 10)
            };
            bool messageFocused = false;
            messageHost.Paint += delegate(object sender, PaintEventArgs e)
            {
                Theme.DrawBorder(e.Graphics, messageHost.ClientRectangle,
                    messageFocused ? Theme.Accent : Theme.Border);
            };

            _message = new TextBox
            {
                Multiline = true,
                AcceptsReturn = true,
                ScrollBars = ScrollBars.Vertical,
                BorderStyle = BorderStyle.None,
                Dock = DockStyle.Fill,
                Font = Theme.Base,
                BackColor = Theme.Panel,
                ForeColor = Theme.Text,
                MaxLength = 4000
            };
            _message.TextChanged += delegate { UpdateCue(); UpdateCheckInState(); };
            _message.Enter += delegate { messageFocused = true; messageHost.Invalidate(); UpdateCue(); };
            _message.Leave += delegate { messageFocused = false; messageHost.Invalidate(); UpdateCue(); };
            messageHost.Controls.Add(_message);

            _messageCue = new Label
            {
                Text = "What did you change? e.g. Thickened wing spar to 3 mm",
                Font = Theme.Base,
                ForeColor = Theme.Muted,
                BackColor = Theme.Panel,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.TopLeft,
                Cursor = Cursors.IBeam
            };
            _messageCue.Click += delegate { _message.Focus(); UpdateCue(); };
            messageHost.Controls.Add(_messageCue);
            _messageCue.BringToFront();

            root.Controls.Add(messageHost, 0, 4);

            _keep = new CheckBox
            {
                Text = "Keep files checked out (I'm still working on them)",
                Font = Theme.Base,
                ForeColor = Theme.Text,
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 10)
            };
            root.Controls.Add(_keep, 0, 5);

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                AutoSize = true,
                Margin = new Padding(0)
            };

            _checkIn = Theme.CreateButton("Check In", ButtonKind.Primary);
            _checkIn.DialogResult = DialogResult.OK;
            _checkIn.Margin = new Padding(0, 0, 0, 0);
            buttons.Controls.Add(_checkIn);

            var cancel = Theme.CreateButton("Cancel", ButtonKind.Outline);
            cancel.DialogResult = DialogResult.Cancel;
            cancel.Margin = new Padding(0, 0, 8, 0);
            buttons.Controls.Add(cancel);

            root.Controls.Add(buttons, 0, 6);
            Controls.Add(root);

            AcceptButton = _checkIn;
            CancelButton = cancel;

            _checkInReady = true;
            _list.ItemChecked += delegate { UpdateCheckInState(); };
            UpdateCue();
            UpdateCheckInState();
        }

        public IReadOnlyList<string> SelectedPaths
        {
            get
            {
                var paths = new List<string>();
                foreach (ListViewItem item in _list.CheckedItems)
                {
                    var file = item.Tag as VaultFile;
                    if (file != null)
                    {
                        paths.Add(file.Path);
                    }
                }

                return paths;
            }
        }

        public string Message
        {
            get { return _message.Text.Trim(); }
        }

        public bool KeepCheckedOut
        {
            get { return _keep.Checked; }
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            _list.Columns[0].Width = LogicalToDeviceUnits(360);
            _list.Columns[1].Width = LogicalToDeviceUnits(280);
            SetupRowHeight();
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

            // Owned by the ImageList from here on (it reads it lazily) - do not dispose.
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

        private void UpdateCue()
        {
            if (_messageCue != null)
            {
                _messageCue.Visible = _message.TextLength == 0 && !_message.Focused;
            }
        }

        private void UpdateCheckInState()
        {
            if (!_checkInReady)
            {
                return;
            }

            _checkIn.Enabled = _message.Text.Trim().Length > 0 && _list.CheckedItems.Count > 0;
        }

        private int Scale(int value)
        {
            return LogicalToDeviceUnits(value);
        }
    }
}
