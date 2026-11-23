using System;
using System.Drawing;
using System.Windows.Forms;

namespace PartLock.UI
{
    /// <summary>Asks for the name/email git should put on commits. Shown once, then remembered by git.</summary>
    public class IdentityDialog : Form
    {
        private readonly TextBox _nameBox;
        private readonly TextBox _emailBox;
        private readonly Label _error;

        public IdentityDialog(string name = null, string email = null)
        {
            PersonName = name ?? string.Empty;
            PersonEmail = email ?? string.Empty;

            Text = "PartLock \u2013 Your details";
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
            ClientSize = new Size(520, 310);

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
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var heading = new Label
            {
                Text = "Who should sign your check-ins?",
                Font = Theme.Heading,
                ForeColor = Theme.Text,
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 8)
            };
            root.Controls.Add(heading, 0, 0);

            var blurb = new Label
            {
                Text = "PartLock needs a name and email for the commits it makes. "
                     + "Use your real name and school email so teammates know who changed what.",
                Font = Theme.Base,
                ForeColor = Theme.Muted,
                AutoSize = true,
                MaximumSize = new Size(452, 0),
                Margin = new Padding(0, 0, 0, 16)
            };
            root.Controls.Add(blurb, 0, 1);

            var fields = new TableLayoutPanel
            {
                ColumnCount = 2,
                RowCount = 2,
                Dock = DockStyle.Fill,
                AutoSize = true,
                Margin = new Padding(0)
            };
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92f));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            fields.Controls.Add(MakeFieldLabel("Full name"), 0, 0);
            _nameBox = MakeTextBox(PersonName);
            _nameBox.Margin = new Padding(0, 0, 0, 10);
            fields.Controls.Add(_nameBox, 1, 0);

            fields.Controls.Add(MakeFieldLabel("Email"), 0, 1);
            _emailBox = MakeTextBox(PersonEmail);
            fields.Controls.Add(_emailBox, 1, 1);

            root.Controls.Add(fields, 0, 2);

            var hint = new Label
            {
                Text = "This is saved in git settings on this computer. You can change it later.",
                Font = Theme.Small,
                ForeColor = Theme.Muted,
                AutoSize = true,
                MaximumSize = new Size(452, 0),
                Margin = new Padding(0, 12, 0, 0)
            };
            root.Controls.Add(hint, 0, 3);

            _error = new Label
            {
                Text = string.Empty,
                Font = Theme.Small,
                ForeColor = Theme.Danger,
                AutoSize = true,
                Visible = false,
                Margin = new Padding(0, 8, 0, 0)
            };
            root.Controls.Add(_error, 0, 4);

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                AutoSize = true,
                Margin = new Padding(0)
            };
            var save = Theme.CreateButton("Save", ButtonKind.Primary);
            save.Margin = new Padding(0, 0, 0, 0);
            save.Click += Save_Click;
            buttons.Controls.Add(save);

            var cancel = Theme.CreateButton("Cancel", ButtonKind.Outline);
            cancel.DialogResult = DialogResult.Cancel;
            cancel.Margin = new Padding(0, 0, 8, 0);
            buttons.Controls.Add(cancel);
            root.Controls.Add(buttons, 0, 5);

            Controls.Add(root);
            AcceptButton = save;
            CancelButton = cancel;
        }

        public string PersonName { get; private set; }

        public string PersonEmail { get; private set; }

        private static Label MakeFieldLabel(string text)
        {
            return new Label
            {
                Text = text,
                Font = Theme.Base,
                ForeColor = Theme.Text,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 6, 8, 0)
            };
        }

        private static TextBox MakeTextBox(string text)
        {
            return new TextBox
            {
                Text = text,
                Font = Theme.Base,
                BorderStyle = BorderStyle.FixedSingle,
                Dock = DockStyle.Fill,
                Margin = new Padding(0)
            };
        }

        private void Save_Click(object sender, EventArgs e)
        {
            string name = _nameBox.Text.Trim();
            string email = _emailBox.Text.Trim();

            if (name.Length < 2)
            {
                ShowError("Please enter your name.", _nameBox);
                return;
            }

            int at = email.IndexOf('@');
            if (at <= 0 || at == email.Length - 1 || email.IndexOf(' ') >= 0)
            {
                ShowError("Please enter a valid email address, like sam@example.edu.", _emailBox);
                return;
            }

            PersonName = name;
            PersonEmail = email;
            this.DialogResult = DialogResult.OK;
            Close();
        }

        private void ShowError(string text, TextBox focus)
        {
            _error.Text = text;
            _error.Visible = true;
            focus.Focus();
            focus.SelectAll();
        }
    }
}
