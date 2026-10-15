using System;
using System.Collections.Generic;
using System.Linq;

namespace ElansAddonHub.Services
{
    // Instance entrances learned in the game (Elan's Toolkit 0.5.7, `atlas.obs` field `entrances`; Lodge sub-feature "atlas6", server 2.24). When a member arrives
    // in a dungeon or raid, the Toolkit notes the last spot outdoors (at most 30 s old, not after a summon): { [instanceMapId] = { { z, x, y, n, at } } } - z the zone
    // map, x / y fractions of it (4 decimals), n = times entered from there. The Lodge merges the members' spots (same zone map, within 1 %) and says how many members
    // entered from each (m). Used for the instances whose way in is not in the game data (AtlasInstances.Entrance).
    public static partial class AtlasObs
    {
        public const int MaxEntranceSpots = 8, MaxEntranceInstances = 300, MaxEntrancesPerShare = 60, MaxEntranceSpotsPerShare = 300, MaxLodgeEntranceInstances = 5000;
        public const double EntranceMerge = 0.01;
        public static readonly Version EntrancesServerVersion = new Version(2, 24, 0);   // sub-feature "atlas6": instance entrances

        public class EntranceSpot { public int Z; public double X, Y; public long N, At; public int M; }

        public static bool SameEntrance(EntranceSpot a, int z, double x, double y) => a.Z == z && (a.X - x) * (a.X - x) + (a.Y - y) * (a.Y - y) <= EntranceMerge * EntranceMerge + 1e-12;

        // { [instanceMapId] = { { z, x, y, n, at?, m? } } }; a bad spot is dropped and counted, a bad key drops that instance. lodge = the merge (members `m` kept)
        public static Dictionary<int, List<EntranceSpot>> ParseEntrances(object node, ref int dropped, bool lodge = false, int maxInstances = MaxEntranceInstances)
        {
            var res = new Dictionary<int, List<EntranceSpot>>();
            var mo = node as Dictionary<string, object>;
            if (mo == null) { dropped++; return res; }
            foreach (var kv in mo)
            {
                if (res.Count >= maxInstances || !Key(kv.Key, MaxMapId, out var inst)) { dropped++; continue; }
                var l = new List<EntranceSpot>();
                foreach (var e in ArrayValues(kv.Value))
                {
                    var o = e as Dictionary<string, object>;
                    if (o == null || !o.TryGetValue("z", out var zv) || !Whole(zv, 1, MaxMapId, out var z) || !o.TryGetValue("x", out var xv) || !Coord4(xv, out var x)
                        || !o.TryGetValue("y", out var yv) || !Coord4(yv, out var y) || !o.TryGetValue("n", out var nv) || !Whole(nv, 1, lodge ? MaxAggregateCount : MaxNodeCount, out var n)) { dropped++; continue; }
                    var sp = new EntranceSpot { Z = (int)z, X = x, Y = y, N = n };
                    if (o.TryGetValue("at", out var av) && Whole(av, MinUnix, MaxAt, out var a)) sp.At = a;
                    if (lodge) sp.M = o.TryGetValue("m", out var mv) && Whole(mv, 1, 100000, out var mm) ? (int)mm : 1;
                    var same = l.FirstOrDefault(p => SameEntrance(p, sp.Z, x, y));
                    if (same != null) { same.N = Math.Max(same.N, n); same.At = Math.Max(same.At, sp.At); same.M = Math.Max(same.M, sp.M); continue; }
                    if (l.Count >= MaxEntranceSpots) { dropped++; continue; }
                    l.Add(sp);
                }
                if (l.Count > 0) res[(int)inst] = l;
            }
            return res;
        }

        public static string Sig(List<EntranceSpot> spots) =>
            Hash(string.Join(";", spots.Select(p => I(p.Z) + ":" + D4(p.X) + ":" + D4(p.Y) + ":" + I(p.N)).OrderBy(x => x, StringComparer.Ordinal)));

        // the wire shape of one instance's spots: [ { z, x, y, n, m? } ]
        internal static List<object> EntrancesNode(List<EntranceSpot> spots, bool withMembers = false) =>
            spots.OrderBy(p => p.Z).ThenBy(p => p.X).ThenBy(p => p.Y).Select(p =>
            {
                var o = new Dictionary<string, object> { ["z"] = p.Z, ["x"] = p.X, ["y"] = p.Y, ["n"] = p.N };
                if (withMembers) o["m"] = p.M;
                return (object)o;
            }).ToList();

        // the spot to show: most members, then most entries
        public static EntranceSpot Best(IEnumerable<EntranceSpot> spots) => spots?.OrderByDescending(p => p.M).ThenByDescending(p => p.N).ThenByDescending(p => p.At).FirstOrDefault();
    }
}
