using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Atlas.Classic;

namespace ElansAddonHub.Services.Db2
{
    // ============================================================================================================================
    // 3.6: Atlas 3D models, step 1 - the data. Which model a creature wears, read from the player's OWN WoW install, read-only, in the background,
    // exactly like SpellData / TalentData:
    //   * CreatureDisplayInfo (display id -> model row, scale, the three monster skins, the "character model" link) and CreatureModelData (model row ->
    //     the .m2 FileDataID, its own scale) come out of the local CASC storage (ModelTables lists them and the layouts this version knows)
    //   * the parsed result is kept in the Outpost's data folder (cache\modeldata.bin), keyed by the game build
    //   * a table with a layout this version does not know is unavailable on its own (a reason, never a guess)
    // The creature's display ids come from what the client saw (SeenData: creaturecache.wdb keeps the FIRST display only), else the Classic baseline
    // (creature_template DisplayId1..4 with their chances). Nothing here touches the network. Nothing starts the service yet: the viewer (step 3) will,
    // on the same off switch as the item data.
    // ============================================================================================================================
    public static class ModelTables
    {
        static SpellTables.Def D(string name, int fileId, uint table, uint layout) => new SpellTables.Def { Name = name, FileId = fileId, TableHash = table, Layouts = new[] { layout }, Build = "1.60.1.70707" };

        // Source: wowdev/WoWDBDefs definitions/*.dbd, the LAYOUT blocks that list 1.60.1.70707 (WoW Forever), checked against the real files of that build
        // (table hash, layout hash and field count read from the WDC5 headers: 28 and 34 fields). Fields counted as Wdc5 does: a $noninline$ column is
        // not a field, an array is one field.
        // CreatureDisplayInfo (layout 7275F5F6, the id is stored): ID(0) ModelID(1) SoundID(2) SizeClass(3) CreatureModelScale(4) CreatureModelAlpha(5) BloodID(6)
        // ExtendedDisplayInfoID(7) NPCSoundID(8) ... ConditionalCreatureModelID(24) MountMaxBankingAngle(25) Field_11_0_0_54210_026(26) TextureVariationFileDataID[4](27)
        public static readonly SpellTables.Def DisplayInfo = D("CreatureDisplayInfo", 1108759, 0xBFDAF9F1, 0x7275F5F6);
        public const int DiModel = 1, DiScale = 4, DiExtended = 7, DiTextures = 27, TextureSlots = 4;
        // CreatureModelData (layout 0F5449F8, the id is not stored): GeoBox[6](0) Flags(1) FileDataID(2) WalkSpeed(3) ... CreatureGeosetDataID(18) HoverHeight(19)
        // AttachedEffectScale(20) ModelScale(21) ... MountScaleOther[2](33)
        public static readonly SpellTables.Def ModelInfo = D("CreatureModelData", 1365368, 0x7F695FA5, 0x0F5449F8);
        public const int CmFlags = 1, CmFile = 2, CmScale = 21;

        public static readonly SpellTables.Def[] All = { DisplayInfo, ModelInfo };
    }

    // one CreatureDisplayInfo row: Textures = TextureVariationFileDataID[0..3], the skins a model's replaceable textures 11 / 12 / 13 take (0 = none)
    public sealed class CreatureDisplay { public int Id, ModelId, ExtendedId; public float Scale; public int[] Textures = new int[ModelTables.TextureSlots]; }
    // one CreatureModelData row
    public sealed class CreatureModelInfo { public int Id, FileId, Flags; public float Scale; }

    public sealed class ModelSet
    {
        public Dictionary<int, CreatureDisplay> Displays;          // null = the table is unavailable (Problems says why)
        public Dictionary<int, CreatureModelInfo> Models;
        public readonly Dictionary<string, string> Problems = new Dictionary<string, string>();     // table name (or "all") -> why it is unavailable
        public readonly List<string> Notes = new List<string>();
        public readonly Dictionary<string, uint> LayoutsSeen = new Dictionary<string, uint>();

        public static ModelSet Parse(SpellRaw raw)
        {
            var s = new ModelSet();
            var di = SpellTables.Open(raw.Get(ModelTables.DisplayInfo), ModelTables.DisplayInfo);
            if (di.Layout != 0) s.LayoutsSeen[ModelTables.DisplayInfo.Name] = di.Layout;
            if (!di.Ok) s.Problems[ModelTables.DisplayInfo.Name] = di.Problem;
            else
            {
                var t = di.Table; s.Displays = new Dictionary<int, CreatureDisplay>(t.Count);
                for (int i = 0; i < t.Count; i++)
                {
                    var d = new CreatureDisplay { Id = t.IdAt(i), ModelId = SpellTables.Int(t, i, ModelTables.DiModel), Scale = SpellTables.Flt(t, i, ModelTables.DiScale), ExtendedId = SpellTables.Int(t, i, ModelTables.DiExtended) };
                    for (int k = 0; k < ModelTables.TextureSlots; k++) d.Textures[k] = SpellTables.IntArr(t, i, ModelTables.DiTextures, k, ModelTables.TextureSlots);
                    s.Displays[d.Id] = d;
                }
                if (t.EncryptedRecords > 0) s.Notes.Add(ModelTables.DisplayInfo.Name + ": " + t.EncryptedRecords + " rows are key-locked and not there");
            }
            var mi = SpellTables.Open(raw.Get(ModelTables.ModelInfo), ModelTables.ModelInfo);
            if (mi.Layout != 0) s.LayoutsSeen[ModelTables.ModelInfo.Name] = mi.Layout;
            if (!mi.Ok) s.Problems[ModelTables.ModelInfo.Name] = mi.Problem;
            else
            {
                var t = mi.Table; s.Models = new Dictionary<int, CreatureModelInfo>(t.Count);
                for (int i = 0; i < t.Count; i++)
                    s.Models[t.IdAt(i)] = new CreatureModelInfo { Id = t.IdAt(i), Flags = SpellTables.Int(t, i, ModelTables.CmFlags), FileId = SpellTables.Int(t, i, ModelTables.CmFile), Scale = SpellTables.Flt(t, i, ModelTables.CmScale) };
                if (t.EncryptedRecords > 0) s.Notes.Add(ModelTables.ModelInfo.Name + ": " + t.EncryptedRecords + " rows are key-locked and not there");
            }
            if (s.Displays == null && s.Models == null) s.Problems["all"] = s.Problems.Values.FirstOrDefault() ?? "no model table could be read";
            return s;
        }
    }

    // one display id a creature may wear: From = "seen" (the client's creaturecache.wdb) or "classic" (the Classic baseline); Chance in percent (0 = none given)
    public sealed class NpcDisplay { public int DisplayId, Chance; public string From; public override string ToString() => DisplayId + " (" + From + (Chance > 0 ? ", " + Chance + "%" : "") + ")"; }

    // what ModelData.ForNpc found: the model to show, or why there is none
    public sealed class NpcModel
    {
        public int Entry;
        public List<NpcDisplay> Considered = new List<NpcDisplay>();
        public int DisplayId, ModelId, ModelFileId;
        public int[] Textures = new int[ModelTables.TextureSlots];
        public float Scale;                   // CreatureDisplayInfo.CreatureModelScale x CreatureModelData.ModelScale
        public bool Humanoid;                 // ExtendedDisplayInfoID != 0: a character model (HD body + bake + items), not shown yet
        public string Reason;                 // why nothing can be shown (null = Ok)
        public string From;                   // where the chosen display id came from
        public bool Ok => Reason == null && ModelFileId > 0;
        public int TextureCount => Textures.Count(x => x > 0);
    }

    // 3.6 step 5: one look of a creature the model card can switch to - a display that resolves to a model that can be shown (not a character model)
    public sealed class NpcLook
    {
        public int DisplayId, Chance;         // Chance in percent; 0 = none given (then no look of the creature has one)
        public NpcModel Model;
        public override string ToString() => DisplayId + " (" + Chance + "%, file " + (Model?.ModelFileId ?? 0) + ")";
    }

    public static class ModelData
    {
        public enum State { NotStarted, Disabled, Loading, Ready, Unavailable }

        public const int FormatVersion = ModelIndexFile.FormatVersion;
        public const string HumanoidReason = "character model, not yet shown";

        public static bool AutoStart = true;         // off in the self-test (it must not read the real game)
        public static string CacheFile = Path.Combine(Util.DataDir, "cache", "modeldata.bin");
        public static event Action Changed;          // on a worker thread, whenever the data or the state changed

        static readonly object gate = new object();
        static volatile ModelSet data;
        static volatile State state = State.NotStarted;
        static string reason, builtFor;
        static bool running, again;
        public static long LastLoadMs { get; private set; }
        public static string LastLoadFrom { get; private set; }
        public static string Build { get; private set; }
        public static bool Stale { get; private set; }

        public static State Status => state;
        public static string Reason => reason;
        public static ModelSet Data => data;
        public static bool Ready => state == State.Ready && data != null;

        // ---------------------------------------------------------------------------------------------------------------- the lookups
        // the display ids an npc may wear: the client's own first (it saw this creature), else the Classic baseline's (empty = none known)
        public static List<NpcDisplay> DisplaysOf(int entry)
        {
            var l = new List<NpcDisplay>();
            int seen = SeenData.Npc(entry)?.DisplayId ?? 0;
            if (seen > 0) { l.Add(new NpcDisplay { DisplayId = seen, Chance = 0, From = "seen" }); return l; }
            foreach (var kv in ClassicAtlas.Displays(entry)) l.Add(new NpcDisplay { DisplayId = kv.Key, Chance = kv.Value, From = "classic" });
            return l;
        }

        public static NpcModel ForNpc(int entry)
        {
            var considered = DisplaysOf(entry);
            var m = Resolve(data, considered, entry);
            if (considered.Count == 0)
            {
                int fmt = ClassicAtlas.Current?.Provenance?.FormatVersion ?? 0;
                m.Reason = "no display id is known for this creature (not met in the game" + (fmt > 0 && fmt < 3 ? ", and the Classic baseline index is older than format 3" : ClassicAtlas.Current == null ? ", and no Classic baseline" : ", and not in the Classic baseline") + ")";
            }
            return m;
        }

        // 3.6 step 5: the looks of a creature, in the order of its display ids (DisplayId1..4): each display that resolves to a model that can be shown.
        // Left out: a character model (humanoid), a display or model the game lacks, and - when any display has a chance - one with a chance of 0 (the
        // game never picks it). The same display twice is one look with the chances added (the game's roll counts both).
        public static List<NpcLook> LooksOf(int entry) => Looks(data, DisplaysOf(entry), entry);

        public static List<NpcLook> Looks(ModelSet s, List<NpcDisplay> considered, int entry)
        {
            var l = new List<NpcLook>();
            if (considered == null || considered.Count == 0) return l;
            bool anyChance = considered.Any(d => d.Chance > 0);
            foreach (var d in considered)
            {
                if (anyChance && d.Chance <= 0) continue;
                var same = l.FirstOrDefault(x => x.DisplayId == d.DisplayId);
                if (same != null) { same.Chance += d.Chance; continue; }
                var m = Resolve(s, new List<NpcDisplay> { d }, entry);
                if (!m.Ok) continue;
                l.Add(new NpcLook { DisplayId = d.DisplayId, Chance = d.Chance, Model = m });
            }
            return l;
        }

        // the look shown first: the highest chance (equal chances keep the order), as Resolve picks; -1 = none
        public static int DefaultLook(List<NpcLook> looks)
        {
            int best = -1;
            for (int i = 0; i < (looks?.Count ?? 0); i++) if (best < 0 || looks[i].Chance > looks[best].Chance) best = i;
            return best;
        }

        // one display id alone (the real-file checks, a later item / pet viewer)
        public static NpcModel ForDisplay(int displayId) => Resolve(data, new List<NpcDisplay> { new NpcDisplay { DisplayId = displayId, From = "asked" } }, 0);

        // The choice: the highest chance first (equal chances keep the order given), the first display the game's CreatureDisplayInfo knows wins.
        // A table that is unavailable gives its reason; nothing is guessed.
        public static NpcModel Resolve(ModelSet s, List<NpcDisplay> considered, int entry)
        {
            var m = new NpcModel { Entry = entry, Considered = considered ?? new List<NpcDisplay>() };
            if (m.Considered.Count == 0) { m.Reason = "no display id is known for this creature"; return m; }
            if (s == null) { m.Reason = "the model tables are not read" + (string.IsNullOrEmpty(reason) ? "" : " (" + reason + ")"); return m; }
            if (s.Displays == null) { m.Reason = s.Problems.TryGetValue(ModelTables.DisplayInfo.Name, out var w) ? w : "CreatureDisplayInfo is unavailable"; return m; }
            var order = m.Considered.Select((d, i) => (d, i)).OrderByDescending(x => x.d.Chance).ThenBy(x => x.i).Select(x => x.d).ToList();
            CreatureDisplay cd = null; NpcDisplay pick = null;
            foreach (var d in order) if (s.Displays.TryGetValue(d.DisplayId, out cd)) { pick = d; break; }
            if (pick == null) { m.DisplayId = order[0].DisplayId; m.Reason = "display " + string.Join(", ", order.Select(d => d.DisplayId)) + (order.Count == 1 ? " is" : " are") + " not in the game's CreatureDisplayInfo"; return m; }
            m.DisplayId = pick.DisplayId; m.From = pick.From; m.ModelId = cd.ModelId; m.Humanoid = cd.ExtendedId != 0;
            Array.Copy(cd.Textures, m.Textures, ModelTables.TextureSlots);
            if (s.Models == null) { m.Reason = s.Problems.TryGetValue(ModelTables.ModelInfo.Name, out var w) ? w : "CreatureModelData is unavailable"; return m; }
            if (!s.Models.TryGetValue(cd.ModelId, out var md)) { m.Reason = "model " + cd.ModelId + " of display " + cd.Id + " is not in the game's CreatureModelData"; return m; }
            m.ModelFileId = md.FileId; m.Scale = cd.Scale * md.Scale;
            if (md.FileId <= 0) { m.Reason = "model " + cd.ModelId + " has no model file"; return m; }
            if (m.Humanoid) m.Reason = HumanoidReason;
            return m;
        }

        // ---------------------------------------------------------------------------------------------------------------- the service
        static bool SafeEnabled() { try { return ItemData.Enabled(); } catch { return true; } }

        public static void Start()
        {
            if (!AutoStart) return;
            if (!SafeEnabled()) { Disable(); return; }
            lock (gate)
            {
                again = true;
                if (running) return;
                running = true;
            }
            if (data == null) SetState(State.Loading, null);
            Task.Factory.StartNew(Worker, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        public static void Disable()
        {
            data = null; Build = null; builtFor = null;
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
                    GameDataProgress.Begin(JobId, "Creature models", "Loading the creature models", 0.1);
                    LoadOnce();
                }
                catch (Exception e)
                {
                    Util.Log("model data: " + e);
                    Publish(null, State.Unavailable, "could not be read: " + e.Message, null);
                }
                LastLoadMs = sw.ElapsedMilliseconds;
                GameDataProgress.End(JobId, state == State.Ready, state == State.Ready ? null : reason);
            }
        }

        static void Publish(ModelSet s, State st, string why, string build)
        {
            lock (gate) { data = st == State.Ready ? s : null; reason = why; state = st; Build = st == State.Ready ? build : null; builtFor = Build; }
            try { Changed?.Invoke(); } catch { }
        }

        static void SetState(State st, string why)
        {
            lock (gate) { state = st; reason = why; }
            try { Changed?.Invoke(); } catch { }
        }

        public static void SetForTest(ModelSet s, State st = State.Ready, string why = null)
        {
            lock (gate) { data = st == State.Ready ? s : null; state = st; reason = why; Build = st == State.Ready ? "test" : null; builtFor = Build; }
        }
        // 3.6 step 3: as the worker does when it finishes (the NPC page's model card waits for it)
        public static void RaiseChangedForTest() { try { Changed?.Invoke(); } catch { } }

        public static void ResetForTest()
        {
            lock (gate) { data = null; state = State.NotStarted; reason = null; running = false; again = false; Build = null; builtFor = null; Stale = false; }
        }

        static void LoadOnce()
        {
            var root = ItemData.WowRoot();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) { Publish(null, State.Unavailable, "the WoW folder is not set", null); return; }
            var flavor = ItemData.Flavor() ?? "_classic_beta_";
            var build = ItemData.GameBuild(root, flavor);
            if (build == null) { Publish(null, State.Unavailable, "no .build.info in the WoW folder", null); return; }
            if (state == State.Ready && data != null && builtFor == build) return;
            var saved = ModelIndexFile.TryRead(CacheFile);
            // the game was patched: the models of the previous build are used at once while the new game files are read in the background
            bool old = data == null && saved != null && saved.Build != build && !saved.Set.Problems.ContainsKey("all");
            if (old) { LastLoadFrom = "cache file (previous game build)"; Stale = true; Publish(saved.Set, State.Ready, null, saved.Build); }
            var set = Obtain(saved, build, CacheFile, CascShared.OneAtATime(JobId, () =>
            {
                GameDataProgress.Rebuilding(JobId, GameDataProgress.ReasonFor(CacheFile, saved?.Build, build), "Reading creature models", 0.3, "tables", old);
                try { return ModelSet.Parse(ReadCasc(root, flavor, (n, t) => GameDataProgress.Report(JobId, n, t, 0, 0.8))); }
                catch (Exception e) { Util.Log("model data: game files: " + e.Message); var f = new ModelSet(); f.Problems["all"] = "the game files could not be read: " + e.Message; return f; }
            }), out var from);
            if (set.Problems.ContainsKey("all") && old) { Util.Log("model data: the new game files could not be read (" + set.Problems["all"] + "); the models of the previous game build stay"); return; }
            if (set.Problems.ContainsKey("all")) { Util.Log("model data: unavailable: " + set.Problems["all"]); Publish(null, State.Unavailable, set.Problems["all"], null); return; }
            foreach (var kv in set.Problems) Util.Log("model data: " + kv.Key + ": " + kv.Value);
            foreach (var n in set.Notes) Util.Log("model data: " + n);
            LastLoadFrom = from; Stale = false;
            Publish(set, State.Ready, null, build);
        }

        // the saved index when it is of this game build, otherwise the game files (which are then saved)
        public static ModelSet Obtain(ModelIndexFile.Saved saved, string build, string cacheFile, Func<ModelSet> readGame, out string from)
        {
            if (saved != null && saved.Build == build && saved.Format == FormatVersion) { from = "cache file"; return saved.Set; }
            var fresh = readGame();
            if (!fresh.Problems.ContainsKey("all")) ModelIndexFile.TryWrite(cacheFile, build, fresh);
            from = "game files";
            return fresh;
        }

        public const string JobId = "models";

        public static SpellRaw ReadCasc(string root, string flavor, Action<int, int> progress = null)
        {
            var raw = new SpellRaw();
            using (var lease = CascShared.Open(root, flavor))     // shared with the other readers; key-locked parts read as zeros (the table reader skips those sections)
            {
                var casc = lease.Casc;
                var all = ModelTables.All;
                var keys = casc.ResolveFileIds(new HashSet<int>(all.Select(d => d.FileId)));
                int i = 0;
                foreach (var def in all)
                {
                    progress?.Invoke(i++, all.Length);
                    try { if (keys.TryGetValue(def.FileId, out var ck)) raw.Tables[def.Name] = casc.ReadByContentKey(ck); }
                    catch (Exception e) { Util.Log("model data: " + def.Name + ".db2: " + e.Message); }
                }
            }
            return raw;
        }
    }

    // ============================================================================================================================
    // the compact copy of the two tables in the Outpost's data folder (gzip of a small binary format), keyed by the game build
    // ============================================================================================================================
    public static class ModelIndexFile
    {
        public sealed class Saved { public int Format; public string Build; public ModelSet Set; }
        public const int FormatVersion = 1;       // bump when the parsing, the table layouts or the cache file change meaning
        const int Magic = 0x4C444D45;       // "EMDL"

        public static bool TryWrite(string path, string build, ModelSet set)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var tmp = path + ".tmp";
                var mem = new MemoryStream(1 << 20);
                using (var w = new BinaryWriter(mem, Encoding.UTF8, true))
                {
                    w.Write(Magic); w.Write(FormatVersion); w.Write(build ?? "");
                    w.Write(set.Problems.Count); foreach (var kv in set.Problems) { w.Write(kv.Key); w.Write(kv.Value ?? ""); }
                    w.Write(set.Notes.Count); foreach (var n in set.Notes) w.Write(n);
                    w.Write(set.LayoutsSeen.Count); foreach (var kv in set.LayoutsSeen) { w.Write(kv.Key); w.Write(kv.Value); }
                    w.Write(set.Displays != null);
                    if (set.Displays != null)
                    {
                        w.Write(set.Displays.Count);
                        foreach (var d in set.Displays.Values) { w.Write(d.Id); w.Write(d.ModelId); w.Write(d.ExtendedId); w.Write(d.Scale); foreach (var t in d.Textures) w.Write(t); }
                    }
                    w.Write(set.Models != null);
                    if (set.Models != null)
                    {
                        w.Write(set.Models.Count);
                        foreach (var m in set.Models.Values) { w.Write(m.Id); w.Write(m.FileId); w.Write(m.Flags); w.Write(m.Scale); }
                    }
                }
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var gz = new GZipStream(fs, CompressionMode.Compress))
                    gz.Write(mem.GetBuffer(), 0, (int)mem.Length);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                return true;
            }
            catch (Exception e) { Util.Log("model data: cannot save the index: " + e.Message); return false; }
        }

        // null for a missing, damaged, foreign or older file (the caller then reads the game files)
        public static Saved TryRead(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var mem = new MemoryStream(1 << 20);
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var gz = new GZipStream(fs, CompressionMode.Decompress))
                    gz.CopyTo(mem);
                mem.Position = 0;
                using (var r = new BinaryReader(mem, Encoding.UTF8))
                {
                    if (r.ReadInt32() != Magic) return null;
                    var s = new Saved { Format = r.ReadInt32(), Build = r.ReadString(), Set = new ModelSet() };
                    if (s.Format != FormatVersion) return null;
                    int n = r.ReadInt32(); Count(n, 1000);
                    for (int i = 0; i < n; i++) s.Set.Problems[r.ReadString()] = r.ReadString();
                    n = r.ReadInt32(); Count(n, 100000);
                    for (int i = 0; i < n; i++) s.Set.Notes.Add(r.ReadString());
                    n = r.ReadInt32(); Count(n, 1000);
                    for (int i = 0; i < n; i++) s.Set.LayoutsSeen[r.ReadString()] = r.ReadUInt32();
                    if (r.ReadBoolean())
                    {
                        n = r.ReadInt32(); Count(n, 5000000);
                        s.Set.Displays = new Dictionary<int, CreatureDisplay>(n);
                        for (int i = 0; i < n; i++)
                        {
                            var d = new CreatureDisplay { Id = r.ReadInt32(), ModelId = r.ReadInt32(), ExtendedId = r.ReadInt32(), Scale = r.ReadSingle() };
                            for (int k = 0; k < ModelTables.TextureSlots; k++) d.Textures[k] = r.ReadInt32();
                            s.Set.Displays[d.Id] = d;
                        }
                    }
                    if (r.ReadBoolean())
                    {
                        n = r.ReadInt32(); Count(n, 5000000);
                        s.Set.Models = new Dictionary<int, CreatureModelInfo>(n);
                        for (int i = 0; i < n; i++) { var m = new CreatureModelInfo { Id = r.ReadInt32(), FileId = r.ReadInt32(), Flags = r.ReadInt32(), Scale = r.ReadSingle() }; s.Set.Models[m.Id] = m; }
                    }
                    return s;
                }
            }
            catch (Exception) { return null; }
        }

        static void Count(int n, int max) { if (n < 0 || n > max) throw new InvalidDataException("count"); }
    }
}
