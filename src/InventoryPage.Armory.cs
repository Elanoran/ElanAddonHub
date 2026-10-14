using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // The Lodge armory on the Characters page: a switch at the top ("My characters" | "Lodge"). The Lodge view lists the characters members chose to share (class, level,
    // profession and name filters) and opens one in the SAME Overview / Gear / Talents / Professions / Pet views my own characters use - a shared snapshot is turned back into
    // the Hub-module model (ArmoryShare.ToChar), so nothing is drawn twice. Read-only: no Bags, Bank or Guild bank tabs, no gold, no counts of my own items. Items open in the
    // Atlas with Ctrl+click like everywhere else. What is shared and what is not: ArmoryShare in Lodge\ArmoryModels.cs.
    public partial class InventoryPage
    {
        LodgeSession armorySession;
        bool armoryMode;                   // the Lodge view is the one on screen
        bool lodgeView;                    // a shared character is being drawn (Overview, Gear... leave out gold, bags, zone; no counts of my items)
        string lodgeBy, lodgeBuild;        // the member who shared it; its build code
        string armoryKey;                  // "owner|key" of the open character
        string ownSearch = "", armSearch = "";
        bool suppressSearch, suppressFilter;
        List<ArmoryEntry> armoryShown = new List<ArmoryEntry>();
        readonly Dictionary<string, KeyValuePair<object, InvChar>> armoryChars = new Dictionary<string, KeyValuePair<object, InvChar>>();   // id -> (snapshot it was made from, character)
        string armoryNote;                 // what the page says instead of the list (offline, old server, loading, none shared), null while the list shows
        string SrcLabel => lodgeView ? (string.IsNullOrEmpty(lodgeBy) ? "shared by a member" : "shared by " + lodgeBy) : CharacterMerge.FromHub;

        public void InitArmory(LodgeSession session)
        {
            armorySession = session;
            session.ArmoryChanged += () => Dispatcher.BeginInvoke(new Action(() => { if (armoryMode) RefreshArmory(false); }));
            session.Changed += () => Dispatcher.BeginInvoke(new Action(() => { if (armoryMode && (armoryNote != null || session.ArmoryOfflineLine != null)) RefreshArmory(false); }));
        }

        // one row of the Lodge list
        public class ArmoryRow : INotifyPropertyChanged
        {
            public ArmoryEntry Entry;
            public bool Selected { get; set; }
            public string ProfFilter;
            public event PropertyChangedEventHandler PropertyChanged;
            Brush portraitBrush;
            public string Id => Entry.Id;
            public string Name => Entry.Name;
            public string Initial => InventoryPage.Initial(Entry.Name);
            public Brush ClassBrush => ClassBrushOf(Entry.Class);
            public Brush ClassTint => TintOf(Entry.Class);
            public int PortraitId => CharPortrait.IconFor(Entry.RaceFile, Entry.Sex, Entry.Class);
            public Brush PortraitBrush { get { return portraitBrush; } private set { portraitBrush = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("PortraitBrush")); } }

            // "Lv 60 Hunter · Mia"; with a profession filter on, the skill replaces the member: "Lv 60 Hunter · Alchemy 150"
            public string Line2
            {
                get
                {
                    var lv = string.Join(" ", new[] { Entry.Level > 0 ? "Lv " + Entry.Level : null, Entry.ClassName }.Where(s => !string.IsNullOrEmpty(s)));
                    string last = Entry.By;
                    if (!string.IsNullOrEmpty(ProfFilter))
                    {
                        var p = Entry.Professions.FirstOrDefault(x => string.Equals(x.Name, ProfFilter, StringComparison.OrdinalIgnoreCase));
                        if (p != null) last = p.Name + " " + p.Skill;
                    }
                    return string.Join(" · ", new[] { lv, last }.Where(s => !string.IsNullOrEmpty(s)));
                }
            }

            public ToolTip Tip
            {
                get
                {
                    var R = Application.Current.Resources;
                    var sp = new StackPanel();
                    sp.Children.Add(new TextBlock { Text = Entry.Name, Style = (Style)R["Text.RowTitle"], Foreground = ClassBrush });
                    var id = string.Join(" ", new[] { Entry.Level > 0 ? "Level " + Entry.Level : null, Entry.Race, Entry.ClassName }.Where(s => !string.IsNullOrEmpty(s)));
                    if (id.Length > 0) sp.Children.Add(new TextBlock { Text = id, Style = (Style)R["Text.Secondary"], Margin = new Thickness(0, 2, 0, 0) });
                    var where = string.Join(" · ", new[] { Entry.Realm, Entry.Faction, string.IsNullOrEmpty(Entry.Guild) ? null : "<" + Entry.Guild + ">" }.Where(s => !string.IsNullOrEmpty(s)));
                    if (where.Length > 0) sp.Children.Add(new TextBlock { Text = where, Style = (Style)R["Text.Caption"], Margin = new Thickness(0, 2, 0, 0) });
                    var stats = string.Join(" · ", new[] { Entry.Ilvl > 0 ? "Item level " + Entry.Ilvl.ToString("0.#", CultureInfo.InvariantCulture) : null,
                        Entry.Professions.Count > 0 ? string.Join(", ", Entry.Professions.Select(p => p.Name + " " + p.Skill)) : null }.Where(s => !string.IsNullOrEmpty(s)));
                    if (stats.Length > 0) sp.Children.Add(new TextBlock { Text = stats, Style = (Style)R["Text.Secondary"], Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 280 });
                    sp.Children.Add(new TextBlock { Text = "Shared by " + Entry.By + " · updated " + InventoryReader.Ago(Entry.At > 0 ? Entry.At : Entry.Up), Style = (Style)R["Text.Caption"], Margin = new Thickness(0, 8, 0, 0) });
                    return new ToolTip { Style = (Style)R["CardTip"], Content = sp };
                }
            }

            public void LoadPortrait()
            {
                var id = PortraitId;
                if (id <= 0) return;
                var have = IconStore.TryGet(id);
                if (have != null) { PortraitBrush = CharRow.MakeBrush(have); return; }
                var disp = Application.Current.Dispatcher;
                IconStore.Request(id, src => disp.BeginInvoke(new Action(() => PortraitBrush = CharRow.MakeBrush(src))));
            }
        }

        // ---- the switch
        void Mode_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsInitialized || Body == null) return;
            SetMode((sender as FrameworkElement)?.Tag as string == "lodge");
        }

        void SetMode(bool lodge)
        {
            if (armoryMode == lodge) return;
            suppressSearch = true;
            try
            {
                if (lodge) ownSearch = SearchBox.Text ?? ""; else armSearch = SearchBox.Text ?? "";
                armoryMode = lodge;
                SearchBox.Text = lodge ? armSearch : ownSearch;
                SearchBox.Tag = lodge ? "Search characters, members, realms" : "Search every bag, bank and worn item";
                SearchBox.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, lodge ? "Search the Lodge's characters" : "Search all characters");
            }
            finally { suppressSearch = false; }
            if (lodge) EnterArmory(); else LeaveArmory();
        }

        void EnterArmory()
        {
            AllCard.Visibility = CharList.Visibility = SaveHint.Visibility = CharSkeleton.Visibility = Visibility.Collapsed;
            TabBags.Visibility = TabBank.Visibility = TabGuild.Visibility = Visibility.Collapsed;
            ArmoryLeft.Visibility = Visibility.Visible;
            ClearFilter.Visibility = Visibility.Collapsed;
            ResultCaption.Visibility = Visibility.Collapsed;
            if (tab == "bags" || tab == "bank" || tab == "guild") tab = "overview";
            armorySession?.RequestArmoryList(true);
            RefreshArmory(false);
        }

        void LeaveArmory()
        {
            lodgeView = false; lodgeBy = lodgeBuild = null;
            ArmoryLeft.Visibility = Visibility.Collapsed;
            TabBags.Visibility = TabBank.Visibility = Visibility.Visible;
            ShowCharSkeleton(!loadedOnce);
            Reload();                       // my own list and view, drawn again from the Hub data
        }

        public void ShowLodge(bool on) { if (on) ModeLodge.IsChecked = true; else ModeMine.IsChecked = true; }

        // ---- the list
        static readonly string[] AnyOption = { "All classes", "Any level", "Any profession" };

        static string LevelBracket(int level) => level >= 60 ? "Level 60" : (level / 10 * 10) + "-" + (level / 10 * 10 + 9);

        static IEnumerable<string> Brackets(IEnumerable<int> levels) => levels.Select(LevelBracket).Distinct()
            .OrderByDescending(b => int.Parse(b.Replace("Level ", "").Split('-')[0], CultureInfo.InvariantCulture));

        void FillOptions(ComboBox box, string any, IEnumerable<string> values)
        {
            var wanted = new List<string> { any };
            wanted.AddRange(values);
            var have = box.ItemsSource as List<string>;
            if (have != null && have.SequenceEqual(wanted)) return;
            var current = box.SelectedItem as string;
            suppressFilter = true;
            try
            {
                box.ItemsSource = wanted;
                box.SelectedIndex = Math.Max(0, current == null ? 0 : wanted.IndexOf(current));
            }
            finally { suppressFilter = false; }
        }

        string Picked(ComboBox box) => box.SelectedIndex > 0 ? box.SelectedItem as string : null;

        void ArmoryFilter_Changed(object sender, SelectionChangedEventArgs e) { if (!suppressFilter && armoryMode && IsInitialized) RefreshArmory(false); }

        // the page says something instead of a list
        void ShowArmoryNote(string title, string text)
        {
            armoryNote = title;
            Body.Visibility = Visibility.Collapsed;
            EmptyBox.Visibility = Visibility.Visible;
            EmptyTitle.Text = title;
            EmptyText.Text = text ?? "";
            EmptyButton.Visibility = Visibility.Collapsed;
            TitleText.Text = "Lodge armory";
            TitleText.Foreground = Res("Brush.Text.Primary");
            Summary.Text = "";
        }

        void RefreshArmory(bool ask)
        {
            if (!armoryMode) return;
            var s = armorySession;
            TitleText.Text = "Lodge armory";
            var off = s?.ArmoryOfflineLine;
            if (s == null || off != null)
            {
                ShowArmoryNote(off != null && off.StartsWith("You are offline") ? "You are offline" : off != null && off.Contains("older than 2.16") ? "This Lodge has no Armory yet" : off != null && off.Contains("switched the Armory off") ? "The Armory is switched off" : off != null && off.Contains("Guests") ? "The Armory is for members" : "The Armory is not available",
                    off ?? "Connect to your Lodge in the Lodge tab.");
                return;
            }
            if (!s.ArmoryListed) { ShowArmoryNote("Loading the Lodge's characters", "One moment."); return; }
            var all = s.ArmoryEntries;
            if (all.Count == 0)
            {
                ShowArmoryNote("No characters shared yet", "Members choose which of their characters the Lodge sees in Settings > Lodge > Armory. Nothing is shared until they do.");
                return;
            }
            armoryNote = null;
            EmptyBox.Visibility = Visibility.Collapsed;
            Body.Visibility = Visibility.Visible;

            FillOptions(ArmoryClass, AnyOption[0], all.Select(e => e.ClassName).Where(n => n.Length > 0).Distinct().OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase));
            FillOptions(ArmoryLevel, AnyOption[1], Brackets(all.Select(e => e.Level)));
            FillOptions(ArmoryProf, AnyOption[2], all.SelectMany(e => e.Professions.Select(p => p.Name)).Distinct().OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase));

            var cls = Picked(ArmoryClass); var lvl = Picked(ArmoryLevel); var prof = Picked(ArmoryProf);
            var q = (SearchBox.Text ?? "").Trim();
            IEnumerable<ArmoryEntry> list = all;
            if (cls != null) list = list.Where(e => e.ClassName == cls);
            if (lvl != null) list = list.Where(e => LevelBracket(e.Level) == lvl);
            if (prof != null) list = list.Where(e => e.Has(prof));
            if (q.Length > 0)
                list = list.Where(e => new[] { e.Name, e.By, e.Realm, e.Guild, e.ClassName, e.Race }.Any(f => !string.IsNullOrEmpty(f) && f.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0));
            armoryShown = prof != null
                ? list.OrderByDescending(e => e.Professions.First(p => string.Equals(p.Name, prof, StringComparison.OrdinalIgnoreCase)).Skill).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList()
                : list.OrderByDescending(e => e.Level).ThenByDescending(e => e.Ilvl).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

            if (armoryKey == null || !armoryShown.Any(e => e.Id == armoryKey)) armoryKey = armoryShown.FirstOrDefault()?.Id;
            var rows = armoryShown.Select(e => new ArmoryRow { Entry = e, Selected = e.Id == armoryKey, ProfFilter = prof }).ToList();
            foreach (var r in rows) r.LoadPortrait();
            ArmoryList.ItemsSource = rows;
            ArmoryNoMatch.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            var members = armoryShown.Select(e => e.Owner).Distinct().Count();
            ArmoryCount.Text = armoryShown.Count == all.Count ? all.Count + (all.Count == 1 ? " character" : " characters") + " from " + members + (members == 1 ? " member" : " members")
                : armoryShown.Count + " of " + all.Count + " characters";
            Summary.Text = ArmoryCount.Text + (prof != null ? " · " + prof + " first" : "");

            var open = armoryShown.FirstOrDefault(e => e.Id == armoryKey);
            if (open == null)
            {
                lodgeView = true;
                TitleText.Text = "Lodge armory";
                TitleText.Foreground = Res("Brush.Text.Primary");
                TabStrip.Visibility = Visibility.Collapsed; ViewInfo.Visibility = Visibility.Collapsed;
                ResultScroll.Visibility = Visibility.Collapsed; BagsScroll.Visibility = Visibility.Collapsed;
                return;
            }
            RenderArmoryChar(open);
        }

        void ArmoryRow_Click(object sender, RoutedEventArgs e)
        {
            armoryKey = (sender as FrameworkElement)?.Tag as string;
            RefreshArmory(false);
        }

        // ---- one shared character
        void RenderArmoryChar(ArmoryEntry e)
        {
            var s = armorySession;
            ClearFilter.Visibility = Visibility.Collapsed; ResultCaption.Visibility = Visibility.Collapsed; ResultScroll.Visibility = Visibility.Collapsed;
            TabStrip.Visibility = Visibility.Visible; BagsScroll.Visibility = Visibility.Visible; ViewInfo.Visibility = Visibility.Visible;
            TitleText.Text = e.Name;
            TitleText.Foreground = ClassBrushOf(e.Class);
            TabBags.Visibility = TabBank.Visibility = TabGuild.Visibility = Visibility.Collapsed;
            bool hunter = string.Equals(e.Class, "HUNTER", StringComparison.OrdinalIgnoreCase);
            TabPet.Visibility = hunter ? Visibility.Visible : Visibility.Collapsed;
            if (tab == "bags" || tab == "bank" || tab == "guild" || (tab == "pet" && !hunter)) tab = "overview";
            SetTabChecked(tab);
            BagsHost.Children.Clear(); iconTargets.Clear(); panelInfo.Clear(); panelChips.Clear();
            BagsMessage.Visibility = Visibility.Collapsed; lastMessage = null;
            CharHost.Children.Clear();
            BagsHost.Visibility = Visibility.Collapsed;
            CharHost.Visibility = Visibility.Visible;
            charTabInfo.Clear();

            var snap = s.ArmoryChar(e.Id);
            if (snap == null)
            {
                lodgeView = true;
                ViewInfo.Text = "";
                s.RequestArmoryChar(e);
                ShowEmptyTab("Opening " + e.Name + "...", "Shared by " + e.By + ".");
                return;
            }
            if (!armoryChars.TryGetValue(e.Id, out var made) || !ReferenceEquals(made.Key, snap))
            {
                var c = ArmoryShare.ToChar(e.Owner, snap);
                armoryChars[e.Id] = made = new KeyValuePair<object, InvChar>(snap, c);
            }
            var ch = made.Value;
            if (ch == null) { lodgeView = true; ShowEmptyTab("This character could not be opened.", null); return; }
            lodgeView = true;
            lodgeBy = e.By;
            lodgeBuild = ArmoryShare.BuildOf(snap);
            RenderCharTab(ch);
            UpdateIconNote();
            RequestIcons();
        }

        // the build code of the shared talents, ready to copy into the Talents page's import box
        FrameworkElement BuildCodeCard(string code)
        {
            var sp = new StackPanel();
            sp.Children.Add(Line("Build code", "Text.Caption"));
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
            var copy = new Button { Style = (Style)FindResource("Button.S"), Content = "Copy", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), ToolTip = "Copy the build code. Paste it into the Talents page to open this build." };
            copy.Click += (s, a) => { try { Clipboard.SetText(code); copy.Content = "Copied"; } catch (Exception) { copy.Content = "Could not copy"; } };
            DockPanel.SetDock(copy, Dock.Right);
            row.Children.Add(copy);
            row.Children.Add(new TextBlock { Text = code, Style = TextStyle("Text.Secondary"), Foreground = Res("Brush.Text.Primary"), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(row);
            charTabInfo.Add("build|" + code);
            var card = CardBox(sp);
            card.MaxWidth = 680; card.HorizontalAlignment = HorizontalAlignment.Left;
            return card;
        }

        // ---- self-test hooks
        public bool ArmoryModeForTest => armoryMode;
        public void ModeForTest(bool lodge) => ShowLodge(lodge);
        public string ArmoryNoteForTest => armoryNote == null ? null : EmptyTitle.Text + "|" + EmptyText.Text;
        public bool ModeStripVisibleForTest => ModeStrip.Visibility == Visibility.Visible;
        public string ModeLabelsForTest => ModeMine.Content + "|" + ModeLodge.Content;
        public bool OwnListVisibleForTest => CharList.Visibility == Visibility.Visible || AllCard.Visibility == Visibility.Visible;
        public bool ArmoryListVisibleForTest => ArmoryLeft.Visibility == Visibility.Visible && Body.Visibility == Visibility.Visible;
        // "Name|second line|selected"
        public List<string> ArmoryRowsForTest() => ((ArmoryList.ItemsSource as List<ArmoryRow>) ?? new List<ArmoryRow>()).Select(r => r.Name + "|" + r.Line2 + "|" + r.Selected).ToList();
        public string ArmoryCountForTest => ArmoryCount.Text;
        public List<string> ArmoryOptionsForTest(string which) => ((which == "class" ? ArmoryClass : which == "level" ? ArmoryLevel : ArmoryProf).ItemsSource as List<string>) ?? new List<string>();
        public void ArmoryFilterForTest(string which, string value)
        {
            var box = which == "class" ? ArmoryClass : which == "level" ? ArmoryLevel : ArmoryProf;
            var list = box.ItemsSource as List<string> ?? new List<string>();
            var i = value == null ? 0 : list.IndexOf(value);
            box.SelectedIndex = Math.Max(0, i);
        }
        public void ArmorySearchForTest(string text) { SearchBox.Text = text; if (armoryMode) RefreshArmory(false); }
        public void ArmoryOpenForTest(string id) { armoryKey = id; RefreshArmory(false); }
        public string ArmoryOpenKeyForTest => armoryKey;
        public string ArmoryTitleForTest => TitleText.Text;
        public bool TabVisibleForTest(string which) => (which == "bags" ? TabBags : which == "bank" ? TabBank : which == "guild" ? TabGuild : which == "gear" ? TabGear : which == "talents" ? TabTalents
            : which == "professions" ? TabProfs : which == "pet" ? TabPet : TabOverview).Visibility == Visibility.Visible;
        public bool SearchBoxVisibleForTest => SearchBox.Visibility == Visibility.Visible;
        public string SearchHintForTest => SearchBox.Tag as string;
        public void ArmoryTabForTest(string which) { SetTabChecked(which); RefreshArmory(false); }
        public void ArmoryRefreshForTest() => RefreshArmory(false);
        // a Ctrl+click on the worn item in a doll slot (the real handler of the tile); true when the Atlas was asked to open something
        public bool AtlasClickForTest(int slot)
        {
            if (!dollSlots.TryGetValue(slot, out var el)) return false;
            bool opened = false;
            // only the test hears the event: the real window must not follow it to the Atlas page
            var real = OpenAtlas;
            OpenAtlas = id => opened = true; forceCtrl = true;
            try { el.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent, Source = el }); }
            finally { forceCtrl = false; OpenAtlas = real; }
            return opened;
        }
    }
}
