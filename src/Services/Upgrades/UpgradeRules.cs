using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Db2;

namespace ElansAddonHub.Services.Upgrades
{
    // Which item may go in which slot, and what the character can use. Pure rules: no window, no files.
    public static class UpgradeRules
    {
        // the paper doll's ids (InventorySlot numbers of the Hub's gear module), in the order the panel lists them
        public static readonly int[] Slots = { 1, 2, 3, 15, 5, 9, 10, 6, 7, 8, 11, 12, 13, 14, 16, 17, 18 };

        public static string SlotName(int slot)
        {
            switch (slot)
            {
                case 1: return "Head"; case 2: return "Neck"; case 3: return "Shoulders"; case 5: return "Chest"; case 6: return "Waist"; case 7: return "Legs"; case 8: return "Feet";
                case 9: return "Wrists"; case 10: return "Hands"; case 11: case 12: return "Ring"; case 13: case 14: return "Trinket"; case 15: return "Back";
                case 16: return "Main hand"; case 17: return "Off hand"; case 18: return "Ranged";
                default: return "Slot " + slot;
            }
        }

        // the slot's name with a count for the pairs: "Ring 1", "Trinket 2"
        public static string SlotTitle(int slot) => slot == 11 || slot == 13 ? SlotName(slot) + " 1" : slot == 12 || slot == 14 ? SlotName(slot) + " 2" : SlotName(slot);

        // InventoryType values: 1 head, 2 neck, 3 shoulder, 5 chest, 20 robe, 6 waist, 7 legs, 8 feet, 9 wrist, 10 hands, 11 finger, 12 trinket, 13 one-hand, 14 shield,
        // 15 bow, 16 back, 17 two-hand, 21 main hand, 22 off-hand weapon, 23 held in off-hand, 25 thrown, 26 gun / crossbow / wand, 28 relic
        public static bool Fits(int slot, int inv)
        {
            switch (slot)
            {
                case 1: return inv == 1;
                case 2: return inv == 2;
                case 3: return inv == 3;
                case 5: return inv == 5 || inv == 20;
                case 6: return inv == 6;
                case 7: return inv == 7;
                case 8: return inv == 8;
                case 9: return inv == 9;
                case 10: return inv == 10;
                case 11: case 12: return inv == 11;
                case 13: case 14: return inv == 12;
                case 15: return inv == 16;
                case 16: return inv == 13 || inv == 17 || inv == 21;
                case 17: return inv == 13 || inv == 14 || inv == 22 || inv == 23;
                case 18: return inv == 15 || inv == 25 || inv == 26 || inv == 28;
                default: return false;
            }
        }

        public static IEnumerable<int> SlotsFor(int inv) => Slots.Where(s => Fits(s, inv));
        public static bool IsTwoHand(ClientItem it) => it != null && it.InventoryType == 17;
        public static bool IsRingOrTrinket(int slot) => slot >= 11 && slot <= 14;
        public static int OtherOfPair(int slot) => slot == 11 ? 12 : slot == 12 ? 11 : slot == 13 ? 14 : slot == 14 ? 13 : 0;

        // Can this character use the item in this slot once they are `level`? The armor and weapon skills come from ItemProficiency (the classic rules: mail and plate from
        // level 40, a shield only for the classes that carry one), a second one-hand weapon needs dual wield, a relic only fits the class whose it is.
        public static bool CanUse(UpgradeChar who, ClientItem it, int slot, int level)
        {
            if (who == null || it == null || !it.Equippable) return false;
            if (it.ClassId != 2 && it.ClassId != 4) return false;
            if (!Fits(slot, it.InventoryType)) return false;
            if (!ItemProficiency.Known(who.ClassFile)) return false;
            if (!ItemProficiency.CanUse(who.ClassFile, level, it.ClassId, it.SubclassId)) return false;
            if (slot == 17 && (it.InventoryType == 13 || it.InventoryType == 22) && it.ClassId == 2)
            {
                int dw = ItemProficiency.DualWieldLevel(who.ClassFile);
                if (dw <= 0 || level < dw) return false;
            }
            if (it.InventoryType == 28 && it.ClassId != 4) return false;
            if (it.InventoryType == 14 && it.SubclassId != 6) return false;
            return true;
        }

        // The "worn" weapon facts: a two-hand weapon in the main hand means there is no off hand
        public static bool WieldsTwoHand(UpgradeChar who, Func<int, ClientItem> lookup)
        {
            if (who == null || !who.Worn.TryGetValue(16, out var id)) return false;
            return IsTwoHand(lookup(id));
        }
    }

    // What an item is worth to a character, and the plain words for the difference.
    public static class UpgradeScore
    {
        public static double Dps(ClientItem it)
        {
            if (it == null || (it.ClassId != 2 && it.InventoryType != 25 && it.InventoryType != 15 && it.InventoryType != 26)) return 0;
            return it.TryDamage(out _, out _, out var dps) ? dps : 0;
        }

        public static double Armor(ClientItem it) => it != null && it.ClassId == 4 ? it.Armor : 0;

        // the value of an item in a slot: stats x weights, armor, and weapon damage per second (halved in the off hand)
        public static double Score(ClientItem it, int slot, IDictionary<string, double> w)
        {
            if (it == null) return 0;
            double s = 0;
            double Wt(string k) => w.TryGetValue(k, out var v) ? v : 0;
            var st = it.Stats;
            if (st != null)
                for (int i = 0; i + 1 < st.Length; i += 2)
                {
                    var key = UpgradeWeights.KeyOfStat(st[i]);
                    if (key == null) continue;
                    double wt = Wt(key);
                    if (st[i] == 45) wt = Math.Max(wt, Wt("heal"));         // spell power counts for healing too
                    s += st[i + 1] * wt;
                }
            s += Armor(it) * Wt("armor");
            double dps = Dps(it);
            if (dps > 0) s += dps * (slot == 18 ? Wt("rdps") : (slot == 17 ? 0.5 : 1.0) * Wt("dps"));
            return s;
        }

        public static string PctText(double? pct)
        {
            if (pct == null) return "new";
            double p = pct.Value;
            if (p >= 1000) return "+1000%+";
            return (p >= 0 ? "+" : "-") + Math.Abs(Math.Round(p)).ToString("0", CultureInfo.InvariantCulture) + "%";
        }

        // One line of the difference: stat type -> amount; the pseudo types are armor (-1) and weapon damage per second (-2, x10)
        public sealed class StatDiff { public string Label; public double Delta; public double Weighted; public string Text; }

        // "+12 Agility, +6 Stamina, +18 armor": the changes between two items, the ones that matter most first (by weight), at most `max`
        public static List<StatDiff> Diff(ClientItem nu, ClientItem old, int slot, IDictionary<string, double> w, int max = 5)
        {
            var inv = CultureInfo.InvariantCulture;
            var a = new Dictionary<int, int>(); var b = new Dictionary<int, int>();
            void Fill(ClientItem it, Dictionary<int, int> d)
            {
                if (it?.Stats == null) return;
                for (int i = 0; i + 1 < it.Stats.Length; i += 2) { d.TryGetValue(it.Stats[i], out var v); d[it.Stats[i]] = v + it.Stats[i + 1]; }
            }
            Fill(nu, a); Fill(old, b);
            double Wt(string k) => w.TryGetValue(k, out var v) ? v : 0;
            var res = new List<StatDiff>();
            foreach (var type in a.Keys.Union(b.Keys))
            {
                a.TryGetValue(type, out var x); b.TryGetValue(type, out var y);
                int d = x - y;
                if (d == 0) continue;
                var key = UpgradeWeights.KeyOfStat(type);
                double wt = key == null ? 0 : Wt(key);
                if (type == 45) wt = Math.Max(wt, Wt("heal"));
                res.Add(new StatDiff { Label = ItemNames.Stat(type), Delta = d, Weighted = Math.Abs(d * wt), Text = (d > 0 ? "+" : "-") + Math.Abs(d).ToString(inv) + " " + ItemNames.Stat(type) });
            }
            int da = (int)Math.Round(Armor(nu) - Armor(old));
            if (da != 0) res.Add(new StatDiff { Label = "armor", Delta = da, Weighted = Math.Abs(da * Wt("armor")), Text = (da > 0 ? "+" : "-") + Math.Abs(da).ToString(inv) + " armor" });
            double nd = Dps(nu), od = Dps(old);
            if (nd > 0 || od > 0)
            {
                double dd = Math.Round(nd - od, 1);
                if (Math.Abs(dd) >= 0.1) res.Add(new StatDiff { Label = "DPS", Delta = dd, Weighted = Math.Abs(dd * (slot == 18 ? Wt("rdps") : Wt("dps"))), Text = (dd > 0 ? "+" : "-") + Math.Abs(dd).ToString("0.0", inv) + " DPS" });
            }
            return res.OrderByDescending(r => r.Weighted).ThenByDescending(r => Math.Abs(r.Delta)).ThenBy(r => r.Label, StringComparer.Ordinal).Take(max).ToList();
        }

        // "+12 Agility, +6 Stamina, +18 armor" (stat part only); empty text when nothing differs
        public static string DiffText(ClientItem nu, ClientItem old, int slot, IDictionary<string, double> w, int max = 4)
            => string.Join(", ", Diff(nu, old, slot, w, max).Select(d => d.Text));

        // the stats of an item alone, the ones that matter most first: for an empty slot
        public static string StatsText(ClientItem it, int slot, IDictionary<string, double> w, int max = 4) => DiffText(it, null, slot, w, max);
    }
}
