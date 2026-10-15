using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Maps;

namespace ElansAddonHub
{
    // The entity page template (Atlas refresh, step 2): ONE page for every kind (item, creature, object, quest, zone, instance, spell, skill, faction, class, race).
    //   hero      a card: the big icon or the kind's glyph tile, the name in its colour (an item's quality, a class' colour), the subtitle, the key-fact chips
    //   tabs      Overview | Sources | Map | Related | Lodge - AtlasSection.Key says which tab a table lives on (AtlasTabs.OfSection); a tab with nothing in it is not there, and a
    //             page with only an Overview has no tab strip. The selected tab is in the id (item:100?tab=sources) but is not a history step.
    //   rail      340 wide at the right from RailFrom px: a thumbnail of the map (opens the Map tab) and the quick facts (what links somewhere); below it, narrower, the facts sit
    //             in the Overview and the map is its tab
    // The tabs are built when first shown (a page build only makes the hero, the strip and the tab it opens on). The layout is placed again only when the mode changes.
    public partial class AtlasPage
    {
        public const double RailFrom = 880, RailWidth = 340;

        string tabNow = "", tabDefault = AtlasTabs.Overview;
        readonly List<string> tabsShown = new List<string>();
        readonly Dictionary<string, RadioButton> tabButtons = new Dictionary<string, RadioButton>();
        readonly Dictionary<string, FrameworkElement> tabContent = new Dictionary<string, FrameworkElement>();
        ContentControl tabHost;
        Grid entityGrid;
        StackPanel railStack;
        FrameworkElement factsCard, railMapHost;
        AtlasMap railMap;
        FrameworkElement heroEl, tabStrip, bannerEl;
        TextBlock heroName;
        Brush heroBrush;
        string pageTitle = "";
        bool suppressTab, railOn;
        bool wide; int columnMode = -1;

        // ---- for the tests
        public string TabNowForTest => tabNow;
        public string TabDefaultForTest => tabDefault;
        public IReadOnlyList<string> TabsForTest => tabsShown.ToList();
        public bool TabStripShownForTest => tabStrip != null && tabStrip.Visibility == Visibility.Visible;
        public RadioButton TabButtonForTest(string tab) => tabButtons.TryGetValue(tab, out var b) ? b : null;
        public int TabCountForTest(string tab) => TabCount(tab);
        public bool TabBuiltForTest(string tab) => tabContent.ContainsKey(tab);
        public FrameworkElement TabHostForTest => tabHost;
        public FrameworkElement HeroForTest => heroEl;
        public FrameworkElement BannerForTest => bannerEl;
        public string HeroNameForTest => heroName?.Text ?? "";
        public Brush HeroBrushForTest => heroBrush;
        public IReadOnlyList<string> HeroChipsForTest => heroEl == null ? new List<string>() : AtlasWalk(heroEl).OfType<Border>().Where(b => b.Tag as string == "chip").Select(b => ChipText(b)).ToList();
        public FrameworkElement HeroTileForTest => heroEl == null ? null : AtlasWalk(heroEl).OfType<Border>().FirstOrDefault(b => b.Tag as string == "hero-tile");
        public bool TitleRowGoneForTest => FindName("TitleBlock") == null && FindName("TitleText") == null && FindName("SearchBox") == null;
        public bool RailMapHostVisibleForTest => railMapHost != null && railMapHost.Visibility == Visibility.Visible;
        public bool RailShownForTest => railOn && railStack != null && railStack.Visibility == Visibility.Visible;
        public FrameworkElement RailForTest => railStack;
        public FrameworkElement RailMapForTest => railMap;
        public FrameworkElement FactsCardForTest => factsCard;
        public bool WideForTest => wide;
        public int ColumnsForTest => columnMode < 0 ? 0 : columnMode + 1;
        public double ScrollerWidthForTest => Scroller.ActualWidth;
        public FrameworkElement BodyForTest => Body;
        public IReadOnlyList<AtlasMap> MapsForTest => maps;
        public string PageTitleForTest => pageTitle;
        public Brush TitleBrushForTest => heroBrush ?? Res("Brush.Text.Primary");
        static string ChipText(Border b) => (b.Child as TextBlock)?.Text ?? "";

        public static int ColumnModeFor(double scrollerWidth, bool hasRail) => hasRail && scrollerWidth >= RailFrom ? 1 : 0;

        // ============================================================================================================================ a page
        void Draw(AtlasPageModel m)
        {
            model = m; browse = null; hosted = null; browseList = null; drawnSig = Signature();
            ReleaseMaps();
            Body.Children.Clear();
            ResetPageState();
            pageTitle = string.IsNullOrEmpty(m.Title) ? "Atlas" : m.Title;
            heroBrush = HeroBrushOf(m);
            UpdateNav();
            ColourLastCrumb(heroBrush);

            var col = new StackPanel();
            Body.Children.Add(col);
            bannerEl = ZoneBanner(m);
            if (bannerEl != null) col.Children.Add(bannerEl);
            heroEl = HeroView(m, out heroName);
            col.Children.Add(heroEl);

            if (m.NotFound)
            {
                col.Children.Add(EmptyCard("", m.Message.Length > 0 ? m.Message : "Nothing is known about this.", "Search for a name (Ctrl+K), or open the start page to see what data Atlas has."));
                foreach (var n in m.Notes) col.Children.Add(Note(n));
                foreach (var s in m.Sections.Where(s => s.Open)) col.Children.Add(SectionCard(s));
                Motion.PageIn(Body);
                return;
            }

            PlanTabs(m);
            factsCard = FactsFor(m);
            var want = AtlasTabs.TabOf(history.Current);
            if (!AtlasTabs.SamePage(history.Current, m.Id) || !tabsShown.Contains(want)) want = "";
            tabNow = want.Length > 0 ? want : tabsShown.Contains(tabDefault) ? tabDefault : AtlasTabs.Overview;
            bool hasRail = factsCard != null || HasRailMap(m);
            int mode = ColumnModeFor(Scroller.ActualWidth, hasRail);
            railOn = mode > 0;

            // the tab strip
            tabStrip = TabStrip(m);
            col.Children.Add(tabStrip);

            // the tab's content and the rail, in a grid that is placed again when the mode changes
            entityGrid = new Grid();
            tabHost = new ContentControl { Focusable = false, IsTabStop = false, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            railStack = new StackPanel();
            entityGrid.Children.Add(tabHost); entityGrid.Children.Add(railStack);
            col.Children.Add(entityGrid);
            columnMode = -1;
            tabHost.Content = TabContentFor(tabNow);
            ApplyLayout(mode);
            // a link named a tab the page does not have: the id says what the page shows
            if (AtlasTabs.TabOf(history.Current).Length > 0 && AtlasTabs.TabOf(history.Current) != tabNow) SyncIdToTab(false);
            Scroller.ScrollToTop();
            Motion.PageIn(Body);
        }

        // the colour of the name: an item's quality, a class' own colour; else the page's primary text colour
        Brush HeroBrushOf(AtlasPageModel m)
        {
            if (m.Kind == "item" && m.Quality >= 0 && m.Quality < ItemLinks.QualityHex.Length) return Lodge.Avatar.Frozen(ItemLinks.QualityHex[m.Quality]);
            if (!string.IsNullOrEmpty(m.Accent)) { try { return Lodge.Avatar.Frozen(m.Accent); } catch (Exception) { } }
            return Res("Brush.Text.Primary");
        }

        void ResetEntity()
        {
            entityGrid = null; tabHost = null; railStack = null; factsCard = null; railMapHost = null; heroEl = null; heroName = null; tabStrip = null; bannerEl = null;
            railMap?.ClearMarkers(); railMap = null;
            tabsShown.Clear(); tabButtons.Clear(); tabContent.Clear();
            tabNow = ""; tabDefault = AtlasTabs.Overview; columnMode = -1; railOn = false; wide = false;
        }

        // ============================================================================================================================ the hero
        FrameworkElement HeroView(AtlasPageModel m, out TextBlock name)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var tile = HeroTile(m); tile.Margin = new Thickness(0, 0, 16, 0); tile.VerticalAlignment = VerticalAlignment.Top;
            grid.Children.Add(tile);
            var col = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            name = Tb(pageTitle, "Text.Title", heroBrush);
            name.TextTrimming = TextTrimming.CharacterEllipsis; name.ToolTip = pageTitle;
            System.Windows.Automation.AutomationProperties.SetName(name, pageTitle);
            col.Children.Add(name);
            var sub = m.NotFound ? AtlasKinds.Singular(m.Kind) : m.Subtitle;
            if (!string.IsNullOrEmpty(sub)) { var s = Tb(sub, "Text.Secondary", null, 2); s.TextTrimming = TextTrimming.CharacterEllipsis; col.Children.Add(s); }
            var chips = m.NotFound ? new List<KeyValuePair<string, Brush>>() : HeroChips(m);
            if (chips.Count > 0)
            {
                var w = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
                foreach (var c in chips)
                {
                    var b = new Border { Style = (Style)FindResource("Chip"), Padding = new Thickness(8, 0, 8, 0), MinHeight = 24, Margin = new Thickness(0, 0, 8, 4), Tag = "chip" };
                    var t = Tb(c.Key, "Text.Secondary", c.Value); t.VerticalAlignment = VerticalAlignment.Center; b.Child = t;
                    w.Children.Add(b);
                }
                col.Children.Add(w);
            }
            Grid.SetColumn(col, 1); grid.Children.Add(col);
            return Card(grid, 12);
        }

        // a zone with painted art (ArtLibrary.Zones) opens with a wide banner: the full width of the page, 176 high, the picture cropped around its focal
        // point and fading into the page at the bottom. Other pages (and zones with no art) have none.
        public const double BannerHeight = 176;
        public static int ZoneIdOf(AtlasPageModel m)
        {
            if (m == null || m.Kind != "zone" || m.NotFound || string.IsNullOrEmpty(m.Id) || !m.Id.StartsWith("zone:", StringComparison.Ordinal)) return 0;
            var s = m.Id.Substring(5); int q = s.IndexOf('?'); if (q >= 0) s = s.Substring(0, q);
            return int.TryParse(s, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) ? id : 0;
        }

        FrameworkElement ZoneBanner(AtlasPageModel m)
        {
            var art = ArtLibrary.Zone(ZoneIdOf(m));
            var back = art == null ? null : ArtLibrary.Backdrop(art, "Brush.Art.BannerFade", (CornerRadius)FindResource("Radius.Card"), "zone-banner-art");
            if (back == null) return null;
            var b = new Border { Height = BannerHeight, Margin = new Thickness(0, 0, 0, 12), Tag = "zone-banner", IsHitTestVisible = false, Child = back };
            System.Windows.Automation.AutomationProperties.SetName(b, "Painting of " + m.Title);
            return b;
        }

        // the big tile: the game's icon (items, spells, skills, classes), else the quality-coloured letter of an item, else the glyph of the kind. A gold ring on an elite creature.
        FrameworkElement HeroTile(AtlasPageModel m)
        {
            var tile = new Border { Width = 72, Height = 72, CornerRadius = new CornerRadius(12), Tag = "hero-tile", BorderThickness = new Thickness(2) };
            tile.SetResourceReference(Border.BackgroundProperty, "Brush.Surface2");
            tile.BorderBrush = Brushes.Transparent;
            if (m.Kind == "item" && m.Quality >= 0 && m.Quality < ItemLinks.QualityHex.Length) tile.BorderBrush = Lodge.Avatar.Frozen(ItemLinks.QualityHex[m.Quality]);
            else if (m.Kind == "npc" && m.Facts.Any(f => f.Label == "Rank" && (f.Value == "Elite" || f.Value == "Rare Elite" || f.Value == "Boss"))) tile.SetResourceReference(Border.BorderBrushProperty, "Brush.GameTip.Gold");
            var g = new Grid();
            var row = new AtlasRow { Title = m.Title, Quality = m.Quality, IconId = m.IconId, Link = m.Id };
            if (m.IconId > 0 || (m.Kind == "item" && !m.NotFound))
            {
                g.Children.Add(new TextBlock { Text = row.Initial, FontSize = 32, FontWeight = FontWeights.SemiBold, Foreground = row.TileBrush, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
                if (m.IconId > 0)
                {
                    var img = new Image { Stretch = Stretch.UniformToFill };
                    RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                    img.SetBinding(Image.SourceProperty, new System.Windows.Data.Binding("Icon") { Source = row });
                    g.Children.Add(new Border { CornerRadius = new CornerRadius(10), ClipToBounds = true, Child = img });
                }
            }
            else g.Children.Add(new TextBlock { Text = AtlasKinds.Glyph(m.Kind), FontFamily = (FontFamily)FindResource("Font.Icons"), FontSize = 32, Foreground = Res("Brush.Text.Tertiary"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            tile.Child = g;
            return tile;
        }

        // the facts worth a chip, in the order of the page (their value, or a few words with it)
        static readonly Dictionary<string, string> chipFacts = new Dictionary<string, string>
        {
            ["Level"] = "Level {0}", ["Requires level"] = "Requires level {0}", ["Rank"] = "{0}", ["Type"] = "{0}", ["Kind"] = "{0}", ["Family"] = "{0}", ["Services"] = "{0}",
            ["Side"] = "{0}", ["Roles"] = "{0}", ["Resource"] = "{0}", ["Players"] = "{0} players", ["Suggested level"] = "Level {0}", ["Starts at level"] = "Starts at level {0}",
            ["Creature levels"] = "Creature levels {0}", ["Cost"] = "{0}", ["Cast time"] = "Cast {0}", ["Cooldown"] = "{0} cooldown", ["Range"] = "{0}", ["Zone"] = "{0}",
            ["In"] = "In {0}", ["Part of"] = "Part of {0}",
        };

        List<KeyValuePair<string, Brush>> HeroChips(AtlasPageModel m)
        {
            var l = new List<KeyValuePair<string, Brush>>();
            void Add(string text, Brush fg = null) { if (!string.IsNullOrWhiteSpace(text) && l.Count < 6 && !l.Any(x => x.Key == text)) l.Add(new KeyValuePair<string, Brush>(text, fg)); }
            if (m.Kind == "item")
            {
                if (m.Quality >= 0 && m.Quality < ItemLinks.QualityNames.Length) Add(ItemLinks.QualityNames[m.Quality], m.Quality >= 2 || m.Quality == 0 ? heroBrush : null);
                foreach (var t in m.Tooltip)
                {
                    switch (t.Kind)
                    {
                        case "ilvl": Add(t.Text); break;
                        case "slot": Add(string.IsNullOrEmpty(t.Right) ? t.Text : t.Text + " " + t.Right); break;
                        case "req": Add(t.Text); break;
                        case "bind": Add(t.Text); break;
                        case "price": Add("Sells for " + AtlasFormat.Money(t.Copper)); break;
                    }
                }
                return l;
            }
            foreach (var f in m.Facts)
            {
                if (!chipFacts.TryGetValue(f.Label, out var fmt) || string.IsNullOrWhiteSpace(f.Value)) continue;
                Add(string.Format(fmt, f.Value));
            }
            return l;
        }

        // ============================================================================================================================ the tabs

        // which tabs the page has: Overview always; Sources / Related / Lodge when a table of theirs has rows; Map when there is a map to show
        void PlanTabs(AtlasPageModel m)
        {
            tabsShown.Clear();
            tabsShown.Add(AtlasTabs.Overview);
            foreach (var t in new[] { AtlasTabs.Sources, AtlasTabs.Map, AtlasTabs.Related, AtlasTabs.Lodge })
            {
                bool has = t == AtlasTabs.Map ? HasMapTab(m) : SectionsOn(m, t).Any();
                if (has) tabsShown.Add(t);
            }
            tabDefault = tabsShown.Contains(AtlasTabs.DefaultFor(m.Kind)) ? AtlasTabs.DefaultFor(m.Kind) : AtlasTabs.Overview;
        }

        static IEnumerable<AtlasSection> SectionsOn(AtlasPageModel m, string tab) => m.Sections.Where(s => s.Open && AtlasTabs.OfSection(s.Key) == tab && !(m.Kind == "item" && s.Key == "owned"));

        int TabCount(string tab)
        {
            if (model == null) return 0;
            if (tab == AtlasTabs.Overview || tab == AtlasTabs.Map) return 0;
            return SectionsOn(model, tab).Sum(s => s.Rows.Count + s.Hidden.Count);
        }

        FrameworkElement TabStrip(AtlasPageModel m)
        {
            var strip = new StackPanel { Orientation = Orientation.Horizontal };
            var line = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(0, 0, 0, 12), Visibility = tabsShown.Count > 1 ? Visibility.Visible : Visibility.Collapsed, Tag = "tabs" };
            line.SetResourceReference(Border.BorderBrushProperty, "Brush.Divider");
            tabButtons.Clear();
            suppressTab = true;
            foreach (var key in tabsShown)
            {
                var rb = new RadioButton { Style = (Style)FindResource("EntityTab"), GroupName = "atlas-entity-tab", Tag = key, IsChecked = key == tabNow };
                var content = new StackPanel { Orientation = Orientation.Horizontal };
                content.Children.Add(Tb(AtlasTabs.Title(key), "Text.Body", null));
                ((TextBlock)content.Children[0]).FontWeight = FontWeights.SemiBold;
                ((TextBlock)content.Children[0]).SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("Foreground") { Source = rb });
                int n = TabCount(key);
                if (n > 0) { var c = Tb(n.ToString("N0", Lodge.CalendarRules.English), "Text.Caption", Res("Brush.Text.Tertiary")); c.Margin = new Thickness(4, 0, 0, 0); c.VerticalAlignment = VerticalAlignment.Center; content.Children.Add(c); }
                rb.Content = content;
                System.Windows.Automation.AutomationProperties.SetName(rb, AtlasTabs.Title(key) + (n > 0 ? " " + n : ""));
                var k = key;
                rb.Checked += (s, e) => { if (!suppressTab) SelectTab(k); };
                tabButtons[key] = rb;
                strip.Children.Add(rb);
            }
            suppressTab = false;
            line.Child = strip;
            return line;
        }



        FrameworkElement TabContentFor(string tab)
        {
            if (tabContent.TryGetValue(tab, out var have)) return have;
            var built = BuildTab(tab);
            tabContent[tab] = built;
            return built;
        }

        FrameworkElement BuildTab(string tab)
        {
            var m = model;
            var col = new StackPanel();
            switch (tab)
            {
                case AtlasTabs.Overview:
                    {
                        col.Children.Add(ItemCard(m));       // every kind: the game tooltip is the page's main card
                        if (!railOn && factsCard != null) col.Children.Add(factsCard);
                        if (m.Custom is ClassSpellBook classSpells) col.Children.Add(ClassSpellsCard(classSpells));
                        if (m.Entrance != null) col.Children.Add(EntranceCard(m));
                        var owned = m.Kind == "item" ? m.Section("owned") : null;
                        if (owned != null && (owned.Open || owned.Empty.Length > 0)) col.Children.Add(SectionCard(owned));
                        foreach (var s in SectionsOn(m, AtlasTabs.Overview)) col.Children.Add(SectionCard(s));        // a quest's rewards and what it asks for
                        var best = BestWaysCard(m);
                        if (best != null) col.Children.Add(best);
                        // what has nothing to list yet still says so (the sources of an unknown item, the map of a thing with no places): here, with the rest of the page's story
                        foreach (var s in m.Sections)
                            if (!s.Open && s.Empty.Length > 0 && !(m.Kind == "item" && s.Key == "owned")) col.Children.Add(SectionCard(s));
                        if (!HasMapTab(m) && MapWanted(m)) col.Children.Add(MapCard(m));       // no map tab: the page says what it knows about its places (or that it knows none) right here
                        foreach (var n in m.Notes) col.Children.Add(Note(n));
                        break;
                    }
                case AtlasTabs.Map:
                    col.Children.Add(MapCard(m, 480));
                    break;
                default:
                    foreach (var s in SectionsOn(m, tab)) col.Children.Add(SectionCard(s));
                    break;
            }
            return col;
        }

        static bool MapWanted(AtlasPageModel m) => m.Map.Any || m.Kind == "item" || m.Kind == "npc" || m.Kind == "obj" || m.Kind == "quest";

        // "Best ways to get it": the first three rows of an item's sources, and a way to all of them
        FrameworkElement BestWaysCard(AtlasPageModel m)
        {
            if (m.Kind != "item") return null;
            var s = m.Section("sources");
            if (s == null || !s.Open) return null;
            var rows = s.Rows.Count > 0 ? s.Rows.Take(3).ToList() : s.Hidden.Take(3).ToList();
            int total = s.Rows.Count + s.Hidden.Count;
            var sp = new StackPanel();
            var head = new DockPanel();
            var c = Tb(total.ToString("N0", Lodge.CalendarRules.English), "Text.Caption", Res("Brush.Text.Tertiary")); c.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(c, Dock.Right); head.Children.Add(c);
            head.Children.Add(Tb("Best ways to get it", "Text.Section"));
            sp.Children.Add(head);
            var host = new ContentControl { Margin = new Thickness(-8, 8, -8, 0), Focusable = false };
            host.Content = new ItemsControl { ItemsSource = rows, ItemTemplate = (DataTemplate)FindResource("AtlasRowTemplate"), Focusable = false };
            sp.Children.Add(host);
            if (total > rows.Count && tabsShown.Contains(AtlasTabs.Sources))
            {
                var all = GhostButton("All " + total.ToString("N0", Lodge.CalendarRules.English) + " sources", () => SelectTab(AtlasTabs.Sources), "Open the Sources tab");
                all.Margin = new Thickness(0, 4, 0, 0); all.Tag = "all-sources";
                sp.Children.Add(all);
            }
            return Card(sp);
        }

        // the quick facts (what links somewhere); an item's own facts (crafted by ...) are all shown. Null when there is none.
        FrameworkElement FactsFor(AtlasPageModel m)
        {
            bool any = m.Kind == "item" ? m.Facts.Count > 0 : m.Facts.Any(f => f.Link != null);
            return any ? FactsCard(m, m.Kind != "item") : null;
        }

        static bool HasMapTab(AtlasPageModel m) => m.Map.Zones.Count > 0 || m.Map.ShowZone > 0;        // a map of a zone to show (an instance with no picture is a note in the Overview)
        static bool HasRailMap(AtlasPageModel m) => HasMapTab(m);

        // ---- choose a tab (a click, a link with ?tab=, the keyboard): the page stays, the content swaps, the id follows without a new step in the history
        public bool SelectTab(string tab) => SelectTab(tab, true);

        public bool SelectTab(string tab, bool animate)
        {
            if (model == null || entityGrid == null || tabHost == null || !tabsShown.Contains(tab)) return false;
            if (tab == tabNow) return true;
            tabNow = tab;
            tabHost.Content = TabContentFor(tab);
            suppressTab = true;
            foreach (var kv in tabButtons) kv.Value.IsChecked = kv.Key == tab;
            suppressTab = false;
            UpdateRailMap();
            ApplyRailWidth();
            SyncIdToTab(true);
            Scroller.ScrollToTop();
            if (animate) Motion.PageIn(tabHost);
            return true;
        }

        // the id of the page names the tab shown (the default tab leaves it out)
        void SyncIdToTab(bool navigated)
        {
            if (model == null) return;
            var want = tabNow == tabDefault ? model.Id : AtlasTabs.WithTab(model.Id, tabNow);
            var old = history.Current ?? "";
            if (old == want) return;
            history.Replace(want);
            NavState.Follow(old, want);
            if (navigated) Navigated?.Invoke(want);
            UpdateNav();
            ColourLastCrumb(heroBrush);            // the crumbs are built again: the last one is the page in its own colour
        }

        // ============================================================================================================================ layout: the rail beside the tab (wide) or the facts inside the Overview (narrow)
        void Layout()
        {
            if (startInfo != null || tileGrids.Count > 0) { LayoutStart(); return; }
            if (entityGrid == null || model == null) return;
            bool hasRail = factsCard != null || HasRailMap(model);
            int m = ColumnModeFor(Scroller.ActualWidth, hasRail);
            if (m == columnMode && entityGrid.ColumnDefinitions.Count > 0) return;
            ApplyLayout(m);
        }

        void ApplyLayout(int mode)
        {
            columnMode = mode; wide = mode > 0; railOn = wide;
            entityGrid.ColumnDefinitions.Clear(); entityGrid.RowDefinitions.Clear();
            if (railOn)
            {
                entityGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                entityGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
                entityGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(RailWidth) });
                Grid.SetColumn(tabHost, 0); Grid.SetRow(tabHost, 0);
                Grid.SetColumn(railStack, 2); Grid.SetRow(railStack, 0);
            }
            else
            {
                entityGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                Grid.SetColumn(tabHost, 0); Grid.SetRow(tabHost, 0); Grid.SetColumn(railStack, 0); Grid.SetRow(railStack, 1);
            }
            PlaceRailItems();
        }

        // wide: the rail holds the map thumbnail and the facts; narrow: the facts are in the Overview (after the tooltip) and the rail is empty
        void PlaceRailItems()
        {
            if (factsCard != null) (factsCard.Parent as Panel)?.Children.Remove(factsCard);
            railStack.Children.Clear();
            railStack.Visibility = railOn ? Visibility.Visible : Visibility.Collapsed;
            if (railOn)
            {
                UpdateRailMap();
                if (railMapHost != null) railStack.Children.Add(railMapHost);
                if (factsCard != null) railStack.Children.Add(factsCard);
            }
            else if (factsCard != null && tabContent.TryGetValue(AtlasTabs.Overview, out var ov) && ov is StackPanel sp)
                sp.Children.Insert(Math.Min(1, sp.Children.Count), factsCard);
            ApplyRailWidth();
        }

        // the rail takes its 340 px only while something is in it (on the Map tab the thumbnail is gone: a page with no facts then has the tab for itself)
        void ApplyRailWidth()
        {
            if (!railOn || entityGrid == null || entityGrid.ColumnDefinitions.Count < 3) return;
            bool any = factsCard != null || (railMapHost != null && railMapHost.Visibility == Visibility.Visible);
            entityGrid.ColumnDefinitions[1].Width = new GridLength(any ? 12 : 0);
            entityGrid.ColumnDefinitions[2].Width = new GridLength(any ? RailWidth : 0);
            railStack.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        }

        // the thumbnail of the map in the rail: made the first time the rail is shown, hidden on the Map tab (it is the map there); a click opens the tab
        void UpdateRailMap()
        {
            if (model == null || !railOn || !HasRailMap(model) || !tabsShown.Contains(AtlasTabs.Map)) { if (railMapHost != null) railMapHost.Visibility = Visibility.Collapsed; return; }
            if (railMapHost == null)
            {
                var plan = model.Map;
                var sp = new StackPanel();
                sp.Children.Add(Tb("Where", "Text.Section"));
                railMap = new AtlasMap { Height = 180, Margin = new Thickness(0, 12, 0, 0), Compact = true };
                var z = plan.ShowZone > 0 ? plan.Zones.FirstOrDefault(x => x.UiMapId == plan.ShowZone) ?? new AtlasMapZone { UiMapId = plan.ShowZone, Name = AtlasQuery.ZoneName(plan.ShowZone) } : plan.Best ?? plan.Zones.FirstOrDefault();
                var mp = railMap;
                if (z != null) Defer(() => { mp.ShowMap(z.UiMapId); mp.AddMarkers(z.Markers); });
                var hit = new Grid();
                hit.Children.Add(railMap);
                var veil = new Border { Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = "Open the map", Tag = "rail-map-open" };
                veil.MouseLeftButtonUp += (s, e) => { e.Handled = true; SelectTab(AtlasTabs.Map); };
                hit.Children.Add(veil);
                sp.Children.Add(hit);
                var open = GhostButton("Open the map", () => SelectTab(AtlasTabs.Map), "Open the Map tab");
                open.Margin = new Thickness(0, 8, 0, 0); open.Tag = "rail-map-button";
                sp.Children.Add(open);
                railMapHost = Card(sp);
            }
            railMapHost.Visibility = tabNow == AtlasTabs.Map ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
