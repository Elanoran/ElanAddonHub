using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace ElansAddonHub.Services.Db2
{
    // ============================================================================================================================
    // Small lookup tables of the player's own game files that turn the numbers of the WDB caches (SeenData) into words: what a lock needs (Herbalism 70,
    // Mining 125, Lockpicking 25), the names of the quest sorts (Cooking, Paladin, The High Order ...) and quest kinds (Elite, Dungeon, PvP), Forever's
    // quest lines, and the list of quests the client knows at all. Read together with the spell data (SpellData, same cache file, same rules: a table whose
    // layout this version does not know is left out, nothing is guessed).
    //
    // Source of every field number: wowdev/WoWDBDefs definitions/*.dbd, the LAYOUT blocks that list 1.60.1.70374 (and the earlier 1.60.1 builds); table and
    // layout hashes read from the real files of that build; values checked: Lock 29 = Herbalism 1, 30 = Herbalism 15, 9 = Herbalism 50, 31 = Herbalism 70,
    // 11 = Herbalism 100, 45 = Herbalism 125, 38 = Mining 1, 39 = Mining 65, 41 = Mining 125, 2 = Lockpicking 25 (the Classic values); LockType 2 Herbalism,
    // 3 Mining, 1 Pick Lock; QuestSort 304 Cooking, 141 Paladin; QuestInfo 81 Dungeon, 1 Elite; QuestV2 holds 6,610 quest ids (90 key-locked).
    // ============================================================================================================================
    public static class WorldTables
    {
        static SpellTables.Def D(string name, int fileId, uint table, uint layout) => new SpellTables.Def { Name = name, FileId = fileId, TableHash = table, Layouts = new[] { layout }, Build = "1.60.1.70374" };

        // Lock: Flags(0) Index[8](1) Skill[8](2) Type[8](3: 1 item, 2 skill) Action[8](4); for Type 2 the Index is a LockType id and Skill the level needed
        public static readonly SpellTables.Def Lock = D("Lock", 1343608, 0xE9BECB23, 0xDA418152);
        public const int LkIndex = 1, LkSkill = 2, LkType = 3;
        // LockType: Name_lang(0) ResourceName_lang(1) Verb_lang(2) CursorName(3) EnumID(4) TrackingIcon(5)
        public static readonly SpellTables.Def LockType = D("LockType", 1284823, 0x9349B344, 0xAF114F71);
        // QuestSort: SortName_lang(0) UiOrderIndex(1) Flags(2)
        public static readonly SpellTables.Def QuestSort = D("QuestSort", 1134585, 0x5479CB09, 0x9B92BE63);
        // QuestInfo: InfoName_lang(0) Type(1) Modifiers(2) Profession(3)
        public static readonly SpellTables.Def QuestInfo = D("QuestInfo", 1120960, 0x2D11D732, 0xE505C927);
        // QuestLine: Name_lang(0) Description_lang(1) ...; QuestLineXQuest (the relation is stored inline): QuestLineID(0) QuestID(1) OrderIndex(2) Flags(3) (4)
        public static readonly SpellTables.Def QuestLine = D("QuestLine", 973430, 0xEB57A423, 0x27C5AD69);
        public static readonly SpellTables.Def QuestLineXQuest = D("QuestLineXQuest", 973441, 0x8CC17856, 0x8552F722);
        // QuestV2: only the id list is used (which quests exist in the client at all)
        public static readonly SpellTables.Def QuestV2 = D("QuestV2", 1139443, 0x3AC83109, 0x1854BDB9);

        public static readonly SpellTables.Def[] All = { Lock, LockType, QuestSort, QuestInfo, QuestLine, QuestLineXQuest, QuestV2 };
    }

    public struct LockNeed { public int LockType, Skill; }

    public sealed class QuestLineInfo { public int Id; public string Name = ""; public int[] Quests = new int[0]; }

    public sealed class WorldLookups
    {
        public readonly Dictionary<int, LockNeed[]> Locks = new Dictionary<int, LockNeed[]>();   // only the skill needs (Type 2)
        public readonly Dictionary<int, string> LockTypes = new Dictionary<int, string>();
        public readonly Dictionary<int, string> QuestSorts = new Dictionary<int, string>();
        public readonly Dictionary<int, string> QuestInfos = new Dictionary<int, string>();
        public readonly Dictionary<int, QuestLineInfo> QuestLines = new Dictionary<int, QuestLineInfo>();
        public readonly Dictionary<int, int> LineOfQuest = new Dictionary<int, int>();
        public readonly HashSet<int> QuestIds = new HashSet<int>();

        public const int LockTypeLockpicking = 1, LockTypeHerbalism = 2, LockTypeMining = 3;

        public WorldLookups() { }

        // a copy whose lock needs and quest list can be changed (the server's hotfixes, ServerHotfixes.Apply); the name tables and quest lines are shared
        public WorldLookups CopyForOverlay()
        {
            var w = new WorldLookups(LockTypes, QuestSorts, QuestInfos, QuestLines, LineOfQuest);
            foreach (var kv in Locks) w.Locks[kv.Key] = kv.Value;
            w.QuestIds.UnionWith(QuestIds);
            return w;
        }

        WorldLookups(Dictionary<int, string> lockTypes, Dictionary<int, string> sorts, Dictionary<int, string> infos, Dictionary<int, QuestLineInfo> lines, Dictionary<int, int> lineOf)
        {
            LockTypes = lockTypes; QuestSorts = sorts; QuestInfos = infos; QuestLines = lines; LineOfQuest = lineOf;
        }

        // the skill needs of one Lock row (Type 2 entries with a LockType); the same rule for the shipped table and a hotfix row
        public static LockNeed[] NeedsOf(int[] index, int[] skill, int[] type)
        {
            var needs = new List<LockNeed>();
            for (int e = 0; e < type.Length; e++) if (type[e] == 2 && index[e] > 0) needs.Add(new LockNeed { LockType = index[e], Skill = skill[e] });
            return needs.ToArray();
        }

        // the skill a lock needs: (lock type, level) - the first need with a skill level (herbs and veins have exactly one; a chest's "Open" / "Close"
        // entries have level 0 and need no skill); null = none / unknown lock
        public LockNeed? SkillNeed(int lockId)
        {
            if (!Locks.TryGetValue(lockId, out var a)) return null;
            foreach (var n in a) if (n.Skill > 0) return n;
            return null;
        }

        public string LockTypeName(int id) => LockTypes.TryGetValue(id, out var n) && !string.IsNullOrEmpty(n) ? n : null;
        // QuestSort names a quest's negative sort: -304 -> "Cooking"
        public string SortName(int sort) => sort < 0 && QuestSorts.TryGetValue(-sort, out var n) && !string.IsNullOrEmpty(n) ? n : null;
        public string InfoName(int id) => id > 0 && QuestInfos.TryGetValue(id, out var n) && !string.IsNullOrEmpty(n) ? n : null;
        public QuestLineInfo LineOf(int questId) => LineOfQuest.TryGetValue(questId, out var l) && QuestLines.TryGetValue(l, out var q) ? q : null;

        // ---- parsing (pure: bytes in, model out)
        public static WorldLookups Parse(AtlasSet set, SpellRaw raw)
        {
            var w = new WorldLookups();
            int got = 0;
            var lk = SpellParse.Take(set, raw, WorldTables.Lock);
            if (lk.Ok)
            {
                got++;
                var t = lk.Table;
                for (int i = 0; i < t.Count; i++)
                {
                    int[] idx = new int[8], skill = new int[8], type = new int[8];
                    for (int e = 0; e < 8; e++)
                    {
                        type[e] = (int)t.FieldArr(i, WorldTables.LkType, e, 8);
                        idx[e] = (int)t.FieldArr(i, WorldTables.LkIndex, e, 8); skill[e] = (int)t.FieldArr(i, WorldTables.LkSkill, e, 8);
                    }
                    var needs = NeedsOf(idx, skill, type);
                    if (needs.Length > 0) w.Locks[t.IdAt(i)] = needs;
                }
            }
            void Names(SpellTables.Def def, Dictionary<int, string> into)
            {
                var o = SpellParse.Take(set, raw, def);
                if (!o.Ok) return;
                got++;
                for (int i = 0; i < o.Table.Count; i++) into[o.Table.IdAt(i)] = (o.Table.String(i, 0) ?? "").Trim();
            }
            Names(WorldTables.LockType, w.LockTypes);
            Names(WorldTables.QuestSort, w.QuestSorts);
            Names(WorldTables.QuestInfo, w.QuestInfos);
            var ql = SpellParse.Take(set, raw, WorldTables.QuestLine);
            if (ql.Ok)
            {
                got++;
                for (int i = 0; i < ql.Table.Count; i++) w.QuestLines[ql.Table.IdAt(i)] = new QuestLineInfo { Id = ql.Table.IdAt(i), Name = (ql.Table.String(i, 0) ?? "").Trim() };
                var qx = SpellParse.Take(set, raw, WorldTables.QuestLineXQuest);
                if (qx.Ok)
                {
                    var by = new Dictionary<int, List<KeyValuePair<int, int>>>();
                    for (int i = 0; i < qx.Table.Count; i++)
                    {
                        int line = (int)qx.Table.Field(i, 0), quest = (int)qx.Table.Field(i, 1), order = (int)qx.Table.Field(i, 2);
                        if (line <= 0 || quest <= 0 || !w.QuestLines.ContainsKey(line)) continue;
                        if (!by.TryGetValue(line, out var l)) by[line] = l = new List<KeyValuePair<int, int>>();
                        l.Add(new KeyValuePair<int, int>(order, quest));
                    }
                    foreach (var kv in by)
                    {
                        w.QuestLines[kv.Key].Quests = kv.Value.OrderBy(x => x.Key).ThenBy(x => x.Value).Select(x => x.Value).ToArray();
                        foreach (var q in w.QuestLines[kv.Key].Quests) if (!w.LineOfQuest.ContainsKey(q)) w.LineOfQuest[q] = kv.Key;
                    }
                }
            }
            var qv = SpellParse.Take(set, raw, WorldTables.QuestV2);
            if (qv.Ok) { got++; for (int i = 0; i < qv.Table.Count; i++) w.QuestIds.Add(qv.Table.IdAt(i)); }
            if (got == 0) { set.Problems["world"] = "none of the lock / quest tables could be read"; return null; }
            return w;
        }

        // ---- the saved index (inside spelldata.bin)
        public static void Write(BinaryWriter w, WorldLookups x)
        {
            w.Write(x != null);
            if (x == null) return;
            w.Write(x.Locks.Count);
            foreach (var kv in x.Locks) { w.Write(kv.Key); w.Write((byte)kv.Value.Length); foreach (var n in kv.Value) { w.Write(n.LockType); w.Write(n.Skill); } }
            void Names(Dictionary<int, string> d) { w.Write(d.Count); foreach (var kv in d) { w.Write(kv.Key); w.Write(kv.Value ?? ""); } }
            Names(x.LockTypes); Names(x.QuestSorts); Names(x.QuestInfos);
            w.Write(x.QuestLines.Count);
            foreach (var l in x.QuestLines.Values) { w.Write(l.Id); w.Write(l.Name ?? ""); w.Write(l.Quests.Length); foreach (var q in l.Quests) w.Write(q); }
            w.Write(x.QuestIds.Count);
            foreach (var q in x.QuestIds) w.Write(q);
        }

        public static WorldLookups Read(BinaryReader r)
        {
            if (!r.ReadBoolean()) return null;
            var x = new WorldLookups();
            int Count(int max) { int n = r.ReadInt32(); if (n < 0 || n > max) throw new InvalidDataException("count"); return n; }
            int c = Count(1000000);
            for (int i = 0; i < c; i++)
            {
                int id = r.ReadInt32(); int k = r.ReadByte();
                var a = new LockNeed[k];
                for (int j = 0; j < k; j++) a[j] = new LockNeed { LockType = r.ReadInt32(), Skill = r.ReadInt32() };
                x.Locks[id] = a;
            }
            void Names(Dictionary<int, string> d) { int n = Count(1000000); for (int i = 0; i < n; i++) d[r.ReadInt32()] = r.ReadString(); }
            Names(x.LockTypes); Names(x.QuestSorts); Names(x.QuestInfos);
            c = Count(1000000);
            for (int i = 0; i < c; i++)
            {
                var l = new QuestLineInfo { Id = r.ReadInt32(), Name = r.ReadString() };
                int n = Count(100000); l.Quests = new int[n];
                for (int j = 0; j < n; j++) { l.Quests[j] = r.ReadInt32(); if (!x.LineOfQuest.ContainsKey(l.Quests[j])) x.LineOfQuest[l.Quests[j]] = l.Id; }
                x.QuestLines[l.Id] = l;
            }
            c = Count(5000000);
            for (int i = 0; i < c; i++) x.QuestIds.Add(r.ReadInt32());
            return x;
        }
    }
}
