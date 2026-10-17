using System;
using System.Windows;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // Settings > Guild Master > The Cairn (2.90, Lodge feature "cairnjoin"): the Guild Master's "Join the Cairn" switch with the Lodge's public address
    // and (during the beta) an invite code, one calm status line (waiting place / admitted / probation / good / lapsed / refused / the code's
    // problems), and a one-time suggestion card while it is off (Turn on / Not now, remembered on this PC). Officers see the status, not the
    // switch. Everything lives on the Lodge server; the invite code is sent once and never shown again. Hidden on an older Lodge.
    public partial class SettingsPage
    {
        bool cairnFormOpen;

        void InitCairnJoin()
        {
            session.CairnJoinChanged += () => Dispatcher.BeginInvoke(new Action(FillCairnJoin));
            session.Changed += () => Dispatcher.BeginInvoke(new Action(FillCairnJoin));
        }

        void FillCairnJoin()
        {
            if (CairnJoinSection == null || settings == null) return;
            bool wasLoading = loading;
            loading = true;
            try
            {
                bool see = session.CanSeeCairnJoin && (session.Connected || session.TestFed);
                CairnJoinSection.Visibility = see ? Visibility.Visible : Visibility.Collapsed;
                if (!see) return;
                var s = session.CairnJoin;
                bool owner = session.CanSwitchCairn;
                bool env = s?.Env == true;
                CairnJoinBox.IsChecked = s != null && s.On;
                CairnJoinBox.IsEnabled = owner && s != null && !env && s.Atlas;
                bool form = owner && s != null && !env && s.Atlas && (s.On || cairnFormOpen) && s.State != "admitted" && s.State != "refused" && s.State != "revoked";
                CairnJoinForm.Visibility = form ? Visibility.Visible : Visibility.Collapsed;
                if (form && string.IsNullOrWhiteSpace(CairnUrlBox.Text)) CairnUrlBox.Text = session.CairnSuggestedUrl();
                CairnUrlBox.IsEnabled = s == null || !(s.State == "waiting" && s.PublicUrl.Length > 0);
                CairnCodeArea.Visibility = form && s != null && (s.WantsCode || s.Mode == "") ? Visibility.Visible : Visibility.Collapsed;
                CairnJoinSave.Content = s != null && s.State == "waiting" ? "Use this code" : "Ask to join";
                CairnJoinStatusText.Text = s == null ? "Asking your Lodge..." : CairnJoinText.Status(s, DateTime.UtcNow) + (s.HasCode && s.State != "admitted" ? " An invite code is waiting to be used." : "");
                // the one-time suggestion: the Guild Master, a Lodge that can, it is off, not answered before
                CairnJoinCard.Visibility = owner && s != null && !s.On && !env && s.Atlas && s.State != "admitted" && s.State != "refused" && s.State != "revoked"
                    && !settings.CairnJoinCardDismissed && !cairnFormOpen ? Visibility.Visible : Visibility.Collapsed;
            }
            finally { loading = wasLoading; }
        }

        void CairnJoin_Click(object sender, RoutedEventArgs e)
        {
            if (loading) return;
            bool on = CairnJoinBox.IsChecked == true;
            var s = session.CairnJoin;
            if (!on) { cairnFormOpen = false; session.SetCairnJoin(false); return; }
            // switching on: an admitted Lodge simply resumes; otherwise the address (and maybe a code) first
            if (s != null && s.State == "admitted") { session.SetCairnJoin(true); return; }
            cairnFormOpen = true;
            CairnJoinBox.IsChecked = false;          // on only once it is asked (the Lodge answers with the real state)
            FillCairnJoin();
            CairnUrlBox.Focus();
        }

        void CairnJoinSave_Click(object sender, RoutedEventArgs e)
        {
            var url = (CairnUrlBox.Text ?? "").Trim();
            if (url.Length == 0 || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) { CairnJoinStatusText.Text = "Enter your Lodge's public address, starting with https://"; return; }
            var code = CairnCodeArea.Visibility == Visibility.Visible ? (CairnCodeBox.Text ?? "").Trim() : "";
            session.SetCairnJoin(true, url, code);
            CairnCodeBox.Text = "";                  // sent once; never kept or shown in the Outpost
            cairnFormOpen = false;
            CairnJoinStatusText.Text = "Asking the Cairn...";
        }

        void CairnCardOn_Click(object sender, RoutedEventArgs e)
        {
            settings.CairnJoinCardDismissed = true;
            SettingsStore.Save(settings);
            cairnFormOpen = true;
            FillCairnJoin();
            CairnJoinRow.BringIntoView();
            CairnUrlBox.Focus();
        }

        void CairnCardLater_Click(object sender, RoutedEventArgs e)
        {
            settings.CairnJoinCardDismissed = true;
            SettingsStore.Save(settings);
            FillCairnJoin();
        }

        // ---- test hooks
        public bool CairnJoinVisibleForTest => CairnJoinSection.Visibility == Visibility.Visible;
        public bool CairnJoinCardVisibleForTest => CairnJoinCard.Visibility == Visibility.Visible;
        public bool CairnJoinFormVisibleForTest => CairnJoinForm.Visibility == Visibility.Visible;
        public bool CairnCodeVisibleForTest => CairnCodeArea.Visibility == Visibility.Visible;
        public bool CairnJoinOnForTest => CairnJoinBox.IsChecked == true;
        public bool CairnJoinEnabledForTest => CairnJoinBox.IsEnabled;
        public string CairnJoinStatusForTest => CairnJoinStatusText.Text;
        public string CairnJoinTextForTest => CairnJoinRow.Tag as string ?? "";
        public string CairnUrlForTest { get => CairnUrlBox.Text; set => CairnUrlBox.Text = value; }
        public void CairnCodeForTest(string code) => CairnCodeBox.Text = code;
        public void CairnSwitchForTest(bool on) { CairnJoinBox.IsChecked = on; CairnJoin_Click(CairnJoinBox, new RoutedEventArgs()); }
        public void CairnSaveForTest() => CairnJoinSave_Click(null, null);
        public void CairnCardOnForTest() => CairnCardOn_Click(null, null);
        public void CairnCardLaterForTest() => CairnCardLater_Click(null, null);
        public void CairnJoinIntoViewForTest() => CairnJoinSection.BringIntoView();
        public void FillCairnJoinForTest() { cairnFormOpen = false; FillCairnJoin(); }
    }
}
