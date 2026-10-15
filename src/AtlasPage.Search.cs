using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Db2;

namespace ElansAddonHub
{
    // The Ctrl+K palette (also opened by the header search box, the Home hero, "/" and a name given to Open): a card over the whole Atlas page with the search box, scope chips
    // (All, Items, Creatures, Quests, Zones, Spells, Pages), grouped results, and a preview of the selected one. Empty, it lists Pinned, Recent and the Pages. The search runs on a
    // worker (AtlasPaletteSearch) with a cancellation token: a newer keystroke cancels the older search, and a late answer is dropped.
    // A WebView2 (the world map) is always drawn above every WPF element, so while the palette is open over the map the live view is swapped for a snapshot of it
    // (WorldMapView.FreezeAsync) and swapped back when the palette closes.
    public partial class AtlasPage
    {
        sealed class PalEntry { public string Id = "", Kind = ""; public AtlasHit Hit; public ContentControl Row; }

        readonly List<PalEntry> pal = new List<PalEntry>();
        readonly Dictionary<string, RadioButton> palChips = new Dictionary<string, RadioButton>();
        readonly DispatcherTimer palDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        readonly DispatcherTimer palPreviewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        string palScope = "all";
        int palSel = -1, palGen, palPreviewGen;
        bool palOpen, palSuppress, palWired;
        CancellationTokenSource palCts;
        AtlasPaletteSearch.Result palResult;
        Task palSearch = Task.CompletedTask, palOpening = Task.CompletedTask;
        WorldMapView frozenMap;
        PalEntry palPreviewFor;

        public bool PaletteOpen => palOpen && PaletteLayer.Visibility == Visibility.Visible;
        public string PaletteScopeForTest => palScope;
        public int PaletteSelectedForTest => palSel;
        public AtlasPaletteSearch.Result PaletteResultForTest => palResult;
        public string PaletteTextForTest { get => PaletteBox.Text; set => PaletteBox.Text = value; }
        public TextBox PaletteBoxForTest => PaletteBox;

        public IReadOnlyList<string> PaletteIdsForTest => pal.Select(p => p.Id).ToList();
        public IReadOnlyList<string> PaletteScopesForTest => palChips.Keys.ToList();
        public RadioButton PaletteChipForTest(string scope) => palChips.TryGetValue(scope, out var c) ? c : null;
        public int PaletteDebounceMsForTest { get => (int)palDebounce.Interval.TotalMilliseconds; set => palDebounce.Interval = TimeSpan.FromMilliseconds(value); }
        public Task PaletteWaitForTest() => palSearch;
        public Task PaletteOpenWaitForTest() => palOpening;
        public FrameworkElement PalettePreviewForTest => PalettePreview;
        public FrameworkElement PaletteLayerForTest => PaletteLayer;
        public FrameworkElement PaletteStackForTest => PaletteStack;
        public FrameworkElement PaletteDimForTest => PaletteDim;
        public FrameworkElement PaletteRowForTest(int i) => pal[i].Row;
        public void PalSelectForTest(int i) => PalSelect(i);
        public Action PaletteSearchHookForTest;                  // self-test: runs on the search thread before the search (to make it slow and to see which thread it is)
        public FrameworkElement PaletteCardForTest => PaletteCard;
        public string PaletteHeadingsForTest => string.Join("|", AtlasWalk(PaletteStack).OfType<TextBlock>().Where(t => t.Tag as string == "head").Select(t => t.Text));
        public IReadOnlyList<string> PaletteTextsForTest => AtlasTexts(PaletteStack);
        public IReadOnlyList<string> PalettePreviewTextsForTest => AtlasTexts(PalettePreview);
        public bool PreviewShownForTest => PalettePreviewHost.Visibility == Visibility.Visible;
        public bool MapFrozenForTest => frozenMap != null && frozenMap.FrozenForTest;
        // the text of a TextBlock whose text is made of Runs (TextBlock.Text stays empty then)
        public static string TextOf(TextBlock tb) => string.IsNullOrEmpty(tb.Text) && tb.Inlines.Count > 0 ? string.Concat(tb.Inlines.OfType<Run>().Select(r => r.Text)) : tb.Text;

        static List<string> AtlasTexts(DependencyObject root)
        {
            var l = new List<string>();
            foreach (var d in AtlasWalk(root))
            {
                if (d is TextBlock tb && tb.Visibility == Visibility.Visible && TextOf(tb).Length > 0) l.Add(TextOf(tb));
                else if (d is Button b && b.Content is string s && s.Length > 0) l.Add(s);
            }
            return l;
        }

        // ---- the palette is a layer over the CONTENT of the window, not over the Atlas page: the main window gives it a host (UseOverlayHost), so Ctrl+K and the search box in the
        // title bar open it over any page (Addons, Lodge, Characters, Settings, the Atlas). Choosing a result switches to the Atlas page (ShowAtlas) and opens it there.
        Panel overlayHost;
        IInputElement palPrevFocus;
        public event Action PaletteOpened, PaletteClosed;
        public Action ShowAtlas;                                  // the main window: switch the rail to the Atlas page
        public Panel OverlayHostForTest => overlayHost;

        public void UseOverlayHost(Panel host)
        {
            if (host == null || overlayHost == host) return;
            overlayHost = host;
            (PaletteLayer.Parent as Panel)?.Children.Remove(PaletteLayer);
            PaletteLayer.Margin = new Thickness(0);
            Grid.SetColumnSpan(PaletteLayer, 1);
            PaletteDim.CornerRadius = new CornerRadius(0, 0, 12, 0);
            host.Children.Add(PaletteLayer);
            host.SizeChanged += (s, e) => { if (palOpen) ApplyPaletteSize(); };
        }

        void WirePalette()
        {
            if (palWired) return;
            palWired = true;
            palDebounce.Tick += (s, e) => { palDebounce.Stop(); palSearch = RunPaletteNow(); };
            PaletteLayer.PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape && palOpen) { e.Handled = true; ClosePalette(); } };
            palPreviewTimer.Tick += (s, e) => { palPreviewTimer.Stop(); _ = PreviewBuildAsync(palPreviewFor, palPreviewGen); };
            PaletteCard.SizeChanged += (s, e) => ApplyPaletteSize();
            SizeChanged += (s, e) => { if (palOpen) ApplyPaletteSize(); };
            PaletteBox.PreviewMouseDown += (s, e) => { };
        }

        void OnMapPaletteRequested() => Dispatcher.BeginInvoke(new Action(() => { if (IsVisible && hosted is WorldMapView) _ = OpenPalette(); }));

        // ---- open / close
        public Task OpenPalette(string seed = "", string scope = null) => palOpening = OpenPaletteAsync(seed ?? "", scope);

        async Task OpenPaletteAsync(string seed, string scope)
        {
            WirePalette();
            if (palOpen)
            {
                SetPaletteText(seed.Length > 0 ? seed : PaletteBox.Text, scope);
                PaletteBox.Focus(); PaletteBox.SelectAll();
                return;
            }
            palOpen = true;
            palScope = "all";                // a fresh open starts with every kind (the scope is not remembered)
            palPrevFocus = Keyboard.FocusedElement;
            try { PaletteOpened?.Invoke(); } catch (Exception e) { Util.Log("atlas palette: opened: " + e.Message); }       // the page behind may hold a web view (the Browser): it steps aside
            // over the world map: swap the live web view for its snapshot first, or the palette would be hidden behind it
            if (IsVisible && hosted is WorldMapView wm)
            {
                frozenMap = wm;
                try { await wm.FreezeAsync(); } catch (Exception e) { Util.Log("atlas palette: map snapshot: " + e.Message); }
            }
            if (!palOpen) { frozenMap?.Thaw(); frozenMap = null; return; }      // closed while the snapshot was taken
            BuildChips();
            if (overlayHost != null) overlayHost.Visibility = Visibility.Visible;
            PaletteLayer.Visibility = Visibility.Visible;
            ApplyPaletteSize();
            if (Motion.Enabled)
            {
                Motion.DialogIn(PaletteCard);
                PaletteDim.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 0.7, Motion.Standard));
            }
            SetPaletteText(seed, scope);
            PaletteBox.Focus();
            PaletteBox.CaretIndex = PaletteBox.Text.Length;
        }

        public void ClosePalette() => ClosePalette(true);

        public void ClosePalette(bool restoreFocus)
        {
            if (!palOpen) return;
            palOpen = false;
            palCts?.Cancel(); palDebounce.Stop(); palPreviewTimer.Stop(); palGen++; palPreviewGen++;
            PaletteLayer.Visibility = Visibility.Collapsed;
            if (overlayHost != null) overlayHost.Visibility = Visibility.Collapsed;
            if (frozenMap != null) { frozenMap.Thaw(); frozenMap = null; }
            try { PaletteClosed?.Invoke(); } catch (Exception e) { Util.Log("atlas palette: closed: " + e.Message); }
            if (!restoreFocus) return;
            // the focus goes back to where it was (not to the title bar box: it would open the palette again); on the Atlas page that is the page
            if (IsVisible) { if (hosted != null) HostContent.Focus(); else Scroller.Focus(); }
            else if (palPrevFocus is UIElement pf && pf.IsVisible && pf.Focusable && !(pf is TextBox tb && tb.IsReadOnly && tb.Tag as string == "Search  (Ctrl K)")) pf.Focus();
            palPrevFocus = null;
        }

        void PaletteDim_Down(object sender, MouseButtonEventArgs e) { e.Handled = true; ClosePalette(); }

        void ApplyPaletteSize()
        {
            double pageW = overlayHost != null && overlayHost.ActualWidth > 0 ? overlayHost.ActualWidth : ActualWidth;
            double pageH = overlayHost != null && overlayHost.ActualHeight > 0 ? overlayHost.ActualHeight : ActualHeight;
            double w = PaletteCard.ActualWidth > 0 ? PaletteCard.ActualWidth : pageW;
            bool preview = w >= 620;
            PalettePreviewCol.Width = new GridLength(preview ? 300 : 0);
            PalettePreviewHost.Visibility = preview ? Visibility.Visible : Visibility.Collapsed;
            double h = Math.Max(160, Math.Min(360, pageH - 260));
            PaletteBody.Height = h;
        }

        // ---- scope chips
        void BuildChips()
        {
            if (palChips.Count > 0) { RefreshChips(); return; }
            foreach (var sc in AtlasPaletteSearch.Scopes)
            {
                var rb = new RadioButton { Content = AtlasPaletteSearch.ScopeTitle(sc), GroupName = "atlas-palette-scope", Style = (Style)FindResource("Segment"), Tag = sc, Focusable = false };
                System.Windows.Automation.AutomationProperties.SetName(rb, "Search " + AtlasPaletteSearch.ScopeTitle(sc).ToLowerInvariant());
                var scope = sc;
                rb.Checked += (s, e) => { if (palSuppress) return; palScope = scope; palSearch = RunPaletteNow(); PaletteBox.Focus(); };
                palChips[sc] = rb;
                PaletteScopes.Children.Add(rb);
            }
            RefreshChips();
        }

        void RefreshChips()
        {
            bool was = palSuppress; palSuppress = true;
            foreach (var kv in palChips) kv.Value.IsChecked = kv.Key == palScope;
            palSuppress = was;
        }

        public void CycleScope(int dir)
        {
            int i = Array.IndexOf(AtlasPaletteSearch.Scopes, palScope);
            palScope = AtlasPaletteSearch.Scopes[((i + dir) % AtlasPaletteSearch.Scopes.Length + AtlasPaletteSearch.Scopes.Length) % AtlasPaletteSearch.Scopes.Length];
            RefreshChips();
            palSearch = RunPaletteNow();
        }

        // ---- the text
        void SetPaletteText(string text, string scope)
        {
            palSuppress = true;
            if (scope != null && Array.IndexOf(AtlasPaletteSearch.Scopes, scope) >= 0) palScope = scope;
            string t = text ?? "";
            if (AtlasPaletteSearch.ParsePrefix(t, out var sc, out var rest)) { palScope = sc; t = rest; }
            PaletteBox.Text = t;
            RefreshChips();
            palSuppress = false;
            palSearch = RunPaletteNow();
        }

        void PaletteBox_Changed(object sender, TextChangedEventArgs e)
        {
            WirePalette();
            if (palSuppress || !palOpen) return;
            // "i:" "n:" "z:" "q:" "s:" "p:" pick a scope
            if (AtlasPaletteSearch.ParsePrefix(PaletteBox.Text, out var sc, out var rest))
            {
                palSuppress = true; palScope = sc; PaletteBox.Text = rest; PaletteBox.CaretIndex = rest.Length; RefreshChips(); palSuppress = false;
            }
            palDebounce.Stop();
            if (string.IsNullOrWhiteSpace(PaletteBox.Text)) { palSearch = RunPaletteNow(); return; }
            palDebounce.Start();
        }

        void PaletteBox_KeyDown(object sender, KeyEventArgs e)
        {
            bool shift = (e.KeyboardDevice.Modifiers & ModifierKeys.Shift) != 0;
            switch (e.Key)
            {
                case Key.Down: e.Handled = true; PalSelect(palSel + 1); break;
                case Key.Up: e.Handled = true; PalSelect(palSel - 1); break;
                case Key.PageDown: e.Handled = true; PalSelect(Math.Min(pal.Count - 1, palSel + 5)); break;
                case Key.PageUp: e.Handled = true; PalSelect(Math.Max(0, palSel - 5)); break;
                case Key.Tab: e.Handled = true; CycleScope(shift ? -1 : 1); break;
                case Key.Escape: e.Handled = true; ClosePalette(); break;
                case Key.Enter: e.Handled = true; palSearch = EnterPalette(); break;
            }
        }

        // Enter before the answer arrived: search now, then open the best hit
        async Task EnterPalette()
        {
            if (palDebounce.IsEnabled) { palDebounce.Stop(); await RunPaletteNow(); }
            else { try { await palSearch; } catch (Exception) { } }
            if (palSel >= 0 && palSel < pal.Count) ChoosePal(pal[palSel]);
            else if (pal.Count > 0) ChoosePal(pal[0]);
        }

        public void PressPaletteForTest(Key key) { PaletteBox_KeyDown(PaletteBox, new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this) ?? new FakeSource(), 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent }); }
        sealed class FakeSource : PresentationSource
        {
            public override Visual RootVisual { get => null; set { } }
            public override bool IsDisposed => false;
            protected override CompositionTarget GetCompositionTargetCore() => null;
        }

        void ChoosePal(PalEntry p)
        {
            if (p == null) return;
            var id = p.Id;
            ClosePalette(false);
            try { ShowAtlas?.Invoke(); } catch (Exception e) { Util.Log("atlas palette: show: " + e.Message); }          // chosen over another page: the Atlas page comes first
            _ = Open(id);
        }

        // ---- searching
        async Task RunPaletteNow()
        {
            palDebounce.Stop();
            palCts?.Cancel();
            var cts = palCts = new CancellationTokenSource();
            int gen = ++palGen;
            var text = (PaletteBox.Text ?? "").Trim();
            var scope = palScope;
            if (text.Length == 0) { RenderEmpty(); return; }
            AtlasPaletteSearch.Result r;
            var hook = PaletteSearchHookForTest;
            try { r = await Task.Run(() => { hook?.Invoke(); return AtlasPaletteSearch.Run(text, scope, cts.Token); }, cts.Token); }
            catch (OperationCanceledException) { return; }
            catch (Exception e) { Util.Log("atlas palette search: " + e); r = new AtlasPaletteSearch.Result { Query = text, Scope = scope }; }
            if (gen != palGen || !palOpen) return;
            RenderResults(r);
        }

        // ---- the empty box: Pinned, Recent, Pages
        static string PalKind(AtlasNavItem it) => it.Kind == "browse" || it.Kind == "page" ? "page" : it.Kind;

        static AtlasHit HitOf(AtlasNavItem it) => new AtlasHit { Id = it.Id, Kind = PalKind(it), Name = it.Title, Detail = it.Detail, Quality = it.Quality, IconId = it.IconId };

        void RenderEmpty()
        {
            palResult = null;
            PaletteStack.Children.Clear(); pal.Clear(); palSel = -1;
            void Section(string title, IEnumerable<AtlasHit> hits)
            {
                var l = hits.ToList();
                if (l.Count == 0) return;
                PaletteStack.Children.Add(PalHead(title, ""));
                foreach (var h in l) PaletteStack.Children.Add(PalRow(h, ""));
            }
            Section("Pinned", NavState.Pins.Select(AtlasNav.Describe).Where(x => x != null && AtlasPaletteSearch.InScope(palScope, PalKind(x))).Select(HitOf));
            Section("Recent", NavState.Recents.Select(AtlasNav.Describe).Where(x => x != null && AtlasPaletteSearch.InScope(palScope, PalKind(x))).Select(HitOf).Take(8));
            if (palScope == "all" || palScope == "pages")
                Section("Pages", AtlasNav.AllPages().Take(palScope == "pages" ? 40 : 6));
            if (pal.Count == 0)
            {
                var none = Tb("Type a name to search the " + (palScope == "all" ? "Atlas" : AtlasPaletteSearch.ScopeTitle(palScope).ToLowerInvariant()) + ".", "Text.Secondary", null, 24); none.Margin = new Thickness(8, 24, 8, 0); none.TextWrapping = TextWrapping.Wrap;
                PaletteStack.Children.Add(none);
            }
            var tip = Tb("Paste a link such as item:2589 or npc:46 to open it. Start with i: n: z: q: s: or p: to search one kind.", "Text.Caption", Res("Brush.Text.Tertiary"), 12); tip.Margin = new Thickness(8, 12, 8, 8); tip.TextWrapping = TextWrapping.Wrap;
            PaletteStack.Children.Add(tip);
            PalSelect(pal.Count > 0 ? 0 : -1);
        }

        void RenderResults(AtlasPaletteSearch.Result r)
        {
            palResult = r;
            PaletteStack.Children.Clear(); pal.Clear(); palSel = -1;
            if (r.Total == 0)
            {
                var box = new StackPanel { Margin = new Thickness(8, 24, 8, 12), HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 360 };
                box.Children.Add(new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("Font.Icons"), FontSize = 32, Foreground = Res("Brush.Text.Tertiary"), HorizontalAlignment = HorizontalAlignment.Center });
                var l1 = Tb("Nothing matches \"" + r.Query + "\"", "Text.Body", null, 8); l1.HorizontalAlignment = HorizontalAlignment.Center; l1.TextAlignment = TextAlignment.Center; l1.TextWrapping = TextWrapping.Wrap; box.Children.Add(l1);
                var hint = Tb(r.Query.Length < 2 ? "Type at least two letters." : "Try fewer letters, or paste a link like item:2589 or npc:46.", "Text.Secondary", null, 4);
                hint.TextWrapping = TextWrapping.Wrap; hint.TextAlignment = TextAlignment.Center; box.Children.Add(hint);
                var layers = LayerHint();
                if (layers.Length > 0) { var ly = Tb(layers, "Text.Caption", Res("Brush.Text.Tertiary"), 8); ly.TextWrapping = TextWrapping.Wrap; ly.TextAlignment = TextAlignment.Center; box.Children.Add(ly); }
                if (r.Scope != "all")
                {
                    var all = new Button { Content = "Search all kinds", Style = (Style)FindResource("Button.M"), Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
                    all.Click += (s, e) => { palScope = "all"; RefreshChips(); palSearch = RunPaletteNow(); PaletteBox.Focus(); };
                    box.Children.Add(all);
                }
                PaletteStack.Children.Add(box);
                PalSelect(-1);
                return;
            }
            if (r.Note.Length > 0) { var n = Tb(r.Note, "Text.Caption", Res("Brush.Text.Tertiary"), 0); n.Margin = new Thickness(8, 0, 8, 4); n.TextWrapping = TextWrapping.Wrap; PaletteStack.Children.Add(n); }
            foreach (var g in r.Groups)
            {
                PaletteStack.Children.Add(PalHead(g.Title, g.Total > g.Hits.Count ? "best " + g.Hits.Count + " of " + g.Total : g.Total.ToString()));
                foreach (var h in g.Hits) PaletteStack.Children.Add(PalRow(h, r.Query));
            }
            PalSelect(0);
        }

        FrameworkElement PalHead(string title, string count)
        {
            var head = new DockPanel { Margin = new Thickness(8, PaletteStack.Children.Count == 0 ? 0 : 12, 8, 4) };
            var c = Tb(count, "Text.Caption"); c.Foreground = Res("Brush.Text.Tertiary");
            DockPanel.SetDock(c, Dock.Right);
            head.Children.Add(c);
            var t = Tb(title, "Text.Caption"); t.Tag = "head";
            head.Children.Add(t);
            return head;
        }

        // 40 px row: icon tile, name (quality colour, the matched letters underlined), one line of detail, where it comes from
        FrameworkElement PalRow(AtlasHit h, string q)
        {
            var row = new ContentControl { Style = (Style)FindResource("Side.Row"), Padding = new Thickness(8, 4, 8, 4), MinHeight = 40, Tag = h.Id, Focusable = false };
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var item = new AtlasNavItem { Id = h.Id, Title = h.Name, Quality = h.Quality, IconId = h.IconId, Kind = h.Kind };
            if (h.Kind == "page")
            {
                var d = AtlasNav.Describe(h.Id);
                item.Glyph = d?.Glyph ?? "";
                if (item.Glyph.Length == 0 && AtlasCategories.TryHosted(h.Id, out var pc, out _)) item.Glyph = pc.Glyph;
                item.Quality = -1; item.IconId = 0;
            }
            else item.Glyph = AtlasKinds.Glyph(h.Kind);
            var tile = IconTile(item, 28, 16); tile.Margin = new Thickness(0, 0, 8, 0);
            g.Children.Add(tile);
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var ar = new AtlasRow { Quality = h.Quality, Title = h.Name };
            stack.Children.Add(NameBlock(h.Name, q, h.Kind == "page" ? Res("Brush.Text.Primary") : ar.TitleBrush));
            if (!string.IsNullOrEmpty(h.Detail)) { var d = Tb(h.Detail, "Text.Caption"); d.TextTrimming = TextTrimming.CharacterEllipsis; stack.Children.Add(d); }
            Grid.SetColumn(stack, 1); g.Children.Add(stack);
            var lab = Tb(h.Label, "Text.Caption"); lab.Foreground = Res("Brush.Text.Tertiary"); lab.Margin = new Thickness(8, 0, 0, 0); lab.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(lab, 2); g.Children.Add(lab);
            row.Content = g;
            var entry = new PalEntry { Id = h.Id, Kind = h.Kind, Hit = h, Row = row };
            row.MouseLeftButtonUp += (s, e) => { e.Handled = true; ChoosePal(entry); };
            row.MouseEnter += (s, e) => { int i = pal.IndexOf(entry); if (i >= 0 && i != palSel) PalSelect(i, false); };
            pal.Add(entry);
            return row;
        }

        // the matched part of a name is underlined, not coloured (DESIGN: colour is the quality's)
        TextBlock NameBlock(string name, string q, Brush fg)
        {
            var t = Tb("", "Text.RowTitle"); t.Foreground = fg; t.TextTrimming = TextTrimming.CharacterEllipsis;
            int at = string.IsNullOrEmpty(q) ? -1 : name.IndexOf(q, StringComparison.OrdinalIgnoreCase);
            if (at < 0) { t.Text = name; return t; }
            if (at > 0) t.Inlines.Add(new Run(name.Substring(0, at)));
            t.Inlines.Add(new Run(name.Substring(at, q.Length)) { TextDecorations = TextDecorations.Underline });
            if (at + q.Length < name.Length) t.Inlines.Add(new Run(name.Substring(at + q.Length)));
            return t;
        }

        void PalSelect(int i, bool scroll = true)
        {
            if (pal.Count == 0) { palSel = -1; ShowPreview(null); return; }
            palSel = ((i % pal.Count) + pal.Count) % pal.Count;
            for (int k = 0; k < pal.Count; k++)
                if (k == palSel) pal[k].Row.SetResourceReference(Control.BackgroundProperty, "Brush.Accent.Tint"); else pal[k].Row.ClearValue(Control.BackgroundProperty);
            if (scroll) pal[palSel].Row.BringIntoView();
            ShowPreview(pal[palSel]);
        }

        string LayerHint()
        {
            var l = new List<string>();
            if (!Services.Db2.ItemData.Ready) l.Add("item data is not read from your game");
            if (!Services.Atlas.Classic.ClassicAtlas.IsReady) l.Add("the Classic baseline is not on this PC");
            if (!Services.Maps.MapData.Ready) l.Add("maps are not read");
            return l.Count == 0 ? "" : "Searching without: " + string.Join(", ", l) + ".";
        }

        // ---- the preview of the selected result: the game tooltip for items and spells, short facts for the rest
        void ShowPreview(PalEntry e)
        {
            palPreviewGen++;
            palPreviewTimer.Stop();
            PalettePreview.Children.Clear();
            palPreviewFor = e;
            if (e == null)
            {
                var t = Tb("Select a result to see what it is.", "Text.Secondary", null, 24); t.TextWrapping = TextWrapping.Wrap; PalettePreview.Children.Add(t);
                return;
            }
            var h = e.Hit;
            var item = new AtlasNavItem { Id = h.Id, Title = h.Name, Quality = h.Quality, IconId = h.IconId, Kind = h.Kind, Glyph = AtlasKinds.Glyph(h.Kind) };
            if (h.Kind == "page")
            {
                var d = AtlasNav.Describe(h.Id); item.Glyph = d?.Glyph ?? ""; item.Quality = -1; item.IconId = 0;
                if (item.Glyph.Length == 0 && AtlasCategories.TryHosted(h.Id, out var pc, out _)) item.Glyph = pc.Glyph;
            }
            if (h.Kind == "item" && AtlasIds.TryParse(h.Id, out _, out var itemNo) && Services.Db2.ItemData.Get(itemNo) != null)
            {
                // the same card the hover shows: the item's own tooltip, the icon beside it
                var gt = RowTipFor(new AtlasRow { Link = h.Id, Title = h.Name, Quality = h.Quality, IconId = h.IconId });
                gt.HorizontalAlignment = HorizontalAlignment.Left; gt.Margin = new Thickness(0, 12, 0, 0);
                PalettePreview.Children.Add(gt);
                return;
            }
            var head = new Grid { Margin = new Thickness(0, 12, 0, 8) };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var tile = IconTile(item, 40, 20); tile.Margin = new Thickness(0, 0, 12, 0); head.Children.Add(tile);
            var col = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var nm = Tb(h.Name, "Text.RowTitle"); nm.TextWrapping = TextWrapping.Wrap;
            if (h.Quality >= 0 && h.Quality < ItemLinks.QualityHex.Length) nm.Foreground = Lodge.Avatar.Frozen(ItemLinks.QualityHex[h.Quality]);
            col.Children.Add(nm);
            col.Children.Add(Tb(h.Kind == "page" ? "Atlas page" : AtlasKinds.Singular(h.Kind), "Text.Caption"));
            Grid.SetColumn(col, 1); head.Children.Add(col);
            PalettePreview.Children.Add(head);
            if (h.Kind == "page")
            {
                string blurb = h.Detail;
                if (string.IsNullOrEmpty(blurb) && AtlasCategories.TryHosted(h.Id, out var hc, out _)) blurb = hc.Blurb;
                if (!string.IsNullOrEmpty(blurb)) { var b = Tb(blurb, "Text.Secondary"); b.TextWrapping = TextWrapping.Wrap; PalettePreview.Children.Add(b); }
                var open = Tb(AtlasNav.IsPage(h.Id) ? "Opens the data sources." : "Opens the page.", "Text.Caption", Res("Brush.Text.Tertiary"), 8); PalettePreview.Children.Add(open);
                return;
            }
            if (!string.IsNullOrEmpty(h.Detail)) { var d = Tb(h.Detail, "Text.Secondary"); d.TextWrapping = TextWrapping.Wrap; PalettePreview.Children.Add(d); }
            if (!string.IsNullOrEmpty(h.Label)) PalettePreview.Children.Add(Tb(h.Label, "Text.Caption", Res("Brush.Text.Tertiary"), 4));
            PalettePreview.Children.Add(Tb("Loading...", "Text.Caption", Res("Brush.Text.Tertiary"), 12));
            palPreviewTimer.Start();            // the page behind it is built on a worker once the selection rests for a moment
        }

        async Task PreviewBuildAsync(PalEntry e, int gen)
        {
            if (e == null || gen != palPreviewGen) return;
            AtlasPageModel m = null;
            try { m = await Task.Run(() => AtlasQuery.Build(e.Id)); }
            catch (Exception ex) { Util.Log("atlas palette preview " + e.Id + ": " + ex.Message); }
            if (gen != palPreviewGen || !palOpen) return;
            // keep the header (first child); replace the rest
            while (PalettePreview.Children.Count > 1) PalettePreview.Children.RemoveAt(1);
            if (m == null || m.NotFound)
            {
                var t = Tb(m != null && m.Message.Length > 0 ? m.Message : "Nothing more is known about this.", "Text.Secondary", null, 4); t.TextWrapping = TextWrapping.Wrap; PalettePreview.Children.Add(t);
                return;
            }
            if (m.Kind == "spell" || m.Kind == "skill" || m.Kind == "item")
            {
                try
                {
                    var gt = GameTooltip.FromAtlas(m); if (m.Kind != "item") gt.SetIcon(m.IconId);
                    gt.HorizontalAlignment = HorizontalAlignment.Left; gt.Margin = new Thickness(0, 4, 0, 0);
                    PalettePreview.Children.Add(gt);
                    return;
                }
                catch (Exception ex) { Util.Log("atlas palette preview tooltip: " + ex.Message); }
            }
            if (!string.IsNullOrEmpty(m.Subtitle)) { var s = Tb(m.Subtitle, "Text.Secondary", null, 4); s.TextWrapping = TextWrapping.Wrap; PalettePreview.Children.Add(s); }
            foreach (var f in m.Facts.Take(6))
            {
                var g = new Grid { Margin = new Thickness(0, 8, 0, 0) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.Children.Add(Tb(f.Label, "Text.Secondary"));
                var v = Tb(f.Value, "Text.Body"); v.TextWrapping = TextWrapping.Wrap; Grid.SetColumn(v, 1); g.Children.Add(v);
                PalettePreview.Children.Add(g);
            }
            var secs = m.Sections.Where(s => s.Open).Take(4).Select(s => s.Title + " " + (s.Rows.Count + s.Hidden.Count).ToString("N0", Lodge.CalendarRules.English)).ToList();
            if (secs.Count > 0) { var sl = Tb(string.Join("  ·  ", secs), "Text.Caption", Res("Brush.Text.Tertiary"), 12); sl.TextWrapping = TextWrapping.Wrap; PalettePreview.Children.Add(sl); }
        }
    }
}
