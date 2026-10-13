using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ElansAddonHub.Services.Professions
{
    // ============================================================================================================================
    // The leveling guide of a GATHERING profession (Herbalism, Mining, Skinning, Fishing): where to go at which skill, by zone.
    //
    // What the game data can and cannot say: the client's tables hold no "skill needed" for a herb, vein, hole or creature, so the skill of a
    // place is ESTIMATED, and the guide says so. The rules, all explainable:
    //   * herbs, veins and fishing holes (nodes): the Classic baseline lists where each node spawns; a zone's level is the median level of the
    //     creatures spawned there (the same baseline); the estimated skill needed is 5 x that level (a zone of level-20 creatures wants about skill 100),
    //     capped at the profession's last rank cap. Observations of the Lodge (opens per zone) add zones the baseline does not list; they are labelled.
    //   * skinning: the skill to skin a creature of level L is L <= 10: 1, L 11-20: 10 x L - 100, above: 5 x L (the usual rule), from the creatures
    //     that have skinning loot in the baseline.
    //   * the skill brackets are 50 wide (1-50, 50-100 ... up to the last rank cap); for each bracket the busiest zones are listed with their level range
    //     and what grows / lives there.
    // ============================================================================================================================
    public sealed class ZoneInfo
    {
        public int Id;
        public string Name = "";
        public int Median, MinLevel, MaxLevel;            // creature levels spawned in the zone (10th to 90th percentile); 0 = unknown
        public bool KnownLevel => Median > 0;
    }

    public sealed class GatherEntity
    {
        public string Kind = "obj";                       // obj | npc
        public int Id;
        public string Name = "";
        public List<int> Items = new List<int>();         // what it gives (herb, ore, leather, fish)
        public int Level;                                 // creatures: their level (skinning)
        public Dictionary<int, int> ClassicZones = new Dictionary<int, int>();     // uiMapId -> spawns in the Classic baseline
        public Dictionary<int, int> LodgeZones = new Dictionary<int, int>();       // uiMapId -> times the Lodge saw it opened / looted there
        public string Link => Kind + ":" + Id.ToString(CultureInfo.InvariantCulture);
    }

    public interface IGatherWorld
    {
        bool Available { get; }
        string Missing { get; }                           // why there is nothing: "download the Classic baseline ...", "map data not read ..."
        IEnumerable<GatherEntity> Entities(string profession);
        ZoneInfo Zone(int uiMapId);
    }

    public sealed class GatherFind
    {
        public string Name, Link, Label;
        public List<int> Items = new List<int>();
        public int Count;
        public int EstSkill;
    }

    public sealed class GatherZone
    {
        public ZoneInfo Zone;
        public int Score;
        public string Label = "";
        public List<GatherFind> Finds = new List<GatherFind>();
    }

    public sealed class GatherBracket
    {
        public int From, To;
        public List<GatherZone> Zones = new List<GatherZone>();
    }

    public sealed class GatherGuide
    {
        public Profession Profession;
        public int Start, Target;
        public readonly List<GatherBracket> Brackets = new List<GatherBracket>();
        public readonly List<string> Notes = new List<string>();
        public string Unavailable;                         // set when the data to build it is not there
        public Func<int, string> ItemName = id => "item " + id;

        public string ToText()
        {
            var sb = new StringBuilder();
            sb.AppendLine(Profession.Name + " " + SkillGuide.Range(Start, Target) + " - where to go");
            if (Unavailable != null) { sb.AppendLine(Unavailable); return sb.ToString(); }
            foreach (var b in Brackets)
            {
                sb.AppendLine().AppendLine(SkillGuide.Range(b.From, b.To) + ":");
                if (b.Zones.Count == 0) sb.AppendLine("  no zone known for this bracket");
                foreach (var z in b.Zones)
                {
                    sb.AppendLine("  " + z.Zone.Name + (z.Zone.KnownLevel ? " (level " + z.Zone.MinLevel + "–" + z.Zone.MaxLevel + ")" : "") + "  [" + z.Label + "]");
                    foreach (var f in z.Finds)
                        sb.AppendLine("    " + f.Name + (f.Items.Count > 0 ? " - " + string.Join(", ", f.Items.Take(4).Select(ItemName)) : "") + "  (" + f.Count + " " + (f.Count == 1 ? "spot" : "spots/seen") + ", skill ~" + f.EstSkill + ")");
                }
            }
            foreach (var n in Notes) sb.AppendLine().AppendLine(n);
            return sb.ToString();
        }
    }

    public static class GatherGuideBuilder
    {
        public const int BracketWidth = 50;

        // skinning: the skill needed to skin a creature of that level
        public static int SkinSkill(int level) => level <= 10 ? 1 : level <= 20 ? 10 * level - 100 : 5 * level;
        // a node in a zone of that median creature level
        public static int NodeSkill(int zoneMedian, int cap) => Math.Max(1, Math.Min(cap, 5 * zoneMedian));

        public static GatherGuide Build(ProfessionBook book, Profession prof, int start, int target, IGatherWorld world)
        {
            start = Math.Max(1, start);
            int cap = prof.MaxCap;
            var g = new GatherGuide { Profession = prof, Start = start, Target = Math.Max(start + 1, Math.Min(target, cap)) };
            g.ItemName = id => book?.Item(id)?.Name ?? "item " + id;
            if (world == null || !world.Available) { g.Unavailable = world?.Missing ?? "No place data."; return g; }
            bool skin = string.Equals(prof.Name, "Skinning", StringComparison.OrdinalIgnoreCase);

            // (zone, est skill) -> entity hits
            var cells = new Dictionary<Tuple<int, int>, List<Tuple<GatherEntity, int, string>>>();     // key: bracket index, zone -> (entity, count, label)
            foreach (var e in world.Entities(prof.Name))
            {
                if (skin)
                {
                    int est = Math.Min(cap, SkinSkill(e.Level));
                    foreach (var z in e.ClassicZones) Put(cells, est, z.Key, e, z.Value, SourceLabels.Classic);
                    foreach (var z in e.LodgeZones) Put(cells, est, z.Key, e, z.Value, SourceLabels.Lodge);
                }
                else
                {
                    foreach (var z in e.ClassicZones) { var zi = world.Zone(z.Key); if (zi != null && zi.KnownLevel) Put(cells, NodeSkill(zi.Median, cap), z.Key, e, z.Value, SourceLabels.Classic); }
                    foreach (var z in e.LodgeZones) { var zi = world.Zone(z.Key); if (zi != null && zi.KnownLevel) Put(cells, NodeSkill(zi.Median, cap), z.Key, e, z.Value, SourceLabels.Lodge); }
                }
            }

            for (int idx = 0; idx * BracketWidth < cap; idx++)
            {
                int from = idx == 0 ? 1 : idx * BracketWidth + 1, to = Math.Min(cap, (idx + 1) * BracketWidth);
                if (to < start || from > g.Target) continue;
                var b = new GatherBracket { From = from, To = to };
                var perZone = cells.Where(c => c.Key.Item1 == idx).GroupBy(c => c.Key.Item2);
                foreach (var zg in perZone)
                {
                    var zone = world.Zone(zg.Key) ?? new ZoneInfo { Id = zg.Key, Name = "zone " + zg.Key };
                    var gz = new GatherZone { Zone = zone };
                    var hits = zg.SelectMany(c => c.Value).ToList();
                    var labels = hits.Select(h => h.Item3).Distinct().OrderBy(x => x == SourceLabels.Classic ? 0 : 1).ToList();
                    gz.Label = string.Join(" + ", labels);
                    foreach (var f in hits.GroupBy(h => h.Item1))
                    {
                        var e = f.Key;
                        gz.Finds.Add(new GatherFind { Name = e.Name, Link = e.Link, Items = e.Items.Take(6).ToList(), Count = f.Sum(x => x.Item2), EstSkill = skin ? Math.Min(cap, SkinSkill(e.Level)) : NodeSkill(zone.Median, cap), Label = string.Join(" + ", f.Select(x => x.Item3).Distinct()) });
                    }
                    gz.Finds = gz.Finds.OrderByDescending(x => x.Count).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Take(5).ToList();
                    gz.Score = hits.Sum(h => h.Item2);
                    b.Zones.Add(gz);
                }
                b.Zones = b.Zones.OrderByDescending(z => z.Score).ThenBy(z => z.Zone.Name, StringComparer.OrdinalIgnoreCase).Take(4).ToList();
                g.Brackets.Add(b);
            }
            g.Notes.Add(skin
                ? "Skinning skill is estimated from the creature's level (level 10 or less: skill 1; levels 11-20: 10 x level - 100; above: 5 x level). The game data has no skill requirement per creature."
                : "The skill a place wants is estimated as 5 x the median level of the creatures around it. The game data has no skill requirement per herb, vein or hole, so use this as a guide to where to go, not as a promise.");
            g.Notes.Add("Places and counts come from the Classic baseline (unverified for WoW Forever) and, where marked, from what Lodge members saw on Forever.");
            return g;
        }

        static void Put(Dictionary<Tuple<int, int>, List<Tuple<GatherEntity, int, string>>> cells, int est, int zone, GatherEntity e, int count, string label)
        {
            int idx = Math.Max(0, (est - 1) / BracketWidth);
            var key = Tuple.Create(idx, zone);
            if (!cells.TryGetValue(key, out var l)) cells[key] = l = new List<Tuple<GatherEntity, int, string>>();
            l.Add(Tuple.Create(e, count, label));
        }
    }

    // a hand-made world for the self-test and for a PC without the downloaded data
    public sealed class FixedGatherWorld : IGatherWorld
    {
        public bool Available { get; set; } = true;
        public string Missing { get; set; }
        public readonly Dictionary<string, List<GatherEntity>> ByProfession = new Dictionary<string, List<GatherEntity>>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<int, ZoneInfo> Zones = new Dictionary<int, ZoneInfo>();
        public IEnumerable<GatherEntity> Entities(string profession) => ByProfession.TryGetValue(profession, out var l) ? l : new List<GatherEntity>();
        public ZoneInfo Zone(int uiMapId) => Zones.TryGetValue(uiMapId, out var z) ? z : null;
    }
}
