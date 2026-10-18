using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // --selftest "activity" (2.96): the background activity tracker (Services/Activity.cs: tokens, keys, counting, names, many threads at once, waiting
    // for saves), the ring in the rail above Settings (show delay, minimum time, tooltip, turning only with Windows animations on, game-file reading
    // folded in from GameDataProgress), a few wired jobs, and the close guard (MainWindow.Activity.cs FinishUp) with a fake long save: nothing to wait
    // for, a save that ends by itself (dialog closes, the Outpost may go), the cap running out, and "Close anyway". Screenshots with ELANSHUB_SHOTS=1
    // (activity-rail.png, activity-rail-zoom.png, activity-finishing.png).
    public partial class MainWindow
    {
        async Task<string> ActivityTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            void Check(string what, bool cond, string detail = null) { SelfTestClock.Mark(what); notes.Add((cond ? "ok   " : "FAIL ") + what + (!cond && detail != null ? " [" + detail + "]" : "")); ok &= cond; }
            int savedDelay = ActivityShowDelayMs, savedMin = ActivityMinShownMs;
            var tokens = new List<Activity.Token>();
            Activity.Token T(string text, Activity.Kind kind = Activity.Kind.Work) { var t = Activity.Begin(text, kind); tokens.Add(t); return t; }
            bool Has(string text) => Activity.Now().Lines.Contains(text);
            try
            {
                // ============================================================================================================ the tracker
                var a = T("Test: reading a file");
                var b = T("Test: saving a file", Activity.Kind.Save);
                var v = Activity.Now();
                Check("tracker: Begin adds a running entry with its name; Save entries are counted apart", Has("Test: reading a file") && Has("Test: saving a file") && v.SaveLines.Contains("Test: saving a file") && !v.SaveLines.Contains("Test: reading a file"));
                Check("tracker: saves are named first in the list (the close guard and the tooltip lead with what must finish)", v.Lines.ToList().IndexOf("Test: saving a file") < v.Lines.ToList().IndexOf("Test: reading a file"));
                var c = T("Test: reading a file");
                Check("tracker: two jobs with the same name count twice but are named once", Activity.Now().Lines.Count(l => l == "Test: reading a file") == 1 && Activity.Now().Count >= 3);
                int before = Activity.Now().Count;
                a.Dispose(); a.Dispose(); Activity.End(a); Activity.End(null);
                Check("tracker: ending a token twice (or null) is harmless and ends it once", Activity.Now().Count == before - 1 && Has("Test: reading a file") && a.Ended);
                c.Dispose(); b.Dispose();
                Check("tracker: all ended -> the names are gone", !Has("Test: reading a file") && !Has("Test: saving a file"));

                Activity.Hold("test.key", "Test: waiting for an answer", Activity.Kind.Save);
                Activity.Hold("test.key", "Test: still waiting", Activity.Kind.Save);
                Check("tracker: Hold with the same key keeps ONE entry and renames it", Activity.IsHeld("test.key") && Has("Test: still waiting") && !Has("Test: waiting for an answer"));
                Activity.Release("test.key"); Activity.Release("test.key"); Activity.Release("no.such.key");
                Check("tracker: Release ends it; again, or an unknown key, is harmless", !Activity.IsHeld("test.key") && !Has("Test: still waiting"));

                var many = Enumerable.Range(0, 7).Select(i => T("Test: job " + i)).ToList();
                var tip = Activity.Describe(Activity.Now());
                Check("tracker: the tooltip names at most " + Activity.MaxLines + " jobs, then 'and N more'", tip.Split('\n').Length == Activity.MaxLines + 1 && tip.Split('\n').Last().StartsWith("and ", StringComparison.Ordinal), tip.Replace("\n", " | "));
                foreach (var t in many) t.Dispose();
                Check("tracker: nothing running -> no tooltip text", Activity.Describe(new Activity.View()) == "");

                int baseCount = Activity.Now().Count, changed = 0;
                Action onChange = () => Interlocked.Increment(ref changed);
                Activity.Changed += onChange;
                try
                {
                    await Task.WhenAll(Enumerable.Range(0, 8).Select(n => Task.Run(() =>
                    {
                        for (int i = 0; i < 400; i++)
                        {
                            using (Activity.Begin("Test: thread " + n, i % 2 == 0 ? Activity.Kind.Save : Activity.Kind.Work)) { }
                            Activity.Hold("test.thread." + n, "Test: held " + n); Activity.Release("test.thread." + n);
                        }
                    })));
                }
                finally { Activity.Changed -= onChange; }
                Check("tracker: 8 threads x 400 begin/end + hold/release -> back to where it was, every change told", Activity.Now().Count == baseCount && changed >= 8 * 400 * 4 && !Activity.Now().Lines.Any(l => l.StartsWith("Test: thread", StringComparison.Ordinal) || l.StartsWith("Test: held", StringComparison.Ordinal)), "count " + Activity.Now().Count + " vs " + baseCount + ", changes " + changed);

                Exception thrown = null;
                try { await Activity.Track("Test: a job that fails", Activity.Kind.Save, async () => { await Task.Delay(20); throw new InvalidOperationException("boom"); }); } catch (Exception e) { thrown = e; }
                Check("tracker: Track ends the entry when the job throws, and the error still reaches the caller", thrown is InvalidOperationException && !Has("Test: a job that fails"));
                int r = await Activity.Track("Test: a job with a result", Activity.Kind.Work, () => Task.Run(() => 42));
                Check("tracker: Track<T> hands back the result", r == 42 && !Has("Test: a job with a result"));

                if (Activity.Now().Saves == 0)
                {
                    var sw = Stopwatch.StartNew();
                    Check("tracker: WaitForSaves with no save running returns true at once", await Activity.WaitForSaves(TimeSpan.FromSeconds(5)) && sw.ElapsedMilliseconds < 200, sw.ElapsedMilliseconds + " ms");
                    var s1 = T("Test: a long save", Activity.Kind.Save);
                    sw.Restart();
                    Check("tracker: WaitForSaves gives up (false) when the cap runs out", !await Activity.WaitForSaves(TimeSpan.FromMilliseconds(300)) && sw.ElapsedMilliseconds >= 280, sw.ElapsedMilliseconds + " ms");
                    _ = Task.Run(async () => { await Task.Delay(250); s1.Dispose(); });
                    sw.Restart();
                    Check("tracker: WaitForSaves returns true soon after the last save ends on another thread", await Activity.WaitForSaves(TimeSpan.FromSeconds(5)) && sw.ElapsedMilliseconds < 1500, sw.ElapsedMilliseconds + " ms");
                    var w1 = T("Test: just reading");
                    Check("tracker: Work does not hold up WaitForSaves", await Activity.WaitForSaves(TimeSpan.FromMilliseconds(500)));
                    w1.Dispose();
                }
                else Check("tracker: no other save runs during the test (precondition)", false, string.Join(", ", Activity.Now().SaveLines));

                // ============================================================================================================ wired jobs
                var seen = new List<string>();
                Action look = () => { lock (seen) seen.AddRange(Activity.Now().Lines); };
                Activity.Changed += look;
                try { SharedDataFile.Write(null, null, null, DateTime.UtcNow); }
                finally { Activity.Changed -= look; }
                Check("wired: writing SharedData.lua for the add-ons is a Save job ('Writing data for your add-ons') and ends", seen.Contains("Writing data for your add-ons") && !Has("Writing data for your add-ons"));
                GameDataProgress.Begin("activitytest", "Test data", "Reading test tables");
                await Settle();
                Check("wired: reading the game files (GameDataProgress) shows as one entry while it runs", Activity.IsHeld("gamedata") && Activity.Now().Lines.Any(l => l.Length > 0));
                GameDataProgress.End("activitytest");
                await Settle();
                Check("wired: ... and leaves when the game data is done (unless other game data still reads)", !Activity.IsHeld("gamedata") || GameDataProgress.Now().Busy);

                // ============================================================================================================ the ring in the rail
                ActivityShowDelayMs = 300; ActivityMinShownMs = 700;
                ShowTab("addons");
                await Settle();
                if (Activity.Now().Busy) await Task.Delay(1200);         // let the app's own background jobs settle when it can
                bool idleStart = !Activity.Now().Busy;
                if (idleStart)
                {
                    UpdateActivity(); await Task.Delay(ActivityMinShownMs + 200); await Settle();
                    Check("ring: hidden while nothing runs (takes no room)", !RailActivityShownForTest && RailActivity.Visibility == Visibility.Collapsed && RailActivity.ActualHeight == 0);
                    var quick = T("Test: a quick job"); await Task.Delay(40); quick.Dispose();
                    await Task.Delay(ActivityShowDelayMs + 150); await Settle();
                    Check("ring: a job shorter than the show delay never shows it (no flicker)", !RailActivityShownForTest && RailActivity.Visibility == Visibility.Collapsed);
                }
                var job = T("Saving Atlas finds", Activity.Kind.Save);
                var job2 = T("Building the world map");
                await Settle();
                Check("ring: not shown in the first " + ActivityShowDelayMs + " ms", !idleStart || !RailActivityShownForTest);
                await Task.Delay(ActivityShowDelayMs + 200); await Settle();
                Check("ring: shown once a job runs longer, above the Settings gear", RailActivityShownForTest && RailActivity.Visibility == Visibility.Visible && RailActivity.ActualHeight > 0
                    && RailActivity.TranslatePoint(new Point(0, 0), this).Y < TabSettings.TranslatePoint(new Point(0, 0), this).Y);
                Check("ring: the tooltip says in plain words what runs", RailActivityTipForTest.Contains("Saving Atlas finds") && RailActivityTipForTest.Contains("Building the world map"), RailActivityTipForTest.Replace("\n", " | "));
                Check("ring: it turns only when Windows animations are on (Motion.Enabled = " + Motion.Enabled + ") and the window is visible", RailActivitySpinningForTest == (Motion.Enabled && IsVisible && WindowState != WindowState.Minimized));
                Check("ring: drawn in theme colours (track Brush.Divider, arc Brush.Text.Secondary)", ReferenceEquals(RailActivityArc.Stroke, FindResource("Brush.Text.Secondary")) && RailActivity.Children.OfType<System.Windows.Shapes.Ellipse>().Any(e => ReferenceEquals(e.Stroke, FindResource("Brush.Divider"))));
                if (SelfTestClock.ShotsOn)
                {
                    await Task.Delay(Motion.Enabled ? 250 : 0); await Settle();
                    Snapshot(Path.Combine(dir, "activity-rail.png"));
                    ShotOf(this, Path.Combine(dir, "activity-rail-zoom.png"), new Rect(0, ActualHeight - 230, 72, 230), 4);
                }
                WindowState = WindowState.Minimized; await Settle();
                Check("ring: stands still while the window is minimised (costs nothing)", !RailActivitySpinningForTest);
                WindowState = WindowState.Normal; Activate(); await Settle();
                Check("ring: turns again when the window is back (with animations on)", RailActivitySpinningForTest == Motion.Enabled);
                job.Dispose(); job2.Dispose();
                if (idleStart)
                {
                    await Task.Delay(ActivityMinShownMs + Motion.FastMs + 300); await Settle();
                    Check("ring: hidden again after the jobs end (at least " + ActivityMinShownMs + " ms shown), turning stopped", !RailActivityShownForTest && !RailActivitySpinningForTest && RailActivity.Visibility == Visibility.Collapsed);
                }

                // ============================================================================================================ the close guard
                if (Activity.Now().Saves == 0)
                {
                    var sw = Stopwatch.StartNew();
                    var res = await FinishUp(TimeSpan.FromSeconds(10));
                    Check("close: nothing to save -> closes at once, no dialog", res == FinishResult.Nothing && sw.ElapsedMilliseconds < 200 && FinishDialogForTest == null, res + ", " + sw.ElapsedMilliseconds + " ms");

                    // a save that is about to end: a moment of waiting, no dialog
                    var soon = T("Test: a short save", Activity.Kind.Save);
                    _ = Task.Run(async () => { await Task.Delay(150); soon.Dispose(); });
                    bool sawDialog = false;
                    var watch = DispatcherWatch(() => { if (FinishDialogForTest != null) sawDialog = true; });
                    res = await FinishUp(TimeSpan.FromSeconds(10));
                    watch.Stop();
                    Check("close: a save that ends within a moment -> no dialog, then close", res == FinishResult.Finished && !sawDialog, res.ToString());

                    // the fake long job: the dialog shows what is being saved, and closes by itself when it is done
                    var fake = T("Saving your Atlas finds", Activity.Kind.Save);
                    sw.Restart();
                    var run = FinishUp(TimeSpan.FromSeconds(10));
                    for (int i = 0; i < 40 && FinishDialogForTest == null; i++) await Task.Delay(50);
                    var d = FinishDialogForTest;
                    Check("close: a long save -> the 'Finishing up' dialog opens", d != null && d.IsVisible && d.TitleText.Text == "Finishing up");
                    if (d != null)
                    {
                        Check("close: it names what is being saved, calmly ('Saving your Atlas finds...')", d.BodyText.Text.StartsWith("Saving your Atlas finds...", StringComparison.Ordinal) && d.BodyText.Text.Contains("closes by itself"), d.BodyText.Text.Replace("\n", " | "));
                        Check("close: its only button is 'Close anyway', a neutral one (waiting is the calm choice, Enter does not close)", d.ConfirmButtonForTest != null && (string)d.ConfirmButtonForTest.Content == "Close anyway" && d.Buttons.Children.Count == 1 && ReferenceEquals(d.ConfirmButtonForTest.Style, FindResource("Button.M")) && !d.ConfirmButtonForTest.IsDefault);
                        var more = T("Uploading a file to your Lodge", Activity.Kind.Save);
                        await Task.Delay(250);
                        Check("close: a second save shows as 'and 1 more'", d.BodyText.Text.Contains("and 1 more"), d.BodyText.Text.Replace("\n", " | "));
                        more.Dispose(); await Task.Delay(250);
                        if (SelfTestClock.ShotsOn) { await Task.Delay(Motion.Enabled ? 300 : 0); ShotOf(d, Path.Combine(dir, "activity-finishing.png"), Rect.Empty, 1); }
                    }
                    _ = Task.Run(async () => { await Task.Delay(400); fake.Dispose(); });
                    res = await run;
                    await Task.Delay(Motion.FastMs + 100);
                    Check("close: the save ends -> the dialog closes by itself and the Outpost may close (Finished)", res == FinishResult.Finished && (d == null || !d.IsVisible) && sw.ElapsedMilliseconds < 5000, res + ", " + sw.ElapsedMilliseconds + " ms");

                    // a save that never ends: the cap
                    var stuck = T("Test: a stuck save", Activity.Kind.Save);
                    sw.Restart();
                    res = await FinishUp(TimeSpan.FromMilliseconds(1500));
                    Check("close: a save that never ends -> it closes anyway when the cap runs out (TimedOut, about the cap)", res == FinishResult.TimedOut && sw.ElapsedMilliseconds >= 1400 && sw.ElapsedMilliseconds < 4000 && FinishDialogForTest == null, res + ", " + sw.ElapsedMilliseconds + " ms");
                    Check("close: the default cap is 15 s", CloseCap == TimeSpan.FromSeconds(15));

                    // "Close anyway"
                    sw.Restart();
                    run = FinishUp(TimeSpan.FromSeconds(10));
                    for (int i = 0; i < 40 && FinishDialogForTest == null; i++) await Task.Delay(50);
                    d = FinishDialogForTest;
                    d?.ConfirmButtonForTest?.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    res = await run;
                    Check("close: 'Close anyway' -> closes at once while the save still runs (ClosedAnyway)", d != null && res == FinishResult.ClosedAnyway && sw.ElapsedMilliseconds < 3000 && Activity.Now().SaveLines.Contains("Test: a stuck save"), res + ", " + sw.ElapsedMilliseconds + " ms");
                    stuck.Dispose();
                    Check("close: read-only work never holds up closing", Activity.Now().Saves == 0 && (await FinishUp(TimeSpan.FromSeconds(5))) == FinishResult.Nothing);
                }
                else Check("close: no other save runs during the test (precondition)", false, string.Join(", ", Activity.Now().SaveLines));
            }
            catch (Exception e) { Check("activity test ran without an exception", false, e.ToString()); }
            finally
            {
                foreach (var t in tokens) t.Dispose();
                ActivityShowDelayMs = savedDelay; ActivityMinShownMs = savedMin;
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                UpdateActivity();
            }
            return "activity: " + (ok ? "ok" : "FAIL") + "\r\n" + string.Join("\r\n", notes);
        }

        // runs `tick` on the window's thread every 20 ms until stopped (the test watches what happens while it awaits something else)
        System.Windows.Threading.DispatcherTimer DispatcherWatch(Action tick)
        {
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            t.Tick += (s, e) => tick();
            t.Start();
            return t;
        }

        // a picture of any visual (a dialog window, a part of this window), optionally a crop of it, scaled up `zoom` times
        static void ShotOf(Visual v, string file, Rect crop, int zoom)
        {
            if (!SelfTestClock.ShotsOn) { SelfTestClock.ShotsSkipped++; return; }
            var fe = v as FrameworkElement;
            if (fe == null) return;
            fe.UpdateLayout();
            int w = (int)Math.Ceiling(fe.ActualWidth), h = (int)Math.Ceiling(fe.ActualHeight);
            if (w <= 0 || h <= 0) return;
            var bmp = new RenderTargetBitmap(w * zoom, h * zoom, 96 * zoom, 96 * zoom, PixelFormats.Pbgra32);
            bmp.Render(fe);
            BitmapSource src = bmp;
            if (!crop.IsEmpty)
            {
                var r = new Int32Rect((int)(crop.X * zoom), (int)(Math.Max(0, crop.Y) * zoom), (int)(crop.Width * zoom), (int)(crop.Height * zoom));
                r.Width = Math.Min(r.Width, bmp.PixelWidth - r.X); r.Height = Math.Min(r.Height, bmp.PixelHeight - r.Y);
                src = new CroppedBitmap(bmp, r);
            }
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(src));
            using (var fs = File.Create(file)) enc.Save(fs);
            SelfTestClock.ShotsTaken++;
        }
    }
}
