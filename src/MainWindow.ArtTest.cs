using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Maps;
using ElansAddonHub.Services.Talents;

namespace ElansAddonHub
{
    // --selftest "art": the painted art (ArtLibrary, src\Assets\Art). Every picture loads from the exe's resources; the window background; the zone banner
    // on Durotar (1411) and Teldrassil (1438) and on no other page; the Atlas Home cards with art (Dungeons and raids, Professions) and only those; the
    // Characters > Talents trees: the spec names from the game's talent data ("Tree N" without it), the hunter's spec art behind its three trees and none
    // behind another class', and the columns and icons growing with the window. Fixtures only (a synthetic map index, talent set and ElansHub.lua under the
    // scratch folder); pictures with ELANSHUB_SHOTS=1.
    public partial class MainWindow
    {
        static TalentSet ArtTestTalents()
        {
            TalentTreeDef Tree(int id, int classId, string name, string file, params string[] specs)
            {
                var t = new TalentTreeDef { Id = id, ClassId = classId, ClassName = name, ClassFile = file, SpecNames = specs };
                // the ids of the Characters fixture (HubCharsLua): 1-9 the first tree, 10-18 the second, 19-27 the third
                for (int n = 1; n <= 27; n++) t.Nodes.Add(new TalentNode { Id = n, Spec = (n - 1) / 9, Col = ((n - 1) % 9) / 3, Row = (n - 1) % 3 });
                t.Reindex();
                return t;
            }
            var set = new TalentSet();
            set.Trees.Add(Tree(1, 1, "Warrior", "WARRIOR", "Arms", "Fury", "Protection"));
            set.Trees.Add(Tree(2, 3, "Hunter", "HUNTER", "Beast Mastery", "Marksmanship", "Survival"));
            return set;
        }

        async Task<string> ArtTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            void Check(string what, bool cond, string detail = null) { SelfTestClock.Mark(what); notes.Add((cond ? "ok   " : "FAIL ") + what + (!cond && detail != null ? " [" + detail + "]" : "")); ok &= cond; }
            string P(string n) => Path.Combine(dir, n);
            var savedWidth = Width; var savedHeight = Height;
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var root = Path.Combine(dir, "artwow");
            try
            {
                // ---- the pictures themselves
                var all = ArtLibrary.All().ToList();
                var bad = all.Where(a => ArtLibrary.Load(a) == null).Select(a => a.File).ToList();
                Check("resources: all " + all.Count + " pictures load from the exe (background, 2 zone banners, 2 category cards, 3 hunter specs)", all.Count == 8 && bad.Count == 0, string.Join(", ", bad));
                var bg = ArtLibrary.Load(ArtLibrary.Background);
                Check("resources: the background is 2016 x 1152, a banner 2304 x 960, a spec picture decoded at 720 wide (no full-size bitmaps for small slots)",
                    bg != null && bg.PixelWidth == 2016 && bg.PixelHeight == 1152 && ArtLibrary.Load(ArtLibrary.Zone(1411))?.PixelWidth == 2304 && ArtLibrary.Load(ArtLibrary.Zone(1411))?.PixelHeight == 960 && ArtLibrary.Load(ArtLibrary.Spec("HUNTER", 0))?.PixelWidth == 720);
                Check("resources: a missing picture is null, never an exception (the slot stays plain)", ArtLibrary.Load(new ArtPiece("nope.jpg", 0.5, 0.5)) == null);
                Check("mapping: zones 1411 / 1438 have banners, others none; categories 'instances' / 'professions' have art; hunter trees 0-2 have art, a warrior's none",
                    ArtLibrary.Zone(1411) != null && ArtLibrary.Zone(1438) != null && ArtLibrary.Zone(11) == null && ArtLibrary.Zone(1429) == null && ArtLibrary.Category("instances") != null && ArtLibrary.Category("professions") != null
                    && ArtLibrary.Category("items") == null && ArtLibrary.Spec("HUNTER", 0) != null && ArtLibrary.Spec("hunter", 2) != null && ArtLibrary.Spec("HUNTER", 3) == null && ArtLibrary.Spec("WARRIOR", 0) == null && ArtLibrary.Spec("HUNTER", -1) == null);
                var wide = ArtLibrary.Crop(2.4, 6, 0.5, 0.66); var tall = ArtLibrary.Crop(0.62, 1.0, 0.5, 0.72); var edge = ArtLibrary.Crop(2.4, 6, 0.5, 0.98);
                Check("crop: a banner keeps the full width and slides down to its focal point; a spec picture keeps its lower part; the crop never leaves the picture",
                    Math.Abs(wide.Width - 1) < 1e-9 && Math.Abs(wide.Height - 0.4) < 1e-9 && Math.Abs(wide.Y - 0.46) < 1e-9 && Math.Abs(tall.Height - 0.62) < 1e-9 && Math.Abs(tall.Y - 0.38) < 1e-9 && Math.Abs(edge.Bottom - 1) < 1e-9,
                    wide + " / " + tall + " / " + edge);

                // ---- the window background
                Width = 1400; Height = 860;
                ShowTab("addons");
                await Task.Delay(200); UpdateLayout();
                Check("window: the background is the new painting, anchored right (the lodge stays in view), under the readability gradient",
                    WindowArt.ImageSource is System.Windows.Media.Imaging.BitmapSource ws && ws.PixelWidth == 2016 && WindowArt.AlignmentX == System.Windows.Media.AlignmentX.Right);
                await ShotWait(400);
                Snapshot(P("art-01-addons.png"));

                // ---- the Atlas: Home cards and zone banners on a synthetic world
                var maps = AtlasTestMapSet();
                maps.UiMaps.Add(new UiMapInfo { Id = 1411, Name = "Durotar", ParentId = 10, Type = UiMapInfo.Zone, Flags = 2 });
                maps.UiMaps.Add(new UiMapInfo { Id = 1438, Name = "Teldrassil", ParentId = 10, Type = UiMapInfo.Zone, Flags = 2 });
                MapData.SetForTest(new MapIndex(maps));
                ResetAtlasForTest();
                ShowTab("atlas");
                await AtlasView.GoStart(); await AtlasView.StartFillForTest; await Task.Delay(200); UpdateLayout();
                var cards = AtlasView.ArtCardsForTest;
                var visible = AtlasCategories.Visible.Select(c => c.Id).ToList();
                var painted = AtlasWalk(AtlasView).OfType<FrameworkElement>().Count(e => e.Tag as string == "card-art");
                Check("Atlas Home: the Dungeons and raids card carries its art" + (visible.Contains("professions") ? ", so does Professions" : ""),
                    cards.ContainsKey("instances") && (!visible.Contains("professions") || cards.ContainsKey("professions")), string.Join(",", cards.Keys) + " of " + string.Join(",", visible));
                Check("Atlas Home: only the cards with art are painted (no other category card has a picture)", painted == cards.Count && cards.Keys.All(k => ArtLibrary.Category(k) != null), painted + " painted, " + cards.Count + " art cards");
                var instCard = cards.TryGetValue("instances", out var ic) ? ic : null;
                Check("Atlas Home: a painted card keeps its title and count on top of the veil, and opens its category like the others",
                    instCard != null && AtlasTexts(instCard).Contains("Dungeons and raids") && instCard.Tag as string == AtlasCategories.Find("instances")?.OpenId, instCard == null ? "none" : string.Join("|", AtlasTexts(instCard)));
                await ShotWait(400);
                Snapshot(P("art-02-atlas-home.png"));

                foreach (var z in new[] { 1411, 1438 })
                {
                    await AtlasView.Open("zone:" + z); await AtlasView.WaitForTest(); await Task.Delay(200); UpdateLayout();
                    var b = AtlasView.BannerForTest;
                    Check("zone " + z + ": the painting runs behind the name card (taller than the " + ElansAddonHub.AtlasPage.BannerHeight + " banner part), the full content width",
                        b != null && b.IsVisible && b.ActualHeight > ElansAddonHub.AtlasPage.BannerHeight && Math.Abs(b.ActualWidth - AtlasView.BodyForTest.ActualWidth) < 1.5
                        && AtlasView.HeroForTest != null && AtlasView.HeroForTest.TranslatePoint(new Point(0, 0), b).Y >= ElansAddonHub.AtlasPage.BannerHeight - 49
                        && AtlasView.HeroForTest.TranslatePoint(new Point(0, AtlasView.HeroForTest.ActualHeight), b).Y <= b.ActualHeight + 1,
                        b == null ? "none" : b.ActualWidth + "x" + b.ActualHeight + " body " + AtlasView.BodyForTest.ActualWidth);
                    await ShotWait(500);
                    Snapshot(P("art-03-zone-" + z + ".png"));
                }
                await AtlasView.Open("zone:11"); await AtlasView.WaitForTest(); UpdateLayout();
                Check("zone 11 (no art): no banner, the page starts with its name card as before", AtlasView.BannerForTest == null && AtlasView.PageTitleForTest == "Greenvale");
                await AtlasView.Open("npc:1"); await AtlasView.WaitForTest(); UpdateLayout();
                Check("a page of another kind: no banner", AtlasView.BannerForTest == null);
                MapData.ResetForTest();

                // ---- Characters > Talents
                if (Directory.Exists(root)) Util.DeleteDir(root);
                Check("fixture: the WoW folder of this test is under the scratch folder (never the real game)", Path.GetFullPath(root).StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase));
                var sv = Path.Combine(root, "_classic_beta_", "WTF", "Account", "ART", "SavedVariables");
                Directory.CreateDirectory(sv);
                Directory.CreateDirectory(Path.Combine(root, "_classic_beta_", "Interface", "AddOns"));
                var hubFile = Path.Combine(sv, "ElansHub.lua");
                File.WriteAllText(hubFile, HubCharsLua(now, now - 60), new UTF8Encoding(false));
                TalentData.SetForTest(ArtTestTalents());
                HubData.ResetCache();
                InventoryView.Init(() => root);
                TabInventory.IsChecked = true;
                for (int i = 0; i < 40 && InventoryView.CharCountForTest < 6; i++) await Task.Delay(200);
                InventoryView.FilterForTest("Forever-Elan");
                InventoryView.SelectTabForTest("talents");
                await Task.Delay(200); UpdateLayout();
                var tl = InventoryView.CharTabLinesForTest();
                Check("hunter talents: the tree headers are the spec names from the game's talent data", new[] { "treehead|1|Beast Mastery|0", "treehead|2|Marksmanship|1", "treehead|3|Survival|2" }.All(tl.Contains), string.Join(" | ", tl.Where(x => x.StartsWith("tree"))));
                Check("hunter talents: each tree has its spec's art behind it (BM, MM, SV) under the veil", new[] { "treeart|1|talent-hunter-bm.jpg", "treeart|2|talent-hunter-mm.jpg", "treeart|3|talent-hunter-sv.jpg" }.All(tl.Contains)
                    && InventoryView.TalentCardsForTest.All(c => c.Tag as string == "tree-painted"), string.Join(" | ", tl.Where(x => x.StartsWith("tree"))));
                Check("hunter talents: the trees and nodes are as before (three 3x3 trees, 29 nodes with the loose ones)", tl.Count(x => x.StartsWith("tree|")) == 3 && tl.Contains("loose|2") && InventoryView.TalentNodesForTest == 29);
                var cardsT = InventoryView.TalentCardsForTest;
                double colA = cardsT[0].ActualWidth, gridA = InventoryView.TalentGridsForTest[0].ActualWidth;
                Check("layout at 1400: three equal columns side by side, at least " + InventoryPage.TreeMinWidth + " wide", InventoryView.TalentTreesSideBySideForTest && cardsT.All(c => Math.Abs(c.ActualWidth - colA) < 1) && colA >= InventoryPage.TreeMinWidth - 0.5, string.Join(",", cardsT.Select(c => c.ActualWidth)));
                await ShotWait(500);
                Snapshot(P("art-04-talents-hunter.png"));
                Width = 900; await Task.Delay(300); UpdateLayout();
                bool stacked = !InventoryView.TalentTreesSideBySideForTest && cardsT[1].TranslatePoint(new Point(0, 0), cardsT[0]).Y > cardsT[0].ActualHeight;
                Width = 1200; await Task.Delay(300); UpdateLayout();
                bool sideN = InventoryView.TalentTreesSideBySideForTest;
                double colN = cardsT[0].ActualWidth, gridN = InventoryView.TalentGridsForTest[0].ActualWidth;
                Width = 1800; await Task.Delay(300); UpdateLayout();
                double colW = cardsT[0].ActualWidth, gridW = InventoryView.TalentGridsForTest[0].ActualWidth;
                var natural = InventoryView.TalentGridNaturalWidthForTest(0);
                Check("layout: at 900 the trees go one under the other", stacked);
                Check("layout: the columns grow with the window (1200 < 1400 < 1800) and stop at " + InventoryPage.TreeMaxWidth, sideN && colN < colA && colA < colW && colW <= InventoryPage.TreeMaxWidth + 0.5 && InventoryView.TalentTreesSideBySideForTest, colN + " / " + colA + " / " + colW);
                Check("layout: the icons grow with the column (32 px at the narrowest, 44 px at the widest; the grid keeps its shape)", gridN < gridA - 0.5 && gridA <= gridW + 0.5 && gridN >= natural * 32.0 / 38 - 1 && gridW <= natural * 44.0 / 38 + 1.5
                    && InventoryPage.TalentIconFor(InventoryPage.TreeMinWidth) == 32 && InventoryPage.TalentIconFor(InventoryPage.TreeMaxWidth) == 44 && InventoryPage.TalentIconFor(9999) == 44 && InventoryPage.TalentIconFor(0) == 32,
                    gridN + " / " + gridA + " / " + gridW + " natural " + natural + " cards " + colN + " / " + colA + " / " + colW + " max " + InventoryView.TalentGridsForTest[0].MaxWidth);
                Width = 1400; await Task.Delay(200); UpdateLayout();

                // without the talent data: "Tree N", the art still follows the order of the three trees
                TalentData.SetForTest(null, TalentData.State.Unavailable, "test");
                InventoryView.SelectTabForTest("overview"); InventoryView.SelectTabForTest("talents");
                tl = InventoryView.CharTabLinesForTest();
                Check("hunter talents without the game's talent data: 'Tree 1..3' as before, the art still by tree order", new[] { "treehead|1|Tree 1|0", "treehead|3|Tree 3|2" }.All(tl.Contains) && tl.Contains("treeart|1|talent-hunter-bm.jpg"), string.Join(" | ", tl.Where(x => x.StartsWith("tree"))));
                TalentData.SetForTest(ArtTestTalents());

                // the same trees on a warrior: Arms / Fury / Protection and no art
                File.WriteAllText(hubFile, HubCharsLua(now, now - 60).Replace("class='Hunter',classFile='HUNTER'", "class='Warrior',classFile='WARRIOR'"), new UTF8Encoding(false));
                File.SetLastWriteTimeUtc(hubFile, DateTime.UtcNow.AddSeconds(5));
                for (int i = 0; i < 40; i++)
                {
                    await Task.Delay(200);
                    InventoryView.FilterForTest("Forever-Elan"); InventoryView.SelectTabForTest("talents");
                    if (InventoryView.CharTabLinesForTest().Contains("treehead|1|Arms|0")) break;
                }
                UpdateLayout();
                tl = InventoryView.CharTabLinesForTest();
                Check("warrior talents: headers Arms / Fury / Protection, no art behind any tree (plain panels)", new[] { "treehead|1|Arms|0", "treehead|2|Fury|1", "treehead|3|Protection|2" }.All(tl.Contains) && !tl.Any(x => x.StartsWith("treeart|"))
                    && InventoryView.TalentCardsForTest.All(c => c.Tag as string == "tree-plain"), string.Join(" | ", tl.Where(x => x.StartsWith("tree"))));
                await ShotWait(500);
                Snapshot(P("art-05-talents-warrior.png"));
            }
            catch (Exception ex) { ok = false; notes.Add("FAIL exception: " + ex); }
            finally
            {
                try
                {
                    TalentData.ResetForTest(); MapData.ResetForTest();
                    HubData.ResetCache();
                    InventoryView.ResetViewForTest();
                    InventoryView.Init(() => settings.WowRoot);
                    ResetAtlasForTest();
                    Width = savedWidth; Height = savedHeight;
                    ShowTab("addons");
                }
                catch { }
            }
            notes.Insert(0, "art (2.67.0): " + (ok ? "all ok" : "FAILURES"));
            return string.Join("\r\n", notes);
        }
    }
}
