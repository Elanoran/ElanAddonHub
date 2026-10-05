using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    public partial class MainWindow
    {
        async Task<string> ToastTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            void Check(string what, bool cond) { notes.Add((cond ? "ok   " : "FAIL ") + what); ok &= cond; }
            var s = settings;
            bool o1 = s.ToastsOff, o2 = s.ToastReactions, o3 = s.ToastPins, o4 = s.ToastMentionsOff; var corner = s.ToastCorner;
            try
            {
                s.ToastsOff = false; s.ToastReactions = false; s.ToastPins = false; s.ToastMentionsOff = false;
                // ---- the pure rules, with a fake clock
                var t0 = new DateTime(2026, 10, 4, 20, 0, 0, DateTimeKind.Utc);
                var now = t0; bool combat = false; string looking = null;
                var tc = new ToastCenter(s) { Clock = () => now, InCombat = () => combat, ChannelOnScreen = c => c == looking };
                ToastItem Mention(string from, string ch = "general") => new ToastItem { Kind = ToastKind.Mention, Sender = from, Channel = ch, ChannelId = ch, Text = "@Elan are you there?" };
                tc.Push(Mention("Bob"));
                Check("a mention shows a toast", tc.Visible.Count == 1 && tc.Visible[0].Title == "Bob · #general");
                now = t0.AddSeconds(1); tc.Tick();
                Check("fades in", tc.Visible[0].Opacity > 0.9);
                now = t0.AddSeconds(5.8); tc.Tick();
                Check("fades out before it goes", tc.Visible[0].Opacity < 0.6 && tc.Visible.Count == 1);
                now = t0.AddSeconds(6.1); tc.Tick();
                Check("stays about 6 s", tc.Visible.Count == 0);
                now = t0.AddSeconds(10);
                tc.Push(Mention("Bob")); tc.Push(Mention("Tess")); tc.Push(Mention("Dax"));
                Check("a burst in one channel is coalesced: '3 new mentions in #general'", tc.Visible.Count == 1 && tc.Visible[0].Title == "3 new mentions in #general");
                tc.Push(Mention("Kor", "loot")); tc.Push(new ToastItem { Kind = ToastKind.Reply, Sender = "Zed", Channel = "general", ChannelId = "general", Text = "good point" });
                tc.Push(new ToastItem { Kind = ToastKind.Update, Sender = "Hub 2.18.0 is ready", Text = "Open the Hub to update." });
                Check("at most 3 on screen (oldest dropped)", tc.Visible.Count == 3 && tc.Visible.All(v => v.Title != "3 new mentions in #general"));
                now = t0.AddSeconds(30); tc.Tick();
                // reactions / pins are off by default
                tc.Push(new ToastItem { Kind = ToastKind.Reaction, Sender = "Bob", Channel = "general", ChannelId = "general", Text = "reacted" });
                tc.Push(new ToastItem { Kind = ToastKind.Pin, Sender = "Bob", Channel = "general", ChannelId = "general", Text = "pinned" });
                Check("reactions and pins: off by default", tc.Visible.Count == 0);
                s.ToastReactions = true;
                tc.Push(new ToastItem { Kind = ToastKind.Reaction, Sender = "Bob", Channel = "general", ChannelId = "general", Text = "reacted" });
                Check("... on when switched on", tc.Visible.Count == 1);
                s.ToastReactions = false; now = t0.AddSeconds(40); tc.Tick();
                // DND: combat
                combat = true;
                tc.Push(Mention("Bob")); tc.Push(Mention("Tess")); tc.Push(new ToastItem { Kind = ToastKind.Reply, Sender = "Zed", Channel = "loot", ChannelId = "loot", Text = "ok" });
                Check("in combat: nothing is shown, three are queued", tc.Visible.Count == 0 && tc.HeldCount == 3);
                now = t0.AddSeconds(50); tc.Tick();
                Check("... still nothing while the fight goes on", tc.Visible.Count == 0);
                combat = false; now = t0.AddSeconds(51); tc.Tick();
                Check("after combat: ONE summary toast", tc.Visible.Count == 1 && tc.Visible[0].Kind == ToastKind.Summary && tc.Visible[0].Text.Contains("2 mentions") && tc.Visible[0].Text.Contains("1 reply") && tc.HeldCount == 0);
                // DND: the Lodge shows that channel
                now = t0.AddSeconds(70); tc.Tick(); looking = "general";
                tc.Push(Mention("Bob"));
                Check("Lodge focused on that channel: no toast", tc.Visible.Count == 0);
                tc.Push(Mention("Bob", "loot"));
                Check("... but another channel still toasts", tc.Visible.Count == 1);
                looking = null; now = t0.AddSeconds(90); tc.Tick();
                s.ToastsOff = true; tc.Push(Mention("Bob"));
                Check("global switch off: nothing", tc.Visible.Count == 0 && tc.HeldCount == 0);
                s.ToastsOff = false; s.ToastMentionsOff = true; tc.Push(Mention("Bob"));
                Check("per-type switch off: nothing", tc.Visible.Count == 0);
                s.ToastMentionsOff = false;

                // ---- through the session: real protocol frames
                Session.Disconnect();
                TabLodge.IsChecked = true;
                LodgePage.ShowChatForTest();
                var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                Session.FeedForTest("{\"t\":\"welcome\",\"you\":1,\"name\":\"Elan\",\"role\":\"owner\",\"server\":\"2.5.0\",\"canPin\":true,\"features\":[\"reply\",\"react\",\"react2\",\"pin\"],"
                    + "\"reactions\":[\"ready\",\"notready\",\"lol\",\"love\",\"fight\",\"loot\",\"wipe\",\"epic\"],"
                    + "\"channels\":[{\"id\":\"general\",\"name\":\"General\",\"type\":\"text\"},{\"id\":\"loot\",\"name\":\"Loot\",\"type\":\"text\"}],"
                    + "\"users\":[{\"id\":1,\"name\":\"Elan\",\"role\":\"owner\"},{\"id\":2,\"name\":\"Bob\",\"role\":\"officer\",\"game\":{\"playing\":true,\"name\":\"Bob\",\"class\":\"Paladin\",\"classFile\":\"PALADIN\",\"level\":25}},"
                    + "{\"id\":3,\"name\":\"Tess\",\"role\":\"member\",\"game\":{\"playing\":true,\"name\":\"Tess\",\"class\":\"Mage\",\"classFile\":\"MAGE\",\"level\":33}}],"
                    + "\"history\":{\"general\":[{\"t\":\"msg\",\"channel\":\"general\",\"id\":\"mine1\",\"at\":" + (nowMs - 60000) + ",\"from\":\"Elan\",\"fromId\":1,\"text\":\"Who wants Wailing Caverns?\"}],\"loot\":[]},\"pins\":{}}");
                var T = Session.Toasts;
                Session.Select(Session.TextChannels.First(c => c.Id == "loot"));   // look at #loot, so #general isn't the focused channel
                Session.IsShownToUser = () => false;                                // the Lodge window isn't in front (the player is in WoW)
                T.Visible.Clear();
                Session.Presence.SetForTest(null, true);
                string Msg(string id, string from, int fromId, string text, string extra = "") =>
                    "{\"t\":\"msg\",\"channel\":\"general\",\"id\":\"" + id + "\",\"at\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + ",\"from\":\"" + from + "\",\"fromId\":" + fromId + ",\"text\":\"" + text + "\"" + extra + "}";
                Session.FeedForTest(Msg("a1", "Bob", 2, "@Elan the Wailing Caverns group needs a hunter, are you in?"));
                Check("session: @mention -> toast with the sender and channel (" + T.Visible.Count + " " + (T.Visible.FirstOrDefault()?.Title) + " who=" + (T.Visible.FirstOrDefault()?.Who != null) + ")", T.Visible.Count == 1 && T.Visible[0].Kind == ToastKind.Mention && T.Visible[0].Title == "Bob · #General" && T.Visible[0].Who != null);
                T.Visible.Clear();
                Session.FeedForTest(Msg("a2", "Tess", 3, "Yes, sign me up", ",\"replyTo\":{\"id\":\"mine1\",\"by\":\"Elan\",\"snippet\":\"Who wants Wailing Caverns?\"}"));
                Check("session: a reply to my message -> reply toast", T.Visible.Count == 1 && T.Visible[0].Kind == ToastKind.Reply && T.Visible[0].Title.StartsWith("Tess"));
                T.Visible.Clear();
                Session.FeedForTest(Msg("a3", "Tess", 3, "hello all", ",\"replyTo\":{\"id\":\"a1\",\"by\":\"Bob\",\"snippet\":\"x\"}"));
                Check("session: a reply to someone else -> nothing", T.Visible.Count == 0);
                Session.FeedForTest("{\"t\":\"react\",\"channel\":\"general\",\"id\":\"mine1\",\"reaction\":\"love\",\"emoji\":\"x\",\"users\":[\"Bob\"],\"by\":\"Bob\",\"on\":true}");
                Check("session: reaction toast is off by default", T.Visible.Count == 0);
                s.ToastReactions = true; s.ToastPins = true;
                Session.FeedForTest("{\"t\":\"react\",\"channel\":\"general\",\"id\":\"mine1\",\"reaction\":\"epic\",\"emoji\":\"x\",\"users\":[\"Bob\",\"Tess\"],\"by\":\"Tess\",\"on\":true}");
                Check("session: a reaction on my message -> toast when enabled", T.Visible.Count == 1 && T.Visible[0].Kind == ToastKind.Reaction);
                Session.FeedForTest("{\"t\":\"react\",\"channel\":\"general\",\"id\":\"a3\",\"reaction\":\"epic\",\"emoji\":\"x\",\"users\":[\"Bob\"],\"by\":\"Bob\",\"on\":true}");
                Check("session: a reaction on someone else's message -> nothing", T.Visible.Count == 1);
                T.Visible.Clear();
                Session.FeedForTest("{\"t\":\"pin\",\"channel\":\"general\",\"pin\":{\"id\":\"a1\",\"text\":\"Raid is on Saturday at 20:00\",\"by\":\"Bob\",\"at\":" + nowMs + ",\"pinnedBy\":\"Bob\",\"pinnedAt\":" + nowMs + "}}");
                Check("session: a new pin -> toast when enabled", T.Visible.Count == 1 && T.Visible[0].Kind == ToastKind.Pin);
                T.Visible.Clear();
                Session.FeedForTest("{\"t\":\"pin\",\"channel\":\"general\",\"pin\":{\"id\":\"a1\",\"text\":\"Raid is on Saturday (edited)\",\"by\":\"Bob\",\"at\":" + nowMs + ",\"pinnedBy\":\"Bob\",\"pinnedAt\":" + nowMs + "}}");
                Check("session: an edited pin re-sent by the server doesn't toast again", T.Visible.Count == 0);
                s.ToastReactions = false; s.ToastPins = false;
                // in combat (presence flag from the companion strip): queued, one summary after
                Session.Presence.SetForTest(new GamePresence.Character { Name = "Elan", ClassFile = "HUNTER", Level = 60, HasLive = true, CombatKnown = true, Flags = 1 + 16 + 64 + 128 }, true);
                Session.FeedForTest(Msg("b1", "Bob", 2, "@Elan heal!"));
                Session.FeedForTest(Msg("b2", "Tess", 3, "@Elan watch the adds"));
                Check("session: in combat inside a dungeon -> queued, nothing on screen", T.Visible.Count == 0 && T.HeldCount == 2);
                Session.Presence.SetForTest(new GamePresence.Character { Name = "Elan", ClassFile = "HUNTER", Level = 60, HasLive = true, CombatKnown = true, Flags = 16 + 64 + 128 }, true);
                T.Tick();
                Check("session: after combat -> one summary", T.Visible.Count == 1 && T.Visible[0].Kind == ToastKind.Summary);
                LodgePage.ForceToastsForTest = true;
                await Task.Delay(700); T.Tick(); await Task.Delay(300); T.Tick();
                LodgePage.SnapshotToasts(System.IO.Path.Combine(dir, "13-toast-summary-after-combat.png"));
                LodgePage.ForceToastsForTest = false;
                T.Visible.Clear();
                // the Lodge window focused on that channel
                Session.IsShownToUser = () => true;
                Session.Select(Session.TextChannels.First(c => c.Id == "general"));
                Session.FeedForTest(Msg("c1", "Bob", 2, "@Elan are you looking at me?"));
                Check("session: Lodge focused on #general -> no toast for #general", T.Visible.Count == 0);
                Session.IsShownToUser = () => false;

                // ---- the window: render + click-through flags
                T.Visible.Clear(); T.Clock = () => DateTime.UtcNow;
                foreach (var it in new[] {
                    new ToastItem { Kind = ToastKind.Mention, Sender = "Bob", Channel = "General", ChannelId = "g", Text = "@Elan the Wailing Caverns group needs a hunter, are you in?", Who = Session.Members.First(m => m.Name == "Bob") },
                    new ToastItem { Kind = ToastKind.Reply, Sender = "Tess", Channel = "General", ChannelId = "g2", Text = "Yes, sign me up", Who = Session.Members.First(m => m.Name == "Tess") },
                    new ToastItem { Kind = ToastKind.Update, Sender = "Hub 2.18.0 is ready", Text = "Open the Hub to update." } })
                    T.Push(it);
                T.Push(new ToastItem { Kind = ToastKind.Mention, Sender = "Dax", Channel = "General", ChannelId = "g", Text = "@Elan one more", Who = null });
                T.Push(new ToastItem { Kind = ToastKind.Mention, Sender = "Kor", Channel = "General", ChannelId = "g", Text = "@everyone raid at 20:00" });
                LodgePage.ForceToastsForTest = true;
                await Task.Delay(1200);
                T.Tick();
                LodgePage.SnapshotToasts(System.IO.Path.Combine(dir, "13-toasts.png"));
                var win = LodgePage.ToastWindowForTest;
                Check("toast window: topmost, click-through, no activation", win != null && win.IsVisible && win.Topmost && !win.ShowActivated && !win.Focusable && ToastWindow.IsClickThrough(win.ExStyleForTest));
                T.Visible.Clear();
                LodgePage.ForceToastsForTest = false;
                Session.Presence.SetForTest(null, false);
                T.Visible.Clear(); await Task.Delay(300);
            }
            catch (Exception e) { ok = false; notes.Add("crashed: " + e); }
            finally { s.ToastsOff = o1; s.ToastReactions = o2; s.ToastPins = o3; s.ToastMentionsOff = o4; s.ToastCorner = corner; SettingsStore.Save(s); }
            return "toast selftest: " + (ok ? "PASSED" : "FAILED") + "\r\n  " + string.Join("\r\n  ", notes);
        }

    }
}
