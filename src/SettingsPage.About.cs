using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // --selftest "about": Settings > About. Two tabs (EntityTab): About (the campfire scene centred, View log / What's new / GitHub / welcome
    // guide, credits, the trademark line on one line when it fits) and What's new (the version history: the Outpost's CHANGELOG.md inside the
    // exe, the Elan's Hub companion's CHANGELOG.txt, the other addons' notes from the update manifest; this version / installed / newer marked).
    // After an update a one-time "What's new in x.y.z" note shows in the window's corner (WhatsNewNote) with a link to the tab.
    public partial class SettingsPage
    {
        const int NewsFirst = 8, NewsStep = 12;
        string newsKey = "outpost";
        int newsShown = NewsFirst;
        bool newsUnseen;   // the note showed this session and the tab was not opened yet: a dot on the tab

        // ---------------------------------------------------------------- tabs

        void AboutTab_Checked(object sender, RoutedEventArgs e) { if (AboutTabNews != null && AboutPaneNews != null) ShowAboutTab(AboutTabNews.IsChecked == true ? "news" : "about"); }   // not while the XAML is still being read

        public void ShowAboutTab(string tab)
        {
            if (AboutPaneAbout == null || AboutPaneNews == null) return;
            bool news = tab == "news";
            if (news && AboutTabNews.IsChecked != true) { AboutTabNews.IsChecked = true; return; }
            if (!news && AboutTabAbout.IsChecked != true) { AboutTabAbout.IsChecked = true; return; }
            var was = AboutPaneNews.Visibility == Visibility.Visible;
            AboutPaneAbout.Visibility = news ? Visibility.Collapsed : Visibility.Visible;
            AboutPaneNews.Visibility = news ? Visibility.Visible : Visibility.Collapsed;
            if (news) { newsUnseen = false; AboutNewsDot.Visibility = Visibility.Collapsed; FillNews(); }
            if (IsLoaded && was != news) Motion.PageIn(news ? AboutPaneNews : (FrameworkElement)AboutPaneAbout);
            if (aboutTimer != null) AboutRun();
        }

        void WhatsNew_Click(object sender, RoutedEventArgs e) => ShowAboutTab("news");

        void ViewLog_Click(object sender, RoutedEventArgs e) => LogViewerWindow.ShowFor(Window.GetWindow(this));

        void Releases_Click(object sender, RoutedEventArgs e)
        {
            const string url = "https://github.com/Elanoran/ElanAddonHub/releases";
            if (OpenWebLink != null) OpenWebLink(url);
            else { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { } }
        }

        void NewsMore_Click(object sender, RoutedEventArgs e) { newsShown += NewsStep; FillNews(); }

        // ---------------------------------------------------------------- What's new

        public class NewsSource
        {
            public string Key, Label, Name;
            public List<ChangelogRelease> Releases;
            public string Current;   // the Outpost's version / the installed addon version (null = not installed)
            public string Latest;    // what the update manifest offers (null = not checked yet)
        }

        static readonly Dictionary<string, string> ShortNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ElansHunterHelper"] = "Hunter Helper", ["ElansHub"] = "Elan's Hub", ["ElansPaladinHelper"] = "Paladin Helper", ["ElansPriestHelper"] = "Priest Helper", ["ElansBags"] = "Bags", ["ElansToolkit"] = "Toolkit",
        };

        public List<NewsSource> NewsSources()
        {
            var man = host?.ManifestForAbout;
            var cards = host?.CardsForAbout?.ToList() ?? new List<AddonCard>();
            string Installed(string id) => cards.FirstOrDefault(c => string.Equals(c.Info?.Id, id, StringComparison.OrdinalIgnoreCase))?.Installed;
            var o = new List<NewsSource>
            {
                new NewsSource { Key = "outpost", Label = "Outpost", Name = Brand.Name, Releases = Changelog.Outpost, Current = App.Version, Latest = man?.Hub?.Version },
            };
            bool hub = false;
            foreach (var a in man?.Addons ?? new List<AddonInfo>())
            {
                if (a == null || string.IsNullOrEmpty(a.Id)) continue;
                var rel = Changelog.FromEntries(a.Changelog);
                if (string.Equals(a.Id, "ElansHub", StringComparison.OrdinalIgnoreCase)) { hub = true; if (rel.Count == 0) rel = Changelog.Companion; }
                if (rel.Count == 0) continue;
                o.Add(new NewsSource { Key = a.Id, Label = ShortNames.TryGetValue(a.Id, out var sn) ? sn : a.Name ?? a.Id, Name = a.Name ?? a.Id, Releases = rel, Current = Installed(a.Id), Latest = a.Version });
            }
            if (!hub && Changelog.Companion.Count > 0)
                o.Add(new NewsSource { Key = "ElansHub", Label = "Elan's Hub", Name = "Elan's Hub", Releases = Changelog.Companion, Current = Installed("ElansHub") });
            return o;
        }

        void FillNews()
        {
            if (NewsList == null) return;
            var sources = NewsSources();
            if (!sources.Any(s => s.Key == newsKey)) newsKey = "outpost";
            var src = sources.First(s => s.Key == newsKey);

            // the picker (only when there is more than the Outpost)
            NewsPick.Children.Clear();
            NewsPickHost.Visibility = sources.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var s in sources)
            {
                var rb = new RadioButton { Style = (Style)FindResource("Segment"), Content = s.Label, GroupName = "newspick", Tag = s.Key, IsChecked = s.Key == newsKey, ToolTip = s.Name };
                rb.Checked += (o, e) => { if (rb.Tag is string k && k != newsKey) { newsKey = k; newsShown = NewsFirst; FillNews(); } };
                NewsPick.Children.Add(rb);
            }

            // newer than what is here
            string banner = null;
            if (src.Latest != null && src.Current != null && Util.CompareVersions(src.Latest, src.Current) > 0)
                banner = src.Key == "outpost"
                    ? $"{Brand.Name} {src.Latest} is ready to install (you have {src.Current}). Its notes show here after the update; they are on GitHub until then."
                    : $"{src.Name} {src.Latest} is available (you have {src.Current}). Update it on the Addons page.";
            else if (src.Key != "outpost" && src.Current == null && src.Latest != null)
                banner = $"{src.Name} is not installed. Its latest version is {src.Latest}.";
            NewsBanner.Visibility = banner != null ? Visibility.Visible : Visibility.Collapsed;
            NewsBannerText.Text = banner ?? "";

            NewsList.Children.Clear();
            var list = src.Releases ?? new List<ChangelogRelease>();
            foreach (var r in list.Take(newsShown)) NewsList.Children.Add(ReleaseCard(r, src));
            int rest = list.Count - Math.Min(newsShown, list.Count);
            NewsMore.Visibility = rest > 0 ? Visibility.Visible : Visibility.Collapsed;
            NewsMore.Content = $"Show {Math.Min(NewsStep, rest)} older versions ({rest} more)";
            NewsEmpty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            NewsEmpty.Text = "No version history is available for this one.";
        }

        public const string ThisVersion = "This version", Installed = "Installed", Latest = "Latest", NotYet = "Not released yet";

        FrameworkElement ReleaseCard(ChangelogRelease r, NewsSource src)
        {
            var card = new ContentControl { Style = (Style)FindResource("Card.Group"), Margin = new Thickness(0, 0, 0, 16), Tag = r.Version };
            var body = new StackPanel { Margin = new Thickness(16, 12, 16, 12) };
            var head = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            var title = new TextBlock { Text = r.Version, Style = (Style)FindResource("Text.Section"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            head.Children.Add(title);
            if (r.Date != null) head.Children.Add(new TextBlock { Text = DateText(r.Date), Style = (Style)FindResource("Text.Caption"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
            bool isCurrent = src.Current != null && Util.CompareVersions(r.Version, src.Current) == 0;
            if (isCurrent) head.Children.Add(Pill(src.Key == "outpost" ? ThisVersion : Installed, "Accent"));
            else if (src.Key == "outpost" && Util.CompareVersions(r.Version, src.Current) > 0 && (src.Latest == null || Util.CompareVersions(r.Version, src.Latest) > 0)) head.Children.Add(Pill(NotYet, "Warn"));
            if (src.Key != "outpost" && src.Latest != null && Util.CompareVersions(r.Version, src.Latest) == 0 && !isCurrent) head.Children.Add(Pill(Latest, "Info"));
            body.Children.Add(head);
            foreach (var it in r.Items) body.Children.Add(Bullet(it));
            if (r.Items.Count == 0) body.Children.Add(new TextBlock { Text = "No notes for this version.", Style = (Style)FindResource("Text.Secondary") });
            card.Content = body;
            System.Windows.Automation.AutomationProperties.SetName(card, "Version " + r.Version + (isCurrent ? ", " + (src.Key == "outpost" ? ThisVersion : Installed) : ""));
            return card;
        }

        static string DateText(string iso) =>
            DateTime.TryParseExact(iso, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? d.ToString("d MMMM yyyy") : iso;

        Border Pill(string text, string role)
        {
            var b = new Border { Style = (Style)FindResource("Chip"), Background = (Brush)FindResource("Brush." + role + ".Tint"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), Tag = text };
            b.Child = new TextBlock { Text = text, Style = (Style)FindResource("Text.Caption"), Foreground = (Brush)FindResource("Brush." + role), VerticalAlignment = VerticalAlignment.Center };
            return b;
        }

        FrameworkElement Bullet(ChangelogItem it)
        {
            var g = new Grid { Margin = new Thickness(0, 4, 0, 4), MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.Children.Add(new TextBlock { Text = "•", Style = (Style)FindResource("Text.Body"), Foreground = (Brush)FindResource("Brush.Text.Tertiary") });
            var t = new TextBlock { Style = (Style)FindResource("Text.Body"), TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("Brush.Text.Secondary") };
            if (!string.IsNullOrEmpty(it.Lead)) { foreach (var run in Runs(it.Lead, true)) t.Inlines.Add(run); t.Inlines.Add(new Run(" ")); }
            foreach (var run in Runs(it.Text, false)) t.Inlines.Add(run);
            Grid.SetColumn(t, 1);
            g.Children.Add(t);
            return g;
        }

        IEnumerable<Inline> Runs(string text, bool lead)
        {
            foreach (var sp in Changelog.Spans(text))
            {
                var r = new Run(sp.Value);
                if (lead || sp.Key == Changelog.SpanKind.Bold) { r.FontWeight = FontWeights.SemiBold; r.Foreground = (Brush)FindResource("Brush.Text.Primary"); }
                if (sp.Key == Changelog.SpanKind.Code) { r.FontFamily = (FontFamily)FindResource("Font.Mono"); r.Background = (Brush)FindResource("Brush.Overlay.Chip"); r.Foreground = (Brush)FindResource("Brush.Text.Primary"); }
                yield return r;
            }
        }

        // ---------------------------------------------------------------- the one-time note after an update

        public static bool NoteAutoShow = Array.IndexOf(Environment.GetCommandLineArgs(), "--selftest") < 0;
        WhatsNewNote note;
        public WhatsNewNote NoteForTest => note;

        void InitWhatsNewNote()
        {
            if (host == null) return;
            host.ContentRendered += (s, e) =>
            {
                if (!NoteAutoShow || settings == null) return;
                if (WhatsNew.ShouldShow(settings.WhatsNewSeen, App.Version, SettingsStore.Fresh)) ShowWhatsNewNote();
                else if (settings.WhatsNewSeen != App.Version) { settings.WhatsNewSeen = App.Version; SettingsStore.Save(settings); }   // a first start: nothing to show, remember
            };
        }

        // shows the note for this version (once: it is remembered right away) in the window's bottom right corner
        public bool ShowWhatsNewNote()
        {
            settings.WhatsNewSeen = App.Version; SettingsStore.Save(settings);
            var rel = Changelog.Outpost.FirstOrDefault(r => Util.CompareVersions(r.Version, App.Version) == 0);
            if (rel == null) return false;
            var target = host.Content as UIElement;
            var layer = target != null ? System.Windows.Documents.AdornerLayer.GetAdornerLayer(target) : null;
            if (layer == null) return false;
            CloseWhatsNewNote();
            note = new WhatsNewNote(target, rel, Brand.Name);
            note.Open += () => { CloseWhatsNewNote(); host.ShowTab("settings"); Show("about"); ShowAboutTab("news"); };
            note.Dismiss += CloseWhatsNewNote;
            layer.Add(note);
            newsUnseen = true; AboutNewsDot.Visibility = Visibility.Visible;
            return true;
        }

        public void CloseWhatsNewNote()
        {
            if (note == null) return;
            var n = note; note = null;
            System.Windows.Documents.AdornerLayer.GetAdornerLayer(n.AdornedElement)?.Remove(n);
        }

        // ---------------------------------------------------------------- test hooks
        public const string FairPlaySep = "||";
        public string FairPlayTextForTest => string.Join(FairPlaySep, ((StackPanel)AboutFairPlay.Content).Children.OfType<TextBlock>().Select(b => b.Text));
        public bool FairPlayShownForTest => AboutFairPlay.IsVisible && AboutFairPlay.ActualHeight > 40;
        public bool AboutNewsVisibleForTest => AboutPaneNews.Visibility == Visibility.Visible;
        public StackPanel NewsListForTest => NewsList;
        public string NewsBannerForTest => NewsBanner.Visibility == Visibility.Visible ? NewsBannerText.Text : null;
        public Button NewsMoreForTest => NewsMore;
        public FrameworkElement AboutVideoForTest => AboutVideo;
        public FrameworkElement AboutPaneForTest => AboutPaneAbout;
        public TextBlock AboutDisclaimerForTest => AboutDisclaimer;
        public Panel NewsPickForTest => NewsPick;
        public bool NewsDotForTest => AboutNewsDot.Visibility == Visibility.Visible;
        public void PickNewsForTest(string key) { newsKey = key; newsShown = NewsFirst; FillNews(); }
        public void ViewLogForTest() => ViewLog_Click(this, null);
    }

    // the one-time "What's new in x.y.z" card, over the window's bottom right corner (an adorner: nothing in the window's own layout moves).
    // An overlay (Surface3, hairline, radius 14, elevation 2); the first three lead-ins of the version's notes, "See what's new" and "Dismiss".
    public class WhatsNewNote : System.Windows.Documents.Adorner
    {
        readonly Border card;
        public event Action Open, Dismiss;
        public Border CardForTest => card;
        public void OpenForTest() => Open?.Invoke();

        public WhatsNewNote(UIElement adorned, ChangelogRelease rel, string product) : base(adorned)
        {
            Brush B(string k) => (Brush)Application.Current.Resources[k];
            Style S(string k) => (Style)Application.Current.Resources[k];
            card = new Border
            {
                Width = 360, Background = B("Brush.Surface3"), BorderBrush = B("Brush.Divider"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14),
                Padding = new Thickness(16), Effect = Application.Current.TryFindResource("Elevation.2") as System.Windows.Media.Effects.Effect,
            };
            System.Windows.Automation.AutomationProperties.SetName(card, "What's new in " + rel.Version);
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = "Updated to " + rel.Version, Style = S("Text.Caption"), Foreground = B("Brush.Accent") });
            sp.Children.Add(new TextBlock { Text = "What's new in " + product + " " + rel.Version, Style = S("Text.Section"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 8) });
            foreach (var it in rel.Items.Take(3))
            {
                var line = it.Lead ?? (it.Text.Length > 90 ? it.Text.Substring(0, 90).TrimEnd() + "…" : it.Text);
                var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.Children.Add(new TextBlock { Text = "•", Style = S("Text.Secondary"), Foreground = B("Brush.Text.Tertiary") });
                var t = new TextBlock { Text = string.Concat(Changelog.Spans(line).Select(x => x.Value)), Style = S("Text.Secondary"), TextWrapping = TextWrapping.Wrap };
                Grid.SetColumn(t, 1); g.Children.Add(t);
                sp.Children.Add(g);
            }
            if (rel.Items.Count > 3) sp.Children.Add(new TextBlock { Text = $"and {rel.Items.Count - 3} more", Style = S("Text.Caption"), Margin = new Thickness(16, 2, 0, 0) });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            var open = new Button { Content = "See what's new", Style = S("Button.Primary.S") };
            open.Click += (s, e) => Open?.Invoke();
            var dismiss = new Button { Content = "Dismiss", Style = S("GhostButton"), Margin = new Thickness(8, 0, 0, 0) };
            dismiss.Click += (s, e) => Dismiss?.Invoke();
            buttons.Children.Add(open); buttons.Children.Add(dismiss);
            sp.Children.Add(buttons);
            card.Child = sp;
            AddVisualChild(card);
            AddLogicalChild(card);
            card.KeyDown += (s, e) => { if (e.Key == Key.Escape) { Dismiss?.Invoke(); e.Handled = true; } };
            Loaded += (s, e) => Motion.DialogIn(card);
        }

        protected override int VisualChildrenCount => 1;
        protected override Visual GetVisualChild(int index) => card;

        protected override Size MeasureOverride(Size constraint)
        {
            card.Measure(new Size(360, double.PositiveInfinity));
            return AdornedElement.RenderSize;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var s = AdornedElement.RenderSize;
            var d = card.DesiredSize;
            card.Arrange(new Rect(Math.Max(0, s.Width - d.Width - 24), Math.Max(0, s.Height - d.Height - 24), d.Width, d.Height));
            return s;
        }
    }
}
