using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Atlas.Classic;
using ElansAddonHub.Services.Maps;
using ElansAddonHub.Services.WorldMap;

namespace ElansAddonHub
{
    // --selftest "atlaslodge" (part): where NPCs really stand (Elan's Toolkit 0.5.10 `npcspots`, Lodge server 2.26 feature "atlas8"). The module's section (validation,
    // sharing only to a lodge that takes it, resends), the lodge's merge and its cache file, which spots a page shows (the Lodge's first, your own after), the NPC
    // page's "Seen at" section and map (Forever's place ahead of the Classic baseline, the baseline dropped only where members fixed the place), and the world map:
    // the "NPCs seen by Lodge members" layer and the vendor / rare layers that prefer the members' places. Fixture maps and a fixture Classic index; nothing real.
    public partial class MainWindow
    {
        static MapSet NpcSpotSet()
        {
            var set = new MapSet { HasMaps = true, HasAssignments = true, HasAreas = true };
            set.Maps.Add(new GameMapInfo { Id = 0, Name = "Eastern Kingdoms", Directory = "Azeroth", InstanceType = 0 });
            set.Maps.Add(new GameMapInfo { Id = 1, Name = "Kalimdor", Directory = "Kalimdor", InstanceType = 0 });
            set.UiMaps.Add(new UiMapInfo { Id = 1415, Name = "Eastern Kingdoms", Type = UiMapInfo.Continent });
            set.UiMaps.Add(new UiMapInfo { Id = 1429, Name = "Elwynn Forest", ParentId = 1415, Type = UiMapInfo.Zone });
            set.UiMaps.Add(new UiMapInfo { Id = 1414, Name = "Kalimdor", Type = UiMapInfo.Continent });
            set.UiMaps.Add(new UiMapInfo { Id = 1440, Name = "Ashenvale", ParentId = 1414, Type = UiMapInfo.Zone });
            set.Assignments.Add(new UiAssignment { Id = 1, UiMapId = 1429, MapId = 0, AreaId = 12, UiMinX = 0, UiMinY = 0, UiMaxX = 1, UiMaxY = 1, MinX = -1000, MaxX = 1000, MinY = -1000, MaxY = 1000 });
            set.Assignments.Add(new UiAssignment { Id = 2, UiMapId = 1440, MapId = 1, AreaId = 331, UiMinX = 0, UiMinY = 0, UiMaxX = 1, UiMaxY = 1, MinX = -1000, MaxX = 1000, MinY = -1000, MaxY = 1000 });
            set.Areas.Add(new AreaInfo { Id = 12, Name = "Elwynn Forest", ContinentId = 0 });
            set.Areas.Add(new AreaInfo { Id = 331, Name = "Ashenvale", ContinentId = 1 });
            return set;
        }

        // a zone map fraction -> the fixture's world position (X = 1000 - fy * 2000, Y = 1000 - fx * 2000)
        static ClassicSpawn NsSpawn(int map, double fx, double fy) => new ClassicSpawn { Map = map, X = (float)(1000 - fy * 2000), Y = (float)(1000 - fx * 2000) };

        static Dictionary<string, object> NsPt(double z, double x, double y, double k, double n, double m = 0)
        {
            var d = new Dictionary<string, object> { ["z"] = z, ["x"] = x, ["y"] = y, ["k"] = k, ["n"] = n };
            if (m > 0) d["m"] = m;
            return d;
        }

        void AtlasNpcSpotsTest(Action<string, bool> check, List<Dictionary<string, object>> sent)
        {
            void Check(string what, bool cond, string extra = null) => check(what + (cond || string.IsNullOrEmpty(extra) ? "" : " [" + extra + "]"), cond);
            // ---- the Toolkit's section
            var hub = new Dictionary<string, object>
            {
                ["since"] = 1.0,
                ["npcspots"] = new Dictionary<string, object>
                {
                    ["466"] = new List<object> { NsPt(1429, 0.4215, 0.6531, 1, 2), NsPt(1429, 0.8, 0.8, 2, 4), NsPt(1429, 0.5, 0.5, 4, 1), NsPt(1429, 1.5, 0.5, 2, 1) },
                    ["0"] = new List<object> { NsPt(1429, 0.5, 0.5, 1, 1) },
                },
            };
            var snap = AtlasObs.FromTables(hub);
            Check("npc spots: atlas.obs `npcspots` is read per npc (zone map, position, kind, times seen); an unknown kind, a spot off the map and a bad npc key are left out and counted; the section is known",
                snap.NpcSpots.Count == 1 && snap.NpcSpots[466].Count == 2 && snap.NpcSpots[466][0].K == AtlasObs.NpcSpotInteract && snap.NpcSpots[466][1].N == 4 && snap.Dropped == 3
                && !snap.UnknownSections.Contains("npcspots") && !snap.IsEmpty && snap.Stats["npcspots"][1] == 1);
            var b0 = AtlasObs.MakeBatch(snap, null, new Dictionary<string, string>(), includeEntrances: true);
            var b1 = AtlasObs.MakeBatch(snap, null, new Dictionary<string, string>(), includeNpcSpots: true);
            var frame = b1?.Frame != null && b1.Frame.TryGetValue("npcspots", out var fo) ? fo as Dictionary<string, object> : null;
            var wire = frame != null && frame.TryGetValue("466", out var w466) ? w466 as List<object> : null;
            Check("npc spots: shared only with a lodge that takes them (atlas8), as { z, x, y, k, n }; a resend of what the lodge has sends nothing",
                (b0 == null || !b0.Frame.ContainsKey("npcspots")) && wire != null && wire.Count == 2 && ((Dictionary<string, object>)wire[0]).Keys.OrderBy(k => k).SequenceEqual(new[] { "k", "n", "x", "y", "z" })
                && AtlasObs.MakeBatch(snap, null, b1.Sigs, includeNpcSpots: true) == null);

            // ---- the session: npc spots only to a 2.26 server that names atlas8; the merge comes back in atlas.data
            var sentFile = Session.AtlasSentPathForTest; var cacheFile = Session.AtlasCachePathForTest;
            string Welcome(string server) => FunWelcome("member", FunPeople(false), "", "\"reply\",\"react\",\"profile\",\"atlas\",\"atlas2\",\"atlas6\",\"atlas8\"", server);
            void Fresh() { Session.Disconnect(); Session.AtlasResetForTest(); sent.Clear(); try { File.Delete(sentFile); File.Delete(cacheFile); } catch { } }
            Fresh();
            WriteAtlasHub("ACC1", AtlasLua("npcspots = { [466] = { { z = 1429, x = 0.42, y = 0.65, k = 1, n = 2, at = " + AtlasAt + " } } }"));
            Session.FeedForTest(Welcome("2.25.0"));
            Check("session: a 2.25 server that names atlas8 is not sent npc spots", !Session.SupportsAtlasNpcSpots && !AFrames(sent, "atlas.share").Any(f => f.ContainsKey("npcspots")));
            Fresh();
            Session.FeedForTest(Welcome("2.26.0"));
            var shares = AFrames(sent, "atlas.share");
            Check("session: a 2.26 server with atlas8 gets your npc spots", Session.SupportsAtlasNpcSpots && shares.Count == 1 && shares[0].Child("npcspots")?.ContainsKey("466") == true, Json.Write(shares.FirstOrDefault()));
            Session.FeedForTest(AtlasPage("np1", 90, "\"npcspots\":{\"466\":[{\"z\":1429,\"x\":0.42,\"y\":0.65,\"k\":1,\"n\":5,\"m\":2}],\"7\":[{\"z\":1429,\"x\":2,\"y\":0.65,\"k\":1,\"n\":5,\"m\":2}]}", true));
            Check("session: the lodge's merged npc spots arrive (a spot off the map is dropped)", Session.AtlasData.NpcSpotsOf(466).Count == 1 && Session.AtlasData.NpcSpotsOf(466)[0].M == 2 && Session.AtlasData.NpcSpotsOf(7).Count == 0);
            Fresh();

            // ---- the lodge's merge
            var lodge = new Lodge.LodgeAtlas();
            bool applied = lodge.Apply(new Dictionary<string, object>
            {
                ["t"] = "atlas.data", ["epoch"] = "npc123", ["rev"] = 9.0, ["full"] = true,
                ["npcspots"] = new Dictionary<string, object>
                {
                    ["466"] = new List<object> { NsPt(1429, 0.42, 0.65, 1, 7, 3), NsPt(1429, 0.5, 0.5, 2, 2, 1) },
                    ["61"] = new List<object> { NsPt(1440, 0.61, 0.33, 3, 1, 1) },
                    ["299"] = new List<object> { NsPt(1429, 0.3, 0.3, 2, 5, 2) },
                    ["7"] = new List<object> { NsPt(1429, 0.3, 0.3, 9, 5, 2) },
                },
            });
            var back = new Lodge.LodgeAtlas();
            Check("npc spots: the lodge's merge is read with its member counts (a bad kind is dropped) and survives the cache file",
                applied && lodge.NpcSpotsOf(466).Count == 2 && lodge.NpcSpotsOf(466)[0].M == 3 && lodge.NpcSpotsOf(7).Count == 0 && lodge.NpcSpotNpcCount == 3
                && back.Apply(Json.Obj(Json.Write(lodge.ToCache()))) && back.NpcSpotsOf(466).Count == 2 && back.NpcSpotsOf(466).Any(p => p.M == 3 && p.K == 1) && back.NpcSpotsOf(61).Count == 1);

            var hadLodge = AtlasQuery.Context.Lodge; var hadOwn = AtlasQuery.Context.OwnNpcSpots; var hadClassic = ClassicAtlas.Current;
            try
            {
                AtlasQuery.Context.Lodge = () => lodge;
                AtlasQuery.Context.OwnNpcSpots = () => snap.NpcSpots;
                var shown = AtlasQuery.NpcSpots(466);
                Check("npc spots shown: the Lodge's first (most members), then your own the Lodge does not have (your visit at its window is the Lodge's spot already)",
                    shown.Count == 3 && shown[0].Members == 3 && shown[0].Kind == 1 && shown[1].Lodge && shown[2].Members == 0 && Math.Abs(shown[2].X - 0.8) < 1e-9,
                    string.Join(" | ", shown.Select(s => s.Kind + "/" + s.Members + "/" + s.X)));
                Check("npc spots shown: the tooltip says who saw it and how (\"Seen by 3 Lodge members · met here 7 times\", \"Seen by you · killed here 4 times\")",
                    AtlasQuery.NpcSpotTip(shown[0], false) == "Seen by 3 Lodge members · met here 7 times" && AtlasQuery.NpcSpotTip(shown[2], false) == "Seen by you · killed here 4 times" && AtlasQuery.NpcSpotTip(shown[0]).EndsWith(AtlasLabel.Forever),
                    AtlasQuery.NpcSpotTip(shown[0], false) + " | " + AtlasQuery.NpcSpotTip(shown[2], false));
                AtlasQuery.Context.Lodge = () => null;
                Check("npc spots shown: with no lodge your own spots are all there is", AtlasQuery.NpcSpots(466).Count == 2 && AtlasQuery.NpcSpots(466).All(s => !s.Lodge));
                AtlasQuery.Context.Lodge = () => lodge;

                // ---- the NPC page: a vendor Forever put somewhere else in Elwynn; the baseline's Ashenvale spawn stays; a creature only killed keeps the baseline
                var ci = new ClassicIndex();
                ci.Npcs[466] = new ClassicNpc { Id = 466, Name = "Test Vendor", MinLevel = 30, MaxLevel = 30, NpcFlags = 0x80, Spawns = new[] { NsSpawn(0, 0.2, 0.3), NsSpawn(1, 0.5, 0.5) } };
                ci.Npcs[299] = new ClassicNpc { Id = 299, Name = "Test Wolf", MinLevel = 5, MaxLevel = 6, Spawns = new[] { NsSpawn(0, 0.31, 0.31), NsSpawn(0, 0.7, 0.2) } };
                ci.Npcs[62] = new ClassicNpc { Id = 62, Name = "Test Rare", MinLevel = 10, MaxLevel = 10, Rank = 4, Spawns = new[] { NsSpawn(1, 0.9, 0.9) } };
                ci.Finish();
                ClassicAtlas.Use(ci);
                var idx = new MapIndex(NpcSpotSet());
                MapData.SetForTest(idx);
                try
                {
                    var page = AtlasQuery.Build("npc:466");
                    var sa = page.Section("seenat");
                    var elwynn = page.Map?.Zones.FirstOrDefault(z => z.UiMapId == 1429);
                    var ashen = page.Map?.Zones.FirstOrDefault(z => z.UiMapId == 1440);
                    Check("NPC page: a \"Seen at\" section lists where Lodge members (and you) saw it: zone, position, how, how many members",
                        sa != null && sa.Rows.Count == 3 && sa.Rows[0].Title == "Elwynn Forest" && sa.Rows[0].Detail.StartsWith("42 / 65 · Seen by 3 Lodge members · met here") && sa.Rows[0].Right == "3" && sa.Rows[0].Type == "Met" && sa.Rows[2].Detail.Contains("Seen by you"),
                        sa == null ? "none" : string.Join(" | ", sa.Rows.Select(r => r.Title + ": " + r.Detail)));
                    Check("NPC page: its map shows the members' places first; where they met it (Elwynn) the Classic baseline's spawn point is left off and the note says so; the baseline's Ashenvale spawn stays",
                        elwynn != null && elwynn.Markers.Count == 3 && elwynn.Markers.All(mk => (mk.Tooltip ?? "").Contains("Seen by")) && !elwynn.Markers.Any(mk => Math.Abs(mk.Fx - 0.2) < 0.01)
                        && ashen != null && ashen.Markers.Count == 1 && (ashen.Markers[0].Tooltip ?? "").Contains(AtlasLabel.Classic) && page.Map.Zones[0].UiMapId == 1429 && sa.Note.Contains("left off the map"),
                        elwynn == null ? "no Elwynn" : string.Join(" | ", elwynn.Markers.Select(mk => mk.Fx + "," + mk.Fy + " " + mk.Tooltip)));
                    var wolf = AtlasQuery.Build("npc:299");
                    var wz = wolf.Map?.Zones.FirstOrDefault(z => z.UiMapId == 1429);
                    Check("NPC page: a creature only killed (kill spots sample its spawns) keeps the baseline's spawn points beside the members' spot",
                        wz != null && wz.Markers.Count == 3 && wz.Markers.Count(mk => (mk.Tooltip ?? "").Contains(AtlasLabel.Classic)) == 2 && wolf.Section("seenat")?.Rows.Count == 1 && !wolf.Section("seenat").Note.Contains("left off"),
                        wz == null ? "none" : wz.Markers.Count.ToString());

                    // ---- the world map
                    var seen0 = WorldMapLayers.Build("seen", 0);
                    var seen1 = WorldMapLayers.Build("seen", 1);
                    Check("world map: \"NPCs seen by Lodge members\" puts every member and own spot on its continent, by kind (met / killed / rares), named and saying who saw it",
                        seen0.Count == 4 && seen0.Json.Contains("Test Vendor") && seen0.Json.Contains("Seen by 3 Lodge members") && seen0.Json.Contains("seen.met") && seen0.Json.Contains("seen.killed") && seen1.Count == 1 && seen1.Json.Contains("seen as a rare here"),
                        seen0.Count + " " + seen1.Count + " " + seen0.Note);
                    var vend0 = WorldMapLayers.Build("vendors", 0);
                    var vend1 = WorldMapLayers.Build("vendors", 1);
                    Check("world map: the vendor layer shows the place members met the vendor instead of the baseline's spawn in that zone; elsewhere the baseline stays",
                        vend0.Count == 1 && vend0.Json.Contains("met here") && vend1.Count == 1 && vend1.Json.Contains("Test Vendor") && vend0.Note.Contains("Lodge members"), vend0.Count + " " + vend1.Count + " " + vend0.Note);
                    var rare1 = WorldMapLayers.Build("rares", 1);
                    Check("world map: the rare layer adds a rare only the minimap showed members (not in the baseline) next to the baseline's rares",
                        rare1.Count == 2 && rare1.Json.Contains("seen as a rare here") && rare1.Json.Contains("Test Rare"), rare1.Count + " " + rare1.Note);
                    var cat = WorldMapLayers.Catalog();
                    var seenRow = cat.FirstOrDefault(c => (string)c["id"] == "seen");
                    var kids = seenRow != null && seenRow.ContainsKey("children") ? ((List<Dictionary<string, object>>)seenRow["children"]).Select(k => (string)k["id"]).ToList() : new List<string>();
                    Check("world map: the layer is in the panel (kinds met / killed / rares), its ids pass the protocol, it is on by default with met and rares, and a saved list from before gets it switched on once",
                        seenRow != null && kids.SequenceEqual(new[] { "seen.met", "seen.killed", "seen.rares" }) && kids.All(WorldMapIds.IsLayerId) && WorldMapIds.IsTopLayer("seen") && !WorldMapIds.IsLayerId("seen.wolves")
                        && WorldMapLayers.DefaultOn.Contains("seen") && WorldMapLayers.DefaultOn.Contains("seen.met") && !WorldMapLayers.DefaultOn.Contains("seen.killed")
                        && WorldMapLayers.WithNewDefaults(new List<string> { "zones" }, 3).Contains("seen") && !WorldMapLayers.WithNewDefaults(new List<string> { "zones" }, 4).Contains("seen"));
                    AtlasQuery.Context.Lodge = () => null; AtlasQuery.Context.OwnNpcSpots = () => null;
                    var none = WorldMapLayers.Build("seen", 0);
                    var bare = AtlasQuery.Build("npc:466");
                    Check("with nothing seen: the layer is empty and says where the places come from; the NPC page has no \"Seen at\" and shows the baseline as before",
                        none.Empty && none.Note.Contains("Elan's Toolkit") && bare.Section("seenat") == null && bare.Map.Zones.Sum(z => z.Markers.Count) == 2);
                }
                finally { MapData.ResetForTest(); }
            }
            catch (Exception e) { Check("npc spots test crashed: " + e, false); }
            finally { AtlasQuery.Context.Lodge = hadLodge; AtlasQuery.Context.OwnNpcSpots = hadOwn; ClassicAtlas.Use(hadClassic); }
        }
    }
}
