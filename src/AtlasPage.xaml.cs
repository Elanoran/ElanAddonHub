using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Maps;

namespace ElansAddonHub
{
    // The Atlas page (rail item 4, Ctrl+4): one search box, and a page for every id (item, NPC, object, quest, zone, instance, spell, skill, faction).
    // AtlasQuery builds the page models on a worker thread; the page only draws them (AtlasPage.View.cs), keeps the history (back / forward) and the
    // start page with the data status, the Classic baseline card and the Lodge (AtlasPage.Start.cs, AtlasPage.Classic.cs). Every reference is a link.
    public partial class AtlasPage : UserControl
    {
        readonly AtlasHistory history = new AtlasHistory();
        Func<string> wowRoot = () => null;
        AtlasPageModel model;
        int buildGen;
        bool started;
        Task lastBuild = Task.CompletedTask;
        public AtlasHistory History => history;
        public bool BackEnabledForTest => BackButton.IsEnabled;
        public AtlasPageModel Model => model;
        public string CurrentId => history.Current ?? "";
        public bool IsStart => string.IsNullOrEmpty(history.Current);
        public bool IsData => AtlasNav.IsPage(history.Current);

        public AtlasPage()
        {
            InitializeComponent();
            InitUpdates();
            SizeChanged += (s, e) => Layout();
            copiedTimer.Tick += (s, e) => { copiedTimer.Stop(); CopiedText.Visibility = Visibility.Collapsed; };
            Scroller.SizeChanged += (s, e) => Layout();
            PreviewKeyDown += Page_PreviewKeyDown;
            PreviewMouseUp += Page_PreviewMouseUp;
            PreviewMouseDown += Page_PreviewMouseDown;
            Loaded += (s, e) => { Subscribe(); if (model == null && !started) { started = true; _ = GoStart(false); } };
            IsVisibleChanged += (s, e) =>
            {
                if (!IsVisible || !started) return;
                if (Signature() != drawnSig) DataChanged();
            };
            Unloaded += (s, e) => { Unsubscribe(); FlushNav(); };
            InitShell();
            UpdateNav();
        }

        // wowRoot: the WoW folder (for the user's own characters: who has the item). The Lodge pool and the Classic setup are looked up through the AtlasQuery context.
        public void Init(Func<string> root)
        {
            wowRoot = root ?? (() => null);
            AtlasQuery.Context.Owned = OwnedItems;
            AtlasQuery.Context.Characters = Characters;
        }

        // ---- the user's own characters (item id -> holdings), read off the UI thread when an item page asks, kept for half a minute
        Dictionary<int, InvItemTotals> ownedCache; DateTime ownedAt;
        readonly object ownedGate = new object();
        public Func<Dictionary<int, InvItemTotals>> OwnedOverrideForTest;

        Dictionary<int, InvItemTotals> OwnedItems()
        {
            if (OwnedOverrideForTest != null) return OwnedOverrideForTest();
            lock (ownedGate)
            {
                if (ownedCache != null && (DateTime.UtcNow - ownedAt).TotalSeconds < 30) return ownedCache;
                try
                {
                    var root = wowRoot();
                    if (string.IsNullOrEmpty(root)) return null;
                    var data = CharacterMerge.Merge(HubData.Read(root), InventoryReader.ReadData(root));
                    ownedCache = InventoryReader.BuildIndex(data.Chars.Where(c => !c.Hidden), data.Guilds);
                    ownedAt = DateTime.UtcNow;
                    return ownedCache;
                }
                catch (Exception e) { Util.Log("atlas: own characters: " + e.Message); return null; }
            }
        }
        public void ForgetOwned() { lock (ownedGate) { ownedCache = null; charsCache = null; } }

        // the user's characters (for the browse lists' "usable by"), read with the holdings and kept as long as they are
        List<InvChar> charsCache;
        public Func<List<InvChar>> CharsOverrideForTest;
        List<InvChar> Characters()
        {
            if (CharsOverrideForTest != null) return CharsOverrideForTest();
            lock (ownedGate)
            {
                if (charsCache != null && (DateTime.UtcNow - ownedAt).TotalSeconds < 30) return charsCache;
                try
                {
                    var root = wowRoot();
                    if (string.IsNullOrEmpty(root)) return null;
                    var data = CharacterMerge.Merge(HubData.Read(root), InventoryReader.ReadData(root));
                    charsCache = data.Chars.Where(c => !c.Hidden).ToList();
                    return charsCache;
                }
                catch (Exception e) { Util.Log("atlas: characters: " + e.Message); return null; }
            }
        }

        // ---- navigation
        public event Action<string> Navigated;         // the id now shown ("" = the start page)

        // Opens an Atlas id ("item:123") or, when it is not an id, searches for the text. Returns when the page is drawn.
        public Task Open(string idOrText)
        {
            var id = AtlasNav.Upgrade(AtlasQuery.NormalizeId(idOrText) ?? idOrText);      // an old link to a list that moved opens where it lives now
            if (AtlasCategories.IsRouted(id)) return Show(id, true);           // a browse page or a hosted category
            if (!AtlasIds.TryParse(id, out _, out _)) return OpenPalette(idOrText ?? "");
            return Show(id, true);
        }

        public Task GoStart(bool record = true) => Show("", record);

        Task Show(string id, bool record)
        {
            ClosePalette();
            started = true;
            // the page already shown, asked for on another tab (a link with ?tab=): the same page - only the tab changes, nothing is built again and the history has no new step
            var cur = history.Current ?? "";
            if (record && id.Length > 0 && cur != id && AtlasTabs.SamePage(cur, id))
            {
                var tab = AtlasTabs.TabOf(id);
                if (model != null && !model.NotFound && model.Id == AtlasTabs.BaseId(id) && entityGrid != null && hosted == null && browse == null)
                {
                    if (tab.Length == 0) tab = tabDefault;
                    if (!SelectTab(tab, true)) { history.Replace(id); NavState.Follow(cur, id); UpdateNav(); SyncIdToTab(false); }
                    return lastBuild;
                }
                history.Replace(id); NavState.Follow(cur, id);          // still being built: it opens on the tab asked for
                UpdateNav(); Navigated?.Invoke(id);
                return lastBuild = BuildAndDraw(id);
            }
            if (record) { history.Go(id); NavState.Visit(id); }
            UpdateNav();
            Navigated?.Invoke(id);
            return lastBuild = BuildAndDraw(id);
        }

        public Task Back() { var id = history.Back(); if (id != null) NavState.Visit(id); UpdateNav(); if (id == null) return Task.CompletedTask; Navigated?.Invoke(id); return lastBuild = BuildAndDraw(id); }
        public Task Forward() { var id = history.Forward(); if (id != null) NavState.Visit(id); UpdateNav(); if (id == null) return Task.CompletedTask; Navigated?.Invoke(id); return lastBuild = BuildAndDraw(id); }
        public Task WaitForTest() => lastBuild;

        void UpdateNav()
        {
            BackButton.IsEnabled = history.CanBack;
            ForwardButton.IsEnabled = history.CanForward;
            HomeButton.IsEnabled = !IsStart;
            UpdateLink();
            UpdateShell();
            BackButton.Opacity = history.CanBack ? 1 : 0.4; ForwardButton.Opacity = history.CanForward ? 1 : 0.4; HomeButton.Opacity = IsStart ? 0.4 : 1;
        }

        // ---- the page's own link: shown in the header with a Copy button; the id is what the search box and the Lodge accept (item:2589, browse:items/weapons?q=3, talents:tree/hunter)
        public Action<string> ClipboardSeamForTest;
        public string LastCopiedForTest { get; private set; }
        public string LinkShown => LinkRow.Visibility == Visibility.Visible ? LinkText.Text : "";
        public Button CopyLinkButtonForTest => CopyLinkButton;
        public bool CopiedShownForTest => CopiedText.Visibility == Visibility.Visible;
        readonly DispatcherTimer copiedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };

        void UpdateLink()
        {
            var id = history.Current ?? "";
            bool has = id.Length > 0;
            LinkRow.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            if (LinkText.Text != id) CopiedText.Visibility = Visibility.Collapsed;
            LinkText.Text = id; LinkText.ToolTip = id;
            CopyLinkButton.ToolTip = "Copy link - paste it in Lodge chat or the Atlas search";
        }

        void CopyLink_Click(object sender, RoutedEventArgs e) => CopyLink();

        public bool CopyLink()
        {
            var id = history.Current ?? "";
            if (id.Length == 0) return false;
            bool ok = true;
            try { if (ClipboardSeamForTest != null) ClipboardSeamForTest(id); else Clipboard.SetText(id); }
            catch (Exception)
            {
                try { System.Threading.Thread.Sleep(80); if (ClipboardSeamForTest != null) ClipboardSeamForTest(id); else Clipboard.SetText(id); }
                catch (Exception) { ok = false; }
            }
            CopiedText.Text = ok ? "Link copied" : "Could not reach the clipboard";
            CopiedText.SetResourceReference(TextBlock.ForegroundProperty, ok ? "Brush.Success" : "Brush.Danger");
            CopiedText.Visibility = Visibility.Visible;
            copiedTimer.Stop(); copiedTimer.Start();
            if (ok) LastCopiedForTest = id;
            return ok;
        }


        void Back_Click(object sender, RoutedEventArgs e) { _ = Back(); }
        void Forward_Click(object sender, RoutedEventArgs e) { _ = Forward(); }
        void Home_Click(object sender, RoutedEventArgs e) { _ = GoStart(); }

        // hovering a table row: the game tooltip of what the row links to (an item, creature, quest, spell ...), built when it opens
        void RowTip_Opened(object sender, RoutedEventArgs e)
        {
            if (!(sender is ToolTip tt) || !(tt.DataContext is AtlasRow r) || string.IsNullOrEmpty(r.Link)) return;
            tt.Content = RowTipFor(r);
        }

        public GameTooltip RowTipFor(AtlasRow r)
        {
            var kind = r.Link.IndexOf(':') > 0 ? r.Link.Substring(0, r.Link.IndexOf(':')) : "";
            GameTooltip gt = null;
            try
            {
                if (kind == "item" && int.TryParse(r.Link.Substring(5), out var id))
                {
                    var ci = ElansAddonHub.Services.Db2.ItemData.Get(id);
                    gt = new GameTooltip(r.Title, ci?.Quality ?? r.Quality).SetIcon(ci?.IconId ?? r.IconId);     // a row has only a small icon: the hover shows it larger, beside the panel
                    if (ci != null) gt.AddItem(ci); else gt.AddText("Not in the game data on this PC", "grey");
                }
                else if (kind == "npc" || kind == "obj" || kind == "quest" || kind == "spell" || kind == "skill" || kind == "faction" || kind == "zone" || kind == "instance" || kind == "class" || kind == "race")
                {
                    var m = ElansAddonHub.Services.Atlas.AtlasQuery.Build(r.Link);
                    if (m != null && !m.NotFound) { gt = GameTooltip.FromAtlas(m); if (m.Kind == "spell" || m.Kind == "skill") gt.SetIcon(m.IconId); }
                }
            }
            catch (Exception ex) { Util.Log("atlas row tooltip: " + ex.Message); }
            if (gt == null) { gt = new GameTooltip(r.Title, r.Quality); if (!string.IsNullOrEmpty(r.Detail)) gt.AddText(r.Detail, "grey"); }
            return gt;
        }

        void Row_Click(object sender, MouseButtonEventArgs e)
        {
            if (!(sender is FrameworkElement fe) || !(fe.Tag is string link) || string.IsNullOrEmpty(link)) return;
            e.Handled = true;
            _ = Open(link);
        }

        public ModifierKeys? ModifiersForTest;                  // self-test: the modifier keys held (the real keyboard cannot be pressed from a test)
        void Page_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var mods = ModifiersForTest ?? Keyboard.Modifiers;
            if (mods == ModifierKeys.Alt && (e.Key == Key.Left || e.SystemKey == Key.Left)) { e.Handled = true; _ = Back(); }
            else if (mods == ModifierKeys.Alt && (e.Key == Key.Right || e.SystemKey == Key.Right)) { e.Handled = true; _ = Forward(); }
            else if (mods == ModifierKeys.Control && (e.Key == Key.F || e.Key == Key.K)) { e.Handled = true; if (PaletteOpen) { PaletteBox.Focus(); PaletteBox.SelectAll(); } else _ = OpenPalette(); }
            else if (e.Key == Key.Oem2 && mods == ModifierKeys.None && !(Keyboard.FocusedElement is TextBox)) { e.Handled = true; _ = OpenPalette(); }   // "/" like the web
            else if (e.Key == Key.Escape && PaletteOpen) { e.Handled = true; ClosePalette(); }
        }

        void Page_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.XButton1) { e.Handled = true; _ = Back(); }
            else if (e.ChangedButton == MouseButton.XButton2) { e.Handled = true; _ = Forward(); }
        }

        void Page_PreviewMouseDown(object sender, MouseButtonEventArgs e) { }

        static bool IsInside(DependencyObject d, DependencyObject root)
        {
            for (; d != null; d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
                if (ReferenceEquals(d, root)) return true;
            return false;
        }

        public void Scroller_ScrollToForTest(FrameworkElement el) { UpdateLayout(); el?.BringIntoView(); }
        public void FocusSearch() { _ = OpenPalette(); }

        // ---- build off the UI thread, draw on it
        async Task BuildAndDraw(string id)
        {
            int gen = ++buildGen;
            ForgetUpdateOffer();
            if (hosted != null && !AtlasCategories.IsHosted(id)) LeaveHosted();
            if (string.IsNullOrEmpty(id)) { DrawStart(); return; }
            if (AtlasNav.IsPage(id)) { DrawData(); return; }
            if (AtlasCategories.TryHosted(id, out var hostCat, out var hostSub)) { DrawHosted(hostCat, hostSub); return; }
            if (AtlasBrowse.IsBrowseId(id)) { await BuildAndDrawBrowse(id, gen); return; }
            var build = Task.Run(() => AtlasQuery.Build(id));
            var first = await Task.WhenAny(build, Task.Delay(150));
            if (gen != buildGen) return;
            if (first != build) ShowSkeleton(id);
            AtlasPageModel m;
            try { m = await build; }
            catch (Exception e) { Util.Log("atlas build " + id + ": " + e); m = new AtlasPageModel { Id = id, NotFound = true, Title = id, Message = "This page could not be built." }; }
            if (gen != buildGen) return;
            Draw(m);
        }

        async Task BuildAndDrawBrowse(string id, int gen)
        {
            var build = Task.Run(() => AtlasBrowse.Run(id));
            var first = await Task.WhenAny(build, Task.Delay(150));
            if (gen != buildGen) return;
            if (first != build) ShowSkeleton(id);
            BrowseResult r;
            try { r = await build; }
            catch (Exception e) { Util.Log("atlas browse " + id + ": " + e); r = new BrowseResult { Id = id, NotFound = true, Title = "Browse", Message = "This list could not be built." }; }
            if (gen != buildGen) return;
            DrawBrowse(r);
        }

        // ---- layers that change while the page is open: the start page and the page shown are redrawn (debounced)
        readonly DispatcherTimer refresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        bool subscribed;
        void Subscribe()
        {
            if (subscribed) return;
            subscribed = true;
            refresh.Tick -= RefreshTick; refresh.Tick += RefreshTick;
            ItemData.Changed += OnDataChanged; SpellData.Changed += OnDataChanged; MapData.Changed += OnDataChanged; SeenData.Changed += OnDataChanged;
            ZoneLevels.Changed += OnDataChanged; ClassData.Changed += OnDataChanged;
        }
        void Unsubscribe()
        {
            if (!subscribed) return;
            subscribed = false;
            ItemData.Changed -= OnDataChanged; SpellData.Changed -= OnDataChanged; MapData.Changed -= OnDataChanged; SeenData.Changed -= OnDataChanged;
            ZoneLevels.Changed -= OnDataChanged; ClassData.Changed -= OnDataChanged;
            refresh.Stop();
        }
        // at most 600 ms after the first change (not after the last: while the readers finish one after the other the changes kept pushing the redraw out until all were done)
        void OnDataChanged() { Dispatcher.BeginInvoke(new Action(() => { if (!refresh.IsEnabled) refresh.Start(); })); }
        void RefreshTick(object s, EventArgs e)
        {
            refresh.Stop();
            if (!IsVisible) return;
            if (IsStart) DrawStart();
            else if (IsData) DrawData();
            // a page that had nothing yet (or zones whose levels were on their way) is drawn again; a page with content is not redrawn under the reader:
            // when the new data would change it, the notice offers it (AtlasPage.Updates.cs)
            else if (browse != null) { if (browse.NotFound || browse.LevelsPending) _ = Redraw(); else _ = OfferUpdate(); }
            else if (model != null && (model.NotFound || !model.Layers.Contains("client"))) _ = Redraw();   // it waited for the game data
            else if (model != null) _ = OfferUpdate();
        }
        public Task Redraw() => lastBuild = BuildAndDraw(history.Current ?? "");

        // what the page was drawn from: when it is shown again and any of it changed (the game data finished loading, the Lodge pool got a page, the Classic baseline arrived), it is drawn again;
        // otherwise coming back to the tab leaves the page, its scroll position and its map as they were
        string drawnSig = "";
        public string Signature()
        {
            var L = AtlasQuery.Context.Lodge?.Invoke();
            return string.Join("|", ItemData.Count, ItemData.Status, SpellData.SpellCount, MapData.MapCount, SeenData.NpcCount + SeenData.QuestCount, Services.Atlas.Classic.ClassicAtlas.IsReady, L == null ? "-" : L.Rev + "," + L.Updated + "," + L.FindCount, ClassicSetup?.State);
        }

        // the page itself asks to be shown again after the Lodge pool or the Classic data changed
        // an answer of "who in the Lodge can craft this" arrived: a recipe (spell) or item page is drawn again to show it
        public void CraftersChanged()
        {
            if (!IsVisible || hosted != null || IsStart || IsData || browse != null) return;
            var id = history.Current ?? "";
            if (id.StartsWith("spell:", StringComparison.Ordinal) || id.StartsWith("item:", StringComparison.Ordinal)) _ = Redraw();
        }

        // the Lodge pool got a page while the start page is open: its cards (pool, recent finds) are drawn again when what they show changed
        public void PoolChanged()
        {
            if (IsVisible && started && hosted == null && browse != null) { UpdateFindsStrip(); return; }
            if (!IsVisible || !started || hosted != null || !(IsStart || IsData)) return;
            if (Signature() != drawnSig) { if (IsData) DrawData(); else DrawStart(); }
        }

        public void DataChanged() { if (hosted != null) return; if (IsStart) DrawStart(); else if (IsData) DrawData(); else _ = Redraw(); }
    }
}
