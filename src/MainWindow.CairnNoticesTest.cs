using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Elans.Shared;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;

namespace ElansAddonHub
{
    // --selftest "cairnnotices" (3.0, Lodge 2.31 feature "cairnnotices"): the Cairn operator's notices on the Lodge page, against synthetic frames
    // signed with a throwaway key pinned for the test (no network; the Lodge side is server/Lodge.Tests/CairnNoticeTests). Covers: an older Lodge
    // asks nothing; cairn.notices.get after the welcome; only correctly signed notices show (a forged text, another key, an expired one, a bad
    // level are dropped); the most urgent wins, coloured by level; closing remembers the id and an officer tells the Lodge (cairn.notice.seen),
    // a member doesn't; a closed notice stays closed after a reconnect; ids the Cairn no longer sends are forgotten. Cairn 1.6.4 (L9): a v2
    // notice for this Lodge's address shows (the saved address differing in case / a trailing '/'), one for another Lodge doesn't, a v1 one for
    // every Lodge shows, a v1 one for one Lodge doesn't, a forged v2 one or a v1 signature passed off as v2 doesn't.
    public partial class MainWindow
    {
        async Task<string> CairnNoticesTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            void Check(string what, bool cond, string detail = null) { SelfTestClock.Mark(what); notes.Add((cond ? "ok   " : "FAIL ") + what + (!cond && detail != null ? " [" + detail + "]" : "")); ok &= cond; }
            var sent = new List<Dictionary<string, object>>();
            bool Sent(string t) { lock (sent) return sent.Any(f => f.Str("t") == t); }
            Dictionary<string, object> Last(string t) { lock (sent) return sent.LastOrDefault(f => f.Str("t") == t); }
            var savedX = CairnAtlas.PinnedKeyX; var savedY = CairnAtlas.PinnedKeyY;
            var savedGone = settings.CairnNoticesDismissed; var savedDismissed = settings.LodgeUpdateDismissed; var savedUrl = settings.LodgeUrl;
            const string Here = "https://lodge.example.test/guild";   // this Lodge's address as the Cairn stored it (normalized)
            string Welcome(string role, bool notices) =>
                FunWelcome(role, FunPeople(false), "", "\"reply\",\"react\",\"profile\",\"displayname\",\"atlas\"" + (notices ? ",\"cairnnotices\"" : ""), Brand.LatestLodgeServer);
            try
            {
                using (var key = Es256.NewKey())
                using (var other = Es256.NewKey())
                {
                    var pub = key.ExportParameters(false);
                    CairnAtlas.PinnedKeyX = B64.Url(pub.Q.X); CairnAtlas.PinnedKeyY = B64.Url(pub.Q.Y);
                    long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    // v = the format the frame claims (0 = no "v", a v1 notice), signV = the one actually signed (default: v)
                    string Notice(long id, string level, string text, long expires = 0, ECDsa signer = null, string signedText = null, int v = 0, string to = "*", int signV = -1)
                    {
                        var n = new LodgeSession.CairnNotice { Id = id, Created = now - 60, Expires = expires != 0 ? expires : now + 86400, Level = level, To = to, Text = signedText ?? text };
                        var msg = (signV < 0 ? v : signV) == 2 ? CairnFormats.NoticeV2(n.Id, n.Created, n.Expires, n.Level, n.To, n.Text) : CairnFormats.Notice(n.Id, n.Created, n.Expires, n.Level, n.To, n.Text);
                        var sig = Convert.ToBase64String(Es256.Sign(signer ?? key, msg));   // what the Cairn signs
                        return "{\"id\":" + id + ",\"created\":" + n.Created + ",\"expires\":" + n.Expires + ",\"level\":" + Q(level) + ",\"to\":" + Q(to) + ",\"text\":" + Q(text)
                            + ",\"sig\":" + Q(sig) + ",\"kid\":\"test\"" + (v != 0 ? ",\"v\":" + v : "") + "}";
                    }
                    string Frame(params string[] list) => "{\"t\":\"cairn.notices\",\"list\":[" + string.Join(",", list) + "]}";

                    Session.Disconnect();
                    Session.SentForTest = o => { lock (sent) sent.Add(o); };
                    settings.CairnNoticesDismissed = null;
                    settings.LodgeUrl = "HTTPS://Lodge.Example.test/guild/";   // the connected Lodge (no socket here: the saved address), not quite in the normalized form
                    settings.LodgeUpdateDismissed = Brand.LatestLodgeServer;
                    TabLodge.IsChecked = true;
                    LodgePage.ShowChatForTest();

                    // ---- an older Lodge: nothing asked, nothing shown
                    Session.FeedForTest(Welcome("owner", false));
                    await Task.Delay(80);
                    Check("an older Lodge (no cairnnotices): nothing is asked and no banner", !Sent("cairn.notices.get") && !LodgePage.CairnNoticeVisibleForTest);
                    Session.Disconnect();

                    // ---- an officer
                    lock (sent) sent.Clear();
                    Session.FeedForTest(Welcome("officer", true));
                    await Task.Delay(80);
                    Check("cairn.notices.get is asked after the welcome", Sent("cairn.notices.get"));
                    Session.FeedForTest(Frame(Notice(1, "info", "Maintenance tonight at 22:00")));
                    await Task.Delay(80);
                    Check("a signed info notice shows, titled From the Cairn with the check mark", LodgePage.CairnNoticeVisibleForTest && LodgePage.CairnNoticeTextForTest == "Maintenance tonight at 22:00"
                        && LodgePage.CairnNoticeTitleForTest == "From the Cairn ✓", LodgePage.CairnNoticeTitleForTest);
                    if (SelfTestClock.ShotsOn) Snapshot(System.IO.Path.Combine(dir, "cairn-notice-info.png"));

                    Session.FeedForTest(Frame(Notice(2, "critical", "Forged", signedText: "Something else"), Notice(3, "critical", "Other key", signer: other),
                        Notice(4, "critical", "Expired", expires: now - 5), Notice(5, "panic", "Bad level"), Notice(1, "info", "Maintenance tonight at 22:00")));
                    await Task.Delay(80);
                    Check("a forged text, another key, an expired notice and a bad level are all dropped", Session.CairnNotices.Count == 1 && LodgePage.CairnNoticeTextForTest == "Maintenance tonight at 22:00",
                        string.Join(",", Session.CairnNotices.Select(n => n.Id)));
                    Check("the Lodge can't change a word: the text is checked, not just the signature's shape",
                        LodgeSession.ParseCairnNotice(LodgeJson.Parse(Notice(9, "info", "Update to 9.9 from evil.example", signedText: "Update to 9.9"))) == null);

                    // ---- L9: a notice names the Lodge it is for
                    Session.FeedForTest(Frame(Notice(1, "info", "Maintenance tonight at 22:00"),
                        Notice(20, "warning", "For this Lodge", v: 2, to: Here),
                        Notice(21, "critical", "For another Lodge", v: 2, to: "https://other.example.test"),
                        Notice(22, "critical", "Old style, for one Lodge", to: "lodge-a"),
                        Notice(23, "info", "New style, for every Lodge", v: 2),
                        Notice(24, "critical", "Forged for this Lodge", v: 2, to: Here, signer: other),
                        Notice(25, "critical", "A v1 signature passed off as v2", v: 2, to: Here, signV: 1),
                        Notice(26, "critical", "A v2 signature passed off as v1", to: "*", signV: 2)));
                    await Task.Delay(80);
                    var ids = string.Join(",", Session.CairnNotices.Select(n => n.Id).OrderBy(x => x));
                    Check("a v2 notice for this Lodge shows (its saved address differs in case and a trailing /)", Session.CairnNotices.Any(n => n.Id == 20 && n.V == 2), ids);
                    Check("a v2 notice the Cairn signed for another Lodge is ignored (no replay)", Session.CairnNotices.All(n => n.Id != 21), ids);
                    Check("a v1 notice for every Lodge and a v2 one for every Lodge show", Session.CairnNotices.Any(n => n.Id == 1 && n.V == 1) && Session.CairnNotices.Any(n => n.Id == 23), ids);
                    Check("a v1 notice for one Lodge is ignored (it can't be tied to this one)", Session.CairnNotices.All(n => n.Id != 22), ids);
                    Check("a forged v2 one, and a signature passed off as the other version, are ignored", ids == "1,20,23", ids);
                    Check("the most urgent shown one wins: the warning for this Lodge", LodgePage.CairnNoticeTextForTest == "For this Lodge", LodgePage.CairnNoticeTextForTest);
                    Check("a v2 notice for this Lodge is ignored where there is no Lodge address, and one signed with a trailing / still matches",
                        LodgeSession.ParseCairnNotice(LodgeJson.Parse(Notice(27, "info", "x", v: 2, to: Here))) == null
                        && LodgeSession.ParseCairnNotice(LodgeJson.Parse(Notice(28, "info", "x", v: 2, to: Here + "/")), Here) != null
                        && LodgeSession.ParseCairnNotice(LodgeJson.Parse(Notice(29, "info", "x", v: 3, to: "*")), Here) == null);

                    Session.FeedForTest(Frame(Notice(1, "info", "Maintenance tonight at 22:00"), Notice(6, "critical", "Security fix - update your Lodge now: sudo lodge-update")));
                    await Task.Delay(80);
                    Check("the critical one wins and is titled Important", LodgePage.CairnNoticeTextForTest.StartsWith("Security fix", StringComparison.Ordinal)
                        && LodgePage.CairnNoticeTitleForTest.StartsWith("Important", StringComparison.Ordinal), LodgePage.CairnNoticeTitleForTest);
                    if (SelfTestClock.ShotsOn) Snapshot(System.IO.Path.Combine(dir, "cairn-notice-critical.png"));

                    LodgePage.CairnNoticeCloseForTest();
                    await Task.Delay(50);
                    var seen = Last("cairn.notice.seen");
                    Check("closing it as an officer tells the Lodge (cairn.notice.seen [6]) and remembers it here",
                        seen != null && seen.List("ids").Count == 1 && Convert.ToInt64(seen.List("ids")[0]) == 6 && settings.CairnNoticesDismissed?.Contains(6) == true);
                    Check("the next one (the info) shows after that", LodgePage.CairnNoticeVisibleForTest && LodgePage.CairnNoticeTextForTest.StartsWith("Maintenance", StringComparison.Ordinal));
                    LodgePage.CairnNoticeCloseForTest();
                    await Task.Delay(50);
                    Check("all closed: no banner", !LodgePage.CairnNoticeVisibleForTest);
                    Session.Disconnect();

                    // ---- a member, after a reconnect: closed stays closed; a member's close is not reported
                    lock (sent) sent.Clear();
                    Session.FeedForTest(Welcome("member", true));
                    await Task.Delay(50);
                    Session.FeedForTest(Frame(Notice(6, "critical", "Security fix - update your Lodge now: sudo lodge-update"), Notice(7, "critical", "Second warning")));
                    await Task.Delay(80);
                    Check("a closed notice stays closed after a reconnect; a new one shows", LodgePage.CairnNoticeVisibleForTest && LodgePage.CairnNoticeTextForTest == "Second warning");
                    LodgePage.CairnNoticeCloseForTest();
                    await Task.Delay(50);
                    Check("a member's close is remembered but not reported", !Sent("cairn.notice.seen") && settings.CairnNoticesDismissed.Contains(7));

                    Session.FeedForTest(Frame(Notice(7, "critical", "Second warning")));
                    await Task.Delay(50);
                    Check("ids the Cairn no longer sends are forgotten", !settings.CairnNoticesDismissed.Contains(1) && !settings.CairnNoticesDismissed.Contains(6) && settings.CairnNoticesDismissed.Contains(7),
                        string.Join(",", settings.CairnNoticesDismissed));
                    Session.FeedForTest(Frame());
                    await Task.Delay(50);
                    Check("withdrawn at the Cairn: no banner", !LodgePage.CairnNoticeVisibleForTest && Session.CairnNotices.Count == 0);
                }
            }
            catch (Exception e) { Check("no exception", false, e.GetType().Name + ": " + e.Message); }
            finally
            {
                Session.Disconnect();
                Session.SentForTest = null;
                CairnAtlas.PinnedKeyX = savedX; CairnAtlas.PinnedKeyY = savedY;
                settings.CairnNoticesDismissed = savedGone; settings.LodgeUpdateDismissed = savedDismissed; settings.LodgeUrl = savedUrl;
            }
            return "== cairnnotices (signed notices from the Cairn on the Lodge page) " + (ok ? "ok" : "FAILED") + "\r\n" + string.Join("\r\n", notes);
        }
    }
}
