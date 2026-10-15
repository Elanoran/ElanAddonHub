using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ElansAddonHub.Services.Db2
{
    // ============================================================================================================================
    // Atlas: classes, races and the per-spell facts (level learned at, cost, cooldown), read from the player's OWN WoW install, read-only, in the background,
    // exactly like SpellData / TalentData: the tables come out of the local CASC storage (ClassTables lists them and the layouts this version knows), the parsed
    // result is kept in the Outpost's data folder (cache\classdata.bin) keyed by the game build, a table with a layout this version does not know makes that
    // table (or, for the three required ones, the layer) unavailable - nothing is guessed. Same off switch as the item data ("Read item data from my WoW
    // installation"): the service follows ItemData's state changes. Spell names, icons and descriptions are NOT stored here: they come from SpellData.
    // ============================================================================================================================
    public static class ClassData
    {
        public enum State { NotStarted, Disabled, Loading, Ready, Unavailable }

        public const int FormatVersion = ClassIndexFile.FormatVersion;

        public static bool AutoStart = true;         // off in the self-test (it must not read the real game)
        public static string CacheFile = Path.Combine(Util.DataDir, "cache", "classdata.bin");
        public static event Action Changed;          // on a worker thread, whenever the data or the state changed

        static readonly object gate = new object();
        static volatile ClassSet data;
        static volatile State state = State.NotStarted;
        static string reason, builtFor;
        static bool running, again, hooked;
        public static long LastLoadMs { get; private set; }
        public static string LastLoadFrom { get; private set; }
        public static string Build { get; private set; }
        public static int StartCountForTest;

        public static State Status => state;
        public static string Reason => reason;
        public static ClassSet Set => data;
        public static ClassSnapshot Data => data?.Data;
        public static bool Ready => state == State.Ready && data != null;

        // the lookups the spell pages use (null / 0 when the table is not read)
        public static string CostText(int spellId) => Data?.CostText(spellId);
        public static int CooldownMs(int spellId) => Data?.CooldownMs(spellId) ?? 0;

        public static string StatusExtra
        {
            get
            {
                var d = data;
                switch (state)
                {
                    case State.Ready: return d == null ? "" : ", " + d.Data.Classes.Count + " classes, " + d.Data.Races.Values.Count(r => r.Playable) + " races";
                    case State.Unavailable: return ", class and race data unavailable" + (string.IsNullOrEmpty(reason) ? "" : " (" + reason + ")");
                    default: return "";
                }
            }
        }

        // ---------------------------------------------------------------------------------------------------------------- the service
        static bool SafeEnabled() { try { return ItemData.Enabled(); } catch { return true; } }

        // follow the item data's switch: when it is turned off the data goes; when it comes back (or the game folder changes) it is read again
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
                    LoadOnce();
                }
                catch (Exception e)
                {
                    Util.Log("class data: " + e);
                    Publish(null, State.Unavailable, "could not be read: " + e.Message, null);
                }
                LastLoadMs = sw.ElapsedMilliseconds;
            }
        }

        static void Publish(ClassSet s, State st, string why, string build)
        {
            lock (gate) { data = st == State.Ready ? s : null; reason = why; state = st; Build = st == State.Ready ? build : null; builtFor = Build; }
            try { Changed?.Invoke(); } catch { }
        }

        static void SetState(State st, string why)
        {
            lock (gate) { state = st; reason = why; }
            try { Changed?.Invoke(); } catch { }
        }

        public static void SetForTest(ClassSet s, State st = State.Ready, string why = null)
        {
            lock (gate) { data = s; state = st; reason = why; Build = st == State.Ready ? "test" : null; builtFor = Build; }
            try { Changed?.Invoke(); } catch { }
        }

        public static void ResetForTest()
        {
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
            var set = Obtain(ClassIndexFile.TryRead(CacheFile), build, CacheFile, () =>
            {
                ClassRaw raw;
                try { raw = ReadCasc(root, flavor); }
                catch (Exception e) { Util.Log("class data: game files: " + e.Message); var f = new ClassSet(); f.Problems["all"] = "the game files could not be read: " + e.Message; return f; }
                return ClassParse.All(raw);
            }, out var from);
            if (set.Problems.ContainsKey("all")) { Util.Log("class data: unavailable: " + set.Problems["all"]); Publish(null, State.Unavailable, set.Problems["all"], null); return; }
            foreach (var kv in set.Problems) Util.Log("class data: " + kv.Key + ": " + kv.Value);
            foreach (var n in set.Notes) Util.Log("class data: " + n);
            if (set.Data.Classes.Count == 0) { Publish(null, State.Unavailable, "no class was found", null); return; }
            LastLoadFrom = from;
            Publish(set, State.Ready, null, build);
        }

        // the saved index when it is of this game build, otherwise the game files (which are then saved)
        public static ClassSet Obtain(ClassIndexFile.Saved saved, string build, string cacheFile, Func<ClassSet> readGame, out string from)
        {
            if (saved != null && saved.Build == build && saved.Format == FormatVersion) { from = "cache file"; return saved.Set; }
            var fresh = readGame();
            if (!fresh.Problems.ContainsKey("all") && fresh.Data.Classes.Count > 0) ClassIndexFile.TryWrite(cacheFile, build, fresh);
            from = "game files";
            return fresh;
        }

        public static ClassRaw ReadCasc(string root, string flavor)
        {
            var raw = new ClassRaw();
            using (var casc = CascStorage.Open(root, flavor))
            {
                casc.ZeroFillEncrypted = true;
                var all = ClassTables.All;
                var keys = casc.ResolveFileIds(new HashSet<int>(all.Select(d => d.FileId)));
                foreach (var def in all)
                {
                    try { if (keys.TryGetValue(def.FileId, out var ck)) raw.Tables[def.Name] = casc.ReadByContentKey(ck); }
                    catch (Exception e) { Util.Log("class data: " + def.Name + ".db2: " + e.Message); }
                }
            }
            return raw;
        }
    }
}
