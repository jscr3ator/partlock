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

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

        private static void NotifyShell()
        {
            try { SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero); }
            catch (Exception) { }
        }
    }
}
