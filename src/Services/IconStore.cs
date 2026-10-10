using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ElansAddonHub.Services
{
    // Item icons straight from the player's own game install (local CASC storage, read-only), converted to PNG once and kept in
    // %LOCALAPPDATA%\ElansAddonHub\cache\icons\<fileDataId>.png. Nothing is downloaded and nothing is written to the WoW folder.
    public static class IconStore
    {
        public static Func<string> WowRoot = () => null;
        public static string Flavor = "_classic_beta_";

        public static string Dir => Path.Combine(Util.DataDir, "cache", "icons");
        public static string PathFor(int id) => Path.Combine(Dir, id + ".png");

        static readonly object gate = new object();
        static readonly Dictionary<int, List<Action<ImageSource>>> waiting = new Dictionary<int, List<Action<ImageSource>>>();
        static readonly Dictionary<int, ImageSource> memory = new Dictionary<int, ImageSource>();
        static readonly HashSet<int> missing = new HashSet<int>();   // not in the game data (this session)
        static bool running;
        static DateTime failedAt = DateTime.MinValue;

        // why icons are not available (null while it works or was not needed yet)
        public static string Problem { get; private set; }
        public static event Action ProblemChanged;

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

        // the icon if it is already on disk / in memory, else null
        public static ImageSource TryGet(int id)
        {
            lock (gate) { if (memory.TryGetValue(id, out var m)) return m; }
            var p = PathFor(id);
            if (!File.Exists(p)) return null;
            var img = Load(p);
            if (img != null) lock (gate) memory[id] = img;
            return img;
        }

        // calls back (on the thread that extracted it - marshal to the UI yourself) once the icon is there; never for an unavailable one
        public static void Request(int id, Action<ImageSource> done)
        {
            if (id <= 0) return;
            var have = TryGet(id);
            if (have != null) { done(have); return; }
            lock (gate)
            {
                if (missing.Contains(id)) return;
                if (Problem != null && (DateTime.UtcNow - failedAt).TotalSeconds < 90) return;
                if (!waiting.TryGetValue(id, out var l)) waiting[id] = l = new List<Action<ImageSource>>();
                l.Add(done);
                if (running) return;
                running = true;
            }
            Task.Run(() => Worker());
        }

        static void Worker()
        {
            try
            {
                Thread.Sleep(120); // let a whole page of requests arrive
                while (true)
                {
                    HashSet<int> batch;
                    lock (gate)
                    {
                        batch = new HashSet<int>(waiting.Keys);
                        if (batch.Count == 0) { running = false; return; }
                    }
                    Dictionary<int, byte[]> done;
                    try { done = Extract(batch); SetProblem(null); }
                    catch (Exception e)
                    {
                        Util.Log("icons: " + e.Message);
                        failedAt = DateTime.UtcNow;
                        SetProblem(e.Message);
                        lock (gate) { waiting.Clear(); running = false; }
                        return;
                    }
                    foreach (var id in batch)
                    {
                        List<Action<ImageSource>> cbs;
                        ImageSource img = null;
                        if (done.TryGetValue(id, out var png))
                        {
                            try { Directory.CreateDirectory(Dir); File.WriteAllBytes(PathFor(id), png); img = Load(PathFor(id)); }
                            catch (Exception e) { Util.Log("icons: cannot cache " + id + ": " + e.Message); }
                        }
                        lock (gate)
                        {
                            waiting.TryGetValue(id, out cbs);
                            waiting.Remove(id);
                            if (img != null) memory[id] = img; else missing.Add(id);
                        }
                        if (img != null && cbs != null) foreach (var cb in cbs) { try { cb(img); } catch { } }
                    }
                }
            }
            catch (Exception e)
            {
                Util.Log("icons worker: " + e);
                lock (gate) { waiting.Clear(); running = false; }
            }
        }

        static void SetProblem(string p)
        {
            if (Problem == p) return;
            Problem = p;
            try { ProblemChanged?.Invoke(); } catch { }
        }

        // PNG bytes per file id that could be read (ids the game does not have are left out)
        public static Dictionary<int, byte[]> Extract(HashSet<int> ids, string wowRoot = null, string flavor = null)
        {
            wowRoot = wowRoot ?? WowRoot();
            if (string.IsNullOrEmpty(wowRoot) || !Directory.Exists(wowRoot)) throw new IOException("the WoW folder is not set");
            var res = new Dictionary<int, byte[]>();
            using (var casc = CascStorage.Open(wowRoot, flavor ?? Flavor))
            {
                var keys = casc.ResolveFileIds(ids);
                foreach (var kv in keys)
                {
                    try
                    {
                        var blp = casc.ReadByContentKey(kv.Value);
                        if (blp == null) continue;
                        res[kv.Key] = ToPng(blp);
                    }
                    catch (Exception e) { Util.Log("icons: " + kv.Key + ": " + e.Message); }
                }
            }
            return res;
        }

        public static byte[] ToPng(byte[] blp)
        {
            var bgra = Blp.Decode(blp, out int w, out int h);
            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bgra, w * 4);
            bmp.Freeze();
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (var ms = new MemoryStream()) { enc.Save(ms); return ms.ToArray(); }
        }

        // for the self-test: forget memory so the next TryGet goes to disk again
        public static void ForgetMemory() { lock (gate) { memory.Clear(); missing.Clear(); Problem = null; failedAt = DateTime.MinValue; } }

        // --iconprobe <fileId>: extract one icon from the local game data to %TEMP%\ehh-iconprobe\<id>.png (read-only, no cache, no settings)
        public static int Probe(string idText)
        {
            var dir = Path.Combine(Path.GetTempPath(), "ehh-iconprobe");
            Directory.CreateDirectory(dir);
            var log = Path.Combine(dir, "result.txt");
            try
            {
                if (!int.TryParse(idText, out var id)) throw new ArgumentException("file id expected");
                var root = Environment.GetEnvironmentVariable("ELANSHUB_WOW");
                if (string.IsNullOrEmpty(root)) root = WowLocator.Find();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var png = Extract(new HashSet<int> { id }, root, Flavor);
                if (!png.TryGetValue(id, out var data)) throw new FileNotFoundException("file id " + id + " is not in the game data");
                var path = Path.Combine(dir, id + ".png");
                File.WriteAllBytes(path, data);
                File.WriteAllText(log, "ok " + id + " " + data.Length + " bytes png in " + sw.ElapsedMilliseconds + " ms -> " + path + " (wow root " + root + ")\r\n");
                return 0;
            }
            catch (Exception e)
            {
                File.WriteAllText(log, "FAIL " + idText + ": " + e + "\r\n");
                return 1;
            }
        }
    }
}
