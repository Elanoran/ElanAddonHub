using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Professions;

namespace ElansAddonHub
{
    // The "Craft now" tab: recipes whose reagents the characters' bags and bank cover, and how many times.
    public partial class ProfessionsPage
    {
        List<CraftNowRow> craftRows = new List<CraftNowRow>();

        void RenderCraft(ProfessionBook b)
        {
            var scope = who;
            var all = CraftNow.Compute(b, chars, scope, true, true);
            var text = (SearchBox?.Text ?? "").Trim();
            craftRows = text.Length == 0 ? all : all.Where(r => r.Recipe.Name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0 || r.Profession.Name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            var head = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            head.Children.Add(Line(craftRows.Count + " recipe" + (craftRows.Count == 1 ? "" : "s") + " you can make now" + (scope != null ? " from " + scope.Name + "'s bags and bank" : " from all characters' bags and bank together"), "Text.Section"));
            var n = Line("Only recipes of a profession that one of the characters has, at a skill high enough for the recipe, are listed. The Outpost cannot see which recipes a character has learned, so a recipe may still need learning first. Reagents spread over several characters count together; the row says when one character has them all.", "Text.Caption", null, 4);
            n.TextWrapping = TextWrapping.Wrap; head.Children.Add(n);
            Body.Children.Add(head);

            if (chars.Count == 0) { Body.Children.Add(EmptyState("No characters yet", "Characters come from Elan's Hub. Log in with each character once with the Hub addon enabled; the Characters page shows them.")); return; }
            if (craftRows.Count == 0) { Body.Children.Add(EmptyState("Nothing to craft right now", "None of the recipes has all its reagents in bags and bank. Open the Leveling guide for a shopping list.")); return; }
            var host = new StackPanel();
            Body.Children.Add(host);
            AddCraftRows(host, craftRows, 0);
        }

        void AddCraftRows(StackPanel host, List<CraftNowRow> rows, int from)
        {
            int to = Math.Min(rows.Count, from + RecipePage);
            for (int i = from; i < to; i++) host.Children.Add(CraftRow(rows[i]));
            if (to < rows.Count)
            {
                var more = new Button { Style = Sty("Button.S"), Content = "Show " + Math.Min(RecipePage, rows.Count - to) + " more (" + (rows.Count - to) + " left)", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
                more.Click += (s, e) => { host.Children.Remove(more); AddCraftRows(host, rows, to); };
                host.Children.Add(more);
            }
        }

        FrameworkElement CraftRow(CraftNowRow row)
        {
            var r = row.Recipe;
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var outer = new Border { Background = Res("Brush.Surface1"), CornerRadius = (CornerRadius)FindResource("Radius.Control"), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 4), Child = grid, Tag = row };
            var icon = IconTile(r.IconId, r.Name, r.Quality, 32); icon.VerticalAlignment = VerticalAlignment.Top;
            grid.Children.Add(icon);

            var mid = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
            Grid.SetColumn(mid, 1);
            mid.Children.Add(LinkText(r.Name + (r.OutCount > 1 ? " x" + r.OutCount : ""), r.ItemId > 0 ? AtlasIds.Item(r.ItemId) : AtlasIds.Spell(r.SpellId), "Text.RowTitle", QualityBrush(r.Quality)));
            var who2 = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
            who2.Children.Add(Line(row.Profession.Name + " - by ", "Text.Caption"));
            if (row.Crafters.Count == 0) who2.Children.Add(Line("nobody (no character has it)", "Text.Caption"));
            for (int i = 0; i < row.Crafters.Count && i < 4; i++)
                who2.Children.Add(Line((i > 0 ? ", " : "") + row.Crafters[i].Name + " (" + row.Crafters[i].SkillOf(row.Profession.Name) + ")", "Text.Caption", ClassBrush(row.Crafters[i].Class)));
            if (row.Crafters.Count > 4) who2.Children.Add(Line(" +" + (row.Crafters.Count - 4), "Text.Caption"));
            mid.Children.Add(who2);
            var rw = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            foreach (var g in r.Reagents)
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 0) };
                sp.Children.Add(LinkText(g.Count + " x " + NameOf(g.ItemId), AtlasIds.Item(g.ItemId), "Text.Secondary"));
                sp.Children.Add(Line(" (" + Math.Min(Have(g.ItemId), 99999).ToString("N0", CultureInfo.InvariantCulture) + ")", "Text.Caption"));
                rw.Children.Add(sp);
            }
            mid.Children.Add(rw);
            if (who == null && row.SingleHolder != null) mid.Children.Add(Line("All the reagents are on " + row.SingleHolder.Name + " (" + SingleTimes(row) + " crafts there)", "Text.Caption", Res("Brush.Success"), 4));
            else if (who == null) mid.Children.Add(Line("The reagents are spread over several characters: collect them on one first.", "Text.Caption", Res("Brush.Warn"), 4));
            grid.Children.Add(mid);

            var right = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 96 };
            Grid.SetColumn(right, 2);
            var t = Line("x " + row.Times.ToString("N0", CultureInfo.InvariantCulture), "Text.Body"); t.HorizontalAlignment = HorizontalAlignment.Right; right.Children.Add(t);
            var c = Line(row.Best != null ? RecipeBands.Word(row.Color) : "-", "Text.Caption", ColorBrush(row.Color)); c.HorizontalAlignment = HorizontalAlignment.Right; c.Margin = new Thickness(0, 2, 0, 0); right.Children.Add(c);
            grid.Children.Add(right);
            SetTip(outer, LazyTip(() => RecipeTip(r)));
            return outer;
        }

        static int SingleTimes(CraftNowRow row) => CraftNow.Times(row.Recipe, row.SingleHolder.Items);

        // ---- test hooks
        public List<string> CraftRowsForTest() => craftRows.Select(r => r.Recipe.Name + " x" + r.Times + " " + RecipeBands.Word(r.Color)).ToList();
    }
}
