using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // Settings > About > Blizzard news (3.10): what Blizzard's staff posted about WoW Forever on the forums (BlizzardNews), newest first. Before the
    // one-time "Load Blizzard's news" click the tab only explains; after it, the posts as cards (date, author, topic, a "Server" / "Hotfixes" pill,
    // the excerpt, "Read on the forum"), the EU / US forum switch, Refresh, and the switch for the new-post note in the window's corner.
    // Opening the tab marks every post as seen (Settings.BlizzardNewsSeen), which clears the dot on the tab and the corner note.
    public partial class SettingsPage
    {
        const int BlizzardFirst = 12;
        bool blizzardHooked, blizzardAll;

        public event Action BlizzardNewsSeenChanged;      // everything listed was seen (the tab was open)

        string BlizzardRegion => settings?.BlizzardNewsRegion == "us" ? "us" : "eu";

        public void SetBlizzardDot(bool on) { if (AboutBlizzardDot != null) AboutBlizzardDot.Visibility = on ? Visibility.Visible : Visibility.Collapsed; }

        void FillBlizzard()
        {
            if (BlizzardBody == null) return;
            if (!blizzardHooked)
            {
                blizzardHooked = true;
                BlizzardNews.Changed += () => Dispatcher.BeginInvoke(new Action(() => { if (AboutPaneBlizzard.IsVisible) FillBlizzard(); }));
            }
            BlizzardBody.Children.Clear();
            TextBlock Line(string text, string style = "Text.Secondary") => new TextBlock { Style = (Style)FindResource(style), TextWrapping = TextWrapping.Wrap, MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left, Text = text };
            var buttons = new WrapPanel { Margin = new Thickness(0, 0, 0, 16) };
            Button Btn(string text, string style, string tag, Action click)
            {
                var b = new Button { Style = (Style)FindResource(style), Content = text, Margin = new Thickness(0, 0, 8, 0), Tag = tag };
                b.Click += (s, a) => click();
                buttons.Children.Add(b);
                return b;
            }

            if (settings == null || !settings.BlizzardNewsLoad)
            {
                var intro = new ContentControl { Style = (Style)FindResource("Card.Group"), Tag = "intro" };
                var sp = new StackPanel { Margin = new Thickness(16, 12, 16, 12) };
                sp.Children.Add(Line("Blizzard's staff post hotfixes, refunds and news about WoW Forever on the forums. The Outpost can list them here and tell you when there is a new one: it then asks Blizzard's forum every half hour, without your account or anything about you, and keeps only the text."));
                sp.Children.Add(Line("Notices about server trouble that Blizzard only posts on X don't reach the forums and won't show here.", "Text.Caption"));
                buttons.Margin = new Thickness(0, 12, 0, 0);
                Btn("Load Blizzard's news", "Button.Primary.S", "load", () => { if (settings != null) { settings.BlizzardNewsLoad = true; SettingsStore.Save(settings); } LoadBlizzard(false); });
                Btn("Open the forum", "GhostButton", "forum", () => OpenWeb(BlizzardNews.ForumPage(BlizzardRegion)));
                sp.Children.Add(buttons);
                intro.Content = sp;
                BlizzardBody.Children.Add(intro);
                return;
            }

            var r = BlizzardNews.Saved();
            if (BlizzardNews.Stale(r, BlizzardRegion) && !BlizzardNews.Busy && (r.Problem == null || r.Region != BlizzardRegion)) LoadBlizzard(false);

            // the bar: EU | US, Refresh, Open the forum
            var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 16), LastChildFill = false };
            var seg = new Border { Style = (Style)FindResource("SegmentHost") };
            var segs = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var reg in BlizzardNews.Regions)
            {
                var rb = new RadioButton { Style = (Style)FindResource("Segment"), Content = reg == "eu" ? "EU forum" : "US forum", GroupName = "blizzregion", Tag = "region:" + reg, IsChecked = reg == BlizzardRegion };
                // each forum numbers its own posts: "seen" starts over on the other one
                rb.Checked += (o, e) => { if (settings != null && BlizzardRegion != reg) { settings.BlizzardNewsRegion = reg; settings.BlizzardNewsSeen = 0; settings.BlizzardNewsNoted = 0; SettingsStore.Save(settings); LoadBlizzard(true); } };
                segs.Children.Add(rb);
            }
            seg.Child = segs;
            DockPanel.SetDock(seg, Dock.Left);
            bar.Children.Add(seg);
            buttons.Margin = new Thickness(0);
            buttons.VerticalAlignment = VerticalAlignment.Center;
            var refresh = Btn(BlizzardNews.Busy ? "Loading…" : "Refresh", "GhostButton", "refresh", () => LoadBlizzard(true));
            refresh.IsEnabled = !BlizzardNews.Busy;
            Btn("Open the forum", "GhostButton", "forum", () => OpenWeb(BlizzardNews.ForumPage(BlizzardRegion)));
            DockPanel.SetDock(buttons, Dock.Right);
            bar.Children.Add(buttons);
            BlizzardBody.Children.Add(bar);

            if (r.Problem != null && r.Region == BlizzardRegion)
                BlizzardBody.Children.Add(new Border { Background = (Brush)FindResource("Brush.Warn.Tint"), CornerRadius = (CornerRadius)FindResource("Radius.Card"), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 12), Tag = "problem", Child = Line(r.Problem, "Text.Body") });

            var posts = r.Region == BlizzardRegion ? r.Posts : new List<BlizzardNews.Post>();
            if (posts.Count == 0)
                BlizzardBody.Children.Add(Line(BlizzardNews.Busy || r.Fetched == DateTime.MinValue || r.Region != BlizzardRegion ? "Loading Blizzard's news…" : "Blizzard's staff have posted nothing about WoW Forever lately."));
            else
            {
                long seen = settings.BlizzardNewsSeen;
                var shown = blizzardAll ? posts : posts.Take(BlizzardFirst).ToList();
                foreach (var p in shown) BlizzardBody.Children.Add(BlizzardCard(p, seen > 0 && p.Id > seen));
                if (shown.Count < posts.Count)
                {
                    var more = new Button { Style = (Style)FindResource("GhostButton"), Content = "Show " + (posts.Count - shown.Count).ToString(CultureInfo.InvariantCulture) + " older", HorizontalAlignment = HorizontalAlignment.Left, Tag = "more" };
                    more.Click += (s, a) => { blizzardAll = true; FillBlizzard(); };
                    BlizzardBody.Children.Add(more);
                }
                MarkBlizzardSeen(r);
            }
            if (r.Fetched != DateTime.MinValue && r.Region == BlizzardRegion)
                BlizzardBody.Children.Add(new TextBlock { Text = "From Blizzard's " + BlizzardRegion.ToUpperInvariant() + " forum, " + r.Fetched.ToLocalTime().ToString("d MMMM HH:mm", CultureInfo.CurrentCulture), Style = (Style)FindResource("Text.Caption"), Margin = new Thickness(0, 12, 0, 0), Tag = "fetched" });

            const string alertText = "Tell me when Blizzard posts something new (a note in the window's corner)";
            var alerts = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = !settings.BlizzardNewsAlertsOff, VerticalAlignment = VerticalAlignment.Center, Tag = "alerts" };
            System.Windows.Automation.AutomationProperties.SetName(alerts, alertText);
            alerts.Click += (s, a) => { settings.BlizzardNewsAlertsOff = alerts.IsChecked != true; SettingsStore.Save(settings); };
            var alertRow = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
            DockPanel.SetDock(alerts, Dock.Left);
            alertRow.Children.Add(alerts);
            alertRow.Children.Add(new TextBlock { Text = alertText, Style = (Style)FindResource("Text.Body"), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 0, 0, 0) });
            BlizzardBody.Children.Add(alertRow);
        }

        FrameworkElement BlizzardCard(BlizzardNews.Post p, bool isNew)
        {
            var card = new ContentControl { Style = (Style)FindResource("Card.Group"), Margin = new Thickness(0, 0, 0, 12), Tag = "post:" + p.Id.ToString(CultureInfo.InvariantCulture) };
            var body = new StackPanel { Margin = new Thickness(16, 12, 16, 12) };
            var head = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
            head.Children.Add(new TextBlock { Text = p.Posted.ToLocalTime().ToString("d MMMM HH:mm", CultureInfo.CurrentCulture), Style = (Style)FindResource("Text.Body"), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
            head.Children.Add(new TextBlock { Text = p.Author + (string.IsNullOrEmpty(p.Title) ? "" : ", " + p.Title), Style = (Style)FindResource("Text.Caption"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
            if (p.Kind == BlizzardNews.Kind.Server) head.Children.Add(Pill("Servers", "Warn"));
            else if (p.Kind == BlizzardNews.Kind.Hotfix) head.Children.Add(Pill("Hotfixes", "Info"));
            if (isNew) head.Children.Add(Pill("New", "Accent"));
            body.Children.Add(head);
            body.Children.Add(new TextBlock { Text = p.TopicTitle, Style = (Style)FindResource("Text.Body"), FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("Brush.Text.Primary"), TextWrapping = TextWrapping.Wrap, MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 4) });
            var text = new TextBlock { Style = (Style)FindResource("Text.Secondary"), TextWrapping = TextWrapping.Wrap, MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };
            if (p.Excerpt.Length > 0) text.Inlines.Add(new Run(p.Excerpt + " "));
            var read = new Hyperlink(new Run("Read on the forum")) { Tag = p.Url };
            read.Click += (s, a) => OpenWeb(p.Url);
            text.Inlines.Add(read);
            body.Children.Add(text);
            card.Content = body;
            System.Windows.Automation.AutomationProperties.SetName(card, p.TopicTitle + ", " + p.Author + ", " + p.Posted.ToLocalTime().ToString("d MMMM", CultureInfo.CurrentCulture) + (isNew ? ", new" : ""));
            return card;
        }

        // the tab is open: everything listed counts as seen
        void MarkBlizzardSeen(BlizzardNews.Result r)
        {
            var newest = BlizzardNews.Newest(r);
            if (settings == null || newest <= settings.BlizzardNewsSeen) { SetBlizzardDot(false); return; }
            settings.BlizzardNewsSeen = newest;
            SetBlizzardDot(false);
            if (newest > settings.BlizzardNewsNoted) settings.BlizzardNewsNoted = newest;
            SettingsStore.Save(settings);
            CloseBlizzardNote();
            try { BlizzardNewsSeenChanged?.Invoke(); } catch { }
        }

        void LoadBlizzard(bool force)
        {
            BlizzardNews.Refresh(BlizzardRegion, force);
            Dispatcher.BeginInvoke(new Action(() => { if (AboutPaneBlizzard.IsVisible) FillBlizzard(); }), System.Windows.Threading.DispatcherPriority.Background);
        }

        // ---------------------------------------------------------------- new posts: the dot on the tab and the note in the window's corner

        public static TimeSpan BlizzardPoll = TimeSpan.FromMinutes(30), BlizzardFirstPoll = TimeSpan.FromSeconds(20);
        System.Windows.Threading.DispatcherTimer blizzardTimer;
        CornerNote blizzardNote;
        public CornerNote BlizzardNoteForTest => blizzardNote;

        // after the window shows: ask the forum 20 s later and then every half hour, only once the reader turned the news on (not in the self-test)
        void InitBlizzardNews()
        {
            BlizzardNews.Changed += () => Dispatcher.BeginInvoke(new Action(CheckBlizzardUnseen));
            if (host == null || !NoteAutoShow) return;
            bool started = false;
            host.ContentRendered += (s, e) =>
            {
                if (started) return;
                started = true;
                blizzardTimer = new System.Windows.Threading.DispatcherTimer { Interval = BlizzardFirstPoll };
                blizzardTimer.Tick += (o, a) =>
                {
                    blizzardTimer.Interval = BlizzardPoll;
                    if (settings != null && settings.BlizzardNewsLoad) BlizzardNews.Refresh(BlizzardRegion);
                    else CheckBlizzardUnseen();
                };
                blizzardTimer.Start();
                CheckBlizzardUnseen();   // the saved news from the last run
            };
        }

        // newer posts than the reader has seen: a dot on the tab, and once per post a note in the corner (unless turned off). The first load
        // ever only remembers the newest post - nothing is "new" before the reader has seen the list once.
        public void CheckBlizzardUnseen()
        {
            if (settings == null || !settings.BlizzardNewsLoad) { SetBlizzardDot(false); return; }
            var r = BlizzardNews.Saved();
            if (r.Region != BlizzardRegion) return;
            var newest = BlizzardNews.Newest(r);
            if (newest <= 0) return;
            if (settings.BlizzardNewsSeen <= 0) { settings.BlizzardNewsSeen = newest; settings.BlizzardNewsNoted = newest; SettingsStore.Save(settings); return; }
            if (AboutPaneBlizzard.IsVisible) return;   // the open tab marks them seen itself (FillBlizzard)
            var unseen = BlizzardNews.Unseen(r, settings.BlizzardNewsSeen);
            SetBlizzardDot(unseen.Count > 0);
            if (unseen.Count == 0 || settings.BlizzardNewsAlertsOff || newest <= settings.BlizzardNewsNoted || note != null || blizzardNote != null) return;
            ShowBlizzardNote(unseen);
        }

        public bool ShowBlizzardNote(List<BlizzardNews.Post> unseen)
        {
            var target = host?.Content as UIElement;
            var layer = target != null ? System.Windows.Documents.AdornerLayer.GetAdornerLayer(target) : null;
            if (layer == null || unseen.Count == 0) return false;
            settings.BlizzardNewsNoted = unseen.Max(p => p.Id); SettingsStore.Save(settings);   // once per post, also after a restart
            bool server = unseen.Any(p => p.Kind == BlizzardNews.Kind.Server);
            string Cut(string t, int n) => t.Length > n ? t.Substring(0, n - 1).TrimEnd() + "…" : t;
            var title = unseen.Count == 1 ? unseen[0].TopicTitle : unseen.Count.ToString(CultureInfo.InvariantCulture) + " new posts from Blizzard";
            var lines = unseen.Count == 1 ? (unseen[0].Excerpt.Length > 0 ? new[] { Cut(unseen[0].Excerpt, 140) } : new string[0])
                                          : unseen.Take(3).Select(p => Cut(p.TopicTitle, 90)).ToArray();
            CloseBlizzardNote();
            blizzardNote = new CornerNote(target, "New from Blizzard: " + title, server ? "Blizzard posted about the servers" : "New from Blizzard", title, lines,
                unseen.Count > 3 ? "and " + (unseen.Count - 3).ToString(CultureInfo.InvariantCulture) + " more" : null, "Read it", server);
            blizzardNote.Open += () => { CloseBlizzardNote(); host.ShowTab("settings"); Show("about"); ShowAboutTab("blizzard"); };
            blizzardNote.Dismiss += CloseBlizzardNote;
            layer.Add(blizzardNote);
            return true;
        }

        public void CloseBlizzardNote()
        {
            if (blizzardNote == null) return;
            var n = blizzardNote; blizzardNote = null;
            System.Windows.Documents.AdornerLayer.GetAdornerLayer(n.AdornedElement)?.Remove(n);
        }

        // ---------------------------------------------------------------- test hooks
        public Panel BlizzardBodyForTest => BlizzardBody;
        public bool BlizzardDotForTest => AboutBlizzardDot.Visibility == Visibility.Visible;
    }
}
