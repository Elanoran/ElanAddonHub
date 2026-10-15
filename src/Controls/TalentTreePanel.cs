using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Talents;

namespace ElansAddonHub
{
    // ============================================================================================================================
    // The one talent-tree renderer of the Outpost, used by Characters > Talents (read-only: the ranks of one character) and the Atlas Talents planner
    // (interactive: spending points). Both build a TalentTreeModel per tree and put a TalentTreePanel per model side by side (TalentTreePanel.Columns):
    //   * a header: the spec's icon (its class skill line's icon from the game data), its name, the points "26 / 51" (spent / the class' max points);
    //   * the game's grid (tier row + column of every talent, from the game data, never guessed) spread evenly over the panel's width, empty cells
    //     left empty so the shape is the game's, icons sized to the panel (36-48 px) with a rank badge "r/m";
    //   * prerequisite arrows from a talent's prerequisite down (or across / around the corner) to the talent, behind the icons: gold when the
    //     prerequisite is full, dim otherwise;
    //   * the spec art (ArtLibrary.Spec) behind the tree under Brush.Art.Veil when there is art for it, a plain panel otherwise.
    // ============================================================================================================================

    public sealed class TalentCellModel
    {
        public int Id, Col, Row, Rank, Max, IconId, SpellId;
        public string Name = "";
        public bool Open;                    // can take a point now (the planner) / could have taken one (a character's tree): drawn undimmed
    }

    // an arrow from the prerequisite (From, the talent above / to the side) to the talent it unlocks (To)
    public sealed class TalentArrowModel
    {
        public int From, To;
        public bool Lit;                     // the prerequisite is at full rank
    }

    public sealed class TalentTreeModel
    {
        public int Spec;                     // 0-based, left to right as the game lays the class' trees out; -1 = not known
        public string Title = "";
        public int IconId;
        public int Spent, MaxPoints;         // MaxPoints 0 = not known ("26 points")
        public int Cols = 4, Rows = 1;
        public ArtPiece Art;
        public List<TalentCellModel> Cells = new List<TalentCellModel>();
        public List<TalentArrowModel> Arrows = new List<TalentArrowModel>();
        public Dictionary<int, int> Gates = new Dictionary<int, int>();          // row -> points the rows above need (the planner shows them)
        public HashSet<int> OpenGates = new HashSet<int>();

        public string PointsText => MaxPoints > 0 ? Spent.ToString(CultureInfo.InvariantCulture) + " / " + MaxPoints.ToString(CultureInfo.InvariantCulture)
            : Spent.ToString(CultureInfo.InvariantCulture) + (Spent == 1 ? " point" : " points");
        public string HeaderText => Title + " " + PointsText;
        public TalentCellModel Cell(int id) => Cells.FirstOrDefault(c => c.Id == id);
    }

    public static class TalentTreeModels
    {
        // the grid of a class: as many columns and rows as its widest / longest tree (all trees of a class get the same grid, so they line up)
        public static void GridOf(TalentTreeDef def, out int cols, out int rows)
        {
            cols = def == null || def.Nodes.Count == 0 ? 1 : def.Nodes.Max(n => n.Col) + 1;
            rows = def == null || def.Nodes.Count == 0 ? 1 : def.Nodes.Max(n => n.Row) + 1;
        }

        // one model per tree of the class from the game data and a set of ranks. openOf null = the game's rules (TalentRules.Available).
        public static List<TalentTreeModel> FromDef(TalentTreeDef def, TalentState st, Func<TalentNode, string> nameOf, Func<TalentNode, int> iconOf, Func<TalentNode, bool> openOf = null)
        {
            var res = new List<TalentTreeModel>();
            if (def == null) return res;
            GridOf(def, out int cols, out int rows);
            for (int s = 0; s < def.SpecCount; s++)
            {
                var m = new TalentTreeModel
                {
                    Spec = s, Cols = cols, Rows = rows, MaxPoints = def.MaxPoints,
                    Title = s < def.SpecNames.Length && !string.IsNullOrWhiteSpace(def.SpecNames[s]) ? def.SpecNames[s] : "Tree " + (s + 1),
                    IconId = s < def.SpecIcons.Length ? def.SpecIcons[s] : 0,
                    Art = ArtLibrary.Spec(def.ClassFile, s),
                };
                foreach (var n in def.Nodes.Where(n => n.Spec == s))
                    m.Cells.Add(new TalentCellModel { Id = n.Id, Col = n.Col, Row = n.Row, Max = n.MaxRanks, SpellId = n.SpellId, Name = nameOf?.Invoke(n) ?? "", IconId = iconOf?.Invoke(n) ?? 0 });
                for (int r = 1; r < rows; r++)
                {
                    int need = def.Nodes.Where(n => n.Spec == s && n.Row == r).Select(n => TalentRules.GateNeeded(def, n)).DefaultIfEmpty(0).Max();
                    if (need > 0) m.Gates[r] = need;
                }
                m.Arrows = ArrowsOf(def, s);
                Apply(m, def, st, openOf);
                res.Add(m);
            }
            return res;
        }

        // the arrows of one tree: every edge of the game data between two talents of it (prerequisite, "sufficient" and the visual-only ones), one
        // arrow per pair, pointing from the talent nearer the top (on one row: the left one) to the other - the game's data has a pair written both ways
        public static List<TalentArrowModel> ArrowsOf(TalentTreeDef def, int spec)
        {
            var res = new List<TalentArrowModel>();
            var seen = new HashSet<long>();
            foreach (var e in def.Edges.Where(e => e.Type == TalentEdge.Required || e.Type == TalentEdge.Sufficient || e.Type == TalentEdge.Visual))
            {
                var a = def.Node(e.From); var b = def.Node(e.To);
                if (a == null || b == null || a.Spec != spec || b.Spec != spec || a.Id == b.Id) continue;
                if (b.Row < a.Row || (b.Row == a.Row && b.Col < a.Col)) { var t = a; a = b; b = t; }
                if (!seen.Add(((long)a.Id << 32) | (uint)b.Id)) continue;
                res.Add(new TalentArrowModel { From = a.Id, To = b.Id });
            }
            return res;
        }

        // ranks, open state, lit arrows, open gates and points from a set of ranks
        public static void Apply(TalentTreeModel m, TalentTreeDef def, TalentState st, Func<TalentNode, bool> openOf = null)
        {
            st = st ?? new TalentState();
            foreach (var c in m.Cells)
            {
                var n = def.Node(c.Id);
                c.Rank = st.Rank(c.Id);
                c.Open = n != null && (openOf != null ? openOf(n) : TalentRules.Available(def, st, n));
            }
            foreach (var a in m.Arrows) { var f = def.Node(a.From); a.Lit = f != null && st.Rank(f.Id) >= f.MaxRanks; }
            m.OpenGates.Clear();
            foreach (var g in m.Gates)
            {
                var nodes = def.Nodes.Where(n => n.Spec == m.Spec && n.Row == g.Key).ToList();
                if (nodes.Count > 0 && nodes.All(n => TalentRules.GateMissing(def, st, n) == 0)) m.OpenGates.Add(g.Key);
            }
            var spent = TalentRules.SpentPerSpec(def, st);
            m.Spent = m.Spec >= 0 && m.Spec < spent.Length ? spent[m.Spec] : 0;
        }
    }

    // one talent on the grid: the icon (a letter tile under it until it loads), the rank badge
    public sealed class TalentNodeView
    {
        public int Id;
        public Border Box;
        public Image Img;
        public TextBlock Letter, Rank;
        public Border Badge;
        public int IconId;
    }

    // ============================================================================================================================
    // The grid of one tree: lays the talents (and the planner's row gates) out on the game's cells and draws the arrows under them.
    // ============================================================================================================================
    public sealed class TalentGridPanel : Panel
    {
        public const double IconMin = 36, IconMax = 48, GateGutter = 24, NaturalCell = 64;
        readonly Dictionary<UIElement, Point> cellOf = new Dictionary<UIElement, Point>();     // node: (col,row); gate: (-1,row)
        readonly Dictionary<int, UIElement> byId = new Dictionary<int, UIElement>();
        public int Cols = 4, Rows = 1;
        public bool Gutter;
        public List<TalentArrowModel> Arrows = new List<TalentArrowModel>();

        public double IconSize { get; private set; } = IconMin;
        public double CellWidth { get; private set; }
        public double RowHeight { get; private set; }

        public void AddNode(int id, int col, int row, UIElement el) { cellOf[el] = new Point(col, row); byId[id] = el; Children.Add(el); }
        public void AddGate(int row, UIElement el) { cellOf[el] = new Point(-1, row); Children.Add(el); }

        // the icon size for a grid this wide: as big as the cells allow, between IconMin and IconMax
        public static double IconFor(double cellWidth) => Math.Max(IconMin, Math.Min(IconMax, Math.Floor(cellWidth - 20)));
        public static double RowFor(double icon) => icon + 32;

        void Metrics(double width)
        {
            double gut = Gutter ? GateGutter : 0;
            if (double.IsInfinity(width) || double.IsNaN(width) || width <= 0) width = gut + Cols * NaturalCell;
            CellWidth = Math.Max(1, (width - gut) / Math.Max(1, Cols));
            IconSize = IconFor(CellWidth);
            RowHeight = RowFor(IconSize);
        }

        protected override Size MeasureOverride(Size available)
        {
            Metrics(available.Width);
            foreach (UIElement ch in Children)
            {
                if (cellOf.TryGetValue(ch, out var p) && p.X >= 0) ch.Measure(new Size(IconSize, IconSize));
                else ch.Measure(new Size(GateGutter, RowHeight));
            }
            double w = double.IsInfinity(available.Width) ? (Gutter ? GateGutter : 0) + Cols * CellWidth : available.Width;
            return new Size(w, Rows * RowHeight);
        }

        protected override Size ArrangeOverride(Size final)
        {
            Metrics(final.Width);
            foreach (UIElement ch in Children)
            {
                if (!cellOf.TryGetValue(ch, out var p)) continue;
                if (p.X >= 0)
                {
                    var c = Center((int)p.X, (int)p.Y);
                    ch.Arrange(new Rect(c.X - IconSize / 2, c.Y - IconSize / 2, IconSize, IconSize));
                }
                else
                {
                    var d = ch.DesiredSize;
                    ch.Arrange(new Rect(0, p.Y * RowHeight + (RowHeight - d.Height) / 2, GateGutter, d.Height));
                }
            }
            InvalidateVisual();
            return final;
        }

        public Point Center(int col, int row) => new Point((Gutter ? GateGutter : 0) + (col + 0.5) * CellWidth, (row + 0.5) * RowHeight);

        // the arranged box of a talent (the self-test checks the grid with it)
        public Rect NodeRect(int id)
        {
            if (!byId.TryGetValue(id, out var el) || !cellOf.TryGetValue(el, out var p)) return Rect.Empty;
            var c = Center((int)p.X, (int)p.Y);
            return new Rect(c.X - IconSize / 2, c.Y - IconSize / 2, IconSize, IconSize);
        }

        // the polyline of an arrow: from the edge of the prerequisite's icon to the edge of the talent's icon. Same column: straight down; same row:
        // straight across; otherwise across from the prerequisite's side to the talent's column, then down onto it.
        public List<Point> ArrowPoints(TalentArrowModel a)
        {
            if (!byId.TryGetValue(a.From, out var fe) || !byId.TryGetValue(a.To, out var te)) return null;
            var pf = cellOf[fe]; var pt = cellOf[te];
            var cf = Center((int)pf.X, (int)pf.Y); var ct = Center((int)pt.X, (int)pt.Y);
            double h = IconSize / 2 + 2;
            var pts = new List<Point>();
            if ((int)pf.X == (int)pt.X)
            {
                double dir = ct.Y >= cf.Y ? 1 : -1;
                pts.Add(new Point(cf.X, cf.Y + dir * h)); pts.Add(new Point(ct.X, ct.Y - dir * (h + 2)));
            }
            else if ((int)pf.Y == (int)pt.Y)
            {
                double dir = ct.X >= cf.X ? 1 : -1;
                pts.Add(new Point(cf.X + dir * h, cf.Y)); pts.Add(new Point(ct.X - dir * (h + 2), ct.Y));
            }
            else
            {
                double dir = ct.X >= cf.X ? 1 : -1, down = ct.Y >= cf.Y ? 1 : -1;
                pts.Add(new Point(cf.X + dir * h, cf.Y)); pts.Add(new Point(ct.X, cf.Y)); pts.Add(new Point(ct.X, ct.Y - down * (h + 2)));
            }
            return pts;
        }

        static Brush Res(string key) => Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            foreach (var a in Arrows.OrderBy(x => x.Lit ? 1 : 0))            // lit ones on top
            {
                var pts = ArrowPoints(a);
                if (pts == null || pts.Count < 2) continue;
                var brush = Res(a.Lit ? "Brush.Badge.Gold" : "Brush.Text.Tertiary");
                var pen = new Pen(brush, 3) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Flat };
                var end = pts[pts.Count - 1]; var prev = pts[pts.Count - 2];
                double dx = end.X - prev.X, dy = end.Y - prev.Y, len = Math.Sqrt(dx * dx + dy * dy);
                if (len < 1) continue;
                double ux = dx / len, uy = dy / len, head = 8, half = 5.5;
                var baseP = new Point(end.X - ux * head, end.Y - uy * head);
                var geo = new StreamGeometry();
                using (var g = geo.Open())
                {
                    g.BeginFigure(pts[0], false, false);
                    for (int i = 1; i < pts.Count - 1; i++) g.LineTo(pts[i], true, true);
                    g.LineTo(baseP, true, true);
                }
                geo.Freeze();
                dc.DrawGeometry(null, pen, geo);
                var tri = new StreamGeometry();
                using (var g = tri.Open())
                {
                    g.BeginFigure(end, true, true);
                    g.LineTo(new Point(baseP.X - uy * half, baseP.Y + ux * half), true, true);
                    g.LineTo(new Point(baseP.X + uy * half, baseP.Y - ux * half), true, true);
                }
                tri.Freeze();
                dc.DrawGeometry(brush, null, tri);
            }
        }
    }

    // ============================================================================================================================
    // One tree's panel: the card (spec art under the veil, or plain), the header and the grid.
    // ============================================================================================================================
    public sealed class TalentTreePanel : Border
    {
        public TalentTreeModel Model { get; private set; }
        public TalentGridPanel Grid { get; } = new TalentGridPanel();
        public readonly Dictionary<int, TalentNodeView> Views = new Dictionary<int, TalentNodeView>();
        public bool Painted { get; }
        public bool Interactive { get; }
        public Image SpecIcon { get; }
        public TextBlock TitleText { get; }
        public TextBlock PointsBlock { get; }

        public event Action<int, bool> NodeClicked;          // id, right button / Delete
        public event Action<int> NodeCtrlClicked;
        readonly Func<int, object> tipFor;
        readonly Dictionary<int, (TextBlock Lock, TextBlock Label)> gateViews = new Dictionary<int, (TextBlock, TextBlock)>();

        static Brush Res(string key) => (Brush)Application.Current.Resources[key];
        static Style St(string key) => (Style)Application.Current.Resources[key];

        // surfaceKey: the plain panel's colour (Surface1 on a page, Surface2 inside a card); tipFor: the tooltip content of a talent, built when it opens
        public TalentTreePanel(TalentTreeModel m, bool interactive, bool gates, string surfaceKey, Func<int, object> tipFor)
        {
            Model = m; Interactive = interactive; this.tipFor = tipFor;
            var radius = (CornerRadius)Application.Current.Resources["Radius.Card"];
            CornerRadius = radius;
            Background = Res(surfaceKey ?? "Brush.Surface1");
            var layers = new System.Windows.Controls.Grid();
            var back = m.Art == null ? null : ArtLibrary.Backdrop(m.Art, "Brush.Art.Veil", radius, "tree-art");
            Painted = back != null;
            if (back != null) layers.Children.Add(back);
            Tag = Painted ? "tree-painted" : "tree-plain";

            var body = new DockPanel();
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
            PointsBlock = new TextBlock { Style = St("Text.Secondary"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            DockPanel.SetDock(PointsBlock, Dock.Right);
            head.Children.Add(PointsBlock);
            SpecIcon = new Image { Width = 24, Height = 24, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = m.IconId > 0 ? Visibility.Visible : Visibility.Collapsed };
            RenderOptions.SetBitmapScalingMode(SpecIcon, BitmapScalingMode.HighQuality);
            if (m.IconId > 0) Bind(m.IconId, SpecIcon, null);
            DockPanel.SetDock(SpecIcon, Dock.Left);
            head.Children.Add(SpecIcon);
            TitleText = new TextBlock { Text = m.Title, Style = St("Text.RowTitle"), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            head.Children.Add(TitleText);
            DockPanel.SetDock(head, Dock.Top);
            body.Children.Add(head);

            Grid.Cols = Math.Max(1, m.Cols); Grid.Rows = Math.Max(1, m.Rows); Grid.Gutter = gates; Grid.Arrows = m.Arrows;
            foreach (var c in m.Cells) Grid.AddNode(c.Id, c.Col, c.Row, NodeBox(c));
            if (gates)
                foreach (var g in m.Gates)
                {
                    var lk = new TextBlock { Text = "", FontFamily = (FontFamily)Application.Current.Resources["Font.Icons"], FontSize = 12, Foreground = Res("Brush.Text.Tertiary"), HorizontalAlignment = HorizontalAlignment.Center };
                    var lb = new TextBlock { Text = g.Value.ToString(CultureInfo.InvariantCulture), Style = St("Text.Caption"), Foreground = Res("Brush.Text.Tertiary"), HorizontalAlignment = HorizontalAlignment.Center };
                    var sp = new StackPanel { ToolTip = g.Value + " points in the rows above open this row", Background = Brushes.Transparent };
                    sp.Children.Add(lk); sp.Children.Add(lb);
                    Grid.AddGate(g.Key, sp);
                    gateViews[g.Key] = (lk, lb);
                }
            body.Children.Add(Grid);
            layers.Children.Add(new Border { Padding = new Thickness(16), Child = body });
            Child = layers;
            Update();
        }

        static void Bind(int id, Image img, TextBlock letter)
        {
            var have = IconStore.TryGet(id);
            if (have != null) { img.Source = have; if (letter != null) letter.Visibility = Visibility.Hidden; return; }
            IconStore.Request(id, src => { try { img.Dispatcher.BeginInvoke(new Action(() => { img.Source = src; if (letter != null) letter.Visibility = Visibility.Hidden; })); } catch { } });
        }

        FrameworkElement NodeBox(TalentCellModel c)
        {
            var v = new TalentNodeView { Id = c.Id };
            var box = new Border
            {
                CornerRadius = new CornerRadius(8), Background = Res("Brush.Surface2"), BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent,
                SnapsToDevicePixels = true, Tag = c.Id, Focusable = Interactive, Cursor = Interactive ? Cursors.Hand : null,
            };
            var g = new System.Windows.Controls.Grid();
            v.Letter = new TextBlock { Text = "?", Style = St("Text.Section"), Foreground = Res("Brush.Text.Secondary"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            g.Children.Add(v.Letter);
            v.Img = new Image { Margin = new Thickness(2), Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(v.Img, BitmapScalingMode.HighQuality);
            g.Children.Add(v.Img);
            v.Rank = new TextBlock { Style = St("Text.Caption"), Foreground = Res("Brush.Text.Primary") };
            v.Badge = new Border { Background = Res("Brush.Surface3"), CornerRadius = new CornerRadius(8), Padding = new Thickness(4, 0, 4, 0), Child = v.Rank, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -8, -8), IsHitTestVisible = false };
            g.Children.Add(v.Badge);
            box.Child = g;
            if (Interactive)
            {
                box.MouseLeftButtonDown += (s, e) => { box.Focus(); e.Handled = true; if (Keyboard.Modifiers == ModifierKeys.Control) NodeCtrlClicked?.Invoke(c.Id); else NodeClicked?.Invoke(c.Id, false); };
                box.MouseRightButtonDown += (s, e) => { box.Focus(); e.Handled = true; NodeClicked?.Invoke(c.Id, true); };
                box.KeyDown += (s, e) =>
                {
                    if (e.Key == Key.Enter || e.Key == Key.Space || e.Key == Key.Add) { NodeClicked?.Invoke(c.Id, false); e.Handled = true; }
                    else if (e.Key == Key.Delete || e.Key == Key.Back || e.Key == Key.Subtract) { NodeClicked?.Invoke(c.Id, true); e.Handled = true; }
                };
            }
            if (tipFor != null)
            {
                var tip = GameTooltip.Wrap(null);
                tip.Opened += (s, e) => tip.Content = tipFor(c.Id);
                box.ToolTip = tip;
                ToolTipService.SetInitialShowDelay(box, 150);
                ToolTipService.SetShowDuration(box, 30000);
            }
            v.Box = box;
            Views[c.Id] = v;
            return box;
        }

        // everything that follows from the model's ranks: badges, borders, dimming, icons, arrows, gates, the points
        public void Update()
        {
            var m = Model;
            foreach (var c in m.Cells)
            {
                if (!Views.TryGetValue(c.Id, out var v)) continue;
                bool maxed = c.Max > 0 && c.Rank >= c.Max;
                v.Rank.Text = c.Rank + "/" + c.Max;
                v.Badge.Background = c.Rank > 0 ? Res(maxed ? "Brush.Accent" : "Brush.Accent.Tint") : Res("Brush.Surface3");
                v.Rank.Foreground = c.Rank > 0 && maxed ? Res("Brush.OnAccent") : Res("Brush.Text.Primary");
                v.Box.BorderBrush = c.Rank > 0 ? Res(maxed ? "Brush.Badge.Gold" : "Brush.Accent") : c.Open ? Res("Brush.Text.Tertiary") : Brushes.Transparent;
                v.Box.Opacity = c.Rank > 0 || c.Open ? 1 : 0.45;
                v.Letter.Text = string.IsNullOrEmpty(c.Name) ? "?" : c.Name.Substring(0, 1).ToUpperInvariant();
                System.Windows.Automation.AutomationProperties.SetName(v.Box, (string.IsNullOrEmpty(c.Name) ? "Talent" : c.Name) + ", rank " + c.Rank + " of " + c.Max);
                if (c.IconId > 0 && (c.IconId != v.IconId || v.Img.Source == null)) { v.IconId = c.IconId; Bind(c.IconId, v.Img, v.Letter); }
            }
            foreach (var g in gateViews)
            {
                bool open = m.OpenGates.Contains(g.Key);
                g.Value.Lock.Visibility = open ? Visibility.Hidden : Visibility.Visible;
                g.Value.Label.Foreground = Res(open ? "Brush.Text.Secondary" : "Brush.Text.Tertiary");
            }
            TitleText.Text = m.Title;
            PointsBlock.Text = m.PointsText;
            Grid.Arrows = m.Arrows;
            Grid.InvalidateVisual();
        }

        public int GateCount => gateViews.Count;
        public bool GateOpen(int row) => Model.OpenGates.Contains(row);

        // ---- the trees side by side: equal columns that grow with the width of `widthSource` between MinWidth and MaxWidth, one under the other
        // when it is too narrow for all of them
        public const double TreeMinWidth = 220, TreeMaxWidth = 440, TreeGap = 12;

        public static System.Windows.Controls.Grid Columns(IList<FrameworkElement> cards, FrameworkElement widthSource, Func<double> fallbackWidth = null, double maxWidth = TreeMaxWidth)
        {
            var grid = new System.Windows.Controls.Grid { HorizontalAlignment = HorizontalAlignment.Left };
            foreach (var card in cards) grid.Children.Add(card);
            int mode = -1;
            void Place(double width)
            {
                int n = cards.Count;
                if (n == 0) return;
                int want = width <= 0 || width >= n * TreeMinWidth + (n - 1) * TreeGap ? 1 : 0;      // 1 = side by side, 0 = one under the other
                double cap = want == 1 ? n * maxWidth + (n - 1) * TreeGap : maxWidth;
                if (width > 0) grid.Width = Math.Min(width, cap);
                if (want == mode) return;
                mode = want;
                grid.ColumnDefinitions.Clear(); grid.RowDefinitions.Clear();
                if (want == 1)
                {
                    for (int i = 0; i < n; i++)
                    {
                        if (i > 0) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(TreeGap) });
                        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = TreeMinWidth, MaxWidth = maxWidth });
                        System.Windows.Controls.Grid.SetColumn(cards[i], i * 2); System.Windows.Controls.Grid.SetRow(cards[i], 0); cards[i].Margin = new Thickness(0);
                    }
                }
                else
                {
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    for (int i = 0; i < n; i++)
                    {
                        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                        System.Windows.Controls.Grid.SetColumn(cards[i], 0); System.Windows.Controls.Grid.SetRow(cards[i], i); double below = i < n - 1 ? TreeGap : 0; cards[i].Margin = new Thickness(0, 0, 0, below);
                    }
                }
            }
            SizeChangedEventHandler h = (s, e) => { if (grid.Parent != null) Place(widthSource.ActualWidth); };
            widthSource.SizeChanged += h;
            grid.Unloaded += (s, e) => { if (grid.Parent == null) widthSource.SizeChanged -= h; };
            Place(widthSource.ActualWidth > 0 ? widthSource.ActualWidth : fallbackWidth?.Invoke() ?? 0);
            return grid;
        }

        public static bool SideBySide(IList<FrameworkElement> cards) => cards.Count > 1 && System.Windows.Controls.Grid.GetRow(cards[0]) == System.Windows.Controls.Grid.GetRow(cards[1]) && System.Windows.Controls.Grid.GetColumn(cards[0]) != System.Windows.Controls.Grid.GetColumn(cards[1]);
    }
}
