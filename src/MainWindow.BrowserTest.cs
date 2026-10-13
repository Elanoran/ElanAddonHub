using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // The Browser page, offline: address / search rules, scheme blocklist, ad-host matcher, settings defaults, chat link detector (all pure),
    // then the UI (page opens, tabs, tiles, Settings switches). Nothing here touches the network; the only browser started is about:blank.
    // ELANSHUB_BROWSER_SHOTS=<dir> (with --selftest) instead runs against the real wowhead.com and saves browser-newtab.png / browser-wowhead.png there.
    public partial class MainWindow
    {
        async Task<string> BrowserTest(string dir)
        {
            var shots = Environment.GetEnvironmentVariable("ELANSHUB_BROWSER_SHOTS");
            if (!string.IsNullOrEmpty(shots)) return await BrowserShots(shots);
            var notes = new List<string>();
            bool ok = true;
            void Check(string what, bool cond) { notes.Add((cond ? "ok   " : "FAIL ") + what); ok &= cond; }
            string R(string input, string engine = null) => BrowserUrl.Resolve(input, engine);
            var oldTiles = settings.BrowserTiles; var oldRemember = settings.BrowserRemember; var oldAdsOff = settings.BrowserAdsOff;
            var oldChat = settings.BrowserFromChat; var oldSearch = settings.BrowserSearch;
            try
            {
                // ---- typed text -> address or search
                Check("address: bare domain gets https", R("wowhead.com") == "https://wowhead.com/");
                Check("address: domain with a path", R("wowhead.com/classic") == "https://wowhead.com/classic");
                Check("address: full URL kept", R("https://www.wowhead.com/classic") == "https://www.wowhead.com/classic");
                Check("address: http stays http", R("http://example.com/a?b=1") == "http://example.com/a?b=1");
                Check("address: surrounding spaces ignored", R("  wowhead.com  ") == "https://wowhead.com/");
                Check("address: localhost and IPs use http", R("localhost:3000") == "http://localhost:3000/" && R("192.168.0.1/admin") == "http://192.168.0.1/admin" && R("[::1]:8080") == "http://[::1]:8080/");
                Check("address: domain with a port", R("example.org:8443/x") == "https://example.org:8443/x");
                Check("search: words go to DuckDuckGo by default", R("hunter pet taming") == "https://duckduckgo.com/?q=hunter%20pet%20taming");
                Check("search: one word that is no domain", R("wowhead") == "https://duckduckgo.com/?q=wowhead");
                Check("search: a sentence that contains a dot", R("what is 1.5 times 2") == "https://duckduckgo.com/?q=what%20is%201.5%20times%202" && R("e.g.").StartsWith("https://duckduckgo.com/"));
                Check("search: special characters are escaped", R("a&b=c#d") == "https://duckduckgo.com/?q=a%26b%3Dc%23d");
                Check("search: engine is a choice (google, brave), unknown id = default",
                    R("tbc hunter", "google") == "https://www.google.com/search?q=tbc%20hunter" && R("x y", "brave").StartsWith("https://search.brave.com/") && R("x y", "nope").StartsWith("https://duckduckgo.com/"));
                Check("search: every engine has a name and a {0} url", BrowserUrl.Engines.All(e => !string.IsNullOrEmpty(e.Name) && e.Url.Contains("{0}") && e.Url.StartsWith("https://")) && BrowserUrl.Engines[0].Id == BrowserUrl.DefaultEngine);
                Check("scheme: javascript: / file: / data: / mailto: typed in the bar are searched, never loaded",
                    R("javascript:alert(1)").StartsWith("https://duckduckgo.com/") && R("file:///c:/windows/win.ini").StartsWith("https://duckduckgo.com/")
                    && R("data:text/html,<b>x</b>").StartsWith("https://duckduckgo.com/") && R("mailto:a@b.com").StartsWith("https://duckduckgo.com/") && R("edge://settings").StartsWith("https://duckduckgo.com/"));
                Check("address: nothing typed = nothing to do", R("") == null && R("   ") == null && R(null) == null);
                Check("address: about:blank passes", R("about:blank") == "about:blank");

                // ---- which schemes may load
                Check("allow: http, https, about:blank", BrowserUrl.IsAllowedScheme("http://a.com/") && BrowserUrl.IsAllowedScheme("https://a.com/x") && BrowserUrl.IsAllowedScheme("HTTPS://A.COM") && BrowserUrl.IsAllowedScheme("about:blank"));
                Check("block: javascript, file, data, blob, ftp, view-source, edge/chrome, custom app schemes",
                    new[] { "javascript:alert(1)", "file:///c:/x", "data:text/html,x", "blob:https://a.com/1", "ftp://a.com/f", "view-source:https://a.com", "edge://settings", "chrome://flags", "steam://run/1", "ms-settings:", "mailto:a@b.com", "about:srcdoc", "https://", "", null }
                        .All(u => !BrowserUrl.IsAllowedScheme(u)));
                Check("security: https secure, http not, blank internal", BrowserUrl.SecurityOf("https://a.com") == PageSecurity.Secure && BrowserUrl.SecurityOf("http://a.com") == PageSecurity.NotSecure
                    && BrowserUrl.SecurityOf("about:blank") == PageSecurity.Internal && BrowserUrl.SecurityOf("") == PageSecurity.Internal);
                Check("display: no scheme, no lone slash", BrowserUrl.Display("https://www.wowhead.com/") == "www.wowhead.com" && BrowserUrl.Display("https://a.com/x/y?z=1") == "a.com/x/y?z=1" && BrowserUrl.Display("about:blank") == "");
                Check("letter tile", BrowserUrl.Initial("Wowhead", "") == "W" && BrowserUrl.Initial("", "https://www.example.com") == "E" && BrowserUrl.Initial("  ", "") == "?");
                Check("tile address: domain accepted, search text / javascript refused", BrowserUrl.TileUrl("wowhead.com") == "https://wowhead.com/" && BrowserUrl.TileUrl("hunter pets") == null
                    && BrowserUrl.TileUrl("wowhead") == null && BrowserUrl.TileUrl("javascript:alert(1)") == null && BrowserUrl.TileUrl("") == null);

                // ---- ad / tracker hosts
                Check("ads: listed host and its subdomains", AdBlock.IsBlockedHost("doubleclick.net") && AdBlock.IsBlockedHost("ads.doubleclick.net") && AdBlock.IsBlockedHost("a.b.c.doubleclick.net")
                    && AdBlock.IsBlockedHost("DoubleClick.NET") && AdBlock.IsBlockedHost("doubleclick.net."));
                Check("ads: look-alikes are NOT matched", !AdBlock.IsBlockedHost("notdoubleclick.net") && !AdBlock.IsBlockedHost("doubleclick.net.evil.com") && !AdBlock.IsBlockedHost("doubleclick-net.com")
                    && !AdBlock.IsBlockedHost("xdoubleclick.net") && !AdBlock.IsBlockedHost("evil.com") && !AdBlock.IsBlockedHost("criteo.com.au") && !AdBlock.IsBlockedHost("mycriteo.com"));
                Check("ads: normal sites are not blocked", !AdBlock.IsBlockedHost("www.wowhead.com") && !AdBlock.IsBlockedHost("wowhead.com") && !AdBlock.IsBlockedHost("github.com")
                    && !AdBlock.IsBlockedHost("duckduckgo.com") && !AdBlock.IsBlockedHost("google.com") && !AdBlock.IsBlockedHost("youtube.com") && !AdBlock.IsBlockedHost("wow-petopia.com"));
                Check("ads: bare TLD, empty, null", !AdBlock.IsBlockedHost("net") && !AdBlock.IsBlockedHost("") && !AdBlock.IsBlockedHost(null) && !AdBlock.IsBlockedHost("."));
                Check("ads: by URL, host only (user-info and path tricks do not fool it)", AdBlock.IsBlockedUrl("https://securepubads.g.doubleclick.net/tag/js/gpt.js") && !AdBlock.IsBlockedUrl("https://wowhead.com/?u=doubleclick.net")
                    && !AdBlock.IsBlockedUrl("https://doubleclick.net@wowhead.com/") && !AdBlock.IsBlockedUrl("https://wowhead.com/doubleclick.net/x") && !AdBlock.IsBlockedUrl("data:text/plain,doubleclick.net") && !AdBlock.IsBlockedUrl("junk"));
                Check("ads: the list is a real list", AdBlock.Count >= 40);
                Check("ads: tooltip text", BrowserPage.BlockedText(false, 0).StartsWith("No ads") && BrowserPage.BlockedText(false, 1) == "1 ad or tracker blocked on this page"
                    && BrowserPage.BlockedText(false, 12) == "12 ads and trackers blocked on this page" && BrowserPage.BlockedText(true, 5).Contains("off"));

                // ---- settings defaults
                var fresh = new Settings();
                Check("settings: private browsing is the default (remember = off)", !fresh.BrowserRemember);
                Check("settings: ad blocking on, chat links in the default browser, DuckDuckGo, default tiles", !fresh.BrowserAdsOff && !fresh.BrowserFromChat && fresh.BrowserTiles == null
                    && BrowserUrl.EngineById(fresh.BrowserSearch).Id == "duckduckgo");
                var tiles = BrowserUrl.DefaultTiles();
                Check("tiles: defaults are Wowhead and the talent calculator (+ the two Forever sites)", tiles.Count >= 2 && tiles[0].Url == "https://www.wowhead.com/classic" && tiles[1].Url.Contains("wowhead.com") && tiles[1].Url.Contains("talent")
                    && tiles.All(t => BrowserUrl.IsAllowedScheme(t.Url) && !string.IsNullOrEmpty(t.Name)));
                var round = Util.FromJson<Settings>(Util.ToJson(new Settings { BrowserSearch = "brave", BrowserRemember = true, BrowserAdsOff = true, BrowserFromChat = true, BrowserTiles = new List<QuickLink> { new QuickLink { Name = "A", Url = "https://a.com/" } } }));
                Check("settings: they survive a save / load", round.BrowserSearch == "brave" && round.BrowserRemember && round.BrowserAdsOff && round.BrowserFromChat && round.BrowserTiles.Count == 1 && round.BrowserTiles[0].Name == "A");

                // ---- chat links
                Check("chat: http and https found anywhere", ChatLinks.All("see https://www.wowhead.com/item=19019, and http://example.com/a.").SequenceEqual(new[] { "https://www.wowhead.com/item=19019", "http://example.com/a" }));
                Check("chat: javascript:, data:, ftp:, file:, mailto: are never links", !ChatLinks.All("javascript:alert(1) data:text/html,x ftp://a.com/f file:///c:/x mailto:a@b.com").Any());
                Check("chat: scheme in capitals, no scheme, bare host", ChatLinks.All("HTTPS://A.COM/X").Count() == 1 && !ChatLinks.All("www.wowhead.com wowhead.com/x").Any());
                Check("chat: brackets and trailing punctuation", ChatLinks.All("(see https://en.wikipedia.org/wiki/Foo_(bar))!").Single() == "https://en.wikipedia.org/wiki/Foo_(bar)"
                    && ChatLinks.All("<https://a.com/x>").Single() == "https://a.com/x" && ChatLinks.All("really? https://a.com/x?!").Single() == "https://a.com/x");
                Check("chat: scheme only / no host is nothing", !ChatLinks.All("https:// and http://").Any() && !ChatLinks.All("").Any() && !ChatLinks.All(null).Any());
                const string line = "hi https://a.com/x and https://youtu.be/dQw4w9WgXcQ ok";
                Check("chat: the link under a character index", ChatLinks.At(line, 5) == "https://a.com/x" && ChatLinks.At(line, 0) == null && ChatLinks.At(line, 24) == "https://youtu.be/dQw4w9WgXcQ" && ChatLinks.At(line, 18) == null && ChatLinks.At(line, -1) == null);

                // ---- downloads (names from websites)
                Check("download: name made safe", BrowserUrl.SafeFileName("..\\..\\evil.exe") == "evil.exe" && BrowserUrl.SafeFileName("a:b*c?.txt") == "a_b_c_.txt" && BrowserUrl.SafeFileName("") == "download"
                    && BrowserUrl.SafeFileName("CON.txt").StartsWith("download") && BrowserUrl.SafeFileName("report.pdf. ") == "report.pdf" && BrowserUrl.SafeFileName(new string('x', 300) + ".zip").Length <= 124);
                Check("download: programs and scripts are flagged", BrowserUrl.IsRiskyFile("setup.exe") && BrowserUrl.IsRiskyFile("X.PS1") && BrowserUrl.IsRiskyFile("a.jar") && !BrowserUrl.IsRiskyFile("guide.pdf") && !BrowserUrl.IsRiskyFile("pic.png"));
                var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\D\a.pdf", @"C:\D\a (2).pdf" };
                Check("download: never overwrites (a (3).pdf)", BrowserUrl.UniquePath(@"C:\D", "a.pdf", taken.Contains) == @"C:\D\a (3).pdf" && BrowserUrl.UniquePath(@"C:\D", "b.pdf", taken.Contains) == @"C:\D\b.pdf");

                // ---- error page text, shortcuts, engine paths
                Check("error page: DNS failure and offline each get their own calm text", BrowserPage.Describe(CoreWebView2WebErrorStatus.HostNameNotResolved, "nope.example").Item1 == "Can't find nope.example"
                    && BrowserPage.Describe(CoreWebView2WebErrorStatus.Disconnected, "x.com").Item1.Contains("offline")
                    && BrowserPage.Describe(CoreWebView2WebErrorStatus.CertificateExpired, "x.com").Item1.Contains("isn't secure") && BrowserPage.Describe(CoreWebView2WebErrorStatus.Timeout, "").Item1.StartsWith("This page"));
                Check("shortcut: Ctrl+4 is the Browser page, Ctrl+5 the Atlas, Ctrl+6 Talents (1-3 unchanged)", TabForKey(Key.D4) == "browser" && TabForKey(Key.NumPad4) == "browser" && TabForKey(Key.D1) == "addons" && TabForKey(Key.D3) == "inventory" && TabForKey(Key.D5) == "atlas" && TabForKey(Key.D6) == "talents" && TabForKey(Key.D7) == null);
                Check("engine: player and browser keep separate profiles under the data folder", WebEngine.UserDataFolder(WebProfile.Browser) == Path.Combine(WebEngine.DataFolder, "browser")
                    && WebEngine.UserDataFolder(WebProfile.Player) == WebEngine.DataFolder && WebEngine.DataFolder.StartsWith(Util.DataDir));

                // ---- UI: the page opens
                ShowTab("addons");
                Check("ui: the rail has a Browser button", TabBrowser != null && TabBrowser.ToolTip.ToString().Contains("Ctrl+4"));
                ShowTab("browser");
                await Task.Delay(500);
                Check("ui: Browser page opens, the others close", BrowserView.Visibility == Visibility.Visible && AddonsPage.Visibility == Visibility.Collapsed && LodgePage.Visibility == Visibility.Collapsed
                    && SettingsPage.Visibility == Visibility.Collapsed && InventoryView.Visibility == Visibility.Collapsed && RefreshBar.Visibility == Visibility.Collapsed);
                bool engine = WebEngine.RuntimeAvailable(out var why);
                if (!engine) notes.Add("note WebView2 runtime not available here: " + why);
                Check("ui: one tab, the new-tab page with the default tiles", BrowserView.TabCount == 1 && BrowserView.NewTabShown && BrowserView.TileCount == tiles.Count && BrowserView.TabTitleForTest(0) == "New tab");
                Snapshot(Path.Combine(dir, "20-browser-newtab.png"));

                // tiles: add, remove, restore
                BrowserView.AddTileForTest("Example", "https://example.com/");
                Check("tiles: adding one stores it in settings", BrowserView.TileCount == tiles.Count + 1 && settings.BrowserTiles.Any(t => t.Url == "https://example.com/"));
                BrowserView.AddTileForTest("Example again", "https://example.com/");
                Check("tiles: the same address is not added twice", BrowserView.TileCount == tiles.Count + 1);
                var tilePanel = (System.Windows.Controls.ItemsControl)BrowserView.FindName("TileList");
                BrowserView.UpdateLayout();
                var rm = FindDescendants<System.Windows.Controls.Button>(tilePanel).FirstOrDefault(b => b.ToolTip as string == "Remove tile");
                Check("tiles: every tile has a remove button", rm != null);
                rm?.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Check("tiles: removing the first one", BrowserView.TileCount == tiles.Count && settings.BrowserTiles.All(t => t.Url != tiles[0].Url));
                ((System.Windows.Controls.Button)BrowserView.FindName("ResetTilesBtn")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Check("tiles: restore defaults", BrowserView.TileCount == tiles.Count && settings.BrowserTiles == null);

                // tabs
                var t2 = BrowserView.NewTabForTest();
                Check("tabs: a new tab is added and selected", BrowserView.TabCount == 2 && BrowserView.Current == t2 && t2.Selected && BrowserView.NewTabShown);
                var t3 = BrowserView.NewTabForTest();
                BrowserView.CloseTabForTest(t3);
                Check("tabs: closing the selected one selects its neighbour", BrowserView.TabCount == 2 && BrowserView.Current == t2);
                BrowserView.KeyForTest(Key.T, ModifierKeys.Control); await Task.Delay(200);
                Check("keys: Ctrl+T adds a tab", BrowserView.TabCount == 3);
                BrowserView.KeyForTest(Key.Tab, ModifierKeys.Control); await Task.Delay(200);
                Check("keys: Ctrl+Tab goes to the next tab (wraps)", BrowserView.Current != null && BrowserView.TabCount == 3 && BrowserView.Current.Selected);
                BrowserView.KeyForTest(Key.W, ModifierKeys.Control); await Task.Delay(200);
                Check("keys: Ctrl+W closes the tab", BrowserView.TabCount == 2);
                Check("keys: Alt+Left, F5, Esc on a blank tab do nothing bad", BrowserView.KeyForTest(Key.Left, ModifierKeys.Alt) && BrowserView.KeyForTest(Key.F5, ModifierKeys.None) && !BrowserView.KeyForTest(Key.Escape, ModifierKeys.None));
                var last = BrowserView.Current;
                BrowserView.CloseTabForTest(BrowserView.Current); BrowserView.CloseTabForTest(BrowserView.Current);
                Check("tabs: closing the last tab leaves a fresh new tab", BrowserView.TabCount == 1 && BrowserView.NewTabShown && BrowserView.Current != last);
                Check("address bar: empty on a new tab, search glyph, nothing blocked", BrowserView.AddressTextForTest == "" && BrowserView.LockGlyphForTest == "\uE721" && BrowserView.AddressTipForTest.Contains("No ads"));

                // the engine really starts (about:blank needs no network) and the tab owns a web view, which closing disposes
                if (engine)
                {
                    BrowserView.NavigateForTest("about:blank");
                    for (int i = 0; i < 100 && !(BrowserView.Current?.Web?.CoreWebView2 != null && !BrowserView.Current.Loading); i++) await Task.Delay(100);
                    var web = BrowserView.Current?.Web;
                    Check("engine: a tab creates its own WebView2 (InPrivate by default) and loads about:blank", web?.CoreWebView2 != null && BrowserView.Current.InPrivate && !BrowserView.NewTabShown);
                    if (web?.CoreWebView2 != null)
                    {
                        var s = web.CoreWebView2.Settings;
                        Check("hardening: dev tools, host objects, web messages, password autosave, autofill all off", !s.AreDevToolsEnabled && !s.AreHostObjectsAllowed && !s.IsWebMessageEnabled && !s.IsPasswordAutosaveEnabled && !s.IsGeneralAutofillEnabled);
                        Check("hardening: tracking prevention is Strict", web.CoreWebView2.Profile.PreferredTrackingPreventionLevel == CoreWebView2TrackingPreventionLevel.Strict);
                        // informational: does Ctrl+T typed while the PAGE has the keyboard reach our shortcut handler? (needs the test window in front)
                        try
                        {
                            Activate(); web.Focus(); await Task.Delay(400);
                            int before = BrowserView.TabCount;
                            keybd_event(0x11, 0, 0, UIntPtr.Zero); keybd_event(0x54, 0, 0, UIntPtr.Zero); keybd_event(0x54, 0, 2, UIntPtr.Zero); keybd_event(0x11, 0, 2, UIntPtr.Zero);
                            await Task.Delay(700);
                            notes.Add("note keys typed inside the page reach Ctrl+T: " + (BrowserView.TabCount > before ? "yes" : "no (tabs " + before + " -> " + BrowserView.TabCount + ", window active=" + IsActive + ")"));
                            while (BrowserView.TabCount > before) BrowserView.CloseTabForTest(BrowserView.Current);
                            BrowserView.SelectTabForTest(BrowserView.Current);
                        }
                        catch (Exception ke) { notes.Add("note key probe: " + ke.Message); }
                        string hit = null; web.CoreWebView2.NavigationStarting += (o, e) => hit = e.Uri;
                        web.CoreWebView2.Navigate("data:text/html,<b>x</b>");
                        await Task.Delay(800);
                        Check("scheme: a data: navigation is cancelled before it loads", hit != null && hit.StartsWith("data:") && web.CoreWebView2.Source == "about:blank");
                    }
                    // the blocklist at work: a local page asks for ad / tracker hosts (refused before any network) and for one normal host
                    var site = Path.Combine(dir, "adsite"); Directory.CreateDirectory(site);
                    File.WriteAllText(Path.Combine(site, "ads.html"), "<!doctype html><title>ads test</title><body><img src=\"https://ads.doubleclick.net/a.gif\">"
                        + "<script src=\"https://pagead2.googlesyndication.com/x.js\"></script><img src=\"https://hotjar.com/p.gif\"><img src=\"https://cdn.blockme.example/p.gif\"><img src=\"https://cdn.blockme.example/q.gif\"><img src=\"https://nothing-listed.example/p.gif\"></body>");
                    settings.BrowserAdsOff = false; AdBlock.ExtraHostForTest = "blockme.example";
                    bool served = await BrowserView.LoadFolderPageForTest(site, "ads.html");
                    notes.Add("note blocked=" + BrowserView.Current.Blocked + " url=" + BrowserView.Current.Url + " tip=" + BrowserView.AddressTipForTest.Replace((char)10, (char)47));
                    Check("ads: a page asking for listed hosts and others gets the listed ones refused (at least our 2 test-host images), counted in the tooltip", served && BrowserView.Current.Blocked >= 2 && BrowserView.Current.Blocked <= 5 && BrowserView.AddressTipForTest.Contains(BrowserView.Current.Blocked + " ads and trackers blocked"));
                    settings.BrowserAdsOff = true;
                    await BrowserView.LoadFolderPageForTest(site, "ads.html");
                    Check("ads: with the setting off nothing is refused", BrowserView.Current.Blocked == 0 && BrowserView.AddressTipForTest.Contains("off"));
                    settings.BrowserAdsOff = false;
                    BrowserView.CloseTabForTest(BrowserView.Current);
                    Check("tabs: closing a tab disposes its web view", BrowserView.TabCount == 1 && BrowserView.Current.Web == null);
                }

                // chat link -> Browser page (about:blank stands in for a site: no network)
                ShowTab("lodge");
                settings.BrowserFromChat = true;
                OpenChatLinkForTest("about:blank");
                await Task.Delay(300);
                Check("chat links on: a link switches to the Browser page and opens in it", BrowserView.Visibility == Visibility.Visible && LodgePage.Visibility == Visibility.Collapsed && !BrowserView.NewTabShown);
                BrowserView.ResetTabsForTest();
                settings.BrowserFromChat = false;

                // ---- Settings > Privacy > Outpost browser
                ShowTab("settings"); SettingsPage.Show("privacy");
                await Task.Delay(300);
                Snapshot(Path.Combine(dir, "20-browser-privacy.png"));
                bool On(string n) => ((System.Windows.Controls.CheckBox)SettingsPage.FindName(n)).IsChecked == true;
                Check("settings page: private browsing on, ad blocking on, chat links off, DuckDuckGo shown", On("PvBrPrivate") && On("PvBrAds") && !On("PvBrChat")
                    && ((System.Windows.Controls.ComboBox)SettingsPage.FindName("PvBrSearch")).SelectedIndex == 0);
                SettingsPage.ToggleForTest("PvBrPrivate");
                Check("settings page: 'Remember logins' is stored and resets the tabs", settings.BrowserRemember && BrowserView.TabCount == 1);
                SettingsPage.ToggleForTest("PvBrPrivate");
                SettingsPage.ToggleForTest("PvBrAds");
                SettingsPage.ToggleForTest("PvBrChat");
                Check("settings page: ads off and chat links on are stored", settings.BrowserAdsOff && settings.BrowserFromChat && !settings.BrowserRemember);
                SettingsPage.ToggleForTest("PvBrAds"); SettingsPage.ToggleForTest("PvBrChat");
                ShowTab("addons");
            }
            catch (Exception e) { ok = false; notes.Add("crashed: " + e); }
            finally
            {
                AdBlock.ExtraHostForTest = null; settings.BrowserTiles = oldTiles; settings.BrowserRemember = oldRemember; settings.BrowserAdsOff = oldAdsOff; settings.BrowserFromChat = oldChat; settings.BrowserSearch = oldSearch;
                try { BrowserView.ResetTabsForTest(); } catch { }
            }
            return "browser selftest: " + (ok ? "PASSED" : "FAILED") + "\r\n  " + string.Join("\r\n  ", notes);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

        void OpenChatLinkForTest(string url) => OpenChatLink(url);

        static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null) yield break;
            int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var c = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (c is T t) yield return t;
                foreach (var d in FindDescendants<T>(c)) yield return d;
            }
        }

        // real network, real WebView2: wowhead in a tab, then the new-tab page (screen capture: the web view is its own window)
        async Task<string> BrowserShots(string outDir)
        {
            try
            {
                Directory.CreateDirectory(outDir);
                var oldTiles = settings.BrowserTiles;
                ShowTab("browser");
                await Task.Delay(800);
                BrowserView.NavigateForTest("https://www.wowhead.com/classic");
                for (int i = 0; i < 150 && !(BrowserView.Current?.Web?.CoreWebView2 != null && !BrowserView.Current.Loading && i > 10); i++) await Task.Delay(200);
                await Task.Delay(4000);
                var shot = Path.Combine(outDir, "browser-wowhead.png");
                Snapshot(shot);
                Rect pr = Rect.Empty;
                var png = await BrowserView.CapturePageForTest(this, r => pr = r);
                if (png != null) PastePage(shot, png, pr);
                var info = $"title='{BrowserView.Current?.Title}' url='{BrowserView.Current?.Url}' blocked={BrowserView.Current?.Blocked} tip='{BrowserView.AddressTipForTest}' glyph={BrowserView.LockGlyphForTest}";
                BrowserView.NewTabForTest();
                await Task.Delay(1200);
                Snapshot(Path.Combine(outDir, "browser-newtab.png"));
                settings.BrowserTiles = oldTiles;
                return "browser shots: " + info;
            }
            catch (Exception e) { return "browser shots crashed: " + e; }
        }

        void PastePage(string shot, byte[] png, Rect pr)
        {
            var dev = PresentationSource.FromVisual(this).CompositionTarget.TransformToDevice;
            var tmp = shot + ".tmp";
            using (var baseImg = new System.Drawing.Bitmap(shot)) using (var web = new System.Drawing.Bitmap(new MemoryStream(png)))
            {
                using (var g = System.Drawing.Graphics.FromImage(baseImg))
                    g.DrawImage(web, (float)(pr.X * dev.M11), (float)(pr.Y * dev.M22), (float)(pr.Width * dev.M11), (float)(pr.Height * dev.M22));
                baseImg.Save(tmp, System.Drawing.Imaging.ImageFormat.Png);
            }
            File.Delete(shot); File.Move(tmp, shot);
        }
    }
}
