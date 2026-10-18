using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // Background activity (Outpost 2.96, Services/Activity.cs): the small ring in the rail above Settings that turns while the Outpost reads, builds,
    // saves or uploads, with the running jobs' names as its tooltip; and the close guard: quitting (window close without the tray, tray Quit, a
    // self-update restart) waits for the jobs that write or upload behind a calm "Finishing up" dialog, at most CloseCap, with "Close anyway".
    public partial class MainWindow
    {
        public static int ActivityShowDelayMs = 300;      // a job shorter than this never shows the ring (no flicker)
        public static int ActivityMinShownMs = 700;       // once shown it stays at least this long
        public static TimeSpan CloseCap = TimeSpan.FromSeconds(15);

        readonly DispatcherTimer activityTimer = new DispatcherTimer();
        bool activityQueued, activityShown, activitySpinning;
        DateTime activityShownAt, activityBusySince;

        public bool RailActivityShownForTest => activityShown;
        public bool RailActivitySpinningForTest => activitySpinning;
        public string RailActivityTipForTest => RailActivity.ToolTip as string ?? "";
        public FrameworkElement RailActivityForTest => RailActivity;

        void InitActivity()
        {
            activityTimer.Tick += (s, e) => { activityTimer.Stop(); UpdateActivity(); };
            Activity.Changed += () =>
            {
                if (activityQueued) return;
                activityQueued = true;
                Dispatcher.BeginInvoke(new Action(() => { activityQueued = false; UpdateActivity(); }), DispatcherPriority.Background);
            };
            // reading the game's files is already told in one place (GameDataProgress): it becomes one entry here, named by its current step
            GameDataProgress.Changed += () =>
            {
                var v = GameDataProgress.Now();
                if (v.Busy) Activity.Hold("gamedata", string.IsNullOrEmpty(v.Current?.Step) ? "Reading the game files" : v.Current.Step, Activity.Kind.Work);
                else Activity.Release("gamedata");
            };
            IsVisibleChanged += (s, e) => SpinActivity();
            StateChanged += (s, e) => SpinActivity();
            UpdateActivity();
        }

        // now (tests): what the ring shows follows the tracker, with the show delay and the minimum time
        public void UpdateActivity()
        {
            var v = Activity.Now();
            var now = DateTime.UtcNow;
            if (v.Busy)
            {
                if (activityBusySince == DateTime.MinValue) activityBusySince = now;
                RailActivity.ToolTip = Activity.Describe(v);
                if (!activityShown)
                {
                    var wait = activityBusySince.AddMilliseconds(ActivityShowDelayMs) - now;
                    if (wait > TimeSpan.Zero) { Arm(wait); return; }
                    activityShown = true; activityShownAt = now;
                    RailActivity.Visibility = Visibility.Visible;
                    Fade(1);
                }
            }
            else
            {
                activityBusySince = DateTime.MinValue;
                if (activityShown)
                {
                    var wait = activityShownAt.AddMilliseconds(ActivityMinShownMs) - now;
                    if (wait > TimeSpan.Zero) { Arm(wait); return; }
                    activityShown = false;
                    Fade(0);
                }
            }
            SpinActivity();
        }

        void Arm(TimeSpan wait)
        {
            activityTimer.Stop();
            activityTimer.Interval = wait < TimeSpan.FromMilliseconds(15) ? TimeSpan.FromMilliseconds(15) : wait;
            activityTimer.Start();
        }

        void Fade(double to)
        {
            if (!Motion.Enabled)
            {
                RailActivity.BeginAnimation(OpacityProperty, null);
                RailActivity.Opacity = to;
                if (to == 0) RailActivity.Visibility = Visibility.Collapsed;
                return;
            }
            var a = new DoubleAnimation(to, Motion.Fast) { EasingFunction = to > 0 ? Motion.EaseOut : Motion.EaseIn };
            if (to == 0) a.Completed += (s, e) => { if (!activityShown) RailActivity.Visibility = Visibility.Collapsed; };
            RailActivity.BeginAnimation(OpacityProperty, a);
        }

        // the ring turns only where someone can see it and Windows animations are on; otherwise it stands still (a hidden window costs nothing)
        void SpinActivity()
        {
            bool spin = activityShown && Motion.Enabled && IsVisible && WindowState != WindowState.Minimized;
            if (spin == activitySpinning) return;
            activitySpinning = spin;
            if (spin) RailActivityRot.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.2)) { RepeatBehavior = RepeatBehavior.Forever });
            else { RailActivityRot.BeginAnimation(RotateTransform.AngleProperty, null); RailActivityRot.Angle = 0; }
        }

        // ------------------------------------------------------------------ close guard

        public enum FinishResult { Nothing, Finished, TimedOut, ClosedAnyway }
        public ThemedDialog FinishDialogForTest { get; private set; }
        bool finishing;

        // the dialog's text: what is being saved, in plain words
        public static string FinishText(Activity.View v)
        {
            if (v == null || v.SaveLines.Count == 0) return "Almost done...";
            var s = v.SaveLines[0] + "...";
            if (v.SaveLines.Count > 1) s += "\nand " + (v.SaveLines.Count - 1) + " more";
            return s + "\n\nThe Outpost closes by itself when this is done.";
        }

        // Waits for the Save jobs (writing data, an upload in flight) before the Outpost closes. Nothing to wait for = at once; a save about to end
        // gets a moment without any dialog; otherwise "Finishing up" with the job's name until it is done, the cap runs out or "Close anyway".
        public async Task<FinishResult> FinishUp(TimeSpan cap)
        {
            if (Activity.Now().Saves == 0) return FinishResult.Nothing;
            if (await Activity.WaitForSaves(TimeSpan.FromMilliseconds(Math.Min(400, cap.TotalMilliseconds)))) return FinishResult.Finished;
            var until = DateTime.UtcNow + cap;
            var d = ThemedDialog.Create(DialogKind.Info, "Finishing up", FinishText(Activity.Now()), "Close anyway", null);
            d.Glyph.Text = "";                                         // save
            if (d.ConfirmButtonForTest != null) { d.ConfirmButtonForTest.Style = (Style)FindResource("Button.M"); d.ConfirmButtonForTest.IsDefault = false; }   // waiting is the calm choice: the way out is not the primary button
            bool userClosed = false, ours = false;
            d.Closed += (s, e) => { if (!ours) userClosed = true; };
            FinishDialogForTest = d;
            var result = FinishResult.TimedOut;
            try
            {
                if (IsVisible && WindowState != WindowState.Minimized) { d.Owner = this; d.ShowScrim(this); }
                else d.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                d.Show();
                while (true)
                {
                    var v = Activity.Now();
                    if (v.Saves == 0) { result = FinishResult.Finished; break; }
                    if (userClosed) { result = FinishResult.ClosedAnyway; break; }
                    if (DateTime.UtcNow >= until) { result = FinishResult.TimedOut; break; }
                    var text = FinishText(v);
                    if (d.BodyText.Text != text) d.BodyText.Text = text;
                    await Task.Delay(100);
                }
            }
            catch (Exception e) { Util.Log("finishing up: " + e.GetType().Name); }
            finally
            {
                ours = true;
                FinishDialogForTest = null;
                try { if (d.IsVisible) d.Close(); } catch { }
            }
            var left = Activity.Now();
            Util.Log("closing: " + result + (left.Saves > 0 ? " (" + string.Join(", ", left.SaveLines) + " still running)" : ""));
            return result;
        }

        // every way out of the Outpost comes here: the saves finish first (or the cap / the person says so), then QuitNow
        async void Quit()
        {
            if (quitting || finishing) return;
            finishing = true;
            try { await FinishUp(CloseCap); }
            catch (Exception e) { Util.Log("finishing up: " + e.GetType().Name); }
            QuitNow();
        }
    }
}
