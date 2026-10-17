using System;
using System.Windows;
using System.Windows.Controls;

namespace ElansAddonHub
{
    // The sharing invite on Atlas Home (2.87): ONE calm card for a Lodge member whose Atlas sharing is off - "Turn on" / "Not now" (equal weight) /
    // "What is shared?" (Settings > Privacy). The main window decides when it shows (MainWindow.SharingInvite.cs: once per offer, remembered);
    // this file only draws it. Also the quiet line on item / creature pages where the Cairn's rows would be (Note, AtlasPage.View.cs).
    public partial class AtlasPage
    {
        public Func<string> SharingInviteText = () => null;       // null = no card
        public Action<string> SharingInviteAnswer;                // "on", "no", "what"
        public Action OpenSharingSettings;                        // the quiet line's link: Settings > Lodge, where the switch is

        Border sharingInvite;
        public bool SharingInviteShownForTest => sharingInvite != null && sharingInvite.Visibility == Visibility.Visible && sharingInvite.Child != null;
        public Button SharingInviteOnForTest { get; private set; }
        public Button SharingInviteNotNowForTest { get; private set; }
        public Button SharingInviteWhatForTest { get; private set; }
        public string SharingInviteTextForTest { get; private set; } = "";

        FrameworkElement SharingInviteView()
        {
            sharingInvite = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(0, 0, 0, 24) };
            sharingInvite.SetResourceReference(Border.BackgroundProperty, "Brush.Info.Tint");
            FillSharingInvite();
            return sharingInvite;
        }

        public void FillSharingInvite()
        {
            if (sharingInvite == null) return;
            SharingInviteOnForTest = SharingInviteNotNowForTest = SharingInviteWhatForTest = null; SharingInviteTextForTest = "";
            string text = null;
            try { text = SharingInviteText?.Invoke(); } catch { }
            sharingInvite.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
            if (text == null) { sharingInvite.Child = null; return; }
            SharingInviteTextForTest = text;
            var sp = new StackPanel();
            var t = Tb(text, "Text.Body"); t.TextWrapping = TextWrapping.Wrap; t.MaxWidth = ReadWidth; sp.Children.Add(t);
            var d = Tb("What Elan's Toolkit sees in the world: drops, vendors, where creatures and nodes are, quests and rare finds - ids and counts, no character names.", "Text.Secondary", null, 4);
            d.TextWrapping = TextWrapping.Wrap; d.MaxWidth = ReadWidth; sp.Children.Add(d);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            // "Turn on" and "Not now" weigh the same (no pushy primary)
            var on = new Button { Content = "Turn on", Style = (Style)FindResource("Button.M"), MinWidth = 96 };
            var no = new Button { Content = "Not now", Style = (Style)FindResource("Button.M"), MinWidth = 96, Margin = new Thickness(8, 0, 0, 0) };
            var what = new Button { Content = "What is shared?", Style = (Style)FindResource("GhostButton"), Height = 32, Margin = new Thickness(8, 0, 0, 0) };
            on.Click += (a, e) => { SharingInviteAnswer?.Invoke("on"); FillSharingInvite(); };
            no.Click += (a, e) => { SharingInviteAnswer?.Invoke("no"); FillSharingInvite(); };
            what.Click += (a, e) => SharingInviteAnswer?.Invoke("what");
            row.Children.Add(on); row.Children.Add(no); row.Children.Add(what);
            SharingInviteOnForTest = on; SharingInviteNotNowForTest = no; SharingInviteWhatForTest = what;
            sp.Children.Add(row);
            sharingInvite.Child = sp;
        }
    }
}
