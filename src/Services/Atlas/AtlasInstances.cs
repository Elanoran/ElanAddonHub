using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ElansAddonHub.Services.Atlas.Classic;
using ElansAddonHub.Services.Maps;

namespace ElansAddonHub.Services.Atlas
{
    // What the pages and the world map say about the dungeons and raids: how deep their level range goes and where their entrances are.
    // The data is on the MapIndex (Map.db2 corpse spot = entrance, LFGDungeons + ContentTuning = suggested level); this is the wording and the lookups.
    public static class AtlasInstances
    {
        // The levels to show: the game's own (one suggested level) first; an instance the game does not list there (Naxxramas, the Ahn'Qiraj raids) gets the range of
        // its creatures from the Classic baseline when that is on the PC. source = the label of where the numbers come from; false = no numbers at all.
        public static bool Levels(InstanceInfo inst, out int lo, out int hi, out string source)
        {
            lo = hi = 0; source = "";
            if (inst == null) return false;
            if (inst.MinLevel > 0) { lo = inst.MinLevel; hi = Math.Max(inst.MinLevel, inst.MaxLevel); source = AtlasLabel.Forever; return true; }
            var dv = AtlasDerived.For(ClassicAtlas.Current);
            if (dv != null && dv.InstanceCreatureLevels(inst.MapId, out lo, out hi)) { source = AtlasLabel.Classic; return true; }
            return false;
        }

        public static string LevelText(InstanceInfo inst) => Levels(inst, out var lo, out var hi, out _) ? AtlasFormat.Levels(lo, hi) : "";

        // the dungeons and raids whose way in stands in this zone (or city) map, by level then name
        public static List<InstanceInfo> InZone(MapIndex idx, int uiMapId)
        {
            if (idx == null || uiMapId <= 0) return new List<InstanceInfo>();
            return idx.Instances.Where(i => !i.Placeholder && i.Entrance != null && i.Entrance.UiMapId == uiMapId)
                .OrderBy(i => i.MinLevel <= 0 ? int.MaxValue : i.MinLevel).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // "Dungeon · 5 players", "Raid · 40 players"
        public static string Summary(InstanceInfo i)
        {
            var lv = LevelText(i);
            return string.Join(" · ", new[] { i.Kind, i.MaxPlayers > 0 ? i.MaxPlayers.ToString(CultureInfo.InvariantCulture) + " players" : "", lv.Length > 0 ? "level " + lv : "" }.Where(s => s.Length > 0));
        }

        // where the entrance is, in words: "Barrens · 45 / 38" (a zone map position in percent, as the game shows it), "" when there is none
        public static string Where(InstanceInfo i)
        {
            var e = i?.Entrance; if (e == null) return "";
            if (e.UiMapId <= 0) return "";
            return e.ZoneName + " · " + Pct(e.Fx) + " / " + Pct(e.Fy);
        }
        public static string Pct(double f) => Math.Round(f * 100, 1).ToString("0.#", CultureInfo.InvariantCulture);

        // the entrance block of an instance page (null when the game gives none)
        public static AtlasEntrance EntranceFor(MapIndex idx, InstanceInfo i)
        {
            var e = i?.Entrance; if (e == null || idx == null) return null;
            var r = new AtlasEntrance
            {
                InstanceId = i.MapId, InstanceName = i.Name, UiMapId = e.UiMapId, ZoneName = e.ZoneName, MapId = e.MapId, X = e.X, Y = e.Y, Fx = e.Fx, Fy = e.Fy, Source = e.Source, Label = AtlasLabel.Forever,
                Wings = i.Parts.Count > 1 ? string.Join(", ", i.Parts) : "",
            };
            try { if (WorldMap.WorldMapIds.WorldMapAvailable(idx)) r.WorldMapId = WorldMap.WorldMapIds.IdAt(e.MapId, e.X, e.Y, 6, true); } catch (Exception) { }
            r.Command = Command(idx, i);
            return r;
        }

        // "Navigate here": /etk way <ui map> <x %> <y %> <the instance's name> - the zone map is the one that shows the entrance
        public static string Command(MapIndex idx, InstanceInfo i)
        {
            var e = i?.Entrance; if (e == null || idx == null) return null;
            var way = WorldMap.WorldMapCommands.Way(idx, e.MapId, e.X, e.Y, out _);
            if (way == null) return null;
            var parts = way.Split(' ');
            if (parts.Length < 5) return way;
            var title = WorldMap.WorldMapCommands.SafeTitle(i.Name);
            return string.Join(" ", parts.Take(5)) + (title.Length > 0 ? " " + title : "");
        }
    }

    // the "Entrance" section of an instance page: the spot on the zone map, the zone, the continent position and the ways to go there
    public sealed class AtlasEntrance
    {
        public int InstanceId, UiMapId, MapId;
        public string InstanceName = "", ZoneName = "", Source = "", Label = "";
        public double Fx, Fy, X, Y;                 // the spot on the zone map (0..1) and in world yards (x north, y west) on continent MapId
        public string WorldMapId;                   // "Show on world map" (null = the continent has no world map here)
        public string Command;                      // "Navigate here": /etk way ... (null = no zone map shows the spot)
        public string Coords => AtlasInstances.Pct(Fx) + " / " + AtlasInstances.Pct(Fy);
        public string Wings = "";                   // "Dire Maul - East, Dire Maul - West, Dire Maul - North" when the game lists several
    }
}
