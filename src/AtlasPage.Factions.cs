using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ElansAddonHub.Services.Atlas;

namespace ElansAddonHub
{
    // The Factions list's side switch (Outpost 2.77): a segmented choice All | Alliance | Horde | Shared with the count of each (after the other filters), above the
    // filter bar. Picking one narrows the list like any filter (the id gets side=..., no new history step); the counts follow the name filter as it is typed.
    // Also: a faction page is drawn again when the Lodge answers where its members stand with the faction.
    public partial class AtlasPage
    {
        Border sideHost;
        readonly Dictionary<string, RadioButton> sideButtons = new Dictionary<string, RadioButton>();
        readonly Dictionary<string, TextBlock> sideCounts = new Dictionary<string, TextBlock>();

        public IReadOnlyDictionary<string, RadioButton> SideButtonsForTest => sideButtons;
        public string SideCountForTest(string side) => sideCounts.TryGetValue(side ?? "", out var t) ? t.Text : null;

        FrameworkElement SideSwitch(BrowseResult r)
        {
            sideButtons.Clear(); sideCounts.Clear();
            var seg = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var s in r.Sides)
            {
                var side = s.Slug;
                var label = new StackPanel { Orientation = Orientation.Horizontal };
                label.Children.Add(new TextBlock { Text = s.Title, VerticalAlignment = VerticalAlignment.Center });
                var n = Tb(s.Count.ToString("N0", Lodge.CalendarRules.English), "Text.Caption", Res("Brush.Text.Tertiary"));
                n.Margin = new Thickness(4, 0, 0, 0); n.VerticalAlignment = VerticalAlignment.Center;
                label.Children.Add(n);
                var rb = new RadioButton { Style = (Style)FindResource("Segment"), Content = label, GroupName = "atlas-faction-side", IsChecked = s.Current, Tag = "side|" + side };
                rb.ToolTip = side.Length == 0 ? "Every faction" : side == "shared" ? "Factions both sides can earn reputation with" : "Factions of the " + s.Title;
                System.Windows.Automation.AutomationProperties.SetName(rb, s.Title + " factions, " + s.Count);
                rb.Checked += (o, e) =>
                {
                    if (suppress || browse == null || bq.Side == side) return;
                    bq.Side = side;
                    ScheduleFilter();
                };
                sideButtons[side] = rb; sideCounts[side] = n;
                seg.Children.Add(rb);
            }
            sideHost = new Border { Style = (Style)FindResource("SegmentHost"), Child = seg, Margin = new Thickness(0, 0, 0, 12) };
            return sideHost;
        }

        // after a filter ran: the counts and the chosen side follow (no redraw of the bar)
        void UpdateSideSwitch(BrowseResult r)
        {
            if (sideHost == null || sideButtons.Count == 0) return;
            bool was = suppress;
            suppress = true;
            try
            {
                foreach (var s in r.Sides)
                {
                    if (sideCounts.TryGetValue(s.Slug, out var t)) t.Text = s.Count.ToString("N0", Lodge.CalendarRules.English);
                    if (sideButtons.TryGetValue(s.Slug, out var b))
                    {
                        if (b.IsChecked != s.Current) b.IsChecked = s.Current;
                        System.Windows.Automation.AutomationProperties.SetName(b, s.Title + " factions, " + s.Count);
                    }
                }
            }
            finally { suppress = was; }
        }

        // an answer of "where does the Lodge stand with this faction" arrived: a faction page is drawn again to show it
        public void StandingsChanged()
        {
            if (!IsVisible || hosted != null || IsStart || IsData || browse != null) return;
            var id = history.Current ?? "";
            if (id.StartsWith("faction:", StringComparison.Ordinal)) _ = Redraw();
        }
    }
}
