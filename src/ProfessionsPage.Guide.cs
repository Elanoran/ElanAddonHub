using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Professions;

namespace ElansAddonHub
{
    // The Leveling guide tab: a generated step-by-step path (crafting professions) or a zone guide (gathering professions). The algorithms live in
    // Services\Professions\SkillGuide.cs and GatherGuide.cs; this file shows them and offers the text for the clipboard or a file.
    public partial class ProfessionsPage
    {
        int? guideStart, guideTarget;
        string guideScope = "all";                    // all | char | none: whose items are subtracted from the shopping list
        bool guideCamp;                               // include camp objects in the path
        SkillGuide lastGuide;
        GatherGuide lastGather;
        int guideGen;

        void RenderGuide(ProfessionBook b)
        {
            if (prof == null) { Body.Children.Add(EmptyState("No profession", "The game data holds no professions.")); return; }
            int cap = prof.MaxCap;
            int charSkill = who?.SkillOf(prof.Name) ?? 0;
            int start = Math.Max(1, Math.Min(cap - 1, guideStart ?? (charSkill > 0 ? charSkill : 1)));
            int target = Math.Max(start + 1, Math.Min(cap, guideTarget ?? cap));
            lastGuide = null; lastGather = null;

            Body.Children.Add(GuideControls(start, target, cap));
            if (prof.Gathering) { RenderGatherGuide(b, start, target); return; }

            IDictionary<int, int> have = guideScope == "none" ? new Dictionary<int, int>() : guideScope == "char" && who != null ? who.Items : pool;
            var g = SkillGuideBuilder.Build(b, prof, start, target, sources, new Dictionary<int, int>(have), who?.Name, false, guideCamp);
            lastGuide = g;
            Body.Children.Add(GuideSummary(g));
            Body.Children.Add(ShoppingList(g));
            Body.Children.Add(Section("Steps", "Each line is a skill range, the recipe to make and how many times. Make them in order."));
            foreach (var s in g.Steps) Body.Children.Add(s.IsMilestone ? MilestoneRow(s) : StepCard(s, g));
            foreach (var n in g.Notes) { var t = Line(n, "Text.Caption", null, 8); t.TextWrapping = TextWrapping.Wrap; Body.Children.Add(t); }
            if (prof.Recipes.Any(r => r.IsCamp)) { var t = Line(guideCamp ? "Camp objects are included in this guide (switch them off above to leave them out)." : "Camp objects are left out of the guide: they are made for their buff, not for skill (switch them on above to include them, or see the Camping tab).", "Text.Caption", null, 4); t.TextWrapping = TextWrapping.Wrap; Body.Children.Add(t); }
        }

        FrameworkElement GuideControls(int start, int target, int cap)
        {
            var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
            Func<string, FrameworkElement> lbl = text => { var t = Line(text, "Text.Secondary"); t.VerticalAlignment = VerticalAlignment.Center; t.Margin = new Thickness(0, 0, 8, 8); return t; };
            bar.Children.Add(lbl("From skill"));
            var from = new TextBox { Style = Sty("Input"), Width = 72, Text = start.ToString(CultureInfo.InvariantCulture), Margin = new Thickness(0, 0, 12, 8),  };
            bar.Children.Add(from);
            bar.Children.Add(lbl("to"));
            var to = new TextBox { Style = Sty("Input"), Width = 72, Text = target.ToString(CultureInfo.InvariantCulture), Margin = new Thickness(0, 0, 12, 8),  };
            bar.Children.Add(to);
            Action apply = () =>
            {
                if (int.TryParse(from.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var s)) guideStart = Math.Max(1, Math.Min(cap - 1, s));
                if (int.TryParse(to.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var t)) guideTarget = Math.Max(2, Math.Min(cap, t));
                RenderBody();
            };
            from.KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Enter) apply(); };
            to.KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Enter) apply(); };
            from.LostKeyboardFocus += (s, e) => { if (from.Text != start.ToString(CultureInfo.InvariantCulture)) apply(); };
            to.LostKeyboardFocus += (s, e) => { if (to.Text != target.ToString(CultureInfo.InvariantCulture)) apply(); };

            var reset = new Button { Style = Sty("Button.M"), Content = "From the start", Margin = new Thickness(0, 0, 8, 8), ToolTip = "Plan from skill 1" };
            reset.Click += (s, e) => { guideStart = 1; guideTarget = null; RenderBody(); };
            bar.Children.Add(reset);
            if (who != null && prof != null && who.Has(prof.Name))
            {
                var cur = new Button { Style = Sty("Button.M"), Content = "From " + who.Name + "'s skill (" + who.SkillOf(prof.Name) + ")", Margin = new Thickness(0, 0, 8, 8) };
                cur.Click += (s, e) => { guideStart = null; guideTarget = null; RenderBody(); };
                bar.Children.Add(cur);
            }
            if (!prof.Gathering)
            {
                var scope = new ComboBox { Style = Sty("Dropdown"), Width = 260, Margin = new Thickness(0, 0, 8, 8) };
                scope.Items.Add(new ComboBoxItem { Content = "Subtract all characters' items", Tag = "all" });
                scope.Items.Add(new ComboBoxItem { Content = who != null ? "Subtract only " + who.Name + "'s items" : "Subtract only the chosen character's items", Tag = "char" });
                scope.Items.Add(new ComboBoxItem { Content = "Subtract nothing", Tag = "none" });
                scope.SelectedIndex = guideScope == "char" ? 1 : guideScope == "none" ? 2 : 0;
                scope.SelectionChanged += (s, e) => { if (scope.SelectedItem is ComboBoxItem ci && (string)ci.Tag != guideScope) { guideScope = (string)ci.Tag; RenderBody(); } };
                bar.Children.Add(scope);
            }
            if (!prof.Gathering && prof.Recipes.Any(r => r.IsCamp))
            {
                var sw = new CheckBox { Style = Sty("Switch"), IsChecked = guideCamp, Margin = new Thickness(8, 0, 8, 8), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Camp objects are made for their buff, but the first ones are cheap skill-ups" };
                sw.Click += (s, e) => { guideCamp = sw.IsChecked == true; RenderBody(); };
                bar.Children.Add(sw);
                var l = lbl("Include camp objects"); bar.Children.Add(l);
            }
            var copy = new Button { Style = Sty("Button.M"), Content = "Copy as text", Margin = new Thickness(0, 0, 8, 8), ToolTip = "Copy the whole guide to the clipboard" };
            copy.Click += (s, e) => { try { Clipboard.SetText(GuideTextNow()); copy.Content = "Copied"; } catch (Exception ex) { copy.Content = "Could not copy"; Util.Log("professions: copy: " + ex.Message); } };
            bar.Children.Add(copy);
            var save = new Button { Style = Sty("Button.M"), Content = "Save as text file...", Margin = new Thickness(0, 0, 8, 8), ToolTip = "Save the guide as a .txt file you can open and print" };
            save.Click += (s, e) => SaveGuide();
            bar.Children.Add(save);
            return bar;
        }

        string GuideTextNow() => lastGuide != null ? lastGuide.ToText() : lastGather != null ? lastGather.ToText() : "";

        void SaveGuide()
        {
            try
            {
                var dlg = new Microsoft.Win32.SaveFileDialog { Title = "Save the leveling guide", FileName = (prof?.Name ?? "guide") + " leveling guide.txt", Filter = "Text file (*.txt)|*.txt" };
                if (dlg.ShowDialog() == true) System.IO.File.WriteAllText(dlg.FileName, GuideTextNow(), new System.Text.UTF8Encoding(false));
            }
            catch (Exception ex) { Util.Log("professions: save: " + ex.Message); }
        }

        FrameworkElement GuideSummary(SkillGuide g)
        {
            var sp = new StackPanel();
            sp.Children.Add(Line(g.Profession.Name + " " + SkillGuide.Range(g.Start, g.Reached) + (g.Character != null ? " for " + g.Character : ""), "Text.Section"));
            var total = (g.TotalApprox ? "about " : "") + ProfMoney.Format(g.TotalCost) + (g.UnknownPrices > 0 ? " plus " + g.UnknownPrices + " item" + (g.UnknownPrices == 1 ? "" : "s") + " with no known price" : "");
            sp.Children.Add(Line("Still to buy or find: " + total + ". " + g.Steps.Count(s => !s.IsMilestone) + " steps, " + g.Crafting.Sum(s => s.Crafts).ToString("N0", CultureInfo.InvariantCulture) + " crafts.", "Text.Secondary", null, 4));
            var how = Line("Generated from the game's recipe table: orange recipes first (every craft a skill-up), then yellow (about 1.25 crafts per point), green only when nothing else is left (about 2.5); the cheapest reagents win. Vendor prices are from the Classic baseline or the Lodge; ~ is an estimate.", "Text.Caption", null, 4);
            how.TextWrapping = TextWrapping.Wrap; sp.Children.Add(how);
            return Card(sp);
        }

        FrameworkElement ShoppingList(SkillGuide g)
        {
            var sp = new StackPanel();
            sp.Children.Add(Line("Shopping list", "Text.Section"));
            sp.Children.Add(Line(guideScope == "none" ? "Everything the guide uses." : "Minus what " + (guideScope == "char" && who != null ? who.Name + " has" : "your characters have") + " in bags and bank.", "Text.Secondary", null, 2));
            if (g.Shopping.Count == 0) sp.Children.Add(Line("Nothing: the guide uses no reagents.", "Text.Secondary", null, 8));
            bool first = true;
            foreach (var l in g.Shopping)
            {
                var it = ItemData.Get(l.ItemId);
                var row = new Grid { Margin = new Thickness(0, first ? 12 : 8, 0, 0) }; first = false;
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var icon = IconTile(it?.IconId ?? 0, NameOf(l.ItemId), it?.Quality ?? 1, 24); icon.HorizontalAlignment = HorizontalAlignment.Left; icon.VerticalAlignment = VerticalAlignment.Top;
                row.Children.Add(icon);
                var mid = new StackPanel(); Grid.SetColumn(mid, 1);
                var nm = new WrapPanel();
                nm.Children.Add(LinkText((l.ToGet > 0 ? l.ToGet + " x " : "") + NameOf(l.ItemId) + (l.IsRecipe ? " (recipe)" : ""), AtlasIds.Item(l.ItemId), "Text.Body", QualityBrush(it?.Quality ?? 1)));
                nm.Children.Add(Line("   need " + l.Need + (l.Have > 0 ? ", have " + l.Have : ""), "Text.Caption"));
                mid.Children.Add(nm);
                if (l.ToGet > 0)
                {
                    var wh = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
                    wh.Children.Add(LinkText(l.Where, string.IsNullOrEmpty(l.Link) ? null : l.Link, "Text.Caption"));
                    if (!string.IsNullOrEmpty(l.Label)) wh.Children.Add(Line(" (" + l.Label + ")", "Text.Caption"));
                    mid.Children.Add(wh);
                }
                row.Children.Add(mid);
                var price = Line(l.ToGet == 0 ? "have it" : l.Price.Known ? (l.Price.Kind == PriceKind.Estimated ? "~" : "") + ProfMoney.Format(l.Total) : "price unknown", "Text.Secondary", l.ToGet == 0 ? Res("Brush.Success") : null);
                price.HorizontalAlignment = HorizontalAlignment.Right; Grid.SetColumn(price, 2);
                row.Children.Add(price);
                sp.Children.Add(row);
            }
            return Card(sp);
        }

        FrameworkElement MilestoneRow(GuideStep s)
        {
            var row = new Border { Background = Res("Brush.Info.Tint"), CornerRadius = (CornerRadius)FindResource("Radius.Control"), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 8), Tag = s };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(Line("Trainer", "Text.Caption", Res("Brush.Info")));
            var t = Line("   " + s.TrainText, "Text.Body"); sp.Children.Add(t);
            row.Child = sp;
            return row;
        }

        FrameworkElement StepCard(GuideStep s, SkillGuide g)
        {
            var r = s.Recipe;
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var card = Card(grid, 8);
            card.Tag = s;
            var range = new StackPanel();
            range.Children.Add(Line(SkillGuide.Range(s.From, s.To), "Text.Section"));
            range.Children.Add(Line("x" + s.Crafts.ToString("N0", CultureInfo.InvariantCulture), "Text.Secondary", null, 2));
            grid.Children.Add(range);
            var icon = IconTile(r.IconId, r.Name, r.Quality, 32); icon.VerticalAlignment = VerticalAlignment.Top; Grid.SetColumn(icon, 1);
            grid.Children.Add(icon);

            var sp = new StackPanel(); Grid.SetColumn(sp, 2);
            var title = new WrapPanel();
            title.Children.Add(LinkText(r.Name + (r.OutCount > 1 ? " (makes " + r.OutCount + ")" : ""), r.ItemId > 0 ? AtlasIds.Item(r.ItemId) : AtlasIds.Spell(r.SpellId), "Text.RowTitle", QualityBrush(r.Quality)));
            title.Children.Add(Line("   " + RecipeBands.Word(s.StartColor) + (s.EndColor != s.StartColor ? " to " + RecipeBands.Word(s.EndColor).ToLowerInvariant() : ""), "Text.Caption", ColorBrush(s.StartColor)));
            if (s.Hard) title.Children.Add(Line("   hard to get", "Text.Caption", Res("Brush.Warn")));
            sp.Children.Add(title);

            var learn = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            learn.Children.Add(Line("Learn: ", "Text.Caption"));
            if (s.LearnItemId > 0)
            {
                learn.Children.Add(LinkText(NameOf(s.LearnItemId), AtlasIds.Item(s.LearnItemId), "Text.Secondary", QualityBrush(2)));
                if (s.LearnSources.Count == 0) learn.Children.Add(Line(" - no source known", "Text.Caption"));
                foreach (var src in s.LearnSources.Take(1)) { learn.Children.Add(Line(" - ", "Text.Caption")); learn.Children.Add(LinkText(src.Text, src.Link, "Text.Caption")); learn.Children.Add(Line(" (" + src.Label + ")", "Text.Caption")); }
            }
            else learn.Children.Add(Line(s.Learn, "Text.Secondary"));
            sp.Children.Add(learn);

            var mats = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            mats.Children.Add(Line("Materials: ", "Text.Caption"));
            foreach (var m in s.Materials)
            {
                var one = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 0) };
                one.Children.Add(LinkText(m.Count.ToString("N0", CultureInfo.InvariantCulture) + " x " + NameOf(m.ItemId), AtlasIds.Item(m.ItemId), "Text.Secondary"));
                mats.Children.Add(one);
            }
            sp.Children.Add(mats);
            sp.Children.Add(Line("Step " + (s.CostApprox ? "~" : "") + ProfMoney.Format(s.Cost) + " - running total ~" + ProfMoney.Format(s.RunningCost), "Text.Caption", null, 4));
            var why = Line("Why: " + s.Why, "Text.Caption", null, 2); why.TextWrapping = TextWrapping.Wrap; sp.Children.Add(why);
            grid.Children.Add(sp);
            SetTip(card, LazyTip(() => RecipeTip(r)));
            return card;
        }

        // ---- gathering
        void RenderGatherGuide(ProfessionBook b, int start, int target)
        {
            var host = new StackPanel();
            host.Children.Add(Line("Working out the places...", "Text.Secondary", null, 8));
            Body.Children.Add(host);
            int gen = ++guideGen;
            var w = world; var p = prof;
            Task.Run(() => GatherGuideBuilder.Build(b, p, start, target, w)).ContinueWith(t =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (gen != guideGen || tab != "guide") return;
                    host.Children.Clear();
                    if (t.IsFaulted) { host.Children.Add(Line("Could not build the guide: " + t.Exception?.GetBaseException().Message, "Text.Secondary", Res("Brush.Danger"))); return; }
                    ShowGatherGuide(host, t.Result);
                }));
            });
        }

        void ShowGatherGuide(StackPanel host, GatherGuide g)
        {
            lastGather = g;
            if (g.Unavailable != null) { host.Children.Add(Card(Wrap(Line(g.Unavailable, "Text.Body")))); return; }
            var head = new StackPanel();
            head.Children.Add(Line(g.Profession.Name + " " + SkillGuide.Range(g.Start, g.Target) + " - where to go", "Text.Section"));
            var how = Line("By skill bracket: the busiest zones, their level range and what you find there. The skill a place wants is estimated (the game data has no per-herb, per-vein or per-creature skill), so use it as a map, not a promise.", "Text.Caption", null, 4);
            how.TextWrapping = TextWrapping.Wrap; head.Children.Add(how);
            host.Children.Add(Card(head));
            foreach (var br in g.Brackets) host.Children.Add(BracketCard(br, g));
            foreach (var n in g.Notes) { var t = Line(n, "Text.Caption", null, 8); t.TextWrapping = TextWrapping.Wrap; host.Children.Add(t); }
        }

        static TextBlock Wrap(TextBlock t) { t.TextWrapping = TextWrapping.Wrap; return t; }

        FrameworkElement BracketCard(GatherBracket br, GatherGuide g)
        {
            var sp = new StackPanel();
            sp.Children.Add(Line("Skill " + SkillGuide.Range(br.From, br.To), "Text.Section"));
            if (br.Zones.Count == 0) sp.Children.Add(Line("No place known for this bracket in the data you have.", "Text.Secondary", null, 8));
            foreach (var z in br.Zones)
            {
                var head = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
                head.Children.Add(LinkText(z.Zone.Name, z.Zone.Id > 0 ? "zone:" + z.Zone.Id : null, "Text.RowTitle"));
                if (z.Zone.KnownLevel) head.Children.Add(Line("   level " + z.Zone.MinLevel + "-" + z.Zone.MaxLevel, "Text.Caption"));
                head.Children.Add(Line("   " + z.Label, "Text.Caption"));
                sp.Children.Add(head);
                foreach (var f in z.Finds)
                {
                    var row = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
                    row.Children.Add(LinkText(f.Name, f.Link, "Text.Body"));
                    var made = f.Items.Take(4).Select(NameOf).Where(n => !string.Equals(n, f.Name, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (made.Count > 0) row.Children.Add(Line(" - " + string.Join(", ", made), "Text.Secondary"));
                    row.Children.Add(Line("   " + f.Count + " seen, skill ~" + f.EstSkill + " (" + f.Label + ")", "Text.Caption"));
                    sp.Children.Add(row);
                }
            }
            var card = Card(sp, 8);
            card.Tag = br;
            return card;
        }

        // ---- test hooks
        public SkillGuide GuideForTest => lastGuide;
        public GatherGuide GatherForTest => lastGather;
        public string GuideTextForTest() => GuideTextNow();
        public void GuideRangeForTest(int? start, int? target, string scope = null) { guideStart = start; guideTarget = target; if (scope != null) guideScope = scope; RenderBody(); }
        // the gather guide is built off the UI thread: wait for it
        public async Task<GatherGuide> WaitGatherForTest(int ms = 15000)
        {
            for (int i = 0; i < ms / 50 && lastGather == null; i++) await Task.Delay(50);
            return lastGather;
        }
        public double GuideShoppingBottomForTest() { UpdateLayout(); return Body.Children.OfType<FrameworkElement>().TakeWhile(c => !(c.Tag is GuideStep)).Sum(c => c.ActualHeight + c.Margin.Top + c.Margin.Bottom) - 40; }
        public void ScrollForTest(double y) { UpdateLayout(); BodyScroll.ScrollToVerticalOffset(Math.Max(0, y)); UpdateLayout(); }
        public FrameworkElement GuideStepCardForTest(int index) => Body.Children.OfType<Border>().Where(b => b.Tag is GuideStep gs && !gs.IsMilestone).Skip(index).FirstOrDefault();
    }
}
