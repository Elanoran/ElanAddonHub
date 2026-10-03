using System.Security.Cryptography;
using System.Text;

namespace Lodge;

// Who a code belongs to. Personal codes carry the friend's name; the shared LODGE_CODE has none.
public record Identity(string Name, bool Personal, string Role);

// Invite codes + the wrong-code lockout.
//   <data>/codes   one "Name:code:role" per line (hub Guild Master panel or `sudo lodge-admin`), re-read when it changes
//   LODGE_CODE     optional shared code (rank: guest)
// The codes are read into an immutable snapshot that is swapped in whole; a failed read keeps the previous one.
// Every write takes the same lock file as lodge-admin (codes.lock) and replaces the file with an atomic rename,
// so readers never see a half-written list.
public class Auth
{
    sealed record Entry(string Name, byte[] Hash, string Role);
    sealed record Snapshot(IReadOnlyList<Entry> Personal, DateTime Stamp, long Size);

    readonly LodgeConfig cfg;
    readonly ILogger log;
    readonly object writeGate = new();
    readonly byte[] sharedHash;
    volatile Snapshot snap = new(Array.Empty<Entry>(), DateTime.MinValue, -1);
    public readonly StrikeTable Strikes;

    public Auth(LodgeConfig cfg, ILogger log, IClock clock = null)
    {
        this.cfg = cfg;
        this.log = log;
        Strikes = new StrikeTable(clock ?? SystemClock.Instance, cfg.MaxTrackedIps);
        sharedHash = string.IsNullOrEmpty(cfg.Code) ? null : Hash(cfg.Code);
        ReloadIfChanged();
        if (sharedHash == null && snap.Personal.Count == 0)
            log.LogWarning("No invite codes yet - add one with: sudo lodge-admin add <name>");
    }

    static byte[] Hash(string s) => SHA256.HashData(Encoding.UTF8.GetBytes(s.Trim()));

    void ReloadIfChanged(bool force = false)
    {
        DateTime stamp;
        long size;
        try
        {
            var fi = new FileInfo(cfg.CodesFile);
            stamp = fi.Exists ? fi.LastWriteTimeUtc : DateTime.MinValue;
            size = fi.Exists ? fi.Length : -1;
        }
        catch (Exception e) { log.LogWarning("Can't check the codes file: {Error}", e.GetType().Name); return; }
        var cur = snap;
        if (!force && stamp == cur.Stamp && size == cur.Size) return;
        try
        {
            var list = new List<Entry>();
            if (File.Exists(cfg.CodesFile))
                foreach (var line in File.ReadAllLines(cfg.CodesFile))
                    if (Parse(line) is { } p) list.Add(new Entry(p.Name, Hash(p.Code), p.Role));
            snap = new Snapshot(list, stamp, size); // swapped in whole
            log.LogInformation("Loaded {Count} personal invite codes", list.Count);
        }
        catch (Exception e)
        {
            // keep the previous snapshot: established identities must not drop on a transient read error
            log.LogWarning("Can't read the codes file, keeping the previous list: {Error}", e.GetType().Name);
        }
    }

    // "Name:code" or "Name:code:role" (names can't contain ':', the writers check)
    static (string Name, string Code, string Role)? Parse(string raw)
    {
        var line = raw.Trim();
        if (line.Length == 0 || line.StartsWith('#')) return null;
        var parts = line.Split(':');
        if (parts.Length < 2 || parts[0].Trim().Length == 0 || parts[1].Trim().Length == 0) return null;
        return (Names.Normalize(parts[0]), parts[1].Trim(), Roles.Normalize(parts.Length > 2 ? parts[2] : "member"));
    }

    public IEnumerable<string> PersonalNames { get { ReloadIfChanged(); return snap.Personal.Select(p => p.Name); } }

    public List<(string Name, string Role)> ListPersonal() { ReloadIfChanged(); return snap.Personal.Select(p => (p.Name, p.Role)).ToList(); }

    // null = not a valid code. Compares against every code in constant time.
    public Identity Check(string code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 256) return null;
        ReloadIfChanged();
        var h = Hash(code);
        Identity hit = null;
        foreach (var p in snap.Personal)
            if (CryptographicOperations.FixedTimeEquals(h, p.Hash)) hit ??= new Identity(p.Name, true, p.Role);
        if (sharedHash != null && CryptographicOperations.FixedTimeEquals(h, sharedHash)) hit ??= new Identity(null, false, "guest");
        return hit;
    }

    // ---- wrong-code lockout (see StrikeTable)
    public bool Blocked(string ip) => Strikes.IsBlocked(ip);

    public void Fail(string ip)
    {
        if (Strikes.Fail(ip))
            log.LogWarning("Blocked {Ip} for {Minutes} min after too many wrong invite codes", ip, Strikes.BlockFor.TotalMinutes);
    }

    public void Success(string ip) => Strikes.Success(ip);

    // ---- editing the codes file (the hub's Guild Master panel; lodge-admin does the same from a shell)

    static readonly System.Text.RegularExpressions.Regex NameRule = new(@"^[A-Za-z0-9][A-Za-z0-9 _-]{0,23}$");

    // new codes: 128 bits (32 hex characters); older, shorter codes keep working
    public static string NewCode() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public string Add(string name, string role)
    {
        name = Names.Normalize(name);
        if (!NameRule.IsMatch(name)) throw new InvalidOperationException("Names are 1-24 letters, digits, space, _ or -");
        var code = NewCode();
        Mutate(lines =>
        {
            if (lines.Select(Parse).Any(p => p != null && Names.Same(p.Value.Name, name)))
                throw new InvalidOperationException($"{name} already has a code");
            lines.Add($"{name}:{code}:{Roles.Normalize(role)}");
        });
        return code;
    }

    public void SetRole(string name, string role) => Change(name, p => $"{p.Name}:{p.Code}:{Roles.Normalize(role)}");

    public void Remove(string name) => Change(name, _ => null);

    void Change(string name, Func<(string Name, string Code, string Role), string> change) => Mutate(lines =>
    {
        bool found = false;
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            var p = Parse(lines[i]);
            if (p == null || !Names.Same(p.Value.Name, name)) continue;
            found = true;
            var replaced = change(p.Value);
            if (replaced == null) lines.RemoveAt(i); else lines[i] = replaced;
        }
        if (!found) throw new InvalidOperationException($"No code for {name}");
    });

    // read-modify-write under the shared lock; the new list is written to a private temp file in the same
    // folder and renamed over the old one (atomic), so nobody ever reads a partial file
    void Mutate(Action<List<string>> edit)
    {
        lock (writeGate)
        {
            using var _ = CodesLock.Acquire(cfg.CodesFile);
            var lines = File.Exists(cfg.CodesFile) ? File.ReadAllLines(cfg.CodesFile).ToList() : new List<string>();
            edit(lines);
            AtomicWrite(cfg.CodesFile, string.Join("\n", lines) + "\n");
            ReloadIfChanged(force: true);
        }
    }

    public static void AtomicWrite(string path, string content)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var tmp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var opts = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) opts.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; // private while written
            using (var fs = new FileStream(tmp, opts))
            using (var w = new StreamWriter(fs, new UTF8Encoding(false)))
            {
                w.Write(content);
                w.Flush();
                fs.Flush(flushToDisk: true);
            }
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead); // 0640
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }
}

// The lock shared with lodge-admin (`flock <codes>.lock`): an exclusive FileStream on Unix is an flock.
public static class CodesLock
{
    public static IDisposable Acquire(string codesFile, int timeoutMs = 10000)
    {
        var path = codesFile + ".lock";
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            try
            {
                var opts = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
                if (!OperatingSystem.IsWindows()) opts.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
                return new FileStream(path, opts);
            }
            catch (IOException) when (DateTime.UtcNow < until) { Thread.Sleep(25); }
        }
    }
}
