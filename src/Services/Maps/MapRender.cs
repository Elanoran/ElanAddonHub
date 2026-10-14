using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ElansAddonHub.Services.Maps
{
    // Map pictures: the BLP tiles of a map's art composed into one bitmap, kept as a PNG in <data>\atlas\maps\<build stamp>_v<render version>_<uiMapId>.png.
    // The base tiles are the "unexplored" art; the game draws the explored parts (WorldMapOverlay / WorldMapOverlayTile) on top, so those are drawn
    // over the base at their offsets as well and the whole zone looks explored.
    // Made lazily, one request batch at a time on a background thread (never on the UI thread); nothing is held in memory afterwards (the caller
    // loads the PNG it needs and lets go of it). Tiles are decoded one at a time straight into the picture, so a 3840 x 2560 continent needs
    // about 40 MB while it is being made and nothing after.
    public static class MapRender
    {
        public static Func<string> WowRoot = () => MapData.WowRoot();
        public static Func<string> Flavor = () => MapData.Flavor();
        // test hook: where tiles come from instead of the game's files (FileDataID -> BGRA tile); null = the game
        public static Func<int, TileImage> TileSourceForTest;
        public static string DirOverride;                 // self-test: a scratch folder

        public static string Dir => DirOverride ?? Path.Combine(Util.DataDir, "atlas", "maps");
        // bump when the picture itself changes (2: explored-area overlays drawn over the base art): the old pictures are then made again
        public const int RenderVersion = 2;
        public const int OverlayTile = 256;               // the overlay textures are cut into 256 px tiles
        static string Prefix(string stamp) => stamp + "_v" + RenderVersion + "_";
        public static string PathFor(string stamp, int uiMapId) => Path.Combine(Dir, Prefix(stamp) + uiMapId + ".png");
        public static string PathFor(int uiMapId) => PathFor(MapData.BuildStamp, uiMapId);

        public sealed class TileImage { public byte[] Bgra; public int Width, Height; }

        static readonly object gate = new object();
        static readonly Dictionary<int, List<Action<string>>> waiting = new Dictionary<int, List<Action<string>>>();
        static readonly Dictionary<int, DateTime> failed = new Dictionary<int, DateTime>();
        static bool running;
        public static string Problem { get; private set; }
        public static long LastRenderMs { get; private set; }
        public static long LastPeakBytes { get; private set; }     // size of the bitmap that was composed last
        public static int RenderCountForTest;                      // pictures composed (not served from the folder)

        // the finished picture if it is already on disk, else null
        public static string TryGet(int uiMapId)
        {
            var p = PathFor(uiMapId);
            try { return File.Exists(p) && new FileInfo(p).Length > 0 ? p : null; } catch { return null; }
        }

        // calls back (on a worker thread - marshal to the UI yourself) with the PNG path once the picture is there, or with null when it cannot be made
        public static void Request(int uiMapId, Action<string> done)
        {
            var have = TryGet(uiMapId);
            if (have != null) { done(have); return; }
            var idx = MapData.Index;
            if (idx == null || !idx.HasArt(uiMapId)) { done(null); return; }
            lock (gate)
            {
                if (failed.TryGetValue(uiMapId, out var at) && (DateTime.UtcNow - at).TotalSeconds < 90) { done(null); return; }
                if (!waiting.TryGetValue(uiMapId, out var l)) waiting[uiMapId] = l = new List<Action<string>>();
                l.Add(done);
                if (running) return;
                running = true;
            }
            Task.Run(Worker);
        }

        static void Worker()
        {
            try
            {
                Thread.Sleep(60);       // let a few requests arrive together: one pass over the game's file list serves them all
                while (true)
                {
                    List<int> batch;
                    lock (gate)
                    {
                        batch = waiting.Keys.ToList();
                        if (batch.Count == 0) { running = false; return; }
                    }
                    var results = new Dictionary<int, string>();
                    try { RenderBatch(batch, results); SetProblem(null); }
                    catch (Exception e)
                    {
                        Util.Log("map render: " + e.Message);
                        SetProblem(e.Message);
                    }
                    foreach (var id in batch)
                    {
                        List<Action<string>> cbs;
                        results.TryGetValue(id, out var path);
                        lock (gate)
                        {
                            waiting.TryGetValue(id, out cbs);
                            waiting.Remove(id);
                            if (path == null) failed[id] = DateTime.UtcNow; else failed.Remove(id);
                        }
                        if (cbs != null) foreach (var cb in cbs) { try { cb(path); } catch { } }
                    }
                }
            }
            catch (Exception e)
            {
                Util.Log("map render worker: " + e);
                lock (gate) { foreach (var l in waiting.Values) foreach (var cb in l) { try { cb(null); } catch { } } waiting.Clear(); running = false; }
            }
        }

        static void SetProblem(string p) { Problem = p; }

        static void RenderBatch(List<int> ids, Dictionary<int, string> results)
        {
            var idx = MapData.Index;
            if (idx == null) throw new IOException("the map data is not loaded");
            var stamp = MapData.BuildStamp;
            var todo = new List<int>();
            foreach (var id in ids) { var have = TryGet(id); if (have != null) results[id] = have; else if (idx.HasArt(id)) todo.Add(id); }
            if (todo.Count == 0) return;
            Directory.CreateDirectory(Dir);
            RemoveOtherBuilds(stamp);

            CascStorage casc = null;
            try
            {
                Func<int, TileImage> source = TileSourceForTest;
                if (source == null)
                {
                    var root = WowRoot();
                    if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) throw new IOException("the WoW folder is not set");
                    casc = CascStorage.Open(root, Flavor() ?? "_classic_beta_");
                    var want = new HashSet<int>();
                    foreach (var id in todo) { var layer = idx.ArtFor(id).BaseLayer; foreach (var t in idx.ArtFor(id).Tiles) if (t.Layer == layer.Index) want.Add(t.FileDataId); foreach (var o in idx.ArtFor(id).Overlays) foreach (var t in o.Tiles) want.Add(t.FileDataId); }
                    var keys = casc.ResolveFileIds(want);
                    var c = casc;
                    source = fid =>
                    {
                        if (!keys.TryGetValue(fid, out var ck)) return null;
                        var blp = c.ReadByContentKey(ck);
                        if (blp == null) return null;
                        var px = Blp.Decode(blp, out int w, out int h);
                        return new TileImage { Bgra = px, Width = w, Height = h };
                    };
                }
                foreach (var id in todo)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        var art = idx.ArtFor(id);
                        var png = RenderPng(art, source, out int missing, out int w, out int h, out int ovMissing);
                        if (png == null) continue;
                        var path = PathFor(stamp, id);
                        var tmp = path + ".tmp";
                        File.WriteAllBytes(tmp, png);
                        if (File.Exists(path)) File.Delete(path);
                        File.Move(tmp, path);
                        results[id] = path;
                        LastRenderMs = sw.ElapsedMilliseconds; LastPeakBytes = (long)w * h * 4;
                        Interlocked.Increment(ref RenderCountForTest);
                        if (missing > 0 || ovMissing > 0) Util.Log("map render: ui map " + id + ": " + missing + " tile(s) and " + ovMissing + " overlay tile(s) not in the game files");
                    }
                    catch (Exception e) { Util.Log("map render: ui map " + id + ": " + e.Message); }
                }
            }
            finally { casc?.Dispose(); }
        }

        // pictures of other game builds (and of older render versions) are of no use any more
        static void RemoveOtherBuilds(string stamp)
        {
            try
            {
                foreach (var f in Directory.GetFiles(Dir, "*.png"))
                    if (!Path.GetFileName(f).StartsWith(Prefix(stamp), StringComparison.Ordinal)) { try { File.Delete(f); } catch { } }
            }
            catch { }
        }

        // ---- the composition itself (no game files, no disk: unit-testable)

        // BGRA pixels of the base layer of an art: tile (row, col) goes to (col * tileWidth, row * tileHeight), cut off at the layer's size.
        // A tile the source does not have (null) is left transparent and counted in `missing`.
        public static byte[] Compose(MapArtInfo art, Func<int, TileImage> tile, out int width, out int height, out int missing)
            => Compose(art, tile, out width, out height, out missing, out _);

        // ... then every overlay of the art over it: overlay tile (row, col) goes to (OffsetX + col * 256, OffsetY + row * 256) of the same pixel space
        // (the art layer's), cut off at the overlay's TextureWidth x TextureHeight and at the picture, blended by its alpha. Overlay tiles the source
        // does not have are skipped and counted in `overlayMissing`.
        public static byte[] Compose(MapArtInfo art, Func<int, TileImage> tile, out int width, out int height, out int missing, out int overlayMissing)
        {
            var layer = art.BaseLayer;
            if (layer == null || layer.Width <= 0 || layer.Height <= 0 || layer.Width > 8192 || layer.Height > 8192) throw new InvalidDataException("the art has no usable layer");
            width = layer.Width; height = layer.Height; missing = 0;
            var px = new byte[width * height * 4];
            foreach (var t in art.Tiles)
            {
                if (t.Layer != layer.Index) continue;
                var img = tile(t.FileDataId);
                if (img == null || img.Bgra == null) { missing++; continue; }
                int x0 = t.Col * layer.TileWidth, y0 = t.Row * layer.TileHeight;
                if (x0 >= width || y0 >= height) continue;
                int w = Math.Min(img.Width, width - x0), h = Math.Min(img.Height, height - y0);
                for (int y = 0; y < h; y++) Buffer.BlockCopy(img.Bgra, y * img.Width * 4, px, ((y0 + y) * width + x0) * 4, w * 4);
            }
            overlayMissing = 0;
            foreach (var o in art.Overlays)
            {
                if (o.Width <= 0 || o.Height <= 0) continue;
                int first = o.Tiles.Length == 0 ? 0 : o.Tiles.Min(x => x.Layer);
                foreach (var t in o.Tiles)
                {
                    if (t.Layer != first) continue;     // the lowest layer is the picture
                    int ox = o.OffsetX + t.Col * OverlayTile, oy = o.OffsetY + t.Row * OverlayTile;
                    int cw = Math.Min(OverlayTile, o.Width - t.Col * OverlayTile), ch = Math.Min(OverlayTile, o.Height - t.Row * OverlayTile);
                    if (cw <= 0 || ch <= 0 || ox >= width || oy >= height || ox + cw <= 0 || oy + ch <= 0) continue;
                    var img = tile(t.FileDataId);
                    if (img == null || img.Bgra == null) { overlayMissing++; continue; }
                    Blend(px, width, height, img, ox, oy, Math.Min(cw, img.Width), Math.Min(ch, img.Height));
                }
            }
            return px;
        }

        // src-over of the top-left w x h part of `img` onto the picture at (x0, y0) (BGRA, straight alpha)
        static void Blend(byte[] px, int width, int height, TileImage img, int x0, int y0, int w, int h)
        {
            for (int y = 0; y < h; y++)
            {
                int dy = y0 + y; if (dy < 0 || dy >= height) continue;
                for (int x = 0; x < w; x++)
                {
                    int dx = x0 + x; if (dx < 0 || dx >= width) continue;
                    int s = (y * img.Width + x) * 4, d = (dy * width + dx) * 4;
                    int sa = img.Bgra[s + 3];
                    if (sa == 0) continue;
                    if (sa == 255) { px[d] = img.Bgra[s]; px[d + 1] = img.Bgra[s + 1]; px[d + 2] = img.Bgra[s + 2]; px[d + 3] = 255; continue; }
                    int da = px[d + 3], outA = sa * 255 + da * (255 - sa);       // x 255
                    if (outA == 0) continue;
                    for (int c = 0; c < 3; c++) px[d + c] = (byte)((img.Bgra[s + c] * sa * 255 + px[d + c] * da * (255 - sa) + outA / 2) / outA);
                    px[d + 3] = (byte)((outA + 127) / 255);
                }
            }
        }

        public static byte[] RenderPng(MapArtInfo art, Func<int, TileImage> tile, out int missing, out int width, out int height)
            => RenderPng(art, tile, out missing, out width, out height, out _);

        public static byte[] RenderPng(MapArtInfo art, Func<int, TileImage> tile, out int missing, out int width, out int height, out int overlayMissing)
        {
            var px = Compose(art, tile, out width, out height, out missing, out overlayMissing);
            if (missing >= art.Tiles.Count(t => t.Layer == art.BaseLayer.Index)) return null;     // nothing of it exists
            return ToPng(px, width, height);
        }

        public static byte[] ToPng(byte[] bgra, int w, int h)
        {
            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bgra, w * 4);
            bmp.Freeze();
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (var ms = new MemoryStream()) { enc.Save(ms); return ms.ToArray(); }
        }

        // loads a picture for display: decoded once, frozen, no file lock
        public static ImageSource Load(string png)
        {
            try
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bi.UriSource = new Uri(png);
                bi.EndInit();
                bi.Freeze();
                return bi;
            }
            catch { return null; }
        }

        public static void ForgetFailuresForTest() { lock (gate) failed.Clear(); }
    }
}
