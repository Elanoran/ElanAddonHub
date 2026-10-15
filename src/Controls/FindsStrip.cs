using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ElansAddonHub.Services.Atlas;

namespace ElansAddonHub
{
    // A row of item icons that scrolls sideways (the Lodge's latest finds): a quality-coloured frame per icon, a count badge when more than one, the game
    // tooltip on hover or keyboard focus, a click opens the item. Scrolls with the mouse wheel, a touch / pen swipe, a mouse drag and the small arrows at the
    // ends (which show only when there is more to scroll); Left / Right move the focus. No scrollbar. Update() keeps the position unless the strip is at its start.
    public sealed class FindsStrip : Grid
    {
        public sealed class Entry
        {
            public long Seq;
            public AtlasRow Row;                 // link, title, quality, icon
            public int Count = 1;
            public string Foot = "";             // "Found by Tess · 2× from Gnarlfang · Greenvale · 10 minutes ago"
        }

        public const double Tile = 44, Gap = 8, Pitch = Tile + Gap;

        public Func<Entry, GameTooltip> TipFor;      // the item's tooltip (the strip adds the footer)
        public Action<string> OpenLink;

        readonly ScrollViewer sv;
        readonly StackPanel stack;
        readonly Button left, right;
        List<Entry> entries = new List<Entry>();
        readonly List<FrameworkElement> tiles = new List<FrameworkElement>();
        double target;
        Point downAt; double downOffset; bool pressed, dragging;

        public IReadOnlyList<Entry> Entries => entries;
        public IReadOnlyList<FrameworkElement> TilesForTest => tiles;
        public ScrollViewer ScrollerForTest => sv;
        public Button LeftArrow => left;
        public Button RightArrow => right;
        public double Offset => sv.HorizontalOffset;
        public double TargetOffset => target;
        public bool AtStart => sv.HorizontalOffset <= 1;

        static readonly DependencyProperty AnimProperty = DependencyProperty.Register("Anim", typeof(double), typeof(FindsStrip), new PropertyMetadata(0.0, (d, e) => ((FindsStrip)d).sv.ScrollToHorizontalOffset((double)e.NewValue)));

        static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

        public FindsStrip()
        {
            Height = Tile + 8;
            ClipToBounds = true;
            stack = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
            KeyboardNavigation.SetTabNavigation(stack, KeyboardNavigationMode.Once);
            KeyboardNavigation.SetDirectionalNavigation(stack, KeyboardNavigationMode.None);
            sv = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                PanningMode = PanningMode.HorizontalOnly, CanContentScroll = false, Focusable = false, Content = stack
            };
            Children.Add(sv);
            left = Arrow("", HorizontalAlignment.Left, "Scroll back to newer finds");
            right = Arrow("", HorizontalAlignment.Right, "Scroll on to older finds");
            left.Click += (s, e) => By(-sv.ViewportWidth * 0.8);
            right.Click += (s, e) => By(sv.ViewportWidth * 0.8);
            Children.Add(left); Children.Add(right);
            sv.ScrollChanged += (s, e) => { if (!animating) target = sv.HorizontalOffset; UpdateArrows(); };
            SizeChanged += (s, e) => UpdateArrows();
            PreviewMouseWheel += OnWheel;
            PreviewMouseLeftButtonDown += OnDown;
            PreviewMouseMove += OnMove;
            PreviewMouseLeftButtonUp += OnUp;
            PreviewKeyDown += OnKey;
        }

        bool animating;

        Button Arrow(string glyph, HorizontalAlignment side, string tip)
        {
            var b = new Button
            {
                Style = (Style)Application.Current.FindResource("IconButton"), Content = glyph, Width = 28, Height = 28, FontSize = 12,
                Background = Res("Brush.Surface3"), HorizontalAlignment = side, VerticalAlignment = VerticalAlignment.Center, Focusable = false,
                Margin = new Thickness(4, 0, 4, 0), Visibility = Visibility.Collapsed, ToolTip = tip
            };
            AutomationProperties.SetName(b, tip);
            return b;
        }

        void UpdateArrows()
        {
            bool more = sv.ScrollableWidth > 1;
            left.Visibility = more && sv.HorizontalOffset > 1 ? Visibility.Visible : Visibility.Collapsed;
            right.Visibility = more && sv.HorizontalOffset < sv.ScrollableWidth - 1 ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---- the data
        // True when the strip was redrawn. Keeps the scroll position unless at the start: finds that arrive at the front push the rest along, the view stays on what it showed.
        public bool Update(IList<Entry> list)
        {
            if (entries.Count == list.Count && entries.Zip(list, (a, b) => a.Seq == b.Seq && a.Count == b.Count && a.Foot == b.Foot).All(x => x)) return false;
            bool atStart = AtStart;
            long topOld = entries.Count > 0 ? entries.Max(e => e.Seq) : 0;
            int added = entries.Count == 0 ? 0 : list.Count(e => e.Seq > topOld);
            double keep = sv.HorizontalOffset;
            entries = list.ToList();
            Build();
            Visibility = entries.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (!atStart) { StopAnim(); sv.UpdateLayout(); Set(Math.Min(keep + added * Pitch, sv.ScrollableWidth)); }
            else { StopAnim(); sv.ScrollToHorizontalOffset(0); target = 0; }
            UpdateArrows();
            return true;
        }

        void Build()
        {
            stack.Children.Clear(); tiles.Clear();
            foreach (var en in entries)
            {
                var t = MakeTile(en);
                tiles.Add(t); stack.Children.Add(t);
            }
        }

        FrameworkElement MakeTile(Entry en)
        {
            var row = en.Row;
            var frame = new Border
            {
                Width = Tile, Height = Tile, BorderThickness = new Thickness(2), BorderBrush = row.TileBrush, Background = Res("Brush.Surface2"),
                CornerRadius = (CornerRadius)Application.Current.FindResource("Radius.Control"), ClipToBounds = true
            };
            var inner = new Grid();
            inner.Children.Add(new TextBlock { Text = row.Initial, FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = row.TileBrush, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            var pic = new Image { Stretch = Stretch.UniformToFill };
            RenderOptions.SetBitmapScalingMode(pic, BitmapScalingMode.HighQuality);
            pic.SetBinding(Image.SourceProperty, new Binding("Icon") { Source = row });
            inner.Children.Add(pic);
            frame.Child = inner;

            var host = new Grid { Width = Tile, Height = Tile, Margin = new Thickness(0, 0, Gap, 0), Focusable = true, Cursor = Cursors.Hand, Background = Brushes.Transparent, Tag = en };
            host.FocusVisualStyle = null;
            host.Children.Add(frame);
            if (en.Count > 1)
            {
                var badge = new Border
                {
                    Background = Res("Brush.Surface3"), CornerRadius = (CornerRadius)Application.Current.FindResource("Radius.Control"), Padding = new Thickness(4, 0, 4, 0),
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 2, 2), IsHitTestVisible = false,
                    Child = new TextBlock { Text = en.Count > 99 ? "99+" : en.Count.ToString(), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Res("Brush.Text.Primary") }
                };
                host.Children.Add(badge);
            }
            var ring = new Border { Margin = new Thickness(-3), BorderThickness = new Thickness(2), BorderBrush = Res("Brush.Focus"), CornerRadius = (CornerRadius)Application.Current.FindResource("Radius.ControlLg"), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
            host.Children.Add(ring);
            AutomationProperties.SetName(host, row.Title + (en.Count > 1 ? ", " + en.Count + " found" : ""));

            var tip = new ToolTip { Style = (Style)Application.Current.FindResource("GameTip"), PlacementTarget = host, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            tip.Opened += (s, e) => { if (tip.Content == null) tip.Content = TipOf(en); };
            host.ToolTip = tip;
            ToolTipService.SetInitialShowDelay(host, 150);
            ToolTipService.SetShowDuration(host, 30000);
            host.GotKeyboardFocus += (s, e) => { ring.Visibility = Visibility.Visible; EnsureVisible(host); if (Mouse.LeftButton != MouseButtonState.Pressed) { tip.Content = TipOf(en); tip.IsOpen = true; } };
            host.LostKeyboardFocus += (s, e) => { ring.Visibility = Visibility.Collapsed; tip.IsOpen = false; };
            host.MouseLeftButtonUp += (s, e) => { if (dragging) return; e.Handled = true; Open(en); };
            host.KeyDown += (s, e) => { if (e.Key == Key.Enter || e.Key == Key.Space) { e.Handled = true; Open(en); } };
            return host;
        }

        void Open(Entry en) { if (!string.IsNullOrEmpty(en.Row.Link)) OpenLink?.Invoke(en.Row.Link); }
        public void OpenForTest(int i) => Open(entries[i]);

        // the game tooltip of the item with the Outpost's own line under it
        public GameTooltip TipOf(Entry en)
        {
            var gt = TipFor?.Invoke(en) ?? new GameTooltip(en.Row.Title, en.Row.Quality);
            if (en.Foot.Length > 0) gt.Footer.Children.Add(GameTooltip.Muted(en.Foot, gt.Footer.Children.Count == 0 ? 0 : 2));
            return gt;
        }

        // ---- scrolling
        void StopAnim() { animating = false; BeginAnimation(AnimProperty, null); }

        void Set(double offset)
        {
            offset = Math.Max(0, Math.Min(offset, sv.ScrollableWidth));
            target = offset;
            sv.ScrollToHorizontalOffset(offset);
        }

        // slide to a position (smooth)
        public void To(double offset)
        {
            offset = Math.Max(0, Math.Min(offset, sv.ScrollableWidth));
            target = offset;
            double from = sv.HorizontalOffset;
            if (Math.Abs(offset - from) < 1) return;
            animating = true;
            var a = new DoubleAnimation(from, offset, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop };
            a.Completed += (s, e) => { animating = false; sv.ScrollToHorizontalOffset(offset); };
            BeginAnimation(AnimProperty, a);
        }

        public void By(double delta) => To((animating ? target : sv.HorizontalOffset) + delta);

        void EnsureVisible(FrameworkElement t)
        {
            int i = tiles.IndexOf(t);
            if (i < 0 || sv.ViewportWidth <= 0) return;
            double l = i * Pitch, r = l + Tile + Gap;
            if (l < sv.HorizontalOffset) To(l - Gap); else if (r > sv.HorizontalOffset + sv.ViewportWidth) To(r - sv.ViewportWidth + Gap);
        }

        void OnWheel(object s, MouseWheelEventArgs e)
        {
            if (sv.ScrollableWidth <= 0) return;      // nothing to scroll sideways: the page scrolls as usual
            e.Handled = true;
            By(-e.Delta);
        }

        void OnDown(object s, MouseButtonEventArgs e)
        {
            if (e.StylusDevice != null) return;       // a touch / pen swipe is the ScrollViewer's own panning
            pressed = true; dragging = false; downAt = e.GetPosition(this); downOffset = sv.HorizontalOffset;
        }

        void OnMove(object s, MouseEventArgs e)
        {
            if (!pressed || e.LeftButton != MouseButtonState.Pressed || e.StylusDevice != null) return;
            double dx = e.GetPosition(this).X - downAt.X;
            if (!dragging && Math.Abs(dx) > 4) { dragging = true; StopAnim(); CaptureMouse(); Cursor = Cursors.SizeWE; }
            if (dragging) Set(downOffset - dx);
        }

        void OnUp(object s, MouseButtonEventArgs e)
        {
            pressed = false;
            if (dragging) { ReleaseMouseCapture(); Cursor = null; e.Handled = true; Dispatcher.BeginInvoke(new Action(() => dragging = false)); }
        }

        public void SimulateDragForTest(double dx)
        {
            downOffset = sv.HorizontalOffset; dragging = true; Set(downOffset - dx); dragging = false;
        }

        void OnKey(object s, KeyEventArgs e)
        {
            if (e.Key == Key.Left) { if (MoveFocus(-1)) e.Handled = true; }
            else if (e.Key == Key.Right) { if (MoveFocus(1)) e.Handled = true; }
        }

        public bool MoveFocus(int delta)
        {
            int cur = tiles.FindIndex(t => t.IsKeyboardFocused || t.IsKeyboardFocusWithin);
            int next = cur < 0 ? (delta > 0 ? 0 : tiles.Count - 1) : cur + delta;
            if (next < 0 || next >= tiles.Count) return cur >= 0;
            tiles[next].Focus();
            return true;
        }
    }
}
