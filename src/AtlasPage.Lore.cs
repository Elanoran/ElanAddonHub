using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;

namespace ElansAddonHub
{
    // "About <zone>" on a zone page's Overview: the Outpost's own notes on the zone (ZoneLore). A Surface1 card: the title (Text.Section) with the
    // "Outpost notes" source chip at the right, then at reading width (ReadWidth) the facts line (levels · faction), the summary, "Starts here"
    // (the area and its races; a race links to its page when the game has it), the history, "Notable" (creatures linking to their pages, with
    // why they matter; the ones no id was found for are plain text), "Worth knowing" bullets, "On Forever" and a quiet line of sources that open in
    // the browser. Zones without notes show nothing new.
    public partial class AtlasPage
    {
        FrameworkElement loreCard;
        public FrameworkElement LoreCardForTest => loreCard;

        FrameworkElement LoreCard(AtlasPageModel m)
        {
            var e = m.Lore;
            var outer = new StackPanel();
            var head = new DockPanel();
            var chip = SourceChip(ZoneLore.Label); chip.VerticalAlignment = VerticalAlignment.Center; chip.Tag = "lore-label";
            DockPanel.SetDock(chip, Dock.Right); head.Children.Add(chip);
            var title = Tb("About " + m.Title, "Text.Section"); title.TextTrimming = TextTrimming.CharacterEllipsis;
            head.Children.Add(title);
            outer.Children.Add(head);

            var sp = new StackPanel { MaxWidth = ReadWidth, HorizontalAlignment = HorizontalAlignment.Left };
            outer.Children.Add(sp);
            var facts = e.FactsLine;
            if (facts.Length > 0) sp.Children.Add(Tagged(Tb(facts, "Text.Secondary", null, 2), "lore-facts"));
            if (e.Summary.Length > 0) sp.Children.Add(Tagged(Para(e.Summary, 12), "lore-summary"));

            if (e.Starts.Count > 0)
            {
                sp.Children.Add(Sub("Starts here"));
                foreach (var s in e.Starts)
                {
                    var row = new WrapPanel { Margin = new Thickness(0, 4, 0, 0), Tag = "lore-start" };
                    var area = Tb(s.Area, "Text.Body"); area.VerticalAlignment = VerticalAlignment.Center; area.Margin = new Thickness(0, 0, 8, 4);
                    row.Children.Add(area);
                    foreach (var r in s.Races) row.Children.Add(RaceChip(r));
                    sp.Children.Add(row);
                    if (s.Note.Length > 0) { var n = Tb(s.Note, "Text.Secondary"); n.TextWrapping = TextWrapping.Wrap; sp.Children.Add(n); }
                }
            }
            if (e.History.Length > 0) { sp.Children.Add(Sub("History")); sp.Children.Add(Tagged(Para(e.History, 4), "lore-history")); }

            if (e.Mobs.Count > 0)
            {
                sp.Children.Add(Sub("Notable"));
                var g = new Grid { Margin = new Thickness(0, 4, 0, 0), Tag = "lore-mobs" };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MaxWidth = 240 });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                foreach (var mob in e.Mobs)
                {
                    g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    int row = g.RowDefinitions.Count - 1;
                    var name = mob.Linked ? LoreLink(mob.Name, mob.Link, "Open " + mob.Name) : Tb(mob.Name, "Text.Body");
                    name.TextWrapping = TextWrapping.Wrap; name.Margin = new Thickness(0, row == 0 ? 0 : 4, 12, 0);
                    if (!mob.Linked) name.ToolTip = "Not found in the game data, so there is no page for it";
                    Grid.SetRow(name, row); g.Children.Add(name);
                    if (mob.Why.Length > 0)
                    {
                        var why = Tb(mob.Why, "Text.Secondary"); why.TextWrapping = TextWrapping.Wrap; why.Margin = new Thickness(0, row == 0 ? 0 : 4, 0, 0); why.VerticalAlignment = VerticalAlignment.Center;
                        Grid.SetRow(why, row); Grid.SetColumn(why, 1); g.Children.Add(why);
                    }
                }
                sp.Children.Add(g);
            }

            if (e.Mentions.Count > 0)
            {
                sp.Children.Add(Sub("Worth knowing"));
                var g = new Grid { Margin = new Thickness(0, 4, 0, 0), Tag = "lore-mentions" };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                foreach (var t in e.Mentions)
                {
                    g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    int row = g.RowDefinitions.Count - 1;
                    var dot = Tb("•", "Text.Body", Res("Brush.Text.Tertiary"), row == 0 ? 0 : 4);
                    Grid.SetRow(dot, row); g.Children.Add(dot);
                    var x = Tb(t, "Text.Body", null, row == 0 ? 0 : 4); x.TextWrapping = TextWrapping.Wrap;
                    Grid.SetRow(x, row); Grid.SetColumn(x, 1); g.Children.Add(x);
                }
                sp.Children.Add(g);
            }

            if (e.Forever.Length > 0) { sp.Children.Add(Sub("On Forever")); sp.Children.Add(Tagged(Para(e.Forever, 4), "lore-forever")); }

            if (e.Sources.Count > 0)
            {
                var w = new WrapPanel { Margin = new Thickness(0, 16, 0, 0), Tag = "lore-sources" };
                w.Children.Add(Tb("Sources ", "Text.Caption", Res("Brush.Text.Tertiary")));
                int i = 0;
                foreach (var url in e.Sources)
                {
                    if (i++ > 0) w.Children.Add(Tb(" · ", "Text.Caption", Res("Brush.Text.Tertiary")));
                    var u = url;
                    var l = new TextBlock { Text = ZoneLore.SourceLabel(u, e.Sources), Style = (Style)FindResource("Text.Caption"), Cursor = Cursors.Hand, Tag = u, ToolTip = u };
                    l.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text.Secondary");
                    l.MouseEnter += (s, a) => l.TextDecorations = TextDecorations.Underline;
                    l.MouseLeave += (s, a) => l.TextDecorations = null;
                    l.MouseLeftButtonUp += (s, a) => { a.Handled = true; OpenWeb(u); };
                    System.Windows.Automation.AutomationProperties.SetName(l, "Source: " + u);
                    w.Children.Add(l);
                }
                sp.Children.Add(w);
            }

            var card = Card(outer);
            card.Tag = "zone-lore";
            System.Windows.Automation.AutomationProperties.SetName(card, "About " + m.Title);
            loreCard = card;
            return card;
        }

        static T Tagged<T>(T fe, string tag) where T : FrameworkElement { fe.Tag = tag; return fe; }

        TextBlock Para(string text, double top)
        {
            var t = Tb(text, "Text.Body", null, top); t.TextWrapping = TextWrapping.Wrap;
            return t;
        }

        TextBlock Sub(string text) => Tb(text, "Text.RowTitle", null, 16);

        // an Atlas link in the notes: Body in the accent colour, underlined on hover
        TextBlock LoreLink(string text, string link, string tip)
        {
            var t = new TextBlock { Text = text, Style = (Style)FindResource("Text.Body"), Cursor = Cursors.Hand, Tag = link, ToolTip = tip };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Accent");   // the notes' links stand out from the prose
            t.MouseEnter += (s, a) => t.TextDecorations = TextDecorations.Underline;
            t.MouseLeave += (s, a) => t.TextDecorations = null;
            t.MouseLeftButtonUp += Row_Click;
            return t;
        }

        // a race of "Starts here": a 24 high chip; the race's page when the game has a playable race of that name
        FrameworkElement RaceChip(string race)
        {
            string link = CharacterLinks.Race(race);
            var b = new Border { Style = (Style)FindResource("Chip"), Padding = new Thickness(8, 0, 8, 0), MinHeight = 24, Margin = new Thickness(0, 0, 8, 4), Tag = link ?? "race-plain" };
            var t = link != null ? LoreLink(race, link, "Open the " + race + " race page") : Tb(race, "Text.Secondary");
            t.VerticalAlignment = VerticalAlignment.Center;
            // the race's portrait in front of the name (the game's race icon, RaceIcons), when the game data has the race as a playable one
            int iconId = 0;
            if (link != null) try { iconId = RaceIcons.For(ElansAddonHub.Services.Db2.ClassData.Data?.RaceByName((race ?? "").Trim())); } catch (Exception) { }
            if (iconId <= 0) { b.Child = t; return b; }
            var img = new Image { Width = 16, Height = 16, Margin = new Thickness(-4, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, Tag = "race-icon|" + iconId };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            var clip = new Border { CornerRadius = new CornerRadius(3), Child = img, ClipToBounds = true, VerticalAlignment = VerticalAlignment.Center };
            var have = IconStore.TryGet(iconId);
            if (have != null) img.Source = have;
            else IconStore.Request(iconId, src => img.Dispatcher.BeginInvoke(new Action(() => img.Source = src)));
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(clip); sp.Children.Add(t);
            b.Child = sp;
            return b;
        }
    }
}
