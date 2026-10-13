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
        public uint Flags0, Flags1;                                // npc flags / type flags, raw
        public int[] QuestItems = NoInts;                          // items the creature drops for quests
        public static readonly int[] NoInts = new int[0];
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
    }

    public sealed class WdbQuest
    {
        public int Id, Type, Level, MinLevel;
        public int Sort;                                           // > 0 the zone (AreaTable id) the quest is sorted under, < 0 a quest sort (class, profession ...)
        public string Title = "", Objectives = "", Description = "";
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

        // ---- creature: [15 byte bit header: title 11, title2 11, cursor 6, leader 1, then name / name2 lengths 11 + 11 for 4 names]
        //      [the names that are not empty, zero-terminated][flags u32 x2, u32, type u8, family u8, ..., rank u8 at +17][display count u32 at +26, total
        //      probability f32, (display id u32, scale f32, probability f32) x count][health f32, energy f32][quest item count u32][36 bytes][title strings][quest items u32 x count]
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
                if (q + 4 > end) return null;
                uint qc = U32(d, q);
                if (qc > 64) return null;
                int ts = q + 40;
                if (ts + tl + tl2 + cl + 4L * qc != end) return null;
                var c = new WdbCreature { Id = id, Name = name, Flags0 = U32(d, p), Flags1 = U32(d, p + 4), Type = d[p + 12], Family = d[p + 13], Rank = d[p + 17], DisplayId = dc > 0 ? (int)U32(d, p + 34) : 0 };
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

        // ---- quest: [id u32, type i32, level i32, ?, min level i32, ?, sort i32, ...numbers (rewards, objectives) ...][12 byte bit header: title 9, objectives 12,
        //      description 12, area 9, giver text 10, giver name 8, turn-in text 10, turn-in name 8, completion log 11][the nine texts, not zero-terminated]
        // The numbers in front vary with the quest (rewards, objective list), so the header is found by its own lengths: the one position where it
        // is followed by exactly that many text bytes up to the end of the record, and whose title reads as text.
        static readonly int[] QWidths = { 9, 12, 12, 9, 10, 8, 10, 8, 11 };

        public static WdbQuest DecodeQuest(byte[] d, int off, int len, int id)
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
