using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Db2;

namespace ElansAddonHub
{
    // Settings > About > Game patches (3.3): the game builds this Outpost has seen, newest first (a Segment picker like What's new), and for the
    // picked build Blizzard's notes (PatchNotes) and what the Outpost found changed in the game files compared with the build before (GameBuilds:
    // talents, spells, items, maps, zones, flight points; counts plus examples that open in the Atlas).
    public partial class SettingsPage
    {
        const int PatchBuildsShown = 6, PatchRowsFirst = 8;
        string patchBuild;                                             // the build picked (null = the newest)
        readonly HashSet<string> patchOpen = new HashSet<string>();     // "build|kind" whose whole example list is shown
        bool patchHooked;

        public Action<string> OpenAtlas;                                // set by the window: an example row opens its Atlas page

        // the game build of the WoW folder now (null = no WoW folder / no .build.info)
        public static Func<string> CurrentGameBuild = () =>
        {
            try { var root = ItemData.WowRoot(); return string.IsNullOrEmpty(root) ? null : ItemData.GameBuild(root, ItemData.Flavor() ?? "_classic_beta_"); }
            catch { return null; }
        };

        void FillPatches()
        {
            if (PatchBody == null) return;
            if (!patchHooked)
            {
                patchHooked = true;
                GameBuilds.Changed += () => Dispatcher.BeginInvoke(new Action(() => { if (AboutPanePatches.IsVisible) FillPatches(); }));
            }
            var current = CurrentGameBuild();
            if (current != null) GameBuilds.Seen(current);
            var builds = GameBuilds.All();

            PatchPick.Children.Clear();
            var shown = builds.Take(PatchBuildsShown).ToList();
            var pick = builds.FirstOrDefault(b => b.Build == patchBuild) ?? builds.FirstOrDefault();
            if (pick != null && !shown.Contains(pick)) shown[shown.Count - 1] = pick;
            PatchPickHost.Visibility = builds.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var b in shown)
            {
                var rb = new RadioButton { Style = (Style)FindResource("Segment"), Content = BuildLabel(b, true), GroupName = "patchpick", Tag = b.Build, IsChecked = b == pick, ToolTip = b.Version };
                rb.Checked += (o, e) => { if (rb.Tag is string k && k != patchBuild) { patchBuild = k; FillPatches(); } };
                PatchPick.Children.Add(rb);
            }

            PatchBody.Children.Clear();
            if (pick == null)
            {
                PatchBody.Children.Add(new TextBlock { Style = (Style)FindResource("Note"), Text = "Set your WoW folder (Settings > Addons) and the Outpost lists the game's builds here, with what each patch changed." });
                return;
            }
            PatchBody.Children.Add(BuildHead(pick, current));
            PatchBody.Children.Add(new TextBlock { Text = "Blizzard's notes", Style = (Style)FindResource("Head") });
            PatchBody.Children.Add(NotesCard(pick, builds));
            PatchBody.Children.Add(new TextBlock { Text = "Found in your game files", Style = (Style)FindResource("Head") });
            PatchBody.Children.Add(FoundCard(pick, builds));
        }

        // "Build 70532" (the picker) / "1.60.1 build 70532"
        static string BuildLabel(GameBuilds.Entry e, bool shortForm)
        {
            var v = e.Version; int n = e.Number;
            if (n <= 0) return v;
            var head = v.Substring(0, Math.Max(0, v.LastIndexOf('.')));
            return shortForm || head.Length == 0 ? "Build " + n.ToString(CultureInfo.InvariantCulture) : head + " build " + n.ToString(CultureInfo.InvariantCulture);
        }

        FrameworkElement BuildHead(GameBuilds.Entry e, string current)
        {
            var card = new ContentControl { Style = (Style)FindResource("Card.Group"), Margin = new Thickness(0, 0, 0, 16), Tag = e.Build };
            var body = new StackPanel { Margin = new Thickness(16, 12, 16, 12) };
            var head = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
            head.Children.Add(new TextBlock { Text = BuildLabel(e, false), Style = (Style)FindResource("Text.Section"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
            if (e.Build == current) head.Children.Add(Pill("Your game", "Accent"));
            body.Children.Add(head);
            var line = new List<string>();
            if (e.Seen != null) line.Add("First seen " + DateText(e.Seen));
            if (e.From != null) line.Add("compared with " + BuildLabel(new GameBuilds.Entry { Build = e.From }, false));
            if (line.Count > 0) body.Children.Add(new TextBlock { Text = string.Join(", ", line), Style = (Style)FindResource("Text.Secondary"), TextWrapping = TextWrapping.Wrap });
            card.Content = body;
            System.Windows.Automation.AutomationProperties.SetName(card, BuildLabel(e, false) + (e.Build == current ? ", your game" : ""));
            return card;
        }

        // ---------------------------------------------------------------- what the readers found

        FrameworkElement FoundCard(GameBuilds.Entry e, List<GameBuilds.Entry> all)
        {
            var card = new ContentControl { Style = (Style)FindResource("Card.Group"), Margin = new Thickness(0, 0, 0, 16), Tag = "found" };
            var body = new StackPanel { Margin = new Thickness(16, 12, 16, 12) };
            card.Content = body;
            TextBlock Line(string text, string style = "Text.Secondary") => new TextBlock { Style = (Style)FindResource(style), TextWrapping = TextWrapping.Wrap, MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left, Text = text };
            void Divider() { if (body.Children.Count > 0) body.Children.Add(new Border { Height = 1, Background = (Brush)FindResource("Brush.Divider"), Margin = new Thickness(0, 12, 0, 12) }); }

            var compared = GameBuilds.Kinds.Where(GameBuilds.Compared).Select(k => e.Of(k)).Where(k => k != null).ToList();
            var hotfixes = e.Of("hotfixes");
            if (compared.Count == 0)
            {
                bool oldest = all.LastOrDefault() == e;
                body.Children.Add(Line(e.From == null && oldest
                    ? "Nothing to compare with yet. When the game is patched, the Outpost reads the new game files and lists here what changed: talents, spells, items, dungeons, zones and flight points."
                    : "The Outpost had no saved copy of the build before this one to compare with, so it can't say what this patch changed in the game files."));
            }
            else if (compared.All(k => k.Total == 0))
            {
                var words = compared.Select(k => One(k.Kind)).ToList();
                var list = words.Count == 1 ? words[0] : string.Join(", ", words.Take(words.Count - 1)) + " or " + words[words.Count - 1];
                body.Children.Add(new TextBlock { Text = "The game files are the same as in " + BuildLabel(new GameBuilds.Entry { Build = e.From ?? "" }, true).ToLowerInvariant(), Style = (Style)FindResource("Text.Body"), FontWeight = FontWeights.SemiBold, Tag = "same" });
                body.Children.Add(Line("No " + list + " changed. This patch changed the game program itself, or its changes come from the servers."));
            }
            else
                foreach (var k in compared) { Divider(); body.Children.Add(KindBlock(e, k)); }

            if (hotfixes != null && hotfixes.Total > 0)
            {
                Divider();
                body.Children.Add(KindBlock(e, hotfixes));
                var cap = Line("What the servers sent your game at this build: they change spells without a patch, so this list can grow while you play.", "Text.Caption");
                cap.Margin = new Thickness(0, 8, 0, 0);
                body.Children.Add(cap);
            }
            if (compared.Any(k => k.Total > 0))
                body.Children.Add(new TextBlock
                {
                    Style = (Style)FindResource("Text.Caption"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 16, 0, 0), MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left,
                    Text = "Compared in the game files on this PC: the tables shipped with the game, before the servers' hotfixes.",
                });
            return card;
        }

        static string One(string kind)
        {
            switch (kind)
            {
                case "talents": return "talent"; case "spells": return "spell"; case "items": return "item";
                case "maps": return "map"; case "areas": return "zone"; case "flights": return "flight point";
                default: return kind;
            }
        }

        FrameworkElement KindBlock(GameBuilds.Entry e, GameBuilds.KindChanges k)
        {
            var sp = new StackPanel { Tag = k.Kind };
            var head = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
            head.Children.Add(new TextBlock { Text = GameBuilds.LabelOf(k.Kind), Style = (Style)FindResource("Text.Body"), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
            if (k.Total == 0) head.Children.Add(new TextBlock { Text = "no changes", Style = (Style)FindResource("Text.Secondary"), VerticalAlignment = VerticalAlignment.Center });
            if (k.Added > 0) head.Children.Add(Pill(k.Added.ToString("N0", CultureInfo.CurrentCulture) + " new", "Accent"));
            if (k.Modified > 0) head.Children.Add(Pill(k.Modified.ToString("N0", CultureInfo.CurrentCulture) + " changed", "Info"));
            if (k.Removed > 0) head.Children.Add(Pill(k.Removed.ToString("N0", CultureInfo.CurrentCulture) + " removed", "Warn"));
            sp.Children.Add(head);
            var key = e.Build + "|" + k.Kind;
            bool open = patchOpen.Contains(key);
            foreach (var r in open ? k.Rows : k.Rows.Take(PatchRowsFirst)) sp.Children.Add(ChangeRow(r));
            int hidden = k.Rows.Count - (open ? k.Rows.Count : Math.Min(PatchRowsFirst, k.Rows.Count));
            if (hidden > 0)
            {
                var more = new Button { Style = (Style)FindResource("GhostButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(8, 4, 0, 0), Content = "Show " + hidden.ToString("N0", CultureInfo.CurrentCulture) + " more" };
                more.Click += (s, a) => { patchOpen.Add(key); FillPatches(); };
                sp.Children.Add(more);
            }
            if (k.Total > k.Rows.Count && (open || hidden == 0))
                sp.Children.Add(new TextBlock { Text = "The first " + k.Rows.Count.ToString("N0", CultureInfo.CurrentCulture) + " are kept; the counts above are complete.", Style = (Style)FindResource("Text.Caption"), Margin = new Thickness(16, 4, 0, 0) });
            return sp;
        }

        FrameworkElement ChangeRow(GameBuilds.Row r)
        {
            var g = new Grid { Margin = new Thickness(0, 3, 0, 3), MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left, Tag = r.Op + r.Id };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            string mark = r.Op == GameBuilds.Added ? "+" : r.Op == GameBuilds.Removed ? "−" : "•";
            string role = r.Op == GameBuilds.Added ? "Brush.Accent" : r.Op == GameBuilds.Removed ? "Brush.Warn" : "Brush.Text.Tertiary";
            g.Children.Add(new TextBlock { Text = mark, Style = (Style)FindResource("Text.Body"), Foreground = (Brush)FindResource(role) });
            var t = new TextBlock { Style = (Style)FindResource("Text.Body"), TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("Brush.Text.Secondary") };
            var name = string.IsNullOrEmpty(r.Name) ? "#" + r.Id.ToString(CultureInfo.InvariantCulture) : r.Name;
            if (r.Link != null && r.Op != GameBuilds.Removed && OpenAtlas != null)
            {
                var link = new Hyperlink(new Run(name)) { Tag = r.Link };
                link.Click += (s, a) => OpenAtlas?.Invoke(r.Link);
                t.Inlines.Add(link);
            }
            else t.Inlines.Add(new Run(name) { Foreground = (Brush)FindResource("Brush.Text.Primary") });
            if (r.Op == GameBuilds.Added) t.Inlines.Add(new Run("  new"));
            else if (r.Op == GameBuilds.Removed) t.Inlines.Add(new Run("  removed"));
            else if (!string.IsNullOrEmpty(r.Detail)) t.Inlines.Add(new Run("  " + r.Detail));
            Grid.SetColumn(t, 1);
            g.Children.Add(t);
            return g;
        }

        // ---------------------------------------------------------------- Blizzard's notes

        FrameworkElement NotesCard(GameBuilds.Entry e, List<GameBuilds.Entry> all)
        {
            var card = new ContentControl { Style = (Style)FindResource("Card.Group"), Margin = new Thickness(0, 0, 0, 16), Tag = "notes" };
            var body = new StackPanel { Margin = new Thickness(16, 12, 16, 12) };
            card.Content = body;
            body.Children.Add(new TextBlock { Style = (Style)FindResource("Text.Secondary"), TextWrapping = TextWrapping.Wrap, MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left, Text = "Blizzard posts the notes for each WoW Forever build on its forums." });
            var open = new Button { Style = (Style)FindResource("Button.M"), Content = "Open Blizzard's notes", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
            open.Click += (s, a) => OpenWeb(PatchNotes.ForumPage);
            body.Children.Add(open);
            return card;
        }

        void OpenWeb(string url)
        {
            if (OpenWebLink != null) OpenWebLink(url);
            else { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { } }
        }

        // ---------------------------------------------------------------- test hooks
        public bool AboutPatchesVisibleForTest => AboutPanePatches.Visibility == Visibility.Visible;
        public Panel PatchPickForTest => PatchPick;
        public Panel PatchBodyForTest => PatchBody;
        public void PickPatchForTest(string build) { patchBuild = build; FillPatches(); }
    }
}
