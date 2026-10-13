using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ElansAddonHub.Services.Db2
{
    public sealed class ItemSetInfo
    {
        public int Id;
        public string Name = "";
        public List<int> Items = new List<int>();                                   // item ids (the 17 slots without the empty ones)
        public List<KeyValuePair<int, int>> Bonuses = new List<KeyValuePair<int, int>>();   // (pieces needed, spell id), in that order
    }

    // The item details the game works out from tables instead of storing them in the item: armor, weapon damage, on-use / on-equip effects, item sets.
    // (ItemSparse holds none of these.) Tables the installed build lays out differently are simply missing here: the matching tooltip lines then do not
    // appear, nothing is guessed. Armor and damage follow the formulas the client's own tables are made for:
    //   armor  = ItemArmorQuality[ilvl][quality] x ItemArmorTotal[ilvl][cloth|leather|mail|plate] x ArmorLocation[slot][material], shields: ItemArmorShield[ilvl][quality]
    //   damage = ItemDamage<kind>[ilvl][quality] (damage per second) x speed; the spread comes from the item's DmgVariance
    public sealed class ItemDetails
    {
        public const int KindOneHand = 0, KindOneHandCaster = 1, KindTwoHand = 2, KindTwoHandCaster = 3, KindAmmo = 4, KindRanged = 5, KindThrown = 6, KindWand = 7;
        public static readonly SpellTables.Def[] DamageDefs =
        {
            SpellTables.DmgOneHand, SpellTables.DmgOneHandCaster, SpellTables.DmgTwoHand, SpellTables.DmgTwoHandCaster, SpellTables.DmgAmmo, SpellTables.DmgRanged, SpellTables.DmgThrown, SpellTables.DmgWand,
        };

        public Dictionary<int, float[]> ArmorQuality, ArmorTotal, ArmorShield, ArmorLocation;     // by item level (location: by inventory type); null = table unavailable
        public readonly Dictionary<int, float[]>[] Damage = new Dictionary<int, float[]>[8];       // by item level, per kind; a null entry = that table is unavailable
        public Dictionary<int, int[]> Effects;                  // item -> [trigger0, spell0, trigger1, spell1 ...]; null = unavailable
        public Dictionary<int, ItemSetInfo> Sets;               // null = unavailable
        public Dictionary<int, int> SetOfItem = new Dictionary<int, int>();

        public bool HasArmor => ArmorQuality != null && ArmorTotal != null && ArmorLocation != null;
        public int EffectCount => Effects == null ? 0 : Effects.Count;
        public int SetCount => Sets == null ? 0 : Sets.Count;

        // ---- the current tables (set by SpellData when they are read; null = none)
        static volatile ItemDetails current;
        public static ItemDetails Current { get { return current; } set { current = value; } }

        // ---- armor
        public int Armor(ClientItem it)
        {
            if (it == null || it.ClassId != 4 || it.Quality < 0 || it.Quality > 6) return 0;
            if (it.SubclassId == 6)
            {
                if (ArmorShield == null || !ArmorShield.TryGetValue(it.ItemLevel, out var sh)) return 0;
                return (int)(sh[it.Quality] + 0.5f);
            }
            if (it.SubclassId < 1 || it.SubclassId > 4 || !HasArmor) return 0;
            int inv = it.InventoryType == 20 ? 5 : it.InventoryType;            // a robe counts as a chest piece
            if (!ArmorLocation.TryGetValue(inv, out var loc) || !ArmorQuality.TryGetValue(it.ItemLevel, out var q) || !ArmorTotal.TryGetValue(it.ItemLevel, out var tot)) return 0;
            return (int)(q[it.Quality] * tot[it.SubclassId - 1] * loc[it.SubclassId - 1] + 0.5f);
        }

        public static int ArmorOf(ClientItem it) { var d = current; return d == null ? 0 : d.Armor(it); }

        // ---- weapon damage
        public static int KindOf(ClientItem it)
        {
            bool caster = (it.Flags2 & 0x200) != 0;
            switch (it.InventoryType)
            {
                case 24: return KindAmmo;
                case 17: return caster ? KindTwoHandCaster : KindTwoHand;
                case 13: case 21: case 22: return caster ? KindOneHandCaster : KindOneHand;
                case 25: return KindThrown;
                case 15: case 26: return it.SubclassId == 19 ? KindWand : it.SubclassId == 16 ? KindThrown : KindRanged;
                default: return -1;
            }
        }

        public bool Dmg(ClientItem it, out int min, out int max, out double dps)
        {
            min = max = 0; dps = 0;
            if (it == null || it.Quality < 0 || it.Quality > 6) return false;
            int kind = KindOf(it);
            if (kind < 0 || Damage[kind] == null || !Damage[kind].TryGetValue(it.ItemLevel, out var q)) return false;
            dps = Math.Round(q[it.Quality], 4);
            if (dps <= 0) return false;
            if (kind == KindAmmo) { dps = Math.Round(q[it.Quality], 1); return true; }
            if (it.DelayMs <= 0) return false;
            double avg = dps * it.DelayMs * 0.001, v = it.DmgVariance;
            if (double.IsNaN(v) || v < 0 || v > 2) v = 0;
            min = (int)Math.Round((1.0 - v * 0.5) * avg, MidpointRounding.AwayFromZero);
            max = (int)Math.Floor(avg * (v * 0.5 + 1.0) + 0.5);
            dps = Math.Round(dps, 1);
            return true;
        }

        public static bool DamageOf(ClientItem it, out int min, out int max, out double dps)
        {
            var d = current;
            if (d == null) { min = max = 0; dps = 0; return false; }
            return d.Dmg(it, out min, out max, out dps);
        }

        // ---- effects: "Use: ..." / "Equip: ..." / "Chance on hit: ..."
        public static string TriggerPrefix(int trigger)
        {
            switch (trigger)
            {
                case 0: case 4: case 5: case 6: return "Use: ";
                case 1: return "Equip: ";
                case 2: return "Chance on hit: ";
                default: return null;
            }
        }

        // (trigger, spell) pairs of an item, in the table's order
        public static List<KeyValuePair<int, int>> EffectsOf(int itemId)
        {
            var res = new List<KeyValuePair<int, int>>();
            var d = current;
            if (d == null || d.Effects == null || !d.Effects.TryGetValue(itemId, out var a)) return res;
            for (int i = 0; i + 1 < a.Length; i += 2) res.Add(new KeyValuePair<int, int>(a[i], a[i + 1]));
            return res;
        }

        // ---- sets
        public static ItemSetInfo SetOf(ClientItem it)
        {
            var d = current;
            if (d == null || d.Sets == null || it == null) return null;
            if (!d.SetOfItem.TryGetValue(it.Id, out int sid)) sid = it.ItemSetId;
            return sid > 0 && d.Sets.TryGetValue(sid, out var s) ? s : null;
        }

        // ---------------------------------------------------------------------------------------------------------------- parsing
        public static ItemDetails Parse(AtlasSet set, SpellRaw raw)
        {
            var d = new ItemDetails();

            // armor: item-level tables (floats) keyed by item level
            var tot = SpellParse.Take(set, raw, SpellTables.ArmorTotal);
            var qua = SpellParse.Take(set, raw, SpellTables.ArmorQuality);
            var loc = SpellParse.Take(set, raw, SpellTables.ArmorLocation);
            var shd = SpellParse.Take(set, raw, SpellTables.ArmorShield);
            if (tot.Ok) { d.ArmorTotal = new Dictionary<int, float[]>(); for (int i = 0; i < tot.Table.Count; i++) d.ArmorTotal[tot.Table.IdAt(i)] = Enumerable.Range(1, 4).Select(f => SpellTables.Flt(tot.Table, i, f)).ToArray(); }
            if (qua.Ok) { d.ArmorQuality = new Dictionary<int, float[]>(); for (int i = 0; i < qua.Table.Count; i++) d.ArmorQuality[qua.Table.IdAt(i)] = Enumerable.Range(0, 7).Select(e => SpellTables.FltArr(qua.Table, i, 0, e, 7)).ToArray(); }
            if (loc.Ok) { d.ArmorLocation = new Dictionary<int, float[]>(); for (int i = 0; i < loc.Table.Count; i++) d.ArmorLocation[loc.Table.IdAt(i)] = Enumerable.Range(0, 5).Select(f => SpellTables.Flt(loc.Table, i, f)).ToArray(); }
            if (shd.Ok) { d.ArmorShield = new Dictionary<int, float[]>(); for (int i = 0; i < shd.Table.Count; i++) d.ArmorShield[shd.Table.IdAt(i)] = Enumerable.Range(0, 7).Select(e => SpellTables.FltArr(shd.Table, i, 0, e, 7)).ToArray(); }
            if (!d.HasArmor) set.Problems["armor"] = "armor needs ItemArmorTotal, ItemArmorQuality and ArmorLocation";

            // weapon damage: ItemLevel(0), Quality[7](1) (damage per second)
            for (int k = 0; k < DamageDefs.Length; k++)
            {
                var o = SpellParse.Take(set, raw, DamageDefs[k]);
                if (!o.Ok) continue;
                var m = new Dictionary<int, float[]>();
                for (int i = 0; i < o.Table.Count; i++) m[o.Table.IdAt(i)] = Enumerable.Range(0, 7).Select(e => SpellTables.FltArr(o.Table, i, 1, e, 7)).ToArray();
                d.Damage[k] = m;
            }

            // item effects: ItemXItemEffect says which effect rows an item has (the item is the relationship column), ItemEffect holds trigger and spell
            var ie = SpellParse.Take(set, raw, SpellTables.ItemEffect);
            var ix = SpellParse.Take(set, raw, SpellTables.ItemXItemEffect);
            if (ie.Ok && ix.Ok)
            {
                var eff = new Dictionary<int, int[]>(ie.Table.Count);
                for (int i = 0; i < ie.Table.Count; i++) eff[ie.Table.IdAt(i)] = new[] { SpellTables.Int(ie.Table, i, 1), SpellTables.Int(ie.Table, i, 6) };
                d.Effects = new Dictionary<int, int[]>();
                var lists = new Dictionary<int, List<int>>();
                for (int i = 0; i < ix.Table.Count; i++)
                {
                    int item = ix.Table.Foreign(i);
                    if (item <= 0 || !eff.TryGetValue(SpellTables.Int(ix.Table, i, 0), out var e) || e[1] <= 0) continue;
                    if (!lists.TryGetValue(item, out var l)) lists[item] = l = new List<int>();
                    l.Add(e[0]); l.Add(e[1]);
                }
                foreach (var kv in lists) d.Effects[kv.Key] = kv.Value.ToArray();
            }
            else set.Problems["itemeffects"] = ie.Ok ? ix.Problem : ie.Problem;

            // item sets: ItemSet (name, 17 item slots) and ItemSetSpell (the bonuses; the set is the relationship column)
            var st = SpellParse.Take(set, raw, SpellTables.ItemSet);
            var ss = SpellParse.Take(set, raw, SpellTables.ItemSetSpell);
            if (st.Ok)
            {
                d.Sets = new Dictionary<int, ItemSetInfo>();
                var t = st.Table;
                for (int i = 0; i < t.Count; i++)
                {
                    var s = new ItemSetInfo { Id = t.IdAt(i), Name = (t.String(i, 0) ?? "").Trim() };
                    for (int e = 0; e < 17; e++)
                    {
                        int item = SpellTables.IntArr(t, i, 4, e, 17);
                        if (item > 0) { s.Items.Add(item); d.SetOfItem[item] = s.Id; }
                    }
                    if (s.Name.Length > 0 && s.Items.Count > 0) d.Sets[s.Id] = s;
                }
                if (ss.Ok)
                    for (int i = 0; i < ss.Table.Count; i++)
                    {
                        int sid = ss.Table.Foreign(i), spell = SpellTables.Int(ss.Table, i, 1), need = SpellTables.Int(ss.Table, i, 3) & 0xFF;     // Threshold is a u8: the pallet entry carries other bits above it
                        if (spell > 0 && need > 0 && d.Sets.TryGetValue(sid, out var s)) s.Bonuses.Add(new KeyValuePair<int, int>(need, spell));
                    }
                foreach (var s in d.Sets.Values) s.Bonuses = s.Bonuses.OrderBy(b => b.Key).ThenBy(b => b.Value).ToList();
            }
            else set.Problems["itemsets"] = st.Problem;
            return d;
        }
    }
}
