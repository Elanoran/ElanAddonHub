using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Lodge;

// Who a code belongs to. Personal codes carry the friend's name; the shared LODGE_CODE has none.
public record Identity(string Name, bool Personal);

// Invite codes + lockout of IPs that keep guessing.
//   /etc/lodge/codes   one "Name:code" per line (managed with `sudo lodge-admin`), re-read when it changes
//   LODGE_CODE         optional shared code (no fixed name)
public class Auth
{
    const int MaxFails = 10;
    static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    static readonly TimeSpan BlockFor = TimeSpan.FromMinutes(15);

    readonly LodgeConfig cfg;
    readonly ILogger log;
    readonly object gate = new();
    List<(string Name, byte[] Hash)> personal = new();
    DateTime fileStamp = DateTime.MinValue;
    readonly byte[] sharedHash;

    class Strikes { public int Fails; public DateTime First; public DateTime BlockedUntil; }
    readonly ConcurrentDictionary<string, Strikes> strikes = new();

    public Auth(LodgeConfig cfg, ILogger log)
    {
        this.cfg = cfg;
        this.log = log;
        sharedHash = string.IsNullOrEmpty(cfg.Code) ? null : Hash(cfg.Code);
        ReloadIfChanged();
        if (sharedHash == null && personal.Count == 0)
            log.LogWarning("No invite codes yet - add one with: sudo lodge-admin add <name>");
    }

    static byte[] Hash(string s) => SHA256.HashData(Encoding.UTF8.GetBytes(s.Trim()));

    void ReloadIfChanged()
    {
        var stamp = File.Exists(cfg.CodesFile) ? File.GetLastWriteTimeUtc(cfg.CodesFile) : DateTime.MinValue;
        if (stamp == fileStamp) return;
        lock (gate)
        {
            var list = new List<(string, byte[])>();
            if (File.Exists(cfg.CodesFile))
            {
                foreach (var raw in File.ReadAllLines(cfg.CodesFile))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith('#')) continue;
                    var i = line.LastIndexOf(':');
                    if (i <= 0 || i == line.Length - 1) continue;
                    list.Add((line[..i].Trim(), Hash(line[(i + 1)..])));
                }
            }
            personal = list;
            fileStamp = stamp;
            log.LogInformation("Loaded {Count} personal invite codes", list.Count);
        }
    }

    public IEnumerable<string> PersonalNames { get { ReloadIfChanged(); return personal.Select(p => p.Name); } }

    // null = not a valid code. Compares against every code in constant time.
    public Identity Check(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        ReloadIfChanged();
        var h = Hash(code);
        Identity hit = null;
        foreach (var p in personal)
            if (CryptographicOperations.FixedTimeEquals(h, p.Hash)) hit ??= new Identity(p.Name, true);
        if (sharedHash != null && CryptographicOperations.FixedTimeEquals(h, sharedHash)) hit ??= new Identity(null, false);
        return hit;
    }

    public bool Blocked(string ip) =>
        strikes.TryGetValue(ip, out var s) && s.BlockedUntil > DateTime.UtcNow;

    public void Fail(string ip)
    {
        var now = DateTime.UtcNow;
        var s = strikes.GetOrAdd(ip, _ => new Strikes { First = now });
        lock (s)
        {
            if (now - s.First > Window) { s.Fails = 0; s.First = now; }
            if (++s.Fails >= MaxFails)
            {
                s.BlockedUntil = now + BlockFor;
                s.Fails = 0;
                log.LogWarning("Blocked {Ip} for {Minutes} min after {Max} wrong invite codes", ip, BlockFor.TotalMinutes, MaxFails);
            }
        }
    }

    public void Success(string ip) => strikes.TryRemove(ip, out _);
}
