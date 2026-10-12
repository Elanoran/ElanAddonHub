using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ElansAddonHub.Lodge
{
    // The invite form (A person / A guest link, name, rank, expiry, Create link, then Copy / Copy as web link). One control, two homes:
    // Settings > Guild Master (the full admin page) and the Lodge sidebar's Invite sheet. The rules (who may pick which rank, what the
    // older-server fallback is) are the same in both because it is the same code.
    public partial class InviteForm : UserControl
    {
        sealed class Choice { public string Label; public int Value; public override string ToString() => Label; }

        LodgeSession session;
        string inviteRoleFor;
        string lastLink, lastSecretLink;

        public InviteForm()
        {
            InitializeComponent();
            InviteExpiry.ItemsSource = new[]
            {
                new Choice { Label = "1 hour", Value = 1 }, new Choice { Label = "1 day", Value = 24 },
                new Choice { Label = "7 days", Value = 168 }, new Choice { Label = "30 days", Value = 720 },
            };
            InviteExpiry.SelectedIndex = 2;
            InviteUses.ItemsSource = new[]
            {
                new Choice { Label = "1 person", Value = 1 }, new Choice { Label = "3 people", Value = 3 },
                new Choice { Label = "5 people", Value = 5 }, new Choice { Label = "10 people", Value = 10 },
            };
            InviteUses.SelectedIndex = 0;
        }

        // Server errors that arrive while this form is on screen are shown in it (the sheet has no other place); Settings shows them on its own status line.
        public bool ShowServerErrors { get; set; }

        // The Advanced (permanent code) button only belongs on the full admin page.
        public bool AllowAdvanced { get; set; }

        public event Action<InviteVM, string> Created;

        public void Bind(LodgeSession s)
        {
            session = s;
            InviteRole.ItemsSource = RankOptions();
            InviteRole.SelectedItem = RankOptions().FirstOrDefault(r => r.Id == "member") ?? RankOptions().FirstOrDefault();
            session.InviteCreated += ShowCreated;
            session.AdminInvited += (name, role, code) =>
            {
                // a permanent code (older server, or the Advanced button): the link carries the code itself
                SetResult($"Permanent code for {name} ({MemberVM.RoleName(role)})", LodgeSession.InviteLink(session.LodgeAddress, code), null,
                    "This link holds a code that works until you remove the person. Send it privately, and consider a one-time link instead.");
                InviteName.Text = "";
                SetStatus(null);
            };
            session.Error += text => { if (ShowServerErrors && IsVisible) SetStatus(text); };
            session.Changed += UpdateMode;
            UpdateMode();
        }

        List<SettingsPage.RoleOption> RankOptions()
        {
            var ids = InviteRanks.Offered(session?.MyRole ?? "member", session?.SupportsTokens ?? true);
            return ids.Select(r => SettingsPage.Ranks.First(x => x.Id == r)).ToList();
        }

        bool GuestKind => InvGuest.IsChecked == true && session != null && session.SupportsTokens;

        // The form offers one-time links when the server can do them; an older server can only make permanent codes.
        public void UpdateMode()
        {
            if (session == null) return;
            bool links = session.SupportsTokens;
            InviteKindHost.Visibility = links ? Visibility.Visible : Visibility.Collapsed;
            ExpiryArea.Visibility = links ? Visibility.Visible : Visibility.Collapsed;
            AdvancedButton.Visibility = AllowAdvanced && links && session.CanManage ? Visibility.Visible : Visibility.Collapsed;
            bool guest = GuestKind;
            NameArea.Visibility = guest ? Visibility.Collapsed : Visibility.Visible;
            InviteRole.Visibility = guest ? Visibility.Collapsed : Visibility.Visible;
            InviteUses.Visibility = guest ? Visibility.Visible : Visibility.Collapsed;
            RoleCaption.Text = guest ? "People who can use it" : "Rank";
            InviteButton.Content = links ? "Create link" : "Create invite";
            // officers invite below their own rank; only the Guild Master picks any rank
            // (the list depends on the rank AND on whether the server makes one-time links, so both are part of the key)
            var rankKey = session.MyRole + "|" + links;
            if (inviteRoleFor != rankKey)
            {
                inviteRoleFor = rankKey;
                var offered = RankOptions();
                InviteRole.ItemsSource = offered;
                InviteRole.SelectedItem = offered.FirstOrDefault(r => r.Id == "member") ?? offered.FirstOrDefault();
            }
            InviteHelp.Text = links
                ? (guest ? "A guest link lets people in as guests (chat and voice, no files). It stops working after the chosen number of people, or when it expires."
                         : "The link works once. Send it privately: it lets one person in, and then it is used up. For a person who is already in, it adds another device.")
                : "Your Lodge server is older than 2.10, so this makes a permanent code. Update the server (sudo lodge-update) to send one-time links instead.";
            InviteHelp.Foreground = (Brush)Application.Current.Resources[links ? "Brush.Text.Secondary" : "Brush.Warn"];
        }

        void InviteKind_Changed(object sender, RoutedEventArgs e) { if (session != null) UpdateMode(); }

        void InviteName_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) Invite_Click(null, null); }

        void SetStatus(string text)
        {
            FormStatus.Text = text ?? "";
            FormStatus.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        void Invite_Click(object sender, RoutedEventArgs e)
        {
            if (session == null) return;
            var role = (InviteRole.SelectedItem as SettingsPage.RoleOption)?.Id ?? "member";
            if (session.SupportsTokens)
            {
                var hours = (InviteExpiry.SelectedItem as Choice)?.Value ?? 168;
                if (GuestKind) { SetStatus(null); session.CreateInvite("guest", "", "guest", hours, (InviteUses.SelectedItem as Choice)?.Value ?? 1); return; }
                var who = InviteName.Text.Trim();
                if (who.Length == 0) { SetStatus("Type their name first"); return; }
                SetStatus(null);
                session.CreateInvite("person", who, role, hours);
                return;
            }
            var name = InviteName.Text.Trim();
            if (name.Length == 0) { SetStatus("Type their name first"); return; }
            SetStatus(null);
            session.Invite(name, role);
        }

        // Advanced: the old kind of invite, a code that works until the person is removed. Only on purpose.
        void Advanced_Click(object sender, RoutedEventArgs e)
        {
            var name = InviteName.Text.Trim();
            if (name.Length == 0) { SetStatus("Type their name first"); return; }
            if (!Dialog.Confirm(Window.GetWindow(this), "Make a permanent code?",
                "A permanent code works until you remove the person, and anyone who sees the link can use it. A one-time link is safer: it is used up the first time it is opened. Only make a code for someone whose Outpost is too old for invite links.",
                "Make a code", danger: true)) return;
            session.Invite(name, (InviteRole.SelectedItem as SettingsPage.RoleOption)?.Id ?? "member");
        }

        // a ready-made person invite: "Send a new device link" on a profile card (the rank is the person's own; the server adds a device for them)
        public void StartDeviceLink(string name, string role)
        {
            ResetChoices();
            InviteName.Text = name ?? "";
            var opt = RankOptions().FirstOrDefault(r => r.Id == role);
            InviteResult.Visibility = Visibility.Collapsed;
            // never fall back to another rank: a device link must carry the person's own rank, or nothing at all
            if (opt == null) { SetStatus("You can only send a device link to people ranked below you"); return; }
            InviteRole.SelectedItem = opt;
            SetStatus(null);
            Invite_Click(null, null);
        }

        // the sheet always opens on the defaults (a person, Member, 7 days, 1 person), not on whatever the last invite used:
        // a device link made later must not inherit a 1 hour expiry or a guest link's settings
        public void ResetChoices()
        {
            InvPerson.IsChecked = true;
            InviteExpiry.SelectedIndex = 2;
            InviteUses.SelectedIndex = 0;
            if (session != null)
            {
                var offered = RankOptions();
                InviteRole.SelectedItem = offered.FirstOrDefault(r => r.Id == "member") ?? offered.FirstOrDefault();
                UpdateMode();
            }
        }

        public void Reset()
        {
            InviteResult.Visibility = Visibility.Collapsed;
            InviteLink.Text = ""; lastLink = lastSecretLink = null;   // a link is shown once: it does not linger in a form that was closed
            InviteName.Text = "";
            SetStatus(null);
        }

        void ShowCreated(InviteVM inv, string link)
        {
            var title = inv.IsGuest ? "Guest link" + (inv.Max > 1 ? $" for {inv.Max} people" : "") + " - copied" : $"Invite for {inv.Name} ({MemberVM.RoleName(inv.Role)}) - copied";
            var note = $"Works {(inv.Max > 1 ? $"for {inv.Max} people, " : "once, ")}expires {AgoText.In(inv.Expires, inv.Now)}. Send it privately (a DM, not a channel). It is shown only now.";
            SetResult(title, link, InviteLinks.MakeWeb(session.LodgeAddress, SecretOf(link)), note);
            InviteName.Text = "";
            SetStatus(null);
            PutOnClipboard(link);
            Created?.Invoke(inv, link);
        }

        // the invite secret inside an elanshub:// link
        static string SecretOf(string link) => InviteLinks.Parse(link).Secret ?? "";

        // the self-test sets this so a test never touches (or overwrites) the real clipboard; null = the real one
        public static Action<string> ClipboardSeam;

        static bool PutOnClipboard(string text)
        {
            var seam = ClipboardSeam;
            if (seam != null) { seam(text); return true; }
            try { Clipboard.SetText(text); return true; } catch { return false; }
        }

        void SetResult(string title, string link, string webLink, string note)
        {
            lastLink = link; lastSecretLink = webLink;
            InviteTitle.Text = title;
            InviteLink.Text = link;
            InviteNote.Text = note;
            CopyWebButton.Visibility = string.IsNullOrEmpty(webLink) ? Visibility.Collapsed : Visibility.Visible;
            InviteResult.Visibility = Visibility.Visible;
        }

        void CopyInvite_Click(object sender, RoutedEventArgs e)
        {
            if (PutOnClipboard(lastLink ?? InviteLink.Text)) InviteTitle.Text = "Copied - now send it to them";
        }

        void CopyWeb_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(lastSecretLink)) return;
            if (PutOnClipboard(lastSecretLink)) InviteTitle.Text = "Web link copied - now send it to them";
        }

        // ---- test hooks
        public string ButtonTextForTest => InviteButton.Content as string;
        public string LinkForTest => InviteLink.Text;
        public string WebLinkForTest => lastSecretLink;
        public string TitleForTest => InviteTitle.Text;
        public string NoteForTest => InviteNote.Text;
        public string ClickHintForTest => InviteClickHint.Text;
        public bool ResultVisibleForTest => InviteResult.Visibility == Visibility.Visible;
        public bool AdvancedVisibleForTest => AdvancedButton.Visibility == Visibility.Visible;
        public bool KindVisibleForTest => InviteKindHost.Visibility == Visibility.Visible;
        public bool CopyWebVisibleForTest => CopyWebButton.Visibility == Visibility.Visible;
        public string StatusForTest => FormStatus.Visibility == Visibility.Visible ? FormStatus.Text : "";
        public string NameForTest => InviteName.Text;
        public string SelectedRoleForTest => (InviteRole.SelectedItem as SettingsPage.RoleOption)?.Id;
        public List<string> RankIdsForTest => (InviteRole.ItemsSource as IEnumerable<SettingsPage.RoleOption>)?.Select(r => r.Id).ToList() ?? new List<string>();
        public bool GuestForTest => GuestKind;
        public void SetGuestKindForTest(bool guest) { (guest ? InvGuest : InvPerson).IsChecked = true; UpdateMode(); }
        public void CreateClickForTest(string name) { InviteName.Text = name; Invite_Click(null, null); }
        public void CopyWebClickForTest() => CopyWeb_Click(null, null);
        public void CopyClickForTest() => CopyInvite_Click(null, null);
        public void SelectRoleForTest(string id) { var o = (InviteRole.ItemsSource as IEnumerable<SettingsPage.RoleOption>)?.FirstOrDefault(r => r.Id == id); if (o != null) InviteRole.SelectedItem = o; }
        public void SelectExpiryForTest(int hours) { var o = (InviteExpiry.ItemsSource as IEnumerable<Choice>)?.FirstOrDefault(c => c.Value == hours); if (o != null) InviteExpiry.SelectedItem = o; }
        public void SelectUsesForTest(int uses) { var o = (InviteUses.ItemsSource as IEnumerable<Choice>)?.FirstOrDefault(c => c.Value == uses); if (o != null) InviteUses.SelectedItem = o; }
        public bool UsesVisibleForTest => InviteUses.Visibility == Visibility.Visible;
        public bool NameVisibleForTest => NameArea.Visibility == Visibility.Visible;
        public void SetResultForDesign(string title, string link, string webLink, string note) => SetResult(title, link, webLink, note);
    }
}
