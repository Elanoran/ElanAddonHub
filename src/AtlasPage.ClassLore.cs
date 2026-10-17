using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ElansAddonHub.Services.Atlas;

namespace ElansAddonHub
{
    // "About <class>" / "About <race>" on a class or race page's Overview (2.81), in the house style of the zone notes (AtlasPage.Lore.cs). A Surface1 card:
    // the title (Text.Section) with the source chips at the right ("From the game" when the client has the text, "Outpost notes" when the Outpost has
    // notes), then at reading width:
    //   the game's own words  the character creation text, set off by a hairline on its left, with a caption that says where it is from
    //   the summary           Outpost notes
    //   At a glance           the notes' facts (capital, leader ...) and lines from the page's own game data (races / classes, armor, weapons,
    //                         racial abilities, the start), each a link to its page; a caption says which lines are the game's
    //   History, Notable figures (a creature page when the data has exactly that creature), Worth knowing, On Forever, Sources
    // An entry written from the in-game data only (Forever's Skyborne) says so under it. The card replaces the game-tooltip card these pages had (2.81).
    public partial class AtlasPage
    {
        FrameworkElement classLoreCard;
        public FrameworkElement ClassLoreCardForTest => classLoreCard;
        public FrameworkElement ClassLoreCardForTestBuild(AtlasPageModel m) { var keep = classLoreCard; var c = ClassLoreCard(m); classLoreCard = keep; return c; }

        // where the lines of "At a glance" come from: "Races, armor, weapons: from the game data; starts in: your characters; the rest: Outpost notes."
        public static string GlanceSourceLine(ClassLorePage p, int writtenFacts)
        {
            string Where(string source) => source == AtlasLabel.Mine ? "your characters" : source == ClassLore.Label ? "" : "the game data";
            var parts = p.Glance.Where(g => Where(g.Source).Length > 0).GroupBy(g => Where(g.Source))
                .Select(grp => string.Join(", ", grp.Select(g => g.Label.ToLowerInvariant())) + ": from " + grp.Key).ToList();
            if (parts.Count == 0) return "";
            bool notes = writtenFacts > 0 || p.Glance.Any(g => Where(g.Source).Length == 0);
            var s = string.Join("; ", parts) + (notes ? "; the rest: Outpost notes." : ".");
            return char.ToUpperInvariant(s[0]) + s.Substring(1);
        }

        FrameworkElement ClassLoreCard(AtlasPageModel m)
        {
            var p = m.ClassLore;
            var outer = new StackPanel();
            var head = new DockPanel();
            var chips = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (p.Official.Length > 0) { var c = SourceChip(ClassLore.GameLabel); c.Tag = "classlore-official-label"; c.ToolTip = "The game's own text: the character creation description in your client"; chips.Children.Add(c); }
            if (p.Notes != null && p.Notes.HasText)
            {
                var c = SourceChip(p.Notes.FromGameDataOnly ? ClassLore.GameDataLabel : ClassLore.Label); c.Tag = "classlore-notes-label"; c.Margin = new Thickness(chips.Children.Count > 0 ? 8 : 0, 0, 0, 0);
                c.ToolTip = p.Notes.FromGameDataOnly ? "Written for the Outpost from the game's own text and the in-game data only" : "Written for the Outpost in its own words";
                chips.Children.Add(c);
            }
            DockPanel.SetDock(chips, Dock.Right); head.Children.Add(chips);
            var title = Tb("About " + p.Title, "Text.Section"); title.TextTrimming = TextTrimming.CharacterEllipsis;
            head.Children.Add(title);
            outer.Children.Add(head);

            var sp = new StackPanel { MaxWidth = ReadWidth, HorizontalAlignment = HorizontalAlignment.Left };
            outer.Children.Add(sp);
            var notes = p.Notes;

            if (p.Official.Length > 0)
            {
                var quote = new StackPanel();
                quote.Children.Add(Para(p.Official, 0));
                quote.Children.Add(Tb("In the game's own words: the character creation text of your client.", "Text.Caption", Res("Brush.Text.Tertiary"), 4));
                var bar = new Border { BorderThickness = new Thickness(2, 0, 0, 0), Padding = new Thickness(12, 0, 0, 0), Margin = new Thickness(0, 12, 0, 0), Child = quote, Tag = "classlore-official" };
                bar.SetResourceReference(Border.BorderBrushProperty, "Brush.Overlay.Pressed");
                sp.Children.Add(bar);
            }
            if (notes != null && notes.Summary.Length > 0) sp.Children.Add(Tagged(Para(notes.Summary, 12), "classlore-summary"));

            // at a glance: the notes' facts, then the lines of the page's own game data
            int written = notes?.Facts.Count ?? 0;
            if (written + p.Glance.Count > 0)
            {
                sp.Children.Add(Sub("At a glance"));
                var g = new Grid { Margin = new Thickness(0, 4, 0, 0), Tag = "classlore-glance" };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MaxWidth = 200 });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                void Row(string label, FrameworkElement value)
                {
                    g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    int r = g.RowDefinitions.Count - 1;
                    var l = Tb(label, "Text.Secondary", null, r == 0 ? 0 : 4); l.Margin = new Thickness(0, r == 0 ? 0 : 4, 16, 0);
                    Grid.SetRow(l, r); g.Children.Add(l);
                    value.Margin = new Thickness(0, r == 0 ? 0 : 4, 0, 0);
                    Grid.SetRow(value, r); Grid.SetColumn(value, 1); g.Children.Add(value);
                }
                if (notes != null) foreach (var f in notes.Facts) { var v = Tb(f.Value, "Text.Body"); v.TextWrapping = TextWrapping.Wrap; Row(f.Label, v); }
                foreach (var gl in p.Glance)
                {
                    var w = new WrapPanel { Tag = "classlore-glance-row" };
                    int i = 0;
                    foreach (var it in gl.Items)
                    {
                        string sep = ++i < gl.Items.Count ? ", " : "";
                        if (it.Value != null)
                        {
                            var link = LoreLink(it.Key, it.Value, "Open " + it.Key);
                            if (sep.Length == 0) w.Children.Add(link);
                            else { var pair = new StackPanel { Orientation = Orientation.Horizontal }; pair.Children.Add(link); pair.Children.Add(Tb(sep, "Text.Body")); w.Children.Add(pair); }   // the comma wraps with its item, never to the start of a line
                        }
                        else w.Children.Add(Tb(it.Key + sep, "Text.Body"));
                    }
                    Row(gl.Label, w);
                }
                sp.Children.Add(g);
                var cap = Tb(GlanceSourceLine(p, written), "Text.Caption", Res("Brush.Text.Tertiary"), 4);
                cap.TextWrapping = TextWrapping.Wrap; cap.Tag = "classlore-glance-source";
                if (cap.Text.Length > 0) sp.Children.Add(cap);
            }

            if (notes != null && notes.History.Length > 0) { sp.Children.Add(Sub("History")); sp.Children.Add(Tagged(Para(notes.History, 4), "classlore-history")); }

            if (p.Figures.Count > 0)
            {
                sp.Children.Add(Sub("Notable figures"));
                var g = new Grid { Margin = new Thickness(0, 4, 0, 0), Tag = "classlore-figures" };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MaxWidth = 240 });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                foreach (var f in p.Figures)
                {
                    g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    int row = g.RowDefinitions.Count - 1;
                    var name = f.Link != null ? LoreLink(f.Figure.Name, f.Link, "Open " + f.Figure.Name) : Tb(f.Figure.Name, "Text.Body");
                    name.TextWrapping = TextWrapping.Wrap; name.Margin = new Thickness(0, row == 0 ? 0 : 4, 12, 0);
                    Grid.SetRow(name, row); g.Children.Add(name);
                    if (f.Figure.Why.Length > 0)
                    {
                        var why = Tb(f.Figure.Why, "Text.Secondary"); why.TextWrapping = TextWrapping.Wrap; why.Margin = new Thickness(0, row == 0 ? 0 : 4, 0, 0); why.VerticalAlignment = VerticalAlignment.Center;
                        Grid.SetRow(why, row); Grid.SetColumn(why, 1); g.Children.Add(why);
                    }
                }
                sp.Children.Add(g);
            }

            if (notes != null && notes.Mentions.Count > 0) { sp.Children.Add(Sub("Worth knowing")); sp.Children.Add(Bullets(notes.Mentions, "classlore-mentions")); }
            if (notes != null && notes.Forever.Length > 0) { sp.Children.Add(Sub("On Forever")); sp.Children.Add(Tagged(Para(notes.Forever, 4), "classlore-forever")); }
            if (notes != null && notes.Sources.Count > 0) sp.Children.Add(SourcesLine(notes.Sources, "classlore-sources"));
            if (notes != null && notes.FromGameDataOnly)
            {
                var b = Tb("Written for the Outpost from the game's own text and the in-game data only; there is no outside source for it.", "Text.Caption", Res("Brush.Text.Tertiary"), 16);
                b.TextWrapping = TextWrapping.Wrap; b.Tag = "classlore-basis";
                sp.Children.Add(b);
            }

            var card = Card(outer);
            card.Tag = "class-lore";
            System.Windows.Automation.AutomationProperties.SetName(card, "About " + p.Title);
            classLoreCard = card;
            return card;
        }
    }
}
