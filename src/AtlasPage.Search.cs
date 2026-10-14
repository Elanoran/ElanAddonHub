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
    // The one search box: results as an overlay under it, grouped by kind, arrow keys + Enter, Esc closes. The search runs on a worker; a newer
    // keystroke makes an older, slower answer drop out.
    public partial class AtlasPage
    {
        readonly DispatcherTimer searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(130) };
        int searchGen;
        AtlasSearchResult results;
        readonly List<ContentControl> hitRows = new List<ContentControl>();
        readonly List<AtlasHit> hitList = new List<AtlasHit>();
        int selected = -1;
        bool searchWired;
        public int DebounceMsForTest { get => (int)searchDebounce.Interval.TotalMilliseconds; set => searchDebounce.Interval = TimeSpan.FromMilliseconds(value); }
        public AtlasSearchResult ResultsForTest => results;
        public bool ResultsOpen => ResultsPanel.Visibility == Visibility.Visible;
        public int SelectedHitForTest => selected;
        public IReadOnlyList<AtlasHit> HitsForTest => hitList;
        public string SearchTextForTest { get => SearchBox.Text; set => SearchBox.Text = value; }
        public TextBox SearchBoxForTest => SearchBox;
        Task searchTask = Task.CompletedTask;
        public Task SearchWaitForTest() => searchTask;

        void WireSearch()
        {
            if (searchWired) return;
            searchWired = true;
            searchDebounce.Tick += (s, e) => { searchDebounce.Stop(); searchTask = RunSearchNow(); };
        }

        void Search_Changed(object sender, TextChangedEventArgs e)
        {
            WireSearch();
            searchDebounce.Stop();
            if (string.IsNullOrWhiteSpace(SearchBox.Text)) { searchGen++; CloseResults(); return; }
            searchDebounce.Start();
        }

        void Search_Focus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(SearchBox.Text) && results != null && !ResultsOpen) ResultsPanel.Visibility = Visibility.Visible;
        }

        public void CloseResults()
        {
            ResultsPanel.Visibility = Visibility.Collapsed;
            selected = -1;
        }

        async Task RunSearchNow()
        {
            WireSearch();
            searchDebounce.Stop();
            var text = SearchBox.Text ?? "";
            if (string.IsNullOrWhiteSpace(text)) { CloseResults(); return; }
            var routed = AtlasQuery.NormalizeId(text);
            if (routed != null && AtlasCategories.IsRouted(routed)) { CloseResults(); await Show(routed, true); return; }
            int gen = ++searchGen;
            AtlasSearchResult r;
            try { r = await Task.Run(() => AtlasQuery.Search(text)); }
            catch (Exception e) { Util.Log("atlas search: " + e); r = new AtlasSearchResult { Query = text }; }
            if (gen != searchGen) return;
            ShowResults(r);
        }

        void ShowResults(AtlasSearchResult r)
        {
            results = r;
            ResultsStack.Children.Clear();
            hitRows.Clear(); hitList.Clear(); selected = -1;
            if (r.Total == 0)
            {
                var box = new StackPanel { Margin = new Thickness(8, 12, 8, 12) };
                box.Children.Add(Tb("Nothing matches \"" + r.Query + "\"", "Text.Body"));
                var hint = Tb(AtlasQuery.NormalizeId(r.Query) == null && r.Query.Length < 2 ? "Type at least two letters." : "Try fewer letters, or paste a link like item:2589 or npc:46.", "Text.Secondary");
                hint.Margin = new Thickness(0, 4, 0, 0); hint.TextWrapping = TextWrapping.Wrap;
                box.Children.Add(hint);
                var layers = Tb(LayerHint(), "Text.Caption"); layers.Margin = new Thickness(0, 8, 0, 0); layers.TextWrapping = TextWrapping.Wrap; layers.Foreground = Res("Brush.Text.Tertiary");
                box.Children.Add(layers);
                ResultsStack.Children.Add(box);
            }
            else
            {
                bool first = true;
                foreach (var g in r.Groups)
                {
                    var head = new DockPanel { Margin = new Thickness(8, first ? 4 : 12, 8, 4) };
                    first = false;
                    var count = Tb(g.Total > g.Hits.Count ? "best " + g.Hits.Count + " of " + g.Total : g.Total.ToString(), "Text.Caption"); count.Foreground = Res("Brush.Text.Tertiary");
                    DockPanel.SetDock(count, Dock.Right);
                    head.Children.Add(count);
                    head.Children.Add(Tb(g.Title, "Text.Caption"));
                    ResultsStack.Children.Add(head);
                    foreach (var h in g.Hits) ResultsStack.Children.Add(HitRow(h));
                }
            }
            ResultsPanel.Visibility = Visibility.Visible;
            if (hitList.Count > 0) Select(0);
        }

        string LayerHint()
        {
            var l = new List<string>();
            if (!Services.Db2.ItemData.Ready) l.Add("item data is not read from your game");
            if (!Services.Atlas.Classic.ClassicAtlas.IsReady) l.Add("the Classic baseline is not on this PC");
            if (!Services.Maps.MapData.Ready) l.Add("maps are not read");
            return l.Count == 0 ? "" : "Searching without: " + string.Join(", ", l) + ".";
        }

        FrameworkElement HitRow(AtlasHit h)
        {
            var row = new ContentControl { Style = (Style)FindResource("Side.Row"), Padding = new Thickness(8, 4, 8, 4), MinHeight = 40, Tag = h.Id, Focusable = false };
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var tile = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            tile.SetResourceReference(Border.BackgroundProperty, "Brush.Surface2");
            var ar = new AtlasRow { Link = h.Id, Title = h.Name, Quality = h.Quality, IconId = h.IconId };
            var tg = new Grid();
            var letter = new TextBlock { Text = ar.ShowGlyph ? ar.Glyph : ar.Initial, FontSize = 16, Foreground = ar.ShowGlyph ? Res("Brush.Text.Tertiary") : ar.TileBrush, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            if (ar.ShowGlyph) letter.FontFamily = (FontFamily)FindResource("Font.Icons"); else letter.FontWeight = FontWeights.SemiBold;
            tg.Children.Add(letter);
            if (h.IconId > 0)
            {
                var img = new Image { Stretch = Stretch.UniformToFill };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                img.SetBinding(Image.SourceProperty, new System.Windows.Data.Binding("Icon") { Source = ar });
                tg.Children.Add(new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = img });
            }
            tile.Child = tg;
            g.Children.Add(tile);
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var name = Tb(h.Name, "Text.RowTitle"); name.Foreground = ar.TitleBrush; name.TextTrimming = TextTrimming.CharacterEllipsis;
            stack.Children.Add(name);
            if (!string.IsNullOrEmpty(h.Detail)) { var d = Tb(h.Detail, "Text.Caption"); d.TextTrimming = TextTrimming.CharacterEllipsis; stack.Children.Add(d); }
            Grid.SetColumn(stack, 1); g.Children.Add(stack);
            var lab = Tb(h.Label, "Text.Caption"); lab.Foreground = Res("Brush.Text.Tertiary"); lab.Margin = new Thickness(8, 0, 0, 0); lab.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(lab, 2); g.Children.Add(lab);
            row.Content = g;
            row.MouseLeftButtonUp += (s, e) => { e.Handled = true; ChooseHit(h); };
            hitRows.Add(row); hitList.Add(h);
            return row;
        }

        void Select(int i)
        {
            if (hitList.Count == 0) { selected = -1; return; }
            selected = ((i % hitList.Count) + hitList.Count) % hitList.Count;
            for (int k = 0; k < hitRows.Count; k++)
                if (k == selected) hitRows[k].SetResourceReference(Control.BackgroundProperty, "Brush.Accent.Tint"); else hitRows[k].ClearValue(Control.BackgroundProperty);
            hitRows[selected].BringIntoView();
        }

        void ChooseHit(AtlasHit h)
        {
            SearchBox.Text = "";
            CloseResults();
            _ = Open(h.Id);
            Scroller.Focus();
        }

        void Search_KeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Down: if (ResultsOpen) { e.Handled = true; Select(selected + 1); } else if (!string.IsNullOrWhiteSpace(SearchBox.Text)) { e.Handled = true; searchTask = RunSearchNow(); } break;
                case Key.Up: if (ResultsOpen) { e.Handled = true; Select(selected - 1); } break;
                case Key.Enter:
                    e.Handled = true;
                    if (ResultsOpen && selected >= 0 && selected < hitList.Count) ChooseHit(hitList[selected]);
                    else searchTask = EnterNow();
                    break;
                case Key.Escape:
                    if (ResultsOpen || !string.IsNullOrEmpty(SearchBox.Text)) { e.Handled = true; SearchBox.Text = ""; CloseResults(); }
                    break;
            }
        }

        // Enter before the results arrived: search now and open the best hit
        async Task EnterNow()
        {
            await RunSearchNow();
            if (results != null && hitList.Count > 0) ChooseHit(hitList[0]);
        }

        public void PressForTest(Key key) { Search_KeyDown(SearchBox, new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this) ?? new FakeSource(), 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent }); }
        sealed class FakeSource : PresentationSource
        {
            public override Visual RootVisual { get => null; set { } }
            public override bool IsDisposed => false;
            protected override CompositionTarget GetCompositionTargetCore() => null;
        }
    }
}
