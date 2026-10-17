using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Professions;

namespace ElansAddonHub
{
    // --selftest "professions", part 2: the player's real game files, read-only, only when there is a WoW install with the Forever beta client on this PC.
    // Checks the numbers this layer was built on (Linen Bandage 30 / 60, the rank caps, WoW Forever's campfire kits, the Faction Banner, a Cooking guide)
    // and takes the screenshots (the files land in the self-test folder: professions-*.png).
    public partial class MainWindow
    {
        async Task ProfRealChecks(string dir, CheckFn Check, List<string> notes)
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
                var set = SpellParse.All(SpellData.ReadCasc(real, "_classic_beta_"));
                var hard = set.Problems.Where(p => p.Value.StartsWith("game updated")).ToList();
                if (hard.Count > 0) { notes.Add("skip real game files: " + string.Join("; ", hard.Select(p => p.Value)) + " (this Outpost does not know that game build's tables; nothing was guessed)"); return; }
                var rawI = ItemData.ReadCasc(real, "_classic_beta_");
                var iset = ItemData.ParseTables(rawI.Sparse, rawI.Item, rawI.Budget);
                if (!iset.Ok || set.Spells == null) { notes.Add("skip real game files: tables not readable (" + (iset.Problem ?? "spells") + ")"); return; }
                SpellData.SetForTest(set);
                ItemData.SetForTest(new ItemSnapshot(iset.Client, null), ItemData.State.Ready);
                var read = sw.ElapsedMilliseconds;
                sw.Restart();
                var book = ProfessionBook.Build(set.Spells, ItemData.Get, set.Details?.Effects);
                long built = sw.ElapsedMilliseconds;

                var cooking = book.ByName("Cooking"); var firstAid = book.ByName("First Aid"); var tailoring = book.ByName("Tailoring");
                Check("real: the professions are there (6 crafting, Herbalism / Mining / Skinning / Fishing, Cooking, First Aid)", cooking != null && firstAid != null && tailoring != null && book.ByName("Herbalism")?.Gathering == true && book.ByName("Mining")?.Gathering == true && book.ByName("Skinning")?.Gathering == true && book.ByName("Fishing")?.Gathering == true && book.Professions.Count(p => p.Kind == ProfKind.Primary) >= 6, string.Join(",", book.Professions.Select(p => p.Name)));
                Check("real: the ranks come from the game's own rank spells: Apprentice 75, Journeyman 150, Expert 225, Artisan 300 (Cooking, Tailoring, Herbalism)",
                    string.Join(",", cooking.Ranks.Select(r => r.Name + r.Cap)) == "Apprentice75,Journeyman150,Expert225,Artisan300" && tailoring.MaxCap == 300 && book.ByName("Herbalism").Ranks.Count == 4, string.Join(",", cooking.Ranks.Select(r => r.Name + r.Cap)));
                var lb = firstAid.Recipes.FirstOrDefault(r => r.Name == "Linen Bandage");
                Check("real: Linen Bandage is 30 / 60 in the table: orange to 29, yellow 30, green 45, grey 60 (what the game shows)", lb != null && lb.Low == 30 && lb.High == 60 && lb.ColorAt(29) == SkillColor.Orange && lb.ColorAt(30) == SkillColor.Yellow && lb.ColorAt(45) == SkillColor.Green && lb.ColorAt(60) == SkillColor.Grey, lb?.ToString());
                var cwm = cooking.Recipes.FirstOrDefault(r => r.Name == "Charred Wolf Meat");
                Check("real: a recipe taught with the profession (Charred Wolf Meat, 45 / 85) is known from skill 1, not from the band's estimate", cwm != null && cwm.Learn == LearnKind.Starter && cwm.Start == 1 && cwm.Low == 45 && cwm.High == 85, cwm?.ToString());
                int taught = book.Professions.SelectMany(p => p.Recipes).Count(r => r.TeachingItems.Count > 0);
                Check("real: 1,500+ recipes are taught by a recipe item (Pattern / Plans / Formula / Blueprint ...), found from the item effects", taught > 1500, taught + "");
                var camp = book.Camp;
                Check("real camping: three kits (Basic, Journeyman, Expert) of Cooking taking 3, 5 and 10 camp objects, burning 15 minutes, benefits after 1 minute",
                    camp.Kits.Count == 3 && camp.Kits.Select(k => k.Capacity).SequenceEqual(new[] { 3, 5, 10 }) && camp.Kits.All(k => k.Profession == "Cooking" && k.FireMs == 900000 && k.BenefitMinutes == 1), string.Join(",", camp.Kits.Select(k => k.Name + k.Capacity + "/" + k.FireMs)));
                Check("real camping: the Journeyman and Expert kits need a Blueprint (item names start with 'Blueprint:'), the Basic kit none; a shared cooldown of 1 hour",
                    !camp.Kits[0].NeedsBlueprint && camp.Kits[1].NeedsBlueprint && camp.Kits[2].NeedsBlueprint && NameOfItem(camp.Kits[1].BlueprintItemId).StartsWith("Blueprint:") && NameOfItem(camp.Kits[2].BlueprintItemId).StartsWith("Blueprint:") && camp.CooldownText == "1 hour");
                Check("real camping: 30+ camp objects, every one from a profession's own recipe list; Tailoring has its Faction Banner (Alliance and Horde) with +Spirit; Blacksmithing's Sharpening Wheel gives Strength and does not stack with Strength of Earth Totem",
                    camp.Objects.Count >= 30 && camp.Objects.Any(o => o.Profession == "Tailoring" && o.Name == "Faction Banner" && o.Side == "Alliance" && o.Buff.Contains("Spirit")) && camp.Objects.Any(o => o.Name == "Faction Banner" && o.Side == "Horde")
                    && camp.Objects.Any(o => o.Name == "Sharpening Wheel" && o.Buff.Contains("Strength") && o.ExclusiveWith == "Strength of Earth Totem" && o.Profession == "Blacksmithing"), camp.Objects.Count + "");

                // a guide priced from the client's own item values (nothing downloaded here: every price is an estimate)
                var fs = new FixedSources();
                foreach (var r in book.Professions.SelectMany(p => p.Recipes))
                    foreach (var g0 in r.Reagents)
                    {
                        var ci = ItemData.Get(g0.ItemId);
                        if (ci != null && !fs.Prices.ContainsKey(g0.ItemId)) fs.Prices[g0.ItemId] = new ItemPrice(ci.BuyPrice > 0 ? ci.BuyPrice : ci.SellPrice * 4, PriceKind.Estimated);
                    }
                var cg = SkillGuideBuilder.Build(book, cooking, 1, 300, fs, null);
                var caps = cg.Steps.Where(s => s.IsMilestone).Select(s => s.From).ToList();
                Check("real guide Cooking 1-300: reaches 300 in 10+ steps, the trainer milestones at 1, 75, 150, 225, the steps are contiguous", cg.Reached == 300 && cg.Crafting.Count() >= 10 && caps.SequenceEqual(new[] { 1, 75, 150, 225 }) && cg.Crafting.Zip(cg.Crafting.Skip(1), (a, b) => a.To == b.From).All(x => x), cg.Reached + " " + string.Join(",", caps));
                Check("real guide: no camp object or kit in the path; every crafting step's recipe is not grey at its start and uses reagents", cg.Crafting.All(s => !s.Recipe.IsCamp && s.StartColor != SkillColor.Grey && s.Materials.Count > 0));

                // ---- screenshots
                SpellData.SetForTest(set);
                var page = ProfessionsView;
                page.ResetForTest();
                var pc = new List<ProfChar> { RealChar("Elanoran", "HUNTER", book, "Cooking", 112, 150, "First Aid", 150, 150, "Tailoring", 88, 150), RealChar("Mossbeard", "DRUID", book, "Herbalism", 140, 150, "Cooking", 40, 75, "Alchemy", 35, 75) };
                // give them reagents of real Cooking / First Aid / Tailoring recipes
                int k = 0;
                foreach (var r in cooking.Recipes.Where(r => !r.IsCamp).Skip(2).Take(9).Concat(firstAid.Recipes.Take(3)).Concat(tailoring.Recipes.Take(3)))
                    foreach (var g0 in r.Reagents) pc[k++ % 2 == 0 ? 0 : 1].Add(g0.ItemId, g0.Count * (3 + k % 5));
                foreach (var r in cooking.Recipes.Where(r => !r.IsCamp).Take(2)) foreach (var g0 in r.Reagents) pc[0].Add(g0.ItemId, g0.Count * 12);
                foreach (var cl in camp.All) foreach (var g0 in cl.Recipe.Reagents.Take(2)) pc[1].Add(g0.ItemId, g0.Count);
                page.SetProfCharsForTest(pc);
                page.SetSourcesForTest(fs, new FixedGatherWorld());
                ShowTab("professions"); await AtlasView.Open("professions:tailoring");   // 2.85: the menu opens the list; the page is a profession's
                page.ShowForTest("recipes", "Cooking", "Elanoran");
                await ShotWait(300);
                Snapshot(Path.Combine(dir, "professions-recipes.png"));
                Check("real page: Cooking recipes for Elanoran at 112/150 (133 recipes in the data, kits among them)", page.RecipeRowNamesForTest().Count > 100, page.RecipeRowNamesForTest().Count + "");
                page.ShowForTest("craft");
                await ShotWait(200);
                Snapshot(Path.Combine(dir, "professions-craft-now.png"));
                Check("real page: craft now lists recipes from the characters' bags (skill-ups first)", page.CraftRowsForTest().Count >= 5, page.CraftRowsForTest().Count + "");
                page.ShowForTest("guide", "Cooking", "Elanoran");
                page.GuideRangeForTest(1, 300, "all");
                await ShotWait(200);
                Snapshot(Path.Combine(dir, "professions-guide-top.png"));
                page.ScrollForTest(page.GuideShoppingBottomForTest());
                await ShotWait(150);
                Snapshot(Path.Combine(dir, "professions-guide-steps.png"));
                Check("real page: the Cooking guide on the page has the text for the clipboard with the 1-300 steps", page.GuideTextForTest().Contains("Cooking 1–300") && page.GuideTextForTest().Contains("train Journeyman Cooking"));

                // the gathering guide: the real Herbalism profession, ranks and herb items, with sample places (the Classic baseline is not downloaded in the self-test)
                var w = new FixedGatherWorld();
                w.Zones[1] = new ZoneInfo { Id = 1429, Name = "Elwynn Forest", Median = 6, MinLevel = 1, MaxLevel = 11 };
                w.Zones[2] = new ZoneInfo { Id = 1436, Name = "Westfall", Median = 12, MinLevel = 9, MaxLevel = 16 };
                w.Zones[3] = new ZoneInfo { Id = 1431, Name = "Duskwood", Median = 21, MinLevel = 18, MaxLevel = 27 };
                w.Zones[4] = new ZoneInfo { Id = 1425, Name = "The Hinterlands", Median = 44, MinLevel = 40, MaxLevel = 50 };
                int Id(string n) => iset.Client.FirstOrDefault(i => i.Name == n && i.ClassId == 7 && i.SubclassId == 9)?.Id ?? 0;
                w.ByProfession["Herbalism"] = new List<GatherEntity>
                {
                    new GatherEntity { Kind = "obj", Id = 1618, Name = "Peacebloom", Items = { Id("Peacebloom") }, ClassicZones = { [1] = 31, [2] = 6 } },
                    new GatherEntity { Kind = "obj", Id = 1617, Name = "Silverleaf", Items = { Id("Silverleaf") }, ClassicZones = { [1] = 22, [2] = 11 } },
                    new GatherEntity { Kind = "obj", Id = 1619, Name = "Earthroot", Items = { Id("Earthroot") }, ClassicZones = { [2] = 17, [3] = 4 } },
                    new GatherEntity { Kind = "obj", Id = 1620, Name = "Mageroyal", Items = { Id("Mageroyal") }, ClassicZones = { [2] = 13, [3] = 9 }, LodgeZones = { [3] = 120 } },
                    new GatherEntity { Kind = "obj", Id = 1621, Name = "Briarthorn", Items = { Id("Briarthorn") }, ClassicZones = { [3] = 15 } },
                    new GatherEntity { Kind = "obj", Id = 2046, Name = "Dreamfoil", Items = { Id("Dreamfoil") }, ClassicZones = { [4] = 7 }, LodgeZones = { [4] = 36 } },
                };
                page.SetSourcesForTest(fs, w);
                page.ShowForTest("guide", "Herbalism", "Mossbeard");
                page.GuideRangeForTest(1, 300, "all");
                var gg = await page.WaitGatherForTest();
                await Task.Delay(250);
                Check("real page: the Herbalism zone guide (sample places) lists brackets of 50 up to 300 with the real herb names", gg != null && gg.Brackets.Count >= 6 && gg.ToText().Contains("Peacebloom") && gg.Brackets[0].Zones[0].Zone.Name == "Elwynn Forest", gg?.Brackets.Count + "");
                Snapshot(Path.Combine(dir, "professions-guide-gathering.png"));

                page.ShowForTest("camp");
                await ShotWait(250);
                Snapshot(Path.Combine(dir, "professions-camping.png"));
                page.ScrollForTest(900);
                await ShotWait(150);
                Snapshot(Path.Combine(dir, "professions-camping-objects.png"));
                Check("real page: the Camping tab shows 3 kits and 30+ objects", page.CampCardsForTest().Count >= 33, page.CampCardsForTest().Count + "");
                notes.Add("note real game files: read " + read + " ms, professions built " + built + " ms, " + book.BySpell.Count + " recipes, " + taught + " taught by an item, " + camp.Kits.Count + " kits, " + camp.Objects.Count + " camp objects");
            }
            catch (Exception e)
            {
                notes.Add("FAIL real game files: " + e);
                Check("real game files: no exception", false, e.Message);
            }
        }

        static string NameOfItem(int id) => ItemData.Get(id)?.Name ?? "";

        static ProfChar RealChar(string name, string cls, ProfessionBook book, params object[] stuff)
        {
            var c = new ProfChar { Key = "Realm-" + name, Name = name, Class = cls, Realm = "Realm", Level = 40 };
            for (int i = 0; i + 2 < stuff.Length; i += 3) c.Skills[(string)stuff[i]] = new KeyValuePair<int, int>((int)stuff[i + 1], (int)stuff[i + 2]);
            return c;
        }
    }
}
