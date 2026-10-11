using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using ElansAddonHub.Services;

namespace ElansAddonHub.Lodge
{
    // Overlay video player, the picture viewer's twin: dark scrim, Esc / click outside / X closes, DialogIn / DialogOut motion.
    // The player is an embedded WebView2 (private and locked down, see Harden). Without the WebView2 runtime (or if anything in
    // creating it throws) the video opens in the default browser instead.
    public partial class VideoViewer : UserControl
    {
        YouTubeLink link;
        WebView2 web;
        int token;
        bool fullscreen;

        // YouTube refuses to play in a page without an https origin / referrer ("Error 153"), which NavigateToString (about:blank) lacks.
        // So the generated page is written to <data>\webview2\site and served from a virtual https host that exists only inside this WebView2.
        public const string PageHost = "outpost.local", PageFile = "player.html";

        public bool IsOpen => Visibility == Visibility.Visible;
        public bool HasPlayerForTest => web != null;
        public string StatusForTest => Status.Text;
        // design shots: RenderTargetBitmap cannot see a WebView2 (its own window), so the page is captured separately and pasted in
        public async Task<byte[]> CapturePlayerForTest(Visual relativeTo, Action<Rect> bounds)
        {
            if (web?.CoreWebView2 == null) return null;
            bounds(web.TransformToAncestor(relativeTo).TransformBounds(new Rect(0, 0, web.ActualWidth, web.ActualHeight)));
            using (var ms = new MemoryStream())
            {
                await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
                return ms.ToArray();
            }
        }

        public VideoViewer() { InitializeComponent(); }

        // ---------------------------------------------------------------- policy (pure, tested offline)

        // The only places the player may navigate to: the nocookie embed (and youtube.com/embed), YouTube's picture / video hosts.
        // Exact host or a real subdomain: "youtube.com.evil.net" and "evilyoutube-nocookie.com" fail.
        public static bool IsAllowedNavigation(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            if (string.Equals(url, "about:blank", StringComparison.OrdinalIgnoreCase)) return true;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(u.UserInfo)) return false;
            var host = u.Host.ToLowerInvariant();
            var path = u.AbsolutePath;
            if (host == PageHost) return path == "/" + PageFile;   // our own player page (virtual host below)
            if (host == "youtube-nocookie.com" || host == "www.youtube-nocookie.com" || host == "youtube.com" || host == "www.youtube.com")
                return path.StartsWith("/embed/", StringComparison.Ordinal);
            return HostIs(host, "ytimg.com") || HostIs(host, "googlevideo.com");
        }
        static bool HostIs(string host, string domain) => host == domain || host.EndsWith("." + domain, StringComparison.Ordinal);

        // the page the player lives in: one 16:9 iframe of the nocookie embed, nothing else (CSP: frames from the embed host only)
        public static string BuildHtml(YouTubeLink l)
        {
            var id = l?.Id;
            if (id == null || !Regex.IsMatch(id, "^[A-Za-z0-9_-]{11}$")) throw new ArgumentException("bad video id");
            var src = "https://www.youtube-nocookie.com/embed/" + id + "?autoplay=1&start=" + Math.Max(0, l.StartSeconds) + "&rel=0&playsinline=1";
            return "<!doctype html><html><head><meta charset=\"utf-8\">"
                + "<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; frame-src https://www.youtube-nocookie.com; style-src 'unsafe-inline'\">"
                + "<meta name=\"referrer\" content=\"strict-origin-when-cross-origin\">"
                + "<style>html,body{margin:0;height:100%;background:#000;overflow:hidden}iframe{border:0;width:100%;height:100%}</style></head><body>"
                + "<iframe src=\"" + src + "\" allow=\"autoplay; encrypted-media; picture-in-picture; fullscreen\" allowfullscreen referrerpolicy=\"strict-origin-when-cross-origin\"></iframe>"
                + "</body></html>";
        }

        // ---------------------------------------------------------------- open / close

        public static void OpenInBrowser(YouTubeLink l)
        {
            if (l == null) return;
            try { Process.Start(new ProcessStartInfo(l.WatchUrl) { UseShellExecute = true }); } catch (Exception e) { Util.Log("video: browser " + e.Message); }
        }

        public async void Open(YouTubeLink l, string title = null, string channel = null)
        {
            if (l == null) return;
            if (IsOpen) CloseCore();
            link = l;
            var my = ++token;
            CapName.Text = title ?? "";
            CapInfo.Text = channel ?? "";
            Status.Text = "Loading the player...";
            Status.Visibility = Visibility.Visible;
            fullscreen = false;
            Visibility = Visibility.Visible;
            Fit();
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Motion.Fast));
            Motion.DialogIn(Box);
            Keyboard.Focus(this); Focus();
            try
            {
                var wv = new WebView2 { Visibility = Visibility.Hidden };
                Stage.Children.Add(wv);
                web = wv;
                Fit();
                var env = await CreateEnvironment();
                // private (InPrivate) profile: no cookies, history or cache survive the window
                var opts = env.CreateCoreWebView2ControllerOptions();
                opts.IsInPrivateModeEnabled = true;
                await wv.EnsureCoreWebView2Async(env, opts);
                if (my != token || web != wv) return;   // closed meanwhile
                Harden(wv.CoreWebView2);
                var site = Path.Combine(Util.DataDir, "webview2", "site");
                Directory.CreateDirectory(site);
                File.WriteAllText(Path.Combine(site, PageFile), BuildHtml(l), new System.Text.UTF8Encoding(false));
                wv.CoreWebView2.SetVirtualHostNameToFolderMapping(PageHost, site, CoreWebView2HostResourceAccessKind.Deny);
                wv.CoreWebView2.Navigate("https://" + PageHost + "/" + PageFile);
                wv.Visibility = Visibility.Visible;
                Status.Visibility = Visibility.Collapsed;
            }
            catch (Exception e)
            {
                if (my != token) return;
                Util.Log("video: WebView2 unavailable, using the browser: " + e.Message);
                CloseCore();
                OpenInBrowser(l);
            }
        }

        public void Close()
        {
            if (!IsOpen) return;
            Motion.DialogOut(Box, () => { if (IsOpen) CloseCore(); });
        }

        // playback stops here: the page goes to about:blank, then the control (and its browser process) is disposed
        void CloseCore()
        {
            token++;
            var wv = web; web = null;
            if (wv != null)
            {
                try { if (wv.CoreWebView2 != null) wv.CoreWebView2.Navigate("about:blank"); } catch { }
                try { Stage.Children.Remove(wv); wv.Dispose(); } catch (Exception e) { Util.Log("video dispose: " + e.Message); }
            }
            BeginAnimation(OpacityProperty, null);
            Box.BeginAnimation(OpacityProperty, null); Box.RenderTransform = Transform.Identity; Box.Opacity = 1;
            Visibility = Visibility.Collapsed;
            link = null;
        }

        // ---------------------------------------------------------------- WebView2 setup

        static Task<CoreWebView2Environment> envTask;
        static Task<CoreWebView2Environment> CreateEnvironment() => envTask ?? (envTask = MakeEnvironment());

        static async Task<CoreWebView2Environment> MakeEnvironment()
        {
            try
            {
                ExtractLoader();
                var data = Path.Combine(Util.DataDir, "webview2");   // never the exe folder
                Directory.CreateDirectory(data);
                var o = new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required" };
                return await CoreWebView2Environment.CreateAsync(null, data, o);
            }
            catch { envTask = null; throw; }
        }

        // The exe is a single file (Costura), so WebView2Loader.dll travels inside it as a resource: write it to
        // <data>\webview2\loader\<arch> once and tell WebView2 where it is.
        static void ExtractLoader()
        {
            var arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : IntPtr.Size == 8 ? "x64" : "x86";
            var dir = Path.Combine(Util.DataDir, "webview2", "loader", arch);
            var file = Path.Combine(dir, "WebView2Loader.dll");
            var res = "WebView2Loader." + arch + ".dll";
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(res))
            {
                if (s == null) throw new InvalidOperationException("embedded " + res + " missing");
                if (!File.Exists(file) || new FileInfo(file).Length != s.Length)
                {
                    Directory.CreateDirectory(dir);
                    var tmp = file + "." + Process.GetCurrentProcess().Id + ".tmp";
                    using (var f = File.Create(tmp)) s.CopyTo(f);
                    try { if (File.Exists(file)) File.Delete(file); File.Move(tmp, file); }
                    catch { try { File.Delete(tmp); } catch { } if (!File.Exists(file)) throw; }
                }
            }
            CoreWebView2Environment.SetLoaderDllFolderPath(dir);
        }

        void Harden(CoreWebView2 c)
        {
            var s = c.Settings;
            s.AreDevToolsEnabled = false;
            s.AreDefaultContextMenusEnabled = false;      // no context menu at all
            s.IsStatusBarEnabled = false;
            s.IsPasswordAutosaveEnabled = false;
            s.IsGeneralAutofillEnabled = false;
            s.AreHostObjectsAllowed = false;              // no AddHostObjectToScript, no bridge
            s.IsWebMessageEnabled = false;                // WebMessageReceived is never subscribed to
            s.AreBrowserAcceleratorKeysEnabled = false;
            s.IsZoomControlEnabled = false;
            s.IsSwipeNavigationEnabled = false;
            s.IsPinchZoomEnabled = false;
            try { c.Profile.PreferredTrackingPreventionLevel = CoreWebView2TrackingPreventionLevel.Strict; } catch (Exception e) { Util.Log("video tracking: " + e.Message); }

            c.NavigationStarting += (o, e) =>
            {
                // our own page, the embed, or nothing
                var uri = e.Uri ?? "";
                if (IsAllowedNavigation(uri)) return;
                e.Cancel = true;
                OpenExternal(uri);
            };
            c.FrameNavigationStarting += (o, e) =>
            {
                var uri = e.Uri ?? "";
                if (IsAllowedNavigation(uri)) return;
                e.Cancel = true;
            };
            c.NewWindowRequested += (o, e) => { e.Handled = true; OpenExternal(e.Uri); };
            c.PermissionRequested += (o, e) => { e.State = CoreWebView2PermissionState.Deny; e.Handled = true; };
            c.DownloadStarting += (o, e) => { e.Cancel = true; e.Handled = true; };
            // fullscreen is the one thing the page may do: the player then fills the whole overlay
            c.ContainsFullScreenElementChanged += (o, e) => { fullscreen = c.ContainsFullScreenElement; Fit(); };
        }

        // anything the player tries to leave for goes to the real browser (http/https only), never into this window
        static void OpenExternal(string url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp))
                try { Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true }); } catch { }
        }

        // ---------------------------------------------------------------- layout / input

        void OnSizeChanged(object sender, SizeChangedEventArgs e) => Fit();

        // 16:9, as large as fits inside the margins (the whole overlay while the video is full screen)
        void Fit()
        {
            Stage.Margin = fullscreen ? new Thickness(0) : new Thickness(64, 56, 64, 64);
            Header.Visibility = Caption.Visibility = fullscreen ? Visibility.Collapsed : Visibility.Visible;
            double w = Math.Max(160, ActualWidth - Stage.Margin.Left - Stage.Margin.Right), h = Math.Max(90, ActualHeight - Stage.Margin.Top - Stage.Margin.Bottom);
            double bw = w, bh = w * 9 / 16;
            if (bh > h) { bh = h; bw = h * 16 / 9; }
            if (fullscreen) { bw = w; bh = h; }
            Box.Width = Math.Floor(bw); Box.Height = Math.Floor(bh);
            if (web != null) { web.HorizontalAlignment = HorizontalAlignment.Center; web.VerticalAlignment = VerticalAlignment.Center; web.Width = Box.Width; web.Height = Box.Height; }
        }

        // a click on the player box does nothing; around it closes, like the picture viewer
        void Stage_Down(object sender, MouseButtonEventArgs e)
        {
            for (var d = e.OriginalSource as DependencyObject; d != null; d = VisualTreeHelper.GetParent(d))
                if (d == Box) { e.Handled = true; return; }
        }

        void Backdrop_Down(object sender, MouseButtonEventArgs e) { if (!e.Handled) Close(); }
        void Close_Click(object sender, RoutedEventArgs e) => Close();
        void OpenYt_Click(object sender, RoutedEventArgs e) { var l = link; CloseCore(); OpenInBrowser(l); }

        void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (!IsOpen) return;
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        }
    }
}
