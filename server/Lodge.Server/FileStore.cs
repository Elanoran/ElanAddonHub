using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.StaticFiles;

namespace Lodge;

public class FileTooBigException : Exception { }

public class FileMeta
{
    public string Id { get; set; }
    public string Name { get; set; }
    public long Size { get; set; }
    public string Mime { get; set; }
    public long At { get; set; }
    public string By { get; set; }
}

// Uploaded files: data/files/<id>.bin + <id>.json. Deleted after LODGE_FILE_DAYS.
public class FileStore
{
    readonly LodgeConfig cfg;
    readonly string dir;
    static readonly FileExtensionContentTypeProvider Types = new();
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public FileStore(LodgeConfig cfg)
    {
        this.cfg = cfg;
        dir = Path.Combine(cfg.DataDir, "files");
        Directory.CreateDirectory(dir);
    }

    static bool ValidId(string id) => id != null && id.Length == 32 && id.All(Uri.IsHexDigit);

    public string PathOf(string id) => Path.Combine(dir, id + ".bin");

    public FileMeta Get(string id)
    {
        if (!ValidId(id)) return null;
        var meta = Path.Combine(dir, id + ".json");
        if (!File.Exists(meta) || !File.Exists(PathOf(id))) return null;
        return JsonSerializer.Deserialize<FileMeta>(File.ReadAllText(meta), Json);
    }

    public async Task<FileMeta> Save(Stream body, string name, string by, CancellationToken ct)
    {
        name = Clean(name);
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var path = PathOf(id);
        long size = 0;
        try
        {
            await using (var fs = File.Create(path))
            {
                var buf = new byte[81920];
                int n;
                while ((n = await body.ReadAsync(buf, ct)) > 0)
                {
                    size += n;
                    if (size > cfg.MaxFileBytes) throw new FileTooBigException();
                    await fs.WriteAsync(buf.AsMemory(0, n), ct);
                }
            }
        }
        catch
        {
            File.Delete(path);
            throw;
        }
        if (!Types.TryGetContentType(name, out var mime)) mime = "application/octet-stream";
        var meta = new FileMeta { Id = id, Name = name, Size = size, Mime = mime, At = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), By = by };
        await File.WriteAllTextAsync(Path.Combine(dir, id + ".json"), JsonSerializer.Serialize(meta, Json), ct);
        return meta;
    }

    static string Clean(string name)
    {
        name = Path.GetFileName(name ?? "").Trim();
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        if (name.Length == 0) name = "file";
        return name.Length > 120 ? name[..120] : name;
    }

    public void StartCleanup(CancellationToken stop)
    {
        _ = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    var cutoff = DateTime.UtcNow.AddDays(-cfg.FileDays);
                    foreach (var f in Directory.GetFiles(dir))
                        if (File.GetLastWriteTimeUtc(f) < cutoff) File.Delete(f);
                }
                catch { }
                try { await Task.Delay(TimeSpan.FromHours(1), stop); } catch { }
            }
        });
    }
}
