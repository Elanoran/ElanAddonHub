using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ElansAddonHub.Services.WorldMap
{
    // ============================================================================================================================
    // Runs the 3D buildings of a continent (WorldMapBuildings): counts them, tells the card what it will cost, and builds the shapes on one background thread
    // at below-normal priority, nearest to the camera first, pausable and resumable. One job per continent; the page's messages reach it through
    // WorldMapBuildingsHost. Nothing is read until the person said "Build now" - the scan only looks at the obj0 files and the roots of the buildings.
    // ============================================================================================================================
    public static class BuildingsService
    {
        public static Func<IBuildingSource> SourceForTest;                   // self-test: the files of a synthetic install
        public static Func<int, IGroundSampler> GroundForTest;               // self-test: the ground of a continent
        public static int BuildCountForTest, ScanCountForTest, UpgradeCountForTest;
        public static double ModelDelayMsForTest;                            // self-test: a pause between models so Pause / Cancel can be tried
        public static event Action<int> Changed;                              // on a worker thread

        sealed class Job
        {
            public int MapId; public string Name = "", WdtName = "";
            public BuildingsState State;
            public BuildingScan Scan;
            public string Dir;
            public CancellationTokenSource Cts;
            public ManualResetEventSlim Gate = new ManualResetEventSlim(true);
            public volatile bool Paused, Running;
            public volatile string Mode = "off";
            public double FocusX = double.NaN, FocusZ = double.NaN;
            public ConcurrentQueue<int> Fresh = new ConcurrentQueue<int>();
            public Task Task;
            public Dictionary<int, List<float[]>> Positions;
        }

        static readonly object gate = new object();
        static readonly Dictionary<int, Job> jobs = new Dictionary<int, Job>();

        static Job JobOf(int mapId)
        {
            lock (gate)
            {
                if (!jobs.TryGetValue(mapId, out var j))
                {
                    var cont = WorldMapService.Continent(Maps.MapData.Index, mapId);
                    j = new Job { MapId = mapId, Name = cont?.Name ?? ("map " + mapId), State = new BuildingsState { MapId = mapId } };
                    j.State.Name = j.Name;
                    jobs[mapId] = j;
                }
                return j;
            }
        }

        static void Fire(int mapId) { try { Changed?.Invoke(mapId); } catch (Exception) { } }
        public static void ResetForTest() { lock (gate) { foreach (var j in jobs.Values) { try { j.Cts?.Cancel(); j.Gate.Set(); } catch (Exception) { } } jobs.Clear(); } }

        public static string DirOf(int mapId) => WorldMapBuildings.Dir(WorldMapCache.MapDir(mapId));
        public static BuildingsState StateOf(int mapId) { lock (gate) return jobs.TryGetValue(mapId, out var j) ? j.State : null; }
        public static BuildingScan ScanOf(int mapId) { lock (gate) return jobs.TryGetValue(mapId, out var j) ? j.Scan : null; }
        public static HashSet<int> Built(int mapId) => WorldMapBuildings.BuiltModels(DirOf(mapId));
        public static List<int> TakeFresh(int mapId)
        {
            var res = new List<int>();
            Job j; lock (gate) jobs.TryGetValue(mapId, out j);
            if (j != null) while (j.Fresh.TryDequeue(out var id)) res.Add(id);
            return res;
        }

        // a finished set of buildings of this mode is on disk
        public static bool IsReady(int mapId, string mode)
        {
            mode = WorldMapBuildings.Normalize(mode);
            if (mode == "off") return false;
            var m = WorldMapBuildings.ReadManifest(DirOf(mapId));
            if (m == null || !m.Valid || m.MapId != mapId || m.BuildKey != WorldMapCache.BuildKey) return false;
            if (!File.Exists(WorldMapBuildings.PlacementsPath(DirOf(mapId)))) return false;
            if (m.Rules < WorldMapBuildings.Rules) return false;        // made by an older group rule: Prepare remakes the models that changed
            return mode == "bridges" ? (m.BridgesDone || m.AllDone) : m.AllDone;
        }

        // ---------------------------------------------------------------- the scan: placements, the roots, the estimate
        // Sets the state to "ask" (with the estimate) or "done" (everything for this mode is on disk already). The terrain must be built (bridges need the ground).
        public static Task Prepare(int mapId, string mode)
        {
            mode = WorldMapBuildings.Normalize(mode);
            var j = JobOf(mapId);
            lock (gate)
            {
                if (j.Running) { j.Mode = mode; j.State.Mode = mode; return j.Task ?? Task.FromResult(0); }
                j.Mode = mode; j.State.Mode = mode; j.State.Error = null;
                if (mode == "off") { j.State.Phase = "idle"; Fire(mapId); return Task.FromResult(0); }
            }
            j.Dir = DirOf(mapId);
            if (IsReady(mapId, mode)) { var m = WorldMapBuildings.ReadManifest(j.Dir); j.State.Phase = "done"; j.State.Done = j.State.Total = mode == "bridges" ? m.Bridges : m.Placements; j.State.BytesOnDisk = WorldMapBuildings.SizeBytes(j.Dir); Fire(mapId); return Task.FromResult(0); }
            // 2.89: a folder finished under an older group rule (WorldMapBuildings.Rules) for this mode is brought up to date without asking again: the few models
            // the new rule makes differently (the big cities) are removed and made again
            var old = WorldMapBuildings.ReadManifest(j.Dir);
            bool upgrade = old != null && old.Valid && old.MapId == mapId && old.BuildKey == WorldMapCache.BuildKey && old.Rules < WorldMapBuildings.Rules
                && File.Exists(WorldMapBuildings.PlacementsPath(j.Dir)) && (mode == "bridges" ? (old.BridgesDone || old.AllDone) : old.AllDone);
            j.State.Phase = "scanning"; Fire(mapId);
            return Task.Run(() =>
            {
                try
                {
                    Interlocked.Increment(ref ScanCountForTest);
                    EnsureScan(j, CancellationToken.None);
                    if (upgrade)
                    {
                        int removed = WorldMapBuildings.RemoveStale(j.Scan, j.Dir, old.Rules);
                        Interlocked.Increment(ref UpgradeCountForTest);
                        Util.Log("world map buildings " + j.Name + ": made by group rule " + old.Rules + ", " + removed + " models are made again under rule " + WorldMapBuildings.Rules);
                        FillEstimate(j);
                        // a folder with every building made is remade as such (a "bridges" run would leave the removed city models missing under a manifest that says all are done)
                        var t = Start(mapId, old.AllDone ? "all" : mode);
                        Fire(mapId);
                        t?.Wait();
                        return;
                    }
                    FillEstimate(j);
                    j.State.Phase = "ask";
                }
                catch (Exception e)
                {
                    Util.Log("world map buildings " + j.Name + ": " + e);
                    j.State.Error = "The buildings of " + j.Name + " could not be counted: " + WorldMapService.Why(e); j.State.Phase = "error";
                }
                Fire(mapId);
            });
        }

        static void FillEstimate(Job j)
        {
            if (j.Scan == null) return;
            j.State.Estimate = WorldMapBuildings.Estimate(j.Scan, j.Mode, j.Dir);
            j.State.BytesOnDisk = WorldMapBuildings.SizeBytes(j.Dir);
            var e = j.State.Estimate;
            j.State.Total = e.Buildings;
            j.State.Done = DoneBuildings(j);
        }

        static int DoneBuildings(Job j)
        {
            if (j.Scan == null) return 0;
            var built = WorldMapBuildings.BuiltModels(j.Dir); int n = 0;
            for (int i = 0; i < j.Scan.Placements.Count; i++) if (WorldMapBuildings.Wants(j.Mode, j.Scan.Bridge[i]) && built.Contains((int)j.Scan.Placements[i].NameId)) n++;
            return n;
        }

        static void LoadStats(Job j)
        {
            // measured costs of an earlier build (any continent of this game build) replace the defaults
            try
            {
                var root = Path.Combine(WorldMapCache.Root, WorldMapCache.BuildKey);
                if (!Directory.Exists(root)) return;
                foreach (var d in Directory.GetDirectories(root))
                {
                    var m = WorldMapBuildings.ReadManifest(WorldMapBuildings.Dir(d));
                    if (m != null && m.Valid && m.SecPerGroup > 0 && m.BytesPerGroup > 0) { WorldMapBuildings.SecondsPerGroup = m.SecPerGroup; WorldMapBuildings.BytesPerGroup = m.BytesPerGroup; if (m.SecBase > 0) WorldMapBuildings.SecondsBase = m.SecBase; break; }
                }
            }
            catch (Exception) { }
        }

        static void EnsureScan(Job j, CancellationToken ct)
        {
            if (j.Scan != null) return;
            LoadStats(j);
            using (var src = OpenSource(j, out var tiles))
            {
                IGroundSampler ground = GroundForTest != null ? GroundForTest(j.MapId) : new TerrainSampler(WorldMapCache.TerrainDir(WorldMapCache.BuildKey, j.MapId));
                var scan = WorldMapBuildings.Scan(tiles, src, ground, ct);
                if (scan.Placements.Count == 0) throw new InvalidDataException(j.Name + " has no buildings in this game install");
                lock (gate) j.Scan = scan;
            }
        }

        static IBuildingSource OpenSource(Job j, out List<WdtTile> tiles)
        {
            if (SourceForTest != null)
            {
                var wf = WdtFile.Parse(WorldMapService.WdtSourceForTest?.Invoke(j.MapId));
                if (!wf.Ok) throw new InvalidDataException(wf.Problem);
                tiles = wf.Tiles.Where(t => t.RootFdid != 0).ToList();
                tiles = WdtFile.WithoutStrays(tiles, 12, null);
                return SourceForTest();
            }
            var root = WorldMapService.WowRoot();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) throw new IOException("the WoW folder is not set");
            var cont = WorldMapService.Continent(Maps.MapData.Index, j.MapId);
            if (cont == null) throw new IOException("the game has no world map for this continent");
            var casc = CascStorage.Open(root, WorldMapService.Flavor() ?? "_classic_beta_");
            try
            {
                casc.ZeroFillEncrypted = true;
                var wk = casc.ResolveFileIds(new HashSet<int> { cont.WdtFileDataId });
                if (!wk.TryGetValue(cont.WdtFileDataId, out var wck)) throw new IOException("the world table of " + cont.Name + " is not in this game install");
                var wf = WdtFile.Parse(casc.ReadByContentKey(wck));
                if (!wf.Ok) throw new InvalidDataException(wf.Problem);
                tiles = WdtFile.WithoutStrays(wf.Tiles.Where(t => t.MinimapFdid != 0 && t.RootFdid != 0).ToList(), 12, null);
                return new CascBuildingSource(casc);
            }
            catch { casc.Dispose(); throw; }
        }

        // ---------------------------------------------------------------- the build
        public static Task Start(int mapId, string mode)
        {
            mode = WorldMapBuildings.Normalize(mode);
            if (mode == "off") return Task.FromResult(0);
            var j = JobOf(mapId);
            lock (gate)
            {
                if (j.Running) { j.Mode = mode == "all" ? "all" : j.Mode; j.State.Mode = j.Mode; j.Paused = false; j.Gate.Set(); return j.Task; }
                j.Running = true; j.Mode = mode; j.State.Mode = mode; j.State.Error = null; j.Paused = false; j.Gate.Set();
                j.Cts = new CancellationTokenSource();
                j.Dir = DirOf(mapId);
                j.State.Phase = j.Scan == null ? "scanning" : "building";
                var ct = j.Cts.Token;
                j.Task = Task.Factory.StartNew(() => Run(j, ct), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }
            Fire(mapId);
            return j.Task;
        }

        public static void Pause(int mapId) { var j = JobOf(mapId); if (!j.Running) return; j.Paused = true; j.Gate.Reset(); j.State.Phase = "paused"; Fire(mapId); }
        public static void Resume(int mapId) { var j = JobOf(mapId); if (!j.Running) return; j.Paused = false; j.State.Phase = "building"; j.Gate.Set(); Fire(mapId); }
        public static void Cancel(int mapId)
        {
            Job j; lock (gate) jobs.TryGetValue(mapId, out j);
            if (j == null || !j.Running) return;
            try { j.Cts?.Cancel(); } catch (Exception) { }
            j.Gate.Set();
        }
        public static void CancelAll() { List<int> ids; lock (gate) ids = jobs.Keys.ToList(); foreach (var i in ids) Cancel(i); }
        public static bool IsRunning(int mapId) { lock (gate) return jobs.TryGetValue(mapId, out var j) && j.Running; }

        // the camera's spot (scene x, z): the next building is the one nearest to it
        public static void Focus(int mapId, double x, double z)
        {
            Job j; lock (gate) jobs.TryGetValue(mapId, out j);
            if (j != null) { j.FocusX = x; j.FocusZ = z; }
        }

        static void Run(Job j, CancellationToken ct)
        {
            try { Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; } catch (Exception) { }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string dir = j.Dir;
            Dictionary<uint, int> texColors = null;
            long writtenBytes = 0; int groupsDone = 0; double secWorked = 0, estWorked = 0;
            try
            {
                Interlocked.Increment(ref BuildCountForTest);
                WorldMapCache.RemoveOtherBuilds(WorldMapCache.BuildKey);          // the buildings of an older game build are of no use any more (a new build is a new folder)
                Directory.CreateDirectory(WorldMapBuildings.ModelsDir(dir));
                EnsureScan(j, ct);
                var scan = j.Scan;
                j.State.Phase = j.Paused ? "paused" : "building";
                // the placements go to disk first: the page draws the buildings as their shapes arrive
                var pp = File.Exists(WorldMapBuildings.PlacementsPath(dir)) ? WorldMapBuildings.PlacementsFromBytes(File.ReadAllBytes(WorldMapBuildings.PlacementsPath(dir)), out var pb) : null;
                if (pp == null || pp.Count != scan.Placements.Count) WriteFile(WorldMapBuildings.PlacementsPath(dir), WorldMapBuildings.PlacementsToBytes(scan.Placements, scan.Bridge));
                FillEstimate(j);
                Fire(j.MapId);
                // the files again (the scan closed its own source): lookups for every group and texture in one pass
                using (var src = OpenSource(j, out var tiles))
                {
                    List<BuildingModelInfo> Remaining() => WorldMapBuildings.ModelsFor(scan, j.Mode).Where(m => !File.Exists(WorldMapBuildings.ModelFile(dir, m.Fdid))).ToList();
                    var todo = Remaining();
                    var prefetched = new HashSet<int>();
                    void Prefetch(List<BuildingModelInfo> l)
                    {
                        var ids = l.Where(m => prefetched.Add(m.Fdid)).SelectMany(m => m.GroupFdids.Concat(m.TextureFdids.Select(t => (int)t)).Concat(new[] { m.Fdid })).ToList();
                        src.Prefetch(ids);
                    }
                    Prefetch(todo);
                    texColors = WorldMapBuildings.ReadTexColors(dir);
                    int sinceSave = 0;
                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();
                        j.Gate.Wait(ct);
                        if (todo.Count == 0)
                        {
                            // a switch to "all" while the bridges were building adds the rest
                            todo = Remaining(); if (todo.Count == 0) break;
                            Prefetch(todo); FillEstimate(j);
                        }
                        int pick = NextModel(j, todo);
                        var info = todo[pick]; todo.RemoveAt(pick);
                        var t0 = System.Diagnostics.Stopwatch.StartNew();
                        long bytes = 0;
                        try { bytes = WorldMapBuildings.BuildModel(info, src, dir, texColors); }
                        catch (IOException) { throw; }
                        catch (UnauthorizedAccessException) { throw; }
                        catch (Exception e)
                        {
                            Util.Log("world map buildings: " + info.Fdid + ": " + e.Message);
                            WriteFile(WorldMapBuildings.ModelFile(dir, info.Fdid), new WmoModel().ToBytes());
                        }
                        if (ModelDelayMsForTest > 0) Thread.Sleep((int)ModelDelayMsForTest);
                        secWorked += t0.Elapsed.TotalSeconds; estWorked += WorldMapBuildings.ModelSeconds(info); writtenBytes += bytes; groupsDone += Math.Max(1, info.OutsideGroups);
                        if (bytes > 0) j.Fresh.Enqueue(info.Fdid);
                        if (++sinceSave >= 40) { sinceSave = 0; WriteTexColorsSafe(dir, texColors); }
                        // progress in buildings (placements); the time left from what this run has measured against the estimate
                        j.State.Done = DoneBuildings(j);
                        double remainSec = 0; foreach (var m in todo) remainSec += WorldMapBuildings.ModelSeconds(m);
                        double ratio = estWorked > 0.05 ? Math.Max(0.2, Math.Min(5, secWorked / estWorked)) : 1;
                        j.State.SecondsLeft = remainSec * ratio;
                        Fire(j.MapId);
                    }
                    WriteTexColorsSafe(dir, texColors);
                }
                // finished: the measured costs, then the manifest (last)
                var man = new WorldMapBuildings.Manifest
                {
                    Version = WorldMapBuildings.Version, Rules = WorldMapBuildings.Rules, MapId = j.MapId, BuildKey = WorldMapCache.BuildKey, Placements = scan.Placements.Count, Models = scan.Models.Count(kv => !kv.Value.RootMissing), Tiles = scan.Tiles,
                    Bridges = scan.Bridge.Count(b => b), BridgeModels = scan.Models.Values.Count(m => m.Bridge), ModelsBuilt = WorldMapBuildings.BuiltModels(dir).Count, Ms = sw.ElapsedMilliseconds,
                };
                var prev = WorldMapBuildings.ReadManifest(dir);
                man.BridgesDone = true;
                man.AllDone = j.Mode == "all" || (prev != null && prev.AllDone);
                if (groupsDone > 0 && secWorked > 0) { man.SecPerGroup = secWorked / groupsDone; man.BytesPerGroup = (double)writtenBytes / groupsDone; }
                else if (prev != null) { man.SecPerGroup = prev.SecPerGroup; man.BytesPerGroup = prev.BytesPerGroup; man.SecBase = prev.SecBase; }
                man.Bytes = WorldMapBuildings.SizeBytes(dir);
                WorldMapBuildings.WriteManifest(dir, man);
                j.State.Phase = "done"; j.State.SecondsLeft = 0; j.State.BytesOnDisk = man.Bytes;
                j.State.Total = WorldMapBuildings.Estimate(scan, j.Mode, dir).Buildings; j.State.Done = j.State.Total;
                Util.Log("world map buildings: " + j.Name + " (" + j.Mode + ") built: " + man.Models + " models, " + man.Placements + " placements, " + (man.Bytes / 1024) + " KB in " + sw.ElapsedMilliseconds + " ms");
            }
            catch (OperationCanceledException)
            {
                try { if (texColors != null) WriteTexColorsSafe(dir, texColors); } catch (Exception) { }
                j.State.Phase = "ask"; j.State.Error = null; j.State.SecondsLeft = 0;
                try { FillEstimate(j); } catch (Exception) { }
            }
            catch (Exception e)
            {
                Util.Log("world map buildings " + j.Name + ": " + e);
                j.State.Error = "The buildings of " + j.Name + " could not be built: " + WorldMapService.Why(e); j.State.Phase = "error";
            }
            finally { lock (gate) { j.Running = false; j.Paused = false; } j.Gate.Set(); Fire(j.MapId); }
        }

        static void WriteFile(string path, byte[] data)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, data);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        static void WriteTexColorsSafe(string dir, Dictionary<uint, int> map) { try { WorldMapBuildings.WriteTexColors(dir, map); } catch (Exception) { } }

        // the model nearest to the camera's spot (the first one when the camera has not been reported yet)
        static int NextModel(Job j, List<BuildingModelInfo> todo)
        {
            double fx = j.FocusX, fz = j.FocusZ;
            if (double.IsNaN(fx) || double.IsNaN(fz)) return 0;
            if (j.Positions == null)
            {
                var pos = new Dictionary<int, List<float[]>>();
                for (int i = 0; i < j.Scan.Placements.Count; i++)
                {
                    var p = j.Scan.Placements[i]; int id = (int)p.NameId;
                    if (!pos.TryGetValue(id, out var l)) pos[id] = l = new List<float[]>();
                    l.Add(new[] { p.X, p.Z, j.Scan.Bridge[i] ? 1f : 0f });
                }
                j.Positions = pos;
            }
            bool bridgesOnly = j.Mode == "bridges";
            int best = 0; double bd = double.MaxValue;
            for (int i = 0; i < todo.Count; i++)
            {
                double d = double.MaxValue;
                if (j.Positions.TryGetValue(todo[i].Fdid, out var l))
                    foreach (var p in l)
                    {
                        if (bridgesOnly && p[2] == 0) continue;
                        double dx = p[0] - fx, dz = p[1] - fz, dd = dx * dx + dz * dz;
                        if (dd < d) d = dd;
                    }
                if (d < bd) { bd = d; best = i; }
            }
            return best;
        }

        // ---------------------------------------------------------------- remove, size, status
        public static int Remove(int mapId = -1)
        {
            int n = 0;
            try
            {
                if (mapId >= 0) Cancel(mapId); else CancelAll();
                var root = WorldMapCache.Root;
                if (!Directory.Exists(root)) return 0;
                foreach (var b in Directory.GetDirectories(root))
                {
                    if (Path.GetFileName(b) == WorldMapCache.PageFolder) continue;
                    foreach (var m in Directory.GetDirectories(b))
                    {
                        if (mapId >= 0 && Path.GetFileName(m) != mapId.ToString(CultureInfo.InvariantCulture)) continue;
                        var d = WorldMapBuildings.Dir(m);
                        if (Directory.Exists(d)) { try { Util.DeleteDir(d); n++; } catch (Exception e) { Util.Log("world map buildings: remove: " + e.Message); } }
                    }
                }
            }
            catch (Exception) { }
            lock (gate) foreach (var j in jobs.Values) if (mapId < 0 || j.MapId == mapId) { if (!j.Running) { j.State.Phase = "idle"; j.State.Done = 0; j.State.BytesOnDisk = 0; j.Scan = null; } }
            return n;
        }

        public sealed class Row { public int MapId; public string Name = ""; public long Bytes; public int Models; public bool Complete; public string Mode = ""; }

        // per continent of the current game build: what is on disk
        public static List<Row> Rows()
        {
            var res = new List<Row>();
            try
            {
                var bd = Path.Combine(WorldMapCache.Root, WorldMapCache.BuildKey);
                if (!Directory.Exists(bd)) return res;
                foreach (var m in Directory.GetDirectories(bd))
                {
                    var d = WorldMapBuildings.Dir(m);
                    if (!Directory.Exists(d) || !int.TryParse(Path.GetFileName(m), NumberStyles.None, CultureInfo.InvariantCulture, out var id)) continue;
                    long bytes = WorldMapBuildings.SizeBytes(d); if (bytes <= 0) continue;
                    var man = WorldMapBuildings.ReadManifest(d);
                    string name = WorldMapService.Continent(Maps.MapData.Index, id)?.Name ?? ("map " + id);
                    res.Add(new Row { MapId = id, Name = name, Bytes = bytes, Models = WorldMapBuildings.BuiltModels(d).Count, Complete = man != null && man.Valid && (man.AllDone || man.BridgesDone), Mode = man != null && man.AllDone ? "all" : man != null && man.BridgesDone ? "bridges" : "partly" });
                }
            }
            catch (Exception) { }
            return res.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static long TotalBytes() => Rows().Sum(r => r.Bytes);
        // "World map 3D buildings" row text for Settings
        public static string StatusLine()
        {
            var rows = Rows();
            if (rows.Count == 0) return "Not made yet. Switch Buildings on in the 3D world map (Atlas > World map > 3D); the shapes are read from your game files once, in the background, and kept here until the game updates. Nothing is uploaded.";
            return "On disk: " + string.Join(", ", rows.Select(r => r.Name + " " + WorldMapCache.SizeText(r.Bytes) + (r.Mode == "all" ? " (all buildings)" : r.Mode == "bridges" ? " (bridges)" : " (not finished)"))) + ". Read from your game files, read-only; nothing is uploaded.";
        }

    }
}
