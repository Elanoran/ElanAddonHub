using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ElansAddonHub
{
    // "Lighting the campfire": the splash screen. It runs on its OWN UI thread, so the animation keeps going while the main
    // window is being built on the main thread, and nothing ever waits for it: the app only feeds it the real startup steps.
    //   Splash.Start(...)  at the very start of OnStartup      Splash.Step("...") for each real step      Splash.Ready() when the main window is ready
    // It stays at least MinSeconds (so the fire gets lit) and at most MaxSeconds; a click or Esc skips it; then it fades out.
    public static class Splash
    {
        public const double MinSeconds = 1.25, MaxSeconds = 2.5, StepSeconds = 0.4, FadeSeconds = 0.3;
        static readonly object gate = new object();
        static readonly List<string> queue = new List<string>();
        static bool ready;
        static int stepsTotal = 3;

        /// <summary>True while a splash is on screen (or about to be).</summary>
        public static bool Active { get; private set; }

        /// <param name="fadeStarted">called (on the main UI thread) when the splash starts fading: show the main window now</param>
        public static void Start(Action fadeStarted)
        {
            Active = true;
            var main = Application.Current.Dispatcher;
            Action onFade = () => { Active = false; try { main.BeginInvoke(fadeStarted); } catch { } };
            var th = new Thread(() =>
            {
                try
                {
                    var w = new SplashWindow(onFade);
                    w.Show();
                    Dispatcher.Run();
                }
                catch (Exception e) { Services.Util.Log("splash failed: " + e.Message); onFade(); }
            }) { IsBackground = true, Name = "splash" };
            th.SetApartmentState(ApartmentState.STA);
            th.Start();
        }

        public static void Step(string text) { lock (gate) { queue.Add(text); } }
        public static void Ready() { lock (gate) { queue.Add("Ready"); ready = true; } }

        internal static bool TakeStep(out string text, out bool isLast)
        {
            lock (gate)
            {
                text = null; isLast = false;
                if (queue.Count == 0) return false;
                text = queue[0]; queue.RemoveAt(0);
                isLast = ready && queue.Count == 0;
                return true;
            }
        }
        internal static bool IsReady { get { lock (gate) return ready && queue.Count == 0; } }
        internal static double ProgressFor(int shown, bool last) => last ? 1 : Math.Min(0.85, shown / (double)stepsTotal);

        // ---------------------------------------------------------------- frames for design review
        // --splashframes <dir>: frames at key times (deterministic), plus a contact sheet
        public static void RenderFrames(string dir)
        {
            Directory.CreateDirectory(dir);
            var times = new[] { 0.2, 0.5, 0.8, 1.1, 1.5, 2.0 };
            var shots = new List<BitmapSource>();
            foreach (var t in times)
            {
                var root = Build(out var scene);
                ApplyScript(scene, t);
                scene.Update(t);
                var bmp = Snap(root, 2);
                Save(bmp, Path.Combine(dir, $"splash-{(int)Math.Round(t * 1000):0000}ms.png"));
                shots.Add(Snap(root, 1));
            }
            // contact sheet: 3 x 2, labelled
            var sheet = new Grid { Background = new SolidColorBrush(Color.FromRgb(0x16, 0x19, 0x1C)) };
            var wrap = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3, Margin = new Thickness(10) };
            for (int i = 0; i < shots.Count; i++)
            {
                var cell = new StackPanel { Margin = new Thickness(4) };
                cell.Children.Add(new Image { Source = shots[i], Width = 520, Height = 340 });
                cell.Children.Add(new TextBlock { Text = $"t = {times[i]:0.0} s", Foreground = Brushes.Gray, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center });
                wrap.Children.Add(cell);
            }
            sheet.Children.Add(wrap);
            Save(Snap(sheet, 1), Path.Combine(dir, "splash-sheet.png"));
        }

        // the status line as the real flow would run it (steps 0.45 s apart), as a pure function of t
        static void ApplyScript(SplashScene scene, double t)
        {
            var script = new[] { (0.0, "Checking for updates...", 0.28), (0.45, "Connecting to the Lodge...", 0.6), (0.9, "Ready", 1.0) };
            foreach (var s in script) if (t >= s.Item1) scene.SetStatus(s.Item2, s.Item1, s.Item3);
        }

        static BitmapSource Snap(FrameworkElement el, int scale)
        {
            el.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = el.DesiredSize.Width > 0 ? el.DesiredSize : new Size(el.Width, el.Height);
            el.Arrange(new Rect(size));
            el.UpdateLayout();
            var bmp = new RenderTargetBitmap((int)size.Width * scale, (int)size.Height * scale, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bmp.Render(el);
            bmp.Freeze();
            return bmp;
        }
        static void Save(BitmapSource bmp, string file)
        {
            var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp));
            using (var fs = File.Create(file)) enc.Save(fs);
        }

        /// <summary>The window content: a soft shadow under the rounded scene (520 x 340 incl. the shadow margin).</summary>
        internal static FrameworkElement Build(out SplashScene scene)
        {
            var root = new Grid { Width = 520, Height = 340, Background = new SolidColorBrush(Color.FromRgb(0x14, 0x17, 0x1A)) };
            var shadow = new Canvas { Width = 520, Height = 340, IsHitTestVisible = false };
            for (int i = 0; i < 12; i++)   // layered translucent rounded rects: a soft shadow without a (software-rendered) blur effect
            {
                var g = 20 - i * 1.6;
                var r = new System.Windows.Shapes.Rectangle { Width = 480 + (12 - i) * 1.9 * 2 - 3, Height = 300 + (12 - i) * 1.9 * 2 - 3, RadiusX = 18 + (12 - i) * 1.4, RadiusY = 18 + (12 - i) * 1.4, Fill = new SolidColorBrush(Color.FromArgb(14, 0, 0, 0)) };
                Canvas.SetLeft(r, 20 - (12 - i) * 1.9 + 1.5); Canvas.SetTop(r, 24 - (12 - i) * 1.9 + 1.5);
                shadow.Children.Add(r);
            }
            root.Children.Add(shadow);
            scene = new SplashScene(Brand.Name) { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(20, 20, 0, 0) };
            root.Children.Add(scene);
            return root;
        }

        // ---------------------------------------------------------------- the window
        sealed class SplashWindow : Window
        {
            readonly SplashScene scene;
            readonly Action onFade;
            readonly Stopwatch clock = Stopwatch.StartNew();
            readonly DispatcherTimer timer;
            readonly bool still = !SystemParameters.ClientAreaAnimation;   // Windows "show animations" off: a static lit scene
            double lastStepAt = -10, fadeAt = -1;
            int shown;
            bool lastShown;

            public SplashWindow(Action onFade)
            {
                this.onFade = onFade;
                WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
                ShowInTaskbar = false; Topmost = true; ResizeMode = ResizeMode.NoResize;
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
                Width = 520; Height = 340; Opacity = 0; Title = Brand.Name;
                SnapsToDevicePixels = true;
                Content = Build(out scene);
                ((Panel)Content).Background = Brushes.Transparent;
                MouseLeftButtonDown += (s, e) => Skip();
                KeyDown += (s, e) => { if (e.Key == Key.Escape || e.Key == Key.Enter || e.Key == Key.Space) Skip(); };
                timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(still ? 50 : 33) };
                timer.Tick += (s, e) => Tick();
                Loaded += (s, e) => { clock.Restart(); timer.Start(); Tick(); };
            }

            void Skip() { if (fadeAt < 0) fadeAt = clock.Elapsed.TotalSeconds; Begin(); }
            bool began;
            void Begin() { if (began) return; began = true; onFade(); }

            void Tick()
            {
                var t = clock.Elapsed.TotalSeconds;
                // the next real step, once the current one has been readable for a moment
                if (t - lastStepAt >= (still ? 0.2 : StepSeconds) && TakeStep(out var text, out var last))
                {
                    shown++; lastShown = last; lastStepAt = t;
                    scene.SetStatus(text, still ? -10 : t, ProgressFor(shown, last));
                }
                // when to leave: everything shown and the minimum reached, or the maximum
                var min = still ? 0.7 : MinSeconds;
                if (fadeAt < 0 && ((lastShown && IsReady && t >= min && t - lastStepAt >= 0.3) || t >= (still ? 1.6 : MaxSeconds))) fadeAt = t;
                if (fadeAt >= 0) Begin();

                scene.Update(still ? 3.0 : t);
                var o = Math.Min(1, t / (still ? 0.12 : 0.2));
                if (fadeAt >= 0) o = Math.Min(o, 1 - (t - fadeAt) / (still ? 0.15 : FadeSeconds));
                Opacity = Math.Max(0, o);
                if (fadeAt >= 0 && t - fadeAt >= (still ? 0.15 : FadeSeconds)) Finish();
            }

            void Finish()
            {
                timer.Stop();
                Topmost = false;
                Begin();
                Close();
                Dispatcher.InvokeShutdown();
            }
        }
    }
}
