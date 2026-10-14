using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Lodge;

// "Can't do that" with the HTTP status and a short machine-readable reason (dataset uploads and downloads).
public sealed class DatasetException : Exception
{
    public int Status { get; }
    public string Reason { get; }
    public DatasetException(int status, string reason, string message) : base(message) { Status = status; Reason = reason; }
}

// What the lodge holds of one dataset. The content is a single file (a zip bundle) stored under a server-generated name.
public sealed class DatasetMeta
{
    public string Name { get; set; }
    public string Version { get; set; }      // opaque to the server: the client's own idea of "which upstream release" (shown and compared by the Outposts)
    public long Size { get; set; }
    public string Sha256 { get; set; }
    public string UploadedBy { get; set; }   // the login name; shown as the display name
    public long At { get; set; }
    public string Blob { get; set; }         // 32 hex, never derived from anything a client sent
}

// One kind of dataset the lodge may host: a name from this fixed list (a client can never invent a name), a size limit and a cheap check
// of the bundle. The server never executes or parses what is inside.
public sealed class DatasetKind
{
    public string Name { get; init; }
    public string Feature { get; init; }                     // advertised in welcome / GET /features while this kind is enabled
    public Func<LodgeConfig, bool> Enabled { get; init; }
    public Func<LodgeConfig, long> MaxBytes { get; init; }
    public Func<LodgeConfig, int> DownloadsPerHour { get; init; }
    public Func<string, string> Check { get; init; }        // path of the received file -> null = fine, otherwise why it is refused
}

public static class DatasetKinds
{
    public static readonly DatasetKind Classic = new()
    {
        Name = "classic", Feature = "classic",
        Enabled = c => c.ClassicEnabled,
        MaxBytes = c => c.ClassicMaxMb * 1024L * 1024L,
        DownloadsPerHour = c => c.ClassicDownloadsPerHour,
        Check = ClassicBundle.Check,
    };

    // the one place a new hosted dataset is added
    public static readonly DatasetKind[] All = { Classic };

    public static DatasetKind Find(string name) => All.FirstOrDefault(k => string.Equals(k.Name, name, StringComparison.Ordinal));
}

// The Classic baseline bundle: the original upstream files (cMaNGOS classic-db), unchanged, in one zip:
//   ClassicDB_*.sql.gz (exactly one), LICENSE.md, COPYRIGHT.md, AUTHORS, source.json, and updates/*.sql (optional).
// Only the names, the sizes and the first two bytes of the dump (the gzip magic) are looked at. Nothing is extracted or read as SQL.
public static class ClassicBundle
{
    static readonly Regex DumpRx = new(@"^ClassicDB_[A-Za-z0-9_.\-]+\.sql\.gz$", RegexOptions.CultureInvariant);
    static readonly Regex UpdateRx = new(@"^updates/[A-Za-z0-9_.,+\-]+\.sql$", RegexOptions.CultureInvariant);
    static readonly string[] Required = { "LICENSE.md", "COPYRIGHT.md", "AUTHORS", "source.json" };
    public const int MaxEntries = 2000;
    public const long MaxUnpackedBytes = 400L * 1024 * 1024;   // the sum of what the entries announce
    public const long MaxSourceJsonBytes = 16 * 1024;

    // null = fine, otherwise the plain reason
    public static string Check(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false);
            if (zip.Entries.Count > MaxEntries) return "The bundle has too many files";
            string dump = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            long total = 0;
            foreach (var e in zip.Entries)
            {
                var n = e.FullName;
                if (n == "updates/") continue;                                  // the folder entry
                if (!seen.Add(n)) return "The bundle lists a file twice";
                total += e.Length;
                if (total > MaxUnpackedBytes) return "The bundle is too big when unpacked";
                if (DumpRx.IsMatch(n))
                {
                    if (dump != null) return "The bundle holds more than one dump";
                    dump = n;
                    if (e.Length < 100) return "The dump is empty";
                    using var s = e.Open();
                    var head = new byte[2];
                    int got = 0, r;
                    while (got < 2 && (r = s.Read(head, got, 2 - got)) > 0) got += r;
                    if (got != 2 || head[0] != 0x1F || head[1] != 0x8B) return "The dump is not a gzip file";
                }
                else if (UpdateRx.IsMatch(n)) { }
                else if (Array.IndexOf(Required, n) >= 0)
                {
                    if (n == "source.json" && (e.Length == 0 || e.Length > MaxSourceJsonBytes)) return "source.json has an unexpected size";
                    if (n != "source.json" && (e.Length == 0 || e.Length > 1024 * 1024)) return n + " has an unexpected size";
                }
                else return "The bundle holds a file that does not belong in it";
            }
            if (dump == null) return "The bundle has no ClassicDB dump";
            foreach (var r in Required) if (!seen.Contains(r)) return "The bundle is missing " + r;
            return null;
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException or IOException)
        {
            return "That is not a valid bundle";
        }
    }
}

// Hosted datasets (2.17, features such as "classic"): small shared reference data the lodge stores once so every member's Outpost can fetch it from the lodge
// instead of the internet. One slot per dataset name, replaced as a whole (an upload replaces the previous one atomically; a failed upload changes nothing).
//  Who: members and up (veteran, officer, owner) read and download; officers and the owner upload, replace and delete. Guests do neither.
//  Transport: the bytes travel over authenticated HTTP, streamed to a .part file and never held in memory: PUT /datasets/{name} (headers X-Version, X-Sha256,
//  Content-Length required), GET /datasets/{name}. Info and delete are WebSocket messages (dataset.info / dataset.delete); every change is announced with dataset.changed.
public sealed class DatasetsModule : IModule
{
    public const int MaxVersionChars = 200;

    readonly LodgeHub hub;
    readonly string dir;
    readonly string indexPath;
    readonly object gate = new();
    readonly List<DatasetMeta> items = new();
    readonly HashSet<string> uploading = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, Queue<DateTime>> recent = new();       // downloads, per name + person, rolling hour
    readonly ConcurrentDictionary<string, Queue<DateTime>> recentUploads = new();  // uploads, per person, rolling 10 minutes
    readonly SemaphoreSlim concurrent = new(3, 3);
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public DatasetsModule(LodgeHub hub)
    {
        this.hub = hub;
        dir = Path.Combine(hub.Cfg.DataDir, "datasets");
        indexPath = Path.Combine(dir, "index.json");
        if (Directory.Exists(dir)) Load();
    }

    public bool Enabled(DatasetKind k) => k != null && k.Enabled(hub.Cfg);
    public string BlobPath(DatasetMeta m) => Path.Combine(dir, m.Blob + ".bin");
    public IReadOnlyList<string> DiskFiles() => Directory.Exists(dir) ? Directory.GetFiles(dir).Select(Path.GetFileName).ToList() : new List<string>();
    public long UsedBytes { get { lock (gate) return items.Sum(i => i.Size); } }

    // ---------------------------------------------------------------- storage

    static bool ValidBlob(string b) => b != null && b.Length == 32 && b.All(Uri.IsHexDigit);
    static void TryDelete(string f) { try { if (File.Exists(f)) File.Delete(f); } catch { } }
    static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    void Load()
    {
        foreach (var f in Directory.GetFiles(dir, "*.part")) TryDelete(f);
        foreach (var f in Directory.GetFiles(dir, "*.tmp")) TryDelete(f);
        try
        {
            if (File.Exists(indexPath))
                items.AddRange(JsonSerializer.Deserialize<List<DatasetMeta>>(File.ReadAllText(indexPath), Json) ?? new());
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            hub.Log.LogWarning("Hosted datasets: the index could not be read ({Error})", e.GetType().Name);
        }
        // forget entries whose content is gone or whose name is not a known dataset; remove blobs nothing refers to
        bool changed = items.RemoveAll(i => DatasetKinds.Find(i.Name) == null || !ValidBlob(i.Blob) || !File.Exists(BlobPath(i))) > 0;
        var live = items.Select(i => i.Blob + ".bin").ToHashSet();
        foreach (var f in Directory.GetFiles(dir, "*.bin"))
            if (!live.Contains(Path.GetFileName(f))) TryDelete(f);
        if (changed) SaveIndexLocked();
    }

    // temp file + rename: the index is either the old or the new one, never half written
    bool SaveIndexLocked()
    {
        try
        {
            Directory.CreateDirectory(dir);
            var tmp = indexPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(items, Json));
            File.Move(tmp, indexPath, true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            hub.Log.LogWarning("Hosted datasets: the index could not be saved ({Error})", e.GetType().Name);
            return false;
        }
    }

    // ---------------------------------------------------------------- views

    string Shown(string login)
    {
        var dn = hub.Profiles.Peek(ProfileRules.Key(login))?.DisplayName;
        return string.IsNullOrEmpty(dn) ? (login ?? "") : dn;
    }

    JsonObject View(DatasetKind kind, DatasetMeta m)
    {
        var o = new JsonObject { ["name"] = kind.Name, ["available"] = m != null };
        if (m != null)
        {
            o["version"] = m.Version; o["sha256"] = m.Sha256; o["size"] = m.Size; o["uploadedBy"] = Shown(m.UploadedBy); o["at"] = m.At;
        }
        return o;
    }

    public JsonObject Info(DatasetKind kind, Member me)
    {
        DatasetMeta m;
        lock (gate) m = items.FirstOrDefault(i => i.Name == kind.Name);
        var o = View(kind, m);
        o["t"] = "dataset.data";
        o["canManage"] = me != null && Roles.Can(me.Role, Perm.DatasetManage);
        o["maxMb"] = (int)(kind.MaxBytes(hub.Cfg) / (1024 * 1024));
        return o;
    }

    public DatasetMeta Peek(string name) { lock (gate) return items.FirstOrDefault(i => i.Name == name); }

    // ---------------------------------------------------------------- upload

    // rolling 10 minutes per person
    void CheckUploadRate(string who)
    {
        var now = hub.Clock.UtcNow;
        var q = recentUploads.GetOrAdd(who, _ => new Queue<DateTime>());
        lock (q)
        {
            while (q.Count > 0 && now - q.Peek() > TimeSpan.FromMinutes(10)) q.Dequeue();
            if (q.Count >= 5) throw new DatasetException(429, "rate", "Too many uploads - try again in a few minutes");
            q.Enqueue(now);
        }
    }

    public async Task<JsonObject> Upload(string by, string role, string rateKey, string name, string version, string sha256, long? length, Stream body, CancellationToken ct)
    {
        var kind = DatasetKinds.Find(name);
        if (kind == null) throw new DatasetException(404, "unknown", "That dataset doesn't exist");
        if (!Enabled(kind)) throw new DatasetException(403, "disabled", "This lodge has switched that off");
        if (!Roles.Can(role, Perm.DatasetManage)) throw new DatasetException(403, "forbidden", "Only officers and the lodge owner can share this with the lodge");
        sha256 = (sha256 ?? "").Trim().ToLowerInvariant();
        if (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit)) throw new DatasetException(400, "bad-sha", "The upload needs its SHA-256");
        if (length == null) throw new DatasetException(411, "length", "The upload needs a Content-Length");
        if (length <= 0) throw new DatasetException(400, "empty", "That file is empty");
        long max = kind.MaxBytes(hub.Cfg);
        if (length > max) throw new DatasetException(413, "too-big", $"The file is larger than {max / (1024 * 1024)} MB");
        version = Names.Text(version ?? "", MaxVersionChars);
        if (version.Length == 0) throw new DatasetException(400, "bad-version", "The upload needs its version");
        lock (gate)
        {
            if (!uploading.Add(kind.Name)) throw new DatasetException(429, "busy", "An upload of this is already running");
        }
        bool slotTaken = false;
        string part = null;
        try
        {
            CheckUploadRate(rateKey);
            if (!await concurrent.WaitAsync(0, ct)) throw new DatasetException(429, "busy", "Too many uploads at once - try again in a moment");
            slotTaken = true;
            Directory.CreateDirectory(dir);
            var blob = NewId();
            part = Path.Combine(dir, blob + ".part");
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            long size = 0;
            string actual;
            try
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using (var fs = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    var buf = new byte[81920];
                    int n;
                    while ((n = await body.ReadAsync(buf, linked.Token)) > 0)
                    {
                        size += n;
                        if (size > length) throw new DatasetException(400, "length", "The upload is longer than it announced");
                        hash.AppendData(buf, 0, n);
                        await fs.WriteAsync(buf.AsMemory(0, n), linked.Token);
                    }
                    await fs.FlushAsync(linked.Token);
                }
                actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw new DatasetException(408, "timeout", "The upload took too long and was stopped");
            }
            if (size != length) throw new DatasetException(400, "length", "The upload is shorter than it announced");
            if (actual != sha256) throw new DatasetException(400, "sha-mismatch", "The file arrived damaged (checksum differs) - try again");
            var why = kind.Check(part);
            if (why != null) throw new DatasetException(415, "bad-bundle", why);

            // commit: content to its server-generated name, then the index (tmp + rename), then the previous content goes
            DatasetMeta old; DatasetMeta fresh; bool replaced;
            lock (gate)
            {
                File.Move(part, Path.Combine(dir, blob + ".bin"));
                part = null;
                fresh = new DatasetMeta { Name = kind.Name, Version = version, Size = size, Sha256 = actual, UploadedBy = by, At = new DateTimeOffset(hub.Clock.UtcNow).ToUnixTimeMilliseconds(), Blob = blob };
                old = items.FirstOrDefault(i => i.Name == kind.Name);
                replaced = old != null;
                if (old != null) items.Remove(old);
                items.Add(fresh);
                if (!SaveIndexLocked())
                {
                    // undo: nothing changed from the outside
                    items.Remove(fresh);
                    if (old != null) items.Add(old);
                    TryDelete(Path.Combine(dir, blob + ".bin"));
                    throw new DatasetException(500, "save", "The server couldn't store the file");
                }
            }
            if (old != null) TryDelete(BlobPath(old));
            hub.Log.LogInformation("{Name} shared the {Dataset} dataset with the lodge ({Size} bytes, {Action})", by, kind.Name, size, replaced ? "replaced" : "new");
            _ = Announce(kind, fresh, replaced ? "replace" : "new");
            return View(kind, fresh);
        }
        finally
        {
            if (part != null) TryDelete(part);
            lock (gate) uploading.Remove(kind.Name);
            if (slotTaken) concurrent.Release();
        }
    }

    // ---------------------------------------------------------------- download

    // rolling hour per dataset and person
    void CheckDownloadRate(DatasetKind kind, string who)
    {
        var now = hub.Clock.UtcNow;
        var q = recent.GetOrAdd(kind.Name + "|" + who, _ => new Queue<DateTime>());
        lock (q)
        {
            while (q.Count > 0 && now - q.Peek() > TimeSpan.FromHours(1)) q.Dequeue();
            if (q.Count >= kind.DownloadsPerHour(hub.Cfg)) throw new DatasetException(429, "rate", "You already fetched this a few times - try again in a while");
            q.Enqueue(now);
        }
    }

    // opened under the lock, so a delete or replace a moment later can't pull the file out from under the reader (on Linux the handle stays valid)
    public FileStream OpenForDownload(string name, string role, string rateKey, out DatasetMeta meta)
    {
        meta = null;
        var kind = DatasetKinds.Find(name);
        if (kind == null) throw new DatasetException(404, "unknown", "That dataset doesn't exist");
        if (!Enabled(kind)) throw new DatasetException(403, "disabled", "This lodge has switched that off");
        if (!Roles.Can(role, Perm.DatasetRead)) throw new DatasetException(403, "forbidden", "This is for lodge members");
        lock (gate)
        {
            meta = items.FirstOrDefault(i => i.Name == kind.Name);
            if (meta == null) throw new DatasetException(404, "missing", "The lodge doesn't hold that");
            CheckDownloadRate(kind, rateKey);
            try { return new FileStream(BlobPath(meta), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true); }
            catch (IOException) { throw new DatasetException(404, "missing", "The lodge doesn't hold that"); }
        }
    }

    // ---------------------------------------------------------------- delete

    public (bool ok, string why) Delete(Member me, string name)
    {
        var kind = DatasetKinds.Find(name);
        if (kind == null) return (false, "unknown");
        if (!Enabled(kind)) return (false, "disabled");
        if (!Roles.Can(me.Role, Perm.DatasetManage)) return (false, "forbidden");
        DatasetMeta gone;
        lock (gate)
        {
            gone = items.FirstOrDefault(i => i.Name == kind.Name);
            if (gone == null) return (false, "missing");
            items.Remove(gone);
            if (!SaveIndexLocked()) { items.Add(gone); return (false, "save"); }
        }
        TryDelete(BlobPath(gone));
        return (true, null);
    }

    // ---------------------------------------------------------------- messages

    // only people who may read it hear about a change
    Task Announce(DatasetKind kind, DatasetMeta m, string action)
    {
        var o = View(kind, m);
        o["t"] = "dataset.changed"; o["action"] = action;
        return hub.Broadcast(o, who => Roles.Can(who.Role, Perm.DatasetRead) && who.Personal);
    }

    public async Task<bool> Handle(Member me, string type, JsonObject m)
    {
        switch (type)
        {
            case "dataset.info":
            {
                var kind = DatasetKinds.Find(J.Str(m, "name"));
                if (kind == null || !Enabled(kind)) { await hub.Error(me, "That isn't available on this lodge", "disabled"); return true; }
                if (!me.Personal || !Roles.Can(me.Role, Perm.DatasetRead)) { await hub.Error(me, "This is for lodge members", "guest"); return true; }
                if (!me.Budget.Page.TryTake()) { me.CountDrop(hub.Clock); await hub.Error(me, "Slow down a little", "rate"); return true; }
                await hub.Send(me, Info(kind, me));
                return true;
            }
            case "dataset.delete":
            {
                var name = J.Str(m, "name") ?? "";
                var (ok, why) = Delete(me, name);
                var r = new JsonObject { ["t"] = "dataset.result", ["op"] = "delete", ["name"] = name, ["ok"] = ok };
                if (why != null) r["why"] = why;
                await hub.Send(me, r);
                if (ok)
                {
                    hub.Log.LogInformation("{Name} took the {Dataset} dataset off the lodge", me.Name, name);
                    await Announce(DatasetKinds.Find(name), null, "delete");
                }
                return true;
            }
        }
        return false;
    }

    // housekeeping: forget rate windows nobody used for a while
    public void Tick()
    {
        var now = hub.Clock.UtcNow;
        foreach (var kv in recent) lock (kv.Value) if (kv.Value.Count == 0 || now - kv.Value.Peek() > TimeSpan.FromHours(1)) recent.TryRemove(kv.Key, out _);
        foreach (var kv in recentUploads) lock (kv.Value) if (kv.Value.Count == 0 || now - kv.Value.Peek() > TimeSpan.FromMinutes(10)) recentUploads.TryRemove(kv.Key, out _);
    }
}
