using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Db2;

namespace ElansAddonHub
{
    // --selftest "atlasclasses", part 5 (2.83): the class and race pages against WoW Forever itself.
    //   fixture  SkillLineAbility.RaceMask: a racial line two races share is split by it (Forever's Skyborne: Read Leyline for the High Order only,
    //            Skysight for the Windshapers only); "Race-only spells" on a class page (a spell the client gives one race only); the mask survives the
    //            spell cache file; one name once in "At a glance".
    //   real     every race and class pair the pages show is the client's own (CharBaseInfo), Forever's six new pairs are there, and what the
    //            class and race notes say that the client can check (who can be what, armor by level, racial and race-only spell names) agrees.
    public partial class MainWindow
    {
        void ClassesForeverFixture(CheckFn Check, string dir, Action restore)
        {
            try
            {
                var cs = new ClassSet(); var d = cs.Data;
                d.Classes[5] = new ClassInfo { Id = 5, Name = "Priest", File = "PRIEST", Roles = ClassInfo.RoleHealer | ClassInfo.RoleDamage };
                d.Classes[4] = new ClassInfo { Id = 4, Name = "Rogue", File = "ROGUE", Roles = ClassInfo.RoleDamage };
                d.Classes[8] = new ClassInfo { Id = 8, Name = "Mage", File = "MAGE", Roles = ClassInfo.RoleDamage };
                d.Classes[9] = new ClassInfo { Id = 9, Name = "Warlock", File = "WARLOCK", Roles = ClassInfo.RoleDamage };
                d.Races[1] = new RaceInfo { Id = 1, Name = "Human", ClientFile = "Human", Bit = 0, Side = RaceInfo.Alliance, Playable = true, StartingLevel = 1 };
                d.Races[7] = new RaceInfo { Id = 7, Name = "Gnome", ClientFile = "Gnome", Bit = 6, Side = RaceInfo.Alliance, Playable = true, StartingLevel = 1 };
                d.Races[95] = new RaceInfo { Id = 95, Name = "High Order Skyborne", LoreName = "Shen'dorei", ClientFile = "Skyborne", Bit = 32, Side = RaceInfo.Alliance, Playable = true, StartingLevel = 1 };
                d.Races[96] = new RaceInfo { Id = 96, Name = "Windshaper Skyborne", LoreName = "Shen'dorei", ClientFile = "Skyborne", Bit = 33, Side = RaceInfo.Horde, Playable = true, StartingLevel = 1 };
                foreach (var p in new[] { (1, 5), (1, 8), (7, 5), (7, 8), (95, 8) }) d.Pairs.Add(ClassSnapshot.PairKey(p.Item1, p.Item2));
                d.HasSkillRows = d.HasLevels = true;
                ulong sky = (1UL << 32) | (1UL << 33);
                d.SkillRows.Add(new SkillClassRow { Skill = 2980, ClassMask = -1, Races = sky, Availability = 1 });              // the Skyborne racial line, shared
                d.SkillRows.Add(new SkillClassRow { Skill = 754, ClassMask = -1, Races = 1, Availability = 1 });                // Racial - Human
                d.SkillRows.Add(new SkillClassRow { Skill = 753, ClassMask = -1, Races = 1UL << 6, Availability = 1 });         // Racial - Gnome
                d.SkillRows.Add(new SkillClassRow { Skill = 56, ClassMask = 16, Races = ulong.MaxValue, Availability = 1 });    // Holy: a priest tab
                d.SkillRows.Add(new SkillClassRow { Skill = 237, ClassMask = 128, Races = ulong.MaxValue, Availability = 1 });  // Arcane: a mage tab
                foreach (var kv in new Dictionary<int, int> { [1277370] = 10, [6346] = 20, [13908] = 10, [3561] = 20, [424035] = 0, [1277455] = 10 }) d.Levels[kv.Key] = new SpellLevelInfo { Base = kv.Value, Spell = kv.Value };
                var infos = new List<SpellInfo>
                {
                    new SpellInfo { Id = 1259705, Name = "Read Leyline", Rank = "Racial" }, new SpellInfo { Id = 1259686, Name = "Skysight", Rank = "Racial" }, new SpellInfo { Id = 1259416, Name = "Walk on Air", Rank = "Racial" },
                    new SpellInfo { Id = 20591, Name = "Expansive Mind", Rank = "Racial Passive" }, new SpellInfo { Id = 1259803, Name = "Expansive Mind", Rank = "Racial Passive" }, new SpellInfo { Id = 20600, Name = "Perception", Rank = "Racial" },
                    new SpellInfo { Id = 1277370, Name = "Divine Grace", Rank = "Rank 1" }, new SpellInfo { Id = 6346, Name = "Fear Ward" }, new SpellInfo { Id = 13908, Name = "Desperate Prayer", Rank = "Rank 1" },
                    new SpellInfo { Id = 3561, Name = "Teleport: Stormwind" }, new SpellInfo { Id = 424035, Name = "Secrets of the Light" }, new SpellInfo { Id = 1277455, Name = "Confounding Flash" },
                };
                var skills = new List<SkillInfo>
                {
                    new SkillInfo { Id = 2980, Name = "Skyborne Racial", Category = 9 }, new SkillInfo { Id = 754, Name = "Racial - Human", Category = 9 }, new SkillInfo { Id = 753, Name = "Racial - Gnome", Category = 9 },
                    new SkillInfo { Id = 56, Name = "Holy", Category = 7 }, new SkillInfo { Id = 237, Name = "Arcane", Category = 7 },
                };
                var abil = new List<SkillAbility>
                {
                    new SkillAbility { Id = 1, Skill = 2980, Spell = 1259705, RaceMask = 1UL << 32 }, new SkillAbility { Id = 2, Skill = 2980, Spell = 1259686, RaceMask = 1UL << 33 }, new SkillAbility { Id = 3, Skill = 2980, Spell = 1259416, RaceMask = sky },
                    new SkillAbility { Id = 4, Skill = 753, Spell = 20591, ClassMask = 400, RaceMask = 1UL << 6 }, new SkillAbility { Id = 5, Skill = 753, Spell = 1259803, ClassMask = 8, RaceMask = 1UL << 6 },
                    new SkillAbility { Id = 6, Skill = 754, Spell = 20600, RaceMask = 1 },
                    new SkillAbility { Id = 7, Skill = 56, Spell = 1277370, ClassMask = 16, RaceMask = 1 },                 // Divine Grace: Humans only
                    new SkillAbility { Id = 8, Skill = 56, Spell = 6346, ClassMask = 16 },                                  // Fear Ward: every priest (no mask)
                    new SkillAbility { Id = 9, Skill = 56, Spell = 13908, ClassMask = 16, RaceMask = 1UL << 2 },            // Desperate Prayer: Dwarves - no dwarf priest in this world, so nobody's
                    new SkillAbility { Id = 10, Skill = 56, Spell = 424035, ClassMask = 16, RaceMask = 1 },                 // a rune: no learn level, left out
                    new SkillAbility { Id = 11, Skill = 56, Spell = 1277455, ClassMask = 16, RaceMask = 1UL << 6 },         // Confounding Flash: Gnomes only
                    new SkillAbility { Id = 12, Skill = 237, Spell = 3561, ClassMask = 128, RaceMask = 1 | (1UL << 6) },    // a portal of a side: not race-only
                };
                var aset = new AtlasSet { Spells = new SpellSnapshot(infos, skills, abil, new Dictionary<int, int[]>()), Details = new ItemDetails() };
                ClassData.SetForTest(cs); SpellData.SetForTest(aset);

                var ho = AtlasQuery.Build("race:95"); var ws = AtlasQuery.Build("race:96");
                List<string> Racials(AtlasPageModel m) => m.Section("racial")?.Rows.Select(r => r.Title).ToList() ?? new List<string>();
                Check("forever: a racial line two races share is split by SkillLineAbility.RaceMask - the High Order has Read Leyline (not Skysight), the Windshapers Skysight (not Read Leyline), both Walk on Air",
                    Racials(ho).SequenceEqual(new[] { "Read Leyline", "Walk on Air" }) && Racials(ws).SequenceEqual(new[] { "Skysight", "Walk on Air" })
                    && ho.ClassLore.Glance.Any(g => g.Label == "Racial abilities" && g.Text == "Read Leyline, Walk on Air"), string.Join(",", Racials(ho)) + " / " + string.Join(",", Racials(ws)));
                Check("forever: a racial ability's spell page names only the races that get it (Skysight: Windshaper Skyborne)",
                    SpellOrigins.Of(1259686).Where(o => o.Racial).Select(o => o.RaceId).SequenceEqual(new[] { 96 }) && SpellOrigins.Of(1259416).Count(o => o.Racial) == 2);
                var gnome = AtlasQuery.Build("race:7");
                Check("forever: per-class variants of a racial (Gnome Expansive Mind) are one name in At a glance, and the table says which class a variant is for",
                    gnome.ClassLore.Glance.Any(g => g.Label == "Racial abilities" && g.Text == "Expansive Mind") && gnome.Section("racial").Rows.Count == 2
                    && gnome.Section("racial").Rows.Any(r => r.Detail == "Racial Passive · Rogue") && gnome.Section("racial").Rows.Any(r => r.Detail == "Racial Passive · Priest, Mage, Warlock"),
                    string.Join(" | ", gnome.Section("racial")?.Rows.Select(r => r.Title + ":" + r.Detail) ?? new string[0]));
                var priest = AtlasQuery.Build("class:5"); var mage = AtlasQuery.Build("class:8");
                var only = priest.ClassLore?.Glance.FirstOrDefault(g => g.Label == "Race-only spells");
                Check("forever: the Priest's At a glance has 'Race-only spells' from the client - Confounding Flash (Gnome), Divine Grace (Human) in the races' order, each linking to its spell; not Fear Ward (every priest), not a dwarf spell in a world without dwarf priests, not a rune without a learn level",
                    only != null && only.Text == "Confounding Flash (Gnome), Divine Grace (Human)" && only.Items[1].Value == "spell:1277370" && only.Source == AtlasLabel.Forever,
                    only == null ? "none" : only.Text);
                Check("forever: a spell a whole side shares (a mage teleport for Humans and Gnomes) is not race-only, so the Mage has no such line",
                    mage.ClassLore != null && !mage.ClassLore.Glance.Any(g => g.Label == "Race-only spells"), string.Join(" | ", mage.ClassLore?.Glance.Select(g => g.Label) ?? new string[0]));
                var file = Path.Combine(dir, "forever-spells.cache");
                bool wrote = SpellIndexFile.TryWrite(file, "1.60.1.test", aset); var back = SpellIndexFile.TryRead(file);
                Check("forever: the race mask survives the spell cache file (the cache format was bumped, so an old file is read again from the game)",
                    wrote && back != null && back.Set.Spells.AbilitiesOf(2980).Select(a => a.RaceMask).SequenceEqual(new[] { 1UL << 32, 1UL << 33, sky }) && SpellData.FormatVersion >= 4);
                Check("forever: an ability with no race mask is for every race; one with a mask only for those bits", new SkillAbility().ForRace(1UL << 40) && new SkillAbility { RaceMask = 2 }.ForRace(2) && !new SkillAbility { RaceMask = 2 }.ForRace(1));
            }
            catch (Exception e) { Check("forever fixture ran to the end", false, e.ToString()); }
            finally { restore(); }
        }

        // the real Forever client: d = its class tables (set as the current layer by the caller, with its spell layer)
        void ClassesForeverReal(CheckFn Check, ClassSnapshot d)
        {
            var spells = SpellData.Spells;
            RaceInfo R(string n) => d.RaceByName(n);
            ClassInfo C(string f) => d.ClassByFile(f);
            List<string> RacesOf(string f) => d.RacesOf(C(f).Id).Select(r => r.Name).OrderBy(x => x, StringComparer.Ordinal).ToList();
            bool Same(IEnumerable<string> a, params string[] b) => a.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(b.OrderBy(x => x, StringComparer.Ordinal));

            // ---- every race / class pair shown on a page is a pair of CharBaseInfo, and all of them are shown
            var bad = new List<string>();
            foreach (var c in d.ClassList())
            {
                var pg = AtlasQuery.Build(AtlasIds.Class(c.Id));
                var shown = pg.Section("races")?.Rows.Select(r => r.Key).ToList() ?? new List<string>();
                var want = d.RacesOf(c.Id).Select(r => AtlasIds.Race(r.Id)).ToList();
                var glance = pg.ClassLore?.Glance.FirstOrDefault(g => g.Label == "Races")?.Items.Select(i => i.Value).ToList() ?? new List<string>();
                if (!shown.OrderBy(x => x).SequenceEqual(want.OrderBy(x => x)) || !glance.OrderBy(x => x).SequenceEqual(want.OrderBy(x => x))) bad.Add(c.Name);
            }
            foreach (var r in d.PlayableRaces())
            {
                var pg = AtlasQuery.Build(AtlasIds.Race(r.Id));
                var shown = pg.Section("classes")?.Rows.Select(x => x.Key).ToList() ?? new List<string>();
                var want = d.Classes.Values.Where(c => d.CanBe(r.Id, c.Id)).Select(c => AtlasIds.Class(c.Id)).ToList();
                var glance = pg.ClassLore?.Glance.FirstOrDefault(g => g.Label == "Classes")?.Items.Select(i => i.Value).ToList() ?? new List<string>();
                if (!shown.OrderBy(x => x).SequenceEqual(want.OrderBy(x => x)) || !glance.OrderBy(x => x).SequenceEqual(want.OrderBy(x => x))) bad.Add(r.Name);
            }
            Check("forever real: every race and class pair the class and race pages show (tables and At a glance) is exactly the client's CharBaseInfo list", bad.Count == 0, string.Join(",", bad));
            Check("forever real: the client has Forever's six new pairs - Human Hunter, Orc Mage, Dwarf Shaman, Gnome Priest, Undead Paladin, Troll Warlock",
                d.CanBe(R("Human").Id, 3) && d.CanBe(R("Orc").Id, 8) && d.CanBe(R("Dwarf").Id, 7) && d.CanBe(R("Gnome").Id, 5) && d.CanBe(R("Undead").Id, 2) && d.CanBe(R("Troll").Id, 9));
            Check("forever real: the Skyborne classes are Blizzard's - High Order: Warrior, Hunter, Rogue, Mage, Druid; Windshaper: Warrior, Hunter, Rogue, Shaman, Druid",
                Same(d.ClassesOf(R("High Order Skyborne").Id).Select(c => c.Name), "Warrior", "Hunter", "Rogue", "Mage", "Druid") && Same(d.ClassesOf(R("Windshaper Skyborne").Id).Select(c => c.Name), "Warrior", "Hunter", "Rogue", "Shaman", "Druid"));

            // ---- what the notes say that the client can check
            string Note(ClassLoreEntry e) => e == null ? "" : string.Join(" ", new[] { e.Summary, e.History, e.Forever }.Concat(e.Mentions).Concat(e.Facts.Select(f => f.Value)));
            var claims = new List<KeyValuePair<string, bool>>
            {
                new KeyValuePair<string, bool>("every race can be a warrior", RacesOf("WARRIOR").Count == d.PlayableRaces().Count),
                new KeyValuePair<string, bool>("paladins are Human, Dwarf or Undead", Same(RacesOf("PALADIN"), "Human", "Dwarf", "Undead")),
                new KeyValuePair<string, bool>("Gnomes and the Forsaken cannot be hunters; both Skyborne can", !RacesOf("HUNTER").Contains("Gnome") && !RacesOf("HUNTER").Contains("Undead") && RacesOf("HUNTER").Count(n => n.Contains("Skyborne")) == 2),
                new KeyValuePair<string, bool>("every Classic race but the tauren can be a rogue", RacesOf("ROGUE").Count == d.PlayableRaces().Count - 1 && !RacesOf("ROGUE").Contains("Tauren")),
                new KeyValuePair<string, bool>("the Skyborne cannot be priests, paladins or warlocks", new[] { "PRIEST", "PALADIN", "WARLOCK" }.All(f => !RacesOf(f).Any(n => n.Contains("Skyborne")))),
                new KeyValuePair<string, bool>("dwarves, night elves and tauren cannot be mages", new[] { "Dwarf", "Night Elf", "Tauren" }.All(n => !RacesOf("MAGE").Contains(n))),
                new KeyValuePair<string, bool>("druids are Night Elf, Tauren and both Skyborne", Same(RacesOf("DRUID"), "Night Elf", "Tauren", "High Order Skyborne", "Windshaper Skyborne")),
                new KeyValuePair<string, bool>("the dwarf is the only Alliance shaman", RacesOf("SHAMAN").Select(R).Where(r => r.Side == RaceInfo.Alliance).Select(r => r.Name).SequenceEqual(new[] { "Dwarf" })),
                new KeyValuePair<string, bool>("the Forsaken are the only Horde paladins", RacesOf("PALADIN").Select(R).Where(r => r.Side == RaceInfo.Horde).Select(r => r.Name).SequenceEqual(new[] { "Undead" })),
                new KeyValuePair<string, bool>("shaman have no tank role", (C("SHAMAN").Roles & ClassInfo.RoleTank) == 0 && (C("SHAMAN").Roles & ClassInfo.RoleHealer) != 0),
            };
            if (spells != null && d.HasSkillRows)
            {
                Func<int, int> catOf = id => spells.Skills.TryGetValue(id, out var sk) ? sk.Category : 0;
                Dictionary<string, int> Armor(string f) => d.Proficiencies(C(f).Id, ClassSnapshot.CatArmor, catOf).Where(kv => spells.Skills.ContainsKey(kv.Key)).ToDictionary(kv => spells.Skills[kv.Key].Name, kv => kv.Value);
                claims.Add(new KeyValuePair<string, bool>("warriors and paladins wear plate from 40", new[] { "WARRIOR", "PALADIN" }.All(f => Armor(f).TryGetValue("Plate Mail", out var l) && l == 40)));
                claims.Add(new KeyValuePair<string, bool>("hunters and shaman wear leather first and mail from 40", new[] { "HUNTER", "SHAMAN" }.All(f => Armor(f).TryGetValue("Mail", out var l) && l == 40 && Armor(f).ContainsKey("Leather") && !Armor(f).ContainsKey("Plate Mail"))));
                claims.Add(new KeyValuePair<string, bool>("mages wear only cloth", Same(Armor("MAGE").Keys, "Cloth")));
                claims.Add(new KeyValuePair<string, bool>("druids wear only cloth and leather", Same(Armor("DRUID").Keys, "Cloth", "Leather")));
                int Lv(string name) { var sp = spells.Spells.Values.Where(s => s.Name == name && d.Level(s.Id).HasValue && d.Level(s.Id).Value.Base > 0).Select(s => d.Level(s.Id).Value.Base).DefaultIfEmpty(0).Min(); return sp; }
                claims.Add(new KeyValuePair<string, bool>("Defensive Stance at 10, Berserker Stance at 30", Lv("Defensive Stance") == 10 && Lv("Berserker Stance") == 30));
                claims.Add(new KeyValuePair<string, bool>("druid forms: Bear 10, Aquatic 16, Cat 20, Travel 30", Lv("Bear Form") == 10 && Lv("Aquatic Form") == 16 && Lv("Cat Form") == 20 && Lv("Travel Form") == 30));
                // racial names the race notes give, on that race's page; the Skyborne split
                var racial = d.PlayableRaces().ToDictionary(r => r.Name, r => AtlasQuery.Build(AtlasIds.Race(r.Id)).Section("racial")?.Rows.Select(x => x.Title).ToList() ?? new List<string>());
                var named = new Dictionary<string, string[]>
                {
                    ["Human"] = new[] { "Perception", "The Human Spirit", "Ruthless", "Will to Survive" }, ["Dwarf"] = new[] { "Stoneform", "Find Treasure", "Earth Fury", "Big Game Hunter" },
                    ["Night Elf"] = new[] { "Elune's Light", "Shadowmeld", "Quickness", "Wisp Spirit" }, ["Gnome"] = new[] { "Expansive Mind", "Eureka!" },
                    ["Orc"] = new[] { "Blood Fury", "Hardiness", "Savage", "Shatter Curse" }, ["Undead"] = new[] { "Touch of the Grave" }, ["Tauren"] = new[] { "Plainsrunning" },
                    ["Troll"] = new[] { "Rapid Regeneration", "Regeneration", "Berserking", "Beast Slaying" },
                };
                var missingRacial = named.SelectMany(kv => kv.Value.Where(n => !racial[kv.Key].Contains(n) || Note(ClassLore.ForRace(kv.Key)).IndexOf(n, StringComparison.Ordinal) < 0).Select(n => kv.Key + ":" + n)).ToList();
                claims.Add(new KeyValuePair<string, bool>("the racials the race notes name are on that race's page [" + string.Join(",", missingRacial) + "]", missingRacial.Count == 0));
                claims.Add(new KeyValuePair<string, bool>("Read Leyline is the High Order's only, Skysight the Windshapers' only, the other three shared",
                    racial["High Order Skyborne"].Contains("Read Leyline") && !racial["High Order Skyborne"].Contains("Skysight") && racial["Windshaper Skyborne"].Contains("Skysight") && !racial["Windshaper Skyborne"].Contains("Read Leyline")
                    && new[] { "Walk on Air", "Wind Blessed", "Elemental Insight" }.All(n => racial["High Order Skyborne"].Contains(n) && racial["Windshaper Skyborne"].Contains(n))));
                // the priest's race-only spells the notes name
                var only = AtlasQuery.RaceOnlySpells(C("PRIEST")).Select(x => x.Name + "|" + x.Race.Name).ToList();
                var wantOnly = new[] { "Divine Grace|Human", "Feedback|Human", "Chastise|Dwarf", "Desperate Prayer|Dwarf", "Starshards|Night Elf", "Elune's Grace|Night Elf", "Confounding Flash|Gnome", "Contingency Plan|Gnome", "Dark Sacrifice|Undead", "Touch of Weakness|Undead", "Hex of Weakness|Troll", "Shadowguard|Troll" };
                claims.Add(new KeyValuePair<string, bool>("the priest's race-only spells are the client's two per race, and Fear Ward and Devouring Plague are no longer race-only [" + string.Join(",", only) + "]",
                    Same(only, wantOnly) && !only.Any(x => x.StartsWith("Fear Ward|") || x.StartsWith("Devouring Plague|"))));
            }
            var wrong = claims.Where(c => !c.Value).Select(c => c.Key).ToList();
            Check("forever real: what the class and race notes say that the client can check agrees (" + claims.Count + " claims: who can be what, armor by level, stance and form levels, racial and race-only spell names)", wrong.Count == 0, string.Join("; ", wrong));

            // the notes never repeat a Classic-only rule the client contradicts
            var classicOnly = new[] { "Fear Ward for dwarves", "the Horde's druids in this era", "only the" };
            var stale = ClassLore.Classes.Concat(ClassLore.Races).SelectMany(e => classicOnly.Where(p => Note(e).IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0).Select(p => e.Name + ": " + p)).ToList();
            Check("forever real: no note keeps a Classic-only claim (Fear Ward for dwarves, the tauren as the Horde's only druids, 'only the X can')", stale.Count == 0, string.Join("; ", stale));
        }
    }
}
