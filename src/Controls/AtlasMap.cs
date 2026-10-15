using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ElansAddonHub.Services.Maps;

namespace ElansAddonHub
{
    // The Atlas map (DESIGN.md: calm, dark, quiet). Shows one map of the player's own game files (a zone, a continent, a battleground or one floor
    // of an instance) scaled to fit, with wheel zoom, drag to pan, a breadcrumb up the map tree, a floor selector when the map has floors, and a
    // marker layer: AddMarkers(...) puts spawns, finds, vendors, nodes and quests on it by the fractions of the picture. Markers that overlap on the
    // screen become one numbered marker (click it to zoom in), the hover text names them, and a click on a single one raises LinkClicked(its id).
    //
    //   var map = new AtlasMap();
    //   map.LinkClicked += id => ...;                 // "npc:456", "obj:789", "quest:12" ...
    //   map.ShowMap(1411);                            // Durotar; the picture is made in the background the first time
    //   map.AddMarkers(new[] { new AtlasMarker(0.52, 0.43, MarkerKind.Vendor, "Innkeeper Grosk", "Sells food", "npc:3321") });
    //   map.ShowInstance(30);                         // an instance by its map id (its first floor; the selector offers the others)
    //
    // The picture comes from MapRender (PNG cache); the tree, floors and names from MapData.Index. Nothing is kept for maps that are not shown.
    public sealed class AtlasMap : UserControl
    {
        // ---- public surface
        public event Action<string> LinkClicked;
        public event Action<int> MapChanged;

        public int UiMapId { get; private set; }
        public int MarkerCount => markers.Count;
        public enum ViewState { Empty, Loading, Ready, NoArt, Unavailable, Failed }
        public ViewState State { get; private set; } = ViewState.Empty;
        public string StateText { get; private set; } = "";

        // zoom: the factor over "fits the stage" (1 = fitted)
        public double Zoom => fit <= 0 ? 1 : scale / fit;
        public double Scale => scale;
        public Point Offset => new Point(ox, oy);
        public Size PictureSize => new Size(pw, ph);
        public FrameworkElement Viewport => view;
        public double MaxZoom = 10;
        public double ClusterRadius = 14;

        // ---- parts
        readonly MapView view;
        readonly Grid stage = new Grid();
        readonly Canvas tipLayer = new Canvas { IsHitTestVisible = false };
        readonly Border tipBox = new Border { Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        readonly StackPanel tipStack = new StackPanel();
        readonly StackPanel stateBox = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        readonly Skeleton skeleton = new Skeleton { Visibility = Visibility.Collapsed, CornerRadius = new CornerRadius(12) };
        readonly TextBlock stateGlyph = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center };
        readonly TextBlock stateTitle = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
        readonly TextBlock stateHint = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 360 };
        readonly StackPanel crumbs = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        readonly Border floorHost = new Border { Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
        readonly StackPanel floorPanel = new StackPanel { Orientation = Orientation.Horizontal };
        readonly Button zoomIn, zoomOut, zoomFit;
        public Button ZoomInButton => zoomIn;
        public Button ZoomOutButton => zoomOut;
        public Button FitButton => zoomFit;
        public IReadOnlyList<string> BreadcrumbForTest => crumbNames;
        public IReadOnlyList<string> FloorNamesForTest => floorNames;
        public int SelectedFloorForTest => floorSelected;
        readonly List<string> crumbNames = new List<string>(), floorNames = new List<string>();
        int floorSelected = -1;
        readonly List<Button> crumbButtons = new List<Button>();
        readonly List<RadioButton> floorButtons = new List<RadioButton>();

        // ---- state
        readonly List<AtlasMarker> markers = new List<AtlasMarker>();
        ImageSource picture;
        double pw, ph;                       // the picture's size in pixels
        double scale = 1, fit = 1, ox, oy;   // DIPs per picture pixel; where the picture's top-left sits in the stage
        bool userMoved;                      // false while the view is "fitted": a resize refits
        int gen;                             // bumps with every map change: late answers for an older map are dropped
        List<MarkerCluster> clusters;
        MarkerCluster hover;
        static int groupCounter;
        readonly string group = "atlasfloor" + (++groupCounter);

        public AtlasMap()
        {
            Focusable = false;
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // ---- toolbar (36 high): breadcrumb, floors, zoom
            var bar = new Grid { Margin = new Thickness(0, 0, 0, 8), MinHeight = 32 }; toolbar = bar;
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            crumbs.Margin = new Thickness(0, 0, 8, 0);
            Grid.SetColumn(crumbs, 0); bar.Children.Add(crumbs);
            floorHost.Child = floorPanel;
            floorHost.Margin = new Thickness(0, 0, 8, 0);
            StyleOf(floorHost, "SegmentHost");
            Grid.SetColumn(floorHost, 1); bar.Children.Add(floorHost);
            var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            zoomOut = Tool("\uE71F", "Zoom out", () => ZoomBy(1 / 1.5));
            zoomIn = Tool("\uE8A3", "Zoom in", () => ZoomBy(1.5));
            zoomFit = Tool("\uE73F", "Fit the map", ResetView);
            tools.Children.Add(zoomOut); tools.Children.Add(zoomIn); tools.Children.Add(zoomFit);
            Grid.SetColumn(tools, 2); bar.Children.Add(tools);
            Grid.SetRow(bar, 0); root.Children.Add(bar);

            // ---- stage: the picture + markers (one element), the hover text, the empty / loading states
            view = new MapView(this);
            stage.Background = Res<Brush>("Brush.Bg") ?? Brushes.Black;
            stage.Children.Add(view);
            skeleton.IsHitTestVisible = false;
            stage.Children.Add(skeleton);
            stateGlyph.FontFamily = Res<FontFamily>("Font.Icons") ?? new FontFamily("Segoe MDL2 Assets");
            stateGlyph.FontSize = 32;
            stateGlyph.Foreground = Res<Brush>("Brush.Text.Tertiary") ?? Brushes.Gray;
            StyleOf(stateTitle, "Text.Body"); StyleOf(stateHint, "Text.Secondary");
            stateHint.Margin = new Thickness(0, 4, 0, 0); stateTitle.Margin = new Thickness(0, 8, 0, 0);
            stateBox.Children.Add(stateGlyph); stateBox.Children.Add(stateTitle); stateBox.Children.Add(stateHint);
            stage.Children.Add(stateBox);
            // the hover text is a game tooltip too (the same panel colours and font as GameTooltip)
            tipBox.Background = GameTooltip.Brush("Brush.GameTip.Bg");
            tipBox.BorderBrush = GameTooltip.Brush("Brush.GameTip.Border"); tipBox.BorderThickness = new Thickness(1);
            tipBox.CornerRadius = TryFindResource("Radius.GameTip") is CornerRadius gtr ? gtr : default(CornerRadius); tipBox.Padding = new Thickness(12, 8, 12, 8);
            System.Windows.Documents.TextElement.SetFontFamily(tipBox, ElansAddonHub.Services.GameFont.Family);
            tipBox.Child = tipStack;
            tipLayer.Children.Add(tipBox);
            stage.Children.Add(tipLayer);
            stage.SizeChanged += (s, e) => { stage.Clip = new RectangleGeometry(new Rect(0, 0, stage.ActualWidth, stage.ActualHeight), 12, 12); OnStageResized(); };
            Grid.SetRow(stage, 1); root.Children.Add(stage);
            // keyboard focus ring (DESIGN.md: 2 px accent, 2 px outside, keyboard focus only)
            focusRing.BorderThickness = new Thickness(2); focusRing.CornerRadius = new CornerRadius(14); focusRing.Margin = new Thickness(-4);
            focusRing.IsHitTestVisible = false; focusRing.Visibility = Visibility.Collapsed;
            focusRing.SetResourceReference(Border.BorderBrushProperty, "Brush.Focus");
            Grid.SetRow(focusRing, 1); root.Children.Add(focusRing);
            Content = root;
            SetState(ViewState.Empty, "Pick a place", "Choose a zone, a dungeon or a raid to see its map.");
            Loaded += (s, e) =>
            {
                MapData.Changed -= OnMapDataChanged; MapData.Changed += OnMapDataChanged;
                if (released) { released = false; if (UiMapId != 0) ShowMap(UiMapId); }
            };
            Unloaded += (s, e) =>
            {
                MapData.Changed -= OnMapDataChanged;
                gen++;
                tipTimer?.Stop();
                // off screen the picture is let go (a continent is tens of MB decoded); it comes back from the PNG when the control is shown again
                if (fromFiles && picture != null) { picture = null; released = true; clusters = null; hover = null; HideTip(); view.InvalidateVisual(); }
            };
        }

        readonly Border focusRing = new Border();
        Grid toolbar;
        // a thumbnail: no breadcrumb, floors or zoom buttons above the picture (the entity page's rail; a click on it opens the real map)
        public bool Compact { get => toolbar != null && toolbar.Visibility == Visibility.Collapsed; set { if (toolbar != null) toolbar.Visibility = value ? Visibility.Collapsed : Visibility.Visible; } }
        bool fromFiles, released;
        public bool PictureHeldForTest => picture != null;
        public bool ReleasedForTest => released;

        // map data arrived (or went away) while this control was showing "not available": try again
        void OnMapDataChanged()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (UiMapId != 0 && State == ViewState.Unavailable && MapData.Ready) ShowMap(UiMapId);
            }));
        }
        public void MapDataChangedForTest() => OnMapDataChanged();

        static T Res<T>(string key) where T : class => Application.Current?.TryFindResource(key) as T;
        static void StyleOf(FrameworkElement el, string key) { var s = Res<Style>(key); if (s != null) el.Style = s; }

        Button Tool(string glyph, string tip, Action click)
        {
            var b = new Button { Content = glyph, ToolTip = tip, Width = 32, Height = 32, FontSize = 16 };
            StyleOf(b, "IconButton");
            b.Width = 32; b.Height = 32;
            b.Click += (s, e) => click();
            System.Windows.Automation.AutomationProperties.SetName(b, tip);
            return b;
        }

        // =============================================================================================================================
        // showing a map
        public void ShowMap(int uiMapId)
        {
            int my = ++gen;
            if (route.Count > 0 && routeMap != uiMapId) ClearRoute();
            UiMapId = uiMapId;
            var idx = MapData.Index;
            ClearPicture();
            BuildChrome(idx, uiMapId);
            if (idx == null)
            {
                SetState(ViewState.Unavailable, "Map data is not available", MapData.Status == MapData.State.Disabled ? "Map data is switched off in Settings." : (string.IsNullOrEmpty(MapData.Reason) ? "The Outpost reads it from your WoW installation." : MapData.Reason));
                MapChanged?.Invoke(uiMapId);
                return;
            }
            var m = idx.Get(uiMapId);
            if (m == null) SetState(ViewState.NoArt, "This map is not in the game files", "");
            else if (!idx.HasArt(uiMapId)) SetState(ViewState.NoArt, m.Name + " has no map picture in the game files", "The game does not ship art for it.");
            else
            {
                var path = MapRender.TryGet(uiMapId);
                if (path != null) LoadPicture(my, uiMapId, path);
                else
                {
                    SetState(ViewState.Loading, "", "");
                    MapRender.Request(uiMapId, p => Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (my != gen) return;
                        if (p == null) SetState(ViewState.Failed, "The map could not be prepared", string.IsNullOrEmpty(MapRender.Problem) ? "Some of its pieces are missing from the game files." : MapRender.Problem);
                        else LoadPicture(my, uiMapId, p);
                    })));
                }
            }
            MapChanged?.Invoke(uiMapId);
        }

        // an instance by its map id: its first floor (or a note when the game has no picture of it)
        public void ShowInstance(int mapId)
        {
            var idx = MapData.Index;
            var inst = idx?.Instance(mapId);
            if (inst == null || inst.Floors.Count == 0)
            {
                int my = ++gen; ClearPicture(); UiMapId = 0;
                BuildChrome(idx, 0, inst);
                SetState(inst == null ? ViewState.Unavailable : ViewState.NoArt, inst == null ? "Map data is not available" : inst.Name + " has no map in the game files",
                    inst == null ? "" : "The game does not ship a picture of this " + inst.Kind.ToLowerInvariant() + ".");
                return;
            }
            ShowMap(inst.Floors[0].Id);
        }

        // show a picture you already have (the self-test, or a caller with its own art); the fractions of markers refer to it
        public void SetPicture(int uiMapId, ImageSource image)
        {
            ++gen;
            UiMapId = uiMapId;
            BuildChrome(MapData.Index, uiMapId);
            ApplyPicture(image, false);
            MapChanged?.Invoke(uiMapId);
        }

        void LoadPicture(int my, int uiMapId, string path)
        {
            if (State != ViewState.Loading) SetState(ViewState.Loading, "", "");
            Task.Run(() => MapRender.Load(path)).ContinueWith(t =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (my != gen) return;
                    if (t.Result == null) SetState(ViewState.Failed, "The map could not be shown", "Its picture could not be read.");
                    else ApplyPicture(t.Result, true);
                }));
            });
        }

        void ApplyPicture(ImageSource img, bool files)
        {
            picture = img; fromFiles = files; released = false;
            pw = (img as System.Windows.Media.Imaging.BitmapSource)?.PixelWidth ?? img.Width;
            ph = (img as System.Windows.Media.Imaging.BitmapSource)?.PixelHeight ?? img.Height;
            userMoved = false;
            SetState(ViewState.Ready, "", "");
            Refit();
        }

        void ClearPicture()
        {
            picture = null; pw = ph = 0; hover = null; HideTip(); clusters = null;
            view.InvalidateVisual();
        }

        void SetState(ViewState s, string title, string hint)
        {
            State = s; StateText = (title + " " + hint).Trim();
            skeleton.Visibility = s == ViewState.Loading ? Visibility.Visible : Visibility.Collapsed;
            skeleton.Show = s == ViewState.Loading;
            stateBox.Visibility = s == ViewState.Ready || s == ViewState.Loading ? Visibility.Collapsed : Visibility.Visible;
            stateGlyph.Text = s == ViewState.Failed ? "\uE783" : s == ViewState.Empty ? "\uE707" : "\uE909";
            stateTitle.Text = title; stateHint.Text = hint;
            stateHint.Visibility = hint.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            view.InvalidateVisual();
        }

        // =============================================================================================================================
        // breadcrumb + floors
        void BuildChrome(MapIndex idx, int uiMapId, InstanceInfo inst = null)
        {
            crumbs.Children.Clear(); crumbNames.Clear(); crumbButtons.Clear();
            floorPanel.Children.Clear(); floorNames.Clear(); floorButtons.Clear(); floorSelected = -1;
            var chain = idx != null && uiMapId != 0 ? idx.Breadcrumb(uiMapId) : new List<UiMapInfo>();
            if (chain.Count == 0 && inst != null) { var t = Text(inst.Name, "Text.RowTitle"); crumbs.Children.Add(t); crumbNames.Add(inst.Name); }
            for (int i = 0; i < chain.Count; i++)
            {
                var m = chain[i];
                if (i > 0)
                {
                    var sep = new TextBlock { Text = "\uE76C", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0), FontSize = 12 };
                    sep.FontFamily = Res<FontFamily>("Font.Icons") ?? sep.FontFamily;
                    sep.Foreground = Res<Brush>("Brush.Text.Tertiary") ?? Brushes.Gray;
                    crumbs.Children.Add(sep);
                }
                crumbNames.Add(m.Name);
                if (i == chain.Count - 1) crumbs.Children.Add(Text(m.Name, "Text.RowTitle"));
                else
                {
                    var id = m.Id;
                    var b = new Button { Content = m.Name, Height = 28, Padding = new Thickness(8, 0, 8, 0) };
                    StyleOf(b, "GhostButton");
                    b.Click += (s, e) => ShowMap(id);
                    crumbs.Children.Add(b); crumbButtons.Add(b);
                }
            }
            var floors = idx != null && uiMapId != 0 ? idx.FloorsOf(uiMapId) : new List<UiMapInfo>();
            if (floors.Count > 1)
            {
                for (int i = 0; i < floors.Count; i++)
                {
                    var f = floors[i]; int fi = i;
                    var name = idx.FloorName(f.Id);
                    if (string.IsNullOrWhiteSpace(name)) name = "Floor " + (i + 1);
                    var rb = new RadioButton { Content = name, GroupName = group, IsChecked = f.Id == uiMapId };
                    StyleOf(rb, "Segment");
                    var fid = f.Id;
                    rb.Checked += (s, e) => { if (fid != UiMapId) ShowMap(fid); };
                    floorPanel.Children.Add(rb); floorNames.Add(name); floorButtons.Add(rb);
                    if (f.Id == uiMapId) floorSelected = i;
                }
                floorHost.Visibility = Visibility.Visible;
            }
            else floorHost.Visibility = Visibility.Collapsed;
        }

        static TextBlock Text(string text, string style)
        {
            var t = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            StyleOf(t, style);
            return t;
        }

        // test hooks for the chrome
        public void ClickBreadcrumbForTest(int index) { if (index >= 0 && index < crumbButtons.Count) crumbButtons[index].RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
        public void SelectFloorForTest(int index) { if (index >= 0 && index < floorButtons.Count) floorButtons[index].IsChecked = true; }

        // =============================================================================================================================
        // view: scale, offset, pan, zoom
        void OnStageResized() { if (picture == null) return; if (!userMoved) Refit(); else { Reclamp(); view.InvalidateVisual(); } }

        double VW => Math.Max(1, stage.ActualWidth);
        double VH => Math.Max(1, stage.ActualHeight);

        void Refit()
        {
            if (picture == null || pw <= 0 || ph <= 0) return;
            fit = Math.Min(VW / pw, VH / ph);
            scale = fit;
            ox = (VW - pw * scale) / 2; oy = (VH - ph * scale) / 2;
            userMoved = false;
            Changed();
        }

        void Reclamp()
        {
            double mw = pw * scale, mh = ph * scale;
            fit = Math.Min(VW / pw, VH / ph);
            scale = Math.Max(fit, Math.Min(scale, fit * MaxZoom));
            mw = pw * scale; mh = ph * scale;
            ox = mw <= VW ? (VW - mw) / 2 : Math.Min(0, Math.Max(VW - mw, ox));
            oy = mh <= VH ? (VH - mh) / 2 : Math.Min(0, Math.Max(VH - mh, oy));
            Changed();
        }

        void Changed() { clusters = null; hover = null; HideTip(); view.InvalidateVisual(); }

        public void ResetView() { userMoved = false; Refit(); }

        public void ZoomBy(double factor) => ZoomAt(new Point(VW / 2, VH / 2), factor);

        // zoom keeping the point under `anchor` (stage coordinates) where it is
        public void ZoomAt(Point anchor, double factor)
        {
            if (picture == null) return;
            double ns = Math.Max(fit, Math.Min(fit * MaxZoom, scale * factor));
            if (Math.Abs(ns - scale) < 1e-9) return;
            double k = ns / scale;
            ox = anchor.X - (anchor.X - ox) * k; oy = anchor.Y - (anchor.Y - oy) * k;
            scale = ns; userMoved = true;
            Reclamp();
        }

        public void PanBy(double dx, double dy)
        {
            if (picture == null) return;
            ox += dx; oy += dy; userMoved = true;
            Reclamp();
        }

        // centre the view on a point of the picture at a given zoom (factor over fitted)
        public void ZoomToFraction(double fx, double fy, double zoom)
        {
            if (picture == null) return;
            scale = Math.Max(fit, Math.Min(fit * MaxZoom, fit * zoom));
            ox = VW / 2 - fx * pw * scale; oy = VH / 2 - fy * ph * scale;
            userMoved = true;
            Reclamp();
        }

        // ---- coordinates: fractions of the picture <-> the stage (device independent pixels from the stage's top-left)
        public Point FractionToViewport(double fx, double fy) => new Point(ox + fx * pw * scale, oy + fy * ph * scale);
        public Point? ViewportToFraction(Point p)
        {
            if (pw <= 0 || ph <= 0 || scale <= 0) return null;
            return new Point((p.X - ox) / (pw * scale), (p.Y - oy) / (ph * scale));
        }
        // a world position (map id, x, y) as a point on the shown map; null when the map does not show it
        public Point? WorldToFraction(int mapId, double x, double y)
        {
            var idx = MapData.Index;
            var p = idx?.WorldToMap(mapId, x, y, UiMapId);
            return p == null ? (Point?)null : new Point(p.Value.Fx, p.Value.Fy);
        }

        // =============================================================================================================================
        // markers
        public void AddMarkers(IEnumerable<AtlasMarker> items)
        {
            if (items == null) return;
            foreach (var m in items) if (m != null && !double.IsNaN(m.Fx) && !double.IsNaN(m.Fy)) markers.Add(m);
            clusters = null; hover = null; HideTip(); view.InvalidateVisual();
        }

        public void ClearMarkers() { markers.Clear(); clusters = null; hover = null; HideTip(); view.InvalidateVisual(); }

        // ---- a route: numbered stops joined by a line (a closed loop), drawn over the picture and under/over the markers; belongs to the map shown when it was set
        public sealed class RouteStop
        {
            public double Fx, Fy;
            public string Label = "";
            public bool Forever = true;                  // false = a Classic baseline position (drawn fainter)
            public RouteStop() { }
            public RouteStop(double fx, double fy, string label = "", bool forever = true) { Fx = fx; Fy = fy; Label = label ?? ""; Forever = forever; }
        }

        readonly List<RouteStop> route = new List<RouteStop>();
        int routeMap;
        public int RouteCount => route.Count;
        public IReadOnlyList<RouteStop> RouteStops => route;
        public int RouteMap => routeMap;

        public void SetRoute(IEnumerable<RouteStop> stops)
        {
            route.Clear();
            routeMap = UiMapId;
            if (stops != null) foreach (var s in stops) if (s != null && !double.IsNaN(s.Fx) && !double.IsNaN(s.Fy)) route.Add(s);
            view.InvalidateVisual();
        }

        public void ClearRoute() { if (route.Count == 0) return; route.Clear(); view.InvalidateVisual(); }

        // where stop i (0-based) is drawn in the stage, for tests
        public Point RouteStopPointForTest(int i) => FractionToViewport(route[i].Fx, route[i].Fy);

        public IReadOnlyList<MarkerCluster> Clusters
        {
            get
            {
                if (clusters == null)
                    clusters = MarkerCluster.Build(markers, m => { var p = FractionToViewport(m.Fx, m.Fy); return new KeyValuePair<double, double>(p.X, p.Y); }, ClusterRadius);
                return clusters;
            }
        }

        static double RadiusOf(MarkerCluster c) => c.Single ? 6 : (c.Count >= 100 ? 14 : c.Count >= 10 ? 12 : 10);

        public MarkerCluster HitTestMarker(Point p)
        {
            MarkerCluster best = null; double bestD = double.MaxValue;
            foreach (var c in Clusters)
            {
                double d = Math.Sqrt((c.X - p.X) * (c.X - p.X) + (c.Y - p.Y) * (c.Y - p.Y));
                if (d <= RadiusOf(c) + 3 && d < bestD) { best = c; bestD = d; }
            }
            return best;
        }

        // a click at a stage point: a single marker raises LinkClicked, a cluster zooms in on its members
        public void ClickAt(Point p)
        {
            var hit = HitTestMarker(p);
            if (hit == null) return;
            if (hit.Single)
            {
                if (!string.IsNullOrEmpty(hit.Members[0].LinkId)) LinkClicked?.Invoke(hit.Members[0].LinkId);
                return;
            }
            double minX = hit.Members.Min(m => m.Fx), maxX = hit.Members.Max(m => m.Fx), minY = hit.Members.Min(m => m.Fy), maxY = hit.Members.Max(m => m.Fy);
            double spanX = Math.Max(1e-6, (maxX - minX) * pw), spanY = Math.Max(1e-6, (maxY - minY) * ph);   // in picture pixels
            double want = Math.Min(VW * 0.6 / spanX, VH * 0.6 / spanY);                                      // scale that shows the group at 60 %
            double z = Math.Max(Zoom * 1.5, want / fit);
            ZoomToFraction((minX + maxX) / 2, (minY + maxY) / 2, Math.Min(MaxZoom, z));
        }

        // ---- hover text
        static TextBlock GameLine(string text, double size, string brushKey) => new TextBlock { Text = text, FontSize = size, Foreground = GameTooltip.Brush(brushKey), TextWrapping = TextWrapping.Wrap };

        void ShowTip(MarkerCluster c)
        {
            tipStack.Children.Clear();
            if (c.Single)
            {
                var m = c.Members[0];
                tipStack.Children.Add(GameLine(string.IsNullOrEmpty(m.Label) ? KindName(m.Kind) : m.Label, 15, "Brush.GameTip.Text"));
                if (!string.IsNullOrEmpty(m.Tooltip)) { var t = GameLine(m.Tooltip, 13, "Brush.GameTip.Text"); t.MaxWidth = 280; tipStack.Children.Add(t); }
                else tipStack.Children.Add(GameLine(KindName(m.Kind), 13, "Brush.GameTip.Text"));
            }
            else
            {
                tipStack.Children.Add(GameLine(c.Count + " places here", 15, "Brush.GameTip.Text"));
                foreach (var m in c.Members.Take(6)) tipStack.Children.Add(GameLine(string.IsNullOrEmpty(m.Label) ? KindName(m.Kind) : m.Label, 13, "Brush.GameTip.Text"));
                if (c.Count > 6) tipStack.Children.Add(GameLine("and " + (c.Count - 6) + " more", 12, "Brush.GameTip.Muted"));
                tipStack.Children.Add(GameLine("Click to zoom in", 12, "Brush.GameTip.Muted"));
            }
            tipBox.Visibility = Visibility.Visible;
            tipBox.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var sz = tipBox.DesiredSize;
            double x = c.X + 16, y = c.Y + 12;
            if (x + sz.Width > VW - 8) x = c.X - 16 - sz.Width;
            if (y + sz.Height > VH - 8) y = c.Y - 12 - sz.Height;
            Canvas.SetLeft(tipBox, Math.Max(8, x)); Canvas.SetTop(tipBox, Math.Max(8, y));
        }

        void HideTip() { tipBox.Visibility = Visibility.Collapsed; }

        public string TipTextForTest => tipBox.Visibility == Visibility.Visible ? string.Join("|", tipStack.Children.OfType<TextBlock>().Select(t => t.Text)) : null;
        public void HoverAtForTest(Point p) => OnHover(p, true);
        public void HoverDelayedForTest(Point p) => OnHover(p, false);
        public string TipStateForTest => (hover != null ? "hover " : "nohover ") + (tipTimer != null && tipTimer.IsEnabled ? "timer-on " : "timer-off ") + (tipBox.Visibility == Visibility.Visible ? "tip-on" : "tip-off");

        // DESIGN.md: a tooltip waits 300 ms; the ring on the marker is instant
        public int TipDelayMs = 300;
        System.Windows.Threading.DispatcherTimer tipTimer;
        void TipTick(object s, EventArgs e) { tipTimer.Stop(); if (hover != null) ShowTip(hover); }

        void OnHover(Point p, bool immediate = false)
        {
            var hit = HitTestMarker(p);
            if (hit == hover) return;
            hover = hit;
            HideTip(); tipTimer?.Stop();
            if (hit != null)
            {
                if (immediate || TipDelayMs <= 0) ShowTip(hit);
                else
                {
                    if (tipTimer == null) { tipTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Normal); tipTimer.Tick += TipTick; }
                    tipTimer.Interval = TimeSpan.FromMilliseconds(TipDelayMs); tipTimer.Start();
                }
            }
            view.Cursor = hit != null && (hit.Single ? !string.IsNullOrEmpty(hit.Members[0].LinkId) : true) ? Cursors.Hand : (dragging ? Cursors.SizeAll : Cursors.Arrow);
            view.InvalidateVisual();
        }

        public static string KindName(MarkerKind k)
        {
            switch (k)
            {
                case MarkerKind.Spawn: return "Spawn";
                case MarkerKind.Find: return "Find";
                case MarkerKind.Vendor: return "Vendor";
                case MarkerKind.Node: return "Node";
                case MarkerKind.Entrance: return "Entrance";
                default: return "Quest";
            }
        }

        public static Brush KindBrush(MarkerKind k)
        {
            string key;
            switch (k)
            {
                case MarkerKind.Spawn: key = "Brush.Cal.Raid"; break;
                case MarkerKind.Find: key = "Brush.Cal.Mine"; break;
                case MarkerKind.Vendor: key = "Brush.Info"; break;
                case MarkerKind.Node: key = "Brush.Cal.Holiday.3"; break;
                case MarkerKind.Entrance: key = "Brush.Accent"; break;
                default: key = "Brush.Cal.Holiday.4"; break;
            }
            return Res<Brush>(key) ?? Brushes.White;
        }

        // =============================================================================================================================
        // mouse (forwarded from the view)
        bool dragging, moved;
        Point downAt, lastAt;

        void PtDown(Point p) { dragging = true; moved = false; downAt = lastAt = p; }
        void PtMove(Point p, bool left)
        {
            if (dragging && left)
            {
                if (!moved && (Math.Abs(p.X - downAt.X) > 3 || Math.Abs(p.Y - downAt.Y) > 3)) { moved = true; hover = null; HideTip(); view.Cursor = Cursors.SizeAll; }
                if (moved) { PanBy(p.X - lastAt.X, p.Y - lastAt.Y); lastAt = p; return; }
            }
            lastAt = p;
            OnHover(p);
        }
        void PtUp(Point p)
        {
            bool wasClick = dragging && !moved;
            dragging = false; view.Cursor = Cursors.Arrow;
            if (wasClick) ClickAt(p);
            OnHover(p);
        }
        void PtLeave() { dragging = false; hover = null; HideTip(); view.InvalidateVisual(); }

        // the same path a mouse takes: press at `from`, move to `to` in a few steps, release (a drag pans; a press and release without moving is a click)
        public void SimulateDragForTest(Point from, Point to)
        {
            PtDown(from);
            for (int i = 1; i <= 4; i++) PtMove(new Point(from.X + (to.X - from.X) * i / 4, from.Y + (to.Y - from.Y) * i / 4), true);
            PtUp(to);
        }
        public bool WasDragForTest => moved;
        void Wheel(Point p, int delta) => ZoomAt(p, delta > 0 ? 1.25 : 1 / 1.25);

        // =============================================================================================================================
        // the picture and the markers, drawn by hand (one visual, so panning and zooming cost nothing but a redraw)
        sealed class MapView : FrameworkElement
        {
            readonly AtlasMap o;
            public MapView(AtlasMap owner) { o = owner; ClipToBounds = true; Cursor = Cursors.Arrow; Focusable = true; FocusVisualStyle = null; }
            protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnGotKeyboardFocus(e); o.focusRing.Visibility = Mouse.LeftButton == MouseButtonState.Pressed ? Visibility.Collapsed : Visibility.Visible; }
            protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnLostKeyboardFocus(e); o.focusRing.Visibility = Visibility.Collapsed; }
            protected override void OnKeyDown(KeyEventArgs e)
            {
                switch (e.Key)
                {
                    case Key.Left: o.PanBy(64, 0); break;
                    case Key.Right: o.PanBy(-64, 0); break;
                    case Key.Up: o.PanBy(0, 64); break;
                    case Key.Down: o.PanBy(0, -64); break;
                    case Key.Add: case Key.OemPlus: o.ZoomBy(1.5); break;
                    case Key.Subtract: case Key.OemMinus: o.ZoomBy(1 / 1.5); break;
                    case Key.D0: case Key.NumPad0: case Key.Home: o.ResetView(); break;
                    default: return;
                }
                e.Handled = true;
            }

            protected override Size MeasureOverride(Size s) => new Size(double.IsInfinity(s.Width) ? 0 : s.Width, double.IsInfinity(s.Height) ? 0 : s.Height);

            protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) { Focus(); CaptureMouse(); o.PtDown(e.GetPosition(this)); e.Handled = true; }
            protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { ReleaseMouseCapture(); o.PtUp(e.GetPosition(this)); e.Handled = true; }
            protected override void OnMouseMove(MouseEventArgs e) { o.PtMove(e.GetPosition(this), e.LeftButton == MouseButtonState.Pressed); }
            protected override void OnMouseLeave(MouseEventArgs e) { o.PtLeave(); }
            protected override void OnMouseWheel(MouseWheelEventArgs e) { o.Wheel(e.GetPosition(this), e.Delta); e.Handled = true; }

            protected override void OnRender(DrawingContext dc)
            {
                dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));     // hit testing
                if (o.picture == null) return;
                var rect = new Rect(o.ox, o.oy, o.pw * o.scale, o.ph * o.scale);
                RenderOptions.SetBitmapScalingMode(this, o.scale < 1 ? BitmapScalingMode.HighQuality : BitmapScalingMode.Linear);
                dc.DrawImage(o.picture, rect);
                DrawRouteLine(dc);
                if (o.markers.Count == 0) { DrawRouteStops(dc); return; }

                var halo = Res<Brush>("Brush.Bg") ?? Brushes.Black;
                var haloPen = new Pen(halo, 2); haloPen.Freeze();
                var ring = new Pen(Res<Brush>("Brush.Text.Primary") ?? Brushes.White, 2); ring.Freeze();
                var onKind = Res<Brush>("Brush.OnAccent") ?? Brushes.Black;
                var mixed = Res<Brush>("Brush.Surface3") ?? Brushes.DimGray;
                var text = Res<Brush>("Brush.Text.Primary") ?? Brushes.White;
                var face = new Typeface(Res<FontFamily>("Font.Ui") ?? new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
                double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
                foreach (var c in o.Clusters)
                {
                    if (c.X < -20 || c.Y < -20 || c.X > ActualWidth + 20 || c.Y > ActualHeight + 20) continue;
                    var ctr = new Point(c.X, c.Y);
                    double r = RadiusOf(c);
                    if (c == o.hover) dc.DrawEllipse(null, ring, ctr, r + 4, r + 4);
                    if (c.Single)
                    {
                        var fill = KindBrush(c.Kind);
                        DrawGlyph(dc, c.Kind, ctr, r, fill, haloPen, onKind);
                    }
                    else
                    {
                        var fill = c.SameKind ? KindBrush(c.Kind) : mixed;
                        dc.DrawEllipse(fill, haloPen, ctr, r, r);
                        var ft = new FormattedText(c.Count > 999 ? "999+" : c.Count.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 11, c.SameKind ? onKind : text, dpi);
                        dc.DrawText(ft, new Point(ctr.X - ft.Width / 2, ctr.Y - ft.Height / 2));
                    }
                }
                DrawRouteStops(dc);
            }

            void DrawRouteLine(DrawingContext dc)
            {
                if (o.route.Count < 2) return;
                var accent = Res<Brush>("Brush.Accent") ?? Brushes.Orange;
                var halo = new Pen(Res<Brush>("Brush.Bg") ?? Brushes.Black, 5) { LineJoin = PenLineJoin.Round }; halo.Freeze();
                var pen = new Pen(accent, 2.5) { LineJoin = PenLineJoin.Round, DashStyle = DashStyles.Solid }; pen.Freeze();
                var g = new StreamGeometry();
                using (var ctx = g.Open())
                {
                    ctx.BeginFigure(o.FractionToViewport(o.route[0].Fx, o.route[0].Fy), false, o.route.Count > 2);
                    for (int i = 1; i < o.route.Count; i++) ctx.LineTo(o.FractionToViewport(o.route[i].Fx, o.route[i].Fy), true, true);
                }
                g.Freeze();
                dc.DrawGeometry(null, halo, g);
                dc.DrawGeometry(null, pen, g);
            }

            void DrawRouteStops(DrawingContext dc)
            {
                if (o.route.Count == 0) return;
                var accent = Res<Brush>("Brush.Accent") ?? Brushes.Orange;
                var faint = Res<Brush>("Brush.Surface3") ?? Brushes.DimGray;
                var ink = Res<Brush>("Brush.OnAccent") ?? Brushes.Black;
                var text = Res<Brush>("Brush.Text.Primary") ?? Brushes.White;
                var halo = new Pen(Res<Brush>("Brush.Bg") ?? Brushes.Black, 2); halo.Freeze();
                var face = new Typeface(Res<FontFamily>("Font.Ui") ?? new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
                double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
                for (int i = 0; i < o.route.Count; i++)
                {
                    var p = o.FractionToViewport(o.route[i].Fx, o.route[i].Fy);
                    if (p.X < -20 || p.Y < -20 || p.X > ActualWidth + 20 || p.Y > ActualHeight + 20) continue;
                    bool fv = o.route[i].Forever;
                    dc.DrawEllipse(fv ? accent : faint, halo, p, 9, 9);
                    var ft = new FormattedText((i + 1).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, i >= 99 ? 8 : 10.5, fv ? ink : text, dpi);
                    dc.DrawText(ft, new Point(p.X - ft.Width / 2, p.Y - ft.Height / 2));
                }
            }

            // one marker: the colour names the kind and so does the shape (spawn circle, find diamond, vendor square, node triangle, quest ringed dot)
            static void DrawGlyph(DrawingContext dc, MarkerKind kind, Point c, double r, Brush fill, Pen halo, Brush ink)
            {
                switch (kind)
                {
                    case MarkerKind.Find:
                        dc.DrawGeometry(fill, halo, Poly(new Point(c.X, c.Y - r - 1), new Point(c.X + r + 1, c.Y), new Point(c.X, c.Y + r + 1), new Point(c.X - r - 1, c.Y)));
                        break;
                    case MarkerKind.Vendor:
                        dc.DrawRoundedRectangle(fill, halo, new Rect(c.X - r + 1, c.Y - r + 1, 2 * r - 2, 2 * r - 2), 2, 2);
                        break;
                    case MarkerKind.Node:
                        dc.DrawGeometry(fill, halo, Poly(new Point(c.X, c.Y - r - 1), new Point(c.X + r + 1, c.Y + r - 1), new Point(c.X - r - 1, c.Y + r - 1)));
                        break;
                    case MarkerKind.Entrance:
                        dc.DrawGeometry(fill, halo, new RectangleGeometry(new Rect(c.X - r, c.Y - r, 2 * r, 2 * r + 1), r, r));
                        dc.DrawRoundedRectangle(ink, null, new Rect(c.X - 2, c.Y, 4, r + 1), 1.5, 1.5);
                        break;
                    case MarkerKind.Quest:
                        dc.DrawEllipse(fill, halo, c, r, r);
                        dc.DrawEllipse(ink, null, c, 2, 2);
                        break;
                    default:
                        dc.DrawEllipse(fill, halo, c, r, r);
                        break;
                }
            }

            static Geometry Poly(params Point[] pts)
            {
                var g = new StreamGeometry();
                using (var ctx = g.Open()) { ctx.BeginFigure(pts[0], true, true); ctx.PolyLineTo(pts.Skip(1).ToList(), true, false); }
                g.Freeze();
                return g;
            }
        }
    }
}
