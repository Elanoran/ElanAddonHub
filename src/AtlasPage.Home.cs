using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Maps;

namespace ElansAddonHub
{
    // --selftest "atlashome". Atlas Home (Outpost 2.74): the hero (where you are, live from the Hub status strip, else where your last character was), the cards for
    // the character, the Lodge's day and who is near you, Continue and the recent finds. AtlasHome (Services/Atlas/AtlasHome.cs) works the data out; this draws it and
    // keeps it current while you play: a new zone or level changes the hero and the cards in place (debounced), a combat or rest flag only the chips. Nothing
    // above what you read moves: a block that grows or shrinks above the view moves the scroll position with it.
    public partial class AtlasPage
    {
        // ---- what the main window hands in
        public Func<HomeLive> LiveSource;                         // the character on the strip now (null = none known)
        public Func<HomeLodge> HomeLodgeSource;                   // the Lodge's day (null = no Lodge)

        // ---- the parts of Home that change in place
        Border homeHeroHost, homeLodgeHost, homeFindsHost;
        StackPanel homeCardsHost;
        UniformGrid homeCardGrid;
        WrapPanel homeChipWrap; ProgressBar homeXpBar; TextBlock homeXpText; FrameworkElement homeRested;
        HomeHere homeHere;
        List<InvChar> homeChars = new List<InvChar>();
        bool homeFindsAll;
        int homeCardsGen;
        Task homeCardsFill = Task.CompletedTask;
        readonly DispatcherTimer homeLiveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        readonly DispatcherTimer homeLodgeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        bool homeTimersWired;
        public static int HomeDebounceMs = 600;

        // ---- for the self-test
        public HomeHere HomeHereForTest => homeHere;
        public FrameworkElement HomeHeroForTest => homeHeroHost;
        public FrameworkElement HomeLodgeForTest => homeLodgeHost?.Visibility == Visibility.Visible ? homeLodgeHost : null;
        public FrameworkElement HomeFindsForTest => homeFindsHost;
        public IReadOnlyList<string> HomeCardKeysForTest => homeCardGrid == null ? new List<string>() : homeCardGrid.Children.OfType<FrameworkElement>().Select(c => ((c.Tag as string) ?? "").Replace("home-card|", "")).ToList();
        public FrameworkElement HomeCardForTest(string key) => homeCardGrid?.Children.OfType<FrameworkElement>().FirstOrDefault(c => c.Tag as string == "home-card|" + key);
        public Task HomeCardsFillForTest => homeCardsFill;
        public int HomeHeroBuildsForTest, HomeChipUpdatesForTest;
        public double HomeXpForTest => homeXpBar?.Value ?? -1;
        public bool HomeFindsAllForTest => homeFindsAll;
        public void HomeFindsToggleForTest(bool all) { homeFindsAll = all; FillHomeFinds(); }
        public void FlushHomeLiveForTest() { if (homeLiveTimer.IsEnabled) { homeLiveTimer.Stop(); ApplyLiveChange(); } }
        public int HomeCardColumnsForTest => homeCardGrid?.Columns ?? 0;
        public ScrollViewer ScrollerForTest => Scroller;

        // ---- opening Home without a jump: the parts filled off the UI thread (the chips, the cards, the shortcuts) change the page's height a moment after it
        // is drawn, which used to show the scroll bar for a moment (or hide it) and push the page about while it faded in. Home is now held back (invisible,
        // no scroll bar) until its first fill is in - at most HomeHoldMs - and then fades in once, at its full height.
        public static int HomeHoldMs = 600;
        DispatcherTimer homeHold;
        bool homeHeld;
        public bool HomeHeldForTest => homeHeld;

        public static bool HomeHoldOffForTest;              // the self-test: open Home the 2.73 way (shown at once) to prove its check sees the difference

        void HoldHome()
        {
            if (Body.Children.Count == 0) return;
            if (HomeHoldOffForTest) { Motion.PageIn(Body); return; }
            homeHeld = true;
            Body.BeginAnimation(UIElement.OpacityProperty, null);      // a fade-in of the page before still running would show this one at once
            Body.Opacity = 0;
            Scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
            if (homeHold == null) { homeHold = new DispatcherTimer(); homeHold.Tick += (s, e) => RevealHome(); }
            homeHold.Interval = TimeSpan.FromMilliseconds(HomeHoldMs);
            homeHold.Stop(); homeHold.Start();
        }

        void RevealHome()
        {
            homeHold?.Stop();
            if (!homeHeld) return;
            homeHeld = false;
            Scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            Scroller.InvalidateMeasure(); Body.InvalidateMeasure();
            Scroller.UpdateLayout();                  // the bar is decided before the first frame that shows the page
            Body.Opacity = 1;
            Motion.PageIn(Body);
        }

        void ResetHome()
        {
            if (homeHeld) { homeHold?.Stop(); homeHeld = false; Body.BeginAnimation(UIElement.OpacityProperty, null); Body.Opacity = 1; Scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; }
            homeHeroHost = homeLodgeHost = homeFindsHost = null; homeCardsHost = null; homeCardGrid = null;
            homeChipWrap = null; homeXpBar = null; homeXpText = null; homeRested = null;
            homeLiveTimer.Stop();
        }

        void WireHomeTimers()
        {
            if (homeTimersWired) return;
            homeTimersWired = true;
            homeLiveTimer.Tick += (s, e) => { homeLiveTimer.Stop(); ApplyLiveChange(); };
            homeLodgeTimer.Tick += (s, e) => { homeLodgeTimer.Stop(); FillHomeLodge(); };
        }

        HomeLive LiveNow() { try { return LiveSource?.Invoke(); } catch (Exception e) { Util.Log("atlas home live: " + e.Message); return null; } }
        HomeHere ResolveHere(List<InvChar> chars) => AtlasHome.Resolve(LiveNow(), chars, MapData.Index);

        // ============================================================================================================================ the hero
        // 176 of painting (the zone's own, else the Atlas one) running on behind a see-through card: the character, "You are in <zone>", the zone note's first
        // sentence, the fit of the zone's creature levels, the live chips and the XP bar. A click (or Enter) opens the zone - or, in a dungeon, the dungeon.
        FrameworkElement HomeHeroBlock()
        {
            WireHomeTimers();
            homeHeroHost = new Border { Tag = "home-hero", Margin = new Thickness(0, 0, 0, 24) };
            homeHere = ResolveHere(AtlasQuery.Characters());
            homeHeroHost.Child = HomeHeroView(homeHere);
            return homeHeroHost;
        }

        FrameworkElement HomeHeroView(HomeHere h)
        {
            HomeHeroBuildsForTest++;
            homeChipWrap = null; homeXpBar = null; homeXpText = null; homeRested = null;
            ArtPiece art; string kind, link = null;
            if (!h.Known) { art = ArtLibrary.Banner("zones"); kind = "none"; }
            else if (h.InInstance) { art = ArtLibrary.Banner("instances"); kind = "instance"; if (h.InstanceId > 0) link = "instance:" + h.InstanceId; }
            else { art = h.ZoneId > 0 ? ArtLibrary.Zone(h.ZoneId) : null; kind = art != null ? "zone" : "atlas"; if (art == null) art = ArtLibrary.Banner("zones"); if (h.ZoneId > 0) link = "zone:" + h.ZoneId; }

            var content = new StackPanel();
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.Children.Add(row);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(text, 1); row.Children.Add(text);
            if (!h.Known)
            {
                var tile = new Border { Width = 56, Height = 56, CornerRadius = (CornerRadius)FindResource("Radius.Card"), Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
                tile.SetResourceReference(Border.BackgroundProperty, "Brush.Surface2");
                tile.Child = AtlasIcons.Make("zones", 32, "Brush.Text.Primary");
                row.Children.Add(tile);
                text.Children.Add(Tb("Atlas", "Text.Title"));
                var l = Tb("The world from your own game files, the Classic baseline and your Lodge.", "Text.Secondary", null, 2); l.TextWrapping = TextWrapping.Wrap; text.Children.Add(l);
                var hint = Tb("Install Elan's Hub (Addons page) and log in to a character once: Home then shows where you are and what fits your level there.", "Text.Secondary", null, 8);
                hint.TextWrapping = TextWrapping.Wrap; hint.MaxWidth = ReadWidth; hint.HorizontalAlignment = HorizontalAlignment.Left; hint.Tag = "home-hub-hint"; text.Children.Add(hint);
            }
            else
            {
                var me = new InvChar { Name = h.Name, Class = h.ClassFile, RaceFile = h.RaceFile, Sex = h.Sex, Level = h.Level };
                var pic = InventoryPage.PortraitOf(me, 56); pic.Margin = new Thickness(0, 0, 16, 0); pic.VerticalAlignment = VerticalAlignment.Center;
                row.Children.Add(pic);
                var who = new TextBlock { Style = (Style)FindResource("Text.Body"), TextTrimming = TextTrimming.CharacterEllipsis };
                who.Inlines.Add(new System.Windows.Documents.Run(h.Name) { Foreground = InventoryPage.ClassBrushOf(h.ClassFile), FontWeight = FontWeights.SemiBold });
                var cls = ClassName(h.ClassFile);
                who.Inlines.Add(new System.Windows.Documents.Run((h.Level > 0 ? " · Level " + h.Level : "") + (cls.Length > 0 ? " " + cls : "")) { Foreground = Res("Brush.Text.Secondary") });
                who.Tag = "home-who";
                text.Children.Add(who);
                var title = Tb(h.Place.Length > 0 ? (h.Live ? "You are in " : "Last seen in ") + h.Place : h.Live ? "You are playing" : "Not in the game", "Text.Title", null, 2); title.TextTrimming = TextTrimming.CharacterEllipsis; title.ToolTip = title.Text; title.Tag = "home-place";
                text.Children.Add(title);
                string line;
                if (h.InInstance) line = InstanceLine(h);
                else line = h.ZoneId > 0 ? AtlasHome.OneLiner(ZoneLore.For(h.ZoneId)?.Summary) : "";
                if (h.Place.Length == 0) line = h.Live ? "Elan's Hub does not know this place yet." : "Elan's Hub has not saved a zone for " + h.Name + " yet.";
                if (!h.Live && h.SeenAt > 0) line = (line.Length > 0 ? line + " " : "") + "Saved by Elan's Hub " + AtlasFormat.Ago(h.SeenAt, AtlasQuery.Context.NowUtc()) + ".";
                if (line.Length > 0) { var l = Tb(line, "Text.Secondary", null, 4); l.TextWrapping = TextWrapping.Wrap; l.MaxWidth = ReadWidth; l.HorizontalAlignment = HorizontalAlignment.Left; l.Tag = "home-line"; text.Children.Add(l); }
                // the chips: the fit of the zone, then what the strip says now
                homeChipWrap = new WrapPanel { Margin = new Thickness(0, 12, 0, 0), Tag = "home-chips" };
                text.Children.Add(homeChipWrap);
                FillHeroChips(h);
                // the XP bar: live percent (or the saved one), a Rested badge
                var xp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0), Tag = "home-xp" };
                homeXpBar = new ProgressBar { Style = (Style)FindResource("ThinProgress"), Minimum = 0, Maximum = 100, Width = 200, VerticalAlignment = VerticalAlignment.Center };
                homeXpText = Tb("", "Text.Caption"); homeXpText.Margin = new Thickness(8, 0, 0, 0); homeXpText.VerticalAlignment = VerticalAlignment.Center;
                homeRested = Pill("Rested", "Brush.Info.Tint", null); ((FrameworkElement)homeRested).Margin = new Thickness(8, 0, 0, 0);
                xp.Children.Add(homeXpBar); xp.Children.Add(homeXpText); xp.Children.Add(homeRested);
                text.Children.Add(xp);
                FillHeroXp(h);
            }

            var card = new ContentControl { Style = (Style)FindResource("Side.Row"), Padding = new Thickness(16), Content = content, Focusable = link != null, Cursor = link != null ? Cursors.Hand : Cursors.Arrow, Tag = "home-hero-card" };
            card.SetResourceReference(Control.BackgroundProperty, "Brush.Art.HeroCard");
            if (link != null)
            {
                var go = link;
                card.ToolTip = h.InInstance ? "Open " + h.InstanceName : "Open " + h.ZoneName;
                System.Windows.Automation.AutomationProperties.SetName(card, (string)card.ToolTip);
                card.MouseLeftButtonUp += (s, e) => { if (e.OriginalSource is DependencyObject d && IsInButton(d)) return; e.Handled = true; _ = Open(go); };
                Keyable(card, () => _ = Open(go), false);
            }
            var g = new Grid { Tag = "home-hero|" + kind + (link != null ? "|" + link : "") };
            var back = art == null ? null : ArtLibrary.Backdrop(art, "Brush.Art.HeroFade", (CornerRadius)FindResource("Radius.Card"), "home-hero-art|" + kind);
            if (back != null)
            {
                g.Children.Add(back);
                System.Windows.Automation.AutomationProperties.SetName(back, "Painting of " + (kind == "zone" ? h.ZoneName : "the Atlas"));
            }
            var stack = new StackPanel();
            stack.Children.Add(new Border { Height = back != null ? BannerHeight - 48 : 0, IsHitTestVisible = false });
            stack.Children.Add(card);
            g.Children.Add(stack);
            return g;
        }

        static bool IsInButton(DependencyObject d)
        {
            for (; d != null; d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d)) if (d is ButtonBase) return true;
            return false;
        }

        static string ClassName(string classFile)
        {
            if (string.IsNullOrEmpty(classFile)) return "";
            foreach (var kv in StripCodec.ClassFiles) if (string.Equals(kv.Value, classFile, StringComparison.OrdinalIgnoreCase)) return StripCodec.ClassNames[kv.Key];
            return "";
        }

        // "Dungeon · 5 players · level 20-24" for the instance you are in
        static string InstanceLine(HomeHere h)
        {
            var inst = h.InstanceId > 0 ? MapData.Index?.Instance(h.InstanceId) : null;
            return inst == null ? "" : AtlasInstances.Summary(inst) + ".";
        }

        // a 24 high pill (Chip) with a status tint for status, the chip veil otherwise
        Border Pill(string text, string tintKey, string textKey)
        {
            var b = new Border { Style = (Style)FindResource("Chip"), Padding = new Thickness(8, 0, 8, 0), MinHeight = 24, Margin = new Thickness(0, 0, 8, 4), Tag = "chip|" + text };
            if (tintKey != null) b.SetResourceReference(Border.BackgroundProperty, tintKey);
            var t = Tb(text, "Text.Secondary", textKey == null ? null : Res(textKey)); t.VerticalAlignment = VerticalAlignment.Center; b.Child = t;
            return b;
        }

        void FillHeroChips(HomeHere h)
        {
            if (homeChipWrap == null) return;
            homeChipWrap.Children.Clear();
            if (!h.InInstance && h.ZoneId > 0 && AtlasHome.ZoneLevels(MapData.Index, h.ZoneId, out var lo, out var hi))
            {
                var fit = AtlasHome.Fit(h.Level, lo, hi);
                var tint = fit == HomeFit.Good ? "Brush.Success.Tint" : fit == HomeFit.High ? "Brush.Warn.Tint" : fit == HomeFit.TooHigh ? "Brush.Danger.Tint" : null;
                var p = Pill(AtlasHome.FitText(fit), tint, null);
                p.ToolTip = "Creatures in " + h.ZoneName + " are level " + AtlasFormat.Levels(lo, hi) + " (Classic baseline)";
                p.Tag = "fit|" + fit;
                if (fit != HomeFit.Unknown) homeChipWrap.Children.Add(p);
                homeChipWrap.Children.Add(Pill("Creatures " + AtlasFormat.Levels(lo, hi), null, null));
            }
            foreach (var c in AtlasHome.Chips(h))
            {
                string tint = c == "In combat" || c == "Dead" ? "Brush.Danger.Tint" : c == "Resting" ? "Brush.Success.Tint" : c.StartsWith("In ", StringComparison.Ordinal) ? "Brush.Info.Tint" : null;
                homeChipWrap.Children.Add(Pill(c, tint, null));
            }
            if (!h.Live && h.SeenAt > 0) homeChipWrap.Children.Add(Pill("Last seen " + AtlasFormat.Ago(h.SeenAt, AtlasQuery.Context.NowUtc()), null, null));
            homeChipWrap.Visibility = homeChipWrap.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        void FillHeroXp(HomeHere h)
        {
            if (homeXpBar == null) return;
            bool show = h.XpPercent >= 0 && !h.InInstance;
            ((FrameworkElement)homeXpBar.Parent).Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            homeXpBar.Value = Math.Max(0, h.XpPercent);
            homeXpText.Text = show ? h.XpPercent + "% XP" + (h.Live ? "" : " when saved") : "";
            homeRested.Visibility = show && h.Rested ? Visibility.Visible : Visibility.Collapsed;
        }

        // ============================================================================================================================ live
        // The main window calls this on every change of the strip (combat, rest, zone, level ...). The chips and the XP follow at once; a new place, level or
        // character waits a moment (more changes usually follow while you zone) and then changes the hero and the cards, in place.
        public void LiveChanged()
        {
            if (!IsStart || homeHeroHost == null) return;
            var now = ResolveHere(homeChars.Count > 0 ? homeChars : AtlasQuery.Characters());
            if (homeHere != null && now.CardSig == homeHere.CardSig)
            {
                if (now.ChipSig != homeHere.ChipSig) { homeHere = now; FillHeroChips(now); FillHeroXp(now); HomeChipUpdatesForTest++; }
                return;
            }
            homeLiveTimer.Interval = TimeSpan.FromMilliseconds(HomeDebounceMs);
            homeLiveTimer.Stop(); homeLiveTimer.Start();
        }

        void ApplyLiveChange()
        {
            if (!IsStart || homeHeroHost == null) return;
            var before = homeHere;
            var now = ResolveHere(homeChars.Count > 0 ? homeChars : AtlasQuery.Characters());
            if (before != null && now.CardSig == before.CardSig) { if (now.ChipSig != before.ChipSig) { homeHere = now; FillHeroChips(now); FillHeroXp(now); } return; }
            homeHere = now;
            KeepAnchor(homeHeroHost, () => homeHeroHost.Child = HomeHeroView(now));
            // the cards follow the live character (when it is the one selected, or nothing is)
            bool sameChar = before != null && string.Equals(before.Name, now.Name, StringComparison.OrdinalIgnoreCase);
            if (now.Live && !sameChar)
            {
                var mine = AtlasHome.Match(homeChars, now.Name, now.Realm);
                if (mine != null && homeChips.ContainsKey(mine.Key)) { homeChar = mine; MarkHomeChip(mine.Key); }
            }
            if (homeChar == null || CardsFollowLive()) FillHomeCards();
            FillHomeLodge();
            FillHomeFinds();
        }

        // the cards are about the live character when it is the one chosen
        bool CardsFollowLive() => homeHere != null && homeHere.Live && homeChar != null && string.Equals(homeChar.Name, homeHere.Name, StringComparison.OrdinalIgnoreCase);

        // A block above the view that grows or shrinks moves the scroll position by as much, so what you read stays where it was: the first block of the page
        // that reaches into the view is pinned (its place on the screen) for a moment, and every change of the page's height until then puts it back there.
        // A turn of the wheel, a key or a click on the page lets go of it. (Nothing to do at the very top: the page simply grows downwards.)
        FrameworkElement pinEl; double pinY; DateTime pinUntil;
        bool pinWired;

        void KeepAnchor(FrameworkElement block, Action change)
        {
            PinView();
            change();
            HoldPin();
        }

        void PinView()
        {
            WirePin();
            if (pinEl != null && DateTime.UtcNow < pinUntil && Body.IsAncestorOf(pinEl)) { pinUntil = DateTime.UtcNow.AddSeconds(2); return; }   // already holding one
            pinEl = null;
            if (Scroller.VerticalOffset <= 0 || !(Body.Children.Count > 0 && Body.Children[0] is Panel col)) return;
            // the first block whose top is in the view (a block that only reaches into it from above may be the one that changes); else the one that spans it
            FrameworkElement spanning = null; double spanY = 0;
            foreach (var c in col.Children.OfType<FrameworkElement>())
            {
                if (c.Visibility != Visibility.Visible || c.ActualHeight <= 0) continue;
                double y = c.TranslatePoint(new Point(0, 0), Scroller).Y;
                if (y >= 0) { pinEl = c; pinY = y; pinUntil = DateTime.UtcNow.AddSeconds(2); return; }
                if (y + c.ActualHeight > 0 && spanning == null) { spanning = c; spanY = y; }
            }
            if (spanning != null) { pinEl = spanning; pinY = spanY; pinUntil = DateTime.UtcNow.AddSeconds(2); }
        }

        void HoldPin()
        {
            if (pinEl == null) return;
            if (DateTime.UtcNow > pinUntil || !Body.IsAncestorOf(pinEl)) { pinEl = null; return; }
            Scroller.UpdateLayout();
            double y = pinEl.TranslatePoint(new Point(0, 0), Scroller).Y;
            double d = y - pinY;
            if (Math.Abs(d) > 0.5) { Scroller.ScrollToVerticalOffset(Math.Max(0, Scroller.VerticalOffset + d)); Scroller.UpdateLayout(); }
        }

        void WirePin()
        {
            if (pinWired) return;
            pinWired = true;
            Body.SizeChanged += (s, e) => { if (pinEl != null && e.HeightChanged) Dispatcher.BeginInvoke(new Action(HoldPin), DispatcherPriority.Render); };
            Scroller.PreviewMouseWheel += (s, e) => pinEl = null;
            Scroller.PreviewMouseDown += (s, e) => pinEl = null;
            Scroller.PreviewKeyDown += (s, e) => pinEl = null;
        }

        // ============================================================================================================================ the cards
        // "Recommended for": the character chips, the cards for the chosen one (only those with something to show), then the shortcuts as before
        void FillHomeCards()
        {
            if (homeCardsHost == null) return;
            int gen = ++homeCardsGen;
            var host = homeCardsHost;
            var who = homeChar;
            var here = HereFor(who);
            homeCardsFill = Task.Run(async () =>
            {
                List<HomeCard> cards;
                try { cards = AtlasHome.Cards(here, who, false); }
                catch (Exception e) { Util.Log("atlas home cards: " + e.Message); cards = new List<HomeCard>(); }
                await Dispatcher.InvokeAsync(() => { if (gen == homeCardsGen && host == homeCardsHost) ShowCards(cards, here, who); });
                // the upgrades take longer the first time (the Characters page's engine): their card joins the row when they are known
                HomeCard up = null;
                try { up = AtlasHome.UpgradesCard(here, who); } catch (Exception e) { Util.Log("atlas home upgrades: " + e.Message); }
                await Dispatcher.InvokeAsync(() =>
                {
                    if (gen != homeCardsGen || host != homeCardsHost) return;
                    if (up != null) { var l = cards.ToList(); int at = l.FindIndex(c => c.Key == "recipes"); l.Insert(at < 0 ? l.Count : at, up); ShowCards(l, here, who); }
                    RevealHome();                     // Home opening: shown once every card is in (or HomeHoldMs is over)
                });
            });
        }

        // the place the cards are about: the live character's where it is played now, another one's where it was saved last
        HomeHere HereFor(InvChar who)
        {
            if (who == null) return homeHere;
            if (homeHere != null && homeHere.Live && string.Equals(homeHere.Name, who.Name, StringComparison.OrdinalIgnoreCase)) return homeHere;
            return AtlasHome.Saved(who, MapData.Index);
        }

        void ShowCards(List<HomeCard> cards, HomeHere here, InvChar who)
        {
            if (homeCardsHost == null) return;
            KeepAnchor(homeCardsHost, () =>
            {
                homeCardsHost.Children.Clear();
                homeCardGrid = null;
                if (cards.Count == 0) { homeCardsHost.Visibility = Visibility.Collapsed; return; }
                homeCardsHost.Visibility = Visibility.Visible;
                homeCardGrid = new UniformGrid { Margin = new Thickness(0, 0, -12, 0), Columns = CardColumnsFor(Scroller.ActualWidth > 0 ? Scroller.ActualWidth : 760, cards.Count) };
                foreach (var c in cards) homeCardGrid.Children.Add(CardView(c));
                homeCardsHost.Children.Add(homeCardGrid);
            });
        }

        public const double HomeCardMin = 260;
        public static int CardColumnsFor(double width, int cards) => Math.Max(1, Math.Min(Math.Max(1, cards), (int)Math.Floor((width - 8 + 12) / (HomeCardMin + 12))));

        FrameworkElement CardView(HomeCard c)
        {
            var sp = new StackPanel();
            var head = Tb(c.Title, "Text.Section"); head.TextTrimming = TextTrimming.CharacterEllipsis; head.ToolTip = c.Title;
            sp.Children.Add(head);
            if (c.Note.Length > 0) { var n = Tb(c.Note, "Text.Caption", null, 2); n.TextWrapping = TextWrapping.Wrap; sp.Children.Add(n); }
            var rows = new StackPanel { Margin = new Thickness(-8, 8, -8, 0) };
            foreach (var r in c.Rows) rows.Children.Add(CardRow(r));
            if (c.Rows.Count > 0) sp.Children.Add(rows);
            if (c.Rows.Count == 0 && c.Key == "recipes")
            {
                var big = Tb(c.Total.ToString("N0", CalendarRules.English), "Text.Title", null, 8); big.Tag = "home-recipes-count"; sp.Children.Add(big);
            }
            if (c.MoreLink.Length > 0)
            {
                var go = c.MoreLink;
                var more = GhostButton(c.MoreText, () => _ = Open(go), go); more.Margin = new Thickness(-8, 4, 0, 0); more.Tag = "home-more|" + go;
                sp.Children.Add(more);
            }
            var card = Card(sp, 12);
            card.Margin = new Thickness(0, 0, 12, 12);
            card.Tag = "home-card|" + c.Key;
            return card;
        }

        FrameworkElement CardRow(AtlasRow r)
        {
            var row = new ContentControl { Style = (Style)FindResource("Side.Row"), Padding = new Thickness(8, 4, 8, 4), Tag = "home-row|" + r.Link, ToolTip = r.Title };
            System.Windows.Automation.AutomationProperties.SetName(row, r.Title);
            var col = new StackPanel();
            var t = Tb(r.Title, "Text.Body"); t.TextTrimming = TextTrimming.CharacterEllipsis;
            if (r.Quality >= 0 && r.Quality < ItemLinks.QualityHex.Length) t.Foreground = Lodge.Avatar.Frozen(ItemLinks.QualityHex[r.Quality]);
            col.Children.Add(t);
            if (!string.IsNullOrEmpty(r.Detail)) { var d = Tb(r.Detail, "Text.Caption"); d.TextTrimming = TextTrimming.CharacterEllipsis; col.Children.Add(d); }
            row.Content = col;
            if (!string.IsNullOrEmpty(r.Link))
            {
                var go = r.Link;
                row.MouseLeftButtonUp += (s, e) => { e.Handled = true; _ = Open(go); };
                Keyable(row, () => _ = Open(go), false);
            }
            return row;
        }

        // ============================================================================================================================ the Lodge today
        // One line: who is online, today's finds, the next event and raid reset; then the members playing where you are (as far as they share it).
        FrameworkElement HomeLodgeBlock()
        {
            homeLodgeHost = new Border { Tag = "home-lodge", Margin = new Thickness(0, 0, 0, 24) };
            FillHomeLodge();
            return homeLodgeHost;
        }

        // the main window: members, presence or calendar changed (debounced)
        public void HomeLodgeChanged()
        {
            if (!IsStart || homeLodgeHost == null) return;
            WireHomeTimers();
            homeLodgeTimer.Stop(); homeLodgeTimer.Start();
        }

        void FillHomeLodge()
        {
            if (homeLodgeHost == null) return;
            HomeLodge l = null;
            try { l = HomeLodgeSource?.Invoke(); } catch (Exception e) { Util.Log("atlas home lodge: " + e.Message); }
            KeepAnchor(homeLodgeHost, () =>
            {
                if (l == null) { homeLodgeHost.Child = null; homeLodgeHost.Visibility = Visibility.Collapsed; return; }
                homeLodgeHost.Visibility = Visibility.Visible;
                var now = AtlasQuery.Context.NowUtc();
                var sp = new StackPanel();
                var head = new DockPanel();
                var title = Tb("Lodge today", "Text.Section"); title.Margin = new Thickness(0, 0, 12, 0); DockPanel.SetDock(title, Dock.Left);
                head.Children.Add(title);
                var line = Tb(AtlasHome.TodayLine(l, AtlasHome.FindsToday(AtlasQuery.LodgeOrNull(), now), now), "Text.Secondary"); line.VerticalAlignment = VerticalAlignment.Center; line.TextTrimming = TextTrimming.CharacterEllipsis; line.Tag = "home-lodge-line"; line.ToolTip = line.Text;
                head.Children.Add(line);
                sp.Children.Add(head);
                var near = AtlasHome.NearYou(l, homeHere);
                if (near.Count > 0)
                {
                    sp.Children.Add(Tb("Near you in " + homeHere.Place, "Text.Caption", null, 8));
                    var wrap = new WrapPanel { Margin = new Thickness(0, 4, 0, 0), Tag = "home-near" };
                    foreach (var m in near.Take(12)) wrap.Children.Add(MemberChip(m));
                    sp.Children.Add(wrap);
                }
                homeLodgeHost.Child = Card(sp, 0);
            });
        }

        FrameworkElement MemberChip(HomeMember m)
        {
            var b = new Border { Style = (Style)FindResource("Chip"), Padding = new Thickness(2, 2, 12, 2), Margin = new Thickness(0, 0, 8, 4), Tag = "member|" + m.CharName };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var av = new Grid { Width = 24, Height = 24 };
            var dot = new System.Windows.Shapes.Ellipse(); dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Brush.Surface3");
            av.Children.Add(dot);
            av.Children.Add(new TextBlock { Text = Lodge.Avatar.Initial(m.CharName), Foreground = m.Color ?? Res("Brush.Text.Primary"), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(av);
            var name = Tb(m.CharName, "Text.Body", m.Color); name.Margin = new Thickness(8, 0, 0, 0); name.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(name);
            var det = (m.Level > 0 ? "Lv " + m.Level : "") + (m.Status.Length > 0 ? (m.Level > 0 ? " · " : "") + m.Status : "");
            if (det.Length > 0) { var d = Tb(det, "Text.Caption"); d.Margin = new Thickness(8, 0, 0, 0); d.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(d); }
            b.Child = row;
            b.ToolTip = m.CharName + (m.Name.Length > 0 && !string.Equals(m.Name, m.CharName, StringComparison.OrdinalIgnoreCase) ? " (" + m.Name + ")" : "") + " · " + m.Place;
            return b;
        }

        // ============================================================================================================================ the recent finds
        // The Lodge's latest finds in the zone you are in (a toggle shows all of them); without a zone, all of them.
        FrameworkElement HomeFindsBlock()
        {
            var L = AtlasQuery.Context.Lodge?.Invoke();
            if (L == null || L.AllFinds.Count == 0) return null;
            homeFindsHost = new Border { Tag = "home-finds", Margin = new Thickness(0, 0, 0, 24) };
            homeFindsAll = !(homeHere != null && homeHere.ZoneId > 0 && L.AllFinds.Any(f => f.Zone == homeHere.ZoneId));
            FillHomeFinds();
            return homeFindsHost;
        }

        void FillHomeFinds()
        {
            if (homeFindsHost == null) return;
            var L = AtlasQuery.Context.Lodge?.Invoke();
            int zone = homeHere?.ZoneId ?? 0;
            KeepAnchor(homeFindsHost, () =>
            {
                var sp = new StackPanel();
                var head = new DockPanel();
                if (zone > 0)
                {
                    var seg = new StackPanel { Orientation = Orientation.Horizontal };
                    var here = new RadioButton { Style = (Style)FindResource("Segment"), Content = homeHere.ZoneName, IsChecked = !homeFindsAll, GroupName = "home-finds", Tag = "finds-here" };
                    var all = new RadioButton { Style = (Style)FindResource("Segment"), Content = "All", IsChecked = homeFindsAll, GroupName = "home-finds", Tag = "finds-all" };
                    here.Checked += (s, e) => { if (homeFindsAll) { homeFindsAll = false; FillHomeFinds(); } };
                    all.Checked += (s, e) => { if (!homeFindsAll) { homeFindsAll = true; FillHomeFinds(); } };
                    seg.Children.Add(here); seg.Children.Add(all);
                    var host = new Border { Style = (Style)FindResource("SegmentHost"), Child = seg, Margin = new Thickness(12, 0, 0, 0) };
                    DockPanel.SetDock(host, Dock.Right); head.Children.Add(host);
                }
                var title = Tb("Recent finds in the Lodge", "Text.Section"); title.VerticalAlignment = VerticalAlignment.Center; head.Children.Add(title);
                sp.Children.Add(head);
                var list = L == null ? new List<AtlasObs.Find>() : L.AllFinds.Where(f => homeFindsAll || zone <= 0 || f.Zone == zone).OrderByDescending(f => f.Seq).ToList();
                if (list.Count == 0) { var none = Tb("Nothing found in " + (homeHere?.ZoneName ?? "this zone") + " yet.", "Text.Secondary", null, 8); none.Tag = "finds-none"; sp.Children.Add(none); }
                else sp.Children.Add(FindRows(list.Take(4)));
                if (L != null && L.AllFinds.Count > 4 && AtlasCategories.Find("items") != null)
                {
                    var allBtn = GhostButton("All " + L.AllFinds.Count.ToString("N0", CalendarRules.English) + " finds", () => { _ = Open(AtlasNav.FindsId); }, "Everything the Lodge has found and seen drop");
                    allBtn.Margin = new Thickness(0, 4, 0, 0); sp.Children.Add(allBtn);
                }
                homeFindsHost.Child = Card(sp, 0);
            });
        }
    }
}
