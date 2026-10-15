using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Db2;

namespace ElansAddonHub
{
    // --selftest "spelldata": Atlas layers A2 (spells, skills, recipes, reagents), A4 (factions) and the item details (armor, weapon damage, effects, sets).
    // Everything runs on tables built in the test (a small WDC5 writer with relationship data lives here), under the scratch folder. The real game files
    // are only touched by the last, optional check (read-only, skipped when there is no WoW install).
    public partial class MainWindow
    {
        // ---- a WDC5 writer for tables with plain fields (ints, floats, strings, arrays), an id list or an inline id, relationship data and a locked section
        sealed class SdF { public int Bits, Len = 1; public char Kind; }
        static SdF SdI(int len = 1, int elemBits = 32) => new SdF { Bits = elemBits * len, Len = len, Kind = 'i' };
        static SdF SdFl(int len = 1) => new SdF { Bits = 32 * len, Len = len, Kind = 'f' };
        static SdF SdS() => new SdF { Bits = 32, Kind = 's' };

        static byte[] SdBuild(uint table, uint layout, SdF[] fs, object[][] rows, int[] ids, int[] foreign = null, int locked = 0, int idIndex = 0, bool idList = true)
        {
            int bitsPer = fs.Sum(f => f.Bits), recSize = (bitsPer + 7) / 8;
            long totalRec = (long)(rows.Length + locked) * recSize;
            var strings = new MemoryStream(); strings.WriteByte(0);
            var recs = new List<byte[]>();
            for (int k = 0; k < rows.Length; k++)
            {
                var rec = new byte[recSize];
                long pos = 0;
                for (int c = 0; c < fs.Length; c++)
                {
                    var f = fs[c];
                    var v = c < rows[k].Length ? rows[k][c] : null;
                    int eb = f.Bits / f.Len;
                    for (int e = 0; e < f.Len; e++)
                    {
                        object x = f.Len > 1 ? (v is Array a && e < a.Length ? a.GetValue(e) : 0) : v ?? 0;
                        long bits;
                        if (f.Kind == 's')
                        {
                            long off = 0;
                            if (x is string s && s.Length > 0) { off = strings.Position; var b = Encoding.UTF8.GetBytes(s); strings.Write(b, 0, b.Length); strings.WriteByte(0); }
                            bits = totalRec + off - ((long)k * recSize + (pos + (long)e * eb) / 8);
                        }
                        else if (f.Kind == 'f') bits = (uint)BitConverter.ToInt32(BitConverter.GetBytes(Convert.ToSingle(x, CultureInfo.InvariantCulture)), 0);
                        else bits = Convert.ToInt64(x, CultureInfo.InvariantCulture);
                        PutBits(rec, pos + (long)e * eb, eb, bits);
                    }
                    pos += f.Bits;
                }
                recs.Add(rec);
            }
            var fields = new List<W5Field>(); long fo = 0;
            foreach (var f in fs) { fields.Add(new W5Field { OffsetBits = (int)fo, SizeBits = f.Bits, Storage = 0 }); fo += f.Bits; }

            var ms = new MemoryStream(); var w = new BinaryWriter(ms);
            var infos = new MemoryStream(); var ib = new BinaryWriter(infos);
            foreach (var f in fields) { ib.Write((ushort)f.OffsetBits); ib.Write((ushort)f.SizeBits); ib.Write(0); ib.Write(0); ib.Write(0u); ib.Write(0u); ib.Write(0u); }
            int secs = locked > 0 ? 2 : 1;
            int headLen = 204 + 40 * secs + 4 * fs.Length + (int)infos.Length;
            var body = new MemoryStream(); var bw = new BinaryWriter(body);
            foreach (var r in recs) bw.Write(r);
            bw.Write(strings.ToArray());
            int idBytes = 0;
            if (idList) { foreach (var id in ids) bw.Write(id); idBytes = ids.Length * 4; }
            int relBytes = 0;
            if (foreign != null)
            {
                bw.Write(foreign.Length); bw.Write(foreign.Min()); bw.Write(foreign.Max());
                for (int k = 0; k < foreign.Length; k++) { bw.Write(foreign[k]); bw.Write(k); }
                relBytes = 12 + 8 * foreign.Length;
            }
            int minId = ids.Length == 0 ? 0 : ids.Min(), maxId = ids.Length == 0 ? 0 : ids.Max();
            w.Write(0x35434457); w.Write(5);
            var schema = new byte[128]; var sb = Encoding.ASCII.GetBytes("wowstatic_selftest"); Array.Copy(sb, schema, sb.Length); w.Write(schema);
            w.Write(rows.Length + locked); w.Write(fs.Length); w.Write(recSize); w.Write((int)strings.Length); w.Write(table); w.Write(layout);
            w.Write(minId); w.Write(maxId); w.Write(0);
            w.Write((ushort)0); w.Write((ushort)idIndex);
            w.Write(fs.Length); w.Write(0); w.Write(0); w.Write((int)infos.Length); w.Write(0); w.Write(0); w.Write(secs);
            // section 0: key 0
            w.Write(0UL); w.Write(headLen); w.Write(rows.Length); w.Write((int)strings.Length); w.Write(0); w.Write(idBytes); w.Write(relBytes); w.Write(0); w.Write(0);
            if (locked > 0) { w.Write(0x1122334455667788UL); w.Write(headLen + (int)body.Length); w.Write(locked); w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0); }
            w.Write(new byte[4 * fs.Length]);
            w.Write(infos.ToArray());
            w.Write(body.ToArray());
            if (locked > 0) w.Write(new byte[locked * recSize]);
            return ms.ToArray();
        }

        // ---- the tables of the fixture, in the layouts of build 1.60.1.70338 (see SpellTables)
        static byte[] SdSpellName(uint layout = 0x782EE721) => SdBuild(SpellTables.SpellName.TableHash, layout, new[] { SdS() },
            new[]
            {
                new object[] { "Fireball" }, new object[] { "Rend" }, new object[] { "Elixir of Testing" }, new object[] { "Smelt Copper" }, new object[] { "Smelt Tin" }, new object[] { "Spiced Wolf Meat" },
                new object[] { "Test Equip Aura" }, new object[] { "Set Bonus Two" }, new object[] { "Set Bonus Three" }, new object[] { "Test Use Spell" }, new object[] { "Poison Cloud" },
            },
            new[] { 133, 772, 5001, 6001, 6002, 6003, 7000, 7001, 7002, 8000, 9000 }, locked: 3);

        static byte[] SdSpell(uint layout = 0xE3D134FB)
        {
            byte[] R(string sub, string desc, string aura) { var ms = new MemoryStream(); foreach (var s in new[] { sub, desc, aura }) { var b = Encoding.UTF8.GetBytes(s); ms.Write(b, 0, b.Length); ms.WriteByte(0); } return ms.ToArray(); }
            var sec = new W5Section
            {
                Records = new[]
                {
                    R("Rank 1", "Hurls a fiery ball that causes $s1 Fire damage and an additional $o2 Fire damage over $d.", "$s2 Fire damage every $t2 sec."),
                    R("Rank 1", "Wounds the target causing them to bleed for $o1 damage over $d. Ticks every $t1 sec. $133s1 and $@spellname133.", ""),
                    R("", "Makes a test elixir.", ""),
                    R("", "Smelts copper.", ""), R("", "Smelts tin.", ""), R("", "Cooks meat.", ""),
                    R("", "Increases your hit rating by $s1.", "Hit +$s1"), R("", "+$s1 Attack Power.", ""), R("", "Decreases the cooldown of Concussive Shot by $/1000;s1 sec.", ""),
                    R("", "Restores $s1 health.", ""), R("", "A cloud of poison: $s1 damage, $w1 more, ${$s1*2}, ${$s1/3}.1, $?s123[known][unknown], $g he:she;, $s2, $s3, $z.", ""),
                },
                Ids = new[] { 133, 772, 5001, 6001, 6002, 6003, 7000, 7001, 7002, 8000, 9000 },
            };
            return BuildWdc5(SpellTables.Spell.TableHash, layout, true, 0, new W5Field[0], new[] { sec });
        }

        // SpellMisc: 16 fields; rows: difficulty, cast idx, duration idx, range idx, icon
        static byte[] SdSpellMisc(uint layout = 0x434B3607)
        {
            var fs = new[] { SdI(17), SdI(), SdI(), SdI(), SdI(), SdI(), SdI(), SdFl(), SdFl(), SdI(), SdI(), SdI(), SdI(), SdI(), SdI(), SdI() };
            object[] M(int diff, int cast, int dur, int range, int icon) => new object[] { new int[17], diff, cast, dur, 0, range, 0, 0f, 0f, 0, icon, 0, 0, 0, 0, 0 };
            return SdBuild(SpellTables.SpellMisc.TableHash, layout, fs, new[] { M(0, 1, 2, 3, 135812), M(0, 0, 4, 0, 132155), M(0, 0, 0, 0, 136000), M(5, 9, 9, 9, 999999), M(0, 0, 0, 0, 133000), M(0, 0, 0, 0, 133001), M(0, 0, 0, 0, 133002) },
                new[] { 1, 2, 3, 4, 5, 6, 7 }, foreign: new[] { 133, 772, 5001, 133, 6001, 6002, 6003 }, locked: 2);
        }

        // SpellEffect: 29 fields; used here: 0 aura, 1 difficulty, 2 index, 3 effect, 6 period, 10 item type, 18 coefficient (float), 22 base points (float), 26 radius index[2]
        static byte[] SdSpellEffect(uint layout = 0x5362E3D4)
        {
            var fs = new List<SdF>();
            for (int i = 0; i < 29; i++) fs.Add(i == 18 || i == 22 ? SdFl() : i == 25 || i == 26 ? SdI(2) : i == 27 ? SdI(4) : i == 28 ? SdI(2, 16) : SdI());
            object[] E(int diff, int index, int effect, int aura, int period, int item, float points, float coef, int radiusIdx = 0)
            {
                var r = new object[29];
                for (int i = 0; i < 29; i++) r[i] = fs[i].Len > 1 ? (object)new int[fs[i].Len] : (fs[i].Kind == 'f' ? (object)0f : 0);
                r[0] = aura; r[1] = diff; r[2] = index; r[3] = effect; r[6] = period; r[10] = item; r[18] = coef; r[22] = points; r[26] = new[] { radiusIdx, 0 };
                return r;
            }
            var rows = new[]
            {
                E(0, 0, 2, 0, 0, 0, 18f, 0f), E(0, 1, 6, 3, 2000, 0, 1f, 0f), E(0, 0, 6, 3, 3000, 0, 5f, 0f), E(0, 0, 24, 0, 0, 1001, 0f, 0f),
                E(0, 0, 24, 0, 0, 2840, 0f, 0f), E(0, 0, 24, 0, 0, 3576, 0f, 0f), E(0, 0, 24, 0, 0, 5004, 0f, 0f),
                E(0, 0, 6, 0, 0, 0, 12f, 0f), E(0, 0, 6, 0, 0, 0, 20f, 0f), E(0, 0, 6, 0, 0, 0, 3000f, 0f), E(0, 0, 10, 0, 0, 0, 300f, 0f),
                E(0, 0, 2, 0, 0, 0, 40f, 0f, 5), E(0, 1, 6, 0, 0, 0, 0f, 0.5f), E(0, 0, 2, 0, 0, 0, 99f, 0f),      // poison cloud: s1 = 40 (radius index 5), s2 has a coefficient and no base amount, a repeated index 0 (the first wins)
                E(6, 0, 2, 0, 0, 0, 777f, 0f),                                                                      // another difficulty's row of 133: left out
            };
            return SdBuild(SpellTables.SpellEffect.TableHash, layout, fs.ToArray(), rows, Enumerable.Range(1, rows.Length).ToArray(),
                foreign: new[] { 133, 133, 772, 5001, 6001, 6002, 6003, 7000, 7001, 7002, 8000, 9000, 9000, 9000, 133 }, locked: 2);
        }

        static byte[] SdSimple(SpellTables.Def def, uint layout, SdF[] fs, object[][] rows, int[] ids) => SdBuild(def.TableHash, layout, fs, rows, ids);

        static byte[] SdReagents(uint layout = 0xFF40D202)
        {
            var fs = new[] { SdI(), SdI(8), SdI(8, 16), SdI(8, 16), SdI(8, 8) };
            object[] R(int spell, int[] items, int[] counts) => new object[] { spell, items, counts, new int[8], new int[8] };
            return SdBuild(SpellTables.SpellReagents.TableHash, layout, fs, new[]
            {
                R(5001, new[] { 2589, 3820, 0, 0, 0, 0, 0, 0 }, new[] { 2, 1, 0, 0, 0, 0, 0, 0 }),
                R(6001, new[] { 2770, 0, 0, 0, 0, 0, 0, 0 }, new[] { 1, 0, 0, 0, 0, 0, 0, 0 }),
                R(6002, new[] { 2771, 0, 0, 0, 0, 0, 0, 0 }, new[] { 1, 0, 0, 0, 0, 0, 0, 0 }),
                R(6003, new[] { 12208, 2589, 0, 0, 0, 0, 0, 0 }, new[] { 1, 3, 0, 0, 0, 0, 0, 0 }),
            }, new[] { 1, 2, 3, 4 });
        }

        static byte[] SdSkillLine(uint layout = 0x6763217C)
        {
            // DisplayName(0) AlternateVerb(1) Description(2) Horde(3) Neutral(4) ID(5) Category(6) Icon(7) CanLink(8) Parent(9) ParentTier(10) Flags(11) SpellBook(12) 2 more
            var fs = new[] { SdS(), SdS(), SdS(), SdS(), SdS(), SdI(), SdI(), SdI(), SdI(), SdI(), SdI(), SdI(), SdI(), SdI(), SdI() };
            object[] L(string name, int id, int cat, string desc = "") => new object[] { name, "", desc, "", "", id, cat, 136000 + id, 1, 0, 0, 0, 0, 0, 0 };
            return SdBuild(SpellTables.SkillLine.TableHash, layout, fs, new[]
            {
                L("Alchemy", 171, 11, "Mix potions."), L("Alchemy", 2937, 11), L("First Aid", 129, 9), L("Swords", 43, 6), L("Mining", 186, 11), L("Cooking", 185, 9),
            }, new int[0], idIndex: 5, idList: false);
        }

        static byte[] SdSkillAbility(uint layout = 0x224F7EA0)
        {
            // AbilityVerb(0) AbilityAllVerb(1) ID(2) SkillLine(3) Spell(4) MinRank(5) ClassMask(6) Supercedes(7) Acquire(8) TrivialHigh(9) TrivialLow(10) Flags(11) ... (18 fields)
            var fs = new List<SdF> { SdS(), SdS() };
            for (int i = 2; i < 16; i++) fs.Add(SdI());
            fs.Add(SdI(2)); fs.Add(SdI(2));
            object[] A(int id, int skill, int spell, int min, int green, int gray) { var r = new object[18]; r[0] = ""; r[1] = ""; r[2] = id; r[3] = skill; r[4] = spell; r[5] = min; r[9] = gray; r[10] = green; r[16] = new int[2]; r[17] = new int[2]; return r; }
            return SdBuild(SpellTables.SkillLineAbility.TableHash, layout, fs.ToArray(), new[]
            {
                A(1, 171, 5001, 25, 45, 65), A(2, 171, 772, 1, 1, 1), A(3, 43, 133, 1, 1, 1), A(4, 186, 6001, 1, 25, 45), A(5, 186, 6002, 65, 85, 105), A(6, 185, 6003, 10, 40, 60), A(7, 129, 9000, 1, 2, 3),
            }, new int[0], idIndex: 2, idList: false);
        }

        static byte[] SdFaction(uint layout = 0x6D443C38)
        {
            var fs = new List<SdF> { SdS(), SdS() };
            for (int i = 2; i < 11; i++) fs.Add(SdI());
            fs.Add(SdI(4)); fs.Add(SdI(4)); fs.Add(SdI(4)); fs.Add(SdI(4)); fs.Add(SdFl(2)); fs.Add(SdI(2)); fs.Add(SdI(2)); fs.Add(SdI(2)); fs.Add(SdI(2)); fs.Add(SdI(2));
            object[] F(string name, string desc, int idx, int parent)
            {
                var r = new object[fs.Count];
                for (int i = 0; i < fs.Count; i++) r[i] = fs[i].Len > 1 ? (object)new int[fs[i].Len] : 0;
                r[0] = name; r[1] = desc; r[2] = idx; r[3] = parent; r[15] = new[] { 0f, 0f };
                return r;
            }
            return SdBuild(SpellTables.Faction.TableHash, layout, fs.ToArray(), new[]
            {
                F("Stormwind", "The human capital.", 4, 469), F("Alliance", "", 0, 0), F("Orgrimmar", "The orc capital.", 9, 67), F("Horde", "", 0, 0), F("Argent Dawn", "", 12, 0), F("Human", "", 0, 0), F("Orc", "", 0, 0), F("Wildlife", "", 0, 0),
            }, new[] { 72, 469, 76, 67, 529, 1, 2, 99 });
        }

        static byte[] SdFactionTemplate(uint layout = 0x22B6DC22)
        {
            var fs = new[] { SdI(), SdI(), SdI(), SdI(), SdI(), SdI(8, 16), SdI(8, 16) };
            object[] T(int faction, int group, int friendGroup, int enemyGroup, int[] enemies = null, int[] friends = null) =>
                new object[] { faction, 0, group, friendGroup, enemyGroup, enemies ?? new int[8], friends ?? new int[8] };
            return SdBuild(SpellTables.FactionTemplate.TableHash, layout, fs, new[]
            {
                T(1, 3, 2, 12),                                     // 1 Human: a player of the Alliance; friends with the Alliance group, enemy of the Horde and monsters
                T(2, 5, 4, 10),                                     // 2 Orc
                T(72, 3, 2, 12),                                    // 11 Stormwind guards (as the client has them)
                T(76, 5, 4, 10),                                    // 85 Orgrimmar
                T(529, 0, 0, 0, null, new[] { 1, 2, 0, 0, 0, 0, 0, 0 }),   // 99: friendly to both races by faction id
                T(99, 8, 0, 3),                                     // 100: monsters hostile to both player groups
                T(0, 0, 0, 0),                                      // 101: nobody's
            }, new[] { 1, 2, 11, 85, 99, 100, 101 });
        }

        // armor and weapon damage tables (item level 20 only)
        static byte[] SdArmorTotal(uint layout = 0xEB155D51) => SdBuild(SpellTables.ArmorTotal.TableHash, layout, new[] { SdI(), SdFl(), SdFl(), SdFl(), SdFl() }, new[] { new object[] { 20, 40f, 60f, 120f, 200f } }, new[] { 20 });
        static byte[] SdArmorQuality(uint layout = 0x2935AA9D) => SdBuild(SpellTables.ArmorQuality.TableHash, layout, new[] { SdFl(7) }, new[] { new object[] { new[] { 0.5f, 1.0f, 1.1f, 1.2f, 1.3f, 1.4f, 1.5f } } }, new[] { 20 });
        static byte[] SdArmorShield(uint layout = 0x7E6C94F9) => SdBuild(SpellTables.ArmorShield.TableHash, layout, new[] { SdFl(7), SdI() }, new[] { new object[] { new[] { 0f, 10f, 20f, 30f, 40f, 50f, 60f }, 20 } }, new[] { 20 });
        static byte[] SdArmorLocation(uint layout = 0xFB67352F) => SdBuild(SpellTables.ArmorLocation.TableHash, layout, new[] { SdFl(), SdFl(), SdFl(), SdFl(), SdFl() },
            new[] { new object[] { 1f, 1f, 1f, 1f, 0f }, new object[] { 0.8f, 0.8f, 0.8f, 0.8f, 0f }, new object[] { 1f, 0f, 0f, 0f, 0f } }, new[] { 5, 1, 16 });
        static byte[] SdDamage(SpellTables.Def def, uint layout, float[] perQuality) => SdBuild(def.TableHash, layout, new[] { SdI(), SdFl(7) }, new[] { new object[] { 20, perQuality } }, new[] { 20 });

        static byte[] SdItemEffect(uint layout = 0x4CA77678)
        {
            var fs = new[] { SdI(), SdI(), SdI(), SdI(), SdI(), SdI(), SdI(), SdI(), SdI() };
            object[] E(int trigger, int spell) => new object[] { 0, trigger, 0, 0, 0, 0, spell, 0, 0 };
            return SdBuild(SpellTables.ItemEffect.TableHash, layout, fs, new[] { E(1, 7000), E(0, 8000), E(2, 9000), E(3, 8000), E(1, 0) }, new[] { 11, 12, 13, 14, 15 }, locked: 1);
        }

        static byte[] SdItemXItemEffect(uint layout = 0x96F083AD) =>
            SdBuild(SpellTables.ItemXItemEffect.TableHash, layout, new[] { SdI() }, new[] { new object[] { 11 }, new object[] { 12 }, new object[] { 13 }, new object[] { 14 }, new object[] { 15 } }, new[] { 1, 2, 3, 4, 5 }, foreign: new[] { 1001, 1001, 1002, 1003, 1004 });

        static byte[] SdItemSet(uint layout = 0xF79068A4)
        {
            var fs = new[] { SdS(), SdI(), SdI(), SdI(), SdI(17) };
            object[] S(string name, params int[] items) { var a = new int[17]; Array.Copy(items, a, items.Length); return new object[] { name, 0, 0, 0, a }; }
            return SdBuild(SpellTables.ItemSet.TableHash, layout, fs, new[] { S("Test Battlegear", 1001, 1002, 1003), S("Empty Set") }, new[] { 700, 701 });
        }

        static byte[] SdItemSetSpell(uint layout = 0x2666A73F) =>
            SdBuild(SpellTables.ItemSetSpell.TableHash, layout, new[] { SdI(), SdI(), SdI(), SdI() },
                new[] { new object[] { 0, 7001, 0, 2 }, new object[] { 0, 7002, 0, 3 | 0x140000 }, new object[] { 0, 7001, 0, 2 } },      // thresholds carry junk above the low byte, like the real table
                new[] { 1, 2, 3 }, foreign: new[] { 700, 700, 999 });

        // ---- the lock / quest lookup tables (WorldLookups) in the layouts of build 1.60.1.70374
        static byte[] SdLock(uint layout = 0xDA418152) => SdBuild(WorldTables.Lock.TableHash, layout, new[] { SdI(), SdI(8), SdI(8, 16), SdI(8, 8), SdI(8, 8) },
            new[]
            {
                new object[] { 0, new[] { 2 }, new[] { 70 }, new[] { 2 }, new int[0] },                       // 31: Briarthorn, Herbalism 70
                new object[] { 0, new[] { 3, 872 }, new[] { 65, 0 }, new[] { 2, 1 }, new int[0] },              // 39: Tin Vein, Mining 65 (and an item entry)
                new object[] { 1, new[] { 1, 5, 6 }, new[] { 25, 0, 0 }, new[] { 2, 2, 2 }, new[] { 1 } },      // 2: Lockpicking 25, then Open / Close without a level
                new object[] { 0, new[] { 0, 5, 6 }, new[] { 0, 0, 0 }, new[] { 0, 2, 2 }, new int[0] },        // 57: a plain chest, no skill
            }, new[] { 31, 39, 2, 57 });
        static byte[] SdLockType(uint layout = 0xAF114F71) => SdBuild(WorldTables.LockType.TableHash, layout, new[] { SdS(), SdS(), SdS(), SdS(), SdI(), SdI() },
            new[] { new object[] { "Pick Lock", "Locked Items", "Pick", "", 0, 0 }, new object[] { "Herbalism", "Herbs", "Gather", "", 1, 0 }, new object[] { "Mining", "Ore Deposits", "Mine", "", 2, 0 }, new object[] { "Open", "Unlocked Items", "Open", "", 4, 0 } }, new[] { 1, 2, 3, 5 });
        static byte[] SdQuestSort(uint layout = 0x9B92BE63) => SdBuild(WorldTables.QuestSort.TableHash, layout, new[] { SdS(), SdI(1, 8), SdI() },
            new[] { new object[] { "Cooking", 0, 0 }, new object[] { "Paladin", 0, 0 }, new object[] { "The High Order", 0, 0 } }, new[] { 304, 141, 660 });
        static byte[] SdQuestInfo(uint layout = 0xE505C927) => SdBuild(WorldTables.QuestInfo.TableHash, layout, new[] { SdS(), SdI(1, 8), SdI(), SdI(1, 16) },
            new[] { new object[] { "Elite", 0, 2, 0 }, new object[] { "Dungeon", 0, 0, 0 } }, new[] { 1, 81 });
        static byte[] SdQuestLine(uint layout = 0x27C5AD69) => SdBuild(WorldTables.QuestLine.TableHash, layout, new[] { SdS(), SdS(), SdI(), SdI(), SdI(), SdI(), SdI() },
            new[] { new object[] { "Toxic Soil", "", 0, 0, 0, 0, 0 } }, new[] { 6026 });
        static byte[] SdQuestLineX(uint layout = 0x8552F722) => SdBuild(WorldTables.QuestLineXQuest.TableHash, layout, new[] { SdI(), SdI(), SdI(), SdI(), SdI() },
            new[] { new object[] { 6026, 92744, 1, 0, 0 }, new object[] { 6026, 92742, 0, 0, 0 }, new object[] { 9999, 1, 0, 0, 0 } }, new[] { 49381, 49380, 49999 });
        static byte[] SdQuestV2(uint layout = 0x1854BDB9) => SdBuild(WorldTables.QuestV2.TableHash, layout, new[] { SdI(), SdI(), SdI() },
            new[] { new object[] { 25, 0, 0 }, new object[] { 498, 0, 0 }, new object[] { 92742, 0, 0 } }, new[] { 25, 498, 92742 }, idIndex: 0, idList: false);

        static SpellRaw SdRaw(Action<SpellRaw> tweak = null)
        {
            var raw = new SpellRaw();
            raw.Tables[SpellTables.SpellName.Name] = SdSpellName();
            raw.Tables[SpellTables.Spell.Name] = SdSpell();
            raw.Tables[SpellTables.SpellMisc.Name] = SdSpellMisc();
            raw.Tables[SpellTables.SpellEffect.Name] = SdSpellEffect();
            raw.Tables[SpellTables.SpellReagents.Name] = SdReagents();
            raw.Tables[SpellTables.SpellCastTimes.Name] = SdSimple(SpellTables.SpellCastTimes, 0x75B6BD3A, new[] { SdI(), SdI() }, new[] { new object[] { 1500, 1500 } }, new[] { 1 });
            raw.Tables[SpellTables.SpellDuration.Name] = SdSimple(SpellTables.SpellDuration, 0xA931BD2B, new[] { SdI(), SdI(), SdI() }, new[] { new object[] { 4000, 4000, 0 }, new object[] { 9000, 9000, 0 } }, new[] { 2, 4 });
            raw.Tables[SpellTables.SpellRange.Name] = SdSimple(SpellTables.SpellRange, 0xADA13705, new[] { SdS(), SdS(), SdI(), SdFl(2), SdFl(2) }, new[] { new object[] { "", "", 0, new[] { 0f, 0f }, new[] { 35f, 40f } } }, new[] { 3 });
            raw.Tables[SpellTables.SpellRadius.Name] = SdSimple(SpellTables.SpellRadius, 0xF9A913EE, new[] { SdFl(), SdFl(), SdFl(), SdFl() }, new[] { new object[] { 8f, 0f, 0f, 8f } }, new[] { 5 });
            raw.Tables[SpellTables.SkillLine.Name] = SdSkillLine();
            raw.Tables[SpellTables.SkillLineAbility.Name] = SdSkillAbility();
            raw.Tables[SpellTables.Faction.Name] = SdFaction();
            raw.Tables[SpellTables.FactionTemplate.Name] = SdFactionTemplate();
            raw.Tables[SpellTables.ArmorTotal.Name] = SdArmorTotal();
            raw.Tables[SpellTables.ArmorQuality.Name] = SdArmorQuality();
            raw.Tables[SpellTables.ArmorShield.Name] = SdArmorShield();
            raw.Tables[SpellTables.ArmorLocation.Name] = SdArmorLocation();
            raw.Tables[SpellTables.DmgOneHand.Name] = SdDamage(SpellTables.DmgOneHand, 0x56F30531, new[] { 1f, 2f, 3f, 10.4f, 5f, 6f, 7f });
            raw.Tables[SpellTables.DmgOneHandCaster.Name] = SdDamage(SpellTables.DmgOneHandCaster, 0x56F30531, new[] { 1f, 2f, 3f, 6.5f, 5f, 6f, 7f });
            raw.Tables[SpellTables.DmgTwoHand.Name] = SdDamage(SpellTables.DmgTwoHand, 0x56F30531, new[] { 1f, 2f, 3f, 14f, 5f, 6f, 7f });
            raw.Tables[SpellTables.DmgAmmo.Name] = SdDamage(SpellTables.DmgAmmo, 0x56F30531, new[] { 0f, 7.5f, 0f, 0f, 0f, 0f, 0f });
            raw.Tables[SpellTables.DmgRanged.Name] = SdDamage(SpellTables.DmgRanged, 0x3CD722C1, new[] { 0f, 0f, 0f, 9f, 0f, 0f, 0f });
            raw.Tables[SpellTables.ItemEffect.Name] = SdItemEffect();
            raw.Tables[SpellTables.ItemXItemEffect.Name] = SdItemXItemEffect();
            raw.Tables[SpellTables.ItemSet.Name] = SdItemSet();
            raw.Tables[SpellTables.ItemSetSpell.Name] = SdItemSetSpell();
            raw.Tables[WorldTables.Lock.Name] = SdLock();
            raw.Tables[WorldTables.LockType.Name] = SdLockType();
            raw.Tables[WorldTables.QuestSort.Name] = SdQuestSort();
            raw.Tables[WorldTables.QuestInfo.Name] = SdQuestInfo();
            raw.Tables[WorldTables.QuestLine.Name] = SdQuestLine();
            raw.Tables[WorldTables.QuestLineXQuest.Name] = SdQuestLineX();
            raw.Tables[WorldTables.QuestV2.Name] = SdQuestV2();
            tweak?.Invoke(raw);
            return raw;
        }

        static string SdFlat(IEnumerable<TipLine> lines) => string.Join(" | ", lines.Select(l => l.ToString()));

        async Task<string> SpellDataTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            void Check(string what, bool cond, string detail = null) { SelfTestClock.Mark(what); notes.Add((cond ? "ok   " : "FAIL ") + what + (!cond && detail != null ? " [" + detail + "]" : "")); ok &= cond; }
            string P(string n) => Path.Combine(dir, n);
            var work = Path.Combine(dir, "spelldata");
            var savedCache = SpellData.CacheFile; var savedItemCache = ItemData.CacheFile; var savedEnabled = ItemData.Enabled;
            try
            {
                if (Directory.Exists(work)) Util.DeleteDir(work);
                Directory.CreateDirectory(work);
                Check("fixture: everything of this test is under the scratch folder (never the real game)", Path.GetFullPath(work).StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase));
                SpellData.ResetForTest(); ItemData.ResetForTest();
                SpellData.CacheFile = Path.Combine(work, "cache", "spelldata.bin");

                // ---- the table reader: relationship data, arrays without compression, the joined string space
                var tn = Wdc5.Parse(SdSpellName());
                Check("wdc5: names through the string block when a locked section follows (the client counts the locked rows before the strings): 11 rows, 3 locked rows skipped",
                    tn.Count == 11 && tn.EncryptedRecords == 3 && tn.String(0, 0) == "Fireball" && tn.String(1, 0) == "Rend" && tn.String(10, 0) == "Poison Cloud", tn.String(0, 0) + " " + tn.String(10, 0));
                var tm = Wdc5.Parse(SdSpellMisc());
                Check("wdc5: relationship data = the spell of each SpellMisc row (133, 772, 5001, 133 ...); a table without it gives 0", tm.Foreign(0) == 133 && tm.Foreign(1) == 772 && tm.Foreign(3) == 133 && tm.Foreign(6) == 6003 && tn.Foreign(0) == 0);
                var ta = Wdc5.Parse(SdArmorQuality());
                Check("wdc5: an uncompressed array field (7 floats in one field entry) is cut into its elements", Math.Abs(Wdc5.AsFloat(ta.FieldArr(0, 0, 3, 7)) - 1.2f) < 0.0001 && Math.Abs(Wdc5.AsFloat(ta.FieldArr(0, 0, 6, 7)) - 1.5f) < 0.0001);

                // ---- all tables
                var set = SpellParse.All(SdRaw());
                Check("parse: spells, factions and item details all available; the only problems are the three damage tables this fixture leaves out (two-hand caster, thrown, wand)", set.Spells != null && set.Factions != null && set.Details != null
                    && set.Problems.Keys.OrderBy(k => k, StringComparer.Ordinal).SequenceEqual(new[] { "ItemDamageThrown", "ItemDamageTwoHandCaster", "ItemDamageWand" }), string.Join("; ", set.Problems.Select(p => p.Key + "=" + p.Value)));
                var wl = set.World;
                Check("world lookups: Lock 31 = Herbalism 70, 39 = Mining 65 (the item entry of the same lock is not a skill), 2 = Lockpicking 25 (Open / Close without a level are skipped), 57 = no skill, unknown lock = none",
                    wl != null && wl.SkillNeed(31)?.LockType == 2 && wl.SkillNeed(31)?.Skill == 70 && wl.SkillNeed(39)?.LockType == 3 && wl.SkillNeed(39)?.Skill == 65 && wl.SkillNeed(2)?.LockType == 1 && wl.SkillNeed(2)?.Skill == 25
                    && wl.SkillNeed(57) == null && wl.SkillNeed(12345) == null && wl.LockTypeName(2) == "Herbalism" && wl.LockTypeName(3) == "Mining", wl == null ? "no world" : string.Join(",", wl.Locks.Keys));
                Check("world lookups: quest sorts by their negative sort (-304 Cooking, -141 Paladin, -660 The High Order), quest kinds (81 Dungeon, 1 Elite), a positive sort or 0 has no name",
                    wl.SortName(-304) == "Cooking" && wl.SortName(-141) == "Paladin" && wl.SortName(-660) == "The High Order" && wl.SortName(304) == null && wl.InfoName(81) == "Dungeon" && wl.InfoName(1) == "Elite" && wl.InfoName(0) == null && wl.InfoName(7) == null);
                Check("world lookups: the quest line 'Toxic Soil' lists its quests in their order (92742 then 92744); a row of an unknown line is ignored; the client's quest list (inline ids)",
                    wl.LineOf(92744)?.Name == "Toxic Soil" && wl.LineOf(92744).Quests.SequenceEqual(new[] { 92742, 92744 }) && wl.LineOf(1) == null && wl.QuestIds.SetEquals(new[] { 25, 498, 92742 }), wl.QuestIds.Count + "");
                var unkLock = SpellParse.All(SdRaw(r => r.Tables[WorldTables.Lock.Name] = SdLock(0x12345678)));
                Check("an unknown Lock layout: no lock needs (nothing guessed), the quest names still there, spells unaffected", unkLock.World != null && unkLock.World.Locks.Count == 0 && unkLock.World.SortName(-304) == "Cooking" && unkLock.Problems.ContainsKey("Lock") && unkLock.Spells != null);
                var noWorld = SpellParse.All(SdRaw(r => { foreach (var d0 in WorldTables.All) r.Tables.Remove(d0.Name); }));
                Check("none of the lock / quest tables: World is null with a reason, the rest unaffected", noWorld.World == null && noWorld.Problems.ContainsKey("world") && noWorld.Spells != null && noWorld.Factions != null);
                Check("parse: 11 spells (the locked names are not there), key-locked rows noted", set.Spells.SpellCount == 11 && set.Notes.Any(n => n.StartsWith("SpellName") && n.Contains("key-locked")), set.Spells.SpellCount + "");
                var fb = set.Spells.Get(133);
                Check("spell 133: name, rank text, icon, cast 1.5 s, duration 4 s, range 35 yards (SpellMisc row of the default difficulty; the hard-difficulty row is ignored)", fb.Name == "Fireball" && fb.Rank == "Rank 1" && fb.IconId == 135812 && fb.CastMs == 1500 && fb.DurationMs == 4000 && Math.Abs(fb.RangeMax - 35f) < 0.01, fb.IconId + " " + fb.CastMs + " " + fb.DurationMs + " " + fb.RangeMax);
                Check("spell 133: two effects (direct 18, then 1 every 2 s); the other difficulty's effect (777) is not used", fb.Effects.Length == 2 && fb.Effects[0].Points == 18f && fb.Effects[1].Aura == 3 && fb.Effects[1].PeriodMs == 2000, fb.Effects.Length + "");
                SpellData.SetForTest(set);
                var fv = SpellData.Spell(133);
                Check("description: $s1 / $o2 / $d filled in: 'Hurls a fiery ball that causes 18 Fire damage and an additional 2 Fire damage over 4 sec.'", fv.Description == "Hurls a fiery ball that causes 18 Fire damage and an additional 2 Fire damage over 4 sec.", fv.Description);
                Check("aura description: $s2 and $t2 (period 2 s): '1 Fire damage every 2 sec.'", fv.AuraDescription == "1 Fire damage every 2 sec.", fv.AuraDescription);
                Check("description: $o1 (5 every 3 s over 9 s = 15), $t1, another spell's value ($133s1 = 18) and its name ($@spellname133)", SpellData.Spell(772).Description == "Wounds the target causing them to bleed for 15 damage over 9 sec. Ticks every 3 sec. 18 and Fireball.", SpellData.Spell(772).Description);
                var cloud = SpellData.Spell(9000).Description;
                Check("description: $s1 = 40; an unknown token stays as written ($w1); ${$s1*2} = 80; ${$s1/3}.1 = 13.3; $?s123[a][b] shows the else branch; $g takes the first form; a scaled amount ($s2) and a missing effect ($s3) stay tokens; $z is readable",
                    cloud == "A cloud of poison: 40 damage, $w1 more, 80, 13.3, unknown, he, $s2, $s3, your home location.", cloud);
                Check("description: $/1000;s1 divides the amount (3000 -> 3)", SpellData.Spell(7002).Description == "Decreases the cooldown of Concussive Shot by 3 sec.", SpellData.Spell(7002).Description);
                Check("spell(id) of an unknown id is null", SpellData.Spell(424242) == null);

                // ---- skills, abilities, recipes
                var sk = set.Spells.Skills;
                Check("skills: 6 (Alchemy twice, First Aid, Swords, Mining, Cooking) with category and icon; SkillByName prefers the profession that has abilities", sk.Count == 6 && sk[171].Category == 11 && sk[129].Category == 9 && sk[43].Category == 6 && SpellData.SkillByName("alchemy").Id == 171 && SpellData.SkillByName("Swords").Id == 43 && SpellData.SkillByName("Nope") == null);
                var ab = SpellData.SpellsOfSkill(171);
                Check("SpellsOfSkill(Alchemy): its two abilities, lowest rank first (Rend at 1, the elixir at 25)", ab.Count == 2 && ab[0].Spell == 772 && ab[0].MinRank == 1 && ab[1].Spell == 5001 && ab[1].MinRank == 25 && ab[1].Green == 45 && ab[1].Gray == 65, string.Join(",", ab.Select(a => a.Spell)));
                var rp = SpellData.RecipesProducing(1001);
                Check("RecipesProducing(1001): the elixir spell 5001 of Alchemy at rank 25, using 2 x item 2589 and 1 x item 3820", rp.Count == 1 && rp[0].SpellId == 5001 && rp[0].SkillId == 171 && rp[0].MinRank == 25 && rp[0].Reagents.SequenceEqual(new[] { 2589, 2, 3820, 1 }), rp.Count == 0 ? "none" : string.Join(",", rp[0].Reagents));
                Check("recipes: 4 in all (the elixir, copper, tin, wolf meat); Rend (no item, no reagent) and First Aid's spell 9000 (the same) are not recipes", set.Spells.RecipeCount == 4, set.Spells.RecipeCount + "");
                Check("ReagentFor(2589): the elixir and the wolf meat; ReagentFor(12208) the wolf meat; nothing for an unused item", SpellData.ReagentFor(2589).Select(r => r.SpellId).OrderBy(x => x).SequenceEqual(new[] { 5001, 6003 }) && SpellData.ReagentFor(12208).Count == 1 && SpellData.ReagentFor(1).Count == 0);
                Check("RecipesOfSkill(Mining): copper (rank 1) then tin (rank 65)", SpellData.RecipesOfSkill(186).Select(r => r.SpellId + "@" + r.MinRank).SequenceEqual(new[] { "6001@1", "6002@65" }));
                Check("ids of the layers: item:123, spell:6150, skill:186, faction:76; parse and refuse", AtlasIds.Item(123) == "item:123" && AtlasIds.Spell(6150) == "spell:6150" && AtlasIds.Skill(186) == "skill:186" && AtlasIds.Faction(76) == "faction:76"
                    && AtlasIds.TryParse("spell:6150", out var kind, out var kid) && kind == "spell" && kid == 6150 && !AtlasIds.TryParse("spell:", out _, out _) && !AtlasIds.TryParse("potion:5", out _, out _) && !AtlasIds.TryParse("item:-3", out _, out _));

                // ---- the description language on its own
                var self = new SpellInfo
                {
                    Id = 1, Name = "T", DurationMs = 3600000,
                    Effects = new[]
                    {
                        new EffectInfo { Index = 0, Points = -12.4f, PeriodMs = 1500, Radius = 8f, Chain = 3 }, new EffectInfo { Index = 1, Points = 0f, Coef = 0.3f }, new EffectInfo { Index = 2, Points = 1f, Misc = -7 },
                    },
                };
                string X(string t) => SpellText.Expand(t, self, id => id == 2 ? new SpellInfo { Id = 2, Name = "Other", DurationMs = 90000, Desc = "Restores $s1 health over $d.", Aura = "Eating.", Effects = new[] { new EffectInfo { Index = 0, Points = 7f } } }
                    : id == 3 ? new SpellInfo { Id = 3, Name = "Loop", Desc = "x $@spelldesc3" } : null);
                Check("tokens: $s1 uses the absolute amount (12), $m1 / $M1 the same, $t1 = 1.5, $a1 = 8, $x1 = 3, $d = 1 hour, $q3 = 7", X("$s1 $m1 $M1 $t1 $a1 $x1 $d $q3") == "12 12 12 1.5 8 3 1 hour 7", X("$s1 $m1 $M1 $t1 $a1 $x1 $d $q3"));
                Check("tokens: $o1 = 12 x 3600 s / 1.5 s = 29760; a plain $ and $$ stay readable", X("$o1") == "29760" && X("costs 5$ or $$") == "costs 5$ or $", X("$o1") + " / " + X("costs 5$ or $$"));
                Check("tokens: $l picks by the number just written ($s3 = 1 -> 'point', $s1 = 12 -> 'points')", X("$s3 combo $lpoint:points;") == "1 combo point" && X("$s1 combo $lpoint:points;") == "12 combo points", X("$s3 combo $lpoint:points;") + " / " + X("$s1 combo $lpoint:points;"));
                Check("tokens: another spell's values ($2s1, $2d) and its name; an unknown spell leaves the token", X("$2s1 $2d $@spellname2 $9s1 $@spellname9") == "7 90 sec Other $9s1 $@spellname9", X("$2s1 $2d $@spellname2 $9s1 $@spellname9"));
                Check("tokens: $@spelldesc2 / $@spelltooltip2 = spell 2's own description with ITS values, $@spellaura2 its aura text; an unknown one is left out (no double space); a self-reference stops",
                    X("Use: $@spelldesc2 Then $@spellaura2") == "Use: Restores 7 health over 90 sec. Then Eating." && X("$@spelltooltip2") == "Restores 7 health over 90 sec."
                    && X("Use: $@spelldesc9 If you eat.") == "Use: If you eat." && X("$@spelldesc3").StartsWith("x x", StringComparison.Ordinal),
                    X("Use: $@spelldesc2 Then $@spellaura2") + " | " + X("Use: $@spelldesc9 If you eat.") + " | " + X("$@spelldesc3"));
                Check("tokens: nothing known = the text as it was (scaled amount, missing effect, unknown letters)", X("$s2 $s5 $w1 $<mult> $b1") == "$s2 $s5 $w1 $<mult> $b1", X("$s2 $s5 $w1 $<mult> $b1"));
                Check("arithmetic: ${$s1*2+1} = 25, ${($s1+8)/4} = 5, ${$s1/0} stays, ${$s1/7}.2 = 1.71, ${ 2 +} stays", X("${$s1*2+1}") == "25" && X("${($s1+8)/4}") == "5" && X("${$s1/0}") == "${$s1/0}" && X("${$s1/7}.2") == "1.71" && X("${ 2 +}") == "${ 2 +}", X("${$s1*2+1}") + " " + X("${($s1+8)/4}") + " " + X("${$s1/7}.2"));
                Check("durations: 45 sec, 2 min, 90 sec (not a whole minute), 2 hours, 1 day, until cancelled", SpellText.Duration(45000) == "45 sec" && SpellText.Duration(120000) == "2 min" && SpellText.Duration(90000) == "90 sec" && SpellText.Duration(7200000) == "2 hours" && SpellText.Duration(86400000) == "1 day" && SpellText.Duration(-1) == "until cancelled");
                Check("malformed text never throws and comes back as it was ('$', '${', '$?s1[', '$l a:b', '$/;s1', '$/0;s1')", new[] { "$", "${", "$?s1[", "$l a:b", "${$s1", "$/;s1", "$/0;s1" }.All(s => { try { return X(s) != null; } catch { return false; } }));
                var deep = "$?a1[" + string.Concat(Enumerable.Repeat("$?a1[", 20)) + "x" + string.Concat(Enumerable.Repeat("][y]", 20)) + "][z]";
                Check("deeply nested conditionals end (depth limit), no hang", X(deep) != null);

                // ---- factions
                var fa = set.Factions;
                Check("factions: 8 factions and 7 templates; names; parent ('Stormwind' under 'Alliance')", fa.Factions.Count == 8 && fa.Templates.Count == 7 && fa.Faction(72).Name == "Stormwind" && fa.Faction(72).ParentId == 469 && fa.Faction(72).Description == "The human capital." && fa.Faction(1000) == null);
                Check("factions: the player templates found are Human (1) and Orc (2)", fa.AllianceTemplate == 1 && fa.HordeTemplate == 2, fa.AllianceTemplate + "/" + fa.HordeTemplate);
                Check("A4: template 11 (Stormwind) is friendly to the Alliance and hostile to the Horde; 85 (Orgrimmar) the other way round; both through their friend / enemy groups",
                    fa.ToAlliance(11) == Standing.Friendly && fa.ToHorde(11) == Standing.Hostile && fa.ToAlliance(85) == Standing.Hostile && fa.ToHorde(85) == Standing.Friendly);
                Check("A4: template 99 lists the player factions among its friends = friendly to both; 100 (monsters) is hostile to both; 101 is neutral to both", fa.ToAlliance(99) == Standing.Friendly && fa.ToHorde(99) == Standing.Friendly && fa.ToAlliance(100) == Standing.Hostile && fa.ToHorde(100) == Standing.Hostile && fa.ToAlliance(101) == Standing.Neutral && fa.ToHorde(101) == Standing.Neutral);
                Check("FactionData: NameOfTemplate(11) = Stormwind, group Alliance; describes the standing; unknown ids give null / neutral",
                    FactionData.NameOfTemplate(11) == "Stormwind" && FactionData.GroupOfTemplate(11) == "Alliance" && FactionData.Describe(11) == "Stormwind (friendly to the Alliance, hostile to the Horde)" && FactionData.Describe(101) == null
                    && FactionData.NameOfTemplate(5555) == null && FactionData.ToAlliance(5555) == Standing.Neutral && FactionData.Describe(99) == "Argent Dawn (friendly to the Alliance, friendly to the Horde)", FactionData.Describe(11) + " / " + FactionData.Describe(99));

                // ---- item details
                var d = set.Details;
                Check("details: armor, 5 damage tables of 8 (two-hand caster, thrown and wand are missing in this fixture), 3 items with effects, 2 sets... (the empty one is not kept: 1)", d.HasArmor && d.Damage.Count(x => x != null) == 5 && d.Damage[ItemDetails.KindTwoHandCaster] == null && d.EffectCount == 3 && d.SetCount == 1, d.Damage.Count(x => x != null) + " " + d.EffectCount + " " + d.SetCount);
                ClientItem It(int id, string name, int q, int il, int inv, int cls, int sub, int delay = 0, float var = 0, int flags2 = 0) =>
                    new ClientItem { Id = id, Name = name, Quality = q, ItemLevel = il, InventoryType = inv, ClassId = cls, SubclassId = sub, DelayMs = delay, DmgVariance = var, Flags2 = flags2, Stats = ClientItem.NoStats };
                ItemDetails.Current = d;
                Check("armor: rare leather chest, level 20 = 1.2 x 60 x 1 = 72; leather head x 0.8 = 58; uncommon cloth robe counts as chest = 44; shield (rare) = 30", It(1, "c", 3, 20, 5, 4, 2).Armor == 72 && It(2, "h", 3, 20, 1, 4, 2).Armor == 58 && It(3, "r", 2, 20, 20, 4, 1).Armor == 44 && It(4, "s", 3, 20, 14, 4, 6).Armor == 30,
                    It(1, "c", 3, 20, 5, 4, 2).Armor + " " + It(2, "h", 3, 20, 1, 4, 2).Armor + " " + It(3, "r", 2, 20, 20, 4, 1).Armor + " " + It(4, "s", 3, 20, 14, 4, 6).Armor);
                Check("armor: a slot without an ArmorLocation row (a ring), an item level the table lacks, a weapon, a quality above 6: 0 (nothing guessed)", It(5, "ring", 3, 20, 11, 4, 0).Armor == 0 && It(6, "x", 3, 21, 5, 4, 2).Armor == 0 && It(7, "w", 3, 20, 13, 2, 7).Armor == 0 && It(8, "q", 7, 20, 5, 4, 2).Armor == 0);
                Check("armor: the cloak (back) uses the cloth column only: cloth cloak = 1.1 x 40 x 1 = 44, leather cloak = 0", It(9, "cl", 2, 20, 16, 4, 1).Armor == 44 && It(10, "cl2", 2, 20, 16, 4, 2).Armor == 0);
                int mn, mx; double dps;
                bool got = It(11, "sword", 3, 20, 13, 2, 7, 2600, 0.5f).TryDamage(out mn, out mx, out dps);
                Check("damage: one-hand sword, level 20, rare, speed 2.6, spread 0.5: 10.4 dps x 2.6 = 27.04 -> 20 - 34 (10.4 dps)", got && mn == 20 && mx == 34 && Math.Abs(dps - 10.4) < 0.01, mn + "-" + mx + " " + dps);
                got = It(12, "dagger", 3, 20, 13, 2, 15, 1800, 0.4f, 0x200).TryDamage(out mn, out mx, out dps);
                Check("damage: a caster weapon (flag 0x200) uses the caster table: 6.5 x 1.8 = 11.7 -> 9 - 14", got && mn == 9 && mx == 14, mn + "-" + mx);
                got = It(13, "staff", 3, 20, 17, 2, 10, 3500, 0.3f).TryDamage(out mn, out mx, out dps);
                Check("damage: two-hand, 14 x 3.5 = 49 -> 42 - 56", got && mn == 42 && mx == 56, mn + "-" + mx);
                Check("damage: a table that is unavailable (two-hand caster, wand) = no damage line, not a made-up one", !It(14, "cs", 3, 20, 17, 2, 10, 3500, 0.3f, 0x200).TryDamage(out _, out _, out _) && !It(15, "wand", 3, 20, 26, 2, 19, 1500, 0.3f).TryDamage(out _, out _, out _));
                got = It(16, "bow", 3, 20, 15, 2, 2, 2400, 0.5f).TryDamage(out mn, out mx, out dps);
                Check("damage: a bow uses the ranged table (a second layout, 3CD722C1): 9 x 2.4 = 21.6 -> 16 - 27", got && mn == 16 && mx == 27, mn + "-" + mx);
                got = It(17, "arrow", 1, 20, 24, 6, 2).TryDamage(out mn, out mx, out dps);
                Check("damage: ammo adds damage per second (7.5), no range", got && Math.Abs(dps - 7.5) < 0.01);
                Check("damage: no item level row, no speed = nothing; a silly spread counts as 0 (27 - 27)", !It(18, "x", 3, 33, 13, 2, 7, 2600, 0.5f).TryDamage(out _, out _, out _) && !It(19, "x", 3, 20, 13, 2, 7, 0, 0.5f).TryDamage(out _, out _, out _)
                    && It(20, "x", 3, 20, 13, 2, 7, 2600, 99f).TryDamage(out mn, out mx, out _) && mn == 27 && mx == 27, mn + "-" + mx);
                var e1001 = ItemDetails.EffectsOf(1001);
                Check("effects: item 1001 has 'Equip' spell 7000 and 'Use' spell 8000; 1002 a chance on hit; 1003's soulstone trigger is kept in the data (no word for it), 1004's missing spell and a locked effect row are not there",
                    e1001.Count == 2 && e1001[0].Key == 1 && e1001[0].Value == 7000 && e1001[1].Key == 0 && e1001[1].Value == 8000 && ItemDetails.EffectsOf(1002).Single().Key == 2 && d.Effects.ContainsKey(1003) && !d.Effects.ContainsKey(1004) && ItemDetails.EffectsOf(5).Count == 0);
                Check("trigger words: Use / Equip / Chance on hit; a soulstone trigger says nothing", ItemDetails.TriggerPrefix(0) == "Use: " && ItemDetails.TriggerPrefix(1) == "Equip: " && ItemDetails.TriggerPrefix(2) == "Chance on hit: " && ItemDetails.TriggerPrefix(3) == null && ItemDetails.TriggerPrefix(9) == null);
                var s700 = d.Sets[700];
                Check("sets: 'Test Battlegear', 3 pieces, bonuses at 2 (spell 7001) and 3 (7002; the junk above the threshold's low byte is masked), the bonus row of an unknown set is dropped; an empty set is not kept",
                    s700.Name == "Test Battlegear" && s700.Items.SequenceEqual(new[] { 1001, 1002, 1003 }) && s700.Bonuses.Select(b => b.Key + ":" + b.Value).SequenceEqual(new[] { "2:7001", "3:7002" }) && !d.Sets.ContainsKey(701) && d.SetOfItem[1002] == 700, string.Join(",", s700.Bonuses.Select(b => b.Key + ":" + b.Value)));

                // ---- the tooltip lines
                var tipItem = It(1001, "Battle Axe", 3, 20, 13, 2, 7, 2600, 0.5f); tipItem.Bonding = 1; tipItem.RequiredLevel = 10; tipItem.SellPrice = 500;
                var snapItems = new ItemSnapshot(new[] { tipItem, It(1002, "Battle Helm", 3, 20, 1, 4, 2), It(1003, "Battle Chest", 3, 20, 5, 4, 2) }, null);
                ItemData.SetForTest(snapItems, ItemData.State.Ready);
                var lines = ItemText.Lines(tipItem, new[] { 1001, 1002 }.ToList());
                Check("tooltip: level, slot, damage '20 - 34 Damage', speed, '(10.4 damage per second)', binding, level requirement, the Equip and Use lines with the spell's own words, the set with 2/3 worn and its pieces, the bonuses (2 reached, 3 not), price",
                    SdFlat(lines) == "ilvl: Item Level 20 | bind: Binds when picked up | slot: One-Hand | Sword | damage: 20 - 34 Damage | speed: Speed 2.60 | dps: (10.4 damage per second) | req: Requires Level 10 | effect: Equip: Increases your hit rating by 12. | effect: Use: Restores 300 health. | setname: Test Battlegear (2/3) | setitem-worn: Battle Axe | setitem-worn: Battle Helm | setitem: Battle Chest | setbonus-on: (2) Set: +20 Attack Power. | setbonus: (3) Set: Decreases the cooldown of Concussive Shot by 3 sec. | price: Sell Price: | 500",
                    SdFlat(lines));
                var arm = ItemText.Lines(snapItems.Get(1003)).Select(l => l.ToString()).ToList();
                Check("tooltip: armor line '72 Armor' right after the slot; without worn pieces the set shows 0/3 and no bonus is 'on'", arm.Contains("armor: 72 Armor") && arm.IndexOf("armor: 72 Armor") == arm.FindIndex(l => l.StartsWith("slot:")) + 1 && arm.Contains("setname: Test Battlegear (0/3)") && arm.All(l => !l.StartsWith("setbonus-on")), string.Join(" | ", arm));
                ItemDetails.Current = null;
                Check("tooltip without the tables: exactly the old lines (no armor, damage, effect or set lines)", ItemText.Lines(tipItem).All(l => l.Kind != "damage" && l.Kind != "dps" && l.Kind != "effect" && l.Kind != "armor" && !l.Kind.StartsWith("set")) && It(1, "c", 3, 20, 5, 4, 2).Armor == 0);
                ItemDetails.Current = d;

                // ---- unknown layouts: only that table is unavailable
                var unkMisc = SpellParse.All(SdRaw(r => r.Tables[SpellTables.SpellMisc.Name] = SdSpellMisc(0xDEADBEEF)));
                Check("an unknown SpellMisc layout: names, descriptions and effects still work; icons, cast time and duration are missing; the reason is recorded and says 'game updated'",
                    unkMisc.Spells != null && unkMisc.Spells.Get(133).IconId == 0 && unkMisc.Spells.Get(133).DurationMs == 0 && unkMisc.Spells.Get(133).Effects.Length == 2 && unkMisc.Problems.ContainsKey("SpellMisc") && unkMisc.Problems["SpellMisc"].Contains("game updated") && unkMisc.Problems["SpellMisc"].Contains("DEADBEEF"), string.Join("; ", unkMisc.Problems.Values));
                Check("... and a description that needs the duration then keeps its token ($d stays), never a guess", SpellText.Expand(unkMisc.Spells.Get(133).Desc, unkMisc.Spells.Get(133), unkMisc.Spells.Get).EndsWith("over $d."));
                var unkEff = SpellParse.All(SdRaw(r => r.Tables[SpellTables.SpellEffect.Name] = SdSpellEffect(0xDEADBEEF)));
                Check("an unknown SpellEffect layout: no effects (so no produced items; the 4 recipes with reagents remain) and the numbers stay tokens; factions and item details still work", unkEff.Spells.Get(133).Effects.Length == 0 && unkEff.Spells.RecipeCount == 4 && unkEff.Spells.Producing(1001).Count == 0 && unkEff.Problems.ContainsKey("SpellEffect") && unkEff.Factions != null && unkEff.Details != null, unkEff.Spells.RecipeCount + "");
                var unkName = SpellParse.All(SdRaw(r => r.Tables[SpellTables.SpellName.Name] = SdSpellName(0xDEADBEEF)));
                Check("an unknown SpellName layout: the spell group is unavailable (null) with the reason; factions and item details are unaffected", unkName.Spells == null && unkName.Problems["spells"].Contains("game updated") && unkName.Factions != null && unkName.Details != null);
                var unkFac = SpellParse.All(SdRaw(r => r.Tables[SpellTables.Faction.Name] = SdFaction(0x12345678)));
                Check("an unknown Faction layout: no factions; spells unaffected", unkFac.Factions == null && unkFac.Problems.ContainsKey("factions") && unkFac.Spells != null);
                var unkTpl = SpellParse.All(SdRaw(r => r.Tables[SpellTables.FactionTemplate.Name] = SdFactionTemplate(0x12345678)));
                Check("an unknown FactionTemplate layout: faction names work, the standing of any template is neutral", unkTpl.Factions != null && unkTpl.Factions.Faction(72).Name == "Stormwind" && unkTpl.Factions.Templates.Count == 0 && unkTpl.Factions.ToAlliance(11) == Standing.Neutral);
                var unkArmor = SpellParse.All(SdRaw(r => r.Tables[SpellTables.ArmorTotal.Name] = SdArmorTotal(0x12345678)));
                ItemDetails.Current = unkArmor.Details;
                Check("an unknown ItemArmorTotal layout: no armor lines, but weapon damage, effects and sets still work", !unkArmor.Details.HasArmor && unkArmor.Problems.ContainsKey("armor") && It(1, "c", 3, 20, 5, 4, 2).Armor == 0 && It(11, "sword", 3, 20, 13, 2, 7, 2600, 0.5f).TryDamage(out _, out _, out _) && unkArmor.Details.EffectCount == 3);
                var unkSet = SpellParse.All(SdRaw(r => r.Tables[SpellTables.ItemSet.Name] = SdItemSet(0x12345678)));
                Check("an unknown ItemSet layout: no sets (null) and no set lines", unkSet.Details.Sets == null && unkSet.Problems.ContainsKey("itemsets") && unkSet.Details.EffectCount == 3);
                ItemDetails.Current = d;
                var wt = SpellParse.All(SdRaw(r => r.Tables[SpellTables.SpellName.Name] = SdFaction()));
                Check("a table in the wrong place (another table's bytes): unavailable, not read as spells", wt.Spells == null && wt.Problems["spells"].Contains("not the table"), wt.Problems.ContainsKey("spells") ? wt.Problems["spells"] : "no problem");
                var nothing = SpellParse.All(new SpellRaw());
                Check("no tables at all (not in this install): everything unavailable, no exception, a reason per group", nothing.Spells == null && nothing.Factions == null && nothing.Details.EffectCount == 0 && nothing.Problems.ContainsKey("spells") && nothing.Problems.ContainsKey("factions"));
                var garbage = new SpellRaw(); garbage.Tables[SpellTables.SpellName.Name] = new byte[] { 1, 2, 3 }; garbage.Tables[SpellTables.Faction.Name] = SdFaction().Take(300).ToArray();
                var gp = SpellParse.All(garbage);
                Check("garbage or a cut file: unavailable with a reason, no exception", gp.Spells == null && gp.Factions == null && gp.Problems["spells"].Length > 0 && gp.Problems["factions"].Length > 0);

                // ---- the saved index (keyed by the game build)
                var idx = Path.Combine(work, "idx", "spelldata.bin");
                Check("index file: written, no leftover .tmp", SpellIndexFile.TryWrite(idx, "wow_classic_beta|1.60.1.70338|abc", set) && File.Exists(idx) && !File.Exists(idx + ".tmp"));
                var back = SpellIndexFile.TryRead(idx);
                var bs = back?.Set;
                Check("index file round trip: build, format, spells with effects, descriptions, skills, abilities, recipes, reagents",
                    back != null && back.Build == "wow_classic_beta|1.60.1.70338|abc" && back.Format == SpellData.FormatVersion && bs.Spells.SpellCount == 11 && bs.Spells.Get(133).Desc == fb.Desc && bs.Spells.Get(133).Effects.Length == 2 && bs.Spells.Get(133).Effects[1].PeriodMs == 2000
                    && bs.Spells.Skills.Count == 6 && bs.Spells.Abilities.Count == 7 && bs.Spells.RecipeCount == 4 && bs.Spells.Producing(1001).Single().Reagents.SequenceEqual(new[] { 2589, 2, 3820, 1 }) && bs.Spells.Get(133).IconId == 135812 && bs.Spells.Get(133).CastMs == 1500);
                Check("index file round trip: the lock / quest lookups", bs.World != null && bs.World.SkillNeed(39)?.Skill == 65 && bs.World.SortName(-141) == "Paladin" && bs.World.InfoName(81) == "Dungeon" && bs.World.LineOf(92742)?.Quests.Length == 2 && bs.World.QuestIds.Count == 3);
                Check("index file round trip: factions (names, templates, standings), item details (armor, damage tables, effects, sets), notes and the layouts that were read",
                    bs.Factions.Faction(72).Name == "Stormwind" && bs.Factions.ToHorde(11) == Standing.Hostile && bs.Factions.ToAlliance(99) == Standing.Friendly && bs.Details.HasArmor && bs.Details.Damage.Count(x => x != null) == 5 && bs.Details.Damage[ItemDetails.KindTwoHandCaster] == null
                    && bs.Details.EffectCount == 3 && bs.Details.Sets[700].Bonuses.Count == 2 && bs.Details.SetOfItem[1003] == 700 && bs.Notes.Count == set.Notes.Count && bs.LayoutsSeen["SpellName"] == 0x782EE721);
                ItemDetails.Current = bs.Details;
                Check("... and the restored tables give the same armor and damage", It(1, "c", 3, 20, 5, 4, 2).Armor == 72 && It(11, "sword", 3, 20, 13, 2, 7, 2600, 0.5f).TryDamage(out mn, out mx, out _) && mn == 20 && mx == 34);
                ItemDetails.Current = d;
                var corrupt = Path.Combine(work, "idx", "corrupt.bin"); var rawIdx = File.ReadAllBytes(idx); File.WriteAllBytes(corrupt, rawIdx.Take(rawIdx.Length / 2).ToArray());
                var junk = Path.Combine(work, "idx", "junk.bin"); File.WriteAllText(junk, "this is not an index");
                Check("index file: a cut, a foreign or a missing file reads as null (the game files are read instead), no exception", SpellIndexFile.TryRead(corrupt) == null && SpellIndexFile.TryRead(junk) == null && SpellIndexFile.TryRead(Path.Combine(work, "idx", "missing.bin")) == null);
                int reads = 0;
                Func<AtlasSet> game = () => { reads++; return SpellParse.All(SdRaw()); };
                var obt = Path.Combine(work, "obt", "spelldata.bin");
                var o1 = SpellData.Obtain(SpellIndexFile.TryRead(obt), "B1", obt, game, out var f1);
                Check("first start: the game files are read once, the index is saved", reads == 1 && f1 == "game files" && o1.Spells != null && File.Exists(obt), f1);
                var o2 = SpellData.Obtain(SpellIndexFile.TryRead(obt), "B1", obt, game, out var f2);
                Check("second start, same build: from the saved index, the game files are NOT read", reads == 1 && f2 == "cache file" && o2.Spells.SpellCount == 11, f2);
                SpellData.Obtain(SpellIndexFile.TryRead(obt), "B2", obt, game, out var f3);
                Check("the game was patched (other build key): the index is not trusted, the game files are read again and saved", reads == 2 && f3 == "game files" && SpellIndexFile.TryRead(obt).Build == "B2");
                File.WriteAllBytes(obt, new byte[] { 1, 2, 3 });
                SpellData.Obtain(SpellIndexFile.TryRead(obt), "B2", obt, game, out var f4);
                Check("a damaged index: the game files are read again and the file is replaced", reads == 3 && f4 == "game files" && SpellIndexFile.TryRead(obt) != null);
                var none = SpellData.Obtain(null, "B3", Path.Combine(work, "obt", "none.bin"), () => { var s = new AtlasSet(); s.Problems["all"] = "the game files could not be read"; return s; }, out _);
                Check("game files that cannot be read: nothing is saved", none.Problems.ContainsKey("all") && !File.Exists(Path.Combine(work, "obt", "none.bin")));

                // ---- the Settings line
                ItemData.SetForTest(snapItems, ItemData.State.Ready);
                SpellData.SetForTest(null, SpellData.State.NotStarted);
                Check("status line without spell data: exactly as before ('3 items, 0 revealed')", ItemData.StatusLine == "Item data: read from your WoW installation (3 items, 0 revealed)", ItemData.StatusLine);
                SpellData.SetForTest(set);
                Check("status line with spell data: '... (3 items, 0 revealed, 11 spells, 4 recipes, 8 factions)'", ItemData.StatusLine == "Item data: read from your WoW installation (3 items, 0 revealed, 11 spells, 4 recipes, 8 factions)", ItemData.StatusLine);
                SpellData.SetForTest(null, SpellData.State.Unavailable, "game updated: SpellName layout DEADBEEF is not known to this Outpost yet");
                Check("status line when the spell data is unavailable: says so with the reason, the item data is not affected", ItemData.StatusLine.Contains("3 items, 0 revealed, spell data unavailable (game updated") && SpellData.Spell(133) == null && !SpellData.Ready && FactionData.NameOfTemplate(11) == null && SpellData.RecipesProducing(1001).Count == 0 && ItemDetails.Current == null);
                SpellData.SetForTest(set);
                SpellData.AutoStart = false; int sc = SpellData.StartCountForTest; ItemData.Enabled = () => true;
                SpellData.Start();
                Check("with AutoStart off Start() reads nothing (the self-test never reads the real game)", SpellData.Status == SpellData.State.Ready && SpellData.StartCountForTest == sc + 1);

                // ---- the Lodge hook: revealed items
                var revItems = new List<ClientItem>
                {
                    new ClientItem { Id = 2001, Name = "Hidden Axe", Quality = 4, ItemLevel = 20, RequiredLevel = 12, InventoryType = 13, ClassId = 2, SubclassId = 7, DelayMs = 2600, DmgVariance = 0.5f, Source = "cache", Stats = new[] { 4, 12, 7, 9, 200, 5, 3, 0 } },
                    new ClientItem { Id = 2002, Name = "Hidden Chest", Quality = 3, ItemLevel = 20, InventoryType = 5, ClassId = 4, SubclassId = 2, Source = "cache", Stats = null },
                    new ClientItem { Id = 2003, Name = "", Quality = 3, Source = "cache" },
                    new ClientItem { Id = 2004, Name = "Shipped Thing", Quality = 3, Source = "client" },
                    new ClientItem { Id = 0, Name = "Zero", Quality = 1, Source = "cache" },
                    new ClientItem { Id = 2005, Name = "Evil\" [[x]] name", Quality = 2, ItemLevel = 5, Source = "cache", Stats = new[] { 3, 5 } },
                };
                var rev = RevealedItemsSource.From(revItems);
                Check("revealed source: only the revealed items with a name and a valid id (2001, 2002, 2005), in id order", rev.Select(r => r.Id).SequenceEqual(new[] { 2001, 2002, 2005 }), string.Join(",", rev.Select(r => r.Id)));
                var ax = rev[0];
                Check("revealed source: the parsed fields (name, quality 4, item level 20, requires 12, class 2 / sub 7, slot 13); stats keep the valid pairs only (no type 200, no zero amount)",
                    ax.Name == "Hidden Axe" && ax.Quality == 4 && ax.ItemLevel == 20 && ax.RequiredLevel == 12 && ax.Class == 2 && ax.SubClass == 7 && ax.Slot == 13 && ax.Stats.Select(s => s[0] + ":" + s[1]).SequenceEqual(new[] { "4:12", "7:9" }), string.Join(",", ax.Stats.Select(s => s[0] + ":" + s[1])));
                Check("revealed source: armor and damage are worked out for the Atlas page and kept beside (axe, epic: 5 dps x 2.6 s = 13 -> 10 - 16 from the one-hand table; chest: 72 armor); unknown stat amounts = no stats; the name is clean",
                    ax.DmgMin == 10 && ax.DmgMax == 16 && rev[1].Stats.Length == 0 && rev[1].Armor == 72 && !rev[2].Name.Contains("\"") && !rev[2].Name.Contains("["), ax.DmgMin + "-" + ax.DmgMax + " " + rev[1].Armor);
                var batch = AtlasObs.MakeBatch(null, rev, new Dictionary<string, string>());
                var bitems = batch?.Frame.Child("items");
                Check("revealed source: goes out through the existing Atlas sync as 'items' (3 entries, name / stats), no armor or damage on the wire", batch != null && bitems.Count == 3 && bitems.Child("2001").Str("n") == "Hidden Axe" && bitems.Child("2001").ContainsKey("st") && !bitems.Child("2001").ContainsKey("armor") && !bitems.Child("2001").ContainsKey("dmg"));
                ItemData.SetForTest(new ItemSnapshot(new[] { revItems[3] }, new[] { revItems[0], revItems[1] }), ItemData.State.Ready);
                var viaData = new RevealedItemsSource().Items().ToList();
                ItemData.SetForTest(null, ItemData.State.Unavailable, "off");
                Check("revealed source on ItemData: it reads the snapshot's revealed items (2001, 2002); with no item data it reveals nothing", viaData.Select(r => r.Id).SequenceEqual(new[] { 2001, 2002 }) && !new RevealedItemsSource().Items().Any());
                Check("the app hands the source to the Lodge sync at startup", AtlasObs.RevealedSource is RevealedItemsSource);

                // ---- the server's hotfixes beyond items (MainWindow.SpellDataTest.Hotfix.cs)
                SdHotfixChecks(work, set, Check);

                // ---- the Characters page
                await SdPageChecks(dir, work, set, Check, P);

                // ---- optional: the real game files, read-only
                SdSmokeReal(Check, notes);
            }
            catch (Exception ex) { ok = false; notes.Add("FAIL exception: " + ex); }
            finally
            {
                ItemDetails.Current = null;
                SpellData.ResetForTest(); ItemData.ResetForTest();
                SpellData.CacheFile = savedCache; ItemData.CacheFile = savedItemCache; ItemData.Enabled = savedEnabled;
                SpellData.AutoStart = false;
                HubData.ResetCache();
                IconStore.ForgetMemory();
                InventoryView.ResetViewForTest();
                InventoryView.Init(() => settings.WowRoot);
                ShowTab("addons");
            }
            notes.Insert(0, "spelldata (Atlas A2 + A4, item details): " + (ok ? "all ok" : "FAILURES"));
            return string.Join("\r\n", notes);
        }

        // the Characters page: item tooltips with armor / damage / effects / the set, recipes under a profession, the spell in a talent tile
        async Task SdPageChecks(string dir, string work, AtlasSet baseSet, CheckFn Check, Func<string, string> P)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var root = Path.Combine(work, "pagewow");
            var sv = Path.Combine(root, "_classic_beta_", "WTF", "Account", "ITEMS", "SavedVariables");
            Directory.CreateDirectory(sv);
            Directory.CreateDirectory(Path.Combine(root, "_classic_beta_", "Interface", "AddOns"));
            File.WriteAllText(Path.Combine(sv, "ElansBags.lua"), BagsLua(3), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(sv, "ElansHub.lua"), HubCharsLua(now, now - 60), new UTF8Encoding(false));
            HubData.ResetCache();

            ClientItem Mk(int id, string name, int q, int il, int inv, int cls, int sub, int delay, float var, int req, int bond, int icon = 0) =>
                new ClientItem { Id = id, Name = name, Quality = q, ItemLevel = il, InventoryType = inv, ClassId = cls, SubclassId = sub, DelayMs = delay, DmgVariance = var, RequiredLevel = req, Bonding = bond, IconId = icon, Stats = ClientItem.NoStats };
            var client = new List<ClientItem>
            {
                Mk(200, "Helm of Testing", 3, 20, 1, 4, 2, 0, 0, 10, 1), Mk(201, "Plain Chestguard", 3, 20, 5, 4, 2, 0, 0, 10, 0), Mk(202, "Epic Blade", 3, 20, 13, 2, 7, 2600, 0.5f, 10, 1),
                Mk(109, "White Cloak", 1, 10, 16, 4, 1, 0, 0, 1, 0), Mk(5001, "Boots of the Set", 3, 20, 8, 4, 2, 0, 0, 10, 1),
                Mk(2840, "Copper Bar", 1, 5, 0, 7, 3, 0, 0, 0, 0, 132999), Mk(3576, "Tin Bar", 1, 10, 0, 7, 3, 0, 0, 0, 0), Mk(2770, "Copper Ore", 1, 1, 0, 7, 7, 0, 0, 0, 0), Mk(2771, "Tin Ore", 1, 1, 0, 7, 7, 0, 0, 0, 0),
                Mk(5004, "Spiced Wolf Meat", 1, 5, 0, 0, 5, 0, 0, 0, 0), Mk(12208, "Tender Wolf Meat", 1, 5, 0, 7, 8, 0, 0, 0, 0), Mk(2589, "Linen Cloth", 1, 5, 0, 7, 5, 0, 0, 0, 0),
            };
            ItemData.SetForTest(new ItemSnapshot(client, null), ItemData.State.Ready);

            // a spell set for the page: the parsed fixture plus Mining / Cooking recipes, an enchant, and the spells of two talents
            var spells = baseSet.Spells.Spells.Values.ToList();
            SpellInfo Sp(int id, string name, string rank, string desc, int icon, params EffectInfo[] fx) => new SpellInfo { Id = id, Name = name, Rank = rank, Desc = desc, IconId = icon, Effects = fx };
            spells.Add(Sp(1002, "Improved Strikes", "Rank 2", "Increases your damage by $s1% and your threat by $s2%.", 132000, new EffectInfo { Index = 0, Points = 4f }, new EffectInfo { Index = 1, Points = 10f }));
            spells.Add(Sp(1004, "Hidden Wisdom", "", "", 132001));
            spells.Add(Sp(6001, "Smelt Copper", "", "Smelts copper.", 133000, new EffectInfo { Index = 0, Effect = 24, ItemType = 2840 }));
            spells.Add(Sp(6002, "Smelt Tin", "", "Smelts tin.", 133001, new EffectInfo { Index = 0, Effect = 24, ItemType = 3576 }));
            spells.Add(Sp(6003, "Spiced Wolf Meat", "", "Cooks meat.", 133002, new EffectInfo { Index = 0, Effect = 24, ItemType = 5004 }));
            spells.Add(Sp(6004, "Enchant Bracer", "", "Permanently adds +2 Stamina to a bracer.", 133003, new EffectInfo { Index = 0, Effect = 53 }));
            var skills = baseSet.Spells.Skills.Values.ToList();
            var abil = baseSet.Spells.Abilities.ToList();
            abil.Add(new SkillAbility { Id = 20, Skill = 186, Spell = 6004, MinRank = 100 });
            var reag = new Dictionary<int, int[]>(baseSet.Spells.Reagents) { [6004] = new[] { 2589, 3 } };
            var uiSet = new AtlasSet { Spells = new SpellSnapshot(spells, skills, abil, reag), Factions = baseSet.Factions, Details = baseSet.Details };
            SpellData.SetForTest(uiSet);

            InventoryView.Init(() => root);
            TabInventory.IsChecked = true;
            for (int i = 0; i < 40 && InventoryView.CharCountForTest != 6; i++) await Task.Delay(200);
            Check("page: the characters are listed (6) with the spell data loaded", InventoryView.CharCountForTest == 6, InventoryView.CharCountForTest.ToString());
            InventoryView.FilterForTest("Forever-Elan");

            // item tooltips (bag slot): armor, damage, speed, dps
            InventoryView.SelectTabForTest("bags");
            var chest = InventoryView.SlotTipLinesForTest(201, "Forever-Elan");
            Check("slot tooltip of a chest piece: name, Item Level, slot + type, '72 Armor', requirement", chest.Take(4).SequenceEqual(new[] { "Plain Chestguard", "Item Level 20", "Chest", "Leather" }) && chest.Contains("72 Armor") && chest.Contains("Requires Level 10"), string.Join(" | ", chest));
            var blade = InventoryView.SlotTipLinesForTest(202, "Forever-Elan");
            Check("slot tooltip of a weapon: '20 - 34 Damage', 'Speed 2.60' and '(10.4 damage per second)'", blade.Contains("20 - 34 Damage") && blade.Contains("Speed 2.60") && blade.Contains("(10.4 damage per second)"), string.Join(" | ", blade));

            // the gear tab: the set count counts what this character wears (head 200, chest 201, main hand 202; the boots 5001 are not worn)
            var setRes = new ItemSetInfo { Id = 800, Name = "Fixture Battlegear", Items = new List<int> { 200, 201, 202, 5001 }, Bonuses = new List<KeyValuePair<int, int>> { new KeyValuePair<int, int>(2, 7001), new KeyValuePair<int, int>(4, 7002) } };
            baseSet.Details.Sets[800] = setRes; foreach (var i in setRes.Items) baseSet.Details.SetOfItem[i] = 800;
            InventoryView.SelectTabForTest("gear");
            var head = InventoryView.DollTipForTest(1);
            Check("gear slot tooltip: the set shows 3/4 worn (the head, chest and weapon of this character), the worn pieces, the bonus that is reached and the one that is not, 58 armor",
                head.Contains("58 Armor") && head.Contains("Fixture Battlegear (3/4)") && head.Contains("(2) Set: +20 Attack Power.") && head.Contains("(4) Set: Decreases the cooldown of Concussive Shot by 3 sec.") && head.Contains("Boots of the Set"), string.Join(" | ", head));
            await ShotWait(300);
            SnapshotSheet(InventoryView.DollTipSheetForTest(1), P("23-spelldata-tooltip-set.png"));
            baseSet.Details.Sets.Remove(800); foreach (var i in setRes.Items) baseSet.Details.SetOfItem.Remove(i);

            // professions: recipes you can learn
            InventoryView.SelectTabForTest("professions");
            var pl = InventoryView.CharTabLinesForTest();
            Check("professions: Mining gets 3 recipes, Cooking 1; Herbalism (no skill line in the data) none", pl.Contains("recipes|Mining|3") && pl.Contains("recipes|Cooking|1") && !pl.Any(x => x.StartsWith("recipes|Herbalism")), string.Join(" | ", pl.Where(x => x.StartsWith("recipes"))));
            var rows = InventoryView.RecipeRowsForTest("Mining");
            Check("professions: Mining's recipes by skill: Copper Bar (1), Tin Bar (65), the enchant without an item by its spell name (100)", rows != null && rows.SequenceEqual(new[] { "Copper Bar | Skill 1", "Tin Bar | Skill 65", "Enchant Bracer | Skill 100" }), rows == null ? "null" : string.Join(" / ", rows));
            var rtip = InventoryView.RecipeTipForTest("Mining", 1);
            Check("recipe tooltip: the item (Tin Bar), its facts, the reagent '1 x Tin Ore', the skill and rank, the spell and item ids", rtip.Count > 5 && rtip[0] == "Tin Bar" && rtip.Contains("Reagents:") && rtip.Contains("1 x Tin Ore") && rtip.Any(l => l.StartsWith("Mining · skill 65")) && rtip.Contains("Spell ID 6002 · Item ID 3576"), string.Join(" | ", rtip));
            var etip = InventoryView.RecipeTipForTest("Mining", 2);
            Check("recipe tooltip of an enchant (makes no item): the spell's description, then the reagents", etip[0] == "Enchant Bracer" && etip.Contains("Permanently adds +2 Stamina to a bracer.") && etip.Contains("3 x Linen Cloth"), string.Join(" | ", etip));
            await ShotWait(400);
            Snapshot(P("23-spelldata-recipes.png"));
            SnapshotSheet(InventoryView.RecipeTipSheetForTest("Mining", 1), P("23-spelldata-tooltip-recipe.png"));

            // talents: the spell of a node in its tooltip
            InventoryView.SelectTabForTest("talents");
            var tl = InventoryView.CharTabLinesForTest();
            Check("talents: nodes with a known spell id (1002, 1004) carry the spell in their tooltip; the other nodes do not", tl.Contains("talentspell|1002|Improved Strikes") && tl.Contains("talentspell|1004|Hidden Wisdom") && tl.Count(x => x.StartsWith("talentspell|")) == 2, string.Join(" | ", tl.Where(x => x.StartsWith("talentspell"))));
            var ttip = InventoryView.TalentTipForTest(1002);
            Check("talent tooltip: the spell's rank text, its description with the numbers filled in ('Increases your damage by 4% and your threat by 10%.'), the spell id",
                ttip.Contains("Rank 2") && ttip.Contains("Increases your damage by 4% and your threat by 10%.") && ttip.Contains("Spell ID 1002"), string.Join(" | ", ttip));
            await ShotWait(300);
            Snapshot(P("23-spelldata-talents.png"));
            SnapshotSheet(InventoryView.TalentTipSheetForTest(1002), P("23-spelldata-tooltip-talent.png"));

            // without the spell data the page is exactly as before
            SpellData.SetForTest(null, SpellData.State.Unavailable, "off for the test");
            InventoryView.SelectTabForTest("overview"); InventoryView.SelectTabForTest("professions");
            pl = InventoryView.CharTabLinesForTest();
            var noRows = InventoryView.RecipeRowsForTest("Mining");
            InventoryView.SelectTabForTest("talents");
            tl = InventoryView.CharTabLinesForTest();
            InventoryView.SelectTabForTest("bags");
            var noSpell = InventoryView.SlotTipLinesForTest(201, "Forever-Elan");
            Check("without spell data: no recipe sections, no spell lines in the talent tiles; item tooltips lose only the lines that need the tables", !pl.Any(x => x.StartsWith("recipes")) && noRows == null && !tl.Any(x => x.StartsWith("talentspell")) && !noSpell.Contains("72 Armor") && noSpell.Contains("Requires Level 10"));
            SpellData.SetForTest(uiSet);
            Check("the Outpost wrote nothing into the WoW folder (only the two test files are left)", Directory.GetFiles(root, "*", SearchOption.AllDirectories).Select(f => Path.GetFileName(f)).OrderBy(x => x).SequenceEqual(new[] { "ElansBags.lua", "ElansHub.lua" }));
        }

        // read-only look at the real game files, only when there is a WoW install on this PC
        void SdSmokeReal(CheckFn Check, List<string> notes)
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
                var raw = SpellData.ReadCasc(real, "_classic_beta_");
                var set = SpellParse.All(raw);
                long ms = sw.ElapsedMilliseconds;
                var hard = set.Problems.Where(p => p.Value.StartsWith("game updated")).ToList();
                if (hard.Count > 0) { notes.Add("skip real game files: " + string.Join("; ", hard.Select(p => p.Value)) + " (this Outpost does not know that game build's tables; nothing was guessed)"); return; }
                SpellData.SetForTest(set);
                Check("real game files: more than 25,000 spells, 100+ skills, 1,000+ recipes, 200+ factions", set.Spells != null && set.Spells.SpellCount > 25000 && set.Spells.Skills.Count > 100 && set.Spells.RecipeCount > 1000 && set.Factions != null && set.Factions.Factions.Count > 200,
                    set.Spells?.SpellCount + " " + set.Spells?.Skills.Count + " " + set.Spells?.RecipeCount + " " + set.Factions?.Factions.Count);
                var fb = SpellData.Spell(133);
                Check("real game files: spell 133 is Fireball (Rank 1), with a description without leftover $s tokens and a cast time", fb != null && fb.Name == "Fireball" && fb.Rank == "Rank 1" && !string.IsNullOrEmpty(fb.Description) && !fb.Description.Contains("$s1") && fb.CastMs > 0, fb?.Description);
                var alch = SpellData.SkillByName("Alchemy");
                Check("real game files: the Alchemy profession has 50+ recipes, and the Major Healing Potion (item 13446) is made by a recipe", alch != null && SpellData.RecipesOfSkill(alch.Id).Count > 50 && SpellData.RecipesProducing(13446).Count >= 1, alch == null ? "no alchemy" : SpellData.RecipesOfSkill(alch.Id).Count + "");
                Check("real game files: faction 72 is Stormwind (under the Alliance); template 11 is friendly to the Alliance and hostile to the Horde; template 85 the other way round",
                    FactionData.Name(72) == "Stormwind" && FactionData.GroupOfTemplate(11) == "Alliance" && FactionData.ToAlliance(11) == Standing.Friendly && FactionData.ToHorde(11) == Standing.Hostile && FactionData.ToHorde(85) == Standing.Friendly && FactionData.ToAlliance(85) == Standing.Hostile);
                Check("real game files: item effects exist (more than 5,000 items) and item sets (more than 300)", set.Details != null && set.Details.EffectCount > 5000 && set.Details.SetCount > 300, set.Details?.EffectCount + " " + set.Details?.SetCount);
                var rawI = ItemData.ReadCasc(real, "_classic_beta_");
                var iset = ItemData.ParseTables(rawI.Sparse, rawI.Item, rawI.Budget);
                if (iset.Ok)
                {
                    var snap = new ItemSnapshot(iset.Client, null);
                    ItemData.SetForTest(snap, ItemData.State.Ready);
                    var tf = snap.Get(19019);
                    Check("real game files: Thunderfury has a damage range and a 'Chance on hit' line; a leather chest piece has armor", tf != null && tf.TryDamage(out var dmin, out var dmax, out _) && dmin > 0 && dmax > dmin && ItemText.Lines(tf).Any(l => l.Kind == "effect" && l.Text.StartsWith("Chance on hit: ")) && (snap.Get(16417)?.Armor ?? 0) > 0);
                }
                var w = set.World;
                Check("real game files: locks need the Classic skill levels (29 Peacebloom: Herbalism 1, 31 Briarthorn: Herbalism 70, 45 Kingsblood: Herbalism 125, 38 Copper Vein: Mining 1, 39 Tin Vein: Mining 65, 41 Iron Deposit: Mining 125, 2: Lockpicking 25)",
                    w != null && w.SkillNeed(29)?.LockType == 2 && w.SkillNeed(29)?.Skill == 1 && w.SkillNeed(31)?.Skill == 70 && w.SkillNeed(45)?.Skill == 125 && w.SkillNeed(38)?.LockType == 3 && w.SkillNeed(38)?.Skill == 1
                    && w.SkillNeed(39)?.Skill == 65 && w.SkillNeed(41)?.Skill == 125 && w.SkillNeed(2)?.LockType == 1 && w.SkillNeed(2)?.Skill == 25 && w.LockTypeName(2) == "Herbalism" && w.LockTypeName(3) == "Mining",
                    w == null ? "no world" : w.SkillNeed(29)?.LockType + "/" + w.SkillNeed(29)?.Skill);
                Check("real game files: quest sorts and kinds have their names (-304 Cooking, -141 Paladin, 81 Dungeon), the client lists more than 5,000 quests (25 Stonetalon Standstill among them)",
                    w != null && w.SortName(-304) == "Cooking" && w.SortName(-141) == "Paladin" && w.InfoName(81) == "Dungeon" && w.QuestIds.Count > 5000 && w.QuestIds.Contains(25), w?.QuestIds.Count + "");
                notes.Add("note real game files: " + set.Spells?.SpellCount + " spells, " + set.Spells?.RecipeCount + " recipes, " + set.Factions?.Factions.Count + " factions, " + set.Factions?.Templates.Count + " templates, " + set.Details?.EffectCount + " item effects, " + set.Details?.SetCount + " sets, read + parse " + ms + " ms");
                foreach (var n in set.Notes) notes.Add("note real game files: " + n);
                foreach (var p in set.Problems) notes.Add("note real game files: " + p.Key + ": " + p.Value);
            }
            catch (Exception e)
            {
                notes.Add("skip real game files: " + e.Message);
            }
        }
    }
}
