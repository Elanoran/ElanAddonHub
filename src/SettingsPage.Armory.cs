using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // Settings > Lodge > Armory: "Share my characters with the Lodge" (off by default) and, under it, one switch per character of this PC. Only ticked characters are
    // uploaded; unticking one, or turning the main switch off, takes it out of the Lodge again (LodgeSession.Armory does the sending).
    public partial class SettingsPage
    {
        List<InvChar> armoryLocal = new List<InvChar>();

        void InitArmory()
        {
            session.ArmoryChanged += () => Dispatcher.BeginInvoke(new Action(() => { if (SecLodge.Visibility == Visibility.Visible) UpdateArmoryNote(); }));
        }

        // (re)build the list of characters; keeps the rows when nothing changed (so a click is never lost to a refresh)
        void FillArmory()
        {
            if (settings == null || ArmoryShareBox == null) return;
            bool wasLoading = loading;
            loading = true;
            try
            {
                ArmoryShareBox.IsChecked = settings.ArmoryShareOn;
                ArmoryRepsBox.IsChecked = settings.ArmoryShareReps;
                ArmoryCharsPanel.Visibility = settings.ArmoryShareOn ? Visibility.Visible : Visibility.Collapsed;
                if (!settings.ArmoryShareOn) { UpdateArmoryNote(); return; }
                var local = ArmoryShare.Local(settings.WowRoot);
                armoryLocal = local;
                var chosen = new HashSet<string>(settings.ArmoryChars ?? new List<string>(), StringComparer.Ordinal);
                var have = ArmoryCharsHost.Children.OfType<HeaderedContentControl>().Select(r => (string)((CheckBox)r.Content).Tag).ToList();
                if (!have.SequenceEqual(local.Select(c => c.Key)))
                {
                    ArmoryCharsHost.Children.Clear();
                    foreach (var c in local.OrderByDescending(c => c.Updated))
                    {
                        var cb = new CheckBox { Style = (Style)FindResource("Switch"), Tag = c.Key, IsChecked = chosen.Contains(c.Key) };
                        cb.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, "Share " + c.Name);
                        cb.Click += ArmoryChar_Click;
                        var line = string.Join(" ", new[] { c.Level > 0 ? "Level " + c.Level : null, c.Core?.Class ?? c.Class }.Where(s => !string.IsNullOrEmpty(s)));
                        var row = new HeaderedContentControl
                        {
                            Style = (Style)FindResource("Setting.Row"), Header = c.Name, Content = cb,
                            Tag = string.Join(" · ", new[] { line, c.Realm }.Where(s => !string.IsNullOrEmpty(s))),
                        };
                        ArmoryCharsHost.Children.Add(row);
                    }
                }
                else
                    foreach (var r in ArmoryCharsHost.Children.OfType<HeaderedContentControl>()) { var cb = (CheckBox)r.Content; cb.IsChecked = chosen.Contains((string)cb.Tag); }
                UpdateArmoryNote();
            }
            finally { loading = wasLoading; }
        }

        void UpdateArmoryNote()
        {
            if (ArmoryCharsNote == null || settings == null) return;
            var chosen = (settings.ArmoryChars ?? new List<string>()).Count(k => armoryLocal.Any(c => c.Key == k));
            string text;
            if (armoryLocal.Count == 0) text = "No characters found yet. Install Elan's Hub, log in on each character once, then /reload or log out.";
            else if (chosen == 0) text = "Nothing is shared yet. Switch on the characters you want your Lodge to see.";
            else text = chosen + (chosen == 1 ? " character is" : " characters are") + " shared" + (chosen >= ArmoryShare.MaxChars ? " (the most the Lodge keeps)" : "") + ".";
            var off = session.ArmoryOfflineLine;
            if (armoryLocal.Count > 0 && off != null && session.Connected) text += " " + (session.SupportsArmory ? off : "This Lodge's server has no Armory yet (it needs 2.16), so nothing is uploaded; your choices are kept.");
            else if (armoryLocal.Count > 0 && !session.Online) text += " You are offline: your choices are uploaded when you connect.";
            if (settings.ArmoryShareReps && chosen > 0 && session.Connected && session.SupportsArmory && !session.SupportsReps) text += " This Lodge's server does not take reputations yet, so they stay on this PC.";
            if (!string.IsNullOrEmpty(session.ArmoryProblem)) text += " " + session.ArmoryProblem;
            ArmoryCharsNote.Text = text;
        }

        void Armory_Click(object sender, RoutedEventArgs e)
        {
            if (loading) return;
            settings.ArmoryShareOn = ArmoryShareBox.IsChecked == true;
            SettingsStore.Save(settings);
            FillArmory();
            session.ArmorySettingsChanged();
        }

        // "Also share their reputations" (off by default): the shared characters are uploaded again with (or without) their reputations
        void ArmoryReps_Click(object sender, RoutedEventArgs e)
        {
            if (loading) return;
            settings.ArmoryShareReps = ArmoryRepsBox.IsChecked == true;
            SettingsStore.Save(settings);
            UpdateArmoryNote();
            session.ArmorySettingsChanged();
        }

        void ArmoryChar_Click(object sender, RoutedEventArgs e)
        {
            if (loading) return;
            var chosen = new List<string>();
            foreach (var r in ArmoryCharsHost.Children.OfType<HeaderedContentControl>())
            {
                var cb = (CheckBox)r.Content;
                if (cb.IsChecked == true && chosen.Count < ArmoryShare.MaxChars) chosen.Add((string)cb.Tag);
                else if (cb.IsChecked == true) cb.IsChecked = false;
            }
            settings.ArmoryChars = chosen;
            SettingsStore.Save(settings);
            UpdateArmoryNote();
            session.ArmorySettingsChanged();
        }

        // self-test hooks
        public bool ArmoryShareForTest => ArmoryShareBox.IsChecked == true;
        public bool ArmoryPanelVisibleForTest => ArmoryCharsPanel.Visibility == Visibility.Visible;
        public void ArmorySwitchForTest(bool on) { ArmoryShareBox.IsChecked = on; Armory_Click(ArmoryShareBox, new RoutedEventArgs()); }
        // "Name|ticked"
        public List<string> ArmoryRowsForTest() => ArmoryCharsHost.Children.OfType<HeaderedContentControl>().Select(r => r.Header + "|" + (((CheckBox)r.Content).IsChecked == true)).ToList();
        public void ArmoryTickForTest(string name, bool on)
        {
            var r = ArmoryCharsHost.Children.OfType<HeaderedContentControl>().First(x => (string)x.Header == name);
            var cb = (CheckBox)r.Content; cb.IsChecked = on; ArmoryChar_Click(cb, new RoutedEventArgs());
        }
        public string ArmoryNoteForTest => ArmoryCharsNote.Text;
        public void ArmoryRepsSwitchForTest(bool on) { ArmoryRepsBox.IsChecked = on; ArmoryReps_Click(ArmoryRepsBox, new RoutedEventArgs()); }
        public bool ArmoryRepsForTest => ArmoryRepsBox.IsChecked == true;
        public void ArmoryScrollForTest() { ArmoryShareBox.BringIntoView(); ArmoryCharsNote.BringIntoView(); }
        public void ArmoryRefreshForTest() => FillArmory();
    }
}
