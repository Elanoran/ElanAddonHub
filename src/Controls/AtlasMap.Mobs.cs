using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using ElansAddonHub.Services.DungeonMaps;
using ElansAddonHub.Services.Maps;

namespace ElansAddonHub
{
    // The Atlas map, part 3: the creatures of a drawn instance (Services/DungeonMaps/DungeonMobs.cs, from the Classic baseline) on the floor shown.
    //   - four chips top-left (FilterChip look: a dot in the layer's colour, shown = filled + Text.Primary): Bosses (Brush.Danger), Pulls (Brush.Info),
    //     Rares (Brush.Badge.Gold), Route (Brush.Accent); they hold for the session, every instance. A quiet line under them says where the positions
    //     come from and, with the route on, that it is a suggestion worked out from the map, not a guide.
    //   - bosses: a red disc with the route's number, never merged with other markers; pulls: a dot (the count inside when more than one); rares: a gold
    //     diamond; a small amber "!" on any marker whose creatures do something that changes the pull (fear, mind control, silence, heals, summons,
    //     stealth, running or calling for help). Hover: who is there, their levels, the warnings in plain words; click: the creature's page.
    //   - the route: an accent line from the entrance (the Entrance glyph) past the bosses in the route's order; dashed where it crosses a passage the plan
    //     has no floor for (a ramp, plank bridge or water); a small "to Level 2" / "from Level 1" tag where it leaves or joins the floor shown.
    //   - patrols: a thin dashed line of their walk, in the pull colour.
    public sealed partial class AtlasMap
    {
        public const string MobLayer = "mobs";
        public static bool ShowBosses = true, ShowPulls = true, ShowRares = true, ShowRoute = true;     // the chips, for the session

        DungeonMobLayer mobLayer; DungeonPlan mobPlan;
        readonly StackPanel mobBox = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12), Visibility = Visibility.Collapsed };
        readonly Border mobChipHost = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(2), HorizontalAlignment = HorizontalAlignment.Left };
        readonly Border mobNoteHost = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false, MaxWidth = 460 };
        readonly TextBlock mobNote = new TextBlock { TextWrapping = TextWrapping.Wrap };
        ToggleButton chipBosses, chipPulls, chipRares, chipRoute;

        public DungeonMobLayer MobLayerForTest => mobLayer;
        public IReadOnlyList<AtlasMarker> MobMarkersForTest => markers.Where(m => m.Layer == MobLayer).ToList();
        public bool MobChipsShownForTest => mobBox.Visibility == Visibility.Visible;
        public string MobNoteForTest => mobNoteHost.Visibility == Visibility.Visible ? mobNote.Text : null;
        public void SetMobChipForTest(string which, bool on)
        {
            var c = which == "bosses" ? chipBosses : which == "pulls" ? chipPulls : which == "rares" ? chipRares : chipRoute;
            c.IsChecked = on;
        }

        static readonly string ChipTemplate =
            "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='ToggleButton'>" +
            "<Grid Height='24' Background='Transparent'>" +
            "<Border x:Name='bd' Background='Transparent' CornerRadius='12' />" +
            "<Border x:Name='hv' Background='{DynamicResource Brush.Overlay.Hover}' CornerRadius='12' Opacity='0' IsHitTestVisible='False' />" +
            "<StackPanel Orientation='Horizontal' Margin='10,0' VerticalAlignment='Center'>" +
            "<Ellipse x:Name='dot' Width='8' Height='8' Fill='{TemplateBinding Tag}' Opacity='0.4' Margin='0,0,6,0' />" +
            "<ContentPresenter VerticalAlignment='Center' /></StackPanel>" +
            "<Border x:Name='ring' Margin='-2' CornerRadius='14' BorderThickness='2' BorderBrush='{DynamicResource Brush.Focus}' Visibility='Collapsed' IsHitTestVisible='False' />" +
            "</Grid><ControlTemplate.Triggers>" +
            "<Trigger Property='IsMouseOver' Value='True'><Setter TargetName='hv' Property='Opacity' Value='1' /></Trigger>" +
            "<Trigger Property='IsChecked' Value='True'><Setter Property='Foreground' Value='{DynamicResource Brush.Text.Primary}' /><Setter TargetName='bd' Property='Background' Value='{DynamicResource Brush.Surface3}' /><Setter TargetName='dot' Property='Opacity' Value='1' /></Trigger>" +
            "<Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='ring' Property='Visibility' Value='Visible' /></Trigger>" +
            "</ControlTemplate.Triggers></ControlTemplate>";

        ToggleButton Chip(string text, string brushKey, string tag, bool on, string help, Action<bool> set)
        {
            var b = new ToggleButton { Content = text, IsChecked = on, FontSize = 12, Cursor = System.Windows.Input.Cursors.Hand, FocusVisualStyle = null, Template = (ControlTemplate)XamlReader.Parse(ChipTemplate), ToolTip = help };
            b.SetResourceReference(TagProperty, brushKey);
            b.SetResourceReference(ForegroundProperty, "Brush.Text.Secondary");
            System.Windows.Automation.AutomationProperties.SetName(b, help);
            b.Name = tag;
            b.Checked += (s, e) => { set(true); RefreshMobs(); };
            b.Unchecked += (s, e) => { set(false); RefreshMobs(); };
            return b;
        }

        void BuildMobParts()
        {
            chipBosses = Chip("Bosses", "Brush.Danger", "mobBosses", ShowBosses, "Show the bosses, numbered in the route's order", v => ShowBosses = v);
            chipPulls = Chip("Pulls", "Brush.Info", "mobPulls", ShowPulls, "Show the other creatures, grouped into pulls", v => ShowPulls = v);
            chipRares = Chip("Rares", "Brush.Badge.Gold", "mobRares", ShowRares, "Show the rare creatures", v => ShowRares = v);
            chipRoute = Chip("Route", "Brush.Accent", "mobRoute", ShowRoute, "Show a suggested route past every boss", v => ShowRoute = v);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var c in new[] { chipBosses, chipPulls, chipRares, chipRoute }) row.Children.Add(c);
            mobChipHost.Child = row;
            mobChipHost.SetResourceReference(Border.BackgroundProperty, "Brush.Surface2");
            StyleOf(mobNote, "Text.Caption");
            mobNoteHost.SetResourceReference(Border.BackgroundProperty, "Brush.Surface2");
            mobNoteHost.Child = mobNote;
            mobBox.Children.Add(mobChipHost); mobBox.Children.Add(mobNoteHost);
            stage.Children.Add(mobBox);
            DungeonMobs.Changed += id => Dispatcher.BeginInvoke(new Action(() => { if (plan != null && plan.MapId == id && mobLayer == null) ShowMobs(); }));
        }

        void LeaveMobs()
        {
            mobLayer = null; mobPlan = null;
            mobBox.Visibility = Visibility.Collapsed;
            if (markers.RemoveAll(m => m.Layer == MobLayer) > 0) { clusters = null; hover = null; HideTip(); view?.InvalidateVisual(); }
        }

        // the floor shown changed (or a new plan): the layer of this plan, then its markers on this floor
        void ShowMobs()
        {
            var p = plan;
            if (p == null || drawnFloor < 0 || !DungeonMobs.Available) { LeaveMobs(); return; }
            if (mobPlan == p && mobLayer != null) { RefreshMobs(); return; }
            var t = DungeonMobs.Layer(p);
            if (t.IsCompleted) { ApplyMobs(p, t.Status == TaskStatus.RanToCompletion ? t.Result : null); return; }
            mobBox.Visibility = Visibility.Visible;
            mobChipHost.Visibility = Visibility.Collapsed;
            mobNote.Text = "Placing the creatures (Classic baseline)...";
            mobNoteHost.Visibility = Visibility.Visible;
            t.ContinueWith(x => Dispatcher.BeginInvoke(new Action(() => { if (plan == p) ApplyMobs(p, x.Status == TaskStatus.RanToCompletion ? x.Result : null); })));
        }

        void ApplyMobs(DungeonPlan p, DungeonMobLayer layer)
        {
            mobPlan = p; mobLayer = layer;
            if (layer == null || layer.Packs.Count == 0) { LeaveMobs(); return; }
            mobBox.Visibility = Visibility.Visible;
            mobChipHost.Visibility = Visibility.Visible;
            chipRares.Visibility = layer.Packs.Any(x => x.Kind == MobPackKind.Rare) ? Visibility.Visible : Visibility.Collapsed;
            chipRoute.Visibility = layer.Route.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            RefreshMobs();
        }

        static string FloorName(DungeonPlan p, int f) => p != null && f >= 0 && f < p.Floors.Count ? p.Floors[f].Name : "another floor";

        void RefreshMobs()
        {
            markers.RemoveAll(m => m.Layer == MobLayer);
            var layer = mobLayer; var p = plan;
            if (layer != null && p != null && drawnFloor >= 0 && mobPlan == p)
            {
                int f = drawnFloor;
                System.Windows.Point Frac(double x, double y) => p.WorldToFraction(f, x, y);
                foreach (var pack in layer.Packs.Where(x => x.Floor == f))
                {
                    if (pack.Kind == MobPackKind.Boss && !ShowBosses) continue;
                    if (pack.Kind == MobPackKind.Pack && !ShowPulls) continue;
                    if (pack.Kind == MobPackKind.Rare && !ShowRares) continue;
                    var at = Frac(pack.X, pack.Y);
                    markers.Add(new AtlasMarker(at.X, at.Y, pack.Kind == MobPackKind.Boss ? MarkerKind.Boss : pack.Kind == MobPackKind.Rare ? MarkerKind.Rare : MarkerKind.Pull, MobLabel(pack), MobTip(pack), "npc:" + pack.Lead.ToString(CultureInfo.InvariantCulture))
                    {
                        Layer = MobLayer, Number = pack.Kind == MobPackKind.Boss ? pack.Order : 0, Count = pack.Count, Warn = pack.Badge, Alone = pack.Kind != MobPackKind.Pack,
                    });
                }
                if (ShowRoute && layer.Route.Count > 1 && layer.Entrance != null && layer.Route[0].Floor == f)
                {
                    var at = Frac(layer.Route[0].X, layer.Route[0].Y);
                    markers.Add(new AtlasMarker(at.X, at.Y, MarkerKind.Entrance, "Entrance", "Where the way in puts you" + (string.IsNullOrWhiteSpace(layer.Entrance.Name) ? "" : " (" + layer.Entrance.Name.Trim() + ")") + ". The route starts here.") { Layer = MobLayer, Alone = true });
                }
                var note = new StringBuilder("Creatures where they stand in Classic (Classic baseline); this server may differ.");
                if (ShowRoute && layer.Route.Count > 1) note.Append(" The route is a suggestion worked out from the map, not a guide: doors, levers and events are not in it." + (layer.Route.Any(x => x.Passage) ? " Dashed: a way the plan has no floor for (a ramp, bridge or water)." : ""));
                if (ShowRoute && layer.RouteNote.Length > 0) note.Append(" " + layer.RouteNote);
                if (layer.BossesWithoutPlace.Count > 0 && ShowBosses) note.Append(" Not placed (they appear during the fight or an event): " + string.Join(", ", layer.BossesWithoutPlace.Where(x => x.Length > 0)) + ".");
                mobNote.Text = note.ToString();
                mobNoteHost.Visibility = Visibility.Visible;
            }
            clusters = null; hover = null; HideTip(); view?.InvalidateVisual();
        }

        static string MobLabel(MobPack p)
        {
            if (p.Kind == MobPackKind.Boss) return p.Order.ToString(CultureInfo.InvariantCulture) + ". " + p.Name;
            if (p.Count == 1 || p.Kind == MobPackKind.Rare) return p.Name;
            if (p.Members.Count == 1) return p.Count.ToString(CultureInfo.InvariantCulture) + " × " + p.Name;
            return "A pull of " + p.Count.ToString(CultureInfo.InvariantCulture);
        }

        static string Rank(MobMember m) => m.RankName.Length > 0 ? " " + m.RankName : "";

        static string MobTip(MobPack p)
        {
            var lines = new List<string>();
            var lead = p.Members.FirstOrDefault();
            if (p.Kind == MobPackKind.Boss) lines.Add("Boss" + (lead == null ? "" : " · level " + lead.Levels + Rank(lead)) + (p.EncounterIndex >= 0 ? " · encounter " + (p.EncounterIndex + 1).ToString(CultureInfo.InvariantCulture) + " in the game's list" : ""));
            else if (p.Kind == MobPackKind.Rare && lead != null) lines.Add("Rare · level " + lead.Levels + Rank(lead));
            else if (p.Members.Count == 1 && lead != null) lines.Add("Level " + lead.Levels + Rank(lead));
            foreach (var m in p.Members.Skip(p.Kind == MobPackKind.Pack && p.Members.Count > 1 ? 0 : 1))
                lines.Add((p.Kind == MobPackKind.Pack && p.Members.Count > 1 ? "" : "With ") + m.Count.ToString(CultureInfo.InvariantCulture) + " × " + m.Name + " · " + m.Levels + Rank(m));
            if (p.Patrol) lines.Add("Walks a patrol path (the dashed line)");
            foreach (var w in p.Warnings) lines.Add((w.Badge ? "! " : "• ") + w.Text);
            lines.Add("Classic baseline position · click for the creature's page");
            return string.Join("\n", lines);
        }

        static double MobRadius(AtlasMarker m) => m.Kind == MarkerKind.Boss ? 10 : m.Kind == MarkerKind.Rare ? 7 : m.Kind == MarkerKind.Entrance ? 7 : m.Count > 1 ? 8 : 6;

        // one creature marker (MapView draws the rest of the markers)
        static void DrawMobGlyph(DrawingContext dc, AtlasMarker m, Point c, double r, Pen halo, Typeface face, double dpi)
        {
            var fill = KindBrush(m.Kind);
            var ink = Res<Brush>(m.Kind == MarkerKind.Boss ? "Brush.OnDanger" : "Brush.OnAccent") ?? Brushes.Black;
            switch (m.Kind)
            {
                case MarkerKind.Rare:
                    var g = new StreamGeometry();
                    using (var ctx = g.Open()) { ctx.BeginFigure(new Point(c.X, c.Y - r - 1), true, true); ctx.PolyLineTo(new[] { new Point(c.X + r + 1, c.Y), new Point(c.X, c.Y + r + 1), new Point(c.X - r - 1, c.Y) }, true, false); }
                    g.Freeze();
                    dc.DrawGeometry(fill, halo, g);
                    break;
                case MarkerKind.Entrance:
                    dc.DrawGeometry(fill, halo, new RectangleGeometry(new Rect(c.X - r, c.Y - r, 2 * r, 2 * r + 1), r, r));
                    dc.DrawRoundedRectangle(Res<Brush>("Brush.OnAccent") ?? Brushes.Black, null, new Rect(c.X - 2, c.Y, 4, r + 1), 1.5, 1.5);
                    break;
                default:
                    dc.DrawEllipse(fill, halo, c, r, r);
                    string text = m.Kind == MarkerKind.Boss ? m.Number.ToString(CultureInfo.InvariantCulture) : m.Count > 1 ? m.Count.ToString(CultureInfo.InvariantCulture) : null;
                    if (text != null)
                    {
                        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, m.Kind == MarkerKind.Boss ? 11 : 9.5, ink, dpi);
                        dc.DrawText(ft, new Point(c.X - ft.Width / 2, c.Y - ft.Height / 2));
                    }
                    break;
            }
            if (m.Warn)
            {
                var bc = new Point(c.X + r * 0.75, c.Y - r * 0.75);
                dc.DrawEllipse(Res<Brush>("Brush.Warn") ?? Brushes.Orange, halo, bc, 4.5, 4.5);
                var ft = new FormattedText("!", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 8, Brushes.Black, dpi);
                dc.DrawText(ft, new Point(bc.X - ft.Width / 2, bc.Y - ft.Height / 2));
            }
        }

        // under the markers: the route on this floor and the patrol walks
        void DrawMobsUnder(DrawingContext dc, double dpi)
        {
            var layer = mobLayer; var p = plan;
            if (layer == null || p == null || mobPlan != p || drawnFloor < 0) return;
            int f = drawnFloor;
            Point V(double x, double y) { var fr = p.WorldToFraction(f, x, y); return FractionToViewport(fr.X, fr.Y); }
            if (ShowPulls)
            {
                var pen = new Pen(KindBrush(MarkerKind.Pull), 1.5) { DashStyle = new DashStyle(new double[] { 2, 2 }, 0), LineJoin = PenLineJoin.Round };
                pen.Freeze();
                foreach (var pack in layer.Packs.Where(x => x.Patrol && x.Floor == f && x.Path.Length >= 6))
                {
                    var g = new StreamGeometry();
                    using (var ctx = g.Open())
                    {
                        ctx.BeginFigure(V(pack.Path[0], pack.Path[1]), false, false);
                        for (int i = 3; i + 2 < pack.Path.Length; i += 3) ctx.LineTo(V(pack.Path[i], pack.Path[i + 1]), true, true);
                    }
                    g.Freeze();
                    dc.PushOpacity(0.75); dc.DrawGeometry(null, pen, g); dc.Pop();
                }
            }
            if (!ShowRoute || layer.Route.Count < 2) return;
            var accent = Res<Brush>("Brush.Accent") ?? Brushes.Orange;
            var halo = new Pen(Res<Brush>("Brush.Bg") ?? Brushes.Black, 6) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }; halo.Freeze();
            var solid = new Pen(accent, 3) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }; solid.Freeze();
            var dashed = new Pen(accent, 3) { DashStyle = new DashStyle(new double[] { 1.5, 1.5 }, 0), LineJoin = PenLineJoin.Round }; dashed.Freeze();
            var r = layer.Route;
            var solidG = new StreamGeometry(); var dashG = new StreamGeometry();
            using (var sc = solidG.Open())
            using (var dsh = dashG.Open())
            {
                for (int i = 1; i < r.Count; i++)
                {
                    if (r[i - 1].Floor != f || r[i].Floor != f) continue;
                    var ctx = r[i].Passage ? dsh : sc;
                    ctx.BeginFigure(V(r[i - 1].X, r[i - 1].Y), false, false);
                    ctx.LineTo(V(r[i].X, r[i].Y), true, true);
                }
            }
            solidG.Freeze(); dashG.Freeze();
            dc.DrawGeometry(null, halo, solidG); dc.DrawGeometry(null, halo, dashG);
            dc.DrawGeometry(null, solid, solidG); dc.DrawGeometry(null, dashed, dashG);
            // where the route leaves or joins this floor
            var tagFace = new Typeface(Res<FontFamily>("Font.Ui") ?? new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            var tagBg = Res<Brush>("Brush.Surface2") ?? Brushes.Black; var tagInk = Res<Brush>("Brush.Text.Primary") ?? Brushes.White;
            var tags = new List<Rect>();
            for (int i = 1; i < r.Count; i++)
            {
                string text = null; Point at = default(Point);
                if (r[i - 1].Floor == f && r[i].Floor != f) { text = "to " + FloorName(p, r[i].Floor); at = V(r[i - 1].X, r[i - 1].Y); }
                else if (r[i - 1].Floor != f && r[i].Floor == f) { text = "from " + FloorName(p, r[i - 1].Floor); at = V(r[i].X, r[i].Y); }
                if (text == null) continue;
                var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tagFace, 11, tagInk, dpi);
                double bw = ft.Width + 12, bh = ft.Height + 4;
                // right of the point, else left, above, below; a tag that would cover another is left out (zoom in to see it)
                var tries = new[] { new Rect(at.X + 8, at.Y - bh / 2, bw, bh), new Rect(at.X - 8 - bw, at.Y - bh / 2, bw, bh), new Rect(at.X - bw / 2, at.Y - 8 - bh, bw, bh), new Rect(at.X - bw / 2, at.Y + 8, bw, bh) };
                var box = tries.FirstOrDefault(t => !tags.Any(o => o.IntersectsWith(t)));
                if (box.IsEmpty || box.Width == 0) continue;
                tags.Add(box);
                dc.DrawRoundedRectangle(tagBg, new Pen(accent, 1), box, box.Height / 2, box.Height / 2);
                dc.DrawText(ft, new Point(box.X + 6, box.Y + 2));
            }
        }

        // the route's segments on the floor shown (for the self-test): solid and dashed
        public int[] RouteSegmentsOnFloorForTest()
        {
            var layer = mobLayer; int f = drawnFloor;
            if (layer == null || !ShowRoute) return new[] { 0, 0 };
            int s = 0, d = 0;
            for (int i = 1; i < layer.Route.Count; i++) if (layer.Route[i - 1].Floor == f && layer.Route[i].Floor == f) { if (layer.Route[i].Passage) d++; else s++; }
            return new[] { s, d };
        }
    }
}
