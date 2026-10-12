using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // "wowevents" / "calendar" selftest, part 2: telling events apart. Occurrences of a repeating holiday stay separate (addon 1.9.1 data) and old merged data is not
    // drawn as a month-long band; the time texts; one colour per kind and a stable colour per holiday title (with contrast checks); labelled chips and bars in the
    // month, "+N more"; the filter chips; and that none of it moves or resizes anything. Also the fixture that ELANSHUB_DESIGN_SHOTS=calendar-distinct draws.
    public partial class MainWindow
    {
        static DateTime Dt(int month, int day, int hour, int minute = 0) => new DateTime(2026, month, day, hour, minute, 0, DateTimeKind.Utc);

        // Modelled on a real October 2026 (PC zone UTC+2 in the tests): Call to Arms weekends Thu 17:00 - Mon 16:00, the Stranglethorn Fishing Extravaganza every Sunday
        // 23:00 - 00:00, Darkmoon Faire, Hallow's End, a raid reset, my Karazhan run and a guild night.
        const string FishingDesc = "A fishing tournament, held along the coasts of Stranglethorn Vale. Catch as many tastyfish as you can! Will you be the master fisherman who catches forty fish first and wins the grand prize? "
            + "Show up at Booty Bay on Sunday afternoon, bring a fishing pole and your best bait, and keep your eyes on the ranking board. The first to hand in forty fish wins; the rest keep the fish.";
        static string DistinctWowEvents() =>
            WowEv("Call to Arms: Warsong Gulch", "holiday", Dt(10, 8, 15), Dt(10, 12, 14), null, null, false, 0, "The battle for Warsong Gulch grows intense. Greater honor is given to those who pledge sword to their liege and battle in the Gulch.", null, 5, true)
            + WowEv("Call to Arms: Warsong Gulch", "holiday", Dt(10, 15, 15), Dt(10, 19, 14), null, null, false, 0, "The battle for Warsong Gulch grows intense. Greater honor is given to those who pledge sword to their liege and battle in the Gulch.", null, 5, true)
            + WowEv("Call to Arms: Arathi Basin", "holiday", Dt(10, 22, 15), Dt(10, 26, 14), null, null, false, 0, "The battle for Arathi Basin grows intense.", null, 5, true)
            + WowEv("Call to Arms: Darkspear Islands", "holiday", Dt(10, 29, 15), Dt(11, 2, 14), null, null, false, 0, "The battle at Darkspear Islands is heating up. Join up and prove your might!", null, 5, true)
            + WowEv("Stranglethorn Fishing Extravaganza", "holiday", Dt(10, 11, 21), Dt(10, 11, 22), null, null, false, 0, FishingDesc, null, 1, true)
            + WowEv("Stranglethorn Fishing Extravaganza", "holiday", Dt(10, 18, 21), Dt(10, 18, 22), null, null, false, 0, FishingDesc, null, 1, true)
            + WowEv("Stranglethorn Fishing Extravaganza", "holiday", Dt(10, 25, 21), Dt(10, 25, 22), null, null, false, 0, FishingDesc, null, 1, true)
            + WowEv("Darkmoon Faire", "holiday", Dt(10, 10, 22), Dt(10, 17, 21, 59), null, null, true, 0, "The Darkmoon Faire is in town.", null, 7, true)
            + WowEv("Hallow's End", "holiday", Dt(10, 17, 22), Dt(11, 1, 21, 59), null, null, true, 0, "Hallow's End celebrates the break of the Forsaken from the Scourge.", null, 15, true)
            + WowEv("Molten Core resets", "raidreset", Dt(10, 13, 15), null, null, null, false, 0, "Your 40 Player lockout ends.")
            + WowEv("Karazhan run", "player", Dt(10, 15, 18), Dt(10, 15, 21), null, null, false, 0, null, "going")
            + WowEv("Guild night", "guild", Dt(10, 16, 19), null, null, null, false, 0, null, "maybe");

        static string DistinctLodgeEvents() => string.Join(",",
            CalEv("m1", "Molten Core", Dt(10, 14, 18), 180, "Elan", true, 2, 3, 5, string.Join(",", CalSig("Mia", "going", "healer"), CalSig("Kor", "going", "dps"), CalSig("Pat", "going", "tank")), "open", true, "Bring fire resistance"),
            CalEv("k1", "Karaoke Sat 20:00", Dt(10, 17, 18), 180, "Bob", false, 0, 0, 0, string.Join(",", CalSig("Dax", "going", ""), CalSig("Zed", "going", ""), CalSig("Mia", "maybe", ""))));

        // the same holidays the way addon 1.9.0 saved them: every same-titled occurrence merged into one span from the first start to the last end
        static string OldMergedWowEvents() =>
            WowEv("Call to Arms: Warsong Gulch", "holiday", Dt(9, 29, 13), Dt(11, 17, 13), "2026-09-29 08:00", "2026-11-17 08:00", false, 0, "The battle for Warsong Gulch grows intense.")
            + WowEv("Stranglethorn Fishing Extravaganza", "holiday", Dt(10, 4, 19), Dt(11, 8, 21), "2026-10-04 14:00", "2026-11-08 16:00", false, 0, FishingDesc)
            + WowEv("Call to Arms: Darkspear Islands", "holiday", Dt(10, 6, 13), Dt(11, 3, 13), "2026-10-06 08:00", "2026-11-03 08:00", false, 0, "The battle at Darkspear Islands is heating up.")
            + WowEv("Hallow's End", "holiday", Dt(10, 18, 9), Dt(11, 2, 9), "2026-10-18 04:00", "2026-11-02 04:00", false, 0, "Hallow's End celebrates the break of the Forsaken from the Scourge.")
            + WowEv("Midsummer Long Fair", "holiday", Dt(10, 5, 9), Dt(11, 10, 9), "2026-10-05 04:00", "2026-11-10 04:00", false, 0, "A long, but not weekly, holiday.")
            + WowEv("Karazhan run", "player", Dt(10, 15, 18), Dt(10, 15, 21), null, null, false, 0, null, "going");

        async Task WowDistinct(Action<string, bool> check, List<Dictionary<string, object>> sent)
        {
            var cal = LodgePage.CalendarForTest;
            var plus2 = CalendarRules.Zone();
            Func<string, System.Windows.Media.Color> Col = key => ((SolidColorBrush)FindResource(key)).Color;

            // ---- addon 1.9.1 data: one entry per occurrence
            WriteWow(WowLua(DistinctWowEvents(), -18000, 2));
            var d = WowCalendar.Read(settings.WowRoot);
            var wsg = d.Events.Where(e => e.Title == "Call to Arms: Warsong Gulch").ToList();
            check("occurrences: format 2 is read (v=2), a weekly holiday is one entry per weekend: Warsong Gulch twice, Arathi and Darkspear once", d.Version == 2 && wsg.Count == 2 && d.Events.Count(e => e.Title.StartsWith("Call to Arms")) == 4);
            check("occurrences: each occurrence keeps its own start / end (4 days, day count and the game's sequence mark stored)", wsg[1].Start - wsg[0].Start == 7 * 86400 && wsg.All(e => e.Finish - e.Start < 5 * 86400 && e.Days == 5 && e.Seq && !e.Recurring));
            check("occurrences: the Sunday fishing contest is three single-day entries, not one span", d.Events.Count(e => e.Title.StartsWith("Stranglethorn")) == 3 && d.Events.Where(e => e.Title.StartsWith("Stranglethorn")).All(e => e.Finish - e.Start == 3600));
            check("occurrences: a single multi-day holiday stays ONE span (Hallow's End, 15 days)", d.Events.Count(e => e.Title == "Hallow's End") == 1 && d.Events.First(e => e.Title == "Hallow's End").Days == 15);
            Session.FeedForTest(CalWelcome("owner", DistinctLodgeEvents(), CalMs(CalNow)));
            Session.ReloadWowEvents(true);
            await Task.Delay(400);
            LodgePage.ShowCalendarForTest();
            cal.SetModeForTest(true);
            await Task.Delay(500);
            var vmW = Session.WowEvents.Where(e => e.Title == "Call to Arms: Warsong Gulch").OrderBy(e => e.StartUtc).ToList();
            var fish = Session.WowEvents.Where(e => e.Title.StartsWith("Stranglethorn")).OrderBy(e => e.StartUtc).ToList();
            var dmf = Session.WowEvents.First(e => e.Title == "Darkmoon Faire");

            // ---- the time texts
            check("time text: a holiday of several days names both days: 'Thu 15 Oct 17:00 - Mon 19 Oct 16:00' (never two bare times)", vmW[1].TimeText == "Thu 15 Oct 17:00 - Mon 19 Oct 16:00" && vmW[1].MultiDay && vmW[1].EndHm == "" && vmW[1].StartHm == "17:00");
            check("time text: a one-day event names its day: 'Sun 23:00 - 00:00' (an end at 00:00 belongs to the day before)", fish[0].TimeText == "Sun 23:00 - 00:00" && !fish[0].MultiDay && fish[0].StartHm == "23:00" && fish[0].EndHm == "00:00");
            check("time text: a reset is 'Tue 17:00', an all-day holiday stays 'All day', a Lodge event keeps '20:00 - 23:00'",
                Session.WowEvents.First(e => e.Title == "Molten Core resets").TimeText == "Tue 17:00" && dmf.TimeText == "All day" && Session.EventById("k1").TimeText == "20:00 - 23:00");
            check("time text: the running holiday's detail line has the full range and the countdown ('... · until tomorrow'): " + vmW[0].WhenLine, vmW[0].WhenLine.StartsWith("Thu 8 Oct 17:00 - Mon 12 Oct 16:00") && vmW[0].WhenLine.EndsWith("until tomorrow"));
            check("time text: WowWhen directly - an instant, a day, a span", CalendarRules.WowWhen(Dt(10, 14, 15), DateTime.MinValue) == "Wed 17:00" && CalendarRules.WowWhen(Dt(10, 14, 15), Dt(10, 14, 17)) == "Wed 17:00 - 19:00"
                && CalendarRules.WowWhen(Dt(10, 14, 15), Dt(10, 16, 6)) == "Wed 14 Oct 17:00 - Fri 16 Oct 08:00");

            // ---- agenda: separate cards, a bar and a type label on every card
            var groups = cal.AgendaGroupsForTest;
            var all = groups.SelectMany(g => g.Events).ToList();
            check("agenda: each occurrence is its own card (Warsong Gulch twice, the fishing contest three times, Hallow's End once)", all.Count(e => e.Title == "Call to Arms: Warsong Gulch") == 2 && all.Count(e => e.Title.StartsWith("Stranglethorn")) == 3 && all.Count(e => e.Title == "Hallow's End") == 1);
            var texts = VisualsOf<TextBlock>(cal.AgendaListElementForTest).Select(t => t.Text).ToList();
            check("drawn: the cards say when ('Thu 15 Oct 17:00 - Mon 19 Oct 16:00', 'Sun 23:00 - 00:00') and what they are (Lodge, Holiday, Raid reset, Guild event, Your event)",
                texts.Contains("Thu 15 Oct 17:00 - Mon 19 Oct 16:00") && texts.Contains("Sun 23:00 - 00:00") && new[] { "Lodge", "Holiday", "Raid reset", "Guild event", "Your event" }.All(k => texts.Contains(k)));
            var bars = VisualsOf<Border>(cal.AgendaListElementForTest).Where(b => Math.Abs(b.ActualWidth - 4) < 0.5 && b.ActualHeight > 40 && b.Background is SolidColorBrush).ToList();
            check("drawn: every card has a 4 px bar in its kind's colour (" + bars.Count + " bars for " + all.Count + " cards)", bars.Count == all.Count && bars.All(b => b.Background is SolidColorBrush sb && sb.Color.A == 255));
            var bar0 = bars.Select(b => ((SolidColorBrush)b.Background).Color).Distinct().Count();
            check("drawn: the bars use several colours (" + bar0 + ")", bar0 >= 5);
            var desc = VisualsOf<TextBlock>(cal.AgendaListElementForTest).FirstOrDefault(t => t.Text != null && t.Text.StartsWith("A fishing tournament"));
            check("drawn: a long description is cut to 2 lines (" + Math.Round(desc?.ActualHeight ?? -1) + " px, the text itself is far longer)", desc != null && desc.ActualHeight <= 32.5 && desc.ActualHeight >= 31.5 && desc.Text.Length > 300);

            // ---- colours
            check("colour: a holiday keeps its colour for its title - every occurrence, any case, any padding", CalendarColors.HolidayIndex("Hallow's End") == CalendarColors.HolidayIndex("  hallow's end ") && vmW[0].ColorKey == vmW[1].ColorKey
                && fish.All(f => f.ColorKey == fish[0].ColorKey) && CalendarColors.HolidayIndex("Darkmoon Faire") == CalendarColors.HolidayIndex("Darkmoon Faire"));
            check("colour: the hash is a fixed function (FNV-1a), not the per-process string hash: 'Darkmoon Faire' = " + CalendarColors.HolidayIndex("Darkmoon Faire") + ", 'Hallow's End' = " + CalendarColors.HolidayIndex("Hallow's End"),
                CalendarColors.HolidayIndex("Darkmoon Faire") == 4 && CalendarColors.HolidayIndex("Hallow's End") == 6);
            var holTitles = new[] { "Call to Arms: Warsong Gulch", "Call to Arms: Arathi Basin", "Call to Arms: Darkspear Islands", "Stranglethorn Fishing Extravaganza", "Darkmoon Faire", "Hallow's End", "Feast of Winter Veil", "Harvest Festival", "Lunar Festival" };
            check("colour: different holidays get different hues (" + holTitles.Select(CalendarColors.HolidayIndex).Distinct().Count() + " of 7 used by 9 titles: " + string.Join(",", holTitles.Select(CalendarColors.HolidayIndex)) + ") and all indexes are 1..7", holTitles.Select(CalendarColors.HolidayIndex).Distinct().Count() >= 4 && holTitles.All(t => CalendarColors.HolidayIndex(t) >= 1 && CalendarColors.HolidayIndex(t) <= 7));
            var keys = new List<string> { "Brush.Cal.Lodge", "Brush.Cal.Raid", "Brush.Cal.Mine", "Brush.Cal.Guild" };
            keys.AddRange(Enumerable.Range(1, CalendarColors.HolidayHues).Select(i => "Brush.Cal.Holiday." + i));
            check("colour: the tokens exist (Lodge, Raid, Mine, Guild, 7 holiday hues, each with a .Tint)", keys.All(k => TryFindResource(k) is SolidColorBrush && TryFindResource(k + ".Tint") is SolidColorBrush));
            check("colour: the kinds map to their tokens (Lodge = the accent colour, raid reset, my event, guild event)",
                Session.EventById("k1").ColorKey == "Brush.Cal.Lodge" && Col("Brush.Cal.Lodge") == Col("Brush.Accent") && Session.WowEvents.First(e => e.Title == "Molten Core resets").ColorKey == "Brush.Cal.Raid"
                && Session.WowEvents.First(e => e.Title == "Karazhan run").ColorKey == "Brush.Cal.Mine" && Session.WowEvents.First(e => e.Title == "Guild night").ColorKey == "Brush.Cal.Guild");
            check("colour: the 11 colours are all different", keys.Select(Col).Distinct().Count() == keys.Count);
            var surf1 = Col("Brush.Surface1"); var surf2 = Col("Brush.Surface2"); var prim = Col("Brush.Text.Primary"); var accTint = Col("Brush.Accent.Tint");
            var worst = new[] { double.MaxValue, double.MaxValue, double.MaxValue, double.MaxValue }; var perKey = new List<string>();
            foreach (var k in keys)
            {
                var bar = Col(k); var tint = Col(k + ".Tint");
                worst[0] = Math.Min(worst[0], CalendarColors.Contrast(prim, CalendarColors.Over(tint, surf1)));                                 // chip title on a day cell
                worst[1] = Math.Min(worst[1], CalendarColors.Contrast(prim, CalendarColors.Over(tint, CalendarColors.Over(accTint, surf1))));       // ... on the selected day
                worst[2] = Math.Min(worst[2], CalendarColors.Contrast(bar, surf1));                                                              // the bar / dot against the surface
                worst[3] = Math.Min(worst[3], CalendarColors.Contrast(bar, CalendarColors.Over(tint, surf2)));                                   // type label text on its chip on a card
                perKey.Add(k.Replace("Brush.Cal.", "") + "=" + CalendarColors.Contrast(bar, CalendarColors.Over(tint, surf2)).ToString("0.0", CultureInfo.InvariantCulture));
            }
            check("contrast: chip text (Text.Primary) on every tint >= 4.5:1 (WCAG AA; worst " + worst[0].ToString("0.0", CultureInfo.InvariantCulture) + "), on the selected day too (" + worst[1].ToString("0.0", CultureInfo.InvariantCulture) + ")", worst[0] >= 4.5 && worst[1] >= 4.5);
            check("contrast: every bar colour against the surface >= 3:1 (worst " + worst[2].ToString("0.0", CultureInfo.InvariantCulture) + "), type label text on its tint >= 4.5:1 (worst " + worst[3].ToString("0.0", CultureInfo.InvariantCulture) + "; " + string.Join(" ", perKey) + ")", worst[2] >= 3 && worst[3] >= 4.5);

            // ---- month: labelled chips and bars
            cal.ShowMonthForTest(2026, 10, new DateTime(2026, 10, 15));
            await Task.Delay(500);
            var cells = cal.MonthCellsForTest; var lay = cal.MonthLayoutForTest;
            MonthCellVM C(int day) => cells.First(c => c.Cell.Day == new DateTime(2026, 10, day));
            int Ix(int day) => cells.IndexOf(C(day));
            check("month: the two Warsong Gulch weekends are two separate bars: 8-12 and 15-19, nothing on the 13th and 14th", Enumerable.Range(8, 5).Concat(Enumerable.Range(15, 5)).All(x => C(x).Cell.Bands.Any(b => b.Title == "Call to Arms: Warsong Gulch"))
                && !C(13).Cell.Bands.Any(b => b.Title.StartsWith("Call to Arms")) && !C(14).Cell.Bands.Any(b => b.Title.StartsWith("Call to Arms")) && !C(20).Cell.Bands.Any(b => b.Title == "Call to Arms: Warsong Gulch"));
            check("month: the fishing contest is a chip on the three Sundays only (11, 18, 25), nothing on the days between", new[] { 11, 18, 25 }.All(x => C(x).Cell.Events.Any(e => e.Title.StartsWith("Stranglethorn"))) && Enumerable.Range(1, 31).Where(x => !new[] { 11, 18, 25 }.Contains(x)).All(x => !C(x).Cell.Events.Any(e => e.Title.StartsWith("Stranglethorn"))));
            check("month: no month-long band anywhere - the longest bar is Hallow's End (15 days), no bar is longer", cells.SelectMany(c => c.Cell.Bands).Distinct().All(b => (b.EndUtc - b.StartUtc).TotalDays <= 15.01));
            string[] ChipsOn(int day) => lay.Chips.Where(s => s.Row * 7 + s.Col == Ix(day)).OrderBy(s => s.Lane).Select(s => s.Label).ToArray();
            check("month: the Lodge event is a chip with its time ('20:00 Karaoke Sat 20:00' on Saturday the 17th) and wins a line over the holidays", ChipsOn(17).Contains("20:00 Karaoke Sat 20:00") && lay.Chips.Any(s => s.Ev.Id == "k1" && s.Lane >= 0));
            int moreSum = lay.More.Sum();
            var moreLabels = cal.MonthOverlayForTest.Children.OfType<TextBlock>().Select(t => t.Text).ToList();
            check("month: ''+N more'' appears where a day has more than its two lines (" + moreLabels.Count + " days, " + moreSum + " events not shown) and every event is counted once: shown + more = all", moreLabels.Count == lay.More.Count(x => x > 0) && moreLabels.Count >= 1
                && cells.Select((c, i) => lay.ShownOn(i) <= 2 && lay.ShownOn(i) + lay.More[i] == c.Cell.Count).All(x => x));
            check("month: the 15th (Warsong Gulch weekend + Darkmoon + my Karazhan run) shows one bar, Karazhan, and ''+1 more''", lay.ShownOn(Ix(15)) == 2 && ChipsOn(15).Single() == "20:00 Karazhan run" && lay.More[Ix(15)] == 1 && lay.Bars.Any(s => s.Row * 7 + s.Col <= Ix(15) && Ix(15) < s.Row * 7 + s.Col + s.Span));
            // a multi-day holiday that crosses the end of a week row: one labelled bar per row, square where it goes on (checked with an 8-day fair and nothing else in the month)
            var fairEv = EventVM.FromWow(new WowEvent { Title = "Synthetic Fair", Kind = "holiday", Start = UnixSecs(Dt(10, 6, 22)), Finish = UnixSecs(Dt(10, 14, 21, 59)), AllDay = true });
            var fairLay = CalendarRules.Layout(CalendarRules.MonthGrid(2026, 10, new[] { fairEv }, CalNow));
            var fp = fairLay.Bars.OrderBy(s => s.Row).ToList();
            check("layout: an 8-day holiday (7-14 Oct) crossing a week row's end = two labelled bars: the first square on the right, the second starting at the row start, square on the left, title again; 8 days together, same line",
                fp.Count == 2 && fp.All(s => s.Label == "Synthetic Fair" && s.Lane == 0) && fp[0].RightOpen && !fp[0].LeftOpen && fp[1].Col == 0 && fp[1].LeftOpen && !fp[1].RightOpen && fp[0].Span + fp[1].Span == 8 && fp[1].Row == fp[0].Row + 1
                && fp[0].Col + fp[0].Span == 7 && fairLay.More.All(m => m == 0));
            var fairLong = EventVM.FromWow(new WowEvent { Title = "Synthetic Long Fair", Kind = "holiday", Start = UnixSecs(Dt(10, 1, 22)), Finish = UnixSecs(Dt(10, 30, 21, 59)), AllDay = true });
            var longLay = CalendarRules.Layout(CalendarRules.MonthGrid(2026, 10, new[] { fairLong }, CalNow));
            check("layout: a 30-day bar is one bar per week row (5 labelled parts, middle rows full width), never one long band", longLay.Bars.Count() >= 5 && longLay.Bars.All(s => s.Label == "Synthetic Long Fair") && longLay.Bars.Count(s => s.Span == 7) >= 3);
            var busy = new[] { fairEv }.Concat(new[] { "Alpha", "Beta", "Gamma" }.Select((n, i) => EventVM.FromWow(new WowEvent { Title = n, Kind = "player", Start = UnixSecs(Dt(10, 8, 10 + i)) }))).ToList();
            var busyCells = CalendarRules.MonthGrid(2026, 10, busy, CalNow);
            var busyLay = CalendarRules.Layout(busyCells);
            int bi = busyCells.FindIndex(c => c.Day == new DateTime(2026, 10, 8));
            check("layout: a day with a holiday bar and three events shows the bar's line + one chip (the day keeps a line for its own event), ''+2 more''", busyLay.ShownOn(bi) == 2 && busyLay.Chips.Count(s => s.Row * 7 + s.Col == bi) == 1 && busyLay.More[bi] == 2);
            var l3 = CalendarRules.Layout(CalendarRules.MonthGrid(2026, 10, busy.Skip(1).ToList(), CalNow));
            check("layout: three events and no holiday = two chips (earliest first) and ''+1 more''", l3.Chips.Count(s => s.Row * 7 + s.Col == bi) == 2 && l3.More[bi] == 1 && l3.Chips.Where(s => s.Row * 7 + s.Col == bi).OrderBy(s => s.Lane).First().Label == "12:00 Alpha");
            var drawnTexts = cal.MonthOverlayForTest.Children.OfType<Grid>().SelectMany(g => g.Children.OfType<TextBlock>()).ToList();
            check("drawn: chip and bar titles use Text.Caption (11 px) and fit their 16 px line", drawnTexts.Count == lay.Segs.Count && drawnTexts.All(t => t.FontSize == 11 && t.ActualHeight <= 16.5 && t.TextTrimming == TextTrimming.CharacterEllipsis));
            var barRects = cal.MonthOverlayForTest.Children.OfType<Grid>().Select(g => g.Children.OfType<System.Windows.Shapes.Rectangle>().FirstOrDefault()).Where(r => r != null).ToList();
            check("drawn: every chip / bar has its coloured left mark in the colour of its kind", barRects.Count >= lay.Segs.Count - lay.Segs.Count(s => s.LeftOpen) && barRects.All(r => r.Fill is SolidColorBrush sb && keys.Any(k => Col(k) == sb.Color)));

            // ---- fixed size, whatever is in the month
            double gridH = cal.MonthGridElementForTest.ActualHeight;
            var topLeft = cal.MonthGridElementForTest.TranslatePoint(new Point(0, 0), cal);
            double ovH = cal.MonthOverlayForTest.ActualHeight;
            bool cellsFixed = VisualsOf<Grid>(cal.MonthGridElementForTest).Where(g => g.Tag is MonthCellVM).All(g => Math.Abs(g.ActualHeight - 80) < 0.5);
            cal.ShowMonthForTest(2027, 2, new DateTime(2027, 2, 10));
            await Task.Delay(300);
            var topLeft2 = cal.MonthGridElementForTest.TranslatePoint(new Point(0, 0), cal);
            check("no shift: an empty month (Feb 2027) has the same grid (504 high), the same cell size, the same overlay and the same position as a busy one",
                Math.Abs(gridH - 504) < 0.5 && Math.Abs(cal.MonthGridElementForTest.ActualHeight - gridH) < 0.5 && Math.Abs(cal.MonthOverlayForTest.ActualHeight - ovH) < 0.5 && Math.Abs(ovH - 504) < 0.5 && cellsFixed
                && VisualsOf<Grid>(cal.MonthGridElementForTest).Where(g => g.Tag is MonthCellVM).All(g => Math.Abs(g.ActualHeight - 80) < 0.5) && Math.Abs(topLeft.Y - topLeft2.Y) < 0.5 && cal.MonthOverlayForTest.Children.Count == 0);
            cal.ShowMonthForTest(2026, 10, new DateTime(2026, 10, 15));
            await Task.Delay(300);

            // ---- filter chips
            check("filter: the four chips are there (Lodge, Holidays, Raid resets, My game events), all on by default, and the bar shows with the WoW bar", cal.FilterBarVisibleForTest && new[] { "lodge", "holiday", "raidreset", "mine" }.All(g => cal.FilterButtonForTest(g).IsChecked == true && cal.FilterButtonForTest(g).IsEnabled)
                && !new Settings().CalHideLodge && !new Settings().CalHideHolidays && !new Settings().CalHideResets && !new Settings().CalHideMine);
            int barsAll = lay.Bars.Count(), chipsAll = cal.MonthLayoutForTest.Chips.Count();
            cal.ClickFilterForTest("holiday", false);
            await Task.Delay(300);
            cal.SetModeForTest(true); await Task.Delay(300);
            var ag = cal.AgendaGroupsForTest.SelectMany(g => g.Events).ToList();
            check("filter: Holidays off -> stored, no holiday cards in the agenda, no bars in the month; Lodge, resets and my events stay", settings.CalHideHolidays && ag.All(e => e.WowKind != "holiday") && ag.Any(e => !e.IsWow) && ag.Any(e => e.WowKind == "raidreset") && ag.Any(e => e.WowKind == "player")
                && cal.FilterButtonForTest("holiday").IsChecked != true);
            cal.ShowMonthForTest(2026, 10, new DateTime(2026, 10, 15)); await Task.Delay(300);
            check("filter: ... and the month has no bars, the Karazhan chip on the 15th now has room (no ''+N more'' there)", cal.MonthLayoutForTest.Bars.Count() == 0 && cal.MonthLayoutForTest.More[Ix(15)] == 0 && cal.MonthLayoutForTest.Chips.Any(s => s.Label == "20:00 Karazhan run"));
            cal.ClickFilterForTest("holiday", true);
            cal.ClickFilterForTest("raidreset", false); cal.ClickFilterForTest("mine", false);
            await Task.Delay(300);
            var mday = cal.MonthLayoutForTest;
            check("filter: Raid resets + My game events off -> no reset chip, no Karazhan / Guild night chip, holidays and Lodge still there", settings.CalHideResets && settings.CalHideMine && !mday.Chips.Any(s => s.Ev.WowKind == "raidreset" || s.Ev.WowKind == "player" || s.Ev.WowKind == "guild") && mday.Bars.Any() && mday.Chips.Any(s => !s.Ev.IsWow));
            cal.ClickFilterForTest("raidreset", true); cal.ClickFilterForTest("mine", true);
            cal.ClickFilterForTest("lodge", false);
            await Task.Delay(300);
            check("filter: Lodge off -> no Lodge chips; the strip above the chat still lists Lodge events (the filter is for the Calendar page)", settings.CalHideLodge && !cal.MonthLayoutForTest.Segs.Any(s => !s.Ev.IsWow) && Session.StripEvents(CalNow).Any(e => !e.IsWow));
            cal.ClickFilterForTest("lodge", true);
            await Task.Delay(300);
            check("filter: everything back on gives the same bars and chips as before", cal.MonthLayoutForTest.Bars.Count() == barsAll && cal.MonthLayoutForTest.Chips.Count() == chipsAll && !settings.CalHideLodge && !settings.CalHideHolidays && !settings.CalHideResets && !settings.CalHideMine);
            cal.ClickWowSwitchForTest(false);
            await Task.Delay(300);
            check("filter: the master switch 'Show in-game WoW events' off hides all three in-game kinds and greys their chips; Lodge stays (the switch keeps its meaning)",
                settings.WowEventsOff && !cal.FilterButtonForTest("holiday").IsEnabled && !cal.FilterButtonForTest("raidreset").IsEnabled && !cal.FilterButtonForTest("mine").IsEnabled && cal.FilterButtonForTest("lodge").IsEnabled
                && cal.MonthLayoutForTest.Segs.All(s => !s.Ev.IsWow) && cal.MonthLayoutForTest.Segs.Count > 0);
            cal.ClickWowSwitchForTest(true);
            await Task.Delay(300);
            check("filter: ... and on again brings back each chip exactly as the toggles had it", !settings.WowEventsOff && cal.FilterButtonForTest("holiday").IsEnabled && cal.MonthLayoutForTest.Bars.Count() == barsAll);
            var back = Util.FromJson<Settings>(Util.ToJson(new Settings { CalHideLodge = true, CalHideMine = true }));
            check("filter: the toggles are saved with the settings (round trip), older settings files read as all shown", back.CalHideLodge && back.CalHideMine && !back.CalHideHolidays && !back.CalHideResets && !Util.FromJson<Settings>("{}").CalHideLodge);

            // ---- old data: merged spans are not drawn as one band
            WriteWow(WowLua(OldMergedWowEvents(), -18000, 1));
            var old = WowCalendar.Read(settings.WowRoot);
            WowEvent OE(string t) => old.Events.First(e => e.Title == t);
            check("old format: the 35-48 day spans of weekly holidays are marked 'recurring' (Warsong Gulch, Darkspear, Fishing Extravaganza)", old.Version == 1 && OE("Call to Arms: Warsong Gulch").Recurring && OE("Call to Arms: Darkspear Islands").Recurring && OE("Stranglethorn Fishing Extravaganza").Recurring);
            check("old format: a 15-day holiday, a long holiday that is not a weekly one and a player event are left as they are", !OE("Hallow's End").Recurring && !OE("Midsummer Long Fair").Recurring && !OE("Karazhan run").Recurring);
            WriteWow(WowLua(OldMergedWowEvents(), -18000, 2));
            check("old format: the same spans in format 2 are trusted (the addon says they are one occurrence)", WowCalendar.Read(settings.WowRoot).Events.All(e => !e.Recurring));
            WriteWow(WowLua(OldMergedWowEvents(), -18000, 1));
            Session.ReloadWowEvents(true);
            await Task.Delay(400);
            cal.ShowMonthForTest(2026, 10, new DateTime(2026, 10, 15));
            await Task.Delay(400);
            var rec = Session.WowEvents.Where(e => e.Recurring).ToList();
            check("old format: recurring entries are no bands and no chips - nothing in the month grid, nothing in the day list", rec.Count == 3 && rec.All(e => !e.IsBand && e.TimeText == "Repeats weekly") && cal.MonthCellsForTest.All(c => !c.Cell.Bands.Any(b => b.Recurring) && !c.Cell.Events.Any(b => b.Recurring))
                && !cal.MonthLayoutForTest.Segs.Any(s => s.Ev.Recurring) && !cal.DayEventsForTest.Any(e => e.Recurring));
            check("old format: the month still draws the ones that are real (Hallow's End and the long fair as bars, Karazhan as a chip)", cal.MonthLayoutForTest.Bars.Any(s => s.Ev.Title == "Hallow's End") && cal.MonthLayoutForTest.Chips.Any(s => s.Label == "20:00 Karazhan run"));
            cal.SetModeForTest(true);
            await Task.Delay(400);
            var agOld = cal.AgendaGroupsForTest.SelectMany(g => g.Events).ToList();
            check("old format: the agenda lists each once, under Today (they are running), with a hint to update the addon in the caption", agOld.Count(e => e.Recurring) == 3 && cal.AgendaGroupsForTest[0].Events.Count(e => e.Recurring) == 3 && cal.WowCaptionForTest.Contains("1.9.1") && cal.WowCaptionForTest.Contains("/reload"));
            check("old format: the card says 'Repeats weekly', not a month-long range (" + string.Join(" / ", rec.Select(e => e.WhenLine)) + ")", rec.All(e => e.WhenLine.StartsWith("Repeats weekly") && !e.WhenLine.Contains(" - ")));
            WriteWow(WowLua(WowFixtureEvents()));
            Session.ReloadWowEvents(true);
            await Task.Delay(400);
            LodgePage.HideCalendarForTest();
        }
    }
}