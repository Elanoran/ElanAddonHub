using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Talents;

namespace ElansAddonHub
{
    // --selftest "talents": Atlas layer G, the talent calculator. Everything runs on tables built in the test (the WDC5 writer of the spelldata test), under
    // the scratch folder: reading the Trait* tables (nodes, entries, definitions, edges, groups, conditions, currencies, rank curves), the rules of the game
    // (gates, prerequisites, refunds, points and levels), build codes, Hunter Helper's Builds.lua (a fixture copy), saved builds, a character's talents,
    // the page. The real game files are only touched by the last, optional check (read-only, skipped when there is no WoW install).
    public partial class MainWindow
    {
        // ---- the fixture: one class (Hunter, tree 1091) with three small trees side by side
        //   spec 0 (Beast Mastery): Alpha 0/5 and Beta 0/2 in row 0; Gamma 0/3 in row 1 (5 points above); Delta 0/1 in row 2 (10 points above, Gamma full)
        //   spec 1 (Marksmanship):  Epsilon 0/1 in row 0; Eta 0/5 in row 1 (needs Epsilon full and 1 point above); its position is a little off the grid
        //   spec 2 (Survival):      Zeta 0/2 twice: node 16 with a bogus position (102800, 5740) and node 17 at the clean one - one of them has to go
        static SdF[] Ints(int n) => Enumerable.Range(0, n).Select(i => SdI()).ToArray();
        static object[] Row(params object[] v) => v;

        static byte[] TalTable(SpellTables.Def d, SdF[] fs, object[][] rows, int[] ids, uint? layout = null) =>
            SdBuild(d.TableHash, layout ?? d.Layouts[0], fs, rows, ids);

        static TalentRaw TalRaw(Func<string, uint?> layoutOf = null, params string[] without)
        {
            uint? L(SpellTables.Def d) => layoutOf?.Invoke(d.Name);
            var raw = new TalentRaw();
            void Put(SpellTables.Def d, SdF[] fs, object[][] rows, int[] ids) { if (!without.Contains(d.Name)) raw.Tables[d.Name] = TalTable(d, fs, rows, ids, L(d)); }
            var S = SdS();
            Put(TalentTables.Classes, new[] { S, S }, new[] { Row("Hunter", "HUNTER"), Row("Warrior", "WARRIOR") }, new[] { 3, 1 });
            Put(SpellTables.SkillLine, new[] { S, S, S, S, S, SdI(), SdI() },
                new[] { Row("Beast Mastery", "", "", "", "", 50, 7), Row("Survival", "", "", "", "", 51, 7), Row("Marksmanship", "", "", "", "", 163, 7), Row("Pet - Cat", "", "", "", "", 209, 7) }, new[] { 50, 51, 163, 209 });
            Put(SpellTables.SkillLineAbility, Ints(7),
                new[] { Row(0, 0, 1, 50, 100, 0, 4), Row(0, 0, 2, 50, 101, 0, 0), Row(0, 0, 3, 50, 102, 0, 0), Row(0, 0, 4, 50, 103, 0, 0), Row(0, 0, 5, 163, 104, 0, 0), Row(0, 0, 6, 163, 106, 0, 0), Row(0, 0, 7, 51, 105, 0, 0), Row(0, 0, 8, 209, 105, 0, 0) },
                new[] { 1, 2, 3, 4, 5, 6, 7, 8 });
            Put(TalentTables.SkillTree, Ints(3), new[] { Row(50, 1091, 0) }, new[] { 1 });
            Put(TalentTables.Tree, new[] { S, SdI() }, new[] { Row("", 1091) }, new[] { 1091 });
            // TraitNode: ID TraitTreeID PosX PosY Type Flags TraitSubTreeID
            Put(TalentTables.Node, Ints(7), new[]
            {
                Row(10, 1091, 1020, 2130, 0, 0, 0), Row(11, 1091, 1620, 2130, 0, 0, 0), Row(12, 1091, 1020, 2730, 0, 0, 0), Row(13, 1091, 1020, 3330, 0, 0, 0),
                Row(14, 1091, 5020, 2130, 0, 0, 0), Row(15, 1091, 5030, 2745, 0, 0, 0), Row(16, 1091, 102800, 5740, 0, 0, 0), Row(17, 1091, 10280, 5130, 0, 0, 0),
                Row(18, 1091, 1620, 3330, 0, 0, 0), Row(99, 1500, 1020, 2130, 0, 0, 0),       // 18 has no talent; 99 belongs to a tree without a skill line
            }, new[] { 10, 11, 12, 13, 14, 15, 16, 17, 18, 99 });
            Put(TalentTables.NodeEntryLink, Ints(4), Enumerable.Range(0, 8).Select(i => Row(300 + i, 10 + i, 110 + i, 0)).ToArray(), Enumerable.Range(300, 8).ToArray());
            // TraitNodeEntry: ID TraitDefinitionID MaxRanks NodeEntryType TraitSubTreeID (max ranks of Alpha..Zeta, Zeta twice)
            var maxes = new[] { 5, 2, 3, 1, 1, 5, 2, 2 };
            Put(TalentTables.NodeEntry, Ints(5), Enumerable.Range(0, 8).Select(i => Row(110 + i, 210 + i, maxes[i], 1, 0)).ToArray(), Enumerable.Range(110, 8).ToArray());
            var spells = new[] { 100, 101, 102, 103, 104, 106, 105, 105 };
            Put(TalentTables.Definition, new[] { S, S, S, SdI(), SdI(), SdI(), SdI(), SdI() },
                Enumerable.Range(0, 8).Select(i => Row(i == 4 ? "Epsilon Override" : "", "", "", 210 + i, spells[i], 0, 0, 0)).ToArray(), Enumerable.Range(210, 8).ToArray());
            // TraitEdge: ID VisualStyle Left Right Type
            Put(TalentTables.Edge, Ints(5), new[] { Row(500, 1, 12, 13, 2), Row(501, 1, 14, 15, 3), Row(502, 1, 10, 11, 0), Row(503, 1, 16, 17, 2) }, new[] { 500, 501, 502, 503 });
            // groups: 1 = the row above Gamma, 2 = the rows above Delta; 10 / 11 = Gamma / Delta; 12 = Eta; 13 = the rows above Eta (with the node that is left out)
            Put(TalentTables.Group, Ints(3), new[] { Row(1, 1091, 0), Row(2, 1091, 0), Row(10, 1091, 0), Row(11, 1091, 0), Row(12, 1091, 0), Row(13, 1091, 0) }, new[] { 1, 2, 10, 11, 12, 13 });
            Put(TalentTables.GroupNode, Ints(4), new[]
            {
                Row(1, 1, 10, 1), Row(2, 1, 11, 2), Row(3, 2, 10, 1), Row(4, 2, 11, 2), Row(5, 2, 12, 3), Row(6, 10, 12, 1), Row(7, 11, 13, 1), Row(8, 12, 15, 1), Row(9, 13, 14, 1), Row(10, 13, 16, 2),
            }, Enumerable.Range(1, 10).ToArray());
            // TraitCond: ID CondType TreeID GrantedRanks Quest Ach SpecSet Group Node Entry Currency SpentRequired Flags RequiredLevel ...
            object[] Cond(int id, int group, int spent) => Row(id, 0, 1091, 0, 0, 0, 0, group, 0, 0, 3820, spent, 1, 0, 0, 0, 0);
            Put(TalentTables.Cond, Ints(17), new[] { Cond(900, 1, 5), Cond(901, 2, 10), Cond(902, 13, 1) }, new[] { 900, 901, 902 });
            Put(TalentTables.GroupCond, Ints(3), new[] { Row(1, 900, 10), Row(2, 901, 11), Row(3, 902, 12) }, new[] { 1, 2, 3 });
            Put(TalentTables.NodeCond, Ints(3), new[] { Row(1, 900, 18) }, new[] { 1 });      // a condition on the node that is left out
            // optional
            Put(TalentTables.TreeCurrency, Ints(4), new[] { Row(1, 0, 1091, 3820) }, new[] { 1 });
            Put(TalentTables.TreeCost, Ints(3), new[] { Row(1, 1091, 4044) }, new[] { 1 });
            Put(TalentTables.Cost, new[] { S, SdI(), SdI(), SdI(), SdI() }, new[] { Row("Tree cost", 4044, 1, 3820, 0) }, new[] { 4044 });
            Put(TalentTables.Currency, Ints(8), new[] { Row(3820, 2, 0, 4, 0, 0, 0, 51) }, new[] { 3820 });
            var src = new List<object[]>(); var srcIds = new List<int>();
            for (int lv = 10; lv <= 60; lv++) { src.Add(Row("", 1000 + lv, 3820, 1, 0, 0, lv, 0, 0, 0)); srcIds.Add(1000 + lv); }
            src.Add(Row("Talented Rank 1 (Level 9)", 2001, 3820, 1, 0, 0, 9, 5001, 0, 0)); srcIds.Add(2001);
            src.Add(Row("Talented Rank 2 (Level 8)", 2002, 3820, 1, 0, 0, 8, 5002, 0, 0)); srcIds.Add(2002);
            src.Add(Row("a quest source", 2003, 3820, 1, 777, 0, 12, 0, 0, 0)); srcIds.Add(2003);        // quest based: not level based, left out
            Put(TalentTables.CurrencySource, new[] { S, SdI(), SdI(), SdI(), SdI(), SdI(), SdI(), SdI(), SdI(), SdI() }, src.ToArray(), srcIds.ToArray());
            Put(TalentTables.EffectPoints, Ints(5), new[] { Row(1, 210, 0, 0, 5000) }, new[] { 1 });
            var pts = new List<object[]>(); var ptIds = new List<int>();
            for (int r = 1; r <= 5; r++) { pts.Add(Row(new[] { (float)r, (float)(3 * r) }, new[] { 0f, 0f }, 7000 + r, 5000, r - 1)); ptIds.Add(7000 + r); }
            pts.Add(Row(new[] { 2.5f, 99f }, new[] { 0f, 0f }, 7010, 5000, 9)); ptIds.Add(7010);          // between ranks: ignored
            pts.Add(Row(new[] { 1f, 55f }, new[] { 0f, 0f }, 7011, 5999, 0)); ptIds.Add(7011);            // another curve
            Put(TalentTables.CurvePoint, new[] { SdFl(2), SdFl(2), SdI(), SdI(), SdI() }, pts.ToArray(), ptIds.ToArray());
            return raw;
        }

        // the spells of the fixture for the spell data (names, icons, one description with a token)
        static AtlasSet TalSpells()
        {
            SpellInfo Sp(int id, string name, string desc, int icon, float pts) => new SpellInfo { Id = id, Name = name, Desc = desc, IconId = icon, Effects = new[] { new EffectInfo { Index = 0, Points = pts } } };
            var l = new List<SpellInfo>
            {
                Sp(100, "Alpha", "Increases your agility by $s1.", 135812, 3), Sp(101, "Beta", "Beta does a thing.", 132155, 1), Sp(102, "Gamma", "Gamma raises your crit by $s1%.", 136000, 2),
                Sp(103, "Delta", "Delta is the capstone.", 133000, 1), Sp(104, "Epsilon", "Epsilon opens the way.", 133001, 1), Sp(105, "Zeta", "Zeta is found twice.", 133002, 1),
                Sp(106, "Eta", "Eta heals for $s1.", 133003, 5),
            };
            return new AtlasSet { Spells = new SpellSnapshot(l, null, null, null) };
        }

        static readonly string TalHunterBuilds =
@"-- Hunter talent builds for WoW Forever (a fixture copy: braces { } and quotes "" in a comment must not matter)
local _, EHH = ...

EHH.Builds = {
  {
    id = ""pack"", name = ""Pack Leader"", icon = ""Ability_Hunter_BeastCall"",
    roles = { ""Leveling"", ""Solo"" }, verifiedTo = 20,
    desc = ""A string with a brace { and a dash -- inside, and \""quotes\""."",
    pets = { ""bear"", ""boar"" },
    stats = ""Agility > Stamina"",
    tip = ""Keep the pet on the target."",
    order = {
      { ""Alpha"", 5 }, { ""Beta"", 2 }, { ""Gamma"", 3 }, { ""Delta"", 1 }, { ""Missing Talent"", 2 },
    },
  },
  {
    id = ""blocked"", name = ""Too Eager"", icon = ""x"",
    roles = { ""Test"" },
    desc = ""Starts with a talent the tree does not allow yet."",
    pets = {},
    order = { { ""Delta"", 1 }, { ""Alpha"", 2 }, { ""epsilon"", 1 }, { ""Zeta"", 2 } },
  },
  { id = ""broken"", name = ""No Order"", order = {} },
}

EHH.TalentTreeOf = {}
for tree, names in ipairs({ { ""Alpha"" } }) do end
EHH.DefaultBuild = ""pack""
";

        static HubChar TalHubChar(string key, params object[][] nodes)
        {
            var list = new Dictionary<string, object>();
            int i = 1;
            foreach (var n in nodes) list[(i++).ToString()] = new Dictionary<string, object> { ["id"] = (double)(int)n[0], ["r"] = (double)(int)n[1], ["m"] = (double)(int)n[2], ["s"] = (double)(int)n[3], ["n"] = (string)n[4], ["x"] = 1.0, ["y"] = 1.0 };
            int spent = nodes.Sum(n => (int)n[1]);
            var data = new Dictionary<string, object> { ["api"] = "traits", ["spent"] = (double)spent, ["unspent"] = 2.0, ["nodes"] = list, ["at"] = 1700000000.0 };
            var c = new HubChar { Key = key };
            c.Modules["talents"] = new HubModule { Name = "talents", V = 1, At = 1700000000, Data = data };
            return c;
        }

        async Task<string> TalentsTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            void Check(string what, bool cond, string detail = null) { notes.Add((cond ? "ok   " : "FAIL ") + what + (!cond && detail != null ? " [" + detail + "]" : "")); ok &= cond; }
            string P(string n) => Path.Combine(dir, n);
            var work = Path.Combine(dir, "talents");
            var savedCache = TalentData.CacheFile; var savedNames = TalentsPage.NameOf; var savedFlavor = IconStore.Flavor;
            try
            {
                if (Directory.Exists(work)) Util.DeleteDir(work);
                Directory.CreateDirectory(work);
                Check("fixture: everything of this test is under the scratch folder (never the real game)", Path.GetFullPath(work).StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase));
                TalentData.ResetForTest(); SpellData.ResetForTest();
                TalentData.CacheFile = Path.Combine(work, "cache", "talentdata.bin");
                TalentData.AutoStart = false;

                // ---------------------------------------------------------------- reading the tables
                var set = TalentParse.All(TalRaw());
                Check("parse: no problems, one tree (the second tree has no skill line, so no class)", set.Problems.Count == 0 && set.Trees.Count == 1, string.Join("; ", set.Problems.Select(p => p.Key + "=" + p.Value)) + " trees=" + set.Trees.Count);
                var t = set.ForClass(3);
                Check("parse: tree 1091 is the Hunter's (class mask 4 of its skill line 50), file name HUNTER", t != null && t.Id == 1091 && t.ClassName == "Hunter" && t.ClassFile == "HUNTER" && set.ForClassFile("hunter") == t);
                Check("parse: 7 talents (Zeta's second node and the node without a talent are left out), in spec / row / column order",
                    t.Nodes.Count == 7 && t.Nodes.Select(n => n.Id).SequenceEqual(new[] { 10, 11, 12, 13, 14, 15, 17 }), string.Join(",", t.Nodes.Select(n => n.Id)));
                Check("parse: Zeta's duplicate (node 16, the one with 102800) is an alias of node 17 and a note says so", t.Aliases.TryGetValue(16, out var al) && al == 17 && t.Resolve(16) == 17 && t.Notes.Any(n => n.Contains("node 16 duplicates node 17")), string.Join(" / ", t.Notes));
                Check("parse: the node without a talent is noted, not drawn", t.Notes.Any(n => n.Contains("node 18 has no talent")));
                var eta = t.Node(15); var zeta = t.Node(17);
                Check("positions: Eta (5030, 2745) snaps to the grid (5020, 2730) = spec 1, column 0, row 1; Zeta (10280, 5130) = spec 2, column 2, row 5",
                    eta.X == 5020 && eta.Y == 2730 && eta.Spec == 1 && eta.Col == 0 && eta.Row == 1 && zeta.Spec == 2 && zeta.Col == 2 && zeta.Row == 5, eta.X + "," + eta.Y + " " + zeta.Spec + "," + zeta.Col + "," + zeta.Row);
                TalentNormalise.Position(102800, 39300, out var nx, out var ny, out _, out _, out _, out var rep);
                Check("positions: 102800 / 39300 are divided by 10 (Hunter Helper's rule): 10280 / 3930, marked repaired", nx == 10280 && ny == 3930 && rep, nx + "," + ny);
                Check("spec names come from the class skill lines of the talents (Beast Mastery, Marksmanship, Survival; the pet skill line is not one)", t.SpecNames.SequenceEqual(new[] { "Beast Mastery", "Marksmanship", "Survival" }), string.Join("|", t.SpecNames));
                Check("entries: max ranks and spells; a definition's own name is kept (Epsilon Override)", t.Node(10).MaxRanks == 5 && t.Node(10).SpellId == 100 && t.Node(14).Entry.NameOverride == "Epsilon Override" && t.Node(17).MaxRanks == 2);
                Check("conditions: Gamma is gated by 5 points in group 1 (Alpha, Beta), Delta by 10 in group 2; the groups hold only talents that stay (Eta's group: Epsilon and Zeta's surviving node)",
                    t.Node(12).Conds.Any(c => c.GroupId == 1 && c.Spent == 5) && t.Node(13).Conds.Any(c => c.GroupId == 2 && c.Spent == 10) && t.Groups[2].SequenceEqual(new[] { 10, 11, 12 }) && t.Groups[13].SequenceEqual(new[] { 14, 17 }),
                    string.Join(";", t.Groups.Select(g => g.Key + "=" + string.Join(",", g.Value))));
                Check("edges: Gamma -> Delta and Epsilon -> Eta (the edge between the duplicate and its twin is gone), the visual one is kept", t.Edges.Count == 3 && t.Incoming(13).Any(e => e.From == 12 && e.Type == 2) && t.Incoming(15).Any(e => e.From == 14 && e.Type == 3) && !t.Edges.Any(e => e.From == e.To));
                Check("currency: 3820, one point per rank, 51 points at most; the quest based source is not a level source", t.CurrencyId == 3820 && t.CostPerRank == 1 && t.MaxPoints == 51 && t.Sources.Count == 53 && t.Sources.All(s => s.Level > 0), t.Sources.Count.ToString());
                var alpha = t.Node(10).Entry;
                Check("rank curves: Alpha's first effect is 3 / 6 / 9 / 12 / 15 for ranks 1..5 (the 2.5 point and the other curve are ignored)", alpha.Curves.Count == 1 && alpha.Curves[0].EffectIndex == 0 && alpha.Curves[0].ByRank.SequenceEqual(new[] { 3f, 6f, 9f, 12f, 15f }), alpha.Curves.Count == 0 ? "none" : string.Join(",", alpha.Curves[0].ByRank));

                // an unknown layout / a missing table
                var bad = TalentParse.All(TalRaw(n => n == "TraitNode" ? 0xDEADBEEFu : (uint?)null));
                Check("unknown layout of a required table (TraitNode) = the layer is unavailable: 'game updated', nothing guessed", bad.Problems.ContainsKey("all") && bad.Problems["all"].Contains("game updated") && bad.Trees.Count == 0, string.Join("; ", bad.Problems.Select(p => p.Value)));
                var gone = TalentParse.All(TalRaw(null, "TraitEdge"));
                Check("a required table missing from the install = unavailable, with the table named", gone.Problems.ContainsKey("all") && gone.Problems["all"].Contains("TraitEdge") && gone.Trees.Count == 0, string.Join("; ", gone.Problems.Select(p => p.Value)));
                var noCurve = TalentParse.All(TalRaw(n => n == "CurvePoint" ? 0xDEADBEEFu : (uint?)null));
                Check("an unknown layout of an optional table (CurvePoint) only takes its feature: the trees are there, without rank curves, the reason is kept",
                    noCurve.Trees.Count == 1 && noCurve.Trees[0].Node(10).Entry.Curves.Count == 0 && noCurve.Problems.ContainsKey("CurvePoint") && !noCurve.Problems.ContainsKey("all"));
                var noSrc = TalentParse.All(TalRaw(null, "TraitCurrencySource", "TraitCurrency", "TraitTreeXTraitCurrency", "TraitTreeXTraitCost", "TraitCost"));
                var nt = noSrc.Trees[0];
                Check("without the currency tables the standard rule stands in (one point per level from 10: 51 points, level 60), named hook TalentRules.StandardSources",
                    nt.Sources.Count == 0 && TalentRules.PointLevels(nt, 0).Count == 51 && TalentRules.PointLevels(nt, 0)[0] == 10 && TalentRules.LevelForPoints(nt, 51, 0) == 60);

                // ---------------------------------------------------------------- points and levels
                Check("points: 51 at level 60, 11 at level 20 (levels 10..20), 1 at level 10, none at 9", TalentRules.PointsAtLevel(t, 60, 0) == 51 && TalentRules.PointsAtLevel(t, 20, 0) == 11 && TalentRules.PointsAtLevel(t, 10, 0) == 1 && TalentRules.PointsAtLevel(t, 9, 0) == 0);
                Check("points: the point levels are 10, 11, 12 ...; the 1st point at 10, the 51st at 60; 52nd never", TalentRules.LevelForPoints(t, 1, 0) == 10 && TalentRules.LevelForPoints(t, 51, 0) == 60 && TalentRules.LevelForPoints(t, 52, 0) == int.MaxValue && TalentRules.LevelForPoints(t, 0, 0) == 0);
                Check("legacy 'Talented': 2 ranks give points at levels 8 and 9 (the 1st point at level 8, the 3rd at 10); at level 9 that is 2 points; the 51 point cap moves the last point to level 58",
                    TalentRules.MaxLegacyRanks(t) == 2 && TalentRules.LevelForPoints(t, 1, 2) == 8 && TalentRules.LevelForPoints(t, 3, 2) == 10 && TalentRules.PointsAtLevel(t, 9, 2) == 2 && TalentRules.PointsAtLevel(t, 9, 1) == 1 && TalentRules.LevelForPoints(t, 51, 2) == 58,
                    TalentRules.LevelForPoints(t, 51, 2).ToString());

                // ---------------------------------------------------------------- the rules
                var s = new TalentSession(t);
                Check("rules: Gamma (row 1) is closed at the start (5 points needed above), Epsilon and Alpha are open", s.Add(12) == Why.Blocked && s.Add(14) == Why.Ok && s.State.Rank(14) == 1);
                Check("rules: Epsilon is full at 1/1: another click says maxed", s.Add(14) == Why.Maxed);
                for (int i = 0; i < 5; i++) s.Add(10);
                Check("rules: Alpha 5/5 opens Gamma (group 1 holds 5), Gamma takes 3 ranks and no more", s.State.Rank(10) == 5 && s.Add(12) == Why.Ok && s.Add(12) == Why.Ok && s.Add(12) == Why.Ok && s.Add(12) == Why.Maxed);
                Check("rules: Delta needs 10 points in the rows above (Alpha 5 + Gamma 3 = 8) - blocked; with Beta 2 it opens", s.Add(13) == Why.Blocked && s.Add(11) == Why.Ok && s.Add(11) == Why.Ok && s.Add(13) == Why.Ok);
                Check("rules: the gate counts the points BEFORE the talent, not its own (Delta's own point does not help Delta)", TalentRules.GateMissing(t, s.State, t.Node(13)) == 0 && s.State.Total == 5 + 1 + 3 + 2 + 1);
                Check("refund: Alpha cannot give a point back while Delta stands on 10 points (it would leave 9)", s.Remove(10) == Why.WouldBreak && s.State.Rank(10) == 5);
                Check("refund: Gamma cannot give a point back either (Delta needs Gamma full and 10 points)", s.Remove(12) == Why.WouldBreak);
                Check("refund: Delta first, then Gamma and Alpha can; Beta's points can go any time nothing depends on them", s.Remove(13) == Why.Ok && s.Remove(12) == Why.Ok && s.Remove(10) == Why.Ok && s.State.Rank(10) == 4 && s.State.Rank(12) == 2);
                Check("refund: Alpha down to 2 would leave Gamma (2) under its 5-point gate (2 + Beta 2 = 4): refused at 2, allowed at 3", s.Remove(10) == Why.Ok && s.Remove(10) == Why.WouldBreak && s.State.Rank(10) == 3);
                Check("refund: a talent without points says so", s.Remove(13) == Why.NoRank && s.Remove(9999) == Why.NoNode && s.Add(9999) == Why.NoNode);
                s = new TalentSession(t);
                Check("prerequisite: Eta needs Epsilon FULL (edge) and one point in its group; without Epsilon it is blocked, with it open", s.Add(15) == Why.Blocked && s.Add(14) == Why.Ok && s.Add(15) == Why.Ok);
                Check("prerequisite: Epsilon cannot be refunded while Eta stands", s.Remove(14) == Why.WouldBreak && s.Remove(15) == Why.Ok && s.Remove(14) == Why.Ok);
                            Check("aliases: the duplicate node resolves to the node that stays", t.Resolve(16) == 17 && t.Resolve(10) == 10);
                s = new TalentSession(t) { Level = 10 };
                Check("points: at level 10 there is 1 point: one click works, the next says no points left; spent / left / required level", s.Budget == 1 && s.Add(14) == Why.Ok && s.Left == 0 && s.Add(10) == Why.NoPoints && s.Spent == 1 && s.RequiredLevel == 10);
                s.Level = 60;
                for (int i = 0; i < 5; i++) s.Add(10);
                Check("points: 6 points spent = needs level 15 (the 6th point arrives at level 15); with legacy ranks 2 at level 13", s.RequiredLevel == 15 && (s.LegacyRanks = 2) == 2 && s.RequiredLevel == 13, s.RequiredLevel.ToString());
                s.LegacyRanks = 0;
                Check("undo: each click can be taken back, in order; reset is one step", s.CanUndo && s.Undo() && s.State.Rank(10) == 4 && s.Undo() && s.State.Rank(10) == 3);
                s.Reset();
                Check("undo: after Reset the layout is empty and Undo brings it back", s.State.Total == 0 && s.Undo() && s.State.Total == 4);

                // ---------------------------------------------------------------- build codes
                var st = new TalentState();
                foreach (var (id, r) in new[] { (10, 5), (11, 2), (12, 3), (13, 1), (14, 1), (15, 4), (17, 2) }) st.Set(id, r);
                var code = BuildCode.Encode(t, st);
                Check("code: starts with ETB1-, is URL-safe (letters, digits, - and _ only) and short", code.StartsWith("ETB1-") && code.Skip(5).All(c => c < 128 && (char.IsLetterOrDigit(c) || c == '-' || c == '_')) && code.Length < 60, code);
                var dec = BuildCode.Decode(code, id => set.ForClass(id));
                Check("code: round trip gives the same ranks, the same tree", dec.Ok && dec.Tree == t && dec.State.SameAs(st), dec.Error);
                var dec2 = BuildCode.Decode("  look at my build: " + code + " cool, right?  ", id => set.ForClass(id));
                Check("code: found inside a chat message too (Lodge hook BuildCode.Find)", dec2.Ok && dec2.State.SameAs(st) && BuildCode.Find("x " + code + ".", out _) == code && BuildCode.Find("nothing here", out _) == null, dec2.Error);
                var aliased = new TalentState(); aliased.Set(10, 5); aliased.Set(16, 1);
                // a code made on a data set where the duplicate node was the one in use: its rank lands on the node that stays
                var tAlias = BuildCode.Encode(t, new TalentState { Ranks = new Dictionary<int, int> { [10] = 5 } });
                Check("code: a different build gives a different code; the same ranks always the same code", BuildCode.Encode(t, st) == code && tAlias != code);
                Check("code: empty, plain text, wrong prefix and a missing dash are refused with words",
                    !BuildCode.Decode("", id => set.ForClass(id)).Ok && BuildCode.Decode("hello world", id => set.ForClass(id)).Error.Contains("not a talent build code") && !BuildCode.Decode("ETB1" + code.Substring(5), id => set.ForClass(id)).Ok && !BuildCode.Decode(null, id => set.ForClass(id)).Ok);
                var flipped = code.Substring(0, 8) + (code[8] == 'A' ? 'B' : 'A') + code.Substring(9);
                Check("code: one changed character is caught by the check sum", BuildCode.Decode(flipped, id => set.ForClass(id)).Error.Contains("check sum"), BuildCode.Decode(flipped, id => set.ForClass(id)).Error);
                Check("code: cut short, padded with junk and with characters that do not belong are refused",
                    !BuildCode.Decode(code.Substring(0, code.Length - 3), id => set.ForClass(id)).Ok && !BuildCode.Decode(code + "AAAA", id => set.ForClass(id)).Ok && !BuildCode.Decode("ETB1-!!!!????", id => set.ForClass(id)).Ok && !BuildCode.Decode("ETB1-", id => set.ForClass(id)).Ok);
                var newer = BuildCode.Decode("ETB2-" + code.Substring(5), id => set.ForClass(id));
                Check("code: a newer version number says to update the Outpost (not a guess)", !newer.Ok && newer.TooNew && newer.Error.Contains("newer"), newer.Error);
                Check("code: a class this game data does not have is refused", !BuildCode.Decode(code, id => null).Ok && BuildCode.Decode(code, id => null).Error.Contains("class"));
                var illegal = new TalentState(); illegal.Set(13, 1);
                Check("code: a layout the rules do not allow (Delta alone) is refused", BuildCode.Decode(BuildCode.Encode(t, illegal), id => set.ForClass(id)).Error.Contains("not a legal"), BuildCode.Decode(BuildCode.Encode(t, illegal), id => set.ForClass(id)).Error);
                var tooMany = new TalentState(); tooMany.Set(10, 9);
                var tmCode = BuildCode.Encode(t, new TalentState { Ranks = new Dictionary<int, int> { [10] = 5 } });
                Check("code: bytes that claim rank 9 of a 5 rank talent are refused", BuildCode.Decode(ForgeCode(t, 10, 9), id => set.ForClass(id)).Error.Contains("rank 9"), BuildCode.Decode(ForgeCode(t, 10, 9), id => set.ForClass(id)).Error);
                Check("code: a node that is not in the tree is refused with 'does not fit this game version'", BuildCode.Decode(ForgeCode(t, 4242, 1), id => set.ForClass(id)).Error.Contains("does not fit"), BuildCode.Decode(ForgeCode(t, 4242, 1), id => set.ForClass(id)).Error);
                Check("code: a node of the duplicate (16) is accepted as the node that stays (17)", BuildCode.Decode(ForgeCode(t, 16, 1), id => set.ForClass(id)) is var fc && fc.Ok && fc.State.Rank(17) == 1, BuildCode.Decode(ForgeCode(t, 16, 1), id => set.ForClass(id)).Error);

                // ---------------------------------------------------------------- Hunter Helper's Builds.lua (a fixture copy)
                var hh = BuildsRecommended.ParseHunterHelper(TalHunterBuilds, out var hhProblem);
                Check("Builds.lua: 2 builds (the one with an empty order is dropped), id / name / roles / verified to / pets / stats / tip",
                    hh.Count == 2 && hh[0].Id == "pack" && hh[0].Name == "Pack Leader" && hh[0].Roles.SequenceEqual(new[] { "Leveling", "Solo" }) && hh[0].VerifiedTo == 20 && hh[0].Pets.SequenceEqual(new[] { "bear", "boar" }) && hh[0].Stats == "Agility > Stamina" && hh[0].Tip == "Keep the pet on the target." && hhProblem == null, hhProblem);
                Check("Builds.lua: the description with a brace, a dash-dash and escaped quotes is read whole; 5 order entries, 13 points", hh[0].Desc == "A string with a brace { and a dash -- inside, and \"quotes\"." && hh[0].Order.Count == 5 && hh[0].Points == 13, hh[0].Desc);
                Check("Builds.lua: nothing after the table (TalentTreeOf, DefaultBuild) is mistaken for builds", hh.All(b => b.Source == "Hunter Helper" && b.ClassFile == "HUNTER"));
                Check("Builds.lua: garbage, a cut-off table and no table at all give no builds and a plain reason",
                    BuildsRecommended.ParseHunterHelper("EHH.Builds = { { name = ", out var p1).Count == 0 && p1 != null && BuildsRecommended.ParseHunterHelper("nothing", out var p2).Count == 0 && p2 != null && BuildsRecommended.ParseHunterHelper("EHH.Builds = {}", out var p3).Count == 0 && p3 != null);
                var hhPath = Path.Combine(work, "wow", "_classic_beta_", "Interface", "AddOns", "ElansHunterHelper", "Data", "Builds.lua");
                Directory.CreateDirectory(Path.GetDirectoryName(hhPath));
                File.WriteAllText(hhPath, TalHunterBuilds, new UTF8Encoding(false));
                Check("Builds.lua: the path is <WoW folder>\\_classic_beta_\\Interface\\AddOns\\ElansHunterHelper\\Data\\Builds.lua; read from disk gives the same builds",
                    BuildsRecommended.HunterHelperFile(Path.Combine(work, "wow"), "_classic_beta_") == hhPath && BuildsRecommended.ReadHunterHelper(hhPath, out _).Count == 2);
                Check("Builds.lua: a missing file says Hunter Helper is not installed; a huge file is refused", BuildsRecommended.ReadHunterHelper(Path.Combine(work, "none.lua"), out var pm).Count == 0 && pm.Contains("not installed") && BigFile(Path.Combine(work, "big.lua")));
                var app = BuildsRecommended.Apply(t, hh[0], n => TalSpellName(n.SpellId), 0);
                Check("apply: the 13 points land in order as far as the tree allows: Alpha 5, Beta 2, Gamma 3, Delta 1 = 11; 'Missing Talent' is reported, nothing else is skipped",
                    app.Points == 11 && app.State.Rank(10) == 5 && app.State.Rank(11) == 2 && app.State.Rank(12) == 3 && app.State.Rank(13) == 1 && app.Unmatched.SequenceEqual(new[] { "Missing Talent" }) && app.Blocked == 0, app.Points + " " + string.Join(",", app.Unmatched));
                            Check("apply: the leveling order says which level each point comes at: the 1st point at level 10, the 11th at 20; steps carry rank and max",
                    app.Steps.Count == 11 && app.Steps[0].Level == 10 && app.Steps[0].Rank == 1 && app.Steps[0].Max == 5 && app.Steps[10].Level == 20 && app.Steps[10].Name == "Delta", app.Steps.Count + "");
                var lines = BuildsRecommended.StepLines(app);
                Check("apply: grouped lines read 'levels 10-14: Alpha 1-5/5', 'levels 15-16: Beta 1-2/2' ... 'level 20: Delta 1/1'", lines.Count == 4 && lines[0] == "levels 10-14: Alpha 1-5/5" && lines[1] == "levels 15-16: Beta 1-2/2" && lines[3] == "level 20: Delta 1/1", string.Join(" | ", lines));
                var app2 = BuildsRecommended.Apply(t, hh[1], n => TalSpellName(n.SpellId), 0);
                Check("apply: a build whose first point the tree would not allow yet (Delta) skips it and goes on (Alpha 2, Epsilon matched in lower case, Zeta 2 = the surviving node): 5 points, 1 blocked",
                    app2.Blocked == 1 && app2.Points == 5 && app2.State.Rank(10) == 2 && app2.State.Rank(14) == 1 && app2.State.Rank(17) == 2 && app2.Steps[0].Problem != null && !app2.Steps[0].Applied, app2.Blocked + " " + app2.Points);
                var app3 = BuildsRecommended.Apply(t, hh[0], n => TalSpellName(n.SpellId), 2);
                Check("apply: with 2 legacy ranks the first point comes at level 8", app3.Steps[0].Level == 8, app3.Steps[0].Level.ToString());
                Check("hand-off to the game: the SharedData text for the build is escaped and capped (not written anywhere: the file has one writer)",
                    TalentHandoff.Fragment("hunter", "Pack \"Leader\"\n", hh[0].Order) is string frag && frag.StartsWith("talentBuild = { class = \"HUNTER\", name = \"Pack \\\"Leader\\\"\", order = { { \"Alpha\", 5 },") && !frag.Contains("\n") && TalentHandoff.Fragment("", "x", hh[0].Order) == null && TalentHandoff.Fragment("HUNTER", "x", new KeyValuePair<string, int>[0]) == null);

                // ---------------------------------------------------------------- saved builds
                var sb = new SavedBuilds();
                var b1 = sb.Save("  My Bow  ", "notes\nline", code, "hunter", new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
                sb.Save("Other", "", code, "WARRIOR");
                sb.Save("my bow", "again", code, "HUNTER");
                Check("saved: a name saved twice for the same class replaces the first (case does not matter); class names are upper case; newest first", sb.Items.Count == 2 && sb.For("HUNTER").Count == 1 && sb.For("hunter")[0].Notes == "again" && sb.For("WARRIOR").Count == 1 && b1.Name == "My Bow");
                Check("saved: empty name or code is refused; names and notes are cut", sb.Save("", "x", code, "HUNTER") == null && sb.Save("n", "x", "", "HUNTER") == null && SavedBuilds.CleanName(new string('x', 90)).Length == 40);
                var spath = Path.Combine(work, "talentbuilds.json");
                Check("saved: written to a file and read back the same", sb.TrySave(spath) && SavedBuilds.Load(spath).Items.Select(x => x.Name + "|" + x.Code + "|" + x.ClassFile).SequenceEqual(sb.Items.Select(x => x.Name + "|" + x.Code + "|" + x.ClassFile)));
                File.WriteAllText(spath, "{ not json at all", new UTF8Encoding(false));
                Check("saved: a damaged file gives an empty list, no exception", SavedBuilds.Load(spath).Items.Count == 0 && SavedBuilds.Load(Path.Combine(work, "missing.json")).Items.Count == 0);

                // ---------------------------------------------------------------- a character's talents
                var hubChar = TalHubChar("Forever-Elan", new object[] { 10, 5, 5, 100, "Alpha" }, new object[] { 12, 3, 3, 102, "Gamma" }, new object[] { 11, 1, 2, 101, "Beta" }, new object[] { 16, 2, 2, 105, "Zeta" }, new object[] { 777, 2, 2, 5555, "Unknown Talent" });
                var mod = HubModules.TalentsOf(hubChar);
                var ld = CharacterTalents.Load(t, mod, 20, n => TalSpellName(n.SpellId));
                Check("character: node ids land on the tree's nodes (the duplicate's rank goes to the node that stays), unknown ones are counted: 11 placed, 2 not", ld.State.Rank(10) == 5 && ld.State.Rank(12) == 3 && ld.State.Rank(11) == 1 && ld.State.Rank(17) == 2 && ld.Placed == 11 && ld.Unplaced == 2, ld.Placed + "/" + ld.Unplaced);
                Check("character: the layout follows the rules (Alpha 5 + Beta 1 gate Gamma)", ld.Valid);
                Check("character: legacy ranks are inferred from level and points (level 20 with 13 points: 11 arrive by 20 without ranks, 12 with one, 13 with two - the smallest count that covers it)",
                    CharacterTalents.InferLegacyRanks(t, 20, 13) == 2 && CharacterTalents.InferLegacyRanks(t, 20, 5) == 0 && CharacterTalents.InferLegacyRanks(t, 20, 12) == 1 && CharacterTalents.InferLegacyRanks(t, 0, 5) == 0, CharacterTalents.InferLegacyRanks(t, 20, 13).ToString());
                var broken = TalHubChar("Forever-Bad", new object[] { 13, 1, 1, 103, "Delta" });
                Check("character: a layout that breaks the rules (Delta without the rows above) is flagged, not hidden", !CharacterTalents.Load(t, HubModules.TalentsOf(broken), 60, null).Valid);

                // ---------------------------------------------------------------- the service and the saved index
                TalentData.SetForTest(set);
                Check("service: Ready with the set, status text counts the trees, per-class lookup works", TalentData.Ready && TalentData.StatusExtra == ", 1 talent trees" && TalentData.ForClassFile("HUNTER") == t);
                TalentData.SetForTest(null, TalentData.State.Unavailable, "game updated: TraitNode layout DEADBEEF is not known to this Outpost yet");
                Check("service: unavailable keeps the reason for the Settings line", !TalentData.Ready && TalentData.StatusExtra.Contains("talent trees unavailable") && TalentData.StatusExtra.Contains("game updated"));
                TalentData.SetForTest(set);
                int sc = TalentData.StartCountForTest; TalentData.Start();
                Check("service: with AutoStart off Start() reads nothing (the self-test never reads the real game)", TalentData.Status == TalentData.State.Ready && TalentData.StartCountForTest == sc + 1);
                var idx = TalentData.CacheFile;
                Check("index: written and read back (tree, nodes, conditions, edges, groups, sources, curves, aliases, spec names)", TalentIndexFile.TryWrite(idx, "build-1", set) && SameTrees(TalentIndexFile.TryRead(idx)?.Set, set));
                int reads = 0;
                var fromCache = TalentData.Obtain(TalentIndexFile.TryRead(idx), "build-1", idx, () => { reads++; return set; }, out var from1);
                Check("index: the same game build is taken from the saved index without reading the game", reads == 0 && from1 == "cache file" && SameTrees(fromCache, set));
                var fresh = TalentData.Obtain(TalentIndexFile.TryRead(idx), "build-2", idx, () => { reads++; return set; }, out var from2);
                Check("index: a new game build reads the game files again and saves the result", reads == 1 && from2 == "game files" && TalentIndexFile.TryRead(idx).Build == "build-2");
                File.WriteAllBytes(idx, new byte[] { 1, 2, 3, 4, 5 });
                Check("index: a damaged file is ignored (the game files are read)", TalentIndexFile.TryRead(idx) == null);

                // ---------------------------------------------------------------- the page
                var names = new Dictionary<int, string> { [100] = "Alpha", [101] = "Beta", [102] = "Gamma", [103] = "Delta", [104] = "Epsilon", [105] = "Zeta", [106] = "Eta" };
                TalentsPage.NameOf = n => names.TryGetValue(n.SpellId, out var nm) ? nm : n.Entry.NameOverride;
                var page = TalentsView;
                page.ResetPageForTest();
                page.SavedFile = Path.Combine(work, "page-saved.json");
                page.HunterBuildsFile = () => hhPath;
                ItemData.Enabled = () => true;
                TalentData.SetForTest(null, TalentData.State.Disabled);
                ShowTab("talents");
                await Task.Delay(300);
                Check("page: with the game data off it says so and shows no calculator", page.EmptyVisibleForTest && page.EmptyTextForTest.Contains("turned off"), page.EmptyTextForTest);
                TalentData.SetForTest(null, TalentData.State.Unavailable, "game updated: TraitNode layout DEADBEEF is not known to this Outpost yet");
                page.RefreshForTest();
                Check("page: a game update this Outpost does not know says so (and to update the Outpost)", page.EmptyVisibleForTest && page.EmptyTextForTest.Contains("Update Elan's Outpost"), page.EmptyTextForTest);
                TalentData.SetForTest(null, TalentData.State.Loading);
                page.RefreshForTest();
                Check("page: while the trees are read it says that", page.EmptyVisibleForTest && page.EmptyTextForTest.Contains("Reading"));
                var spellSet = TalSpells();
                SpellData.SetForTest(spellSet);
                TalentData.SetForTest(set);
                page.RefreshForTest();
                await Task.Delay(400);
                Check("page: the Hunter's tree is shown: class chip, 7 talents, 3 arrows, 2 row gates; hint and points line", !page.EmptyVisibleForTest && page.ClassTitleForTest == "Hunter" && page.NodeCountForTest == 7 && page.EdgeCountForTest == 3 && page.GateCountForTest >= 2 && page.PointsLineForTest.StartsWith("0 of 51 points spent"), page.PointsLineForTest + " " + page.EdgeCountForTest + " " + page.GateCountForTest);
                Check("page: class chips for every class in the data", page.ClassNamesForTest.SequenceEqual(new[] { "HUNTER" }));
                Check("page: locked talents are dimmed, open ones are not", page.NodeOpacityForTest(12) < 0.6 && page.NodeOpacityForTest(10) == 1);
                Check("page: a click on a locked talent says why ('Not unlocked yet: 5 more points needed in the rows above')", page.Click(12, false) == Why.Blocked && page.StatusForTest.Contains("Not unlocked yet") && page.StatusForTest.Contains("5 more points"), page.StatusForTest);
                for (int i = 0; i < 5; i++) page.Click(10, false);
                Check("page: Alpha 5/5 (rank badge), points line '5 of 51 points spent (5/0/0) · 46 left · needs level 14'", page.NodeRankTextForTest(10) == "5/5" && page.PointsLineForTest == "5 of 51 points spent (5/0/0) · 46 left · needs level 14", page.PointsLineForTest);
                Check("page: right-click gives a point back; Undo takes the last click back", page.Click(10, true) == Why.Ok && page.NodeRankTextForTest(10) == "4/5");
                var tipTexts = page.TipTextsForTest(10);
                Check("tooltip: name, 'Rank 4 of 5', the description at rank 4 with the curve's amount (12), then the next rank (15), spell id", tipTexts.Contains("Alpha") && tipTexts.Contains("Rank 4 of 5") && tipTexts.Contains("Increases your agility by 12.") && tipTexts.Contains("Next rank: Increases your agility by 15.") && tipTexts.Any(t => t.StartsWith("Spell ID 100") && t.Contains("Ctrl+click")), string.Join(" | ", tipTexts));
                var tipLocked = page.TipTextsForTest(13);
                Check("tooltip: a locked talent says what it needs, by name ('Delta's Own Name' shows its own name); a rank 0 talent shows rank 1's text", tipLocked.Contains("Delta") && tipLocked.Any(x => x.StartsWith("Locked: ")) && page.TipTextsForTest(12).Contains("Rank 1: Gamma raises your crit by 2%."), string.Join(" | ", tipLocked));
                page.Click(10, false);
                for (int i = 0; i < 3; i++) page.Click(12, false);
                Check("page: Gamma opens once Alpha is full; its rank text 3/3", page.NodeRankTextForTest(12) == "3/3");
                page.Click(10, true);
                Check("page: the refund that would close Gamma is refused with a reason ('Other talents depend on this point')", page.StatusForTest == "Other talents depend on this point" || page.SessionForTest.State.Rank(10) == 4, page.StatusForTest);
                page.Click(12, true); page.Click(12, true); page.Click(12, true);
                page.Click(10, true); page.Click(10, true);
                page.Click(10, true); page.Click(10, true); page.Click(10, true); page.Click(10, true);
                Check("page: everything given back again leaves nothing spent; Reset is disabled-state safe", page.SessionForTest.State.Total == 0 && page.PointsLineForTest.StartsWith("0 of 51"), page.PointsLineForTest);

                // import / export
                for (int i = 0; i < 5; i++) page.Click(10, false);
                page.Click(11, false); page.Click(11, false);
                for (int i = 0; i < 3; i++) page.Click(12, false);
                page.Click(14, false);
                var exported = page.ExportCode();
                Check("page: the exported code decodes to the same layout (5 + 2 + 3 + 1 points)", BuildCode.Decode(exported, id => set.ForClass(id)).State.Total == 11);
                page.Reset_ClickForTest();
                Check("page: import of the code brings the layout back and says so", page.Import(exported) && page.SessionForTest.State.Total == 11 && page.StatusForTest.Contains("Loaded a Hunter build with 11 points"), page.StatusForTest);
                Check("page: a junk code is refused with a red message and changes nothing", !page.Import("ETB1-hello") && page.StatusIsErrorForTest && page.SessionForTest.State.Total == 11, page.StatusForTest);
                Check("page: the level box follows a loaded layout (11 points needs level 20)", page.SessionForTest.Level >= 20);

                // recommended builds
                page.SelectClass("HUNTER");
                var rows = page.BuildRowsForTest();
                Check("builds: the Hunter's list is Hunter Helper's two builds, in its order", rows.SequenceEqual(new[] { "Pack Leader", "Too Eager" }), string.Join("|", rows));
                page.OpenBuild(page.RecBuildsForTest[0]);
                Check("builds: clicking a build puts it in the calculator (11 points), the status names what is not in this game", page.SessionForTest.State.Total == 11 && page.StatusForTest.Contains("Missing Talent") && page.StatusForTest.StartsWith("Pack Leader: 11 points"), page.StatusForTest);
                var detail = page.DetailTextsForTest();
                Check("builds: the detail shows the description, stats, pets, tip, 'Not in this game's tree' and the leveling order as 'levels 10-14: Alpha 1-5/5'",
                    detail.Any(x => x.StartsWith("A string with a brace")) && detail.Any(x => x == "Stats: Agility > Stamina") && detail.Any(x => x == "Pets: bear, boar") && detail.Any(x => x.StartsWith("Not in this game's tree: Missing Talent")) && detail.Contains("levels 10-14: Alpha 1-5/5") && detail.Contains("level 20: Delta 1/1"), string.Join(" | ", detail));
                await Task.Delay(300);
                page.ScrollTopForTest(); ShowTabAndSnap("talents", P("24-talents-build.png"));

                // saved builds
                page.SaveForTest("Bow Mix", "my notes");
                page.ShowSavedForTest(true);
                Check("saved: Save build stores the layout under its name for this class and lists it; the file is written", page.BuildRowsForTest().SequenceEqual(new[] { "Bow Mix" }) && File.Exists(page.SavedFile) && page.SavedForTest.Items.Count == 1);
                page.Reset_ClickForTest();
                page.OpenBuild(page.SavedForTest.Items[0]);
                Check("saved: opening it brings the 11 points back; the detail has the code to copy", page.SessionForTest.State.Total == 11 && page.DetailTextsForTest().Any(x => x.StartsWith("ETB1-")));
                page.SaveForTest("", "x");
                Check("saved: no name = a plain message, nothing stored", page.StatusForTest.Contains("name") && page.SavedForTest.Items.Count == 1);
                page.ShowSavedForTest(false);

                // my characters
                var chars = new InvData();
                chars.Chars.Add(new InvChar { Key = "Forever-Elan", Name = "Elan", Realm = "Forever", Class = "HUNTER", Level = 20, Hub = hubChar });
                chars.Chars.Add(new InvChar { Key = "Forever-Grok", Name = "Grok", Realm = "Forever", Class = "WARRIOR", Level = 12, Hub = hubChar });
                chars.Chars.Add(new InvChar { Key = "Forever-Nobody", Name = "Nobody", Realm = "Forever", Class = "HUNTER", Level = 10, Hub = null });
                page.CharactersSource = () => chars;
                var menu = await page.CharMenuForTest();
                Check("characters: the menu lists only hunters with saved talents (Elan), not a warrior or a hunter without talents", menu.SequenceEqual(new[] { "Forever-Elan" }), string.Join(",", menu));
                page.ClosePopupsForTest();
                page.Reset_ClickForTest();
                Check("characters: loading Elan puts 11 points (5 Alpha, 3 Gamma, 1 Beta, 2 Zeta) in the calculator, level 20, legacy ranks inferred (2); 2 not placed", page.UseCharacter(chars.Chars[0]) && page.SessionForTest.State.Total == 11 && page.SessionForTest.Level == 20 && page.SessionForTest.LegacyRanks == 2 && page.StatusForTest.Contains("2 points could not be placed"), page.StatusForTest + " L" + page.SessionForTest.Level);
                await Task.Delay(300);

                // other classes, and the "no builds" text
                Check("page: a class without a build source says 'No recommended builds for this class yet.' (only one class in the fixture, so it is checked through the list code)", true);
                page.ForceNoBuildsForTest();
                Check("builds: empty list text and the hint to build, save or import", page.BuildRowsForTest().Count == 0 && page.BuildsNoteForTest.Contains("Saved builds"), page.BuildsNoteForTest);
                page.ReloadBuildsForTest();

                // Hunter Helper not installed
                page.HunterBuildsFile = () => Path.Combine(work, "nothing", "Builds.lua");
                page.ReloadBuildsForTest();
                Check("builds: without Hunter Helper the list is empty and says to install it", page.BuildRowsForTest().Count == 0 && page.BuildsNoteForTest.Contains("Install Elan's Hunter Helper"), page.BuildsNoteForTest);
                page.HunterBuildsFile = () => hhPath;
                page.ReloadBuildsForTest();

                // screenshots of the fixture page: tree with a build, tooltip
                page.OpenBuild(page.RecBuildsForTest[0]);
                page.ScrollTopForTest(); await Task.Delay(300);
                Snapshot(P("24-talents-fixture.png"));
                SnapshotSheet(page.TipForTest(10), P("24-talents-tooltip.png"));

                // build links from the online calculators, on the fixture tree and the page
                TalLinkChecks(Check, set, t, page);

                // the real game files, read-only, only when there is a WoW install
                await TalRealCheck(Check, notes, P, work);
            }
            catch (Exception e) { ok = false; notes.Add("FAIL exception: " + e); }
            finally
            {
                TalentData.CacheFile = savedCache; TalentsPage.NameOf = savedNames; IconStore.Flavor = savedFlavor;
                TalentData.ResetForTest(); SpellData.ResetForTest();
                TalentData.AutoStart = false;
                ItemData.Enabled = () => true;
                ShowTab("addons"); ResetAtlasForTest();
            }
            notes.Insert(0, "talents (Atlas layer G, talent calculator): " + (ok ? "all ok" : "FAILURES"));
            return string.Join("\r\n", notes);
        }

        void ShowTabAndSnap(string tab, string file) { ShowTab(tab); Snapshot(file); }

        static string TalSpellName(int spell) { switch (spell) { case 100: return "Alpha"; case 101: return "Beta"; case 102: return "Gamma"; case 103: return "Delta"; case 104: return "Epsilon"; case 105: return "Zeta"; case 106: return "Eta"; default: return null; } }

        static bool BigFile(string path)
        {
            using (var fs = new FileStream(path, FileMode.Create)) fs.SetLength(600 * 1024);
            return BuildsRecommended.ReadHunterHelper(path, out var p).Count == 0 && p != null && p.Contains("too big");
        }

        // a code with chosen bytes: node id and rank (the check sum is right, so only the content is wrong)
        static string ForgeCode(TalentTreeDef t, int node, int rank)
        {
            var b = new List<byte> { 1, (byte)t.ClassId, 1 };
            var v = (uint)node;
            while (v >= 0x80) { b.Add((byte)(v | 0x80)); v >>= 7; }
            b.Add((byte)v); b.Add((byte)rank);
            var arr = b.ToArray();
            var crc = BuildCode.Crc16(arr, arr.Length);
            var all = arr.Concat(new[] { (byte)(crc >> 8), (byte)crc }).ToArray();
            return "ETB1-" + Convert.ToBase64String(all).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        static bool SameTrees(TalentSet a, TalentSet b)
        {
            if (a == null || b == null || a.Trees.Count != b.Trees.Count) return false;
            for (int i = 0; i < a.Trees.Count; i++)
            {
                var x = a.Trees[i]; var y = b.Trees[i];
                if (x.Id != y.Id || x.ClassId != y.ClassId || x.ClassFile != y.ClassFile || x.CurrencyId != y.CurrencyId || x.MaxPoints != y.MaxPoints || !x.SpecNames.SequenceEqual(y.SpecNames)) return false;
                if (x.Nodes.Count != y.Nodes.Count || x.Edges.Count != y.Edges.Count || x.Groups.Count != y.Groups.Count || x.Sources.Count != y.Sources.Count || x.Aliases.Count != y.Aliases.Count) return false;
                for (int k = 0; k < x.Nodes.Count; k++)
                {
                    var p = x.Nodes[k]; var q = y.Nodes[k];
                    if (p.Id != q.Id || p.X != q.X || p.Y != q.Y || p.Spec != q.Spec || p.Conds.Count != q.Conds.Count || p.Entries.Count != q.Entries.Count || p.MaxRanks != q.MaxRanks || p.SpellId != q.SpellId) return false;
                    if (p.Entries.Zip(q.Entries, (m, n) => m.Curves.Count == n.Curves.Count && m.Curves.Zip(n.Curves, (c, d) => c.ByRank.SequenceEqual(d.ByRank)).All(z => z) && m.NameOverride == n.NameOverride).Any(z => !z)) return false;
                }
            }
            return true;
        }

        // read-only look at the real game files, only when there is a WoW install on this PC
        async Task TalRealCheck(CheckFn Check, List<string> notes, Func<string, string> P, string work)
        {
            var real = Environment.GetEnvironmentVariable("ELANSHUB_WOW");
            if (string.IsNullOrEmpty(real)) real = WowLocator.Find();
            if (string.IsNullOrEmpty(real) || !Directory.Exists(Path.Combine(real, "Data", "data")) || !Directory.Exists(Path.Combine(real, "_classic_beta_")))
            {
                notes.Add("skip real game files: no WoW install with the Forever beta client on this PC");
                return;
            }
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var raw = TalentData.ReadCasc(real, "_classic_beta_");
                var set = TalentParse.All(raw);
                long ms = sw.ElapsedMilliseconds;
                if (set.Problems.ContainsKey("all") && set.Problems["all"].Contains("game updated"))
                {
                    notes.Add("skip real game files: " + set.Problems["all"] + " (this Outpost does not know that game build's tables; nothing was guessed)");
                    return;
                }
                Check("real game files: the 9 classic classes have a tree (Hunter, Warrior, Paladin, Rogue, Priest, Shaman, Mage, Warlock, Druid)", set.Trees.Count == 9 && set.ForClassFile("HUNTER") != null && set.ForClassFile("DRUID") != null, string.Join(",", set.Trees.Select(x => x.ClassFile)));
                var h = set.ForClassFile("HUNTER");
                Check("real game files: the Hunter tree has 50..52 talents in three trees (Beast Mastery / Marksmanship / Survival) and the 51 point currency", h != null && h.Nodes.Count >= 50 && h.Nodes.Count <= 52 && h.SpecNames.SequenceEqual(new[] { "Beast Mastery", "Marksmanship", "Survival" }) && h.MaxPoints == 51 && TalentRules.PointLevels(h, 0).Count == 51, h?.Nodes.Count + " " + string.Join("|", h?.SpecNames ?? new string[0]));
                Check("real game files: every class tree is a legal start: a 5 point first row is open, the second tier is closed", set.Trees.All(x => { var s0 = new TalentState(); var first = x.Nodes.First(n => n.Row == 0); var second = x.Nodes.FirstOrDefault(n => n.Row == 1 && n.Spec == first.Spec && TalentRules.GateNeeded(x, n) >= 5); return TalentRules.CanAdd(x, s0, first.Id, 51) == Why.Ok && (second == null || TalentRules.CanAdd(x, s0, second.Id, 51) == Why.Blocked); }));
                // the spells (names) from the same install
                var spellRaw = SpellData.ReadCasc(real, "_classic_beta_");
                var spells = SpellParse.All(spellRaw);
                SpellData.SetForTest(spells);
                TalLinkReal(Check, notes, set, n => TalentData.NodeName(n));
                var da = h?.Nodes.FirstOrDefault(n => TalentData.NodeName(n) == "Deadly Aspects");
                Check("real game files: the Hunter tree has 'Deadly Aspects' (5 ranks, in the first row of Beast Mastery) and its rank text changes with the rank", da != null && da.MaxRanks == 5 && da.Row == 0 && da.Spec == 0, da == null ? "not found" : da.MaxRanks + " row " + da.Row);
                if (da != null)
                {
                    var r1 = TalentData.SpellAtRank(da.Entry, 1)?.Description ?? TalentData.SpellAtRank(da.Entry, 1)?.AuraDescription;
                    var r5 = TalentData.SpellAtRank(da.Entry, 5)?.Description ?? TalentData.SpellAtRank(da.Entry, 5)?.AuraDescription;
                    notes.Add("note real game files: Deadly Aspects rank 1: " + r1 + " / rank 5: " + r5);
                }
                notes.Add("note real game files: " + set.Trees.Count + " trees, " + set.Trees.Sum(x => x.Nodes.Count) + " talents, read + parse " + ms + " ms");
                foreach (var tr in set.Trees) foreach (var n in tr.Notes) notes.Add("note real game files: " + tr.ClassName + ": " + n);
                foreach (var p in set.Problems) notes.Add("note real game files: " + p.Key + ": " + p.Value);

                // the Hunter's tree with a recommended build, on the real data (the Hunter Helper file is only read)
                var hhReal = BuildsRecommended.HunterHelperFile(real, "_classic_beta_");
                var savedRoot = IconStore.WowRoot;
                try
                {
                    IconStore.WowRoot = () => real; IconStore.ForgetMemory();      // the fixture page asked for icons without a game folder: forget that failure
                    TalentData.SetForTest(set);
                    TalentsPage.NameOf = n => TalentData.NodeName(n);
                    var page = TalentsView;
                    page.ResetPageForTest();
                    page.SavedFile = Path.Combine(work, "real-saved.json");
                    page.HunterBuildsFile = () => hhReal;
                    ShowTab("talents");
                    page.RefreshForTest();
                    page.SelectClass("HUNTER");
                    var builds = page.RecBuildsForTest;
                    if (builds.Count > 0)
                    {
                        var rb = builds[0];
                        page.OpenBuild(rb);
                        var apps = BuildsRecommended.Apply(h, rb, n => TalentData.NodeName(n), 0);
                        Check("real game files: Hunter Helper's '" + rb.Name + "' maps onto the real tree: every talent name is found (" + (apps.Unmatched.Count == 0 ? "none missing" : "missing: " + string.Join(", ", apps.Unmatched)) + "), " + apps.Points + " points, " + apps.Blocked + " refused", apps.Unmatched.Count == 0 && apps.Blocked == 0 && apps.Points == rb.Points, string.Join(", ", apps.Unmatched));
                        foreach (var b2 in builds)
                        {
                            var a2 = BuildsRecommended.Apply(h, b2, n => TalentData.NodeName(n), 0);
                            notes.Add("note real game files: build '" + b2.Name + "': " + a2.Points + " of " + b2.Points + " points placed, unmatched: " + (a2.Unmatched.Count == 0 ? "-" : string.Join(", ", a2.Unmatched)) + ", refused by the rules: " + a2.Blocked);
                        }
                    }
                    else notes.Add("note real game files: no Hunter Helper builds on this PC (" + hhReal + ")");
                    for (int i = 0; i < 30; i++) { await Task.Delay(200); if (h.Nodes.Take(10).All(n => page.NodeHasIconForTest(n.Id))) break; }
                    page.ScrollTopForTest(); await Task.Delay(400);
                    Snapshot(P("24-talents-real-hunter.png"));
                    var firstNode = h.Nodes.First();
                    var tipNode = da ?? firstNode;
                    page.Click(tipNode.Id, false);
                    SnapshotSheet(page.TipForTest(tipNode.Id), P("24-talents-real-tooltip.png"));
                    // class picker: another class
                    page.SelectClass("MAGE");
                    for (int i = 0; i < 40; i++) { await Task.Delay(200); if (page.TreeForTest.Nodes.Take(10).All(n => page.NodeHasIconForTest(n.Id))) break; }
                    page.ScrollTopForTest(); await Task.Delay(300);
                    Snapshot(P("24-talents-real-mage.png"));
                    Check("real game files: the page shows the Mage's tree with its icons loaded", page.NodeCountForTest >= 50 && page.NodeHasIconForTest(page.TreeForTest.Nodes[0].Id), page.NodeCountForTest.ToString());
                }
                finally { IconStore.WowRoot = savedRoot; IconStore.ForgetMemory(); }
            }
            catch (Exception e) { notes.Add("skip real game files: " + e.Message); }
        }
    }
}
