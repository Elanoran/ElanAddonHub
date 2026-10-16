using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace ElansAddonHub.Services
{
    // ============================================================================================================================
    // What the Outpost is doing with the game's files right now, in one place: every reader (items, spells, classes, maps, talents, what the client has
    // seen, the zone levels, ...) says when it starts, how far it is (a fraction, or n of m) and when it is done or failed, with a human step name
    // ("Reading spells") and whether this is a fast load of the saved copy or a rebuild from the game files (the first start, or after WoW or the
    // Outpost was updated). The window's progress strip and the pages that wait for game data show it (Controls/GameDataProgressView.cs).
    // Thread-safe and cheap: a lock around a small list; Changed is raised on the reporting thread, at most every ReportMs for plain progress
    // (start / done / failed / a new step always). Nothing here decides anything: it only tells.
    // ============================================================================================================================
    public static class GameDataProgress
    {
        public enum JobState { Running, Done, Failed }
        public enum Why { None, FirstStart, OutpostUpdated, WowUpdated }      // in this order of weight: the strip names the strongest

        public sealed class Job
        {
            public string Id, Title, Step, Error;
            public string Unit;                  // what Done / Total count ("tables", "terrain tiles"); null = the count is not shown
            public JobState State;
            public bool Rebuild;                 // reading the game files (slow), not the saved copy (fast)
            public bool Background;              // ... while the copy of the previous game build is shown (nothing waits for it)
            public Why Reason;
            public double Fraction = -1;         // 0..1 of this job, -1 = unknown
            public int Done, Total;              // "12 of 40" when the step counts things (0 = no count)
            public double Weight = 1;            // its share of the overall bar
            public DateTime Started, Ended;
            public long Ms => (long)((State == JobState.Running ? DateTime.UtcNow : Ended) - Started).TotalMilliseconds;
            public Job Copy() => (Job)MemberwiseClone();
        }

        // what the strip and the pages draw: a copy, safe to keep
        public sealed class View
        {
            public IReadOnlyList<Job> Jobs = new Job[0];   // this batch (everything since the last time all was done), in start order
            public bool Busy;                              // something is running
            public bool Rebuilding;                        // something of this batch reads the game files
            public bool Background;                        // ... and every such job has older data on screen meanwhile (no page waits)
            public double Overall;                         // 0..1 over this batch, by weight
            public Job Current;                            // the running job to name (the rebuild with the most left, else the first running one)
            public Why Reason;                             // the strongest reason of the batch's rebuilds
            public int Generation;                         // a new batch = a new number
            public DateTime FinishedAt;                    // when the batch's last job ended (Busy false)
            public bool Failed => Jobs.Any(j => j.State == JobState.Failed);
            public Job Find(string id) => Jobs.FirstOrDefault(j => j.Id == id);
            public bool IsRunning(string id) => Jobs.Any(j => j.Id == id && j.State == JobState.Running);
        }

        static readonly object gate = new object();
        static readonly List<Job> batch = new List<Job>();
        static int generation;
        static DateTime finishedAt;
        static long lastRaise;
        public static int ReportMs = 100;
        public static event Action Changed;

        // ---- reporting (any thread)
        public static void Begin(string id, string title, string step, double weight = 1)
        {
            lock (gate)
            {
                if (!batch.Any(j => j.State == JobState.Running)) { if (batch.Count > 0) { batch.Clear(); } generation++; }
                batch.RemoveAll(j => j.Id == id);
                batch.Add(new Job { Id = id, Title = title, Step = step, State = JobState.Running, Weight = Math.Max(0.01, weight), Started = DateTime.UtcNow, Fraction = 0 });
            }
            Raise(true);
        }

        // this job reads the game files (the saved copy was missing, of another game build or of an older Outpost)
        public static void Rebuilding(string id, Why why, string step = null, double weight = -1, string unit = null, bool background = false)
        {
            lock (gate)
            {
                var j = Get(id);
                if (j == null) return;
                j.Rebuild = true; j.Reason = why; j.Background = background;
                if (step != null) { j.Step = step; j.Unit = unit; j.Done = j.Total = 0; }
                if (weight > 0) j.Weight = weight;
            }
            Raise(true);
        }

        public static void Step(string id, string step, double fraction = -1, string unit = null, int done = 0, int total = 0)
        {
            lock (gate)
            {
                var j = Get(id);
                if (j == null) return;
                j.Step = step; j.Unit = unit; j.Done = done; j.Total = total;
                if (fraction >= 0) j.Fraction = Math.Max(j.Fraction, Math.Min(1, fraction));
            }
            Raise(true);
        }

        // progress inside the current step: n of m (the fraction of the job is then lo + (hi - lo) * n / m)
        public static void Report(string id, int done, int total, double lo = 0, double hi = 1)
        {
            lock (gate)
            {
                var j = Get(id);
                if (j == null) return;
                j.Done = done; j.Total = total;
                if (total > 0) j.Fraction = Math.Max(j.Fraction, Math.Min(1, lo + (hi - lo) * done / (double)total));
            }
            Raise(false);
        }

        public static void End(string id, bool ok = true, string error = null)
        {
            lock (gate)
            {
                var j = Get(id);
                if (j == null) return;
                j.State = ok ? JobState.Done : JobState.Failed; j.Error = error; j.Ended = DateTime.UtcNow; j.Fraction = 1;
                if (!batch.Any(x => x.State == JobState.Running)) finishedAt = DateTime.UtcNow;
            }
            Raise(true);
        }

        static Job Get(string id) => batch.FirstOrDefault(j => j.Id == id && j.State == JobState.Running);

        static void Raise(bool always)
        {
            long now = Environment.TickCount;
            if (!always && now - Interlocked.Read(ref lastRaise) < ReportMs) return;
            Interlocked.Exchange(ref lastRaise, now);
            try { Changed?.Invoke(); } catch (Exception) { }
        }

        // ---- reading
        public static View Now()
        {
            lock (gate)
            {
                var jobs = batch.Select(j => j.Copy()).ToList();
                var v = new View { Jobs = jobs, Generation = generation, FinishedAt = finishedAt };
                v.Busy = jobs.Any(j => j.State == JobState.Running);
                v.Rebuilding = jobs.Any(j => j.Rebuild);
                v.Background = v.Rebuilding && jobs.Where(j => j.Rebuild).All(j => j.Background);
                double w = jobs.Sum(j => j.Weight);
                v.Overall = w <= 0 ? 0 : jobs.Sum(j => j.Weight * (j.State != JobState.Running ? 1 : Math.Max(0, j.Fraction))) / w;
                var running = jobs.Where(j => j.State == JobState.Running).ToList();
                v.Current = running.Where(j => j.Rebuild).OrderByDescending(j => j.Weight * (1 - Math.Max(0, j.Fraction))).FirstOrDefault() ?? running.FirstOrDefault();
                v.Reason = jobs.Where(j => j.Rebuild).Select(j => j.Reason).DefaultIfEmpty(Why.None).Max();
                return v;
            }
        }

        public static void ResetForTest() { lock (gate) { batch.Clear(); generation++; finishedAt = default(DateTime); } Raise(true); }

        // ---- words
        // why a saved copy is not used: its game build, or none at all (savedBuild == null: the file is missing, damaged or of an older Outpost)
        public static Why ReasonFor(string cacheFile, string savedBuild, string build)
        {
            if (savedBuild != null) return savedBuild != build ? Why.WowUpdated : Why.OutpostUpdated;
            try { return cacheFile != null && File.Exists(cacheFile) ? Why.OutpostUpdated : Why.FirstStart; }
            catch (Exception) { return Why.FirstStart; }
        }

        public static string ReasonText(Why why, bool background = false)
        {
            if (background && why == Why.WowUpdated) return "WoW was updated: reading the new game files in the background";
            if (background && why == Why.OutpostUpdated) return "The Outpost was updated: reading the game files in the background";
            switch (why)
            {
                case Why.WowUpdated: return "WoW was updated: reading the new game files once";
                case Why.OutpostUpdated: return "The Outpost was updated: reading the game files once";
                case Why.FirstStart: return "First start: reading your game files once";
                default: return "";
            }
        }

        // "Reading spells: 12 of 40 tables" / "Reading spells"
        public static string StepText(Job j)
        {
            if (j == null) return "";
            var s = j.Step ?? j.Title ?? "";
            return j.Total > 0 && !string.IsNullOrEmpty(j.Unit) ? s + ": " + j.Done.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " of " + j.Total.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " " + j.Unit : s;
        }
    }
}
