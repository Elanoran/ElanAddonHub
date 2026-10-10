using System;
using System.Threading;
using System.Windows;

namespace ElansAddonHub.Services
{
    // Is WoW running? One cheap poll for the whole app (every 2 s on a worker thread). Everything that only makes sense while the
    // game runs - the pixel-strip capture, the presence poll, the overlay/toast placement, the CurseForge/health extra polling -
    // pauses while this says "no" and resumes within a couple of seconds after WoW starts. FileSystemWatchers stay on (they cost nothing).
    public class WowWatch
    {
        public static readonly WowWatch Shared = new WowWatch(DefaultDetect, 2000);

        // ---- the real detector. A full process list costs ~5 ms, so it only runs when needed: a window named "World of Warcraft"
        // (or WoW's window class) is the cheap hint every 2 s; no hint = nothing running, with a full scan only every 10 s as a safety net.
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern IntPtr FindWindowW(string cls, string title);
        static int quiet;
        static bool Hint() => FindWindowW(null, "World of Warcraft") != IntPtr.Zero || FindWindowW("GxWindowClass", null) != IntPtr.Zero;

        // test-only switches (only with a test data folder): ELANSHUB_FAKE_WOW=1/0 fixes the answer
        static readonly string Fake = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ELANSHUB_DATA")) ? null : Environment.GetEnvironmentVariable("ELANSHUB_FAKE_WOW");

        static bool DefaultDetect()
        {
            if (Fake != null) return Fake == "1";
            bool was = Shared != null && Shared.Known && Shared.Running;
            if (Shared == null || !Shared.Known) return GamePresence.WowRunningNow();
            if (Hint()) { quiet = 0; return was || GamePresence.WowRunningNow(); }
            if (was) { quiet = 0; return GamePresence.WowRunningNow(); }          // the window is gone: confirm that WoW really exited
            if (++quiet >= 5) { quiet = 0; return GamePresence.WowRunningNow(); }
            return false;
        }

        public Func<bool> Detector;                 // tests swap in a fake
        readonly int intervalMs;
        Timer timer;
        int busy;
        public bool Running { get; private set; }
        public bool Known { get; private set; }     // false until the first poll
        public int Polls;                           // how often the detector ran (idle-cost accounting in the self-test)
        public event Action<bool> Changed;

        public WowWatch(Func<bool> detector, int intervalMs = 2000) { Detector = detector; this.intervalMs = intervalMs; }

        // first poll right away (synchronously), then every interval
        public void Start()
        {
            if (timer != null) return;
            Poll();
            timer = new Timer(_ => Background(), null, intervalMs, intervalMs);
        }

        public void Stop() { timer?.Dispose(); timer = null; }

        void Background()
        {
            if (Interlocked.Exchange(ref busy, 1) == 1) return;
            try
            {
                bool now;
                try { Polls++; now = Detector(); } catch { now = false; }
                if (!Known || now != Running)
                {
                    var d = Application.Current?.Dispatcher;
                    if (d != null) d.BeginInvoke(new Action(() => Set(now))); else Set(now);
                }
            }
            finally { busy = 0; }
        }

        // synchronous poll (start-up and tests)
        public void Poll()
        {
            bool now;
            try { Polls++; now = Detector(); } catch { now = false; }
            Set(now);
        }

        void Set(bool now)
        {
            bool first = !Known;
            Known = true;
            if (!first && now == Running) return;
            Running = now;
            Changed?.Invoke(now);
        }
    }
}
