using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace ElansAddonHub.Services
{
    // Keeps the Addon health report up to date: watches the SavedVariables folders (read-only; WoW writes them on /reload and logout),
    // re-reads with a debounce, remembers which errors the user already looked at ("seen") and which ones already got a toast.
    public class HealthMonitor
    {
        readonly Settings settings;
        Func<string> wowRoot;
        public Func<string> Root { get => wowRoot; set { wowRoot = value; } }
        readonly Dispatcher dispatcher;
        readonly List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();
        readonly System.Threading.Timer debounce;      // not a DispatcherTimer: a busy UI must not starve the re-read
        readonly TimeSpan wait;
        string watchedRoot;
        int generation;

        public HealthReport Report { get; private set; } = new HealthReport();
        public event Action Changed;
        // an addon's title and how many errors are new to the user and not yet announced: the toast says "Elan's Bags: 2 new errors"
        public event Action<string, int> NewErrors;

        public HealthMonitor(Settings s, Func<string> root, Dispatcher d, TimeSpan? debounceTime = null)
        {
            settings = s; wowRoot = root; dispatcher = d;
            wait = debounceTime ?? TimeSpan.FromMilliseconds(900);
            debounce = new System.Threading.Timer(_ => { try { dispatcher.BeginInvoke(new Action(ReloadAsync)); } catch { } }, null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
        }

        public int NewCount => Report.NewTotal;
        public int ErrorCount => Report.ErrorTotal;

        public void Start()
        {
            EnsureWatchers();
            ReloadAsync();
        }

        // (re)create the watchers when the WoW folder changed or new account folders appeared
        public bool RootChanged => watchedRoot != wowRoot();
        public void EnsureWatchers()
        {
            var root = wowRoot();
            var dirs = HealthReader.Dirs(root);
            if (watchedRoot == root && watchers.Count == dirs.Count) return;
            Dispose();
            watchedRoot = root;
            foreach (var d in dirs)
            {
                try
                {
                    var w = new FileSystemWatcher(d, "Elans*.lua") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime };
                    FileSystemEventHandler h = (s, e) => Poke();
                    w.Changed += h; w.Created += h; w.Deleted += h;
                    w.Renamed += (s, e) => Poke();
                    w.EnableRaisingEvents = true;
                    watchers.Add(w);
                }
                catch { }
            }
        }

        void Poke()
        {
            try { debounce.Change(wait, System.Threading.Timeout.InfiniteTimeSpan); } catch { }
        }

        public void Dispose()
        {
            foreach (var w in watchers) { try { w.EnableRaisingEvents = false; w.Dispose(); } catch { } }
            watchers.Clear();
            watchedRoot = null;
        }

        // reads on a worker thread (the Hunter Helper's file can be big), then applies on the UI thread
        public void ReloadAsync()
        {
            var root = wowRoot();
            int gen = ++generation;
            Task.Run(() =>
            {
                HealthReport rep;
                try { rep = HealthReader.Read(root); }
                catch (Exception e) { Util.Log("health read: " + e.Message); rep = new HealthReport(); }
                try { dispatcher.BeginInvoke(new Action(() => { if (gen == generation) Apply(rep); })); } catch { }
            });
        }

        // synchronous (tests, and "Refresh" in the panel)
        public void ReloadNow()
        {
            generation++;
            HealthReport rep;
            try { rep = HealthReader.Read(wowRoot()); }
            catch (Exception e) { Util.Log("health read: " + e.Message); rep = new HealthReport(); }
            Apply(rep);
        }

        void Apply(HealthReport rep)
        {
            HealthReader.Evaluate(rep, settings.HealthSeen);
            var counts = HealthReader.CountsOf(rep);
            // first run of this feature: the errors already in the files are no news for a toast (the panel still shows them as new)
            if (settings.HealthToasted == null)
            {
                settings.HealthToasted = new Dictionary<string, int>(counts);
                SettingsStore.Save(settings);
            }
            var announce = new List<KeyValuePair<string, int>>();
            foreach (var h in rep.Addons)
            {
                int n = h.Bugs.Count(b => b.IsNew && (!settings.HealthToasted.TryGetValue(b.Key, out var t) || b.Count > t));
                if (n > 0) announce.Add(new KeyValuePair<string, int>(h.Title, n));
            }
            bool changed = false;
            foreach (var kv in counts)
                if (!settings.HealthToasted.TryGetValue(kv.Key, out var t) || t < kv.Value) { settings.HealthToasted[kv.Key] = kv.Value; changed = true; }
            if (changed) SettingsStore.Save(settings);
            Report = rep;
            Changed?.Invoke();
            foreach (var a in announce) NewErrors?.Invoke(a.Key, a.Value);
        }

        // "Mark as seen": everything in the files now is known
        public void MarkSeen()
        {
            var counts = HealthReader.CountsOf(Report);
            if (settings.HealthSeen == null) settings.HealthSeen = new Dictionary<string, int>();
            foreach (var kv in counts) settings.HealthSeen[kv.Key] = kv.Value;
            if (settings.HealthToasted == null) settings.HealthToasted = new Dictionary<string, int>();
            foreach (var kv in counts) settings.HealthToasted[kv.Key] = Math.Max(kv.Value, settings.HealthToasted.TryGetValue(kv.Key, out var t) ? t : 0);
            SettingsStore.Save(settings);
            HealthReader.Evaluate(Report, settings.HealthSeen);
            Changed?.Invoke();
        }

        // the SavedVariables folder with the newest of our files (else the first one), for "Open SavedVariables folder"
        public string BestFolder()
        {
            try
            {
                var newest = Report.Addons.Where(a => a.HasFile).OrderByDescending(a => a.FileWritten).FirstOrDefault();
                foreach (var d in Report.Folders)
                    if (newest != null && File.Exists(Path.Combine(d, newest.Def.File))) return d;
                return Report.Folders.FirstOrDefault();
            }
            catch { return null; }
        }
    }
}
