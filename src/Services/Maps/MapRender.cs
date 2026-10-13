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
    // Map pictures: the BLP tiles of a map's art composed into one bitmap, kept as a PNG in <data>\atlas\maps\<build stamp>_<uiMapId>.png.
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
        public static string PathFor(string stamp, int uiMapId) => Path.Combine(Dir, stamp + "_" + uiMapId + ".png");
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
                    foreach (var id in todo) { var layer = idx.ArtFor(id).BaseLayer; foreach (var t in idx.ArtFor(id).Tiles) if (t.Layer == layer.Index) want.Add(t.FileDataId); }
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
                        var png = RenderPng(art, source, out int missing, out int w, out int h);
                        if (png == null) continue;
                        var path = PathFor(stamp, id);
                        var tmp = path + ".tmp";
                        File.WriteAllBytes(tmp, png);
                        if (File.Exists(path)) File.Delete(path);
                        File.Move(tmp, path);
                        results[id] = path;
                        LastRenderMs = sw.ElapsedMilliseconds; LastPeakBytes = (long)w * h * 4;
                        Interlocked.Increment(ref RenderCountForTest);
                        if (missing > 0) Util.Log("map render: ui map " + id + ": " + missing + " tile(s) not in the game files");
                    }
                    catch (Exception e) { Util.Log("map render: ui map " + id + ": " + e.Message); }
                }
            }
            finally { casc?.Dispose(); }
        }

        // pictures of other game builds are of no use any more
        static void RemoveOtherBuilds(string stamp)
        {
            try
            {
                foreach (var f in Directory.GetFiles(Dir, "*.png"))
                    if (!Path.GetFileName(f).StartsWith(stamp + "_", StringComparison.Ordinal)) { try { File.Delete(f); } catch { } }
            }
            catch { }
        }

        // ---- the composition itself (no game files, no disk: unit-testable)

        // BGRA pixels of the base layer of an art: tile (row, col) goes to (col * tileWidth, row * tileHeight), cut off at the layer's size.
        // A tile the source does not have (null) is left transparent and counted in `missing`.
        public static byte[] Compose(MapArtInfo art, Func<int, TileImage> tile, out int width, out int height, out int missing)
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
            return px;
        }

        public static byte[] RenderPng(MapArtInfo art, Func<int, TileImage> tile, out int missing, out int width, out int height)
        {
            var px = Compose(art, tile, out width, out height, out missing);
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
