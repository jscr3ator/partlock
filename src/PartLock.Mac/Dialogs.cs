using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using PartLock.Core;
using PartLock.Platform;

namespace PartLock.Mac
{
    /// <summary>A required first-run step ("Install Git", "Sign in to GitHub") with one action, a live log and Quit.</summary>
    internal sealed class StepWindow : Window
    {
        private readonly Func<IProgress<string>, CancellationToken, Task<bool>> _action;
        private readonly Button _primary;
        private readonly TextBlock _problem;
        private readonly TextBox _log;
        private readonly string _primaryText;
        private bool _busy;
        public bool Succeeded { get; private set; }

        public StepWindow(string title, string heading, string message, string primaryText,
            Func<IProgress<string>, CancellationToken, Task<bool>> action, string footnote = null)
        {
            _action = action;
            _primaryText = primaryText;
            Title = "PartLock – " + title;
            Width = 600; Height = 430; MinWidth = 480; MinHeight = 320;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Ui.Panel;

            _problem = Ui.Label(string.Empty, 12, Ui.Danger);
            _problem.IsVisible = false;
            _log = new TextBox
            {
                IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, IsVisible = false,
                FontFamily = new FontFamily("Menlo, Consolas, monospace"), FontSize = 11, Background = Ui.LogBg
            };
            _primary = Ui.Button(primaryText, true, async () => await RunAsync());
            Button quit = Ui.Button("Quit", false, () => { if (!_busy) Close(); });

            var buttons = Ui.Row(8, quit, _primary);
            buttons.HorizontalAlignment = HorizontalAlignment.Right;
            var top = Ui.Column(8, Ui.Heading(heading), Ui.Label(message, 13, Ui.Muted));
            if (!string.IsNullOrEmpty(footnote)) top.Children.Add(Ui.Label(footnote, 12, Ui.Muted));
            top.Children.Add(_problem);

            var dock = new DockPanel { Margin = new Thickness(24, 20, 24, 16), LastChildFill = true };
            DockPanel.SetDock(top, Dock.Top);
            DockPanel.SetDock(buttons, Dock.Bottom);
            buttons.Margin = new Thickness(0, 10, 0, 0);
            _log.Margin = new Thickness(0, 10, 0, 0);
            dock.Children.Add(top);
            dock.Children.Add(buttons);
            dock.Children.Add(_log);
            Content = dock;
            Closing += (s, e) => { if (_busy) e.Cancel = true; };
        }

        public void ShowProblem(string text)
        {
            _problem.Text = text;
            _problem.IsVisible = true;
        }

        private async Task RunAsync()
        {
            if (_busy) return;
            _busy = true;
            _primary.IsEnabled = false;
            _primary.Content = "Working…";
            _problem.IsVisible = false;
            _log.IsVisible = true;
            try
            {
                bool ok = await _action(new Progress<string>(l => _log.Text += l + "\n"), CancellationToken.None);
                if (ok)
                {
                    Succeeded = true;
                    _busy = false;
                    Close();
                    return;
                }
            }
            catch (VaultException ex) { ShowProblem(ex.Message); ErrorLog.Write(ex.Message, ex.Details); }
            catch (Exception ex) { ShowProblem("Something went wrong: " + ex.Message); ErrorLog.Write(ex.Message, ex.ToString()); }
            finally
            {
                _busy = false;
                _primary.IsEnabled = true;
                _primary.Content = _primaryText;
            }
        }
    }

    internal static class Dialogs
    {
        // ------------------------------------------------------------------ name / email

        public static async Task<Tuple<string, string>> AskIdentity(Window owner, string name, string email)
        {
            Window w = Ui.Dialog("Your details", 520, 300);
            w.SizeToContent = SizeToContent.Height;
            var nameBox = new TextBox { Text = name ?? string.Empty, Watermark = "Full name" };
            var emailBox = new TextBox { Text = email ?? string.Empty, Watermark = "you@school.edu" };
            TextBlock error = Ui.Label(string.Empty, 12, Ui.Danger);
            error.IsVisible = false;
            Tuple<string, string> result = null;
            Button save = Ui.Button("Save", true, () =>
            {
                string n = (nameBox.Text ?? string.Empty).Trim(), e = (emailBox.Text ?? string.Empty).Trim();
                int at = e.IndexOf('@');
                if (n.Length < 2) { error.Text = "Please enter your name."; error.IsVisible = true; return; }
                if (at <= 0 || at == e.Length - 1 || e.Contains(" ")) { error.Text = "Please enter a valid email address."; error.IsVisible = true; return; }
                result = Tuple.Create(n, e);
                w.Close();
            });
            var buttons = Ui.Row(8, Ui.Button("Cancel", false, () => w.Close()), save);
            buttons.HorizontalAlignment = HorizontalAlignment.Right;
            w.Content = Ui.Column(10,
                Ui.Heading("Who should sign your check-ins?"),
                Ui.Label("PartLock needs a name and email for the check-ins it makes. Use your real name so teammates know who changed what.", 13, Ui.Muted),
                nameBox, emailBox, error, buttons);
            ((StackPanel)w.Content).Margin = new Thickness(22, 18, 22, 16);
            await w.ShowDialog(owner);
            return result;
        }

        // ------------------------------------------------------------------ check in

        public sealed class CheckInResult
        {
            public List<string> Paths;
            public string Message;
            public bool KeepCheckedOut;
        }

        public static async Task<CheckInResult> AskCheckIn(Window owner, IReadOnlyList<VaultFile> files, ICollection<string> preselected, string me)
        {
            Window w = Ui.Dialog("Check In", 640, 520);
            var boxes = new List<Tuple<CheckBox, VaultFile>>();
            var list = new StackPanel { Spacing = 2 };
            foreach (VaultFile f in files)
            {
                var cb = new CheckBox
                {
                    IsChecked = preselected.Contains(f.Path),
                    Content = Ui.Row(10,
                        new TextBlock { Text = f.Path, FontSize = 13, Foreground = Ui.Text, VerticalAlignment = VerticalAlignment.Center },
                        new TextBlock { Text = Ui.StatusText(f, me), FontSize = 12, Foreground = Ui.StatusColor(f.State), VerticalAlignment = VerticalAlignment.Center })
                };
                boxes.Add(Tuple.Create(cb, f));
                list.Children.Add(cb);
            }
            var message = new TextBox { Watermark = "What did you change? e.g. Wing spar cap 3 mm thicker", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 80 };
            var keep = new CheckBox { Content = "Keep them checked out (I'm still working on them)" };
            TextBlock error = Ui.Label(string.Empty, 12, Ui.Danger);
            error.IsVisible = false;

            CheckInResult result = null;
            Button ok = Ui.Button("Check In", true, () =>
            {
                List<string> picked = boxes.Where(b => b.Item1.IsChecked == true).Select(b => b.Item2.Path).ToList();
                string text = (message.Text ?? string.Empty).Trim();
                if (picked.Count == 0) { error.Text = "Tick at least one file."; error.IsVisible = true; return; }
                if (text.Length == 0) { error.Text = "Write a short message about what you changed."; error.IsVisible = true; return; }
                result = new CheckInResult { Paths = picked, Message = text, KeepCheckedOut = keep.IsChecked == true };
                w.Close();
            });
            var buttons = Ui.Row(8, Ui.Button("Cancel", false, () => w.Close()), ok);
            buttons.HorizontalAlignment = HorizontalAlignment.Right;

            var top = Ui.Column(6, Ui.Heading("Check in your changes"),
                Ui.Label("Ticked files are uploaded to GitHub so the team gets them on their next Get Latest.", 13, Ui.Muted));
            var bottom = Ui.Column(8, Ui.Label("Message", 12, Ui.Text, FontWeight.SemiBold), message, keep, error, buttons);
            var dock = new DockPanel { Margin = new Thickness(22, 18, 22, 16) };
            DockPanel.SetDock(top, Dock.Top);
            DockPanel.SetDock(bottom, Dock.Bottom);
            top.Margin = new Thickness(0, 0, 0, 10);
            bottom.Margin = new Thickness(0, 10, 0, 0);
            dock.Children.Add(top);
            dock.Children.Add(bottom);
            dock.Children.Add(new Border { BorderBrush = Ui.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(8), Child = new ScrollViewer { Content = list } });
            w.Content = dock;
            await w.ShowDialog(owner);
            return result;
        }

        // ------------------------------------------------------------------ sub-assemblies and parts

        /// <summary>Returns paths to add (empty = just the assembly), or null when cancelled.</summary>
        public static async Task<List<string>> AskParts(Window owner, List<string> assemblies, List<string> offer, List<string> takenByOthers)
        {
            Window w = Ui.Dialog("Sub-assemblies and parts", 640, 500);
            var boxes = new List<Tuple<CheckBox, string>>();
            var list = new StackPanel { Spacing = 2 };
            foreach (string p in offer)
            {
                var cb = new CheckBox
                {
                    IsChecked = true,
                    Content = Ui.Row(8,
                        new TextBlock { Text = SolidWorksRefs.IsAssembly(p) ? "Sub-assembly" : "Part", Width = 92, Foreground = Ui.Muted, FontSize = 12, VerticalAlignment = VerticalAlignment.Center },
                        new TextBlock { Text = p, FontSize = 13, VerticalAlignment = VerticalAlignment.Center })
                };
                boxes.Add(Tuple.Create(cb, p));
                list.Children.Add(cb);
            }
            string names = string.Join(", ", assemblies.Select(Path.GetFileName));
            List<string> result = null;
            Button all = Ui.Button(offer.Count > 0 ? "Check out all ticked" : "Continue", true, () =>
            {
                result = boxes.Where(b => b.Item1.IsChecked == true).Select(b => b.Item2).ToList();
                w.Close();
            });
            var buttons = Ui.Row(8, Ui.Button("Cancel", false, () => w.Close()));
            if (offer.Count > 0) buttons.Children.Add(Ui.Button("Just the assembly", false, () => { result = new List<string>(); w.Close(); }));
            buttons.Children.Add(all);
            buttons.HorizontalAlignment = HorizontalAlignment.Right;

            var top = Ui.Column(6, Ui.Heading(offer.Count > 0
                ? names + " uses these sub-assemblies and parts (" + offer.Count + ")"
                : names + " uses files you can't check out right now"),
                Ui.Label("They're all ticked – untick any you won't edit.", 13, Ui.Muted));
            var note = Ui.Label(takenByOthers.Count == 0
                ? "Toolbox / library files outside the vault aren't included."
                : "Skipped (someone else has them): " + string.Join(", ", takenByOthers) + ".", 12, takenByOthers.Count == 0 ? Ui.Muted : Ui.Warning);
            var bottom = Ui.Column(10, note, buttons);
            var dock = new DockPanel { Margin = new Thickness(22, 18, 22, 16) };
            DockPanel.SetDock(top, Dock.Top);
            DockPanel.SetDock(bottom, Dock.Bottom);
            top.Margin = new Thickness(0, 0, 0, 10);
            bottom.Margin = new Thickness(0, 10, 0, 0);
            dock.Children.Add(top);
            dock.Children.Add(bottom);
            dock.Children.Add(new Border { BorderBrush = Ui.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(8), Child = new ScrollViewer { Content = list } });
            w.Content = dock;
            await w.ShowDialog(owner);
            return result;
        }

        // ------------------------------------------------------------------ team notifications

        public static async Task<TeamNotifyConfig> AskTeamNotify(Window owner, string repoRoot, TeamSender who)
        {
            TeamNotifyConfig config = TeamNotify.Load(repoRoot);
            Window w = Ui.Dialog("Team notifications", 600, 460);
            w.SizeToContent = SizeToContent.Height;
            var url = new TextBox { Text = config.Webhook ?? string.Empty, Watermark = "https://discord.com/api/webhooks/…" };
            var checkIn = new CheckBox { Content = "Someone checks in (with their message)", IsChecked = config.Events.Contains(TeamEvent.CheckIn) };
            var checkOut = new CheckBox { Content = "Someone checks out", IsChecked = config.Events.Contains(TeamEvent.CheckOut) };
            var undo = new CheckBox { Content = "Someone undoes a check-out", IsChecked = config.Events.Contains(TeamEvent.Undo) };
            var force = new CheckBox { Content = "A team lead force-unlocks someone's file", IsChecked = config.Events.Contains(TeamEvent.ForceUnlock) };
            TextBlock status = Ui.Label(string.Empty, 12);
            TeamNotifyConfig result = null;

            Func<string, bool> valid = u =>
            {
                Uri uri;
                return u.Length == 0 || (Uri.TryCreate(u, UriKind.Absolute, out uri) && uri.Scheme == Uri.UriSchemeHttps);
            };
            Button test = null;
            test = Ui.Button("Send test message", false, async () =>
            {
                string u = (url.Text ?? string.Empty).Trim();
                if (u.Length == 0 || !valid(u)) { status.Text = "Paste a webhook link that starts with https:// first."; status.Foreground = Ui.Danger; return; }
                test.IsEnabled = false;
                status.Text = "Sending…"; status.Foreground = Ui.Muted;
                try { await TeamNotify.SendTestAsync(u, repoRoot, who); status.Text = "Sent. Check your chat channel."; status.Foreground = Ui.Success; }
                catch (Exception ex) { status.Text = ex.Message; status.Foreground = Ui.Danger; }
                finally { test.IsEnabled = true; }
            });
            Button save = Ui.Button("Save for the team", true, () =>
            {
                string u = (url.Text ?? string.Empty).Trim();
                if (!valid(u)) { status.Text = "The webhook link must start with https://"; status.Foreground = Ui.Danger; return; }
                var c = new TeamNotifyConfig { Webhook = u };
                c.Events.Clear();
                if (checkOut.IsChecked == true) c.Events.Add(TeamEvent.CheckOut);
                if (checkIn.IsChecked == true) c.Events.Add(TeamEvent.CheckIn);
                if (undo.IsChecked == true) c.Events.Add(TeamEvent.Undo);
                if (force.IsChecked == true) c.Events.Add(TeamEvent.ForceUnlock);
                result = c;
                w.Close();
            });
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            grid.Children.Add(test);
            var right = Ui.Row(8, Ui.Button("Cancel", false, () => w.Close()), save);
            Grid.SetColumn(right, 1);
            grid.Children.Add(right);

            var content = Ui.Column(10,
                Ui.Heading("Post check-outs and check-ins to your team chat"),
                Ui.Label("Paste a webhook link from Discord (Edit Channel → Integrations → Webhooks), Slack (Incoming Webhooks) or Microsoft Teams. "
                       + "It's saved in the vault, so everyone's PartLock posts to it after their next Get Latest. Posts show each person's GitHub name and picture.", 13, Ui.Muted),
                url, checkIn, checkOut, undo, force,
                Ui.Label("Anyone who can open the vault can see this link. Use a channel just for CAD updates.", 12, Ui.Muted),
                status, grid);
            content.Margin = new Thickness(22, 18, 22, 16);
            w.Content = content;
            await w.ShowDialog(owner);
            return result;
        }

        // ------------------------------------------------------------------ history

        public static async Task ShowHistory(Window owner, string path, IReadOnlyList<HistoryEntry> entries)
        {
            Window w = Ui.Dialog("History", 700, 460);
            var list = new StackPanel { Spacing = 6 };
            if (entries.Count == 0) list.Children.Add(Ui.Label("No check-ins yet for this file.", 13, Ui.Muted));
            foreach (HistoryEntry e in entries)
            {
                list.Children.Add(Ui.Card(Ui.Column(2,
                    Ui.Label(e.Message ?? string.Empty, 13, Ui.Text, FontWeight.SemiBold),
                    Ui.Label((e.Author ?? "?") + " · " + (e.Date == DateTime.MinValue ? string.Empty : e.Date.ToLocalTime().ToString("MMM d, yyyy h:mm tt")) +
                             " · " + (e.Commit ?? string.Empty).Substring(0, Math.Min(7, (e.Commit ?? string.Empty).Length)), 12, Ui.Muted)), 10));
            }
            var top = Ui.Column(4, Ui.Heading(Path.GetFileName(path)), Ui.Label(path, 12, Ui.Muted));
            top.Margin = new Thickness(0, 0, 0, 10);
            var close = Ui.Button("Close", true, () => w.Close());
            close.HorizontalAlignment = HorizontalAlignment.Right;
            close.Margin = new Thickness(0, 10, 0, 0);
            var dock = new DockPanel { Margin = new Thickness(22, 18, 22, 16) };
            DockPanel.SetDock(top, Dock.Top);
            DockPanel.SetDock(close, Dock.Bottom);
            dock.Children.Add(top);
            dock.Children.Add(close);
            dock.Children.Add(new ScrollViewer { Content = list });
            w.Content = dock;
            await w.ShowDialog(owner);
        }
    }
}
