using System.Globalization;
using System.Linq;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Maps;

namespace ElansAddonHub.Services.Atlas
{
    public static partial class AtlasQuery
    {
        // A race page whose starting zone the game data cannot tell (no first quests for it, no character of it seen) takes it from the zone notes when
        // a zone's notes say the race starts there (Forever's Skyborne on Zephras Isle). Never over a start the data did find: no contradiction.
        static void AddStartFromLore(AtlasPageModel m, RaceInfo r)
        {
            if (m.Facts.Any(f => f.Label == "Starts in")) return;
            var hit = ZoneLore.StartsFor(r.Name, r.LoreName).FirstOrDefault();
            if (hit.Key == null) return;
            var zone = MapData.Index?.Get(hit.Key.ZoneId)?.Name;
            if (string.IsNullOrEmpty(zone)) zone = hit.Key.Name;
            var area = hit.Value.Area.Trim();
            var text = area.Length > 0 && !string.Equals(area, zone, System.StringComparison.OrdinalIgnoreCase) ? area + ", " + zone : zone;
            m.Notes.RemoveAll(n => n.Contains("starting zone") && n.Contains("not shown"));
            m.Facts.Add(new AtlasFact("Starts in", text, ZoneLore.Label, "zone:" + hit.Key.ZoneId.ToString(CultureInfo.InvariantCulture)));
            m.Notes.Add("Starting zone: from the Outpost's notes on " + zone + " (the game data has no first quests or characters of this race to tell).");
        }
    }
}
