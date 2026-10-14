using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // "Who in the Lodge can craft this?" under a recipe of the Recipes tab: a link per recipe; a click asks the Lodge's Armory (server 2.19) and shows member,
    // character and skill of every shared character that has learned it. Nothing is asked until the link is clicked.
    public partial class ProfessionsPage
    {
        Func<IReadOnlyCollection<int>, LodgeCrafters> crafters;
        readonly HashSet<int> craftersOpen = new HashSet<int>();
        readonly Dictionary<int, StackPanel> craftersHosts = new Dictionary<int, StackPanel>();

        public void UseCrafters(Func<IReadOnlyCollection<int>, LodgeCrafters> ask) { crafters = ask; }

        // an answer arrived: the opened recipes are filled again
        public void CraftersChanged()
        {
            foreach (var kv in craftersHosts.ToList())
                if (craftersOpen.Contains(kv.Key)) FillCrafters(kv.Value, kv.Key);
        }

        FrameworkElement CraftersLink(int spellId)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            var host = new StackPanel { Margin = new Thickness(0, 2, 0, 0), Visibility = Visibility.Collapsed };
            craftersHosts[spellId] = host;
            var link = Line("Who in the Lodge can craft this?", "Text.Caption", Res("Brush.Text.Secondary"));
            link.Cursor = Cursors.Hand;
            link.ToolTip = "Ask the Lodge's Armory which shared characters have learned this recipe";
            link.MouseEnter += (s, e) => link.TextDecorations = TextDecorations.Underline;
            link.MouseLeave += (s, e) => link.TextDecorations = null;
            link.MouseLeftButtonUp += (s, e) => { ToggleCrafters(spellId); e.Handled = true; };
            sp.Children.Add(link);
            sp.Children.Add(host);
            if (craftersOpen.Contains(spellId)) { host.Visibility = Visibility.Visible; FillCrafters(host, spellId); }
            return sp;
        }

        void ToggleCrafters(int spellId)
        {
            if (!craftersHosts.TryGetValue(spellId, out var host)) return;
            if (craftersOpen.Remove(spellId)) { host.Visibility = Visibility.Collapsed; return; }
            craftersOpen.Add(spellId);
            host.Visibility = Visibility.Visible;
            FillCrafters(host, spellId);
        }

        void FillCrafters(StackPanel host, int spellId)
        {
            host.Children.Clear();
            LodgeCrafters r = null;
            try { r = crafters?.Invoke(new[] { spellId }); } catch (Exception ex) { Util.Log("professions: crafters: " + ex.Message); }
            if (r == null || !r.Available) { host.Children.Add(Line(r?.Problem ?? "The Lodge is not available.", "Text.Caption", null, 0)); return; }
            if (r.Rows.Count == 0)
            {
                host.Children.Add(Line(r.Pending ? "Asking the Lodge..." : "Nobody in the Lodge has shared a character that has learned this. Members share them in Settings > Lodge, and the recipes are read by Elan's Hub when a profession window is opened in game.", "Text.Caption"));
                return;
            }
            foreach (var c in r.Rows.OrderByDescending(x => x.Skill).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).Take(30))
            {
                var row = new WrapPanel();
                row.Children.Add(Line(c.Name, "Text.Caption", ClassBrush(c.Class)));
                row.Children.Add(Line(" (" + c.By + ") - " + c.Detail + (c.Partial ? " · recipe list partly read" : ""), "Text.Caption"));
                host.Children.Add(row);
            }
        }

        // ---- test hooks
        public void ToggleCraftersForTest(int spellId) { if (!craftersHosts.ContainsKey(spellId)) RenderBody(); ToggleCrafters(spellId); }
        public List<string> CraftersTextsForTest(int spellId)
        {
            var res = new List<string>();
            if (!craftersHosts.TryGetValue(spellId, out var host)) return res;
            void Walk(DependencyObject o)
            {
                if (o is TextBlock tb) { if (!string.IsNullOrEmpty(tb.Text)) res.Add(tb.Text); return; }
                foreach (var ch in LogicalTreeHelper.GetChildren(o).OfType<DependencyObject>()) Walk(ch);
            }
            Walk(host);
            return res;
        }
    }
}
