using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ElansAddonHub.Services
{
    // What may become a path. The manifest names addon folders, an id and a WoW flavor; the installer deletes and copies at those paths, so a hostile or
    // broken manifest ("..\..\Documents", "C:\Users", a device name) must be refused BEFORE anything is touched. Plain names only, and every computed path
    // must resolve inside the folder it belongs to.
    public static class PathSafety
    {
        public const int MaxFolderLength = 64;
        public const int MaxIdLength = 64;

        static readonly HashSet<string> Devices = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "CLOCK$",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

        // the name as it is shown in a message: no control characters, short
        public static string Show(string s)
        {
            if (s == null) return "(nothing)";
            var sb = new StringBuilder();
            foreach (var c in s) { sb.Append(char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format ? '?' : c); if (sb.Length >= 40) { sb.Append("..."); break; } }
            return "\"" + sb + "\"";
        }

        static bool IsDevice(string name)
        {
            var stem = name;
            var dot = stem.IndexOf('.');
            if (dot >= 0) stem = stem.Substring(0, dot);
            return Devices.Contains(stem.TrimEnd(' '));
        }

        // an addon folder name: letters, digits and _ - . ' ! + ( ) space; no separators, no "..", no leading dot / space, no trailing dot / space, no device name
        public static string FolderProblem(string name)
        {
            if (string.IsNullOrEmpty(name)) return "an addon folder name is empty";
            if (name.Length > MaxFolderLength) return "the folder name " + Show(name) + " is longer than " + MaxFolderLength + " characters";
            foreach (var c in name)
                if (!(char.IsLetterOrDigit(c) || "_-.'!+() ".IndexOf(c) >= 0))
                    return "the folder name " + Show(name) + " has a character a plain folder name cannot have";
            if (name.Contains("..") || name[0] == '.' || name[0] == ' ' || name[name.Length - 1] == '.' || name[name.Length - 1] == ' ')
                return "the folder name " + Show(name) + " is not a plain folder name";
            if (IsDevice(name)) return "the folder name " + Show(name) + " is a reserved Windows name";
            return null;
        }

        // an addon id (it names the backup folder): letters, digits, _ - .
        public static string IdProblem(string id)
        {
            if (string.IsNullOrEmpty(id)) return "the addon has no id";
            if (id.Length > MaxIdLength) return "the id " + Show(id) + " is too long";
            foreach (var c in id)
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-' || c == '.'))
                    return "the id " + Show(id) + " has a character an id cannot have";
            if (id.Contains("..") || id[0] == '.' || id[id.Length - 1] == '.' || IsDevice(id)) return "the id " + Show(id) + " is not a plain name";
            return null;
        }

        // a WoW client folder such as _retail_, _classic_, _classic_era_, _classic_beta_ (null/empty = the default)
        public static string FlavorProblem(string flavor)
        {
            if (string.IsNullOrEmpty(flavor)) return null;
            if (flavor.Length > 32 || flavor[0] != '_' || flavor[flavor.Length - 1] != '_' || flavor.Contains("__"))
                return "the game version " + Show(flavor) + " is not a client folder name";
            foreach (var c in flavor)
                if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_')) return "the game version " + Show(flavor) + " is not a client folder name";
            return null;
        }

        // child is strictly inside parent (both resolved: no "..", no relative parts, case-insensitive like Windows)
        public static bool IsInside(string parent, string child)
        {
            try
            {
                var p = Path.GetFullPath(parent).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                var c = Path.GetFullPath(child).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                return c.Length > p.Length && c.StartsWith(p, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception) { return false; }
        }

        // Everything about a manifest entry that becomes a path. null = safe; otherwise a sentence that says what was wrong.
        public static string AddonProblem(string wowRoot, string backupDir, AddonInfo a)
        {
            if (a == null) return "the addon entry is empty";
            var p = IdProblem(a.Id) ?? FlavorProblem(a.Flavor);
            if (p != null) return p;
            if (a.Folders == null || a.Folders.Count == 0) return "the addon names no folders to install";
            if (a.Folders.Count > 32) return "the addon names too many folders";
            foreach (var f in a.Folders) { p = FolderProblem(f); if (p != null) return p; }
            if (string.IsNullOrEmpty(wowRoot)) return null;   // nothing to resolve yet
            var addOns = WowLocator.AddOnsDir(wowRoot, a.Flavor);
            foreach (var f in a.Folders)
                if (!IsInside(addOns, Path.Combine(addOns, f))) return "the folder " + Show(f) + " would end up outside the AddOns folder";
            if (!string.IsNullOrEmpty(backupDir) && !IsInside(backupDir, Path.Combine(backupDir, a.Id, "x")))
                return "the id " + Show(a.Id) + " would put the backup outside the backup folder";
            return null;
        }

        public static string SelfTest()
        {
            var sb = new StringBuilder();
            void Ck(string what, bool ok, string info = null) => sb.AppendLine($"pathsafety {what}: {(ok ? "ok" : "FAIL")}{(info == null ? "" : " (" + info + ")")}");
            try
            {
                foreach (var good in new[] { "ElansHub", "Questie", "!BugGrabber", "Details_Streamer", "Titan Panel", "ElvUI-Classic", "Bagnon's.Util", "+Wowhead", "Folder2" })
                    Ck("folder ok: " + good, FolderProblem(good) == null, FolderProblem(good));
                foreach (var bad in new[] { "", "..", ".", "..\\..\\Documents", "../x", "a/b", "a\\b", "C:\\Users", "C:", "\\\\server\\share", "/etc", "x..y", ".hidden", "name.", "name ", " name", "CON", "con.txt", "NUL", "LPT1", "com9.lua", "a:b", "a*b", "a?b", "a|b", "a<b", "tab\tname", "evil\u202Efdp", new string('a', 65) })
                    Ck("folder refused: " + Show(bad), FolderProblem(bad) != null);
                Ck("folder at the length cap ok", FolderProblem(new string('a', 64)) == null);
                foreach (var good in new[] { "ElansHub", "elans-hub_2.1", "RbTestA1b2c3" }) Ck("id ok: " + good, IdProblem(good) == null, IdProblem(good));
                foreach (var bad in new[] { "", "..", "a/b", "a\\b", ".x", "x.", "a..b", "CON", "id with space", "C:x", new string('a', 65) }) Ck("id refused: " + Show(bad), IdProblem(bad) != null);
                foreach (var good in new[] { null, "", "_retail_", "_classic_", "_classic_era_", "_classic_beta_", "_classic_ptr_", "_forever_" }) Ck("flavor ok: " + (good ?? "null"), FlavorProblem(good) == null);
                foreach (var bad in new[] { "retail", "_re/tail_", "..\\x", "_a__b_", "_Retail_", "_" + new string('a', 40) + "_" }) Ck("flavor refused: " + Show(bad), FlavorProblem(bad) != null);

                var root = Path.Combine(Path.GetTempPath(), "wow-root-for-test");
                var backups = Path.Combine(Path.GetTempPath(), "wow-backups-for-test");
                AddonInfo A(string id, string flavor, params string[] folders) => new AddonInfo { Id = id, Flavor = flavor, Folders = folders.ToList() };
                Ck("a normal addon passes", AddonProblem(root, backups, A("ElansHub", "_classic_", "ElansHub", "ElansHub_Options")) == null);
                Ck("a traversal in folders is refused", AddonProblem(root, backups, A("ElansHub", "_classic_", "ElansHub", "..\\..\\Documents")) != null);
                Ck("an absolute folder is refused", AddonProblem(root, backups, A("ElansHub", "_classic_", "C:\\Users\\x")) != null);
                Ck("a bad id is refused", AddonProblem(root, backups, A("..\\..\\x", "_classic_", "ElansHub")) != null);
                Ck("a bad flavor is refused", AddonProblem(root, backups, A("ElansHub", "..\\..", "ElansHub")) != null);
                Ck("no folders is refused", AddonProblem(root, backups, A("ElansHub", "_classic_")) != null);
                Ck("the message names the culprit", (AddonProblem(root, backups, A("ElansHub", "_classic_", "..\\..\\Documents")) ?? "").Contains("Documents"));
                Ck("containment: inside", IsInside(root, Path.Combine(root, "a", "b")));
                Ck("containment: the folder itself is not inside", !IsInside(root, root));
                Ck("containment: a sibling with the same prefix is outside", !IsInside(root, root + "-other"));
                Ck("containment: .. leaves", !IsInside(root, Path.Combine(root, "..", "x")));
                Ck("containment: another drive / unc is outside", !IsInside(root, "\\\\server\\share\\x"));
            }
            catch (Exception e) { sb.AppendLine("pathsafety test: FAIL " + e); }
            return sb.ToString().TrimEnd();
        }
    }
}
