using System.Text;
using System.Text.Json.Nodes;

namespace Lodge;

// data/calendar.json = { "v": 1, "events": [ event, ... ] } - its own atomic file (like pins and history), bounded:
// at most Cal.MaxFuture future events (enforced by the module on create), past events dropped Cal.PruneAfterDays after their
// end, a size cap on load, every loaded event re-validated. Saved atomically (tmp file, flush, rename); a leftover tmp from a
// crash is used only if the main file is missing. Failures are logged without any event text.
public sealed class CalendarStore
{
    const long MaxFileBytes = 8 * 1024 * 1024;
    public const int MaxStored = 800;     // future (200) plus a generous bound for the 30 days of past events

    readonly string path, tmp;
    readonly ILogger log;
    readonly IClock clock;
    public readonly object Gate = new();
    readonly List<JsonObject> events = new();

    public CalendarStore(string dataDir, ILogger log, IClock clock)
    {
        this.log = log; this.clock = clock;
        path = Path.Combine(dataDir, "calendar.json");
        tmp = path + ".tmp";
        try
        {
            if (File.Exists(tmp)) { if (!File.Exists(path)) File.Move(tmp, path); else File.Delete(tmp); }
            if (File.Exists(path) && new FileInfo(path).Length <= MaxFileBytes)
            {
                var root = JsonNode.Parse(File.ReadAllText(path))?.AsObject();
                if (root?["events"] is JsonArray arr)
                    foreach (var n in arr)
                    {
                        if (n is not JsonObject o || (J.Str(o, "id") ?? "").Length == 0 || Cal.Start(o) <= 0 || (J.Str(o, "title") ?? "").Length == 0) continue;
                        if (events.Count >= MaxStored) break;
                        events.Add(o.DeepClone().AsObject());
                    }
            }
        }
        catch (Exception e) { log?.LogWarning("Calendar: load failed: {Error}", e.GetType().Name); }
        lock (Gate) Prune(NowMs);
    }

    public long NowMs => new DateTimeOffset(clock.UtcNow).ToUnixTimeMilliseconds();

    // all callers hold Gate
    public List<JsonObject> Events => events;
    public JsonObject Find(string id) => string.IsNullOrEmpty(id) ? null : events.FirstOrDefault(e => J.Str(e, "id") == id);
    public int Future(long nowMs) => events.Count(e => Cal.Start(e) >= nowMs);

    // past events go 30 days after they end; returns how many were removed (the caller saves)
    public int Prune(long nowMs) => events.RemoveAll(e => Cal.Finish(e) < nowMs - Cal.PruneAfterDays * 86_400_000L);

    public JsonArray Snapshot(Func<JsonObject, bool> where)
    {
        lock (Gate) return new JsonArray(events.Where(where).OrderBy(Cal.Start).Select(e => (JsonNode)e.DeepClone()).ToArray());
    }

    public bool Save()
    {
        try
        {
            var root = new JsonObject { ["v"] = 1, ["events"] = new JsonArray(events.Select(e => (JsonNode)e.DeepClone()).ToArray()) };
            var bytes = Encoding.UTF8.GetBytes(root.ToJsonString());
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception e)
        {
            log?.LogWarning("Calendar: save failed: {Error}", e.GetType().Name);
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            return false;
        }
    }
}
