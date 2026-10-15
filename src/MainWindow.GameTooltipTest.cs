using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ellipse = System.Windows.Shapes.Ellipse;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Db2;

namespace ElansAddonHub
{
    // --selftest "gametooltip": the GameTooltip control (item and spell tooltips drawn like the game's), its colours and line order, the footer, the Atlas kinds
    // (creature, object, quest, spell, zone, instance, faction), the font (name-table check, never shipped) and that the surfaces use it.
    // Nothing here reads the game except the last optional check (read-only, skipped without a WoW install).
    public partial class MainWindow
    {
        // (text, colour) of every text in a tooltip, in order
        static List<KeyValuePair<string, string>> GtColored(DependencyObject root)
        {
            var res = new List<KeyValuePair<string, string>>();
            void Walk(object o)
            {
                if (o is TextBlock tb) res.Add(new KeyValuePair<string, string>(tb.Text, tb.Foreground is SolidColorBrush b ? b.Color.ToString() : "?"));
                else if (o is Panel p) foreach (var ch in p.Children) Walk(ch);
                else if (o is Border bd) Walk(bd.Child);
            }
            Walk(root);
            return res;
        }

        static List<T> GtAll<T>(DependencyObject root) where T : DependencyObject
        {
            var res = new List<T>();
            void Walk(DependencyObject d)
            {
                if (d == null) return;
                if (d is T t) res.Add(t);
                int n = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetChildrenCount(d) : 0;
                if (n == 0) { foreach (var c in LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>()) Walk(c); }
                else for (int i = 0; i < n; i++) Walk(VisualTreeHelper.GetChild(d, i));
            }
            Walk(root);
            return res;
        }

        static string GtColor(List<KeyValuePair<string, string>> l, string text) => l.FirstOrDefault(x => x.Key == text).Value;

        static byte[] GtFakeFont(string family)
        {
            // a minimal sfnt: header, one 'name' table with one record (platform 3, name id 1)
            var name = System.Text.Encoding.BigEndianUnicode.GetBytes(family);
            var t = new List<byte>();
            void U16(int v) { t.Add((byte)(v >> 8)); t.Add((byte)v); }
            void U32(uint v) { t.Add((byte)(v >> 24)); t.Add((byte)(v >> 16)); t.Add((byte)(v >> 8)); t.Add((byte)v); }
            U32(0x00010000); U16(1); U16(16); U16(0); U16(0);
            U32(0x6E616D65); U32(0); U32(28); U32((uint)(18 + name.Length));      // tag, checksum, offset, length
            U16(0); U16(1); U16(18);                                              // format, count, string offset
            U16(3); U16(1); U16(0x409); U16(1); U16(name.Length); U16(0);        // platform, encoding, language, name id, length, offset
            t.AddRange(name);
            return t.ToArray();
        }

        async Task<string> GameTooltipTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            void Check(string what, bool cond, string detail = null) { SelfTestClock.Mark(what); notes.Add((cond ? "ok   " : "FAIL ") + what + (!cond && detail != null ? " [" + detail + "]" : "")); ok &= cond; }
            string P(string n) => Path.Combine(dir, n);
            try
            {
                // ---- real font first (optional): the screenshots below then show it
                var real = Environment.GetEnvironmentVariable("ELANSHUB_WOW");
                if (string.IsNullOrEmpty(real)) real = WowLocator.Find();
                var savedRoot = ItemData.WowRoot;

                // ---- tokens and the panel
                string Hex(string key) => (Application.Current.TryFindResource(key) as SolidColorBrush)?.Color.ToString();
                Check("theme: the game's colours are tokens (Brush.GameTip.*): background navy at ~95 %, hairline grey-blue, gold, green, red, qualities",
                    Hex("Brush.GameTip.Bg") == "#F2070C1F" && Hex("Brush.GameTip.Border") == "#FF5A6B85" && Hex("Brush.GameTip.Gold") == "#FFFFD100" && Hex("Brush.GameTip.Green") == "#FF1EFF00" && Hex("Brush.GameTip.Red") == "#FFFF2020"
                    && Hex("Brush.GameTip.Q0") == "#FF9D9D9D" && Hex("Brush.GameTip.Q2") == "#FF1EFF00" && Hex("Brush.GameTip.Q3") == "#FF0070DD" && Hex("Brush.GameTip.Q4") == "#FFA335EE" && Hex("Brush.GameTip.Q5") == "#FFFF8000" && Hex("Brush.GameTip.Q6") == "#FFE6CC80" && Hex("Brush.GameTip.Q7") == "#FF00CCFF");
                var weapon = new GameTooltip("Frostbite Edge", 4)
                    .AddLines(new[]
                    {
                        new TipLine { Kind = "ilvl", Text = "Item Level 60" }, new TipLine { Kind = "bind", Text = "Binds when equipped" }, new TipLine { Kind = "slot", Text = "One-Hand", Right = "Sword" },
                        new TipLine { Kind = "damage", Text = "62 - 116 Damage" }, new TipLine { Kind = "speed", Text = "Speed 2.60" }, new TipLine { Kind = "dps", Text = "(34.3 damage per second)" },
                        new TipLine { Kind = "stat", Text = "+9 Agility" }, new TipLine { Kind = "stat2", Text = "+14 Attack Power" }, new TipLine { Kind = "req", Text = "Requires Level 55", Level = 55 },
                        new TipLine { Kind = "effect", Text = "Chance on hit: Blasts the target with frost." }, new TipLine { Kind = "desc", Text = "\"It is very cold.\"" }, new TipLine { Kind = "price", Text = "Sell Price:", Copper = 12345 },
                    });
                var wc = GtColored(weapon);
                Check("panel: near-black navy background, grey-blue 1 px border, radius 4, max width 320, wrapping text", ((SolidColorBrush)weapon.Background).Color.ToString() == "#F2070C1F" && ((SolidColorBrush)weapon.BorderBrush).Color.ToString() == "#FF5A6B85" && weapon.BorderThickness.Left == 1 && weapon.CornerRadius.TopLeft == 4 && weapon.MaxWidth == 320);
                Check("weapon: the order is name, Item Level, binding, slot | type, damage | speed, dps, stats, requirement, effect, flavour text, price",
                    string.Join(" / ", wc.Select(x => x.Key)) == "Frostbite Edge / Item Level 60 / Binds when equipped / One-Hand / Sword / 62 - 116 Damage / Speed 2.60 / (34.3 damage per second) / +9 Agility / +14 Attack Power / Requires Level 55 / Chance on hit: Blasts the target with frost. / \"It is very cold.\" / Sell Price: / 1 / 23 / 45", string.Join(" / ", wc.Select(x => x.Key)));
                Check("weapon colours: epic name, gold Item Level, white binding / slot / damage / base stat / requirement, green bonus stat and effect, gold flavour text",
                    GtColor(wc, "Frostbite Edge") == "#FFA335EE" && GtColor(wc, "Item Level 60") == "#FFFFD100" && GtColor(wc, "Binds when equipped") == "#FFFFFFFF" && GtColor(wc, "One-Hand") == "#FFFFFFFF" && GtColor(wc, "62 - 116 Damage") == "#FFFFFFFF"
                    && GtColor(wc, "+9 Agility") == "#FFFFFFFF" && GtColor(wc, "+14 Attack Power") == "#FF1EFF00" && GtColor(wc, "Requires Level 55") == "#FFFFFFFF" && GtColor(wc, "Chance on hit: Blasts the target with frost.") == "#FF1EFF00" && GtColor(wc, "\"It is very cold.\"") == "#FFFFD100");
                var damageRow = weapon.Body.Children.OfType<Grid>().FirstOrDefault(g => g.Children.OfType<TextBlock>().Any(t => t.Text == "62 - 116 Damage"));
                Check("weapon: damage on the left and speed on the right of ONE row; slot likewise (two columns)", damageRow != null && damageRow.Children.OfType<TextBlock>().Any(t => t.Text == "Speed 2.60" && Grid.GetColumn(t) == 1)
                    && weapon.Body.Children.OfType<Grid>().Any(g => g.Children.OfType<TextBlock>().Any(t => t.Text == "One-Hand") && g.Children.OfType<TextBlock>().Any(t => t.Text == "Sword" && Grid.GetColumn(t) == 1)));
                var price = weapon.Body.Children.OfType<StackPanel>().Last();
                var coins = price.Children.OfType<Ellipse>().Select(e => ((SolidColorBrush)e.Fill).Color.ToString()).ToList();
                Check("price: 'Sell Price:' with 1g 23s 45c as three coin glyphs (gold, silver, copper)", coins.Count == 3 && coins[0] == "#FFFFD100" && coins[1] == "#FFC0C7D0" && coins[2] == "#FFC8764A");
                Check("no app accent inside: every colour of the tooltip is one of the game's tokens", wc.All(x => new[] { "#FFFFFFFF", "#FFFFD100", "#FF1EFF00", "#FFA335EE", "#FF9D9D9D", "#FFFF2020" }.Contains(x.Value)), string.Join(",", wc.Select(x => x.Value).Distinct()));
                await ShotWait(100);
                SnapshotSheet(Gt(weapon), P("gt-weapon.png"));

                // ---- armor with a set
                var armor = new GameTooltip("Battlegear Chestguard", 4)
                    .AddLines(new[]
                    {
                        new TipLine { Kind = "ilvl", Text = "Item Level 66" }, new TipLine { Kind = "bind", Text = "Binds when picked up" }, new TipLine { Kind = "slot", Text = "Chest", Right = "Plate" }, new TipLine { Kind = "armor", Text = "1011 Armor" },
                        new TipLine { Kind = "stat", Text = "+25 Strength" }, new TipLine { Kind = "stat", Text = "+20 Stamina" }, new TipLine { Kind = "req", Text = "Requires Level 60", Level = 60 },
                        new TipLine { Kind = "setname", Text = "Battlegear (2/3)" }, new TipLine { Kind = "setitem-worn", Text = "Battle Helm" }, new TipLine { Kind = "setitem-worn", Text = "Battlegear Chestguard" }, new TipLine { Kind = "setitem", Text = "Battle Boots" },
                        new TipLine { Kind = "setbonus-on", Text = "(2) Set: +20 Attack Power." }, new TipLine { Kind = "setbonus", Text = "(3) Set: Increases your hit rating by 12." }, new TipLine { Kind = "price", Text = "Sell Price:", Copper = 250000 },
                    });
                var ac = GtColored(armor);
                Check("armor with a set: set name gold, worn pieces white, missing piece grey, reached bonus green, the other grey; armor line white",
                    GtColor(ac, "Battlegear (2/3)") == "#FFFFD100" && GtColor(ac, "Battle Helm") == "#FFFFFFFF" && GtColor(ac, "Battle Boots") == "#FF9D9D9D" && GtColor(ac, "(2) Set: +20 Attack Power.") == "#FF1EFF00" && GtColor(ac, "(3) Set: Increases your hit rating by 12.") == "#FF9D9D9D" && GtColor(ac, "1011 Armor") == "#FFFFFFFF");
                SnapshotSheet(Gt(armor), P("gt-armor-set.png"));

                // ---- a consumable with Use:, a poor item, a recipe
                var potion = new GameTooltip("Superior Healing Potion", 1).AddLines(new[] { new TipLine { Kind = "slot", Text = "Consumable" }, new TipLine { Kind = "req", Text = "Requires Level 35", Level = 35 }, new TipLine { Kind = "effect", Text = "Use: Restores 900 to 1500 health." }, new TipLine { Kind = "desc", Text = "\"Tastes of berries.\"" } });
                var pc = GtColored(potion);
                Check("consumable: white name, 'Use:' line green, flavour text gold", GtColor(pc, "Superior Healing Potion") == "#FFFFFFFF" && GtColor(pc, "Use: Restores 900 to 1500 health.") == "#FF1EFF00" && GtColor(pc, "\"Tastes of berries.\"") == "#FFFFD100");
                SnapshotSheet(Gt(potion), P("gt-consumable.png"));
                var junk = new GameTooltip("Broken Fang", 0).AddText("Miscellaneous", "slot");
                Check("a poor item: the name is grey (#9D9D9D)", GtColor(GtColored(junk), "Broken Fang") == "#FF9D9D9D");
                Check("quality colours: uncommon green, rare blue, legendary orange, artifact, heirloom; unknown quality = white",
                    GtColor(GtColored(new GameTooltip("a", 2)), "a") == "#FF1EFF00" && GtColor(GtColored(new GameTooltip("a", 3)), "a") == "#FF0070DD" && GtColor(GtColored(new GameTooltip("a", 5)), "a") == "#FFFF8000"
                    && GtColor(GtColored(new GameTooltip("a", 6)), "a") == "#FFE6CC80" && GtColor(GtColored(new GameTooltip("a", 7)), "a") == "#FF00CCFF" && GtColor(GtColored(new GameTooltip("a", -1)), "a") == "#FFFFFFFF");
                // a recipe: the item it makes, then the reagents (Reagents: white lines)
                var recipe = new GameTooltip("Elixir of Testing", 1).AddText("Consumable", "slot").AddText("Use: Increases your test score by 10.", "effect").AddText("Reagents:", "plain").AddText("2 x Test Herb", "plain");
                Check("recipe: item lines, then 'Reagents:' and the reagents in white", string.Join(" / ", recipe.Texts()) == "Elixir of Testing / Consumable / Use: Increases your test score by 10. / Reagents: / 2 x Test Herb" && GtColor(GtColored(recipe), "2 x Test Herb") == "#FFFFFFFF");

                // ---- requirement: red when the character is below it
                string ReqColor(int playerLevel, int need) => GtColor(GtColored(new GameTooltip("x", 1, null, playerLevel).AddLines(new[] { new TipLine { Kind = "req", Text = "Requires Level " + need, Level = need } })), "Requires Level " + need);
                Check("requirement: red (#FF2020) when the chosen character is below it, white when it meets it, white without a character", ReqColor(40, 55) == "#FFFF2020" && ReqColor(55, 55) == "#FFFFFFFF" && ReqColor(60, 55) == "#FFFFFFFF" && ReqColor(0, 55) == "#FFFFFFFF");

                // ---- spell variant
                var fireball = new SpellView { Id = 133, Name = "Fireball", Rank = "Rank 1", Description = "Hurls a fiery ball that causes 20 Fire damage.", CastMs = 1500, RangeMax = 35 };
                var spell = new GameTooltip(fireball.Name, 1, fireball.Rank).AddSpell(fireball);
                var sc = GtColored(spell);
                Check("spell: white name, grey rank on the right, range, cast time, description in gold",
                    string.Join(" / ", sc.Select(x => x.Key)) == "Fireball / Rank 1 / 35 yd range / 1.5 sec cast / Hurls a fiery ball that causes 20 Fire damage." && GtColor(sc, "Fireball") == "#FFFFFFFF" && GtColor(sc, "Rank 1") == "#FF9D9D9D" && GtColor(sc, "Hurls a fiery ball that causes 20 Fire damage.") == "#FFFFD100"
                    && spell.Body.Children.OfType<Grid>().First().Children.OfType<TextBlock>().Any(t => t.Text == "Rank 1" && Grid.GetColumn(t) == 1));
                Check("spell: an instant spell says 'Instant'; no range, no range line", GtColored(new GameTooltip("Slam", 1).AddSpell(new SpellView { Name = "Slam", CastMs = 0, Description = "Slams." })).Select(x => x.Key).SequenceEqual(new[] { "Slam", "Instant", "Slams." }));
                SnapshotSheet(Gt(spell), P("gt-spell.png"));

                // ---- footer: the Outpost's own part sits below the game's, separated, muted
                var withFooter = new GameTooltip("Frostbite Edge", 4).AddText("Item Level 60", "ilvl");
                Check("footer: none until used (no empty separator)", !withFooter.HasFooter && withFooter.Body.Children.OfType<Border>().All(b => b.Height != 1));
                withFooter.Footer.Children.Add(GameTooltip.Muted("You have 42 across characters"));
                var kids = withFooter.Body.Children.Cast<UIElement>().ToList();
                var sepIdx = kids.FindIndex(k => k is Border b && b.Height == 1);
                Check("footer: a hairline, then the muted text, after every game line", sepIdx == kids.Count - 2 && kids.Last() == withFooter.Footer && withFooter.Footer.Children[0] is TextBlock ft && ((SolidColorBrush)ft.Foreground).Color.ToString() == "#FF8C9AB5");
                Check("icon outside: WithIcon puts a 56 px framed icon to the LEFT of the panel", GameTooltip.WithIcon(new Border(), weapon) is StackPanel sp && sp.Orientation == Orientation.Horizontal && sp.Children[0] is Border fr && fr.Width == 56 && fr.Height == 56 && sp.Children[1] == weapon);

                // ---- the hover icon (2.50.2): left of the panel, outside it, drawn by the ToolTip style; none unless asked for
                var plainTip = new GameTooltip("Plain", 1);
                var iconTip = new GameTooltip("With Icon", 2);
                iconTip.Icon = new System.Windows.Media.Imaging.WriteableBitmap(4, 4, 96, 96, PixelFormats.Bgra32, null);
                Check("hover icon: none by default (no frame), present after Icon is set; the panel itself stays the same size either way", !plainTip.HasIcon && plainTip.Icon == null && plainTip.IconFileId == 0 && iconTip.HasIcon && iconTip.Icon != null && iconTip.MaxWidth == 320);
                Check("hover icon: SetIcon with no file id (0) changes nothing", new GameTooltip("x", 1).SetIcon(0).Icon == null && new GameTooltip("x", 1).SetItemIcon(0).IconFileId == 0 && new GameTooltip("x", 1).SetSpellIcon(-5).IconFileId == 0);
                FrameworkElement FrameOf(GameTooltip g) { var tt = GameTooltip.Wrap(g); tt.ApplyTemplate(); return tt.Template.FindName("IconFrame", tt) as FrameworkElement; }
                var withF = FrameOf(iconTip); var withoutF = FrameOf(plainTip);
                Check("hover icon: the ToolTip style draws a 40 px frame left of the panel (game dark border) with an icon, and nothing without",
                    withF != null && withF.Visibility == Visibility.Visible && withF.Width == 40 && withF.Height == 40 && ((Border)withF).BorderBrush is SolidColorBrush ib && ib.Color.ToString() == "#FF1B2438" && withoutF != null && withoutF.Visibility == Visibility.Collapsed);
                var hostTT = GameTooltip.Wrap(iconTip); hostTT.ApplyTemplate();
                Check("hover icon: the frame and the panel sit in one horizontal row, the frame first", hostTT.Template.FindName("IconFrame", hostTT) is Border fb && VisualTreeHelper.GetParent(fb) is StackPanel hp && hp.Orientation == Orientation.Horizontal && hp.Children.IndexOf(fb) == 0 && hp.Children.Count == 2);

                // ---- the Atlas kinds
                AtlasPageModel M(string kind, string title, params AtlasFact[] facts) { var m = new AtlasPageModel { Id = kind + ":1", Kind = kind, Title = title }; m.Facts.AddRange(facts); return m; }
                var wolf = M("npc", "Rampaging Wolf", new AtlasFact("Level", "23", "x"), new AtlasFact("Rank", "Elite", "x"), new AtlasFact("Type", "Beast", "x"), new AtlasFact("Faction", "Wolves (hostile to the Alliance, hostile to the Horde)", "x"), new AtlasFact("Family", "Wolf", "x"), new AtlasFact("Found in", "Greenvale", "x", "zone:11"));
                var wp = AtlasTip.For(wolf);
                Check("creature: hostile = red name; 'Level 23 Elite Beast'; the faction in grey; the rest (family) in the footer; a linked fact stays out of the footer", wp.Tone == "hostile" && wp.Lines.Any(l => l.Text == "Level 23 Elite Beast") && wp.Lines.Any(l => l.Kind == "grey" && l.Text.StartsWith("Wolves")) && wp.Footer.SequenceEqual(new[] { "Family: Wolf" }), string.Join(" | ", wp.Lines) + " // " + string.Join(" | ", wp.Footer));
                var wolfTip = GameTooltip.FromAtlas(wolf);
                Check("creature: the drawn title is red", GtColor(GtColored(wolfTip), "Rampaging Wolf") == "#FFFF2020");
                var vendor = M("npc", "Gilda the Smith", new AtlasFact("Title", "<Weapon Merchant>", "x"), new AtlasFact("Level", "30", "x"), new AtlasFact("Rank", "Normal", "x"), new AtlasFact("Type", "Humanoid", "x"), new AtlasFact("Faction", "Stormwind (friendly to the Alliance, hostile to the Horde)", "x"), new AtlasFact("Services", "Vendor, Repair", "x"));
                var vp = AtlasTip.For(vendor);
                Check("creature: a mixed reaction is a white name; '<Weapon Merchant>' under it; 'Level 30 Humanoid' (no 'Normal'); services in the footer", vp.Tone == "white" && vp.Lines[0].Text == "<Weapon Merchant>" && vp.Lines[1].Text == "Level 30 Humanoid" && vp.Footer.Contains("Services: Vendor, Repair"));
                Check("creature: friendly to both = green, neutral = yellow", AtlasTip.Reaction("Moonglade (friendly to the Alliance, friendly to the Horde)") == "friendly" && AtlasTip.Reaction("Critters") == "neutral" && AtlasTip.Reaction(null) == "" && GameTooltip.ToneBrush("neutral", 0) is SolidColorBrush yb && yb.Color.ToString() == "#FFFFFF00");
                var boss = M("npc", "Gnarlfang", new AtlasFact("Rank", "Boss", "x"), new AtlasFact("Type", "Beast", "x"));
                Check("creature: a boss without a known level reads 'Level ?? Boss Beast'; nothing is invented when the type is unknown", AtlasTip.For(boss).Lines[0].Text == "Level ?? Boss Beast" && AtlasTip.For(M("npc", "Mystery")).Lines.Count == 0);
                var obj = M("obj", "Peacebloom", new AtlasFact("Kind", "gathering node", "x"));
                Check("object: white name, the kind line capitalised, nothing about lock skills it does not know", AtlasTip.For(obj).Tone == "white" && AtlasTip.For(obj).Lines.Single().Text == "Gathering node");
                var qm = M("quest", "Wolves at the Gate", new AtlasFact("Level", "12", "x"), new AtlasFact("Requires level", "9", "x"), new AtlasFact("Zone", "Greenvale", "x"));
                qm.Texts.Add(new AtlasText("Objectives", "Kill 8 wolves.", "x")); qm.Texts.Add(new AtlasText("Description", "The gate is not safe.", "x"));
                var rw = new AtlasSection { Key = "rewards", Title = "Rewards" }; rw.Rows.Add(new AtlasRow { Title = "Wolfskin Cloak", Quality = 2 }); qm.Sections.Add(rw);
                var qp = AtlasTip.For(qm);
                Check("quest: gold title; 'Level 12', 'Requires Level 9', objectives in white, 'You will receive:' and the rewards in their quality colour; zone and description in the footer",
                    qp.Tone == "gold" && string.Join(" / ", qp.Lines.Select(l => l.Text)) == "Level 12 / Requires Level 9 / Kill 8 wolves. / You will receive: / Wolfskin Cloak" && qp.Lines.Single(l => l.Kind == "req").Level == 9 && qp.Footer.SequenceEqual(new[] { "Zone: Greenvale", "The gate is not safe." }), string.Join(" / ", qp.Lines.Select(l => l.Text)) + " // " + string.Join(" | ", qp.Footer));
                var qTip = GameTooltip.FromAtlas(qm, 5);
                Check("quest: a requirement above the character's level is red; the reward is green (uncommon)", GtColor(GtColored(qTip), "Requires Level 9") == "#FFFF2020" && GtColor(GtColored(qTip), "Wolfskin Cloak") == "#FF1EFF00" && GtColor(GtColored(qTip), "Wolves at the Gate") == "#FFFFD100");
                var spm = M("spell", "Fireball", new AtlasFact("Rank", "Rank 1", "x"), new AtlasFact("Cast time", "1.5 s", "x"), new AtlasFact("Range", "35 yards", "x"), new AtlasFact("Talent of", "Mage", "x", "talents:tree/mage"));
                spm.Texts.Add(new AtlasText("Description", "Hurls a fiery ball.", "x"));
                var spp = AtlasTip.For(spm);
                Check("spell page: rank on the right, '35 yd range', '1.5 s cast', description in gold", spp.Rank == "Rank 1" && string.Join(" / ", spp.Lines.Select(l => l.Text)) == "35 yd range / 1.5 s cast / Hurls a fiery ball." && spp.Lines.Last().Kind == "spelldesc");
                var zone = M("zone", "Greenvale", new AtlasFact("Type", "Zone", "x"), new AtlasFact("In", "Eastern Kingdoms", "x", "zone:1"), new AtlasFact("Creature levels", "10-20", "x"));
                var zp = AtlasTip.For(zone);
                Check("zone: gold name; 'Level 10-20', the type line", zp.Tone == "gold" && string.Join(" / ", zp.Lines.Select(l => l.Text)) == "Level 10-20 / Zone / Eastern Kingdoms");
                var inst = M("instance", "Deadmines", new AtlasFact("Type", "Dungeon", "x"), new AtlasFact("Players", "5", "x"), new AtlasFact("Creature levels", "17-26", "x"));
                var raid = M("instance", "Molten Core", new AtlasFact("Type", "Raid", "x"), new AtlasFact("Players", "40", "x"));
                Check("instance: gold name; 'Dungeon (5 players)' and 'Raid (40 players)'; the level only when the data has one", AtlasTip.For(inst).Lines.Select(l => l.Text).SequenceEqual(new[] { "Level 17-26", "Dungeon (5 players)" }) && AtlasTip.For(raid).Lines.Select(l => l.Text).SequenceEqual(new[] { "Raid (40 players)" }));
                var fm = M("faction", "Stormwind"); fm.Texts.Add(new AtlasText("About", "The human capital.", "x"));
                Check("faction: gold name, the description in white", AtlasTip.For(fm).Tone == "gold" && AtlasTip.For(fm).Lines.Single().Text == "The human capital." && GtColor(GtColored(GameTooltip.FromAtlas(fm)), "The human capital.") == "#FFFFFFFF" && GtColor(GtColored(GameTooltip.FromAtlas(fm)), "Stormwind") == "#FFFFD100");
                SnapshotSheet(Gt(wolfTip), P("gt-npc.png"));
                SnapshotSheet(Gt(qTip), P("gt-quest.png"));
                SnapshotSheet(Gt(GameTooltip.FromAtlas(inst)), P("gt-instance.png"));

                // ---- the font
                var fake = GtFakeFont("Friz Quadrata TT");
                Check("font: the name table of a font is read (family 'Friz Quadrata TT'); bytes that are no font are refused", GameFont.FamilyName(fake) == "Friz Quadrata TT" && GameFont.IsFriz(fake) && GameFont.FamilyName(new byte[] { 1, 2, 3 }) == null && !GameFont.IsFriz(GtFakeFont("Arial Narrow")) && !GameFont.IsFriz(null));
                var fdir = Path.Combine(dir, "gamefont"); Directory.CreateDirectory(fdir);
                File.WriteAllBytes(Path.Combine(fdir, GameFont.FileName), GtFakeFont("Arial Narrow"));
                Check("font: a file that is not Friz Quadrata is never used (the system fallback stays)", !GameFont.UseFolder(fdir, "test") && GameFont.Source == "system font" && GameFont.Family.Source.Contains("Palatino"));
                if (!string.IsNullOrEmpty(real) && Directory.Exists(Path.Combine(real, "Data", "data")) && Directory.Exists(Path.Combine(real, "_classic_beta_")))
                {
                    try
                    {
                        ItemData.WowRoot = () => real;
                        ElansAddonHub.Services.Maps.MapData.Flavor = () => "_classic_beta_";
                        GameFont.Start();
                        var cached = Path.Combine(GameFont.CacheDir, GameFont.FileName);
                        Check("real game files: Friz Quadrata (FileDataID 615960) is read from the game files into <data>\\cache\\fonts and used", File.Exists(cached) && GameFont.IsFriz(File.ReadAllBytes(cached)) && GameFont.Source.StartsWith("game font"), GameFont.Source);
                        notes.Add("note font: " + GameFont.Source);
                        var again = new GameTooltip("Frostbite Edge", 4);
                        Check("real game files: a tooltip made after that uses the game's font family", again.GetValue(System.Windows.Documents.TextElement.FontFamilyProperty) is FontFamily ff && ff.Source != null && ff.Source.Contains("Friz Quadrata"), (again.GetValue(System.Windows.Documents.TextElement.FontFamilyProperty) as FontFamily)?.Source);
                        SnapshotSheet(Gt(new GameTooltip("Frostbite Edge", 4).AddLines(new[] { new TipLine { Kind = "ilvl", Text = "Item Level 60" }, new TipLine { Kind = "bind", Text = "Binds when equipped" }, new TipLine { Kind = "slot", Text = "One-Hand", Right = "Sword" }, new TipLine { Kind = "stat", Text = "+9 Agility" }, new TipLine { Kind = "desc", Text = "\"It is very cold.\"" } })), P("gt-weapon-realfont.png"));
                    }
                    finally { ItemData.WowRoot = savedRoot; GameFont.UseSystem(); }
                }
                else notes.Add("skip real game files: no WoW install with the Forever beta client on this PC (the font stays the system fallback)");
            }
            catch (Exception e)
            {
                notes.Add("FAIL gametooltip test crashed: " + e);
                ok = false;
            }
            notes.Insert(0, "gametooltip (2.50.1): " + (ok ? "all ok" : "FAILURES"));
            return string.Join("\r\n", notes);
        }

        // a tooltip laid out and ready for a screenshot
        static FrameworkElement Gt(GameTooltip t)
        {
            t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            t.Arrange(new Rect(t.DesiredSize));
            return t;
        }
    }
}
