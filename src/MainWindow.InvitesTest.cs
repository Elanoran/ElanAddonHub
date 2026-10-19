using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // --selftest "invites": one-time invite links and device tokens (Lodge server 2.10) - link parsing, the DPAPI token store, the "Upgrade this
    // device" rule, the error messages, the join flow against a canned server, and the Guild Master / Your devices panels from synthetic frames.
    // No server and no network: the exchange goes through InviteExchange.Send, which the test points at canned answers.
    public partial class MainWindow
    {
        delegate void InvCheck(string what, bool cond, string extra = null);

        // synthetic secrets only
        const string InvA = "i1.AbCdEfGhIjKlMnOpQrStUvWxYz012345";
        const string TokA = "t1.SyntheticTokenSyntheticTokenSyntheticToken1";
        const string TokB = "t1.SyntheticTokenSyntheticTokenSyntheticToken2";

        static string InvWelcome(string role, string server, bool tokens, bool canInvite, bool canRetire, string deviceName = null, bool guest = false) =>
            "{\"t\":\"welcome\",\"you\":1,\"name\":" + Q(guest ? "Wanderer (guest)" : "Elan") + ",\"role\":" + Q(role) + ",\"server\":" + Q(server) + ",\"maxFileMb\":25,\"canShareFiles\":true,"
            + "\"canModerate\":" + (role == "owner" || role == "officer" ? "true" : "false") + ",\"canManage\":" + (role == "owner" ? "true" : "false") + ",\"canPin\":false,"
            + "\"features\":[\"reply\",\"react\",\"react2\",\"pin\",\"profile\",\"displayname\",\"lfg\",\"history2\",\"search\"" + (tokens ? ",\"tokens\",\"invites\",\"devices\"" : "") + "],"
            + (tokens ? "\"canInvite\":" + (canInvite ? "true" : "false") + ",\"canRetire\":" + (canRetire ? "true" : "false") + "," : "")
            + (deviceName != null ? "\"device\":{\"id\":\"d1\",\"name\":" + Q(deviceName) + "}," : "")
            + "\"reactions\":[\"ready\",\"notready\",\"lol\",\"love\",\"fight\",\"loot\",\"wipe\",\"epic\"],"
            + "\"channels\":[{\"id\":\"general\",\"name\":\"General\",\"type\":\"text\",\"minRole\":\"guest\"}],"
            + "\"users\":[{\"id\":1,\"name\":" + Q(guest ? "Wanderer (guest)" : "Elan") + ",\"role\":" + Q(role) + (guest ? ",\"guest\":true" : "") + "}],\"history\":{\"general\":[]},\"pins\":{}}";

        static string InvInviteJson(string id, string kind, string name, string role, string by, long now, double createdAgoH, double expiresInH, int max, int uses, bool revoked = false)
        {
            bool expired = expiresInH <= 0, used = uses > 0, pending = !revoked && uses < max && !expired;
            return "{\"id\":" + Q(id) + ",\"kind\":" + Q(kind) + ",\"name\":" + Q(name) + ",\"role\":" + Q(role) + ",\"by\":" + Q(by)
                 + ",\"created\":" + (long)(now - createdAgoH * 3600000) + ",\"expires\":" + (long)(now + expiresInH * 3600000) + ",\"max\":" + max + ",\"uses\":" + uses
                 + ",\"used\":" + (used ? "true" : "false") + ",\"expired\":" + (expired ? "true" : "false") + ",\"revoked\":" + (revoked ? "true" : "false") + ",\"pending\":" + (pending ? "true" : "false") + "}";
        }

        static string InvInvitesFrame(long now) =>
            "{\"t\":\"admin.invites\",\"now\":" + now + ",\"invites\":["
            + InvInviteJson("a1", "person", "Nova", "member", "Elan", now, 1, 6.5 * 24, 1, 0) + ","
            + InvInviteJson("a2", "guest", "", "guest", "Bob", now, 3, 20, 3, 1) + ","
            + InvInviteJson("a3", "person", "Mira", "veteran", "Elan", now, 200, -1, 1, 0) + ","
            + InvInviteJson("a4", "person", "Dax", "member", "Elan", now, 30, 100, 1, 1) + ","
            + InvInviteJson("a5", "person", "Tess", "member", "Elan", now, 5, 40, 1, 0, revoked: true) + "]}";

        static string InvDevicesFrame(string type, long now, bool all) =>
            "{\"t\":" + Q(type) + ",\"name\":\"\",\"now\":" + now + ",\"devices\":["
            + "{\"id\":\"d1\",\"name\":\"Elan\",\"role\":\"owner\",\"device\":\"Elan-PC\",\"created\":" + (now - 86400000L * 20) + ",\"lastSeen\":" + (now - 30000) + ",\"ip\":\"203.0.113.x\",\"current\":true,\"online\":true},"
            + "{\"id\":\"d2\",\"name\":\"Elan\",\"role\":\"owner\",\"device\":\"Laptop\",\"created\":" + (now - 86400000L * 9) + ",\"lastSeen\":" + (now - 3 * 3600000L) + ",\"ip\":\"198.51.100.x\",\"current\":false,\"online\":false}"
            + (all ? ",{\"id\":\"d3\",\"name\":\"Nova\",\"role\":\"member\",\"device\":\"Nova-desktop\",\"created\":" + (now - 86400000L * 2) + ",\"lastSeen\":" + (now - 5 * 60000L) + ",\"ip\":\"192.0.2.x\",\"current\":false,\"online\":false}" : "")
            + "]}";

        async Task<string> InvitesTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            InvCheck check = (what, cond, extra) => { notes.Add((cond ? "ok   " : "FAIL ") + what + (cond || extra == null ? "" : "  -> " + extra)); ok &= cond; };
            var s = settings;
            var saved = new { s.LodgeUrl, s.LodgeCodeProtected, s.LodgeTokenProtected, s.LodgeDeviceName, s.LodgeDeviceId, s.LodgeUpgradeDeclined, s.LodgeName, s.LodgeManualConnect };
            var sendWas = InviteExchange.Send;
            var sentFrames = new List<Dictionary<string, object>>();
            try
            {
                Session.Disconnect();
                Session.NoNetworkForTest = true;
                Session.SentForTest = o => sentFrames.Add(o);
                TabLodge.IsChecked = true;
                LodgePage.ShowChatForTest();
                InvitesLinks(check);
                InvitesTokenStore(check);
                await InvitesMessages(check);
                await InvitesJoin(dir, check);
                await InvitesKeepCreds(check);
                await InvitesUpgrade(check);
                await InvitesPanels(sentFrames, check);
                await InvitesOldServer(sentFrames, check);
            }
            catch (Exception e) { ok = false; notes.Add("crashed: " + e); }
            finally
            {
                InviteExchange.Send = sendWas;
                Session.SentForTest = null;
                Session.Disconnect();
                Session.NoNetworkForTest = false;
                s.LodgeUrl = saved.LodgeUrl; s.LodgeCodeProtected = saved.LodgeCodeProtected; s.LodgeTokenProtected = saved.LodgeTokenProtected; s.LodgeDeviceName = saved.LodgeDeviceName;
                s.LodgeDeviceId = saved.LodgeDeviceId; s.LodgeUpgradeDeclined = saved.LodgeUpgradeDeclined; s.LodgeName = saved.LodgeName; s.LodgeManualConnect = saved.LodgeManualConnect;
                SettingsStore.Save(s);
            }
            return "invites selftest: " + (ok ? "PASSED" : "FAILED") + "\r\n  " + string.Join("\r\n  ", notes);
        }

        // ---------------------------------------------------------------- link parsing
        void InvitesLinks(InvCheck check)
        {
            var l = InviteLinks.Parse("elanshub://lodge/join#example.org/lodge|" + InvA);
            check("link: new format -> https address + invite", l.Url == "https://example.org/lodge" && l.Secret == InvA && l.IsInvite && !l.IsOldCode);
            l = InviteLinks.Parse("  ELANSHUB://lodge/join#http://192.168.1.5:5280/lodge|" + InvA + "  ");
            check("link: scheme is case-insensitive, an http address and surrounding spaces are kept right", l.Url == "http://192.168.1.5:5280/lodge" && l.Secret == InvA);
            // https only (2.54+ security): plain http:// to a host that is not this PC is flagged and never used; no scheme means https
            check("https-only: an http address to a LAN host is flagged Insecure (the address is kept so the message can say so)", l.Insecure && l.Url == "http://192.168.1.5:5280/lodge");
            check("https-only: https and a bare address are fine", !InviteLinks.Parse("elanshub://lodge/join#https://example.org/lodge|" + InvA).Insecure && !InviteLinks.Parse("elanshub://lodge/join#example.org/lodge|" + InvA).Insecure
                && !InviteLinks.Parse("elanshub://lodge/join#192.168.1.5:5280/lodge|" + InvA).Insecure);
            check("https-only: http to localhost / 127.0.0.1 / [::1] stays allowed for testing", !InviteLinks.Parse("elanshub://lodge/join#http://localhost:5280/lodge|" + InvA).Insecure
                && !InviteLinks.Parse("elanshub://lodge/join#http://127.0.0.1:5280/lodge|" + InvA).Insecure && InviteLinks.InsecureReason("http://[::1]:5280/lodge") == null && InviteLinks.InsecureReason("HTTP://LOCALHOST/lodge") == null);
            check("https-only: other http hosts are refused, however they are spelled", InviteLinks.InsecureReason("http://example.org/lodge") != null && InviteLinks.InsecureReason("HTTP://EXAMPLE.ORG") != null
                && InviteLinks.InsecureReason("http://127.0.0.1.example.org/lodge") != null && InviteLinks.InsecureReason("http://localhost.example.org/lodge") != null && InviteLinks.InsecureReason("http://user@example.org/lodge") != null
                && InviteLinks.InsecureReason("http://10.0.0.2:5280/lodge") != null);
            check("https-only: the web form and the older #invite= form are checked the same way", InviteLinks.Parse("https://site.example/lodge/#join=http://example.org/lodge|" + InvA).Insecure
                && InviteLinks.Parse("http://example.org/lodge#invite=" + InvA).Insecure && !InviteLinks.Parse("https://example.org/lodge#invite=" + InvA).Insecure);
            check("https-only: the refusal text explains what to do", InviteLinks.InsecureText.Contains("https://") && InviteLinks.InsecureText.Contains("Guild Master") && InviteLinks.InsecureText.Contains("localhost"));
            // 3.1.1 (L1): a user-info part shows a trusted name before the host the link really goes to - refused in every form
            check("user-info: refused in an elanshub:// link, a web link and the older form", InviteLinks.Parse("elanshub://lodge/join#nasferatu.dk@evil.example/lodge|" + InvA).MissingAddress
                && InviteLinks.Parse("elanshub://lodge/join#https://nasferatu.dk@evil.example/lodge|" + InvA).Url == null && InviteLinks.Parse("elanshub://lodge/join#a:b@evil.example/lodge|" + InvA).MissingAddress
                && InviteLinks.Parse("elanshub://lodge/join#nasferatu.dk%40evil.example/lodge|" + InvA).MissingAddress
                && InviteLinks.Parse("https://site.example/lodge/#join=nasferatu.dk@evil.example/lodge|" + InvA).MissingAddress
                && InviteLinks.Parse("https://nasferatu.dk@evil.example/lodge#invite=" + InvA).Url == null
                && LinkOpen.Validate("elanshub://lodge/join#nasferatu.dk@evil.example/lodge|" + InvA) == null);
            check("user-info: ValidAddress refuses it, honest addresses are still fine", !InviteLinks.ValidAddress("nasferatu.dk@evil.example/lodge") && !InviteLinks.ValidAddress("https://x@evil.example")
                && InviteLinks.ValidAddress("nasferatu.dk/lodge") && InviteLinks.ValidAddress("https://example.org:8443/lodge") && InviteLinks.ValidAddress("[::1]:5280/lodge"));
            check("shown address: the real host, punycode for an internationalised name, the port only when not the default ("
                + LodgeView.ShortAddress("https://nаsferatu.dk/lodge") + ")",
                LodgeView.ShortAddress("https://nasferatu.dk/lodge") == "nasferatu.dk/lodge" && LodgeView.ShortAddress("https://nasferatu.dk/lodge/") == "nasferatu.dk/lodge"
                && LodgeView.ShortAddress("http://127.0.0.1:5280/lodge") == "127.0.0.1:5280/lodge" && LodgeView.ShortAddress("https://example.org:443/lodge") == "example.org/lodge"
                && LodgeView.ShortAddress("https://nasferatu.dk@evil.example/lodge") == "evil.example/lodge"
                && LodgeView.ShortAddress("https://nаsferatu.dk/lodge").StartsWith("xn--") && LodgeView.ShortAddress("https://bücher.example/lodge") == "xn--bcher-kva.example/lodge");
            // 3.1.1 (L2): a saved sign-in is only for the same scheme + host + port
            check("same lodge: scheme, host and port must match; the path may differ", InviteLinks.SameOrigin("https://example.org/lodge", "https://EXAMPLE.org/lodge2")
                && InviteLinks.SameOrigin("example.org/lodge", "https://example.org:443/x") && !InviteLinks.SameOrigin("https://example.org/lodge", "https://evil.example/lodge")
                && !InviteLinks.SameOrigin("https://example.org/lodge", "https://example.org:8443/lodge") && !InviteLinks.SameOrigin("https://example.org/lodge", "http://example.org/lodge")
                && !InviteLinks.SameOrigin("https://example.org/lodge", "https://example.org.evil.example/lodge") && !InviteLinks.SameOrigin("https://example.org/lodge", null)
                && !InviteLinks.SameOrigin("https://example.org/lodge", "https://example.org@evil.example/lodge"));
            l = InviteLinks.Parse("elanshub://lodge/join#example.org/lodge%7C" + InvA);
            check("link: a pipe that a chat app turned into %7C still parses", l.Url == "https://example.org/lodge" && l.Secret == InvA);
            l = InviteLinks.Parse("Join us: <elanshub://lodge/join#example.org/lodge|" + InvA + ">");
            check("link: a closing bracket after the invite is not part of it", l.Secret == InvA);
            l = InviteLinks.Parse("elanshub://lodge/join#example.org/lodge|" + InvA + ")");
            check("link: trailing punctuation is cut off", l.Secret == InvA);
            l = InviteLinks.Parse("https://nasferatu.dk/lodge#invite=0123456789abcdef0123456789abcdef");
            check("link: the older https#invite= format still parses (permanent code, not an invite)", l.Url == "https://nasferatu.dk/lodge" && l.Secret == "0123456789abcdef0123456789abcdef" && !l.IsInvite && l.IsOldCode);
            l = InviteLinks.Parse("https://nasferatu.dk/lodge#invite=" + InvA);
            check("link: an invite carried in the older https format is recognised", l.IsInvite && l.Secret == InvA && l.Url == "https://nasferatu.dk/lodge");
            string code;
            var url = LodgeSession.ParseInvite("https://nasferatu.dk/lodge/#invite=ABCDEF123456", out code);
            check("link: LodgeSession.ParseInvite (used by the guide) is unchanged for old links", url == "https://nasferatu.dk/lodge" && code == "ABCDEF123456");
            url = LodgeSession.ParseInvite("elanshub://lodge/join#example.org/lodge|" + InvA, out code);
            check("link: ...and understands new links", url == "https://example.org/lodge" && code == InvA);
            l = InviteLinks.Parse("nasferatu.dk/lodge");
            check("link: a bare address has no secret", l.Url == "https://nasferatu.dk/lodge" && l.Secret == null && !l.IsInvite);

            // the secret is only ever read from the #fragment
            l = InviteLinks.Parse("https://site.dk/lodge?invite=" + InvA);
            check("link: a secret in the query string is ignored", l.Secret == null && !l.IsInvite && !(l.Url ?? "").Contains("?"));
            l = InviteLinks.Parse("https://site.dk/lodge?code=abc12345&invite=" + InvA + "#invite=REALCODE123");
            check("link: only the fragment counts, and the query is dropped from the address", l.Secret == "REALCODE123" && l.Url == "https://site.dk/lodge");
            l = InviteLinks.Parse("elanshub://lodge/join?invite=" + InvA + "#example.org/lodge");
            check("link: new scheme with the secret in a query -> no secret", l.Secret == null && l.Url == "https://example.org/lodge");
            l = InviteLinks.Parse("elanshub://lodge/join/" + InvA + "#example.org/lodge");
            check("link: a secret in the path is ignored too", l.Secret == null);
            var made = InviteLinks.Make("https://example.org/lodge/", InvA);
            check("link: Make puts the secret in the fragment and never in a query or path",
                made == "elanshub://lodge/join#example.org/lodge|" + InvA && !made.Contains("?") && made.IndexOf(InvA, StringComparison.Ordinal) > made.IndexOf('#'));
            check("link: Make then Parse round-trips", InviteLinks.Parse(InviteLinks.Make("http://10.0.0.2:5280/lodge", InvA)) is JoinLink r && r.Url == "http://10.0.0.2:5280/lodge" && r.Secret == InvA);

            // junk never throws and never invents a secret
            var junkOk = true; string junkBad = null;
            foreach (var junk in new[] { null, "", "   ", "hello", "#", "#|", "elanshub://lodge/join", "elanshub://lodge/join#", "elanshub://lodge/join#|", "elanshub:", "|||",
                "javascript:alert(1)#invite=x", "\u0000\u0001", new string('#', 500), "elanshub://lodge/join#" + new string('a', 5000) + "|" + InvA })
            {
                try
                {
                    var j = InviteLinks.Parse(junk);
                    bool carriesInvite = junk != null && junk.EndsWith("|" + InvA);   // the one case where an invite really is in there
                    if (j.IsInvite && !carriesInvite) { junkOk = false; junkBad = "invented an invite from " + junk; }
                }
                catch (Exception e) { junkOk = false; junkBad = e.Message; }
            }
            check("link: junk never throws and never invents an invite", junkOk, junkBad);
            var jsLink = InviteLinks.Parse("javascript:alert(1)#invite=abcdef123456");
            check("link: a javascript: pseudo-address does not become a usable http(s) address", !(Uri.TryCreate(jsLink.Url ?? "", UriKind.Absolute, out var ju) && ju.Host == "javascript"));
            check("link: a short or odd secret is not an invite", !InviteLinks.LooksLikeInvite("i1.short") && !InviteLinks.LooksLikeInvite("0123456789abcdef0123456789abcdef") && !InviteLinks.LooksLikeInvite(null)
                && !InviteLinks.LooksLikeInvite("i1.abc def ghi jkl mno pqr stu vwx") && InviteLinks.LooksLikeInvite(InvA));
        }

        // ---------------------------------------------------------------- the DPAPI token store
        void InvitesTokenStore(InvCheck check)
        {
            var st = new Settings();
            check("store: nothing saved -> no credential", !LodgeCredentials.Load(st, out _, out _));
            LodgeCredentials.StoreCode(st, "https://example.org/lodge", "synthetic-old-code-12345");
            check("store: an old code loads as a code", LodgeCredentials.Load(st, out var c1, out var t1) && c1 == "synthetic-old-code-12345" && !t1);
            LodgeCredentials.StoreToken(st, "https://example.org/lodge", TokA, "Elan-PC");
            check("store: a token replaces the code (the code is no longer kept)", st.LodgeCodeProtected == null && !string.IsNullOrEmpty(st.LodgeTokenProtected) && LodgeCredentials.HasToken(st) && !LodgeCredentials.HasCode(st));
            check("store: DPAPI round trip gives the token back", LodgeCredentials.Load(st, out var c2, out var t2) && c2 == TokA && t2);
            var json = Util.ToJson(st);
            check("store: the saved settings hold neither the token nor the code in clear", !json.Contains(TokA) && !json.Contains(TokA.Substring(3)) && !json.Contains("synthetic-old-code") && json.Contains("lodgeToken"));
            var back = Util.FromJson<Settings>(json);
            check("store: ...and load again after a save", LodgeCredentials.Load(back, out var c3, out var t3) && c3 == TokA && t3 && back.LodgeDeviceName == "Elan-PC");
            check("store: the protected value is not the token (DPAPI blob)", st.LodgeTokenProtected != TokA && SettingsStore.Unprotect(st.LodgeTokenProtected) == TokA);
            st.LodgeCodeProtected = SettingsStore.Protect("an-old-leftover-code-123");
            check("store: with both saved the token wins", LodgeCredentials.Load(st, out var c4, out var t4) && c4 == TokA && t4);
            check("store: a garbled blob is treated as nothing, not a crash", SettingsStore.Unprotect("not-base64!!") == null && !LodgeCredentials.Load(new Settings { LodgeTokenProtected = "AAAA" }, out _, out _));
            LodgeCredentials.Clear(st);
            check("store: Clear forgets both", !LodgeCredentials.Load(st, out _, out _));
            var id1 = LodgeCredentials.DeviceId(st); var id2 = LodgeCredentials.DeviceId(st);
            check("store: the device id is random, made once and stable", id1 == id2 && id1.Length == 32 && id1 != LodgeCredentials.DeviceId(new Settings()));
            check("store: the device name defaults to the PC name and can be changed", LodgeCredentials.DeviceName(new Settings()) == Environment.MachineName && LodgeCredentials.DeviceName(new Settings { LodgeDeviceName = "  Deck  " }) == "Deck");
        }

        // ---------------------------------------------------------------- the words
        async Task InvitesMessages(InvCheck check)
        {
            check("message: used or expired", InviteExchange.Friendly(410, "used-or-expired", "x") == "This invite has been used or expired - ask for a new one");
            check("message: unknown invite", InviteExchange.Friendly(401, "wrong", null).Contains("isn't valid") && InviteExchange.Friendly(401, "wrong", null, true).Contains("code"));
            check("message: locked out", InviteExchange.Friendly(429, "blocked", null).Contains("15 minutes"));
            check("message: an older server names the update command", InviteExchange.Friendly(404, null, null).Contains("older server") && InviteExchange.Friendly(404, null, null).Contains(Brand.ServerUpdateCommand));
            check("message: unreachable", InviteExchange.Friendly(0, null, null).StartsWith("Can't reach the lodge"));
            check("message: the server's own words for a refused device", InviteExchange.Friendly(409, "too-many-devices", "This account has too many devices") == "This account has too many devices");
            check("message: a revoked device", LodgeClient.SignedOutText.Contains("signed out") && LodgeClient.SignedOutText.Contains("new invite"));
            var now = 1_800_000_000_000L;
            check("time: 'in 6 days', 'in 20 hours', 'in 1 hour', 'in 5 min', 'expired'",
                AgoText.In(now + 6L * 86400000 + 3600000, now) == "in 6 days" && AgoText.In(now + 20L * 3600000, now) == "in 20 hours" && AgoText.In(now + 3600000, now) == "in 1 hour"
                && AgoText.In(now + 5 * 60000, now) == "in 5 min" && AgoText.In(now - 1, now) == "expired");
            check("time: 'just now', '40 min ago', '3 hours ago', '5 days ago'",
                AgoText.Ago(now - 30000, now) == "just now" && AgoText.Ago(now - 40 * 60000L, now) == "40 min ago" && AgoText.Ago(now - 3 * 3600000L, now) == "3 hours ago" && AgoText.Ago(now - 5L * 86400000, now) == "5 days ago");

            // the exchange request: invite in a header, device in the body, nothing in the URL
            HttpRequestMessage seen = null; string body = null;
            InviteExchange.Send = async req =>
            {
                seen = req; body = req.Content == null ? null : await req.Content.ReadAsStringAsync();
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"token\":" + Q(TokA) + ",\"deviceId\":\"d9\",\"device\":\"Elan-PC\",\"name\":\"Nova\",\"role\":\"member\",\"guest\":false}") };
            };
            var res = await InviteExchange.Exchange("https://example.org/lodge/", InvA, null, "Elan-PC", "device-id-1");
            check("exchange: POST /exchange with the invite in X-Lodge-Invite", seen != null && seen.Method == HttpMethod.Post && seen.RequestUri.ToString() == "https://example.org/lodge/exchange"
                && seen.Headers.GetValues("X-Lodge-Invite").Single() == InvA && !seen.Headers.Contains("X-Lodge-Code"));
            check("exchange: the secret is not in the URL (no query, no path)", seen != null && !seen.RequestUri.ToString().Contains(InvA) && seen.RequestUri.Query == "");
            check("exchange: the device name and id travel in the body", body != null && body.Contains("\"device\":\"Elan-PC\"") && body.Contains("\"deviceId\":\"device-id-1\"") && !body.Contains(InvA));
            check("exchange: a 200 gives the token, name and rank", res.Ok && res.Token == TokA && res.Name == "Nova" && res.Role == "member" && res.DeviceId == "d9" && !res.Guest && res.Message == null);
            await InviteExchange.Exchange("https://example.org/lodge", null, "synthetic-old-code-12345", "PC", "id");
            check("exchange: an upgrade sends the old code in X-Lodge-Code and no invite", seen.Headers.GetValues("X-Lodge-Code").Single() == "synthetic-old-code-12345" && !seen.Headers.Contains("X-Lodge-Invite"));
            InviteExchange.Send = req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Gone) { Content = new StringContent("{\"error\":\"This invite has been used or expired - ask for a new one\",\"reason\":\"used-or-expired\"}") });
            res = await InviteExchange.Exchange("https://example.org/lodge", InvA, null, "PC", "id");
            check("exchange: 410 -> the used-or-expired sentence", !res.Ok && res.Status == 410 && res.Message == "This invite has been used or expired - ask for a new one" && res.Token == null);
            InviteExchange.Send = req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("<html>404 Not Found</html>") });
            res = await InviteExchange.Exchange("https://example.org/lodge", InvA, null, "PC", "id");
            check("exchange: a 404 HTML page (an older server) -> the update hint, no crash", res.Status == 404 && res.Message.Contains("older server"));
            InviteExchange.Send = req => throw new HttpRequestException("connection refused");
            res = await InviteExchange.Exchange("https://example.org/lodge", InvA, null, "PC", "id");
            check("exchange: no connection -> status 0 and a calm sentence", res.Status == 0 && res.Message.StartsWith("Can't reach"));
            InviteExchange.Send = req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"name\":\"x\"}") });
            res = await InviteExchange.Exchange("https://example.org/lodge", InvA, null, "PC", "id");
            check("exchange: a 200 without a token is an error, not a sign-in", !res.Ok && res.Token == null);
            // https only: nothing is sent to an http:// lodge that is not this PC
            int sendCalls = 0;
            InviteExchange.Send = req => { sendCalls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"token\":" + Q(TokA) + "}") }); };
            res = await InviteExchange.Exchange("http://example.org/lodge", InvA, null, "PC", "id");
            check("https-only: an invite exchange with an http:// lodge sends nothing and says why", sendCalls == 0 && !res.Ok && res.Message == InviteLinks.InsecureText);
            res = await InviteExchange.Exchange("http://127.0.0.1:5280/lodge", InvA, null, "PC", "id");
            check("https-only: ...but a localhost lodge is still exchanged (testing)", sendCalls == 1 && res.Ok);
            var insecureClient = new LodgeClient("http://example.org/lodge", "synthetic-code-123456", "Nova");
            string insecureStatus = null; bool insecureOnline = true;
            insecureClient.Status += (text, online) => { insecureStatus = text; insecureOnline = online; };
            insecureClient.Start();
            await Task.Delay(150);
            check("https-only: the connection to an http:// lodge is never opened: stopped, offline, with the reason", !insecureClient.IsRunning && !insecureClient.Online && !insecureOnline && insecureStatus == InviteLinks.InsecureText);
            var localClient = new LodgeClient("http://127.0.0.1:9", "synthetic-code-123456", "Nova");
            localClient.Start(); await Task.Delay(50);
            check("https-only: a localhost client does start", localClient.IsRunning);
            localClient.Stop();
        }

        // ---------------------------------------------------------------- joining with a pasted link (the join box)
        async Task InvitesJoin(string dir, InvCheck check)
        {
            var s = settings;
            Session.Disconnect();
            LodgeCredentials.Clear(s);
            s.LodgeUrl = null; s.LodgeManualConnect = false;
            var asked = new List<HttpRequestMessage>();
            InviteExchange.Send = req => { asked.Add(req); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Gone) { Content = new StringContent("{\"error\":\"x\",\"reason\":\"used-or-expired\"}") }); };
            LodgePage.SignOut();                         // the join box
            LodgePage.JoinWithLink("elanshub://lodge/join#http://127.0.0.1:9/lodge|" + InvA);
            for (int i = 0; i < 20 && asked.Count == 0; i++) await Task.Delay(100);
            await Task.Delay(300);
            check("join: pasting a used link shows the plain-language message", LodgePage.JoinStatusForTest == "This invite has been used or expired - ask for a new one", LodgePage.JoinStatusForTest);
            check("join: ...nothing was saved and nobody is connected", !LodgeCredentials.HasToken(s) && !LodgeCredentials.HasCode(s) && !Session.Connected);
            check("join: ...and the exchange went to /exchange without the secret in the URL", asked.Count == 1 && asked[0].RequestUri.AbsolutePath == "/lodge/exchange" && !asked[0].RequestUri.ToString().Contains(InvA));

            InviteExchange.Send = req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("not found") });
            LodgePage.JoinWithLink("elanshub://lodge/join#http://127.0.0.1:9/lodge|" + InvA);
            await Task.Delay(500);
            check("join: a server without invite support says so", LodgePage.JoinStatusForTest.Contains("older server"), LodgePage.JoinStatusForTest);

            InviteExchange.Send = req => Task.FromResult(new HttpResponseMessage((HttpStatusCode)429) { Content = new StringContent("{\"error\":\"x\",\"reason\":\"blocked\"}") });
            LodgePage.JoinWithLink("elanshub://lodge/join#http://127.0.0.1:9/lodge|" + InvA);
            await Task.Delay(500);
            check("join: a lockout says to wait", LodgePage.JoinStatusForTest.Contains("15 minutes"), LodgePage.JoinStatusForTest);

            HttpRequestMessage ok = null; string okBody = null;
            InviteExchange.Send = async req => { ok = req; okBody = await req.Content.ReadAsStringAsync(); return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"token\":" + Q(TokA) + ",\"deviceId\":\"d9\",\"device\":\"Quest-PC\",\"name\":\"Nova\",\"role\":\"member\",\"guest\":false}") }; };
            s.LodgeDeviceName = "Quest-PC";
            LodgePage.SignOut();
            LodgePage.JoinWithLink("elanshub://lodge/join#http://127.0.0.1:9/lodge|" + InvA);
            for (int i = 0; i < 20 && ok == null; i++) await Task.Delay(100);
            await Task.Delay(500);
            check("join: a good invite is not stored before the lodge says welcome", !LodgeCredentials.HasToken(s) && Session.HasPendingCredential);
            Session.FeedForTest(InvWelcome("member", "2.10.0", true, false, false));
            await Task.Delay(100);
            check("join: a good invite stores the token (DPAPI) and no code", LodgeCredentials.Load(s, out var cred, out var isTok) && cred == TokA && isTok && !LodgeCredentials.HasCode(s));
            check("join: ...connects with the token", Session.Connected && Session.UsingToken && s.LodgeUrl == "http://127.0.0.1:9/lodge" && s.LodgeName == "Nova");
            check("join: ...with this PC's name and id sent", okBody != null && okBody.Contains("Quest-PC") && okBody.Contains(LodgeCredentials.DeviceId(s)));
            Session.Disconnect();

            // an old permanent code in an older link still joins the old way
            InviteExchange.Send = req => throw new Exception("an old code must not be exchanged at join");
            LodgePage.SignOut();
            LodgePage.JoinWithLink("http://127.0.0.1:9/lodge#invite=0123456789abcdef0123456789abcdef");
            await Task.Delay(300);
            Session.FeedForTest(InvWelcome("member", "2.9.0", false, false, false));
            await Task.Delay(100);
            check("join: an old link with a permanent code works as before (stored as a code, no exchange)", LodgeCredentials.Load(s, out var oc, out var ot) && oc == "0123456789abcdef0123456789abcdef" && !ot && Session.Connected && !Session.UsingToken);
            Session.Disconnect();
            LodgePage.SignOut();
            check("join: leaving the lodge forgets the token and the code", !LodgeCredentials.Load(s, out _, out _));
        }

        // ---------------------------------------------------------------- a failed attempt never costs the sign-in that worked
        async Task InvitesKeepCreds(InvCheck check)
        {
            var s = settings;
            const string url = "http://127.0.0.1:9/lodge";
            var asked = new List<HttpRequestMessage>();
            InviteExchange.Send = req => { asked.Add(req); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{\"error\":\"x\",\"reason\":\"wrong\"}") }); };
            Action saveToken = () => { Session.Disconnect(); LodgeCredentials.Clear(s); s.LodgeUrl = null; LodgeCredentials.StoreToken(s, url, TokA, "Elan-PC"); LodgePage.ShowChatForTest(); };
            Func<string, bool> stillToken = what => LodgeCredentials.Load(s, out var cr, out var isT) && cr == TokA && isT && s.LodgeUrl == url;

            // 1. a wrong code on top of a saved token
            saveToken();
            Session.Connect(url, TokA, true);
            LodgePage.EditConnection();
            LodgePage.UrlBoxForTest = url;
            LodgePage.CodeBoxForTest = "synthetic-wrong-code-123";
            LodgePage.JoinForTest();
            check("keep: a code being tried is held in memory, not saved", Session.HasPendingCredential && stillToken(null));
            Session.StopForTest("Wrong invite code");
            await Task.Delay(100);
            check("keep: a wrong code is rejected and the saved token is still there", stillToken(null) && !LodgeCredentials.HasCode(s));
            check("keep: ...the error stays on the join panel with a way back", LodgePage.JoinPanelVisibleForTest && LodgePage.JoinStatusForTest == "Wrong invite code" && LodgePage.RestoreVisibleForTest && !Session.Connected && !Session.StoppedSignedOut);
            LodgePage.RestoreSavedForTest();
            check("keep: 'Back to my saved sign-in' reconnects with the saved token", Session.Connected && Session.UsingToken && !LodgePage.JoinPanelVisibleForTest && !Session.HasPendingCredential && stillToken(null));

            // 3.1.1 (L2): another host, port or scheme with an empty code never gets the saved token - it needs its own invite
            foreach (var other in new[] { "http://127.0.0.1:8/lodge", "http://localhost:9/lodge", "https://127.0.0.1:9/lodge", "https://evil.example/lodge" })
            {
                Session.Disconnect();
                LodgePage.EditConnection();
                LodgePage.UrlBoxForTest = other;
                LodgePage.CodeBoxForTest = "";
                LodgePage.JoinForTest();
                check("edit connection: " + other + " with no code is refused, nothing is sent or pending (" + LodgePage.JoinStatusForTest + ")",
                    LodgePage.JoinStatusForTest == LodgeView.OtherLodgeNeedsInviteText && !Session.HasPendingCredential && !Session.Connected && stillToken(null));
                LodgePage.CancelEditForTest();
            }
            // an unreachable address / refused connect counts as a failed attempt too (the same lodge, another path: the saved token may go)
            LodgePage.EditConnection();
            LodgePage.UrlBoxForTest = "http://127.0.0.1:9/other";
            LodgePage.CodeBoxForTest = "";
            LodgePage.JoinForTest();
            check("keep: a new path on the same lodge alone is also only a candidate", Session.HasPendingCredential && stillToken(null));

            Session.StopForTest("Can't reach the lodge - check the address and your connection");
            await Task.Delay(100);
            check("keep: ...and a failed connect keeps the old address and token", stillToken(null) && LodgePage.RestoreVisibleForTest);

            // 2. a wrong invite
            saveToken();
            asked.Clear();
            LodgePage.JoinWithLink("elanshub://lodge/join#" + url.Substring(7) + "|" + InvA);
            for (int i = 0; i < 20 && asked.Count == 0; i++) await Task.Delay(100);
            await Task.Delay(300);
            check("keep: a wrong invite is rejected and the saved token is still there", asked.Count == 1 && stillToken(null) && LodgePage.JoinStatusForTest.Contains("isn't valid"), LodgePage.JoinStatusForTest);
            check("keep: ...with a way back to the saved sign-in", LodgePage.RestoreVisibleForTest);
            // an invite that exchanges fine but whose new token the lodge then refuses
            InviteExchange.Send = req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"token\":" + Q(TokB) + ",\"deviceId\":\"d8\",\"device\":\"Elan-PC\",\"name\":\"Nova\",\"role\":\"member\",\"guest\":false}") });
            saveToken();
            LodgePage.JoinWithLink("elanshub://lodge/join#" + url.Substring(7) + "|" + InvA);
            for (int i = 0; i < 20 && !Session.HasPendingCredential; i++) await Task.Delay(100);
            check("keep: an exchanged token is only a candidate until the welcome", Session.HasPendingCredential && stillToken(null));
            Session.StopForTest(LodgeClient.SignedOutText, true);
            await Task.Delay(100);
            check("keep: ...if the lodge refuses it the old token survives (not treated as a revoked device)", stillToken(null) && !Session.StoppedSignedOut && LodgePage.RestoreVisibleForTest);

            // 3. success replaces, but only after the welcome
            saveToken();
            LodgePage.EditConnection();
            LodgePage.UrlBoxForTest = url;
            LodgePage.CodeBoxForTest = "synthetic-new-code-12345";
            LodgePage.JoinForTest();
            check("keep: before the welcome the old token is still the saved one", stillToken(null));
            Session.FeedForTest(InvWelcome("member", "2.9.0", false, false, false));
            check("keep: after the welcome the new code replaces it", LodgeCredentials.Load(s, out var nc, out var nt) && nc == "synthetic-new-code-12345" && !nt && !LodgeCredentials.HasToken(s) && !Session.HasPendingCredential);
            saveToken();
            LodgePage.JoinWithLink("elanshub://lodge/join#" + url.Substring(7) + "|" + InvA);
            for (int i = 0; i < 20 && !Session.HasPendingCredential; i++) await Task.Delay(100);
            check("keep: a good invite waits for the welcome too", stillToken(null));
            Session.FeedForTest(InvWelcome("member", "2.10.0", true, false, false));
            check("keep: ...then its token replaces the old one", LodgeCredentials.Load(s, out var nb, out var nbt) && nb == TokB && nbt && !LodgeCredentials.HasCode(s));

            // 4. a placeholder link says so, stores nothing, and is never read as a code
            saveToken();
            asked.Clear();
            LodgePage.JoinWithLink("elanshub://lodge/join#<your-lodge-address>|" + InvA);
            await Task.Delay(200);
            check("placeholder: the clear message is shown", LodgePage.JoinStatusForTest == "This link is missing the lodge address - ask for a new link", LodgePage.JoinStatusForTest);
            check("placeholder: nothing was stored, tried or connected", stillToken(null) && asked.Count == 0 && !Session.HasPendingCredential && !Session.Connected);
            LodgePage.JoinWithLink("elanshub://lodge/join#your-lodge-address|" + InvA);
            check("placeholder: a bare word with no dot is no address either", LodgePage.JoinStatusForTest.Contains("missing the lodge address") && asked.Count == 0);
            LodgePage.JoinWithLink("elanshub://lodge/join#|" + InvA);
            check("placeholder: an empty address too", LodgePage.JoinStatusForTest.Contains("missing the lodge address") && stillToken(null));
            var pl = InviteLinks.Parse("elanshub://lodge/join#<your-lodge-address>|" + InvA);
            check("placeholder: the parser gives no address", pl.MissingAddress && pl.Url == null);
            check("placeholder: real addresses (host, ip, lan name with a port) are still fine", !InviteLinks.Parse("elanshub://lodge/join#example.org/lodge|" + InvA).MissingAddress
                && !InviteLinks.Parse("elanshub://lodge/join#192.168.1.5:5280/lodge|" + InvA).MissingAddress && !InviteLinks.Parse("elanshub://lodge/join#nas:5280/lodge|" + InvA).MissingAddress);
            // junk that is not a link at all never touches the saved sign-in
            LodgePage.JoinWithLink("hello there");
            await Task.Delay(100);
            check("keep: text that is not a link changes nothing", stillToken(null) && !Session.HasPendingCredential && LodgePage.JoinStatusForTest.StartsWith("Paste the invite link"));

            // 5. the whole link pasted into the CODE box is an invite
            saveToken();
            InviteExchange.Send = req => { asked.Add(req); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{\"error\":\"x\",\"reason\":\"wrong\"}") }); };
            asked.Clear();
            LodgePage.EditConnection();
            LodgePage.UrlBoxForTest = "";
            LodgePage.CodeBoxForTest = "elanshub://lodge/join#" + url.Substring(7) + "|" + InvA;
            LodgePage.JoinForTest();
            for (int i = 0; i < 20 && asked.Count == 0; i++) await Task.Delay(100);
            await Task.Delay(200);
            check("codebox: a link pasted as the code is exchanged as an invite", asked.Count == 1 && asked[0].Headers.Contains("X-Lodge-Invite") && asked[0].Headers.GetValues("X-Lodge-Invite").Single() == InvA
                && asked[0].RequestUri.AbsolutePath == "/lodge/exchange", asked.Count + " requests");
            check("codebox: ...the link moved to the link box, the code box is empty, and nothing was saved as a code", LodgePage.CodeBoxForTest == "" && LodgePage.UrlBoxForTest.StartsWith("elanshub:") && stillToken(null) && !LodgeCredentials.HasCode(s));
            LodgePage.CodeBoxForTest = "elanshub://lodge/join#<your-lodge-address>|" + InvA;
            LodgePage.UrlBoxForTest = "";
            LodgePage.JoinForTest();
            check("codebox: a placeholder link there gets the same clear message", LodgePage.JoinStatusForTest.Contains("missing the lodge address") && stillToken(null) && !LodgeCredentials.HasCode(s));

            // 6. Cancel in "Edit connection" keeps everything
            saveToken();
            Session.Connect(url, TokA, true);
            asked.Clear();
            LodgePage.EditConnection();
            LodgePage.UrlBoxForTest = "http://127.0.0.1:7/elsewhere";
            LodgePage.CodeBoxForTest = "synthetic-typed-code-123";
            LodgePage.CancelEditForTest();
            check("cancel: nothing changes, nothing is pending, the chat is back", stillToken(null) && !LodgeCredentials.HasCode(s) && !Session.HasPendingCredential && Session.Connected && !LodgePage.JoinPanelVisibleForTest && asked.Count == 0);

            Session.Disconnect();
            LodgeCredentials.Clear(s);
        }

        // ---------------------------------------------------------------- "Upgrade this device"
        async Task InvitesUpgrade(InvCheck check)
        {
            var s = settings;
            check("upgrade rule: old code + 2.10 server -> offered", UpgradePolicy.ShouldOffer(false, true, true, false, null, "https://a/lodge"));
            check("upgrade rule: not for a token, an older server, a guest or without a saved code",
                !UpgradePolicy.ShouldOffer(true, true, true, false, null, "u") && !UpgradePolicy.ShouldOffer(false, true, false, false, null, "u")
                && !UpgradePolicy.ShouldOffer(false, true, true, true, null, "u") && !UpgradePolicy.ShouldOffer(false, false, true, false, null, "u"));
            check("upgrade rule: once declined for this lodge it stays quiet; another lodge is asked", !UpgradePolicy.ShouldOffer(false, true, true, false, "https://a/lodge", "https://a/lodge")
                && UpgradePolicy.ShouldOffer(false, true, true, false, "https://a/lodge", "https://b/lodge"));

            Session.Disconnect();
            s.LodgeUrl = "http://127.0.0.1:9/lodge";
            LodgeCredentials.StoreCode(s, s.LodgeUrl, "synthetic-old-code-12345");
            s.LodgeUpgradeDeclined = null;
            LodgePage.ShowChatForTest();
            Session.FeedForTest(InvWelcome("member", "2.10.0", true, false, false));
            await Task.Delay(300);
            check("upgrade: a 2.10 server + a saved code -> the quiet notice shows", Session.UpgradeOffered && LodgePage.UpgradeNoticeVisibleForTest);
            LodgePage.DeclineUpgradeForTest();
            await Task.Delay(200);
            check("upgrade: 'Not now' hides it and remembers it for this lodge", !LodgePage.UpgradeNoticeVisibleForTest && s.LodgeUpgradeDeclined == "http://127.0.0.1:9/lodge" && !Session.UpgradeOffered);
            Session.FeedForTest(InvWelcome("member", "2.10.0", true, false, false));
            await Task.Delay(200);
            check("upgrade: ...and a reconnect does not bring it back", !LodgePage.UpgradeNoticeVisibleForTest);
            s.LodgeUpgradeDeclined = null;
            Session.FeedForTest(InvWelcome("member", "2.9.0", false, false, false));
            await Task.Delay(200);
            check("upgrade: a 2.9 server -> nothing offered", !Session.UpgradeOffered && !LodgePage.UpgradeNoticeVisibleForTest && !Session.SupportsTokens);
            Session.FeedForTest(InvWelcome("member", "2.9.0", true, false, false));
            check("upgrade: a server that says 'tokens' but reports 2.9 is still treated as old (the version rule wins)", !Session.SupportsTokens);
            Session.FeedForTest(InvWelcome("member", "2.10.0", true, false, false, guest: true));
            check("upgrade: a guest is never offered it", !Session.UpgradeOffered);
            Session.FeedForTest(InvWelcome("member", "2.10.0", true, false, false));
            await Task.Delay(200);
            check("upgrade: offered again once the state fits", LodgePage.UpgradeNoticeVisibleForTest);

            // doing it: the stored code goes in X-Lodge-Code, the token replaces it, the session reconnects with it
            HttpRequestMessage req2 = null;
            InviteExchange.Send = req => { req2 = req; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"token\":" + Q(TokA) + ",\"deviceId\":\"d7\",\"device\":\"PC\",\"name\":\"Elan\",\"role\":\"member\",\"guest\":false}") }); };
            var problem = await Session.UpgradeThisDevice();
            check("upgrade: the exchange sends the saved code and succeeds", problem == null && req2 != null && req2.Headers.GetValues("X-Lodge-Code").Single() == "synthetic-old-code-12345" && req2.RequestUri.AbsolutePath.EndsWith("/exchange"));
            check("upgrade: the saved code stays until the lodge welcomes the new token", LodgeCredentials.HasCode(s) && !LodgeCredentials.HasToken(s));
            Session.FeedForTest(InvWelcome("member", "2.10.0", true, false, false));
            check("upgrade: the token replaces the code and the session uses it", LodgeCredentials.Load(s, out var c, out var t) && c == TokA && t && !LodgeCredentials.HasCode(s) && Session.UsingToken);
            Session.Disconnect();
            LodgeCredentials.StoreCode(s, s.LodgeUrl, "synthetic-old-code-12345");
            InviteExchange.Send = req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{\"error\":\"Wrong invite code\",\"reason\":\"wrong\"}") });
            Session.FeedForTest(InvWelcome("member", "2.10.0", true, false, false));
            problem = await Session.UpgradeThisDevice();
            check("upgrade: a failure leaves the saved code alone and says why", problem != null && problem.Contains("isn't valid") && LodgeCredentials.HasCode(s) && !LodgeCredentials.HasToken(s));
            LodgeCredentials.Clear(s);
        }

        // ---------------------------------------------------------------- Guild Master panel + Your devices, from synthetic frames
        async Task InvitesPanels(List<Dictionary<string, object>> sent, InvCheck check)
        {
            var s = settings;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            s.LodgeUrl = "https://example.org/lodge";
            Session.Disconnect();
            LodgePage.ShowChatForTest();
            sent.Clear();
            Session.FeedForTest(InvWelcome("owner", "2.10.0", true, true, true, "Elan-PC"));
            Session.FeedForTest("{\"t\":\"admin.members\",\"members\":[{\"name\":\"Elan\",\"role\":\"owner\",\"online\":true,\"hasCode\":false,\"devices\":2},{\"name\":\"Bob\",\"role\":\"officer\",\"online\":true,\"hasCode\":true,\"devices\":1},"
                + "{\"name\":\"Dax\",\"role\":\"member\",\"online\":false,\"hasCode\":true,\"devices\":0}],\"channels\":[{\"id\":\"general\",\"name\":\"General\",\"type\":\"text\",\"minRole\":\"guest\",\"max\":0}]}");
            ShowTab("settings");
            SettingsPage.Show("gm");
            await Task.Delay(300);
            check("gm: the owner's panel asks for members, invites and devices", sent.Any(f => f.Str("t") == "admin.members") && sent.Any(f => f.Str("t") == "admin.invites") && sent.Any(f => f.Str("t") == "admin.devices"));
            Session.FeedForTest(InvInvitesFrame(now));
            await Task.Delay(200);
            var rows = SettingsPage.InviteRowsForTest;
            check("gm: the invite list shows all five, pending ones first", rows.Count == 5 && rows[0].State == "Pending" && rows[1].State == "Pending" && rows.Skip(2).All(r => r.State != "Pending"), string.Join(",", rows.Select(r => r.State)));
            check("gm: a person invite reads 'Nova · Member', made by, expiry, still pending", rows[0].Title == "Nova · Member" && rows[0].Detail == "Made by Elan · expires in 6 days");
            check("gm: a guest link shows its uses", rows[1].Title == "Guest link (3 uses) · Guest" && rows[1].Detail == "Made by Bob · expires in 20 hours · used 1 of 3");
            var byId = rows.ToDictionary(r => r.Id);
            check("gm: used, expired and revoked invites are dimmed and have no Revoke button", byId["a4"].Detail == "Used · made by Elan" && byId["a3"].Detail == "Expired · made by Elan" && byId["a5"].State == "Revoked"
                && new[] { "a3", "a4", "a5" }.All(id => byId[id].RevokeVisibility == Visibility.Collapsed && byId[id].RowOpacity < 1) && byId["a1"].RevokeVisibility == Visibility.Visible && byId["a1"].RowOpacity == 1);
            check("gm: the list never shows a secret or a hash", rows.All(r => !(r.Title + r.Detail).Contains("i1.")));
            sent.Clear();
            SettingsPage.RevokeInviteForTest("a1");
            check("gm: Revoke sends admin.invite.revoke with the id", sent.Count == 1 && sent[0].Str("t") == "admin.invite.revoke" && sent[0].Str("id") == "a1");

            // creating
            check("gm: a 2.10 server shows the person/guest choice, the expiry and the Advanced button", SettingsPage.InviteKindVisibleForTest && SettingsPage.InviteButtonTextForTest == "Create link" && SettingsPage.AdvancedVisibleForTest);
            sent.Clear();
            SettingsPage.CreateInviteClickForTest("Cleo");
            var create = sent.FirstOrDefault(f => f.Str("t") == "admin.invite.create");
            check("gm: Create sends a one-time invite request (never a permanent code), 7 days by default", create != null && create.Str("kind") == "person" && create.Str("name") == "Cleo" && create.Str("role") == "member" && Convert.ToInt32(create["hours"]) == 168 && !sent.Any(f => f.Str("t") == "admin.invite"));
            SettingsPage.SetGuestKindForTest(true);
            sent.Clear();
            SettingsPage.CreateInviteClickForTest("");
            create = sent.FirstOrDefault(f => f.Str("t") == "admin.invite.create");
            check("gm: a guest link asks for a guest invite with its number of uses", create != null && create.Str("kind") == "guest" && Convert.ToInt32(create["uses"]) == 1);
            SettingsPage.SetGuestKindForTest(false);
            Session.FeedForTest("{\"t\":\"admin.invite.created\",\"id\":\"zz\",\"kind\":\"person\",\"name\":\"Cleo\",\"role\":\"member\",\"by\":\"Elan\",\"created\":" + now + ",\"expires\":" + (now + 7L * 86400000)
                + ",\"max\":1,\"uses\":0,\"used\":false,\"expired\":false,\"revoked\":false,\"pending\":true,\"secret\":" + Q(InvA) + "}");
            await Task.Delay(200);
            var link = SettingsPage.InviteLinkForTest;
            check("gm: the new link is elanshub://lodge/join#server|invite, secret in the fragment only", link == "elanshub://lodge/join#example.org/lodge|" + InvA && !link.Contains("?") && SettingsPage.InviteResultVisibleForTest);
            check("gm: ...with a title that says it was copied and a note that it works once", SettingsPage.InviteTitleForTest.Contains("Cleo") && SettingsPage.InviteTitleForTest.Contains("copied") && SettingsPage.InviteNoteForTest.Contains("once"));

            // members: devices and retire
            var mem = SettingsPage.MemberRowsForTest;
            var bob = mem.FirstOrDefault(m => m.Name == "Bob"); var dax = mem.FirstOrDefault(m => m.Name == "Dax"); var elan = mem.FirstOrDefault(m => m.Name == "Elan");
            check("gm: members show how many devices they have", bob?.DevicesText == "1 device" && dax?.DevicesText == "0 devices" && elan?.DevicesText == "2 devices");
            check("gm: Retire code only appears for a member with a code AND a device (never locks anyone out), for the owner", bob?.RetireVisibility == Visibility.Visible && dax?.RetireVisibility == Visibility.Collapsed && elan?.RetireVisibility == Visibility.Collapsed);
            sent.Clear();
            Session.RetireCode("Bob");
            check("gm: Retire sends admin.retire", sent.Count == 1 && sent[0].Str("t") == "admin.retire" && sent[0].Str("name") == "Bob");

            // devices
            Session.FeedForTest(InvDevicesFrame("admin.devices", now, true));
            Session.FeedForTest(InvDevicesFrame("devices", now, false));
            await Task.Delay(200);
            var all = SettingsPage.AllDeviceRowsForTest;
            check("gm: the devices list shows everyone's devices with the owner's name", all.Count == 3 && all.Any(d => d.GmTitle == "Nova · Nova-desktop" && d.Detail == "Last seen 5 min ago · 192.0.2.x"));
            ShowTab("settings");
            SettingsPage.Show("lodge");
            await Task.Delay(300);
            var mine = SettingsPage.MyDeviceRowsForTest;
            check("devices: 'Your devices' lists this PC (marked) and the laptop", mine.Count == 2 && mine[0].Title == "Elan-PC" && mine[0].CurrentVisibility == Visibility.Visible && mine[1].CurrentVisibility == Visibility.Collapsed);
            check("devices: online now / last seen / coarse address", mine[0].Detail == "Online now · 203.0.113.x" && mine[1].Detail == "Last seen 3 hours ago · 198.51.100.x");
            check("devices: Settings > Lodge asked the server for the list", sent.Any(f => f.Str("t") == "device.list"));
            sent.Clear();
            Session.RevokeDevice("d2");
            check("devices: Sign out sends device.revoke with the id", sent.Count == 1 && sent[0].Str("t") == "device.revoke" && sent[0].Str("id") == "d2");
            check("devices: the session knows which device it is", Session.DeviceId == "d1" && Session.DeviceName == "Elan-PC" && Session.SupportsTokens);

            // owner on a 2.22 server: Revert Atlas per member (undo for bad data); an owner on an older server and an officer do not get it
            Session.Disconnect();
            LodgePage.ShowChatForTest();
            sent.Clear();
            Session.FeedForTest(InvWelcome("owner", "2.21.0", true, true, true, "Elan-PC"));
            ShowTab("settings");
            SettingsPage.Show("gm");
            await Task.Delay(300);
            Session.FeedForTest("{\"t\":\"admin.members\",\"members\":[{\"name\":\"Bob\",\"role\":\"officer\",\"online\":true,\"hasCode\":true,\"devices\":1}],\"channels\":[]}");
            check("gm: an owner on an older server has no Revert Atlas button", !Session.CanPurge && SettingsPage.MemberRowsForTest.All(m => m.RevertVisibility == Visibility.Collapsed));
            Session.Disconnect();
            LodgePage.ShowChatForTest();
            Session.FeedForTest(InvWelcome("owner", "2.22.0", true, true, true, "Elan-PC").Replace("\"canRetire\":true,", "\"canRetire\":true,\"canPurge\":true,"));
            await Task.Delay(200);
            Session.FeedForTest("{\"t\":\"admin.members\",\"members\":[{\"name\":\"Bob\",\"role\":\"officer\",\"online\":true,\"hasCode\":true,\"devices\":1}],\"channels\":[]}");
            check("gm: the owner of a 2.22 server gets Revert Atlas on every member", Session.CanPurge && SettingsPage.MemberRowsForTest.Count == 1 && SettingsPage.MemberRowsForTest.All(m => m.RevertVisibility == Visibility.Visible));
            sent.Clear();
            Session.RevertAtlas("Bob");
            check("gm: Revert Atlas sends admin.purge for that member, Atlas only (the member stays)", sent.Count == 1 && sent[0].Str("t") == "admin.purge" && sent[0].Str("name") == "Bob" && sent[0].Bool("atlasOnly"));
            string purgedFor = null; Session.AdminPurged += pm => purgedFor = pm.Name + ":" + pm.Atlas;
            Session.FeedForTest("{\"t\":\"admin.purged\",\"name\":\"Bob\",\"atlasOnly\":true,\"atlas\":12,\"total\":12}");
            check("gm: the answer to a revert reaches the page and says how many items went", purgedFor == "Bob:12" && SettingsPage.GmStatusForTest.Contains("12") && SettingsPage.GmStatusForTest.Contains("Bob"));

            // an officer: invites and devices, but not ranks and channels
            Session.Disconnect();
            LodgePage.ShowChatForTest();
            sent.Clear();
            Session.FeedForTest(InvWelcome("officer", "2.10.0", true, true, false, "Bob-PC"));
            ShowTab("settings");
            SettingsPage.Show("gm");
            await Task.Delay(300);
            check("officer: the Guild Master page is there, without the owner-only members and channels", SettingsPage.NavGmVisibleForTest && !SettingsPage.GmOwnerOnlyVisibleForTest);
            check("officer: asks for invites and devices, not for the member list", sent.Any(f => f.Str("t") == "admin.invites") && sent.Any(f => f.Str("t") == "admin.devices") && !sent.Any(f => f.Str("t") == "admin.members"));
            check("officer: no Advanced (permanent code) button", !SettingsPage.AdvancedVisibleForTest);
            Session.FeedForTest("{\"t\":\"admin.members\",\"members\":[{\"name\":\"Bob\",\"role\":\"officer\",\"online\":true,\"hasCode\":true,\"devices\":1}],\"channels\":[]}");
            check("officer: no Retire button (owner only)", SettingsPage.MemberRowsForTest.All(m => m.RetireVisibility == Visibility.Collapsed));
            // a plain member
            Session.Disconnect();
            LodgePage.ShowChatForTest();
            Session.FeedForTest(InvWelcome("member", "2.10.0", true, false, false, "Mia-PC"));
            await Task.Delay(200);
            check("member: no Guild Master page", !SettingsPage.NavGmVisibleForTest);
        }

        // ---------------------------------------------------------------- an older server (< 2.10): everything stays as it was
        async Task InvitesOldServer(List<Dictionary<string, object>> sent, InvCheck check)
        {
            var s = settings;
            Session.Disconnect();
            LodgePage.ShowChatForTest();
            sent.Clear();
            Session.FeedForTest(InvWelcome("owner", "2.9.0", false, false, false));
            ShowTab("settings");
            SettingsPage.Show("gm");
            await Task.Delay(300);
            check("old server: no invite, device or token features", !Session.SupportsTokens && !Session.CanInvite && Session.DeviceId == null);
            check("old server: the Guild Master panel makes permanent codes (button says so) and warns that the server is old", !SettingsPage.InviteKindVisibleForTest && SettingsPage.InviteButtonTextForTest == "Create invite" && !SettingsPage.AdvancedVisibleForTest);
            check("old server: no invite or device requests are sent", !sent.Any(f => (f.Str("t") ?? "").StartsWith("admin.invite") || f.Str("t") == "admin.devices" || f.Str("t") == "device.list"));
            sent.Clear();
            SettingsPage.CreateInviteClickForTest("Cleo");
            check("old server: Create sends the old admin.invite", sent.Count == 1 && sent[0].Str("t") == "admin.invite" && sent[0].Str("name") == "Cleo");
            Session.FeedForTest("{\"t\":\"admin.invited\",\"name\":\"Cleo\",\"role\":\"member\",\"code\":\"0123456789abcdef0123456789abcdef\"}");
            check("old server: the answer still becomes a link the old way", SettingsPage.InviteLinkForTest == "https://example.org/lodge#invite=0123456789abcdef0123456789abcdef");
            ShowTab("settings");
            SettingsPage.Show("lodge");
            await Task.Delay(200);
            check("old server: Your devices explains why there are none", SettingsPage.DevicesNoteForTest.Contains("older than 2.10") && !SettingsPage.UpgradeDeviceVisibleForTest);
            check("old server: a welcome without the frames' new fields doesn't break the member list", true);
            Session.Disconnect();
        }
    }
}
