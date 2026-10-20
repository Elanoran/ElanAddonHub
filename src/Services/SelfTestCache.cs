using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ElansAddonHub.Services
{
    // The "<name>real" sections run the checks that read the player's real WoW install (CASC, Map.db2, terrain, buildings ...). They are slow and
    // their result only depends on three things: the installed game build, the source files that read / parse / render game data, and the test files
    // themselves. SelfTestCache keys a passing run on exactly that, so the next run (same game build, same readers) can skip them:
    //   "worldmapreal: skipped (game 1.60.1.70374 and readers unchanged since a passing run on 2026-10-15)".
    // The cache lives outside the app's data folder (%LOCALAPPDATA%\ElansAddonHub-selftest-cache): the self-test never touches the real data folder.
    // A FAIL is never stored as a pass (it removes the entry); a new game patch changes the key and re-runs everything. Test-only code.
    public static class SelfTestCache
    {
        public sealed class Def
        {
            public string Name, Parent, Readers, Tests;
        }

        // Readers = repo-relative paths (forward slashes) that read, parse or render game data for that section; Tests = the section's own real-data test files.
        // The same patterns decide, in --changed mode, when the section runs (TestScope), so "what re-runs it" and "what invalidates the cache" cannot drift apart.
        const string Core = @"Services/(Casc|Blp|Db2/Wdc5|Maps/\w+)\.cs";
        const string Wm = @"Services/WorldMap/(?!WorldMapPage|WorldMapProtocol)\w+\.cs";
        public static readonly Def[] Defs =
        {
            new Def { Name = "mapdatareal", Parent = "mapdata", Readers = @"^src/(" + Core + @"|Services/Db2/\w+\.cs|Controls/AtlasMap\.cs)$", Tests = @"^src/MainWindow\.MapDataTest(\.Control|\.Real)?\.cs$" },
            new Def { Name = "worldmapreal", Parent = "worldmap", Readers = @"^src/(" + Core + "|" + Wm + @"|Services/Atlas/Classic/\w+\.cs)$", Tests = @"^src/MainWindow\.WorldMapTest\.(Real|Terrain|Water|Page3d|Islands)\.cs$" },
            new Def { Name = "instancesreal", Parent = "instances", Readers = @"^src/(" + Core + "|" + Wm + @"|Services/Atlas/(AtlasInstances|AtlasObs\.Entrances)\.cs)$", Tests = @"^src/MainWindow\.InstancesTest\.(Real|Audit)\.cs$" },
            new Def { Name = "buildingsreal", Parent = "buildings", Readers = @"^src/(" + Core + "|" + Wm + ")$", Tests = @"^src/MainWindow\.BuildingsTest\.Real\.cs$" },
            new Def { Name = "objectsreal", Parent = "objects", Readers = @"^src/(" + Core + "|" + Wm + ")$", Tests = @"^src/MainWindow\.ObjectsTest\.Real\.cs$" },
            new Def { Name = "dungeonmapsreal", Parent = "dungeonmaps", Readers = @"^src/(" + Core + "|" + Wm + @"|Services/DungeonMaps/\w+\.cs|Controls/AtlasMap\.\w+\.cs|Services/Atlas/(AtlasInstances|AtlasObs\.Entrances)\.cs)$", Tests = @"^src/MainWindow\.DungeonMapsTest\.Real\.cs$" },
            new Def { Name = "atlaspagereal", Parent = "atlaspage", Readers = @"^src/(" + Core + @"|Services/Db2/\w+\.cs|Services/Atlas/\w+(\.\w+)?\.cs|Services/Atlas/Classic/\w+\.cs|Services/(IconStore|IconSheet)\.cs|Controls/AtlasMap\.cs|Controls/GameTooltip\w*\.cs)$", Tests = @"^src/MainWindow\.AtlasPageTest\.Real\.cs$" },
            new Def { Name = "atlasdatareal", Parent = "atlasdata", Readers = @"^src/(Services/Atlas/(ObjectKinds|QuestChains|SeenView|AtlasBrowse\.Index)\.cs|Services/Atlas/Classic/\w+\.cs|Services/Db2/(WorldLookups|WdbSeen)\.cs)$", Tests = @"^src/MainWindow\.AtlasDataTest\.Real\.cs$" },
            new Def { Name = "atlashiddenreal", Parent = "atlashidden", Readers = @"^src/(Services/Atlas/AtlasHidden\.cs|Services/Db2/(SpellModel|SpellTables|ClassModel|ClassTables|ItemTables)\.cs)$", Tests = @"^src/MainWindow\.AtlasHiddenTest\.Real\.cs$" },
            new Def { Name = "zonelorereal", Parent = "zonelore", Readers = @"^src/(Services/Atlas/ZoneLore(\.\w+)?\.cs|Assets/Data/zonelore\.json)$", Tests = @"^src/MainWindow\.ZoneLoreTest\.Real\.cs$" },
        };

        public static Def Find(string name) => Defs.FirstOrDefault(d => d.Name == name);
        public static bool IsReal(string name) => Find(name) != null;

        // ---- the game build: "wow_classic_beta 1.60.1.70374 25822068" from .build.info (null when there is none)
        public static string GameStamp(string wowRoot, string product = "wow_classic_beta", string flavor = "_classic_beta_")
        {
            try
            {
                if (string.IsNullOrEmpty(wowRoot)) return null;
                var f = Path.Combine(wowRoot, ".build.info");
                if (!File.Exists(f)) return null;
                string text;
                using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) using (var sr = new StreamReader(fs)) text = sr.ReadToEnd();
                var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                var cols = lines[0].Split('|').Select(c => c.Split('!')[0]).ToList();
                int iB = cols.IndexOf("Build Key"), iV = cols.IndexOf("Version"), iP = cols.IndexOf("Product");
                if (iB < 0 || iV < 0 || iP < 0) return null;
                var row = lines.Skip(1).Select(l => l.Split('|')).FirstOrDefault(r => r.Length > Math.Max(iB, Math.Max(iV, iP)) && r[iP] == product);
                if (row == null) return null;
                // the beta client's own folder is part of the install too: a client that was removed is not "the same game"
                if (!string.IsNullOrEmpty(flavor) && !Directory.Exists(Path.Combine(wowRoot, flavor))) return null;
                return product + " " + row[iV] + " " + row[iB].Substring(0, Math.Min(8, row[iB].Length));
            }
            catch (Exception) { return null; }
        }

        public static string VersionOf(string stamp) { var p = (stamp ?? "").Split(' '); return p.Length > 1 ? p[1] : stamp ?? ""; }

        // ---- the key: game stamp + the content of every reader and test file of the section
        public static string Key(string repo, Def d, string stamp)
        {
            var files = FilesOf(repo, d);
            if (files == null) return null;
            return KeyOf(stamp, files.Select(f => Tuple.Create(f.Item1, Sha(f.Item2))));
        }

        public static string KeyOf(string stamp, IEnumerable<Tuple<string, string>> fileHashes)
        {
            var sb = new StringBuilder();
            sb.Append("game=").Append(stamp).Append('\n');
            foreach (var f in fileHashes.OrderBy(x => x.Item1, StringComparer.Ordinal)) sb.Append(f.Item1).Append('=').Append(f.Item2).Append('\n');
            return Sha(Encoding.UTF8.GetBytes(sb.ToString()));
        }

        static string Sha(byte[] b) { using (var h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(b)).Replace("-", "").ToLowerInvariant(); }

        // (repo-relative path, content) of the section's files, line endings normalised (a checkout with other autocrlf is the same source); null when the repo has no src folder
        static List<Tuple<string, byte[]>> FilesOf(string repo, Def d)
        {
            try
            {
                var src = Path.Combine(repo ?? "", "src");
                if (string.IsNullOrEmpty(repo) || !Directory.Exists(src)) return null;
                var rx = new Regex(d.Readers + "|" + d.Tests, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                var list = new List<Tuple<string, byte[]>>();
                foreach (var f in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
                {
                    var rel = "src/" + f.Substring(src.Length).TrimStart('\\', '/').Replace('\\', '/');
                    if (rel.StartsWith("src/bin/") || rel.StartsWith("src/obj/") || !rx.IsMatch(rel)) continue;
                    var bytes = File.ReadAllBytes(f);
                    list.Add(Tuple.Create(rel, Normalise(bytes)));
                }
                return list;
            }
            catch (Exception) { return null; }
        }

        static byte[] Normalise(byte[] b)
        {
            var o = new List<byte>(b.Length);
            for (int i = 0; i < b.Length; i++) if (!(b[i] == 13 && i + 1 < b.Length && b[i + 1] == 10)) o.Add(b[i]);
            return o.ToArray();
        }

        // ---- the store: section|key|result|stamp|date
        public sealed class Entry { public string Section, Key, Result, Stamp, Date; }

        public static string CacheDir()
        {
            var env = Environment.GetEnvironmentVariable("ELANSHUB_SELFTEST_CACHE");
            var dir = !string.IsNullOrEmpty(env) ? env : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ElansAddonHub-selftest-cache");
            // never inside the app's real data folder
            var real = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ElansAddonHub")).TrimEnd('\\') + "\\";
            if ((Path.GetFullPath(dir).TrimEnd('\\') + "\\").StartsWith(real, StringComparison.OrdinalIgnoreCase)) return null;
            return dir;
        }

        public static List<Entry> Load(string dir)
        {
            var r = new List<Entry>();
            try
            {
                var f = Path.Combine(dir ?? "", "realcache.txt");
                if (dir == null || !File.Exists(f)) return r;
                foreach (var l in File.ReadAllLines(f))
                {
                    var p = l.Split('|');
                    if (p.Length >= 5) r.Add(new Entry { Section = p[0], Key = p[1], Result = p[2], Stamp = p[3], Date = p[4] });
                }
            }
            catch (Exception) { }
            return r;
        }

        public static void Save(string dir, List<Entry> entries)
        {
            try
            {
                if (dir == null) return;
                Directory.CreateDirectory(dir);
                File.WriteAllLines(Path.Combine(dir, "realcache.txt"), entries.Select(e => string.Join("|", e.Section, e.Key, e.Result, e.Stamp, e.Date)));
            }
            catch (Exception) { }
        }

        // the decision: a message when the section can be skipped (same key, last result a pass), else null
        public static string Decide(List<Entry> entries, string section, string key, string stamp)
        {
            if (key == null || stamp == null) return null;
            var e = entries.FirstOrDefault(x => x.Section == section);
            if (e == null || e.Result != "pass" || e.Key != key) return null;
            return section + ": skipped (game " + VersionOf(stamp) + " and readers unchanged since a passing run on " + e.Date + ")";
        }

        // remember the outcome: a pass is stored with its key, anything else drops the entry (a failure is never a pass)
        public static void Record(List<Entry> entries, string section, string key, string stamp, bool pass, string date)
        {
            entries.RemoveAll(x => x.Section == section);
            if (pass && key != null && stamp != null) entries.Add(new Entry { Section = section, Key = key, Result = "pass", Stamp = stamp, Date = date });
        }

        // ---- the runner's side: skip decision before the section, recording after it
        public sealed class Gate
        {
            public bool Enabled; public string Repo, Stamp, Dir; public List<Entry> Entries = new List<Entry>();
            public string Key(string section) => Enabled ? SelfTestCache.Key(Repo, Find(section), Stamp) : null;

            // null = run the section; otherwise the line to print instead
            public string SkipLine(string section) => Enabled ? Decide(Entries, section, Key(section), Stamp) : null;

            // after a run: pass = no FAIL / crash in its lines and it really did read the game (a "skip real game files" note is no pass)
            public void After(string section, string output)
            {
                if (!Enabled) return;
                bool pass = !string.IsNullOrEmpty(output) && !output.Contains("FAIL") && !output.Contains("crashed") && !output.Contains("skip real game files");
                Record(Entries, section, Key(section), Stamp, pass, DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                Save(Dir, Entries);
            }
        }

        // --use-cache: set by the runner script for the default (--changed) mode and by -UseCache; --no-cache wins. Needs --repo (the source to hash) and a game stamp.
        public static Gate Open(string[] args)
        {
            var g = new Gate();
            if (!args.Contains("--use-cache") || args.Contains("--no-cache")) return g;
            g.Repo = TestScope.ArgValue(args, "--repo");
            var wow = Environment.GetEnvironmentVariable("ELANSHUB_WOW");
            if (string.IsNullOrEmpty(wow)) wow = WowLocator.Find();
            g.Stamp = GameStamp(wow);
            g.Dir = CacheDir();
            g.Entries = Load(g.Dir);
            g.Enabled = g.Repo != null && g.Stamp != null && g.Dir != null;
            return g;
        }

        // pure checks on a throw-away repo and game folder (no CASC, no UI); returns "ok ..." / "FAIL ..." lines
        public static string SelfTest(string dir)
        {
            var n = new List<string>(); bool ok = true;
            void Ck(string w, bool c) { n.Add((c ? "ok   " : "FAIL ") + w); ok &= c; }
            try
            {
                var root = Path.Combine(dir, "cachetest");
                if (Directory.Exists(root)) Util.DeleteDir(root);
                var repo = Path.Combine(root, "repo"); var wow = Path.Combine(root, "wow");
                Directory.CreateDirectory(Path.Combine(repo, "src", "Services", "Maps")); Directory.CreateDirectory(Path.Combine(repo, "src", "Services", "WorldMap")); Directory.CreateDirectory(Path.Combine(wow, "_classic_beta_"));
                File.WriteAllText(Path.Combine(repo, "src", "Services", "Casc.cs"), "class Casc {}\r\n");
                File.WriteAllText(Path.Combine(repo, "src", "Services", "Maps", "MapData.cs"), "class MapData {}\n");
                File.WriteAllText(Path.Combine(repo, "src", "Services", "WorldMap", "WdtFile.cs"), "class Wdt {}\n");
                File.WriteAllText(Path.Combine(repo, "src", "MainWindow.WorldMapTest.Terrain.cs"), "// real terrain checks\n");
                File.WriteAllText(Path.Combine(repo, "src", "AtlasPage.Nav.cs"), "// UI\n");
                void Build(string version) => File.WriteAllText(Path.Combine(wow, ".build.info"),
                    "Branch!STRING:0|Active!DEC:1|Build Key!HEX:16|Version!STRING:0|Product!STRING:0\r\nus|1|25822068f8fce8b21bc714e7e3210481|" + version + "|wow_classic_beta\r\neu|1|334a36cf5f3c32a5f7a167405487fa14|12.1.5.70391|wow\r\n");
                Build("1.60.1.70374");
                var d = Find("worldmapreal");
                var s1 = GameStamp(wow); var k1 = Key(repo, d, s1);
                Ck("game stamp: the beta product's version and build key from .build.info (" + s1 + ")", s1 != null && s1.StartsWith("wow_classic_beta 1.60.1.70374 25822068") && VersionOf(s1) == "1.60.1.70374");
                Ck("game stamp: no .build.info, or no beta client folder, gives no stamp (the cache stays off)", GameStamp(Path.Combine(root, "nowhere")) == null && GameStamp(wow, "wow_classic_beta", "_nope_") == null);
                var entries = new List<Entry>();
                Ck("first run: nothing cached -> the section runs", Decide(entries, d.Name, k1, s1) == null);
                Record(entries, d.Name, k1, s1, true, "2026-10-15");
                var msg = Decide(entries, d.Name, k1, s1);
                Ck("same key after a pass -> skipped, saying so (" + msg + ")", msg == "worldmapreal: skipped (game 1.60.1.70374 and readers unchanged since a passing run on 2026-10-15)");
                File.WriteAllText(Path.Combine(repo, "src", "AtlasPage.Nav.cs"), "// UI changed\n");
                Ck("a UI-only file changed -> same key, still skipped", Key(repo, d, s1) == k1 && Decide(entries, d.Name, Key(repo, d, s1), s1) != null);
                File.WriteAllText(Path.Combine(repo, "src", "Services", "Casc.cs"), "class Casc {}\n");
                Ck("line endings alone do not change the key", Key(repo, d, s1) == k1);
                Build("1.60.2.70500");
                var s2 = GameStamp(wow);
                Ck("a new game build (patch) -> new key -> runs again", s2 != s1 && Key(repo, d, s2) != k1 && Decide(entries, d.Name, Key(repo, d, s2), s2) == null);
                Build("1.60.1.70374");
                File.WriteAllText(Path.Combine(repo, "src", "Services", "WorldMap", "WdtFile.cs"), "class Wdt { int x; }\n");
                Ck("a reader file changed (WorldMap/WdtFile.cs) -> runs again", Decide(entries, d.Name, Key(repo, d, s1), s1) == null);
                File.WriteAllText(Path.Combine(repo, "src", "Services", "WorldMap", "WdtFile.cs"), "class Wdt {}\n");
                File.WriteAllText(Path.Combine(repo, "src", "MainWindow.WorldMapTest.Terrain.cs"), "// real terrain checks, edited\n");
                Ck("the section's own test file changed -> runs again", Decide(entries, d.Name, Key(repo, d, s1), s1) == null);
                File.WriteAllText(Path.Combine(repo, "src", "MainWindow.WorldMapTest.Terrain.cs"), "// real terrain checks\n");
                Ck("everything back as it was -> skipped again", Decide(entries, d.Name, Key(repo, d, s1), s1) != null);
                Record(entries, d.Name, Key(repo, d, s1), s1, false, "2026-10-16");
                Ck("a failing run is never stored as a pass: the next run executes", Decide(entries, d.Name, Key(repo, d, s1), s1) == null && entries.All(e => e.Result == "pass"));
                Record(entries, d.Name, Key(repo, d, s1), s1, true, "2026-10-17");
                var cdir = Path.Combine(root, "store");
                Save(cdir, entries); var back = Load(cdir);
                Ck("the cache file round-trips", back.Count == 1 && back[0].Key == entries[0].Key && Decide(back, d.Name, Key(repo, d, s1), s1) != null);
                var other = Find("buildingsreal");
                Ck("another section's entry does not skip this one", Decide(back, other.Name, Key(repo, other, s1), s1) == null);
                var realDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ElansAddonHub", "x");
                var envWas = Environment.GetEnvironmentVariable("ELANSHUB_SELFTEST_CACHE");
                Environment.SetEnvironmentVariable("ELANSHUB_SELFTEST_CACHE", realDir);
                var refused = CacheDir() == null;
                Environment.SetEnvironmentVariable("ELANSHUB_SELFTEST_CACHE", envWas);
                Ck("a cache folder inside the app's real data folder is refused", refused);
                var gate = new Gate { Enabled = true, Repo = repo, Stamp = s1, Dir = cdir, Entries = new List<Entry>() };
                gate.After("worldmapreal", "worldmapreal: ok\r\nok   a\r\nskip real game files: no WoW install");
                Ck("a section that skipped for lack of a game is no pass", gate.SkipLine("worldmapreal") == null);
                gate.After("worldmapreal", "ok   a\r\nFAIL b");
                Ck("a failing line makes it no pass", gate.SkipLine("worldmapreal") == null);
                gate.After("worldmapreal", "ok   a\r\nok   b");
                Ck("a clean run is a pass: the gate then skips it, and says so", (gate.SkipLine("worldmapreal") ?? "").StartsWith("worldmapreal: skipped (game 1.60.1.70374"));
                Ck("every real section has a parent section and a readers and tests pattern that compile", Defs.All(x => TestScope.All.Contains(x.Parent) && TestScope.All.Contains(x.Name) && new Regex(x.Readers) != null && new Regex(x.Tests) != null));
            }
            catch (Exception e) { Ck("cache self-test crashed: " + e.Message, false); }
            return (ok ? "selftest cache: ok" : "selftest cache: FAILED") + "\r\n" + string.Join("\r\n", n);
        }
    }
}
