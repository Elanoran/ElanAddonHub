using System.Security.Cryptography;
using System.Text;

namespace Lodge;

// Who a code belongs to. Personal codes carry the friend's name; the shared LODGE_CODE has none.
public record Identity(string Name, bool Personal, string Role, string DeviceId = null)
{
    // the rank an HTTP route checks: a non-personal credential (the shared code, a guest device) is always a guest
    public string Rank => Personal ? Role : "guest";
    // the uploader name stored with a file: only ever the verified name (never a name the client claims in a header)
    public string By => Personal ? Name : "Guest";
    // what an upload / download budget is kept per: the person, or for a guest their address (IpKey: an IPv6 /64)
    public string RateKey(string ip) => Personal ? "p:" + Name.ToLowerInvariant() : "g:" + Elans.Shared.IpKey.Of(ip);
}

// Invite codes + the wrong-code lockout.
//   <data>/codes   one "Name:code:role" per line (hub Guild Master panel or `sudo lodge-admin`), re-read when it changes
//   LODGE_CODE     optional shared code (rank: guest)
// The codes are read into an immutable snapshot that is swapped in whole; a failed read keeps the previous one.
// Every write takes the same lock file as lodge-admin (codes.lock) and replaces the file with an atomic rename,
// so readers never see a half-written list.
public partial class Auth
{
    // Hash is null for a retired code (the person has devices but no permanent code any more)
    sealed record Entry(string Name, byte[] Hash, string Role);
    sealed record Snapshot(IReadOnlyList<Entry> Personal, DateTime Stamp, long Size);

    readonly LodgeConfig cfg;
    readonly ILogger log;
    readonly IClock clock;
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
        this.clock = clock ?? SystemClock.Instance;
        ReloadIfChanged();
        ReloadDevicesIfChanged();
        if (sharedHash == null && snap.Personal.Count == 0)
            log.LogWarning("No members yet - make the owner link with: sudo lodge-admin claim-owner");
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
                    if (Parse(line) is { } p) list.Add(new Entry(p.Name, IsRetired(p.Code) ? null : Hash(p.Code), p.Role));
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
            if (p.Hash != null && CryptographicOperations.FixedTimeEquals(h, p.Hash)) hit ??= new Identity(p.Name, true, p.Role);
        if (sharedHash != null && CryptographicOperations.FixedTimeEquals(h, sharedHash)) hit ??= new Identity(null, false, "guest");
        return hit;
    }

    // ---- wrong-code lockout (see StrikeTable)
    public bool Blocked(string ip) => Strikes.IsBlocked(ip);

    public LodgeSecurity Security;   // 2.31: set by the hub (LodgeSecurity.cs)

    public void Fail(string ip)
    {
        Security?.Note("wrong-code", ip);
        if (Strikes.Fail(ip))
        {
            log.LogWarning("Blocked {Ip} for {Minutes} min after too many wrong invite codes", Elans.Shared.IpKey.Of(ip), Strikes.BlockFor.TotalMinutes);
            Security?.Note("lockout", ip, Strikes.BlockFor.TotalMinutes + " minutes after too many wrong invite codes");
        }
    }

    public void Success(string ip) => Strikes.Success(ip);

    public enum Gate { Ok, Blocked, Wrong }

    // Every credential check of an HTTP request / WebSocket goes through here: a device token or a code, one wrong-guess count per
    // address (StrikeTable). A blocked address is refused before its credential is looked at - except when only the shared overflow
    // record is blocked (the table is full of other addresses, e.g. someone rotating through IPv6 /64s): then a VALID device token
    // still gets in (a token can't be guessed, so this answers no guessing), and a wrong one still counts and is refused as blocked.
    public Gate Admit(string ip, string credential, bool isToken, out Identity who)
    {
        who = null;
        var block = Strikes.State(ip);
        if (block == StrikeTable.Block.Own || (block == StrikeTable.Block.Overflow && !isToken)) return Gate.Blocked;
        who = CheckCredential(credential, isToken);
        if (who == null) { Fail(ip); return block == StrikeTable.Block.None ? Gate.Wrong : Gate.Blocked; }
        Success(ip);
        return Gate.Ok;
    }

    // ---- editing the codes file (the hub's Guild Master panel; lodge-admin does the same from a shell)

    static readonly System.Text.RegularExpressions.Regex NameRule = new(@"^[\p{L}\p{N}][\p{L}\p{N} _-]{0,23}$");

    // new codes: 128 bits (32 hex characters); older, shorter codes keep working
    public static string NewCode() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public string Add(string name, string role)
    {
        name = Names.Normalize(name);
        if (!NameRule.IsMatch(name)) throw new InvalidOperationException("Names are 1-24 letters (any language), digits, space, _ or -");
        var code = NewCode();
        Mutate(lines =>
        {
            if (lines.Select(Parse).Any(p => p != null && (Names.Same(p.Value.Name, name) || Names.Alike(p.Value.Name, name))))
                throw new InvalidOperationException($"{name} already has a code (or a name that looks just like it)");
            lines.Add($"{name}:{code}:{Roles.Normalize(role)}");
        });
        return code;
    }

    public void SetRole(string name, string role)
    {
        role = Roles.Normalize(role);
        Mutate(lines =>
        {
            if (role != "owner") EnsureOtherOwner(lines, name);
            ChangeLines(lines, name, p => $"{p.Name}:{p.Code}:{role}");
        });
    }

    public void Remove(string name)
    {
        Mutate(lines => { EnsureOtherOwner(lines, name); ChangeLines(lines, name, _ => null); });
        Locked(() => { RevokePendingInvitesLocked(name); DropDeviceNoticesLocked(name); return 0; });
        RevokeDevicesOf(name); // a removed person's devices stop working with them
    }

    // The one last-owner rule (leave, role change, remove): if `name` is an owner now, someone else must stay one.
    static void EnsureOtherOwner(List<string> lines, string name)
    {
        var people = lines.Select(Parse).Where(p => p != null).Select(p => p!.Value).ToList();
        if (!people.Any(p => Names.Same(p.Name, name) && p.Role == "owner")) return;
        if (!people.Any(p => p.Role == "owner" && !Names.Same(p.Name, name))) throw new InvalidOperationException(LastOwnerMessage);
    }

    // A person leaves for good: the codes entry, every device and every pending device link go; their messages stay.
    // Refused for the last Guild Master. Returns the signed-out devices (the hub closes their sockets).
    public List<DeviceInfo> Leave(string name) => Locked(() =>
    {
        ReloadIfChanged(force: true);
        MutateCodesLocked(lines => { EnsureOtherOwner(lines, name); ChangeLines(lines, name, _ => null); });
        var devs = ReadDeviceRows();
        var gone = devs.Where(d => !d.Guest && Names.Same(d.Name, name)).ToList();
        if (gone.Count > 0) { devs.RemoveAll(gone.Contains); WriteDevices(devs); }
        RevokePendingInvitesLocked(name);
        DropDeviceNoticesLocked(name);
        return gone;
    });


    static void ChangeLines(List<string> lines, string name, Func<(string Name, string Code, string Role), string> change)
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
    }

    // read-modify-write under the shared lock; the new list is written to a private temp file in the same
    // folder and renamed over the old one (atomic), so nobody ever reads a partial file
    void Mutate(Action<List<string>> edit) => Locked(() => { MutateCodesLocked(edit); return 0; });

    // the same, for a caller that already holds the lock (Locked)
    void MutateCodesLocked(Action<List<string>> edit)
    {
        var lines = File.Exists(cfg.CodesFile) ? File.ReadAllLines(cfg.CodesFile).ToList() : new List<string>();
        edit(lines);
        AtomicWrite(cfg.CodesFile, string.Join('\n', lines) + "\n");
        ReloadIfChanged(force: true);
    }

    // one process-wide gate plus the file lock shared with lodge-admin; the lock is not re-entrant (don't nest)
    T Locked<T>(Func<T> work)
    {
        lock (writeGate)
        {
            using var _ = CodesLock.Acquire(cfg.CodesFile);
            return work();
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
