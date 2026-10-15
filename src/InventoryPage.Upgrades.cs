using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Maps;
using ElansAddonHub.Services.Upgrades;

namespace ElansAddonHub
{
    // Characters > Gear > Upgrades: for each slot of the paper doll the best few items this character can use, with the places to get them. The engine
    // (Services/Upgrades) does the work off the UI thread and caches it; this file only draws its answer and keeps the filters. Nothing here writes a file
    // except the person's own filter choice and weight changes, which go to the Outpost's settings.
    public partial class InventoryPage
    {
        UpgradeFilters upFilters;
        Func<string> upGet = () => null;
        Action<string> upStore = v => { };
        InvChar upChar;
        UpgradeChar upWho;
        UpgradeSet upSet;
        Border upCard;
        StackPanel upBody, upWeightsHost;
        int upGen, upFocus;
        bool upWeightsOpen, upReady, upSuppress;
        readonly List<string> upLines = new List<string>();
        readonly Dictionary<string, FrameworkElement> upCtl = new Dictionary<string, FrameworkElement>();
        readonly Dictionary<int, FrameworkElement> upBlocks = new Dictionary<int, FrameworkElement>();
        readonly Dictionary<int, Brush> upEdge = new Dictionary<int, Brush>();
        readonly Dictionary<string, ToolTip> upTips = new Dictionary<string, ToolTip>();
        readonly Dictionary<string, Border> upRows = new Dictionary<string, Border>();
        const int StaleDays = 7;

        public void UseUpgradeSettings(Func<string> get, Action<string> store)
        {
            upGet = get ?? (() => null); upStore = store ?? (v => { });
            upFilters = null;
        }

        UpgradeFilters Filters => upFilters ?? (upFilters = UpgradeFilters.Parse(upGet()));
        void SaveFilters() { try { upStore(Filters.Serialize()); } catch (Exception) { } }

        // ---- the card under the paper doll
        FrameworkElement BuildUpgradesCard(InvChar c)
        {
            upChar = c; upWho = UpgradeChar.From(c); upSet = null; upReady = false; upFocus = 0; upWeightsOpen = false;
            upCtl.Clear(); upBlocks.Clear(); upLines.Clear(); upEdge.Clear(); upTips.Clear(); upRows.Clear();
            var sp = new StackPanel();
            var head = new DockPanel();
            var weightsBtn = new Button { Content = "Weights", Style = (Style)FindResource("GhostButton"), Height = 28, ToolTip = "How much each stat is worth to " + c.Name, VerticalAlignment = VerticalAlignment.Center };
            System.Windows.Automation.AutomationProperties.SetName(weightsBtn, "Weights");
            weightsBtn.Click += (s, e) => ToggleWeights();
            DockPanel.SetDock(weightsBtn, Dock.Right);
            head.Children.Add(weightsBtn);
            var titles = new StackPanel();
            titles.Children.Add(new TextBlock { Text = "Upgrades", Style = TextStyle("Text.Section") });
            var sub = new TextBlock { Style = TextStyle("Text.Secondary"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0), MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left };
            sub.Text = "Where to find gear " + c.Name + " can use, compared with what is worn now.";
            titles.Children.Add(sub);
            upCtl["sub"] = sub;
            head.Children.Add(titles);
            sp.Children.Add(head);

            var bar = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            CheckBox Sw(string key, string text, string tip, bool on, Action<bool> set)
            {
                var cell = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 8), VerticalAlignment = VerticalAlignment.Center, ToolTip = tip };
                var cb = new CheckBox { Style = (Style)FindResource("Switch"), VerticalAlignment = VerticalAlignment.Center, IsChecked = on };
                System.Windows.Automation.AutomationProperties.SetName(cb, text);
                cb.Click += (s, e) => { if (upSuppress) return; set(cb.IsChecked == true); SaveFilters(); RenderUpgrades(); };
                cell.Children.Add(cb);
                cell.Children.Add(new TextBlock { Text = "  " + text, Style = TextStyle("Text.Secondary"), VerticalAlignment = VerticalAlignment.Center });
                bar.Children.Add(cell);
                upCtl[key] = cb;
                return cb;
            }
            var f = Filters;
            Sw("dungeons", "Dungeons", "Include items that drop in dungeons", f.Dungeons, v => Filters.Dungeons = v);
            Sw("raids", "Raids", "Include items that drop in raids", f.Raids, v => Filters.Raids = v);
            Sw("classic", "Classic baseline", "Include drops, vendors and quests that only the Classic baseline knows", f.Classic, v => Filters.Classic = v);
            Sw("tradeable", "Only tradeable", "Only items you could also buy or be given: not bound when picked up", f.TradeableOnly, v => Filters.TradeableOnly = v);
            Sw("soon", "Coming soon", "Also show items up to 3 levels above " + c.Name + "'s level, apart", f.LevelOffset > 0, v => Filters.LevelOffset = v ? 3 : 0);
            Sw("enchants", "Enchants", "Suggest enchants, armor kits, scopes and the like for the worn items", f.Enchants, v => Filters.Enchants = v);
            Sw("applicable", "Only enchants I can apply", "Only enchants that one of your characters or a member of your Lodge can make, or kits you already own", f.OnlyApplicable, v => Filters.OnlyApplicable = v);
            Sw("food", "Consumables", "Show the best food, drink, elixirs, potions, scrolls, bandages and ammo for this level", f.Food, v => Filters.Food = v);
            sp.Children.Add(bar);

            upWeightsHost = new StackPanel { Visibility = Visibility.Collapsed };
            sp.Children.Add(upWeightsHost);
            upBody = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            sp.Children.Add(upBody);
            upBody.Children.Add(new TextBlock { Text = "Looking for upgrades...", Style = TextStyle("Text.Secondary"), Margin = new Thickness(0, 4, 0, 0) });

            upCard = CardBox(sp);
            upCard.HorizontalAlignment = HorizontalAlignment.Left;
            upCard.MaxWidth = 960;
            upCard.Margin = new Thickness(0, 0, 12, 12);
            StartUpgrades();
            return upCard;
        }

        // the heavy part runs on a worker; a newer ask (another character, new weights) drops an older answer
        void StartUpgrades()
        {
            var gen = ++upGen;
            var who = upWho;
            upReady = false;
            Task.Run(() =>
            {
                try { return UpgradeEngine.Compute(who); }
                catch (Exception e) { Util.Log("upgrades: " + e); return new UpgradeSet { Who = who, Problem = "error" }; }
            }).ContinueWith(t => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (gen != upGen) return;
                upSet = t.Result; upReady = true;
                RenderUpgrades();
            })), TaskScheduler.Default);
        }

        Brush UpQuality(int q) => Frozen(QualityHex[Math.Max(0, Math.Min(QualityHex.Length - 1, q))]);

        TextBlock UpNote(string text, double top = 8)
        {
            var t = new TextBlock { Text = text, Style = TextStyle("Text.Secondary"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 0), MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left };
            return t;
        }

        void RenderUpgrades()
        {
            if (upBody == null) return;
            upBody.Children.Clear(); upBlocks.Clear(); upTips.Clear(); upRows.Clear(); upLines.Clear();
            var set = upSet;
            if (set == null) { upBody.Children.Add(UpNote("Looking for upgrades...", 4)); return; }
            var cls = UpgradeChar.Pretty(upWho.ClassFile);
            var sub = upCtl.TryGetValue("sub", out var sb) ? sb as TextBlock : null;
            if (sub != null) sub.Text = "Where to find gear " + upWho.Name + " can use, compared with what is worn now" + (set.SpecName.Length > 0 ? " · " + cls + ", " + set.SpecName + (upWho.Spec == 0 ? " weights" : " weights (from the talents)") : "") + ".";
            if (set.Problem.Length > 0)
            {
                string msg = set.Problem == "items"
                    ? (ItemData.Status == ItemData.State.Disabled ? "Game data is switched off (Settings > General), so there are no items to compare." : "Your game's item data is not ready yet. Upgrades appear when it has been read.")
                    : set.Problem == "class" ? "Upgrades need this character's class and level, which Elan's Hub saves when you log in." : "Upgrades could not be worked out. Try again after the next save.";
                upBody.Children.Add(UpNote(msg, 4));
                upLines.Add("upnote|problem|" + set.Problem);
                return;
            }

            var picks = UpgradeEngine.Pick(set, Filters);
            int shown = 0, hidden = 0;
            upLines.Add("upinfo|" + cls + "|" + set.SpecName + "|" + (upWho.Spec == 0 ? "default" : "talents") + "|" + set.Considered);
            foreach (var p in picks)
            {
                hidden += p.Hidden;
                bool focus = upFocus == p.Slot;
                if (p.Upgrades.Count == 0 && p.Soon.Count == 0 && p.Enchants.Count == 0 && !focus) continue;
                var block = UpgradeBlock(p, focus);
                upBlocks[p.Slot] = block;
                upBody.Children.Add(block);
                shown += p.Upgrades.Count + p.Soon.Count + p.Enchants.Count;
            }
            if (shown == 0)
            {
                upBody.Children.Add(UpNote(hidden > 0 ? "Nothing better turned up with these filters. " + hidden + " better " + (hidden == 1 ? "item is" : "items are") + " hidden by them." : "Nothing better turned up for " + upWho.Name + ": everything found is no better than what is worn.", 4));
                upLines.Add("upnote|none|" + hidden);
            }
            else if (hidden > 0) { upBody.Children.Add(UpNote(hidden + " more " + (hidden == 1 ? "upgrade is" : "upgrades are") + " hidden by the filters.", 12)); upLines.Add("upnote|hidden|" + hidden); }

            ConsumablesSection(set);

            // calm notes about the data behind the list
            var notes = new List<string>();
            if (!set.ClassicData && Filters.Classic) notes.Add("Download the Classic baseline for drop and vendor sources (Atlas > start page): until then only what your Lodge has seen is listed.");
            else if (!set.LodgeData && !set.ClassicData) notes.Add("No drop or vendor data yet: join a Lodge or download the Classic baseline.");
            var gear = upChar?.Hub != null ? HubModules.GearOf(upChar.Hub) : null;
            if (gear != null && gear.At > 0)
            {
                var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(gear.At);
                if (age.TotalDays >= StaleDays) notes.Add("Your Elan's Hub data for " + upWho.Name + " is " + (int)age.TotalDays + " days old (older than " + StaleDays + " days): log in with this character to compare against what is worn now.");
            }
            if (Filters.Enchants && set.EnchantProblem == "unavailable") notes.Add("Enchants need the enchant tables of your game files, which could not be read" + (EnchantData.Reason != null ? " (" + EnchantData.Reason + ")" : "") + ".");
            else if (Filters.Enchants && set.EnchantProblem == "off") notes.Add("Game data is switched off (Settings > General), so no enchants are suggested.");
            if (set.WithoutSource > 0) notes.Add(set.WithoutSource + " better " + (set.WithoutSource == 1 ? "item has" : "items have") + " no known source yet and " + (set.WithoutSource == 1 ? "is" : "are") + " left out.");
            foreach (var n in notes) { upBody.Children.Add(UpNote(n, upBody.Children.Count == 0 ? 4 : 8)); upLines.Add("upnote|data|" + n); }
            if (set.Notes.Count > 0) foreach (var n in set.Notes) upBody.Children.Add(UpNote(n));
            RefreshWeights();
        }

        // ---- one slot: a header with what is worn, then the top few
        FrameworkElement UpgradeBlock(UpgradeSlotResult p, bool focus)
        {
            var outer = new Border { Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(8), CornerRadius = (CornerRadius)FindResource("Radius.Control"), BorderThickness = new Thickness(focus ? 1 : 0), Background = focus ? Res("Brush.Surface2") : Brushes.Transparent, BorderBrush = Res("Brush.Accent") };
            var sp = new StackPanel();
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(new TextBlock { Text = p.SlotName, Style = TextStyle("Text.RowTitle") });
            string wornName = p.WornId > 0 ? WornNameOf(p) : null;
            head.Children.Add(new TextBlock { Text = p.WornId > 0 ? "  ·  wearing " + wornName : "  ·  empty", Style = TextStyle("Text.Caption"), Foreground = Res("Brush.Text.Tertiary"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            sp.Children.Add(head);
            upLines.Add("upslot|" + p.Slot + "|" + p.SlotName + "|" + (wornName ?? "") + "|" + p.Upgrades.Count + "|" + p.Soon.Count + (focus ? "|focus" : ""));
            if (!string.IsNullOrEmpty(p.Note)) sp.Children.Add(UpNote(p.Note, 4));
            else if (p.Upgrades.Count == 0 && p.Soon.Count == 0 && p.Enchants.Count == 0) sp.Children.Add(UpNote("Nothing better found for " + p.SlotName.ToLowerInvariant() + " with these filters.", 4));
            if (p.EnchantCurrent.Length > 0)
            {
                sp.Children.Add(new TextBlock { Text = p.EnchantCurrent, Style = TextStyle("Text.Caption"), Foreground = p.NoEnchant ? Res("Brush.Warn") : Res("Brush.Text.Tertiary"), Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap });
                upLines.Add("upench|" + p.Slot + "|" + p.EnchantCurrent);
            }
            foreach (var c in p.Upgrades) sp.Children.Add(UpgradeRow(p, c, false));
            foreach (var en in p.Enchants) sp.Children.Add(ExtraRow(en, "e" + p.Slot + ":" + (en.ItemId > 0 ? en.ItemId : en.SpellId), "upe|" + p.Slot));
            if (p.Soon.Count > 0)
            {
                sp.Children.Add(new TextBlock { Text = "Coming soon", Style = TextStyle("Text.Caption"), Foreground = Res("Brush.Text.Tertiary"), Margin = new Thickness(8, 8, 0, 0) });
                foreach (var c in p.Soon) sp.Children.Add(UpgradeRow(p, c, true));
            }
            outer.Child = sp;
            return outer;
        }

        string WornNameOf(UpgradeSlotResult p)
        {
            HubItem hi = null;
            if (upChar?.Hub != null && HubModules.StateOf(upChar.Hub, "gear") == HubModState.Ok) HubModules.GearOf(upChar.Hub).Slots.TryGetValue(p.Slot, out hi);
            return (hi != null ? CharacterMerge.NameFor(hi, null) : null) ?? ItemData.Get(p.WornId)?.Name ?? "Item " + p.WornId;
        }

        FrameworkElement UpgradeRow(UpgradeSlotResult p, UpgradeCandidate c, bool soon)
        {
            var it = c.Item;
            var bd = new Border { Padding = new Thickness(8), CornerRadius = (CornerRadius)FindResource("Radius.Control"), Background = Brushes.Transparent, Cursor = Cursors.Hand, Margin = new Thickness(0, 4, 0, 0) };
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // the icon: the game's picture when it can be read, the first letter in the quality colour until then
            var iconBox = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(8), Background = SlotBg, BorderThickness = new Thickness(1), BorderBrush = EdgeFor(it.Quality), VerticalAlignment = VerticalAlignment.Top };
            var ig = new Grid();
            var fb = new Border { Margin = new Thickness(4), Child = new TextBlock { Text = Initial(it.Name), Foreground = UpQuality(it.Quality), FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            var img = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(2), SnapsToDevicePixels = true };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            ig.Children.Add(fb); ig.Children.Add(img);
            iconBox.Child = ig;
            if (it.IconId > 0)
            {
                var have = IconStore.TryGet(it.IconId);
                if (have != null) { img.Source = have; fb.Visibility = Visibility.Collapsed; }
                else IconStore.Request(it.IconId, src => Dispatcher.BeginInvoke(new Action(() => { img.Source = src; fb.Visibility = Visibility.Collapsed; })));
            }
            g.Children.Add(iconBox);

            var main = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
            var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
            nameRow.Children.Add(new TextBlock { Text = it.Name, Style = TextStyle("Text.RowTitle"), Foreground = UpQuality(it.Quality), TextTrimming = TextTrimming.CharacterEllipsis });
            if (soon) nameRow.Children.Add(new TextBlock { Text = "  at level " + c.NeedsLevel, Style = TextStyle("Text.Caption"), Foreground = Res("Brush.Warn"), VerticalAlignment = VerticalAlignment.Center });
            else if (c.Revealed) nameRow.Children.Add(new TextBlock { Text = "  seen by your Lodge", Style = TextStyle("Text.Caption"), Foreground = Res("Brush.Text.Tertiary"), VerticalAlignment = VerticalAlignment.Center });
            main.Children.Add(nameRow);
            main.Children.Add(new TextBlock { Text = c.Delta, Style = TextStyle("Text.Secondary"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
            if (!string.IsNullOrEmpty(c.EnchantNote)) main.Children.Add(new TextBlock { Text = c.EnchantNote, Style = TextStyle("Text.Caption"), Foreground = Res("Brush.Text.Tertiary"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
            var best = c.Sources[0];
            var srcRow = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
            if (best.ZoneMap > 0 && OpenAtlas != null && MapLinkFor(best) != null)
            {
                var map = new Button { Content = "Show on map", Style = (Style)FindResource("GhostButton"), Height = 28, Margin = new Thickness(8, 0, 0, 0), ToolTip = "Open the world map at " + (string.IsNullOrEmpty(best.Zone) ? "this place" : best.Zone) };
                System.Windows.Automation.AutomationProperties.SetName(map, "Show on map");
                var link = MapLinkFor(best);
                map.Click += (s, e) => OpenAtlas?.Invoke(link);
                DockPanel.SetDock(map, Dock.Right);
                srcRow.Children.Add(map);
            }
            srcRow.Children.Add(new TextBlock { Text = best.Line + (c.Sources.Count > 1 ? " · +" + (c.Sources.Count - 1) + " more" : ""), Style = TextStyle("Text.Caption"), Foreground = Res("Brush.Text.Tertiary"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
            main.Children.Add(srcRow);
            Grid.SetColumn(main, 1);
            g.Children.Add(main);

            var pct = new TextBlock { Text = UpgradeScore.PctText(c.Pct), Style = TextStyle("Text.RowTitle"), Foreground = Res("Brush.Success"), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Right };
            Grid.SetColumn(pct, 2);
            g.Children.Add(pct);
            bd.Child = g;
            bd.MouseEnter += (s, e) => bd.Background = Res("Brush.Overlay.Hover");
            bd.MouseLeave += (s, e) => bd.Background = Brushes.Transparent;
            bd.MouseLeftButtonUp += (s, e) => { if (!e.Handled && OpenAtlas != null) { e.Handled = true; OpenAtlas(AtlasIds.Item(it.Id)); } };
            var tip = UpgradeTip(p, c);
            bd.ToolTip = tip;
            upTips[p.Slot + ":" + it.Id] = tip; upRows[p.Slot + ":" + it.Id] = bd;
            ToolTipService.SetInitialShowDelay(bd, 150);
            ToolTipService.SetShowDuration(bd, 30000);
            upLines.Add("up|" + p.Slot + "|" + it.Id + "|" + it.Name + "|" + UpgradeScore.PctText(c.Pct) + "|" + c.Delta + "|" + best.Line + (soon ? "|soon" : ""));
            return bd;
        }

        // the world map at the source's zone (when the world map knows the zone)
        string MapLinkFor(UpgradeSource s)
        {
            try
            {
                var idx = ElansAddonHub.Services.Maps.MapData.Index;
                if (idx == null || s.ZoneMap <= 0) return null;
                var markers = new List<AtlasMarker>();
                if (s.Fx >= 0 && s.Fy >= 0) markers.Add(new AtlasMarker(s.Fx, s.Fy, s.Kind == "vendor" ? MarkerKind.Vendor : MarkerKind.Spawn, s.What, s.Line, s.Link));
                return ElansAddonHub.Services.WorldMap.WorldMapIds.ShowOnMapId(idx, s.ZoneMap, markers);
            }
            catch (Exception) { return null; }
        }

        // the game's tooltip, then what changes against the worn item (green better, red worse), then every place to get it
        ToolTip UpgradeTip(UpgradeSlotResult p, UpgradeCandidate c)
        {
            var it = c.Item;
            var gt = new GameTooltip(it.Name, it.Quality, null, upWho?.Level ?? 0);
            gt.AddItem(it);
            var foot = gt.Footer;
            var w = UpgradeWeights.Effective(upWho.ClassFile, upWho.Spec);
            var worn = p.WornId > 0 ? (ItemData.Get(p.WornId) ?? UpgradeEngine.FromRevealed(AtlasQuery.LodgeOrNull()?.Revealed(p.WornId))) : null;
            foot.Children.Add(GameTooltip.Muted(worn != null ? "Compared with " + worn.Name + " (worn)" : p.WornId > 0 ? "The worn item is not known to your game data" : "Nothing is worn in this slot"));
            foreach (var d in UpgradeScore.Diff(it, worn, p.Slot, w, 8))
                foot.Children.Add(new TextBlock { Text = d.Text, Foreground = GameTooltip.Brush(d.Delta > 0 ? "Brush.GameTip.Green" : "Brush.GameTip.Red"), FontSize = 13, Margin = new Thickness(0, 2, 0, 0) });
            foot.Children.Add(GameTooltip.Muted("Score " + UpgradeScore.PctText(c.Pct) + (c.ReplacesOffhand ? " · replaces your off hand" : ""), 4));
            foot.Children.Add(GameTooltip.Muted("Where to get it", 8));
            foreach (var s in c.Sources.Take(5)) foot.Children.Add(GameTooltip.Muted(s.Line));
            if (c.Sources.Count > 5) foot.Children.Add(GameTooltip.Muted("+" + (c.Sources.Count - 5) + " more in the Atlas"));
            foot.Children.Add(GameTooltip.Muted("Click to open it in the Atlas", 8));
            return GameTooltip.Wrap(gt);
        }

        // ---- enchants and food: the same row as an item, with the entry's own icon, words and way to get it
        FrameworkElement ExtraRow(ExtraEntry en, string key, string linePrefix)
        {
            var bd = new Border { Padding = new Thickness(8), CornerRadius = (CornerRadius)FindResource("Radius.Control"), Background = Brushes.Transparent, Cursor = Cursors.Hand, Margin = new Thickness(0, 4, 0, 0) };
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var iconBox = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(8), Background = SlotBg, BorderThickness = new Thickness(1), BorderBrush = EdgeFor(en.Quality), VerticalAlignment = VerticalAlignment.Top };
            var ig = new Grid();
            var fb = new Border { Margin = new Thickness(4), Child = new TextBlock { Text = Initial(en.Name), Foreground = UpQuality(en.Quality), FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            var img = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(2), SnapsToDevicePixels = true };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            ig.Children.Add(fb); ig.Children.Add(img);
            iconBox.Child = ig;
            if (en.IconId > 0)
            {
                var have = IconStore.TryGet(en.IconId);
                if (have != null) { img.Source = have; fb.Visibility = Visibility.Collapsed; }
                else IconStore.Request(en.IconId, src => Dispatcher.BeginInvoke(new Action(() => { img.Source = src; fb.Visibility = Visibility.Collapsed; })));
            }
            g.Children.Add(iconBox);
            var main = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
            var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
            nameRow.Children.Add(new TextBlock { Text = en.Name, Style = TextStyle("Text.RowTitle"), Foreground = en.ItemId > 0 ? UpQuality(en.Quality) : Res("Brush.Text.Primary"), TextTrimming = TextTrimming.CharacterEllipsis });
            nameRow.Children.Add(new TextBlock { Text = "  " + en.Kind.ToLowerInvariant(), Style = TextStyle("Text.Caption"), Foreground = Res("Brush.Text.Tertiary"), VerticalAlignment = VerticalAlignment.Center });
            if (en.Applicable) nameRow.Children.Add(new TextBlock { Text = "  you can get it done", Style = TextStyle("Text.Caption"), Foreground = Res("Brush.Success"), VerticalAlignment = VerticalAlignment.Center });
            main.Children.Add(nameRow);
            string words = en.Delta.Length > 0 ? en.Delta : en.Buff;
            main.Children.Add(new TextBlock { Text = words, Style = TextStyle("Text.Secondary"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
            var best = en.Best;
            string how = best == null ? "" : best.Line + (en.Sources.Count > 1 ? " · +" + (en.Sources.Count - 1) + " more" : "");
            if (how.Length > 0) main.Children.Add(new TextBlock { Text = how, Style = TextStyle("Text.Caption"), Foreground = Res("Brush.Text.Tertiary"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
            if (en.Owned > 0 && en.ItemId > 0) main.Children.Add(new TextBlock { Text = "You have " + en.Owned + " across your characters", Style = TextStyle("Text.Caption"), Foreground = Res("Brush.Text.Tertiary"), Margin = new Thickness(0, 2, 0, 0) });
            if (en.Reagents.Count > 0) main.Children.Add(new TextBlock { Text = "Reagents: " + string.Join(", ", en.Reagents), Style = TextStyle("Text.Caption"), Foreground = Res("Brush.Text.Tertiary"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
            Grid.SetColumn(main, 1);
            g.Children.Add(main);
            bool isFood = UpgradeExtras.GroupOf(en.Kind).Length > 0;
            if (!isFood)
            {
                var pct = new TextBlock { Text = UpgradeScore.PctText(en.Pct), Style = TextStyle("Text.RowTitle"), Foreground = Res("Brush.Success"), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Right };
                Grid.SetColumn(pct, 2);
                g.Children.Add(pct);
            }
            bd.Child = g;
            bd.MouseEnter += (s, e) => bd.Background = Res("Brush.Overlay.Hover");
            bd.MouseLeave += (s, e) => bd.Background = Brushes.Transparent;
            var link = en.Link;
            bd.MouseLeftButtonUp += (s, e) => { if (!e.Handled && OpenAtlas != null && link.Length > 0) { e.Handled = true; OpenAtlas(link); } };
            var tip = ExtraTip(en);
            bd.ToolTip = tip;
            upTips[key] = tip; upRows[key] = bd;
            ToolTipService.SetInitialShowDelay(bd, 150);
            ToolTipService.SetShowDuration(bd, 30000);
            upLines.Add(linePrefix + "|" + (en.ItemId > 0 ? en.ItemId : en.SpellId) + "|" + en.Name + "|" + (isFood ? en.Buff : UpgradeScore.PctText(en.Pct) + "|" + en.Delta) + "|" + (best == null ? "" : best.Line) + (en.Applicable ? "|applicable" : "") + (en.ClassicOnly ? "|classic" : ""));
            return bd;
        }

        ToolTip ExtraTip(ExtraEntry en)
        {
            var gt = new GameTooltip(en.Name, en.ItemId > 0 ? en.Quality : 1, null, upWho?.Level ?? 0);
            var item = en.ItemId > 0 ? ItemData.Get(en.ItemId) : null;
            if (item != null) gt.AddItem(item);
            gt.AddText(en.Kind, "slot");
            if (en.Buff.Length > 0) gt.AddText(en.Buff, "stat2");
            var foot = gt.Footer;
            if (en.Delta.Length > 0)
                foreach (var part in en.Delta.Split(new[] { ", " }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var t = part.TrimStart('(');
                    if (t.StartsWith("+") || t.StartsWith("-")) foot.Children.Add(new TextBlock { Text = part, Foreground = GameTooltip.Brush(t.StartsWith("+") ? "Brush.GameTip.Green" : "Brush.GameTip.Red"), FontSize = 13, Margin = new Thickness(0, 2, 0, 0) });
                }
            if (en.Slot > 0) foot.Children.Add(GameTooltip.Muted(en.Delta.Contains("no enchant") ? "Nothing is enchanted there now" : "Compared with what is on the worn item now", 4));
            if (en.Learn.Count > 0) { foot.Children.Add(GameTooltip.Muted("How the formula is learned", 8)); foreach (var l in en.Learn) foot.Children.Add(GameTooltip.Muted(l)); }
            if (en.Reagents.Count > 0) { foot.Children.Add(GameTooltip.Muted("Reagents", 8)); foreach (var l in en.Reagents) foot.Children.Add(GameTooltip.Muted(l)); }
            foot.Children.Add(GameTooltip.Muted(en.Kind == "Enchanting" ? "Who can apply it" : "Where to get it", 8));
            foreach (var s in en.Sources.Take(5)) foot.Children.Add(GameTooltip.Muted(s.Line));
            if (en.Owned > 0) foot.Children.Add(GameTooltip.Muted("You have " + en.Owned + " across your characters"));
            if (en.ClassicOnly) foot.Children.Add(GameTooltip.Muted("Only the Classic baseline knows where"));
            if (en.Partial) foot.Children.Add(GameTooltip.Muted("This enchant also has an effect that is not scored"));
            foot.Children.Add(GameTooltip.Muted("Click to open it in the Atlas", 8));
            return GameTooltip.Wrap(gt);
        }

        // "Consumables": the best three of each kind that applies to the class, in groups that fold away
        static string KindCaption(string kind, string who)
        {
            switch (kind)
            {
                case "Well fed": return "Well fed: buffs that match " + who + "'s weights";
                case "Food": return "Healing food for this level";
                case "Drink": return "Mana drinks for this level";
                case "Elixir": return "Elixirs that match the weights";
                case "Flask": return "Flasks that match the weights";
                case "Healing potion": return "Healing potions";
                case "Mana potion": return "Mana potions";
                case "Potion": return "Potions with a buff that matches the weights";
                case "Scroll": return "Scrolls that match the weights";
                case "Bandage": return "Bandages (First Aid)";
                case "Ammo": return "Ammo for the ranged weapon, best first";
                default: return "Temporary: gone after a while";
            }
        }

        void ConsumablesSection(UpgradeSet set)
        {
            var food = UpgradeEngine.PickFood(set, Filters);
            if (food.Count == 0) return;
            var sp = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            sp.Children.Add(new TextBlock { Text = "Consumables", Style = TextStyle("Text.RowTitle"), Margin = new Thickness(8, 0, 0, 0) });
            foreach (var group in UpgradeExtras.Groups)
            {
                var inGroup = food.Where(e => UpgradeExtras.GroupOf(e.Kind) == group).ToList();
                if (inGroup.Count == 0) continue;
                bool hidden = Filters.HiddenGroups.Contains(group);
                var g = group;
                var head = new Button { Content = (hidden ? "▸ " : "▾ ") + UpgradeExtras.GroupTitle(group) + " · " + inGroup.Count, Style = (Style)FindResource("GhostButton"), Height = 28, Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, ToolTip = hidden ? "Show these" : "Hide these" };
                System.Windows.Automation.AutomationProperties.SetName(head, UpgradeExtras.GroupTitle(group));
                head.Click += (s, e) => ToggleGroup(g);
                upCtl["group:" + group] = head;
                sp.Children.Add(head);
                upLines.Add("upgroup|" + group + "|" + inGroup.Count + (hidden ? "|collapsed" : ""));
                if (hidden) continue;
                foreach (var kind in UpgradeExtras.KindOrder)
                {
                    var l = inGroup.Where(e => e.Kind == kind).ToList();
                    if (l.Count == 0) continue;
                    sp.Children.Add(new TextBlock { Text = KindCaption(kind, upWho.Name), Style = TextStyle("Text.Caption"), Foreground = Res("Brush.Text.Tertiary"), Margin = new Thickness(8, 4, 0, 0) });
                    foreach (var e in l) sp.Children.Add(ExtraRow(e, "f:" + kind + ":" + e.ItemId, "upfood|" + kind));
                }
            }
            upBody.Children.Add(sp);
        }

        void ToggleGroup(string group)
        {
            if (!Filters.HiddenGroups.Remove(group)) Filters.HiddenGroups.Add(group);
            SaveFilters();
            RenderUpgrades();
        }
        public void UpgradeToggleGroupForTest(string group) => ToggleGroup(group);

        // ---- a click on a paper-doll slot: its upgrades are highlighted (a second click clears it)
        void FocusUpgradeSlot(int slot)
        {
            if (upBody == null || upWho == null) return;
            upFocus = upFocus == slot ? 0 : slot;
            // the doll tile gets an accent edge while it is the one in focus
            foreach (var kv in dollSlots)
                if (kv.Value is Border b)
                {
                    if (!upEdge.ContainsKey(kv.Key)) upEdge[kv.Key] = b.BorderBrush;
                    b.BorderBrush = kv.Key == upFocus ? Res("Brush.Accent") : upEdge[kv.Key];
                }
            RenderUpgrades();
            if (upFocus != 0 && upBlocks.TryGetValue(upFocus, out var el)) { upLines.Add("focus|" + upFocus); el.BringIntoView(); }
        }

        // ---- the weights editor
        void ToggleWeights()
        {
            upWeightsOpen = !upWeightsOpen;
            RefreshWeights();
        }

        void RefreshWeights()
        {
            if (upWeightsHost == null) return;
            upWeightsHost.Children.Clear();
            upWeightsHost.Visibility = upWeightsOpen ? Visibility.Visible : Visibility.Collapsed;
            if (!upWeightsOpen || upWho == null) return;
            upSuppress = true;
            var cls = upWho.ClassFile; int spec = upWho.Spec;
            var w = UpgradeWeights.Effective(cls, spec);
            var box = new Border { Background = Res("Brush.Surface2"), CornerRadius = (CornerRadius)FindResource("Radius.ControlLg"), Padding = new Thickness(12), Margin = new Thickness(0, 4, 0, 4) };
            var sp = new StackPanel();
            string specName = UpgradeWeights.SpecName(cls, UpgradeWeights.SpecKeyOf(cls, spec)) ?? "";
            sp.Children.Add(new TextBlock { Text = "Weights for " + UpgradeChar.Pretty(cls) + (specName.Length > 0 ? ", " + specName : ""), Style = TextStyle("Text.RowTitle") });
            sp.Children.Add(UpNote("What one point of each stat is worth to you, next to the others. A higher number makes items with more of it rank higher. They are kept for this class and talent tree." + (UpgradeWeights.IsChanged(cls, spec) ? " You changed some of them." : ""), 2));
            var wrap = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            var boxes = new Dictionary<string, TextBox>();
            foreach (var k in UpgradeWeights.Keys)
            {
                var cell = new StackPanel { Orientation = Orientation.Horizontal, Width = 232, Margin = new Thickness(0, 0, 12, 8) };
                cell.Children.Add(new TextBlock { Text = k.Value, Style = TextStyle("Text.Secondary"), Width = 140, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
                w.TryGetValue(k.Key, out var v);
                var tb = new TextBox { Style = (Style)FindResource("Input"), Width = 72, Text = v.ToString("0.###", CultureInfo.InvariantCulture), Tag = k.Value, VerticalAlignment = VerticalAlignment.Center };
                System.Windows.Automation.AutomationProperties.SetName(tb, k.Value + " weight");
                tb.KeyDown += (s, e) => { if (e.Key == Key.Enter) ApplyWeights(boxes); };
                boxes[k.Key] = tb; upCtl["w:" + k.Key] = tb;
                cell.Children.Add(tb);
                wrap.Children.Add(cell);
            }
            sp.Children.Add(wrap);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            var apply = new Button { Content = "Apply", Style = (Style)FindResource("Button.Primary.S"), Height = 28, Margin = new Thickness(0, 0, 8, 0) };
            apply.Click += (s, e) => ApplyWeights(boxes);
            var reset = new Button { Content = "Reset to defaults", Style = (Style)FindResource("GhostButton"), Height = 28, Margin = new Thickness(0, 0, 8, 0) };
            reset.Click += (s, e) => { UpgradeWeights.Reset(cls, spec); UpgradeEngine.ClearCache(); RefreshWeights(); StartUpgrades(); };
            upCtl["w:apply"] = apply; upCtl["w:reset"] = reset;
            row.Children.Add(apply); row.Children.Add(reset);
            sp.Children.Add(row);
            box.Child = sp;
            upWeightsHost.Children.Add(box);
            upSuppress = false;
        }

        void ApplyWeights(Dictionary<string, TextBox> boxes)
        {
            var w = new Dictionary<string, double>();
            foreach (var kv in boxes)
            {
                if (double.TryParse((kv.Value.Text ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 0 && v <= 1000 && !double.IsNaN(v)) w[kv.Key] = v;
                else { w[kv.Key] = UpgradeWeights.Effective(upWho.ClassFile, upWho.Spec).TryGetValue(kv.Key, out var old) ? old : 0; kv.Value.Text = w[kv.Key].ToString("0.###", CultureInfo.InvariantCulture); }
            }
            UpgradeWeights.Set(upWho.ClassFile, upWho.Spec, w);
            UpgradeEngine.ClearCache();
            StartUpgrades();
        }

        // the Lodge's crafters arrived (or the Professions page learned something): the panel asks again
        public void UpgradesDataChanged()
        {
            if (upBody == null || upWho == null || !IsLoaded) return;
            UpgradeEngine.ClearCache();
            StartUpgrades();
        }

        // ---- self-test hooks
        public bool UpgradeReadyForTest => upReady;
        public bool UpgradePanelForTest => upCard != null && upCard.IsVisible;
        public List<string> UpgradeLinesForTest() => upLines.ToList();
        public UpgradeSet UpgradeSetForTest => upSet;
        public FrameworkElement UpgradeCardForTest => upCard;
        public List<string> UpgradeExtraTipForTest(string key) => upTips.TryGetValue(key, out var tip) && tip.Content is GameTooltip gt ? gt.Texts() : new List<string>();
        public void UpgradeClickExtraForTest(string key) { if (upRows.TryGetValue(key, out var row)) row.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent }); }
        public bool UpgradeSwitchForTest(string key) => upCtl.TryGetValue(key, out var el) && el is CheckBox cb && cb.IsChecked == true;
        public void UpgradeClickSwitchForTest(string key)
        {
            if (!upCtl.TryGetValue(key, out var el) || !(el is CheckBox cb)) return;
            cb.IsChecked = cb.IsChecked != true;
            cb.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        }
        public void UpgradeClickSlotForTest(int slot) => FocusUpgradeSlot(slot);
        public int UpgradeFocusForTest => upFocus;
        public bool UpgradeDollEdgeForTest(int slot) => dollSlots.TryGetValue(slot, out var el) && el is Border b && ReferenceEquals(b.BorderBrush, Res("Brush.Accent"));
        public bool UpgradeBlockVisibleForTest(int slot) => upBlocks.ContainsKey(slot);
        public List<string> UpgradeTipForTest(int slot, int itemId)
        {
            var res = new List<string>();
            if (!upTips.TryGetValue(slot + ":" + itemId, out var tip) || !(tip.Content is GameTooltip gt)) return res;
            return gt.Texts();
        }
        public List<KeyValuePair<string, bool>> UpgradeTipColorsForTest(int slot, int itemId)       // text -> true when green
        {
            var res = new List<KeyValuePair<string, bool>>();
            if (!upTips.TryGetValue(slot + ":" + itemId, out var tip) || !(tip.Content is GameTooltip gt)) return res;
            void Walk(object o)
            {
                if (o is TextBlock tb) res.Add(new KeyValuePair<string, bool>(tb.Text, ReferenceEquals(tb.Foreground, GameTooltip.Brush("Brush.GameTip.Green"))));
                else if (o is Panel p) foreach (var ch in p.Children) Walk(ch);
                else if (o is Border bd) Walk(bd.Child);
            }
            Walk(gt.Body);
            return res;
        }
        public void UpgradeClickRowForTest(int slot, int itemId)
        {
            if (!upRows.TryGetValue(slot + ":" + itemId, out var row)) return;
            row.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
        }
        public string UpgradeMapLinkForTest(UpgradeSource s) => MapLinkFor(s);
        public GameTooltip UpgradeTipControlForTest(int slot, int itemId) => upTips.TryGetValue(slot + ":" + itemId, out var tip) ? tip.Content as GameTooltip : null;
        public void UpgradeToggleWeightsForTest() => ToggleWeights();
        public bool UpgradeWeightsOpenForTest => upWeightsOpen && upWeightsHost != null && upWeightsHost.Visibility == Visibility.Visible;
        public string UpgradeWeightForTest(string key) => upCtl.TryGetValue("w:" + key, out var el) && el is TextBox tb ? tb.Text : null;
        public void UpgradeSetWeightForTest(string key, string text) { if (upCtl.TryGetValue("w:" + key, out var el) && el is TextBox tb) tb.Text = text; }
        public void UpgradeApplyWeightsForTest() { if (upCtl.TryGetValue("w:apply", out var el) && el is Button b) b.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); }
        public void UpgradeResetWeightsForTest() { if (upCtl.TryGetValue("w:reset", out var el) && el is Button b) b.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); }
    }
}
