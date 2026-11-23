// PartLock - asks a running SolidWorks which files an assembly uses.
//
// Uses ISldWorks.GetDocumentDependencies2 through late-bound IDispatch (no SolidWorks
// interop DLLs needed, works with any SolidWorks version). Only talks to a SolidWorks
// that is already running, unless the caller asks it to start a hidden SolidWorks
// (which is closed again afterwards).
//
// Lists are also saved in the vault (.partlock/refs/<assembly path>.txt, with the SHA-256 of
// the assembly they belong to) when an assembly is checked in, so teammates get them without
// SolidWorks at all.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace PartLock.Platform
{
    public static class SolidWorksRefs
    {
        /// <summary>
        /// Full paths of every file the assembly uses (all levels), or null when SolidWorks
        /// isn't running, didn't answer within <paramref name="timeout"/>, or the call failed.
        /// </summary>
        public static async Task<List<string>> GetDependenciesAsync(string assemblyPath, TimeSpan timeout,
            bool startIfNotRunning = false)
        {
            var before = new HashSet<int>();
            if (startIfNotRunning)
            {
                try { foreach (var p in System.Diagnostics.Process.GetProcessesByName("SLDWORKS")) before.Add(p.Id); }
                catch (Exception) { }
            }

            Task<List<string>> work = Task.Run(() => Query(assemblyPath, startIfNotRunning));
            Task done = await Task.WhenAny(work, Task.Delay(timeout)).ConfigureAwait(false);
            if (done != work)
            {
                // SolidWorks busy (e.g. a dialog is open); give up quietly. If we started a hidden
                // SolidWorks that got stuck (license prompt etc.), don't leave it running.
                if (startIfNotRunning && before.Count == 0)
                {
                    try
                    {
                        foreach (var p in System.Diagnostics.Process.GetProcessesByName("SLDWORKS"))
                            if (!before.Contains(p.Id)) { try { p.Kill(); } catch (Exception) { } }
                    }
                    catch (Exception) { }
                }
                return null;
            }
            try { return await work.ConfigureAwait(false); }
            catch (Exception) { return null; }
        }

        /// <summary>True when SolidWorks is installed (registered for automation) on this PC.</summary>
        public static bool IsInstalled
        {
            get
            {
                try { return Type.GetTypeFromProgID("SldWorks.Application", false) != null; }
                catch (Exception) { return false; }
            }
        }

        public static bool IsRunning
        {
            get
            {
                try { object o = Marshal.GetActiveObject("SldWorks.Application"); Marshal.ReleaseComObject(o); return true; }
                catch (Exception) { return false; }
            }
        }

        private static List<string> Query(string assemblyPath, bool startIfNotRunning)
        {
            object sw = null;
            bool startedByUs = false;
            try
            {
                sw = Marshal.GetActiveObject("SldWorks.Application");
            }
            catch (Exception)
            {
                if (!startIfNotRunning) return null; // not running
                try
                {
                    Type type = Type.GetTypeFromProgID("SldWorks.Application", false);
                    if (type == null) return null; // not installed
                    sw = Activator.CreateInstance(type);
                    startedByUs = true;
                    try { sw.GetType().InvokeMember("Visible", BindingFlags.SetProperty, null, sw, new object[] { false }); }
                    catch (Exception) { }
                }
                catch (Exception)
                {
                    return null;
                }
            }

            try
            {
                object result = sw.GetType().InvokeMember(
                    "GetDocumentDependencies2", BindingFlags.InvokeMethod, null, sw,
                    new object[] { assemblyPath, true, true, false });

                // Pairs of [file name, full path].
                var items = new List<string>();
                var list = result as IEnumerable;
                if (list == null) return items;
                foreach (object o in list) items.Add(o == null ? null : o.ToString());

                var paths = new List<string>();
                for (int i = 1; i < items.Count; i += 2)
                {
                    if (!string.IsNullOrEmpty(items[i])) paths.Add(items[i]);
                }
                return paths;
            }
            finally
            {
                if (startedByUs)
                {
                    try { sw.GetType().InvokeMember("ExitApp", BindingFlags.InvokeMethod, null, sw, null); }
                    catch (Exception) { }
                }
                try { Marshal.ReleaseComObject(sw); } catch (Exception) { }
            }
        }

        // ------------------------------------------------------------------
        // Saved lists in the vault
        // ------------------------------------------------------------------

        public const string CacheFolder = ".partlock/refs";

        public static string CacheRelativePath(string assemblyRelative)
        {
            return CacheFolder + "/" + assemblyRelative.Replace('\\', '/') + ".txt";
        }

        public static string FileHash(string fullPath)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            using (FileStream stream = File.OpenRead(fullPath))
            {
                byte[] hash = sha.ComputeHash(stream);
                var sb = new System.Text.StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>The saved sub-assembly list for this exact version of the assembly, or null.</summary>
        public static List<string> ReadCache(string repoRoot, string assemblyRelative)
        {
            try
            {
                string cache = Path.Combine(repoRoot, CacheRelativePath(assemblyRelative).Replace('/', Path.DirectorySeparatorChar));
                string assembly = Path.Combine(repoRoot, assemblyRelative.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(cache) || !File.Exists(assembly)) return null;

                string[] lines = File.ReadAllLines(cache);
                string expected = null;
                bool includesParts = false;
                var subs = new List<string>();
                foreach (string raw in lines)
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                    if (line.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase)) { expected = line.Substring(7).Trim(); continue; }
                    if (line.StartsWith("includes=", StringComparison.OrdinalIgnoreCase))
                    {
                        includesParts = line.IndexOf("parts", StringComparison.OrdinalIgnoreCase) >= 0;
                        continue;
                    }
                    subs.Add(line.Replace('\\', '/'));
                }
                if (!includesParts) return null; // saved by v0.5.x (sub-assemblies only): look it up again
                if (expected == null || !string.Equals(expected, FileHash(assembly), StringComparison.OrdinalIgnoreCase)) return null;
                return subs;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Saves the list; returns the cache file's repo-relative path, or null on failure.</summary>
        public static string WriteCache(string repoRoot, string assemblyRelative, IEnumerable<string> subAssemblies)
        {
            try
            {
                string rel = CacheRelativePath(assemblyRelative);
                string cache = Path.Combine(repoRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                string assembly = Path.Combine(repoRoot, assemblyRelative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(cache));
                var text = new System.Text.StringBuilder();
                text.AppendLine("# Sub-assemblies and parts of " + assemblyRelative + " (written by PartLock; safe to delete)");
                text.AppendLine("sha256=" + FileHash(assembly));
                text.AppendLine("includes=subassemblies,parts");
                foreach (string sub in subAssemblies) text.AppendLine(sub);
                File.WriteAllText(cache, text.ToString(), new System.Text.UTF8Encoding(false));
                return rel;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static bool IsAssembly(string path)
        {
            return !string.IsNullOrEmpty(path) &&
                   path.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsPart(string path)
        {
            return !string.IsNullOrEmpty(path) &&
                   path.EndsWith(".sldprt", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Repo-relative, forward-slash paths of the sub-assemblies and parts that are real files
        /// inside the vault (sub-assemblies first, then parts, each in SolidWorks' order).
        /// </summary>
        public static List<string> ComponentsInVault(IEnumerable<string> dependencies, string repoRoot, string mainAssemblyRelative)
        {
            var list = dependencies == null ? new List<string>() : new List<string>(dependencies);
            var result = SubAssembliesInVault(list, repoRoot, mainAssemblyRelative);
            result.AddRange(FilesInVault(list, repoRoot, mainAssemblyRelative, IsPart));
            return result;
        }

        /// <summary>Repo-relative, forward-slash paths of the sub-assemblies that are real files inside the vault.</summary>
        public static List<string> SubAssembliesInVault(IEnumerable<string> dependencies, string repoRoot, string mainAssemblyRelative)
        {
            return FilesInVault(dependencies, repoRoot, mainAssemblyRelative, IsAssembly);
        }

        private static List<string> FilesInVault(IEnumerable<string> dependencies, string repoRoot, string mainAssemblyRelative,
            Func<string, bool> kind)
        {
            var result = new List<string>();
            if (dependencies == null || string.IsNullOrEmpty(repoRoot)) return result;

            string root = Path.GetFullPath(repoRoot).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string dep in dependencies)
            {
                if (!kind(dep)) continue;
                string full;
                try { full = Path.GetFullPath(dep); }
                catch (Exception) { continue; }
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue; // Temp / Toolbox / other folders
                if (!File.Exists(full)) continue;

                string relative = full.Substring(root.Length).Replace('\\', '/');
                if (string.Equals(relative, mainAssemblyRelative, StringComparison.OrdinalIgnoreCase)) continue;
                if (seen.Add(relative)) result.Add(relative);
            }
            return result;
        }
    }
}
