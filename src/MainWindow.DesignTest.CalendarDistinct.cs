using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // ELANSHUB_DESIGN_SHOTS=calendar-distinct with --selftest <dir>: the Calendar with events you can tell apart (calendar-distinct-month.png, -day.png, -agenda.png), from a
    // fixture modelled on a real October 2026: Call to Arms weekends, the Sunday fishing contest, Darkmoon Faire, Hallow's End, a raid reset, my Karazhan run, a guild
    // night and a Lodge "Karaoke Sat 20:00". Synthetic names, a fixture ElansHub.lua, no server. Quits when done.
    public partial class MainWindow
    {
        async Task CalendarDistinctShots(string dir)
        {
            var hadClock = Session.Clock; var hadZone = CalendarRules.Zone;
            try
            {
                Width = 1180; Height = 900;
                Session.Disconnect();
                TabLodge.IsChecked = true;
                LodgePage.ShowChatForTest();
                Session.IsShownToUser = () => false;
                Session.Clock = () => CalNow;
                Session.SentForTest = o => { };
                settings.EventsStripOff = false; settings.WowEventsOff = false;
                settings.CalHideLodge = settings.CalHideHolidays = settings.CalHideResets = settings.CalHideMine = false;
                CalendarRules.Zone = () => TimeZoneInfo.CreateCustomTimeZone("test+2", TimeSpan.FromHours(2), "Test +2", "Test +2");
                WriteWow(WowLua(DistinctWowEvents()));
                Session.FeedForTest(CalWelcome("owner", DistinctLodgeEvents(), CalMs(CalNow), "2.11.0", true, ""));
                Session.Select(Session.TextChannels.First(c => c.Id == "general"));
                await Task.Delay(1200);
                LodgePage.ShowCalendarForTest();
                var cal = LodgePage.CalendarForTest;
                cal.SetModeForTest(true);
                await Task.Delay(1000);
                Snapshot(Path.Combine(dir, "calendar-distinct-agenda.png"));

                cal.ShowMonthForTest(2026, 10, new DateTime(2026, 10, 15));
                await Task.Delay(900);
                ((System.Windows.Controls.ScrollViewer)cal.MonthScrollForTest).ScrollToVerticalOffset(0);
                await Task.Delay(300);
                Snapshot(Path.Combine(dir, "calendar-distinct-month.png"));
                ((System.Windows.Controls.ScrollViewer)cal.MonthScrollForTest).ScrollToVerticalOffset(2000);
                await Task.Delay(500);
                Snapshot(Path.Combine(dir, "calendar-distinct-day.png"));
                File.WriteAllText(Path.Combine(dir, "result.txt"), "calendar-distinct design shots done");
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