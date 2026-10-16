using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ElansAddonHub.Services.Db2;

namespace ElansAddonHub.Services
{
    // ============================================================================================================================
    // Atlas layer A3: what this player's client has SEEN on WoW Forever. The client keeps the server's answers about every creature, game object and
    // quest it met in Cache\WDB\<locale>\creaturecache.wdb / gameobjectcache.wdb / questcache.wdb. Like ItemData this reads them read-only (shared), in
    // the background, keeps a compact copy of the parsed index in the Outpost's data folder (cache\seendata.bin, keyed by the three files' sizes and times)
    // and looks again, after things have been quiet for a few seconds, when the game rewrites one of them. Nothing is written to the WoW folder and nothing
    // is uploaded from here (sharing is the ISeenSource hook in AtlasObs). Ids are the foundation's: npc:123, obj:45, quest:6.
    // ============================================================================================================================
    public sealed class SeenHit
    {
        public string Kind;                         // "npc" | "obj" | "quest"
        public int Id;
        public string Name, Detail;                 // Detail: "<title>" and rank, "chest", "level 60" ...
        public string AtlasId => Kind + ":" + Id.ToString(CultureInfo.InvariantCulture);
        public string Source => "Forever";          // read from this client's cache of what the Forever server told it
    }

    // an immutable index of everything seen
    public sealed class SeenSnapshot
    {
        public readonly Dictionary<int, WdbCreature> Npcs = new Dictionary<int, WdbCreature>();
        public readonly Dictionary<int, WdbObject> Objects = new Dictionary<int, WdbObject>();
        public readonly Dictionary<int, WdbQuest> Quests = new Dictionary<int, WdbQuest>();
        readonly string[] lower; readonly SeenHit[] hits;        // every named thing, sorted by name

        public SeenSnapshot(IEnumerable<WdbCreature> npcs, IEnumerable<WdbObject> objects, IEnumerable<WdbQuest> quests)
        {
            foreach (var n in npcs ?? Enumerable.Empty<WdbCreature>()) Npcs[n.Id] = n;
            foreach (var o in objects ?? Enumerable.Empty<WdbObject>()) Objects[o.Id] = o;
            foreach (var q in quests ?? Enumerable.Empty<WdbQuest>()) Quests[q.Id] = q;
            var all = new List<SeenHit>(Npcs.Count + Objects.Count + Quests.Count);
            foreach (var n in Npcs.Values) all.Add(Hit(n));
            foreach (var o in Objects.Values) all.Add(Hit(o));
            foreach (var q in Quests.Values) all.Add(Hit(q));
            hits = all.Where(h => !string.IsNullOrWhiteSpace(h.Name)).OrderBy(h => h.Name, StringComparer.OrdinalIgnoreCase).ThenBy(h => h.Kind, StringComparer.Ordinal).ThenBy(h => h.Id).ToArray();
            lower = hits.Select(h => h.Name.ToLowerInvariant()).ToArray();
        }

        public int NpcCount => Npcs.Count;
        public int ObjectCount => Objects.Count;
        public int QuestCount => Quests.Count;

        static readonly string[] Ranks = { "", "elite", "rare elite", "boss", "rare" };
        public static string RankText(int rank) => rank > 0 && rank < Ranks.Length ? Ranks[rank] : "";
        public static SeenHit Hit(WdbCreature n) => new SeenHit { Kind = "npc", Id = n.Id, Name = n.Name, Detail = string.Join(", ", new[] { string.IsNullOrEmpty(n.Title) ? null : "<" + n.Title + ">", RankText(n.Rank) }.Where(s => !string.IsNullOrEmpty(s))) };
        public static SeenHit Hit(WdbObject o) => new SeenHit { Kind = "obj", Id = o.Id, Name = o.Name, Detail = "type " + o.Type.ToString(CultureInfo.InvariantCulture) };
        public static SeenHit Hit(WdbQuest q) => new SeenHit { Kind = "quest", Id = q.Id, Name = q.Title, Detail = q.Level > 0 ? "level " + q.Level.ToString(CultureInfo.InvariantCulture) : "" };

        // Every word of the query must occur in the name. "npc:123", "obj:45", "quest:6" and a bare number find that id. Best first: names that start with the query,
        // then names with a word that starts with it, then the rest; then by name. `kind` limits the search to "npc", "obj" or "quest".
        public List<SeenHit> Search(string query, int max, string kind = null)
        {
            var res = new List<SeenHit>();
            query = (query ?? "").Trim().ToLowerInvariant();
            if (query.Length == 0 || max <= 0) return res;
            string idKind = null; string idText = query;
            int colon = query.IndexOf(':');
            if (colon > 0) { idKind = query.Substring(0, colon); idText = query.Substring(colon + 1).Trim(); if (idKind == "object") idKind = "obj"; }
            if (int.TryParse(idText, NumberStyles.None, CultureInfo.InvariantCulture, out int id) && (colon <= 0 || idKind == "npc" || idKind == "obj" || idKind == "quest"))
            {
                if ((idKind == null || idKind == "npc") && (kind == null || kind == "npc") && Npcs.TryGetValue(id, out var n)) res.Add(Hit(n));
                if ((idKind == null || idKind == "obj") && (kind == null || kind == "obj") && Objects.TryGetValue(id, out var o)) res.Add(Hit(o));
                if ((idKind == null || idKind == "quest") && (kind == null || kind == "quest") && Quests.TryGetValue(id, out var q)) res.Add(Hit(q));
                if (colon > 0) return res.Take(max).ToList();
            }
            var terms = query.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var found = new List<KeyValuePair<int, int>>();   // (rank, index)
            for (int i = 0; i < lower.Length; i++)
            {
                if (kind != null && hits[i].Kind != kind) continue;
                var s = lower[i];
                int rank = 2; bool all = true;
                for (int t = 0; t < terms.Length; t++)
                {
                    int at = s.IndexOf(terms[t], StringComparison.Ordinal);
                    if (at < 0) { all = false; break; }
                    if (t == 0) rank = at == 0 ? 0 : (s[at - 1] == ' ' || s[at - 1] == '-' || s[at - 1] == '\'' ? 1 : 2);
                }
                if (all) found.Add(new KeyValuePair<int, int>(rank, i));
            }
            found.Sort((a, b) => { int c = a.Key.CompareTo(b.Key); return c != 0 ? c : a.Value.CompareTo(b.Value); });   // the array is sorted by name already
            foreach (var f in found)
            {
                if (res.Count >= max) break;
                var h = hits[f.Value];
                if (res.Any(r => r.Kind == h.Kind && r.Id == h.Id)) continue;
                res.Add(h);
            }
            return res;
        }
    }

    // what the three files gave us, before it is turned into a snapshot
    public sealed class SeenSet
    {
        public List<WdbCreature> Npcs = new List<WdbCreature>();
        public List<WdbObject> Objects = new List<WdbObject>();
        public List<WdbQuest> Quests = new List<WdbQuest>();
        public SeenStamp[] Stamps = new SeenStamp[3];                        // creature, game object, quest file (null entry = no such file)
        public List<string> Notes = new List<string>();                       // a file that was skipped, a cut-off file ...
        public int Build;                                                     // the game build written in the files' headers (0 = unknown)
        public string Problem;                                                // set = nothing could be read
        public bool Ok => Problem == null;
    }

    public sealed class SeenStamp
    {
        public long Size, Ticks;
        public static SeenStamp Of(string path)
        {
            try { var fi = new FileInfo(path); return fi.Exists ? new SeenStamp { Size = fi.Length, Ticks = fi.LastWriteTimeUtc.Ticks } : null; }
            catch { return null; }
        }
        public static bool Same(SeenStamp a, SeenStamp b) => a == null ? b == null : b != null && a.Size == b.Size && a.Ticks == b.Ticks;
    }

    public static class SeenData
    {
        public enum State { NotStarted, Disabled, Loading, Ready, Unavailable }

        public const int FormatVersion = 2;          // bump when the parsing or the saved file changes meaning (2: the full quest answer, kill credits, all three type flags)
        public const string CreatureFile = "creaturecache.wdb", ObjectFile = "gameobjectcache.wdb", QuestFile = "questcache.wdb";

        // ---- hooks
        public static Func<string> WowRoot = () => null;
        public static Func<bool> Enabled = () => true;                     // the "Read item data from my WoW installation" switch
        public static Func<string> Flavor = () => IconStore.Flavor;
        public static bool AutoStart = true;                               // off in the self-test (it must not read the real game)
        public static string CacheFile = Path.Combine(Util.DataDir, "cache", "seendata.bin");
        public static event Action Changed;                                // on a worker thread, whenever the index or the state changed

        // ---- state
        static readonly object gate = new object();
        static SeenSnapshot snap;
        static volatile State state = State.NotStarted;
        static string reason;
        static bool running, again;
        public static long LastLoadMs { get; private set; }
        public static string LastLoadFrom { get; private set; }            // "cache file" | "game files"
        public static int LastBuild { get; private set; }
        public static State Status => state;
        public static string Reason => reason;
        public static SeenSnapshot Snapshot => snap;
        public static bool Ready => state == State.Ready && snap != null;
        public static int StartCountForTest;

        // ---- API
        public static WdbCreature Npc(int id) { WdbCreature n = null; snap?.Npcs.TryGetValue(id, out n); return n; }
        public static WdbObject Object(int id) { WdbObject o = null; snap?.Objects.TryGetValue(id, out o); return o; }
        public static WdbQuest Quest(int id) { WdbQuest q = null; snap?.Quests.TryGetValue(id, out q); return q; }
        public static List<SeenHit> Search(string query, int max, string kind = null) => snap == null ? new List<SeenHit>() : snap.Search(query, max, kind);
        public static int NpcCount => snap?.NpcCount ?? 0;
        public static int ObjectCount => snap?.ObjectCount ?? 0;
        public static int QuestCount => snap?.QuestCount ?? 0;

        static string N(int n) => n.ToString("N0", CultureInfo.InvariantCulture);

        // the line for Settings
        public static string StatusLine
        {
            get
            {
                switch (state)
                {
                    case State.Disabled: return "Seen data: off";
                    case State.Loading: return "Seen data: reading your WoW cache...";
                    case State.Ready: return "Seen data: " + N(NpcCount) + " NPCs, " + N(ObjectCount) + " objects, " + N(QuestCount) + " quests seen";
                    case State.Unavailable: return "Seen data: unavailable" + (string.IsNullOrEmpty(reason) ? "" : " (" + reason + ")");
                    default: return "Seen data: not read yet";
                }
            }
        }

        // what the Lodge sync shares (AtlasObs.SeenSource)
        sealed class Source : AtlasObs.ISeenSource
        {
            public IEnumerable<WdbCreature> Npcs() { var s = snap; return s == null ? (IEnumerable<WdbCreature>)new WdbCreature[0] : s.Npcs.Values.ToArray(); }
            public IEnumerable<WdbObject> Objects() { var s = snap; return s == null ? (IEnumerable<WdbObject>)new WdbObject[0] : s.Objects.Values.ToArray(); }
            public IEnumerable<WdbQuest> Quests() { var s = snap; return s == null ? (IEnumerable<WdbQuest>)new WdbQuest[0] : s.Quests.Values.ToArray(); }
        }
        public static readonly AtlasObs.ISeenSource AsSource = new Source();

        // ---------------------------------------------------------------------------------------------------------------------
        // the service: read in the background, never on the UI thread
        public const string JobId = "seen";

        public static void Start()
        {
            Interlocked.Increment(ref StartCountForTest);
            if (!SafeEnabled()) { Disable(); return; }
            lock (gate)
            {
                again = true;
                if (running) return;
                running = true;
            }
            if (snap == null) SetState(State.Loading, null);
            Task.Factory.StartNew(Worker, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        static bool SafeEnabled() { try { return Enabled(); } catch { return true; } }

        public static void Disable()
        {
            lock (gate) { snap = null; StopWatchingLocked(); }
            SetState(State.Disabled, null);
        }

        static void Worker()
        {
            try { Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; } catch { }
            while (true)
            {
                lock (gate) { if (!again) { running = false; return; } again = false; }
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    if (!SafeEnabled()) { Disable(); continue; }
                    GameDataProgress.Begin(JobId, "Seen creatures and quests", "Loading what your client has seen", 0.2);
                    LoadOnce();
                }
                catch (Exception e)
                {
                    Util.Log("seen data: " + e);
                    Publish(null, State.Unavailable, "could not be read: " + e.Message);
                }
                LastLoadMs = sw.ElapsedMilliseconds;
                GameDataProgress.End(JobId, state == State.Ready, state == State.Ready ? null : reason);
            }
        }

        static void Publish(SeenSnapshot s, State st, string why)
        {
            lock (gate) { snap = st == State.Ready ? s : null; reason = why; state = st; }
            try { Changed?.Invoke(); } catch { }
        }

        static void SetState(State st, string why)
        {
            lock (gate) { state = st; reason = why; }
            try { Changed?.Invoke(); } catch { }
        }

        public static void SetForTest(SeenSnapshot s, State st = State.Ready, string why = null) { lock (gate) { snap = s; state = st; reason = why; } }
        public static void ResetForTest() { lock (gate) { StopWatchingLocked(); snap = null; state = State.NotStarted; reason = null; running = false; again = false; } }

        // ---------------------------------------------------------------------------------------------------------------------
        static void LoadOnce()
        {
            var root = WowRoot();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) { Publish(null, State.Unavailable, "the WoW folder is not set"); return; }
            var dir = WdbDir(root, Flavor() ?? "_classic_beta_");
            if (dir == null) { Publish(null, State.Unavailable, "the game has not written its cache yet"); return; }
            Watch(dir);
            var saved = SeenIndexFile.TryRead(CacheFile);
            // no saved copy (first start, an older Outpost): the strip says so; a cache the game rewrote is read again without a word (that is every play session)
            if (saved == null || saved.Format != FormatVersion) GameDataProgress.Rebuilding(JobId, GameDataProgress.ReasonFor(CacheFile, null, null), "Reading what your client has seen", 0.5);
            else GameDataProgress.Step(JobId, "Reading what your client has seen", 0.3);
            var set = Obtain(saved, dir, CacheFile, out var from);
            if (!set.Ok) { Util.Log("seen data: unavailable: " + set.Problem); Publish(null, State.Unavailable, set.Problem); return; }
            foreach (var n in set.Notes) Util.Log("seen data: " + n);
            LastLoadFrom = from; LastBuild = set.Build;
            Publish(new SeenSnapshot(set.Npcs, set.Objects, set.Quests), State.Ready, null);
        }

        // The decision: the saved index is used when none of the three files changed (size and time), otherwise the files are read again and the index is saved.
        public static SeenSet Obtain(SeenIndexFile.Saved saved, string dir, string cacheFile, out string from)
        {
            var stamps = new[] { SeenStamp.Of(Path.Combine(dir, CreatureFile)), SeenStamp.Of(Path.Combine(dir, ObjectFile)), SeenStamp.Of(Path.Combine(dir, QuestFile)) };
            if (saved != null && saved.Format == FormatVersion && saved.Set.Ok && Enumerable.Range(0, 3).All(i => SeenStamp.Same(saved.Set.Stamps[i], stamps[i])))
            {
                from = "cache file";
                return saved.Set;
            }
            var set = ReadFiles(dir);
            if (set.Ok) SeenIndexFile.TryWrite(cacheFile, set);
            from = "game files";
            return set;
        }

        // the three files of one WDB folder. A file that is missing, damaged or in a layout this version does not know is left out (with a note);
        // when none of them could be used the set has a Problem.
        public static SeenSet ReadFiles(string dir)
        {
            var set = new SeenSet();
            int usable = 0, present = 0;
            // the stamp is taken before reading: a file the game rewrites in the meantime then simply looks changed at the next check
            set.Stamps[0] = SeenStamp.Of(Path.Combine(dir, CreatureFile)); set.Stamps[1] = SeenStamp.Of(Path.Combine(dir, ObjectFile)); set.Stamps[2] = SeenStamp.Of(Path.Combine(dir, QuestFile));
            void One<T>(string file, Func<byte[], WdbResult<T>> parse, List<T> into)
            {
                var path = Path.Combine(dir, file);
                if (!File.Exists(path)) return;
                present++;
                try
                {
                    var r = parse(Wdb.ReadShared(path));
                    if (!r.Recognised) { set.Notes.Add(file + ": layout not recognised (build " + r.Build + ", record version " + r.RecordVersion + ", " + r.Skipped + " of " + r.Records + " records did not fit): not used"); return; }
                    into.AddRange(r.Items);
                    usable++;
                    if (set.Build == 0) set.Build = r.Build;
                    if (r.Skipped > 0) set.Notes.Add(file + ": " + r.Skipped + " of " + r.Records + " records skipped");
                    if (r.Truncated) set.Notes.Add(file + ": ends early (the game was writing it); the complete records were used");
                }
                catch (Exception e) { set.Notes.Add(file + ": " + e.Message); }
            }
            One(CreatureFile, WdbSeen.Creatures, set.Npcs);
            One(ObjectFile, WdbSeen.Objects, set.Objects);
            One(QuestFile, WdbSeen.Quests, set.Quests);
            if (present == 0) set.Problem = "the game has not written its cache yet";
            else if (usable == 0) set.Problem = set.Notes.Count > 0 ? set.Notes[0] : "the cache files could not be read";
            return set;
        }

        // Cache\WDB\<locale> of this client folder (enUS when it has a creature cache, else the newest one that has)
        public static string WdbDir(string root, string flavor)
        {
            try
            {
                var wdb = Path.Combine(root, flavor, "Cache", "WDB");
                if (!Directory.Exists(wdb)) return null;
                var en = Path.Combine(wdb, "enUS");
                if (File.Exists(Path.Combine(en, CreatureFile))) return en;
                return Directory.GetDirectories(wdb).Where(d => File.Exists(Path.Combine(d, CreatureFile))).OrderByDescending(d => new FileInfo(Path.Combine(d, CreatureFile)).LastWriteTimeUtc).FirstOrDefault();
            }
            catch { return null; }
        }

        // ---------------------------------------------------------------------------------------------------------------------
        // the game rewrites the caches (on logout, on exit, now and then): look again after things have been quiet for a few seconds
        static FileSystemWatcher watcher;
        static string watchedDir;
        static Timer debounce;
        public static int DebounceMs = 4000;

        static void Watch(string dir) { lock (gate) WatchLocked(dir); }

        static void WatchLocked(string dir)
        {
            if (dir == watchedDir && (watcher != null || dir == null)) return;
            StopWatchingLocked();
            watchedDir = dir;
            if (dir == null) return;
            try
            {
                var fw = new FileSystemWatcher(dir, "*.wdb") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName, EnableRaisingEvents = false };
                FileSystemEventHandler h = (s, e) => FilesChanged(e.Name);
                fw.Changed += h; fw.Created += h; fw.Renamed += (s, e) => FilesChanged(e.Name);
                fw.EnableRaisingEvents = true;
                watcher = fw;
            }
            catch (Exception e) { Util.Log("seen data: cannot watch the WDB folder: " + e.Message); }
        }

        static void FilesChanged(string name)
        {
            if (name == null || !(name.Equals(CreatureFile, StringComparison.OrdinalIgnoreCase) || name.Equals(ObjectFile, StringComparison.OrdinalIgnoreCase) || name.Equals(QuestFile, StringComparison.OrdinalIgnoreCase))) return;
            lock (gate)
            {
                if (debounce == null) debounce = new Timer(_ => { if (SafeEnabled()) Start(); }, null, Timeout.Infinite, Timeout.Infinite);
                debounce.Change(DebounceMs, Timeout.Infinite);
            }
        }

        public static void WatchForTest(string dir) => Watch(dir);
        public static void StopWatching() { lock (gate) StopWatchingLocked(); }
        static void StopWatchingLocked()
        {
            try { watcher?.Dispose(); } catch { }
            watcher = null; watchedDir = null;
            try { debounce?.Dispose(); } catch { }
            debounce = null;
        }
    }

    // ============================================================================================================================
    // the compact copy of the index in the Outpost's data folder (gzip of a small binary format)
    public static class SeenIndexFile
    {
        public sealed class Saved { public int Format; public SeenSet Set; }
        const int Magic = 0x4E455345;     // "ESEN"

        public static bool TryWrite(string path, SeenSet set)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var tmp = path + ".tmp";
                var mem = new MemoryStream(1 << 20);
                using (var w = new BinaryWriter(mem, Encoding.UTF8, true))
                {
                    w.Write(Magic); w.Write(SeenData.FormatVersion); w.Write(set.Build);
                    for (int i = 0; i < 3; i++)
                    {
                        var s = set.Stamps[i];
                        w.Write(s != null);
                        if (s != null) { w.Write(s.Size); w.Write(s.Ticks); }
                    }
                    w.Write(set.Npcs.Count);
                    foreach (var n in set.Npcs)
                    {
                        w.Write(n.Id); w.Write(n.Name ?? ""); w.Write(n.Title ?? ""); w.Write((byte)n.Type); w.Write((short)n.Family); w.Write((byte)n.Rank); w.Write(n.DisplayId); w.Write(n.Flags0); w.Write(n.Flags1); w.Write(n.Flags2); w.Write(n.KillCredit1); w.Write(n.KillCredit2);
                        w.Write(n.QuestItems.Length); foreach (var q in n.QuestItems) w.Write(q);
                    }
                    w.Write(set.Objects.Count);
                    foreach (var o in set.Objects)
                    {
                        w.Write(o.Id); w.Write(o.Name ?? ""); w.Write(o.Type); w.Write(o.DisplayId);
                        foreach (var v in o.Data) w.Write(v);
                        w.Write(o.QuestItems.Length); foreach (var q in o.QuestItems) w.Write(q);
                    }
                    w.Write(set.Quests.Count);
                    foreach (var q in set.Quests)
                    {
                        w.Write(q.Id); w.Write(q.Type); w.Write(q.Level); w.Write(q.MinLevel); w.Write(q.Sort); w.Write(q.Title ?? ""); w.Write(q.Objectives ?? ""); w.Write(q.Description ?? "");
                        WriteQuestDetails(w, q);
                    }
                }
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var gz = new GZipStream(fs, CompressionMode.Compress))
                    gz.Write(mem.GetBuffer(), 0, (int)mem.Length);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                return true;
            }
            catch (Exception e) { Util.Log("seen data: cannot save the index: " + e.Message); return false; }
        }

        // null for a missing, damaged or foreign file (the caller then reads the game files)
        public static Saved TryRead(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var mem = new MemoryStream(1 << 20);
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var gz = new GZipStream(fs, CompressionMode.Decompress))
                    gz.CopyTo(mem);
                mem.Position = 0;
                using (var r = new BinaryReader(mem, Encoding.UTF8))
                {
                    if (r.ReadInt32() != Magic) return null;
                    var s = new Saved { Format = r.ReadInt32(), Set = new SeenSet() };
                    if (s.Format != SeenData.FormatVersion) return null;
                    s.Set.Build = r.ReadInt32();
                    for (int i = 0; i < 3; i++) s.Set.Stamps[i] = r.ReadBoolean() ? new SeenStamp { Size = r.ReadInt64(), Ticks = r.ReadInt64() } : null;
                    int n = Count(r);
                    for (int k = 0; k < n; k++)
                    {
                        var c = new WdbCreature { Id = r.ReadInt32(), Name = r.ReadString(), Title = r.ReadString(), Type = r.ReadByte(), Family = r.ReadInt16(), Rank = r.ReadByte(), DisplayId = r.ReadInt32(), Flags0 = r.ReadUInt32(), Flags1 = r.ReadUInt32(), Flags2 = r.ReadUInt32(), KillCredit1 = r.ReadInt32(), KillCredit2 = r.ReadInt32() };
                        int qc = Count(r, 64);
                        if (qc > 0) { c.QuestItems = new int[qc]; for (int j = 0; j < qc; j++) c.QuestItems[j] = r.ReadInt32(); }
                        s.Set.Npcs.Add(c);
                    }
                    n = Count(r);
                    for (int k = 0; k < n; k++)
                    {
                        var o = new WdbObject { Id = r.ReadInt32(), Name = r.ReadString(), Type = r.ReadInt32(), DisplayId = r.ReadInt32() };
                        for (int j = 0; j < WdbObject.Count; j++) o.Data[j] = r.ReadInt32();
                        int qc = Count(r, 64);
                        if (qc > 0) { o.QuestItems = new int[qc]; for (int j = 0; j < qc; j++) o.QuestItems[j] = r.ReadInt32(); }
                        s.Set.Objects.Add(o);
                    }
                    n = Count(r);
                    for (int k = 0; k < n; k++)
                    {
                        var q = new WdbQuest { Id = r.ReadInt32(), Type = r.ReadInt32(), Level = r.ReadInt32(), MinLevel = r.ReadInt32(), Sort = r.ReadInt32(), Title = r.ReadString(), Objectives = r.ReadString(), Description = r.ReadString() };
                        ReadQuestDetails(r, q);
                        s.Set.Quests.Add(q);
                    }
                    return s;
                }
            }
            catch (Exception) { return null; }
        }

        // the fields of the full quest answer (WdbQuest.Full)
        static void WriteQuestDetails(BinaryWriter w, WdbQuest q)
        {
            w.Write(q.Full);
            if (!q.Full) return;
            w.Write(q.Flags); w.Write(q.FlagsEx); w.Write(q.InfoId); w.Write(q.SuggestedGroup); w.Write(q.NextQuest); w.Write(q.Money); w.Write(q.RewardSpell); w.Write(q.StartItem); w.Write(q.Races);
            void Items(WdbItemCount[] a) { w.Write(a.Length); foreach (var x in a) { w.Write(x.Id); w.Write(x.Count); } }
            Items(q.Rewards); Items(q.Choices); Items(q.Provided);
            w.Write(q.RepFactions.Length); foreach (var f in q.RepFactions) w.Write(f);
            w.Write(q.Goals.Length); foreach (var g in q.Goals) { w.Write(g.Type); w.Write(g.ObjectId); w.Write(g.Amount); w.Write(g.Text ?? ""); }
            w.Write(q.AreaDescription ?? ""); w.Write(q.CompletionLog ?? "");
        }

        static void ReadQuestDetails(BinaryReader r, WdbQuest q)
        {
            q.Full = r.ReadBoolean();
            if (!q.Full) return;
            q.Flags = r.ReadInt32(); q.FlagsEx = r.ReadInt32(); q.InfoId = r.ReadInt32(); q.SuggestedGroup = r.ReadInt32(); q.NextQuest = r.ReadInt32(); q.Money = r.ReadInt32(); q.RewardSpell = r.ReadInt32(); q.StartItem = r.ReadInt32(); q.Races = r.ReadUInt64();
            WdbItemCount[] Items() { int c = Count(r, 64); var a = c == 0 ? WdbQuest.NoItems : new WdbItemCount[c]; for (int i = 0; i < c; i++) a[i] = new WdbItemCount { Id = r.ReadInt32(), Count = r.ReadInt32() }; return a; }
            q.Rewards = Items(); q.Choices = Items(); q.Provided = Items();
            int n = Count(r, 64); if (n > 0) { q.RepFactions = new int[n]; for (int i = 0; i < n; i++) q.RepFactions[i] = r.ReadInt32(); }
            n = Count(r, 64); if (n > 0) { q.Goals = new WdbObjective[n]; for (int i = 0; i < n; i++) q.Goals[i] = new WdbObjective { Type = r.ReadInt32(), ObjectId = r.ReadInt32(), Amount = r.ReadInt32(), Text = r.ReadString() }; }
            q.AreaDescription = r.ReadString(); q.CompletionLog = r.ReadString();
        }

        static int Count(BinaryReader r, int max = 2000000)
        {
            int n = r.ReadInt32();
            if (n < 0 || n > max) throw new InvalidDataException("count");
            return n;
        }
    }
}
