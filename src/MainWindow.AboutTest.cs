using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // --selftest "about": Settings > About (2.77). The public changelog inside the exe (parsed sections = the file's "## x.y.z" headers, the
    // first one is this version, lead-ins, dates, the companion's notes, the manifest's addon notes) and its privacy scan (the shipped notes
    // hold no user paths, e-mails, IPs or tokens; the scanner catches planted ones), the About tab (the campfire centred at three widths, the
    // trademark line on one line when wide and wrapping when narrow), What's new (cards, this version marked, a newer version / addon update
    // banner, older versions on demand, the picker), the one-time note after an update, and the log viewer (levels and colours, exceptions and
    // stack frames, personal details hidden in the view and in Copy, search, Warnings / Errors only, live tail incl. a half-written line and a
    // rewritten file, a 20 000-line log stays virtualised). Fixtures only; pictures with ELANSHUB_SHOTS=1.
    public partial class MainWindow
    {
        async Task<string> AboutTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            void Check(string what, bool cond, string detail = null) { SelfTestClock.Mark(what); notes.Add((cond ? "ok   " : "FAIL ") + what + (!cond && detail != null ? " [" + detail + "]" : "")); ok &= cond; }
            string P(string n) => Path.Combine(dir, n);
            var savedW = Width; var savedH = Height; var savedMan = manifest; var savedSeen = settings.WhatsNewSeen; var savedState = WindowState;
            LogViewerWindow viewer = null;
            try
            {
                // ============================================================================================ the changelog inside the exe
                var md = Changelog.ReadResource(Changelog.OutpostResource);
                var txt = Changelog.ReadResource(Changelog.CompanionResource);
                Check("changelog: CHANGELOG.md rides inside the exe", !string.IsNullOrEmpty(md) && md.Contains("## 2.76.0"));
                var headers = Regex.Matches(md ?? "", @"^##\s+v?\d+(\.\d+)+", RegexOptions.Multiline).Count;
                var outpost = Changelog.Outpost;
                Check($"changelog: one section per '## x.y.z' header ({outpost.Count} of {headers})", outpost.Count == headers && headers > 50, outpost.Count + " vs " + headers);
                Check("changelog: the first section is this version (" + App.Version + ")", outpost.Count > 0 && outpost[0].Version == App.Version, outpost.FirstOrDefault()?.Version);
                Check("changelog: every section has notes; newest first", outpost.All(r => r.Items.Count > 0) && outpost.Zip(outpost.Skip(1), (a, b) => Util.CompareVersions(a.Version, b.Version) > 0).All(x => x),
                    string.Join(",", outpost.Where(r => r.Items.Count == 0).Select(r => r.Version)));
                var r276 = outpost.FirstOrDefault(r => r.Version == "2.76.0");
                Check("changelog: the bold lead-in is split from the text (2.76.0: 'Atlas Home is about you now.')", r276 != null && r276.Items[0].Lead == "Atlas Home is about you now." && r276.Items[0].Text.StartsWith("When you play"), r276?.Items[0].Lead);
                Check("changelog: a line without a lead-in keeps all its text", r276 != null && r276.Items.Any(i => i.Lead == null && i.Text.StartsWith("Fixed:")));
                var fx = Changelog.ParseMarkdown("# Title\nintro - not a note\n\n## 1.3.0 - 2026-01-05\n- **Lead.** text with `code` and **bold**\n  continued here\n- plain\n## v1.2.0 (2025-12-24)\n* star bullet\n## Notes\n- not a version\n## 1.1\n- short version");
                Check("changelog parser: dates ('- 2026-01-05' / '(2025-12-24)'), a continued bullet, '* ' bullets, a heading that is no version ends the section",
                    fx.Count == 3 && fx[0].Date == "2026-01-05" && fx[1].Date == "2025-12-24" && fx[1].Version == "1.2.0" && fx[0].Items.Count == 2 && fx[0].Items[0].Text.EndsWith("continued here")
                    && fx[1].Items.Count == 1 && fx[2].Version == "1.1" && fx[2].Date == null, string.Join(" | ", fx.Select(r => r.Version + ":" + r.Items.Count + ":" + r.Date)));
                var spans = Changelog.Spans("a `x.y` b **c** d `open");
                Check("changelog parser: inline `code` and **bold** spans, an unclosed one stays text",
                    spans.Count == 5 && spans[1].Key == Changelog.SpanKind.Code && spans[1].Value == "x.y" && spans[3].Key == Changelog.SpanKind.Bold && spans[3].Value == "c" && spans[4].Value == " d `open");
                var comp = Changelog.Companion;
                var compLines = Regex.Matches(txt ?? "", @"^\d+(\.\d+)+\s", RegexOptions.Multiline).Count;
                Check($"changelog: the Elan's Hub companion's notes ride along, newest first ({comp.Count} versions)", comp.Count == compLines && compLines > 10 && Util.CompareVersions(comp[0].Version, comp[comp.Count - 1].Version) > 0, comp.Count + " vs " + compLines);
                var fe = Changelog.FromEntries(new[] { new ChangeEntry { Version = "1.0.0", Text = "first" }, new ChangeEntry { Version = "1.10.0", Text = "- a\n- **b.** c" }, new ChangeEntry { Version = "1.0.0", Text = "second" }, new ChangeEntry { Version = "", Text = "x" } });
                Check("changelog: manifest notes grouped per version, newest first (1.10 > 1.0), bullets split, lead-ins found",
                    fe.Count == 2 && fe[0].Version == "1.10.0" && fe[0].Items.Count == 2 && fe[0].Items[1].Lead == "b." && fe[1].Items.Select(i => i.Text).SequenceEqual(new[] { "first", "second" }));

                // ============================================================================================ privacy of what is shown
                var mdProblems = Changelog.PrivacyProblems(md);
                var txtProblems = Changelog.PrivacyProblems(txt);
                Check("privacy: the shipped CHANGELOG.md holds no user paths, e-mails, IPs, account names or tokens", mdProblems.Count == 0, string.Join("; ", mdProblems.Take(5)));
                Check("privacy: the shipped companion CHANGELOG.txt neither", txtProblems.Count == 0, string.Join("; ", txtProblems.Take(5)));
                var planted = new[] { @"see C:\Users\bob\AppData\x", @"D:\Games\WoW\_classic_", @"WTF\Account\MYACC123\SavedVariables", "mail me: someone@example.dk", "server 192.168.2.54:3002", "ghp_abcdefghijklmnop1234", "token=abcd1234efgh", "Authorization: Bearer abcdefghijklmnop", "https://user:pw@host.example/", "http://nas.lan/lodge" };
                var missed = planted.Where(p => Changelog.PrivacyProblems(p).Count == 0).ToList();
                Check("privacy: the scanner catches each kind (user folder, local path, account folder, e-mail, IP, GitHub token, token=, bearer, password in a link, home-network host)", missed.Count == 0, string.Join(" | ", missed));
                var benign = new[] { "version 2.77.0 and 1.15.3", "`http://localhost` and `http://127.0.0.1` are allowed", "Settings > About", "WoW 1.0.4258.31", "https://github.com/Elanoran/ElanAddonHub", "WTF\\Account\\<account>" };
                var flagged = benign.Where(p => Changelog.PrivacyProblems(p).Count > 0).ToList();
                Check("privacy: version numbers, localhost, loopback and public links are not flagged", flagged.Count == 0, string.Join(" | ", flagged));
                Check("one-time note: shown after an update (older seen / none seen on an existing install), not on a first start, not twice",
                    WhatsNew.ShouldShow("2.76.0", "2.77.0", false) && WhatsNew.ShouldShow(null, "2.77.0", false) && !WhatsNew.ShouldShow(null, "2.77.0", true)
                    && !WhatsNew.ShouldShow("2.77.0", "2.77.0", false) && !WhatsNew.ShouldShow("2.78.0", "2.77.0", false));

                // ============================================================================================ the About tab
                WindowState = WindowState.Normal;
                ShowTab("settings"); SettingsPage.Show("about"); SettingsPage.ShowAboutTab("about");
                await Settle();
                Check("about: opens on the About tab (the campfire, credits), What's new hidden", await Until(() => SettingsPage.AboutPaneForTest.IsVisible && !SettingsPage.AboutNewsVisibleForTest));
                foreach (var w in new[] { 1100.0, 1400.0, 1900.0 })
                {
                    Width = w; Height = 860;
                    await Until(() => Math.Abs(ActualWidth - w) < 2, 1500);
                    UpdateLayout();
                    var pane = SettingsPage.AboutPaneForTest; var v = SettingsPage.AboutVideoForTest;
                    var at = v.TranslatePoint(new Point(0, 0), pane);
                    double left = at.X, right = pane.ActualWidth - at.X - v.ActualWidth;
                    Check($"about: the campfire is centred at {w:0} px (left {left:0}, right {right:0}, {v.ActualWidth:0} wide)", v.ActualWidth > 200 && Math.Abs(left - right) <= 2, $"pane {pane.ActualWidth:0}");
                    if (w == 1400)
                    {
                        // "How we look at it": shown above the trademark line, with the honest no-responsibility words, the same text as the README's section
                        var fp = SettingsPage.FairPlayTextForTest;
                        string readme = null;
                        for (var dirUp = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory); dirUp != null && readme == null; dirUp = dirUp.Parent)
                            if (File.Exists(Path.Combine(dirUp.FullName, "README.md")) && File.Exists(Path.Combine(dirUp.FullName, "ARTWORK.md"))) readme = File.ReadAllText(Path.Combine(dirUp.FullName, "README.md")).Replace("\r\n", "\n");
                        Check("about: \"How we look at it\" is shown: fair play, own risk, no responsibility, keep a copy of WTF, tell us",
                            SettingsPage.FairPlayShownForTest && fp.Contains("only Blizzard decides") && fp.Contains("at your own risk") && fp.Contains("can't take responsibility") && fp.Contains("WTF folder") && fp.Contains("tell us"), fp);
                        Check("about: the README's \"How we look at it\" has the same words (skipped when the source folder is not found)",
                            readme == null || fp.Split(new[] { SettingsPage.FairPlaySep }, StringSplitOptions.None).All(par => readme.Contains(par)));
                    }
                    var d = SettingsPage.AboutDisclaimerForTest;
                    var lines = Math.Round(d.ActualHeight / 14.0);
                    if (w >= 1400) Check($"about: the trademark line is one line at {w:0} px ({d.ActualHeight:0} px high)", d.ActualHeight > 0 && d.ActualHeight < 20, d.ActualHeight.ToString("0"));
                    var dl = d.TranslatePoint(new Point(0, 0), pane).X;
                    Check($"about: the trademark line is centred and inside the page at {w:0} px", dl >= 0 && dl + d.ActualWidth <= pane.ActualWidth + 1 && Math.Abs(dl - (pane.ActualWidth - dl - d.ActualWidth)) <= 2);
                    if (w == 1400) Snapshot(P("about-tab-1400.png"));
                }
                // narrow: the page column is narrower than the line, it wraps (and is not cut)
                SettingsPage.AboutDisclaimerForTest.MaxWidth = 360; UpdateLayout();
                var dn = SettingsPage.AboutDisclaimerForTest;
                Check($"about: on a narrow page the trademark line wraps instead of being cut ({dn.ActualHeight:0} px high)", dn.ActualHeight >= 26 && dn.ActualWidth <= 361);
                dn.ClearValue(FrameworkElement.MaxWidthProperty); UpdateLayout();

                // ============================================================================================ What's new
                manifest = new Manifest
                {
                    Hub = new HubInfo { Version = App.Version },
                    Addons = new List<AddonInfo>
                    {
                        new AddonInfo { Id = "ElansHub", Name = "Elan's Hub", Version = "9.1.0", Changelog = new List<ChangeEntry> { new ChangeEntry { Version = "9.1.0", Text = "**Newer.** from the manifest" }, new ChangeEntry { Version = "9.0.0", Text = "older" } } },
                        new AddonInfo { Id = "ElansTestNotes", Name = "Elan's Test Notes", Version = "2.0.0", Changelog = new List<ChangeEntry> { new ChangeEntry { Version = "1.0.0", Text = "one" }, new ChangeEntry { Version = "2.0.0", Text = "two" } } },
                        new AddonInfo { Id = "ElansNoNotes", Name = "No notes", Version = "1.0.0" },
                    },
                };
                SettingsPage.ShowAboutTab("news");
                await Settle();
                var list = SettingsPage.NewsListForTest;
                Check("what's new: the tab shows the first 8 versions as cards", await Until(() => SettingsPage.AboutNewsVisibleForTest && list.Children.Count == Math.Min(8, outpost.Count)), list.Children.Count.ToString());
                var first = list.Children.OfType<FrameworkElement>().FirstOrDefault();
                Check("what's new: the first card is this version and says so ('This version'); no other card does",
                    first != null && (string)first.Tag == App.Version && Pills(first).Contains(SettingsPage.ThisVersion) && list.Children.OfType<FrameworkElement>().Skip(1).All(c => !Pills(c).Contains(SettingsPage.ThisVersion)));
                Check("what's new: the lead-in is semibold, the rest Secondary", first != null && Texts(first).Any(t => t.Inlines.OfType<Run>().Any(r => r.FontWeight == FontWeights.SemiBold) && t.Inlines.OfType<Run>().Any(r => r.FontWeight != FontWeights.SemiBold)));
                Check("what's new: no newer-version banner while this is the newest", SettingsPage.NewsBannerForTest == null, SettingsPage.NewsBannerForTest);
                var more = SettingsPage.NewsMoreForTest;
                Check("what's new: 'Show older versions' says how many are left", more.Visibility == Visibility.Visible && ((string)more.Content).Contains((outpost.Count - 8) + " more"), (string)more.Content);
                more.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Check("what's new: it adds 12 more", await Until(() => list.Children.Count == Math.Min(20, outpost.Count)), list.Children.Count.ToString());
                Snapshot(P("about-whatsnew.png"));
                manifest.Hub.Version = "9.9.9";
                SettingsPage.PickNewsForTest("outpost");
                Check("what's new: a newer Outpost from the update check is announced (the notes come with it)", (SettingsPage.NewsBannerForTest ?? "").Contains("9.9.9 is ready"), SettingsPage.NewsBannerForTest);
                var picks = SettingsPage.NewsPickForTest.Children.OfType<RadioButton>().Select(r => (string)r.Tag).ToList();
                Check("what's new: the picker lists the Outpost and every addon with notes (not one without)", picks.SequenceEqual(new[] { "outpost", "ElansHub", "ElansTestNotes" }), string.Join(",", picks));
                SettingsPage.PickNewsForTest("ElansHub"); UpdateLayout();
                var hubFirst = list.Children.OfType<FrameworkElement>().FirstOrDefault();
                Check("what's new: Elan's Hub shows the manifest's notes (newer than the copy inside the exe), not installed = a banner", hubFirst != null && (string)hubFirst.Tag == "9.1.0" && (SettingsPage.NewsBannerForTest ?? "").Contains("not installed"));
                SettingsPage.PickNewsForTest("ElansTestNotes"); UpdateLayout();
                Check("what's new: an addon's notes, newest first, the latest marked", list.Children.Count == 2 && (string)((FrameworkElement)list.Children[0]).Tag == "2.0.0" && Pills((FrameworkElement)list.Children[0]).Contains(SettingsPage.Latest));
                manifest = null;
                SettingsPage.PickNewsForTest("outpost");
                picks = SettingsPage.NewsPickForTest.Children.OfType<RadioButton>().Select(r => (string)r.Tag).ToList();
                Check("what's new: before any update check the companion's notes inside the exe are offered", picks.SequenceEqual(new[] { "outpost", "ElansHub" }) && SettingsPage.NewsBannerForTest == null, string.Join(",", picks));
                manifest = savedMan;

                // ============================================================================================ the one-time note
                SettingsPage.ShowAboutTab("about");
                ShowTab("addons");
                await Settle();
                settings.WhatsNewSeen = "2.0.0";
                var shown = SettingsPage.ShowWhatsNewNote();
                await Settle();
                var note = SettingsPage.NoteForTest;
                Check("note: after an update the 'What's new in " + App.Version + "' card shows in the window, remembered at once", shown && note != null && note.CardForTest.IsVisible && settings.WhatsNewSeen == App.Version
                    && Texts(note.CardForTest).Any(t => t.Text.Contains("What's new in") && t.Text.Contains(App.Version)));
                if (note != null) await Until(() => note.CardForTest.Opacity >= 1 && note.CardForTest.RenderTransform.Value.IsIdentity, 2000);   // the open animation (scale 0.96 -> 1) has finished
                var nc = note?.CardForTest.TranslatePoint(new Point(0, 0), (UIElement)Content) ?? new Point();
                Check("note: in the bottom right corner, 24 from the edges", note != null && Math.Abs(((FrameworkElement)Content).ActualWidth - nc.X - note.CardForTest.ActualWidth - 24) < 2 && Math.Abs(((FrameworkElement)Content).ActualHeight - nc.Y - note.CardForTest.ActualHeight - 24) < 2, $"at {nc.X:0} x {nc.Y:0}, card {note?.CardForTest.ActualWidth:0} x {note?.CardForTest.ActualHeight:0}, content {((FrameworkElement)Content).ActualWidth:0} x {((FrameworkElement)Content).ActualHeight:0}, adorned {note?.AdornedElement.RenderSize}");
                Check("note: the What's new tab gets a dot until it is opened", SettingsPage.NewsDotForTest);
                if (note != null) SnapshotElement(note.CardForTest, P("about-note.png"));
                Snapshot(P("about-note-window.png"));
                note?.OpenForTest();
                await Settle();
                Check("note: 'See what's new' opens Settings > About > What's new and goes away", await Until(() => SettingsPage.IsVisible && SettingsPage.AboutNewsVisibleForTest) && SettingsPage.NoteForTest == null && !SettingsPage.NewsDotForTest);

                // ============================================================================================ the log viewer
                var logPath = P("about-hub.log");
                var red = new LogRedactor("Testy", "TESTPC", @"C:\Users\Testy");
                var sb = new StringBuilder();
                sb.Append("2026-10-16 10:00:00  atlas: 28 mobs, 46 vendors, 302 finds from the lodge (rev 10561)\r\n");
                sb.Append("2026-10-16 10:00:01  releases api failed, using stable: 403 rate limit\r\n");
                sb.Append("2026-10-16 10:00:02  curseforge didn't confirm the link in 2 s; will resend once\r\n");
                sb.Append("2026-10-16 10:00:03  crash: System.InvalidOperationException: Den angivne Visual er ikke et overordnet element\r\n");
                sb.Append("   ved System.Windows.Media.Visual.TrySimpleTransformToAncestor(Visual ancestor)\r\n");
                sb.Append("   at ElansAddonHub.MainWindow.Foo(Object sender) in C:\\Users\\Testy\\src\\MainWindow.cs:line 12\r\n");
                sb.Append("2026-10-16 10:00:04  health read failed: C:\\Users\\Testy\\WoW\\WTF\\Account\\MYACCOUNT\\SavedVariables\\ElansHub.lua: unexpected end\r\n");
                sb.Append("2026-10-16 10:00:05  lodge: connected to 192.168.2.54 as someone@example.dk on TESTPC token=abcdef123456\r\n");
                sb.Append("2026-10-16 10:00:06  atlas: shared 608 entries (0 finds): accepted 608, unchanged 0, rejected 0\r\n");
                sb.Append("2026-10-16 10:00:07  lodge send: closing\r\n");
                sb.Append("System.Net.WebSockets.WebSocketException: The remote party closed the connection\r\n");
                File.WriteAllText(logPath, sb.ToString());
                viewer = new LogViewerWindow(logPath, red) { WindowStartupLocation = WindowStartupLocation.CenterScreen };
                viewer.Show();
                await Settle(); await Task.Delay(100); viewer.UpdateLayout();
                var all = viewer.AllForTest;
                Check("log: every line read (11), entries split by the time stamp (8)", all.Count == 11 && all.Select(l => l.Entry).Distinct().Count() == 8, all.Count + " lines: " + string.Join(",", all.Select(l => l.Entry + (l.Continuation ? "c" : ""))));
                LogLine Find(string s) => all.FirstOrDefault(l => l.Text.Contains(s));
                Check("log: levels - 'failed' is an error, 'will resend' a warning, counts of 0 rejected are info, plain lines info",
                    Find("releases api")?.Level == LogLevel.Error && Find("will resend")?.Level == LogLevel.Warning && Find("rejected 0")?.Level == LogLevel.Info && Find("28 mobs")?.Level == LogLevel.Info);
                Check("log: a crash and its stack (Danish 'ved' and English 'at') are one error entry", all.Where(l => l.Entry == Find("crash:")?.Entry).Count() == 3 && all.Where(l => l.Entry == Find("crash:")?.Entry).All(l => l.Level == LogLevel.Error) && all.Count(l => l.Continuation) == 3);
                Check("log: an exception on the line under a plain message makes that message an error too", Find("lodge send: closing")?.Level == LogLevel.Error);
                var plainAll = string.Join("\n", all.Select(l => l.Plain));
                var leaks = new[] { "Testy", "TESTPC", "MYACCOUNT", "someone@example.dk", "192.168.2.54", "abcdef123456" }.Where(x => plainAll.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                Check("log: personal details are hidden (user folder, user and PC name, WoW account, e-mail, IP, token)", leaks.Count == 0, string.Join(",", leaks));
                Check("log: ... and replaced by what they were (%USERPROFILE%, <account>, <e-mail>, <ip>, <pc>, token=<hidden>)",
                    new[] { @"%USERPROFILE%\WoW\WTF\Account\<account>\SavedVariables", "<e-mail>", "<ip>", "<pc>", "token=<hidden>", @"in %USERPROFILE%\src\MainWindow.cs:line 12" }.All(x => plainAll.Contains(x)), plainAll);
                var hl = Find("health read");
                Check("log: each hidden bit is marked where it is in the line", hl != null && hl.Redactions.Count == 2 && hl.Redactions.All(r => r.Start >= 0 && r.Start + r.Length <= hl.Text.Length)
                    && hl.Text.Substring(hl.Redactions[0].Start, hl.Redactions[0].Length) == "%USERPROFILE%" && hl.Text.Substring(hl.Redactions[1].Start, hl.Redactions[1].Length) == "<account>",
                    hl == null ? "?" : string.Join(",", hl.Redactions.Select(r => r.Start + "+" + r.Length)));
                Check("log: the header says how many details are hidden", (viewer.HiddenForTest ?? "").StartsWith(all.Sum(l => l.Redactions.Count) + " personal details hidden"), viewer.HiddenForTest);
                // colours
                var sp1 = LogParser.Spans(Find("releases api"));
                var spC = LogParser.Spans(Find("crash:"));
                var spAt = LogParser.Spans(all.First(l => l.Continuation && l.Text.TrimStart().StartsWith("at ")));
                var spA = LogParser.Spans(Find("28 mobs"));
                Check("log colours: the category, the level's words, exception names, stack frames (at / method / file), numbers, hidden bits",
                    spA.Any(s => s.Key == LogSpan.Category && s.Value == "atlas") && spA.Any(s => s.Key == LogSpan.Number && s.Value == "28")
                    && sp1.Any(s => s.Key == LogSpan.Error && s.Value == "failed") && spC.Any(s => s.Key == LogSpan.Exception && s.Value == "System.InvalidOperationException")
                    && spAt.Any(s => s.Key == LogSpan.StackAt && s.Value == "at") && spAt.Any(s => s.Key == LogSpan.StackMethod && s.Value.StartsWith("ElansAddonHub.MainWindow.Foo("))
                    && spAt.Any(s => s.Key == LogSpan.StackFile && s.Value.EndsWith("MainWindow.cs:line 12")) && LogParser.Spans(hl).Any(s => s.Key == LogSpan.Redacted && s.Value == "<account>"),
                    string.Join(" | ", spAt.Select(s => s.Key + ":" + s.Value)));
                Brush R(string k) => (Brush)Application.Current.Resources[k];
                Check("log colours: time Tertiary, errors Danger, warnings Warn, info text Secondary, error text Primary, a red / amber bar for error / warning entries",
                    LogRowView.SpanBrush(LogSpan.Time, LogLevel.Info) == R("Brush.Text.Tertiary") && LogRowView.SpanBrush(LogSpan.Error, LogLevel.Error) == R("Brush.Danger")
                    && LogRowView.SpanBrush(LogSpan.Warning, LogLevel.Warning) == R("Brush.Warn") && LogRowView.SpanBrush(LogSpan.Text, LogLevel.Info) == R("Brush.Text.Secondary")
                    && LogRowView.SpanBrush(LogSpan.Text, LogLevel.Error) == R("Brush.Text.Primary") && LogRowView.BarBrush(LogLevel.Error) == R("Brush.Danger") && LogRowView.BarBrush(LogLevel.Warning) == R("Brush.Warn"));
                var rows = Descendants<LogRowView>(viewer.ListForTest).ToList();
                var crashRow = rows.FirstOrDefault(r => (r.DataContext as LogRow)?.Line.Text.Contains("crash:") == true);
                var crashRuns = crashRow?.TextForTest.Inlines.OfType<Run>().ToList();
                Check("log rows: drawn in the mono font, time first in Tertiary, the exception name semibold in Danger, the redacted bit on the warn tint",
                    crashRow != null && crashRow.TextForTest.FontFamily == (FontFamily)Application.Current.Resources["Font.Mono"] && crashRuns[0].Text.StartsWith("2026-10-16 10:00:03") && crashRuns[0].Foreground == R("Brush.Text.Tertiary")
                    && crashRuns.Any(r => r.Text == "System.InvalidOperationException" && r.Foreground == R("Brush.Danger") && r.FontWeight == FontWeights.SemiBold)
                    && rows.SelectMany(r => r.TextForTest.Inlines.OfType<Run>()).Any(r => r.Text == "%USERPROFILE%" && r.Background == R("Brush.Warn.Tint")), rows.Count + " rows");
                // filters
                viewer.SetLevelForTest(LogLevel.Error); await Settle();
                Check("log filter: Errors only = the 4 error entries with their stack lines (7 lines)", viewer.ShownForTest.Count == 7 && viewer.ShownForTest.All(r => r.Line.Level == LogLevel.Error), viewer.ShownForTest.Count.ToString());
                viewer.SetLevelForTest(LogLevel.Warning); await Settle();
                Check("log filter: Warnings = warnings and errors (8 lines)", viewer.ShownForTest.Count == 8, viewer.ShownForTest.Count.ToString());
                viewer.SetLevelForTest(LogLevel.Info); viewer.SetSearchForTest("rate LIMIT"); await Settle();
                Check("log search: words in any case", viewer.ShownForTest.Count == 1 && viewer.ShownForTest[0].Line.Text.Contains("403 rate limit"));
                Check("log search: the hits are marked (accent tint)", LogRowView.Hits("403 rate limit", new[] { "rate", "LIMIT" }).Count(h => h.Item2) == 2);
                viewer.SetSearchForTest("Testy"); await Settle();
                Check("log search: a hidden detail cannot be searched for", viewer.ShownForTest.Count == 0);
                viewer.SetSearchForTest("lodge closed"); await Settle();
                Check("log search: every word must be in the line", viewer.ShownForTest.Count == 0);
                viewer.SetSearchForTest(""); await Settle();
                // copy
                viewer.CopyLines();
                Check("log copy: everything shown, redacted, one line each", viewer.LastCopied == string.Join("\r\n", viewer.ShownForTest.Select(r => r.Line.Plain)) && !viewer.LastCopied.Contains("Testy") && viewer.LastCopied.Contains("%USERPROFILE%"));
                viewer.ListForTest.SelectedItems.Clear();
                viewer.ListForTest.SelectedItems.Add(viewer.ShownForTest[3]); viewer.ListForTest.SelectedItems.Add(viewer.ShownForTest[1]);
                viewer.CopyLines();
                Check("log copy: with a selection only those lines, in the log's order", viewer.LastCopied == viewer.ShownForTest[1].Line.Plain + "\r\n" + viewer.ShownForTest[3].Line.Plain, viewer.LastCopied);
                viewer.ListForTest.SelectedItems.Clear();
                if (viewer.RootForTest != null) SnapshotElement(viewer.RootForTest, P("about-log.png"));
                // live tail
                int before = viewer.AllForTest.Count;
                File.AppendAllText(logPath, "2026-10-16 10:01:00  atlas: new line one\r\n2026-10-16 10:01:01  check failed: timed out\r\n");
                Check("log tail: new lines appear while it is open", await Until(() => viewer.AllForTest.Count == before + 2 && viewer.ShownForTest.Last().Line.Text.Contains("check failed"), 4000), viewer.AllForTest.Count.ToString());
                Check("log tail: it follows the end", viewer.FollowForTest && await Until(() => viewer.AtEndForTest, 1500));
                File.AppendAllText(logPath, "2026-10-16 10:01:02  half a li");
                viewer.Tail();
                Check("log tail: a half-written line waits", viewer.AllForTest.Count == before + 2);
                File.AppendAllText(logPath, "ne\r\n");
                viewer.Tail();
                Check("log tail: ... and comes in whole", viewer.AllForTest.Count == before + 3 && viewer.AllForTest.Last().Text == "half a line", viewer.AllForTest.Last().Text);
                File.WriteAllText(logPath, "2026-10-16 11:00:00  hub started\r\n");
                viewer.Tail();
                Check("log tail: a rewritten (shorter) log is read again from the start", viewer.AllForTest.Count == 1 && viewer.ShownForTest.Count == 1);
                viewer.Close(); viewer = null;
                // a big log stays quick: the list virtualises
                var big = new StringBuilder();
                for (int i = 0; i < 20000; i++) big.Append("2026-10-16 12:").Append((i / 60 % 60).ToString("00")).Append(':').Append((i % 60).ToString("00")).Append(i % 97 == 0 ? "  upload failed: error " : "  atlas: line ").Append(i).Append("\r\n");
                File.WriteAllText(logPath, big.ToString());
                var sw = System.Diagnostics.Stopwatch.StartNew();
                viewer = new LogViewerWindow(logPath, red) { WindowStartupLocation = WindowStartupLocation.CenterScreen };
                viewer.Show(); await Settle(); viewer.UpdateLayout();
                var openMs = sw.ElapsedMilliseconds;
                var realized = Descendants<LogRowView>(viewer.ListForTest).Count();
                Check($"log: 20 000 lines open in {openMs} ms with {realized} rows drawn (virtualised)", viewer.AllForTest.Count == 20000 && realized > 5 && realized < 200 && openMs < 8000);
                viewer.SetLevelForTest(LogLevel.Error); await Settle();
                Check("log: Errors only on the big log", viewer.ShownForTest.Count == Enumerable.Range(0, 20000).Count(i => i % 97 == 0));
                viewer.Close(); viewer = null;
            }
            catch (Exception e) { ok = false; notes.Add("crashed: " + e); }
            finally
            {
                try { viewer?.Close(); } catch { }
                SettingsPage.CloseWhatsNewNote();
                manifest = savedMan; settings.WhatsNewSeen = savedSeen;
                Width = savedW; Height = savedH; WindowState = savedState;
                SettingsPage.ShowAboutTab("about");
            }
            return "about selftest: " + (ok ? "PASSED" : "FAILED") + "\r\n  " + string.Join("\r\n  ", notes);
        }

        static List<string> Pills(FrameworkElement card) => Descendants<Border>(card).Select(b => b.Tag as string).Where(t => t != null).ToList();

        static IEnumerable<TextBlock> Texts(DependencyObject root) => Descendants<TextBlock>(root);

        static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null) yield break;
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var c = VisualTreeHelper.GetChild(root, i);
                if (c is T t) yield return t;
                foreach (var d in Descendants<T>(c)) yield return d;
            }
        }
    }
}
