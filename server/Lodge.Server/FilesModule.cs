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
    public List<AddonFolder> Addon { get; set; }   // 2.35: an addon version's folders as their tocs say (read on upload / when marked)
}

// 2.36: a named set of slots (kits.json next to index.json)
public sealed class Kit
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string Desc { get; set; }
    public string See { get; set; }
    public string CreatedBy { get; set; }
    public List<string> Slots { get; set; } = new();
}

public sealed class Slot
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string CreatedBy { get; set; }
    public string Kind { get; set; }     // 2.32: "addon" = a zip of WoW addon folders (Elans.Shared.AddonZip); 2.34 "text" = an import string (.txt); null = a plain file
    public string Desc { get; set; }     // 2.33: a longer description (FilesModule.MaxDescChars, line breaks kept)
    public string Cat { get; set; }      // 2.37: what a plain file is (FileSlot.Cats), null = not said
    public string See { get; set; }      // 2.33: the lowest rank that sees it (FileSlot.SeeRanks); null = everyone, guests included
    public string Edit { get; set; }     // 2.33: the lowest rank that may update it (FileSlot.EditRanks); null = officers
    public string Cover { get; set; }    // 2.33: the cover picture's blob (32 hex, server-made) and its type
    public string CoverType { get; set; }
    public int CoverV { get; set; }      // goes up with every new picture (clients cache by it)
    public Dictionary<string, int> Installs { get; set; }   // 2.35: who (profile key) has which version installed - only counts leave the server
    public int NextV { get; set; } = 1;  // version numbers are never reused (not even after a roll back)
    public List<SlotVersion> Versions { get; set; } = new();
}

// What a file may be, independent of storage: the extension block and the zip listing.
public static class FileRules
{
    // the blocked types: the one shared list (Elans.Shared.DangerousFiles) that the Outpost warns about and refuses too
    public static IReadOnlyCollection<string> Blocked => Elans.Shared.DangerousFiles.Extensions;

    public const int MaxZipEntries = 20000;

    // the last extension, ignoring trailing dots/spaces ("setup.exe. " is still setup.exe on Windows); "" when none
    public static string Extension(string name) => Elans.Shared.DangerousFiles.Extension(name);

    public static bool IsBlockedName(string name) => Elans.Shared.DangerousFiles.IsDangerous(name);

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
    public const int MaxDescChars = 1000, MaxDescLines = 20;
    public const long MaxCoverBytes = 2 * 1024 * 1024;

    // 2.33: who sees a slot, who may update it (a new version, roll back, its description / kind / notes / picture). Officers and the owner see
    // every slot and may update every slot (FilesManage); a slot that is hidden from someone does not exist for them (404, never "forbidden").
    public static bool CanSee(string role, Slot s) => Roles.Can(role, Perm.FilesManage) || Roles.AtLeast(role, s.See ?? "guest");
    public static bool CanUpdate(string role, Slot s) => Roles.Can(role, Perm.FilesManage) || (s.Edit != null && Roles.AtLeast(role, s.Edit) && CanSee(role, s));

    // a description: line breaks kept (at most MaxDescLines), other control characters out, trimmed, at most MaxDescChars
    public static string CleanDesc(string d)
    {
        var t = (d ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
        var sb = new StringBuilder();
        foreach (var c in t) if (c == '\n' || !char.IsControl(c)) sb.Append(c);
        var lines = sb.ToString().Split('\n').Select(l => l.TrimEnd()).Take(MaxDescLines);
        t = string.Join("\n", lines).Trim();
        return t.Length > MaxDescChars ? t.Substring(0, MaxDescChars).TrimEnd() : t;
    }

    // a picture by its first bytes (never by what the client says): png, jpg, gif, webp; null = not one of them
    public static string ImageType(byte[] head)
    {
        if (head == null || head.Length < 12) return null;
        if (head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47) return "png";
        if (head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF) return "jpg";
        if (head[0] == 0x47 && head[1] == 0x49 && head[2] == 0x46 && head[3] == 0x38) return "gif";
        if (head[0] == 0x52 && head[1] == 0x49 && head[2] == 0x46 && head[3] == 0x46 && head[8] == 0x57 && head[9] == 0x45 && head[10] == 0x42 && head[11] == 0x50) return "webp";
        return null;
    }

    readonly LodgeHub hub;
    readonly string dir;
    readonly string indexPath;
    readonly object gate = new();
    readonly List<Slot> slots = new();
    readonly List<Kit> kits = new();
    string KitsPath => Path.Combine(dir, "kits.json");
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
        foreach (var s in slots)
        {
            if (s.Cover != null && (!ValidBlob(s.Cover) || !File.Exists(Path.Combine(dir, s.Cover + ".bin")))) { s.Cover = null; s.CoverType = null; changed = true; }
            if (s.See != null && Array.IndexOf(FileSlot.SeeRanks, s.See) < 0) { s.See = "officer"; changed = true; }       // unknown = the safe side
            if (s.Edit != null && Array.IndexOf(FileSlot.EditRanks, s.Edit) < 0) { s.Edit = null; changed = true; }
        }
        var live = slots.SelectMany(s => s.Versions).Select(v => v.Blob + ".bin").Concat(slots.Where(s => s.Cover != null).Select(s => s.Cover + ".bin")).ToHashSet();
        foreach (var f in Directory.GetFiles(dir, "*.bin"))
            if (!live.Contains(Path.GetFileName(f))) TryDelete(f);
        if (changed) SaveIndexLocked();
        try
        {
            if (File.Exists(KitsPath)) kits.AddRange((JsonSerializer.Deserialize<List<Kit>>(File.ReadAllText(KitsPath), Json) ?? new()).Where(k => ValidBlob(k.Id) && !string.IsNullOrEmpty(k.Name)).Take(FileKit.MaxKits));
            foreach (var k in kits) { k.Slots = (k.Slots ?? new()).Where(ValidBlob).Distinct().Take(FileKit.MaxSlots).ToList(); if (k.See != null && Array.IndexOf(FileSlot.SeeRanks, k.See) < 0) k.See = "officer"; }
        }
        catch (Exception e) when (e is JsonException or IOException) { hub.Log.LogWarning("Shared files: the kits could not be read ({Error})", e.GetType().Name); }
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
        V = v.V, FileName = v.FileName, Size = v.Size, Sha256 = v.Sha256, UploadedBy = v.UploadedBy, At = v.At, Note = v.Note ?? "", Addon = v.Addon ?? new List<AddonFolder>(),
    };

    static FileSlot SlotOf(Slot s, string role = null) => new()
    {
        Id = s.Id, Name = s.Name, CreatedBy = s.CreatedBy, Kind = s.Kind == FileSlot.AddonKind || s.Kind == FileSlot.TextKind ? s.Kind : null, Versions = s.Versions.OrderBy(v => v.V).Select(VersionOf).ToList(),
        Desc = string.IsNullOrEmpty(s.Desc) ? null : s.Desc, See = s.See, Edit = s.Edit, Cat = Array.IndexOf(FileSlot.Cats, s.Cat) >= 0 ? s.Cat : null, Cover = s.Cover != null ? Math.Max(1, s.CoverV) : 0,
        CanUpdate = role != null && CanUpdate(role, s),
        HaveNewest = s.Kind == FileSlot.AddonKind && s.Installs != null && s.Versions.Count > 0 ? s.Installs.Values.Count(v => v == s.Versions.Max(x => x.V)) : 0,
        HaveOlder = s.Kind == FileSlot.AddonKind && s.Installs != null && s.Versions.Count > 0 ? s.Installs.Values.Count(v => v > 0 && v < s.Versions.Max(x => x.V)) : 0,
    };

    static JsonObject SlotJson(Slot s, string role = null) => LodgeJson.Build(SlotOf(s, role).Write);

    JsonObject SlotJsonFor(string id, string role) { lock (gate) { var s = Find(id); return s == null || !CanSee(role, s) ? null : SlotJson(s, role); } }

    public JsonObject ListJson(Member me)
    {
        lock (gate)
            return LodgeJson.Build(new FilesDataMsg
            {
                MaxMb = hub.Cfg.FilesMaxMb, TotalMb = hub.Cfg.FilesTotalMb, UsedBytes = slots.Sum(s => s.Versions.Sum(v => v.Size)),
                Keep = KeptVersions, CanManage = Roles.Can(me.Role, Perm.FilesManage),
                Slots = slots.Where(s => CanSee(me.Role, s)).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Select(s => SlotOf(s, me.Role)).ToList(),
                Kits = KitsFor(me.Role),
            }.Write);
    }

    public JsonObject SlotView(string id, string role = null)
    {
        lock (gate) { var s = Find(id); return s == null || (role != null && !CanSee(role, s)) ? null : SlotJson(s, role); }
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
    // kind (header X-Slot-Kind, 2.32): "addon" marks the slot as a WoW addon - the file must then be an addon zip (AddonZip.Check); "file" unmarks it;
    // absent (an older Outpost) keeps what the slot was
    public async Task<JsonObject> Upload(string by, string role, string rateKey, string slotId, string slotName, string fileName, string note,
        string sha256, long? length, Stream body, CancellationToken ct, string kind = null)
    {
        kind = (kind ?? "").Trim().ToLowerInvariant();
        if (kind.Length > 0 && kind != FileSlot.AddonKind && kind != FileSlot.TextKind && kind != "file") throw new FilesException(400, "bad-kind", "Unknown kind of file");
        if (kind == FileSlot.TextKind && length > FileSlot.MaxTextBytes) throw new FilesException(413, "too-big", "Text can be 256 KB at most");
        var cfg = hub.Cfg;
        if (!Enabled) throw new FilesException(403, "disabled", "File sharing is switched off on this lodge");
        if (slotId == null && !Roles.Can(role, Perm.FilesManage)) throw new FilesException(403, "forbidden", "Only officers and the lodge owner can share new files");
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
                if (s == null || !CanSee(role, s)) throw new FilesException(404, "missing", "That slot doesn't exist any more");
                if (!CanUpdate(role, s)) throw new FilesException(403, "forbidden", "You can't update this file");
            }
            if (slots.Sum(s => s.Versions.Sum(v => v.Size)) + reservedBytes + length.Value - FreedByPrune(slotId) > cfg.FilesTotalBytes)
                throw new FilesException(507, "full", $"The lodge's file space ({cfg.FilesTotalMb} MB) is full - delete something first");
            if (running.TryGetValue(rateKey, out var n0) && n0 >= 1) throw new FilesException(429, "busy", "You already have an upload running");
            running[rateKey] = 1;
            reservedBytes += reserve;
        }
        bool slotTaken = false;
        string part = null;
        List<AddonFolder> addonInfo = null;
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
            if (kind.Length == 0 && !isNew)
            {
                string was; lock (gate) was = Find(slotId)?.Kind;
                if (was == FileSlot.TextKind || was == FileSlot.AddonKind) kind = was;       // an older Outpost says nothing: the slot keeps its kind and its rules
            }
            if (kind == FileSlot.TextKind)
            {
                if (FileRules.Extension(fileName) != "txt") throw new FilesException(415, "not-text", "Text is shared as a .txt file");
                var why = FileSlot.TextProblem(await File.ReadAllBytesAsync(part, ct));
                if (why != null) throw new FilesException(415, "not-text", why);
            }
            if (kind == FileSlot.AddonKind)
            {
                if (!FileRules.IsZip(fileName)) throw new FilesException(415, "not-addon", "An addon has to be a .zip of its addon folders");
                Elans.Shared.AddonZip.Result ar;
                await using (var zs = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.Read))
                    ar = Elans.Shared.AddonZip.Check(zs);
                if (!ar.Ok) throw new FilesException(415, "not-addon", ar.Problem);
                addonInfo = AddonFolder.From(ar);
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
                    if (!CanUpdate(role, s)) throw new FilesException(403, "forbidden", "You can't update this file");
                }
                var kindBefore = s.Kind;
                if (kind == FileSlot.AddonKind) s.Kind = FileSlot.AddonKind; else if (kind == FileSlot.TextKind) s.Kind = FileSlot.TextKind; else if (kind == "file") s.Kind = null;
                if (slots.Sum(x => x.Versions.Sum(v => v.Size)) + size - (isNew ? 0 : FreedByPrune(s.Id)) > cfg.FilesTotalBytes)
                    throw new FilesException(507, "full", $"The lodge's file space ({cfg.FilesTotalMb} MB) is full - delete something first");
                File.Move(part, Path.Combine(dir, blob + ".bin"));
                part = null;
                var v = new SlotVersion
                {
                    V = s.NextV, FileName = fileName, Size = size, Sha256 = actual, UploadedBy = by, Note = note, Blob = blob, Addon = addonInfo,
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
                    s.NextV--; s.Versions.Clear(); s.Versions.AddRange(before); s.Kind = kindBefore;
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
            return SlotJsonFor(id, role) ?? view;
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
    public FileStream OpenVersion(string slotId, int v, out SlotVersion ver, string role = "owner")
    {
        ver = null;
        if (!Enabled) throw new FilesException(403, "disabled", "File sharing is switched off on this lodge");
        lock (gate)
        {
            var s = Find(slotId);
            if (s != null && !CanSee(role, s)) s = null;
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
        string coverGone = null;
        lock (gate)
        {
            var s = Find(slotId);
            if (s == null) return (false, "missing");
            gone = s.Versions.ToList();
            if (s.Cover != null) coverGone = Path.Combine(dir, s.Cover + ".bin");
            name = s.Name;
            slots.Remove(s);
            if (!SaveIndexLocked()) { slots.Add(s); return (false, "save"); }
        }
        foreach (var v in gone) TryDelete(BlobPath(v));
        if (coverGone != null) TryDelete(coverGone);
        return (true, null);
    }

    // version v becomes the newest again: every newer kept version is removed (version numbers are not reused)
    public (bool ok, string why) Rollback(Member me, string slotId, int v, out string name, out int current)
    {
        name = null; current = 0;
        if (!Enabled) return (false, "disabled");
        List<SlotVersion> gone;
        lock (gate)
        {
            var s = Find(slotId);
            if (s == null || !CanSee(me.Role, s)) return (false, "missing");
            if (!CanUpdate(me.Role, s)) return (false, "forbidden");
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

    // ---------------------------------------------------------------- 2.33: edit without uploading, the cover picture

    public async Task<(bool ok, string why, string name)> EditAsync(Member me, FilesEditMsg req)
    {
        if (!Enabled) return (false, "disabled", null);
        bool admin = Roles.Can(me.Role, Perm.FilesManage);
        string newName = null, desc = null;
        if (req.Name != null)
        {
            if (!admin) return (false, "forbidden", null);
            newName = Names.Text(req.Name, MaxNameChars);
            if (newName.Length == 0) return (false, "bad-name", null);
        }
        if (req.See != null && (!admin || (req.See.Length > 0 && Array.IndexOf(FileSlot.SeeRanks, req.See) < 0))) return (false, admin ? "bad-rank" : "forbidden", null);
        if (req.Edit != null && (!admin || (req.Edit.Length > 0 && Array.IndexOf(FileSlot.EditRanks, req.Edit) < 0))) return (false, admin ? "bad-rank" : "forbidden", null);
        if (req.Kind != null && req.Kind != FileSlot.AddonKind && req.Kind != "file") return (false, "bad-kind", null);
        if (req.Cat != null && req.Cat.Length > 0 && Array.IndexOf(FileSlot.Cats, req.Cat) < 0) return (false, "bad-cat", null);
        if (req.Desc != null) desc = CleanDesc(req.Desc);
        string note = req.NoteV > 0 ? Names.Text(req.Note ?? "", MaxNoteChars) : null;
        string addonCheckBlob = null, addonCheckName = null;
        lock (gate)
        {
            var s = Find(req.Slot);
            if (s == null || !CanSee(me.Role, s)) return (false, "missing", null);
            if (!CanUpdate(me.Role, s)) return (false, "forbidden", null);
            if (newName != null && slots.Any(x => x != s && string.Equals(x.Name, newName, StringComparison.OrdinalIgnoreCase))) return (false, "name-taken", null);
            if (req.NoteV > 0 && s.Versions.All(v => v.V != req.NoteV)) return (false, "missing", null);
            if (req.Kind == FileSlot.AddonKind && s.Kind != FileSlot.AddonKind)
            {
                var newest = s.Versions.OrderBy(v => v.V).LastOrDefault();
                if (newest == null || !FileRules.IsZip(newest.FileName)) return (false, "not-addon", null);
                addonCheckBlob = BlobPath(newest); addonCheckName = s.Id;
            }
        }
        List<AddonFolder> markedInfo = null;
        if (addonCheckBlob != null)
        {
            AddonZip.Result ar;
            try { await using var fs = new FileStream(addonCheckBlob, FileMode.Open, FileAccess.Read, FileShare.Read); ar = AddonZip.Check(fs); }
            catch (IOException) { return (false, "missing", null); }
            if (!ar.Ok) return (false, "not-addon", null);
            markedInfo = AddonFolder.From(ar);
        }
        lock (gate)
        {
            var s = Find(req.Slot);
            if (s == null) return (false, "missing", null);
            var before = (s.Name, s.Desc, s.See, s.Edit, s.Kind, s.Cover, s.CoverType, Notes: s.Versions.Select(v => v.Note).ToList(), s.Cat);
            string coverGone = null;
            if (newName != null) s.Name = newName;
            if (desc != null) s.Desc = desc.Length == 0 ? null : desc;
            if (req.See != null) s.See = req.See.Length == 0 || req.See == "guest" ? null : req.See;
            if (req.Edit != null) s.Edit = req.Edit.Length == 0 || req.Edit == "officer" ? null : req.Edit;
            if (req.Kind != null) s.Kind = req.Kind == FileSlot.AddonKind ? FileSlot.AddonKind : null;
            if (req.Cat != null) s.Cat = req.Cat.Length == 0 ? null : req.Cat;
            if (markedInfo != null && s.Versions.Count > 0) s.Versions.OrderBy(v => v.V).Last().Addon = markedInfo;
            if (note != null) s.Versions.First(v => v.V == req.NoteV).Note = note;
            if (req.Cover == false && s.Cover != null) { coverGone = Path.Combine(dir, s.Cover + ".bin"); s.Cover = null; s.CoverType = null; }
            if (!SaveIndexLocked())
            {
                (s.Name, s.Desc, s.See, s.Edit, s.Kind, s.Cover, s.CoverType, s.Cat) = (before.Name, before.Desc, before.See, before.Edit, before.Kind, before.Cover, before.CoverType, before.Cat);
                for (int i = 0; i < s.Versions.Count; i++) s.Versions[i].Note = before.Notes[i];
                return (false, "save", null);
            }
            if (coverGone != null) TryDelete(coverGone);
            return (true, null, s.Name);
        }
    }

    // PUT /slots/{id}/cover: a picture (png, jpg, gif, webp by its own bytes; at most MaxCoverBytes) - anyone who may update the slot
    public async Task<JsonObject> PutCover(string by, string role, string slotId, string sha256, long? length, Stream body, CancellationToken ct)
    {
        if (!Enabled) throw new FilesException(403, "disabled", "File sharing is switched off on this lodge");
        sha256 = (sha256 ?? "").Trim().ToLowerInvariant();
        if (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit)) throw new FilesException(400, "bad-sha", "The upload needs its SHA-256");
        if (length == null) throw new FilesException(411, "length", "The upload needs a Content-Length");
        if (length <= 0) throw new FilesException(400, "empty", "That file is empty");
        if (length > MaxCoverBytes) throw new FilesException(413, "too-big", "A picture can be 2 MB at most");
        lock (gate)
        {
            var s = Find(slotId);
            if (s == null || !CanSee(role, s)) throw new FilesException(404, "missing", "That slot doesn't exist any more");
            if (!CanUpdate(role, s)) throw new FilesException(403, "forbidden", "You can't update this file");
        }
        using var ms = new MemoryStream();
        var buf = new byte[81920]; int n; long size = 0;
        using (var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2)))
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token))
            while ((n = await body.ReadAsync(buf, linked.Token)) > 0)
            {
                size += n;
                if (size > length || size > MaxCoverBytes) throw new FilesException(400, "length", "The upload is longer than it announced");
                ms.Write(buf, 0, n);
            }
        var data = ms.ToArray();
        if (data.Length != length) throw new FilesException(400, "length", "The upload is shorter than it announced");
        if (Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant() != sha256) throw new FilesException(400, "sha-mismatch", "The file arrived damaged (checksum differs) - try again");
        var type = ImageType(data) ?? throw new FilesException(415, "not-image", "That isn't a picture (png, jpg, gif or webp)");
        Directory.CreateDirectory(dir);
        var blob = NewId();
        var path = Path.Combine(dir, blob + ".bin");
        await File.WriteAllBytesAsync(path, data, ct);
        string old = null, name;
        lock (gate)
        {
            var s = Find(slotId);
            if (s == null) { TryDelete(path); throw new FilesException(404, "missing", "That slot doesn't exist any more"); }
            var before = (s.Cover, s.CoverType, s.CoverV);
            old = s.Cover; s.Cover = blob; s.CoverType = type; s.CoverV++;
            if (!SaveIndexLocked()) { (s.Cover, s.CoverType, s.CoverV) = before; TryDelete(path); throw new FilesException(500, "save", "The server couldn't store the picture"); }
            name = s.Name;
        }
        if (old != null) TryDelete(Path.Combine(dir, old + ".bin"));
        hub.Log.LogInformation("{Name} set a shared file's picture ({Size} bytes)", by, data.Length);
        _ = Announce("edit", slotId, name, null, by);
        return SlotJsonFor(slotId, role);
    }

    // GET /slots/{id}/cover: the bytes and their type, or null when there is none / the caller doesn't see the slot
    public (byte[] data, string type)? GetCover(string slotId, string role)
    {
        string path, type;
        lock (gate)
        {
            var s = Find(slotId);
            if (s == null || s.Cover == null || !CanSee(role, s)) return null;
            path = Path.Combine(dir, s.Cover + ".bin"); type = s.CoverType;
        }
        try { return (File.ReadAllBytes(path), type); } catch (IOException) { return null; }
    }

    // ---------------------------------------------------------------- 2.36: kits

    bool SaveKitsLocked()
    {
        try { var tmp = KitsPath + ".tmp"; File.WriteAllText(tmp, JsonSerializer.Serialize(kits, Json)); File.Move(tmp, KitsPath, true); return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { hub.Log.LogWarning("Shared files: the kits could not be saved ({Error})", e.GetType().Name); return false; }
    }

    static bool KitVisible(string role, Kit k) => Roles.Can(role, Perm.FilesManage) || Roles.AtLeast(role, k.See ?? "guest");

    // the kits a viewer sees, each with only the slots the viewer sees (a kit with none left is not shown)
    List<FileKit> KitsFor(string role) => kits.Where(k => KitVisible(role, k)).Select(k => new FileKit
    {
        Id = k.Id, Name = k.Name, Desc = k.Desc, See = k.See, CreatedBy = k.CreatedBy,
        Slots = k.Slots.Where(id => Find(id) is Slot s && CanSee(role, s)).ToList(),
    }).Where(k => k.Slots.Count > 0 || Roles.Can(role, Perm.FilesManage)).OrderBy(k => k.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public (bool ok, string why, string id, string name) SaveKit(Member me, FilesKitMsg req)
    {
        if (!Enabled) return (false, "disabled", null, null);
        if (!Roles.Can(me.Role, Perm.FilesManage)) return (false, "forbidden", null, null);
        var name = Names.Text(req.Name ?? "", MaxNameChars);
        if (name.Length == 0) return (false, "bad-name", null, null);
        var desc = Names.Text(req.Desc ?? "", FileKit.MaxDescChars);
        var see = string.IsNullOrEmpty(req.See) || req.See == "guest" ? null : req.See;
        if (see != null && Array.IndexOf(FileSlot.SeeRanks, see) < 0) return (false, "bad-rank", null, null);
        var ids = (req.Slots ?? "").Split(',').Select(x => x.Trim()).Where(ValidBlob).Distinct().ToList();
        if (ids.Count > FileKit.MaxSlots) return (false, "too-many", null, null);
        lock (gate)
        {
            ids = ids.Where(id => Find(id) != null).ToList();
            var k = req.Id == null ? null : kits.FirstOrDefault(x => x.Id == req.Id);
            if (req.Id != null && k == null) return (false, "missing", null, null);
            if (kits.Any(x => x != k && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))) return (false, "name-taken", null, null);
            if (k == null)
            {
                if (kits.Count >= FileKit.MaxKits) return (false, "too-many", null, null);
                k = new Kit { Id = NewId(), CreatedBy = me.Name };
                kits.Add(k);
            }
            var before = (k.Name, k.Desc, k.See, Slots: k.Slots.ToList());
            k.Name = name; k.Desc = desc.Length == 0 ? null : desc; k.See = see; k.Slots = ids;
            if (!SaveKitsLocked())
            {
                if (before.Name == null) kits.Remove(k); else (k.Name, k.Desc, k.See, k.Slots) = (before.Name, before.Desc, before.See, before.Slots);
                return (false, "save", null, null);
            }
            return (true, null, k.Id, k.Name);
        }
    }

    public (bool ok, string why, string name) DeleteKit(Member me, string id)
    {
        if (!Enabled) return (false, "disabled", null);
        if (!Roles.Can(me.Role, Perm.FilesManage)) return (false, "forbidden", null);
        lock (gate)
        {
            var k = kits.FirstOrDefault(x => x.Id == id);
            if (k == null) return (false, "missing", null);
            kits.Remove(k);
            if (!SaveKitsLocked()) { kits.Add(k); return (false, "save", null); }
            return (true, null, k.Name);
        }
    }

    // 2.35: who has which version of an addon slot (a version the slot never had, a slot the member doesn't see or a non-addon is ignored)
    public void NoteInstalled(string who, string role, string slotId, int v)
    {
        if (string.IsNullOrEmpty(who) || v < 0) return;
        lock (gate)
        {
            var s = Find(slotId);
            if (s == null || s.Kind != FileSlot.AddonKind || !CanSee(role, s) || v >= s.NextV) return;
            s.Installs ??= new Dictionary<string, int>(StringComparer.Ordinal);
            if (v == 0) { if (!s.Installs.Remove(who)) return; }
            else
            {
                if (s.Installs.TryGetValue(who, out var had) && had == v) return;
                if (!s.Installs.ContainsKey(who) && s.Installs.Count >= 1000) return;
                s.Installs[who] = v;
            }
            SaveIndexLocked();
        }
    }

    // ---------------------------------------------------------------- messages

    // to everybody who sees the slot (see = its rank before a change that hid it; a deleted slot: everybody who saw it)
    Task Announce(string action, string id, string name, int? v, string by, string see = null)
    {
        if (see == null) lock (gate) see = Find(id)?.See;
        var min = see ?? "guest";
        return hub.Broadcast(LodgeJson.Build(new FilesChangedMsg
        {
            Action = action, Slot = id, Name = name, By = by, At = new DateTimeOffset(hub.Clock.UtcNow).ToUnixTimeMilliseconds(), V = v,
        }.Write), m => Roles.Can(m.Role, Perm.FilesManage) || Roles.AtLeast(m.Role, min));
    }

    // a kit changed: everybody who sees it (or saw it) asks for the list again
    Task AnnounceKit(string id, string name, string by, string see) => hub.Broadcast(LodgeJson.Build(new FilesChangedMsg
    {
        Action = "kit", Slot = id, Name = name, By = by, At = new DateTimeOffset(hub.Clock.UtcNow).ToUnixTimeMilliseconds(),
    }.Write), m => Roles.Can(m.Role, Perm.FilesManage) || Roles.AtLeast(m.Role, string.IsNullOrEmpty(see) ? "guest" : see));

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
                string seeBefore; lock (gate) seeBefore = Find(id)?.See;
                var (ok, why) = Delete(me, id, out var name);
                await Result(me, "delete", ok, why, id);
                if (ok) { hub.Log.LogInformation("{Name} deleted a shared file slot", me.Name); await Announce("delete", id, name, null, me.Name, seeBefore ?? "guest"); }
                return true;
            }
            case FilesEditMsg.Type:
            {
                var req = FilesEditMsg.Read(new LodgeJsonFields(m));
                var id = req.Slot ?? "";
                string seeBefore; lock (gate) seeBefore = Find(id)?.See;
                var (ok, why, name) = await EditAsync(me, req);
                await Result(me, "edit", ok, why, id);
                if (ok)
                {
                    hub.Log.LogInformation("{Name} edited a shared file slot", me.Name);
                    string seeNow; lock (gate) seeNow = Find(id)?.See;
                    // who saw it before and who sees it now both hear of it (a slot that was hidden from someone vanishes from their list)
                    var widest = Array.IndexOf(FileSlot.SeeRanks, seeBefore ?? "guest") <= Array.IndexOf(FileSlot.SeeRanks, seeNow ?? "guest") ? seeBefore ?? "guest" : seeNow ?? "guest";
                    await Announce("edit", id, name, null, me.Name, widest);
                }
                return true;
            }
            case FilesInstalledMsg.Type:
            {
                var req = FilesInstalledMsg.Read(new LodgeJsonFields(m));
                if (!Enabled || !Roles.AtLeast(me.Role, "member")) return true;                 // guests share one name: they are not counted
                if (!me.Budget.Page.TryTake()) return true;
                NoteInstalled(ProfileRules.Key(me.Name), me.Role, req.Slot ?? "", req.V);
                return true;
            }
            case FilesKitMsg.Type:
            {
                var req = FilesKitMsg.Read(new LodgeJsonFields(m));
                var (ok, why, id, name) = SaveKit(me, req);
                await Result(me, "kit", ok, why, id);
                if (ok) { hub.Log.LogInformation("{Name} saved a file kit", me.Name); await AnnounceKit(id, name, me.Name, req.See); }
                return true;
            }
            case FilesKitMsg.DeleteType:
            {
                var id = new LodgeJsonFields(m).Str("id") ?? "";
                string see; lock (gate) see = kits.FirstOrDefault(x => x.Id == id)?.See;
                var (ok, why, name) = DeleteKit(me, id);
                await Result(me, "kit", ok, why, id);
                if (ok) await AnnounceKit(id, name, me.Name, see);
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
