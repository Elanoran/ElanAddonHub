using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Atlas.Classic;
using ElansAddonHub.Services.Db2;

namespace ElansAddonHub
{
    // The Data sources page (atlas:data, 2.93): who owns each part of the Atlas's data and how complete the Forever part is, at a glance.
    //   1. "Where this comes from" (2.91, AtlasPage.DataHonesty.cs), restyled: the three kinds side by side with their source's icon
    //   2. the source strip: Your game / You / Your Lodge / The Cairn / Classic baseline - each its own line icon (AtlasIcons src-*) and colour (Brush.Source.*,
    //      the same as the source chips on Atlas rows), one figure, "who owns it" in one line; a card with a list of its own opens it (filtered Atlas lists)
    //   3. "How complete is the Forever data": a stacked bar per kind (drops, vendors, spawn points, nodes, flight times) - by you / your Lodge / the Cairn /
    //      only in the Classic baseline - each with its sentence underneath (the text equivalent; the bar's automation name is the same sentence)
    //   4. the Lodge's finds per day (the one history this PC has: each find's own date) and the Cairn card (standing, last dataset, Lodges, give to get, Settings);
    //      the Cairn keeps no history here, and the page says so instead of drawing one
    //   5. the detail cards as before (game data, the Classic baseline with Get / Remove, the Lodge pool, the world map cache)
    // Nothing moves on its own: the page fades in through Motion.PageIn (off with Windows animations off). Self-test section "datasources".
    public partial class AtlasPage
    {
        // the main window fills these (MainWindow.Atlas.cs); the self-test swaps the whole set
        public Func<Services.AtlasObs.Snapshot> OwnObservations = () => null;
        public Func<ICollection<string>> OwnFlightLegs = () => null, LodgeFlightLegs = () => null;
        public Func<CairnFacts> CairnFactsSource = () => null;
        public Func<SourcesFacts> SourcesFactsOverrideForTest;

        public const string SourceCardTag = "source-card|", SourceCoverageTag = "coverage|", GrowthTag = "growth", CairnCardTag = "cairn-card", ExplainerGridTag = "sources-explainer-grid";
        UniformGrid sourceStrip, explainerGrid;
        Grid sourcesPair; FrameworkElement pairLeft, pairRight; int sourcesPairCols;

        public IReadOnlyList<ContentControl> SourceCardsForTest => AtlasWalk(Body).OfType<ContentControl>().Where(c => c.Tag is string t && t.StartsWith(SourceCardTag, StringComparison.Ordinal)).ToList();
        public IReadOnlyList<string> SourceCardKeysForTest => SourceCardsForTest.Select(c => ((string)c.Tag).Substring(SourceCardTag.Length)).ToList();
        public IReadOnlyList<string> SourceCardTextsForTest(string key) { var c = SourceCardsForTest.FirstOrDefault(x => (string)x.Tag == SourceCardTag + key); return c == null ? new List<string>() : AtlasWalk(c).OfType<TextBlock>().Select(t => t.Text).ToList(); }
        public IReadOnlyList<string> CoverageTextsForTest => AtlasWalk(Body).OfType<FrameworkElement>().Where(f => f.Tag is string t && t.StartsWith(SourceCoverageTag, StringComparison.Ordinal)).Select(f => AutomationProperties.GetName(f)).ToList();
        public string GrowthTextForTest => AtlasWalk(Body).OfType<FrameworkElement>().Where(f => Equals(f.Tag, GrowthTag)).Select(f => AutomationProperties.GetName(f)).FirstOrDefault();
        public IReadOnlyList<string> CairnCardTextsForTest => AtlasWalk(Body).OfType<FrameworkElement>().Where(f => Equals(f.Tag, CairnCardTag)).SelectMany(f => AtlasWalk(f).OfType<TextBlock>()).Select(t => t.Text).ToList();
        public Button CairnSettingsForTest { get; private set; }
        public int SourceStripColumnsForTest => sourceStrip?.Columns ?? 0;
        public int SourcesPairColumnsForTest => sourcesPairCols;

        // ---- what the page is drawn from
        SourcesFacts GatherSourcesFacts()
        {
            if (SourcesFactsOverrideForTest != null) return SourcesFactsOverrideForTest();
            var f = new SourcesFacts { GameReady = ItemData.Ready && SpellData.Ready, GameItems = ItemData.Count, GameSpells = SpellData.SpellCount, NowUtc = AtlasQuery.Context.NowUtc() };
            try { f.Lodge = AtlasQuery.LodgeOrNull(); } catch (Exception) { }
            f.Cairn = CairnAtlas.Current;
            f.Classic = ClassicAtlas.Current;
            try { f.Own = OwnObservations?.Invoke(); } catch (Exception) { }
            try { f.OwnFlights = OwnFlightLegs?.Invoke(); } catch (Exception) { }
            try { f.LodgeFlights = LodgeFlightLegs?.Invoke(); } catch (Exception) { }
            CairnFacts cf = null;
            try { cf = CairnFactsSource?.Invoke(); } catch (Exception) { }
            if (cf == null)
            {
                var p = Pool();
                cf = new CairnFacts { HasLodge = f.Lodge != null || p.State.StartsWith("Connected", StringComparison.Ordinal), Online = p.State.StartsWith("Connected", StringComparison.Ordinal), Refused = CairnAtlas.RefusedWhy };
                try { cf.Sharing = AtlasQuery.Context.CairnNeedsSharing?.Invoke() != true; } catch (Exception) { }
            }
            f.Cairn2 = cf;
            if (f.Lodge != null)
            {
                try { f.LodgeMembers = Math.Max(f.Lodge.SeenMembers, f.Lodge.AllFinds.Select(x => x.By ?? "").Where(x => x.Length > 0).Distinct().Count()); } catch (Exception) { }
            }
            return f;
        }

        // ---- the page
        void DrawSourcesPage(StackPanel col)
        {
            var f = GatherSourcesFacts();
            col.Children.Add(SourcesExplainerCard());          // 2.91's words, 2.93's look (AtlasPage.DataHonesty.cs)
            col.Children.Add(SourcesHead("Who owns what", "Five sources, each in its own colour - the same colour as the source chip on every Atlas row."));
            col.Children.Add(SourceStripView(f));
            col.Children.Add(SourcesHead("How complete is the Forever data", "What only the server knows is learned as people play. Each thing is counted once, for the first source that has it."));
            col.Children.Add(CoverageCard(f));
            var g = DataSources.LodgeGrowth(f.Lodge, f.NowUtc);
            pairLeft = GrowthCard(f, g);
            pairRight = CairnCardView(f);
            sourcesPair = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            sourcesPairCols = 0;
            col.Children.Add(sourcesPair);
            col.Children.Add(SourcesHead("Details", "Everything is read on this PC; nothing is uploaded without your say."));
        }

        FrameworkElement SourcesHead(string title, string line)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 12, 0, 8) };
            sp.Children.Add(Tb(title, "Text.Section"));
            var t = Tb(line, "Text.Secondary", null, 2); t.TextWrapping = TextWrapping.Wrap; t.MaxWidth = ReadWidth; t.HorizontalAlignment = HorizontalAlignment.Left;
            sp.Children.Add(t);
            return sp;
        }

        // a source's tile: its icon in its colour on its tint
        Border SourceTile(string key, double size)
        {
            var b = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size >= 36 ? 10 : 8), VerticalAlignment = VerticalAlignment.Top };
            b.SetResourceReference(Border.BackgroundProperty, DataSources.TintKey(key));
            var icon = AtlasIcons.Make(DataSources.IconKey(key), Math.Round(size * 0.56), DataSources.BrushKey(key));
            if (icon != null) b.Child = icon;
            return b;
        }

        // ---- 2. the source strip
        FrameworkElement SourceStripView(SourcesFacts f)
        {
            sourceStrip = new UniformGrid { Columns = StripColumnsFor(Scroller.ActualWidth > 0 ? Scroller.ActualWidth : 760), Margin = new Thickness(0, 0, -8, 4) };
            foreach (var key in DataSources.Order) sourceStrip.Children.Add(SourceCard(key, f));
            return sourceStrip;
        }

        public static int StripColumnsFor(double w) => w >= 1060 ? 5 : w >= 620 ? 3 : 1;

        ContentControl SourceCard(string key, SourcesFacts f)
        {
            var list = DataSources.ListOf(key);
            bool opens = list.Length > 0;
            var card = new ContentControl { Style = (Style)FindResource("Side.Row"), Padding = new Thickness(12), Margin = new Thickness(0, 0, 8, 8), Tag = SourceCardTag + key, Focusable = false, VerticalContentAlignment = VerticalAlignment.Stretch };
            card.SetResourceReference(Control.BackgroundProperty, "Brush.Surface1");
            card.Cursor = opens ? Cursors.Hand : Cursors.Arrow;
            var owner = DataSources.Owner(key, f);
            var figure = DataSources.Figure(key, f);
            AutomationProperties.SetName(card, DataSources.Title(key) + ": " + figure + ". " + owner + (opens ? " Opens " + DataSources.ListTitle(key) + "." : ""));
            if (opens)
            {
                card.ToolTip = "Open " + DataSources.ListTitle(key);
                card.MouseLeftButtonUp += (s, e) => { e.Handled = true; _ = Open(list); };
                Keyable(card, () => _ = Open(list), false);
            }
            var sp = new StackPanel();
            var top = new DockPanel { LastChildFill = true };
            var tile = SourceTile(key, 36); tile.Margin = new Thickness(0, 0, 12, 0);
            DockPanel.SetDock(tile, Dock.Left); top.Children.Add(tile);
            if (opens)
            {
                var chev = new TextBlock { Style = (Style)FindResource("Text.Chevron"), Text = "", Margin = new Thickness(8, 0, 0, 0) };     // chevron right
                DockPanel.SetDock(chev, Dock.Right); top.Children.Add(chev);
            }
            var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var title = Tb(DataSources.Title(key), "Text.RowTitle"); title.TextTrimming = TextTrimming.CharacterEllipsis;
            names.Children.Add(title);
            var fig = Tb(figure, "Text.Caption", Res(DataSources.BrushKey(key))); fig.TextWrapping = TextWrapping.Wrap;     // "24,007 items · 31,926 spells" wraps in a narrow card
            names.Children.Add(fig);
            top.Children.Add(names);
            sp.Children.Add(top);
            var own = Tb(owner, "Text.Secondary", null, 10); own.TextWrapping = TextWrapping.Wrap;
            sp.Children.Add(own);
            var what = Tb(DataSources.What(key), "Text.Caption", Res("Brush.Text.Tertiary"), 4); what.TextWrapping = TextWrapping.Wrap;
            sp.Children.Add(what);
            card.Content = sp;
            // Side.Row centres its content; the cards of a row are as tall as the tallest, so each card's content takes the full height (top-aligned)
            // (a new width lets the row find its height again first)
            void Fill() { double h = card.ActualHeight - card.Padding.Top - card.Padding.Bottom; if (h > 0 && Math.Abs(sp.MinHeight - h) > 0.5) sp.MinHeight = h; }
            card.SizeChanged += (s, e) =>
            {
                if (e.WidthChanged && e.PreviousSize.Width > 0) { sp.MinHeight = 0; Dispatcher.BeginInvoke(new Action(Fill), System.Windows.Threading.DispatcherPriority.Loaded); }
                else Fill();
            };
            return card;
        }

        // ---- 3. coverage: one stacked bar per kind
        static readonly string[] CoverageParts = { DataSources.You, DataSources.Lodge, DataSources.Cairn, DataSources.Classic };

        FrameworkElement CoverageCard(SourcesFacts f)
        {
            var sp = new StackPanel();
            // the legend: what the colours of the bars are
            var legend = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
            foreach (var k in CoverageParts)
            {
                var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 4) };
                var sw = new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
                sw.SetResourceReference(Border.BackgroundProperty, DataSources.BrushKey(k));
                item.Children.Add(sw);
                item.Children.Add(Tb(k == DataSources.Classic ? "Only in the Classic baseline" : k == DataSources.You ? "Seen by you" : "Seen by " + DataSources.Title(k).Replace("Your ", "your ").Replace("The ", "the "), "Text.Caption"));
                legend.Children.Add(item);
            }
            sp.Children.Add(legend);
            foreach (var k in DataSources.Coverage(f)) sp.Children.Add(CoverageRow(k));
            return Card(sp);
        }

        FrameworkElement CoverageRow(KindCoverage k)
        {
            var row = new StackPanel { Margin = new Thickness(0, 12, 0, 0), Tag = SourceCoverageTag + k.Kind, Focusable = false };
            AutomationProperties.SetName(row, k.Text);
            var head = new DockPanel();
            var total = Tb(k.Total > 0 ? AtlasFormat.Count(k.Total, k.One, k.Many) : "none yet", "Text.Caption", Res("Brush.Text.Tertiary")); total.VerticalAlignment = VerticalAlignment.Bottom;
            DockPanel.SetDock(total, Dock.Right); head.Children.Add(total);
            head.Children.Add(Tb(k.Title, "Text.RowTitle"));
            row.Children.Add(head);
            row.Children.Add(StackedBar(k));
            var t = Tb(SentenceAfterTitle(k), "Text.Secondary", null, 6); t.TextWrapping = TextWrapping.Wrap; t.MaxWidth = ReadWidth; t.HorizontalAlignment = HorizontalAlignment.Left;
            row.Children.Add(t);
            return row;
        }

        // the sentence without its "Drops: " (the title is right above it)
        static string SentenceAfterTitle(KindCoverage k)
        {
            var s = k.Text ?? "";
            var lead = k.Title + ": ";
            if (s.StartsWith(lead, StringComparison.Ordinal)) s = s.Substring(lead.Length);
            return s.Length > 0 ? char.ToUpperInvariant(s[0]) + s.Substring(1) : s;
        }

        FrameworkElement StackedBar(KindCoverage k)
        {
            var g = new Grid { Height = 8, Margin = new Thickness(0, 8, 0, 0), SnapsToDevicePixels = true };
            AutomationProperties.SetName(g, k.Text);
            var parts = new List<KeyValuePair<string, int>>
            {
                new KeyValuePair<string, int>(DataSources.You, k.You), new KeyValuePair<string, int>(DataSources.Lodge, k.Lodge),
                new KeyValuePair<string, int>(DataSources.Cairn, k.Cairn), new KeyValuePair<string, int>(DataSources.Classic, k.Classic),
            }.Where(p => p.Value > 0).ToList();
            if (parts.Count == 0)
            {
                var empty = new Border { Height = 8, CornerRadius = new CornerRadius(4) };
                empty.SetResourceReference(Border.BackgroundProperty, "Brush.Surface3");
                g.Children.Add(empty);
                return g;
            }
            for (int i = 0; i < parts.Count; i++)
            {
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(parts[i].Value, GridUnitType.Star), MinWidth = 6 });
                bool first = i == 0, last = i == parts.Count - 1;
                var seg = new Border { CornerRadius = new CornerRadius(first ? 4 : 0, last ? 4 : 0, last ? 4 : 0, first ? 4 : 0), Margin = new Thickness(0, 0, last ? 0 : 2, 0), ToolTip = DataSources.Title(parts[i].Key) + ": " + parts[i].Value.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) };
                seg.SetResourceReference(Border.BackgroundProperty, DataSources.BrushKey(parts[i].Key));
                Grid.SetColumn(seg, i);
                g.Children.Add(seg);
            }
            return g;
        }

        // ---- 4a. the Lodge's finds per day
        FrameworkElement GrowthCard(SourcesFacts f, Growth g)
        {
            var sp = new StackPanel { Tag = GrowthTag };
            var text = g.Text + " " + (f.Cairn != null ? "The Cairn: " + DataSources.CairnHistory(f.Cairn) : "");
            AutomationProperties.SetName(sp, text.Trim());
            var head = new DockPanel();
            var tile = SourceTile(DataSources.Lodge, 28); tile.Margin = new Thickness(0, 0, 12, 0); tile.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(tile, Dock.Left); head.Children.Add(tile);
            head.Children.Add(new TextBlock { Text = "Growth", Style = (Style)FindResource("Text.Section"), VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(head);
            var lodgeLine = Tb("Your Lodge: finds per day", "Text.RowTitle", null, 12);
            sp.Children.Add(lodgeLine);
            sp.Children.Add(Spark(g));
            var axis = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
            var right = Tb("today", "Text.Caption", Res("Brush.Text.Tertiary")); DockPanel.SetDock(right, Dock.Right); axis.Children.Add(right);
            axis.Children.Add(Tb(g.PerDay.Length / 7 + " weeks ago", "Text.Caption", Res("Brush.Text.Tertiary")));
            sp.Children.Add(axis);
            var gt = Tb(g.Text, "Text.Secondary", null, 6); gt.TextWrapping = TextWrapping.Wrap; sp.Children.Add(gt);
            var cairnHead = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
            var ct = SourceTile(DataSources.Cairn, 20); ct.Margin = new Thickness(0, 0, 8, 0); ct.VerticalAlignment = VerticalAlignment.Center;
            cairnHead.Children.Add(ct);
            cairnHead.Children.Add(new TextBlock { Text = "The Cairn", Style = (Style)FindResource("Text.RowTitle"), VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(cairnHead);
            var ch = Tb(f.Cairn != null ? DataSources.CairnHistory(f.Cairn) : "No Cairn data on this PC, so there is nothing to count.", "Text.Secondary", null, 4); ch.TextWrapping = TextWrapping.Wrap;
            sp.Children.Add(ch);
            return Card(sp, 0);
        }

        // 28 thin bars, the days without a find as a faint baseline; drawn once, nothing animates
        FrameworkElement Spark(Growth g)
        {
            const double h = 44;
            var grid = new UniformGrid { Rows = 1, Columns = Math.Max(1, g.PerDay.Length), Height = h, Margin = new Thickness(0, 8, 0, 0) };
            AutomationProperties.SetName(grid, g.Text);
            int max = g.PerDay.Length == 0 ? 0 : g.PerDay.Max();
            foreach (var v in g.PerDay)
            {
                var b = new Border { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 2, 0), Height = v > 0 && max > 0 ? Math.Max(4, h * v / max) : 2 };
                b.SetResourceReference(Border.BackgroundProperty, v > 0 ? DataSources.BrushKey(DataSources.Lodge) : "Brush.Surface3");
                grid.Children.Add(b);
            }
            return grid;
        }

        // ---- 4b. the Cairn card
        FrameworkElement CairnCardView(SourcesFacts f)
        {
            var info = DataSources.CairnCard(f);
            var sp = new StackPanel { Tag = CairnCardTag };
            AutomationProperties.SetName(sp, "The Cairn: " + info.Title + ". " + info.Line);
            var head = new DockPanel();
            var tile = SourceTile(DataSources.Cairn, 28); tile.Margin = new Thickness(0, 0, 12, 0); tile.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(tile, Dock.Left); head.Children.Add(tile);
            var chip = new Border { Style = (Style)FindResource("Chip"), Padding = new Thickness(8, 0, 12, 0), MinHeight = 24, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            string dot = info.Standing == CairnStanding.Good ? "Brush.Success" : info.Standing == CairnStanding.Lapsed || info.Standing == CairnStanding.Waiting ? "Brush.Warn" : "Brush.Text.Tertiary";
            var cs = new StackPanel { Orientation = Orientation.Horizontal };
            var e = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            e.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, dot);
            cs.Children.Add(e);
            cs.Children.Add(new TextBlock { Text = info.Title, Style = (Style)FindResource("Text.Caption"), VerticalAlignment = VerticalAlignment.Center, Foreground = Res("Brush.Text.Primary") });
            chip.Child = cs;
            DockPanel.SetDock(chip, Dock.Right); head.Children.Add(chip);
            head.Children.Add(new TextBlock { Text = "The Cairn", Style = (Style)FindResource("Text.Section"), VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(head);
            var line = Tb(info.Line, "Text.Body", null, 12); line.TextWrapping = TextWrapping.Wrap; sp.Children.Add(line);
            void Fact(string label, string value)
            {
                if (string.IsNullOrEmpty(value)) return;
                var g = new Grid { Margin = new Thickness(0, 8, 0, 0) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.Children.Add(Tb(label, "Text.Secondary"));
                var v = Tb(value, "Text.Body"); v.TextWrapping = TextWrapping.Wrap; Grid.SetColumn(v, 1); g.Children.Add(v);
                sp.Children.Add(g);
            }
            Fact("Last dataset", info.Dataset);
            Fact("Lodges", info.Lodges);
            var give = Tb(info.GiveToGet, "Text.Caption", Res("Brush.Text.Tertiary"), 12); give.TextWrapping = TextWrapping.Wrap; sp.Children.Add(give);
            CairnSettingsForTest = null;
            if (OpenSharingSettings != null)
            {
                var b = new Button { Content = info.Standing == CairnStanding.NotSharing ? "Turn on sharing" : "Sharing settings", Style = (Style)FindResource("Button.S"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0), Tag = "cairn-settings" };
                b.Click += (s, a) => OpenSharingSettings?.Invoke();
                AutomationProperties.SetName(b, (string)b.Content + " (Settings > Lodge)");
                sp.Children.Add(b);
                CairnSettingsForTest = b;
            }
            return Card(sp, 0);
        }

        // ---- the widths: the strip 5 / 3 / 1 across, the growth and Cairn cards side by side from InfoTwoColumns, the explainer's three kinds likewise
        void LayoutSources(double w)
        {
            if (sourceStrip != null) { int c = StripColumnsFor(w); if (sourceStrip.Columns != c) sourceStrip.Columns = c; }
            if (explainerGrid != null) { int c = w >= InfoTwoColumns ? 3 : 1; if (explainerGrid.Columns != c) explainerGrid.Columns = c; }
            if (sourcesPair == null || pairLeft == null || pairRight == null) return;
            int cols = w >= InfoTwoColumns ? 2 : 1;
            if (cols == sourcesPairCols) return;
            sourcesPairCols = cols;
            sourcesPair.Children.Clear(); sourcesPair.ColumnDefinitions.Clear(); sourcesPair.RowDefinitions.Clear();
            if (cols == 2)
            {
                sourcesPair.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                sourcesPair.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
                sourcesPair.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                Grid.SetRow(pairLeft, 0); Grid.SetRow(pairRight, 0);
                Grid.SetColumn(pairLeft, 0); Grid.SetColumn(pairRight, 2);
            }
            else
            {
                sourcesPair.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                sourcesPair.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
                sourcesPair.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetColumn(pairLeft, 0); Grid.SetColumn(pairRight, 0);
                Grid.SetRow(pairLeft, 0); Grid.SetRow(pairRight, 2);
            }
            sourcesPair.Children.Add(pairLeft); sourcesPair.Children.Add(pairRight);
        }

        void ResetSourcesPage() { sourceStrip = null; explainerGrid = null; sourcesPair = null; pairLeft = pairRight = null; sourcesPairCols = 0; CairnSettingsForTest = null; }
    }
}
