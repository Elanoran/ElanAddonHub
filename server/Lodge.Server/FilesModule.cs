using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Elans.Shared;

namespace Lodge;

// "Can't do that" with the HTTP status and a short machine-readable reason (files.* uploads and downloads).
public sealed class FilesException : Exception
{
    public int Status { get; }
    public string Reason { get; }
    public FilesException(int status, string reason, string message) : base(message) { Status = status; Reason = reason; }
}

public sealed class SlotVersion
{
    public int V { get; set; }
    public string FileName { get; set; }
    public long Size { get; set; }
    public string Sha256 { get; set; }
    public string UploadedBy { get; set; }
    public long At { get; set; }
    public string Note { get; set; }
    public string Blob { get; set; }     // server-generated file name on disk (32 hex) - never derived from anything a client sent
}

public sealed class Slot
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string CreatedBy { get; set; }
    public int NextV { get; set; } = 1;  // version numbers are never reused (not even after a roll back)
    public List<SlotVersion> Versions { get; set; } = new();
}

// What a file may be, independent of storage: the extension block and the zip listing.
public static class FileRules
{
    public static readonly HashSet<string> Blocked = new(StringComparer.OrdinalIgnoreCase)
    {
        "exe", "dll", "com", "bat", "cmd", "ps1", "psm1", "vbs", "vbe", "js", "jse", "wsf", "wsh", "msi", "msp", "scr", "cpl",
        "jar", "lnk", "reg", "hta", "sys", "drv", "pif", "application", "appref-ms",
    };

    public const int MaxZipEntries = 20000;

    // the last extension, ignoring trailing dots/spaces ("setup.exe. " is still setup.exe on Windows); "" when none
    public static string Extension(string name)
    {
        name = (name ?? "").Trim(' ', '.', '\t');
        var dot = name.LastIndexOf('.');
        var slash = name.LastIndexOfAny(new[] { '/', '\\' });
        return dot > slash && dot >= 0 ? name[(dot + 1)..].Trim().ToLowerInvariant() : "";
    }

    public static bool IsBlockedName(string name) => Blocked.Contains(Extension(name));

    public static bool IsZip(string name) => Extension(name) == "zip";

    // an entry path that would escape the folder it is unpacked into (or write to an alternate stream / another drive)
    public static bool UnsafeEntryPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return true;
        if (path.Contains('\0') || path.Contains(':')) return true;
        var p = path.Replace('\\', '/');
        if (p.StartsWith('/')) return true;
        if (p.Length >= 2 && char.IsLetter(p[0]) && p[1] == ':') return true;
        foreach (var seg in p.Split('/'))
            if (seg == "..") return true;
        return false;
    }

    // null = fine, otherwise why the zip is refused. Lists the entries only - nothing is extracted.
    public static string CheckZip(Stream seekable)
    {
        try
        {
            using var zip = new ZipArchive(seekable, ZipArchiveMode.Read, leaveOpen: true);
            if (zip.Entries.Count > MaxZipEntries) return "The zip has too many entries";
            foreach (var e in zip.Entries)
            {
                if (UnsafeEntryPath(e.FullName)) return "The zip contains an unsafe path";
                if (e.FullName.EndsWith('/') || e.FullName.EndsWith('\\')) continue; // folder entry
                if (IsBlockedName(e.Name)) return "The zip contains a program or script file (." + Extension(e.Name) + ")";
            }
            return null;
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException or IOException)
        {
            return "That is not a valid zip file";
        }
    }
}

// Shared files (2.14, feature "files"). Named slots; each holds up to KeptVersions newest versions on disk.
//  Who: Perm.FilesRead (everybody) lists and saves; Perm.FilesManage (officers and the owner) uploads a new slot, replaces
//  (= a new version), rolls back and deletes.
//  Transport: the bytes travel over authenticated HTTP (same device token / code headers as /files), streamed to a .part file
//  and never held in memory: PUT /slots/new or PUT /slots/{id} (headers X-Slot-Name, X-File-Name, X-Sha256, X-Note, all URL-encoded
//  text; Content-Length required), GET /slots/{id}/{v}. Listing, delete and roll back are WebSocket messages
//  (files.list / files.delete / files.rollback); every change is announced to everybody with files.changed. The socket messages are
//  typed in shared/Elans.Shared/Lodge/FileMessages.cs.
public sealed class FilesModule : IModule
{
    public const int KeptVersions = 3;
    public const int MaxSlots = 200;
    public const int MaxNameChars = 60;
    public const int MaxNoteChars = 200;

    readonly LodgeHub hub;
    readonly string dir;
    readonly string indexPath;
    readonly object gate = new();
    readonly List<Slot> slots = new();
    long reservedBytes;
    readonly Dictionary<string, int> running = new();
    readonly ConcurrentDictionary<string, Queue<DateTime>> recent = new();
    readonly SemaphoreSlim concurrent = new(3, 3);
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public FilesModule(LodgeHub hub)
    {
        this.hub = hub;
        dir = Path.Combine(hub.Cfg.DataDir, "slots");
        indexPath = Path.Combine(dir, "index.json");
        if (hub.Cfg.FilesEnabled || Directory.Exists(dir)) Load();
    }

    public bool Enabled => hub.Cfg.FilesEnabled;

    public long UsedBytes { get { lock (gate) return slots.Sum(s => s.Versions.Sum(v => v.Size)); } }
    public int SlotCount { get { lock (gate) return slots.Count; } }
    public string BlobPath(SlotVersion v) => Path.Combine(dir, v.Blob + ".bin");
    public IReadOnlyList<string> DiskFiles() => Directory.Exists(dir) ? Directory.GetFiles(dir).Select(Path.GetFileName).ToList() : new List<string>();

    // ---------------------------------------------------------------- storage

    static bool ValidBlob(string b) => b != null && b.Length == 32 && b.All(Uri.IsHexDigit);

    void Load()
    {
        Directory.CreateDirectory(dir);
        foreach (var f in Directory.GetFiles(dir, "*.part")) TryDelete(f);
        foreach (var f in Directory.GetFiles(dir, "*.tmp")) TryDelete(f);
        try
        {
            if (File.Exists(indexPath))
                slots.AddRange(JsonSerializer.Deserialize<List<Slot>>(File.ReadAllText(indexPath), Json) ?? new());
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            hub.Log.LogWarning("Shared files: the index could not be read ({Error})", e.GetType().Name);
        }
        // forget versions whose content is gone, slots with nothing left; remove blobs nothing refers to
        bool changed = false;
        foreach (var s in slots.ToList())
        {
            s.Versions ??= new();
            if (s.Versions.RemoveAll(v => !ValidBlob(v.Blob) || !File.Exists(BlobPath(v))) > 0) changed = true;
            if (s.Versions.Count == 0) { slots.Remove(s); changed = true; }
        }
        var live = slots.SelectMany(s => s.Versions).Select(v => v.Blob + ".bin").ToHashSet();
        foreach (var f in Directory.GetFiles(dir, "*.bin"))
            if (!live.Contains(Path.GetFileName(f))) TryDelete(f);
        if (changed) SaveIndexLocked();
    }

    // temp file + rename: the index is either the old or the new one, never half written
    bool SaveIndexLocked()
    {
        try
        {
            var tmp = indexPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(slots, Json));
            File.Move(tmp, indexPath, true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            hub.Log.LogWarning("Shared files: the index could not be saved ({Error})", e.GetType().Name);
            return false;
        }
    }

    // 2.22: a member left / was removed: shared files stay (they belong to the lodge) but no longer name them as the uploader / creator
    public int Anonymise(string login, string former)
    {
        int n = 0;
        lock (gate)
        {
            foreach (var s in slots)
            {
                if (Names.Same(s.CreatedBy, login)) { s.CreatedBy = former; n++; }
                foreach (var v in s.Versions) if (Names.Same(v.UploadedBy, login)) { v.UploadedBy = former; n++; }
            }
            if (n > 0) SaveIndexLocked();
        }
        return n;
    }

    static void TryDelete(string f) { try { if (File.Exists(f)) File.Delete(f); } catch { } }

    static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    // ---------------------------------------------------------------- views

    // the views are typed in shared/Elans.Shared/Lodge/FileMessages.cs (FileVersion, FileSlot, FilesDataMsg); the HTTP upload answers a FileSlot too
    static FileVersion VersionOf(SlotVersion v) => new()
    {
        V = v.V, FileName = v.FileName, Size = v.Size, Sha256 = v.Sha256, UploadedBy = v.UploadedBy, At = v.At, Note = v.Note ?? "",
    };

    static FileSlot SlotOf(Slot s) => new()
    {
        Id = s.Id, Name = s.Name, CreatedBy = s.CreatedBy, Versions = s.Versions.OrderBy(v => v.V).Select(VersionOf).ToList(),
    };

    static JsonObject SlotJson(Slot s) => LodgeJson.Build(SlotOf(s).Write);

    public JsonObject ListJson(Member me)
    {
        lock (gate)
            return LodgeJson.Build(new FilesDataMsg
            {
                MaxMb = hub.Cfg.FilesMaxMb, TotalMb = hub.Cfg.FilesTotalMb, UsedBytes = slots.Sum(s => s.Versions.Sum(v => v.Size)),
                Keep = KeptVersions, CanManage = Roles.Can(me.Role, Perm.FilesManage),
                Slots = slots.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Select(SlotOf).ToList(),
            }.Write);
    }

    public JsonObject SlotView(string id)
    {
        lock (gate) { var s = Find(id); return s == null ? null : SlotJson(s); }
    }

    Slot Find(string id) => slots.FirstOrDefault(s => s.Id == id);

    // ---------------------------------------------------------------- upload

    static string Clean(string name, int max, string what, bool required)
    {
        var t = Names.Text(name ?? "", max);
        if (required && t.Length == 0) throw new FilesException(400, "bad-" + what, "A " + what + " is needed");
        return t;
    }

    // rolling 10 minute window per person
    void CheckRate(string who)
    {
        var now = hub.Clock.UtcNow;
        var q = recent.GetOrAdd(who, _ => new Queue<DateTime>());
        lock (q)
        {
            while (q.Count > 0 && now - q.Peek() > TimeSpan.FromMinutes(10)) q.Dequeue();
            if (q.Count >= hub.Cfg.FilesUploadsPer10Min) throw new FilesException(429, "rate", "Too many uploads - try again in a few minutes");
            q.Enqueue(now);
        }
    }

    // slotId null = a new slot called slotName. Returns the slot as the clients see it.
    public async Task<JsonObject> Upload(string by, string role, string rateKey, string slotId, string slotName, string fileName, string note,
        string sha256, long? length, Stream body, CancellationToken ct)
    {
        var cfg = hub.Cfg;
        if (!Enabled) throw new FilesException(403, "disabled", "File sharing is switched off on this lodge");
        if (!Roles.Can(role, Perm.FilesManage)) throw new FilesException(403, "forbidden", "Only officers and the lodge owner can upload files");
        sha256 = (sha256 ?? "").Trim().ToLowerInvariant();
        if (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit)) throw new FilesException(400, "bad-sha", "The upload needs its SHA-256");
        if (length == null) throw new FilesException(411, "length", "The upload needs a Content-Length");
        if (length <= 0) throw new FilesException(400, "empty", "That file is empty");
        if (length > cfg.FilesMaxBytes) throw new FilesException(413, "too-big", $"File is larger than {cfg.FilesMaxMb} MB");
        fileName = Names.FileName(fileName);
        if (FileRules.IsBlockedName(fileName)) throw new FilesException(415, "blocked-type", "Programs and scripts (." + FileRules.Extension(fileName) + ") can't be shared here");
        note = Clean(note, MaxNoteChars, "note", false);
        bool isNew = slotId == null;
        string cleanName = null;
        if (isNew) cleanName = Clean(slotName, MaxNameChars, "name", true);
        long reserve = length.Value;
        lock (gate)
        {
            if (isNew)
            {
                if (slots.Count >= MaxSlots) throw new FilesException(507, "too-many", "There are too many slots - delete one first");
                if (slots.Any(s => string.Equals(s.Name, cleanName, StringComparison.OrdinalIgnoreCase))) throw new FilesException(409, "name-taken", "A slot with that name exists - upload a new version to it instead");
            }
            else
            {
                var s = Find(slotId);
                if (s == null) throw new FilesException(404, "missing", "That slot doesn't exist any more");
            }
            if (slots.Sum(s => s.Versions.Sum(v => v.Size)) + reservedBytes + length.Value - FreedByPrune(slotId) > cfg.FilesTotalBytes)
                throw new FilesException(507, "full", $"The lodge's file space ({cfg.FilesTotalMb} MB) is full - delete something first");
            if (running.TryGetValue(rateKey, out var n0) && n0 >= 1) throw new FilesException(429, "busy", "You already have an upload running");
            running[rateKey] = 1;
            reservedBytes += reserve;
        }
        bool slotTaken = false;
        string part = null;
        try
        {
            CheckRate(rateKey);
            if (!await concurrent.WaitAsync(0, ct)) throw new FilesException(429, "busy", "Too many uploads at once - try again in a moment");
            slotTaken = true;
            Directory.CreateDirectory(dir);
            var blob = NewId();
            part = Path.Combine(dir, blob + ".part");
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
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
                        if (size > length) throw new FilesException(400, "length", "The upload is longer than it announced");
                        hash.AppendData(buf, 0, n);
                        await fs.WriteAsync(buf.AsMemory(0, n), linked.Token);
                    }
                    await fs.FlushAsync(linked.Token);
                }
                actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw new FilesException(408, "timeout", "The upload took too long and was stopped");
            }
            if (size != length) throw new FilesException(400, "length", "The upload is shorter than it announced");
            if (actual != sha256) throw new FilesException(400, "sha-mismatch", "The file arrived damaged (checksum differs) - try again");
            if (FileRules.IsZip(fileName))
            {
                string why;
                await using (var zs = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.Read))
                    why = FileRules.CheckZip(zs);
                if (why != null) throw new FilesException(415, "blocked-zip", why);
            }

            // commit: content to its server-generated name, then the index (tmp + rename), then old versions go
            var pruned = new List<SlotVersion>();
            JsonObject view;
            string action; int ver; string slotNameOut; string id;
            lock (gate)
            {
                Slot s;
                if (isNew)
                {
                    if (slots.Count >= MaxSlots || slots.Any(x => string.Equals(x.Name, cleanName, StringComparison.OrdinalIgnoreCase)))
                        throw new FilesException(409, "name-taken", "A slot with that name exists - upload a new version to it instead");
                    s = new Slot { Id = NewId(), Name = cleanName, CreatedBy = by };
                }
                else
                {
                    s = Find(slotId) ?? throw new FilesException(404, "missing", "That slot doesn't exist any more");
                }
                if (slots.Sum(x => x.Versions.Sum(v => v.Size)) + size - (isNew ? 0 : FreedByPrune(s.Id)) > cfg.FilesTotalBytes)
                    throw new FilesException(507, "full", $"The lodge's file space ({cfg.FilesTotalMb} MB) is full - delete something first");
                File.Move(part, Path.Combine(dir, blob + ".bin"));
                part = null;
                var v = new SlotVersion
                {
                    V = s.NextV, FileName = fileName, Size = size, Sha256 = actual, UploadedBy = by, Note = note, Blob = blob,
                    At = new DateTimeOffset(hub.Clock.UtcNow).ToUnixTimeMilliseconds(),
                };
                var before = s.Versions.ToList();
                s.NextV++;
                s.Versions.Add(v);
                s.Versions.Sort((a, b) => a.V.CompareTo(b.V));
                while (s.Versions.Count > KeptVersions) { pruned.Add(s.Versions[0]); s.Versions.RemoveAt(0); }
                if (isNew) slots.Add(s);
                if (!SaveIndexLocked())
                {
                    // undo in memory and on disk: nothing changed from the outside
                    s.NextV--; s.Versions.Clear(); s.Versions.AddRange(before);
                    if (isNew) slots.Remove(s);
                    TryDelete(Path.Combine(dir, blob + ".bin"));
                    throw new FilesException(500, "save", "The server couldn't store the file");
                }
                view = SlotJson(s); action = isNew ? "new" : "version"; ver = v.V; slotNameOut = s.Name; id = s.Id;
            }
            foreach (var p in pruned) TryDelete(BlobPath(p));
            hub.Log.LogInformation("{Name} shared a file ({Action}, {Size} bytes)", by, action, size);
            hub.Badges.Note(by, "files");
            _ = Announce(action, id, slotNameOut, ver, by);
            return view;
        }
        finally
        {
            if (part != null) TryDelete(part);
            lock (gate)
            {
                reservedBytes -= reserve;
                running.Remove(rateKey);
            }
            if (slotTaken) concurrent.Release();
        }
    }

    // the oldest version leaves when the slot already holds KeptVersions: its space is free again for the new one
    long FreedByPrune(string slotId)
    {
        var s = slotId == null ? null : Find(slotId);
        return s != null && s.Versions.Count >= KeptVersions ? s.Versions.OrderBy(v => v.V).First().Size : 0;
    }

    // ---------------------------------------------------------------- download

    // opened under the lock, so a delete a moment later can't pull the file out from under the reader (on Linux the handle stays valid)
    public FileStream OpenVersion(string slotId, int v, out SlotVersion ver)
    {
        ver = null;
        if (!Enabled) throw new FilesException(403, "disabled", "File sharing is switched off on this lodge");
        lock (gate)
        {
            var s = Find(slotId);
            ver = s?.Versions.FirstOrDefault(x => x.V == v);
            if (ver == null) throw new FilesException(404, "missing", "That file isn't there any more");
            try { return new FileStream(BlobPath(ver), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true); }
            catch (IOException) { throw new FilesException(404, "missing", "That file isn't there any more"); }
        }
    }

    // ---------------------------------------------------------------- delete / roll back

    public (bool ok, string why) Delete(Member me, string slotId, out string name)
    {
        name = null;
        if (!Enabled) return (false, "disabled");
        if (!Roles.Can(me.Role, Perm.FilesManage)) return (false, "forbidden");
        List<SlotVersion> gone;
        lock (gate)
        {
            var s = Find(slotId);
            if (s == null) return (false, "missing");
            gone = s.Versions.ToList();
            name = s.Name;
            slots.Remove(s);
            if (!SaveIndexLocked()) { slots.Add(s); return (false, "save"); }
        }
        foreach (var v in gone) TryDelete(BlobPath(v));
        return (true, null);
    }

    // version v becomes the newest again: every newer kept version is removed (version numbers are not reused)
    public (bool ok, string why) Rollback(Member me, string slotId, int v, out string name, out int current)
    {
        name = null; current = 0;
        if (!Enabled) return (false, "disabled");
        if (!Roles.Can(me.Role, Perm.FilesManage)) return (false, "forbidden");
        List<SlotVersion> gone;
        lock (gate)
        {
            var s = Find(slotId);
            if (s == null) return (false, "missing");
            var target = s.Versions.FirstOrDefault(x => x.V == v);
            if (target == null) return (false, "missing");
            name = s.Name; current = v;
            gone = s.Versions.Where(x => x.V > v).ToList();
            if (gone.Count == 0) return (false, "already-newest");
            var before = s.Versions.ToList();
            s.Versions.RemoveAll(x => x.V > v);
            if (!SaveIndexLocked()) { s.Versions.Clear(); s.Versions.AddRange(before); return (false, "save"); }
        }
        foreach (var x in gone) TryDelete(BlobPath(x));
        return (true, null);
    }

    // ---------------------------------------------------------------- messages

    Task Announce(string action, string id, string name, int? v, string by) => hub.Broadcast(LodgeJson.Build(new FilesChangedMsg
    {
        Action = action, Slot = id, Name = name, By = by, At = new DateTimeOffset(hub.Clock.UtcNow).ToUnixTimeMilliseconds(), V = v,
    }.Write));

    Task Result(Member me, string op, bool ok, string why, string slot) =>
        hub.Send(me, LodgeJson.Build(new FilesResultMsg { Op = op, Ok = ok, Why = why, Slot = slot }.Write));

    public async Task<bool> Handle(Member me, string type, JsonObject m)
    {
        switch (type)
        {
            case FilesListMsg.Type:                     // { t } only
                if (!Enabled) { await Result(me, "list", false, "disabled", null); return true; }
                if (!me.Budget.Page.TryTake()) { me.CountDrop(hub.Clock); await hub.Error(me, "Slow down a little", "rate"); return true; }
                await hub.Send(me, ListJson(me));
                return true;
            case FilesDeleteMsg.Type:
            {
                var id = FilesDeleteMsg.Read(new LodgeJsonFields(m)).Slot ?? "";
                var (ok, why) = Delete(me, id, out var name);
                await Result(me, "delete", ok, why, id);
                if (ok) { hub.Log.LogInformation("{Name} deleted a shared file slot", me.Name); await Announce("delete", id, name, null, me.Name); }
                return true;
            }
            case FilesRollbackMsg.Type:
            {
                var req = FilesRollbackMsg.Read(new LodgeJsonFields(m));
                var id = req.Slot ?? "";
                var v = req.V;
                var (ok, why) = Rollback(me, id, v, out var name, out var cur);
                await Result(me, "rollback", ok, why, id);
                if (ok) { hub.Log.LogInformation("{Name} rolled a shared file back", me.Name); await Announce("rollback", id, name, cur, me.Name); }
                return true;
            }
        }
        return false;
    }
}
