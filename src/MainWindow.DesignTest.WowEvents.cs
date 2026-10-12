using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // ELANSHUB_DESIGN_SHOTS=wowevents with --selftest <dir>: the strip of upcoming events above the Lodge chat, and the Calendar's agenda and month view with WoW's
    // own events mixed in (events-*.png). Synthetic names and a fixture ElansHub.lua; the holiday pictures are flat stand-ins in the icon cache. Quits when done.
    public partial class MainWindow
    {
        async Task WowEventsShots(string dir)
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
                settings.EventsStripOff = false; settings.WowEventsOff = false;
                CalendarRules.Zone = () => TimeZoneInfo.CreateCustomTimeZone("test+2", TimeSpan.FromHours(2), "Test +2", "Test +2");
                CacheFixtureIcons();
                // the game's calendar: two holidays (one running), a raid reset, my Karazhan run, a guild night
                WriteWow(WowLua(
                    WowEv("Darkmoon Faire", "holiday", RealmUtc(5, 0, 0), RealmUtc(16, 23, 59), "2026-10-05 00:00", "2026-10-16 23:59", true, IconDarkmoon, "The Darkmoon Faire is in town.")
                    + WowEv("Hallow's End", "holiday", RealmUtc(13, 0, 0), RealmUtc(20, 23, 59), "2026-10-13 00:00", "2026-10-20 23:59", true, IconHallow, "Trick or treat! Costumes, candy and the headless horseman.")
                    + WowEv("Molten Core resets", "raidreset", Utc(14, 15), null, "2026-10-14 10:00", null, false, 0, "Your 40 Player lockout ends.")
                    + WowEv("Karazhan run", "player", Utc(14, 18), Utc(14, 21), "2026-10-14 13:00", "2026-10-14 16:00", false, 0, null, "going")
                    + WowEv("Guild night", "guild", Utc(15, 19), null, "2026-10-15 14:00", null, false, 0, null, "maybe")));

                var b1 = new DateTime(2026, 10, 12, 18, 0, 0, DateTimeKind.Utc);
                string card = "{\"t\":\"msg\",\"channel\":\"general\",\"id\":\"k1\",\"at\":" + CalMs(CalNow.AddMinutes(-4)) + ",\"from\":\"Elan\",\"fromId\":1,\"kind\":\"event\",\"event\":{\"id\":\"b1\",\"title\":\"Molten Core\",\"start\":"
                    + CalMs(b1) + ",\"end\":" + CalMs(b1.AddHours(3)) + ",\"by\":\"Elan\",\"status\":\"open\",\"weekly\":true},\"text\":\"[Event] Molten Core\"}";
                string chat(string id, string from, int fromId, string text, int minsAgo) =>
                    "{\"t\":\"msg\",\"channel\":\"general\",\"id\":" + Q(id) + ",\"at\":" + (CalMs(CalNow) - minsAgo * 60000L) + ",\"from\":" + Q(from) + ",\"fromId\":" + fromId + ",\"text\":" + Q(text) + "}";
                var history = string.Join(",", chat("m1", "Bob", 2, "Tomorrow night is Molten Core again, bring your fire resistance gear.", 12), chat("m2", "Mia", 3, "I can heal. Putting my name down.", 9),
                    chat("m3", "Kor", 4, "Anyone up for Karazhan on Wednesday as well?", 6), card);
                var events = string.Join(",", CalEv("a1", "The Deadmines", new DateTime(2026, 10, 11, 19, 0, 0, DateTimeKind.Utc), 120, "Bob", true, 1, 1, 3, string.Join(",", CalSig("Elan", "going", "tank", "bring water"), CalSig("Mia", "going", "healer"))),
                    CalEv("b1", "Molten Core", b1, 180, "Elan", true, 2, 3, 5, string.Join(",", CalSig("Mia", "going", "healer"), CalSig("Kor", "going", "dps"), CalSig("Tess", "maybe", "healer"), CalSig("Pat", "going", "tank")), "open", true, "Bring fire resistance"),
                    CalEv("d1", "Blackwing Lair", new DateTime(2026, 10, 17, 18, 0, 0, DateTimeKind.Utc), 180, "Elan", true, 2, 3, 5, "", "open", true));
                Session.FeedForTest(CalWelcome("owner", events, CalMs(CalNow), "2.11.0", true, history));
                Session.Select(Session.TextChannels.First(c => c.Id == "general"));
                await Task.Delay(1200);
                LodgePage.ScrollToEndForTest();
                await Task.Delay(500);
                Snapshot(Path.Combine(dir, "events-strip-full.png"));
                CropShot(Path.Combine(dir, "events-strip-full.png"), Path.Combine(dir, "events-strip.png"), new System.Windows.Int32Rect(320, 0, 860, 360));

                LodgePage.ShowCalendarForTest();
                var cal = LodgePage.CalendarForTest;
                cal.SetModeForTest(true);
                await Task.Delay(1000);
                Snapshot(Path.Combine(dir, "events-agenda.png"));

                cal.ShowMonthForTest(2026, 10, new DateTime(2026, 10, 14));
                await Task.Delay(800);
                Snapshot(Path.Combine(dir, "events-month.png"));

                cal.SetModeForTest(true);
                await Task.Delay(300);
                cal.OpenDetail(Session.WowEvents.First(e => e.Title == "Hallow's End"));
                await Task.Delay(900);
                Snapshot(Path.Combine(dir, "events-detail.png"));
                cal.CloseDetailForTest();
                await Task.Delay(300);
                File.WriteAllText(Path.Combine(dir, "result.txt"), "wowevents design shots done");
            }
            catch (Exception e) { File.WriteAllText(Path.Combine(dir, "result.txt"), "design shots crashed: " + e); }
            finally
            {
                try { if (File.Exists(WowSvFile())) File.Delete(WowSvFile()); } catch { }
                CalendarRules.Zone = hadZone; Session.Clock = hadClock; Session.SentForTest = null; Session.Disconnect();
            }
            Quit();
        }
    }
}
