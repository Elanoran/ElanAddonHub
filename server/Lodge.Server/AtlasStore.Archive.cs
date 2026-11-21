using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;

namespace Lodge;

// 2.40: the raw find archive. A find the caps remove (Atlas.MaxFinds / MaxFindsPerMember, or a lower cap at load) is not lost: it is
// appended to data/atlas-archive/finds-YYYY-MM.jsonl.gz (the month the find was made, UTC), one stored find per line (the same shape
// as atlas.json "finds": b = the member's Lodge identity, w = their display name; never a character name). The archive is never sent
// to the Outposts; it is the raw data the item records (docs: where items drop) are rebuilt from. lodge-backup includes it (inside data/).
// Each flush appends one gzip member (a .gz of several members reads as one stream). Written just before atlas.json, so a crash in between
// can only repeat a line (a rebuild drops repeats by the find key), never lose one.
// The member forget (Purge) rewrites the files without that member's lines. LODGE_ATLAS_ARCHIVE=0 turns it off (removed finds are then
// dropped, as before 2.40); LODGE_ATLAS_ARCHIVE_MB caps its size (the oldest months go first).
public sealed partial class AtlasStore
{
    public bool ArchiveEnabled { get; set; } = true;
    public long ArchiveCapBytes { get; set; } = 50L * 1024 * 1024;
    readonly List<Atlas.Find> archiveQueue = new();
    bool archiveCapLogged;

    string ArchiveDir => Path.Combine(Path.GetDirectoryName(path)!, "atlas-archive");

    static JsonObject StoredFindJson(Atlas.Find f)
    {
        var j = FindJson(f, ""); j.Remove("by"); j.Remove("s");
        j["b"] = f.Identity; j["w"] = f.Who; j["r"] = f.Rev;
        if (f.Inst > 0) j["ii"] = f.Inst;
        if (f.Suffix != 0) j["sx"] = f.Suffix;
        if (f.Ctx > 0) j["cx"] = f.Ctx;
        return j;
    }

    void Archive(Atlas.Find f) { if (ArchiveEnabled) archiveQueue.Add(f); }

    static string MonthFile(long at) => "finds-" + DateTimeOffset.FromUnixTimeSeconds(Math.Max(0, at)).UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture) + ".jsonl.gz";

    // Caller holds Gate. A failure keeps the queue (the next save tries again) and never stops atlas.json from being saved.
    void FlushArchive()
    {
        if (archiveQueue.Count == 0) return;
        if (!ArchiveEnabled) { archiveQueue.Clear(); return; }
        try
        {
            Directory.CreateDirectory(ArchiveDir);
            foreach (var g in archiveQueue.GroupBy(f => MonthFile(f.At)))
            {
                var sb = new StringBuilder();
                foreach (var f in g) sb.Append(StoredFindJson(f).ToJsonString()).Append('\n');
                using var fs = new FileStream(Path.Combine(ArchiveDir, g.Key), FileMode.Append, FileAccess.Write, FileShare.None);
                using (var gz = new GZipStream(fs, CompressionLevel.SmallestSize, leaveOpen: true)) gz.Write(Encoding.UTF8.GetBytes(sb.ToString()));
                fs.Flush(flushToDisk: true);
            }
            archiveQueue.Clear();
            CapArchive();
        }
        catch (Exception e) { log?.LogWarning("Atlas: the find archive could not be written: {Error}", e.GetType().Name); }
    }

    IEnumerable<FileInfo> ArchiveFiles() =>
        Directory.Exists(ArchiveDir) ? new DirectoryInfo(ArchiveDir).GetFiles("finds-*.jsonl.gz").OrderBy(f => f.Name, StringComparer.Ordinal) : Enumerable.Empty<FileInfo>();

    void CapArchive()
    {
        var files = ArchiveFiles().ToList();
        long total = files.Sum(f => f.Length);
        int i = 0;
        while (total > ArchiveCapBytes && i < files.Count - 1)        // the newest month always stays
        {
            total -= files[i].Length; files[i].Delete(); i++;
        }
        if (i > 0 && !archiveCapLogged) { archiveCapLogged = true; log?.LogWarning("Atlas: the find archive reached its cap ({Mb} MB): the oldest month(s) were removed", ArchiveCapBytes / (1024 * 1024)); }
    }

    // every archived find, oldest month first (a line that does not parse is skipped). Caller holds Gate.
    public IEnumerable<JsonObject> ArchivedFinds()
    {
        FlushArchive();
        foreach (var file in ArchiveFiles())
            foreach (var line in ReadLines(file.FullName))
            {
                JsonObject o = null;
                try { o = JsonNode.Parse(line) as JsonObject; } catch { }
                if (o != null) yield return o;
            }
    }

    static List<string> ReadLines(string file)
    {
        var lines = new List<string>();
        using var fs = File.OpenRead(file);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var rd = new StreamReader(gz, Encoding.UTF8);
        string l;
        while ((l = rd.ReadLine()) != null) if (l.Length > 0) lines.Add(l);
        return lines;
    }

    public long ArchiveBytes { get { lock (Gate) return ArchiveFiles().Sum(f => f.Length); } }

    // the member forget: their archived finds go too. Returns how many lines were removed. Caller holds Gate.
    int PurgeArchive(string identity)
    {
        int removed = archiveQueue.RemoveAll(f => f.Identity == identity);
        foreach (var file in ArchiveFiles().ToList())
        {
            try
            {
                var lines = ReadLines(file.FullName);
                var keep = lines.Where(l => !LineOf(l, identity)).ToList();
                if (keep.Count == lines.Count) continue;
                removed += lines.Count - keep.Count;
                if (keep.Count == 0) { file.Delete(); continue; }
                var tmpFile = file.FullName + ".tmp";
                using (var fs = new FileStream(tmpFile, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    using (var gz = new GZipStream(fs, CompressionLevel.SmallestSize, leaveOpen: true)) gz.Write(Encoding.UTF8.GetBytes(string.Join('\n', keep) + "\n"));
                    fs.Flush(flushToDisk: true);
                }
                File.Move(tmpFile, file.FullName, overwrite: true);
            }
            catch (Exception e) { log?.LogWarning("Atlas: a find archive file could not be cleaned: {Error}", e.GetType().Name); }
        }
        return removed;
    }

    static bool LineOf(string line, string identity)
    {
        try { return JsonNode.Parse(line) is JsonObject o && o["b"] is JsonValue v && v.TryGetValue<string>(out var b) && b == identity; }
        catch { return false; }
    }
}
