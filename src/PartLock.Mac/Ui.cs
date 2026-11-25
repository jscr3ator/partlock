using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using PartLock.Core;

namespace PartLock.Mac
{
    /// <summary>Colours, fonts and small builders shared by every PartLock window (same palette as Windows).</summary>
    internal static class Ui
    {
        public static readonly IBrush Background = Brush("#F5F6F8");
        public static readonly IBrush Panel = Brushes.White;
        public static readonly IBrush Border = Brush("#E2E5EA");
        public static readonly IBrush Text = Brush("#1F2937");
        public static readonly IBrush Muted = Brush("#6B7280");
        public static readonly IBrush Accent = Brush("#2563EB");
        public static readonly IBrush Success = Brush("#15803D");
        public static readonly IBrush Warning = Brush("#B45309");
        public static readonly IBrush Danger = Brush("#DC2626");
        public static readonly IBrush BannerBg = Brush("#FEF3C7");
        public static readonly IBrush BannerText = Brush("#92400E");
        public static readonly IBrush InfoBg = Brush("#DBEAFE");
        public static readonly IBrush InfoText = Brush("#1E3A8A");
        public static readonly IBrush LogBg = Brush("#F9FAFB");

        public static IBrush Brush(string hex) { return new SolidColorBrush(Color.Parse(hex)); }

        private static Bitmap _logo;
        public static Bitmap Logo
        {
            get
            {
                if (_logo != null) return _logo;
                try
                {
                    using (Stream s = typeof(Ui).Assembly.GetManifestResourceStream("PartLock.logo.png"))
                        if (s != null) _logo = new Bitmap(s);
                }
                catch (Exception) { }
                return _logo;
            }
        }

        public static TextBlock Label(string text, double size = 13, IBrush color = null, FontWeight weight = FontWeight.Normal)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = size,
                Foreground = color ?? Text,
                FontWeight = weight,
                TextWrapping = TextWrapping.Wrap
            };
        }

        public static TextBlock Heading(string text) { return Label(text, 17, Text, FontWeight.SemiBold); }

        public static Button Button(string text, bool primary = false, Action onClick = null)
        {
            var b = new Button
            {
                Content = text,
                Padding = new Thickness(16, 7),
                MinHeight = 34,
                VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center
            };
            if (primary) b.Classes.Add("accent");
            if (onClick != null) b.Click += (s, e) => onClick();
            return b;
        }

        public static void SetPrimary(Button b, bool primary)
        {
            if (primary && !b.Classes.Contains("accent")) b.Classes.Add("accent");
            if (!primary && b.Classes.Contains("accent")) b.Classes.Remove("accent");
        }

        public static Border Card(Control child, double padding = 16)
        {
            return new Border
            {
                Background = Panel,
                BorderBrush = Border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(padding),
                Child = child
            };
        }

        public static StackPanel Row(double spacing, params Control[] children)
        {
            var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing };
            foreach (Control c in children) p.Children.Add(c);
            return p;
        }

        public static StackPanel Column(double spacing, params Control[] children)
        {
            var p = new StackPanel { Orientation = Orientation.Vertical, Spacing = spacing };
            foreach (Control c in children) p.Children.Add(c);
            return p;
        }

        public static Window Dialog(string title, double width, double height)
        {
            return new Window
            {
                Title = "PartLock – " + title,
                Width = width,
                Height = height,
                MinWidth = Math.Min(width, 420),
                MinHeight = Math.Min(height, 240),
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = Panel,
                CanResize = true
            };
        }

        // ------------------------------------------------------------- message boxes

        /// <summary>Simple modal message. Returns the index of the clicked button (or -1 if closed).</summary>
        public static async Task<int> Ask(Window owner, string title, string message, params string[] buttons)
        {
            if (buttons == null || buttons.Length == 0) buttons = new[] { "OK" };
            Window w = Dialog(title, 480, 220);
            w.SizeToContent = SizeToContent.Height;
            w.CanResize = false;
            int result = -1;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
            for (int i = 0; i < buttons.Length; i++)
            {
                int index = i;
                Button b = Button(buttons[i], i == 0, () => { result = index; w.Close(); });
                row.Children.Add(b);
            }
            // Primary (first) button on the right, like macOS.
            var ordered = new List<Control>(row.Children);
            ordered.Reverse();
            row.Children.Clear();
            foreach (Control c in ordered) row.Children.Add(c);

            w.Content = new StackPanel
            {
                Margin = new Thickness(22, 18, 22, 16),
                Spacing = 18,
                Children = { new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 13, Foreground = Text }, row }
            };
            if (owner != null && owner.IsVisible) await w.ShowDialog(owner);
            else await ShowStandalone(w);
            return result;
        }

        public static Task Info(Window owner, string message) { return Ask(owner, "PartLock", message, "OK"); }

        public static async Task Error(Window owner, Exception ex)
        {
            var vault = ex as VaultException;
            string message = vault != null ? vault.Message : "Something went wrong: " + ex.Message;
            string details = vault != null ? (vault.Details ?? string.Empty) : ex.ToString();
            ErrorLog.Write(message, details);
            if (string.IsNullOrWhiteSpace(details))
            {
                await Info(owner, message);
                return;
            }
            int choice = await Ask(owner, "Problem", message, "OK", "Show details");
            if (choice == 1)
            {
                Window w = Dialog("Details", 680, 420);
                w.Content = new DockPanel
                {
                    Margin = new Thickness(16),
                    Children =
                    {
                        new TextBox
                        {
                            Text = message + "\n\n" + GitRunner.Redact(details),
                            IsReadOnly = true,
                            AcceptsReturn = true,
                            TextWrapping = TextWrapping.NoWrap,
                            FontFamily = new FontFamily("Menlo, Consolas, monospace"),
                            FontSize = 12
                        }
                    }
                };
                if (owner != null && owner.IsVisible) await w.ShowDialog(owner);
                else await ShowStandalone(w);
            }
        }

        /// <summary>Shows a window with no owner and waits until it closes.</summary>
        public static Task ShowStandalone(Window w)
        {
            var done = new TaskCompletionSource<bool>();
            w.Closed += (s, e) => done.TrySetResult(true);
            w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            w.Show();
            return done.Task;
        }

        public static string FileCount(int n) { return n + (n == 1 ? " file" : " files"); }

        public static string RelativeTime(DateTime utc)
        {
            if (utc == DateTime.MinValue) return string.Empty;
            TimeSpan ago = DateTime.UtcNow - utc;
            if (ago.TotalMinutes < 1) return "just now";
            if (ago.TotalMinutes < 60) return (int)ago.TotalMinutes + " min ago";
            if (ago.TotalHours < 24) return (int)ago.TotalHours + " h ago";
            if (ago.TotalDays < 7) return (int)ago.TotalDays + " d ago";
            return utc.ToLocalTime().ToString("MMM d");
        }

        public static string StatusText(VaultFile f, string me)
        {
            switch (f.State)
            {
                case FileState.ConflictLockedByOther: return "Edited, but " + Owner(f) + " has it";
                case FileState.LockedByOther: return "Checked out by " + Owner(f);
                case FileState.CheckedOutByMe: return f.Modified ? "Checked out – edited" : "Checked out by you";
                case FileState.ModifiedNotCheckedOut: return "Edited (not checked out)";
                case FileState.NewFile: return "New";
                case FileState.Changed: return "Changed";
                case FileState.Deleted: return "Deleted";
                case FileState.OutOfDate: return "Newer version on server";
                default: return "Available";
            }
        }

        public static IBrush StatusColor(FileState s)
        {
            switch (s)
            {
                case FileState.ConflictLockedByOther: return Danger;
                case FileState.LockedByOther: return Danger;
                case FileState.CheckedOutByMe: return Accent;
                case FileState.ModifiedNotCheckedOut: return Warning;
                case FileState.NewFile: return Brush("#7C3AED");
                case FileState.Changed: return Warning;
                case FileState.Deleted: return Brush("#475569");
                case FileState.OutOfDate: return Warning;
                default: return Success;
            }
        }

        private static string Owner(VaultFile f) { return f.Lock != null && !string.IsNullOrEmpty(f.Lock.Owner) ? f.Lock.Owner : "someone"; }
    }

    /// <summary>Writes problems to ~/Library/Logs/PartLock/error.log (the place to look when reporting a bug).</summary>
    internal static class ErrorLog
    {
        public static string FilePath
        {
            get
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string dir = OperatingSystem.IsMacOS()
                    ? Path.Combine(home, "Library", "Logs", "PartLock")
                    : Path.Combine(home, ".partlock");
                return Path.Combine(dir, "error.log");
            }
        }

        public static void Write(string message, string details)
        {
            try
            {
                string file = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.AppendAllText(file, "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + message + Environment.NewLine +
                                         GitRunner.Redact(details ?? string.Empty) + Environment.NewLine + Environment.NewLine);
            }
            catch (Exception) { }
        }
    }
}

namespace PartLock.UI
{
    /// <summary>Shared Core/Platform code logs through this name on Windows; on Mac it goes to ErrorLog.</summary>
    internal static class ErrorDialog
    {
        public static void LogToFile(string message, string details) { PartLock.Mac.ErrorLog.Write(message, details); }
    }
}
