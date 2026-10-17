using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Atlas.Classic;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Maps;
using ElansAddonHub.Services.Professions;

namespace ElansAddonHub
{
    // --selftest "atlashidden" (2.93): the test / placeholder rule (AtlasHidden) on fixture data - the name rule (what it takes and what it leaves), skill lines
    // nobody can learn (a Survival-like old line hidden; a Comprehension-like line with an ability that comes with it, and a line taught by an item, kept),
    // and every place it applies: the browse lists, their chips and counts, search, the Professions list; a page opened by its id still opens.
    // --selftest "atlasnameless" (2.93): a creature the game has not named yet is "Not named yet" (with the line why) while WoW runs, "NPC <id>" after.
    public partial class MainWindow
    {
        static AtlasSet HiddenTestSpells(bool withEffects = true)
        {
            SpellInfo Make(int id, string name, int item = 0) => new SpellInfo { Id = id, Name = name, Effects = item > 0 ? new[] { new EffectInfo { Index = 0, Effect = EffectInfo.CreateItem, ItemType = item } } : SpellInfo.NoEffects };
            var spells = new[]
            {
                Make(4000, "Heavy Linen Bandage", 108), Make(4001, "Test Bandage", 9303),                // First Aid's real recipe; a test one in Tailoring
                Make(1240345, "Smelt Copper (Test)", 102),                                                 // Test Profession [DNT]
                Make(7359, "Bright Campfire"),                                                             // Survival (old line): reagents only
                Make(1309973, "Comprehend Scroll"), Make(1302508, "Study", 9300),                          // Comprehension: an ability that comes with the line + a recipe
                Make(5000, "Scribe Note", 9301),                                                           // Scribing: taught by an item
                Make(6000, "Bolt of Cloth", 9302),                                                         // Tailoring
                Make(7000, "Test Fire Spell"), Make(7001, "Fireball"),
            };
            var skills = new[]
            {
                new SkillInfo { Id = 129, Name = "First Aid", Category = SkillInfo.CatSecondary },
                new SkillInfo { Id = 2933, Name = "Test Profession [DNT]", Category = SkillInfo.CatProfession },
                new SkillInfo { Id = 142, Name = "Survival", Category = SkillInfo.CatSecondary },
                new SkillInfo { Id = 3012, Name = "Comprehension", Category = SkillInfo.CatSecondary },
                new SkillInfo { Id = 3100, Name = "Scribing", Category = SkillInfo.CatSecondary },
                new SkillInfo { Id = 197, Name = "Tailoring", Category = SkillInfo.CatProfession },
            };
            var abilities = new[]
            {
                new SkillAbility { Id = 1, Skill = 129, Spell = 4000, MinRank = 40, Green = 60, Gray = 80 }, new SkillAbility { Id = 2, Skill = 197, Spell = 4001, MinRank = 1 },
                new SkillAbility { Id = 3, Skill = 2933, Spell = 1240345, MinRank = 1 },
                new SkillAbility { Id = 4, Skill = 142, Spell = 7359, MinRank = 1 },
                new SkillAbility { Id = 5, Skill = 3012, Spell = 1309973, MinRank = 1, Acquire = 1 }, new SkillAbility { Id = 6, Skill = 3012, Spell = 1302508, MinRank = 1 },
                new SkillAbility { Id = 7, Skill = 3100, Spell = 5000, MinRank = 1 },
                new SkillAbility { Id = 8, Skill = 197, Spell = 6000, MinRank = 1 },
            };
            var reagents = new Dictionary<int, int[]> { [4000] = new[] { 102, 2 }, [7359] = new[] { 102, 1 }, [1302508] = new[] { 102, 1 } };
            var details = new ItemDetails { Effects = withEffects ? new Dictionary<int, int[]> { [9101] = new[] { 6, 5000 } } : null };
            return new AtlasSet
            {
                Spells = new SpellSnapshot(spells, skills, abilities, reagents),
                Factions = new FactionSnapshot(new FactionInfo[0], new FactionTemplateInfo[0]),
                Details = details,
            };
        }

        static ItemSnapshot HiddenTestItems() => new ItemSnapshot(new[]
        {
            CItem(100, "Gnarlfang's Claw", 3, 14, 10, 13, 2, 7), CItem(102, "Linen Cloth", 1, 5, 1, 0, 7, 5), CItem(108, "Heavy Linen Bandage", 1, 20, 1, 0, 0, 1),
            CItem(9001, "Deprecated Sword", 1, 5, 1, 13, 2, 7), CItem(9002, "[PH] Placeholder Helm", 1, 5, 1, 1, 4, 1), CItem(9003, "zzOLD Belt", 1, 5, 1, 6, 4, 1),
            CItem(9004, "Test Sword", 1, 5, 1, 13, 2, 7), CItem(9005, "High Test Eternium Fishing Line", 1, 5, 1, 0, 7, 0), CItem(9006, "Testament of Hope", 1, 5, 1, 0, 15, 0),
            CItem(9007, "TEST SWORD 2", 1, 5, 1, 13, 2, 7), CItem(9008, "Old Blanchy's Blanket", 1, 5, 1, 0, 15, 0), CItem(9009, "Hidden Belt (DNT)", 1, 5, 1, 6, 4, 1),
            CItem(9100, "Pattern: Bolt", 1, 5, 1, 0, 9, 2), CItem(9101, "Scribing Tome", 1, 5, 1, 0, 9, 0), CItem(9300, "Bundle of Scrolls", 1, 5, 1, 0, 0, 0),
            CItem(9301, "Note", 1, 5, 1, 0, 15, 0), CItem(9302, "Bolt", 1, 5, 1, 0, 7, 5), CItem(9303, "Test Bandage", 1, 5, 1, 0, 0, 1),
        }, new ClientItem[0]);

        static SeenSnapshot HiddenTestSeen() => new SeenSnapshot(
            new[] { new WdbCreature { Id = 900, Name = "Zzarc' Vul", Type = 7, Rank = 4 }, new WdbCreature { Id = 901, Name = "silithus test mob", Type = 7 }, new WdbCreature { Id = 902, Name = "Wolf", Type = 1 } },
            new[] { new WdbObject { Id = 950, Name = "TEST Ship", Type = 3 }, new WdbObject { Id = 951, Name = "Barrel of Apples", Type = 3 } },
            new[] { new WdbQuest { Id = 970, Title = "Test of Lore", Level = 25 }, new WdbQuest { Id = 971, Title = "TEST QUEST", Level = 1 }, new WdbQuest { Id = 972, Title = "The Test of Righteousness", Level = 22 } });

        async Task<string> AtlasHiddenTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            void Check(string what, bool cond, string detail = null) { SelfTestClock.Mark(what); notes.Add((cond ? "ok   " : "FAIL ") + what + (!cond && detail != null ? " [" + detail + "]" : "")); ok &= cond; }
            var savedLodge = AtlasQuery.Context.Lodge; var savedCairn = CairnAtlas.Current;
            try
            {
                await Task.Yield();
                // ============================================================================================================ the name rule
                var hide = new[] { "Test Profession [DNT]", "[PH] Placeholder Helm", "Deprecated Sword", "DEPRECATED Shot", "zzOLD Belt", "zzOLDCodex of Prayer", "ZZOLD Boots", "OLD Stormwind Seasoning Salts", "QA Tabard of Weighted Dice",
                    "Word of Recall (OLD)", "Swim Speed (TEST)", "UNUSED Ring", "Unused Gloves", "Test Sword", "TEST SWORD 2", "Eric Test Item A", "Level 60 Test Gear Mail - Hunter", "silithus test mob", "Test", "test",
                    "Hidden Belt (DNT)", "DNT Kill Credit", "Gnomish Weather Machine NYI", "Conjure Furis Felsteed DUMMY DND", "PLACEHOLDER_ASHBRINGER_QUESTITEM_01", "Quest Reward Placeholder", "DEBUG Samophlange Manual Page",
                    "Greatly Improved Weapon Enhancements [DOES NOT WORK]", "<DNT> Senna's Test Spell", "TestBoots - Puffed Mail Green", "TESTT", "Shane Test (DELETE ME)" };
                var keep = new[] { "Test of Lore", "Test of Faith", "Test of Endurance", "The Test of Righteousness", "High Test Eternium Fishing Line", "Testament of Hope", "Testing Totem", "Testing an Enemy's Strength",
                    "Zzarc' Vul", "Old Blanchy's Blanket", "Target Dummy", "Advanced Target Dummy", "Gold Dust", "Placeholder-free Robe".Replace("Placeholder-free ", ""), "Tari'qa", "[TMP] Satyrbane", "Cloth Armor", "", null };
                Check("names: the developer leftovers are hidden ([DNT] [PH] Deprecated zzOLD OLD QA (OLD) (TEST) UNUSED, Test words, DNT / NYI / DUMMY in capitals, PLACEHOLDER, DEBUG, [CAPITALS])",
                    hide.All(AtlasHidden.IsHiddenName), string.Join(", ", hide.Where(n => !AtlasHidden.IsHiddenName(n))));
                Check("names: real names that look alike stay (Test of Lore, The Test of Righteousness, High Test Eternium Fishing Line, Testament, Zzarc' Vul, Target Dummy, [TMP] ...)",
                    keep.All(n => !AtlasHidden.IsHiddenName(n)), string.Join(", ", keep.Where(AtlasHidden.IsHiddenName)));
                Check("names: the rule says why (for the real-data list)", AtlasHidden.WhyName("Test Profession [DNT]") == "tag [DNT]" && AtlasHidden.WhyName("zzOLD Belt") == "prefix zz" && AtlasHidden.WhyName("Test Sword") == "test name" && AtlasHidden.WhyName("Wolf Pelt") == "");

                // ============================================================================================================ the fixture world
                ItemData.SetForTest(HiddenTestItems(), ItemData.State.Ready);
                SpellData.SetForTest(HiddenTestSpells());
                SeenData.SetForTest(HiddenTestSeen());
                MapData.SetForTest(new MapIndex(AtlasTestMapSet()));
                ClassicAtlas.Use(null);
                AtlasQuery.Context.Lodge = () => null; CairnAtlas.SetForTest(null);
                AtlasHidden.ForgetForTest(); AtlasBrowse.ForgetForTest();
                var sn = SpellData.Spells;
                string Why(int id) => AtlasHidden.WhySkill(sn.Skills[id]);
                Check("skills: Test Profession [DNT] by its name; Survival (an old line: no ability that comes with it, no teaching item, not a profession) because nobody can learn it",
                    Why(2933) == "tag [DNT]" && Why(142) == AtlasHidden.NobodyLearns, Why(2933) + " | " + Why(142));
                Check("skills: kept - First Aid (a profession), Comprehension (an ability comes with the line), Scribing (an item teaches its recipe), Tailoring",
                    Why(129) == "" && Why(3012) == "" && Why(3100) == "" && Why(197) == "", Why(129) + "|" + Why(3012) + "|" + Why(3100) + "|" + Why(197));
                SpellData.SetForTest(HiddenTestSpells(withEffects: false)); AtlasHidden.ForgetForTest();
                Check("skills: without the item effects (not read) nothing is hidden on half the facts - Survival stays", AtlasHidden.WhySkill(SpellData.Spells.Skills[142]) == "" && AtlasHidden.WhySkill(SpellData.Spells.Skills[3100]) == "");
                SpellData.SetForTest(HiddenTestSpells()); AtlasHidden.ForgetForTest(); AtlasBrowse.ForgetForTest();
                sn = SpellData.Spells;

                // ============================================================================================================ the lists, chips, counts
                var items = AtlasBrowse.Run("browse:items");
                var itemTitles = items.Rows.Select(r => r.Title).ToList();
                Check("items list: no hidden entry, the real look-alikes listed, the count is the rows (18 items - 7 hidden = 11)",
                    !itemTitles.Any(AtlasHidden.IsHiddenName) && itemTitles.Contains("High Test Eternium Fishing Line") && itemTitles.Contains("Testament of Hope") && itemTitles.Contains("Old Blanchy's Blanket") && items.Total == 11 && items.Total == itemTitles.Count,
                    items.Total + ": " + string.Join(", ", itemTitles));
                var rec = AtlasBrowse.Run("browse:recipes");
                var chips = rec.Children.Select(c => c.Title).ToList();
                Check("recipes list: no Test Profession [DNT] or Survival chip; Comprehension, Scribing, First Aid, Tailoring stay", !chips.Contains("Test Profession [DNT]") && !chips.Contains("Survival") && new[] { "Comprehension", "Scribing", "First Aid", "Tailoring" }.All(chips.Contains), string.Join(",", chips));
                Check("recipes list: no Smelt Copper (Test) / Bright Campfire / Test Bandage rows; the count says 4", !rec.Rows.Any(r => r.Title == "Copper Bar" || r.Title == "Linen Cloth" || r.Title == "Test Bandage" || r.Title == "Bright Campfire") && rec.Total == 4,
                    rec.Total + ": " + string.Join(", ", rec.Rows.Select(r => r.Title)));
                var cat = AtlasCategories.Find("recipes");
                Check("menu number: the side menu's count of Recipes is the listed 4", cat != null && AtlasBrowse.Count(cat) == 4, cat == null ? "no category" : AtlasBrowse.Count(cat) + "");
                var cr = AtlasBrowse.Run("browse:creatures"); var ob = AtlasBrowse.Run("browse:objects"); var qs = AtlasBrowse.Run("browse:quests");
                Check("creatures / objects / quests: silithus test mob, TEST Ship and TEST QUEST are left out; Zzarc' Vul, the barrel, Test of Lore and The Test of Righteousness stay",
                    cr.Rows.Any(r => r.Title == "Zzarc' Vul") && !cr.Rows.Any(r => r.Title == "silithus test mob") && cr.Total == 2 && ob.Rows.Any(r => r.Title == "Barrel of Apples") && !ob.Rows.Any(r => r.Title == "TEST Ship")
                    && qs.Rows.Any(r => r.Title == "Test of Lore") && qs.Rows.Any(r => r.Title == "The Test of Righteousness") && !qs.Rows.Any(r => r.Title == "TEST QUEST"),
                    string.Join(",", cr.Rows.Select(r => r.Title)) + " | " + string.Join(",", ob.Rows.Select(r => r.Title)) + " | " + string.Join(",", qs.Rows.Select(r => r.Title)));
                var sk = AtlasBrowse.Run("browse:skills");
                Check("skills list: no Test Profession / Survival rows", !sk.Rows.Any(r => r.Title == "Test Profession [DNT]" || r.Title == "Survival"), string.Join(",", sk.Rows.Select(r => r.Title)));

                // ============================================================================================================ search
                var s1 = AtlasQuery.Search("test");
                Check("search \"test\": only the real ones (Test of Lore, The Test of Righteousness, High Test ..., Testament ...), nothing hidden",
                    !s1.All.Any(h => AtlasHidden.IsHiddenName(h.Name)) && s1.All.Any(h => h.Name == "Test of Lore") && s1.All.Any(h => h.Name == "High Test Eternium Fishing Line") && !s1.All.Any(h => h.Name == "Test Fire Spell"),
                    string.Join(", ", s1.All.Select(h => h.Name)));
                var s2 = AtlasQuery.Search("survival");
                Check("search \"survival\": the hidden skill line is not offered", !s2.All.Any(h => h.Kind == "skill" && h.Name == "Survival"), string.Join(", ", s2.All.Select(h => h.Kind + ":" + h.Name)));
                Check("search \"zzarc\": the real creature is found", AtlasQuery.Search("zzarc").All.Any(h => h.Name == "Zzarc' Vul"));

                // ============================================================================================================ the Professions list
                var book = ProfessionBook.Build(sn, ItemData.Get, SpellData.Data?.Details?.Effects);
                var profs = book.Professions.Select(p => p.Name).ToList();
                Check("Professions: no Test Profession [DNT], no Survival; Tailoring there", !profs.Contains("Test Profession [DNT]") && !profs.Contains("Survival") && profs.Contains("Tailoring"), string.Join(",", profs));
                var tl = book.ByName("Tailoring");
                Check("Professions: Tailoring's recipes leave out the Test Bandage", tl != null && tl.Recipes.Any(r => r.Name == "Bolt") && !tl.Recipes.Any(r => r.Name == "Test Bandage"), tl == null ? "no Tailoring" : string.Join(",", tl.Recipes.Select(r => r.Name)));
                var pl = AtlasHosts.ProfessionList();
                Check("Professions names (the hosted page's list): no Test Profession, no Survival", !pl.Contains("Test Profession [DNT]") && !pl.Contains("Survival") && pl.Contains("Tailoring"), string.Join(",", pl));

                // ============================================================================================================ a page by its id
                var page = AtlasQuery.Build("item:9001");
                Check("a page opened by its id still opens (a link or a pasted id): Deprecated Sword", !page.NotFound && page.Title == "Deprecated Sword", page.Title);
            }
            catch (Exception e) { Check("atlashidden: no exception", false, e.ToString()); }
            finally
            {
                try
                {
                    AtlasQuery.Context.Lodge = savedLodge; CairnAtlas.SetForTest(savedCairn);
                    ClassicAtlas.Use(null); ItemData.ResetForTest(); SpellData.ResetForTest(); SeenData.ResetForTest(); MapData.ResetForTest(); AtlasBrowse.ForgetForTest(); AtlasHidden.ForgetForTest();
                    ResetAtlasForTest();
                }
                catch { }
            }
            return string.Join("\r\n", notes) + "\r\natlashidden: " + (ok ? "ok" : "FAILED");
        }

        async Task<string> AtlasNamelessTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            void Check(string what, bool cond, string detail = null) { SelfTestClock.Mark(what); notes.Add((cond ? "ok   " : "FAIL ") + what + (!cond && detail != null ? " [" + detail + "]" : "")); ok &= cond; }
            var savedLodge = AtlasQuery.Context.Lodge; var savedWow = AtlasQuery.Context.WowRunning; var savedNow = AtlasQuery.Context.NowUtc;
            var savedTiles = MapRender.TileSourceForTest; var savedMapDir = MapRender.DirOverride;
            var work = Path.Combine(dir, "atlasnameless");
            try
            {
                if (Directory.Exists(work)) Util.DeleteDir(work);
                Directory.CreateDirectory(work);
                MapRender.DirOverride = Path.Combine(work, "maps");
                MapRender.TileSourceForTest = fdid => Solid(256, 256, 70, 96, 60);
                ItemData.SetForTest(AtlasTestItems(), ItemData.State.Ready);
                SpellData.SetForTest(AtlasTestSpells());
                MapData.SetForTest(new MapIndex(AtlasTestMapSet()));
                ClassicAtlas.Use(null);
                AtlasQuery.Context.NowUtc = () => AtUtc(AtNow);
                // a new Forever creature: the game has met it (the cache row has no name yet), the Lodge looted it
                SeenSnapshot Seen(string name) => new SeenSnapshot(new[] { new WdbCreature { Id = 70, Name = name, Type = 1 }, new WdbCreature { Id = 1, Name = "Gnarlfang", Type = 1 } }, new WdbObject[0], new WdbQuest[0]);
                var lodge = new LodgeAtlas();
                lodge.Apply(Json.Obj("{\"t\":\"atlas.data\",\"epoch\":\"e1\",\"rev\":1,\"at\":" + (AtNow - 600) + ",\"full\":true,\"kills\":{\"70\":{\"n\":40,\"m\":2,\"items\":{\"100\":{\"n\":4,\"c\":4}},\"z\":{\"11\":40}}}}"));
                AtlasQuery.Context.Lodge = () => lodge;
                SeenData.SetForTest(Seen(""));
                bool wow = true;
                AtlasQuery.Context.WowRunning = () => wow;
                AtlasBrowse.ForgetForTest();
                var p = AtlasQuery.Build("npc:70");
                Check("WoW running: the nameless creature's page is \"Not named yet\" with the line that the name comes when the game closes; its id stays in the subtitle",
                    p.Title == AtlasQuery.NotNamedYet && p.TitleNote == AtlasQuery.NotNamedLine && p.Subtitle.StartsWith("NPC 70"), p.Title + " | " + p.TitleNote + " | " + p.Subtitle);
                Check("the words: \"Not named yet\" / \"The name appears after you close the game (WoW saves its cache then).\"", AtlasQuery.NotNamedYet == "Not named yet" && AtlasQuery.NotNamedLine == "The name appears after you close the game (WoW saves its cache then).");
                var src = AtlasQuery.Build("item:100").Section("sources");
                var row = src?.Rows.Concat(src.Hidden).FirstOrDefault(r => r.Key == "npc:70");
                Check("WoW running: lists use the same friendly title (the item's sources: the Lodge's drop from it)", row != null && row.Title == AtlasQuery.NotNamedYet && AtlasQuery.NpcName(70) == AtlasQuery.NotNamedYet, row?.Title);
                Check("a named creature is not touched", AtlasQuery.Build("npc:1").Title == "Gnarlfang" && AtlasQuery.Build("npc:1").TitleNote == "");
                ShowTab("atlas");
                await AtlasView.Open("npc:70"); await AtlasView.WaitForTest(); await Task.Delay(150);
                Check("in the window: the page's title and the quiet line under it", AtlasView.PageTitleForTest == AtlasQuery.NotNamedYet && AtlasView.TitleNoteForTest == AtlasQuery.NotNamedLine, AtlasView.PageTitleForTest + " | " + AtlasView.TitleNoteForTest);
                await ShotWait(300); if (SelfTestClock.ShotsOn) Snapshot(Path.Combine(dir, "atlasnameless-running.png"));
                wow = false;
                var p2 = AtlasQuery.Build("npc:70");
                Check("WoW closed, the cache still without a name: \"NPC 70\" and no line (there is nothing to wait for)", p2.Title == "NPC 70" && p2.TitleNote == "" && AtlasQuery.NpcName(70) == "NPC 70", p2.Title + " | " + p2.TitleNote);
                SeenData.SetForTest(Seen("Ashmane Prowler"));
                AtlasBrowse.ForgetForTest();
                var p3 = AtlasQuery.Build("npc:70");
                Check("WoW closed and its cache read: the real name, no line", p3.Title == "Ashmane Prowler" && p3.TitleNote == "" && AtlasQuery.NpcName(70) == "Ashmane Prowler", p3.Title);
                wow = true;
                Check("a name once known stays even while WoW runs again", AtlasQuery.Build("npc:70").Title == "Ashmane Prowler");
                await AtlasView.Open("npc:70"); await AtlasView.WaitForTest(); await Task.Delay(150);
                Check("in the window: the real name and no line", AtlasView.PageTitleForTest == "Ashmane Prowler" && AtlasView.TitleNoteForTest == null, AtlasView.PageTitleForTest + " | " + AtlasView.TitleNoteForTest);
            }
            catch (Exception e) { Check("atlasnameless: no exception", false, e.ToString()); }
            finally
            {
                try
                {
                    AtlasQuery.Context.Lodge = savedLodge; AtlasQuery.Context.WowRunning = savedWow; AtlasQuery.Context.NowUtc = savedNow;
                    ItemData.ResetForTest(); SpellData.ResetForTest(); SeenData.ResetForTest(); MapData.ResetForTest(); AtlasBrowse.ForgetForTest();
                    MapRender.TileSourceForTest = savedTiles; MapRender.DirOverride = savedMapDir; MapRender.ForgetFailuresForTest();
                    ResetAtlasForTest();
                    ShowTab("addons");
                }
                catch { }
            }
            return string.Join("\r\n", notes) + "\r\natlasnameless: " + (ok ? "ok" : "FAILED");
        }
    }
}
