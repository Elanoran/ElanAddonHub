using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // --selftest "cairnjoin" (2.90): Settings > Guild Master > The Cairn against synthetic welcome / cairn.join frames (no network; the Lodge side is
    // tested by server/Lodge.Tests/CairnJoinTests). Covers: an older Lodge (no "cairnjoin") and members see nothing and nothing is asked; officers
    // see the status but not the switch; the Guild Master's switch opens the address (prefilled) and, in the beta, the invite code; "Ask to join"
    // sends admin.cairn {on, publicUrl, code} once and forgets the code; the honest text; the one-time suggestion card (Not now / Turn on,
    // remembered); every status line (needs a code, beta full, code expired / invalid / used, waiting place, admitted with probation, good, lapsed,
    // refused, revoked, on through lodge.env).
    public partial class MainWindow
    {
        string CairnJoinWelcome(string role, bool join) =>
            FunWelcome(role, FunPeople(false), "", "\"reply\",\"react\",\"profile\",\"displayname\",\"atlas\"" + (join ? ",\"cairnjoin\"" : ""), "2.28.0");

        static string CairnJoinFrame(string extra) => "{\"t\":\"cairn.join\",\"atlas\":true,\"cairnHost\":\"cairn.test\"" + (extra.Length > 0 ? "," + extra : "") + "}";

        async Task<string> CairnJoinTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            void Check(string what, bool cond, string detail = null) { SelfTestClock.Mark(what); notes.Add((cond ? "ok   " : "FAIL ") + what + (!cond && detail != null ? " [" + detail + "]" : "")); ok &= cond; }
            var sent = new List<Dictionary<string, object>>();
            var savedUrl = settings.LodgeUrl; var savedCard = settings.CairnJoinCardDismissed;
            bool Sent(string t) { lock (sent) return sent.Any(f => f.Str("t") == t); }
            Dictionary<string, object> Last(string t) { lock (sent) return sent.LastOrDefault(f => f.Str("t") == t); }
            var now = DateTime.UtcNow;
            try
            {
                Session.Disconnect();
                Session.SentForTest = o => { lock (sent) sent.Add(o); };
                settings.LodgeUrl = "https://lodge.test/lodge";
                settings.CairnJoinCardDismissed = false;
                if (SelfTestClock.ShotsOn) ShowTab("settings");
                SettingsPage.Show("gm");
                await Task.Delay(50);

                // ---- an older Lodge: nothing
                Session.FeedForTest(CairnJoinWelcome("owner", false));
                await Task.Delay(80);
                Check("an older Lodge (no cairnjoin): the Cairn section is hidden and nothing is asked", !SettingsPage.CairnJoinVisibleForTest && !Sent("cairn.join.get"));
                Session.Disconnect();

                // ---- a member: nothing
                lock (sent) sent.Clear();
                Session.FeedForTest(CairnJoinWelcome("member", true));
                await Task.Delay(80);
                Check("a member: no Cairn section, nothing asked, and the switch sends nothing", !SettingsPage.CairnJoinVisibleForTest && !Sent("cairn.join.get") && !Session.CanSwitchCairn);
                Session.SetCairnJoin(true, "https://x.test/lodge");
                Check("a member can't send admin.cairn", !Sent("admin.cairn"));
                Session.Disconnect();

                // ---- an officer: the status, not the switch
                lock (sent) sent.Clear();
                Session.FeedForTest(CairnJoinWelcome("officer", true));
                await Task.Delay(50);
                Session.FeedForTest(CairnJoinFrame("\"on\":true,\"state\":\"waiting\",\"place\":3,\"of\":7,\"mode\":\"open\",\"publicUrl\":\"https://lodge.test/lodge\""));
                await Task.Delay(80);
                Check("an officer: asks cairn.join.get, sees the section and the status, the switch is disabled, no form, no card",
                    Sent("cairn.join.get") && SettingsPage.CairnJoinVisibleForTest && !SettingsPage.CairnJoinEnabledForTest && !SettingsPage.CairnJoinFormVisibleForTest
                    && !SettingsPage.CairnJoinCardVisibleForTest && SettingsPage.CairnJoinStatusForTest.StartsWith("Waiting: place 3 of 7", StringComparison.Ordinal), SettingsPage.CairnJoinStatusForTest);
                Session.Disconnect();

                // ---- the Guild Master, switched off: the honest text and the one-time card
                lock (sent) sent.Clear();
                Session.FeedForTest(CairnJoinWelcome("owner", true));
                await Task.Delay(50);
                Check("the Guild Master: cairn.join.get is asked after the welcome", Sent("cairn.join.get"));
                Session.FeedForTest(CairnJoinFrame("\"on\":false,\"state\":\"\",\"mode\":\"\",\"suggestedUrl\":\"\""));
                await Task.Delay(80);
                var text = SettingsPage.CairnJoinTextForTest;
                Check("the switch is there, off, and enabled for the Guild Master", SettingsPage.CairnJoinVisibleForTest && !SettingsPage.CairnJoinOnForTest && SettingsPage.CairnJoinEnabledForTest);
                Check("honest text: pooled totals of members who share, ids and counts only, no names, give to get, the public address goes to the Cairn, members' own switch unchanged",
                    text.Contains("pooled totals of the members who share") && text.Contains("ids and counts only") && text.Contains("never names") && text.Contains("Give to get")
                    && text.Contains("public address") && text.Contains("own sharing switch"), text);
                Check("off: the status says nothing is shared", SettingsPage.CairnJoinStatusForTest.StartsWith("Off.", StringComparison.Ordinal), SettingsPage.CairnJoinStatusForTest);
                Check("the one-time suggestion card shows while it is off", SettingsPage.CairnJoinCardVisibleForTest);
                if (SelfTestClock.ShotsOn) { SettingsPage.CairnJoinIntoViewForTest(); await ShotWait(300); Snapshot(Path.Combine(dir, "cairnjoin-card.png")); }
                SettingsPage.CairnCardLaterForTest();
                Check("Not now: the card goes and stays gone (remembered on this PC)", !SettingsPage.CairnJoinCardVisibleForTest && settings.CairnJoinCardDismissed);
                SettingsPage.FillCairnJoinForTest();
                Check("... also after the page is filled again", !SettingsPage.CairnJoinCardVisibleForTest);
                settings.CairnJoinCardDismissed = false;
                SettingsPage.FillCairnJoinForTest();
                SettingsPage.CairnCardOnForTest();
                Check("Turn on: the card goes, the form opens with the address this Outpost uses",
                    !SettingsPage.CairnJoinCardVisibleForTest && SettingsPage.CairnJoinFormVisibleForTest && SettingsPage.CairnUrlForTest == "https://lodge.test/lodge", SettingsPage.CairnUrlForTest);
                Check("nothing is sent before \"Ask to join\"", !Sent("admin.cairn"));

                // ---- the beta: the Lodge says a code is needed
                Session.FeedForTest(CairnJoinFrame("\"on\":true,\"state\":\"error\",\"reason\":\"invite-needed\",\"mode\":\"beta\",\"publicUrl\":\"https://lodge.test/lodge\""));
                await Task.Delay(80);
                Check("beta: \"needs an invite code\" and the code field shows",
                    SettingsPage.CairnJoinStatusForTest.Contains("needs an invite code") && SettingsPage.CairnCodeVisibleForTest && SettingsPage.CairnJoinFormVisibleForTest, SettingsPage.CairnJoinStatusForTest);
                if (SelfTestClock.ShotsOn) { SettingsPage.CairnJoinIntoViewForTest(); await ShotWait(300); Snapshot(Path.Combine(dir, "cairnjoin-beta.png")); }
                SettingsPage.CairnUrlForTest = "https://lodge.test/lodge";
                SettingsPage.CairnCodeForTest(" CAIRN-ABCD-EFGH ");
                SettingsPage.CairnSaveForTest();
                var a = Last("admin.cairn");
                Check("Ask to join: admin.cairn {on, publicUrl, code} goes to the Lodge once, and the code box is emptied",
                    a != null && a.Bool("on") && a.Str("publicUrl") == "https://lodge.test/lodge" && a.Str("code") == "CAIRN-ABCD-EFGH");
                bool HttpRefused()
                {
                    int n; lock (sent) n = sent.Count(f => f.Str("t") == "admin.cairn");
                    SettingsPage.CairnSwitchForTest(true);
                    SettingsPage.CairnUrlForTest = "http://lodge.test/lodge";
                    SettingsPage.CairnSaveForTest();
                    int m; lock (sent) m = sent.Count(f => f.Str("t") == "admin.cairn");
                    return m == n && SettingsPage.CairnJoinStatusForTest.Contains("https://");
                }
                Check("an address without https is refused in the Outpost", HttpRefused());

                // ---- every status line
                async Task<string> S(string extra) { Session.FeedForTest(CairnJoinFrame(extra)); await Task.Delay(40); return CairnJoinText.Status(Session.CairnJoin, now); }
                Check("beta full: says so and keeps the code", (await S("\"on\":true,\"state\":\"error\",\"reason\":\"beta-full\",\"mode\":\"beta\",\"hasCode\":true")).Contains("beta is full") && Session.CairnJoin.HasCode);
                Check("code expired", (await S("\"on\":true,\"state\":\"error\",\"reason\":\"code-expired\",\"mode\":\"beta\"")).Contains("expired"));
                Check("code invalid", (await S("\"on\":true,\"state\":\"error\",\"reason\":\"code-invalid\",\"mode\":\"beta\"")).Contains("not valid"));
                Check("code used", (await S("\"on\":true,\"state\":\"error\",\"reason\":\"code-used\",\"mode\":\"beta\"")).Contains("already used"));
                Check("not verified: the Cairn's reason is shown", (await S("\"on\":true,\"state\":\"error\",\"reason\":\"unverified\",\"error\":\"the Lodge answered HTTP 404\"")).Contains("HTTP 404"));
                var w = await S("\"on\":true,\"state\":\"waiting\",\"place\":2,\"of\":5,\"estDay\":\"2026-10-19\",\"mode\":\"open\"");
                Check("waiting: the place and the expected day", w.StartsWith("Waiting: place 2 of 5", StringComparison.Ordinal) && w.Contains("19 October"), w);
                long pu = new DateTimeOffset(now.AddDays(5)).ToUnixTimeSeconds();
                var p = await S("\"on\":true,\"state\":\"admitted\",\"standing\":\"good\",\"settled\":false,\"probationUntil\":" + pu + ",\"lastPost\":" + new DateTimeOffset(now.AddHours(-3)).ToUnixTimeSeconds());
                Check("admitted on probation: good standing, last post, probation until a date", p.StartsWith("Admitted.", StringComparison.Ordinal) && p.Contains("good standing") && p.Contains("3 hours ago") && p.Contains("probation until"), p);
                var g = await S("\"on\":true,\"state\":\"admitted\",\"standing\":\"good\",\"settled\":true,\"lastPost\":" + new DateTimeOffset(now.AddMinutes(-30)).ToUnixTimeSeconds());
                Check("admitted and settled: good standing, no probation", g.Contains("good standing") && !g.Contains("probation"), g);
                Check("admitted with the form closed (nothing to ask any more)", !SettingsPage.CairnJoinFormVisibleForTest && SettingsPage.CairnJoinOnForTest);
                if (SelfTestClock.ShotsOn) { SettingsPage.CairnJoinIntoViewForTest(); await ShotWait(300); Snapshot(Path.Combine(dir, "cairnjoin-admitted.png")); }
                Check("lapsed: contribute to receive again", (await S("\"on\":true,\"state\":\"admitted\",\"standing\":\"lapsed\",\"settled\":true,\"lastPost\":1")).Contains("Lapsed"));
                Check("refused", (await S("\"on\":true,\"state\":\"refused\"")).Contains("didn't admit"));
                Check("revoked", (await S("\"on\":true,\"state\":\"revoked\"")).Contains("revoked"));
                var e = await S("\"on\":true,\"env\":true,\"state\":\"admitted\",\"standing\":\"good\",\"settled\":true");
                Check("on through lodge.env: says so, the switch is locked, no card, no form",
                    e.Contains("lodge.env") && !SettingsPage.CairnJoinEnabledForTest && !SettingsPage.CairnJoinCardVisibleForTest && !SettingsPage.CairnJoinFormVisibleForTest, e);
                Check("the Atlas off on the server: said so", (await S("\"on\":false,\"atlas\":false")).Contains("Atlas is switched off"));

                // ---- switching off sends {on:false}
                await S("\"on\":true,\"state\":\"admitted\",\"standing\":\"good\",\"settled\":true");
                SettingsPage.CairnSwitchForTest(false);
                var off = Last("admin.cairn");
                Check("switching off sends admin.cairn {on: false} and nothing else", off != null && !off.Bool("on") && off.Str("code") == null && off.Str("publicUrl") == null);
                SettingsPage.CairnSwitchForTest(true);
                var back = Last("admin.cairn");
                Check("switching an admitted Lodge back on resumes at once (no form)", back != null && back.Bool("on") && back.Str("code") == null);
            }
            catch (Exception ex) { Check("cairnjoin test ran without an exception", false, ex.ToString()); }
            finally
            {
                Session.Disconnect();
                Session.SentForTest = null;
                settings.LodgeUrl = savedUrl; settings.CairnJoinCardDismissed = savedCard;
                SettingsPage.Show("general");
            }
            return "== cairnjoin (the Guild Master's \"Join the Cairn\") " + (ok ? "ok" : "FAILED") + "\r\n" + string.Join("\r\n", notes);
        }
    }
}
