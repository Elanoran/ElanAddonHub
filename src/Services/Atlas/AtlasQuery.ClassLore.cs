using System;
using System.Collections.Generic;
using System.Linq;
using ElansAddonHub.Services.Atlas.Classic;
using ElansAddonHub.Services.Db2;

namespace ElansAddonHub.Services.Atlas
{
    // The "About <class>" / "About <race>" card's content (2.81): the game's own character creation text, the Outpost's notes (ClassLore) with
    // their notable figures linked to creature pages where the game data has exactly that creature, and "At a glance" lines taken from the
    // page's own game data (races / classes, armor, weapons, racial abilities, the starting zone). Built last, from what the page already holds.
    public static partial class AtlasQuery
    {
        static void AddClassLore(AtlasPageModel m, ClassInfo c)
        {
            var p = new ClassLorePage { Title = c.Name, Official = (c.Description ?? "").Trim(), Notes = ClassLore.ForClass(c.File) };
            Glance(p, "Races", m.Section("races"), AtlasLabel.Forever);
            Glance(p, "Armor", m.Section("armor"), AtlasLabel.Forever, true);
            Glance(p, "Weapons", m.Section("weapons"), AtlasLabel.Forever, true);
            RaceOnlyGlance(p, c);
            Finish(m, p);
        }

        static void AddRaceLore(AtlasPageModel m, RaceInfo r)
        {
            var p = new ClassLorePage { Title = r.Name, Official = (r.Description ?? "").Trim(), Notes = ClassLore.ForRace(r.Name) };
            var start = m.Facts.FirstOrDefault(f => f.Label == "Starts in");
            if (start != null) p.Glance.Add(new ClassLoreGlance { Label = "Starts in", Source = start.Source, Items = { new KeyValuePair<string, string>(start.Value, start.Link) } });
            Glance(p, "Classes", m.Section("classes"), AtlasLabel.Forever);
            Glance(p, "Racial abilities", m.Section("racial"), AtlasLabel.Forever);
            Finish(m, p);
        }

        // a table of the page as one line of names (with "from level N" where the row says so)
        static void Glance(ClassLorePage p, string label, AtlasSection s, string source, bool withDetail = false)
        {
            if (s == null || s.Rows.Count == 0) return;
            var g = new ClassLoreGlance { Label = label, Source = source };
            foreach (var row in s.Rows)
            {
                if (g.Items.Any(i => i.Key == row.Title || i.Key.StartsWith(row.Title + " (", StringComparison.Ordinal))) continue;   // one name once (Forever's per-class variants of a racial)
                var text = withDetail && row.Detail.StartsWith("From level ", StringComparison.Ordinal) ? row.Title + " (" + row.Detail.Substring(0, 1).ToLowerInvariant() + row.Detail.Substring(1) + ")" : row.Title;
                g.Items.Add(new KeyValuePair<string, string>(text, row.Link));
            }
            p.Glance.Add(g);
        }

        // "Race-only spells": the class spells the client gives one race only (SkillLineAbility.RaceMask), with that race - Forever's priests have two
        // each (Feedback and Divine Grace for Humans, Chastise and Desperate Prayer for Dwarves, Confounding Flash and Contingency Plan for Gnomes ...).
        // A spell a whole side or several races share (mage portals, the paladin mounts) is not race-only; a spell without a learn level (runes) is left out.
        static void RaceOnlyGlance(ClassLorePage p, ClassInfo c)
        {
            var g = new ClassLoreGlance { Label = "Race-only spells", Source = AtlasLabel.Forever };
            foreach (var x in RaceOnlySpells(c)) g.Items.Add(new KeyValuePair<string, string>(x.Name + " (" + x.Race.Name + ")", AtlasIds.Spell(x.Spell)));
            if (g.Items.Count > 0) p.Glance.Add(g);
        }

        public sealed class RaceOnlySpell { public string Name; public int Spell, Level; public RaceInfo Race; }

        public static List<RaceOnlySpell> RaceOnlySpells(ClassInfo c)
        {
            var res = new List<RaceOnlySpell>();
            var snap = ClassData.Data; var spells = SpellData.Spells;
            if (snap == null || spells == null || c == null || !snap.HasSkillRows) return res;
            Func<int, int> catOf = id => spells.Skills.TryGetValue(id, out var sk) ? sk.Category : 0;
            var races = snap.RacesOf(c.Id);
            var byName = new Dictionary<string, List<KeyValuePair<SkillAbility, int>>>(StringComparer.OrdinalIgnoreCase);
            foreach (var skill in snap.TabSkills(c.Id, catOf))
                foreach (var a in spells.AbilitiesOf(skill))
                {
                    if (a.RaceMask == 0 || (a.ClassMask != 0 && a.ClassMask != -1 && (a.ClassMask & c.Mask) == 0)) continue;
                    var sp = spells.Get(a.Spell);
                    if (sp == null || string.IsNullOrEmpty(sp.Name)) continue;
                    int lvl = snap.Level(a.Spell)?.Base ?? 0;
                    if (!byName.TryGetValue(sp.Name, out var l)) byName[sp.Name] = l = new List<KeyValuePair<SkillAbility, int>>();
                    l.Add(new KeyValuePair<SkillAbility, int>(a, lvl));
                }
            foreach (var kv in byName)
            {
                var who = races.Where(r => kv.Value.Any(a => (a.Key.RaceMask & r.BitMask) != 0)).ToList();
                var learned = kv.Value.Where(a => a.Value > 0).ToList();
                if (who.Count != 1 || learned.Count == 0) continue;
                var first = learned.OrderBy(a => a.Value).ThenBy(a => a.Key.Spell).First();
                res.Add(new RaceOnlySpell { Name = kv.Key, Spell = first.Key.Spell, Level = first.Value, Race = who[0] });
            }
            var order = races.Select((r, i) => new { r.Id, i }).ToDictionary(x => x.Id, x => x.i);
            return res.OrderBy(x => order[x.Race.Id]).ThenBy(x => x.Level).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        static void Finish(AtlasPageModel m, ClassLorePage p)
        {
            if (p.Notes != null) foreach (var f in p.Notes.Figures) p.Figures.Add(new ClassLoreFigureLink { Figure = f, Link = FigureLink(f.Name) });
            if (p.Any) m.ClassLore = p;
        }

        static string NpcLink(int id) => "npc:" + id.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // a figure of the notes links to its creature page when the data knows exactly one creature of that name: first the zone notes' checked
        // creatures (ZoneLore), then the Classic baseline (one creature of the name, or one of them with spawn points). Else plain text.
        public static string FigureLink(string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) return null;
            try
            {
                var z = ZoneLore.All.SelectMany(e => e.Mobs).Where(x => x.Linked && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)).Select(x => x.Id).Distinct().ToList();
                if (z.Count == 1) return NpcLink(z[0]);
                var ci = ClassicAtlas.Current;
                if (ci == null) return null;
                var all = ci.Npcs.Values.Where(n => string.Equals((n.Name ?? "").Trim(), name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (all.Count == 1) return NpcLink(all[0].Id);
                var placed = all.Where(n => n.Spawns != null && n.Spawns.Length > 0).ToList();
                if (placed.Count == 1) return NpcLink(placed[0].Id);
            }
            catch (Exception e) { Util.Log("class notes: figure " + name + ": " + e.Message); }
            return null;
        }
    }
}
