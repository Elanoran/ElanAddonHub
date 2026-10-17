using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ElansAddonHub.Services.Maps;

namespace ElansAddonHub.Services.Atlas
{
    public static partial class AtlasQuery
    {
        public const int MaxNodeMarkersPerObject = 150;

        // the text under a Forever node: "Looted 12 times · Forever"
        public static string NodeTip(long n) => (n <= 1 ? "Looted once" : "Looted " + n.ToString("N0", CultureInfo.InvariantCulture) + " times") + " · " + AtlasLabel.Forever;

        // "12 catches · Raw Brilliant Smallfish ×7, Oily Blackmouth ×3 · Forever"
        public static string FishSpotTip(AtlasObs.FishSpot p)
        {
            var items = string.Join(", ", p.Items.OrderByDescending(i => i.Value).ThenBy(i => i.Key).Take(3).Select(i => ItemName(i.Key) + " ×" + i.Value.ToString("N0", CultureInfo.InvariantCulture)));
            return (p.N <= 1 ? "1 catch" : p.N.ToString("N0", CultureInfo.InvariantCulture) + " catches") + (items.Length > 0 ? " · " + items : "") + " · " + AtlasLabel.Forever;
        }

        // Forever nodes of one object on every map they were recorded on (the Lodge's merged nodes and this PC's own), most looted first, as markers above the
        // Classic baseline's. Added after the Classic spawns so the Forever ones are drawn on top and the zone with the most of them comes first.
        static int AddObjectNodes(PlanBuilder plan, int objectId, string title)
        {
            var src = new LiveNodeSource();
            int total = 0;
            foreach (var map in src.ForeverMaps(objectId))
                foreach (var n in src.Forever(objectId, map).OrderByDescending(p => p.N).Take(MaxNodeMarkersPerObject))
                {
                    plan.Spot(map, n.X, n.Y, MarkerKind.Node, title, NodeTip(n.N), "obj:" + objectId);
                    total++;
                }
            total += AddCairnNodes(plan, src, objectId, title, null);
            return total;
        }

        // the Cairn's nodes of an object (2.93), after yours and your Lodge's: on one map (uiMapId > 0) or on every map it has; the same spot is not drawn twice
        static int AddCairnNodes(PlanBuilder plan, INodeSource src, int objectId, string title, int? uiMapId)
        {
            int total = 0;
            foreach (var map in uiMapId.HasValue ? new List<int> { uiMapId.Value } : CairnNodeMaps(objectId))
            {
                var have = src.Forever(objectId, map);
                foreach (var c in CairnNodes(objectId, map, have).Take(Math.Max(0, MaxNodeMarkersPerObject - have.Count)))
                {
                    plan.Spot(map, c.X, c.Y, MarkerKind.Node, title, CairnSpotTip(c, true), "obj:" + objectId);
                    total++;
                }
            }
            return total;
        }

        // a zone page: the Forever nodes of every object that has some on this map (herbs, veins, chests, pools)
        static int AddZoneNodes(PlanBuilder plan, AtlasPageModel m, int uiMapId)
        {
            var src = new LiveNodeSource();
            var objs = GatherNodes.ObjectsOnMap(uiMapId);
            var spots = GatherNodes.FishSpotsOn(uiMapId);
            var cairnObjs = CairnObjectsOnMap(uiMapId).Where(k => !objs.Any(o => o.Key == k.Key)).ToList();      // 2.93: objects only the Cairn has here
            if (objs.Count == 0 && spots.Count == 0 && cairnObjs.Count == 0) return 0;
            var s = Section(m, "nodes", "Gathering nodes");
            s.Note = "Where players looted herbs, veins, chests and pools on this map. " + AtlasLabel.Forever + " positions come first (yours and your Lodge's, then the " + AtlasLabel.Cairn + "'s); the Classic baseline's are listed under each object's page.";
            int total = 0;
            if (spots.Count > 0)
            {
                s.Rows.Add(new AtlasRow { Key = "fishspots", Title = "Fishing spots", Label = AtlasLabel.Forever, Tier = 0, Type = "Fishing", Detail = AtlasFormat.Count(spots.Count, "spot", "spots") + " where players stood and caught something" });
                foreach (var p in spots.Take(MaxNodeMarkersPerObject)) { plan.Spot(uiMapId, p.X, p.Y, MarkerKind.Node, "Fishing spot", FishSpotTip(p), null); total++; }
            }
            foreach (var kv in objs.Take(MaxZoneRows))
            {
                string name = ObjectName(kv.Key);
                s.Rows.Add(new AtlasRow { Key = "obj:" + kv.Key, Link = "obj:" + kv.Key, Title = name, Label = AtlasLabel.Forever, Tier = 0, Type = "Object", Detail = AtlasFormat.Count(kv.Value, "node", "nodes") });
                foreach (var n in src.Forever(kv.Key, uiMapId).OrderByDescending(p => p.N).Take(MaxNodeMarkersPerObject))
                {
                    plan.Spot(uiMapId, n.X, n.Y, MarkerKind.Node, name, NodeTip(n.N), "obj:" + kv.Key);
                    total++;
                }
                total += AddCairnNodes(plan, src, kv.Key, name, uiMapId);
            }
            foreach (var kv in cairnObjs.Take(Math.Max(0, MaxZoneRows - objs.Count)))
            {
                string name = ObjectName(kv.Key);
                s.Rows.Add(new AtlasRow { Key = "obj:" + kv.Key, Link = "obj:" + kv.Key, Title = name, Label = AtlasLabel.Cairn, Tier = 1, Type = "Object", Detail = AtlasFormat.Count(kv.Value, "node", "nodes") });
                total += AddCairnNodes(plan, src, kv.Key, name, uiMapId);
            }
            if (cairnObjs.Count > 0 || total > 0 && CairnAtlas.Current != null) AddLayer(m, "cairn");
            return total;
        }
    }
}
