using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Atlas.Classic;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Maps;

namespace ElansAddonHub
{
    // The start page: what Atlas knows and where that comes from (game data, Classic baseline, the Lodge pool) and the Lodge's latest finds.
    public partial class AtlasPage
    {
        public ClassicSetup ClassicSetup { get; private set; }
        public Func<string> LodgeStateOverrideForTest;

        // The Lodge's state as the start page says it: set by the main window from the session
        public Func<string> LodgeState = () => "";

        void DrawStart()
        {
            model = null; browse = null; hosted = null; browseList = null; drawnSig = Signature();
            ReleaseMaps();
            Body.Children.Clear();
            cols = null; leftCol = rightCol = null; startInfo = null; tileGrid = null; mapCol = null; mapCardEl = null;
            TitleText.Text = "Atlas"; TitleText.Foreground = Res("Brush.Text.Primary");
            SubText.Text = "Browse or search the items, creatures, quests, zones and instances of WoW Forever";
            UpdateNav();
            var col = new StackPanel();
            Body.Children.Add(col);

            col.Children.Add(ClassicBannerView());
            col.Children.Add(ClassicOfferView());
            col.Children.Add(Card(BrowseBlock()));
            _ = FillBrowseBlock();

            // the info cards sit side by side when the window is wide (see LayoutStart) and stack when it is narrow
            startCards.Clear();
            startCards.Add(Card(GameDataBlock()));
            startCards.Add(ClassicCardView());
            startCards.Add(Card(LodgeBlock()));
            var finds = FindsBlock();
            if (finds != null) startCards.Add(Card(finds));
            startInfo = new Grid();
            col.Children.Add(startInfo);
            startInfoCols = 0; startTileCols = 0;
            LayoutStart();
            Scroller.ScrollToTop();
            Motion.PageIn(Body);
        }

        // ---- start page layout: tiles per row and info columns follow the width; nothing is drawn again, the same elements are placed anew only when the count changes
        public const double InfoTwoColumns = 900, InfoThreeColumns = 1400, TileMinWidth = 170;
        readonly List<FrameworkElement> startCards = new List<FrameworkElement>();
        Grid startInfo; int startInfoCols, startTileCols;
        public int TileColumnsForTest => startTileCols;
        public int InfoColumnsForTest => startInfoCols;

        public static int TileColumnsFor(double scrollerWidth) => (int)Math.Max(2, Math.Min(6, Math.Floor((scrollerWidth - 8 - 32 + 8) / (TileMinWidth + 8))));
        public static int InfoColumnsFor(double scrollerWidth) => scrollerWidth >= InfoThreeColumns ? 3 : scrollerWidth >= InfoTwoColumns ? 2 : 1;

        void LayoutStart()
        {
            if (startInfo == null) return;
            double w = Scroller.ActualWidth > 0 ? Scroller.ActualWidth : 760;
            int tiles = TileColumnsFor(w), info = InfoColumnsFor(w);
            if (tileGrid != null && tiles != startTileCols) { tileGrid.Columns = tiles; }
            startTileCols = tiles;
            if (info == startInfoCols) return;
            startInfoCols = info;
            foreach (var c in startCards) (VisualTreeHelper.GetParent(c) as Panel)?.Children.Remove(c);
            startInfo.Children.Clear(); startInfo.ColumnDefinitions.Clear();
            var lists = new List<List<FrameworkElement>>();
            for (int i = 0; i < info; i++) lists.Add(new List<FrameworkElement>());
            for (int i = 0; i < startCards.Count; i++)
            {
                int to = info == 1 ? 0 : info == 2 ? (i < (startCards.Count + 1) / 2 ? 0 : 1) : (startCards.Count == 4 ? new[] { 0, 1, 0, 2 }[i] : i % 3);
                lists[to].Add(startCards[i]);
            }
            for (int i = 0; i < info; i++)
            {
                if (i > 0) startInfo.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
                startInfo.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var sp = new StackPanel();
                foreach (var c in lists[i]) sp.Children.Add(c);
                Grid.SetColumn(sp, i * 2);
                startInfo.Children.Add(sp);
            }
        }

        // ---- game data: one line per layer, as the Settings "Game data" line says it
        public List<string> GameDataLines()
        {
            var l = new List<string>
            {
                "Items: " + StatusOnly(ItemData.StatusLine),
                "Spells and recipes: " + (SpellData.Ready ? SpellData.SpellCount.ToString("N0", CalendarRules.English) + " spells, " + SpellData.RecipeCount.ToString("N0", CalendarRules.English) + " recipes" : SpellData.Status == SpellData.State.Disabled ? "switched off" : SpellData.Status == SpellData.State.Unavailable ? "unavailable (" + SpellData.Reason + ")" : "not read yet"),
                "Maps: " + StatusOnly(MapData.StatusLine),
                "Seen by your game: " + StatusOnly(SeenData.StatusLine),
            };
            return l;
        }

        // the Settings lines say their own name first ("Item data: read from ...", "Maps: off"): the start page has the name in its own column
        static string StatusOnly(string line)
        {
            line = (line ?? "").Trim();
            int c = line.IndexOf(':');
            if (c > 0 && c < 16) line = line.Substring(c + 1).Trim();
            return line == "off" ? "switched off" : line;
        }

        FrameworkElement GameDataBlock()
        {
            var sp = new StackPanel();
            sp.Children.Add(Tb("Your game data", "Text.Section"));
            var n = Tb("Read from your own WoW installation, on this PC only. Label: Forever.", "Text.Secondary", null, 2); n.TextWrapping = TextWrapping.Wrap; sp.Children.Add(n);
            foreach (var line in GameDataLines())
            {
                int c = line.IndexOf(':');
                var g = new Grid { Margin = new Thickness(0, 8, 0, 0) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.Children.Add(Tb(line.Substring(0, c), "Text.Secondary"));
                var v = Tb(line.Substring(c + 1).Trim(), "Text.Body"); v.TextWrapping = TextWrapping.Wrap; Grid.SetColumn(v, 1); g.Children.Add(v);
                sp.Children.Add(g);
            }
            var hint = Tb(ItemData.Status == ItemData.State.Disabled ? "Switched on again in Settings > General > Read item data from my WoW installation." : "Switch it off in Settings > General > Read item data from my WoW installation.", "Text.Caption", Res("Brush.Text.Tertiary"), 12);
            hint.TextWrapping = TextWrapping.Wrap; sp.Children.Add(hint);
            return sp;
        }

        // ---- the Lodge pool
        public sealed class PoolSummary
        {
            public bool Has; public int Finds, FindMembers, Sources, Vendors, Quests; public long Updated; public string State = "";
        }

        public PoolSummary Pool()
        {
            var L = AtlasQuery.Context.Lodge?.Invoke();
            var p = new PoolSummary { State = LodgeStateOverrideForTest != null ? LodgeStateOverrideForTest() : LodgeState() };
            if (L == null) return p;
            p.Has = L.HasData;
            p.Finds = L.FindCount; p.FindMembers = L.AllFinds.Select(f => f.By ?? "a member").Distinct().Count();
            p.Sources = L.KillSources + L.ObjectSources + L.FishingZones; p.Vendors = L.VendorCount; p.Updated = L.Updated;
            return p;
        }

        FrameworkElement LodgeBlock()
        {
            var sp = new StackPanel();
            sp.Children.Add(Tb("Lodge pool", "Text.Section"));
            var p = Pool();
            var l1 = Tb(p.State.Length > 0 ? p.State : "Not connected to a Lodge", "Text.Body", null, 4); l1.TextWrapping = TextWrapping.Wrap; sp.Children.Add(l1);
            if (p.Has)
            {
                var d = Tb(AtlasFormat.Count(p.Sources, "source", "sources") + " with observed loot, " + AtlasFormat.Count(p.Vendors, "vendor", "vendors") + ", " + AtlasFormat.Count(p.Finds, "find", "finds") + " by "
                    + AtlasFormat.Count(p.FindMembers, "member", "members") + (p.Updated > 0 ? ". Last update " + AtlasFormat.Ago(p.Updated, AtlasQuery.Context.NowUtc()) + "." : "."), "Text.Secondary", null, 4);
                d.TextWrapping = TextWrapping.Wrap; sp.Children.Add(d);
            }
            else
            {
                var d = Tb("Members who share what they see in game (with Elan's Toolkit) build this pool: drop chances from real kills, vendor prices, finds and spawn points. Label: Forever.", "Text.Secondary", null, 4);
                d.TextWrapping = TextWrapping.Wrap; sp.Children.Add(d);
            }
            return sp;
        }

        FrameworkElement FindsBlock()
        {
            var L = AtlasQuery.Context.Lodge?.Invoke();
            if (L == null || L.AllFinds.Count == 0) return null;
            var sp = new StackPanel();
            sp.Children.Add(Tb("Recent finds in the Lodge", "Text.Section"));
            var rows = new List<AtlasRow>();
            foreach (var f in L.AllFinds.OrderByDescending(f => f.Seq).Take(8))
            {
                var r = AtlasQuery.ItemRow(f.Item, "", AtlasLabel.Forever);
                var src = f.Src ?? "";
                int c = src.IndexOf(':'); int.TryParse(c > 0 ? src.Substring(c + 1) : "0", out var sid);
                var kind = c > 0 ? src.Substring(0, c) : src;
                var from = kind == "npc" ? AtlasQuery.NpcName(sid) : kind == "obj" ? AtlasQuery.ObjectName(sid) : kind == "vendor" ? "bought from " + AtlasQuery.NpcName(sid) : kind == "quest" ? "quest " + AtlasQuery.QuestTitle(sid) : kind == "fish" ? "fishing" : "";
                r.Detail = (string.IsNullOrEmpty(f.By) ? "a member" : f.By) + " · " + f.Count + "× " + (from.Length > 0 ? "from " + from + " " : "") + (f.Zone > 0 ? "· " + AtlasQuery.ZoneName(f.Zone) : "");
                r.Right = AtlasFormat.Ago(f.At, AtlasQuery.Context.NowUtc());
                rows.Add(r);
            }
            var host = new ContentControl { Margin = new Thickness(-8, 8, -8, 0), Focusable = false, Content = new ItemsControl { ItemsSource = rows, ItemTemplate = (DataTemplate)FindResource("AtlasRowTemplate"), Focusable = false } };
            sp.Children.Add(host);
            return sp;
        }
    }
}
