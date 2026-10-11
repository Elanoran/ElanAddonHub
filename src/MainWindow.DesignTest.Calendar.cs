using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // ELANSHUB_DESIGN_SHOTS=calendar with --selftest <dir>: renders the raid / event calendar from synthetic frames - the agenda, the month view, an event's
    // detail, the create form, the next-event line in the sidebar and an event card in chat (calendar-*.png). Synthetic names only. Quits when done.
    public partial class MainWindow
    {
        // a part of a snapshot, as its own picture
        static void CropShot(string from, string to, System.Windows.Int32Rect area)
        {
            var dec = new System.Windows.Media.Imaging.PngBitmapDecoder(new Uri(from), System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(new System.Windows.Media.Imaging.CroppedBitmap(dec.Frames[0], area)));
            using (var fs = System.IO.File.Create(to)) enc.Save(fs);
        }

        async Task CalendarShots(string dir)
        {
            var hadClock = Session.Clock; var hadZone = CalendarRules.Zone;
            try
            {
                Width = 1180; Height = 760;
                Session.Disconnect();
                TabLodge.IsChecked = true;
                LodgePage.ShowChatForTest();
                Session.IsShownToUser = () => false;
                Session.Clock = () => CalNow;
                Session.SentForTest = o => { };
                CalendarRules.Zone = () => TimeZoneInfo.CreateCustomTimeZone("test+2", TimeSpan.FromHours(2), "Test +2", "Test +2");
                var b1 = new DateTime(2026, 10, 12, 18, 0, 0, DateTimeKind.Utc);
                string card = "{\"t\":\"msg\",\"channel\":\"general\",\"id\":\"k1\",\"at\":" + CalMs(CalNow.AddMinutes(-4)) + ",\"from\":\"Elan\",\"fromId\":1,\"kind\":\"event\",\"event\":{\"id\":\"b1\",\"title\":\"Molten Core\",\"start\":"
                    + CalMs(b1) + ",\"end\":" + CalMs(b1.AddHours(3)) + ",\"by\":\"Elan\",\"status\":\"open\",\"weekly\":true},\"text\":\"[Event] Molten Core\"}";
                string chat(string id, string from, int fromId, string text, int minsAgo) =>
                    "{\"t\":\"msg\",\"channel\":\"general\",\"id\":" + Q(id) + ",\"at\":" + (CalMs(CalNow) - minsAgo * 60000L) + ",\"from\":" + Q(from) + ",\"fromId\":" + fromId + ",\"text\":" + Q(text) + "}";
                var history = string.Join(",", chat("m1", "Bob", 2, "Tomorrow night is Molten Core again, bring your fire resistance gear.", 12), chat("m2", "Mia", 3, "I can heal. Putting my name down.", 9), card);
                Session.FeedForTest(CalWelcome("owner", CalEvents(), CalMs(CalNow), "2.11.0", true, history));
                Session.Select(Session.TextChannels.First(c => c.Id == "general"));
                await Task.Delay(800);

                // the next-event line sits at the bottom of the sidebar, under the channels (and the chat card is in the chat)
                LodgePage.ScrollToEndForTest();
                await Task.Delay(300);
                Snapshot(System.IO.Path.Combine(dir, "calendar-chat.png"));
                CropShot(System.IO.Path.Combine(dir, "calendar-chat.png"), System.IO.Path.Combine(dir, "calendar-next-event.png"), new System.Windows.Int32Rect(96, 520, 250, 210));

                LodgePage.ShowCalendarForTest();
                var cal = LodgePage.CalendarForTest;
                cal.SetModeForTest(true);
                await Task.Delay(900);
                Snapshot(System.IO.Path.Combine(dir, "calendar-agenda.png"));

                cal.ShowMonthForTest(2026, 10, new DateTime(2026, 10, 12));
                await Task.Delay(700);
                Snapshot(System.IO.Path.Combine(dir, "calendar-month.png"));

                cal.SetModeForTest(true);
                await Task.Delay(300);
                cal.OpenDetail(Session.EventById("b1"));
                await Task.Delay(800);
                Snapshot(System.IO.Path.Combine(dir, "calendar-detail.png"));
                cal.CloseDetailForTest();
                await Task.Delay(300);

                cal.OpenFormForTest();
                await Task.Delay(300);
                cal.FormTitleForTest = "Molten Core";
                cal.FormWhenForTest(0, 17, 20);
                cal.FormWeeklyForTest(true, 3);
                cal.FormLengthForTest(4);
                cal.FormNoteForTest = "Bring fire resistance gear";
                await Task.Delay(700);
                Snapshot(System.IO.Path.Combine(dir, "calendar-form.png"));
                cal.CloseFormForTest();
                await Task.Delay(300);
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "result.txt"), "calendar design shots done");
            }
            catch (Exception e) { System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "result.txt"), "design shots crashed: " + e); }
            finally { CalendarRules.Zone = hadZone; Session.Clock = hadClock; Session.SentForTest = null; Session.Disconnect(); }
            Quit();
        }
    }
}
