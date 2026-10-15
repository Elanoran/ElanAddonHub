using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Atlas.Classic;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Maps;
using ElansAddonHub.Services.Upgrades;

namespace ElansAddonHub
{
    // --selftest "upgrades": Characters > Gear > Upgrades and the Atlas item fact. The engine on an invented world (MainWindow.UpgradesTest.Data.cs): the slot rules, the
    // class armor and weapon skills, the weights, the score and its words, the sources and their ranking, the filters, the cache; then the panel in the window and the
    // "Upgrade for" fact of an item page; the last, optional part reads the real game files (read-only).
    public partial class MainWindow
    {
        static List<string> Ids(UpgradeSlotResult r) => r.Upgrades.Select(c => c.Item.Id.ToString()).ToList();
        static string Joined(IEnumerable<string> l) => string.Join(",", l);

        async Task<string> UpgradesTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            void Check(string what, bool cond, string detail = null) { notes.Add((cond ? "ok   " : "FAIL ") + what + (!cond && detail != null ? " [" + detail + "]" : "")); ok &= cond; }
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var savedLodge = AtlasQuery.Context.Lodge; var savedChars = AtlasQuery.Context.Characters; var savedCrafters = AtlasQuery.Context.Crafters;
            var savedOv = UpgradeWeights.Overrides; var savedSave = UpgradeWeights.SaveOverrides; var savedOwned = AtlasQuery.Context.Owned; var savedEnchAuto = EnchantData.AutoStart;
            var savedW = Width; var savedH = Height;
            try
            {
                // ---------------------------------------------------------------------------------------------------------------- the world
                var hub = HubData.Parse(UpHubLua(now));
                var chars = UpInvChars(hub);
                InvChar C(string name) => chars.First(c => c.Name == name);
                ClassData.ResetForTest();
                ItemData.SetForTest(UpItems(), ItemData.State.Ready);
                SpellData.SetForTest(UpSpells());
                SeenData.SetForTest(null, SeenData.State.NotStarted);
                MapData.SetForTest(new MapIndex(AtlasTestMapSet()));
                ClassicAtlas.Use(UpClassic());
                var lodge = UpLodge();
                AtlasQuery.Context.Lodge = () => lodge;
                AtlasQuery.Context.Characters = () => chars;
                AtlasQuery.Context.Crafters = null;
                int saves = 0;
                EnchantData.AutoStart = false; EnchantData.SetForTest(UpEnchants()); UpgradeExtras.ForgetForTest();
                AtlasQuery.Context.Owned = () => UpOwned(chars);
                UpgradeWeights.Bind(new Dictionary<string, string>(), () => saves++);
                UpgradeEngine.ClearCache();
                var items = UpItems();
                ClientItem It(int id) => items.Get(id);
                var aela = UpgradeChar.From(C("Aela"));
                var borg = UpgradeChar.From(C("Borg"));
                var cass = UpgradeChar.From(C("Cass"));
                var dru = UpgradeChar.From(C("Dru"));
                var wren = UpgradeChar.From(C("Wren"));

                // ---------------------------------------------------------------------------------------------------------------- the character
                Check("character: Aela is a level 30 Night Elf Hunter of the Alliance with five things worn; her talents say Marksmanship (spec 2); the main hand and off hand are told apart",
                    aela.Level == 30 && aela.ClassFile == "HUNTER" && aela.Side == "Alliance" && aela.Spec == 2 && aela.Worn.Count == 5 && aela.Worn[1] == 100 && aela.Worn[16] == 140 && !aela.Worn.ContainsKey(17) && aela.WornKnown, aela.Fingerprint);
                Check("character: the race decides the side when the Hub saved none (Orc = Horde, Human = Alliance); the Hunter with no talents has spec 0",
                    borg.Side == "Horde" && wren.Side == "Alliance" && borg.Spec == 0 && new UpgradeChar { Race = "Scourge" }.Side == "Horde" && new UpgradeChar { Race = "NightElf" }.Side == "Alliance" && new UpgradeChar { Race = "Zorp" }.Side == "");
                Check("character: no gear module = nothing is known about what is worn (the engine says so, it does not call everything an upgrade)", !UpgradeChar.From(new InvChar { Key = "x", Name = "X", Class = "HUNTER", Level = 20 }).WornKnown);

                // ---------------------------------------------------------------------------------------------------------------- the slots and the class skills
                Check("slots: every item type fits its slot - head 1, neck 2, shoulder 3, robe and chest 5, back 16 -> slot 15, finger 11 -> both ring slots, trinket 12 -> both trinket slots",
                    UpgradeRules.Fits(1, 1) && UpgradeRules.Fits(2, 2) && UpgradeRules.Fits(3, 3) && UpgradeRules.Fits(5, 5) && UpgradeRules.Fits(5, 20) && UpgradeRules.Fits(15, 16) && UpgradeRules.Fits(11, 11) && UpgradeRules.Fits(12, 11)
                    && UpgradeRules.Fits(13, 12) && UpgradeRules.Fits(14, 12) && !UpgradeRules.Fits(1, 5) && !UpgradeRules.Fits(11, 12) && !UpgradeRules.Fits(4, 4));
                Check("slots: a two-hand weapon fits the main hand only; a one-hand weapon fits both hands; a shield and a held-in-off-hand item the off hand only; a bow, gun, wand, thrown weapon and relic the ranged slot",
                    UpgradeRules.Fits(16, 17) && !UpgradeRules.Fits(17, 17) && UpgradeRules.Fits(16, 13) && UpgradeRules.Fits(17, 13) && UpgradeRules.Fits(17, 14) && !UpgradeRules.Fits(16, 14) && UpgradeRules.Fits(17, 23) && UpgradeRules.Fits(16, 21) && UpgradeRules.Fits(17, 22)
                    && UpgradeRules.Fits(18, 15) && UpgradeRules.Fits(18, 26) && UpgradeRules.Fits(18, 25) && UpgradeRules.Fits(18, 28) && UpgradeRules.SlotsFor(12).SequenceEqual(new[] { 13, 14 }) && UpgradeRules.SlotsFor(13).SequenceEqual(new[] { 16, 17 }));
                Check("slots: 17 slots are listed (no shirt, tabard or ammo), the pairs are named Ring 1 / Ring 2 and Trinket 1 / Trinket 2", UpgradeRules.Slots.Length == 17 && UpgradeRules.Slots.Distinct().Count() == 17 && UpgradeRules.SlotTitle(11) == "Ring 1" && UpgradeRules.SlotTitle(12) == "Ring 2" && UpgradeRules.SlotTitle(14) == "Trinket 2" && UpgradeRules.SlotTitle(1) == "Head" && UpgradeRules.SlotTitle(16) == "Main hand");
                Check("class skills: no mail before level 40 for a Hunter (the Chain Coif), mail from 40; never plate; a Paladin gets plate from 40 and not before",
                    !UpgradeRules.CanUse(aela, It(102), 1, 30) && !UpgradeRules.CanUse(aela, It(102), 1, 39) && UpgradeRules.CanUse(aela, It(102), 1, 40) && !UpgradeRules.CanUse(aela, It(112), 5, 60) && !UpgradeRules.CanUse(cass, It(112), 5, 39) && UpgradeRules.CanUse(cass, It(112), 5, 40));
                Check("class skills: a Rogue or a Priest wears leather / cloth only, a Hunter and a Warrior leather from level 1, a Mage cloth only",
                    UpgradeRules.CanUse(dru, It(101), 1, 25) && !UpgradeRules.CanUse(dru, It(102), 1, 60) && UpgradeRules.CanUse(wren, It(102), 1, 30) && !UpgradeRules.CanUse(new UpgradeChar { ClassFile = "MAGE" }, It(101), 1, 60) && UpgradeRules.CanUse(new UpgradeChar { ClassFile = "MAGE" }, It(103), 1, 10)
                    && !UpgradeRules.CanUse(new UpgradeChar { ClassFile = "PRIEST" }, It(101), 1, 60));
                Check("class skills: weapons - a Hunter has no maces or wands, a Priest a wand but no sword, a Rogue no two-hand sword, a Warrior all swords",
                    !UpgradeRules.CanUse(aela, It(161), 16, 50) && !UpgradeRules.CanUse(aela, It(152), 18, 50) && UpgradeRules.CanUse(new UpgradeChar { ClassFile = "PRIEST" }, It(152), 18, 30) && !UpgradeRules.CanUse(new UpgradeChar { ClassFile = "PRIEST" }, It(170), 16, 30)
                    && !UpgradeRules.CanUse(dru, It(176), 16, 25) && UpgradeRules.CanUse(dru, It(177), 16, 25) && UpgradeRules.CanUse(wren, It(172), 16, 30) && UpgradeRules.CanUse(wren, It(170), 16, 30));
                Check("class skills: a shield only for the classes that carry one (Warrior, Paladin, Shaman), a libram only for the Paladin and an idol only for the Druid, never in another slot",
                    !UpgradeRules.CanUse(aela, It(143), 17, 30) && UpgradeRules.CanUse(wren, It(143), 17, 30) && UpgradeRules.CanUse(cass, It(162), 17, 50) && !UpgradeRules.CanUse(dru, It(143), 17, 25)
                    && UpgradeRules.CanUse(cass, It(164), 18, 50) && !UpgradeRules.CanUse(cass, It(165), 18, 50) && UpgradeRules.CanUse(new UpgradeChar { ClassFile = "DRUID" }, It(165), 18, 50) && !UpgradeRules.CanUse(aela, It(164), 18, 50) && !UpgradeRules.CanUse(wren, It(143), 16, 30));
                Check("class skills: a second one-hand weapon needs dual wield - Rogue from 10, Warrior and Hunter from 20, Shaman from 40, a Paladin, Priest and Mage never; two-hand weapons are not off-hand items",
                    UpgradeRules.CanUse(dru, It(177), 17, 10) && !UpgradeRules.CanUse(dru, It(177), 17, 9) && !UpgradeRules.CanUse(aela, It(141), 17, 19) && UpgradeRules.CanUse(aela, It(141), 17, 20) && UpgradeRules.CanUse(wren, It(170), 17, 20) && !UpgradeRules.CanUse(wren, It(170), 17, 19)
                    && !UpgradeRules.CanUse(new UpgradeChar { ClassFile = "SHAMAN" }, It(141), 17, 39) && UpgradeRules.CanUse(new UpgradeChar { ClassFile = "SHAMAN" }, It(141), 17, 40) && !UpgradeRules.CanUse(cass, It(161), 17, 60) && !UpgradeRules.CanUse(wren, It(172), 17, 60));
                Check("class skills: items that are not armor or weapons (reagents, cloth) and a class this version does not know are never offered", !UpgradeRules.CanUse(aela, It(190), 1, 30) && !UpgradeRules.CanUse(new UpgradeChar { ClassFile = "DEATHKNIGHT" }, It(101), 1, 60) && !UpgradeRules.CanUse(null, It(101), 1, 30) && !UpgradeRules.CanUse(aela, null, 1, 30));

                // ---------------------------------------------------------------------------------------------------------------- the weights
                var hunter0 = UpgradeWeights.DefaultsOf("HUNTER", 0); var hunter1 = UpgradeWeights.DefaultsOf("HUNTER", 1); var hunter2 = UpgradeWeights.DefaultsOf("HUNTER", 2);
                Check("weights: a Hunter's Agility is the baseline (1.0), Attack power, Stamina and ranged damage come after it; the Marksmanship preset values critical strike a little higher; no talents = Beast Mastery's, the Hunter's levelling build",
                    hunter0["agi"] == 1.0 && hunter0["ap"] < hunter0["agi"] && hunter0["sta"] < hunter0["agi"] && hunter0["rdps"] > hunter0["agi"] && hunter2["crit"] > hunter1["crit"] && hunter0["crit"] == hunter1["crit"] && UpgradeWeights.DefaultSpecOf("HUNTER") == 1);
                var pal1 = UpgradeWeights.DefaultsOf("PALADIN", 1); var pal2 = UpgradeWeights.DefaultsOf("PALADIN", 2); var pal3 = UpgradeWeights.DefaultsOf("PALADIN", 3);
                Check("weights: Paladin presets - Holy values healing, Intellect and mana per 5 sec. and not Strength; Protection values Stamina, Defense, Dodge, Parry and Block; Retribution values Strength and weapon damage; no talents = Retribution",
                    pal1["heal"] >= 1 && pal1["int"] > 0.5 && pal1["mp5"] > 0.5 && !pal1.ContainsKey("str") && pal2["sta"] >= 1 && pal2["defense"] > 1 && pal2["dodge"] > 0.5 && pal2["parry"] > 0.5 && pal2["block"] > 0.3 && pal3["str"] >= 1 && pal3["dps"] > pal2["dps"]
                    && UpgradeWeights.DefaultsOf("PALADIN", 0)["str"] == pal3["str"] && UpgradeWeights.DefaultSpecOf("PALADIN") == 3);
                Check("weights: all nine classes have three presets with only known keys, positive numbers and a name for each tree (Marksmanship, Retribution, Shadow ...)",
                    UpgradeWeights.Classes.Length == 9 && UpgradeWeights.Classes.All(c => Enumerable.Range(1, 3).All(s => { var w = UpgradeWeights.DefaultsOf(c, s); return w.Count > 5 && w.All(k => UpgradeWeights.IsKey(k.Key) && k.Value >= 0) && UpgradeWeights.SpecName(c, s) != null; }))
                    && UpgradeWeights.SpecName("HUNTER", 2) == "Marksmanship" && UpgradeWeights.SpecName("PALADIN", 3) == "Retribution" && UpgradeWeights.SpecName("PRIEST", 3) == "Shadow" && UpgradeWeights.SpecName("HUNTER", 0) == null);
                Check("weights: a class that is not known gets stamina and armor only, never an exception; every stat the item tables name maps to a weight (or to none)",
                    UpgradeWeights.DefaultsOf("DEATHKNIGHT", 1).ContainsKey("sta") && UpgradeWeights.DefaultsOf(null, 0).Count > 0 && UpgradeWeights.KeyOfStat(3) == "agi" && UpgradeWeights.KeyOfStat(45) == "spell" && UpgradeWeights.KeyOfStat(38) == "ap" && UpgradeWeights.KeyOfStat(39) == "ap" && UpgradeWeights.KeyOfStat(32) == "crit"
                    && UpgradeWeights.KeyOfStat(53) == "res" && UpgradeWeights.KeyOfStat(9999) == null);
                var hub3 = HubModules.TalentsOf(C("Aela").Hub);
                Check("spec from the talents: the tree (left to right) with the most points - 27 of 33 in the middle tree = Marksmanship; no talents module = 0", UpgradeWeights.DetectSpec(hub3) == 2 && UpgradeWeights.DetectSpec(null) == 0 && UpgradeChar.From(C("Cass")).Spec == 0);
                HubTalentNode TN(double x, double y, int r) => new HubTalentNode { Id = 1, R = r, M = 3, X = x, Y = y, HasPos = true };
                HubTalentsModule Tm(params HubTalentNode[] n) { var m = new HubTalentsModule(); m.Nodes.AddRange(n); return m; }
                HubTalentsModule Tm3(int p1, int p2, int p3) => Tm(TN(1800, 600, p1), TN(2400, 600, 0), TN(5400, 600, p2), TN(6000, 600, 0), TN(9000, 600, p3), TN(9600, 600, 0));
                Check("spec from the talents: a tie, fewer than 5 points, or trees that were not recognised (two trees only) give 0 = the class's levelling build, never a guess",
                    UpgradeWeights.DetectSpec(Tm3(6, 6, 2)) == 0 && UpgradeWeights.DetectSpec(Tm3(1, 3, 1)) == 0 && UpgradeWeights.DetectSpec(Tm3(1, 9, 1)) == 2 && UpgradeWeights.DetectSpec(Tm(TN(1800, 600, 5), TN(2400, 600, 5), TN(5400, 600, 2), TN(6000, 600, 2))) == 0 && UpgradeWeights.DetectSpec(new HubTalentsModule()) == 0);
                Check("weights: parsing is strict - unknown keys, negative, huge or non-numbers are ignored, commas and semicolons both separate; formatting keeps the editor's order",
                    UpgradeWeights.Parse("agi=abc;zzz=3;sta=-1;int=2000;crit=1.5,hit=2").Count == 2 && UpgradeWeights.Parse("crit=1.5,hit=2")["hit"] == 2 && UpgradeWeights.Parse(null).Count == 0 && UpgradeWeights.Parse("garbage").Count == 0
                    && UpgradeWeights.Format(new Dictionary<string, double> { ["crit"] = 1.5, ["agi"] = 0.25 }) == "agi=0.25;crit=1.5");
                var fp0 = UpgradeWeights.Fingerprint("HUNTER", 2);
                var edit = UpgradeWeights.Effective("HUNTER", 2); edit["agi"] = 2.5; edit["sta"] = 0.45;
                UpgradeWeights.Set("HUNTER", 2, edit);
                Check("edit: a changed weight is kept for this class and tree only as the difference from the default (HUNTER/2 = agi=2.5), Effective returns it, other trees and classes are untouched, the settings were asked to save",
                    UpgradeWeights.Overrides.Count == 1 && UpgradeWeights.Overrides["HUNTER/2"] == "agi=2.5" && UpgradeWeights.Effective("HUNTER", 2)["agi"] == 2.5 && UpgradeWeights.Effective("HUNTER", 1)["agi"] == 1.0 && UpgradeWeights.Effective("PALADIN", 3)["str"] == 1.0
                    && UpgradeWeights.IsChanged("HUNTER", 2) && !UpgradeWeights.IsChanged("HUNTER", 1) && saves == 1 && UpgradeWeights.Fingerprint("HUNTER", 2) != fp0);
                UpgradeWeights.Reset("HUNTER", 2);
                bool resetOk = UpgradeWeights.Overrides.Count == 0 && UpgradeWeights.Effective("HUNTER", 2)["agi"] == 1.0 && UpgradeWeights.Fingerprint("HUNTER", 2) == fp0 && saves == 2;
                UpgradeWeights.Set("HUNTER", 2, UpgradeWeights.DefaultsOf("HUNTER", 2));
                Check("edit: reset brings the default back and drops the entry; saving weights equal to the defaults stores nothing", resetOk && UpgradeWeights.Overrides.Count == 0);
                UpgradeWeights.Set("HUNTER", 0, new Dictionary<string, double>(UpgradeWeights.DefaultsOf("HUNTER", 0)) { ["str"] = 3 });
                Check("edit: with no talents the weights are kept under the class's levelling build (HUNTER/1), so Reset on the same screen finds them", UpgradeWeights.Overrides.ContainsKey("HUNTER/1") && UpgradeWeights.Effective("HUNTER", 0)["str"] == 3 && UpgradeWeights.Effective("HUNTER", 1)["str"] == 3);
                UpgradeWeights.Reset("HUNTER", 0);

                // ---------------------------------------------------------------------------------------------------------------- score and words
                var w2 = UpgradeWeights.Effective("HUNTER", 2);
                double s100 = UpgradeScore.Score(It(100), 1, w2), s101 = UpgradeScore.Score(It(101), 1, w2);
                Check("score: stats times weights plus armor times its weight (Frayed Cap: 3 Agility + 2 Stamina x 0.45 + 20 armor x 0.02 = 4.3; Ranger's Cowl 12.13)", Math.Abs(s100 - 4.3) < 0.001 && Math.Abs(s101 - 12.13) < 0.001, s100 + " " + s101);
                Check("score: weapon damage per second counts - a bow 10 dps x 14 plus Agility; a melee weapon x 1.5; the off hand counts half",
                    Math.Abs(UpgradeScore.Dps(It(150)) - 5.0) < 0.001 && Math.Abs(UpgradeScore.Dps(It(151)) - 10.0) < 0.001 && Math.Abs(UpgradeScore.Score(It(151), 18, w2) - 142) < 0.001 && Math.Abs(UpgradeScore.Score(It(141), 16, w2) - 18.35) < 0.001 && Math.Abs(UpgradeScore.Score(It(141), 17, w2) - 12.35) < 0.001,
                    UpgradeScore.Score(It(151), 18, w2) + " " + UpgradeScore.Score(It(141), 16, w2) + " " + UpgradeScore.Score(It(141), 17, w2));
                Check("words: '+6 Agility, +3 Stamina, +24 armor' - the changes that matter most to the weights first; the other way round everything is red-signed; nothing differs = empty",
                    UpgradeScore.DiffText(It(101), It(100), 1, w2) == "+6 Agility, +3 Stamina, +24 armor" && UpgradeScore.DiffText(It(100), It(101), 1, w2) == "-6 Agility, -3 Stamina, -24 armor" && UpgradeScore.DiffText(It(100), It(100), 1, w2) == "" && UpgradeScore.DiffText(It(151), It(150), 18, w2) == "+5.0 DPS, +2 Agility",
                    UpgradeScore.DiffText(It(101), It(100), 1, w2) + " | " + UpgradeScore.DiffText(It(151), It(150), 18, w2));
                Check("words: a stat the other item does not have is a plain gain or loss with its name (Gold Band against Copper Band: +8 Agility, -3 Stamina); an empty slot lists the item's own stats, the most valuable first",
                    UpgradeScore.DiffText(It(122), It(120), 11, w2) == "+8 Agility, -3 Stamina" && UpgradeScore.StatsText(It(111), 5, w2) == "+8 Agility, +5 Stamina, +40 armor" && UpgradeScore.Diff(It(101), It(100), 1, w2, 2).Count == 2);
                Check("words: the percentage - 'new' for an empty slot, '+14%', '-3%', and a huge one is capped", UpgradeScore.PctText(null) == "new" && UpgradeScore.PctText(14.4) == "+14%" && UpgradeScore.PctText(-3) == "-3%" && UpgradeScore.PctText(5000) == "+1000%+" && UpgradeScore.PctText(0.2) == "+0%");

                // ---------------------------------------------------------------------------------------------------------------- the engine: Aela
                int c0 = UpgradeEngine.ComputeCount;
                var set = UpgradeEngine.Compute(aela);
                var f0 = new UpgradeFilters();
                var picks = UpgradeEngine.Pick(set, f0);
                UpgradeSlotResult Slot(List<UpgradeSlotResult> l, int s) => l.First(x => x.Slot == s);
                Check("engine: it ran once, found no problem, read the item data, the Classic baseline and the Lodge, and the spec is Marksmanship", UpgradeEngine.ComputeCount == c0 + 1 && set.Problem == "" && set.GameData && set.ClassicData && set.LodgeData && set.SpecName == "Marksmanship" && set.Considered > 40, set.Problem + " " + set.SpecName + " " + set.Considered);
                var head = Slot(picks, 1);
                Check("head: Aela (level 30) gets the Crypt Crown and the Ranger's Cowl - not the Chain Coif (mail comes at 40), not the Cloth Hood (less than she wears), not the Deepmaw Helm (a raid, off by default), not the Warchief's Cap (a Horde quest) nor the Horde vendor's cap, not the caps that need level 32 / 34, not a poor or test item",
                    Joined(Ids(head)) == "105,101" && head.WornId == 100 && head.Hidden == 1 && !set.BySlot[1].Any(c => new[] { 102, 103, 106, 109, 108, 191 }.Contains(c.Item.Id)), Joined(Ids(head)) + " hidden " + head.Hidden + " all " + Joined(set.BySlot[1].Select(c => c.Item.Id.ToString())));
                var cowl = head.Upgrades.First(c => c.Item.Id == 101); var crown = head.Upgrades.First(c => c.Item.Id == 105);
                Check("head: the score of the cowl is +182 % over the cap and the words say what changes ('+6 Agility, +3 Stamina, +24 armor (score +182%)'); the crown is the bigger gain (+301 %)",
                    cowl.Delta == "+6 Agility, +3 Stamina, +24 armor (score +182%)" && Math.Round(cowl.Pct.Value) == 182 && Math.Round(crown.Pct.Value) == 301 && cowl.OldItemId == 100 && cowl.Gain > 7.8 && cowl.Gain < 7.9, cowl.Delta + " " + cowl.Pct + " " + crown.Pct);
                Check("head: the Crypt Crown (a dungeon drop the Lodge saw, 15 %) ranks above the cowl although the cowl is a sure buy - the gain is bigger; the Deepmaw Helm needs raids and then ranks last (a raid boss, 8 %, far above her level)",
                    head.Upgrades[0].Rank > head.Upgrades[1].Rank && Joined(Ids(Slot(UpgradeEngine.Pick(set, new UpgradeFilters { Raids = true }), 1))) == "105,101,104");
                Check("sources: the crown's best source is the Lodge's - 'Drop · Crypt Lord · Crypt of Bones · 15% (Lodge)', a dungeon (the Classic 12 % rides on the same row, not a second one)",
                    crown.Best.Line == "Drop · Crypt Lord · Crypt of Bones · 15% (Lodge)" && crown.Best.Dungeon && !crown.Best.Raid && crown.Sources.Count == 1 && crown.Best.Lodge, crown.Best.Line + " " + crown.Sources.Count);
                Check("sources: the cowl - a vendor with the Lodge's price ('Vendor · Quartermaster Voss · Greenvale · 1g 20s 50c (Lodge)') and the quest 'Scout the Vale' (level 12) from the baseline, the Lodge's source first",
                    cowl.Sources.Count == 2 && cowl.Sources[0].Line == "Vendor · Quartermaster Voss · Greenvale · 1g 20s 50c (Lodge)" && cowl.Sources[1].Line.StartsWith("Quest · Scout the Vale · lvl 12"), string.Join(" || ", cowl.Sources.Select(s => s.Line)));
                var chest = Slot(picks, 5);
                Check("chest: nothing is worn, so everything she can use is an upgrade: the Studded Leather Vest (she can craft it) and the Padded Vest (a vendor), 'new' instead of a percentage, the items' own stats in words",
                    Joined(Ids(chest)) == "111,110" && chest.WornId == 0 && chest.Upgrades.All(c => c.Pct == null) && chest.Upgrades[0].Delta == "+8 Agility, +5 Stamina, +40 armor (empty slot)", Joined(Ids(chest)) + " " + chest.Upgrades[0].Delta);
                Check("sources: a recipe - 'Crafted · Leatherworking 110 · You can make it' (Aela knows it and has the skill); a vendor of the baseline has no price: 'Vendor · Peddler Nim · Greenvale (Classic)'",
                    chest.Upgrades[0].Best.Line == "Crafted · Leatherworking 110 · You can make it" && chest.Upgrades[0].Best.CanCraftNow && chest.Upgrades[1].Best.Line == "Vendor · Peddler Nim · Greenvale (Classic)", chest.Upgrades[0].Best.Line + " | " + chest.Upgrades[1].Best.Line);
                var rings = Slot(picks, 11); var rings2 = Slot(picks, 12);
                Check("rings: slot 1 (wearing the Copper Band) is offered the Mystery Ring (known only from a Lodge member's game, flagged) and the Gold Band - not the Silver Band she wears in the other slot; slot 2 not the Copper Band",
                    Joined(Ids(rings)) == "555,122" && rings.Upgrades[0].Revealed && !rings.Upgrades[1].Revealed && Joined(Ids(rings2)) == "555,122" && !set.BySlot[11].Any(c => c.Item.Id == 121) && !set.BySlot[12].Any(c => c.Item.Id == 120) && rings.SlotName == "Ring 1" && rings2.SlotName == "Ring 2" && rings.WornId == 120 && rings2.WornId == 121,
                    Joined(Ids(rings)) + " / " + Joined(Ids(rings2)));
                Check("rings: the Gold Band's best source is the Lodge's 9 % drop; the baseline's 0.5 % drop and the quest that needs level 40 come after it",
                    rings.Upgrades[1].Best.Line == "Drop · Plains Stalker · Greenvale · 9% (Lodge)" && rings.Upgrades[1].Sources.Count == 3 && rings.Upgrades[1].Sources.Skip(1).Any(s => s.NeedsLevel == 40), string.Join(" || ", rings.Upgrades[1].Sources.Select(s => s.Line)));
                var trinkets = Slot(picks, 13); var trinkets2 = Slot(picks, 14);
                Check("trinkets: two slots, the same Lucky Charm offered to both (nothing worn in either)", Joined(Ids(trinkets)) == "130" && Joined(Ids(trinkets2)) == "130" && trinkets.SlotName == "Trinket 1" && trinkets2.SlotName == "Trinket 2");
                var back = Slot(picks, 15);
                Check("back: a cloak is cloth for everybody", Joined(Ids(back)) == "125");
                var mh = Slot(picks, 16); var oh = Slot(picks, 17);
                Check("main hand: the weapon with the most damage and Agility first - the two-hand greatsword (+379 %) and the Great Staff (+364 %), then the Twin Axe (+162 %); a two-hand weapon does not say 'replaces your off hand' when nothing is in it",
                    Joined(Ids(mh)) == "176,142,141" && !mh.Upgrades[0].ReplacesOffhand && mh.Upgrades[0].Delta.EndsWith("(score +379%)") && mh.Upgrades[1].Delta.EndsWith("(score +364%)") && mh.Upgrades[2].Delta.EndsWith("(score +162%)"), Joined(Ids(mh)) + " " + string.Join(" | ", mh.Upgrades.Select(c => c.Delta)));
                Check("off hand: a one-hand weapon in an empty off hand (a Hunter dual wields from 20), halved damage, no shield (not hers), no two-hand weapon; 'new' with the plain stats",
                    Joined(Ids(oh)) == "141,175,177" && oh.Upgrades.All(c => c.Pct == null && c.Delta.EndsWith("(empty slot)")) && !set.BySlot[17].Any(c => c.Item.Id == 143 || c.Item.Id == 142), Joined(Ids(oh)));
                var ranged = Slot(picks, 18);
                Check("ranged: the Long Bow in place of the Short Bow - damage per second counts most for a Hunter: '+5.0 DPS, +2 Agility (score +103%)'; the wand is not hers", Joined(Ids(ranged)) == "151" && ranged.Upgrades[0].Delta == "+5.0 DPS, +2 Agility (score +103%)" && !set.BySlot[18].Any(c => c.Item.Id == 152), Joined(Ids(ranged)) + " " + ranged.Upgrades.FirstOrDefault()?.Delta);
                Check("empty result: slots with nothing better (neck, shoulders, waist, legs, feet, wrists, hands) come back empty and without a note; the data notes count what has no source",
                    new[] { 2, 3, 6, 7, 8, 9, 10 }.All(s => Slot(picks, s).Upgrades.Count == 0 && Slot(picks, s).Note == "") && set.WithoutSource >= 2 && picks.Count == 17);

                // ---------------------------------------------------------------------------------------------------------------- the filters
                var noDungeon = UpgradeEngine.Pick(set, new UpgradeFilters { Dungeons = false });
                var tradeable = UpgradeEngine.Pick(set, new UpgradeFilters { TradeableOnly = true });
                var noClassic = UpgradeEngine.Pick(set, new UpgradeFilters { Classic = false });
                Check("filters: dungeons off leaves the cowl alone in the head slot (the crown is a dungeon drop, the helm a raid) and says one better item is hidden", Joined(Ids(Slot(noDungeon, 1))) == "101" && Slot(noDungeon, 1).Hidden == 2, Joined(Ids(Slot(noDungeon, 1))) + " hidden " + Slot(noDungeon, 1).Hidden);
                Check("filters: only tradeable drops what binds when picked up (the crown) and keeps the cowl, the Gold Band, the bow and the vest (bind when equipped is tradeable until worn)", Joined(Ids(Slot(tradeable, 1))) == "101" && Slot(tradeable, 1).Hidden >= 1 && Joined(Ids(Slot(tradeable, 5))) == "111,110" && Joined(Ids(Slot(tradeable, 18))) == "151");
                Check("filters: without the Classic baseline's sources only what the Lodge saw or the game's recipes say remains - the vest she can craft stays, the baseline-only Padded Vest and the Twin Axe go; the cowl stays through the Lodge's vendor",
                    Joined(Ids(Slot(noClassic, 5))) == "111" && Joined(Ids(Slot(noClassic, 1))) == "105,101" && !Slot(noClassic, 16).Upgrades.Any(c => c.Item.Id == 141) && Slot(noClassic, 5).Hidden == 1, Joined(Ids(Slot(noClassic, 5))) + " | " + Joined(Ids(Slot(noClassic, 16))));
                var soon3 = UpgradeEngine.Pick(set, new UpgradeFilters { LevelOffset = 3 });
                Check("filters: 'coming soon' (up to +3 levels) shows the Cap of the Coming Day (level 32) apart, with the level it needs; level 34 is too far; off by default; the usable list does not change",
                    Joined(Slot(soon3, 1).Soon.Select(c => c.Item.Id.ToString())) == "107" && Slot(soon3, 1).Soon[0].Soon && Slot(soon3, 1).Soon[0].NeedsLevel == 32 && Slot(picks, 1).Soon.Count == 0 && Joined(Ids(Slot(soon3, 1))) == "105,101", Joined(Slot(soon3, 1).Soon.Select(c => c.Item.Id.ToString())));
                Check("filters: per slot at most the number asked for (3 by default, the top ones kept in order)", Slot(UpgradeEngine.Pick(set, new UpgradeFilters { PerSlot = 1 }), 16).Upgrades.Count == 1 && Slot(UpgradeEngine.Pick(set, new UpgradeFilters { PerSlot = 1 }), 16).Upgrades[0].Item.Id == 176 && picks.All(p => p.Upgrades.Count <= 3));
                var f1 = new UpgradeFilters { Dungeons = false, Raids = true, Classic = false, TradeableOnly = true, LevelOffset = 3 };
                var f2 = UpgradeFilters.Parse(f1.Serialize());
                Check("filters: saved as one short text and read back (D0R1C0T1L3N1A0F1); an empty, damaged or unknown text gives the defaults (dungeons on, raids off, Classic on)", f1.Serialize() == "D0R1C0T1L3N1A0F1" && !f2.Dungeons && f2.Raids && !f2.Classic && f2.TradeableOnly && f2.LevelOffset == 3
                    && UpgradeFilters.Parse(null).Dungeons && !UpgradeFilters.Parse("").Raids && UpgradeFilters.Parse("zzz").Classic && UpgradeFilters.Parse("D9R9").Dungeons && UpgradeFilters.Parse("L7").LevelOffset == 0);
                Check("filters: Pick never changes what the cache holds (the result is a copy): the same set with the raid filter and without gives the same cached candidates", set.BySlot[1].Count(c => c.Item.Id == 104) == 1 && set.BySlot[1].First(c => c.Item.Id == 104).Sources.Count == 1);

                // ---------------------------------------------------------------------------------------------------------------- reach and ranking
                var fx = new UpgradeFilters();
                double R(UpgradeSource s, UpgradeChar who = null) => UpgradeEngine.ReachOf(s, who ?? aela, fx);
                Check("reach: a vendor is a sure thing (1.0), a quest 0.9, a craft you can make now 0.95 and one a Lodge member can make 0.8, a craft with nobody and no skill 0.35",
                    R(new UpgradeSource { Kind = "vendor" }) == 1.0 && R(new UpgradeSource { Kind = "quest" }) == 0.9 && R(new UpgradeSource { Kind = "craft", CanCraftNow = true }) == 0.95 && R(new UpgradeSource { Kind = "craft", OwnMaker = true }) == 0.9
                    && R(new UpgradeSource { Kind = "craft", LodgeMaker = true, Makers = { "Bob can make it" } }) == 0.8 && R(new UpgradeSource { Kind = "craft" }) == 0.35 && R(new UpgradeSource { Kind = "craft", ProfessionSkill = 120, Skill = 110 }) == 0.6 && R(new UpgradeSource { Kind = "craft", ProfessionSkill = 50, Skill = 110 }) == 0.45);
                Check("reach: a drop grows with its chance (30 % -> 0.8, 10 % -> 0.5), a rare one (under 2 %) never rises above 0.2, a dungeon x 0.9, a raid x 0.55, a creature 10 levels above the character x 0.55, a quest she cannot take yet x 0.4, what the Lodge saw +0.05",
                    Math.Abs(R(new UpgradeSource { Kind = "drop", Chance = 30 }) - 0.8) < 1e-9 && Math.Abs(R(new UpgradeSource { Kind = "drop", Chance = 10 }) - 0.5) < 1e-9 && R(new UpgradeSource { Kind = "drop", Chance = 1.5 }) <= 0.2 && Math.Abs(R(new UpgradeSource { Kind = "drop", Chance = 30, Dungeon = true }) - 0.72) < 1e-9
                    && Math.Abs(R(new UpgradeSource { Kind = "drop", Chance = 30, Raid = true }) - 0.44) < 1e-9 && Math.Abs(R(new UpgradeSource { Kind = "drop", Chance = 30, Level = 40 }) - 0.44) < 1e-9 && Math.Abs(R(new UpgradeSource { Kind = "quest", NeedsLevel = 40 }) - 0.36) < 1e-9
                    && Math.Abs(R(new UpgradeSource { Kind = "drop", Chance = 30, Lodge = true }) - 0.85) < 1e-9);
                Check("ranking: with the same gain the reachable item comes first - a 40 % gain from a sure vendor beats a 60 % gain from a 0.5 % drop (rank = gain x (0.35 + 0.65 x reach)); the Mystery Ring with a huge gain from a 6 % drop still beats the Gold Band's smaller gain",
                    rings.Upgrades[0].Rank > rings.Upgrades[1].Rank && 40 * (0.35 + 0.65 * 1.0) > 60 * (0.35 + 0.65 * R(new UpgradeSource { Kind = "drop", Chance = 0.5 })));

                // ---------------------------------------------------------------------------------------------------------------- Borg, Cass, Dru, Wren
                var setB = UpgradeEngine.Compute(borg);
                var picksB = UpgradeEngine.Pick(setB, f0);
                Check("Borg (level 45 Hunter): mail is allowed from 40 - the Chain Coif and Gilded Mail Hauberk are candidates for him and were not for Aela; the Horde vendor's cap and the Horde quest's cap are his, not Aela's",
                    setB.BySlot[1].Any(c => c.Item.Id == 102) && setB.BySlot[1].Any(c => c.Item.Id == 109) && setB.BySlot[1].Any(c => c.Item.Id == 106) && setB.BySlot[5].Any(c => c.Item.Id == 113) && !set.BySlot[1].Any(c => c.Item.Id == 109 || c.Item.Id == 106),
                    Joined(setB.BySlot[1].Select(c => c.Item.Id.ToString())));
                Check("Borg: plate is still not his; the vest he cannot craft says so - 'Crafted · Leatherworking 110 · Aela can make it' (another of my characters knows the recipe)",
                    !setB.BySlot[5].Any(c => c.Item.Id == 112) && setB.BySlot[5].First(c => c.Item.Id == 111).Best.Line == "Crafted · Leatherworking 110 · Aela can make it" && setB.BySlot[5].First(c => c.Item.Id == 111).Best.OwnMaker, setB.BySlot[5].First(c => c.Item.Id == 111).Best.Line);
                var q106 = setB.BySlot[1].First(c => c.Item.Id == 106).Best;
                Check("faction: the Horde quest 'Warchief's Errand' is on Borg's list (an Orc) with its level; the Horde vendor is a vendor of his own side", q106.Line.StartsWith("Quest · Warchief's Errand · lvl 20") && setB.BySlot[1].First(c => c.Item.Id == 109).Best.What == "Quartermaster Grosh");
                var setC = UpgradeEngine.Compute(cass); var picksC = UpgradeEngine.Pick(setC, f0);
                var cMh = Slot(picksC, 16); var cOh = Slot(picksC, 17);
                Check("two-hand (Paladin with a Heavy Mace): a bigger two-hand weapon is an upgrade (the Great Maul +24 %, the Greatmace of Might +9 %), a one-hand mace is not (it would give up the two-hand damage), the off hand slot says she wields a two-hand weapon and offers nothing - not even the Tower Shield",
                    Joined(Ids(cMh)) == "163,172" && cMh.Upgrades[0].Delta.EndsWith("(score +24%)") && cOh.Upgrades.Count == 0 && cOh.Note.StartsWith("You wield a two-hand weapon") && !setC.BySlot.ContainsKey(17), Joined(Ids(cMh)) + " " + cMh.Upgrades.FirstOrDefault()?.Delta + " | " + cOh.Note);
                var world50 = UpgradeWorld.Create(cass); var world50W = UpgradeWorld.Create(wren);
                var srcC = world50.SourcesOf(It(163), 50); var srcW = world50W.SourcesOf(It(163), 50);
                Check("class quests: the Paladin's Trial (a Paladin-only quest) is a source for the Paladin and not for the Warrior; the vendor is for both",
                    srcC.Any(s => s.Kind == "quest" && s.What == "The Paladin's Trial") && !srcW.Any(s => s.Kind == "quest") && srcC.Any(s => s.Kind == "vendor") && srcW.Any(s => s.Kind == "vendor"));
                var setW = UpgradeEngine.Compute(wren); var picksW = UpgradeEngine.Pick(setW, f0);
                var wMh = Slot(picksW, 16);
                Check("two-hand (Warrior with two swords): a much better two-hand sword wins against both hands together and says 'replaces your off hand'; the weak one (less than the pair) is not offered; the shield is no better than the sword in the off hand",
                    wMh.Upgrades.Any(c => c.Item.Id == 172 && c.ReplacesOffhand && c.Delta.EndsWith("replaces your off hand)")) && !setW.BySlot[16].Any(c => c.Item.Id == 173) && (!setW.BySlot.TryGetValue(17, out var wl) || !wl.Any(c => c.Item.Id == 143)),
                    Joined(Ids(wMh)) + " " + string.Join(" | ", wMh.Upgrades.Select(c => c.Delta)));
                var setD = UpgradeEngine.Compute(dru);
                Check("Rogue (level 25): the Stiletto beats the dagger in the main hand, a second one-hand weapon is allowed in the off hand, and no two-hand weapon is ever offered", setD.BySlot[16].Any(c => c.Item.Id == 175) && setD.BySlot[17].Any(c => c.Item.Id == 175) && !setD.BySlot[16].Any(c => c.Item.Id == 176 || c.Item.Id == 142) && setD.BySlot[16].All(c => c.Item.InventoryType != 17));

                // ---------------------------------------------------------------------------------------------------------------- the cache and the weights
                int c1 = UpgradeEngine.ComputeCount;
                UpgradeEngine.Compute(aela); UpgradeEngine.Compute(aela); UpgradeEngine.Pick(set, f1);
                Check("cache: the same character, gear, weights and data are not worked out again (and a filter change never does); the heavy part ran once for each of Aela, Borg, Cass, Wren and Dru",
                    UpgradeEngine.ComputeCount == c1 && c1 - c0 == 5, (UpgradeEngine.ComputeCount - c1) + " " + (c1 - c0));
                var aela2 = UpgradeChar.From(C("Aela")); aela2.Level = 31;
                var aela3 = UpgradeChar.From(C("Aela")); aela3.Worn[1] = 101;
                UpgradeEngine.Compute(aela2); UpgradeEngine.Compute(aela3);
                Check("cache: a new level or a changed piece of gear works it out again", UpgradeEngine.ComputeCount == c1 + 2);
                var ed = UpgradeWeights.Effective("HUNTER", 2); ed["sta"] = 5;
                UpgradeWeights.Set("HUNTER", 2, ed);
                int c2 = UpgradeEngine.ComputeCount;
                var setE = UpgradeEngine.Compute(aela);
                var cowlE = UpgradeEngine.Pick(setE, f0)[0].Upgrades.First(c => c.Item.Id == 101);
                Check("weights: with Stamina at 5 the same cowl reads '+3 Stamina, +6 Agility, +24 armor' - Stamina now matters most; the cache was left behind for the new weights",
                    UpgradeEngine.ComputeCount == c2 + 1 && cowlE.Delta.StartsWith("+3 Stamina, +6 Agility, +24 armor") && UpgradeWeights.Overrides["HUNTER/2"] == "sta=5", cowlE.Delta);
                UpgradeWeights.Reset("HUNTER", 2);
                Check("weights: reset, and the original wording is back", UpgradeEngine.Pick(UpgradeEngine.Compute(aela), f0)[0].Upgrades.First(c => c.Item.Id == 101).Delta.StartsWith("+6 Agility, +3 Stamina"));

                // ---------------------------------------------------------------------------------------------------------------- the Lodge's crafters
                AtlasQuery.Context.Crafters = spells =>
                {
                    var r = new LodgeCrafters { Available = true };
                    r.Rows.Add(new LodgeCrafter { Spell = 5001, Owner = "u1", By = "Tess", Key = "k1", Name = "Bob", Class = "ROGUE", Profession = "Leatherworking", Skill = 150, Max = 150 });
                    return r;
                };
                UpgradeEngine.ClearCache();
                var setL = UpgradeEngine.Compute(borg);
                var vestB = setL.BySlot[5].First(c => c.Item.Id == 111).Best;
                Check("crafters: a Lodge member who can make it is told - 'Crafted · Leatherworking 110 · Aela can make it, Bob (Tess) can make it' (my own characters first), the reach 0.9 for my own, the Lodge's 0.8 when it is the only one",
                    vestB.Line == "Crafted · Leatherworking 110 · Aela can make it, Bob (Tess) can make it" && vestB.LodgeMaker && vestB.OwnMaker, vestB.Line);
                var setL2 = UpgradeEngine.Compute(UpgradeChar.From(C("Wren")));
                var vestW = setL2.BySlot[5].FirstOrDefault(c => c.Item.Id == 111)?.Best;
                Check("crafters: for Wren (no profession) the line lists the character who knows it and the Lodge's maker, and a craft only a Lodge member can make has the reach 0.8", vestW != null && vestW.Line == "Crafted · Leatherworking 110 · Aela can make it, Bob (Tess) can make it" && Math.Abs(UpgradeEngine.ReachOf(new UpgradeSource { Kind = "craft", LodgeMaker = true, Makers = { "Bob (Tess) can make it" } }, wren, fx) - 0.8) < 1e-9, vestW?.Line);
                AtlasQuery.Context.Crafters = spells => new LodgeCrafters { Available = false, Problem = "an older server" };
                UpgradeEngine.ClearCache();
                var setL3 = UpgradeEngine.Compute(UpgradeChar.From(C("Wren")));
                Check("crafters: an older server, a guest or offline adds nothing and breaks nothing", setL3.BySlot[5].First(c => c.Item.Id == 111).Best.Makers.All(m => m.StartsWith("Aela")));
                AtlasQuery.Context.Crafters = null;
                UpgradeEngine.ClearCache();

                // ---------------------------------------------------------------------------------------------------------------- the other layers missing
                ClassicAtlas.Use(null); UpgradeEngine.ClearCache();
                var setNc = UpgradeEngine.Compute(aela);
                var pNc = UpgradeEngine.Pick(setNc, f0);
                Check("without the Classic baseline: only what the Lodge saw and the recipes are listed (the crown, the cowl through the Lodge's vendor, the vest, the bow, the rings); the data says the baseline is missing",
                    !setNc.ClassicData && setNc.LodgeData && Joined(Ids(Slot(pNc, 1))) == "105,101" && Joined(Ids(Slot(pNc, 5))) == "111" && Joined(Ids(Slot(pNc, 18))) == "151", Joined(Ids(Slot(pNc, 1))));
                var lodgeBackup = lodge;
                AtlasQuery.Context.Lodge = () => null; UpgradeEngine.ClearCache();
                var setN2 = UpgradeEngine.Compute(aela);
                Check("without the baseline and without a Lodge: only the game's own recipes are a source - not an error, nothing but the vest in the chest slot, and a count of what has no source", setN2.Problem == "" && UpgradeEngine.Pick(setN2, f0).Where(p => p.Slot != 5).All(p => p.Upgrades.Count == 0) && Joined(Ids(Slot(UpgradeEngine.Pick(setN2, f0), 5))) == "111" && setN2.WithoutSource > 5 && !setN2.LodgeData && !setN2.ClassicData);
                AtlasQuery.Context.Lodge = () => lodge; ClassicAtlas.Use(UpClassic()); UpgradeEngine.ClearCache();
                ItemData.SetForTest(null, ItemData.State.Disabled, "off");
                UpgradeEngine.ClearCache();
                var setOff = UpgradeEngine.Compute(aela);
                Check("game data off: the engine says 'items' (nothing to compare), it does not guess; a character without class or level says 'class'", setOff.Problem == "items" || (setOff.Problem == "" && setOff.BySlot.Count > 0 && setOff.BySlot.Values.SelectMany(l => l).All(c => c.Revealed)) , setOff.Problem);
                ItemData.SetForTest(UpItems(), ItemData.State.Ready); UpgradeEngine.ClearCache();
                Check("a character without a class, or at level 0, or null: a calm 'class' problem / none", UpgradeEngine.Compute(new UpgradeChar { ClassFile = "", Level = 30 }).Problem == "class" && UpgradeEngine.Compute(new UpgradeChar { ClassFile = "HUNTER", Level = 0 }).Problem == "class" && UpgradeEngine.Compute(null).Problem == "none");

                // ---------------------------------------------------------------------------------------------------------------- the Atlas item page
                var pg = AtlasQuery.Build("item:101");
                var fact = pg.Facts.FirstOrDefault(f => f.Label == "Upgrade for");
                Check("item page: 'Upgrade for' names my characters the item would improve - 'Aela (Hunter) +182%', Borg too, and a character with an empty head slot 'fills an empty head slot'", fact != null && fact.Value.Contains("Aela (Hunter) +182%") && fact.Value.Contains("Borg (Hunter) +182%") && fact.Value.Contains("Wren (Warrior) fills an empty head slot") && fact.Source == AtlasLabel.Mine, fact?.Value);
                var pg2 = AtlasQuery.Build("item:102");
                var fact2 = pg2.Facts.FirstOrDefault(f => f.Label == "Upgrade for");
                Check("item page: the Chain Coif (mail) is an upgrade for Borg (45) and the Paladin and not for Aela (30) - the class skills are the same rules as the panel's", fact2 != null && fact2.Value.Contains("Borg (Hunter)") && fact2.Value.Contains("Cass (Paladin)") && !fact2.Value.Contains("Aela"), fact2?.Value);
                Check("item page: nothing for an item that is no better (the Cloth Hood), for a poor item, for a reagent, or an item nobody of mine can wear", AtlasQuery.Build("item:103").Facts.All(f => f.Label != "Upgrade for" || !f.Value.Contains("Aela")) && AtlasQuery.Build("item:191").Facts.All(f => f.Label != "Upgrade for") && AtlasQuery.Build("item:190").Facts.All(f => f.Label != "Upgrade for"));
                AtlasQuery.Context.Characters = () => new List<InvChar>();
                Check("item page: without characters there is no such fact", AtlasQuery.Build("item:101").Facts.All(f => f.Label != "Upgrade for"));
                AtlasQuery.Context.Characters = () => chars;

                UpgradesExtrasChecks(Check, chars, C, hub, now);

                // ---------------------------------------------------------------------------------------------------------------- the panel
                await UpgradesUiChecks(Check, dir, now, chars, hub);

                // ---------------------------------------------------------------------------------------------------------------- the real game files (read-only), when there are any
                UpgradesUsableChecks(Check, aela, notes);
                UpgradesSmokeReal(Check, notes);
            }
            catch (Exception ex) { ok = false; notes.Add("FAIL exception: " + ex); }
            finally
            {
                Width = savedW; Height = savedH;
                AtlasQuery.Context.Lodge = savedLodge; AtlasQuery.Context.Characters = savedChars; AtlasQuery.Context.Crafters = savedCrafters; AtlasQuery.Context.Owned = savedOwned; EnchantData.AutoStart = savedEnchAuto; EnchantData.ResetForTest(); UpgradeExtras.ForgetForTest();
                UpgradeWeights.Overrides = savedOv; UpgradeWeights.SaveOverrides = savedSave;
                UpgradeEngine.ClearCache();
                ClassicAtlas.Use(null); ItemData.ResetForTest(); SpellData.ResetForTest(); SeenData.ResetForTest(); MapData.ResetForTest(); ClassData.ResetForTest(); MapRender.ForgetFailuresForTest();
                HubData.ResetCache();
                IconStore.ForgetMemory();
                InventoryView.ResetViewForTest();
                InventoryView.UseUpgradeSettings(() => settings.UpgradeFilter, v => { settings.UpgradeFilter = v; SettingsStore.Save(settings); });
                InventoryView.Init(() => settings.WowRoot);
                ShowTab("addons");
            }
            notes.Insert(0, "upgrades (2.55.0): " + (ok ? "all ok" : "FAILURES"));
            return string.Join("\r\n", notes);
        }
    }
}
