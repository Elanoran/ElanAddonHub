using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Elans.Shared
{
    // What a shared file marked "addon" must be (Lodge server 2.32, feature "filesaddons"): a zip of one or more WoW addon folders, the
    // way CurseForge and the addons' own releases ship them. The Lodge server checks it on upload; the Outpost checks it again on the
    // member's PC before anything goes near AddOns - the mark is a promise, never trusted alone.
    //   * every file lies inside a top-level folder (no loose files), no unsafe path (.., rooted, drive, alternate stream), no program or
    //     script (DangerousFiles), at most MaxEntries entries and MaxUnpackedBytes unpacked (a zip bomb is refused before unpacking);
    //   * every top-level folder is a plain addon folder name and holds its own .toc directly inside: "<Folder>.toc" or a flavour toc such as
    //     "<Folder>_Vanilla.toc" / "<Folder>-Classic.toc" (what WoW itself accepts);
    //   * Version: the "## Version:" of each folder's main toc, for the confirm text ("" when it has none).
    // Tests: the Outpost's --selftest lodgefiles (and the Lodge server's FilesAddonTests).
    public static class AddonZip
    {
        public const int MaxEntries = 20000;
        public const long MaxUnpackedBytes = 500L * 1024 * 1024;
        public const int MaxFolders = 40;

        public sealed class Result
        {
            public string Problem;                                   // null = an addon zip
            public List<string> Folders = new List<string>();        // the top-level folders, in zip order
            public Dictionary<string, string> Versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, FolderInfo> Info = new Dictionary<string, FolderInfo>(StringComparer.OrdinalIgnoreCase);   // 2.35: what each folder's main toc says
            public bool Ok => Problem == null;
        }

        // 2.35: from a folder's main toc - "## Version:", the "## Interface:" numbers (several when one toc serves several games), "## Dependencies:" /
        // "## RequiredDeps:" (the addons it needs; OptionalDeps are not needed)
        public sealed class FolderInfo
        {
            public string Folder = "", Version = "";
            public List<int> Interfaces = new List<int>();
            public List<string> Deps = new List<string>();
        }

        // the Interface number of a game version: 1.60.1 -> 16001 (major * 10000 + minor * 100 + patch); 0 when it can't be read
        public static int InterfaceOf(string version)
        {
            var p = (version ?? "").Split('.');
            if (p.Length < 3 || !int.TryParse(p[0], out var a) || !int.TryParse(p[1], out var b) || !int.TryParse(p[2], out var c) || a < 0 || b < 0 || b > 99 || c < 0 || c > 99) return 0;
            return a * 10000 + b * 100 + c;
        }

        // a folder name WoW loads and Windows can hold: letters, digits, space, _ - . ! ( ) +, not only dots, no trailing dot or space
        public static bool SafeFolder(string f)
        {
            if (string.IsNullOrEmpty(f) || f.Length > 64 || f.Trim('.') .Length == 0 || f.EndsWith(".") || f.EndsWith(" ") || f.StartsWith(" ")) return false;
            foreach (var c in f) if (!(char.IsLetterOrDigit(c) || " _-.!()+".IndexOf(c) >= 0) || c > 127) return false;
            var up = f.ToUpperInvariant();
            var stem = up.Split('.')[0];
            return !(stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3])));
        }

        // "<Folder>.toc", "<Folder>_Vanilla.toc", "<Folder>-Classic.toc" directly in the folder
        public static bool IsTocOf(string folder, string fileName)
        {
            if (!fileName.EndsWith(".toc", StringComparison.OrdinalIgnoreCase)) return false;
            var stem = fileName.Substring(0, fileName.Length - 4);
            if (string.Equals(stem, folder, StringComparison.OrdinalIgnoreCase)) return true;
            return stem.Length > folder.Length + 1 && stem.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && (stem[folder.Length] == '_' || stem[folder.Length] == '-');
        }

        public static Result Check(string zipPath)
        {
            try { using (var fs = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read)) return Check(fs); }
            catch (IOException) { return new Result { Problem = "The file couldn't be read" }; }
        }

        public static Result Check(Stream seekable)
        {
            var r = new Result();
            try
            {
                using (var zip = new ZipArchive(seekable, ZipArchiveMode.Read, leaveOpen: true))
                {
                    if (zip.Entries.Count > MaxEntries) return Fail(r, "The zip has too many entries");
                    long total = 0;
                    var tocs = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
                    foreach (var e in zip.Entries)
                    {
                        var name = e.FullName.Replace('\\', '/');
                        var parts = name.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                        if (name.StartsWith("/") || name.Contains(":") || name.Contains("\0") || parts.Any(p => p == ".." || p == ".")) return Fail(r, "The zip contains an unsafe path");
                        if (parts.Length == 0) continue;
                        bool dir = name.EndsWith("/");
                        if (parts.Length == 1 && !dir) return Fail(r, "The zip has files outside an addon folder (" + parts[0] + ") - an addon zip holds only addon folders");
                        if (!SafeFolder(parts[0])) return Fail(r, "\"" + parts[0] + "\" isn't a usable addon folder name");
                        if (!r.Folders.Contains(parts[0], StringComparer.OrdinalIgnoreCase))
                        {
                            if (r.Folders.Count >= MaxFolders) return Fail(r, "The zip has more than " + MaxFolders + " folders");
                            r.Folders.Add(parts[0]);
                        }
                        if (dir) continue;
                        if (DangerousFiles.IsDangerous(e.Name)) return Fail(r, "The zip contains a program or script file (." + DangerousFiles.Extension(e.Name) + ")");
                        total += e.Length;
                        if (e.Length < 0 || total > MaxUnpackedBytes) return Fail(r, "The zip unpacks to more than " + (MaxUnpackedBytes / (1024 * 1024)) + " MB");
                        if (parts.Length == 2 && IsTocOf(parts[0], parts[1]))
                        {
                            // the main toc ("<Folder>.toc") wins over a flavour one for the version shown
                            if (!tocs.ContainsKey(parts[0]) || string.Equals(parts[1], parts[0] + ".toc", StringComparison.OrdinalIgnoreCase)) tocs[parts[0]] = e;
                        }
                    }
                    if (r.Folders.Count == 0) return Fail(r, "The zip is empty");
                    var noToc = r.Folders.Where(f => !tocs.ContainsKey(f)).ToList();
                    if (noToc.Count > 0) return Fail(r, "Not an addon: " + string.Join(", ", noToc.Take(3)) + (noToc.Count == 1 ? " has" : " have") + " no .toc file of " + (noToc.Count == 1 ? "its" : "their") + " own");
                    foreach (var kv in tocs)
                    {
                        var info = TocInfo(kv.Value);
                        info.Folder = kv.Key;
                        r.Info[kv.Key] = info;
                        r.Versions[kv.Key] = info.Version;
                    }
                    return r;
                }
            }
            catch (Exception e) when (e is InvalidDataException || e is NotSupportedException || e is IOException)
            {
                return Fail(r, "That is not a valid zip file");
            }
        }

        static Result Fail(Result r, string why) { r.Problem = why; r.Folders.Clear(); r.Versions.Clear(); r.Info.Clear(); return r; }

        // Version / Interface / Dependencies from the first 64 KB of a toc (values capped: a toc is the uploader's text)
        static FolderInfo TocInfo(ZipArchiveEntry e)
        {
            var info = new FolderInfo();
            try
            {
                if (e.Length > 1024 * 1024) return info;
                using (var s = e.Open())
                using (var rd = new StreamReader(s, Encoding.UTF8))
                {
                    var buf = new char[65536];
                    int n = rd.ReadBlock(buf, 0, buf.Length);
                    foreach (var line in new string(buf, 0, n).Split('\n'))
                    {
                        var l = line.Trim();
                        string Val(string key) => l.StartsWith(key, StringComparison.OrdinalIgnoreCase) ? l.Substring(key.Length).Trim() : null;
                        if (Val("## Version:") is string v)
                        {
                            if (info.Version.Length == 0) info.Version = v.Length > 40 ? v.Substring(0, 40) : v;
                        }
                        else if (Val("## Interface:") is string iface)
                        {
                            if (info.Interfaces.Count == 0)
                                foreach (var part in iface.Split(','))
                                    if (int.TryParse(part.Trim(), out var iv) && iv > 0 && iv < 1000000 && info.Interfaces.Count < 8) info.Interfaces.Add(iv);
                        }
                        else if ((Val("## Dependencies:") ?? Val("## RequiredDeps:") ?? Val("## Dep:")) is string deps)
                        {
                            foreach (var part in deps.Split(','))
                            {
                                var dn = part.Trim();
                                if (SafeFolder(dn) && info.Deps.Count < 20 && !info.Deps.Contains(dn, StringComparer.OrdinalIgnoreCase)) info.Deps.Add(dn);
                            }
                        }
                    }
                }
            }
            catch { }
            return info;
        }
    }
}
