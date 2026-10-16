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
        static double Lum(System.Windows.Media.Color c) => 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;

        // the page panels (the Border that holds each page's content; Brush.Panel since 2.70), from the pages' own trees
        IEnumerable<Border> PagePanels()
        {
            var panel = FindResource("Brush.Panel");
            foreach (FrameworkElement page in new FrameworkElement[] { AtlasView, InventoryView, BrowserView, TalentsView, SettingsPage, LodgePage })
                foreach (var b in LogicalWalk(page).OfType<Border>().Where(b => ReferenceEquals(b.Background, panel))) yield return b;
        }

        // the logical tree (a collapsed page has no visual tree until it is first shown)
        static IEnumerable<DependencyObject> LogicalWalk(DependencyObject d)
        {
            if (d == null) yield break;
            yield return d;
            foreach (var c in LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>())
                foreach (var x in LogicalWalk(c)) yield return x;
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
                // nothing lost (51 zone banners, 13 category banners since 2.71, 27 talent trees; the window background and the Home card art are gone) and every one loads; more art is fine
                Check("resources: all " + all.Count + " pictures load from the exe (at least 51 zone banners, 13 category banners, 27 talent trees), no window wallpaper and no Home card art (card-*.jpg) is listed or built in",
                    all.Count >= 91 && !all.Any(a => a.File.StartsWith("card-")) && ArtLibrary.Load(new ArtPiece("card-items.jpg", 0.5, 0.5)) == null && bad.Count == 0 && !all.Any(a => a.File == "background.jpg") && ArtLibrary.Load(new ArtPiece("background.jpg", 0.5, 0.5)) == null, string.Join(", ", bad));
                var banner = ArtLibrary.Load(ArtLibrary.Zone(1411)); var specPic = ArtLibrary.Load(ArtLibrary.Spec("HUNTER", 0));
                var catBanners = ArtLibrary.Banners.Values.Select(ArtLibrary.Load).ToList();
                // shapes, not the files' pixel counts: a repainted picture of another size must not fail this
                Check("resources: a zone banner and every category banner is 4:1, a spec picture is decoded at no more than 720 wide (no full-size bitmaps for small slots)",
                    banner != null && Math.Abs((double)banner.PixelWidth / banner.PixelHeight - 4) < 0.08 && catBanners.All(b => b != null && Math.Abs((double)b.PixelWidth / b.PixelHeight - 4) < 0.08) && specPic != null && specPic.PixelWidth <= 720 && specPic.PixelWidth >= 360,
                    "banner " + banner?.PixelWidth + "x" + banner?.PixelHeight + " spec " + specPic?.PixelWidth);
                Check("resources: a missing picture is null, never an exception (the slot stays plain)", ArtLibrary.Load(new ArtPiece("nope.jpg", 0.5, 0.5)) == null);
                var listCats = AtlasCategories.All.Where(c => !c.Hosted).Select(c => c.Id).ToList();
                Check("mapping: every game zone has a banner (2665 shares 2521's), zone 11 none; every list category (" + listCats.Count + ") and the Lodge's finds have a browse banner, the hosted pages (no header) none; every class has three trees of art, no fourth",
                    ArtLibrary.Zone(1411) != null && ArtLibrary.Zone(1438) != null && ArtLibrary.Zone(1429) != null && ArtLibrary.Zone(11) == null && ArtLibrary.Zone(2665)?.File == ArtLibrary.Zone(2521)?.File
                    && listCats.Count >= 12 && listCats.All(c => ArtLibrary.Banner(c) != null) && ArtLibrary.Banner("finds") != null && ArtLibrary.Banner("talents") == null && ArtLibrary.Banner("worldmap") == null && ArtLibrary.Banner(null) == null && ArtLibrary.Spec("HUNTER", 0) != null && ArtLibrary.Spec("hunter", 2) != null && ArtLibrary.Spec("HUNTER", 3) == null && ArtLibrary.Spec("WARRIOR", 0) != null && ArtLibrary.Spec("DRUID", 2) != null && ArtLibrary.Spec("HUNTER", -1) == null);
                var wide = ArtLibrary.Crop(2.4, 6, 0.5, 0.66); var tall = ArtLibrary.Crop(0.62, 1.0, 0.5, 0.72); var edge = ArtLibrary.Crop(2.4, 6, 0.5, 0.98);
                Check("crop: a banner keeps the full width and slides down to its focal point; a spec picture keeps its lower part; the crop never leaves the picture",
                    Math.Abs(wide.Width - 1) < 1e-9 && Math.Abs(wide.Height - 0.4) < 1e-9 && Math.Abs(wide.Y - 0.46) < 1e-9 && Math.Abs(tall.Height - 0.62) < 1e-9 && Math.Abs(tall.Y - 0.38) < 1e-9 && Math.Abs(edge.Bottom - 1) < 1e-9,
                    wide + " / " + tall + " / " + edge);

                // ---- the window background
                Width = 1400; Height = 860;
                ShowTab("addons");
                await Task.Delay(200); UpdateLayout();
                var winGrad = FindResource("Brush.Window") as System.Windows.Media.LinearGradientBrush;
                var chrome = AtlasWalk(WindowFrame).OfType<Border>().ToList();
                var pageHosts = new FrameworkElement[] { AtlasView, InventoryView, BrowserView, TalentsView, SettingsPage, LodgePage };
                Check("window: no wallpaper - the frame is the Brush.Window gradient (lighter at the top, deeper at the bottom), no picture brush in the window chrome outside the pages",
                    winGrad != null && ReferenceEquals(WindowFrame.Background, winGrad) && winGrad.GradientStops.Count >= 2 && Lum(winGrad.GradientStops[0].Color) > Lum(winGrad.GradientStops.Last().Color)
                    && !chrome.Any(b => b.Background is System.Windows.Media.ImageBrush && !pageHosts.Any(p => b.IsDescendantOf(p))));
                var panels = PagePanels().ToList();
                Check("window: the rail and every page panel are solid (alpha 255), the panel lighter than the window's bottom so it reads as a surface (" + panels.Count + " panels)",
                    FindResource("Brush.Rail") is System.Windows.Media.SolidColorBrush rb && rb.Color.A == 255 && FindResource("Brush.Panel") is System.Windows.Media.SolidColorBrush pb && pb.Color.A == 255
                    && winGrad != null && Lum(pb.Color) > Lum(winGrad.GradientStops.Last().Color) && panels.Count >= 6 && panels.All(p => p.Background is System.Windows.Media.SolidColorBrush s && s.Color.A == 255),
                    string.Join(",", panels.Select(p => (p.Background as System.Windows.Media.SolidColorBrush)?.Color.ToString() ?? "?")));
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
                var cards = AtlasView.CategoryTilesForTest;
                var visible = AtlasCategories.Visible.Select(c => c.Id).ToList();
                string IconOf(DependencyObject d) => AtlasWalk(d).OfType<Viewbox>().Select(v => v.Tag as string).FirstOrDefault(t => t != null && t.StartsWith("atlas-icon|"));
                Check("Atlas Home: a tile per visible category (" + visible.Count + "), each with its own line icon (atlas-icon|<id>)",
                    visible.All(cards.ContainsKey) && cards.All(kv => IconOf(kv.Value) == "atlas-icon|" + kv.Key), string.Join(",", cards.Select(kv => kv.Key + "=" + IconOf(kv.Value))) + " of " + string.Join(",", visible));
                Check("Atlas Home: no painted art on the tiles (no card-art layer, no picture brush anywhere on a tile)",
                    !AtlasWalk(AtlasView).OfType<FrameworkElement>().Any(e => e.Tag as string == "card-art") && cards.Values.All(t => !AtlasWalk(t).OfType<Border>().Any(b => b.Background is System.Windows.Media.ImageBrush)));
                var instCard = cards.TryGetValue("instances", out var ic) ? ic : null;
                Check("Atlas Home: a tile keeps its title and count, and opens its category",
                    instCard != null && AtlasTexts(instCard).Contains("Dungeons and raids") && instCard.Tag as string == AtlasCategories.Find("instances")?.OpenId, instCard == null ? "none" : string.Join("|", AtlasTexts(instCard)));
                if (cards.TryGetValue("skills", out var skillTile))
                {
                    var tt = AtlasWalk(skillTile).OfType<TextBlock>().FirstOrDefault(t => t.Text == "Skills");
                    Check("Atlas Home: a tile's title wraps instead of being cut off (Skills)", tt != null && tt.TextWrapping == TextWrapping.Wrap && tt.TextTrimming == TextTrimming.None);
                }
                var entries = AtlasNav.Entries();
                Check("Atlas side navigation: every category row draws its line icon (" + entries.Count + " rows)", entries.All(e => AtlasIcons.Has(e.Icon)) && entries.Count(e => AtlasWalk(AtlasView).OfType<ContentControl>().Any(r => r.Tag as string == e.Key + "|off" && IconOf(r) == "atlas-icon|" + e.Icon)) == entries.Count,
                    string.Join(",", entries.Select(e => e.Key + ":" + e.Icon)));
                Check("icons: every category (lists and hosted pages) and the Lodge's finds has an icon; the same drawing for the world map's two ids; an unknown id has none; every drawing stays inside its 24 x 24 grid",
                    AtlasCategories.All.All(c => AtlasIcons.Has(c.Id)) && AtlasIcons.Has("finds") && AtlasIcons.Has("worldmap") && ReferenceEquals(AtlasIcons.For("map"), AtlasIcons.For("worldmap")) && !AtlasIcons.Has("nope") && !AtlasIcons.Has(null)
                    && AtlasIcons.Keys.Count() >= 16 && AtlasIcons.Keys.All(k => AtlasIcons.For(k) is System.Windows.Media.Geometry g && g.IsFrozen && !g.Bounds.IsEmpty && g.Bounds.Left >= 0 && g.Bounds.Top >= 0 && g.Bounds.Right <= 24 && g.Bounds.Bottom <= 24),
                    string.Join(",", AtlasCategories.All.Where(c => !AtlasIcons.Has(c.Id)).Select(c => c.Id)));
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
                // the category browse pages the synthetic world can list (zones, dungeons): the category's banner behind the header, like a zone's
                foreach (var cat in new[] { "zones", "instances" })
                {
                    await AtlasView.Open("browse:" + cat); await AtlasView.WaitForTest(); await Task.Delay(200); UpdateLayout();
                    var b = AtlasView.BannerForTest;
                    Check("browse:" + cat + ": its banner runs behind the header card (taller than the " + ElansAddonHub.AtlasPage.BannerHeight + " banner part), the full content width, first on the page, with the category's icon",
                        AtlasView.IsBrowse && b != null && b.Tag as string == "browse-banner|" + cat && b.IsVisible && b.ActualHeight > ElansAddonHub.AtlasPage.BannerHeight && Math.Abs(b.ActualWidth - AtlasView.BodyForTest.ActualWidth) < 1.5
                        && b.TranslatePoint(new Point(0, 0), AtlasView.BodyForTest).Y < 1 && IconOf(b) == "atlas-icon|" + cat, b == null ? "none" : b.Tag + " " + b.ActualWidth + "x" + b.ActualHeight);
                    if (cat == "zones") { await ShotWait(500); Snapshot(P("art-03-browse-zones.png")); }
                }
                MapData.ResetForTest();

                // ---- Characters > Talents (full-size trees: the shape of the game's Hunter and Warrior data, a 51-point build each)
                if (Directory.Exists(root)) Util.DeleteDir(root);
                Check("fixture: the WoW folder of this test is under the scratch folder (never the real game)", Path.GetFullPath(root).StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase));
                var sv = Path.Combine(root, "_classic_beta_", "WTF", "Account", "ART", "SavedVariables");
                Directory.CreateDirectory(sv);
                Directory.CreateDirectory(Path.Combine(root, "_classic_beta_", "Interface", "AddOns"));
                var hubFile = Path.Combine(sv, "ElansHub.lua");
                var full = FullTalents();
                var hunterDef = full.ForClassFile("HUNTER"); var warriorDef = full.ForClassFile("WARRIOR");
                var hunterLua = HubCharsLua(now, now - 60, "60", FullHubTalents(hunterDef, FullHunterBuild, now - 300));
                File.WriteAllText(hubFile, hunterLua, new UTF8Encoding(false));
                if (SelfTestClock.ShotsOn) CopyShotIcons(hunterDef, warriorDef);
                TalentData.SetForTest(full);
                HubData.ResetCache();
                InventoryView.Init(() => root);
                TabInventory.IsChecked = true;
                for (int i = 0; i < 40 && InventoryView.CharCountForTest < 6; i++) await Task.Delay(200);
                InventoryView.FilterForTest("Forever-Elan");
                InventoryView.SelectTabForTest("talents");
                await Task.Delay(200); UpdateLayout();
                var tl = InventoryView.CharTabLinesForTest();
                Check("hunter talents: the tree headers are the spec names from the game's talent data", new[] { "treehead|1|Beast Mastery|0", "treehead|2|Marksmanship|1", "treehead|3|Survival|2" }.All(tl.Contains), string.Join(" | ", tl.Where(x => x.StartsWith("tree"))));
                Check("hunter talents: each tree has its spec's art behind it (BM, MM, SV) under the veil", new[] { "treeart|1|talent-hunter-bm.jpg", "treeart|2|talent-hunter-1.jpg", "treeart|3|talent-hunter-sv.jpg" }.All(tl.Contains)
                    && InventoryView.TalentCardsForTest.All(c => c.Tag as string == "tree-painted"), string.Join(" | ", tl.Where(x => x.StartsWith("tree"))));
                Check("hunter talents: placed from the game data, 51 spent, three 4 x 7 trees (16 / 17 / 18 talents) with 31 / 20 / 0 points, no talent apart (the hidden Lightning Reflexes copy 104982 counts as 110859, Improved Serpent Sting's y 39300 does not matter)",
                    tl.Contains("talentsource|game data|Hunter") && tl.Contains("talentinfo|51|0|3|0") && tl.Contains("tree|1|4x7|31|16") && tl.Contains("tree|2|4x7|20|17") && tl.Contains("tree|3|4x7|0|18") && !tl.Any(x => x.StartsWith("loose|")),
                    string.Join(" | ", tl.Where(x => x.StartsWith("t") || x.StartsWith("loose"))));
                Check("hunter talents: headers 'Beast Mastery 31 / 51', 'Marksmanship 20 / 51', 'Survival 0 / 51' with the spec icons of the game data",
                    new[] { "treepoints|1|31 / 51", "treepoints|2|20 / 51", "treepoints|3|0 / 51" }.All(tl.Contains) && InventoryView.TalentPanelsForTest.Select(p => p.Model.HeaderText).SequenceEqual(new[] { "Beast Mastery 31 / 51", "Marksmanship 20 / 51", "Survival 0 / 51" })
                    && InventoryView.TalentPanelsForTest.Select(p => p.Model.IconId).SequenceEqual(new[] { 132164, 132222, 132215 }) && InventoryView.TalentPanelsForTest.All(p => p.SpecIcon.Visibility == Visibility.Visible));
                Check("hunter arrows: one per prerequisite pair (the pair written both ways is one arrow, pointing down), lit when the prerequisite is full",
                    new[] { "arrow|1|104964>104961|lit", "arrow|1|104967>104962|lit", "arrow|1|104970>104964|lit", "arrow|2|105004>104997|dim", "arrow|2|105005>104999|lit", "arrow|2|105008>105002|lit", "arrow|3|104985>104984|dim", "arrow|3|104986>104989|dim" }.All(tl.Contains)
                    && tl.Count(x => x.StartsWith("arrow|")) == 8, string.Join(" | ", tl.Where(x => x.StartsWith("arrow"))));
                Check("hunter grid: every talent sits on its game cell, the 4 columns spread evenly over the panel, the arrows drawn from under the prerequisite to above the talent",
                    GridMatches(InventoryView.TalentPanelsForTest, hunterDef, out var why), why);
                Check("hunter talents: ranks, dimming (rank 0 behind a closed gate dimmed, rank 0 that could be taken not), the highest talent of BM lit", RanksMatch(InventoryView.TalentPanelsForTest, hunterDef, FullHunterBuild, out var why2), why2);
                var cardsT = InventoryView.TalentCardsForTest;
                double colA = cardsT[0].ActualWidth, iconA = InventoryView.TalentPanelsForTest[0].Grid.IconSize;
                Check("layout at 1400: three equal columns side by side, at least " + InventoryPage.TreeMinWidth + " wide", InventoryView.TalentTreesSideBySideForTest && cardsT.All(c => Math.Abs(c.ActualWidth - colA) < 1) && colA >= InventoryPage.TreeMinWidth - 0.5, string.Join(",", cardsT.Select(c => c.ActualWidth)));
                await ShotWait(800);
                Snapshot(P("art-04-talents-hunter.png"));
                Width = 900; await Task.Delay(300); UpdateLayout();
                bool stacked = !InventoryView.TalentTreesSideBySideForTest && cardsT[1].TranslatePoint(new Point(0, 0), cardsT[0]).Y > cardsT[0].ActualHeight;
                Width = 1200; await Task.Delay(300); UpdateLayout();
                bool sideN = InventoryView.TalentTreesSideBySideForTest;
                double colN = cardsT[0].ActualWidth, iconN = InventoryView.TalentPanelsForTest[0].Grid.IconSize;
                Width = 1800; await Task.Delay(300); UpdateLayout();
                double colW = cardsT[0].ActualWidth, iconW = InventoryView.TalentPanelsForTest[0].Grid.IconSize;
                bool gridW = GridMatches(InventoryView.TalentPanelsForTest, hunterDef, out var whyW);
                Check("layout: at 900 the trees go one under the other", stacked);
                Check("layout: the columns grow with the window (1200 < 1400 < 1800) and stop at " + InventoryPage.TreeMaxWidth, sideN && colN < colA && colA < colW && colW <= InventoryPage.TreeMaxWidth + 0.5 && InventoryView.TalentTreesSideBySideForTest, colN + " / " + colA + " / " + colW);
                Check("layout: the icons follow the panel (36 to 48 px, never smaller at a wider panel) and the grid still fills the panel at 1800",
                    iconN >= TalentGridPanel.IconMin && iconW <= TalentGridPanel.IconMax && iconN <= iconA && iconA <= iconW && iconW > iconN && gridW
                    && TalentGridPanel.IconFor(10) == 36 && TalentGridPanel.IconFor(1000) == 48, iconN + " / " + iconA + " / " + iconW + " " + whyW);
                Width = 1400; await Task.Delay(200); UpdateLayout();

                // without the talent data: "Tree N", the Hub's positions (the copy left out, the extra-digit position placed), the art still by tree order
                TalentData.SetForTest(null, TalentData.State.Unavailable, "test");
                InventoryView.SelectTabForTest("overview"); InventoryView.SelectTabForTest("talents");
                tl = InventoryView.CharTabLinesForTest();
                Check("hunter talents without the game's talent data: 'Tree 1..3', '31 points', the art still by tree order, the copy left out and nothing apart",
                    new[] { "treehead|1|Tree 1|0", "treehead|3|Tree 3|2", "treepoints|1|31 points", "talentsource|positions", "copies|104982", "talentinfo|51|0|3|0" }.All(tl.Contains) && tl.Contains("treeart|1|talent-hunter-bm.jpg") && !tl.Any(x => x.StartsWith("loose|"))
                    && tl.Count(x => x.StartsWith("tree|") && x.Contains("|4x7|")) == 3, string.Join(" | ", tl.Where(x => x.StartsWith("t") || x.StartsWith("c") || x.StartsWith("loose"))));
                TalentData.SetForTest(full);

                // a talent with ranks the class' trees do not have: listed apart, with what it is
                File.WriteAllText(hubFile, HubCharsLua(now, now - 60, "60", FullHubTalents(hunterDef, FullHunterBuild, now - 300).Replace("nodes={\n", "nodes={\n[999]={id=424242,r=2,m=3,x=1020,y=2130,s=987654,n=\"Forgotten Talent\",ic=0},\n")), new UTF8Encoding(false));
                File.SetLastWriteTimeUtc(hubFile, DateTime.UtcNow.AddSeconds(2));
                for (int i = 0; i < 40; i++)
                {
                    await Task.Delay(200);
                    InventoryView.FilterForTest("Forever-Elan"); InventoryView.SelectTabForTest("talents");
                    if (InventoryView.CharTabLinesForTest().Any(x => x.StartsWith("loose|"))) break;
                }
                tl = InventoryView.CharTabLinesForTest();
                Check("a talent with ranks that is in none of the class' trees is listed apart under 'Not in the Hunter talent trees' (the trees stay as they are)",
                    tl.Contains("loose|1|Not in the Hunter talent trees") && tl.Contains("tree|1|4x7|31|16"), string.Join(" | ", tl.Where(x => x.StartsWith("t") || x.StartsWith("loose"))));

                // the same build idea on a warrior: Arms / Fury / Protection, no art
                File.WriteAllText(hubFile, HubCharsLua(now, now - 60, "60", FullHubTalents(warriorDef, FullWarriorBuild, now - 300)).Replace("class='Hunter',classFile='HUNTER'", "class='Warrior',classFile='WARRIOR'"), new UTF8Encoding(false));
                File.SetLastWriteTimeUtc(hubFile, DateTime.UtcNow.AddSeconds(5));
                for (int i = 0; i < 40; i++)
                {
                    await Task.Delay(200);
                    InventoryView.FilterForTest("Forever-Elan"); InventoryView.SelectTabForTest("talents");
                    if (InventoryView.CharTabLinesForTest().Contains("treehead|1|Arms|0")) break;
                }
                UpdateLayout();
                tl = InventoryView.CharTabLinesForTest();
                Check("warrior talents: headers Arms / Fury / Protection with 31 / 20 / 0 of 51, each tree with the warrior's own art, nothing apart",
                    new[] { "treehead|1|Arms|0", "treehead|2|Fury|1", "treehead|3|Protection|2", "treepoints|1|31 / 51", "treepoints|2|20 / 51", "treepoints|3|0 / 51", "talentinfo|51|0|3|0", "treeart|1|talent-warrior-0.jpg", "treeart|2|talent-warrior-1.jpg", "treeart|3|talent-warrior-2.jpg" }.All(tl.Contains)
                    && InventoryView.TalentCardsForTest.All(c => c.Tag as string == "tree-painted"), string.Join(" | ", tl.Where(x => x.StartsWith("t"))));
                Check("warrior arrows: 8, lit where the prerequisite is full (Arms' four), dim otherwise",
                    new[] { "arrow|1|105945>105941|lit", "arrow|1|105950>105947|lit", "arrow|1|105956>105950|lit", "arrow|1|105954>105951|lit", "arrow|2|105927>105928|dim", "arrow|2|105931>113569|dim", "arrow|3|105965>105959|dim", "arrow|3|105976>105971|dim" }.All(tl.Contains)
                    && tl.Count(x => x.StartsWith("arrow|")) == 8, string.Join(" | ", tl.Where(x => x.StartsWith("arrow"))));
                Check("warrior grid: every talent on its game cell, evenly spread", GridMatches(InventoryView.TalentPanelsForTest, warriorDef, out var why3), why3);
                await ShotWait(800);
                Snapshot(P("art-05-talents-warrior.png"));

                // the planner (Atlas > Talents) on the same data: the same renderer, the hunter's spec art behind its trees, a character's build loaded
                var hunterChar = HubData.Parse(hunterLua).Char("Forever-Elan");
                ShowTab("talents"); await Task.Delay(300);
                var page = TalentsView;
                page.SelectClass("HUNTER"); await Task.Delay(200); UpdateLayout();
                Check("planner: one panel per hunter tree with the spec art behind it (same treatment as Characters)", page.PanelsForTest.Count == 3 && page.PanelsForTest.All(p => p.Painted && p.Tag as string == "tree-painted")
                    && page.InfoForTest.Contains("treeart|0|talent-hunter-bm.jpg") && page.InfoForTest.Contains("treeart|2|talent-hunter-sv.jpg"), string.Join(" | ", page.InfoForTest.Where(x => x.StartsWith("treeart"))));
                Check("planner: 'Load from my character' lays the 51 points on the trees, headers 31 / 51 and 20 / 51, the BM arrows lit",
                    page.UseCharacter(new InvChar { Key = "Forever-Elan", Name = "Elan", Realm = "Forever", Class = "HUNTER", Level = 60, Hub = hunterChar }) && page.SessionForTest.State.Total == 51
                    && page.PanelsForTest[0].Model.HeaderText == "Beast Mastery 31 / 51" && page.PanelsForTest[1].Model.HeaderText == "Marksmanship 20 / 51" && page.PanelsForTest[0].Model.Arrows.All(a => a.Lit),
                    page.StatusForTest + " " + string.Join(", ", page.PanelsForTest.Select(p => p.Model.HeaderText)));
                UpdateLayout();
                Check("planner grid: every talent on its game cell, evenly spread", GridMatches(page.PanelsForTest, hunterDef, out var why4), why4);
                page.ScrollTopForTest();
                await ShotWait(800);
                Snapshot(P("art-06-talents-planner.png"));
                ShowTab("addons");
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
            notes.Insert(0, "art (2.71.0): " + (ok ? "all ok" : "FAILURES"));
            return string.Join("\r\n", notes);
        }
    }
}
