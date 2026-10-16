using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ElansAddonHub.Services.Atlas.Classic;
using ElansAddonHub.Services.Maps;

namespace ElansAddonHub.Services.Atlas
{
    // The level range of every zone on the Zones list comes from the Classic baseline's creatures that stand in it (AtlasDerived.Zone), and where a creature
    // stands is decided by the terrain under it (AreaProbe): on a first start, or after a WoW update, that is several hundred terrain tiles of the game files.
    // The list used to wait for all of them (a minute and more of grey skeleton bars, with the tiles read one by one). Now it shows the zones at once, and
    // this works the levels out in the background: the tiles are read ahead on a few threads (with progress for the strip and the page), then every zone is
    // worked out, then the list is drawn again with its levels. The same tiles and the same levels as before; only when they arrive changed.
    public static class ZoneLevels
    {
        public const string JobId = "zonelevels";
        static readonly object gate = new object();
        static MapIndex forMap; static ClassicIndex forClassic; static Task task;
        public static int Revision;                 // a new value = levels arrived (the Zones index is built again)
        public static event Action Changed;
        public static int Threads = 3;
        public static int SyncTileLimit = 24;       // fewer tiles than this to read: the list just waits for them (a few hundred ms at most)

        // the zones BuildZones lists
        public static List<int> ZoneIds(MapIndex map) => map == null ? new List<int>() : map.UiMaps.Where(m => m.System == 0 && m.Type == UiMapInfo.Zone && !string.IsNullOrEmpty(m.Name) && !MapIndex.IsPlaceholderName(m.Name)).Select(m => m.Id).ToList();

        // true = ask AtlasDerived.Zone right away (all known, or nothing worth waiting for); false = the levels are on their way (Ensure started them)
        public static bool ReadyOrCheap(MapIndex map, AtlasDerived dv, IList<int> zones)
        {
            if (map == null || dv == null) return true;
            if (zones.All(z => dv.HasZone(map, z))) return true;
            lock (gate) if (task != null && !task.IsCompleted && ReferenceEquals(forMap, map) && ReferenceEquals(forClassic, dv.Index)) return false;
            var tiles = dv.ZoneTiles(map, zones);
            int missing = tiles.Sum(kv => AreaProbe.Missing(kv.Key, kv.Value));
            if (missing < SyncTileLimit) return true;
            Ensure(map, dv, zones, tiles, missing);
            return false;
        }

        public static bool Running { get { lock (gate) return task != null && !task.IsCompleted; } }
        public static Task TaskForTest { get { lock (gate) return task; } }

        static void Ensure(MapIndex map, AtlasDerived dv, IList<int> zones, Dictionary<int, HashSet<int>> tiles, int missing)
        {
            lock (gate)
            {
                if (task != null && !task.IsCompleted && ReferenceEquals(forMap, map) && ReferenceEquals(forClassic, dv.Index)) return;
                forMap = map; forClassic = dv.Index;
                var z = zones.ToList();
                GameDataProgress.Begin(JobId, "Zone levels", "Working out the zone levels", 0.5);
                GameDataProgress.Rebuilding(JobId, ReasonNow(), "Reading the zone borders from the terrain", 3, "terrain tiles", z.Any(id => dv.ZoneAny(id) != null));
                task = Task.Factory.StartNew(() => Run(map, dv, z, tiles, missing), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }
        }

        // the reason the strip gives: the map data's own (the tiles are cached per game build, like the maps)
        static GameDataProgress.Why ReasonNow()
        {
            var m = GameDataProgress.Now().Find(MapData.JobId);
            if (m != null && m.Rebuild) return m.Reason;
            return AreaProbe.AnySaved() ? GameDataProgress.Why.WowUpdated : GameDataProgress.Why.FirstStart;   // tiles of another build saved: the game was patched
        }

        static void Run(MapIndex map, AtlasDerived dv, List<int> zones, Dictionary<int, HashSet<int>> tiles, int missing)
        {
            try { Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; } catch (Exception) { }
            bool ok = true; string err = null;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                int before = 0;
                foreach (var kv in tiles)
                {
                    int b = before;
                    before += AreaProbe.Prefetch(kv.Key, kv.Value, CancellationToken.None, (d, t) => GameDataProgress.Report(JobId, Math.Min(missing, b + d), missing, 0, 0.8), Threads);
                }
                long readMs = sw.ElapsedMilliseconds;
                GameDataProgress.Step(JobId, "Working out the zone levels", 0.8, "zones", 0, zones.Count);
                for (int i = 0; i < zones.Count; i++)
                {
                    if (!ReferenceEquals(MapData.Index, map)) break;      // new map data meanwhile: the list asks again for that one
                    dv.Zone(map, zones[i]);
                    GameDataProgress.Report(JobId, i + 1, zones.Count, 0.8, 1);
                }
                Util.Log("zone levels: " + before + " terrain tiles read in " + readMs + " ms, " + zones.Count + " zones in " + sw.ElapsedMilliseconds + " ms");
            }
            catch (Exception e) { ok = false; err = e.Message; Util.Log("zone levels: " + e); }
            GameDataProgress.End(JobId, ok, err);
            Interlocked.Increment(ref Revision);
            try { Changed?.Invoke(); } catch (Exception) { }
        }

        public static void ResetForTest() { lock (gate) { forMap = null; forClassic = null; task = null; } }
    }
}
