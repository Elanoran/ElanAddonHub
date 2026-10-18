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
            Path.Combine(root, Resolve(root, flavor) ?? "_retail_", "Interface", "AddOns");

        public static bool HasFlavor(string root, string flavor) =>
            !string.IsNullOrEmpty(root) && Directory.Exists(Path.Combine(root, Resolve(root, flavor) ?? "_retail_"));

        // 2.94: WoW Forever is played from _classic_beta_ during its beta; the released game gets its own client folder, _forever_ (as Wowhead's
        // client expects, with the uninstall entry "World of Warcraft Forever"). Both names mean "the Forever client": a real _forever_ client wins,
        // else _classic_beta_. Any other folder name is returned as it is.
        public const string ForeverFolder = "_forever_", ForeverBetaFolder = "_classic_beta_";

        public static bool IsForever(string flavor) => flavor == ForeverFolder || flavor == ForeverBetaFolder;

        public static string Resolve(string root, string flavor)
        {
            if (!IsForever(flavor) || string.IsNullOrEmpty(root)) return flavor;
            if (IsClient(root, ForeverFolder)) return ForeverFolder;
            if (Directory.Exists(Path.Combine(root, ForeverBetaFolder))) return ForeverBetaFolder;
            return flavor;
        }

        // a client folder the launcher installed: it has its .flavor.info, or a Wow*.exe
        public static bool IsClient(string root, string flavor)
        {
            try
            {
                var dir = Path.Combine(root, flavor);
                return Directory.Exists(dir) && (File.Exists(Path.Combine(dir, ".flavor.info")) || Directory.GetFiles(dir, "Wow*.exe").Length > 0);
            }
            catch { return false; }
        }

        // the product the launcher wrote into the client folder's .flavor.info ("Product Flavor!STRING:0" then e.g. "wow_classic_beta"); null when there is none
        public static string ProductOf(string root, string flavor)
        {
            try
            {
                if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(flavor)) return null;
                var f = Path.Combine(root, flavor, ".flavor.info");
                if (!File.Exists(f)) return null;
                string text;
                using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) using (var sr = new StreamReader(fs)) text = sr.ReadToEnd();
                var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length < 2 || !lines[0].StartsWith("Product Flavor", StringComparison.OrdinalIgnoreCase)) return null;
                var p = lines[1].Trim();
                return p.Length > 0 && p.Length <= 64 && p.All(c => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_') ? p : null;
            }
            catch { return null; }
        }

        // 2.94: the Forever client folder (_classic_beta_ in the beta, _forever_ once released) and the product of a client folder, on a made-up WoW folder
        public static string SelfTest(string dir)
        {
            var sb = new System.Text.StringBuilder();
            void Ck(string what, bool ok, string info = null) => sb.AppendLine($"wowlocator {what}: {(ok ? "ok" : "FAIL")}{(info == null ? "" : " (" + info + ")")}");
            try
            {
                var root = Path.Combine(dir, "wowlocator", "World of Warcraft");
                if (Directory.Exists(root)) Directory.Delete(root, true);
                Directory.CreateDirectory(Path.Combine(root, ForeverBetaFolder));
                Directory.CreateDirectory(Path.Combine(root, "_classic_era_"));
                File.WriteAllText(Path.Combine(root, ForeverBetaFolder, ".flavor.info"), "Product Flavor!STRING:0\r\nwow_classic_beta\r\n");
                Ck("beta only: _classic_beta_ is the Forever client", Resolve(root, ForeverBetaFolder) == ForeverBetaFolder && Resolve(root, ForeverFolder) == ForeverBetaFolder);
                Ck("beta only: add-ons go to _classic_beta_", AddOnsDir(root, ForeverBetaFolder) == Path.Combine(root, ForeverBetaFolder, "Interface", "AddOns"));
                Directory.CreateDirectory(Path.Combine(root, ForeverFolder));
                Ck("an empty _forever_ folder is not a client yet", Resolve(root, ForeverBetaFolder) == ForeverBetaFolder);
                File.WriteAllText(Path.Combine(root, ForeverFolder, ".flavor.info"), "Product Flavor!STRING:0\r\nwow_forever\r\n");
                Ck("released: _forever_ wins for both names", Resolve(root, ForeverBetaFolder) == ForeverFolder && Resolve(root, ForeverFolder) == ForeverFolder);
                Ck("released: add-ons go to _forever_ (a manifest that still says _classic_beta_)", AddOnsDir(root, ForeverBetaFolder) == Path.Combine(root, ForeverFolder, "Interface", "AddOns") && HasFlavor(root, ForeverBetaFolder));
                Ck("other client folders are left alone", Resolve(root, "_classic_era_") == "_classic_era_" && Resolve(root, "_retail_") == "_retail_" && Resolve(root, null) == null);
                Ck("the product comes from .flavor.info", ProductOf(root, ForeverFolder) == "wow_forever" && CascStorage.ProductFor(root, ForeverBetaFolder) == "wow_forever", ProductOf(root, ForeverFolder));
                Ck("no .flavor.info: the known list", ProductOf(root, "_classic_era_") == null && CascStorage.ProductFor(root, "_classic_era_") == "wow_classic_era");
                File.WriteAllText(Path.Combine(root, "_classic_era_", ".flavor.info"), "Product Flavor!STRING:0\r\n..\\evil path\r\n");
                Ck("a .flavor.info with a strange product is ignored", ProductOf(root, "_classic_era_") == null);
                var rows = new List<string[]> { new[] { "wow" }, new[] { "wow_classic_era" } };
                Ck("a client's product that is missing never falls back to another game", CascStorage.PickRow(rows, 0, "wow_forever", ForeverFolder) == null);
                Ck("an unknown folder never falls back to retail", CascStorage.PickRow(rows, 0, null, "_odd_")?[0] == "wow_classic_era");
                Ck("retail still finds wow", CascStorage.PickRow(rows, 0, "wow", "_retail_")?[0] == "wow");
                Directory.Delete(Path.Combine(dir, "wowlocator"), true);
            }
            catch (Exception e) { sb.AppendLine("wowlocator test: FAIL " + e); }
            return sb.ToString().TrimEnd();
        }
    }
}
