using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using ElansAddonHub.Services.Db2;

namespace ElansAddonHub.Services.Atlas
{
    // ============================================================================================================================
    // The class page's spellbook: the spells of one class by the level they are learned at, grouped, with rank chains and filters. Pure data (no controls).
    // Where each fact comes from (all of it the player's own game files, nothing is invented):
    //   * which skill lines are the class' tabs      SkillRaceClassInfo (class mask = exactly this class) + SkillLine category (class skill / the class' own secondary skill)
    //   * which spells a tab holds                   SkillLineAbility (spell, class mask)
    //   * the level a spell is learned at            SpellLevels.BaseLevel; where that is 0, the spell's own level (SpellLevels.SpellLevel) when it is above 1, labelled as such;
    //                                                a spell the client gives no level at all sits in its own group "No level in the game data"
    //   * the rank                                   the spell's own rank text ("Rank 3"); a chain = the same name with ranks (SkillLineAbility's SupercedesSpell is 0 in this client)
    //   * cost and cooldown                          SpellPower / SpellCooldowns
    //   * talent spells                              the talent trees of the class (TalentData): they are learned with a talent point, not at a level
    // ============================================================================================================================

    // what a trainer teaches for what (the Classic baseline's npc_trainer when a build of it carries it; the Outpost's current baseline index has no trainer table)
    public sealed class TrainerOffer
    {
        public int SpellId, NpcId, ReqLevel;
        public long Cost;                    // copper
        public string NpcName = "";
    }

    public static class ClassicTrainers
    {
        // set by the Classic layer when its index holds trainer data (and by the self-test); null = there is none
        public static Func<int, IReadOnlyList<TrainerOffer>> Source;
        public static bool Available => Source != null;
        public static IReadOnlyList<TrainerOffer> For(int spellId)
        {
            try { return Source?.Invoke(spellId) ?? new TrainerOffer[0]; } catch (Exception) { return new TrainerOffer[0]; }
        }
    }

    public sealed class ClassSpellEntry
    {
        public int SpellId, SkillId, Level, RankNo, IconId, CastMs;
        public int LevelKind;                          // 0 = the level it is learned at (BaseLevel), 1 = the spell's own level (BaseLevel is 0), 2 = the client gives no level
        public string Name = "", Rank = "", SkillName = "", Description = "", Cost = "", Cooldown = "";
        public bool Talent, Pet, HasText;
        public int CooldownMs;
        public long TrainCost;                         // Classic baseline: what the cheapest trainer asks (copper), 0 = unknown
        public string Trainer = "";                    // Classic baseline: "Hunter trainer ..." (names, at most 2)
        public AtlasRow Row;
        public override string ToString() => (Level > 0 ? "L" + Level + " " : "") + Name + (Rank.Length > 0 ? " (" + Rank + ")" : "");
    }

    public sealed class ClassTab
    {
        public int SkillId, IconId, Count;
        public string Name = "";
        public bool Pet;
        public override string ToString() => Name + " (" + Count + ")";
    }

    public sealed class ClassSpellFilter
    {
        public int SkillId;                 // 0 = all tabs
        public bool NewOnly;                // hide higher ranks: only the first rank of every spell
        public int MinLevel, MaxLevel;      // 0 = no limit
        public string Text = "";
        public bool Talents;                // include the spells a talent teaches
        public bool Pet = true;             // include the pet abilities (only when no tab is picked)
        public ClassSpellFilter Clone() => (ClassSpellFilter)MemberwiseClone();
    }

    public sealed class LevelGroup
    {
        public int Level;                   // 0 = the group of spells with no level
        public string Title = "";
        public List<ClassSpellEntry> Entries = new List<ClassSpellEntry>();
    }

    public sealed class ClassSpellBook
    {
        public int ClassId;
        public string ClassName = "", ClassFile = "";
        public List<ClassTab> Tabs = new List<ClassTab>();
        public List<ClassSpellEntry> Entries = new List<ClassSpellEntry>();
        public int HiddenHelpers;           // internal helper spells (no description, no cost) that are left out
        public bool HasTalentData, HasTrainerData;
        public List<string> Notes = new List<string>();
        public int Total => Entries.Count;

        static readonly Regex RankNumber = new Regex(@"(\d+)", RegexOptions.CultureInvariant);

        public static int RankOf(string rank)
        {
            if (string.IsNullOrEmpty(rank)) return 0;
            var m = RankNumber.Match(rank);
            return m.Success && int.TryParse(m.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
        }

        public static string LevelTitle(int level, int kind) => level > 0 ? "Level " + level.ToString(CultureInfo.InvariantCulture) : "No level in the game data";

        // The spellbook of a class, or null when the layers it needs are not read (class data, spell data). Never throws.
        public static ClassSpellBook Build(int classId, bool describe = true)
        {
            try { return BuildInner(classId, describe); }
            catch (Exception e) { Util.Log("class spellbook " + classId + ": " + e); return null; }
        }

        static ClassSpellBook BuildInner(int classId, bool describe)
        {
            var snap = ClassData.Data;
            var spells = SpellData.Spells;
            var cls = snap?.Class(classId);
            if (cls == null || spells == null) return null;
            var book = new ClassSpellBook { ClassId = classId, ClassName = cls.Name, ClassFile = cls.File };
            if (!snap.HasSkillRows) { book.Notes.Add("The skill lines of this class are not in the game data this Outpost can read."); return book; }
            if (!snap.HasLevels) book.Notes.Add("The levels spells are learned at (SpellLevels) are not in the game data this Outpost can read, so the spells are not grouped by level.");

            // talent spells of the class
            var talentSpells = new HashSet<int>();
            var td = TalentData.Data;
            if (td != null)
            {
                book.HasTalentData = true;
                foreach (var t in td.Trees.Where(x => x.ClassId == classId))
                    foreach (var n in t.Nodes) foreach (var e in n.Entries) if (e.SpellId > 0) talentSpells.Add(e.SpellId);
            }
            else book.Notes.Add("The talent trees are not read yet, so spells that a talent teaches are not told apart.");

            Func<int, int> catOf = id => spells.Skills.TryGetValue(id, out var s) ? s.Category : 0;
            var tabs = snap.TabSkills(classId, catOf);
            var seen = new HashSet<int>();
            var sk = new Dictionary<int, SkillInfo>();
            foreach (var skillId in tabs)
            {
                if (!spells.Skills.TryGetValue(skillId, out var skill) || string.IsNullOrEmpty(skill.Name)) continue;
                sk[skillId] = skill;
                bool pet = skill.Name.IndexOf("Beast Training", StringComparison.OrdinalIgnoreCase) >= 0;
                int count = 0;
                foreach (var a in spells.AbilitiesOf(skillId))
                {
                    if (a.ClassMask != 0 && (a.ClassMask & cls.Mask) == 0) continue;
                    var sp = spells.Get(a.Spell);
                    if (sp == null || string.IsNullOrEmpty(sp.Name)) continue;
                    if (!seen.Add(a.Spell)) continue;
                    var lvl = snap.Level(a.Spell);
                    if (lvl == null && snap.HasLevels) continue;                       // no SpellLevels row: a talent rank, a passive effect, an internal spell
                    var e = new ClassSpellEntry
                    {
                        SpellId = a.Spell, SkillId = skillId, SkillName = skill.Name, Name = sp.Name, Rank = sp.Rank ?? "", RankNo = RankOf(sp.Rank), IconId = sp.IconId, CastMs = sp.CastMs, Pet = pet,
                        Talent = talentSpells.Contains(a.Spell),
                    };
                    if (lvl != null)
                    {
                        if (lvl.Value.Base > 0) { e.Level = lvl.Value.Base; e.LevelKind = 0; }
                        else if (lvl.Value.Spell > 1) { e.Level = lvl.Value.Spell; e.LevelKind = 1; }
                        else e.LevelKind = 2;
                    }
                    else e.LevelKind = 2;
                    e.Cost = snap.CostText(a.Spell) ?? "";
                    e.CooldownMs = snap.CooldownMs(a.Spell);
                    e.Cooldown = ClassSnapshot.CooldownText(e.CooldownMs) ?? "";
                    e.HasText = !string.IsNullOrWhiteSpace(sp.Desc) || !string.IsNullOrWhiteSpace(sp.Aura);
                    if (describe)
                    {
                        var view = SpellData.Spell(a.Spell);
                        var d = view == null ? "" : (!string.IsNullOrWhiteSpace(view.Description) ? view.Description : view.AuraDescription ?? "");
                        e.Description = Shorten(d, 160);
                    }
                    book.Entries.Add(e);
                    count++;
                }
                book.Tabs.Add(new ClassTab { SkillId = skillId, Name = skill.Name, IconId = skill.IconId, Count = count, Pet = pet });
            }
            DropHelpers(book);
            foreach (var t in book.Tabs) t.Count = book.Entries.Count(e => e.SkillId == t.SkillId);
            book.Tabs = book.Tabs.Where(t => t.Count > 0).OrderBy(t => t.Pet ? 1 : 0).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
            // the Classic baseline's trainers: what it costs to learn
            book.HasTrainerData = ClassicTrainers.Available;
            if (book.HasTrainerData)
                foreach (var e in book.Entries)
                {
                    var offers = ClassicTrainers.For(e.SpellId);
                    if (offers.Count == 0) continue;
                    e.TrainCost = offers.Min(o => o.Cost);
                    e.Trainer = string.Join(", ", offers.Where(o => !string.IsNullOrEmpty(o.NpcName)).Select(o => o.NpcName).Distinct().Take(2));
                }
            return book;
        }

        static string Shorten(string s, int max)
        {
            s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            if (s.Length <= max) return s;
            int cut = s.LastIndexOf(' ', max);
            return s.Substring(0, cut > max / 2 ? cut : max).TrimEnd(' ', ',', ';', '.') + "…";
        }

        // Helper and variant spells share the name and the rank of the spell that matters:
        //   * the one with a cost or a cooldown wins over a half of the same spell that has neither (a trap and its "Effect" spell, the periodic part of a spell)
        //   * a spell with a learn level wins over a variant of the same name and rank that has none (the client keeps a second, level-less copy of some spells)
        //   * a spell with no description, no cost and no cooldown is an internal one
        static void DropHelpers(ClassSpellBook book)
        {
            var drop = new HashSet<ClassSpellEntry>();
            foreach (var g in book.Entries.GroupBy(e => e.Name + "|" + e.Rank))
            {
                var list = g.ToList();
                if (list.Count < 2) continue;
                bool anyReal = list.Any(e => e.Cost.Length > 0 || e.CooldownMs > 0);
                if (anyReal) foreach (var e in list) if (e.Cost.Length == 0 && e.CooldownMs == 0) drop.Add(e);
                var left = list.Where(e => !drop.Contains(e)).ToList();
                if (left.Count > 1 && left.Any(e => e.LevelKind == 0)) foreach (var e in left) if (e.LevelKind != 0) drop.Add(e);
            }
            var names = new HashSet<string>(book.Entries.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var e in book.Entries)
            {
                if (!e.HasText && e.Cost.Length == 0 && e.CooldownMs == 0 && !e.Talent) drop.Add(e);
                if (e.Name.EndsWith(" Effect", StringComparison.Ordinal) && names.Contains(e.Name.Substring(0, e.Name.Length - 7))) drop.Add(e);
            }
            book.HiddenHelpers = drop.Count;
            book.Entries.RemoveAll(drop.Contains);
        }

        // ---- the groups for a filter: by level, ascending; the spells with no level last
        public List<LevelGroup> Groups(ClassSpellFilter f)
        {
            f = f ?? new ClassSpellFilter();
            IEnumerable<ClassSpellEntry> src = Entries;
            if (f.SkillId > 0) src = src.Where(e => e.SkillId == f.SkillId);
            else if (!f.Pet) src = src.Where(e => !e.Pet);
            if (!f.Talents) src = src.Where(e => !e.Talent);
            var list = src.ToList();
            if (f.NewOnly)
            {
                // the first rank of every spell: ranked spells are one chain per name (per tab); the lowest level, then the lowest rank, stays
                var keep = new HashSet<ClassSpellEntry>(list.Where(e => e.RankNo == 0));
                foreach (var g in list.Where(e => e.RankNo > 0).GroupBy(e => e.SkillId + "|" + e.Name))
                    keep.Add(g.OrderBy(e => e.Level == 0 ? int.MaxValue : e.Level).ThenBy(e => e.RankNo).ThenBy(e => e.SpellId).First());
                list = list.Where(keep.Contains).ToList();
            }
            if (f.MinLevel > 0 || f.MaxLevel > 0)
                list = list.Where(e => e.Level > 0 && (f.MinLevel <= 0 || e.Level >= f.MinLevel) && (f.MaxLevel <= 0 || e.Level <= f.MaxLevel)).ToList();
            var words = (f.Text ?? "").Trim().ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > 0)
                list = list.Where(e => { var hay = (e.Name + " " + e.Rank + " " + e.SkillName + " " + e.Description).ToLowerInvariant(); return words.All(hay.Contains); }).ToList();
            var groups = new List<LevelGroup>();
            foreach (var g in list.GroupBy(e => e.Level).OrderBy(g => g.Key == 0 ? int.MaxValue : g.Key))
            {
                var lg = new LevelGroup { Level = g.Key, Title = LevelTitle(g.Key, 0) };
                lg.Entries.AddRange(g.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.RankNo).ThenBy(e => e.SpellId));
                groups.Add(lg);
            }
            return groups;
        }

        // one row of the table for an entry (label: the game's data; the trainer part: the Classic baseline, labelled)
        public static AtlasRow RowOf(ClassSpellEntry e)
        {
            var bits = new List<string>();
            if (e.Rank.Length > 0) bits.Add(e.Rank);
            if (e.Cost.Length > 0) bits.Add(e.Cost);
            if (e.Cooldown.Length > 0) bits.Add(e.Cooldown);
            if (e.Talent) bits.Add("talent");
            if (e.LevelKind == 1) bits.Add("spell level");
            var r = new AtlasRow
            {
                Key = "spell:" + e.SpellId, Link = AtlasIds.Spell(e.SpellId), Title = e.Name, IconId = e.IconId, Label = AtlasLabel.Forever, Tier = 1, Type = "Spell",
                Detail = string.Join(" · ", bits), Extra = e.Description, Sort = -e.Level,
            };
            if (e.TrainCost > 0)
            {
                r.Right = AtlasFormat.Money(e.TrainCost);
                r.Extra = (r.Extra.Length > 0 ? r.Extra + " | " : "") + AtlasLabel.Classic + ": trainer " + AtlasFormat.Money(e.TrainCost) + (e.Trainer.Length > 0 ? " (" + e.Trainer + ")" : "");
            }
            return r;
        }
    }

    // A character's race and class as Atlas links (Characters page): by the race's name ("Night Elf", the lore name works too) and the class file ("HUNTER") or name. Null = not known to the game data.
    public static class CharacterLinks
    {
        public static string Race(string raceName)
        {
            var r = string.IsNullOrWhiteSpace(raceName) ? null : ClassData.Data?.RaceByName(raceName.Trim());
            return r != null && r.Playable ? AtlasIds.Race(r.Id) : null;
        }

        public static string Class(string classFile, string className)
        {
            var snap = ClassData.Data;
            var c = snap == null ? null : (!string.IsNullOrWhiteSpace(classFile) ? snap.ClassByFile(classFile.Trim()) : null) ?? (!string.IsNullOrWhiteSpace(className) ? snap.ClassByName(className.Trim()) : null);
            return c == null ? null : AtlasIds.Class(c.Id);
        }
    }

    // ============================================================================================================================
    // "Learned by": which class teaches a spell (and at which level / rank), and which race has it as a racial ability. Built once per pair of layers.
    // ============================================================================================================================
    public sealed class SpellOrigin
    {
        public int ClassId, RaceId, Level, LevelKind, RankNo;
        public string Rank = "";
        public string SkillName = "";
        public bool Talent;
        public bool Racial => RaceId > 0;
    }

    public static class SpellOrigins
    {
        static readonly object gate = new object();
        static object builtFor1, builtFor2, builtFor3;
        static Dictionary<int, List<SpellOrigin>> index;

        public static List<SpellOrigin> Of(int spellId)
        {
            var snap = ClassData.Data;
            var spells = SpellData.Spells;
            if (snap == null || spells == null || !snap.HasSkillRows) return new List<SpellOrigin>();
            Dictionary<int, List<SpellOrigin>> idx;
            lock (gate)
            {
                if (index == null || !ReferenceEquals(builtFor1, snap) || !ReferenceEquals(builtFor2, spells) || !ReferenceEquals(builtFor3, TalentData.Data))
                {
                    index = BuildIndex(snap, spells);
                    builtFor1 = snap; builtFor2 = spells; builtFor3 = TalentData.Data;
                }
                idx = index;
            }
            return idx.TryGetValue(spellId, out var l) ? l : new List<SpellOrigin>();
        }

        static Dictionary<int, List<SpellOrigin>> BuildIndex(ClassSnapshot snap, SpellSnapshot spells)
        {
            var res = new Dictionary<int, List<SpellOrigin>>();
            void Add(int spell, SpellOrigin o) { if (!res.TryGetValue(spell, out var l)) res[spell] = l = new List<SpellOrigin>(); l.Add(o); }
            foreach (var c in snap.ClassList())
            {
                var book = ClassSpellBook.Build(c.Id, false);
                if (book == null) continue;
                foreach (var e in book.Entries)
                    Add(e.SpellId, new SpellOrigin { ClassId = c.Id, Level = e.Level, LevelKind = e.LevelKind, Rank = e.Rank, RankNo = e.RankNo, SkillName = e.SkillName, Talent = e.Talent });
            }
            foreach (var r in snap.PlayableRaces())
                foreach (var skill in RacialSkills(snap, spells, r))
                    foreach (var a in RacialAbilities(spells, skill, r))
                    {
                        var sp = spells.Get(a.Spell);
                        if (sp == null || string.IsNullOrEmpty(sp.Name)) continue;
                        Add(a.Spell, new SpellOrigin { RaceId = r.Id, Rank = sp.Rank ?? "", SkillName = spells.Skills.TryGetValue(skill, out var k) ? k.Name : "" });
                    }
            return res;
        }

        // the racial skill lines of a race: secondary skills whose name says "Racial" and whose race mask names this race
        public static List<int> RacialSkills(ClassSnapshot snap, SpellSnapshot spells, RaceInfo race)
        {
            Func<int, int> catOf = id => spells.Skills.TryGetValue(id, out var s) ? s.Category : 0;
            return snap.RaceRows(race, catOf, ClassSnapshot.CatSecondary)
                .Select(r => r.Skill).Distinct()
                .Where(id => spells.Skills.TryGetValue(id, out var s) && s.Name.IndexOf("Racial", StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
        }

        // the abilities of a racial skill line this race gets: Forever shares one line between races and splits it with SkillLineAbility.RaceMask
        // (the Skyborne line: Read Leyline for the High Order only, Skysight for the Windshapers only)
        public static IEnumerable<SkillAbility> RacialAbilities(SpellSnapshot spells, int skill, RaceInfo race) => spells.AbilitiesOf(skill).Where(a => a.ForRace(race.BitMask));

        public static string Describe(SpellOrigin o)
        {
            var snap = ClassData.Data;
            if (o.Racial) return (snap?.Race(o.RaceId)?.Name ?? "Race " + o.RaceId) + " racial ability";
            var name = snap?.Class(o.ClassId)?.Name ?? "Class " + o.ClassId;
            if (o.Talent) return name + ", talent" + (o.RankNo > 0 ? " (rank " + o.RankNo + ")" : "");
            string lvl = o.Level > 0 ? ", level " + o.Level + (o.LevelKind == 1 ? " (spell level)" : "") : "";
            return name + lvl + (o.RankNo > 0 ? " (rank " + o.RankNo + ")" : "");
        }
    }
}
