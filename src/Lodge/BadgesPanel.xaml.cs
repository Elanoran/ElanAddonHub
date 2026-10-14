using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace ElansAddonHub.Lodge
{
    // The list of badges of one person, grouped by family: earned ones with their date, unearned ones with how to earn them and a progress line where a number exists,
    // secret ones as "???" until earned. For someone else it holds only what they earned. It scrolls inside whatever hosts it (an overlay card, the Settings page).
    public partial class BadgesPanel : UserControl
    {
        public BadgesPanel() { InitializeComponent(); }

        public void Show(IEnumerable<BadgeVM> badges, string summary, string emptyText)
        {
            var list = (badges ?? Enumerable.Empty<BadgeVM>()).ToList();
            GroupList.ItemsSource = LodgeSession.BadgeGroups(list);
            Summary.Text = summary ?? "";
            Summary.Visibility = string.IsNullOrEmpty(summary) ? Visibility.Collapsed : Visibility.Visible;
            Empty.Text = emptyText ?? "";
            Empty.Visibility = list.Count == 0 && !string.IsNullOrEmpty(emptyText) ? Visibility.Visible : Visibility.Collapsed;
        }

        // test hooks
        public IEnumerable<BadgeVM> ItemsForTest => (GroupList.ItemsSource as List<BadgeGroupVM> ?? new List<BadgeGroupVM>()).SelectMany(g => g.Items);
        public string SummaryForTest => Summary.Text;
        public string EmptyForTest => Empty.Visibility == Visibility.Visible ? Empty.Text : "";
    }
}
