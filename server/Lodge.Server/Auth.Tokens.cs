using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Lodge;

// One-time invites and per-device tokens (2.10).
//
//   <data>/invites   one tab-separated row per invite:
//                    id  sha256(secret)  kind(person|guest)  name  role  createdBy  created  expires  maxUses  uses  revoked
//   <data>/devices   one tab-separated row per device:
//                    id  sha256(token)  name(- = guest)  role  deviceName  clientDeviceId  created  lastSeen  lastIp(coarse)  addedBy (2.31.2)
//   <data>/device-notices   (2.31.2) one tab-separated row per notice the member hasn't been given yet:
//                    name  deviceId  deviceName  addedBy  at
//
// Times are unix seconds. Only HASHES of the invite secret and the device token are stored; the secrets themselves
// are shown once (on creation) and never logged. Both files share codes.lock with the codes file and lodge-admin,
// every write is a read-modify-write under that lock and an atomic rename.
// A person with devices but no permanent code is a retired line in the codes file: "Name:!<random>:role".
public sealed record InviteInfo(string Id, string Kind, string Name, string Role, string CreatedBy,
    DateTime Created, DateTime Expires, int MaxUses, int Uses, bool Revoked)
{
    public bool Used => Uses > 0;
    public bool UsedUp => Uses >= MaxUses;
    public bool Expired(DateTime now) => now >= Expires;
    public bool Pending(DateTime now) => !Revoked && !UsedUp && !Expired(now);
}

public sealed class DeviceInfo
{
    public string Id, TokenHash, Name, Role, DeviceName, ClientId, LastIp;
    public string AddedBy = "";        // 2.31.2: who added it to an existing member's account, when that wasn't the member ("" = their own)
    public DateTime Created, LastSeen;
    internal DateTime Persisted;
    public bool Guest => string.IsNullOrEmpty(Name);
}

// Status: 200 ok, 401 unknown secret (counts as a wrong guess), 410 used/expired/revoked, 409 can't add the device,
// 429 blocked by the lockout, 403 not allowed, 500 storage problem
// 2.31.2: "a new device was added to your account by someone else", waiting for the member (Name) to be told
public sealed record DeviceNotice(string Name, string DeviceId, string DeviceName, string By, DateTime At);

public sealed record ExchangeOutcome(int Status, string Error, string Reason, string Token = null, DeviceInfo Device = null, Identity Who = null);

public partial class Auth
{
    public const string InvitePrefix = "i1.";
    public const string TokenPrefix = "t1.";
    public const int MaxInviteHours = 30 * 24;

    sealed class InviteRow
    {
        public string Id, Hash, Kind, Name, Role, By;
        public long Created, Expires;
        public int Max, Uses;
        public bool Revoked;
        public InviteInfo Info() => new(Id, Kind, Name, Role, By, FromUnix(Created), FromUnix(Expires), Max, Uses, Revoked);
        public string Format() => string.Join('\t', Id, Hash, Kind, Name, Role, By, Created, Expires, Max, Uses, Revoked ? "1" : "0");
    }

    sealed record DevSnapshot(Dictionary<string, DeviceInfo> ByHash, List<DeviceInfo> All, DateTime Stamp, long Size);
    volatile DevSnapshot devSnap = new(new(), new(), DateTime.MinValue, -1);

    string DataFolder => Path.GetDirectoryName(Path.GetFullPath(cfg.CodesFile))!;
    public string InvitesFile => Path.Combine(DataFolder, "invites");
    public string DevicesFile => Path.Combine(DataFolder, "devices");
    public string DeviceNoticesFile => Path.Combine(DataFolder, "device-notices");

    static DateTime FromUnix(long s) => DateTimeOffset.FromUnixTimeSeconds(s).UtcDateTime;
    static long ToUnix(DateTime t) => new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc)).ToUnixTimeSeconds();
    long NowUnix() => ToUnix(clock.UtcNow);

    // ---- secrets

    public static bool IsRetired(string code) => code != null && code.StartsWith('!');
    static string B64Url(byte[] b) => Elans.Shared.B64.Url(b);
    public static string NewInviteSecret() => InvitePrefix + B64Url(RandomNumberGenerator.GetBytes(24));   // 192 bits
    public static string NewToken() => TokenPrefix + B64Url(RandomNumberGenerator.GetBytes(32));           // 256 bits
    static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(5)).ToLowerInvariant();
    static string RetiredCode() => "!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    public static string HashHex(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret.Trim()))).ToLowerInvariant();
    static bool SameHash(string a, string b) =>
        a.Length == b.Length && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));

    // a coarse address for the device list: IPv4 -> a.b.c.x, IPv6 -> the first 48 bits
    public static string CoarseIp(string ip)
    {
        if (!System.Net.IPAddress.TryParse(ip ?? "", out var a)) return "";
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        var b = a.GetAddressBytes();
        if (b.Length == 4) return $"{b[0]}.{b[1]}.{b[2]}.x";
        return $"{b[0]:x2}{b[1]:x2}:{b[2]:x2}{b[3]:x2}:{b[4]:x2}{b[5]:x2}::";
    }

    static string Clean(string s, int max, string fallback)
    {
        s = Names.Normalize(s ?? "");
        if (s.Length > max) s = Names.Cut(s, max).TrimEnd();
        return s.Length == 0 ? fallback : s.Replace('\t', ' ');
    }

    // ================================================================ invites

    List<InviteRow> ReadInvites()
    {
        var rows = new List<InviteRow>();
        if (!File.Exists(InvitesFile)) return rows;
        foreach (var line in File.ReadAllLines(InvitesFile))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var f = line.Split('\t');
            if (f.Length < 11) continue;
            try
            {
                rows.Add(new InviteRow
                {
                    Id = f[0], Hash = f[1], Kind = f[2], Name = f[3], Role = Roles.Normalize(f[4]), By = f[5],
                    Created = long.Parse(f[6], CultureInfo.InvariantCulture), Expires = long.Parse(f[7], CultureInfo.InvariantCulture),
                    Max = Math.Max(1, int.Parse(f[8], CultureInfo.InvariantCulture)), Uses = int.Parse(f[9], CultureInfo.InvariantCulture), Revoked = f[10] == "1",
                });
            }
            catch (FormatException) { }
        }
        return rows;
    }

    void WriteInvites(List<InviteRow> rows)
    {
        // rows long past their end are forgotten (a month after expiry)
        var cutoff = NowUnix() - 30L * 86400;
        var keep = rows.Where(r => r.Expires > cutoff);
        var body = "# Lodge invites: id, sha256(secret), kind, name, role, created by, created, expires, max uses, uses, revoked (the secrets are not stored)\n"
                   + string.Join("", keep.Select(r => r.Format() + "\n"));
        AtomicWrite(InvitesFile, body);
    }

    public List<InviteInfo> ListInvites()
    {
        lock (writeGate) return ReadInvites().Select(r => r.Info()).OrderByDescending(i => i.Created).ToList();
    }

    // the secret is returned once and stored only as a hash
    public (InviteInfo Info, string Secret) CreateInvite(string kind, string name, string role, string createdBy, int? hours = null, int? maxUses = null)
    {
        kind = kind == "guest" ? "guest" : "person";
        int h = Math.Clamp(hours ?? cfg.InviteDefaultHours, 1, MaxInviteHours);
        string secret = NewInviteSecret();
        return Locked(() =>
        {
            ReloadIfChanged();
            var rows = ReadInvites();
            var now = NowUnix();
            if (rows.Count(r => r.Info().Pending(clock.UtcNow)) >= cfg.MaxPendingInvites)
                throw new InvalidOperationException("Too many pending invites - revoke some first");
            string useName = "-", useRole = "guest";
            int uses = 1;
            if (kind == "guest") uses = Math.Clamp(maxUses ?? 1, 1, 50);
            else
            {
                name = Names.Normalize(name);
                if (!NameRule.IsMatch(name)) throw new InvalidOperationException("Names are 1-24 letters (any language), digits, space, _ or -");
                var existing = snap.Personal.FirstOrDefault(p => Names.Same(p.Name, name));
                if (existing != null) { useName = existing.Name; useRole = existing.Role; }   // another device for someone who is already in
                else
                {
                    if (snap.Personal.Any(p => Names.Alike(p.Name, name))) throw new InvalidOperationException($"{name} looks just like a name that's already taken");
                    useName = name;
                    useRole = Roles.Normalize(role);
                    if (useRole == "guest") useRole = "member";
                }
                if (rows.Any(r => r.Kind == "person" && r.Info().Pending(clock.UtcNow) && Names.Same(r.Name, useName)))
                    throw new InvalidOperationException($"{useName} already has a pending invite - revoke it first");
            }
            var row = new InviteRow
            {
                Id = NewId(), Hash = HashHex(secret), Kind = kind, Name = useName, Role = useRole, By = Clean(createdBy, 24, "?"),
                Created = now, Expires = now + h * 3600L, Max = uses, Uses = 0,
            };
            while (rows.Any(r => r.Id == row.Id)) row.Id = NewId();
            rows.Add(row);
            WriteInvites(rows);
            return (row.Info(), secret);
        });
    }

    public InviteInfo RevokeInvite(string id) => Locked(() =>
    {
        var rows = ReadInvites();
        var row = rows.FirstOrDefault(r => string.Equals(r.Id, (id ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
                  ?? throw new InvalidOperationException("No such invite");
        row.Revoked = true;
        WriteInvites(rows);
        return row.Info();
    });

    // ================================================================ exchange (invite or old code -> device token)

    public ExchangeOutcome ExchangeInvite(string ip, string invite, string deviceName, string clientDeviceId, string typedName = null)
    {
        if (Blocked(ip)) return BlockedNote(ip);
        if (string.IsNullOrWhiteSpace(invite) || invite.Length > 256) { Fail(ip); return Wrong(); }
        ExchangeOutcome res;
        try { res = Locked(() => ExchangeInviteLocked(HashHex(invite), deviceName, clientDeviceId, ip, typedName)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.LogWarning("Invite exchange failed to write: {Error}", e.GetType().Name);
            return new ExchangeOutcome(500, "The server couldn't save the new device", "storage");
        }
        if (res.Status == 401) Fail(ip); else if (res.Status == 200) Success(ip);
        return res;
    }

    // the old permanent code, exchanged once for a device token ("Upgrade this device")
    public ExchangeOutcome ExchangeCode(string ip, string code, string deviceName, string clientDeviceId)
    {
        if (Blocked(ip)) return BlockedNote(ip);
        var who = Check(code);
        if (who == null) { Fail(ip); return new ExchangeOutcome(401, "Wrong invite code", "wrong"); }
        if (!who.Personal) return new ExchangeOutcome(403, "Guests join with an invite link - ask for one", "guest");
        ExchangeOutcome res;
        try
        {
            res = Locked(() =>
            {
                var made = CreateDeviceLocked(who.Name, who.Role, deviceName, clientDeviceId, ip, out var err);
                return made == null ? err : new ExchangeOutcome(200, null, null, made.Value.Token, made.Value.Device, who with { DeviceId = made.Value.Device.Id });
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.LogWarning("Code upgrade failed to write: {Error}", e.GetType().Name);
            return new ExchangeOutcome(500, "The server couldn't save the new device", "storage");
        }
        if (res.Status == 200) Success(ip);
        return res;
    }

    ExchangeOutcome Blocked429() => new(429, "Too many wrong invite codes - try again in 15 minutes", "blocked");
    ExchangeOutcome BlockedNote(string ip) { Security?.Note("blocked", ip); return Blocked429(); }
    static ExchangeOutcome Wrong() => new(401, "This invite isn't valid - check the link", "wrong");
    static ExchangeOutcome Gone() => new(410, "This invite has been used or expired - ask for a new one", "used-or-expired");

    ExchangeOutcome ExchangeInviteLocked(string hash, string deviceName, string clientDeviceId, string ip, string typedName)
    {
        ReloadIfChanged();
        var rows = ReadInvites();
        InviteRow row = null;
        foreach (var r in rows) if (SameHash(r.Hash, hash)) row = r;   // no early exit: same work wherever the match is
        if (row == null) return Wrong();
        if (!row.Info().Pending(clock.UtcNow)) return Gone();

        string name = null, role = "guest";
        bool createPerson = false, promote = false;
        if (row.Kind == ClaimKind)
        {
            var claim = ResolveClaim(rows, row, typedName);
            if (claim.Refusal != null) return claim.Refusal;
            (name, role, createPerson, promote) = (claim.Name, "owner", claim.Create, claim.Promote);
        }
        else if (row.Kind == "person")
        {
            var existing = snap.Personal.FirstOrDefault(p => Names.Same(p.Name, row.Name));
            if (existing != null) { name = existing.Name; role = existing.Role; }
            else { name = row.Name; role = row.Role; createPerson = true; }
        }
        // limits first, nothing is written yet
        if (!DeviceRoom(name, clientDeviceId, out var refusal)) return refusal;
        if (promote) MutateCodesLocked(lines => ChangeLines(lines, name, p => $"{p.Name}:{p.Code}:owner"));
        if (createPerson)
        {
            try
            {
                MutateCodesLocked(lines =>
                {
                    if (lines.Select(Parse).Any(p => p != null && (Names.Same(p.Value.Name, name) || Names.Alike(p.Value.Name, name))))
                        throw new InvalidOperationException("name taken");
                    lines.Add($"{name}:{RetiredCode()}:{role}");
                });
            }
            catch (InvalidOperationException) { return new ExchangeOutcome(409, "That name was just taken - ask for a new invite", "name-taken"); }
        }
        // the invite is marked used BEFORE the device exists: a crash in between costs a new invite, never a free extra device
        row.Uses++;
        WriteInvites(rows);
        // 2.31.2: a device for someone who was already in, made by somebody else, records who added it - and the member will be told
        string addedBy = name != null && !createPerson && !Names.Same(row.By ?? "", name) ? Clean(row.By, 24, "?") : "";
        var made = CreateDeviceLocked(name, role, deviceName, clientDeviceId, ip, out var err, addedBy);
        if (made == null)
        {
            row.Uses--;
            try { WriteInvites(rows); } catch (IOException) { }
            return err;
        }
        if (addedBy.Length > 0)
        {
            AddDeviceNoticeLocked(new DeviceNotice(name, made.Value.Device.Id, made.Value.Device.DeviceName, addedBy, made.Value.Device.Created));
            log.LogInformation("Device {Device} was added to {Name}'s account by {By}; they will be told", made.Value.Device.Id, Names.ForLog(name), Names.ForLog(addedBy));
        }
        var who = name == null ? new Identity(null, false, "guest", made.Value.Device.Id) : new Identity(name, true, role, made.Value.Device.Id);
        log.LogInformation("Invite {Invite} used: new device {Device} for {Name}", row.Id, made.Value.Device.Id, name == null ? "a guest" : Names.ForLog(name));
        if (row.Kind == ClaimKind) log.LogInformation("Owner link {Invite} used: {Name} is now a Guild Master of this lodge", row.Id, Names.ForLog(name));
        return new ExchangeOutcome(200, null, null, made.Value.Token, made.Value.Device, who);
    }

    // ---- owner claim (2.12): a console-made, one-hour, single-use invite that creates the first Guild Master
    //   kind "owner-claim", name "-"   claim a lodge that has NO owner; the person types the name they want
    //   kind "owner-claim", name "*"   recovery: the typed name must already be an owner
    //   kind "owner-claim", name NAME  recovery for that owner
    // The secret is printed once, to the console, by lodge-admin / install.sh; it is stored only as a hash and never logged.
    public const string ClaimKind = "owner-claim";
    public const int ClaimHours = 1;
    public const string LastOwnerMessage = "The lodge needs at least one Guild Master";

    // the codes are the authority; a device that still says "owner" counts too (a damaged codes file must not make the lodge claimable)
    public bool HasOwner()
    {
        ReloadIfChanged();
        ReloadDevicesIfChanged();
        return snap.Personal.Any(p => p.Role == "owner") || devSnap.All.Any(d => !d.Guest && d.Role == "owner");
    }

    public (InviteInfo Info, string Secret) CreateOwnerClaim(string boundName = null, bool recover = false)
    {
        string secret = NewInviteSecret();
        return Locked(() =>
        {
            ReloadIfChanged(force: true);
            ReloadDevicesIfChanged(force: true);
            bool owner = HasOwner();
            string useName = "-";
            if (!recover)
            {
                if (owner) throw new InvalidOperationException("This lodge already has a Guild Master (use --recover if they are locked out)");
            }
            else
            {
                if (!owner) throw new InvalidOperationException("This lodge has no Guild Master to recover - use claim-owner");
                useName = "*";
                if (!string.IsNullOrWhiteSpace(boundName))
                {
                    var o = snap.Personal.FirstOrDefault(p => p.Role == "owner" && Names.Same(p.Name, boundName))
                            ?? throw new InvalidOperationException($"{Names.ForLog(boundName)} is not a Guild Master of this lodge");
                    useName = o.Name;
                }
            }
            var rows = ReadInvites();
            var now = NowUnix();
            foreach (var r in rows.Where(r => r.Kind == ClaimKind && r.Info().Pending(clock.UtcNow))) r.Revoked = true;   // one open claim at a time
            var row = new InviteRow
            {
                Id = NewId(), Hash = HashHex(secret), Kind = ClaimKind, Name = useName, Role = "owner", By = "console",
                Created = now, Expires = now + ClaimHours * 3600L, Max = 1, Uses = 0,
            };
            while (rows.Any(r => r.Id == row.Id)) row.Id = NewId();
            rows.Add(row);
            WriteInvites(rows);
            return (row.Info(), secret);
        });
    }

    sealed record ClaimPlan(ExchangeOutcome Refusal, string Name, bool Create, bool Promote);

    ClaimPlan ResolveClaim(List<InviteRow> rows, InviteRow row, string typed)
    {
        static ClaimPlan No(ExchangeOutcome o) => new(o, null, false, false);
        typed = Names.Normalize(typed ?? "");
        if (row.Name == "-")
        {
            if (HasOwner())
            {
                row.Revoked = true;   // somebody else got there first: this link is dead
                try { WriteInvites(rows); } catch (IOException) { }
                return No(new ExchangeOutcome(409, "This lodge already has a Guild Master", "owner-exists"));
            }
            if (!NameRule.IsMatch(typed)) return No(NameNeeded());
            var existing = snap.Personal.FirstOrDefault(p => Names.Same(p.Name, typed));
            if (existing != null) return new ClaimPlan(null, existing.Name, false, existing.Role != "owner");
            if (snap.Personal.Any(p => Names.Alike(p.Name, typed))) return No(new ExchangeOutcome(409, $"{typed} looks just like a name that's already taken - pick another", "name-taken"));
            return new ClaimPlan(null, typed, true, false);
        }
        var target = row.Name == "*" ? typed : row.Name;
        if (row.Name == "*" && !NameRule.IsMatch(target)) return No(NameNeeded());
        var o = snap.Personal.FirstOrDefault(p => p.Role == "owner" && Names.Same(p.Name, target));
        if (o == null) return No(new ExchangeOutcome(409, "That is not a Guild Master of this lodge", "not-owner"));
        return new ClaimPlan(null, o.Name, false, false);
    }

    static ExchangeOutcome NameNeeded() =>
        new(400, "Type the name you want in the lodge (1-24 letters, digits, space, _ or -)", "name-required");

    // a person who left or was removed: their pending device links stop working
    int RevokePendingInvitesLocked(string name)
    {
        var rows = ReadInvites();
        int n = 0;
        foreach (var r in rows.Where(r => r.Kind == "person" && Names.Same(r.Name, name) && r.Info().Pending(clock.UtcNow))) { r.Revoked = true; n++; }
        if (n > 0) WriteInvites(rows);
        return n;
    }

    // may this identity get another device (a re-join from the same clientDeviceId replaces its old one)?
    bool DeviceRoom(string name, string clientId, out ExchangeOutcome refusal)
    {
        refusal = null;
        var devs = ReadDeviceRows();
        var cid = Clean(clientId, 64, "?");
        int mine = devs.Count(d => (name == null ? d.Guest : Names.Same(d.Name ?? "", name)) && d.ClientId != cid);
        if (name != null && mine >= cfg.MaxDevicesPerPerson)
            refusal = new ExchangeOutcome(409, "This account has too many devices - sign one out first (Settings, Your devices)", "too-many-devices");
        else if (devs.Count >= cfg.MaxDevicesTotal)
            refusal = new ExchangeOutcome(409, "The lodge has too many devices - ask the Guild Master", "too-many-devices");
        return refusal == null;
    }

    (string Token, DeviceInfo Device)? CreateDeviceLocked(string name, string role, string deviceName, string clientDeviceId, string ip, out ExchangeOutcome err, string addedBy = "")
    {
        err = null;
        if (!DeviceRoom(name, clientDeviceId, out err)) return null;
        var devs = ReadDeviceRows();
        var cid = Clean(clientDeviceId, 64, "?");
        devs.RemoveAll(d => d.ClientId == cid && (name == null ? d.Guest : Names.Same(d.Name ?? "", name)));
        var token = NewToken();
        var now = clock.UtcNow;
        var dev = new DeviceInfo
        {
            Id = NewId(), TokenHash = HashHex(token), Name = name, Role = role, DeviceName = Clean(deviceName, 32, "Unknown device"),
            ClientId = cid, Created = now, LastSeen = now, LastIp = CoarseIp(ip), Persisted = now, AddedBy = addedBy ?? "",
        };
        while (devs.Any(d => d.Id == dev.Id)) dev.Id = NewId();
        devs.Add(dev);
        WriteDevices(devs);
        return (token, dev);
    }

    // ================================================================ devices

    static string FormatDevice(DeviceInfo d) => string.Join('\t', d.Id, d.TokenHash, d.Name ?? "-", d.Role, d.DeviceName, d.ClientId,
        ToUnix(d.Created), ToUnix(d.LastSeen), d.LastIp ?? "", (d.AddedBy ?? "").Replace('\t', ' '));

    List<DeviceInfo> ReadDeviceRows()
    {
        var list = new List<DeviceInfo>();
        if (!File.Exists(DevicesFile)) return list;
        foreach (var line in File.ReadAllLines(DevicesFile))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var f = line.Split('\t');
            if (f.Length < 8) continue;
            try
            {
                var d = new DeviceInfo
                {
                    Id = f[0], TokenHash = f[1], Name = f[2] == "-" ? null : f[2], Role = Roles.Normalize(f[3]), DeviceName = f[4], ClientId = f[5],
                    Created = FromUnix(long.Parse(f[6], CultureInfo.InvariantCulture)), LastSeen = FromUnix(long.Parse(f[7], CultureInfo.InvariantCulture)),
                    LastIp = f.Length > 8 ? f[8] : "", AddedBy = f.Length > 9 ? f[9] : "",
                };
                d.Persisted = d.LastSeen;
                list.Add(d);
            }
            catch (FormatException) { }
        }
        return list;
    }

    void WriteDevices(List<DeviceInfo> devs)
    {
        AtomicWrite(DevicesFile, "# Lodge devices: id, sha256(token), name (- = guest), role, device name, client device id, created, last seen, last address (coarse), added by\n"
                                 + string.Join("", devs.Select(d => FormatDevice(d) + "\n")));
        ReloadDevicesIfChanged(force: true);
    }

    void ReloadDevicesIfChanged(bool force = false)
    {
        DateTime stamp;
        long size;
        try
        {
            var fi = new FileInfo(DevicesFile);
            stamp = fi.Exists ? fi.LastWriteTimeUtc : DateTime.MinValue;
            size = fi.Exists ? fi.Length : -1;
        }
        catch (Exception e) { log.LogWarning("Can't check the devices file: {Error}", e.GetType().Name); return; }
        var cur = devSnap;
        if (!force && stamp == cur.Stamp && size == cur.Size) return;
        try
        {
            var all = ReadDeviceRows();
            // keep the in-memory last-seen of devices we already know (it is ahead of the file between writes)
            foreach (var d in all)
                if (cur.ByHash.TryGetValue(d.TokenHash, out var o) && o.LastSeen > d.LastSeen) { d.LastSeen = o.LastSeen; d.Persisted = o.Persisted; }
            devSnap = new DevSnapshot(all.GroupBy(d => d.TokenHash).ToDictionary(g => g.Key, g => g.First()), all, stamp, size);
        }
        catch (Exception e) { log.LogWarning("Can't read the devices file, keeping the previous list: {Error}", e.GetType().Name); }
    }

    // Looked up by the hash of the token (a 256-bit random value), so a timing difference can't help guess it.
    public Identity CheckToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 256) return null;
        ReloadDevicesIfChanged();
        ReloadIfChanged();
        if (!devSnap.ByHash.TryGetValue(HashHex(token), out var dev)) return null;
        if (dev.Guest) return new Identity(null, false, "guest", dev.Id);
        var p = snap.Personal.FirstOrDefault(x => Names.Same(x.Name, dev.Name));
        return p == null ? null : new Identity(p.Name, true, p.Role, dev.Id);
    }

    public Identity CheckCredential(string credential, bool isToken) => isToken ? CheckToken(credential) : Check(credential);

    public List<DeviceInfo> ListDevices(string name = null)
    {
        ReloadDevicesIfChanged();
        return devSnap.All.Where(d => name == null || (!d.Guest && Names.Same(d.Name, name)))
            .OrderBy(d => d.Name ?? "~", StringComparer.OrdinalIgnoreCase).ThenByDescending(d => d.LastSeen).ToList();
    }

    public DeviceInfo FindDevice(string id)
    {
        ReloadDevicesIfChanged();
        return devSnap.All.FirstOrDefault(d => string.Equals(d.Id, (id ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
    }

    // removes the device (its token stops working at once); returns it so the hub can close its socket
    public DeviceInfo RevokeDevice(string id) => Locked(() =>
    {
        var devs = ReadDeviceRows();
        var d = devs.FirstOrDefault(x => string.Equals(x.Id, (id ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException("No such device");
        devs.Remove(d);
        WriteDevices(devs);
        return d;
    });

    public List<DeviceInfo> RevokeDevicesOf(string name) => Locked(() =>
    {
        var devs = ReadDeviceRows();
        var gone = devs.Where(d => !d.Guest && Names.Same(d.Name, name)).ToList();
        if (gone.Count > 0) { devs.RemoveAll(gone.Contains); WriteDevices(devs); }
        return gone;
    });

    // a connection just authenticated with this device: remember when and from (roughly) where; written at most every 10 minutes
    public void Touch(string deviceId, string ip)
    {
        if (deviceId == null) return;
        ReloadDevicesIfChanged();
        var d = devSnap.All.FirstOrDefault(x => x.Id == deviceId);
        if (d == null) return;
        var now = clock.UtcNow;
        var coarse = CoarseIp(ip);
        bool write = now - d.Persisted > TimeSpan.FromMinutes(10) || (coarse.Length > 0 && coarse != d.LastIp);
        d.LastSeen = now;
        if (!write) return;
        try
        {
            Locked(() =>
            {
                var devs = ReadDeviceRows();
                var row = devs.FirstOrDefault(x => x.Id == deviceId);
                if (row == null) return 0;
                row.LastSeen = now;
                if (coarse.Length > 0) row.LastIp = coarse;
                WriteDevices(devs);
                return 0;
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log.LogWarning("Can't record a device's last seen: {Error}", e.GetType().Name); }
    }

    // ================================================================ device notices (2.31.2)
    // "A new device was added to your account by Y": kept until the member is told once (on one of their other devices), dropped when they
    // leave or are removed. Device names and login names only - never a token, an invite or a hash.

    public const int MaxNoticesPerPerson = 20, MaxNoticesTotal = 1000;

    List<DeviceNotice> ReadDeviceNotices()
    {
        var list = new List<DeviceNotice>();
        if (!File.Exists(DeviceNoticesFile)) return list;
        foreach (var line in File.ReadAllLines(DeviceNoticesFile))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var f = line.Split('\t');
            if (f.Length < 5) continue;
            try { list.Add(new DeviceNotice(f[0], f[1], f[2], f[3], FromUnix(long.Parse(f[4], CultureInfo.InvariantCulture)))); }
            catch (Exception e) when (e is FormatException or OverflowException or ArgumentOutOfRangeException) { }
        }
        return list;
    }

    void WriteDeviceNotices(List<DeviceNotice> list) =>
        AtomicWrite(DeviceNoticesFile, "# Lodge device notices not yet shown: name, device id, device name, added by, at\n"
            + string.Join("", list.Select(n => string.Join('\t', n.Name, n.DeviceId, n.DeviceName.Replace('\t', ' '), n.By.Replace('\t', ' '), ToUnix(n.At)) + "\n")));

    void AddDeviceNoticeLocked(DeviceNotice n)
    {
        var list = ReadDeviceNotices();
        list.Add(n);
        // bounded: the oldest of that person's, then the oldest overall, go first
        while (list.Count(x => Names.Same(x.Name, n.Name)) > MaxNoticesPerPerson) list.Remove(list.First(x => Names.Same(x.Name, n.Name)));
        while (list.Count > MaxNoticesTotal) list.RemoveAt(0);
        WriteDeviceNotices(list);
    }

    int DropDeviceNoticesLocked(string name)
    {
        var list = ReadDeviceNotices();
        int n = list.RemoveAll(x => Names.Same(x.Name, name));
        if (n > 0) WriteDeviceNotices(list);
        return n;
    }

    public List<DeviceNotice> PendingDeviceNotices(string name) { lock (writeGate) return ReadDeviceNotices().Where(x => Names.Same(x.Name, name)).ToList(); }

    // the member's notices that can be given now (deliverable), removed from the file in the same step: each is handed out exactly once
    public List<DeviceNotice> TakeDeviceNotices(string name, Func<DeviceNotice, bool> deliverable) => Locked(() =>
    {
        var list = ReadDeviceNotices();
        var take = list.Where(x => Names.Same(x.Name, name) && deliverable(x)).ToList();
        if (take.Count > 0) { list.RemoveAll(take.Contains); WriteDeviceNotices(list); }
        return take;
    });

    // ================================================================ retiring a permanent code

    public bool HasPermanentCode(string name) { ReloadIfChanged(); return snap.Personal.Any(p => Names.Same(p.Name, name) && p.Hash != null); }

    // the person keeps their name, rank and devices; the old code stops working. Refused when they have no device yet
    // (unless force) - that would lock them out.
    public void RetireCode(string name, bool force = false) => Locked(() =>
    {
        ReloadIfChanged(force: true);
        ReloadDevicesIfChanged(force: true);
        var p = snap.Personal.FirstOrDefault(x => Names.Same(x.Name, name)) ?? throw new InvalidOperationException($"No member called {name}");
        if (p.Hash == null) throw new InvalidOperationException($"{p.Name} has no permanent code (already retired)");
        if (!force && !devSnap.All.Any(d => !d.Guest && Names.Same(d.Name, p.Name)))
            throw new InvalidOperationException($"{p.Name} has no device on a token yet - retiring the code would lock them out");
        MutateCodesLocked(lines =>
        {
            for (int i = 0; i < lines.Count; i++)
            {
                var q = Parse(lines[i]);
                if (q != null && Names.Same(q.Value.Name, p.Name)) lines[i] = $"{q.Value.Name}:{RetiredCode()}:{q.Value.Role}";
            }
        });
        log.LogInformation("Permanent code of {Name} retired", Names.ForLog(p.Name));
        return 0;
    });
}
