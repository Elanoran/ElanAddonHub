using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
        public CfAddon Cf;                       // set when the CurseForge app manages this addon (the hub then never touches it)
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
        static readonly string[] AllSuffixes = { "Vanilla", "Classic", "Era", "TBC", "BCC", "Burning", "Wrath", "WOTLKC", "Wotlk", "Cata", "Cataclysm", "Mists", "MoP", "Mainline", "Forever", "Camelot" };

        // 16001 -> 1 (classic era family), 20505 -> 2, 110002 -> 99 (retail)
        // WoW Forever (client 1.60.x "Camelot", Interface 16001): its own flavor, not Classic Era (11xxx)
        public static bool IsForever(int iface) => iface / 1000 == 16;

        public static int Major(int iface) => iface >= 100000 ? 99 : iface / 10000;

        public static string[] Suffixes(int iface)
        {
            var l = new List<string>();
            if (IsForever(iface)) { l.AddRange(new[] { "Camelot", "Forever" }); return l.ToArray(); }   // WoW Forever is its own flavor: never the Classic/Era tocs
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

        public static List<AddonEntry> Scan(string addOnsDir, int clientInterface, ICollection<string> skipFolders, out int detectedInterface, AddonSuggest.Index wowi = null)
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
            GroupFolders(tocs, names, Union, wowi);

            foreach (var grp in names.GroupBy(Root, StringComparer.OrdinalIgnoreCase))
            {
                var members = grp.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                // primary: the folder the others depend on most, else a folder that is a prefix of the rest, else the shortest name
                var primary = members.OrderByDescending(m => members.All(o => o == m || PrefixOf(m, o)) ? 1 : 0)
                    .ThenByDescending(m => members.Count(o => Deps(tocs[o]).Contains(m, StringComparer.OrdinalIgnoreCase)))
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

        // ------------------------------------------------------------------ self-test (synthetic AddOns folder, release.json, no network)
        public static string SelfTest()
        {
            var sb = new StringBuilder();
            bool ok = true;
            void Check(string what, bool cond, string info = null) { sb.AppendLine($"scanner {what}: {(cond ? "ok" : "FAIL")}{(info == null ? "" : " (" + info + ")")}"); ok &= cond; }
            var root = Path.Combine(Path.GetTempPath(), "ehh-grouptest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                void Toc(string folder, string text, string suffix = "") { var d = Path.Combine(root, folder); Directory.CreateDirectory(d); File.WriteAllText(Path.Combine(d, folder + suffix + ".toc"), text); }
                // Details + plugins: plugins have no Author/Version, different Interface lines
                Toc("Details", "## Interface: 12000\n## Title: Details! Damage Meter\n## Version: #D.1\n");
                Toc("Details", "## Interface: 16001\n## Title: Details! Damage Meter\n## Version: #D.2\n", "_Camelot");
                Toc("Details_Vanguard", "## Interface: 30405\n## Title: Details!: Vanguard (plugin)\n## RequiredDeps: Details\n");
                Toc("Details_Streamer", "## Interface: 30405\n## Title: Details!: Streamer\n## RequiredDeps: Details\n");
                // Questie + QuestieDB: no separator, joined by RequiredDeps
                Toc("Questie", "## Interface: 16001\n## Title: Questie v1\n## Version: 12.0\n## RequiredDeps: QuestieDB\n");
                Toc("QuestieDB", "## Interface: 16001\n## Title: QuestieDB\n## Version: 1.0\n");
                // DBM-*: no parent folder, same author
                Toc("DBM-Core", "## Interface: 16001\n## Title: Deadly Boss Mods\n## Author: Tandanu\n## Version: 1.1\n");
                Toc("DBM-GUI", "## Interface: 16001\n## Title: DBM GUI\n## Author: Tandanu\n## Version: 1.2\n");
                Toc("DBM-Naxx", "## Interface: 16001\n## Title: DBM Naxx\n");
                // ElvUI + Libraries via X-Part-Of
                Toc("ElvUI", "## Interface: 16001\n## Title: ElvUI\n## Author: Elv\n");
                Toc("Weirdname", "## Interface: 16001\n## Title: Config\n## X-Part-Of: ElvUI\n");
                // shared libraries and unrelated addons stay separate
                Toc("LibStub", "## Interface: 16001\n## Title: LibStub\n");
                Toc("Ace3", "## Interface: 16001\n## Title: Ace3\n## Author: Ace\n");
                Toc("!BugGrabber", "## Interface: 16001\n## Title: BugGrabber\n");
                Toc("Atlas", "## Interface: 16001\n## Title: Atlas\n## Author: A\n## RequiredDeps: LibStub\n");
                Toc("AtlasLoot", "## Interface: 16001\n## Title: AtlasLoot\n## Author: B\n## RequiredDeps: LibStub\n");
                Toc("Bagnon", "## Interface: 16001\n## Title: Bagnon\n## Author: C\n## RequiredDeps: LibStub, Ace3\n");
                Toc("Bagnon_Config", "## Interface: 16001\n## Title: Bagnon Config\n## Author: C\n");
                Toc("Other_One", "## Interface: 16001\n## Title: One\n## Author: X\n");
                Toc("Other_Two", "## Interface: 16001\n## Title: Two\n## Author: Y\n");
                // WoWInterface bundle: two folders with no hint of their own
                Toc("Zorp", "## Interface: 16001\n## Title: Zorp\n");
                Toc("Blat", "## Interface: 16001\n## Title: Blat\n");

                string G(List<AddonEntry> l, string key) { var e = l.FirstOrDefault(x => x.Folders.Contains(key, StringComparer.OrdinalIgnoreCase)); return e == null ? "?" : string.Join("+", e.Folders); }
                var list = Scan(root, 16001, null, out _);
                Check("Details + plugins", G(list, "Details") == "Details+Details_Streamer+Details_Vanguard", G(list, "Details"));
                Check("Forever toc picked (_Camelot)", list.First(x => x.Key == "Details").Version == "#D.2", list.First(x => x.Key == "Details").TocName);
                Check("Questie + QuestieDB (RequiredDeps, no separator)", G(list, "Questie") == "Questie+QuestieDB" && list.First(x => x.Folders.Contains("Questie")).Key == "Questie", G(list, "Questie"));
                Check("DBM-* siblings (author / title word, module without author)", G(list, "DBM-Core") == "DBM-Core+DBM-GUI+DBM-Naxx", G(list, "DBM-Core"));
                Check("X-Part-Of", G(list, "ElvUI") == "ElvUI+Weirdname", G(list, "ElvUI"));
                Check("libraries stay separate", G(list, "LibStub") == "LibStub" && G(list, "Ace3") == "Ace3" && G(list, "!BugGrabber") == "!BugGrabber");
                Check("Atlas / AtlasLoot not merged (shared library only)", G(list, "Atlas") == "Atlas" && G(list, "AtlasLoot") == "AtlasLoot");
                Check("Bagnon + Bagnon_Config", G(list, "Bagnon") == "Bagnon+Bagnon_Config", G(list, "Bagnon"));
                Check("same prefix, different authors stay apart", G(list, "Other_One") == "Other_One" && G(list, "Other_Two") == "Other_Two");
                Check("unlinked pair stays apart without a bundle", G(list, "Zorp") == "Zorp");
                var ix = AddonSuggest.BuildIndex(AddonSuggest.ParseFilelist("[{\"UID\":\"9\",\"UIName\":\"Zorp Suite\",\"UIVersion\":\"1\",\"UIDate\":1,\"UIAuthorName\":\"q\",\"UICompatibility\":[],\"UIDir\":[\"Zorp\",\"Blat\",\"Missing\"]}]"));
                var list2 = Scan(root, 16001, null, out _, ix);
                Check("WoWInterface folder set bundles Zorp + Blat", G(list2, "Zorp") == "Blat+Zorp" || G(list2, "Zorp") == "Zorp+Blat", G(list2, "Zorp"));

                // toc suffix order: Forever/Camelot, never Classic/Vanilla
                var sfx = string.Join(",", Suffixes(16001));
                Check("suffixes for WoW Forever", sfx == "Camelot,Forever", sfx);
                Directory.CreateDirectory(Path.Combine(root, "Onlyclassic"));
                File.WriteAllText(Path.Combine(root, "Onlyclassic", "Onlyclassic_Vanilla.toc"), "## Interface: 11507\n");
                File.WriteAllText(Path.Combine(root, "Onlyclassic", "Onlyclassic.toc"), "## Interface: 16001\n");
                Check("plain toc beats a Vanilla toc", Path.GetFileName(PickToc(Path.Combine(root, "Onlyclassic"), 16001)) == "Onlyclassic.toc");
            }
            catch (Exception e) { sb.AppendLine("scanner selftest crashed: " + e); ok = false; }
            finally { try { Directory.Delete(root, true); } catch { } }

            // release.json: Forever vs Classic assets
            string RJ(params string[] rel) => "{\"releases\":[" + string.Join(",", rel) + "]}";
            string R(string file, string flavor, int iface, bool nolib = false) => "{\"filename\":\"" + file + "\",\"nolib\":" + (nolib ? "true" : "false") + ",\"metadata\":[{\"flavor\":\"" + flavor + "\",\"interface\":" + iface + "}]}";
            var zips = new List<string> { "A-classic.zip", "A-forever.zip", "A-mainline.zip" };
            var pick = AddonSources.PickFromReleaseJson(RJ(R("A-classic.zip", "classic", 11507), R("A-forever.zip", "forever", 16001), R("A-mainline.zip", "mainline", 120100)), 16001, zips);
            void Chk(string what, bool cond, string info) { sb.AppendLine($"asset {what}: {(cond ? "ok" : "FAIL")} ({info})"); ok &= cond; }
            Chk("Forever asset chosen over Classic", pick == "A-forever.zip", pick);
            var pick2 = AddonSources.PickFromReleaseJson(RJ(R("A-classic.zip", "classic", 11507), R("A-mainline.zip", "mainline", 120100)), 16001, zips);
            Chk("only Classic available -> no automatic pick", pick2 == null, pick2);
            var pick3 = AddonSources.PickFromReleaseJson(RJ(R("A-classic.zip", "classic", 11507), R("A-forever.zip", "classic", 16001)), 16001, zips);
            Chk("interface 16001 matches by number", pick3 == "A-forever.zip", pick3);
            Chk("by name: -forever picked", AddonSources.PickAssetByName(new List<string> { "A-classic.zip", "A-forever.zip" }, 16001) == "A-forever.zip", "");
            Chk("by name: only classic -> manual", AddonSources.PickAssetByName(new List<string> { "A-classic.zip", "A-mainline.zip" }, 16001) == null, "");
            Chk("by name: camelot counts as Forever", AddonSources.PickAssetByName(new List<string> { "A-classic.zip", "A-camelot.zip" }, 16001) == "A-camelot.zip", "");
            Chk("classic asset flagged for warning", AddonSources.IsClassicAsset("A-classic.zip") && !AddonSources.IsClassicAsset("A-forever.zip"), "");
            sb.AppendLine("scanner/asset selftest " + (ok ? "PASSED" : "FAILED"));
            return sb.ToString();
        }

        // ------------------------------------------------------------------ grouping
        // Shared libraries (LibStub, Ace3, !BugGrabber, CallbackHandler-1.0 ...) are used by many addons: never a group's parent or child.
        public static bool IsLibraryName(string n) =>
            Regex.IsMatch(n ?? "", @"^(!|Lib[A-Z0-9_-]|Lib$|Ace[A-Z0-9]|Ace$|CallbackHandler|HereBeDragons|oUF|LibStub)|-\d+\.\d+$", RegexOptions.None);

        static string Auth(TocInfo t) => StripColors(t.Get("Author") ?? "").Trim();
        static string Ver(TocInfo t) => (t.Get("Version") ?? "").Trim();
        static bool AuthorConflict(TocInfo a, TocInfo b)
        {
            string x = Auth(a).ToLowerInvariant(), y = Auth(b).ToLowerInvariant();
            return x != "" && y != "" && x != y && !x.Contains(y) && !y.Contains(x);
        }
        static string TitleWord(TocInfo t)
        {
            var m = Regex.Match(StripColors(t.Get("Title") ?? "").ToLowerInvariant(), @"[a-z0-9]+");
            return m.Success && m.Value.Length >= 4 ? m.Value : "";
        }
        // "child" is "parent" + separator or CamelCase continuation (Foo_Options, Foo-Bar, FooDB)
        static bool PrefixOf(string parent, string child) =>
            child.Length > parent.Length && child.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
            && (child[parent.Length] == '_' || child[parent.Length] == '-' || char.IsUpper(child[parent.Length]) || char.IsDigit(child[parent.Length]));
        static bool SepPrefixOf(string parent, string child) =>
            child.Length > parent.Length && child.StartsWith(parent, StringComparison.OrdinalIgnoreCase) && (child[parent.Length] == '_' || child[parent.Length] == '-');

        static List<string> Parts(TocInfo t)
        {
            var l = new List<string>();
            foreach (var k in new[] { "X-Part-Of", "X-Child-Of", "X-Parent" })
                if (t.Get(k) is string s) l.AddRange(s.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries));
            return l;
        }

        static void GroupFolders(Dictionary<string, TocInfo> tocs, List<string> names, Action<string, string> union, AddonSuggest.Index wowi)
        {
            bool Has(string n) => n != null && tocs.ContainsKey(n);
            bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            string Fam(string n) { var i = n.IndexOfAny(new[] { '_', '-' }); return (i >= 3 ? n.Substring(0, i) : n); }

            // a folder that three or more unrelated addons depend on is a shared library, not a parent
            var fanIn = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in names)
                foreach (var d in Deps(tocs[n]))
                    if (Has(d) && !Eq(Fam(d), Fam(n)) && !PrefixOf(d, n))
                    {
                        if (!fanIn.TryGetValue(d, out var set)) fanIn[d] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        set.Add(Fam(n));
                    }
            bool Lib(string n) => IsLibraryName(n) || (fanIn.TryGetValue(n, out var s) && s.Count >= 3);

            // one more independent hint that two folders are the same addon
            bool Corroborated(string child, string parentName, bool allowCamel)
            {
                var c = tocs[child]; var p = tocs[parentName];
                if (AuthorConflict(c, p)) return false;
                if (SepPrefixOf(parentName, child) || (allowCamel && PrefixOf(parentName, child))) return true;
                if (Ver(c) != "" && Ver(c) == Ver(p)) return true;
                if (Auth(c) != "" && Eq(Auth(c), Auth(p))) return true;
                var tw = TitleWord(c);
                return tw != "" && tw == TitleWord(p);
            }

            foreach (var n in names)
            {
                var t = tocs[n];
                // explicit: "## X-Part-Of: Foo" / "## X-Child-Of: Foo"
                foreach (var d in Parts(t)) if (Has(d) && !Eq(d, n) && !Lib(d)) union(n, d);
                // a hard dependency (Dependencies / RequiredDeps / LoadWith) that is installed, not a library, and plausibly the same addon
                if (!Lib(n))
                    foreach (var d in Deps(t))
                    {
                        if (!Has(d) || Eq(d, n) || Lib(d)) continue;
                        if (PrefixOf(d, n) || PrefixOf(n, d) || Corroborated(n, d, true) || Corroborated(d, n, true)) union(n, d);   // a name prefix + a hard dependency is enough, even if the author credits differ
                    }
                foreach (var m in names)
                {
                    if (string.Compare(n, m, StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    var o = tocs[m];
                    // same project id on a site / same GitHub repo
                    if (Same(t.Get("X-Curse-Project-ID"), o.Get("X-Curse-Project-ID")) || Same(t.Get("X-WoWI-ID"), o.Get("X-WoWI-ID")) || Same(t.Get("X-Wago-ID"), o.Get("X-Wago-ID"))
                        || (FindGithub(t) is string g1 && Eq(g1, FindGithub(o)) && !Lib(n) && !Lib(m)))
                        union(n, m);
                }
                // Foo_Options / Foo-Bar next to a folder called Foo (versions may differ, modules may lack Version/Author)
                var cut = n.IndexOfAny(new[] { '_', '-' });
                if (cut >= 3 && !Lib(n))
                {
                    var prefix = n.Substring(0, cut);
                    if (Has(prefix) && !Lib(prefix) && !AuthorConflict(t, tocs[prefix])) union(n, prefix);
                }
            }
            // DBM-Core / DBM-GUI ...: same prefix, no parent folder; needs one more hint (same author, version or title word)
            foreach (var g in names.Where(n => !Lib(n) && n.IndexOfAny(new[] { '_', '-' }) >= 3)
                .GroupBy(Fam, StringComparer.OrdinalIgnoreCase))
            {
                var list = g.ToList();
                for (int i = 0; i < list.Count; i++)
                    for (int j = i + 1; j < list.Count; j++)
                    {
                        var a = tocs[list[i]]; var b = tocs[list[j]];
                        if (AuthorConflict(a, b)) continue;
                        bool dep = Deps(a).Contains(list[j], StringComparer.OrdinalIgnoreCase) || Deps(b).Contains(list[i], StringComparer.OrdinalIgnoreCase);
                        bool famTitle = AddonSuggest.Norm(g.Key).Length >= 3 && AddonSuggest.Norm(a.Get("Title")).StartsWith(AddonSuggest.Norm(g.Key)) && AddonSuggest.Norm(b.Get("Title")).StartsWith(AddonSuggest.Norm(g.Key));
                        bool same = famTitle || (Ver(a) != "" && Ver(a) == Ver(b)) || (Auth(a) != "" && Eq(Auth(a), Auth(b))) || (TitleWord(a) != "" && TitleWord(a) == TitleWord(b));
                        if (dep || same) union(list[i], list[j]);
                    }
            }
            // a WoWInterface file that ships several of the installed folders (libraries only when it ships them too)
            if (wowi != null)
            {
                var seen = new HashSet<string>();
                foreach (var n in names)
                    if (wowi.ByDir.TryGetValue(n, out var files))
                        foreach (var f in files)
                        {
                            if (f.Dirs == null || f.Dirs.Count > 25 || !seen.Add(f.Id)) continue;
                            var have = f.Dirs.Where(Has).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                            if (have.Count < 2) continue;
                            var fname = AddonSuggest.Norm(f.Name);
                            // the file must be about one of these folders (not a compilation pack of unrelated addons)
                            if (!have.Any(h => AddonSuggest.Norm(h).Length >= 4 && (fname.Length > 0 && (fname.Contains(AddonSuggest.Norm(h)) || AddonSuggest.Norm(h).Contains(fname))))) continue;
                            for (int i = 1; i < have.Count; i++) union(have[0], have[i]);
                        }
            }
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
