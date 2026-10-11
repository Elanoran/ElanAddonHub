using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // One tab of the Browser page. The WebView2 is created the first time the tab navigates (a new tab is a native page, no browser process).
    public sealed class BrowserTab : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        void Raise(params string[] names) { foreach (var n in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n)); }

        public WebView2 Web;
        public Task Creating;
        public bool Closed, InPrivate, Fullscreen, CanBack, CanForward;
        public int Blocked;
        public string ErrorTitle, ErrorText, ErrorUrl;
        public string PendingUrl;

        string url = "", title = "";
        bool blank = true, loading, selected;
        ImageSource icon;
        double width = 160;

        public string Url { get => url; set { url = value ?? ""; Raise(nameof(Url)); } }
        public bool Blank { get => blank; set { blank = value; Raise(nameof(Title)); } }
        public bool Loading { get => loading; set { loading = value; Raise(nameof(Loading)); } }
        public bool Selected { get => selected; set { selected = value; Raise(nameof(SelectedOpacity), nameof(CloseOpacity), nameof(TitleBrush)); } }
        public double Width { get => width; set { width = value; Raise(nameof(Width)); } }
        public ImageSource Icon { get => icon; set { icon = value; Raise(nameof(Icon), nameof(GlyphVisibility)); } }
        public string RawTitle { get => title; set { title = value ?? ""; Raise(nameof(Title)); } }
        public string Title => blank ? "New tab" : !string.IsNullOrWhiteSpace(title) ? title : !string.IsNullOrEmpty(BrowserUrl.HostOf(url)) ? BrowserUrl.HostOf(url) : "Loading...";
        public bool HasError => ErrorTitle != null;
        public double SelectedOpacity => selected ? 1 : 0;
        public double CloseOpacity => selected ? 1 : 0;
        public Visibility GlyphVisibility => icon == null ? Visibility.Visible : Visibility.Collapsed;
        public Brush TitleBrush => (Brush)Application.Current.Resources[selected ? "Brush.Text.Primary" : "Brush.Text.Secondary"];
        public void ChangedError() => Raise(nameof(HasError));
    }

    public sealed class TileVM
    {
        public string Name { get; set; }
        public string Url { get; set; }
        public ImageSource Icon { get; set; }
        public string Initial => BrowserUrl.Initial(Name, Url);
        public Visibility LetterVisibility => Icon == null ? Visibility.Visible : Visibility.Collapsed;
    }

    // The Browser page: a calm in-app browser (tabs, address bar, quick-link new-tab page) on the shared WebEngine.
    public partial class BrowserPage : UserControl
    {
        Settings settings;
        readonly ObservableCollection<BrowserTab> tabs = new ObservableCollection<BrowserTab>();
        BrowserTab cur;
        bool probed, unavailable, downloadAsking;
        string noticePath, lastUrl;
        readonly Dictionary<string, ImageSource> icons = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        readonly DispatcherTimer noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        int progressToken;

        public event Action<string> ShortcutTab;   // Ctrl+1..4 / Ctrl+, pressed while the web page has the keyboard

        const int MaxTabs = 16;

        public BrowserPage()
        {
            InitializeComponent();
            TabStrip.ItemsSource = tabs;
            noticeTimer.Tick += (s, e) => { noticeTimer.Stop(); Notice.Visibility = Visibility.Collapsed; };
            IsVisibleChanged += (s, e) =>
            {
                if (!(bool)e.NewValue) return;
                if (!probed) { probed = true; _ = Probe(); }
                if (cur != null) ApplyView();
            };
        }

        public void Init(Settings s)
        {
            settings = s;
            LoadIcons();
            RebuildTiles();
            ResetTabs();
        }

        // ================================================================ availability

        public bool Unavailable => unavailable;

        async Task Probe()
        {
            string err = null;
            bool ok = await Task.Run(() => WebEngine.RuntimeAvailable(out err));
            if (!ok) SetUnavailable(err);
        }

        void SetUnavailable(string why)
        {
            if (unavailable) return;
            unavailable = true;
            Util.Log("browser: WebView2 unavailable: " + why);
            FallbackDetail.Text = string.IsNullOrEmpty(why) ? "" : why;
            Chrome.Visibility = Visibility.Collapsed;
            PageArea.Visibility = Visibility.Collapsed;
            Fallback.Visibility = Visibility.Visible;
            foreach (var t in tabs.ToList()) DisposeWeb(t);
        }

        void FallbackOpen_Click(object sender, RoutedEventArgs e)
        {
            var u = lastUrl ?? BrowserUrl.Resolve(AddressBox.Text, settings?.BrowserSearch) ?? (settings?.BrowserTiles ?? BrowserUrl.DefaultTiles()).FirstOrDefault()?.Url ?? "https://www.wowhead.com/classic";
            WebEngine.OpenExternal(u);
        }

        // ================================================================ tabs

        public int TabCount => tabs.Count;
        public BrowserTab Current => cur;

        void ResetTabs()
        {
            foreach (var t in tabs.ToList()) { t.Closed = true; DisposeWeb(t); }
            tabs.Clear();
            cur = null;
            AddTab(null, true);
        }

        BrowserTab AddTab(string url, bool select)
        {
            if (tabs.Count >= MaxTabs) { var last = tabs.Last(); if (select) SelectTab(last); if (url != null) Navigate(last, url); return last; }
            var t = new BrowserTab { InPrivate = !(settings?.BrowserRemember ?? false) };
            tabs.Add(t);
            UpdateWidths();
            if (select || cur == null) SelectTab(t);
            if (url != null) Navigate(t, url);
            return t;
        }

        void SelectTab(BrowserTab t)
        {
            if (t == null) return;
            if (cur != null) cur.Selected = false;
            cur = t; t.Selected = true;
            ApplyView();
            if (t.Blank) { }
        }

        void CloseTab(BrowserTab t)
        {
            if (t == null || !tabs.Contains(t)) return;
            int i = tabs.IndexOf(t);
            t.Closed = true;
            tabs.Remove(t);
            DisposeWeb(t);
            if (tabs.Count == 0) { cur = null; AddTab(null, true); }
            else if (cur == t) { cur = null; SelectTab(tabs[Math.Min(i, tabs.Count - 1)]); }
            UpdateWidths();
        }

        void DisposeWeb(BrowserTab t)
        {
            var w = t.Web; t.Web = null;
            if (w == null) return;
            try { WebHost.Children.Remove(w); w.Dispose(); } catch (Exception e) { Util.Log("browser dispose: " + e.Message); }
        }

        void UpdateWidths()
        {
            double avail = Math.Max(120, (PageArea?.ActualWidth ?? ActualWidth) - 48);
            double w = Math.Max(72, Math.Min(200, Math.Floor(avail / Math.Max(1, tabs.Count)) - 4));
            foreach (var t in tabs) t.Width = w;
        }

        void Page_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateWidths();

        void Tab_Click(object sender, RoutedEventArgs e)
        {
            if (e.Handled) return;
            if ((sender as FrameworkElement)?.Tag is BrowserTab t) SelectTab(t);
        }

        void Tab_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Middle) return;
            if ((sender as FrameworkElement)?.Tag is BrowserTab t) { e.Handled = true; CloseTab(t); }
        }

        void TabClose_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if ((sender as FrameworkElement)?.Tag is BrowserTab t) CloseTab(t);
        }

        void NewTab_Click(object sender, RoutedEventArgs e) => NewTabAndFocus();

        void NewTabAndFocus()
        {
            if (unavailable) return;
            AddTab(null, true);
            FocusAddress();
        }

        // ---- for the rest of the app

        // a link from somewhere else in the Outpost (Lodge chat): a new tab, or the empty current one
        public void OpenUrl(string url)
        {
            var u = BrowserUrl.IsAllowedScheme(url) ? url : null;
            if (u == null) return;
            lastUrl = u;
            if (unavailable) { WebEngine.OpenExternal(u); return; }
            if (cur != null && cur.Blank && cur.Web == null) { Navigate(cur, u); return; }
            AddTab(u, true);
        }

        // Settings changed. profileChanged = private browsing flipped: open tabs belong to the other profile, so they are closed.
        public void SettingsChanged(bool profileChanged)
        {
            if (settings == null) return;
            if (profileChanged) ResetTabs();
            RefreshHint();
            UpdateAddressUi();
        }

        // ================================================================ navigation

        void Navigate(BrowserTab t, string input)
        {
            var url = BrowserUrl.Resolve(input, settings?.BrowserSearch);
            if (url == null) return;
            if (!BrowserUrl.IsAllowedScheme(url)) return;
            lastUrl = url;
            t.Blank = false;
            t.Url = url == "about:blank" ? "" : url;
            t.ErrorTitle = null; t.ChangedError();
            t.RawTitle = "";
            t.Icon = null;
            t.Loading = true;
            if (t == cur) { ApplyView(); StartProgress(); }
            _ = NavigateAsync(t, url);
        }

        async Task NavigateAsync(BrowserTab t, string url)
        {
            try
            {
                await EnsureWeb(t);
                if (t.Closed || t.Web?.CoreWebView2 == null) return;
                t.Web.CoreWebView2.Navigate(url);
            }
            catch (Exception e) { Util.Log("browser navigate: " + e.Message); }
        }

        Task EnsureWeb(BrowserTab t)
        {
            if (t.Web?.CoreWebView2 != null) return Task.CompletedTask;
            return t.Creating ?? (t.Creating = CreateWeb(t));
        }

        async Task CreateWeb(BrowserTab t)
        {
            var wv = new WebView2 { Visibility = Visibility.Hidden, DefaultBackgroundColor = System.Drawing.Color.FromArgb(15, 17, 19) };
            WebHost.Children.Add(wv);
            t.Web = wv;
            try { await WebEngine.Attach(wv, WebProfile.Browser, t.InPrivate); }
            catch (Exception e)
            {
                try { WebHost.Children.Remove(wv); wv.Dispose(); } catch { }
                t.Web = null; t.Creating = null;
                SetUnavailable(e.Message);
                return;
            }
            if (t.Closed || t.Web != wv) { try { WebHost.Children.Remove(wv); wv.Dispose(); } catch { } return; }
            Wire(t, wv);
            ApplyView();
        }

        void Wire(BrowserTab t, WebView2 wv)
        {
            var c = wv.CoreWebView2;
            WebEngine.Harden(c, false);

            // links may only lead to web pages (and about:blank); everything else is refused
            c.NavigationStarting += (o, e) =>
            {
                var uri = e.Uri ?? "";
                if (uri.StartsWith("chrome-error://", StringComparison.OrdinalIgnoreCase)) return;
                if (!BrowserUrl.IsAllowedScheme(uri)) { e.Cancel = true; ShowNotice("Blocked a link that is not a web address.", null); return; }
                t.Blank = false;
                t.ErrorTitle = null; t.ChangedError();
                t.Loading = true;
                t.Blocked = 0;
                lastUrl = uri;
                if (t == cur) { ApplyView(); StartProgress(); UpdateAddressUi(); }
            };
            c.SourceChanged += (o, e) =>
            {
                var src = c.Source ?? "";
                if (src.StartsWith("chrome-error://", StringComparison.OrdinalIgnoreCase)) return;
                t.Url = src == "about:blank" ? "" : src;
                if (t == cur) UpdateAddressUi();
            };
            c.DocumentTitleChanged += (o, e) => { var d = c.DocumentTitle ?? ""; t.RawTitle = d.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? "" : d; };
            c.HistoryChanged += (o, e) => { t.CanBack = c.CanGoBack; t.CanForward = c.CanGoForward; if (t == cur) UpdateButtons(); };
            c.FaviconChanged += async (o, e) => await LoadFavicon(t, c);
            c.NavigationCompleted += (o, e) =>
            {
                t.Loading = false;
                t.CanBack = c.CanGoBack; t.CanForward = c.CanGoForward;
                if (!e.IsSuccess && e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled && e.WebErrorStatus != CoreWebView2WebErrorStatus.Unknown)
                {
                    var d = Describe(e.WebErrorStatus, BrowserUrl.HostOf(t.Url));
                    t.ErrorTitle = d.Item1; t.ErrorText = d.Item2; t.ErrorUrl = t.Url; t.ChangedError();
                }
                if (t == cur) { ApplyView(); EndProgress(); UpdateButtons(); UpdateAddressUi(); }
            };
            c.ProcessFailed += (o, e) =>
            {
                if (e.ProcessFailedKind != CoreWebView2ProcessFailedKind.RenderProcessExited && e.ProcessFailedKind != CoreWebView2ProcessFailedKind.RenderProcessUnresponsive && e.ProcessFailedKind != CoreWebView2ProcessFailedKind.BrowserProcessExited) return;
                t.Loading = false;
                t.ErrorTitle = "This page stopped working"; t.ErrorText = "Something went wrong while showing it. Try again."; t.ErrorUrl = t.Url; t.ChangedError();
                if (t == cur) { ApplyView(); EndProgress(); }
            };
            // a window.open / target=_blank link opens as a new tab (only when you clicked something; pop-ups on their own are dropped)
            c.NewWindowRequested += (o, e) =>
            {
                e.Handled = true;
                if (e.IsUserInitiated && BrowserUrl.IsAllowedScheme(e.Uri)) AddTab(e.Uri, true);
            };
            c.WindowCloseRequested += (o, e) => CloseTab(t);
            c.ContainsFullScreenElementChanged += (o, e) => { t.Fullscreen = c.ContainsFullScreenElement; if (t == cur) ApplyView(); };

            // built-in ad / tracker blocklist: sub-requests to listed hosts are refused (the page you open yourself always loads)
            var env = c.Environment;
            c.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            c.WebResourceRequested += (o, e) =>
            {
                if (settings.BrowserAdsOff || e.ResourceContext == CoreWebView2WebResourceContext.Document) return;
                if (!AdBlock.IsBlockedUrl(e.Request.Uri)) return;
                e.Response = env.CreateWebResourceResponse(null, 403, "Blocked", "");
                t.Blocked++;
                if (t == cur) UpdateAddressTip();
            };

            c.DownloadStarting += (o, e) => OnDownload(t, e);
            // keys pressed while the page itself has the keyboard reach Page_PreviewKeyDown: the WPF WebView2 raises its accelerator keys as WPF key events
        }

        async Task LoadFavicon(BrowserTab t, CoreWebView2 c)
        {
            try
            {
                using (var st = await c.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png))
                {
                    if (st == null) { t.Icon = null; return; }
                    var ms = new MemoryStream(); st.CopyTo(ms); var bytes = ms.ToArray();
                    var bi = new BitmapImage();
                    bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.StreamSource = new MemoryStream(bytes); bi.EndInit(); bi.Freeze();
                    t.Icon = bi;
                    RememberIcon(BrowserUrl.HostOf(t.Url), bi, bytes);
                }
            }
            catch (Exception e) { Util.Log("browser favicon: " + e.Message); }
        }

        // ================================================================ view state

        // which of web view / new-tab page / error page is on screen, plus the bar around it
        void ApplyView()
        {
            if (unavailable || cur == null) return;
            foreach (var t in tabs) if (t.Web != null) t.Web.Visibility = t == cur && !t.Blank && !t.HasError && t.Web.CoreWebView2 != null ? Visibility.Visible : Visibility.Hidden;
            NewTabPanel.Visibility = cur.Blank ? Visibility.Visible : Visibility.Collapsed;
            ErrorPanel.Visibility = !cur.Blank && cur.HasError ? Visibility.Visible : Visibility.Collapsed;
            if (cur.HasError) { ErrTitle.Text = cur.ErrorTitle; ErrText.Text = cur.ErrorText; ErrUrl.Text = cur.ErrorUrl ?? ""; }
            Chrome.Visibility = cur.Fullscreen ? Visibility.Collapsed : Visibility.Visible;
            WebHost.Margin = new Thickness(0);
            UpdateButtons();
            UpdateAddressUi();
            if (cur.Loading) StartProgress(); else EndProgress();
            if (cur.Blank) RefreshHint();
        }

        void UpdateButtons()
        {
            if (cur == null) return;
            BackBtn.IsEnabled = cur.CanBack;
            FwdBtn.IsEnabled = cur.CanForward;
            ReloadBtn.IsEnabled = !cur.Blank;
            ReloadBtn.Content = cur.Loading ? "" : "";
            ReloadBtn.ToolTip = cur.Loading ? "Stop  (Esc)" : "Reload  (F5)";
        }

        void UpdateAddressUi()
        {
            if (cur == null) return;
            if (!AddressBox.IsKeyboardFocused) AddressBox.Text = cur.Blank ? "" : BrowserUrl.Display(cur.Url);
            var sec = cur.Blank ? PageSecurity.Internal : BrowserUrl.SecurityOf(cur.Url);
            LockGlyph.Text = sec == PageSecurity.Secure ? "" : sec == PageSecurity.NotSecure ? "" : "";   // lock / open lock / search
            LockGlyph.Foreground = (Brush)Application.Current.Resources[sec == PageSecurity.NotSecure ? "Brush.Warn" : "Brush.Text.Tertiary"];
            NotSecureText.Visibility = sec == PageSecurity.NotSecure ? Visibility.Visible : Visibility.Collapsed;
            AddressHint.Visibility = AddressBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateAddressTip();
        }

        void UpdateAddressTip()
        {
            if (cur == null) return;
            var sec = cur.Blank ? PageSecurity.Internal : BrowserUrl.SecurityOf(cur.Url);
            var first = sec == PageSecurity.Secure ? "Secure connection (https)" : sec == PageSecurity.NotSecure ? "Not secure: this page is sent without encryption (http)" : "Search or enter an address";
            AddressFrameBox.ToolTip = first + "\n" + BlockedText(settings.BrowserAdsOff, cur.Blocked);
        }

        public static string BlockedText(bool off, int n) => off ? "Ad and tracker blocking is off" : n == 0 ? "No ads or trackers blocked on this page" : n + (n == 1 ? " ad or tracker" : " ads and trackers") + " blocked on this page";

        void RefreshHint()
        {
            NtHint.Text = settings != null && settings.BrowserRemember
                ? "Logins are remembered on this PC. Passwords are never saved."
                : "Private browsing: everything is forgotten when the Outpost closes.";
        }

        // ================================================================ address bar

        void FocusAddress()
        {
            if (unavailable) return;
            AddressBox.Focus();
            Keyboard.Focus(AddressBox);
            AddressBox.SelectAll();
        }

        void Address_GotFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (cur == null) return;
            AddressBox.Text = cur.Blank ? "" : cur.Url;
            Dispatcher.BeginInvoke(new Action(() => AddressBox.SelectAll()), DispatcherPriority.Input);
        }

        void Address_LostFocus(object sender, KeyboardFocusChangedEventArgs e) { UpdateAddressUi(); }
        void Address_TextChanged(object sender, TextChangedEventArgs e) { AddressHint.Visibility = AddressBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; }

        void Address_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                var text = AddressBox.Text;
                e.Handled = true;
                if (cur == null) return;
                Navigate(cur, text);
                (cur.Web as FrameworkElement ?? PageArea).Focus();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                AddressBox.Text = cur == null || cur.Blank ? "" : cur.Url;
                (cur?.Web as FrameworkElement ?? PageArea).Focus();
                Keyboard.ClearFocus();
                UpdateAddressUi();
            }
        }

        // ================================================================ toolbar

        void Back_Click(object sender, RoutedEventArgs e) => GoBack();
        void Forward_Click(object sender, RoutedEventArgs e) => GoForward();
        void Reload_Click(object sender, RoutedEventArgs e) { if (cur != null && cur.Loading) StopLoading(); else Reload(); }

        void GoBack() { try { if (cur?.Web?.CoreWebView2 != null && cur.Web.CoreWebView2.CanGoBack) cur.Web.CoreWebView2.GoBack(); } catch { } }
        void GoForward() { try { if (cur?.Web?.CoreWebView2 != null && cur.Web.CoreWebView2.CanGoForward) cur.Web.CoreWebView2.GoForward(); } catch { } }
        void StopLoading() { try { cur?.Web?.CoreWebView2?.Stop(); } catch { } if (cur != null) { cur.Loading = false; ApplyView(); } }
        void Reload()
        {
            if (cur == null || cur.Blank) return;
            if (cur.HasError && cur.ErrorUrl != null) { Navigate(cur, cur.ErrorUrl); return; }
            try { cur.Web?.CoreWebView2?.Reload(); } catch { }
        }

        // ================================================================ keyboard

        void Page_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (HandleKey(key, Keyboard.Modifiers)) e.Handled = true;
        }

        // Ctrl+L / T / W / Tab, Alt+Left / Right, F5, Esc. Also reached from the web page's own key events.
        bool HandleKey(Key key, ModifierKeys mods)
        {
            if (unavailable || !IsVisible) return false;
            bool ctrl = (mods & ModifierKeys.Control) != 0, alt = (mods & ModifierKeys.Alt) != 0, shift = (mods & ModifierKeys.Shift) != 0;
            if (ctrl && !alt)
            {
                switch (key)
                {
                    case Key.L: Dispatcher.BeginInvoke(new Action(FocusAddress)); return true;
                    case Key.T: Dispatcher.BeginInvoke(new Action(NewTabAndFocus)); return true;
                    case Key.W: Dispatcher.BeginInvoke(new Action(() => CloseTab(cur))); return true;
                    case Key.R: Dispatcher.BeginInvoke(new Action(Reload)); return true;
                    case Key.Tab:
                    case Key.PageDown:
                    case Key.PageUp:
                        { bool back = key == Key.PageUp || (key == Key.Tab && shift); Dispatcher.BeginInvoke(new Action(() => CycleTab(back ? -1 : 1))); return true; }
                    case Key.D1: case Key.NumPad1: ShortcutTab?.Invoke("addons"); return true;
                    case Key.D2: case Key.NumPad2: ShortcutTab?.Invoke("lodge"); return true;
                    case Key.D3: case Key.NumPad3: ShortcutTab?.Invoke("inventory"); return true;
                    case Key.D4: case Key.NumPad4: return true;   // already here
                    case Key.OemComma: ShortcutTab?.Invoke("settings"); return true;
                }
                return false;
            }
            if (alt && !ctrl)
            {
                if (key == Key.Left) { Dispatcher.BeginInvoke(new Action(GoBack)); return true; }
                if (key == Key.Right) { Dispatcher.BeginInvoke(new Action(GoForward)); return true; }
                return false;
            }
            if (key == Key.F5) { Dispatcher.BeginInvoke(new Action(Reload)); return true; }
            if (key == Key.Escape && cur != null)
            {
                if (cur.Loading) { Dispatcher.BeginInvoke(new Action(StopLoading)); return true; }
                if (cur.Fullscreen) { try { cur.Web.CoreWebView2.ExecuteScriptAsync("document.exitFullscreen&&document.exitFullscreen()"); } catch { } return true; }
            }
            return false;
        }

        void CycleTab(int d)
        {
            if (tabs.Count < 2 || cur == null) return;
            SelectTab(tabs[(tabs.IndexOf(cur) + d + tabs.Count) % tabs.Count]);
        }

        // ================================================================ loading line

        void StartProgress()
        {
            int tok = ++progressToken;
            Progress.BeginAnimation(OpacityProperty, null);
            Progress.Opacity = 1;
            ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            if (!Motion.Enabled) { ProgressScale.ScaleX = 0.7; return; }
            ProgressScale.ScaleX = 0.05;
            // no real percentage exists: ease towards 85 % and wait for the page
            ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.05, 0.85, TimeSpan.FromSeconds(8)) { EasingFunction = Motion.EaseOut });
        }

        void EndProgress()
        {
            int tok = ++progressToken;
            if (cur != null && cur.Loading) return;
            ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            if (!Motion.Enabled) { ProgressScale.ScaleX = 0; Progress.Opacity = 0; return; }
            var grow = new DoubleAnimation(1, Motion.Fast) { EasingFunction = Motion.EaseOut };
            grow.Completed += (s, e) =>
            {
                if (tok != progressToken) return;
                var fade = new DoubleAnimation(0, Motion.Standard);
                fade.Completed += (s2, e2) => { if (tok == progressToken) { ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, null); ProgressScale.ScaleX = 0; } };
                Progress.BeginAnimation(OpacityProperty, fade);
            };
            ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        }

        // ================================================================ error page

        // a calm title + line for a failed navigation (pure apart from the enum, tested offline)
        public static Tuple<string, string> Describe(CoreWebView2WebErrorStatus st, string host)
        {
            var site = string.IsNullOrEmpty(host) ? "This page" : host;
            switch (st)
            {
                case CoreWebView2WebErrorStatus.HostNameNotResolved:
                    return Tuple.Create("Can't find " + site, "The address couldn't be found. Check the spelling, and that you're online.");
                case CoreWebView2WebErrorStatus.Disconnected:
                    return Tuple.Create("You seem to be offline", "Check your internet connection, then try again.");
                case CoreWebView2WebErrorStatus.CertificateCommonNameIsIncorrect:
                case CoreWebView2WebErrorStatus.CertificateExpired:
                case CoreWebView2WebErrorStatus.ClientCertificateContainsErrors:
                case CoreWebView2WebErrorStatus.CertificateRevoked:
                case CoreWebView2WebErrorStatus.CertificateIsInvalid:
                    return Tuple.Create("This connection isn't secure", site + "'s security certificate isn't valid, so the Outpost won't open it.");
                case CoreWebView2WebErrorStatus.Timeout:
                case CoreWebView2WebErrorStatus.ServerUnreachable:
                case CoreWebView2WebErrorStatus.ConnectionAborted:
                case CoreWebView2WebErrorStatus.ConnectionReset:
                case CoreWebView2WebErrorStatus.CannotConnect:
                    return Tuple.Create(site + " isn't responding", "The site didn't answer or refused the connection. Try again in a moment.");
                case CoreWebView2WebErrorStatus.RedirectFailed:
                    return Tuple.Create("Too many redirects", "The site keeps sending you around in circles.");
                default:
                    return Tuple.Create("This page couldn't load", "Something went wrong on the way. Try again.");
            }
        }

        void ErrRetry_Click(object sender, RoutedEventArgs e) => Reload();
        void ErrExternal_Click(object sender, RoutedEventArgs e) { if (cur != null) WebEngine.OpenExternal(cur.ErrorUrl ?? cur.Url); }

        // ================================================================ new-tab page tiles

        List<QuickLink> Links() => settings?.BrowserTiles ?? BrowserUrl.DefaultTiles();

        public int TileCount => TileList.Items.Count;
        public bool NewTabShown => NewTabPanel.Visibility == Visibility.Visible;
        public string TabTitleForTest(int i) => tabs[i].Title;

        void RebuildTiles()
        {
            TileList.ItemsSource = Links().Select(l => new TileVM { Name = l.Name, Url = l.Url, Icon = IconFor(BrowserUrl.HostOf(l.Url)) }).ToList();
            ResetTilesBtn.Visibility = settings?.BrowserTiles == null ? Visibility.Collapsed : Visibility.Visible;
        }

        void Tile_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is TileVM v && cur != null) Navigate(cur, v.Url);
        }

        void TileRemove_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.Tag is TileVM v)) return;
            var list = Links().Where(l => l.Url != v.Url).ToList();
            settings.BrowserTiles = list;
            SettingsStore.Save(settings);
            RebuildTiles();
        }

        void AddTile_Click(object sender, RoutedEventArgs e)
        {
            AddForm.Visibility = AddForm.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            AddError.Visibility = Visibility.Collapsed;
            if (AddForm.Visibility == Visibility.Visible) { AddName.Text = ""; AddUrl.Text = ""; AddName.Focus(); }
        }

        void AddCancel_Click(object sender, RoutedEventArgs e) => AddForm.Visibility = Visibility.Collapsed;

        void AddForm_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { e.Handled = true; AddOk_Click(null, null); }
            else if (e.Key == Key.Escape) { e.Handled = true; AddForm.Visibility = Visibility.Collapsed; }
        }

        void AddOk_Click(object sender, RoutedEventArgs e)
        {
            var url = BrowserUrl.TileUrl(AddUrl.Text);
            if (url == null) { AddError.Text = "That doesn't look like a web address."; AddError.Visibility = Visibility.Visible; return; }
            var name = AddName.Text.Trim();
            if (name.Length == 0) name = BrowserUrl.HostOf(url).Replace("www.", "");
            AddTileCore(name, url);
            AddForm.Visibility = Visibility.Collapsed;
        }

        void AddTileCore(string name, string url)
        {
            var list = Links().ToList();
            if (list.Any(l => string.Equals(l.Url, url, StringComparison.OrdinalIgnoreCase))) return;
            list.Add(new QuickLink { Name = name, Url = url });
            settings.BrowserTiles = list;
            SettingsStore.Save(settings);
            RebuildTiles();
        }

        void ResetTiles_Click(object sender, RoutedEventArgs e)
        {
            settings.BrowserTiles = null;
            SettingsStore.Save(settings);
            RebuildTiles();
        }

        // ---- tile icons: the site's own favicon, kept (per tile host only) after you visited it; until then a letter tile
        static string IconDir => Path.Combine(WebEngine.DataFolder, "browser-icons");

        ImageSource IconFor(string host) => host != null && icons.TryGetValue(IconKey(host), out var i) ? i : null;
        static string IconKey(string host) => (host ?? "").ToLowerInvariant().Replace("www.", "");

        void LoadIcons()
        {
            try
            {
                if (!Directory.Exists(IconDir)) return;
                foreach (var f in Directory.GetFiles(IconDir, "*.png"))
                    try
                    {
                        var bi = new BitmapImage();
                        bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.UriSource = new Uri(f); bi.EndInit(); bi.Freeze();
                        icons[Path.GetFileNameWithoutExtension(f)] = bi;
                    }
                    catch { }
            }
            catch { }
        }

        void RememberIcon(string host, ImageSource img, byte[] png)
        {
            var k = IconKey(host);
            if (k.Length == 0 || !Links().Any(l => IconKey(BrowserUrl.HostOf(l.Url)) == k)) return;
            icons[k] = img;
            try { Directory.CreateDirectory(IconDir); File.WriteAllBytes(Path.Combine(IconDir, k + ".png"), png); } catch { }
            RebuildTiles();
        }

        // ================================================================ downloads

        static string DownloadsFolder()
        {
            try
            {
                if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out var p) == 0 && p != IntPtr.Zero)
                {
                    var s = Marshal.PtrToStringUni(p); Marshal.FreeCoTaskMem(p);
                    if (!string.IsNullOrEmpty(s)) return s;
                }
            }
            catch { }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
        [DllImport("shell32.dll")] static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

        // Nothing is saved without a yes. One question at a time: a page that fires several downloads gets the first asked, the rest refused.
        void OnDownload(BrowserTab t, CoreWebView2DownloadStartingEventArgs e)
        {
            e.Handled = true;   // our own question, not the browser's download bubble
            if (downloadAsking) { e.Cancel = true; return; }
            var def = e.GetDeferral();
            downloadAsking = true;
            try
            {
                var name = BrowserUrl.SafeFileName(Path.GetFileName(e.ResultFilePath ?? ""));
                var host = BrowserUrl.HostOf(e.DownloadOperation.Uri);
                if (host.Length == 0) host = BrowserUrl.HostOf(t.Url);
                bool risky = BrowserUrl.IsRiskyFile(name);
                var text = name + "\nFrom " + (host.Length == 0 ? "this page" : host) + "\n\nIt will be saved in your Downloads folder."
                    + (risky ? "\n\nThis kind of file can run programs on your PC. Only keep it if you trust the site." : "");
                var owner = Window.GetWindow(this);
                bool yes = risky ? Dialog.ConfirmWarn(owner, "Download this file?", text, "Download") : Dialog.Confirm(owner, "Download this file?", text, "Download");
                if (!yes) { e.Cancel = true; return; }
                var dir = DownloadsFolder();
                Directory.CreateDirectory(dir);
                var path = BrowserUrl.UniquePath(dir, name);
                e.ResultFilePath = path;
                var op = e.DownloadOperation;
                op.StateChanged += (o, a) =>
                {
                    if (op.State == CoreWebView2DownloadState.Completed) ShowNotice("Saved " + Path.GetFileName(path) + " to Downloads.", path);
                    else if (op.State == CoreWebView2DownloadState.Interrupted) ShowNotice("The download of " + Path.GetFileName(path) + " didn't finish.", null);
                };
            }
            catch (Exception ex) { Util.Log("browser download: " + ex.Message); e.Cancel = true; }
            finally { downloadAsking = false; def.Complete(); }
        }

        void ShowNotice(string text, string path)
        {
            noticePath = path;
            NoticeText.Text = text;
            NoticeShow.Visibility = path == null ? Visibility.Collapsed : Visibility.Visible;
            Notice.Visibility = Visibility.Visible;
            noticeTimer.Stop(); noticeTimer.Start();
        }

        void NoticeClose_Click(object sender, RoutedEventArgs e) { noticeTimer.Stop(); Notice.Visibility = Visibility.Collapsed; }
        void NoticeShow_Click(object sender, RoutedEventArgs e)
        {
            try { if (noticePath != null && File.Exists(noticePath)) Process.Start("explorer.exe", "/select,\"" + noticePath + "\""); } catch { }
        }

        // ================================================================ selftest hooks

        public BrowserTab NewTabForTest() => AddTab(null, true);
        public void CloseTabForTest(BrowserTab t) => CloseTab(t);
        public void SelectTabForTest(BrowserTab t) => SelectTab(t);
        public bool KeyForTest(Key k, ModifierKeys m) => HandleKey(k, m);
        public void AddTileForTest(string name, string url) => AddTileCore(name, url);
        public string AddressTextForTest { get => AddressBox.Text; set => AddressBox.Text = value; }
        public bool AddressFocusedForTest => AddressBox.IsKeyboardFocused;
        public bool FallbackShownForTest => Fallback.Visibility == Visibility.Visible;
        public string LockGlyphForTest => LockGlyph.Text;
        public string AddressTipForTest => AddressFrameBox.ToolTip as string;
        public void NavigateForTest(string input) { if (cur != null) Navigate(cur, input); }
        // a page from a folder, served from a virtual https host that exists only inside this web view (no network): lets the selftest watch the blocklist work
        public async Task<bool> LoadFolderPageForTest(string folder, string file)
        {
            NavigateForTest("about:blank");
            for (int i = 0; i < 100 && cur?.Web?.CoreWebView2 == null; i++) await Task.Delay(100);
            var c = cur?.Web?.CoreWebView2;
            if (c == null) return false;
            c.SetVirtualHostNameToFolderMapping("outpost-test.local", folder, CoreWebView2HostResourceAccessKind.Deny);
            cur.Blocked = 0;
            Navigate(cur, "https://outpost-test.local/" + file);
            for (int i = 0; i < 100 && cur.Loading; i++) await Task.Delay(100);
            await Task.Delay(500);
            return true;
        }
        public void ResetTabsForTest() => ResetTabs();

        // design shot: the page is a separate window, so capture it and let the caller paste it in
        public async Task<byte[]> CapturePageForTest(Visual relativeTo, Action<Rect> bounds)
        {
            var w = cur?.Web;
            if (w?.CoreWebView2 == null) return null;
            bounds(w.TransformToAncestor(relativeTo).TransformBounds(new Rect(0, 0, w.ActualWidth, w.ActualHeight)));
            using (var ms = new MemoryStream())
            {
                await w.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
                return ms.ToArray();
            }
        }
    }
}
