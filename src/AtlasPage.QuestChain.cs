using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using ElansAddonHub.Services.Atlas;

namespace ElansAddonHub
{
    // The quest page's "Quest chain" card (2.77, QuestChains): a compact timeline of the chain with this quest highlighted and every other step a link,
    // then what has to come first (pre-quests, the quests that open it, an exclusive group), what it opens, and breadcrumbs. A long chain shows the
    // steps around this one and a button for all of them.
    public partial class AtlasPage
    {
        public const int ChainFoldOver = 12, ChainAround = 3;
        public StackPanel QuestChainTimelineForTest { get; private set; }
        public Button QuestChainAllForTest { get; private set; }

        FrameworkElement QuestChainCard(QuestChainInfo c)
        {
            var sp = new StackPanel { Tag = "quest-chain" };
            var head = new DockPanel();
            if (c.StepText.Length > 0)
            {
                var step = Tb(c.StepText, "Text.Caption", Res("Brush.Text.Tertiary")); step.VerticalAlignment = VerticalAlignment.Center; step.Tag = "chain-step";
                DockPanel.SetDock(step, Dock.Right); head.Children.Add(step);
            }
            head.Children.Add(Tb(c.Name.Length > 0 ? c.Name : c.HasChain ? "Quest chain" : "Before and after", "Text.Section"));
            sp.Children.Add(head);

            QuestChainTimelineForTest = null; QuestChainAllForTest = null;
            if (c.HasChain)
            {
                var timeline = new StackPanel { Margin = new Thickness(0, 8, 0, 0), Tag = "chain-steps" };
                QuestChainTimelineForTest = timeline;
                void Fill(bool all)
                {
                    timeline.Children.Clear();
                    int cur = Math.Max(0, c.Position - 1);
                    bool fold = !all && c.Steps.Count > ChainFoldOver;
                    for (int i = 0; i < c.Steps.Count; i++)
                    {
                        bool show = !fold || i == 0 || i == c.Steps.Count - 1 || Math.Abs(i - cur) <= ChainAround;
                        if (!show)
                        {
                            int j = i; while (j + 1 < c.Steps.Count && !(j + 1 == c.Steps.Count - 1 || Math.Abs(j + 1 - cur) <= ChainAround)) j++;
                            timeline.Children.Add(ChainGap(j - i + 1, i == 0, false));
                            i = j; continue;
                        }
                        timeline.Children.Add(ChainStepRow(c.Steps[i], i == 0, i == c.Steps.Count - 1));
                    }
                }
                Fill(false);
                sp.Children.Add(timeline);
                if (c.Steps.Count > ChainFoldOver)
                {
                    bool all = false;
                    Button b = null;
                    b = GhostButton("Show all " + c.Steps.Count + " steps", () => { all = !all; Fill(all); b.Content = all ? "Show fewer steps" : "Show all " + c.Steps.Count + " steps"; }, "The whole chain");
                    b.Margin = new Thickness(-8, 4, 0, 0); b.Tag = "chain-all";
                    QuestChainAllForTest = b;
                    sp.Children.Add(b);
                }
                if (c.Cut) sp.Children.Add(Tb("The chain's links loop or run very long; it stops where it would repeat.", "Text.Caption", Res("Brush.Text.Tertiary"), 4));
            }

            void Links(string label, IEnumerable<int> ids, string tag, string tail = "")
            {
                var list = ids.Distinct().ToList();
                if (list.Count == 0) return;
                var g = new Grid { Margin = new Thickness(0, 12, 0, 0), Tag = tag };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(128) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var l = Tb(label, "Text.Secondary"); l.TextWrapping = TextWrapping.Wrap; l.Margin = new Thickness(0, 4, 8, 0); g.Children.Add(l);
                var wrap = new WrapPanel { Margin = new Thickness(-8, 0, 0, 0) };
                foreach (var id in list.Take(12))
                {
                    var t = AtlasQuery.QuestTitle(id); int qid = id;
                    var b = GhostButton(t, () => { _ = Open("quest:" + qid); }, "Open " + t);
                    b.Tag = "chain-link:" + qid; b.Margin = new Thickness(0, 0, 4, 0);
                    wrap.Children.Add(b);
                }
                if (list.Count > 12) wrap.Children.Add(Tb("and " + (list.Count - 12) + " more", "Text.Caption", Res("Brush.Text.Tertiary"), 4));
                if (tail.Length > 0) { var tt = Tb(tail, "Text.Caption", Res("Brush.Text.Tertiary"), 4); tt.Margin = new Thickness(8, 4, 0, 0); wrap.Children.Add(tt); }
                Grid.SetColumn(wrap, 1); g.Children.Add(wrap);
                sp.Children.Add(g);
            }
            Links("Complete first", c.Requires, "chain-requires");
            Links("Have in your log", c.RequiresActive, "chain-active");
            Links(c.OpenedByAll ? "Opened by all of" : "Opened by", c.OpenedBy, "chain-opened");
            Links("Also needed", c.AllOfGroup, "chain-allof", "all of these before the next step opens");
            Links("Only one of", c.ExclusiveWith, "chain-exclusive", "doing one of these closes the others");
            if (c.BreadcrumbFor > 0) Links("Leads to", new[] { c.BreadcrumbFor }, "chain-breadcrumb", "a breadcrumb: you can skip it");
            Links("Led here by", c.Breadcrumbs, "chain-breadcrumbs", "optional breadcrumbs");
            Links("Opens", c.Unlocks, "chain-unlocks");
            Links("Can be taken with it", c.WhileActive, "chain-while");
            var label = c.FromLine ? AtlasLabel.Forever : c.AnyClassic ? AtlasLabel.Classic : AtlasLabel.Forever;
            sp.Children.Add(SourceChip(c.FromLine ? label + " · quest line" : label, 12));
            return Card(sp);
        }

        // one step: a dot on a line (this quest's dot filled in the accent), the step number and the title (a link, except this quest)
        FrameworkElement ChainStepRow(QuestChainStep s, bool first, bool last)
        {
            var g = new Grid { MinHeight = 28, Tag = s.Current ? "chain-current" : "chain-step:" + s.Id };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.Children.Add(ChainRail(first, last));
            var dot = new Ellipse { Width = s.Current ? 12 : 8, Height = s.Current ? 12 : 8, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, StrokeThickness = s.Current ? 0 : 2 };
            if (s.Current) dot.SetResourceReference(Shape.FillProperty, "Brush.Accent");
            else { dot.SetResourceReference(Shape.FillProperty, "Brush.Surface1"); dot.SetResourceReference(Shape.StrokeProperty, "Brush.Text.Tertiary"); }
            g.Children.Add(dot);
            var n = Tb(s.Step + ".", "Text.Caption", Res("Brush.Text.Tertiary")); n.VerticalAlignment = VerticalAlignment.Center; n.HorizontalAlignment = HorizontalAlignment.Right; n.Margin = new Thickness(0, 0, 8, 0);
            Grid.SetColumn(n, 1); g.Children.Add(n);
            FrameworkElement title;
            if (s.Current)
            {
                var t = Tb(s.Title, "Text.Body"); t.FontWeight = FontWeights.SemiBold; t.VerticalAlignment = VerticalAlignment.Center; t.TextTrimming = TextTrimming.CharacterEllipsis;
                var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                row.Children.Add(t);
                var here = Tb("this quest", "Text.Caption", Res("Brush.Text.Tertiary")); here.Margin = new Thickness(8, 0, 0, 0); here.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(here);
                title = row;
            }
            else
            {
                int id = s.Id;
                var b = GhostButton(s.Title, () => { _ = Open("quest:" + id); }, "Open step " + s.Step + ": " + s.Title);
                b.Margin = new Thickness(-8, 0, 0, 0); b.Tag = "chain-open:" + id;
                title = b;
            }
            Grid.SetColumn(title, 2); g.Children.Add(title);
            return g;
        }

        // "… 6 more steps" between the shown ones
        FrameworkElement ChainGap(int count, bool first, bool last)
        {
            var g = new Grid { MinHeight = 24, Tag = "chain-gap" };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.Children.Add(ChainRail(first, last));
            var t = Tb(count + (count == 1 ? " more step" : " more steps"), "Text.Caption", Res("Brush.Text.Tertiary")); t.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(t, 2); g.Children.Add(t);
            return g;
        }

        // the line through the dots: from the middle of the first row down, to the middle of the last row
        static FrameworkElement ChainRail(bool first, bool last)
        {
            var host = new Grid();
            host.RowDefinitions.Add(new RowDefinition()); host.RowDefinitions.Add(new RowDefinition());
            if (!first) { var up = new Border { Width = 2, HorizontalAlignment = HorizontalAlignment.Center }; up.SetResourceReference(Border.BackgroundProperty, "Brush.Divider"); host.Children.Add(up); }
            if (!last) { var down = new Border { Width = 2, HorizontalAlignment = HorizontalAlignment.Center }; down.SetResourceReference(Border.BackgroundProperty, "Brush.Divider"); Grid.SetRow(down, 1); host.Children.Add(down); }
            return host;
        }
    }
}
