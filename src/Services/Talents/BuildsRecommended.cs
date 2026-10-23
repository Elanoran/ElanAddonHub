using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ElansAddonHub.Services.Talents
{
    // A recommended build: a name, some words, and the ORDER the points are spent in (one point per level from the first talent level).
    public sealed class RecBuild
    {
        public string Id, Name, Icon, Desc, Stats, Tip, Source = "", ClassFile = "";
        public List<string> Roles = new List<string>(), Pets = new List<string>();
        public int VerifiedTo;
        public List<KeyValuePair<string, int>> Order = new List<KeyValuePair<string, int>>();    // ("Deadly Aspects", 5)
        public int Points => Order.Sum(o => o.Value);
    }

    public sealed class BuildStep
    {
        public int Number;              // 1-based point number
        public int Level;               // the level that point arrives at
        public string Name;
        public int Rank, Max, NodeId;   // rank after this point
        public bool Applied;
        public string Problem;
        public string Text => "level " + Level + ": " + Name + " " + Rank + "/" + Max;
    }

    public sealed class BuildApplication
    {
        public TalentState State = new TalentState();
        public List<BuildStep> Steps = new List<BuildStep>();
        public List<string> Unmatched = new List<string>();      // talent names of the build that are not in this tree
        public int Blocked;                                      // points that the rules would not allow at that moment
        public int Points => State.Total;
    }

    // Hunter Helper's own build list (Data\Builds.lua in the installed addon, read-only), and how any build is laid onto a tree.
    public static class BuildsRecommended
    {
        public const string HunterHelperRelative = @"Interface\AddOns\ElansHunterHelper\Data\Builds.lua";

        // <WowRoot>\<flavor>\Interface\AddOns\ElansHunterHelper\Data\Builds.lua
        public static string HunterHelperFile(string wowRoot, string flavor)
        {
            if (string.IsNullOrEmpty(wowRoot)) return null;
            try { return Path.Combine(wowRoot, WowLocator.Resolve(wowRoot, string.IsNullOrEmpty(flavor) ? "_classic_beta_" : flavor), HunterHelperRelative); } catch { return null; }
        }

        // the builds of the file, or an empty list (and `problem`) when it is not there / not readable. The file is only read, shared, and capped at 512 KB.
        public static List<RecBuild> ReadHunterHelper(string path, out string problem)
        {
            problem = null;
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) { problem = "Hunter Helper is not installed (no Builds.lua found)."; return new List<RecBuild>(); }
                string text;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (fs.Length > 512 * 1024) { problem = "Hunter Helper's build list is too big to be real."; return new List<RecBuild>(); }
                    using (var sr = new StreamReader(fs, new UTF8Encoding(false))) text = sr.ReadToEnd();
                }
                return ParseHunterHelper(text, out problem);
            }
            catch (Exception e) { problem = "Hunter Helper's build list could not be read (" + e.GetType().Name + ")."; return new List<RecBuild>(); }
        }

        // text of Builds.lua: `EHH.Builds = { { id = "...", name = ..., order = { { "Talent", 5 }, ... } }, ... }`
        public static List<RecBuild> ParseHunterHelper(string text, out string problem)
        {
            problem = null;
            var res = new List<RecBuild>();
            int at = text.IndexOf("EHH.Builds", StringComparison.Ordinal);
            int open = at < 0 ? -1 : text.IndexOf('{', at);
            if (open < 0) { problem = "Hunter Helper's build list has a layout this Outpost does not know."; return res; }
            int close = MatchBrace(text, open);
            if (close < 0) { problem = "Hunter Helper's build list is cut off."; return res; }
            Dictionary<string, object> g;
            try { g = LuaData.ReadGlobals("Builds = " + text.Substring(open, close - open + 1)); }
            catch (Exception) { problem = "Hunter Helper's build list could not be understood."; return res; }
            if (!g.TryGetValue("Builds", out var o) || !(o is Dictionary<string, object> list)) { problem = "Hunter Helper's build list is empty."; return res; }
            res = ParseList(list, "Hunter Helper", "HUNTER");
            if (res.Count == 0) problem = "Hunter Helper's build list has no builds.";
            return res;
        }

        // ---- 3.2: the Outpost's own builds for the classes without a helper addon (Assets\Data\talentbuilds.lua, embedded "Data.talentbuilds.lua":
        // Builds = { WARRIOR = { <builds as in Hunter Helper's file> }, ... }). Read once; the talentsreal self-test checks every build on the game's trees.
        public const string BuiltInResource = "Data.talentbuilds.lua", BuiltInSource = "Elan's Outpost";
        static Dictionary<string, object> builtIn;
        public static Func<string> BuiltInTextForTest;
        public static void ResetBuiltInForTest() { builtIn = null; }

        public static List<RecBuild> BuiltIn(string classFile, out string problem)
        {
            problem = null;
            var file = (classFile ?? "").ToUpperInvariant();
            try
            {
                if (builtIn == null)
                {
                    string text = BuiltInTextForTest?.Invoke();
                    if (text == null)
                        using (var s = typeof(BuildsRecommended).Assembly.GetManifestResourceStream(BuiltInResource))
                        {
                            if (s == null) { problem = "The Outpost's build list is not in this build."; return new List<RecBuild>(); }
                            using (var r = new StreamReader(s, new UTF8Encoding(false))) text = r.ReadToEnd();
                        }
                    var g = LuaData.ReadGlobals(text);
                    builtIn = g.TryGetValue("Builds", out var o) && o is Dictionary<string, object> d ? d : new Dictionary<string, object>();
                }
                return builtIn.TryGetValue(file, out var c) && c is Dictionary<string, object> list ? ParseList(list, BuiltInSource, file) : new List<RecBuild>();
            }
            catch (Exception e) { problem = "The Outpost's build list could not be read (" + e.GetType().Name + ")."; return new List<RecBuild>(); }
        }

        // the builds of one list (Hunter Helper's file, or one class of the Outpost's)
        static List<RecBuild> ParseList(Dictionary<string, object> list, string source, string classFile)
        {
            var res = new List<RecBuild>();
            foreach (var kv in list.OrderBy(k => int.TryParse(k.Key, out var n) ? n : int.MaxValue))
            {
                if (!(kv.Value is Dictionary<string, object> b)) continue;
                var name = Clean(b.Str("name"), 60);
                if (name == null) continue;
                var rb = new RecBuild
                {
                    Id = Clean(b.Str("id"), 40) ?? name, Name = name, Icon = Clean(b.Str("icon"), 80), Desc = Clean(b.Str("desc"), 600), Stats = Clean(b.Str("stats"), 200), Tip = Clean(b.Str("tip"), 400),
                    VerifiedTo = Math.Max(0, Math.Min(80, b.Int("verifiedTo"))), Source = source, ClassFile = classFile,
                };
                foreach (var r in Strings(b.Child("roles"))) rb.Roles.Add(r);
                foreach (var p in Strings(b.Child("pets"))) rb.Pets.Add(p);
                var order = b.Child("order");
                if (order != null)
                    foreach (var step in order.OrderBy(k => int.TryParse(k.Key, out var n) ? n : int.MaxValue))
                    {
                        if (!(step.Value is Dictionary<string, object> s)) continue;
                        var tn = Clean(s.Text("1"), 60);
                        if (tn == null || !int.TryParse(s.Text("2") ?? "", NumberStyles.Float, CultureInfo.InvariantCulture, out var ranks)) continue;
                        if (ranks < 1 || ranks > 5) continue;
                        if (rb.Order.Count >= 120) break;
                        rb.Order.Add(new KeyValuePair<string, int>(tn, ranks));
                    }
                if (rb.Order.Count > 0) res.Add(rb);
            }
            return res;
        }

        static IEnumerable<string> Strings(Dictionary<string, object> t)
        {
            if (t == null) yield break;
            foreach (var kv in t.OrderBy(k => int.TryParse(k.Key, out var n) ? n : int.MaxValue))
                if (kv.Value is string s) { var c = Clean(s, 40); if (c != null) yield return c; }
        }

        static string Clean(string s, int max)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var sb = new StringBuilder();
            foreach (var c in s) if (c >= ' ' && c != '\u007f') sb.Append(c);
            var t = sb.ToString().Trim();
            return t.Length == 0 ? null : t.Length > max ? t.Substring(0, max) : t;
        }

        // index of the brace that closes the one at `open` (strings and -- comments skipped), or -1
        static int MatchBrace(string s, int open)
        {
            int depth = 0;
            for (int i = open; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"' || c == '\'') { char q = c; i++; while (i < s.Length && s[i] != q) { if (s[i] == '\\') i++; i++; } continue; }
                if (c == '-' && i + 1 < s.Length && s[i + 1] == '-') { while (i < s.Length && s[i] != '\n') i++; continue; }
                if (c == '{') depth++;
                else if (c == '}') { depth--; if (depth == 0) return i; }
            }
            return -1;
        }

        // ---- laying a build onto a tree
        public static string Key(string name)
        {
            if (name == null) return "";
            var sb = new StringBuilder();
            foreach (var c in name) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        // name -> node of the tree (a node is known by its talent's name: `nameOf` gives the display name of a node, or null)
        public static Dictionary<string, TalentNode> NameIndex(TalentTreeDef tree, Func<TalentNode, string> nameOf)
        {
            var d = new Dictionary<string, TalentNode>();
            foreach (var n in tree.Nodes) { var k = Key(nameOf(n)); if (k.Length > 0 && !d.ContainsKey(k)) d[k] = n; }
            return d;
        }

        // Spends the build's points in its own order, one at a time, the way the game would allow it: a point the rules refuse at that moment (gate not open
        // yet, prerequisite missing, rank already full) is skipped and counted as Blocked. Names the tree does not have are listed in Unmatched.
        public static BuildApplication Apply(TalentTreeDef tree, RecBuild build, Func<TalentNode, string> nameOf, int legacyRanks)
        {
            var app = new BuildApplication();
            var idx = NameIndex(tree, nameOf);
            var levels = TalentRules.PointLevels(tree, legacyRanks);
            int number = 0;
            foreach (var step in build.Order)
            {
                if (!idx.TryGetValue(Key(step.Key), out var node))
                {
                    if (!app.Unmatched.Contains(step.Key)) app.Unmatched.Add(step.Key);
                    continue;
                }
                for (int i = 0; i < step.Value; i++)
                {
                    var why = TalentRules.Add(tree, app.State, node.Id, int.MaxValue);
                    if (why != Why.Ok)
                    {
                        app.Blocked++;
                        app.Steps.Add(new BuildStep { Number = ++number, Level = 0, Name = step.Key, Rank = app.State.Rank(node.Id), Max = node.MaxRanks, NodeId = node.Id, Applied = false, Problem = TalentRules.Explain(why) });
                        continue;
                    }
                    number++;
                    int placed = app.State.Total;
                    app.Steps.Add(new BuildStep { Number = number, Level = placed - 1 < levels.Count ? levels[placed - 1] : 0, Name = step.Key, Rank = app.State.Rank(node.Id), Max = node.MaxRanks, NodeId = node.Id, Applied = true });
                }
            }
            return app;
        }

        // "Deadly Aspects 1-5, Focused Fire 1-2 ..." grouped for a short line: consecutive points of one talent are one item with a level range
        public static List<string> StepLines(BuildApplication app) => StepGroups(app).Select(g => g.Text).ToList();

        public static List<StepGroup> StepGroups(BuildApplication app)
        {
            var groups = new List<StepGroup>();
            BuildStep first = null, last = null;
            int at = 0, firstAt = 0;
            void Flush()
            {
                if (first == null) return;
                var rank = first.Rank == last.Rank ? first.Rank + "/" + first.Max : first.Rank + "-" + last.Rank + "/" + first.Max;
                groups.Add(new StepGroup { Text = (first.Level == last.Level ? "level " + first.Level : "levels " + first.Level + "-" + last.Level) + ": " + first.Name + " " + rank, First = firstAt, Last = at - 1, NodeId = first.NodeId });
                first = last = null;
            }
            foreach (var s in app.Steps.Where(s => s.Applied))
            {
                at++;
                if (first != null && first.NodeId == s.NodeId) { last = s; continue; }
                Flush(); first = last = s; firstAt = at;
            }
            at++;
            Flush();
            return groups;
        }
    }
}
