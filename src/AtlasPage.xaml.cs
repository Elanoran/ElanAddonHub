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
    // The Atlas page (rail item 5, Ctrl+5): one search box, and a page for every id (item, NPC, object, quest, zone, instance, spell, skill, faction).
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

        public AtlasPage()
        {
            InitializeComponent();
            SizeChanged += (s, e) => Layout();
            Scroller.SizeChanged += (s, e) => Layout();
            PreviewKeyDown += Page_PreviewKeyDown;
            PreviewMouseUp += Page_PreviewMouseUp;
            PreviewMouseDown += Page_PreviewMouseDown;
            Loaded += (s, e) => { Subscribe(); if (model == null && !started) { started = true; _ = GoStart(false); } };
            IsVisibleChanged += (s, e) => { if (IsVisible && started && Signature() != drawnSig) DataChanged(); };
            Unloaded += (s, e) => Unsubscribe();
            UpdateNav();
        }

        // wowRoot: the WoW folder (for the user's own characters: who has the item). The Lodge pool and the Classic setup are looked up through the AtlasQuery context.
        public void Init(Func<string> root)
        {
            wowRoot = root ?? (() => null);
            AtlasQuery.Context.Owned = OwnedItems;
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
        public void ForgetOwned() { lock (ownedGate) { ownedCache = null; } }

        // ---- navigation
        public event Action<string> Navigated;         // the id now shown ("" = the start page)

        // Opens an Atlas id ("item:123") or, when it is not an id, searches for the text. Returns when the page is drawn.
        public Task Open(string idOrText)
        {
            var id = AtlasQuery.NormalizeId(idOrText) ?? idOrText;
            if (!AtlasIds.TryParse(id, out _, out _)) { SearchBox.Text = idOrText ?? ""; return RunSearchNow(); }
            return Show(id, true);
        }

        public Task GoStart(bool record = true) => Show("", record);

        Task Show(string id, bool record)
        {
            CloseResults();
            if (record) history.Go(id);
            UpdateNav();
            Navigated?.Invoke(id);
            return lastBuild = BuildAndDraw(id);
        }

        public Task Back() { var id = history.Back(); UpdateNav(); if (id == null) return Task.CompletedTask; Navigated?.Invoke(id); return lastBuild = BuildAndDraw(id); }
        public Task Forward() { var id = history.Forward(); UpdateNav(); if (id == null) return Task.CompletedTask; Navigated?.Invoke(id); return lastBuild = BuildAndDraw(id); }
        public Task WaitForTest() => lastBuild;

        void UpdateNav()
        {
            BackButton.IsEnabled = history.CanBack;
            ForwardButton.IsEnabled = history.CanForward;
            HomeButton.IsEnabled = !IsStart;
            BackButton.Opacity = history.CanBack ? 1 : 0.4; ForwardButton.Opacity = history.CanForward ? 1 : 0.4; HomeButton.Opacity = IsStart ? 0.4 : 1;
        }

        void Back_Click(object sender, RoutedEventArgs e) { _ = Back(); }
        void Forward_Click(object sender, RoutedEventArgs e) { _ = Forward(); }
        void Home_Click(object sender, RoutedEventArgs e) { _ = GoStart(); }

        void Row_Click(object sender, MouseButtonEventArgs e)
        {
            if (!(sender is FrameworkElement fe) || !(fe.Tag is string link) || string.IsNullOrEmpty(link)) return;
            e.Handled = true;
            _ = Open(link);
        }

        void Page_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var mods = Keyboard.Modifiers;
            if (mods == ModifierKeys.Alt && (e.Key == Key.Left || e.SystemKey == Key.Left)) { e.Handled = true; _ = Back(); }
            else if (mods == ModifierKeys.Alt && (e.Key == Key.Right || e.SystemKey == Key.Right)) { e.Handled = true; _ = Forward(); }
            else if (mods == ModifierKeys.Control && (e.Key == Key.F || e.Key == Key.K)) { e.Handled = true; FocusSearch(); }
            else if (e.Key == Key.Oem2 && mods == ModifierKeys.None && !(Keyboard.FocusedElement is TextBox)) { e.Handled = true; FocusSearch(); }   // "/" like the web
        }

        void Page_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.XButton1) { e.Handled = true; _ = Back(); }
            else if (e.ChangedButton == MouseButton.XButton2) { e.Handled = true; _ = Forward(); }
        }

        void Page_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (ResultsPanel.Visibility != Visibility.Visible) return;
            if (e.OriginalSource is DependencyObject d && (IsInside(d, ResultsPanel) || IsInside(d, SearchBox))) return;
            CloseResults();
        }

        static bool IsInside(DependencyObject d, DependencyObject root)
        {
            for (; d != null; d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
                if (ReferenceEquals(d, root)) return true;
            return false;
        }

        public void FocusSearch() { SearchBox.Focus(); SearchBox.SelectAll(); }

        // ---- build off the UI thread, draw on it
        async Task BuildAndDraw(string id)
        {
            int gen = ++buildGen;
            if (string.IsNullOrEmpty(id)) { DrawStart(); return; }
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

        // ---- layers that change while the page is open: the start page and the page shown are redrawn (debounced)
        readonly DispatcherTimer refresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        bool subscribed;
        void Subscribe()
        {
            if (subscribed) return;
            subscribed = true;
            refresh.Tick -= RefreshTick; refresh.Tick += RefreshTick;
            ItemData.Changed += OnDataChanged; SpellData.Changed += OnDataChanged; MapData.Changed += OnDataChanged; SeenData.Changed += OnDataChanged;
        }
        void Unsubscribe()
        {
            if (!subscribed) return;
            subscribed = false;
            ItemData.Changed -= OnDataChanged; SpellData.Changed -= OnDataChanged; MapData.Changed -= OnDataChanged; SeenData.Changed -= OnDataChanged;
            refresh.Stop();
        }
        void OnDataChanged() { Dispatcher.BeginInvoke(new Action(() => { refresh.Stop(); refresh.Start(); })); }
        void RefreshTick(object s, EventArgs e)
        {
            refresh.Stop();
            if (!IsVisible) return;
            if (IsStart) DrawStart();
            else if (model != null && (model.NotFound || model.Notes.Count > 0 || !model.Layers.Contains("client"))) _ = Redraw();
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
        public void DataChanged() { if (IsStart) DrawStart(); else _ = Redraw(); }
    }
}
