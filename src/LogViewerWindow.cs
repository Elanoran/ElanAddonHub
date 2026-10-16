using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // --selftest "about": Settings > About > View log. The Outpost's own log (hub.log in the data folder, Util.Log) in a code-style view:
    // times dim, the level's words in its colour (errors red, warnings amber), exception names and stack frames picked out, a search box,
    // All | Warnings | Errors, Copy (the selection, else everything shown) and Open folder. New lines are read while it is open (a light
    // poll of the file's length; the log is rewritten from the start past 512 KB and then read again). Personal details (the user folder,
    // user / PC names, WoW account names, e-mails, IPs, tokens) are hidden in the view AND in Copy, each hidden bit marked.
    // Built in code like HealthDialog; the list virtualises.
    public class LogViewerWindow : Window
    {
        public static string DefaultPath => Path.Combine(Util.DataDir, "hub.log");

        readonly string path;
        readonly LogRedactor redactor;
        readonly LogParser parser;
        readonly List<LogLine> all = new List<LogLine>();
        readonly ObservableCollection<LogRow> shown = new ObservableCollection<LogRow>();
        readonly ListBox list = new ListBox();
        readonly TextBox search = new TextBox();
        readonly RadioButton lvAll = new RadioButton(), lvWarn = new RadioButton(), lvErr = new RadioButton();
        readonly CheckBox follow = new CheckBox();
        readonly TextBlock sub = new TextBlock(), count = new TextBlock(), empty = new TextBlock();
        readonly Border hiddenChip = new Border();
        readonly TextBlock hiddenText = new TextBlock();
        readonly Button copyButton;
        readonly DispatcherTimer tail = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        readonly DispatcherTimer copiedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.2) };
        readonly DispatcherTimer searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        readonly Border root = new Border();
        long offset;
        string pending = "";
        const int MaxLines = 100000;

        public FrameworkElement RootForTest => root;
        public string LastCopied { get; private set; }
        public IReadOnlyList<LogLine> AllForTest => all;
        public IReadOnlyList<LogRow> ShownForTest => shown;
        public ListBox ListForTest => list;
        public string Query => search.Text ?? "";
        public LogLevel MinLevel => lvErr.IsChecked == true ? LogLevel.Error : lvWarn.IsChecked == true ? LogLevel.Warning : LogLevel.Info;
        public string SubtitleForTest => sub.Text;
        public string HiddenForTest => hiddenChip.Visibility == Visibility.Visible ? hiddenText.Text : null;

        static Brush Res(string key) => (Brush)Application.Current.Resources[key];
        static Style Sty(string key) => (Style)Application.Current.Resources[key];

        bool closing;
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            base.OnClosing(e);
            if (closing || e.Cancel || !IsVisible || !Motion.Enabled || Dispatcher.HasShutdownStarted) return;
            closing = true;
            e.Cancel = true;
            Motion.DialogOut(root, () => { try { Close(); } catch { } });
        }

        public LogViewerWindow(string path = null, LogRedactor redactor = null)
        {
            this.path = path ?? DefaultPath;
            this.redactor = redactor ?? new LogRedactor();
            parser = new LogParser(this.redactor);
            WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.CanResizeWithGrip;
            ShowInTaskbar = false; Width = 1040; Height = Math.Min(800, SystemParameters.WorkArea.Height - 24); MinWidth = 560; MinHeight = 420;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            UseLayoutRounding = true; FontFamily = new FontFamily("Segoe UI"); Title = "Outpost log";
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

            // an overlay (DESIGN 5/8): Surface3, hairline, radius 14, elevation 2; the 40 px margin holds the shadow
            root.Margin = new Thickness(40); root.Background = Res("Brush.Surface3"); root.BorderBrush = Res("Brush.Divider"); root.BorderThickness = new Thickness(1); root.CornerRadius = new CornerRadius(14);
            root.Effect = Application.Current.TryFindResource("Elevation.2") as System.Windows.Media.Effects.Effect;
            Loaded += (s, e) => Motion.DialogIn(root);
            Content = root;

            var grid = new Grid { Margin = new Thickness(24) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.Child = grid;

            // header: title, file line, close
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
            var close = new Button { Style = Sty("CloseButton"), VerticalAlignment = VerticalAlignment.Top, ToolTip = "Close", Margin = new Thickness(0, -8, -8, 0) };
            AutomationName(close, "Close");
            close.Click += (s, e) => Close();
            DockPanel.SetDock(close, Dock.Right);
            head.Children.Add(close);
            var titles = new StackPanel();
            titles.Children.Add(new TextBlock { Text = "Outpost log", Style = Sty("Text.Section") });
            sub.Style = Sty("Text.Secondary"); sub.Margin = new Thickness(0, 2, 0, 0); sub.TextTrimming = TextTrimming.CharacterEllipsis;
            titles.Children.Add(sub);
            head.Children.Add(titles);
            grid.Children.Add(head);

            // toolbar: search, level, follow, the hidden-details chip
            var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 12), LastChildFill = false };
            search.Style = Sty("Input"); search.Tag = "Search the log"; search.Width = 280; search.Height = 32; search.VerticalContentAlignment = VerticalAlignment.Center;
            AutomationName(search, "Search the log");
            search.TextChanged += (s, e) => { searchTimer.Stop(); searchTimer.Start(); };
            searchTimer.Tick += (s, e) => { searchTimer.Stop(); Refilter(); };
            bar.Children.Add(search);
            var seg = new Border { Style = Sty("SegmentHost"), Margin = new Thickness(12, 0, 0, 0) };
            var segRow = new StackPanel { Orientation = Orientation.Horizontal };
            void Seg(RadioButton rb, string text, string tip) { rb.Style = Sty("Segment"); rb.Content = text; rb.GroupName = "loglevel-" + GetHashCode(); rb.ToolTip = tip; rb.Checked += (s, e) => Refilter(); segRow.Children.Add(rb); }
            Seg(lvAll, "All", "Every line");
            Seg(lvWarn, "Warnings", "Warnings and errors");
            Seg(lvErr, "Errors only", "Only errors, with their stack");
            lvAll.IsChecked = true;
            seg.Child = segRow;
            bar.Children.Add(seg);
            var followRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            follow.Style = Sty("Switch"); follow.IsChecked = true; follow.ToolTip = "Scroll to new lines as they are written";
            AutomationName(follow, "Follow new lines");
            followRow.Children.Add(follow);
            followRow.Children.Add(new TextBlock { Text = "Follow new lines", Style = Sty("Text.Secondary"), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            follow.Checked += (s, e) => ScrollToEnd();
            bar.Children.Add(followRow);
            hiddenChip.Style = (Style)Application.Current.Resources["Chip"]; hiddenChip.Background = Res("Brush.Warn.Tint"); hiddenChip.VerticalAlignment = VerticalAlignment.Center;
            hiddenText.Style = Sty("Text.Caption"); hiddenText.Foreground = Res("Brush.Warn"); hiddenText.VerticalAlignment = VerticalAlignment.Center;
            hiddenChip.Child = hiddenText; hiddenChip.Visibility = Visibility.Collapsed;
            DockPanel.SetDock(hiddenChip, Dock.Right);
            bar.Children.Insert(0, hiddenChip);
            Grid.SetRow(bar, 1);
            grid.Children.Add(bar);

            // the code panel: Surface1, radius 12, monospace rows (virtualised)
            var panel = new Border { Background = Res("Brush.Surface1"), CornerRadius = new CornerRadius(12), Padding = new Thickness(4, 8, 4, 8), ClipToBounds = true };
            var host = new Grid();
            list.ItemsSource = shown;
            list.Background = Brushes.Transparent; list.BorderThickness = new Thickness(0);
            list.SelectionMode = SelectionMode.Extended;
            list.FocusVisualStyle = null;
            list.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Auto);
            ScrollViewer.SetCanContentScroll(list, true);
            VirtualizingPanel.SetIsVirtualizing(list, true);
            VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
            list.ItemContainerStyle = RowStyle();
            list.ItemTemplate = new DataTemplate { VisualTree = new FrameworkElementFactory(typeof(LogRowView)) };
            AutomationName(list, "Log lines");
            list.KeyDown += (s, e) => { if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { CopyLines(); e.Handled = true; } };
            host.Children.Add(list);
            empty.Style = Sty("Text.Secondary"); empty.HorizontalAlignment = HorizontalAlignment.Center; empty.VerticalAlignment = VerticalAlignment.Center; empty.TextAlignment = TextAlignment.Center; empty.TextWrapping = TextWrapping.Wrap; empty.MaxWidth = 420;
            host.Children.Add(empty);
            panel.Child = host;
            Grid.SetRow(panel, 2);
            grid.Children.Add(panel);

            // footer: count, Copy, Open folder, Close
            var foot = new DockPanel { Margin = new Thickness(0, 16, 0, 0), LastChildFill = false };
            Button Btn(string text, string style) => new Button { Content = text, Style = Sty(style), Margin = new Thickness(0, 0, 8, 0) };
            copyButton = Btn("Copy", "Button.Primary.M"); copyButton.MinWidth = 96;
            copyButton.ToolTip = "Copies the selected lines, or every line shown. Personal details stay hidden.";
            copyButton.Click += (s, e) => CopyLines();
            var open = Btn("Open folder", "Button.M");
            open.ToolTip = "Opens the Outpost's data folder, where hub.log is";
            open.Click += (s, e) => OpenFolder();
            var closeBtn = new Button { Content = "Close", Style = Sty("GhostButton"), Height = 32, Padding = new Thickness(12, 0, 12, 0), FontSize = 13, IsCancel = true };
            closeBtn.Click += (s, e) => Close();
            DockPanel.SetDock(closeBtn, Dock.Right);
            count.Style = Sty("Text.Caption"); count.VerticalAlignment = VerticalAlignment.Center; count.Margin = new Thickness(8, 0, 16, 0);
            DockPanel.SetDock(count, Dock.Right);
            foot.Children.Add(closeBtn);
            foot.Children.Add(count);
            foot.Children.Add(copyButton);
            foot.Children.Add(open);
            Grid.SetRow(foot, 3);
            grid.Children.Add(foot);

            copiedTimer.Tick += (s, e) => { copiedTimer.Stop(); copyButton.Content = "Copy"; };
            tail.Tick += (s, e) => Tail();
            MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is FrameworkElement fe && (fe is Border || fe is Grid || fe is DockPanel) && !IsInside(fe, list)) try { DragMove(); } catch { } };
            KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) { Close(); e.Handled = true; }
                else if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { search.Focus(); search.SelectAll(); e.Handled = true; }
            };
            Closed += (s, e) => { tail.Stop(); copiedTimer.Stop(); searchTimer.Stop(); };

            Reload();
            tail.Start();
        }

        static void AutomationName(DependencyObject d, string name) => System.Windows.Automation.AutomationProperties.SetName(d, name);
        static bool IsInside(DependencyObject d, DependencyObject parent) { for (; d != null; d = VisualTreeHelper.GetParent(d)) if (d == parent) return true; return false; }

        // rows: transparent, a hover veil, the selection in the accent's tint, no focus rectangle (DESIGN: selection is the accent's job)
        static Style RowStyle()
        {
            const string xaml = @"<Style TargetType='ListBoxItem' xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>
  <Setter Property='FocusVisualStyle' Value='{x:Null}' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' />
  <Setter Property='Padding' Value='0' />
  <Setter Property='Template'><Setter.Value>
    <ControlTemplate TargetType='ListBoxItem'>
      <Border x:Name='bd' Background='Transparent' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'><ContentPresenter /></Border>
      <ControlTemplate.Triggers>
        <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='bd' Property='Background' Value='{DynamicResource Brush.Overlay.Hover}' /></Trigger>
        <Trigger Property='IsSelected' Value='True'><Setter TargetName='bd' Property='Background' Value='{DynamicResource Brush.Accent.Tint}' /></Trigger>
      </ControlTemplate.Triggers>
    </ControlTemplate>
  </Setter.Value></Setter>
</Style>";
            return (Style)XamlReader.Parse(xaml);
        }

        // ---------------------------------------------------------------- reading

        public void Reload()
        {
            all.Clear(); parser.Reset(); offset = 0; pending = "";
            if (!File.Exists(path)) { Refilter(); return; }
            Append(LogParser.ReadShared(path, 0, out offset) ?? "", false);
            Refilter();
            ScrollToEnd();
        }

        // the poll: the file grew -> read the new part; it got shorter (rewritten past 512 KB, or deleted) -> read it again
        public void Tail()
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) { if (all.Count > 0 || offset > 0) Reload(); return; }
                if (fi.Length < offset) { Reload(); return; }
                if (fi.Length == offset) return;
                var text = LogParser.ReadShared(path, offset, out var end);
                if (text == null) return;
                offset = end;
                Append(text, true);
            }
            catch { }
        }

        void Append(string text, bool live)
        {
            text = pending + text;
            int cut = text.LastIndexOf('\n');
            if (cut < 0) { pending = text; return; }
            pending = text.Substring(cut + 1);
            var fresh = parser.Feed(text.Substring(0, cut).Split('\n'));
            all.AddRange(fresh);
            if (all.Count > MaxLines) all.RemoveRange(0, all.Count - MaxLines);
            if (!live) return;
            bool atEnd = AtEnd();
            // an exception that arrived under an earlier plain line raises that line's level: show the whole entry again
            if (fresh.Count > 0 && MinLevel > LogLevel.Info && fresh.Any(l => l.Continuation && l.Level >= MinLevel)) { Refilter(); }
            else foreach (var l in fresh) if (LogFilter.Keep(l, Query, MinLevel)) shown.Add(new LogRow(l, Query));
            UpdateCounts();
            if (follow.IsChecked == true && atEnd) ScrollToEnd();
        }

        bool AtEnd()
        {
            var sv = FindScroll();
            return sv == null || sv.VerticalOffset >= sv.ScrollableHeight - 1;
        }

        ScrollViewer FindScroll()
        {
            if (VisualTreeHelper.GetChildrenCount(list) == 0) return null;
            var b = VisualTreeHelper.GetChild(list, 0) as Border;
            return b?.Child as ScrollViewer;
        }

        void ScrollToEnd()
        {
            if (shown.Count == 0) return;
            list.ScrollIntoView(shown[shown.Count - 1]);
            FindScroll()?.ScrollToBottom();
        }

        public void Refilter()
        {
            var q = Query; var min = MinLevel;
            shown.Clear();
            foreach (var l in all) if (LogFilter.Keep(l, q, min)) shown.Add(new LogRow(l, q));
            UpdateCounts();
            if (follow.IsChecked == true) ScrollToEnd();
        }

        void UpdateCounts()
        {
            var fi = new FileInfo(path);
            sub.Text = fi.Exists ? $"hub.log · {Size(fi.Length)} · {all.Count:N0} lines · {all.Count(l => l.Level == LogLevel.Error && !l.Continuation):N0} errors, {all.Count(l => l.Level == LogLevel.Warning && !l.Continuation):N0} warnings · new lines appear while this is open"
                                 : "hub.log is not there yet: the Outpost writes it when it has something to say";
            count.Text = shown.Count == all.Count ? $"{all.Count:N0} lines" : $"Showing {shown.Count:N0} of {all.Count:N0} lines";
            var hidden = all.SelectMany(l => l.Redactions).ToList();
            hiddenChip.Visibility = hidden.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            hiddenText.Text = $"{hidden.Count:N0} personal detail{(hidden.Count == 1 ? "" : "s")} hidden";
            hiddenChip.ToolTip = "Hidden in this view and in Copy: " + string.Join(", ", hidden.Select(r => r.Why).Distinct()) + ".\nThe file itself is unchanged.";
            empty.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            empty.Text = !fi.Exists ? "Nothing logged yet." : all.Count == 0 ? "The log is empty." : "No line matches. Clear the search or pick All.";
        }

        static string Size(long b) => b >= 1024 * 1024 ? (b / 1048576.0).ToString("0.0") + " MB" : b >= 1024 ? (b / 1024) + " KB" : b + " bytes";

        // ---------------------------------------------------------------- actions

        public string CopyText()
        {
            var sel = list.SelectedItems.Cast<LogRow>().ToList();
            var rows = sel.Count > 0 ? shown.Where(r => sel.Contains(r)) : shown;
            return string.Join("\r\n", rows.Select(r => r.Line.Plain));
        }

        public void CopyLines()
        {
            var text = CopyText();
            LastCopied = text;
            if (text.Length == 0) return;
            try { Clipboard.SetText(text); copyButton.Content = "Copied"; }
            catch { try { System.Threading.Thread.Sleep(80); Clipboard.SetText(text); copyButton.Content = "Copied"; } catch { copyButton.Content = "Clipboard busy"; } }
            copiedTimer.Stop(); copiedTimer.Start();
        }

        public void SetSearchForTest(string q) { search.Text = q; searchTimer.Stop(); Refilter(); }
        public void SetLevelForTest(LogLevel min) { (min == LogLevel.Error ? lvErr : min == LogLevel.Warning ? lvWarn : lvAll).IsChecked = true; }
        public bool FollowForTest { get => follow.IsChecked == true; set => follow.IsChecked = value; }
        public bool AtEndForTest => AtEnd();

        void OpenFolder()
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + Path.GetDirectoryName(path) + "\"") { UseShellExecute = true }); } catch (Exception e) { Util.Log("open folder: " + e.Message); }
        }

        static LogViewerWindow open;
        public static LogViewerWindow ShowFor(Window owner, string path = null)
        {
            if (open != null && open.IsLoaded) { open.Activate(); return open; }
            var d = new LogViewerWindow(path);
            if (owner != null && owner.IsVisible) d.Owner = owner;
            d.Closed += (s, e) => { if (open == d) open = null; };
            open = d;
            d.Show();
            return d;
        }
    }

    // one shown line with the search it was shown for (the hits are marked)
    public class LogRow
    {
        public readonly LogLine Line;
        public readonly string Query;
        public LogRow(LogLine line, string query) { Line = line; Query = query; }
        public override string ToString() => Line.Plain;
    }

    // a log line: a 2 px bar in the level's colour, the time (Tertiary), then the coloured spans (LogParser.Spans) in the mono font;
    // a stack line is indented under the message; search hits get the accent's tint, hidden details the warn tint
    public class LogRowView : Border
    {
        readonly TextBlock text = new TextBlock();
        public TextBlock TextForTest => text;

        public LogRowView()
        {
            BorderThickness = new Thickness(2, 0, 0, 0);
            Padding = new Thickness(8, 0, 8, 0);
            text.FontFamily = (FontFamily)Application.Current.Resources["Font.Mono"];
            text.FontSize = 12;
            text.LineHeight = 18; text.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            text.TextWrapping = TextWrapping.NoWrap;
            Child = text;
            DataContextChanged += (s, e) => Build();
        }

        static Brush B(string key) => (Brush)Application.Current.Resources[key];

        public static Brush SpanBrush(LogSpan k, LogLevel level)
        {
            switch (k)
            {
                case LogSpan.Time: return B("Brush.Text.Tertiary");
                case LogSpan.Category: return B("Brush.Info");
                case LogSpan.Error: case LogSpan.Exception: return B("Brush.Danger");
                case LogSpan.Warning: case LogSpan.Redacted: return B("Brush.Warn");
                case LogSpan.StackAt: return B("Brush.Text.Tertiary");
                case LogSpan.StackMethod: return B("Brush.Text.Secondary");
                case LogSpan.StackFile: return B("Brush.Info");
                case LogSpan.Number: return B("Brush.Text.Primary");
                default: return level == LogLevel.Info ? B("Brush.Text.Secondary") : B("Brush.Text.Primary");
            }
        }

        public static Brush BarBrush(LogLevel level) => level == LogLevel.Error ? B("Brush.Danger") : level == LogLevel.Warning ? B("Brush.Warn") : Brushes.Transparent;

        void Build()
        {
            text.Inlines.Clear();
            if (!(DataContext is LogRow row)) return;
            var l = row.Line;
            BorderBrush = BarBrush(l.Level);
            // the time column: 19 characters + 2; a continuation line is indented by the same (and 4 more for a stack frame)
            text.Inlines.Add(new Run(l.Time != null ? l.Time + "  " : new string(' ', 21)) { Foreground = B("Brush.Text.Tertiary") });
            var words = (row.Query ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var sp in LogParser.Spans(l))
            {
                var fg = SpanBrush(sp.Key, l.Level);
                bool strong = sp.Key == LogSpan.Error || sp.Key == LogSpan.Exception;
                foreach (var (part, hit) in Hits(sp.Value, words))
                {
                    var r = new Run(part) { Foreground = fg };
                    if (strong) r.FontWeight = FontWeights.SemiBold;
                    if (sp.Key == LogSpan.Redacted) r.Background = B("Brush.Warn.Tint");
                    if (hit) { r.Background = B("Brush.Accent.Tint"); r.Foreground = B("Brush.Text.Primary"); }
                    text.Inlines.Add(r);
                }
            }
            var why = l.Redactions.Select(r => r.Why).Distinct().ToList();
            ToolTip = why.Count > 0 ? "Hidden here: " + string.Join(", ", why) : null;
        }

        // a span split at the search words (any case); true = a hit
        public static List<(string, bool)> Hits(string s, string[] words)
        {
            var o = new List<(string, bool)>();
            if (words == null || words.Length == 0 || string.IsNullOrEmpty(s)) { o.Add((s ?? "", false)); return o; }
            var mark = new bool[s.Length];
            foreach (var w in words)
                for (int i = s.IndexOf(w, StringComparison.OrdinalIgnoreCase); i >= 0; i = s.IndexOf(w, i + Math.Max(1, w.Length), StringComparison.OrdinalIgnoreCase))
                    for (int k = i; k < i + w.Length && k < s.Length; k++) mark[k] = true;
            int start = 0;
            for (int i = 1; i <= s.Length; i++) if (i == s.Length || mark[i] != mark[start]) { o.Add((s.Substring(start, i - start), mark[start])); start = i; }
            return o;
        }
    }
}
