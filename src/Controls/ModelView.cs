using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Models3d;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace ElansAddonHub
{
    // ============================================================================================================================
    // 3.6: Atlas 3D models, steps 3 + 4 - the model viewer: a built creature model (CreatureModels, "M2B2") turning slowly and playing its Stand loop in a
    // sandboxed page (src\ModelViewer\viewer.html + viewer.js, three.js r149). The host for the NPC page's "Model" card (AtlasPage.Model.cs).
    //   * the HWND WebView2, as the world map. WebView2CompositionControl (drawn by WPF, the plan's first choice) was tried and does not run in this build:
    //     it needs the Windows SDK projection (Microsoft.Windows.SDK.NET, a net10.0-windows10.0.17763.0 target) for its GraphicsCaptureSession, and the
    //     Outpost targets plain net10.0-windows - its Loaded handler throws FileNotFoundException. So the airspace is handled here:
    //       - scrolling: the web view's window gets a window region (SetWindowRgn) of the part of the card inside the page's ScrollViewer, updated after
    //         every layout pass that moved it - the page clips it as if WPF drew it; nothing of it shows over the Atlas header or the tab strip
    //       - a WPF layer over it (the Ctrl+K palette): FreezeAsync swaps the live view for a snapshot (CapturePreviewAsync into an Image), Thaw swaps back
    //         (WorldMapView's way); popups (menus, tooltips, dropdowns) are windows of their own and are drawn above it anyway
    //       - Ctrl+K pressed while the page has the keyboard is handed to the app (PaletteRequested), as the world map does
    //   * the web view is made the first time the control is visible with a model to show (lazy), and let go by Dispose (the page leaves the NPC page)
    //   * sandbox as the world map's: the WorldMap profile, InPrivate, dev tools / host objects / context menu / zoom / autofill off, every permission denied,
    //     navigation only to its own page on its own virtual host https://models.outpost.local/ (mapped read-only to <data>\atlas\models: the page in
    //     _page, the models beside it), every request elsewhere answered 403, no new windows, no frames, messages checked (source, size, kind)
    //   * 3.6 step 5: Load hands the page another look of the same creature ("load"): the same web view, the page keeps its view; every "state"
    //     message names its model and one about a model no longer asked for is dropped
    //   * rendering pauses while the control is not visible, is scrolled out of its ScrollViewer or is frozen (a "pause" message: the page stops its loop)
    // ============================================================================================================================
    public sealed class ModelView : Grid
    {
        public enum ViewState { Idle, Starting, Loading, Shown, Failed, NoWebView }
        public ViewState State { get; private set; } = ViewState.Idle;
        public string Reason { get; private set; }
        public event Action StateChanged;
        public event Action PaletteRequested;           // Ctrl+K while the page had the keyboard (the page is another window: WPF never sees the key)

        WebView2 web;
        Task<bool> webTask;
        string modelUp;                  // 3.7: "head" or null, sent with "show"
        string modelRel;                 // "<key>/m/<display>.m2b2" (3.7: or "<key>/m/item-<display>.m2b2") under the host
        bool pageHello, paused, disposed, frozen;
        Image freezeImg;
        ScrollViewer scroller;
        public static bool ForceNoWebViewForTest;
        public int BlockedForTest;
        public readonly List<string> BlockedUrlsForTest = new List<string>();
        public readonly List<string> RefusedForTest = new List<string>();
        public readonly List<string> ReceivedForTest = new List<string>();
        public bool HasWebForTest => web != null;
        public bool PageHelloForTest => pageHello;
        public bool PausedForTest => paused;
        public bool FrozenForTest => frozen;
        public bool FrozenImageShownForTest => freezeImg != null && freezeImg.Source != null;
        public bool WebHiddenForTest => web != null && web.Visibility != Visibility.Visible;
        public string ModelForTest => modelRel;
        public FrameworkElement WebForTest => web;
        public CoreWebView2 CoreForTest => web?.CoreWebView2;
        public Int32Rect ClipForTest => lastClip;           // the window region now set, in device pixels of the web view's window (Empty = none set yet)
        public int ClipUpdatesForTest;
        public int PausePostsForTest;
        public int LoadPostsForTest, WebCreatedForTest;     // 3.6 step 5: "load" messages posted; web views made by this control

        public ModelView()
        {
            ClipToBounds = true;
            Focusable = false;
            IsVisibleChanged += (s, e) => { if (IsVisible) MaybeStart(); UpdatePause(); };
            Loaded += (s, e) => HookScroller();
            Unloaded += (s, e) => UnhookScroller();
            LayoutUpdated += OnLayoutUpdated;
        }

        // ---------------------------------------------------------------- the model
        // a model file CreatureModels made (under CreatureModels.Root); the view starts when it is visible
        // 3.7: up = "head" for a staff or polearm (its heavier end goes on top; otherwise the far end from the grip)
        public void Show(string modelPath, string up = null)
        {
            if (disposed) return;
            var rel = RelOf(modelPath);
            if (rel == null) { SetState(ViewState.Failed, "the model file is not in the models folder"); return; }
            modelRel = rel; modelUp = up == "head" ? "head" : null;
            SetState(ViewState.Loading, null);
            if (pageHello) PostShow(); else MaybeStart();
        }

        // 3.6 step 5: another look of the same creature - the page loads the other file in the web view it has (a "load" message: the view keeps its
        // turn and zoom, the look before stays drawn until the new one is in). Before the page has said hello it is the same as Show.
        public void Load(string modelPath)
        {
            if (disposed) return;
            var rel = RelOf(modelPath);
            if (rel == null) { SetState(ViewState.Failed, "the model file is not in the models folder"); return; }
            if (rel == modelRel && State == ViewState.Shown) return;
            modelRel = rel;
            SetState(ViewState.Loading, null);
            if (!pageHello) { MaybeStart(); return; }
            LoadPostsForTest++;
            Post("{\"t\":\"load\",\"model\":" + Json.Write(modelRel) + "}");
            UpdatePause(true);
        }

        static readonly Regex RelRx = new Regex(@"^[A-Za-z0-9._-]{1,80}/m/(item-)?\d{1,9}\.m2b2$", RegexOptions.CultureInvariant);     // 3.7: item-<display> for an item model
        public static string RelOf(string modelPath)
        {
            try
            {
                if (string.IsNullOrEmpty(modelPath)) return null;
                var rel = Path.GetRelativePath(Path.GetFullPath(CreatureModels.Root), Path.GetFullPath(modelPath)).Replace('\\', '/');
                return RelRx.IsMatch(rel) ? rel : null;
            }
            catch (Exception) { return null; }
        }

        void SetState(ViewState s, string why)
        {
            if (State == s && Reason == why) return;
            State = s; Reason = why;
            try { StateChanged?.Invoke(); } catch (Exception e) { Util.Log("model view: " + e.Message); }
        }

        void MaybeStart()
        {
            if (disposed || modelRel == null || !IsVisible) return;
            if (webTask != null && !webTask.IsFaulted) return;
            webTask = CreateWeb();
        }

        // ---------------------------------------------------------------- the web view
        async Task<bool> CreateWeb()
        {
            SetState(ViewState.Starting, null);
            string err = null;
            bool avail = !ForceNoWebViewForTest && await Task.Run(() => WebEngine.RuntimeAvailable(out err));
            if (disposed) return false;
            if (!avail) { Util.Log("model view: WebView2 not available: " + (err ?? "switched off for the test")); SetState(ViewState.NoWebView, "Microsoft Edge WebView2 is not installed on this PC"); return false; }
            var wv = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x16, 0x19, 0x1C) };
            Children.Insert(0, wv);
            web = wv; lastClip = Int32Rect.Empty; WebCreatedForTest++;
            try
            {
                await WebEngine.Attach(wv, WebProfile.WorldMap, true);
                if (disposed) { DropWeb(); return false; }
                await Task.Run(() => ModelViewerPage.Ensure());
                var c = wv.CoreWebView2;
                Harden(c);
                HookKeys(wv);
                Directory.CreateDirectory(CreatureModels.Root);
                c.SetVirtualHostNameToFolderMapping(ModelViewerPage.Host, CreatureModels.Root, CoreWebView2HostResourceAccessKind.Deny);
                c.WebMessageReceived += OnMessage;
                c.ProcessFailed += (o, e) => { string k = ""; try { k = e.ProcessFailedKind.ToString(); } catch (Exception) { } Util.Log("model view: a browser process stopped: " + k); if (k.IndexOf("Render", StringComparison.OrdinalIgnoreCase) >= 0 && k.IndexOf("Frame", StringComparison.OrdinalIgnoreCase) < 0) Dispatcher.BeginInvoke(new Action(() => SetState(ViewState.Failed, "the viewer stopped"))); };
                lastClip = Int32Rect.Empty; UpdateClip();
                c.Navigate(ModelViewerPage.PageUrl);
                return true;
            }
            catch (Exception e)
            {
                Util.Log("model view: the page could not be started: " + e.Message);
                DropWeb();
                SetState(ViewState.Failed, "the viewer could not be started");
                return false;
            }
        }

        void DropWeb()
        {
            try { Thaw(); if (web != null) { Children.Remove(web); web.Dispose(); } } catch (Exception) { }
            web = null; pageHello = false; lastClip = Int32Rect.Empty;
        }

        public static bool IsOwnUrl(string url)
        {
            if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
            return u.Scheme == Uri.UriSchemeHttps && string.Equals(u.Host, ModelViewerPage.Host, StringComparison.OrdinalIgnoreCase) && u.Port == 443 && string.IsNullOrEmpty(u.UserInfo);
        }
        // the only document: the viewer page itself
        static bool IsPageUrl(string url) => IsOwnUrl(url) && Uri.TryCreate(url, UriKind.Absolute, out var u) && string.Equals(u.AbsolutePath, "/" + ModelViewerPage.Folder + "/viewer.html", StringComparison.Ordinal);

        // the sandbox: the world map's rules (WorldMapView.Harden) for this host
        void Harden(CoreWebView2 c)
        {
            WebEngine.Harden(c, true);
            c.Settings.AreDefaultContextMenusEnabled = false;
            c.Settings.IsWebMessageEnabled = true;                   // the one channel: validated message by message
            c.Settings.IsStatusBarEnabled = false;
            c.Settings.IsZoomControlEnabled = false;
            c.Settings.IsPinchZoomEnabled = false;
            c.Settings.AreDevToolsEnabled = false;
            c.Settings.AreHostObjectsAllowed = false;
            c.NavigationStarting += (o, e) => { if (!IsPageUrl(e.Uri) && !string.Equals(e.Uri, "about:blank", StringComparison.OrdinalIgnoreCase)) e.Cancel = true; };
            c.FrameNavigationStarting += (o, e) => e.Cancel = true;      // the page has no frames
            c.NewWindowRequested += (o, e) => { e.Handled = true; };
            c.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            c.WebResourceRequested += (o, e) =>
            {
                var u = e.Request.Uri ?? "";
                if (u.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || u.StartsWith("blob:", StringComparison.OrdinalIgnoreCase)) return;
                if (IsOwnUrl(u)) return;
                BlockedForTest++; if (BlockedUrlsForTest.Count < 20) BlockedUrlsForTest.Add(u);
                e.Response = c.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
            };
        }

        [DllImport("user32.dll")] static extern short GetKeyState(int vk);
        // Ctrl+K inside the page: the browser's controller sees the key, WPF does not (WorldMapView.HookKeys, the same reflection)
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
            }
            catch (Exception ex) { Util.Log("model view: key hook: " + ex.Message); }
        }

        // ---------------------------------------------------------------- messages
        void Post(string json) { try { web?.CoreWebView2?.PostWebMessageAsJson(json); } catch (Exception e) { Util.Log("model view post: " + e.Message); } }
        void PostShow() { if (modelRel != null) Post("{\"t\":\"show\",\"model\":" + Json.Write(modelRel) + (modelUp != null ? ",\"up\":\"" + modelUp + "\"" : "") + "}"); UpdatePause(true); }

        void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string src = null, json = null;
            try { src = e.Source; json = e.WebMessageAsJson; } catch (Exception) { }
            if (!IsPageUrl(src)) { RefusedForTest.Add("source"); return; }
            Handle(json);
        }

        // (public for the self-test: the same path as a message from the page)
        public void Handle(string json)
        {
            if (string.IsNullOrEmpty(json) || json.Length > 4096) { RefusedForTest.Add("size"); return; }
            Dictionary<string, object> d;
            try { d = Json.Obj(json); } catch (Exception) { d = null; }
            var t = d?.Str("t");
            if (t == null) { RefusedForTest.Add("shape"); return; }
            if (ReceivedForTest.Count < 50) ReceivedForTest.Add(t);
            switch (t)
            {
                case "hello":
                    pageHello = true;
                    if (!d.Bool("gl")) { SetState(ViewState.Failed, "this PC's graphics cannot draw 3D here (WebGL)"); return; }
                    PostShow();
                    break;
                case "state":
                    // 3.6 step 5: the page names the model it speaks of; an answer about a look that is no longer asked for is dropped
                    var about = d.Str("model"); if (about == "") about = null;
                    if (about != null && !RelRx.IsMatch(about)) { RefusedForTest.Add("model"); return; }
                    if (about != null && about != modelRel) { RefusedForTest.Add("stale"); return; }
                    var st = d.Str("state");
                    if (st == "shown") SetState(ViewState.Shown, null);
                    else if (st == "error") SetState(ViewState.Failed, Cut(d.Str("why")) ?? "the model could not be shown");
                    else RefusedForTest.Add("state");
                    break;
                case "error":
                    Util.Log("model view page: " + Cut(d.Str("msg")));
                    break;
                default: RefusedForTest.Add("kind"); break;
            }
        }
        static string Cut(string s) => s == null ? null : s.Length > 160 ? s.Substring(0, 160) : s;

        // ---------------------------------------------------------------- airspace 1: the window region = the part of the card inside the ScrollViewer
        [DllImport("gdi32.dll")] static extern IntPtr CreateRectRgn(int l, int t, int r, int b);
        [DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
        [DllImport("user32.dll")] static extern int SetWindowRgn(IntPtr hwnd, IntPtr rgn, bool redraw);
        Int32Rect lastClip = Int32Rect.Empty;
        bool lastRound;

        // the visible part of this control, in its own coordinates (Rect.Empty = none)
        Rect VisiblePart()
        {
            if (!IsVisible || ActualWidth < 1 || ActualHeight < 1) return Rect.Empty;
            var own = new Rect(0, 0, ActualWidth, ActualHeight);
            if (scroller == null || !scroller.IsVisible) return own;
            try
            {
                var toScroller = TransformToAncestor(scroller);
                var r = toScroller.TransformBounds(own);
                var vis = Rect.Intersect(r, new Rect(0, 0, scroller.ViewportWidth, scroller.ViewportHeight));
                if (vis.IsEmpty || vis.Width < 0.5 || vis.Height < 0.5) return Rect.Empty;
                return new Rect(vis.X - r.X, vis.Y - r.Y, vis.Width, vis.Height);
            }
            catch (Exception) { return own; }
        }

        void UpdateClip()
        {
            if (web == null || web.Handle == IntPtr.Zero) { UpdatePause(); return; }
            var vis = VisiblePart();
            var src = PresentationSource.FromVisual(this);
            double sx = 1, sy = 1;
            if (src?.CompositionTarget != null) { var m = src.CompositionTarget.TransformToDevice; sx = m.M11; sy = m.M22; }
            Int32Rect want; bool round;
            if (vis.IsEmpty) { want = new Int32Rect(0, 0, 0, 0); round = false; }
            else
            {
                int l = (int)Math.Floor(vis.X * sx), t = (int)Math.Floor(vis.Y * sy), r = (int)Math.Ceiling(vis.Right * sx), b = (int)Math.Ceiling(vis.Bottom * sy);
                want = new Int32Rect(l, t, Math.Max(0, r - l), Math.Max(0, b - t));
                round = Math.Abs(vis.Width - ActualWidth) < 0.5 && Math.Abs(vis.Height - ActualHeight) < 0.5;     // whole: the card's 8 px corners
            }
            if (!lastClip.IsEmpty && want.Equals(lastClip) && round == lastRound) { UpdatePause(); return; }
            var rgn = round ? CreateRoundRectRgn(want.X, want.Y, want.X + want.Width + 1, want.Y + want.Height + 1, (int)Math.Round(16 * sx), (int)Math.Round(16 * sy))
                            : CreateRectRgn(want.X, want.Y, want.X + want.Width, want.Y + want.Height);
            if (SetWindowRgn(web.Handle, rgn, true) == 0) DeleteObject(rgn);      // on success the system owns the region
            lastClip = want; lastRound = round; ClipUpdatesForTest++;
            UpdatePause();
        }

        // ---------------------------------------------------------------- airspace 2: a WPF layer over the view (the palette): a snapshot in its place
        public async Task<bool> FreezeAsync()
        {
            if (frozen) return true;
            if (web?.CoreWebView2 == null || web.Visibility != Visibility.Visible) return false;
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
            catch (Exception e) { Util.Log("model view snapshot: " + e.Message); bmp = null; }
            if (web == null) return false;
            freezeImg = new Image { Source = bmp, Stretch = Stretch.Fill, IsHitTestVisible = false };
            Children.Add(freezeImg);
            frozen = true;
            web.Visibility = Visibility.Hidden;
            UpdatePause();
            return true;
        }

        public void Thaw()
        {
            if (!frozen) return;
            frozen = false;
            if (freezeImg != null) { Children.Remove(freezeImg); freezeImg = null; }
            if (web != null) web.Visibility = Visibility.Visible;
            lastClip = Int32Rect.Empty; UpdateClip();
        }

        // ---------------------------------------------------------------- pause: not visible, scrolled out of the page, or frozen
        void HookScroller()
        {
            UnhookScroller();
            DependencyObject p = this;
            while (p != null && !(p is ScrollViewer)) p = VisualTreeHelper.GetParent(p);
            scroller = p as ScrollViewer;
            if (scroller != null) scroller.ScrollChanged += OnScroll;
            lastClip = Int32Rect.Empty; UpdateClip();
        }
        void UnhookScroller() { if (scroller != null) scroller.ScrollChanged -= OnScroll; scroller = null; }
        void OnScroll(object s, ScrollChangedEventArgs e) => UpdateClip();
        void OnLayoutUpdated(object s, EventArgs e) => UpdateClip();

        public bool InViewForTest => !VisiblePart().IsEmpty;

        void UpdatePause(bool force = false)
        {
            if (web?.CoreWebView2 == null || !pageHello) return;
            bool want = frozen || VisiblePart().IsEmpty;
            if (want == paused && !force) return;
            paused = want; PausePostsForTest++;
            Post(want ? "{\"t\":\"pause\",\"on\":true}" : "{\"t\":\"pause\",\"on\":false}");
        }

        // ---------------------------------------------------------------- test helpers
        public async Task<string> EvalForTest(string js)
        {
            if (web?.CoreWebView2 == null) return null;
            return await web.CoreWebView2.ExecuteScriptAsync(js);
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

        // the page is left: the browser goes
        public void Dispose()
        {
            disposed = true;
            LayoutUpdated -= OnLayoutUpdated;
            UnhookScroller();
            DropWeb();
            webTask = null; modelRel = null;
        }
    }
}
