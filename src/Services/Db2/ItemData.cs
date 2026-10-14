using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ElansAddonHub.Services.Db2
{
    // ============================================================================================================================
    // Atlas, layer 1: item data. The Outpost reads the player's OWN WoW install, read-only, at runtime:
    //   * ItemSparse.db2 (+ Item.db2, RandPropPoints.db2) out of the local CASC storage = every item the client ships with
    //   * Cache\ADB\<locale>\DBCache.bin = the item rows the server pushed to this client (items that were hidden until somebody found them)
    // Nothing is shipped with the Outpost, nothing is uploaded and nothing is written to the WoW folder. The parsed index lives in memory; a compact
    // copy is kept in the Outpost's own data folder (cache\itemdata.bin) so the next start does not have to parse again.
    // ============================================================================================================================

    // an immutable index of all items: by id and by lower-case name
    public sealed class ItemSnapshot
    {
        public readonly Dictionary<int, ClientItem> Items;
        readonly ClientItem[] byName;       // named items, sorted by name
        readonly string[] lower;            // the same, lower case
        public readonly int Revealed;       // items that came from the hotfix cache
        public readonly int ClientCount;    // items that came from the shipped table (and were not replaced by the cache)

        public int Count => Items.Count;

        public ItemSnapshot(IEnumerable<ClientItem> client, IEnumerable<ClientItem> cache)
        {
            Items = new Dictionary<int, ClientItem>();
            foreach (var i in client ?? Enumerable.Empty<ClientItem>()) { i.Source = "client"; Items[i.Id] = i; }
            foreach (var i in cache ?? Enumerable.Empty<ClientItem>()) { i.Source = "cache"; Items[i.Id] = i; }   // the cache wins for the same id
            foreach (var i in Items.Values) if (i.Source == "cache") Revealed++; else ClientCount++;
            byName = Items.Values.Where(i => !string.IsNullOrWhiteSpace(i.Name) && !IsPlaceholder(i.Name)).OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Id).ToArray();
            lower = byName.Select(i => i.Name.ToLowerInvariant()).ToArray();
        }

        public ClientItem Get(int id) => Items.TryGetValue(id, out var i) ? i : null;

        static readonly System.Text.RegularExpressions.Regex JunkWords = new System.Text.RegularExpressions.Regex(@"deprecated|zz\s?old|\bunused\b|test item|placeholder|\bdnt\b|\[ph\]", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        static readonly System.Text.RegularExpressions.Regex JunkOld = new System.Text.RegularExpressions.Regex(@"^[\[\(\*\s]*OLD\b");   // upper case only: "Old Blanchy's Blanket" is a real item
        // items the client carries only as leftovers ("Deprecated ...", "OLD ...") are not offered by the search
        public static bool IsPlaceholder(string name) => JunkWords.IsMatch(name) || JunkOld.IsMatch(name);

        // Every word of the query must occur in the name (or the query is an item id). Best first: names that start with the query, then names with a
        // word that starts with it, then the rest; within a group the higher quality, then the name. `skip` leaves out items the caller already lists.
        public List<ClientItem> Search(string query, int max, Func<ClientItem, bool> skip = null)
        {
            var res = new List<ClientItem>();
            query = (query ?? "").Trim().ToLowerInvariant();
            if (query.Length == 0 || max <= 0) return res;
            if (int.TryParse(query, NumberStyles.None, CultureInfo.InvariantCulture, out int idq) && Items.TryGetValue(idq, out var byId) && (skip == null || !skip(byId))) res.Add(byId);
            var terms = query.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var hits = new List<KeyValuePair<int, int>>();   // (rank, index)
            for (int i = 0; i < lower.Length; i++)
            {
                var n = lower[i];
                int rank = 2;
                bool all = true;
                for (int t = 0; t < terms.Length; t++)
                {
                    int at = n.IndexOf(terms[t], StringComparison.Ordinal);
                    if (at < 0) { all = false; break; }
                    if (t == 0) rank = at == 0 ? 0 : (n[at - 1] == ' ' || n[at - 1] == '-' || n[at - 1] == '\'' ? 1 : 2);
                }
                if (all) hits.Add(new KeyValuePair<int, int>(rank, i));
            }
            hits.Sort((a, b) =>
            {
                int c = a.Key.CompareTo(b.Key);
                if (c != 0) return c;
                var x = byName[a.Value]; var y = byName[b.Value];
                c = y.Quality.CompareTo(x.Quality);
                return c != 0 ? c : a.Value.CompareTo(b.Value);   // byName is sorted by name already
            });
            foreach (var h in hits)
            {
                var it = byName[h.Value];
                if (res.Count >= max) break;
                if (res.Contains(it) || (skip != null && skip(it))) continue;
                res.Add(it);
            }
            return res;
        }
    }

    // what the game files gave us, before it is turned into a snapshot
    public sealed class ItemSet
    {
        public List<ClientItem> Client = new List<ClientItem>(), Cache = new List<ClientItem>();
        public ItemTables.Budgets Budgets;
        public uint SparseHash, ItemHash, BudgetHash;
        public string Problem;                       // set = unavailable (nothing else is valid)
        public List<string> Notes = new List<string>();   // partial results (no class names, no stat amounts ...)
        public bool Ok => Problem == null;
    }

    public static class ItemData
    {
        public enum State { NotStarted, Disabled, Loading, Ready, Unavailable }

        public const int FormatVersion = 2;          // bump when the parsing or the cache file changes meaning (2: weapon damage spread, flags and item set id)

        // ---- hooks
        public static Func<string> WowRoot = () => null;
        public static Func<bool> Enabled = () => true;
        public static Func<string> Flavor = () => IconStore.Flavor;
        public static bool AutoStart = true;         // off in the self-test (it must not read the real game)
        public static string CacheFile = Path.Combine(Util.DataDir, "cache", "itemdata.bin");
        public static event Action Changed;          // on a worker thread, whenever the index or the state changed

        // ---- state
        static readonly object gate = new object();
        static ItemSnapshot snap;
        static volatile State state = State.NotStarted;
        static string reason;
        static bool running, again;
        public static long LastLoadMs { get; private set; }
        public static string LastLoadFrom { get; private set; }     // "cache file" | "game files" | "cache file + DBCache.bin"
        public static State Status => state;
        public static string Reason => reason;
        public static ItemSnapshot Snapshot => snap;
        public static int Count => snap?.Count ?? 0;
        public static int Revealed => snap?.Revealed ?? 0;
        public static bool Ready => state == State.Ready && snap != null;

        public static ClientItem Get(int id) => snap?.Get(id);

        public static List<ClientItem> Search(string query, int max, Func<ClientItem, bool> skip = null) => snap == null ? new List<ClientItem>() : snap.Search(query, max, skip);

        // the line for Settings
        public static string StatusLine
        {
            get
            {
                switch (state)
                {
                    case State.Disabled: return "Item data: off";
                    case State.Loading: return "Item data: reading your WoW installation...";
                    case State.Ready: return "Item data: read from your WoW installation (" + Count.ToString("N0", CultureInfo.InvariantCulture) + " items, " + Revealed.ToString("N0", CultureInfo.InvariantCulture) + " revealed" + SpellData.StatusExtra + ")";
                    case State.Unavailable: return "Item data: unavailable" + (string.IsNullOrEmpty(reason) ? "" : " (" + reason + ")");
                    default: return "Item data: not read yet";
                }
            }
        }

        // ---------------------------------------------------------------------------------------------------------------------
        // the service: read in the background, never on the UI thread
        public static int StartCountForTest;       // how often a (re)load was asked for

        public static void Start()
        {
            Interlocked.Increment(ref StartCountForTest);
            if (!SafeEnabled()) { Disable(); return; }
            SpellData.Start();                       // spells, skills, factions and item details are read alongside (same switch, same WoW folder)
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
            snap = null;
            SpellData.Disable();
            StopWatching();
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
                    LoadOnce();
                }
                catch (Exception e)
                {
                    Util.Log("item data: " + e);
                    Publish(null, State.Unavailable, "could not be read: " + e.Message);
                }
                LastLoadMs = sw.ElapsedMilliseconds;
            }
        }

        static void Publish(ItemSnapshot s, State st, string why)
        {
            lock (gate) { snap = st == State.Ready ? s : null; reason = why; state = st; }
            try { Changed?.Invoke(); } catch { }
        }

        static void SetState(State st, string why)
        {
            lock (gate) { state = st; reason = why; }
            try { Changed?.Invoke(); } catch { }
        }

        // test hook: use this snapshot as the loaded data
        public static void SetForTest(ItemSnapshot s, State st = State.Ready, string why = null) { lock (gate) { snap = s; state = st; reason = why; } }
        public static void RaiseChangedForTest() { try { Changed?.Invoke(); } catch { } }
        public static void ResetForTest() { StopWatching(); lock (gate) { snap = null; state = State.NotStarted; reason = null; running = false; again = false; } }

        // ---------------------------------------------------------------------------------------------------------------------
        // one load: the saved index when it still matches this game build, otherwise the game files
        static void LoadOnce()
        {
            var root = WowRoot();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) { Publish(null, State.Unavailable, "the WoW folder is not set"); return; }
            var flavor = Flavor() ?? "_classic_beta_";
            var build = GameBuild(root, flavor);
            if (build == null) { Publish(null, State.Unavailable, "no .build.info in the WoW folder"); return; }
            var adb = AdbFile(root, flavor);
            long dbSize = 0, dbTicks = 0;
            if (adb != null) { try { var fi = new FileInfo(adb); dbSize = fi.Length; dbTicks = fi.LastWriteTimeUtc.Ticks; } catch { adb = null; } }
            WatchAdb(adb);

            var set = Obtain(ItemIndexFile.TryRead(CacheFile), build, dbSize, dbTicks, adb, CacheFile, () =>
            {
                RawTables raw;
                try { raw = ReadCasc(root, flavor); }
                catch (Exception e) { Util.Log("item data: game files: " + e.Message); return new ItemSet { Problem = "the game files could not be read: " + e.Message }; }
                return ParseTables(raw.Sparse, raw.Item, raw.Budget);
            }, out var from);
            if (!set.Ok) { Util.Log("item data: unavailable: " + set.Problem); Publish(null, State.Unavailable, set.Problem); return; }
            foreach (var n in set.Notes) Util.Log("item data: " + n);
            LastLoadFrom = from;
            Publish(new ItemSnapshot(set.Client, set.Cache), State.Ready, null);
        }

        // The decision: the saved index is used when the game build is the same (the shipped tables cannot have changed), and only the hotfix cache is
        // read again when DBCache.bin changed (size or time). Anything else = read the game files (readGame) and save the result.
        public static ItemSet Obtain(ItemIndexFile.Saved saved, string build, long dbSize, long dbTicks, string adb, string cacheFile, Func<ItemSet> readGame, out string from)
        {
            if (saved != null && saved.Build == build && saved.Format == FormatVersion && ItemTables.SparseFor(saved.Set.SparseHash) != null)
            {
                var set = saved.Set;
                if (saved.DbSize == dbSize && saved.DbTicks == dbTicks) { from = "cache file"; return set; }
                ReadCacheRows(set, adb);
                from = "cache file + DBCache.bin";
                ItemIndexFile.TryWrite(cacheFile, build, dbSize, dbTicks, set);
                return set;
            }
            var fresh = readGame();
            if (fresh.Ok) { ReadCacheRows(fresh, adb); ItemIndexFile.TryWrite(cacheFile, build, dbSize, dbTicks, fresh); }
            from = "game files";
            return fresh;
        }

        // ---------------------------------------------------------------------------------------------------------------------
        // the game's tables
        public sealed class RawTables { public byte[] Sparse, Item, Budget; }

        public static RawTables ReadCasc(string root, string flavor)
        {
            using (var casc = CascStorage.Open(root, flavor))
            {
                casc.ZeroFillEncrypted = true;     // key-locked parts read as zeros; the table reader skips those sections
                var keys = casc.ResolveFileIds(new HashSet<int> { ItemTables.ItemSparseFileDataId, ItemTables.ItemFileDataId, ItemTables.RandPropPointsFileDataId });
                byte[] Read(int fid) => keys.TryGetValue(fid, out var ck) ? casc.ReadByContentKey(ck) : null;
                var raw = new RawTables { Sparse = Read(ItemTables.ItemSparseFileDataId) };
                if (raw.Sparse == null) throw new IOException("ItemSparse.db2 is not in this game install");
                try { raw.Item = Read(ItemTables.ItemFileDataId); } catch (Exception e) { Util.Log("item data: Item.db2: " + e.Message); }
                try { raw.Budget = Read(ItemTables.RandPropPointsFileDataId); } catch (Exception e) { Util.Log("item data: RandPropPoints.db2: " + e.Message); }
                return raw;
            }
        }

        // the shipped items. A table whose layout this version does not know makes the whole thing unavailable: nothing is guessed.
        public static ItemSet ParseTables(byte[] sparse, byte[] item, byte[] budget)
        {
            var set = new ItemSet();
            Wdc5 w;
            try { w = Wdc5.Parse(sparse); }
            catch (Exception e) { set.Problem = "ItemSparse.db2 could not be read: " + e.Message; return set; }
            if (w.TableHash != ItemTables.ItemSparseHash || !w.Sparse) { set.Problem = "the item table is not the one this version expects"; return set; }
            var L = ItemTables.SparseFor(w.LayoutHash);
            if (L == null) { set.Problem = "game updated: item table layout " + w.LayoutHash.ToString("X8", CultureInfo.InvariantCulture) + " is not known to this Outpost yet"; return set; }
            set.SparseHash = w.LayoutHash;

            // budgets for the stat amounts (optional: without them the stats are left out, never guessed)
            if (budget != null)
            {
                try
                {
                    var bw = Wdc5.Parse(budget);
                    var bl = ItemTables.BudgetFor(bw.LayoutHash);
                    if (bw.TableHash == ItemTables.RandPropPointsHash && bl != null) { set.Budgets = ItemTables.Budgets.From(bw, bl); set.BudgetHash = bw.LayoutHash; }
                    else set.Notes.Add("RandPropPoints layout " + bw.LayoutHash.ToString("X8", CultureInfo.InvariantCulture) + " not known: no stat amounts");
                }
                catch (Exception e) { set.Notes.Add("RandPropPoints.db2: " + e.Message + ": no stat amounts"); }
            }
            else set.Notes.Add("RandPropPoints.db2 not found: no stat amounts");

            // class / subclass / icon (optional)
            Dictionary<int, int[]> classes = null;
            if (item != null)
            {
                try
                {
                    var iw = Wdc5.Parse(item);
                    var il = ItemTables.ItemFor(iw.LayoutHash);
                    if (iw.TableHash == ItemTables.ItemHash && il != null)
                    {
                        set.ItemHash = iw.LayoutHash;
                        classes = new Dictionary<int, int[]>(iw.Count);
                        for (int i = 0; i < iw.Count; i++)
                            classes[iw.IdAt(i)] = new[] { (int)iw.Field(i, il.ClassField), (int)iw.Field(i, il.SubclassField), (int)iw.Field(i, il.InventoryField), (int)iw.Field(i, il.IconField) };
                    }
                    else set.Notes.Add("Item layout " + iw.LayoutHash.ToString("X8", CultureInfo.InvariantCulture) + " not known: no item classes");
                }
                catch (Exception e) { set.Notes.Add("Item.db2: " + e.Message + ": no item classes"); }
            }
            if (w.EncryptedRecords > 0) set.Notes.Add(w.EncryptedRecords + " item rows are key-locked in the game files and skipped");

            set.Client = new List<ClientItem>(w.Count);
            for (int i = 0; i < w.Count; i++)
            {
                var seg = w.Raw(i);
                ClientItem it;
                try { it = ItemTables.ParseSparse(w.IdAt(i), seg.Array, seg.Offset, seg.Count, L, set.Budgets, "client"); }
                catch (Exception) { continue; }
                if (it == null) continue;
                if (classes != null && classes.TryGetValue(it.Id, out var c)) { it.ClassId = c[0]; it.SubclassId = c[1]; it.IconId = c[3]; }
                ItemTables.ResolveStats(it, set.Budgets);
                set.Client.Add(it);
            }
            return set;
        }

        // the hotfix cache's ItemSparse rows (+ their Item rows for class and icon) into set.Cache; a missing or unreadable cache = no revealed items
        public static void ReadCacheRows(ItemSet set, string adbFile)
        {
            set.Cache = new List<ClientItem>();
            if (adbFile == null) return;
            var L = ItemTables.SparseFor(set.SparseHash);
            if (L == null) return;
            try
            {
                var file = DbCache.ReadAllShared(adbFile);
                set.Cache = ParseCache(file, L, set.Budgets, ItemTables.ItemFor(set.ItemHash));
            }
            catch (Exception e) { Util.Log("item data: DBCache.bin: " + e.Message); set.Notes.Add("DBCache.bin: " + e.Message); }
        }

        public static List<ClientItem> ParseCache(byte[] file, ItemTables.SparseLayout L, ItemTables.Budgets budgets, ItemTables.ItemLayout itemLayout)
        {
            var res = new List<ClientItem>();
            var rows = DbCache.Read(file, ItemTables.ItemSparseHash);
            Dictionary<int, byte[]> itemRows = itemLayout == null ? null : DbCache.Read(file, ItemTables.ItemHash).Rows;
            foreach (var kv in rows.Rows)
            {
                ClientItem it;
                try { it = ItemTables.ParseSparse(kv.Key, kv.Value, 0, kv.Value.Length, L, budgets, "cache"); }
                catch (Exception) { continue; }
                if (it == null) continue;
                if (itemRows != null && itemRows.TryGetValue(kv.Key, out var r) && r.Length >= itemLayout.CacheRowLength)
                {
                    it.ClassId = BitConverter.ToInt32(r, 0); it.SubclassId = r[4]; it.IconId = BitConverter.ToInt32(r, 13);
                    if (it.InventoryType == 0) it.InventoryType = r[6];
                }
                ItemTables.ResolveStats(it, budgets);
                res.Add(it);
            }
            return res;
        }

        // ---------------------------------------------------------------------------------------------------------------------
        // where things are
        // a string that changes when the game is patched: product, version and the build key from .build.info
        public static string GameBuild(string root, string flavor)
        {
            try
            {
                var info = Path.Combine(root, ".build.info");
                if (!File.Exists(info)) return null;
                string[] lines;
                using (var fs = new FileStream(info, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) using (var sr = new StreamReader(fs)) lines = sr.ReadToEnd().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length < 2) return null;
                var cols = lines[0].Split('|').Select(c => c.Split('!')[0]).ToList();
                int iBuild = cols.IndexOf("Build Key"), iProd = cols.IndexOf("Product"), iVer = cols.IndexOf("Version");
                if (iBuild < 0 || iProd < 0) return null;
                var want = CascStorage.ProductFor(flavor);
                var rows = lines.Skip(1).Select(l => l.Split('|')).Where(r => r.Length > Math.Max(iBuild, iProd)).ToList();
                var row = rows.FirstOrDefault(r => r[iProd] == want) ?? rows.FirstOrDefault(r => r[iProd].StartsWith("wow", StringComparison.Ordinal));
                if (row == null) return null;
                return row[iProd] + "|" + (iVer >= 0 && iVer < row.Length ? row[iVer] : "") + "|" + row[iBuild];
            }
            catch { return null; }
        }

        // Cache\ADB\<locale>\DBCache.bin of this client folder (enUS when there is one, else the newest)
        public static string AdbFile(string root, string flavor)
        {
            try
            {
                var adb = Path.Combine(root, flavor, "Cache", "ADB");
                if (!Directory.Exists(adb)) return null;
                var en = Path.Combine(adb, "enUS", "DBCache.bin");
                if (File.Exists(en)) return en;
                return Directory.GetDirectories(adb).Select(d => Path.Combine(d, "DBCache.bin")).Where(File.Exists).OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc).FirstOrDefault();
            }
            catch { return null; }
        }

        // ---------------------------------------------------------------------------------------------------------------------
        // DBCache.bin changes (the game learned new items): look again after things have been quiet for a few seconds
        static FileSystemWatcher watcher;
        static string watchedDir;
        static Timer debounce;
        public static int DebounceMs = 4000;

        static void WatchAdb(string adbFile)
        {
            lock (gate)
            {
                var dir = adbFile == null ? null : Path.GetDirectoryName(adbFile);
                if (dir == watchedDir && (watcher != null || dir == null)) return;
                StopWatchingLocked();
                watchedDir = dir;
                if (dir == null) return;
                try
                {
                    var fw = new FileSystemWatcher(dir, "DBCache.bin") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName, EnableRaisingEvents = false };
                    FileSystemEventHandler h = (s, e) => CacheChanged();
                    fw.Changed += h; fw.Created += h; fw.Renamed += (s, e) => CacheChanged();
                    fw.EnableRaisingEvents = true;
                    watcher = fw;
                }
                catch (Exception e) { Util.Log("item data: cannot watch DBCache.bin: " + e.Message); }
            }
        }

        static void CacheChanged()
        {
            lock (gate)
            {
                if (debounce == null) debounce = new Timer(_ => { if (SafeEnabled()) Start(); }, null, Timeout.Infinite, Timeout.Infinite);
                debounce.Change(DebounceMs, Timeout.Infinite);
            }
        }

        public static void WatchForTest(string adbFile) => WatchAdb(adbFile);

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
    // the compact copy of the index in the Outpost's data folder (gzip of a small binary format). Keyed by the game build; the hotfix cache part
    // is keyed by DBCache.bin's size and time, so a changed cache only re-reads that one file.
    public static class ItemIndexFile
    {
        public sealed class Saved { public int Format; public string Build; public long DbSize, DbTicks; public ItemSet Set; }

        public static bool TryWrite(string path, string build, long dbSize, long dbTicks, ItemSet set)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var tmp = path + ".tmp";
                var mem = new MemoryStream(1 << 20);
                using (var w = new BinaryWriter(mem, Encoding.UTF8, true))
                {
                    w.Write(0x4D544945);                         // "EITM"
                    w.Write(ItemData.FormatVersion);
                    w.Write(build ?? "");
                    w.Write(dbSize); w.Write(dbTicks);
                    w.Write(set.SparseHash); w.Write(set.ItemHash); w.Write(set.BudgetHash);
                    w.Write(set.Budgets != null);
                    if (set.Budgets != null)
                    {
                        w.Write(set.Budgets.ByLevel.Count);
                        foreach (var kv in set.Budgets.ByLevel) { w.Write(kv.Key); foreach (var v in kv.Value) w.Write(v); }
                    }
                    WriteItems(w, set.Client);
                    WriteItems(w, set.Cache);
                }
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var gz = new GZipStream(fs, CompressionMode.Compress))
                    gz.Write(mem.GetBuffer(), 0, (int)mem.Length);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                return true;
            }
            catch (Exception e) { Util.Log("item data: cannot save the index: " + e.Message); return false; }
        }

        static void WriteItems(BinaryWriter w, List<ClientItem> items)
        {
            w.Write(items.Count);
            foreach (var i in items)
            {
                w.Write(i.Id); w.Write(i.Name ?? ""); w.Write(i.Description ?? "");
                w.Write((byte)i.Quality); w.Write((short)i.ItemLevel); w.Write((short)i.RequiredLevel); w.Write((byte)i.InventoryType);
                w.Write((short)i.ClassId); w.Write((short)i.SubclassId); w.Write(i.IconId); w.Write((byte)i.Bonding); w.Write(i.Stackable);
                w.Write(i.DelayMs); w.Write((byte)i.ContainerSlots); w.Write(i.SellPrice); w.Write(i.BuyPrice);
                w.Write(i.DmgVariance); w.Write(i.Flags2); w.Write((ushort)i.ItemSetId);
                if (i.Stats == null) w.Write(-1);
                else { w.Write(i.Stats.Length); foreach (var s in i.Stats) w.Write(s); }
            }
        }

        // null for a missing, damaged or foreign file (the caller then reads the game files)
        public static Saved TryRead(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                // unzip into memory first: reading small values straight from the zip stream is slow
                var mem = new MemoryStream(1 << 20);
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var gz = new GZipStream(fs, CompressionMode.Decompress))
                    gz.CopyTo(mem);
                mem.Position = 0;
                using (var r = new BinaryReader(mem, Encoding.UTF8))
                {
                    if (r.ReadInt32() != 0x4D544945) return null;
                    var s = new Saved { Format = r.ReadInt32(), Build = r.ReadString(), DbSize = r.ReadInt64(), DbTicks = r.ReadInt64(), Set = new ItemSet() };
                    if (s.Format != ItemData.FormatVersion) return null;
                    s.Set.SparseHash = r.ReadUInt32(); s.Set.ItemHash = r.ReadUInt32(); s.Set.BudgetHash = r.ReadUInt32();
                    if (r.ReadBoolean())
                    {
                        var b = new ItemTables.Budgets();
                        int n = r.ReadInt32();
                        if (n < 0 || n > 100000) return null;
                        for (int k = 0; k < n; k++) { int lvl = r.ReadInt32(); var a = new int[15]; for (int j = 0; j < 15; j++) a[j] = r.ReadInt32(); b.ByLevel[lvl] = a; }
                        s.Set.Budgets = b;
                    }
                    s.Set.Client = ReadItems(r, "client");
                    s.Set.Cache = ReadItems(r, "cache");
                    return s;
                }
            }
            catch (Exception) { return null; }
        }

        static List<ClientItem> ReadItems(BinaryReader r, string source)
        {
            int n = r.ReadInt32();
            if (n < 0 || n > 5000000) throw new InvalidDataException("item count");
            var l = new List<ClientItem>(n);
            for (int k = 0; k < n; k++)
            {
                var i = new ClientItem { Id = r.ReadInt32(), Name = r.ReadString(), Description = r.ReadString(), Source = source };
                i.Quality = r.ReadByte(); i.ItemLevel = r.ReadInt16(); i.RequiredLevel = r.ReadInt16(); i.InventoryType = r.ReadByte();
                i.ClassId = r.ReadInt16(); i.SubclassId = r.ReadInt16(); i.IconId = r.ReadInt32(); i.Bonding = r.ReadByte(); i.Stackable = r.ReadInt32();
                i.DelayMs = r.ReadInt32(); i.ContainerSlots = r.ReadByte(); i.SellPrice = r.ReadInt64(); i.BuyPrice = r.ReadInt64();
                i.DmgVariance = r.ReadSingle(); i.Flags2 = r.ReadInt32(); i.ItemSetId = r.ReadUInt16();
                int sc = r.ReadInt32();
                if (sc < 0) i.Stats = null;
                else if (sc == 0) i.Stats = ClientItem.NoStats;
                else { if (sc > 64) throw new InvalidDataException("stat count"); i.Stats = new int[sc]; for (int s = 0; s < sc; s++) i.Stats[s] = r.ReadInt32(); }
                l.Add(i);
            }
            return l;
        }
    }
}
