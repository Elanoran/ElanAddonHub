using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Atlas.Classic;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Maps;

namespace ElansAddonHub
{
    // --selftest "zonelore": the Outpost's zone notes (ZoneLore, Assets\Data\zonelore.json) and the "About <zone>" card on a zone page's Overview.
    // The shipped notes themselves (every entry readable, aliases, link rules), the spawn check on the invented world (here / capital city /
    // cannot tell / elsewhere), the card on Durotar, Elwynn Forest and Zephras Isle (alias 2665 shows 2521's), a zone without notes unchanged, the
    // links (creatures to their pages, races to theirs, sources to the browser), and the race page's start from the notes. Fixtures only (the
    // invented map set with the real zone ids added); pictures with ELANSHUB_SHOTS=1. The real Classic baseline check is "zonelorereal".
    public partial class MainWindow
    {
        static MapSet ZoneLoreMapSet()
        {
            var maps = AtlasTestMapSet();
            foreach (var z in new[] { new { Id = 1411, Name = "Durotar" }, new { Id = 1429, Name = "Elwynn Forest" }, new { Id = 2521, Name = "Zephras Isle" }, new { Id = 2482, Name = "Mount Hyjal" }, new { Id = 1454, Name = "Orgrimmar" } })
                maps.UiMaps.Add(new UiMapInfo { Id = z.Id, Name = z.Name, ParentId = 10, Type = UiMapInfo.Zone, Flags = 2 });
            maps.UiMaps.Add(new UiMapInfo { Id = 2665, Name = "Zephras Isle", ParentId = 0, Type = UiMapInfo.Zone, Flags = 2, System = 1 });
            return maps;
        }

        static ClassSet ZoneLoreRaces()
        {
            var set = new ClassSet();
            set.Data.Races[2] = new RaceInfo { Id = 2, Name = "Orc", Playable = true, Bit = 1, Side = RaceInfo.Horde, StartingLevel = 1 };
            set.Data.Races[8] = new RaceInfo { Id = 8, Name = "Troll", Playable = false, Bit = 7, Side = RaceInfo.Horde };     // not playable: no link
            set.Data.Races[90] = new RaceInfo { Id = 90, Name = "High Order Skyborne", Playable = true, Bit = 40, Side = RaceInfo.Alliance, StartingLevel = 1 };
            return set;
        }

        async Task<string> ZoneLoreTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            void Check(string what, bool cond, string detail = null) { SelfTestClock.Mark(what); notes.Add((cond ? "ok   " : "FAIL ") + what + (!cond && detail != null ? " [" + detail + "]" : "")); ok &= cond; }
            string P(string n) => Path.Combine(dir, n);
            var work = Path.Combine(dir, "zonelore");
            var savedWidth = Width; var savedHeight = Height; var savedLink = AtlasView.OpenLinkOverride;
            var savedSrc = AtlasQuery.StartCharsSource; var savedFile = AtlasQuery.RaceStartsFile; var savedCaps = ZoneLoreCheck.Capitals;
            var savedTiles = MapRender.TileSourceForTest; var savedMapDir = MapRender.DirOverride;
            try
            {
                if (Directory.Exists(work)) Util.DeleteDir(work);
                Directory.CreateDirectory(work);
                ZoneLore.ResetForTest();

                // ============================================================================================================ the shipped notes
                var all = ZoneLore.All;
                Check("data: the notes ride in the exe and read (" + all.Count + " zones, no problem)", all.Count == 50 && ZoneLore.Problem == null, ZoneLore.Problem);
                Check("data: every zone has its name, a summary, a facts line, a history and at least one source; every source is a web link",
                    all.All(e => e.Name.Length > 0 && e.Summary.Length > 0 && e.FactsLine.Length > 0 && e.History.Length > 0 && e.Sources.Count > 0 && e.Sources.All(s => s.StartsWith("https://") || s.StartsWith("http://"))),
                    string.Join(", ", all.Where(e => e.Summary.Length == 0 || e.Sources.Count == 0 || e.History.Length == 0).Select(e => e.ZoneId)));
                var mobs = all.SelectMany(e => e.Mobs).ToList();
                Check("data: every notable creature is classic, forever-seen or unverified; the first two have an id and link to their page, the unverified ones have none",
                    mobs.All(m => m.Source == "classic" || m.Source == "forever-seen" || m.Source == "unverified") && mobs.Where(m => m.Source != "unverified").All(m => m.Id > 0 && m.Link == "npc:" + m.Id)
                    && mobs.Where(m => m.Source == "unverified").All(m => m.Id == 0 && !m.Linked && m.Link == null) && mobs.Count(m => m.Source == "unverified") >= 1);
                Check("data: only creatures from the Classic baseline can be 'unchecked'", mobs.Where(m => m.Unchecked).All(m => m.Source == "classic"));
                Check("data: 2665 (the flight-map copy of Zephras Isle) is an alias of 2521 and shows its notes; 2521's notes do not send the reader to a 2665 entry",
                    ZoneLore.Resolve(2665) == 2521 && ZoneLore.For(2665) != null && ReferenceEquals(ZoneLore.For(2665), ZoneLore.For(2521)) && !all.Any(e => e.ZoneId == 2665) && !ZoneLore.For(2521).Forever.Contains("2665"));
                Check("data: a zone without notes has none (Greenvale 11)", ZoneLore.For(11) == null && ZoneLore.Resolve(11) == 11);
                var dur = ZoneLore.For(1411);
                Check("data: Durotar - 'Levels 1-10 · Horde', the Valley of Trials for the Orc and the Echo Isles for the Troll", dur != null && dur.FactsLine == "Levels 1-10 · Horde"
                    && dur.Starts.Any(s => s.Area == "Valley of Trials" && s.Races.SequenceEqual(new[] { "Orc" })) && dur.Starts.Any(s => s.Area == "Echo Isles" && s.Races.Contains("Troll")), dur?.FactsLine);
                Check("data: the facts line - a city says City, one level says Level, odd text stays as written",
                    new ZoneLoreEntry { Levels = "City", Faction = "Alliance" }.FactsLine == "City · Alliance" && new ZoneLoreEntry { Levels = "60", Faction = "Contested" }.FactsLine == "Level 60 · Contested"
                    && new ZoneLoreEntry { Levels = "35-50 (unconfirmed)", Faction = "" }.FactsLine == "Levels 35-50 (unconfirmed)" && new ZoneLoreEntry().FactsLine == "");
                Check("data: source names are the site (www. dropped), with the page when one site is cited twice; a non-web link is no source", ZoneLore.SourceName("https://www.wowhead.com/classic/npc=1") == "wowhead.com" && ZoneLore.SourceName("https://warcraft.wiki.gg/wiki/Durotar") == "warcraft.wiki.gg" && ZoneLore.SourceName("file:///c:/x") == "" && ZoneLore.SourceName("nope") == ""
                    && ZoneLore.SourceLabel("https://warcraft.wiki.gg/wiki/Zephras_Isle", ZoneLore.For(2521).Sources) == "warcraft.wiki.gg: Zephras Isle" && ZoneLore.SourceLabel("https://news.blizzard.com/x", ZoneLore.For(2521).Sources) == "news.blizzard.com");
                Check("data: damaged notes read as none (a problem, never an exception), an alias to nothing is dropped",
                    !ZoneLore.Parse("{oops", out _, out _, out var why1) && why1 != null && !ZoneLore.Parse("{\"zones\":[]}", out _, out _, out _)
                    && ZoneLore.Parse("{\"aliases\":{\"5\":7,\"6\":1},\"zones\":[{\"zoneId\":1,\"name\":\"A\",\"mobs\":[{\"name\":\"X\",\"id\":3,\"source\":\"classic\",\"unchecked\":true}]}]}", out var pz, out var pa, out _)
                    && pa.Count == 1 && pa[6] == 1 && pz[1].Mobs[0].Unchecked && pz[1].Mobs[0].Link == "npc:3");
                Check("data: the race starts in the notes - the Orc in Durotar, the Skyborne on Zephras Isle (by name, case does not matter)",
                    ZoneLore.StartsFor("orc").Select(k => k.Key.ZoneId).SequenceEqual(new[] { 1411 }) && ZoneLore.StartsFor("High Order Skyborne").Any(k => k.Key.ZoneId == 2521) && ZoneLore.StartsFor("Nobody", null).Count == 0);

                // ============================================================================================================ the spawn check (the invented world)
                MapData.SetForTest(new MapIndex(ZoneLoreMapSet()));
                var ci = AtlasTestClassic();
                ZoneLoreCheck.Capitals = new[] { 13 };      // Highkeep stands in for a capital
                ZoneLoreMob Mob(int id) => new ZoneLoreMob { Name = "m" + id, Id = id, Source = "classic" };
                var idx = MapData.Index;
                var here = ZoneLoreCheck.Check(11, Mob(1), ci, idx); var away = ZoneLoreCheck.Check(12, Mob(1), ci, idx); var city = ZoneLoreCheck.Check(11, Mob(2), ci, idx);
                var inst = ZoneLoreCheck.Check(11, Mob(4), ci, idx); var none = ZoneLoreCheck.Check(11, Mob(999), ci, idx);
                Check("check: a creature whose spawn point is on the zone's map is here (Gnarlfang in Greenvale); on another zone's map it is elsewhere, with where it is",
                    here.Verdict == ZoneLoreVerdict.Here && away.Verdict == ZoneLoreVerdict.Elsewhere && away.Detail.Contains("Greenvale"), here + " / " + away);
                Check("check: one standing in a capital city next to the zone is 'city' (kept, unchecked); one only inside an instance (Crypt Lord) or unknown to the baseline cannot be told",
                    city.Verdict == ZoneLoreVerdict.City && city.Detail.Contains("Highkeep") && inst.Verdict == ZoneLoreVerdict.NoData && none.Verdict == ZoneLoreVerdict.NoData && none.Detail.Contains("not in the Classic baseline"), city + " / " + inst + " / " + none);
                ZoneLoreCheck.Capitals = savedCaps;
                Check("check: only 'classic' creatures with an id are checked", ZoneLoreCheck.CheckAll(new[] { new ZoneLoreEntry { ZoneId = 11, Mobs = { Mob(1), new ZoneLoreMob { Name = "a", Id = 5, Source = "forever-seen" }, new ZoneLoreMob { Name = "b", Source = "unverified" } } } }, ci, idx).Count == 1);

                // ============================================================================================================ the race page
                ClassicAtlas.Use(null); SeenData.SetForTest(null, SeenData.State.NotStarted);
                ClassData.SetForTest(ZoneLoreRaces());
                AtlasQuery.RaceStartsFile = Path.Combine(work, "racestarts.txt");
                AtlasQuery.StartCharsSource = () => new List<HubCore>();
                var orcPage = AtlasQuery.Build("race:2");
                var st = orcPage.Facts.FirstOrDefault(f => f.Label == "Starts in");
                Check("race page: when the game data cannot tell, the start comes from the zone notes - 'Valley of Trials, Durotar', labelled Outpost notes, linked to the zone; the 'not shown' note is gone",
                    st != null && st.Value == "Valley of Trials, Durotar" && st.Source == ZoneLore.Label && st.Link == "zone:1411" && !orcPage.Notes.Any(n => n.Contains("not shown")) && orcPage.Notes.Any(n => n.Contains("Outpost's notes on Durotar")),
                    string.Join(" | ", orcPage.Facts) + " / " + string.Join(" | ", orcPage.Notes));
                var sky = AtlasQuery.Build("race:90").Facts.FirstOrDefault(f => f.Label == "Starts in");
                Check("race page: Forever's High Order Skyborne start on Zephras Isle (Thendal Village) from the notes", sky != null && sky.Link == "zone:2521" && sky.Value.StartsWith("Thendal Village") && sky.Value.EndsWith("Zephras Isle"), sky?.Value);
                AtlasQuery.StartCharsSource = () => new List<HubCore> { new HubCore { Race = "Orc", Level = 1, Zone = "Durotar", Subzone = "Razor Hill" } };
                var orc2 = AtlasQuery.Build("race:2");
                Check("race page: a start the data did find stays the only one (a character of the race in Razor Hill: no second 'Starts in' from the notes)",
                    orc2.Facts.Count(f => f.Label == "Starts in") == 1 && orc2.Facts.First(f => f.Label == "Starts in").Source == AtlasLabel.Mine, string.Join(" | ", orc2.Facts));
                AtlasQuery.StartCharsSource = () => new List<HubCore>();
                if (File.Exists(AtlasQuery.RaceStartsFile)) File.Delete(AtlasQuery.RaceStartsFile);

                // ============================================================================================================ the card in the window
                MapRender.DirOverride = Path.Combine(work, "maps");
                MapRender.TileSourceForTest = fdid => Solid(256, 256, (byte)(70 + fdid % 40), (byte)(96 + fdid % 30), (byte)(60 + fdid % 25));
                MapRender.ForgetFailuresForTest();
                Width = 1400; Height = 1100;
                ResetAtlasForTest();
                ShowTab("atlas");
                string openedUrl = null; AtlasView.OpenLinkOverride = u => openedUrl = u;
                async Task<FrameworkElement> OpenOverview(int zone)
                {
                    await AtlasView.Open("zone:" + zone); await AtlasView.WaitForTest(); await Task.Delay(100); UpdateLayout();
                    AtlasView.SelectTab(AtlasTabs.Overview, false); await Task.Delay(100); UpdateLayout();
                    return AtlasView.LoreCardForTest;
                }
                List<TextBlock> Links(FrameworkElement card, string prefix) => AtlasWalk(card).OfType<TextBlock>().Where(t => t.Tag is string s && s.StartsWith(prefix, StringComparison.Ordinal)).ToList();

                var card = await OpenOverview(1411);
                var texts = card == null ? new List<string>() : AtlasTexts(card);
                Check("Durotar: the Overview has 'About Durotar' with the source label, the facts line and the summary", card != null && card.IsVisible && texts.Contains("About Durotar") && texts.Contains(ZoneLore.Label) && texts.Contains("Levels 1-10 · Horde") && texts.Contains(dur.Summary),
                    string.Join(" | ", texts.Take(6)));
                var ov = (AtlasView.TabHostForTest as ContentControl)?.Content as Panel;
                Check("Durotar: the card is the first thing on the Overview (2.70: a zone page has no game-tooltip card of its own any more, its header says it)", ov != null && ov.Children.IndexOf(card) == 0, ov == null ? "no panel" : ov.Children.IndexOf(card).ToString());
                Check("Durotar: 'Starts here' lists the Valley of Trials and the Echo Isles; the Orc is a link to its race page, the Troll (not playable here) plain",
                    texts.Contains("Starts here") && texts.Contains("Valley of Trials") && texts.Contains("Echo Isles") && Links(card, "race:").Select(t => t.Tag as string).SequenceEqual(new[] { "race:2" }) && texts.Contains("Troll")
                    && AtlasWalk(card).OfType<Border>().Any(b => b.Tag as string == "race-plain"), string.Join(",", Links(card, "race:").Select(t => t.Tag)));
                var npcLinks = Links(card, "npc:");
                Check("Durotar: History, Notable (each creature a link to its NPC page with why it matters), Worth knowing and the sources are shown",
                    texts.Contains("History") && texts.Contains(dur.History) && texts.Contains("Notable") && npcLinks.Count == dur.Mobs.Count(m => m.Linked) && npcLinks.Select(t => t.Text).SequenceEqual(dur.Mobs.Where(m => m.Linked).Select(m => m.Name))
                    && dur.Mobs.All(m => m.Why.Length == 0 || texts.Contains(m.Why)) && texts.Contains("Worth knowing") && dur.Mentions.All(texts.Contains) && texts.Contains("warcraft.wiki.gg"),
                    string.Join(" | ", npcLinks.Select(t => t.Text)));
                var srcLinks = AtlasWalk(card).OfType<TextBlock>().Where(t => t.Tag is string s && s.StartsWith("http", StringComparison.Ordinal)).ToList();
                Check("Durotar: every source is a small link (Caption) that names its site", srcLinks.Count == dur.Sources.Count && srcLinks.All(t => t.Style == (Style)FindResource("Text.Caption")) && srcLinks[0].Text == ZoneLore.SourceLabel(dur.Sources[0], dur.Sources));
                ClickRow(srcLinks[0]);
                Check("Durotar: a source opens in the browser like the other outside links", openedUrl == dur.Sources[0], openedUrl);
                Check("Durotar: the paragraphs keep to the reading width", AtlasWalk(card).OfType<TextBlock>().Where(t => t.TextWrapping == TextWrapping.Wrap).All(t => t.ActualWidth <= ElansAddonHub.AtlasPage.ReadWidth + 0.5));
                await ShotWait(400);
                Snapshot(P("zonelore-1411-durotar.png"));
                SnapshotSheet(card, P("zonelore-1411-card.png"));
                var first = npcLinks[0]; var firstLink = first.Tag as string;
                ClickRow(first);
                await AtlasView.WaitForTest(); await Task.Delay(100);
                Check("Durotar: a creature's link opens its NPC page (" + firstLink + ")", AtlasTabs.BaseId(AtlasView.CurrentId) == firstLink, AtlasView.CurrentId);

                card = await OpenOverview(1429);
                texts = card == null ? new List<string>() : AtlasTexts(card);
                var elw = ZoneLore.For(1429);
                Check("Elwynn Forest: 'About Elwynn Forest', Northshire Valley for the Human, its creatures as links", card != null && texts.Contains("About Elwynn Forest") && texts.Contains(elw.Starts[0].Area) && texts.Contains("Human") && Links(card, "npc:").Count == elw.Mobs.Count(m => m.Linked));
                await ShotWait(400);
                Snapshot(P("zonelore-1429-elwynn.png"));
                SnapshotSheet(card, P("zonelore-1429-card.png"));

                card = await OpenOverview(2521);
                texts = card == null ? new List<string>() : AtlasTexts(card);
                var zep = ZoneLore.For(2521);
                Check("Zephras Isle: the Skyborne start (High Order Skyborne links to its race page), 'On Forever' with Forever's note, the forever-seen creatures as links",
                    card != null && texts.Contains("About Zephras Isle") && Links(card, "race:").Any(t => (string)t.Tag == "race:90") && texts.Contains("On Forever") && texts.Contains(zep.Forever) && Links(card, "npc:").Count == zep.Mobs.Count(m => m.Linked) && zep.Mobs.Count > 0);
                await ShotWait(400);
                Snapshot(P("zonelore-2521-zephras.png"));
                SnapshotSheet(card, P("zonelore-2521-card.png"));

                card = await OpenOverview(2665);
                var t2665 = card == null ? new List<string>() : AtlasTexts(card);
                Check("Zephras Isle 2665 (the alternate map): its page shows 2521's notes", card != null && t2665.Contains(zep.Summary) && t2665.Contains("About Zephras Isle"));

                card = await OpenOverview(2482);
                var hy = ZoneLore.For(2482);
                var plain = hy.Mobs.Where(m => !m.Linked).Select(m => m.Name).ToList();
                var hyTexts = card == null ? new List<string>() : AtlasTexts(card);
                Check("Mount Hyjal: creatures with no id are plain text (no link), the others link", card != null && plain.Count > 0 && plain.All(hyTexts.Contains) && !Links(card, "npc:").Any(t => plain.Contains(t.Text)) && Links(card, "npc:").Count == hy.Mobs.Count(m => m.Linked));

                await AtlasView.Open("zone:11"); await AtlasView.WaitForTest(); UpdateLayout();
                AtlasView.SelectTab(AtlasTabs.Overview, false); await Task.Delay(100); UpdateLayout();
                Check("Greenvale (no notes): no card, nothing named 'About' on the Overview", AtlasView.LoreCardForTest == null && !AtlasWalk(AtlasView.TabHostForTest).OfType<FrameworkElement>().Any(f => f.Tag as string == "zone-lore") && !AtlasTexts(AtlasView.TabHostForTest).Any(t => t.StartsWith("About ")));
                Check("a zone with notes opens on its Overview (the notes), the map one click away", await OpenTabOf(1411) == AtlasTabs.Overview);
            }
            catch (Exception e) { Check("zone notes test ran to the end", false, e.ToString()); }
            finally
            {
                Width = savedWidth; Height = savedHeight; AtlasView.OpenLinkOverride = savedLink;
                AtlasQuery.StartCharsSource = savedSrc; AtlasQuery.RaceStartsFile = savedFile; ZoneLoreCheck.Capitals = savedCaps;
                MapRender.TileSourceForTest = savedTiles; MapRender.DirOverride = savedMapDir;
                ClassData.ResetForTest(); SeenData.SetForTest(null, SeenData.State.NotStarted); ClassicAtlas.Use(null);
                MapData.ResetForTest(); ZoneLore.ResetForTest();
                try { ResetAtlasForTest(); } catch (Exception) { }
            }
            return (ok ? "zonelore: ok" : "zonelore: FAILED") + "\r\n" + string.Join("\r\n", notes);
        }

        async Task<string> OpenTabOf(int zone)
        {
            await AtlasView.Open("zone:" + zone); await AtlasView.WaitForTest(); UpdateLayout();
            return AtlasView.TabNowForTest;
        }
    }
}
