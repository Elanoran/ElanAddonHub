using System;
using Elans.Shared;
using System.Collections.Generic;
using System.Linq;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;

namespace ElansAddonHub
{
    // --selftest "atlaslodge" (part): more accurate loot (Elan's Toolkit 0.5.10, Lodge server 2.26 feature "atlas9"). Counter extras (builds, quest kills, level,
    // class, corpse fingerprints; item quest, suffixes, builds) and the pickpocket / gathering / contained counters: validation part by part, sharing only to a
    // lodge that takes them, the lodge's merge with `dup` and its cache, and how the pages word a rate: "N of M kills (x %)", each corpse once, quest items
    // over the kills while on the quest, no rate under 30 kills, suffixes, since the last patch, level / class, the new sections and "Contained in".
    public partial class MainWindow
    {
        static Dictionary<string, object> FaItem(double n, double c, Dictionary<string, object> more = null)
        {
            var d = new Dictionary<string, object> { ["n"] = n, ["c"] = c };
            if (more != null) foreach (var kv in more) d[kv.Key] = kv.Value;
            return d;
        }
        static Dictionary<string, object> FaMap(params (string k, double v)[] kv) => kv.ToDictionary(p => p.k, p => (object)p.v);

        void AtlasFindsAccuracyTest(Action<string, bool> check)
        {
            void Check(string what, bool cond, string extra = null) => check(what + (cond || string.IsNullOrEmpty(extra) ? "" : " [" + extra + "]"), cond);

            // ---- the Toolkit's tables
            var hub = new Dictionary<string, object>
            {
                ["since"] = 1.0, ["build"] = 70374.0,
                ["kills"] = new Dictionary<string, object>
                {
                    ["8"] = new Dictionary<string, object>
                    {
                        ["n"] = 3.0, ["items"] = new Dictionary<string, object> { ["15210"] = FaItem(3, 3, new Dictionary<string, object> { ["q"] = 33.0, ["sx"] = FaMap(("1047", 2), ("-6", 1)), ["b"] = FaMap(("70374", 3)) }) },
                        ["x"] = new Dictionary<string, object> { ["b"] = FaMap(("70374", 3)), ["q"] = FaMap(("33", 2)), ["lv"] = new List<object> { 31.0, 33.0 }, ["cl"] = FaMap(("1", 2), ("2", 1)), ["fp"] = new List<object> { 11.0, 12.0 } },
                    },
                    ["9"] = new Dictionary<string, object> { ["n"] = 2.0, ["x"] = new Dictionary<string, object> { ["b"] = FaMap(("70374", 99)), ["lv"] = new List<object> { 40.0, 30.0 } } },
                },
                ["pickpocket"] = new Dictionary<string, object> { ["3"] = new Dictionary<string, object> { ["n"] = 2.0, ["items"] = new Dictionary<string, object> { ["5428"] = FaItem(2, 2) } } },
                ["contained"] = new Dictionary<string, object> { ["5523"] = new Dictionary<string, object> { ["n"] = 1.0, ["items"] = new Dictionary<string, object> { ["5498"] = FaItem(1, 1) } } },
                ["finds"] = new Dictionary<string, object> { ["1"] = new Dictionary<string, object> { ["i"] = 5498.0, ["c"] = 1.0, ["src"] = "item:5523", ["at"] = 1_790_000_000.0, ["b"] = 70374.0 } },   // a Lua list as the Hub file reader gives it
            };
            var snap = AtlasObs.FromTables(hub);
            var k8 = snap.Kills[8];
            Check("extras: a counter's builds, quest kills, level range, classes and corpse fingerprints are read, and per item its quest, suffixes and builds",
                k8.X != null && k8.X.B[70374] == 3 && k8.X.Q[33] == 2 && k8.X.Lo == 31 && k8.X.Hi == 33 && k8.X.Cl[1] == 2 && k8.X.Fp.Count == 2 && k8.X.It[15210].Q == 33 && k8.X.It[15210].Sx[-6] == 1 && k8.X.It[15210].B[70374] == 3);
            Check("extras: bad parts (more builds than kills, an upside-down level range) are left out but the counter stays",
                snap.Kills[9].N == 2 && snap.Kills[9].X == null);
            Check("new sections: pickpocket and contained are read; build is known; nothing is an unknown section",
                snap.Gather["pickpocket"][3].N == 2 && snap.Gather["contained"][5523].Items.ContainsKey(5498) && snap.Build == 70374 && snap.UnknownSections.Count == 0 && snap.Finds.Count == 1,
                string.Join(",", snap.UnknownSections) + " finds " + snap.Finds.Count + " build " + snap.Build + " gather " + string.Join(",", snap.Gather.Keys));
            var b0 = AtlasObs.MakeBatch(snap, null, new Dictionary<string, string>());
            var b1 = AtlasObs.MakeBatch(snap, null, new Dictionary<string, string>(), includeExtras: true);
            var k0 = Wire(b0.Frame).Child("kills").Child("8");
            var k1 = Wire(b1.Frame).Child("kills").Child("8");
            Check("sharing: an older lodge gets the plain counters (no extras, no new sections, no finds from inside items); an atlas9 lodge gets all of it; a resend sends nothing",
                !k0.ContainsKey("x") && !Wire(b0.Frame).ContainsKey("pickpocket") && !Wire(b0.Frame).ContainsKey("finds") && k1.ContainsKey("x") && Wire(b1.Frame).Child("pickpocket") != null && Wire(b1.Frame).Child("contained") != null
                && Wire(b1.Frame).List("finds").Count == 1 && AtlasObs.MakeBatch(snap, null, b1.Sigs, includeExtras: true) == null,
                LodgeJson.Serialize(b0.Frame).Substring(0, Math.Min(300, LodgeJson.Serialize(b0.Frame).Length)) + " || " + LodgeJson.Serialize(b1.Frame).Substring(0, Math.Min(400, LodgeJson.Serialize(b1.Frame).Length)));

            // ---- the wording of a rate
            Check("rate text: \"4 of 37 kills (10.8 %)\", and under 30 kills no rate is claimed",
                AtlasObs.RateText(4, 37, "kill", "kills") == "4 of 37 kills (10.8 %)" && AtlasObs.RateText(2, 12, "kill", "kills") == "2 of 12 kills (too few to tell a rate)" && AtlasObs.RateText(1, 1, "kill", "kills").Contains("1 kill "));

            // ---- the lodge's merge and the pages
            var lodge = new Lodge.LodgeAtlas();
            bool applied = lodge.Apply(LodgeJson.FromPlain(new Dictionary<string, object>
            {
                ["t"] = "atlas.data", ["epoch"] = "fa1", ["rev"] = 3.0, ["full"] = true,
                ["kills"] = new Dictionary<string, object>
                {
                    ["8"] = new Dictionary<string, object>
                    {
                        ["n"] = 40.0, ["m"] = 3.0, ["dup"] = 4.0,
                        ["items"] = new Dictionary<string, object> { ["2000"] = FaItem(9, 9, new Dictionary<string, object> { ["b"] = FaMap(("70374", 6)), ["sx"] = FaMap(("1047", 6), ("-6", 3)) }), ["750"] = FaItem(5, 5, new Dictionary<string, object> { ["q"] = 33.0 }) },
                        ["x"] = new Dictionary<string, object> { ["b"] = FaMap(("70338", 10), ("70374", 30)), ["q"] = FaMap(("33", 10)), ["lv"] = new List<object> { 31.0, 33.0 }, ["cl"] = FaMap(("1", 40)) },
                    },
                    ["12"] = new Dictionary<string, object> { ["n"] = 12.0, ["m"] = 1.0, ["items"] = new Dictionary<string, object> { ["2000"] = FaItem(2, 2) } },
                },
                ["skinning"] = new Dictionary<string, object> { ["8"] = new Dictionary<string, object> { ["n"] = 30.0, ["m"] = 2.0, ["items"] = new Dictionary<string, object> { ["2318"] = FaItem(30, 60) } } },
                ["pickpocket"] = new Dictionary<string, object> { ["8"] = new Dictionary<string, object> { ["n"] = 5.0, ["m"] = 1.0, ["items"] = new Dictionary<string, object> { ["5428"] = FaItem(1, 1) } } },
                ["contained"] = new Dictionary<string, object> { ["5523"] = new Dictionary<string, object> { ["n"] = 50.0, ["m"] = 2.0, ["items"] = new Dictionary<string, object> { ["5498"] = FaItem(12, 12) } } },
            }) as System.Text.Json.Nodes.JsonObject);
            var back = new Lodge.LodgeAtlas();
            Check("lodge: the merge's dup and extras and the new counters are read and survive the cache file",
                applied && lodge.Loot("npc", 8).X?.Dup == 4 && lodge.Loot("skin", 8)?.N == 30 && lodge.Loot("item", 5523)?.N == 50
                && back.Apply(LodgeJson.Parse(LodgeJson.Serialize(lodge.ToCache()))) && back.Loot("npc", 8).X?.Dup == 4 && back.Loot("npc", 8).X.It[2000].Sx[1047] == 6 && back.Loot("pick", 8)?.N == 5 && back.Loot("item", 5523)?.N == 50);

            var hadLodge = AtlasQuery.Context.Lodge;
            try
            {
                AtlasQuery.Context.Lodge = () => lodge;
                var page = AtlasQuery.Build("npc:8");
                var loot = page.Section("loot");
                var r2000 = loot?.Rows.FirstOrDefault(r => r.Link == "item:2000");
                var r750 = loot?.Rows.FirstOrDefault(r => r.Link == "item:750");
                Check("NPC page: \"8 of 36 kills (22.2 %)\": each corpse once (40 kills, 4 counted twice; the drops scaled the same) and the note says so",
                    r2000 != null && r2000.Detail == "8 of 36 kills (22.2 %)" && loot.Note.Contains("4 corpses that several members looted counted once"), r2000?.Detail + " | " + loot?.Note);
                Check("NPC page: a quest item's rate counts only the kills while its quest still wanted it, and says so (10 kills: too few for a rate)",
                    r750 != null && r750.Detail == "5 of 10 kills while on the quest (too few to tell a rate)", r750?.Detail);
                Check("NPC page: the random suffixes and the rate since the last patch ride along",
                    r2000 != null && r2000.Extra.Contains("Random suffix: #1047 67 % · #-6 33 %") && r2000.Extra.Contains("Since the last patch: 6 of 30 kills (20.0 %)"), r2000?.Extra);
                // ---- event builds: the end of the beta (70532) drops epics and other unusual items; that build's kills and drops stay out of the rates
                var ev = new Lodge.LodgeAtlas();
                ev.Apply(LodgeJson.FromPlain(new Dictionary<string, object>
                {
                    ["t"] = "atlas.data", ["epoch"] = "fa2", ["rev"] = 1.0, ["full"] = true,
                    ["kills"] = new Dictionary<string, object>
                    {
                        ["9"] = new Dictionary<string, object>
                        {
                            ["n"] = 50.0, ["m"] = 2.0,
                            ["items"] = new Dictionary<string, object> { ["3000"] = FaItem(8, 8, new Dictionary<string, object> { ["b"] = FaMap(("70450", 6), ("70532", 2)) }), ["19019"] = FaItem(3, 3, new Dictionary<string, object> { ["b"] = FaMap(("70532", 3)) }) },
                            ["x"] = new Dictionary<string, object> { ["b"] = FaMap(("70450", 40), ("70532", 10)) },
                        },
                        ["10"] = new Dictionary<string, object> { ["n"] = 4.0, ["m"] = 1.0, ["items"] = new Dictionary<string, object> { ["19019"] = FaItem(4, 4, new Dictionary<string, object> { ["b"] = FaMap(("70532", 4)) }) }, ["x"] = new Dictionary<string, object> { ["b"] = FaMap(("70532", 4)) } },
                    },
                }) as System.Text.Json.Nodes.JsonObject);
                AtlasQuery.Context.Lodge = () => ev;
                var evLoot = AtlasQuery.Build("npc:9").Section("loot");
                var r3000 = evLoot?.Rows.FirstOrDefault(r => r.Link == "item:3000");
                Check("event builds: the end-of-beta build (70532) is left out of a rate: 6 of 40 kills, not 8 of 50; an item seen only then (19019) is not listed; the note says why",
                    AtlasObs.EventBuilds.Contains(70532) && r3000 != null && r3000.Detail == "6 of 40 kills (15.0 %)" && !evLoot.Rows.Any(r => r.Link == "item:19019") && evLoot.Note.Contains("10 kills from the end of the beta"),
                    r3000?.Detail + " | " + evLoot?.Note + " | " + string.Join(",", evLoot?.Rows.Select(r => r.Link) ?? new string[0]));
                Check("event builds: a source seen only in that build is gone, and the item's own page lists no drop for it; the raw numbers stay (cache)",
                    ev.Loot("npc", 10) == null && ev.Drops(19019).Count == 0 && ev.ToCache().ToJsonString().Contains("70532"), ev.Drops(19019).Count + "");
                var plainC = new AtlasObs.Counter { N = 5 }; plainC.Items[1] = new long[] { 1, 1 };
                Check("event builds: a counter without builds (an older Toolkit) and an empty list leave the numbers alone",
                    ReferenceEquals(AtlasObs.WithoutEventBuilds(plainC), plainC) && lodge.Loot("npc", 8) != null && AtlasObs.WithoutEventBuilds(ev.Loot("npc", 9), new HashSet<int>()) != null);
                AtlasQuery.Context.Lodge = () => lodge;
                Check("NPC page: the level range and class members saw (\"Level seen\")", page.Facts.Any(f => f.Label == "Level seen" && f.Value == AtlasFormat.Levels(31, 33) + " · Elite" && f.Source == AtlasLabel.Forever),
                    string.Join(" | ", page.Facts.Select(f => f.ToString())));
                var skin = page.Section("skin"); var pick = page.Section("pick");
                Check("NPC page: skinning and pickpocketing are sections of their own (30 corpses, 5 pickpockets), not part of the loot",
                    skin != null && skin.Rows.Any(r => r.Link == "item:2318" && r.Detail.StartsWith("30 of 30 corpses") && r.Type == "Skinning") && pick != null && pick.Rows.Any(r => r.Detail == "1 of 5 pickpockets (too few to tell a rate)")
                    && !loot.Rows.Any(r => r.Link == "item:2318"), skin == null ? "no skin" : string.Join(" | ", skin.Rows.Select(r => r.Detail)));
                var few = AtlasQuery.Build("npc:12").Section("loot");
                Check("NPC page: a source with 12 kills says these are counts, not drop rates yet", few != null && few.Note.Contains("Fewer than 30 so far") && few.Rows[0].Detail == "2 of 12 kills (too few to tell a rate)", few?.Note);
                var clam = AtlasQuery.Build("item:5523").Section("contains");
                var pearl = AtlasQuery.Build("item:5498").Section("sources");
                var inClam = pearl?.Rows.FirstOrDefault(r => r.Type == "Contained in");
                Check("item pages: an opened item lists what it contained (12 of 50 openings) and the found item says it was contained in it",
                    clam != null && clam.Rows.Any(r => r.Link == "item:5498" && r.Detail == "12 of 50 openings (24.0 %)") && inClam != null && inClam.Link == "item:5523" && inClam.Detail.StartsWith("12 of 50 openings (24.0 %)"),
                    (clam == null ? "no contains" : string.Join(" | ", clam.Rows.Select(r => r.Detail))) + " / " + inClam?.Detail);
            }
            catch (Exception e) { Check("finds accuracy test crashed: " + e, false); }
            finally { AtlasQuery.Context.Lodge = hadLodge; }
        }
    }
}
