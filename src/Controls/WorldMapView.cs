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
        public bool KeyHookForTest { get; private set; }
        public event Action PaletteRequested;                  // Ctrl+K was pressed while the web page had the keyboard (the page is another window: WPF never sees the key)
        public bool FrozenImageShownForTest => freezeImg != null && freezeImg.Source != null;
        public BitmapSource FrozenSnapshotForTest => freezeImg?.Source as BitmapSource;
        public static bool ForceNoGlForTest;                   // self-test: pretend WebGL is missing (the page is told to behave as if it were)

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
        readonly Button overlayButton = new Button { Margin = new Thickness(0, 16, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, Visibility = Visibility.Collapsed, MinWidth = 120 };
        WebView2 web;
        Task<bool> webTask;

        // ---- state
        int mapId = -1; string slug = "";
        bool hasView; double vx, vy, vz;
        WorldMapTarget target = new WorldMapTarget();
        int initGen, buildGen;
        string pendingInit;
        bool pageHello, suppressSeg, suppressMode;
        bool subscribed;
        bool mode3d, modeKnown;
        string loadedPage;                                     // "2d" | "3d": the document in the web view
        int borderWaitMap = -1;                                // a "Zone borders" request is waiting for the terrain of this map
        Action overlayAction;

        public WorldMapView()
        {
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
            SetRow(bar, 0); Children.Add(bar);

            var card = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24) };
            overlayGlyph.Text = ""; overlayGlyph.FontFamily = new FontFamily("Segoe MDL2 Assets"); overlayGlyph.FontSize = 32; overlayGlyph.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text.Tertiary");
            overlayTitle.Style = (Style)FindResource("Text.Section"); overlayTitle.Margin = new Thickness(0, 8, 0, 0);
            overlayHint.Style = (Style)FindResource("Text.Secondary"); overlayHint.Margin = new Thickness(0, 4, 0, 0);
            overlayBar.Style = (Style)FindResource("ThinProgress");
            overlayButton.Style = (Style)FindResource("Button.M");
            overlayButton.Click += (s, e) => overlayAction?.Invoke();
            card.Children.Add(overlayGlyph); card.Children.Add(overlayTitle); card.Children.Add(overlayHint); card.Children.Add(overlayBar); card.Children.Add(overlayButton);
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
            ShowModeSegments();
            target = t;
            Resolve();
        }

        void ShowModeSegments() { suppressMode = true; seg2d.IsChecked = !mode3d; seg3d.IsChecked = mode3d; suppressMode = false; }

        // the 2D | 3D switch: the view (x, y, zoom) is handed over
        public void SetMode(bool is3d)
        {
            modeKnown = true;
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
                if (t.HasView) { WorldMapMath.WorldToLatLng(t.X, t.Y, out var lat, out var lng); Post(WorldMapProtocol.Goto(lat, lng, t.Z, t.Pin ? "" : null)); }
                else if (t.Slug.Length > 0) Post("{\"t\":\"fit\"}");      // the continent by name: the whole continent again
                Present();
                SubChanged?.Invoke(CurrentSub);
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
            webWanted = false; ApplyWebVisibility();
            overlay.Visibility = Visibility.Visible;
        }

        void ShowBuild(WorldBuildState st, string name, bool terrain = false)
        {
            Show("", st != null && st.Running ? st.Text : terrain ? "Building " + name + " terrain..." : "Building " + name + "...",
                terrain ? "The 3D view needs the shape of the land. The first time, the Outpost reads it from your game files, in the background. This happens once per game version. Nothing is uploaded and nothing is written to the WoW folder."
                        : "The first time you open a continent, the Outpost makes its map from your game files, in the background. This happens once per game version; later you open it at once. Nothing is uploaded and nothing is written to the WoW folder.", true);
            overlayBar.Visibility = Visibility.Visible;
            overlayBar.Value = st != null && st.Total > 0 ? Math.Min(1, st.Done / (double)st.Total) : 0;
            overlayButton.Content = "Cancel"; overlayButton.Visibility = Visibility.Visible;
            int id = mapId; overlayAction = () => { WorldMapService.Cancel(id); };
            if (st != null && st.Running && st.Text.Length > 0) overlayTitle.Text = st.Text;
        }

        void ShowBuildFailed(WorldContinent c, bool terrain)
        {
            var st = terrain ? WorldMapService.TerrainStateOf(c.MapId) : WorldMapService.StateOf(c.MapId);
            Show("", "The " + c.Name + (terrain ? " terrain" : " map") + " could not be built", st?.Error ?? "Something went wrong while reading the game files.", false);
            overlayButton.Content = "Try again"; overlayButton.Visibility = Visibility.Visible;
            overlayAction = () => { WorldMapService.ResetForTest(); Resolve(); };
        }

        void OnBuildChanged(int id)
        {
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (id != mapId) return;
                    var st = WorldMapService.RunningStateOf(id);
                    if (st == null) return;
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
            webWanted = true; ApplyWebVisibility();
        }

        // ---------------------------------------------------------------- the page
        async void StartPage(WorldContinent c, WorldMapTarget t)
        {
            int gen = ++initGen;
            Show("", "Opening " + c.Name + "...", "", true);
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
                ["mode"] = is3 ? "3d" : "2d",
                ["map"] = new Dictionary<string, object>
                {
                    ["id"] = c.MapId, ["name"] = c.Name, ["base"] = "https://" + WorldMapPage.Host + "/" + WorldMapCache.BuildKey + "/" + c.MapId.ToString(CultureInfo.InvariantCulture),
                    ["minCol"] = man.MinCol, ["maxCol"] = man.MaxCol, ["minRow"] = man.MinRow, ["maxRow"] = man.MaxRow, ["sea"] = man.Sea,
                },
                ["layers"] = WorldMapLayers.Catalog(),
                ["on"] = (Settings?.WorldMapLayers ?? WorldMapLayers.DefaultOn.ToList()).Where(WorldMapIds.IsLayerId).Distinct().ToList(),
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
                };
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
        }

        public string Detail() { var d = Settings?.WorldMap3dDetail; return d == "low" || d == "high" ? d : "medium"; }
        public double Exag() { var e = Settings?.WorldMapExag ?? 0; return e >= 1 && e <= 3 ? e : 1.5; }

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
                loadedPage = mode3d ? "3d" : "2d";
                c.Navigate(mode3d ? WorldMapPage.Page3dUrl : WorldMapPage.PageUrl);
                return true;
            }
            catch (Exception e)
            {
                Util.Log("world map web: " + e.Message);
                try { stage.Children.Remove(wv); wv.Dispose(); } catch (Exception) { }
                web = null; webTask = null; loadedPage = null;
                return false;
            }
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
            Show("", "The world map needs Microsoft Edge WebView2", "WebView2 is not installed on this PC, so the map cannot be shown here. The rest of the Atlas works as usual: zones, creatures, objects and routes all still have their own maps.", false);
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
                    break;
                case WorldMapMessageType.Ready:
                    PageReady = true; PageInited = true;
                    Present();
                    SubChanged?.Invoke(CurrentSub);
                    break;
                case WorldMapMessageType.View:
                    {
                        WorldMapMath.LatLngToWorld(m.Lat, m.Lng, out var x, out var y);
                        hasView = true; vx = x; vy = y; vz = m.Z;
                        SubChanged?.Invoke(CurrentSub);
                        break;
                    }
                case WorldMapMessageType.View3:
                    {
                        LastView3 = m;
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
                    if (Settings != null) { Settings.WorldMapLayers = m.On.ToList(); SaveSettings?.Invoke(); }
                    break;
                case WorldMapMessageType.Need:
                    NeedLayer(m.Layer);
                    break;
                case WorldMapMessageType.Webgl:
                    GlOk = m.Ok; GlRenderer = m.Detail;
                    if (!m.Ok && mode3d && mapId >= 0)
                    {
                        // no WebGL: the flat map, with a note (the choice of 3D is kept off until the person asks again)
                        GlFallbackReason = m.Why;
                        mode3d = false;
                        if (Settings != null && Settings.WorldMap3d) { Settings.WorldMap3d = false; SaveSettings?.Invoke(); }
                        ShowModeSegments();
                        target = new WorldMapTarget { MapId = mapId, HasView = hasView, X = vx, Y = vy, Z = vz };
                        var why = m.Why;
                        Dispatcher.BeginInvoke(new Action(() => { if (!mode3d) { Resolve(); SetNote("The 3D view needs WebGL, which is not available here (" + (string.IsNullOrEmpty(why) ? "no reason given" : why) + "); showing the flat map."); } }));
                    }
                    break;
                case WorldMapMessageType.Stats:
                    LastStats = m;
                    break;
                case WorldMapMessageType.Prefs:
                    if (Settings != null) { Settings.WorldMap3dDetail = m.Detail; Settings.WorldMapExag = Math.Round(m.Exag, 1); SaveSettings?.Invoke(); }
                    break;
            }
        }

        void PostLayerInfo(string layer, string state, string note) => Post(Json.Write(new Dictionary<string, object> { ["t"] = "layerinfo", ["id"] = layer, ["state"] = state, ["count"] = 0, ["note"] = note ?? "" }));

        void NeedLayer(string layer)
        {
            int my = mapId, g = initGen;
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
            try { WorldMapService.BuildChanged -= OnBuildChanged; MapData.Changed -= OnMapDataChanged; subscribed = false; } catch (Exception) { }
            try { Thaw(); if (web != null) { stage.Children.Remove(web); web.Dispose(); web = null; webTask = null; } } catch (Exception) { }
            pageHello = false; PageInited = false; PageReady = false; pendingInit = null; mapId = -1; initGen++; buildGen++; loadedPage = null; GlOk = false; borderWaitMap = -1;
        }
    }
}
