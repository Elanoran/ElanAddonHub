using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ElansAddonHub.Services.Db2;

namespace ElansAddonHub.Services.Atlas
{
    // ============================================================================================================================
    // The numbers on the right of the Atlas side menu's rows (2.77): remembered, not worked out while you wait.
    //   * one number per nav row (its key: "items", "zones", "worldmap", "professions", "finds" ...), held here for the whole run, so leaving the Atlas and
    //     coming back shows them at once;
    //   * written to atlas-nav-counts.txt in the data folder, so the next start shows the last numbers before any game data is read;
    //   * worked out again on a worker when what they count changed (the game data finished loading, the Lodge pool got finds, the Classic baseline,
    //     the map index, the zone levels, a category registered): Invalidate() starts a short wait, then one background pass; Changed tells the page,
    //     which puts the new numbers into the rows it already has (no rebuild, nothing moves but the number).
    // A count that comes back 0 or unknown (its data is not loaded yet) never wipes a remembered one: a start without the game data keeps the last numbers.
    // What a row counts: a list category its list (AtlasBrowse.Count), a hosted page what it says (AtlasCategory.NavCount: World map = continents,
    // Professions = professions), the Lodge's Finds the rows of the Finds list. Home, Talents (a calculator: its classes are the Classes row) and
    // Data sources have none.
    // ============================================================================================================================
    public static class AtlasNavCounts
    {
        static readonly object gate = new object();
        static readonly Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.Ordinal);
        static bool loaded, enabled, running, again;
        static string doneToken;
        static Timer debounce;
        static Task last = Task.CompletedTask;

        public static string FilePath = Path.Combine(Util.DataDir, "atlas-nav-counts.txt");
        public static int DebounceMs = 800;
        public static event Action Changed;                      // on a worker thread: one or more numbers changed
        public static Func<AtlasNavEntry, int> ComputeOverrideForTest;
        public static int PassesForTest;

        // the remembered number of a row, -1 when there is none (read from the file the first time)
        public static int Get(string key)
        {
            EnsureLoaded();
            lock (gate) return key != null && counts.TryGetValue(key, out var n) ? n : -1;
        }

        public static IReadOnlyDictionary<string, int> Snapshot() { EnsureLoaded(); lock (gate) return new Dictionary<string, int>(counts, StringComparer.Ordinal); }

        public static string Text(string key)
        {
            int n = Get(key);
            return n > 0 ? n.ToString("N0", ElansAddonHub.Lodge.CalendarRules.English) : "";
        }

        static void EnsureLoaded()
        {
            lock (gate)
            {
                if (loaded) return;
                loaded = true;
                try
                {
                    if (!File.Exists(FilePath)) return;
                    foreach (var line in File.ReadAllLines(FilePath))
                    {
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        if (int.TryParse(line.Substring(eq + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0) counts[line.Substring(0, eq).Trim()] = n;
                    }
                }
                catch (Exception e) { Util.Log("atlas nav counts load: " + e.Message); }
            }
        }

        static void Save(Dictionary<string, int> snap)
        {
            try
            {
                var tmp = FilePath + ".tmp";
                File.WriteAllLines(tmp, snap.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Key + "=" + k.Value.ToString(CultureInfo.InvariantCulture)));
                if (File.Exists(FilePath)) File.Delete(FilePath);
                File.Move(tmp, FilePath);
            }
            catch (Exception e) { Util.Log("atlas nav counts save: " + e.Message); }
        }

        // the self-test: forget everything (the next Get reads the file again)
        public static void ResetForTest(string file = null)
        {
            lock (gate) { counts.Clear(); loaded = false; enabled = false; doneToken = null; again = false; }
            if (file != null) FilePath = file;
        }

        // the Atlas was shown: from now on a change of the data is counted again (before that nothing is built for the numbers alone)
        public static void Enable()
        {
            lock (gate) { if (enabled) return; enabled = true; }
            Hook();
            Invalidate();
        }

        static bool hooked;
        static void Hook()
        {
            if (hooked) return;
            hooked = true;
            ItemData.Changed += Invalidate; SpellData.Changed += Invalidate; ClassData.Changed += Invalidate; SeenData.Changed += Invalidate;
            Maps.MapData.Changed += Invalidate; ZoneLevels.Changed += Invalidate; AtlasCategories.Changed += Invalidate;
        }

        // something the numbers come from changed: one pass a moment later (a burst of changes is one pass)
        public static void Invalidate()
        {
            lock (gate)
            {
                if (!enabled) return;
                if (debounce == null) debounce = new Timer(_ => { _ = RefreshAsync(); }, null, Timeout.Infinite, Timeout.Infinite);
                debounce.Change(DebounceMs, Timeout.Infinite);
            }
        }

        // what the numbers were worked out from: the browse token (game data, Classic, Lodge pool, map index), the zone levels and the visible rows
        static string DataToken()
        {
            string rows = "";
            try { rows = string.Join(",", AtlasCategories.Visible.Select(c => c.Id)) + "|" + AtlasQuery.LodgeHasData(); } catch (Exception) { }
            return AtlasBrowse.DataToken() + "|zl" + ZoneLevels.Revision + "|" + rows;
        }

        // one pass now (on a worker); force = even when the data looks the same. The task ends when the numbers are in.
        public static Task RefreshAsync(bool force = false)
        {
            lock (gate)
            {
                if (running) { again = true; return last; }
                running = true;
                if (force) doneToken = null;
                return last = Task.Run(() => Pass());
            }
        }

        static void Pass()
        {
            try
            {
                while (true)
                {
                    lock (gate) again = false;
                    var tok = DataToken();
                    bool same; lock (gate) same = tok == doneToken;
                    if (!same)
                    {
                        PassesForTest++;
                        var fresh = new Dictionary<string, int>(StringComparer.Ordinal);
                        List<AtlasNavEntry> entries;
                        try { entries = AtlasNav.Entries(); } catch (Exception e) { Util.Log("atlas nav counts: " + e.Message); entries = new List<AtlasNavEntry>(); }
                        foreach (var e in entries)
                        {
                            int n;
                            try { n = ComputeOverrideForTest?.Invoke(e) ?? Compute(e); } catch (Exception ex) { Util.Log("atlas nav count " + e.Key + ": " + ex.Message); n = -1; }
                            fresh[e.Key] = n;
                        }
                        EnsureLoaded();
                        bool changed = false; Dictionary<string, int> snap;
                        lock (gate)
                        {
                            foreach (var kv in fresh)
                            {
                                if (kv.Value <= 0) continue;                     // not loaded yet (or nothing): the remembered number stays
                                if (!counts.TryGetValue(kv.Key, out var old) || old != kv.Value) { counts[kv.Key] = kv.Value; changed = true; }
                            }
                            doneToken = tok;
                            snap = new Dictionary<string, int>(counts, StringComparer.Ordinal);
                        }
                        if (changed)
                        {
                            Save(snap);
                            try { Changed?.Invoke(); } catch (Exception e) { Util.Log("atlas nav counts changed: " + e.Message); }
                        }
                    }
                    lock (gate) { if (!again) { running = false; return; } }
                }
            }
            catch (Exception e) { Util.Log("atlas nav counts pass: " + e); lock (gate) running = false; }
        }

        // the number of one row (on a worker; may build a list index)
        public static int Compute(AtlasNavEntry e)
        {
            if (e == null) return -1;
            if (e.Key == AtlasNav.FindsKey) { var r = AtlasBrowse.Run(AtlasNav.FindsId); return r == null || r.NotFound ? -1 : r.Total; }
            if (e.Extra || e.Members.Count != 1) return -1;
            var c = e.Members[0];
            if (c.Hosted) return c.NavCount == null ? -1 : c.NavCount();
            return AtlasBrowse.Count(c);
        }
    }
}
