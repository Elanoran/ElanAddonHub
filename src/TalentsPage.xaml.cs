using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Talents;

namespace ElansAddonHub
{
    // The Talents page (Atlas layer G): a calculator for the talent trees of every class, read from the player's own game files (TalentData), with the
    // game's own rules (TalentRules), recommended builds (Hunter Helper's list, read from the installed addon), saved builds and shareable build codes.
    public partial class TalentsPage : UserControl
    {
        // hooks (the self-test replaces them)
        public static Func<TalentNode, string> NameOf = n => TalentData.NodeName(n);
        public Func<InvData> CharactersSource;                 // null = read the saved variables
        public Func<string> HunterBuildsFile = null;           // null = <WoW folder>\<flavor>\Interface\AddOns\ElansHunterHelper\Data\Builds.lua
        public string SavedFile = SavedBuilds.File;

        public event Action<string> ClassChanged;              // the class file now shown: picked, imported with a build, or restored (the Atlas keeps its id in step)

        Func<string> wowRoot = () => null;
        TalentSet set;
        TalentTreeDef tree;
        TalentSession session;
        string classFile;                                      // the chosen class
        string lastClassFile;
        readonly Dictionary<int, TalentNodeView> views = new Dictionary<int, TalentNodeView>();
        readonly List<TalentTreePanel> panels = new List<TalentTreePanel>();
        SavedBuilds saved = new SavedBuilds();
        List<RecBuild> recBuilds = new List<RecBuild>();
        string recProblem;
        object openBuild;                                      // the RecBuild / SavedBuild that is open
        BuildApplication openApplication;
        bool building, loadedOnce;
        readonly List<string> info = new List<string>();       // self-test breadcrumbs

        public TalentsPage()
        {
            InitializeComponent();
            HintLine.Text = Hint;
            for (int l = 10; l <= 60; l++) LevelBox.Items.Add(l);
            for (int r = 0; r <= 5; r++) LegacyBox.Items.Add(r);
            LevelBox.SelectedItem = 60; LegacyBox.SelectedItem = 0;
            TalentData.Changed += OnDataChanged;
            SpellData.Changed += OnDataChanged;
            // new talent trees while a class is on screen (a patch: the previous build's trees were shown meanwhile): offered, not swapped under the planner
            if (Content is UIElement page) { Content = null; var g = new Grid(); g.Children.Add(page); g.Children.Add(updateNotice); Content = g; }
            updateNotice.ShowClicked += () => { applyNew = true; Refresh(); applyNew = false; DataUpdateNotice.FadeIn(CalcCard); };
            GameDataProgress.Changed += () => { try { Dispatcher.BeginInvoke(new Action(() => { if (EmptyBox.Visibility == Visibility.Visible && set == null && (TalentData.Status == TalentData.State.Loading || TalentData.Status == TalentData.State.NotStarted)) WaitingText(); }), System.Windows.Threading.DispatcherPriority.Background); } catch { } };
            PreviewKeyDown += (s, e) => { if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control && !(Keyboard.FocusedElement is TextBox)) { Undo_Click(null, null); e.Handled = true; } };
        }

        public void Init(Func<string> root)
        {
            wowRoot = root ?? (() => null);
            TalentData.Hook();
        }

        // The page lives inside the Atlas (hosted category "talents"): no title and no panel of its own, the Atlas draws both.
        bool embedded;
        public void Embed()
        {
            embedded = true;
            Panel.Background = null; Panel.Margin = new Thickness(0); Panel.Padding = new Thickness(0);
            TitleBlock.Visibility = Visibility.Collapsed;
        }

        public string ClassFileNow => classFile;

        // asks for a class: now when the trees are read, as soon as they are otherwise. The class on screen is left alone (the points spent stay) when it is the one asked for.
        public void ShowClass(string file)
        {
            if (string.IsNullOrEmpty(file)) return;
            if (set == null) { classFile = file; return; }
            if (!string.Equals(classFile, file, StringComparison.OrdinalIgnoreCase)) SelectClass(file);
        }

        // the page was opened
        public void Shown()
        {
            if (!loadedOnce) { saved = SavedBuilds.Load(SavedFile); loadedOnce = true; }
            if (TalentData.Status == TalentData.State.NotStarted || TalentData.Status == TalentData.State.Disabled) TalentData.Start();
            applyNew = true; Refresh(); applyNew = false;     // opening the page shows the newest trees (no offer needed)
            if (!embedded) Motion.PageIn(this);
        }

        void OnDataChanged() { try { Dispatcher.BeginInvoke(new Action(() => { if (IsLoaded || set != null) Refresh(); })); } catch { } }

        // ------------------------------------------------------------------------------------------------------------------------------ state of the data
        void Refresh()
        {
            var st = TalentData.Status;
            var d = TalentData.Data;
            if (st == TalentData.State.Ready && d != null && d.Trees.Count > 0)
            {
                bool newSet = !ReferenceEquals(d, set);
                if (newSet && set != null && tree != null && IsVisible && !applyNew) { updateNotice.Offer(); info.Add("update offered"); return; }
                updateNotice.Hide();
                set = d;
                EmptyBox.Visibility = Visibility.Collapsed; CalcCard.Visibility = Visibility.Visible; BuildsCard.Visibility = Visibility.Visible; ClassBar.Visibility = Visibility.Visible;
                if (newSet || ClassBar.Children.Count == 0) BuildClassBar();
                if (tree == null || newSet)
                {
                    var want = classFile ?? lastClassFile ?? (set.ForClassFile("HUNTER") != null ? "HUNTER" : set.Trees[0].ClassFile);
                    SelectClass(want);
                }
                else RefreshStates();
                return;
            }
            set = null; tree = null; session = null;
            CalcCard.Visibility = Visibility.Collapsed; BuildsCard.Visibility = Visibility.Collapsed; ClassBar.Visibility = Visibility.Collapsed;
            EmptyBox.Visibility = Visibility.Visible;
            switch (st)
            {
                case TalentData.State.Disabled:
                    EmptyTitle.Text = "Game data is turned off";
                    EmptyText.Text = "Turn on \"Read item data from my WoW installation\" in Settings > General to plan talents from the trees in your game."; break;
                case TalentData.State.Unavailable:
                    EmptyTitle.Text = "The talent trees could not be read";
                    EmptyText.Text = (TalentData.Reason ?? "").Length == 0 ? "Check the WoW folder in Settings." : TalentData.Reason + (TalentData.Reason.Contains("game updated") ? ". Update Elan's Outpost for this game version." : ""); break;
                default: WaitingText(); break;
            }
            info.Add("empty|" + st);
        }

        readonly DataUpdateNotice updateNotice = new DataUpdateNotice();
        bool applyNew;
        public bool UpdateOfferedForTest => updateNotice.ShownForTest;

        // the first read of the trees: what is being done right now (GameDataProgress), and why
        void WaitingText()
        {
            var v = GameDataProgress.Now();
            var j = v.Find(TalentData.JobId);
            EmptyTitle.Text = j != null && j.State == GameDataProgress.JobState.Running ? GameDataProgress.StepText(j) + "..." : "Reading the talent trees from your game...";
            var why = GameDataProgress.ReasonText(v.Reason);
            EmptyText.Text = (why.Length > 0 ? why + ". " : "") + "This takes a moment the first time and is kept for the next start.";
        }

        public static Brush ClassBrush(string classFile) => InventoryPage.ClassBrushOf(classFile);      // the one table of class colours (identity colours)

        static Brush Res(string key) => (Brush)Application.Current.Resources[key];

        // ------------------------------------------------------------------------------------------------------------------------------ class picker
        void BuildClassBar()
        {
            ClassBar.Children.Clear();
            foreach (var t in set.Trees)
            {
                var rb = new RadioButton { Style = (Style)FindResource("ClassChip"), Tag = t.ClassFile, IsChecked = string.Equals(t.ClassFile, classFile, StringComparison.OrdinalIgnoreCase) };
                System.Windows.Automation.AutomationProperties.SetName(rb, t.ClassName);
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                var img = new Image { Width = 24, Height = 24, Margin = new Thickness(0, 0, 8, 0) };
                RenderOptions.SetBitmapScalingMode(img, System.Windows.Media.BitmapScalingMode.HighQuality);
                int icon = CharPortrait.ClassIcon(t.ClassFile);
                if (icon > 0) BindIcon(icon, img);
                sp.Children.Add(img);
                sp.Children.Add(new TextBlock { Text = t.ClassName, Style = (Style)FindResource("Text.RowTitle"), Foreground = ClassBrush(t.ClassFile), VerticalAlignment = VerticalAlignment.Center });
                rb.Content = sp;
                rb.Click += (s, e) => SelectClass((string)((RadioButton)s).Tag);
                ClassBar.Children.Add(rb);
            }
        }

        public void SelectClass(string file)
        {
            if (set == null) return;
            var t = set.ForClassFile(file) ?? set.Trees.FirstOrDefault();
            if (t == null) return;
            tree = t; classFile = t.ClassFile; lastClassFile = classFile;
            foreach (RadioButton rb in ClassBar.Children.OfType<RadioButton>()) rb.IsChecked = string.Equals((string)rb.Tag, classFile, StringComparison.OrdinalIgnoreCase);
            session = new TalentSession(tree);
            building = true;
            LevelBox.SelectedItem = 60; LegacyBox.SelectedItem = 0;
            building = false;
            LegacyPanel.Visibility = TalentRules.MaxLegacyRanks(tree) > 0 ? Visibility.Visible : Visibility.Collapsed;
            session.Level = 60; session.LegacyRanks = 0;
            openBuild = null; openApplication = null; DetailBox.Visibility = Visibility.Collapsed;
            ClassTitle.Foreground = ClassBrush(classFile);
            ClassTitle.Text = tree.ClassName;
            SaveName.Text = ""; SaveNotes.Text = ""; CodeBox.Text = "";
            SetStatus("", false);
            BuildTree();
            LoadBuildLists();
            PageScroll.ScrollToTop();
            ClassChanged?.Invoke(classFile);
        }

        // ------------------------------------------------------------------------------------------------------------------------------ the trees
        // one TalentTreePanel per tree (the shared renderer): the game's grid, the arrows, the row gates in a narrow gutter, the spec art behind it
        void BuildTree()
        {
            views.Clear(); panels.Clear();
            var models = TalentTreeModels.FromDef(tree, session.State, NameOf, IconOf, n => TalentRules.Available(tree, session.State, n));
            var cards = new List<FrameworkElement>();
            foreach (var m in models)
            {
                var p = new TalentTreePanel(m, true, true, "Brush.Surface2", id => TipFor(id));
                p.NodeClicked += (id, right) => Click(id, right);
                p.NodeCtrlClicked += OpenSpell;
                foreach (var kv in p.Views) views[kv.Key] = kv.Value;
                panels.Add(p); cards.Add(p);
                if (p.Painted) info.Add("treeart|" + m.Spec + "|" + m.Art.File);
            }
            TreeHost.Content = TalentTreePanel.Columns(cards, TreeHost, () => CalcCard.ActualWidth - 32);
            RefreshStates();
        }

        static int IconOf(TalentNode n) => n.Entry?.IconOverride > 0 ? n.Entry.IconOverride : (n.SpellId > 0 ? SpellData.Spell(n.SpellId)?.IconId ?? 0 : 0);

        void BindIcon(int id, Image img)
        {
            if (id <= 0) return;
            var have = IconStore.TryGet(id);
            if (have != null) { img.Source = have; return; }
            IconStore.Request(id, src => { try { img.Dispatcher.BeginInvoke(new Action(() => img.Source = src)); } catch { } });
        }

        // ranks, icons, arrows, gates and the points: everything that follows from the current ranks
        void RefreshStates()
        {
            if (tree == null || session == null) return;
            var st = session.State;
            foreach (var p in panels)
            {
                foreach (var c in p.Model.Cells) { var n = tree.Node(c.Id); if (n != null) { c.Name = NameOf(n) ?? ""; c.IconId = IconOf(n); } }
                TalentTreeModels.Apply(p.Model, tree, st);
                p.Update();
            }
            UpdatePointsLine();
        }

        void UpdatePointsLine()
        {
            if (session == null) return;
            var spec = TalentRules.SpentPerSpec(tree, session.State);
            int spent = session.Spent;
            var sb = new System.Text.StringBuilder();
            sb.Append(spent).Append(" of ").Append(session.Budget).Append(" points spent");
            if (spec.Length > 1 && spent > 0) sb.Append(" (").Append(string.Join("/", spec)).Append(")");
            sb.Append(" · ").Append(session.Left).Append(" left");
            if (spent > 0) { int lv = session.RequiredLevel; sb.Append(" · needs level ").Append(lv == int.MaxValue ? "?" : lv.ToString(CultureInfo.InvariantCulture)); }
            PointsLine.Text = sb.ToString();
            UndoButton.IsEnabled = session.CanUndo;
            ResetButton.IsEnabled = session.State.Total > 0;
            CopyButton.IsEnabled = session.State.Total > 0;
            SaveButton.IsEnabled = session.State.Total > 0;
        }

        // ------------------------------------------------------------------------------------------------------------------------------ clicks
        // click +1, right-click -1 (TalentTreePanel.NodeClicked); Ctrl+click: the talent's spell page in the Atlas, no point spent (NodeCtrlClicked)
        // the talent's spell page (spell:<id>); the Atlas page opens it in place, with history
        public void OpenSpell(int nodeId)
        {
            int id = tree?.Node(nodeId)?.SpellId ?? 0;
            if (id > 0) ElansAddonHub.Services.Professions.ProfessionLinks.Open(AtlasIds.Spell(id));
        }

        public Why Click(int nodeId, bool right)
        {
            if (session == null) return Why.NoNode;
            var w = right ? session.Remove(nodeId) : session.Add(nodeId);
            if (w == Why.Ok) { SetStatus("", false); openBuild = null; DetailBox.Visibility = Visibility.Collapsed; }
            else { var n = tree.Node(nodeId); SetStatus(TalentRules.Explain(w) + (w == Why.Blocked && n != null ? ": " + WhyBlocked(n) : ""), false); }
            RefreshStates();
            return w;
        }

        string WhyBlocked(TalentNode n)
        {
            var parts = new List<string>();
            int miss = TalentRules.GateMissing(tree, session.State, n);
            if (miss > 0) parts.Add(miss + " more point" + (miss == 1 ? "" : "s") + " needed in the rows above");
            var pre = tree.Incoming(n.Id).Where(e => e.Type == TalentEdge.Required || e.Type == TalentEdge.Sufficient).ToList();
            if (pre.Count > 0)
            {
                var open = pre.Any(e => e.Type == TalentEdge.Sufficient) && !pre.Where(e => e.Type == TalentEdge.Sufficient).Any(e => { var f = tree.Node(e.From); return f != null && session.State.Rank(f.Id) >= f.MaxRanks; });
                var req = pre.Where(e => e.Type == TalentEdge.Required && tree.Node(e.From) != null && session.State.Rank(e.From) < tree.Node(e.From).MaxRanks);
                if (open || req.Any())
                    parts.Add("needs " + string.Join(" or ", pre.Select(e => NameOf(tree.Node(e.From)) ?? "a talent").Distinct()) + " at full rank");
            }
            return parts.Count == 0 ? "locked" : string.Join("; ", parts);
        }

        // error = Danger (a real error: a bad code); warn = Warn (it worked, but something was left out); neither = plain
        void SetStatus(string text, bool error, bool warn = false)
        {
            StatusLine.Text = text ?? "";
            StatusLine.Foreground = Res(error ? "Brush.Danger" : warn ? "Brush.Warn" : "Brush.Text.Secondary");
            StatusLine.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        void Level_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (building || session == null || !(LevelBox.SelectedItem is int lv)) return;
            session.Level = lv; RefreshStates();
        }

        void Legacy_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (building || session == null || !(LegacyBox.SelectedItem is int r)) return;
            session.LegacyRanks = r; RefreshStates();
            if (openBuild is RecBuild rb) ShowDetail(rb, false);
        }

        void Undo_Click(object sender, RoutedEventArgs e) { if (session != null && session.Undo()) { SetStatus("", false); RefreshStates(); } }
        void Reset_Click(object sender, RoutedEventArgs e) { if (session == null) return; session.Reset(); openBuild = null; DetailBox.Visibility = Visibility.Collapsed; SetStatus("", false); RefreshStates(); }

        // the level box follows what a loaded layout needs (never below it)
        void FitLevel()
        {
            building = true;
            int need = session.RequiredLevel;
            if (need != int.MaxValue && need > session.Level) session.Level = Math.Min(60, Math.Max(10, need));
            LevelBox.SelectedItem = session.Level;
            LegacyBox.SelectedItem = session.LegacyRanks;
            building = false;
        }

        // ------------------------------------------------------------------------------------------------------------------------------ tooltips
        TextBlock Line(string text, string style, Brush fg = null, double top = 0)
        {
            var t = new TextBlock { Text = text, Style = (Style)FindResource(style), TextWrapping = TextWrapping.Wrap, MaxWidth = 300, HorizontalAlignment = HorizontalAlignment.Left };
            if (fg != null) t.Foreground = fg;
            if (top > 0) t.Margin = new Thickness(0, top, 0, 0);
            return t;
        }

        static string DescOf(SpellView v) => v == null ? null : !string.IsNullOrWhiteSpace(v.Description) ? v.Description.Trim() : !string.IsNullOrWhiteSpace(v.AuraDescription) ? v.AuraDescription.Trim() : null;

        GameTooltip TipFor(int nodeId)
        {
            var n = tree?.Node(nodeId);
            if (n == null) return new GameTooltip("");
            int rank = session.State.Rank(nodeId);
            var e = n.Entry;
            var gt = new GameTooltip(NameOf(n) ?? ("Talent " + n.Id), 1);
            gt.AddText("Rank " + rank + " of " + n.MaxRanks);
            info.Add("tip|" + nodeId);
            if (e != null && e.SpellId > 0)
            {
                string cur = rank > 0 ? DescOf(TalentData.SpellAtRank(e, rank)) : null;
                string next = rank < n.MaxRanks ? DescOf(TalentData.SpellAtRank(e, rank + 1)) : null;
                if (!string.IsNullOrEmpty(e.DescriptionOverride)) { cur = rank > 0 ? e.DescriptionOverride : null; next = rank < n.MaxRanks ? e.DescriptionOverride : null; }
                if (rank > 0 && cur != null) gt.AddText(cur, "spelldesc");
                if (rank < n.MaxRanks && next != null) gt.AddText((rank > 0 ? "Next rank: " : "Rank 1: ") + next, rank > 0 ? "desc" : "spelldesc");
            }
            if (rank < n.MaxRanks && !TalentRules.Available(tree, session.State, n)) gt.AddText("Locked: " + WhyBlocked(n), "bad");
            var foot = new List<string>();
            if (rank >= n.MaxRanks) foot.Add("Full rank. Right-click to give a point back.");
            else if (session.Left < tree.CostPerRank) foot.Add("No points left at this level.");
            else if (TalentRules.Available(tree, session.State, n)) foot.Add("Click to spend a point.");
            if (e != null && e.SpellId > 0) foot.Add("Spell ID " + e.SpellId + "  ·  Ctrl+click opens it in the Atlas");
            for (int i = 0; i < foot.Count; i++) gt.Footer.Children.Add(GameTooltip.Muted(foot[i], i == 0 ? 0 : 4));
            return gt;
        }

        // ------------------------------------------------------------------------------------------------------------------------------ the hint under the trees
        // Says exactly what TalentTreePanel does with a talent (the self-test presses each of these and compares): left click / Enter / Space / + add a
        // point, right click / Delete / Backspace / - take one back, Ctrl+click opens the talent's spell page. No Shift shortcut exists, so none is named.
        public const string Hint = "Click a talent to add a point, right-click to take one back, Ctrl+click to open its spell page. "
            + "Keyboard: Enter or + adds a point, Delete or - takes one back. A row opens once enough points are spent in the rows above it.";

        // ------------------------------------------------------------------------------------------------------------------------------ online calculator
        // Seemeta's WoW Forever calculator with this class and build (BuildLinks.SeemetaUrl, the format the page reads), in the Outpost browser.
        // Only the class and the ranks go into the address: nothing about the person or their characters.
        public static Action<string> OpenWebLink;
        public string SeemetaUrlNow => tree == null || session == null ? null : BuildLinks.SeemetaUrl(tree, session.State);

        void Seemeta_Click(object sender, RoutedEventArgs e)
        {
            var url = SeemetaUrlNow;
            if (url == null) return;
            info.Add("seemeta|" + url);
            if (OpenWebLink != null) OpenWebLink(url); else WebEngine.OpenExternal(url);
        }

        // ------------------------------------------------------------------------------------------------------------------------------ build code
        public string ExportCode() => session == null ? null : BuildCode.Encode(tree, session.State);

        void Copy_Click(object sender, RoutedEventArgs e)
        {
            var code = ExportCode();
            if (code == null) return;
            try { Clipboard.SetText(code); SetStatus("Build code copied. Paste it into the Lodge chat or send it to a friend.", false); }
            catch (Exception) { CodeBox.Text = code; SetStatus("Could not reach the clipboard: the code is in the box below, copy it from there.", true); }
            info.Add("copy|" + code);
        }

        void Code_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { Import_Click(null, null); e.Handled = true; } }

        void Import_Click(object sender, RoutedEventArgs e) => Import(CodeBox.Text);

        public bool Import(string text)
        {
            if (set == null) { SetStatus("The talent trees are not loaded yet.", true); return false; }
            var d = BuildCode.Decode(text, id => set.ForClass(id));
            if (d.Ok)
            {
                if (!string.Equals(d.Tree.ClassFile, classFile, StringComparison.OrdinalIgnoreCase)) SelectClass(d.Tree.ClassFile);
                session.Load(d.State); FitLevel(); openBuild = null; DetailBox.Visibility = Visibility.Collapsed;
                RefreshStates();
                SetStatus("Loaded a " + tree.ClassName + " build with " + d.State.Total + " points.", false);
                return true;
            }
            // not one of our codes: a link from a talent calculator, or the words of the code's own error when it was meant as a code
            BuildCode.Find(text ?? "", out int codeVersion);
            if (BuildCode.Find(text ?? "", out _) != null || codeVersion > 0 || d.TooNew) { SetStatus(d.Error, true); return false; }
            var l = BuildLinks.Decode(text, set, NameOf, session?.LegacyRanks ?? 0);
            if (!l.Ok) { SetStatus(l.Error, true); return false; }
            if (!string.Equals(l.Tree.ClassFile, classFile, StringComparison.OrdinalIgnoreCase)) SelectClass(l.Tree.ClassFile);
            session.Load(l.Order != null ? l.Order.State : l.State); FitLevel();
            var lb = new LinkBuild { Site = l.Site, ClassFile = l.Tree.ClassFile, Name = tree.ClassName + " build from " + l.Site, Order = l.Order, OrderNote = l.OrderNote };
            openBuild = lb; openApplication = l.Order;
            RefreshStates();
            ShowDetail(lb, true);
            // offered to keep: the save boxes are filled in, one click on "Save build" keeps it (the site is named in the notes)
            SaveName.Text = SavedBuilds.CleanName(lb.Name); SaveNotes.Text = "From a " + l.Site + " link.";
            SetStatus("Loaded a " + tree.ClassName + " build with " + session.State.Total + " points from a " + l.Site + " link." + (l.Order != null ? " Its leveling order is below." : "") + " Press Save build to keep it.", false, l.OrderNote != null);
            return true;
        }

        // ------------------------------------------------------------------------------------------------------------------------------ my characters
        async void Char_Click(object sender, RoutedEventArgs e)
        {
            if (tree == null) return;
            CharList.Children.Clear();
            var cls = tree.ClassFile;
            List<InvChar> mine;
            try
            {
                var root = wowRoot();
                var data = await Task.Run(() => CharactersSource != null ? CharactersSource() : CharacterMerge.Merge(HubData.Read(root), InventoryReader.ReadData(root)));
                mine = (data?.Chars ?? new List<InvChar>()).Where(c => string.Equals(c.Class, cls, StringComparison.OrdinalIgnoreCase) && c.Hub != null && HubModules.TalentsOf(c.Hub) != null).ToList();
            }
            catch (Exception) { mine = new List<InvChar>(); }
            if (mine.Count == 0)
            {
                CharList.Children.Add(Line("No " + tree.ClassName + " with saved talents yet. Log in with Elan's Hub and /reload.", "Text.Secondary"));
            }
            foreach (var c in mine)
            {
                var ch = c;
                var b = new Button { Style = (Style)FindResource("Button.M"), HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 4), Tag = ch.Key };
                var sp = new StackPanel();
                sp.Children.Add(new TextBlock { Text = ch.Name, Style = (Style)FindResource("Text.RowTitle"), Foreground = ClassBrush(ch.Class) });
                b.Content = sp;
                b.ToolTip = (ch.Level > 0 ? "Level " + ch.Level + " · " : "") + ch.Realm;
                b.Click += (s, a) => { CharPopup.IsOpen = false; UseCharacter(ch); };
                CharList.Children.Add(b);
            }
            CharPopup.IsOpen = true;
            info.Add("charmenu|" + mine.Count);
        }

        public bool UseCharacter(InvChar c)
        {
            if (tree == null || c == null) return false;
            var mod = HubModules.TalentsOf(c.Hub);
            if (mod == null) { SetStatus(c.Name + " has no saved talents yet.", true); return false; }
            var loaded = CharacterTalents.Load(tree, mod, c.Level, NameOf);
            session.LegacyRanks = loaded.LegacyRanks;
            if (c.Level > 0) session.Level = Math.Max(10, Math.Min(60, c.Level));
            session.Load(loaded.State); FitLevel(); openBuild = null; DetailBox.Visibility = Visibility.Collapsed;
            RefreshStates();
            var msg = c.Name + ": " + loaded.Placed + " points loaded" + (mod.Unspent > 0 ? ", " + mod.Unspent + " unspent" : "") + ".";
            if (loaded.Unplaced > 0) msg += " " + loaded.Unplaced + " point" + (loaded.Unplaced == 1 ? "" : "s") + " could not be placed in this tree.";
            if (!loaded.Valid) msg += " The layout does not follow the tree's rules (a respec in progress?).";
            SetStatus(msg, false, !loaded.Valid || loaded.Unplaced > 0);
            return true;
        }

        // ------------------------------------------------------------------------------------------------------------------------------ builds
        void LoadBuildLists()
        {
            recBuilds = new List<RecBuild>(); recProblem = null;
            if (string.Equals(classFile, "HUNTER", StringComparison.OrdinalIgnoreCase))
            {
                var path = HunterBuildsFile != null ? HunterBuildsFile() : BuildsRecommended.HunterHelperFile(wowRoot(), IconStore.Flavor);
                recBuilds = BuildsRecommended.ReadHunterHelper(path, out recProblem);
            }
            RenderBuilds();
        }

        void BuildsTab_Checked(object sender, RoutedEventArgs e) { if (BuildList != null && tree != null) RenderBuilds(); }

        bool ShowingSaved => SegSaved.IsChecked == true;

        void RenderBuilds()
        {
            BuildList.Children.Clear();
            BuildsNote.Text = "";
            if (!ShowingSaved)
            {
                if (recBuilds.Count == 0)
                {
                    BuildList.Children.Add(Line("No recommended builds for this class yet.", "Text.Body", Res("Brush.Text.Primary")));
                    BuildsNote.Text = string.Equals(classFile, "HUNTER", StringComparison.OrdinalIgnoreCase) && recProblem != null
                        ? recProblem + " Install Elan's Hunter Helper to get its builds here."
                        : "Build and save your own below, or open a build code a friend sent you. Saved builds and codes work for every class.";
                }
                else
                {
                    foreach (var b in recBuilds) BuildList.Children.Add(BuildRow(b, b.Name, string.Join(" · ", b.Roles) + (b.VerifiedTo > 0 ? " · checked to level " + b.VerifiedTo : ""), b.Points + " points"));
                    BuildsNote.Text = "From your installed Elan's Hunter Helper. Click a build to put it in the calculator.";
                }
            }
            else
            {
                var mine = saved.For(classFile);
                if (mine.Count == 0) BuildList.Children.Add(Line("Nothing saved for " + tree.ClassName + " yet. Spend some points and press Save build.", "Text.Body", Res("Brush.Text.Primary")));
                foreach (var b in mine) BuildList.Children.Add(BuildRow(b, b.Name, string.IsNullOrEmpty(b.Notes) ? "Saved " + DateTimeOffset.FromUnixTimeSeconds(b.At).LocalDateTime.ToString("d MMM yyyy", CultureInfo.CurrentCulture) : b.Notes, null));
            }
            info.Add("builds|" + (ShowingSaved ? "saved" : "rec") + "|" + BuildList.Children.Count);
        }

        FrameworkElement BuildRow(object build, string title, string sub, string right)
        {
            var b = new Button { Style = (Style)FindResource("BuildRow"), Tag = build };
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("Text.RowTitle"), TextTrimming = TextTrimming.CharacterEllipsis });
            sp.Children.Add(new TextBlock { Text = sub, Style = (Style)FindResource("Text.Caption"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) });
            g.Children.Add(sp);
            if (right != null)
            {
                var r = new TextBlock { Text = right, Style = (Style)FindResource("Text.Caption"), VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(r, 1); g.Children.Add(r);
            }
            if (build is SavedBuild sb)
            {
                var del = new Button { Style = (Style)FindResource("IconButton"), Content = new TextBlock { Text = "", FontFamily = (FontFamily)Application.Current.Resources["Font.Icons"], FontSize = 12, Foreground = Res("Brush.Danger") }, ToolTip = "Delete this saved build", Width = 28, Height = 28, VerticalAlignment = VerticalAlignment.Center };
                del.Click += (s, e) => { e.Handled = true; DeleteSaved(sb); };
                Grid.SetColumn(del, 1); g.Children.Add(del);
            }
            b.Content = g;
            b.Click += (s, e) => OpenBuild(build);
            return b;
        }

        void DeleteSaved(SavedBuild b) { saved.Delete(b); saved.TrySave(SavedFile); if (ReferenceEquals(openBuild, b)) { openBuild = null; DetailBox.Visibility = Visibility.Collapsed; } RenderBuilds(); SetStatus("Deleted " + b.Name + ".", false); }

        public void OpenBuild(object build)
        {
            if (tree == null) return;
            if (build is RecBuild rb)
            {
                var app = BuildsRecommended.Apply(tree, rb, NameOf, session.LegacyRanks);
                session.Load(app.State); FitLevel();
                openBuild = rb; openApplication = app;
                RefreshStates();
                ShowDetail(rb, true);
                var msg = rb.Name + ": " + app.Points + " points" + (app.Unmatched.Count > 0 ? ". Not in this game: " + string.Join(", ", app.Unmatched) : "") + (app.Blocked > 0 ? ". " + app.Blocked + " point(s) the tree would not allow at that moment were skipped" : "") + ".";
                SetStatus(msg, false, app.Unmatched.Count > 0 || app.Blocked > 0);
            }
            else if (build is SavedBuild sbd)
            {
                var d = BuildCode.Decode(sbd.Code, id => set.ForClass(id));
                if (!d.Ok) { SetStatus(sbd.Name + ": " + d.Error, true); return; }
                session.Load(d.State); FitLevel();
                openBuild = sbd; openApplication = null;
                RefreshStates();
                ShowDetail(sbd, true);
                SetStatus("Loaded " + sbd.Name + " (" + d.State.Total + " points).", false);
            }
        }

        void ShowDetail(object build, bool scroll)
        {
            DetailPanel.Children.Clear();
            if (build is RecBuild rb)
            {
                DetailPanel.Children.Add(Line(rb.Name, "Text.Section"));
                if (!string.IsNullOrEmpty(rb.Desc)) DetailPanel.Children.Add(Line(rb.Desc, "Text.Body", Res("Brush.Text.Secondary"), 4));
                if (!string.IsNullOrEmpty(rb.Stats)) DetailPanel.Children.Add(Line("Stats: " + rb.Stats, "Text.Secondary", null, 8));
                if (rb.Pets.Count > 0) DetailPanel.Children.Add(Line("Pets: " + string.Join(", ", rb.Pets), "Text.Secondary", null, 4));
                if (!string.IsNullOrEmpty(rb.Tip)) DetailPanel.Children.Add(Line("Tip: " + rb.Tip, "Text.Secondary", null, 4));
                var app = BuildsRecommended.Apply(tree, rb, NameOf, session.LegacyRanks);
                if (app.Unmatched.Count > 0) DetailPanel.Children.Add(Line("Not in this game's tree: " + string.Join(", ", app.Unmatched), "Text.Secondary", Res("Brush.Warn"), 8));
                DetailPanel.Children.Add(Line("Leveling order (one point per level)", "Text.Caption", null, 12));
                foreach (var l in BuildsRecommended.StepLines(app)) DetailPanel.Children.Add(Line(l, "Text.Secondary", null, 2));
                DetailPanel.Children.Add(Line("Source: " + rb.Source + ". Tree data credit as in that addon (foreverwisp.com / talentsforever.com, CC BY 4.0).", "Text.Caption", null, 12));
                openApplication = app;
            }
            else if (build is SavedBuild sb)
            {
                DetailPanel.Children.Add(Line(sb.Name, "Text.Section"));
                if (!string.IsNullOrEmpty(sb.Notes)) DetailPanel.Children.Add(Line(sb.Notes, "Text.Body", Res("Brush.Text.Secondary"), 4));
                var code = new TextBox { Text = sb.Code, IsReadOnly = true, Style = (Style)FindResource("Input"), Margin = new Thickness(0, 8, 0, 0) };
                DetailPanel.Children.Add(code);
            }
            else if (build is LinkBuild lk)
            {
                DetailPanel.Children.Add(Line(lk.Name, "Text.Section"));
                DetailPanel.Children.Add(Line("Opened from a " + lk.Site + " link. Press Save build below to keep it.", "Text.Secondary", null, 4));
                if (lk.Order != null)
                {
                    DetailPanel.Children.Add(Line("Leveling order (one point per level)", "Text.Caption", null, 12));
                    foreach (var l in BuildsRecommended.StepLines(lk.Order)) DetailPanel.Children.Add(Line(l, "Text.Secondary", null, 2));
                }
                else DetailPanel.Children.Add(Line(lk.OrderNote ?? "This link holds the final talents only, not the order the points were spent in.", "Text.Secondary", lk.OrderNote != null ? Res("Brush.Warn") : null, 8));
            }
            DetailBox.Visibility = Visibility.Visible;
            info.Add("detail|" + (build is RecBuild r2 ? r2.Name : build is LinkBuild lb2 ? lb2.Name : (build as SavedBuild)?.Name));
        }

        void Save_Click(object sender, RoutedEventArgs e)
        {
            if (session == null) return;
            var code = ExportCode();
            var name = SavedBuilds.CleanName(SaveName.Text);
            if (name.Length == 0) { SetStatus("Give the build a name first.", true); SaveName.Focus(); return; }
            if (session.State.Total == 0) { SetStatus("Spend some points first.", true); return; }
            var b = saved.Save(name, SaveNotes.Text, code, classFile);
            if (b == null) return;
            saved.TrySave(SavedFile);
            SaveName.Text = ""; SaveNotes.Text = "";
            SegSaved.IsChecked = true; RenderBuilds();
            SetStatus("Saved " + name + ".", false);
        }

        // ------------------------------------------------------------------------------------------------------------------------------ self-test hooks
        public TalentSession SessionForTest => session;
        public TalentTreeDef TreeForTest => tree;
        public string HintForTest => HintLine.Text;
        public void SeemetaClickForTest() => Seemeta_Click(null, null);
        public bool SummaryGoneForTest => FindName("Summary") == null && !AllText(this).Any(t => t.StartsWith("Plan a build", StringComparison.OrdinalIgnoreCase));
        static IEnumerable<string> AllText(DependencyObject d)
        {
            if (d is TextBlock tb && tb.Visibility == Visibility.Visible) yield return tb.Text ?? "";
            foreach (var c in LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>()) foreach (var t in AllText(c)) yield return t;
        }
        public bool TitleHiddenForTest => TitleBlock.Visibility != Visibility.Visible;
        public string StatusForTest => StatusLine.Visibility == Visibility.Visible ? StatusLine.Text : "";
        public bool StatusIsErrorForTest => StatusLine.Visibility == Visibility.Visible && StatusLine.Foreground == Res("Brush.Danger");
        public bool StatusIsWarnForTest => StatusLine.Visibility == Visibility.Visible && StatusLine.Foreground == Res("Brush.Warn");
        public void ScrollTopForTest() { PageScroll.ScrollToTop(); }
        public string PointsLineForTest => PointsLine.Text;
        public string ClassTitleForTest => ClassTitle.Text;
        public bool EmptyVisibleForTest => EmptyBox.Visibility == Visibility.Visible;
        public string EmptyTextForTest => EmptyTitle.Text + " | " + EmptyText.Text;
        public int NodeCountForTest => views.Count;
        public int EdgeCountForTest => panels.Sum(p => p.Model.Arrows.Count);
        public int GateCountForTest => panels.Sum(p => p.GateCount);
        public IReadOnlyList<TalentTreePanel> PanelsForTest => panels;
        public string BuildsNoteForTest => BuildsNote.Text;
        public List<string> InfoForTest => info;
        public List<string> ClassNamesForTest => ClassBar.Children.OfType<RadioButton>().Select(r => (string)r.Tag).ToList();
        public string NodeRankTextForTest(int id) => views.TryGetValue(id, out var v) ? v.Rank.Text : null;
        public double NodeOpacityForTest(int id) => views.TryGetValue(id, out var v) ? v.Box.Opacity : -1;
        public bool NodeHasIconForTest(int id) => views.TryGetValue(id, out var v) && v.Img.Source != null;
        public FrameworkElement NodeBoxForTest(int id) => views.TryGetValue(id, out var v) ? v.Box : null;
        public FrameworkElement TipForTest(int id)
        {
            var frame = TipFor(id);
            frame.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            frame.Arrange(new Rect(frame.DesiredSize));
            return frame;
        }
        public List<string> TipTextsForTest(int id) { var l = new List<string>(); Collect(TipFor(id), l); return l; }
        public List<string> BuildRowsForTest() => BuildList.Children.OfType<Button>().Select(b => (b.Tag as RecBuild)?.Name ?? (b.Tag as SavedBuild)?.Name).ToList();
        public List<string> DetailTextsForTest() { var l = new List<string>(); Collect(DetailPanel, l); return l; }
        public void ShowSavedForTest(bool on) { (on ? SegSaved : SegRec).IsChecked = true; RenderBuilds(); }
        public List<RecBuild> RecBuildsForTest => recBuilds;
        public SavedBuilds SavedForTest => saved;
        public string SaveNameForTest => SaveName.Text;
        public string SaveNotesForTest => SaveNotes.Text;
        public void SaveForTest(string name, string notes) { SaveName.Text = name; SaveNotes.Text = notes; Save_Click(null, null); }
        public void SetLevelForTest(int level) { LevelBox.SelectedItem = level; }
        public void SetLegacyForTest(int r) { LegacyBox.SelectedItem = r; }
        public void ReloadBuildsForTest() { LoadBuildLists(); }
        public void ResetPageForTest() { set = null; tree = null; session = null; classFile = null; lastClassFile = null; loadedOnce = false; saved = new SavedBuilds(); ClassBar.Children.Clear(); }
        public void ForceLoadSavedForTest() { saved = SavedBuilds.Load(SavedFile); loadedOnce = true; }
        public void RefreshForTest() { Refresh(); }
        public async Task<List<string>> CharMenuForTest() { Char_Click(null, null); await Task.Delay(300); return CharList.Children.OfType<Button>().Select(b => (string)b.Tag).ToList(); }
        public void Reset_ClickForTest() { Reset_Click(null, null); }
        public void ForceNoBuildsForTest() { var c = classFile; classFile = "WARRIOR"; recBuilds = new List<RecBuild>(); recProblem = null; SegRec.IsChecked = true; RenderBuilds(); classFile = c; }
        public bool CharPopupOpenForTest => CharPopup.IsOpen;
        public void ClosePopupsForTest() { CharPopup.IsOpen = false; }

        static void Collect(DependencyObject d, List<string> into)
        {
            if (d is TextBlock t && !string.IsNullOrEmpty(t.Text)) into.Add(t.Text);
            if (d is TextBox tb && !string.IsNullOrEmpty(tb.Text)) into.Add(tb.Text);
            int n = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetChildrenCount(d) : 0;
            if (n == 0 && d is Panel p) foreach (var c in p.Children.OfType<DependencyObject>()) Collect(c, into);
            else if (n == 0 && d is ContentControl cc && cc.Content is DependencyObject co) Collect(co, into);
            else for (int i = 0; i < n; i++) Collect(VisualTreeHelper.GetChild(d, i), into);
        }
    }
}
