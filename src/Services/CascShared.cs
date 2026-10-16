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
        internal sealed class Entry { public string Key; public CascStorage Casc; public int Refs; public Timer Idle; public bool Closed; }

        // A lease holds the storage it was given: it stays open until the lease is given back (an entry closes only with no lease on it), so Casc is
        // never null while the lease lives. After Dispose it throws (not a null for the caller to trip over).
        public sealed class Lease : IDisposable
        {
            Entry e;
            readonly CascStorage casc;
            internal Lease(Entry e) { this.e = e; casc = e.Casc; }
            public CascStorage Casc => Volatile.Read(ref e) != null ? casc : throw new ObjectDisposedException("CascShared.Lease", "the game storage lease was given back");
            public void Dispose() { var x = Interlocked.Exchange(ref e, null); if (x != null) Release(x); }
        }

        static readonly object gate = new object();
        static Entry cur;           // under gate; never a closed entry (it is set to null when it closes)
        public static int IdleMs = 15000;
        public static int OpensForTest;
        public static int RetryPauseMs = 400;
        public static Func<string, string, CascStorage> OpenStorageForTest;   // self-test: fail an open now and then

        // Throws (IOException and the like) when the storage cannot be opened; the readers log that as "the game files could not be read". It never
        // hands out a lease without an open storage.
        public static Lease Open(string root, string flavor)
        {
            var key = (root ?? "") + "|" + (flavor ?? "") + "|" + (Db2.ItemData.GameBuild(root, flavor) ?? "");
            lock (gate)
            {
                if (cur != null && cur.Key == key && !cur.Closed)
                {
                    cur.Refs++;
                    cur.Idle?.Change(Timeout.Infinite, Timeout.Infinite);
                    return new Lease(cur);
                }
                // another folder or build (the game was patched while the Outpost ran): the old one closes now when no reader holds it, else when its
                // last reader is done. It stops being the current one either way - also when the new one cannot be opened (it used to stay current
                // closed, and the next reader of the old folder got a lease without a storage: the NullReferenceException of 2.70.0).
                if (cur != null)
                {
                    var old = cur;
                    cur = null;
                    if (old.Refs == 0) Close(old);
                }
                var c = OpenStorage(root, flavor);
                c.ZeroFillEncrypted = true;
                c.UseRootIndex = true;
                Interlocked.Increment(ref OpensForTest);
                cur = new Entry { Key = key, Casc = c, Refs = 1 };
                return new Lease(cur);
            }
        }

        // a storage that looks like a real install but cannot be opened right now (the launcher is writing an index file, a file is locked) gets two more
        // tries; a folder that is no game folder fails at once
        static CascStorage OpenStorage(string root, string flavor)
        {
            for (int attempt = 1; ; attempt++)
            {
                try { return OpenStorageForTest != null ? OpenStorageForTest(root, flavor) : CascStorage.Open(root, flavor); }
                catch (Exception e) when (attempt < 3 && (e is IOException || e is UnauthorizedAccessException) && LooksLikeGame(root))
                {
                    Util.Log("game files: cannot open the game storage (" + e.Message + "), trying again");
                    Thread.Sleep(RetryPauseMs * attempt);
                }
            }
        }

        static bool LooksLikeGame(string root)
        {
            try { return !string.IsNullOrEmpty(root) && File.Exists(Path.Combine(root, ".build.info")) && Directory.Exists(Path.Combine(root, "Data", "data")); }
            catch (Exception) { return false; }
        }

        static void Release(Entry e)
        {
            lock (gate)
            {
                e.Refs--;
                if (e.Refs < 0) { Util.Log("game files: a storage lease was given back twice"); e.Refs = 0; }
                if (e.Refs > 0) return;
                if (!ReferenceEquals(e, cur)) { Close(e); return; }
                if (IdleMs <= 0) { Close(e); cur = null; return; }
                if (e.Idle == null) e.Idle = new Timer(_ => { lock (gate) { if (e.Refs == 0 && ReferenceEquals(e, cur)) { Close(e); cur = null; } } }, null, IdleMs, Timeout.Infinite);
                else e.Idle.Change(IdleMs, Timeout.Infinite);
            }
        }

        // under gate, only with no lease on the entry
        static void Close(Entry e)
        {
            if (e.Closed) return;
            e.Closed = true;
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

        // close it now (tests). Refused (false) while a reader holds it: that one keeps its storage, it closes when given back.
        public static bool CloseIdle() { lock (gate) { if (cur == null) return true; if (cur.Refs > 0) return false; Close(cur); cur = null; return true; } }
        public static bool IsOpenForTest { get { lock (gate) return cur != null; } }
        public static int LeasesForTest { get { lock (gate) return cur?.Refs ?? 0; } }
    }
}
