using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Db2;

namespace ElansAddonHub
{
    // An item or spell tooltip drawn like the game's own: near-black navy panel, grey-blue hairline, the name in its quality colour, white / green / gold
    // lines in the game's order. The colours are the game's (Brush.GameTip.* in the theme), not the app theme's. Everything the Outpost adds (what you own,
    // ids, hints) goes into Footer, below a hairline, in a muted colour.
    public sealed partial class GameTooltip : Border
    {
        readonly StackPanel body = new StackPanel();
        StackPanel footer;
        readonly string title;
        public int PlayerLevel;                      // > 0 = the shown character's level: a requirement above it turns red

        public StackPanel Body => body;

        // The optional icon, shown to the LEFT of the panel and outside it, the way the game's item link tooltip shows it (the GameTip style of ToolTip
        // draws the 40 px frame from this property). Unset = no frame. Not drawn when the tooltip is placed on a page by hand (WithIcon does that there).
        public static readonly DependencyProperty IconProperty = DependencyProperty.Register(nameof(Icon), typeof(ImageSource), typeof(GameTooltip), new PropertyMetadata(null, (d, e) => d.SetValue(HasIconProperty, e.NewValue != null)));
        public static readonly DependencyProperty HasIconProperty = DependencyProperty.Register(nameof(HasIcon), typeof(bool), typeof(GameTooltip), new PropertyMetadata(false));
        public bool HasIcon => (bool)GetValue(HasIconProperty);
        public ImageSource Icon { get => (ImageSource)GetValue(IconProperty); set => SetValue(IconProperty, value); }
        public int IconFileId { get; private set; }      // the icon file asked for (self-test), 0 = none

        // the icon of a game file id: from disk when extracted already, else filled in when it arrives (read-only from the player's own game files)
        public GameTooltip SetIcon(int fileId)
        {
            if (fileId <= 0) return this;
            IconFileId = fileId;
            var have = IconStore.TryGet(fileId);
            if (have != null) { Icon = have; return this; }
            var disp = Application.Current?.Dispatcher;
            IconStore.Request(fileId, img => { if (img != null) disp?.BeginInvoke(new Action(() => Icon = img)); });
            return this;
        }
        public GameTooltip SetItemIcon(int itemId) { try { var ci = itemId > 0 ? ItemData.Get(itemId) : null; if (ci != null) SetIcon(ci.IconId); } catch (Exception) { } return this; }
        public GameTooltip SetSpellIcon(int spellId) { try { var sp = spellId > 0 ? SpellData.Spell(spellId) : null; if (sp != null) SetIcon(sp.IconId); } catch (Exception) { } return this; }
        public string Title => title;

        public GameTooltip(string title, int quality = -1, string rank = null, int playerLevel = 0)
        {
            this.title = title ?? "";
            PlayerLevel = playerLevel;
            Background = Brush("Brush.GameTip.Bg");
            BorderBrush = Brush("Brush.GameTip.Border");
            BorderThickness = new Thickness(1);
            CornerRadius = Application.Current?.TryFindResource("Radius.GameTip") is CornerRadius cr ? cr : default(CornerRadius);
            Padding = new Thickness(12, 8, 12, 8);
            MaxWidth = 320;
            MinWidth = 200;
            HorizontalAlignment = HorizontalAlignment.Left;
            TextElement.SetFontFamily(this, GameFont.Family);
            TextElement.SetFontSize(this, 13);
            TextElement.SetForeground(this, Brush("Brush.GameTip.Text"));
            Child = body;
            body.Children.Add(rank == null ? Wrapped(this.title, QualityBrush(quality), 15) : TwoColumn(this.title, QualityBrush(quality), rank, Brush("Brush.GameTip.Grey"), 15));
        }

        // ---- brushes (the game's colours, from the theme)
        public static Brush Brush(string key) => Application.Current?.TryFindResource(key) as Brush ?? Brushes.White;
        public static Brush QualityBrush(int q) => Brush("Brush.GameTip.Q" + (q < 0 ? 1 : Math.Min(7, q)));

        // ---- building
        public GameTooltip AddItem(ClientItem it, ICollection<int> worn = null) => it == null ? this : AddLines(ItemText.Lines(it, worn));

        public GameTooltip AddLines(IEnumerable<TipLine> lines)
        {
            var l = lines.ToList();
            for (int i = 0; i < l.Count; i++)
            {
                var t = l[i];
                if (t.Kind == "damage" && i + 1 < l.Count && l[i + 1].Kind == "speed")       // "20 - 34 Damage" left, "Speed 2.60" right
                {
                    body.Children.Add(TwoColumn(t.Text, Brush("Brush.GameTip.Text"), l[i + 1].Text, Brush("Brush.GameTip.Text"), 13));
                    i++;
                    continue;
                }
                body.Children.Add(Row(t));
            }
            return this;
        }

        public GameTooltip AddText(string text, string kind = "plain", string right = null)
            => AddLines(new[] { new TipLine { Kind = kind, Text = text, Right = right } });

        // the spell variant: range, cast time, then the description in gold
        public GameTooltip AddSpell(SpellView sp, bool details = true)
        {
            if (sp == null) return this;
            var inv = CultureInfo.InvariantCulture;
            var lines = new List<TipLine>();
            string range = details && sp.RangeMax > 0 ? sp.RangeMax.ToString("0.#", inv) + " yd range" : null;
            // the game's own order: cost on the left with the range on the right, then the cast time with the cooldown on the right
            if (!string.IsNullOrEmpty(sp.CostText)) lines.Add(new TipLine { Kind = "slot", Text = sp.CostText, Right = range });
            else if (range != null) lines.Add(new TipLine { Kind = "range", Text = range });
            if (details) lines.Add(new TipLine { Kind = "cast", Text = sp.CastMs > 0 ? (sp.CastMs / 1000.0).ToString("0.##", inv) + " sec cast" : "Instant", Right = ClassSnapshot.CooldownText(sp.CooldownMs) });
            var d = !string.IsNullOrWhiteSpace(sp.Description) ? sp.Description : sp.AuraDescription;
            if (!string.IsNullOrWhiteSpace(d)) lines.Add(new TipLine { Kind = "spelldesc", Text = d.Trim() });
            return AddLines(lines);
        }

        // the part that is the Outpost's own, under a hairline
        public StackPanel Footer
        {
            get
            {
                if (footer == null)
                {
                    footer = new StackPanel();
                    body.Children.Add(new Border { Height = 1, Background = Brush("Brush.GameTip.Divider"), Margin = new Thickness(0, 8, 0, 8) });
                    body.Children.Add(footer);
                }
                return footer;
            }
        }
        public bool HasFooter => footer != null;

        // an item as shown on a page: the icon (left, outside the panel) in a 56 px frame with the game's dark border, then the tooltip
        public static FrameworkElement WithIcon(FrameworkElement icon, GameTooltip tip, double size = 56)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
            var frame = new Border { Width = size, Height = size, BorderBrush = Brush("Brush.GameTip.IconBorder"), Background = Brush("Brush.GameTip.Bg"), BorderThickness = new Thickness(2), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 8, 0), Child = icon, ClipToBounds = true };
            row.Children.Add(frame);
            row.Children.Add(tip);
            return row;
        }

        // ---- rows
        static TextBlock Text(string text, Brush fg, double size = 13, double top = 0)
            => new TextBlock { Text = text, Foreground = fg, FontSize = size, Margin = new Thickness(0, top, 0, 0), TextWrapping = TextWrapping.Wrap };

        static TextBlock Wrapped(string text, Brush fg, double size) => new TextBlock { Text = text, Foreground = fg, FontSize = size, TextWrapping = TextWrapping.Wrap };

        static FrameworkElement TwoColumn(string left, Brush lfg, string right, Brush rfg, double size)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(new TextBlock { Text = left, Foreground = lfg, FontSize = size, TextWrapping = TextWrapping.Wrap });
            if (!string.IsNullOrEmpty(right))
            {
                var r = new TextBlock { Text = right, Foreground = rfg, FontSize = size, Margin = new Thickness(16, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
                Grid.SetColumn(r, 1);
                g.Children.Add(r);
            }
            return g;
        }

        FrameworkElement Row(TipLine l)
        {
            Brush white = Brush("Brush.GameTip.Text"), gold = Brush("Brush.GameTip.Gold"), green = Brush("Brush.GameTip.Green"), grey = Brush("Brush.GameTip.Grey");
            switch (l.Kind)
            {
                case "ilvl": return Text(l.Text, gold);
                case "slot": case "cast": return TwoColumn(l.Text, white, l.Right, white, 13);
                case "stat2": return Text(l.Text, green);
                case "effect": case "setbonus-on": return Text(l.Text, green, 13, l.Kind == "effect" ? 4 : 0);
                case "desc": case "spelldesc": return Text(l.Text, gold, 13, 4);
                case "req": return Text(l.Text, PlayerLevel > 0 && l.Level > PlayerLevel ? Brush("Brush.GameTip.Red") : white);
                case "setname": return Text(l.Text, gold, 13, 8);
                case "setitem": return Indented(l.Text, grey);
                case "setitem-worn": return Indented(l.Text, white);
                case "setbonus": case "grey": return Text(l.Text, grey);
                case "bad": return Text(l.Text, Brush("Brush.GameTip.Red"), 13, 4);
                case "header": return Text(l.Text, gold, 13, 4);
                case "item": return Text(l.Text, QualityBrush(l.Quality), 13, 0);
                case "unrevealed": return Text(l.Text, grey, 13, 4);
                case "price": return Price(l.Text, l.Copper);
                default: return Text(l.Text, white);
            }
        }

        static FrameworkElement Indented(string text, Brush fg) { var t = Text(text, fg); t.Margin = new Thickness(8, 0, 0, 0); return t; }

        // "Sell Price: 25 (gold) 53 (silver) 55 (copper)" with the game's coin pictures (MoneyText)
        static FrameworkElement Price(string label, long copper)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            row.Children.Add(new TextBlock { Text = label, Foreground = Brush("Brush.GameTip.Text"), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new MoneyText(copper) { Foreground = Brush("Brush.GameTip.Text"), Margin = new Thickness(8, 0, 0, 0) });
            return row;
        }

        // a muted line for the footer
        public static TextBlock Muted(string text, double top = 0) => new TextBlock { Text = text, Foreground = Brush("Brush.GameTip.Muted"), FontSize = 12, Margin = new Thickness(0, top, 0, 0), TextWrapping = TextWrapping.Wrap };

        // every text of the tooltip in order (self-test)
        public List<string> Texts()
        {
            var res = new List<string>();
            void Walk(object o)
            {
                if (o is TextBlock tb) res.Add(tb.Text);
                else if (o is Panel p) foreach (var ch in p.Children) Walk(ch);
                else if (o is Border bd) Walk(bd.Child);
            }
            Walk(body);
            return res;
        }

        // the ToolTip that carries a GameTooltip (no chrome of its own)
        public static ToolTip Wrap(GameTooltip tip)
        {
            var tt = new ToolTip { Content = tip };
            if (Application.Current?.TryFindResource("GameTip") is Style st) tt.Style = st;
            return tt;
        }
    }
}
