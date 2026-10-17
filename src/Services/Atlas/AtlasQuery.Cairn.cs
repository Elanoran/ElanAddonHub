using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ElansAddonHub.Services.Atlas
{
    // The Cairn's numbers (from the user's Lodge, give to get) on the Atlas pages (self-test section "cairn"). Label "Cairn", tier 1: below the user's own Lodge's rows
    // (tier 0), above the Classic baseline (tier 2). A source the Lodge already shows gets the Cairn's figure as a second line instead
    // of a row of its own, the way the Classic baseline rides along.
    public static partial class AtlasQuery
    {
        // the Cairn's drop kinds -> the Lodge's kind words (the row keys "npc:448", "skin:448" ... are the same on both)
        public static string CairnLodgeKind(string cairnKind)
        {
            switch (cairnKind)
            {
                case "kill": return "npc";
                case "object": return "obj";
                case "fishing": return "fish";
                case "pickpocket": return "pick";
                case "skinning": return "skin";
                case "mining": return "mine";
                case "herbalism": return "herb";
                case "salvage": return "salvage";
                case "contained": return "item";
                default: return null;
            }
        }

        static string CairnKindOf(string lodgeKind)
        {
            foreach (var k in CairnSet.Kinds) if (CairnLodgeKind(k) == lodgeKind) return k;
            return null;
        }

        static string Lodges(int m) => AtlasFormat.Count(Math.Max(1, m), "lodge", "lodges");

        static string CairnRate(CairnSet.Drop d, string what) => AtlasObs.RateText(d.Times, d.Kills, what, what + "s") + " · " + Lodges(d.M);

        // item page: where the Cairn saw the item drop and who sells it
        static void AddCairnItemRows(AtlasPageModel m, int id, Action<AtlasRow> addRow, List<AtlasRow> tiny, Dictionary<string, AtlasRow> byKey)
        {
            var c = CairnAtlas.Current;
            if (c == null) return;
            foreach (var d in c.DropsOf(id))
            {
                var lk = CairnLodgeKind(d.From.Kind);
                if (lk == null) continue;
                var key = lk + ":" + d.From.Id;
                string w = LootWord(lk);
                if (byKey.TryGetValue(key, out var seen))
                {
                    seen.Extra = (seen.Extra.Length > 0 ? seen.Extra + " · " : "") + AtlasLabel.Cairn + ": " + CairnRate(d, w);
                    AddLayer(m, "cairn");
                    continue;
                }
                bool fish = lk == "fish", obj = lk == "obj", inItem = lk == "item";
                var r = new AtlasRow
                {
                    Key = key, Link = fish ? "zone:" + d.From.Id : inItem ? "item:" + d.From.Id : obj ? key : "npc:" + d.From.Id, Label = AtlasLabel.Cairn, Tier = 1,
                    Sort = (AtlasObs.LowSample(d.Kills) ? d.Chance / 1000 : d.Chance) + Math.Min(d.Kills, 100000) / 1e7,
                    Type = LootTypeOf(lk),
                    Title = fish ? "Fishing in " + ZoneName(d.From.Id) : obj ? ObjectName(d.From.Id) : inItem ? ItemName(d.From.Id) : NpcName(d.From.Id),
                    Detail = CairnRate(d, w),
                };
                if (!AtlasObs.LowSample(d.Kills) && d.Chance < TinyChance) tiny.Add(r); else addRow(r);
                AddLayer(m, "cairn");
            }
            foreach (var v in c.VendorsOf(id))
            {
                var key = "vendor:" + v.Npc;
                var price = (v.Extended ? (v.Price > 0 ? AtlasFormat.Money(v.Price) + " + " : "") + "special cost" : v.Price > 0 ? AtlasFormat.Money(v.Price) : "free")
                    + (v.Stack > 1 ? " · stack of " + v.Stack : "") + (v.Limit >= 0 ? " · limit " + v.Limit : "");
                if (byKey.TryGetValue(key, out var seen))
                {
                    seen.Extra = (seen.Extra.Length > 0 ? seen.Extra + " · " : "") + AtlasLabel.Cairn + ": " + price + " · " + Lodges(v.M);
                    AddLayer(m, "cairn");
                    continue;
                }
                addRow(new AtlasRow { Key = key, Link = "npc:" + v.Npc, Label = AtlasLabel.Cairn, Tier = 1, Type = "Vendor", Title = NpcName(v.Npc), Detail = price + " · " + Lodges(v.M), Sort = 1e6 - v.Price });
                AddLayer(m, "cairn");
            }
        }

        // creature / object / container loot tables: the Cairn's items of that source
        static void AddCairnLoot(AtlasPageModel m, AtlasSection s, string lodgeKind, int id, List<AtlasRow> rows, List<AtlasRow> tiny, Dictionary<int, AtlasRow> by)
        {
            var c = CairnAtlas.Current;
            var kind = CairnKindOf(lodgeKind);
            var src = kind != null ? c?.Loot(kind, id) : null;
            if (src == null) return;
            string what = LootWord(lodgeKind);
            foreach (var d in src.Items)
            {
                if (by.TryGetValue(d.Item, out var seen)) { seen.Extra = (seen.Extra.Length > 0 ? seen.Extra + " · " : "") + AtlasLabel.Cairn + ": " + CairnRate(d, what); continue; }
                var r = ItemRow(d.Item, CairnRate(d, what), AtlasLabel.Cairn);
                r.Type = LootTypeOf(lodgeKind); r.Tier = 1; r.Sort = AtlasObs.LowSample(d.Kills) ? d.Chance / 1000 : d.Chance;
                if (!AtlasObs.LowSample(d.Kills) && d.Chance < TinyChance) tiny.Add(r); else rows.Add(r);
                by[d.Item] = r;
            }
            var line = "The Cairn (merged from " + Lodges(c.Lodges) + "): " + AtlasFormat.Count(src.N, "looted " + what, "looted " + what + "s") + " from " + Lodges(src.M) + ".";
            s.Note = s.Note.Length == 0 ? line : s.Note + " " + line;
            AddLayer(m, "cairn");
        }
    }
}
