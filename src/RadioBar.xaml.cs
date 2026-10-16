using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // --selftest "radio": the title bar's Radio Paradise player (see RadioPlayer for the stream rules, RadioNowPlaying for the covers). It shows the
    // player's state; the window wires it (MainWindow.Radio.cs): settings, links, ducking, the title bar layout. 2.73: one compact pill (cover, the
    // title that scrolls only when it does not fit, play / pause, a small equaliser while it plays); the channels and the volume are in the pop-out.
    public partial class RadioBar : UserControl
    {
        RadioPlayer player;
        RadioNowPlaying songs;
        Settings settings;
        bool loading;
        public Action<string> OpenLink;                 // the Outpost browser (like the Atlas' outside links)
        public int Mode { get; private set; }           // 0 wide (title up to 240), 1 medium (title up to 140), 2 narrow (cover + play only)
        public Func<bool> AnimationsOnForTest;          // self-test: as if Windows animations were on / off (null = Motion.Enabled)
        bool AnimationsOn => AnimationsOnForTest?.Invoke() ?? Motion.Enabled;

        const string GlyphVolume = "", GlyphMuted = "";
        const double MarqueeSpeed = 30, MarqueePauseSec = 2;   // px per second; the rest at each end
        readonly List<RadioButton> chips = new List<RadioButton>();

        public RadioBar()
        {
            InitializeComponent();
            foreach (var c in RadioParadise.Channels)
            {
                var chip = new RadioButton { Content = c.Name, Tag = c, Style = (Style)FindResource("Radio.Chip"), ToolTip = "Switch to " + c.Name + " (this session; the favourite is in Settings > General)" };
                System.Windows.Automation.AutomationProperties.SetName(chip, c.Name + " channel");
                chip.Checked += Chip_Checked;
                chips.Add(chip);
                ChannelChips.Children.Add(chip);
            }
            var fade = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            fade.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
            fade.GradientStops.Add(new GradientStop(Colors.Black, 0.05));
            fade.GradientStops.Add(new GradientStop(Colors.Black, 0.95));
            fade.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
            fade.Freeze();
            marqueeMask = fade;
            IsVisibleChanged += (s, e) => Refresh();
        }

        readonly Brush marqueeMask;

        public void Init(RadioPlayer p, Settings s, RadioNowPlaying np)
        {
            player = p; settings = s; songs = np;
            loading = true;
            Select(p.Channel);
            VolumeSlider.Value = p.Volume;
            loading = false;
            p.Changed += Refresh;
            if (np != null) np.Changed += Refresh;
            Refresh();
        }

        public void SetMode(int mode)
        {
            Mode = mode;
            titleMax = mode == 0 ? 240 : 140;
            Refresh();
        }

        double titleMax = 240, titleRoom = double.PositiveInfinity;
        const double TitleMin = 48;

        // the room the title bar can give the whole pill (the game data chip keeps its own): the title shrinks first, and goes when it would be a stub
        public void SetRoom(double room)
        {
            Chip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double titleW = TitleClip.Visibility == Visibility.Visible ? TitleClip.Width + TitleClip.Margin.Left + TitleClip.Margin.Right : 0;
            double fixedW = Chip.DesiredSize.Width - (double.IsNaN(titleW) ? 0 : titleW);
            double t = room - fixedW - TitleClip.Margin.Left - TitleClip.Margin.Right - 4;
            double r = t < TitleMin ? 0 : t;
            if (Math.Abs(r - titleRoom) < 0.5) return;
            titleRoom = r;
            Refresh();
        }

        void Select(RadioChannel c)
        {
            bool was = loading; loading = true;
            foreach (var chip in chips) chip.IsChecked = chip.Tag == c;
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
                    glyph = ""; tip = "Pause"; now = player.Title ?? "Radio Paradise · " + ch; status = "Playing " + ch + ", live."; break;
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

            // the song: what the stream says now, with the cover it names (RadioNowPlaying fetched it for exactly this title)
            bool playingTitle = player.State == RadioState.Playing && player.Title != null;
            var song = playingTitle ? songs?.Current : null;
            if (song != null && (song.StreamTitle != player.Title || song.Channel != player.Channel)) song = null;
            string artist = null, title = null;
            if (playingTitle) { RadioNowPlaying.SplitTitle(player.Title, out artist, out title); now = artist != null ? artist + " – " + title : title; }
            SetCover(CoverArt, CoverBrush, song?.CoverSmall ?? song?.CoverLarge);
            SetCover(FlyCoverArt, FlyCoverBrush, song?.CoverLarge ?? song?.CoverSmall);
            SongRow.Visibility = playingTitle ? Visibility.Visible : Visibility.Collapsed;
            FlyTitle.Text = title ?? "";
            FlyArtist.Text = artist ?? "";
            FlyArtist.Visibility = FlyArtist.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

            SetTitle(now);
            SetEqualiser(player.State == RadioState.Playing && Mode != 2);

            FlyStatus.Text = status ?? "";
            RetryButton.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
            VolumeText.Text = Math.Round(VolumeSlider.Value * 100) + " %";
            bool muted = player.Muted || player.Volume <= 0.001;
            MuteButton.Content = muted ? GlyphMuted : GlyphVolume;
            MuteButton.ToolTip = player.Muted ? "Unmute" : "Mute";
            System.Windows.Automation.AutomationProperties.SetName(MuteButton, player.Muted ? "Unmute the radio" : "Mute the radio");
        }

        static void SetCover(Border art, ImageBrush brush, ImageSource img)
        {
            if (brush.ImageSource != img) brush.ImageSource = img;
            art.Visibility = img != null ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---- the title: static when it fits; otherwise it scrolls to its end and back at MarqueeSpeed, resting MarqueePauseSec at each end
        // (with Windows animations off it stays put, trimmed with "...", the full text in the tooltip)
        string marqueeKey;
        bool marqueeOn;

        void SetTitle(string now)
        {
            bool show = Mode != 2 && titleRoom > 0 && !string.IsNullOrEmpty(now) && IsVisible;
            if (NowText.Text != (now ?? "")) NowText.Text = now ?? "";
            TitleClip.ToolTip = string.IsNullOrEmpty(now) ? null : now;
            TitleClip.Visibility = Mode != 2 && titleRoom > 0 && !string.IsNullOrEmpty(now) ? Visibility.Visible : Visibility.Collapsed;
            double max = Math.Max(0, Math.Min(titleMax, titleRoom));
            NowText.ClearValue(WidthProperty);
            NowText.TextTrimming = TextTrimming.None;
            NowText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double w = Math.Ceiling(NowText.DesiredSize.Width);
            bool overflow = w > max + 0.5;
            TitleClip.Width = overflow ? max : w;
            if (!show || !overflow) { StopMarquee(); return; }
            if (!AnimationsOn) { StopMarquee(); NowText.Width = max; NowText.TextTrimming = TextTrimming.CharacterEllipsis; return; }
            var key = now + "|" + max;
            if (marqueeOn && key == marqueeKey) return;
            marqueeKey = key; marqueeOn = true;
            double over = w - max + 8;                         // a little past the end, so the last letter clears the fade
            double run = over / MarqueeSpeed;
            var a = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
            a.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            a.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(MarqueePauseSec))));
            a.KeyFrames.Add(new LinearDoubleKeyFrame(-over, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(MarqueePauseSec + run))));
            a.KeyFrames.Add(new LinearDoubleKeyFrame(-over, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2 * MarqueePauseSec + run))));
            a.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2 * MarqueePauseSec + 2 * run))));
            Timeline.SetDesiredFrameRate(a, 30);
            TitleClip.OpacityMask = marqueeMask;
            NowShift.BeginAnimation(TranslateTransform.XProperty, a);
        }

        void StopMarquee()
        {
            if (!marqueeOn && NowShift.X == 0 && TitleClip.OpacityMask == null) return;
            marqueeOn = false; marqueeKey = null;
            NowShift.BeginAnimation(TranslateTransform.XProperty, null);
            NowShift.X = 0;
            TitleClip.OpacityMask = null;
        }

        // ---- the equaliser: shown while it plays; its bars move only with Windows animations on (otherwise three still bars)
        bool eqOn;
        void SetEqualiser(bool playing)
        {
            bool show = playing && IsVisible;
            Equaliser.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
            bool run = show && AnimationsOn;
            if (run == eqOn) return;
            eqOn = run;
            var bars = new[] { Bar1Scale, Bar2Scale, Bar3Scale };
            var rest = new[] { 0.5, 1.0, 0.7 };
            var from = new[] { 0.3, 1.0, 0.55 };
            var to = new[] { 1.0, 0.35, 0.95 };
            var ms = new[] { 420, 560, 360 };
            for (int i = 0; i < 3; i++)
            {
                if (!run) { bars[i].BeginAnimation(ScaleTransform.ScaleYProperty, null); bars[i].ScaleY = rest[i]; continue; }
                var a = new DoubleAnimation(from[i], to[i], TimeSpan.FromMilliseconds(ms[i])) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
                Timeline.SetDesiredFrameRate(a, 20);
                bars[i].BeginAnimation(ScaleTransform.ScaleYProperty, a);
            }
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
        void Chip_Checked(object sender, RoutedEventArgs e)
        {
            if (loading || player == null || !(((RadioButton)sender).Tag is RadioChannel c)) return;
            player.SetChannel(c.Id);
            Refresh();
        }

        // moving the slider unmutes
        void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (VolumeText != null) VolumeText.Text = Math.Round(e.NewValue * 100) + " %";
            if (loading || player == null) return;
            player.Muted = false;
            player.Volume = (float)e.NewValue;
            settings.RadioVolume = Math.Round(e.NewValue, 2);
            SettingsStore.Save(settings);
            Refresh();
        }

        void Mute_Click(object sender, RoutedEventArgs e)
        {
            if (player == null) return;
            player.Muted = !player.Muted;
            Refresh();
        }

        void Song_Click(object sender, RoutedEventArgs e) { Flyout.IsOpen = !Flyout.IsOpen; }
        void Flyout_Opened(object sender, EventArgs e) { Refresh(); Motion.DialogIn(FlyCard); songs?.WantLarge(true); }
        void Flyout_Closed(object sender, EventArgs e) { songs?.WantLarge(false); }

        void RpLink_Click(object sender, RoutedEventArgs e) { Flyout.IsOpen = false; OpenLink?.Invoke(RadioParadise.HomeUrl); }
        void Support_Click(object sender, RoutedEventArgs e) { Flyout.IsOpen = false; OpenLink?.Invoke(RadioParadise.DonateUrl); }

        // ---- for the self-test
        public Button PlayButtonForTest => PlayButton;
        public string NowTextForTest => TitleClip.Visibility == Visibility.Visible ? NowText.Text : "";
        public Popup FlyoutForTest => Flyout;
        public FrameworkElement FlyCardForTest => FlyCard;
        public string RpLinkTextForTest => RpLinkText.Text;
        public Slider VolumeSliderForTest => VolumeSlider;
        public Button MuteButtonForTest => MuteButton;
        public Button SongButtonForTest => SongButton;
        public Button RetryButtonForTest => RetryButton;
        public Button RpLinkForTest => RpLink;
        public IReadOnlyList<RadioButton> ChipsForTest => chips;
        public RadioChannel CheckedChipForTest => chips.FirstOrDefault(c => c.IsChecked == true)?.Tag as RadioChannel;
        public bool CoverShownForTest => CoverArt.Visibility == Visibility.Visible && CoverBrush.ImageSource != null;
        public bool FlyCoverShownForTest => FlyCoverArt.Visibility == Visibility.Visible && FlyCoverBrush.ImageSource != null;
        public ImageSource CoverForTest => CoverBrush.ImageSource;
        public ImageSource FlyCoverForTest => FlyCoverBrush.ImageSource;
        public string FlyTitleForTest => FlyTitle.Text;
        public string FlyArtistForTest => FlyArtist.Visibility == Visibility.Visible ? FlyArtist.Text : "";
        public bool RetryShownForTest => RetryButton.Visibility == Visibility.Visible;
        public bool TitleShownForTest => TitleClip.Visibility == Visibility.Visible;
        public double TitleWidthForTest => TitleClip.Visibility == Visibility.Visible ? TitleClip.Width : 0;
        public bool TitleTrimmedForTest => NowText.TextTrimming != TextTrimming.None;
        public bool MarqueeRunningForTest => marqueeOn;
        public double MarqueeOffsetForTest => NowShift.X;
        public bool EqualiserShownForTest => Equaliser.Visibility == Visibility.Visible;
        public bool EqualiserAnimatingForTest => eqOn;
        public FrameworkElement EqualiserForTest => Equaliser;
        public void ClickSupportForTest() => Support_Click(null, null);
        public void ClickRpLinkForTest() => RpLink_Click(null, null);
        public FrameworkElement ChipForTest => Chip;
    }
}
