using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Professions;
using ElansAddonHub.Services.Upgrades;

namespace ElansAddonHub
{
    // --selftest "money" (2.77): money shown like the game - the one formatter (MoneyFormat: parts, zero parts left out, 0c, negative, compact,
    // reading amounts back out of a line), the MoneyText control (numbers then coins, in the game's order), money inside a line of text
    // (MoneyText.Coins / MoneyText.Source), the coin pictures (the game's own from the local install when there is one, read-only; the drawn
    // coins without game data), the old helpers all agreeing with it, and a scan of the source for money formatted by hand or shown without coins.
    public partial class MainWindow
    {
        async Task<string> MoneyTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            void Check(string what, bool cond, string got = null) { SelfTestClock.Mark(what); notes.Add((cond ? "ok   " : "FAIL ") + what + (cond || got == null ? "" : "  [got: " + got + "]")); ok &= cond; }
            var savedUse = MoneyIcons.UseGameArt;
            try
            {
                // ---- the formatter
                var table = new (long copper, string text)[]
                {
                    (0, "0c"), (1, "1c"), (99, "99c"), (100, "1s"), (101, "1s 1c"), (9999, "99s 99c"), (10000, "1g"), (10005, "1g 5c"), (10500, "1g 5s"), (12345, "1g 23s 45c"),
                    (123456789, "12,345g 67s 89c"), (long.MaxValue, "922,337,203,685,477g 58s 7c"), (-5, "-5c"), (-2550, "-25s 50c"), (-10005, "-1g 5c"),
                };
                var bad = table.Where(t => MoneyFormat.Text(t.copper) != t.text).Select(t => t.copper + " -> " + MoneyFormat.Text(t.copper) + " (want " + t.text + ")").ToList();
                Check("format: 0c, 1c, 99c, 1s, 1s 1c, 99s 99c, 1g, 1g 5c (zero silver left out), 1g 5s, 1g 23s 45c, 12,345g 67s 89c, the largest amount, negative -5c / -25s 50c / -1g 5c", bad.Count == 0, string.Join("; ", bad));
                Check("format: long.MinValue does not overflow", MoneyFormat.Text(long.MinValue).StartsWith("-922,337,203,685,477g", StringComparison.Ordinal), MoneyFormat.Text(long.MinValue));
                var compact = new (long copper, string text)[] { (0, "0c"), (99, "99c"), (12345, "1g 23s"), (10045, "1g"), (9999999, "999g 99s"), (12345678, "1,234g"), (-12345, "-1g 23s") };
                var badC = compact.Where(t => MoneyFormat.Text(t.copper, true) != t.text).Select(t => t.copper + " -> " + MoneyFormat.Text(t.copper, true)).ToList();
                Check("compact: no copper from 1 gold, no silver from 1,000 gold; under a gold nothing is dropped", badC.Count == 0, string.Join("; ", badC));

                // every helper the app had says the same now (InventoryReader wrote "1g 0s 5c", the others did not)
                var amounts = new long[] { 0, 5, 100, 10005, 12345, 123456789 };
                Check("old helpers agree: InventoryReader.Money, AtlasFormat.Money, ProfMoney.Format, LiveSources.Money, UpgradeSource.Money, ItemText.Coins",
                    amounts.All(a => InventoryReader.Money(a) == MoneyFormat.Text(a) && AtlasFormat.Money(a) == MoneyFormat.Text(a) && ProfMoney.Format(a) == MoneyFormat.Text(a)
                        && LiveSources.Money(a) == MoneyFormat.Text(a) && UpgradeSource.Money(a) == MoneyFormat.Text(a)
                        && string.Join(" ", ItemText.Coins(a).Select(c => c.Key.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("en-US")) + c.Value)) == MoneyFormat.Text(a)),
                    InventoryReader.Money(10005) + " | " + AtlasFormat.Money(10005) + " | " + ProfMoney.Format(10005));

                // reading amounts back out of a line
                string Sp(string s) => string.Join("|", MoneyFormat.Split(s).Select(p => p.Value == null ? "'" + p.Key + "'" : "$" + p.Value));
                Check("read: 'Sold by Kyra for 1g 5s · stack of 5' -> one amount of 10500", Sp("Sold by Kyra for 1g 5s · stack of 5") == "'Sold by Kyra for '|$10500|' · stack of 5'", Sp("Sold by Kyra for 1g 5s · stack of 5"));
                Check("read: '12,345g 6c', '~2s 50c', '(-25s)', '0c' are money", Sp("12,345g 6c") == "$123450006" && Sp("~2s 50c") == "'~'|$250" && Sp("(-25s)") == "'('|$-2500|')'" && Sp("0c") == "$0",
                    Sp("12,345g 6c") + " ; " + Sp("~2s 50c") + " ; " + Sp("(-25s)"));
                Check("read: not money - 'Cast 1.5 sec', '1.5s', 'x5c', '5cm', '30 sec', 'level 60', '100s 5c'",
                    new[] { "Cast 1.5 sec", "1.5s", "x5c", "5cm", "30 sec", "level 60", "item 2g3" }.All(s => !MoneyFormat.HasMoney(s)) && !MoneyFormat.Split("100s 5c").Any(p => p.Value == 10005));
                Check("read: every formatted amount reads back to itself", table.All(t => MoneyFormat.Split(t.text).Count == 1 && MoneyFormat.Split(t.text)[0].Value == t.copper || t.copper == long.MaxValue));

                // ---- the coin pictures without game data: drawn coins in the game's metals (theme tokens)
                MoneyIcons.UseGameArt = false; MoneyIcons.Current.ResetForTest();
                Brush Fill(ImageSource src) => ((src as DrawingImage)?.Drawing as DrawingGroup)?.Children.OfType<GeometryDrawing>().FirstOrDefault(d => d.Geometry is EllipseGeometry)?.Brush;
                var R = Application.Current.Resources;
                Check("no game data: the three coins are drawn (gold, silver, copper from Brush.Coin.*), not empty",
                    Fill(MoneyIcons.Current.Gold) == R["Brush.Coin.Gold"] && Fill(MoneyIcons.Current.Silver) == R["Brush.Coin.Silver"] && Fill(MoneyIcons.Current.Copper) == R["Brush.Coin.Copper"]
                    && !MoneyIcons.Current.FromGame('g') && MoneyIcons.Current.Gold.Width > 0);

                // ---- the control
                var m1 = new MoneyText(12345);
                var texts = m1.Children.OfType<TextBlock>().Select(t => t.Text).ToList();
                var order = string.Join(",", m1.Children.Cast<UIElement>().Select(c => c is TextBlock t ? t.Text : "[" + ((Image)c).Tag + "]"));
                Check("control: 12345 copper = 1 [gold] 23 [silver] 45 [copper] - number first, then its coin, biggest first", order == "1,[g],23,[s],45,[c]", order);
                Check("control: the coins show the coin pictures (bound to the shared icons)", m1.Children.OfType<Image>().All(i => i.Source != null && i.Source == MoneyIcons.Current.For((char)i.Tag)));
                Check("control: 100 = 1 [silver] only; 0 = 0 [copper]; 10005 = 1 [gold] 5 [copper]", new MoneyText(100).CoinOrder == "s" && new MoneyText(0).CoinOrder == "c" && new MoneyText(0).Children.OfType<TextBlock>().Single().Text == "0" && new MoneyText(10005).CoinOrder == "gc");
                var neg = new MoneyText(-2550);
                Check("control: negative starts with a minus (-25 [silver] 50 [copper])", neg.Children.OfType<TextBlock>().First().Text == "-" && neg.CoinOrder == "sc" && neg.AsText == "-25s 50c");
                var big = new MoneyText(123456789) { Compact = true };
                Check("control: compact 12,345 [gold] only; large amounts with thousands separators", big.CoinOrder == "g" && big.Children.OfType<TextBlock>().Single().Text == "12,345");
                m1.Copper = 250; Check("control: changing Copper redraws (2 [silver] 50 [copper])", m1.CoinOrder == "sc" && m1.AsText == "2s 50c");
                Check("control: screen readers hear the amount with words", System.Windows.Automation.AutomationProperties.GetName(m1) == "2s 50c" && System.Windows.Automation.AutomationProperties.GetName(m1.Children.OfType<Image>().First()) == "silver");
                var sized = new MoneyText(12345) { FontSize = 20 };
                Check("control: the coins follow the text size", sized.Children.OfType<Image>().All(i => i.Width == MoneyText.CoinSize(20) && i.Width > MoneyText.CoinSize(12)));

                // ---- money inside a line
                var tb = MoneyText.Coins(new TextBlock(), "Sold by Kyra for 1g 5s · stack of 5");
                var runs = string.Concat(tb.Inlines.Select(i => i is Run r ? r.Text : i is InlineUIContainer u && u.Child is Image im ? "[" + im.Tag + "]" : "?"));
                Check("line: 'Sold by Kyra for 1g 5s · stack of 5' -> 'Sold by Kyra for 1[g] 5[s] · stack of 5' (letters become coins)", runs == "Sold by Kyra for 1[g] 5[s] · stack of 5", runs);
                Check("line: the plain text stays readable (copy, tests)", MoneyText.PlainText(tb) == "Sold by Kyra for 1g 5s · stack of 5" && MoneyText.CoinsIn(tb) == "gs");
                var plain = MoneyText.Coins(new TextBlock(), "Cast 1.5 sec · 30 yd range");
                Check("line: a line without money stays plain text (no coins)", plain.Text == "Cast 1.5 sec · 30 yd range" && !plain.Inlines.OfType<InlineUIContainer>().Any());
                var again = MoneyText.Coins(MoneyText.Coins(new TextBlock(), "Step ~1g - running total ~3g 20s"));
                Check("line: drawing coins twice keeps the same line (no lost amounts)", MoneyText.CoinsIn(again) == "g gs" && MoneyText.PlainText(again) == "Step ~1g - running total ~3g 20s", MoneyText.CoinsIn(again));
                var bound = new TextBlock(); MoneyText.SetSource(bound, "25s");
                bool boundCoins = MoneyText.CoinsIn(bound) == "s";
                MoneyText.SetSource(bound, "free");
                Check("line: the XAML form (MoneyText.Source) draws coins too, and follows a new value", boundCoins && bound.Text == "free" && MoneyText.CoinsIn(bound) == "");
                bound.Text = "later text"; Check("line: a plain Text set later wins over the stored plain text", MoneyText.PlainText(bound) == "later text");

                // ---- the game tooltip and the Characters card use the control
                var gt = new GameTooltip("Frostbite Edge", 4).AddLines(new[] { new TipLine { Kind = "price", Text = "Sell Price:", Copper = 10005 } });
                var gm = GtAll<MoneyText>(gt).SingleOrDefault();
                Check("tooltip: Sell Price shows 1 [gold] 5 [copper] with MoneyText in the game's white, no coloured dots", gm != null && gm.CoinOrder == "gc" && gm.Foreground == R["Brush.GameTip.Text"] && !GtAll<System.Windows.Shapes.Ellipse>(gt).Any());

                // ---- the game's own coins (read-only from the local install; skipped without one)
                var wow = WowLocator.Find();
                if (!string.IsNullOrEmpty(wow))
                {
                    var savedRoot = IconStore.WowRoot;
                    try
                    {
                        IconStore.WowRoot = () => wow; IconStore.ForgetMemory();
                        MoneyIcons.UseGameArt = true; MoneyIcons.Current.ResetForTest();
                        var img = MoneyIcons.NewImage('g', 12);
                        _ = MoneyIcons.Current.Gold; _ = MoneyIcons.Current.Silver; _ = MoneyIcons.Current.Copper;
                        for (int i = 0; i < 150 && !(MoneyIcons.Current.FromGame('g') && MoneyIcons.Current.FromGame('s') && MoneyIcons.Current.FromGame('c')); i++) await Task.Delay(100);
                        Check("game: the three coins are read from the game data (cached like every icon)", MoneyIcons.Current.FromGame('g') && MoneyIcons.Current.FromGame('s') && MoneyIcons.Current.FromGame('c')
                            && File.Exists(IconStore.PathFor(MoneyIcons.GoldFile)) && File.Exists(IconStore.PathFor(MoneyIcons.CopperFile)));
                        Check("game: an Image made before the coin arrived now shows the game's picture", img.Source is BitmapSource);
                        // what the pictures look like: 16 x 16, a yellow gold, a grey silver, a red-brown copper (the right files, not some other icon)
                        double[] Avg(ImageSource s)
                        {
                            var b = new FormatConvertedBitmap((BitmapSource)s, PixelFormats.Bgra32, null, 0);
                            var px = new byte[b.PixelWidth * b.PixelHeight * 4]; b.CopyPixels(px, b.PixelWidth * 4, 0);
                            double r = 0, g = 0, bl = 0, n = 0;
                            for (int i = 0; i < px.Length; i += 4) if (px[i + 3] > 200) { bl += px[i]; g += px[i + 1]; r += px[i + 2]; n++; }
                            return n == 0 ? new double[] { 0, 0, 0, 0 } : new[] { r / n, g / n, bl / n, ((BitmapSource)s).PixelWidth };
                        }
                        var ga = Avg(MoneyIcons.Current.Gold); var sa = Avg(MoneyIcons.Current.Silver); var ca = Avg(MoneyIcons.Current.Copper);
                        string F(double[] a) => string.Join("/", a.Select(x => ((int)x).ToString()));
                        Check("game: gold is yellow, silver grey, copper red-brown, all 16 x 16",
                            ga[3] == 16 && sa[3] == 16 && ca[3] == 16 && ga[0] > ga[2] + 60 && ga[1] > ga[2] + 40 && Math.Abs(sa[0] - sa[2]) < 30 && ca[0] > ca[1] + 25 && ca[1] > ca[2] - 10 && ca[0] < ga[0] + 40 && ca[1] < ga[1] - 20,
                            "gold " + F(ga) + " silver " + F(sa) + " copper " + F(ca));
                        // the file ids are the named files Interface\MoneyFrame\UI-*Icon.blp (the root's name hashes point at the same content)
                        Check("game: file ids " + MoneyIcons.GoldFile + " / " + MoneyIcons.SilverFile + " / " + MoneyIcons.CopperFile + " are UI-GoldIcon / UI-SilverIcon / UI-CopperIcon", SameAsNamed(wow), lastNameCheck);
                    }
                    catch (Exception e) { Check("game: reading the coins", false, e.Message); }
                    finally { IconStore.WowRoot = savedRoot; }
                }
                else notes.Add("ok   game: no WoW install here - the game's coins were not checked (the drawn coins were)");

                // ---- every place that shows money uses the coins: a scan of the source
                var src = FindSrcDir();
                if (src == null) notes.Add("ok   source scan: no source folder next to the exe - skipped");
                else
                {
                    var handRolled = ScanHandRolledMoney(src);
                    Check("source: no money split by hand outside MoneyFormat (copper / 10000, / 100 % 100, \"g \" glued to a number, coloured coin dots)", handRolled.Count == 0, string.Join(" ; ", handRolled.Take(6)));
                    var bare = ScanBareMoneyInUi(src);
                    Check("source: every money text a page shows goes through MoneyText (or is plain text on purpose: copy, test hooks)", bare.Count == 0, string.Join(" ; ", bare.Take(6)));
                    var xaml = File.ReadAllText(Path.Combine(src, "AtlasPage.xaml"));
                    Check("source: the Atlas rows draw money in detail, extra and the right-hand figure", new[] { "detail", "extra", "right" }.All(n => Regex.IsMatch(xaml, "x:Name=\"" + n + "\" local:MoneyText.Source=\"\\{Binding")));
                }

                // ---- a picture: drawn and (when there is a game) game coins at a few sizes
                if (SelfTestClock.ShotsOn)
                {
                    var sheet = new StackPanel { Background = (Brush)R["Brush.Surface1"], Margin = new Thickness(0) };
                    void Row(string label, FrameworkElement el) { var r = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 8, 16, 8) }; r.Children.Add(new TextBlock { Text = label, Width = 160, Foreground = (Brush)R["Brush.Text.Secondary"] }); r.Children.Add(el); sheet.Children.Add(r); }
                    Row("12345", new MoneyText(12345));
                    Row("123456789", new MoneyText(123456789));
                    Row("compact", new MoneyText(123456789, true));
                    Row("0", new MoneyText(0));
                    Row("-2550", new MoneyText(-2550));
                    Row("20 px", new MoneyText(12345) { FontSize = 20 });
                    Row("line", MoneyText.Coins(new TextBlock { Foreground = (Brush)R["Brush.Text.Primary"] }, "Sold by Kyra for 1g 5s · stack of 5"));
                    Row("tooltip", new GameTooltip("Frostbite Edge", 4).AddLines(new[] { new TipLine { Kind = "price", Text = "Sell Price:", Copper = 12345 } }));
                    var host = new Border { Child = sheet };
                    var w = new Window { Content = host, SizeToContent = SizeToContent.WidthAndHeight, ShowInTaskbar = false, Left = -4000, Top = -4000, WindowStyle = WindowStyle.None, ShowActivated = false };
                    w.Show(); await ShotWait(300);
                    SnapshotSheet(sheet, Path.Combine(dir, "money-coins.png"));
                    w.Close();
                }
            }
            catch (Exception e) { ok = false; notes.Add("crashed: " + e); }
            finally { MoneyIcons.UseGameArt = savedUse; MoneyIcons.Current.ResetForTest(); }
            return "money selftest: " + (ok ? "PASSED" : "FAILED") + "\r\n  " + string.Join("\r\n  ", notes);
        }

        string lastNameCheck = "";
        // the root's name-hash entries for the three paths have the same content keys as the three file ids
        bool SameAsNamed(string wow)
        {
            try
            {
                using (var casc = CascStorage.Open(wow, IconStore.Flavor))
                {
                    var byName = casc.ResolveNameHashes(new HashSet<ulong>(MoneyIcons.GamePaths.Select(CascStorage.NameHash)));
                    var byId = casc.ResolveFileIds(new HashSet<int> { MoneyIcons.GoldFile, MoneyIcons.SilverFile, MoneyIcons.CopperFile });
                    var ids = new[] { MoneyIcons.GoldFile, MoneyIcons.SilverFile, MoneyIcons.CopperFile };
                    bool all = true;
                    var sb = new List<string>();
                    for (int i = 0; i < 3; i++)
                    {
                        byName.TryGetValue(CascStorage.NameHash(MoneyIcons.GamePaths[i]), out var a);
                        byId.TryGetValue(ids[i], out var b);
                        bool same = a != null && b != null && a.SequenceEqual(b);
                        sb.Add(MoneyIcons.GamePaths[i] + (a == null ? " (no name)" : "") + (b == null ? " (no id)" : "") + (same ? " =" : " !="));
                        all &= same;
                    }
                    lastNameCheck = string.Join(", ", sb);
                    return all;
                }
            }
            catch (Exception e) { lastNameCheck = e.Message; return false; }
        }

        static string FindSrcDir()
        {
            var d = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            for (int i = 0; i < 6 && d != null; i++, d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "ElansAddonHub.csproj")) && File.Exists(Path.Combine(d.FullName, "AtlasPage.xaml"))) return d.FullName;
            return null;
        }

        static IEnumerable<string> SourceFiles(string src) => Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
                && !f.Contains(@"\bin\") && !f.Contains(@"\obj\") && !Regex.IsMatch(Path.GetFileName(f), @"^MainWindow\..*Test.*\.cs$"));

        // splitting copper into coins anywhere but MoneyFormat
        static List<string> ScanHandRolledMoney(string src)
        {
            var rx = new[]
            {
                new Regex(@"(copper|money|price|cost|amount)\w*\s*/\s*10000\b", RegexOptions.IgnoreCase),
                new Regex(@"/\s*100\s*%\s*100\b"),
                new Regex(@"\}g \{|\}s \{|""\s*\+\s*""g""|\+\s*""g ""|\+\s*""g""\)|\+\s*""s ""|\+\s*""c""\)"),
                new Regex(@"Ellipse[^;]*(GameTip\.(Gold|Silver|Copper)|Brush\.Warn)[^;]*;.*$"),
            };
            var hits = new List<string>();
            foreach (var f in SourceFiles(src))
            {
                var name = f.Substring(src.Length).TrimStart('\\');
                if (name == @"Services\MoneyFormat.cs") continue;
                int n = 0;
                foreach (var line in File.ReadLines(f))
                {
                    n++;
                    var t = line.Trim();
                    if (t.StartsWith("//")) continue;
                    if (rx.Any(r => r.IsMatch(line))) hits.Add(name + ":" + n + ": " + t.Substring(0, Math.Min(120, t.Length)));
                }
            }
            return hits;
        }

        // a money formatter called in a page (not in a service, whose strings the pages draw through MoneyText) on a line that does not draw coins:
        // allowed only where the text stays plain on purpose (listed with the reason)
        static readonly string[] PlainOnPurpose =
        {
            "charTabInfo.Add(\"gold|\"",                       // test hook of the Characters overview
            "InventoryReader.Money(Char.Money),",              // TipLines(): the tooltip's words for tests; the tooltip itself draws MoneyText
            "static string TotalMoneyText(",                   // the words; Summary draws them with MoneyText.Coins
            "var money = InventoryReader.Money(g.Money);",     // the words; ViewInfo draws them with MoneyText.Coins
            "case \"price\": Add(\"Sells for \"",              // a hero chip; the chip's TextBlock goes through MoneyText.Coins
            "var total = (g.TotalApprox ? \"about \" : \"\")", // the words; the summary line draws them with MoneyText.Coins
            "var price = p.Known ?",                           // the words; the tooltip's muted line goes through MoneyText.Coins
        };
        static List<string> ScanBareMoneyInUi(string src)
        {
            var call = new Regex(@"\b(AtlasFormat\.Money|InventoryReader\.Money|ProfMoney\.Format|LiveSources\.Money|UpgradeSource\.Money|MoneyFormat\.Text|ItemText\.Coins)\s*\(");
            var hits = new List<string>();
            foreach (var f in SourceFiles(src))
            {
                var name = f.Substring(src.Length).TrimStart('\\');
                if (name.StartsWith(@"Services\", StringComparison.OrdinalIgnoreCase) || name.StartsWith(@"Lodge\Lodge", StringComparison.Ordinal) && name.EndsWith("Models.cs", StringComparison.Ordinal)) continue;
                if (name == @"Controls\MoneyText.cs") continue;
                int n = 0;
                foreach (var line in File.ReadLines(f))
                {
                    n++;
                    var t = line.Trim();
                    if (t.StartsWith("//") || !call.IsMatch(line) || line.Contains("MoneyText")) continue;
                    if (PlainOnPurpose.Any(p => line.Contains(p))) continue;
                    hits.Add(name + ":" + n + ": " + t.Substring(0, Math.Min(120, t.Length)));
                }
            }
            return hits;
        }
    }
}
