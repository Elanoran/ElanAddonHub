using System;
using Elans.Shared;
using System.Collections.Generic;
using System.Linq;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Maps;
using ElansAddonHub.Services.WorldMap;

namespace ElansAddonHub
{
    // --selftest "dungeonmaps", part 4: the way into the instances the game data gives no ghost spot for. The approximate game-data source (NamedEntrances: a map point
    // or a continent area of the instance's name), the labels of the instance list (a levelled one stays a dungeon), the learned entrances (Elan's Toolkit `entrances`,
    // the Lodge's merge, atlas6) and which spot the pages and the world map show.
    public partial class MainWindow
    {
        static MapSet DmEntranceSet()
        {
            var set = new MapSet { HasMaps = true, HasAssignments = true, HasAreas = true };
            set.Maps.Add(new GameMapInfo { Id = 1, Name = "Kalimdor", Directory = "Kalimdor", InstanceType = 0, WdtFileDataId = 9001 });
            set.Maps.Add(new GameMapInfo { Id = 36, Name = "Deadmines", Directory = "DeadminesInstance", InstanceType = 1, MaxPlayers = 10, CorpseMapId = 1, CorpseX = 100, CorpseY = 100 });
            set.Maps.Add(new GameMapInfo { Id = 2784, Name = "Demon Fall Canyon", Directory = "2784", InstanceType = 1, MaxPlayers = 5, CorpseMapId = -1 });
            set.Maps.Add(new GameMapInfo { Id = 2804, Name = "The Crystal Vale", Directory = "2804", InstanceType = 2, MaxPlayers = 20, CorpseMapId = -1, AltEntranceMapId = 1, AltX = 300, AltY = 400, AltAreaId = 2740, AltKind = NamedEntrances.KindArea });
            set.Maps.Add(new GameMapInfo { Id = 3065, Name = "The Hall of Thanes", Directory = "3065", InstanceType = 1, MaxPlayers = 5, CorpseMapId = -1 });
            set.UiMaps.Add(new UiMapInfo { Id = 1414, Name = "Kalimdor", Type = UiMapInfo.Continent });
            set.UiMaps.Add(new UiMapInfo { Id = 1440, Name = "Ashenvale", ParentId = 1414, Type = UiMapInfo.Zone });
            set.Assignments.Add(new UiAssignment { Id = 1, UiMapId = 1440, MapId = 1, AreaId = 331, UiMinX = 0, UiMinY = 0, UiMaxX = 1, UiMaxY = 1, MinX = -1000, MaxX = 1000, MinY = -1000, MaxY = 1000 });
            set.Areas.Add(new AreaInfo { Id = 331, Name = "Ashenvale", ContinentId = 1 });
            set.Areas.Add(new AreaInfo { Id = 435, Name = "Demon Fall Canyon", ParentId = 331, ContinentId = 1 });
            set.Areas.Add(new AreaInfo { Id = 15475, Name = "Demon Fall Canyon", ContinentId = 2784 });
            set.Lfg.Add(new LfgDungeonInfo { Id = 1, Name = "The Hall of Thanes", MinLevel = 13, MaxLevel = 13 });
            set.HasLfg = true;
            return set;
        }

        void DmEntranceChecks(CheckFn Check)
        {
            var I = System.Globalization.CultureInfo.InvariantCulture;
            // ---- the approximate source: a map point named like the instance wins over nothing; the instance's own area (on its own map) is no continent
            var set = DmEntranceSet();
            var pois = new List<NamedEntrances.Poi>
            {
                new NamedEntrances.Poi { Id = 1239, Name = "Demon Fall Canyon", ContinentId = 1, AreaId = 434, X = 500, Y = 200 },
                new NamedEntrances.Poi { Id = 7, Name = "Deadmines", ContinentId = 1, X = 1, Y = 1 },
            };
            NamedEntrances.FromPois(set, pois);
            var dfcRow = set.Maps.First(m => m.Id == 2784);
            var area = NamedEntrances.AreaNamed(set, dfcRow);
            Check("approximate entrances: a map point (AreaPOI) named like an instance with no ghost spot gives its approximate way in; an instance with a ghost spot is left alone; the continent area of the same name is found (not the instance's own)",
                dfcRow.AltEntranceMapId == 1 && dfcRow.AltX == 500 && dfcRow.AltKind == NamedEntrances.KindPoi && set.Maps.First(m => m.Id == 36).AltEntranceMapId < 0 && area != null && area.Id == 435,
                dfcRow.AltEntranceMapId + " " + dfcRow.AltKind + " " + area?.Id);
            var idx = new MapIndex(set);
            var dm = idx.Instance(36); var dfc = idx.Instance(2784); var cv = idx.Instance(2804); var thanes = idx.Instance(3065);
            Check("approximate entrances: the instance page's entrance is marked approximate with its own source (a map point / an area, not the door); the ghost spot is the door",
                dfc.Entrance != null && dfc.Entrance.Approximate && dfc.Entrance.Source == MapIndex.PoiSourceName && cv.Entrance != null && cv.Entrance.Approximate && cv.Entrance.Source == MapIndex.AreaSourceName && dm.Entrance != null && !dm.Entrance.Approximate && dfc.Entrance.UiMapId == 1440,
                dfc.Entrance?.Source + " | " + cv.Entrance?.Source);
            Check("labels: an instance with no ghost spot but a suggested level (The Hall of Thanes, level 13) is likely live: a dungeon, flagged NoGhostSpot, not an event; one with no level is an event",
                !thanes.Event && thanes.NoGhostSpot && thanes.MinLevel == 13 && dfc.Event && dfc.NoGhostSpot && !dm.NoGhostSpot && AtlasQuery.InstanceDetail(thanes) == "Dungeon · 5 players", AtlasQuery.InstanceDetail(thanes));

            // ---- the Toolkit's notes
            var hub = new Dictionary<string, object>
            {
                ["since"] = 1.0,
                ["entrances"] = new Dictionary<string, object>
                {
                    ["3065"] = new List<object> { new Dictionary<string, object> { ["z"] = 1440.0, ["x"] = 0.25, ["y"] = 0.75, ["n"] = 3.0, ["at"] = 1_790_000_000.0 } },
                    ["2784"] = new List<object> { new Dictionary<string, object> { ["z"] = 1440.0, ["x"] = 0.6, ["y"] = 0.4, ["n"] = 1.0 }, new Dictionary<string, object> { ["z"] = 1440.0, ["x"] = 1.5, ["y"] = 0.4, ["n"] = 1.0 } },
                    ["0"] = new List<object> { new Dictionary<string, object> { ["z"] = 1440.0, ["x"] = 0.6, ["y"] = 0.4, ["n"] = 1.0 } },
                },
            };
            var snap = AtlasObs.FromTables(hub);
            Check("Toolkit notes: atlas.obs `entrances` is read per instance (zone map, position, times entered); a spot off the map and a bad instance key are left out and counted; the section is known",
                snap.Entrances.Count == 2 && snap.Entrances[3065][0].Z == 1440 && snap.Entrances[3065][0].N == 3 && snap.Entrances[2784].Count == 1 && snap.Dropped == 2 && !snap.UnknownSections.Contains("entrances") && !snap.IsEmpty,
                snap.Entrances.Count + " " + snap.Dropped);
            var b0 = AtlasObs.MakeBatch(snap, null, new Dictionary<string, string>());
            var b1 = AtlasObs.MakeBatch(snap, null, new Dictionary<string, string>(), includeEntrances: true);
            var sentFrame = Wire(b1?.Frame) != null && Wire(b1.Frame).TryGetValue("entrances", out var eo) ? eo as Dictionary<string, object> : null;
            Check("sharing: entrances go to a lodge that takes them (atlas6) and only there; a resend of what the lodge has sends nothing",
                (b0 == null || !Wire(b0.Frame).ContainsKey("entrances")) && sentFrame != null && sentFrame.Count == 2 && AtlasObs.MakeBatch(snap, null, b1.Sigs, includeEntrances: true) == null);

            // ---- the lodge's merge and the spot shown
            var lodge = new Lodge.LodgeAtlas();
            bool applied = lodge.Apply(LodgeJson.FromPlain(new Dictionary<string, object>
            {
                ["t"] = "atlas.data", ["epoch"] = "abc123", ["rev"] = 5.0, ["full"] = true,
                ["entrances"] = new Dictionary<string, object>
                {
                    ["3065"] = new List<object>
                    {
                        new Dictionary<string, object> { ["z"] = 1440.0, ["x"] = 0.3, ["y"] = 0.7, ["n"] = 9.0, ["m"] = 4.0 },
                        new Dictionary<string, object> { ["z"] = 1440.0, ["x"] = 0.9, ["y"] = 0.1, ["n"] = 20.0, ["m"] = 1.0 },
                    },
                },
            }) as System.Text.Json.Nodes.JsonObject);
            var hadLodge = AtlasQuery.Context.Lodge; var hadOwn = AtlasQuery.Context.OwnEntrances;
            try
            {
                AtlasQuery.Context.OwnEntrances = () => snap.Entrances;
                AtlasQuery.Context.Lodge = () => null;
                var own = AtlasInstances.Entrance(idx, thanes);
                Check("shown: with no lodge data your own note is the way in (\"You\", seen in the game)", own != null && own.Learned && own.Members == 0 && Math.Abs(own.Fx - 0.25) < 1e-9 && AtlasInstances.LabelOf(own) == AtlasInstances.YouLabel && own.MapId == 1 && own.Source == AtlasInstances.OwnSource, own?.Source);
                AtlasQuery.Context.Lodge = () => lodge;
                var lo = AtlasInstances.Entrance(idx, thanes);
                Check("shown: the lodge's spot most members entered from wins (4 members, not the stray spot with more entries), labelled \"Lodge members\"; its world position comes from the zone map",
                    applied && lo != null && lo.Learned && lo.Members == 4 && Math.Abs(lo.Fx - 0.3) < 1e-9 && AtlasInstances.LabelOf(lo) == AtlasInstances.LodgeLabel && lo.Source.Contains("4 members") && lo.MapId == 1 && Math.Abs(lo.X - (1000 - 0.7 * 2000)) < 1e-6,
                    lo == null ? "none" : lo.Members + " " + lo.Fx + " " + lo.X.ToString(I));
                var g = AtlasInstances.Entrance(idx, dm);
                var a = AtlasInstances.Entrance(idx, cv);
                var dfcShown = AtlasInstances.Entrance(idx, dfc);
                Check("shown: the game's ghost spot beats everything; a learned spot beats the game data's approximate one (Demon Fall Canyon: your note); the approximate one stays when nothing was seen (The Crystal Vale)",
                    g != null && !g.Learned && !g.Approximate && dfcShown != null && dfcShown.Learned && a != null && a.Approximate && AtlasInstances.LabelOf(g) == AtlasLabel.Forever);
                var ent = AtlasInstances.EntranceFor(idx, thanes);
                Check("instance page: the entrance card of The Hall of Thanes says where Lodge members entered from (Ashenvale · 30 / 70, label Lodge members)", ent != null && ent.Learned && ent.ZoneName == "Ashenvale" && ent.Coords == "30 / 70" && ent.Label == AtlasInstances.LodgeLabel, ent?.ZoneName + " " + ent?.Coords + " " + ent?.Label);
                var here = AtlasInstances.InZone(idx, 1440).Select(i => i.Name).ToList();
                Check("zone page: Ashenvale lists the instances whose shown way in is there (Demon Fall Canyon, The Hall of Thanes)", here.Contains("The Hall of Thanes") && here.Contains("Demon Fall Canyon"), string.Join(", ", here));
                MapData.SetForTest(idx);
                try
                {
                    var layer = WorldMapLayers.Instances(1);
                    var json = layer.Json ?? "";
                    Check("world map: the dungeons layer shows the learned entrances, saying who saw them", json.Contains("The Hall of Thanes") && json.Contains("seen by Lodge members") && json.Contains("Demon Fall Canyon"), json.Length > 400 ? json.Substring(0, 400) : json);
                }
                finally { MapData.ResetForTest(); }
            }
            finally { AtlasQuery.Context.Lodge = hadLodge; AtlasQuery.Context.OwnEntrances = hadOwn; }
            var cache = lodge.ToCache();
            var back = new Lodge.LodgeAtlas();
            Check("lodge cache: the merged entrances survive the cache file", back.Apply(LodgeJson.Parse(LodgeJson.Serialize(cache))) && back.EntrancesOf(3065).Count == 2 && back.EntrancesOf(3065).Any(p => p.M == 4));
        }
    }
}
