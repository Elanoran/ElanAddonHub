using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Maps;

namespace ElansAddonHub
{
    // Draws an AtlasPageModel: identity card (tooltip / facts) on the left, tables and the map on the right; one column when the page is narrow.
    public partial class AtlasPage
    {
        // ---- small builders (tokens only: no raw colours, sizes from the type ramp, spacing on the 4-pt grid)
        static Brush Res(string key) => (Brush)Application.Current.FindResource(key);
        TextBlock Tb(string text, string style, Brush fg = null, double top = 0)
        {
            var t = new TextBlock { Text = text ?? "", Style = (Style)FindResource(style), Margin = new Thickness(0, top, 0, 0) };
            if (fg != null) t.Foreground = fg;
            return t;
        }

        Border Card(UIElement child, double bottom = 12)
        {
            var b = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(16), Margin = new Thickness(0, 0, 0, bottom), Child = child };
            b.SetResourceReference(Border.BackgroundProperty, "Brush.Surface1");
            return b;
        }

        Border Note(string text)
        {
            var t = Tb(text, "Text.Secondary"); t.TextWrapping = TextWrapping.Wrap; t.MaxWidth = ReadWidth;
            UIElement child = t;
            if (Services.Atlas.ClassicSetup.IsGetPrompt(text) && !Services.Atlas.Classic.ClassicAtlas.IsReady)
            {
                // the note says the Classic baseline is missing: the way to get it is right there
                var row = new DockPanel();
                var get = new Button { Content = Services.Atlas.ClassicSetup.GetButtonText, Style = (Style)FindResource("Button.S"), Tag = "classic-get", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
                get.Click += (s, e) => StartClassicDownload();
                DockPanel.SetDock(get, Dock.Right);
                row.Children.Add(get); row.Children.Add(t);
                child = row;
            }
            var b = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 8), Child = child };
            b.SetResourceReference(Border.BackgroundProperty, "Brush.Surface2");
            return b;
        }

        Button GhostButton(string text, Action click, string tip = null)
        {
            var b = new Button { Content = text, Style = (Style)FindResource("GhostButton"), Height = 28, HorizontalAlignment = HorizontalAlignment.Left };
            if (tip != null) b.ToolTip = tip;
            b.Click += (s, e) => click();
            return b;
        }

        public const double ReadWidth = 760;   // the longest line of a paragraph

        // ---- layout: one column when narrow, two above 700 px, three (identity | tables | map) above 1500 px when the page has a map. Only a change of mode places the elements again.
        Grid cols; StackPanel leftCol, rightCol, mapCol;
        FrameworkElement mapCardEl; int mapIndex;
        public const double TwoColumnWidth = 700, ThreeColumnWidth = 1500, LeftWidth = 300, LeftWidthWide = 340;
        bool wide; int columnMode = -1;
        public int ColumnsForTest => columnMode < 0 ? 0 : columnMode + 1;

        public static int ColumnModeFor(double scrollerWidth, bool hasMap) => scrollerWidth >= ThreeColumnWidth && hasMap ? 2 : scrollerWidth >= TwoColumnWidth ? 1 : 0;

        void Layout()
        {
            if (startInfo != null) { LayoutStart(); return; }
            if (cols == null || leftCol == null || rightCol == null) return;
            int m = ColumnModeFor(Scroller.ActualWidth, mapCardEl != null);
            if (m == columnMode && cols.ColumnDefinitions.Count > 0) return;
            ApplyLayout(m);
        }

        void ApplyLayout(int mode)
        {
            columnMode = mode; wide = mode > 0;
            cols.ColumnDefinitions.Clear(); cols.RowDefinitions.Clear();
            // the map card lives in the right column, or in a column of its own when there are three
            if (mapCardEl != null && mapCol != null)
            {
                var from = mapCardEl.Parent as Panel;
                if (mode == 2 && from != mapCol) { from?.Children.Remove(mapCardEl); mapCol.Children.Add(mapCardEl); }
                else if (mode != 2 && from == mapCol) { mapCol.Children.Remove(mapCardEl); rightCol.Children.Insert(Math.Min(mapIndex, rightCol.Children.Count), mapCardEl); }
            }
            if (mode >= 1)
            {
                double lw = Scroller.ActualWidth >= 1000 ? LeftWidthWide : LeftWidth;
                cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(lw) });
                cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
                cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
                Grid.SetColumn(leftCol, 0); Grid.SetRow(leftCol, 0);
                Grid.SetColumn(rightCol, 2); Grid.SetRow(rightCol, 0);
                if (mode == 2)
                {
                    cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
                    cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
                    Grid.SetColumn(mapCol, 4); Grid.SetRow(mapCol, 0);
                }
            }
            else
            {
                cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                cols.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                cols.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetColumn(leftCol, 0); Grid.SetRow(leftCol, 0);
                Grid.SetColumn(rightCol, 0); Grid.SetRow(rightCol, 1);
            }
            leftCol.Visibility = leftCol.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (mapCol != null) mapCol.Visibility = mode == 2 ? Visibility.Visible : Visibility.Collapsed;
        }

        public bool WideForTest => wide;
        public FrameworkElement BodyForTest => Body;
        public IReadOnlyList<AtlasMap> MapsForTest => maps;
        readonly List<AtlasMap> maps = new List<AtlasMap>();

        void ShowSkeleton(string id)
        {
            ReleaseMaps();
            Body.Children.Clear();
            cols = null; leftCol = rightCol = null; startInfo = null; tileGrid = null; mapCol = null; mapCardEl = null;
            if (AtlasBrowse.IsBrowseId(id)) { BrowseQuery.TryParse(id, out var sq); TitleText.Text = AtlasCategories.Find(sq?.Cat)?.Title ?? "Browse"; }
            else TitleText.Text = AtlasKinds.Singular(id.Contains(":") ? id.Substring(0, id.IndexOf(':')) : "") + " " + (id.Contains(":") ? id.Substring(id.IndexOf(':') + 1) : id);
            SubText.Text = "";
            var p = new StackPanel();
            p.Children.Add(new Skeleton { Height = 120, CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 0, 12) });
            for (int i = 0; i < 3; i++) p.Children.Add(new Skeleton { Height = 48, CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 0, 8) });
            Body.Children.Add(p);
        }

        void ReleaseMaps()
        {
            foreach (var m in maps) { try { m.ClearMarkers(); } catch { } }
            maps.Clear(); zonePicks.Clear(); routeButtons.Clear(); worldMapButtons.Clear(); routePanels.Clear();
        }

        // ============================================================================================================================ a page
        void Draw(AtlasPageModel m)
        {
            model = m; browse = null; hosted = null; browseList = null; drawnSig = Signature();
            ReleaseMaps();
            Body.Children.Clear();
            cols = null; leftCol = rightCol = null; startInfo = null; tileGrid = null; mapCol = null; mapCardEl = null;
            TitleText.Text = string.IsNullOrEmpty(m.Title) ? "Atlas" : m.Title;
            TitleText.Foreground = m.Kind == "item" && m.Quality >= 0 && m.Quality < ItemLinks.QualityHex.Length ? Lodge.Avatar.Frozen(ItemLinks.QualityHex[m.Quality]) : Res("Brush.Text.Primary");
            SubText.Text = m.NotFound ? AtlasKinds.Singular(m.Kind) : m.Subtitle;
            UpdateNav();

            if (m.NotFound)
            {
                Body.Children.Add(EmptyCard("", m.Message.Length > 0 ? m.Message : "Nothing is known about this.", "Search for a name above, or open the start page to see what data Atlas has."));
                foreach (var n in m.Notes) Body.Children.Add(Note(n));
                foreach (var s in m.Sections.Where(s => s.Open)) Body.Children.Add(SectionCard(s));
                Motion.PageIn(Body);
                return;
            }

            cols = new Grid(); leftCol = new StackPanel(); rightCol = new StackPanel(); mapCol = new StackPanel(); columnMode = -1;
            cols.Children.Add(leftCol); cols.Children.Add(rightCol); cols.Children.Add(mapCol);
            Body.Children.Add(cols);

            // left: who it is
            leftCol.Children.Add(ItemCard(m));       // every kind: the game tooltip is the page's main card
            if (m.Kind != "item" && m.Facts.Any(f => f.Link != null)) leftCol.Children.Add(FactsCard(m, true));     // what links somewhere stays clickable, in the app style
            if (m.Kind == "item" && m.Section("owned") != null) leftCol.Children.Add(SectionCard(m.Section("owned")));

            // right: tables and the map
            var mapCard = m.Map.Any || m.Kind == "item" || m.Kind == "npc" || m.Kind == "obj" || m.Kind == "quest" ? MapCard(m) : null;
            if (m.MapFirst && mapCard != null) rightCol.Children.Add(mapCard);
            bool mapPlaced = m.MapFirst;
            foreach (var s in m.Sections)
            {
                if (m.Kind == "item" && s.Key == "owned") continue;
                if (!s.Open && s.Empty.Length == 0) continue;
                rightCol.Children.Add(SectionCard(s));
                // an item's map comes right after the sources table
                if (!mapPlaced && s.Key == "sources" && mapCard != null) { rightCol.Children.Add(mapCard); mapPlaced = true; }
            }
            if (!mapPlaced && mapCard != null) rightCol.Children.Insert(0, mapCard);
            foreach (var n in m.Notes) rightCol.Children.Add(Note(n));
            mapCardEl = m.Map.Any ? mapCard : null; mapIndex = mapCardEl == null ? 0 : rightCol.Children.IndexOf(mapCardEl);
            ApplyLayout(ColumnModeFor(Scroller.ActualWidth, mapCardEl != null));
            Scroller.ScrollToTop();
            Motion.PageIn(Body);
        }

        FrameworkElement EmptyCard(string glyph, string line, string hint)
        {
            var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 460, Margin = new Thickness(0, 16, 0, 16) };
            sp.Children.Add(new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("Font.Icons"), FontSize = 32, Foreground = Res("Brush.Text.Tertiary"), HorizontalAlignment = HorizontalAlignment.Center });
            var l = Tb(line, "Text.Body", null, 8); l.TextWrapping = TextWrapping.Wrap; l.TextAlignment = TextAlignment.Center; sp.Children.Add(l);
            var h = Tb(hint, "Text.Secondary", null, 4); h.TextWrapping = TextWrapping.Wrap; h.TextAlignment = TextAlignment.Center; sp.Children.Add(h);
            return Card(sp);
        }

        // ---- the main card of a page: the game's own tooltip (item, creature, quest ...), the icon outside it on the left; the Outpost's own bits below in the app style
        FrameworkElement ItemCard(AtlasPageModel m)
        {
            var gt = GameTooltip.FromAtlas(m);
            var sp = new StackPanel();
            FrameworkElement icon = null;
            if (m.Kind == "item" || m.IconId > 0) icon = IconBox(m);
            sp.Children.Add(icon != null ? GameTooltip.WithIcon(icon, gt) : gt);
            if (m.TooltipSource.Length > 0) sp.Children.Add(SourceChip(m.TooltipSource, 12));
            return Card(sp);
        }

        // the 56 px icon: the picture when it is read, else the quality-coloured letter
        FrameworkElement IconBox(AtlasPageModel m)
        {
            var row = new AtlasRow { Title = m.Title, Quality = m.Quality, IconId = m.IconId, Link = m.Id };
            var tg = new Grid();
            tg.Children.Add(new TextBlock { Text = row.Initial, FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = row.TileBrush, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            if (m.IconId > 0)
            {
                var img = new Image { Stretch = Stretch.UniformToFill };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                img.SetBinding(Image.SourceProperty, new System.Windows.Data.Binding("Icon") { Source = row });
                tg.Children.Add(img);
            }
            return tg;
        }

        FrameworkElement SourceChip(string label, double top = 0)
        {
            var b = new Border { Style = (Style)FindResource("Chip"), Padding = new Thickness(8, 0, 8, 0), MinHeight = 20, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, top, 0, 0) };
            var t = Tb(label, "Text.Caption"); t.VerticalAlignment = VerticalAlignment.Center; b.Child = t;
            return b;
        }

        // ---- facts (label, value, where it comes from) and the longer texts
        FrameworkElement FactsCard(AtlasPageModel m, bool linksOnly = false)
        {
            var sp = new StackPanel();
            foreach (var f in m.Facts.Where(f => !linksOnly || f.Link != null))
            {
                var g = new Grid { Margin = new Thickness(0, sp.Children.Count == 0 ? 0 : 8, 0, 0) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.Children.Add(Tb(f.Label, "Text.Secondary"));
                var v = Tb(f.Value, "Text.Body"); v.TextWrapping = TextWrapping.Wrap;
                var col = new StackPanel();
                if (!string.IsNullOrEmpty(f.Link))
                {
                    var link = f.Link;
                    var btn = new TextBlock { Text = f.Value, Style = (Style)FindResource("Text.Body"), TextWrapping = TextWrapping.Wrap, Cursor = Cursors.Hand, Tag = link, ToolTip = "Open " + f.Value };
                    btn.TextDecorations = null;
                    btn.MouseEnter += (s, e) => btn.TextDecorations = TextDecorations.Underline;
                    btn.MouseLeave += (s, e) => btn.TextDecorations = null;
                    btn.MouseLeftButtonUp += Row_Click;
                    col.Children.Add(btn);
                }
                else col.Children.Add(v);
                if (f.Source.Length > 0) { var s = Tb(f.Source, "Text.Caption"); s.Foreground = Res("Brush.Text.Tertiary"); col.Children.Add(s); }
                Grid.SetColumn(col, 1); g.Children.Add(col);
                sp.Children.Add(g);
            }
            foreach (var t in linksOnly ? Enumerable.Empty<AtlasText>() : m.Texts)
            {
                sp.Children.Add(Tb(t.Title, "Text.Secondary", null, sp.Children.Count == 0 ? 0 : 16));
                var b = Tb(t.Body, "Text.Body", null, 2); b.TextWrapping = TextWrapping.Wrap; sp.Children.Add(b);
                if (t.Source.Length > 0) { var s = Tb(t.Source, "Text.Caption", Res("Brush.Text.Tertiary")); sp.Children.Add(s); }
            }
            return Card(sp);
        }

        // ============================================================================================================================ tables
        public const int VirtualFrom = 40;

        FrameworkElement SectionCard(AtlasSection s)
        {
            var sp = new StackPanel();
            var head = new DockPanel();
            int total = s.Rows.Count + s.Hidden.Count;
            if (total > 0 && s.Hidden.Count == 0)
            {
                var c = Tb(total.ToString("N0", Lodge.CalendarRules.English), "Text.Caption", Res("Brush.Text.Tertiary")); c.VerticalAlignment = VerticalAlignment.Center;
                DockPanel.SetDock(c, Dock.Right); head.Children.Add(c);
            }
            head.Children.Add(Tb(s.Title, "Text.Section"));
            sp.Children.Add(head);
            if (!string.IsNullOrEmpty(s.Note)) { var n = Tb(s.Note, "Text.Secondary", null, 2); n.TextWrapping = TextWrapping.Wrap; n.MaxWidth = ReadWidth; n.HorizontalAlignment = HorizontalAlignment.Left; sp.Children.Add(n); }
            if (s.Rows.Count == 0 && s.Hidden.Count == 0)
            {
                var e = Tb(s.Empty, "Text.Secondary", null, 8); e.TextWrapping = TextWrapping.Wrap; sp.Children.Add(e);
                return Card(sp);
            }
            var host = new ContentControl { Margin = new Thickness(-8, 8, -8, 0), Focusable = false };
            var shown = new List<AtlasRow>(s.Rows);
            host.Content = RowsView(shown);
            sp.Children.Add(host);
            if (s.Hidden.Count > 0)
            {
                bool open = false;
                var fold = new Button { Style = (Style)FindResource("GhostButton"), Height = 28, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
                var label = new TextBlock { Text = s.HiddenText.Length > 0 ? s.HiddenText : "+ " + s.Hidden.Count + " more", Style = (Style)FindResource("Text.Secondary") };
                fold.Content = label;
                fold.ToolTip = "Show or hide these rows";
                fold.Click += (a, b) =>
                {
                    open = !open;
                    var list = new List<AtlasRow>(s.Rows);
                    if (open) list.AddRange(s.Hidden);
                    host.Content = RowsView(list);
                    label.Text = open ? "Hide those " + s.Hidden.Count.ToString("N0", Lodge.CalendarRules.English) : s.HiddenText.Length > 0 ? s.HiddenText : "+ " + s.Hidden.Count + " more";
                };
                fold.Tag = "fold";
                sp.Children.Add(fold);
            }
            else if (!string.IsNullOrEmpty(s.HiddenText)) sp.Children.Add(Tb(s.HiddenText, "Text.Caption", Res("Brush.Text.Tertiary"), 4));
            return Card(sp);
        }

        // short tables are plain; long ones are a virtualised list with its own scroll
        FrameworkElement RowsView(List<AtlasRow> rows)
        {
            var tpl = (DataTemplate)FindResource("AtlasRowTemplate");
            if (rows.Count > VirtualFrom)
            {
                var lb = new ListBox { Style = (Style)FindResource("AtlasList"), ItemsSource = rows, MaxHeight = 440 };
                lb.PreviewMouseWheel += (s, e) =>
                {
                    // at the end of the inner list the wheel scrolls the page
                    var sv = FindScroll(lb);
                    if (sv == null) return;
                    bool atTop = sv.VerticalOffset <= 0 && e.Delta > 0, atBottom = sv.VerticalOffset >= sv.ScrollableHeight && e.Delta < 0;
                    if (atTop || atBottom) { e.Handled = true; Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset - e.Delta); }
                };
                return lb;
            }
            return new ItemsControl { ItemsSource = rows, ItemTemplate = tpl, Focusable = false };
        }

        static ScrollViewer FindScroll(DependencyObject d)
        {
            if (d == null) return null;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            {
                var c = VisualTreeHelper.GetChild(d, i);
                if (c is ScrollViewer sv) return sv;
                var r = FindScroll(c);
                if (r != null) return r;
            }
            return null;
        }

        // ============================================================================================================================ the map
        FrameworkElement MapCard(AtlasPageModel m)
        {
            var plan = m.Map;
            var sp = new StackPanel();
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.Children.Add(Tb("Map", "Text.Section"));
            sp.Children.Add(head);

            if (!plan.Any)
            {
                var e = Tb(plan.Note.Length > 0 ? plan.Note : "No places are known yet. Spawn points appear here from the Classic baseline, and finds and spawns from the Lodge.", "Text.Secondary", null, 4);
                e.TextWrapping = TextWrapping.Wrap; sp.Children.Add(e);
                AddMapExtras(sp, plan);
                return Card(sp);
            }

            var map = new AtlasMap { Height = 360, Margin = new Thickness(0, 12, 0, 0) };
            maps.Add(map);
            map.LinkClicked += id => { _ = Open(id); };
            AtlasMapZone current = null;
            void ShowZone(AtlasMapZone z)
            {
                current = z;
                map.ClearMarkers();
                if (z != null) { map.ShowMap(z.UiMapId); map.AddMarkers(z.Markers); }
            }
            if (plan.InstanceId > 0 && plan.Zones.Count == 0) { Defer(() => map.ShowInstance(plan.InstanceId)); }
            else if (plan.Zones.Count > 0 && plan.ShowZone == 0)
            {
                var best = plan.Best ?? plan.Zones[0];
                if (plan.Zones.Count > 1)
                {
                    var pick = new ComboBox { Style = (Style)FindResource("Dropdown"), MinWidth = 220, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Zone shown on the map" };
                    foreach (var z in plan.Zones) pick.Items.Add(new ComboBoxItem { Content = z.Name + " · " + z.Markers.Count + (z.Markers.Count == 1 ? " place" : " places"), Tag = z });
                    pick.SelectedIndex = plan.Zones.IndexOf(best);
                    pick.SelectionChanged += (s, e) => { if (pick.SelectedItem is ComboBoxItem it && it.Tag is AtlasMapZone z) ShowZone(z); };
                    Grid.SetColumn(pick, 1); head.Children.Add(pick);
                    zonePicks.Add(pick);
                }
                Defer(() => ShowZone(best));
            }
            else if (plan.ShowZone > 0)
            {
                var z = plan.Zones.FirstOrDefault(x => x.UiMapId == plan.ShowZone) ?? new AtlasMapZone { UiMapId = plan.ShowZone, Name = AtlasQuery.ZoneName(plan.ShowZone) };
                Defer(() => ShowZone(z));
            }
            sp.Children.Add(map);
            sp.Children.Add(Legend(plan));
            // gathering routes: on an object page and a zone page, and wherever the map shows nodes
            if (plan.Zones.Count > 0 && (m.Kind == "obj" || m.Kind == "zone" || plan.Zones.Any(z => z.Markers.Any(k => k.Kind == MarkerKind.Node))))
            {
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var panel = RoutesPanel(m, map, () => map.UiMapId > 0 ? map.UiMapId : (plan.Best ?? plan.Zones[0]).UiMapId, out var rui);
                var rb = GhostButton(m.Kind == "obj" ? "Show route" : "Routes", () => rui.Toggle(m.Kind == "obj"), "Build a gathering loop through the nodes of this zone: pick what to gather, see its length and time, save it");
                rb.Margin = new Thickness(8, 0, 0, 0); rb.Tag = "routes";
                Grid.SetColumn(rb, 2); head.Children.Add(rb);
                routeButtons.Add(rb);
                sp.Children.Add(panel);
                var pend = m.Kind == "zone" && AtlasIds.TryParse(m.Id, out _, out var zid) ? GatherNodes.TakePending(zid) : null;
                if (pend != null) { rui.Preselect = pend.Objects; Defer(() => rui.Toggle(false)); }
            }
            // "Show on world map": the same places on the whole continent (not for an instance or a battleground: they have no continent)
            {
                string WorldMapIdNow()
                {
                    int ui = map.UiMapId > 0 ? map.UiMapId : plan.ShowZone > 0 ? plan.ShowZone : (plan.Best ?? plan.Zones.FirstOrDefault())?.UiMapId ?? 0;
                    var z = plan.Zones.FirstOrDefault(x => x.UiMapId == ui);
                    return ElansAddonHub.Services.WorldMap.WorldMapIds.ShowOnMapId(MapData.Index, ui, m.Kind == "zone" ? null : z?.Markers);
                }
                if (AtlasCategories.Find(ElansAddonHub.Services.WorldMap.WorldMapIds.Category) != null && WorldMapIdNow() != null)
                {
                    head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var wb = GhostButton("Show on world map", () => { var id = WorldMapIdNow(); if (id != null) _ = Open(id); }, "Open the world map of the continent, centred here");
                    wb.Margin = new Thickness(8, 0, 0, 0); wb.Tag = "worldmap";
                    Grid.SetColumn(wb, head.ColumnDefinitions.Count - 1); head.Children.Add(wb);
                    worldMapButtons.Add(wb);
                }
            }
            AddMapExtras(sp, plan);
            return Card(sp);
        }

        public readonly List<Button> worldMapButtons = new List<Button>();
        public readonly List<ComboBox> zonePicks = new List<ComboBox>();
        public readonly List<Button> routeButtons = new List<Button>();

        // the map loads after the rest of the page is drawn (it decodes a picture the first time it is asked for)
        void Defer(Action a) { Dispatcher.BeginInvoke(new Action(a), System.Windows.Threading.DispatcherPriority.Background); }

        FrameworkElement Legend(AtlasMapPlan plan)
        {
            var kinds = plan.Zones.SelectMany(z => z.Markers).Select(m => m.Kind).Distinct().OrderBy(k => (int)k).ToList();
            var w = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            foreach (var k in kinds)
            {
                var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 0) };
                item.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = AtlasMap.KindBrush(k), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
                item.Children.Add(Tb(AtlasMap.KindName(k), "Text.Caption"));
                w.Children.Add(item);
            }
            return w;
        }

        void AddMapExtras(StackPanel sp, AtlasMapPlan plan)
        {
            if (!string.IsNullOrEmpty(plan.Note) && plan.Any) { var n = Tb(plan.Note, "Text.Caption", Res("Brush.Text.Tertiary"), 8); n.TextWrapping = TextWrapping.Wrap; sp.Children.Add(n); }
            if (plan.InInstances.Count > 0)
            {
                var wp = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
                wp.Children.Add(Tb("Also inside:", "Text.Secondary"));
                foreach (var i in plan.InInstances.Take(4))
                {
                    var inst = MapData.Index?.Instance(i.Key);
                    if (inst == null) continue;
                    var b = GhostButton(inst.Name + " (" + i.Value + ")", () => { _ = Open("instance:" + i.Key); }, "No map in the game files: open the instance page");
                    b.Margin = new Thickness(4, 0, 0, 0);
                    wp.Children.Add(b);
                }
                sp.Children.Add(wp);
            }
            if (plan.Unplaced > 0) sp.Children.Add(Tb(AtlasFormat.Count(plan.Unplaced, "spawn point", "spawn points") + " could not be placed on a map.", "Text.Caption", Res("Brush.Text.Tertiary"), 4));
        }
    }
}
