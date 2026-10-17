using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Lodge.Tests;

// 2.10: one-time invites, per-device tokens, device revoke, retiring a permanent code. Synthetic secrets only.
public class InviteTests
{
    const string Ip = "198.51.100.7";

    // a socket that stays open until the hub closes it
    sealed class ClosableSock : WebSocket
    {
        readonly TaskCompletionSource<bool> closed = new();
        WebSocketState state = WebSocketState.Open;
        public readonly List<JsonObject> Sent = new();
        public string? CloseReason;
        public override WebSocketCloseStatus? CloseStatus => state == WebSocketState.Open ? null : WebSocketCloseStatus.PolicyViolation;
        public override string? CloseStatusDescription => CloseReason;
        public override WebSocketState State => state;
        public override string? SubProtocol => null;
        public override void Abort() { state = WebSocketState.Aborted; closed.TrySetResult(true); }
        public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken ct) { CloseReason = d; state = WebSocketState.Closed; closed.TrySetResult(true); return Task.CompletedTask; }
        public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken ct) { CloseReason = d; state = WebSocketState.CloseSent; closed.TrySetResult(true); return Task.CompletedTask; }
        public override void Dispose() { }
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
        {
            await Task.WhenAny(closed.Task, Task.Delay(Timeout.Infinite, ct));
            return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
        }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType t, bool end, CancellationToken ct)
        {
            if (t == WebSocketMessageType.Text)
                lock (Sent) Sent.Add(JsonNode.Parse(Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count))!.AsObject());
            return Task.CompletedTask;
        }
        public List<JsonObject> Of(string t) { lock (Sent) return Sent.Where(x => (string?)x["t"] == t).ToList(); }
    }

    sealed class Rig : IDisposable
    {
        public readonly TempDir Dir = new();
        public readonly FakeClock Clock = new();
        public readonly CaptureLogger Log = new();
        public readonly LodgeConfig Cfg;
        public readonly Auth Auth;
        public readonly LodgeHub Hub;
        public readonly AdminModule Admin;
        public readonly List<Task> Runs = new();
        readonly CancellationTokenSource cts = new();
        int ips;

        public Rig(Action<LodgeConfig>? tweak = null)
        {
            Cfg = T.Cfg(Dir.Path, tweak);
            T.WriteCodes(Cfg, $"Elan:{T.ElanCode}:owner", $"Bob:{T.BobCode}:officer", "Mia:synthetic0mia00code0000000000003:member");
            Auth = new Auth(Cfg, Log, Clock);
            Hub = new LodgeHub(Cfg, Auth, new FileStore(Cfg), Log, Clock);
            Admin = new AdminModule(Hub);
        }

        public (string Token, DeviceInfo Dev) Join(string name, string device = "PC", string cid = "cid1")
        {
            var (info, secret) = Auth.CreateInvite("person", name, "member", "Elan");
            var r = Auth.ExchangeInvite(Ip, secret, device, cid);
            Assert.Equal(200, r.Status);
            return (r.Token!, r.Device!);
        }

        // a connected member, as the hub would admit one
        public (Member M, ClosableSock S) Connect(string credential, bool token)
        {
            var who = Auth.CheckCredential(credential, token)!;
            Assert.NotNull(who);
            var s = new ClosableSock();
            Runs.Add(Hub.Run(s, who, credential, who.Name, "hub/test", "9.9.9." + (++ips), cts.Token));
            SpinWait.SpinUntil(() => Hub.Members.Any(m => m.Ws == s), 2000);
            return (Hub.Members.First(m => m.Ws == s), s);
        }

        public async Task<ClosableSock> Ask(Member m, ClosableSock s, object msg)
        {
            var j = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(msg))!.AsObject();
            await Admin.Handle(m, (string)j["t"]!, j);
            return s;
        }

        public void Dispose() { cts.Cancel(); try { Task.WhenAll(Runs).Wait(2000); } catch { } Dir.Dispose(); }
    }

    static string Fresh(Rig r, string name = "Nova", string role = "member", int? hours = null) => r.Auth.CreateInvite("person", name, role, "Elan", hours).Secret;

    // ---------------------------------------------------------------- format

    [Fact]
    public void Secrets_are_long_random_and_prefixed()
    {
        var a = Auth.NewInviteSecret();
        var b = Auth.NewInviteSecret();
        Assert.StartsWith("i1.", a);
        Assert.NotEqual(a, b);
        Assert.True(a.Length >= 3 + 32, "at least 192 bits of base64url");
        Assert.DoesNotContain('+', a); Assert.DoesNotContain('/', a); Assert.DoesNotContain('=', a);
        var t = Auth.NewToken();
        Assert.StartsWith("t1.", t);
        Assert.True(t.Length >= 3 + 43, "256 bits of base64url");
    }

    // ---------------------------------------------------------------- single use, expiry

    [Fact]
    public void An_invite_works_once_and_the_second_try_says_used_or_expired()
    {
        using var r = new Rig();
        var secret = Fresh(r);
        var first = r.Auth.ExchangeInvite(Ip, secret, "Nova-PC", "cid-a");
        Assert.Equal(200, first.Status);
        Assert.Equal("Nova", first.Who!.Name);
        Assert.Equal("member", first.Who.Role);
        var second = r.Auth.ExchangeInvite(Ip, secret, "Other-PC", "cid-b");
        Assert.Equal(410, second.Status);
        Assert.Contains("used or expired", second.Error);
        Assert.Null(second.Token);
        Assert.Single(r.Auth.ListDevices("Nova"));
    }

    [Fact]
    public void An_invite_expires_with_the_clock_and_default_is_seven_days()
    {
        using var r = new Rig();
        var (info, week) = r.Auth.CreateInvite("person", "Nova", "member", "Elan");
        Assert.Equal(TimeSpan.FromDays(7), info.Expires - info.Created);
        var (_, hour) = r.Auth.CreateInvite("person", "Quick", "member", "Elan", hours: 1);
        r.Clock.Advance(TimeSpan.FromMinutes(61));
        Assert.Equal(410, r.Auth.ExchangeInvite(Ip, hour, "PC", "c1").Status);    // the 1 h one is over
        r.Clock.Advance(TimeSpan.FromDays(6));
        Assert.Equal(200, r.Auth.ExchangeInvite(Ip, week, "PC", "c2").Status);    // the 7 d one still has a day left
        var (_, late) = r.Auth.CreateInvite("person", "Late", "member", "Elan");
        r.Clock.Advance(TimeSpan.FromDays(7) + TimeSpan.FromSeconds(1));
        Assert.Equal(410, r.Auth.ExchangeInvite(Ip, late, "PC", "c3").Status);
    }

    [Fact]
    public void Expiry_bounds_are_one_hour_to_thirty_days()
    {
        using var r = new Rig();
        Assert.Equal(TimeSpan.FromHours(1), Span(r.Auth.CreateInvite("person", "A1", "member", "Elan", 0).Info));
        Assert.Equal(TimeSpan.FromDays(30), Span(r.Auth.CreateInvite("person", "A2", "member", "Elan", 100000).Info));
        Assert.Equal(TimeSpan.FromHours(5), Span(r.Auth.CreateInvite("person", "A3", "member", "Elan", 5).Info));
        static TimeSpan Span(InviteInfo i) => i.Expires - i.Created;
    }

    [Fact]
    public void An_expired_invite_is_forgotten_a_month_later()
    {
        using var r = new Rig();
        Fresh(r, "Old", hours: 1);
        r.Clock.Advance(TimeSpan.FromDays(40));
        Fresh(r, "Newer");                      // any write prunes
        Assert.DoesNotContain(r.Auth.ListInvites(), i => i.Name == "Old");
    }

    [Fact]
    public async Task Concurrent_double_use_of_one_invite_exactly_one_wins()
    {
        using var r = new Rig();
        var secret = Fresh(r);
        var results = await Task.WhenAll(Enumerable.Range(0, 24).Select(i =>
            Task.Run(() => r.Auth.ExchangeInvite("203.0.113." + i, secret, "PC " + i, "cid" + i))));
        Assert.Equal(1, results.Count(x => x.Status == 200));
        Assert.Equal(23, results.Count(x => x.Status == 410));
        Assert.Single(r.Auth.ListDevices("Nova"));
        Assert.Equal(1, r.Auth.ListInvites().Single().Uses);
    }

    [Fact]
    public async Task A_guest_invite_with_three_uses_gives_three_separate_guest_devices()
    {
        using var r = new Rig();
        var (info, secret) = r.Auth.CreateInvite("guest", "", "", "Bob", maxUses: 3);
        Assert.Equal("guest", info.Kind);
        var rs = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => Task.Run(() => r.Auth.ExchangeInvite(Ip, secret, "Guest PC " + i, "g" + i))));
        Assert.Equal(3, rs.Count(x => x.Status == 200));
        Assert.Equal(3, rs.Count(x => x.Status == 410));
        var ok = rs.Where(x => x.Status == 200).ToList();
        Assert.Equal(3, ok.Select(x => x.Token).Distinct().Count());
        foreach (var x in ok)
        {
            var who = r.Auth.CheckToken(x.Token!)!;
            Assert.False(who.Personal);
            Assert.Equal("guest", who.Role);
            Assert.Null(who.Name);
        }
        // single-use is the default for guests too
        var (_, once) = r.Auth.CreateInvite("guest", "", "", "Bob");
        Assert.Equal(200, r.Auth.ExchangeInvite(Ip, once, "PC", "x").Status);
        Assert.Equal(410, r.Auth.ExchangeInvite(Ip, once, "PC", "y").Status);
    }

    [Fact]
    public void A_revoked_invite_cannot_be_used()
    {
        using var r = new Rig();
        var (info, secret) = r.Auth.CreateInvite("person", "Nova", "member", "Elan");
        r.Auth.RevokeInvite(info.Id);
        var res = r.Auth.ExchangeInvite(Ip, secret, "PC", "c");
        Assert.Equal(410, res.Status);
        Assert.Empty(r.Auth.ListDevices());
        Assert.Throws<InvalidOperationException>(() => r.Auth.RevokeInvite("nosuchid"));
    }

    // ---------------------------------------------------------------- what is stored

    [Fact]
    public void Only_hashes_are_stored_never_the_secret_or_the_token()
    {
        using var r = new Rig();
        var (info, secret) = r.Auth.CreateInvite("person", "Nova", "member", "Elan");
        var res = r.Auth.ExchangeInvite(Ip, secret, "Nova-PC", "cid-1");
        var token = res.Token!;
        var all = string.Join("\n", Directory.GetFiles(r.Dir.Path).Where(f => !f.EndsWith(".lock")).Select(File.ReadAllText));
        Assert.DoesNotContain(secret, all);
        Assert.DoesNotContain(token, all);
        Assert.DoesNotContain(secret[3..], all);
        Assert.DoesNotContain(token[3..], all);
        Assert.Contains(Auth.HashHex(secret), File.ReadAllText(r.Auth.InvitesFile));
        Assert.Contains(Auth.HashHex(token), File.ReadAllText(r.Auth.DevicesFile));
        Assert.Contains(info.Id, File.ReadAllText(r.Auth.InvitesFile));
        // the new member's codes line is a retired code, not a usable one
        var line = File.ReadAllLines(r.Cfg.CodesFile).Single(l => l.StartsWith("Nova:"));
        Assert.StartsWith("Nova:!", line);
        Assert.Null(r.Auth.Check("!" + line.Split(':')[1][1..]));
        Assert.Null(r.Auth.Check(line.Split(':')[1]));
    }

    [Fact]
    public void The_device_list_keeps_a_coarse_address_only()
    {
        using var r = new Rig();
        var (_, dev) = r.Join("Nova");
        Assert.Equal("198.51.100.x", dev.LastIp);
        Assert.Equal("2001:0db8:85a3::", Auth.CoarseIp("2001:db8:85a3:8d3:1319:8a2e:370:7348"));
        Assert.Equal("10.1.2.x", Auth.CoarseIp("::ffff:10.1.2.3"));
        Assert.DoesNotContain("7", File.ReadAllText(r.Auth.DevicesFile).Split('\n').Last(l => l.Length > 0).Split('\t')[^1].Replace("198.51.100.x", ""));
    }

    // ---------------------------------------------------------------- token auth, old codes

    [Fact]
    public void A_device_token_signs_in_as_the_person_with_their_rank()
    {
        using var r = new Rig();
        var (token, dev) = r.Join("Nova");
        var who = r.Auth.CheckToken(token)!;
        Assert.Equal("Nova", who.Name);
        Assert.True(who.Personal);
        Assert.Equal("member", who.Role);
        Assert.Equal(dev.Id, who.DeviceId);
        Assert.Null(r.Auth.CheckToken("t1.not-a-real-token"));
        Assert.Null(r.Auth.CheckToken(""));
        Assert.Null(r.Auth.CheckToken(new string('x', 300)));
        Assert.Null(r.Auth.Check(token));                       // a token is not a code
        r.Auth.SetRole("Nova", "veteran");
        Assert.Equal("veteran", r.Auth.CheckToken(token)!.Role); // the rank follows the person, not the token
    }

    [Fact]
    public void Old_personal_codes_and_the_guest_code_keep_working_next_to_tokens()
    {
        using var r = new Rig(c => c.GetType().GetProperty("Code")!.SetValue(c, "synthetic-guest-code"));
        r.Join("Nova");
        Assert.Equal("Elan", r.Auth.Check(T.ElanCode)!.Name);
        Assert.Equal("owner", r.Auth.Check(T.ElanCode)!.Role);
        Assert.Equal("Bob", r.Auth.Check(T.BobCode)!.Name);
        Assert.False(r.Auth.Check("synthetic-guest-code")!.Personal);
        Assert.Null(r.Auth.Check("nope-nope-nope"));
    }

    [Fact]
    public void An_unknown_token_is_not_accepted_as_a_code_by_the_hub_even_with_a_matching_name()
    {
        using var r = new Rig();
        var (token, _) = r.Join("Nova");
        Assert.Null(r.Auth.CheckCredential(token, isToken: false));
        Assert.Null(r.Auth.CheckCredential(T.ElanCode, isToken: true));
    }

    // ---------------------------------------------------------------- upgrade a code to a token

    [Fact]
    public void An_old_code_is_exchanged_for_a_token_and_the_code_stays_valid()
    {
        using var r = new Rig();
        var res = r.Auth.ExchangeCode(Ip, T.BobCode, "Bob-laptop", "bob-cid");
        Assert.Equal(200, res.Status);
        var who = r.Auth.CheckToken(res.Token!)!;
        Assert.Equal("Bob", who.Name);
        Assert.Equal("officer", who.Role);
        Assert.Equal("Bob", r.Auth.Check(T.BobCode)!.Name);                   // still valid until the owner retires it
        Assert.Single(r.Auth.ListDevices("Bob"));
        // doing it again from the same device replaces, it does not pile up
        var again = r.Auth.ExchangeCode(Ip, T.BobCode, "Bob-laptop", "bob-cid");
        Assert.Equal(200, again.Status);
        Assert.Single(r.Auth.ListDevices("Bob"));
        Assert.Null(r.Auth.CheckToken(res.Token!));
        Assert.NotNull(r.Auth.CheckToken(again.Token!));
    }

    [Fact]
    public void Upgrading_with_a_wrong_code_is_a_strike_and_a_guest_code_is_refused()
    {
        using var r = new Rig(c => c.GetType().GetProperty("Code")!.SetValue(c, "synthetic-guest-code"));
        Assert.Equal(401, r.Auth.ExchangeCode(Ip, "wrong-code-wrong", "PC", "c").Status);
        Assert.Equal(403, r.Auth.ExchangeCode(Ip, "synthetic-guest-code", "PC", "c").Status);
    }

    // ---------------------------------------------------------------- lockout

    [Fact]
    public void Wrong_invites_share_the_code_guess_lockout()
    {
        using var r = new Rig();
        var valid = Fresh(r);
        for (int i = 1; i <= 9; i++) Assert.Equal(401, r.Auth.ExchangeInvite(Ip, "i1.wrong" + i, "PC", "c").Status);
        Assert.Equal(401, r.Auth.ExchangeInvite(Ip, "i1.wrong10", "PC", "c").Status);  // the 10th is still 401...
        Assert.True(r.Auth.Blocked(Ip));
        var blocked = r.Auth.ExchangeInvite(Ip, valid, "PC", "c");                       // ...then 429, even with a valid invite
        Assert.Equal(429, blocked.Status);
        Assert.Empty(r.Auth.ListDevices());
        Assert.Equal(429, r.Auth.ExchangeCode(Ip, T.ElanCode, "PC", "c").Status);        // and a valid old code is blocked too
        Assert.Null(r.Auth.Strikes.IsBlocked("10.0.0.1") ? "x" : null);                  // other addresses are fine
        r.Clock.Advance(TimeSpan.FromMinutes(16));
        Assert.False(r.Auth.Blocked(Ip));
        Assert.Equal(200, r.Auth.ExchangeInvite(Ip, valid, "PC", "c").Status);
    }

    [Fact]
    public void Wrong_codes_and_wrong_invites_count_together()
    {
        using var r = new Rig();
        for (int i = 0; i < 5; i++) r.Auth.Fail(Ip);                                   // five wrong codes (as /ws would)
        for (int i = 0; i < 4; i++) Assert.Equal(401, r.Auth.ExchangeInvite(Ip, "i1.guess" + i, "PC", "c").Status);
        Assert.False(r.Auth.Blocked(Ip));
        Assert.Equal(401, r.Auth.ExchangeInvite(Ip, "i1.guess-last", "PC", "c").Status);
        Assert.True(r.Auth.Blocked(Ip));
        Assert.Equal(429, r.Auth.ExchangeInvite(Ip, "i1.anything", "PC", "c").Status);
    }

    [Fact]
    public void A_used_or_expired_invite_is_not_a_strike()
    {
        using var r = new Rig();
        var secret = Fresh(r);
        r.Auth.ExchangeInvite(Ip, secret, "PC", "c");
        for (int i = 0; i < 30; i++) Assert.Equal(410, r.Auth.ExchangeInvite(Ip, secret, "PC", "c").Status);
        Assert.False(r.Auth.Blocked(Ip));                                              // someone re-pasting an old link is not locked out
    }

    // ---------------------------------------------------------------- devices

    [Fact]
    public void Joining_again_from_the_same_device_replaces_its_token_and_the_cap_is_per_person()
    {
        using var r = new Rig(c => c.MaxDevicesPerPerson = 3);
        var (t1, d1) = r.Join("Nova", "PC", "same-device");
        var (t2, d2) = r.Join("Nova", "PC", "same-device");
        Assert.Single(r.Auth.ListDevices("Nova"));
        Assert.Null(r.Auth.CheckToken(t1));
        Assert.NotNull(r.Auth.CheckToken(t2));
        r.Join("Nova", "Laptop", "d2");
        r.Join("Nova", "Deck", "d3");
        var (_, secret) = r.Auth.CreateInvite("person", "Nova", "member", "Elan");
        var res = r.Auth.ExchangeInvite(Ip, secret, "Fourth", "d4");
        Assert.Equal(409, res.Status);
        Assert.Equal("too-many-devices", res.Reason);
        Assert.Equal(3, r.Auth.ListDevices("Nova").Count);
        // the refusal didn't burn the invite
        r.Auth.RevokeDevice(d2.Id);
        Assert.Equal(200, r.Auth.ExchangeInvite(Ip, secret, "Fourth", "d4").Status);
    }

    [Fact]
    public void An_invite_for_an_existing_member_adds_a_device_with_their_current_rank()
    {
        using var r = new Rig();
        var (info, secret) = r.Auth.CreateInvite("person", "bob", "member", "Elan");      // Bob exists (officer): rank is theirs, not the request's
        Assert.Equal("Bob", info.Name);
        Assert.Equal("officer", info.Role);
        var res = r.Auth.ExchangeInvite(Ip, secret, "New PC", "c");
        Assert.Equal(200, res.Status);
        Assert.Equal("officer", r.Auth.CheckToken(res.Token!)!.Role);
        Assert.Equal(1, File.ReadAllLines(r.Cfg.CodesFile).Count(l => l.StartsWith("Bob:")));
    }

    [Fact]
    public void Names_follow_the_server_rules_and_a_lookalike_is_refused()
    {
        using var r = new Rig();
        Assert.Throws<InvalidOperationException>(() => r.Auth.CreateInvite("person", "bad:name", "member", "Elan"));
        Assert.Throws<InvalidOperationException>(() => r.Auth.CreateInvite("person", "", "member", "Elan"));
        Assert.Throws<InvalidOperationException>(() => r.Auth.CreateInvite("person", "Εlan", "member", "Elan"));   // Greek capital Epsilon: looks like Elan
        var (info, _) = r.Auth.CreateInvite("person", "Åse Ørsted", "veteran", "Elan");                             // any letters
        Assert.Equal("Åse Ørsted", info.Name);
        Assert.Throws<InvalidOperationException>(() => r.Auth.CreateInvite("person", "Åse Ørsted", "member", "Elan")); // already pending
    }

    [Fact]
    public void Revoking_a_device_stops_its_token_and_the_member_list_keeps_the_person()
    {
        using var r = new Rig();
        var (token, dev) = r.Join("Nova");
        r.Auth.RevokeDevice(dev.Id);
        Assert.Null(r.Auth.CheckToken(token));
        Assert.Contains(r.Auth.ListPersonal(), p => p.Name == "Nova");
        Assert.Throws<InvalidOperationException>(() => r.Auth.RevokeDevice(dev.Id));
    }

    [Fact]
    public void Removing_a_person_removes_their_devices()
    {
        using var r = new Rig();
        var (token, _) = r.Join("Nova");
        r.Auth.Remove("Nova");
        Assert.Null(r.Auth.CheckToken(token));
        Assert.Empty(r.Auth.ListDevices());
    }

    [Fact]
    public async Task Revoking_a_device_closes_its_socket_at_once_and_only_that_one()
    {
        using var r = new Rig();
        var (tokenA, devA) = r.Join("Nova", "Desktop", "a");
        var (tokenB, devB) = r.Join("Mira", "Laptop", "b");
        var (a, sa) = r.Connect(tokenA, true);
        var (b, sb) = r.Connect(tokenB, true);
        var (elan, selan) = r.Connect(T.ElanCode, false);
        Assert.Equal(devA.Id, a.DeviceId);
        await r.Ask(elan, selan, new { t = "device.revoke", id = devA.Id });
        Assert.Equal("This device was signed out", sa.CloseReason);
        Assert.Equal(WebSocketState.CloseSent, sa.State);
        Assert.Equal(WebSocketState.Open, sb.State);
        Assert.DoesNotContain(r.Hub.Members, m => m.Id == a.Id);
        Assert.Contains(r.Hub.Members, m => m.Id == b.Id);
        Assert.Null(r.Auth.CheckToken(tokenA));
        Assert.NotNull(r.Auth.CheckToken(tokenB));
    }

    [Fact]
    public async Task A_member_can_sign_out_their_own_device_but_not_someone_elses()
    {
        using var r = new Rig();
        var (tNova, dNova) = r.Join("Nova", "Desktop", "a");
        var (tNova2, dNova2) = r.Join("Nova", "Laptop", "a2");
        var (tMira, dMira) = r.Join("Mira", "Desktop", "b");
        var (nova, snova) = r.Connect(tNova, true);
        await r.Ask(nova, snova, new { t = "device.list" });
        var list = snova.Of("devices").Last()["devices"]!.AsArray();
        Assert.Equal(2, list.Count);
        Assert.Single(list.Where(d => (bool)d!["current"]!));
        Assert.DoesNotContain("t1.", snova.Sent.Last().ToJsonString());      // never a token in a list
        await r.Ask(nova, snova, new { t = "device.revoke", id = dMira.Id });  // Mira's: refused
        Assert.NotNull(r.Auth.CheckToken(tMira));
        Assert.Contains("rank", (string)snova.Of("error").Last()["text"]!);
        await r.Ask(nova, snova, new { t = "device.revoke", id = dNova2.Id }); // own other device: ok
        Assert.Null(r.Auth.CheckToken(tNova2));
        Assert.NotNull(r.Auth.CheckToken(tNova));
    }

    [Fact]
    public async Task An_officer_sees_and_signs_out_lower_ranks_devices_but_not_the_owners()
    {
        using var r = new Rig();
        var (tMia, dMia) = r.Join("Nova", "Desktop", "a");
        var ownerDev = r.Auth.ExchangeCode(Ip, T.ElanCode, "Elan PC", "e");
        var (bob, sbob) = r.Connect(T.BobCode, false);
        await r.Ask(bob, sbob, new { t = "admin.devices" });
        var all = sbob.Of("admin.devices").Last()["devices"]!.AsArray();
        Assert.Equal(2, all.Count);
        await r.Ask(bob, sbob, new { t = "device.revoke", id = ownerDev.Device!.Id });
        Assert.NotNull(r.Auth.CheckToken(ownerDev.Token!));                      // owner's: refused
        await r.Ask(bob, sbob, new { t = "device.revoke", id = dMia.Id });
        Assert.Null(r.Auth.CheckToken(tMia));                                     // member's: done
        // a plain member cannot use the admin list
        var (nova, snova) = r.Connect(r.Join("Zed").Token, true);
        await r.Ask(nova, snova, new { t = "admin.devices" });
        Assert.Contains("rank", (string)snova.Of("error").Last()["text"]!);
    }

    [Fact]
    public async Task A_token_removed_from_the_file_by_lodge_admin_disconnects_within_the_check()
    {
        using var r = new Rig();
        var (token, dev) = r.Join("Nova");
        var (_, s) = r.Connect(token, true);
        // what `lodge-admin revoke-device` does: drop the row from the file
        File.WriteAllLines(r.Auth.DevicesFile, File.ReadAllLines(r.Auth.DevicesFile).Where(l => !l.StartsWith(dev.Id)));
        await r.Hub.ApplyCodeChanges();
        Assert.Equal("This device was signed out", s.CloseReason);
        Assert.Empty(r.Hub.Members);
    }

    [Fact]
    public void Last_seen_is_remembered_and_written_at_most_every_ten_minutes()
    {
        using var r = new Rig();
        var (_, dev) = r.Join("Nova");
        var before = File.ReadAllText(r.Auth.DevicesFile);
        r.Clock.Advance(TimeSpan.FromMinutes(3));
        r.Auth.Touch(dev.Id, Ip);
        Assert.Equal(before, File.ReadAllText(r.Auth.DevicesFile));                     // too soon: memory only
        Assert.Equal(r.Clock.UtcNow, r.Auth.FindDevice(dev.Id)!.LastSeen);
        r.Clock.Advance(TimeSpan.FromMinutes(20));
        r.Auth.Touch(dev.Id, "203.0.113.9");
        Assert.NotEqual(before, File.ReadAllText(r.Auth.DevicesFile));
        Assert.Equal("203.0.113.x", r.Auth.FindDevice(dev.Id)!.LastIp);
    }

    // ---------------------------------------------------------------- retire a permanent code

    [Fact]
    public void Retiring_a_code_needs_a_device_first_then_only_the_code_stops()
    {
        using var r = new Rig();
        var ex = Assert.Throws<InvalidOperationException>(() => r.Auth.RetireCode("Bob"));
        Assert.Contains("lock them out", ex.Message);
        Assert.NotNull(r.Auth.Check(T.BobCode));
        var up = r.Auth.ExchangeCode(Ip, T.BobCode, "Bob PC", "b");
        r.Auth.RetireCode("bob");
        Assert.Null(r.Auth.Check(T.BobCode));                                    // the old code is dead
        var who = r.Auth.CheckToken(up.Token!)!;                                 // the device is fine, same name and rank
        Assert.Equal("Bob", who.Name);
        Assert.Equal("officer", who.Role);
        Assert.Contains(r.Auth.ListPersonal(), p => p.Name == "Bob" && p.Role == "officer");
        Assert.False(r.Auth.HasPermanentCode("Bob"));
        Assert.Throws<InvalidOperationException>(() => r.Auth.RetireCode("Bob"));    // already
        Assert.Throws<InvalidOperationException>(() => r.Auth.RetireCode("Nobody", force: true));
        r.Auth.SetRole("Bob", "veteran");                                         // role edits keep the retired marker
        Assert.False(r.Auth.HasPermanentCode("Bob"));
        Assert.Equal("veteran", r.Auth.CheckToken(up.Token!)!.Role);
    }

    [Fact]
    public async Task Only_the_owner_retires_codes_and_a_connected_old_code_user_is_disconnected()
    {
        using var r = new Rig();
        r.Auth.ExchangeCode(Ip, T.BobCode, "Bob PC", "b");
        var (bob, sbob) = r.Connect(T.BobCode, false);
        await r.Ask(bob, sbob, new { t = "admin.retire", name = "Bob" });         // an officer may not
        Assert.Contains("rank", (string)sbob.Of("error").Last()["text"]!);
        Assert.True(r.Auth.HasPermanentCode("Bob"));
        var (elan, selan) = r.Connect(T.ElanCode, false);
        await r.Ask(elan, selan, new { t = "admin.retire", name = "Bob" });
        Assert.False(r.Auth.HasPermanentCode("Bob"));
        Assert.Equal("Your invite code was removed", sbob.CloseReason);
        var members = selan.Of("admin.members").Last()["members"]!.AsArray();
        var b = members.Single(x => (string)x!["name"]! == "Bob")!;
        Assert.False((bool)b["hasCode"]!);
        Assert.Equal(1, (int)b["devices"]!);
    }

    // ---------------------------------------------------------------- admin messages

    [Fact]
    public async Task Officers_create_invites_below_their_rank_and_the_secret_is_sent_once()
    {
        using var r = new Rig();
        var (bob, sbob) = r.Connect(T.BobCode, false);
        await r.Ask(bob, sbob, new { t = "admin.invite.create", kind = "person", name = "Nova", role = "member", hours = 24 });
        var made = sbob.Of("admin.invite.created").Single();
        var secret = (string)made["secret"]!;
        Assert.StartsWith("i1.", secret);
        Assert.Equal("Nova", (string)made["name"]!);
        var listed = sbob.Of("admin.invites").Last();
        Assert.DoesNotContain(secret, listed.ToJsonString());                    // the list has no secret...
        Assert.DoesNotContain(Auth.HashHex(secret), listed.ToJsonString());      // ...nor its hash
        var row = listed["invites"]!.AsArray().Single()!;
        Assert.Equal("Bob", (string)row["by"]!);
        Assert.True((bool)row["pending"]!);
        Assert.False((bool)row["used"]!);
        Assert.Equal(24 * 3600 * 1000L, (long)row["expires"]! - (long)row["created"]!);
        await r.Ask(bob, sbob, new { t = "admin.invite.create", kind = "person", name = "Rex", role = "officer" });
        Assert.Contains("below your own", (string)sbob.Of("error").Last()["text"]!);
        await r.Ask(bob, sbob, new { t = "admin.invite.create", kind = "person", name = "Elan", role = "member" });   // would give a device on the owner
        Assert.Contains("below your rank", (string)sbob.Of("error").Last()["text"]!);
        await r.Ask(bob, sbob, new { t = "admin.invite.create", kind = "guest", uses = 5 });
        Assert.Equal(5, (int)sbob.Of("admin.invite.created").Last()["max"]!);
        // after use the list says so
        r.Auth.ExchangeInvite(Ip, secret, "PC", "c");
        await r.Ask(bob, sbob, new { t = "admin.invites" });
        var after = sbob.Of("admin.invites").Last()["invites"]!.AsArray().Single(x => (string)x!["name"]! == "Nova")!;
        Assert.True((bool)after["used"]!);
        Assert.False((bool)after["pending"]!);
        // revoke from the panel
        var id = (string)sbob.Of("admin.invite.created").Last()["id"]!;
        await r.Ask(bob, sbob, new { t = "admin.invite.revoke", id });
        Assert.True((bool)sbob.Of("admin.invites").Last()["invites"]!.AsArray().Single(x => (string)x!["id"]! == id)!["revoked"]!);
    }

    [Fact]
    public async Task A_plain_member_cannot_create_list_or_revoke_invites()
    {
        using var r = new Rig();
        var (tok, _) = r.Join("Nova");
        var (nova, s) = r.Connect(tok, true);
        foreach (var msg in new object[] { new { t = "admin.invite.create", kind = "guest" }, new { t = "admin.invites" }, new { t = "admin.invite.revoke", id = "x" }, new { t = "admin.retire", name = "Bob" } })
            await r.Ask(nova, s, msg);
        Assert.Equal(4, s.Of("error").Count);
        Assert.Single(r.Auth.ListInvites());      // only the one Join() made
    }

    // ---------------------------------------------------------------- welcome and secrets in logs

    [Fact]
    public void The_welcome_announces_tokens_and_the_device_you_are_on()
    {
        using var r = new Rig();
        var (token, dev) = r.Join("Nova", "Nova's PC");
        var (nova, _) = r.Connect(token, true);
        var w = r.Hub.Welcome(nova);
        Assert.Equal("2.27.0", (string)w["server"]!);
        var features = w["features"]!.AsArray().Select(x => (string)x!).ToList();
        Assert.Contains("tokens", features);
        Assert.Contains("invites", features);
        Assert.Equal(dev.Id, (string)w["device"]!["id"]!);
        Assert.False((bool)w["canInvite"]!);
        var (elan, _) = r.Connect(T.ElanCode, false);
        Assert.True((bool)r.Hub.Welcome(elan)["canInvite"]!);
        Assert.Null(r.Hub.Welcome(elan)["device"]);
    }

    [Fact]
    public async Task Logs_never_contain_invites_tokens_codes_or_their_hashes()
    {
        using var r = new Rig();
        var (bob, sbob) = r.Connect(T.BobCode, false);
        await r.Ask(bob, sbob, new { t = "admin.invite.create", kind = "person", name = "Nova", role = "member" });
        var secret = (string)sbob.Of("admin.invite.created").Single()["secret"]!;
        var ex = r.Auth.ExchangeInvite(Ip, secret, "Nova's PC", "cid");
        r.Auth.ExchangeInvite(Ip, secret, "again", "cid2");                 // used
        r.Auth.ExchangeInvite(Ip, "i1.guessed-secret-value", "x", "y");     // wrong
        var up = r.Auth.ExchangeCode(Ip, T.ElanCode, "Elan PC", "e");
        var (nova, snova) = r.Connect(ex.Token!, true);
        await r.Ask(nova, snova, new { t = "device.list" });
        r.Auth.RetireCode("Elan");
        await r.Hub.ApplyCodeChanges();
        r.Auth.RevokeDevice(ex.Device!.Id);
        await r.Hub.ApplyCodeChanges();
        for (int i = 0; i < 12; i++) r.Auth.ExchangeInvite("192.0.2.5", "i1.blast" + i, "x", "y");   // gets the IP blocked: logs a warning
        string log;
        lock (r.Log.Lines) log = string.Join("\n", r.Log.Lines);
        Assert.False(string.IsNullOrWhiteSpace(log));
        foreach (var secretValue in new[] { secret, secret[3..], ex.Token!, ex.Token![3..], up.Token!, T.ElanCode, T.BobCode, "i1.guessed-secret-value",
                     Auth.HashHex(secret), Auth.HashHex(ex.Token!), Auth.HashHex(T.ElanCode) })
            Assert.DoesNotContain(secretValue, log);
        // what is logged is ids and names
        Assert.Contains(ex.Device!.Id, log);
    }

    [Fact]
    public void Hub_logs_a_failed_write_without_secrets()
    {
        using var r = new Rig();
        var secret = Fresh(r);
        File.Delete(r.Auth.DevicesFile);
        Directory.CreateDirectory(r.Auth.DevicesFile);      // writing the devices file now fails
        var res = r.Auth.ExchangeInvite(Ip, secret, "PC", "c");
        Assert.True(res.Status is 500 or 409);
        string log;
        lock (r.Log.Lines) log = string.Join("\n", r.Log.Lines);
        Assert.DoesNotContain(secret, log);
    }
}
