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
                List<string> subs = await FindSubAssembliesAsync(vault.RepoRoot, assembly, log);
                if (subs == null)
                {
                    if (log != null)
                        log("Couldn't find the sub-assemblies and parts of " + Path.GetFileName(assembly) +
                            " (SolidWorks isn't installed or didn't answer), so only the assembly was checked out.");
                    continue;
                }
                askedSolidWorks = true;

                foreach (string sub in subs)
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

        /// <summary>
        /// Sub-assemblies (repo-relative) of one assembly. Tries, in order: an open SolidWorks
        /// (instant), the list saved in the vault for this exact version of the assembly, then a
        /// hidden SolidWorks started just for this (slow the first time). Null when none worked.
        /// </summary>
        public static async Task<List<string>> FindSubAssembliesAsync(string repoRoot, string assembly, Action<string> log)
        {
            string full = Path.Combine(repoRoot, assembly.Replace('/', Path.DirectorySeparatorChar));

            List<string> deps = await SolidWorksRefs.GetDependenciesAsync(full, TimeSpan.FromSeconds(20));
            if (deps != null) return SolidWorksRefs.ComponentsInVault(deps, repoRoot, assembly);

            List<string> saved = SolidWorksRefs.ReadCache(repoRoot, assembly);
            if (saved != null)
            {
                return saved.Where(p => File.Exists(Path.Combine(repoRoot, p.Replace('/', Path.DirectorySeparatorChar))))
                            .ToList();
            }

            if (!SolidWorksRefs.IsInstalled) return null;
            if (log != null)
                log("Starting SolidWorks in the background to find the sub-assemblies and parts of " + Path.GetFileName(assembly) +
                    " (can take a minute; it closes again afterwards)\u2026");
            deps = await SolidWorksRefs.GetDependenciesAsync(full, TimeSpan.FromMinutes(3), true);
            return deps == null ? null : SolidWorksRefs.ComponentsInVault(deps, repoRoot, assembly);
        }

        /// <summary>
        /// Before checking in assemblies: if SolidWorks is open, save each assembly's sub-assembly
        /// list in the vault (.partlock/refs) so teammates can check out sub-assemblies without
        /// SolidWorks. Returns the paths to check in (the originals plus any saved lists). Never throws.
        /// </summary>
        public static async Task<List<string>> AddSavedListsAsync(IVaultService vault, List<string> paths)
        {
            var result = new List<string>(paths);
            try
            {
                if (!(vault is VaultService) || !paths.Any(SolidWorksRefs.IsAssembly) || !SolidWorksRefs.IsRunning)
                    return result;

                foreach (string assembly in paths.Where(SolidWorksRefs.IsAssembly).ToList())
                {
                    string full = Path.Combine(vault.RepoRoot, assembly.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(full)) continue;
                    List<string> deps = await SolidWorksRefs.GetDependenciesAsync(full, TimeSpan.FromSeconds(20));
                    if (deps == null) continue;

                    string cacheRel = SolidWorksRefs.CacheRelativePath(assembly);
                    if (await IsLockableAsync(vault.RepoRoot, cacheRel)) continue; // would need a check-out of its own

                    string written = SolidWorksRefs.WriteCache(vault.RepoRoot, assembly,
                        SolidWorksRefs.ComponentsInVault(deps, vault.RepoRoot, assembly));
                    if (written != null && !result.Contains(written, StringComparer.OrdinalIgnoreCase)) result.Add(written);
                }
            }
            catch (Exception ex)
            {
                ErrorDialog.LogToFile("Saving sub-assembly lists failed.", ex.ToString());
            }
            return result;
        }

        private static async Task<bool> IsLockableAsync(string repoRoot, string relative)
        {
            try
            {
                GitResult r = await new GitRunner(null).RunAsync(repoRoot,
                    new[] { "check-attr", "lockable", "--", relative }, System.Threading.CancellationToken.None, null, false, 30000);
                string text = (r.StdOut ?? string.Empty).Trim();
                return !r.Success || !(text.EndsWith(": unspecified") || text.EndsWith(": unset"));
            }
            catch (Exception)
            {
                return true; // unsure: don't risk it
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
            Text = "PartLock – Sub-assemblies and parts";
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
                    ? names + " uses these sub-assemblies and parts (" + offer.Count + "). They're all ticked – untick any you won't edit."
                    : names + " uses sub-assemblies and parts that you can't check out right now.",
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
            foreach (string path in offer) _list.Items.Add(new Entry(path), true);
            root.Controls.Add(_list, 0, 1);

            root.Controls.Add(new Label
            {
                Text = takenByOthers.Count == 0
                    ? "Toolbox / library files outside the vault aren't included. Checking out many files can take a minute."
                    : "Skipped (someone else has them): " + string.Join(", ", takenByOthers.ToArray()) + ".",
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
            var yes = Theme.CreateButton(offer.Count > 0 ? "Check out all ticked" : "Continue", ButtonKind.Primary);
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

        private sealed class Entry
        {
            public Entry(string path) { Path = path; }
            public string Path { get; private set; }
            public override string ToString()
            {
                return (SolidWorksRefs.IsAssembly(Path) ? "Sub-assembly:  " : "Part:  ") + Path;
            }
        }

        public List<string> Ticked
        {
            get
            {
                var result = new List<string>();
                foreach (object item in _list.CheckedItems)
                {
                    var entry = item as Entry;
                    if (entry != null) result.Add(entry.Path);
                }
                return result.Where(p => !string.IsNullOrEmpty(p)).ToList();
            }
        }
    }
}
