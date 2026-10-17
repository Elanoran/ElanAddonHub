using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Atlas.Classic;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Maps;

namespace ElansAddonHub
{
    // --selftest "datasources" (2.92): the Data sources page on the invented world of the Atlas page test (AtlasTestLodge, the fixture Cairn, the Classic
    // fixture) plus an own-observations snapshot and flight legs made here. The numbers of every coverage bar are worked out by hand below, so a change in
    // how a source is counted fails a check. Also the source colours shared with the row chips, the source cards' lists, the Cairn card's standings, growth,
    // layout at three widths, keyboard and the text equivalents; screenshots with ELANSHUB_SHOTS=1 (datasources-*.png).
    public partial class MainWindow
    {
        static AtlasObs.Snapshot SourcesOwnFixture()
        {
            var s = new AtlasObs.Snapshot();
            s.Kills[3] = new AtlasObs.Counter { N = 12 };                                             // a Plains Wolf this PC looted (the Classic baseline knows its drops too)
            s.Nodes[1732] = new Dictionary<int, List<AtlasObs.NodePt>> { [11] = new List<AtlasObs.NodePt> { new AtlasObs.NodePt { X = 0.4, Y = 0.4, N = 1 } } };
            return s;
        }

        SourcesFacts SourcesFixture(LodgeAtlas lodge, CairnSet cairn, ClassicIndex classic, bool own = true)
        {
            return new SourcesFacts
            {
                GameReady = true, GameItems = ItemData.Count, GameSpells = SpellData.SpellCount, Lodge = lodge, Cairn = cairn, Classic = classic,
                Own = own ? SourcesOwnFixture() : null, OwnFlights = own ? new List<string> { "Horde|11>22" } : null,
                LodgeFlights = lodge != null ? new List<string> { "Horde|11>23", "Alliance|1>2" } : null,
                LodgeMembers = lodge != null ? 2 : 0, NowUtc = AtUtc(AtNow),
                Cairn2 = new CairnFacts { HasLodge = lodge != null, Online = lodge != null, Sharing = true, LodgeHandsOut = true },
            };
        }

        async Task<string> DataSourcesTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            void Check(string what, bool cond, string detail = null) { SelfTestClock.Mark(what); notes.Add((cond ? "ok   " : "FAIL ") + what + (!cond && detail != null ? " [" + detail + "]" : "")); ok &= cond; }
            var savedLodge = AtlasQuery.Context.Lodge; var savedNow = AtlasQuery.Context.NowUtc; var savedCairn = CairnAtlas.Current;
            var savedTiles = MapRender.TileSourceForTest; var savedMapDir = MapRender.DirOverride; var savedOpen = AtlasView.OpenSharingSettings;
            double savedW = Width, savedH = Height;
            var work = Path.Combine(dir, "datasources");
            try
            {
                if (Directory.Exists(work)) Util.DeleteDir(work);
                Directory.CreateDirectory(work);
                MapRender.DirOverride = Path.Combine(work, "maps");
                MapRender.TileSourceForTest = fdid => Solid(256, 256, 70, 96, 60);
                ItemData.SetForTest(AtlasTestItems(), ItemData.State.Ready);
                SpellData.SetForTest(AtlasTestSpells());
                SeenData.SetForTest(AtlasTestSeen());
                MapData.SetForTest(new MapIndex(AtlasTestMapSet()));
                var classic = AtlasTestClassic();
                ClassicAtlas.Use(classic);
                var lodge = AtlasTestLodge();
                var cairn = CairnSet.Parse(CairnDatasetJson(1, 200));
                AtlasQuery.Context.NowUtc = () => AtUtc(AtNow);
                AtlasQuery.Context.Lodge = () => lodge;
                CairnAtlas.SetForTest(cairn);
                AtlasBrowse.ForgetForTest();
                var facts = SourcesFixture(lodge, cairn, classic);

                // ============================================================================================================ the five sources, defined once
                Check("sources: five, in order (Your game, You, Your Lodge, The Cairn, Classic baseline), each with an icon (AtlasIcons src-*) and a colour token (Brush.Source.* + .Tint)",
                    string.Join(",", DataSources.Order.Select(DataSources.Title)) == "Your game,You,Your Lodge,The Cairn,Classic baseline"
                    && DataSources.Order.All(k => AtlasIcons.Has(DataSources.IconKey(k)) && TryFindResource(DataSources.BrushKey(k)) is SolidColorBrush && TryFindResource(DataSources.TintKey(k)) is SolidColorBrush));
                var hues = DataSources.Order.Select(k => ((SolidColorBrush)FindResource(DataSources.BrushKey(k))).Color).ToList();
                Check("sources: the five colours differ from each other and from the accent", hues.Distinct().Count() == 5 && !hues.Contains(((SolidColorBrush)FindResource("Brush.Accent")).Color), string.Join(",", hues));
                // the row chips: the Lodge's own rows say "lodge", the label tells the rest
                var npc1 = AtlasQuery.Build("npc:1");
                var lodgeRow = npc1.Section("loot")?.Rows.FirstOrDefault(r => r.Key == "item:100");
                var classicRow = npc1.Section("loot")?.Rows.FirstOrDefault(r => r.Label == AtlasLabel.Classic);
                var cairnRow = AtlasQuery.Build("item:100").Section("sources")?.Rows.Concat(AtlasQuery.Build("item:100").Section("sources").Hidden).FirstOrDefault(r => r.Label == AtlasLabel.Cairn);
                Check("row chips: a Lodge drop row is the Lodge's colour, a Classic row the baseline's, a Cairn row the Cairn's; a game-file row (label Forever) is your game's",
                    lodgeRow?.SourceKey == DataSources.Lodge && classicRow?.SourceKey == DataSources.Classic && cairnRow?.SourceKey == DataSources.Cairn && new AtlasRow { Label = AtlasLabel.Forever }.SourceKey == DataSources.Game
                    && new AtlasRow { Label = AtlasLabel.Mine }.SourceKey == DataSources.You && new AtlasRow { Label = "" }.SourceKey == ""
                    && (lodgeRow.SourceBrush as SolidColorBrush)?.Color == hues[2] && (classicRow.SourceBrush as SolidColorBrush)?.Color == hues[4],
                    lodgeRow?.SourceKey + " " + classicRow?.SourceKey + " " + cairnRow?.SourceKey);

                // ============================================================================================================ who owns it
                Check("owners: one line each - stays on this PC / shared with your Lodge / 2 members / 3 Lodges (give to get) / downloaded by you",
                    DataSources.Owner(DataSources.Game, facts) == "Read from your WoW install - stays on this PC." && DataSources.Owner(DataSources.You, facts) == "Noted by your own game, kept on this PC and shared with your Lodge."
                    && DataSources.Owner(DataSources.Lodge, facts) == "Shared by 2 members of your Lodge." && DataSources.Owner(DataSources.Cairn, facts) == "Pooled by 3 Lodges (give to get)."
                    && DataSources.Owner(DataSources.Classic, facts) == "Old Classic data, downloaded by you.",
                    string.Join(" | ", DataSources.Order.Select(k => DataSources.Owner(k, facts))));
                var noShare = SourcesFixture(lodge, cairn, classic); noShare.Cairn2.Sharing = false;
                var alone = SourcesFixture(null, null, null, false);
                Check("owners: not sharing says where to switch it; no Lodge, no Cairn, no baseline say so",
                    DataSources.Owner(DataSources.You, noShare).EndsWith("not shared (Settings > Lodge).") && DataSources.Owner(DataSources.Lodge, alone) == "You are not in a Lodge."
                    && DataSources.Owner(DataSources.Cairn, alone).EndsWith("not on this PC.") && DataSources.Owner(DataSources.Classic, alone) == "Old Classic data - not downloaded.");

                // ============================================================================================================ coverage (counted by hand)
                var cov = DataSources.Coverage(facts).ToDictionary(k => k.Kind);
                // drops: you npc 3; Lodge npc 1 + obj 50; Cairn npc 449 (npc 1 is the Lodge's already); Classic: npc 1, 3, 4, 5, 10-39 and obj 50 = 35, of them 32 only there
                Check("coverage drops: 1 by you, 2 by your Lodge, 1 by the Cairn, 32 only in the Classic baseline (36 in all; each thing once, for the first source)",
                    cov["drops"].ToString() == "drops 1/2/1/32 of 36", cov["drops"].ToString());
                Check("coverage drops: the sentence (the text equivalent of the bar)",
                    cov["drops"].Text == "Drops: 36 creatures and objects. Seen on Forever: 4 (11 %) - 1 by you, 2 by your Lodge, 1 by the Cairn. Only in the Classic baseline: 32.", cov["drops"].Text);
                Check("coverage vendors: the Lodge's vendor 2 (the baseline's too), the Cairn's vendor 77", cov["vendors"].ToString() == "vendors 0/1/1/0 of 2", cov["vendors"].ToString());
                Check("coverage spawns: Lodge npc 1, Cairn npc 449, 35 creatures only in the baseline", cov["spawns"].ToString() == "spawns 0/1/1/35 of 37", cov["spawns"].ToString());
                Check("coverage nodes: your node 1732, the Cairn's 1731; the baseline's chest is no gathering node", cov["nodes"].ToString() == "nodes 1/0/1/0 of 2", cov["nodes"].ToString());
                Check("coverage flight times: 1 by you, 2 by your Lodge (the Cairn's leg is the Lodge's already); the baseline has none, and says so",
                    cov["flights"].ToString() == "flights 1/2/0/0 of 3" && !cov["flights"].ClassicApplies && cov["flights"].Text == "Flight times: 3 flights. Seen on Forever: 3 - 1 by you, 2 by your Lodge. The Classic baseline has no flight times.", cov["flights"].Text);
                var covAlone = DataSources.Coverage(alone).ToDictionary(k => k.Kind);
                Check("coverage with nothing: \"nothing known yet\", and that the baseline is not on this PC",
                    covAlone["drops"].Total == 0 && covAlone["drops"].Text == "Drops: nothing known yet (the Classic baseline is not on this PC)." && covAlone["flights"].Text == "Flight times: nothing known yet.", covAlone["drops"].Text);
                var onlyClassic = DataSources.Coverage(SourcesFixture(null, null, classic, false)).First();
                Check("coverage with only the baseline: none seen on Forever yet, all 35 only in the baseline", onlyClassic.Text == "Drops: 35 creatures and objects. None seen on Forever yet. Only in the Classic baseline: 35.", onlyClassic.Text);

                // ============================================================================================================ growth and the Cairn's history
                var g = DataSources.LodgeGrowth(lodge, AtUtc(AtNow));
                Check("growth: the Lodge's finds per day from their own dates - 2 today, 1 yesterday, 1 two days ago, 28 days",
                    g.PerDay.Length == 28 && g.PerDay[27] == 2 && g.PerDay[26] == 1 && g.PerDay[25] == 1 && g.Total == 4 && g.Text == "Finds per day, last 4 weeks: 4 in all, the most on 9 October (2).", string.Join(",", g.PerDay.Skip(24)) + " " + g.Text);
                Check("growth: without a Lodge nothing is drawn and the text says why", DataSources.LodgeGrowth(null, AtUtc(AtNow)).Text == "No Lodge, so no finds to count." && !DataSources.LodgeGrowth(null, AtUtc(AtNow)).Has);
                Check("the Cairn: no history is made up - today's numbers and why there is no line",
                    DataSources.CairnHistory(cairn) == "This PC keeps only the newest dataset (2026-10-17), so there is no history to draw: today's numbers are 3 loot sources, 2 vendors and 1 flight time.", DataSources.CairnHistory(cairn));

                // ============================================================================================================ the Cairn card's standings
                CairnCardInfo Card(Action<SourcesFacts> tweak) { var f = SourcesFixture(lodge, cairn, classic); tweak(f); return DataSources.CairnCard(f); }
                var good = Card(f => { });
                Check("Cairn card: receiving -> in good standing, the dataset (day, number), the Lodges, give to get",
                    good.Standing == CairnStanding.Good && good.Title == "In good standing" && good.Dataset == "Dataset of 2026-10-17 (number 1)" && good.Lodges == "3 Lodges contribute" && good.GiveToGet.StartsWith("Give to get:") && good.GiveToGet.Contains("never names"), good.Dataset + " | " + good.Lodges);
                Check("Cairn card: sharing off -> not sharing (give to get)", Card(f => f.Cairn2.Sharing = false).Standing == CairnStanding.NotSharing && Card(f => f.Cairn2.Sharing = false).Line.Contains("Give to get"));
                Check("Cairn card: the Guild Master's status says lapsed -> lapsed (even with a copy on this PC)", Card(f => f.Cairn2.JoinStanding = "lapsed").Standing == CairnStanding.Lapsed);
                Check("Cairn card: the Lodge doesn't contribute (refused \"lodge\", no copy) -> not joined; switched off by the Guild Master -> not joined, with where to switch it",
                    Card(f => { f.Cairn = null; f.Cairn2.Refused = "lodge"; }).Standing == CairnStanding.NotJoined && Card(f => { f.Cairn = null; f.Cairn2.JoinKnown = true; f.Cairn2.JoinOn = false; }).Line.Contains("Settings > Guild Master"));
                Check("Cairn card: no Lodge -> no Lodge; a guest -> not sharing; nothing received yet -> waiting",
                    Card(f => f.Cairn2.HasLodge = false).Standing == CairnStanding.NoLodge && Card(f => f.Cairn2.Guest = true).Standing == CairnStanding.NotSharing && Card(f => f.Cairn = null).Standing == CairnStanding.Waiting
                    && Card(f => f.Cairn = null).Dataset == "No dataset on this PC" && Card(f => f.Cairn = null).Lodges == "");

                // ============================================================================================================ the lists the cards open
                Check("cards open filtered lists: items of your game / seen by the Lodge / in the Cairn's data / from the baseline; You has no list of its own",
                    DataSources.ListOf(DataSources.Game) == "browse:items?src=game" && DataSources.ListOf(DataSources.Lodge) == "browse:items?src=lodge" && DataSources.ListOf(DataSources.Cairn) == "browse:items?src=cairn"
                    && DataSources.ListOf(DataSources.Classic) == "browse:items?src=classic" && DataSources.ListOf(DataSources.You) == "" && BrowseQuery.TryParse("browse:items?src=cairn", out var cq) && cq.Src == "cairn" && cq.ToId() == "browse:items?src=cairn");
                var cairnItems = AtlasBrowse.Run("browse:items?src=cairn");
                Check("browse src=cairn: only the items the Cairn's dataset has (Gnarlfang's Claw: drops and vendors; Linen Cloth: a drop)",
                    string.Join(",", cairnItems.Rows.Select(r => r.Title).OrderBy(x => x)) == "Gnarlfang's Claw,Linen Cloth", string.Join(",", cairnItems.Rows.Select(r => r.Title)));
                var fv = AtlasBrowse.Run("browse:creatures?src=forever");
                Check("browse src=forever: the creatures with Forever drop data (Gnarlfang)", fv.Rows.Count == 1 && fv.Rows[0].Title == "Gnarlfang", string.Join(",", fv.Rows.Select(r => r.Title)));

                // ============================================================================================================ the page in the window
                ShowTab("atlas");
                bool settingsOpened = false;
                AtlasView.OpenSharingSettings = () => settingsOpened = true;
                AtlasView.SourcesFactsOverrideForTest = () => facts;
                Width = 1500; Height = 1000; await Task.Delay(200);
                async Task OpenData() { await AtlasView.Open(AtlasNav.DataId); await AtlasView.WaitForTest(); await Task.Delay(150); UpdateLayout(); }
                await OpenData();
                Check("page: the explainer first (2.91's words), then the strip of five cards in order", AtlasView.ExplainerTextsForTest.Contains(DataHonesty.Title) && DataHonesty.Explainer.All(t => AtlasView.ExplainerTextsForTest.Any(x => x.Contains(t)))
                    && AtlasView.SourceCardKeysForTest.SequenceEqual(DataSources.Order), string.Join(",", AtlasView.SourceCardKeysForTest));
                var lodgeCard = AtlasView.SourceCardTextsForTest(DataSources.Lodge);
                Check("page: a card shows its name, its figure and who owns it (Your Lodge: 2 loot sources · 4 finds, shared by 2 members)",
                    lodgeCard.Contains("Your Lodge") && lodgeCard.Contains("2 loot sources · 4 finds") && lodgeCard.Contains("Shared by 2 members of your Lodge."), string.Join(" | ", lodgeCard));
                var covTexts = AtlasView.CoverageTextsForTest;
                Check("page: five coverage rows, each named by its full sentence (what a screen reader hears)", covTexts.SequenceEqual(DataSources.Coverage(facts).Select(k => k.Text)), string.Join(" | ", covTexts));
                Check("page: the growth card names the finds per day and the Cairn's missing history", (AtlasView.GrowthTextForTest ?? "").StartsWith(g.Text) && AtlasView.GrowthTextForTest.Contains("no history to draw"), AtlasView.GrowthTextForTest);
                var cairnTexts = AtlasView.CairnCardTextsForTest;
                Check("page: the Cairn card - standing, last dataset, Lodges, give to get", cairnTexts.Contains("In good standing") && cairnTexts.Contains("Dataset of 2026-10-17 (number 1)") && cairnTexts.Contains("3 Lodges contribute") && cairnTexts.Contains(good.GiveToGet), string.Join(" | ", cairnTexts));
                Press(AtlasView.CairnSettingsForTest);
                Check("page: the Cairn card's button opens the sharing settings", settingsOpened);
                Check("page: wide window -> the strip 5 across, growth and the Cairn side by side", AtlasView.SourceStripColumnsForTest == 5 && AtlasView.SourcesPairColumnsForTest == 2, AtlasView.SourceStripColumnsForTest + "/" + AtlasView.SourcesPairColumnsForTest);
                var cards = AtlasView.SourceCardsForTest;
                var youCard = cards.First(c => (string)c.Tag == ElansAddonHub.AtlasPage.SourceCardTag + DataSources.You);
                var lCard = cards.First(c => (string)c.Tag == ElansAddonHub.AtlasPage.SourceCardTag + DataSources.Lodge);
                Check("page: the cards with a list are keyboard stops with an accessible name that says where they go; You is not a stop",
                    lCard.IsTabStop && lCard.Focusable && System.Windows.Automation.AutomationProperties.GetName(lCard).Contains("Opens Items the Lodge has seen") && !youCard.Focusable,
                    System.Windows.Automation.AutomationProperties.GetName(lCard));
                await ShotWait(400); if (SelfTestClock.ShotsOn) { Snapshot(Path.Combine(dir, "datasources-wide.png")); AtlasView.Scroller.ScrollToVerticalOffset(700); await ShotWait(300); Snapshot(Path.Combine(dir, "datasources-wide-2.png")); AtlasView.Scroller.ScrollToTop(); }
                Width = 900; Height = 1000; await Task.Delay(250); UpdateLayout(); await Task.Delay(100);
                Check("page: a narrower window -> 3 across, growth and the Cairn stacked", AtlasView.SourceStripColumnsForTest == 3 && AtlasView.SourcesPairColumnsForTest == 1, AtlasView.SourceStripColumnsForTest + "/" + AtlasView.SourcesPairColumnsForTest);
                await ShotWait(400); if (SelfTestClock.ShotsOn) Snapshot(Path.Combine(dir, "datasources-narrow.png"));
                Width = 1500; await Task.Delay(200);
                ClickRow(lCard);
                await AtlasView.WaitForTest(); await Task.Delay(150);
                Check("page: the Lodge card opens the items the Lodge has seen", AtlasView.History.Current == "browse:items?src=lodge", AtlasView.History.Current);
                await OpenData();
                // the row chips in the window carry the source's colour (the dot)
                await AtlasView.Open("npc:1?tab=related"); await AtlasView.WaitForTest(); await Task.Delay(200); UpdateLayout();
                var dots = AtlasWalk(AtlasView).OfType<System.Windows.Shapes.Ellipse>().Where(el => el.Name == "srcdot" && el.IsVisible).Select(el => (el.Fill as SolidColorBrush)?.Color).ToList();
                Check("rows in the window: the source chip's dot is the Lodge's violet on the Lodge's rows and the baseline's grey on its rows", dots.Contains(hues[2]) && dots.Contains(hues[4]), dots.Count + " dots");
                // nothing on this PC: the empty states
                AtlasView.SourcesFactsOverrideForTest = () => alone;
                await OpenData();
                Check("page with nothing: the cards say no Lodge / not received / not on this PC, the Cairn card says no Lodge",
                    AtlasView.SourceCardTextsForTest(DataSources.Lodge).Contains("No Lodge") && AtlasView.SourceCardTextsForTest(DataSources.Cairn).Contains("Not received") && AtlasView.SourceCardTextsForTest(DataSources.Classic).Contains("Not on this PC") && AtlasView.CairnCardTextsForTest.Contains("No Lodge"),
                    string.Join(" | ", AtlasView.CairnCardTextsForTest));
                await ShotWait(400); if (SelfTestClock.ShotsOn) Snapshot(Path.Combine(dir, "datasources-empty.png"));
            }
            catch (Exception e) { Check("datasources: no exception", false, e.ToString()); }
            finally
            {
                try
                {
                    AtlasView.SourcesFactsOverrideForTest = null; AtlasView.OpenSharingSettings = savedOpen;
                    Width = savedW; Height = savedH;
                    AtlasQuery.Context.Lodge = savedLodge; AtlasQuery.Context.NowUtc = savedNow; CairnAtlas.SetForTest(savedCairn);
                    ClassicAtlas.Use(null); ItemData.ResetForTest(); SpellData.ResetForTest(); SeenData.ResetForTest(); MapData.ResetForTest(); AtlasBrowse.ForgetForTest();
                    MapRender.TileSourceForTest = savedTiles; MapRender.DirOverride = savedMapDir; MapRender.ForgetFailuresForTest();
                    ResetAtlasForTest();
                    ShowTab("addons");
                }
                catch { }
            }
            return string.Join("\r\n", notes) + "\r\ndatasources: " + (ok ? "ok" : "FAILED");
        }
    }
}
