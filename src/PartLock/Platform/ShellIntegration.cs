// PartLock - "PartLock >" right-click menu on CAD files in File Explorer.
//
// Registered per user under HKCU\Software\Classes\SystemFileAssociations\.<ext>\shell\PartLock
// (no admin rights, doesn't touch the file type's own handler). Each entry runs
// PartLock.exe --checkout|--checkin|--undo|--open "<file>".
// On Windows 11 the menu is under "Show more options" (or Shift+right-click).
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PartLock.Platform
{
    public static class ShellIntegration
    {
        private static readonly string[] Extensions =
        {
            "sldprt", "sldasm", "slddrw", "step", "stp", "iges", "igs", "stl", "x_t", "dxf", "dwg", "3mf"
        };

        private static readonly string[][] Verbs =
        {
            new[] { "1checkout", "Check Out", "--checkout" },
            new[] { "2checkin", "Check In…", "--checkin" },
            new[] { "3undo", "Undo Check Out", "--undo" },
            new[] { "4open", "Open in PartLock", "--open" }
        };

        private static string KeyFor(string extension)
        {
            return @"Software\Classes\SystemFileAssociations\." + extension + @"\shell\PartLock";
        }

        /// <summary>Adds (or refreshes, e.g. after the exe moved) the right-click menu. Never throws.</summary>
        public static void Register(string exePath)
        {
            try
            {
                bool changed = false;
                foreach (string ext in Extensions)
                {
                    using (RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyFor(ext)))
                    {
                        if (key == null) continue;
                        changed |= Set(key, "MUIVerb", "PartLock");
                        changed |= Set(key, "SubCommands", string.Empty);
                        changed |= Set(key, "Icon", "\"" + exePath + "\",0");

                        foreach (string[] verb in Verbs)
                        {
                            using (RegistryKey v = key.CreateSubKey(@"shell\" + verb[0]))
                            using (RegistryKey c = key.CreateSubKey(@"shell\" + verb[0] + @"\command"))
                            {
                                if (v == null || c == null) continue;
                                changed |= Set(v, "MUIVerb", verb[1]);
                                changed |= Set(c, null, "\"" + exePath + "\" " + verb[2] + " \"%1\"");
                            }
                        }
                    }
                }
                if (changed) NotifyShell();
            }
            catch (Exception)
            {
                // A missing right-click menu must never stop PartLock.
            }
        }

        public static void Unregister()
        {
            try
            {
                foreach (string ext in Extensions)
                    Registry.CurrentUser.DeleteSubKeyTree(KeyFor(ext), false);
                NotifyShell();
            }
            catch (Exception) { }
        }

        private static bool Set(RegistryKey key, string name, string value)
        {
            object current = key.GetValue(name);
            if (current is string && (string)current == value) return false;
            key.SetValue(name ?? string.Empty, value, RegistryValueKind.String);
            return true;
        }

        // ------------------------------------------------------------------
        // Windows 11: show the full right-click menu right away (no "Show more options").
        // Third-party entries only appear in Windows 11's short menu for signed, packaged apps,
        // so instead PartLock can switch this user's Explorer back to the full menu. It's the
        // standard per-user setting (an empty InprocServer32 for this CLSID) and fully reversible.
        // ------------------------------------------------------------------

        private const string ClassicMenuKey =
            @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";

        public static bool IsWindows11
        {
            get
            {
                try
                {
                    using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                    {
                        int build;
                        return key != null && int.TryParse(Convert.ToString(key.GetValue("CurrentBuildNumber")), out build) && build >= 22000;
                    }
                }
                catch (Exception) { return false; }
            }
        }

        public static bool FullMenuEnabled
        {
            get
            {
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(ClassicMenuKey + @"\InprocServer32"))
                        return key != null;
                }
                catch (Exception) { return false; }
            }
        }

        /// <summary>Turns the full right-click menu on/off for this user and restarts File Explorer so it applies.</summary>
        public static void SetFullMenu(bool enabled)
        {
            if (enabled)
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(ClassicMenuKey + @"\InprocServer32"))
                {
                    if (key != null) key.SetValue(string.Empty, string.Empty, RegistryValueKind.String);
                }
            }
            else
            {
                Registry.CurrentUser.DeleteSubKeyTree(ClassicMenuKey, false);
            }
            RestartExplorer();
        }

        private static void RestartExplorer()
        {
            try
            {
                foreach (System.Diagnostics.Process p in System.Diagnostics.Process.GetProcessesByName("explorer"))
                {
                    try { p.Kill(); p.WaitForExit(5000); } catch (Exception) { }
                }
                System.Threading.Thread.Sleep(800);
                // Windows usually restarts the shell by itself; start it if it didn't.
                if (System.Diagnostics.Process.GetProcessesByName("explorer").Length == 0)
                    System.Diagnostics.Process.Start("explorer.exe");
            }
            catch (Exception) { }
        }

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

        private static void NotifyShell()
        {
            try { SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero); }
            catch (Exception) { }
        }
    }
}
