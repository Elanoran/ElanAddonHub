using System;
using System.Collections.Generic;
using System.Linq;

namespace ElansAddonHub.Services.WorldMap
{
    // ============================================================================================================================
    // 2.98: the Lodge members' trails on the world map, drawn exactly like your own (LivePosition): the last 10 minutes of each member who shares their
    // position with you, built from the Lodge's "memberpos" stream (about once a second), with the move bits for the prints and a height for the 3D view
    // worked out by the same LiveElevation as yours (one solver per member: it keeps a little memory of the last point and the flight under way).
    // MEMORY ONLY: never written to disk, never logged, never sent anywhere. A member's trail goes the moment they stop sharing, leave, go invisible, or
    // "Show my live position on the world map" is switched off; another character of theirs starts a new one.
    // ============================================================================================================================
    public sealed class MemberTrails
    {
        sealed class Entry { public readonly LiveTrail Trail = new LiveTrail(); public LiveElevation Elev; public LiveTrail.Point Here; }
        readonly Dictionary<int, Entry> byId = new Dictionary<int, Entry>();
        readonly object gate = new object();
        IElevationWorld elevWorld;
        // tests set their own world; null = the Outpost's terrain cache, buildings and flight routes (GameElevationWorld.Shared)
        public IElevationWorld ElevationWorld { get => elevWorld; set { lock (gate) { elevWorld = value; foreach (var e in byId.Values) e.Elev = null; } } }

        public int Count { get { lock (gate) return byId.Count; } }
        public bool Has(int id) { lock (gate) return byId.ContainsKey(id); }

        // a member's new position (world coordinates: continent map id, x north, y west, yards). charKey: their character (another one starts a new trail).
        // The point is always their "here"; it joins the trail by LiveTrail's own rules (at most one a second, a few yards apart).
        public void Add(int id, string charKey, int mapId, double x, double y, int move, DateTime at)
        {
            if (id <= 0 || double.IsNaN(x + y) || double.IsInfinity(x + y)) return;
            lock (gate)
            {
                if (!byId.TryGetValue(id, out var e)) byId[id] = e = new Entry();
                if (e.Trail.Claim(charKey ?? "")) e.Elev?.Reset();
                var pt = new LiveTrail.Point { At = at, MapId = mapId, X = x, Y = y, Move = move & 0xFF };
                try
                {
                    if (e.Elev == null) e.Elev = new LiveElevation(elevWorld ?? GameElevationWorld.Shared);
                    var r = e.Elev.Solve(mapId, x, y, move, at);
                    if (!double.IsNaN(r.Z) && !double.IsInfinity(r.Z)) { pt.Z = r.Z; pt.Src = r.Src; pt.HasZ = true; }
                }
                catch (Exception) { }                                   // no height: the page puts them on its own ground (never a log line about a member's place)
                e.Here = pt;
                e.Trail.Add(charKey ?? "", pt);
            }
        }

        public bool Drop(int id) { lock (gate) return byId.Remove(id); }
        public void Clear() { lock (gate) byId.Clear(); }
        // everyone not in `ids` goes (they left, or can no longer be seen)
        public void KeepOnly(ICollection<int> ids) { lock (gate) foreach (var k in byId.Keys.Where(k => !ids.Contains(k)).ToList()) byId.Remove(k); }

        public LiveTrail.Point? HereOf(int id) { lock (gate) return byId.TryGetValue(id, out var e) ? e.Here : (LiveTrail.Point?)null; }
        public List<LiveTrail.Point> PointsOf(int id, DateTime now) { lock (gate) return byId.TryGetValue(id, out var e) ? e.Trail.Points(now) : new List<LiveTrail.Point>(); }
    }
}
