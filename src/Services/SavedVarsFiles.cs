using System;
using System.Collections.Generic;
using System.IO;

namespace ElansAddonHub.Services
{
    // The one shared way to find an addon's SavedVariables files: <root>\_<flavor>_\WTF\Account\<ACCOUNT>\SavedVariables\<name>.lua.
    // Read-only. Folders and files that are reparse points (junctions, symlinks) are skipped, so a link can never lead the app out of the WoW folder.
    // New code uses this; the older readers (GamePresence, InventoryData, AddonHealth, WowCalendar, FlightShare) still walk the folders themselves.
    public static class SavedVarsFiles
    {
        public static List<string> For(string root, string addonFileName)
        {
            var files = new List<string>();
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(addonFileName) || !Directory.Exists(root)) return files;
            if (addonFileName.IndexOfAny(new[] { '\\', '/', ':' }) >= 0) return files; // a file name, never a path
            try
            {
                foreach (var flavor in Directory.GetDirectories(root, "_*_"))
                {
                    var accounts = Path.Combine(flavor, "WTF", "Account");
                    if (!Directory.Exists(accounts) || IsReparse(accounts)) continue;
                    foreach (var acc in Directory.GetDirectories(accounts))
                    {
                        if (IsReparse(acc)) continue;
                        var dir = Path.Combine(acc, "SavedVariables");
                        if (!Directory.Exists(dir) || IsReparse(dir)) continue;
                        var f = Path.Combine(dir, addonFileName);
                        if (File.Exists(f) && !IsReparse(f)) files.Add(f);
                    }
                }
            }
            catch { }
            return files;
        }

        static bool IsReparse(string path)
        {
            try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; } catch { return true; }
        }
    }
}
