using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ElansAddonHub.Services.Atlas.Classic;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Maps;

namespace ElansAddonHub.Services.Atlas
{
    // Where an NPC really stands on Forever (Elan's Toolkit `npcspots`, merged by the Lodge, atlas8): the spots members met it at (a window opened), killed it at
    // (a looted corpse) or saw it as a rare (a minimap vignette). The Lodge's merge comes first; this PC's own spots fill in what the Lodge does not have yet
    // (not uploaded yet, no Lodge). They are shown ahead of the Classic baseline: in a zone where an NPC was met or seen as a rare (a fixed place), the baseline's
    // spawn points in that zone are left out (Forever moved it, or they say the same); kill spots only sample a creature's spawns, so the baseline stays beside them.
    public sealed class NpcSpotView
    {
        public int UiMapId, Kind, Members;      // Members: Lodge members who saw it there (0 = only you)
        public double X, Y;
        public long N;
        public bool Lodge => Members > 0;
        public bool Fixed => Kind == AtlasObs.NpcSpotInteract || Kind == AtlasObs.NpcSpotVignette;
    }

    public static partial class AtlasQuery
    {
        public const string NpcSpotLodgeLabel = "Seen by Lodge members", NpcSpotYouLabel = "Seen by you";

        static Dictionary<int, List<AtlasObs.NpcSpot>> OwnNpcSpots() { try { return Context.OwnNpcSpots?.Invoke(); } catch (Exception) { return null; } }

        // one npc's spots to show: the Lodge's (most members first), then this PC's own that no Lodge spot of the same kind already covers (twice the merge distance)
        static List<NpcSpotView> MergeSpots(IReadOnlyList<AtlasObs.NpcSpot> lodge, List<AtlasObs.NpcSpot> own)
        {
            var res = new List<NpcSpotView>();
            if (lodge != null)
                foreach (var p in lodge.OrderByDescending(p => p.M).ThenByDescending(p => p.N).ThenBy(p => p.K).ThenBy(p => p.Z))
                    res.Add(new NpcSpotView { UiMapId = p.Z, Kind = p.K, X = p.X, Y = p.Y, N = p.N, Members = Math.Max(1, p.M) });
            if (own != null)
                foreach (var p in own.OrderByDescending(p => p.N).ThenBy(p => p.K).ThenBy(p => p.Z))
                {
                    if (res.Any(r => r.Lodge && r.UiMapId == p.Z && r.Kind == p.K && (r.X - p.X) * (r.X - p.X) + (r.Y - p.Y) * (r.Y - p.Y) <= AtlasObs.NpcSpotMerge * AtlasObs.NpcSpotMerge * 4)) continue;
                    res.Add(new NpcSpotView { UiMapId = p.Z, Kind = p.K, X = p.X, Y = p.Y, N = p.N });
                }
            return res;
        }

        public static List<NpcSpotView> NpcSpots(int npcId)
        {
            var own = OwnNpcSpots();
            return MergeSpots(Lodge()?.NpcSpotsOf(npcId), own != null && own.TryGetValue(npcId, out var mine) ? mine : null);
        }

        // every npc with spots (the Lodge's and this PC's) at once, for the world map's layers (each source is asked once)
        public static Dictionary<int, List<NpcSpotView>> AllNpcSpots()
        {
            var res = new Dictionary<int, List<NpcSpotView>>();
            var l = Lodge();
            var own = OwnNpcSpots();
            var ids = new HashSet<int>();
            if (l != null) foreach (var kv in l.AllNpcSpots) ids.Add(kv.Key);
            if (own != null) foreach (var k in own.Keys) ids.Add(k);
            foreach (var id in ids.OrderBy(i => i))
            {
                var v = MergeSpots(l?.NpcSpotsOf(id), own != null && own.TryGetValue(id, out var mine) ? mine : null);
                if (v.Count > 0) res[id] = v;
            }
            return res;
        }

        // "Seen by 3 Lodge members · met here" / "Seen by you · killed here 4 times" (+ "· Forever" for the map's source count)
        public static string NpcSpotTip(NpcSpotView s, bool label = true)
        {
            var who = s.Lodge ? (s.Members == 1 ? "Seen by 1 Lodge member" : "Seen by " + s.Members.ToString("N0", CultureInfo.InvariantCulture) + " Lodge members") : NpcSpotYouLabel;
            var what = AtlasObs.NpcSpotKindText(s.Kind) + (s.N > 1 ? " " + s.N.ToString("N0", CultureInfo.InvariantCulture) + " times" : "");
            return who + " · " + what + (label ? " · " + AtlasLabel.Forever : "");
        }

        // "45 / 38" (percent, as the game shows map positions)
        public static string NpcSpotPos(NpcSpotView s) => Math.Round(s.X * 100).ToString(CultureInfo.InvariantCulture) + " / " + Math.Round(s.Y * 100).ToString(CultureInfo.InvariantCulture);

        // the zones where members fixed the npc's place (met it at its window, saw it as a rare)
        public static HashSet<int> FixedZones(IEnumerable<NpcSpotView> spots) => new HashSet<int>((spots ?? Enumerable.Empty<NpcSpotView>()).Where(s => s.Fixed).Select(s => s.UiMapId));

        // the Classic baseline's spawn points minus those in zones the members fixed (null idx: all stay); dropped = how many were left out
        internal static IReadOnlyList<ClassicSpawn> ClassicOutside(IReadOnlyList<ClassicSpawn> spawns, HashSet<int> fixedZones, out int dropped)
        {
            dropped = 0;
            var idx = MapData.Index;
            if (spawns == null || spawns.Count == 0 || fixedZones == null || fixedZones.Count == 0 || idx == null) return spawns;
            var keep = new List<ClassicSpawn>(spawns.Count);
            foreach (var s in spawns)
            {
                var p = idx.WorldToMap(s.Map, s.X, s.Y);
                if (p != null && fixedZones.Contains(p.Value.UiMapId)) { dropped++; continue; }
                keep.Add(s);
            }
            return keep;
        }

        // the "Seen at" section of an NPC page
        static void NpcSpotSection(AtlasPageModel m, List<NpcSpotView> spots, int classicDropped)
        {
            if (spots.Count == 0) return;
            var s = Section(m, "seenat", "Seen at");
            bool lodge = spots.Any(x => x.Lodge);
            s.Note = (lodge ? "Where Lodge members met it, killed it or saw it as a rare, from Elan's Toolkit." : "Where you met it, killed it or saw it as a rare, from Elan's Toolkit.")
                + (classicDropped > 0 ? " The Classic baseline's " + (classicDropped == 1 ? "spawn point" : classicDropped + " spawn points") + " in " + (spots.Count(x => x.Fixed) == 1 ? "that zone is" : "those zones are") + " left off the map: Forever's place wins." : "");
            int i = 0;
            foreach (var sp in spots)
                s.Rows.Add(new AtlasRow
                {
                    Key = "seenat:" + (i++), Link = AtlasIds.TryParse("zone:" + sp.UiMapId, out _, out _) ? "zone:" + sp.UiMapId : null, Title = ZoneName(sp.UiMapId), Zone = ZoneName(sp.UiMapId),
                    Detail = NpcSpotPos(sp) + " · " + NpcSpotTip(sp, false), Label = AtlasLabel.Forever, Tier = sp.Lodge ? 0 : 1, Type = sp.Kind == AtlasObs.NpcSpotInteract ? "Met" : sp.Kind == AtlasObs.NpcSpotKill ? "Killed" : "Rare",
                    Right = sp.Lodge ? sp.Members.ToString("N0", CultureInfo.InvariantCulture) : "",
                });
            AddLayer(m, lodge ? "lodge" : "client");
        }
    }
}
