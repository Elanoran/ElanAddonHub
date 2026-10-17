using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ElansAddonHub.Services.Maps;
using ElansAddonHub.Services.WorldMap;

namespace ElansAddonHub.Services.DungeonMaps
{
    // ============================================================================================================================
    // Drawn plans of the instances the game ships no map art for (most dungeons and raids of this client). Everything is made on the player's PC from the
    // player's own game files, read-only (DungeonGeometry -> DungeonFloors -> DungeonPlanImage), and kept in the Outpost's data folder per game build:
    //   <data>\atlas\dungeonmaps\<build stamp>_v<Format>\<map id>\plan.json       the plan: floors with their frames (world -> picture), what it was made from, timings
    //                                                         \floor<N>.png       one role picture per drawn floor, bottom to top (colours come from the theme)
    //                                                         \outside.jpg        the minimap picture of a map whose terrain is part of the instance
    //                                    \stats.json                               the measured cost of the last build (the next estimate uses it)
    // Nothing is shipped, uploaded or written to the WoW folder. A new game build is a new folder; the old ones are removed after a plan of the new build is made.
    //
    // The first time a plan is asked for (the Atlas map card of an instance with no art), the map's files are counted (the WDT, the obj0 files, the WMO roots:
    // well under a second) and the cost is estimated from the groups and tiles; a plan that takes under AutoSeconds is drawn straight away in the background,
    // a bigger one waits for "Draw the map" (the card shows the estimate). One plan is drawn at a time, at below-normal priority.
    //
    // A map whose terrain is part of the instance (Zul'Gurub, Zul'Farrak, the Ruins of Ahn'Qiraj, Shadowfang Keep's grounds - DungeonGeometry's terrain rule) gets an
    // "Outside" floor first: the game's own minimap pictures of its tiles, composed like the world map does (game art, shown as it is: the one place that does
    // not follow the theme, as on the world map), then drawn floors of the building interiors when they are a real part of it (InteriorShare of the building
    // floor and InteriorArea square yards).
    //
    // Extension point for pins (entrances, creature spawns): DungeonPlan.WorldToFraction(floor, x, y) gives the spot on a floor's picture and
    // DungeonPlan.FloorAt(x, y, z) the floor a position is on.
    // ============================================================================================================================
    public sealed class DungeonPlanFloor
    {
        public string Name = "", File = "", Kind = "plan";      // plan = a role picture coloured by the theme; picture = game art shown as it is
        public string GameFile = "";                            // a drawn floor: the game's own minimap pictures laid onto it (DungeonMinimap; "" = none)
        public double GameCoverage;                             // how much of the floor they cover (0..1)
        public bool HasGame => GameFile.Length > 0;
        public double MinZ, MaxZ, MeanZ, Area;
        public int Triangles;
        public double MaxX, MaxY, Scale;                        // the frame: world x (north) and y (west) of the top-left corner, pixels per yard
        public int Width, Height;
        public bool IsPicture => Kind == "picture";

        public System.Windows.Point WorldToFraction(double x, double y) => new System.Windows.Point(Width <= 0 ? 0 : (MaxY - y) * Scale / Width, Height <= 0 ? 0 : (MaxX - x) * Scale / Height);
    }

    public sealed class DungeonPlan
    {
        public const int Format = 3;           // 2: the game's minimap pictures per drawn floor; 3: where a character can walk (nav.bin, DungeonNav)
        public int MapId;
        public string Name = "", Build = "", Source = "", TerrainRule = "";
        public int Buildings, Groups, Tiles, Islands;
        public long Ms, Bytes;
        public List<DungeonPlanFloor> Floors = new List<DungeonPlanFloor>();
        public string Dir;
        public string NavFile = "";            // the walkable grid of the drawn floors ("" = none: a map of the game's own pictures only)
        DungeonNav nav; bool navRead;
        public DungeonNav Nav() { if (!navRead) { navRead = true; nav = NavFile.Length > 0 ? DungeonNav.Load(Path.Combine(Dir ?? "", NavFile)) : null; } return nav; }

        // a world position (x north, y west) as fractions of a floor's picture (0..1 from the left / top); outside the picture is < 0 or > 1
        public System.Windows.Point WorldToFraction(int floor, double x, double y) => Floors[floor].WorldToFraction(x, y);

        // the floor a height is on: the one whose height range holds it (3 yards of slack), else the nearest by its middle height; -1 = no floors
        public int FloorAt(double z)
        {
            int best = -1; double bd = double.MaxValue;
            for (int i = 0; i < Floors.Count; i++)
            {
                var f = Floors[i];
                double d = Score(f, z);
                if (d < bd) { bd = d; best = i; }
            }
            return best;
        }

        static double Score(DungeonPlanFloor f, double z)
        {
            double d = z >= f.MinZ - 3 && z <= f.MaxZ + 3 ? Math.Abs(z - f.MeanZ) / 1000 : Math.Abs(z - f.MeanZ);
            return f.IsPicture ? d + 0.5 : d;      // a drawn floor that holds it is the better answer
        }

        // the floor a world position is on: of the floors whose picture shows ground there (within 2 pixels), the one whose height range holds z, else the
        // nearest by height; no floor shows it = FloorAt(z). Reads the drawn floors once (any thread).
        public int FloorAt(double x, double y, double z)
        {
            var roles = Roles();
            int best = -1; double bd = double.MaxValue;
            for (int i = 0; i < Floors.Count; i++)
            {
                var f = Floors[i];
                var p = f.WorldToFraction(x, y);
                int px = (int)Math.Floor(p.X * f.Width), py = (int)Math.Floor(p.Y * f.Height);
                bool shows = f.IsPicture ? px >= 0 && py >= 0 && px < f.Width && py < f.Height : Shows(roles[i], f, px, py);
                if (!shows) continue;
                double d = Score(f, z);
                if (d < bd) { bd = d; best = i; }
            }
            return best >= 0 ? best : FloorAt(z);
        }

        static bool Shows(byte[] r, DungeonPlanFloor f, int px, int py)
        {
            if (r == null) return false;
            for (int dy = -2; dy <= 2; dy++)
                for (int dx = -2; dx <= 2; dx++)
                {
                    int x = px + dx, y = py + dy;
                    if (x < 0 || y < 0 || x >= f.Width || y >= f.Height) continue;
                    byte v = r[y * f.Width + x];
                    if (v != DungeonPlanImage.Empty && v != DungeonPlanImage.Ghost) return true;
                }
            return false;
        }

        byte[][] roles;
        byte[][] Roles()
        {
            if (roles != null) return roles;
            var res = new byte[Floors.Count][];
            for (int i = 0; i < Floors.Count; i++)
            {
                if (Floors[i].IsPicture) continue;
                var px = DungeonPlanImage.ReadPng(FloorPath(i), out var w, out var h);
                res[i] = px != null && w == Floors[i].Width && h == Floors[i].Height ? px : null;
            }
            return roles = res;
        }

        public string FloorPath(int i) => Path.Combine(Dir ?? "", Floors[i].File);

        public string ToJson()
        {
            return Json.Write(new Dictionary<string, object>
            {
                ["format"] = Format, ["mapId"] = MapId, ["name"] = Name, ["build"] = Build, ["source"] = Source, ["terrainRule"] = TerrainRule,
                ["buildings"] = Buildings, ["groups"] = Groups, ["tiles"] = Tiles, ["islands"] = Islands, ["ms"] = Ms, ["bytes"] = Bytes, ["nav"] = NavFile,
                ["floors"] = Floors.Select(f => (object)new Dictionary<string, object>
                {
                    ["name"] = f.Name, ["file"] = f.File, ["kind"] = f.Kind, ["minZ"] = f.MinZ, ["maxZ"] = f.MaxZ, ["meanZ"] = f.MeanZ, ["area"] = f.Area, ["triangles"] = f.Triangles, ["game"] = f.GameFile, ["gameCoverage"] = f.GameCoverage,
                    ["maxX"] = f.MaxX, ["maxY"] = f.MaxY, ["scale"] = f.Scale, ["width"] = f.Width, ["height"] = f.Height,
                }).ToList(),
            });
        }

        static double D(Dictionary<string, object> o, string k) => o != null && o.TryGetValue(k, out var v) && v is double d ? d : double.NaN;

        // null = damaged, another format, or a floor file missing
        public static DungeonPlan FromJson(string text, string dir)
        {
            try
            {
                var o = Json.Obj(text);
                if (o == null || o.Int("format") != Format) return null;
                var p = new DungeonPlan
                {
                    MapId = o.Int("mapId"), Name = o.Str("name") ?? "", Build = o.Str("build") ?? "", Source = o.Str("source") ?? "", TerrainRule = o.Str("terrainRule") ?? "",
                    Buildings = o.Int("buildings"), Groups = o.Int("groups"), Tiles = o.Int("tiles"), Islands = o.Int("islands"), Ms = o.Long("ms"), Bytes = o.Long("bytes"), Dir = dir,
                    NavFile = o.Str("nav") ?? "",
                };
                if (p.NavFile.Length > 0 && (p.NavFile.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || !File.Exists(Path.Combine(dir, p.NavFile)))) return null;
                foreach (var fo in o.List("floors").OfType<Dictionary<string, object>>())
                {
                    var f = new DungeonPlanFloor
                    {
                        Name = fo.Str("name") ?? "", File = fo.Str("file") ?? "", Kind = fo.Str("kind") == "picture" ? "picture" : "plan", MinZ = D(fo, "minZ"), MaxZ = D(fo, "maxZ"), MeanZ = D(fo, "meanZ"),
                        Area = D(fo, "area"), Triangles = fo.Int("triangles"), MaxX = D(fo, "maxX"), MaxY = D(fo, "maxY"), Scale = D(fo, "scale"), Width = fo.Int("width"), Height = fo.Int("height"),
                    };
                    f.GameFile = fo.Str("game") ?? ""; var gc = D(fo, "gameCoverage"); f.GameCoverage = double.IsNaN(gc) ? 0 : gc;
                    if (f.Width <= 0 || f.Height <= 0 || !(f.Scale > 0) || double.IsNaN(f.MaxX) || double.IsNaN(f.MaxY)) return null;
                    if (string.IsNullOrEmpty(f.File) || f.File.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || !File.Exists(Path.Combine(dir, f.File))) return null;
                    if (f.GameFile.Length > 0 && (f.IsPicture || f.GameFile.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || !File.Exists(Path.Combine(dir, f.GameFile)))) return null;
                    p.Floors.Add(f);
                }
                return p.Floors.Count == 0 ? null : p;
            }
            catch (Exception) { return null; }
        }
    }

    public sealed class DungeonPlanState
    {
        public int MapId;
        public string Name = "";
        public string Phase = "idle";             // idle | counting | ask | drawing | done | failed | unavailable
        public int Done, Total;                   // files read while drawing
        public double Seconds;                    // the estimate
        public long Bytes;
        public string Error = "";
        public DungeonPlan Plan;

        // "Drawing Deadmines from your game files: 120 / 300 parts", the question, the estimate line
        public string Sentence => Phase == "drawing" ? "Drawing " + Name + " from your game files" + (Total > 0 ? ": " + Done.ToString("N0", CultureInfo.InvariantCulture) + " / " + Total.ToString("N0", CultureInfo.InvariantCulture) + " parts" : "")
            : Phase == "counting" ? "Reading " + Name + " from your game files"
            : Phase == "ask" ? "Draw a plan of " + Name + " from your game files?" : "";
        public string EstimateText => DungeonPlans.EstimateText(Seconds, Bytes);
    }

    public static class DungeonPlans
    {
        public static Func<string> WowRoot = () => MapData.WowRoot();
        public static Func<string> Flavor = () => MapData.Flavor();
        public static Func<bool> Enabled = () => MapData.Enabled();
        public static string RootOverride;                                        // self-test: a scratch folder
        public static string Root => RootOverride ?? Path.Combine(Util.DataDir, "atlas", "dungeonmaps");
        public static Func<int, IBuildingSource> SourceForTest;                    // self-test: the files of a synthetic install (by map id)
        public static double AutoSeconds = 30;                                     // drawn without asking when the estimate is under this
        public static double SecondsPerGroup = 0.03, SecondsPerTile = 0.08, SecondsBase = 0.5, BytesPerGroup = 3000, BytesPerTile = 8000;     // build 1.60.1.70338: 0.4-15 s, 50-500 KB per instance
        public const double InteriorShare = 0.2, InteriorArea = 5000;
        public static int BuildCountForTest;
        public static double DelayMsForTest;                                       // self-test: a pause per file so the progress state can be seen
        public static event Action<int> Changed;                                   // on a worker thread

        static readonly object gate = new object();
        static readonly Dictionary<int, DungeonPlanState> states = new Dictionary<int, DungeonPlanState>();
        static readonly Dictionary<int, Task> tasks = new Dictionary<int, Task>();
        static readonly SemaphoreSlim one = new SemaphoreSlim(1, 1);

        public static string Key => (MapData.BuildStamp ?? "unknown") + "_v" + DungeonPlan.Format;
        public static string DirOf(int mapId) => Path.Combine(Root, Key, mapId.ToString(CultureInfo.InvariantCulture));
        static void Fire(int mapId) { try { Changed?.Invoke(mapId); } catch (Exception) { } }

        public static void ResetForTest() { lock (gate) { states.Clear(); tasks.Clear(); } BuildCountForTest = 0; }

        public static DungeonPlanState StateOf(int mapId) { lock (gate) return states.TryGetValue(mapId, out var s) ? s : null; }

        // can a plan be drawn at all: map data is there, the map has a WDT, the game files can be read (or a test source is set)
        public static bool CanDraw(int mapId)
        {
            try
            {
                if (!Enabled()) return false;
                var m = MapData.Index?.GameMap(mapId);
                if (m == null || m.WdtFileDataId <= 0) return false;
                if (SourceForTest != null) return true;
                var root = WowRoot();
                return !string.IsNullOrEmpty(root) && Directory.Exists(root);
            }
            catch (Exception) { return false; }
        }

        // the plan on disk for this game build (null = none, damaged, or another build)
        public static DungeonPlan Cached(int mapId)
        {
            try
            {
                var dir = DirOf(mapId);
                var f = Path.Combine(dir, "plan.json");
                if (!File.Exists(f)) return null;
                var p = DungeonPlan.FromJson(File.ReadAllText(f), dir);
                return p != null && p.MapId == mapId && p.Build == Key ? p : null;
            }
            catch (Exception) { return null; }
        }

        static DungeonPlanState New(int mapId)
        {
            var name = MapData.Index?.Instance(mapId)?.Name ?? MapData.Index?.GameMap(mapId)?.Name ?? ("map " + mapId);
            return new DungeonPlanState { MapId = mapId, Name = name };
        }

        // The first look: done when it is on disk; else count and estimate, then draw at once when it is quick, or wait for Start (phase "ask").
        public static Task Request(int mapId, bool startEvenIfBig = false)
        {
            lock (gate)
            {
                if (tasks.TryGetValue(mapId, out var running) && !running.IsCompleted) return running;
                if (!states.TryGetValue(mapId, out var st)) states[mapId] = st = New(mapId);
                var cached = Cached(mapId);
                if (cached != null) { st.Phase = "done"; st.Plan = cached; st.Error = ""; Fire(mapId); return Task.FromResult(0); }
                if (!CanDraw(mapId)) { st.Phase = "unavailable"; st.Error = "the game files cannot be read"; Fire(mapId); return Task.FromResult(0); }
                if (st.Phase == "ask" && !startEvenIfBig) return Task.FromResult(0);
                st.Phase = "counting"; st.Error = ""; st.Done = st.Total = 0;
                var t = Task.Factory.StartNew(() => Run(st, startEvenIfBig), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                tasks[mapId] = t;
                Fire(mapId);
                return t;
            }
        }

        // "Draw the map" after an estimate (also "Try again" after a failure)
        public static Task Start(int mapId) => Request(mapId, true);

        public static string EstimateText(double seconds, long bytes)
        {
            string t = seconds < 10 ? "a few seconds" : seconds < 50 ? "about " + (int)(Math.Ceiling(seconds / 5) * 5) + " seconds" : seconds < 90 ? "about a minute" : "about " + (int)Math.Ceiling(seconds / 60) + " minutes";
            return t + ", " + WorldMapCache.SizeText(Math.Max(bytes, 32 * 1024)) + " on disk";
        }

        sealed class Opened : IDisposable
        {
            public IBuildingSource Src; public CascStorage Casc; public WdtFile Wdt;
            public void Dispose() { try { Src?.Dispose(); } catch (Exception) { } try { Casc?.Dispose(); } catch (Exception) { } }
        }

        static Opened Open(int mapId)
        {
            var m = MapData.Index?.GameMap(mapId);
            if (m == null || m.WdtFileDataId <= 0) throw new InvalidDataException("the map has no world table in the game data");
            var o = new Opened();
            try
            {
                if (SourceForTest != null) o.Src = SourceForTest(mapId);
                else { o.Casc = CascStorage.Open(WowRoot(), Flavor() ?? "_classic_beta_"); o.Src = new CascBuildingSource(o.Casc); }
                var wb = o.Src.Read(m.WdtFileDataId);
                if (wb == null) throw new FileNotFoundException("the world table of " + (m.Name ?? "the map") + " is not in this game install");
                o.Wdt = WdtFile.Parse(wb);
                if (o.Wdt.GlobalWmo == null && !o.Wdt.Ok) throw new InvalidDataException(o.Wdt.Problem ?? "the world table could not be read");
                return o;
            }
            catch { o.Dispose(); throw; }
        }

        static void LoadStats()
        {
            try
            {
                var o = Json.Obj(File.ReadAllText(Path.Combine(Root, "stats.json")));
                double g = o.TryGetValue("secPerGroup", out var a) && a is double da ? da : 0, t = o.TryGetValue("secPerTile", out var b) && b is double db ? db : 0;
                if (g > 0 && g < 5) SecondsPerGroup = g;
                if (t > 0 && t < 30) SecondsPerTile = t;
            }
            catch (Exception) { }
        }

        static void SaveStats(double perGroup, double perTile)
        {
            try
            {
                Directory.CreateDirectory(Root);
                File.WriteAllText(Path.Combine(Root, "stats.json"), Json.Write(new Dictionary<string, object> { ["secPerGroup"] = perGroup, ["secPerTile"] = perTile }));
            }
            catch (Exception) { }
        }

        static void Run(DungeonPlanState st, bool go)
        {
            try { Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; } catch (Exception) { }
            int mapId = st.MapId;
            one.Wait();
            try
            {
                var cached = Cached(mapId);
                if (cached != null) { st.Plan = cached; st.Phase = "done"; return; }
                LoadStats();
                using (var o = Open(mapId))
                {
                    // count: the buildings and their groups, the tiles
                    var places = DungeonGeometry.Buildings(o.Wdt, o.Src, out _);
                    int groups = 0;
                    foreach (var id in places.Select(p => (int)p.NameId).Distinct())
                    {
                        var r = WmoRoot.Parse(o.Src.Read(id));
                        if (r.Ok) groups += r.GroupInfos.Count(g => g.Fdid != 0) * places.Count(p => p.NameId == id);
                    }
                    int tiles = o.Wdt.GlobalWmo != null ? 0 : o.Wdt.Tiles.Count(t => t.RootFdid != 0);
                    st.Seconds = SecondsBase + groups * SecondsPerGroup + tiles * SecondsPerTile;
                    st.Bytes = (long)(groups * BytesPerGroup + tiles * BytesPerTile);
                    st.Total = groups + tiles;
                    if (!go && st.Seconds > AutoSeconds) { st.Phase = "ask"; return; }
                    st.Phase = "drawing"; Fire(mapId);
                    var plan = Build(mapId, st.Name, o, (d, t) =>
                    {
                        st.Done = d; st.Total = t;
                        if (DelayMsForTest > 0) Thread.Sleep((int)DelayMsForTest);
                        if (d % 16 == 0 || d == t) Fire(mapId);
                    });
                    st.Plan = plan; st.Phase = "done"; st.Error = "";
                    // measured: a tile costs about as much as four groups; the next estimate starts from this one
                    double unit = Math.Max(0.0005, (plan.Ms / 1000.0 - SecondsBase) / Math.Max(1, groups + tiles * 4.0));
                    if (groups + tiles > 0) SaveStats(unit, unit * 4);
                    RemoveOtherBuilds();
                }
            }
            catch (Exception e)
            {
                Util.Log("dungeon plan " + st.Name + ": " + e.Message);
                st.Phase = "failed"; st.Error = e.Message;
            }
            finally { one.Release(); Fire(mapId); }
        }

        // read, split, draw, write: the plan of one map (the caller opened its files)
        static DungeonPlan Build(int mapId, string name, Opened o, Action<int, int> progress)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Interlocked.Increment(ref BuildCountForTest);
            var geo = DungeonGeometry.Collect(o.Wdt, o.Src, CancellationToken.None, progress);
            if (!geo.Ok) throw new InvalidDataException(geo.Problem ?? "nothing to draw");
            // the game's own minimap pictures of the buildings (when the install has them): laid onto the drawn floors
            var mini = DungeonMinimap.Index(o.Src);
            if (!DungeonMinimap.Any(geo, mini)) mini = null;
            var pics = new Dictionary<int, DungeonMinimap.Picture>();
            DungeonMinimap.Picture Tex(int fdid) { if (!pics.TryGetValue(fdid, out var t)) pics[fdid] = t = DungeonMinimap.Decode(o.Src.Read(fdid)); return t; }
            var dir = DirOf(mapId);
            if (Directory.Exists(dir)) Util.DeleteDir(dir);
            Directory.CreateDirectory(dir);
            DungeonPlanFloor outside = null;
            if (geo.TerrainUsed) outside = Outside(o, geo, dir);
            DungeonPlan plan;
            if (outside != null)
            {
                // the minimap shows the grounds; the interiors are drawn when they are a real part of the instance
                var inside = geo.Units.Where(u => u.Kind == PlanUnitKind.Building && u.Interior).ToList();
                double all = geo.Units.Where(u => u.Kind == PlanUnitKind.Building).Sum(u => u.Area), inArea = inside.Sum(u => u.Area);
                var sub = new PlanGeometry { Units = inside, Buildings = geo.Buildings, Groups = geo.Groups, Tiles = geo.Tiles, TerrainUsed = true, TerrainRule = geo.TerrainRule, Places = geo.Places, Roots = geo.Roots, Walls = geo.Walls };
                plan = inside.Count > 0 && inArea >= all * InteriorShare && inArea >= InteriorArea ? Make(mapId, name, sub, dir, outside, mini, Tex) : Make(mapId, name, null, dir, outside);
                plan.Source = plan.Floors.Count > 1 ? "minimap and buildings" : "minimap";
            }
            else plan = Make(mapId, name, geo, dir, null, mini, Tex);
            plan.Buildings = geo.Buildings; plan.Groups = geo.Groups; plan.Tiles = geo.Tiles; plan.TerrainRule = geo.TerrainRule; plan.Islands = geo.Islands;
            plan.Ms = sw.ElapsedMilliseconds;
            File.WriteAllText(Path.Combine(dir, "plan.json"), plan.ToJson());
            Util.Log("dungeon plan " + name + ": " + plan.Floors.Count + " floors (" + string.Join(", ", plan.Floors.Select(f => f.Name + " " + f.Width + " x " + f.Height)) + "), " + plan.Groups + " groups, " + plan.Tiles + " tiles, " + geo.TerrainRule + ", " + plan.Ms + " ms, " + plan.Bytes + " bytes; the game's minimap pictures on " + plan.Floors.Count(f => f.HasGame) + " floors (" + string.Join(", ", plan.Floors.Where(f => f.HasGame).Select(f => Math.Round(f.GameCoverage * 100) + " %")) + ")");
            return plan;
        }

        // the minimap pictures of the map's tiles, side by side (null = the map has none in this install)
        static DungeonPlanFloor Outside(Opened o, PlanGeometry geo, string dir)
        {
            var tiles = WdtFile.WithoutStrays(o.Wdt.Tiles.Where(t => t.MinimapFdid != 0).ToList(), 12, null);
            if (tiles.Count == 0) return null;
            o.Src.Prefetch(tiles.Select(t => t.MinimapFdid));
            var imgs = new Dictionary<WdtTile, byte[]>(); int size = 0;
            foreach (var t in tiles)
            {
                var b = o.Src.Read(t.MinimapFdid);
                if (b == null) continue;
                try { var px = Blp.Decode(b, out var w, out var h); if (px == null || w != h || w < 16) continue; if (size == 0) size = w; if (w != size) continue; imgs[t] = px; }
                catch (Exception) { }
            }
            if (imgs.Count == 0) return null;
            int c0 = imgs.Keys.Min(t => t.Col), c1 = imgs.Keys.Max(t => t.Col), r0 = imgs.Keys.Min(t => t.Row), r1 = imgs.Keys.Max(t => t.Row);
            int W = (c1 - c0 + 1) * size, H = (r1 - r0 + 1) * size;
            var all = new byte[W * H * 4];
            foreach (var kv in imgs)
            {
                int x0 = (kv.Key.Col - c0) * size, y0 = (kv.Key.Row - r0) * size;
                for (int y = 0; y < size; y++) Buffer.BlockCopy(kv.Value, y * size * 4, all, ((y0 + y) * W + x0) * 4, size * 4);
            }
            double tile = 1600.0 / 3, maxX = (32 - r0) * tile, maxY = (32 - c0) * tile, sc = size / tile;
            // cut to the terrain that was kept (DungeonGeometry crops it to the buildings' extent: a map's outer tiles are often filler borrowed from other zones)
            var terrain = geo.Units.Where(u => u.Kind == PlanUnitKind.Terrain).ToList();
            if (terrain.Count > 0)
            {
                double x0 = double.MaxValue, x1 = double.MinValue, y0 = double.MaxValue, y1 = double.MinValue;
                foreach (var u in terrain) for (int i = 0; i + 3 <= u.Tri.Count; i += 3) { x0 = Math.Min(x0, u.Tri[i]); x1 = Math.Max(x1, u.Tri[i]); y0 = Math.Min(y0, u.Tri[i + 1]); y1 = Math.Max(y1, u.Tri[i + 1]); }
                int px0 = Math.Max(0, (int)Math.Floor((maxY - y1) * sc)), px1 = Math.Min(W, (int)Math.Ceiling((maxY - y0) * sc));
                int py0 = Math.Max(0, (int)Math.Floor((maxX - x1) * sc)), py1 = Math.Min(H, (int)Math.Ceiling((maxX - x0) * sc));
                if (px1 - px0 >= 16 && py1 - py0 >= 16 && (px1 - px0 < W || py1 - py0 < H))
                {
                    int nw = px1 - px0, nh = py1 - py0;
                    var cut = new byte[nw * nh * 4];
                    for (int y = 0; y < nh; y++) Buffer.BlockCopy(all, ((py0 + y) * W + px0) * 4, cut, y * nw * 4, nw * 4);
                    all = cut; W = nw; H = nh; maxX -= py0 / sc; maxY -= px0 / sc;
                }
            }
            // halved until it fits MaxSide (the world map's tiles are the same pictures), stored as a JPEG like the world map's
            while (Math.Max(W, H) > DungeonPlanImage.MaxSide && W > 32 && H > 32) { all = Halve(all, W, H); W /= 2; H /= 2; sc /= 2; }
            WriteJpeg(Path.Combine(dir, "outside.jpg"), all, W, H);
            double lo = double.MaxValue, hi = double.MinValue, sz = 0, sa = 0;
            foreach (var u in terrain) for (int i = 0; i + 9 <= u.Tri.Count; i += 9) { for (int k = 0; k < 3; k++) { lo = Math.Min(lo, u.Tri[i + k * 3 + 2]); hi = Math.Max(hi, u.Tri[i + k * 3 + 2]); } }
            foreach (var u in terrain) { sa += u.Area; for (int i = 0; i + 9 <= u.Tri.Count; i += 9) sz += (u.Tri[i + 2] + u.Tri[i + 5] + u.Tri[i + 8]) / 3; }
            int nt = terrain.Sum(u => u.TriCount);
            return new DungeonPlanFloor
            {
                Name = "Outside", File = "outside.jpg", Kind = "picture", Width = W, Height = H, Scale = sc,
                MaxX = maxX, MaxY = maxY, MinZ = lo == double.MaxValue ? 0 : lo, MaxZ = hi == double.MinValue ? 0 : hi, MeanZ = nt == 0 ? 0 : sz / nt, Area = Math.Round(sa), Triangles = nt,
            };
        }

        static byte[] Halve(byte[] src, int w, int h)
        {
            int nw = w / 2, nh = h / 2;
            var dst = new byte[nw * nh * 4];
            for (int y = 0; y < nh; y++)
                for (int x = 0; x < nw; x++)
                {
                    int s0 = ((y * 2) * w + x * 2) * 4, d = (y * nw + x) * 4;
                    for (int c = 0; c < 3; c++) dst[d + c] = (byte)((src[s0 + c] + src[s0 + 4 + c] + src[s0 + w * 4 + c] + src[s0 + w * 4 + 4 + c] + 2) >> 2);
                    dst[d + 3] = 255;
                }
            return dst;
        }

        static void WriteJpeg(string path, byte[] bgra, int w, int h)
        {
            var bmp = System.Windows.Media.Imaging.BitmapSource.Create(w, h, 96, 96, System.Windows.Media.PixelFormats.Bgr32, null, bgra, w * 4);
            bmp.Freeze();
            var enc = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = WorldMapPyramid.JpegQuality };
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            var tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None)) enc.Save(fs);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        // the drawn floors of collected geometry (null = none), after an optional picture floor, written to dir (also used by the self-test on synthetic geometry)
        // mini / tex: the game's minimap pictures (DungeonMinimap) to lay onto each drawn floor that they cover at least MinCoverage of (null = none)
        public static DungeonPlan Make(int mapId, string name, PlanGeometry geo, string dir, DungeonPlanFloor first = null, WmoMinimapIndex mini = null, Func<int, DungeonMinimap.Picture> tex = null)
        {
            Directory.CreateDirectory(dir);
            var plan = new DungeonPlan
            {
                MapId = mapId, Name = name ?? "", Build = Key, Dir = dir,
                Source = geo == null ? "minimap" : geo.GlobalWmo ? "building" : geo.TerrainUsed ? (geo.Groups > 0 ? "terrain and buildings" : "terrain") : "buildings",
            };
            long bytes = 0;
            if (first != null) { plan.Floors.Add(first); bytes += new FileInfo(Path.Combine(dir, first.File)).Length; }
            if (geo != null)
            {
                var floors = DungeonFloors.Split(geo, name);
                plan.Islands = geo.Islands;
                if (floors.Count == 0 && first == null) throw new InvalidDataException("nothing to draw");
                if (floors.Count > 0)
                {
                    var fr = DungeonPlanImage.FrameOf(floors);
                    var pics = DungeonPlanImage.DrawAll(floors, fr);
                    for (int i = 0; i < floors.Count; i++)
                    {
                        var file = "floor" + (i + 1).ToString(CultureInfo.InvariantCulture) + ".png";
                        var path = Path.Combine(dir, file);
                        DungeonPlanImage.WritePng(path, pics[i], fr.Width, fr.Height);
                        bytes += new FileInfo(path).Length;
                        var f = floors[i];
                        var pf = new DungeonPlanFloor
                        {
                            Name = f.Name, File = file, MinZ = f.MinZ, MaxZ = f.MaxZ, MeanZ = f.MeanZ, Area = Math.Round(f.Area), Triangles = f.Triangles,
                            MaxX = fr.MaxX, MaxY = fr.MaxY, Scale = fr.Scale, Width = fr.Width, Height = fr.Height,
                        };
                        if (mini != null && tex != null)
                        {
                            var g = DungeonMinimap.Compose(f, fr, pics[i], geo, mini, tex);
                            pf.GameCoverage = Math.Round(g.Coverage, 4);
                            if (g.Coverage >= DungeonMinimap.MinCoverage)
                            {
                                pf.GameFile = "floor" + (i + 1).ToString(CultureInfo.InvariantCulture) + "-game.png";
                                DungeonMinimap.WritePng(Path.Combine(dir, pf.GameFile), g);
                                bytes += new FileInfo(Path.Combine(dir, pf.GameFile)).Length;
                            }
                        }
                        plan.Floors.Add(pf);
                    }
                    // where a character can walk on the drawn floors (the suggested route of the mob layer)
                    int off = first != null ? 1 : 0;
                    var nav = DungeonNav.Build(floors.Select((f, i) => new KeyValuePair<int, IList<PlanUnit>>(i + off, f.Units)).ToList(), geo.Walls);
                    if (nav.Count > 0)
                    {
                        plan.NavFile = "nav.bin";
                        nav.Save(Path.Combine(dir, plan.NavFile));
                        bytes += new FileInfo(Path.Combine(dir, plan.NavFile)).Length;
                    }
                }
            }
            if (plan.Floors.Count == 0) throw new InvalidDataException("nothing to draw");
            // "Level 2" after an "Outside": the drawn floors count from 1 among themselves
            if (first != null) { int n = 0; foreach (var f in plan.Floors.Skip(1)) { n++; if (f.Name.StartsWith("Level ", StringComparison.Ordinal)) f.Name = "Inside " + n; } }
            plan.Bytes = bytes;
            return plan;
        }

        public static int RemoveOtherBuilds()
        {
            int n = 0;
            try
            {
                if (!Directory.Exists(Root)) return 0;
                foreach (var d in Directory.GetDirectories(Root))
                    if (!string.Equals(Path.GetFileName(d), Key, StringComparison.OrdinalIgnoreCase)) { Util.DeleteDir(d); n++; }
            }
            catch (Exception) { }
            return n;
        }

        public static long SizeBytes()
        {
            try { return Directory.Exists(Root) ? new DirectoryInfo(Root).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0; }
            catch (Exception) { return 0; }
        }
    }
}
