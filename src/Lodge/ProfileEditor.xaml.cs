using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ElansAddonHub.Lodge
{
    public partial class ProfileEditor : UserControl
    {
        public event Action<ProfileEditVM> Saved;
        public ProfileEditVM Model => DataContext as ProfileEditVM;

        public ProfileEditor() { InitializeComponent(); }

        public void Open(ProfileEditVM vm)
        {
            DataContext = vm;
            SaveButton.IsEnabled = vm.Supported;
            Visibility = Visibility.Visible;
            Focus();
        }

        public void Close() => Visibility = Visibility.Collapsed;
        public FrameworkElement PanelForTest => Panel;
        public void ScrollForTest(double offset) => Scroll.ScrollToVerticalOffset(offset);

        void Cancel_Click(object sender, RoutedEventArgs e) => Close();
        void Save_Click(object sender, RoutedEventArgs e) { var vm = Model; Close(); if (vm != null) Saved?.Invoke(vm); }
        void Scrim_Down(object sender, MouseButtonEventArgs e) => Close();
        void Panel_Down(object sender, MouseButtonEventArgs e) => e.Handled = true;
        void OnKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) { Close(); e.Handled = true; } }

        void Avatar_Click(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is AvatarTile t) Model?.SelectAvatar(t.Id);
        }

        void Swatch_Click(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is SwatchTile t) Model?.SelectAccent(t.Id);
        }

        void Hide_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is CharVM c) Model?.HideChanged(c);
        }
    }
}
