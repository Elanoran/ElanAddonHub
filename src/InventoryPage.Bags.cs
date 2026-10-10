using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // The in-game look: one panel per bag (header with the bag, free/total) with a grid of square slots; tabs Bags / Bank / Guild bank.
    // Icons come from IconStore (read from the player's own game files, cached on disk); until they are there a coloured tile stands in.
    public partial class InventoryPage
    {
        const double SlotSize = 38;

        string tab = "bags";
        int guildTab = -1;
        bool suppressTab;
        int renderGen;

        class IconTarget { public Image Img; public Border Fallback; }
        readonly Dictionary<int, List<IconTarget>> iconTargets = new Dictionary<int, List<IconTarget>>();
        bool lastNoLayout, lastNoIcons;
        // for the self-test
        readonly List<string> panelInfo = new List<string>();
        int slotsDrawn, slotsFilled, slotsWithImage, slotsPlaceholder;
        string lastMessage;

        static readonly Brush SlotBg = Frozen("#FF0A0B0D");
        static readonly Brush EmptyEdge = Frozen("#FF23262B");
        static readonly string[] EdgeHex = { "#FF6E6E6E", "#FF8F939B", "#FF1EFF00", "#FF0070DD", "#FFA335EE", "#FFFF8000", "#FFE6CC80", "#FF00CCFF" };

        static Brush EdgeFor(int q) => Frozen(EdgeHex[Math.Max(0, Math.Min(EdgeHex.Length - 1, q))]);

        void Tab_Checked(object sender, RoutedEventArgs e)
        {
            if (suppressTab || !IsInitialized || Body == null) return;
            tab = (sender as FrameworkElement)?.Tag as string ?? "bags";
            var only = filterKey == null ? null : Visible.FirstOrDefault(c => c.Key == filterKey);
            if (only != null && string.IsNullOrWhiteSpace(SearchBox.Text)) RenderBags(only);
        }

        InvGuild GuildFor(InvChar c)
        {
            if (c == null || string.IsNullOrEmpty(c.Guild)) return null;
            return data.Guilds.FirstOrDefault(g => string.Equals(g.Name, c.Guild, StringComparison.OrdinalIgnoreCase) && (g.Realm == null || c.Realm == null || g.Realm == c.Realm) && g.Tabs.Any(t => t.Slots.Count > 0 || t.At > 0));
        }

        void SetTabChecked(string which)
        {
            suppressTab = true;
            try { (which == "bank" ? TabBank : which == "guild" ? TabGuild : TabBags).IsChecked = true; }
            finally { suppressTab = false; }
            tab = which;
        }

        void RenderBags(InvChar c)
        {
            BagsHost.Children.Clear();
            iconTargets.Clear();
            panelInfo.Clear();
            slotsDrawn = slotsFilled = slotsWithImage = slotsPlaceholder = 0;
            lastNoLayout = lastNoIcons = false;
            BagsMessage.Visibility = Visibility.Collapsed;
            lastMessage = null;
            if (c == null) return;
            var flavor = InventoryReader.FlavorOf(c.File);
            if (flavor != null) IconStore.Flavor = flavor;
            var guild = GuildFor(c);
            TabGuild.Visibility = guild != null ? Visibility.Visible : Visibility.Collapsed;
            if (tab == "guild" && guild == null) tab = "bags";
            SetTabChecked(tab);

            if (tab == "guild") { RenderGuild(c, guild); }
            else
            {
                bool bank = tab == "bank";
                var items = bank ? c.Bank : c.Bags;
                var conts = bank ? c.BankCont : c.BagsCont;
                if (bank && items == null)
                {
                    ViewInfo.Text = "";
                    ShowMessage("The bank has not been opened with " + c.Name + " yet. Visit a banker once, then /reload or log out, and it shows up here.");
                }
                else
                {
                    if (conts == null || conts.Count == 0)
                    {
                        // saved by Elan's Bags 0.1.x: only the summed list exists, so show it as one pile
                        lastNoLayout = true;
                        var pile = new InvContainer { BagId = 99, Size = 0 };
                        int n = 1;
                        foreach (var it in (items ?? new List<InvItem>()).OrderByDescending(i => i.Quality).ThenBy(i => i.Name))
                            pile.Slots[n++] = new InvSlot { Slot = n - 1, Id = it.Id, Count = it.Count, Quality = it.Quality, Icon = it.Icon, Name = it.Name };
                        pile.Size = Math.Max(16, ((pile.Slots.Count + 3) / 4) * 4);
                        conts = new List<InvContainer> { pile };
                    }
                    int total = 0, used = 0;
                    var where = bank ? "Bank" : "Bags";
                    foreach (var ct in conts)
                    {
                        BagsHost.Children.Add(BuildPanel(ct, where, c, ct.BagId == 99));
                        total += ct.Size; used += ct.Used;
                    }
                    var at = bank ? c.BankAt : (c.BagsAt > 0 ? c.BagsAt : c.Updated);
                    ViewInfo.Text = lastNoLayout ? (items.Sum(i => i.Count) + " items · saved " + InventoryReader.Ago(at))
                        : (total - used) + " free of " + total + " slots · " + (bank ? "seen " : "saved ") + InventoryReader.Ago(at);
                    if (conts.Count > 0 && conts.SelectMany(x => x.Slots.Values).Any() && conts.SelectMany(x => x.Slots.Values).All(s => s.Icon <= 0)) lastNoIcons = true;
                }
            }
            UpdateIconNote();
            RequestIcons();
        }

        void ShowMessage(string text)
        {
            lastMessage = text;
            BagsMessage.Text = text;
            BagsMessage.Visibility = Visibility.Visible;
        }

        void RenderGuild(InvChar c, InvGuild g)
        {
            var tabs = g.Tabs.Where(t => t.Slots.Count > 0 || t.At > 0).ToList();
            if (tabs.Count == 0) { ShowMessage("No guild bank tab saved yet."); return; }
            if (!tabs.Any(t => t.Index == guildTab)) guildTab = tabs[0].Index;
            var cur = tabs.First(t => t.Index == guildTab);
            guildCols = GuildCols();
            var wrap = new StackPanel { Margin = new Thickness(0, 0, 10, 10) };
            var strip = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            foreach (var t in tabs)
            {
                var tt = t;
                var rb = new RadioButton { Style = (Style)FindResource("Segment"), Content = string.IsNullOrEmpty(t.Name) ? "Tab " + t.Index : t.Name, GroupName = "inv-gtab", IsChecked = t.Index == guildTab, Margin = new Thickness(0, 0, 6, 4) };
                rb.Checked += (s, e) => { if (guildTab != tt.Index) { guildTab = tt.Index; RenderBags(c); } };
                strip.Children.Add(rb);
            }
            wrap.Children.Add(strip);
            var pseudo = new InvContainer { BagId = 100 + cur.Index, Size = cur.Size, BagName = (string.IsNullOrEmpty(cur.Name) ? "Tab " + cur.Index : cur.Name), Slots = cur.Slots, BagIcon = cur.Icon, BagQuality = 1 };
            wrap.Children.Add(BuildPanel(pseudo, "Guild bank", c, false, GuildCols(), true));
            BagsHost.Children.Add(wrap);
            ViewInfo.Text = g.Name + " · " + tabs.Count + (g.NumTabs > tabs.Count ? " of " + g.NumTabs : "") + " tabs saved · " + InventoryReader.Money(g.Money) + " · " + InventoryReader.Ago(cur.At > 0 ? cur.At : g.Updated);
        }

        // 14 like the game when there is room, fewer in a narrow window
        int GuildCols() => BagsScroll.ActualWidth < 50 ? 14 : Math.Max(7, Math.Min(14, (int)((BagsScroll.ActualWidth - 50) / (SlotSize + 3))));

        void BagsScroll_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (tab != "guild" || BagsScroll.Visibility != Visibility.Visible || !e.WidthChanged) return;
            var only = filterKey == null ? null : Visible.FirstOrDefault(c => c.Key == filterKey);
            if (only != null && guildCols != GuildCols()) RenderBags(only);
        }
        int guildCols;

        static int ColsFor(InvContainer c) => c.BagId == -1 ? 7 : c.Size <= 16 ? 4 : c.Size <= 24 ? 6 : 8;

        FrameworkElement BuildPanel(InvContainer c, string where, InvChar owner, bool pile, int cols = 0, bool guild = false)
        {
            if (cols <= 0) cols = ColsFor(c);
            if (pile) cols = 8;
            double gridW = cols * (SlotSize + 3);
            var title = pile ? "Items" : guild ? c.BagName : c.Title;
            var panel = new Border { Background = Res("Card"), BorderBrush = Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 9, 10, 8), Margin = new Thickness(0, 0, 10, 10), VerticalAlignment = VerticalAlignment.Top };
            var stack = new StackPanel { Width = gridW };

            // header: bag icon, name, free/total
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 7), LastChildFill = true };
            var iconBox = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(4), Background = SlotBg, BorderThickness = new Thickness(1.5), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            iconBox.BorderBrush = c.BagItemId > 0 ? EdgeFor(c.BagQuality) : EmptyEdge;
            var holder = new Grid();
            var img = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(1), SnapsToDevicePixels = true };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            var fb = new Border { Visibility = Visibility.Collapsed };
            holder.Children.Add(img);
            iconBox.Child = holder;
            var bagImg = (c.BagId == 0 || c.BagId == -1 || pile) ? Application.Current.TryFindResource("Icon.Bags") as ImageSource : null;
            if (bagImg != null) img.Source = bagImg;
            else if (c.BagIcon > 0) BindIcon(c.BagIcon, img, null);
            DockPanel.SetDock(iconBox, Dock.Left);
            head.Children.Add(iconBox);
            string free;
            Brush freeBrush = Res("TextDim");
            if (pile) free = c.Used + " stacks";
            else
            {
                free = c.Free + "/" + c.Size;
                if (c.Free == 0) freeBrush = Res("Danger"); else if (c.Free <= 2) freeBrush = Res("Gold");
            }
            var freeText = new TextBlock { Text = free, FontSize = 11.5, Foreground = freeBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), ToolTip = pile ? null : "free slots / size" };
            DockPanel.SetDock(freeText, Dock.Right);
            head.Children.Add(freeText);
            head.Children.Add(new TextBlock
            {
                Text = title, FontSize = 12.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
                Foreground = c.BagItemId > 0 ? Frozen(QualityHex[Math.Max(0, Math.Min(QualityHex.Length - 1, c.BagQuality))]) : Res("Text"),
            });
            stack.Children.Add(head);

            var grid = new UniformGrid { Columns = cols, Rows = (c.Size + cols - 1) / cols, Width = gridW };
            for (int i = 1; i <= c.Size; i++)
            {
                c.Slots.TryGetValue(i, out var s);
                grid.Children.Add(MakeSlot(s, where, title, i, owner, guild));
            }
            stack.Children.Add(grid);
            panel.Child = stack;
            panelInfo.Add(title + "|" + c.Used + "|" + c.Size);
            return panel;
        }

        FrameworkElement MakeSlot(InvSlot s, string where, string bagTitle, int slotNo, InvChar owner, bool guild)
        {
            slotsDrawn++;
            var bd = new Border
            {
                Width = SlotSize, Height = SlotSize, Margin = new Thickness(1.5), CornerRadius = new CornerRadius(4), Background = SlotBg,
                BorderThickness = new Thickness(s == null ? 1 : 1.5), BorderBrush = s == null ? EmptyEdge : EdgeFor(s.Quality), SnapsToDevicePixels = true,
            };
            if (s == null) return bd;
            slotsFilled++;
            var g = new Grid();
            var img = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(1.5), SnapsToDevicePixels = true };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            var q = Frozen(QualityHex[Math.Max(0, Math.Min(QualityHex.Length - 1, s.Quality))]);
            var fb = new Border { Margin = new Thickness(2), CornerRadius = new CornerRadius(3), Background = TileFillFor(s.Quality) };
            fb.Child = new TextBlock { Text = Initial(s.Name), Foreground = q, FontSize = 16, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            g.Children.Add(fb);
            g.Children.Add(img);
            var src = s.Icon > 0 ? IconStore.TryGet(s.Icon) : null;
            if (src != null) { img.Source = src; fb.Visibility = Visibility.Collapsed; slotsWithImage++; }
            else
            {
                slotsPlaceholder++;
                if (s.Icon > 0) BindIcon(s.Icon, img, fb);
            }
            if (s.Count > 1)
            {
                var text = s.Count.ToString(CultureInfo.InvariantCulture);
                foreach (var p in Outlined(text)) g.Children.Add(p);
            }
            bd.Child = g;
            bd.ToolTip = SlotTip(s, where, bagTitle, slotNo, owner, guild);
            ToolTipService.SetInitialShowDelay(bd, 150);
            ToolTipService.SetShowDuration(bd, 30000);
            return bd;
        }

        object SlotTip(InvSlot s, string where, string bagTitle, int slotNo, InvChar owner, bool guild)
        {
            var sp = new StackPanel { MinWidth = 120 };
            sp.Children.Add(new TextBlock { Text = s.Name, FontWeight = FontWeights.SemiBold, FontSize = 13, Foreground = Frozen(QualityHex[Math.Max(0, Math.Min(QualityHex.Length - 1, s.Quality))]) });
            if (s.Count > 1) sp.Children.Add(new TextBlock { Text = "Count: " + s.Count.ToString("N0"), FontSize = 12, Margin = new Thickness(0, 3, 0, 0) });
            var loc = guild ? where + " · " + bagTitle + " · slot " + slotNo
                : (owner != null ? owner.Name + " · " : "") + where + (where == "Bags" || where == "Bank" ? " · " + bagTitle : "") + " · slot " + slotNo;
            sp.Children.Add(new TextBlock { Text = loc, FontSize = 11.5, Foreground = Res("TextDim"), Margin = new Thickness(0, 4, 0, 0) });
            sp.Children.Add(new TextBlock { Text = "Item ID " + s.Id, FontSize = 11, Foreground = Res("TextDim") });
            return new ToolTip { Style = (Style)FindResource("CardTip"), Content = sp };
        }

        // white number with a black outline, like the game's stack counts
        IEnumerable<FrameworkElement> Outlined(string text)
        {
            var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), 12.5, Brushes.White, 1.0);
            var geo = ft.BuildGeometry(new Point(0, 0));
            geo.Freeze();
            var back = new Path { Data = geo, Stroke = Brushes.Black, StrokeThickness = 3.2, StrokeLineJoin = PenLineJoin.Round, Width = ft.Width, Height = ft.Height, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 3, 0), IsHitTestVisible = false };
            var fore = new Path { Data = geo, Fill = Brushes.White, Width = ft.Width, Height = ft.Height, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 3, 0), IsHitTestVisible = false };
            yield return back;
            yield return fore;
        }

        // show the icon when it is on disk, else when it has been extracted
        void BindIcon(int id, Image img, Border fallback)
        {
            var have = IconStore.TryGet(id);
            if (have != null) { img.Source = have; if (fallback != null) fallback.Visibility = Visibility.Collapsed; return; }
            if (!iconTargets.TryGetValue(id, out var list)) iconTargets[id] = list = new List<IconTarget>();
            list.Add(new IconTarget { Img = img, Fallback = fallback });
        }

        void RequestIcons()
        {
            var gen = ++renderGen;
            foreach (var kv in iconTargets.ToList())
            {
                var targets = kv.Value;
                IconStore.Request(kv.Key, src =>
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        foreach (var t in targets)
                        {
                            t.Img.Source = src;
                            if (t.Fallback != null) t.Fallback.Visibility = Visibility.Collapsed;
                        }
                        if (gen == renderGen) { slotsPlaceholder = Math.Max(0, slotsPlaceholder - targets.Count(t => t.Fallback != null)); slotsWithImage += targets.Count(t => t.Fallback != null); }
                    }));
                });
            }
        }

        void UpdateIconNote()
        {
            string note = null;
            if (lastNoLayout) note = "Saved by an older Elan's Bags: update the addon to 0.2.0 and /reload (or log out) to see your real bags with their icons.";
            else if (lastNoIcons) note = "This data has no icon numbers yet: update Elan's Bags to 0.2.0 and /reload (or log out).";
            else if (IconStore.Problem != null) note = "Icons are not available right now (" + IconStore.Problem + "). Coloured tiles are shown instead.";
            IconNote.Text = note ?? "";
            IconNote.Visibility = note != null && BagsScroll.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        }

        void OnIconProblemChanged() => Dispatcher.BeginInvoke(new Action(UpdateIconNote));

        // ---- self-test hooks
        public void ResultsForTest(string key) { filterKey = key; userAll = key == null; Render(wowRoot()); RenderResults(); }
        public void SelectTabForTest(string which) { SetTabChecked(which); var only = filterKey == null ? null : Visible.FirstOrDefault(c => c.Key == filterKey); if (only != null) RenderBags(only); }
        public List<string> PanelsForTest() => panelInfo.ToList();
        public int SlotsDrawnForTest => slotsDrawn;
        public int SlotsFilledForTest => slotsFilled;
        public int SlotsWithImageForTest => slotsWithImage;
        public int SlotsPlaceholderForTest => slotsPlaceholder;
        public string IconNoteForTest => IconNote.Visibility == Visibility.Visible ? IconNote.Text : null;
        public string BagsMessageForTest => lastMessage;
        public bool GuildTabVisibleForTest => TabGuild.Visibility == Visibility.Visible;
        public bool TabStripVisibleForTest => TabStrip.Visibility == Visibility.Visible;
        public string TitleForTest => TitleText.Text;
        public string ViewInfoForTest => ViewInfo.Text;
        public string SelectedKeyForTest => filterKey;
        public void RefreshIconsForTest() { var only = filterKey == null ? null : Visible.FirstOrDefault(c => c.Key == filterKey); if (only != null) RenderBags(only); }
    }
}
