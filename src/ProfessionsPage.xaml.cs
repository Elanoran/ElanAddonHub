using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Professions;

namespace ElansAddonHub
{
    // Atlas layer H: the Professions page. Tabs: Recipes (every recipe of a profession in skill order, colours relative to a chosen character, how each is
    // learned and where its recipe item and reagents come from), Leveling guide (a generated 1 to max path, or the zone guide of a gathering profession),
    // Craft now (what the characters' bags and bank cover) and Camping (WoW Forever's campfire kits and camp objects). All data comes from the player's own
    // game files (SpellData / ItemData) plus the Classic baseline and the Lodge pool, always labelled; nothing is shipped.
    // The Hub does not save which recipes a character has learned, so nothing here claims to know that.
    public partial class ProfessionsPage : UserControl
    {
        Func<string> wowRoot = () => null;
        Func<LodgeAtlas> lodge = () => null;
        List<InvChar> inv = new List<InvChar>();
        List<ProfChar> chars = new List<ProfChar>();
        Dictionary<int, int> pool = new Dictionary<int, int>();
        ProfessionBook book;
        object bookFor;
        IProfessionSources sources;
        IGatherWorld world;
        string tab = "recipes", filter = "all";
        Profession prof;
        ProfChar who;
        bool building;                 // pickers are being filled: their events are ignored
        string pendingSlug;            // a profession asked for (from an Atlas id) before the recipe tables were read
        string raisedKey = "";
        public event Action<string, string> ViewChanged;       // (profession name, tab): the user picked another profession or tab, or the first profession appeared (the Atlas keeps its id in step)
        bool fixedChars;                // the self-test hands the characters in
        int loadGen;
        readonly System.Windows.Threading.DispatcherTimer searchDebounce = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };

        static readonly string[] QualityHex = { "#9D9D9D", "#FFFFFF", "#1EFF00", "#0070DD", "#A335EE", "#FF8000", "#E6CC80", "#00CCFF" };
        static readonly Dictionary<string, string> ClassColors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["HUNTER"] = "#ABD473", ["WARRIOR"] = "#C79C6E", ["MAGE"] = "#69CCF0", ["ROGUE"] = "#FFF569", ["DRUID"] = "#FF7D0A",
            ["PALADIN"] = "#F58CBA", ["PRIEST"] = "#FFFFFF", ["SHAMAN"] = "#0070DE", ["WARLOCK"] = "#9482C9",
        };

        public ProfessionsPage()
        {
            InitializeComponent();
            searchDebounce.Tick += (s, e) => { searchDebounce.Stop(); RenderBody(); };
            IsVisibleChanged += (s, e) => { if (IsVisible) ReloadAsync(); };
            SpellData.Changed += OnDataChanged;
        }

        void OnDataChanged()
        {
            try { Dispatcher.BeginInvoke(new Action(() => { if (IsVisible) { Refresh(); } })); } catch { }
        }

        // The page lives inside the Atlas (hosted category "professions"): no title and no panel of its own, the Atlas draws both.
        public void Embed()
        {
            Panel.Background = null; Panel.Margin = new Thickness(0); Panel.Padding = new Thickness(0);
            TitleBlock.Visibility = Visibility.Collapsed;
        }

        public string ProfessionNow => prof?.Name;
        public string TabNow => tab;

        void RaiseView()
        {
            raisedKey = (prof?.Name ?? "") + "|" + tab;
            ViewChanged?.Invoke(prof?.Name, tab);
        }

        // Shows a profession (its slug, "" = the one on screen) on a tab ("" = the tab on screen). The page is left as it is when that is what it shows already.
        public void Navigate(string slug, string tabName)
        {
            bool changed = false;
            building = true;
            try
            {
                if (!string.IsNullOrEmpty(tabName) && tabName != tab)
                {
                    tab = tabName; changed = true;
                    switch (tabName) { case "guide": TabGuide.IsChecked = true; break; case "craft": TabCraft.IsChecked = true; break; case "camp": TabCamp.IsChecked = true; break; default: TabRecipes.IsChecked = true; break; }
                }
                if (!string.IsNullOrEmpty(slug))
                {
                    var b = EnsureBook();
                    var p = b?.Professions.FirstOrDefault(x => ElansAddonHub.Services.Atlas.AtlasHosts.Slug(x.Name) == slug);
                    if (p == null) pendingSlug = b == null ? slug : null;
                    else if (!ReferenceEquals(p, prof) && p.Name != prof?.Name)
                    {
                        prof = p; changed = true; guideStart = null; guideTarget = null;
                        ProfPicker.SelectedIndex = b.Professions.IndexOf(p);
                    }
                }
                if (changed) { ApplyTabChrome(); FillChars(); }
            }
            finally { building = false; }
            if (changed) { RenderBody(); BodyScroll.ScrollToTop(); }
        }

        public void Init(Func<string> root, Func<LodgeAtlas> lodgeAtlas)
        {
            wowRoot = root ?? (() => null);
            lodge = lodgeAtlas ?? (() => null);
            sources = new LiveSources(lodge);
            world = new LiveGatherWorld(lodge);
            ReloadAsync();
        }

        // ------------------------------------------------------------------------------------------------------------ data
        static InvData ReadSafe(string root)
        {
            try { return CharacterMerge.Merge(HubData.Read(root), InventoryReader.ReadData(root)); }
            catch (Exception e) { Util.Log("professions: " + e.Message); return new InvData(); }
        }

        async void ReloadAsync()
        {
            if (fixedChars) return;
            var gen = ++loadGen;
            var root = wowRoot();
            var read = await Task.Run(() => ReadSafe(root));
            if (gen != loadGen) return;
            SetCharacters(read.Chars);
        }

        void SetCharacters(IEnumerable<InvChar> list)
        {
            inv = list.ToList();
            chars = Holdings.From(inv.Where(c => !c.Hidden));
            pool = Holdings.Pool(chars);
            Refresh();
            if (opts?.FollowedGuides != null && opts.FollowedGuides.Count > 0 && FollowSignature() != followSig) ScheduleFollowRebuild();     // a followed character's skill may have changed
        }

        ProfessionBook EnsureBook()
        {
            var data = SpellData.Data;
            if (!SpellData.Ready || data?.Spells == null) { book = null; bookFor = null; return null; }
            if (book != null && ReferenceEquals(bookFor, data)) return book;
            book = ProfessionBook.Build(data.Spells, ItemData.Get, data.Details?.Effects);
            bookFor = data;
            return book;
        }

        // ------------------------------------------------------------------------------------------------------------ pickers and tabs
        void Refresh()
        {
            var b = EnsureBook();
            building = true;
            try
            {
                // professions
                string keep = prof?.Name;
                if (pendingSlug != null && b != null)
                {
                    var pm = b.Professions.FirstOrDefault(x => ElansAddonHub.Services.Atlas.AtlasHosts.Slug(x.Name) == pendingSlug);
                    if (pm != null) keep = pm.Name;
                    pendingSlug = null;
                }
                ProfPicker.Items.Clear();
                if (b != null)
                    foreach (var p in b.Professions)
                    {
                        var kindWord = p.Kind == ProfKind.Secondary ? " (secondary)" : p.Kind == ProfKind.Gathering ? " (gathering)" : "";
                        ProfPicker.Items.Add(new ComboBoxItem { Content = p.Name + kindWord, Tag = p });
                    }
                var again = b?.ByName(keep) ?? b?.Professions.FirstOrDefault();
                prof = again;
                if (again != null) ProfPicker.SelectedIndex = b.Professions.IndexOf(again);
                FillChars();
            }
            finally { building = false; }
            Summary.Text = SummaryText(b);
            RenderBody();
            if (((prof?.Name ?? "") + "|" + tab) != raisedKey) RaiseView();
        }

        string SummaryText(ProfessionBook b)
        {
            if (b == null) return SpellData.Status == SpellData.State.Loading ? "Reading the game's recipe tables..." : "Needs the game data from your WoW installation.";
            int withProf = chars.Count(c => c.Skills.Count > 0);
            return b.Professions.Count + " professions, " + b.BySpell.Count.ToString("N0", CultureInfo.InvariantCulture) + " recipes from your game files"
                + (chars.Count > 0 ? ", " + chars.Count + " character" + (chars.Count == 1 ? "" : "s") + " (" + withProf + " with professions)" : "");
        }

        void FillChars()
        {
            string keep = who?.Key;
            CharPicker.Items.Clear();
            CharPicker.Items.Add(new ComboBoxItem { Content = tab == "craft" ? "All characters together" : "No character (colour bands only)", Tag = null });
            IEnumerable<ProfChar> list = chars;
            if ((tab == "recipes" || tab == "guide") && prof != null) list = chars.Where(c => c.Has(prof.Name));
            foreach (var c in list.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                string extra = (tab == "recipes" || tab == "guide") && prof != null ? " - " + prof.Name + " " + c.SkillOf(prof.Name) + "/" + c.MaxOf(prof.Name) : "";
                CharPicker.Items.Add(new ComboBoxItem { Content = c.Name + extra, Tag = c, Foreground = ClassBrush(c.Class) });
            }
            who = chars.FirstOrDefault(c => c.Key == keep);
            int idx = 0;
            for (int i = 1; i < CharPicker.Items.Count; i++) if (((ComboBoxItem)CharPicker.Items[i]).Tag == who && who != null) idx = i;
            if (idx == 0 && who != null && !list.Contains(who)) who = null;
            // first look: the character with the highest skill in this profession
            if (who == null && idx == 0 && (tab == "recipes" || tab == "guide") && prof != null && !userNoChar)
            {
                var best = list.OrderByDescending(c => c.SkillOf(prof.Name)).FirstOrDefault();
                if (best != null) { who = best; for (int i = 1; i < CharPicker.Items.Count; i++) if (((ComboBoxItem)CharPicker.Items[i]).Tag == best) idx = i; }
            }
            CharPicker.SelectedIndex = idx;
        }
        bool userNoChar;

        void Tab_Checked(object sender, RoutedEventArgs e)
        {
            if (!(sender is RadioButton rb) || Body == null) return;
            tab = (string)rb.Tag;
            if (building) return;
            ApplyTabChrome();
            building = true; try { FillChars(); } finally { building = false; }
            RenderBody();
            BodyScroll.ScrollToTop();
            RaiseView();
        }

        void ApplyTabChrome()
        {
            ProfPicker.Visibility = tab == "recipes" || tab == "guide" ? Visibility.Visible : Visibility.Collapsed;
            CharPicker.Visibility = tab == "camp" ? Visibility.Collapsed : Visibility.Visible;
            SearchBox.Visibility = tab == "recipes" || tab == "craft" ? Visibility.Visible : Visibility.Collapsed;
            FilterHost.Visibility = tab == "recipes" ? Visibility.Visible : Visibility.Collapsed;
        }

        void Prof_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (building || !(ProfPicker.SelectedItem is ComboBoxItem it)) return;
            prof = it.Tag as Profession;
            building = true; try { FillChars(); } finally { building = false; }
            guideStart = null; guideTarget = null;
            RenderBody();
            BodyScroll.ScrollToTop();
            RaiseView();
        }

        void Char_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (building || !(CharPicker.SelectedItem is ComboBoxItem it)) return;
            who = it.Tag as ProfChar;
            userNoChar = who == null;
            guideStart = null;
            RenderBody();
        }

        void Search_Changed(object sender, TextChangedEventArgs e) { searchDebounce.Stop(); searchDebounce.Start(); }

        void Filter_Checked(object sender, RoutedEventArgs e)
        {
            if (!(sender is RadioButton rb) || Body == null) return;
            filter = (string)rb.Tag;
            if (!building) RenderBody();
        }

        // ------------------------------------------------------------------------------------------------------------ the body
        void RenderBody()
        {
            if (Body == null) return;
            ApplyTabChrome();
            Body.Children.Clear();
            var b = EnsureBook();
            if (b == null) { Body.Children.Add(EmptyState(SpellData.Status == SpellData.State.Loading ? "Reading the game's recipe tables..." : "No game data yet", SpellData.Status == SpellData.State.Loading ? "This takes a few seconds the first time." : "Professions are built from the recipe tables in your own WoW installation. Switch on \"Read item data from my WoW installation\" in Settings and make sure the WoW folder is set.")); return; }
            switch (tab)
            {
                case "guide": RenderGuide(b); break;
                case "craft": RenderCraft(b); break;
                case "camp": RenderCamp(b); break;
                default: RenderRecipes(b); break;
            }
        }

        // ------------------------------------------------------------------------------------------------------------ small builders
        static Brush Res(string key) => (Brush)Application.Current.Resources[key];
        static Brush Frozen(string hex) { var br = (SolidColorBrush)new BrushConverter().ConvertFromString(hex); br.Freeze(); return br; }
        static Brush ClassBrush(string cls) => cls != null && ClassColors.TryGetValue(cls, out var hex) ? Frozen(hex) : Res("Brush.Text.Primary");
        static Brush QualityBrush(int q) => Frozen(QualityHex[Math.Max(0, Math.Min(QualityHex.Length - 1, q))]);
        static Brush ColorBrush(SkillColor c) => Frozen(RecipeBands.Hex(c));
        Style Sty(string key) => (Style)FindResource(key);

        TextBlock Line(string text, string style, Brush fg = null, double top = 0)
        {
            var t = new TextBlock { Text = text ?? "", Style = Sty(style), Margin = new Thickness(0, top, 0, 0) };
            if (fg != null) t.Foreground = fg;
            return t;
        }

        // a text that opens an Atlas link
        TextBlock LinkText(string text, string link, string style = "Text.Body", Brush fg = null)
        {
            var t = Line(text, style, fg ?? Res("Brush.Text.Primary"));
            if (string.IsNullOrEmpty(link)) return t;
            t.Cursor = Cursors.Hand;
            t.ToolTip = "Open " + link + " in the Atlas";
            var normal = t.TextDecorations;
            t.MouseEnter += (s, e) => t.TextDecorations = TextDecorations.Underline;
            t.MouseLeave += (s, e) => t.TextDecorations = normal;
            t.MouseLeftButtonUp += (s, e) => { ProfessionLinks.Open(link); e.Handled = true; };
            return t;
        }

        FrameworkElement EmptyState(string title, string text)
        {
            var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 460, Margin = new Thickness(0, 32, 0, 0) };
            sp.Children.Add(new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("Font.Icons"), FontSize = 32, Foreground = Res("Brush.Text.Tertiary"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 12) });
            var t = Line(title, "Text.Section"); t.HorizontalAlignment = HorizontalAlignment.Center; t.TextAlignment = TextAlignment.Center; sp.Children.Add(t);
            var s = Line(text, "Text.Secondary", null, 8); s.TextWrapping = TextWrapping.Wrap; s.TextAlignment = TextAlignment.Center; sp.Children.Add(s);
            return sp;
        }

        Border Card(UIElement child, double bottom = 12)
        {
            return new Border { Background = Res("Brush.Surface1"), CornerRadius = (CornerRadius)FindResource("Radius.Card"), Padding = new Thickness(16), Margin = new Thickness(0, 0, 0, bottom), Child = child };
        }

        FrameworkElement Section(string title, string note = null)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 12, 0, 8) };
            sp.Children.Add(Line(title, "Text.Section"));
            if (!string.IsNullOrEmpty(note)) { var n = Line(note, "Text.Secondary", null, 2); n.TextWrapping = TextWrapping.Wrap; sp.Children.Add(n); }
            return sp;
        }

        Border ChipText(string text, Brush fg = null, Brush bg = null)
        {
            return new Border { Style = Sty("Chip"), Background = bg ?? Res("Brush.Overlay.Chip"), Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 8, 0), Child = Line(text, "Text.Caption", fg ?? Res("Brush.Text.Secondary")) };
        }

        // an icon tile with the fallback letter until the picture is on disk
        FrameworkElement IconTile(int iconId, string name, int quality, double size)
        {
            var g = new Grid { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
            var fb = new Border { CornerRadius = (CornerRadius)FindResource("Radius.Control"), Background = Res("Brush.Overlay.Chip"), Child = new TextBlock { Text = string.IsNullOrEmpty(name) ? "?" : name.Substring(0, 1).ToUpperInvariant(), Foreground = QualityBrush(quality), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            var img = new Image { Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            g.Children.Add(fb); g.Children.Add(img);
            if (iconId > 0)
            {
                var have = IconStore.TryGet(iconId);
                if (have != null) { img.Source = have; fb.Visibility = Visibility.Collapsed; }
                else IconStore.Request(iconId, src => Dispatcher.BeginInvoke(new Action(() => { img.Source = src; fb.Visibility = Visibility.Collapsed; })));
            }
            return g;
        }

        string NameOf(int itemId) { var it = ItemData.Get(itemId); return it != null && !string.IsNullOrEmpty(it.Name) ? it.Name : "item " + itemId; }

        int Have(int itemId) => pool.TryGetValue(itemId, out var n) ? n : 0;

        // ------------------------------------------------------------------------------------------------------------ item tooltips
        // the item's facts as the client's table gives them (the same lines as the Characters page), then what the caller adds
        void AddItemFacts(GameTooltip gt, int id)
        {
            var it = ItemData.Get(id);
            if (it == null) { gt.AddText(ItemData.Ready ? ItemText.NotRevealed : "Item data is not read yet.", "unrevealed"); return; }
            gt.AddItem(it);
        }

        ToolTip LazyTip(Func<GameTooltip> build)
        {
            var tt = GameTooltip.Wrap(null);
            tt.Opened += (s, e) => { try { tt.Content = build(); } catch (Exception ex) { var g = new GameTooltip("Could not build the tooltip"); g.AddText(ex.Message, "plain"); tt.Content = g; } };
            return tt;
        }

        static void SetTip(FrameworkElement el, ToolTip tt)
        {
            el.ToolTip = tt;
            ToolTipService.SetInitialShowDelay(el, 150);
            ToolTipService.SetShowDuration(el, 30000);
        }

        // ------------------------------------------------------------------------------------------------------------ test hooks
        public void SetCharactersForTest(IEnumerable<InvChar> list) { loadGen++; SetCharacters(list); }
        public void SetProfCharsForTest(IEnumerable<ProfChar> list) { fixedChars = true; loadGen++; chars = list.ToList(); inv = new List<InvChar>(); pool = Holdings.Pool(chars); Refresh(); }
        public void SetSourcesForTest(IProfessionSources s, IGatherWorld w) { sources = s; world = w; }
        public IReadOnlyList<ProfChar> CharsForTest => chars;
        public ProfessionBook BookForTest => EnsureBook();
        public void ResetForTest() { fixedChars = false; book = null; bookFor = null; prof = null; who = null; userNoChar = false; guideStart = null; guideTarget = null; guideScope = "all"; guideCamp = false; lastGuide = null; lastGather = null; filter = "all"; SearchBox.Text = ""; FilterAll.IsChecked = true; TabRecipes.IsChecked = true; tab = "recipes"; }

        public void ShowForTest(string tabName, string profession = null, string character = null)
        {
            building = true;
            try
            {
                switch (tabName) { case "guide": TabGuide.IsChecked = true; break; case "craft": TabCraft.IsChecked = true; break; case "camp": TabCamp.IsChecked = true; break; default: TabRecipes.IsChecked = true; break; }
                tab = tabName;
                var b = EnsureBook();
                if (b != null && profession != null) { var p = b.ByName(profession); if (p != null) { prof = p; ProfPicker.SelectedIndex = b.Professions.IndexOf(p); } }
                who = character == null ? null : chars.FirstOrDefault(c => string.Equals(c.Name, character, StringComparison.OrdinalIgnoreCase));
                userNoChar = who == null;
                ApplyTabChrome();
                FillChars();
            }
            finally { building = false; }
            RenderBody();
            BodyScroll.ScrollToTop();
        }

        // what the user does: pick a tab / a profession (raises ViewChanged, unlike ShowForTest)
        public void PickTabForTest(string tabName) { (tabName == "guide" ? TabGuide : tabName == "craft" ? TabCraft : tabName == "camp" ? TabCamp : TabRecipes).IsChecked = true; }
        public void PickProfessionForTest(string name)
        {
            for (int i = 0; i < ProfPicker.Items.Count; i++)
                if (((ComboBoxItem)ProfPicker.Items[i]).Tag is Profession p && p.Name == name) { ProfPicker.SelectedIndex = i; return; }
        }
        public bool TitleHiddenForTest => TitleBlock.Visibility != Visibility.Visible;

        public void SearchForTest(string text) { SearchBox.Text = text; searchDebounce.Stop(); RenderBody(); }
        public void FilterForTest(string f) { filter = f; building = true; try { (f == "now" ? FilterNow : f == "later" ? FilterLater : FilterAll).IsChecked = true; } finally { building = false; } RenderBody(); }

        // every text on the page body, top to bottom
        public List<string> BodyTextsForTest()
        {
            var res = new List<string>();
            void Walk(DependencyObject o)
            {
                if (o is TextBlock tb) { if (!string.IsNullOrEmpty(tb.Text)) res.Add(tb.Text); return; }
                foreach (var ch in LogicalTreeHelper.GetChildren(o).OfType<DependencyObject>()) Walk(ch);
            }
            Walk(Body);
            return res;
        }

        public FrameworkElement BodyForTest => Body;
    }
}
