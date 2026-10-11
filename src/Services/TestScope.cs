using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ElansAddonHub.Services
{
    // Which parts of --selftest to run. The sections, their dependencies and the path -> section table all live here, so
    // adding a self-test block means touching this file plus the driver in MainWindow.SelfTest.
    //   --only a,b      run just those sections (plus whatever they depend on)
    //   --changed base  run the sections the files changed since <base> can affect (git diff base...HEAD + uncommitted)
    //   --repo dir      where to run git for --changed (default: the current directory)
    //   --list          print the section names
    // No flag: everything, exactly as before. Anything unknown or shared maps to ALL: safe rather than clever.
    public static class TestScope
    {
        // execution order == order of the full run
        public static readonly string[] All =
        {
            "addons", "thirdparty", "viewer", "voice", "update", "strip",
            "lodge", "lodgefeatures", "video", "presence", "profile", "toasts", "dialogs", "inventory", "health", "round3",
        };

        // section -> sections that must also run because they read state the other one leaves behind. Everything else a section
        // needs (window shown, fixture, first update check, isolation check) is shared setup that runs for every selection.
        // Verified by running each section alone and comparing its lines with the full run.
        static readonly Dictionary<string, string[]> Deps = new Dictionary<string, string[]>
        {
        };

        // one line of what each section covers (for --list and the docs)
        public static readonly Dictionary<string, string> Info = new Dictionary<string, string>
        {
            ["addons"] = "install + check-all + pills + settings tour (Hunter Helper cards)",
            ["thirdparty"] = "other addons: scan, suggest, CurseForge, zip install/rollback, real GitHub/WoWI lookups",
            ["viewer"] = "in-app picture viewer",
            ["voice"] = "voice effects",
            ["update"] = "update channel + installer",
            ["strip"] = "pixel strip",
            ["lodge"] = "live Lodge session (only with ELANSHUB_TEST_LODGE=url|code|name)",
            ["lodgefeatures"] = "Lodge chat features from synthetic frames",
            ["video"] = "YouTube preview cards + in-app player",
            ["presence"] = "member presence",
            ["profile"] = "profiles and the editor",
            ["toasts"] = "toast notifications",
            ["dialogs"] = "themed dialogs",
            ["inventory"] = "Inventory page + Bags",
            ["health"] = "addon health",
            ["round3"] = "settings round 3 (WoW watch, lodge update, welcome, ...)",
        };

        const string Star = "*";

        // path (repo-relative, forward slashes) -> sections. All matching rules are unioned; a file no rule matches means ALL.
        // "*" = ALL (shared by everything), no sections = nothing the self-test exercises.
        static readonly List<Tuple<Regex, string[]>> Rules = BuildRules();

        static List<Tuple<Regex, string[]>> BuildRules()
        {
            var r = new List<Tuple<Regex, string[]>>();
            void R(string rx, params string[] s) => r.Add(Tuple.Create(new Regex(rx, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), s));
            var lodgeAll = new[] { "lodge", "lodgefeatures", "video", "viewer", "toasts", "presence", "profile", "round3" };

            // ---- shared: everything depends on these
            R(@"^src/(App\.xaml(\.cs)?|MainWindow\.xaml(\.cs)?|MainWindow\.Others\.cs|MainWindow\.Welcome\.cs|ElansAddonHub\.csproj|Motion\.cs|Brand\.cs|SourceIcons\.cs|PillBase\.cs|WelcomeGuide\.cs)$", Star);
            R(@"^src/Theme/", Star);
            R(@"^src/Services/(Util|SettingsStore|Models|Json|Net|SelfTestFixture|FakeUpdateServer|TestScope|WowLocator|Splash|SplashScene)\.cs$", Star);
            R(@"^src/Assets/", Star);
            // ---- inventory
            R(@"^src/InventoryPage", "inventory");
            R(@"^src/Services/(InventoryData|IconStore|IconSheet|Blp|Casc|CharPortrait|LuaData|ZoneNames)\.cs$", "inventory");
            R(@"^src/MainWindow\.(InventoryTest|DesignTest\.Inventory)\.cs$", "inventory");
            // ---- lodge: chat, voice, files, profiles, toasts, presence, viewer
            R(@"^src/Lodge/", lodgeAll);
            R(@"^src/Services/(LodgeClient|AvatarArt|GamePresence)\.cs$", lodgeAll);
            R(@"^src/Services/(VoiceEngine|VoiceEffects)\.cs$", "voice", "lodge");
            R(@"^src/Services/Sounds\.cs$", "toasts", "lodge", "voice");
            R(@"^src/SettingsPage", "addons", "profile", "round3", "voice", "lodge");
            R(@"^src/MainWindow\.ProfileTest\.cs$", "profile");
            R(@"^src/MainWindow\.VideoTest\.cs$", "video");
            R(@"^src/MainWindow\.PresenceTest\.cs$", "presence");
            R(@"^src/MainWindow\.ToastTest\.cs$", "toasts");
            R(@"^src/MainWindow\.DesignTest(\.Toasts)?\.cs$");     // screenshot-only design runs, not part of --selftest
            R(@"^server/", "lodge", "lodgefeatures", "presence", "profile");
            // ---- addons
            R(@"^src/(AddonCard|ThirdPartyCard)\.cs$", "addons", "thirdparty");
            R(@"^src/Services/(AddonScanner|AddonSuggest|AddonSources|CurseForgeLocal|GitHubStats)\.cs$", "thirdparty", "addons");
            R(@"^src/Services/(Installer|SelfUpdater)\.cs$", "update", "addons");
            R(@"^src/Services/UpdateChannel\.cs$", "update", "round3");
            R(@"^addon/", "addons");
            // ---- health, dialogs, strip
            R(@"^src/(HealthDialog\.cs|MainWindow\.Health(Test)?\.cs)$", "health");
            R(@"^src/Services/(AddonHealth|HealthMonitor|WowWatch)\.cs$", "health", "round3");
            R(@"^src/MainWindow\.Round3Test\.cs$", "round3");
            R(@"^src/ThemedDialog", "dialogs");
            R(@"^src/Services/(StripSelfTest|PixelStrip)\.cs$", "strip");
            // ---- not exercised by the self-test
            R(@"^(docs/|README\.md$|DESIGN\.md$|\.gitignore$|\.gitattributes$|src/CHANGELOG\.md$|Tools/)");
            return r;
        }

        // sections affected by the changed files, with the reason per file; unknown/shared files make it ALL
        public static List<string> FromPaths(IEnumerable<string> files, out List<string> why)
        {
            why = new List<string>();
            var set = new HashSet<string>();
            bool any = false;
            foreach (var raw in files ?? Enumerable.Empty<string>())
            {
                var f = (raw ?? "").Trim().Replace('\\', '/');
                if (f.Length == 0) continue;
                any = true;
                var hit = Rules.Where(x => x.Item1.IsMatch(f)).ToList();
                if (hit.Count == 0) { why.Add(f + " -> ALL (unknown file)"); set.UnionWith(All); continue; }
                var secs = new HashSet<string>(hit.SelectMany(x => x.Item2));
                if (secs.Contains(Star)) { why.Add(f + " -> ALL (shared)"); set.UnionWith(All); continue; }
                why.Add(f + " -> " + (secs.Count == 0 ? "nothing (not self-tested)" : string.Join(",", Order(secs))));
                set.UnionWith(secs);
            }
            if (!any) why.Add("no changed files");
            return Closure(set);
        }

        static IEnumerable<string> Order(IEnumerable<string> s) => All.Where(a => s.Contains(a));

        // the selection plus everything it depends on, in run order
        public static List<string> Closure(IEnumerable<string> picked)
        {
            var set = new HashSet<string>(picked ?? Enumerable.Empty<string>());
            for (bool grew = true; grew;)
            {
                grew = false;
                foreach (var s in set.ToList())
                    if (Deps.TryGetValue(s, out var d))
                        foreach (var x in d) if (set.Add(x)) grew = true;
            }
            return Order(set).ToList();
        }

        // --only a,b,c -> sections; unknown names are returned so the caller can fail loudly instead of silently running nothing
        public static List<string> ParseOnly(string csv, out List<string> unknown)
        {
            var names = (csv ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim().ToLowerInvariant()).ToList();
            unknown = names.Where(n => n != "all" && !All.Contains(n)).ToList();
            if (names.Contains("all")) return All.ToList();
            return Closure(names.Where(n => All.Contains(n)));
        }

        // the chosen sections for this process (no flag = everything); `explain` goes into result.txt, `error` means run nothing
        public static List<string> FromArgs(string[] args, out string explain, out string error)
        {
            explain = "scope: all sections (no --only / --changed)"; error = null;
            string only = ArgValue(args, "--only"), changed = ArgValue(args, "--changed");
            if (only == null && changed == null) return All.ToList();
            var picked = new HashSet<string>();
            var sb = new StringBuilder();
            if (only != null)
            {
                var s = ParseOnly(only, out var unknown);
                if (unknown.Count > 0) { error = "unknown section(s): " + string.Join(", ", unknown) + " (valid: " + string.Join(", ", All) + ")"; return new List<string>(); }
                picked.UnionWith(s);
                sb.AppendLine("scope --only " + only + " -> " + string.Join(",", s));
            }
            if (changed != null)
            {
                var files = GitChanged(changed, ArgValue(args, "--repo") ?? Directory.GetCurrentDirectory(), out var gitErr);
                if (files == null) { picked.UnionWith(All); sb.AppendLine("scope --changed " + changed + " -> ALL (git failed: " + gitErr + ")"); }
                else
                {
                    var s = FromPaths(files, out var why);
                    picked.UnionWith(s);
                    sb.AppendLine("scope --changed " + changed + ": " + files.Count + " changed file(s) -> " + (s.Count == All.Length ? "ALL" : s.Count == 0 ? "no sections" : string.Join(",", s)));
                    foreach (var w in why) sb.AppendLine("  " + w);
                }
            }
            explain = sb.ToString().TrimEnd();
            return Closure(picked);
        }

        public static string ArgValue(string[] args, string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[i + 1] : null;
        }

        // git diff --name-only <base>...HEAD + uncommitted tracked changes + untracked files; null if git is not usable.
        // (diff paths are repo-root relative; ls-files needs --full-name for that)
        public static List<string> GitChanged(string baseRef, string repo, out string error)
        {
            error = null;
            var all = new List<string>();
            var bs = Git("rev-parse --verify --quiet \"" + baseRef + "^{commit}\"", repo, out error);
            if (bs == null || bs.Count == 0) { error = error ?? "unknown ref " + baseRef; return null; }
            var hd = Git("rev-parse HEAD", repo, out error);
            if (hd == null || hd.Count == 0) return null;
            var cmds = new List<string>();
            if (bs[0] != hd[0]) cmds.Add("diff --name-only " + baseRef + "...HEAD");   // base == HEAD: only uncommitted work
            cmds.Add("diff --name-only HEAD");
            cmds.Add("ls-files --others --exclude-standard --full-name");
            foreach (var a in cmds)
            {
                var o = Git(a, repo, out error);
                if (o == null) return null;
                all.AddRange(o);
            }
            return all.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct().ToList();
        }

        static List<string> Git(string args, string repo, out string error)
        {
            error = null;
            try
            {
                var psi = new ProcessStartInfo("git", args) { WorkingDirectory = repo, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                using (var p = Process.Start(psi))
                {
                    var err = p.StandardError.ReadToEndAsync();
                    var o = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(30000)) { error = "git timed out"; return null; }
                    if (p.ExitCode != 0) { error = "git " + args + ": " + err.Result.Trim(); return null; }
                    return o.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                }
            }
            catch (Exception e) { error = e.Message; return null; }
        }

        // pure-function checks (no git, no UI)
        public static string SelfTest()
        {
            var n = new List<string>(); bool ok = true;
            void Ck(string w, bool c) { n.Add((c ? "ok   " : "FAIL ") + w); ok &= c; }
            bool Same(IEnumerable<string> a, params string[] b) => a.SequenceEqual(b);
            List<string> Map(params string[] f) => FromPaths(f, out _);
            Ck("inventory files -> inventory only", Same(Map("src/InventoryPage.xaml.cs", "src/Services/InventoryData.cs", "src/Services/IconStore.cs"), "inventory"));
            Ck("backslash paths are normalised", Same(Map("src\\InventoryPage.Bags.cs"), "inventory"));
            var lodge = Map("src/Lodge/LodgeView.xaml.cs");
            Ck("Lodge files -> lodge, viewer, toasts, presence (not inventory)", new[] { "lodge", "viewer", "toasts", "presence" }.All(lodge.Contains) && !lodge.Contains("inventory"));
            Ck("Theme and Motion -> ALL", Map("src/Theme/Theme.xaml").Count == All.Length && Map("src/Motion.cs").Count == All.Length);
            Ck("shared files -> ALL", new[] { "src/App.xaml.cs", "src/MainWindow.xaml.cs", "src/ElansAddonHub.csproj", "src/Services/Util.cs", "src/Services/SettingsStore.cs" }.All(f => Map(f).Count == All.Length));
            Ck("unknown file -> ALL", Map("src/Brand_new_file.cs").Count == All.Length && Map("somewhere/else.bin").Count == All.Length);
            Ck("one shared file among specific ones -> ALL", Map("src/InventoryPage.xaml", "src/Motion.cs").Count == All.Length);
            Ck("docs only -> nothing", Map("README.md", "docs/design/x.png", "src/CHANGELOG.md").Count == 0);
            Ck("no changes -> nothing", Map().Count == 0);
            Ck("results are in run order", Map("src/InventoryPage.xaml", "src/HealthDialog.cs", "src/ThemedDialog.xaml").SequenceEqual(All.Where(a => a == "dialogs" || a == "inventory" || a == "health")));
            Ck("every rule section is a real section", Rules.SelectMany(r => r.Item2).All(s => s == Star || All.Contains(s)));
            Ck("every dependency is a real section", Deps.All(d => All.Contains(d.Key) && d.Value.All(All.Contains)));
            Ck("every section has a description", All.All(Info.ContainsKey));
            // closure through the same code path, with synthetic edges
            var saved = Deps.ToDictionary(k => k.Key, k => k.Value);
            try
            {
                Deps["toasts"] = new[] { "presence" }; Deps["presence"] = new[] { "profile" };
                Ck("dependency closure is transitive and in run order", Same(Closure(new[] { "toasts" }), "presence", "profile", "toasts"));
                Ck("closure of nothing is nothing", Closure(new string[0]).Count == 0);
            }
            finally { Deps.Clear(); foreach (var k in saved) Deps[k.Key] = k.Value; }
            var only = ParseOnly("Inventory, toasts", out var unk);
            Ck("--only parses names case-insensitively", Same(only, "toasts", "inventory") && unk.Count == 0);
            ParseOnly("inventory,bogus", out unk);
            Ck("--only reports unknown names", Same(unk, "bogus"));
            Ck("--only all -> everything", ParseOnly("all", out _).Count == All.Length);
            Ck("no flags -> everything", FromArgs(new[] { "x.exe", "--selftest", "d" }, out _, out _).Count == All.Length);
            var bad = FromArgs(new[] { "--only", "nope" }, out _, out var err);
            Ck("unknown --only -> error and nothing runs", bad.Count == 0 && err != null);
            var fb = FromArgs(new[] { "--changed", "no-such-ref-xyz", "--repo", Path.GetTempPath() }, out var ex, out _);
            Ck("git unusable -> falls back to ALL", fb.Count == All.Length && ex.Contains("ALL"));
            return "scope selftest: " + (ok ? "PASSED" : "FAILED") + "\r\n  " + string.Join("\r\n  ", n);
        }
    }
}
