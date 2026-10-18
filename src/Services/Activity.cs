using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ElansAddonHub.Services
{
    // ============================================================================================================================
    // What the Outpost is busy with in the background, in one place (Outpost 2.96): every job that reads, builds, saves or uploads says when it starts
    // and ends, with a plain name ("Saving Atlas finds", "Building the world map"). The rail's small wheel above Settings turns while anything runs
    // and its tooltip lists the names (MainWindow.Activity.cs); closing the Outpost waits for the jobs of kind Save (writing data, an upload in flight)
    // behind a short "Finishing up" dialog (CloseGuard.cs). Work (reading files, rendering, building caches that resume) simply stops on close.
    // Begin returns a token: dispose it when done (twice is harmless). A job whose start and end are in different places (an upload waiting for its
    // answer) uses Hold / Release with a key instead, one entry per key. Thread-safe: a lock around a small list; Changed is raised on the reporting
    // thread after every change. Nothing here decides anything: it only tells.
    // ============================================================================================================================
    public static class Activity
    {
        public enum Kind { Work, Save }      // Work = may simply stop when the Outpost closes; Save = writing or uploading: closing waits for it

        public sealed class Token : IDisposable
        {
            internal long Id;
            internal string Key;
            public string Text { get; internal set; }
            public Kind Kind { get; internal set; }
            public DateTime Started { get; internal set; }
            internal int ended;
            public bool Ended => ended != 0;
            public void Dispose() => End(this);
        }

        // what the indicator and the close guard read: a copy, safe to keep
        public sealed class View
        {
            public int Count;                                   // jobs running (each Begin / Hold counts once)
            public int Saves;                                   // ... of them must finish before the Outpost closes
            public IReadOnlyList<string> Lines = new string[0]; // their names, each once, saves first, then in start order
            public IReadOnlyList<string> SaveLines = new string[0];
            public bool Busy => Count > 0;
        }

        static readonly object gate = new object();
        static readonly List<Token> running = new List<Token>();
        static readonly Dictionary<string, Token> held = new Dictionary<string, Token>(StringComparer.Ordinal);
        static long nextId;
        public static event Action Changed;

        public static Token Begin(string text, Kind kind = Kind.Work)
        {
            var t = new Token { Id = Interlocked.Increment(ref nextId), Text = string.IsNullOrWhiteSpace(text) ? "Working" : text.Trim(), Kind = kind, Started = DateTime.UtcNow };
            lock (gate) running.Add(t);
            Raise();
            return t;
        }

        public static void End(Token t)
        {
            if (t == null || Interlocked.Exchange(ref t.ended, 1) == 1) return;
            lock (gate)
            {
                running.Remove(t);
                if (t.Key != null && held.TryGetValue(t.Key, out var h) && ReferenceEquals(h, t)) held.Remove(t.Key);
            }
            Raise();
        }

        // one entry per key: holding again only renames it; Release ends it (an unknown key is fine)
        public static void Hold(string key, string text, Kind kind = Kind.Save)
        {
            if (string.IsNullOrEmpty(key)) return;
            lock (gate)
            {
                if (held.TryGetValue(key, out var h)) { h.Text = text; h.Kind = kind; }
                else
                {
                    h = new Token { Id = Interlocked.Increment(ref nextId), Key = key, Text = string.IsNullOrWhiteSpace(text) ? "Working" : text.Trim(), Kind = kind, Started = DateTime.UtcNow };
                    held[key] = h; running.Add(h);
                }
            }
            Raise();
        }

        public static void Release(string key)
        {
            Token h;
            lock (gate) { if (key == null || !held.TryGetValue(key, out h)) return; }
            End(h);
        }

        public static bool IsHeld(string key) { lock (gate) return key != null && held.ContainsKey(key); }

        // wrap a task: the entry lasts as long as the task does (faulted or cancelled included)
        public static async Task Track(string text, Kind kind, Func<Task> work)
        {
            using (Begin(text, kind)) await work().ConfigureAwait(false);
        }

        public static async Task<T> Track<T>(string text, Kind kind, Func<Task<T>> work)
        {
            using (Begin(text, kind)) return await work().ConfigureAwait(false);
        }

        public static View Now()
        {
            lock (gate)
            {
                var order = running.Where(t => t.Kind == Kind.Save).Concat(running.Where(t => t.Kind != Kind.Save)).ToList();
                return new View
                {
                    Count = running.Count,
                    Saves = running.Count(t => t.Kind == Kind.Save),
                    Lines = order.Select(t => t.Text).Distinct(StringComparer.Ordinal).ToList(),
                    SaveLines = running.Where(t => t.Kind == Kind.Save).Select(t => t.Text).Distinct(StringComparer.Ordinal).ToList(),
                };
            }
        }

        // the indicator's tooltip: the names, at most MaxLines of them, then "and N more"
        public const int MaxLines = 5;
        public static string Describe(View v)
        {
            if (v == null || !v.Busy) return "";
            var lines = v.Lines.Take(MaxLines).ToList();
            if (v.Lines.Count > MaxLines) lines.Add("and " + (v.Lines.Count - MaxLines) + " more");
            return string.Join("\n", lines);
        }

        // true when no Save job is left (at once when there was none), false when the cap or the token ran out first
        public static async Task<bool> WaitForSaves(TimeSpan cap, CancellationToken ct = default(CancellationToken))
        {
            var until = DateTime.UtcNow + cap;
            while (Now().Saves > 0)
            {
                if (ct.IsCancellationRequested || DateTime.UtcNow >= until) return false;
                try { await Task.Delay(50, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return false; }
            }
            return true;
        }

        static void Raise()
        {
            try { Changed?.Invoke(); } catch (Exception e) { Util.Log("activity: " + e.GetType().Name); }
        }

        public static void ResetForTest() { lock (gate) { foreach (var t in running) t.ended = 1; running.Clear(); held.Clear(); } Raise(); }
    }
}
