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
            return total;
        }

        // a zone page: the Forever nodes of every object that has some on this map (herbs, veins, chests, pools)
        static int AddZoneNodes(PlanBuilder plan, AtlasPageModel m, int uiMapId)
        {
            var src = new LiveNodeSource();
            var objs = GatherNodes.ObjectsOnMap(uiMapId);
            if (objs.Count == 0) return 0;
            var s = Section(m, "nodes", "Gathering nodes");
            s.Note = "Where players looted herbs, veins, chests and pools on this map. " + AtlasLabel.Forever + " positions come first; the Classic baseline's are listed under each object's page.";
            int total = 0;
            foreach (var kv in objs.Take(MaxZoneRows))
            {
                string name = ObjectName(kv.Key);
                s.Rows.Add(new AtlasRow { Key = "obj:" + kv.Key, Link = "obj:" + kv.Key, Title = name, Label = AtlasLabel.Forever, Tier = 0, Type = "Object", Detail = AtlasFormat.Count(kv.Value, "node", "nodes") });
                foreach (var n in src.Forever(kv.Key, uiMapId).OrderByDescending(p => p.N).Take(MaxNodeMarkersPerObject))
                {
                    plan.Spot(uiMapId, n.X, n.Y, MarkerKind.Node, name, NodeTip(n.N), "obj:" + kv.Key);
                    total++;
                }
            }
            return total;
        }
    }
}
