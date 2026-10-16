using System;
using System.Collections.Generic;
using System.Linq;

namespace ElansAddonHub.Services.Db2
{
    // Atlas layer A4: factions and faction templates from the player's own game files (read together with the spell data, see SpellData).
    // An NPC (or a vendor, or an object) in the other layers names its faction TEMPLATE; this turns it into a faction name and tells how that template
    // treats the Alliance and the Horde. Everything is null / Neutral when the tables are not read or were laid out differently by a patch.
    public static class FactionData
    {
        public static bool Ready => SpellData.Factions != null;
        public static int Count => SpellData.Factions?.Factions.Count ?? 0;
        public static int TemplateCount => SpellData.Factions?.Templates.Count ?? 0;

        public static FactionInfo Faction(int id) => SpellData.Factions?.Faction(id);
        public static FactionTemplateInfo Template(int templateId) => SpellData.Factions?.Template(templateId);

        // the name of a faction ("Stormwind"); null when unknown
        public static string Name(int factionId) { var f = Faction(factionId); return f == null || string.IsNullOrEmpty(f.Name) ? null : f.Name; }

        // the name of the faction a template belongs to ("Stormwind" for the guards' template); null when unknown
        public static string NameOfTemplate(int templateId)
        {
            var t = Template(templateId);
            return t == null ? null : Name(t.FactionId);
        }

        // the faction and, if it has one, the parent (the reputation group it is shown under: "Alliance", "Steamwheedle Cartel" ...)
        public static string GroupOfTemplate(int templateId)
        {
            var t = Template(templateId);
            var f = t == null ? null : Faction(t.FactionId);
            var p = f == null || f.ParentId <= 0 ? null : Faction(f.ParentId);
            return p == null || string.IsNullOrEmpty(p.Name) ? null : p.Name;
        }

        // how the template treats a player of the Alliance / the Horde
        public static Standing ToAlliance(int templateId) => SpellData.Factions?.ToAlliance(templateId) ?? Standing.Neutral;
        public static Standing ToHorde(int templateId) => SpellData.Factions?.ToHorde(templateId) ?? Standing.Neutral;

        // "Stormwind (friendly to the Alliance, hostile to the Horde)"; just the name when it is neutral to both
        public static string Describe(int templateId)
        {
            var name = NameOfTemplate(templateId);
            if (name == null) return null;
            var a = ToAlliance(templateId); var h = ToHorde(templateId);
            var parts = new List<string>();
            if (a != Standing.Neutral) parts.Add(Word(a) + " to the Alliance");
            if (h != Standing.Neutral) parts.Add(Word(h) + " to the Horde");
            return parts.Count == 0 ? name : name + " (" + string.Join(", ", parts) + ")";
        }

        static string Word(Standing s) => s == Standing.Friendly ? "friendly" : s == Standing.Hostile ? "hostile" : "neutral";

        // ---- which side a faction belongs to (the Atlas Factions list: All | Alliance | Horde | Shared)
        // The client's own tree decides: a faction is the Alliance's when it, or a group above it, is the "Alliance" or "Alliance Forces" group (ids 469 / 891,
        // or those names when a patch renumbered them), the Horde's likewise (67 / 892); a few factions only one side can earn reputation with sit outside those
        // groups and are named here. Everything else (Steamwheedle Cartel, Argent Dawn, the creature factions ...) is shared.
        public const string Alliance = "alliance", Horde = "horde", Shared = "shared";
        public static readonly int[] AllianceGroups = { 469, 891 }, HordeGroups = { 67, 892 };
        static readonly Dictionary<int, string> sideOverrides = new Dictionary<int, string>
        {
            [589] = Alliance,      // Wintersaber Trainers: only night elves of the Alliance can earn it
            [630] = Horde,         // Ravasaur Trainers: the Horde's counterpart of the Wintersaber Trainers
            [471] = Alliance,      // Wildhammer Clan
            [2586] = Alliance,     // Azeroth Commerce Authority (the Alliance's trade reputation)
            [2587] = Horde,        // Durotar Supply and Logistics (the Horde's)
        };

        public static string SideOf(int factionId) => SideOf(SpellData.Factions, factionId);

        public static string SideOf(FactionSnapshot fs, int factionId)
        {
            if (sideOverrides.TryGetValue(factionId, out var o)) return o;
            var seen = new HashSet<int>();
            int id = factionId;
            while (id > 0 && seen.Add(id) && seen.Count <= 12)
            {
                if (AllianceGroups.Contains(id)) return Alliance;
                if (HordeGroups.Contains(id)) return Horde;
                var f = fs?.Faction(id);
                if (f == null) break;
                var n = (f.Name ?? "").Trim();
                if (string.Equals(n, "Alliance", StringComparison.OrdinalIgnoreCase) || string.Equals(n, "Alliance Forces", StringComparison.OrdinalIgnoreCase)) return Alliance;
                if (string.Equals(n, "Horde", StringComparison.OrdinalIgnoreCase) || string.Equals(n, "Horde Forces", StringComparison.OrdinalIgnoreCase)) return Horde;
                id = f.ParentId;
            }
            return Shared;
        }

        public static string SideTitle(string side) => side == Alliance ? "Alliance" : side == Horde ? "Horde" : side == Shared ? "Shared" : "All";
        public static string SideFact(string side) => side == Alliance ? "Alliance" : side == Horde ? "Horde" : "Both sides";
    }

    // Reputation standings as the game numbers them (1 Hated .. 8 Exalted) and the progress inside one standing
    public static class Reputation
    {
        static readonly string[] names = { "", "Hated", "Hostile", "Unfriendly", "Neutral", "Friendly", "Honored", "Revered", "Exalted" };

        public static string StandingName(int s) => s >= 1 && s <= 8 ? names[s] : "Unknown";

        // 0..1 inside the current standing; -1 when its bounds are not known
        public static double Progress(int value, int min, int max) => max > min ? Math.Max(0, Math.Min(1, (value - min) / (double)(max - min))) : -1;

        // "Revered 4,200 / 21,000" (the bar the game's reputation window shows), just the standing when the bounds are not known
        public static string Line(int s, int value, int min, int max)
            => StandingName(s) + (max > min ? " " + Math.Max(0, value - min).ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " / " + (max - min).ToString("N0", System.Globalization.CultureInfo.InvariantCulture) : "");
    }
}
