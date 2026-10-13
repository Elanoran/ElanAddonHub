using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Professions;

namespace ElansAddonHub
{
    // The Recipes tab: every recipe of the profession in skill order, coloured for the chosen character's skill.
    public partial class ProfessionsPage
    {
        const int RecipePage = 120;

        // the recipes the tab shows for the current profession, search and filter
        List<ProfRecipe> VisibleRecipes()
        {
            if (prof == null) return new List<ProfRecipe>();
            IEnumerable<ProfRecipe> q = prof.Recipes;
            var text = (SearchBox?.Text ?? "").Trim();
            if (text.Length > 0)
                q = q.Where(r => r.Name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0 || r.Reagents.Any(g => NameOf(g.ItemId).IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0));
            if (who != null)
            {
                int skill = who.SkillOf(prof.Name);
                if (filter == "now") q = q.Where(r => r.Start <= skill && r.ColorAt(skill) != SkillColor.Grey);
                else if (filter == "later") q = q.Where(r => r.Start > skill);
            }
            return q.ToList();
        }

        void RenderRecipes(ProfessionBook b)
        {
            if (prof == null) { Body.Children.Add(EmptyState("No profession", "The game data holds no professions.")); return; }
            var list = VisibleRecipes();
            int skill = who?.SkillOf(prof.Name) ?? 0;

            // what the colours mean for whom
            var head = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            head.Children.Add(Line(prof.Name + " - " + list.Count.ToString("N0", CultureInfo.InvariantCulture) + " of " + prof.Recipes.Count.ToString("N0", CultureInfo.InvariantCulture) + " recipes"
                + (who != null ? " - colours for " + who.Name + " at skill " + skill + "/" + who.MaxOf(prof.Name) : " - colour bands only (pick a character to colour them)"), "Text.Section"));
            var legend = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            foreach (var c in new[] { SkillColor.Orange, SkillColor.Yellow, SkillColor.Green, SkillColor.Grey })
                legend.Children.Add(ChipText(RecipeBands.Word(c) + (c == SkillColor.Orange ? ": every craft is a skill-up" : c == SkillColor.Yellow ? ": most crafts" : c == SkillColor.Green ? ": sometimes" : ": never"), ColorBrush(c)));
            head.Children.Add(legend);
            var note = Line("The Outpost cannot see which recipes a character has already learned (the Hub does not save them), so \"can make now\" means the skill is high enough, not that the recipe is known. Where a recipe starts is estimated from its colour band.", "Text.Caption", null, 4);
            note.TextWrapping = TextWrapping.Wrap;
            head.Children.Add(note);
            if (prof.Ranks.Count > 0)
            {
                var rk = Line("Ranks: " + string.Join(", ", prof.Ranks.Select(r => r.Name + " (to " + r.Cap + ")")) + " - raise the cap at the trainer before you reach it.", "Text.Caption", null, 4);
                rk.TextWrapping = TextWrapping.Wrap; head.Children.Add(rk);
            }
            Body.Children.Add(head);

            if (list.Count == 0) { Body.Children.Add(EmptyState("No recipes", prof.Recipes.Count == 0 ? prof.Name + " has no recipes of its own: see the leveling guide for where to gather." : "Nothing matches the search and filter.")); return; }
            var host = new StackPanel();
            Body.Children.Add(host);
            AddRecipeRows(host, list, 0, skill);
        }

        void AddRecipeRows(StackPanel host, List<ProfRecipe> list, int from, int skill)
        {
            int to = Math.Min(list.Count, from + RecipePage);
            for (int i = from; i < to; i++) host.Children.Add(RecipeRow(list[i], skill));
            if (to < list.Count)
            {
                var more = new Button { Style = Sty("Button.S"), Content = "Show " + Math.Min(RecipePage, list.Count - to) + " more (" + (list.Count - to) + " left)", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
                more.Click += (s, e) => { host.Children.Remove(more); AddRecipeRows(host, list, to, skill); };
                host.Children.Add(more);
            }
        }

        FrameworkElement RecipeRow(ProfRecipe r, int skill)
        {
            var grid = new Grid();
            var outer = new Border { Background = Res("Brush.Surface1"), CornerRadius = (CornerRadius)FindResource("Radius.Control"), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 4), Child = grid };
            outer.Tag = r;
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var icon = IconTile(r.IconId, r.Name, r.Quality, 32); icon.VerticalAlignment = VerticalAlignment.Top;
            grid.Children.Add(icon);

            var mid = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
            Grid.SetColumn(mid, 1);
            var nameRow = new WrapPanel();
            nameRow.Children.Add(LinkText(r.Name + (r.OutCount > 1 ? " x" + r.OutCount : ""), r.ItemId > 0 ? AtlasIds.Item(r.ItemId) : AtlasIds.Spell(r.SpellId), "Text.RowTitle", QualityBrush(r.Quality)));
            if (r.IsCamp) nameRow.Children.Add(new Border { Style = Sty("Chip"), Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(8, 0, 8, 0), MinHeight = 20, Child = Line("Camp, tier " + r.Tier, "Text.Caption") });
            mid.Children.Add(nameRow);
            mid.Children.Add(Line(LearnLine(r), "Text.Caption", null, 2));

            if (r.Reagents.Length > 0)
            {
                var rw = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
                foreach (var g in r.Reagents)
                {
                    int have = Have(g.ItemId);
                    var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 0) };
                    sp.Children.Add(LinkText(g.Count + " x " + NameOf(g.ItemId), AtlasIds.Item(g.ItemId), "Text.Secondary", Res("Brush.Text.Primary")));
                    if (chars.Count > 0) sp.Children.Add(Line(" (have " + have.ToString("N0", CultureInfo.InvariantCulture) + ")", "Text.Caption", have >= g.Count ? Res("Brush.Success") : null));
                    rw.Children.Add(sp);
                }
                mid.Children.Add(rw);
            }
            if (r.Learn == LearnKind.RecipeItem)
            {
                var item = r.TeachingItems[0];
                var srcs = sources?.SourcesOf(item, 2) ?? new List<SourceLine>();
                var lw = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
                lw.Children.Add(LinkText(NameOf(item), AtlasIds.Item(item), "Text.Secondary", QualityBrush(ItemData.Get(item)?.Quality ?? 2)));
                if (srcs.Count == 0) lw.Children.Add(Line(" - no source known (the Classic baseline and the Lodge have none)", "Text.Caption"));
                foreach (var s in srcs)
                {
                    lw.Children.Add(Line(" - ", "Text.Caption"));
                    lw.Children.Add(LinkText(s.Text, s.Link, "Text.Caption"));
                    lw.Children.Add(Line(" (" + s.Label + ")", "Text.Caption"));
                }
                mid.Children.Add(lw);
            }
            grid.Children.Add(mid);

            // colour for the character, else the band
            var right = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 120 };
            Grid.SetColumn(right, 2);
            if (r.High <= 0) right.Children.Add(Line("No skill-up", "Text.Body", ColorBrush(SkillColor.None)));
            else
            {
                if (who != null)
                {
                    var c = r.ColorAt(skill);
                    var t = Line(RecipeBands.Word(c), "Text.Body", ColorBrush(c)); t.HorizontalAlignment = HorizontalAlignment.Right; right.Children.Add(t);
                    if (r.Start > skill) { var l = Line("from skill " + r.Start, "Text.Caption"); l.HorizontalAlignment = HorizontalAlignment.Right; right.Children.Add(l); }
                }
                var bands = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, who != null ? 2 : 0, 0, 0) };
                bands.Children.Add(Line(r.Yellow.ToString(CultureInfo.InvariantCulture), "Text.Caption", ColorBrush(SkillColor.Yellow)));
                bands.Children.Add(Line(" " + r.Green.ToString(CultureInfo.InvariantCulture), "Text.Caption", ColorBrush(SkillColor.Green)));
                bands.Children.Add(Line(" " + r.Grey.ToString(CultureInfo.InvariantCulture), "Text.Caption", ColorBrush(SkillColor.Grey)));
                right.Children.Add(bands);
            }
            grid.Children.Add(right);
            SetTip(outer, LazyTip(() => RecipeTip(r)));
            return outer;
        }

        string LearnLine(ProfRecipe r)
        {
            switch (r.Learn)
            {
                case LearnKind.Starter: return "Taught with the profession";
                case LearnKind.Trainer: return "Trainer (no recipe item in the game data)";
                case LearnKind.RecipeItem: return r.TeachingItems.Count > 1 ? "Recipe item (" + r.TeachingItems.Count + " versions)" : "Recipe item";
                default: return "Special (neither trainer nor recipe item in the game data)";
            }
        }

        StackPanel RecipeTip(ProfRecipe r)
        {
            var sp = new StackPanel { MinWidth = 240 };
            sp.Children.Add(Line(r.Name, "Text.RowTitle", QualityBrush(r.Quality)));
            if (r.ItemId > 0) AddItemFacts(sp, r.ItemId);
            else
            {
                var spell = SpellData.Spell(r.SpellId);
                var d = spell == null ? null : (!string.IsNullOrWhiteSpace(spell.Description) ? spell.Description : spell.AuraDescription);
                if (!string.IsNullOrWhiteSpace(d)) { var t = Line(d.Trim(), "Text.Body", Res("Brush.Warn"), 6); t.TextWrapping = TextWrapping.Wrap; t.MaxWidth = 280; sp.Children.Add(t); }
            }
            if (r.Reagents.Length > 0)
            {
                sp.Children.Add(Line("Reagents", "Text.Caption", null, 12));
                foreach (var g in r.Reagents)
                {
                    var p = sources?.PriceOf(g.ItemId) ?? default(ItemPrice);
                    sp.Children.Add(Line(g.Count + " x " + NameOf(g.ItemId) + (chars.Count > 0 ? "  (you have " + Have(g.ItemId) + " across characters)" : "") + (p.Known ? "  " + (p.Kind == PriceKind.Estimated ? "~" : "") + ProfMoney.Format(p.Copper) + " each" : ""), "Text.Body", null, 2));
                }
            }
            sp.Children.Add(Line(prof?.Name + " - yellow from " + r.Yellow + ", green " + r.Green + ", grey " + r.Grey + (r.Start > 1 ? " - can be learned from about skill " + r.Start + " (estimated)" : ""), "Text.Caption", null, 12));
            sp.Children.Add(Line("Spell ID " + r.SpellId + (r.ItemId > 0 ? " - Item ID " + r.ItemId : ""), "Text.Caption"));
            return sp;
        }

        // ---- test hooks
        public List<string> RecipeRowNamesForTest() => TaggedRecipes().Select(r => r.Name).ToList();
        List<ProfRecipe> TaggedRecipes()
        {
            var res = new List<ProfRecipe>();
            void Walk(DependencyObject o) { if (o is Border bd && bd.Tag is ProfRecipe pr) { res.Add(pr); return; } foreach (var ch in LogicalTreeHelper.GetChildren(o).OfType<DependencyObject>()) Walk(ch); }
            Walk(Body);
            return res;
        }
        public int VisibleRecipeCountForTest => VisibleRecipes().Count;
        public FrameworkElement RecipeTipSheetForTest(int spellId)
        {
            var b = EnsureBook();
            if (b == null || !b.BySpell.TryGetValue(spellId, out var r)) return null;
            return TipSheet(RecipeTip(r));
        }

        FrameworkElement TipSheet(StackPanel content)
        {
            var frame = new Border { Background = Res("Brush.Surface3"), BorderBrush = Res("Brush.Divider"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8, 12, 8), Child = content };
            frame.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            frame.Arrange(new Rect(frame.DesiredSize));
            return frame;
        }
    }
}
