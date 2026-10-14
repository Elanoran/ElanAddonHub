using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Db2;

namespace ElansAddonHub
{
    // Atlas A2 on the Characters page: under a profession on the Professions tab, "Recipes you can learn for this profession" opens the profession's
    // recipes from the game's own SkillLineAbility table (lowest skill first), each with the item it makes (the same tooltip as everywhere) and what it uses.
    // It only appears when the spell data was read from the player's installation; nothing is guessed about what the character already knows.
    public partial class InventoryPage
    {
        const int RecipeRowsMax = 700;
        readonly Dictionary<string, StackPanel> recipeLists = new Dictionary<string, StackPanel>();
        readonly Dictionary<string, Action> recipeOpeners = new Dictionary<string, Action>();
        readonly Dictionary<string, List<Recipe>> recipeData = new Dictionary<string, List<Recipe>>();

        void AddRecipeList(StackPanel host, HubProfession p)
        {
            if (!SpellData.Ready) return;
            var sk = SpellData.SkillByName(p.Name);
            if (sk == null) return;
            var recipes = SpellData.RecipesOfSkill(sk.Id);
            if (recipes.Count == 0) return;
            recipeData[p.Name] = recipes;
            charTabInfo.Add("recipes|" + p.Name + "|" + recipes.Count);

            var list = new StackPanel();
            var scroll = new ScrollViewer { MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = list, Margin = new Thickness(0, 4, 0, 0) };
            var detail = new Border { Child = scroll, Visibility = Visibility.Collapsed };
            recipeLists[p.Name] = list;
            bool filled = false;
            Action fill = () =>
            {
                if (filled) return;
                filled = true;
                foreach (var r in recipes.Take(RecipeRowsMax)) list.Children.Add(RecipeRow(r, sk));
                if (recipes.Count > RecipeRowsMax) list.Children.Add(Line((recipes.Count - RecipeRowsMax) + " more recipes are not listed here.", "Text.Caption", null, 8));
            };

            var chevron = new TextBlock { Style = TextStyle("Text.Chevron"), Margin = new Thickness(8, 0, 0, 0) };
            var head = new Grid { Background = Brushes.Transparent, Margin = new Thickness(0, 12, 0, 0), Cursor = Cursors.Hand, ToolTip = "Show / hide the recipes" };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.Children.Add(new TextBlock { Text = "Recipes you can learn for this profession", Style = TextStyle("Text.Secondary"), VerticalAlignment = VerticalAlignment.Center, Foreground = Res("Brush.Text.Secondary"), TextTrimming = TextTrimming.CharacterEllipsis });
            var count = new TextBlock { Text = recipes.Count.ToString("N0", CultureInfo.InvariantCulture), Style = TextStyle("Text.Caption"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            Grid.SetColumn(count, 1);
            head.Children.Add(count);
            Grid.SetColumn(chevron, 2);
            head.Children.Add(chevron);
            Action toggle = () =>
            {
                bool open = detail.Visibility != Visibility.Visible;
                if (open) { fill(); detail.Visibility = Visibility.Visible; } else detail.Visibility = Visibility.Collapsed;
                Motion.SetTurn(chevron, open);
            };
            head.MouseLeftButtonUp += (s, e) => toggle();
            recipeOpeners[p.Name] = () => { if (detail.Visibility != Visibility.Visible) toggle(); };
            host.Children.Add(head);
            host.Children.Add(detail);
        }

        FrameworkElement RecipeRow(Recipe r, SkillInfo sk)
        {
            var item = r.ItemId > 0 ? ItemData.Get(r.ItemId) : null;
            var spell = SpellData.Spell(r.SpellId);
            string name = item != null && !string.IsNullOrEmpty(item.Name) ? item.Name : spell != null ? spell.Name : "Spell " + r.SpellId;
            int quality = item != null ? item.Quality : 1;
            int iconId = item != null && item.IconId > 0 ? item.IconId : spell?.IconId ?? 0;

            var row = new DockPanel { Background = Brushes.Transparent, Margin = new Thickness(0, 4, 0, 0), LastChildFill = true };
            var icon = new Grid { Width = 24, Height = 24, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            var fb = new Border { CornerRadius = (CornerRadius)FindResource("Radius.Control"), Background = Res("Brush.Overlay.Chip"), Child = new TextBlock { Text = Initial(name), Foreground = Res("Brush.Text.Secondary"), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            var img = new Image { Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            icon.Children.Add(fb);
            icon.Children.Add(img);
            if (iconId > 0) BindIcon(iconId, img, fb);
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            var need = new TextBlock { Text = r.MinRank > 0 ? "Skill " + r.MinRank : "", Style = TextStyle("Text.Caption"), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(need, Dock.Right);
            row.Children.Add(need);
            row.Children.Add(new TextBlock { Text = name, Style = TextStyle("Text.Body"), Foreground = Frozen(QualityHex[Math.Max(0, Math.Min(QualityHex.Length - 1, quality))]), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            var tt = GameTooltip.Wrap(null);
            tt.Opened += (s, e) => tt.Content = RecipeTipContent(r, sk, name, quality);
            row.ToolTip = tt;
            ToolTipService.SetInitialShowDelay(row, 150);
            ToolTipService.SetShowDuration(row, 30000);
            return row;
        }

        // the tooltip of a recipe: the item it makes (facts as everywhere), or the spell's description for one that makes nothing (an enchant), then the skill and the reagents
        GameTooltip RecipeTipContent(Recipe r, SkillInfo sk, string name, int quality)
        {
            var gt = new GameTooltip(name, r.ItemId > 0 ? quality : 1);
            bool facts = r.ItemId > 0 && AddItemFacts(gt, r.ItemId);
            if (!facts)
            {
                var spell = SpellData.Spell(r.SpellId);
                var d = spell == null ? null : (!string.IsNullOrWhiteSpace(spell.Description) ? spell.Description : spell.AuraDescription);
                if (!string.IsNullOrWhiteSpace(d)) gt.AddText(d.Trim(), "spelldesc");
            }
            if (r.ReagentCount > 0)
            {
                var parts = new List<string>();
                for (int i = 0; i + 1 < r.Reagents.Length; i += 2)
                {
                    var ri = ItemData.Get(r.Reagents[i]);
                    parts.Add(r.Reagents[i + 1] + " x " + (ri != null && !string.IsNullOrEmpty(ri.Name) ? ri.Name : "Item " + r.Reagents[i]));
                }
                gt.AddText("Reagents:", "plain");
                foreach (var p in parts) gt.AddText(p, "plain");
            }
            var sp = gt.Footer;
            sp.Children.Add(GameTooltip.Muted(sk.Name + (r.MinRank > 0 ? " · skill " + r.MinRank : "") + (r.Green > 0 ? " · green " + r.Green + " · grey " + r.Gray : "")));
            sp.Children.Add(GameTooltip.Muted("Spell ID " + r.SpellId + (r.ItemId > 0 ? " · Item ID " + r.ItemId : "")));
            return gt;
        }

        // ---- self-test hooks
        public List<string> RecipeRowsForTest(string profession)
        {
            if (!recipeOpeners.TryGetValue(profession, out var open)) return null;
            open();
            var res = new List<string>();
            foreach (var ch in recipeLists[profession].Children.OfType<DockPanel>())
                res.Add(string.Join(" | ", ch.Children.OfType<TextBlock>().Reverse().Select(t => t.Text).Where(t => !string.IsNullOrEmpty(t))));      // the name, then the skill it needs
            return res;
        }

        GameTooltip RecipeTipFor(string profession, int index)
        {
            if (!recipeData.TryGetValue(profession, out var recipes) || index < 0 || index >= recipes.Count) return null;
            var r = recipes[index];
            var sk = SpellData.Skill(r.SkillId);
            var item = r.ItemId > 0 ? ItemData.Get(r.ItemId) : null;
            var sp = SpellData.Spell(r.SpellId);
            return RecipeTipContent(r, sk, item != null ? item.Name : sp?.Name ?? "?", item?.Quality ?? 1);
        }

        static List<string> Texts(object content)
        {
            var res = new List<string>();
            void Walk(object o)
            {
                if (o is TextBlock tb) res.Add(tb.Text);
                else if (o is Panel p) foreach (var ch in p.Children) Walk(ch);
                else if (o is Border bd) Walk(bd.Child);
            }
            Walk(content);
            return res;
        }

        public List<string> RecipeTipForTest(string profession, int index) => Texts(RecipeTipFor(profession, index));
        public FrameworkElement RecipeTipSheetForTest(string profession, int index) => TipSheet(GameTooltip.Wrap(RecipeTipFor(profession, index)));
        public FrameworkElement DollTipSheetForTest(int slot) => dollSlots.TryGetValue(slot, out var el) && el.ToolTip is ToolTip t ? TipSheet(t) : null;

        // the tile of the talent whose spell id is `spellId` (its tooltip ends in "Spell ID n")
        static IEnumerable<DependencyObject> Descend(DependencyObject o)
        {
            foreach (var c in LogicalTreeHelper.GetChildren(o).OfType<DependencyObject>())
            {
                yield return c;
                foreach (var d in Descend(c)) yield return d;
            }
        }

        ToolTip TalentTileTip(int spellId) =>
            Descend(CharHost).OfType<Border>().Select(b => b.ToolTip as ToolTip).FirstOrDefault(t => t != null && t.Content is GameTooltip gt && gt.Texts().Contains("Spell ID " + spellId));

        public List<string> TalentTipForTest(int spellId) => Texts(TalentTileTip(spellId)?.Content);
        public FrameworkElement TalentTipSheetForTest(int spellId) { var t = TalentTileTip(spellId); return t == null ? null : TipSheet(t); }
    }
}
