using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // --selftest "radio": the title bar's Radio Paradise player (see RadioPlayer for the stream rules). It shows the player's state; the window wires
    // it (MainWindow.Radio.cs): settings, links, ducking, the title bar layout.
    public partial class RadioBar : UserControl
    {
        RadioPlayer player;
        Settings settings;
        bool loading;
        public Action<string> OpenLink;                 // the Outpost browser (like the Atlas' outside links)
        public int Mode { get; private set; }           // 0 wide (title up to 240), 1 medium (title up to 140), 2 narrow (no channel box, no title: both in the flyout)

        public RadioBar()
        {
            InitializeComponent();
            foreach (var c in RadioParadise.Channels) { ChannelBox.Items.Add(c); FlyChannelBox.Items.Add(c); }
        }

        public void Init(RadioPlayer p, Settings s)
        {
            player = p; settings = s;
            loading = true;
            Select(p.Channel);
            VolumeSlider.Value = p.Volume;
            loading = false;
            p.Changed += Refresh;
            Refresh();
        }

        public void SetMode(int mode)
        {
            Mode = mode;
            ChannelBox.Visibility = mode == 2 ? Visibility.Collapsed : Visibility.Visible;
            FlyChannelBox.Visibility = mode == 2 ? Visibility.Visible : Visibility.Collapsed;
            titleMax = mode == 0 ? 240 : 140;
            NowText.MaxWidth = Math.Min(titleMax, titleRoom);
            Refresh();
        }

        double titleMax = 240, titleRoom = double.PositiveInfinity;
        const double TitleMin = 48;

        // the room the title bar can give the whole player (the game data chip keeps its own): the title shrinks first, and goes when it would be a stub
        public void SetRoom(double room)
        {
            Chip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double fixedW = Chip.DesiredSize.Width - (NowText.Visibility == Visibility.Visible ? NowText.DesiredSize.Width + 8 : 0);
            double t = room - fixedW - 8;
            titleRoom = t < TitleMin ? 0 : t;
            NowText.MaxWidth = Math.Min(titleMax, titleRoom);
            Refresh();
        }

        void Select(RadioChannel c)
        {
            bool was = loading; loading = true;
            ChannelBox.SelectedItem = c; FlyChannelBox.SelectedItem = c;
            loading = was;
        }

        public void Refresh()
        {
            if (player == null) return;
            Select(player.Channel);
            var ch = player.Channel.Name;
            RpLinkText.Text = "Radio Paradise · " + ch;
            string glyph = "", tip = "Play Radio Paradise · " + ch, now = null, status = null;
            bool retry = false;
            switch (player.State)
            {
                case RadioState.Connecting:
                    glyph = ""; tip = "Pause"; now = player.Attempt > 0 ? "Reconnecting..." : "Connecting..."; status = now; break;
                case RadioState.Playing:
                    glyph = ""; tip = "Pause"; now = player.Title ?? "Radio Paradise · " + ch; status = "Playing " + ch; break;
                case RadioState.Waiting:
                    glyph = ""; tip = "Stop trying"; now = "Reconnecting...";
                    status = "Lost the stream. Trying again in " + Math.Max(1, (int)Math.Round(player.NextWait.TotalSeconds)) + " s (try " + player.Attempt + " of " + RadioParadise.MaxTries + ")."; break;
                case RadioState.GaveUp:
                    glyph = ""; tip = "Retry"; now = "Couldn't connect"; retry = true;
                    status = "Couldn't reach Radio Paradise after " + RadioParadise.MaxTries + " tries."; break;
                case RadioState.Unsupported:
                    now = "Can't play this"; status = "This stream can't be played here" + (player.LastError != null ? " (" + player.LastError + ")." : "."); break;
                case RadioState.NoDevice:
                    now = "No sound device"; status = "No sound output was found. Plug in headphones or speakers and press Play."; break;
                default:
                    status = "Press Play to listen. Nothing connects until you do."; break;
            }
            PlayButton.Content = glyph;
            PlayButton.ToolTip = tip;
            System.Windows.Automation.AutomationProperties.SetName(PlayButton, tip);
            NowText.Text = now ?? "";
            NowText.ToolTip = string.IsNullOrEmpty(now) ? null : now;
            NowText.Visibility = Mode == 2 || titleRoom <= 0 || string.IsNullOrEmpty(now) ? Visibility.Collapsed : Visibility.Visible;
            FlyTitle.Text = player.State == RadioState.Playing && player.Title != null ? player.Title : "";
            FlyTitle.Visibility = FlyTitle.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            FlyStatus.Text = status ?? "";
            RetryButton.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
            VolumeText.Text = Math.Round(VolumeSlider.Value * 100) + " %";
        }

        // a press on the player is its own: no window drag
        void Chip_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

        void Play_Click(object sender, RoutedEventArgs e)
        {
            if (player == null) return;
            if (player.State == RadioState.GaveUp) player.Retry(); else player.Toggle();
        }

        void Retry_Click(object sender, RoutedEventArgs e) => player?.Retry();

        // a channel picked here is for this session; the favourite (the one every start begins with) is set in Settings
        void Channel_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (loading || player == null || !(((ComboBox)sender).SelectedItem is RadioChannel c)) return;
            player.SetChannel(c.Id);
            Refresh();
        }

        void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            VolumeText.Text = Math.Round(e.NewValue * 100) + " %";
            if (loading || player == null) return;
            player.Volume = (float)e.NewValue;
            settings.RadioVolume = Math.Round(e.NewValue, 2);
            SettingsStore.Save(settings);
        }

        void More_Click(object sender, RoutedEventArgs e) { Flyout.IsOpen = !Flyout.IsOpen; }
        void Flyout_Opened(object sender, EventArgs e) { Refresh(); Motion.DialogIn(FlyCard); }

        void RpLink_Click(object sender, RoutedEventArgs e) { Flyout.IsOpen = false; OpenLink?.Invoke(RadioParadise.HomeUrl); }
        void Support_Click(object sender, RoutedEventArgs e) { Flyout.IsOpen = false; OpenLink?.Invoke(RadioParadise.DonateUrl); }

        // ---- for the self-test
        public Button PlayButtonForTest => PlayButton;
        public ComboBox ChannelBoxForTest => ChannelBox;
        public string NowTextForTest => NowText.Visibility == Visibility.Visible ? NowText.Text : "";
        public Popup FlyoutForTest => Flyout;
        public FrameworkElement FlyCardForTest => FlyCard;
        public string RpLinkTextForTest => RpLinkText.Text;
        public Slider VolumeSliderForTest => VolumeSlider;
        public bool RetryShownForTest => RetryButton.Visibility == Visibility.Visible;
        public void ClickSupportForTest() => Support_Click(null, null);
        public void ClickRpLinkForTest() => RpLink_Click(null, null);
        public FrameworkElement ChipForTest => Chip;
    }
}
