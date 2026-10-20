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
    // client's UiMap tree lists, and (2.91) the open-world maps that are an island or zone of their own (Zephras Isle).
    public sealed class WorldContinent
    {
        public int MapId;                 // the world map id (0 Eastern Kingdoms, 1 Kalimdor, 2991 Zephras Isle ...)
        public int UiMapId;               // the continent's UiMap (for an island: its own zone map)
        public string Name = "", Slug = "";
        public int WdtFileDataId;
        public bool IsContinent;          // a continent-type UiMap shows it; false = an island / zone map of its own
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
        public static Func<byte[]> LiquidTypeSourceForTest;        // self-test: the LiquidType.db2 bytes instead of the game's file
        public static Func<int, byte[]> WdlSourceForTest;          // self-test: the WDL bytes of a FileDataID instead of the game's file (null = none)
        public static Func<WdtTile, MapRender.TileImage> StandInForTest;   // self-test: the low-detail picture of a map tile instead of the continent's Atlas art
        public static int TerrainBuildCountForTest;
        public static event Action<int> BuildChanged;       // on a worker thread: progress, done, failed

        // ---- which continents
        // The open-world maps a player can stand on (Map.db2: no instance, world map type, a world table):
        //  1. the ones a continent-type UiMap shows (Kalimdor, the Eastern Kingdoms), named after that UiMap;
        //  2. 2.91: the others whose top map in the game's own map tree is a zone or an orphan map hung under the world (WoW Forever's Zephras Isle, the
        //     Skyborne start: map 2991, UiMap 2521 "Zephras Isle", a Zone whose parent is Azeroth 947; there is no continent UiMap for it). The top map is the
        //     normal (System 0) map assigned to it whose parent is not assigned to it too; of several, the one with the largest rectangle.
        //     A map no normal UiMap shows (Azshara Crater, Dalaran City, Hyjal Crater, Eastern Kingdoms Preserved, the Scarlet raid phase copy of the
        //     Plaguelands ...) is left out: the game's own map cannot show it, and a live position there has no map to come from. Leftovers (test names
        //     and folders, "nothing to see here") are left out by name.
        public static List<WorldContinent> Continents(MapIndex idx) => idx == null ? new List<WorldContinent>() : ComputeContinents(idx);

        static bool OpenWorld(GameMapInfo gm) => gm != null && gm.WdtFileDataId > 0 && gm.InstanceType == 0 && gm.MapType == 1;

        static List<WorldContinent> ComputeContinents(MapIndex idx)
        {
            var res = new List<WorldContinent>();
            var seen = new HashSet<int>();
            foreach (var ui in idx.UiMaps.Where(m => m.Type == UiMapInfo.Continent && m.System == 0).OrderBy(m => m.Id))
            {
                foreach (var mapId in idx.Set.Assignments.Where(a => a.UiMapId == ui.Id).Select(a => a.MapId).Distinct().OrderBy(i => i))
                {
                    var gm = idx.GameMap(mapId);
                    if (!OpenWorld(gm) || !seen.Add(mapId)) continue;
                    var name = !string.IsNullOrWhiteSpace(ui.Name) ? ui.Name : gm.Name;
                    res.Add(new WorldContinent { MapId = mapId, UiMapId = ui.Id, Name = name, Slug = AtlasHosts.Slug(name), WdtFileDataId = gm.WdtFileDataId, IsContinent = true });
                }
            }
            // the open-world maps with no continent UiMap: an island or a zone of its own (2.91)
            var byMap = idx.Set.Assignments.GroupBy(a => a.MapId).ToDictionary(g => g.Key, g => g.ToList());
            foreach (var gm in idx.Set.Maps.OrderBy(m => m.Id))
            {
                if (!OpenWorld(gm) || seen.Contains(gm.Id) || !byMap.TryGetValue(gm.Id, out var asg)) continue;
                if (MapIndex.IsPlaceholderName(gm.Name) || MapIndex.IsTestDirectory(gm.Directory)) continue;
                var mine = new HashSet<int>(asg.Select(a => a.UiMapId));
                var top = asg.Select(a => new { a, m = idx.Get(a.UiMapId) })
                    .Where(h => h.m != null && h.m.System == 0 && (h.m.Type == UiMapInfo.Zone || h.m.Type == UiMapInfo.Orphan) && !MapIndex.IsPlaceholderName(h.m.Name) && !mine.Contains(h.m.ParentId))
                    .OrderByDescending(h => h.a.Area).ThenBy(h => h.m.Id).FirstOrDefault();
                if (top == null) continue;
                seen.Add(gm.Id);
                res.Add(new WorldContinent { MapId = gm.Id, UiMapId = top.m.Id, Name = top.m.Name.Trim(), Slug = AtlasHosts.Slug(top.m.Name), WdtFileDataId = gm.WdtFileDataId });
            }
            // two maps of the same name keep apart by their map id in the slug
            foreach (var g in res.GroupBy(c => c.Slug).Where(g => g.Count() > 1)) foreach (var c in g.Skip(1)) c.Slug = c.Slug + "-" + c.MapId.ToString(System.Globalization.CultureInfo.InvariantCulture);
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

        public static void ResetForTest() { lock (gate) { states.Clear(); cts.Clear(); tasks.Clear(); tstates.Clear(); tcts.Clear(); ttasks.Clear(); retried.Clear(); tretried.Clear(); rtasks.Clear(); } WorldMapZones.ClearCache(); }

        // "Try again" after a failed build: the failed states are forgotten (a finished build of another continent is not touched)
        public static void ForgetFailures(int mapId) { lock (gate) { if (states.TryGetValue(mapId, out var s) && !s.Running) { states.Remove(mapId); tasks.Remove(mapId); } if (tstates.TryGetValue(mapId, out var t) && !t.Running) { tstates.Remove(mapId); ttasks.Remove(mapId); } } }

        // the reason for the page: a file the install lists but cannot give (BLTE) is almost always a game that is still downloading in the background, or a damaged install
        public static string Why(Exception e)
            => e is IOException && (e.Message ?? "").Contains("BLTE")
                ? "part of your game files could not be read. If the Battle.net app is still downloading or updating the game, let it finish; otherwise use Scan and Repair in the Battle.net app, then try again."
                : e.Message;

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
            using (Activity.Begin("Building the " + cont.Name + " world map", Activity.Kind.Work))   // it resumes next time: closing need not wait
            try
            {
                Interlocked.Increment(ref BuildCountForTest);
                WorldMapCache.RemoveOtherBuilds(key);
                var res = Build(cont.MapId, cont.Name, cont.WdtFileDataId, key, WorldMapCache.MapDir(key, cont.MapId), null, ct, p =>
                {
                    st.Phase = p.Phase; st.Done = p.Done; st.Total = p.Total;
                    Fire(st.MapId);
                }, ContinentArt(cont, MapData.Index));
                st.Result = res; st.Complete = res.Complete; st.Running = false; st.Ms = sw.ElapsedMilliseconds;
                lock (gate) retried.Add(cont.MapId);
                Util.Log("world map: " + cont.Name + " built: " + res.Adt + " map tiles (" + res.Decoded + " decoded, " + res.Reused + " reused, " + res.Missing + " missing" + (res.Unreadable > 0 ? ", " + res.Unreadable + " could not be read" : "") + (res.Pending.Count > 0 ? ", " + res.Pending.Count + " shown with a stand-in for now" : "") + "), " + res.Files + " files, " + (res.Bytes / 1048576) + " MB in " + st.Ms + " ms");
                Fire(st.MapId);
                return res.Complete;
            }
            catch (OperationCanceledException) { st.Cancelled = true; st.Running = false; st.Error = "Building " + cont.Name + " was cancelled. It continues where it stopped the next time."; Fire(st.MapId); return false; }
            catch (Exception e)
            {
                Util.Log("world map " + cont.Name + ": " + e);
                st.Running = false; st.Error = "The " + cont.Name + " map could not be built: " + Why(e); Fire(st.MapId);
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
            using (Activity.Begin("Building the " + cont.Name + " terrain", Activity.Kind.Work))
            try
            {
                Interlocked.Increment(ref TerrainBuildCountForTest);
                WorldMapCache.RemoveOtherBuilds(key);
                var res = BuildTerrain(cont.MapId, cont.Name, cont.WdtFileDataId, key, WorldMapCache.MapDir(key, cont.MapId), null, zr, ct, p =>
                {
                    st.Phase = p.Phase; st.Done = p.Done; st.Total = p.Total;
                    Fire(st.MapId);
                }, !cont.IsContinent);      // 2.91: an island floating in the sky (Zephras Isle): the chunks with no ground are open sea
                st.TerrainResult = res; st.Complete = res.Complete; st.Running = false; st.Ms = sw.ElapsedMilliseconds;
                lock (gate) tretried.Add(cont.MapId);
                Util.Log("world map: " + cont.Name + " terrain built: " + res.Tiles + " tiles (" + res.Written + " read, " + res.Reused + " reused, " + res.Missing + " missing, " + res.Failed + " failed" + (res.Pending.Count > 0 ? "; " + res.LowDetail + " at low detail, " + res.Holes + " placeholders for now" : "") + "), " + res.Zones + " zones, " + (res.Bytes / 1024) + " KB in " + st.Ms + " ms");
                Fire(st.MapId);
                return res.Complete;
            }
            catch (OperationCanceledException) { st.Cancelled = true; st.Running = false; st.Error = "Building the " + cont.Name + " terrain was cancelled. It continues where it stopped the next time."; Fire(st.MapId); return false; }
            catch (Exception e)
            {
                Util.Log("world map terrain " + cont.Name + ": " + e);
                st.Running = false; st.Error = "The " + cont.Name + " terrain could not be built: " + Why(e); Fire(st.MapId);
                return false;
            }
        }

        // The work itself, no state: reads the WDT, the root ADT of each tile and writes the height grids + borders under mapDir\terrain.
        public static TerrainResult BuildTerrain(int mapId, string name, int wdtFdid, string key, string mapDir, Func<WdtTile, bool> filter, ZoneResolver zr, CancellationToken ct, Action<PyramidProgress> progress, bool sky = false)
        {
            CascStorage casc = null;
            try
            {
                List<WdtTile> tiles; Func<WdtTile, byte[]> load; LiquidKinds kinds = null; WdlFile wdl = null;
                if (WdtSourceForTest != null)
                {
                    var wf = WdtFile.Parse(WdtSourceForTest(mapId));
                    if (!wf.Ok) throw new InvalidDataException(wf.Problem);
                    tiles = wf.Tiles.Where(t => t.MinimapFdid != 0 && t.RootFdid != 0).ToList();
                    load = t => AdtSourceForTest?.Invoke(t.RootFdid);
                    if (LiquidTypeSourceForTest != null) kinds = LiquidKinds.FromDb2(LiquidTypeSourceForTest());
                    if (WdlSourceForTest != null && wf.WdlFdid > 0) { var wb = WdlSourceForTest(wf.WdlFdid); if (wb != null) wdl = WdlFile.Parse(wb); }
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
                    try
                    {
                        var lk = casc.ResolveFileIds(new HashSet<int> { MapTables.LiquidType.FileDataId });
                        kinds = LiquidKinds.FromDb2(lk.TryGetValue(MapTables.LiquidType.FileDataId, out var lck) ? casc.ReadByContentKey(lck) : null);
                        if (kinds.Problem != null) Util.Log("world map terrain: " + kinds.Problem + "; the well-known liquid ids are used");
                    }
                    catch (Exception e) { Util.Log("world map terrain: liquid types: " + e.Message); }
                    load = t =>
                    {
                        if (!keys.TryGetValue(t.RootFdid, out var ck)) throw new FileNotThereException("unlisted", "the terrain file");
                        return c.ReadByContentKey(ck) ?? throw new FileNotThereException("missing", "the terrain file");
                    };
                    // the low-resolution heights (one small file): the stand-in of a tile whose own terrain file is not readable yet
                    if (wf.WdlFdid > 0)
                    {
                        try
                        {
                            var wk2 = casc.ResolveFileIds(new HashSet<int> { wf.WdlFdid });
                            var wb = wk2.TryGetValue(wf.WdlFdid, out var wck2) ? casc.ReadByContentKey(wck2) : null;
                            if (wb != null) { wdl = WdlFile.Parse(wb); if (!wdl.Ok) { Util.Log("world map terrain: " + name + ": " + wdl.Problem); wdl = null; } }
                        }
                        catch (Exception e) { Util.Log("world map terrain: " + name + ": the low-resolution heights could not be read: " + WorldMapPending.Short(e)); wdl = null; }
                    }
                }
                tiles = WdtFile.WithoutStrays(tiles, 12, null);
                if (filter != null) tiles = tiles.Where(filter).ToList();
                if (tiles.Count == 0) throw new InvalidDataException(name + " has no terrain tiles");
                var w = wdl != null && wdl.Ok ? wdl : null;
                return WorldMapTerrain.Build(mapId, key, tiles, load, mapDir, zr, progress, ct, kinds, w == null ? (Func<WdtTile, TerrainTile>)null : t => w.Tile(t.Col, t.Row), WorldMapPending.StorageStamp(WowRoot()), sky);
            }
            finally { casc?.Dispose(); }
        }

        // The work itself, no state: reads the WDT, the minimaps and writes the pyramid under mapDir. `filter` limits it to some map tiles (a test of a region).
        public static PyramidResult Build(int mapId, string name, int wdtFdid, string key, string mapDir, Func<WdtTile, bool> filter, CancellationToken ct, Action<PyramidProgress> progress,
            Func<WdtTile, MapRender.TileImage> standIn = null)
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
                        if (!k.TryGetValue(fid, out var ck)) throw new FileNotThereException("unlisted", "the minimap tile");
                        var blp = c.ReadByContentKey(ck) ?? throw new FileNotThereException("missing", "the minimap tile");
                        var px = Blp.Decode(blp, out int w, out int h);
                        return new MapRender.TileImage { Bgra = px, Width = w, Height = h };
                    };
                }
                var stray = new List<WdtTile>();
                tiles = WdtFile.WithoutStrays(tiles, 12, stray);
                if (stray.Count > 0) Util.Log("world map: " + name + ": " + stray.Count + " stray map tile(s) left out");
                if (filter != null) tiles = tiles.Where(filter).ToList();
                if (tiles.Count == 0) throw new InvalidDataException(name + " has no minimap tiles");
                return WorldMapPyramid.Build(mapId, name, key, tiles, t => tileOf(t.MinimapFdid), mapDir, progress, ct, StandInForTest ?? standIn, WorldMapPending.StorageStamp(WowRoot()));
            }
            finally { casc?.Dispose(); }
        }

        // ---------------------------------------------------------------- stand-ins: the continent's own Atlas map art, under a tile that cannot be read yet
        // The continent picture (MapRender: made from the game's world map art) is decoded once, the first time a tile needs it; a map tile's square of it is
        // found from the world position of the tile's corners (the continent map is a linear, axis-aligned view of the world) and scaled up bilinearly.
        public static Func<WdtTile, MapRender.TileImage> ContinentArt(WorldContinent cont, MapIndex idx)
        {
            if (StandInForTest != null) return StandInForTest;
            if (cont == null || idx == null || cont.UiMapId <= 0) return null;
            byte[] art = null; int aw = 0, ah = 0; bool tried = false; var lk = new object();
            return t =>
            {
                lock (lk)
                {
                    if (!tried)
                    {
                        tried = true;
                        try
                        {
                            if (!idx.HasArt(cont.UiMapId)) return null;
                            var path = MapRender.TryGet(cont.UiMapId);
                            if (path == null)
                            {
                                using (var done = new ManualResetEventSlim(false)) { MapRender.Request(cont.UiMapId, p => { path = p; try { done.Set(); } catch (Exception) { } }); done.Wait(30000); }
                            }
                            if (path != null && MapRender.Load(path) is System.Windows.Media.Imaging.BitmapSource bs)
                            {
                                var f = new System.Windows.Media.Imaging.FormatConvertedBitmap(bs, System.Windows.Media.PixelFormats.Bgra32, null, 0);
                                aw = f.PixelWidth; ah = f.PixelHeight; art = new byte[aw * ah * 4];
                                f.CopyPixels(art, aw * 4, 0);
                            }
                        }
                        catch (Exception e) { Util.Log("world map: continent art for stand-ins: " + WorldMapPending.Short(e)); art = null; }
                    }
                }
                if (art == null) return null;
                // the tile's corners on the continent map: a point just inside each edge, extended to the edge (a corner exactly on the map's border may fall outside it)
                double ty = WorldMapMath.TileYards, inset = ty * 0.02;
                double xN = (32 - t.Row) * ty, yW = (32 - t.Col) * ty;
                var a = idx.WorldToMap(cont.MapId, xN - inset, yW - inset, cont.UiMapId);
                var b = idx.WorldToMap(cont.MapId, xN - ty + inset, yW - ty + inset, cont.UiMapId);
                if (a == null || b == null) return null;
                double k = ty / (ty - 2 * inset);
                double dfx = (b.Value.Fx - a.Value.Fx) * k, dfy = (b.Value.Fy - a.Value.Fy) * k;
                double fx0 = a.Value.Fx - dfx * (inset / ty), fy0 = a.Value.Fy - dfy * (inset / ty);
                const int N = WorldMapMath.MinimapPx;
                var o = new byte[N * N * 4];
                for (int y = 0; y < N; y++)
                {
                    double sy = (fy0 + dfy * (y + 0.5) / N) * ah - 0.5;
                    int y0 = Math.Max(0, Math.Min(ah - 1, (int)Math.Floor(sy))), y1 = Math.Min(ah - 1, y0 + 1); double wy = Math.Max(0, Math.Min(1, sy - y0));
                    for (int x = 0; x < N; x++)
                    {
                        double sx = (fx0 + dfx * (x + 0.5) / N) * aw - 0.5;
                        int x0 = Math.Max(0, Math.Min(aw - 1, (int)Math.Floor(sx))), x1 = Math.Min(aw - 1, x0 + 1); double wx = Math.Max(0, Math.Min(1, sx - x0));
                        int i = (y * N + x) * 4, p00 = (y0 * aw + x0) * 4, p01 = (y0 * aw + x1) * 4, p10 = (y1 * aw + x0) * 4, p11 = (y1 * aw + x1) * 4;
                        for (int c = 0; c < 3; c++)
                            o[i + c] = (byte)Math.Round((art[p00 + c] * (1 - wx) + art[p01 + c] * wx) * (1 - wy) + (art[p10 + c] * (1 - wx) + art[p11 + c] * wx) * wy);
                        o[i + 3] = 255;
                    }
                }
                return new MapRender.TileImage { Bgra = o, Width = N, Height = N };
            };
        }

        // ---------------------------------------------------------------- tiles that could not be read yet: what is pending, and trying them again
        static readonly HashSet<int> retried = new HashSet<int>(), tretried = new HashSet<int>();      // maps whose stand-ins were tried in this run of the app
        static readonly Dictionary<int, Task<RetryResult>> rtasks = new Dictionary<int, Task<RetryResult>>();
        public static int RetryCountForTest;

        public sealed class RetryResult { public int Recovered, StillPending; public string Error; public bool Ran; }

        // the stand-ins of a continent now: the pictures' and (when it was read) the terrain's
        public static List<PendingTile> PendingOf(int mapId)
        {
            var l = new List<PendingTile>();
            var m = WorldMapCache.Built(mapId); if (m != null) l.AddRange(m.Pending);
            var t = WorldMapCache.TerrainBuilt(mapId); if (t != null) l.AddRange(t.Pending);
            return l;
        }

        public static bool Retrying(int mapId) { lock (gate) return rtasks.TryGetValue(mapId, out var t) && !t.IsCompleted; }

        // worth trying again by itself: stand-ins are there and either they were not tried yet in this run of the app, or the game's storage changed since
        public static bool ShouldAutoRetry(int mapId)
        {
            var m = WorldMapCache.Built(mapId); var t = WorldMapCache.TerrainBuilt(mapId);
            bool pics = m != null && m.Pending.Count > 0, ter = t != null && t.Pending.Count > 0;
            if (!pics && !ter) return false;
            if (Retrying(mapId)) return false;
            string now = WorldMapPending.StorageStamp(WowRoot());
            lock (gate)
            {
                if (pics && (!retried.Contains(mapId) || (now.Length > 0 && now != m.Stamp))) return true;
                if (ter && (!tretried.Contains(mapId) || (now.Length > 0 && now != t.Stamp))) return true;
            }
            return false;
        }

        // reads the stand-in tiles of a continent again (pictures and terrain, whichever has some), in the background; a running retry is joined.
        // The map stays usable meanwhile (the finished manifests stay until the new ones are written).
        public static Task<RetryResult> RetryPending(int mapId)
        {
            lock (gate)
            {
                if (rtasks.TryGetValue(mapId, out var running) && !running.IsCompleted) return running;
                var cont = Continent(MapData.Index, mapId);
                var key = WorldMapCache.BuildKey;
                var zr = ZoneResolver.FromIndex(MapData.Index);
                var idx = MapData.Index;
                var task = Task.Factory.StartNew(() =>
                {
                    var r = new RetryResult();
                    if (cont == null) { r.Error = "The game has no world map for this continent."; return r; }
                    try { Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; } catch (Exception) { }
                    Interlocked.Increment(ref RetryCountForTest);
                    try
                    {
                        var m = WorldMapCache.Built(mapId, key);
                        if (m != null && m.Pending.Count > 0)
                        {
                            r.Ran = true;
                            var res = Build(cont.MapId, cont.Name, cont.WdtFileDataId, key, WorldMapCache.MapDir(key, cont.MapId), null, CancellationToken.None, null, ContinentArt(cont, idx));
                            r.Recovered += res.Recovered; r.StillPending += res.Pending.Count;
                            Util.Log("world map: " + cont.Name + ": retried " + m.Pending.Count + " picture tile(s): " + res.Recovered + " read now, " + res.Pending.Count + " still not readable");
                        }
                        lock (gate) retried.Add(mapId);
                        var t = WorldMapCache.TerrainBuilt(mapId, key);
                        if (t != null && t.Pending.Count > 0)
                        {
                            r.Ran = true;
                            var res = BuildTerrain(cont.MapId, cont.Name, cont.WdtFileDataId, key, WorldMapCache.MapDir(key, cont.MapId), null, zr, CancellationToken.None, null, !cont.IsContinent);
                            r.Recovered += res.Recovered; r.StillPending += res.Pending.Count;
                            Util.Log("world map: " + cont.Name + ": retried " + t.Pending.Count + " terrain tile(s): " + res.Recovered + " read now, " + res.Pending.Count + " still not readable");
                        }
                        lock (gate) tretried.Add(mapId);
                    }
                    catch (Exception e) { r.Error = Why(e); Util.Log("world map: " + cont.Name + ": retry failed: " + WorldMapPending.Short(e)); }
                    if (r.Recovered > 0) Interlocked.Increment(ref revision);
                    Fire(mapId);
                    return r;
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                rtasks[mapId] = task;
                return task;
            }
        }

        // bumps when a retry replaced stand-ins: the page adds it to its file addresses so the browser does not show the old pictures from its cache
        static int revision;
        public static int Revision => revision;
    }
}
