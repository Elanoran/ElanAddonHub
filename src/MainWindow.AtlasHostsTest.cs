using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Atlas.Classic;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Professions;
using ElansAddonHub.Services.Talents;

namespace ElansAddonHub
{
    // --selftest "atlashosts": Talents and Professions as hosted categories of the Atlas, on the invented worlds of the talents and Atlas page tests (a Hunter tree and a
    // copy of it as the Mage, three professions with a recipe each): the tiles, the ids and their sub-paths, the pages kept alive and in step with the id, history across
    // item page -> Talents -> Professions, Ctrl+6, the rail without the two buttons, ShowTab, the links the entity pages give, the recipe link inside Professions and
    // back, the search offering the categories by name, the pure id functions, screenshots.
    public partial class MainWindow
    {
        // Talents and Professions live in the Atlas now: a test that used them leaves the Atlas page on the start page for the tests that follow
        void ResetAtlasForTest() { AtlasView.History.Clear(); _ = AtlasView.GoStart(); }

        async Task<string> AtlasHostsTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            void Check(string what, bool cond, string detail = null) { notes.Add((cond ? "ok   " : "FAIL ") + what + (!cond && detail != null ? " [" + detail + "]" : "")); ok &= cond; }
            var work = Path.Combine(dir, "atlashosts");
            var savedLodge = AtlasQuery.Context.Lodge; var savedNames = TalentsPage.NameOf; var savedAuto = TalentData.AutoStart; var savedCache = TalentData.CacheFile;
            var savedSaved = TalentsView.SavedFile; var savedHh = TalentsView.HunterBuildsFile; var savedLink = ProfessionLinks.Last;
            var savedWidth = Width; var h0 = Height;
            try
            {
                if (Directory.Exists(work)) Util.DeleteDir(work);
                Directory.CreateDirectory(work);
                TalentData.AutoStart = false; TalentData.CacheFile = Path.Combine(work, "talentdata.bin");

                // ---- the world: a Hunter tree (and the same tree as the Mage), First Aid / Cooking / Alchemy with a recipe each, a few items
                var tset = TalentParse.All(TalRaw());
                var hunterTree = tset.ForClassFile("HUNTER");
                tset.Trees.Add(new TalentTreeDef { Id = 1092, ClassId = 8, ClassName = "Mage", ClassFile = "MAGE", CurrencyId = hunterTree.CurrencyId, CostPerRank = hunterTree.CostPerRank, MaxPoints = hunterTree.MaxPoints, SpecNames = hunterTree.SpecNames,
                    Nodes = hunterTree.Nodes, Edges = hunterTree.Edges, Groups = hunterTree.Groups, Sources = hunterTree.Sources, Aliases = hunterTree.Aliases });
                TalentData.SetForTest(tset);
                TalentsPage.NameOf = n => "Talent " + n.Id;
                var items = new[]
                {
                    CItem(102, "Linen Cloth", 1, 5, 1, 0, 7, 5, 13), CItem(108, "Heavy Linen Bandage", 1, 20, 1, 0, 0, 1), CItem(218, "Raw Trout", 1, 1, 1, 0, 0, 5), CItem(4101, "Roast Trout", 1, 5, 1, 0, 0, 5),
                    CItem(4102, "Elixir of Tests", 1, 5, 1, 0, 0, 1),
                };
                var spells = new[]
                {
                    new SpellInfo { Id = 100, Name = "Alpha", Rank = "Rank 1", Desc = "The first talent." },
                    new SpellInfo { Id = 4000, Name = "Heavy Linen Bandage", Rank = "Rank 2", Desc = "Heals the target.", Effects = new[] { new EffectInfo { Index = 0, Effect = EffectInfo.CreateItem, ItemType = 108 } } },
                    new SpellInfo { Id = 4100, Name = "Roast Trout", Desc = "Cooks a fish.", Effects = new[] { new EffectInfo { Index = 0, Effect = EffectInfo.CreateItem, ItemType = 4101 } } },
                    new SpellInfo { Id = 4200, Name = "Elixir of Tests", Desc = "Brews an elixir.", Effects = new[] { new EffectInfo { Index = 0, Effect = EffectInfo.CreateItem, ItemType = 4102 } } },
                };
                var skills = new[]
                {
                    new SkillInfo { Id = 129, Name = "First Aid", Category = SkillInfo.CatProfession, Description = "Bandages and anti-venoms." },
                    new SkillInfo { Id = 185, Name = "Cooking", Category = SkillInfo.CatProfession, Description = "Food and drink." },
                    new SkillInfo { Id = 171, Name = "Alchemy", Category = SkillInfo.CatProfession },
                };
                var abilities = new[]
                {
                    new SkillAbility { Id = 1, Skill = 129, Spell = 4000, MinRank = 40, Green = 60, Gray = 80 },
                    new SkillAbility { Id = 2, Skill = 185, Spell = 4100, MinRank = 10, Green = 30, Gray = 50 },
                    new SkillAbility { Id = 3, Skill = 171, Spell = 4200, MinRank = 1, Green = 20, Gray = 40 },
                };
                var reagents = new Dictionary<int, int[]> { [4000] = new[] { 102, 2 }, [4100] = new[] { 218, 1 }, [4200] = new[] { 102, 1 } };
                var sset = new AtlasSet
                {
                    Spells = new SpellSnapshot(spells, skills, abilities, reagents),
                    Factions = new FactionSnapshot(new[] { new FactionInfo { Id = 72, Name = "Stormwind" } }, new FactionTemplateInfo[0]),
                    Details = new ItemDetails(),
                };
                ItemData.SetForTest(new ItemSnapshot(items, new ClientItem[0]), ItemData.State.Ready);
                SpellData.SetForTest(sset);
                SeenData.SetForTest(null, SeenData.State.NotStarted);
                ClassicAtlas.Use(null);
                AtlasBrowse.ForgetForTest();
                AtlasQuery.Context.Lodge = () => AtlasTestLodge();
                var tp = TalentsView; var pp = ProfessionsView;
                tp.ResetPageForTest();
                tp.SavedFile = Path.Combine(work, "saved.json");
                tp.HunterBuildsFile = () => Path.Combine(work, "no-builds.lua");
                pp.ResetForTest();
                pp.SetProfCharsForTest(new ProfChar[0]);
                pp.SetSourcesForTest(PSources(), new FixedGatherWorld());

                // ============================================================================================================ the pure parts
                AtlasHosts.ParseTalents("tree/Hunter", out var cf1, out var code1); AtlasHosts.ParseTalents("build/ETB1-abc", out var cf2, out var code2); AtlasHosts.ParseTalents("", out var cf3, out var code3);
                Check("ids: talents sub-paths - tree/<class> gives the class file in capitals, build/<code> the code as typed, nothing = as it was left",
                    cf1 == "HUNTER" && code1 == "" && cf2 == "" && code2 == "ETB1-abc" && cf3 == "" && code3 == "" && AtlasHosts.TalentsId("HUNTER") == "talents:tree/hunter" && AtlasHosts.TalentsId(null) == "talents:", cf1 + code2);
                AtlasHosts.ParseProfessions("first-aid/guide", out var s1, out var t1); AtlasHosts.ParseProfessions("camp", out var s2, out var t2); AtlasHosts.ParseProfessions("Cooking", out var s3, out var t3); AtlasHosts.ParseProfessions("", out var s4, out var t4);
                Check("ids: professions sub-paths - <profession>/<tab>, a bare profession (tab as it was), a bare tab ('camp'), nothing = as it was left; names become slugs ('First Aid' = first-aid)",
                    s1 == "first-aid" && t1 == "guide" && s2 == "" && t2 == "camp" && s3 == "cooking" && t3 == "" && s4 == "" && t4 == "" && AtlasHosts.Slug("First Aid") == "first-aid"
                    && AtlasHosts.ProfessionsId("First Aid", "recipes") == "professions:first-aid/recipes" && AtlasHosts.ProfessionsId("Cooking", "camp") == "professions:camp" && AtlasHosts.ProfessionsId(null, "craft") == "professions:craft",
                    s1 + t1 + s2 + t2 + s3 + t3);
                var reg = AtlasCategories.Find("talents"); var regP = AtlasCategories.Find("professions");
                Check("registry: the window registered Talents (after Spells) and Professions (after Recipes) as hosted categories with a glyph, a blurb, a breadcrumb title and a search",
                    reg != null && reg.Hosted && regP != null && regP.Hosted && reg.Order > AtlasCategories.Find("spells").Order && reg.Order < AtlasCategories.Find("skills").Order && regP.Order > AtlasCategories.Find("recipes").Order
                    && reg.Glyph.Length > 0 && regP.Glyph.Length > 0 && reg.Blurb.Length > 0 && reg.SubTitle != null && reg.Search != null && regP.SubTitle != null && regP.Search != null && reg.OpenId == "talents:" && AtlasQuery.NormalizeId("talents:tree/hunter") == "talents:tree/hunter");

                // ============================================================================================================ the start page
                ShowTab("atlas");
                AtlasView.History.Clear();
                await AtlasView.GoStart(); await AtlasView.StartFillForTest; await Task.Delay(100); UpdateLayout();
                FrameworkElement Tile(string openId) => AtlasWalk(AtlasView).OfType<ContentControl>().FirstOrDefault(c => c.Tag is string s && s == openId && c.IsVisible);
                var tx = AtlasTexts(AtlasView);
                Check("start page: tiles for Talents and Professions next to Items, Creatures, ... with their blurbs, Talents between Spells and Skills, Professions after Recipes",
                    Tile("talents:") != null && Tile("professions:") != null && Tile("browse:items") != null && tx.Contains("Talents") && tx.Contains("Professions") && tx.Any(t => t.StartsWith("Plan a build"))
                    && tx.IndexOf("Spells") < tx.IndexOf("Talents") && tx.IndexOf("Talents") < tx.IndexOf("Skills and professions") && tx.IndexOf("Recipes") < tx.IndexOf("Professions"), string.Join(" | ", tx.Take(40)));
                Snapshot(Path.Combine(dir, "atlas-hosts-start.png"));

                // ============================================================================================================ opening Talents
                ClickRow(Tile("talents:")); await AtlasView.WaitForTest(); await Task.Delay(150); UpdateLayout();
                Check("talents: the tile opens the calculator inside the Atlas - hosted, the page shown and its own title hidden, Hunter (the default class), the id says tree/hunter, one step in the history",
                    AtlasView.IsHosted && ReferenceEquals(AtlasView.HostedForTest, tp) && tp.IsVisible && tp.TitleHiddenForTest && tp.ClassFileNow == "HUNTER" && tp.ClassTitleForTest == "Hunter" && AtlasView.CurrentId == "talents:tree/hunter"
                    && AtlasView.History.Count == 2 && AtlasView.HostSlotVisibleForTest, AtlasView.CurrentId + " " + AtlasView.IsHosted);
                Check("talents: the breadcrumb reads Atlas > Talents > Hunter, the title is Talents, the Atlas page's own scroller is out of the way", AtlasView.HostedCrumbsForTest == "Atlas > Talents > Hunter" && AtlasTexts(AtlasView).Contains("Talents"), AtlasView.HostedCrumbsForTest);
                Check("talents: the calculator works as before inside the Atlas - nodes drawn, a click spends a point", tp.NodeCountForTest == 7 && tp.Click(10, false) == Why.Ok && tp.SessionForTest.State.Total == 1, tp.NodeCountForTest + "");
                Snapshot(Path.Combine(dir, "atlas-hosts-talents.png"));

                await AtlasView.Open("talents:tree/mage"); await Task.Delay(100); UpdateLayout();
                Check("talents: talents:tree/mage selects the Mage (the id is a step in the history), the breadcrumb follows", tp.ClassFileNow == "MAGE" && tp.ClassTitleForTest == "Mage" && AtlasView.CurrentId == "talents:tree/mage" && AtlasView.HostedCrumbsForTest == "Atlas > Talents > Mage" && AtlasView.History.Count == 3);
                tp.SelectClass("HUNTER");
                Check("talents: picking a class in the page changes the Atlas id without a new history step (like a browse filter) and the breadcrumb", AtlasView.CurrentId == "talents:tree/hunter" && AtlasView.History.Count == 3 && AtlasView.History.Current == "talents:tree/hunter" && AtlasView.HostedCrumbsForTest == "Atlas > Talents > Hunter", AtlasView.CurrentId);
                tp.Click(10, false);
                await AtlasView.Open("talents:tree/hunter"); await Task.Delay(50);
                Check("talents: opening the class that is shown already keeps the points spent (the page is the same, kept alive)", tp.ClassFileNow == "HUNTER" && tp.SessionForTest.State.Total == 1, tp.SessionForTest.State.Total + "");
                var code = tp.ExportCode();
                Check("talents: the build code of the points spent exists", !string.IsNullOrEmpty(code) && code.StartsWith("ETB1-"), code);

                // ============================================================================================================ opening Professions, state of both pages kept
                await AtlasView.Open("professions:first-aid/recipes"); await Task.Delay(300); UpdateLayout();
                Check("professions: professions:first-aid/recipes shows the page on First Aid, Recipes, with no title of its own; the breadcrumb is Atlas > Professions > First Aid",
                    AtlasView.IsHosted && ReferenceEquals(AtlasView.HostedForTest, pp) && pp.IsVisible && pp.TitleHiddenForTest && pp.ProfessionNow == "First Aid" && pp.TabNow == "recipes" && AtlasView.CurrentId == "professions:first-aid/recipes"
                    && AtlasView.HostedCrumbsForTest == "Atlas > Professions > First Aid" && !tp.IsVisible, pp.ProfessionNow + "/" + pp.TabNow + " " + AtlasView.CurrentId + " " + AtlasView.HostedCrumbsForTest + " book=" + string.Join(",", pp.BookForTest?.Professions.Select(p => p.Name) ?? new string[0]));
                Check("professions: the recipe list of First Aid is drawn (Heavy Linen Bandage)", pp.RecipeRowNamesForTest().Contains("Heavy Linen Bandage"), string.Join(",", pp.RecipeRowNamesForTest()));
                await AtlasView.Open("professions:cooking/guide"); await Task.Delay(300); UpdateLayout();
                Check("professions: professions:cooking/guide - Cooking on the Leveling guide tab", pp.ProfessionNow == "Cooking" && pp.TabNow == "guide" && AtlasView.CurrentId == "professions:cooking/guide", pp.ProfessionNow + "/" + pp.TabNow);
                await AtlasView.Open("professions:camp"); await Task.Delay(200);
                Check("professions: professions:camp is the Camping tab", pp.TabNow == "camp" && AtlasView.CurrentId == "professions:camp");
                await AtlasView.Open("professions:alchemy/craft"); await Task.Delay(200);
                Check("professions: professions:alchemy/craft - the Craft now tab (it has no profession picker, the profession is kept in the id)", pp.TabNow == "craft" && AtlasView.CurrentId.StartsWith("professions:") && AtlasView.CurrentId.EndsWith("/craft"), AtlasView.CurrentId);
                pp.PickTabForTest("recipes"); await Task.Delay(100);
                int stepsBefore = AtlasView.History.Count;
                pp.PickProfessionForTest("Cooking"); await Task.Delay(150);
                Check("professions: picking another profession in the page changes the Atlas id (professions:cooking/recipes) and the breadcrumb without a history step",
                    AtlasView.CurrentId == "professions:cooking/recipes" && AtlasView.History.Count == stepsBefore && AtlasView.HostedCrumbsForTest == "Atlas > Professions > Cooking", AtlasView.CurrentId + " " + AtlasView.HostedCrumbsForTest);
                pp.PickTabForTest("guide"); await Task.Delay(150);
                Check("professions: picking another tab changes the id to professions:cooking/guide, in place", AtlasView.CurrentId == "professions:cooking/guide" && AtlasView.History.Count == stepsBefore, AtlasView.CurrentId);
                await AtlasView.Open("professions:nothing/recipes"); await Task.Delay(100);
                Check("professions: a profession the data does not have is not an error - the page stays as it was", AtlasView.IsHosted && pp.ProfessionNow == "Cooking", pp.ProfessionNow);
                await AtlasView.Open("professions:cooking/recipes"); await Task.Delay(300); UpdateLayout();
                Snapshot(Path.Combine(dir, "atlas-hosts-professions.png"));

                // ============================================================================================================ history: item page -> Talents -> Professions and back
                AtlasView.History.Clear();
                await AtlasView.Open("item:108"); await AtlasView.WaitForTest(); UpdateLayout();
                Check("history: an item page is drawn in the page's own scroller (the hosted slot is gone)", AtlasView.Model != null && AtlasView.Model.Id == "item:108" && !AtlasView.IsHosted && !AtlasView.HostSlotVisibleForTest && !pp.IsVisible && !tp.IsVisible);
                await AtlasView.Open("talents:tree/hunter"); await Task.Delay(100);
                tp.SelectClass("MAGE"); tp.Click(10, false); var mageCode = tp.ExportCode();
                await AtlasView.Open("professions:cooking/recipes"); await Task.Delay(300);
                Check("history: item -> Talents (Mage picked in the page) -> Professions are three steps", AtlasView.History.Count == 3 && AtlasView.History.Items[1] == "talents:tree/mage" && AtlasView.History.Items[2] == "professions:cooking/recipes", string.Join(" > ", AtlasView.History.Items));
                await AtlasView.Back(); await Task.Delay(200); UpdateLayout();
                Check("history: Back from Professions returns to Talents on the Mage (the class the user picked there), the same page instance", AtlasView.IsHosted && ReferenceEquals(AtlasView.HostedForTest, tp) && tp.IsVisible && !pp.IsVisible && tp.ClassFileNow == "MAGE" && AtlasView.CurrentId == "talents:tree/mage" && tp.ExportCode() == mageCode, tp.ExportCode());
                await AtlasView.Back(); await AtlasView.WaitForTest(); UpdateLayout();
                Check("history: Back again returns to the item page", !AtlasView.IsHosted && AtlasView.Model != null && AtlasView.Model.Id == "item:108" && !tp.IsVisible);
                await AtlasView.Forward(); await Task.Delay(100); await AtlasView.Forward(); await Task.Delay(300); UpdateLayout();
                Check("history: Forward twice comes back to Professions on Cooking / Recipes", AtlasView.IsHosted && ReferenceEquals(AtlasView.HostedForTest, pp) && pp.ProfessionNow == "Cooking" && pp.TabNow == "recipes" && AtlasView.CurrentId == "professions:cooking/recipes", AtlasView.CurrentId);
                Check("history: the point spent in the Mage's calculator is still there after the round trip (the page is kept alive, not rebuilt)", tp.ExportCode() == mageCode);

                // ============================================================================================================ a recipe link inside Professions
                await AtlasView.Open("professions:cooking/recipes"); await Task.Delay(300); UpdateLayout();
                int before = AtlasView.History.Count;
                var link = AtlasWalk(pp.BodyForTest).OfType<TextBlock>().FirstOrDefault(t => t.Cursor == Cursors.Hand && t.ToolTip is string s && s.Contains("in the Atlas"));
                Check("recipe link: the Cooking recipe list has Atlas links (the recipe's item)", link != null, link?.Text);
                if (link != null) ClickRow(link);
                await WaitFor(() => AtlasView.Model != null && !AtlasView.IsHosted, 5000); await AtlasView.WaitForTest(); UpdateLayout();
                Check("recipe link: clicking it opens its Atlas page in the same Atlas page, as a new step in the history", AtlasView.Model != null && AtlasView.History.Count == before + 1 && !AtlasView.IsHosted && ProfessionLinks.Last != null && ProfessionLinks.Last == AtlasView.CurrentId, AtlasView.CurrentId + " " + ProfessionLinks.Last);
                await AtlasView.Back(); await Task.Delay(300); UpdateLayout();
                Check("recipe link: Back returns to the recipe list of Cooking, as it was", AtlasView.IsHosted && ReferenceEquals(AtlasView.HostedForTest, pp) && pp.ProfessionNow == "Cooking" && pp.TabNow == "recipes" && pp.RecipeRowNamesForTest().Contains("Roast Trout") && AtlasView.CurrentId == "professions:cooking/recipes");

                // ============================================================================================================ the build code in an id
                await AtlasView.Open("talents:build/" + code); await Task.Delay(150); UpdateLayout();
                Check("talents: talents:build/<ETB code> opens the calculator with that build imported (the id keeps the code)", AtlasView.IsHosted && ReferenceEquals(AtlasView.HostedForTest, tp) && tp.ExportCode() == code && AtlasView.CurrentId == "talents:build/" + code && AtlasView.HostedCrumbsForTest == "Atlas > Talents > Build", AtlasView.CurrentId + " " + AtlasView.HostedCrumbsForTest);

                // ============================================================================================================ the rail, the shortcuts, ShowTab
                var railNames = AtlasWalk(this).OfType<RadioButton>().Where(r => r.GroupName == "tabs").Select(r => System.Windows.Automation.AutomationProperties.GetName(r)).ToList();
                Check("rail: no Talents and no Professions button any more, the page hosts are gone from the window; the other buttons are all there",
                    FindName("TabTalents") == null && FindName("TabProfessions") == null && FindName("TalentsView") == null && !railNames.Contains("Talents") && !railNames.Contains("Professions") && railNames.Contains("Atlas") && railNames.Contains("Addons") && railNames.Contains("Browser"), string.Join(",", railNames));
                Check("rail: the Atlas button's tooltip names Ctrl+5 and Talents Ctrl+6", TabAtlas.ToolTip.ToString().Contains("Ctrl+5") && TabAtlas.ToolTip.ToString().Contains("Ctrl+6"));
                ShowTab("addons"); await Task.Delay(50);
                Check("shortcut: with the Addons page open, Ctrl+6 means the Atlas on Talents (the Addons page is showing before)",
                    TabForKey(Key.D6) == "talents" && TabForKey(Key.NumPad6) == "talents" && TabForKey(Key.D5) == "atlas" && AddonsPage.Visibility == Visibility.Visible);
                ShowTab(TabForKey(Key.D6)); await Task.Delay(200); UpdateLayout();
                Check("shortcut: Ctrl+6 opens the Atlas on Talents, the calculator showing, the Addons page away", TabAtlas.IsChecked == true && AtlasView.Visibility == Visibility.Visible && AddonsPage.Visibility != Visibility.Visible && AtlasView.IsHosted && AtlasView.CurrentId.StartsWith("talents:") && tp.IsVisible, AtlasView.CurrentId);
                string keepId = AtlasView.CurrentId; int keepSteps = AtlasView.History.Count;
                ShowTab("addons"); ShowTab("talents"); await Task.Delay(100);
                Check("shortcut: Ctrl+6 again does not reset what the calculator shows (same id, no new history step)", AtlasView.CurrentId == keepId && AtlasView.History.Count == keepSteps, AtlasView.CurrentId);
                ShowTab("addons"); ShowTab("professions"); await Task.Delay(300); UpdateLayout();
                Check("ShowTab('professions') lands in the Atlas on the Professions page (old callers keep working)", TabAtlas.IsChecked == true && AtlasView.IsHosted && ReferenceEquals(AtlasView.HostedForTest, pp) && AtlasView.CurrentId.StartsWith("professions:") && pp.IsVisible && AddonsPage.Visibility != Visibility.Visible && RefreshBar.Visibility != Visibility.Visible, AtlasView.CurrentId);

                // ============================================================================================================ links the entity pages give
                var item = AtlasQuery.Build("item:108");
                var craftedBy = item.Facts.FirstOrDefault(f => f.Label == "Crafted by");
                Check("links: a crafted item names the profession and skill ('Crafted by First Aid 40') and links to professions:first-aid/recipes", craftedBy != null && craftedBy.Value == "First Aid 40" && craftedBy.Link == "professions:first-aid/recipes", craftedBy?.ToString());
                var recipe = AtlasQuery.Build("spell:4100");
                Check("links: the spell page of a recipe has a 'Profession' fact linking to the Cooking recipes", recipe.Facts.Any(f => f.Label == "Profession" && f.Value == "Cooking" && f.Link == "professions:cooking/recipes"), string.Join(" | ", recipe.Facts));
                var talentSpell = AtlasQuery.Build("spell:100");
                Check("links: the spell page of a talent has a 'Talent of' fact linking to talents:tree/hunter", talentSpell.Facts.Any(f => f.Label == "Talent of" && f.Value == "Hunter" && f.Link == "talents:tree/hunter"), string.Join(" | ", talentSpell.Facts));
                var plain = AtlasQuery.Build("spell:4000");
                Check("links: a spell that is no talent has no 'Talent of' fact", !plain.Facts.Any(f => f.Label == "Talent of"));
                var skillPage = AtlasQuery.Build("skill:171");
                Check("links: a profession's skill page has a 'Professions page' fact", skillPage.Facts.Any(f => f.Label == "Professions page" && f.Link == "professions:alchemy/recipes"), string.Join(" | ", skillPage.Facts));
                var skillRows = AtlasBrowse.Run("browse:skills");
                Check("links: in the Skills list the professions open the Professions page (Alchemy, First Aid), other rows keep their skill page", skillRows.Rows.Count >= 3 && skillRows.Rows.Where(r => r.Title == "Alchemy" || r.Title == "First Aid" || r.Title == "Cooking").All(r => r.Link != null && r.Link.StartsWith("professions:")), string.Join(" | ", skillRows.Rows.Select(r => r.Title + "=" + r.Link)));
                var tipLines = tp.TipTextsForTest(10);
                Check("links: a talent's tooltip says Ctrl+click opens its spell page, and Ctrl+click asks the Atlas for spell:100", tipLines.Any(t => t.Contains("Spell ID 100") && t.Contains("Ctrl+click")), string.Join(" | ", tipLines));
                ProfessionLinks.Last = null;
                tp.OpenSpell(10);
                Check("links: OpenSpell on the Alpha talent asks for spell:100", ProfessionLinks.Last == "spell:100", ProfessionLinks.Last);
                await Task.Delay(200); await AtlasView.WaitForTest(); UpdateLayout();
                Check("links: ... and the Atlas page shows that spell, with Back returning to the page the calculator was on", AtlasView.Model != null && AtlasView.Model.Id == "spell:100" && !AtlasView.IsHosted, AtlasView.CurrentId);
                await AtlasView.Back(); await Task.Delay(200);
                Check("links: Back from the talent's spell page returns to the page the user was on", AtlasView.IsHosted && AtlasView.CurrentId.StartsWith("professions:"), AtlasView.CurrentId);

                // ============================================================================================================ search
                var s_cook = AtlasQuery.Search("cooking");
                Check("search: 'cooking' offers the Cooking page of Professions as the top group, before any item or spell", s_cook.Groups.Count > 1 && s_cook.Groups[0].Kind == "page" && s_cook.Groups[0].Hits[0].Id == "professions:cooking/recipes" && s_cook.Groups[0].Hits[0].Name == "Cooking", string.Join(" | ", s_cook.Groups.Select(g => g.Kind + ":" + g.Hits[0].Name)));
                var s_ht = AtlasQuery.Search("hunter talents");
                Check("search: 'hunter talents' offers talents:tree/hunter first", s_ht.Groups.Count > 0 && s_ht.Groups[0].Kind == "page" && s_ht.Groups[0].Hits[0].Id == "talents:tree/hunter", string.Join(" | ", s_ht.Groups.SelectMany(g => g.Hits.Select(h => h.Id))));
                var s_t = AtlasQuery.Search("talents");
                Check("search: 'talents' offers Talents itself, then the class pages", s_t.Groups[0].Kind == "page" && s_t.Groups[0].Hits[0].Id == "talents:" && s_t.Groups[0].Hits.Any(h => h.Id == "talents:tree/mage"), string.Join(" | ", s_t.Groups[0].Hits.Select(h => h.Id)));
                var s_camp = AtlasQuery.Search("camping");
                Check("search: 'camping' offers the Camping tab (professions:camp)", s_camp.Groups.Count > 0 && s_camp.Groups[0].Kind == "page" && s_camp.Groups[0].Hits.Any(h => h.Id == "professions:camp"));
                Check("search: 'professions' and 'first aid' find their pages; a text no page names adds no group", AtlasQuery.Search("professions").Groups[0].Hits[0].Id == "professions:" && AtlasQuery.Search("first aid").Groups[0].Hits.Any(h => h.Id == "professions:first-aid/recipes") && !AtlasQuery.Search("linen cloth").Groups.Any(g => g.Kind == "page"));
                await AtlasView.GoStart(); await Task.Delay(100); UpdateLayout();
                AtlasView.SearchTextForTest = "cooking";
                await WaitFor(() => AtlasView.ResultsOpen && AtlasView.HitsForTest.Count > 0, 5000); await AtlasView.SearchWaitForTest(); UpdateLayout();
                Check("search: in the overlay the first group is 'Atlas pages' with the Cooking row selected", AtlasView.ResultsOpen && AtlasView.ResultsForTest.Groups[0].Title == "Atlas pages" && AtlasView.HitsForTest[0].Id == "professions:cooking/recipes" && AtlasView.SelectedHitForTest == 0 && AtlasTexts(AtlasView).Contains("Atlas pages"));
                Snapshot(Path.Combine(dir, "atlas-hosts-search.png"));
                AtlasView.PressForTest(Key.Enter); await Task.Delay(300); await AtlasView.WaitForTest(); UpdateLayout();
                Check("search: Enter opens the Cooking page of Professions inside the Atlas", AtlasView.IsHosted && ReferenceEquals(AtlasView.HostedForTest, pp) && pp.ProfessionNow == "Cooking" && AtlasView.CurrentId == "professions:cooking/recipes" && !AtlasView.ResultsOpen, AtlasView.CurrentId);

                // ============================================================================================================ without game data the pages still open
                TalentData.SetForTest(null, TalentData.State.Disabled);
                await AtlasView.Open("talents:tree/hunter"); await Task.Delay(150);
                Check("no game data: Talents opens and says so on its own page (no exception, the Atlas stays usable)", AtlasView.IsHosted && tp.EmptyVisibleForTest, tp.EmptyTextForTest);
                Check("no game data: the search still offers Talents and Professions by name (the class pages come with the data)", AtlasQuery.Search("talents").Groups[0].Hits.Any(h => h.Id == "talents:") && AtlasQuery.Search("professions").Groups[0].Hits.Any(h => h.Id == "professions:"));
            }
            catch (Exception e) { Check("atlashosts test ran to the end", false, e.ToString()); }
            finally
            {
                try
                {
                    AtlasQuery.Context.Lodge = savedLodge; TalentsPage.NameOf = savedNames; TalentData.AutoStart = savedAuto; TalentData.CacheFile = savedCache;
                    TalentsView.SavedFile = savedSaved; TalentsView.HunterBuildsFile = savedHh; ProfessionLinks.Last = savedLink;
                    ProfessionLinks.OpenRequested -= OnProfessionLink; ProfessionLinks.OpenRequested += OnProfessionLink;
                    TalentData.ResetForTest(); SpellData.ResetForTest(); ItemData.ResetForTest(); SeenData.ResetForTest(); ClassicAtlas.Use(null); AtlasBrowse.ForgetForTest();
                    ProfessionsView.ResetForTest(); TalentsView.ResetPageForTest();
                    AtlasView.CloseResults(); AtlasView.SearchTextForTest = "";
                    Width = savedWidth; Height = h0;
                    ShowTab("addons"); ResetAtlasForTest();
                }
                catch { }
            }
            return string.Join("\r\n", notes) + "\r\natlashosts: " + (ok ? "ok" : "FAILED");
        }
    }
}
