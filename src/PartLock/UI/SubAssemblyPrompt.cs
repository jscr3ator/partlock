using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using PartLock.Core;
using PartLock.Platform;

namespace PartLock.UI
{
    /// <summary>
    /// When the user checks out an assembly, offers to check out its sub-assemblies too
    /// (found by asking a running SolidWorks). Anything that can't be determined falls
    /// back to checking out exactly what the user picked.
    /// </summary>
    public static class SubAssemblyPrompt
    {
        /// <summary>
        /// Returns the paths to check out: <paramref name="paths"/> plus the sub-assemblies the
        /// user ticked, or null when the user cancelled. <paramref name="files"/> is the current
        /// file list (used for lock state). <paramref name="log"/> may be null.
        /// </summary>
        public static async Task<List<string>> ExpandAsync(IWin32Window owner, IVaultService vault,
            IReadOnlyList<VaultFile> files, List<string> paths, Action<string> log)
        {
            List<string> assemblies = paths.Where(SolidWorksRefs.IsAssembly).ToList();
            if (assemblies.Count == 0 || !(vault is VaultService) || files == null) return paths;

            var byPath = new Dictionary<string, VaultFile>(StringComparer.OrdinalIgnoreCase);
            foreach (VaultFile f in files)
                if (f != null && !string.IsNullOrEmpty(f.Path)) byPath[f.Path] = f;

            var chosen = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
            var offer = new List<string>();
            var takenByOthers = new List<string>();
            bool askedSolidWorks = false;

            foreach (string assembly in assemblies)
            {
                string full = Path.Combine(vault.RepoRoot, assembly.Replace('/', Path.DirectorySeparatorChar));
                List<string> deps = await SolidWorksRefs.GetDependenciesAsync(full, TimeSpan.FromSeconds(20));
                if (deps == null)
                {
                    if (log != null)
                        log("SolidWorks isn't open (or didn't answer), so only " + Path.GetFileName(assembly) +
                            " was checked out. Open SolidWorks first to also check out its sub-assemblies.");
                    continue;
                }
                askedSolidWorks = true;

                foreach (string sub in SolidWorksRefs.SubAssembliesInVault(deps, vault.RepoRoot, assembly))
                {
                    if (chosen.Contains(sub) || offer.Contains(sub, StringComparer.OrdinalIgnoreCase)) continue;
                    VaultFile file;
                    if (!byPath.TryGetValue(sub, out file) || !file.IsLockable) continue;
                    if (file.Lock != null && file.Lock.IsMine) continue; // already yours
                    if (file.Lock != null && !file.Lock.IsMine)
                    {
                        takenByOthers.Add(file.FileName + " (checked out by " + (file.Lock.Owner ?? "someone else") + ")");
                        continue;
                    }
                    if (file.State == FileState.Available || file.State == FileState.OutOfDate ||
                        file.State == FileState.ModifiedNotCheckedOut)
                    {
                        offer.Add(sub);
                    }
                }
            }

            if (!askedSolidWorks || (offer.Count == 0 && takenByOthers.Count == 0)) return paths;

            using (var dialog = new SubAssemblyDialog(assemblies, offer, takenByOthers))
            {
                DialogResult answer = dialog.ShowDialog(owner);
                if (answer == DialogResult.Cancel) return null;
                var result = new List<string>(paths);
                if (answer == DialogResult.Yes) result.AddRange(dialog.Ticked);
                return result;
            }
        }
    }

    internal sealed class SubAssemblyDialog : Form
    {
        private readonly CheckedListBox _list;
        private readonly List<string> _paths;

        public SubAssemblyDialog(List<string> assemblies, List<string> offer, List<string> takenByOthers)
        {
            _paths = offer;
            Text = "PartLock – Sub-assemblies";
            Font = Theme.Base;
            BackColor = Theme.Panel;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96f, 96f);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            if (Theme.AppIcon != null) Icon = Theme.AppIcon;
            ClientSize = new Size(600, 440);
            MinimumSize = new Size(480, 320);

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Padding = new Padding(20, 16, 20, 14),
                BackColor = Theme.Panel
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            string names = string.Join(", ", assemblies.Select(Path.GetFileName).ToArray());
            root.Controls.Add(new Label
            {
                Text = offer.Count > 0
                    ? names + " uses these sub-assemblies. Check them out too?"
                    : names + " uses sub-assemblies that you can't check out right now.",
                Font = Theme.Heading,
                ForeColor = Theme.Text,
                AutoSize = true,
                MaximumSize = new Size(550, 0),
                Margin = new Padding(0, 0, 0, 10)
            }, 0, 0);

            _list = new CheckedListBox
            {
                Dock = DockStyle.Fill,
                CheckOnClick = true,
                BorderStyle = BorderStyle.FixedSingle,
                IntegralHeight = false,
                Font = Theme.Base,
                Margin = new Padding(0)
            };
            foreach (string path in offer) _list.Items.Add(path, true);
            root.Controls.Add(_list, 0, 1);

            root.Controls.Add(new Label
            {
                Text = takenByOthers.Count == 0
                    ? "Parts aren't included – check those out separately if you'll edit them."
                    : "Skipped (someone else has them): " + string.Join(", ", takenByOthers.ToArray()) +
                      ". Parts aren't included either.",
                Font = Theme.Small,
                ForeColor = takenByOthers.Count == 0 ? Theme.Muted : Theme.Warning,
                AutoSize = true,
                MaximumSize = new Size(550, 0),
                Margin = new Padding(0, 8, 0, 10)
            }, 0, 2);

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                AutoSize = true,
                Margin = new Padding(0)
            };
            var yes = Theme.CreateButton(offer.Count > 0 ? "Check out ticked too" : "Continue", ButtonKind.Primary);
            yes.DialogResult = offer.Count > 0 ? DialogResult.Yes : DialogResult.No;
            yes.Margin = new Padding(0);
            buttons.Controls.Add(yes);
            if (offer.Count > 0)
            {
                var only = Theme.CreateButton("Just the assembly", ButtonKind.Outline);
                only.DialogResult = DialogResult.No;
                only.Margin = new Padding(0, 0, 8, 0);
                buttons.Controls.Add(only);
            }
            var cancel = Theme.CreateButton("Cancel", ButtonKind.Outline);
            cancel.DialogResult = DialogResult.Cancel;
            cancel.Margin = new Padding(0, 0, 8, 0);
            buttons.Controls.Add(cancel);
            root.Controls.Add(buttons, 0, 3);

            Controls.Add(root);
            AcceptButton = yes;
            CancelButton = cancel;
        }

        public List<string> Ticked
        {
            get
            {
                var result = new List<string>();
                foreach (object item in _list.CheckedItems) result.Add(item as string);
                return result.Where(p => !string.IsNullOrEmpty(p)).ToList();
            }
        }
    }
}
