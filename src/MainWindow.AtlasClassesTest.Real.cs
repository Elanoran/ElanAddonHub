using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Db2;

namespace ElansAddonHub
{
    // --selftest "atlasclasses", part 3: a read-only look at the real game files, only when there is a WoW install with the Forever client on this PC. Nothing is written
    // into the game folder. Screenshots of the real pages are only taken when ELANSHUB_CLASSES_SHOTS names a folder (they show game art and never go into the repo).
    public partial class MainWindow
    {
        async Task ClassesSmokeReal(CheckFn Check, List<string> notes, string dir)
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
                var cset = ClassParse.All(ClassData.ReadCasc(real, "_classic_beta_"));
                long ms = sw.ElapsedMilliseconds;
                var hard = cset.Problems.Where(p => p.Value.StartsWith("game updated")).ToList();
                if (hard.Count > 0) { notes.Add("skip real game files: " + string.Join("; ", hard.Select(p => p.Value)) + " (this Outpost does not know that game build's tables; nothing was guessed)"); return; }
                var sset = SpellParse.All(SpellData.ReadCasc(real, "_classic_beta_"));
                var tset = TalentParse.All(TalentData.ReadCasc(real, "_classic_beta_"));
                ClassData.SetForTest(cset); SpellData.SetForTest(sset); TalentData.SetForTest(tset);
                var d = cset.Data;
                var names = d.ClassList().Select(c => c.Name).ToList();
                Check("real game files: 9 classes - Warrior, Paladin, Hunter, Rogue, Priest, Shaman, Mage, Warlock, Druid (ids 1-5, 7-9, 11)",
                    names.Count == 9 && new[] { "Warrior", "Paladin", "Hunter", "Rogue", "Priest", "Shaman", "Mage", "Warlock", "Druid" }.All(names.Contains) && d.Class(3).File == "HUNTER" && d.Class(3).IconId > 0 && d.Class(3).HasColor, string.Join(",", names));
                var races = d.PlayableRaces().Select(r => r.Name).ToList();
                Check("real game files: the playable races include the eight of Classic (Human, Dwarf, Night Elf, Gnome, Orc, Undead, Tauren, Troll) on the right sides; a race that is only in the table (Goblin) is not playable",
                    new[] { "Human", "Dwarf", "Night Elf", "Gnome", "Orc", "Undead", "Tauren", "Troll" }.All(races.Contains) && d.RaceByName("Human").Side == RaceInfo.Alliance && d.RaceByName("Troll").Side == RaceInfo.Horde && d.Races.Values.Any(r => r.Name == "Goblin" && !r.Playable), string.Join(",", races));
                Check("real game files: Humans can be a Hunter but Gnomes cannot; Tauren can be a Warrior, Hunter, Shaman and Druid and nothing else; the pairs table has 50+ rows",
                    d.CanBe(d.RaceByName("Human").Id, 3) && !d.CanBe(d.RaceByName("Gnome").Id, 3) && d.ClassesOf(d.RaceByName("Tauren").Id).Select(c => c.Name).OrderBy(x => x).SequenceEqual(new[] { "Druid", "Hunter", "Shaman", "Warrior" }) && d.Pairs.Count > 50, d.Pairs.Count + "");
                Check("real game files: SpellLevels (10,000+ spells), SpellPower (3,000+), SpellCooldowns (4,000+) and the skill rows (150+) are read", d.Levels.Count > 10000 && d.Costs.Count > 3000 && d.Cooldowns.Count > 4000 && d.SkillRows.Count > 150, d.Levels.Count + " " + d.Costs.Count + " " + d.Cooldowns.Count + " " + d.SkillRows.Count);
                var hunter = ClassSpellBook.Build(3);
                var groups = hunter.Groups(new ClassSpellFilter { Pet = false });
                var hawk = hunter.Entries.Where(e => e.Name == "Aspect of the Hawk").OrderBy(e => e.RankNo).ToList();
                Check("real game files: the Hunter has Aspect of the Hawk early - Rank 1 at level 10, then ranks that ascend with the level (18, 28, 38, 48, 58, 60)", hawk.Count >= 6 && hawk[0].Level == 10 && hawk[0].RankNo == 1 && hawk.Zip(hawk.Skip(1), (a, b) => b.Level >= a.Level && b.RankNo > a.RankNo).All(x => x) && hawk[1].Level == 18, string.Join(",", hawk.Select(e => e.Level + "/" + e.RankNo)));
                var arcane = hunter.Entries.Where(e => e.Name == "Arcane Shot").OrderBy(e => e.RankNo).ToList();
                Check("real game files: Arcane Shot's ranks ascend by level (6, 12, 20, 28, 36, 44, 52, 60), rank 1 costs 25 Mana with a 6 sec cooldown",
                    arcane.Select(e => e.Level).SequenceEqual(new[] { 6, 12, 20, 28, 36, 44, 52, 60 }) && arcane.Select(e => e.RankNo).SequenceEqual(Enumerable.Range(1, 8)) && arcane[0].Cost == "25 Mana" && arcane[0].Cooldown == "6 sec cooldown", string.Join(",", arcane.Select(e => e.Level + "/" + e.Cost)));
                Check("real game files: the Hunter's list starts at level 1 (Auto Shot, Raptor Strike, Track Beasts), its tabs are Beast Mastery, Marksmanship, Survival and the pet's Beast Training, and the traps' helper spells are left out",
                    groups[0].Level == 1 && groups[0].Entries.Any(e => e.Name == "Auto Shot") && new[] { "Beast Mastery", "Marksmanship", "Survival", "Beast Training" }.All(n => hunter.Tabs.Any(t => t.Name == n)) && hunter.Entries.Count(e => e.Name == "Immolation Trap" && e.Rank == "Rank 1") == 1
                    && !hunter.Entries.Any(e => e.Name == "Explosive Trap Effect") && groups.Count > 25, string.Join(",", groups.Take(5).Select(g => g.Level)));
                var classes = d.ClassList().Select(c => new { c.Name, Book = ClassSpellBook.Build(c.Id) }).ToList();
                Check("real game files: every class has a spellbook with 100+ spells, a level 1 group and tabs; ranks within a name never go down as the level goes up", classes.All(c => c.Book != null && c.Book.Entries.Count > 100 && c.Book.Groups(new ClassSpellFilter { Pet = false }).Any(g => g.Level == 1) && c.Book.Tabs.Count >= 3
                    && c.Book.Entries.Where(e => e.RankNo > 0 && e.Level > 0 && e.LevelKind == 0).GroupBy(e => e.SkillId + "|" + e.Name).All(g => g.OrderBy(e => e.RankNo).Zip(g.OrderBy(e => e.RankNo).Skip(1), (a, b) => b.Level >= a.Level).All(x => x))),
                    string.Join(",", classes.Select(c => c.Name + ":" + c.Book?.Entries.Count)));
                var sp = AtlasQuery.Build("spell:14281");
                Check("real game files: spell 14281 (Arcane Shot Rank 2) says 'Learned by Hunter, level 12 (rank 2)', has a Cost and a Cooldown, and its tooltip carries both", sp.Facts.Any(f => f.Label == "Learned by" && f.Value == "Hunter, level 12 (rank 2)" && f.Link == "class:3") && sp.Facts.Any(f => f.Label == "Cost" && f.Value == "35 Mana") && sp.Facts.Any(f => f.Label == "Cooldown" && f.Value == "6 sec")
                    && GameTooltip.FromAtlas(sp).Texts().Contains("6 sec cooldown"), string.Join(" | ", sp.Facts));
                Check("real game files: Heroic Strike costs 15 Rage, Fireball 30 Mana, a druid form costs a percentage of base mana", d.CostText(78) == "15 Rage" && d.CostText(133) == "30 Mana" && d.Costs.Values.Any(c => c.Type == 0 && c.Pct > 0 && c.Amount == 0), d.CostText(78) + " " + d.CostText(133));
                var orc = AtlasQuery.Build("race:" + d.RaceByName("Orc").Id); var human = AtlasQuery.Build("race:" + d.RaceByName("Human").Id);
                Check("real game files: the Orc's racial abilities include Blood Fury and the Human's Perception; both have a language, a mount skill and the classes they can be", orc.Section("racial").Rows.Any(r => r.Title == "Blood Fury") && human.Section("racial").Rows.Any(r => r.Title == "Perception")
                    && orc.Facts.Any(f => f.Label == "Language" && f.Value == "Orcish") && orc.Facts.Any(f => f.Label == "Mount skill" && f.Value == "Wolf Riding") && human.Section("classes").Rows.Any(r => r.Title == "Hunter") && !human.Section("classes").Rows.Any(r => r.Title == "Druid"), string.Join(" | ", orc.Facts));
                var orcSpell = AtlasQuery.Build("spell:20572");
                Check("real game files: Blood Fury's spell page says it is a racial ability of the Orc", orcSpell.Facts.Any(f => f.Label == "Racial ability of" && f.Value == "Orc"), string.Join(" | ", orcSpell.Facts));
                var sHunter = AtlasQuery.Search("hunter"); var sSky = AtlasQuery.Search("skyborne");
                Check("real game files: search finds the Hunter class and the Skyborne races (this client's own)", sHunter.Groups.Any(g => g.Kind == "class" && g.Hits.Any(h => h.Name == "Hunter")) && sSky.Groups.Any(g => g.Kind == "race" && g.Hits.Count == 2));
                var bc = AtlasBrowse.Run("browse:classes"); var br = AtlasBrowse.Run("browse:races");
                Check("real game files: browse lists 9 classes and the playable races", bc.Total == 9 && br.Total == races.Count && br.Total >= 8, bc.Total + " " + br.Total);
                notes.Add("note real game files: " + d.Classes.Count + " classes, " + d.Races.Count + " race rows (" + races.Count + " playable), " + d.Pairs.Count + " pairs, " + d.SkillRows.Count + " skill rows, " + d.Levels.Count + " spell levels, " + d.Costs.Count + " costs, " + d.Cooldowns.Count + " cooldowns; read in " + ms + " ms; Hunter spellbook " + hunter.Entries.Count + " spells (" + hunter.HiddenHelpers + " helper / variant spells left out)");
                foreach (var n in cset.Notes) notes.Add("note real game files: " + n);
                foreach (var p in cset.Problems) notes.Add("note real game files: " + p.Key + ": " + p.Value);

                var shots = Environment.GetEnvironmentVariable("ELANSHUB_CLASSES_SHOTS");
                if (!string.IsNullOrEmpty(shots) && Directory.Exists(shots))
                {
                    var wasW = Width; var wasH = Height;
                    try
                    {
                        Width = 1400; Height = 1000;
                        ShowTab("atlas"); await Task.Delay(100);
                        AtlasView.History.Clear();
                        async Task Shot(string id, string file, Func<Task> after = null)
                        {
                            await AtlasView.Open(id); await AtlasView.WaitForTest(); await Task.Delay(500); UpdateLayout();
                            if (after != null) { await after(); await Task.Delay(300); UpdateLayout(); }
                            Snapshot(Path.Combine(shots, file));
                        }
                        await Shot("browse:classes", "classes-list.png");
                        await Shot("class:3", "classes-hunter.png");
                        await Shot("class:3", "classes-hunter-marksmanship.png", async () => { ((Button)AtlasView.ClassControlForTest("tab:163")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Task.Delay(100); });
                        await Shot("class:3", "classes-hunter-new-only.png", async () =>
                        {
                            ((Button)AtlasView.ClassControlForTest("tab:0")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                            var sw2 = (CheckBox)AtlasView.ClassControlForTest("new"); sw2.IsChecked = true; sw2.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                            ((TextBox)AtlasView.ClassControlForTest("min")).Text = "10"; ((TextBox)AtlasView.ClassControlForTest("max")).Text = "30"; await Task.Delay(400);
                        });
                        await Shot("class:1", "classes-warrior.png");
                        await Shot("browse:races", "classes-races-list.png");
                        await Shot("race:" + d.RaceByName("Orc").Id, "classes-race-orc.png");
                        await Shot("spell:14281", "classes-spell-arcane-shot.png");
                    }
                    finally { Width = wasW; Height = wasH; }
                }
            }
            catch (Exception e)
            {
                Check("real game files: the class and race check ran to the end", false, e.ToString());
            }
        }
    }
}
