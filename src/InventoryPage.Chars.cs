using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // The Characters page's own tabs (Overview, Gear, Talents, Professions). Bags / Bank / Guild bank are the older views in InventoryPage.Bags.cs.
    // Data comes from the Hub modules of the character (InvChar.Hub); a missing module gets a calm empty state, a module newer than this app
    // understands says "Update Elan's Outpost to show ...". Nothing here reads or writes any file.
    public partial class InventoryPage
    {
        // what the current Overview / Gear / Talents / Professions tab shows, as plain lines (self-test)
        readonly List<string> charTabInfo = new List<string>();

        static string UpdateOutpost(string what) => "Update Elan's Outpost to show " + what + ".";

        static readonly Dictionary<int, string> SlotNames = new Dictionary<int, string>
        {
            [1] = "Head", [2] = "Neck", [3] = "Shoulders", [4] = "Shirt", [5] = "Chest", [6] = "Waist", [7] = "Legs", [8] = "Feet", [9] = "Wrists", [10] = "Hands",
            [11] = "Ring", [12] = "Ring", [13] = "Trinket", [14] = "Trinket", [15] = "Back", [16] = "Main hand", [17] = "Off hand", [18] = "Ranged", [19] = "Tabard",
        };
        // like the paper doll: head to feet down the left, rings, trinkets and weapons after
        static readonly int[] SlotOrder = { 1, 2, 3, 15, 5, 4, 19, 9, 10, 6, 7, 8, 11, 12, 13, 14, 16, 17, 18 };

        void RenderCharTab(InvChar c)
        {
            charTabInfo.Clear();
            talentNodes = talentIcons = 0;
            ViewInfo.Text = "";
            switch (tab)
            {
                case "gear": RenderGear(c); break;
                case "talents": RenderTalents(c); break;
                case "professions": RenderProfessions(c); break;
                case "pet": RenderPet(c); break;
                default: RenderOverview(c); break;
            }
        }

        StackPanel Column(double maxWidth)
        {
            var col = new StackPanel { MaxWidth = maxWidth, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 12, 12) };
            CharHost.Children.Add(col);
            return col;
        }

        Border CardBox(UIElement child, double bottom = 12)
        {
            return new Border { Background = Res("Brush.Surface1"), CornerRadius = (CornerRadius)FindResource("Radius.Card"), Padding = new Thickness(16), Margin = new Thickness(0, 0, 0, bottom), Child = child };
        }

        // one icon, one Body line, one Secondary hint (DESIGN: empty state)
        void ShowEmptyTab(string line, string hint)
        {
            var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 420 };
            sp.Children.Add(new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("Font.Icons"), FontSize = 32, Foreground = Res("Brush.Text.Tertiary"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 8) });
            sp.Children.Add(new TextBlock { Text = line, Style = TextStyle("Text.Body"), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center });
            if (!string.IsNullOrEmpty(hint))
                sp.Children.Add(new TextBlock { Text = hint, Style = TextStyle("Text.Secondary"), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) });
            var card = CardBox(sp);
            card.Padding = new Thickness(32);
            card.Margin = new Thickness(0, 0, 12, 12);
            CharHost.Children.Add(card);
            charTabInfo.Add("empty|" + line);
        }

        // false (and an empty state shown) when the module is missing or too new
        bool Gate(InvChar c, string module, string what, string missingLine, string missingHint = null, string tooNewHint = null)
        {
            var st = HubModules.StateOf(c.Hub, module);
            if (st == HubModState.Ok) return true;
            if (st == HubModState.TooNew) ShowEmptyTab(UpdateOutpost(what), tooNewHint ?? "This was saved by a newer Elan's Hub than this Outpost understands.");
            else ShowEmptyTab(missingLine, missingHint ?? "Log in to this character with Elan's Hub 1.12.0 or newer, then /reload or log out.");
            return false;
        }

        static string Fmt(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
        static string UpdatedLine(long at, string from) => "Updated " + InventoryReader.Ago(at) + (from != null ? " · " + from : "");

        FrameworkElement Portrait(InvChar c, double size)
        {
            var g = new Grid { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Top };
            g.Children.Add(new Ellipse { Fill = TintOf(c.Class) });
            g.Children.Add(new TextBlock { Text = Initial(c.Name), Foreground = ClassBrushOf(c.Class), FontSize = 20, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            var top = new Ellipse();
            g.Children.Add(top);
            var id = CharPortrait.IconFor(c.RaceFile, c.Sex, c.Class);
            if (id > 0)
            {
                var have = IconStore.TryGet(id);
                if (have != null) top.Fill = CharRow.MakeBrush(have);
                else IconStore.Request(id, src => Dispatcher.BeginInvoke(new Action(() => top.Fill = CharRow.MakeBrush(src))));
            }
            return g;
        }

        // a thin bar: done (neutral), then an optional lighter part, on a Surface3 track
        FrameworkElement Bar(double done, double extra, double of)
        {
            if (of <= 0) of = 1;
            double a = Math.Max(0, Math.Min(1, done / of)), b = Math.Max(0, Math.Min(1 - a, extra / of));
            var grid = new Grid { Height = 6 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(a, 0.0001), GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(b, 0.0001), GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - a - b, 0.0001), GridUnitType.Star) });
            var fill = new Border { Height = 6, Background = Res("Brush.Text.Secondary"), CornerRadius = new CornerRadius(3), Visibility = a > 0 ? Visibility.Visible : Visibility.Hidden };
            var rest = new Border { Background = Res("Brush.Text.Tertiary"), Visibility = b > 0 ? Visibility.Visible : Visibility.Hidden };
            Grid.SetColumn(rest, 1);
            grid.Children.Add(fill);
            grid.Children.Add(rest);
            return new Border { Background = Res("Brush.Surface3"), CornerRadius = new CornerRadius(3), Child = grid, Height = 6 };
        }

        FrameworkElement CoinLine(long copper)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new Ellipse { Width = 12, Height = 12, Fill = Res("Brush.Warn"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
            row.Children.Add(new TextBlock { Text = InventoryReader.Money(copper), Style = TextStyle("Text.Body"), Foreground = Res("Brush.Warn"), VerticalAlignment = VerticalAlignment.Center });
            return row;
        }

        FrameworkElement Tile(string label, string value)
        {
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = label, Style = TextStyle("Text.Caption") });
            sp.Children.Add(new TextBlock { Text = value, Style = TextStyle("Text.Body"), TextWrapping = TextWrapping.Wrap, MaxWidth = 220 });
            charTabInfo.Add("stat|" + label + "|" + value);
            return new Border { Background = Res("Brush.Surface2"), CornerRadius = (CornerRadius)FindResource("Radius.Control"), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 8, 8), MinWidth = 128, Child = sp };
        }

        // ---- Overview
        void RenderOverview(InvChar c)
        {
            var core = c.Core;
            var col = Column(680);
            var from = core != null ? CharacterMerge.FromHub : (c.BagsFrom ?? c.BankFrom);
            ViewInfo.Text = UpdatedLine(c.Updated, from);

            var race = !string.IsNullOrEmpty(core?.Race) ? core.Race : c.RaceFile;
            var cls = !string.IsNullOrEmpty(core?.Class) ? core.Class : Pretty(c.Class);
            var lineOne = string.Join(" ", new[] { c.Level > 0 ? "Level " + c.Level : null, race, cls }.Where(s => !string.IsNullOrEmpty(s)));
            var info = new StackPanel();
            info.Children.Add(new TextBlock { Text = lineOne.Length > 0 ? lineOne : c.Name, Style = TextStyle("Text.RowTitle"), Foreground = ClassBrushOf(c.Class), TextTrimming = TextTrimming.CharacterEllipsis });
            var where = string.Join(" · ", new[] { c.Realm, c.Faction }.Where(s => !string.IsNullOrEmpty(s)));
            if (where.Length > 0) info.Children.Add(Line(where, "Text.Secondary", null, 2));
            if (!string.IsNullOrEmpty(c.Guild)) info.Children.Add(Line("<" + c.Guild + ">", "Text.Secondary", null, 2));
            var zone = string.Join(" · ", new[] { core?.Zone, core?.Subzone }.Where(s => !string.IsNullOrEmpty(s)));
            if (zone.Length > 0) info.Children.Add(Line(zone, "Text.Caption", null, 2));
            charTabInfo.Add("identity|" + lineOne);
            charTabInfo.Add("where|" + where + "|" + (c.Guild ?? "") + "|" + zone);

            if (core != null && core.XpMax > 0 && c.Level > 0)
            {
                var pct = (int)Math.Min(100, core.Xp * 100 / core.XpMax);
                var xp = "Experience " + core.Xp.ToString("N0") + " / " + core.XpMax.ToString("N0") + " (" + pct + " %)" + (core.Rested > 0 ? " · Rested " + core.Rested.ToString("N0") : "");
                info.Children.Add(Line(xp, "Text.Caption", null, 12));
                var bar = Bar(core.Xp, core.Rested, core.XpMax);
                bar.Margin = new Thickness(0, 4, 0, 0);
                info.Children.Add(bar);
                charTabInfo.Add("xp|" + xp);
            }
            var gold = new Border { Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Child = CoinLine(c.Money) };
            info.Children.Add(gold);
            charTabInfo.Add("gold|" + InventoryReader.Money(c.Money));

            if (core == null)
            {
                var st = HubModules.StateOf(c.Hub, "core");
                var noteText = Line(st == HubModState.TooNew ? UpdateOutpost("more about this character")
                    : "Log in to this character with Elan's Hub 1.12.0 or newer to see experience, zone and more.", "Text.Caption", null, 12);
                noteText.TextWrapping = TextWrapping.Wrap;
                info.Children.Add(noteText);
                charTabInfo.Add("note|" + (st == HubModState.TooNew ? "too new" : "no core"));
            }

            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var portrait = Portrait(c, 64);
            portrait.Margin = new Thickness(0, 0, 16, 0);
            head.Children.Add(portrait);
            Grid.SetColumn(info, 1);
            head.Children.Add(info);
            col.Children.Add(CardBox(head));

            // summary counts
            var tiles = new WrapPanel();
            string bagText = "—";
            if (c.BagsCont != null && c.BagsCont.Count > 0)
            {
                int total = c.BagsCont.Where(x => x.Special == null).Sum(x => x.Size), used = c.BagsCont.Where(x => x.Special == null).Sum(x => x.Used);
                bagText = (total - used) + " free of " + total;
            }
            else if (c.Bags.Count > 0) bagText = c.Bags.Sum(i => i.Count).ToString("N0") + " items";
            tiles.Children.Add(Tile("Bag space", bagText));
            tiles.Children.Add(Tile("Bank", c.Bank == null ? "not seen yet" : c.Bank.Sum(i => i.Count).ToString("N0") + " items"));
            var gear = HubModules.GearOf(c.Hub);
            double avg = gear == null ? 0 : gear.Ilvl > 0 ? gear.Ilvl : (gear.Slots.Values.Any(s => s.Il > 0) ? gear.Slots.Values.Where(s => s.Il > 0).Average(s => s.Il) : 0);
            tiles.Children.Add(Tile("Item level", avg > 0 ? Fmt(avg) : "—"));
            var tal = HubModules.TalentsOf(c.Hub);
            int spent = tal == null ? 0 : (tal.Spent > 0 ? tal.Spent : tal.Nodes.Sum(n => n.R));
            tiles.Children.Add(Tile("Talents", tal == null ? "—" : spent + " spent" + (tal.Unspent > 0 ? " · " + tal.Unspent + " unspent" : "")));
            var profs = HubModules.ProfessionsOf(c.Hub);
            tiles.Children.Add(Tile("Professions", profs == null || profs.List.Count == 0 ? "—" : string.Join(", ", profs.List.Select(p => p.Name))));
            col.Children.Add(tiles);
        }

        // ---- Gear
        void RenderGear(InvChar c)
        {
            if (!Gate(c, "gear", "gear", "No gear saved for " + c.Name + " yet")) return;
            var g = HubModules.GearOf(c.Hub);
            double avg = g.Ilvl > 0 ? g.Ilvl : (g.Slots.Values.Any(s => s.Il > 0) ? g.Slots.Values.Where(s => s.Il > 0).Average(s => s.Il) : 0);
            ViewInfo.Text = (avg > 0 ? "Average item level " + Fmt(avg) + " · " : "") + g.Slots.Count + (g.Slots.Count == 1 ? " item worn" : " items worn") + " · " + UpdatedLine(g.At, CharacterMerge.FromHub);
            charTabInfo.Add("gearinfo|" + (avg > 0 ? Fmt(avg) : "-") + "|" + g.Slots.Count);
            if (g.Slots.Count == 0) { ShowEmptyTab(c.Name + " wears nothing.", null); return; }
            var col = Column(680);
            var order = SlotOrder.Where(g.Slots.ContainsKey).Concat(g.Slots.Keys.Where(k => !SlotOrder.Contains(k))).ToList();
            foreach (var id in order)
            {
                var it = g.Slots[id];
                var name = CharacterMerge.NameFor(it, null);
                var slotName = SlotNames.TryGetValue(id, out var sn) ? sn : "Slot " + id;
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var slot = MakeSlot(new InvSlot { Slot = id, Id = it.I, Count = 1, Quality = it.Q, Icon = it.Ic, Name = name }, "Worn", slotName, id, c, false);
                slot.Margin = new Thickness(0, 0, 12, 0);
                row.Children.Add(slot);
                var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                names.Children.Add(Line(name, "Text.RowTitle", Frozen(QualityHex[Math.Max(0, Math.Min(QualityHex.Length - 1, it.Q))])));
                ((TextBlock)names.Children[0]).TextTrimming = TextTrimming.CharacterEllipsis;
                names.Children.Add(Line(slotName, "Text.Caption"));
                Grid.SetColumn(names, 1);
                row.Children.Add(names);
                if (it.Il > 0)
                {
                    var il = Line("ilvl " + it.Il, "Text.Secondary");
                    il.VerticalAlignment = VerticalAlignment.Center;
                    il.Margin = new Thickness(12, 0, 0, 0);
                    Grid.SetColumn(il, 2);
                    row.Children.Add(il);
                }
                var box = new Border { Background = Res("Brush.Surface1"), CornerRadius = (CornerRadius)FindResource("Radius.Card"), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 4), Child = row };
                col.Children.Add(box);
                charTabInfo.Add("slot|" + id + "|" + slotName + "|" + name + "|" + it.Il);
            }
        }

        // ---- Professions
        void RenderProfessions(InvChar c)
        {
            if (!Gate(c, "professions", "professions", "No professions saved for " + c.Name + " yet")) return;
            var m = HubModules.ProfessionsOf(c.Hub);
            ViewInfo.Text = m.List.Count + (m.List.Count == 1 ? " skill" : " skills") + " · " + UpdatedLine(m.At, CharacterMerge.FromHub);
            if (m.List.Count == 0) { ShowEmptyTab(c.Name + " has no professions.", null); return; }
            var col = Column(680);
            void Group(string title, IEnumerable<HubProfession> list)
            {
                var items = list.ToList();
                if (items.Count == 0) return;
                var gh = Line(title, "Text.Caption", null, 0);
                gh.Margin = new Thickness(4, col.Children.Count == 0 ? 0 : 12, 0, 8);
                col.Children.Add(gh);
                foreach (var p in items)
                {
                    var row = new Grid();
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    var icon = new Grid { Width = 32, Height = 32, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
                    var fb = new Border { CornerRadius = (CornerRadius)FindResource("Radius.Control"), Background = Res("Brush.Overlay.Chip"), Child = new TextBlock { Text = Initial(p.Name), Foreground = Res("Brush.Text.Secondary"), FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
                    var img = new Image { Stretch = Stretch.Uniform };
                    RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                    icon.Children.Add(fb);
                    icon.Children.Add(img);
                    if (p.Ic > 0) BindIcon(p.Ic, img, fb);
                    row.Children.Add(icon);
                    var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                    var top = new DockPanel();
                    var num = new TextBlock { Text = p.Skill + " / " + p.Max, Style = TextStyle("Text.Body"), Margin = new Thickness(12, 0, 0, 0) };
                    DockPanel.SetDock(num, Dock.Right);
                    top.Children.Add(num);
                    top.Children.Add(new TextBlock { Text = p.Name, Style = TextStyle("Text.RowTitle"), TextTrimming = TextTrimming.CharacterEllipsis });
                    right.Children.Add(top);
                    var bar = Bar(p.Skill, 0, p.Max);
                    bar.Margin = new Thickness(0, 8, 0, 0);
                    right.Children.Add(bar);
                    Grid.SetColumn(right, 1);
                    row.Children.Add(right);
                    col.Children.Add(new Border { Background = Res("Brush.Surface1"), CornerRadius = (CornerRadius)FindResource("Radius.Card"), Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(0, 0, 0, 4), Child = row });
                    charTabInfo.Add("prof|" + p.Name + "|" + p.Skill + "|" + p.Max + "|" + p.Kind);
                }
            }
            Group("Professions", m.List.Where(p => p.Kind != "secondary"));
            Group("Secondary skills", m.List.Where(p => p.Kind == "secondary"));
        }

        // ---- Talents
        void RenderTalents(InvChar c)
        {
            if (!Gate(c, "talents", "talents", "No talents saved for " + c.Name + " yet")) return;
            var m = HubModules.TalentsOf(c.Hub);
            int spent = m.Spent > 0 ? m.Spent : m.Nodes.Sum(n => n.R);
            ViewInfo.Text = spent + " points spent" + (m.Unspent > 0 ? " · " + m.Unspent + " unspent" : "") + " · " + UpdatedLine(m.At, CharacterMerge.FromHub);
            if (m.Nodes.Count == 0) { ShowEmptyTab(c.Name + " has no talents to show yet.", null); return; }
            var layout = TalentGrid.Group(m.Nodes);
            charTabInfo.Add("talentinfo|" + spent + "|" + m.Unspent + "|" + layout.Trees.Count + "|" + layout.Loose.Count);
            var wrap = new WrapPanel { Margin = new Thickness(0, 0, 0, 0) };
            foreach (var tree in layout.Trees)
            {
                var sp = new StackPanel();
                var title = "Tree " + tree.Index + " · " + tree.Points + (tree.Points == 1 ? " point" : " points");
                sp.Children.Add(new TextBlock { Text = title, Style = TextStyle("Text.Caption"), Margin = new Thickness(0, 0, 0, 8) });
                var grid = new Grid();
                for (int i = 0; i < tree.Cols; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                for (int i = 0; i < tree.Rows; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto, MinHeight = SlotSize + 3 });
                foreach (var cell in tree.Cells)
                {
                    var stack = new StackPanel { Orientation = Orientation.Horizontal };
                    foreach (var n in cell.Nodes) stack.Children.Add(TalentNodeBox(n));
                    Grid.SetColumn(stack, cell.Col);
                    Grid.SetRow(stack, cell.Row);
                    grid.Children.Add(stack);
                }
                sp.Children.Add(grid);
                var card = CardBox(sp);
                card.Margin = new Thickness(0, 0, 12, 12);
                card.VerticalAlignment = VerticalAlignment.Top;
                wrap.Children.Add(card);
                charTabInfo.Add("tree|" + tree.Index + "|" + tree.Cols + "x" + tree.Rows + "|" + tree.Points + "|" + tree.Cells.Sum(x => x.Nodes.Count));
            }
            if (layout.Loose.Count > 0)
            {
                var sp = new StackPanel { MaxWidth = 420 };
                sp.Children.Add(new TextBlock { Text = "Other talents", Style = TextStyle("Text.Caption"), Margin = new Thickness(0, 0, 0, 8) });
                var w = new WrapPanel();
                foreach (var n in layout.Loose) w.Children.Add(TalentNodeBox(n));
                sp.Children.Add(w);
                var card = CardBox(sp);
                card.Margin = new Thickness(0, 0, 12, 12);
                card.VerticalAlignment = VerticalAlignment.Top;
                wrap.Children.Add(card);
                charTabInfo.Add("loose|" + layout.Loose.Count);
            }
            CharHost.Children.Add(wrap);
        }

        // ---- Pet (hunters only; Hunter Helper hands the data to Elan's Hub as hunter.pets)
        Border Chip(string text)
        {
            return new Border { Background = Res("Brush.Overlay.Chip"), CornerRadius = (CornerRadius)FindResource("Radius.Chip"), Height = 24, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(0, 0, 8, 8),
                Child = new TextBlock { Text = text, Style = TextStyle("Text.Secondary"), VerticalAlignment = VerticalAlignment.Center } };
        }

        static string AbilityText(HubPetAbility a) => a.Rank > 0 ? a.Name + " " + a.Rank : a.Name;
        static string LoyaltyOf(HubPet p) => p.Loyalty > 0 ? HubModules.LoyaltyNames[p.Loyalty - 1] : p.LoyaltyText;

        // one pet: family icon (or the family's first letter), name, level / family / loyalty, and its abilities with ranks
        FrameworkElement PetCard(HubPet p, bool full)
        {
            double size = full ? 48 : 32;
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var icon = new Grid { Width = size, Height = size, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top };
            var fb = new Border { CornerRadius = (CornerRadius)FindResource(full ? "Radius.ControlLg" : "Radius.Control"), Background = Res("Brush.Overlay.Chip"),
                Child = new TextBlock { Text = Initial(p.Family ?? p.FamilyId ?? p.Name), Foreground = Res("Brush.Text.Secondary"), FontSize = full ? 20 : 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            var img = new Image { Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            icon.Children.Add(fb);
            icon.Children.Add(img);
            if (p.Ic > 0) BindIcon(p.Ic, img, fb);
            row.Children.Add(icon);

            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock { Text = p.Name, Style = TextStyle(full ? "Text.Section" : "Text.RowTitle"), TextTrimming = TextTrimming.CharacterEllipsis });
            var bits = new List<string>();
            if (p.Level > 0) bits.Add("Level " + p.Level);
            if (!string.IsNullOrEmpty(p.Family)) bits.Add(p.Family);
            var loy = LoyaltyOf(p);
            if (!string.IsNullOrEmpty(loy)) bits.Add(loy);
            if (p.Happiness > 0) bits.Add(p.Happiness == 1 ? "Unhappy" : p.Happiness == 2 ? "Content" : "Happy");
            if (p.InStable) bits.Add("Stable slot " + p.Stable);
            var sub = string.Join(" · ", bits);
            info.Children.Add(Line(sub, "Text.Secondary", null, 2));
            Grid.SetColumn(info, 1);
            row.Children.Add(info);

            string tp = null;
            if (full && p.TpTotal > 0)
            {
                tp = "Training points " + p.TpSpent + " / " + p.TpTotal;
                var r = Line(tp, "Text.Caption");
                r.VerticalAlignment = VerticalAlignment.Top;
                r.Margin = new Thickness(12, 4, 0, 0);
                Grid.SetColumn(r, 2);
                row.Children.Add(r);
            }

            var body = new StackPanel();
            body.Children.Add(row);
            var abil = string.Join(", ", p.Abilities.Select(AbilityText));
            if (full)
            {
                if (p.Abilities.Count > 0)
                {
                    var wrap = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
                    foreach (var a in p.Abilities) wrap.Children.Add(Chip(AbilityText(a)));
                    body.Children.Add(wrap);
                }
                else body.Children.Add(Line("No abilities saved for this pet yet.", "Text.Caption", null, 12));
            }
            else if (p.Abilities.Count > 0)
            {
                var t = Line(string.Join(" · ", p.Abilities.Select(AbilityText)), "Text.Caption", null, 8);
                t.TextWrapping = TextWrapping.Wrap;
                body.Children.Add(t);
            }
            charTabInfo.Add("pet|" + (p.InStable ? "stable " + p.Stable : "with you") + "|" + p.Name + "|" + p.Family + "|" + p.Level + "|" + loy + "|" + (tp ?? "") + "|" + abil);
            var card = full ? CardBox(body) : new Border { Background = Res("Brush.Surface1"), CornerRadius = (CornerRadius)FindResource("Radius.Card"), Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(0, 0, 0, 4), Child = body };
            return card;
        }

        void RenderPet(InvChar c)
        {
            if (!Gate(c, "hunter.pets", "pets", "No pets saved for " + c.Name + " yet", "Log in to this hunter with Hunter Helper 1.26.5 or newer, then /reload or log out.",
                "This was saved by a newer Hunter Helper than this Outpost understands.")) return;
            var m = HubModules.PetsOf(c.Hub);
            var withYou = m.WithYou.ToList();
            var stabled = m.Stabled.ToList();
            ViewInfo.Text = m.Pets.Count + (m.Pets.Count == 1 ? " pet" : " pets") + " · " + UpdatedLine(m.At, "Hunter Helper");
            charTabInfo.Add("petinfo|" + withYou.Count + "|" + stabled.Count + "|" + m.Known.Count);
            if (m.Pets.Count == 0 && m.Known.Count == 0) { ShowEmptyTab(c.Name + " has no pets saved yet.", "Call a pet or visit a stable master with Hunter Helper, then /reload or log out."); return; }
            var col = Column(680);
            foreach (var p in withYou) col.Children.Add(PetCard(p, true));
            void Header(string title)
            {
                var gh = Line(title, "Text.Caption", null, 0);
                gh.Margin = new Thickness(4, col.Children.Count == 0 ? 0 : 12, 0, 8);
                col.Children.Add(gh);
            }
            if (stabled.Count > 0)
            {
                Header("Stabled");
                foreach (var p in stabled) col.Children.Add(PetCard(p, false));
            }
            if (m.Known.Count > 0)
            {
                Header("Your hunter can teach");
                var wrap = new WrapPanel();
                var known = m.Known.OrderBy(k => HubModules.PetAbilityName(k.Key), StringComparer.CurrentCultureIgnoreCase).ToList();
                foreach (var k in known) wrap.Children.Add(Chip(HubModules.PetAbilityName(k.Key) + " " + k.Value));
                var card = CardBox(wrap, 4);
                card.Padding = new Thickness(16, 16, 16, 8);
                col.Children.Add(card);
                charTabInfo.Add("known|" + string.Join(", ", known.Select(k => HubModules.PetAbilityName(k.Key) + " " + k.Value)));
            }
        }

        // ---- self-test hooks
        public bool PetTabVisibleForTest => TabPet.Visibility == Visibility.Visible;
        public List<string> CharTabLinesForTest() => charTabInfo.ToList();
        public string TabForTest => tab;
        // as on the very first look: the newest character, Overview tab, no search
        public void ResetViewForTest() { filterKey = null; userAll = false; tab = "overview"; SearchBox.Text = ""; }
        public void SelectCharTabForTest(string which) => SelectTabForTest(which);
        public bool CharHostVisibleForTest => CharHost.Visibility == Visibility.Visible;
        public int CharHostChildrenForTest => CharHost.Children.Count;
    }
}
