using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace ElansAddonHub.Services
{
    // One installed addon as shown on a card: one or more folders that belong together.
    public class AddonEntry
    {
        public string Key;                       // primary folder name (also the key for saved links)
        public List<string> Folders = new List<string>();
        public string Title, Version, Author, Notes, Interface;
        public string CurseId, WowiId, WagoId, Website, GithubRepo;   // GithubRepo = "owner/repo" from the toc
        public bool IsDev;                       // a folder holds .git: a development copy, never updated
        public DateTime Modified;                // newest toc write time of the primary folder (UTC)
        public string TocName;                   // which toc file was read
    }

    public class TocInfo
    {
        public string Folder, File;
        public Dictionary<string, string> Tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string Get(string k) => Tags.TryGetValue(k, out var v) ? v : null;
        public DateTime Modified;
    }

    // Reads Interface\AddOns: every .toc, the one matching the client flavor, and groups multi-folder addons.
    public static class AddonScanner
    {
        static readonly string[] AllSuffixes = { "Vanilla", "Classic", "Era", "TBC", "BCC", "Burning", "Wrath", "WOTLKC", "Wotlk", "Cata", "Cataclysm", "Mists", "MoP", "Mainline", "Forever" };

        // 16001 -> 1 (classic era family), 20505 -> 2, 110002 -> 99 (retail)
        public static int Major(int iface) => iface >= 100000 ? 99 : iface / 10000;

        public static string[] Suffixes(int iface)
        {
            var l = new List<string>();
            if (iface / 1000 == 16) l.Add("Forever");
            switch (Major(iface))
            {
                case 1: l.AddRange(new[] { "Vanilla", "Classic", "Era" }); break;
                case 2: l.AddRange(new[] { "TBC", "BCC", "Burning" }); break;
                case 3: l.AddRange(new[] { "Wrath", "WOTLKC", "Wotlk", "Classic" }); break;
                case 4: l.AddRange(new[] { "Cata", "Cataclysm", "Classic" }); break;
                case 5: l.AddRange(new[] { "Mists", "MoP", "Classic" }); break;
                case 99: l.Add("Mainline"); break;
            }
            return l.ToArray();
        }

        public static string StripColors(string s)
        {
            if (s == null) return null;
            s = Regex.Replace(s, @"\|c[0-9a-fA-F]{8}", "");
            s = Regex.Replace(s, @"\|T.*?\|t", "", RegexOptions.IgnoreCase);
            s = s.Replace("|r", "").Replace("|R", "").Replace("|n", " ");
            return Regex.Replace(s, @"\s+", " ").Trim();
        }

        public static TocInfo ParseToc(string file)
        {
            var t = new TocInfo { File = file, Folder = Path.GetFileName(Path.GetDirectoryName(file)) };
            try { t.Modified = File.GetLastWriteTimeUtc(file); } catch { }
            foreach (var raw in File.ReadAllLines(file))
            {
                var line = raw.TrimStart('﻿');
                var m = Regex.Match(line, @"^##\s*([^:]+?)\s*:\s*(.*?)\s*$");
                if (m.Success && !t.Tags.ContainsKey(m.Groups[1].Value)) t.Tags[m.Groups[1].Value] = m.Groups[2].Value;
            }
            return t;
        }

        // the toc to read for one folder: flavor-specific for this client first, else the plain one
        public static string PickToc(string folderPath, int clientInterface)
        {
            var name = Path.GetFileName(folderPath);
            string[] tocs;
            try { tocs = Directory.GetFiles(folderPath, "*.toc"); } catch { return null; }
            if (tocs.Length == 0) return null;
            string Find(string fileName) => tocs.FirstOrDefault(f => string.Equals(Path.GetFileName(f), fileName, StringComparison.OrdinalIgnoreCase));
            foreach (var suf in Suffixes(clientInterface))
            {
                var hit = Find($"{name}_{suf}.toc") ?? Find($"{name}-{suf}.toc");
                if (hit != null) return hit;
            }
            var plain = Find(name + ".toc");
            if (plain != null) return plain;
            // a toc for another flavor only: not for this client, but better than nothing
            bool IsFlavored(string f) => AllSuffixes.Any(s => f.EndsWith("_" + s + ".toc", StringComparison.OrdinalIgnoreCase) || f.EndsWith("-" + s + ".toc", StringComparison.OrdinalIgnoreCase));
            return tocs.FirstOrDefault(f => !IsFlavored(f)) ?? tocs[0];
        }

        // the client's Interface number: from our own addons (known to be right for this client), else the most common one
        public static int ClientInterface(string addOnsDir, IEnumerable<string> ownFolders, IEnumerable<TocInfo> tocs)
        {
            foreach (var f in ownFolders ?? Enumerable.Empty<string>())
            {
                var dir = Path.Combine(addOnsDir, f);
                if (!Directory.Exists(dir)) continue;
                foreach (var toc in Directory.GetFiles(dir, "*.toc").OrderBy(x => x.Length))
                {
                    var n = FirstInterface(ParseToc(toc).Get("Interface"));
                    if (n > 0) return n;
                }
            }
            var mode = tocs.Select(t => FirstInterface(t.Get("Interface"))).Where(n => n > 0)
                .GroupBy(n => n).OrderByDescending(g => g.Count()).FirstOrDefault();
            return mode?.Key ?? 0;
        }

        public static int FirstInterface(string s) => AllInterfaces(s).FirstOrDefault();

        public static List<int> AllInterfaces(string s)
        {
            var l = new List<int>();
            if (string.IsNullOrEmpty(s)) return l;
            foreach (Match m in Regex.Matches(s, @"\d{4,6}")) if (int.TryParse(m.Value, out var n)) l.Add(n);
            return l;
        }

        public static List<AddonEntry> Scan(string addOnsDir, int clientInterface, ICollection<string> skipFolders, out int detectedInterface)
        {
            detectedInterface = clientInterface;
            var result = new List<AddonEntry>();
            if (!Directory.Exists(addOnsDir)) return result;
            var tocs = new Dictionary<string, TocInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in Directory.GetDirectories(addOnsDir))
            {
                var name = Path.GetFileName(dir);
                if (name.StartsWith(".") || name.StartsWith("Blizzard_", StringComparison.OrdinalIgnoreCase)) continue;
                if (skipFolders != null && skipFolders.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                try
                {
                    var toc = PickToc(dir, clientInterface);
                    if (toc != null) tocs[name] = ParseToc(toc);
                }
                catch (Exception e) { Util.Log("toc read failed in " + name + ": " + e.Message); }
            }
            if (clientInterface == 0)
            {
                // our own addons weren't passed or aren't installed: guess from everything, then re-pick flavor tocs
                clientInterface = ClientInterface(addOnsDir, null, tocs.Values);
                detectedInterface = clientInterface;
                if (clientInterface != 0)
                    foreach (var k in tocs.Keys.ToList())
                    {
                        var toc = PickToc(Path.Combine(addOnsDir, k), clientInterface);
                        if (toc != null && !string.Equals(toc, tocs[k].File, StringComparison.OrdinalIgnoreCase)) tocs[k] = ParseToc(toc);
                    }
            }

            // ---- group folders that belong together (union-find)
            var names = tocs.Keys.ToList();
            var parent = names.ToDictionary(n => n, n => n, StringComparer.OrdinalIgnoreCase);
            string Root(string n) { while (!string.Equals(parent[n], n, StringComparison.OrdinalIgnoreCase)) n = parent[n] = parent[parent[n]]; return n; }
            void Union(string a, string b) { a = Root(a); b = Root(b); if (!string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) parent[b] = a; }
            string Ver(TocInfo t) => (t.Get("Version") ?? "").Trim();
            string Auth(TocInfo t) => (t.Get("Author") ?? "").Trim();
            // same author (or one unknown) and same version (or one unknown)
            bool Alike(TocInfo a, TocInfo b) =>
                (Auth(a) == "" || Auth(b) == "" || string.Equals(Auth(a), Auth(b), StringComparison.OrdinalIgnoreCase)) &&
                (Ver(a) == "" || Ver(b) == "" || Ver(a) == Ver(b));

            foreach (var n in names)
            {
                var t = tocs[n];
                // 1. a required dependency that is installed and alike
                foreach (var d in Deps(t))
                    if (tocs.TryGetValue(d, out var dt) && !string.Equals(d, n, StringComparison.OrdinalIgnoreCase) && Alike(t, dt) && Auth(t) != "")
                        Union(n, dt.Folder);
                // 2. same project id on a site
                foreach (var m in names)
                {
                    if (string.Compare(n, m, StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    var o = tocs[m];
                    if (Same(t.Get("X-Curse-Project-ID"), o.Get("X-Curse-Project-ID")) || Same(t.Get("X-WoWI-ID"), o.Get("X-WoWI-ID")) || Same(t.Get("X-Wago-ID"), o.Get("X-Wago-ID")))
                        Union(n, m);
                }
                // 3. BigWigs_Core / BigWigs_Options next to a folder called BigWigs
                var cut = n.IndexOfAny(new[] { '_', '-' });
                if (cut >= 3)
                {
                    var prefix = n.Substring(0, cut);
                    if (tocs.TryGetValue(prefix, out var pt) && Alike(t, pt) && Auth(t) != "") Union(n, prefix);
                }
            }
            // 4. DBM-Core / DBM-GUI ...: same prefix, same non-empty author AND version, more than one folder
            foreach (var g in names.Where(n => n.IndexOfAny(new[] { '_', '-' }) >= 3)
                .GroupBy(n => n.Substring(0, n.IndexOfAny(new[] { '_', '-' })), StringComparer.OrdinalIgnoreCase))
            {
                var list = g.ToList();
                if (list.Count < 2) continue;
                foreach (var m in list.Skip(1))
                    if (Auth(tocs[list[0]]) != "" && Ver(tocs[list[0]]) != "" && Auth(tocs[list[0]]) == Auth(tocs[m]) && Ver(tocs[list[0]]) == Ver(tocs[m]))
                        Union(list[0], m);
            }

            foreach (var grp in names.GroupBy(Root, StringComparer.OrdinalIgnoreCase))
            {
                var members = grp.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                // primary: the folder the others depend on most, else a folder that is a prefix of the rest, else the shortest name
                var primary = members.OrderByDescending(m => members.Count(o => Deps(tocs[o]).Contains(m, StringComparer.OrdinalIgnoreCase)))
                    .ThenBy(m => m.Length).ThenBy(m => m, StringComparer.OrdinalIgnoreCase).First();
                var pt = tocs[primary];
                string Pick(Func<TocInfo, string> f) => f(pt) is string s && s.Length > 0 ? s : members.Select(m => f(tocs[m])).FirstOrDefault(x => !string.IsNullOrEmpty(x));
                var e = new AddonEntry
                {
                    Key = primary,
                    Folders = members,
                    Title = StripColors(Pick(t => t.Get("Title"))) ?? primary,
                    Version = Pick(t => t.Get("Version")),
                    Author = StripColors(Pick(t => t.Get("Author"))),
                    Notes = StripColors(Pick(t => t.Get("Notes"))),
                    Interface = Pick(t => t.Get("Interface")),
                    CurseId = Pick(t => t.Get("X-Curse-Project-ID")),
                    WowiId = Pick(t => t.Get("X-WoWI-ID")),
                    WagoId = Pick(t => t.Get("X-Wago-ID")),
                    Website = Pick(t => t.Get("X-Website")),
                    Modified = pt.Modified,
                    TocName = Path.GetFileName(pt.File),
                };
                if (string.IsNullOrEmpty(e.Title)) e.Title = primary;
                e.WowiId = Regex.Match(e.WowiId ?? "", @"\d+").Value;
                if (e.WowiId == "") e.WowiId = null;
                foreach (var m in members)
                {
                    var p = Path.Combine(addOnsDir, m, ".git");
                    if (Directory.Exists(p) || File.Exists(p)) e.IsDev = true;
                    e.GithubRepo = e.GithubRepo ?? FindGithub(tocs[m]);
                }
                result.Add(e);
            }
            return result.OrderBy(a => a.Title, StringComparer.OrdinalIgnoreCase).ToList();
        }

        static bool Same(string a, string b) => !string.IsNullOrWhiteSpace(a) && string.Equals(a.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

        static List<string> Deps(TocInfo t)
        {
            var l = new List<string>();
            foreach (var k in new[] { "Dependencies", "RequiredDeps", "Dep1", "Dep2", "Dep3" })
                if (t.Get(k) is string s) l.AddRange(s.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries));
            return l;
        }

        public static string ParseGithub(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var m = Regex.Match(text, @"github\.com[/:]([A-Za-z0-9_.-]+)/([A-Za-z0-9_.-]+)", RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            var repo = Regex.Replace(m.Groups[2].Value, @"\.git$", "");
            if (repo.Length == 0) return null;
            return m.Groups[1].Value + "/" + repo;
        }

        static string FindGithub(TocInfo t)
        {
            foreach (var k in new[] { "X-Website", "X-GitHub", "X-Github", "X-Source", "X-Repository", "X-Repo", "X-Project", "Website" })
                if (ParseGithub(t.Get(k)) is string r) return r;
            foreach (var v in t.Tags.Values) if (ParseGithub(v) is string r) return r;
            return null;
        }

        public static string ParseWowiId(string text)
        {
            var m = Regex.Match(text ?? "", @"wowinterface\.com/downloads/info(\d+)", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : null;
        }
    }
}
