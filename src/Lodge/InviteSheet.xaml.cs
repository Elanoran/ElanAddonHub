using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ElansAddonHub.Lodge
{
    public partial class InviteSheet : UserControl
    {
        LodgeSession session;
        readonly OverlayController overlay;

        public InviteSheet()
        {
            InitializeComponent();
            overlay = new OverlayController(Overlay, Box);
        }

        public InviteForm FormForTest => Form;
        public InviteListView PendingForTest => Pending;
        public bool IsOpen => overlay.IsOpen;
        public string WhereForTest => Where.Text;

        public void Bind(LodgeSession s)
        {
            session = s;
            Form.Bind(s);
            Pending.Bind(s);
        }

        // prefillName / prefillRole: "Send a new device link" from a profile card creates the person invite for that member straight away
        public void Open(string prefillName = null, string prefillRole = null)
        {
            if (session == null || !session.CanInvite) return;
            Form.Reset();
            Form.ResetChoices();
            Where.Text = "To " + session.LodgeDisplayName + (string.IsNullOrEmpty(session.LodgeAddress) ? "" : " · " + LodgeView.ShortAddress(session.LodgeAddress));
            session.RequestInvites();
            overlay.Open();
            if (!string.IsNullOrEmpty(prefillName)) Form.StartDeviceLink(prefillName, prefillRole);
        }

        public void Close() => overlay.Close();

        void Close_Click(object sender, RoutedEventArgs e) => Close();
        void Scrim_Down(object sender, MouseButtonEventArgs e) { Close(); e.Handled = true; }
        void Box_Down(object sender, MouseButtonEventArgs e) { e.Handled = true; }   // a click inside the card must not reach the scrim
        void Sheet_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Escape && overlay.IsOpen) { Close(); e.Handled = true; } }
    }
}
