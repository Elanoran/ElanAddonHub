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
        static CascStorage casc;
        static Timer idle;

        static string CachePath(int mapId)
            => Path.Combine(CacheDirOverride ?? Path.Combine(Util.DataDir, "cache"), "areas-" + MapData.BuildStamp + "-" + mapId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".bin");

        public static void ResetForTest()
        {
            lock (gate) { maps.Clear(); CloseCasc(false); ReadsForTest = 0; }
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
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return false;
                var gm = MapData.Index.GameMap(mapId);
                return gm != null && gm.WdtFileDataId > 0;
            }
            catch (Exception) { return false; }
        }

        static int[] Load(int mapId, MapAreas m, int col, int row)
        {
            ReadsForTest++;
            if (TileSourceForTest != null) return TileSourceForTest(mapId, col, row);
            try
            {
                var gm = MapData.Index.GameMap(mapId);
                if (casc == null)
                {
                    casc = CascStorage.Open(MapData.WowRoot(), MapData.Flavor() ?? "_classic_beta_");
                    casc.ZeroFillEncrypted = true;
                }
                if (m.Roots == null)
                {
                    m.Roots = new Dictionary<int, int>();
                    var wk = casc.ResolveFileIds(new HashSet<int> { gm.WdtFileDataId });
                    if (wk.TryGetValue(gm.WdtFileDataId, out var wck))
                    {
                        var wdt = WdtFile.Parse(casc.ReadByContentKey(wck));
                        if (wdt.Ok) foreach (var t in wdt.Tiles) if (t.RootFdid != 0) m.Roots[t.Col * 64 + t.Row] = t.RootFdid;
                    }
                }
                if (!m.Roots.TryGetValue(col * 64 + row, out var fdid)) return null;
                var keys = casc.ResolveFileIds(new HashSet<int> { fdid });
                if (!keys.TryGetValue(fdid, out var ck)) return null;
                var adt = AdtFile.Parse(casc.ReadByContentKey(ck));
                if (!adt.Ok) return null;
                var a = new int[256];
                for (int i = 0; i < 256; i++) a[i] = adt.Chunks[i] == null ? 0 : Math.Max(0, adt.Chunks[i].AreaId);
                return a;
            }
            catch (Exception e) { Util.Log("area probe: " + e.Message); CloseCasc(false); return null; }
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
            try { casc?.Dispose(); } catch (Exception) { }
            casc = null;
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
