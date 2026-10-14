using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ElansAddonHub.Services.Atlas;

namespace ElansAddonHub.Services.Professions
{
    // Turns the guides of the Professions page into what the game gets (SharedDataFile's GuideOut, contract "profguide" part D):
    //   crafting guide  -> train / craft steps (recipe spell, product, suggested crafts, reagents for them),
    //   gathering guide -> a train step per rank and one gather step per skill bracket (objects, zones, what it gives), with a closed loop per zone
    //                      (Forever nodes first, the Classic baseline's positions only when there are fewer than 8 Forever nodes; or the saved route the
    //                      user chose for that zone) and the known nodes for map pins (Forever with their real count, Classic with n = 0).
    public static class GuideHandoff
    {
        public static string Slug(string s) => new string((s ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

        // settings key of "Use for guide": profession + zone
        public static string RouteKey(string profession, int zone) => Slug(profession) + ":" + zone.ToString(CultureInfo.InvariantCulture);

        static string Title(Profession p, int from, int to) => p.Name + " " + from.ToString(CultureInfo.InvariantCulture) + "-" + to.ToString(CultureInfo.InvariantCulture);

        public static GuideOut FromSkillGuide(Profession prof, SkillGuide g)
        {
            var o = new GuideOut { SkillId = prof.Id, Name = prof.Name, Max = prof.MaxCap, Title = Title(prof, g.Start, Math.Max(g.Reached, g.Start + 1)) };
            foreach (var s in g.Steps)
            {
                if (s.IsMilestone)
                {
                    o.Steps.Add(new GuideStepOut { K = "train", From = s.From, Text = (s.Train?.Name ?? "Next rank") + " " + prof.Name });
                    continue;
                }
                var r = s.Recipe;
                var st = new GuideStepOut { K = "craft", From = s.From, To = s.To, Spell = r.SpellId, Item = r.ItemId, N = Math.Max(1, s.Crafts), Text = r.Name };
                st.Reagents = s.Materials.Where(m => m.ItemId > 0 && m.Count > 0).Take(SharedDataFile.MaxReagents).Select(m => new[] { m.ItemId, m.Count }).ToList();
                if (s.LearnItemId > 0 && g.ItemName != null) st.Text = r.Name + " (learn: " + g.ItemName(s.LearnItemId) + ")";
                o.Steps.Add(st);
            }
            return o;
        }

        // `choice`: RouteKey -> id of the SavedRoute the user picked for that zone; `saved`: all saved routes
        public static GuideOut FromGatherGuide(Profession prof, GatherGuide g, INodeSource nodes, IDictionary<string, string> choice = null, IEnumerable<SavedRoute> saved = null)
        {
            var o = new GuideOut { SkillId = prof.Id, Name = prof.Name, Max = prof.MaxCap, Title = Title(prof, g.Start, g.Target) };
            var savedList = (saved ?? Enumerable.Empty<SavedRoute>()).ToList();
            var steps = new List<GuideStepOut>();
            var ranks = prof.Ranks.OrderBy(r => r.Cap).ToList();
            for (int i = 0; i < ranks.Count; i++)
            {
                int from = i == 0 ? 1 : ranks[i - 1].Cap;
                if (from < g.Start || from > g.Target) continue;
                steps.Add(new GuideStepOut { K = "train", From = from, Text = ranks[i].Name + " " + prof.Name });
            }
            var gather = new List<(GuideStepOut step, List<int> zoneIds, Dictionary<int, List<int>> objsByZone)>();
            foreach (var br in g.Brackets)
            {
                if (br.Zones.Count == 0) continue;
                var objsByZone = new Dictionary<int, List<int>>();
                foreach (var z in br.Zones)
                    objsByZone[z.Zone.Id] = z.Finds.Where(f => f.ObjectId > 0).Select(f => f.ObjectId).Distinct().ToList();
                var allObjs = objsByZone.Values.SelectMany(x => x).Distinct().Take(SharedDataFile.MaxObjectsPerStep).ToList();
                var firstFind = br.Zones.SelectMany(z => z.Finds).FirstOrDefault();
                var names = br.Zones.SelectMany(z => z.Finds).Select(f => f.Name).Where(n => !string.IsNullOrEmpty(n)).Distinct().Take(3).ToList();
                var st = new GuideStepOut
                {
                    K = "gather", From = br.From, To = br.To, Item = br.Zones.SelectMany(z => z.Finds).SelectMany(f => f.Items).FirstOrDefault(),
                    Objects = allObjs, Zones = br.Zones.Select(z => z.Zone.Id).Where(z => z > 0).Take(SharedDataFile.MaxZonesPerStep).ToList(),
                    Text = string.Join(", ", names) + (br.Zones.Count > 0 ? " - " + string.Join(", ", br.Zones.Select(z => z.Zone.Name).Take(2)) : ""),
                };
                steps.Add(st);
                gather.Add((st, st.Zones, objsByZone));
            }
            // train steps first at the same skill, then in skill order
            o.Steps = steps.Select((s, i) => new { s, i }).OrderBy(x => x.s.From).ThenBy(x => x.s.K == "train" ? 0 : 1).ThenBy(x => x.i).Select(x => x.s).Take(SharedDataFile.MaxSteps).ToList();
            foreach (var (step, zoneIds, objsByZone) in gather)
            {
                int index = o.Steps.IndexOf(step) + 1;
                if (index <= 0) continue;
                foreach (var zone in zoneIds)
                {
                    var objs = objsByZone.TryGetValue(zone, out var l) ? l : new List<int>();
                    // the route: the saved one the user picked for this profession and zone, else the one built from the nodes
                    GuideRouteOut route = null;
                    if (choice != null && choice.TryGetValue(RouteKey(prof.Name, zone), out var rid))
                    {
                        var sr = savedList.FirstOrDefault(x => x.Id == rid && x.Zone == zone);
                        var pts = sr?.ToNodes();
                        if (pts != null && pts.Count > 0) route = new GuideRouteOut { Src = sr.Src == "c" ? "c" : "f", Points = pts.Take(SharedDataFile.MaxRoutePoints).Select(p => new[] { p.X, p.Y }).ToList() };
                    }
                    if (route == null && objs.Count > 0 && nodes != null)
                    {
                        var rr = GatherRoutes.Build(nodes, objs, zone);
                        if (rr.Count > 0) route = new GuideRouteOut { Src = rr.Src, Points = rr.Points.Take(SharedDataFile.MaxRoutePoints).Select(p => new[] { p.X, p.Y }).ToList() };
                    }
                    if (route != null)
                    {
                        if (!o.Routes.TryGetValue(index, out var byZone)) o.Routes[index] = byZone = new Dictionary<int, GuideRouteOut>();
                        byZone[zone] = route;
                    }
                    // the pins: Forever nodes with their real count first, the Classic positions (n = 0) after them
                    if (nodes != null)
                        foreach (var obj in objs.Take(SharedDataFile.MaxObjectsPerStep))
                        {
                            var pins = nodes.Forever(obj, zone).OrderByDescending(n => n.N).Select(n => new[] { n.X, n.Y, (double)Math.Max(1, n.N) }).ToList();
                            if (pins.Count < SharedDataFile.MaxNodesPerObjectMap)
                                pins.AddRange(nodes.Classic(obj, zone).Where(c => !pins.Any(p => Math.Abs(p[0] - c.X) < 0.004 && Math.Abs(p[1] - c.Y) < 0.004)).Select(n => new[] { n.X, n.Y, 0.0 }));
                            pins = pins.Take(SharedDataFile.MaxNodesPerObjectMap).ToList();
                            if (pins.Count == 0) continue;
                            if (!o.Nodes.TryGetValue(obj, out var byZ)) o.Nodes[obj] = byZ = new Dictionary<int, List<double[]>>();
                            byZ[zone] = pins;
                        }
                }
            }
            return o;
        }
    }

    // A guide the user chose to follow in game: one per character and profession.
    [System.Runtime.Serialization.DataContract]
    public sealed class FollowedGuide
    {
        [System.Runtime.Serialization.DataMember(Name = "char")] public string CharKey { get; set; }       // "Realm-Name"
        [System.Runtime.Serialization.DataMember(Name = "skill")] public int SkillId { get; set; }
        [System.Runtime.Serialization.DataMember(Name = "camp")] public bool Camp { get; set; }
        public const int MaxFollowed = 40;
    }
}
