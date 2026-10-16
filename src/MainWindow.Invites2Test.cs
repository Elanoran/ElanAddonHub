using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // --selftest "invites2": inviting from inside the app (Lodge server 2.12) - the Invite sheet, web links, the profile card's "Send a new device link",
    // the "Create a lodge" guide, the Settings > Lodge Danger zone, the Guild Master's "Your lodge" card and the join banner.
    // Synthetic frames only: no network, no real secrets, no real dialog (Dialog.TestHook answers every question) and no clipboard
    // (InviteForm.ClipboardSeam catches what Copy would put there).
    public partial class MainWindow
    {
        const string Inv2Url = "https://example.org/lodge";

        static string Inv2Welcome(string role, bool canInvite, string lodgeName = "The Silver Lodge", string welcome = "Welcome in. Be kind.", bool v212 = true, bool tokens = true)
        {
            var w = InvWelcome(role, v212 ? "2.12.0" : "2.11.0", tokens, canInvite, role == "owner", "Elan-PC");
            if (!v212) return w;
            w = w.Replace("\"features\":[", "\"features\":[\"leave\",\"lodgeinfo\",");
            return w.Replace("{\"t\":\"welcome\",", "{\"t\":\"welcome\",\"lodge\":{\"name\":" + Q(lodgeName) + ",\"welcome\":" + Q(welcome) + "},");
        }

        static string Inv2Members(params string[] nameRole) =>
            "{\"t\":\"admin.members\",\"members\":[" + string.Join(",", nameRole.Select(p =>
            {
                var a = p.Split(':');
                return "{\"name\":" + Q(a[0]) + ",\"role\":" + Q(a[1]) + ",\"online\":true,\"hasCode\":false,\"devices\":1}";
            })) + "],\"channels\":[]}";

        static string Inv2Devices(long now, int n) =>
            "{\"t\":\"devices\",\"name\":\"\",\"now\":" + now + ",\"devices\":[" + string.Join(",", Enumerable.Range(1, n).Select(i =>
                "{\"id\":\"d" + i + "\",\"name\":\"Elan\",\"role\":\"owner\",\"device\":\"PC-" + i + "\",\"created\":" + (now - 86400000L * i) + ",\"lastSeen\":" + (now - 30000) + ",\"ip\":\"203.0.113.x\",\"current\":" + (i == 1 ? "true" : "false") + ",\"online\":" + (i == 1 ? "true" : "false") + "}")) + "]}";

        static string Inv2Created(long now, string kind, string name, string role, int max, string secret) =>
            "{\"t\":\"admin.invite.created\",\"id\":\"zz\",\"kind\":" + Q(kind) + ",\"name\":" + Q(name) + ",\"role\":" + Q(role) + ",\"by\":\"Elan\",\"created\":" + now + ",\"expires\":" + (now + 7L * 86400000)
            + ",\"max\":" + max + ",\"uses\":0,\"used\":false,\"expired\":false,\"revoked\":false,\"pending\":true,\"secret\":" + Q(secret) + "}";

        async Task<string> Invites2Test(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            InvCheck check = (what, cond, extra) => { notes.Add((cond ? "ok   " : "FAIL ") + what + (cond || extra == null ? "" : "  -> " + extra)); ok &= cond; };
            var s = settings;
            var saved = new { s.LodgeUrl, s.LodgeCodeProtected, s.LodgeTokenProtected, s.LodgeDeviceName, s.LodgeDeviceId, s.LodgeUpgradeDeclined, s.LodgeName, s.LodgeManualConnect };
            var sendWas = InviteExchange.Send;
            var sent = new List<Dictionary<string, object>>();
            var clip = new List<string>();
            try
            {
                Session.Disconnect();
                Session.NoNetworkForTest = true;
                Session.SentForTest = o => sent.Add(o);
                InviteForm.ClipboardSeam = t => clip.Add(t);
                TabLodge.IsChecked = true;
                LodgePage.ShowChatForTest();
                s.LodgeUrl = Inv2Url;
                await Inv2Sheet(sent, clip, check);
                Inv2WebLinks(check);
                await Inv2WebJoin(check);
                await Inv2ProfileCard(sent, check);
                await Inv2Guide(check);
                await Inv2NameRequired(check);
                await Inv2NameRequired(check);
                Inv2Policy(check);
                await Inv2Danger(sent, check);
                await Inv2LodgeCard(sent, check);
                await Inv2Banner(check);
            }
            catch (Exception e) { ok = false; notes.Add("crashed: " + e); }
            finally
            {
                Dialog.TestHook = null;
                InviteForm.ClipboardSeam = null;
                InviteExchange.Send = sendWas;
                Session.SentForTest = null;
                Session.Disconnect();
                Session.NoNetworkForTest = false;
                s.LodgeUrl = saved.LodgeUrl; s.LodgeCodeProtected = saved.LodgeCodeProtected; s.LodgeTokenProtected = saved.LodgeTokenProtected; s.LodgeDeviceName = saved.LodgeDeviceName;
                s.LodgeDeviceId = saved.LodgeDeviceId; s.LodgeUpgradeDeclined = saved.LodgeUpgradeDeclined; s.LodgeName = saved.LodgeName; s.LodgeManualConnect = saved.LodgeManualConnect;
                SettingsStore.Save(s);
            }
            return "invites2 selftest: " + (ok ? "PASSED" : "FAILED") + "\r\n  " + string.Join("\r\n  ", notes);
        }

        static Dictionary<string, object> Inv2Last(List<Dictionary<string, object>> sent, string t) => sent.LastOrDefault(f => f.Str("t") == t);

        // ---------------------------------------------------------------- the Invite sheet (Lodge sidebar)
        async Task Inv2Sheet(List<Dictionary<string, object>> sent, List<string> clip, InvCheck check)
        {
            var sheet = LodgePage.InviteSheetForTest;
            var form = sheet.FormForTest;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var s = settings;

            // who sees it
            Session.FeedForTest(Inv2Welcome("member", false));
            check("sheet: a member has no Invite button", !LodgePage.InviteButtonVisibleForTest);
            sent.Clear();
            sheet.Open();
            check("sheet: ...and the sheet does not open for them (nor ask for invites)", !sheet.IsOpen && !sent.Any(f => f.Str("t") == "admin.invites"));
            Session.FeedForTest(Inv2Welcome("officer", true));
            check("sheet: an officer has the Invite button", LodgePage.InviteButtonVisibleForTest);
            Session.FeedForTest(Inv2Welcome("owner", true));
            check("sheet: the Guild Master has it", LodgePage.InviteButtonVisibleForTest);
            Session.FeedForTest(Inv2Welcome("officer", true, tokens: false, v212: false));
            check("sheet: ...but not on a server without invite links (an officer cannot make codes there)", !LodgePage.InviteButtonVisibleForTest);

            // ranks follow InviteRanks.Offered
            check("ranks: owner -> all four", InviteRanks.Offered("owner", true).SequenceEqual(new[] { "owner", "officer", "veteran", "member" }));
            check("ranks: officer -> only below officer", InviteRanks.Offered("officer", true).SequenceEqual(new[] { "veteran", "member" }));
            check("ranks: veteran -> member only, member -> nothing", InviteRanks.Offered("veteran", true).SequenceEqual(new[] { "member" }) && InviteRanks.Offered("member", true).Count == 0);
            check("ranks: an old server (permanent codes) offers any rank to a manager", InviteRanks.Offered("officer", false).Count == 4);

            Session.FeedForTest(Inv2Welcome("officer", true));
            sent.Clear();
            sheet.Open();
            check("sheet: an officer opens it and it asks the server for the open invites", sheet.IsOpen && sent.Any(f => f.Str("t") == "admin.invites"));
            check("sheet: ...it says which lodge the invite is to", sheet.WhereForTest.StartsWith("To The Silver Lodge") && sheet.WhereForTest.Contains("example.org/lodge"), sheet.WhereForTest);
            check("sheet: an officer is offered Veteran and Member only, Member preselected", form.RankIdsForTest.SequenceEqual(new[] { "veteran", "member" }) && form.SelectedRoleForTest == "member", string.Join(",", form.RankIdsForTest));
            check("sheet: the button says 'Create link', one-time wording, no Advanced button", form.ButtonTextForTest == "Create link" && form.KindVisibleForTest && !form.AdvancedVisibleForTest);

            // person
            sent.Clear();
            form.CreateClickForTest("  ");
            check("sheet: no name -> asks for one and sends nothing", sent.Count == 0 && form.StatusForTest == "Type their name first", form.StatusForTest);
            form.SelectRoleForTest("veteran");
            form.SelectExpiryForTest(24);
            sent.Clear();
            form.CreateClickForTest("Cleo");
            var c = Inv2Last(sent, "admin.invite.create");
            check("sheet: Create (person) sends kind person, the name, the chosen rank and the chosen expiry",
                sent.Count == 1 && c != null && c.Str("kind") == "person" && c.Str("name") == "Cleo" && c.Str("role") == "veteran" && Convert.ToInt32(c["hours"]) == 24 && Convert.ToInt32(c["uses"]) == 1, c == null ? "nothing sent" : string.Join(",", c.Select(kv => kv.Key + "=" + kv.Value)));
            check("sheet: ...never a permanent code", !sent.Any(f => f.Str("t") == "admin.invite"));
            form.SelectRoleForTest("officer");
            check("sheet: an officer cannot pick Officer (not offered, selection unchanged)", form.SelectedRoleForTest == "veteran");

            // guest
            form.SetGuestKindForTest(true);
            check("sheet: a guest link hides the name and rank, shows 'people who can use it'", form.GuestForTest && !form.NameVisibleForTest && form.UsesVisibleForTest);
            form.SelectUsesForTest(5);
            form.SelectExpiryForTest(720);
            sent.Clear();
            form.CreateClickForTest("");
            c = Inv2Last(sent, "admin.invite.create");
            check("sheet: Create (guest) sends kind guest, rank guest, 5 uses, 30 days", c != null && c.Str("kind") == "guest" && c.Str("role") == "guest" && Convert.ToInt32(c["uses"]) == 5 && Convert.ToInt32(c["hours"]) == 720 && c.Str("name") == "");
            form.SetGuestKindForTest(false);

            // the answer: Copy / Copy as web link
            clip.Clear();
            Session.FeedForTest(Inv2Created(now, "person", "Cleo", "veteran", 1, InvA));
            await Task.Delay(100);
            var elans = "elanshub://lodge/join#example.org/lodge|" + InvA;
            var web = "https://example.org/lodge/#join=example.org/lodge|" + InvA;
            check("sheet: the new link is shown with the secret in the fragment only", form.ResultVisibleForTest && form.LinkForTest == elans && !form.LinkForTest.Contains("?"));
            check("sheet: ...and was copied for you (the elanshub:// link)", clip.Count >= 1 && clip.All(t => t == elans), string.Join(" | ", clip));
            check("sheet: ...the title names the person and rank", form.TitleForTest.Contains("Cleo") && form.TitleForTest.Contains("Veteran"), form.TitleForTest);
            clip.Clear();
            form.CopyClickForTest();
            check("sheet: Copy puts the elanshub:// link on the clipboard", clip.Count == 1 && clip[0] == elans && form.TitleForTest.StartsWith("Copied"), string.Join(" | ", clip));
            clip.Clear();
            check("sheet: 'Copy as web link' is offered", form.CopyWebVisibleForTest);
            form.CopyWebClickForTest();
            check("sheet: Copy as web link gives https://host/lodge/#join=host/lodge|invite", clip.Count == 1 && clip[0] == web && form.WebLinkForTest == web && form.TitleForTest.StartsWith("Web link copied"), string.Join(" | ", clip));
            check("sheet: ...and the secret is only after the # in both", elans.IndexOf(InvA, StringComparison.Ordinal) > elans.IndexOf('#') && web.IndexOf(InvA, StringComparison.Ordinal) > web.IndexOf('#') && !web.Contains("?"));
            sheet.Close();
            await Until(() => !sheet.IsOpen);
            await Settle();
            sheet.Open();
            check("sheet: opened again, the old link is gone (it is shown once)", !form.ResultVisibleForTest && form.LinkForTest == "" && form.WebLinkForTest == null && form.NameForTest == "");

            // pending list
            Session.FeedForTest(InvInvitesFrame(now));
            await Task.Delay(100);
            var pend = sheet.PendingForTest.RowsForTest;
            check("pending: only the open invites are listed (used, expired and revoked ones are not)", pend.Select(r => r.Id).OrderBy(x => x).SequenceEqual(new[] { "a1", "a2" }), string.Join(",", pend.Select(r => r.Id)));
            check("pending: ...with name, rank and expiry, and a Revoke button", pend.Any(r => r.Id == "a1" && r.Title == "Nova · Member" && r.Detail == "Made by Elan · expires in 6 days" && r.RevokeVisibility == Visibility.Visible));
            check("pending: ...no secret or hash anywhere", pend.All(r => !(r.Title + r.Detail).Contains("i1.")));
            sent.Clear();
            sheet.PendingForTest.RevokeForTest("a1");
            check("pending: Revoke sends admin.invite.revoke with that id", sent.Count == 1 && sent[0].Str("t") == "admin.invite.revoke" && sent[0].Str("id") == "a1");
            sheet.UpdateLayout();
            var revoke = FindDown<Button>(sheet.PendingForTest, b => (b.Content as string) == "Revoke" && (b.Tag as string) == "a2");
            sent.Clear();
            revoke?.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            check("pending: the real Revoke button on a row sends the same frame", revoke != null && sent.Count == 1 && sent[0].Str("t") == "admin.invite.revoke" && sent[0].Str("id") == "a2", revoke == null ? "button not found" : sent.Count + " frames");
            Session.FeedForTest("{\"t\":\"admin.invites\",\"now\":" + now + ",\"invites\":[]}");
            check("pending: with nothing open the list says so", sheet.PendingForTest.RowsForTest.Count == 0);
            sheet.Close();

            // the Guild Master: any rank, and the server error shows inside the sheet
            Session.FeedForTest(Inv2Welcome("owner", true));
            sheet.Open();
            check("sheet: the Guild Master is offered all four ranks", form.RankIdsForTest.SequenceEqual(new[] { "owner", "officer", "veteran", "member" }), string.Join(",", form.RankIdsForTest));
            form.SelectRoleForTest("officer");
            sent.Clear();
            form.CreateClickForTest("Bob");
            c = Inv2Last(sent, "admin.invite.create");
            check("sheet: ...and an owner can invite an officer", c != null && c.Str("role") == "officer" && c.Str("name") == "Bob");
            Session.FeedForTest("{\"t\":\"error\",\"text\":\"Your rank can't do that\"}");
            check("sheet: a refusal from the server shows in the sheet itself", form.StatusForTest == "Your rank can't do that", form.StatusForTest);
            sheet.Close();
            await Until(() => !sheet.IsOpen);
            s.LodgeUrl = Inv2Url;
        }

        // ---------------------------------------------------------------- web links: MakeWeb -> Parse
        void Inv2WebLinks(InvCheck check)
        {
            var web = InviteLinks.MakeWeb("https://example.org/lodge", InvA);
            check("web: MakeWeb is https://host/lodge/#join=host/lodge|invite", web == "https://example.org/lodge/#join=example.org/lodge|" + InvA, web);
            check("web: a trailing slash in the address makes no difference", InviteLinks.MakeWeb("https://example.org/lodge/", InvA) == web);
            var p = InviteLinks.Parse(web);
            check("web: MakeWeb -> Parse round trip (https)", p.Url == "https://example.org/lodge" && p.Secret == InvA && p.IsInvite && !p.MissingAddress);
            var lan = InviteLinks.MakeWeb("http://10.0.0.2:5280/lodge", InvA);
            p = InviteLinks.Parse(lan);
            check("web: ...and for a lodge without TLS (http, ip, port)", lan == "http://10.0.0.2:5280/lodge/#join=http://10.0.0.2:5280/lodge|" + InvA && p.Url == "http://10.0.0.2:5280/lodge" && p.Secret == InvA && p.IsInvite, lan + " -> " + p.Url);
            p = InviteLinks.Parse(web.Replace("|", "%7C"));
            check("web: a pipe turned into %7C by a chat app still parses", p.Url == "https://example.org/lodge" && p.Secret == InvA);
            p = InviteLinks.Parse("https://example.org/lodge/#join=" + Uri.EscapeDataString("example.org/lodge|" + InvA));
            check("web: a fully percent-encoded join= value still parses", p.Url == "https://example.org/lodge" && p.Secret == InvA);
            p = InviteLinks.Parse("Come join us: " + web + " - see you there!");
            check("web: pasted inside a sentence", p.Url == "https://example.org/lodge" && p.Secret == InvA);
            p = InviteLinks.Parse("<" + web + ">");
            check("web: in angle brackets", p.Secret == InvA && p.Url == "https://example.org/lodge");
            check("web: LooksLikeLink knows a web link, so the join box treats it as a link", InviteLinks.LooksLikeLink(web) && !InviteLinks.LooksLikeLink("hello there"));
            p = InviteLinks.Parse("https://example.org/lodge/?utm=x#join=example.org/lodge|" + InvA);
            check("web: a query on the page address is dropped, the join= value still names the lodge", p.Url == "https://example.org/lodge" && p.Secret == InvA);

            // a secret in the query string is refused
            p = InviteLinks.Parse("https://example.org/lodge/?join=example.org/lodge|" + InvA);
            check("web: a secret in the query (?join=) is refused", p.Secret == null && !p.IsInvite);
            p = InviteLinks.Parse("https://example.org/lodge/?invite=" + InvA + "#join=example.org/lodge");
            check("web: ?invite= in the query is refused even with a #join= address", p.Secret == null && !p.IsInvite && p.Url == "https://example.org/lodge");
            p = InviteLinks.Parse("https://evil.example/?join=evil.example|" + InvA + "#join=example.org/lodge|i1.ZzZzZzZzZzZzZzZzZzZzZzZzZzZz9999");
            check("web: when both are there only the fragment counts", p.Url == "https://example.org/lodge" && p.Secret == "i1.ZzZzZzZzZzZzZzZzZzZzZzZzZzZz9999");
            p = InviteLinks.Parse("elanshub://lodge/join?join=example.org/lodge|" + InvA);
            check("web: ...and for the elanshub:// form", p.Secret == null && !p.IsInvite);
            p = InviteLinks.Parse("https://example.org/lodge/#join=example.org/lodge&invite=" + InvA);
            check("web: a second parameter in the fragment is not read as the invite", p.Secret == null);

            // placeholder address
            p = InviteLinks.Parse("https://example.org/lodge/#join=<your-lodge-address>|" + InvA);
            check("web: a placeholder address is reported as missing, never guessed", p.MissingAddress && p.Url == null);
            p = InviteLinks.Parse("https://example.org/lodge/#join=your-lodge-address|" + InvA);
            check("web: a bare word with no dot is no address either", p.MissingAddress && p.Url == null);
            p = InviteLinks.Parse("https://example.org/lodge/#join=|" + InvA);
            check("web: an empty address too", p.MissingAddress && p.Url == null);

            // junk
            var junkOk = true; string bad = null;
            foreach (var junk in new[] { null, "", "   ", "#join=", "#join=|", "https://x.y/#join=", "https://x.y/#join=%", "https://x.y/#join=%ZZ|" + InvA, "https://x.y/#join=" + new string('a', 6000) + "|" + InvA, "https://x.y/#join=a b|c", "#join=\u0000", "https://x.y/#join=javascript:alert(1)|" + InvA })
            {
                try
                {
                    var j = InviteLinks.Parse(junk);
                    if (j.IsInvite && junk != null && !junk.EndsWith("|" + InvA)) { junkOk = false; bad = "invented an invite from " + junk; }
                    if (!string.IsNullOrEmpty(j.Url) && !(j.Url.StartsWith("https://") || j.Url.StartsWith("http://"))) { junkOk = false; bad = "odd address " + j.Url; }
                }
                catch (Exception e) { junkOk = false; bad = e.Message; }
            }
            check("web: junk never throws, never invents an invite, never yields a non-http(s) address", junkOk, bad);
        }

        // a web link pasted into the join box goes through the normal join
        async Task Inv2WebJoin(InvCheck check)
        {
            var s = settings;
            Session.Disconnect();
            LodgeCredentials.Clear(s);
            s.LodgeUrl = null; s.LodgeManualConnect = false;
            var asked = new List<HttpRequestMessage>();
            InviteExchange.Send = req => { asked.Add(req); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Gone) { Content = new StringContent("{\"error\":\"x\",\"reason\":\"used-or-expired\"}") }); };
            s.LodgeUrl = "https://old.example.org/lodge"; LodgeCredentials.StoreToken(s, s.LodgeUrl, TokB, "Cleo-PC");
            LodgePage.EditConnection();
            LodgePage.CodeBoxForTest = "synthetic-typed-code-123";
            LodgePage.CancelEditForTest();
            check("leftover: a code typed in 'Edit connection' and then cancelled does not wait in the box for the next join", LodgePage.CodeBoxForTest == "");
            LodgePage.CodeBoxForTest = "synthetic-typed-code-123";
            LodgePage.SignOut();
            check("leftover: ...nor after signing out", LodgePage.CodeBoxForTest == "" && !LodgeCredentials.HasToken(s));
            LodgePage.SignOut();
            LodgePage.JoinWithLink("https://127.0.0.1:9/lodge/#join=127.0.0.1:9/lodge|" + InvA);
            for (int i = 0; i < 20 && asked.Count == 0; i++) await Task.Delay(100);
            await Task.Delay(200);
            check("webjoin: a pasted web link is exchanged as an invite, secret in the header and not in the URL",
                asked.Count == 1 && asked[0].Headers.GetValues("X-Lodge-Invite").Single() == InvA && !asked[0].RequestUri.ToString().Contains(InvA) && asked[0].RequestUri.AbsolutePath == "/lodge/exchange", asked.Count + " requests");
            check("webjoin: ...a used link says so and nothing is saved", LodgePage.JoinStatusForTest.Contains("used or expired") && !LodgeCredentials.HasToken(s) && !LodgeCredentials.HasCode(s) && !Session.HasPendingCredential);
            asked.Clear();
            LodgePage.JoinWithLink("https://example.org/lodge/#join=<your-lodge-address>|" + InvA);
            await Task.Delay(200);
            check("webjoin: a web link with a placeholder address says so and sends nothing", LodgePage.JoinStatusForTest == "This link is missing the lodge address - ask for a new link" && asked.Count == 0, LodgePage.JoinStatusForTest);
            asked.Clear();
            LodgePage.JoinWithLink("https://example.org/lodge/?join=example.org/lodge|" + InvA);
            await Task.Delay(200);
            check("webjoin: a secret in the query is not an invite: nothing is sent", asked.Count == 0 && !Session.HasPendingCredential && !LodgeCredentials.HasToken(s), $"asked={asked.Count} pending={Session.HasPendingCredential} token={LodgeCredentials.HasToken(s)} status={LodgePage.JoinStatusForTest}");
            LodgePage.SignOut();
        }

        // ---------------------------------------------------------------- "Send a new device link" on a profile card
        async Task Inv2ProfileCard(List<Dictionary<string, object>> sent, InvCheck check)
        {
            var lodge = LodgePage;
            var sheet = lodge.InviteSheetForTest;
            Func<string, string, bool, bool> shows = (name, role, guest) =>
            {
                lodge.ShowMemberForTest(new MemberVM { Id = 50, Name = name, Role = role, Guest = guest });
                return lodge.DeviceLinkVisibleForTest;
            };

            Session.FeedForTest(Inv2Welcome("owner", true));
            check("card: owner -> shown for a member, a veteran, an officer and another Guild Master",
                shows("Mia", "member", false) && shows("Vera", "veteran", false) && shows("Bob", "officer", false) && shows("Olga", "owner", false));
            check("card: ...but not for a guest", !shows("Wanderer (guest)", "guest", true));
            lodge.ShowMemberForTest(new MemberVM { Id = 1, Name = "Elan", Role = "owner", IsMe = true });
            check("card: ...and not on my own card", !lodge.DeviceLinkVisibleForTest);

            Session.FeedForTest(Inv2Welcome("officer", true));
            check("card: officer -> shown for a veteran and a member", shows("Vera", "veteran", false) && shows("Mia", "member", false));
            check("card: ...NOT for another officer or for a Guild Master", !shows("Bob", "officer", false) && !shows("Olga", "owner", false));
            check("card: ...and not for a guest", !shows("Wanderer (guest)", "guest", true));

            Session.FeedForTest(Inv2Welcome("veteran", false));
            check("card: a veteran (no invite right) -> never shown", !shows("Mia", "member", false) && !shows("Vera", "veteran", false));
            Session.FeedForTest(Inv2Welcome("member", false));
            check("card: a member -> never shown", !shows("Mia", "member", false) && !shows("Bob", "officer", false) && !shows("Olga", "owner", false));
            Session.FeedForTest(Inv2Welcome("owner", true, tokens: false, v212: false));
            check("card: a server without invite links -> not shown, even to the Guild Master", !shows("Mia", "member", false));
            check("card: the rule itself (InviteRanks.CanSendDeviceLink)",
                InviteRanks.CanSendDeviceLink("owner", true, true, "owner", false) && InviteRanks.CanSendDeviceLink("officer", true, true, "veteran", false)
                && !InviteRanks.CanSendDeviceLink("officer", true, true, "officer", false) && !InviteRanks.CanSendDeviceLink("officer", true, true, "owner", false)
                && !InviteRanks.CanSendDeviceLink("member", false, true, "member", false) && !InviteRanks.CanSendDeviceLink("owner", true, true, "member", true) && !InviteRanks.CanSendDeviceLink("owner", true, false, "member", false));

            // what it does: a person invite with the member's current rank
            Session.FeedForTest(Inv2Welcome("owner", true));
            sent.Clear();
            shows("Vera", "veteran", false);
            lodge.DeviceLinkClickForTest();
            var c = Inv2Last(sent, "admin.invite.create");
            check("card: owner -> Vera (Veteran): one person invite for 'Vera' with the rank Veteran, 7 days", c != null && c.Str("kind") == "person" && c.Str("name") == "Vera" && c.Str("role") == "veteran" && Convert.ToInt32(c["hours"]) == 168 && sent.Count(f => f.Str("t") == "admin.invite.create") == 1,
                c == null ? "nothing sent" : string.Join(",", c.Select(kv => kv.Key + "=" + kv.Value)));
            check("card: ...the sheet is open on it and asked for the open invites too", sheet.IsOpen && sent.Any(f => f.Str("t") == "admin.invites"));
            Session.FeedForTest(Inv2Created(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "person", "Vera", "veteran", 1, InvA));
            await Task.Delay(100);
            check("card: ...and the answer shows as a link for Vera", sheet.FormForTest.ResultVisibleForTest && sheet.FormForTest.TitleForTest.Contains("Vera") && sheet.FormForTest.LinkForTest.EndsWith("|" + InvA));
            sheet.Close();
            await Until(() => !sheet.IsOpen);

            sent.Clear();
            shows("Bob", "officer", false);
            lodge.DeviceLinkClickForTest();
            c = Inv2Last(sent, "admin.invite.create");
            check("card: owner -> Bob (Officer): the rank is Officer, not the default Member", c != null && c.Str("name") == "Bob" && c.Str("role") == "officer");
            sheet.Close();
            await Until(() => !sheet.IsOpen);

            Session.FeedForTest(Inv2Welcome("officer", true));
            sent.Clear();
            shows("Mia", "member", false);
            lodge.DeviceLinkClickForTest();
            c = Inv2Last(sent, "admin.invite.create");
            check("card: officer -> Mia (Member): a person invite as Member", c != null && c.Str("kind") == "person" && c.Str("name") == "Mia" && c.Str("role") == "member");
            sheet.Close();
            await Until(() => !sheet.IsOpen);

            // a rank the officer may not give is never sent as 'member' instead
            sent.Clear();
            sheet.Open("Bob", "officer");
            check("card: an officer asked for a device link on an officer sends NOTHING (never falls back to another rank) and says why",
                !sent.Any(f => f.Str("t") == "admin.invite.create") && sheet.FormForTest.StatusForTest.Contains("below you"), sheet.FormForTest.StatusForTest);
            sheet.Close();
            await Until(() => !sheet.IsOpen);
        }

        // ---------------------------------------------------------------- "Create a lodge" guide
        async Task Inv2Guide(InvCheck check)
        {
            var s = settings;
            var lodge = LodgePage;
            var asked = new List<HttpRequestMessage>(); var everything = new List<HttpRequestMessage>(); var bodies = new List<string>();
            Func<HttpResponseMessage> postAnswer = () => new HttpResponseMessage(HttpStatusCode.Gone) { Content = new StringContent("{\"error\":\"x\",\"reason\":\"used-or-expired\"}") };
            InviteExchange.Send = async req =>
            {
                everything.Add(req);
                if (req.Method == HttpMethod.Get) return new HttpResponseMessage(HttpStatusCode.NotFound);
                asked.Add(req); bodies.Add(req.Content == null ? "" : await req.Content.ReadAsStringAsync());
                return postAnswer();
            };
            Session.Disconnect();
            LodgeCredentials.Clear(s);
            s.LodgeUrl = null; s.LodgeManualConnect = false; s.LodgeName = "Cleo"; s.LodgeDeviceName = "Cleo-PC";
            lodge.SignOut();
            check("guide: the join screen offers 'Create a lodge'", lodge.CreateLodgeVisibleForTest);
            var g = lodge.GuideForTest;
            g.NoClipboardForTest = true;
            var opened = new List<string>();
            g.OpenUrl = u => opened.Add(u);
            check("guide: closed to begin with", !g.IsOpen);
            lodge.CreateLodgeClickForTest();
            check("guide: it opens on step 1 with Next and no Back", g.IsOpen && g.StepForTest == 1 && g.NextTextForTest == "Next" && !g.BackVisibleForTest);
            g.ReadmeClickForTest();
            check("guide: step 1 links to the setup guide on GitHub (https, nothing else is launched)", opened.Count == 1 && opened[0] == CreateLodgeGuide.ReadmeUrl && opened[0].StartsWith("https://github.com/"), string.Join(",", opened));
            g.NextForTest();
            check("guide: step 2 has Back", g.StepForTest == 2 && g.BackVisibleForTest && g.NextTextForTest == "Next");
            check("guide: ...and the two install commands (clone + install.sh)", g.CommandsForTest == CreateLodgeGuide.InstallCommands && g.CommandsForTest.Contains("git clone") && g.CommandsForTest.Contains("install.sh") && g.CommandsForTest.Split('\n').Length == 2);
            g.CopyCommandsForTest();
            check("guide: Copy commands copies exactly them and says so", g.CopiedForTest == CreateLodgeGuide.InstallCommands && g.CopyNoteForTest.StartsWith("Copied"));
            g.BackForTest();
            check("guide: Back returns to step 1", g.StepForTest == 1 && !g.BackVisibleForTest);
            g.NextForTest(); g.NextForTest();
            check("guide: step 3 is where the owner link goes; the button now says 'Use this link'", g.StepForTest == 3 && g.NextTextForTest == "Use this link");

            // step 3 refusals leave the guide open and send nothing
            g.LinkBoxForTest = ""; g.NextForTest();
            check("guide: no link -> it asks for the owner link", g.IsOpen && g.StepForTest == 3 && (g.LinkProblemForTest ?? "").StartsWith("Paste the owner link"), g.LinkProblemForTest);
            g.LinkBoxForTest = "hello there"; g.NextForTest();
            check("guide: text that is not a link is refused", g.IsOpen && (g.LinkProblemForTest ?? "").Contains("does not look like the owner link"), g.LinkProblemForTest);
            g.LinkBoxForTest = "elanshub://lodge/join#<your-lodge-address>|" + InvA; g.NextForTest();
            check("guide: a placeholder address is refused with the fix (LODGE_PUBLIC_URL / claim-owner)", g.IsOpen && (g.LinkProblemForTest ?? "").Contains("LODGE_PUBLIC_URL") && (g.LinkProblemForTest ?? "").Contains("claim-owner"), g.LinkProblemForTest);
            g.LinkBoxForTest = "https://example.org/lodge#invite=0123456789abcdef0123456789abcdef"; g.NextForTest();
            check("guide: an old permanent-code link is not an owner link", g.IsOpen && (g.LinkProblemForTest ?? "").Contains("does not look like the owner link"));
            g.LinkBoxForTest = "https://example.org/lodge/?join=example.org/lodge|" + InvA; g.NextForTest();
            check("guide: a secret in a query string is refused", g.IsOpen && g.LinkProblemForTest != null);
            check("guide: ...none of that connected, saved or sent anything", asked.Count == 0 && !Session.HasPendingCredential && !LodgeCredentials.HasToken(s) && lodge.JoinPanelVisibleForTest);
            check("guide: ...and the join screen is not showing an invite yet", lodge.IncomingTitleForTest == null);

            // a saved sign-in elsewhere must survive the whole owner-link flow until the welcome (2.38.1)
            const string oldUrl = "https://old.example.org/lodge";
            LodgeCredentials.StoreToken(s, oldUrl, TokB, "Cleo-PC");
            s.LodgeUrl = oldUrl;
            var ownerLink = "elanshub://lodge/join#example.org/lodge|" + InvA;
            g.LinkBoxForTest = ownerLink; g.NextForTest();
            check("guide: a good owner link closes the guide and empties the box (the secret does not stay in it)", await Until(() => !g.IsOpen && g.LinkBoxForTest == ""));
            check("guide: ...it feeds the normal join: the Lodge join screen with 'Your new lodge: example.org/lodge'", lodge.JoinPanelVisibleForTest && lodge.IncomingTitleForTest == "Your new lodge: example.org/lodge", lodge.IncomingTitleForTest);
            check("guide: ...it says you become the Guild Master and that nothing happens until Join", (lodge.IncomingTextForTest ?? "").Contains("Guild Master") && (lodge.IncomingTextForTest ?? "").Contains("owner link"));
            check("guide: ...the name box is empty and captioned for the Guild Master's name", lodge.NameBoxForTest == "" && lodge.NameCaptionForTest.Contains("Guild Master"), lodge.NameCaptionForTest);
            check("guide: ...the secret is masked in the link box", lodge.UrlBoxForTest.StartsWith("elanshub://lodge/join#example.org/lodge|i1.") && !lodge.UrlBoxForTest.Contains(InvA.Substring(5, 20)) && lodge.UrlBoxForTest.Contains("•"), lodge.UrlBoxForTest);
            check("guide: ...ZERO requests of any kind so far (opening the guide, every step, the refusals, handing the link on), nothing committed", asked.Count == 0 && everything.Count == 0 && !Session.HasPendingCredential && TokenIs(s, TokB) && s.LodgeUrl == oldUrl);

            // join without a name: the server says name-required -> the caption changes and nothing is committed
            lodge.NameBoxForTest = "";
            postAnswer = () => new HttpResponseMessage((HttpStatusCode)400) { Content = new StringContent("{\"error\":\"Type the name you want in the lodge (1-24 letters, digits, space, _ or -)\",\"reason\":\"name-required\"}") };
            asked.Clear(); bodies.Clear();
            lodge.JoinForTest();
            for (int i = 0; i < 20 && asked.Count == 0; i++) await Task.Delay(100);
            await Task.Delay(300);
            check("guide: the owner link is exchanged with the real secret in the header", asked.Count == 1 && asked[0].Headers.GetValues("X-Lodge-Invite").Single() == InvA && !asked[0].RequestUri.ToString().Contains(InvA) && asked[0].RequestUri.Host == "example.org");
            check("guide: name-required -> the status shows the server's words and the caption asks for the Guild Master's name",
                lodge.JoinStatusForTest.StartsWith("Type the name you want") && lodge.NameCaptionForTest == "YOUR NAME IN THE LODGE  (you will be its Guild Master)", lodge.JoinStatusForTest + " / " + lodge.NameCaptionForTest);
            check("guide: ...the link is NOT committed: the saved sign-in is untouched and nothing is pending (commit only after the welcome)",
                TokenIs(s, TokB) && s.LodgeUrl == oldUrl && !Session.HasPendingCredential && !Session.Connected && !LodgeCredentials.HasCode(s));
            check("guide: ...and the join screen stays up with a way back to the saved sign-in", lodge.JoinPanelVisibleForTest && lodge.RestoreVisibleForTest);

            // the second try with a name works, but still only counts after the welcome
            postAnswer = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"token\":" + Q(TokA) + ",\"deviceId\":\"d1\",\"device\":\"Cleo-PC\",\"name\":\"Silva\",\"role\":\"owner\",\"guest\":false}") };
            lodge.NameBoxForTest = "Silva";
            asked.Clear(); bodies.Clear();
            lodge.JoinForTest();
            for (int i = 0; i < 20 && !Session.HasPendingCredential; i++) await Task.Delay(100);
            await Task.Delay(200);
            check("guide: with a name the body carries it (so the server can name the new Guild Master)", bodies.Count == 1 && bodies[0].Contains("\"name\":\"Silva\"") && !bodies[0].Contains(InvA), string.Join("|", bodies));
            check("guide: ...the new token is only a candidate until the welcome; the old sign-in still stands", Session.HasPendingCredential && TokenIs(s, TokB) && s.LodgeUrl == oldUrl);
            Session.FeedForTest(Inv2Welcome("owner", true, lodgeName: ""));
            await Task.Delay(100);
            check("guide: after the welcome the owner token replaces it and the lodge address is the new one", TokenIs(s, TokA) && s.LodgeUrl == Inv2Url && !Session.HasPendingCredential && s.LodgeName == "Silva", s.LodgeUrl + " " + s.LodgeName);

            // the web form of the owner link is accepted too
            Session.Disconnect(); LodgeCredentials.Clear(s); s.LodgeUrl = null; lodge.SignOut();
            everything.Clear();
            lodge.CreateLodgeClickForTest();
            g.NextForTest(); g.NextForTest();
            g.LinkBoxForTest = InviteLinks.MakeWeb("https://example.org/lodge", InvA);
            g.NextForTest();
            await Task.Delay(600);
            check("guide: the web form of the owner link (#join=) is accepted as well", !g.IsOpen && lodge.IncomingTitleForTest == "Your new lodge: example.org/lodge", lodge.IncomingTitleForTest);
            check("guide: ...and handing the web form on makes no request either", everything.Count == 0, everything.Count + " requests");
            lodge.SignOut();
            check("guide: Escape / the close button leave without a trace", CloseGuideCheck(g));
        }

        // an owner link pasted straight into the join box (not through the guide): no name -> the server says name-required
        async Task Inv2NameRequired(InvCheck check)
        {
            var s = settings;
            var lodge = LodgePage;
            var asked = new List<HttpRequestMessage>(); var bodies = new List<string>();
            InviteExchange.Send = async req =>
            {
                asked.Add(req); bodies.Add(req.Content == null ? "" : await req.Content.ReadAsStringAsync());
                return new HttpResponseMessage((HttpStatusCode)400) { Content = new StringContent("{\"error\":\"Type the name you want in the lodge (1-24 letters, digits, space, _ or -)\",\"reason\":\"name-required\"}") };
            };
            Session.Disconnect();
            LodgeCredentials.Clear(s);
            s.LodgeUrl = null; s.LodgeManualConnect = false; s.LodgeName = "";
            lodge.SignOut();
            check("name-required: the name caption is the ordinary one to begin with", lodge.NameCaptionForTest.StartsWith("NAME  (only used with a shared guest code)"), lodge.NameCaptionForTest);
            lodge.JoinWithLink("elanshub://lodge/join#example.org/lodge|" + InvA);
            for (int i = 0; i < 20 && asked.Count == 0; i++) await Task.Delay(100);
            await Task.Delay(300);
            check("name-required: the exchange went out without a name", asked.Count == 1 && !bodies[0].Contains("\"name\""), bodies.Count > 0 ? bodies[0] : "no request");
            check("name-required: the answer changes the caption to the Guild Master wording and shows the server's sentence",
                lodge.NameCaptionForTest.StartsWith("YOUR NAME IN THE LODGE") && lodge.JoinStatusForTest.StartsWith("Type the name you want"), lodge.NameCaptionForTest + " / " + lodge.JoinStatusForTest);
            check("name-required: ...and nothing is stored, pending or connected", !LodgeCredentials.HasToken(s) && !LodgeCredentials.HasCode(s) && !Session.HasPendingCredential && !Session.Connected && s.LodgeUrl == null);
            lodge.SignOut();
            check("name-required: the Guild Master wording does not stick to the next join screen", lodge.NameCaptionForTest.StartsWith("NAME  (only used with a shared guest code)"), lodge.NameCaptionForTest);
            lodge.ShowInviteLink("elanshub://lodge/join#example.org/lodge|" + InvA, true);
            check("name-required: an owner link sets it up front", lodge.NameCaptionForTest.StartsWith("YOUR NAME IN THE LODGE"));
            lodge.ShowInviteLink("elanshub://lodge/join#example.org/lodge|" + InvA, false);
            check("name-required: an ordinary invite link after it puts the ordinary caption back", lodge.NameCaptionForTest.StartsWith("NAME  (only used with a shared guest code)"), lodge.NameCaptionForTest);
            lodge.SignOut();
        }

        static bool CloseGuideCheck(CreateLodgeGuide g)
        {
            g.Open();
            bool open = g.IsOpen;
            g.NextForTest(); g.NextForTest();
            g.LinkBoxForTest = "elanshub://lodge/join#example.org/lodge|" + InvA;
            g.Close();
            g.Open();
            // re-opening starts again at step 1 with an empty box: a half-typed secret does not wait there
            return open && g.StepForTest == 1 && g.LinkBoxForTest == "";
        }

        // ---------------------------------------------------------------- the rules behind the Danger zone (pure)
        void Inv2Policy(InvCheck check)
        {
            check("policy: a Guild Master with one device must type the name; with two they need not; a member never", LeavePolicy.SignOutNeedsTypedName("owner", 1) && LeavePolicy.SignOutNeedsTypedName("owner", 0)
                && !LeavePolicy.SignOutNeedsTypedName("owner", 2) && !LeavePolicy.SignOutNeedsTypedName("member", 1) && !LeavePolicy.SignOutNeedsTypedName("officer", 1));
            check("policy: the phrase is the lodge's name, or the address when it has none", LeavePolicy.Phrase(" The Silver Lodge ", Inv2Url) == "The Silver Lodge" && LeavePolicy.Phrase("", Inv2Url) == "example.org/lodge" && LeavePolicy.Phrase(null, "http://10.0.0.2:5280/lodge") == "10.0.0.2:5280/lodge");
            check("policy: typing matches ignoring case and outer spaces, but nothing else", LeavePolicy.Matches("the silver lodge", "The Silver Lodge") && LeavePolicy.Matches("  The Silver Lodge ", "The Silver Lodge")
                && !LeavePolicy.Matches("The Silver", "The Silver Lodge") && !LeavePolicy.Matches("", "The Silver Lodge") && !LeavePolicy.Matches("x", "") && !LeavePolicy.Matches(null, null));
            check("policy: the recovery command names the person (or a placeholder)", LeavePolicy.RecoverCommand("Elan") == "sudo lodge-admin recover-owner Elan" && LeavePolicy.RecoverCommand(" ").Contains("<your name>"));
            var people = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("Elan", "owner"), new KeyValuePair<string, string>("Zed", "officer"), new KeyValuePair<string, string>("Bob", "officer"),
                new KeyValuePair<string, string>("Dax", "member"), new KeyValuePair<string, string>("Gus", "guest"), new KeyValuePair<string, string>("Olga", "owner"),
            };
            check("policy: transfer candidates are the officers (sorted); not me, not another owner, not guests", LeavePolicy.TransferCandidates(people, "Elan").SequenceEqual(new[] { "Bob", "Zed" }));
            check("policy: with no officer, the members", LeavePolicy.TransferCandidates(people.Where(p => p.Value != "officer"), "Elan").SequenceEqual(new[] { "Dax" }));
            check("policy: with nobody, nobody", LeavePolicy.TransferCandidates(people.Where(p => p.Key == "Elan" || p.Value == "guest"), "Elan").Count == 0);

            // the real dialog: the confirm button works only once the phrase is typed (built, never shown)
            var d = ThemedDialog.Create(DialogKind.Danger, "Sign out the only device of a Guild Master?", "Type The Silver Lodge to confirm.", "Sign out this PC", "Cancel", "The Silver Lodge");
            var box = d.TypedBoxForTest; var ok = d.ConfirmButtonForTest;
            bool begin = box.Visibility == Visibility.Visible && !ok.IsEnabled;
            box.Text = "Wrong name"; bool wrong = !ok.IsEnabled;
            box.Text = "The Silver Lodg"; bool almost = !ok.IsEnabled;
            box.Text = "the silver lodge"; bool right = ok.IsEnabled;
            box.Text = "  The Silver Lodge  "; bool spaces = ok.IsEnabled;
            box.Text = ""; bool empty = !ok.IsEnabled;
            d.Close();
            check("typed dialog: the confirm button is off to begin with and for a wrong, partial or empty name", begin && wrong && almost && empty);
            check("typed dialog: ...and on for the name (any case, spaces around ignored)", right && spaces);
            var plain = ThemedDialog.Create(DialogKind.Danger, "Sign out this PC?", "x", "Sign out", "Cancel");
            bool plainOk = plain.TypedBoxForTest.Visibility != Visibility.Visible && plain.ConfirmButtonForTest.IsEnabled;
            plain.Close();
            check("typed dialog: an ordinary confirm has no typing box and an enabled button", plainOk);
        }

        // ---------------------------------------------------------------- Settings > Lodge > Danger zone
        async Task Inv2Danger(List<Dictionary<string, object>> sent, InvCheck check)
        {
            var s = settings;
            var calls = new List<DialogCall>();
            bool answer = false; string typed = "";
            Dialog.TestHook = c => { calls.Add(c); return c.Kind == "typed" ? LeavePolicy.Matches(typed, c.Phrase) : answer; };
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            async Task Fresh(string role, int devices, string lodgeName = "The Silver Lodge", bool v212 = true)
            {
                Session.Disconnect();
                LodgeCredentials.Clear(s);
                LodgeCredentials.StoreToken(s, Inv2Url, TokA, "Elan-PC");
                s.LodgeUrl = Inv2Url;
                LodgePage.ShowChatForTest();
                Session.FeedForTest(Inv2Welcome(role, role == "owner" || role == "officer", lodgeName, v212: v212));
                Session.FeedForTest(Inv2Devices(now, devices));
                ShowTab("settings");
                SettingsPage.Show("lodge");
                await Task.Delay(250);
                calls.Clear(); sent.Clear();
            }

            // ---- Sign out this PC: a Guild Master with one device
            await Fresh("owner", 1);
            typed = "Some Other Lodge";
            SettingsPage.SignOutPcClickForTest();
            check("danger: a Guild Master signing out their only device is asked to TYPE the lodge's name", calls.Count == 1 && calls[0].Kind == "typed" && calls[0].Phrase == "The Silver Lodge", calls.Count + " " + (calls.FirstOrDefault()?.Kind));
            check("danger: ...the question says how to get back in (recover-owner) and what to type", calls[0].Text.Contains("sudo lodge-admin recover-owner Elan") && calls[0].Text.Contains("Type The Silver Lodge"), calls[0].Text);
            check("danger: ...a wrong name cancels: still signed in, nothing forgotten, nothing sent", TokenIs(s, TokA) && s.LodgeUrl == Inv2Url && !LodgePage.JoinPanelVisibleForTest && sent.Count == 0 && Session.MyRole == "owner");
            calls.Clear(); typed = "the silver lodge";
            SettingsPage.SignOutPcClickForTest();
            await Task.Delay(100);
            check("danger: the right name (any case) signs this PC out: the sign-in is forgotten here, the join screen shows", calls.Count == 1 && !LodgeCredentials.HasToken(s) && !LodgeCredentials.HasCode(s) && LodgePage.JoinPanelVisibleForTest);
            check("danger: ...and this is only this PC: no leave or remove request went to the lodge", !sent.Any(f => (f.Str("t") ?? "").Contains("leave") || (f.Str("t") ?? "").Contains("remove") || f.Str("t") == "device.revoke"));

            // a lodge with no name yet: the address is the phrase
            await Fresh("owner", 1, lodgeName: "");
            typed = "x";
            SettingsPage.SignOutPcClickForTest();
            check("danger: a lodge without a name asks for its address instead", calls.Count == 1 && calls[0].Kind == "typed" && calls[0].Phrase == "example.org/lodge" && TokenIs(s, TokA), calls.FirstOrDefault()?.Phrase);

            // a Guild Master with two devices: an ordinary confirm
            await Fresh("owner", 2);
            answer = false;
            SettingsPage.SignOutPcClickForTest();
            check("danger: a Guild Master with two devices gets an ordinary confirm (no typing)", calls.Count == 1 && calls[0].Kind == "danger" && calls[0].Phrase == null && TokenIs(s, TokA) && !LodgePage.JoinPanelVisibleForTest);
            answer = true; calls.Clear();
            SettingsPage.SignOutPcClickForTest();
            await Task.Delay(100);
            check("danger: ...confirmed, it signs this PC out", calls.Count == 1 && !LodgeCredentials.HasToken(s) && LodgePage.JoinPanelVisibleForTest);

            // an officer / member: one ordinary confirm
            await Fresh("member", 1);
            answer = false;
            SettingsPage.SignOutPcClickForTest();
            check("danger: a member signing out gets one ordinary confirm", calls.Count == 1 && calls[0].Kind == "danger" && TokenIs(s, TokA));

            // ---- Leave the lodge: a member, one confirm
            await Fresh("member", 1);
            answer = false;
            SettingsPage.LeaveLodgeClickForTest();
            check("danger: Leave the lodge asks once; 'Cancel' sends nothing", calls.Count == 1 && calls[0].Title == "Leave the lodge?" && calls[0].Kind == "danger" && !sent.Any(f => f.Str("t") == "member.leave") && TokenIs(s, TokA));
            answer = true; calls.Clear(); sent.Clear();
            SettingsPage.LeaveLodgeClickForTest();
            check("danger: a member leaves with ONE confirm (no typing) and 'member.leave' goes to the lodge", calls.Count == 1 && calls[0].Kind == "danger" && sent.Count(f => f.Str("t") == "member.leave") == 1, calls.Count + " dialogs");
            check("danger: ...nothing is forgotten until the lodge answers", TokenIs(s, TokA) && !LodgePage.JoinPanelVisibleForTest);
            Session.FeedForTest("{\"t\":\"member.left\"}");
            await Task.Delay(150);
            check("danger: 'member.left' signs this PC out and the join screen says you left", !LodgeCredentials.HasToken(s) && !LodgeCredentials.HasCode(s) && LodgePage.JoinPanelVisibleForTest && LodgePage.JoinStatusForTest.StartsWith("You left the lodge"), LodgePage.JoinStatusForTest);

            // an older server cannot do it
            await Fresh("member", 1, v212: false);
            SettingsPage.LeaveLodgeClickForTest();
            check("danger: on a server older than 2.12, Leave explains and sends nothing", calls.Count == 1 && calls[0].Kind == "info" && calls[0].Text.Contains("2.12") && !sent.Any(f => f.Str("t") == "member.leave") && TokenIs(s, TokA));

            // ---- the last Guild Master: refusal -> transfer panel
            await Fresh("owner", 2);
            answer = true;
            SettingsPage.LeaveLodgeClickForTest();
            check("danger: a Guild Master leaving is asked once and the request goes out", calls.Count == 1 && sent.Count(f => f.Str("t") == "member.leave") == 1);
            check("danger: no transfer panel yet", !SettingsPage.TransferVisibleForTest);
            sent.Clear();
            Session.FeedForTest("{\"t\":\"error\",\"text\":\"Transfer ownership first\",\"code\":\"last-owner\"}");
            check("transfer: the last-owner refusal shows the transfer panel (and asks for the member list)", SettingsPage.TransferVisibleForTest && SettingsPage.TransferTitleForTest == "Transfer ownership first" && sent.Any(f => f.Str("t") == "admin.members"));
            check("transfer: ...and is not shown as a red error elsewhere", SettingsPage.GmStatusForTest == "");
            Session.FeedForTest(Inv2Members("Elan:owner", "Zed:officer", "Bob:officer", "Dax:member", "Gus:guest"));
            check("transfer: the picker lists the officers, not me, not guests, not members", SettingsPage.TransferChoicesForTest.SequenceEqual(new[] { "Bob", "Zed" }) && SettingsPage.TransferRowVisibleForTest && !SettingsPage.TransferEmptyVisibleForTest, string.Join(",", SettingsPage.TransferChoicesForTest));
            SettingsPage.PickTransferForTest("Zed");
            calls.Clear(); sent.Clear(); answer = false;
            SettingsPage.TransferConfirmClickForTest();
            check("transfer: 'Cancel' on the confirm sends nothing", calls.Count == 1 && calls[0].Title == "Transfer ownership to Zed?" && !sent.Any(f => f.Str("t") == "admin.role"));
            answer = true; calls.Clear();
            SettingsPage.TransferConfirmClickForTest();
            var role = Inv2Last(sent, "admin.role");
            check("transfer: confirming sends admin.role owner for the picked person", calls.Count == 1 && role != null && role.Str("name") == "Zed" && role.Str("role") == "owner" && sent.Count(f => f.Str("t") == "admin.role") == 1,
                role == null ? "no admin.role" : string.Join(",", role.Select(kv => kv.Key + "=" + kv.Value)));
            Session.FeedForTest(Inv2Members("Elan:owner", "Zed:owner", "Bob:officer", "Dax:member"));
            check("transfer: once someone else is a Guild Master the panel says so and the picker goes away", SettingsPage.TransferTitleForTest == "Ownership transferred" && !SettingsPage.TransferRowVisibleForTest && !SettingsPage.TransferEmptyVisibleForTest);

            // no officers -> members; nobody -> a hint
            await Fresh("owner", 2);
            SettingsPage.ShowTransferForTest("Transfer ownership first");
            Session.FeedForTest(Inv2Members("Elan:owner", "Mia:member", "Dax:member", "Gus:guest"));
            check("transfer: with no officers the picker lists the members", SettingsPage.TransferChoicesForTest.SequenceEqual(new[] { "Dax", "Mia" }) && SettingsPage.TransferTextForTest.Contains("no officer"), string.Join(",", SettingsPage.TransferChoicesForTest));
            SettingsPage.PickTransferForTest("Dax");
            sent.Clear(); answer = true;
            SettingsPage.TransferConfirmClickForTest();
            role = Inv2Last(sent, "admin.role");
            check("transfer: ...and sends admin.role owner for the member picked", role != null && role.Str("name") == "Dax" && role.Str("role") == "owner");
            Session.FeedForTest(Inv2Members("Elan:owner", "Gus:guest"));
            check("transfer: with nobody to hand over to it says to invite someone first", SettingsPage.TransferEmptyVisibleForTest && !SettingsPage.TransferRowVisibleForTest && SettingsPage.TransferChoicesForTest.Count == 0);
            sent.Clear();
            SettingsPage.TransferConfirmClickForTest();
            check("transfer: ...and the button does nothing", !sent.Any(f => f.Str("t") == "admin.role"));

            Dialog.TestHook = null;
            Session.Disconnect();
            LodgeCredentials.Clear(s);
        }

        // ---------------------------------------------------------------- Guild Master > Your lodge
        async Task Inv2LodgeCard(List<Dictionary<string, object>> sent, InvCheck check)
        {
            var s = settings;
            Session.Disconnect();
            LodgeCredentials.Clear(s);
            s.LodgeUrl = Inv2Url;
            LodgePage.ShowChatForTest();
            sent.Clear();
            Session.FeedForTest(Inv2Welcome("owner", true));
            ShowTab("settings");
            SettingsPage.Show("gm");
            check("lodgecard: the Guild Master sees 'Your lodge', filled from the welcome", await Until(() => SettingsPage.GmOwnerOnlyVisibleForTest && SettingsPage.LodgeNameBoxForTest == "The Silver Lodge" && SettingsPage.LodgeWelcomeBoxForTest == "Welcome in. Be kind."), SettingsPage.LodgeNameBoxForTest);
            check("lodgecard: Save is on for a 2.12 server", SettingsPage.LodgeInfoSaveEnabledForTest);
            check("lodgecard: the boxes stop typing at the server's caps (40 and 280)", SettingsPage.LodgeNameMaxForTest == LodgeSession.MaxLodgeName && SettingsPage.LodgeWelcomeMaxForTest == LodgeSession.MaxLodgeWelcome && LodgeSession.MaxLodgeName == 40 && LodgeSession.MaxLodgeWelcome == 280 && LodgeSession.MaxLodgeWelcomeLines == 4);

            SettingsPage.LodgeNameBoxForTest = "  The Gold Lodge  ";
            SettingsPage.LodgeWelcomeBoxForTest = "Hello there.\nRead the pins.";
            sent.Clear();
            SettingsPage.LodgeInfoSaveForTest();
            var set = Inv2Last(sent, "admin.lodge.set");
            check("lodgecard: Save sends admin.lodge.set with the trimmed name and the welcome text", set != null && sent.Count == 1 && set.Str("name") == "The Gold Lodge" && (set.Str("welcome") ?? "").Replace("\r\n", "\n") == "Hello there.\nRead the pins.", set == null ? "nothing sent" : string.Join(",", set.Select(kv => kv.Key + "=" + kv.Value)));

            // caps are enforced by the UI before anything is sent
            Func<string, string, string> trySave = (name, welcome) =>
            {
                SettingsPage.LodgeNameBoxForTest = name; SettingsPage.LodgeWelcomeBoxForTest = welcome;
                sent.Clear();
                SettingsPage.LodgeInfoSaveForTest();
                return sent.Any(f => f.Str("t") == "admin.lodge.set") ? "sent" : SettingsPage.GmStatusForTest;
            };
            var r = trySave(new string('N', 41), "ok");
            check("lodgecard: a name of 41 characters is refused in the UI, nothing sent", r.Contains("at most 40"), r);
            r = trySave("ok", new string('w', 281));
            check("lodgecard: a welcome text of 281 characters is refused in the UI, nothing sent", r.Contains("at most 280"), r);
            r = trySave("ok", "1\n2\n3\n4\n5");
            check("lodgecard: a welcome text of 5 lines is refused in the UI, nothing sent", r.Contains("at most 4 lines"), r);
            r = trySave("ok", "1\r\n2\r\n3\r\n4\r\n5");
            check("lodgecard: ...also with Windows line breaks", r.Contains("at most 4 lines"), r);
            r = trySave(new string('N', 40), new string('w', 280));
            check("lodgecard: exactly 40 and 280 characters are fine", r == "sent", r);
            r = trySave("ok", "1\r\n2\r\n3\r\n4");
            check("lodgecard: exactly 4 lines are fine", r == "sent", r);
            r = trySave("", "");
            var cleared = Inv2Last(sent, "admin.lodge.set");
            check("lodgecard: empty boxes clear the name and the text (sent as empty strings)", r == "sent" && cleared != null && cleared.Str("name") == "" && cleared.Str("welcome") == "");
            check("lodgecard: a refusal clears the old status once a good save is made", SettingsPage.GmStatusForTest == "");

            // the lodge answers: the boxes follow
            Session.FeedForTest("{\"t\":\"lodge.info\",\"lodge\":{\"name\":\"The Platinum Lodge\",\"welcome\":\"Fresh text\"}}");
            await Task.Delay(100);
            check("lodgecard: a lodge.info frame updates the session and the boxes", Session.LodgeName == "The Platinum Lodge" && SettingsPage.LodgeNameBoxForTest == "The Platinum Lodge" && SettingsPage.LodgeWelcomeBoxForTest == "Fresh text");

            // not for officers or members; not on an old server
            Session.FeedForTest(Inv2Welcome("officer", true));
            ShowTab("settings"); SettingsPage.Show("gm");
            check("lodgecard: an officer sees the Guild Master page but NOT 'Your lodge' (owner only)", await Until(() => SettingsPage.NavGmVisibleForTest && !SettingsPage.GmOwnerOnlyVisibleForTest));
            Session.FeedForTest(Inv2Welcome("member", false));
            check("lodgecard: a member has no Guild Master page at all", await Until(() => !SettingsPage.NavGmVisibleForTest && !SettingsPage.GmOwnerOnlyVisibleForTest));
            Session.FeedForTest(Inv2Welcome("owner", true, v212: false));
            ShowTab("settings"); SettingsPage.Show("gm");
            await Task.Delay(200);
            check("lodgecard: on a server older than 2.12 Save is off and the hint names 2.12", !SettingsPage.LodgeInfoSaveEnabledForTest);
            Session.Disconnect();
        }

        // ---------------------------------------------------------------- the join banner (no request before Join)
        async Task Inv2Banner(InvCheck check)
        {
            var s = settings;
            var lodge = LodgePage;
            var reqs = new List<HttpRequestMessage>();
            bool joined = false;
            InviteExchange.Send = req =>
            {
                reqs.Add(req);
                return Task.FromResult(joined
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"token\":" + Q(TokA) + ",\"deviceId\":\"d1\",\"device\":\"Cleo-PC\",\"name\":\"Cleo\",\"role\":\"member\",\"guest\":false}") }
                    : new HttpResponseMessage(HttpStatusCode.NotFound));
            };
            Session.Disconnect();
            LodgeCredentials.Clear(s);
            s.LodgeUrl = null; s.LodgeManualConnect = false;
            lodge.SignOut();
            string Link(string host) => "elanshub://lodge/join#" + host + "/lodge|" + InvA;

            // not signed in: only what the link itself says
            OpenInviteLink(Link("banner.example.test"));
            await Task.Delay(800);
            check("banner: the join screen shows only the address from the link", lodge.IncomingTitleForTest == "You were invited to banner.example.test/lodge", lodge.IncomingTitleForTest);
            check("banner: ...a calm line says the lodge's name appears once you join", (lodge.IncomingTextForTest ?? "").Contains(LodgeView.NameAfterJoinLine) && (lodge.IncomingTextForTest ?? "").Contains("Nothing happens until you do"), lodge.IncomingTextForTest);
            check("banner: ZERO requests of any kind were made by opening the link", reqs.Count == 0, reqs.Count + " requests");
            check("banner: ...and nothing was joined or saved by looking", !Session.HasPendingCredential && !LodgeCredentials.HasToken(s) && !Session.Connected);

            // a web link pasted into the box
            lodge.SignOut();
            lodge.UrlBoxForTest = InviteLinks.MakeWeb("https://paste.example.test/lodge", InvA);
            await Task.Delay(800);
            check("banner: a web link pasted into the box makes no request of any kind", reqs.Count == 0, reqs.Count + " requests");

            // an owner link says 'Your new lodge'
            lodge.ShowInviteLink(Link("owner.example.test"), true);
            await Task.Delay(800);
            check("banner: an owner link says 'Your new lodge: <address>' and still asks nothing", lodge.IncomingTitleForTest == "Your new lodge: owner.example.test/lodge" && reqs.Count == 0, lodge.IncomingTitleForTest);

            // signed in to ANOTHER lodge: the known name belongs to the other lodge and is not shown for this link
            lodge.SignOut();
            LodgeCredentials.StoreToken(s, Inv2Url, TokB, "Cleo-PC");
            s.LodgeUrl = Inv2Url;
            Session.FeedForTest(Inv2Welcome("member", false, "The Silver Lodge", "Welcome in.\nBe kind to each other."));
            OpenInviteLink(Link("elsewhere.example.test"));
            await Task.Delay(800);
            check("banner: a link for another lodge shows its address, never the signed-in lodge's name", lodge.IncomingTitleForTest == "You were invited to elsewhere.example.test/lodge" && !(lodge.IncomingTextForTest ?? "").Contains("Welcome in."), lodge.IncomingTitleForTest);
            check("banner: ...with no request of any kind", reqs.Count == 0, reqs.Count + " requests");

            // signed in to THE SAME lodge: the name and welcome text the app already knows (no request)
            OpenInviteLink("elanshub://lodge/join#example.org/lodge|" + InvA);
            await Task.Delay(800);
            check("banner: already signed in to that lodge -> the known name from the current session", lodge.IncomingTitleForTest == "You were invited to The Silver Lodge", lodge.IncomingTitleForTest);
            check("banner: ...with its welcome text above the usual wording", (lodge.IncomingTextForTest ?? "").StartsWith("Welcome in.\nBe kind to each other.") && (lodge.IncomingTextForTest ?? "").Contains("Joining adds this PC as another device"), lodge.IncomingTextForTest);
            check("banner: ...and still ZERO requests of any kind", reqs.Count == 0, reqs.Count + " requests");
            lodge.CancelEditForTest();
            Session.Disconnect();

            // after Join and the welcome the name comes from the welcome
            LodgeCredentials.Clear(s); s.LodgeUrl = null;
            lodge.SignOut();
            OpenInviteLink(Link("example.org"));
            await Task.Delay(300);
            check("banner: before Join the name is not shown", lodge.IncomingTitleForTest == "You were invited to example.org/lodge" && reqs.Count == 0, lodge.IncomingTitleForTest);
            joined = true;
            lodge.JoinForTest();
            for (int i = 0; i < 20 && !Session.HasPendingCredential; i++) await Task.Delay(100);
            check("banner: pressing Join is what makes the first request", reqs.Count == 1 && reqs[0].Method == HttpMethod.Post, reqs.Count + " requests");
            Session.FeedForTest(Inv2Welcome("member", false, "The Silver Lodge", "Welcome in. Be kind."));
            await Task.Delay(200);
            check("banner: after the welcome frame the lodge name and welcome text are the session's", Session.LodgeName == "The Silver Lodge" && Session.LodgeWelcome == "Welcome in. Be kind.", Session.LodgeName);
            lodge.SignOut();
        }
    }
}
