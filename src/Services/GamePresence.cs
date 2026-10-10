using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Threading;

namespace ElansAddonHub.Services
{
    // What you're playing, for the Lodge. WoW only writes SavedVariables on /reload and logout, so the file is
    // never "live"; to avoid showing the wrong character we only trust it when (a) WoW is running, (b) the
    // addon says the character is still online (not logged out) and (c) it was written during this WoW session.
    // Otherwise we say "Playing WoW" without a character. FileSystemWatcher picks up writes immediately,
    // a 5 s poll covers WoW starting/exiting and missed events.
    public class GamePresence
    {
        public class Character
        {
            public string Name, Realm, Class, ClassFile, Race, RaceFile, Zone, Guild, Instance;
            public int Level, Sex;
            public long Updated;
            public bool Online = true; // false = the addon saw a real logout
            // ---- live presence from the strip's second row (companion 1.5+); HasLive = it was there
            public bool HasLive, CombatKnown;
            public int Flags, XpPercent = -1, GroupSize;   // Flags: 1 combat 2 dead 4 AFK 8 resting 16 instance 32 raid inst 64 party inst 128 group
            public bool Rested, InInstance;
            public string InstanceName;
            public Character Copy() => (Character)MemberwiseClone();
            public bool InCombat => (Flags & 1) != 0;
            public bool Dead => (Flags & 2) != 0;
            public bool Afk => (Flags & 4) != 0;
            public bool Resting => (Flags & 8) != 0;
        }

        readonly Func<string> wowRoot;
        readonly Func<bool> pixelOn;
        readonly WowWatch watch;
        public readonly PixelStrip Strip;
        Character held; // last character seen on the strip (kept for a while if the strip disappears)
        readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        readonly DispatcherTimer debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        readonly List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();
        string watchKey = "";
        string lastSig;
        Character last; // newest character the addon has written (whatever its state)

        public bool Playing { get; private set; }
        public Character Current { get; private set; }
        // class of the character you play now, or else the one the addon wrote last (null = unknown)
        public string LastClassFile => (Current ?? last)?.ClassFile;
        public event Action Changed;

        public GamePresence(Func<string> wowRoot, Func<bool> pixelOn = null, WowWatch watch = null)
        {
            this.wowRoot = wowRoot;
            this.watch = watch ?? WowWatch.Shared;
            this.pixelOn = pixelOn ?? (() => true);
            Strip = new PixelStrip(this.pixelOn);
            Strip.Changed += () => System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(Poll));
            timer.Tick += (s, e) => Poll();
            debounce.Tick += (s, e) => { debounce.Stop(); Poll(); };
            this.watch.Changed += running => { ApplyWow(running); Poll(); };
        }

        public void Start() { ApplyWow(watch.Running); Poll(); }

        // the poll timer and the pixel-strip capture only run while WoW does (idle hub = no capture, no 5 s poll)
        public bool Active => timer.IsEnabled;
        void ApplyWow(bool running)
        {
            if (running) { if (!timer.IsEnabled) timer.Start(); Strip.Start(); }
            else { timer.Stop(); Strip.Pause(); }
        }

        // tests: pretend this is what the strip says
        public void SetForTest(Character c, bool playing) { Current = c; Playing = playing; Changed?.Invoke(); }

        public void Poll()
        {
            DateTime started = DateTime.MinValue;
            var playing = watch.Running && WowRunning(out started);

            var files = SavedVariablesFiles(wowRoot());
            Watch(files);
            var sig = string.Join("|", files.Select(f => f + "@" + SafeStamp(f).Ticks));
            if (sig != lastSig)
            {
                lastSig = sig;
                // newest by the addon's own timestamp (file times can be touched by sync tools)
                last = files.Select(Read).Where(c => c != null).OrderByDescending(c => c.Updated).FirstOrDefault();
            }

            Character shown = last;
            if (playing && last != null && !(last.Online && FromThisSession(last, started))) shown = null;
            if (playing && pixelOn()) shown = FromStrip(shown, started);
            else held = null;

            bool changed = playing != Playing || !Same(shown, Current);
            Playing = playing; Current = shown;
            if (changed) Changed?.Invoke();
        }

        // Live data from the addon's pixel strip beats SavedVariables (which only change on /reload and logout).
        // If the strip vanishes (character select, UI hidden, WoW minimised) we hold the character for 45 s, then
        // trust SavedVariables only if the addon wrote them after the strip was last seen.
        Character FromStrip(Character saved, DateTime started)
        {
            var info = Strip.Latest;
            var seen = Strip.LastSeen;
            if (info != null && seen >= started.ToUniversalTime().AddSeconds(-5))
            {
                var age = DateTime.UtcNow - seen;
                if (age < TimeSpan.FromSeconds(12))
                {
                    var match = last != null && string.Equals(last.Name, info.Name, StringComparison.OrdinalIgnoreCase) ? last : null;
                    held = new Character
                    {
                        Name = info.Name, Class = info.ClassName, ClassFile = info.ClassFile, Race = info.RaceName, RaceFile = info.RaceFile,
                        Sex = info.Sex, Level = info.Level, Online = true, Realm = match?.Realm, Guild = match?.Guild,
                        Zone = match != null && FromThisSession(match, started) ? match.Zone : null,
                        Updated = new DateTimeOffset(seen).ToUnixTimeSeconds(),
                    };
                    ApplyLive(held, info);
                    return held;
                }
                if (age < TimeSpan.FromSeconds(45) && held != null)
                {
                    // the strip is gone for the moment (loading screen, UI hidden): keep the character, but never a stale "in combat"
                    if (held.InCombat) { held = held.Copy(); held.Flags &= ~1; }
                    return held;
                }
                held = null;
                if (last != null && last.Online && last.Updated > new DateTimeOffset(seen).ToUnixTimeSeconds()) return last;
                return null; // strip gone for good: probably at character select
            }
            return saved;
        }

        // the strip's second row: live zone (a map name, or the instance's name inside dungeons), flags, XP, group
        public static void ApplyLive(Character c, StripCodec.Info info)
        {
            if (!info.HasV2) return;
            c.HasLive = true;
            c.Flags = info.Flags1; c.CombatKnown = info.CombatKnown; c.GroupSize = info.GroupSize;
            c.XpPercent = info.XpKnown ? info.XpPercent : -1;
            c.Rested = info.Rested;
            c.InInstance = info.InInstance;
            var inst = info.InInstance ? ZoneNames.Instance(info.InstanceId) : null;
            c.InstanceName = inst;
            var map = ZoneNames.Map(info.MapId);
            if (map != null) c.Zone = map;          // a known open-world map beats the (stale) SavedVariables zone
            else if (inst != null) c.Zone = inst;
            else if (info.MapId != 0 || info.InstanceId != 0 || info.InInstance) c.Zone = null; // somewhere we have no name for: show nothing, not a wrong zone
        }

        static bool FromThisSession(Character c, DateTime started)
        {
            if (started == DateTime.MinValue || c.Updated <= 0) return true;
            return DateTimeOffset.FromUnixTimeSeconds(c.Updated).UtcDateTime >= started.ToUniversalTime().AddSeconds(-5);
        }

        static bool Same(Character a, Character b) =>
            ReferenceEquals(a, b) || (a != null && b != null && a.Name == b.Name && a.Realm == b.Realm && a.Online == b.Online && a.Level == b.Level
                && a.ClassFile == b.ClassFile && a.RaceFile == b.RaceFile && a.Sex == b.Sex && a.Zone == b.Zone && a.Guild == b.Guild
                && a.HasLive == b.HasLive && a.Flags == b.Flags && a.XpPercent == b.XpPercent && a.Rested == b.Rested && a.GroupSize == b.GroupSize
                && a.InInstance == b.InInstance && a.InstanceName == b.InstanceName && a.CombatKnown == b.CombatKnown);

        static DateTime SafeStamp(string f) { try { return File.GetLastWriteTimeUtc(f); } catch { return DateTime.MinValue; } }

        void Watch(List<string> files)
        {
            var dirs = files.Select(Path.GetDirectoryName).Distinct().OrderBy(x => x).ToList();
            var key = string.Join("|", dirs);
            if (key == watchKey) return;
            watchKey = key;
            foreach (var w in watchers) w.Dispose();
            watchers.Clear();
            foreach (var d in dirs)
            {
                try
                {
                    var w = new FileSystemWatcher(d, "ElansHub.lua") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName };
                    FileSystemEventHandler h = (s, e) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() => { debounce.Stop(); debounce.Start(); }));
                    w.Changed += h; w.Created += h;
                    w.EnableRaisingEvents = true;
                    watchers.Add(w);
                }
                catch { /* the poll still covers it */ }
            }
        }

        public static bool WowRunningNow() => WowRunning(out _);

        // tests only (needs a test data folder): act as if WoW never runs, so the idle-CPU comparison measures the timers and not WoW itself
        public static readonly bool IgnoreWow = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ELANSHUB_DATA")) && Environment.GetEnvironmentVariable("ELANSHUB_IGNORE_WOW") == "1";

        static bool WowRunning(out DateTime started)
        {
            started = DateTime.MinValue;
            bool any = false;
            try
            {
                foreach (var p in Process.GetProcesses())
                {
                    try
                    {
                        var n = p.ProcessName;
                        if (!IgnoreWow && n.StartsWith("Wow", StringComparison.OrdinalIgnoreCase)
                            && n.IndexOf("Voice", StringComparison.OrdinalIgnoreCase) < 0
                            && n.IndexOf("Error", StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            any = true;
                            try { if (p.StartTime > started) started = p.StartTime; } catch { }
                        }
                    }
                    catch { }
                    finally { p.Dispose(); }
                }
            }
            catch { return false; }
            return any;
        }

        // <root>\_<flavor>_\WTF\Account\<ACCOUNT>\SavedVariables\ElansHub.lua
        public static List<string> SavedVariablesFiles(string root)
        {
            var files = new List<string>();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return files;
            try
            {
                foreach (var flavor in Directory.GetDirectories(root, "_*_"))
                {
                    var accounts = Path.Combine(flavor, "WTF", "Account");
                    if (!Directory.Exists(accounts)) continue;
                    foreach (var acc in Directory.GetDirectories(accounts))
                    {
                        var f = Path.Combine(acc, "SavedVariables", "ElansHub.lua");
                        if (File.Exists(f)) files.Add(f);
                    }
                }
            }
            catch { }
            return files;
        }

        public static string NewestSavedVariables(string root) =>
            SavedVariablesFiles(root).OrderByDescending(SafeStamp).FirstOrDefault();

        public static Character Read(string file)
        {
            if (file == null) return null;
            try
            {
                string text = null;
                for (int i = 0; i < 4 && text == null; i++) // WoW may still hold the file while writing
                {
                    try
                    {
                        using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        using (var r = new StreamReader(fs)) text = r.ReadToEnd();
                    }
                    catch (IOException) { System.Threading.Thread.Sleep(80); }
                }
                if (text == null) return null;
                var db = LuaData.ReadGlobals(text).TryGetValue("ElansHubDB", out var v) ? v as Dictionary<string, object> : null;
                var chars = db?.Child("chars");
                var cur = chars?.Child(db.Str("current") ?? "");
                if (cur == null) return null;
                return new Character
                {
                    Name = cur.Str("name"), Realm = cur.Str("realm"), Class = cur.Str("class"), ClassFile = cur.Str("classFile"),
                    Race = cur.Str("race"), RaceFile = cur.Str("raceFile"), Sex = cur.Int("sex"), Updated = cur.Long("updated"),
                    Zone = cur.Str("zone"), Guild = cur.Str("guild"), Instance = cur.Str("instance"), Level = cur.Int("level"),
                    Online = !db.ContainsKey("online") || db.Bool("online"),
                };
            }
            catch (Exception e)
            {
                Util.Log("presence: " + e.Message);
                return null;
            }
        }
    }
}
