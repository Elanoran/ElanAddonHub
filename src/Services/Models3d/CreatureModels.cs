using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Maps;
using ElansAddonHub.Services.WorldMap;

namespace ElansAddonHub.Services.Models3d
{
    // ============================================================================================================================
    // 3.6: Atlas 3D models, step 2 - where the built creature models live and how one is made. Read from the player's OWN WoW install, read-only, on demand
    // (the viewer asks for one display at a time), never the network:
    //   <data>\atlas\models\<build key>\m\{displayId}.m2b2   the model (CreatureModel's "M2B2")
    //   <data>\atlas\models\<build key>\t\{fdid}.mtx         its textures (ObjTexture's "MTX1", shared by every model that uses them)
    // The build key is the game build's stamp (MapData.Stamp) + "_m" + the format, so a game patch or a new format is a new folder, and the folders of
    // other builds go the first time a model is made for this one (as WorldMapCache.RemoveOtherBuilds). The model file is written last: when it is
    // there, its textures are too. The game storage is the shared one (CascShared): no continent, no world map.
    // ============================================================================================================================
    public interface IModelSource : IDisposable
    {
        byte[] Read(int fdid);                        // null = not in the install
        byte[] ReadBlp(int fdid, int maxSize);        // a BLP from the first mip level that fits maxSize (ObjTexture.Trim); null = not readable
    }

    public sealed class CascModelSource : IModelSource
    {
        readonly CascShared.Lease lease;
        readonly Dictionary<int, byte[]> keys = new Dictionary<int, byte[]>();
        public CascModelSource(CascShared.Lease l) { lease = l; }
        bool Key(int fdid, out byte[] ck)
        {
            lock (keys)
            {
                if (keys.TryGetValue(fdid, out ck)) return ck != null;
                lease.Casc.ResolveFileIds(new HashSet<int> { fdid }).TryGetValue(fdid, out ck);
                keys[fdid] = ck; return ck != null;
            }
        }
        public byte[] Read(int fdid)
        {
            if (fdid <= 0 || !Key(fdid, out var ck)) return null;
            try { return lease.Casc.ReadByContentKey(ck); } catch (Exception e) { Util.Log("creature models: file " + fdid + ": " + e.Message); return null; }
        }
        public byte[] ReadBlp(int fdid, int maxSize)
        {
            if (fdid <= 0 || !Key(fdid, out var ck)) return null;
            try { return ObjTexture.Trim((o, n) => lease.Casc.ReadRangeByContentKey(ck, o, n), maxSize); } catch (Exception e) { Util.Log("creature models: texture " + fdid + ": " + e.Message); return null; }
        }
        public void Dispose() => lease.Dispose();
    }

    // the self-test's files
    public sealed class FuncModelSource : IModelSource
    {
        readonly Func<int, byte[]> read;
        public int Reads;
        public FuncModelSource(Func<int, byte[]> r) { read = r; }
        public byte[] Read(int fdid) { Interlocked.Increment(ref Reads); return read(fdid); }
        public byte[] ReadBlp(int fdid, int maxSize) => ObjTexture.TrimBytes(Read(fdid), maxSize);
        public void Dispose() { }
    }

    // what EnsureAsync gives: the model file's path, or why there is none
    public sealed class ModelBuildResult
    {
        public string Path, Reason;
        public bool Built;                    // this call made the file (false = it was there)
        public CreatureModel Model;           // the parsed model when this call made it (the checks look at it)
        public List<int> MissingTextures = new List<int>();
        public bool Ok => Path != null;
        public override string ToString() => Ok ? Path : "no model: " + Reason;
    }

    public static class CreatureModels
    {
        public static string RootOverride;                      // self-test: a scratch folder
        public static string Root => RootOverride ?? Path.Combine(Util.DataDir, "atlas", "models");
        public static Func<IModelSource> SourceForTest;          // self-test: files instead of the game's storage
        public static Func<string> BuildForTest;                 // self-test: the game build text (as ItemData.GameBuild gives it)
        public static int TextureMaxSize = 1024;                 // a creature is seen from close by: one level more than the world map's 512
        public static int BuildCountForTest;

        public static string KeyFor(string build) => MapData.Stamp(build) + "_m" + CreatureModel.FormatVersion.ToString(CultureInfo.InvariantCulture);
        public static string Dir(string key) => Path.Combine(Root, key);
        public static string ModelPath(string key, int displayId) => Path.Combine(Root, key, "m", displayId.ToString(CultureInfo.InvariantCulture) + ".m2b2");
        public static string TexturePath(string key, int fdid) => Path.Combine(Root, key, "t", fdid.ToString(CultureInfo.InvariantCulture) + ".mtx");

        static readonly object gate = new object();
        static readonly Dictionary<string, Task<ModelBuildResult>> running = new Dictionary<string, Task<ModelBuildResult>>();     // key|display -> its one build
        static readonly Dictionary<string, string> failed = new Dictionary<string, string>();                                        // key|display -> why (the files will not change)
        static readonly HashSet<string> good = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> cleaned = new HashSet<string>(StringComparer.Ordinal);

        public static void ResetForTest() { lock (gate) { running.Clear(); failed.Clear(); good.Clear(); cleaned.Clear(); } BuildCountForTest = 0; }

        // the folders of other game builds (or formats) are of no use any more
        public static int RemoveOtherBuilds(string key)
        {
            int n = 0;
            try
            {
                if (!Directory.Exists(Root)) return 0;
                foreach (var d in Directory.GetDirectories(Root))
                {
                    if (string.Equals(Path.GetFileName(d), key, StringComparison.Ordinal)) continue;
                    if (string.Equals(Path.GetFileName(d), ModelViewerPage.Folder, StringComparison.OrdinalIgnoreCase)) continue;     // 3.6 step 3: the viewer's own page
                    try { Util.DeleteDir(d); n++; } catch (Exception e) { Util.Log("creature models: " + e.Message); }
                }
            }
            catch (Exception) { }
            return n;
        }

        // a finished model file of this build: it reads, and every texture it names is there
        public static bool Valid(string key, string path)
        {
            lock (gate) if (good.Contains(path) && File.Exists(path)) return true;
            try
            {
                if (!File.Exists(path)) return false;
                var meta = CreatureModel.ReadMeta(File.ReadAllBytes(path), out _);
                if (meta == null) return false;
                foreach (var t in meta.List("textures")) if (t is double x && !File.Exists(TexturePath(key, (int)x))) return false;
                lock (gate) good.Add(path);
                return true;
            }
            catch (Exception) { return false; }
        }

        // The model of one creature display, built when it is not there yet. Safe to call again and again: a finished file is returned at once, a build
        // that is running is shared (one per display at a time), and a display that cannot be built keeps its reason for the session.
        public static Task<ModelBuildResult> EnsureAsync(int displayId)
        {
            string build, root = null, flavor = null;
            if (SourceForTest != null) build = BuildForTest?.Invoke() ?? "test|0.0.0.0|test";
            else
            {
                root = ItemData.WowRoot();
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return Task.FromResult(new ModelBuildResult { Reason = "the WoW folder is not set" });
                flavor = ItemData.Flavor() ?? "_classic_beta_";
                build = ItemData.GameBuild(root, flavor);
                if (build == null) return Task.FromResult(new ModelBuildResult { Reason = "no .build.info in the WoW folder" });
            }
            if (displayId <= 0) return Task.FromResult(new ModelBuildResult { Reason = "no display id" });
            var key = KeyFor(build);
            var path = ModelPath(key, displayId);
            if (Valid(key, path)) return Task.FromResult(new ModelBuildResult { Path = path });
            var id = key + "|" + displayId.ToString(CultureInfo.InvariantCulture);
            lock (gate)
            {
                if (failed.TryGetValue(id, out var why)) return Task.FromResult(new ModelBuildResult { Reason = why });
                if (running.TryGetValue(id, out var t)) return t;
                t = Task.Run(() => Build(displayId, key, root, flavor));
                running[id] = t;
                t.ContinueWith(_ => { lock (gate) running.Remove(id); }, TaskScheduler.Default);
                return t;
            }
        }

        static ModelBuildResult Build(int displayId, string key, string root, string flavor)
        {
            var res = new ModelBuildResult();
            var id = key + "|" + displayId.ToString(CultureInfo.InvariantCulture);
            if (!ModelData.Ready)
            {
                if (ModelData.Status == ModelData.State.NotStarted) ModelData.Start();
                res.Reason = "the creature model data is " + (ModelData.Status == ModelData.State.Unavailable ? "unavailable" + (string.IsNullOrEmpty(ModelData.Reason) ? "" : " (" + ModelData.Reason + ")")
                    : ModelData.Status == ModelData.State.Disabled ? "switched off" : "not read yet");
                return res;     // not kept: it changes when the data is read
            }
            var nm = ModelData.ForDisplay(displayId);
            if (!nm.Ok) { res.Reason = nm.Reason ?? "no model"; lock (gate) failed[id] = res.Reason; return res; }
            Interlocked.Increment(ref BuildCountForTest);
            lock (gate) if (cleaned.Add(key)) { int gone = RemoveOtherBuilds(key); if (gone > 0) Util.Log("creature models: removed " + gone + " folder(s) of other game builds"); }
            try
            {
                using (var src = SourceForTest != null ? SourceForTest() : new CascModelSource(CascShared.Open(root, flavor)))
                {
                    var rootFile = src.Read(nm.ModelFileId);
                    if (rootFile == null) return Fail(res, id, "the model file " + nm.ModelFileId + " is not in this game install");
                    var ch = CreatureModel.Chunks.Parse(rootFile);
                    var skin = ch.Ok ? src.Read(ch.Skins[0]) : null;
                    var m = CreatureModel.Parse(rootFile, skin, nm.Textures, nm.Scale, src.Read, displayId);
                    m.ModelFdid = nm.ModelFileId;
                    if (!m.Drawable) return Fail(res, id, "the model " + nm.ModelFileId + " cannot be shown: " + (m.Problem ?? "nothing to draw"));
                    foreach (var fdid in m.Textures.ToList())
                    {
                        var tp = TexturePath(key, fdid);
                        if (File.Exists(tp)) continue;
                        var mtx = ObjTexture.Pack(src.ReadBlp(fdid, TextureMaxSize));
                        if (mtx == null) { res.MissingTextures.Add(fdid); continue; }
                        Write(tp, mtx);
                    }
                    if (res.MissingTextures.Count > 0)
                    {
                        // a texture that cannot be read is a stand-in, not a broken file
                        foreach (var b in m.Batches) if (res.MissingTextures.Contains(b.Tex)) b.Tex = 0;
                        m.Textures.RemoveAll(res.MissingTextures.Contains);
                        m.Notes.Add("texture " + string.Join(", ", res.MissingTextures) + " could not be read; a stand-in is shown");
                    }
                    var path = ModelPath(key, displayId);
                    Write(path, m.ToBytes());
                    lock (gate) good.Add(path);
                    res.Path = path; res.Built = true; res.Model = m;
                    return res;
                }
            }
            catch (Exception e) { Util.Log("creature models: display " + displayId + ": " + e.Message); res.Reason = "the game files could not be read: " + e.Message; return res; }
        }

        static ModelBuildResult Fail(ModelBuildResult r, string id, string why) { r.Reason = why; lock (gate) failed[id] = why; return r; }

        static void Write(string path, byte[] b)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var tmp = path + "." + Environment.CurrentManagedThreadId.ToString(CultureInfo.InvariantCulture) + ".tmp";
            File.WriteAllBytes(tmp, b);
            File.Move(tmp, path, true);
        }

        // bytes of the built models and their textures
        public static long SizeBytes()
        {
            long total = 0;
            try { if (Directory.Exists(Root)) foreach (var f in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)) { try { total += new FileInfo(f).Length; } catch (Exception) { } } }
            catch (Exception) { }
            return total;
        }
    }
}
