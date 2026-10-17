using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Maps;
using ElansAddonHub.Services.WorldMap;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace ElansAddonHub
{
    // ============================================================================================================================
    // The Atlas "World map": a seamless, zoomable map of a continent drawn from the player's own game files (tile pyramid: WorldMapPyramid) in a
    // sandboxed WebView2 page, either flat (Leaflet, src\WorldMap\index.html) or as 3D terrain (three.js, src\WorldMap\view3d.html, height grids from
    // WorldMapTerrain). This control is the host:
    //   * the continent switch and the 2D | 3D switch (WPF, above the page - a WebView2 cannot be overlapped) and the build progress / notes (WPF,
    //     shown INSTEAD of the page)
    //   * the page, served from <data>\atlas\worldmap through a private virtual host (https://worldmap.outpost.local/), nothing from the internet
    //   * the message protocol (WorldMapProtocol): the page's messages are checked, the layers are built on request and posted, "Navigate here" copies
    //     a /etk way command, a click opens an Atlas id
    // Sandbox: dev tools off, no host objects, no new windows, navigation only inside the virtual host (everything else is cancelled), every request
    // to another host is answered with 403, every permission denied, an InPrivate profile of its own. Without the WebView2 runtime the control shows a
    // calm note instead of the page; if the 3D page cannot get WebGL the flat map is shown with a note. The two pages are two documents of the same
    // origin: switching mode navigates the WebView to the other one and hands over the view (WorldMapMath: world x / y, and the zoom <-> distance rule).
    // ============================================================================================================================
    public sealed class WorldMapView : Grid
    {
        // ---- surface
        public event Action<string> OpenAtlas;                 // an Atlas id the page asked for ("npc:46")
        public event Action<string> SubChanged;                // the canonical sub-path of "worldmap:" after the map moved or the continent changed
        public Settings Settings;                              // layers + last continent are remembered here
        public Action SaveSettings;
        public Action<string> ClipboardSeamForTest;
        public string LastCommandForTest { get; private set; }
        public string LastToastForTest { get; private set; }
        public readonly List<string> ReceivedForTest = new List<string>();
        public readonly List<string> RefusedForTest = new List<string>();
        public bool PageReady { get; private set; }            // the page said its first tiles are in
        public bool PageInited { get; private set; }
        public int MapId => mapId;
        public string CurrentSub => WorldMapIds.SubOf(mapId, slug, hasView, vx, vy, vz, false, mode3d ? 3 : 0);
        public string Note => noteText.Text;
        public string ProgressText => overlayTitle.Text;
        public bool OverlayShown => overlay.Visibility == Visibility.Visible;
        public bool NoWebViewShown { get; private set; }
        public bool WebShown => web != null && web.Visibility == Visibility.Visible;
        public IReadOnlyList<string> Continents => segs.Select(s => (string)s.Content).ToList();
        public string SelectedContinent => segs.FirstOrDefault(s => s.IsChecked == true)?.Content as string ?? "";
        // 3D
        public bool Is3D => mode3d;
        public bool GlOk { get; private set; }                 // the 3D page said WebGL works
        public string GlRenderer { get; private set; } = "";
        public string GlFallbackReason { get; private set; }   // set when the 3D page could not start and the flat map was shown instead
        public WorldMapMessage LastStats { get; private set; }
        public WorldMapMessage LastView3 { get; private set; }
        public bool ModeSegmentsShown => modeHost.Visibility == Visibility.Visible;
        public bool Segment3dChecked => seg3d.IsChecked == true;
        public string LoadedPage => loadedPage;
        public bool FrozenForTest => frozen;
        public int StaleViewsForTest { get; private set; }     // view reports dropped as older than the app's last goto
        public int ViewSeqForTest => viewSeq;
        public bool KeyHookForTest { get; private set; }
        public event Action PaletteRequested;                  // Ctrl+K was pressed while the web page had the keyboard (the page is another window: WPF never sees the key)
        public bool FrozenImageShownForTest => freezeImg != null && freezeImg.Source != null;
        public BitmapSource FrozenSnapshotForTest => freezeImg?.Source as BitmapSource;
        public static bool ForceNoGlForTest;                   // self-test: pretend WebGL is missing (the page is told to behave as if it were)
        // 2.70: problems are never a black or empty map - a status line for tiles that could not be read yet, a failure card with what failed, Retry,
        // "Copy details" (WorldMapReport) and "Use the flat map"; a page that does not start (or a browser part that stops) is noticed and logged
        public static int PageTimeoutMs = 15000;               // a page that has not said "ready" this long after it was opened has failed
        public static bool Ignore2dReadyForTest, Ignore3dReadyForTest;     // self-test: a page that never becomes ready
        public static Func<string> WebViewVersionForTest;
        public string StatusText => statusText.Text;
        public bool StatusShown => statusHost.Visibility == Visibility.Visible;
        public bool RetryShown => retryButton.Visibility == Visibility.Visible;
        public bool Try3dShown => try3dButton.Visibility == Visibility.Visible;
        public bool FailureShown => overlay.Visibility == Visibility.Visible && overlayCopy.Visibility == Visibility.Visible;
        public bool FlatButtonShown => overlayFlat.Visibility == Visibility.Visible;
        public string FailureTitle => overlayTitle.Text;
        public string FailureHint => overlayHint.Text;
        public string LastReportForTest { get; private set; }
        public string Problem => lastProblem ?? (GlFallbackReason != null ? "3D view not available: " + GlFallbackReason : "");
        public IReadOnlyList<string> PageErrors => pageErrors;
        public int TimeoutsForTest { get; private set; }
        public Task<WorldMapService.RetryResult> LastRetryForTest { get; private set; }

        // ---- parts
        readonly StackPanel bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        readonly Border segHost = new Border();
        readonly StackPanel segPanel = new StackPanel { Orientation = Orientation.Horizontal };
        readonly List<RadioButton> segs = new List<RadioButton>();
        readonly Border modeHost = new Border();
        readonly RadioButton seg2d = new RadioButton { Content = "2D", GroupName = "wm-mode" }, seg3d = new RadioButton { Content = "3D", GroupName = "wm-mode" };
        readonly TextBlock noteText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        readonly Grid stage = new Grid { Background = new SolidColorBrush(Color.FromRgb(0x08, 0x11, 0x10)) };
        readonly Border overlay = new Border { Visibility = Visibility.Visible };
        readonly TextBlock overlayGlyph = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center };
        readonly TextBlock overlayTitle = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
        readonly TextBlock overlayHint = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 };
        readonly ProgressBar overlayBar = new ProgressBar { Minimum = 0, Maximum = 1, Width = 320, Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed };
        readonly Button overlayButton = new Button { Margin = new Thickness(4, 0, 4, 0), HorizontalAlignment = HorizontalAlignment.Center, Visibility = Visibility.Collapsed, MinWidth = 120 };
        readonly Button overlayCopy = new Button { Margin = new Thickness(4, 0, 4, 0), Content = "Copy details", Visibility = Visibility.Collapsed };
        readonly Button overlayFlat = new Button { Margin = new Thickness(4, 0, 4, 0), Content = "Use the flat map", Visibility = Visibility.Collapsed };
        // the status line beside the switches: tiles that could not be read yet, a retry in progress, 3D switched off after a problem
        readonly StackPanel statusHost = new StackPanel { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        readonly TextBlock statusGlyph = new TextBlock { Text = "", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        readonly TextBlock statusText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        readonly Button retryButton = new Button { Content = "Retry", Margin = new Thickness(8, 0, 0, 0) }, statusCopy = new Button { Content = "Copy details", Margin = new Thickness(4, 0, 0, 0) }, try3dButton = new Button { Content = "Try 3D again", Margin = new Thickness(4, 0, 0, 0) };
        readonly List<string> pageErrors = new List<string>();
        string lastProblem;
        DispatcherTimer pageTimer;
        WebView2 web;
        Task<bool> webTask;

        // ---- state
        int mapId = -1; string slug = "";
        bool hasView; double vx, vy, vz;
        int viewSeq;                                           // counts the places the app sent the page to (init/goto/fit "s"); a view report from before the last one is stale
        WorldMapTarget target = new WorldMapTarget();
        int initGen, buildGen;
        string pendingInit;
        bool pageHello, suppressSeg, suppressMode;
        bool subscribed;
        bool mode3d, modeKnown;
        string loadedPage;                                     // "2d" | "3d": the document in the web view
        int borderWaitMap = -1;                                // a "Zone borders" request is waiting for the terrain of this map
        Action overlayAction;
        readonly WorldMapBuildingsHost bld;                    // the page's Buildings switch (3D buildings): see WorldMapBuildingsHost

        public WorldMapView()
        {
            bld = new WorldMapBuildingsHost(Post, a => { try { Dispatcher.BeginInvoke(a); return true; } catch (Exception) { return false; } }, () => mapId, () => Settings, () => SaveSettings?.Invoke());
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            segHost.Style = (Style)FindResource("SegmentHost");
            segHost.Child = segPanel;
            bar.Children.Add(segHost);
            modeHost.Style = (Style)FindResource("SegmentHost");
            modeHost.Margin = new Thickness(12, 0, 0, 0);
            foreach (var rb in new[] { seg2d, seg3d })
            {
                rb.Style = (Style)FindResource("Segment");
                System.Windows.Automation.AutomationProperties.SetName(rb, rb == seg2d ? "Flat map" : "3D terrain view");
                rb.Checked += (s, e) => { if (!suppressMode) SetMode(s == seg3d); };
            }
            seg2d.IsChecked = true;
            modeHost.Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { seg2d, seg3d } };
            bar.Children.Add(modeHost);
            noteText.Style = (Style)FindResource("Text.Caption");
            bar.Children.Add(noteText);
            statusGlyph.FontFamily = (FontFamily)FindResource("Font.Icons"); statusGlyph.FontSize = 12; statusGlyph.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Warn");
            statusText.Style = (Style)FindResource("Text.Caption"); statusText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text.Secondary");
            foreach (var b in new[] { retryButton, statusCopy, try3dButton }) { b.Style = (Style)FindResource("GhostButton"); b.Height = 28; b.Padding = new Thickness(8, 0, 8, 0); }
            retryButton.ToolTip = "Read the areas that could not be read again (useful once the Battle.net app has finished downloading or updating the game)";
            statusCopy.ToolTip = "Copy a short report of the world map (versions, graphics, what could not be read, recent log lines) to paste to whoever helps you";
            try3dButton.ToolTip = "Start the 3D view again";
            retryButton.Click += (s, e) => StartRetry(false);
            statusCopy.Click += (s, e) => CopyDetails();
            try3dButton.Click += (s, e) => { if (Settings != null) { Settings.WorldMap3dProblem = null; SaveSettings?.Invoke(); } GlFallbackReason = null; SetMode(true); UpdateStatus(); };
            statusHost.Children.Add(statusGlyph); statusHost.Children.Add(statusText); statusHost.Children.Add(retryButton); statusHost.Children.Add(statusCopy); statusHost.Children.Add(try3dButton);
            bar.Children.Add(statusHost);
            SetRow(bar, 0); Children.Add(bar);

            var card = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24) };
            overlayGlyph.Text = ""; overlayGlyph.FontFamily = new FontFamily("Segoe MDL2 Assets"); overlayGlyph.FontSize = 32; overlayGlyph.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text.Tertiary");
            overlayTitle.Style = (Style)FindResource("Text.Section"); overlayTitle.Margin = new Thickness(0, 8, 0, 0);
            overlayHint.Style = (Style)FindResource("Text.Secondary"); overlayHint.Margin = new Thickness(0, 4, 0, 0);
            overlayBar.Style = (Style)FindResource("ThinProgress");
            overlayButton.Style = (Style)FindResource("Button.M");
            overlayButton.Click += (s, e) => overlayAction?.Invoke();
            overlayCopy.Style = (Style)FindResource("Button.M"); overlayFlat.Style = (Style)FindResource("Button.M");
            overlayCopy.Click += (s, e) => CopyDetails();
            overlayFlat.Click += (s, e) => OpenFlatAtlasMap();
            overlayFlat.ToolTip = "Open the continent's flat map in the Atlas (drawn by the app itself, no web page)";
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 0) };
            buttons.Children.Add(overlayButton); buttons.Children.Add(overlayCopy); buttons.Children.Add(overlayFlat);
            card.Children.Add(overlayGlyph); card.Children.Add(overlayTitle); card.Children.Add(overlayHint); card.Children.Add(overlayBar); card.Children.Add(buttons);
            overlay.Child = card;
            stage.Children.Add(overlay);
            SetRow(stage, 1); Children.Add(stage);
            Loaded += (s, e) => Subscribe();
        }

        void Subscribe()
        {
            if (subscribed) return;
            subscribed = true;
            WorldMapService.BuildChanged += OnBuildChanged;
            bld.Attach();
            MapData.Changed += OnMapDataChanged;
        }

        // ---------------------------------------------------------------- navigation
        // the sub-path of "worldmap:" (see WorldMapIds); called each time the Atlas shows the page
        public void Navigate(string sub)
        {
            Subscribe();
            var idx = MapData.Index;
            var t = WorldMapIds.Parse(sub, idx == null ? (Func<string, int?>)null : WorldMapIds.SlugsOf(idx));
            if (!t.Valid)
            {
                SetNote("That world map link is not valid (" + t.Problem + "); showing the map instead.");
                t = new WorldMapTarget();
            }
            else SetNote("");
            if (!modeKnown) { modeKnown = true; mode3d = Settings != null && Settings.WorldMap3d; }
            if (t.Mode == 3) mode3d = true; else if (t.Mode == 2) mode3d = false;
            if (FlatOnly) mode3d = false;
            ShowModeSegments();
            target = t;
            Resolve();
        }

        void ShowModeSegments() { suppressMode = true; seg2d.IsChecked = !mode3d; seg3d.IsChecked = mode3d; suppressMode = false; modeHost.Visibility = FlatOnly ? Visibility.Collapsed : Visibility.Visible; }

        // Settings > World map view "Flat only": the 3D view is never started
        bool FlatOnly => Settings != null && Settings.WorldMapFlat;

        // the 2D | 3D switch: the view (x, y, zoom) is handed over
        public void SetMode(bool is3d)
        {
            modeKnown = true;
            if (is3d && FlatOnly) { is3d = false; SetNote("3D is off: Settings > General > World map view is \"Flat only\"."); }
            if (is3d == mode3d && loadedPage == (is3d ? "3d" : "2d")) { ShowModeSegments(); return; }
            mode3d = is3d;
            if (Settings != null && Settings.WorldMap3d != is3d) { Settings.WorldMap3d = is3d; SaveSettings?.Invoke(); }
            if (is3d) GlFallbackReason = null;
            ShowModeSegments();
            if (mapId < 0) return;
            SetNote("");
            target = new WorldMapTarget { MapId = mapId, HasView = hasView, X = vx, Y = vy, Z = vz };
            Resolve();
        }

        void OnMapDataChanged() { try { Dispatcher.BeginInvoke(new Action(() => { if (mapId < 0 || !PageInited) Resolve(); else RefreshSegments(); })); } catch (Exception) { } }

        void Resolve()
        {
            var idx = MapData.Index;
            if (idx == null)
            {
                switch (MapData.Status)
                {
                    case MapData.State.Disabled: Show("", "The world map is made from your game files", "Game data is switched off. Switch \"Read item data from my WoW installation\" on in Settings > General to use the world map.", false); break;
                    case MapData.State.Unavailable: Show("", "The world map needs your game's maps", "The map tables could not be read (" + (MapData.Reason ?? "unknown") + "). Check the WoW folder in Settings > General.", false); break;
                    default: Show("", "Reading the maps from your game files...", "The world map starts as soon as the map tables are read.", true); break;
                }
                return;
            }
            var conts = WorldMapService.Continents(idx);
            RefreshSegments(conts);
            if (conts.Count == 0) { Show("", "This game has no world map", "Its files list no continent with a world table and minimap pictures.", false); return; }
            WorldContinent c = target.MapId >= 0 ? conts.FirstOrDefault(x => x.MapId == target.MapId) : null;
            if (c == null && target.MapId >= 0) SetNote("The game has no world map for map " + target.MapId + "; showing " + conts[0].Name + ".");
            if (c == null && target.MapId < 0 && Settings != null && !string.IsNullOrEmpty(Settings.WorldMapLast)) c = conts.FirstOrDefault(x => x.Slug == Settings.WorldMapLast);
            if (c == null) c = conts.FirstOrDefault(x => x.MapId == mapId) ?? conts[0];
            SwitchTo(c, target);
        }

        void RefreshSegments(List<WorldContinent> conts = null)
        {
            var idx = MapData.Index; if (idx == null) return;
            conts = conts ?? WorldMapService.Continents(idx);
            if (segs.Count == conts.Count && segs.Select(s => (int)s.Tag).SequenceEqual(conts.Select(c => c.MapId))) return;
            suppressSeg = true;
            segPanel.Children.Clear(); segs.Clear();
            foreach (var c in conts)
            {
                var rb = new RadioButton { Style = (Style)FindResource("Segment"), Content = c.Name, GroupName = "wm-cont", Tag = c.MapId, IsChecked = c.MapId == mapId };
                System.Windows.Automation.AutomationProperties.SetName(rb, c.Name);
                rb.Checked += (s, e) => { if (!suppressSeg && s is RadioButton r && r.Tag is int id) { target = new WorldMapTarget { MapId = id }; hasView = false; Resolve(); } };
                segs.Add(rb); segPanel.Children.Add(rb);
            }
            suppressSeg = false;
            segHost.Visibility = conts.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        }

        void SwitchTo(WorldContinent c, WorldMapTarget t)
        {
            string want = mode3d ? "3d" : "2d";
            bool same = c.MapId == mapId && PageInited && loadedPage == want;
            mapId = c.MapId; slug = c.Slug;
            hasView = t.HasView; if (t.HasView) { vx = t.X; vy = t.Y; vz = t.Z; }
            if (Settings != null && Settings.WorldMapLast != c.Slug) { Settings.WorldMapLast = c.Slug; SaveSettings?.Invoke(); }
            suppressSeg = true; foreach (var s in segs) s.IsChecked = (int)s.Tag == mapId; suppressSeg = false;
            ShowModeSegments();
            int gen = ++buildGen;
            if (same && web != null && WorldMapCache.Built(mapId) != null && (!mode3d || WorldMapCache.TerrainBuilt(mapId) != null))
            {
                if (t.HasView) { WorldMapMath.WorldToLatLng(t.X, t.Y, out var lat, out var lng); Post(WorldMapProtocol.Goto(lat, lng, t.Z, t.Pin ? "" : null, null, ++viewSeq)); }
                else if (t.Slug.Length > 0) Post("{\"t\":\"fit\",\"s\":" + (++viewSeq).ToString(CultureInfo.InvariantCulture) + "}");      // the continent by name: the whole continent again
                Present();
                SubChanged?.Invoke(CurrentSub);
                UpdateStatus();
                MaybeAutoRetry();
                return;
            }
            EnsureAssets(c, t, gen);
        }

        // the pictures (always) and the terrain (for 3D) of the continent exist, then the page starts
        void EnsureAssets(WorldContinent c, WorldMapTarget t, int gen)
        {
            if (WorldMapCache.Built(c.MapId) == null)
            {
                ShowBuild(WorldMapService.StateOf(c.MapId), c.Name);
                WorldMapService.EnsureBuilt(c.MapId).ContinueWith(task =>
                {
                    try { Dispatcher.BeginInvoke(new Action(() => { if (gen != buildGen || mapId != c.MapId) return; if (task.Status == TaskStatus.RanToCompletion && task.Result) EnsureAssets(c, t, gen); else ShowBuildFailed(c, false); })); } catch (Exception) { }
                });
                return;
            }
            if (mode3d && WorldMapCache.TerrainBuilt(c.MapId) == null)
            {
                ShowBuild(WorldMapService.TerrainStateOf(c.MapId), c.Name, true);
                WorldMapService.EnsureTerrain(c.MapId).ContinueWith(task =>
                {
                    try
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (gen != buildGen || mapId != c.MapId) return;
                            if (task.Status == TaskStatus.RanToCompletion && task.Result) EnsureAssets(c, t, gen);
                            else
                            {
                                // no terrain: the flat map still works
                                var st = WorldMapService.TerrainStateOf(c.MapId);
                                GlFallbackReason = "the terrain could not be read" + (st?.Error != null ? " (" + st.Error + ")" : "");
                                lastProblem = "The 3D terrain could not be read: " + (st?.Error ?? "unknown reason");
                                Util.Log("world map: " + c.Name + ": 3D terrain could not be built, showing the flat map: " + (st?.Error ?? "unknown reason"));
                                mode3d = false; ShowModeSegments();
                                SetNote("The 3D view needs the terrain, which could not be read from your game files; showing the flat map.");
                                StartPage(c, t);
                            }
                        }));
                    }
                    catch (Exception) { }
                });
                return;
            }
            StartPage(c, t);
        }

        [DllImport("user32.dll")] static extern short GetKeyState(int vk);

        // The keyboard focus inside the web page belongs to the WebView2 window, so Ctrl+K never reaches the Atlas page. The WebView2 control reports every key first
        // (AcceleratorKeyPressed on its controller, which the WPF wrapper keeps private: found by reflection); Ctrl+K is taken from there. If the hook can not be made the shortcut simply does not work over the map.
        void HookKeys(WebView2 wv)
        {
            try
            {
                var inst = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                var baseObj = typeof(WebView2).GetField("m_webview2Base", inst)?.GetValue(wv);
                var ctl = baseObj?.GetType().GetField("<CoreWebView2Controller>k__BackingField", inst)?.GetValue(baseObj) as CoreWebView2Controller;
                if (ctl == null) return;
                ctl.AcceleratorKeyPressed += (s, e) =>
                {
                    try
                    {
                        if (e.KeyEventKind != CoreWebView2KeyEventKind.KeyDown && e.KeyEventKind != CoreWebView2KeyEventKind.SystemKeyDown) return;
                        if (e.VirtualKey == 0x4B && (GetKeyState(0x11) & 0x8000) != 0 && (GetKeyState(0x12) & 0x8000) == 0) { e.Handled = true; PaletteRequested?.Invoke(); }
                    }
                    catch (Exception) { }
                };
                KeyHookForTest = true;
            }
            catch (Exception ex) { Util.Log("world map: key hook: " + ex.Message); }
        }

        // ---------------------------------------------------------------- airspace: a WebView2 is a window of its own and is drawn above every WPF element, so a WPF layer (the Atlas palette)
        // can not cover it. While one is open the live view is swapped for a snapshot of it (CapturePreviewAsync into an Image, which WPF can cover) and swapped back on Thaw.
        // The snapshot is the last frame, nothing moves under the layer; if the capture fails the stage colour stands in and the layer still works.
        bool frozen, webWanted;
        Image freezeImg;

        void ApplyWebVisibility() { if (web != null) web.Visibility = webWanted && !frozen ? Visibility.Visible : Visibility.Hidden; }

        public async Task<bool> FreezeAsync()
        {
            if (frozen) return true;
            if (web == null || web.CoreWebView2 == null || web.Visibility != Visibility.Visible) return false;      // nothing in the way: no web view on screen
            BitmapImage bmp = null;
            try
            {
                using (var ms = new MemoryStream())
                {
                    var cap = web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
                    if (await Task.WhenAny(cap, Task.Delay(1500)) == cap)
                    {
                        await cap;
                        ms.Position = 0;
                        bmp = new BitmapImage();
                        bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.StreamSource = ms; bmp.EndInit(); bmp.Freeze();
                    }
                }
            }
            catch (Exception e) { Util.Log("world map snapshot: " + e.Message); bmp = null; }
            freezeImg = new Image { Source = bmp, Stretch = Stretch.Fill, IsHitTestVisible = false };
            stage.Children.Add(freezeImg);
            frozen = true;
            ApplyWebVisibility();
            return true;
        }

        public void Thaw()
        {
            if (!frozen) return;
            frozen = false;
            if (freezeImg != null) { stage.Children.Remove(freezeImg); freezeImg = null; }
            ApplyWebVisibility();
        }

        // ---------------------------------------------------------------- overlay (build progress, notes, no WebView2)
        void Show(string glyph, string title, string hint, bool busy)
        {
            overlayGlyph.Text = glyph; overlayTitle.Text = title; overlayHint.Text = hint;
            overlayHint.Visibility = hint.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            overlayBar.Visibility = Visibility.Collapsed; overlayButton.Visibility = Visibility.Collapsed;
            overlayCopy.Visibility = Visibility.Collapsed; overlayFlat.Visibility = Visibility.Collapsed;
            statusHost.Visibility = Visibility.Collapsed;
            webWanted = false; ApplyWebVisibility();
            overlay.Visibility = Visibility.Visible;
        }

        // why the map is being made: the first time, or again because the game was updated (the older version's map is of no use with the new files)
        public static string BuildHint(int mapId, bool terrain)
        {
            const string tail = " Nothing is uploaded and nothing is written to the WoW folder.";
            bool again = WorldMapCache.MadeForOlderGame(mapId, terrain);
            if (terrain)
                return (again ? "Your game was updated, so the Outpost reads the shape of the land again from the new game files, in the background. This happens once per game version."
                              : "The 3D view needs the shape of the land. The first time, the Outpost reads it from your game files, in the background. This happens once per game version.") + tail;
            return (again ? "Your game was updated, so the Outpost makes this continent's map again from the new game files, in the background. This happens once per game version; later you open it at once."
                          : "The first time you open a continent, the Outpost makes its map from your game files, in the background. This happens once per game version; later you open it at once.") + tail;
        }

        void ShowBuild(WorldBuildState st, string name, bool terrain = false)
        {
            Show("", st != null && st.Running ? st.Text : terrain ? "Building " + name + " terrain..." : "Building " + name + "...", BuildHint(mapId, terrain), true);
            overlayBar.Visibility = Visibility.Visible;
            overlayBar.Value = st != null && st.Total > 0 ? Math.Min(1, st.Done / (double)st.Total) : 0;
            overlayButton.Content = "Cancel"; overlayButton.Visibility = Visibility.Visible;
            int id = mapId; overlayAction = () => { WorldMapService.Cancel(id); };
            if (st != null && st.Running && st.Text.Length > 0) overlayTitle.Text = st.Text;
        }

        void ShowBuildFailed(WorldContinent c, bool terrain)
        {
            var st = terrain ? WorldMapService.TerrainStateOf(c.MapId) : WorldMapService.StateOf(c.MapId);
            ShowFailure("The " + c.Name + (terrain ? " terrain" : " map") + " could not be built", st?.Error ?? "Something went wrong while reading the game files.",
                "Building the " + c.Name + (terrain ? " terrain" : " map") + " failed: " + (st?.Error ?? "unknown reason"), () => { WorldMapService.ForgetFailures(c.MapId); Resolve(); });
        }

        // a failure: what failed in plain words, Try again, Copy details and the app's own flat map of the continent (no web page)
        void ShowFailure(string title, string hint, string problem, Action retry, bool flat = true)
        {
            lastProblem = problem;
            Show("", title, hint, false);            // the warning glyph (Segoe Fluent / MDL2), Tertiary like every state of the stack
            SetNote("");                                   // an older note ("1 area could be read now ...") does not belong to the failure
            overlayButton.Content = "Try again"; overlayButton.Visibility = retry != null ? Visibility.Visible : Visibility.Collapsed;
            overlayAction = retry;
            overlayCopy.Visibility = Visibility.Visible;
            overlayFlat.Visibility = flat && FlatMapId() > 0 ? Visibility.Visible : Visibility.Collapsed;
            statusHost.Visibility = Visibility.Collapsed;
        }
        public void ShowFailureForTest(string title, string hint, string problem) => ShowFailure(title, hint, problem, () => Resolve());

        // the continent's own map in the Atlas (a WPF picture of the game's world map art: works without WebView2 or graphics acceleration)
        int FlatMapId()
        {
            var idx = MapData.Index; if (idx == null) return 0;
            int id = mapId >= 0 ? mapId : target.MapId;
            var c = id >= 0 ? WorldMapService.Continent(idx, id) : WorldMapService.Continents(idx).FirstOrDefault();
            return c != null && idx.Get(c.UiMapId) != null ? c.UiMapId : 0;
        }
        public string FlatMapLinkForTest => FlatMapId() > 0 ? "zone:" + FlatMapId().ToString(CultureInfo.InvariantCulture) + "?tab=map" : null;
        void OpenFlatAtlasMap() { var id = FlatMapId(); if (id > 0) OpenAtlas?.Invoke("zone:" + id.ToString(CultureInfo.InvariantCulture) + "?tab=map"); }

        // ---------------------------------------------------------------- tiles that could not be read yet (WorldMapPending): the status line, Retry, the automatic retry
        public void UpdateStatus()
        {
            string text = ""; bool retry = false, try3d = false;
            if (mapId >= 0)
            {
                var pend = WorldMapService.PendingOf(mapId);
                if (!mode3d) pend = pend.Where(p => p.Part == "picture").ToList();
                int areas = pend.Select(p => p.Col * 64 + p.Row).Distinct().Count();
                bool rough = pend.Any(p => p.Shown == "plain" || p.Shown == "flat");
                if (WorldMapService.Retrying(mapId) && areas > 0) text = "Reading " + Areas(areas) + " again...";
                else if (areas > 0) { text = (rough ? Areas(areas) + " couldn't be read yet" : Areas(areas) + " shown in low detail") + " (the game may still be downloading)"; retry = true; }
            }
            if (text.Length == 0 && !mode3d && !FlatOnly && Settings != null && !string.IsNullOrEmpty(Settings.WorldMap3dProblem))
            { text = "3D is off after a problem on this PC (" + Settings.WorldMap3dProblem + ")"; try3d = true; }
            statusText.Text = text;
            retryButton.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
            try3dButton.Visibility = try3d ? Visibility.Visible : Visibility.Collapsed;
            statusHost.Visibility = text.Length > 0 && overlay.Visibility != Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
            statusHost.ToolTip = text.Length > 0 && retry ? "Parts of your game files were not on this PC yet when the map was made (a Battle.net install that is still downloading in the background, or a game that was updating). Those areas are shown with a stand-in and are read again by themselves the next time you open the map; Retry does it now." : null;
        }
        static string Areas(int n) => n == 1 ? "1 area" : n.ToString(CultureInfo.InvariantCulture) + " areas";

        void MaybeAutoRetry() { if (mapId >= 0 && WorldMapService.ShouldAutoRetry(mapId)) StartRetry(true); }

        // reads the stand-in tiles again in the background; when some read now the page is opened again with them (a new "rev", so nothing comes from the browser cache)
        void StartRetry(bool auto)
        {
            int my = mapId; if (my < 0) return;
            var c = WorldMapService.Continent(MapData.Index, my); if (c == null) return;
            Util.Log("world map: " + c.Name + ": trying the tiles that could not be read again (" + (auto ? "automatic" : "Retry") + ")");
            var task = WorldMapService.RetryPending(my);
            LastRetryForTest = task;
            UpdateStatus();
            task.ContinueWith(t =>
            {
                try
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (my != mapId) return;
                        var r = t.Status == TaskStatus.RanToCompletion ? t.Result : null;
                        if (r != null && r.Recovered > 0 && PageInited && web != null)
                        {
                            SetNote(Areas(r.Recovered) + " could be read now and " + (r.Recovered == 1 ? "is" : "are") + " shown properly.");
                            StartPage(c, new WorldMapTarget { MapId = mapId, HasView = hasView, X = vx, Y = vy, Z = vz });
                        }
                        else if (!auto && r != null && r.StillPending > 0) SetNote("Still not readable: let the Battle.net app finish downloading or updating the game, then press Retry (or just open the map again later).");
                        UpdateStatus();
                    }));
                }
                catch (Exception) { }
            });
        }
        public void RetryForTest() => StartRetry(false);
        public void RetryFailureForTest() => overlayAction?.Invoke();

        // ---------------------------------------------------------------- "Copy details"
        static string WebViewVersion()
        {
            if (WebViewVersionForTest != null) return WebViewVersionForTest();
            try { return CoreWebView2Environment.GetAvailableBrowserVersionString() ?? ""; } catch (Exception) { return ""; }
        }

        public string BuildReport()
        {
            var idx = MapData.Index;
            var c = mapId >= 0 && idx != null ? WorldMapService.Continent(idx, mapId) : null;
            var man = mapId >= 0 ? WorldMapCache.Built(mapId) : null; var tman = mapId >= 0 ? WorldMapCache.TerrainBuilt(mapId) : null;
            var info = new WorldMapReport.Info
            {
                Outpost = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "",
                Game = MapData.BuildStamp ?? "", Flavor = MapData.Flavor() ?? "", WebView = WebViewVersion(), Renderer = GlRenderer,
                Continent = c?.Name ?? "", MapId = mapId, Mode = mode3d ? "3D" : "2D", Problem = lastProblem ?? (GlFallbackReason != null ? "3D view not available: " + GlFallbackReason : ""),
                PictureTiles = man == null ? 0 : man.Adt + man.Missing, TerrainTiles = tman == null ? 0 : tman.Tiles + tman.Missing,
                Pending = mapId >= 0 ? WorldMapService.PendingOf(mapId) : new List<PendingTile>(),
                Page = (loadedPage ?? "none") + " page: started " + (pageHello ? "yes" : "no") + ", ready " + (PageReady ? "yes" : "no") + (TimeoutsForTest > 0 ? ", timed out " + TimeoutsForTest + "x" : "") + (GlOk ? ", WebGL ok" : ""),
                PageErrors = pageErrors.ToList(),
            };
            return WorldMapReport.Build(info);
        }

        void CopyDetails()
        {
            var text = BuildReport();
            LastReportForTest = text;
            bool ok = Copy(text);
            SetNote(ok ? "Details copied: paste them in Lodge chat (or wherever you ask for help)." : "Could not reach the clipboard; try again.");
        }
        public void CopyDetailsForTest() => CopyDetails();

        void OnBuildChanged(int id)
        {
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (id != mapId) return;
                    var st = WorldMapService.RunningStateOf(id);
                    if (st == null) { if (overlay.Visibility != Visibility.Visible) UpdateStatus(); return; }      // a retry of tiles that could not be read: the status line follows it
                    if (overlay.Visibility == Visibility.Visible)
                    {
                        overlayTitle.Text = st.Text;
                        overlayBar.Visibility = Visibility.Visible;
                        overlayBar.Value = st.Total > 0 ? Math.Min(1, st.Done / (double)st.Total) : 0;
                    }
                    else if (borderWaitMap == id && st.Terrain) SetNote("Zone borders: " + st.Text.Replace("Building " + st.Name + " terrain", "reading the terrain from your game files"));
                }));
            }
            catch (Exception) { }
        }

        void SetNote(string t) { noteText.Text = t ?? ""; }

        void Present()
        {
            if (web == null || !PageInited) return;
            overlay.Visibility = Visibility.Collapsed;
            overlayCopy.Visibility = Visibility.Collapsed; overlayFlat.Visibility = Visibility.Collapsed;
            webWanted = true; ApplyWebVisibility();
            UpdateStatus();
        }

        // the page has PageTimeoutMs to say "ready" after it was opened: a page that never does (a script that cannot run, a graphics process that hangs) is a
        // failure the person sees, never a black or empty area. The 3D page falls back to the flat map; the flat page shows the failure card.
        void ArmPageTimer(int gen, WorldContinent c)
        {
            pageTimer?.Stop();
            pageTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(200, PageTimeoutMs)) };
            pageTimer.Tick += (s, e) =>
            {
                pageTimer?.Stop();
                if (gen != initGen || PageReady || mapId != c.MapId) return;
                TimeoutsForTest++;
                var what = !pageHello ? "the page did not start" : "the page started but did not finish drawing";
                Util.Log("world map: " + c.Name + ": " + (loadedPage ?? "?") + " page not ready after " + (PageTimeoutMs / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + " s (" + what + "); WebView2 " + WebViewVersion() + (GlRenderer.Length > 0 ? ", WebGL " + GlRenderer : "") + (pageErrors.Count > 0 ? ", page errors: " + string.Join(" | ", pageErrors.Take(3)) : ""));
                if (mode3d) Fallback3d("the 3D page did not finish loading (" + what + ")", GlRenderer);
                else ShowFailure("The world map page did not open", "The map is made, but the page that shows it did not " + (!pageHello ? "start" : "finish drawing") + ". Try again; if it keeps happening, \"Copy details\" and send them to whoever helps you. The flat map of the Atlas works without it.",
                    "The flat world map page did not become ready within " + (PageTimeoutMs / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + " s (" + what + ")", () => { RecreateWeb(); Resolve(); });
            };
            pageTimer.Start();
        }

        // 3D could not be used here: the flat map, a note, and (Automatic) it is remembered with a way back ("Try 3D again" in the status line)
        void Fallback3d(string why, string renderer)
        {
            GlFallbackReason = why;
            lastProblem = "3D view not available: " + why;
            Util.Log("world map: 3D view not available (" + why + "); renderer " + (string.IsNullOrEmpty(renderer) ? "unknown" : renderer) + ", WebView2 " + WebViewVersion() + "; showing the flat map");
            mode3d = false;
            if (Settings != null) { Settings.WorldMap3d = false; Settings.WorldMap3dProblem = why; SaveSettings?.Invoke(); }
            ShowModeSegments();
            target = new WorldMapTarget { MapId = mapId, HasView = hasView, X = vx, Y = vy, Z = vz };
            Dispatcher.BeginInvoke(new Action(() => { if (!mode3d) { Resolve(); SetNote("The 3D view can't draw on this PC (" + why + "); showing the flat map."); UpdateStatus(); } }));
        }

        // the web view is made again (after its browser part stopped, or a page that never started)
        void RecreateWeb()
        {
            try { pageTimer?.Stop(); Thaw(); if (web != null) { stage.Children.Remove(web); web.Dispose(); } } catch (Exception) { }
            web = null; webTask = null; loadedPage = null; pageHello = false; PageInited = false; PageReady = false; pendingInit = null;
        }

        // ---------------------------------------------------------------- the page
        async void StartPage(WorldContinent c, WorldMapTarget t)
        {
            int gen = ++initGen;
            Show("", "Opening " + c.Name + "...", "", true);
            lastProblem = null; pageErrors.Clear();
            bool ok;
            try { ok = await EnsureWeb(); }
            catch (Exception e) { Util.Log("world map: " + e); ok = false; }
            if (gen != initGen) return;
            if (!ok) { ShowNoWebView(); return; }
            var man = WorldMapCache.Built(c.MapId);
            if (man == null) { ShowBuildFailed(c, false); return; }
            bool is3 = mode3d;
            var tman = is3 ? WorldMapCache.TerrainBuilt(c.MapId) : null;
            if (is3 && tman == null) { is3 = mode3d = false; ShowModeSegments(); }
            var init = new Dictionary<string, object>
            {
                ["t"] = "init",
                ["s"] = ++viewSeq,
                ["mode"] = is3 ? "3d" : "2d",
                ["map"] = new Dictionary<string, object>
                {
                    ["id"] = c.MapId, ["name"] = c.Name, ["base"] = "https://" + WorldMapPage.Host + "/" + WorldMapCache.BuildKey + "/" + c.MapId.ToString(CultureInfo.InvariantCulture),
                    ["minCol"] = man.MinCol, ["maxCol"] = man.MaxCol, ["minRow"] = man.MinRow, ["maxRow"] = man.MaxRow, ["sea"] = man.Sea,
                    ["rev"] = WorldMapService.Revision,
                },
                ["layers"] = WorldMapLayers.Catalog(),
                ["on"] = OnLayers(),
                ["follow"] = Settings != null && Settings.WorldMapFollow,
            };
            if (is3)
            {
                var tiles = new List<int>();
                try
                {
                    foreach (var f in Directory.GetFiles(WorldMapCache.TerrainDir(WorldMapCache.BuildKey, c.MapId), "*.wmt"))
                    {
                        var p = Path.GetFileNameWithoutExtension(f).Split('_');
                        if (p.Length == 2 && int.TryParse(p[0], out var col) && int.TryParse(p[1], out var row) && WorldMapMath.InGrid(col, row)) { tiles.Add(col); tiles.Add(row); }
                    }
                }
                catch (Exception) { }
                init["terrain"] = new Dictionary<string, object>
                {
                    ["base"] = "https://" + WorldMapPage.Host + "/" + WorldMapCache.BuildKey + "/" + c.MapId.ToString(CultureInfo.InvariantCulture) + "/" + WorldMapTerrain.Folder,
                    ["minZ"] = Math.Round(tman.MinZ, 1), ["maxZ"] = Math.Round(tman.MaxZ, 1), ["tiles"] = tiles,
                    ["holes"] = tman.Pending.Where(p => p.Shown == "flat").SelectMany(p => new[] { p.Col, p.Row }).ToList(),      // no heights at all yet: a flat hatched placeholder
                };
                init["buildings"] = bld.InitInfo(c.MapId, "https://" + WorldMapPage.Host + "/" + WorldMapCache.BuildKey + "/" + c.MapId.ToString(CultureInfo.InvariantCulture));
                init["prefs"] = new Dictionary<string, object> { ["detail"] = Detail(), ["exag"] = Exag(), ["userDetail"] = Settings != null && !string.IsNullOrEmpty(Settings.WorldMap3dDetail) };
                if (ForceNoGlForTest) init["forceNoGl"] = true;
            }
            if (t.HasView)
            {
                WorldMapMath.WorldToLatLng(t.X, t.Y, out var lat, out var lng);
                var v = new Dictionary<string, object> { ["t"] = "goto", ["lat"] = lat, ["lng"] = lng, ["z"] = t.Z };
                if (t.Pin) v["pin"] = new Dictionary<string, object> { ["name"] = "", ["tip"] = "" };
                init["view"] = v;
            }
            var json = Json.Write(init);
            // the right document in the web view: navigating to the other page starts that page's own hello
            string wantPage = is3 ? "3d" : "2d";
            PageReady = false; PageInited = false; GlOk = false;
            if (loadedPage != wantPage)
            {
                pageHello = false; loadedPage = wantPage; pendingInit = json;
                try { web.CoreWebView2.Navigate(is3 ? WorldMapPage.Page3dUrl : WorldMapPage.PageUrl); } catch (Exception e) { Util.Log("world map navigate: " + e.Message); }
            }
            else if (pageHello) Post(json); else pendingInit = json;
            ArmPageTimer(gen, c);
            MaybeAutoRetry();
        }

        public string Detail() { var d = Settings?.WorldMap3dDetail; return d == "low" || d == "high" ? d : "medium"; }
        public double Exag() { var e = Settings?.WorldMapExag ?? 0; return e >= 0.5 && e <= 1.5 ? e : 1; }   // 1 = true height, in the middle of 0.5 - 1.5

        Task<bool> EnsureWeb()
        {
            if (webTask != null && !webTask.IsFaulted) return webTask;
            return webTask = CreateWeb();
        }

        async Task<bool> CreateWeb()
        {
            string err = null;
            if (ForceNoWebViewForTest) return false;
            bool avail = await Task.Run(() => WebEngine.RuntimeAvailable(out err));
            if (!avail) { Util.Log("world map: WebView2 not available: " + err); return false; }
            var wv = new WebView2 { Visibility = Visibility.Hidden, DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x08, 0x11, 0x10) };
            stage.Children.Insert(0, wv);
            web = wv;
            try
            {
                await WebEngine.Attach(wv, WebProfile.WorldMap, true);
                var dir = await Task.Run(() => WorldMapPage.Ensure());
                var c = wv.CoreWebView2;
                Harden(c);
                HookKeys(wv);
                Directory.CreateDirectory(WorldMapCache.Root);
                c.SetVirtualHostNameToFolderMapping(WorldMapPage.Host, WorldMapCache.Root, CoreWebView2HostResourceAccessKind.Deny);
                c.WebMessageReceived += OnMessage;
                c.ProcessFailed += OnProcessFailed;
                loadedPage = mode3d ? "3d" : "2d";
                c.Navigate(mode3d ? WorldMapPage.Page3dUrl : WorldMapPage.PageUrl);
                return true;
            }
            catch (Exception e)
            {
                Util.Log("world map web: the page could not be started (WebView2 " + WebViewVersion() + "): " + WorldMapPending.Short(e));
                lastProblem = "The web view of the world map could not be started: " + WorldMapPending.Short(e);
                try { stage.Children.Remove(wv); wv.Dispose(); } catch (Exception) { }
                web = null; webTask = null; loadedPage = null;
                return false;
            }
        }

        // a browser process of the page stopped (a crash, the graphics process, out of memory): logged, and never left as a frozen or black map
        void OnProcessFailed(object sender, CoreWebView2ProcessFailedEventArgs e)
        {
            string kind = "", reason = "";
            try { kind = e.ProcessFailedKind.ToString(); reason = e.Reason.ToString(); } catch (Exception) { }
            try { Dispatcher.BeginInvoke(new Action(() => HandleProcessFailed(kind, reason))); } catch (Exception) { }
        }

        public void HandleProcessFailed(string kind, string reason)
        {
            Util.Log("world map: a browser process of the page stopped: " + kind + " (" + reason + "); " + (loadedPage ?? "?") + " page, WebView2 " + WebViewVersion() + (GlRenderer.Length > 0 ? ", WebGL " + GlRenderer : ""));
            if (mapId < 0) return;
            bool gpu = kind.IndexOf("Gpu", StringComparison.OrdinalIgnoreCase) >= 0;
            if (gpu && mode3d) { RecreateWeb(); Fallback3d("the graphics process stopped", GlRenderer); return; }
            if (kind.IndexOf("FrameRender", StringComparison.OrdinalIgnoreCase) >= 0 || kind.IndexOf("Utility", StringComparison.OrdinalIgnoreCase) >= 0 || gpu) return;      // the page itself lives on
            RecreateWeb();
            ShowFailure("The world map page stopped", "A part of the browser that draws the map closed (" + kind + "). Try again; if it keeps happening, \"Copy details\" and send them to whoever helps you.",
                "A browser process of the world map page stopped: " + kind + " (" + reason + ")", () => Resolve());
        }

        // the sandbox: see the class comment. Allowed: the page's own virtual host. Everything else is refused.
        public static bool IsOwnUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
            if (u.Scheme == Uri.UriSchemeHttps && string.Equals(u.Host, WorldMapPage.Host, StringComparison.OrdinalIgnoreCase) && u.Port == 443 && string.IsNullOrEmpty(u.UserInfo)) return true;
            return false;
        }

        void Harden(CoreWebView2 c)
        {
            WebEngine.Harden(c, true);                               // dev tools, host objects, autofill, permissions, downloads, tracking prevention, external schemes
            c.Settings.AreDefaultContextMenusEnabled = false;        // the page has its own menu
            c.Settings.IsWebMessageEnabled = true;                   // the one channel: validated message by message
            c.Settings.IsStatusBarEnabled = false;
            c.Settings.IsZoomControlEnabled = false;
            c.Settings.AreDevToolsEnabled = false;
            c.Settings.AreHostObjectsAllowed = false;
            c.NavigationStarting += (o, e) => { if (!IsOwnUrl(e.Uri) && !string.Equals(e.Uri, "about:blank", StringComparison.OrdinalIgnoreCase)) e.Cancel = true; };
            c.FrameNavigationStarting += (o, e) => { if (!IsOwnUrl(e.Uri)) e.Cancel = true; };
            c.NewWindowRequested += (o, e) => { e.Handled = true; };
            c.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            c.WebResourceRequested += (o, e) =>
            {
                var u = e.Request.Uri ?? "";
                if (u.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || u.StartsWith("blob:", StringComparison.OrdinalIgnoreCase) || IsOwnUrl(u)) return;
                BlockedForTest++; if (BlockedUrlsForTest.Count < 20) BlockedUrlsForTest.Add(u);
                e.Response = c.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
            };
        }
        public int BlockedForTest;
        public readonly List<string> BlockedUrlsForTest = new List<string>();
        public static bool ForceNoWebViewForTest;                 // self-test: pretend WebView2 is not installed

        void ShowNoWebView()
        {
            NoWebViewShown = true;
            bool missing = ForceNoWebViewForTest || string.IsNullOrEmpty(WebViewVersion());
            Util.Log("world map: no web view: " + (missing ? "the WebView2 runtime is not installed" : "WebView2 " + WebViewVersion() + " could not be started") + (lastProblem != null ? " (" + lastProblem + ")" : ""));
            ShowFailure(missing ? "The world map needs Microsoft Edge WebView2" : "The world map page could not be started",
                missing ? "WebView2 is not installed on this PC, so this map cannot be shown here. \"Use the flat map\" opens the continent's map in the Atlas instead; zones, creatures, objects and routes all still have their own maps."
                        : "Microsoft Edge WebView2 is there but did not start. Try again, or use the Atlas' flat map of the continent. \"Copy details\" makes a short report for whoever helps you.",
                lastProblem ?? (missing ? "The WebView2 runtime is not installed" : "WebView2 could not be started"), () => { webTask = null; NoWebViewShown = false; Resolve(); });
        }

        // ---------------------------------------------------------------- messages
        void Post(string json)
        {
            try { web?.CoreWebView2?.PostWebMessageAsJson(json); } catch (Exception e) { Util.Log("world map post: " + e.Message); }
        }
        public void PostForTest(string json) => Post(json);

        void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string src = null, json = null;
            try { src = e.Source; json = e.WebMessageAsJson; } catch (Exception) { }
            if (!IsOwnUrl(src)) { RefusedForTest.Add("source"); return; }
            Handle(json);
        }

        // zoom <-> viewing distance of the 3D camera: a 2D view at zoom z shows 800 * 133.33 / 2^z yards (the same rule is in view3d.js)
        public static double DistToZoom(double dist) => Math.Max(0, Math.Min(WorldMapIds.MaxZoom, Math.Log(106666.0 / Math.Max(dist, 1)) / Math.Log(2)));

        // (public for the self-test: the same path as a message from the page)
        public void Handle(string json)
        {
            var m = WorldMapProtocol.Parse(json, out var refusal);
            if (m == null) { RefusedForTest.Add(refusal ?? "refused"); Util.Log("world map: refused a message from the page: " + refusal); return; }
            ReceivedForTest.Add(m.Type.ToString().ToLowerInvariant());
            switch (m.Type)
            {
                case WorldMapMessageType.Hello:
                    // a hello from the document that was replaced is not for the new one
                    if (!string.IsNullOrEmpty(m.Detail) && loadedPage != null && m.Detail != loadedPage) break;
                    pageHello = true;
                    if (pendingInit != null) { var j = pendingInit; pendingInit = null; Post(j); }
                    break;
                case WorldMapMessageType.Inited:
                    PageInited = true;
                    if (mode3d && mapId >= 0) bld.OnInited(mapId);
                    lastMe = null; PushLive(true);
                    break;
                case WorldMapMessageType.HowTo:
                    HowToOpenedForTest++;
                    HelpPages.Open(HelpPages.LivePosition, Window.GetWindow(this));
                    break;
                case WorldMapMessageType.Follow:
                    if (Settings != null && Settings.WorldMapFollow != m.Ok) { Settings.WorldMapFollow = m.Ok; SaveSettings?.Invoke(); }
                    break;
                case WorldMapMessageType.Ready:
                    if ((Ignore2dReadyForTest && loadedPage == "2d") || (Ignore3dReadyForTest && loadedPage == "3d")) break;
                    PageReady = true; PageInited = true;
                    pageTimer?.Stop();
                    if (mode3d && GlOk && Settings != null && Settings.WorldMap3dProblem != null) { Settings.WorldMap3dProblem = null; SaveSettings?.Invoke(); }      // 3D works again
                    Present();
                    SubChanged?.Invoke(CurrentSub);
                    break;
                case WorldMapMessageType.Error:
                    if (pageErrors.Count < 10) pageErrors.Add(m.Detail);
                    Util.Log("world map page error (" + (loadedPage ?? "?") + "): " + m.Detail);
                    break;
                case WorldMapMessageType.View:
                    {
                        // a report the page sent before it had the app's last goto/fit/init (in flight, or its debounce timer) is not where the map is going: kept out of the id
                        if (m.Seq >= 0 && m.Seq < viewSeq) { StaleViewsForTest++; break; }
                        WorldMapMath.LatLngToWorld(m.Lat, m.Lng, out var x, out var y);
                        hasView = true; vx = x; vy = y; vz = m.Z;
                        SubChanged?.Invoke(CurrentSub);
                        break;
                    }
                case WorldMapMessageType.View3:
                    {
                        if (m.Seq >= 0 && m.Seq < viewSeq) { StaleViewsForTest++; break; }
                        LastView3 = m;
                        bld.Focus(m.Lat, m.Lng, mapId);
                        WorldMapMath.LatLngToWorld(m.Lat, m.Lng, out var x, out var y);
                        hasView = true; vx = x; vy = y; vz = Math.Round(DistToZoom(m.Dist), 2);
                        SubChanged?.Invoke(CurrentSub);
                        break;
                    }
                case WorldMapMessageType.Open:
                    OpenAtlas?.Invoke(m.Id);
                    break;
                case WorldMapMessageType.Nav:
                    NavigateHere(m.Lat, m.Lng);
                    break;
                case WorldMapMessageType.Layers:
                    if (Settings != null) { Settings.WorldMapLayers = m.On.ToList(); Settings.WorldMapLayersVersion = WorldMapLayers.LayersVersion; SaveSettings?.Invoke(); }
                    break;
                case WorldMapMessageType.Need:
                    NeedLayer(m.Layer);
                    break;
                case WorldMapMessageType.Webgl:
                    GlOk = m.Ok; GlRenderer = m.Detail;
                    if (m.Ok) Util.Log("world map: 3D page has WebGL: " + (m.Detail.Length > 0 ? m.Detail : "renderer unknown") + (m.Why.Length > 0 ? " (" + m.Why + ")" : "") + ", WebView2 " + WebViewVersion());
                    if (!m.Ok && mode3d && mapId >= 0)
                    {
                        // no WebGL (none, blocked, or the context was lost): the flat map, with a note; remembered (Automatic) with a way back
                        pageTimer?.Stop();
                        Fallback3d(string.IsNullOrEmpty(m.Why) ? "graphics/WebGL unavailable" : "graphics/WebGL unavailable: " + m.Why, m.Detail);
                    }
                    break;
                case WorldMapMessageType.Stats:
                    LastStats = m;
                    break;
                case WorldMapMessageType.Buildings: case WorldMapMessageType.BBuild: case WorldMapMessageType.BPause: case WorldMapMessageType.BResume: case WorldMapMessageType.BCancel:
                    if (mode3d) bld.Handle(m, mapId);
                    break;
                case WorldMapMessageType.Prefs:
                    if (Settings != null) { Settings.WorldMap3dDetail = m.Detail; Settings.WorldMapExag = Math.Round(m.Exag, 1); SaveSettings?.Invoke(); }
                    break;
            }
        }

        // ---------------------------------------------------------------- the live position layer ("Show my live position on the world map")
        // LivePosition says the state and keeps the trail (memory only); this pushes it to the page as a "me" message, at most once a second.
        LivePosition live;
        DispatcherTimer liveTimer;
        DateTime lastLivePost = DateTime.MinValue;
        string lastMe;
        public string LastMeForTest => lastMe;
        public int HowToOpenedForTest { get; private set; }
        public int LivePostsForTest { get; private set; }
        public static TimeSpan LiveInterval = TimeSpan.FromSeconds(1);
        public LivePosition Live
        {
            get => live;
            set { if (live != null) live.Changed -= OnLiveChanged; live = value; if (live != null) live.Changed += OnLiveChanged; PushLive(true); }
        }
        void OnLiveChanged() { try { Dispatcher.BeginInvoke(new Action(() => PushLive(false))); } catch (Exception) { } }
        bool LiveOn => Settings != null && Settings.WorldMapLivePos;

        // the "me" message for the map shown now
        public string LiveJson()
        {
            var now = DateTime.UtcNow;
            if (live == null || !LiveOn) return WorldMapProtocol.Me(LiveState.Off, null, null, null, null, mapId, now);
            var here = live.Here;
            string elsewhere = null;
            if (live.State == LiveState.Ok && here.HasValue && here.Value.MapId != mapId)
            {
                var idx = MapData.Index;
                try { elsewhere = idx == null ? null : WorldMapService.Continent(idx, here.Value.MapId)?.Name; } catch (Exception) { }
            }
            return WorldMapProtocol.Me(live.State, live.Name, LivePosition.ColorOf(live.ClassFile), here, live.Trail.Points(now), mapId, now, elsewhere, live.Trail.Marks(now), TrailPrints.Style(live.Prints));
        }

        // force: send now (a new page, the setting changed); else at most once per LiveInterval, the newest state wins
        public void PushLive(bool force)
        {
            if (!PageInited || web == null && !ForceLiveForTest) return;
            var wait = LiveInterval - (DateTime.UtcNow - lastLivePost);
            if (!force && wait > TimeSpan.Zero)
            {
                if (liveTimer == null) { liveTimer = new DispatcherTimer(); liveTimer.Tick += (s, e) => { liveTimer.Stop(); PushLive(false); }; }
                if (!liveTimer.IsEnabled) { liveTimer.Interval = wait; liveTimer.Start(); }
                return;
            }
            var json = LiveJson();
            if (json == lastMe) return;
            lastMe = json; lastLivePost = DateTime.UtcNow; LivePostsForTest++;
            Post(json);
        }
        public static bool ForceLiveForTest;

        // the layers that are on: the person's saved list (brought up to this catalog once) or the defaults
        List<string> OnLayers()
        {
            if (Settings != null && Settings.WorldMapLayers != null && Settings.WorldMapLayersVersion < WorldMapLayers.LayersVersion)
            {
                Settings.WorldMapLayers = WorldMapLayers.WithNewDefaults(Settings.WorldMapLayers, Settings.WorldMapLayersVersion);
                Settings.WorldMapLayersVersion = WorldMapLayers.LayersVersion;
                SaveSettings?.Invoke();
            }
            return (Settings?.WorldMapLayers ?? WorldMapLayers.DefaultOn.ToList()).Where(WorldMapIds.IsLayerId).Distinct().ToList();
        }

        void PostLayerInfo(string layer, string state, string note) => Post(Json.Write(new Dictionary<string, object> { ["t"] = "layerinfo", ["id"] = layer, ["state"] = state, ["count"] = 0, ["note"] = note ?? "" }));

        // 2.88: a layer whose data changed while the page shows it (learned flight paths: a new learned route, the Lodge's, cleared) is built and sent again;
        // only one the page asked for in this view (an unrequested layer stays unbuilt until it is switched on)
        readonly Dictionary<string, int> askedLayers = new Dictionary<string, int>();
        public int RebuildsForTest { get; private set; }
        public void LayerDataChanged(string layer)
        {
            if (!PageInited || mapId < 0 || !askedLayers.TryGetValue(layer, out var g) || g != initGen) return;
            RebuildsForTest++;
            BuildAndPost(layer, mapId, initGen);
        }

        void NeedLayer(string layer)
        {
            int my = mapId, g = initGen;
            askedLayers[layer] = g;
            // zone borders come from the terrain: read it first (once per game version), the layer arrives when it is done
            if (layer == "borders" && !WorldMapService.TerrainBuilt(my))
            {
                borderWaitMap = my;
                PostLayerInfo(layer, "loading", "Reading the terrain from your game files...");
                WorldMapService.EnsureTerrain(my).ContinueWith(t =>
                {
                    try
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (borderWaitMap == my) { borderWaitMap = -1; SetNote(""); }
                            if (my != mapId || g != initGen) return;
                            if (t.Status == TaskStatus.RanToCompletion && t.Result) BuildAndPost(layer, my, g);
                            else PostLayerInfo(layer, "empty", WorldMapService.TerrainStateOf(my)?.Error ?? "The terrain could not be read.");
                        }));
                    }
                    catch (Exception) { }
                });
                return;
            }
            BuildAndPost(layer, my, g);
        }

        void BuildAndPost(string layer, int my, int g)
        {
            Task.Run(() => WorldMapLayers.Build(layer, my)).ContinueWith(t =>
            {
                try
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (my != mapId || g != initGen) return;
                        var r = t.Status == TaskStatus.RanToCompletion ? t.Result : new LayerResult { Note = "This layer could not be built." };
                        if (r.Json.Length == 0) PostLayerInfo(layer, "empty", r.Note);
                        else Post(r.Json);
                    }));
                }
                catch (Exception) { }
            });
        }

        // "Navigate here": the spot -> /etk way <uiMapId> <x> <y> <title> on the clipboard
        void NavigateHere(double lat, double lng)
        {
            WorldMapMath.LatLngToWorld(lat, lng, out var x, out var y);
            var cmd = WorldMapCommands.Way(MapData.Index, mapId, x, y, out var problem);
            if (cmd == null) { LastToastForTest = problem; Post(WorldMapProtocol.Toast(problem, false)); return; }
            bool ok = Copy(cmd);
            LastCommandForTest = ok ? cmd : null;
            var msg = ok ? "Copied - paste it in WoW chat" : "Could not reach the clipboard";
            LastToastForTest = msg;
            Post(WorldMapProtocol.Toast(msg, ok));
        }

        bool Copy(string text)
        {
            try { if (ClipboardSeamForTest != null) ClipboardSeamForTest(text); else Clipboard.SetText(text); return true; }
            catch (Exception)
            {
                try { System.Threading.Thread.Sleep(80); if (ClipboardSeamForTest != null) ClipboardSeamForTest(text); else Clipboard.SetText(text); return true; }
                catch (Exception) { return false; }
            }
        }

        // ---------------------------------------------------------------- test helpers
        public async Task<string> EvalForTest(string js)
        {
            if (web?.CoreWebView2 == null) return null;
            var r = await web.CoreWebView2.ExecuteScriptAsync(js);
            return r;
        }

        public async Task<byte[]> CaptureForTest()
        {
            if (web?.CoreWebView2 == null) return null;
            using (var ms = new MemoryStream())
            {
                await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
                return ms.ToArray();
            }
        }

        // the memory of the browser processes of this web view (private bytes, MB): the renderer and GPU process of the 3D page are what counts
        public long ProcessMemoryMbForTest(out string detail)
        {
            detail = "";
            try
            {
                var env = web?.CoreWebView2?.Environment; if (env == null) return -1;
                long total = 0; var parts = new List<string>();
                foreach (var pi in env.GetProcessInfos())
                {
                    try
                    {
                        using (var p = System.Diagnostics.Process.GetProcessById(pi.ProcessId))
                        {
                            p.Refresh();
                            long mb = p.PrivateMemorySize64 / 1048576;
                            total += mb; parts.Add(pi.Kind + " " + mb);
                        }
                    }
                    catch (Exception) { }
                }
                detail = string.Join(", ", parts);
                return total;
            }
            catch (Exception e) { detail = e.Message; return -1; }
        }

        public CoreWebView2 CoreForTest => web?.CoreWebView2;
        public bool HasWebForTest => web != null;

        // The Atlas leaves the page (another page is shown): nothing to stop, the page keeps what it had
        public void Dispose()
        {
            try { pageTimer?.Stop(); } catch (Exception) { }
            try { bld.Detach(); WorldMapService.BuildChanged -= OnBuildChanged; MapData.Changed -= OnMapDataChanged; subscribed = false; } catch (Exception) { }
            try { Thaw(); if (web != null) { stage.Children.Remove(web); web.Dispose(); web = null; webTask = null; } } catch (Exception) { }
            pageHello = false; PageInited = false; PageReady = false; pendingInit = null; mapId = -1; initGen++; buildGen++; loadedPage = null; GlOk = false; borderWaitMap = -1;
        }
    }
}
