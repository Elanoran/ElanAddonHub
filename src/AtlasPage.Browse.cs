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

namespace ElansAddonHub
{
    // Browse by category: the tiles and the "where to get" buttons of the start page, the browse page (breadcrumb, sub-categories, filter bar, a virtualised list)
    // and the hosted categories (another page living inside the Atlas, see AtlasCategories). The list is rebuilt on a worker when a filter changes; the page and the
    // history entry (id with the filters) change without drawing the filter bar again, so typing in a box is never interrupted.
    public partial class AtlasPage
    {
        BrowseResult browse;
        FrameworkElement hosted;
        string hostedId = "";
        ListBox browseList;
        TextBlock browseCount, browseEmpty;
        StackPanel browseNotes;
        Button browseClear;
        BrowseQuery bq = new BrowseQuery();
        readonly Dictionary<string, FrameworkElement> bctl = new Dictionary<string, FrameworkElement>();
        readonly DispatcherTimer filterDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        int filterGen;
        bool suppress, filterWired;
        Task filterTask = Task.CompletedTask;

        public BrowseResult BrowseModel => browse;
        public bool IsBrowse => browse != null;
        public bool IsHosted => hosted != null;
        public FrameworkElement HostedForTest => hosted;
        public ListBox BrowseListForTest => browseList;
        public FrameworkElement BrowseControlForTest(string key) => bctl.TryGetValue(key, out var c) ? c : null;
        public Task BrowseWaitForTest() => filterTask;
        public int FilterDebounceMsForTest { get => (int)filterDebounce.Interval.TotalMilliseconds; set => filterDebounce.Interval = TimeSpan.FromMilliseconds(value); }
        public string BrowseCountTextForTest => browseCount?.Text ?? "";
        public IReadOnlyList<string> BrowseNotesForTest => browseNotes == null ? new List<string>() : AtlasWalk(browseNotes).OfType<TextBlock>().Select(t => t.Text).ToList();

        static IEnumerable<DependencyObject> AtlasWalk(DependencyObject d)
        {
            if (d == null) yield break;
            yield return d;
            int n = d is Visual ? VisualTreeHelper.GetChildrenCount(d) : 0;
            for (int i = 0; i < n; i++) foreach (var c in AtlasWalk(VisualTreeHelper.GetChild(d, i))) yield return c;
        }

        // ============================================================================================================================ a browse page
        void DrawBrowse(BrowseResult r)
        {
            model = null; hosted = null; browse = r; bq = r.Query.Clone(); drawnSig = Signature();
            ReleaseMaps();
            Body.Children.Clear(); bctl.Clear();
            ResetPageState();
            TitleText.Text = r.Title; TitleText.Foreground = Res("Brush.Text.Primary");
            SubText.Text = r.NotFound ? "Browse" : r.Subtitle;
            UpdateNav();
            if (r.NotFound)
            {
                Body.Children.Add(EmptyCard("", r.Message.Length > 0 ? r.Message : "Nothing to list here.", "Open the start page to see what data Atlas has."));
                browseList = null;
                Motion.PageIn(Body);
                return;
            }
            var col = new StackPanel();
            Body.Children.Add(col);
            if (r.Children.Count > 0) col.Children.Add(ChildChips(r));
            col.Children.Add(FilterBar(r));
            browseCount = Tb(r.CountText, "Text.Caption", Res("Brush.Text.Tertiary"), 4);
            col.Children.Add(browseCount);
            browseNotes = new StackPanel();
            col.Children.Add(browseNotes);
            FillNotes(r);
            browseEmpty = Tb("Nothing matches these filters.", "Text.Secondary", null, 12);
            browseEmpty.Visibility = r.Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            col.Children.Add(browseEmpty);
            browseList = new ListBox { Style = (Style)FindResource("AtlasList"), ItemsSource = r.Rows, Margin = new Thickness(-8, 4, -8, 0), MinHeight = 200, MaxHeight = Math.Max(240, Scroller.ViewportHeight > 0 ? Scroller.ViewportHeight - 240 : 480) };   // a height from the start: a list with none would build every row
            browseList.PreviewMouseWheel += (s, e) =>
            {
                var sv = FindScroll(browseList);
                if (sv == null) return;
                bool atTop = sv.VerticalOffset <= 0 && e.Delta > 0, atBottom = sv.VerticalOffset >= sv.ScrollableHeight && e.Delta < 0;
                if (atTop || atBottom) { e.Handled = true; Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset - e.Delta); }
            };
            browseList.Visibility = r.Rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            col.Children.Add(browseList);
            Scroller.ScrollToTop();
            Motion.PageIn(Body);
            Dispatcher.BeginInvoke(new Action(SizeBrowseList), DispatcherPriority.Background);
        }

        // the list takes the rest of the window below the filter bar
        void SizeBrowseList()
        {
            if (browseList == null || !browseList.IsVisible && browseList.Visibility != Visibility.Visible) return;
            try
            {
                double y = browseList.TranslatePoint(new Point(0, 0), Scroller).Y + Scroller.VerticalOffset;
                browseList.MaxHeight = Math.Max(240, Scroller.ViewportHeight - y - 4);
            }
            catch (InvalidOperationException) { }
        }

        FrameworkElement CrumbRow(IList<AtlasCrumb> crumbs, double bottom = 8)
        {
            var w = new WrapPanel { Margin = new Thickness(0, 0, 0, bottom) };
            for (int i = 0; i < crumbs.Count; i++)
            {
                var c = crumbs[i];
                if (i > 0) w.Children.Add(Tb("›", "Text.Secondary", Res("Brush.Text.Tertiary")));
                var t = Tb(c.Title, string.IsNullOrEmpty(c.Link) ? "Text.Body" : "Text.Secondary");
                t.Margin = new Thickness(i > 0 ? 4 : 0, 0, 4, 0);
                if (!string.IsNullOrEmpty(c.Link))
                {
                    var link = c.Link;
                    t.Cursor = Cursors.Hand; t.ToolTip = "Open " + c.Title;
                    t.MouseEnter += (s, e) => t.TextDecorations = TextDecorations.Underline;
                    t.MouseLeave += (s, e) => t.TextDecorations = null;
                    t.MouseLeftButtonUp += (s, e) => { e.Handled = true; if (link == AtlasBrowse.HomeLink) _ = GoStart(); else _ = Open(link); };
                }
                w.Children.Add(t);
            }
            return w;
        }

        FrameworkElement ChildChips(BrowseResult r)
        {
            var w = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
            foreach (var k in r.Children)
            {
                var id = k.Id;
                var b = new Button { Style = (Style)FindResource("GhostButton"), Height = 28, Margin = new Thickness(0, 0, 8, 8), ToolTip = k.Current ? "You are here" : "Open " + k.Title };
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                var t = Tb(k.Title, "Text.Secondary", k.Current ? Res("Brush.Text.Primary") : null);
                if (k.Current) t.FontWeight = FontWeights.SemiBold;
                sp.Children.Add(t);
                var n = Tb(k.Count.ToString("N0", Lodge.CalendarRules.English), "Text.Caption", Res("Brush.Text.Tertiary")); n.Margin = new Thickness(4, 0, 0, 0);
                sp.Children.Add(n);
                b.Content = sp;
                b.Click += (s, e) => { if (!k.Current) _ = Open(id); };
                w.Children.Add(b);
            }
            return w;
        }

        // ---- the filter bar: only the controls that mean something for the category
        FrameworkElement FilterBar(BrowseResult r)
        {
            var c = r.Controls;
            var panel = new WrapPanel();
            suppress = true;
            WireFilterTimer();

            var name = new TextBox { Style = (Style)FindResource("Input"), Width = 180, Margin = new Thickness(0, 0, 8, 8), Tag = "Name contains", Text = bq.Name, VerticalAlignment = VerticalAlignment.Center };
            System.Windows.Automation.AutomationProperties.SetName(name, "Name contains");
            name.TextChanged += (s, e) => { bq.Name = name.Text.Trim(); ScheduleFilter(); };
            bctl["name"] = name; panel.Children.Add(name);

            if (c.Level)
            {
                string unit = c.LevelLabel.IndexOf("kill", StringComparison.OrdinalIgnoreCase) >= 0 ? "skill" : "level";
                var lo = LevelBox("minlvl", "Min " + unit, bq.MinLevel, v => bq.MinLevel = v);
                var hi = LevelBox("maxlvl", "Max " + unit, bq.MaxLevel, v => bq.MaxLevel = v);
                panel.Children.Add(lo); panel.Children.Add(hi);
            }
            if (c.Quality)
            {
                var items = Enumerable.Range(0, 6).Select(i => new BrowseChoice(i.ToString(), ItemLinks.QualityNames[i]));
                panel.Children.Add(Combo("q", "Any quality", items, bq.Quality >= 0 ? bq.Quality.ToString() : "", v => bq.Quality = v.Length == 0 ? -1 : int.Parse(v)));
            }
            if (c.Slot) panel.Children.Add(Combo("slot", "Any slot", c.Slots, bq.Slot, v => bq.Slot = v));
            if (c.For) panel.Children.Add(Combo("for", "Usable by anyone", c.Chars, bq.For, v => bq.For = v, "Only what this character can wear or wield at their level"));
            if (c.Learn) panel.Children.Add(Combo("learn", "Any recipe", new[] { new BrowseChoice("can", "Ones my characters can learn") }, bq.Learn, v => bq.Learn = v, "Recipes of the professions your characters have, that they have not learned yet and have the skill for"));
            if (c.Src)
                panel.Children.Add(Combo("src", "Any data source", new[] { new BrowseChoice("game", "Your game data"), new BrowseChoice("classic", "Classic baseline"), new BrowseChoice("lodge", "Seen by the Lodge") }, bq.Src, v => bq.Src = v));
            if (c.How)
                panel.Children.Add(Combo("how", "Any way to get it", AtlasBrowse.Hows.Select(h => new BrowseChoice(h, AtlasBrowse.HowText(h))), bq.How, v => bq.How = v, "How to get it: drops, vendor, quest reward, crafted, fishing, containers, gathering"));
            if (c.Zone) panel.Children.Add(Combo("zone", "Any zone", c.Zones, bq.Zone, v => bq.Zone = v));
            if (c.Rank) panel.Children.Add(Combo("rank", "Any rank", new[] { new BrowseChoice("elite", "Elite"), new BrowseChoice("rare", "Rare"), new BrowseChoice("boss", "Boss") }, bq.Rank, v => bq.Rank = v));
            if (c.Sorts.Count > 1)
            {
                var sort = new ComboBox { Style = (Style)FindResource("Dropdown"), MinWidth = 150, Margin = new Thickness(0, 0, 8, 8), ToolTip = "Sort the list" };
                System.Windows.Automation.AutomationProperties.SetName(sort, "Sort");
                foreach (var s in c.Sorts) sort.Items.Add(new ComboBoxItem { Content = "Sort: " + s.Text, Tag = s.Value });
                int at = c.Sorts.FindIndex(s => s.Value == r.Sort); sort.SelectedIndex = at < 0 ? 0 : at;
                sort.SelectionChanged += (s, e) => { if (suppress || !(sort.SelectedItem is ComboBoxItem it)) return; bq.Sort = (string)it.Tag == c.DefaultSort ? "" : (string)it.Tag; ScheduleFilter(); };
                bctl["sort"] = sort; panel.Children.Add(sort);
            }
            browseClear = GhostButton("Clear filters", () =>
            {
                var keepSort = bq.Sort; var path = bq.Path.ToList();
                bq = new BrowseQuery { Cat = bq.Cat, Path = path, Sort = keepSort };
                _ = Open(bq.ToId());
            }, "Remove every filter");
            browseClear.Margin = new Thickness(0, 0, 8, 8);
            browseClear.Visibility = bq.HasFilters ? Visibility.Visible : Visibility.Collapsed;
            panel.Children.Add(browseClear);
            suppress = false;
            var card = Card(panel, 4);
            card.Padding = new Thickness(12, 12, 12, 4);
            return card;
        }

        TextBox LevelBox(string key, string placeholder, int value, Action<int> set)
        {
            var t = new TextBox { Style = (Style)FindResource("Input"), Width = 92, Margin = new Thickness(0, 0, 8, 8), Tag = placeholder, Text = value > 0 ? value.ToString() : "", MaxLength = 3, VerticalAlignment = VerticalAlignment.Center };
            System.Windows.Automation.AutomationProperties.SetName(t, placeholder);
            t.TextChanged += (s, e) => { int n; set(int.TryParse(t.Text.Trim(), out n) && n > 0 ? n : 0); ScheduleFilter(); };
            bctl[key] = t;
            return t;
        }

        ComboBox Combo(string key, string anyText, IEnumerable<BrowseChoice> choices, string selected, Action<string> set, string tip = null)
        {
            var cb = new ComboBox { Style = (Style)FindResource("Dropdown"), MinWidth = 150, Margin = new Thickness(0, 0, 8, 8) };
            System.Windows.Automation.AutomationProperties.SetName(cb, anyText);
            if (tip != null) cb.ToolTip = tip;
            cb.Items.Add(new ComboBoxItem { Content = anyText, Tag = "" });
            int sel = 0;
            foreach (var ch in choices) { cb.Items.Add(new ComboBoxItem { Content = ch.Text, Tag = ch.Value }); if (ch.Value == selected) sel = cb.Items.Count - 1; }
            cb.SelectedIndex = sel;
            cb.SelectionChanged += (s, e) => { if (suppress || !(cb.SelectedItem is ComboBoxItem it)) return; set((string)it.Tag); ScheduleFilter(); };
            bctl[key] = cb;
            return cb;
        }

        void FillNotes(BrowseResult r)
        {
            browseNotes.Children.Clear();
            foreach (var n in r.Notes) { var b = Note(n); b.Margin = new Thickness(0, 8, 0, 0); browseNotes.Children.Add(b); }
        }

        // ---- a filter changed: the list is built again on a worker, the id of the page follows (no new history step)
        void WireFilterTimer()
        {
            if (filterWired) return;
            filterWired = true;
            filterDebounce.Tick += (s, e) => { filterDebounce.Stop(); filterTask = ApplyFilters(); };
        }

        void ScheduleFilter()
        {
            if (suppress || browse == null) return;
            WireFilterTimer();
            filterDebounce.Stop(); filterDebounce.Start();
        }

        async Task ApplyFilters()
        {
            if (browse == null) return;
            var q = bq.Clone();
            int gen = ++filterGen, bg = buildGen;
            BrowseResult r;
            try { r = await Task.Run(() => AtlasBrowse.Run(q)); }
            catch (Exception e) { Util.Log("atlas browse " + q + ": " + e); return; }
            if (gen != filterGen || bg != buildGen || browse == null || browseList == null) return;
            browse = r; drawnSig = Signature();
            browseList.ItemsSource = r.Rows;
            browseList.Visibility = r.Rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            browseEmpty.Visibility = r.Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            browseCount.Text = r.CountText; SubText.Text = r.Subtitle;
            FillNotes(r);
            browseClear.Visibility = r.Query.HasFilters ? Visibility.Visible : Visibility.Collapsed;
            if (browseList.Items.Count > 0) browseList.ScrollIntoView(browseList.Items[0]);
            { var old = history.Current; history.Replace(r.Id); NavState.Follow(old, r.Id); }
            Navigated?.Invoke(r.Id);
            UpdateNav();
            SizeBrowseList();
        }

        // ============================================================================================================================ a hosted category
        // The hosted control sits in HostSlot (its own scrolling, under a breadcrumb "Atlas > Talents > Hunter") instead of the page's Body. The control is built once by its
        // category and handed back for every visit, so what the user was doing in it survives navigating away and back.
        AtlasCategory hostedCat;

        void DrawHosted(AtlasCategory cat, string sub)
        {
            model = null; browse = null; browseList = null;
            drawnSig = Signature();
            ReleaseMaps();
            Body.Children.Clear(); bctl.Clear();
            ResetPageState();
            TitleText.Text = cat.Title; TitleText.Foreground = Res("Brush.Text.Primary");
            SubText.Text = cat.Blurb;
            UpdateNav();
            FrameworkElement el = null;
            try { el = cat.Host(sub); }
            catch (Exception e) { Util.Log("atlas hosted " + cat.Id + ": " + e); }
            if (el == null)
            {
                LeaveHosted();
                Body.Children.Add(EmptyCard("", cat.Title + " could not be opened.", "Open the start page to see what data Atlas has."));
                Motion.PageIn(Body);
                return;
            }
            hosted = el; hostedCat = cat; hostedId = cat.Id + ":" + sub;
            HostContent.Content = el;
            Scroller.Visibility = Visibility.Collapsed; HostSlot.Visibility = Visibility.Visible;
            Motion.PageIn(HostSlot);
            // the page shows what it shows (the class last used for a bare 'talents:'): the id says so too
            string canon = null;
            try { canon = cat.Canonical?.Invoke(sub); } catch (Exception) { }
            if (canon != null) HostedChanged(cat.Id, canon);
        }

        // the page is not a hosted one any more (or the host failed): the page's own scroller is back and the hosted control is let go (the category keeps it)
        void LeaveHosted()
        {
            hosted = null; hostedCat = null; hostedId = "";
            HostContent.Content = null;
            HostSlot.Visibility = Visibility.Collapsed; Scroller.Visibility = Visibility.Visible;
        }

        FrameworkElement HostedCrumbs(AtlasCategory cat, string sub)
        {
            var list = new List<AtlasCrumb> { new AtlasCrumb("Atlas", AtlasBrowse.HomeLink) };
            string last = "";
            try { last = cat.SubTitle?.Invoke(sub) ?? ""; } catch (Exception) { }
            list.Add(new AtlasCrumb(cat.Title, ""));
            if (last.Length > 0) list.Add(new AtlasCrumb(last, ""));
            return CrumbRow(list);
        }

        // The hosted page changed what it shows by itself (another class, profession or tab): the id of the page follows without a new step in the history, like a browse filter.
        public void HostedChanged(string catId, string sub)
        {
            if (hosted == null || hostedCat == null || !string.Equals(hostedCat.Id, catId, StringComparison.OrdinalIgnoreCase)) return;
            var id = hostedCat.Id + ":" + (sub ?? "").Trim('/');
            if (id == hostedId) return;
            hostedId = id;
            { var old = history.Current; history.Replace(id); NavState.Follow(old, id); }
            Navigated?.Invoke(id);
            UpdateNav();
        }

        public string HostedCrumbsForTest => hosted != null ? CrumbsForTest : "";
        public bool HostSlotVisibleForTest => HostSlot.Visibility == Visibility.Visible;
    }
}