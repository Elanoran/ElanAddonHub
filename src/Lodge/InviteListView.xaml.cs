using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace ElansAddonHub.Lodge
{
    public partial class InviteListView : UserControl
    {
        LodgeSession session;

        public InviteListView() { InitializeComponent(); }

        // true: only the invites that can still be used (the Invite sheet); false: all of them with their state (Guild Master page)
        public bool PendingOnly { get; set; }

        public void Bind(LodgeSession s)
        {
            session = s;
            session.InvitesChanged += Fill;
            Fill();
        }

        void Fill()
        {
            if (session == null) return;
            var rows = session.Invites.Where(i => !PendingOnly || i.Pending).Select(i => new SettingsPage.InviteRowVM(i) { ShowState = !PendingOnly }).ToList();
            InviteRows.ItemsSource = rows;
            InvitesEmpty.Text = PendingOnly ? "No open invites." : "No invites yet.";
            InvitesEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        void RevokeInvite_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is string id) session.RevokeInvite(id);
        }

        public List<SettingsPage.InviteRowVM> RowsForTest => (InviteRows.ItemsSource as List<SettingsPage.InviteRowVM>) ?? new List<SettingsPage.InviteRowVM>();
        public void RevokeForTest(string id) => session.RevokeInvite(id);
    }
}
