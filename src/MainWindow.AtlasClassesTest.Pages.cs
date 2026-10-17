using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Db2;

namespace ElansAddonHub
{
    // --selftest "atlasclasses", part 4 (2.81): the class and race pages' Overview and frame - no game-tooltip card under the tabs (the hero says it), the
    // "About" card with the game's own words and the Outpost's notes, each marked; the hero and the tabs stay put while the content scrolls, and the page's
    // own scroll bar never shows (frame-sampled while the page opens, at three window heights).
    public partial class MainWindow
    {
        async Task ClassPagesChecks(CheckFn Check, string dir)
        {
            // ---- the notes shipped in the exe: every class and race of Classic and Forever's Skyborne pair, in the house style
            var classFiles = new[] { "WARRIOR", "PALADIN", "HUNTER", "ROGUE", "PRIEST", "SHAMAN", "MAGE", "WARLOCK", "DRUID" };
            var raceNames = new[] { "Human", "Dwarf", "Night Elf", "Gnome", "Orc", "Undead", "Tauren", "Troll", "High Order Skyborne", "Windshaper Skyborne" };
            ClassLore.ResetForTest();
            var missing = classFiles.Where(f => ClassLore.ForClass(f) == null).Concat(raceNames.Where(n => ClassLore.ForRace(n) == null)).ToList();
            Check("class notes: the exe carries notes for the 9 classes and the 10 playable races (the Classic eight, High Order and Windshaper Skyborne), read without a problem",
                ClassLore.Problem == null && missing.Count == 0 && ClassLore.Classes.Count == 9 && ClassLore.Races.Count == 10, ClassLore.Problem + " missing " + string.Join(",", missing));
            var allNotes = ClassLore.Classes.Concat(ClassLore.Races).ToList();
            var thin = allNotes.Where(e => e.Summary.Length < 40 || e.History.Length < 80 || e.Facts.Count < 2 || e.Figures.Count == 0 || e.Mentions.Count == 0).Select(e => e.Name).ToList();
            Check("class notes: each one has a summary, a history, at least two facts, notable figures and 'Worth knowing' lines", thin.Count == 0, string.Join(",", thin));
            var sky = raceNames.Skip(8).Select(ClassLore.ForRace).ToList();
            var others = allNotes.Where(e => !sky.Contains(e)).ToList();
            Check("class notes: the Skyborne pair is written from the game's own data only (basis 'game', no outside source, an 'On Forever' line saying so); every other entry names its sources",
                sky.All(e => e.FromGameDataOnly && e.Sources.Count == 0 && e.Forever.Contains("not from an outside source")) && others.All(e => !e.FromGameDataOnly && e.Sources.Count > 0 && e.Sources.All(u => ZoneLore.SourceName(u).Length > 0)),
                string.Join(",", allNotes.Select(e => e.Name + ":" + e.Basis + ":" + e.Sources.Count)));
            Check("class notes: a broken file is a calm problem (nothing thrown, no entries); keys are the class file and the race name, any case",
                !ClassLore.Parse("{", out _, out _, out var why) && why.Contains("do not read") && !ClassLore.Parse("{\"classes\":[]}", out _, out _, out _)
                && ClassLore.ForClass("hunter") == ClassLore.ForClass("HUNTER") && ClassLore.ForRace("night elf") == ClassLore.ForRace("Night Elf"));

            // ---- the page models: the official text from the game, the notes, the "At a glance" lines from the page's own data
            var hp = AtlasQuery.Build("class:3"); var rp = AtlasQuery.Build("race:2");
            Check("class page: the Hunter has its About content - the game's own words (ChrClasses.Description), the Outpost's Hunter notes, figures, and 'At a glance' lines from its tables (races, armor with 'from level 40', weapons)",
                hp.ClassLore != null && hp.ClassLore.Official == "Hunters know the uncharted places of the world." && hp.ClassLore.Notes == ClassLore.ForClass("HUNTER") && hp.ClassLore.Figures.Count > 0
                && hp.ClassLore.Glance.Any(g => g.Label == "Races" && g.Items.Select(i => i.Key).SequenceEqual(new[] { "Human", "Night Elf", "Orc" }) && g.Items.All(i => i.Value.StartsWith("race:")))
                && hp.ClassLore.Glance.Any(g => g.Label == "Armor" && g.Text == "Cloth, Leather, Mail (from level 40)") && hp.ClassLore.Glance.Any(g => g.Label == "Weapons"),
                hp.ClassLore == null ? "none" : string.Join(" | ", hp.ClassLore.Glance.Select(g => g.Label + "=" + g.Text)));
            Check("race page: the Orc has the game's lore text (ChrRaces.LoreDescription), the Outpost's Orc notes and 'At a glance' lines for its classes and racial abilities",
                rp.ClassLore != null && rp.ClassLore.Official == "The orc race originated on Draenor." && rp.ClassLore.Notes == ClassLore.ForRace("Orc")
                && rp.ClassLore.Glance.Any(g => g.Label == "Classes" && g.Text == "Warrior, Hunter") && rp.ClassLore.Glance.Any(g => g.Label == "Racial abilities" && g.Text.Contains("Blood Fury")),
                rp.ClassLore == null ? "none" : string.Join(" | ", rp.ClassLore.Glance.Select(g => g.Label + "=" + g.Text)));
            var everyPage = ClassData.Data.ClassList().Select(c => AtlasQuery.Build(AtlasIds.Class(c.Id))).Concat(ClassData.Data.PlayableRaces().Select(r => AtlasQuery.Build(AtlasIds.Race(r.Id)))).ToList();
            Check("class and race pages: every class and playable race of the world has both the game's text and the Outpost's notes",
                everyPage.Count == 6 && everyPage.All(m => m.ClassLore != null && m.ClassLore.Official.Length > 0 && m.ClassLore.Notes != null), string.Join(",", everyPage.Select(m => m.Title + ":" + (m.ClassLore?.Notes != null))));
            {
                var gp = new ClassLorePage();
                gp.Glance.Add(new ClassLoreGlance { Label = "Starts in", Source = ZoneLore.Label }); gp.Glance.Add(new ClassLoreGlance { Label = "Classes", Source = AtlasLabel.Forever }); gp.Glance.Add(new ClassLoreGlance { Label = "Racial abilities", Source = AtlasLabel.Forever });
                var gm = new ClassLorePage(); gm.Glance.Add(new ClassLoreGlance { Label = "Starts in", Source = AtlasLabel.Mine }); gm.Glance.Add(new ClassLoreGlance { Label = "Classes", Source = AtlasLabel.Forever });
                var l1 = ElansAddonHub.AtlasPage.GlanceSourceLine(gp, 0); var l2 = ElansAddonHub.AtlasPage.GlanceSourceLine(gm, 3); var l3 = ElansAddonHub.AtlasPage.GlanceSourceLine(new ClassLorePage(), 2);
                Check("at a glance: the caption says where each line is from - a start taken from the zone notes counts as Outpost notes, one from your characters says so, the tables are the game data",
                    l1 == "Classes, racial abilities: from the game data; the rest: Outpost notes." && l2 == "Starts in: from your characters; classes: from the game data; the rest: Outpost notes." && l3 == "", l1 + " | " + l2);
            }
            Check("figures: a notable figure links to the creature page when the data has exactly that creature (none here: no Classic baseline, no zone notes with it), else plain text",
                AtlasQuery.FigureLink("Rexxar") == null && AtlasQuery.FigureLink("") == null);

            // ---- the pages in the window
            Width = 1300; Height = 900; await Task.Delay(80);
            async Task OpenPage(string id) { await AtlasView.Open(id); await AtlasView.WaitForTest(); await Task.Delay(200); UpdateLayout(); }
            foreach (var id in new[] { "class:3", "race:2" })
            {
                await OpenPage(id);
                var tips = GtAll<GameTooltip>(AtlasView.BodyForTest).Count;
                var ov = AtlasView.TabHostForTest is ContentControl tc ? tc.Content as Panel : null;
                var first = ov == null ? null : ov.Children.OfType<FrameworkElement>().FirstOrDefault();
                Check("window " + id + ": no game-tooltip card under the tabs any more (the hero has the name, roles, resource, start level) - the Overview opens with the 'About' card",
                    tips == 0 && first != null && first.Tag as string == "class-lore" && ReferenceEquals(first, AtlasView.ClassLoreCardForTest) && AtlasView.HeroChipsForTest.Count > 0, tips + " tooltips, first " + (first?.Tag ?? "none"));
                var card = AtlasView.ClassLoreCardForTest;
                var tags = card == null ? new List<string>() : AtlasWalk(card).OfType<FrameworkElement>().Select(f => f.Tag as string).Where(t => t != null).ToList();
                var chips = card == null ? new List<string>() : AtlasWalk(card).OfType<Border>().Where(b => (b.Tag as string ?? "").EndsWith("-label")).Select(b => (b.Child as TextBlock)?.Text).ToList();
                Check("window " + id + ": the About card marks what is the game's (a 'From the game' chip, the text set off with its caption) and what was written for the Outpost (an 'Outpost notes' chip), with the summary, At a glance, History, Notable figures, Worth knowing and Sources",
                    chips.SequenceEqual(new[] { ClassLore.GameLabel, ClassLore.Label }) && new[] { "classlore-official", "classlore-summary", "classlore-glance", "classlore-history", "classlore-figures", "classlore-mentions", "classlore-sources" }.All(tags.Contains)
                    && !tags.Contains("classlore-basis") && AtlasTexts(card).Contains("In the game's own words: the character creation text of your client."), string.Join(",", chips) + " / " + string.Join(",", tags.Distinct()));
            }
            // a race written from the game's data only is marked so on its card (a fixture model: the world has no Skyborne)
            {
                var m = AtlasQuery.Build("race:2");
                m.ClassLore.Notes = ClassLore.ForRace("High Order Skyborne"); m.ClassLore.Title = "High Order Skyborne";
                var built = AtlasView.ClassLoreCardForTestBuild(m);
                var chips = AtlasWalk(built).OfType<Border>().Where(b => (b.Tag as string ?? "").EndsWith("-label")).Select(b => (b.Child as TextBlock)?.Text).ToList();
                Check("window: notes written from the in-game data only carry 'Outpost notes, from the game's data' and a line under them saying there is no outside source",
                    chips.Contains(ClassLore.GameDataLabel) && AtlasWalk(built).OfType<TextBlock>().Any(t => t.Tag as string == "classlore-basis") && !AtlasWalk(built).OfType<FrameworkElement>().Any(f => f.Tag as string == "classlore-sources"), string.Join(",", chips));
            }

            // ---- the top stays: only the content scrolls
            await OpenPage("class:3");
            Check("frame: the class page has its fixed top - the hero (painted banner) and the tabs in the head, the tab's content in a scroller of its own; the page's Scroller does not scroll",
                AtlasView.FixedHeadOnForTest && AtlasView.FixedHeadForTest != null && AtlasView.BannerForTest != null && IsUnder(AtlasView.BannerForTest, AtlasView.FixedHeadForTest) && !IsUnder(AtlasView.TabHostForTest, AtlasView.FixedHeadForTest)
                && IsUnder(AtlasView.TabHostForTest, AtlasView.ContentScrollerForTest) && AtlasView.ScrollerForTest.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled && AtlasView.ScrollerForTest.ScrollableHeight < 0.5);
            {
                var flips = new List<string>(); var still = new List<string>();
                foreach (var hh in new[] { 700, 900, 1200 })
                {
                    Height = hh; await Task.Delay(120);
                    foreach (var id in new[] { "race:2", "class:3", "spell:3044", "class:1" })
                    {
                        var sv = AtlasView.ScrollerForTest; var seen = new List<string>(); var clock = Stopwatch.StartNew();
                        EventHandler tick = (s, e) =>
                        {
                            if (AtlasView.CurrentId != id || !AtlasView.FixedHeadOnForTest) return;    // the new class / race page only (a spell page scrolls as a whole, rightly)
                            var f = sv.ComputedVerticalScrollBarVisibility.ToString();
                            if (seen.Count == 0 || !seen[seen.Count - 1].StartsWith(f)) seen.Add(f + " (" + clock.ElapsedMilliseconds + " ms, " + Math.Round(sv.ExtentHeight) + " of " + Math.Round(sv.ViewportHeight) + ")");
                        };
                        System.Windows.Media.CompositionTarget.Rendering += tick;
                        try { await OpenPage(id); await Task.Delay(250); }
                        finally { System.Windows.Media.CompositionTarget.Rendering -= tick; }
                        if (id.StartsWith("spell:")) continue;
                        if (seen.Count != 1 || !seen[0].StartsWith("Collapsed")) flips.Add(hh + " " + id + ": " + string.Join(" ; ", seen));
                        // scrolling the content leaves the hero and the tabs where they are
                        var cs = AtlasView.ContentScrollerForTest;
                        if (cs == null) { still.Add(hh + " " + id + ": no content scroller"); continue; }
                        var hero0 = AtlasView.HeroForTest.TranslatePoint(new Point(0, 0), this); var tabs0 = AtlasView.TabButtonForTest("overview").TranslatePoint(new Point(0, 0), this);
                        var tab0 = AtlasView.TabHostForTest.TranslatePoint(new Point(0, 0), this);
                        cs.ScrollToVerticalOffset(cs.ScrollableHeight); UpdateLayout(); await Task.Delay(30);
                        var hero1 = AtlasView.HeroForTest.TranslatePoint(new Point(0, 0), this); var tabs1 = AtlasView.TabButtonForTest("overview").TranslatePoint(new Point(0, 0), this);
                        var tab1 = AtlasView.TabHostForTest.TranslatePoint(new Point(0, 0), this);
                        bool moved = cs.ScrollableHeight > 0 && tab1.Y < tab0.Y - 1;
                        if (Math.Abs(hero1.Y - hero0.Y) > 0.5 || Math.Abs(tabs1.Y - tabs0.Y) > 0.5 || (cs.ScrollableHeight > 0 && !moved) || sv.ComputedVerticalScrollBarVisibility == Visibility.Visible)
                            still.Add(hh + " " + id + ": hero " + Math.Round(hero0.Y) + "->" + Math.Round(hero1.Y) + ", tabs " + Math.Round(tabs0.Y) + "->" + Math.Round(tabs1.Y) + ", content " + Math.Round(tab0.Y) + "->" + Math.Round(tab1.Y) + " of " + Math.Round(cs.ScrollableHeight));
                        cs.ScrollToTop();
                    }
                }
                Check("frame: the page's own scroll bar never shows on a class or race page, not for a frame while it opens (from a spell page that scrolls as a whole too), at 3 window heights (700, 900, 1200)", flips.Count == 0, string.Join(" | ", flips));
                Check("frame: scrolling the content moves the content and leaves the hero and the tab strip exactly where they are on screen, at 3 window heights", still.Count == 0, string.Join(" | ", still));
            }
            Height = 700; await OpenPage("class:3");
            {
                var cs = AtlasView.ContentScrollerForTest;
                bool canScroll = cs != null && cs.ScrollableHeight > 20;
                var hero = AtlasView.HeroForTest;
                hero.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = UIElement.PreviewMouseWheelEvent, Source = hero });
                UpdateLayout(); double afterWheel = cs?.VerticalOffset ?? 0;
                cs?.ScrollToTop(); UpdateLayout();
                Keyboard.ClearFocus();
                bool key = AtlasView.PressFixedHeadKeyForTest(Key.PageDown); UpdateLayout(); double afterKey = cs?.VerticalOffset ?? 0;
                bool end = AtlasView.PressFixedHeadKeyForTest(Key.End); UpdateLayout(); double afterEnd = cs?.VerticalOffset ?? 0;
                bool home = AtlasView.PressFixedHeadKeyForTest(Key.Home); UpdateLayout(); double afterHome = cs?.VerticalOffset ?? -1;
                Check("frame: the mouse wheel over the hero scrolls the content, and so do Page Down, End and Home when the focus is not in the content (the 'Spells by level' list scrolls inside it)",
                    canScroll && afterWheel > 0 && key && afterKey > 0 && end && Math.Abs(afterEnd - cs.ScrollableHeight) < 1 && home && afterHome == 0, canScroll + " wheel " + afterWheel + " key " + afterKey + " end " + afterEnd + "/" + cs?.ScrollableHeight + " home " + afterHome);
                var search = (TextBox)AtlasView.ClassControlForTest("search");
                search.Focus();
                Check("frame: in the spell search box the keys stay the box's own (Home / End move the caret, not the page)", !AtlasView.PressFixedHeadKeyForTest(Key.End) && !AtlasView.PressFixedHeadKeyForTest(Key.Home));
                Keyboard.ClearFocus();
                cs.ScrollToVerticalOffset(200); UpdateLayout();
                await SwitchTab("related");
                Check("frame: choosing another tab shows it from its top (the content scroller goes back up), still under the same fixed head", cs.VerticalOffset == 0 && AtlasView.FixedHeadOnForTest && ReferenceEquals(cs, AtlasView.ContentScrollerForTest), cs.VerticalOffset + "");
                await SwitchTab("overview");
            }
            Height = 900;
            await OpenPage("spell:3044");
            Check("frame: other pages are as before - a spell page has no fixed head and the page's Scroller scrolls it (bar when needed, padding for it)",
                !AtlasView.FixedHeadOnForTest && AtlasView.ScrollerForTest.VerticalScrollBarVisibility == ScrollBarVisibility.Auto && AtlasView.ScrollerForTest.Padding.Right == 8);

            // ---- screenshots (ELANSHUB_SHOTS=1): a class page and a race page with the About card; one scrolled to show the top staying put
            Width = 1300; Height = 1000;
            await OpenPage("class:3"); await ShotWait(300); Snapshot(Path.Combine(dir, "atlas-classpage-hunter.png"));
            if (AtlasView.ContentScrollerForTest != null) { AtlasView.ContentScrollerForTest.ScrollToVerticalOffset(520); UpdateLayout(); }
            await ShotWait(300); Snapshot(Path.Combine(dir, "atlas-classpage-hunter-scrolled.png"));
            await OpenPage("race:2"); await ShotWait(300); Snapshot(Path.Combine(dir, "atlas-racepage-orc.png"));
            Width = 1300; Height = 900;
        }
    }
}
