using System.Globalization;
using System.Text.Json.Nodes;

namespace Lodge;

// 2.31: security events in the log, so an attack can be seen and followed. One line per event, starting "security:", never a code, a token or
// a key. The address is in the line (the journal keeps it 30 days); it never goes into a file of the Lodge or the admin stats.
//   - the first event of a kind from an address within RepeatWindow is written out in full; the rest are counted and summed up once a minute
//     ("security: 37 more wrong-code events from 3 addresses in the last minute"), so a flood can't fill the disk or bury everything else;
//   - a summary of the UTC day ("security 2026-10-18: ...") when the day changes; Today() for the admin stats (Elan's Ops), without addresses.
// Kinds: wrong-code, lockout, blocked, flood, too-big, admin-refused, http-rate.
public sealed class LodgeSecurity
{
    public static readonly TimeSpan RepeatWindow = TimeSpan.FromMinutes(10);
    readonly ILogger log;
    readonly IClock clock;
    readonly object gate = new();
    readonly Dictionary<(string kind, string ip), DateTime> lastFull = new();
    readonly Dictionary<string, (int n, HashSet<string> ips)> minute = new(StringComparer.Ordinal);
    Dictionary<string, long> today = new(StringComparer.Ordinal);
    string day;
    readonly List<JsonObject> recent = new();
    DateTime lastFlush;

    public LodgeSecurity(ILogger log, IClock clock) { this.log = log; this.clock = clock; day = Day(clock.UtcNow); lastFlush = clock.UtcNow; }

    static string Day(DateTime t) => t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public void Note(string kind, string ip, string detail = null)
    {
        lock (gate)
        {
            var now = clock.UtcNow;
            RollDay(now);
            today[kind] = today.GetValueOrDefault(kind) + 1;
            var key = (kind, ip ?? "?");
            if (!lastFull.TryGetValue(key, out var at) || now - at >= RepeatWindow)
            {
                lastFull[key] = now;
                if (lastFull.Count > 20_000) foreach (var k in lastFull.Where(kv => now - kv.Value >= RepeatWindow).Select(kv => kv.Key).ToList()) lastFull.Remove(k);
                log?.LogWarning("security: {Kind} from {Ip}{Detail}", kind, ip ?? "?", detail == null ? "" : " - " + detail);
                recent.Add(new JsonObject { ["at"] = new DateTimeOffset(now).ToUnixTimeSeconds(), ["kind"] = kind, ["detail"] = detail });
                if (recent.Count > 50) recent.RemoveAt(0);
                return;
            }
            var (n, ips) = minute.TryGetValue(kind, out var m) ? m : (0, new HashSet<string>(StringComparer.Ordinal));
            ips.Add(ip ?? "?");
            minute[kind] = (n + 1, ips);
        }
    }

    // the housekeeping tick calls this; it sums up once a minute
    public void Tick()
    {
        lock (gate)
        {
            var now = clock.UtcNow;
            RollDay(now);
            if (now - lastFlush < TimeSpan.FromMinutes(1)) return;
            lastFlush = now;
            foreach (var (kind, (n, ips)) in minute)
                log?.LogWarning("security: {N} more {Kind} events from {Ips} addresses in the last minute", n, kind, ips.Count);
            minute.Clear();
        }
    }

    void RollDay(DateTime now)
    {
        var d = Day(now);
        if (d == day) return;
        log?.LogInformation("security {Day}: {Summary}", day, today.Count == 0 ? "nothing" : string.Join(", ", today.OrderBy(kv => kv.Key).Select(kv => kv.Key + " " + kv.Value)));
        today = new Dictionary<string, long>(StringComparer.Ordinal);
        day = d;
    }

    public JsonObject Today()
    {
        lock (gate)
        {
            RollDay(clock.UtcNow);
            var counts = new JsonObject();
            foreach (var (k, v) in today.OrderBy(kv => kv.Key)) counts[k] = v;
            return new JsonObject { ["day"] = day, ["counts"] = counts, ["recent"] = new JsonArray(recent.Select(r => (JsonNode)r.DeepClone()).Reverse().Take(20).ToArray()) };
        }
    }
}
