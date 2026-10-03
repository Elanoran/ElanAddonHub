using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Lodge;

// Who a code belongs to. Personal codes carry the friend's name; the shared LODGE_CODE has none.
public record Identity(string Name, bool Personal, string Role);

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
    List<(string Name, byte[] Hash, string Role)> personal = new();
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
            var list = new List<(string, byte[], string)>();
            if (File.Exists(cfg.CodesFile))
            {
                // "Name:code" or "Name:code:role" (names can't contain ':', lodge-admin checks)
                foreach (var raw in File.ReadAllLines(cfg.CodesFile))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith('#')) continue;
                    var parts = line.Split(':');
                    if (parts.Length < 2 || parts[0].Trim().Length == 0 || parts[1].Trim().Length == 0) continue;
                    list.Add((parts[0].Trim(), Hash(parts[1]), Roles.Normalize(parts.Length > 2 ? parts[2] : "member")));
                }
            }
            personal = list;
            fileStamp = stamp;
            log.LogInformation("Loaded {Count} personal invite codes", list.Count);
        }
    }

    public IEnumerable<string> PersonalNames { get { ReloadIfChanged(); return personal.Select(p => p.Name); } }

    public List<(string Name, string Role)> ListPersonal() { ReloadIfChanged(); return personal.Select(p => (p.Name, p.Role)).ToList(); }

    // ---- editing the codes file (the hub's Guild Master panel; lodge-admin does the same from a shell)

    static readonly System.Text.RegularExpressions.Regex NameRule = new(@"^[A-Za-z0-9][A-Za-z0-9 _-]{0,23}$");

    public string Add(string name, string role)
    {
        if (!NameRule.IsMatch(name ?? "")) throw new InvalidOperationException("Names are 1-24 letters, digits, space, _ or -");
        lock (gate)
        {
            if (ListPersonal().Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"{name} already has a code");
            var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
            File.AppendAllText(cfg.CodesFile, $"{name}:{code}:{Roles.Normalize(role)}\n");
            fileStamp = DateTime.MinValue; // re-read now
            return code;
        }
    }

    public void SetRole(string name, string role) => Rewrite(name, line =>
    {
        var parts = line.Split(':');
        return $"{parts[0]}:{parts[1]}:{Roles.Normalize(role)}";
    });

    public void Remove(string name) => Rewrite(name, _ => null);

    void Rewrite(string name, Func<string, string> change)
    {
        lock (gate)
        {
            bool found = false;
            var lines = new List<string>();
            foreach (var line in File.ReadAllLines(cfg.CodesFile))
            {
                var n = line.Split(':')[0].Trim();
                if (!line.TrimStart().StartsWith('#') && string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
                {
                    found = true;
                    var replaced = change(line);
                    if (replaced != null) lines.Add(replaced);
                }
                else lines.Add(line);
            }
            if (!found) throw new InvalidOperationException($"No code for {name}");
            File.WriteAllLines(cfg.CodesFile, lines); // same file: keeps owner and permissions
            fileStamp = DateTime.MinValue;
        }
    }

    // null = not a valid code. Compares against every code in constant time.
    public Identity Check(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        ReloadIfChanged();
        var h = Hash(code);
        Identity hit = null;
        foreach (var p in personal)
            if (CryptographicOperations.FixedTimeEquals(h, p.Hash)) hit ??= new Identity(p.Name, true, p.Role);
        if (sharedHash != null && CryptographicOperations.FixedTimeEquals(h, sharedHash)) hit ??= new Identity(null, false, "guest");
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
