using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using PartLock.Core;

namespace PartLock.UI
{
    /// <summary>Colours, fonts and small UI factories so every form looks the same.</summary>
    internal static class Theme
    {
        // ------------------------------------------------------------- colours

        public static readonly Color WindowBackground = FromHex("#F5F6F8");
        public static readonly Color White = Color.White;
        public static readonly Color AccentColor = FromHex("#2563EB");
        public static readonly Color AccentHover = FromHex("#1D4ED8");
        public static readonly Color AccentPressed = FromHex("#1E40AF");
        public static readonly Color BorderColor = FromHex("#E2E5EA");
        public static readonly Color TextColor = FromHex("#1F2937");
        public static readonly Color MutedColor = FromHex("#6B7280");
        public static readonly Color HoverColor = FromHex("#F3F4F6");
        public static readonly Color PressedColor = FromHex("#E5E7EB");
        public static readonly Color BannerBackground = FromHex("#FEF3C7");
        public static readonly Color BannerText = FromHex("#92400E");
        public static readonly Color LogBackground = FromHex("#F9FAFB");
        public static readonly Color Success = FromHex("#15803D");
        public static readonly Color Warning = FromHex("#B45309");
        public static readonly Color Danger = FromHex("#DC2626");
        public static readonly Color Purple = FromHex("#7C3AED");
        public static readonly Color Slate = FromHex("#475569");

        // Short aliases, handy in property initialisers.
        public static readonly Color Background = WindowBackground;
        public static readonly Color Panel = White;
        public static readonly Color Text = TextColor;
        public static readonly Color Muted = MutedColor;
        public static readonly Color Border = BorderColor;
        public static readonly Color Accent = AccentColor;
        public static readonly Color Banner = BannerBackground;

        // --------------------------------------------------------------- fonts

        public static readonly Font BaseFont = new Font("Segoe UI", 9.75f);
        public static readonly Font SmallFont = new Font("Segoe UI", 9f);
        public static readonly Font TinyFont = new Font("Segoe UI", 8.25f);
        public static readonly Font PillFont = new Font("Segoe UI", 9f);
        public static readonly Font HeadingFont = CreateSemibold(12f);
        public static readonly Font TitleFont = CreateSemibold(17f);
        public static readonly Font BrandFont = CreateSemibold(20f);
        public static readonly Font MonoFont = new Font("Consolas", 9f);
        public static readonly Font BoldFont = CreateSemibold(9.75f);

        public static readonly Font Base = BaseFont;
        public static readonly Font Small = SmallFont;
        public static readonly Font Bold = BoldFont;
        public static readonly Font Heading = HeadingFont;
        public static readonly Font Title = TitleFont;
        public static readonly Font Brand = BrandFont;
        public static readonly Font Mono = MonoFont;
        public static readonly Font Pill = PillFont;

        public static Color FromHex(string hex)
        {
            return ColorTranslator.FromHtml(hex);
        }

        public static void StyleForm(Form form)
        {
            form.Font = BaseFont;
            form.BackColor = WindowBackground;
            form.ForeColor = TextColor;
            form.AutoScaleDimensions = new SizeF(96f, 96f);
            form.AutoScaleMode = AutoScaleMode.Dpi;
        }

        public static Label MakeLabel(string text, Font font, Color color)
        {
            return new Label
            {
                Text = text,
                Font = font,
                ForeColor = color,
                AutoSize = true,
                BackColor = Color.Transparent,
                Margin = new Padding(0)
            };
        }

        public static Label HeadingLabel(string text)
        {
            return MakeLabel(text, HeadingFont, TextColor);
        }

        public static LinkLabel MakeLink(string text)
        {
            return new LinkLabel
            {
                Text = text,
                Font = SmallFont,
                AutoSize = true,
                BackColor = Color.Transparent,
                LinkColor = AccentColor,
                ActiveLinkColor = AccentPressed,
                VisitedLinkColor = AccentColor,
                LinkBehavior = LinkBehavior.HoverUnderline,
                Margin = new Padding(0)
            };
        }

        /// <summary>White panel with a 1 px border, used for the setup cards.</summary>
        public static System.Windows.Forms.Panel Card()
        {
            var card = new System.Windows.Forms.Panel
            {
                BackColor = White,
                Padding = new Padding(18),
                Margin = new Padding(0, 0, 0, 12),
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink
            };
            card.Paint += delegate(object sender, PaintEventArgs e)
            {
                DrawBorder(e.Graphics, card.ClientRectangle, BorderColor);
            };
            return card;
        }

        public static void DrawBorder(Graphics graphics, Rectangle bounds, Color color)
        {
            if (graphics == null) return;
            using (var pen = new Pen(color))
            {
                graphics.DrawRectangle(pen, bounds.X, bounds.Y,
                    Math.Max(1, bounds.Width - 1), Math.Max(1, bounds.Height - 1));
            }
        }

        public static int Scale(Control control, int pixels)
        {
            if (control == null) return pixels;
            return (int)Math.Round(pixels * control.DeviceDpi / 96.0);
        }

        public static Color Lighten(Color color, double amount)
        {
            double t = Math.Max(0.0, Math.Min(1.0, amount));
            return Color.FromArgb(
                color.A,
                (int)Math.Round(color.R + (255 - color.R) * t),
                (int)Math.Round(color.G + (255 - color.G) * t),
                (int)Math.Round(color.B + (255 - color.B) * t));
        }

        public static GraphicsPath RoundedRect(Rectangle rectangle, int radius)
        {
            int diameter = Math.Max(2, radius * 2);
            var path = new GraphicsPath();
            path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        // ------------------------------------------------------------- buttons

        public static Button CreateButton(string text, ButtonKind kind)
        {
            switch (kind)
            {
                case ButtonKind.Primary:
                    return FlatButton.Primary(text);

                case ButtonKind.Ghost:
                    FlatButton ghost = FlatButton.Secondary(text);
                    ghost.FlatAppearance.BorderSize = 0;
                    ghost.BackColor = Color.Transparent;
                    ghost.ForeColor = AccentColor;
                    ghost.FlatAppearance.MouseOverBackColor = HoverColor;
                    ghost.FlatAppearance.MouseDownBackColor = PressedColor;
                    return ghost;

                default:
                    return FlatButton.Secondary(text);
            }
        }

        // -------------------------------------------------------------- status

        public static Color StatusColor(FileState state)
        {
            switch (state)
            {
                case FileState.ConflictLockedByOther:
                    return Danger;
                case FileState.LockedByOther:
                    return Warning;
                case FileState.CheckedOutByMe:
                    return AccentColor;
                case FileState.ModifiedNotCheckedOut:
                    return Danger;
                case FileState.NewFile:
                    return Success;
                case FileState.Changed:
                    return Slate;
                case FileState.Deleted:
                    return Danger;
                case FileState.OutOfDate:
                    return Purple;
                default:
                    return MutedColor;
            }
        }

        public static string StatusText(VaultFile file, string serverUserName)
        {
            if (file == null) return "";

            switch (file.State)
            {
                case FileState.ConflictLockedByOther:
                    return "Conflict \u2013 " + LockOwner(file, serverUserName) + " has it";
                case FileState.LockedByOther:
                    return "Checked out by " + LockOwner(file, serverUserName);
                case FileState.CheckedOutByMe:
                    return file.Modified ? "Checked out by you \u00b7 edited" : "Checked out by you";
                case FileState.ModifiedNotCheckedOut:
                    return "Edited without check-out";
                case FileState.NewFile:
                    return "New \u2013 not in vault";
                case FileState.Changed:
                    return "Changed";
                case FileState.Deleted:
                    return "Deleted";
                case FileState.OutOfDate:
                    return "Newer version available";
                case FileState.Available:
                    return "Available";
                default:
                    return file.State.ToString();
            }
        }

        public static string OwnerText(VaultFile file, string serverUserName)
        {
            if (file == null) return "";
            if (file.Lock != null && !string.IsNullOrEmpty(file.Lock.Owner))
            {
                return IsMe(file.Lock, serverUserName) ? "you" : file.Lock.Owner;
            }
            return file.State == FileState.CheckedOutByMe ? "you" : "";
        }

        public static string RelativeTime(DateTime whenUtc)
        {
            if (whenUtc == DateTime.MinValue) return "";
            DateTime when = whenUtc.Kind == DateTimeKind.Utc ? whenUtc.ToLocalTime() : whenUtc;
            TimeSpan delta = DateTime.Now - when;

            if (delta.TotalSeconds < 45) return "just now";
            if (delta.TotalMinutes < 60) return Math.Max(1, (int)delta.TotalMinutes) + " min ago";
            if (delta.TotalHours < 24) return Math.Max(1, (int)delta.TotalHours) + " h ago";
            if (delta.TotalDays < 7) return Math.Max(1, (int)delta.TotalDays) + " d ago";
            return when.ToString("MMM d");
        }

        private static string LockOwner(VaultFile file, string serverUserName)
        {
            if (file.Lock == null || string.IsNullOrEmpty(file.Lock.Owner)) return "someone";
            return IsMe(file.Lock, serverUserName) ? "you" : file.Lock.Owner;
        }

        private static bool IsMe(LockInfo info, string serverUserName)
        {
            if (info == null) return false;
            if (info.IsMine) return true;
            return !string.IsNullOrEmpty(serverUserName) && !string.IsNullOrEmpty(info.Owner) &&
                   string.Equals(info.Owner, serverUserName, StringComparison.OrdinalIgnoreCase);
        }

        // ---------------------------------------------------------- cue banner

        private const int EmSetCueBanner = 0x1501;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        public static void SetCueText(TextBox box, string cue)
        {
            SetCueBanner(box, cue);
        }

        /// <summary>Grey placeholder text inside an empty TextBox.</summary>
        public static void SetCueBanner(TextBox box, string cue)
        {
            if (box == null || string.IsNullOrEmpty(cue)) return;
            if (box.IsHandleCreated)
            {
                ApplyCue(box, cue);
            }
            else
            {
                box.HandleCreated += delegate { ApplyCue(box, cue); };
            }
        }

        private static void ApplyCue(TextBox box, string cue)
        {
            try
            {
                SendMessage(box.Handle, EmSetCueBanner, new IntPtr(1), cue);
            }
            catch
            {
                // Placeholder text is cosmetic only.
            }
        }

        private static Font CreateSemibold(float size)
        {
            var candidate = new Font("Segoe UI Semibold", size);
            if (string.Equals(candidate.Name, "Segoe UI Semibold", StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
            candidate.Dispose();
            return new Font("Segoe UI", size, FontStyle.Bold);
        }
    }

    public enum ButtonKind
    {
        Primary,
        Outline,
        Ghost
    }

    /// <summary>Flat, flat, flat. Primary = blue filled, Outline = white with a border.</summary>
    internal sealed class FlatButton : Button
    {
        private FlatButton(string text, bool primary, bool small)
        {
            Text = text;
            FlatStyle = FlatStyle.Flat;
            UseVisualStyleBackColor = false;
            Font = small ? Theme.SmallFont : Theme.BaseFont;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            MinimumSize = new Size(small ? 0 : 90, small ? 30 : 40);
            Padding = new Padding(small ? 10 : 16, 0, small ? 10 : 16, 0);
            Margin = new Padding(0, 0, 8, 0);
            TextAlign = ContentAlignment.MiddleCenter;
            Cursor = Cursors.Hand;
            FlatAppearance.BorderSize = primary ? 0 : 1;
            FlatAppearance.BorderColor = Theme.BorderColor;
            BackColor = primary ? Theme.AccentColor : Theme.White;
            ForeColor = primary ? Theme.White : Theme.TextColor;
            FlatAppearance.MouseOverBackColor = primary ? Theme.AccentHover : Theme.HoverColor;
            FlatAppearance.MouseDownBackColor = primary ? Theme.AccentPressed : Theme.PressedColor;
        }

        public static FlatButton Primary(string text)
        {
            return new FlatButton(text, true, false);
        }

        public static FlatButton Secondary(string text)
        {
            return new FlatButton(text, false, false);
        }

        public static FlatButton SmallSecondary(string text)
        {
            return new FlatButton(text, false, true);
        }
    }

    /// <summary>Status text + colour, plus the little rounded pill painter.</summary>
    internal static class StatusStyle
    {
        public static string GetText(VaultFile file)
        {
            return Theme.StatusText(file, null);
        }

        public static Color GetColor(FileState state)
        {
            return Theme.StatusColor(state);
        }

        public static void DrawPill(Graphics graphics, Rectangle cell, string text, Color color, bool selected)
        {
            using (var background = new SolidBrush(selected ? SystemColors.Highlight : Theme.White))
            {
                graphics.FillRectangle(background, cell);
            }

            if (string.IsNullOrEmpty(text)) return;

            Size textSize = TextRenderer.MeasureText(
                graphics, text, Theme.PillFont,
                new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);

            int height = Math.Max(18, textSize.Height + 6);
            int dot = Math.Max(5, height / 3);
            int sidePadding = 9;
            int gap = 6;
            int neededWidth = sidePadding * 2 + dot + gap + textSize.Width;
            int maxWidth = Math.Max(24, cell.Width - 8);
            bool clipped = neededWidth > maxWidth;
            int width = clipped ? maxWidth : neededWidth;

            int x = cell.Left + 3;
            int y = cell.Top + Math.Max(1, (cell.Height - height) / 2);
            var pill = new Rectangle(x, y, width, height);

            using (var path = Theme.RoundedRect(pill, height / 2))
            using (var fill = new SolidBrush(Theme.Lighten(color, 0.87)))
            using (var outline = new Pen(Theme.Lighten(color, 0.55)))
            {
                graphics.FillPath(fill, path);
                graphics.DrawPath(outline, path);
            }

            int dotX = x + sidePadding;
            int dotY = y + (height - dot) / 2;
            using (var dotBrush = new SolidBrush(color))
            {
                graphics.FillEllipse(dotBrush, dotX, dotY, dot, dot);
            }

            int textLeft = dotX + dot + gap;
            var textRect = new Rectangle(textLeft, y, Math.Max(1, pill.Right - 4 - textLeft), height);
            TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                                    TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            if (clipped) flags |= TextFormatFlags.EndEllipsis;
            TextRenderer.DrawText(graphics, text, Theme.PillFont, textRect, color, flags);
        }
    }
}
