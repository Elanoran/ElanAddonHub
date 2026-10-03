using System.Globalization;
using System.Text;

namespace Lodge;

// Time source, so expiry rules can be tested with a fake clock instead of real sleeps.
public interface IClock { DateTime UtcNow { get; } }

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();
    public DateTime UtcNow => DateTime.UtcNow;
}

// One canonical form for display names, used for personal names, guest reservations and reconnect matching:
// Unicode NFKC (fullwidth/compatibility forms fold to plain letters), control and format characters
// removed, any run of whitespace becomes one space, trimmed, at most 24 characters.
public static class Names
{
    public const int Max = 24;

    public static string Normalize(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        string s;
        try { s = name.Normalize(NormalizationForm.FormKC); } catch (ArgumentException) { s = name; } // invalid surrogates
        var sb = new StringBuilder(s.Length);
        bool space = false;
        foreach (var c in s)
        {
            var cat = char.GetUnicodeCategory(c);
            if (char.IsWhiteSpace(c) || cat == UnicodeCategory.SpaceSeparator) { space = sb.Length > 0; continue; }
            if (cat is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate
                or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned) continue;
            if (space) { sb.Append(' '); space = false; }
            sb.Append(c);
        }
        var r = sb.ToString();
        return r.Length > Max ? r[..Max].TrimEnd() : r;
    }

    public static bool Same(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    // log fields chosen by clients: no control characters, bounded
    public static string ForLog(string s, int max = 48)
    {
        s = Normalize(s ?? "");
        return s.Length > max ? s[..max] : s;
    }
}

// Classic token bucket: up to Capacity actions in a burst, refilled at Rate per second.
public sealed class TokenBucket
{
    readonly IClock clock;
    readonly double capacity, rate;
    double tokens;
    DateTime last;

    public TokenBucket(double capacity, double ratePerSecond, IClock clock)
    {
        this.capacity = capacity;
        rate = ratePerSecond;
        this.clock = clock;
        tokens = capacity;
        last = clock.UtcNow;
    }

    public bool TryTake(double n = 1)
    {
        lock (this)
        {
            var now = clock.UtcNow;
            tokens = Math.Min(capacity, tokens + (now - last).TotalSeconds * rate);
            last = now;
            if (tokens < n) return false;
            tokens -= n;
            return true;
        }
    }
}

// Wrong-code bookkeeping per client IP, bounded.
//  - MaxFails failures inside Window block the IP for BlockFor; the failure that reaches the limit is still answered
//    401, the next request gets 429 (the 1.x contract).
//  - At most Capacity IPs are tracked. Expired records are swept; active blocks are never evicted to make room.
//    When the table is full, every untracked IP shares one "overflow" record - so an attacker spreading guesses over
//    many addresses is limited together instead of growing memory.
//  - A success clears only that IP's own record (never the shared overflow record).
public sealed class StrikeTable
{
    public const string OverflowKey = "*overflow*";

    sealed class Entry { public int Fails; public DateTime First; public DateTime BlockedUntil; }

    readonly IClock clock;
    readonly int maxFails, capacity;
    readonly TimeSpan window, blockFor;
    readonly Dictionary<string, Entry> map = new();
    readonly object gate = new();

    public StrikeTable(IClock clock, int capacity = 10000, int maxFails = 10, TimeSpan? window = null, TimeSpan? blockFor = null)
    {
        this.clock = clock;
        this.capacity = Math.Max(1, capacity);
        this.maxFails = maxFails;
        this.window = window ?? TimeSpan.FromMinutes(15);
        this.blockFor = blockFor ?? TimeSpan.FromMinutes(15);
    }

    public int Count { get { lock (gate) return map.Count; } }
    public TimeSpan BlockFor => blockFor;

    string Resolve(string ip, bool create)
    {
        if (map.ContainsKey(ip)) return ip;
        if (map.Count < capacity) return create ? ip : null;
        SweepLocked();
        if (map.Count < capacity) return create ? ip : null;
        return OverflowKey;
    }

    public bool IsBlocked(string ip)
    {
        lock (gate)
        {
            var key = Resolve(ip, create: false);
            return key != null && map.TryGetValue(key, out var e) && e.BlockedUntil > clock.UtcNow;
        }
    }

    // returns true when this failure blocked the IP
    public bool Fail(string ip)
    {
        lock (gate)
        {
            var now = clock.UtcNow;
            var key = Resolve(ip, create: true);
            if (!map.TryGetValue(key, out var e)) map[key] = e = new Entry { First = now };
            if (e.BlockedUntil > now) return false; // already blocked: don't extend
            if (now - e.First > window) { e.Fails = 0; e.First = now; }
            if (++e.Fails < maxFails) return false;
            e.BlockedUntil = now + blockFor;
            e.Fails = 0;
            e.First = now;
            return true;
        }
    }

    public void Success(string ip)
    {
        lock (gate)
        {
            if (map.TryGetValue(ip, out var e) && e.BlockedUntil <= clock.UtcNow) map.Remove(ip);
        }
    }

    public int Sweep() { lock (gate) return SweepLocked(); }

    int SweepLocked()
    {
        var now = clock.UtcNow;
        var dead = map.Where(kv => kv.Value.BlockedUntil <= now && now - kv.Value.First > window).Select(kv => kv.Key).ToList();
        foreach (var k in dead) map.Remove(k);
        return dead.Count;
    }
}
