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
    }
}
