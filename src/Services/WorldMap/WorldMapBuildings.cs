using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ElansAddonHub.Services.WorldMap
{
    // ============================================================================================================================
    // 3D buildings of the world map (the page's Buildings switch): the WMO placements of a continent (the MODF chunks of its obj0 ADTs) are scanned in a couple of
    // seconds, the number of buildings / minutes / disk space for the card comes from that scan, and then the building shapes are read from the player's own game
    // files in the background (WmoFile, WmoMesh) into  <map folder>\buildings\ :
    //   manifest.json       version, game build, mode state, counts, measured times (written last, so a damaged folder is never taken for a finished one)
    //   placements.bin      "WMP1" | int count | count x 60 bytes { uint fdid, uint flags (1 = bridge), float pos[3], float rot[3] (degrees), float scale, float min[3], float max[3] }
    //                       positions are in the scene's axes already (see AdtObjects); one record per placed building, a building that crosses a tile edge once
    //   m\{fdid}.wmb        one simplified model per distinct building (WmoModel); a stub for one with nothing to draw, so it is not tried again
    //   texcolors.bin       "TXC1" | int count | count x { uint fdid, uint rgb }: the average colour of the textures the models were tinted from
    // Each file is written as .tmp and renamed, so a cancelled or crashed build resumes with the models that are done; a new game build is a new folder.
    //
    // Bridges: a building is a bridge when it is long and narrow (width <= 0.45 x length, or <= 0.55 x length when the ground is a gap all along the middle; height <= 1.5 x length, 10 - 150 yards
    // long, at most 4 groups) AND placed across a gap: along its long axis, the ground or a liquid surface is at least 2 yards below the model's origin (its deck) in the middle part
    // and the ground at both ends is dry and not more than 9 yards below the deck. Checked against the community names of the Eastern Kingdoms' WMOs (offline, by the
    // author, nothing of it ships): see ARCHITECTURE.md.
    // ============================================================================================================================
    public static class WmoTransform
    {
        // the model's own point (WoW world axes: x north, y west, z up) in the scene: axes (y, z, x), scaled, rotated by Ry(rotY) Rx(rotX) Rz(rotZ) (three.js 'YXZ'), moved to the position
        public static void ToScene(WmoPlacement p, double vx, double vy, double vz, out double x, out double y, out double z)
        {
            double lx = vy * p.Scale, ly = vz * p.Scale, lz = vx * p.Scale;
            double ax = p.RotX * Math.PI / 180, ay = p.RotY * Math.PI / 180, az = p.RotZ * Math.PI / 180;
            // Rz first
            double cz = Math.Cos(az), sz = Math.Sin(az);
            double x1 = lx * cz - ly * sz, y1 = lx * sz + ly * cz, z1 = lz;
            // then Rx
            double cx = Math.Cos(ax), sx = Math.Sin(ax);
            double x2 = x1, y2 = y1 * cx - z1 * sx, z2 = y1 * sx + z1 * cx;
            // then Ry
            double cy = Math.Cos(ay), sy = Math.Sin(ay);
            x = x2 * cy + z2 * sy + p.X; y = y2 + p.Y; z = -x2 * sy + z2 * cy + p.Z;
        }
    }

    public interface IGroundSampler
    {
        double Ground(double x, double z);      // terrain height at a scene position; NaN = no terrain there
        double Water(double x, double z);       // liquid surface; NaN = none
    }

    // the ground and liquids of a continent from the terrain cache (.wmt / .wmw), tile by tile on demand
    public sealed class TerrainSampler : IGroundSampler
    {
        readonly string dir;
        readonly Dictionary<int, TerrainTile> ground = new Dictionary<int, TerrainTile>();
        readonly Dictionary<int, WaterTile> water = new Dictionary<int, WaterTile>();
        const double Ty = 1600.0 / 3;
        public TerrainSampler(string terrainDir) { dir = terrainDir; }

        public double Ground(double x, double z)
        {
            int col = (int)Math.Floor(x / Ty), row = (int)Math.Floor(z / Ty);
            if (col < 0 || row < 0 || col > 63 || row > 63) return double.NaN;
            int key = col * 64 + row;
            if (!ground.TryGetValue(key, out var t)) { t = WorldMapTerrain.ReadTile(dir, col, row); ground[key] = t; }
            if (t == null) return double.NaN;
            double st = Ty / 64, fc = (x - col * Ty) / st, fr = (z - row * Ty) / st;
            int c = Math.Min(63, (int)fc), r = Math.Min(63, (int)fr); double ac = fc - c, ar = fr - r;
            double range = t.MaxZ - t.MinZ;
            Func<int, int, double> h = (rr, cc) => t.MinZ + t.H[rr * 65 + cc] / 65535.0 * range;
            return (h(r, c) * (1 - ac) + h(r, c + 1) * ac) * (1 - ar) + (h(r + 1, c) * (1 - ac) + h(r + 1, c + 1) * ac) * ar;
        }

        public double Water(double x, double z)
        {
            int col = (int)Math.Floor(x / Ty), row = (int)Math.Floor(z / Ty);
            if (col < 0 || row < 0 || col > 63 || row > 63) return double.NaN;
            int key = col * 64 + row;
            if (!water.TryGetValue(key, out var t)) { t = WorldMapTerrain.ReadWater(dir, col, row); water[key] = t; }
            if (t == null) return double.NaN;
            double st = Ty / 128;
            int X = Math.Min(127, (int)((x - col * Ty) / st)), Y = Math.Min(127, (int)((z - row * Ty) / st));
            return t.SurfaceAt((Y / 8) * 16 + X / 8, X % 8, Y % 8);
        }
    }

    public static class BridgeRules
    {
        public const double MaxWidthOfLength = 0.45, FatWidthOfLength = 0.55, MaxHeightOfLength = 1.5, MinLength = 10, MaxLength = 150, MinDrop = 2.0, EndBelowDeck = 9;
        public const int MaxGroups = 4;

        // is this placement of this building a bridge? (see the class comment of WorldMapBuildings)
        public static bool IsBridge(WmoRoot r, WmoPlacement p, IGroundSampler g)
        {
            if (r == null || !r.Ok || g == null || r.Groups > MaxGroups) return false;
            double s = p.Scale, dx = (r.MaxY - r.MinY) * s, dz = (r.MaxX - r.MinX) * s, h = (r.MaxZ - r.MinZ) * s;
            bool alongX = dx >= dz; double L = Math.Max(dx, dz), W = Math.Min(dx, dz);
            if (L < MinLength || L > MaxLength || W > L * FatWidthOfLength || h > L * MaxHeightOfLength) return false;
            bool slim = W <= L * MaxWidthOfLength;           // a broader building (a bridge with piers and towers sticking out) needs the strongest evidence: every sample across the middle is a gap
            double cx = (r.MinY + r.MaxY) / 2 * s, cz = (r.MinX + r.MaxX) / 2 * s;
            double th = p.RotY * Math.PI / 180, c = Math.Cos(th), sn = Math.Sin(th);
            double ctrx = cx * c + cz * sn + p.X, ctrz = -cx * sn + cz * c + p.Z;
            double ux = alongX ? 1 : 0, uz = alongX ? 0 : 1;
            double dirx = ux * c + uz * sn, dirz = -ux * sn + uz * c, half = L / 2, deck = p.Y;
            int gaps = 0;
            foreach (var t in new[] { -0.4, -0.2, 0, 0.2, 0.4 })
            {
                double x = ctrx + dirx * half * t, z = ctrz + dirz * half * t, gr = g.Ground(x, z), wa = g.Water(x, z);
                if (double.IsNaN(gr)) continue;
                if (!double.IsNaN(wa) && wa > gr + 0.3) gaps++; else if (deck - gr >= MinDrop) gaps++;
            }
            if (slim ? gaps < 2 : gaps < 5) return false;
            bool d1 = false, d2 = false;
            foreach (var t in new[] { 0.85, 1.0, 1.15 })
            {
                d1 |= Dry(g, ctrx - dirx * half * t, ctrz - dirz * half * t, deck);
                d2 |= Dry(g, ctrx + dirx * half * t, ctrz + dirz * half * t, deck);
            }
            return d1 && d2;
        }

        static bool Dry(IGroundSampler g, double x, double z, double deck)
        {
            double gr = g.Ground(x, z), wa = g.Water(x, z);
            if (double.IsNaN(gr)) return false;
            if (!double.IsNaN(wa) && wa > gr + 0.3) return false;
            return gr >= deck - EndBelowDeck;
        }
    }

    // a distinct building of a continent as the scan found it
    public sealed class BuildingModelInfo
    {
        public int Fdid;
        public int Groups, OutsideGroups;
        public int ExteriorLitGroups;       // groups open to the sky but not flagged exterior (the big cities' streets): drawn since 2.89 (Rules 2)
        public bool Bridge;                 // at least one placement is a bridge
        public int Placements, BridgePlacements;
        public List<int> GroupFdids = new List<int>();
        public List<uint> TextureFdids = new List<uint>();
        public bool RootMissing;
        public bool LaterCopy;              // its root, a group or a texture is read from a later copy of a tie in the root (3.1.1): before, it was missing (Rules 3)
    }

    public sealed class BuildingScan
    {
        public List<WmoPlacement> Placements = new List<WmoPlacement>();
        public List<bool> Bridge = new List<bool>();
        public Dictionary<int, BuildingModelInfo> Models = new Dictionary<int, BuildingModelInfo>();
        public int Tiles, NameIndexSkipped, Duplicates;
        public long Ms;
    }

    public sealed class BuildingEstimate
    {
        public string Mode = "";
        public int Buildings, Models, ModelsDone;
        public double Minutes;
        public long Bytes;                  // when finished, all of it on disk
    }

    public sealed class BuildingsState
    {
        public int MapId;
        public string Name = "", Mode = "off";
        public string Phase = "idle";       // idle | scanning | ask | building | paused | done | error
        public int Done, Total;
        public double SecondsLeft;
        public string Error;
        public BuildingEstimate Estimate;
        public long BytesOnDisk;
        public string Text
        {
            get
            {
                if (Error != null) return Error;
                string who = Name.Length > 0 ? Name : "the continent";
                var n = Total.ToString("N0", CultureInfo.InvariantCulture);
                switch (Phase)
                {
                    case "scanning": return "Counting the buildings of " + who + "...";
                    case "building": return "Building " + who + ": " + Done.ToString("N0", CultureInfo.InvariantCulture) + " / " + n + " buildings" + (SecondsLeft > 0 ? " - " + LeftText(SecondsLeft) : "");
                    case "paused": return "Paused: " + Done.ToString("N0", CultureInfo.InvariantCulture) + " / " + n + " buildings";
                    default: return "";
                }
            }
        }
        public static string LeftText(double sec)
        {
            if (sec < 45) return "less than a minute left";
            int m = (int)Math.Round(sec / 60);
            return Math.Max(1, m) + " min left";
        }
    }

    // where the files come from: the game's CASC, or a test's stand-in
    public interface IBuildingSource : IDisposable
    {
        void Prefetch(IEnumerable<int> fdids);       // look up many ids at once (the root file is walked once)
        byte[] Read(int fdid);                        // null = not in the install; never throws for a single missing / damaged file
        byte[] ReadTexture(int fdid);                 // a texture reduced to what the average colour needs (a small mip level); null = not in the install
        bool LaterCopy(int fdid) => false;            // the file is read from a later copy of a tie in the game's root (CascStorage.TookLaterCopy)
        Dictionary<string, int> Unread() => null;     // the files this source could not give, "<kind>:<reason>" (kind building | texture, reason as PendingTile.Reason) -> count
    }

    public sealed class CascBuildingSource : IBuildingSource
    {
        readonly CascStorage casc;
        readonly Dictionary<int, byte[]> keys = new Dictionary<int, byte[]>();
        readonly HashSet<int> asked = new HashSet<int>();
        public CascBuildingSource(CascStorage c) { casc = c; casc.ZeroFillEncrypted = false; }
        public void Prefetch(IEnumerable<int> fdids)
        {
            var want = new HashSet<int>(fdids.Where(i => i > 0 && !asked.Contains(i)));
            if (want.Count == 0) return;
            foreach (var w in want) asked.Add(w);
            try { foreach (var kv in casc.ResolveFileIds(want)) keys[kv.Key] = kv.Value; } catch (Exception e) { Util.Log("world map buildings: lookup: " + e.Message); }
        }
        readonly Dictionary<string, int> unread = new Dictionary<string, int>();
        internal static void Note(Dictionary<string, int> d, string kind, string reason) { lock (d) { var k = kind + ":" + reason; d.TryGetValue(k, out var n); d[k] = n + 1; } }
        public Dictionary<string, int> Unread() { lock (unread) return new Dictionary<string, int>(unread); }
        public bool LaterCopy(int fdid) => casc.TookLaterCopy(fdid);
        bool Key(int fdid, out byte[] ck)
        {
            if (keys.TryGetValue(fdid, out ck)) return true;
            if (!asked.Contains(fdid)) { Prefetch(new[] { fdid }); if (keys.TryGetValue(fdid, out ck)) return true; }
            return false;
        }
        public byte[] Read(int fdid)
        {
            if (!Key(fdid, out var ck)) { Note(unread, "building", "unlisted"); return null; }
            try { var b = casc.ReadByContentKey(ck); if (b == null) Note(unread, "building", "missing"); return b; } catch (Exception e) { Note(unread, "building", WorldMapPending.ReasonOf(e)); return null; }
        }
        public byte[] ReadTexture(int fdid)
        {
            if (!Key(fdid, out var ck)) { Note(unread, "texture", "unlisted"); return null; }
            try
            {
                var h = casc.ReadRangeByContentKey(ck, 0, 148);
                if (h == null) { Note(unread, "texture", "missing"); return null; }
                if (h.Length < 148 || h[0] != 'B' || h[1] != 'L' || h[2] != 'P' || h[3] != '2' || BitConverter.ToUInt32(h, 4) != 1) { Note(unread, "texture", "unreadable"); return null; }
                int w = BitConverter.ToInt32(h, 12), ht = BitConverter.ToInt32(h, 16), k = 0;
                while (k < 15 && (w >> (k + 1)) >= 8 && (ht >> (k + 1)) >= 8 && BitConverter.ToUInt32(h, 20 + (k + 1) * 4) != 0 && BitConverter.ToUInt32(h, 84 + (k + 1) * 4) != 0) k++;
                int off = (int)BitConverter.ToUInt32(h, 20 + k * 4), size = (int)BitConverter.ToUInt32(h, 84 + k * 4);
                if (off <= 0 || size <= 0 || size > (16 << 20)) { Note(unread, "texture", "unreadable"); return null; }
                int hdr = h[8] == 1 ? 148 + 1024 : 148;
                var head = hdr == 148 ? h : casc.ReadRangeByContentKey(ck, 0, hdr);
                var data = casc.ReadRangeByContentKey(ck, off, size);
                if (head == null || head.Length < hdr || data == null || data.Length < size) { Note(unread, "texture", "unreadable"); return null; }
                var o = new byte[hdr + size];
                Buffer.BlockCopy(head, 0, o, 0, hdr); Buffer.BlockCopy(data, 0, o, hdr, size);
                for (int i = 0; i < 16; i++) { Buffer.BlockCopy(BitConverter.GetBytes(0), 0, o, 20 + i * 4, 4); Buffer.BlockCopy(BitConverter.GetBytes(0), 0, o, 84 + i * 4, 4); }
                Buffer.BlockCopy(BitConverter.GetBytes(hdr), 0, o, 20, 4); Buffer.BlockCopy(BitConverter.GetBytes(size), 0, o, 84, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(Math.Max(1, w >> k)), 0, o, 12, 4); Buffer.BlockCopy(BitConverter.GetBytes(Math.Max(1, ht >> k)), 0, o, 16, 4);
                return o;
            }
            catch (Exception e) { Note(unread, "texture", WorldMapPending.ReasonOf(e)); return null; }
        }
        public void Dispose() { casc.Dispose(); }
    }

    public sealed class FuncBuildingSource : IBuildingSource
    {
        readonly Func<int, byte[]> read;
        public int Reads;
        public Func<int, bool> Later;                 // self-test: the ids a "later copy" gave
        readonly Dictionary<string, int> unread = new Dictionary<string, int>();
        public FuncBuildingSource(Func<int, byte[]> r) { read = r; }
        public void Prefetch(IEnumerable<int> fdids) { }
        // the self-test's reader says why with FileNotThereException ("unlisted" / "missing"); null is "missing", any other exception a read error
        byte[] Get(int fdid, string kind)
        {
            Interlocked.Increment(ref Reads);
            try { var b = read(fdid); if (b == null) CascBuildingSource.Note(unread, kind, "missing"); return b; }
            catch (Exception e) { CascBuildingSource.Note(unread, kind, WorldMapPending.ReasonOf(e)); return null; }
        }
        public byte[] Read(int fdid) => Get(fdid, "building");
        public byte[] ReadTexture(int fdid) => Get(fdid, "texture");
        public bool LaterCopy(int fdid) => Later != null && Later(fdid);
        public Dictionary<string, int> Unread() { lock (unread) return new Dictionary<string, int>(unread); }
        public void Dispose() { }
    }

    public static class WorldMapBuildings
    {
        public const int Version = 1;
        // which groups a model is made of: 1 = exterior groups only (before 2.89), 2 = exterior and exterior-lit ones (WmoRoot.OutsideGroups). A finished folder made by an
        // older rule is not ready: the models it would make differently (ExteriorLitGroups > 0, a handful per continent) are removed and made again, without asking.
        // 3 (3.1.1) = the files the game lists twice are read from the copy that is on the disk: the models with such a file (most textures of the Forever client) are made again.
        public const int Rules = 3;
        public const string Folder = "buildings";
        public const int PlacementBytes = 60;
        public static string Dir(string mapDir) => Path.Combine(mapDir, Folder);
        public static string ModelsDir(string dir) => Path.Combine(dir, "m");
        public static string ModelFile(string dir, int fdid) => Path.Combine(ModelsDir(dir), fdid.ToString(CultureInfo.InvariantCulture) + ".wmb");
        public static string ManifestPath(string dir) => Path.Combine(dir, "manifest.json");
        public static string PlacementsPath(string dir) => Path.Combine(dir, "placements.bin");
        public static string TexColorsPath(string dir) => Path.Combine(dir, "texcolors.bin");

        public static string Normalize(string mode) => mode == "bridges" || mode == "all" ? mode : "off";
        public static bool Wants(string mode, bool bridgePlacement) => mode == "all" || (mode == "bridges" && bridgePlacement);

        // measured per-model costs (defaults from the real files of build 1.60.1.70338; replaced by what a build on this PC measures)
        public static double SecondsBase = 0.005, SecondsPerGroup = 0.012, BytesBase = 500, BytesPerGroup = 26000;

        static void WriteAtomic(string path, byte[] data)
        {
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, data);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        // ---------------------------------------------------------------- placements.bin
        public static byte[] PlacementsToBytes(IList<WmoPlacement> ps, IList<bool> bridge)
        {
            var ms = new MemoryStream(); var w = new BinaryWriter(ms);
            w.Write(new[] { (byte)'W', (byte)'M', (byte)'P', (byte)'1' }); w.Write(ps.Count);
            for (int i = 0; i < ps.Count; i++)
            {
                var p = ps[i];
                w.Write(p.NameId); w.Write((uint)(bridge[i] ? 1 : 0));
                w.Write(p.X); w.Write(p.Y); w.Write(p.Z); w.Write(p.RotX); w.Write(p.RotY); w.Write(p.RotZ); w.Write(p.Scale);
                w.Write(p.MinX); w.Write(p.MinY); w.Write(p.MinZ); w.Write(p.MaxX); w.Write(p.MaxY); w.Write(p.MaxZ);
            }
            w.Flush(); return ms.ToArray();
        }

        public static List<WmoPlacement> PlacementsFromBytes(byte[] b, out List<bool> bridge)
        {
            bridge = new List<bool>();
            if (b == null || b.Length < 8 || b[0] != 'W' || b[1] != 'M' || b[2] != 'P' || b[3] != '1') return null;
            int n = BitConverter.ToInt32(b, 4);
            if (n < 0 || 8L + (long)n * PlacementBytes != b.Length) return null;
            var res = new List<WmoPlacement>(n);
            for (int i = 0; i < n; i++)
            {
                int o = 8 + i * PlacementBytes;
                res.Add(new WmoPlacement
                {
                    NameId = BitConverter.ToUInt32(b, o), X = BitConverter.ToSingle(b, o + 8), Y = BitConverter.ToSingle(b, o + 12), Z = BitConverter.ToSingle(b, o + 16),
                    RotX = BitConverter.ToSingle(b, o + 20), RotY = BitConverter.ToSingle(b, o + 24), RotZ = BitConverter.ToSingle(b, o + 28), Scale = BitConverter.ToSingle(b, o + 32),
                    MinX = BitConverter.ToSingle(b, o + 36), MinY = BitConverter.ToSingle(b, o + 40), MinZ = BitConverter.ToSingle(b, o + 44),
                    MaxX = BitConverter.ToSingle(b, o + 48), MaxY = BitConverter.ToSingle(b, o + 52), MaxZ = BitConverter.ToSingle(b, o + 56),
                });
                bridge.Add((BitConverter.ToUInt32(b, o + 4) & 1) != 0);
            }
            return res;
        }

        // "textures: 12 listed but not on disk; buildings: 1 not in the game's file list" (Manifest.Unread in words)
        public static string UnreadText(Dictionary<string, int> unread)
        {
            if (unread == null || unread.Count == 0) return "";
            var parts = new List<string>();
            foreach (var kind in new[] { "building", "texture" })
            {
                var l = unread.Where(kv => kv.Key.StartsWith(kind + ":", StringComparison.Ordinal) && kv.Value > 0).OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => kv.Value.ToString(CultureInfo.InvariantCulture) + " " + WorldMapPending.ReasonText(kv.Key.Substring(kind.Length + 1), "")).ToList();
                if (l.Count > 0) parts.Add((kind == "building" ? "building files" : "textures") + ": " + string.Join(", ", l));
            }
            return string.Join("; ", parts);
        }

        // ---------------------------------------------------------------- manifest
        public sealed class Manifest
        {
            public int Version, Rules, MapId, Placements, Models, Bridges, BridgeModels, ModelsBuilt, Tiles;
            public bool BridgesDone, AllDone;       // every building of that mode is on disk
            public string BuildKey = "", Built = "";
            public long Bytes, Ms;
            public double SecPerGroup, BytesPerGroup, SecBase;
            public Dictionary<string, int> Unread = new Dictionary<string, int>();     // 3.1.1: the files the last build could not read, "<kind>:<reason>" -> count (for "Copy details")
            public bool Valid => Version == WorldMapBuildings.Version && Placements >= 0 && Models >= 0;
        }

        public static Manifest ReadManifest(string dir)
        {
            try
            {
                var p = ManifestPath(dir);
                if (!File.Exists(p)) return null;
                var d = Json.Obj(File.ReadAllText(p));
                if (d == null) return null;
                Func<string, double> num = k => d.TryGetValue(k, out var o) && o is double x ? x : 0;
                return new Manifest
                {
                    Version = d.Int("v"), Rules = d.ContainsKey("rules") ? d.Int("rules") : 1, MapId = d.Int("mapId"), BuildKey = d.Str("build") ?? "", Placements = d.Int("placements"), Models = d.Int("models"), Bridges = d.Int("bridges"), BridgeModels = d.Int("bridgeModels"),
                    ModelsBuilt = d.Int("modelsBuilt"), Tiles = d.Int("tiles"), Bytes = d.Long("bytes"), Ms = d.Long("ms"), Built = d.Str("built") ?? "", SecPerGroup = num("secPerGroup"), BytesPerGroup = num("bytesPerGroup"), SecBase = num("secBase"), BridgesDone = d.Bool("bridgesDone"), AllDone = d.Bool("allDone"),
                    Unread = d.TryGetValue("unread", out var u) && u is Dictionary<string, object> ud ? ud.Where(kv => kv.Value is double).ToDictionary(kv => kv.Key, kv => (int)(double)kv.Value) : new Dictionary<string, int>(),
                };
            }
            catch (Exception) { return null; }
        }

        public static void WriteManifest(string dir, Manifest m)
        {
            var d = new Dictionary<string, object>
            {
                ["v"] = Version, ["rules"] = m.Rules, ["mapId"] = m.MapId, ["build"] = m.BuildKey, ["placements"] = m.Placements, ["models"] = m.Models, ["bridges"] = m.Bridges, ["bridgeModels"] = m.BridgeModels, ["modelsBuilt"] = m.ModelsBuilt,
                ["tiles"] = m.Tiles, ["bytes"] = m.Bytes, ["ms"] = m.Ms, ["secPerGroup"] = Math.Round(m.SecPerGroup, 5), ["bytesPerGroup"] = Math.Round(m.BytesPerGroup, 1), ["secBase"] = Math.Round(m.SecBase, 5),
                ["bridgesDone"] = m.BridgesDone, ["allDone"] = m.AllDone, ["built"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ["unread"] = (m.Unread ?? new Dictionary<string, int>()).ToDictionary(kv => kv.Key, kv => (object)kv.Value),
            };
            WriteAtomic(ManifestPath(dir), Encoding.UTF8.GetBytes(Json.Write(d)));
        }

        // ---------------------------------------------------------------- scan: placements + the roots of the distinct buildings
        // tiles: the continent's tiles with an obj0 file. Throws OperationCanceledException.
        public static BuildingScan Scan(IList<WdtTile> tiles, IBuildingSource src, IGroundSampler ground, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var scan = new BuildingScan();
            var seen = new HashSet<long>();
            src.Prefetch(tiles.Where(t => t.Obj0Fdid > 0).Select(t => t.Obj0Fdid));
            foreach (var t in tiles.Where(t => t.Obj0Fdid > 0).OrderBy(t => t.Row).ThenBy(t => t.Col))
            {
                ct.ThrowIfCancellationRequested();
                var d = src.Read(t.Obj0Fdid); if (d == null) continue;
                scan.Tiles++;
                foreach (var p in AdtObjects.ParsePlacements(d, out var prob))
                {
                    if (p.HasNameIndex) { scan.NameIndexSkipped++; continue; }
                    long key = ((long)p.UniqueId << 32) ^ p.NameId;
                    if (!seen.Add(key)) { scan.Duplicates++; continue; }
                    scan.Placements.Add(p);
                }
            }
            // the roots of the distinct buildings
            var ids = scan.Placements.Select(p => (int)p.NameId).Distinct().ToList();
            src.Prefetch(ids);
            var roots = new Dictionary<int, WmoRoot>();
            foreach (var id in ids)
            {
                ct.ThrowIfCancellationRequested();
                var bytes = src.Read(id);
                var r = bytes == null ? null : WmoRoot.Parse(bytes);
                var info = new BuildingModelInfo { Fdid = id, RootMissing = r == null || !r.Ok };
                if (r != null && r.Ok)
                {
                    roots[id] = r; info.Groups = r.Groups;
                    var og = r.OutsideGroups(); info.OutsideGroups = og.Count; foreach (var g in og) info.GroupFdids.Add(g.Fdid);
                    info.ExteriorLitGroups = r.ExteriorLitOnlyGroups();
                    info.TextureFdids = r.TextureFdid.Where(f => f != 0 && f <= int.MaxValue).Distinct().ToList();
                }
                scan.Models[id] = info;
            }
            // which models read a file from a later copy of a tie in the root (a group, a texture): they looked different before 3.1.1 (Rules 3)
            src.Prefetch(scan.Models.Values.SelectMany(m => m.GroupFdids.Concat(m.TextureFdids.Select(t => (int)t))));
            foreach (var m in scan.Models.Values) m.LaterCopy = !m.RootMissing && (src.LaterCopy(m.Fdid) || m.GroupFdids.Any(src.LaterCopy) || m.TextureFdids.Any(t => src.LaterCopy((int)t)));
            foreach (var p in scan.Placements)
            {
                bool br = roots.TryGetValue((int)p.NameId, out var r) && ground != null && BridgeRules.IsBridge(r, p, ground);
                scan.Bridge.Add(br);
                var info = scan.Models[(int)p.NameId]; info.Placements++; if (br) { info.BridgePlacements++; info.Bridge = true; }
            }
            scan.Ms = sw.ElapsedMilliseconds;
            return scan;
        }

        public static IEnumerable<BuildingModelInfo> ModelsFor(BuildingScan scan, string mode)
        {
            foreach (var m in scan.Models.Values)
            {
                if (m.RootMissing) continue;
                if (mode == "all" || (mode == "bridges" && m.Bridge)) yield return m;
            }
        }

        public static double ModelSeconds(BuildingModelInfo m) => SecondsBase + SecondsPerGroup * Math.Max(1, m.OutsideGroups);
        public static long ModelBytes(BuildingModelInfo m) => (long)(BytesBase + BytesPerGroup * Math.Max(1, m.OutsideGroups));

        public static BuildingEstimate Estimate(BuildingScan scan, string mode, string dir)
        {
            var e = new BuildingEstimate { Mode = mode };
            double sec = 0; long bytes = 0;
            foreach (var m in ModelsFor(scan, mode))
            {
                e.Models++;
                bool done = dir != null && File.Exists(ModelFile(dir, m.Fdid));
                if (done) { e.ModelsDone++; try { bytes += new FileInfo(ModelFile(dir, m.Fdid)).Length; } catch (Exception) { } }
                else { sec += ModelSeconds(m); bytes += ModelBytes(m); }
            }
            for (int i = 0; i < scan.Placements.Count; i++) if (Wants(mode, scan.Bridge[i]) && scan.Models.TryGetValue((int)scan.Placements[i].NameId, out var mi) && !mi.RootMissing) e.Buildings++;
            e.Minutes = sec / 60; e.Bytes = bytes;
            return e;
        }

        // ---------------------------------------------------------------- textures: the average colour of a texture
        public static int AverageColor(byte[] blp)
        {
            try
            {
                if (blp == null || blp.Length < 148 || blp[0] != 'B' || blp[1] != 'L' || blp[2] != 'P' || blp[3] != '2') return -1;
                // decode a small mip level: the header is edited on a copy so that mip 0 is that level
                var d = (byte[])blp.Clone();
                int w = BitConverter.ToInt32(d, 12), h = BitConverter.ToInt32(d, 16), k = 0;
                while (k < 15 && (w >> (k + 1)) >= 8 && (h >> (k + 1)) >= 8 && BitConverter.ToUInt32(d, 20 + (k + 1) * 4) != 0 && BitConverter.ToUInt32(d, 84 + (k + 1) * 4) != 0) k++;
                if (k > 0)
                {
                    Buffer.BlockCopy(d, 20 + k * 4, d, 20, 4); Buffer.BlockCopy(d, 84 + k * 4, d, 84, 4);
                    Buffer.BlockCopy(BitConverter.GetBytes(Math.Max(1, w >> k)), 0, d, 12, 4); Buffer.BlockCopy(BitConverter.GetBytes(Math.Max(1, h >> k)), 0, d, 16, 4);
                }
                var px = Blp.Decode(d, out int dw, out int dh);
                double r = 0, g = 0, b = 0; int n = 0;
                for (int i = 0; i + 3 < px.Length; i += 4)
                {
                    if (px[i + 3] < 40) continue;
                    b += px[i]; g += px[i + 1]; r += px[i + 2]; n++;
                }
                if (n == 0) return -1;
                return ((int)(r / n) << 16) | ((int)(g / n) << 8) | (int)(b / n);
            }
            catch (Exception) { return -1; }
        }

        public static Dictionary<uint, int> ReadTexColors(string dir)
        {
            var res = new Dictionary<uint, int>();
            try
            {
                var p = TexColorsPath(dir); if (!File.Exists(p)) return res;
                var b = File.ReadAllBytes(p);
                if (b.Length < 8 || b[0] != 'T' || b[1] != 'X' || b[2] != 'C' || b[3] != '1') return res;
                int n = BitConverter.ToInt32(b, 4);
                if (n < 0 || 8L + (long)n * 8 != b.Length) return res;
                // 3.1.1: a texture that could not be read (-1) is not taken from the file: it is asked for again in the next build (it may be on disk by now,
                // or found by a later copy in the game's file list) - no "missing" is kept across runs
                for (int i = 0; i < n; i++) { int c = BitConverter.ToInt32(b, 12 + i * 8); if (c != -1) res[BitConverter.ToUInt32(b, 8 + i * 8)] = c; }
            }
            catch (Exception) { }
            return res;
        }

        public static void WriteTexColors(string dir, Dictionary<uint, int> map)
        {
            var ms = new MemoryStream(); var w = new BinaryWriter(ms);
            w.Write(new[] { (byte)'T', (byte)'X', (byte)'C', (byte)'1' }); w.Write(map.Count);
            foreach (var kv in map) { w.Write(kv.Key); w.Write(kv.Value); }
            w.Flush(); WriteAtomic(TexColorsPath(dir), ms.ToArray());
        }

        // ---------------------------------------------------------------- one model
        // reads the groups of a building and makes its file; returns the bytes written (0 = nothing to draw: a stub was written)
        public static long TicksRead, TicksParse, TicksTex, TicksMesh;     // diagnostics: where the time of the builds went (Stopwatch ticks)
        public static long BuildModel(BuildingModelInfo info, IBuildingSource src, string dir, Dictionary<uint, int> texColors, WmoBuildOptions o = null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var rootBytes = src.Read(info.Fdid);
            var root = rootBytes == null ? null : WmoRoot.Parse(rootBytes);
            WmoModel model;
            if (root == null || !root.Ok) model = new WmoModel();
            else
            {
                var meshes = new List<WmoGroupMesh>();
                foreach (var g in root.OutsideGroups())
                {
                    sw.Restart();
                    var gb = src.Read(g.Fdid);
                    TicksRead += sw.ElapsedTicks; sw.Restart();
                    if (gb == null) continue;
                    var gm = WmoGroupMesh.Parse(gb);
                    if (gm.Ok) meshes.Add(gm);
                    TicksParse += sw.ElapsedTicks;
                }
                sw.Restart();
                // the colour of every material that is used: the average colour of its texture
                var used = new HashSet<int>(); foreach (var m in meshes) foreach (var mt in m.Mat) used.Add(mt);
                var rgb = new Dictionary<int, int>();
                foreach (var mid in used)
                {
                    int c = -1;
                    if (mid < root.TextureFdid.Length)
                    {
                        uint tf = root.TextureFdid[mid];
                        if (tf != 0 && !texColors.TryGetValue(tf, out c)) { var tb = src.ReadTexture((int)tf); c = tb == null ? -1 : AverageColor(tb); texColors[tf] = c; }
                        else if (tf == 0) c = -1;
                    }
                    rgb[mid] = c;
                }
                TicksTex += sw.ElapsedTicks; sw.Restart();
                o = o ?? new WmoBuildOptions(); o.Bridge = info.Bridge;
                // a whole city keeps more triangles than a farmhouse: the cap grows with the number of groups and with the triangles they have (up to 150,000)
                long rawTris = 0; foreach (var m in meshes) rawTris += m.Idx.Length / 3;
                o.Lod0MaxTris = Math.Max(o.Lod0MaxTris, Lod0Cap(info.OutsideGroups, rawTris)); o.Lod1MaxTris = Math.Max(o.Lod1MaxTris, o.Lod0MaxTris / 6);
                model = WmoModel.Build(meshes, mid => rgb.TryGetValue(mid, out var c) ? c : -1, o);
                TicksMesh += sw.ElapsedTicks; sw.Restart();
            }
            model.Bridge = info.Bridge;
            var bytes = model.ToBytes();
            Directory.CreateDirectory(ModelsDir(dir));
            WriteAtomic(ModelFile(dir, info.Fdid), bytes);
            return model.Stub ? 0 : bytes.Length;
        }

        // the nearest level's triangle budget of a model: 8,000 + 900 per drawn group, or half of the triangles it has when that is more (Orgrimmar: 21 groups,
        // 234,000 triangles -> 117,000; before 2.89 only the group count, 26,900), never more than 150,000
        public const int MaxLod0Tris = 150000;
        public static int Lod0Cap(int groups, long rawTris) => (int)Math.Min(MaxLod0Tris, Math.Max(8000L + 900L * groups, rawTris / 2));

        // a finished folder made by an older group rule: the models the new rule makes differently are removed (made again by the next build); returns how many
        public static int RemoveStale(BuildingScan scan, string dir, int oldRules)
        {
            int n = 0;
            if (scan == null || oldRules >= Rules) return 0;
            foreach (var m in scan.Models.Values)
            {
                if (m.RootMissing || !((oldRules < 2 && m.ExteriorLitGroups > 0) || (oldRules < 3 && m.LaterCopy))) continue;
                var f = ModelFile(dir, m.Fdid);
                try { if (File.Exists(f)) { File.Delete(f); n++; } } catch (Exception) { }
            }
            return n;
        }

        static int Bgr(uint c) => (int)(((c & 0xFF) << 16) | (c & 0xFF00) | ((c >> 16) & 0xFF));

        // ---------------------------------------------------------------- the parts of the folder
        public static long SizeBytes(string dir)
        {
            long total = 0;
            try { if (Directory.Exists(dir)) foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) { try { total += new FileInfo(f).Length; } catch (Exception) { } } } catch (Exception) { }
            return total;
        }

        public static HashSet<int> BuiltModels(string dir)
        {
            var res = new HashSet<int>();
            try
            {
                var md = ModelsDir(dir);
                if (!Directory.Exists(md)) return res;
                foreach (var f in Directory.EnumerateFiles(md, "*.wmb")) if (int.TryParse(Path.GetFileNameWithoutExtension(f), NumberStyles.None, CultureInfo.InvariantCulture, out var id)) res.Add(id);
            }
            catch (Exception) { }
            return res;
        }

        public static string SizeText(long b) => WorldMapCache.SizeText(b);
    }
}
