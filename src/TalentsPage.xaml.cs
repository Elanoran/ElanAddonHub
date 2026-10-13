using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Path = System.Windows.Shapes.Path;
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
        // layout of the trees on the canvas (device independent pixels)
        const double Node = 40, Step = 52, Gutter = 36, SpecGap = 24, HeaderH = 32, Top = HeaderH + 8;

        // hooks (the self-test replaces them)
        public static Func<TalentNode, string> NameOf = n => TalentData.NodeName(n);
        public Func<InvData> CharactersSource;                 // null = read the saved variables
        public Func<string> HunterBuildsFile = null;           // null = <WoW folder>\<flavor>\Interface\AddOns\ElansHunterHelper\Data\Builds.lua
        public string SavedFile = SavedBuilds.File;

        Func<string> wowRoot = () => null;
        TalentSet set;
        TalentTreeDef tree;
        TalentSession session;
        string classFile;                                      // the chosen class
        string lastClassFile;
        readonly Dictionary<int, NodeView> views = new Dictionary<int, NodeView>();
        readonly List<Path> edgePaths = new List<Path>();
        readonly List<TalentEdge> edgeList = new List<TalentEdge>();
        readonly List<TextBlock> specHeads = new List<TextBlock>();
        readonly List<GateView> gates = new List<GateView>();
        SavedBuilds saved = new SavedBuilds();
        List<RecBuild> recBuilds = new List<RecBuild>();
        string recProblem;
        object openBuild;                                      // the RecBuild / SavedBuild that is open
        BuildApplication openApplication;
        bool building, loadedOnce;
        readonly List<string> info = new List<string>();       // self-test breadcrumbs

        sealed class NodeView
        {
            public TalentNode Node;
            public Border Box;
            public Image Img;
            public TextBlock Letter, Rank;
            public Border Badge;
            public int IconId;
        }

        sealed class GateView { public int Spec, Row, Need; public TextBlock Label; public TextBlock Lock; }

        public TalentsPage()
        {
            InitializeComponent();
            for (int l = 10; l <= 60; l++) LevelBox.Items.Add(l);
            for (int r = 0; r <= 5; r++) LegacyBox.Items.Add(r);
            LevelBox.SelectedItem = 60; LegacyBox.SelectedItem = 0;
            TalentData.Changed += OnDataChanged;
            SpellData.Changed += OnDataChanged;
            PreviewKeyDown += (s, e) => { if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control && !(Keyboard.FocusedElement is TextBox)) { Undo_Click(null, null); e.Handled = true; } };
        }

        public void Init(Func<string> root)
        {
            wowRoot = root ?? (() => null);
            TalentData.Hook();
        }

        // the page was opened
        public void Shown()
        {
            if (!loadedOnce) { saved = SavedBuilds.Load(SavedFile); loadedOnce = true; }
            if (TalentData.Status == TalentData.State.NotStarted || TalentData.Status == TalentData.State.Disabled) TalentData.Start();
            Refresh();
            Motion.PageIn(this);
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
                default:
                    EmptyTitle.Text = "Reading the talent trees from your game...";
                    EmptyText.Text = "This takes a moment the first time and is kept for the next start."; break;
            }
            info.Add("empty|" + st);
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
        }

        // ------------------------------------------------------------------------------------------------------------------------------ the trees
        void BuildTree()
        {
            TreeCanvas.Children.Clear(); views.Clear(); edgePaths.Clear(); edgeList.Clear(); specHeads.Clear(); gates.Clear();
            int specs = tree.SpecCount;
            int cols = Math.Max(1, tree.Nodes.Max(n => n.Col) + 1), rows = Math.Max(1, tree.Nodes.Max(n => n.Row) + 1);
            double specW = Gutter + cols * Step;
            TreeCanvas.Width = specs * specW + (specs - 1) * SpecGap;
            TreeCanvas.Height = Top + rows * Step;
            double X(TalentNode n) => n.Spec * (specW + SpecGap) + Gutter + n.Col * Step;
            double Y(TalentNode n) => Top + n.Row * Step;

            for (int s = 0; s < specs; s++)
            {
                double x0 = s * (specW + SpecGap);
                var head = new TextBlock { Text = tree.SpecNames.Length > s ? tree.SpecNames[s] : "Tree " + (s + 1), Style = (Style)FindResource("Text.RowTitle"), Foreground = Res("Brush.Text.Primary") };
                Canvas.SetLeft(head, x0 + Gutter); Canvas.SetTop(head, 4);
                TreeCanvas.Children.Add(head); specHeads.Add(head);
                var rule = new Border { Width = cols * Step - (Step - Node), Height = 1, Background = Res("Brush.Divider") };
                Canvas.SetLeft(rule, x0 + Gutter); Canvas.SetTop(rule, HeaderH);
                TreeCanvas.Children.Add(rule);
                // the gate of each row (what the rows above must hold), in the gutter
                for (int r = 1; r < rows; r++)
                {
                    int need = tree.Nodes.Where(n => n.Spec == s && n.Row == r).Select(n => TalentRules.GateNeeded(tree, n)).DefaultIfEmpty(0).Max();
                    if (need <= 0) continue;
                    var g = new GateView { Spec = s, Row = r, Need = need };
                    g.Lock = new TextBlock { Text = "", FontFamily = (FontFamily)Application.Current.Resources["Font.Icons"], FontSize = 12, Foreground = Res("Brush.Text.Tertiary") };
                    g.Label = new TextBlock { Text = need.ToString(CultureInfo.InvariantCulture), Style = (Style)FindResource("Text.Caption"), Foreground = Res("Brush.Text.Tertiary") };
                    var sp = new StackPanel { Orientation = Orientation.Horizontal, ToolTip = need + " points in the rows above open this row" };
                    sp.Children.Add(g.Lock); sp.Children.Add(new TextBlock { Text = " ", FontSize = 11 }); sp.Children.Add(g.Label);
                    Canvas.SetLeft(sp, x0); Canvas.SetTop(sp, Top + r * Step + (Node - 16) / 2);
                    TreeCanvas.Children.Add(sp);
                    gates.Add(g);
                }
            }

            // arrows first, so the nodes sit on top of them
            foreach (var e in tree.Edges.Where(e => e.Type == TalentEdge.Required || e.Type == TalentEdge.Sufficient || e.Type == TalentEdge.Visual))
            {
                var a = tree.Node(e.From); var b = tree.Node(e.To);
                if (a == null || b == null || a.Spec != b.Spec) continue;
                double ax = X(a) + Node / 2, ay = Y(a) + Node / 2, bx = X(b) + Node / 2, by = Y(b) + Node / 2;
                double dx = bx - ax, dy = by - ay, len = Math.Sqrt(dx * dx + dy * dy);
                if (len < 1) continue;
                double ux = dx / len, uy = dy / len;
                // from the border of one node to the border of the other
                double sx = ax + ux * (Node / 2), sy = ay + uy * (Node / 2), ex = bx - ux * (Node / 2 + 2), ey = by - uy * (Node / 2 + 2);
                var geo = new PathGeometry();
                var fig = new PathFigure { StartPoint = new Point(sx, sy) };
                fig.Segments.Add(new LineSegment(new Point(ex, ey), true));
                if (e.Type != TalentEdge.Visual)
                {
                    double px = -uy, py = ux;
                    var h1 = new Point(ex - ux * 8 + px * 4, ey - uy * 8 + py * 4); var h2 = new Point(ex - ux * 8 - px * 4, ey - uy * 8 - py * 4);
                    fig.Segments.Add(new LineSegment(h1, true)); fig.Segments.Add(new LineSegment(new Point(ex, ey), false)); fig.Segments.Add(new LineSegment(h2, true));
                }
                geo.Figures.Add(fig);
                var path = new Path { Data = geo, Stroke = Res("Brush.Text.Tertiary"), StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round, IsHitTestVisible = false };
                TreeCanvas.Children.Add(path); edgePaths.Add(path); edgeList.Add(e);
            }

            foreach (var n in tree.Nodes)
            {
                var v = new NodeView { Node = n };
                var box = new Border
                {
                    Width = Node, Height = Node, CornerRadius = new CornerRadius(8), Background = Res("Brush.Surface2"), BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent,
                    Cursor = Cursors.Hand, Focusable = true, SnapsToDevicePixels = true, Tag = n.Id,
                };
                var grid = new Grid();
                v.Letter = new TextBlock { Text = "?", Style = (Style)FindResource("Text.Section"), Foreground = Res("Brush.Text.Secondary"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                grid.Children.Add(v.Letter);
                v.Img = new Image { Margin = new Thickness(2), Stretch = Stretch.Uniform };
                RenderOptions.SetBitmapScalingMode(v.Img, System.Windows.Media.BitmapScalingMode.HighQuality);
                grid.Children.Add(v.Img);
                v.Rank = new TextBlock { Style = (Style)FindResource("Text.Caption"), Foreground = Res("Brush.Text.Primary") };
                v.Badge = new Border { Background = Res("Brush.Surface3"), CornerRadius = new CornerRadius(8), Padding = new Thickness(4, 0, 4, 0), Child = v.Rank, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -6, -8), IsHitTestVisible = false };
                grid.Children.Add(v.Badge);
                box.Child = grid;
                box.MouseLeftButtonDown += NodeLeft;
                box.MouseRightButtonDown += NodeRight;
                box.KeyDown += (s, e) => { int id = (int)((Border)s).Tag; if (e.Key == Key.Enter || e.Key == Key.Space || e.Key == Key.Add) { Click(id, false); e.Handled = true; } else if (e.Key == Key.Delete || e.Key == Key.Back || e.Key == Key.Subtract) { Click(id, true); e.Handled = true; } };
                var tip = new ToolTip { Style = (Style)FindResource("CardTip") };
                int nodeId = n.Id;
                tip.Opened += (s, e) => tip.Content = TipFor(nodeId);
                box.ToolTip = tip;
                ToolTipService.SetInitialShowDelay(box, 150);
                Canvas.SetLeft(box, X(n)); Canvas.SetTop(box, Y(n));
                TreeCanvas.Children.Add(box);
                v.Box = box; views[n.Id] = v;
            }
            RefreshStates();
        }

        static ImageSource IconNow(int id) => id > 0 ? IconStore.TryGet(id) : null;

        void BindIcon(int id, Image img)
        {
            if (id <= 0) return;
            var have = IconStore.TryGet(id);
            if (have != null) { img.Source = have; return; }
            IconStore.Request(id, src => { try { img.Dispatcher.BeginInvoke(new Action(() => img.Source = src)); } catch { } });
        }

        // colours, ranks, icons, arrows, gates and the points line: everything that follows from the current ranks
        void RefreshStates()
        {
            if (tree == null || session == null) return;
            var st = session.State;
            foreach (var v in views.Values)
            {
                var n = v.Node;
                int rank = st.Rank(n.Id);
                bool avail = TalentRules.Available(tree, st, n) && session.Left >= tree.CostPerRank;
                bool availNow = TalentRules.Available(tree, st, n);
                bool maxed = rank >= n.MaxRanks;
                v.Rank.Text = rank + "/" + n.MaxRanks;
                v.Badge.Background = rank > 0 ? Res(maxed ? "Brush.Accent" : "Brush.Accent.Tint") : Res("Brush.Surface3");
                v.Rank.Foreground = rank > 0 && maxed ? Res("Brush.OnAccent") : Res("Brush.Text.Primary");
                v.Box.BorderBrush = rank > 0 ? Res("Brush.Accent") : availNow ? Res("Brush.Text.Tertiary") : Brushes.Transparent;
                v.Box.Opacity = rank > 0 || availNow ? 1 : 0.45;
                var name = NameOf(n);
                v.Letter.Text = string.IsNullOrEmpty(name) ? "?" : name.Substring(0, 1).ToUpperInvariant();
                int icon = n.Entry?.IconOverride > 0 ? n.Entry.IconOverride : (n.SpellId > 0 ? SpellData.Spell(n.SpellId)?.IconId ?? 0 : 0);
                if (icon > 0 && icon != v.IconId) { v.IconId = icon; BindIcon(icon, v.Img); }
                else if (icon > 0 && v.Img.Source == null) BindIcon(icon, v.Img);
                _ = avail;
            }
            // arrows: lit when the node they come from is full
            for (int i = 0; i < edgePaths.Count; i++)
            {
                var e = edgeList[i]; var from = tree.Node(e.From);
                bool full = from != null && st.Rank(e.From) >= from.MaxRanks;
                edgePaths[i].Stroke = Res(full ? "Brush.Text.Secondary" : "Brush.Text.Tertiary");
            }
            var spec = TalentRules.SpentPerSpec(tree, st);
            for (int s = 0; s < specHeads.Count; s++)
                specHeads[s].Text = (tree.SpecNames.Length > s ? tree.SpecNames[s] : "Tree " + (s + 1)) + (s < spec.Length && spec[s] > 0 ? "  " + spec[s] : "");
            foreach (var g in gates)
            {
                // open when the first node of the row (of that tree) is available as far as its gate goes
                var nodes = tree.Nodes.Where(n => n.Spec == g.Spec && n.Row == g.Row).ToList();
                bool open = nodes.Count > 0 && nodes.All(n => TalentRules.GateMissing(tree, st, n) == 0);
                g.Lock.Visibility = open ? Visibility.Hidden : Visibility.Visible;
                g.Label.Foreground = Res(open ? "Brush.Text.Secondary" : "Brush.Text.Tertiary");
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
        void NodeLeft(object sender, MouseButtonEventArgs e) { var b = (Border)sender; b.Focus(); Click((int)b.Tag, false); e.Handled = true; }
        void NodeRight(object sender, MouseButtonEventArgs e) { var b = (Border)sender; b.Focus(); Click((int)b.Tag, true); e.Handled = true; }

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

        StackPanel TipFor(int nodeId)
        {
            var sp = new StackPanel { MinWidth = 200 };
            var n = tree?.Node(nodeId);
            if (n == null) return sp;
            int rank = session.State.Rank(nodeId);
            var e = n.Entry;
            var name = NameOf(n) ?? ("Talent " + n.Id);
            sp.Children.Add(Line(name, "Text.RowTitle"));
            sp.Children.Add(Line("Rank " + rank + " of " + n.MaxRanks, "Text.Secondary", null, 2));
            info.Add("tip|" + nodeId);
            if (e != null && e.SpellId > 0)
            {
                string cur = rank > 0 ? DescOf(TalentData.SpellAtRank(e, rank)) : null;
                string next = rank < n.MaxRanks ? DescOf(TalentData.SpellAtRank(e, rank + 1)) : null;
                if (!string.IsNullOrEmpty(e.DescriptionOverride)) { cur = rank > 0 ? e.DescriptionOverride : null; next = rank < n.MaxRanks ? e.DescriptionOverride : null; }
                if (rank > 0 && cur != null) sp.Children.Add(Line(cur, "Text.Body", Res("Brush.Warn"), 8));
                if (rank < n.MaxRanks && next != null) sp.Children.Add(Line((rank > 0 ? "Next rank: " : "Rank 1: ") + next, "Text.Body", rank > 0 ? Res("Brush.Text.Secondary") : Res("Brush.Warn"), 8));
            }
            if (rank < n.MaxRanks && !TalentRules.Available(tree, session.State, n)) sp.Children.Add(Line("Locked: " + WhyBlocked(n), "Text.Secondary", Res("Brush.Danger"), 8));
            else if (rank >= n.MaxRanks) sp.Children.Add(Line("Full rank. Right-click to give a point back.", "Text.Caption", null, 8));
            else if (session.Left < tree.CostPerRank) sp.Children.Add(Line("No points left at this level.", "Text.Caption", null, 8));
            else sp.Children.Add(Line("Click to spend a point.", "Text.Caption", null, 8));
            if (e != null && e.SpellId > 0) sp.Children.Add(Line("Spell ID " + e.SpellId, "Text.Caption", null, 4));
            return sp;
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
            if (!d.Ok) { SetStatus(d.Error, true); return false; }
            if (!string.Equals(d.Tree.ClassFile, classFile, StringComparison.OrdinalIgnoreCase)) SelectClass(d.Tree.ClassFile);
            session.Load(d.State); FitLevel(); openBuild = null; DetailBox.Visibility = Visibility.Collapsed;
            RefreshStates();
            SetStatus("Loaded a " + tree.ClassName + " build with " + d.State.Total + " points.", false);
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
            DetailBox.Visibility = Visibility.Visible;
            info.Add("detail|" + (build is RecBuild r2 ? r2.Name : (build as SavedBuild)?.Name));
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
        public string StatusForTest => StatusLine.Visibility == Visibility.Visible ? StatusLine.Text : "";
        public bool StatusIsErrorForTest => StatusLine.Visibility == Visibility.Visible && StatusLine.Foreground == Res("Brush.Danger");
        public bool StatusIsWarnForTest => StatusLine.Visibility == Visibility.Visible && StatusLine.Foreground == Res("Brush.Warn");
        public void ScrollTopForTest() { PageScroll.ScrollToTop(); }
        public string PointsLineForTest => PointsLine.Text;
        public string ClassTitleForTest => ClassTitle.Text;
        public bool EmptyVisibleForTest => EmptyBox.Visibility == Visibility.Visible;
        public string EmptyTextForTest => EmptyTitle.Text + " | " + EmptyText.Text;
        public int NodeCountForTest => views.Count;
        public int EdgeCountForTest => edgePaths.Count;
        public int GateCountForTest => gates.Count;
        public string BuildsNoteForTest => BuildsNote.Text;
        public List<string> InfoForTest => info;
        public List<string> ClassNamesForTest => ClassBar.Children.OfType<RadioButton>().Select(r => (string)r.Tag).ToList();
        public string NodeRankTextForTest(int id) => views.TryGetValue(id, out var v) ? v.Rank.Text : null;
        public double NodeOpacityForTest(int id) => views.TryGetValue(id, out var v) ? v.Box.Opacity : -1;
        public bool NodeHasIconForTest(int id) => views.TryGetValue(id, out var v) && v.Img.Source != null;
        public FrameworkElement NodeBoxForTest(int id) => views.TryGetValue(id, out var v) ? v.Box : null;
        public FrameworkElement TipForTest(int id)
        {
            var frame = new Border { Background = Res("Brush.Surface3"), BorderBrush = Res("Brush.Divider"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8, 12, 8), Child = TipFor(id) };
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
