using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // Addon health: one card per Elan addon with versions, last diag, the errors it caught (expandable) and a few diag facts.
    // Built in code. Read-only view of the SavedVariables; the buttons copy an anonymous report, open the folder, mark errors as seen.
    public class HealthDialog : Window
    {
        readonly HealthMonitor monitor;
        readonly StackPanel list = new StackPanel();
        readonly TextBlock sub = new TextBlock();
        readonly Button copyButton, seenButton;
        readonly DispatcherTimer copiedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.2) };
        readonly string outpostVersion;
        readonly Border root = new Border();
        readonly List<UIElement> details = new List<UIElement>();
        bool expandAll;

        public FrameworkElement RootForTest => root;
        public string LastCopied { get; private set; }

        static Brush Res(string key) => (Brush)Application.Current.Resources[key];
        static Style Sty(string key) => (Style)Application.Current.Resources[key];
        static Style Sty0(string key) => (Style)Application.Current.Resources[key];

        bool closing;
        // closing eases out (scale 0.96 + fade) before the window goes away
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            base.OnClosing(e);
            if (closing || e.Cancel || !IsVisible || !Motion.Enabled || Dispatcher.HasShutdownStarted) return;
            closing = true;
            e.Cancel = true;
            Motion.DialogOut(root, () => { try { Close(); } catch { } });
        }
        static Brush Frozen(string hex) { var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex); b.Freeze(); return b; }

        public HealthDialog(HealthMonitor monitor, string outpostVersion)
        {
            this.monitor = monitor;
            this.outpostVersion = outpostVersion;
            WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false; Width = 864; Height = Math.Min(760, SystemParameters.WorkArea.Height - 24); WindowStartupLocation = WindowStartupLocation.CenterOwner;
            UseLayoutRounding = true; FontFamily = new FontFamily("Segoe UI");
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

            // an overlay (DESIGN 5/8): Surface3, hairline, radius 14, elevation 2; the 40 px margin holds the shadow
            root.Margin = new Thickness(40); root.Background = Res("Brush.Surface3"); root.BorderBrush = Res("Brush.Divider"); root.BorderThickness = new Thickness(1); root.CornerRadius = new CornerRadius(14);
            root.Effect = new DropShadowEffect { BlurRadius = 40, ShadowDepth = 12, Direction = 270, Opacity = 0.5, Color = Colors.Black };
            Loaded += (s, e) => Motion.DialogIn(root);
            Content = root;

            var grid = new Grid { Margin = new Thickness(24) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.Child = grid;

            // header
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 16), LastChildFill = true };
            var close = new Button { Content = "", Style = (Style)Application.Current.FindResource("CloseButton"), VerticalAlignment = VerticalAlignment.Top, ToolTip = "Close", Margin = new Thickness(0, -8, -8, 0) };
            close.Click += (s, e) => Close();
            DockPanel.SetDock(close, Dock.Right);
            head.Children.Add(close);
            var titles = new StackPanel();
            titles.Children.Add(new TextBlock { Text = "Addon health", Style = Sty("Text.Section") });
            sub.Style = Sty("Text.Secondary"); sub.Margin = new Thickness(0, 2, 0, 0); sub.TextWrapping = TextWrapping.Wrap;
            titles.Children.Add(sub);
            head.Children.Add(titles);
            Grid.SetRow(head, 0);
            grid.Children.Add(head);

            // content
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = list, Padding = new Thickness(0, 0, 8, 0) };
            Motion.SetEdgeFade(scroll, true);
            Grid.SetRow(scroll, 1);
            grid.Children.Add(scroll);

            // footer
            var foot = new DockPanel { Margin = new Thickness(0, 16, 0, 0), LastChildFill = false };
            Button Btn(string text, string style, double minW = 0)
            {
                var b = new Button { Content = text, Style = (Style)Application.Current.FindResource(style), Margin = new Thickness(0, 0, 8, 0) };
                if (minW > 0) b.MinWidth = minW;
                return b;
            }
            copyButton = Btn("Copy report", "Button.Primary.M", 112);
            copyButton.ToolTip = "Copies a plain-text report (errors, stacks, diag results) with character names replaced by char1, char2 ...";
            copyButton.Click += (s, e) => CopyReport();
            var open = Btn("Open SavedVariables folder", "Button.M");
            open.Click += (s, e) => OpenFolder();
            seenButton = Btn("Mark as seen", "Button.M");
            seenButton.ToolTip = "The errors listed now stop counting as new (the badge and the toast)";
            seenButton.Click += (s, e) => monitor.MarkSeen();
            var closeBtn = Btn("Close", "GhostButton");
            closeBtn.Height = 32; closeBtn.Padding = new Thickness(12, 0, 12, 0); closeBtn.FontSize = 13;
            closeBtn.Margin = new Thickness(0);
            closeBtn.IsCancel = true;
            closeBtn.Click += (s, e) => Close();
            DockPanel.SetDock(closeBtn, Dock.Right);
            foot.Children.Add(closeBtn);
            foot.Children.Add(copyButton);
            foot.Children.Add(open);
            foot.Children.Add(seenButton);
            Grid.SetRow(foot, 2);
            grid.Children.Add(foot);

            copiedTimer.Tick += (s, e) => { copiedTimer.Stop(); copyButton.Content = "Copy report"; };
            MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is FrameworkElement fe && !(fe is TextBox) && IsBackground(fe)) try { DragMove(); } catch { } };
            monitor.Changed += OnChanged;
            Closed += (s, e) => { monitor.Changed -= OnChanged; copiedTimer.Stop(); };
            Rebuild();
        }

        static bool IsBackground(FrameworkElement fe) => fe is Border || fe is Grid || fe is StackPanel || fe is DockPanel || fe is TextBlock;

        void OnChanged() { Rebuild(); }

        public string Report() => HealthReader.ReportText(monitor.Report, outpostVersion, true);

        public void CopyForTest() => CopyReport();

        void CopyReport()
        {
            var text = Report();
            LastCopied = text;
            try { Clipboard.SetText(text); copyButton.Content = "Copied"; }
            catch { try { System.Threading.Thread.Sleep(80); Clipboard.SetText(text); copyButton.Content = "Copied"; } catch { copyButton.Content = "Clipboard busy"; } }
            copiedTimer.Stop(); copiedTimer.Start();
        }

        void OpenFolder()
        {
            var dir = monitor.BestFolder();
            if (dir == null) { copyButton.Content = "Copy report"; return; }
            try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true }); } catch (Exception e) { Util.Log("open folder: " + e.Message); }
        }

        // for the screenshot test: open every error's details
        public void ExpandForTest(bool all) { expandAll = all; Rebuild(); }

        void Rebuild()
        {
            var rep = monitor.Report;
            list.Children.Clear();
            details.Clear();
            int newN = rep.NewTotal, errN = rep.ErrorTotal;
            sub.Text = !rep.AnyData
                ? "No saved variables found yet. Log in with an Elan addon, wait a few seconds, then /reload once - WoW writes them only then."
                : (newN > 0 ? $"{newN} new error{(newN == 1 ? "" : "s")} · " : "") + $"{errN} in total · as of the last /reload or logout · read-only";
            sub.Foreground = newN > 0 ? Res("Brush.Danger") : Res("Brush.Text.Secondary");
            seenButton.IsEnabled = newN > 0;
            copyButton.IsEnabled = rep.Addons.Count > 0;
            if (rep.Addons.Count == 0)
            {
                list.Children.Add(new TextBlock { Text = "No Elan addons found in the WoW folder.", Style = Sty("Text.Body"), Foreground = Res("Brush.Text.Secondary"), Margin = new Thickness(0, 8, 0, 0) });
                return;
            }
            foreach (var h in rep.Addons) list.Children.Add(Card(h));
        }

        Border Card(AddonHealth h)
        {
            var status = HealthReader.Status(h, out int level);
            var role = level == 2 ? "Danger" : level == 1 ? "Warn" : "Success";
            var color = Res("Brush." + role);
            // a card inside the overlay sits one step back: Surface2, no outline (DESIGN 4/5)
            var card = new Border { Background = Res("Brush.Surface2"), CornerRadius = new CornerRadius(12), Padding = new Thickness(16), Margin = new Thickness(0, 0, 0, 12) };
            var st = new StackPanel();
            card.Child = st;

            var top = new DockPanel();
            var chip = new Border { Style = Sty0("Chip"), Background = Res("Brush." + role + ".Tint"), VerticalAlignment = VerticalAlignment.Center, MaxWidth = 340 };
            chip.Child = new TextBlock { Text = (level == 2 ? "\u26A0 " : level == 0 ? "\u2713 " : "") + ShortStatus(h, level, status), Style = Sty("Text.Secondary"), Foreground = color, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            chip.ToolTip = status;
            DockPanel.SetDock(chip, Dock.Right);
            top.Children.Add(chip);
            top.Children.Add(new TextBlock { Text = h.Title, Style = Sty("Text.Section"), VerticalAlignment = VerticalAlignment.Center });
            st.Children.Add(top);

            // versions + last diag
            var diagText = h.Last == null ? "no diag yet"
                : $"diag v{h.DiagVersion ?? "?"}{(h.DiagBehind ? " (older)" : "")} \u00B7 {InventoryReader.Ago(h.Last.At)} \u00B7 {HealthReader.RunKind(h.Last)}";
            var line = $"Installed {(h.Installed != null ? "v" + h.Installed : "?")} \u00B7 {diagText}" + (h.HasFile ? $" \u00B7 saved {InventoryReader.Ago(new DateTimeOffset(h.FileWritten, TimeSpan.Zero).ToUnixTimeSeconds())}" : "");
            st.Children.Add(Dim(line, "Text.Secondary", new Thickness(0, 4, 0, 0)));
            if (level == 1) st.Children.Add(new TextBlock { Text = status, Style = Sty("Text.Secondary"), Foreground = Res("Brush.Warn"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });

            // diag facts
            var facts = HealthReader.Facts(h);
            if (facts.Count > 0)
            {
                var fp = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
                foreach (var f in facts) fp.Children.Add(Dim("\u2022 " + f, "Text.Caption", new Thickness(0, 2, 0, 0), true));
                st.Children.Add(fp);
            }

            // errors
            st.Children.Add(new Border { Height = 1, Background = Res("Brush.Divider"), Margin = new Thickness(0, 12, 0, 12) });
            if (h.Bugs.Count == 0)
                st.Children.Add(Dim(h.HasFile ? "No errors caught." : "No data to read yet.", "Text.Secondary", new Thickness(0)));
            else
            {
                st.Children.Add(new TextBlock
                {
                    Text = $"Errors ({h.Bugs.Count}{(h.NewCount > 0 ? ", " + h.NewCount + " new" : "")})" + (h.OtherErrors > 0 ? $" \u00B7 {h.OtherErrors} about other addons not shown" : ""),
                    Style = Sty("Text.RowTitle"), Foreground = h.NewCount > 0 ? Res("Brush.Danger") : Res("Brush.Text.Primary"), Margin = new Thickness(0, 0, 0, 8)
                });
                foreach (var b in h.Bugs) st.Children.Add(ErrorRow(b));
            }
            return card;
        }

        static string ShortStatus(AddonHealth h, int level, string status)
        {
            if (level == 2 || level == 0) return status;
            if (h.Unreadable) return "Unreadable";
            if (!h.HasFile) return h.Installed != null ? "No data yet" : "Not installed";
            if (h.NoDiag) return "No diag yet";
            return "Diag is older";
        }

        static Brush Tint(Brush b, byte alpha)
        {
            if (b is SolidColorBrush sb) { var x = new SolidColorBrush(Color.FromArgb(alpha, sb.Color.R, sb.Color.G, sb.Color.B)); x.Freeze(); return x; }
            return b;
        }

        static TextBlock Dim(string text, string style, Thickness margin, bool wrap = false) =>
            new TextBlock { Text = text, Style = Sty(style), Margin = margin, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis };

        UIElement ErrorRow(HealthBug b)
        {
            // one step further back again: Surface1, no outline; a red dot marks a new error
            var box = new Border { Background = Res("Brush.Surface1"), CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 0, 8) };
            var st = new StackPanel();
            box.Child = st;

            var inner = new StackPanel();
            inner.Children.Add(new TextBox { Text = b.Msg, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Res("Brush.Text.Primary"), FontSize = 12, Padding = new Thickness(0), Margin = new Thickness(0, 0, 0, 8) });
            if (!string.IsNullOrWhiteSpace(b.Stack))
                inner.Children.Add(new TextBox { Text = b.Stack.Replace("\r", ""), IsReadOnly = true, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Res("Brush.Text.Secondary"), FontFamily = (FontFamily)Application.Current.Resources["Font.Mono"], FontSize = 11, Padding = new Thickness(0), TextWrapping = TextWrapping.NoWrap });
            // grows / shrinks (Motion.Expand) instead of popping
            var detail = new Border { Padding = new Thickness(12, 0, 12, 12), Child = inner };
            if (!expandAll) detail.Visibility = Visibility.Collapsed;
            details.Add(detail);

            var chevron = new TextBlock { Style = Sty("Text.Chevron"), Margin = new Thickness(8, 0, 0, 0) };
            if (expandAll) Motion.SetTurn(chevron, true);
            var head = new DockPanel { Background = Brushes.Transparent, Margin = new Thickness(12, 8, 12, 8), Cursor = Cursors.Hand, ToolTip = "Show / hide the details" };
            DockPanel.SetDock(chevron, Dock.Right);
            head.Children.Add(chevron);
            var meta = new TextBlock { Style = Sty("Text.Caption"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12, 2, 0, 0) };
            DockPanel.SetDock(meta, Dock.Right);
            meta.Text = $"x{b.Count} \u00B7 v{b.Version ?? "?"}{(b.Older ? " (older)" : "")} \u00B7 {InventoryReader.Ago(b.At)}";
            head.Children.Add(meta);
            if (b.IsNew)
            {
                var dot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = Res("Brush.Danger"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), ToolTip = "New" };
                DockPanel.SetDock(dot, Dock.Left);
                head.Children.Add(dot);
            }
            var msg = new StackPanel();
            msg.Children.Add(new TextBlock { Text = FirstLine(b.Msg), Style = Sty("Text.Body"), TextTrimming = TextTrimming.CharacterEllipsis });
            if (b.FirstLine.Length > 0) msg.Children.Add(new TextBlock { Text = b.FirstLine, Style = Sty("Text.Caption"), FontFamily = (FontFamily)Application.Current.Resources["Font.Mono"], TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) });
            head.Children.Add(msg);
            head.MouseLeftButtonUp += (s, e) =>
            {
                bool open = !Motion.GetExpand(detail);
                Motion.SetExpand(detail, open);
                Motion.SetTurn(chevron, open);
            };
            st.Children.Add(head);
            st.Children.Add(detail);
            if (expandAll) Motion.SetExpand(detail, true);
            return box;
        }

        static string FirstLine(string s) => (s ?? "").Replace("\r", "").Split('\n')[0];

        // one window at a time
        static HealthDialog open;
        public static HealthDialog ShowFor(Window owner, HealthMonitor monitor, string version)
        {
            if (open != null && open.IsLoaded) { open.Activate(); return open; }
            var d = new HealthDialog(monitor, version);
            if (owner != null && owner.IsVisible) d.Owner = owner;
            d.Closed += (s, e) => { if (open == d) open = null; };
            open = d;
            d.Show();
            return d;
        }
    }
}
