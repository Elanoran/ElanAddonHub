using System;
using System.Collections.Generic;
using System.Linq;

namespace ElansAddonHub.Services
{
    // Where NPCs really stand on Forever (Elan's Toolkit 0.5.10, `atlas.obs` field `npcspots`; Lodge sub-feature "atlas8", server 2.26). The server keeps its spawn
    // points to itself, so the Toolkit notes them from play: { [npcId] = { { z, x, y, k, n, at } } } - z the zone map, x / y fractions of it (4 decimals), k the
    // kind (1 = a window opened for it: gossip, quest, vendor, trainer, flight master ...; you stood next to it; 2 = a looted corpse; 3 = a minimap vignette, a rare,
    // at its own position), n = times seen there. The Lodge merges the members' spots (same kind, same zone map, within 1 %) and says how many members saw each (m).
    // The NPC pages and the world map show them ahead of the Classic baseline (AtlasQuery.NpcSpots).
    public static partial class AtlasObs
    {
        public const int NpcSpotInteract = 1, NpcSpotKill = 2, NpcSpotVignette = 3;
        public const int NpcSpotCombat = 4;   // 2.95: from this PC's combat log (CombatLogData), never in the Toolkit's data or the Lodge's
        public const int MaxNpcSpotsPerNpc = 10, MaxLodgeNpcSpotsPerNpc = 12, MaxNpcSpotNpcs = 3000, MaxLodgeNpcSpotNpcs = 30000, MaxNpcSpotNpcsPerShare = 400, MaxNpcSpotsPerShare = 2000;
        public const double NpcSpotMerge = 0.01;
        public static readonly Version NpcSpotsServerVersion = new Version(2, 26, 0);   // sub-feature "atlas8": npc spots

        public class NpcSpot { public int Z, K; public double X, Y; public long N, At; public int M; }

        public static bool SameNpcSpot(NpcSpot a, int z, int k, double x, double y) => a.Z == z && a.K == k && (a.X - x) * (a.X - x) + (a.Y - y) * (a.Y - y) <= NpcSpotMerge * NpcSpotMerge + 1e-12;

        // { [npcId] = { { z, x, y, k, n, at?, m? } } }; a bad spot is dropped and counted, a bad key drops that npc. lodge = the merge (members `m` kept, 12 per npc)
        public static Dictionary<int, List<NpcSpot>> ParseNpcSpots(object node, ref int dropped, bool lodge = false, int maxNpcs = MaxNpcSpotNpcs)
        {
            var res = new Dictionary<int, List<NpcSpot>>();
            var mo = node as Dictionary<string, object>;
            if (mo == null) { dropped++; return res; }
            int per = lodge ? MaxLodgeNpcSpotsPerNpc : MaxNpcSpotsPerNpc;
            foreach (var kv in mo)
            {
                if (res.Count >= maxNpcs || !Key(kv.Key, MaxId, out var npc)) { dropped++; continue; }
                var l = new List<NpcSpot>();
                foreach (var e in ArrayValues(kv.Value))
                {
                    var o = e as Dictionary<string, object>;
                    if (o == null || !o.TryGetValue("z", out var zv) || !Whole(zv, 1, MaxMapId, out var z) || !o.TryGetValue("k", out var kvv) || !Whole(kvv, NpcSpotInteract, NpcSpotVignette, out var k)
                        || !o.TryGetValue("x", out var xv) || !Coord4(xv, out var x) || !o.TryGetValue("y", out var yv) || !Coord4(yv, out var y)
                        || !o.TryGetValue("n", out var nv) || !Whole(nv, 1, lodge ? MaxAggregateCount : MaxNodeCount, out var n)) { dropped++; continue; }
                    var sp = new NpcSpot { Z = (int)z, K = (int)k, X = x, Y = y, N = n };
                    if (o.TryGetValue("at", out var av) && Whole(av, MinUnix, MaxAt, out var a)) sp.At = a;
                    if (lodge) sp.M = o.TryGetValue("m", out var mv) && Whole(mv, 1, 100000, out var mm) ? (int)mm : 1;
                    var same = l.FirstOrDefault(p => SameNpcSpot(p, sp.Z, sp.K, x, y));
                    if (same != null) { same.N = Math.Max(same.N, n); same.At = Math.Max(same.At, sp.At); same.M = Math.Max(same.M, sp.M); continue; }
                    if (l.Count >= per) { dropped++; continue; }
                    l.Add(sp);
                }
                if (l.Count > 0) res[(int)npc] = l;
            }
            return res;
        }

        public static string Sig(List<NpcSpot> spots) =>
            Hash(string.Join(";", spots.Select(p => I(p.Z) + ":" + I(p.K) + ":" + D4(p.X) + ":" + D4(p.Y) + ":" + I(p.N)).OrderBy(x => x, StringComparer.Ordinal)));

        // the wire shape of one npc's spots: [ { z, x, y, k, n, m? } ]
        internal static List<object> NpcSpotsNode(List<NpcSpot> spots, bool withMembers = false) =>
            spots.OrderBy(p => p.K).ThenBy(p => p.Z).ThenBy(p => p.X).ThenBy(p => p.Y).Select(p =>
            {
                var o = new Dictionary<string, object> { ["z"] = p.Z, ["x"] = p.X, ["y"] = p.Y, ["k"] = p.K, ["n"] = p.N };
                if (withMembers) o["m"] = p.M;
                return (object)o;
            }).ToList();

        // "met at its window" / "killed here" / "seen as a rare" (the tooltips and the "Seen at" rows)
        public static string NpcSpotKindText(int k) => k == NpcSpotInteract ? "met here" : k == NpcSpotKill ? "killed here" : k == NpcSpotVignette ? "seen as a rare here" : k == NpcSpotCombat ? "fought here" : "seen here";
    }
}
