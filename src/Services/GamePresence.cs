using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Threading;

namespace ElansAddonHub.Services
{
    // What you're playing, for the Lodge: whether WoW is running (live) and your character from the
    // Elan's Hub companion addon (as of the last /reload or logout - WoW only saves then).
    public class GamePresence
    {
        public class Character
        {
            public string Name, Realm, Class, ClassFile, Race, Zone, Guild, Instance;
            public int Level;
        }

        readonly Func<string> wowRoot;
        readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        string lastFile;
        DateTime lastStamp;

        public bool Playing { get; private set; }
        public Character Current { get; private set; }
        public event Action Changed;

        public GamePresence(Func<string> wowRoot)
        {
            this.wowRoot = wowRoot;
            timer.Tick += (s, e) => Poll();
        }

        public void Start() { Poll(); timer.Start(); }

        public void Poll()
        {
            bool changed = false;
            var playing = WowRunning();
            if (playing != Playing) { Playing = playing; changed = true; }

            var file = NewestSavedVariables(wowRoot());
            var stamp = file != null ? File.GetLastWriteTimeUtc(file) : DateTime.MinValue;
            if (file != lastFile || stamp != lastStamp)
            {
                lastFile = file;
                lastStamp = stamp;
                var c = Read(file);
                if (c != null || Current != null) { Current = c; changed = true; }
            }
            if (changed) Changed?.Invoke();
        }

        static bool WowRunning()
        {
            try
            {
                return Process.GetProcesses().Any(p =>
                {
                    var n = p.ProcessName;
                    return n.StartsWith("Wow", StringComparison.OrdinalIgnoreCase)
                        && n.IndexOf("Voice", StringComparison.OrdinalIgnoreCase) < 0
                        && n.IndexOf("Error", StringComparison.OrdinalIgnoreCase) < 0;
                });
            }
            catch { return false; }
        }

        // <root>\_<flavor>_\WTF\Account\<ACCOUNT>\SavedVariables\ElansHub.lua - the newest one wins
        public static string NewestSavedVariables(string root)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return null;
            try
            {
                var files = new List<string>();
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
                return files.OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            }
            catch { return null; }
        }

        public static Character Read(string file)
        {
            if (file == null) return null;
            try
            {
                var db = LuaData.ReadGlobals(File.ReadAllText(file)).TryGetValue("ElansHubDB", out var v) ? v as Dictionary<string, object> : null;
                var chars = db?.Child("chars");
                var cur = chars?.Child(db.Str("current") ?? "");
                if (cur == null) return null;
                return new Character
                {
                    Name = cur.Str("name"), Realm = cur.Str("realm"), Class = cur.Str("class"), ClassFile = cur.Str("classFile"),
                    Race = cur.Str("race"), Zone = cur.Str("zone"), Guild = cur.Str("guild"), Instance = cur.Str("instance"),
                    Level = cur.Int("level"),
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
