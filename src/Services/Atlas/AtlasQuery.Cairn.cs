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

        // 2.87 give to get: where the Cairn's rows would show for a member who doesn't share - one quiet line per page, not one per row
        public const string CairnShareNote = "The Cairn's numbers are for members who share - turn on sharing to get them from your Lodge.";
        static void AddCairnShareNote(AtlasPageModel m)
        {
            bool needs = false;
            try { needs = CairnAtlas.Current == null && Context.CairnNeedsSharing?.Invoke() == true; } catch { }
            if (needs && !m.Notes.Contains(CairnShareNote)) m.Notes.Add(CairnShareNote);
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

        // ---------------------------------------------------------------- the Cairn's places on the maps (2.93)
        // Spawn points (where members of the Cairn's Lodges killed or met a creature), gathering nodes and flight times, labelled "Cairn" and merged with what
        // your own Lodge (and you) saw: the Lodge's own places come first and a Cairn place that is the same spot (inside the node merge distance) is left out,
        // since the Cairn's numbers include your Lodge's. Only there while CairnAtlas.Current is - for members who share.
        public const double CairnSameSpot = AtlasObs.NodeMerge;

        static bool Near(double ax, double ay, double bx, double by) => (ax - bx) * (ax - bx) + (ay - by) * (ay - by) <= CairnSameSpot * CairnSameSpot + 1e-12;

        // "Seen 12 times · 3 lodges · Cairn" (spawns) / "Looted 12 times · 3 lodges · Cairn" (nodes)
        public static string CairnSpotTip(CairnSet.Spot s, bool node)
        {
            string n = s.N <= 1 ? (node ? "Looted once" : "Seen once") : (node ? "Looted " : "Seen ") + s.N.ToString("N0", CultureInfo.InvariantCulture) + " times";
            return n + " · " + Lodges(s.M) + " · " + AtlasLabel.Cairn;
        }

        // the Cairn's spawn points of a creature that are not already one of `have` (uiMap, x, y), most seen first
        public static List<CairnSet.Spot> CairnSpawns(int npc, IEnumerable<(int Map, double X, double Y)> have = null)
        {
            var c = CairnAtlas.Current; if (c == null) return new List<CairnSet.Spot>();
            var h = have?.ToList() ?? new List<(int, double, double)>();
            return c.Spawns(npc).Where(s => !h.Any(o => o.Item1 == s.Map && Near(o.Item2, o.Item3, s.X, s.Y))).OrderByDescending(s => s.N).ToList();
        }

        // the Cairn's nodes of an object on one map that are not one of `have` (your own / the Lodge's nodes there)
        public static List<CairnSet.Spot> CairnNodes(int obj, int uiMap, IEnumerable<GNode> have = null)
        {
            var c = CairnAtlas.Current; if (c == null) return new List<CairnSet.Spot>();
            var h = have?.ToList() ?? new List<GNode>();
            return c.Nodes(obj).Where(s => s.Map == uiMap && !h.Any(o => Near(o.X, o.Y, s.X, s.Y))).OrderByDescending(s => s.N).ToList();
        }

        // the maps the Cairn has nodes of an object on (most first)
        public static List<int> CairnNodeMaps(int obj)
        {
            var c = CairnAtlas.Current; if (c == null) return new List<int>();
            return c.Nodes(obj).GroupBy(s => s.Map).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => g.Key).ToList();
        }

        // the objects the Cairn has nodes of on one map, with how many
        public static List<KeyValuePair<int, int>> CairnObjectsOnMap(int uiMap)
        {
            var c = CairnAtlas.Current; var res = new List<KeyValuePair<int, int>>();
            if (c == null) return res;
            foreach (var kv in c.AllNodes) { int n = kv.Value.Count(s => s.Map == uiMap); if (n > 0) res.Add(new KeyValuePair<int, int>(kv.Key, n)); }
            return res.OrderByDescending(k => k.Value).ThenBy(k => k.Key).ToList();
        }

        // a flight leg's time from the Cairn ("Horde|11>22", either way round); 0 = none
        public static double CairnFlight(string faction, int a, int b, out int lodges)
        {
            lodges = 0;
            var c = CairnAtlas.Current; if (c == null) return 0;
            foreach (var fac in faction == null ? new[] { "Alliance", "Horde" } : new[] { faction })
            {
                if (c.Flight(fac + "|" + a.ToString(CultureInfo.InvariantCulture) + ">" + b.ToString(CultureInfo.InvariantCulture), out var t, out lodges) && t > 0) return t;
                if (c.Flight(fac + "|" + b.ToString(CultureInfo.InvariantCulture) + ">" + a.ToString(CultureInfo.InvariantCulture), out t, out lodges) && t > 0) return t;
            }
            lodges = 0; return 0;
        }

        // creature / object / container loot tables: the Cairn's items of that source
        // looted / lodges: what the Cairn's rows are based on (the section's coverage line says it, DataHonesty)
        static void AddCairnLoot(AtlasPageModel m, AtlasSection s, string lodgeKind, int id, List<AtlasRow> rows, List<AtlasRow> tiny, Dictionary<int, AtlasRow> by, out long looted, out int lodges)
        {
            looted = 0; lodges = 0;
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
            looted = Math.Max(1, src.N); lodges = Math.Max(1, src.M);
            AddLayer(m, "cairn");
        }
    }
}
