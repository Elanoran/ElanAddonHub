using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;

namespace ElansAddonHub
{
    // --selftest "cairnnotices" (3.0, Lodge 2.31 feature "cairnnotices"): the Cairn operator's notices on the Lodge page, against synthetic frames
    // signed with a throwaway key pinned for the test (no network; the Lodge side is server/Lodge.Tests/CairnNoticeTests). Covers: an older Lodge
    // asks nothing; cairn.notices.get after the welcome; only correctly signed notices show (a forged text, another key, an expired one, a bad
    // level are dropped); the most urgent wins, coloured by level; closing remembers the id and an officer tells the Lodge (cairn.notice.seen),
    // a member doesn't; a closed notice stays closed after a reconnect; ids the Cairn no longer sends are forgotten.
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
            var savedGone = settings.CairnNoticesDismissed; var savedDismissed = settings.LodgeUpdateDismissed;
            string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            string Welcome(string role, bool notices) =>
                FunWelcome(role, FunPeople(false), "", "\"reply\",\"react\",\"profile\",\"displayname\",\"atlas\"" + (notices ? ",\"cairnnotices\"" : ""), Brand.LatestLodgeServer);
            try
            {
                using (var key = ECDsa.Create(ECCurve.NamedCurves.nistP256))
                using (var other = ECDsa.Create(ECCurve.NamedCurves.nistP256))
                {
                    var pub = key.ExportParameters(false);
                    CairnAtlas.PinnedKeyX = B64Url(pub.Q.X); CairnAtlas.PinnedKeyY = B64Url(pub.Q.Y);
                    long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    string Notice(long id, string level, string text, long expires = 0, ECDsa signer = null, string signedText = null)
                    {
                        var n = new LodgeSession.CairnNotice { Id = id, Created = now - 60, Expires = expires != 0 ? expires : now + 86400, Level = level, To = "*", Text = signedText ?? text };
                        var sig = Convert.ToBase64String((signer ?? key).SignData(System.Text.Encoding.UTF8.GetBytes(LodgeSession.NoticeMessage(n)), HashAlgorithmName.SHA256));
                        return "{\"id\":" + id + ",\"created\":" + n.Created + ",\"expires\":" + n.Expires + ",\"level\":" + Q(level) + ",\"to\":\"*\",\"text\":" + Q(text) + ",\"sig\":" + Q(sig) + ",\"kid\":\"test\"}";
                    }
                    string Frame(params string[] list) => "{\"t\":\"cairn.notices\",\"list\":[" + string.Join(",", list) + "]}";

                    Session.Disconnect();
                    Session.SentForTest = o => { lock (sent) sent.Add(o); };
                    settings.CairnNoticesDismissed = null;
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
                        LodgeSession.ParseCairnNotice(Json.Obj(Notice(9, "info", "Update to 9.9 from evil.example", signedText: "Update to 9.9"))) == null);

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
                settings.CairnNoticesDismissed = savedGone; settings.LodgeUpdateDismissed = savedDismissed;
            }
            return "== cairnnotices (signed notices from the Cairn on the Lodge page) " + (ok ? "ok" : "FAILED") + "\r\n" + string.Join("\r\n", notes);
        }
    }
}
