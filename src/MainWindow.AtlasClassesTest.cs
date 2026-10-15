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
using ElansAddonHub.Services.Atlas.Classic;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Talents;

namespace ElansAddonHub
{
    // --selftest "atlasclasses": the Atlas Classes and Races (ids class:<id> / race:<id>), the class page's spells by level, the spell pages' "Learned by", cost and cooldown.
    // All on the invented world of MainWindow.AtlasClassesTest.Data.cs (tables in the layouts of build 1.60.1.70338, written in the test); the real game files are only
    // touched by the last, optional part (read-only, skipped when there is no WoW install).
    public partial class MainWindow
    {
        async Task<string> AtlasClassesTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            void Check(string what, bool cond, string detail = null) { SelfTestClock.Mark(what); notes.Add((cond ? "ok   " : "FAIL ") + what + (!cond && detail != null ? " [" + detail + "]" : "")); ok &= cond; }
            var work = Path.Combine(dir, "atlasclasses");
            var savedLodge = AtlasQuery.Context.Lodge; var savedAuto = ClassData.AutoStart; var savedCache = ClassData.CacheFile; var savedTrainers = ClassicTrainers.Source;
            var savedWidth = Width; var savedHeight = Height;
            try
            {
                if (Directory.Exists(work)) Util.DeleteDir(work);
                Directory.CreateDirectory(work);
                ClassData.AutoStart = false; ClassData.CacheFile = Path.Combine(work, "classdata.bin");
                ClassicTrainers.Source = null;
                var spellList = CcSpells();

                // ============================================================================================================ the tables
                var set = ClassParse.All(CcRaw(spellList));
                var d = set.Data;
                Check("tables: classes, races and pairs parse - 3 classes (names, files, colours, roles), 4 race rows of which 3 are playable (the Goblin is in no pair), 6 pairs; no problem",
                    set.Problems.Count == 0 && d.Classes.Count == 3 && d.Class(3).Name == "Hunter" && d.Class(3).File == "HUNTER" && d.Class(3).ColorHex == "#AAD372" && d.Class(3).RoleText == "Damage" && d.Class(1).RoleText == "Tank, Damage"
                    && d.Races.Count == 4 && d.PlayableRaces().Count == 3 && !d.Race(9).Playable && d.Pairs.Count == 6 && d.CanBe(1, 3) && !d.CanBe(4, 1), set.Problems.Count + " " + string.Join(",", set.Problems.Values));
                Check("tables: races carry side, display order, race bit, lore name and faction template; the playable ones sort Alliance first (Human, Night Elf), then Horde (Orc)",
                    d.Race(2).Side == RaceInfo.Horde && d.Race(1).Side == RaceInfo.Alliance && d.Race(4).LoreName == "Kaldorei" && d.Race(2).Bit == 1 && d.Race(2).BitMask == 2 && d.Race(2).FactionTemplate == 2
                    && string.Join(",", d.PlayableRaces().Select(r => r.Name)) == "Human,Night Elf,Orc", string.Join(",", d.PlayableRaces().Select(r => r.Name)));
                Check("tables: ChrClassesXPowerTypes gives each class its resources (Warrior: Rage, Hunter: Mana and Focus)", d.Class(1).PowerTypes.SequenceEqual(new[] { 1 }) && d.Class(3).PowerTypes.SequenceEqual(new[] { 0, 2 }) && d.Class(8).PowerTypes.SequenceEqual(new[] { 0 }),
                    string.Join("/", d.Classes.Values.Select(c => string.Join(",", c.PowerTypes))));
                Check("tables: SkillRaceClassInfo rows keep the two race-mask halves (Skyborne-style bits above 31 stay), SpellLevels / SpellPower / SpellCooldowns are read by spell",
                    d.HasSkillRows && d.SkillRows.Count > 20 && d.SkillRows.Any(r => r.Skill == 148 && r.Races == (0xFEUL | (3UL << 32))) && d.HasLevels && d.Level(3044).Value.Base == 6 && d.HasCosts && d.HasCooldowns, d.SkillRows.Count + " " + d.Levels.Count);
                Check("tables: the cost text follows the game - '25 Mana', rage is stored x10 ('15 Rage'), percent of base mana, nothing for a free spell; the cooldown is the spell's own (6 sec) or its category's",
                    d.CostText(3044) == "25 Mana" && d.CostText(78) == "15 Rage" && d.CostText(1459) == "4.5% of base mana" && d.CostText(100) == null && d.CostText(999999) == null && d.CooldownMs(3044) == 6000 && ClassSnapshot.CooldownText(6000) == "6 sec cooldown"
                    && ClassSnapshot.CooldownText(480000) == "8 min cooldown" && ClassSnapshot.CooldownText(0) == null && ClassSnapshot.CooldownText(3600000) == "1 hr cooldown", d.CostText(3044) + " " + d.CostText(78) + " " + d.CostText(1459));

                // unknown layouts: a required table makes the layer unavailable (calm note), an optional one only drops its feature, a foreign table is refused
                var bad = ClassParse.All(CcRaw(spellList, n => n == "ChrRaces" ? 0xDEADBEEF : (uint?)null));
                Check("unknown layout: ChrRaces with a layout this version does not know = the whole layer is unavailable, with the table named in the note; nothing is guessed, nothing thrown",
                    bad.Problems.ContainsKey("all") && bad.Problems["all"].Contains("ChrRaces") && bad.Problems["all"].Contains("DEADBEEF") && bad.Data.Classes.Count == 0, string.Join(",", bad.Problems.Values));
                var partial = ClassParse.All(CcRaw(spellList, n => n == "SpellPower" ? 0x11111111 : (uint?)null));
                Check("unknown layout: SpellPower of another layout only drops the costs - classes, races, levels and cooldowns are still there", !partial.Problems.ContainsKey("all") && partial.Problems.ContainsKey("SpellPower") && partial.Data.Classes.Count == 3 && !partial.Data.HasCosts && partial.Data.HasLevels && partial.Data.HasCooldowns);
                var missing = ClassParse.All(CcRaw(spellList, null, "CharBaseInfo"));
                Check("missing table: no CharBaseInfo in the install = unavailable (nothing to say who can be what), a calm note", missing.Problems.ContainsKey("all") && missing.Problems["all"].Contains("CharBaseInfo"), string.Join(",", missing.Problems.Values));
                var foreign = CcRaw(spellList); foreign.Tables[ClassTables.Races.Name] = CcRaces(CcLayoutRaces).Take(8).Concat(new byte[] { 1, 2, 3, 4 }).ToArray();
                Check("damaged table: a cut-off file is reported, not thrown", ClassParse.All(foreign).Problems.ContainsKey("all"));
                var noOpt = ClassParse.All(CcRaw(spellList, null, "SkillRaceClassInfo", "SpellLevels", "SpellPower", "SpellCooldowns", "ChrClassesXPowerTypes"));
                Check("missing optional tables: classes and races work, the spell tables are simply not there (no skill rows, no levels, no costs)", !noOpt.Problems.ContainsKey("all") && noOpt.Data.Classes.Count == 3 && !noOpt.Data.HasSkillRows && !noOpt.Data.HasLevels && !noOpt.Data.HasCosts && !noOpt.Data.HasCooldowns);

                // the saved index
                var saveFile = Path.Combine(work, "classdata.bin");
                bool wrote = ClassIndexFile.TryWrite(saveFile, "build-1", set);
                var back = ClassIndexFile.TryRead(saveFile);
                Check("saved index: written and read back (classes, races, pairs, skill rows, levels, costs, cooldowns, colours, resources) for the build it was made from",
                    wrote && back != null && back.Build == "build-1" && back.Set.Data.Classes.Count == 3 && back.Set.Data.PlayableRaces().Count == 3 && back.Set.Data.Pairs.Count == 6 && back.Set.Data.SkillRows.Count == d.SkillRows.Count && back.Set.Data.Levels.Count == d.Levels.Count
                    && back.Set.Data.CostText(78) == "15 Rage" && back.Set.Data.CooldownMs(3044) == 6000 && back.Set.Data.Class(3).ColorHex == "#AAD372" && back.Set.Data.Class(3).PowerTypes.SequenceEqual(new[] { 0, 2 }) && back.Set.Data.Race(2).Description.StartsWith("The orc"));
                int reads = 0;
                var fromCache = ClassData.Obtain(back, "build-1", saveFile, () => { reads++; return set; }, out var from1);
                var fromGame = ClassData.Obtain(back, "build-2", saveFile, () => { reads++; return set; }, out var from2);
                Check("saved index: the same game build is not read again; a new build reads the game files (and saves them)", from1 == "cache file" && from2 == "game files" && reads == 1 && ClassIndexFile.TryRead(saveFile).Build == "build-2");
                File.WriteAllBytes(saveFile, new byte[] { 1, 2, 3, 4, 5 });
                Check("saved index: a damaged file is ignored (null), not thrown", ClassIndexFile.TryRead(saveFile) == null && ClassIndexFile.TryRead(Path.Combine(work, "nothing.bin")) == null);

                // ============================================================================================================ the world for the pages
                ClassData.SetForTest(set);
                SpellData.SetForTest(CcSpellSet(spellList));
                TalentData.SetForTest(CcTalents());
                ItemData.SetForTest(new ItemSnapshot(new ClientItem[0], new ClientItem[0]), ItemData.State.Ready);
                SeenData.SetForTest(null, SeenData.State.NotStarted);
                ClassicAtlas.Use(null);
                AtlasBrowse.ForgetForTest();
                AtlasQuery.Context.Lodge = () => null;

                // ============================================================================================================ ids, routing, search
                Check("ids: class:<id> and race:<id> build, parse and normalise ('Class 3', 'race=2'); other kinds are untouched",
                    AtlasIds.Class(3) == "class:3" && AtlasIds.Race(2) == "race:2" && AtlasIds.TryParse("class:3", out var k1, out var n1) && k1 == "class" && n1 == 3 && AtlasIds.TryParse("race:95", out var k2, out var n2) && k2 == "race" && n2 == 95
                    && !AtlasIds.TryParse("class:0", out _, out _) && AtlasQuery.NormalizeId("Class 3") == "class:3" && AtlasQuery.NormalizeId("race=2") == "race:2" && AtlasQuery.NormalizeId("spell 133") == "spell:133");
                Check("ids: the kinds have names, plurals and glyphs and sort after the other kinds", AtlasKinds.Singular("class") == "Class" && AtlasKinds.Plural("race") == "Races" && AtlasKinds.Glyph("class").Length == 1 && AtlasKinds.Glyph("race").Length == 1 && Array.IndexOf(AtlasKinds.Order, "class") > Array.IndexOf(AtlasKinds.Order, "faction"));
                var cc = AtlasCategories.Find("classes"); var cr = AtlasCategories.Find("races");
                Check("categories: Classes and Races are in the Character group of the sub-navigation (own rows, after Spells), list categories that open browse:classes / browse:races",
                    cc != null && cr != null && cc.Group == AtlasNav.Character && cr.Group == AtlasNav.Character && cc.OpenId == "browse:classes" && cr.OpenId == "browse:races" && cc.Kind == "class" && cr.Kind == "race" && cc.Glyph.Length > 0
                    && AtlasNav.Entries().Where(e => e.Group == AtlasNav.Character).Select(e => e.Title).ToList() is var navRows && navRows.Contains("Classes") && navRows.Contains("Races") && navRows.IndexOf("Classes") > navRows.IndexOf("Spells and talents") - 1, "");
                Check("nav: a class page and a race page belong to their nav rows (classes, races) for the highlighted row and the breadcrumb", AtlasNav.KeyFor("class:3") == "classes" && AtlasNav.KeyFor("race:2") == "races" && AtlasNav.CategoryCrumb("class").Title == "Classes" && AtlasNav.CategoryCrumb("race").Link == "browse:races");
                var dsc = AtlasNav.Describe("class:3"); var dsr = AtlasNav.Describe("race:2");
                Check("nav: Recent / Pinned / palette describe the pages - the class with its icon, the race with its side", dsc != null && dsc.Title == "Hunter" && dsc.IconId == 626000 && dsc.Detail.StartsWith("Class") && dsr != null && dsr.Title == "Orc" && dsr.Detail.Contains("Horde"), dsc?.Detail + " | " + dsr?.Detail);
                var sHunter = AtlasQuery.Search("hunter");
                var gClass = sHunter.Groups.FirstOrDefault(g => g.Kind == "class");
                Check("search: 'hunter' finds the class (with its icon and a role / resource line) in a group Classes, ahead of the spells that merely contain the word",
                    gClass != null && gClass.Title == "Classes" && gClass.Hits[0].Id == "class:3" && gClass.Hits[0].IconId == 626000 && gClass.Hits[0].Detail.Contains("Damage") && sHunter.Groups.IndexOf(gClass) <= 1, string.Join(" | ", sHunter.Groups.Select(g => g.Kind + ":" + g.Hits.Count)));
                var sOrc = AtlasQuery.Search("orc"); var sKal = AtlasQuery.Search("kaldorei");
                Check("search: 'orc' finds the race; the lore name ('kaldorei') finds the Night Elf; the Goblin (not playable) is never offered",
                    sOrc.Groups.Any(g => g.Kind == "race" && g.Hits.Any(h => h.Id == "race:2")) && sKal.Groups.Any(g => g.Kind == "race" && g.Hits.Any(h => h.Id == "race:4")) && !AtlasQuery.Search("goblin").Groups.Any(g => g.Kind == "race") && AtlasQuery.Describe("race:9") == null);
                var byId = AtlasQuery.Search("class:3");
                Check("search: 'class:3' goes straight to the class", byId.ById && byId.Groups[0].Hits[0].Name == "Hunter");
                var pal = AtlasPaletteSearch.Run("warrior", "pages", default(System.Threading.CancellationToken));
                Check("palette: the Pages scope also finds classes and races (they are pages of the Atlas); the Items scope does not", pal.All.Any(h => h.Id == "class:1") && !AtlasPaletteSearch.Run("warrior", "items", default(System.Threading.CancellationToken)).All.Any(h => h.Kind == "class"));

                Check("characters: a character's race and class become Atlas links by the race's name (the lore name too) and the class file or name; a race or class the game data does not have gives none",
                    CharacterLinks.Race("Orc") == "race:2" && CharacterLinks.Race("Night Elf") == "race:4" && CharacterLinks.Race("Kaldorei") == "race:4" && CharacterLinks.Race("Goblin") == null && CharacterLinks.Race("") == null
                    && CharacterLinks.Class("HUNTER", "Hunter") == "class:3" && CharacterLinks.Class("", "Mage") == "class:8" && CharacterLinks.Class("mage", null) == "class:8" && CharacterLinks.Class("DEATHKNIGHT", "Death Knight") == null);

                // ============================================================================================================ the lists
                var bc = AtlasBrowse.Run("browse:classes"); var br = AtlasBrowse.Run("browse:races");
                Check("browse: browse:classes lists the 3 classes with their icons and a role line, each opening class:<id>; the count reads '3 classes'",
                    !bc.NotFound && bc.Total == 3 && bc.Rows.Select(r => r.Link).OrderBy(x => x).SequenceEqual(new[] { "class:1", "class:3", "class:8" }) && bc.Rows.All(r => r.IconId > 0) && bc.CountText == "3 classes", bc.Total + " " + bc.CountText);
                Check("browse: browse:races lists the 3 playable races (not the Goblin), grouped by side in the sub-categories (Alliance 2, Horde 1)",
                    !br.NotFound && br.Total == 3 && br.Rows.All(r => r.Link.StartsWith("race:")) && !br.Rows.Any(r => r.Link == "race:9") && br.Children.Any(c => c.Title == "Alliance" && c.Count == 2) && br.Children.Any(c => c.Title == "Horde" && c.Count == 1), br.Children.Count + "");

                // ============================================================================================================ the class page (model)
                var hp = AtlasQuery.Build("class:3");
                var book = hp.Custom as ClassSpellBook;
                Check("class page: Hunter - title, icon, the class colour as the accent, subtitle 'Class 3 · HUNTER', facts (roles, resource, start level, the Talents link)",
                    !hp.NotFound && hp.Title == "Hunter" && hp.IconId == 626000 && hp.Accent == "#AAD372" && hp.Subtitle == "Class 3 · HUNTER" && hp.Facts.Any(f => f.Label == "Roles" && f.Value == "Damage") && hp.Facts.Any(f => f.Label == "Resource" && f.Value == "Mana, Focus")
                    && hp.Facts.Any(f => f.Label == "Talents" && f.Link == "talents:tree/hunter"), string.Join(" | ", hp.Facts));
                Check("class page: the armor and the weapon skills of the class are tables of links to the skill pages from the skill lines - Cloth, Leather and Mail (from level 40); Axes, Bows, Staves",
                    string.Join(",", hp.Section("armor").Rows.Select(r => r.Title)) == "Cloth,Leather,Mail" && hp.Section("armor").Rows.Single(r => r.Title == "Mail").Detail == "From level 40" && hp.Section("armor").Rows.All(r => r.Link.StartsWith("skill:"))
                    && string.Join(",", hp.Section("weapons").Rows.Select(r => r.Title).OrderBy(x => x)) == "Axes,Bows,Staves", string.Join(",", hp.Section("armor").Rows.Select(r => r.Title)));
                var wpg = AtlasQuery.Build("class:1");
                Check("class page: Warrior - Plate from level 40, Rage as the resource, Tank and Damage roles; the Hunter's Mail line is not on it", string.Join(",", wpg.Section("armor").Rows.Select(r => r.Title)) == "Cloth,Leather,Plate Mail" && wpg.Facts.Any(f => f.Label == "Resource" && f.Value == "Rage") && wpg.Facts.Any(f => f.Label == "Roles" && f.Value == "Tank, Damage"), string.Join(" | ", wpg.Facts));
                Check("class page: the races that can be a Hunter (Human, Night Elf, Orc: the client's race and class list) are a table of links to the race pages; the Mage's only Human",
                    hp.Section("races") != null && hp.Section("races").Rows.Select(r => r.Title).SequenceEqual(new[] { "Human", "Night Elf", "Orc" }) && hp.Section("races").Rows.All(r => r.Link.StartsWith("race:")) && AtlasQuery.Build("class:8").Section("races").Rows.Count == 1);
                Check("class page: the spellbook is a part of the page (a ClassSpellBook) with the Hunter's tabs - Beast Mastery, Marksmanship and the pet's Beast Training last; the empty Survival tab is not there",
                    book != null && string.Join(",", book.Tabs.Select(t => t.Name)) == "Beast Mastery,Marksmanship,Beast Training" && book.Tabs.Last().Pet && book.HasTalentData && !book.Tabs.Any(t => t.Name == "Survival"), book == null ? "no book" : string.Join(",", book.Tabs.Select(t => t.Name)));
                Check("class page: the page says what is missing - the learning costs are not in the game files and the Classic baseline has no trainer table", hp.Notes.Any(n => n.Contains("learn from a trainer") && n.Contains("Classic baseline")), string.Join(" | ", hp.Notes));
                Check("class page: an unknown class id is a calm 'no class with this id' page; the race page of a non-playable race says it cannot be created", AtlasQuery.Build("class:77").NotFound && AtlasQuery.Build("class:77").Message.Contains("no class") && AtlasQuery.Build("race:9").NotFound && AtlasQuery.Build("race:9").Message.Contains("cannot be created"));

                // ============================================================================================================ spells by level
                var all = book.Groups(new ClassSpellFilter { Pet = false });
                var levels = all.Select(g => g.Level).ToList();
                Check("by level: groups ascend by level (1, 4, 6, 10, 12, 16, 18, 20, 30, 34) with the group of spells that have no level last; the pet's abilities and the talent are not in the default list",
                    levels.SequenceEqual(new[] { 1, 4, 6, 10, 12, 16, 18, 20, 28, 30, 34, 0 }) && !all.SelectMany(g => g.Entries).Any(e => e.Pet || e.Talent) && all.Last().Title == "No level in the game data", string.Join(",", levels));
                string Names(LevelGroup g) => string.Join("; ", g.Entries.Select(e => e.Name + (e.Rank.Length > 0 ? " " + e.Rank : "")));
                Check("by level: level 6 holds Arcane Shot Rank 1 and Hunter's Mark Rank 1; level 10 the first Aspect of the Hawk, Serpent Sting Rank 2 and Tame Beast",
                    Names(all.First(g => g.Level == 6)) == "Arcane Shot Rank 1; Hunter's Mark Rank 1" && Names(all.First(g => g.Level == 10)) == "Aspect of the Hawk Rank 1; Serpent Sting Rank 2; Tame Beast", Names(all.First(g => g.Level == 6)) + " | " + Names(all.First(g => g.Level == 10)));
                var arcane = all.SelectMany(g => g.Entries).Where(e => e.Name == "Arcane Shot").ToList();
                Check("ranks: Arcane Shot's ranks ascend with the level they are learned at (6, 12, 20 = rank 1, 2, 3), each with its own spell and the client's cost (25, 35, 50 Mana) and cooldown (6 sec)",
                    arcane.Count == 3 && arcane.Select(e => e.Level).SequenceEqual(new[] { 6, 12, 20 }) && arcane.Select(e => e.RankNo).SequenceEqual(new[] { 1, 2, 3 }) && arcane.Select(e => e.Cost).SequenceEqual(new[] { "25 Mana", "35 Mana", "50 Mana" }) && arcane.All(e => e.Cooldown == "6 sec cooldown"));
                Check("level kinds: Aspect of the Beast has no learn level but a spell level of 30 (shown as that, labelled); Chimera Shot has neither (the 'No level' group)",
                    all.First(g => g.Level == 30).Entries.First(e => e.Name == "Aspect of the Beast").LevelKind == 1 && all.Last().Entries.Single(e => e.Name == "Chimera Shot").LevelKind == 2 && all.First(g => g.Level == 6).Entries.All(e => e.LevelKind == 0));
                var helpers = all.SelectMany(g => g.Entries).ToList();
                Check("helpers: the trap's costless half (same name and rank) and the 'Effect' spell are left out, and so is the level-less copy of Raptor Strike; the count is kept (3)",
                    helpers.Count(e => e.Name == "Immolation Trap") == 1 && helpers.Count(e => e.Name == "Raptor Strike") == 1 && !helpers.Any(e => e.Name.EndsWith("Effect")) && book.HiddenHelpers >= 3 && helpers.Single(e => e.Name == "Immolation Trap").Cost == "50 Mana", book.HiddenHelpers + "");
                var newOnly = book.Groups(new ClassSpellFilter { Pet = false, NewOnly = true }).SelectMany(g => g.Entries).ToList();
                Check("only new spells: every ranked spell once at its first rank (Arcane Shot at 6, Aspect of the Hawk at 10, Serpent Sting at 4), spells without ranks stay",
                    newOnly.Count(e => e.Name == "Arcane Shot") == 1 && newOnly.Single(e => e.Name == "Arcane Shot").Level == 6 && newOnly.Single(e => e.Name == "Aspect of the Hawk").Level == 10 && newOnly.Single(e => e.Name == "Serpent Sting").Level == 4 && newOnly.Any(e => e.Name == "Tame Beast") && newOnly.Count < helpers.Count);
                var range = book.Groups(new ClassSpellFilter { Pet = false, MinLevel = 10, MaxLevel = 18 });
                Check("level range: 10 to 18 keeps levels 10, 12, 16 and 18 only (no 'No level' group: a spell without a level is outside any range)", range.Select(g => g.Level).SequenceEqual(new[] { 10, 12, 16, 18 }), string.Join(",", range.Select(g => g.Level)));
                var onlyMin = book.Groups(new ClassSpellFilter { Pet = false, MinLevel = 30 });
                Check("level range: a minimum alone works (30 and up: 30, 34)", onlyMin.Select(g => g.Level).SequenceEqual(new[] { 30, 34 }), string.Join(",", onlyMin.Select(g => g.Level)));
                var marks = book.Groups(new ClassSpellFilter { SkillId = 163 }).SelectMany(g => g.Entries).ToList();
                Check("tab: Marksmanship alone holds only its spells (Arcane Shot, Serpent Sting ...), not Aspect of the Hawk", marks.Count > 5 && marks.All(e => e.SkillName == "Marksmanship") && marks.Any(e => e.Name == "Arcane Shot") && !marks.Any(e => e.Name.StartsWith("Aspect")));
                var pet = book.Groups(new ClassSpellFilter { SkillId = 261 }).SelectMany(g => g.Entries).ToList();
                Check("tab: Beast Training is the pet's list - Growl and Claw ranks with their spell levels (labelled), Rank 1 abilities with no level in the 'No level' group",
                    pet.Count == 4 && pet.All(e => e.Pet) && pet.Single(e => e.Name == "Growl" && e.RankNo == 2).Level == 10 && pet.Single(e => e.Name == "Growl" && e.RankNo == 2).LevelKind == 1 && pet.Single(e => e.Name == "Claw" && e.RankNo == 1).LevelKind == 2);
                var text = book.Groups(new ClassSpellFilter { Pet = false, Text = "sting" }).SelectMany(g => g.Entries).ToList();
                Check("search: 'sting' finds both ranks of Serpent Sting (and the Immolation Trap? no: only names, ranks and descriptions that contain it)", text.Count(e => e.Name == "Serpent Sting") == 2 && text.All(e => (e.Name + e.Description + e.Rank).ToLowerInvariant().Contains("sting")), string.Join(",", text.Select(e => e.Name)));
                var withTalents = book.Groups(new ClassSpellFilter { Pet = false, Talents = true }).SelectMany(g => g.Entries).ToList();
                Check("talents: Aimed Shot (a spell of the Hunter's talent tree) is hidden by default and listed, marked as a talent, when asked for", !helpers.Any(e => e.Name == "Aimed Shot") && withTalents.Single(e => e.Name == "Aimed Shot").Talent && AtlasClasses_RowDetail(withTalents.Single(e => e.Name == "Aimed Shot")).Contains("talent"));
                Check("filters: no match = no groups (not an error); an empty filter is the whole spellbook without the pet's list", book.Groups(new ClassSpellFilter { Text = "zzzz" }).Count == 0 && book.Groups(null).Count > 5);
                var row = ClassSpellBook.RowOf(arcane[0]);
                Check("rows: a spell row links to the spell page, shows the rank, the cost and the cooldown, the short description and the client label ('Forever')", row.Link == "spell:3044" && row.Title == "Arcane Shot" && row.Detail == "Rank 1 · 25 Mana · 6 sec cooldown" && row.Extra.StartsWith("An instant shot") && row.Label == AtlasLabel.Forever && row.IconId > 0, row.Detail);

                // the trainer data of the Classic baseline, when a build of it has some: merged and labelled
                ClassicTrainers.Source = spell => spell == 3044 ? new[] { new TrainerOffer { SpellId = 3044, NpcId = 987, NpcName = "Hunter Trainer", Cost = 100, ReqLevel = 6 }, new TrainerOffer { SpellId = 3044, NpcId = 988, NpcName = "Other Trainer", Cost = 120 } } : new TrainerOffer[0];
                var bookT = ClassSpellBook.Build(3);
                var as1 = bookT.Entries.Single(e => e.SpellId == 3044); var rowT = ClassSpellBook.RowOf(as1);
                Check("trainers: with trainer data the cheapest cost to learn is merged into the row (right column '1s' style money) and the line says it is the Classic baseline's, with the trainer's name; spells it has no offer for stay as they were",
                    bookT.HasTrainerData && as1.TrainCost == 100 && rowT.Right.Length > 0 && rowT.Extra.Contains("Classic baseline: trainer") && rowT.Extra.Contains("Hunter Trainer") && rowT.Label == AtlasLabel.Forever && ClassSpellBook.RowOf(bookT.Entries.First(e => e.SpellId == 14281)).Right == "", rowT.Right + " | " + rowT.Extra);
                Check("trainers: with trainer data the page no longer says that costs are missing", !AtlasQuery.Build("class:3").Notes.Any(n => n.Contains("learn from a trainer")));
                ClassicTrainers.Source = null;

                // ============================================================================================================ the race page
                var rp = AtlasQuery.Build("race:2");
                Check("race page: Orc - title, subtitle with the side, facts (Side Horde, language Orcish, mount skill Wolf Riding, start level)",
                    !rp.NotFound && rp.Title == "Orc" && rp.Subtitle == "Race 2 · Horde" && rp.Facts.Any(f => f.Label == "Side" && f.Value == "Horde")
                    && rp.Facts.Any(f => f.Label == "Language" && f.Value == "Orcish" && f.Link == "skill:109") && rp.Facts.Any(f => f.Label == "Mount skill" && f.Value == "Wolf Riding" && f.Link == "skill:149") && rp.Facts.Any(f => f.Label == "Starts at level" && f.Value == "1"), string.Join(" | ", rp.Facts));
                Check("race page: the Human has Common and the Horse Riding that names it alone (the 'everyone else' row of Horse Riding is not its mount skill); the Night Elf's lore name is shown as 'Also called'",
                    AtlasQuery.Build("race:1").Facts.Any(f => f.Label == "Mount skill" && f.Value == "Horse Riding") && AtlasQuery.Build("race:1").Facts.Count(f => f.Label == "Mount skill") == 1 && AtlasQuery.Build("race:1").Facts.Any(f => f.Label == "Language" && f.Value == "Common")
                    && AtlasQuery.Build("race:4").Facts.Any(f => f.Label == "Also called" && f.Value == "Kaldorei"));
                var racial = rp.Section("racial");
                Check("race page: the racial abilities are the spells of the race's racial skill line (Blood Fury, Hardiness), each a link to its spell page; the Human's are not on the Orc's page",
                    racial != null && racial.Rows.Select(r => r.Title).OrderBy(x => x).SequenceEqual(new[] { "Blood Fury", "Hardiness" }) && racial.Rows.All(r => r.Link.StartsWith("spell:") && r.IconId > 0 && r.Extra.Length > 0) && racial.Note.Contains("Orc Racial"), racial?.Note);
                Check("race page: the classes the race can be (Warrior, Hunter) are links to the class pages with icons; the page says where it starts is not in the game files",
                    rp.Section("classes").Rows.Select(r => r.Title).SequenceEqual(new[] { "Warrior", "Hunter" }) && rp.Section("classes").Rows.All(r => r.Link.StartsWith("class:") && r.IconId > 0) && rp.Notes.Any(n => n.Contains("starts") && n.Contains("not in your game files")), string.Join(" | ", rp.Notes));
                Check("race page: the description of the race is a text on the page", rp.Texts.Any(t => t.Title == "About" && t.Body.StartsWith("The orc race")));

                // ============================================================================================================ the spell pages
                var sp = AtlasQuery.Build("spell:14281");
                Check("spell page: Arcane Shot Rank 2 says 'Learned by Hunter, level 12 (rank 2)' and links to the class page; it has the Cost (35 Mana) and Cooldown (6 sec) facts",
                    sp.Facts.Any(f => f.Label == "Learned by" && f.Value == "Hunter, level 12 (rank 2)" && f.Link == "class:3") && sp.Facts.Any(f => f.Label == "Cost" && f.Value == "35 Mana") && sp.Facts.Any(f => f.Label == "Cooldown" && f.Value == "6 sec"), string.Join(" | ", sp.Facts));
                var spAimed = AtlasQuery.Build("spell:19434");
                Check("spell page: a talent's spell says 'Hunter, talent'; a spell with only a spell level says so ('level 30 (spell level)'); one with no level at all gives no level", spAimed.Facts.Any(f => f.Label == "Learned by" && f.Value.StartsWith("Hunter, talent")) && AtlasQuery.Build("spell:13161").Facts.Any(f => f.Label == "Learned by" && f.Value.Contains("level 30 (spell level)"))
                    && AtlasQuery.Build("spell:409433").Facts.Any(f => f.Label == "Learned by" && f.Value == "Hunter"), string.Join(" | ", spAimed.Facts));
                var spOrc = AtlasQuery.Build("spell:20572");
                Check("spell page: a racial ability says 'Racial ability of Orc' and links to the race page", spOrc.Facts.Any(f => f.Label == "Racial ability of" && f.Value == "Orc" && f.Link == "race:2") && !spOrc.Facts.Any(f => f.Label == "Learned by"), string.Join(" | ", spOrc.Facts));
                Check("spell page: a spell no class teaches has no 'Learned by'; the Warrior's Heroic Strike costs 15 Rage; the Mage's Arcane Intellect costs a percentage and Evocation has an 8 min cooldown",
                    !AtlasQuery.Build("spell:13813").Facts.Any(f => f.Label == "Learned by" && f.Value.Contains("Mage")) && AtlasQuery.Build("spell:78").Facts.Any(f => f.Label == "Cost" && f.Value == "15 Rage") && AtlasQuery.Build("spell:1459").Facts.Any(f => f.Label == "Cost" && f.Value == "4.5% of base mana")
                    && AtlasQuery.Build("spell:12051").Facts.Any(f => f.Label == "Cooldown" && f.Value == "8 min"));
                var gt = GameTooltip.FromAtlas(sp);
                var tipTexts = gt.Texts();
                Check("tooltip: the spell's game tooltip has the cost line with the range's place and the cast line with the cooldown on the right - '35 Mana' and '6 sec cooldown' are in it", tipTexts.Contains("35 Mana") && tipTexts.Contains("6 sec cooldown") && tipTexts.Contains("Instant"), string.Join(" | ", tipTexts));
                var gt2 = new GameTooltip("Arcane Shot", -1, "Rank 1").AddSpell(SpellData.Spell(3044));
                Check("tooltip: the spell variant (hover on a spell row) shows the cost and the cooldown too; a spell without them shows neither", gt2.Texts().Contains("25 Mana") && gt2.Texts().Contains("6 sec cooldown") && !new GameTooltip("x").AddSpell(SpellData.Spell(100)).Texts().Any(t => t.Contains("cooldown")));
                Check("spell data: the SpellView carries the cost text and the cooldown (the game's own wording) once the class layer is read, and nothing when it is not",
                    SpellData.Spell(3044).CostText == "25 Mana" && SpellData.Spell(3044).CooldownMs == 6000 && CcDo(() => { ClassData.SetForTest(null, ClassData.State.Disabled); var s = SpellData.Spell(3044); ClassData.SetForTest(set); return s.CostText == null && s.CooldownMs == 0; }));

                // ============================================================================================================ the pages in the window
                Width = 1300; Height = 900;
                ShowTab("atlas"); await Task.Delay(100);
                AtlasView.History.Clear();
                await AtlasView.Open("browse:classes"); await AtlasView.WaitForTest(); await Task.Delay(100); UpdateLayout();
                Check("window: browse:classes shows the three classes as rows; clicking one opens its page", AtlasView.IsBrowse && AtlasView.BrowseListForTest != null && AtlasView.BrowseListForTest.Items.Count == 3, AtlasView.IsBrowse + "");
                await AtlasView.Open("class:3"); await AtlasView.WaitForTest(); await Task.Delay(250); UpdateLayout();
                Check("window: the Hunter page is drawn - title Hunter, the breadcrumb Atlas > Classes > Hunter, the 'Spells by level' card with its tabs and filters, the spells in groups",
                    AtlasView.Model != null && AtlasView.Model.Id == "class:3" && AtlasView.CrumbsForTest == "Atlas > Classes > Hunter" && AtlasTexts(AtlasView).Contains("Spells by level") && AtlasView.ClassControlForTest("tab:163") != null && AtlasView.ClassControlForTest("search") != null
                    && AtlasView.ClassHeadersForTest.Any() && AtlasView.ClassHeadersForTest.First() == "Level 1", AtlasView.CrumbsForTest + " " + string.Join(",", AtlasView.ClassHeadersForTest.Take(4)));
                Check("window: the tabs - All and one chip per tab (Beast Mastery, Marksmanship, Beast Training (pet)), All selected; the count line reads the spells and levels",
                    AtlasView.ClassControlForTest("tab:0") != null && AtlasView.ClassControlForTest("tab:50") != null && AtlasView.ClassControlForTest("tab:261") != null && AtlasView.ClassControlForTest("tab:51") == null && AtlasTexts(AtlasView).Contains("Beast Training (pet)")
                    && AtlasView.ClassCountTextForTest.Contains("spells at 11 levels"), AtlasView.ClassCountTextForTest);
                Snapshot(Path.Combine(dir, "atlas-classes-hunter.png"));
                Check("window: the Hunter hero - the name in the class colour (#AAD372), the class icon in the 72 px tile, the chips Damage and Mana, Focus; the tabs are Overview (the spells by level) and Related (the races)",
                    AtlasView.HeroNameForTest == "Hunter" && ((System.Windows.Media.SolidColorBrush)AtlasView.HeroBrushForTest).Color == (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#AAD372") && AtlasView.HeroTileForTest != null && AtlasView.HeroTileForTest.Width == 72 && AtlasWalk(AtlasView.HeroTileForTest).OfType<Image>().Any()
                    && AtlasView.HeroChipsForTest.Contains("Damage") && AtlasView.HeroChipsForTest.Contains("Mana, Focus") && AtlasView.TabsForTest.SequenceEqual(new[] { "overview", "related" }) && AtlasView.TabNowForTest == "overview", string.Join(" | ", AtlasView.HeroChipsForTest) + " tabs " + string.Join(",", AtlasView.TabsForTest));

                void ClickTab(string key) { var b = (Button)AtlasView.ClassControlForTest(key); b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); }
                ClickTab("tab:163"); await Task.Delay(100); UpdateLayout();
                Check("window: the Marksmanship chip shows only that tab; the filter is remembered in the card", AtlasView.ClassFilterForTest.SkillId == 163 && AtlasView.ClassRowsForTest.Count > 5 && AtlasView.ClassRowsForTest.All(r => !r.Title.StartsWith("Aspect")) && AtlasView.ClassControlForTest("clear").Visibility == Visibility.Visible);
                var search = (TextBox)AtlasView.ClassControlForTest("search");
                search.Text = "arcane shot"; await Task.Delay(400); UpdateLayout();
                Check("window: typing 'arcane shot' (debounced) narrows the list to the three ranks, in level order 6, 12, 20", AtlasView.ClassRowsForTest.Count == 3 && AtlasView.ClassRowsForTest.All(r => r.Title == "Arcane Shot") && AtlasView.ClassHeadersForTest.SequenceEqual(new[] { "Level 6", "Level 12", "Level 20" }), string.Join(",", AtlasView.ClassHeadersForTest));
                var sw = (CheckBox)AtlasView.ClassControlForTest("new"); sw.IsChecked = true; sw.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Task.Delay(100);
                Check("window: 'Only new spells' hides the higher ranks (Arcane Shot once, at level 6)", AtlasView.ClassRowsForTest.Count == 1 && AtlasView.ClassHeadersForTest.SequenceEqual(new[] { "Level 6" }));
                ((Button)AtlasView.ClassControlForTest("clear")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Task.Delay(100); UpdateLayout();
                Check("window: Clear filters brings back the whole list and the All tab", AtlasView.ClassFilterForTest.SkillId == 0 && string.IsNullOrEmpty(AtlasView.ClassFilterForTest.Text) && !AtlasView.ClassFilterForTest.NewOnly && search.Text == "" && AtlasView.ClassRowsForTest.Count > 12 && AtlasView.ClassControlForTest("clear").Visibility == Visibility.Collapsed);
                var min = (TextBox)AtlasView.ClassControlForTest("min"); var max = (TextBox)AtlasView.ClassControlForTest("max");
                min.Text = "16"; max.Text = "20"; await Task.Delay(400); UpdateLayout();
                Check("window: Min / Max level 16 and 20 keep the levels 16, 18 and 20", AtlasView.ClassHeadersForTest.SequenceEqual(new[] { "Level 16", "Level 18", "Level 20" }), string.Join(",", AtlasView.ClassHeadersForTest));
                min.Text = "90"; await Task.Delay(400);
                Check("window: a level range nothing is in says so calmly ('No spell matches these filters')", AtlasView.ClassRowsForTest.Count == 0 && AtlasTexts(AtlasView).Contains("No spell matches these filters."));
                min.Text = ""; max.Text = ""; await Task.Delay(400);
                ClickTab("tab:261"); await Task.Delay(100);
                Check("window: the Beast Training (pet) chip lists the pet's abilities, including the one with no level in its own group", AtlasView.ClassRowsForTest.Count == 4 && AtlasView.ClassHeadersForTest.Contains("No level in the game data"));
                ClickTab("tab:0"); await Task.Delay(100);
                var rowTip = AtlasView.RowTipFor(AtlasView.ClassRowsForTest.First(r => r.Link == "spell:3044"));
                Check("window: hovering a spell row gives the game tooltip with the cost and cooldown", rowTip.Texts().Contains("25 Mana") && rowTip.Texts().Contains("6 sec cooldown"), string.Join(" | ", rowTip.Texts()));
                var classLinks = AtlasWalk(AtlasView).OfType<TextBlock>().Where(t => t.Tag is string s && s.StartsWith("race:")).ToList();
                var racesCard = AtlasView.Model.Section("races");
                Check("window: the Races table of the class page is on its Related tab with a row per race (the Overview keeps the spells by level)", racesCard != null && AtlasView.TabsForTest.Contains("related") && AtlasView.ClassRowsForTest.Count > 0 && await SwitchTab("related") && AtlasTexts(AtlasView).Contains("Races") && AtlasTexts(AtlasView).Contains("Night Elf") && await SwitchTab("overview"));
                await AtlasView.Open("spell:14281"); await AtlasView.WaitForTest(); await Task.Delay(150); UpdateLayout();
                var learned = AtlasWalk(AtlasView).OfType<TextBlock>().FirstOrDefault(t => t.Tag is string s && s == "class:3");
                Check("window: the spell page shows 'Learned by' as a link; clicking it opens the Hunter page (and Back returns to the spell)", learned != null && learned.Text.StartsWith("Hunter, level 12"), learned?.Text);
                if (learned != null) ClickRow(learned);
                await AtlasView.WaitForTest(); await Task.Delay(250); UpdateLayout();
                Check("window: ... the Hunter page is open again, its spells drawn from the top", AtlasView.Model != null && AtlasView.Model.Id == "class:3" && AtlasView.ClassHeadersForTest.Any(), AtlasView.CurrentId);
                await AtlasView.Back(); await AtlasView.WaitForTest(); await Task.Delay(100);
                Check("window: Back returns to the spell page", AtlasView.Model != null && AtlasView.Model.Id == "spell:14281");
                var pinned = AtlasView.NavState.TogglePin("class:3");
                Check("window: a class page can be pinned like any other page (the pin button's state follows the id)", pinned && AtlasView.NavState.IsPinned("class:3") && AtlasNav.Describe("class:3").Title == "Hunter");
                AtlasView.NavState.Unpin("class:3");
                await AtlasView.Open("race:2"); await AtlasView.WaitForTest(); await Task.Delay(200); UpdateLayout();
                Snapshot(Path.Combine(dir, "atlas-classes-race.png"));
                Check("window: the Orc page is drawn - breadcrumb Atlas > Races > Orc, the hero (Orc, Horde), the Racial abilities and Classes tables on Related, the facts on the identity card", AtlasView.CrumbsForTest == "Atlas > Races > Orc" && AtlasView.HeroNameForTest == "Orc" && await SwitchTab("related") && AtlasTexts(AtlasView).Contains("Racial abilities") && AtlasTexts(AtlasView).Contains("Blood Fury") && AtlasTexts(AtlasView).Contains("Classes") && AtlasTexts(AtlasView).Contains("Warrior") && await SwitchTab("overview"), AtlasView.CrumbsForTest);
                await AtlasView.Open("class:8"); await AtlasView.WaitForTest(); await Task.Delay(250); UpdateLayout();
                Check("window: another class resets the card (Mage: its own tab Arcane, level 1 first, no Hunter spells)", AtlasView.ClassFilterForTest.SkillId == 0 && AtlasView.ClassControlForTest("tab:237") != null && AtlasView.ClassControlForTest("tab:163") == null && AtlasView.ClassRowsForTest.Any(r => r.Title == "Fireball") && !AtlasView.ClassRowsForTest.Any(r => r.Title == "Arcane Shot"));

                // ============================================================================================================ no data
                ClassData.SetForTest(null, ClassData.State.Unavailable, "game updated: ChrRaces layout 12345678 is not known to this Outpost yet");
                AtlasBrowse.ForgetForTest();
                var noData = AtlasQuery.Build("class:3"); var noRace = AtlasQuery.Build("race:2");
                Check("no class data: the class and race pages say so calmly with the reason (nothing thrown, no half page)", noData.NotFound && noData.Message.Contains("could not be read") && noData.Message.Contains("game updated") && noRace.NotFound, noData.Message);
                Check("no class data: search finds no class, browse says the list is not available, the other spell pages still work (no 'Learned by', no cost)",
                    !AtlasQuery.Search("hunter").Groups.Any(g => g.Kind == "class") && AtlasBrowse.Run("browse:classes").NotFound && AtlasQuery.Build("spell:3044").Facts.All(f => f.Label != "Learned by" && f.Label != "Cost") && !AtlasQuery.Build("spell:3044").NotFound);
                ClassData.SetForTest(null, ClassData.State.Disabled);
                Check("no class data: with the game data switched off the pages say so", AtlasQuery.Build("class:3").Message.Contains("switched off"), AtlasQuery.Build("class:3").Message);
                SpellData.SetForTest(null, SpellData.State.Disabled); ClassData.SetForTest(set);
                var noSpells = AtlasQuery.Build("class:3");
                Check("no spell data: the class page still has its facts and races, and a note that the spells are not listed (no spellbook)", !noSpells.NotFound && noSpells.Custom == null && noSpells.Notes.Any(n => n.Contains("spells are not listed")) && noSpells.Section("races") != null, string.Join(" | ", noSpells.Notes));
                SpellData.SetForTest(CcSpellSet(spellList));

                // ============================================================================================================ the real game files (read-only), when there are any
                await ClassesSmokeReal(Check, notes, dir);
            }
            catch (Exception e) { Check("atlasclasses test ran to the end", false, e.ToString()); }
            finally
            {
                try
                {
                    AtlasQuery.Context.Lodge = savedLodge; ClassData.AutoStart = savedAuto; ClassData.CacheFile = savedCache; ClassicTrainers.Source = savedTrainers;
                    ClassData.ResetForTest(); TalentData.ResetForTest(); SpellData.ResetForTest(); ItemData.ResetForTest(); SeenData.ResetForTest(); ClassicAtlas.Use(null); AtlasBrowse.ForgetForTest();
                    Width = savedWidth; Height = savedHeight;
                    ShowTab("addons"); ResetAtlasForTest();
                }
                catch { }
            }
            return string.Join("\r\n", notes) + "\r\natlasclasses: " + (ok ? "ok" : "FAILED");
        }

        static bool CcDo(Func<bool> f) { try { return f(); } catch (Exception) { return false; } }
        static string AtlasClasses_RowDetail(ClassSpellEntry e) => ClassSpellBook.RowOf(e).Detail;
    }
}
