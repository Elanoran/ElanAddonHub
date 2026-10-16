using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // ============================================================================================================================
    // How the Outpost shows that it reads the game files (GameDataProgress). Three pieces, one look (DESIGN.md "Game data progress"):
    //   GameDataStrip   - a slim chip in the title bar row (it never pushes a page down): the step, a ThinProgress of the whole batch, and the
    //                     reason in its tooltip and, when there is room, after the step. Only while game files are being read (a rebuild: the first
    //                     start, after a WoW or Outpost update), never for the fast load of the saved copy. "Game data ready" for a moment, then it fades.
    //   GameDataWait    - the calm build stack for a page that has nothing to show yet (only on a true first start: after a patch the previous
    //                     build's data is shown meanwhile): a glyph, the step in Body, the reason + "this only happens once" in Secondary, a 320
    //                     wide ThinProgress. It replaces the bare skeleton.
    //   DataUpdateNotice - "Updated with the new game data  [Show]": a pill floating over the bottom of a page when fresh data arrived while the page
    //                     showed the older data. The page is not redrawn under the reader; Show (or going to another page) applies it.
    // ============================================================================================================================
    static class GdpUi
    {
        public static object Res(string key) => Application.Current.TryFindResource(key);
        public static TextBlock Text(string style, string brush, string text = "")
        {
            var t = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis };
            if (Res(style) is Style s) t.Style = s;
            t.SetResourceReference(TextBlock.ForegroundProperty, brush);
            return t;
        }
        public static ProgressBar Bar(double width)
        {
            var p = new ProgressBar { Width = width, Minimum = 0, Maximum = 1, IsHitTestVisible = false };
            if (Res("ThinProgress") is Style s) p.Style = s;
            return p;
        }
        public static string Percent(double f) => ((int)Math.Round(Math.Max(0, Math.Min(1, f)) * 100)).ToString(System.Globalization.CultureInfo.InvariantCulture) + " %";
    }

    // the title bar chip
    public sealed class GameDataStrip : Border
    {
        readonly TextBlock step = GdpUi.Text("Text.Caption", "Brush.Text.Primary");
        readonly TextBlock why = GdpUi.Text("Text.Caption", "Brush.Text.Secondary");
        readonly ProgressBar bar = GdpUi.Bar(96);
        readonly DispatcherTimer tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
        readonly DispatcherTimer linger = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2000) };
        bool dirty, shown;
        int shownGen = -1;
        public static int LingerMs = 2000;

        public string StepForTest => step.Text;
        public string ReasonForTest => why.Text;
        public double ValueForTest => bar.Value;
        public bool ShownForTest => shown && Visibility == Visibility.Visible;

        public GameDataStrip()
        {
            Visibility = Visibility.Collapsed;
            HorizontalAlignment = HorizontalAlignment.Center; VerticalAlignment = VerticalAlignment.Center;
            Height = 32; Padding = new Thickness(12, 0, 12, 0); MaxWidth = 560;
            SetResourceReference(BackgroundProperty, "Brush.Surface1");
            if (GdpUi.Res("Radius.ControlLg") is CornerRadius cr) CornerRadius = cr;
            AutomationPropertiesName("Game data progress");
            var row = new DockPanel { LastChildFill = true, VerticalAlignment = VerticalAlignment.Center };
            bar.VerticalAlignment = VerticalAlignment.Center; bar.Margin = new Thickness(12, 0, 0, 0);
            DockPanel.SetDock(bar, Dock.Right);
            row.Children.Add(bar);
            var words = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            why.Margin = new Thickness(8, 0, 0, 0);
            words.Children.Add(step); words.Children.Add(why);
            row.Children.Add(words);
            Child = row;
            tick.Tick += (s, e) => { if (dirty) { dirty = false; Update(); } else tick.Stop(); };
            linger.Tick += (s, e) => { linger.Stop(); Hide(); };
            GameDataProgress.Changed += OnChanged;
            // the reason after the step only when the window is wide enough for both (the tooltip always has it)
            Loaded += (s, e) => { var w = Window.GetWindow(this); if (w != null) { w.SizeChanged += (a, b) => FitWhy(w.ActualWidth); FitWhy(w.ActualWidth); } };
            Update();
        }

        void FitWhy(double windowWidth) => why.Visibility = windowWidth > 0 && windowWidth < 1100 ? Visibility.Collapsed : Visibility.Visible;

        void AutomationPropertiesName(string n) => System.Windows.Automation.AutomationProperties.SetName(this, n);

        void OnChanged() { dirty = true; Dispatcher.BeginInvoke(new Action(() => { if (!tick.IsEnabled) { dirty = false; Update(); tick.Start(); } })); }

        public void Update()
        {
            var v = GameDataProgress.Now();
            if (v.Busy && v.Rebuilding)
            {
                linger.Stop();
                var cur = v.Current;
                step.Text = cur == null ? "Reading your game files" : GameDataProgress.StepText(cur);
                why.Text = GameDataProgress.ReasonText(v.Reason, v.Background);
                bar.Value = v.Overall;
                ToolTip = Tip(v);
                shownGen = v.Generation;
                Show();
            }
            else if (shown && shownGen == v.Generation && !v.Busy)
            {
                step.Text = v.Failed ? "Some game data could not be read" : "Game data ready";
                why.Text = v.Failed ? "Settings > General shows what" : "";
                bar.Value = 1;
                ToolTip = Tip(v);
                if (!linger.IsEnabled) { linger.Interval = TimeSpan.FromMilliseconds(Math.Max(1, LingerMs)); linger.Start(); }
            }
            else if (shown && !v.Busy) Hide();
        }

        static string Tip(GameDataProgress.View v)
        {
            var lines = v.Jobs.Select(j => j.Title + ": " + (j.State == GameDataProgress.JobState.Running ? GameDataProgress.StepText(j) + (j.Fraction > 0 ? " (" + GdpUi.Percent(j.Fraction) + ")" : "")
                : j.State == GameDataProgress.JobState.Failed ? "could not be read" + (string.IsNullOrEmpty(j.Error) ? "" : " (" + j.Error + ")")
                : (j.Rebuild ? "read from the game files" : "loaded") + " in " + (j.Ms / 1000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s"));
            var head = GameDataProgress.ReasonText(v.Reason, v.Background);
            return (head.Length > 0 ? head + ".\n" : "") + (v.Background ? "The Atlas shows the previous data until then.\n" : "") + "Read-only: nothing is written to the WoW folder.\n\n" + string.Join("\n", lines);
        }

        void Show()
        {
            if (shown && Visibility == Visibility.Visible) return;
            shown = true; Visibility = Visibility.Visible;
            BeginAnimation(OpacityProperty, null);
            if (Motion.Enabled) BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Motion.Standard) { EasingFunction = Motion.EaseOut });
            else Opacity = 1;
        }

        void Hide()
        {
            if (!shown) return;
            shown = false;
            if (!Motion.Enabled || !IsLoaded) { Visibility = Visibility.Collapsed; return; }
            var a = new DoubleAnimation(Opacity, 0, Motion.Standard) { EasingFunction = Motion.EaseOut };
            a.Completed += (s, e) => { if (!shown) { BeginAnimation(OpacityProperty, null); Opacity = 1; Visibility = Visibility.Collapsed; } };
            BeginAnimation(OpacityProperty, a);
        }
    }

    // the calm build stack of a page that waits for game data (first start only)
    public sealed class GameDataWait : StackPanel
    {
        readonly TextBlock line = GdpUi.Text("Text.Body", "Brush.Text.Primary");
        readonly TextBlock sub = GdpUi.Text("Text.Secondary", "Brush.Text.Secondary");
        readonly ProgressBar bar = GdpUi.Bar(320);
        readonly string[] jobs;
        readonly string what;
        readonly DispatcherTimer tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };

        public string LineForTest => line.Text;
        public string SubForTest => sub.Text;

        // jobs: the GameDataProgress ids this page waits for (its bar is their progress); what: "the zones" -> "Getting the zones ready"
        public GameDataWait(string what, params string[] jobs)
        {
            this.what = what; this.jobs = jobs ?? new string[0];
            HorizontalAlignment = HorizontalAlignment.Center; MaxWidth = 460; Margin = new Thickness(0, 48, 0, 16);
            var glyph = new TextBlock { Text = "", FontSize = 32, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 12) };   // download / reading glyph
            if (GdpUi.Res("Font.Icons") is FontFamily ff) glyph.FontFamily = ff;
            glyph.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text.Tertiary");
            line.HorizontalAlignment = HorizontalAlignment.Center; line.TextWrapping = TextWrapping.Wrap; line.TextAlignment = TextAlignment.Center; line.TextTrimming = TextTrimming.None;
            sub.HorizontalAlignment = HorizontalAlignment.Center; sub.TextWrapping = TextWrapping.Wrap; sub.TextAlignment = TextAlignment.Center; sub.TextTrimming = TextTrimming.None; sub.Margin = new Thickness(0, 4, 0, 12);
            bar.HorizontalAlignment = HorizontalAlignment.Center;
            Children.Add(glyph); Children.Add(line); Children.Add(sub); Children.Add(bar);
            Tag = "game-data-wait";
            tick.Tick += (s, e) => Update();
            Loaded += (s, e) => { Update(); tick.Start(); };
            Unloaded += (s, e) => tick.Stop();
            Update();
        }

        public void Update()
        {
            var v = GameDataProgress.Now();
            var mine = v.Jobs.Where(j => jobs.Contains(j.Id)).ToList();
            var running = mine.FirstOrDefault(j => j.State == GameDataProgress.JobState.Running);
            if (running == null && mine.Count == 0 && v.Current != null) running = v.Current;   // our reader has not started yet: name what runs
            line.Text = "Getting " + what + " ready" + (running != null ? ": " + Lower(GameDataProgress.StepText(running)) : "");
            var reason = GameDataProgress.ReasonText(v.Reason);
            sub.Text = (reason.Length > 0 ? reason + ". " : "") + (v.Rebuilding ? "This happens on the first start and once after a WoW update; later starts use the saved copy." : "Reading the saved copy of your game data.");
            double w = mine.Sum(j => j.Weight);
            bar.Value = w > 0 ? mine.Sum(j => j.Weight * (j.State == GameDataProgress.JobState.Running ? Math.Max(0, j.Fraction) : 1)) / w : v.Overall;
        }

        static string Lower(string s) => string.IsNullOrEmpty(s) ? s : char.ToLowerInvariant(s[0]) + s.Substring(1);
    }

    // "Updated with the new game data  [Show]" - floats over the bottom of a page (no layout shift); the page decides what Show does
    public sealed class DataUpdateNotice : Border
    {
        readonly Button show = new Button { Content = "Show", Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        readonly TextBlock text = GdpUi.Text("Text.Secondary", "Brush.Text.Primary", "Updated with the new game data");
        public event Action ShowClicked;
        public bool ShownForTest => Visibility == Visibility.Visible;

        public DataUpdateNotice()
        {
            Visibility = Visibility.Collapsed; IsHitTestVisible = true;
            HorizontalAlignment = HorizontalAlignment.Center; VerticalAlignment = VerticalAlignment.Bottom; Margin = new Thickness(0, 0, 0, 16);   // like a snackbar: over the bottom of the page, not over its title
            Padding = new Thickness(12, 4, 4, 4);
            SetResourceReference(BackgroundProperty, "Brush.Surface3");
            SetResourceReference(BorderBrushProperty, "Brush.Edge");
            BorderThickness = new Thickness(1);
            if (GdpUi.Res("Radius.ControlLg") is CornerRadius cr) CornerRadius = cr;
            if (GdpUi.Res("Elevation.1") is System.Windows.Media.Effects.Effect fx) Effect = fx;
            if (GdpUi.Res("Button.S") is Style bs) show.Style = bs;
            show.Tag = "data-update-show";
            System.Windows.Automation.AutomationProperties.SetName(this, "New game data");
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var dot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            dot.SetResourceReference(BackgroundProperty, "Brush.Accent");
            text.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(dot); row.Children.Add(text); row.Children.Add(show);
            Child = row;
            show.Click += (s, e) => { Hide(); ShowClicked?.Invoke(); };
            Panel.SetZIndex(this, 20);
        }

        public void Offer(string message = null)
        {
            text.Text = message ?? "Updated with the new game data";
            if (Visibility == Visibility.Visible) return;
            Visibility = Visibility.Visible;
            BeginAnimation(OpacityProperty, null);
            if (Motion.Enabled) BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Motion.Standard) { EasingFunction = Motion.EaseOut }); else Opacity = 1;
        }

        public void Hide() { BeginAnimation(OpacityProperty, null); Opacity = 1; Visibility = Visibility.Collapsed; }
        public void ClickForTest() => show.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

        // a gentle fade-in of what was redrawn after Show
        public static void FadeIn(UIElement el)
        {
            if (el == null || !Motion.Enabled) return;
            el.BeginAnimation(OpacityProperty, new DoubleAnimation(0.35, 1, Motion.Emphasized) { EasingFunction = Motion.EaseOut });
        }
    }
}
