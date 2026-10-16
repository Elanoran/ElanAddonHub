using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Atlas.Classic;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Maps;
using ElansAddonHub.Services.WorldMap;

namespace ElansAddonHub
{
    // The start page: what Atlas knows and where that comes from (game data, Classic baseline, the Lodge pool) and the Lodge's latest finds.
    public partial class AtlasPage
    {
        public ClassicSetup ClassicSetup { get; private set; }
        public Func<string> LodgeStateOverrideForTest;

        // The Lodge's state as the start page says it: set by the main window from the session
        public Func<string> LodgeState = () => "";

        // the pieces of a page that belong to one kind of page are let go when another is drawn
        void ResetPageState()
        {
            ResetEntity(); startInfo = null;
            classBook = null; classGroups = null; classShown.Clear();
            tileGrids.Clear(); tileCaptions.Clear(); presetHost = null; presetLabel = null;
            classicBanner = null; classicOffer = null; classicCard = null; classicBody = null;
            ClassicBannerGetForTest = ClassicBannerNotNowForTest = null; ClassicBannerProgressForTest = null;
            ClassicPrimaryForTest = ClassicCancelForTest = ClassicRemoveForTest = null;
        }

        // ============================================================================================================================ Home
        // The start page is a launchpad: the Classic call to action (only while the baseline is missing), the hero search (opens the palette), Continue (recent pages),
        // every category as a card in its group, shortcuts for your characters, the Lodge's latest finds, and ONE line for the state of the data (opens "Data sources").
        void DrawStart()
        {
            model = null; browse = null; hosted = null; browseList = null; drawnSig = Signature();
            ReleaseMaps();
            Body.Children.Clear();
            ResetPageState();
            pageTitle = "Atlas"; heroBrush = null;
            UpdateNav();
            var col = new StackPanel();
            Body.Children.Add(col);

            col.Children.Add(ClassicBannerView());
            col.Children.Add(ClassicOfferView());
            col.Children.Add(HeroSearch());
            var cont = ContinueBlock();
            if (cont != null) col.Children.Add(cont);
            col.Children.Add(BrowseBlock());
            _ = FillBrowseBlock();
            var finds = FindsBlock(4);
            if (finds != null) col.Children.Add(Card(finds, 24));
            col.Children.Add(StatusLine());
            startInfoCols = 0; startTileCols = 0;
            LayoutStart();
            Scroller.ScrollToTop();
            Motion.PageIn(Body);
        }

        // ============================================================================================================================ Data sources
        // The cards the start page used to open with, on a page of their own: game data, the Classic baseline (with its Get / Remove / licence), the Lodge pool, the world map cache.
        void DrawData()
        {
            model = null; browse = null; hosted = null; browseList = null; drawnSig = Signature();
            ReleaseMaps();
            Body.Children.Clear();
            ResetPageState();
            pageTitle = "Data sources"; heroBrush = null;
            UpdateNav();
            var col = new StackPanel();
            Body.Children.Add(col);
            var intro = Tb("Where the Atlas gets what it knows. Everything is read on this PC; nothing is uploaded without your say.", "Text.Secondary"); intro.TextWrapping = TextWrapping.Wrap; intro.MaxWidth = ReadWidth; intro.HorizontalAlignment = HorizontalAlignment.Left; intro.Margin = new Thickness(0, 0, 0, 12);
            col.Children.Add(intro);
            col.Children.Add(ClassicOfferView());
            // the cards sit side by side when the window is wide (see LayoutStart) and stack when it is narrow
            startCards.Clear();
            startCards.Add(Card(GameDataBlock()));
            startCards.Add(ClassicCardView());
            startCards.Add(Card(LodgeBlock()));
            startCards.Add(Card(WorldMapBlock()));
            startInfo = new Grid();
            col.Children.Add(startInfo);
            startInfoCols = 0; startTileCols = 0;
            LayoutStart();
            Scroller.ScrollToTop();
            Motion.PageIn(Body);
        }

        // ---- layout: tiles per row and info columns follow the width; nothing is drawn again, the same elements are placed anew only when the count changes
        public const double InfoTwoColumns = 900, InfoThreeColumns = 1400, TileMinWidth = 170;
        readonly List<FrameworkElement> startCards = new List<FrameworkElement>();
        readonly List<System.Windows.Controls.Primitives.UniformGrid> tileGrids = new List<System.Windows.Controls.Primitives.UniformGrid>();
        Grid startInfo; int startInfoCols, startTileCols;
        public int TileColumnsForTest => startTileCols;
        public int InfoColumnsForTest => startInfoCols;

        public static int TileColumnsFor(double scrollerWidth) => (int)Math.Max(2, Math.Min(6, Math.Floor((scrollerWidth - 8 - 32 + 8) / (TileMinWidth + 8))));
        public static int InfoColumnsFor(double scrollerWidth) => scrollerWidth >= InfoThreeColumns ? 3 : scrollerWidth >= InfoTwoColumns ? 2 : 1;

        void LayoutStart()
        {
            if (startInfo == null && tileGrids.Count == 0) return;
            double w = Scroller.ActualWidth > 0 ? Scroller.ActualWidth : 760;
            if (tileGrids.Count > 0)
            {
                int tiles = TileColumnsFor(w);
                foreach (var g in tileGrids) if (g.Columns != tiles) g.Columns = tiles;
                startTileCols = tiles;
            }
            if (startInfo == null) return;
            int info = InfoColumnsFor(w);
            if (info == startInfoCols) return;
            startInfoCols = info;
            foreach (var c in startCards) (VisualTreeHelper.GetParent(c) as Panel)?.Children.Remove(c);
            startInfo.Children.Clear(); startInfo.ColumnDefinitions.Clear();
            var lists = new List<List<FrameworkElement>>();
            for (int i = 0; i < info; i++) lists.Add(new List<FrameworkElement>());
            for (int i = 0; i < startCards.Count; i++)
            {
                int to = info == 1 ? 0 : info == 2 ? (i < (startCards.Count + 1) / 2 ? 0 : 1) : (startCards.Count == 4 ? new[] { 0, 1, 0, 2 }[i] : i % 3);
                lists[to].Add(startCards[i]);
            }
            for (int i = 0; i < info; i++)
            {
                if (i > 0) startInfo.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
                startInfo.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var sp = new StackPanel();
                foreach (var c in lists[i]) sp.Children.Add(c);
                Grid.SetColumn(sp, i * 2);
                startInfo.Children.Add(sp);
            }
        }

        // ---- the hero search: it is the palette's front door (a click, Enter or any typing opens it)
        public ContentControl HeroSearchForTest { get; private set; }

        FrameworkElement HeroSearch()
        {
            var hero = new ContentControl { Style = (Style)FindResource("Side.Row"), Padding = new Thickness(16, 0, 16, 0), MinHeight = 48, Margin = new Thickness(0, 0, 0, 24), Focusable = true, IsTabStop = true, Cursor = Cursors.IBeam };
            hero.SetResourceReference(Control.BackgroundProperty, "Brush.Surface2");
            hero.ToolTip = "Search the Atlas  (Ctrl+K)";
            System.Windows.Automation.AutomationProperties.SetName(hero, "Search the Atlas");
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("Font.Icons"), FontSize = 20, Foreground = Res("Brush.Text.Tertiary"), VerticalAlignment = VerticalAlignment.Center });
            var t = Tb("Search items, creatures, zones, quests, or paste a link", "Text.Body", Res("Brush.Text.Secondary")); t.VerticalAlignment = VerticalAlignment.Center; t.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(t, 1); g.Children.Add(t);
            var chip = new Border { Style = (Style)FindResource("Chip"), Padding = new Thickness(8, 0, 8, 0), MinHeight = 24, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            chip.Child = Tb("Ctrl K", "Text.Caption"); ((TextBlock)chip.Child).VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(chip, 2); g.Children.Add(chip);
            hero.Content = g;
            hero.MouseLeftButtonUp += (s, e) => { e.Handled = true; _ = OpenPalette(); };
            hero.PreviewKeyDown += (s, e) => { if (e.Key == Key.Enter || e.Key == Key.Space || e.Key == Key.Down) { e.Handled = true; _ = OpenPalette(); } };
            hero.PreviewTextInput += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Text)) { e.Handled = true; _ = OpenPalette(e.Text); } };
            HeroSearchForTest = hero;
            return hero;
        }

        TextBlock SectionHead(string text, double top = 0, double bottom = 8)
        {
            var t = Tb(text, "Text.Section", null, top); t.Margin = new Thickness(0, top, 0, bottom);
            return t;
        }

        // ---- Continue: the pages opened last, as cards with their icon
        public IReadOnlyList<string> ContinueIdsForTest => AtlasWalk(Body).OfType<ContentControl>().Where(c => c.Tag is string t && t.StartsWith("continue|")).Select(c => ((string)c.Tag).Substring(9)).ToList();

        FrameworkElement ContinueBlock()
        {
            var items = NavState.Recents.Select(AtlasNav.Describe).Where(x => x != null).Take(6).ToList();
            if (items.Count == 0) return null;
            var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 24) };
            sp.Children.Add(SectionHead("Continue"));
            var grid = new System.Windows.Controls.Primitives.UniformGrid { Margin = new Thickness(0, 0, -8, 0), Columns = TileColumnsFor(Scroller.ActualWidth > 0 ? Scroller.ActualWidth : 760) };
            tileGrids.Add(grid);
            foreach (var it in items)
            {
                var card = new ContentControl { Style = (Style)FindResource("Side.Row"), Padding = new Thickness(12, 8, 12, 8), MinHeight = 64, Margin = new Thickness(0, 0, 8, 8), Tag = "continue|" + it.Id, Focusable = false, Cursor = Cursors.Hand, ToolTip = it.Title };
                card.SetResourceReference(Control.BackgroundProperty, "Brush.Surface1");
                System.Windows.Automation.AutomationProperties.SetName(card, it.Title);
                var link = it.Id;
                card.MouseLeftButtonUp += (s, e) => { e.Handled = true; _ = Open(link); };
                Keyable(card, () => _ = Open(link), false);
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var tile = IconTile(it, 40, 16); tile.Margin = new Thickness(0, 0, 12, 0);
                g.Children.Add(tile);
                var col = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                var name = Tb(it.Title, "Text.RowTitle"); name.TextTrimming = TextTrimming.CharacterEllipsis;
                if (it.Quality >= 0 && it.Quality < ItemLinks.QualityHex.Length) name.Foreground = Lodge.Avatar.Frozen(ItemLinks.QualityHex[it.Quality]);
                col.Children.Add(name);
                var det = Tb(it.Detail, "Text.Caption"); det.TextTrimming = TextTrimming.CharacterEllipsis; col.Children.Add(det);
                Grid.SetColumn(col, 1); g.Children.Add(col);
                card.Content = g;
                grid.Children.Add(card);
            }
            sp.Children.Add(grid);
            return sp;
        }

        // ---- Browse: every category as a card (icon tile, name, how many), grouped like the nav
        readonly Dictionary<string, TextBlock> tileCaptions = new Dictionary<string, TextBlock>();
        int startGen;
        Task startFill = Task.CompletedTask;
        public Task StartFillForTest => startFill;
        StackPanel presetHost;
        TextBlock presetLabel;

        FrameworkElement BrowseBlock()
        {
            tileCaptions.Clear(); artCards.Clear();
            var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            sp.Children.Add(SectionHead("Browse"));
            var cats = AtlasCategories.Visible;
            var groups = AtlasNav.Groups.Where(g => cats.Any(c => (string.IsNullOrEmpty(c.Group) ? AtlasNav.Database : c.Group) == g)).ToList();
            foreach (var c in cats) { var g = string.IsNullOrEmpty(c.Group) ? AtlasNav.Database : c.Group; if (!groups.Contains(g)) groups.Add(g); }
            bool lodgeFinds = false;
            try { lodgeFinds = AtlasQuery.LodgeHasData() && AtlasCategories.Find("items") != null; } catch (Exception) { }
            if (lodgeFinds && !groups.Contains(AtlasNav.Lodge)) groups.Add(AtlasNav.Lodge);
            int cols = TileColumnsFor(Scroller.ActualWidth > 0 ? Scroller.ActualWidth : 760);
            foreach (var grp in groups)
            {
                var label = Tb(grp, "Text.Secondary"); double above = sp.Children.Count > 1 ? 8 : 0; label.Margin = new Thickness(0, above, 0, 8);
                sp.Children.Add(label);
                var wrap = new System.Windows.Controls.Primitives.UniformGrid { Margin = new Thickness(0, 0, -8, 0), Columns = cols };
                tileGrids.Add(wrap);
                foreach (var c in cats.Where(c => (string.IsNullOrEmpty(c.Group) ? AtlasNav.Database : c.Group) == grp))
                    wrap.Children.Add(CategoryCard(c.Title, c.Glyph, c.OpenId, c.Blurb, c.Hosted ? c.Blurb : "", c.Hosted ? null : c.Id, c.Id));
                if (grp == AtlasNav.Lodge && lodgeFinds)
                    wrap.Children.Add(CategoryCard("Finds", "", AtlasNav.FindsId, "What the Lodge's members found and saw drop", "What members found and saw drop", null, "finds"));
                sp.Children.Add(wrap);
            }
            presetHost = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            sp.Children.Add(presetHost);
            return sp;
        }

        FrameworkElement CategoryCard(string title, string glyph, string openId, string tip, string caption, string countFor, string artKey = null)
        {
            var tile = new ContentControl { Style = (Style)FindResource("Side.Row"), Padding = new Thickness(12, 8, 12, 8), MinHeight = 64, Margin = new Thickness(0, 0, 8, 8), Tag = openId, Focusable = false, Cursor = Cursors.Hand, ToolTip = tip };
            tile.SetResourceReference(Control.BackgroundProperty, "Brush.Surface1");
            tile.MouseLeftButtonUp += Row_Click;
            Keyable(tile, () => _ = Open(openId), false);
            System.Windows.Automation.AutomationProperties.SetName(tile, title);
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var it = new AtlasNavItem { Title = title, Glyph = glyph };
            var ic = IconTile(it, 40, 20); ic.Margin = new Thickness(0, 0, 12, 0);
            g.Children.Add(ic);
            var col = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            col.Children.Add(Tb(title, "Text.RowTitle"));
            var cap = Tb(caption, "Text.Caption"); cap.TextTrimming = TextTrimming.CharacterEllipsis;
            col.Children.Add(cap);
            if (countFor != null) tileCaptions[countFor] = cap;
            Grid.SetColumn(col, 1); g.Children.Add(col);
            tile.Content = g;
            var art = ArtLibrary.Category(artKey);
            var back = art == null ? null : ArtLibrary.Backdrop(art, "Brush.Art.CardVeil", (CornerRadius)FindResource("Radius.Control"), "card-art");
            if (back != null)
            {
                // painted card: the art fills the whole tile under a veil that is darkest behind the text; a slow-enough zoom on hover (Motion.Fast, ease-out)
                tile.Content = null;
                tile.Padding = new Thickness(0);
                g.Margin = new Thickness(12, 8, 12, 8);
                var layers = new Grid();
                layers.Children.Add(back); layers.Children.Add(g);
                tile.Content = layers;
                artCards[artKey] = tile;
                if (((Grid)back).Children[0] is Border pic && pic.Background is ImageBrush brush)
                {
                    var zoom = new ScaleTransform(1, 1, 0.5, 0.5);
                    brush.RelativeTransform = zoom;
                    void To(double s)
                    {
                        if (!Motion.Enabled) { zoom.ScaleX = zoom.ScaleY = s; return; }
                        var a = new System.Windows.Media.Animation.DoubleAnimation(s, Motion.Fast) { EasingFunction = s > 1 ? Motion.EaseOut : Motion.EaseIn };
                        zoom.BeginAnimation(ScaleTransform.ScaleXProperty, a); zoom.BeginAnimation(ScaleTransform.ScaleYProperty, a);
                    }
                    tile.MouseEnter += (s, e) => To(1.04);
                    tile.MouseLeave += (s, e) => To(1);
                }
            }
            return tile;
        }

        // the category cards that carry painted art (ArtLibrary.Categories), by category id
        readonly Dictionary<string, FrameworkElement> artCards = new Dictionary<string, FrameworkElement>(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyDictionary<string, FrameworkElement> ArtCardsForTest => artCards;

        // counts on the cards and the shortcuts that the data can answer, worked out off the UI thread (the first time they build the category indexes)
        Task FillBrowseBlock()
        {
            int gen = ++startGen;
            var host = presetHost;
            var caps = new Dictionary<string, TextBlock>(tileCaptions);
            var cats = AtlasCategories.Visible.Where(c => !c.Hosted).ToList();
            return startFill = Task.Run(async () =>
            {
                foreach (var c in cats)
                {
                    int n = AtlasBrowse.Count(c);
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (gen != startGen || !caps.TryGetValue(c.Id, out var cap)) return;
                        cap.Text = n > 0 ? n.ToString("N0", Lodge.CalendarRules.English) : "Nothing yet";
                    });
                    if (gen != startGen) return;
                }
                var presets = AtlasBrowse.Presets();
                var me = AtlasQuery.Characters().FirstOrDefault();
                await Dispatcher.InvokeAsync(() =>
                {
                    if (gen != startGen || host != presetHost) return;
                    navSig = ""; BuildNav();                      // the nav shows the counts that are known now
                    host.Children.Clear();
                    if (presets.Count == 0)
                    {
                        host.Children.Add(Tb(ClassicBaselineText(), "Text.Caption", Res("Brush.Text.Tertiary"), 4));
                        return;
                    }
                    presetLabel = Tb(me == null ? "Where to get things" : "For " + me.Name, "Text.Section"); presetLabel.Margin = new Thickness(0, 0, 0, 8);
                    host.Children.Add(presetLabel);
                    var w = new WrapPanel { Margin = new Thickness(0, 0, 0, 0) };
                    foreach (var p in presets)
                    {
                        var id = p.Id;
                        var b = GhostButton(p.Title, () => { _ = Open(id); }, p.Tip + " (" + AtlasFormat.Count(p.Count, "match", "matches") + ")");
                        b.Margin = new Thickness(0, 0, 8, 8);
                        w.Children.Add(b);
                    }
                    host.Children.Add(w);
                    if (!Services.Atlas.Classic.ClassicAtlas.IsReady) host.Children.Add(Tb(ClassicBaselineText(), "Text.Caption", Res("Brush.Text.Tertiary"), 0));
                    host.Margin = new Thickness(0, 12, 0, 24);
                });
            });
        }

        static string ClassicBaselineText() => Services.Atlas.Classic.ClassicAtlas.IsReady ? "" : "Download the Classic baseline (above or on the Data sources page) for drop, vendor and spawn sources: more shortcuts appear when the data can answer them.";

        // ---- the one status line: a dot and a few words for each source; it opens the Data sources page
        public sealed class SourceState { public string Name = "", Text = ""; public int Level; }      // Level: 2 ready, 1 working / partial, 0 off or missing

        public List<SourceState> SourceStates()
        {
            var l = new List<SourceState>();
            bool gameReady = ItemData.Ready && SpellData.Ready && MapData.Ready;
            bool gameOff = ItemData.Status == ItemData.State.Disabled && SpellData.Status == SpellData.State.Disabled && MapData.Status == MapData.State.Disabled;
            l.Add(new SourceState { Name = "Game data", Text = gameReady && (ItemData.Stale || SpellData.Stale || MapData.Stale) ? "Game data of the previous WoW version (the new one is being read)" : gameReady ? "Game data ready" : gameOff ? "Game data off" : ItemData.Ready || SpellData.Ready || MapData.Ready ? "Game data partly read" : "Game data not read yet", Level = gameReady ? 2 : gameOff ? 0 : 1 });
            EnsureSetup();
            var cs = ClassicSetup.State;
            l.Add(new SourceState { Name = "Classic", Text = cs == ClassicCardState.Ready ? "Classic baseline ready" : cs == ClassicCardState.Downloading || cs == ClassicCardState.Importing || cs == ClassicCardState.Asking ? "Classic baseline downloading" : cs == ClassicCardState.Failed ? "Classic baseline problem" : "Classic baseline not on this PC", Level = cs == ClassicCardState.Ready ? 2 : cs == ClassicCardState.NotDownloaded ? 0 : 1 });
            var p = Pool();
            bool online = p.State.StartsWith("Connected", StringComparison.Ordinal);
            l.Add(new SourceState { Name = "Lodge", Text = p.Has ? "Lodge pool: " + AtlasFormat.Count(p.Finds, "find", "finds") : online ? "Lodge connected" : "No Lodge", Level = p.Has ? 2 : online ? 1 : 0 });
            int built = 0, total = 0;
            try { foreach (var c in WorldMapService.Continents(MapData.Index)) { total++; if (WorldMapService.IsBuilt(c.MapId)) built++; } } catch (Exception) { }
            l.Add(new SourceState { Name = "World map", Text = total == 0 ? "World map off" : built == total ? "World map cached" : built > 0 ? "World map: " + built + " of " + total + " cached" : "World map not built yet", Level = total == 0 ? 0 : built == total ? 2 : built > 0 ? 1 : 0 });
            return l;
        }

        public ContentControl StatusLineForTest { get; private set; }

        FrameworkElement StatusLine()
        {
            var line = new ContentControl { Style = (Style)FindResource("Side.Row"), Padding = new Thickness(12, 4, 12, 4), MinHeight = 32, Focusable = true, IsTabStop = true, Cursor = Cursors.Hand, ToolTip = "Open the data sources" };
            System.Windows.Automation.AutomationProperties.SetName(line, "Data sources");
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var wrap = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            foreach (var st in SourceStates())
            {
                var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 0) };
                var dot = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
                dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, st.Level == 2 ? "Brush.Success" : st.Level == 1 ? "Brush.Warn" : "Brush.Text.Tertiary");
                item.Children.Add(dot);
                item.Children.Add(Tb(st.Text, "Text.Caption"));
                wrap.Children.Add(item);
            }
            g.Children.Add(wrap);
            var more = Tb("Data sources", "Text.Caption", Res("Brush.Text.Secondary")); more.VerticalAlignment = VerticalAlignment.Center; more.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(more, 1); g.Children.Add(more);
            line.Content = g;
            line.MouseLeftButtonUp += (s, e) => { e.Handled = true; _ = Open(AtlasNav.DataId); };
            line.PreviewKeyDown += (s, e) => { if (e.Key == Key.Enter || e.Key == Key.Space) { e.Handled = true; _ = Open(AtlasNav.DataId); } };
            StatusLineForTest = line;
            return line;
        }

        FrameworkElement WorldMapBlock()
        {
            var sp = new StackPanel();
            sp.Children.Add(Tb("World map cache", "Text.Section"));
            var n = Tb("The continent pictures are made from your own game files the first time you open a continent, once per game version, and kept on this PC.", "Text.Secondary", null, 2); n.TextWrapping = TextWrapping.Wrap; sp.Children.Add(n);
            List<WorldContinent> cont = null;
            try { cont = WorldMapService.Continents(MapData.Index); } catch (Exception) { }
            if (cont == null || cont.Count == 0)
            {
                var none = Tb(MapData.Status == MapData.State.Disabled ? "Maps are switched off." : "Maps are not read yet, so there is no continent to build.", "Text.Body", null, 8); none.TextWrapping = TextWrapping.Wrap; sp.Children.Add(none);
                return sp;
            }
            foreach (var c in cont)
            {
                var g = new Grid { Margin = new Thickness(0, 8, 0, 0) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.Children.Add(Tb(c.Name, "Text.Secondary"));
                bool built = false, terrain = false;
                try { built = WorldMapService.IsBuilt(c.MapId); terrain = WorldMapService.TerrainBuilt(c.MapId); } catch (Exception) { }
                var v = Tb(built ? (terrain ? "Built, with the 3D terrain" : "Built") : "Not built yet", "Text.Body"); Grid.SetColumn(v, 1); g.Children.Add(v);
                sp.Children.Add(g);
            }
            return sp;
        }


        // ---- game data: one line per layer, as the Settings "Game data" line says it
        public List<string> GameDataLines()
        {
            var l = new List<string>
            {
                "Items: " + StatusOnly(ItemData.StatusLine),
                "Spells and recipes: " + (SpellData.Ready ? SpellData.SpellCount.ToString("N0", CalendarRules.English) + " spells, " + SpellData.RecipeCount.ToString("N0", CalendarRules.English) + " recipes" : SpellData.Status == SpellData.State.Disabled ? "switched off" : SpellData.Status == SpellData.State.Unavailable ? "unavailable (" + SpellData.Reason + ")" : "not read yet"),
                "Maps: " + StatusOnly(MapData.StatusLine),
                "Seen by your game: " + StatusOnly(SeenData.StatusLine),
            };
            return l;
        }

        // the Settings lines say their own name first ("Item data: read from ...", "Maps: off"): the start page has the name in its own column
        static string StatusOnly(string line)
        {
            line = (line ?? "").Trim();
            int c = line.IndexOf(':');
            if (c > 0 && c < 16) line = line.Substring(c + 1).Trim();
            return line == "off" ? "switched off" : line;
        }

        FrameworkElement GameDataBlock()
        {
            var sp = new StackPanel();
            sp.Children.Add(Tb("Your game data", "Text.Section"));
            var n = Tb("Read from your own WoW installation, on this PC only. Label: Forever.", "Text.Secondary", null, 2); n.TextWrapping = TextWrapping.Wrap; sp.Children.Add(n);
            foreach (var line in GameDataLines())
            {
                int c = line.IndexOf(':');
                var g = new Grid { Margin = new Thickness(0, 8, 0, 0) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.Children.Add(Tb(line.Substring(0, c), "Text.Secondary"));
                var v = Tb(line.Substring(c + 1).Trim(), "Text.Body"); v.TextWrapping = TextWrapping.Wrap; Grid.SetColumn(v, 1); g.Children.Add(v);
                sp.Children.Add(g);
            }
            var hint = Tb(ItemData.Status == ItemData.State.Disabled ? "Switched on again in Settings > General > Read item data from my WoW installation." : "Switch it off in Settings > General > Read item data from my WoW installation.", "Text.Caption", Res("Brush.Text.Tertiary"), 12);
            hint.TextWrapping = TextWrapping.Wrap; sp.Children.Add(hint);
            return sp;
        }

        // ---- the Lodge pool
        public sealed class PoolSummary
        {
            public bool Has; public int Finds, FindMembers, Sources, Vendors, Quests; public long Updated; public string State = "";
            public int SeenQuests, SeenNpcs, SeenObjects, SeenMembers, GameQuests;   // the seen pool (server 2.25 counts the members; GameQuests = the quests the game files list)
        }

        public PoolSummary Pool()
        {
            var L = AtlasQuery.Context.Lodge?.Invoke();
            var p = new PoolSummary { State = LodgeStateOverrideForTest != null ? LodgeStateOverrideForTest() : LodgeState() };
            if (L == null) return p;
            p.Has = L.HasData;
            p.Finds = L.FindCount; p.FindMembers = L.AllFinds.Select(f => f.By ?? "a member").Distinct().Count();
            p.Sources = L.KillSources + L.ObjectSources + L.FishingZones; p.Vendors = L.VendorCount; p.Updated = L.Updated;
            p.SeenQuests = L.SeenQuestCount; p.SeenNpcs = L.SeenNpcCount; p.SeenObjects = L.SeenObjectCount; p.SeenMembers = L.SeenMembers;
            p.GameQuests = SpellData.World?.QuestIds.Count ?? 0;
            return p;
        }

        // "Quests known to the Lodge: 1,240 of 6,610 in the game files, from 4 members' game caches; also 3,100 creatures and 1,900 objects." ("" = nothing seen yet)
        public static string SeenCoverage(PoolSummary p)
        {
            if (p == null || p.SeenQuests + p.SeenNpcs + p.SeenObjects == 0) return "";
            var inv = CalendarRules.English;
            var s = "Quests known to the Lodge: " + p.SeenQuests.ToString("N0", inv) + (p.GameQuests > 0 ? " of " + p.GameQuests.ToString("N0", inv) + " in the game files" : "");
            if (p.SeenMembers > 0) s += ", from " + AtlasFormat.Count(p.SeenMembers, "member's", "members'") + " game " + (p.SeenMembers == 1 ? "cache" : "caches");
            return s + "; also " + AtlasFormat.Count(p.SeenNpcs, "creature", "creatures") + " and " + AtlasFormat.Count(p.SeenObjects, "object", "objects") + ".";
        }

        FrameworkElement LodgeBlock()
        {
            var sp = new StackPanel();
            sp.Children.Add(Tb("Lodge pool", "Text.Section"));
            var p = Pool();
            var l1 = Tb(p.State.Length > 0 ? p.State : "Not connected to a Lodge", "Text.Body", null, 4); l1.TextWrapping = TextWrapping.Wrap; sp.Children.Add(l1);
            if (p.Has)
            {
                var d = Tb(AtlasFormat.Count(p.Sources, "source", "sources") + " with observed loot, " + AtlasFormat.Count(p.Vendors, "vendor", "vendors") + ", " + AtlasFormat.Count(p.Finds, "find", "finds") + " by "
                    + AtlasFormat.Count(p.FindMembers, "member", "members") + (p.Updated > 0 ? ". Last update " + AtlasFormat.Ago(p.Updated, AtlasQuery.Context.NowUtc()) + "." : "."), "Text.Secondary", null, 4);
                d.TextWrapping = TextWrapping.Wrap; sp.Children.Add(d);
                var cov = SeenCoverage(p);
                if (cov.Length > 0) { var c = Tb(cov, "Text.Secondary", null, 4); c.TextWrapping = TextWrapping.Wrap; sp.Children.Add(c); }
            }
            else
            {
                var d = Tb("Members who share what they see in game (with Elan's Toolkit) build this pool: drop chances from real kills, vendor prices, finds and spawn points. Label: Forever.", "Text.Secondary", null, 4);
                d.TextWrapping = TextWrapping.Wrap; sp.Children.Add(d);
            }
            return sp;
        }

        FrameworkElement FindsBlock(int take = 8)
        {
            var L = AtlasQuery.Context.Lodge?.Invoke();
            if (L == null || L.AllFinds.Count == 0) return null;
            var sp = new StackPanel();
            sp.Children.Add(Tb("Recent finds in the Lodge", "Text.Section"));
            var rows = new List<AtlasRow>();
            foreach (var f in L.AllFinds.OrderByDescending(f => f.Seq).Take(take))
            {
                var r = AtlasQuery.ItemRow(f.Item, "", AtlasLabel.Forever);
                var src = f.Src ?? "";
                int c = src.IndexOf(':'); int.TryParse(c > 0 ? src.Substring(c + 1) : "0", out var sid);
                var kind = c > 0 ? src.Substring(0, c) : src;
                var from = kind == "npc" ? AtlasQuery.NpcName(sid) : kind == "obj" ? AtlasQuery.ObjectName(sid) : kind == "vendor" ? "bought from " + AtlasQuery.NpcName(sid) : kind == "quest" ? "quest " + AtlasQuery.QuestTitle(sid) : kind == "fish" ? "fishing" : "";
                r.Detail = (string.IsNullOrEmpty(f.By) ? "a member" : f.By) + " · " + f.Count + "× " + (from.Length > 0 ? "from " + from + " " : "") + (f.Zone > 0 ? "· " + AtlasQuery.ZoneName(f.Zone) : "");
                r.Right = AtlasFormat.Ago(f.At, AtlasQuery.Context.NowUtc());
                rows.Add(r);
            }
            var host = new ContentControl { Margin = new Thickness(-8, 8, -8, 0), Focusable = false, Content = new ItemsControl { ItemsSource = rows, ItemTemplate = (DataTemplate)FindResource("AtlasRowTemplate"), Focusable = false } };
            sp.Children.Add(host);
            if (take < 8 && L.AllFinds.Count > take && AtlasCategories.Find("items") != null)
            {
                var all = GhostButton("All " + L.AllFinds.Count.ToString("N0", CalendarRules.English) + " finds", () => { _ = Open(AtlasNav.FindsId); }, "Everything the Lodge has found and seen drop");
                all.Margin = new Thickness(0, 4, 0, 0); sp.Children.Add(all);
            }
            return sp;
        }
    }
}
