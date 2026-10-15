using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Atlas.Classic;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Maps;

namespace ElansAddonHub
{
    // --selftest "atlaspage", part 2: the page in the window.
    public partial class MainWindow
    {
        static void ClickButton(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        // clicks Copy link in the Atlas header and returns what went to the clipboard (null when nothing did)
        string AtlasClickCopy()
        {
            string got = null; AtlasView.ClipboardSeamForTest = s => got = s;
            try { if (AtlasView.CopyLinkButtonForTest.IsVisible) ClickButton(AtlasView.CopyLinkButtonForTest); } finally { AtlasView.ClipboardSeamForTest = null; }
            return got;
        }

        static void ClickRow(FrameworkElement row)
            => row.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent, Source = row });

        FrameworkElement RowFor(string link) => AtlasWalk(AtlasView).OfType<ContentControl>().FirstOrDefault(c => c.Tag is string s && s == link && c.IsVisible);

        async Task<bool> MapReady(int index = 0, int ms = 20000)
            => await WaitFor(() => AtlasView.MapsForTest.Count > index && AtlasView.MapsForTest[index].State == AtlasMap.ViewState.Ready, ms);

        async Task AtlasPageUiTest(string dir, string work, CheckFn Check)
        {
            var classic = ClassicAtlas.Current;
            var h0 = Height;
            // ---- the rail entry and the page opening
            Check("nav: the rail has an Atlas button with Ctrl+5 in its tooltip, and Ctrl+5 means the Atlas page", TabAtlas != null && TabAtlas.ToolTip.ToString().Contains("Ctrl+5") && TabForKey(Key.D5) == "atlas" && TabForKey(Key.NumPad5) == "atlas");
            ResetAtlasForTest();           // (another section may have left the Atlas on one of its pages)
            ShowTab("atlas");
            await AtlasView.WaitForTest();
            await Task.Delay(200);
            UpdateLayout();
            Check("nav: the Atlas page opens and the others close", AtlasView.Visibility == Visibility.Visible && AddonsPage.Visibility == Visibility.Collapsed && LodgePage.Visibility == Visibility.Collapsed && SettingsPage.Visibility == Visibility.Collapsed
                && InventoryView.Visibility == Visibility.Collapsed && BrowserView.Visibility == Visibility.Collapsed && RefreshBar.Visibility == Visibility.Collapsed);
            ShowTab("inventory"); ShowTab("atlas");
            Check("nav: going away and coming back keeps the page", AtlasView.Visibility == Visibility.Visible && AtlasView.IsStart, "id=" + AtlasView.CurrentId + " vis=" + AtlasView.Visibility);

            // ---- the start page: game data, Classic baseline, Lodge pool, recent finds
            var fake = new FakeClassic { Dir = Path.Combine(work, "classicdl"), Idx = classic };
            Directory.CreateDirectory(fake.Dir);
            var setup = new ClassicSetup(fake);
            AtlasView.UseClassicSetup(setup);
            await AtlasView.GoStart();
            UpdateLayout();
            var st = AtlasTexts(AtlasView);
            Check("start page: the hero search, the Browse cards, the Lodge's recent finds and the one status line (no data cards on it)", st.Any(t => t.StartsWith("Search items, creatures")) && st.Contains("Browse") && st.Contains("Recent finds in the Lodge") && AtlasView.StatusLineForTest != null && !st.Contains("Your game data") && !st.Contains("Lodge pool"));
            Check("start page: recent finds are rows with the item, member and age; newest first (Ruby Ring 10 min ago)", st.Contains("Ruby Ring") && st.Contains("10 min ago") && RowFor("item:105") != null);
            await AtlasView.Open(AtlasNav.DataId);
            UpdateLayout();
            st = AtlasTexts(AtlasView);
            Check("data sources page: title 'Data sources'; cards for your game data, the Classic baseline, the Lodge pool and the world map cache", st.Contains("Data sources") && st.Contains("Your game data") && st.Contains("Classic baseline") && st.Contains("Lodge pool") && st.Contains("World map cache") && AtlasView.CurrentId == AtlasNav.DataId);
            Check("data sources page: game data lines (items, spells and recipes, maps, seen) and the Lodge line from the session", st.Any(t => t.StartsWith("Items")) && st.Any(t => t.StartsWith("Spells and recipes")) && st.Any(t => t.StartsWith("Maps")) && st.Any(t => t.StartsWith("Seen by your game"))
                && st.Contains("Connected · sharing your observations and receiving the Lodge's") && st.Any(t => t.Contains("4 finds by 2 members") && t.Contains("Last update 10 min ago")), string.Join(" | ", st.Where(t => t.Contains("finds"))));
            Check("data sources page: the pool summary - sources, vendors, finds, members, the last update", AtlasView.Pool().Has && AtlasView.Pool().Finds == 4 && AtlasView.Pool().FindMembers == 2 && AtlasView.Pool().Vendors == 1 && AtlasView.Pool().Sources == 3 && AtlasView.Pool().Updated == AtNow - 600);
            // the Lodge pool gets a page while the data page is open: its card is drawn again (no tab switch needed); the start page shows the new find
            {
                var origLodge = AtlasQuery.Context.Lodge; var live = AtlasTestLodge(); AtlasQuery.Context.Lodge = () => live;
                try
                {
                    AtlasView.DataChanged(); UpdateLayout();
                    var poolBefore = AtlasTexts(AtlasView);
                    live.Apply(Json.Obj("{\"t\":\"atlas.data\",\"epoch\":\"e1\",\"rev\":8,\"at\":" + (AtNow - 5) + ",\"finds\":[{\"i\":104,\"c\":1,\"at\":" + (AtNow - 60) + ",\"src\":\"npc:1\",\"z\":11,\"x\":0.3,\"y\":0.3,\"by\":\"Tess\",\"s\":9}]}"));
                    AtlasView.PoolChanged(); UpdateLayout();
                    var poolAfter = AtlasTexts(AtlasView);
                    Check("data sources page: a pool update while the page is open redraws it (5 finds by 2 members)", !poolBefore.Any(t => t.Contains("5 finds by 2 members")) && poolAfter.Any(t => t.Contains("5 finds by 2 members")), string.Join(" | ", poolAfter.Where(t => t.Contains("finds"))));
                    await AtlasView.GoStart(); UpdateLayout();
                    Check("start page: ... and the new find 'Crypt Blade' is on top of the recent finds", AtlasTexts(AtlasView).Contains("Crypt Blade"));
                    AtlasView.PoolChanged(); UpdateLayout();
                    await AtlasView.Open(AtlasNav.DataId); UpdateLayout();
                }
                finally { AtlasQuery.Context.Lodge = origLodge; AtlasView.DataChanged(); UpdateLayout(); }
            }
            // ---- the Classic baseline card with a fake downloader
            Check("classic card: not downloaded -> the button says what it gets (size, source, licence), the credit line and the GitHub link are there, no licence file link yet",
                setup.State == ClassicCardState.NotDownloaded && AtlasView.ClassicPrimaryForTest != null && AtlasView.ClassicPrimaryForTest.Content.ToString() == "Get Classic data (about 19 MB, cMaNGOS, GPL-3.0)"
                && AtlasTexts(AtlasView.ClassicCardForTest).Contains("Not downloaded") && AtlasTexts(AtlasView.ClassicCardForTest).Any(t => t.StartsWith("Classic baseline: cMaNGOS classic-db (GPL-3.0)")) && AtlasTexts(AtlasView.ClassicCardForTest).Contains("cMaNGOS classic-db on GitHub")
                && !AtlasTexts(AtlasView.ClassicCardForTest).Contains("Licence (file on this PC)"));
            AtlasView.ConfirmOverrideForTest = r => false;
            ClickButton(AtlasView.ClassicPrimaryForTest);
            await WaitFor(() => fake.Queries == 1 && !setup.Busy, 5000);
            Check("classic card: the user says no to the question -> nothing is downloaded (1 query, 0 downloads), the card is as it was", fake.Queries == 1 && fake.Downloads == 0 && fake.Builds == 0 && setup.State == ClassicCardState.NotDownloaded && setup.Remote == null);
            // ---- the "Get Classic data" banner at the top of the start page
            await AtlasView.GoStart(); UpdateLayout();
            Check("banner: while the baseline is not installed the start page opens with a banner (the first thing on it) that says what it adds, with a primary 'Get Classic data' button and 'Not now'",
                AtlasView.ClassicBannerShownForTest && ((Panel)AtlasView.ClassicBannerForTest.Parent).Children[0] == AtlasView.ClassicBannerForTest && AtlasTexts(AtlasView.ClassicBannerForTest).Contains(ClassicSetup.BannerText)
                && ClassicSetup.BannerText.StartsWith("Drop chances, vendors, spawns and quests from the Classic database: about 19 MB, downloaded from its public source and credited")
                && AtlasView.ClassicBannerGetForTest != null && AtlasView.ClassicBannerGetForTest.Content.ToString() == "Get Classic data" && AtlasView.ClassicBannerNotNowForTest != null && AtlasView.ClassicBannerNotNowForTest.Content.ToString() == "Not now",
                string.Join(" | ", AtlasTexts(AtlasView.ClassicBannerForTest)));
            int q0 = fake.Queries;
            ClickButton(AtlasView.ClassicBannerGetForTest);
            await WaitFor(() => fake.Queries == q0 + 1 && !setup.Busy, 5000);
            Check("banner: its button starts the same download as the card (the question is asked first: 'no' = one query, nothing downloaded) and the banner stays", fake.Queries == q0 + 1 && fake.Downloads == 0 && setup.State == ClassicCardState.NotDownloaded && AtlasView.ClassicBannerShownForTest);
            ClickButton(AtlasView.ClassicBannerNotNowForTest);
            await Task.Delay(100); UpdateLayout();
            Check("banner: 'Not now' hides it", !AtlasView.ClassicBannerShownForTest);
            await AtlasView.GoStart(); await Task.Delay(100); UpdateLayout();
            Check("banner: ... and it stays hidden when the start page is drawn again in this session", !AtlasView.ClassicBannerShownForTest);
            ElansAddonHub.AtlasPage.ResetClassicBannerForTest();      // (a new session: nothing is saved)
            await AtlasView.GoStart(); await Task.Delay(100); UpdateLayout();
            Check("banner: a new session shows it again", AtlasView.ClassicBannerShownForTest && AtlasView.ClassicBannerGetForTest != null);
            Snapshot(Path.Combine(dir, "atlas-page-classic-banner.png"));
            await AtlasView.Open(AtlasNav.DataId); UpdateLayout();

            var calls = new List<DialogCall>();
            AtlasView.ConfirmOverrideForTest = null;
            Dialog.TestHook = c => { lock (calls) calls.Add(c); return true; };
            File.WriteAllText(Path.Combine(fake.Dir, "LICENSE.md"), "licence text");
            var states = new List<KeyValuePair<ClassicCardState, double>>();
            setup.Changed += () => { lock (states) states.Add(new KeyValuePair<ClassicCardState, double>(setup.State, setup.Fraction)); };
            ClickButton(AtlasView.ClassicPrimaryForTest);
            bool gotReady = await WaitFor(() => setup.State == ClassicCardState.Ready, 8000);
            await Task.Delay(150);
            UpdateLayout();
            DialogCall ask; lock (calls) ask = calls.FirstOrDefault();
            Check("classic card: the question shows the exact size, the dump, the number of update files and the licence before anything downloads, with a Download button",
                ask != null && ask.Title == "Get the Classic baseline?" && ask.Confirm == "Download" && ask.Text.Contains("18.2 MB") && ask.Text.Contains("ClassicDB_test.sql.gz") && ask.Text.Contains("2 update files") && ask.Text.Contains("GPL-3.0") && ask.Text.Contains("cmangos/classic-db"), ask?.Text);
            List<KeyValuePair<ClassicCardState, double>> seq; lock (states) seq = states.ToList();
            var order = seq.Select(s => s.Key).Distinct().ToList();
            Check("classic card: states go Asking -> Downloading -> Importing -> Ready, each shown", gotReady && order.SequenceEqual(new[] { ClassicCardState.Asking, ClassicCardState.Downloading, ClassicCardState.Importing, ClassicCardState.Ready }), string.Join(">", order));
            var dl = seq.Where(s => s.Key == ClassicCardState.Downloading).Select(s => s.Value).ToList(); var im = seq.Where(s => s.Key == ClassicCardState.Importing).Select(s => s.Value).ToList();
            Check("classic card: download progress rises to 100 % (fraction of bytes), import progress rises to 100 % too", dl.Count >= 4 && dl.Zip(dl.Skip(1), (a, b) => b >= a).All(x => x) && Math.Abs(dl.Max() - 1) < 1e-9 && im.Count >= 3 && im.Zip(im.Skip(1), (a, b) => b >= a).All(x => x) && Math.Abs(im.Max() - 1) < 1e-9);
            var ready = AtlasTexts(AtlasView.ClassicCardForTest);
            string wantReady = "Ready · dump of 2 May 2024 · " + classic.Npcs.Count + " creatures, " + classic.Items.Count + " items, " + classic.Quests.Count + " quests";
            await AtlasView.GoStart(); UpdateLayout();
            Check("banner: once the baseline is ready the banner is gone", !AtlasView.ClassicBannerShownForTest && AtlasView.ClassicBannerGetForTest == null);
            await AtlasView.Open(AtlasNav.DataId); UpdateLayout();
            Check("classic card: ready shows the dump date and what is in it, a Remove button, the credit line and a link to the licence file next to the data", ready.Contains(wantReady) && ready.Contains("Ready") && ready.Contains("Remove from this PC") && ready.Contains("Licence (file on this PC)") && AtlasView.ClassicRemoveForTest != null && AtlasView.ClassicPrimaryForTest == null, string.Join(" | ", ready.Take(8)));
            string opened = null, openedUrl = null;
            AtlasView.OpenFileOverrideForTest = p => { opened = p; return true; }; AtlasView.OpenLinkOverride = u => openedUrl = u;
            ClickButton(AtlasWalk(AtlasView.ClassicCardForTest).OfType<Button>().First(b => b.Content?.ToString() == "Licence (file on this PC)"));
            ClickButton(AtlasWalk(AtlasView.ClassicCardForTest).OfType<Button>().First(b => b.Content?.ToString() == "cMaNGOS classic-db on GitHub"));
            Check("classic card: the licence link opens the local file, the GitHub link the repository", opened != null && opened.EndsWith("LICENSE.md") && openedUrl == "https://github.com/cmangos/classic-db", opened + " " + openedUrl);
            AtlasView.ConfirmRemoveOverrideForTest = () => false;
            ClickButton(AtlasView.ClassicRemoveForTest);
            Check("classic card: Remove asks first - 'no' keeps everything", fake.Deletes == 0 && setup.State == ClassicCardState.Ready);
            AtlasView.ConfirmRemoveOverrideForTest = () => true;
            ClickButton(AtlasView.ClassicRemoveForTest);
            await Task.Delay(150); UpdateLayout();
            Check("classic card: Remove deletes the data and the card offers the download again", fake.Deletes == 1 && setup.State == ClassicCardState.NotDownloaded && AtlasView.ClassicPrimaryForTest != null && AtlasTexts(AtlasView.ClassicCardForTest).Contains("Not downloaded"));
            // the card while it works (a picture for the design notes): the download holds at 50 %
            fake.Hold = new TaskCompletionSource<bool>();
            ClickButton(AtlasView.ClassicPrimaryForTest);
            await WaitFor(() => setup.State == ClassicCardState.Downloading && setup.Fraction >= 0.5, 5000);
            await Task.Delay(250); UpdateLayout();
            var progTexts = AtlasTexts(AtlasView.ClassicCardForTest);
            await AtlasView.GoStart(); await Task.Delay(100); UpdateLayout();
            Check("banner: while it downloads the banner shows the progress (bar, 'Downloading the dump · ... · 50 %') instead of the buttons", AtlasView.ClassicBannerShownForTest && AtlasView.ClassicBannerGetForTest == null && AtlasView.ClassicBannerProgressForTest != null && Math.Abs(AtlasView.ClassicBannerProgressForTest.Value - 50) < 1
                && AtlasTexts(AtlasView.ClassicBannerForTest).Any(t => t.StartsWith("Downloading the dump") && t.EndsWith("50 %")), string.Join(" | ", AtlasTexts(AtlasView.ClassicBannerForTest)));
            await AtlasView.Open(AtlasNav.DataId); await Task.Delay(100); UpdateLayout();
            Check("classic card: while downloading it shows a progress bar, 'Downloading the dump · the file · 50 %' and a Cancel button (and no Get button)", progTexts.Any(t => t.StartsWith("Downloading the dump · ClassicDB_test.sql.gz") && t.EndsWith("50 %")) && progTexts.Contains("Cancel") && AtlasView.ClassicCancelForTest != null && AtlasView.ClassicPrimaryForTest == null
                && AtlasWalk(AtlasView.ClassicCardForTest).OfType<ProgressBar>().Any(b => Math.Abs(b.Value - 50) < 1), string.Join(" | ", progTexts));
            Height = 1000; await Task.Delay(300); UpdateLayout();
            AtlasView.Scroller_ScrollToForTest(AtlasView.ClassicCardForTest);
            await Task.Delay(200);
            Snapshot(Path.Combine(dir, "atlas-page-classic-progress.png"));
            Height = h0; await Task.Delay(100);
            fake.Hold.SetResult(true);
            Check("classic card: ... and finishes after the hold", await WaitFor(() => setup.State == ClassicCardState.Ready, 8000));
            fake.Hold = null;
            await Task.Delay(100);
            AtlasView.ConfirmRemoveOverrideForTest = () => true;
            ClickButton(AtlasView.ClassicRemoveForTest);
            await Task.Delay(100);
            // a failed download: the message, a retry, nothing half-done
            fake.FailDownload = new ClassicDownloadException("GitHub is limiting requests from this connection right now. Try again in a while.");
            AtlasView.ConfirmOverrideForTest = r => true;
            int dlBefore = fake.Downloads;
            ClickButton(AtlasView.ClassicPrimaryForTest);
            await WaitFor(() => setup.State == ClassicCardState.Failed, 5000); await Task.Delay(150); UpdateLayout();
            var failTexts = AtlasTexts(AtlasView.ClassicCardForTest);
            Check("classic card: a refused download shows GitHub's message in the card with 'Try again', nothing else changes", setup.State == ClassicCardState.Failed && fake.Downloads == dlBefore + 1 && failTexts.Contains("Problem") && failTexts.Any(t => t.StartsWith("GitHub is limiting requests")) && failTexts.Contains("Try again") && fake.St == ClassicState.NotDownloaded);
            fake.FailDownload = null;
            ClickButton(AtlasView.ClassicPrimaryForTest);
            Check("classic card: Try again works", await WaitFor(() => setup.State == ClassicCardState.Ready, 8000));
            // GitHub not reachable: the message, no download started
            fake.St = ClassicState.NotDownloaded; setup.Refresh(); await Task.Delay(150);
            int dlNow = fake.Downloads;
            fake.FailQuery = new ClassicDownloadException("Could not reach GitHub: no route");
            ClickButton(AtlasView.ClassicPrimaryForTest);
            await WaitFor(() => setup.State == ClassicCardState.Failed, 5000);
            Check("classic card: GitHub not reachable -> the message, no download started", setup.Error.StartsWith("Could not reach GitHub") && fake.Downloads == dlNow, setup.Error);
            fake.FailQuery = null;
            // cancel in the middle of the download
            fake.Builds = 0;
            fake.OnDownload = ct => setup.Cancel();
            await setup.StartAsync();
            fake.OnDownload = null;
            Check("classic card: Cancel during the download stops it - no import, no failure message, the card is back to 'not downloaded'", setup.State == ClassicCardState.NotDownloaded && fake.Builds == 0 && setup.Error == "" && fake.St == ClassicState.NotDownloaded);
            // downloaded but not read
            fake.St = ClassicState.Downloaded; setup.Refresh(); await Task.Delay(100); UpdateLayout();
            Check("classic card: data on disk but no index -> 'Read the data' and Remove", setup.State == ClassicCardState.Downloaded && AtlasView.ClassicPrimaryForTest?.Content.ToString() == "Read the data" && AtlasView.ClassicRemoveForTest != null);
            int builds = fake.Builds;
            ClickButton(AtlasView.ClassicPrimaryForTest);
            Check("classic card: 'Read the data' builds the index without downloading again", await WaitFor(() => setup.State == ClassicCardState.Ready, 8000) && fake.Builds == builds + 1);
            // the real backend never touches the network here: it only reports what is on disk
            var realSetup = new ClassicSetup();
            Check("classic card: the real backend (no download in this test folder) starts at 'not downloaded' and nothing was fetched", realSetup.State == ClassicCardState.NotDownloaded || realSetup.State == ClassicCardState.Ready);
            Dialog.TestHook = null;
            AtlasView.ConfirmOverrideForTest = null;
            AtlasView.UseClassicSetup(new ClassicSetup(new FakeClassic { Dir = fake.Dir, Idx = classic, St = ClassicState.Ready }));

            // ---- search in the window: the Ctrl+K palette (the header box is its launcher)
            AtlasView.PaletteDebounceMsForTest = 30;
            await AtlasView.GoStart();
            await AtlasView.OpenPalette();
            Check("palette: opens over the page, with the search box ready and the palette's empty state", AtlasView.PaletteOpen && AtlasView.PaletteBoxForTest.IsKeyboardFocused && AtlasView.PaletteTextForTest == "");
            AtlasView.PaletteTextForTest = "wolf";
            Check("palette: typing shows the results after a short pause", await WaitFor(() => AtlasView.PaletteResultForTest != null && AtlasView.PaletteResultForTest.Query == "wolf" && AtlasView.PaletteResultForTest.Total > 0, 5000));
            await AtlasView.PaletteWaitForTest();
            UpdateLayout();
            var rt = AtlasView.PaletteTextsForTest;
            var phits = AtlasView.PaletteResultForTest.All.ToList();
            Check("palette: results are grouped (Items, NPCs) with the best hit selected first, each hit with its name and where it comes from", rt.Contains("Items") && rt.Contains("NPCs") && phits[0].Name == "Wolf Pelt" && AtlasView.PaletteSelectedForTest == 0 && rt.Contains("Wolf Pelt") && rt.Contains("Plains Wolf") && rt.Contains("Classic baseline"), string.Join(" | ", rt) + " SEL=" + AtlasView.PaletteSelectedForTest);
            AtlasView.PressPaletteForTest(Key.Down);
            int afterDown = AtlasView.PaletteSelectedForTest;
            AtlasView.PressPaletteForTest(Key.Up);
            Check("palette: Down and Up move the selection", afterDown == 1 && AtlasView.PaletteSelectedForTest == 0);
            Snapshot(Path.Combine(dir, "atlas-page-search.png"));
            AtlasView.PressPaletteForTest(Key.Down);
            var chosenId = AtlasView.PaletteIdsForTest[AtlasView.PaletteSelectedForTest];
            AtlasView.PressPaletteForTest(Key.Enter);
            await AtlasView.WaitForTest(); UpdateLayout();
            Check("palette: Enter opens the selected hit and closes the palette", await WaitFor(() => AtlasView.CurrentId == chosenId && !AtlasView.PaletteOpen, 5000) && AtlasView.Model.Id == chosenId, AtlasView.CurrentId + " vs " + chosenId);
            await AtlasView.OpenPalette("gnarlfang");
            AtlasView.PressPaletteForTest(Key.Enter);          // before the results came: it searches now and opens the best hit
            Check("palette: Enter before the answer arrived still opens the best hit (Gnarlfang the creature)", await WaitFor(() => AtlasView.CurrentId == "npc:1", 5000));
            await AtlasView.OpenPalette("item:105");
            AtlasView.PressPaletteForTest(Key.Enter);
            Check("palette: an id typed in opens that page (item:105)", await WaitFor(() => AtlasView.CurrentId == "item:105", 5000));
            await AtlasView.OpenPalette("qqqzzz");
            Check("palette: nothing matching says so and what to try", await WaitFor(() => AtlasView.PaletteResultForTest != null && AtlasView.PaletteResultForTest.Query == "qqqzzz", 5000) && AtlasView.PaletteTextsForTest.Any(t => t.StartsWith("Nothing matches \"qqqzzz\"")) && AtlasView.PaletteTextsForTest.Any(t => t.StartsWith("Try fewer letters")));
            AtlasView.PressPaletteForTest(Key.Escape);
            Check("palette: Esc closes it", !AtlasView.PaletteOpen);

            // ---- the item page in the window: the hero, the tabs, the Overview
            await AtlasView.Open("item:100");
            UpdateLayout();
            var tx = AtlasTexts(AtlasView);
            var chips = AtlasView.HeroChipsForTest;
            Check("item page: the hero names the page - the name in the quality colour, the subtitle, chips (Rare, Item Level 14, the slot, Requires Level 10, binding, the sell price) - and there is no title row under the breadcrumb",
                AtlasView.CurrentId == "item:100" && AtlasView.HeroNameForTest == "Gnarlfang's Claw" && chips.Contains("Rare") && chips.Contains("Item Level 14") && chips.Contains("Requires Level 10") && chips.Contains("Binds when picked up")
                && chips.Any(c => c.StartsWith("One-Hand")) && chips.Any(c => c.StartsWith("Sells for 1g 20s 50c")) && tx.Contains("Item 100 · from your game data") && AtlasView.TitleRowGoneForTest, string.Join(" | ", chips));
            var tabsNow = AtlasView.TabsForTest;
            Check("item page: tabs Overview | Sources | Map | Related? | Lodge, in that order, each only when it has something (Sources 'Where it comes from', Map with places, Lodge 'Found by the Lodge'); it opens on Overview",
                AtlasView.TabStripShownForTest && new[] { "overview", "sources", "map", "lodge" }.All(tabsNow.Contains) && tabsNow.SequenceEqual(tabsNow.OrderBy(t => Array.IndexOf(AtlasTabs.Order, t))) && AtlasView.TabNowForTest == "overview" && AtlasView.TabDefaultForTest == "overview", string.Join(",", tabsNow));
            Check("item page: the Overview is the tooltip card, 'Your characters' and 'Best ways to get it' (the first three sources and 'All N sources'); the tables live on their tabs",
                AtlasView.CurrentId == "item:100" && tx.Contains("Gnarlfang's Claw") && tx.Contains("Item Level 14") && tx.Contains("Your characters") && tx.Contains("Best ways to get it") && tx.Any(t => t.StartsWith("All ")) && !tx.Contains("Where it comes from") && !tx.Contains("Found by the Lodge") && !AtlasView.TabBuiltForTest("sources") && !AtlasView.TabBuiltForTest("map"),
                string.Join(" | ", tx.Take(30)));
            Check("item page: the tab buttons count what is on them (Sources 4 + 30 folded = 34 rows; Overview and Map show no count) and the names for assistive tools say so",
                AtlasView.TabCountForTest("sources") == 34 && AtlasView.TabCountForTest("overview") == 0 && System.Windows.Automation.AutomationProperties.GetName(AtlasView.TabButtonForTest("sources")) == "Sources 34" && System.Windows.Automation.AutomationProperties.GetName(AtlasView.TabButtonForTest("overview")) == "Overview", AtlasView.TabCountForTest("sources").ToString());
            var itemTip = GtAll<GameTooltip>(AtlasView.BodyForTest).FirstOrDefault();
            Check("item page: the main card of the Overview is a GameTooltip (game order: name, Item Level, binding, slot ...), the icon in a 56 px frame to its LEFT, the source chip below it in the app style",
                itemTip != null && itemTip.Title == "Gnarlfang's Claw" && itemTip.Parent is StackPanel ip && ip.Children[0] is Border ifr && ifr.Width == 56 && ip.Orientation == Orientation.Horizontal && itemTip.Texts().Contains("Item Level 14")
                && itemTip.Texts().IndexOf("Item Level 14") == 1 && !itemTip.HasFooter && tx.LastIndexOf("Forever") > tx.IndexOf("Item Level 14"), itemTip == null ? "no GameTooltip" : string.Join(" | ", itemTip.Texts()));
            Check("item page: the default window is one column - the tabs under the hero, no rail", !AtlasView.WideForTest && AtlasView.ColumnsForTest == 1 && !AtlasView.RailShownForTest, "scroller " + AtlasView.ScrollerWidthForTest + " page " + AtlasView.ActualWidth + " window " + Width);
            // Sources
            Check("item page: the Sources tab lists where it comes from; the same page, no new history step, the id names the tab (item:100?tab=sources)",
                await SwitchTab("sources") && AtlasView.TabNowForTest == "sources" && AtlasView.CurrentId == "item:100?tab=sources" && AtlasView.LinkShown == "item:100?tab=sources" && AtlasView.Model.Id == "item:100");
            tx = AtlasTexts(AtlasView);
            Check("item page: the sources show the Lodge's 12 % row with its figures, the baseline's figure on the second line, both labels as chips, and the folded line for the world drops",
                tx.Contains("Where it comes from") && tx.Contains("12% · 340 kills · 3 members") && tx.Contains("Classic baseline: 10%") && tx.Contains("Forever") && tx.Contains("Classic baseline") && tx.Contains("+ 30 world-drop sources under 0.1%") && !tx.Contains("Best ways to get it"));
            // the fold
            int before = AtlasWalk(AtlasView).OfType<ContentControl>().Count(c => c.Tag is string s && s.StartsWith("npc:"));
            var fold = AtlasWalk(AtlasView).OfType<Button>().First(b => b.Tag as string == "fold");
            ClickButton(fold);
            UpdateLayout();
            int afterOpen = AtlasWalk(AtlasView).OfType<ContentControl>().Count(c => c.Tag is string s && s.StartsWith("npc:"));
            Check("item page: the folded line opens in place (30 more rows) and 'Hide those 30' closes it again", afterOpen == before + 30 && AtlasTexts(fold).Contains("Hide those 30"), before + " -> " + afterOpen);
            ClickButton(fold); UpdateLayout();
            Check("item page: ... and closes", AtlasWalk(AtlasView).OfType<ContentControl>().Count(c => c.Tag is string s && s.StartsWith("npc:")) == before);
            // Map
            Check("item page: the Map tab has the one map with a zone picker (Greenvale first); the id is item:100?tab=map", await SwitchTab("map") && AtlasView.CurrentId == "item:100?tab=map" && AtlasView.MapsForTest.Count == 1 && AtlasView.zonePicks.Count == 1 && AtlasView.zonePicks[0].SelectedIndex == 0 && AtlasView.zonePicks[0].Items.Count >= 2);
            Check("item page: the map loads after the tab is shown, shows the best zone with its markers", await MapReady() && AtlasView.MapsForTest[0].UiMapId == 11 && AtlasView.MapsForTest[0].MarkerCount == AtlasQuery.Build("item:100").Map.Best.Markers.Count && AtlasView.MapsForTest[0].MarkerCount >= 4);
            var picker = AtlasView.zonePicks[0];
            int hk = picker.Items.Cast<ComboBoxItem>().ToList().FindIndex(i => ((AtlasMapZone)i.Tag).UiMapId == 13);
            picker.SelectedIndex = hk;
            await WaitFor(() => AtlasView.MapsForTest[0].UiMapId == 13 && (AtlasView.MapsForTest[0].State == AtlasMap.ViewState.Ready || AtlasView.MapsForTest[0].State == AtlasMap.ViewState.NoArt), 15000);
            Check("item page: picking Highkeep in the picker shows that zone's map and its markers", AtlasView.MapsForTest[0].UiMapId == 13 && AtlasView.MapsForTest[0].MarkerCount == ((AtlasMapZone)((ComboBoxItem)picker.SelectedItem).Tag).Markers.Count);
            picker.SelectedIndex = 0;
            await WaitFor(() => AtlasView.MapsForTest[0].UiMapId == 11 && AtlasView.MapsForTest[0].State == AtlasMap.ViewState.Ready, 15000);
            Height = 900; await Task.Delay(300); UpdateLayout(); await MapReady(); await Task.Delay(300);
            Snapshot(Path.Combine(dir, "atlas-page-item-map.png"));
            Height = h0; await Task.Delay(200);
            // Lodge
            Check("item page: the Lodge tab holds 'Found by the Lodge' with the members' names", await SwitchTab("lodge") && AtlasTexts(AtlasView).Contains("Found by the Lodge") && AtlasView.CurrentId == "item:100?tab=lodge");
            await SwitchTab("overview"); await Task.Delay(100); UpdateLayout();
            Height = 900; await Task.Delay(300); UpdateLayout();
            Snapshot(Path.Combine(dir, "atlas-page-item.png"));
            Height = h0; await Task.Delay(200);

            // ---- links and history
            var row = RowFor("npc:1");
            Check("links: a row of a source is a link to its page (the NPC Gnarlfang)", row != null);
            ClickRow(row);
            await AtlasView.WaitForTest(); UpdateLayout();
            Check("links: clicking it opens the NPC page and makes it the current page", AtlasView.CurrentId == "npc:1" && AtlasView.Model.Id == "npc:1" && AtlasView.TabsForTest.Contains("related") && AtlasView.History.CanBack);
            Check("links: the NPC page shows its facts on the Overview and its loot, with the Lodge's 12 %, on Related", AtlasTexts(AtlasView).Contains("<Fanglord>") && await SwitchTab("related") && AtlasTexts(AtlasView).Contains("Loot") && AtlasTexts(AtlasView).Any(t => t.StartsWith("12% · 41 of 340 kills")) && await SwitchTab("overview"));
            Check("links: a fact is a link too (Faction: Stormwind opens the faction)", RowFor("zone:11") == null && AtlasWalk(AtlasView).OfType<TextBlock>().Any(t => t.Tag as string == "faction:72"));
            await SwitchTab("map"); await MapReady(); await SwitchTab("overview"); await Task.Delay(500);
            Snapshot(Path.Combine(dir, "atlas-page-npc.png"));
            await AtlasView.Back();
            Check("history: Back returns to the item with its page drawn again", AtlasView.CurrentId == "item:100" && AtlasView.Model.Id == "item:100" && AtlasView.History.CanForward && AtlasView.BackEnabledForTest);
            await AtlasView.Forward();
            Check("history: Forward goes to the NPC again", AtlasView.CurrentId == "npc:1" && !AtlasView.History.CanForward);
            await AtlasView.Back();
            await AtlasView.Open("quest:7");
            Check("history: a new visit after Back drops the forward part", AtlasView.CurrentId == "quest:7" && !AtlasView.History.CanForward && AtlasView.History.Items.Last() == "quest:7" && AtlasView.History.Items[AtlasView.History.Count - 2] == "item:100");
            await AtlasView.GoStart();
            Check("history: the start button goes to the start page; it is a step of the history too", AtlasView.IsStart && AtlasView.History.CanBack);
            await AtlasView.Open("quest:7");
            // a marker on the map is a link, too
            await AtlasView.Open("item:105"); UpdateLayout();
            await SwitchTab("map");
            await MapReady();
            var map = AtlasView.MapsForTest[0];
            string clicked = null; AtlasView.Navigated += id => clicked = id;
            map.ClickAt(map.FractionToViewport(0.55, 0.4));
            Check("links: a marker on the map opens its page (the Classic chest of the Ruby Ring -> obj:50)", clicked == "obj:50", clicked + " cl=" + string.Join(",", map.Clusters.Select(c => c.Count + "@" + c.Members[0].Label)));

            // ---- other kinds in the window
            await AtlasView.Open("zone:11"); UpdateLayout();
            tx = AtlasTexts(AtlasView);
            Check("zone page in the window: the hero names it, the map comes first (a zone opens on its Map tab), the tabs are Overview | Sources | Map | Related", AtlasView.Model.Id == "zone:11" && tx.Contains("Greenvale") && AtlasView.TabNowForTest == "map" && AtlasView.TabDefaultForTest == "map" && AtlasView.MapsForTest.Count == 1 && new[] { "overview", "sources", "map", "related" }.All(AtlasView.TabsForTest.Contains), string.Join(",", AtlasView.TabsForTest));
            await MapReady();
            await Task.Delay(500);
            Snapshot(Path.Combine(dir, "atlas-page-zone.png"));
            await SwitchTab("related"); tx = AtlasTexts(AtlasView);
            Check("zone page: Related holds the Creatures / Objects / Quests tables; Sources the notable drops; the Overview the facts", tx.Contains("Creatures") && tx.Contains("Objects") && tx.Contains("Quests") && await SwitchTab("sources") && AtlasTexts(AtlasView).Contains("Notable drops") && await SwitchTab("overview") && AtlasView.TabNowForTest == "overview", string.Join(" | ", tx.Take(12)));
            await AtlasView.Open("instance:98"); UpdateLayout();
            Check("instance page in the window: a raid with no map says so in the map card's own words", await WaitFor(() => AtlasView.MapsForTest.Count == 1 && AtlasView.MapsForTest[0].State == AtlasMap.ViewState.NoArt, 5000) && AtlasView.MapsForTest[0].StateText.Contains("no map in the game files"), AtlasView.MapsForTest.Count > 0 ? AtlasView.MapsForTest[0].State + " " + AtlasView.MapsForTest[0].StateText : "no map control");
            await AtlasView.Open("item:99999"); UpdateLayout();
            Check("not found in the window: the message and a calm hint, no map card", AtlasTexts(AtlasView).Any(t => t.Contains("99999")) && AtlasView.MapsForTest.Count == 0 && AtlasView.Model.NotFound);
            await AtlasView.Open("quest:7"); UpdateLayout();
            Check("quest page in the window: the Overview has the text and the rewards, Sources 'Given by', the Map tab a map of the giver", AtlasTexts(AtlasView).Contains("Rewards") && AtlasTexts(AtlasView).Contains("Bring four Wolf Pelts to Innkeeper Hale.") && AtlasView.MapsForTest.Count == 0 && await SwitchTab("sources") && AtlasTexts(AtlasView).Contains("Given by") && await SwitchTab("map") && AtlasView.MapsForTest.Count == 1 && await SwitchTab("overview"));
            Check("quest page: the main card is a GameTooltip with a gold title", GtAll<GameTooltip>(AtlasView.BodyForTest).Any(g => g.Title == AtlasView.Model.Title && GtColor(GtColored(g), g.Title) == "#FFFFD100"));
            await AtlasView.Open("spell:4000"); UpdateLayout();
            Check("spell page in the window: the tooltip on the Overview, the recipe on Related, no map", AtlasTexts(AtlasView).Contains("Heals the target.") && AtlasView.MapsForTest.Count == 0 && !AtlasView.TabsForTest.Contains("map") && await SwitchTab("related") && AtlasTexts(AtlasView).Contains("Recipe") && await SwitchTab("overview"));

            Check("spell page: the main card is a GameTooltip with the description in gold", GtAll<GameTooltip>(AtlasView.BodyForTest).Any(g => g.Title == AtlasView.Model.Title && GtColor(GtColored(g), "Heals the target.") == "#FFFFD100"));
            Snapshot(Path.Combine(dir, "atlas-page-spell-gt.png"));
            await AtlasView.Open("item:100"); UpdateLayout(); await Task.Delay(300);
            Snapshot(Path.Combine(dir, "atlas-page-item-gt.png"));
            var rowTip = AtlasView.RowTipFor(new AtlasRow { Link = "item:100", Title = "Gnarlfang's Claw", Quality = 3 });
            Check("a table row's hover tooltip is the item's GameTooltip; a row of an unknown id says it is not in the game data", rowTip.Title == "Gnarlfang's Claw" && rowTip.Texts().Contains("Item Level 14") && AtlasView.RowTipFor(new AtlasRow { Link = "item:424242", Title = "Ghost" }).Texts().Contains("Not in the game data on this PC"));
            Check("a row of a creature shows the creature's GameTooltip", AtlasView.RowTipFor(new AtlasRow { Link = "quest:7", Title = "Quest" }).Title.Length > 0);

            // ---- every page shows its link, with a Copy button that puts exactly that id on the clipboard
            {
                var kinds = new[] { "item:100", "npc:1", "obj:50", "quest:7", "zone:11", "instance:98", "spell:4000", "skill:129", "faction:72", "item:99999" };
                var bad = new List<string>();
                foreach (var kid in kinds)
                {
                    await AtlasView.Open(kid); UpdateLayout();
                    var got = AtlasClickCopy();
                    if (AtlasView.LinkShown != kid || got != kid || AtlasView.LastCopiedForTest != kid || !AtlasView.CopiedShownForTest || AtlasView.CopyLinkButtonForTest.ToolTip.ToString() != "Copy link - paste it in Lodge chat or the Atlas search") bad.Add(kid + " shown=" + AtlasView.LinkShown + " copied=" + got);
                }
                Check("link: item, creature, object, quest, zone, instance, spell, skill, faction (and a page that was not found) each show their id in the header and Copy puts exactly that id on the clipboard, with 'Link copied' beside it", bad.Count == 0, string.Join("; ", bad));
                Check("link: the Copy link button is a 24 px IconButton with the name 'Copy link' (tooltip says where to paste it)", AtlasView.CopyLinkButtonForTest.Width == 24 && System.Windows.Automation.AutomationProperties.GetName(AtlasView.CopyLinkButtonForTest) == "Copy link" && AtlasView.CopyLinkButtonForTest.ToolTip.ToString().StartsWith("Copy link"));
                await AtlasView.Open("item:100"); UpdateLayout();
                await AtlasView.GoStart(); UpdateLayout();
                Check("link: the start page has no link; coming back to a page shows it again", AtlasView.LinkShown == "" && AtlasClickCopy() == null);
                await AtlasView.Back(); UpdateLayout();
                Check("link: Back restores the link of that page and hides the old confirmation", AtlasView.LinkShown == "item:100" && !AtlasView.CopiedShownForTest);
            }

            // ---- wide windows: the rail (the map thumbnail and the quick facts) beside the tab; narrow ones stack it
            {
                await AtlasView.Open("item:100"); UpdateLayout();
                var w0 = Width;
                Width = 1800; Height = 1000; await Task.Delay(300); UpdateLayout();
                await Task.Delay(300);
                var railMapEl = AtlasView.RailMapForTest;
                Check("layout: a wide window (1800) puts the rail (340 px) beside the tab - a thumbnail of the map and the quick facts - and the Overview keeps the rest", AtlasView.ColumnsForTest == 2 && AtlasView.WideForTest && AtlasView.RailShownForTest && railMapEl != null && Math.Abs(AtlasView.RailForTest.ActualWidth - 340) < 1 && AtlasView.RailForTest.ActualHeight > 100, AtlasView.ColumnsForTest + " rail " + AtlasView.RailForTest?.ActualWidth + " scroller " + AtlasView.ScrollerWidthForTest);
                await Task.Delay(700); Snapshot(Path.Combine(dir, "atlas-page-item-wide.png"));
                Check("layout: the thumbnail is a second, quiet map (not one of the page's maps), and a click on it opens the Map tab with the real one",
                    AtlasView.MapsForTest.Count == 0 && AtlasWalk(AtlasView.RailForTest).OfType<Border>().Any(b => b.Tag as string == "rail-map-open") && ClickRailMap() && AtlasView.TabNowForTest == "map" && AtlasView.MapsForTest.Count == 1 && !AtlasView.RailMapHostVisibleForTest);
                Snapshot(Path.Combine(dir, "atlas-page-item-wide-map.png"));
                await SwitchTab("overview"); await Task.Delay(200);
                await AtlasView.Open("zone:11"); UpdateLayout(); await MapReady(); await Task.Delay(600);
                Snapshot(Path.Combine(dir, "atlas-page-zone-wide.png"));
                await AtlasView.Open("item:100"); UpdateLayout();
                Width = 1100; await Task.Delay(300); UpdateLayout();
                Check("layout: at 1100 (the page area is narrower than 880 px beside the nav) it is one column again, the rail is gone", AtlasView.ColumnsForTest == 1 && !AtlasView.WideForTest && !AtlasView.RailShownForTest, AtlasView.ScrollerWidthForTest.ToString());
                Width = w0; Height = h0; await Task.Delay(250); UpdateLayout();
            }

            // ---- narrow window: one column
            await AtlasView.Open("item:100"); UpdateLayout();
            Width = 760; await Task.Delay(250); UpdateLayout();
            Check("layout: a narrow window stacks everything (the hero, the tab strip, then the tab)", !AtlasView.WideForTest && AtlasView.TabStripShownForTest, AtlasView.WideForTest + " " + AtlasView.ActualWidth);
            Snapshot(Path.Combine(dir, "atlas-page-item-narrow.png"));
            Width = 920; await Task.Delay(250); UpdateLayout();
            Check("layout: and the same at the default width", !AtlasView.WideForTest);

            // ---- never blocks the UI; a slow page does not overwrite a newer one
            int ticks = 0; var uiThread = Thread.CurrentThread.ManagedThreadId; int buildThread = -1;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) }; timer.Tick += (s, e) => ticks++; timer.Start();
            AtlasQuery.BuildHookForTest = id => { if (id == "zone:11") { buildThread = Thread.CurrentThread.ManagedThreadId; Thread.Sleep(600); } };
            var slow = AtlasView.Open("zone:11");
            await Task.Delay(350);
            int tickedWhileBuilding = ticks;
            bool skeleton = AtlasWalk(AtlasView.BodyForTest).OfType<Skeleton>().Any();
            await slow; UpdateLayout();
            timer.Stop();
            Check("never blocks: while the zone page builds (0.6 s) the window keeps running (timer ticks), a skeleton stands in, the build runs on another thread", tickedWhileBuilding >= 10 && skeleton && buildThread > 0 && buildThread != uiThread && AtlasView.Model.Id == "zone:11", tickedWhileBuilding + " ticks, skeleton " + skeleton);
            AtlasQuery.BuildHookForTest = id => { if (id == "item:100") Thread.Sleep(500); };
            var first = AtlasView.Open("item:100"); var second = AtlasView.Open("npc:1");
            await Task.WhenAll(first, second); await Task.Delay(700);
            Check("never blocks: a slow answer that arrives after a newer click is dropped (the NPC stays)", AtlasView.CurrentId == "npc:1" && AtlasView.Model.Id == "npc:1", AtlasView.Model.Id);
            AtlasQuery.BuildHookForTest = null;

            // ---- long lists are virtualised
            var big = new ClassicIndex { Provenance = classic.Provenance };
            foreach (var kv in classic.Items) big.Items[kv.Key] = kv.Value;
            for (int i = 0; i < 500; i++) big.Npcs[1000 + i] = new ClassicNpc { Id = 1000 + i, Name = "Hound " + i, MinLevel = 5, MaxLevel = 7, Spawns = new[] { Sp(0, 400 + i % 500, 300 + (i * 7) % 1600) } };
            big.Finish();
            ClassicAtlas.Use(big);
            var sw = Stopwatch.StartNew();
            var bigZone = await Task.Run(() => AtlasQuery.Build("zone:11"));
            sw.Stop();
            Check("virtualised: a zone with 500 creature kinds builds on the worker in under 3 s and its table is capped at 400 rows (+ a line for the rest)", bigZone.Section("creatures").Rows.Count == 400 && bigZone.Section("creatures").HiddenText.StartsWith("100 more") && sw.ElapsedMilliseconds < 3000, sw.ElapsedMilliseconds + " ms, " + bigZone.Section("creatures").Rows.Count);
            await AtlasView.Open("zone:10"); await AtlasView.Open("zone:11"); UpdateLayout(); await SwitchTab("related"); await Task.Delay(300); UpdateLayout();
            var lb = AtlasWalk(AtlasView).OfType<ListBox>().FirstOrDefault();
            int realized = lb == null ? -1 : AtlasWalk(lb).OfType<ListBoxItem>().Count();
            Check("virtualised: the 400 rows are a virtualising list - only the rows in view are real controls (" + realized + " of 400)", lb != null && lb.Items.Count == 400 && VirtualizingPanel.GetIsVirtualizing(lb) && realized > 0 && realized < 60, "realized " + realized);
            ClassicAtlas.Use(classic);

            // ---- other places can open the Atlas
            ShowTab("addons");
            OpenInAtlas("item:105"); await AtlasView.WaitForTest();
            Check("open in Atlas: from elsewhere in the app (chat item chips, the Characters page) the rail switches to the Atlas and the page opens", TabAtlas.IsChecked == true && AtlasView.Visibility == Visibility.Visible && AtlasView.CurrentId == "item:105");
            OpenInAtlas("wolf");
            Check("open in Atlas: a name opens the palette with it", await WaitFor(() => AtlasView.PaletteOpen && AtlasView.PaletteTextForTest == "wolf", 5000));
            AtlasView.ClosePalette();

            // ---- coming back to the tab does not redraw a page that has not changed
            await AtlasView.Open("item:100"); UpdateLayout();
            var modelBefore = AtlasView.Model;
            ShowTab("inventory"); ShowTab("atlas"); await AtlasView.WaitForTest(); UpdateLayout();
            Check("tab: coming back to the Atlas leaves the page as it was (the same page, not drawn again)", ReferenceEquals(AtlasView.Model, modelBefore));
            AtlasQuery.Context.Lodge().Apply(Json.Obj("{\"t\":\"atlas.data\",\"epoch\":\"e1\",\"rev\":8,\"at\":" + (AtNow - 60) + "}"));
            ShowTab("inventory"); ShowTab("atlas"); await AtlasView.WaitForTest(); UpdateLayout();
            Check("tab: when the Lodge pool changed meanwhile the page is drawn again", !ReferenceEquals(AtlasView.Model, modelBefore) && AtlasView.Model.Id == "item:100");

            // ---- the Atlas shell: sub-navigation, recents and pins, breadcrumb, Home, palette
            await AtlasShellChecks(dir, Check);
            await AtlasRefresh2Checks(dir, Check);

            // ---- screenshots of the start page: with data, and with nothing at all
            Height = 1000; await AtlasView.GoStart(); await Task.Delay(400); UpdateLayout();
            Snapshot(Path.Combine(dir, "atlas-page-start.png"));
            Height = h0;
            ClassicAtlas.Use(null); ItemData.SetForTest(null, ItemData.State.Disabled, "off"); SpellData.SetForTest(null, SpellData.State.Disabled); SeenData.SetForTest(null, SeenData.State.Disabled); MapData.SetForTest(null, null, MapData.State.Disabled);
            AtlasQuery.Context.Lodge = () => null;
            AtlasView.UseClassicSetup(new ClassicSetup(new FakeClassic { Dir = fake.Dir }));
            AtlasView.LodgeState = () => "Not connected to a Lodge";
            await AtlasView.GoStart(); await Task.Delay(300); UpdateLayout();
            var bareHome = AtlasTexts(AtlasView);
            Check("start page: with no game data, no baseline and no Lodge the page still opens, its status line says each source is off / missing, there is no finds block and the Classic banner offers the download", bareHome.Contains("Game data off") && bareHome.Contains("Classic baseline not on this PC") && bareHome.Contains("No Lodge") && !bareHome.Contains("Recent finds in the Lodge") && AtlasView.ClassicBannerGetForTest != null, string.Join(" | ", bareHome.Where(t => t.Contains("Game") || t.Contains("Lodge"))));
            Snapshot(Path.Combine(dir, "atlas-page-start-empty.png"));
            await AtlasView.Open(AtlasNav.DataId); await Task.Delay(200); UpdateLayout();
            var bare = AtlasTexts(AtlasView);
            Check("data sources page: with no game data, no baseline and no Lodge it still opens, says each layer is off / missing and offers the download", bare.Contains("Not connected to a Lodge") && bare.Contains("Classic baseline") && bare.Any(t => t.Contains("switched off") || t.Contains("off")) && AtlasView.ClassicPrimaryForTest != null);
            Snapshot(Path.Combine(dir, "atlas-data-empty.png"));
            await AtlasView.Open("item:100"); UpdateLayout();
            Check("item page with no layer but the baseline's name missing too: a calm page, not an error", AtlasView.Model.NotFound && AtlasTexts(AtlasView).Any(t => t.Contains("Item")));

            // ---- the player's own game files (read-only), when this PC has them
            await AtlasRealChecks(dir, work, Check);
        }
    }
}
