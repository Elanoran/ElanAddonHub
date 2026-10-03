using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace ElansAddonHub.Services
{
    // Finds the "World of Warcraft" folder (the one holding _retail_, _classic_, _classic_beta_ ...).
    public static class WowLocator
    {
        public static bool IsWowRoot(string dir)
        {
            try
            {
                return !string.IsNullOrEmpty(dir) && Directory.Exists(dir) &&
                       Directory.GetDirectories(dir).Any(d =>
                       {
                           var n = Path.GetFileName(d);
                           return n.Length > 2 && n.StartsWith("_") && n.EndsWith("_");
                       });
            }
            catch { return false; }
        }

        // A picked file or folder -> the WoW root, e.g. ...\_classic_beta_\WowClassicB.exe -> ...\World of Warcraft
        public static string RootFrom(string path)
        {
            var dir = File.Exists(path) ? Path.GetDirectoryName(path) : path;
            for (int i = 0; i < 4 && dir != null; i++)
            {
                if (IsWowRoot(dir)) return dir;
                dir = Path.GetDirectoryName(dir);
            }
            return null;
        }

        public static string Find()
        {
            foreach (var c in Candidates())
            {
                var root = RootFrom(c);
                if (root != null) return root;
            }
            return null;
        }

        static IEnumerable<string> Candidates()
        {
            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                string p = null;
                try
                {
                    using (var hk = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (var k = hk.OpenSubKey(@"SOFTWARE\Blizzard Entertainment\World of Warcraft"))
                        p = k?.GetValue("InstallPath") as string;
                }
                catch { }
                if (!string.IsNullOrEmpty(p)) yield return p.TrimEnd('\\');
            }
            var subs = new[] { @"World of Warcraft", @"Blizzard\World of Warcraft", @"Games\World of Warcraft",
                               @"Program Files (x86)\World of Warcraft", @"Program Files\World of Warcraft",
                               @"Battle.net\World of Warcraft", @"Games\Blizzard\World of Warcraft" };
            foreach (var drive in DriveInfo.GetDrives())
            {
                bool ready;
                try { ready = drive.IsReady && drive.DriveType == DriveType.Fixed; } catch { ready = false; }
                if (!ready) continue;
                foreach (var s in subs) yield return Path.Combine(drive.RootDirectory.FullName, s);
            }
        }

        public static string AddOnsDir(string root, string flavor) =>
            Path.Combine(root, flavor ?? "_retail_", "Interface", "AddOns");

        public static bool HasFlavor(string root, string flavor) =>
            !string.IsNullOrEmpty(root) && Directory.Exists(Path.Combine(root, flavor ?? "_retail_"));
    }
}
