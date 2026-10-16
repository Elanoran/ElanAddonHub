using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using ElansAddonHub.Services.WorldMap;

namespace ElansAddonHub.Services.Maps
{
    // Which area (and so which zone) a world position is really in. The UiMapAssignment rectangles are boxes: Stormwind City's covers the edge of Northshire, a city's box wins over the
    // zone around it, a zone's box overlaps its neighbour's. The terrain is exact: every 33-yard chunk of a continent's ADT tiles carries its AreaTable id. This reads the one tile that holds
    // a position (root ADT through the continent's WDT, the player's own game files, read-only), keeps the 16 x 16 area ids of it in memory and in `cache\areas-<build>-<map>.bin`
    // (1 KB a tile, a whole continent is a few hundred KB), and holds the game's file storage open only while it is being used (closed after 20 s idle).
    // MapIndex.WorldToMap asks it (MapIndex.AreaAt): 0 = unknown (no game files, no such tile, a test) and the rectangles answer as before.
    public static class AreaProbe
    {
        public static Func<int, int, int, int[]> TileSourceForTest;          // (map, col, row) -> 256 area ids, null = no such tile
        public static string CacheDirOverride;
        public static int LoadedTilesForTest { get { lock (gate) return maps.Values.Sum(m => m.Tiles.Count); } }
        public static int ReadsForTest;                                      // tiles read from the game files (or the test source)

        sealed class MapAreas
        {
            public readonly Dictionary<int, int[]> Tiles = new Dictionary<int, int[]>();     // col * 64 + row -> 256 area ids (null = the continent has no such tile)
            public Dictionary<int, int> Roots;                                                 // col * 64 + row -> root ADT FileDataID (from the WDT)
            public bool Dirty, FileRead;
        }

        static readonly object gate = new object();
        static readonly Dictionary<int, MapAreas> maps = new Dictionary<int, MapAreas>();
        static CascShared.Lease lease;                                       // the game storage, shared with the data readers (opened once, root kept as a table)
        static CascStorage casc => lease?.Casc;
        static byte[] tileBuffer;                                            // under gate; let go with the storage
        static string rootSeen; static bool rootOk; static long rootCheckedAt;   // under gate: the WoW folder exists (checked at most once a second)
        static Timer idle;

        static string CachePath(int mapId)
            => Path.Combine(CacheDirOverride ?? Path.Combine(Util.DataDir, "cache"), "areas-" + MapData.BuildStamp + "-" + mapId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".bin");

        public static void ResetForTest()
        {
            lock (gate) { maps.Clear(); CloseCasc(false); ReadsForTest = 0; rootSeen = null; }
        }

        // the AreaTable id of the terrain chunk at the world position (0 = unknown)
        public static int AreaAt(int mapId, double x, double y)
        {
            if (double.IsNaN(x) || double.IsNaN(y) || !WorldMapMath.InWorld(x, y)) return 0;
            WorldMapMath.WorldToGrid(x, y, out var c, out var r);
            int col = (int)Math.Floor(c), row = (int)Math.Floor(r);
            if (!WorldMapMath.InGrid(col, row)) return 0;
            int cx = Math.Max(0, Math.Min(15, (int)((c - col) * 16))), cy = Math.Max(0, Math.Min(15, (int)((r - row) * 16)));
            int key = col * 64 + row;
            lock (gate)
            {
                if (!Available(mapId)) return 0;
                if (!maps.TryGetValue(mapId, out var m)) maps[mapId] = m = new MapAreas();
                if (!m.FileRead) { m.FileRead = true; ReadFile(mapId, m); }
                if (!m.Tiles.TryGetValue(key, out var t))
                {
                    t = Load(mapId, m, col, row);
                    m.Tiles[key] = t; m.Dirty = true;
                    ScheduleIdle();
                }
                return t == null ? 0 : Math.Max(0, t[cy * 16 + cx]);
            }
        }

        static bool Available(int mapId)
        {
            if (TileSourceForTest != null) return true;
            try
            {
                if (!MapData.Ready) return false;
                var root = MapData.WowRoot();
                if (string.IsNullOrEmpty(root)) return false;
                // the folder check is a file-system call: it was made for every position asked (the zone levels ask a hundred thousand), now at most once a second
                long now = Environment.TickCount;
                if (root != rootSeen || now - rootCheckedAt > 1000 || now < rootCheckedAt) { rootSeen = root; rootOk = Directory.Exists(root); rootCheckedAt = now; }
                if (!rootOk) return false;
                var gm = MapData.Index.GameMap(mapId);
                return gm != null && gm.WdtFileDataId > 0;
            }
            catch (Exception) { return false; }
        }

        static int[] Load(int mapId, MapAreas m, int col, int row)
        {
            Interlocked.Increment(ref ReadsForTest);
            if (TileSourceForTest != null) return TileSourceForTest(mapId, col, row);
            try
            {
                var gm = MapData.Index.GameMap(mapId);
                if (lease == null) lease = CascShared.Open(MapData.WowRoot(), MapData.Flavor() ?? "_classic_beta_");
                EnsureRoots(m, gm, casc);
                if (!m.Roots.TryGetValue(col * 64 + row, out var fdid)) return null;
                return ReadTile(casc, fdid, ref tileBuffer);
            }
            catch (Exception e) { Util.Log("area probe: " + e.Message); CloseCasc(false); return null; }
        }

        // the continent's tiles (WDT): tile key -> root ADT file; asked once per continent (under gate)
        static void EnsureRoots(MapAreas m, GameMapInfo gm, CascStorage c)
        {
            if (m.Roots != null) return;
            m.Roots = new Dictionary<int, int>();
            var wk = c.ResolveFileIds(new HashSet<int> { gm.WdtFileDataId });
            if (wk.TryGetValue(gm.WdtFileDataId, out var wck))
            {
                var wdt = WdtFile.Parse(c.ReadByContentKey(wck));
                if (wdt.Ok) foreach (var t in wdt.Tiles) if (t.RootFdid != 0) m.Roots[t.Col * 64 + t.Row] = t.RootFdid;
            }
        }

        // only the chunks' area ids, read into a buffer kept between tiles (a continent is several hundred tiles: no large array per tile for the GC)
        static int[] ReadTile(CascStorage c, int fdid, ref byte[] buffer)
        {
            var keys = c.ResolveFileIds(new HashSet<int> { fdid });
            if (!keys.TryGetValue(fdid, out var ck)) return null;
            int len = c.ReadByContentKey(ck, ref buffer);
            return len < 0 ? null : AdtFile.AreaIds(buffer, len);
        }

        // The tiles (col * 64 + row) the caller is about to ask about, read ahead on a few threads outside the lock (the zone levels of the Zones list
        // need several hundred of them on a first start). Same tiles and the same answers as asking AreaAt one by one; tiles already known are skipped.
        // progress(done, total). Returns the number of tiles read.
        public static int Prefetch(int mapId, ICollection<int> tileKeys, CancellationToken ct, Action<int, int> progress = null, int threads = 3)
        {
            MapAreas m; List<int> missing; GameMapInfo gm = null;
            lock (gate)
            {
                if (!Available(mapId)) return 0;
                if (!maps.TryGetValue(mapId, out m)) maps[mapId] = m = new MapAreas();
                if (!m.FileRead) { m.FileRead = true; ReadFile(mapId, m); }
                missing = tileKeys.Where(k => !m.Tiles.ContainsKey(k)).Distinct().ToList();
                if (missing.Count == 0) return 0;
                if (TileSourceForTest == null) gm = MapData.Index.GameMap(mapId);
            }
            var got = new System.Collections.Concurrent.ConcurrentDictionary<int, int[]>();
            int done = 0;
            if (TileSourceForTest != null)
            {
                foreach (var k in missing) { ct.ThrowIfCancellationRequested(); Interlocked.Increment(ref ReadsForTest); got[k] = TileSourceForTest(mapId, k / 64, k % 64); progress?.Invoke(++done, missing.Count); }
            }
            else
            {
                using (var l = CascShared.Open(MapData.WowRoot(), MapData.Flavor() ?? "_classic_beta_"))
                {
                    lock (gate) EnsureRoots(m, gm, l.Casc);
                    var roots = m.Roots;
                    System.Threading.Tasks.Parallel.ForEach(missing, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, threads), CancellationToken = ct },
                        () => (byte[])null,
                        (k, st, buf) =>
                        {
                            Interlocked.Increment(ref ReadsForTest);
                            int[] t = null;
                            try { if (roots.TryGetValue(k, out var fdid)) t = ReadTile(l.Casc, fdid, ref buf); }
                            catch (Exception e) { Util.Log("area probe: " + e.Message); return buf; }   // not stored: AreaAt tries that tile again itself
                            got[k] = t;
                            progress?.Invoke(Interlocked.Increment(ref done), missing.Count);
                            return buf;
                        },
                        buf => { });
                }
            }
            lock (gate)
            {
                foreach (var kv in got) if (!m.Tiles.ContainsKey(kv.Key)) { m.Tiles[kv.Key] = kv.Value; m.Dirty = true; }
                if (got.Count > 0) ScheduleIdle();
            }
            return got.Count;
        }

        // any terrain areas saved (of any game build)
        public static bool AnySaved()
        {
            try { var d = CacheDirOverride ?? Path.Combine(Util.DataDir, "cache"); return Directory.Exists(d) && Directory.EnumerateFiles(d, "areas-*.bin").Any(); }
            catch (Exception) { return false; }
        }

        // how many of these tiles asking would read from the game files (0: all known from memory or the saved file, or there are no game files to read)
        public static int Missing(int mapId, ICollection<int> tileKeys)
        {
            lock (gate)
            {
                if (TileSourceForTest != null || !Available(mapId)) return 0;
                if (!maps.TryGetValue(mapId, out var m)) maps[mapId] = m = new MapAreas();
                if (!m.FileRead) { m.FileRead = true; ReadFile(mapId, m); }
                return tileKeys.Count(k => !m.Tiles.ContainsKey(k));
            }
        }

        // the tile key (col * 64 + row) of a world position, -1 outside the grid (the same rule AreaAt uses)
        public static int TileKey(double x, double y)
        {
            if (double.IsNaN(x) || double.IsNaN(y) || !WorldMapMath.InWorld(x, y)) return -1;
            WorldMapMath.WorldToGrid(x, y, out var c, out var r);
            int col = (int)Math.Floor(c), row = (int)Math.Floor(r);
            return WorldMapMath.InGrid(col, row) ? col * 64 + row : -1;
        }

        static void ScheduleIdle()
        {
            if (idle == null) idle = new Timer(_ => Flush(), null, 20000, Timeout.Infinite); else idle.Change(20000, Timeout.Infinite);
        }

        // close the game storage and save what was read (also what the idle timer does)
        public static void Flush()
        {
            lock (gate)
            {
                CloseCasc(true);
            }
        }

        static void CloseCasc(bool save)
        {
            try { lease?.Dispose(); } catch (Exception) { }
            lease = null; tileBuffer = null;
            if (!save) return;
            foreach (var kv in maps) if (kv.Value.Dirty) { WriteFile(kv.Key, kv.Value); kv.Value.Dirty = false; }
        }

        const int Magic = 0x41524541;     // "AERA"
        static void WriteFile(int mapId, MapAreas m)
        {
            try
            {
                var path = CachePath(mapId);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var tmp = path + ".tmp";
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var w = new BinaryWriter(fs, Encoding.UTF8))
                {
                    w.Write(Magic); w.Write(m.Tiles.Count);
                    foreach (var kv in m.Tiles) { w.Write(kv.Key); w.Write(kv.Value == null ? 0 : 256); if (kv.Value != null) foreach (var v in kv.Value) w.Write(v); }
                }
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch (Exception e) { Util.Log("area probe: cannot save: " + e.Message); }
        }

        static void ReadFile(int mapId, MapAreas m)
        {
            try
            {
                var path = CachePath(mapId);
                if (!File.Exists(path)) return;
                using (var r = new BinaryReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite), Encoding.UTF8))
                {
                    if (r.ReadInt32() != Magic) return;
                    int n = r.ReadInt32();
                    if (n < 0 || n > 4096) return;
                    var got = new Dictionary<int, int[]>();
                    for (int i = 0; i < n; i++)
                    {
                        int key = r.ReadInt32(), len = r.ReadInt32();
                        if (len != 0 && len != 256) return;
                        int[] t = null;
                        if (len == 256) { t = new int[256]; for (int k = 0; k < 256; k++) t[k] = r.ReadInt32(); }
                        got[key] = t;
                    }
                    foreach (var kv in got) m.Tiles[kv.Key] = kv.Value;
                }
            }
            catch (Exception) { }
        }
    }
}
