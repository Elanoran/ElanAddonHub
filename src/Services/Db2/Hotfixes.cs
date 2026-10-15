using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ElansAddonHub.Services.Db2
{
    // ============================================================================================================================
    // The server's hotfixes beyond items: the rows of Cache\ADB\<locale>\DBCache.bin (see DbCache) for the tables the spell / world layers read, laid over
    // the shipped tables the way the client does it - a hotfix row replaces the shipped row of the same id, a "record removed" entry takes the shipped row
    // away, an invalid / not-public entry only cancels an earlier hotfix row. So WoW Forever's own content that is not in the game files (new spells,
    // new quests, changed locks and faction templates) shows in the Atlas, marked "Server hotfix".
    //
    // A hotfix row is the table's record of this build at its natural widths: every column of the .dbd's LAYOUT in order, an inline id included, a
    // non-inline id left out, a non-inline relationship (the spell of a SpellMisc / SpellEffect row) appended at the end; strings inline. Each decoder
    // below reads exactly the bytes its layout says and drops a row of any other length (nothing is half-read).
    //
    // Source of every layout: wowdev/WoWDBDefs definitions/*.dbd, the LAYOUT block that lists build 1.60.1.70374; the same layout hash is in the shipped
    // table of that build (a table is only used when the game files carry exactly that layout, and only when DBCache.bin is of the game's build).
    // Checked against the real cache of build 70374 (every row of these tables decodes to its exact length) and against the shipped rows of the same id:
    // all 25 QuestV2 rows (434, 92841 ... 96222) are the shipped rows byte for byte; SpellName 8913 "Sacred Cleansing" and Spell 8913 ("Removes the
    // protective enchantments around Morbent Fel.") = shipped; SpellMisc 862534 = shipped except the duration index (347 -> 6); 786136 = shipped except
    // Attributes[12]; SpellEffect 1337938 = shipped except the base points (10.5 -> 12.5), 1349232 (-15 -> -20), 686181 / 686183 (spell 8913) except
    // EffectRadiusIndex ([10, 0] -> [0, 10]); Lock 4516 = shipped minus its 4th entry; FactionTemplate 3561 = shipped except EnemyGroup (9 -> 1); and new rows the game files do
    // not have (spells 1325994-1325997 "[DNT] Teleport to ..." with their SpellMisc / SpellEffect / SpellXSpellVisual rows, Lock 4588).
    //
    // Left out on purpose (reported in Notes when the cache has rows for them): the tables the Atlas has no verified use for (BroadcastText - no client
    // table links a line to a creature; GossipNPCOption, SpellXSpellVisual, SpellAuraOptions, CreatureXDisplayInfo, CreatureDifficultyTreasure ...), and the
    // tables whose cache entries carry no data on this server (Creature, CreatureDifficulty, GameObjects, QuestV2CliTask, QuestObjective: status 3 / 2 only).
    // ============================================================================================================================
    [Flags]
    public enum HotfixParts { None = 0, New = 1, Name = 2, Text = 4, Numbers = 8, Effects = 16, Removed = 32 }

    // what the server's hotfixes changed, for the "Server hotfix" labels (empty when DBCache.bin was not read or had nothing for these tables)
    public sealed class HotfixInfo
    {
        public int CacheBuild, Rows;
        public readonly Dictionary<int, HotfixParts> Spells = new Dictionary<int, HotfixParts>();
        public readonly HashSet<int> NewQuests = new HashSet<int>();               // quest ids the shipped QuestV2 does not have
        public readonly HashSet<int> Locks = new HashSet<int>(), Templates = new HashSet<int>();
        public readonly List<string> Notes = new List<string>();
        public HotfixParts Spell(int id) => Spells.TryGetValue(id, out var p) ? p : HotfixParts.None;
        public static readonly HotfixInfo Empty = new HotfixInfo();
    }

    public static class ServerHotfixes
    {
        // the tables this version decodes: the shipped table's definition (name, table hash) and the one layout verified for its hotfix rows
        public sealed class Def { public SpellTables.Def Table; public uint Layout; public int Length; }
        static Def D(SpellTables.Def t, uint layout, int length = -1) => new Def { Table = t, Layout = layout, Length = length };

        // SpellName 782EE721: Name_lang (string)
        public static readonly Def SpellName = D(SpellTables.SpellName, 0x782EE721);
        // Spell E3D134FB: NameSubtext_lang, Description_lang, AuraDescription_lang (strings)
        public static readonly Def Spell = D(SpellTables.Spell, 0xE3D134FB);
        // SpellMisc 434B3607 (119 bytes): Attributes[17] i32, DifficultyID i16, CastingTimeIndex u16, DurationIndex u16, PvPDurationIndex u16, RangeIndex u16,
        // SchoolMask u8, Speed f, LaunchDelay f, MinDuration f, SpellIconFileDataID i32, ActiveIconFileDataID i32, ContentTuningID i32,
        // ShowFutureSpellPlayerConditionID i32, SpellVisualScript i32, ActiveSpellVisualScript i32, SpellID i32 (the relationship)
        public static readonly Def SpellMisc = D(SpellTables.SpellMisc, 0x434B3607, 119);
        // SpellEffect 5362E3D4 (136 bytes): EffectAura i16, DifficultyID i16, EffectIndex i32, Effect u32, EffectAmplitude f, EffectAttributes i32,
        // EffectAuraPeriod i32, EffectBonusCoefficient f, EffectChainAmplitude f, EffectChainTargets i32, EffectItemType i32, EffectMechanic i32,
        // EffectPointsPerResource f, EffectPos_facing f, EffectRealPointsPerLevel f, EffectTriggerSpell i32, BonusCoefficientFromAP f, PvpMultiplier f,
        // Coefficient f, Variance f, ResourceCoefficient f, GroupSizeBasePointsCoefficient f, EffectBasePointsF f, ScalingClass i32, Node i32,
        // EffectMiscValue[2] i32, EffectRadiusIndex[2] u32, EffectSpellClassMask[4] i32, ImplicitTarget[2] i16, SpellID i32 (the relationship)
        public static readonly Def SpellEffect = D(SpellTables.SpellEffect, 0x5362E3D4, 136);
        // QuestV2 1854BDB9 (12 bytes): ID i32 (inline: must be the entry's id), UniqueBitFlag i32, UiQuestDetailsThemeID i32
        public static readonly Def QuestV2 = D(WorldTables.QuestV2, 0x1854BDB9, 12);
        // Lock DA418152 (68 bytes): Flags i32, Index[8] i32, Skill[8] u16, Type[8] u8, Action[8] u8
        public static readonly Def Lock = D(WorldTables.Lock, 0xDA418152, 68);
        // FactionTemplate 22B6DC22 (41 bytes): Faction u16, Flags i32, FactionGroup u8, FriendGroup u8, EnemyGroup u8, Enemies[8] u16, Friend[8] u16
        public static readonly Def FactionTemplate = D(SpellTables.FactionTemplate, 0x22B6DC22, 41);

        public static readonly Def[] All = { SpellName, Spell, SpellMisc, SpellEffect, QuestV2, Lock, FactionTemplate };

        // tables of the cache this version reads nothing from, by hash, for the note (name, why)
        static readonly Dictionary<uint, string> LeftOut = new Dictionary<uint, string>
        {
            [0x021826BB] = "BroadcastText (no verified link from a line to a creature)",
            [0x936188F9] = "GossipNPCOption (no verified link to a creature)",
            [0xC9D6B6B3] = "Creature", [0xC9E0A749] = "CreatureDifficulty", [0x13C403A5] = "GameObjects",
            [0x0F992211] = "QuestV2CliTask", [0x01325CA0] = "QuestObjective",
        };

        // ---- a hotfix row, read field by field at its natural widths; any read past the end marks it bad
        public sealed class Row
        {
            readonly byte[] a; int p;
            public bool Bad { get; private set; }
            public Row(byte[] row) { a = row ?? new byte[0]; }
            public bool AtEnd => !Bad && p == a.Length;
            bool Need(int n) { if (Bad || p + n > a.Length) { Bad = true; return false; } return true; }
            public int I32() { if (!Need(4)) return 0; int v = BitConverter.ToInt32(a, p); p += 4; return v; }
            public int I16() { if (!Need(2)) return 0; int v = BitConverter.ToInt16(a, p); p += 2; return v; }
            public int U16() { if (!Need(2)) return 0; int v = BitConverter.ToUInt16(a, p); p += 2; return v; }
            public int U8() { if (!Need(1)) return 0; return a[p++]; }
            public float F32() { if (!Need(4)) return 0; float v = BitConverter.ToSingle(a, p); p += 4; return v; }
            public void Skip(int n) { if (Need(n)) p += n; }
            public string Str()
            {
                if (Bad) return "";
                int e = p;
                while (e < a.Length && a[e] != 0) e++;
                if (e >= a.Length) { Bad = true; return ""; }
                var s = e == p ? "" : Encoding.UTF8.GetString(a, p, e - p);
                p = e + 1;
                return s;
            }
        }

        // ---- the decoded rows of one cache read (pure: bytes in, rows out)
        public sealed class MiscRow { public int SpellId, Difficulty, CastIndex, DurationIndex, RangeIndex, IconId; }
        public sealed class EffectRow { public int SpellId, Difficulty, RadiusIndex; public EffectInfo Effect; }

        public sealed class Decoded
        {
            public int CacheBuild, Rows, Dropped;
            public readonly Dictionary<int, string> Names = new Dictionary<int, string>();
            public readonly Dictionary<int, string[]> Texts = new Dictionary<int, string[]>();      // rank (NameSubtext), description, aura description
            public readonly Dictionary<int, MiscRow> Misc = new Dictionary<int, MiscRow>();           // by SpellMisc row id
            public readonly Dictionary<int, EffectRow> Effects = new Dictionary<int, EffectRow>();   // by SpellEffect row id
            public readonly HashSet<int> Quests = new HashSet<int>();
            public readonly Dictionary<int, LockNeed[]> Locks = new Dictionary<int, LockNeed[]>();
            public readonly Dictionary<int, FactionTemplateInfo> Templates = new Dictionary<int, FactionTemplateInfo>();
            // removed rows ("record removed"), by table name
            public readonly Dictionary<string, HashSet<int>> Removed = new Dictionary<string, HashSet<int>>();
            public readonly List<string> Notes = new List<string>();
            public HashSet<int> RemovedOf(Def d) => Removed.TryGetValue(d.Table.Name, out var s) ? s : NoIds;
            static readonly HashSet<int> NoIds = new HashSet<int>();
        }

        // the game build number at the end of the version ("wow_classic_beta|1.60.1.70374|key" -> 70374); 0 = not known
        public static int BuildNumber(string gameBuild)
        {
            if (string.IsNullOrEmpty(gameBuild)) return 0;
            var parts = gameBuild.Split('|');
            var ver = parts.Length >= 2 ? parts[1] : gameBuild;
            var dot = ver.LastIndexOf('.');
            return int.TryParse(dot >= 0 ? ver.Substring(dot + 1) : ver, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
        }

        // Read the rows of the tables above from DBCache.bin's bytes. `shippedLayouts` = the layout hash each shipped table was read with (AtlasSet.LayoutsSeen):
        // a table is only decoded when its shipped layout is the verified one. `gameBuild` (0 = unknown) must match the cache's own build.
        public static Decoded Decode(byte[] file, IDictionary<string, uint> shippedLayouts, int gameBuild)
        {
            var d = new Decoded();
            var tables = DbCache.ReadTables(file, new HashSet<uint>(All.Select(x => x.Table.TableHash).Concat(LeftOut.Keys)));
            d.CacheBuild = tables.Build;
            if (tables.Truncated) d.Notes.Add("DBCache.bin ends in the middle of an entry (the game was writing it): the rows before that are used");
            if (gameBuild > 0 && tables.Build != gameBuild)
            {
                d.Notes.Add("DBCache.bin is of build " + tables.Build.ToString(CultureInfo.InvariantCulture) + ", the game is " + gameBuild.ToString(CultureInfo.InvariantCulture) + ": its hotfixes are not used");
                return d;
            }
            foreach (var kv in LeftOut)
            {
                var t = tables.Get(kv.Key);
                if (t != null && (t.Rows.Count > 0 || t.Removed.Count > 0)) d.Notes.Add("hotfixes: " + kv.Value + ": " + t.Rows.Count.ToString(CultureInfo.InvariantCulture) + " rows not used");
            }
            foreach (var def in All)
            {
                var t = tables.Get(def.Table.TableHash);
                if (t == null || (t.Rows.Count == 0 && t.Removed.Count == 0)) continue;
                if (shippedLayouts == null || !shippedLayouts.TryGetValue(def.Table.Name, out var lay) || lay != def.Layout)
                {
                    d.Notes.Add("hotfixes: " + def.Table.Name + ": " + t.Rows.Count.ToString(CultureInfo.InvariantCulture) + " rows not used (the game's " + def.Table.Name + " layout is not the one this Outpost verified)");
                    continue;
                }
                if (t.Removed.Count > 0) d.Removed[def.Table.Name] = new HashSet<int>(t.Removed);
                foreach (var r in t.Rows)
                {
                    bool ok;
                    try { ok = DecodeRow(d, def, r.Key, r.Value); } catch (Exception) { ok = false; }
                    if (ok) d.Rows++; else d.Dropped++;
                }
            }
            if (d.Dropped > 0) d.Notes.Add("hotfixes: " + d.Dropped.ToString(CultureInfo.InvariantCulture) + " rows did not have their layout's length and were skipped");
            return d;
        }

        static bool DecodeRow(Decoded d, Def def, int id, byte[] bytes)
        {
            if (def.Length >= 0 && bytes.Length != def.Length) return false;
            var r = new Row(bytes);
            if (def == SpellName)
            {
                var n = r.Str();
                if (!r.AtEnd) return false;
                d.Names[id] = n.Trim();
            }
            else if (def == Spell)
            {
                var a = new[] { r.Str().Trim(), r.Str().Trim(), r.Str().Trim() };
                if (!r.AtEnd) return false;
                d.Texts[id] = a;
            }
            else if (def == SpellMisc)
            {
                r.Skip(17 * 4);
                var m = new MiscRow { Difficulty = r.I16(), CastIndex = r.U16(), DurationIndex = r.U16() };
                r.U16(); m.RangeIndex = r.U16(); r.U8(); r.F32(); r.F32(); r.F32();
                m.IconId = r.I32(); r.I32(); r.I32(); r.I32(); r.I32(); r.I32();
                m.SpellId = r.I32();
                if (!r.AtEnd || m.SpellId <= 0) return false;
                d.Misc[id] = m;
            }
            else if (def == SpellEffect)
            {
                var e = new EffectInfo();
                e.Aura = r.I16(); int diff = r.I16(); e.Index = r.I32(); e.Effect = r.I32(); r.F32(); r.I32(); e.PeriodMs = r.I32(); r.F32(); r.F32(); e.Chain = r.I32();
                e.ItemType = r.I32(); r.I32(); r.F32(); r.F32(); r.F32(); e.Trigger = r.I32(); r.F32(); r.F32(); e.Coef = r.F32(); r.F32(); r.F32(); r.F32(); e.Points = r.F32();
                r.I32(); r.I32(); e.Misc = r.I32(); r.I32(); int radius = r.I32(); int radius2 = r.I32(); if (radius <= 0) radius = radius2; r.Skip(16); r.I16(); r.I16();   // radius: the second slot when the first is empty
                int spell = r.I32();
                if (!r.AtEnd || spell <= 0 || e.Index < 0 || e.Index > 31) return false;
                d.Effects[id] = new EffectRow { SpellId = spell, Difficulty = diff, RadiusIndex = radius, Effect = e };
            }
            else if (def == QuestV2)
            {
                int inline = r.I32(); r.I32(); r.I32();
                if (!r.AtEnd || inline != id || id <= 0) return false;
                d.Quests.Add(id);
            }
            else if (def == Lock)
            {
                r.I32();
                int[] idx = new int[8], skill = new int[8], type = new int[8];
                for (int e = 0; e < 8; e++) idx[e] = r.I32();
                for (int e = 0; e < 8; e++) skill[e] = r.U16();
                for (int e = 0; e < 8; e++) type[e] = r.U8();
                r.Skip(8);
                if (!r.AtEnd) return false;
                d.Locks[id] = WorldLookups.NeedsOf(idx, skill, type);
            }
            else if (def == FactionTemplate)
            {
                var f = new FactionTemplateInfo { Id = id, FactionId = r.U16(), Flags = r.I32(), Group = r.U8(), FriendGroup = r.U8(), EnemyGroup = r.U8() };
                var en = new List<int>(); var fr = new List<int>();
                for (int e = 0; e < 8; e++) { int v = r.U16(); if (v > 0) en.Add(v); }
                for (int e = 0; e < 8; e++) { int v = r.U16(); if (v > 0) fr.Add(v); }
                if (!r.AtEnd) return false;
                f.Enemies = en.ToArray(); f.Friends = fr.ToArray();
                d.Templates[id] = f;
            }
            else return false;
            return true;
        }

        // ---- laying the rows over the shipped set: a NEW set (the shipped one, saved in the index file, is never changed)
        public static AtlasSet Apply(AtlasSet shipped, Decoded d)
        {
            if (shipped == null) return null;
            var o = new AtlasSet { Spells = shipped.Spells, Factions = shipped.Factions, Details = shipped.Details, World = shipped.World };
            foreach (var kv in shipped.Problems) o.Problems[kv.Key] = kv.Value;
            o.Notes.AddRange(shipped.Notes);
            foreach (var kv in shipped.LayoutsSeen) o.LayoutsSeen[kv.Key] = kv.Value;
            var info = new HotfixInfo { CacheBuild = d?.CacheBuild ?? 0, Rows = d?.Rows ?? 0 };
            o.Hotfixes = info;
            if (d == null) return o;
            info.Notes.AddRange(d.Notes);
            if (shipped.Spells != null) o.Spells = ApplySpells(shipped.Spells, d, info);
            if (shipped.World != null && (d.Quests.Count > 0 || d.Locks.Count > 0 || d.RemovedOf(QuestV2).Count > 0 || d.RemovedOf(Lock).Count > 0))
            {
                var w = shipped.World.CopyForOverlay();
                foreach (var q in d.Quests) { if (!shipped.World.QuestIds.Contains(q)) info.NewQuests.Add(q); w.QuestIds.Add(q); }
                foreach (var q in d.RemovedOf(QuestV2)) w.QuestIds.Remove(q);
                foreach (var kv in d.Locks)
                {
                    info.Locks.Add(kv.Key);
                    if (kv.Value.Length > 0) w.Locks[kv.Key] = kv.Value; else w.Locks.Remove(kv.Key);
                }
                foreach (var l in d.RemovedOf(Lock)) { if (w.Locks.Remove(l)) info.Locks.Add(l); }
                o.World = w;
            }
            if (shipped.Factions != null && (d.Templates.Count > 0 || d.RemovedOf(FactionTemplate).Count > 0))
            {
                var t = new Dictionary<int, FactionTemplateInfo>(shipped.Factions.Templates);
                foreach (var kv in d.Templates) { t[kv.Key] = kv.Value; info.Templates.Add(kv.Key); }
                foreach (var id in d.RemovedOf(FactionTemplate)) if (t.Remove(id)) info.Templates.Add(id);
                o.Factions = new FactionSnapshot(shipped.Factions.Factions.Values, t.Values);
            }
            return o;
        }

        static SpellSnapshot ApplySpells(SpellSnapshot snap, Decoded d, HotfixInfo info)
        {
            bool any = d.Names.Count + d.Texts.Count + d.Misc.Count + d.Effects.Count > 0 || d.RemovedOf(SpellName).Count + d.RemovedOf(Spell).Count + d.RemovedOf(SpellMisc).Count + d.RemovedOf(SpellEffect).Count > 0;
            if (!any) return snap;
            var spells = new Dictionary<int, SpellInfo>(snap.Spells);
            var copied = new HashSet<int>();
            var lk = snap.Lookups;
            void Mark(int id, HotfixParts p) { info.Spells[id] = info.Spell(id) | p; }
            SpellInfo Edit(int id)
            {
                if (!spells.TryGetValue(id, out var s)) return null;
                if (copied.Add(id))
                {
                    s = new SpellInfo { Id = s.Id, Name = s.Name, Rank = s.Rank, Desc = s.Desc, Aura = s.Aura, IconId = s.IconId, CastMs = s.CastMs, DurationMs = s.DurationMs, RangeMax = s.RangeMax, Effects = s.Effects.ToArray() };
                    spells[id] = s;
                }
                return s;
            }
            void RemoveEffect(int spell, int index)
            {
                var s = Edit(spell);
                if (s == null || s.Effects.All(e => e.Index != index)) return;
                s.Effects = s.Effects.Where(e => e.Index != index).ToArray();
                Mark(spell, HotfixParts.Effects);
            }
            // names first: a new name is a new spell, a removed name is a spell the client no longer has
            foreach (var kv in d.Names)
            {
                if (kv.Value.Length == 0) continue;
                if (spells.ContainsKey(kv.Key))
                {
                    var s = Edit(kv.Key);
                    if (s.Name != kv.Value) { s.Name = kv.Value; Mark(kv.Key, HotfixParts.Name); }
                }
                else { spells[kv.Key] = new SpellInfo { Id = kv.Key, Name = kv.Value }; copied.Add(kv.Key); Mark(kv.Key, HotfixParts.New); }
            }
            foreach (var id in d.RemovedOf(SpellName)) if (spells.Remove(id)) { info.Spells[id] = HotfixParts.Removed; }
            foreach (var kv in d.Texts)
            {
                var s = Edit(kv.Key);
                if (s == null) continue;
                if (s.Rank != kv.Value[0] || s.Desc != kv.Value[1] || s.Aura != kv.Value[2]) { s.Rank = kv.Value[0]; s.Desc = kv.Value[1]; s.Aura = kv.Value[2]; Mark(kv.Key, HotfixParts.Text); }
            }
            foreach (var id in d.RemovedOf(Spell))
            {
                if (!spells.TryGetValue(id, out var have) || (have.Rank.Length == 0 && have.Desc.Length == 0 && have.Aura.Length == 0)) continue;
                var s = Edit(id); s.Rank = ""; s.Desc = ""; s.Aura = ""; Mark(id, HotfixParts.Text);
            }
            // the numbers: a SpellMisc row (default difficulty) gives icon, cast time, duration and range; a removed one takes them away
            foreach (var id in d.RemovedOf(SpellMisc))
            {
                if (!lk.MiscRows.TryGetValue(id, out var sp) || d.Misc.ContainsKey(id)) continue;
                var s = Edit(sp);
                if (s == null) continue;
                s.IconId = 0; s.CastMs = 0; s.DurationMs = 0; s.RangeMax = 0; Mark(sp, HotfixParts.Numbers);
            }
            foreach (var kv in d.Misc)
            {
                var m = kv.Value;
                if (m.Difficulty != 0) continue;
                var s = Edit(m.SpellId);
                if (s == null) continue;
                int cast = lk.CastMs.TryGetValue(m.CastIndex, out var c) ? c : 0, dur = lk.DurationMs.TryGetValue(m.DurationIndex, out var du) ? du : 0;
                float range = lk.RangeMax.TryGetValue(m.RangeIndex, out var ra) ? ra : 0;
                if (s.IconId != m.IconId || s.CastMs != cast || s.DurationMs != dur || s.RangeMax != range)
                {
                    s.IconId = m.IconId; s.CastMs = cast; s.DurationMs = dur; s.RangeMax = range;
                    Mark(m.SpellId, HotfixParts.Numbers);
                }
            }
            // the effects: a row replaces the effect of its spell and index (and, if the shipped row of that id sat elsewhere, that one goes)
            foreach (var id in d.RemovedOf(SpellEffect))
                if (lk.EffectRows.TryGetValue(id, out var key) && !d.Effects.ContainsKey(id)) RemoveEffect((int)(key >> 8), (int)(key & 0xFF));
            foreach (var kv in d.Effects)
            {
                var er = kv.Value;
                if (er.Difficulty != 0) continue;
                if (lk.EffectRows.TryGetValue(kv.Key, out var was) && was != SpellLookups.EffectKey(er.SpellId, er.Effect.Index)) RemoveEffect((int)(was >> 8), (int)(was & 0xFF));
                var s = Edit(er.SpellId);
                if (s == null) continue;
                var e = er.Effect;
                if (er.RadiusIndex > 0 && lk.Radius.TryGetValue(er.RadiusIndex, out var rr)) e.Radius = rr;
                var old = s.Effects.FirstOrDefault(x => x.Index == e.Index);
                if (old != null && Same(old, e)) continue;
                s.Effects = s.Effects.Where(x => x.Index != e.Index).Concat(new[] { e }).OrderBy(x => x.Index).ToArray();
                Mark(er.SpellId, HotfixParts.Effects);
            }
            return new SpellSnapshot(spells.Values, snap.Skills.Values, snap.Abilities, snap.Reagents, lk);
        }

        static bool Same(EffectInfo a, EffectInfo b) =>
            a.Effect == b.Effect && a.Aura == b.Aura && a.PeriodMs == b.PeriodMs && a.Chain == b.Chain && a.ItemType == b.ItemType && a.Misc == b.Misc && a.Trigger == b.Trigger
            && a.Points == b.Points && a.Coef == b.Coef && a.Radius == b.Radius;

        // what a spell page / tooltip says about the server's part ("" = nothing): "a new spell" / "changed its description and cast time ..."
        public static string Describe(HotfixParts p)
        {
            if (p == HotfixParts.None) return "";
            if ((p & HotfixParts.New) != 0) return "A spell the server added (not in the game files)";
            var what = new List<string>();
            if ((p & HotfixParts.Name) != 0) what.Add("name");
            if ((p & HotfixParts.Text) != 0) what.Add("description");
            if ((p & HotfixParts.Numbers) != 0) what.Add("icon, cast time, duration or range");
            if ((p & HotfixParts.Effects) != 0) what.Add("effects");
            return what.Count == 0 ? "" : "The server changed its " + string.Join(", ", what.Take(what.Count - 1)) + (what.Count > 1 ? " and " : "") + what.Last();
        }
    }
}
