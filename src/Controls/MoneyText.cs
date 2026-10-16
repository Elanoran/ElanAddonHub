using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // Money shown the way the game shows it (2.77): the number, then the coin - "12 (gold) 34 (silver) 56 (copper)" - with the game's own coin
    // pictures (Interface\MoneyFrame\UI-GoldIcon / UI-SilverIcon / UI-CopperIcon, read read-only from the player's install through IconStore and cached
    // like every icon) and small drawn coins in the game's metals when there is no game to read them from. Zero parts are left out, nothing is "0c",
    // Compact drops the copper from 1 gold up and the silver from 1,000 gold up (Services\MoneyFormat.cs is the one formatter behind it).
    //
    //   new MoneyText { Copper = 12345 }                      a control of its own (tooltips, the character card)
    //   MoneyText.Coins(textBlock)                            a line of text that already says "Sold for 1g 5s": the amounts become coins in place
    //   <TextBlock local:MoneyText.Source="{Binding Right}"/> the same from XAML (the Atlas rows)
    // Copy, chat and other plain text keep the letters: MoneyFormat.Text(copper) ("1,234g 5s 6c").
    public sealed class MoneyText : StackPanel
    {
        public static readonly DependencyProperty CopperProperty = DependencyProperty.Register(nameof(Copper), typeof(long), typeof(MoneyText), new PropertyMetadata(0L, (d, e) => ((MoneyText)d).Rebuild()));
        public static readonly DependencyProperty CompactProperty = DependencyProperty.Register(nameof(Compact), typeof(bool), typeof(MoneyText), new PropertyMetadata(false, (d, e) => ((MoneyText)d).Rebuild()));

        public long Copper { get => (long)GetValue(CopperProperty); set => SetValue(CopperProperty, value); }
        public bool Compact { get => (bool)GetValue(CompactProperty); set => SetValue(CompactProperty, value); }
        // the numbers' colour and size (Text.Body's by default; the coins follow the size)
        public Brush Foreground { get => (Brush)GetValue(TextElement.ForegroundProperty); set => SetValue(TextElement.ForegroundProperty, value); }
        public double FontSize { get => (double)GetValue(TextElement.FontSizeProperty); set => SetValue(TextElement.FontSizeProperty, value); }

        public MoneyText()
        {
            Orientation = Orientation.Horizontal;
            VerticalAlignment = VerticalAlignment.Center;
            // looks like Text.Body unless told otherwise (a tooltip sets its own colour and size)
            SetResourceReference(TextElement.ForegroundProperty, "Brush.Text.Primary");
            SetValue(TextElement.FontSizeProperty, 13.0);
            Rebuild();
        }

        public MoneyText(long copper, bool compact = false) : this() { Compact = compact; Copper = copper; }

        // what it says, with letters ("1g 23s 45c"): screen readers, copy, tests
        public string AsText => MoneyFormat.Text(Copper, Compact);
        // the coins it shows, in order ("gsc")
        public string CoinOrder => new string(Children.OfType<Image>().Select(i => (char)i.Tag).ToArray());

        protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            if (e.Property == TextElement.FontSizeProperty) foreach (var i in Children.OfType<Image>()) i.Width = i.Height = CoinSize(FontSize);
        }

        void Rebuild()
        {
            Children.Clear();
            var copper = Copper;
            bool first = true;
            if (MoneyFormat.Negative(copper) && copper != 0) Children.Add(new TextBlock { Text = "-", VerticalAlignment = VerticalAlignment.Center });
            foreach (var p in MoneyFormat.Parts(copper, Compact))
            {
                Children.Add(new TextBlock { Text = p.Amount.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("en-US")), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(first ? 0 : 4, 0, 0, 0) });
                var img = MoneyIcons.NewImage(p.Coin, CoinSize(FontSize));
                img.Margin = new Thickness(2, 0, 0, 0);
                Children.Add(img);
                first = false;
            }
            AutomationProperties.SetName(this, AsText);
        }

        // the coin a little smaller than the text, like the game's (12 at the body size)
        internal static double CoinSize(double fontSize) => Math.Max(8, Math.Round((double.IsNaN(fontSize) || fontSize <= 0 ? 13 : fontSize) * 0.92));

        // ---------------------------------------------------------------- money inside a line of text

        // the text the line was given, with letters (TextBlock.Text no longer has them once coins are drawn)
        public static readonly DependencyProperty PlainProperty = DependencyProperty.RegisterAttached("Plain", typeof(string), typeof(MoneyText), new PropertyMetadata(null));
        public static string PlainText(TextBlock tb)
        {
            if (tb == null) return "";
            var p = (string)tb.GetValue(PlainProperty);
            return p != null && tb.Inlines.OfType<InlineUIContainer>().Any(i => i.Child is Image im && im.Tag is char) ? p : tb.Text;
        }

        // XAML: <TextBlock local:MoneyText.Source="{Binding Right}" />
        public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached("Source", typeof(string), typeof(MoneyText), new PropertyMetadata(null, (d, e) => { if (d is TextBlock tb) Coins(tb, (string)e.NewValue ?? ""); }));
        public static void SetSource(TextBlock tb, string v) => tb.SetValue(SourceProperty, v);
        public static string GetSource(TextBlock tb) => (string)tb.GetValue(SourceProperty);

        // the amounts in the line ("Sold by Kyra for 1g 5s · stack of 5") drawn as numbers and coins; a line without money stays plain text.
        // Returns the same TextBlock, so a call site can wrap what it already builds: MoneyText.Coins(Line(...)).
        public static TextBlock Coins(TextBlock tb, string text = null)
        {
            if (tb == null) return null;
            text = text ?? PlainText(tb);
            tb.SetValue(PlainProperty, text);
            var pieces = MoneyFormat.Split(text);
            if (!pieces.Any(p => p.Value != null)) { tb.Text = text; return tb; }
            tb.Inlines.Clear();
            var size = CoinSize(tb.FontSize);
            foreach (var p in pieces)
            {
                if (p.Value == null) { tb.Inlines.Add(new Run(p.Key)); continue; }
                long copper = p.Value.Value;
                if (copper < 0) tb.Inlines.Add(new Run("-"));
                bool first = true;
                foreach (var part in MoneyFormat.Parts(copper))
                {
                    tb.Inlines.Add(new Run((first ? "" : " ") + part.Amount.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("en-US"))));
                    var img = MoneyIcons.NewImage(part.Coin, size);
                    img.Margin = new Thickness(2, 0, 0, 0);
                    tb.Inlines.Add(new InlineUIContainer(img) { BaselineAlignment = BaselineAlignment.Center });
                    first = false;
                }
            }
            return tb;
        }

        // the coins drawn in a line, in order ("gsc gs") - tests
        public static string CoinsIn(TextBlock tb)
        {
            var sb = new System.Text.StringBuilder();
            char last = '\0';
            foreach (var i in tb.Inlines)
            {
                if (i is InlineUIContainer u && u.Child is Image im && im.Tag is char c) { if (last != '\0' && "gsc".IndexOf(c) <= "gsc".IndexOf(last)) sb.Append(' '); sb.Append(c); last = c; }
            }
            return sb.ToString();
        }
    }

    // The three coin pictures, shared by every MoneyText: the game's own (FileDataIDs of Interface\MoneyFrame\UI-GoldIcon.blp / UI-SilverIcon.blp /
    // UI-CopperIcon.blp, 16 x 16, checked as pictures and by name in the self-test) once IconStore has them, drawn coins until then or without a game.
    // Images bind to Current, so a coin read later swaps in everywhere at once (a weak binding: nothing is kept alive by it).
    public sealed class MoneyIcons : INotifyPropertyChanged
    {
        public const int GoldFile = 237618, SilverFile = 237620, CopperFile = 237617;
        public static readonly string[] GamePaths = { @"Interface\MoneyFrame\UI-GoldIcon.blp", @"Interface\MoneyFrame\UI-SilverIcon.blp", @"Interface\MoneyFrame\UI-CopperIcon.blp" };
        public static readonly MoneyIcons Current = new MoneyIcons();
        public static bool UseGameArt = true;          // false: drawn coins only (self-test: "no game installed")

        readonly ImageSource[] game = new ImageSource[3];
        readonly ImageSource[] drawn = new ImageSource[3];
        bool asked;

        public event PropertyChangedEventHandler PropertyChanged;

        public ImageSource Gold => Get(0);
        public ImageSource Silver => Get(1);
        public ImageSource Copper => Get(2);

        static int Index(char coin) => coin == 'g' ? 0 : coin == 's' ? 1 : 2;
        static readonly string[] Props = { nameof(Gold), nameof(Silver), nameof(Copper) };
        public static int FileOf(char coin) => new[] { GoldFile, SilverFile, CopperFile }[Index(coin)];

        public ImageSource For(char coin) => Get(Index(coin));
        // true when the picture shown for the coin is the game's own
        public bool FromGame(char coin) => UseGameArt && game[Index(coin)] != null;

        ImageSource Get(int i)
        {
            if (UseGameArt)
            {
                Ask();
                if (game[i] != null) return game[i];
            }
            return drawn[i] ?? (drawn[i] = Drawn("gsc"[i]));
        }

        void Ask()
        {
            if (asked) return;
            asked = true;
            var ui = Application.Current?.Dispatcher;
            for (int i = 0; i < 3; i++)
            {
                int k = i, id = FileOf("gsc"[i]);
                var have = IconStore.TryGet(id);
                if (have != null) { game[k] = have; continue; }
                IconStore.Request(id, img => ui?.BeginInvoke(DispatcherPriority.Background, new Action(() => { game[k] = img; Changed(k); })));
            }
        }

        void Changed(int i) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(Props[i]));

        // self-test: forget the pictures (and ask the game again on next use)
        public void ResetForTest()
        {
            asked = false;
            for (int i = 0; i < 3; i++) { game[i] = null; drawn[i] = null; Changed(i); }
        }

        // an Image that shows the coin and follows Current
        public static Image NewImage(char coin, double size)
        {
            var img = new Image { Width = size, Height = size, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center, Tag = coin, SnapsToDevicePixels = true };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            img.SetBinding(Image.SourceProperty, new Binding(Props[Index(coin)]) { Source = Current, Mode = BindingMode.OneWay });
            AutomationProperties.SetName(img, coin == 'g' ? "gold" : coin == 's' ? "silver" : "copper");
            return img;
        }

        // a 16 x 16 coin in the game's metal: a shaded disc, a dark rim, a thin inner ring like a minted edge
        public static DrawingImage Drawn(char coin)
        {
            string key = coin == 'g' ? "Gold" : coin == 's' ? "Silver" : "Copper";
            var res = Application.Current?.Resources;
            var fill = res?["Brush.Coin." + key] as Brush ?? Brushes.Gray;
            var rim = res?["Brush.Coin." + key + "Rim"] as Brush ?? Brushes.DimGray;
            var g = new DrawingGroup();
            g.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
            g.Children.Add(new GeometryDrawing(fill, new Pen(rim, 1.2), new EllipseGeometry(new Point(8, 8), 6.6, 6.6)));
            g.Children.Add(new GeometryDrawing(null, new Pen(rim, 0.6) { DashStyle = null }, new EllipseGeometry(new Point(8, 8), 4.4, 4.4)));
            g.Freeze();
            var di = new DrawingImage(g);
            di.Freeze();
            return di;
        }
    }
}
