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
    // Runs the "Trees & objects" of a continent (WorldMapObjects, 3.2 experimental): counts them, tells the card what it will cost, and reads the models
    // and their textures on one background thread at below-normal priority, nearest to the camera first, pausable and resumable. One job per continent;
    // the page's messages reach it through WorldMapObjectsHost. Nothing is read until the person said "Build now" - the scan only reads the obj0 files.
    // The same shape as BuildingsService; the build does not depend on the level (Low / Medium / High only change how far the page draws).
    // ============================================================================================================================
    public static class ObjectsService
    {
        public static Func<IBuildingSource> SourceForTest;
        public static int BuildCountForTest, ScanCountForTest;
        public static double ModelDelayMsForTest;
        public static event Action<int> Changed;                              // on a worker thread

        sealed class Job
        {
            public int MapId; public string Name = "";
            public ObjectsState State;
            public ObjectScan Scan;
            public string Dir;
            public CancellationTokenSource Cts;
            public ManualResetEventSlim Gate = new ManualResetEventSlim(true);
            public volatile bool Paused, Running;
            public double FocusX = double.NaN, FocusZ = double.NaN;
            public ConcurrentQueue<int> Fresh = new ConcurrentQueue<int>();
            public Task Task;
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
                    j = new Job { MapId = mapId, Name = cont?.Name ?? ("map " + mapId), State = new ObjectsState { MapId = mapId } };
                    j.State.Name = j.Name;
                    jobs[mapId] = j;
                }
                return j;
            }
        }

        static void Fire(int mapId) { try { Changed?.Invoke(mapId); } catch (Exception) { } }
        public static void ResetForTest() { lock (gate) { foreach (var j in jobs.Values) { try { j.Cts?.Cancel(); j.Gate.Set(); } catch (Exception) { } } jobs.Clear(); } }

        public static string DirOf(int mapId) => WorldMapObjects.Dir(WorldMapCache.MapDir(mapId));
        public static ObjectsState StateOf(int mapId) { lock (gate) return jobs.TryGetValue(mapId, out var j) ? j.State : null; }
        public static ObjectScan ScanOf(int mapId) { lock (gate) return jobs.TryGetValue(mapId, out var j) ? j.Scan : null; }
        public static HashSet<int> Built(int mapId) => WorldMapObjects.BuiltModels(DirOf(mapId));
        public static List<int> TakeFresh(int mapId)
        {
            var res = new List<int>();
            Job j; lock (gate) jobs.TryGetValue(mapId, out j);
            if (j != null) while (j.Fresh.TryDequeue(out var id)) res.Add(id);
            return res;
        }

        // every model of the continent is on disk (for this game build)
        public static bool IsReady(int mapId)
        {
            var m = WorldMapObjects.ReadManifest(DirOf(mapId));
            return m != null && m.Valid && m.MapId == mapId && m.BuildKey == WorldMapCache.BuildKey && m.AllDone && File.Exists(WorldMapObjects.TilesPath(DirOf(mapId)));
        }

        // ---------------------------------------------------------------- the scan and the estimate: the state becomes "ask" (or "done")
        public static Task Prepare(int mapId)
        {
            var j = JobOf(mapId);
            lock (gate)
            {
                if (j.Running) return j.Task ?? Task.FromResult(0);
                j.State.Error = null;
            }
            j.Dir = DirOf(mapId);
            if (IsReady(mapId))
            {
                var m = WorldMapObjects.ReadManifest(j.Dir);
                j.State.Phase = "done"; j.State.Done = j.State.Total = m.Models; j.State.Placements = m.Placements; j.State.BytesOnDisk = WorldMapObjects.SizeBytes(j.Dir);
                Fire(mapId); return Task.FromResult(0);
            }
            j.State.Phase = "scanning"; Fire(mapId);
            return Task.Run(() =>
            {
                try
                {
                    Interlocked.Increment(ref ScanCountForTest);
                    EnsureScan(j, CancellationToken.None);
                    WorldMapObjects.FillEstimate(j.State, j.Scan, j.Dir);
                    j.State.Phase = "ask";
                }
                catch (Exception e)
                {
                    Util.Log("world map objects " + j.Name + ": " + e);
                    j.State.Error = "The trees and objects of " + j.Name + " could not be counted: " + WorldMapService.Why(e); j.State.Phase = "error";
                }
                Fire(mapId);
            });
        }

        static void LoadStats()
        {
            // measured costs of an earlier build (any continent of this game build) replace the defaults
            try
            {
                var root = Path.Combine(WorldMapCache.Root, WorldMapCache.BuildKey);
                if (!Directory.Exists(root)) return;
                foreach (var d in Directory.GetDirectories(root))
                {
                    var m = WorldMapObjects.ReadManifest(WorldMapObjects.Dir(d));
                    if (m != null && m.Valid && m.SecPerModel > 0 && m.BytesPerModel > 0) { WorldMapObjects.SecondsPerModel = m.SecPerModel; WorldMapObjects.BytesPerModel = m.BytesPerModel; break; }
                }
            }
            catch (Exception) { }
        }

        static void EnsureScan(Job j, CancellationToken ct)
        {
            if (j.Scan != null) return;
            LoadStats();
            using (var src = BuildingsService.OpenSourceFor(j.MapId, SourceForTest, out var tiles))
            {
                var scan = WorldMapObjects.Scan(tiles, src, ct);
                if (scan.Placements == 0) throw new InvalidDataException(j.Name + " has no trees or objects in this game install");
                lock (gate) j.Scan = scan;
            }
        }

        // ---------------------------------------------------------------- the build
        public static Task Start(int mapId)
        {
            var j = JobOf(mapId);
            lock (gate)
            {
                if (j.Running) { j.Paused = false; j.Gate.Set(); return j.Task; }
                j.Running = true; j.State.Error = null; j.Paused = false; j.Gate.Set();
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

        // the camera's spot (scene x, z): the models standing nearest to it are read first
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
            Dictionary<string, int> unread = null;
            long writtenBytes = 0; int modelsMade = 0; double secWorked = 0;
            try
            {
                Interlocked.Increment(ref BuildCountForTest);
                WorldMapCache.RemoveOtherBuilds(WorldMapCache.BuildKey);          // the files of an older game build are of no use any more
                EnsureScan(j, ct);
                var scan = j.Scan;
                j.State.Phase = j.Paused ? "paused" : "building";
                // the placements go to disk first: the page draws each model as it arrives
                if (!WorldMapObjects.PlacementsOnDisk(dir, scan)) WorldMapObjects.WritePlacements(dir, scan);
                WorldMapObjects.FillEstimate(j.State, scan, dir);
                Fire(j.MapId);
                using (var src = BuildingsService.OpenSourceFor(j.MapId, SourceForTest, out var tiles))
                {
                    var todo = scan.Models.Keys.Where(id => !WorldMapObjects.ModelDone(dir, id)).ToList();
                    src.Prefetch(todo);
                    var texDone = new Dictionary<int, bool>();
                    var order = new Order(scan);
                    // the next models' roots are read a batch at a time and their skins and textures looked up together: one lookup in the game's file list
                    // for 64 models instead of one for each file (a single lookup walks the whole index)
                    var roots = new Dictionary<int, byte[]>();
                    while (todo.Count > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        j.Gate.Wait(ct);
                        int id = order.Next(todo, j.FocusX, j.FocusZ);
                        if (!roots.ContainsKey(id))
                        {
                            roots.Clear();
                            var batch = new List<int> { id }; batch.AddRange(order.Upcoming(63));
                            var more = new List<int>();
                            foreach (var b in batch)
                            {
                                var rb = src.Read(b); roots[b] = rb;
                                var r = M2Mesh.Root.Parse(rb);
                                if (r.Ok) { more.Add(r.Skins[0]); more.AddRange(r.Textures.Where(t => t > 0)); }
                            }
                            src.Prefetch(more);
                        }
                        bool rootRead = roots.TryGetValue(id, out var rootBytes); roots.Remove(id);
                        var t0 = System.Diagnostics.Stopwatch.StartNew();
                        long bytes = 0;
                        try { bytes = WorldMapObjects.BuildModel(id, src, dir, texDone, rootBytes, rootRead); }
                        catch (IOException) { throw; }
                        catch (UnauthorizedAccessException) { throw; }
                        catch (Exception e)
                        {
                            Util.Log("world map objects: " + id + ": " + e.Message);
                            WriteFile(WorldMapObjects.ModelFile(dir, id), (byte[])M2Mesh.Stub.Clone());
                        }
                        if (ModelDelayMsForTest > 0) Thread.Sleep((int)ModelDelayMsForTest);
                        secWorked += t0.Elapsed.TotalSeconds; writtenBytes += bytes; modelsMade++;
                        if (bytes > 0) j.Fresh.Enqueue(id);
                        j.State.Done++;
                        double per = modelsMade >= 20 ? secWorked / modelsMade : WorldMapObjects.SecondsPerModel;
                        j.State.SecondsLeft = todo.Count * per;
                        Fire(j.MapId);
                    }
                    unread = src.Unread();
                }
                var man = new WorldMapObjects.Manifest
                {
                    Version = WorldMapObjects.Version, MapId = j.MapId, BuildKey = WorldMapCache.BuildKey, Placements = scan.Placements, Models = scan.Models.Count, Tiles = scan.ByTile.Count,
                    ModelsBuilt = WorldMapObjects.BuiltModels(dir).Count, Textures = WorldMapObjects.TextureCount(dir), Ms = sw.ElapsedMilliseconds, AllDone = true,
                };
                var prev = WorldMapObjects.ReadManifest(dir);
                if (modelsMade >= 20 && secWorked > 0) { man.SecPerModel = secWorked / modelsMade; man.BytesPerModel = (double)writtenBytes / modelsMade; }
                else if (prev != null) { man.SecPerModel = prev.SecPerModel; man.BytesPerModel = prev.BytesPerModel; }
                man.Bytes = WorldMapObjects.SizeBytes(dir);
                man.Unread = unread ?? new Dictionary<string, int>();
                if (man.Unread.Count > 0) Util.Log("world map objects: " + j.Name + ": not readable in this build: " + UnreadText(man.Unread));
                WorldMapObjects.WriteManifest(dir, man);
                j.State.Phase = "done"; j.State.SecondsLeft = 0; j.State.BytesOnDisk = man.Bytes; j.State.Done = j.State.Total = man.Models;
                Util.Log("world map objects: " + j.Name + " built: " + man.ModelsBuilt + " of " + man.Models + " models drawable, " + man.Textures + " textures, " + man.Placements + " placements, " + (man.Bytes / 1024) + " KB in " + sw.ElapsedMilliseconds + " ms");
            }
            catch (OperationCanceledException)
            {
                j.State.Phase = "ask"; j.State.Error = null; j.State.SecondsLeft = 0;
                try { if (j.Scan != null) WorldMapObjects.FillEstimate(j.State, j.Scan, dir); } catch (Exception) { }
            }
            catch (Exception e)
            {
                Util.Log("world map objects " + j.Name + ": " + e);
                j.State.Error = "The trees and objects of " + j.Name + " could not be read: " + WorldMapService.Why(e); j.State.Phase = "error";
            }
            finally { lock (gate) { j.Running = false; j.Paused = false; } j.Gate.Set(); Fire(j.MapId); }
        }

        // "textures: 12 listed but not on disk; model files: 1 not in the game's file list"
        public static string UnreadText(Dictionary<string, int> unread) => WorldMapBuildings.UnreadText(unread).Replace("building files:", "model files:");

        // nearest first: the remaining models sorted by their nearest placement to the camera's spot, sorted again when the spot moved more than a tile
        // or every 200 models (a pick per model over every placement would be a billion comparisons on a big continent)
        sealed class Order
        {
            readonly Dictionary<int, float[]> pos = new Dictionary<int, float[]>();
            List<int> sorted; int at, since; double sx = double.NaN, sz = double.NaN;
            public Order(ObjectScan scan)
            {
                var tmp = new Dictionary<int, List<float>>();
                foreach (var l in scan.ByTile.Values) foreach (var p in l) { int id = (int)p.NameId; if (!tmp.TryGetValue(id, out var f)) tmp[id] = f = new List<float>(); f.Add(p.X); f.Add(p.Z); }
                foreach (var kv in tmp) pos[kv.Key] = kv.Value.ToArray();
            }
            // the models after the one Next gave last, in the same order (they may change when the camera moves on: then those are read on their own turn)
            public List<int> Upcoming(int n) => sorted == null ? new List<int>() : sorted.Skip(at).Take(n).ToList();

            public int Next(List<int> todo, double fx, double fz)
            {
                bool moved = !double.IsNaN(fx) && (double.IsNaN(sx) || Math.Abs(fx - sx) + Math.Abs(fz - sz) > 533);
                if (sorted == null || at >= sorted.Count || moved || since >= 200)
                {
                    sx = fx; sz = fz; since = 0; at = 0;
                    if (double.IsNaN(fx)) sorted = new List<int>(todo);
                    else
                    {
                        var d = new Dictionary<int, double>();
                        foreach (var id in todo)
                        {
                            double best = double.MaxValue;
                            if (pos.TryGetValue(id, out var a)) for (int i = 0; i + 1 < a.Length; i += 2) { double dx = a[i] - fx, dz = a[i + 1] - fz, dd = dx * dx + dz * dz; if (dd < best) best = dd; }
                            d[id] = best;
                        }
                        sorted = todo.OrderBy(id => d[id]).ToList();
                    }
                }
                since++;
                int pick = sorted[at++];
                todo.Remove(pick);
                return pick;
            }
        }

        static void WriteFile(string path, byte[] data)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, data);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
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
                        var d = WorldMapObjects.Dir(m);
                        if (Directory.Exists(d)) { try { Util.DeleteDir(d); n++; } catch (Exception e) { Util.Log("world map objects: remove: " + e.Message); } }
                    }
                }
            }
            catch (Exception) { }
            lock (gate) foreach (var j in jobs.Values) if (mapId < 0 || j.MapId == mapId) { if (!j.Running) { j.State.Phase = "idle"; j.State.Done = 0; j.State.BytesOnDisk = 0; j.Scan = null; } }
            return n;
        }

        public sealed class Row { public int MapId; public string Name = ""; public long Bytes; public int Models; public bool Complete; }

        public static List<Row> Rows()
        {
            var res = new List<Row>();
            try
            {
                var bd = Path.Combine(WorldMapCache.Root, WorldMapCache.BuildKey);
                if (!Directory.Exists(bd)) return res;
                foreach (var m in Directory.GetDirectories(bd))
                {
                    var d = WorldMapObjects.Dir(m);
                    if (!Directory.Exists(d) || !int.TryParse(Path.GetFileName(m), NumberStyles.None, CultureInfo.InvariantCulture, out var id)) continue;
                    long bytes = WorldMapObjects.SizeBytes(d); if (bytes <= 0) continue;
                    var man = WorldMapObjects.ReadManifest(d);
                    string name = WorldMapService.Continent(Maps.MapData.Index, id)?.Name ?? ("map " + id);
                    res.Add(new Row { MapId = id, Name = name, Bytes = bytes, Models = WorldMapObjects.BuiltModels(d).Count, Complete = man != null && man.Valid && man.AllDone });
                }
            }
            catch (Exception) { }
            return res.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static long TotalBytes() => Rows().Sum(r => r.Bytes);
        public static string StatusLine()
        {
            var rows = Rows();
            if (rows.Count == 0) return "Not made yet. Switch Trees & objects on in the 3D world map (Atlas > World map > 3D); the models are read from your game files once, in the background, and kept here until the game updates. Nothing is uploaded.";
            return "On disk: " + string.Join(", ", rows.Select(r => r.Name + " " + WorldMapCache.SizeText(r.Bytes) + (r.Complete ? "" : " (not finished)"))) + ". Read from your game files, read-only; nothing is uploaded.";
        }
    }
}
