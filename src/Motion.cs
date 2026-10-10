using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ElansAddonHub
{
    // Standard motion for the Outpost (DESIGN.md, "Motion"): durations, easing and a few attached behaviours so XAML can ask for
    // "expand / collapse", "hover" and "turn" without every page writing its own storyboard. Everything collapses to "instant" when
    // Windows animations are off (Settings > Accessibility > Visual effects > Animation effects).
    public static class Motion
    {
        public const int FastMs = 120, StandardMs = 180, EmphasizedMs = 240;

        public static bool Enabled => SystemParameters.ClientAreaAnimation;
        public static Duration Fast => D(FastMs);
        public static Duration Standard => D(StandardMs);
        public static Duration Emphasized => D(EmphasizedMs);
        static Duration D(int ms) => new Duration(Enabled ? TimeSpan.FromMilliseconds(ms) : TimeSpan.Zero);

        public static readonly IEasingFunction EaseOut = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });
        public static readonly IEasingFunction EaseIn = Frozen(new CubicEase { EasingMode = EasingMode.EaseIn });
        static IEasingFunction Frozen(CubicEase e) { e.Freeze(); return e; }

        // ---- Hover: Track="True" mirrors IsMouseOver into IsHot, which templates trigger on (and tests can set directly)
        public static readonly DependencyProperty IsHotProperty =
            DependencyProperty.RegisterAttached("IsHot", typeof(bool), typeof(Motion), new FrameworkPropertyMetadata(false));
        public static bool GetIsHot(DependencyObject d) => d != null && (bool)d.GetValue(IsHotProperty);
        public static void SetIsHot(DependencyObject d, bool v) { if (d != null) d.SetValue(IsHotProperty, v); }

        public static readonly DependencyProperty TrackProperty =
            DependencyProperty.RegisterAttached("Track", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnTrack));
        public static bool GetTrack(DependencyObject d) => (bool)d.GetValue(TrackProperty);
        public static void SetTrack(DependencyObject d, bool v) => d.SetValue(TrackProperty, v);
        static void OnTrack(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is UIElement el)) return;
            el.MouseEnter -= HotOn; el.MouseLeave -= HotOff;
            if ((bool)e.NewValue) { el.MouseEnter += HotOn; el.MouseLeave += HotOff; }
        }
        static void HotOn(object s, System.Windows.Input.MouseEventArgs e) => SetIsHot((DependencyObject)s, true);
        static void HotOff(object s, System.Windows.Input.MouseEventArgs e) => SetIsHot((DependencyObject)s, false);

        // ---- Expand: bind to a bool; the element grows / shrinks (height + fade) instead of popping. Put it on a Border (its Padding
        //      is part of the animated height). First value is applied without animation.
        public static readonly DependencyProperty ExpandProperty =
            DependencyProperty.RegisterAttached("Expand", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnExpand));
        public static bool GetExpand(DependencyObject d) => (bool)d.GetValue(ExpandProperty);
        public static void SetExpand(DependencyObject d, bool v) => d.SetValue(ExpandProperty, v);

        static readonly DependencyProperty GenProperty = DependencyProperty.RegisterAttached("Gen", typeof(int), typeof(Motion), new PropertyMetadata(0));

        static void Settle(FrameworkElement el, bool open)
        {
            el.BeginAnimation(FrameworkElement.HeightProperty, null);
            el.BeginAnimation(UIElement.OpacityProperty, null);
            el.ClearValue(FrameworkElement.HeightProperty);
            el.ClearValue(UIElement.OpacityProperty);
            el.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        }

        static void OnExpand(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var el = d as FrameworkElement;
            if (el == null) return;
            bool open = (bool)e.NewValue;
            int gen = (int)el.GetValue(GenProperty) + 1;
            el.SetValue(GenProperty, gen);
            if (!el.IsLoaded || !Enabled || !(el.Parent is FrameworkElement parent) || parent.ActualWidth <= 0) { Settle(el, open); return; }

            el.ClipToBounds = true;
            if (open)
            {
                double from = el.Visibility == Visibility.Visible ? el.ActualHeight : 0;
                el.BeginAnimation(FrameworkElement.HeightProperty, null);
                el.ClearValue(FrameworkElement.HeightProperty);
                el.Visibility = Visibility.Visible;
                el.Measure(new Size(Math.Max(0, parent.ActualWidth - el.Margin.Left - el.Margin.Right), double.PositiveInfinity));
                double to = el.DesiredSize.Height;
                el.Height = from;
                var h = new DoubleAnimation(from, to, Standard) { EasingFunction = EaseOut, FillBehavior = FillBehavior.HoldEnd };
                h.Completed += (s, a) => { if ((int)el.GetValue(GenProperty) == gen) Settle(el, true); };
                el.BeginAnimation(FrameworkElement.HeightProperty, h);
                el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(el.Opacity, 1, Standard) { EasingFunction = EaseOut, FillBehavior = FillBehavior.HoldEnd });
            }
            else
            {
                if (el.Visibility != Visibility.Visible) { Settle(el, false); return; }
                double from = el.ActualHeight;
                el.Height = from;
                var h = new DoubleAnimation(from, 0, Fast) { EasingFunction = EaseIn, FillBehavior = FillBehavior.HoldEnd };
                h.Completed += (s, a) => { if ((int)el.GetValue(GenProperty) == gen) Settle(el, false); };
                el.BeginAnimation(FrameworkElement.HeightProperty, h);
                el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(el.Opacity, 0, Fast) { EasingFunction = EaseIn, FillBehavior = FillBehavior.HoldEnd });
            }
        }

        // ---- Turn: a chevron that rotates 180 degrees when the bound bool is true
        public static readonly DependencyProperty TurnProperty =
            DependencyProperty.RegisterAttached("Turn", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnTurn));
        public static bool GetTurn(DependencyObject d) => (bool)d.GetValue(TurnProperty);
        public static void SetTurn(DependencyObject d, bool v) => d.SetValue(TurnProperty, v);
        static void OnTurn(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var el = d as UIElement;
            if (el == null) return;
            var rot = el.RenderTransform as RotateTransform;
            if (rot == null || rot.IsFrozen) { rot = new RotateTransform(0); el.RenderTransform = rot; el.RenderTransformOrigin = new Point(0.5, 0.5); }
            double to = (bool)e.NewValue ? 180 : 0;
            if (!(d is FrameworkElement fe) || !fe.IsLoaded || !Enabled) { rot.BeginAnimation(RotateTransform.AngleProperty, null); rot.Angle = to; return; }
            rot.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(to, Standard) { EasingFunction = to > 0 ? EaseOut : EaseIn });
        }

        // ---- EdgeFade: on a ScrollViewer, content fades out at an edge only while there is more to scroll that way
        public static readonly DependencyProperty EdgeFadeProperty =
            DependencyProperty.RegisterAttached("EdgeFade", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnEdgeFade));
        public static bool GetEdgeFade(DependencyObject d) => (bool)d.GetValue(EdgeFadeProperty);
        public static void SetEdgeFade(DependencyObject d, bool v) => d.SetValue(EdgeFadeProperty, v);
        const double FadePx = 22;
        static void OnEdgeFade(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is ScrollViewer sv)) return;
            sv.ScrollChanged -= FadeChanged;
            sv.SizeChanged -= FadeSized;
            if ((bool)e.NewValue) { sv.ScrollChanged += FadeChanged; sv.SizeChanged += FadeSized; }
            else sv.OpacityMask = null;
        }
        static void FadeChanged(object s, ScrollChangedEventArgs e) => UpdateFade((ScrollViewer)s);
        static void FadeSized(object s, SizeChangedEventArgs e) => UpdateFade((ScrollViewer)s);
        static void UpdateFade(ScrollViewer sv)
        {
            double h = sv.ActualHeight;
            double top = Math.Min(sv.VerticalOffset, FadePx) / FadePx;                              // 0 at the very top
            double bottom = Math.Min(Math.Max(0, sv.ScrollableHeight - sv.VerticalOffset), FadePx) / FadePx; // 0 at the very bottom
            if (h < FadePx * 3 || sv.ScrollableHeight < 1 || (top <= 0.001 && bottom <= 0.001)) { if (sv.OpacityMask != null) sv.OpacityMask = null; return; }
            double f = FadePx / h;
            Color C(double a) => Color.FromArgb((byte)Math.Round(255 * a), 0, 0, 0);
            var br = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            br.GradientStops.Add(new GradientStop(C(1 - top), 0));
            br.GradientStops.Add(new GradientStop(C(1), f));
            br.GradientStops.Add(new GradientStop(C(1), 1 - f));
            br.GradientStops.Add(new GradientStop(C(1 - bottom), 1));
            br.Freeze();
            sv.OpacityMask = br;
        }

        // ---- Page enter: fade + 8 px slide up, once, when a page becomes visible
        public static void PageIn(FrameworkElement page)
        {
            if (page == null || !Enabled) return;
            var tt = new TranslateTransform(0, 8);
            page.RenderTransform = tt;
            var slide = new DoubleAnimation(8, 0, Standard) { EasingFunction = EaseOut };
            slide.Completed += (s, a) => { if (page.RenderTransform == tt) page.RenderTransform = Transform.Identity; };
            tt.BeginAnimation(TranslateTransform.YProperty, slide);
            page.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Standard) { EasingFunction = EaseOut });
        }
    }
}
