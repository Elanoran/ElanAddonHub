using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Atlas.Classic;
using ElansAddonHub.Services.Db2;

namespace ElansAddonHub
{
    // --selftest "atlashosts", part 2 (2.85): the Professions list in front of the Professions page. The side menu row, the palette and a bare
    // "professions:" open browse:professions - one row per profession in the house style of the Recipes list (the painted banner, the Primary /
    // Secondary chips, the rows with the skill range, the recipes and the trainers the data knows); a row opens that profession's page
    // (professions:<slug>/recipes, a gatherer without recipes on its guide). Its own small world for a moment (a secondary profession, rank spells,
    // trainers in a Classic baseline), the hosts test's world put back after.
    public partial class MainWindow
    {
        async Task ProfessionListChecks(Action<string, bool, string> Check, string dir, AtlasSet keep, ProfessionsPage pp)
        {
            try
            {
                var spells = new[]
                {
                    new SpellInfo { Id = 4000, Name = "Heavy Linen Bandage", Rank = "Rank 2", Desc = "Heals the target.", Effects = new[] { new EffectInfo { Index = 0, Effect = EffectInfo.CreateItem, ItemType = 108 } } },
                    new SpellInfo { Id = 4100, Name = "Roast Trout", Desc = "Cooks a fish.", Effects = new[] { new EffectInfo { Index = 0, Effect = EffectInfo.CreateItem, ItemType = 4101 } } },
                    new SpellInfo { Id = 4200, Name = "Elixir of Tests", Desc = "Brews an elixir.", Effects = new[] { new EffectInfo { Index = 0, Effect = EffectInfo.CreateItem, ItemType = 4102 } } },
                    new SpellInfo { Id = 2259, Name = "Alchemy", Rank = "Apprentice", Desc = "Allows an alchemist to make potions up to a maximum potential skill of 75." },
                    new SpellInfo { Id = 3101, Name = "Alchemy", Rank = "Journeyman", Desc = "Allows an alchemist to make potions up to a maximum potential skill of 150." },
                }.ToList();
                // Cooking and First Aid are secondary lines: like in the game they have more than a handful of recipes (the book takes a secondary line from 5)
                for (int i = 1; i <= 4; i++)
                {
                    spells.Add(new SpellInfo { Id = 4000 + i * 10, Name = "Bandage " + i, Desc = "Heals.", Effects = new[] { new EffectInfo { Index = 0, Effect = EffectInfo.CreateItem, ItemType = 108 } } });
                    spells.Add(new SpellInfo { Id = 4100 + i * 10, Name = "Dish " + i, Desc = "Food.", Effects = new[] { new EffectInfo { Index = 0, Effect = EffectInfo.CreateItem, ItemType = 4101 } } });
                }
                var skills = new[]
                {
                    new SkillInfo { Id = 129, Name = "First Aid", Category = SkillInfo.CatSecondary },
                    new SkillInfo { Id = 185, Name = "Cooking", Category = SkillInfo.CatSecondary },
                    new SkillInfo { Id = 171, Name = "Alchemy", Category = SkillInfo.CatProfession },
                    new SkillInfo { Id = 182, Name = "Herbalism", Category = SkillInfo.CatProfession },
                    new SkillInfo { Id = 197, Name = "Tailoring", Category = SkillInfo.CatProfession },       // a profession of the game with no recipe in this data: a row all the same
                };
                var abilities = new[]
                {
                    new SkillAbility { Id = 1, Skill = 129, Spell = 4000, MinRank = 40, Green = 60, Gray = 80 },
                    new SkillAbility { Id = 2, Skill = 185, Spell = 4100, MinRank = 10, Green = 30, Gray = 50 },
                    new SkillAbility { Id = 3, Skill = 171, Spell = 4200, MinRank = 1, Green = 20, Gray = 40 },
                    new SkillAbility { Id = 4, Skill = 171, Spell = 2259 }, new SkillAbility { Id = 5, Skill = 171, Spell = 3101 },
                }.ToList();
                var reagents = new Dictionary<int, int[]> { [4000] = new[] { 102, 2 }, [4100] = new[] { 218, 1 }, [4200] = new[] { 102, 1 } };
                for (int i = 1; i <= 4; i++)
                {
                    abilities.Add(new SkillAbility { Id = 10 + i, Skill = 129, Spell = 4000 + i * 10, MinRank = 40 + i * 10, Green = 60 + i * 10, Gray = 80 + i * 10 }); reagents[4000 + i * 10] = new[] { 102, 1 };
                    abilities.Add(new SkillAbility { Id = 20 + i, Skill = 185, Spell = 4100 + i * 10, MinRank = 10 + i * 10, Green = 30 + i * 10, Gray = 50 + i * 10 }); reagents[4100 + i * 10] = new[] { 218, 1 };
                }
                SpellData.SetForTest(new AtlasSet { Spells = new SpellSnapshot(spells, skills, abilities, reagents), Factions = keep.Factions, Details = new ItemDetails() });
                var idx = new ClassicIndex();
                idx.Npcs[1] = new ClassicNpc { Id = 1, Name = "Kylanna", SubName = "Alchemy Trainer", NpcFlags = 0x10 | 0x2, MinLevel = 30, MaxLevel = 30 };
                idx.Npcs[2] = new ClassicNpc { Id = 2, Name = "Lilyssia", SubName = "Alchemy Trainer", NpcFlags = 0x10, MinLevel = 30, MaxLevel = 30 };
                idx.Npcs[3] = new ClassicNpc { Id = 3, Name = "Zarrin", SubName = "Cooking Trainer", NpcFlags = 0x10, MinLevel = 30, MaxLevel = 30 };
                idx.Npcs[4] = new ClassicNpc { Id = 4, Name = "Ulthir", SubName = "Alchemy Supplies", NpcFlags = 0x80, MinLevel = 30, MaxLevel = 30 };     // a vendor, not a trainer
                idx.Npcs[5] = new ClassicNpc { Id = 5, Name = "Old Gil", SubName = "Alchemy Trainer", NpcFlags = 0x2, MinLevel = 30, MaxLevel = 30 };       // the title, but no trainer flag
                ClassicAtlas.Use(idx);
                AtlasBrowse.ForgetForTest();

                // ---- the registry and the ids
                var cat = AtlasCategories.Find(AtlasHosts.Professions);
                var entry = AtlasNav.Entries().FirstOrDefault(e => e.Key == "professions");
                Check("professions list (2.85): Professions is hosted AND listed - the side menu row, the palette and the old ids open browse:professions; the profession pages keep their ids",
                    cat != null && cat.Hosted && cat.Listed && cat.OpenId == "browse:professions" && entry?.OpenId == "browse:professions"
                    && AtlasNav.PageHits("professions").Any(h => h.Id == "browse:professions") && AtlasNav.Upgrade("professions:") == "browse:professions" && AtlasNav.Upgrade("browse:skills/professions") == "browse:professions"
                    && AtlasNav.Upgrade("professions:cooking/recipes") == "professions:cooking/recipes" && AtlasNav.KeyFor("browse:professions") == "professions" && AtlasNav.KeyFor("browse:professions/secondary") == "professions",
                    entry?.OpenId + " " + AtlasNav.Upgrade("professions:"));

                // ---- the list itself
                var r = AtlasBrowse.Run("browse:professions");
                string Row(string name) { var x = r.Rows.FirstOrDefault(y => y.Title == name); return x == null ? "-" : x.Detail + " | " + x.Extra + " | " + x.Link; }
                var alch = r.Rows.FirstOrDefault(x => x.Title == "Alchemy"); var cook = r.Rows.FirstOrDefault(x => x.Title == "Cooking");
                var herb = r.Rows.FirstOrDefault(x => x.Title == "Herbalism"); var aid = r.Rows.FirstOrDefault(x => x.Title == "First Aid");
                Check("professions list: one row per profession of the game data in name order (Alchemy, Cooking, First Aid, Herbalism, and Tailoring that has no recipe here), 'Professions' with '5 professions', the chips Primary 3 and Secondary 2; the side menu's number is the same",
                    !r.NotFound && r.Title == "Professions" && r.Rows.Select(x => x.Title).SequenceEqual(new[] { "Alchemy", "Cooking", "First Aid", "Herbalism", "Tailoring" }) && r.CountText == "5 professions"
                    && r.Children.Select(c => c.Title + " " + c.Count).SequenceEqual(new[] { "Primary 3", "Secondary 2" }) && AtlasNavCounts.Compute(entry) == 5 && AtlasHosts.ProfessionCount() == 5, r.Message + " " + string.Join(",", r.Rows.Select(x => x.Title)) + " / " + string.Join(",", r.Children.Select(c => c.Title + " " + c.Count)));
                Check("professions list: a row says the kind, the skill range from the rank spells and its ranks, the recipes and the trainers the data knows (Alchemy: Primary · skill 1-150 · Apprentice to Journeyman, 1 recipe · 2 trainers - not the vendor, not the one without the trainer flag; Cooking 5 recipes · 1 trainer)",
                    alch != null && alch.Detail == "Primary · skill 1-150 · Apprentice to Journeyman" && alch.Extra == "1 recipe · 2 trainers" && cook != null && cook.Detail.StartsWith("Secondary") && cook.Extra == "5 recipes · 1 trainer"
                    && aid != null && aid.Extra == "5 recipes" && r.Rows.Any(x => x.Title == "Tailoring" && x.Detail == "Primary" && x.Extra == "") && herb != null && herb.Detail.StartsWith("Primary, gathering") && herb.Extra == "gathered, no recipes", Row("Alchemy") + " / " + Row("Cooking") + " / " + Row("First Aid") + " / " + Row("Herbalism"));
                Check("professions list: a row opens its profession's page - professions:alchemy/recipes, professions:first-aid/recipes; a gatherer without recipes opens its guide (professions:herbalism/guide); the profession's icon",
                    alch?.Link == "professions:alchemy/recipes" && aid?.Link == "professions:first-aid/recipes" && herb?.Link == "professions:herbalism/guide" && alch?.Key == "skill:171", Row("Alchemy") + " / " + Row("Herbalism"));
                var sec = AtlasBrowse.Run("browse:professions/secondary"); var most = AtlasBrowse.Run("browse:professions?sort=recipes"); var named = AtlasBrowse.Run("browse:professions?name=aid");
                Check("professions list: the Secondary chip keeps Cooking and First Aid, the name box finds First Aid, 'Most recipes' puts Cooking and First Aid (5) first and the ones with none last; the page says what a row opens",
                    sec.Rows.Select(x => x.Title).SequenceEqual(new[] { "Cooking", "First Aid" }) && named.Rows.Select(x => x.Title).SequenceEqual(new[] { "First Aid" }) && most.Rows.Select(x => x.Title).SequenceEqual(new[] { "Cooking", "First Aid", "Alchemy", "Herbalism", "Tailoring" })
                    && r.Notes.Any(n => n.Contains("opens its own page")), string.Join(",", sec.Rows.Select(x => x.Title)) + " / " + string.Join(",", most.Rows.Select(x => x.Title)));
                ClassicAtlas.Use(null); AtlasBrowse.ForgetForTest();
                var noTr = AtlasBrowse.Run("browse:professions");
                Check("professions list: without the Classic baseline and the game's cache no trainer count is guessed (the rows leave it out, a note says why)",
                    noTr.Rows.All(x => !x.Extra.Contains("trainer")) && noTr.Notes.Any(n => n.Contains("no trainer counts")), string.Join(" | ", noTr.Notes));
                ClassicAtlas.Use(idx); AtlasBrowse.ForgetForTest();

                // ---- the banner: a 4:1 painting in the house style, like the other category banners
                var art = ArtLibrary.Banner("professions"); var bmp = art == null ? null : ArtLibrary.Load(art);
                Check("professions list: the Professions banner exists (banner-professions.jpg) and is 4:1 like the other category banners, small like them",
                    art != null && art.File == "banner-professions.jpg" && bmp != null && Math.Abs((double)bmp.PixelWidth / bmp.PixelHeight - 4) < 0.02 && bmp.PixelWidth == 1680,
                    art == null ? "no entry" : bmp == null ? "not readable" : bmp.PixelWidth + "x" + bmp.PixelHeight);

                // ---- in the window: the side menu row opens the list with its banner; a row opens the profession's page; the crumb goes back to the list
                Width = 1300; Height = 900;
                AtlasView.History.Clear(); await AtlasView.GoStart(); await Task.Delay(100); UpdateLayout();
                var navRow = AtlasView.NavRowForTest("professions");
                if (navRow != null) ClickRow(navRow);
                await AtlasView.WaitForTest(); await Task.Delay(250); UpdateLayout();
                var b = AtlasView.BannerForTest;
                Check("professions list in the window: the Professions row opens browse:professions - the painted banner behind the title, the chips, the 5 rows; the row is lit",
                    navRow != null && AtlasView.IsBrowse && AtlasView.CurrentId == "browse:professions" && b != null && b.Tag as string == "browse-banner|professions" && b.IsVisible
                    && AtlasWalk(b).OfType<System.Windows.Controls.Border>().Any(x => x.Background is ImageBrush ib && ib.ImageSource == bmp)
                    && AtlasView.BrowseListForTest != null && AtlasView.BrowseListForTest.Items.Count == 5 && AtlasView.NavSelectedForTest.SequenceEqual(new[] { "professions" }) && AtlasView.CrumbsForTest == "Atlas > Professions",
                    AtlasView.CurrentId + " banner=" + (b?.Tag ?? "none") + " crumbs=" + AtlasView.CrumbsForTest);
                Snapshot(Path.Combine(dir, "atlas-professions-list.png"));
                int steps = AtlasView.History.Count;
                var cookRow = RowFor("professions:cooking/recipes");
                if (cookRow != null) ClickRow(cookRow);
                await WaitFor(() => AtlasView.IsHosted, 5000); await Task.Delay(300); UpdateLayout();
                Check("professions list in the window: clicking Cooking opens the Professions page on Cooking / Recipes, one step in the history; its crumb Professions goes back to the list",
                    cookRow != null && AtlasView.IsHosted && ReferenceEquals(AtlasView.HostedForTest, pp) && pp.ProfessionNow == "Cooking" && pp.TabNow == "recipes" && AtlasView.CurrentId == "professions:cooking/recipes"
                    && AtlasView.History.Count == steps + 1 && AtlasView.HostedCrumbsForTest == "Atlas > Professions > Cooking" && AtlasView.CrumbLinksForTest().Contains("browse:professions"),
                    AtlasView.CurrentId + " " + AtlasView.HostedCrumbsForTest);
                await AtlasView.Back(); await AtlasView.WaitForTest(); await Task.Delay(150);
                bool backOk = AtlasView.IsBrowse && AtlasView.CurrentId == "browse:professions";
                await AtlasView.Open("professions:"); await AtlasView.WaitForTest(); await Task.Delay(150);
                bool oldOk = AtlasView.IsBrowse && AtlasView.CurrentId == "browse:professions";
                Check("professions list in the window: Back returns to the list; an old bare professions: link (pins, chat, the old rail tab) opens the list too", backOk && oldOk, AtlasView.CurrentId);

                // ---- without spell data the list says so calmly
                SpellData.SetForTest(null, SpellData.State.Disabled); AtlasBrowse.ForgetForTest();
                var none = AtlasBrowse.Run("browse:professions");
                Check("professions list: with the game data switched off the list says so (not found, the reason), nothing thrown", none.NotFound && none.Message.Contains("switched off"), none.Message);
            }
            catch (Exception e) { Check("professions list checks ran to the end", false, e.ToString()); }
            finally
            {
                SpellData.SetForTest(keep); ClassicAtlas.Use(null); AtlasBrowse.ForgetForTest();
            }
        }
    }
}
