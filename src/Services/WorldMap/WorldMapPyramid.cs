using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ElansAddonHub.Services.Maps;

namespace ElansAddonHub.Services.WorldMap
{
    // ============================================================================================================================
    // The tile pyramid of one continent, made from its minimap textures and kept on disk as small JPEG files:
    //     <map folder>\z{level}\{x}_{y}.jpg      level 7 = native (2 x 2 tiles of 256 px per 512 px minimap), 6 .. 0 = each the 2 x 2 tiles below it halved
    //     <map folder>\manifest.json            written LAST: its presence means the pyramid is complete
    // A tile with no land in it has no file (the viewer's background is the sea colour that is also used to fill the gaps inside a tile).
    // One minimap is decoded at a time and the levels above are made by reading four small tiles back, so memory stays at a few MB whatever the
    // size of the continent. A build that is cancelled (or crashes) leaves its finished tiles; the next one skips every tile whose file is already
    // there, so it resumes. Files are written to a .tmp name and renamed, so a half-written tile never exists.
    // ============================================================================================================================
    public sealed class PyramidResult
    {
        public int Adt;                  // map tiles asked for
        public int Decoded;              // minimaps read and cut this time
        public int Reused;               // map tiles whose four files were already there
        public int Missing;              // map tiles the source did not have
        public int Unreadable;           // of those: tiles the install lists but could not give (counted in Missing too)
        public int Written;              // files written this time (all levels)
        public int Skipped;              // files of the upper levels that were already there
        public int Files;                // files in the pyramid
        public long Bytes;
        public int MinCol, MaxCol, MinRow, MaxRow;
        public bool Complete;
        public int Recovered;            // tiles that had a stand-in and read now (2.70)
        public List<PendingTile> Pending = new List<PendingTile>();     // tiles drawn with a stand-in this time (missing / unreadable), not final
    }

    public sealed class PyramidProgress { public string Phase = ""; public int Done, Total; }

    public static class WorldMapPyramid
    {
        public const int Version = 1;
        public const int JpegQuality = 84;
        public const int SeaB = 0x10, SeaG = 0x11, SeaR = 0x08;            // the colour of the game's open-water minimap tiles (measured on the real files): #081110
        public const string SeaHex = "#081110";

        public static string ManifestPath(string mapDir) => Path.Combine(mapDir, "manifest.json");
        public static string TileFile(string mapDir, int level, int tx, int ty) => Path.Combine(mapDir, "z" + level, tx + "_" + ty + ".jpg");

        public sealed class Manifest
        {
            public int Version, MapId, NativeLevel, Adt, Missing, Files;
            public string BuildKey = "", Name = "", Built = "", Sea = SeaHex;
            public int MinCol, MaxCol, MinRow, MaxRow;
            public long Bytes;
            public List<PendingTile> Pending = new List<PendingTile>();     // 2.70: tiles shown with a stand-in, read again later (WorldMapPending)
            public string Stamp = "";                                       // the game storage stamp when it was built (WorldMapPending.StorageStamp)
            public bool Valid => Version == WorldMapPyramid.Version && Adt > 0 && Files > 0;
        }

        public static Manifest ReadManifest(string mapDir)
        {
            try
            {
                var p = ManifestPath(mapDir);
                if (!File.Exists(p)) return null;
                var d = Json.Obj(File.ReadAllText(p));
                if (d == null) return null;
                return new Manifest
                {
                    Version = d.Int("v"), MapId = d.Int("mapId"), BuildKey = d.Str("build") ?? "", Name = d.Str("name") ?? "", NativeLevel = d.Int("native"),
                    Adt = d.Int("adt"), Missing = d.Int("missing"), Files = d.Int("files"), Bytes = d.Long("bytes"), Built = d.Str("built") ?? "", Sea = d.Str("sea") ?? SeaHex,
                    MinCol = d.Int("minCol"), MaxCol = d.Int("maxCol"), MinRow = d.Int("minRow"), MaxRow = d.Int("maxRow"),
                    Pending = WorldMapPending.ListFrom(d, "pending"), Stamp = d.Str("stamp") ?? "",
                };
            }
            catch (Exception) { return null; }
        }

        public static string ManifestJson(Manifest m) => Json.Write(new Dictionary<string, object>
        {
            ["v"] = m.Version, ["mapId"] = m.MapId, ["build"] = m.BuildKey, ["name"] = m.Name, ["native"] = m.NativeLevel, ["tilePx"] = WorldMapMath.TilePx,
            ["adt"] = m.Adt, ["missing"] = m.Missing, ["files"] = m.Files, ["bytes"] = m.Bytes, ["built"] = m.Built, ["sea"] = m.Sea,
            ["minCol"] = m.MinCol, ["maxCol"] = m.MaxCol, ["minRow"] = m.MinRow, ["maxRow"] = m.MaxRow,
            ["pending"] = WorldMapPending.ListJson(m.Pending), ["stamp"] = m.Stamp ?? "",
        });

        // "<x>_<y>.low" next to the native tiles of a map tile drawn with a stand-in (one marker per map tile, at its top-left native tile)
        public static string MarkerFile(string mapDir, int col, int row) => Path.Combine(mapDir, "z" + WorldMapMath.NativeLevel, (col * 2) + "_" + (row * 2) + WorldMapPending.MarkerExt);

        static bool Have(string path) { try { var f = new FileInfo(path); return f.Exists && f.Length > 0; } catch (Exception) { return false; } }

        // ---- build
        // `load` gives the BGRA minimap of a map tile (null = the source does not have it). Throws OperationCanceledException when `ct` is cancelled.
        // `standIn` (may be null) gives a low-detail picture of a map tile whose minimap cannot be read (the continent's Atlas art there); null = a plain hatch.
        // A finished pyramid's manifest stays while it is built again (a retry): the map keeps working and the manifest is replaced at the end.
        public static PyramidResult Build(int mapId, string name, string buildKey, IList<WdtTile> tiles, Func<WdtTile, MapRender.TileImage> load, string mapDir,
            Action<PyramidProgress> progress, CancellationToken ct, Func<WdtTile, MapRender.TileImage> standIn = null, string stamp = "")
        {
            var res = new PyramidResult { Adt = tiles.Count };
            Directory.CreateDirectory(mapDir);
            var old = ReadManifest(mapDir);
            if (old == null || !old.Valid) { try { if (File.Exists(ManifestPath(mapDir))) File.Delete(ManifestPath(mapDir)); } catch (Exception) { } }
            var present = new HashSet<long>();                // pyramid tiles that exist at the level being finished
            var fresh = new List<long>();                     // native tiles written this time: the tiles above them are made again
            int real = 0;
            int nativeLevel = WorldMapMath.NativeLevel;
            // work to do: every map tile, then the tiles of the levels above
            int upper = 0;
            {
                var s = new HashSet<long>();
                foreach (var t in tiles) { WorldMapMath.PyramidRangeOfAdt(nativeLevel, t.Col, t.Row, out var x0, out var y0, out var x1, out var y1); for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) s.Add(Key(x, y)); }
                for (int lv = nativeLevel - 1; lv >= 0; lv--)
                {
                    var n = new HashSet<long>(); foreach (var k in s) n.Add(Key(KX(k) / 2, KY(k) / 2));
                    upper += n.Count; s = n;
                }
            }
            int total = tiles.Count + upper, done = 0;
            void Report(string phase) { progress?.Invoke(new PyramidProgress { Phase = phase, Done = done, Total = total }); }
            Report("tiles");

            int minC = int.MaxValue, maxC = int.MinValue, minR = int.MaxValue, maxR = int.MinValue;
            foreach (var t in tiles.OrderBy(t => t.Row).ThenBy(t => t.Col))
            {
                ct.ThrowIfCancellationRequested();
                WorldMapMath.PyramidRangeOfAdt(nativeLevel, t.Col, t.Row, out var x0, out var y0, out var x1, out var y1);
                var marker = MarkerFile(mapDir, t.Col, t.Row);
                bool wasStandIn = File.Exists(marker);
                bool haveFiles = true;
                for (int y = y0; y <= y1 && haveFiles; y++) for (int x = x0; x <= x1 && haveFiles; x++) haveFiles = Have(TileFile(mapDir, nativeLevel, x, y));
                bool all = haveFiles && !wasStandIn;              // a stand-in is never final: its minimap is read again every time
                if (all)
                {
                    res.Reused++; real++;
                    for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) present.Add(Key(x, y));
                }
                else
                {
                    // a minimap the install cannot give (still downloading in the background, or damaged): a stand-in for now (continent art or a hatch),
                    // marked as not final, and logged
                    MapRender.TileImage img; string why = "missing", detail = "";
                    try { img = load(t); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception e) { detail = WorldMapPending.Short(e); Util.Log("world map: " + (name ?? "map " + mapId) + ": minimap " + t.Col + "," + t.Row + " could not be read: " + detail); img = null; res.Unreadable++; why = "unreadable"; }
                    bool good = !(img == null || img.Bgra == null || img.Width <= 0 || img.Height <= 0 || img.Bgra.Length < img.Width * img.Height * 4);
                    byte[] px; string shown = "";
                    if (!good && wasStandIn && haveFiles)
                    {
                        // still not readable: the stand-in that is there stays (nothing rewritten), the marker says why now
                        res.Missing++;
                        var keep = WorldMapPending.ReadMarker(marker) ?? new PendingTile { Col = t.Col, Row = t.Row, Part = "picture", Shown = "plain" };
                        keep.Reason = why; keep.Detail = detail;
                        WorldMapPending.WriteMarker(marker, keep);
                        res.Pending.Add(keep);
                        for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) present.Add(Key(x, y));
                        if (t.Col < minC) minC = t.Col; if (t.Col > maxC) maxC = t.Col; if (t.Row < minR) minR = t.Row; if (t.Row > maxR) maxR = t.Row;
                        done++;
                        if ((done & 7) == 0 || done == tiles.Count) Report("tiles");
                        continue;
                    }
                    if (good) px =img.Width == WorldMapMath.MinimapPx && img.Height == WorldMapMath.MinimapPx ? img.Bgra : Resample(img, WorldMapMath.MinimapPx);
                    else
                    {
                        res.Missing++;
                        MapRender.TileImage art = null;
                        try { art = standIn?.Invoke(t); } catch (OperationCanceledException) { throw; } catch (Exception) { art = null; }
                        shown = art != null && art.Bgra != null && art.Width > 0 && art.Bgra.Length >= art.Width * art.Height * 4 ? "art" : "plain";
                        px = Placeholder(shown == "art" ? (art.Width == WorldMapMath.MinimapPx && art.Height == WorldMapMath.MinimapPx ? art.Bgra : Resample(art, WorldMapMath.MinimapPx)) : null);
                        if (why == "missing") Util.Log("world map: " + (name ?? "map " + mapId) + ": minimap " + t.Col + "," + t.Row + " is not in the local game storage yet; shown as " + (shown == "art" ? "continent map art" : "a placeholder") + " and read again later");
                    }
                    var tile = new byte[WorldMapMath.TilePx * WorldMapMath.TilePx * 4];
                    for (int y = y0; y <= y1; y++)
                        for (int x = x0; x <= x1; x++)
                        {
                            int ox = (x - t.Col * 2) * WorldMapMath.TilePx, oy = (y - t.Row * 2) * WorldMapMath.TilePx;
                            for (int r = 0; r < WorldMapMath.TilePx; r++)
                                Buffer.BlockCopy(px, ((oy + r) * WorldMapMath.MinimapPx + ox) * 4, tile, r * WorldMapMath.TilePx * 4, WorldMapMath.TilePx * 4);
                            WriteJpeg(TileFile(mapDir, nativeLevel, x, y), tile);
                            res.Written++; present.Add(Key(x, y)); fresh.Add(Key(x, y));
                        }
                    if (good)
                    {
                        res.Decoded++; real++;
                        if (wasStandIn) { WorldMapPending.RemoveMarker(marker); res.Recovered++; Util.Log("world map: " + (name ?? "map " + mapId) + ": minimap " + t.Col + "," + t.Row + " reads now; the stand-in is replaced"); }
                    }
                    else
                    {
                        var p = new PendingTile { Col = t.Col, Row = t.Row, Part = "picture", Reason = why, Detail = detail, Shown = shown };
                        WorldMapPending.WriteMarker(marker, p);
                        res.Pending.Add(p);
                    }
                }
                if (t.Col < minC) minC = t.Col; if (t.Col > maxC) maxC = t.Col; if (t.Row < minR) minR = t.Row; if (t.Row > maxR) maxR = t.Row;
                done++;
                if ((done & 7) == 0 || done == tiles.Count) Report("tiles");
            }
            if (real == 0) throw new InvalidDataException("none of the " + tiles.Count + " minimap tiles could be read");

            // a native tile written this time (a first build, a resumed one, or a stand-in replaced by the real minimap): the tiles above it are made again
            foreach (var k in fresh)
            {
                int ax = KX(k), ay = KY(k);
                for (int lv = nativeLevel - 1; lv >= 0; lv--) { ax /= 2; ay /= 2; var f = TileFile(mapDir, lv, ax, ay); try { if (File.Exists(f)) File.Delete(f); } catch (Exception) { } }
            }

            // the levels above, from the one below
            var below = present;
            for (int lv = nativeLevel - 1; lv >= 0; lv--)
            {
                var cur = new HashSet<long>();
                foreach (var k in below) cur.Add(Key(KX(k) / 2, KY(k) / 2));
                var buf = new byte[WorldMapMath.TilePx * WorldMapMath.TilePx * 4];
                var child = new byte[WorldMapMath.TilePx * WorldMapMath.TilePx * 4];
                foreach (var k in cur.OrderBy(k => KY(k)).ThenBy(k => KX(k)))
                {
                    ct.ThrowIfCancellationRequested();
                    int px = KX(k), py = KY(k);
                    var file = TileFile(mapDir, lv, px, py);
                    if (Have(file)) res.Skipped++;
                    else
                    {
                        for (int i = 0; i < buf.Length; i += 4) { buf[i] = SeaB; buf[i + 1] = SeaG; buf[i + 2] = SeaR; buf[i + 3] = 255; }
                        for (int dy = 0; dy < 2; dy++)
                            for (int dx = 0; dx < 2; dx++)
                            {
                                int cx = px * 2 + dx, cy = py * 2 + dy;
                                if (!below.Contains(Key(cx, cy)) || !ReadJpeg(TileFile(mapDir, lv + 1, cx, cy), child)) continue;
                                HalveInto(child, buf, dx * (WorldMapMath.TilePx / 2), dy * (WorldMapMath.TilePx / 2));
                            }
                        WriteJpeg(file, buf);
                        res.Written++;
                    }
                    done++;
                    if ((done & 15) == 0) Report("levels");
                }
                below = cur;
            }
            Report("levels");

            long bytes = 0; int files = 0;
            for (int lv = 0; lv <= nativeLevel; lv++)
            {
                var dir = Path.Combine(mapDir, "z" + lv);
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.GetFiles(dir, "*.jpg")) { files++; try { bytes += new FileInfo(f).Length; } catch (Exception) { } }
            }
            res.Files = files; res.Bytes = bytes; res.MinCol = minC == int.MaxValue ? 0 : minC; res.MaxCol = maxC == int.MinValue ? 0 : maxC; res.MinRow = minR == int.MaxValue ? 0 : minR; res.MaxRow = maxR == int.MinValue ? 0 : maxR;
            var m = new Manifest
            {
                Version = Version, MapId = mapId, BuildKey = buildKey, Name = name ?? "", NativeLevel = nativeLevel, Adt = tiles.Count - res.Missing, Missing = res.Missing, Files = files, Bytes = bytes,
                Built = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), MinCol = res.MinCol, MaxCol = res.MaxCol, MinRow = res.MinRow, MaxRow = res.MaxRow,
                Pending = res.Pending, Stamp = stamp ?? "",
            };
            var tmp = ManifestPath(mapDir) + ".tmp";
            File.WriteAllText(tmp, ManifestJson(m));
            if (File.Exists(ManifestPath(mapDir))) File.Delete(ManifestPath(mapDir));
            File.Move(tmp, ManifestPath(mapDir));
            res.Complete = true;
            return res;
        }

        // the stand-in of a minimap that cannot be read yet (512 x 512 BGRA): the low-detail picture under a light diagonal hatch, or - with none - the hatch on
        // the theme's raised surfaces (Surface2 #1E2226 with Surface3 #262B30 stripes, Theme.xaml). Calm, and clearly not land or sea.
        public const int HatchPeriod = 24, HatchWidth = 3;
        public static readonly byte[] PlainBase = { 0x26, 0x22, 0x1E }, PlainStripe = { 0x30, 0x2B, 0x26 };    // BGR of Surface2 / Surface3
        public static bool IsHatch(int x, int y) => ((x + y) % HatchPeriod) < HatchWidth;
        public static byte[] Placeholder(byte[] art)
        {
            const int N = WorldMapMath.MinimapPx;
            var o = new byte[N * N * 4];
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    int i = (y * N + x) * 4; bool h = IsHatch(x, y);
                    if (art == null) { var c = h ? PlainStripe : PlainBase; o[i] = c[0]; o[i + 1] = c[1]; o[i + 2] = c[2]; }
                    else
                        for (int k = 0; k < 3; k++)
                        {
                            int v = art[i + k] * 4 / 5;                    // a little darker than the real minimaps around it
                            o[i + k] = (byte)(h ? Math.Min(255, v + (255 - v) / 5) : v);
                        }
                    o[i + 3] = 255;
                }
            return o;
        }

        static long Key(int x, int y) => ((long)x << 32) | (uint)y;
        static int KX(long k) => (int)(k >> 32);
        static int KY(long k) => (int)(k & 0xFFFFFFFF);

        // nearest-neighbour to a square size (a minimap of another size than expected; not a path the real game takes)
        static byte[] Resample(MapRender.TileImage img, int size)
        {
            var o = new byte[size * size * 4];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    Buffer.BlockCopy(img.Bgra, ((y * img.Height / size) * img.Width + x * img.Width / size) * 4, o, (y * size + x) * 4, 4);
            return o;
        }

        // a 256 x 256 tile averaged 2 x 2 into the 128 x 128 square of the 256 x 256 destination at (ox, oy)
        static void HalveInto(byte[] src, byte[] dst, int ox, int oy)
        {
            const int N = WorldMapMath.TilePx;
            for (int y = 0; y < N / 2; y++)
                for (int x = 0; x < N / 2; x++)
                {
                    int s = ((y * 2) * N + x * 2) * 4, d = ((oy + y) * N + ox + x) * 4;
                    for (int c = 0; c < 3; c++)
                        dst[d + c] = (byte)((src[s + c] + src[s + 4 + c] + src[s + N * 4 + c] + src[s + N * 4 + 4 + c] + 2) >> 2);
                    dst[d + 3] = 255;
                }
        }

        // ---- JPEG
        public static void WriteJpeg(string path, byte[] bgra)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var bmp = BitmapSource.Create(WorldMapMath.TilePx, WorldMapMath.TilePx, 96, 96, PixelFormats.Bgr32, null, bgra, WorldMapMath.TilePx * 4);
            bmp.Freeze();
            var enc = new JpegBitmapEncoder { QualityLevel = JpegQuality };
            enc.Frames.Add(BitmapFrame.Create(bmp));
            var tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None)) enc.Save(fs);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        // false when the file is missing or damaged
        public static bool ReadJpeg(string path, byte[] into)
        {
            try
            {
                if (!Have(path)) return false;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var dec = new JpegBitmapDecoder(fs, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
                    BitmapSource f = dec.Frames[0];
                    if (f.PixelWidth != WorldMapMath.TilePx || f.PixelHeight != WorldMapMath.TilePx) return false;
                    if (f.Format != PixelFormats.Bgra32) f = new FormatConvertedBitmap(f, PixelFormats.Bgra32, null, 0);
                    f.CopyPixels(into, WorldMapMath.TilePx * 4, 0);
                    return true;
                }
            }
            catch (Exception) { return false; }
        }
    }
}
