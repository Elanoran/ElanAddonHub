using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ElansAddonHub.Services.Talents;

namespace ElansAddonHub.Services.Db2
{
    // ============================================================================================================================
    // Atlas layer G: the talent trees of every class, read from the player's OWN WoW install (the Trait* tables of the client), read-only, in the
    // background, exactly like SpellData / MapData: the tables come out of the local CASC storage (TalentTables lists them and the layouts this version
    // knows), the parsed result is kept in the Outpost's data folder (cache\talentdata.bin) keyed by the game build, and a table with a layout this
    // version does not know makes the layer (or, for the optional ones, just that feature) unavailable - nothing is guessed. Same off switch as the item
    // data ("Read item data from my WoW installation"): the service follows ItemData's state changes, so no Settings code was touched.
    // Spell names, icons and descriptions are NOT stored here: they come from SpellData at the time they are shown.
    // ============================================================================================================================
    public static class TalentData
    {
        public enum State { NotStarted, Disabled, Loading, Ready, Unavailable }

        public const int FormatVersion = TalentIndexFile.FormatVersion;          // bump when the parsing, the table layouts or the cache file change meaning

        public static bool AutoStart = true;         // off in the self-test (it must not read the real game)
        public static string CacheFile = Path.Combine(Util.DataDir, "cache", "talentdata.bin");
        public static event Action Changed;          // on a worker thread, whenever the data or the state changed

        static readonly object gate = new object();
        static volatile TalentSet data;
        static volatile State state = State.NotStarted;
        static string reason, builtFor;
        static bool running, again, hooked;
        public static long LastLoadMs { get; private set; }
        public static string LastLoadFrom { get; private set; }
        public static string Build { get; private set; }
        public static int StartCountForTest;

        public static State Status => state;
        public static string Reason => reason;
        public static TalentSet Data => data;
        public static bool Ready => state == State.Ready && data != null;

        public static TalentTreeDef ForClass(int classId) => data?.ForClass(classId);
        public static TalentTreeDef ForClassFile(string file) => data?.ForClassFile(file);

        // the display name of a node: its spell's name (needs the spell data), else the definition's own name, else null
        public static string NodeName(TalentNode n)
        {
            var e = n?.Entry;
            if (e == null) return null;
            // the spell's name first: that is the name the game's tooltip and Hunter Helper's builds use; the definition's own name when the spell has none
            var sn = e.SpellId > 0 ? SpellData.Spell(e.SpellId)?.Name : null;
            return !string.IsNullOrEmpty(sn) ? sn : string.IsNullOrEmpty(e.NameOverride) ? null : e.NameOverride;
        }

        public static string StatusExtra
        {
            get
            {
                var d = data;
                switch (state)
                {
                    case State.Ready: return d == null ? "" : ", " + d.Trees.Count + " talent trees";
                    case State.Unavailable: return ", talent trees unavailable" + (string.IsNullOrEmpty(reason) ? "" : " (" + reason + ")");
                    default: return "";
                }
            }
        }

        // a spell shown at a rank of a talent: the spell's description with the amounts that change per rank taken from the definition's curves
        public static SpellView SpellAtRank(TalentEntry e, int rank)
        {
            if (e == null || e.SpellId <= 0) return null;
            Dictionary<int, float> over = null;
            if (rank > 0 && e.Curves.Count > 0)
            {
                over = new Dictionary<int, float>();
                foreach (var c in e.Curves)
                    if (c.ByRank.Length > 0) over[c.EffectIndex] = c.ByRank[Math.Min(rank, c.ByRank.Length) - 1];
            }
            return SpellData.Spell(e.SpellId, over);
        }

        // ---------------------------------------------------------------------------------------------------------------- the service
        static bool SafeEnabled() { try { return ItemData.Enabled(); } catch { return true; } }

        // follow the item data's switch: when it is turned off the trees go; when it comes back (or the game folder changes) they are read again
        public static void Hook()
        {
            if (hooked) return;
            hooked = true;
            ItemData.Changed += () =>
            {
                try
                {
                    if (!SafeEnabled()) { if (state != State.Disabled && state != State.NotStarted) Disable(); }
                    else if (state == State.Disabled) Start();
                }
                catch { }
            };
        }

        public static void Start()
        {
            Interlocked.Increment(ref StartCountForTest);
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
                    GameDataProgress.Begin(JobId, "Talent trees", "Loading the talent trees", 0.2);
                    LoadOnce();
                }
                catch (Exception e)
                {
                    Util.Log("talent data: " + e);
                    Publish(null, State.Unavailable, "could not be read: " + e.Message, null);
                }
                LastLoadMs = sw.ElapsedMilliseconds;
                GameDataProgress.End(JobId, state == State.Ready, state == State.Ready ? null : reason);
            }
        }

        static void Publish(TalentSet s, State st, string why, string build)
        {
            lock (gate) { data = st == State.Ready ? s : null; reason = why; state = st; Build = st == State.Ready ? build : null; builtFor = Build; }
            try { Changed?.Invoke(); } catch { }
        }

        static void SetState(State st, string why)
        {
            lock (gate) { state = st; reason = why; }
            try { Changed?.Invoke(); } catch { }
        }

        public static void SetForTest(TalentSet s, State st = State.Ready, string why = null)
        {
            lock (gate) { data = s; state = st; reason = why; Build = st == State.Ready ? "test" : null; builtFor = Build; }
            try { Changed?.Invoke(); } catch { }
        }

        public static void ResetForTest()
        { Stale = false;
            lock (gate) { data = null; state = State.NotStarted; reason = null; running = false; again = false; Build = null; builtFor = null; }
        }

        static void LoadOnce()
        {
            var root = ItemData.WowRoot();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) { Publish(null, State.Unavailable, "the WoW folder is not set", null); return; }
            var flavor = ItemData.Flavor() ?? "_classic_beta_";
            var build = ItemData.GameBuild(root, flavor);
            if (build == null) { Publish(null, State.Unavailable, "no .build.info in the WoW folder", null); return; }
            if (state == State.Ready && data != null && builtFor == build) return;
            var saved = TalentIndexFile.TryRead(CacheFile);
            // the game was patched: the talent trees of the previous build are shown at once while the new game files are read in the background
            bool old = data == null && saved != null && saved.Build != build && saved.Format == FormatVersion && !saved.Set.Problems.ContainsKey("all") && saved.Set.Trees.Count > 0;
            if (old) { LastLoadFrom = "cache file (previous game build)"; Stale = true; Publish(saved.Set, State.Ready, null, saved.Build); }
            var set = Obtain(saved, build, CacheFile, CascShared.OneAtATime(JobId, () =>
            {
                GameDataProgress.Rebuilding(JobId, GameDataProgress.ReasonFor(CacheFile, saved?.Build, build), "Reading talent trees", 0.6, "tables", old);
                TalentRaw raw;
                try { raw = ReadCasc(root, flavor, (n, t) => GameDataProgress.Report(JobId, n, t, 0, 0.6)); GameDataProgress.Step(JobId, "Sorting the talent trees", 0.6); }
                catch (Exception e) { Util.Log("talent data: game files: " + e.Message); var f = new TalentSet(); f.Problems["all"] = "the game files could not be read: " + e.Message; return f; }
                return TalentParse.All(raw);
            }), out var from);
            if (set.Problems.ContainsKey("all") && old) { Util.Log("talent data: the new game files could not be read (" + set.Problems["all"] + "); the talent trees of the previous game build stay"); return; }
            if (set.Problems.ContainsKey("all")) { Util.Log("talent data: unavailable: " + set.Problems["all"]); Publish(null, State.Unavailable, set.Problems["all"], null); return; }
            foreach (var kv in set.Problems) Util.Log("talent data: " + kv.Key + ": " + kv.Value);
            foreach (var n in set.Notes) Util.Log("talent data: " + n);
            if (set.Trees.Count == 0) { Publish(null, State.Unavailable, set.Problems.Values.FirstOrDefault() ?? "no talent tree was found", null); return; }
            if (from == "game files" && saved != null && saved.Build != build && saved.Format == FormatVersion && saved.Set.Trees.Count > 0) GameBuilds.Compare("talents", saved.Build, build, saved.Set, set, GameBuilds.DiffTalents);     // a patch: what it changed (Settings > About > Game patches)
            LastLoadFrom = from; Stale = false;
            Publish(set, State.Ready, null, build);
        }

        // the saved index when it is of this game build, otherwise the game files (which are then saved)
        public static TalentSet Obtain(TalentIndexFile.Saved saved, string build, string cacheFile, Func<TalentSet> readGame, out string from)
        {
            if (saved != null && saved.Build == build && saved.Format == FormatVersion) { from = "cache file"; return saved.Set; }
            var fresh = readGame();
            if (!fresh.Problems.ContainsKey("all") && fresh.Trees.Count > 0) TalentIndexFile.TryWrite(cacheFile, build, fresh);
            from = "game files";
            return fresh;
        }

        public const string JobId = "talents";

        // the data shown is of the previous game build (the new one is being read)
        public static bool Stale { get; private set; }

        public static TalentRaw ReadCasc(string root, string flavor, Action<int, int> progress = null)
        {
            var raw = new TalentRaw();
            using (var lease = CascShared.Open(root, flavor))     // shared with the other readers; key-locked parts read as zeros
            {
                var casc = lease.Casc;
                var all = TalentTables.All;
                var keys = casc.ResolveFileIds(new HashSet<int>(all.Select(d => d.FileId)));
                int i = 0;
                foreach (var def in all)
                {
                    progress?.Invoke(i++, all.Length);
                    try { if (keys.TryGetValue(def.FileId, out var ck)) raw.Tables[def.Name] = casc.ReadByContentKey(ck); }
                    catch (Exception e) { Util.Log("talent data: " + def.Name + ".db2: " + e.Message); }
                }
            }
            return raw;
        }
    }

}
