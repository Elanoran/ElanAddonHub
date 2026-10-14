using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Maps;

namespace ElansAddonHub.Services.WorldMap
{
    // A continent the client has a world table (WDT) and minimaps for: Kalimdor and the Eastern Kingdoms, plus any other continent-type map the
    // client's UiMap tree lists.
    public sealed class WorldContinent
    {
        public int MapId;                 // the world map id (0 Eastern Kingdoms, 1 Kalimdor ...)
        public int UiMapId;               // the continent's UiMap
        public string Name = "", Slug = "";
        public int WdtFileDataId;
    }

    // What a build of one continent is doing right now
    public sealed class WorldBuildState
    {
        public int MapId;
        public bool Running, Complete, Cancelled;
        public bool Terrain;                          // true = the height grids and zone borders, false = the pictures
        public string Phase = "", Error;
        public int Done, Total;
        public string Name = "";
        public long Ms;
        public PyramidResult Result;
        public TerrainResult TerrainResult;
        public string Text
        {
            get
            {
                if (Error != null) return Error;
                if (Complete) return "";
                if (Terrain) return Phase == "borders" ? "Building " + Name + " terrain: drawing the zone borders" : "Building " + Name + " terrain: " + Done.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " / " + Total.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " tiles";
                if (Phase == "levels") return "Building " + Name + ": finishing the zoom levels (" + Done.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " / " + Total.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + ")";
                return "Building " + Name + ": " + Done.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " / " + Total.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " tiles";
            }
        }
    }

    // ============================================================================================================================
    // Makes the world map pictures of a continent from the player's own WoW files (read-only), on a background thread, once per game build:
    // the continent's WDT (FileDataID from Map.db2) names the minimap texture of each of its map tiles, those are decoded one by one and cut into the
    // tile pyramid (WorldMapPyramid). Cancellable, with progress; a cancelled build resumes where it stopped.
    // ============================================================================================================================
    public static class WorldMapService
    {
        public static Func<string> WowRoot = () => MapData.WowRoot();
        public static Func<string> Flavor = () => MapData.Flavor();
        // self-test hooks: the WDT bytes of a map and the BGRA minimap of a FileDataID instead of the game's files
        public static Func<int, byte[]> WdtSourceForTest;
        public static Func<int, MapRender.TileImage> TileSourceForTest;
        public static int BuildCountForTest;

        static readonly object gate = new object();
        static readonly Dictionary<int, WorldBuildState> states = new Dictionary<int, WorldBuildState>();
        static readonly Dictionary<int, CancellationTokenSource> cts = new Dictionary<int, CancellationTokenSource>();
        static readonly Dictionary<int, Task<bool>> tasks = new Dictionary<int, Task<bool>>();
        static readonly Dictionary<int, WorldBuildState> tstates = new Dictionary<int, WorldBuildState>();
        static readonly Dictionary<int, CancellationTokenSource> tcts = new Dictionary<int, CancellationTokenSource>();
        static readonly Dictionary<int, Task<bool>> ttasks = new Dictionary<int, Task<bool>>();
        public static Func<int, byte[]> AdtSourceForTest;          // self-test: the root ADT bytes of a FileDataID instead of the game's files
        public static int TerrainBuildCountForTest;
        public static event Action<int> BuildChanged;       // on a worker thread: progress, done, failed

        // ---- which continents
        public static List<WorldContinent> Continents(MapIndex idx)
        {
            var res = new List<WorldContinent>();
            if (idx == null) return res;
            var seen = new HashSet<int>();
            foreach (var ui in idx.UiMaps.Where(m => m.Type == UiMapInfo.Continent && m.System == 0).OrderBy(m => m.Id))
            {
                foreach (var mapId in idx.Set.Assignments.Where(a => a.UiMapId == ui.Id).Select(a => a.MapId).Distinct().OrderBy(i => i))
                {
                    var gm = idx.GameMap(mapId);
                    if (gm == null || gm.WdtFileDataId <= 0 || gm.InstanceType != 0 || gm.MapType != 1 || !seen.Add(mapId)) continue;
                    var name = !string.IsNullOrWhiteSpace(ui.Name) ? ui.Name : gm.Name;
                    res.Add(new WorldContinent { MapId = mapId, UiMapId = ui.Id, Name = name, Slug = AtlasHosts.Slug(name), WdtFileDataId = gm.WdtFileDataId });
                }
            }
            return res.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static WorldContinent Continent(MapIndex idx, int mapId) => Continents(idx).FirstOrDefault(c => c.MapId == mapId);

        // ---- state
        public static WorldBuildState StateOf(int mapId)
        {
            lock (gate) return states.TryGetValue(mapId, out var s) ? s : null;
        }

        public static bool IsBuilt(int mapId) => WorldMapCache.Built(mapId) != null;

        public static void Cancel(int mapId)
        {
            lock (gate) { if (cts.TryGetValue(mapId, out var c)) { try { c.Cancel(); } catch (Exception) { } }  if (tcts.TryGetValue(mapId, out var tc)) { try { tc.Cancel(); } catch (Exception) { } } }
        }

        public static void CancelAll() { lock (gate) { foreach (var c in cts.Values.Concat(tcts.Values)) { try { c.Cancel(); } catch (Exception) { } } } }

        public static void ResetForTest() { lock (gate) { states.Clear(); cts.Clear(); tasks.Clear(); tstates.Clear(); tcts.Clear(); ttasks.Clear(); } WorldMapZones.ClearCache(); }

        static void Fire(int mapId) { try { BuildChanged?.Invoke(mapId); } catch (Exception) { } }

        // The pyramid of the continent exists when the task returns true. A build already running is joined, not repeated.
        public static Task<bool> EnsureBuilt(int mapId)
        {
            lock (gate)
            {
                if (tasks.TryGetValue(mapId, out var t) && !t.IsCompleted) return t;
                var idx = MapData.Index;
                var cont = Continent(idx, mapId);
                var st = new WorldBuildState { MapId = mapId, Name = cont?.Name ?? ("map " + mapId), Running = true, Phase = "tiles" };
                states[mapId] = st;
                var src = new CancellationTokenSource();
                cts[mapId] = src;
                if (cont == null) { st.Running = false; st.Error = "The game has no world map for this continent."; var f = Task.FromResult(false); tasks[mapId] = f; Fire(mapId); return f; }
                if (WorldMapCache.Built(mapId) != null) { st.Running = false; st.Complete = true; var f = Task.FromResult(true); tasks[mapId] = f; return f; }
                var key = WorldMapCache.BuildKey;
                var task = Task.Factory.StartNew(() => Run(cont, key, st, src.Token), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                tasks[mapId] = task;
                Fire(mapId);
                return task;
            }
        }

        static bool Run(WorldContinent cont, string key, WorldBuildState st, CancellationToken ct)
        {
            try { Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; } catch (Exception) { }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                Interlocked.Increment(ref BuildCountForTest);
                WorldMapCache.RemoveOtherBuilds(key);
                var res = Build(cont.MapId, cont.Name, cont.WdtFileDataId, key, WorldMapCache.MapDir(key, cont.MapId), null, ct, p =>
                {
                    st.Phase = p.Phase; st.Done = p.Done; st.Total = p.Total;
                    Fire(st.MapId);
                });
                st.Result = res; st.Complete = res.Complete; st.Running = false; st.Ms = sw.ElapsedMilliseconds;
                Util.Log("world map: " + cont.Name + " built: " + res.Adt + " map tiles (" + res.Decoded + " decoded, " + res.Reused + " reused, " + res.Missing + " missing), " + res.Files + " files, " + (res.Bytes / 1048576) + " MB in " + st.Ms + " ms");
                Fire(st.MapId);
                return res.Complete;
            }
            catch (OperationCanceledException) { st.Cancelled = true; st.Running = false; st.Error = "Building " + cont.Name + " was cancelled. It continues where it stopped the next time."; Fire(st.MapId); return false; }
            catch (Exception e)
            {
                Util.Log("world map " + cont.Name + ": " + e);
                st.Running = false; st.Error = "The " + cont.Name + " map could not be built: " + e.Message; Fire(st.MapId);
                return false;
            }
        }

        // ---------------------------------------------------------------- terrain: height grids + zone borders (WorldMapTerrain, WorldMapZones)
        public static WorldBuildState TerrainStateOf(int mapId)
        {
            lock (gate) return tstates.TryGetValue(mapId, out var s) ? s : null;
        }

        // the state the page should show progress for: a running terrain build, else a running picture build
        public static WorldBuildState RunningStateOf(int mapId)
        {
            lock (gate)
            {
                if (tstates.TryGetValue(mapId, out var t) && t.Running) return t;
                if (states.TryGetValue(mapId, out var s) && s.Running) return s;
                return null;
            }
        }

        public static bool TerrainBuilt(int mapId) => WorldMapCache.TerrainBuilt(mapId) != null;

        // The height grids and zone borders of the continent exist when the task returns true. A build already running is joined, not repeated.
        public static Task<bool> EnsureTerrain(int mapId)
        {
            lock (gate)
            {
                if (ttasks.TryGetValue(mapId, out var t) && !t.IsCompleted) return t;
                var idx = MapData.Index;
                var cont = Continent(idx, mapId);
                var st = new WorldBuildState { MapId = mapId, Terrain = true, Name = cont?.Name ?? ("map " + mapId), Running = true, Phase = "terrain" };
                tstates[mapId] = st;
                var src = new CancellationTokenSource();
                tcts[mapId] = src;
                if (cont == null) { st.Running = false; st.Error = "The game has no world map for this continent."; var f = Task.FromResult(false); ttasks[mapId] = f; Fire(mapId); return f; }
                if (WorldMapCache.TerrainBuilt(mapId) != null) { st.Running = false; st.Complete = true; var f = Task.FromResult(true); ttasks[mapId] = f; return f; }
                var key = WorldMapCache.BuildKey;
                var zr = ZoneResolver.FromIndex(idx);
                var task = Task.Factory.StartNew(() => RunTerrain(cont, key, st, zr, src.Token), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                ttasks[mapId] = task;
                Fire(mapId);
                return task;
            }
        }

        static bool RunTerrain(WorldContinent cont, string key, WorldBuildState st, ZoneResolver zr, CancellationToken ct)
        {
            try { Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; } catch (Exception) { }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                Interlocked.Increment(ref TerrainBuildCountForTest);
                WorldMapCache.RemoveOtherBuilds(key);
                var res = BuildTerrain(cont.MapId, cont.Name, cont.WdtFileDataId, key, WorldMapCache.MapDir(key, cont.MapId), null, zr, ct, p =>
                {
                    st.Phase = p.Phase; st.Done = p.Done; st.Total = p.Total;
                    Fire(st.MapId);
                });
                st.TerrainResult = res; st.Complete = res.Complete; st.Running = false; st.Ms = sw.ElapsedMilliseconds;
                Util.Log("world map: " + cont.Name + " terrain built: " + res.Tiles + " tiles (" + res.Written + " read, " + res.Reused + " reused, " + res.Missing + " missing, " + res.Failed + " failed), " + res.Zones + " zones, " + (res.Bytes / 1024) + " KB in " + st.Ms + " ms");
                Fire(st.MapId);
                return res.Complete;
            }
            catch (OperationCanceledException) { st.Cancelled = true; st.Running = false; st.Error = "Building the " + cont.Name + " terrain was cancelled. It continues where it stopped the next time."; Fire(st.MapId); return false; }
            catch (Exception e)
            {
                Util.Log("world map terrain " + cont.Name + ": " + e);
                st.Running = false; st.Error = "The " + cont.Name + " terrain could not be built: " + e.Message; Fire(st.MapId);
                return false;
            }
        }

        // The work itself, no state: reads the WDT, the root ADT of each tile and writes the height grids + borders under mapDir\terrain.
        public static TerrainResult BuildTerrain(int mapId, string name, int wdtFdid, string key, string mapDir, Func<WdtTile, bool> filter, ZoneResolver zr, CancellationToken ct, Action<PyramidProgress> progress)
        {
            CascStorage casc = null;
            try
            {
                List<WdtTile> tiles; Func<WdtTile, byte[]> load;
                if (WdtSourceForTest != null)
                {
                    var wf = WdtFile.Parse(WdtSourceForTest(mapId));
                    if (!wf.Ok) throw new InvalidDataException(wf.Problem);
                    tiles = wf.Tiles.Where(t => t.MinimapFdid != 0 && t.RootFdid != 0).ToList();
                    load = t => AdtSourceForTest?.Invoke(t.RootFdid);
                }
                else
                {
                    var root = WowRoot();
                    if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) throw new IOException("the WoW folder is not set");
                    casc = CascStorage.Open(root, Flavor() ?? "_classic_beta_");
                    casc.ZeroFillEncrypted = true;
                    var wk = casc.ResolveFileIds(new HashSet<int> { wdtFdid });
                    if (!wk.TryGetValue(wdtFdid, out var wck)) throw new IOException("the world table of " + name + " is not in this game install");
                    var wf = WdtFile.Parse(casc.ReadByContentKey(wck));
                    if (!wf.Ok) throw new InvalidDataException(wf.Problem);
                    tiles = wf.Tiles.Where(t => t.MinimapFdid != 0 && t.RootFdid != 0).ToList();
                    casc.ZeroFillEncrypted = false;
                    var keys = casc.ResolveFileIds(new HashSet<int>(tiles.Select(t => t.RootFdid)));
                    var c = casc;
                    load = t => keys.TryGetValue(t.RootFdid, out var ck) ? c.ReadByContentKey(ck) : null;
                }
                tiles = WdtFile.WithoutStrays(tiles, 12, null);
                if (filter != null) tiles = tiles.Where(filter).ToList();
                if (tiles.Count == 0) throw new InvalidDataException(name + " has no terrain tiles");
                return WorldMapTerrain.Build(mapId, key, tiles, load, mapDir, zr, progress, ct);
            }
            finally { casc?.Dispose(); }
        }

        // The work itself, no state: reads the WDT, the minimaps and writes the pyramid under mapDir. `filter` limits it to some map tiles (a test of a region).
        public static PyramidResult Build(int mapId, string name, int wdtFdid, string key, string mapDir, Func<WdtTile, bool> filter, CancellationToken ct, Action<PyramidProgress> progress)
        {
            byte[] wdtBytes; Func<int, MapRender.TileImage> tileOf; CascStorage casc = null;
            Dictionary<int, byte[]> keys = null;
            try
            {
                List<WdtTile> tiles;
                if (WdtSourceForTest != null)
                {
                    wdtBytes = WdtSourceForTest(mapId);
                    var wf = WdtFile.Parse(wdtBytes);
                    if (!wf.Ok) throw new InvalidDataException(wf.Problem);
                    tiles = wf.Tiles.Where(t => t.MinimapFdid != 0).ToList();
                    tileOf = TileSourceForTest ?? (f => null);
                }
                else
                {
                    var root = WowRoot();
                    if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) throw new IOException("the WoW folder is not set");
                    casc = CascStorage.Open(root, Flavor() ?? "_classic_beta_");
                    casc.ZeroFillEncrypted = true;
                    var wk = casc.ResolveFileIds(new HashSet<int> { wdtFdid });
                    if (!wk.TryGetValue(wdtFdid, out var wck)) throw new IOException("the world table of " + name + " is not in this game install");
                    wdtBytes = casc.ReadByContentKey(wck);
                    var wf = WdtFile.Parse(wdtBytes);
                    if (!wf.Ok) throw new InvalidDataException(wf.Problem);
                    tiles = wf.Tiles.Where(t => t.MinimapFdid != 0).ToList();
                    casc.ZeroFillEncrypted = false;
                    keys = casc.ResolveFileIds(new HashSet<int>(tiles.Select(t => t.MinimapFdid)));
                    var c = casc; var k = keys;
                    tileOf = fid =>
                    {
                        if (!k.TryGetValue(fid, out var ck)) return null;
                        var blp = c.ReadByContentKey(ck);
                        if (blp == null) return null;
                        var px = Blp.Decode(blp, out int w, out int h);
                        return new MapRender.TileImage { Bgra = px, Width = w, Height = h };
                    };
                }
                var stray = new List<WdtTile>();
                tiles = WdtFile.WithoutStrays(tiles, 12, stray);
                if (stray.Count > 0) Util.Log("world map: " + name + ": " + stray.Count + " stray map tile(s) left out");
                if (filter != null) tiles = tiles.Where(filter).ToList();
                if (tiles.Count == 0) throw new InvalidDataException(name + " has no minimap tiles");
                return WorldMapPyramid.Build(mapId, name, key, tiles, t => tileOf(t.MinimapFdid), mapDir, progress, ct);
            }
            finally { casc?.Dispose(); }
        }
    }
}
