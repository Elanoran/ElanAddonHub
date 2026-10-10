using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ElansAddonHub
{
    public enum DialogKind { Info, Question, Warning, Danger, Error }

    // The hub's own message box: dark card, kind icon, English buttons, Enter/Esc. Use Dialog.* instead of MessageBox.
    public partial class ThemedDialog : Window
    {
        Window scrim;
        public bool Result { get; private set; }
        public FrameworkElement RootForTest => Outer;

        ThemedDialog() { InitializeComponent(); }

        // glyph + the status role it is drawn in (Brush.<role> / Brush.<role>.Tint): status colours only mark status
        static (string glyph, string role) Look(DialogKind k)
        {
            switch (k)
            {
                case DialogKind.Danger: return ("", "Danger");    // bin
                case DialogKind.Error: return ("", "Danger");     // error badge
                case DialogKind.Warning: return ("", "Warn");     // warning
                case DialogKind.Question: return ("", "Info");    // help
                default: return ("", "Info");                     // info
            }
        }

        // confirmText == null: a single "OK" button (info style). danger puts keyboard focus on the safe button.
        public static ThemedDialog Create(DialogKind kind, string title, string text, string confirmText, string cancelText)
        {
            var d = new ThemedDialog();
            var (glyph, role) = Look(kind);
            d.Glyph.Text = glyph;
            d.Glyph.Foreground = (Brush)Application.Current.FindResource("Brush." + role);
            d.Badge.Background = (Brush)Application.Current.FindResource("Brush." + role + ".Tint");
            d.TitleText.Text = title ?? "";
            d.BodyText.Text = text ?? "";
            d.BodyText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

            bool danger = kind == DialogKind.Danger;
            Button cancel = null;
            if (cancelText != null)
            {
                cancel = new Button { Content = cancelText, Style = (Style)Application.Current.FindResource("Button.M"), MinWidth = 80, IsCancel = true };
                cancel.Click += (s, e) => { d.Result = false; d.Close(); };
            }
            var ok = new Button
            {
                Content = confirmText ?? "OK",
                Style = (Style)Application.Current.FindResource(danger ? "Button.Danger.M" : "Button.Primary.M"),
                MinWidth = 80,
                Margin = new Thickness(cancel != null ? 8 : 0, 0, 0, 0),
                IsDefault = !danger,   // Enter confirms, except on destructive questions where Enter keeps the safe choice
                IsCancel = cancel == null,
            };
            ok.Click += (s, e) => { d.Result = true; d.Close(); };
            if (cancel != null) d.Buttons.Children.Add(cancel);
            d.Buttons.Children.Add(ok);
            var focus = danger && cancel != null ? cancel : ok;
            d.Loaded += (s, e) => { focus.Focus(); Motion.DialogIn(d.Root); };
            d.PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape && cancel == null) { d.Result = false; d.Close(); e.Handled = true; } };
            d.MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { d.DragMove(); } catch { } };
            return d;
        }

        // dims the owner window while the dialog is up
        void ShowScrim(Window owner)
        {
            try
            {
                if (owner == null || !owner.IsVisible || owner.WindowState == WindowState.Minimized) return;
                var src = PresentationSource.FromVisual(owner);
                double sx = src?.CompositionTarget?.TransformFromDevice.M11 ?? 1, sy = src?.CompositionTarget?.TransformFromDevice.M22 ?? 1;
                var tl = owner.PointToScreen(new Point(0, 0));
                scrim = new Window
                {
                    WindowStyle = WindowStyle.None, AllowsTransparency = true, ShowInTaskbar = false, ShowActivated = false, Owner = owner,
                    Background = new SolidColorBrush(Color.FromArgb(0x99, 0x08, 0x0A, 0x0C)), ResizeMode = ResizeMode.NoResize,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = tl.X * sx, Top = tl.Y * sy, Width = owner.ActualWidth, Height = owner.ActualHeight,
                };
                scrim.Opacity = Motion.Enabled ? 0 : 1;
                scrim.Show();
                if (Motion.Enabled) scrim.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, Motion.Standard) { EasingFunction = Motion.EaseOut });
            }
            catch { scrim = null; }
        }

        bool closing;
        // closing eases out (scale 0.96 + fade) before the window goes away; the dialog's Result is already set
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            base.OnClosing(e);
            if (closing || e.Cancel || !IsVisible || !Motion.Enabled || Dispatcher.HasShutdownStarted) return;
            closing = true;
            e.Cancel = true;
            try { scrim?.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, Motion.Fast) { EasingFunction = Motion.EaseIn }); } catch { }
            Motion.DialogOut(Root, () => { try { Close(); } catch { } });
        }

        protected override void OnClosed(EventArgs e)
        {
            try { scrim?.Close(); } catch { }
            base.OnClosed(e);
        }

        public static bool Run(Window owner, DialogKind kind, string title, string text, string confirmText, string cancelText)
        {
            try
            {
                if (owner == null) owner = Application.Current?.MainWindow;
                if (owner != null && !owner.IsLoaded) owner = null;
                var d = Create(kind, title, text, confirmText, cancelText);
                if (owner != null && owner.IsVisible) { d.Owner = owner; d.ShowScrim(owner); }
                else d.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                d.ShowDialog();
                return d.Result;
            }
            catch (Exception ex)
            {
                // the UI isn't usable (very early or during shutdown): the plain Windows box is better than nothing
                Services.Util.Log("ThemedDialog fallback: " + ex.Message);
                var r = MessageBox.Show(text ?? title, title ?? Brand.Name, cancelText != null ? MessageBoxButton.OKCancel : MessageBoxButton.OK,
                    kind == DialogKind.Error ? MessageBoxImage.Error : kind == DialogKind.Info ? MessageBoxImage.Information : MessageBoxImage.Warning);
                return r == MessageBoxResult.OK;
            }
        }
    }

    // MessageBox replacement. All calls block like MessageBox.Show; owner may be null.
    public static class Dialog
    {
        // a question with two buttons; danger = red confirm button, focus on the cancel button
        public static bool Confirm(Window owner, string title, string text, string confirmText = "OK", bool danger = false, string cancelText = "Cancel") =>
            ThemedDialog.Run(owner, danger ? DialogKind.Danger : DialogKind.Question, title, text, confirmText, cancelText);

        // a warning-flavoured two-button question (e.g. "this replaces a git copy")
        public static bool ConfirmWarn(Window owner, string title, string text, string confirmText = "Continue", string cancelText = "Cancel") =>
            ThemedDialog.Run(owner, DialogKind.Warning, title, text, confirmText, cancelText);

        public static void Info(Window owner, string title, string text) => ThemedDialog.Run(owner, DialogKind.Info, title, text, "OK", null);
        public static void Warn(Window owner, string title, string text) => ThemedDialog.Run(owner, DialogKind.Warning, title, text, "OK", null);
        public static void Error(Window owner, string title, string text) => ThemedDialog.Run(owner, DialogKind.Error, title, text, "OK", null);
    }
}
