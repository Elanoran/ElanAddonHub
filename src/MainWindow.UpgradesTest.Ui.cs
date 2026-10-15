using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Atlas.Classic;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Upgrades;

namespace ElansAddonHub
{
    // --selftest "upgrades", part 2: the panel in the window (fed by an ElansHub.lua of the invented characters), and part 3: the real game files (read-only).
    // Screenshots of the invented data go to the folder named by ELANSHUB_UPGRADES_SHOTS (or the test's own scratch folder).
    public partial class MainWindow
    {
        async Task UpgradesUiChecks(CheckFn Check, string dir, long now, List<InvChar> chars, HubModel hubModel)
        {
            var root = Path.Combine(dir, "upgradeswow");
            if (Directory.Exists(root)) Util.DeleteDir(root);
            var sv = Path.Combine(root, "_classic_beta_", "WTF", "Account", "CHARS", "SavedVariables");
            Directory.CreateDirectory(sv);
            Directory.CreateDirectory(Path.Combine(root, "_classic_beta_", "Interface", "AddOns"));
            var hubFile = Path.Combine(sv, "ElansHub.lua");
            var lua = UpHubLua(now);
            File.WriteAllText(hubFile, lua, new UTF8Encoding(false));
            HubData.ResetCache();
            string stored = null;
            InventoryView.UseUpgradeSettings(() => stored, v => stored = v);
            InventoryView.Init(() => root);
            string opened = null;
            Action<string> onOpen = id => opened = id;
            InventoryView.OpenAtlas += onOpen;
            var shots = Environment.GetEnvironmentVariable("ELANSHUB_UPGRADES_SHOTS");
            string Shot(string name) => Path.Combine(!string.IsNullOrEmpty(shots) && Directory.Exists(shots) ? shots : dir, "upgrades-" + name + ".png");
            try
            {
                Width = 1320; Height = 1040;
                TabInventory.IsChecked = true;
                for (int i = 0; i < 40 && InventoryView.CharCountForTest != 5; i++) await Task.Delay(200);
                Check("panel fixture: the page lists the five invented characters", InventoryView.CharCountForTest == 5, InventoryView.CharCountForTest.ToString());
                async Task Ready()
                {
                    for (int i = 0; i < 80 && !InventoryView.UpgradeReadyForTest; i++) await Task.Delay(100);
                    await Task.Delay(150);
                }
                async Task Gear(string key)
                {
                    InventoryView.FilterForTest(key);
                    InventoryView.SelectTabForTest("gear");
                    await Ready();
                }
                await Gear("Forever-Aela");
                var lines = InventoryView.UpgradeLinesForTest();
                Check("panel: the Gear tab has an Upgrades card under the paper doll, fed off the UI thread (it was 'looking for upgrades' first), for Aela: Hunter, Marksmanship from her talents",
                    InventoryView.UpgradePanelForTest && InventoryView.UpgradeReadyForTest && lines.Any(l => l.StartsWith("upinfo|Hunter|Marksmanship|talents|")), string.Join(" ; ", lines.Take(3)));
                Check("panel: the paper doll and the stats are still there next to it (Aela's five worn items, the doll's 19 slots)", InventoryView.CharTabLinesForTest().Count(x => x.StartsWith("slot|")) == 5 && InventoryView.DollLayoutForTest().Count == 19);
                Check("panel: slots with upgrades get a block with the worn item - Head (wearing Frayed Cap) 2 items, Chest (empty) 2, Ring 1 (Copper Band), Ring 2 (Silver Band), the weapons, the bow",
                    lines.Contains("upslot|1|Head|Frayed Cap|2|0") && lines.Contains("upslot|5|Chest||2|0") && lines.Contains("upslot|11|Ring 1|Copper Band|2|0") && lines.Contains("upslot|12|Ring 2|Silver Band|2|0") && lines.Contains("upslot|16|Main hand|Hunter's Knife|3|0") && lines.Any(l => l.StartsWith("upslot|18|Ranged|Short Bow|1|0")),
                    string.Join(" ; ", lines.Where(l => l.StartsWith("upslot|"))));
                Check("panel: a slot without anything better has no block (neck, shoulders ...)", !InventoryView.UpgradeBlockVisibleForTest(2) && !InventoryView.UpgradeBlockVisibleForTest(3) && InventoryView.UpgradeBlockVisibleForTest(1));
                Check("panel: the rows - icon, name, score, the words, the best source: 'Ranger's Cowl +182% ... Vendor · Quartermaster Voss ...'; the crown first (+301%) as its gain is bigger",
                    lines.Contains("up|1|101|Ranger's Cowl|+182%|+6 Agility, +3 Stamina, +24 armor (score +182%)|Vendor · Quartermaster Voss · Greenvale · 1g 20s 50c (Lodge)") && lines.Contains("up|1|105|Crypt Crown|+301%|+10 Agility, +5 Stamina, +34 armor (score +301%)|Drop · Crypt Lord · Crypt of Bones · 15% (Lodge)")
                    && lines.IndexOf(lines.First(l => l.StartsWith("up|1|105|"))) < lines.IndexOf(lines.First(l => l.StartsWith("up|1|101|"))), string.Join(" ; ", lines.Where(l => l.StartsWith("up|1|"))));
                Check("panel: an empty slot says 'new' and 'empty slot' with the item's stats; a craft says who can make it", lines.Contains("up|5|111|Studded Leather Vest|new|+8 Agility, +5 Stamina, +40 armor (empty slot)|Crafted · Leatherworking 110 · You can make it"), string.Join(" ; ", lines.Where(l => l.StartsWith("up|5|"))));
                Check("filters: dungeons on, raids off, Classic baseline on, only tradeable off, coming soon off - as the defaults say", InventoryView.UpgradeSwitchForTest("dungeons") && !InventoryView.UpgradeSwitchForTest("raids") && InventoryView.UpgradeSwitchForTest("classic") && !InventoryView.UpgradeSwitchForTest("tradeable") && !InventoryView.UpgradeSwitchForTest("soon"));
                await Task.Delay(300);
                Snapshot(Shot("gear-window"));
                SnapshotSheet(InventoryView.UpgradeCardForTest, Shot("panel"));

                // ---- the tooltip: the game's tooltip, then the comparison with the worn item in green and red
                var tip = InventoryView.UpgradeTipForTest(1, 101);
                Check("tooltip: the item as the game shows it (name, item level, slot), then 'Compared with Frayed Cap (worn)', the three changes, the score and where to get it; the Atlas hint at the end",
                    tip.Count > 8 && tip[0] == "Ranger's Cowl" && tip.Contains("Item Level 22") && tip.Contains("Compared with Frayed Cap (worn)") && tip.Contains("+6 Agility") && tip.Contains("+3 Stamina") && tip.Contains("+24 armor") && tip.Contains("Score +182%")
                    && tip.Contains("Where to get it") && tip.Contains("Vendor · Quartermaster Voss · Greenvale · 1g 20s 50c (Lodge)") && tip.Last() == "Click to open it in the Atlas", string.Join(" | ", tip));
                var colors = InventoryView.UpgradeTipColorsForTest(11, 122).Where(k => k.Key.StartsWith("+8 ") || k.Key.StartsWith("-3 ")).ToList();
                Check("tooltip: better stats are green, worse are red (Gold Band over the Copper Band: +8 Agility green, -3 Stamina red)", colors.Count == 3 && colors.Last(k => k.Key == "+8 Agility").Value && !colors.First(k => k.Key == "-3 Stamina").Value, string.Join(" | ", colors.Select(k => k.Key + ":" + k.Value)));
                Check("tooltip: an empty slot is compared with nothing ('Nothing is worn in this slot')", InventoryView.UpgradeTipForTest(5, 111).Contains("Nothing is worn in this slot"));
try                {                    var tgt = InventoryView.UpgradeTipControlForTest(11, 122);                    if (tgt != null) { tgt.Measure(new Size(400, 2000)); tgt.Arrange(new Rect(tgt.DesiredSize)); SnapshotSheet(tgt, Shot("tooltip")); }                }                catch (Exception) { }

                // ---- click an item: its Atlas page
                InventoryView.UpgradeClickRowForTest(1, 101);
                Check("click: a row opens the item in the Atlas (item:101)", opened == "item:101", opened);

                // ---- enchants and food in the panel
                lines = InventoryView.UpgradeLinesForTest();
                Check("enchants: each slot block says what is on the worn item - 'No enchant' (a flagged, warm line) on Aela's cap, main hand and bow, where something can be put",
                    lines.Contains("upench|1|No enchant") && lines.Contains("upench|16|No enchant") && lines.Contains("upench|18|No enchant") && !lines.Any(l => l.StartsWith("upench|2|")), string.Join(" ; ", lines.Where(l => l.StartsWith("upench"))));
                Check("enchants: rows under the item upgrades - the cap's Swiftness (a formula she cannot make yet: 'learn it first'), the scope for the bow ('new', +7 weapon damage, a Classic vendor), the Impact formula for the dagger",
                    lines.Any(l => l.StartsWith("upe|1|7007|Enchant Head - Swiftness|new|+8 Agility (no enchant, also has an effect that is not scored)|Crafted · Enchanting 160 · learn it first")) && lines.Any(l => l.StartsWith("upe|18|601|Crude Scope|new|+7 weapon damage (no enchant)|Vendor · Peddler Nim · Greenvale (Classic)") && l.EndsWith("|classic"))
                    && lines.Any(l => l.StartsWith("upe|16|7004|Enchant Weapon - Impact|")), string.Join(" ; ", lines.Where(l => l.StartsWith("upe|"))));
                var etip = InventoryView.UpgradeExtraTipForTest("e1:7007");
                Check("enchant tooltip: the name, 'Enchanting', the stat in green, what is on the item now, how the formula is learned, the reagent with what I own, who can apply it, the unscored note, the Atlas hint",
                    etip.Contains("Enchant Head - Swiftness") && etip.Contains("Enchanting") && etip.Contains("+8 Agility") && etip.Contains("Nothing is enchanted there now") && etip.Contains("How the formula is learned") && etip.Contains("Learn it at the Enchanting trainer") && etip.Contains("Reagents") && etip.Contains("Linen Cloth x2 (you have 6)")
                    && etip.Contains("Who can apply it") && etip.Contains("This enchant also has an effect that is not scored") && etip.Last() == "Click to open it in the Atlas", string.Join(" | ", etip));
                opened = null; InventoryView.UpgradeClickExtraForTest("e1:7007");
                Check("enchant click: a formula opens its spell page in the Atlas (spell:7007)", opened == "spell:7007", opened);
                Check("food: a 'Food & drink' section lists the best well-fed foods for her weights, then healing food and mana drinks, three of each at most, with how to get them - the squid is a Classic-baseline vendor item, labelled",
                    lines.Any(l => l.StartsWith("upfood|Well fed|310|Grilled Squid|+30 Attack Power for 30 min|Vendor · Peddler Nim · Greenvale (Classic)") && l.EndsWith("|classic")) && lines.Count(l => l.StartsWith("upfood|Well fed|")) == 3 && lines.Count(l => l.StartsWith("upfood|Food|")) == 3 && lines.Count(l => l.StartsWith("upfood|Drink|")) == 3,
                    string.Join(" ; ", lines.Where(l => l.StartsWith("upfood")).Take(4)));
                var ftip = InventoryView.UpgradeExtraTipForTest("f:Well fed:301");
                Check("food tooltip: the item as the game shows it, how the Cooking recipe is learned (a recipe item), the reagents with what I own, where to get it", ftip.Contains("Spiced Wolf Ribs") && ftip.Contains("Taught by Recipe: Spiced Wolf Ribs") && ftip.Contains("Linen Cloth x2 (you have 6)") && ftip.Contains("Where to get it"), string.Join(" | ", ftip));
                await Task.Delay(200);
                SnapshotSheet(InventoryView.UpgradeCardForTest, Shot("panel-enchants-food"));
                InventoryView.UpgradeClickSwitchForTest("applicable"); await Task.Delay(100);
                lines = InventoryView.UpgradeLinesForTest();
                Check("filter: 'only enchants I can apply' drops what nobody of mine or of the Lodge can make - the cap keeps only the armor kit Borg owns - and saves the switch", !lines.Any(l => l.StartsWith("upe|1|7007|")) && lines.Any(l => l.StartsWith("upe|1|600|Rough Armor Kit|") && l.Contains("|applicable")) && stored == "D1R0C1T0L0N1A1F1", stored + " | " + string.Join(" ; ", lines.Where(l => l.StartsWith("upe|1|"))));
                InventoryView.UpgradeClickSwitchForTest("applicable");
                InventoryView.UpgradeClickSwitchForTest("enchants"); await Task.Delay(100);
                lines = InventoryView.UpgradeLinesForTest();
                Check("filter: enchants off hides the 'No enchant' lines and the rows; the item upgrades stay", !lines.Any(l => l.StartsWith("upench|") || l.StartsWith("upe|")) && lines.Any(l => l.StartsWith("up|1|101|")) && stored == "D1R0C1T0L0N0A0F1", stored);
                InventoryView.UpgradeClickSwitchForTest("enchants");
                InventoryView.UpgradeClickSwitchForTest("food"); await Task.Delay(100);
                lines = InventoryView.UpgradeLinesForTest();
                Check("filter: Food & drink off hides the section", !lines.Any(l => l.StartsWith("upfood|")) && lines.Any(l => l.StartsWith("upe|1|7007|")) && stored == "D1R0C1T0L0N1A0F0", stored);
                InventoryView.UpgradeClickSwitchForTest("food"); await Task.Delay(100);
                lines = InventoryView.UpgradeLinesForTest();
                Check("consumables: the section is 'Consumables' with a group per kind that applies - food, drink, elixirs, flasks, potions, scrolls, bandages, ammo, temporary - each with its count (Aela: bow, so arrows)",
                    new[] { "upgroup|food|6", "upgroup|drink|3", "upgroup|elixir|2", "upgroup|flask|1", "upgroup|potion|4", "upgroup|scroll|2", "upgroup|bandage|3", "upgroup|ammo|2", "upgroup|temp|1" }.All(lines.Contains) && lines.Any(l => l.StartsWith("upfood|Ammo|371|Razor Arrow|")) && lines.Any(l => l.StartsWith("upfood|Elixir|330|Elixir of Agility|+12 Agility for 60 min|")), string.Join(" ; ", lines.Where(l => l.StartsWith("upfood|Ammo") || l.StartsWith("upfood|Elixir"))));
                InventoryView.UpgradeToggleGroupForTest("elixir"); await Task.Delay(100);
                lines = InventoryView.UpgradeLinesForTest();
                Check("consumables: a group folds away from its header - only the header stays ('collapsed'), the others are untouched, and the choice is part of the saved filters", lines.Contains("upgroup|elixir|2|collapsed") && !lines.Any(l => l.StartsWith("upfood|Elixir|")) && lines.Any(l => l.StartsWith("upfood|Flask|")) && stored == "D1R0C1T0L0N1A0F1Helixir", stored);
                InventoryView.UpgradeToggleGroupForTest("elixir"); await Task.Delay(100);
                Check("consumables: and opens again", InventoryView.UpgradeLinesForTest().Any(l => l.StartsWith("upfood|Elixir|")) && stored == "D1R0C1T0L0N1A0F1", stored);
                EnchantData.SetForTest(null, EnchantData.State.Unavailable, "layout"); InventoryView.UpgradesDataChanged(); await Task.Delay(100); await Ready();
                lines = InventoryView.UpgradeLinesForTest();
                Check("missing data: enchant tables the game does not have (an unknown layout) = a calm note under the list, no enchant rows; the items and the food stay", lines.Any(l => l.StartsWith("upnote|data|Enchants need the enchant tables of your game files")) && !lines.Any(l => l.StartsWith("upe|")) && lines.Any(l => l.StartsWith("up|1|101|")) && lines.Any(l => l.StartsWith("upfood|")), string.Join(" ; ", lines.Where(l => l.StartsWith("upnote"))));
                EnchantData.SetForTest(UpEnchants()); InventoryView.UpgradesDataChanged(); await Task.Delay(100); await Ready();

                // ---- the filters change the list and are remembered
                InventoryView.UpgradeClickSwitchForTest("raids"); await Task.Delay(100);
                lines = InventoryView.UpgradeLinesForTest();
                Check("filter: raids on adds the Deepmaw Helm to the head list (three items), and the choice is saved as 'D1R1C1T0L0'", lines.Contains("upslot|1|Head|Frayed Cap|3|0") && lines.Any(l => l.StartsWith("up|1|104|Deepmaw Helm|")) && stored == "D1R1C1T0L0N1A0F1", stored + " | " + string.Join(" ; ", lines.Where(l => l.StartsWith("upslot|1"))));
                InventoryView.UpgradeClickSwitchForTest("raids"); InventoryView.UpgradeClickSwitchForTest("dungeons"); await Task.Delay(100);
                lines = InventoryView.UpgradeLinesForTest();
                Check("filter: dungeons off leaves the cowl alone in the head slot and says 2 better items are hidden by the filters", lines.Contains("upslot|1|Head|Frayed Cap|1|0") && !lines.Any(l => l.StartsWith("up|1|105|")) && lines.Any(l => l.StartsWith("upnote|hidden|")), string.Join(" ; ", lines.Where(l => l.StartsWith("upnote"))));
                InventoryView.UpgradeClickSwitchForTest("dungeons");
                InventoryView.UpgradeClickSwitchForTest("tradeable"); await Task.Delay(100);
                lines = InventoryView.UpgradeLinesForTest();
                Check("filter: only tradeable drops what binds when picked up (the crown); the vest and bow stay", lines.Contains("upslot|1|Head|Frayed Cap|1|0") && lines.Any(l => l.StartsWith("up|5|111|")) && lines.Any(l => l.StartsWith("up|18|151|")) && stored == "D1R0C1T1L0N1A0F1", stored);
                InventoryView.UpgradeClickSwitchForTest("tradeable");
                InventoryView.UpgradeClickSwitchForTest("classic"); await Task.Delay(100);
                lines = InventoryView.UpgradeLinesForTest();
                Check("filter: without the Classic baseline's sources the vendor-only Padded Vest goes and the vest she can craft stays", lines.Contains("upslot|5|Chest||1|0") && lines.Any(l => l.StartsWith("up|5|111|")) && !lines.Any(l => l.StartsWith("up|5|110|")));
                InventoryView.UpgradeClickSwitchForTest("classic");
                InventoryView.UpgradeClickSwitchForTest("soon"); await Task.Delay(100);
                lines = InventoryView.UpgradeLinesForTest();
                Check("filter: coming soon lists the Cap of the Coming Day (level 32) apart under 'Coming soon', marked 'at level 32'; saved as L3", lines.Contains("upslot|1|Head|Frayed Cap|2|1") && lines.Any(l => l.StartsWith("up|1|107|Cap of the Coming Day|") && l.EndsWith("|soon")) && stored == "D1R0C1T0L3N1A0F1", stored);
                await Task.Delay(200);
                Snapshot(Shot("filters-soon"));
                InventoryView.UpgradeClickSwitchForTest("soon");
                Check("filter: the choices were remembered in the settings text and the page default is back after switching everything again", stored == "D1R0C1T0L0N1A0F1", stored);

                // ---- a click on a paper-doll slot
                InventoryView.UpgradeClickSlotForTest(11); await Task.Delay(100);
                lines = InventoryView.UpgradeLinesForTest();
                Check("slot click: clicking Ring 1 on the paper doll highlights that slot's upgrades (accent border on its block and on the doll tile) and brings them into view", InventoryView.UpgradeFocusForTest == 11 && lines.Contains("upslot|11|Ring 1|Copper Band|2|0|focus") && lines.Contains("focus|11") && InventoryView.UpgradeDollEdgeForTest(11) && !InventoryView.UpgradeDollEdgeForTest(1), string.Join(" ; ", lines.Where(l => l.StartsWith("upslot|11") || l.StartsWith("focus"))));
                await Task.Delay(200);
                Snapshot(Shot("slot-click"));
                InventoryView.UpgradeClickSlotForTest(10); await Task.Delay(100);
                lines = InventoryView.UpgradeLinesForTest();
                Check("slot click: a slot with nothing better still gets its block saying so (Hands: 'Nothing better found for hands'), the doll's focus moves", InventoryView.UpgradeFocusForTest == 10 && InventoryView.UpgradeBlockVisibleForTest(10) && lines.Contains("upslot|10|Hands||0|0|focus") && InventoryView.UpgradeDollEdgeForTest(10) && !InventoryView.UpgradeDollEdgeForTest(11));
                InventoryView.UpgradeClickSlotForTest(10);
                Check("slot click: a second click on the same slot clears the highlight", InventoryView.UpgradeFocusForTest == 0 && !InventoryView.UpgradeDollEdgeForTest(10) && !InventoryView.UpgradeBlockVisibleForTest(10));

                // ---- the weights editor
                InventoryView.UpgradeToggleWeightsForTest();
                Check("weights: the editor opens with the weights of Aela's build (Hunter, Marksmanship): Agility 1, Critical strike 1.4, Ranged weapon DPS 14", InventoryView.UpgradeWeightsOpenForTest && InventoryView.UpgradeWeightForTest("agi") == "1" && InventoryView.UpgradeWeightForTest("crit") == "1.4" && InventoryView.UpgradeWeightForTest("rdps") == "14", InventoryView.UpgradeWeightForTest("crit"));
                await Task.Delay(200);
                Snapshot(Shot("weights"));
                InventoryView.UpgradeSetWeightForTest("sta", "5");
                InventoryView.UpgradeApplyWeightsForTest();
                await Task.Delay(100); await Ready();
                lines = InventoryView.UpgradeLinesForTest();
                Check("weights: Apply keeps the change for this class and tree only (HUNTER/2 = sta=5) and the list is worked out again - the cowl now leads with '+3 Stamina'", UpgradeWeights.Overrides.TryGetValue("HUNTER/2", out var ov) && ov == "sta=5" && lines.Any(l => l.StartsWith("up|1|101|Ranger's Cowl|") && l.Contains("|+3 Stamina, +6 Agility")) && InventoryView.UpgradeWeightsOpenForTest, ov + " | " + string.Join(" ; ", lines.Where(l => l.StartsWith("up|1|101"))));
                InventoryView.UpgradeSetWeightForTest("agi", "banana");
                InventoryView.UpgradeApplyWeightsForTest();
                await Task.Delay(100); await Ready();
                Check("weights: something that is not a number is put back to the old value, nothing else changes", InventoryView.UpgradeWeightForTest("agi") == "1" && UpgradeWeights.Overrides["HUNTER/2"] == "sta=5");
                InventoryView.UpgradeResetWeightsForTest();
                await Task.Delay(100); await Ready();
                lines = InventoryView.UpgradeLinesForTest();
                Check("weights: 'Reset to defaults' drops the change and the original wording is back", UpgradeWeights.Overrides.Count == 0 && lines.Any(l => l.StartsWith("up|1|101|Ranger's Cowl|") && l.Contains("|+6 Agility, +3 Stamina")) && InventoryView.UpgradeWeightForTest("sta") == "0.45");
                InventoryView.UpgradeToggleWeightsForTest();
                Check("weights: closing the editor hides it", !InventoryView.UpgradeWeightsOpenForTest);

                // ---- other characters of the invented world
                await Gear("Forever-Cass");
                lines = InventoryView.UpgradeLinesForTest();
                Check("Cass (Paladin, a two-hand mace): the Great Maul is her main-hand upgrade; the Off hand block says she wields a two-hand weapon (no off hand to fill) when it is the focus", lines.Any(l => l.StartsWith("up|16|163|Great Maul|+24%|")) && lines.Any(l => l.StartsWith("upinfo|Paladin|Retribution|default|")));
                InventoryView.UpgradeClickSlotForTest(17); await Task.Delay(100);
                Check("Cass: the off hand block names the two-hand weapon and offers nothing", InventoryView.UpgradeLinesForTest().Contains("upslot|17|Off hand||0|0|focus"));
                InventoryView.UpgradeClickSlotForTest(17);
                await Gear("Forever-Borg");
                lines = InventoryView.UpgradeLinesForTest();
                Check("Borg (level 45 Hunter, Horde): the head list has mail (the Chain Coif) and the Horde vendor's cap; the caps sold only by the Alliance quartermaster are not his, and the raid helm is off", lines.Any(l => l.StartsWith("up|1|102|Chain Coif|")) && lines.Any(l => l.StartsWith("up|1|109|Grosh")) && !lines.Any(l => l.StartsWith("up|1|104|") || l.StartsWith("up|1|107|") || l.StartsWith("up|1|108|")), string.Join(" ; ", lines.Where(l => l.StartsWith("up|1|"))));

                // ---- calm notes about missing data
                await Gear("Forever-Aela");
                ClassicAtlas.Use(null); UpgradeEngine.ClearCache();
                InventoryView.UpgradesDataChanged(); await Task.Delay(100); await Ready();
                lines = InventoryView.UpgradeLinesForTest();
                Check("missing data: without the Classic baseline the panel says so calmly - 'Download the Classic baseline for drop and vendor sources' - and lists what the Lodge knows", lines.Any(l => l.StartsWith("upnote|data|Download the Classic baseline for drop and vendor sources")) && lines.Any(l => l.StartsWith("up|1|105|")), string.Join(" ; ", lines.Where(l => l.StartsWith("upnote"))));
                ClassicAtlas.Use(UpClassic()); UpgradeEngine.ClearCache();
                ItemData.SetForTest(null, ItemData.State.Disabled, "off"); AtlasQuery.Context.Lodge = () => null; UpgradeEngine.ClearCache();
                InventoryView.UpgradesDataChanged(); await Task.Delay(100); await Ready();
                lines = InventoryView.UpgradeLinesForTest();
                Check("missing data: game data switched off = 'Game data is switched off (Settings > General), so there are no items to compare.'", lines.Contains("upnote|problem|items"), string.Join(" ; ", lines.Take(4)));
                ItemData.SetForTest(UpItems(), ItemData.State.Ready); AtlasQuery.Context.Lodge = () => UpLodge(); UpgradeEngine.ClearCache();
                var old = lua;
                int at = old.IndexOf("at=" + (now - 60) + ",slots=", StringComparison.Ordinal);
                old = old.Substring(0, at) + "at=" + (now - 20 * 86400) + ",slots=" + old.Substring(at + ("at=" + (now - 60) + ",slots=").Length);
                File.WriteAllText(hubFile, old, new UTF8Encoding(false));
                File.SetLastWriteTimeUtc(hubFile, DateTime.UtcNow.AddSeconds(5));
                for (int i = 0; i < 60 && !InventoryView.UpgradeLinesForTest().Any(l => l.StartsWith("upnote|data|Your Elan's Hub data for Aela is 20 days old")); i++) { await Task.Delay(200); if (i % 10 == 9) { InventoryView.FilterForTest("Forever-Aela"); InventoryView.SelectTabForTest("gear"); } }
                lines = InventoryView.UpgradeLinesForTest();
                Check("missing data: gear the Hub saved 20 days ago says so - 'Your Elan's Hub data for Aela is 20 days old (older than 7 days): log in with this character ...'", lines.Any(l => l.StartsWith("upnote|data|Your Elan's Hub data for Aela is 20 days old (older than 7 days)")), string.Join(" ; ", lines.Where(l => l.StartsWith("upnote"))));
                await Ready();
                await Task.Delay(200);
                Snapshot(Shot("notes"));

                // ---- the narrow window: the card follows the page, nothing runs off
                Width = 760;
                await Task.Delay(300);
var card = InventoryView.UpgradeCardForTest; card.Measure(new Size(700, double.PositiveInfinity));                Check("layout: in a narrow page (700 px) the card is measured no wider than the page", card.DesiredSize.Width <= 700.5, card.DesiredSize.Width.ToString());
                Snapshot(Shot("narrow"));
                Width = 1320;

                var map = InventoryView.UpgradeMapLinkForTest(new UpgradeSource { Kind = "vendor", ZoneMap = 11, Fx = 0.5, Fy = 0.5, What = "x" });
                Check("world map: 'Show on map' asks the world map for a link at the source's zone; it is a world map id or nothing when that zone has no continent map, never an exception", map == null || map.StartsWith("worldmap", StringComparison.OrdinalIgnoreCase) || map.Contains(":"), map);
            }
            finally
            {
                InventoryView.OpenAtlas -= onOpen;
                AtlasQuery.Context.Lodge = () => null;
                HubData.ResetCache();
            }
        }

        // --selftest "upgrades", part 3: a read-only look at the real game files. A level 30 Hunter gets no mail (it comes at 40), a level 45 Hunter does; some upgrades are found.
        void UpgradesSmokeReal(CheckFn Check, List<string> notes)
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
                var raw = ItemData.ReadCasc(real, "_classic_beta_");
                var set = ItemData.ParseTables(raw.Sparse, raw.Item, raw.Budget);
                if (!set.Ok) { notes.Add("skip real game files: " + set.Problem + " (this Outpost does not know that game build's item table; nothing was guessed)"); return; }
                ItemData.ReadCacheRows(set, ItemData.AdbFile(real, "_classic_beta_"));
                var snap = new ItemSnapshot(set.Client, set.Cache);
                try { var sset = SpellParse.All(SpellData.ReadCasc(real, "_classic_beta_")); SpellData.SetForTest(sset); } catch (Exception e) { notes.Add("note real game files: spell tables not read (" + e.Message + "), armor and damage are left out of the score"); }
                ItemData.SetForTest(snap, ItemData.State.Ready);
                ClassicAtlas.Use(null);
                UpgradeEngine.ClearCache();
                List<UpgradeCandidate> Run(string cls, int level, string race, out int considered)
                {
                    var ev = new UpgradeEngine.Evaluator(new UpgradeChar { Key = "real", Name = "Real", ClassFile = cls, Race = race, Level = level });
                    var res = new List<UpgradeCandidate>(); considered = 0;
                    foreach (var it in snap.Items.Values)
                    {
                        if (!UpgradeEngine.Evaluator.Candidate(it, level)) continue;
                        considered++;
                        foreach (var slot in UpgradeRules.SlotsFor(it.InventoryType)) { var c = ev.Evaluate(it, slot); if (c != null) res.Add(c); }
                    }
                    return res;
                }
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var h30 = Run("HUNTER", 30, "Orc", out int n30);
                long ms = sw.ElapsedMilliseconds;
                var h45 = Run("HUNTER", 45, "Orc", out int n45);
                var w30 = Run("WARRIOR", 30, "Orc", out _);
                var p45 = Run("PALADIN", 45, "Human", out _);
                var p50 = Run("PALADIN", 50, "Human", out _);
                Check("real game files: a level 30 Hunter gets leather, cloth and no mail and no plate - mail starts at 40 (candidates: " + h30.Count + ", from " + n30 + " items)", h30.Count > 100 && h30.All(c => c.Item.ClassId != 4 || (c.Item.SubclassId != 3 && c.Item.SubclassId != 4)) && h30.Any(c => c.Item.ClassId == 4 && c.Item.SubclassId == 2), h30.Count + " " + h30.Count(c => c.Item.SubclassId == 3));
                Check("real game files: nothing above level 33 for a level 30 character (the 'coming soon' limit is +3), the usable ones are marked apart", h30.All(c => c.Item.RequiredLevel <= 33) && h30.All(c => c.Soon == (c.Item.RequiredLevel > 30)) && h30.Any(c => !c.Soon) && h30.Any(c => c.Soon));
                Check("real game files: a level 45 Hunter does get mail (and still no plate); no maces, no wands, no shields for a Hunter", h45.Any(c => c.Item.ClassId == 4 && c.Item.SubclassId == 3 && !c.Soon) && !h45.Any(c => c.Item.ClassId == 4 && c.Item.SubclassId == 4) && !h45.Any(c => c.Item.ClassId == 2 && (c.Item.SubclassId == 4 || c.Item.SubclassId == 5 || c.Item.SubclassId == 19)) && !h45.Any(c => c.Item.InventoryType == 14),
                    h45.Count + " " + h45.Count(c => c.Item.SubclassId == 3));
                Check("real game files: a Warrior of 30 wields swords and axes and carries a shield; a Paladin of 45 has no plate yet in the level-45 usable list only if it is below 40 - plate is there at 45 and not for a Hunter", w30.Any(c => c.Item.InventoryType == 14) && p45.Any(c => c.Item.ClassId == 4 && c.Item.SubclassId == 4 && !c.Soon) && p45.Any(c => c.Item.InventoryType == 28 && c.Item.SubclassId == 7) && !p45.Any(c => c.Item.InventoryType == 28 && c.Item.SubclassId != 7));
                Check("real game files: a level 50 Paladin sees two-hand weapons only in the main hand and never as an off-hand upgrade", p50.Where(c => c.Slot == 17).All(c => c.Item.InventoryType != 17) && p50.Any(c => c.Slot == 16 && c.Item.InventoryType == 17) && p50.All(c => c.Item.RequiredLevel <= 53));
                Check("real game files: every candidate is a real equippable item with a name, stats and a positive gain (score words never empty)", h45.All(c => c.Item.Equippable && !string.IsNullOrEmpty(c.Item.Name) && c.Gain > 0 && c.Delta.Length > 4));
                // ---- enchants on the real files (read-only): the two tables read by their verified layouts, the formulas and kits found, and a few applicability facts
                var wasRoot = ItemData.WowRoot; var wasFlavor = ItemData.Flavor; var wasAuto = EnchantData.AutoStart;
                try
                {
                    ItemData.WowRoot = () => real; ItemData.Flavor = () => "_classic_beta_"; EnchantData.AutoStart = true; EnchantData.ResetForTest(); UpgradeExtras.ForgetForTest();
                    bool er = EnchantData.Ensure();
                    var rs = EnchantData.Set;
                    Check("real game files: the enchant tables are read with their checked layouts (SpellItemEnchantment 952B72B2, SpellEquippedItems D9177916) - " + (rs?.Enchants.Count ?? 0) + " enchants, " + (rs?.Equipped.Count ?? 0) + " spells with equipped-item rows", er && rs != null && rs.Enchants.Count > 1500 && rs.Equipped.Count > 500, EnchantData.Reason);
                    if (er)
                    {
                        var ropts = UpgradeExtras.Options(rs);
                        var forms = ropts.Where(o => o.Kit == null).ToList(); var kits = ropts.Where(o => o.Kit != null).ToList();
                        Check("real game files: more than 50 Enchanting formulas and some kits / scopes are found from the spells' own effect 53 (formulas: " + forms.Count + ", kits and scopes: " + kits.Count + ")", forms.Count > 50 && kits.Count > 0, forms.Count + " " + kits.Count);
                        var chestItem = snap.Items.Values.FirstOrDefault(i => i.ClassId == 4 && i.InventoryType == 5 && i.Quality >= 2 && i.RequiredLevel >= 20 && i.RequiredLevel <= 40);
                        var headItem = snap.Items.Values.FirstOrDefault(i => i.ClassId == 4 && i.InventoryType == 1 && i.Quality >= 2 && i.RequiredLevel >= 20 && i.RequiredLevel <= 40);
                        var bowItem = snap.Items.Values.FirstOrDefault(i => i.ClassId == 2 && i.SubclassId == 2 && i.Quality >= 2 && i.RequiredLevel >= 20);
                        int forChest = chestItem == null ? 0 : forms.Count(o => rs.Applies(o.SpellId, chestItem));
                        int forHead = headItem == null ? 0 : forms.Count(o => rs.Applies(o.SpellId, headItem));
                        Check("real game files: formulas apply by the item - a chest piece has chest enchants (" + forChest + "), a bow does not take a melee weapon enchant, a bow takes a scope when the game has one",
                            forChest >= 3 && (forms.Count(o => (SpellData.Spell(o.SpellId)?.Name ?? "").IndexOf("Chest", StringComparison.OrdinalIgnoreCase) >= 0 && rs.Applies(o.SpellId, chestItem)) >= 1) && forHead < forms.Count / 3 && (bowItem == null || kits.Any(k => k.Kind == "Scope" && rs.Applies(k.SpellId, bowItem)) || !kits.Any(k => k.Kind == "Scope")), forChest + " " + forHead);
                        var stat = rs.Enchants.Values.FirstOrDefault(x => x.Type[0] == EnchantInfo.KindStat && x.Arg[0] == 3 && x.Points[0] > 0 && x.Name.IndexOf("Agility", StringComparison.OrdinalIgnoreCase) >= 0);
                        Check("real game files: an 'Agility' enchant is a stat enchant of Agility (kind 5, argument 3) with a positive amount ('" + stat?.Name + " +" + (stat?.Points[0] ?? 0) + "'); at least one enchant is a spell / proc (not scored)", stat != null && rs.Enchants.Values.Any(x => x.Type[0] == 1 || x.Type[0] == 3 || x.Type[0] == 7));
                        var ev = new UpgradeEngine.Evaluator(new UpgradeChar { Key = "real", Name = "Real", ClassFile = "HUNTER", Race = "Orc", Level = 45 });
                        int scored = 0, offered = 0;
                        if (chestItem != null) foreach (var o in forms.Where(o => rs.Applies(o.SpellId, chestItem))) { var parts = UpgradeExtras.PartsOf(o.Info, 5, chestItem, ev.W, out _); if (parts.Count > 0) scored++; if (UpgradeExtras.ScoreOf(parts) > 0.5) offered++; }
                        Check("real game files: for a level 45 Hunter's chest piece some formulas are scored (" + scored + ") and some are worth suggesting (" + offered + ")", scored > 0 && offered > 0);
                        notes.Add("note real game files: " + rs.Enchants.Count + " enchants, " + rs.Equipped.Count + " equipped-item rows, " + forms.Count + " formulas, " + kits.Count + " kits and scopes (" + string.Join(", ", kits.GroupBy(k => k.Kind).Select(g => g.Key + " " + g.Count())) + ")");
                    }
                }
                finally { ItemData.WowRoot = wasRoot; ItemData.Flavor = wasFlavor; EnchantData.AutoStart = wasAuto; EnchantData.ResetForTest(); UpgradeExtras.ForgetForTest(); }
                notes.Add("note real game files: " + snap.Count + " items; level 30 Hunter " + h30.Count + " better-than-nothing candidates from " + n30 + " items in " + ms + " ms; level 45 Hunter " + h45.Count + " (" + h45.Count(c => c.Item.SubclassId == 3 && c.Item.ClassId == 4) + " mail)");
            }
            catch (Exception e)
            {
                Check("real game files: the upgrade check ran to the end", false, e.ToString());
            }
        }
    }
}
