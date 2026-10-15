using System;
using System.Collections.Generic;
using System.Text;

namespace ElansAddonHub.Services.Db2
{
    // ============================================================================================================================
    // Atlas layer A3, record decoders for the three caches. Layouts were worked out against the real files of build 70338 (WoW Forever, 1.60.1) and the
    // public descriptions of the server's query answers (WoWDev wiki "WDB", TrinityCore's CreatureStats / GameObjectStats / QuestInfo packets), and every
    // decoder verifies the exact record length it computes: a record that does not fit is dropped, never half-read.
    // Fields that were checked against known creatures / objects / quests are decoded; everything else stays raw or is not read.
    // ============================================================================================================================
    public sealed class WdbCreature
    {
        public int Id;
        public string Name = "", Title = "";                       // Title = the "<Subname>" under the name (Fanglord, Weaponsmith ...)
        public int Type, Family, Rank;                             // Type: 1 beast ... 7 humanoid ... (CreatureType); Rank: 0 normal, 1 elite, 2 rare elite, 3 boss, 4 rare
        public int DisplayId;                                      // the first model id (0 = none)
        // Flags0 / Flags1 / Flags2 = the creature's TYPE flags (CreatureTypeFlags, 1-3), NOT npc flags: the query answer carries no npc flags (vendors and
        // trainers show 0x08000000 like every other creature). Verified bits: 0x1 tameable (all 156 tameable beasts of the real cache, and a beast whose
        // family is set but which Forever made untameable lacks it), 0x4 boss level "??" (Thrall, Cairne, Sylvanas, Varimathras).
        public uint Flags0, Flags1, Flags2;
        public int KillCredit1, KillCredit2;                       // the creature this one counts as for quests ("ProxyCreatureID"), 0 = none
        public int[] QuestItems = NoInts;                          // items the creature drops for quests
        public static readonly int[] NoInts = new int[0];
        public const uint TypeTameable = 0x1, TypeBoss = 0x4;
        public bool Tameable => (Flags0 & TypeTameable) != 0;
        public bool BossLevel => (Flags0 & TypeBoss) != 0;
    }

    public sealed class WdbObject
    {
        public int Id;
        public string Name = "";
        public int Type, DisplayId;                                // Type: GameObject type (3 chest, 7 chair, 19 mailbox, 50 gathering node ...)
        public int[] Data = new int[Count];                        // the 35 type-specific data fields, raw (chest: lock id, loot id ...)
        public int[] QuestItems = WdbCreature.NoInts;
        public const int Count = 35;
        public int ChestLootId => Type == 3 && Data.Length > 1 ? Data[1] : 0;
        // the Lock (Lock.db2) a chest-type object opens with: herbs, ore veins and chests are all type 3 here. Verified on the real cache against the
        // Lock table: Peacebloom / Silverleaf 29 (Herbalism 1), Earthroot 30 (15), Mageroyal 9 (50), Briarthorn 31 (70), Bruiseweed 11 (100), Kingsblood 45 (125),
        // Copper Vein 38 (Mining 1), Tin Vein 39 (65), Iron Deposit 41 (125) - the Classic skill levels.
        public int LockId => Type == 3 && Data.Length > 0 ? Data[0] : 0;
    }

    public struct WdbItemCount { public int Id, Count; }

    // one objective of a quest. Type (QuestObjectiveType): 0 kill / talk to a creature, 1 item, 2 game object, 6 reputation (ObjectId = faction, Amount = the
    // reputation value), 10 area (exploration), others raw. Verified against the Classic baseline: 136 of 138 kill / object lists and 137 of 138 item lists
    // are the same ids and counts (the rest are Forever's changes).
    public sealed class WdbObjective { public int Type, ObjectId, Amount; public string Text = ""; }

    public sealed class WdbQuest
    {
        public int Id, Type, Level, MinLevel;
        public int Sort;                                           // > 0 the zone (AreaTable id) the quest is sorted under, < 0 a quest sort (class, profession ...)
        public string Title = "", Objectives = "", Description = "";
        // ---- the fields of the full answer (Full = true). Each was checked against the real cache of build 70374 (200 quests, every record decoded to its
        // exact length) and against the Classic baseline's quest_template for the 138 quests both know: level 137/138, min level 135, sort 138, flags 136,
        // quest info 134, reward items 136, choices 127 (+ Forever's new items), provided items 137, start item 138, reward spell 137, money 136,
        // suggested group 138, race masks 11/11, reputation factions 131 - the differences are Forever's own changes.
        public bool Full;
        public int Flags, FlagsEx;                                 // QuestFlags (0x8 sharable ...)
        public int InfoId;                                         // QuestInfo.db2 (1 elite / group, 41 PvP, 62 raid, 81 dungeon ...); 0 = normal
        public int SuggestedGroup;
        public int NextQuest;                                      // the quest the giver offers right after this one (0 = none)
        public int Money;                                          // copper (QuestMoneyReward[level][difficulty] gives the same number)
        public int RewardSpell, StartItem;
        public ulong Races = AllRaces;                             // allowed races as ChrRaces.PlayableRaceBit bits; AllRaces = no limit
        public WdbItemCount[] Rewards = NoItems, Choices = NoItems, Provided = NoItems;   // Provided = items the quest hands out for its objectives
        public int[] RepFactions = WdbCreature.NoInts;             // factions it gives reputation with (the amount is an index the client turns into a number: not shown)
        public WdbObjective[] Goals = NoGoals;
        public string AreaDescription = "", CompletionLog = "";   // "Scout the gazebo ..." / "Return to Rau Cliffrunner in Freewind Post."
        public const ulong AllRaces = ulong.MaxValue;
        public static readonly WdbItemCount[] NoItems = new WdbItemCount[0];
        public static readonly WdbObjective[] NoGoals = new WdbObjective[0];
        public bool RaceLimited => Races != AllRaces && Races != 0;
    }

    public sealed class WdbResult<T>
    {
        public List<T> Items = new List<T>();
        public int Records, Skipped;
        public int Build, RecordVersion;
        public bool Truncated;
        // most records fit the layout: the file is used. Fewer = the game was patched and this reader does not know the new layout.
        public bool Recognised => Records == 0 || Items.Count * 10L >= Records * 8L;
    }

    public static class WdbSeen
    {
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, false);

        public static WdbResult<WdbCreature> Creatures(byte[] data)
        {
            var f = Wdb.Parse(data, Wdb.CreatureMagic);
            var r = Start<WdbCreature>(f);
            foreach (var e in f.Entries)
            {
                var c = DecodeCreature(f.Data, e.Offset, e.Length, e.Id);
                if (c == null) r.Skipped++; else r.Items.Add(c);
            }
            return r;
        }

        public static WdbResult<WdbObject> Objects(byte[] data)
        {
            var f = Wdb.Parse(data, Wdb.GameObjectMagic);
            var r = Start<WdbObject>(f);
            foreach (var e in f.Entries)
            {
                var o = DecodeObject(f.Data, e.Offset, e.Length, e.Id);
                if (o == null) r.Skipped++; else r.Items.Add(o);
            }
            return r;
        }

        public static WdbResult<WdbQuest> Quests(byte[] data)
        {
            var f = Wdb.Parse(data, Wdb.QuestMagic);
            var r = Start<WdbQuest>(f);
            foreach (var e in f.Entries)
            {
                var q = DecodeQuest(f.Data, e.Offset, e.Length, e.Id);
                if (q == null) r.Skipped++; else r.Items.Add(q);
            }
            return r;
        }

        static WdbResult<T> Start<T>(WdbFile f) => new WdbResult<T> { Records = f.Entries.Count, Build = f.Build, RecordVersion = f.RecordVersion, Truncated = f.Truncated };

        static uint U32(byte[] d, int at) => BitConverter.ToUInt32(d, at);

        // a zero-terminated string of exactly `len` bytes including the terminator
        static string CStr(byte[] d, int at, int len)
        {
            if (len <= 0) return "";
            if (d[at + len - 1] != 0) return null;
            return len == 1 ? "" : Utf8.GetString(d, at, len - 1);
        }

        // ---- creature (TrinityCore QueryCreatureResponse of the 11.x / 12.x clients this one is built on; every field position checked on the real cache,
        //      all 1,575 records of build 70374 decode to their exact length):
        //      [15 byte bit header: title 11, title2 11, cursor 6, leader 1, then name / name2 lengths 11 + 11 for 4 names]
        //      [the names that are not empty, zero-terminated][type flags u32 x3, type u8, family i32, rank i8, kill credit i32 x2][display count u32,
        //      total probability f32, (display id u32, scale f32, probability f32) x count][health f32, energy f32][quest item count u32][quest currency count u32]
        //      [movement, health scaling, expansion, vignette, class, difficulty, widget set, widget condition: i32 x8][title strings][quest items u32 x count][currencies u32 x count]
        public static WdbCreature DecodeCreature(byte[] d, int off, int len, int id)
        {
            try
            {
                if (len < 15) return null;
                var b = new WdbBits(d, off, 15);
                int tl = b.Read(11), tl2 = b.Read(11), cl = b.Read(6); b.Read(1);
                var nl = new int[8];
                for (int i = 0; i < 8; i++) nl[i] = b.Read(11);
                int p = off + 15, end = off + len;
                string name = null;
                for (int i = 0; i < 8; i++)
                {
                    if (nl[i] == 0) continue;
                    if (p + nl[i] > end) return null;
                    var s = CStr(d, p, nl[i]);
                    if (s == null) return null;
                    if (i == 0) name = s;
                    p += nl[i];
                }
                if (string.IsNullOrEmpty(name)) return null;
                if (p + 34 > end) return null;
                uint dc = U32(d, p + 26);
                if (dc > 64) return null;
                int q = p + 34 + 12 * (int)dc + 8;
                if (q + 8 > end) return null;
                uint qc = U32(d, q), cc = U32(d, q + 4);
                if (qc > 64 || cc > 64) return null;
                int ts = q + 40;
                if (ts + tl + tl2 + cl + 4L * qc + 4L * cc != end) return null;
                var c = new WdbCreature
                {
                    Id = id, Name = name, Flags0 = U32(d, p), Flags1 = U32(d, p + 4), Flags2 = U32(d, p + 8), Type = d[p + 12], Family = (int)U32(d, p + 13), Rank = (sbyte)d[p + 17],
                    KillCredit1 = (int)U32(d, p + 18), KillCredit2 = (int)U32(d, p + 22), DisplayId = dc > 0 ? (int)U32(d, p + 34) : 0,
                };
                if (c.Family < 0 || c.Family > 100000) c.Family = 0;
                if (c.Rank < 0) c.Rank = 0;
                if (tl > 0) { var t = CStr(d, ts, tl); if (t == null) return null; c.Title = t; }
                if (qc > 0)
                {
                    c.QuestItems = new int[qc];
                    int at = ts + tl + tl2 + cl;
                    for (int i = 0; i < qc; i++) c.QuestItems[i] = (int)U32(d, at + 4 * i);
                }
                return c;
            }
            catch (Exception) { return null; }
        }

        // ---- game object: [type u32][display u32][7 zero-terminated strings: 4 names, icon, cast bar, unknown][35 x i32 data][size f32][quest item count u8][u32 x count][8 bytes]
        public static WdbObject DecodeObject(byte[] d, int off, int len, int id)
        {
            try
            {
                int end = off + len;
                if (len < 8 + 7) return null;
                int p = off + 8;
                string name = null;
                for (int i = 0; i < 7; i++)
                {
                    int z = Array.IndexOf(d, (byte)0, p, end - p);
                    if (z < 0) return null;
                    if (i == 0) name = Utf8.GetString(d, p, z - p);
                    p = z + 1;
                }
                if (string.IsNullOrEmpty(name)) return null;
                int dataAt = p, qcAt = p + 4 * WdbObject.Count + 4;
                if (qcAt + 1 > end) return null;
                int qc = d[qcAt];
                if (qc > 64 || qcAt + 1 + 4 * qc + 8 != end) return null;
                var o = new WdbObject { Id = id, Name = name, Type = (int)U32(d, off), DisplayId = (int)U32(d, off + 4) };
                for (int i = 0; i < WdbObject.Count; i++) o.Data[i] = (int)U32(d, dataAt + 4 * i);
                if (qc > 0) { o.QuestItems = new int[qc]; for (int i = 0; i < qc; i++) o.QuestItems[i] = (int)U32(d, qcAt + 1 + 4 * i); }
                return o;
            }
            catch (Exception) { return null; }
        }

        // ---- quest. The whole answer (QueryQuestInfoResponse) as this client sends it: the 12.1 retail layout (TrinityCore WowPacketParser,
        //      module V12_0_0_65390 QueryHandler) with the Classic quest head (QuestLevel, min level) of the 1.x clients. Byte positions:
        //   +0 id  +4 type  +8 level  +12 (0)  +16 min level  +20 (0)  +24 sort  +28 quest info  +32 suggested group  +36 next quest  +40 xp difficulty
        //   +44 xp multiplier f32  +48 money  +52 money difficulty  +56 money multiplier f32  +60 bonus money  +64 display spell count  +68 reward spell
        //   +72 honor  +76 kill honor f32  +80 favor  +84 artifact xp difficulty  +88 artifact xp multiplier f32  +92 artifact category  +96 start item
        //   +100 flags  +104 flags ex  +108 flags ex2  +112 flags ex3  +116 reward items (item, amount, provided item, provided amount) x4
        //   +180 choices (item, amount, display) x6  +252 POI (map, x f32, y f32, priority)  +268 title, arena points, skill line, skill ups
        //   +284 portraits x4  +300 reputation (faction, value, override, cap) x5  +380 reputation flags  +384 currencies (id, amount) x4
        //   +416 accept sound  +420 complete sound  +424 area group  +428 time allowed i64  +436 objective count  +440 race mask u64
        //   +448 treasure pickers, non-displayed pickers  +456 expansion  +460 world state  +464 session bonus  +468 giver creature
        //   +472 conditional descriptions, conditional completion logs, house rooms, house decor (counts)  +488 display spells (spell, condition, type) x count
        //   then the objectives: id u32, type u32, storage index i8, object i32, amount i32, conditional amount i32, flags u32, flags2 u32, progress f32,
        //   visual effect count i32, parent i32, effects i32 x count, [8 bits text length, 1 bit visible], text
        //   then the pickers, the conditional texts (condition i32, creature i32, [12 bits length], text), house rooms / decor i32 x count,
        //   [9 + 12 + 12 + 9 + 10 + 8 + 10 + 8 + 11 bits of text lengths, 2 flag bits] and the nine texts.
        // A record has to end exactly where this layout says; one that does not is read with the older text-only rule below (title and texts, no numbers).
        // Not decoded because they could not be checked against known quests: the xp / reputation amounts (indexes into QuestXP / QuestFactionReward whose
        // numbers do not match the Classic values), the time limit, the portraits, the POI (one quest only) and the giver creature (always 0 on Forever).
        public static WdbQuest DecodeQuest(byte[] d, int off, int len, int id)
        {
            var q = DecodeQuestFull(d, off, len, id);
            return q ?? DecodeQuestTexts(d, off, len, id);
        }

        static int I32(byte[] d, int at) => BitConverter.ToInt32(d, at);

        public static WdbQuest DecodeQuestFull(byte[] d, int off, int len, int id)
        {
            try
            {
                const int Head = 488;
                if (len < Head + 12 || U32(d, off) != (uint)id) return null;
                int end = off + len;
                int disp = I32(d, off + 64), oc = I32(d, off + 436), tp = I32(d, off + 448), tp2 = I32(d, off + 452);
                int cdesc = I32(d, off + 472), clog = I32(d, off + 476), rooms = I32(d, off + 480), decor = I32(d, off + 484);
                foreach (var n in new[] { disp, oc, tp, tp2, cdesc, clog, rooms, decor }) if (n < 0 || n > 64) return null;
                int p = off + Head + 12 * disp;
                var goals = new List<WdbObjective>();
                for (int i = 0; i < oc; i++)
                {
                    if (p + 43 > end) return null;
                    int type = I32(d, p + 4), obj = I32(d, p + 9), amount = I32(d, p + 13), ve = I32(d, p + 33);
                    if (ve < 0 || ve > 64) return null;
                    p += 41 + 4 * ve;
                    if (p + 2 > end) return null;
                    int tl = new WdbBits(d, p, 2).Read(8);
                    p += 2;
                    if (p + tl > end) return null;
                    goals.Add(new WdbObjective { Type = type, ObjectId = obj, Amount = amount, Text = tl == 0 ? "" : Utf8.GetString(d, p, tl) });
                    p += tl;
                }
                p += 4 * (tp + tp2);
                for (int i = 0; i < cdesc + clog; i++)
                {
                    if (p + 10 > end) return null;
                    int tl = new WdbBits(d, p + 8, 2).Read(12);
                    p += 10 + tl;
                }
                p += 4 * (rooms + decor);
                if (p + 12 > end) return null;
                var b = new WdbBits(d, p, 12);
                var lens = new int[9];
                long sum = 0;
                for (int i = 0; i < 9; i++) { lens[i] = b.Read(QWidths[i]); sum += lens[i]; }
                p += 12;
                if (lens[0] == 0 || p + sum != end || !ReadsAsText(d, p, Math.Min(lens[0], 64))) return null;
                var t = new string[9];
                for (int i = 0; i < 9; i++) { t[i] = lens[i] == 0 ? "" : Utf8.GetString(d, p, lens[i]); p += lens[i]; }
                var q = new WdbQuest
                {
                    Id = id, Type = I32(d, off + 4), Level = I32(d, off + 8), MinLevel = I32(d, off + 16), Sort = I32(d, off + 24), InfoId = I32(d, off + 28),
                    SuggestedGroup = I32(d, off + 32), NextQuest = I32(d, off + 36), Money = I32(d, off + 48), RewardSpell = I32(d, off + 68), StartItem = I32(d, off + 96),
                    Flags = I32(d, off + 100), FlagsEx = I32(d, off + 104), Races = BitConverter.ToUInt64(d, off + 440),
                    Title = t[0], Objectives = t[1], Description = t[2], AreaDescription = t[3], CompletionLog = t[8], Full = true,
                };
                if (q.Level < -1 || q.Level > 255 || q.MinLevel < 0 || q.MinLevel > 255) return null;
                var rw = new List<WdbItemCount>(); var pv = new List<WdbItemCount>(); var ch = new List<WdbItemCount>(); var rep = new List<int>();
                for (int i = 0; i < 4; i++)
                {
                    int at = off + 116 + 16 * i;
                    if (I32(d, at) > 0) rw.Add(new WdbItemCount { Id = I32(d, at), Count = Math.Max(1, I32(d, at + 4)) });
                    if (I32(d, at + 8) > 0) pv.Add(new WdbItemCount { Id = I32(d, at + 8), Count = Math.Max(1, I32(d, at + 12)) });
                }
                for (int i = 0; i < 6; i++)
                {
                    int at = off + 180 + 12 * i;
                    if (I32(d, at) > 0) ch.Add(new WdbItemCount { Id = I32(d, at), Count = Math.Max(1, I32(d, at + 4)) });
                }
                for (int i = 0; i < 5; i++) { int f = I32(d, off + 300 + 16 * i); if (f > 0 && !rep.Contains(f)) rep.Add(f); }
                if (rw.Count > 0) q.Rewards = rw.ToArray();
                if (pv.Count > 0) q.Provided = pv.ToArray();
                if (ch.Count > 0) q.Choices = ch.ToArray();
                if (rep.Count > 0) q.RepFactions = rep.ToArray();
                if (goals.Count > 0) q.Goals = goals.ToArray();
                return q;
            }
            catch (Exception) { return null; }
        }

        // the older rule (texts only): the numbers in front vary with the quest, so the text header is found by its own lengths: the one position where it
        // is followed by exactly that many text bytes up to the end of the record, and whose title reads as text.
        static readonly int[] QWidths = { 9, 12, 12, 9, 10, 8, 10, 8, 11 };

        public static WdbQuest DecodeQuestTexts(byte[] d, int off, int len, int id)
        {
            try
            {
                const int Fixed = 28, Header = 12;
                if (len < Fixed + Header + 1 || U32(d, off) != (uint)id) return null;
                var lens = new int[9];
                for (int k = 40; k + Header <= len; k++)
                {
                    var b = new WdbBits(d, off + k, Header);
                    long sum = 0;
                    for (int i = 0; i < 9; i++) { lens[i] = b.Read(QWidths[i]); sum += lens[i]; }
                    if (lens[0] == 0 || k + Header + sum != len) continue;
                    int p = off + k + Header;
                    if (!ReadsAsText(d, p, Math.Min(lens[0], 64))) continue;
                    var q = new WdbQuest { Id = id, Type = (int)U32(d, off + 4), Level = (int)U32(d, off + 8), MinLevel = (int)U32(d, off + 16), Sort = (int)U32(d, off + 24) };
                    q.Title = Utf8.GetString(d, p, lens[0]); p += lens[0];
                    q.Objectives = Utf8.GetString(d, p, lens[1]); p += lens[1];
                    q.Description = Utf8.GetString(d, p, lens[2]);
                    return q;
                }
                return null;
            }
            catch (Exception) { return null; }
        }

        static bool ReadsAsText(byte[] d, int at, int n)
        {
            for (int i = 0; i < n; i++) { byte c = d[at + i]; if (c < 32 && c != 9 && c != 10 && c != 13) return false; if (c == 127) return false; }
            return true;
        }
    }
}
