using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.StaticFiles;

namespace Lodge;

public class FileTooBigException : Exception { }

// "Can't take this upload now" - storage full, too many files, too many uploads at once, upload rate. Status 507/429.
public class UploadRefusedException : Exception
{
    public int Status { get; }
    public UploadRefusedException(int status, string message) : base(message) { Status = status; }
}

public class FileMeta
{
    public string Id { get; set; }
    public string Name { get; set; }
    public long Size { get; set; }
    public string Mime { get; set; }
    public long At { get; set; }
    public string By { get; set; }
}

// Uploaded files: data/files/<id>.bin + <id>.json, deleted after LODGE_FILE_DAYS.
// Bounded: total bytes (LODGE_MAX_STORAGE_MB), file count (LODGE_MAX_FILES), uploads running at once
// (LODGE_MAX_CONCURRENT_UPLOADS) and uploads per person (LODGE_UPLOADS_PER_10MIN). Space is reserved before
// writing (Content-Length, or the per-file maximum when unknown) so parallel uploads can't all pass the same check;
// the reservation shrinks to the real size when done and is released on any failure. Existing files are never
// deleted to make room - a full store refuses new uploads until old ones expire.
public class FileStore
{
    readonly LodgeConfig cfg;
    readonly IClock clock;
    readonly string dir;
    readonly SemaphoreSlim slots;
    readonly object gate = new();
    long usedBytes, reservedBytes;
    int fileCount, reservedFiles;
    readonly ConcurrentDictionary<string, Queue<DateTime>> recent = new();
    readonly Dictionary<string, int> running = new(); // uploads in flight per identity (under gate)
    static readonly FileExtensionContentTypeProvider Types = new();
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public FileStore(LodgeConfig cfg, IClock clock = null)
    {
        this.cfg = cfg;
        this.clock = clock ?? SystemClock.Instance;
        dir = Path.Combine(cfg.DataDir, "files");
        Directory.CreateDirectory(dir);
        slots = new SemaphoreSlim(cfg.MaxConcurrentUploads, cfg.MaxConcurrentUploads);
        Reconcile();
    }

    public long UsedBytes { get { lock (gate) return usedBytes; } }
    public int FileCount { get { lock (gate) return fileCount; } }
    public long ReservedBytes { get { lock (gate) return reservedBytes; } }

    // startup: partial uploads from a crash are removed, a .bin without metadata (or the other way round) too,
    // then the real usage is counted
    void Reconcile()
    {
        foreach (var f in Directory.GetFiles(dir, "*.part")) TryDelete(f);
        foreach (var f in Directory.GetFiles(dir, "*.json.tmp")) TryDelete(f);
        long used = 0;
        int count = 0;
        foreach (var bin in Directory.GetFiles(dir, "*.bin"))
        {
            var meta = Path.ChangeExtension(bin, ".json");
            if (!File.Exists(meta)) { TryDelete(bin); continue; }
            used += new FileInfo(bin).Length;
            count++;
        }
        foreach (var meta in Directory.GetFiles(dir, "*.json"))
            if (!File.Exists(Path.ChangeExtension(meta, ".bin"))) TryDelete(meta);
        lock (gate) { usedBytes = used; fileCount = count; }
    }

    static bool ValidId(string id) => id != null && id.Length == 32 && id.All(Uri.IsHexDigit);

    public string PathOf(string id) => Path.Combine(dir, id + ".bin");

    public FileMeta Get(string id)
    {
        if (!ValidId(id)) return null;
        var meta = Path.Combine(dir, id + ".json");
        if (!File.Exists(meta) || !File.Exists(PathOf(id))) return null;
        try
        {
            var m = JsonSerializer.Deserialize<FileMeta>(File.ReadAllText(meta), Json);
            if (m != null) m.Name = Clean(m.Name); // files stored by older servers may carry a name that isn't safe everywhere
            return m;
        }
        catch { return null; }
    }

    // 2.22: a member left / was removed: the files they uploaded stay (they belong to the lodge and expire on their own) but no longer name them
    public int Anonymise(string login, string former)
    {
        int n = 0;
        foreach (var f in Directory.GetFiles(dir, "*.json"))
        {
            try
            {
                var m = JsonSerializer.Deserialize<FileMeta>(File.ReadAllText(f), Json);
                if (m == null || !Names.Same(m.By, login)) continue;
                m.By = former;
                File.WriteAllText(f + ".tmp", JsonSerializer.Serialize(m, Json));
                File.Move(f + ".tmp", f, true);
                n++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return n;
    }

    // per person (guests: per IP) - a rolling 10 minute window
    void CheckRate(string who)
    {
        var now = clock.UtcNow;
        var q = recent.GetOrAdd(who, _ => new Queue<DateTime>());
        lock (q)
        {
            while (q.Count > 0 && now - q.Peek() > TimeSpan.FromMinutes(10)) q.Dequeue();
            if (q.Count >= cfg.UploadsPer10Min) throw new UploadRefusedException(429, "Too many uploads - try again in a few minutes");
            q.Enqueue(now);
        }
    }

    public async Task<FileMeta> Save(Stream body, long? declaredLength, string name, string by, string rateKey, CancellationToken ct)
    {
        if (declaredLength > cfg.MaxFileBytes) throw new FileTooBigException();
        // 2.22: the same program / script block as the shared Files module (a chat upload is just as easy to double-click)
        if (FileRules.IsBlockedName(Clean(name))) throw new UploadRefusedException(415, "Programs and scripts (." + FileRules.Extension(Clean(name)) + ") can't be shared here");
        // one person (guests: one address) can't hold all the slots: at most MaxUploadsPerIdentity at once
        var who = rateKey ?? "?";
        lock (gate)
        {
            running.TryGetValue(who, out var n0);
            if (n0 >= cfg.MaxUploadsPerIdentity) throw new UploadRefusedException(429, $"You already have {n0} uploads running - wait for one to finish");
            running[who] = n0 + 1;
        }
        bool slotTaken = false;
        long reserved = 0;
        bool fileReserved = false;
        string part = null;
        // an upload that crawls (a stalled or very slow sender) is cut off after UploadTimeoutSeconds
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(cfg.UploadTimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var origCt = ct;
        ct = linked.Token;
        try
        {
            if (!await slots.WaitAsync(0, ct)) throw new UploadRefusedException(429, "Too many uploads at once - try again in a moment");
            slotTaken = true;
            CheckRate(who);
            reserved = declaredLength ?? cfg.MaxFileBytes;
            lock (gate)
            {
                if (fileCount + reservedFiles >= cfg.MaxFiles)
                    throw new UploadRefusedException(507, "The lodge's file storage is full (too many files) - old files expire after a while");
                if (usedBytes + reservedBytes + reserved > cfg.MaxStorageBytes)
                    throw new UploadRefusedException(507, "The lodge's file storage is full - old files expire after a while");
                reservedBytes += reserved;
                reservedFiles++;
                fileReserved = true;
            }

            name = Clean(name);
            var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            part = Path.Combine(dir, id + ".part");
            long size = 0;
            await using (var fs = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buf = new byte[81920];
                int n;
                while ((n = await body.ReadAsync(buf, ct)) > 0)
                {
                    size += n;
                    if (size > cfg.MaxFileBytes || size > reserved) throw new FileTooBigException(); // never past the reservation
                    await fs.WriteAsync(buf.AsMemory(0, n), ct);
                }
                await fs.FlushAsync(ct);
            }
            if (!Types.TryGetContentType(name, out var mime)) mime = "application/octet-stream";
            var meta = new FileMeta { Id = id, Name = name, Size = size, Mime = mime, At = new DateTimeOffset(clock.UtcNow).ToUnixTimeMilliseconds(), By = by };

            // metadata first (tmp + rename), then the content: a crash in between leaves something Reconcile removes
            var metaPath = Path.Combine(dir, id + ".json");
            await File.WriteAllTextAsync(metaPath + ".tmp", JsonSerializer.Serialize(meta, Json), ct);
            File.Move(metaPath + ".tmp", metaPath);
            File.Move(part, PathOf(id));
            part = null;
            lock (gate)
            {
                reservedBytes -= reserved;
                reservedFiles--;
                usedBytes += size;
                fileCount++;
                reserved = 0;
                fileReserved = false;
            }
            return meta;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !origCt.IsCancellationRequested)
        {
            throw new UploadRefusedException(408, "The upload took too long and was stopped");
        }
        finally
        {
            if (part != null)
            {
                TryDelete(part);
                TryDelete(Path.Combine(dir, Path.GetFileNameWithoutExtension(part) + ".json.tmp"));
                TryDelete(Path.Combine(dir, Path.GetFileNameWithoutExtension(part) + ".json"));
            }
            lock (gate)
            {
                if (fileReserved) { reservedBytes -= reserved; reservedFiles--; }
                if (running.TryGetValue(who, out var left)) { if (left <= 1) running.Remove(who); else running[who] = left - 1; }
            }
            if (slotTaken) slots.Release();
        }
    }

    static void TryDelete(string f) { try { if (File.Exists(f)) File.Delete(f); } catch { } }

    // see Names.FileName: no path parts, no Windows-invalid or invisible characters, no device names
    static string Clean(string name) => Names.FileName(name);

    // expired uploads go; their space and slot come back
    public int Expire()
    {
        int removed = 0;
        var cutoff = clock.UtcNow.AddDays(-cfg.FileDays);
        foreach (var meta in Directory.GetFiles(dir, "*.json"))
        {
            var bin = Path.ChangeExtension(meta, ".bin");
            DateTime written;
            try { written = File.GetLastWriteTimeUtc(File.Exists(bin) ? bin : meta); } catch { continue; }
            if (written >= cutoff) continue;
            long size = 0;
            try { if (File.Exists(bin)) size = new FileInfo(bin).Length; } catch { }
            TryDelete(bin);
            TryDelete(meta);
            if (!File.Exists(bin) && !File.Exists(meta))
            {
                lock (gate) { usedBytes -= size; fileCount--; }
                removed++;
            }
        }
        // forget rate windows nobody used for a while
        foreach (var kv in recent)
            lock (kv.Value) if (kv.Value.Count == 0 || clock.UtcNow - kv.Value.Peek() > TimeSpan.FromMinutes(10)) recent.TryRemove(kv.Key, out _);
        return removed;
    }

    public void StartCleanup(CancellationToken stop)
    {
        _ = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try { Expire(); } catch { }
                try { await Task.Delay(TimeSpan.FromHours(1), stop); } catch { return; }
            }
        });
    }
}
