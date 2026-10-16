using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // --selftest "radio": the Radio Paradise player in the title bar (2.72). It sits right of the search box (or its icon); the update banner and the
    // game data chip start after it, so nothing overlaps at any width (LayoutTitleBar). Nothing connects until Play; Pause, turning it off in Settings
    // and quitting close the stream. While I am in a Lodge voice room and anyone in it talks (me included: the same state that lights the speaking
    // rings), the music fades to Settings' "Music while people talk" level (30 % by default) and comes back after they stop.
    public partial class MainWindow
    {
        public RadioPlayer Radio { get; private set; }
        public RadioNowPlaying RadioSongs { get; private set; }   // the song and its cover, both from the stream (2.73), see RadioNowPlaying
        readonly RadioDucker radioDucker = new RadioDucker();
        DispatcherTimer duckTimer;
        readonly Stopwatch duckClock = Stopwatch.StartNew();
        public Func<bool?> RadioSpeechForTest;          // self-test: who is talking in my voice room (null = not in one)
        public const double RadioWideFrom = 1100, GameDataChipRoom = 280, RadioFullRow = 150;   // below RadioFullRow of room: cover + play only

        void InitRadio()
        {
            Radio = new RadioPlayer { Volume = (float)(settings.RadioVolume ?? 0.6) };
            Radio.SetChannel(RadioParadise.Channel(settings.RadioChannel).Id);   // the favourite, pre-selected; never plays by itself
            radioDucker.Level = settings.RadioDuck ?? 0.3;
            RadioSongs = new RadioNowPlaying();
            RadioPlayerBar.OpenLink = OpenAtlasLink;
            RadioPlayerBar.QualityPicked = PickRadioQuality;   // 2.77: the pop-out's quality is Settings' Sound quality
            RadioPlayerBar.Init(Radio, settings, RadioSongs);
            Radio.Changed += RadioChanged;
            RadioSongs.Changed += () => LayoutTitleBar();   // a longer title can change the pill's width
            RadioPlayerBar.SizeChanged += (s, e) => LayoutTitleBar();
            GameDataBar.IsVisibleChanged += (s, e) => LayoutTitleBar();   // the chip takes its room back from the radio's title while it is up
            ((FrameworkElement)TitleSearch.Parent).SizeChanged += (s, e) => LayoutTitleBar();
            PreviewKeyDown += Radio_MediaKeys;
            ApplyRadioSettings();
        }

        // the media keys (play / pause, stop) while the Outpost has the keyboard
        void Radio_MediaKeys(object sender, KeyEventArgs e)
        {
            if (settings.RadioOff || Radio == null) return;
            if (e.Key == Key.MediaPlayPause) { if (Radio.State == RadioState.GaveUp) Radio.Retry(); else Radio.Toggle(); e.Handled = true; }
            else if (e.Key == Key.MediaStop) { Radio.Stop(); e.Handled = true; }
        }

        // Settings > General > Radio Paradise
        public void ApplyRadioSettings()
        {
            if (Radio == null) return;
            radioDucker.Level = settings.RadioDuck ?? 0.3;
            Radio.SetQuality(RadioParadise.ParseQuality(settings.RadioQuality));   // while it plays: the old stream closes, the new one opens
            if (settings.RadioOff) Radio.Stop();
            else if (!Radio.WantsPlayback) Radio.SetChannel(RadioParadise.Channel(settings.RadioChannel).Id);   // a new favourite shows at once while nothing plays
            RadioPlayerBar.Visibility = settings.RadioOff ? Visibility.Collapsed : Visibility.Visible;
            UpdateRadioSong();
            LayoutTitleBar();
        }

        // 2.77: the pop-out's Quality segments: saved as Settings > General > Sound quality (the Settings row follows) and applied at once. Only the
        // quality: not ApplyRadioSettings, which would put a stopped player back on the favourite channel (the session's channel stays)
        void PickRadioQuality(RadioQuality q)
        {
            settings.RadioQuality = q == RadioQuality.Standard ? null : RadioParadise.QualityId(q);
            SettingsStore.Save(settings);
            SettingsPage.SyncRadioQuality();
            Radio.SetQuality(q);   // while it plays: the old stream closes, the new one opens
            UpdateRadioSong();
        }

        // the song and its cover (the stream's StreamUrl): the picture is fetched only while it plays and only when Settings allows it
        void UpdateRadioSong() => RadioSongs?.Update(Radio.Channel, Radio.Title, Radio.CoverUrl, Radio.State == RadioState.Playing, !settings.RadioOff && !settings.RadioNoCovers);

        // the ducking runs only while the radio wants to play (nothing ticks otherwise)
        void RadioChanged()
        {
            UpdateRadioSong();
            bool on = Radio.WantsPlayback;
            if (on && duckTimer == null)
            {
                duckTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(30) };
                duckTimer.Tick += (s, e) => DuckTick();
                duckTimer.Start();
            }
            else if (!on && duckTimer != null) { duckTimer.Stop(); duckTimer = null; radioDucker.Reset(); Radio.Duck = 1; }
        }

        void DuckTick() => Radio.Duck = (float)radioDucker.Step(RoomSpeech(), duckClock.Elapsed.TotalMilliseconds);

        // anyone talking in my voice room, me included; null when I am not in one (then the radio is left alone)
        bool? RoomSpeech()
        {
            if (RadioSpeechForTest != null) return RadioSpeechForTest();
            var room = Session?.MyRoom;
            if (room == null) return null;
            return Session.Members.Any(m => m.Room == room && m.Speaking);
        }

        // the title bar, left to right: search box (or its icon), the radio, then the update banner / the game data chip; the realm dots and the window
        // buttons keep the right. Called when the width, the search box, the radio or the realm dots change.
        void LayoutTitleBar()
        {
            double left = 16 + (titleSearchNarrow ? 32 : TitleSearchWidth) + 8;
            double right = 112;
            if (RealmStrip.Visibility == Visibility.Visible)
            {
                RealmStrip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                right += RealmStrip.DesiredSize.Width - RealmStrip.Margin.Left - RealmStrip.Margin.Right + 8;   // DesiredSize holds the margin
            }
            if (RadioPlayerBar != null)
            {
                RadioPlayerBar.Margin = new Thickness(left, 0, 0, 0);
                // the room between the search box and the right side; while the game data chip is up it keeps GameDataChipRoom of it; the pill's
                // title shrinks to the room (SetRoom) and the pill becomes cover + play when less than RadioFullRow is left
                var bar = (FrameworkElement)TitleSearch.Parent;
                bool chipUp = GameDataBar.Visibility == Visibility.Visible;
                double room = bar.ActualWidth <= 0 ? double.PositiveInfinity
                    : chipUp ? bar.ActualWidth - left - Math.Max(180, right + 8) - 8 - GameDataChipRoom : bar.ActualWidth - left - right - 8;
                int mode = ActualWidth <= 0 || ActualWidth >= RadioWideFrom ? 0 : 1;   // the title up to 240 / 140; it shrinks further to the room
                if (room < RadioFullRow) mode = 2;
                if (mode != RadioPlayerBar.Mode) RadioPlayerBar.SetMode(mode);
                RadioPlayerBar.SetRoom(room);
                if (RadioPlayerBar.Visibility == Visibility.Visible)
                {
                    RadioPlayerBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    left += RadioPlayerBar.DesiredSize.Width - RadioPlayerBar.Margin.Left + 8;   // DesiredSize holds the margin
                }
            }
            HubBanner.Margin = new Thickness(left, 0, right, 0);
            GameDataBar.Margin = new Thickness(left + 8, 0, Math.Max(180, right + 8), 0);
        }

        // ---- for the self-test
        public RadioBar RadioBarForTest => RadioPlayerBar;
        public RadioDucker RadioDuckerForTest => radioDucker;
        public bool DuckTimerRunningForTest => duckTimer != null;
    }
}
