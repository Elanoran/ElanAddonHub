using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using ElansAddonHub.Lodge;

namespace ElansAddonHub
{
    // The realm status dots in the title bar (Lodge feature "realms", see LodgeSession.Realms.cs): one small chip per realm, green up / red down / grey unknown, a tooltip with the details.
    // Hidden while no Lodge is connected, when the server lacks the feature, when it found no Forever realms (available=false) and when the member turned it off (Settings > General).
    public partial class MainWindow
    {
        string realmSig = "";

        void InitRealms()
        {
            Action go = () => { if (Dispatcher.CheckAccess()) RefreshRealms(); else Dispatcher.BeginInvoke(new Action(RefreshRealms)); };
            Session.RealmsChanged += go;
            Session.Changed += go;
        }

        public void RefreshRealms()
        {
            bool show = !settings.RealmStatusOff && Session.RealmsShowable;
            var sig = show ? string.Join("|", Session.Realms.Select(r => r.Label + ":" + r.Status)) : "";
            if (!show) { RealmStrip.Visibility = Visibility.Collapsed; RealmItems.Children.Clear(); realmSig = ""; ShiftBannerForRealms(); return; }
            if (sig != realmSig || RealmItems.Children.Count == 0)
            {
                realmSig = sig;
                RealmItems.Children.Clear();
                foreach (var r in Session.Realms) RealmItems.Children.Add(RealmChip(r));
            }
            else
            {
                // same dots: keep the elements (a tooltip that is open stays), only point them at the newest data
                for (int i = 0; i < RealmItems.Children.Count && i < Session.Realms.Count; i++) ((FrameworkElement)RealmItems.Children[i]).Tag = Session.Realms[i];
            }
            RealmStrip.Visibility = Visibility.Visible;
            ShiftBannerForRealms();
        }

        FrameworkElement RealmChip(RealmVM r)
        {
            var brush = (Brush)FindResource(r.Up ? "Brush.Success" : r.Down ? "Brush.Danger" : "Brush.Text.Tertiary");
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = brush, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = r.Label, Style = (Style)FindResource("Text.Caption"), VerticalAlignment = VerticalAlignment.Center });
            var chip = new Border { Background = Brushes.Transparent, Padding = new Thickness(4, 0, 4, 0), Tag = r, Child = row, Height = 24 };
            var tip = new ToolTip { Content = Session.RealmTip(r) };
            chip.ToolTip = tip;
            ToolTipService.SetInitialShowDelay(chip, 300);
            chip.ToolTipOpening += (s, e) => { if (chip.Tag is RealmVM now) tip.Content = Session.RealmTip(now); };   // "checked 3 min ago" is worked out when it opens
            System.Windows.Automation.AutomationProperties.SetName(chip, r.Name + ", " + r.StatusWord);
            return chip;
        }

        // the update banner sits at the left of the same bar: keep it clear of the dots
        void ShiftBannerForRealms()
        {
            LayoutTitleBar();   // one layout for the whole title bar (MainWindow.Radio.cs)
        }

        // ---- for the self-test
        public bool RealmStripVisibleForTest => RealmStrip.Visibility == Visibility.Visible;
        public System.Collections.Generic.List<(string Label, string Status, string Tip)> RealmChipsForTest =>
            RealmItems.Children.OfType<Border>().Select(b => ((string)((TextBlock)((StackPanel)b.Child).Children[1]).Text,
                ((Ellipse)((StackPanel)b.Child).Children[0]).Fill == FindResource("Brush.Success") ? "UP" : ((Ellipse)((StackPanel)b.Child).Children[0]).Fill == FindResource("Brush.Danger") ? "DOWN" : "UNKNOWN",
                Session.RealmTip((RealmVM)b.Tag))).ToList();
    }
}
