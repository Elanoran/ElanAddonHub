using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // "lodgefun", part 2: the "Looking for group" cards and their form, from synthetic frames
    public partial class MainWindow
    {
        static IEnumerable<T> VisualsOf<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null) yield break;
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var c = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (c is T t) yield return t;
                foreach (var d in VisualsOf<T>(c)) yield return d;
            }
        }

        static string FunLfgJson(string title, long start, bool now, string note, string status, string by, int tank, int healer, int dps, string signups) =>
            "{\"title\":" + Q(title) + ",\"start\":" + start + ",\"now\":" + (now ? "true" : "false") + ",\"note\":" + Q(note) + ",\"status\":" + Q(status) + ",\"by\":" + Q(by)
            + ",\"slots\":{\"tank\":" + tank + ",\"healer\":" + healer + ",\"dps\":" + dps + "},\"signups\":[" + signups + "]}";

        static string FunLfgMsg(string id, string from, int fromId, long at, string lfgJson, string text) =>
            "{\"t\":\"msg\",\"channel\":\"general\",\"id\":" + Q(id) + ",\"at\":" + at + ",\"from\":" + Q(from) + ",\"fromId\":" + fromId + ",\"text\":" + Q(text) + ",\"kind\":\"lfg\",\"lfg\":" + lfgJson + "}";

        async Task FunLfg(string dir, Action<string, bool> check)
        {
            var hadClock = Session.Clock; var hadShown = Session.IsShownToUser;
            var fixedNow = new DateTime(2026, 10, 11, 17, 0, 0, DateTimeKind.Utc);
            Session.Clock = () => fixedNow;
            long Ms(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeMilliseconds();
            var sent = new List<Dictionary<string, object>>();
            Session.SentForTest = o => sent.Add(o);
            try
            {
                Session.IsShownToUser = () => false;
                var start = fixedNow.AddMinutes(25);
                var card = FunLfgMsg("c1", "Bob", 2, Ms(fixedNow) - 60000, FunLfgJson("The Deadmines", Ms(start), false, "bring water", "open", "Bob", 1, 1, 3, "{\"n\":\"Mia\",\"r\":\"healer\"},{\"n\":\"Dax\",\"r\":\"dps\"},{\"n\":\"Zed\",\"r\":\"maybe\"}"),
                    "[Looking for group] The Deadmines - Sat 11 Oct 17:25 UTC - Tank 0/1, Healer 1/1, DPS 1/3, 1 maybe - bring water (sign up in Elan's Outpost)");
                var oldStyle = "{\"t\":\"msg\",\"channel\":\"general\",\"id\":\"c0\",\"at\":" + (Ms(fixedNow) - 120000) + ",\"from\":\"Kor\",\"fromId\":6,\"text\":\"[Looking for group] Stratholme - now - Tank 0/1 (sign up in Elan's Outpost)\"}";
                Session.FeedForTest(FunWelcome("owner", FunPeople(false), oldStyle + "," + card));
                Session.Select(Session.TextChannels.First(c => c.Id == "general"));
                await Task.Delay(500);
                var msgs = Session.MessagesOf("general");
                var cm = msgs.First(m => m.Id == "c1"); var l = cm.Lfg;

                check("lfg: a 2.9 server announces the feature", Session.SupportsLfg);
                check("lfg: the message becomes a card (payload parsed), the fallback text is not shown", l != null && cm.LfgVisibility == Visibility.Visible && cm.TextVisibility == Visibility.Collapsed);
                check("lfg: title, note, slots and who signed up", l.Title == "The Deadmines" && l.Note == "bring water" && l.Tank.Total == 1 && l.Healer.Total == 1 && l.Dps.Total == 3
                    && l.Healer.Taken == 1 && l.Dps.Taken == 1 && l.Maybe.Taken == 1 && l.By == "Bob" && l.IsOpen);
                check("lfg: open places are drawn for the roles that have room", l.Tank.Slots.Count == 1 && !l.Tank.Slots[0].Filled && l.Dps.Slots.Count == 3 && l.Dps.Slots.Count(x => x.Filled) == 1);
                check("lfg: the time is local, the countdown follows the clock", l.TimeText.EndsWith(start.ToLocalTime().ToString("HH:mm")) && l.Countdown == "in 25 min" && l.WhenLine.Contains("in 25 min"));
                check("lfg: countdown wording (hours, days, started)", LfgVM.CountdownFor(fixedNow.AddMinutes(125), fixedNow, false, true) == "in 2 h 5 min" && LfgVM.CountdownFor(fixedNow.AddDays(3), fixedNow, false, true) == "in 3 days"
                    && LfgVM.CountdownFor(fixedNow.AddMinutes(-12), fixedNow, false, true).StartsWith("started 12") && LfgVM.CountdownFor(fixedNow, fixedNow, true, true) == "" && LfgVM.CountdownFor(fixedNow.AddHours(1), fixedNow, false, false) == "");
                var plain = msgs.First(m => m.Id == "c0");
                check("lfg: an old-style line (no payload) stays an ordinary message with its readable text", plain.Lfg == null && plain.TextVisibility == Visibility.Visible && plain.Text.StartsWith("[Looking for group] Stratholme"));

                // ---- drawn
                await Task.Delay(300);
                LodgePage.UpdateLayout();
                var texts = VisualsOf<TextBlock>(LodgePage.ChatListForTest).Select(t => t.Text).Where(t => !string.IsNullOrEmpty(t)).ToList();
                var buttons = VisualsOf<Button>(LodgePage.ChatListForTest).Where(b => b.Content is string).GroupBy(b => (string)b.Content).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
                check("lfg: the card shows the instance, the note, the role rows and their counts", texts.Contains("The Deadmines") && texts.Contains("bring water") && texts.Contains("Tank") && texts.Contains("Healer") && texts.Contains("DPS") && texts.Contains("Maybe"));
                check("lfg: the signed-up people are drawn by name (display name when they have one)", texts.Contains("Mia") && texts.Contains("Dax") && texts.Contains(DisplayNames.Shown("Zed")));
                check("lfg: buttons Join as Tank / Healer / DPS and Maybe; no Leave while I'm not in; organiser tools for the owner (an officer)",
                    buttons.ContainsKey("Join as Tank") && buttons.ContainsKey("Join as Healer") && buttons.ContainsKey("Join as DPS") && buttons.ContainsKey("Maybe")
                    && (!buttons.ContainsKey("Leave") || buttons["Leave"].Visibility != Visibility.Visible) && buttons["Close the group"].Visibility == Visibility.Visible && buttons["Cancel it"].Visibility == Visibility.Visible);
                check("lfg: the healer role is full: its button is off, the others work", !buttons["Join as Healer"].IsEnabled && buttons["Join as Tank"].IsEnabled && buttons["Join as DPS"].IsEnabled);

                // clicking really asks the server (and only asks: nothing changes until the frame arrives)
                sent.Clear();
                buttons["Join as Tank"].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                check("lfg: 'Join as Tank' sends lfg:join with the role", sent.Count == 1 && (string)sent[0]["t"] == "lfg:join" && (string)sent[0]["id"] == "c1" && (string)sent[0]["role"] == "tank");
                check("lfg: nothing moved before the server answered", l.Tank.Taken == 0 && l.MyRole == null);
                sent.Clear();
                buttons["Maybe"].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                check("lfg: 'Maybe' sends lfg:maybe", sent.Count == 1 && (string)sent[0]["t"] == "lfg:maybe" && (string)sent[0]["id"] == "c1");

                // ---- the server's update frame
                var signed = "{\"n\":\"Mia\",\"r\":\"healer\"},{\"n\":\"Dax\",\"r\":\"dps\"},{\"n\":\"Zed\",\"r\":\"maybe\"},{\"n\":\"Elan\",\"r\":\"tank\"}";
                Session.FeedForTest("{\"t\":\"lfg\",\"channel\":\"general\",\"id\":\"c1\",\"lfg\":" + FunLfgJson("The Deadmines", Ms(start), false, "bring water", "open", "Bob", 1, 1, 3, signed) + ",\"text\":\"[Looking for group] The Deadmines - ...\"}");
                await Task.Delay(200);
                check("lfg: an update frame fills the slot; I am the tank now", l.MyRole == "tank" && l.Tank.Taken == 1 && l.Tank.Full && l.MyLine == "You are in as Tank" && l.LeaveVisibility == Visibility.Visible);
                check("lfg: my own role's button is off (already there); Leave shows", !l.TankEnabled && l.HealerEnabled == false && l.MaybeEnabled && l.DpsEnabled);
                check("lfg: the card's fallback text follows the state for search and pins", cm.Text.StartsWith("[Looking for group] The Deadmines"));
                sent.Clear();
                var leave = VisualsOf<Button>(LodgePage.ChatListForTest).First(b => b.Content as string == "Leave");
                leave.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                check("lfg: 'Leave' sends lfg:leave", sent.Count == 1 && (string)sent[0]["t"] == "lfg:leave");

                // roles move: a role that someone else just filled
                Session.FeedForTest("{\"t\":\"lfg\",\"channel\":\"general\",\"id\":\"c1\",\"lfg\":" + FunLfgJson("The Deadmines", Ms(start), false, "bring water", "open", "Bob", 1, 1, 3,
                    "{\"n\":\"Mia\",\"r\":\"healer\"},{\"n\":\"Dax\",\"r\":\"dps\"},{\"n\":\"Kor\",\"r\":\"tank\"}") + "}");
                check("lfg: someone else took the tank place: my entry is gone, Join as Tank is off", l.MyRole == null && l.Tank.Full && !l.TankEnabled && l.LeaveVisibility == Visibility.Collapsed);

                // ---- closing and cancelling
                Session.FeedForTest("{\"t\":\"lfg\",\"channel\":\"general\",\"id\":\"c1\",\"lfg\":" + FunLfgJson("The Deadmines", Ms(start), false, "bring water", "closed", "Bob", 1, 1, 3,
                    "{\"n\":\"Mia\",\"r\":\"healer\"},{\"n\":\"Dax\",\"r\":\"dps\"},{\"n\":\"Kor\",\"r\":\"tank\"}") + "}");
                await Task.Delay(200);
                check("lfg: a closed card has no sign-up buttons and says Closed", l.StatusText == "Closed" && l.ActionsVisibility == Visibility.Collapsed && l.EndVisibility == Visibility.Collapsed && l.Countdown == "");
                Session.FeedForTest("{\"t\":\"msg\",\"channel\":\"general\",\"id\":\"c2\",\"at\":" + Ms(fixedNow) + ",\"from\":\"Elan\",\"fromId\":1,\"text\":\"x\",\"kind\":\"lfg\",\"lfg\":"
                    + FunLfgJson("Molten Core", Ms(fixedNow.AddHours(30)), false, "", "cancelled", "Elan", 2, 2, 10, "") + "}");
                var l2 = Session.MessagesOf("general").First(m => m.Id == "c2").Lfg;
                check("lfg: a cancelled card says Cancelled, has no buttons and draws no open places", l2.StatusText == "Cancelled" && l2.ActionsVisibility == Visibility.Collapsed && l2.Dps.Total == 10 && l2.Dps.Slots.Count == 0 && l2.CardOpacity < 1);
                Session.FeedForTest("{\"t\":\"msg\",\"channel\":\"general\",\"id\":\"c4\",\"at\":" + Ms(fixedNow) + ",\"from\":\"Elan\",\"fromId\":1,\"text\":\"x\",\"kind\":\"lfg\",\"lfg\":"
                    + FunLfgJson("Molten Core", Ms(fixedNow.AddHours(30)), false, "", "open", "Elan", 2, 2, 10, "{\"n\":\"Dax\",\"r\":\"dps\"}") + "}");
                var l6 = Session.MessagesOf("general").First(m => m.Id == "c4").Lfg;
                check("lfg: many open places collapse to '+N open' (a card tomorrow reads Tomorrow / a weekday)", l6.Dps.MoreText == "+4 open" && l6.Dps.Slots.Count == 6 && l6.Dps.Slots.Count(x => x.Filled) == 1 && (l6.TimeText.StartsWith("Tomorrow") || l6.TimeText.Length > 5) && l6.Countdown == "in 1 day");

                // an officer / the author may close; a plain member who isn't the author may not
                Session.FeedForTest("{\"t\":\"msg\",\"channel\":\"general\",\"id\":\"c3\",\"at\":" + Ms(fixedNow) + ",\"from\":\"Bob\",\"fromId\":2,\"text\":\"x\",\"kind\":\"lfg\",\"lfg\":"
                    + FunLfgJson("Wailing Caverns", Ms(fixedNow.AddHours(2)), false, "", "open", "Bob", 1, 1, 3, "") + "}");
                var l3 = Session.MessagesOf("general").First(m => m.Id == "c3").Lfg;
                check("lfg: the owner (officer rank) can end someone else's open card", l3.CanEnd && l3.EndVisibility == Visibility.Visible);
                Session.FeedForTest(FunWelcome("member", FunPeople(false), card + "," + FunLfgMsg("m1", "Elan", 1, Ms(fixedNow), FunLfgJson("Scholomance", Ms(fixedNow.AddHours(2)), false, "", "open", "Elan", 1, 1, 3, ""), "x")));
                var l4 = Session.MessagesOf("general").First(m => m.Id == "c1").Lfg;
                var l5 = Session.MessagesOf("general").First(m => m.Id == "m1").Lfg;
                check("lfg: a member who is not the organiser has no end tools; the organiser has them", !l4.CanEnd && l4.EndVisibility == Visibility.Collapsed && l5.CanEnd && l5.IAmAuthor);

                // ---- the 10-minute reminder toast
                Session.FeedForTest(FunWelcome("owner", FunPeople(false), ""));
                Session.Toasts.Visible.Clear();
                Session.Toasts.Clock = () => DateTime.UtcNow;
                var soon = fixedNow.AddMinutes(8);
                Session.FeedForTest(FunLfgMsg("r1", "Bob", 2, Ms(fixedNow), FunLfgJson("Blackfathom Deeps", Ms(soon), false, "", "open", "Bob", 1, 1, 3, "{\"n\":\"Elan\",\"r\":\"dps\"}"), "x"));
                Session.FeedForTest(FunLfgMsg("r2", "Bob", 2, Ms(fixedNow), FunLfgJson("Gnomeregan", Ms(fixedNow.AddMinutes(8)), false, "", "open", "Bob", 1, 1, 3, "{\"n\":\"Elan\",\"r\":\"maybe\"}"), "x"));
                Session.FeedForTest(FunLfgMsg("r3", "Bob", 2, Ms(fixedNow), FunLfgJson("Razorfen Kraul", Ms(fixedNow.AddMinutes(45)), false, "", "open", "Bob", 1, 1, 3, "{\"n\":\"Elan\",\"r\":\"dps\"}"), "x"));
                Session.FeedForTest(FunLfgMsg("r4", "Bob", 2, Ms(fixedNow), FunLfgJson("Uldaman", Ms(fixedNow.AddMinutes(8)), false, "", "open", "Bob", 1, 1, 3, "{\"n\":\"Mia\",\"r\":\"dps\"}"), "x"));
                int before = Session.Toasts.Shown;
                Session.LfgTick();
                var ev = Session.Toasts.Visible.Where(t => t.Kind == ToastKind.Event).ToList();
                check("toast: one reminder, 10 minutes before, for the group I signed up for (not a maybe, not later, not other people's)",
                    Session.Toasts.Shown == before + 1 && ev.Count == 1 && ev[0].Title.Contains("Blackfathom Deeps starts in 8 min") && ev[0].Text.Contains("Dps".ToUpper()) && ev[0].Kicker == "Group");
                Session.LfgTick();
                check("toast: only once per group", Session.Toasts.Shown == before + 1);
                fixedNow = fixedNow.AddMinutes(36);   // r3 is now 9 minutes away
                Session.LfgTick();
                check("toast: the later group gets its own when its time comes", Session.Toasts.Shown == before + 2 && Session.Toasts.Visible.Any(t => t.Title.Contains("Razorfen Kraul")));
                var cd = Session.MessagesOf("general").First(m => m.Id == "r3").Lfg;
                check("countdown: the card moved on with the clock", cd.Countdown == "in 9 min");

                // ---- the form
                Session.FeedForTest(FunWelcome("member", FunPeople(false), ""));
                Session.Select(Session.TextChannels.First(c => c.Id == "general"));
                LodgePage.PlusClickForTest();
                check("form: with files and sign-up cards the composer's + opens a small menu with both", LodgePage.PlusMenuOpenForTest && LodgePage.PlusMenuForTest.Trim() == "file lfg");
                LodgePage.ClosePlusForTest();
                LodgePage.OpenLfgFormForTest();
                await Task.Delay(300);
                check("form: opens with the default places 1 tank / 1 healer / 3 DPS", LodgePage.LfgFormOpenForTest && LodgePage.LfgCountsForTest.SequenceEqual(new[] { 1, 1, 3 }));
                check("form: suggestions from the built-in list (six to start with)", LodgePage.LfgSuggestionsForTest.Cast<string>().Count() == 6);
                LodgePage.LfgTitleForTest = "dead";
                check("form: typing narrows the suggestions to matching instances", LodgePage.LfgSuggestionsForTest.Cast<string>().SequenceEqual(new[] { "The Deadmines" }));
                LodgePage.LfgTitleForTest = "molten";
                check("form: suggestions also match inside a name", LodgePage.LfgSuggestionsForTest.Cast<string>().Contains("Molten Core"));
                LodgePage.LfgTitleForTest = "my own run";
                check("form: a free title has no suggestions", !LodgePage.LfgSuggestionsForTest.Cast<string>().Any());
                check("form: the catalogue is a small built-in list of classic dungeons and raids", LfgCatalog.Instances.Length >= 25 && LfgCatalog.Instances.Contains("Naxxramas") && LfgCatalog.Instances.Contains("Blackrock Depths"));
                LodgePage.LfgTitleForTest = "";
                sent.Clear();
                check("form: no title is refused with a reason and nothing is sent", !LodgePage.SubmitLfgForTest() && LodgePage.LfgErrorForTest.Length > 0 && sent.Count == 0 && LodgePage.LfgFormOpenForTest);
                LodgePage.LfgTitleForTest = "Zul'Gurub";
                for (int i = 0; i < 3; i++) { LodgePage.LfgStepForTest("tank", -1); LodgePage.LfgStepForTest("healer", -1); LodgePage.LfgStepForTest("dps", -1); }
                check("form: places stop at 0 and an empty group is refused", LodgePage.LfgCountsForTest.SequenceEqual(new[] { 0, 0, 0 }) && !LodgePage.SubmitLfgForTest() && sent.Count == 0);
                for (int i = 0; i < 60; i++) LodgePage.LfgStepForTest("dps", 1);
                check("form: places stop at 40", LodgePage.LfgCountsForTest[2] == 40);
                for (int i = 0; i < 40; i++) LodgePage.LfgStepForTest("dps", -1);
                LodgePage.LfgStepForTest("tank", 1); LodgePage.LfgStepForTest("healer", 1); for (int i = 0; i < 3; i++) LodgePage.LfgStepForTest("dps", 1);
                LodgePage.LfgNoteForTest = "bring water";
                LodgePage.LfgSetLaterForTest(false);
                Session.Select(Session.TextChannels.First(c => c.Id == "general"));
                var posted = LodgePage.SubmitLfgForTest();
                var m1 = sent.LastOrDefault();
                var lf1 = m1?.Child("lfg"); var sl1 = lf1?.Child("slots");
                check("form: 'Now' posts a card: kind lfg, title, now, places, note, in the open channel",
                    posted && m1 != null && m1.Str("t") == "msg" && m1.Str("kind") == "lfg" && m1.Str("channel") == "general" && lf1.Str("title") == "Zul'Gurub" && lf1.Bool("now") && !lf1.ContainsKey("start")
                    && sl1.Int("tank") == 1 && sl1.Int("healer") == 1 && sl1.Int("dps") == 3 && lf1.Str("note") == "bring water" && !LodgePage.LfgFormOpenForTest);
                LodgePage.OpenLfgFormForTest();
                LodgePage.LfgTitleForTest = "Molten Core";
                LodgePage.LfgSetLaterForTest(true, 1, 20, 2);   // tomorrow 20:30
                sent.Clear();
                bool posted2 = LodgePage.SubmitLfgForTest();
                var lf2 = sent.LastOrDefault()?.Child("lfg");
                var wantLocal = DateTime.Now.Date.AddDays(1).AddHours(20).AddMinutes(30);
                var gotMs = lf2 != null && lf2.ContainsKey("start") ? (long)Convert.ToDouble(lf2["start"]) : 0;
                check("form: a picked time is sent as the exact local moment (UTC milliseconds)", posted2 && !lf2.Bool("now") && gotMs == new DateTimeOffset(wantLocal.ToUniversalTime()).ToUnixTimeMilliseconds());
                LodgePage.OpenLfgFormForTest();
                LodgePage.LfgTitleForTest = "Too late";
                LodgePage.LfgSetLaterForTest(true, 0, 0, 0);    // today 00:00 - already past, unless it is the first minutes of the day
                sent.Clear();
                bool past = DateTime.Now.Date.AddMinutes(0) < DateTime.Now.AddMinutes(-10);
                check("form: a time that has already passed is refused here (and again by the server)", !past || (!LodgePage.SubmitLfgForTest() && sent.Count == 0 && LodgePage.LfgErrorForTest.Contains("passed")));
                LodgePage.CloseLfgFormForTest();

                // ---- a 2.8 server: no cards, nothing breaks
                Session.FeedForTest(FunWelcome("member", FunPeople(false), oldStyle, "\"reply\",\"react\",\"pin\",\"profile\",\"displayname\"", "2.8.0"));
                await Task.Delay(200);
                LodgePage.OpenLfgFormForTest();
                check("2.8 server: no sign-up feature, the form refuses to open, old-style lines stay readable", !Session.SupportsLfg && !LodgePage.LfgFormOpenForTest && Session.MessagesOf("general").First(m => m.Id == "c0").Lfg == null);
                check("2.8 server: the composer's + goes straight to sharing a file (no menu)", LodgePage.PlusIsVisibleForTest);
            }
            finally { Session.Clock = hadClock; Session.SentForTest = null; Session.IsShownToUser = hadShown; Session.Toasts.Clock = () => DateTime.UtcNow; LodgePage.CloseLfgFormForTest(); }
        }
    }
}
