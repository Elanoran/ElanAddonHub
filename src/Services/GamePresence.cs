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
        }

        readonly Func<string> wowRoot;
        readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        readonly DispatcherTimer debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        readonly List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();
        string watchKey = "";
        string lastSig;
        Character last; // newest character the addon has written (whatever its state)

        public bool Playing { get; private set; }
        public Character Current { get; private set; }
        public event Action Changed;

        public GamePresence(Func<string> wowRoot)
        {
            this.wowRoot = wowRoot;
            timer.Tick += (s, e) => Poll();
            debounce.Tick += (s, e) => { debounce.Stop(); Poll(); };
        }

        public void Start() { Poll(); timer.Start(); }

        public void Poll()
        {
            var playing = WowRunning(out var started);

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

            bool changed = playing != Playing || !Same(shown, Current);
            Playing = playing; Current = shown;
            if (changed) Changed?.Invoke();
        }

        static bool FromThisSession(Character c, DateTime started)
        {
            if (started == DateTime.MinValue || c.Updated <= 0) return true;
            return DateTimeOffset.FromUnixTimeSeconds(c.Updated).UtcDateTime >= started.ToUniversalTime().AddSeconds(-5);
        }

        static bool Same(Character a, Character b) =>
            ReferenceEquals(a, b) || (a != null && b != null && a.Name == b.Name && a.Realm == b.Realm && a.Updated == b.Updated && a.Online == b.Online);

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
                        if (n.StartsWith("Wow", StringComparison.OrdinalIgnoreCase)
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
