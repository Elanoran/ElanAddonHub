using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ElansAddonHub.Services.Db2;

namespace ElansAddonHub.Services.Maps
{
    // ============================================================================================================================
    // Atlas, layer B: maps. The Outpost reads the UiMap / UiMapArt / UiMapAssignment / Map / AreaTable tables of the player's OWN WoW install,
    // read-only, in the background (the same way ItemData does): nothing is shipped, nothing is uploaded and nothing is written to the WoW folder.
    // The parsed rows live in memory as a MapIndex; a compact copy is kept in the Outpost's data folder (cache\mapdata.bin), keyed by the game
    // build, so the next start does not read the game files again. The map PICTURES are made later and one at a time (MapRender).
    // ============================================================================================================================
    public static class MapData
    {
        public enum State { NotStarted, Disabled, Loading, Ready, Unavailable }

        public const int FormatVersion = 4;          // (4: instance entrances + dungeon levels; 3: map WDT file ids, flight tables; 2: explored-area overlays) bump when the parsing or the saved file changes meaning

        // ---- hooks (the same shape as ItemData's)
        public static Func<string> WowRoot = () => null;
        public static Func<bool> Enabled = () => true;
        public static Func<string> Flavor = () => IconStore.Flavor;
        public static bool AutoStart = true;         // off in the self-test (it must not read the real game)
        public static string CacheFile = Path.Combine(Util.DataDir, "cache", "mapdata.bin");
        public static event Action Changed;          // on a worker thread, whenever the index or the state changed

        // ---- state
        static readonly object gate = new object();
        static MapIndex index;
        static volatile State state = State.NotStarted;
        static string reason, build;
        static bool running, again;
        public static long LastLoadMs { get; private set; }
        public static string LastLoadFrom { get; private set; }     // "cache file" | "game files"
        public static State Status => state;
        public static string Reason => reason;
        public static MapIndex Index => index;
        public static bool Ready => state == State.Ready && index != null;
        public static int StartCountForTest;

        // The game build the index was made from, as a short file-name-safe stamp ("1.60.1.70338-2417084b"). Map pictures are cached under it.
        public static string BuildStamp => Stamp(build);

        public static string Stamp(string buildText)
        {
            if (string.IsNullOrEmpty(buildText)) return "unknown";
            var p = buildText.Split('|');
            var ver = p.Length > 1 ? p[1] : buildText;
            var key = p.Length > 2 ? p[2] : "";
            var s = ver + (key.Length >= 8 ? "-" + key.Substring(0, 8) : "");
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }

        public static string OverlayProblem => index?.Set.OverlayProblem;
        public static int MapCount => index?.MapsWithArt ?? 0;
        public static int InstanceCount => index?.Instances.Count(i => !i.Placeholder) ?? 0;

        // "Maps: 60 maps, 18 instances" for Settings (the game-data row)
        public static string StatusLine
        {
            get
            {
                switch (state)
                {
                    case State.Disabled: return "Maps: off";
                    case State.Loading: return "Maps: reading your WoW installation...";
                    case State.Ready: return "Maps: " + MapCount.ToString("N0", CultureInfo.InvariantCulture) + " maps, " + InstanceCount.ToString("N0", CultureInfo.InvariantCulture) + " instances";
                    case State.Unavailable: return "Maps: unavailable" + (string.IsNullOrEmpty(reason) ? "" : " (" + reason + ")");
                    default: return "Maps: not read yet";
                }
            }
        }

        // appended to the item data line in Settings > Game data: "; 60 maps, 54 instances" (nothing while it loads or when it is off)
        public static string StatusSuffix
        {
            get
            {
                switch (state)
                {
                    case State.Ready: return "; " + MapCount.ToString("N0", CultureInfo.InvariantCulture) + " maps, " + InstanceCount.ToString("N0", CultureInfo.InvariantCulture) + " instances";
                    case State.Unavailable: return "; maps unavailable" + (string.IsNullOrEmpty(reason) ? "" : " (" + reason + ")");
                    default: return "";
                }
            }
        }

        // ---------------------------------------------------------------------------------------------------------------------
        public static void Start()
        {
            Interlocked.Increment(ref StartCountForTest);
            if (!SafeEnabled()) { Disable(); return; }
            lock (gate)
            {
                again = true;
                if (running) return;
                running = true;
            }
            if (index == null) SetState(State.Loading, null);
            Task.Factory.StartNew(Worker, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        static bool SafeEnabled() { try { return Enabled(); } catch { return true; } }

        public static void Disable()
        {
            lock (gate) { index = null; }
            SetState(State.Disabled, null);
        }

        static void Worker()
        {
            try { Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; } catch { }
            while (true)
            {
                lock (gate) { if (!again) { running = false; return; } again = false; }
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    if (!SafeEnabled()) { Disable(); continue; }
                    LoadOnce();
                }
                catch (Exception e)
                {
                    Util.Log("map data: " + e);
                    Publish(null, null, State.Unavailable, "could not be read: " + e.Message);
                }
                LastLoadMs = sw.ElapsedMilliseconds;
            }
        }

        static void Publish(MapIndex idx, string buildText, State st, string why)
        {
            lock (gate) { index = st == State.Ready ? idx : null; build = st == State.Ready ? buildText : build; reason = why; state = st; }
            try { Changed?.Invoke(); } catch { }
        }

        static void SetState(State st, string why)
        {
            lock (gate) { state = st; reason = why; }
            try { Changed?.Invoke(); } catch { }
        }

        // test hooks
        public static void SetForTest(MapIndex idx, string buildText = "wow_classic_beta|1.60.1.70338|0123456789abcdef", State st = State.Ready, string why = null)
        { lock (gate) { index = idx; build = buildText; state = st; reason = why; } }
        public static void ResetForTest() { lock (gate) { index = null; build = null; state = State.NotStarted; reason = null; running = false; again = false; } }

        // ---------------------------------------------------------------------------------------------------------------------
        static void LoadOnce()
        {
            var root = WowRoot();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) { Publish(null, null, State.Unavailable, "the WoW folder is not set"); return; }
            var flavor = Flavor() ?? "_classic_beta_";
            var b = ItemData.GameBuild(root, flavor);
            if (b == null) { Publish(null, null, State.Unavailable, "no .build.info in the WoW folder"); return; }
            var set = Obtain(MapIndexFile.TryRead(CacheFile), b, CacheFile, () =>
            {
                try { return ReadAndParse(root, flavor); }
                catch (Exception e) { Util.Log("map data: game files: " + e.Message); return new MapSet { Problem = "the game files could not be read: " + e.Message }; }
            }, out var from);
            if (!set.Ok) { Util.Log("map data: unavailable: " + set.Problem); Publish(null, null, State.Unavailable, set.Problem); return; }
            foreach (var n in set.Notes) Util.Log("map data: " + n);
            LastLoadFrom = from;
            MapIndex.AreaAt = AreaProbe.AreaAt;
            Publish(new MapIndex(set), b, State.Ready, null);
        }

        // The decision: the saved index is used when the game build is the same (the shipped tables cannot have changed), else the game files are
        // read (readGame) and the result is saved.
        public static MapSet Obtain(MapIndexFile.Saved saved, string buildText, string cacheFile, Func<MapSet> readGame, out string from)
        {
            if (saved != null && saved.Build == buildText && saved.Format == FormatVersion && saved.Set.Ok) { from = "cache file"; return saved.Set; }
            var fresh = readGame();
            if (fresh.Ok) MapIndexFile.TryWrite(cacheFile, buildText, fresh);
            from = "game files";
            return fresh;
        }

        // the tables -> rows, plus the one thing the tables cannot say: the terrain area under each instance entrance (EntranceAreas; a few small ADT files)
        public static MapSet ReadAndParse(string root, string flavor)
        {
            using (var casc = CascStorage.Open(root, flavor))
            {
                casc.ZeroFillEncrypted = true;     // key-locked parts read as zeros; the table reader skips those sections
                var set = MapTables.Parse(ReadTables(casc));
                if (set.Ok && set.HasMaps) EntranceAreas.Resolve(casc, set);
                return set;
            }
        }

        static Dictionary<string, byte[]> ReadTables(CascStorage casc)
        {
            var keys = casc.ResolveFileIds(new HashSet<int>(MapTables.All.Select(d => d.FileDataId)));
            var raw = new Dictionary<string, byte[]>();
            foreach (var d in MapTables.All)
            {
                try { raw[d.Name] = keys.TryGetValue(d.FileDataId, out var ck) ? casc.ReadByContentKey(ck) : null; }
                catch (Exception e) { Util.Log("map data: " + d.Name + ".db2: " + e.Message); raw[d.Name] = null; }
            }
            return raw;
        }

        // the raw table files by name (null = not in this install)
        public static Dictionary<string, byte[]> ReadCasc(string root, string flavor)
        {
            using (var casc = CascStorage.Open(root, flavor))
            {
                casc.ZeroFillEncrypted = true;     // key-locked parts read as zeros; the table reader skips those sections
                var keys = casc.ResolveFileIds(new HashSet<int>(MapTables.All.Select(d => d.FileDataId)));
                var raw = new Dictionary<string, byte[]>();
                foreach (var d in MapTables.All)
                {
                    try { raw[d.Name] = keys.TryGetValue(d.FileDataId, out var ck) ? casc.ReadByContentKey(ck) : null; }
                    catch (Exception e) { Util.Log("map data: " + d.Name + ".db2: " + e.Message); raw[d.Name] = null; }
                }
                return raw;
            }
        }
    }

    // ============================================================================================================================
    // the compact copy of the rows in the Outpost's data folder (gzip of a small binary format), keyed by the game build
    public static class MapIndexFile
    {
        public sealed class Saved { public int Format; public string Build; public MapSet Set; }

        const int Magic = 0x5041504D;     // "MPAP"

        public static bool TryWrite(string path, string build, MapSet set)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var tmp = path + ".tmp";
                var mem = new MemoryStream(1 << 18);
                using (var w = new BinaryWriter(mem, Encoding.UTF8, true))
                {
                    w.Write(Magic); w.Write(MapData.FormatVersion); w.Write(build ?? "");
                    w.Write(set.HasArt); w.Write(set.HasAssignments); w.Write(set.HasMaps); w.Write(set.HasFloors); w.Write(set.HasAreas); w.Write(set.HasOverlays); w.Write(set.HasTaxi); w.Write(set.HasLfg); w.Write(set.OverlayProblem ?? ""); w.Write(set.LockedMapRows);
                    w.Write(set.Notes.Count); foreach (var n in set.Notes) w.Write(n);
                    w.Write(set.UiMaps.Count);
                    foreach (var m in set.UiMaps) { w.Write(m.Id); w.Write(m.Name ?? ""); w.Write(m.ParentId); w.Write(m.Flags); w.Write(m.System); w.Write(m.Type); }
                    w.Write(set.Arts.Count);
                    foreach (var a in set.Arts)
                    {
                        w.Write(a.Id); w.Write(a.StyleId);
                        w.Write(a.Layers.Length);
                        foreach (var l in a.Layers) { w.Write(l.Index); w.Write(l.Width); w.Write(l.Height); w.Write(l.TileWidth); w.Write(l.TileHeight); }
                        w.Write(a.Tiles.Length);
                        foreach (var t in a.Tiles) { w.Write((byte)t.Row); w.Write((byte)t.Col); w.Write((byte)t.Layer); w.Write(t.FileDataId); }
                        w.Write(a.Overlays.Length);
                        foreach (var o in a.Overlays)
                        {
                            w.Write(o.Id); w.Write(o.Width); w.Write(o.Height); w.Write(o.OffsetX); w.Write(o.OffsetY);
                            w.Write(o.Tiles.Length);
                            foreach (var t in o.Tiles) { w.Write((byte)t.Row); w.Write((byte)t.Col); w.Write((byte)t.Layer); w.Write(t.FileDataId); }
                        }
                    }
                    w.Write(set.ArtLinks.Count);
                    foreach (var l in set.ArtLinks) { w.Write(l.UiMapId); w.Write(l.ArtId); w.Write(l.Phase); }
                    w.Write(set.Assignments.Count);
                    foreach (var a in set.Assignments)
                    {
                        w.Write(a.Id); w.Write(a.UiMapId); w.Write(a.MapId); w.Write(a.AreaId); w.Write(a.Order);
                        w.Write(a.UiMinX); w.Write(a.UiMinY); w.Write(a.UiMaxX); w.Write(a.UiMaxY);
                        w.Write(a.MinX); w.Write(a.MinY); w.Write(a.MinZ); w.Write(a.MaxX); w.Write(a.MaxY); w.Write(a.MaxZ);
                    }
                    w.Write(set.Maps.Count);
                    foreach (var m in set.Maps) { w.Write(m.Id); w.Write(m.Directory ?? ""); w.Write(m.Name ?? ""); w.Write(m.InstanceType); w.Write(m.MapType); w.Write(m.Expansion); w.Write(m.MaxPlayers); w.Write(m.AreaTableId); w.Write(m.WdtFileDataId); w.Write(m.CorpseMapId); w.Write(m.CorpseX); w.Write(m.CorpseY); w.Write(m.CorpseAreaId); }
                    w.Write(set.Floors.Count);
                    foreach (var f in set.Floors) { w.Write(f.GroupId); w.Write(f.UiMapId); w.Write(f.FloorIndex); w.Write(f.RelativeHeight); w.Write(f.Name ?? ""); }
                    w.Write(set.Areas.Count);
                    foreach (var a in set.Areas) { w.Write(a.Id); w.Write(a.Name ?? ""); w.Write(a.ParentId); w.Write(a.ContinentId); }
                    w.Write(set.TaxiNodes.Count);
                    foreach (var n in set.TaxiNodes) { w.Write(n.Id); w.Write(n.MapId); w.Write(n.Flags); w.Write(n.MountHorde); w.Write(n.MountAlliance); w.Write(n.X); w.Write(n.Y); w.Write(n.Z); w.Write(n.Name ?? ""); }
                    w.Write(set.TaxiPaths.Count);
                    foreach (var p in set.TaxiPaths) { w.Write(p.Id); w.Write(p.FromNode); w.Write(p.ToNode); w.Write(p.Cost); }
                    w.Write(set.TaxiPoints.Count);
                    foreach (var q in set.TaxiPoints) { w.Write(q.PathId); w.Write(q.Index); w.Write(q.MapId); w.Write(q.X); w.Write(q.Y); w.Write(q.Z); }
                    w.Write(set.Lfg.Count);
                    foreach (var d in set.Lfg) { w.Write(d.Id); w.Write(d.Name ?? ""); w.Write(d.TypeId); w.Write(d.ContentTuningId); w.Write(d.MinLevel); w.Write(d.MaxLevel); }
                }
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var gz = new GZipStream(fs, CompressionMode.Compress))
                    gz.Write(mem.GetBuffer(), 0, (int)mem.Length);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                return true;
            }
            catch (Exception e) { Util.Log("map data: cannot save the index: " + e.Message); return false; }
        }

        // null for a missing, damaged or foreign file (the caller then reads the game files)
        public static Saved TryRead(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var mem = new MemoryStream(1 << 18);
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var gz = new GZipStream(fs, CompressionMode.Decompress))
                    gz.CopyTo(mem);
                mem.Position = 0;
                using (var r = new BinaryReader(mem, Encoding.UTF8))
                {
                    if (r.ReadInt32() != Magic) return null;
                    var s = new Saved { Format = r.ReadInt32(), Build = r.ReadString(), Set = new MapSet() };
                    if (s.Format != MapData.FormatVersion) return null;
                    var set = s.Set;
                    set.HasArt = r.ReadBoolean(); set.HasAssignments = r.ReadBoolean(); set.HasMaps = r.ReadBoolean(); set.HasFloors = r.ReadBoolean(); set.HasAreas = r.ReadBoolean(); set.HasOverlays = r.ReadBoolean(); set.HasTaxi = r.ReadBoolean(); set.HasLfg = r.ReadBoolean(); var op = r.ReadString(); set.OverlayProblem = op.Length == 0 ? null : op;
                    set.LockedMapRows = r.ReadInt32();
                    int n = Count(r, 1000); for (int i = 0; i < n; i++) set.Notes.Add(r.ReadString());
                    n = Count(r, 100000);
                    for (int i = 0; i < n; i++) set.UiMaps.Add(new UiMapInfo { Id = r.ReadInt32(), Name = r.ReadString(), ParentId = r.ReadInt32(), Flags = r.ReadInt32(), System = r.ReadInt32(), Type = r.ReadInt32() });
                    n = Count(r, 1000000);
                    for (int i = 0; i < n; i++)
                    {
                        var a = new MapArtInfo { Id = r.ReadInt32(), StyleId = r.ReadInt32() };
                        int lc = Count(r, 64); a.Layers = new ArtLayerInfo[lc];
                        for (int k = 0; k < lc; k++) a.Layers[k] = new ArtLayerInfo { Index = r.ReadInt32(), Width = r.ReadInt32(), Height = r.ReadInt32(), TileWidth = r.ReadInt32(), TileHeight = r.ReadInt32() };
                        int tc = Count(r, 1000000); a.Tiles = new ArtTile[tc];
                        for (int k = 0; k < tc; k++) a.Tiles[k] = new ArtTile { Row = r.ReadByte(), Col = r.ReadByte(), Layer = r.ReadByte(), FileDataId = r.ReadInt32() };
                        int oc = Count(r, 10000); a.Overlays = new ArtOverlay[oc];
                        for (int k = 0; k < oc; k++)
                        {
                            var o = new ArtOverlay { Id = r.ReadInt32(), Width = r.ReadInt32(), Height = r.ReadInt32(), OffsetX = r.ReadInt32(), OffsetY = r.ReadInt32() };
                            int ot = Count(r, 10000); o.Tiles = new ArtTile[ot];
                            for (int m = 0; m < ot; m++) o.Tiles[m] = new ArtTile { Row = r.ReadByte(), Col = r.ReadByte(), Layer = r.ReadByte(), FileDataId = r.ReadInt32() };
                            a.Overlays[k] = o;
                        }
                        set.Arts.Add(a);
                    }
                    n = Count(r, 1000000); for (int i = 0; i < n; i++) set.ArtLinks.Add(new MapArtLink { UiMapId = r.ReadInt32(), ArtId = r.ReadInt32(), Phase = r.ReadInt32() });
                    n = Count(r, 1000000);
                    for (int i = 0; i < n; i++)
                        set.Assignments.Add(new UiAssignment
                        {
                            Id = r.ReadInt32(), UiMapId = r.ReadInt32(), MapId = r.ReadInt32(), AreaId = r.ReadInt32(), Order = r.ReadInt32(),
                            UiMinX = r.ReadSingle(), UiMinY = r.ReadSingle(), UiMaxX = r.ReadSingle(), UiMaxY = r.ReadSingle(),
                            MinX = r.ReadSingle(), MinY = r.ReadSingle(), MinZ = r.ReadSingle(), MaxX = r.ReadSingle(), MaxY = r.ReadSingle(), MaxZ = r.ReadSingle(),
                        });
                    n = Count(r, 1000000);
                    for (int i = 0; i < n; i++) set.Maps.Add(new GameMapInfo { Id = r.ReadInt32(), Directory = r.ReadString(), Name = r.ReadString(), InstanceType = r.ReadInt32(), MapType = r.ReadInt32(), Expansion = r.ReadInt32(), MaxPlayers = r.ReadInt32(), AreaTableId = r.ReadInt32(), WdtFileDataId = r.ReadInt32(), CorpseMapId = r.ReadInt32(), CorpseX = r.ReadDouble(), CorpseY = r.ReadDouble(), CorpseAreaId = r.ReadInt32() });
                    n = Count(r, 1000000);
                    for (int i = 0; i < n; i++) set.Floors.Add(new FloorMember { GroupId = r.ReadInt32(), UiMapId = r.ReadInt32(), FloorIndex = r.ReadInt32(), RelativeHeight = r.ReadInt32(), Name = r.ReadString() });
                    n = Count(r, 2000000);
                    for (int i = 0; i < n; i++) set.Areas.Add(new AreaInfo { Id = r.ReadInt32(), Name = r.ReadString(), ParentId = r.ReadInt32(), ContinentId = r.ReadInt32() });
                    n = Count(r, 100000);
                    for (int i = 0; i < n; i++) set.TaxiNodes.Add(new TaxiNodeInfo { Id = r.ReadInt32(), MapId = r.ReadInt32(), Flags = r.ReadInt32(), MountHorde = r.ReadInt32(), MountAlliance = r.ReadInt32(), X = r.ReadDouble(), Y = r.ReadDouble(), Z = r.ReadDouble(), Name = r.ReadString() });
                    n = Count(r, 100000);
                    for (int i = 0; i < n; i++) set.TaxiPaths.Add(new TaxiPathInfo { Id = r.ReadInt32(), FromNode = r.ReadInt32(), ToNode = r.ReadInt32(), Cost = r.ReadInt32() });
                    n = Count(r, 2000000);
                    for (int i = 0; i < n; i++) set.TaxiPoints.Add(new TaxiPoint { PathId = r.ReadInt32(), Index = r.ReadInt32(), MapId = r.ReadInt32(), X = r.ReadDouble(), Y = r.ReadDouble(), Z = r.ReadDouble() });
                    n = Count(r, 100000);
                    for (int i = 0; i < n; i++) set.Lfg.Add(new LfgDungeonInfo { Id = r.ReadInt32(), Name = r.ReadString(), TypeId = r.ReadInt32(), ContentTuningId = r.ReadInt32(), MinLevel = r.ReadInt32(), MaxLevel = r.ReadInt32() });
                    return s;
                }
            }
            catch (Exception) { return null; }
        }

        static int Count(BinaryReader r, int max)
        {
            int n = r.ReadInt32();
            if (n < 0 || n > max) throw new InvalidDataException("count");
            return n;
        }
    }
}
