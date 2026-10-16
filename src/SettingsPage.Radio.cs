using System;
using System.Windows;
using System.Windows.Controls;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // --selftest "radio": Settings > General > Radio Paradise: the player on / off, the favourite channel (every start begins there), the sound quality
    // (2.75: Standard MP3 / High AAC / Lossless FLAC; a change while it plays switches the stream at once) and the music level while people talk in a
    // voice room.
    public partial class SettingsPage
    {
        void LoadRadio(Settings s)
        {
            if (RadioFavBox.Items.Count == 0) foreach (var c in RadioParadise.Channels) RadioFavBox.Items.Add(c);
            RadioBox.IsChecked = !s.RadioOff;
            RadioFavBox.SelectedItem = RadioParadise.Channel(s.RadioChannel);
            RadioCoversBox.IsChecked = !s.RadioNoCovers;
            var q = RadioParadise.ParseQuality(s.RadioQuality);
            RadioQualityStd.IsChecked = q == RadioQuality.Standard;
            RadioQualityHigh.IsChecked = q == RadioQuality.High;
            RadioQualityLossless.IsChecked = q == RadioQuality.Lossless;
            RadioDuckSlider.Value = s.RadioDuck ?? 0.3;
            RadioDuckText.Text = Math.Round(RadioDuckSlider.Value * 100) + " %";
        }

        void Radio_Click(object sender, RoutedEventArgs e)
        {
            if (loading || settings == null) return;
            settings.RadioOff = RadioBox.IsChecked != true;
            SettingsStore.Save(settings);
            host.ApplyRadioSettings();
        }

        // album covers (2.73: the picture the stream names): off = RadioNowPlaying fetches no picture
        void RadioCovers_Click(object sender, RoutedEventArgs e)
        {
            if (loading || settings == null) return;
            settings.RadioNoCovers = RadioCoversBox.IsChecked != true;
            SettingsStore.Save(settings);
            host.ApplyRadioSettings();
        }

        void RadioQuality_Changed(object sender, RoutedEventArgs e)
        {
            if (loading || settings == null) return;
            var q = RadioQualityLossless.IsChecked == true ? RadioQuality.Lossless : RadioQualityHigh.IsChecked == true ? RadioQuality.High : RadioQuality.Standard;
            settings.RadioQuality = q == RadioQuality.Standard ? null : RadioParadise.QualityId(q);
            SettingsStore.Save(settings);
            host.ApplyRadioSettings();
        }

        void RadioFav_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (loading || settings == null || !(RadioFavBox.SelectedItem is RadioChannel c)) return;
            settings.RadioChannel = c.Id;
            SettingsStore.Save(settings);
            host.ApplyRadioSettings();
        }

        void RadioDuck_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (RadioDuckText != null) RadioDuckText.Text = Math.Round(e.NewValue * 100) + " %";
            if (loading || settings == null) return;
            settings.RadioDuck = Math.Round(e.NewValue, 2);
            SettingsStore.Save(settings);
            host.ApplyRadioSettings();
        }

        // ---- for the self-test
        public void SetRadioForTest(bool on) { RadioBox.IsChecked = on; Radio_Click(RadioBox, null); }
        public void SetRadioCoversForTest(bool on) { RadioCoversBox.IsChecked = on; RadioCovers_Click(RadioCoversBox, null); }
        public void SetRadioFavouriteForTest(string id) => RadioFavBox.SelectedItem = RadioParadise.Channel(id);
        public void SetRadioDuckForTest(double v) => RadioDuckSlider.Value = v;
        public void SetRadioQualityForTest(RadioQuality q) => (q == RadioQuality.Lossless ? RadioQualityLossless : q == RadioQuality.High ? RadioQualityHigh : RadioQualityStd).IsChecked = true;
        public FrameworkElement RadioQualityRowForTest => RadioQualityRow;
        public string RadioFavouriteForTest => (RadioFavBox.SelectedItem as RadioChannel)?.Id;
    }
}
