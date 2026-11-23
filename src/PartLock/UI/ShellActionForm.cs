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
    /// Small window for the File Explorer right-click menu: Check Out / Check In / Undo on one
    /// file, with the same rules and dialogs as the main window.
    /// </summary>
    public class ShellActionForm : Form
    {
        public enum ShellVerb { CheckOut, CheckIn, Undo }

        private readonly ShellVerb _action;
        private readonly string _fullPath;
        private readonly VaultService _vault;
        private readonly Label _status;
        private readonly TextBox _log;

        public ShellActionForm(ShellVerb action, string repoRoot, string fullPath)
        {
            _action = action;
            _fullPath = fullPath;
            _vault = new VaultService(repoRoot, null);

            string verb = action == ShellVerb.CheckOut ? "Check Out" : action == ShellVerb.CheckIn ? "Check In" : "Undo Check Out";
            Text = "PartLock – " + verb;
            Font = Theme.Base;
            BackColor = Theme.Panel;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96f, 96f);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            if (Theme.AppIcon != null) Icon = Theme.AppIcon;
            ClientSize = new Size(560, 260);

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(20, 16, 20, 16),
                BackColor = Theme.Panel
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            root.Controls.Add(new Label
            {
                Text = verb + ": " + Path.GetFileName(fullPath),
                Font = Theme.Heading,
                ForeColor = Theme.Text,
                AutoSize = true,
                MaximumSize = new Size(510, 0),
                Margin = new Padding(0, 0, 0, 6)
            }, 0, 0);

            _status = new Label
            {
                Text = "Working…",
                Font = Theme.Base,
                ForeColor = Theme.Muted,
                AutoSize = true,
                MaximumSize = new Size(510, 0),
                Margin = new Padding(0, 0, 0, 8)
            };
            root.Controls.Add(_status, 0, 1);

            _log = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.FixedSingle,
                Font = Theme.Mono,
                BackColor = Theme.LogBackground
            };
            root.Controls.Add(_log, 0, 2);
            Controls.Add(root);

            _vault.Log = new Progress<string>(line => { if (!IsDisposed) _log.AppendText(line + Environment.NewLine); });
            Shown += async delegate { await RunAsync(); };
        }

        private async Task RunAsync()
        {
            UseWaitCursor = true;
            try
            {
                string rel = Relative(_fullPath);
                _status.Text = "Checking the server…";
                IReadOnlyList<VaultFile> files = await _vault.GetFilesAsync(true);
                VaultFile file = files.FirstOrDefault(f => string.Equals(f.Path, rel, StringComparison.OrdinalIgnoreCase));

                string done = null;
                if (_action == ShellVerb.CheckOut) done = await CheckOutAsync(file, files);
                else if (_action == ShellVerb.CheckIn) done = await CheckInAsync(file);
                else done = await UndoAsync(file);

                if (done != null)
                    MessageBox.Show(this, done, "PartLock", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                UseWaitCursor = false;
                ErrorDialog.ShowException(this, ex);
            }
            finally
            {
                Close();
            }
        }

        private async Task<string> CheckOutAsync(VaultFile file, IReadOnlyList<VaultFile> files)
        {
            if (file == null || !file.IsLockable)
                return "PartLock can't check out this file. Only CAD files in the vault can be checked out.";
            if (file.Lock != null && !file.Lock.IsMine)
                return file.FileName + " is checked out by " + (file.Lock.Owner ?? "someone else") + ". Try again when they've checked it in.";
            if (file.Lock != null && file.Lock.IsMine)
                return file.FileName + " is already checked out by you.";

            var paths = new List<string> { file.Path };
            if (PartLock.Platform.SolidWorksRefs.IsAssembly(file.Path))
            {
                _status.Text = "Finding sub-assemblies and parts (may start SolidWorks in the background)…";
                List<string> expanded;
                try { expanded = await SubAssemblyPrompt.ExpandAsync(this, _vault, files, paths, Log); }
                catch (Exception ex) { ErrorDialog.LogToFile("Sub-assembly lookup failed.", ex.ToString()); expanded = paths; }
                if (expanded == null) return null; // cancelled
                paths = expanded;
            }

            _status.Text = "Checking out…";
            await _vault.CheckOutAsync(paths);
            return (paths.Count == 1 ? file.FileName + " is" : paths.Count + " files are") +
                   " checked out to you and can be edited now.\n\nIf it's already open in SolidWorks, close it and open it again.";
        }

        private async Task<string> CheckInAsync(VaultFile file)
        {
            bool canCheckIn = file != null &&
                (file.State == FileState.CheckedOutByMe || file.State == FileState.NewFile ||
                 file.State == FileState.Changed || file.State == FileState.Deleted ||
                 (file.State == FileState.ModifiedNotCheckedOut && (file.Lock == null || file.Lock.IsMine)));
            if (!canCheckIn)
            {
                if (file != null && file.Lock != null && !file.Lock.IsMine)
                    return file.FileName + " is checked out by " + (file.Lock.Owner ?? "someone else") + ", so you can't check it in.";
                return "There's nothing to check in for this file. Check it out first, edit and save it, then check it in.";
            }

            Tuple<string, string> identity = await _vault.GetCommitIdentityAsync();
            if (identity == null || string.IsNullOrWhiteSpace(identity.Item1) || string.IsNullOrWhiteSpace(identity.Item2))
            {
                using (var dialog = new IdentityDialog(identity == null ? null : identity.Item1, identity == null ? null : identity.Item2))
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return null;
                    await _vault.SetCommitIdentityAsync(dialog.PersonName, dialog.PersonEmail);
                }
            }

            string message;
            bool keep;
            using (var dialog = new CheckInDialog(new[] { file }, new[] { file.Path }, _vault.ServerUserName))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK || dialog.SelectedPaths.Count == 0) return null;
                message = dialog.Message;
                keep = dialog.KeepCheckedOut;
            }

            if (file.State == FileState.ModifiedNotCheckedOut)
            {
                _status.Text = "Checking out first (you edited it without checking out)…";
                await _vault.CheckOutAsync(new[] { file.Path });
            }
            _status.Text = "Checking in…";
            List<string> toCommit = await SubAssemblyPrompt.AddSavedListsAsync(_vault, new List<string> { file.Path });
            await _vault.CheckInAsync(toCommit, message, keep);
            return file.FileName + " is checked in and uploaded to GitHub.";
        }

        private async Task<string> UndoAsync(VaultFile file)
        {
            bool canUndo = file != null &&
                (file.State == FileState.CheckedOutByMe || file.State == FileState.ModifiedNotCheckedOut ||
                 file.State == FileState.Changed || file.State == FileState.Deleted);
            if (!canUndo) return "This file isn't checked out by you and has no changes, so there's nothing to undo.";

            DialogResult answer = MessageBox.Show(this,
                "Throw away your changes to " + file.FileName + " and release it? This can't be undone.",
                "PartLock", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return null;

            _status.Text = "Undoing…";
            await _vault.UndoCheckOutAsync(new[] { file.Path });
            return "Done. " + file.FileName + " is back to the latest checked-in version.";
        }

        private void Log(string line)
        {
            if (!IsDisposed) _log.AppendText(line + Environment.NewLine);
        }

        private string Relative(string fullPath)
        {
            string root = _vault.RepoRoot.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(fullPath);
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? full.Substring(root.Length).Replace('\\', '/')
                : full.Replace('\\', '/');
        }
    }
}
