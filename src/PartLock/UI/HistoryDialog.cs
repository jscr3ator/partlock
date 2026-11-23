using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using PartLock.Core;

namespace PartLock.UI
{
    /// <summary>Read-only commit history for one file (who changed it, when, and why).</summary>
    public class HistoryDialog : Form
    {
        private readonly ListView _list;
        private readonly Label _empty;

        public HistoryDialog(string path, IReadOnlyList<HistoryEntry> entries)
        {
            Text = "PartLock \u2013 History";
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
            ClientSize = new Size(780, 560);
            MinimumSize = new Size(600, 420);

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                BackColor = Theme.Background,
                Padding = new Padding(24, 18, 24, 16)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            string displayPath = path ?? string.Empty;
            int slash = displayPath.LastIndexOf('/');
            string fileName = slash >= 0 ? displayPath.Substring(slash + 1) : displayPath;

            var heading = new Label
            {
                Text = string.IsNullOrEmpty(fileName) ? "History" : fileName,
                Font = Theme.Heading,
                ForeColor = Theme.Text,
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 3)
            };
            root.Controls.Add(heading, 0, 0);

            var sub = new Label
            {
                Text = string.IsNullOrEmpty(displayPath) ? "This file" : displayPath,
                Font = Theme.Small,
                ForeColor = Theme.Muted,
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 12)
            };
            root.Controls.Add(sub, 0, 1);

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
                MultiSelect = false,
                HideSelection = false,
                BorderStyle = BorderStyle.None,
                Dock = DockStyle.Fill,
                BackColor = Theme.Panel,
                ForeColor = Theme.Text,
                Font = Theme.Base
            };
            _list.Columns.Add("When", LogicalToDeviceUnits(150));
            _list.Columns.Add("Who", LogicalToDeviceUnits(160));
            _list.Columns.Add("Version", LogicalToDeviceUnits(90));
            _list.Columns.Add("What changed", LogicalToDeviceUnits(300));

            var ordered = entries == null
                ? new List<HistoryEntry>()
                : entries.OrderByDescending(entry => entry.Date).ToList();

            foreach (HistoryEntry entry in ordered)
            {
                if (entry == null)
                {
                    continue;
                }

                string commit = entry.Commit ?? string.Empty;
                if (commit.Length > 7)
                {
                    commit = commit.Substring(0, 7);
                }

                var item = new ListViewItem(entry.Date.ToString("yyyy-MM-dd HH:mm"));
                item.SubItems.Add(entry.Author ?? "unknown");
                item.SubItems.Add(commit);
                item.SubItems.Add(entry.Message ?? string.Empty);
                item.ToolTipText = entry.Message ?? string.Empty;
                _list.Items.Add(item);
            }

            _list.Resize += delegate { SizeMessageColumn(); };
            listHost.Controls.Add(_list);
            root.Controls.Add(listHost, 0, 2);

            _empty = new Label
            {
                Text = "No history for this file yet.",
                Font = Theme.Base,
                ForeColor = Theme.Muted,
                BackColor = Theme.Panel,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Visible = ordered.Count == 0
            };
            listHost.Controls.Add(_empty);
            _empty.BringToFront();

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                AutoSize = true,
                Margin = new Padding(0)
            };
            var close = Theme.CreateButton("Close", ButtonKind.Primary);
            close.DialogResult = DialogResult.OK;
            close.Margin = new Padding(0, 0, 0, 0);
            buttons.Controls.Add(close);
            root.Controls.Add(buttons, 0, 3);

            Controls.Add(root);
            AcceptButton = close;
            CancelButton = close;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            _list.Columns[0].Width = LogicalToDeviceUnits(150);
            _list.Columns[1].Width = LogicalToDeviceUnits(160);
            _list.Columns[2].Width = LogicalToDeviceUnits(90);
            SizeMessageColumn();
        }

        private void SizeMessageColumn()
        {
            if (_list.Columns.Count < 4)
            {
                return;
            }

            int used = _list.Columns[0].Width + _list.Columns[1].Width + _list.Columns[2].Width;
            int width = _list.ClientSize.Width - used - 4;
            if (width > 120)
            {
                _list.Columns[3].Width = width;
            }
        }
    }
}
