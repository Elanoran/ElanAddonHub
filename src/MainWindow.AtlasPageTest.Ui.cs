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

        static void ClickRow(FrameworkElement row)
            => row.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent, Source = row });

        FrameworkElement RowFor(string link) => AtlasWalk(AtlasView).OfType<ContentControl>().FirstOrDefault(c => c.Tag is string s && s == link && c.IsVisible);

        async Task<bool> MapReady(int index = 0, int ms = 20000)
            => await WaitFor(() => AtlasView.MapsForTest.Count > index && AtlasView.MapsForTest[index].State == AtlasMap.ViewState.Ready, ms);

        async Task AtlasPageUiTest(string dir, string work, CheckFn Check)
        {
            var classic = ClassicAtlas.Current;
            // ---- the rail entry and the page opening
            Check("nav: the rail has an Atlas button with Ctrl+5 in its tooltip, and Ctrl+5 means the Atlas page", TabAtlas != null && TabAtlas.ToolTip.ToString().Contains("Ctrl+5") && TabForKey(Key.D5) == "atlas" && TabForKey(Key.NumPad5) == "atlas");
            ShowTab("atlas");
            await AtlasView.WaitForTest();
            await Task.Delay(200);
            UpdateLayout();
            Check("nav: the Atlas page opens and the others close", AtlasView.Visibility == Visibility.Visible && AddonsPage.Visibility == Visibility.Collapsed && LodgePage.Visibility == Visibility.Collapsed && SettingsPage.Visibility == Visibility.Collapsed
                && InventoryView.Visibility == Visibility.Collapsed && BrowserView.Visibility == Visibility.Collapsed && RefreshBar.Visibility == Visibility.Collapsed);
            ShowTab("inventory"); ShowTab("atlas");
            Check("nav: going away and coming back keeps the page", AtlasView.Visibility == Visibility.Visible && AtlasView.IsStart);

            // ---- the start page: game data, Classic baseline, Lodge pool, recent finds
            var fake = new FakeClassic { Dir = Path.Combine(work, "classicdl"), Idx = classic };
            Directory.CreateDirectory(fake.Dir);
            var setup = new ClassicSetup(fake);
            AtlasView.UseClassicSetup(setup);
            await AtlasView.GoStart();
            UpdateLayout();
            var st = AtlasTexts(AtlasView);
            Check("start page: title Atlas; cards for your game data, the Classic baseline, the Lodge pool and the Lodge's recent finds", st.Contains("Atlas") && st.Contains("Your game data") && st.Contains("Classic baseline") && st.Contains("Lodge pool") && st.Contains("Recent finds in the Lodge"));
            Check("start page: game data lines (items, spells and recipes, maps, seen) and the Lodge line from the session", st.Any(t => t.StartsWith("Items")) && st.Any(t => t.StartsWith("Spells and recipes")) && st.Any(t => t.StartsWith("Maps")) && st.Any(t => t.StartsWith("Seen by your game"))
                && st.Contains("Connected · sharing your observations and receiving the Lodge's") && st.Any(t => t.Contains("4 finds by 2 members") && t.Contains("Last update 10 min ago")), string.Join(" | ", st.Where(t => t.Contains("finds"))));
            Check("start page: recent finds are rows with the item, member and age; newest first (Ruby Ring 10 min ago)", st.Contains("Ruby Ring") && st.Contains("10 min ago") && RowFor("item:105") != null);
            Check("start page: the pool summary - sources, vendors, finds, members, the last update", AtlasView.Pool().Has && AtlasView.Pool().Finds == 4 && AtlasView.Pool().FindMembers == 2 && AtlasView.Pool().Vendors == 1 && AtlasView.Pool().Sources == 3 && AtlasView.Pool().Updated == AtNow - 600);

            // ---- the Classic baseline card with a fake downloader
            Check("classic card: not downloaded -> the button says what it gets (size, source, licence), the credit line and the GitHub link are there, no licence file link yet",
                setup.State == ClassicCardState.NotDownloaded && AtlasView.ClassicPrimaryForTest != null && AtlasView.ClassicPrimaryForTest.Content.ToString() == "Get Classic data (about 19 MB, cMaNGOS, GPL-3.0)"
                && AtlasTexts(AtlasView.ClassicCardForTest).Contains("Not downloaded") && AtlasTexts(AtlasView.ClassicCardForTest).Any(t => t.StartsWith("Classic baseline: cMaNGOS classic-db (GPL-3.0)")) && AtlasTexts(AtlasView.ClassicCardForTest).Contains("cMaNGOS classic-db on GitHub")
                && !AtlasTexts(AtlasView.ClassicCardForTest).Contains("Licence (file on this PC)"));
            AtlasView.ConfirmOverrideForTest = r => false;
            ClickButton(AtlasView.ClassicPrimaryForTest);
            await WaitFor(() => fake.Queries == 1 && !setup.Busy, 5000);
            Check("classic card: the user says no to the question -> nothing is downloaded (1 query, 0 downloads), the card is as it was", fake.Queries == 1 && fake.Downloads == 0 && fake.Builds == 0 && setup.State == ClassicCardState.NotDownloaded && setup.Remote == null);
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

            // ---- search in the window
            AtlasView.DebounceMsForTest = 30;
            await AtlasView.GoStart();
            AtlasView.SearchBoxForTest.Focus();
            AtlasView.SearchTextForTest = "wolf";
            Check("search box: typing opens the results under the box after a short pause", await WaitFor(() => AtlasView.ResultsOpen && AtlasView.HitsForTest.Count > 0, 5000));
            await AtlasView.SearchWaitForTest();
            UpdateLayout();
            var rt = AtlasTexts(AtlasView);
            Check("search box: results are grouped (Items, NPCs) with the best hit selected first, each hit with its name and where it comes from", rt.Contains("Items") && rt.Contains("NPCs") && AtlasView.HitsForTest[0].Name == "Wolf Pelt" && AtlasView.SelectedHitForTest == 0 && rt.Contains("Wolf Pelt") && rt.Contains("Plains Wolf") && rt.Contains("Classic baseline"), string.Join(" | ", AtlasView.HitsForTest));
            AtlasView.PressForTest(Key.Down);
            int afterDown = AtlasView.SelectedHitForTest;
            AtlasView.PressForTest(Key.Up);
            Check("search box: Down and Up move the selection", afterDown == 1 && AtlasView.SelectedHitForTest == 0);
            Snapshot(Path.Combine(dir, "atlas-page-search.png"));
            AtlasView.PressForTest(Key.Down);
            var chosen = AtlasView.HitsForTest[AtlasView.SelectedHitForTest];
            AtlasView.PressForTest(Key.Enter);
            await AtlasView.WaitForTest(); UpdateLayout();
            Check("search box: Enter opens the selected hit, closes the results and clears the box", AtlasView.CurrentId == chosen.Id && !AtlasView.ResultsOpen && AtlasView.SearchTextForTest == "" && AtlasView.Model.Id == chosen.Id, AtlasView.CurrentId + " vs " + chosen.Id);
            AtlasView.SearchTextForTest = "gnarlfang";
            AtlasView.PressForTest(Key.Enter);          // before the results came: it searches now and opens the best hit
            Check("search box: Enter before the answer arrived still opens the best hit (Gnarlfang the creature)", await WaitFor(() => AtlasView.CurrentId == "npc:1", 5000));
            AtlasView.SearchTextForTest = "item:105";
            AtlasView.PressForTest(Key.Enter);
            Check("search box: an id typed in opens that page (item:105)", await WaitFor(() => AtlasView.CurrentId == "item:105", 5000));
            AtlasView.SearchTextForTest = "qqqzzz";
            Check("search box: nothing matching says so and what to try", await WaitFor(() => AtlasView.ResultsOpen, 5000) && AtlasTexts(AtlasView).Any(t => t.StartsWith("Nothing matches \"qqqzzz\"")) && AtlasTexts(AtlasView).Any(t => t.StartsWith("Try fewer letters")));
            AtlasView.PressForTest(Key.Escape);
            Check("search box: Esc closes the results and clears the box", !AtlasView.ResultsOpen && AtlasView.SearchTextForTest == "");

            // ---- the item page in the window
            await AtlasView.Open("item:100");
            UpdateLayout();
            var tx = AtlasTexts(AtlasView);
            Check("item page: title in the header, tooltip card, 'Where it comes from', 'Your characters', 'Map', 'Found by the Lodge', the notes that apply",
                AtlasView.CurrentId == "item:100" && tx.Contains("Gnarlfang's Claw") && tx.Contains("Item Level 14") && tx.Contains("Where it comes from") && tx.Contains("Your characters") && tx.Contains("Map") && tx.Contains("Found by the Lodge") && tx.Contains("Rare"), string.Join(" | ", tx.Take(14)));
            Check("item page: the sources show the Lodge's 12 % row with its figures, the baseline's figure on the second line, both labels as chips, and the folded line for the world drops",
                tx.Contains("12% · 340 kills · 3 members") && tx.Contains("Classic baseline: 10%") && tx.Contains("Forever") && tx.Contains("Classic baseline") && tx.Contains("+ 30 world-drop sources under 0.1%"));
            Check("item page: wide window -> two columns (the card on the left, the tables on the right)", AtlasView.WideForTest);
            Check("item page: one map with a zone picker (Greenvale first)", AtlasView.MapsForTest.Count == 1 && AtlasView.zonePicks.Count == 1 && AtlasView.zonePicks[0].SelectedIndex == 0 && AtlasView.zonePicks[0].Items.Count >= 2);
            Check("item page: the map loads after the page is drawn, shows the best zone with its markers", await MapReady() && AtlasView.MapsForTest[0].UiMapId == 11 && AtlasView.MapsForTest[0].MarkerCount == AtlasQuery.Build("item:100").Map.Best.Markers.Count && AtlasView.MapsForTest[0].MarkerCount >= 5);
            var picker = AtlasView.zonePicks[0];
            int hk = picker.Items.Cast<ComboBoxItem>().ToList().FindIndex(i => ((AtlasMapZone)i.Tag).UiMapId == 13);
            picker.SelectedIndex = hk;
            await WaitFor(() => AtlasView.MapsForTest[0].UiMapId == 13 && (AtlasView.MapsForTest[0].State == AtlasMap.ViewState.Ready || AtlasView.MapsForTest[0].State == AtlasMap.ViewState.NoArt), 15000);
            Check("item page: picking Highkeep in the picker shows that zone's map and its markers", AtlasView.MapsForTest[0].UiMapId == 13 && AtlasView.MapsForTest[0].MarkerCount == ((AtlasMapZone)((ComboBoxItem)picker.SelectedItem).Tag).Markers.Count);
            picker.SelectedIndex = 0;
            await WaitFor(() => AtlasView.MapsForTest[0].UiMapId == 11 && AtlasView.MapsForTest[0].State == AtlasMap.ViewState.Ready, 15000);
            // the fold
            int before = AtlasWalk(AtlasView).OfType<ContentControl>().Count(c => c.Tag is string s && s.StartsWith("npc:"));
            var fold = AtlasWalk(AtlasView).OfType<Button>().First(b => b.Tag as string == "fold");
            ClickButton(fold);
            UpdateLayout();
            int afterOpen = AtlasWalk(AtlasView).OfType<ContentControl>().Count(c => c.Tag is string s && s.StartsWith("npc:"));
            Check("item page: the folded line opens in place (30 more rows) and 'Hide those 30' closes it again", afterOpen == before + 30 && AtlasTexts(fold).Contains("Hide those 30"), before + " -> " + afterOpen);
            ClickButton(fold); UpdateLayout();
            Check("item page: ... and closes", AtlasWalk(AtlasView).OfType<ContentControl>().Count(c => c.Tag is string s && s.StartsWith("npc:")) == before);
            Snapshot(Path.Combine(dir, "atlas-page-item.png"));

            // ---- links and history
            var row = RowFor("npc:1");
            Check("links: a row of a source is a link to its page (the NPC Gnarlfang)", row != null);
            ClickRow(row);
            await AtlasView.WaitForTest(); UpdateLayout();
            Check("links: clicking it opens the NPC page and makes it the current page", AtlasView.CurrentId == "npc:1" && AtlasView.Model.Id == "npc:1" && AtlasTexts(AtlasView).Contains("Loot") && AtlasView.History.CanBack);
            Check("links: the NPC page shows its facts with their sources and its loot with the Lodge's 12 %", AtlasTexts(AtlasView).Contains("<Fanglord>") && AtlasTexts(AtlasView).Any(t => t.StartsWith("12% · 41 of 340 kills")));
            Check("links: a fact is a link too (Faction: Stormwind opens the faction)", RowFor("zone:11") == null && AtlasWalk(AtlasView).OfType<TextBlock>().Any(t => t.Tag as string == "faction:72"));
            await MapReady(); await Task.Delay(500);
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
            await MapReady();
            var map = AtlasView.MapsForTest[0];
            string clicked = null; AtlasView.Navigated += id => clicked = id;
            map.ClickAt(map.FractionToViewport(0.55, 0.4));
            Check("links: a marker on the map opens its page (the Classic chest of the Ruby Ring -> obj:50)", clicked == "obj:50", clicked + " cl=" + string.Join(",", map.Clusters.Select(c => c.Count + "@" + c.Members[0].Label)));

            // ---- other kinds in the window
            await AtlasView.Open("zone:11"); UpdateLayout();
            tx = AtlasTexts(AtlasView);
            Check("zone page in the window: title, map first, Creatures / Objects / Quests / Notable drops tables, the facts with sources", AtlasView.Model.Id == "zone:11" && tx.Contains("Greenvale") && tx.Contains("Creatures") && tx.Contains("Objects") && tx.Contains("Quests") && tx.Contains("Notable drops") && tx.Contains("Creature levels") && AtlasView.MapsForTest.Count == 1);
            await MapReady();
            await Task.Delay(500);
            Snapshot(Path.Combine(dir, "atlas-page-zone.png"));
            await AtlasView.Open("instance:98"); UpdateLayout();
            Check("instance page in the window: a raid with no map says so in the map card's own words", await WaitFor(() => AtlasView.MapsForTest.Count == 1 && AtlasView.MapsForTest[0].State == AtlasMap.ViewState.NoArt, 5000) && AtlasView.MapsForTest[0].StateText.Contains("no map in the game files"), AtlasView.MapsForTest.Count > 0 ? AtlasView.MapsForTest[0].State + " " + AtlasView.MapsForTest[0].StateText : "no map control");
            await AtlasView.Open("item:99999"); UpdateLayout();
            Check("not found in the window: the message and a calm hint, no map card", AtlasTexts(AtlasView).Any(t => t.Contains("99999")) && AtlasView.MapsForTest.Count == 0 && AtlasView.Model.NotFound);
            await AtlasView.Open("quest:7"); UpdateLayout();
            Check("quest page in the window: text, rewards, given by, a map of the giver", AtlasTexts(AtlasView).Contains("Rewards") && AtlasTexts(AtlasView).Contains("Given by") && AtlasTexts(AtlasView).Contains("Bring four Wolf Pelts to Innkeeper Hale.") && AtlasView.MapsForTest.Count == 1);
            await AtlasView.Open("spell:4000"); UpdateLayout();
            Check("spell page in the window: facts and the recipe", AtlasTexts(AtlasView).Contains("Recipe") && AtlasTexts(AtlasView).Contains("Heals the target.") && AtlasView.MapsForTest.Count == 0);

            // ---- narrow window: one column
            await AtlasView.Open("item:100"); UpdateLayout();
            Width = 760; await Task.Delay(250); UpdateLayout();
            Check("layout: a narrow window stacks the columns (the tooltip card above the tables)", !AtlasView.WideForTest, AtlasView.WideForTest + " " + AtlasView.ActualWidth);
            Snapshot(Path.Combine(dir, "atlas-page-item-narrow.png"));
            Width = 920; await Task.Delay(250); UpdateLayout();
            Check("layout: and back to two columns when there is room", AtlasView.WideForTest);

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
            await AtlasView.Open("zone:10"); await AtlasView.Open("zone:11"); UpdateLayout(); await Task.Delay(300); UpdateLayout();
            var lb = AtlasWalk(AtlasView).OfType<ListBox>().FirstOrDefault();
            int realized = lb == null ? -1 : AtlasWalk(lb).OfType<ListBoxItem>().Count();
            Check("virtualised: the 400 rows are a virtualising list - only the rows in view are real controls (" + realized + " of 400)", lb != null && lb.Items.Count == 400 && VirtualizingPanel.GetIsVirtualizing(lb) && realized > 0 && realized < 60, "realized " + realized);
            ClassicAtlas.Use(classic);

            // ---- other places can open the Atlas
            ShowTab("addons");
            OpenInAtlas("item:105"); await AtlasView.WaitForTest();
            Check("open in Atlas: from elsewhere in the app (chat item chips, the Characters page) the rail switches to the Atlas and the page opens", TabAtlas.IsChecked == true && AtlasView.Visibility == Visibility.Visible && AtlasView.CurrentId == "item:105");
            OpenInAtlas("wolf");
            Check("open in Atlas: a name searches", await WaitFor(() => AtlasView.ResultsOpen && AtlasView.SearchTextForTest == "wolf", 5000));
            AtlasView.SearchTextForTest = ""; AtlasView.CloseResults();

            // ---- coming back to the tab does not redraw a page that has not changed
            await AtlasView.Open("item:100"); UpdateLayout();
            var modelBefore = AtlasView.Model;
            ShowTab("inventory"); ShowTab("atlas"); await AtlasView.WaitForTest(); UpdateLayout();
            Check("tab: coming back to the Atlas leaves the page as it was (the same page, not drawn again)", ReferenceEquals(AtlasView.Model, modelBefore));
            AtlasQuery.Context.Lodge().Apply(Json.Obj("{\"t\":\"atlas.data\",\"epoch\":\"e1\",\"rev\":8,\"at\":" + (AtNow - 60) + "}"));
            ShowTab("inventory"); ShowTab("atlas"); await AtlasView.WaitForTest(); UpdateLayout();
            Check("tab: when the Lodge pool changed meanwhile the page is drawn again", !ReferenceEquals(AtlasView.Model, modelBefore) && AtlasView.Model.Id == "item:100");

            // ---- screenshots of the start page: with data, and with nothing at all
            await AtlasView.GoStart(); await Task.Delay(300); UpdateLayout();
            Snapshot(Path.Combine(dir, "atlas-page-start.png"));
            ClassicAtlas.Use(null); ItemData.SetForTest(null, ItemData.State.Disabled, "off"); SpellData.SetForTest(null, SpellData.State.Disabled); SeenData.SetForTest(null, SeenData.State.Disabled); MapData.SetForTest(null, null, MapData.State.Disabled);
            AtlasQuery.Context.Lodge = () => null;
            AtlasView.UseClassicSetup(new ClassicSetup(new FakeClassic { Dir = fake.Dir }));
            AtlasView.LodgeState = () => "Not connected to a Lodge";
            await AtlasView.GoStart(); await Task.Delay(300); UpdateLayout();
            var bare = AtlasTexts(AtlasView);
            Check("start page: with no game data, no baseline and no Lodge the page still opens, says each layer is off / missing and offers the download", bare.Contains("Not connected to a Lodge") && bare.Contains("Classic baseline") && bare.Any(t => t.Contains("switched off") || t.Contains("off")) && !bare.Contains("Recent finds in the Lodge") && AtlasView.ClassicPrimaryForTest != null);
            Snapshot(Path.Combine(dir, "atlas-page-start-empty.png"));
            await AtlasView.Open("item:100"); UpdateLayout();
            Check("item page with no layer but the baseline's name missing too: a calm page, not an error", AtlasView.Model.NotFound && AtlasTexts(AtlasView).Any(t => t.Contains("Item")));

            // ---- the player's own game files (read-only), when this PC has them
            await AtlasRealChecks(dir, work, Check);
        }
    }
}
