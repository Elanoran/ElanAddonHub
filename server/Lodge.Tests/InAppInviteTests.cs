using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Lodge.Tests;

// 2.12: owner claim (new lodge), recovery, leaving the lodge, the last-owner guard everywhere, the lodge name / welcome text and the
// public web invite page. Synthetic names and secrets only.
public class InAppInviteTests
{
    const string Ip = "198.51.100.9";

    sealed class Sock : WebSocket
    {
        readonly TaskCompletionSource<bool> closed = new();
        WebSocketState state = WebSocketState.Open;
        public readonly List<JsonObject> Sent = new();
        public string? CloseReason;
        public override WebSocketCloseStatus? CloseStatus => state == WebSocketState.Open ? null : WebSocketCloseStatus.PolicyViolation;
        public override string? CloseStatusDescription => CloseReason;
        public override WebSocketState State => state;
        public override string? SubProtocol => null;
        public bool Closed => state != WebSocketState.Open;
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
        readonly List<Task> runs = new();
        readonly CancellationTokenSource cts = new();
        int ips;

        public Rig(params string[] codes)
        {
            Cfg = T.Cfg(Dir.Path);
            T.WriteCodes(Cfg, codes);
            Auth = new Auth(Cfg, Log, Clock);
            Hub = new LodgeHub(Cfg, Auth, new FileStore(Cfg), Log, Clock);
            Admin = new AdminModule(Hub);
        }

        public static Rig WithOwner() => new($"Elan:{T.ElanCode}:owner", $"Bob:{T.BobCode}:officer", "Mia:synthetic0mia00code0000000000003:member");

        public (string Token, DeviceInfo Dev) Join(string name, string device = "PC", string cid = "cid1")
        {
            var (_, secret) = Auth.CreateInvite("person", name, "member", "Elan");
            var r = Auth.ExchangeInvite(Ip, secret, device, cid);
            Assert.Equal(200, r.Status);
            return (r.Token!, r.Device!);
        }

        public (Member M, Sock S) Connect(string credential, bool token)
        {
            var who = Auth.CheckCredential(credential, token)!;
            Assert.NotNull(who);
            var s = new Sock();
            runs.Add(Hub.Run(s, who, credential, who.Name, "hub/test", "9.9.9." + (++ips), cts.Token));
            SpinWait.SpinUntil(() => Hub.Members.Any(m => m.Ws == s), 2000);
            return (Hub.Members.First(m => m.Ws == s), s);
        }

        public async Task Ask(Member m, object msg)
        {
            var j = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(msg))!.AsObject();
            Assert.True(await Admin.Handle(m, (string)j["t"]!, j));
        }

        public string Everything()
        {
            var sb = new StringBuilder();
            foreach (var f in new[] { Cfg.CodesFile, Auth.InvitesFile, Auth.DevicesFile }) if (File.Exists(f)) sb.Append(File.ReadAllText(f));
            lock (Log.Lines) sb.Append(string.Join("\n", Log.Lines));
            return sb.ToString();
        }

        public void Dispose() { cts.Cancel(); try { Task.WhenAll(runs).Wait(2000); } catch { } Dir.Dispose(); }
    }

    // ================================================================ owner claim

    [Fact]
    public void Claim_is_only_possible_while_there_is_no_owner()
    {
        using var r = new Rig("Mia:synthetic0mia00code0000000000003:member");
        Assert.False(r.Auth.HasOwner());
        var (info, secret) = r.Auth.CreateOwnerClaim();
        Assert.Equal("owner-claim", info.Kind);
        Assert.Equal(TimeSpan.FromHours(1), info.Expires - info.Created);
        Assert.Equal(1, info.MaxUses);
        Assert.StartsWith("i1.", secret);

        using var withOwner = Rig.WithOwner();
        Assert.True(withOwner.Auth.HasOwner());
        var e = Assert.Throws<InvalidOperationException>(() => withOwner.Auth.CreateOwnerClaim());
        Assert.Contains("already has a Guild Master", e.Message);
    }

    [Fact]
    public void Claim_asks_for_a_name_then_creates_the_owner_with_a_device_and_no_permanent_code()
    {
        using var r = new Rig();   // an empty lodge
        var (_, secret) = r.Auth.CreateOwnerClaim();
        var noName = r.Auth.ExchangeInvite(Ip, secret, "Home PC", "cid-1");
        Assert.Equal(400, noName.Status);
        Assert.Equal("name-required", noName.Reason);
        var bad = r.Auth.ExchangeInvite(Ip, secret, "Home PC", "cid-1", "<b>x:y");
        Assert.Equal(400, bad.Status);                                  // neither attempt used the link up
        var ok = r.Auth.ExchangeInvite(Ip, secret, "Home PC", "cid-1", "  Aurelia ");
        Assert.Equal(200, ok.Status);
        Assert.Equal("Aurelia", ok.Who!.Name);
        Assert.Equal("owner", ok.Who.Role);
        Assert.NotNull(ok.Token);
        Assert.True(r.Auth.HasOwner());
        Assert.False(r.Auth.HasPermanentCode("Aurelia"), "no permanent code is created");
        Assert.Equal("owner", r.Auth.CheckToken(ok.Token!)!.Role);
        Assert.Single(r.Auth.ListDevices("Aurelia"));
        // the link is single use
        Assert.Equal(410, r.Auth.ExchangeInvite(Ip, secret, "Other PC", "cid-2", "Aurelia").Status);
        // and now there is an owner, a new claim can't be made
        Assert.Throws<InvalidOperationException>(() => r.Auth.CreateOwnerClaim());
    }

    [Fact]
    public void Claim_expires_after_one_hour_and_a_new_one_replaces_the_old()
    {
        using var r = new Rig();
        var (_, old) = r.Auth.CreateOwnerClaim();
        var (_, fresh) = r.Auth.CreateOwnerClaim();
        Assert.Equal(410, r.Auth.ExchangeInvite(Ip, old, "PC", "c", "Aurelia").Status);   // revoked by the newer claim
        r.Clock.Advance(TimeSpan.FromMinutes(61));
        Assert.Equal(410, r.Auth.ExchangeInvite(Ip, fresh, "PC", "c", "Aurelia").Status);
        Assert.False(r.Auth.HasOwner());
    }

    [Fact]
    public void Claim_link_is_dead_when_an_owner_appeared_in_the_meantime()
    {
        using var r = new Rig();
        var (_, claim) = r.Auth.CreateOwnerClaim();
        r.Auth.Add("Boss", "owner");   // a legacy code made first
        var res = r.Auth.ExchangeInvite(Ip, claim, "PC", "c", "Aurelia");
        Assert.Equal(409, res.Status);
        Assert.Equal("owner-exists", res.Reason);
        Assert.Equal(410, r.Auth.ExchangeInvite(Ip, claim, "PC", "c", "Aurelia").Status);   // burned
        Assert.Empty(r.Auth.ListDevices("Aurelia"));
    }

    [Fact]
    public void Claim_uses_the_name_rules()
    {
        using var r = new Rig("Mia:synthetic0mia00code0000000000003:member");
        var (_, claim) = r.Auth.CreateOwnerClaim();
        Assert.Equal(400, r.Auth.ExchangeInvite(Ip, claim, "PC", "c", "no:colons").Status);
        Assert.Equal(400, r.Auth.ExchangeInvite(Ip, claim, "PC", "c", "   ").Status);
        Assert.Equal(409, r.Auth.ExchangeInvite(Ip, claim, "PC", "c", "Мia").Status);   // Cyrillic M: looks just like Mia
        Assert.False(r.Auth.HasOwner());
        Assert.Equal(200, r.Auth.ExchangeInvite(Ip, claim, "PC", "c", "Miaæ").Status);   // none of the refusals used the link up
    }

    [Fact]
    public void Claim_with_the_name_of_an_existing_member_promotes_that_member()
    {
        using var r = new Rig("Mia:synthetic0mia00code0000000000003:member");
        var (_, claim) = r.Auth.CreateOwnerClaim();
        var res = r.Auth.ExchangeInvite(Ip, claim, "PC", "c", "mia");
        Assert.Equal(200, res.Status);
        Assert.Equal("Mia", res.Who!.Name);
        Assert.Equal("owner", res.Who.Role);
        Assert.Contains(r.Auth.ListPersonal(), p => p.Name == "Mia" && p.Role == "owner");
        Assert.True(r.Auth.CheckCredential("synthetic0mia00code0000000000003", false) != null, "her old code still works");
    }

    [Fact]
    public void Claim_secret_is_stored_only_hashed_and_never_logged()
    {
        using var r = new Rig();
        var (_, secret) = r.Auth.CreateOwnerClaim();
        var res = r.Auth.ExchangeInvite(Ip, secret, "PC", "c", "Aurelia");
        Assert.Equal(200, res.Status);
        r.Auth.ExchangeInvite(Ip, secret, "PC", "c", "Aurelia");   // a failing try as well
        var all = r.Everything();
        Assert.DoesNotContain(secret, all);
        Assert.DoesNotContain(secret["i1.".Length..], all);
        Assert.DoesNotContain(res.Token!, all);
        Assert.Contains(Auth.HashHex(secret), all);
    }

    // ================================================================ recovery

    [Fact]
    public void Recovery_needs_an_owner_and_adds_a_device_for_an_existing_owner_only()
    {
        using var empty = new Rig();
        Assert.Throws<InvalidOperationException>(() => empty.Auth.CreateOwnerClaim(null, recover: true));

        using var r = Rig.WithOwner();
        Assert.Throws<InvalidOperationException>(() => r.Auth.CreateOwnerClaim("Mia", recover: true));      // not an owner
        Assert.Throws<InvalidOperationException>(() => r.Auth.CreateOwnerClaim("Nobody", recover: true));
        var codesBefore = File.ReadAllText(r.Cfg.CodesFile);

        var (info, bound) = r.Auth.CreateOwnerClaim("elan", recover: true);
        Assert.Equal("Elan", info.Name);
        var res = r.Auth.ExchangeInvite(Ip, bound, "New PC", "cid-new");
        Assert.Equal(200, res.Status);
        Assert.Equal("Elan", res.Who!.Name);
        Assert.Equal("owner", res.Who.Role);
        Assert.Equal(codesBefore, File.ReadAllText(r.Cfg.CodesFile));   // nobody new, nothing promoted
        Assert.Single(r.Auth.ListDevices("Elan"));

        // the unbound form: the typed name must be an owner
        var (_, any) = r.Auth.CreateOwnerClaim(null, recover: true);
        Assert.Equal(400, r.Auth.ExchangeInvite(Ip, any, "PC", "c2").Status);
        Assert.Equal(409, r.Auth.ExchangeInvite(Ip, any, "PC", "c2", "Mia").Status);       // a member can't take it
        Assert.Equal(409, r.Auth.ExchangeInvite(Ip, any, "PC", "c2", "Stranger").Status);
        Assert.Equal(200, r.Auth.ExchangeInvite(Ip, any, "PC", "c2", "ELAN").Status);
    }

    // ================================================================ the last-owner guard

    [Fact]
    public void Last_owner_guard_applies_to_role_change_remove_and_leave()
    {
        using var r = Rig.WithOwner();
        var e1 = Assert.Throws<InvalidOperationException>(() => r.Auth.SetRole("Elan", "officer"));
        Assert.Equal(Auth.LastOwnerMessage, e1.Message);
        var e2 = Assert.Throws<InvalidOperationException>(() => r.Auth.Remove("Elan"));
        Assert.Equal(Auth.LastOwnerMessage, e2.Message);
        var e3 = Assert.Throws<InvalidOperationException>(() => r.Auth.Leave("elan"));
        Assert.Equal(Auth.LastOwnerMessage, e3.Message);
        Assert.Contains(r.Auth.ListPersonal(), p => p.Name == "Elan" && p.Role == "owner");

        r.Auth.SetRole("Bob", "owner");                       // a second owner: the first may now step down, be removed or leave
        r.Auth.SetRole("Elan", "officer");
        var e4 = Assert.Throws<InvalidOperationException>(() => r.Auth.Leave("Bob"));   // Bob is the last one now
        Assert.Equal(Auth.LastOwnerMessage, e4.Message);
        r.Auth.SetRole("Elan", "owner");
        r.Auth.Remove("Bob");
        Assert.DoesNotContain(r.Auth.ListPersonal(), p => p.Name == "Bob");
    }

    [Fact]
    public async Task The_hub_refuses_the_last_owner_with_a_code_and_the_role_change_too()
    {
        using var r = Rig.WithOwner();
        var (tok, _) = r.Join("Nova");
        var (elan, s) = r.Connect(T.ElanCode, false);
        await r.Ask(elan, new { t = "member.leave" });
        var err = Assert.Single(s.Of("error"));
        Assert.Equal("last-owner", (string?)err["code"]);
        Assert.Equal("Transfer ownership first", (string?)err["text"]);
        Assert.False(s.Closed);
        Assert.Contains(r.Auth.ListPersonal(), p => p.Name == "Elan");

        await r.Ask(elan, new { t = "admin.role", name = "Elan", role = "member" });
        Assert.Equal(2, s.Of("error").Count);
        Assert.Contains("at least one Guild Master", (string?)s.Of("error")[1]["text"]);
        Assert.NotNull(tok);
    }

    // ================================================================ transfer and leave

    [Fact]
    public async Task Transfer_then_leave_removes_codes_devices_and_invites_and_closes_sockets()
    {
        using var r = Rig.WithOwner();
        var (token1, dev1) = r.Join("Nova", "PC one", "c1");
        var (token2, dev2) = r.Join("Nova", "Laptop", "c2");
        r.Auth.CreateInvite("person", "Nova", "member", "Elan");     // a pending device link for her
        var (_, otherSecret) = r.Auth.CreateInvite("person", "Mia", "member", "Elan");
        var (nova1, s1) = r.Connect(token1, true);
        var (elan, se) = r.Connect(T.ElanCode, false);
        Assert.Equal("Nova", nova1.Name);

        // make Nova the owner (transfer), then Elan (the old owner) leaves
        await r.Ask(elan, new { t = "admin.role", name = "Nova", role = "owner" });
        Assert.Contains(r.Auth.ListPersonal(), p => p.Name == "Nova" && p.Role == "owner");
        await r.Ask(elan, new { t = "member.leave" });
        Assert.DoesNotContain(r.Auth.ListPersonal(), p => p.Name == "Elan");
        Assert.Single(se.Of("member.left"));
        Assert.True(se.Closed, "the leaving person's session closes");
        Assert.Null(r.Auth.CheckCredential(T.ElanCode, false));

        // now Nova (sole owner) can't leave while Bob and Mia exist... she must transfer first
        await r.Ask(nova1, new { t = "member.leave" });
        Assert.True(s1.Of("error").Count == 1, string.Join(" | ", s1.Sent.Select(x => x.ToJsonString())));
        Assert.Equal("last-owner", (string?)s1.Of("error")[0]["code"]);
        await r.Ask(nova1, new { t = "admin.role", name = "Bob", role = "owner" });
        await r.Ask(nova1, new { t = "member.leave" });

        Assert.DoesNotContain(r.Auth.ListPersonal(), p => p.Name == "Nova");
        Assert.Empty(r.Auth.ListDevices("Nova"));
        Assert.Null(r.Auth.CheckToken(token1));
        Assert.Null(r.Auth.CheckToken(token2));
        Assert.True(s1.Closed, "her live session is disconnected");
        Assert.Contains("left", s1.CloseReason ?? "");
        Assert.DoesNotContain(r.Auth.ListInvites(), i => i.Pending(r.Clock.UtcNow) && i.Name == "Nova");
        Assert.Contains(r.Auth.ListInvites(), i => i.Pending(r.Clock.UtcNow) && i.Name == "Mia");   // other people's invites stay
        Assert.Equal(200, r.Auth.ExchangeInvite(Ip, otherSecret, "PC", "cm").Status);
        Assert.NotNull(dev1); Assert.NotNull(dev2);
    }

    [Fact]
    public async Task A_member_can_leave_and_the_name_is_free_again()
    {
        using var r = Rig.WithOwner();
        var (tok, _) = r.Join("Nova");
        var (nova, s) = r.Connect(tok, true);
        await r.Ask(nova, new { t = "member.leave" });
        Assert.Single(s.Of("member.left"));
        Assert.Null(r.Auth.CheckToken(tok));
        Assert.DoesNotContain(r.Auth.ListPersonal(), p => p.Name == "Nova");
        var (_, again) = r.Auth.CreateInvite("person", "Nova", "member", "Elan");
        Assert.Equal(200, r.Auth.ExchangeInvite(Ip, again, "PC", "c9").Status);
    }

    [Fact]
    public async Task A_guest_leaving_only_signs_the_device_out()
    {
        using var r = Rig.WithOwner();
        var (_, g) = r.Auth.CreateInvite("guest", "", "guest", "Elan");
        var ex = r.Auth.ExchangeInvite(Ip, g, "PC", "cg");
        var (guest, s) = r.Connect(ex.Token!, true);
        await r.Ask(guest, new { t = "member.leave" });
        Assert.True(s.Closed);
        Assert.Null(r.Auth.CheckToken(ex.Token!));
        Assert.Equal(3, r.Auth.ListPersonal().Count);
    }

    // ================================================================ lodge name and welcome text

    [Fact]
    public async Task Only_the_owner_sets_the_lodge_name_and_it_is_validated_and_capped()
    {
        using var r = Rig.WithOwner();
        var (elan, se) = r.Connect(T.ElanCode, false);
        var (bob, sb) = r.Connect(T.BobCode, false);

        await r.Ask(bob, new { t = "admin.lodge.set", name = "Mine", welcome = "x" });
        Assert.Single(sb.Of("error"));
        Assert.Equal("", r.Hub.Info.Name);

        await r.Ask(elan, new { t = "admin.lodge.set", name = "  The‮ Silver   Lodge ", welcome = "Welcome!\r\nBe nice.\u0007" });
        Assert.Equal("The Silver Lodge", r.Hub.Info.Name);                  // bidi override out, spaces folded
        Assert.Equal("Welcome!\nBe nice.", r.Hub.Info.Welcome);
        Assert.Single(sb.Of("lodge.info"));                                  // everyone is told
        Assert.Equal("The Silver Lodge", (string?)sb.Of("lodge.info")[0]["lodge"]!["name"]);

        await r.Ask(elan, new { t = "admin.lodge.set", name = new string('N', 41), welcome = "" });
        await r.Ask(elan, new { t = "admin.lodge.set", name = "Ok", welcome = new string('w', 281) });
        await r.Ask(elan, new { t = "admin.lodge.set", name = "Ok", welcome = "1\n2\n3\n4\n5" });
        Assert.Equal(3, se.Of("error").Count);
        Assert.Equal("The Silver Lodge", r.Hub.Info.Name);                  // refused changes leave it alone

        // it survives a restart and shows in the welcome frame
        var again = new LodgeInfoStore(r.Dir.Path);
        Assert.Equal("The Silver Lodge", again.Name);
        Assert.Equal("The Silver Lodge", (string?)r.Hub.Welcome(elan)["lodge"]!["name"]);

        await r.Ask(elan, new { t = "admin.lodge.set", name = "", welcome = "" });   // empty clears
        Assert.Equal("the lodge", r.Hub.Info.DisplayName);
    }

    // ================================================================ the public web invite page

    [Fact]
    public void The_web_page_has_a_strict_csp_and_the_other_security_headers()
    {
        var nonce = InvitePage.NewNonce();
        var h = InvitePage.Headers(nonce);
        var csp = h["Content-Security-Policy"];
        Assert.Contains("default-src 'none'", csp);
        Assert.Contains($"script-src 'nonce-{nonce}'", csp);
        Assert.DoesNotContain("unsafe-inline", csp);
        Assert.DoesNotContain("connect-src *", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.Contains("base-uri 'none'", csp);
        Assert.Equal("nosniff", h["X-Content-Type-Options"]);
        Assert.Equal("no-referrer", h["Referrer-Policy"]);
        Assert.NotEqual(nonce, InvitePage.NewNonce());
    }

    [Fact]
    public void The_web_page_never_touches_the_network_or_the_fragment_and_encodes_the_lodge_name()
    {
        var nonce = "NONCE123";
        var html = InvitePage.Render(nonce, "<img src=x onerror=alert(1)> & Co", "Hello <script>alert(1)</script>\nSecond line");
        Assert.DoesNotContain("<img", html);
        Assert.Contains("&lt;img src=x", html);
        Assert.DoesNotContain("<script>alert", html);
        Assert.Contains($"<script nonce=\"{nonce}\">", html);
        Assert.Contains($"<style nonce=\"{nonce}\">", html);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(html, "<script").Count);   // the one nonced script
        foreach (var banned in new[] { "fetch(", "XMLHttpRequest", "sendBeacon", "WebSocket", "analytics", "location.href", "location.replace", "location.assign", "window.open", "onclick=", "style=\"" })
            Assert.DoesNotContain(banned, html);
        Assert.Contains("location.hash", html);            // the script reads it in the browser...
        Assert.Contains("elanshub://lodge/join#", html);   // ...to build the Outpost link
        Assert.Contains("Open in the Outpost", html);
        Assert.Contains(InvitePage.DownloadUrl, html);
        // a render has nothing of any request in it: two renders for the same lodge differ only in their nonce
        Assert.Equal(InvitePage.Render("ZZZZ", "Lodge", "w").Replace("ZZZZ", "N"), InvitePage.Render("YYYY", "Lodge", "w").Replace("YYYY", "N"));
        Assert.Contains("You're invited to", InvitePage.Render(nonce, "", ""));
        Assert.Contains("the lodge", InvitePage.Render(nonce, "", ""));
    }
}
