using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace ElansAddonHub.Services.WorldMap
{
    // ============================================================================================================================
    // "Trees & objects" of the 3D world map (3.2, experimental): the M2 placements of a continent (the MDDF chunks of its obj0 ADTs: trees, rocks, fences,
    // carts, banners) with their real shapes and textures, read from the player's own game files into  <map folder>\objects\ :
    //   manifest.json       version, game build, counts, measured costs, what could not be read (written last: a damaged folder is never taken for a finished one)
    //   p\{col}_{row}.bin   the placements of one tile: "M2P2" | int count | count x 36 bytes { uint fdid, float pos[3], float rot[3] (degrees), float scale, uint flags }
    //                       flags 1 = a building's furnishing (WmoDetail.Doodads: the lamps, benches and trees of a city; drawn only with the buildings on)
    //                       (scene axes, as the buildings; a placement listed by two tiles is kept once, by the tile it stands in)
    //   tiles.bin           "M2T1" | int count | count x { ushort key (col * 64 + row), ushort placements }: which tiles have a placements file
    //   m\{fdid}.m2b        one model per distinct M2 (M2Mesh); the stub "M2BX" for one with nothing to draw, so it is not tried again
    //   t\{fdid}.mtx        one texture per distinct texture (ObjTexture): the game's own DXT blocks, every mip level from 512 px down; written before
    //                       the first model that uses it, so a model file on disk always has its textures
    // The page draws each model as an InstancedMesh per tile, only within the distance of the level the person chose (Low / Medium / High); the
    // build itself does not depend on the level. Each file is written as .tmp and renamed: a cancelled or crashed build resumes with what is done.
    // ============================================================================================================================
    public sealed class ObjModelInfo
    {
        public int Fdid, Placements;
        public bool LaterCopy;
    }

    public sealed class ObjectScan
    {
        public Dictionary<int, List<M2Placement>> ByTile = new Dictionary<int, List<M2Placement>>();     // key col * 64 + row
        public Dictionary<int, ObjModelInfo> Models = new Dictionary<int, ObjModelInfo>();
        public int Tiles, Placements, NameIndexSkipped, Duplicates, Furnishings;
        public long Ms;
    }

    public sealed class ObjectsState
    {
        public int MapId;
        public string Name = "";
        public string Phase = "idle";       // idle | scanning | ask | building | paused | done | error
        public int Done, Total;             // models
        public int Placements;
        public double SecondsLeft;
        public string Error;
        public double Minutes;              // the estimate for the card: what is left
        public long Bytes, BytesOnDisk;
        public string Text
        {
            get
            {
                if (Error != null) return Error;
                string who = Name.Length > 0 ? Name : "the continent";
                switch (Phase)
                {
                    case "scanning": return "Counting the trees and objects of " + who + "...";
                    case "building": return "Reading the trees and objects of " + who + ": " + Done.ToString("N0", CultureInfo.InvariantCulture) + " / " + Total.ToString("N0", CultureInfo.InvariantCulture) + " models" + (SecondsLeft > 0 ? " - " + BuildingsState.LeftText(SecondsLeft) : "");
                    case "paused": return "Paused: " + Done.ToString("N0", CultureInfo.InvariantCulture) + " / " + Total.ToString("N0", CultureInfo.InvariantCulture) + " models";
                    default: return "";
                }
            }
        }
    }

    public static class WorldMapObjects
    {
        public const int Version = 2;          // 2 (3.2): the buildings' furnishings, placements with flags (M2P2)
        public const string Folder = "objects";
        public const int PlacementBytes = 36;
        public const uint FlagFurnishing = 1;
        public const double Ty = 1600.0 / 3;
        public static string Dir(string mapDir) => Path.Combine(mapDir, Folder);
        public static string ModelsDir(string dir) => Path.Combine(dir, "m");
        public static string TexturesDir(string dir) => Path.Combine(dir, "t");
        public static string PlacementsDir(string dir) => Path.Combine(dir, "p");
        public static string ModelFile(string dir, int fdid) => Path.Combine(ModelsDir(dir), fdid.ToString(CultureInfo.InvariantCulture) + ".m2b");
        public static string TextureFile(string dir, int fdid) => Path.Combine(TexturesDir(dir), fdid.ToString(CultureInfo.InvariantCulture) + ".mtx");
        public static string TileFile(string dir, int col, int row) => Path.Combine(PlacementsDir(dir), col.ToString(CultureInfo.InvariantCulture) + "_" + row.ToString(CultureInfo.InvariantCulture) + ".bin");
        public static string TilesPath(string dir) => Path.Combine(dir, "tiles.bin");
        public static string ManifestPath(string dir) => Path.Combine(dir, "manifest.json");

        // the levels of the page's switch (experimental; Off is the default and the fallback)
        public static string Normalize(string level) => level == "low" || level == "medium" || level == "high" ? level : "off";

        // measured costs per model (defaults from the lab's runs on the Forever client; replaced by what a build on this PC measures)
        public static double SecondsPerModel = 0.012, BytesPerModel = 60000;

        static void WriteAtomic(string path, byte[] data)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, data);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        // ---------------------------------------------------------------- placements
        public static byte[] TileToBytes(IList<M2Placement> ps)
        {
            var ms = new MemoryStream(); var w = new BinaryWriter(ms);
            w.Write(new[] { (byte)'M', (byte)'2', (byte)'P', (byte)'2' }); w.Write(ps.Count);
            foreach (var p in ps) { w.Write(p.NameId); w.Write(p.X); w.Write(p.Y); w.Write(p.Z); w.Write(p.RotX); w.Write(p.RotY); w.Write(p.RotZ); w.Write(p.Scale); w.Write(p.HasNameIndex ? 0u : (uint)(p.Flags == 0x40 && p.UniqueId >= FurnishingUidBase ? FlagFurnishing : 0)); }
            w.Flush(); return ms.ToArray();
        }

        public static List<M2Placement> TileFromBytes(byte[] b)
        {
            if (b == null || b.Length < 8 || b[0] != 'M' || b[1] != '2' || b[2] != 'P' || b[3] != '2') return null;
            int n = BitConverter.ToInt32(b, 4);
            if (n < 0 || 8L + (long)n * PlacementBytes != b.Length) return null;
            var res = new List<M2Placement>(n);
            for (int i = 0; i < n; i++)
            {
                int o = 8 + i * PlacementBytes;
                res.Add(new M2Placement
                {
                    NameId = BitConverter.ToUInt32(b, o), X = BitConverter.ToSingle(b, o + 4), Y = BitConverter.ToSingle(b, o + 8), Z = BitConverter.ToSingle(b, o + 12),
                    RotX = BitConverter.ToSingle(b, o + 16), RotY = BitConverter.ToSingle(b, o + 20), RotZ = BitConverter.ToSingle(b, o + 24), Scale = BitConverter.ToSingle(b, o + 28),
                    UniqueId = (BitConverter.ToUInt32(b, o + 32) & FlagFurnishing) != 0 ? FurnishingUidBase : 0, Flags = 0x40,
                });
            }
            return res;
        }

        public static byte[] TilesToBytes(ObjectScan scan)
        {
            var keys = scan.ByTile.Where(kv => kv.Value.Count > 0).OrderBy(kv => kv.Key).ToList();
            var ms = new MemoryStream(); var w = new BinaryWriter(ms);
            w.Write(new[] { (byte)'M', (byte)'2', (byte)'T', (byte)'1' }); w.Write(keys.Count);
            foreach (var kv in keys) { w.Write((ushort)kv.Key); w.Write((ushort)Math.Min(65535, kv.Value.Count)); }
            w.Flush(); return ms.ToArray();
        }

        public static Dictionary<int, int> TilesFromBytes(byte[] b)
        {
            if (b == null || b.Length < 8 || b[0] != 'M' || b[1] != '2' || b[2] != 'T' || b[3] != '1') return null;
            int n = BitConverter.ToInt32(b, 4);
            if (n < 0 || 8L + n * 4L != b.Length) return null;
            var res = new Dictionary<int, int>();
            for (int i = 0; i < n; i++) res[BitConverter.ToUInt16(b, 8 + i * 4)] = BitConverter.ToUInt16(b, 10 + i * 4);
            return res;
        }

        public static void WritePlacements(string dir, ObjectScan scan)
        {
            foreach (var kv in scan.ByTile) if (kv.Value.Count > 0) WriteAtomic(TileFile(dir, kv.Key / 64, kv.Key % 64), TileToBytes(kv.Value));
            WriteAtomic(TilesPath(dir), TilesToBytes(scan));
        }

        public static bool PlacementsOnDisk(string dir, ObjectScan scan)
        {
            if (!scan.ByTile.Where(kv => kv.Value.Count > 0).Take(1).All(kv => File.Exists(TileFile(dir, kv.Key / 64, kv.Key % 64)) && TileFromBytes(File.ReadAllBytes(TileFile(dir, kv.Key / 64, kv.Key % 64))) != null)) return false;      // an older format
            var t = File.Exists(TilesPath(dir)) ? TilesFromBytes(File.ReadAllBytes(TilesPath(dir))) : null;
            if (t == null || t.Count != scan.ByTile.Count(kv => kv.Value.Count > 0)) return false;
            foreach (var kv in scan.ByTile) if (kv.Value.Count > 0 && (!t.TryGetValue(kv.Key, out var n) || n != Math.Min(65535, kv.Value.Count) || !File.Exists(TileFile(dir, kv.Key / 64, kv.Key % 64)))) return false;
            return true;
        }

        // ---------------------------------------------------------------- manifest
        public sealed class Manifest
        {
            public int Version, MapId, Placements, Models, ModelsBuilt, Textures, Tiles;
            public bool AllDone;
            public string BuildKey = "", Built = "";
            public long Bytes, Ms;
            public double SecPerModel, BytesPerModel;
            public Dictionary<string, int> Unread = new Dictionary<string, int>();
            public bool Valid => Version == WorldMapObjects.Version && Placements >= 0 && Models >= 0;
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
                    Version = d.Int("v"), MapId = d.Int("mapId"), BuildKey = d.Str("build") ?? "", Placements = d.Int("placements"), Models = d.Int("models"), ModelsBuilt = d.Int("modelsBuilt"), Textures = d.Int("textures"),
                    Tiles = d.Int("tiles"), Bytes = d.Long("bytes"), Ms = d.Long("ms"), Built = d.Str("built") ?? "", SecPerModel = num("secPerModel"), BytesPerModel = num("bytesPerModel"), AllDone = d.Bool("allDone"),
                    Unread = d.TryGetValue("unread", out var u) && u is Dictionary<string, object> ud ? ud.Where(kv => kv.Value is double).ToDictionary(kv => kv.Key, kv => (int)(double)kv.Value) : new Dictionary<string, int>(),
                };
            }
            catch (Exception) { return null; }
        }

        public static void WriteManifest(string dir, Manifest m)
        {
            var d = new Dictionary<string, object>
            {
                ["v"] = Version, ["mapId"] = m.MapId, ["build"] = m.BuildKey, ["placements"] = m.Placements, ["models"] = m.Models, ["modelsBuilt"] = m.ModelsBuilt, ["textures"] = m.Textures, ["tiles"] = m.Tiles,
                ["bytes"] = m.Bytes, ["ms"] = m.Ms, ["secPerModel"] = Math.Round(m.SecPerModel, 5), ["bytesPerModel"] = Math.Round(m.BytesPerModel, 1), ["allDone"] = m.AllDone,
                ["built"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), ["unread"] = (m.Unread ?? new Dictionary<string, int>()).ToDictionary(kv => kv.Key, kv => (object)kv.Value),
            };
            WriteAtomic(ManifestPath(dir), Encoding.UTF8.GetBytes(Json.Write(d)));
        }

        // ---------------------------------------------------------------- scan: the placements of every tile and the distinct models
        // furnishings get unique ids from here up (an ADT's own ids are far below): a building's placement id x 4096 + the doodad's index, kept apart from them
        public const uint FurnishingUidBase = 0x80000000;

        public static ObjectScan Scan(IList<WdtTile> tiles, IBuildingSource src, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var scan = new ObjectScan();
            var seen = new HashSet<uint>();
            var wmos = new List<WmoPlacement>(); var wmoSeen = new HashSet<uint>();
            src.Prefetch(tiles.Where(t => t.Obj0Fdid > 0).Select(t => t.Obj0Fdid));
            foreach (var t in tiles.Where(t => t.Obj0Fdid > 0).OrderBy(t => t.Row).ThenBy(t => t.Col))
            {
                ct.ThrowIfCancellationRequested();
                var d = src.Read(t.Obj0Fdid); if (d == null) continue;
                scan.Tiles++;
                foreach (var p in AdtObjects.ParseDoodads(d, out _))
                {
                    if (p.HasNameIndex) { scan.NameIndexSkipped++; continue; }
                    if (!seen.Add(p.UniqueId)) { scan.Duplicates++; continue; }
                    Add(scan, p);
                }
                foreach (var w in AdtObjects.ParsePlacements(d, out _)) if (!w.HasNameIndex && wmoSeen.Add(w.UniqueId)) wmos.Add(w);
            }
            // 3.2: the buildings' own furnishings (set 0 and the placement's set): each building's root once
            var roots = new Dictionary<int, WmoRoot>();
            src.Prefetch(wmos.Select(w => (int)w.NameId).Distinct());
            foreach (var w in wmos)
            {
                ct.ThrowIfCancellationRequested();
                int id = (int)w.NameId;
                if (!roots.TryGetValue(id, out var r)) { var rb = src.Read(id); roots[id] = r = rb == null ? null : WmoRoot.Parse(rb); }
                if (r == null || !r.Ok) continue;
                uint uid = FurnishingUidBase + (w.UniqueId & 0x7FFFF) * 4096;
                foreach (var dp in WmoDetail.Doodads(r, w, uid)) { Add(scan, dp); scan.Furnishings++; }
            }
            scan.Ms = sw.ElapsedMilliseconds;
            return scan;
        }

        static void Add(ObjectScan scan, M2Placement p)
        {
            int col = (int)Math.Floor(p.X / Ty), row = (int)Math.Floor(p.Z / Ty);
            if (col < 0 || row < 0 || col > 63 || row > 63) return;
            int key = col * 64 + row;
            if (!scan.ByTile.TryGetValue(key, out var l)) scan.ByTile[key] = l = new List<M2Placement>();
            l.Add(p); scan.Placements++;
            int id = (int)p.NameId;
            if (!scan.Models.TryGetValue(id, out var mi)) scan.Models[id] = mi = new ObjModelInfo { Fdid = id };
            mi.Placements++;
        }

        public static bool ModelDone(string dir, int fdid) => File.Exists(ModelFile(dir, fdid));

        public static HashSet<int> BuiltModels(string dir)
        {
            var res = new HashSet<int>();
            try
            {
                var md = ModelsDir(dir);
                if (!Directory.Exists(md)) return res;
                foreach (var f in Directory.EnumerateFiles(md, "*.m2b"))
                {
                    if (!int.TryParse(Path.GetFileNameWithoutExtension(f), NumberStyles.None, CultureInfo.InvariantCulture, out var id)) continue;
                    try { if (new FileInfo(f).Length > 8) res.Add(id); } catch (Exception) { }       // the stubs are not listed: nothing to fetch
                }
            }
            catch (Exception) { }
            return res;
        }

        public static void FillEstimate(ObjectsState st, ObjectScan scan, string dir)
        {
            int done = 0, left = 0;
            foreach (var m in scan.Models.Values) { if (ModelDone(dir, m.Fdid)) done++; else left++; }
            st.Total = scan.Models.Count; st.Done = done; st.Placements = scan.Placements;
            st.Minutes = left * SecondsPerModel / 60; st.Bytes = (long)(left * BytesPerModel);
            st.BytesOnDisk = SizeBytes(dir);
        }

        // the card's question: "Trees & objects of Kalimdor: 4,210 models for 312,000 placements, about 3 minutes and 240 MB."
        public static string CardText(ObjectsState st)
        {
            string who = st.Name.Length > 0 ? st.Name : "this continent";
            string left = st.Done > 0 ? (st.Total - st.Done).ToString("N0", CultureInfo.InvariantCulture) + " of " + st.Total.ToString("N0", CultureInfo.InvariantCulture) + " models left" : st.Total.ToString("N0", CultureInfo.InvariantCulture) + " models";
            int min = (int)Math.Ceiling(st.Minutes);
            return "Trees & objects of " + who + ": " + left + " for " + st.Placements.ToString("N0", CultureInfo.InvariantCulture) + " placements, about " + (min <= 1 ? "a minute" : min + " minutes") + " and " + SizeText(st.Bytes) + " on this PC. They are read from your game files once, in the background.";
        }

        // ---------------------------------------------------------------- one model and its textures
        // returns the bytes written (0 = nothing to draw: a stub was written). Throws IOException / UnauthorizedAccessException for a disk that fails (the build stops).
        public static long BuildModel(int fdid, IBuildingSource src, string dir, Dictionary<int, bool> texDone, byte[] rootBytes = null, bool rootRead = false)
        {
            long written = 0;
            var root = rootRead ? rootBytes : src.Read(fdid);        // rootRead: the build read it already (null = it could not, counted once)
            var r = M2Mesh.Root.Parse(root);
            M2Mesh mesh = null;
            if (r.Ok)
            {
                var skin = src.Read(r.Skins[0]);
                mesh = M2Mesh.Parse(root, skin);
            }
            if (mesh != null && mesh.Drawable)
            {
                // the textures first: a model on disk always has its textures; one that cannot be read becomes a stand-in colour (tex 0)
                foreach (var b in mesh.Batches)
                {
                    if (b.Tex <= 0) continue;
                    if (!texDone.TryGetValue(b.Tex, out var ok))
                    {
                        ok = File.Exists(TextureFile(dir, b.Tex));
                        if (!ok)
                        {
                            var packed = ObjTexture.Pack(src.ReadBlp(b.Tex, ObjTexture.MaxSize));
                            if (packed != null) { WriteAtomic(TextureFile(dir, b.Tex), packed); written += packed.Length; ok = true; }
                        }
                        texDone[b.Tex] = ok;
                    }
                    if (!ok) b.Tex = 0;
                }
                mesh.Textures.RemoveWhere(t => !texDone.TryGetValue(t, out var ok) || !ok);
            }
            var bytes = mesh != null && mesh.Drawable ? mesh.ToBytes() : (byte[])M2Mesh.Stub.Clone();
            WriteAtomic(ModelFile(dir, fdid), bytes);
            return bytes.Length > 8 ? written + bytes.Length : 0;
        }

        // ---------------------------------------------------------------- the parts of the folder
        public static long SizeBytes(string dir)
        {
            long total = 0;
            try { if (Directory.Exists(dir)) foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) { try { total += new FileInfo(f).Length; } catch (Exception) { } } } catch (Exception) { }
            return total;
        }

        public static int TextureCount(string dir)
        {
            try { var t = TexturesDir(dir); return Directory.Exists(t) ? Directory.EnumerateFiles(t, "*.mtx").Count() : 0; } catch (Exception) { return 0; }
        }

        public static string SizeText(long b) => WorldMapCache.SizeText(b);
    }
}
