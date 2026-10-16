using System;
using System.IO;
using System.Threading;

namespace ElansAddonHub.Services
{
    // One opened game storage for the game-data readers that run at the same time (items, spells, classes, talents, maps, the area probe). Each of them used
    // to open its own CascStorage: the 16 index files (about 40 MB) read five times over, and the root file walked again and again. Now they lease the same
    // one: opened once per WoW folder + client + game build, with the root kept as a sorted table (CascStorage.UseRootIndex), and closed a little while
    // after the last reader gave it back (IdleMs), so readers that start a few seconds apart still share it. Read-only and thread-safe (CascStorage
    // serialises its file reads); key-locked parts read as zeros (ZeroFillEncrypted), as every DB2 reader wants. Readers that switch that off (icons,
    // the world map pictures) keep opening their own.
    public static class CascShared
    {
        internal sealed class Entry { public string Key; public CascStorage Casc; public int Refs; public Timer Idle; }

        public sealed class Lease : IDisposable
        {
            Entry e;
            internal Lease(Entry e) { this.e = e; }
            public CascStorage Casc => e?.Casc;
            public void Dispose() { var x = Interlocked.Exchange(ref e, null); if (x != null) Release(x); }
        }

        static readonly object gate = new object();
        static Entry cur;
        public static int IdleMs = 15000;
        public static int OpensForTest;

        public static Lease Open(string root, string flavor)
        {
            var key = (root ?? "") + "|" + (flavor ?? "") + "|" + (Db2.ItemData.GameBuild(root, flavor) ?? "");
            lock (gate)
            {
                if (cur != null && cur.Key == key)
                {
                    cur.Refs++;
                    cur.Idle?.Change(Timeout.Infinite, Timeout.Infinite);
                    return new Lease(cur);
                }
                // another folder or build (the game was patched while the Outpost ran): the old one closes when its last reader is done
                if (cur != null && cur.Refs == 0) Close(cur);
                var c = CascStorage.Open(root, flavor);
                c.ZeroFillEncrypted = true;
                c.UseRootIndex = true;
                Interlocked.Increment(ref OpensForTest);
                cur = new Entry { Key = key, Casc = c, Refs = 1 };
                return new Lease(cur);
            }
        }

        static void Release(Entry e)
        {
            lock (gate)
            {
                e.Refs--;
                if (e.Refs > 0) return;
                if (!ReferenceEquals(e, cur)) { Close(e); return; }
                if (IdleMs <= 0) { Close(e); cur = null; return; }
                if (e.Idle == null) e.Idle = new Timer(_ => { lock (gate) { if (e.Refs == 0 && ReferenceEquals(e, cur)) { Close(e); cur = null; } } }, null, IdleMs, Timeout.Infinite);
                else e.Idle.Change(IdleMs, Timeout.Infinite);
            }
        }

        static void Close(Entry e)
        {
            try { e.Idle?.Dispose(); } catch (Exception) { }
            e.Idle = null;
            try { e.Casc?.Dispose(); } catch (Exception) { }
            e.Casc = null;
        }

        // One rebuild from the game files at a time. They used to run side by side, and the big short-lived arrays of all of them (decoded tables, terrain
        // tiles, the root table) were alive together: the process peaked at several GB. One after the other they take about as long (they shared the disk
        // and the CPU anyway) and the peak is the largest single one. The maps start first (the Atlas pages wait for them most). A load of the saved copy
        // never waits for this.
        static readonly SemaphoreSlim rebuildGate = new SemaphoreSlim(1, 1);
        public static IDisposable RebuildTurn(string jobId)
        {
            if (!rebuildGate.Wait(0))
            {
                GameDataProgress.Step(jobId, "Waiting for the other game data", -1);
                rebuildGate.Wait();
            }
            return new Turn();
        }
        public static Func<T> OneAtATime<T>(string jobId, Func<T> rebuild) => () => { using (RebuildTurn(jobId)) return rebuild(); };
        sealed class Turn : IDisposable { int done; public void Dispose() { if (Interlocked.Exchange(ref done, 1) == 0) rebuildGate.Release(); } }

        // close it now (tests; nothing may be using it)
        public static void CloseIdle() { lock (gate) { if (cur != null && cur.Refs == 0) { Close(cur); cur = null; } } }
        public static bool IsOpenForTest { get { lock (gate) return cur != null; } }
    }
}
