using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ElansAddonHub.Services.Db2
{
    // ============================================================================================================================
    // Atlas layers A2 (spells, skills, recipes) and A4 (factions): the data model and the parsing of the client tables.
    // Everything is read from the player's OWN game files (see SpellData for the service); nothing is shipped with the Outpost.
    // ============================================================================================================================

    // the ids every layer links with (ARCHITECTURE.md, "Foundation the layers share")
    public static class AtlasIds
    {
        public static string Item(int id) => "item:" + id.ToString(CultureInfo.InvariantCulture);
        public static string Spell(int id) => "spell:" + id.ToString(CultureInfo.InvariantCulture);
        public static string Skill(int id) => "skill:" + id.ToString(CultureInfo.InvariantCulture);
        public static string Faction(int id) => "faction:" + id.ToString(CultureInfo.InvariantCulture);
        public static string Class(int id) => "class:" + id.ToString(CultureInfo.InvariantCulture);
        public static string Race(int id) => "race:" + id.ToString(CultureInfo.InvariantCulture);

        // "spell:6150" -> kind "spell", id 6150; false for anything else
        public static bool TryParse(string s, out string kind, out int id)
        {
            kind = null; id = 0;
            if (string.IsNullOrEmpty(s)) return false;
            if (s.IndexOf('?') > 0) s = Services.Atlas.AtlasTabs.BaseId(s);      // "item:1?tab=sources" is the page item:1 (see AtlasTabs)
            int c = s.IndexOf(':');
            if (c <= 0 || c == s.Length - 1) return false;
            kind = s.Substring(0, c);
            if (kind != "item" && kind != "spell" && kind != "skill" && kind != "faction" && kind != "npc" && kind != "obj" && kind != "quest" && kind != "zone" && kind != "instance" && kind != "class" && kind != "race") { kind = null; return false; }
            return int.TryParse(s.Substring(c + 1), NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;
        }
    }

    // one effect of a spell: what the description tokens ($s1, $o1, $t1 ...) are filled from
    public sealed class EffectInfo
    {
        public int Index;              // 0-based (the $s1 of a description is Index 0)
        public int Effect, Aura;       // SPELL_EFFECT_* and the aura type
        public int PeriodMs;           // ticks of a periodic aura
        public int Chain, ItemType, Misc, Trigger;
        public float Points;           // EffectBasePointsF
        public float Coef;             // Coefficient (a scaling spell; the shown amount then depends on the character)
        public float Radius;           // yards
        public const int CreateItem = 24;
    }

    public sealed class SpellInfo
    {
        public static readonly EffectInfo[] NoEffects = new EffectInfo[0];
        public int Id;
        public string Name = "", Rank = "", Desc = "", Aura = "";   // the descriptions still hold their $tokens
        public int IconId;                                           // SpellMisc.SpellIconFileDataID
        public int CastMs, DurationMs;                               // cast time, duration (-1 = until cancelled)
        public float RangeMax;                                       // yards
        public EffectInfo[] Effects = NoEffects;

        public EffectInfo Effect(int index1) { foreach (var e in Effects) if (e.Index == index1 - 1) return e; return null; }
    }

    // what callers get: the spell with its descriptions filled in
    public sealed class SpellView
    {
        public int Id;
        public string Name, Rank, Description, AuraDescription;
        public int IconId, CastMs, DurationMs;
        public float RangeMax;
        public string CostText;                // "50 Mana" / "4% of base mana" (the class layer's SpellPower), null = none or not read
        public int CooldownMs;                 // the spell's own cooldown (the class layer's SpellCooldowns), 0 = none or not read
        public HotfixParts Hotfix;             // what the server's hotfix rows changed (None = the game files as shipped)
        public override string ToString() => Id + " " + Name + (string.IsNullOrEmpty(Rank) ? "" : " (" + Rank + ")");
    }

    public sealed class SkillInfo
    {
        public int Id, Category, IconId, ParentId, Flags, CanLink;
        public string Name = "", Description = "";
        // SkillLine.CategoryID as the client uses it
        public const int CatAttributes = 5, CatWeapon = 6, CatClass = 7, CatArmor = 8, CatSecondary = 9, CatLanguage = 10, CatProfession = 11;
        public bool IsProfession => Category == CatProfession || Category == CatSecondary;
    }

    public sealed class SkillAbility
    {
        public int Id, Skill, Spell, MinRank, Green, Gray, Acquire, Flags, ClassMask, Supersedes;   // Green / Gray = the ranks where the colour turns (SkillLineAbility's trivial low / high)
    }

    // a crafting recipe: the spell that makes something, the skill and rank it needs, the item it makes and what it uses up
    public sealed class Recipe
    {
        public int SpellId, SkillId, MinRank, Green, Gray, ItemId;
        public int[] Reagents = new int[0];            // [item0, count0, item1, count1 ...]
        public int ReagentCount => Reagents.Length / 2;
    }

    // the small tables a spell's numbers come from and which table row gave which spell its numbers: kept with the snapshot so the server's hotfix
    // rows (ServerHotfixes) can be laid over it later without the game files - a SpellMisc / SpellEffect hotfix row names its own row id and spell
    public sealed class SpellLookups
    {
        public readonly Dictionary<int, int> CastMs = new Dictionary<int, int>(), DurationMs = new Dictionary<int, int>();
        public readonly Dictionary<int, float> RangeMax = new Dictionary<int, float>(), Radius = new Dictionary<int, float>();
        public readonly Dictionary<int, int> MiscRows = new Dictionary<int, int>();            // SpellMisc row id -> spell (the default-difficulty rows that were used)
        public readonly Dictionary<int, long> EffectRows = new Dictionary<int, long>();        // SpellEffect row id -> spell << 8 | effect index (the same)
        public static long EffectKey(int spell, int index) => ((long)spell << 8) | (uint)(index & 0xFF);
    }

    public sealed class SpellSnapshot
    {
        public readonly SpellLookups Lookups;
        public readonly Dictionary<int, SpellInfo> Spells;
        public readonly Dictionary<int, SkillInfo> Skills;
        public readonly List<SkillAbility> Abilities;
        public readonly Dictionary<int, int[]> Reagents;            // spell -> [item, count ...]
        readonly Dictionary<int, List<SkillAbility>> bySkill = new Dictionary<int, List<SkillAbility>>();
        readonly Dictionary<int, Recipe> recipeBySpell = new Dictionary<int, Recipe>();
        readonly Dictionary<int, List<Recipe>> producing = new Dictionary<int, List<Recipe>>(), usingReagent = new Dictionary<int, List<Recipe>>();
        public static readonly List<Recipe> NoRecipes = new List<Recipe>();

        public int RecipeCount => recipeBySpell.Count;
        public int SpellCount => Spells.Count;
        public IEnumerable<Recipe> Recipes => recipeBySpell.Values;

        public SpellSnapshot(IEnumerable<SpellInfo> spells, IEnumerable<SkillInfo> skills, IEnumerable<SkillAbility> abilities, Dictionary<int, int[]> reagents, SpellLookups lookups = null)
        {
            Lookups = lookups ?? new SpellLookups();
            Spells = new Dictionary<int, SpellInfo>();
            foreach (var s in spells ?? Enumerable.Empty<SpellInfo>()) Spells[s.Id] = s;
            Skills = new Dictionary<int, SkillInfo>();
            foreach (var s in skills ?? Enumerable.Empty<SkillInfo>()) Skills[s.Id] = s;
            Abilities = (abilities ?? Enumerable.Empty<SkillAbility>()).ToList();
            Reagents = reagents ?? new Dictionary<int, int[]>();
            foreach (var a in Abilities)
            {
                if (!bySkill.TryGetValue(a.Skill, out var l)) bySkill[a.Skill] = l = new List<SkillAbility>();
                l.Add(a);
            }
            foreach (var l in bySkill.Values) l.Sort((x, y) => x.MinRank != y.MinRank ? x.MinRank.CompareTo(y.MinRank) : x.Id.CompareTo(y.Id));
            // recipes: abilities of a profession (or secondary skill) whose spell makes an item or uses reagents
            foreach (var a in Abilities)
            {
                if (!Skills.TryGetValue(a.Skill, out var sk) || !sk.IsProfession) continue;
                if (!Spells.TryGetValue(a.Spell, out var sp) || string.IsNullOrEmpty(sp.Name)) continue;
                int item = 0;
                foreach (var e in sp.Effects) if (e.Effect == EffectInfo.CreateItem && e.ItemType > 0) { item = e.ItemType; break; }
                Reagents.TryGetValue(a.Spell, out var rg);
                if (item == 0 && (rg == null || rg.Length == 0)) continue;
                if (recipeBySpell.ContainsKey(a.Spell)) continue;
                var r = new Recipe { SpellId = a.Spell, SkillId = a.Skill, MinRank = a.MinRank, Green = a.Green, Gray = a.Gray, ItemId = item, Reagents = rg ?? new int[0] };
                recipeBySpell[a.Spell] = r;
                if (item > 0) Add(producing, item, r);
                for (int i = 0; i + 1 < r.Reagents.Length; i += 2) if (r.Reagents[i] > 0 && r.Reagents[i + 1] > 0) Add(usingReagent, r.Reagents[i], r);
            }
        }

        static void Add(Dictionary<int, List<Recipe>> d, int key, Recipe r) { if (!d.TryGetValue(key, out var l)) d[key] = l = new List<Recipe>(); if (!l.Contains(r)) l.Add(r); }

        public SpellInfo Get(int id) => Spells.TryGetValue(id, out var s) ? s : null;
        public List<SkillAbility> AbilitiesOf(int skill) => bySkill.TryGetValue(skill, out var l) ? l : new List<SkillAbility>();
        public Recipe RecipeOf(int spell) => recipeBySpell.TryGetValue(spell, out var r) ? r : null;
        public List<Recipe> Producing(int item) => producing.TryGetValue(item, out var l) ? l : NoRecipes;
        public List<Recipe> UsingReagent(int item) => usingReagent.TryGetValue(item, out var l) ? l : NoRecipes;
    }

    // ---- factions (A4)
    public sealed class FactionInfo
    {
        public int Id, ParentId, ReputationIndex, Flags;
        public string Name = "", Description = "";
    }

    public sealed class FactionTemplateInfo
    {
        public int Id, FactionId, Flags, Group, FriendGroup, EnemyGroup;
        public int[] Enemies = new int[0], Friends = new int[0];     // faction ids named in the template
        public const int GroupPlayer = 1, GroupAlliance = 2, GroupHorde = 4, GroupMonster = 8;
        public bool IsAllianceGroup => (Group & GroupAlliance) != 0;
        public bool IsHordeGroup => (Group & GroupHorde) != 0;
    }

    public enum Standing { Neutral, Friendly, Hostile }

    public sealed class FactionSnapshot
    {
        public readonly Dictionary<int, FactionInfo> Factions = new Dictionary<int, FactionInfo>();
        public readonly Dictionary<int, FactionTemplateInfo> Templates = new Dictionary<int, FactionTemplateInfo>();
        public readonly int AllianceTemplate, HordeTemplate;      // the player templates the standing is worked out against (0 = no template table)

        public FactionSnapshot(IEnumerable<FactionInfo> factions, IEnumerable<FactionTemplateInfo> templates)
        {
            foreach (var f in factions ?? Enumerable.Empty<FactionInfo>()) Factions[f.Id] = f;
            foreach (var t in templates ?? Enumerable.Empty<FactionTemplateInfo>()) Templates[t.Id] = t;
            // the player races: the lowest template that is a player of the Alliance / the Horde and not of both
            AllianceTemplate = Templates.Values.Where(t => (t.Group & (FactionTemplateInfo.GroupPlayer | FactionTemplateInfo.GroupAlliance)) == (FactionTemplateInfo.GroupPlayer | FactionTemplateInfo.GroupAlliance) && !t.IsHordeGroup).Select(t => t.Id).DefaultIfEmpty(0).Min();
            HordeTemplate = Templates.Values.Where(t => (t.Group & (FactionTemplateInfo.GroupPlayer | FactionTemplateInfo.GroupHorde)) == (FactionTemplateInfo.GroupPlayer | FactionTemplateInfo.GroupHorde) && !t.IsAllianceGroup).Select(t => t.Id).DefaultIfEmpty(0).Min();
        }

        public FactionInfo Faction(int id) => Factions.TryGetValue(id, out var f) ? f : null;
        public FactionTemplateInfo Template(int id) => Templates.TryGetValue(id, out var t) ? t : null;

        // how the owner of template `npc` treats a player of template `player`: its enemy group / enemy factions first, then its friends (the client's own rule)
        public static Standing Attitude(FactionTemplateInfo npc, FactionTemplateInfo player)
        {
            if (npc == null || player == null) return Standing.Neutral;
            if ((npc.EnemyGroup & player.Group) != 0 || (player.FactionId > 0 && npc.Enemies.Contains(player.FactionId))) return Standing.Hostile;
            if ((npc.FriendGroup & player.Group) != 0 || (player.FactionId > 0 && npc.Friends.Contains(player.FactionId))) return Standing.Friendly;
            return Standing.Neutral;
        }

        public Standing ToAlliance(int templateId) => AllianceTemplate == 0 ? Standing.Neutral : Attitude(Template(templateId), Template(AllianceTemplate));
        public Standing ToHorde(int templateId) => HordeTemplate == 0 ? Standing.Neutral : Attitude(Template(templateId), Template(HordeTemplate));
    }

    // ============================================================================================================================
    // the parsing of the tables; every function is pure (bytes in, model out) so the self-test can feed it synthetic tables
    // ============================================================================================================================
    public sealed class SpellRaw
    {
        // table name (SpellTables.Def.Name) -> file bytes; a missing entry = not in the install
        public readonly Dictionary<string, byte[]> Tables = new Dictionary<string, byte[]>();
        public byte[] Get(SpellTables.Def d) => Tables.TryGetValue(d.Name, out var b) ? b : null;
    }

    // what one read of the game files gave. A group that is null is unavailable (the reason is in Problems); the others are still good.
    public sealed class AtlasSet
    {
        public SpellSnapshot Spells;
        public FactionSnapshot Factions;
        public ItemDetails Details;
        public WorldLookups World;                                  // locks, quest sorts / kinds / lines, the client's quest list (WorldLookups.cs)
        public HotfixInfo Hotfixes;                                 // what the server's hotfix rows (DBCache.bin) changed; null = not laid over (the shipped tables as they are)
        public readonly Dictionary<string, string> Problems = new Dictionary<string, string>();      // group or table -> why it is unavailable
        public readonly List<string> Notes = new List<string>();
        public readonly Dictionary<string, uint> LayoutsSeen = new Dictionary<string, uint>();      // table -> layout hash that was read (for the saved index)
    }

    public static class SpellParse
    {
        // ---- small readers
        static string Str(byte[] a, ref int p, int end)
        {
            int e = p;
            while (e < end && a[e] != 0) e++;
            if (e >= end) { p = end; return null; }
            var s = e == p ? "" : System.Text.Encoding.UTF8.GetString(a, p, e - p);
            p = e + 1;
            return s;
        }

        internal static SpellTables.Opened Take(AtlasSet set, SpellRaw raw, SpellTables.Def def)
        {
            var o = SpellTables.Open(raw.Get(def), def);
            if (!o.Ok) { set.Problems[def.Name] = o.Problem; return o; }
            set.LayoutsSeen[def.Name] = o.Layout;
            if (o.Table.EncryptedRecords > 0) set.Notes.Add(def.Name + ": " + o.Table.EncryptedRecords.ToString(CultureInfo.InvariantCulture) + " rows are key-locked in the game files and skipped");
            return o;
        }

        public static AtlasSet All(SpellRaw raw)
        {
            var set = new AtlasSet();
            try { set.Spells = Spells(set, raw); } catch (Exception e) { set.Spells = null; set.Problems["spells"] = "could not be read: " + e.Message; }
            try { set.Factions = Factions(set, raw); } catch (Exception e) { set.Factions = null; set.Problems["factions"] = "could not be read: " + e.Message; }
            try { set.Details = ItemDetails.Parse(set, raw); } catch (Exception e) { set.Details = null; set.Problems["itemdetails"] = "could not be read: " + e.Message; }
            try { set.World = WorldLookups.Parse(set, raw); } catch (Exception e) { set.World = null; set.Problems["world"] = "could not be read: " + e.Message; }
            return set;
        }

        // ---------------------------------------------------------------------------------------------------------------- spells, skills, recipes
        public static SpellSnapshot Spells(AtlasSet set, SpellRaw raw)
        {
            var names = Take(set, raw, SpellTables.SpellName);
            if (!names.Ok) { set.Problems["spells"] = names.Problem; return null; }       // no names = nothing to show
            var spells = new Dictionary<int, SpellInfo>(names.Table.Count);
            var nt = names.Table;
            for (int i = 0; i < nt.Count; i++)
            {
                var n = nt.String(i, 0);
                if (string.IsNullOrEmpty(n)) continue;
                int id = nt.IdAt(i);
                spells[id] = new SpellInfo { Id = id, Name = n.Trim() };
            }

            // the three texts (sparse table: NameSubtext, Description, AuraDescription inline)
            var sp = Take(set, raw, SpellTables.Spell);
            if (sp.Ok && sp.Table.Sparse)
            {
                var t = sp.Table;
                for (int i = 0; i < t.Count; i++)
                {
                    if (!spells.TryGetValue(t.IdAt(i), out var s)) continue;
                    var seg = t.Raw(i);
                    int p = seg.Offset, end = seg.Offset + seg.Count;
                    var sub = Str(seg.Array, ref p, end); var desc = Str(seg.Array, ref p, end); var aura = Str(seg.Array, ref p, end);
                    s.Rank = (sub ?? "").Trim(); s.Desc = (desc ?? "").Trim(); s.Aura = (aura ?? "").Trim();
                }
            }
            else if (sp.Ok) { set.Problems[SpellTables.Spell.Name] = "Spell.db2 is not the sparse table this version expects"; }

            var lk = new SpellLookups();
            // small lookup tables (cast time, duration, range, radius) by id
            Dictionary<int, int> castMs = Simple(set, raw, SpellTables.SpellCastTimes, (t, i) => SpellTables.Int(t, i, 0));
            Dictionary<int, int> durMs = Simple(set, raw, SpellTables.SpellDuration, (t, i) => SpellTables.Int(t, i, 0));
            Dictionary<int, float> rangeMax = null, radius = null;
            var rg = Take(set, raw, SpellTables.SpellRange);
            if (rg.Ok) { rangeMax = new Dictionary<int, float>(); for (int i = 0; i < rg.Table.Count; i++) rangeMax[rg.Table.IdAt(i)] = SpellTables.FltArr(rg.Table, i, 4, 0, 2); }
            var rd = Take(set, raw, SpellTables.SpellRadius);
            if (rd.Ok) { radius = new Dictionary<int, float>(); for (int i = 0; i < rd.Table.Count; i++) radius[rd.Table.IdAt(i)] = SpellTables.Flt(rd.Table, i, 0); }
            void Keep<T>(Dictionary<int, T> from, Dictionary<int, T> into) { if (from != null) foreach (var kv in from) into[kv.Key] = kv.Value; }
            Keep(castMs, lk.CastMs); Keep(durMs, lk.DurationMs); Keep(rangeMax, lk.RangeMax); Keep(radius, lk.Radius);

            // SpellMisc: icon and the ids above (the spell is the relationship column; the default difficulty only)
            var misc = Take(set, raw, SpellTables.SpellMisc);
            if (misc.Ok)
            {
                var t = misc.Table;
                for (int i = 0; i < t.Count; i++)
                {
                    int spellId = t.Foreign(i);
                    if (spellId <= 0 || !spells.TryGetValue(spellId, out var s)) continue;
                    if (SpellTables.Int(t, i, SpellTables.MiscDifficulty) != 0) continue;
                    lk.MiscRows[t.IdAt(i)] = spellId;
                    s.IconId = SpellTables.Int(t, i, SpellTables.MiscIcon);
                    int ci = SpellTables.Int(t, i, SpellTables.MiscCastTime), di = SpellTables.Int(t, i, SpellTables.MiscDuration), ri = SpellTables.Int(t, i, SpellTables.MiscRange);
                    if (castMs != null && castMs.TryGetValue(ci, out var cm)) s.CastMs = cm;
                    if (durMs != null && durMs.TryGetValue(di, out var dm)) s.DurationMs = dm;
                    if (rangeMax != null && rangeMax.TryGetValue(ri, out var rm)) s.RangeMax = rm;
                }
            }

            // SpellEffect: what the description tokens are filled from
            var eff = Take(set, raw, SpellTables.SpellEffect);
            if (eff.Ok)
            {
                var t = eff.Table;
                var lists = new Dictionary<int, List<EffectInfo>>();
                for (int i = 0; i < t.Count; i++)
                {
                    int spellId = t.Foreign(i);
                    if (spellId <= 0 || !spells.ContainsKey(spellId)) continue;
                    if (SpellTables.Int(t, i, SpellTables.EffDifficulty) != 0) continue;
                    var e = new EffectInfo
                    {
                        Index = SpellTables.Int(t, i, SpellTables.EffIndex), Effect = SpellTables.Int(t, i, SpellTables.EffEffect), Aura = SpellTables.Int(t, i, SpellTables.EffAura),
                        PeriodMs = SpellTables.Int(t, i, SpellTables.EffPeriod), Chain = SpellTables.Int(t, i, SpellTables.EffChain), ItemType = SpellTables.Int(t, i, SpellTables.EffItemType),
                        Misc = SpellTables.IntArr(t, i, SpellTables.EffMisc, 0, 2), Trigger = SpellTables.Int(t, i, SpellTables.EffTrigger),
                        Points = SpellTables.Flt(t, i, SpellTables.EffPoints), Coef = SpellTables.Flt(t, i, SpellTables.EffCoefficient),
                    };
                    int rad = SpellTables.IntArr(t, i, SpellTables.EffRadius, 0, 2);
                    if (rad <= 0) rad = SpellTables.IntArr(t, i, SpellTables.EffRadius, 1, 2);   // EffectRadiusIndex[2]: some rows (Forever hotfixes) use only the second
                    if (radius != null && rad > 0 && radius.TryGetValue(rad, out var rr)) e.Radius = rr;
                    if (e.Index < 0 || e.Index > 31) continue;
                    if (!lists.TryGetValue(spellId, out var l)) lists[spellId] = l = new List<EffectInfo>();
                    if (l.All(x => x.Index != e.Index)) { l.Add(e); lk.EffectRows[t.IdAt(i)] = SpellLookups.EffectKey(spellId, e.Index); }
                }
                foreach (var kv in lists) spells[kv.Key].Effects = kv.Value.OrderBy(x => x.Index).ToArray();
            }

            // skills and what they teach
            var skills = new List<SkillInfo>(); var abilities = new List<SkillAbility>();
            var sl = Take(set, raw, SpellTables.SkillLine);
            if (sl.Ok)
            {
                var t = sl.Table;
                for (int i = 0; i < t.Count; i++)
                    skills.Add(new SkillInfo
                    {
                        Id = t.IdAt(i), Name = (t.String(i, 0) ?? "").Trim(), Description = (t.String(i, 2) ?? "").Trim(), Category = SpellTables.Int(t, i, 6), IconId = SpellTables.Int(t, i, 7),
                        CanLink = SpellTables.Int(t, i, 8), ParentId = SpellTables.Int(t, i, 9), Flags = SpellTables.Int(t, i, 11),
                    });
            }
            var sa = Take(set, raw, SpellTables.SkillLineAbility);
            if (sa.Ok)
            {
                var t = sa.Table;
                for (int i = 0; i < t.Count; i++)
                    abilities.Add(new SkillAbility
                    {
                        Id = t.IdAt(i), Skill = SpellTables.Int(t, i, 3), Spell = SpellTables.Int(t, i, 4), MinRank = SpellTables.Int(t, i, 5), ClassMask = SpellTables.Int(t, i, 6),
                        Supersedes = SpellTables.Int(t, i, 7), Acquire = SpellTables.Int(t, i, 8), Gray = SpellTables.Int(t, i, 9), Green = SpellTables.Int(t, i, 10), Flags = SpellTables.Int(t, i, 11),
                    });
            }
            if (!sl.Ok || !sa.Ok) abilities.Clear();      // abilities without skills (or the other way round) mean nothing

            // reagents
            var reagents = new Dictionary<int, int[]>();
            var rt = Take(set, raw, SpellTables.SpellReagents);
            if (rt.Ok)
            {
                var t = rt.Table;
                for (int i = 0; i < t.Count; i++)
                {
                    int spellId = SpellTables.Int(t, i, 0);
                    if (spellId <= 0) continue;
                    var l = new List<int>();
                    for (int e = 0; e < 8; e++)
                    {
                        int item = SpellTables.IntArr(t, i, 1, e, 8), cnt = SpellTables.IntArr(t, i, 2, e, 8);
                        if (item > 0 && cnt > 0) { l.Add(item); l.Add(cnt); }
                    }
                    if (l.Count > 0) reagents[spellId] = l.ToArray();
                }
            }
            return new SpellSnapshot(spells.Values, skills, abilities, reagents, lk);
        }

        // id -> one number, for the small lookup tables
        static Dictionary<int, int> Simple(AtlasSet set, SpellRaw raw, SpellTables.Def def, Func<Wdc5, int, int> read)
        {
            var o = Take(set, raw, def);
            if (!o.Ok) return null;
            var d = new Dictionary<int, int>(o.Table.Count);
            for (int i = 0; i < o.Table.Count; i++) d[o.Table.IdAt(i)] = read(o.Table, i);
            return d;
        }

        // ---------------------------------------------------------------------------------------------------------------- factions
        public static FactionSnapshot Factions(AtlasSet set, SpellRaw raw)
        {
            var ft = Take(set, raw, SpellTables.Faction);
            if (!ft.Ok) { set.Problems["factions"] = ft.Problem; return null; }
            var factions = new List<FactionInfo>(ft.Table.Count);
            var t = ft.Table;
            for (int i = 0; i < t.Count; i++)
                factions.Add(new FactionInfo { Id = t.IdAt(i), Name = (t.String(i, 0) ?? "").Trim(), Description = (t.String(i, 1) ?? "").Trim(), ReputationIndex = SpellTables.Int(t, i, 2), ParentId = SpellTables.Int(t, i, 3), Flags = SpellTables.Int(t, i, 6) });
            var templates = new List<FactionTemplateInfo>();
            var tt = Take(set, raw, SpellTables.FactionTemplate);
            if (tt.Ok)
            {
                var u = tt.Table;
                for (int i = 0; i < u.Count; i++)
                {
                    var f = new FactionTemplateInfo { Id = u.IdAt(i), FactionId = SpellTables.Int(u, i, 0), Flags = SpellTables.Int(u, i, 1), Group = SpellTables.Int(u, i, 2), FriendGroup = SpellTables.Int(u, i, 3), EnemyGroup = SpellTables.Int(u, i, 4) };
                    var en = new List<int>(); var fr = new List<int>();
                    for (int e = 0; e < 8; e++)
                    {
                        int a = SpellTables.IntArr(u, i, 5, e, 8), b = SpellTables.IntArr(u, i, 6, e, 8);
                        if (a > 0) en.Add(a);
                        if (b > 0) fr.Add(b);
                    }
                    f.Enemies = en.ToArray(); f.Friends = fr.ToArray();
                    templates.Add(f);
                }
            }
            return new FactionSnapshot(factions, templates);
        }
    }
}
