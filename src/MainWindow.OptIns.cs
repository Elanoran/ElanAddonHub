using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // 3.11 "New things you can turn on" (Services/OptIns.cs): one calm card, once, a few seconds after start (so the Lodge has connected).
    // Every switch starts off and changes its setting at once; "Done" closes it and none of them is asked again. Never on the run that
    // shows the welcome guide, never during a self-test, never over the guide. The Lodge entries wait while you are not a member.
    public partial class MainWindow
    {
        bool optInsShown;
        Border optInsCard;
        List<OptIn> optInsDue;
        public Dictionary<string, CheckBox> OptInSwitchesForTest { get; private set; }
        public bool OptInsShowingForTest => optInsCard != null;

        void InitOptIns()
        {
            bool freshRun = SettingsStore.Fresh;
            Loaded += async (s, e) =>
            {
                if (freshRun || LinkOpen.SelfTestRun) return;
                await Task.Delay(TimeSpan.FromSeconds(8));
                MaybeShowOptIns();
            };
        }

        bool OptInAvailable(OptIn o) => !o.NeedsLodge || (Session != null && Session.Me != null && !Session.IsGuestNow
            && (o.Id != "armory" || Session.SupportsArmory) && (o.Id != "livepos" || Session.SupportsLivePosition));

        public void MaybeShowOptIns()
        {
            if (optInsShown || guide != null || GuideHost.Visibility == Visibility.Visible) return;
            var due = OptIns.Due(settings, OptInAvailable);
            if (due.Count == 0) return;
            optInsShown = true;
            ShowOptIns(due);
        }

        static Brush OptBrush(string key) => (Brush)Application.Current.Resources[key];
        static Style OptStyle(string key) => (Style)Application.Current.FindResource(key);

        void ShowOptIns(List<OptIn> due)
        {
            var scrim = new Border { Background = OptBrush("Brush.Scrim"), CornerRadius = (CornerRadius)Application.Current.Resources["Radius.Window"] };
            var card = new Border
            {
                Background = OptBrush("Brush.Surface3"), BorderBrush = OptBrush("Brush.Divider"), BorderThickness = new Thickness(1), CornerRadius = (CornerRadius)Application.Current.Resources["Radius.Window"],
                Effect = (Effect)Application.Current.Resources["Elevation.2"], Width = 520, MaxHeight = 640, Padding = new Thickness(24), Margin = new Thickness(24),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            scrim.Child = card;
            var sp = new StackPanel();
            card.Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = sp };
            sp.Children.Add(new TextBlock { Text = "New things you can turn on", Style = OptStyle("Text.Title"), TextWrapping = TextWrapping.Wrap });
            sp.Children.Add(new TextBlock { Text = "Each one stays off unless you switch it on here. You'll see this only once, and every switch is also in Settings.", Style = OptStyle("Text.Body"), Foreground = OptBrush("Brush.Text.Secondary"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 16) });
            OptInSwitchesForTest = new Dictionary<string, CheckBox>();
            foreach (var o in due)
            {
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var texts = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
                texts.Children.Add(new TextBlock { Text = o.Title, Style = OptStyle("Text.RowTitle"), TextWrapping = TextWrapping.Wrap });
                texts.Children.Add(new TextBlock { Text = o.Text, Style = OptStyle("Text.Secondary"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
                g.Children.Add(texts);
                var sw = new CheckBox { Style = OptStyle("Switch"), IsChecked = false, VerticalAlignment = VerticalAlignment.Center };
                System.Windows.Automation.AutomationProperties.SetName(sw, o.Title);
                var item = o;
                sw.Click += (s, e) => { SetOptIn(item.Id, sw.IsChecked == true); sw.IsChecked = item.IsOn(settings); };
                Grid.SetColumn(sw, 1);
                g.Children.Add(sw);
                sp.Children.Add(new Border { Background = OptBrush("Brush.Surface2"), CornerRadius = (CornerRadius)Application.Current.Resources["Radius.Card"], Padding = (Thickness)Application.Current.Resources["Pad.Card"], Margin = new Thickness(0, 0, 0, 12), Child = g });
                OptInSwitchesForTest[o.Id] = sw;
            }
            var done = new Button { Content = "Done", Style = OptStyle("Button.Primary.M"), MinWidth = 96, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            done.Click += (s, e) => CloseOptIns(due);
            sp.Children.Add(done);
            optInsCard = card; optInsDue = due;
            GuideHost.Child = scrim;
            GuideHost.Visibility = Visibility.Visible;
            Motion.DialogIn(card);
        }

        void CloseOptIns(List<OptIn> due)
        {
            OptIns.MarkAnswered(settings, due.Select(o => o.Id));
            SettingsStore.Save(settings);
            GuideHost.Visibility = Visibility.Collapsed;
            GuideHost.Child = null;
            optInsCard = null;
        }
        public void OptInsDoneForTest() { if (optInsCard != null) CloseOptIns(optInsDue); }
        public void ShowOptInsForTest() { optInsShown = false; MaybeShowOptIns(); }

        // the same settings as Settings > Addons / Privacy
        public void SetOptIn(string id, bool on)
        {
            switch (id)
            {
                case "autoupdate":
                    settings.AutoUpdate = on;
                    SettingsStore.Save(settings);
                    if (on) AutoUpdateTurnedOn();
                    break;
                case "armory":
                    if (on && (settings.ArmoryChars == null || settings.ArmoryChars.Count == 0))
                    {
                        var main = GuideMainKeyOverride ?? MainCharacter()?.Key;
                        if (main != null) settings.ArmoryChars = new List<string> { main };
                    }
                    settings.ArmoryShareOn = on && settings.ArmoryChars != null && settings.ArmoryChars.Count > 0;
                    SettingsStore.Save(settings);
                    Session?.ArmorySettingsChanged();
                    break;
                case "livepos":
                    settings.ShareLivePosWithLodge = on;
                    if (on) settings.LivePosScope = "officers";
                    SettingsStore.Save(settings);
                    Session?.SendPosition();
                    break;
            }
        }
    }
}
