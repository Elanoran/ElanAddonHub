using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
    // sandboxed WebView2 page with Leaflet (src\WorldMap\). This control is the host:
    //   * the continent switch (WPF, above the page - a WebView2 cannot be overlapped) and the build progress / notes (WPF, shown INSTEAD of the page)
    //   * the page, served from <data>\atlas\worldmap through a private virtual host (https://worldmap.outpost.local/), nothing from the internet
    //   * the message protocol (WorldMapProtocol): the page's messages are checked, the layers are built on request and posted, "Navigate here" copies
    //     a /etk way command, a click opens an Atlas id
    // Sandbox: dev tools off, no host objects, no new windows, navigation only inside the virtual host (everything else is cancelled), every request
    // to another host is answered with 403, every permission denied, an InPrivate profile of its own. Without the WebView2 runtime the control shows a
    // calm note instead of the page.
    // Phase 2 (a 3D terrain view) adds a second host for the same continent in this control: the tile / world math is in WorldMapMath.
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
        public string CurrentSub => WorldMapIds.SubOf(mapId, slug, hasView, vx, vy, vz);
        public string Note => noteText.Text;
        public string ProgressText => overlayTitle.Text;
        public bool OverlayShown => overlay.Visibility == Visibility.Visible;
        public bool NoWebViewShown { get; private set; }
        public bool WebShown => web != null && web.Visibility == Visibility.Visible;
        public IReadOnlyList<string> Continents => segs.Select(s => (string)s.Content).ToList();
        public string SelectedContinent => segs.FirstOrDefault(s => s.IsChecked == true)?.Content as string ?? "";

        // ---- parts
        readonly StackPanel bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        readonly Border segHost = new Border();
        readonly StackPanel segPanel = new StackPanel { Orientation = Orientation.Horizontal };
        readonly List<RadioButton> segs = new List<RadioButton>();
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
        bool pageHello, suppressSeg;
        bool subscribed;
        Action overlayAction;

        public WorldMapView()
        {
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            segHost.Style = (Style)FindResource("SegmentHost");
            segHost.Child = segPanel;
            bar.Children.Add(segHost);
            noteText.Style = (Style)FindResource("Text.Caption");
            bar.Children.Add(noteText);
            SetRow(bar, 0); Children.Add(bar);

            var card = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24) };
            overlayGlyph.Text = ""; overlayGlyph.FontFamily = new FontFamily("Segoe MDL2 Assets"); overlayGlyph.FontSize = 32; overlayGlyph.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text.Tertiary");
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
            target = t;
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
                    case MapData.State.Disabled: Show("", "The world map is made from your game files", "Game data is switched off. Switch \"Read item data from my WoW installation\" on in Settings > General to use the world map.", false); break;
                    case MapData.State.Unavailable: Show("", "The world map needs your game's maps", "The map tables could not be read (" + (MapData.Reason ?? "unknown") + "). Check the WoW folder in Settings > General.", false); break;
                    default: Show("", "Reading the maps from your game files...", "The world map starts as soon as the map tables are read.", true); break;
                }
                return;
            }
            var conts = WorldMapService.Continents(idx);
            RefreshSegments(conts);
            if (conts.Count == 0) { Show("", "This game has no world map", "Its files list no continent with a world table and minimap pictures.", false); return; }
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
            bool same = c.MapId == mapId && PageInited;
            mapId = c.MapId; slug = c.Slug;
            hasView = t.HasView; if (t.HasView) { vx = t.X; vy = t.Y; vz = t.Z; }
            if (Settings != null && Settings.WorldMapLast != c.Slug) { Settings.WorldMapLast = c.Slug; SaveSettings?.Invoke(); }
            suppressSeg = true; foreach (var s in segs) s.IsChecked = (int)s.Tag == mapId; suppressSeg = false;
            int gen = ++buildGen;
            if (same && web != null && WorldMapCache.Built(mapId) != null)
            {
                if (t.HasView) { WorldMapMath.WorldToLatLng(t.X, t.Y, out var lat, out var lng); Post(WorldMapProtocol.Goto(lat, lng, t.Z, t.Pin ? "" : null)); }
                else if (t.Slug.Length > 0) Post("{\"t\":\"fit\"}");      // the continent by name: the whole continent again
                Present();
                SubChanged?.Invoke(CurrentSub);
                return;
            }
            if (WorldMapCache.Built(mapId) != null) { StartPage(c, t); return; }
            ShowBuild(WorldMapService.StateOf(mapId), c.Name);
            WorldMapService.EnsureBuilt(mapId).ContinueWith(task =>
            {
                try { Dispatcher.BeginInvoke(new Action(() => { if (gen != buildGen || mapId != c.MapId) return; if (task.Status == TaskStatus.RanToCompletion && task.Result) StartPage(c, t); else ShowBuildFailed(c); })); } catch (Exception) { }
            });
        }

        // ---------------------------------------------------------------- overlay (build progress, notes, no WebView2)
        void Show(string glyph, string title, string hint, bool busy)
        {
            overlayGlyph.Text = glyph; overlayTitle.Text = title; overlayHint.Text = hint;
            overlayHint.Visibility = hint.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            overlayBar.Visibility = Visibility.Collapsed; overlayButton.Visibility = Visibility.Collapsed;
            if (web != null) web.Visibility = Visibility.Hidden;
            overlay.Visibility = Visibility.Visible;
        }

        void ShowBuild(WorldBuildState st, string name)
        {
            Show("", st != null && st.Running ? st.Text : "Building " + name + "...", "The first time you open a continent, the Outpost makes its map from your game files, in the background. This happens once per game version; later you open it at once. Nothing is uploaded and nothing is written to the WoW folder.", true);
            overlayBar.Visibility = Visibility.Visible;
            overlayBar.Value = st != null && st.Total > 0 ? Math.Min(1, st.Done / (double)st.Total) : 0;
            overlayButton.Content = "Cancel"; overlayButton.Visibility = Visibility.Visible;
            int id = mapId; overlayAction = () => WorldMapService.Cancel(id);
            if (st != null && st.Running && st.Text.Length > 0) overlayTitle.Text = st.Text;
        }

        void ShowBuildFailed(WorldContinent c)
        {
            var st = WorldMapService.StateOf(c.MapId);
            Show("", "The " + c.Name + " map could not be built", st?.Error ?? "Something went wrong while reading the game files.", false);
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
                    var st = WorldMapService.StateOf(id);
                    if (st == null || !st.Running) return;
                    overlayTitle.Text = st.Text;
                    overlayBar.Visibility = Visibility.Visible;
                    overlayBar.Value = st.Total > 0 ? Math.Min(1, st.Done / (double)st.Total) : 0;
                }));
            }
            catch (Exception) { }
        }

        void SetNote(string t) { noteText.Text = t ?? ""; }

        void Present()
        {
            if (web == null || !PageInited) return;
            overlay.Visibility = Visibility.Collapsed;
            web.Visibility = Visibility.Visible;
        }

        // ---------------------------------------------------------------- the page
        async void StartPage(WorldContinent c, WorldMapTarget t)
        {
            int gen = ++initGen;
            Show("", "Opening " + c.Name + "...", "", true);
            bool ok;
            try { ok = await EnsureWeb(); }
            catch (Exception e) { Util.Log("world map: " + e); ok = false; }
            if (gen != initGen) return;
            if (!ok) { ShowNoWebView(); return; }
            var man = WorldMapCache.Built(c.MapId);
            if (man == null) { ShowBuildFailed(c); return; }
            PageReady = false; PageInited = false;
            var init = new Dictionary<string, object>
            {
                ["t"] = "init",
                ["map"] = new Dictionary<string, object>
                {
                    ["id"] = c.MapId, ["name"] = c.Name, ["base"] = "https://" + WorldMapPage.Host + "/" + WorldMapCache.BuildKey + "/" + c.MapId.ToString(CultureInfo.InvariantCulture),
                    ["minCol"] = man.MinCol, ["maxCol"] = man.MaxCol, ["minRow"] = man.MinRow, ["maxRow"] = man.MaxRow, ["sea"] = man.Sea,
                },
                ["layers"] = WorldMapLayers.Catalog(),
                ["on"] = (Settings?.WorldMapLayers ?? WorldMapLayers.DefaultOn.ToList()).Where(WorldMapIds.IsLayerId).Distinct().ToList(),
            };
            if (t.HasView)
            {
                WorldMapMath.WorldToLatLng(t.X, t.Y, out var lat, out var lng);
                var v = new Dictionary<string, object> { ["t"] = "goto", ["lat"] = lat, ["lng"] = lng, ["z"] = t.Z };
                if (t.Pin) v["pin"] = new Dictionary<string, object> { ["name"] = "", ["tip"] = "" };
                init["view"] = v;
            }
            var json = Json.Write(init);
            if (pageHello) Post(json); else pendingInit = json;
        }

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
                Directory.CreateDirectory(WorldMapCache.Root);
                c.SetVirtualHostNameToFolderMapping(WorldMapPage.Host, WorldMapCache.Root, CoreWebView2HostResourceAccessKind.Deny);
                c.WebMessageReceived += OnMessage;
                c.Navigate(WorldMapPage.PageUrl);
                return true;
            }
            catch (Exception e)
            {
                Util.Log("world map web: " + e.Message);
                try { stage.Children.Remove(wv); wv.Dispose(); } catch (Exception) { }
                web = null; webTask = null;
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
                BlockedForTest++;
                e.Response = c.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
            };
        }
        public int BlockedForTest;
        public static bool ForceNoWebViewForTest;                 // self-test: pretend WebView2 is not installed

        void ShowNoWebView()
        {
            NoWebViewShown = true;
            Show("", "The world map needs Microsoft Edge WebView2", "WebView2 is not installed on this PC, so the map cannot be shown here. The rest of the Atlas works as usual: zones, creatures, objects and routes all still have their own maps.", false);
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

        // (public for the self-test: the same path as a message from the page)
        public void Handle(string json)
        {
            var m = WorldMapProtocol.Parse(json, out var refusal);
            if (m == null) { RefusedForTest.Add(refusal ?? "refused"); Util.Log("world map: refused a message from the page: " + refusal); return; }
            ReceivedForTest.Add(m.Type.ToString().ToLowerInvariant());
            switch (m.Type)
            {
                case WorldMapMessageType.Hello:
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
            }
        }

        void NeedLayer(string layer)
        {
            int my = mapId, g = initGen;
            Task.Run(() => WorldMapLayers.Build(layer, my)).ContinueWith(t =>
            {
                try
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (my != mapId || g != initGen) return;
                        var r = t.Status == TaskStatus.RanToCompletion ? t.Result : new LayerResult { Note = "This layer could not be built." };
                        if (r.Json.Length == 0) Post(Json.Write(new Dictionary<string, object> { ["t"] = "layerinfo", ["id"] = layer, ["state"] = "empty", ["count"] = 0, ["note"] = r.Note }));
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

        public CoreWebView2 CoreForTest => web?.CoreWebView2;
        public bool HasWebForTest => web != null;

        // The Atlas leaves the page (another page is shown): nothing to stop, the page keeps what it had
        public void Dispose()
        {
            try { WorldMapService.BuildChanged -= OnBuildChanged; MapData.Changed -= OnMapDataChanged; subscribed = false; } catch (Exception) { }
            try { if (web != null) { stage.Children.Remove(web); web.Dispose(); web = null; webTask = null; } } catch (Exception) { }
            pageHello = false; PageInited = false; PageReady = false; pendingInit = null; mapId = -1; initGen++; buildGen++;
        }
    }
}
