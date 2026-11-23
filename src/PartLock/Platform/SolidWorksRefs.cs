// PartLock - asks a running SolidWorks which files an assembly uses.
//
// Uses ISldWorks.GetDocumentDependencies2 through late-bound IDispatch (no SolidWorks
// interop DLLs needed, works with any SolidWorks version). Only talks to a SolidWorks
// that is already running: PartLock never starts SolidWorks on its own.
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
        public static async Task<List<string>> GetDependenciesAsync(string assemblyPath, TimeSpan timeout)
        {
            Task<List<string>> work = Task.Run(() => Query(assemblyPath));
            Task done = await Task.WhenAny(work, Task.Delay(timeout)).ConfigureAwait(false);
            if (done != work) return null; // SolidWorks busy (e.g. a dialog is open); give up quietly
            try { return await work.ConfigureAwait(false); }
            catch (Exception) { return null; }
        }

        private static List<string> Query(string assemblyPath)
        {
            object sw;
            try
            {
                sw = Marshal.GetActiveObject("SldWorks.Application");
            }
            catch (Exception)
            {
                return null; // not running
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
                try { Marshal.ReleaseComObject(sw); } catch (Exception) { }
            }
        }

        public static bool IsAssembly(string path)
        {
            return !string.IsNullOrEmpty(path) &&
                   path.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Repo-relative, forward-slash paths of the sub-assemblies that are real files inside the vault.</summary>
        public static List<string> SubAssembliesInVault(IEnumerable<string> dependencies, string repoRoot, string mainAssemblyRelative)
        {
            var result = new List<string>();
            if (dependencies == null || string.IsNullOrEmpty(repoRoot)) return result;

            string root = Path.GetFullPath(repoRoot).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string dep in dependencies)
            {
                if (!IsAssembly(dep)) continue;
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
