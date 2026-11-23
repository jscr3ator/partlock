using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using PartLock.Core;

namespace PartLock.UI
{
    /// <summary>
    /// A required first-run step ("Install Git", "Sign in with GitHub"): heading, explanation,
    /// one primary action that runs asynchronously with a live activity log, and a Quit button.
    /// DialogResult is OK only when the action succeeded.
    /// </summary>
    public class SetupStepDialog : Form
    {
        private readonly Func<IProgress<string>, CancellationToken, Task<bool>> _action;
        private readonly Button _primary;
        private readonly Button _quit;
        private readonly Label _status;
        private readonly TextBox _log;
        private readonly string _primaryText;
        private CancellationTokenSource _cts;
        private bool _busy;

        public SetupStepDialog(string title, string heading, string message, string primaryText,
            Func<IProgress<string>, CancellationToken, Task<bool>> action, string footnote = null)
        {
            _action = action;
            _primaryText = primaryText;

            Text = "PartLock – " + title;
            Font = Theme.Base;
            BackColor = Theme.Panel;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96f, 96f);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = true;
            MaximizeBox = false;
            if (Theme.AppIcon != null) Icon = Theme.AppIcon;
            ClientSize = new Size(580, 420);

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 6,
                BackColor = Theme.Panel,
                Padding = new Padding(24, 20, 24, 16)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            root.Controls.Add(new Label
            {
                Text = heading,
                Font = Theme.Heading,
                ForeColor = Theme.Text,
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 8)
            }, 0, 0);

            root.Controls.Add(new Label
            {
                Text = message,
                Font = Theme.Base,
                ForeColor = Theme.Muted,
                AutoSize = true,
                MaximumSize = new Size(530, 0),
                Margin = new Padding(0, 0, 0, 8)
            }, 0, 1);

            root.Controls.Add(new Label
            {
                Text = footnote ?? string.Empty,
                Font = Theme.Small,
                ForeColor = Theme.Muted,
                AutoSize = true,
                Visible = !string.IsNullOrEmpty(footnote),
                MaximumSize = new Size(530, 0),
                Margin = new Padding(0, 0, 0, 8)
            }, 0, 2);

            _status = new Label
            {
                Text = string.Empty,
                Font = Theme.Small,
                ForeColor = Theme.Danger,
                AutoSize = true,
                Visible = false,
                MaximumSize = new Size(530, 0),
                Margin = new Padding(0, 0, 0, 8)
            };
            root.Controls.Add(_status, 0, 3);

            _log = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                WordWrap = true,
                ScrollBars = ScrollBars.Vertical,
                BorderStyle = BorderStyle.FixedSingle,
                Dock = DockStyle.Fill,
                Font = Theme.Mono,
                BackColor = Theme.LogBackground,
                ForeColor = Theme.Text,
                Visible = false,
                Margin = new Padding(0, 0, 0, 10)
            };
            root.Controls.Add(_log, 0, 4);

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                AutoSize = true,
                Margin = new Padding(0)
            };
            _primary = Theme.CreateButton(primaryText, ButtonKind.Primary);
            _primary.Margin = new Padding(0);
            _primary.Click += Primary_Click;
            buttons.Controls.Add(_primary);

            _quit = Theme.CreateButton("Quit", ButtonKind.Outline);
            _quit.DialogResult = DialogResult.Cancel;
            _quit.Margin = new Padding(0, 0, 8, 0);
            buttons.Controls.Add(_quit);
            root.Controls.Add(buttons, 0, 5);

            Controls.Add(root);
            AcceptButton = _primary;
            CancelButton = _quit;
        }

        private async void Primary_Click(object sender, EventArgs e)
        {
            if (_busy) return;
            SetBusy(true);
            _status.Visible = false;
            _log.Visible = true;
            _cts = new CancellationTokenSource();
            try
            {
                bool ok = await _action(new Progress<string>(AppendLog), _cts.Token);
                if (ok)
                {
                    SetBusy(false);
                    DialogResult = DialogResult.OK;
                    Close();
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                AppendLog("Cancelled.");
            }
            catch (VaultException ex)
            {
                ShowProblem(ex.Message);
                ErrorDialog.LogToFile(ex.Message, ex.Details);
            }
            catch (Exception ex)
            {
                ShowProblem("Something went wrong: " + ex.Message);
                ErrorDialog.LogToFile(ex.Message, ex.ToString());
            }
            finally
            {
                if (_busy) SetBusy(false);
                if (_cts != null) { _cts.Dispose(); _cts = null; }
            }
        }

        /// <summary>Shows a red problem line under the message (also usable by the action).</summary>
        public void ShowProblem(string text)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(ShowProblem), text); return; }
            _status.Text = text;
            _status.Visible = true;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_busy)
            {
                e.Cancel = true;
                if (_cts != null) _cts.Cancel();
            }
            base.OnFormClosing(e);
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            _primary.Enabled = !busy;
            _primary.Text = busy ? "Working…" : _primaryText;
            UseWaitCursor = busy;
        }

        private void AppendLog(string line)
        {
            if (IsDisposed || line == null) return;
            _log.AppendText(line + Environment.NewLine);
        }
    }
}
