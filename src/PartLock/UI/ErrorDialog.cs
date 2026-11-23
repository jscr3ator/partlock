using System;
using System.Drawing;
using System.Windows.Forms;
using PartLock.Core;

namespace PartLock.UI
{
    /// <summary>
    /// Friendly sentence on top, raw git output behind a "Show details" link, plus Copy.
    /// </summary>
    public class ErrorDialog : Form
    {
        private readonly string _details;
        private readonly RowStyle _detailsRow;
        private readonly Panel _detailsHost;
        private readonly LinkLabel _toggle;
        private readonly Button _copy;
        private bool _expanded;

        public ErrorDialog(string message, string details = null)
        {
            _details = details;

            Text = "PartLock \u2013 That didn't work";
            Font = Theme.Base;
            BackColor = Theme.Panel;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96f, 96f);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ShowIcon = false;
            ClientSize = new Size(580, 280);
            MinimumSize = new Size(480, 230);

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 5,
                BackColor = Theme.Panel,
                Padding = new Padding(24, 20, 24, 16)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

            var heading = new Label
            {
                Text = "That didn't work",
                Font = Theme.Heading,
                ForeColor = Theme.Text,
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 10)
            };
            root.Controls.Add(heading, 0, 0);

            var messageLabel = new Label
            {
                Text = string.IsNullOrWhiteSpace(message) ? "Something went wrong." : message,
                Font = Theme.Base,
                ForeColor = Theme.Text,
                Dock = DockStyle.Fill,
                AutoSize = false,
                TextAlign = ContentAlignment.TopLeft,
                Margin = new Padding(0, 0, 0, 8)
            };
            root.Controls.Add(messageLabel, 0, 1);

            _toggle = new LinkLabel
            {
                Text = "Show details \u25BE",
                Font = Theme.Small,
                LinkColor = Theme.Muted,
                ActiveLinkColor = Theme.Accent,
                VisitedLinkColor = Theme.Muted,
                AutoSize = true,
                LinkBehavior = LinkBehavior.HoverUnderline,
                Margin = new Padding(0, 0, 0, 6),
                Visible = !string.IsNullOrEmpty(_details)
            };
            _toggle.LinkClicked += Toggle_LinkClicked;
            root.Controls.Add(_toggle, 0, 2);

            _detailsRow = new RowStyle(SizeType.Absolute, 0f);
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(_detailsRow);
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            _detailsHost = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Panel,
                Margin = new Padding(0, 0, 0, 10),
                Visible = false
            };
            var detailsBox = new TextBox
            {
                Text = _details ?? string.Empty,
                Multiline = true,
                ReadOnly = true,
                WordWrap = false,
                ScrollBars = ScrollBars.Both,
                Dock = DockStyle.Fill,
                Font = Theme.Mono,
                BackColor = Theme.LogBackground,
                ForeColor = Theme.Text,
                BorderStyle = BorderStyle.FixedSingle
            };
            _detailsHost.Controls.Add(detailsBox);
            root.Controls.Add(_detailsHost, 0, 3);

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                AutoSize = true,
                Margin = new Padding(0)
            };

            var close = Theme.CreateButton("Close", ButtonKind.Primary);
            close.DialogResult = DialogResult.Cancel;
            close.Margin = new Padding(0, 0, 0, 0);
            buttons.Controls.Add(close);

            _copy = Theme.CreateButton("Copy details", ButtonKind.Outline);
            _copy.Visible = false;
            _copy.Margin = new Padding(0, 0, 8, 0);
            _copy.Click += Copy_Click;
            buttons.Controls.Add(_copy);

            root.Controls.Add(buttons, 0, 4);
            Controls.Add(root);

            AcceptButton = close;
            CancelButton = close;
        }

        private void Toggle_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            _expanded = !_expanded;
            _detailsHost.Visible = _expanded;
            _copy.Visible = _expanded;
            _toggle.Text = _expanded ? "Hide details \u25B4" : "Show details \u25BE";
            _detailsRow.Height = _expanded ? LogicalToDeviceUnits(180) : 0;
            Height += _expanded ? LogicalToDeviceUnits(190) : -LogicalToDeviceUnits(190);
        }

        private void Copy_Click(object sender, EventArgs e)
        {
            try
            {
                Clipboard.SetText(_details ?? string.Empty);
                _copy.Text = "Copied";
            }
            catch
            {
                _copy.Text = "Couldn't copy";
            }
        }

        public static void ShowException(IWin32Window owner, Exception exception)
        {
            string message;
            string details;

            var vaultError = exception as VaultException;
            if (vaultError != null)
            {
                message = string.IsNullOrWhiteSpace(vaultError.Message)
                    ? "PartLock couldn't finish that."
                    : vaultError.Message;
                details = vaultError.Details;
            }
            else
            {
                message = "Something went wrong.";
                details = exception == null ? null : exception.ToString();
            }

            using (var dialog = new ErrorDialog(message, details))
            {
                if (owner != null)
                {
                    dialog.ShowDialog(owner);
                }
                else
                {
                    dialog.ShowDialog();
                }
            }
        }
    }
}
