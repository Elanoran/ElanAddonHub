using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // "All characters": what every character has (bags, bank, worn), read from the account SavedVariables of Elan's Bags.
    // Read-only: the hub never writes to the WoW folder. Re-reads when WoW saves the file (FileSystemWatcher + debounce).
    public partial class InventoryPage : UserControl
    {
        const int MaxRows = 250, ExtraMax = 40;

        static readonly Dictionary<string, string> ClassColors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["HUNTER"] = "#ABD473", ["WARRIOR"] = "#C79C6E", ["MAGE"] = "#69CCF0", ["ROGUE"] = "#FFF569", ["DRUID"] = "#FF7D0A",
            ["PALADIN"] = "#F58CBA", ["PRIEST"] = "#FFFFFF", ["SHAMAN"] = "#0070DE", ["WARLOCK"] = "#9482C9",
        };
        static readonly string[] QualityHex = { "#9D9D9D", "#FFFFFF", "#1EFF00", "#0070DD", "#A335EE", "#FF8000", "#E6CC80", "#00CCFF" };

        static Brush Frozen(string hex)
        {
            var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
            b.Freeze();
            return b;
        }
        static Brush Res(string key) => (Brush)Application.Current.Resources[key];
        internal static Brush ClassBrushOf(string cls) => cls != null && ClassColors.TryGetValue(cls, out var hex) ? Frozen(hex) : Res("Brush.Text.Primary");

        // one compact row of the character list: portrait, name in class colour, "Lv 27 Paladin". The rest is in the hover tooltip.
        public class CharRow : INotifyPropertyChanged
        {
            public InvChar Char;
            public bool Selected { get; set; }
            public event PropertyChangedEventHandler PropertyChanged;
            Brush portraitBrush;
            public string Key => Char.Key;
            public string Name => Char.Name;
            public string Initial => InventoryPage.Initial(Char.Name);
            public Brush ClassBrush => ClassBrushOf(Char.Class);
            public Brush ClassTint => TintOf(Char.Class);
            // "Lv 60 Hunter · Realm": the realm tells two characters of the same name apart
            public string Line2 => string.Join(" · ", new[] { string.Join(" ", new[] { Char.Level > 0 ? "Lv " + Char.Level : null, Pretty(Char.Class) }.Where(s => !string.IsNullOrEmpty(s))), Char.Realm }.Where(s => !string.IsNullOrEmpty(s)));
            public int PortraitId => CharPortrait.IconFor(Char.RaceFile, Char.Sex, Char.Class);
            public bool IsRacePortrait => CharPortrait.RaceIcon(Char.RaceFile, Char.Sex) > 0;
            public Brush PortraitBrush { get { return portraitBrush; } private set { portraitBrush = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("PortraitBrush")); } }
            public bool HasPortrait => portraitBrush != null;

            // short tooltip: name, "Realm · Faction", then one icon line - gold, bags, bank
            // [0] name  [1] realm · faction  [2] money  [3] bags "119 · 12h"  [4] bank "562 · 16h" or "not seen"
            public List<string> TipLines()
            {
                var where = string.Join(" · ", new[] { Char.Realm, Char.Faction }.Where(x => !string.IsNullOrEmpty(x)));
                return new List<string>
                {
                    Char.Name,
                    where,
                    InventoryReader.Money(Char.Money),
                    (Char.Bags?.Sum(i => i.Count) ?? 0).ToString("N0", ElansAddonHub.Lodge.CalendarRules.English) + " · " + ShortAgo(Char.BagsAt > 0 ? Char.BagsAt : Char.Updated),
                    Char.Bank == null ? "not seen" : Char.Bank.Sum(i => i.Count).ToString("N0", ElansAddonHub.Lodge.CalendarRules.English) + " · " + ShortAgo(Char.BankAt),
                };
            }

            static string ShortAgo(long unix)
            {
                if (unix <= 0) return "never";
                var span = DateTime.UtcNow - DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
                if (span.TotalMinutes < 1) return "now";
                if (span.TotalMinutes < 60) return (int)span.TotalMinutes + "m";
                if (span.TotalHours < 24) return (int)span.TotalHours + "h";
                return (int)span.TotalDays + "d";
            }

            public ToolTip Tip
            {
                get
                {
                    var l = TipLines();
                    var R = Application.Current.Resources;
                    var sp = new StackPanel();
                    sp.Children.Add(new TextBlock { Text = l[0], Style = (Style)R["Text.RowTitle"], Foreground = ClassBrush });
                    if (l[1].Length > 0) sp.Children.Add(new TextBlock { Text = l[1], Style = (Style)R["Text.Caption"], Foreground = Res("Brush.Text.Tertiary"), Margin = new Thickness(0, 2, 0, 0) });
                    var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
                    // a gold coin like the game's, then the bag and bank glyphs
                    line.Children.Add(new System.Windows.Shapes.Ellipse { Width = 10, Height = 10, Fill = Res("Brush.Warn"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
                    line.Children.Add(new TextBlock { Text = l[2], Style = (Style)R["Text.Secondary"], Foreground = Res("Brush.Warn"), VerticalAlignment = VerticalAlignment.Center });
                    Add(line, "\uE719", l[3], "Bags: items · last save");
                    Add(line, "\uE825", l[4], "Bank: items · last seen");
                    sp.Children.Add(line);
                    return new ToolTip { Style = (Style)R["CardTip"], Content = sp };
                }
            }

            static void Add(StackPanel line, string glyph, string text, string what)
            {
                var R = Application.Current.Resources;
                line.Children.Add(new TextBlock { Text = glyph, FontFamily = (FontFamily)R["Font.Icons"], FontSize = 12, Foreground = Res("Brush.Text.Tertiary"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 4, 0), ToolTip = what });
                line.Children.Add(new TextBlock { Text = text, Style = (Style)R["Text.Secondary"], VerticalAlignment = VerticalAlignment.Center });
            }

            // the portrait from the local game data: at once when it is on disk, else when it has been extracted
            public void LoadPortrait()
            {
                var id = PortraitId;
                if (id <= 0) return;
                var have = IconStore.TryGet(id);
                if (have != null) { PortraitBrush = MakeBrush(have); return; }
                var disp = Application.Current.Dispatcher;
                IconStore.Request(id, src => disp.BeginInvoke(new Action(() => PortraitBrush = MakeBrush(src))));
            }

            // the game icons have a baked-in bevelled frame: crop it so the circle shows the picture only
            internal static Brush MakeBrush(ImageSource src)
            {
                var b = new ImageBrush(src) { Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.RelativeToBoundingBox, Viewbox = new Rect(0.08, 0.08, 0.84, 0.84) };
                RenderOptions.SetBitmapScalingMode(b, BitmapScalingMode.HighQuality);
                b.Freeze();
                return b;
            }
        }

        static string Pretty(string cls) => string.IsNullOrEmpty(cls) ? null : char.ToUpperInvariant(cls[0]) + cls.Substring(1).ToLowerInvariant();

        // class colour at 20 % (circle behind the initial)
        static Brush TintOf(string cls)
        {
            if (cls == null || !ClassColors.TryGetValue(cls, out var hex)) return Res("Brush.Overlay.Chip");
            var c = (Color)ColorConverter.ConvertFromString(hex);
            var b = new SolidColorBrush(Color.FromArgb(0x33, c.R, c.G, c.B));
            b.Freeze();
            return b;
        }

        public class HoldingRow
        {
            public string Name { get; set; }
            public Brush ClassBrush { get; set; }
            public string Where { get; set; }
        }

        public class ResultRow
        {
            public string Name { get; set; }
            public int Id { get; set; }
            public Brush QualityBrush { get; set; }
            public int Quality { get; set; }
            public Brush TileFill { get; set; }
            public string Initial { get; set; }
            public string TotalText { get; set; }
            public List<HoldingRow> Holdings { get; set; }
            public bool NotOwned { get; set; }      // from the game data only: none of your characters has it
        }

        Func<string> wowRoot = () => null;
        readonly List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();
        readonly DispatcherTimer debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        List<InvChar> chars = new List<InvChar>();
        InvData data = new InvData();
        Dictionary<int, InvItemTotals> index = new Dictionary<int, InvItemTotals>();   // itemId -> what each character / guild bank holds, built once per load
        string filterKey;
        bool userAll;          // the player picked "All characters" (otherwise the newest character is shown first)
        string watchedRoot;
        public event Action GoToAddons;
        public event Action GoToSettings;
        public event Action<string> OpenAtlas;      // Ctrl+click on an item: its page in the Atlas
        bool forceCtrl;       // self-test: a click counts as Ctrl+click
        bool CtrlAtlas(int itemId, MouseButtonEventArgs e)
        {
            if (itemId <= 0 || ((Keyboard.Modifiers & ModifierKeys.Control) == 0 && !forceCtrl) || OpenAtlas == null) return false;
            e.Handled = true; OpenAtlas(ElansAddonHub.Services.Db2.AtlasIds.Item(itemId));
            return true;
        }
        void Result_Click(object sender, MouseButtonEventArgs e) { if ((sender as FrameworkElement)?.DataContext is ResultRow r) CtrlAtlas(r.Id, e); }

        public InventoryPage()
        {
            InitializeComponent();
            debounce.Tick += (s, e) => { debounce.Stop(); ReloadAsync(); };
            Loaded += (s, e) => { IconStore.ProblemChanged -= OnIconProblemChanged; IconStore.ProblemChanged += OnIconProblemChanged; EnsureWatchers(); ReloadAsync(); };
            Unloaded += (s, e) => { IconStore.ProblemChanged -= OnIconProblemChanged; DisposeWatchers(); };
            IsVisibleChanged += (s, e) => { if (IsVisible) { EnsureWatchers(); ReloadAsync(); } };
            EmptyIcon.Source = Application.Current.TryFindResource("Icon.Bags") as System.Windows.Media.ImageSource;
        }

        public void Init(Func<string> root)
        {
            wowRoot = root;
            IconStore.WowRoot = root;
            EnsureWatchers();
            ReloadAsync();
        }

        // (re)create the watchers when the WoW folder changed or new account folders appeared
        void EnsureWatchers()
        {
            var root = wowRoot();
            var dirs = InventoryReader.Dirs(root);
            if (watchedRoot == root && watchers.Count == dirs.Count * 2) return;
            DisposeWatchers();
            watchedRoot = root;
            // the legacy ElansBags.lua and the Hub's ElansHub.lua: one watcher each per SavedVariables folder, one shared 700 ms debounce
            foreach (var d in dirs)
                foreach (var file in new[] { InventoryReader.FileName, HubData.FileName })
                {
                    try
                    {
                        var w = new FileSystemWatcher(d, file) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime };
                        FileSystemEventHandler h = (s, e) => Dispatcher.BeginInvoke(new Action(() => { debounce.Stop(); debounce.Start(); }));
                        w.Changed += h; w.Created += h; w.Deleted += h;
                        w.Renamed += (s, e) => Dispatcher.BeginInvoke(new Action(() => { debounce.Stop(); debounce.Start(); }));
                        w.EnableRaisingEvents = true;
                        watchers.Add(w);
                    }
                    catch { }
                }
        }

        void DisposeWatchers()
        {
            foreach (var w in watchers) { try { w.EnableRaisingEvents = false; w.Dispose(); } catch { } }
            watchers.Clear();
            watchedRoot = null;
        }

        int loadGen;        // a newer load (or a synchronous Reload) makes an older, slower one drop its result
        bool loadedOnce;    // the first look shows skeleton rows until the file has been read

        static InvData ReadSafe(string root)
        {
            try { return CharacterMerge.Merge(HubData.Read(root), InventoryReader.ReadData(root)); }
            catch (Exception e) { Util.Log("inventory: " + e.Message); return new InvData(); }
        }

        // the page's own refreshes (shown, file changed, startup) read the SavedVariables off the UI thread; the first one shows a skeleton character list
        async void ReloadAsync()
        {
            var gen = ++loadGen;
            var root = wowRoot();
            if (!loadedOnce && !armoryMode) ShowCharSkeleton(true);
            var read = await Task.Run(() => ReadSafe(root));
            if (gen != loadGen) return;
            Apply(root, read);
        }

        void ShowCharSkeleton(bool on)
        {
            CharSkeleton.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            var rest = on || armoryMode ? Visibility.Collapsed : Visibility.Visible;
            AllCard.Visibility = CharList.Visibility = SaveHint.Visibility = rest;
        }
        public bool CharSkeletonForTest => CharSkeleton.Visibility == Visibility.Visible;
        // the page as it is on its very first look, before the saved bags have been read (screenshots)
        public void LoadingStateForTest()
        {
            EmptyBox.Visibility = Visibility.Collapsed; Body.Visibility = Visibility.Visible;
            CharList.ItemsSource = null; Summary.Text = "";
            ShowCharSkeleton(true);
        }

        // synchronous (tests, and callers that need the result at once)
        public void Reload()
        {
            loadGen++;
            var root = wowRoot();
            Apply(root, ReadSafe(root));
        }

        void Apply(string root, InvData read)
        {
            loadedOnce = true;
            ShowCharSkeleton(false);
            data = read;
            chars = read.Chars;
            index = InventoryReader.BuildIndex(chars.Where(c => !c.Hidden), read.Guilds);
            if (armoryMode) return;                 // the Lodge view is on screen: my own list is drawn again when the person switches back
            if (filterKey != null && !chars.Any(c => c.Key == filterKey && !c.Hidden)) filterKey = null;
            // first look: the character that was saved last (usually the one you just played)
            if (filterKey == null && !userAll)
            {
                var newest = chars.Where(c => !c.Hidden).OrderByDescending(c => c.Updated).FirstOrDefault();
                filterKey = newest?.Key;
            }
            Render(root);
        }

        List<InvChar> Visible => chars.Where(c => !c.Hidden).ToList();

        void Render(string root)
        {
            var vis = Visible;
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                ShowEmpty("I can't find your WoW folder", "Pick it in Settings > General, then come back here.", true);
                return;
            }
            if (vis.Count == 0)
            {
                bool any = InventoryReader.Files(root).Count > 0 || SavedVarsFiles.For(root, HubData.FileName).Count > 0;
                if (!any) ShowEmpty("No characters yet", "Install Elan's Hub and log in once on each character. WoW then saves what they carry and know when you /reload or log out, and it shows up here.", true);
                else ShowEmpty("No characters yet", "Elan's Hub is installed, but no character has been saved yet. Log in on each character once, then /reload or log out.", false);
                return;
            }
            EmptyBox.Visibility = Visibility.Collapsed;
            Body.Visibility = Visibility.Visible;
            var newest = vis.Max(c => c.Updated);
            Summary.Text = vis.Count + (vis.Count == 1 ? " character" : " characters") + " · " + TotalMoneyText(vis) + " in total · latest save " + InventoryReader.Ago(newest);
            var flavor = InventoryReader.FlavorOf(vis.Select(c => c.File).FirstOrDefault(f => f != null));
            if (flavor != null) IconStore.Flavor = flavor;
            var rows = vis.Select(c => new CharRow { Char = c, Selected = c.Key == filterKey }).ToList();
            foreach (var r in rows) r.LoadPortrait();
            CharList.ItemsSource = rows;
            AllLine.Text = vis.Count + (vis.Count == 1 ? " character" : " characters");
            AllCard.IsChecked = filterKey == null;
            RenderView();
        }

        // search results (all characters, or a search) or the bag view of the picked character
        void RenderView()
        {
            var vis = Visible;
            var only = filterKey == null ? null : vis.FirstOrDefault(c => c.Key == filterKey);
            bool search = !string.IsNullOrWhiteSpace(SearchBox.Text);
            bool results = only == null || search;
            TitleText.Text = only != null ? only.Name : "All characters";
            TitleText.Foreground = only != null ? ClassBrushOf(only.Class) : Res("Brush.Text.Primary");
            ClearFilter.Visibility = results && only != null ? Visibility.Visible : Visibility.Collapsed;
            ResultCaption.Visibility = results ? Visibility.Visible : Visibility.Collapsed;
            ResultScroll.Visibility = results ? Visibility.Visible : Visibility.Collapsed;
            TabStrip.Visibility = results ? Visibility.Collapsed : Visibility.Visible;
            BagsScroll.Visibility = results ? Visibility.Collapsed : Visibility.Visible;
            ViewInfo.Visibility = results ? Visibility.Collapsed : Visibility.Visible;
            if (results) { IconNote.Visibility = Visibility.Collapsed; ViewInfo.Text = ""; RenderResults(); }
            else RenderBags(only);
        }

        static string TotalMoneyText(List<InvChar> vis) => InventoryReader.Money(vis.Sum(c => c.Money));

        void ShowEmpty(string title, string text, bool addonsButton)
        {
            Body.Visibility = Visibility.Collapsed;
            CharList.ItemsSource = null;            // no stale rows behind the empty state (the folder may have changed)
            EmptyBox.Visibility = Visibility.Visible;
            EmptyTitle.Text = title;
            EmptyText.Text = text;
            EmptyButton.Visibility = addonsButton ? Visibility.Visible : Visibility.Collapsed;
            EmptyButton.Content = title.StartsWith("I can't") ? "Open Settings" : "Go to Addons";
            Summary.Text = "";
        }

        public string EmptyTitleForTest => EmptyBox.Visibility == Visibility.Visible ? EmptyTitle.Text : null;
        public string EmptyTextForTest => EmptyText.Text;

        void RenderResults()
        {
            var vis = Visible;
            var only = filterKey == null ? null : vis.FirstOrDefault(c => c.Key == filterKey);
            var query = SearchBox.Text ?? "";
            var all = InventoryReader.Search(vis, query, only, GuildsOf(only != null ? new List<InvChar> { only } : vis));
            // items of the game data (your own WoW files) that match the words but are on none of these characters
            var extra = new List<ResultRow>();
            if (query.Trim().Length >= 2 && ElansAddonHub.Services.Db2.ItemData.Ready)
            {
                var have = new HashSet<int>(all.Select(r => r.Id));
                foreach (var it in ElansAddonHub.Services.Db2.ItemData.Search(query, ExtraMax, i => have.Contains(i.Id)))
                {
                    int q = Math.Max(0, Math.Min(QualityHex.Length - 1, it.Quality));
                    extra.Add(new ResultRow
                    {
                        Name = it.Name, Id = it.Id, Quality = q, QualityBrush = Frozen(QualityHex[q]), TileFill = TileFillFor(q), Initial = Initial(it.Name), TotalText = "", NotOwned = true,
                        Holdings = new List<HoldingRow> { new HoldingRow { Name = "Not on your characters", ClassBrush = Res("Brush.Text.Secondary"), Where = "" } },
                    });
                }
            }
            var shown = all.Take(MaxRows).Select(r => new ResultRow
            {
                Name = r.Name,
                Id = r.Id,
                Quality = r.Quality,
                QualityBrush = Frozen(QualityHex[Math.Max(0, Math.Min(QualityHex.Length - 1, r.Quality))]),
                TileFill = TileFillFor(r.Quality),
                Initial = Initial(r.Name),
                TotalText = "× " + r.Total.ToString("N0", ElansAddonHub.Lodge.CalendarRules.English),
                Holdings = r.Holdings.OrderByDescending(h => h.Total).ThenBy(h => h.Char?.Name ?? h.GuildLabel).Select(h => new HoldingRow
                {
                    Name = h.Char != null ? h.Char.Name : h.GuildLabel,
                    ClassBrush = h.Char != null ? ClassBrushOf(h.Char.Class) : Res("Brush.Text.Primary"),
                    Where = string.Join(" · ", new[] { h.Bags > 0 ? "bags " + h.Bags : null, h.Bank > 0 ? "bank " + h.Bank : null, h.Equipped > 0 ? "worn" : null,
                        h.GuildBank > 0 ? "guild bank " + h.GuildBank + (h.GuildSeen > 0 ? " (seen " + InventoryReader.Ago(h.GuildSeen) + ")" : "") : null }.Where(s => s != null)),
                }).ToList(),
            }).ToList();
            if (shown.Count < MaxRows) shown.AddRange(extra.Take(MaxRows - shown.Count));
            Results.ItemsSource = shown;
            ResultCaption.Visibility = Visibility.Visible;
            var scope = only != null ? "on " + only.Name : "across all characters";
            ResultCaption.Text = query.Trim().Length == 0 ? "Everything " + scope : all.Count + (all.Count == 1 ? " match " : " matches ") + scope + (extra.Count > 0 ? " · plus " + extra.Count + " from the game data" : "");
            MoreText.Visibility = all.Count > MaxRows ? Visibility.Visible : Visibility.Collapsed;
            MoreText.Text = "Showing the first " + MaxRows + " of " + all.Count + " items. Type in the search box to narrow it down.";
            NoMatch.Visibility = all.Count == 0 && extra.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            NoMatch.Text = query.Trim().Length == 0 ? "No items saved yet." : "Nothing matches \"" + query.Trim() + "\".";
            ResultScroll.ScrollToTop();
        }

        // the guild banks of these characters (each guild once)
        List<InvGuild> GuildsOf(IEnumerable<InvChar> who)
        {
            var res = new List<InvGuild>();
            foreach (var c in who)
            {
                var g = GuildFor(c);
                if (g != null && !res.Contains(g)) res.Add(g);
            }
            return res;
        }

        static Brush TileFillFor(int q)
        {
            var c = (Color)ColorConverter.ConvertFromString(QualityHex[Math.Max(0, Math.Min(QualityHex.Length - 1, q))]);
            var b = new SolidColorBrush(Color.FromArgb(0x26, c.R, c.G, c.B));
            b.Freeze();
            return b;
        }

        static string Initial(string name)
        {
            foreach (var ch in name ?? "") if (char.IsLetterOrDigit(ch)) return char.ToUpperInvariant(ch).ToString();
            return "?";
        }

        void Search_Changed(object sender, TextChangedEventArgs e)
        {
            if (armoryMode) { if (!suppressSearch) RefreshArmory(false); return; }
            if (IsInitialized && Body != null && Body.Visibility == Visibility.Visible) RenderView();
        }

        void Char_Click(object sender, RoutedEventArgs e)
        {
            var key = (sender as FrameworkElement)?.Tag as string;
            filterKey = key;
            userAll = false;
            Render(wowRoot());
        }

        void All_Click(object sender, RoutedEventArgs e) { filterKey = null; userAll = true; Render(wowRoot()); }

        void ClearFilter_Click(object sender, RoutedEventArgs e) { filterKey = null; userAll = true; Render(wowRoot()); }

        void GoAddons_Click(object sender, RoutedEventArgs e)
        {
            if ((EmptyButton.Content as string) == "Open Settings") GoToSettings?.Invoke(); else GoToAddons?.Invoke();
        }

        // ---- self-test hooks
        public void SetSearchForTest(string text) { SearchBox.Text = text; if (Body.Visibility == Visibility.Visible) RenderView(); }
        public void FilterForTest(string key) { filterKey = key; userAll = key == null; Render(wowRoot()); }
        public int ResultCountForTest => (Results.ItemsSource as List<ResultRow>)?.Count ?? 0;
        public List<string> ResultTipForTest(int index) => ResultTipLines(((Results.ItemsSource as List<ResultRow>) ?? new List<ResultRow>()).ElementAtOrDefault(index));
        public List<string> ResultTextsForTest() => ((Results.ItemsSource as List<ResultRow>) ?? new List<ResultRow>())
            .Select(r => r.Name + " " + r.TotalText + " [" + string.Join("; ", r.Holdings.Select(h => h.Name + " " + h.Where)) + "]").ToList();
        public int CharCountForTest => (CharList.ItemsSource as List<CharRow>)?.Count ?? 0;
        // "Name|second line|portrait file id|race portrait?|has image yet|selected"
        public List<string> CharRowsForTest() => ((CharList.ItemsSource as List<CharRow>) ?? new List<CharRow>())
            .Select(r => r.Name + "|" + r.Line2 + "|" + r.PortraitId + "|" + r.IsRacePortrait + "|" + r.HasPortrait + "|" + r.Selected).ToList();
        public List<string> CharTipForTest(string key) => ((CharList.ItemsSource as List<CharRow>) ?? new List<CharRow>()).FirstOrDefault(r => r.Key == key)?.TipLines();
        public bool AllSelectedForTest => AllCard.IsChecked == true;
        public string AllLineForTest => AllLine.Text;
        public List<string> ItemTipForTest(int itemId, string charKey) => InventoryReader.TipFor(index, itemId, charKey == null ? null : chars.FirstOrDefault(c => c.Key == charKey)).Lines();
        public int IndexSizeForTest => index.Count;
        public string ResultCaptionForTest => ResultCaption.Text;
        public bool NoMatchVisibleForTest => NoMatch.Visibility == Visibility.Visible;
        public int WatcherCountForTest => watchers.Count;
        public FrameworkElement BodyForTest => this;
    }
}
