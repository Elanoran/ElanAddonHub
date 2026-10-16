using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Db2;

namespace ElansAddonHub
{
    // The Atlas shell (DESIGN 4, "Atlas shell"): the sub-navigation on the left (built from the AtlasCategories registry, grouped; Recent and Pinned below), the breadcrumb header,
    // the Pin button. Everything here is built from caches (the registry, the counts of the indexes already built, the id lists): it never waits for a page.
    public partial class AtlasPage
    {
        public const double NavWide = 216, NavNarrow = 56, NavCollapseBelow = 1000, RecentsShown = 5;
        AtlasNavState nav;
        Settings navSettings;
        Action navSave;
        readonly DispatcherTimer navSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        bool navCollapsed, shellInited;
        string navSig = "";
        readonly Dictionary<string, ContentControl> navRows = new Dictionary<string, ContentControl>();
        readonly Dictionary<string, string> navRowSection = new Dictionary<string, string>();      // nav key -> its section brush key (Brush.Section.*)
        public string NavKeyNow { get; private set; } = "";

        public AtlasNavState NavState => nav ?? (nav = new AtlasNavState(new List<string>(), new List<string>(), null));

        // the settings hold the two lists (recents, pins); every change is saved a moment later (one write for a burst of clicks)
        public void UseNavSettings(Settings s, Action save)
        {
            navSettings = s; navSave = save;
            if (s != null)
            {
                s.AtlasRecents = s.AtlasRecents ?? new List<string>(); s.AtlasPins = s.AtlasPins ?? new List<string>();
                nav = new AtlasNavState(s.AtlasRecents, s.AtlasPins, NavChanged);
            }
            else nav = new AtlasNavState(new List<string>(), new List<string>(), null);
            UpdateShell();
        }

        void NavChanged()
        {
            if (navSave != null) { navSaveTimer.Stop(); navSaveTimer.Start(); }
            navSig = "";                       // the lists changed: the nav is built again
        }

        // writes the lists now (the page is leaving, or a test wants to see the file)
        public void FlushNav()
        {
            if (!navSaveTimer.IsEnabled) return;
            navSaveTimer.Stop();
            try { navSave?.Invoke(); } catch (Exception e) { Util.Log("atlas nav save: " + e.Message); }
        }

        void InitShell()
        {
            if (shellInited) return;
            shellInited = true;
            navSaveTimer.Tick += (s, e) => FlushNav();
            AtlasCategories.Changed += () => Dispatcher.BeginInvoke(new Action(() => { navSig = ""; UpdateShell(); }));
            SizeChanged += (s, e) => ApplyNavWidth();
            ApplyNavWidth();
        }

        // wide: the 216 px nav; narrow (the page is under 1000 px): a 56 px strip of icons with tooltips
        void ApplyNavWidth()
        {
            bool narrow = ActualWidth > 0 && ActualWidth < NavCollapseBelow;
            if (narrow == navCollapsed && NavCol.Width.Value == (narrow ? NavNarrow : NavWide)) return;
            navCollapsed = narrow;
            NavCol.Width = new GridLength(narrow ? NavNarrow : NavWide);
            navSig = "";
            if (shellInited) UpdateShell();
        }

        public bool NavCollapsedForTest => navCollapsed;
        public double NavWidthForTest => NavCol.ActualWidth;
        public IReadOnlyList<string> NavGroupsForTest => AtlasWalk(NavStack).OfType<TextBlock>().Where(t => t.Tag as string == "group").Select(t => t.Text).ToList();
        public IReadOnlyList<string> NavRowsForTest => navRows.Values.Select(r => System.Windows.Automation.AutomationProperties.GetName(r)).ToList();
        public IReadOnlyList<string> NavRecentsForTest => AtlasWalk(NavStack).OfType<ContentControl>().Where(c => c.Tag is string t && t.StartsWith("recent|")).Select(c => ((string)c.Tag).Substring(7)).ToList();
        public IReadOnlyList<string> NavPinsForTest => AtlasWalk(NavStack).OfType<ContentControl>().Where(c => c.Tag is string t && t.StartsWith("pin|")).Select(c => ((string)c.Tag).Substring(4)).ToList();
        public bool NavRowSelected(string key) => navRows.TryGetValue(key, out var r) && r.Tag is string t && t.EndsWith("|on", StringComparison.Ordinal);
        public IReadOnlyList<string> NavSelectedForTest => navRows.Where(kv => NavRowSelected(kv.Key)).Select(kv => kv.Key).ToList();
        public ContentControl NavRowForTest(string key) => navRows.TryGetValue(key, out var r) ? r : null;
        public string NavRowSectionForTest(string key) => navRowSection.TryGetValue(key, out var s) ? s : null;
        // the group label's text block (its Foreground is the section hue)
        public TextBlock NavGroupLabelForTest(string group) => AtlasWalk(NavStack).OfType<TextBlock>().FirstOrDefault(t => t.Tag as string == "group" && t.Text == group);
        public FrameworkElement NavBackdropForTest => NavBackdrop;
        public IReadOnlyList<string> NavSubRowsForTest => AtlasWalk(NavStack).OfType<ContentControl>().Where(c => c.Tag is string t && t.StartsWith("sub|")).Select(c => ((string)c.Tag).Substring(4).Split('|')[0]).ToList();
        public FrameworkElement NavScroller_ForTest => NavPanel;
        public void UpdateNavForTest() { navSig = ""; BuildNav(); }
        public Button PinButtonForTest => PinButton;
        public bool PinShownForTest => PinButton.Visibility == Visibility.Visible;
        public bool PinnedNowForTest => IsPinnedNow;
        public string CrumbsForTest => CrumbHost.Content is DependencyObject d ? string.Join(" > ", AtlasWalk(d).OfType<TextBlock>().Where(t => t.Text != "›").Select(t => t.Text)) : "";
        public FrameworkElement CrumbHostForTest => CrumbHost;

        bool IsPinnedNow => !string.IsNullOrEmpty(history.Current) && NavState.IsPinned(history.Current);

        // ---- everything the shell shows follows the page: crumbs, Pin, the nav's highlight and lists
        void UpdateShell()
        {
            if (!shellInited) return;
            UpdateCrumbs();
            UpdatePin();
            BuildNav();
        }

        void UpdatePin()
        {
            bool has = !string.IsNullOrEmpty(history.Current);
            PinButton.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            bool pinned = has && NavState.IsPinned(history.Current);
            PinButton.Content = pinned ? "" : "";
            PinButton.ToolTip = pinned ? "Unpin this page" : "Pin this page: it stays in the list on the left";
            System.Windows.Automation.AutomationProperties.SetName(PinButton, pinned ? "Unpin page" : "Pin page");
        }

        void Pin_Click(object sender, RoutedEventArgs e) => TogglePinCurrent();

        public bool TogglePinCurrent()
        {
            var id = history.Current ?? "";
            if (id.Length == 0) return false;
            bool now = NavState.TogglePin(id);
            UpdateShell();
            return now;
        }

        // ---- the breadcrumb: Atlas > Items > Weapons ... (every crumb but the last is a link), back / forward / start stay
        void UpdateCrumbs()
        {
            var id = history.Current ?? "";
            CrumbHost.Content = CrumbRow(CrumbsFor(id), 0);
            if (heroBrush != null && model != null && id.Length > 0 && AtlasTabs.SamePage(model.Id, id)) ColourLastCrumb(heroBrush);      // the page itself, in the colour of its name (an item's quality), whenever the crumbs are built
        }

        List<AtlasCrumb> CrumbsFor(string id)
        {
            var l = new List<AtlasCrumb>();
            if (string.IsNullOrEmpty(id)) { l.Add(new AtlasCrumb("Atlas", "")); return l; }
            l.Add(new AtlasCrumb("Atlas", AtlasBrowse.HomeLink));
            if (AtlasNav.IsPage(id)) { l.Add(new AtlasCrumb("Data sources", "")); return l; }
            if (AtlasBrowse.IsBrowseId(id))
            {
                if (browse != null && !browse.NotFound && browse.Id == id && browse.Crumbs.Count > 1) { l.AddRange(browse.Crumbs.Skip(1)); return l; }
                if (BrowseQuery.TryParse(id, out var q)) l.Add(new AtlasCrumb(AtlasCategories.Find(q.Cat)?.Title ?? "Browse", ""));
                return l;
            }
            if (AtlasCategories.TryHosted(id, out var hc, out var sub))
            {
                string last = "";
                try { last = hc.SubTitle?.Invoke(sub) ?? ""; } catch (Exception) { }
                l.Add(new AtlasCrumb(hc.Title, last.Length > 0 ? hc.Id + ":" : ""));
                if (last.Length > 0) l.Add(new AtlasCrumb(last, ""));
                return l;
            }
            if (AtlasIds.TryParse(id, out var kind, out _))
            {
                var cc = AtlasNav.CategoryCrumb(kind);
                if (cc != null) l.Add(cc);
                var bid = AtlasTabs.BaseId(id);             // the tab is not part of the page's identity: item:1?tab=sources is the crumbs of item:1
                bool mine = model != null && model.Id == bid;
                if (mine) foreach (var c in model.Crumbs) if (c.Link != bid) l.Add(c);
                string title = mine && !string.IsNullOrEmpty(model.Title) ? model.Title : AtlasNav.Describe(bid)?.Title ?? bid;
                l.Add(new AtlasCrumb(title, ""));
            }
            return l;
        }

        // ---- the sub-navigation
        // a row you can reach with Tab: Enter or Space does what a click does; the keyboard focus is the Side.Row's own 2 px focus ring (DESIGN: every row shows where the focus is)
        void Keyable(ContentControl row, Action activate, bool selected)
        {
            row.Focusable = true; row.IsTabStop = true; row.FocusVisualStyle = null;
            row.PreviewKeyDown += (s, e) => { if (e.Key == Key.Enter || e.Key == Key.Space) { e.Handled = true; activate(); } };
        }

        // the last crumb is the page itself: in the colour of its title (an item's quality)
        void ColourLastCrumb(Brush b)
        {
            if (b == null || !(CrumbHost.Content is DependencyObject d)) return;
            var last = AtlasWalk(d).OfType<TextBlock>().LastOrDefault(t => t.Text != "›");
            if (last != null && !(last.Cursor == Cursors.Hand)) last.Foreground = b;
        }

        // the section colour of a nav group (DESIGN 4, "Section colours"): Database, World, Character and Lodge have their own hue; Home, Pinned, Recent,
        // Data sources and a group the registry does not know are Neutral
        internal static string SectionBrushKey(string group) => Array.IndexOf(AtlasNav.Groups, group) >= 0 ? "Brush.Section." + group : "Brush.Section.Neutral";

        // a selected row of a secondary menu: its section's tint as the fill and the 4 px bar in the section's hue (Side.Row's BorderBrush)
        internal static void MarkSelected(Control row, string section)
        {
            row.SetResourceReference(Control.BackgroundProperty, section + ".Tint");
            row.SetResourceReference(Control.BorderBrushProperty, section);
        }

        FrameworkElement NavRow(string key, string title, string glyph, string openId, string count, bool selected, string tag = null, double indent = 0, string kbd = null, string icon = null, string section = "Brush.Section.Neutral")
        {
            var row = new ContentControl { Style = (Style)FindResource("Side.Row"), Padding = new Thickness(8, 4, 8, 4), MinHeight = indent > 0 ? 28 : 32, Margin = new Thickness(0, 0, 0, 2), Focusable = false, Cursor = Cursors.Hand };
            row.Tag = (tag ?? key) + (selected ? "|on" : "|off");
            System.Windows.Automation.AutomationProperties.SetName(row, title);
            if (tag == null) navRowSection[key] = section;
            if (selected) MarkSelected(row, section);
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            if (!navCollapsed) { g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); }
            var gl = new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("Font.Icons"), FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            gl.SetResourceReference(TextBlock.ForegroundProperty, section);      // the row's icon carries its section's hue (selected or not); the name stays a text colour
            // a category row draws its own line icon (AtlasIcons) in place of the font glyph
            var drawn = indent > 0 ? null : AtlasIcons.Make(icon, 20, section);
            if (indent > 0) { gl.Visibility = Visibility.Hidden; }
            g.Children.Add(drawn ?? (FrameworkElement)gl);
            if (!navCollapsed)
            {
                var t = Tb(title, "Text.Body"); t.TextTrimming = TextTrimming.CharacterEllipsis; t.VerticalAlignment = VerticalAlignment.Center; t.Margin = new Thickness(indent > 0 ? 4 : 8, 0, 0, 0);
                if (selected) t.FontWeight = FontWeights.SemiBold; else t.Foreground = Res("Brush.Text.Secondary");
                Grid.SetColumn(t, 1); g.Children.Add(t);
                if (!string.IsNullOrEmpty(count) || kbd != null)
                {
                    var c = Tb(kbd ?? count, "Text.Caption", Res("Brush.Text.Tertiary")); c.VerticalAlignment = VerticalAlignment.Center; c.Margin = new Thickness(8, 0, 0, 0);
                    Grid.SetColumn(c, 2); g.Children.Add(c);
                }
            }
            else row.ToolTip = title;
            row.Content = g;
            Action go = () => { if (key == "home") { _ = GoStart(); } else _ = Open(openId); };
            row.MouseLeftButtonUp += (s, e) => { e.Handled = true; go(); };
            Keyable(row, go, selected);
            return row;
        }

        static string CountText(AtlasNavEntry e)
        {
            if (e.Extra || e.Members.Count != 1 || e.Members[0].Hosted) return "";
            int n = AtlasBrowse.CountIfBuilt(e.Members[0].Id);
            return n > 0 ? n.ToString("N0", Lodge.CalendarRules.English) : "";
        }

        string NavSignature(string key)
        {
            var cats = string.Join(",", AtlasCategories.Visible.Select(c => c.Id + "/" + AtlasBrowse.CountIfBuilt(c.Id)));
            return string.Join("|", key, navCollapsed, cats, string.Join(",", NavState.Recents.Take((int)RecentsShown)), string.Join(",", NavState.Pins), AtlasQuery.LodgeHasData());
        }

        void BuildNav()
        {
            if (!shellInited || NavStack == null) return;
            var id = history.Current ?? "";
            var key = AtlasNav.KeyFor(id);
            NavKeyNow = key;
            var sig = NavSignature(key) + "|" + AtlasTabs.BaseId(id);
            if (sig == navSig) return;
            navSig = sig;
            NavStack.Children.Clear(); navRows.Clear(); navRowSection.Clear();
            void AddRow(string k, string title, string glyph, string openId, string count = "", string icon = null, string section = "Brush.Section.Neutral")
            {
                var r = (ContentControl)NavRow(k, title, glyph, openId, count, key == k, null, 0, null, icon, section);
                navRows[k] = r; NavStack.Children.Add(r);
            }
            // no Search row: the title bar box and Ctrl+K are the one way to search (2.71.1)
            AddRow("home", "Home", "", AtlasBrowse.HomeLink);
            var entries = AtlasNav.Entries();
            foreach (var grp in AtlasNav.Groups)
            {
                var list = entries.Where(e => e.Group == grp).ToList();
                if (list.Count == 0) continue;
                var section = SectionBrushKey(grp);
                NavStack.Children.Add(GroupLabel(grp, null, section));
                foreach (var e in list)
                {
                    AddRow(e.Key, e.Title, e.Glyph, e.OpenId, CountText(e), e.Icon, section);
                    // a shared row ("Professions": skills, recipes, the hosted page) lists its members while you are in it
                    if (key == e.Key && e.Members.Count > 1 && !navCollapsed)
                    {
                        var cur = CurrentCategoryId(id);
                        foreach (var m in e.Members.OrderBy(m => m.Order))
                        {
                            var sub = (ContentControl)NavRow(e.Key, m.Title, m.Glyph, m.OpenId, "", cur == m.Id, "sub|" + m.Id, 12, null, null, section);
                            NavStack.Children.Add(sub);
                        }
                    }
                }
            }
            // anything in a group the registry does not know (a plug-in page) is listed after the known ones
            foreach (var e in entries.Where(e => Array.IndexOf(AtlasNav.Groups, e.Group) < 0))
            {
                if (!NavStack.Children.OfType<TextBlock>().Any(t => t.Text == e.Group)) NavStack.Children.Add(GroupLabel(e.Group));
                AddRow(e.Key, e.Title, e.Glyph, e.OpenId, CountText(e), e.Icon);
            }
            // Pinned and Recent
            var pins = NavState.Pins.Select(AtlasNav.Describe).Where(x => x != null).ToList();
            if (pins.Count > 0)
            {
                NavStack.Children.Add(GroupLabel("Pinned"));
                foreach (var p in pins) NavStack.Children.Add(ListedRow(p, true, id));
            }
            var recents = NavState.Recents.Select(AtlasNav.Describe).Where(x => x != null).Take((int)RecentsShown).ToList();
            if (recents.Count > 0)
            {
                var head = GroupLabel("Recent", () => { NavState.ClearRecents(); UpdateShell(); });
                NavStack.Children.Add(head);
                foreach (var r in recents) NavStack.Children.Add(ListedRow(r, false, id));
            }
            // the footer: Data sources
            NavFooter.Children.Clear();
            var data = (ContentControl)NavRow("data", "Data sources", "", AtlasNav.DataId, "", key == "data");
            navRows["data"] = data; NavFooter.Children.Add(data);
        }

        FrameworkElement GroupLabel(string text, Action clear = null, string section = "Brush.Section.Neutral")
        {
            if (navCollapsed)
            {
                var d = new Border { Height = 1, Margin = new Thickness(8, 8, 8, 8) };
                d.SetResourceReference(Border.BackgroundProperty, "Brush.Divider");
                var tag = new TextBlock { Text = text, Tag = "group", Visibility = Visibility.Collapsed };
                return new Grid { Children = { d, tag } };
            }
            var g = new Grid { Margin = new Thickness(8, 12, 0, 4) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var t = Tb(text, "Text.Caption"); t.Tag = "group";
            t.SetResourceReference(TextBlock.ForegroundProperty, section);     // the group's label in its section hue (tinted, as readable as Secondary text)
            g.Children.Add(t);
            if (clear != null)
            {
                var b = new Button { Content = "Clear", Style = (Style)FindResource("GhostButton"), Height = 20, Padding = new Thickness(8, 0, 8, 0), ToolTip = "Clear the list of recent pages" };
                b.Click += (s, e) => clear();
                Grid.SetColumn(b, 1); g.Children.Add(b);
            }
            return g;
        }

        // a Recent or Pinned page: icon tile, title; pinned rows have an unpin button on hover and can be dragged to reorder
        FrameworkElement ListedRow(AtlasNavItem it, bool pinned, string currentId)
        {
            bool on = AtlasTabs.SamePage(it.Id, currentId);
            var row = new ContentControl { Style = (Style)FindResource("Side.Row"), Padding = new Thickness(8, 4, 8, 4), MinHeight = 32, Margin = new Thickness(0, 0, 0, 2), Focusable = false, Cursor = Cursors.Hand };
            row.Tag = (pinned ? "pin|" : "recent|") + it.Id;
            System.Windows.Automation.AutomationProperties.SetName(row, it.Title);
            row.ToolTip = it.Title + (it.Detail.Length > 0 ? "  (" + it.Detail + ")" : "");
            if (on) MarkSelected(row, "Brush.Section.Neutral");             // Pinned and Recent are neutral sections
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            var tile = IconTile(it, 20, 12);
            tile.HorizontalAlignment = HorizontalAlignment.Center;
            g.Children.Add(tile);
            Button x = null;
            if (!navCollapsed)
            {
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var t = Tb(it.Title, "Text.Body"); t.TextTrimming = TextTrimming.CharacterEllipsis; t.VerticalAlignment = VerticalAlignment.Center; t.Margin = new Thickness(8, 0, 0, 0);
                t.Foreground = it.Quality >= 0 && it.Quality < ItemLinks.QualityHex.Length ? Lodge.Avatar.Frozen(ItemLinks.QualityHex[it.Quality]) : (on ? Res("Brush.Text.Primary") : Res("Brush.Text.Secondary"));
                if (on) t.FontWeight = FontWeights.SemiBold;
                Grid.SetColumn(t, 1); g.Children.Add(t);
                if (pinned)
                {
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    x = new Button { Content = "", Style = (Style)FindResource("IconButton"), Width = 24, Height = 24, FontSize = 12, Visibility = Visibility.Collapsed, ToolTip = "Unpin" };
                    System.Windows.Automation.AutomationProperties.SetName(x, "Unpin " + it.Title);
                    x.Click += (s, e) => { e.Handled = true; NavState.Unpin(it.Id); UpdateShell(); };
                    Grid.SetColumn(x, 2); g.Children.Add(x);
                    row.MouseEnter += (s, e) => x.Visibility = Visibility.Visible;
                    row.MouseLeave += (s, e) => x.Visibility = Visibility.Collapsed;
                }
            }
            row.Content = g;
            row.MouseLeftButtonUp += (s, e) => { if (dragMoved) { dragMoved = false; return; } e.Handled = true; _ = Open(it.Id); };
            Keyable(row, () => _ = Open(it.Id), on);
            if (pinned)
            {
                row.AllowDrop = true;
                row.PreviewMouseLeftButtonDown += (s, e) => { dragStart = e.GetPosition(null); dragId = it.Id; dragMoved = false; };
                row.PreviewMouseMove += (s, e) =>
                {
                    if (e.LeftButton != MouseButtonState.Pressed || dragId != it.Id) return;
                    var p = e.GetPosition(null);
                    if (Math.Abs(p.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(p.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                    dragMoved = true;
                    try { DragDrop.DoDragDrop(row, new DataObject("atlas-pin", it.Id), DragDropEffects.Move); } catch (Exception) { }
                    dragId = null;
                };
                row.Drop += (s, e) =>
                {
                    if (e.Data.GetData("atlas-pin") is string from && from != it.Id)
                    {
                        int to = NavState.Pins.ToList().IndexOf(it.Id);
                        NavState.MovePinTo(from, to);
                        e.Handled = true; UpdateShell();
                    }
                };
            }
            return row;
        }
        Point dragStart; string dragId; bool dragMoved;

        string CurrentCategoryId(string id)
        {
            if (AtlasBrowse.IsBrowseId(id)) return BrowseQuery.TryParse(id, out var q) ? q.Cat : "";
            if (AtlasCategories.TryHosted(id, out var hc, out _)) return hc.Id;
            if (AtlasIds.TryParse(id, out var kind, out _)) return AtlasNav.CategoryOfKind(kind);
            return "";
        }

        // ---- one tile for everything: the game icon when there is one (items, spells), else the quality-coloured first letter, else the kind's glyph
        FrameworkElement IconTile(AtlasNavItem it, double size, double fontSize)
        {
            var tile = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size >= 40 ? 10 : 8), VerticalAlignment = VerticalAlignment.Center };
            tile.SetResourceReference(Border.BackgroundProperty, "Brush.Surface2");
            var row = new AtlasRow { Link = it.Id, Title = it.Title, Quality = it.Quality, IconId = it.IconId };
            var tg = new Grid();
            var drawn = it.IconId <= 0 && it.Quality < 0 ? AtlasIcons.Make(it.Icon, size >= 40 ? 24 : size >= 28 ? 16 : 12, "Brush.Text.Secondary") : null;
            if (drawn != null) { tile.Child = drawn; return tile; }
            bool glyph = it.IconId <= 0 && it.Quality < 0 && it.Glyph.Length > 0;
            var letter = new TextBlock { Text = glyph ? it.Glyph : row.Initial, FontSize = fontSize, Foreground = glyph ? Res("Brush.Text.Tertiary") : row.TileBrush, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            if (glyph) letter.FontFamily = (FontFamily)FindResource("Font.Icons"); else letter.FontWeight = FontWeights.SemiBold;
            tg.Children.Add(letter);
            if (it.IconId > 0)
            {
                var img = new Image { Stretch = Stretch.UniformToFill };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                img.SetBinding(Image.SourceProperty, new System.Windows.Data.Binding("Icon") { Source = row });
                tg.Children.Add(new Border { CornerRadius = new CornerRadius(size >= 40 ? 10 : 8), ClipToBounds = true, Child = img });
            }
            tile.Child = tg;
            return tile;
        }
    }
}
