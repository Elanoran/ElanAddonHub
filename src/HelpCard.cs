using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using ElansAddonHub.Services.WorldMap;

namespace ElansAddonHub
{
    // ============================================================================================================================
    // Short in-app how-to cards (no web page): a title, numbered steps and "what it does". Opened by a page id, so any part of the app (the world
    // map's live layer now, the Lodge's member cards later) can show the same help:  HelpPages.Open(HelpPages.LivePosition, owner)
    // ============================================================================================================================
    public static class HelpPages
    {
        public const string LivePosition = "help:live-position";
        public static readonly string[] All = { LivePosition };
        public static bool IsHelpId(string id) => id != null && All.Contains(id);

        // self-test seam: when set, no window opens; the hook gets the page id
        public static Action<string> TestHook;
        public static string LastOpenedForTest { get; private set; }

        // the card for a page id (not shown); null for an unknown id
        public static HelpCard Create(string id)
        {
            switch (id)
            {
                case LivePosition:
                    return new HelpCard(LivePositionText.HowToTitle, LivePositionText.HowToSteps, "What it does", LivePositionText.HubDescription);
                default: return null;
            }
        }

        // shows the card (not modal: the map stays usable); false for an unknown id
        public static bool Open(string id, Window owner = null)
        {
            if (!IsHelpId(id)) return false;
            LastOpenedForTest = id;
            if (TestHook != null) { TestHook(id); return true; }
            try
            {
                var card = Create(id);
                if (owner == null) owner = Application.Current?.MainWindow;
                if (owner != null && owner.IsVisible) { card.Owner = owner; card.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
                else card.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                card.Show();
                return true;
            }
            catch (Exception e) { Services.Util.Log("help card " + id + ": " + e.Message); return false; }
        }
    }

    public sealed class HelpCard : Window
    {
        readonly Border root = new Border();
        public string TitleForTest { get; }
        public IReadOnlyList<string> StepsForTest { get; }
        public string AboutForTest { get; }
        public FrameworkElement RootForTest => root;

        static Brush Res(string key) => (Brush)Application.Current.Resources[key];
        static Style Sty(string key) => (Style)Application.Current.Resources[key];

        public HelpCard(string title, IList<string> steps, string aboutHeader, string about)
        {
            TitleForTest = title; StepsForTest = steps.ToList(); AboutForTest = about;
            Title = title;
            WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false; Width = 560; SizeToContent = SizeToContent.Height;
            UseLayoutRounding = true; FontFamily = new FontFamily("Segoe UI");
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            // an overlay (DESIGN 5/8): Surface3, hairline, radius 14, elevation 2; the margin holds the shadow
            root.Margin = new Thickness(32); root.Background = Res("Brush.Surface3"); root.BorderBrush = Res("Brush.Divider"); root.BorderThickness = new Thickness(1); root.CornerRadius = new CornerRadius(14);
            root.Effect = Application.Current.TryFindResource("Elevation.2") as Effect ?? new DropShadowEffect { BlurRadius = 40, ShadowDepth = 12, Direction = 270, Opacity = 0.5, Color = Colors.Black };
            Content = root;
            Loaded += (s, e) => Motion.DialogIn(root);
            PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape || e.Key == Key.Enter) { Close(); e.Handled = true; } };
            MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch { } };

            var stack = new StackPanel { Margin = new Thickness(24) };
            root.Child = stack;
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
            var close = new Button { Content = "", Style = Sty("CloseButton"), VerticalAlignment = VerticalAlignment.Top, ToolTip = "Close", Margin = new Thickness(0, -8, -8, 0) };
            close.Click += (s, e) => Close();
            DockPanel.SetDock(close, Dock.Right);
            head.Children.Add(close);
            head.Children.Add(new TextBlock { Text = title, Style = Sty("Text.Section"), TextWrapping = TextWrapping.Wrap });
            stack.Children.Add(head);

            for (int i = 0; i < steps.Count; i++)
            {
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
                var num = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = Res("Brush.Info.Tint"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 12, 0) };
                num.Child = new TextBlock { Text = (i + 1).ToString(), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold, Foreground = Res("Brush.Info") };
                DockPanel.SetDock(num, Dock.Left);
                row.Children.Add(num);
                row.Children.Add(new TextBlock { Text = steps[i], Style = Sty("Text.Body"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
                stack.Children.Add(row);
            }
            stack.Children.Add(new TextBlock { Text = aboutHeader, Style = Sty("Text.RowTitle"), Margin = new Thickness(0, 10, 0, 6) });
            stack.Children.Add(new TextBlock { Text = about, Style = Sty("Text.Secondary"), TextWrapping = TextWrapping.Wrap });
            var ok = new Button { Content = "Got it", Style = Sty("Button.Primary.M"), MinWidth = 90, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0), IsDefault = true };
            ok.Click += (s, e) => Close();
            stack.Children.Add(ok);
        }

        bool closing;
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            base.OnClosing(e);
            if (closing || e.Cancel || !IsVisible || !Motion.Enabled || Dispatcher.HasShutdownStarted) return;
            closing = true;
            e.Cancel = true;
            Motion.DialogOut(root, () => { try { Close(); } catch { } });
        }
    }
}
