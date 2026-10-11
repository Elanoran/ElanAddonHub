using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ElansAddonHub
{
    // Loading placeholder (DESIGN.md, "Loading"): a quiet Surface2 block with a soft lighter band that sweeps across it (1.2 s, linear, left to right).
    // Use it where a list or panel would otherwise be blank while its data loads: <app:Skeleton Height="12" Width="140" />. Give it the shape
    // of the thing that is coming (a line, a 36 px circle via CornerRadius="18", a card). All blocks on screen sweep in step (one shared clock).
    // With Windows animations off (Motion.Enabled) it is a static Surface2 block, nothing moves. It only animates while it is visible.
    public class Skeleton : Border
    {
        public const int SweepMs = 1200;
        static readonly DateTime Epoch = DateTime.UtcNow;

        public static readonly DependencyProperty ShowProperty =
            DependencyProperty.Register("Show", typeof(bool), typeof(Skeleton), new PropertyMetadata(true, (d, e) => ((Skeleton)d).Refresh()));
        // set false to hold the block still (a screenshot, or a caller that wants it static)
        public bool Show { get => (bool)GetValue(ShowProperty); set => SetValue(ShowProperty, value); }

        TranslateTransform slide;
        public bool Sweeping { get; private set; }

        public Skeleton()
        {
            CornerRadius = new CornerRadius(8);
            SetResourceReference(BackgroundProperty, "Brush.Surface2");
            IsHitTestVisible = false;
            Focusable = false;
            Loaded += (s, e) => Refresh();
            Unloaded += (s, e) => Stop();
            IsVisibleChanged += (s, e) => Refresh();
        }

        static Color Res(string key, Color fallback) => (Application.Current?.TryFindResource(key) as SolidColorBrush)?.Color ?? fallback;

        void Refresh()
        {
            if (!IsLoaded || !IsVisible || !Show || !Motion.Enabled) { Stop(); return; }
            if (Sweeping) return;
            var baseC = Res("Brush.Surface2", Color.FromRgb(0x1E, 0x22, 0x26));
            var bandC = Res("Brush.Surface3", Color.FromRgb(0x26, 0x2B, 0x30));
            slide = new TranslateTransform(-1, 0);
            var br = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0), RelativeTransform = slide };
            br.GradientStops.Add(new GradientStop(baseC, 0.30));
            br.GradientStops.Add(new GradientStop(bandC, 0.50));
            br.GradientStops.Add(new GradientStop(baseC, 0.70));
            Background = br;
            // every block starts "now" on one shared clock, so neighbours sweep together
            var phase = (DateTime.UtcNow - Epoch).TotalMilliseconds % SweepMs;
            var anim = new DoubleAnimation(-1, 1, new Duration(TimeSpan.FromMilliseconds(SweepMs))) { RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromMilliseconds(-phase) };
            slide.BeginAnimation(TranslateTransform.XProperty, anim);
            Sweeping = true;
        }

        void Stop()
        {
            if (slide != null) { slide.BeginAnimation(TranslateTransform.XProperty, null); slide = null; }
            Sweeping = false;
            SetResourceReference(BackgroundProperty, "Brush.Surface2");
        }
    }
}
