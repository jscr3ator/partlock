using System;
using System.Drawing;
using System.Windows.Forms;
using PartLock.Platform;

namespace PartLock.UI
{
    /// <summary>Team lead sets the chat webhook (Discord / Slack / Teams / other) and which events post to it.</summary>
    public class TeamNotifyDialog : Form
    {
        private readonly string _repoRoot;
        private readonly string _who;
        private readonly TextBox _url;
        private readonly CheckBox _checkOut;
        private readonly CheckBox _checkIn;
        private readonly CheckBox _undo;
        private readonly CheckBox _force;
        private readonly Label _status;
        private readonly Button _test;

        public TeamNotifyDialog(string repoRoot, string who)
        {
            _repoRoot = repoRoot;
            _who = who;
            TeamNotifyConfig config = TeamNotify.Load(repoRoot);

            Text = "PartLock – Team notifications";
            Font = Theme.Base;
            BackColor = Theme.Panel;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96f, 96f);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            if (Theme.AppIcon != null) Icon = Theme.AppIcon;
            ClientSize = new Size(600, 430);

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 8,
                Padding = new Padding(22, 18, 22, 14),
                BackColor = Theme.Panel
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            for (int i = 0; i < 7; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            root.Controls.Add(new Label
            {
                Text = "Post check-outs and check-ins to your team chat",
                Font = Theme.Heading,
                ForeColor = Theme.Text,
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 6)
            }, 0, 0);

            root.Controls.Add(new Label
            {
                Text = "Paste a webhook link from Discord (Channel settings → Integrations → Webhooks), Slack "
                     + "(Incoming Webhooks) or Microsoft Teams. It's saved in the vault, so everyone's PartLock "
                     + "posts to it after their next Get Latest.",
                Font = Theme.Base,
                ForeColor = Theme.Muted,
                AutoSize = true,
                MaximumSize = new Size(550, 0),
                Margin = new Padding(0, 0, 0, 10)
            }, 0, 1);

            _url = new TextBox
            {
                Dock = DockStyle.Fill,
                Font = Theme.Base,
                BorderStyle = BorderStyle.FixedSingle,
                Text = config.Webhook ?? string.Empty,
                Margin = new Padding(0, 0, 0, 10)
            };
            Theme.SetCueText(_url, "https://discord.com/api/webhooks/…");
            root.Controls.Add(_url, 0, 2);

            var events = new FlowLayoutPanel
            {
                AutoSize = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Margin = new Padding(0, 0, 0, 6)
            };
            _checkIn = MakeCheck("Someone checks in (with their message)", config.Events.Contains(TeamEvent.CheckIn));
            _checkOut = MakeCheck("Someone checks out", config.Events.Contains(TeamEvent.CheckOut));
            _undo = MakeCheck("Someone undoes a check-out", config.Events.Contains(TeamEvent.Undo));
            _force = MakeCheck("A team lead force-unlocks someone's file", config.Events.Contains(TeamEvent.ForceUnlock));
            events.Controls.AddRange(new Control[] { _checkIn, _checkOut, _undo, _force });
            root.Controls.Add(events, 0, 3);

            root.Controls.Add(new Label
            {
                Text = "Anyone who can open the vault can see this link. Use a channel just for CAD updates.",
                Font = Theme.Small,
                ForeColor = Theme.Muted,
                AutoSize = true,
                MaximumSize = new Size(550, 0),
                Margin = new Padding(0, 4, 0, 6)
            }, 0, 4);

            _status = new Label
            {
                Font = Theme.Small,
                AutoSize = true,
                MaximumSize = new Size(550, 0),
                Margin = new Padding(0, 0, 0, 6)
            };
            root.Controls.Add(_status, 0, 5);

            var buttons = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _test = Theme.CreateButton("Send test message", ButtonKind.Outline);
            _test.Margin = new Padding(0);
            _test.Anchor = AnchorStyles.Left;
            _test.Click += Test_Click;
            buttons.Controls.Add(_test, 0, 0);

            var right = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = new Padding(0) };
            var save = Theme.CreateButton("Save for the team", ButtonKind.Primary);
            save.Margin = new Padding(0);
            save.Click += Save_Click;
            right.Controls.Add(save);
            var cancel = Theme.CreateButton("Cancel", ButtonKind.Outline);
            cancel.DialogResult = DialogResult.Cancel;
            cancel.Margin = new Padding(0, 0, 8, 0);
            right.Controls.Add(cancel);
            buttons.Controls.Add(right, 1, 0);
            root.Controls.Add(buttons, 0, 6);

            Controls.Add(root);
            AcceptButton = save;
            CancelButton = cancel;
        }

        /// <summary>The settings to save (Webhook empty = turn notifications off).</summary>
        public TeamNotifyConfig Result { get; private set; }

        private static CheckBox MakeCheck(string text, bool on)
        {
            return new CheckBox { Text = text, Checked = on, AutoSize = true, Font = Theme.Base, Margin = new Padding(0, 2, 0, 2) };
        }

        private bool ValidUrl(string url)
        {
            Uri uri;
            return url.Length == 0 ||
                   (Uri.TryCreate(url, UriKind.Absolute, out uri) && uri.Scheme == Uri.UriSchemeHttps);
        }

        private async void Test_Click(object sender, EventArgs e)
        {
            string url = _url.Text.Trim();
            if (url.Length == 0 || !ValidUrl(url))
            {
                ShowStatus("Paste a webhook link that starts with https:// first.", true);
                return;
            }
            _test.Enabled = false;
            ShowStatus("Sending…", false);
            try
            {
                await TeamNotify.SendTestAsync(url, _repoRoot, _who);
                ShowStatus("Sent. Check your chat channel for the test message.", false);
            }
            catch (Exception ex)
            {
                ShowStatus(ex.Message, true);
            }
            finally
            {
                _test.Enabled = true;
            }
        }

        private void Save_Click(object sender, EventArgs e)
        {
            string url = _url.Text.Trim();
            if (!ValidUrl(url))
            {
                ShowStatus("The webhook link must start with https://", true);
                return;
            }
            var config = new TeamNotifyConfig { Webhook = url };
            config.Events.Clear();
            if (_checkOut.Checked) config.Events.Add(TeamEvent.CheckOut);
            if (_checkIn.Checked) config.Events.Add(TeamEvent.CheckIn);
            if (_undo.Checked) config.Events.Add(TeamEvent.Undo);
            if (_force.Checked) config.Events.Add(TeamEvent.ForceUnlock);
            Result = config;
            DialogResult = DialogResult.OK;
            Close();
        }

        private void ShowStatus(string text, bool error)
        {
            _status.Text = text;
            _status.ForeColor = error ? Theme.Danger : Theme.Success;
        }
    }
}
