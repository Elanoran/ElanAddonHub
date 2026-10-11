using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // --selftest "lodgehardening": the client side of the Lodge 2.8 hardening, from synthetic frames (no server)
    public partial class MainWindow
    {
        async Task<string> HardeningTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            void Check(string what, bool cond) { notes.Add((cond ? "ok   " : "FAIL ") + what); ok &= cond; }
            var s = settings;
            var hadNotify = s.NotifyMode; var hadHint = s.DisplayNameHintShown;
            try
            {
                // ---- file names and the download path
                Check("name: traversal and drive letters lose their path", SafeFile.Name("..\\..\\Startup\\x.bat") == "x.bat" && SafeFile.Name("C:\\Users\\a\\evil.exe") == "evil.exe" && SafeFile.Name("../../etc/passwd") == "passwd");
                Check("name: Windows-invalid characters become _", SafeFile.Name("a|b<c>d\"e:f*g?h.txt") == "a_b_c_d_e_f_g_h.txt");
                Check("name: bidi override and zero width are gone", SafeFile.Name("photo\u202Egnp.exe") == "photognp.exe" && SafeFile.Name("a\u200Bb.txt") == "ab.txt");
                Check("name: device names, leading dots and spaces", SafeFile.Name("NUL.txt") == "_NUL.txt" && SafeFile.Name("COM1") == "_COM1" && SafeFile.Name("  ..x.txt ") == "x.txt" && SafeFile.Name("") == "file");
                var dl = Path.Combine(dir, "downloads-test");
                Directory.CreateDirectory(dl);
                var evil = new FileVM { Id = "abc", Name = SafeFile.Name("..\\..\\Startup\\x|y.bat"), Size = 1 };
                var target = LodgeSession.DownloadTarget(dl, evil);
                Check("download path stays inside the folder with the clean name", target != null && Path.GetDirectoryName(target) == Path.GetFullPath(dl) && Path.GetFileName(target) == "x_y.bat");
                File.WriteAllText(target, "x");
                var again = LodgeSession.DownloadTarget(dl, evil);
                Check("download path: an existing name gets (2), still inside", again != null && Path.GetFileName(again) == "x_y (2).bat");
                Check("PathIn refuses names that would climb out", SafeFile.PathIn(dl, "..") == null || Path.GetDirectoryName(SafeFile.PathIn(dl, "..")) == Path.GetFullPath(dl));
                Check("cache id: only letters and digits", SafeFile.Id("0a1B2c") != null && SafeFile.Id("..\\x") == null && SafeFile.Id("a/b") == null);
                Check("risky extensions are flagged with the real extension", evil.IsRisky && evil.ExtText == "BAT" && new FileVM { Name = "notes.txt" }.IsRisky == false
                    && SafeFile.IsRisky("x.PS1") && SafeFile.IsRisky("a.lnk") && !SafeFile.IsRisky("pic.png"));
                Check("chat text: bidi overrides stripped, blank-line floods collapse", SafeFile.Text("a\u202Eb\u2066c") == "abc" && SafeFile.Text("a" + new string('\n', 30) + "b") == "a\n\n\n\nb");

                // ---- cache housekeeping
                var cache = Path.Combine(dir, "cache-test"); Directory.CreateDirectory(cache);
                var oldF = Path.Combine(cache, "old.png"); File.WriteAllText(oldF, "x"); File.SetLastWriteTimeUtc(oldF, DateTime.UtcNow.AddDays(-40));
                var newF = Path.Combine(cache, "new.png"); File.WriteAllText(newF, "x");
                var big1 = Path.Combine(cache, "b1.bin"); File.WriteAllBytes(big1, new byte[2000]); File.SetLastWriteTimeUtc(big1, DateTime.UtcNow.AddDays(-2));
                var big2 = Path.Combine(cache, "b2.bin"); File.WriteAllBytes(big2, new byte[2000]);
                SafeFile.PruneCache(cache, TimeSpan.FromDays(30), 2500, DateTime.UtcNow);
                Check("cache: files older than 30 days go, then the oldest until it fits the cap", !File.Exists(oldF) && !File.Exists(big1) && File.Exists(big2));
                var paste = Path.Combine(dir, "paste-temp"); Directory.CreateDirectory(Path.Combine(paste, "elanshub-paste-1234abcd"));
                SafeFile.DeletePasteLeftovers(paste);
                Check("leftover elanshub-paste-* folders are deleted", Directory.GetDirectories(paste).Length == 0);

                // ---- the exact WoW executables
                Check("overlay: Wow.exe and friends are WoW, WowUp is not", OverlayWindow.IsWowProcess("Wow") && OverlayWindow.IsWowProcess("WowClassic") && OverlayWindow.IsWowProcess("WowT")
                    && OverlayWindow.IsWowProcess("WowB") && !OverlayWindow.IsWowProcess("WowUp") && !OverlayWindow.IsWowProcess("WowError"));
                Check("status codes: 401/429 come from the response, not from message text", LodgeClient.StatusOf(new Exception("x", new System.Net.WebSockets.WebSocketException("The server returned status code '401' when status code '101' was expected."))) == 401
                    && LodgeClient.StatusOf(new Exception("nothing here")) == 0 && LodgeClient.IsFullClose("The lodge is full") && !LodgeClient.IsFullClose("Kicked by Elan"));

                // ---- a welcome, then a second welcome that must MERGE
                Session.Disconnect();
                s.NotifyMode = "mentions"; s.DisplayNameHintShown = false;
                TabLodge.IsChecked = true;
                LodgePage.ShowChatForTest();
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                string Msg(string id, string from, string text, int minsAgo, string extra = "") =>
                    "{\"t\":\"msg\",\"channel\":\"general\",\"id\":" + Q(id) + ",\"at\":" + (now - minsAgo * 60000L) + ",\"from\":" + Q(from) + ",\"fromId\":" + (from == "Elan" ? 1 : 2) + ",\"text\":" + Q(text) + extra + "}";
                string Welcome(string history, string extraUsers = "", string names = "\"names\":{\"Zed\":\"Zedd\"},") =>
                    "{\"t\":\"welcome\",\"you\":1,\"name\":\"Elan\",\"role\":\"owner\",\"server\":\"2.8.0\"," + names + "\"maxFileMb\":25,\"canShareFiles\":true,\"canModerate\":true,\"canManage\":true,\"canPin\":true,"
                    + "\"features\":[\"reply\",\"react\",\"react2\",\"pin\",\"profile\",\"displayname\"],\"reactions\":[\"ready\",\"notready\",\"lol\",\"love\",\"fight\",\"loot\",\"wipe\",\"epic\"],"
                    + "\"channels\":[{\"id\":\"general\",\"name\":\"General\",\"type\":\"text\",\"minRole\":\"guest\"},{\"id\":\"loot\",\"name\":\"Loot\",\"type\":\"text\",\"minRole\":\"guest\"},"
                    + "{\"id\":\"dungeon\",\"name\":\"Dungeon group\",\"type\":\"voice\",\"minRole\":\"guest\",\"max\":5}],"
                    + "\"users\":[{\"id\":1,\"name\":\"Elan\",\"role\":\"owner\"},{\"id\":2,\"name\":\"Bob\",\"role\":\"officer\"},{\"id\":3,\"name\":\"Tess\",\"role\":\"member\"}" + extraUsers + "],"
                    + "\"history\":{\"general\":[" + history + "],\"loot\":[]},\"pins\":{}}";
                var h1 = string.Join(",",
                    Msg("a1", "Bob", "first from Bob", 60), Msg("a2", "Bob", "second from Bob", 59), Msg("a3", "Tess", "hello there", 58),
                    Msg("a4", "Zed", "I left a while ago", 50));
                var hadShown = Session.IsShownToUser; Session.IsShownToUser = () => false;   // not looking: new messages count as unread
                Session.SetReadMarkForTest("general", "a1", now - 60 * 60000L);
                Session.FeedForTest(Welcome(h1));
                var general = Session.TextChannels.First(c => c.Id == "general");
                Session.Select(general);
                var a2 = Session.MessagesOf("general").First(m => m.Id == "a2");
                Check("welcome: names directory -> an offline author shows the display name", Session.MessagesOf("general").First(m => m.Id == "a4").FromShown == "Zedd");
                Check("welcome: a display-name hint is offered once", s.DisplayNameHintShown);
                Check("welcome: follow-up from the same person has no header", a2.Continuation && a2.HeaderVisibility == Visibility.Collapsed);

                // a deleted header message: the follow-up gets its header back
                Session.FeedForTest("{\"t\":\"deleted\",\"channel\":\"general\",\"id\":\"a1\"}");
                var a2b = Session.MessagesOf("general").First(m => m.Id == "a2");
                Check("delete of a header message gives the follow-up its header back", !a2b.Continuation && a2b.HeaderVisibility == Visibility.Visible);

                // unread numbers: a2, a3, a4 are unread; a mention arrives
                Session.FeedForTest(Msg("a5", "Bob", "@Elan look at this", 1));
                Check("unread: 4 unread, 1 mention", general.UnreadCount == 4 && general.Mentions == 1);
                Session.FeedForTest("{\"t\":\"deleted\",\"channel\":\"general\",\"id\":\"a5\"}");
                Check("delete of an unread mention lowers unread and mentions", general.UnreadCount == 3 && general.Mentions == 0);
                var firstUnread = general.FirstUnreadId;
                Session.FeedForTest("{\"t\":\"deleted\",\"channel\":\"general\",\"id\":\"" + firstUnread + "\"}");
                Check("delete of the first unread moves the 'New messages' line on", general.FirstUnreadId != firstUnread && general.FirstUnreadId == "a3" && general.UnreadCount == 2);

                // an edit that adds a mention to an unread message badges the channel
                Session.FeedForTest("{\"t\":\"edited\",\"channel\":\"general\",\"id\":\"a3\",\"text\":\"hey @Elan\"}");
                Check("an edit that adds a mention to an unread message badges the channel", general.Mentions == 1 && Session.MessagesOf("general").First(m => m.Id == "a3").Mentioned);

                Session.IsShownToUser = hadShown;

                // second welcome: same objects, newcomers inserted, deleted removed, state kept
                LodgePage.ComposerTextForTest = "typing in progress";
                var idBefore = Session.MessagesOf("general").First(m => m.Id == "a3");
                var listObj = Session.MessagesOf("general");
                var h2 = string.Join(",", Msg("a3", "Tess", "hey @Elan", 58), Msg("a6", "Tess", "brand new", 2));
                Session.FeedForTest(Welcome(h2));
                var idAfter = Session.MessagesOf("general").First(m => m.Id == "a3");
                Check("resync: the channel object and the message objects survive", Session.TextChannels.First(c => c.Id == "general") == general && ReferenceEquals(idBefore, idAfter) && ReferenceEquals(listObj, Session.MessagesOf("general")));
                Check("resync: a new message is merged in, the deleted one (a4) is gone", Session.MessagesOf("general").Any(m => m.Id == "a6") && Session.MessagesOf("general").All(m => m.Id != "a4"));
                Check("resync: selected channel and the text being typed are kept", Session.Selected == general && LodgePage.ComposerTextForTest == "typing in progress");

                // ---- drafts per channel
                var loot = Session.TextChannels.First(c => c.Id == "loot");
                Session.Select(loot);
                Check("drafts: the text of the channel I left is saved, the new channel starts empty", LodgePage.DraftForTest("general") == "typing in progress" && LodgePage.ComposerTextForTest == "");
                LodgePage.ComposerTextForTest = "loot draft";
                Session.Select(general);
                Check("drafts: coming back restores it", LodgePage.ComposerTextForTest == "typing in progress" && LodgePage.DraftForTest("loot") == "loot draft");
                LodgePage.ComposerTextForTest = "";

                // ---- the composer keeps what did not go out
                LodgePage.ComposerTextForTest = "this must not be lost";
                LodgePage.SendTextForTest();
                Check("send: the text leaves the box when the send started", LodgePage.ComposerTextForTest == "" && Session.Pending != null);
                Session.FeedForTest("{\"t\":\"error\",\"text\":\"Slow down a little\",\"code\":\"rate\"}");
                Check("rate limit: the text comes back and the reason is shown", LodgePage.ComposerTextForTest == "this must not be lost" && Session.Pending == null);
                LodgePage.ComposerTextForTest = "second try";
                LodgePage.SendTextForTest();
                Session.FeedForTest(Msg("e1", "Elan", "second try", 0));
                Check("the echo of my message clears the pending send", Session.Pending == null && LodgePage.ComposerTextForTest == "");
                LodgePage.ComposerTextForTest = "old server wording";
                LodgePage.SendTextForTest();
                Session.FeedForTest("{\"t\":\"error\",\"text\":\"Slow down a little\"}");
                Check("2.7 server (no error code): still restored", LodgePage.ComposerTextForTest == "old server wording");
                LodgePage.ComposerTextForTest = "";
                Check("composer is capped at 2000 characters", LodgePage.ComposerMaxLengthForTest == 2000);

                // ---- a full voice room
                Session.JoinRoom("dungeon");
                Check("voice: asking for a room does not enter it", Session.MyRoom == null && Session.PendingRoom == "dungeon" && Session.Voice == null);
                Session.FeedForTest("{\"t\":\"error\",\"text\":\"Dungeon group is full (5)\",\"code\":\"room-full\",\"room\":\"dungeon\"}");
                Check("voice: a full room leaves nothing connected and no microphone open", Session.MyRoom == null && Session.PendingRoom == null && Session.Voice == null);
                Session.JoinRoom("dungeon");
                Session.FeedForTest("{\"t\":\"error\",\"text\":\"Dungeon group is full (5)\"}");   // a 2.7 server has no code
                Check("voice: same with the 2.7 wording", Session.MyRoom == null && Session.PendingRoom == null);

                // ---- notifications
                s.NotifyMode = "none";
                Check("notify 'Nothing' silences the toasts too", !Session.Toasts.Enabled(ToastKind.Mention) && !Session.Toasts.Enabled(ToastKind.Reply));
                s.NotifyMode = "mentions";
                Check("notify 'mentions': toasts follow their own switches", Session.Toasts.Enabled(ToastKind.Mention));

                // ---- mentions: who may ping everyone, and the left word boundary
                Session.FeedForTest(Msg("p1", "Tess", "@everyone party", 0));
                Session.FeedForTest(Msg("p2", "Bob", "@everyone raid now", 0));
                Session.FeedForTest(Msg("p3", "Bob", "mail foo@Elan.example", 0));
                Session.FeedForTest(Msg("p4", "Bob", "(@Elan) are you there", 0));
                var ml = Session.MessagesOf("general");
                Check("@everyone from a member does not ping, from an officer it does", !ml.First(m => m.Id == "p1").Mentioned && ml.First(m => m.Id == "p2").Mentioned);
                Check("foo@Elan does not ping, (@Elan) does", !ml.First(m => m.Id == "p3").Mentioned && ml.First(m => m.Id == "p4").Mentioned);

                Check("online list: one number (the status line)", Session.StatusText.Contains("online"));
            }
            catch (Exception e) { ok = false; notes.Add("crashed: " + e); }
            finally
            {
                s.NotifyMode = hadNotify; s.DisplayNameHintShown = hadHint;
                SettingsStore.Save(s);
                Session.Disconnect();
            }
            await Task.CompletedTask;
            return "lodge hardening selftest: " + (ok ? "PASSED" : "FAILED") + "\r\n  " + string.Join("\r\n  ", notes);
        }
    }
}
