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
    // The Camping tab: WoW Forever's campfire kits and the camp objects every profession can make, read from the client's own tables (see Camping.cs), with
    // which of the user's characters can make which. Rules that are not in the tables are shown separately and marked "from guides".
    public partial class ProfessionsPage
    {
        void RenderCamp(ProfessionBook b)
        {
            var camp = b.Camp;
            if (!camp.HasData) { Body.Children.Add(EmptyState("No camping data in this game build", "The recipe tables hold no \"Tier\" camp recipes. Camping arrives with WoW Forever's later builds; nothing is guessed.")); return; }

            // ---- rules
            var rules = new StackPanel();
            rules.Children.Add(Line("How camping works", "Text.Section"));
            foreach (var l in RuleLines(camp)) { var t = Line(l.Key, "Text.Body", null, 6); t.TextWrapping = TextWrapping.Wrap; rules.Children.Add(t); if (!string.IsNullOrEmpty(l.Value)) { var s = Line(l.Value, "Text.Caption", null, 0); s.TextWrapping = TextWrapping.Wrap; rules.Children.Add(s); } }
            Body.Children.Add(Card(rules));

            // ---- kits
            Body.Children.Add(Section("Campfire kits", "A cook makes the kit; each kit takes more camp objects. Journeyman and Expert need their Blueprint first."));
            foreach (var k in camp.Kits) Body.Children.Add(CampCard(k, b));

            // ---- objects by profession
            Body.Children.Add(Section("Camp objects by profession", "Every profession has a first object from skill 20 or so; the higher tiers replace it and keep its benefit."));
            foreach (var grp in camp.ByProfession())
            {
                var head = Line(grp.Key, "Text.RowTitle", null, 8);
                head.Margin = new Thickness(0, 8, 0, 8);
                Body.Children.Add(head);
                foreach (var o in grp) Body.Children.Add(CampCard(o, b));
            }
        }

        // the rules of camping: what the tables say first, then what only the guides say
        List<KeyValuePair<string, string>> RuleLines(CampBook camp)
        {
            var res = new List<KeyValuePair<string, string>>();
            var kits = camp.Kits;
            if (kits.Count > 0)
            {
                res.Add(new KeyValuePair<string, string>("A campfire kit takes " + string.Join(", ", kits.Select(k => k.Capacity)) + " camp objects (" + string.Join(" / ", kits.Select(k => k.Name.Replace(" Kit", ""))) + ").", "From the game data: the kits' own text."));
                var first = kits[0];
                if (first.FireMs > 0) res.Add(new KeyValuePair<string, string>("A fire burns " + Minutes(first.FireMs) + " minutes.", "From the game data: the kit spell's duration. (Guides often say about 10 minutes.)"));
                if (first.BenefitMinutes > 0) res.Add(new KeyValuePair<string, string>("Sit or craft near the fire for " + first.BenefitMinutes + " minute" + (first.BenefitMinutes == 1 ? "" : "s") + " and the objects' benefits apply.", "From the game data."));
            }
            if (!string.IsNullOrEmpty(camp.CooldownText)) res.Add(new KeyValuePair<string, string>("All camping features share a cooldown of " + camp.CooldownText + ": each player can place one object per " + camp.CooldownText + ".", "From the game data: the objects' own text."));
            res.Add(new KeyValuePair<string, string>("A higher object placed over a lower one replaces it and gives its benefit too.", "From the game data: \"May be placed over ... to replace it\"."));
            res.Add(new KeyValuePair<string, string>("Several buffs do not stack with the matching class buff (e.g. Strength of Earth Totem, Power Word: Fortitude); each object lists its own.", "From the game data."));
            res.Add(new KeyValuePair<string, string>("Camping is unlocked by the quest \"The Great Outdoors\" around level 5 and needs Cooking for the kit.", "From guides, not in the game tables the Outpost reads."));
            res.Add(new KeyValuePair<string, string>("Campsites can also offer vendors and repairs (Reagent Bot, Repair Bot).", "From the objects' text for those two; the rest is from guides."));
            res.Add(new KeyValuePair<string, string>("There is no Camping skill: camp objects are ordinary recipes of their profession, so the profession's skill gates them.", "From the game data: no \"Camping\" skill line exists in this build."));
            return res;
        }

        static string Minutes(long ms) => (ms / 60000.0).ToString("0.#", CultureInfo.InvariantCulture);

        FrameworkElement CampCard(CampItem o, ProfessionBook b)
        {
            var r = o.Recipe;
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var card = Card(grid, 8);
            card.Tag = o;
            var icon = IconTile(r.IconId, r.Name, r.Quality, 32); icon.VerticalAlignment = VerticalAlignment.Top;
            grid.Children.Add(icon);
            var sp = new StackPanel();
            Grid.SetColumn(sp, 1);
            var title = new WrapPanel();
            title.Children.Add(LinkText(o.Name + (o.Side.Length > 0 ? " (" + o.Side + ")" : ""), o.ItemId > 0 ? AtlasIds.Item(o.ItemId) : AtlasIds.Spell(r.SpellId), "Text.RowTitle", QualityBrush(r.Quality)));
            title.Children.Add(new Border { Style = Sty("Chip"), Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(8, 0, 8, 0), MinHeight = 20, Child = Line(o.IsKit ? "Kit, takes " + o.Capacity + " objects" : "Tier " + o.Tier, "Text.Caption") });
            sp.Children.Add(title);
            if (!string.IsNullOrWhiteSpace(o.UseText)) { var t = Line(o.UseText, "Text.Secondary", null, 4); t.TextWrapping = TextWrapping.Wrap; sp.Children.Add(t); }
            var facts = new List<string>();
            if (!string.IsNullOrEmpty(o.ExclusiveWith)) facts.Add("Does not stack with " + o.ExclusiveWith);
            if (!string.IsNullOrEmpty(o.Upgrades)) facts.Add("Replaces " + o.Upgrades + " (keeps its benefit)");
            if (facts.Count > 0) { var t = Line(string.Join(" - ", facts), "Text.Caption", null, 4); t.TextWrapping = TextWrapping.Wrap; sp.Children.Add(t); }
            sp.Children.Add(Line(o.Profession + (r.Low > 0 ? " - made from skill " + r.Low + " (the recipe's band " + r.Low + "-" + r.High + ")" : " - no skill gate in the recipe table"), "Text.Caption", null, 4));

            var rw = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            rw.Children.Add(Line("Needs: ", "Text.Caption"));
            foreach (var g in r.Reagents)
            {
                var one = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 0) };
                one.Children.Add(LinkText(g.Count + " x " + NameOf(g.ItemId), AtlasIds.Item(g.ItemId), "Text.Secondary"));
                if (chars.Count > 0) one.Children.Add(Line(" (have " + Have(g.ItemId) + ")", "Text.Caption", Have(g.ItemId) >= g.Count ? Res("Brush.Success") : null));
                rw.Children.Add(one);
            }
            sp.Children.Add(rw);

            if (o.NeedsBlueprint)
            {
                var bw = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
                bw.Children.Add(Line("Learn it from ", "Text.Caption"));
                bw.Children.Add(LinkText(NameOf(o.BlueprintItemId), AtlasIds.Item(o.BlueprintItemId), "Text.Secondary", QualityBrush(2)));
                var srcs = sources?.SourcesOf(o.BlueprintItemId, 2) ?? new List<SourceLine>();
                if (srcs.Count == 0) bw.Children.Add(Line(" - no source known yet (the Classic baseline has no Forever blueprints; the Lodge will once members find one)", "Text.Caption"));
                foreach (var s in srcs) { bw.Children.Add(Line(" - ", "Text.Caption")); bw.Children.Add(LinkText(s.Text, s.Link, "Text.Caption")); bw.Children.Add(Line(" (" + s.Label + ")", "Text.Caption")); }
                sp.Children.Add(bw);
            }
            else sp.Children.Add(Line("No blueprint: learned with the profession", "Text.Caption", null, 4));

            // who can make it
            var cw = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            var makers = chars.Where(c => c.Has(o.Profession)).OrderByDescending(c => c.SkillOf(o.Profession)).ToList();
            if (makers.Count == 0) cw.Children.Add(Line(chars.Count == 0 ? "No characters loaded" : "None of your characters has " + o.Profession, "Text.Caption"));
            foreach (var c in makers)
            {
                bool skillOk = c.SkillOf(o.Profession) >= r.Low;
                bool mats = CraftNow.Times(r, c.Items) >= 1;
                string state = !skillOk ? "needs skill " + r.Low : mats ? (o.NeedsBlueprint ? "can make it if the blueprint is known" : "can make it now") : "needs reagents";
                var chip = new Border { Style = Sty("Chip"), Margin = new Thickness(0, 0, 8, 4), Padding = new Thickness(8, 4, 8, 4), Background = skillOk && mats ? Res("Brush.Success.Tint") : Res("Brush.Overlay.Chip") };
                var inner = new StackPanel { Orientation = Orientation.Horizontal };
                inner.Children.Add(Line(c.Name, "Text.Caption", ClassBrush(c.Class)));
                inner.Children.Add(Line(" " + c.SkillOf(o.Profession) + " - " + state, "Text.Caption", skillOk && mats ? Res("Brush.Text.Primary") : null));
                chip.Child = inner;
                cw.Children.Add(chip);
            }
            sp.Children.Add(cw);
            grid.Children.Add(sp);
            return card;
        }

        // ---- test hooks
        public List<string> CampCardsForTest()
        {
            var res = new List<string>();
            void Walk(DependencyObject d) { if (d is Border bd && bd.Tag is CampItem ci) { res.Add(ci.Name); return; } foreach (var ch in LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>()) Walk(ch); }
            Walk(Body);
            return res;
        }
    }
}
