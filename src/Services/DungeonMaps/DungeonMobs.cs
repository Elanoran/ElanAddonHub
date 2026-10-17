using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ElansAddonHub.Services.Atlas.Classic;
using ElansAddonHub.Services.Db2;

namespace ElansAddonHub.Services.DungeonMaps
{
    // ============================================================================================================================
    // The creatures of a drawn instance on its floors (Classic baseline positions, DungeonMobData), grouped into pulls, with the bosses in order, plain
    // warnings that come only from what the data says, and a suggested route from the entrance past every boss.
    //   placing   each spawn goes to the floor whose walkable grid (DungeonNav) holds its position; else the plan's FloorAt
    //   left out  critters and totems, triggers and other unseen helpers (not selectable, flagged invisible, "Trigger" / "[...]" names), creatures friendly
    //             to both sides (the game's FactionTemplate)
    //   bosses    the creatures whose kill counts for an encounter of the map (instance_dungeon_encounters, in the game's order), or that bind the
    //             instance when killed (extra flag 0x1), or of rank "boss"
    //   rares     rank rare / rare elite
    //   pulls     spawns within PackYards (and PackHeight up or down) on one floor are one pull; linked spawns (creature_linking) join their master's
    //             pull wherever they stand; a creature walking a path is a patrol, its own pull unless linked
    //   warnings  from the spells a creature has (creature_template_spells, its spell list, the spells its script casts) read through spell_template:
    //             auras and mechanics (fear, mind control, silence, stun, incapacitate), dispel types of the debuffs it casts (curse, disease, poison),
    //             heals, summons, knockback, mana drain, stealth; and from its script: running for help, calling for help. Each says the spell's name.
    //   route     the walk from where the entrance puts you past every boss that has a place: the shortest order (exact up to 13 bosses), each leg the
    //             cheapest walk on the grid, where a step close to a pull that is not a stop costs AvoidCost times as much (so it goes around packs when
    //             it can). A suggestion worked out from the map, not a guide: doors, levers and events are not in the data.
    // ============================================================================================================================
    public enum MobPackKind { Boss, Pack, Rare }

    public sealed class MobWarning
    {
        public string Kind = "", Text = "";
        public bool Badge;                          // worth a "!" on the map: it changes how the pull goes
        public override string ToString() => Text;
    }

    public sealed class MobMember
    {
        public int Entry, Count, MinLevel, MaxLevel, Rank;
        public string Name = "";
        public bool Boss;
        public string Levels => MinLevel == MaxLevel ? MinLevel.ToString(CultureInfo.InvariantCulture) : MinLevel.ToString(CultureInfo.InvariantCulture) + "-" + MaxLevel.ToString(CultureInfo.InvariantCulture);
        public string RankName => Rank == 1 ? "Elite" : Rank == 2 ? "Rare Elite" : Rank == 3 ? "Boss" : Rank == 4 ? "Rare" : "";
    }

    public sealed class MobPack
    {
        public readonly List<MobMember> Members = new List<MobMember>();   // each kind of creature once, the lead first
        public MobPackKind Kind;
        public int Floor = -1, Node = -1;
        public double X, Y, Z;
        public readonly List<MobSpawn> Spawns = new List<MobSpawn>();
        public bool Patrol;
        public float[] Path = new float[0];                     // the patrol's walk (x, y, z per point)
        public int Order;                                       // bosses: 1, 2, 3 ... (the route's order, else the game's); 0 = not a boss
        public int EncounterIndex = -1;                         // the game's encounter list (0-based), -1 = not in it
        public int Lead;                                        // the creature the marker opens (the boss, the rare, else the highest level)
        public string Name = "";                               // the lead's name
        public readonly List<MobWarning> Warnings = new List<MobWarning>();
        public bool Badge => Warnings.Any(w => w.Badge);
        public int Count => Spawns.Count;
    }

    public sealed class MobRoutePoint { public int Floor; public double X, Y, Z; public int Stop = -1; public bool Passage; }   // Stop: the boss reached here (index into Bosses); Passage: the way here is a passage the plan has no floor for (DungeonNav.Links)

    public sealed class DungeonMobLayer
    {
        public int MapId;
        public readonly List<MobPack> Packs = new List<MobPack>();
        public readonly List<MobPack> Bosses = new List<MobPack>();          // in route order (else the game's)
        public readonly List<string> BossesWithoutPlace = new List<string>(); // encounters whose creature has no spawn on the map (they come with an event)
        public readonly List<string> Unreached = new List<string>();          // bosses the walkable grid does not reach from the entrance
        public readonly List<MobRoutePoint> Route = new List<MobRoutePoint>();
        public MobEntrance Entrance;
        public int EntranceFloor = -1;
        public string RouteNote = "";                                         // why there is no route, or what it leaves out
        public int Spawns, LeftOut;
        public double RouteYards;
        public long Ms;
    }

    public static class MobWarnings
    {
        // spell_template values (1.12): auras, effects, mechanics, dispel types
        const int AuraConfuse = 5, AuraCharm = 6, AuraFear = 7, AuraPeriodicHeal = 8, AuraStun = 12, AuraStealth = 16, AuraSilence = 27, AuraAoeCharm = 177;
        const int EffApplyAura = 6, EffPowerDrain = 8, EffHeal = 10, EffSummon = 28, EffSummonWild = 41, EffSummonGuardian = 42, EffPowerBurn = 62, EffKnockBack = 98;
        const int MechCharm = 1, MechFear = 5, MechSilence = 9, MechSleep = 10, MechStun = 12, MechPolymorph = 17, MechHorror = 24;
        const int DispelCurse = 2, DispelDisease = 3, DispelPoison = 4;

        // an effect of the spell matches (hostile: only effects that land on someone other than the caster count, and the spell-wide mechanic only when
        // one of its effects does)
        static bool Has(MobSpell s, Func<int, bool> aura = null, Func<int, bool> effect = null, Func<int, bool> mech = null, bool hostile = false)
        {
            bool any = false;
            for (int k = 0; k < 3; k++)
            {
                if (s.Effect[k] == 0) continue;
                if (hostile && s.OnSelf(k)) continue;
                any = true;
                if (aura != null && s.Effect[k] == EffApplyAura && aura(s.Aura[k])) return true;
                if (aura != null && s.Effect[k] != EffApplyAura && s.Aura[k] != 0 && aura(s.Aura[k])) return true;   // area auras carry their aura too
                if (effect != null && effect(s.Effect[k])) return true;
                if (mech != null && mech(s.EffectMechanic[k])) return true;
            }
            return any && mech != null && mech(s.Mechanic);
        }

        static bool AppliesAura(MobSpell s) { for (int k = 0; k < 3; k++) if (s.Aura[k] != 0 && !s.OnSelf(k)) return true; return false; }

        // the warnings of one creature, from its data; tmpl names summoned creatures (null = by spell name only)
        public static List<MobWarning> Of(MobTemplate t, Func<int, MobSpell> spell, Func<int, MobTemplate> tmpl, bool boss = false)
        {
            var found = new List<KeyValuePair<string, string>>();        // kind, spell name
            void Add(string kind, string name) { if (!found.Any(f => f.Key == kind && f.Value == name)) found.Add(new KeyValuePair<string, string>(kind, name ?? "")); }
            foreach (var id in t.Spells)
            {
                var s = spell(id); if (s == null) continue;
                var n = string.IsNullOrWhiteSpace(s.Name) ? "spell " + id : s.Name.Trim();
                bool sleepy = Has(s, mech: m => m == MechPolymorph || m == MechSleep, hostile: true);
                if (Has(s, aura: a => a == AuraCharm || a == AuraAoeCharm, mech: m => m == MechCharm, hostile: true)) Add("Mind control", n);
                else if (Has(s, aura: a => a == AuraFear, mech: m => m == MechFear || m == MechHorror, hostile: true)) Add("Fear", n);
                if (Has(s, aura: a => a == AuraSilence, mech: m => m == MechSilence, hostile: true)) Add("Silence", n);
                if (sleepy || Has(s, aura: a => a == AuraConfuse, hostile: true)) Add("Incapacitate", n);
                else if (Has(s, aura: a => a == AuraStun, mech: m => m == MechStun, hostile: true)) Add("Stun", n);
                if (AppliesAura(s) && s.Dispel == DispelCurse) Add("Curse", n);
                if (AppliesAura(s) && s.Dispel == DispelDisease) Add("Disease", n);
                if (AppliesAura(s) && s.Dispel == DispelPoison) Add("Poison", n);
                if (n.IndexOf("Enrage", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Frenzy", StringComparison.OrdinalIgnoreCase) >= 0) Add("Enrage", n);
                if (Has(s, effect: e => e == EffHeal, aura: a => a == AuraPeriodicHeal)) Add("Heals", n);
                if (Has(s, effect: e => e == EffSummon || e == EffSummonWild || e == EffSummonGuardian || (e >= 87 && e <= 90))) Add("Summons", n);
                if (Has(s, effect: e => e == EffKnockBack, hostile: true)) Add("Knockback", n);
                if (Has(s, effect: e => e == EffPowerDrain || e == EffPowerBurn, hostile: true)) Add("Mana drain", n);
                if (Has(s, aura: a => a == AuraStealth)) Add("Stealth", n);
            }
            foreach (var id in t.Auras) { var s = spell(id); if (s != null && Has(s, aura: a => a == AuraStealth)) Add("Stealth", s.Name); }
            foreach (var id in t.Summons) { var m = tmpl?.Invoke(id); if (m != null && !string.IsNullOrWhiteSpace(m.Name)) Add("Summons", m.Name.Trim()); }

            var res = new List<MobWarning>();
            foreach (var g in found.GroupBy(f => f.Key))
            {
                var names = string.Join(", ", g.Select(f => f.Value).Where(v => v.Length > 0).Distinct().Take(3));
                string text;
                switch (g.Key)
                {
                    case "Mind control": text = "Mind control: " + names; break;
                    case "Fear": text = "Fears: " + names; break;
                    case "Silence": text = "Silences: " + names; break;
                    case "Stun": text = "Stuns: " + names; break;
                    case "Incapacitate": text = "Incapacitates: " + names; break;
                    case "Curse": text = "Curse: " + names; break;
                    case "Disease": text = "Disease: " + names; break;
                    case "Poison": text = "Poison: " + names; break;
                    case "Enrage": text = "Enrages: " + names; break;
                    case "Heals": text = "Heals: " + names; break;
                    case "Summons": text = "Summons: " + names; break;
                    case "Knockback": text = "Knocks back: " + names; break;
                    case "Mana drain": text = "Drains mana: " + names; break;
                    default: text = "Stealthed until it attacks" + (names.Length > 0 ? " (" + names + ")" : ""); break;
                }
                res.Add(new MobWarning { Kind = g.Key, Text = text, Badge = g.Key == "Mind control" || g.Key == "Fear" || g.Key == "Silence" || g.Key == "Heals" || g.Key == "Summons" || g.Key == "Stealth" });
            }
            if (t.Flees) res.Add(new MobWarning { Kind = "Runs", Text = "Runs for help at low health", Badge = true });
            if (t.CallsHelp) res.Add(new MobWarning { Kind = "Calls", Text = "Calls nearby allies for help", Badge = true });
            if (!boss)
            {
                var imm = new List<string>();
                if ((t.MechanicImmune & (1 << (MechFear - 1))) != 0) imm.Add("fear");
                if ((t.MechanicImmune & (1 << (MechStun - 1))) != 0) imm.Add("stuns");
                if ((t.MechanicImmune & (1 << (MechPolymorph - 1))) != 0) imm.Add("polymorph");
                if (imm.Count > 0) res.Add(new MobWarning { Kind = "Immune", Text = "Immune to " + string.Join(", ", imm) });
            }
            var schools = new[] { "Holy", "Fire", "Nature", "Frost", "Shadow", "Arcane" };
            var sch = schools.Where((n, i) => (t.SchoolImmune & (2 << i)) != 0).ToList();
            if (sch.Count > 0 && sch.Count < 6) res.Add(new MobWarning { Kind = "School", Text = "Immune to " + string.Join(", ", sch) });
            return res;
        }
    }

    public static class DungeonMobLayers
    {
        public const double PackYards = 10, PackHeight = 5, AvoidYards = 9, AvoidCost = 5, LinkYards = 30;
        public const int ExactUpTo = 13;

        // spawns that are not something to fight: critters (8), totems (11), unseen helpers, creatures friendly to both sides
        public static bool LeftOut(MobTemplate t, bool boss, Func<MobTemplate, bool> friendly)
        {
            if (t == null) return true;
            if (boss) return false;
            if (t.Type == 8 || t.Type == 11) return true;
            if ((t.UnitFlags & 0x02000000) != 0 || (t.ExtraFlags & 0x80) != 0) return true;
            var n = t.Name ?? "";
            if (n.Length == 0 || n.IndexOf("Trigger", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf('[') >= 0 || n.IndexOf("Invisible", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return friendly != null && friendly(t);
        }

        // friendly to both sides by the game's FactionTemplate; without the table, a creature that offers a service (vendor, quest, gossip, trainer)
        public static bool FriendlyToAll(MobTemplate t)
        {
            if (FactionData.Ready && FactionData.Template(t.Faction) != null)
                return FactionData.ToAlliance(t.Faction) == Standing.Friendly && FactionData.ToHorde(t.Faction) == Standing.Friendly;
            return (t.NpcFlags & 0x3FFF) != 0;
        }

        sealed class Placed { public MobSpawn S; public MobTemplate T; public int Floor, Node; public bool Boss, Rare, Patrol; }

        public static DungeonMobLayer Build(int mapId, DungeonPlan plan, DungeonMobData data, Func<MobTemplate, bool> friendly = null, CancellationToken ct = default(CancellationToken))
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var layer = new DungeonMobLayer { MapId = mapId };
            var nav = plan?.Nav();
            var enc = data.EncountersOf(mapId).ToList();
            var credit = new Dictionary<int, MobEncounter>();
            foreach (var e in enc) if (e.CreditNpc > 0 && !credit.ContainsKey(e.CreditNpc)) credit[e.CreditNpc] = e;

            int FloorOf(double x, double y, double z, out int node)
            {
                node = nav == null ? -1 : nav.Nearest(x, y, z, 5, 5);
                if (node >= 0) return nav.Floor[node];
                return plan == null ? 0 : plan.FloorAt(x, y, z);
            }

            // place
            var placed = new List<Placed>();
            foreach (var s in data.SpawnsOn(mapId))
            {
                var t = data.Template(s.Entry);
                bool boss = t != null && (credit.ContainsKey(t.Id) || t.BindsInstance || t.Rank == 3);
                layer.Spawns++;
                if (LeftOut(t, boss, friendly)) { layer.LeftOut++; continue; }
                int f = FloorOf(s.X, s.Y, s.Z, out var node);
                placed.Add(new Placed { S = s, T = t, Floor = f, Node = node, Boss = boss, Rare = !boss && (t.Rank == 2 || t.Rank == 4), Patrol = s.Movement == 2 && s.Path.Length >= 6 });
            }
            ct.ThrowIfCancellationRequested();

            // pulls
            int n = placed.Count;
            var parent = Enumerable.Range(0, n).ToArray();
            int Find(int a) { while (parent[a] != a) { parent[a] = parent[parent[a]]; a = parent[a]; } return a; }
            void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[Math.Max(a, b)] = Math.Min(a, b); }
            for (int i = 0; i < n; i++)
            {
                var a = placed[i]; if (a.Rare || a.Patrol) continue;
                for (int j = i + 1; j < n; j++)
                {
                    var b = placed[j]; if (b.Rare || b.Patrol || b.Floor != a.Floor) continue;
                    double dx = a.S.X - b.S.X, dy = a.S.Y - b.S.Y;
                    if (dx * dx + dy * dy <= PackYards * PackYards && Math.Abs(a.S.Z - b.S.Z) <= PackHeight) Union(i, j);
                }
            }
            var byGuid = new Dictionary<int, int>();
            for (int i = 0; i < n; i++) byGuid[placed[i].S.Guid] = i;
            for (int i = 0; i < n; i++) if (data.LinkMaster.TryGetValue(placed[i].S.Guid, out var mg) && byGuid.TryGetValue(mg, out var mi)) Union(i, mi);
            foreach (var lt in data.LinkTemplates.Where(l => l.Map == mapId))
            {
                double range = lt.Range > 0 ? lt.Range : LinkYards;
                for (int i = 0; i < n; i++)
                {
                    if (placed[i].S.Entry != lt.Entry) continue;
                    int best = -1; double bd = range * range;
                    for (int j = 0; j < n; j++)
                    {
                        if (placed[j].S.Entry != lt.Master) continue;
                        double dx = placed[i].S.X - placed[j].S.X, dy = placed[i].S.Y - placed[j].S.Y, d = dx * dx + dy * dy;
                        if (d <= bd) { bd = d; best = j; }
                    }
                    if (best >= 0) Union(i, best);
                }
            }
            foreach (var g in Enumerable.Range(0, n).GroupBy(Find))
            {
                var members = g.Select(i => placed[i]).ToList();
                var pack = new MobPack();
                foreach (var m in members) pack.Spawns.Add(m.S);
                pack.Kind = members.Any(m => m.Boss) ? MobPackKind.Boss : members.Any(m => m.Rare) ? MobPackKind.Rare : MobPackKind.Pack;
                var lead = members.FirstOrDefault(m => m.Boss) ?? members.FirstOrDefault(m => m.Rare) ?? members.OrderByDescending(m => m.T.MaxLevel).ThenByDescending(m => m.T.Rank).First();
                pack.Lead = lead.T.Id; pack.Name = lead.T.Name ?? "";
                foreach (var g2 in members.GroupBy(m => m.T.Id).OrderByDescending(x => x.Key == lead.T.Id).ThenByDescending(x => x.First().T.MaxLevel))
                {
                    var t0 = g2.First().T;
                    pack.Members.Add(new MobMember { Entry = t0.Id, Name = t0.Name ?? "", Count = g2.Count(), MinLevel = t0.MinLevel, MaxLevel = t0.MaxLevel, Rank = t0.Rank, Boss = g2.Any(m => m.Boss) });
                }
                var pat = members.FirstOrDefault(m => m.Patrol);
                if (pat != null) { pack.Patrol = true; pack.Path = pat.S.Path; }
                var anchor = pack.Kind == MobPackKind.Pack ? members : members.Where(m => m == lead).ToList();
                pack.X = anchor.Average(m => (double)m.S.X); pack.Y = anchor.Average(m => (double)m.S.Y); pack.Z = anchor.Average(m => (double)m.S.Z);
                pack.Floor = members.GroupBy(m => m.Floor).OrderByDescending(x => x.Count()).First().Key;
                pack.Node = lead.Node >= 0 ? lead.Node : members.Select(m => m.Node).FirstOrDefault(x => x >= 0);
                if (nav != null && pack.Node < 0) pack.Node = nav.Nearest(pack.X, pack.Y, pack.Z, 12, 8);
                // warnings: each kind of creature once
                foreach (var t in members.Select(m => m.T).GroupBy(t => t.Id).Select(x => x.First()))
                    foreach (var w in MobWarnings.Of(t, data.Spell, data.Template, members.Any(m => m.Boss && m.T.Id == t.Id)))
                        if (!pack.Warnings.Any(x => x.Text == w.Text)) pack.Warnings.Add(new MobWarning { Kind = w.Kind, Badge = w.Badge, Text = (members.Select(m => m.T.Id).Distinct().Count() > 1 ? t.Name + " - " : "") + w.Text });
                if (pack.Kind == MobPackKind.Boss)
                {
                    var e = members.Where(m => m.Boss && credit.ContainsKey(m.T.Id)).Select(m => credit[m.T.Id]).OrderBy(x => x.Index).FirstOrDefault();
                    pack.EncounterIndex = e?.Index ?? -1;
                }
                layer.Packs.Add(pack);
            }
            // a boss that stands in several places (a spawn per phase) is one stop: the encounter's first place
            var bosses = layer.Packs.Where(p => p.Kind == MobPackKind.Boss).OrderBy(p => p.EncounterIndex < 0 ? int.MaxValue : p.EncounterIndex).ToList();
            var seenBoss = new HashSet<int>();
            foreach (var b in bosses) if (seenBoss.Add(b.Lead)) layer.Bosses.Add(b);
            foreach (var e in enc) if (e.CreditNpc > 0 && !layer.Bosses.Any(b => b.Spawns.Any(s => s.Entry == e.CreditNpc))) layer.BossesWithoutPlace.Add(e.Name.Length > 0 ? e.Name : data.Template(e.CreditNpc)?.Name ?? "");
            for (int i = 0; i < layer.Bosses.Count; i++) layer.Bosses[i].Order = i + 1;
            ct.ThrowIfCancellationRequested();

            // where the entrance puts you
            foreach (var e in data.EntrancesOf(mapId))
            {
                int f = FloorOf(e.X, e.Y, e.Z, out var node);
                if (layer.Entrance == null || (node >= 0 && layer.EntranceFloor < 0)) { layer.Entrance = e; layer.EntranceFloor = f; }
            }
            Route(layer, nav, ct);
            layer.Ms = sw.ElapsedMilliseconds;
            return layer;
        }

        // ---------------------------------------------------------------- the route
        static void Route(DungeonMobLayer layer, DungeonNav nav, CancellationToken ct)
        {
            if (nav == null || nav.Count == 0) { layer.RouteNote = "No route: this map has no walkable grid."; return; }
            if (layer.Bosses.Count == 0) { layer.RouteNote = "No route: no bosses with a place on this map."; return; }
            int start = layer.Entrance == null ? -1 : nav.Nearest(layer.Entrance.X, layer.Entrance.Y, layer.Entrance.Z, 12, 8);
            var stops = layer.Bosses.Where(b => b.Node >= 0).ToList();
            foreach (var b in layer.Bosses.Where(b => b.Node < 0)) layer.Unreached.Add(Name(b));
            if (stops.Count == 0) { layer.RouteNote = "No route: the bosses stand off the walkable grid."; return; }
            if (start < 0) { start = stops[0].Node; layer.RouteNote = "No entrance point is known for this map: the route starts at the first boss."; }

            // walking close to a pull that is not a stop costs more
            var cost = new float[nav.Count];
            for (int i = 0; i < cost.Length; i++) cost[i] = 1;
            foreach (var p in layer.Packs)
            {
                if (p.Kind == MobPackKind.Boss) continue;
                foreach (var s in p.Spawns)
                {
                    int c = nav.Nearest(s.X, s.Y, s.Z, 4, 5); if (c < 0) continue;
                    // the nodes within AvoidYards: a small flood from the spawn's node
                    var q = new Queue<int>(); var seen = new HashSet<int> { c }; q.Enqueue(c);
                    while (q.Count > 0)
                    {
                        int u = q.Dequeue(); cost[u] = (float)AvoidCost;
                        foreach (var v in nav.Neighbours(u))
                        {
                            if (seen.Contains(v)) continue;
                            double dx = nav.WorldX(v) - s.X, dy = nav.WorldY(v) - s.Y;
                            if (dx * dx + dy * dy > AvoidYards * AvoidYards || Math.Abs(nav.Z[v] - s.Z) > PackHeight) continue;
                            seen.Add(v); q.Enqueue(v);
                        }
                    }
                }
            }
            foreach (var b in stops) cost[b.Node] = 1;

            // the walks between the entrance (0) and the stops (1..k)
            var nodes = new[] { start }.Concat(stops.Select(b => b.Node)).ToArray();
            int k = nodes.Length;
            var dist = new double[k, k]; var prevs = new int[k][];
            for (int i = 0; i < k; i++)
            {
                ct.ThrowIfCancellationRequested();
                nav.Dijkstra(nodes[i], cost, out var d, out var pv);
                prevs[i] = pv;
                for (int j = 0; j < k; j++) dist[i, j] = d[nodes[j]];
            }
            var reach = Enumerable.Range(1, k - 1).Where(j => !double.IsInfinity(dist[0, j])).ToList();
            foreach (var j in Enumerable.Range(1, k - 1).Except(reach)) layer.Unreached.Add(Name(stops[j - 1]));
            if (reach.Count == 0) { layer.RouteNote = "No route: no boss can be walked to from the entrance on the drawn floors."; return; }
            var order = Order(dist, reach);

            // the walk, leg by leg, thinned to the corners
            int at = 0; double yards = 0;
            var newOrder = new List<MobPack>();
            foreach (var j in order)
            {
                var path = nav.PathTo(prevs[at], nodes[j]);
                if (at == 0 || layer.Route.Count == 0) AddPoints(layer, nav, path);
                else AddPoints(layer, nav, path.Skip(1).ToList());
                layer.Route[layer.Route.Count - 1].Stop = newOrder.Count;
                for (int i = 1; i < path.Count; i++) yards += Math.Sqrt(Sq(nav.WorldX(path[i]) - nav.WorldX(path[i - 1])) + Sq(nav.WorldY(path[i]) - nav.WorldY(path[i - 1])) + Sq(nav.Z[path[i]] - nav.Z[path[i - 1]]));
                newOrder.Add(stops[j - 1]);
                at = j;
            }
            layer.RouteYards = Math.Round(yards);
            // the bosses are numbered in the order the route meets them; the rest follow in the game's order
            var rest = layer.Bosses.Where(b => !newOrder.Contains(b)).ToList();
            layer.Bosses.Clear(); layer.Bosses.AddRange(newOrder); layer.Bosses.AddRange(rest);
            for (int i = 0; i < layer.Bosses.Count; i++) layer.Bosses[i].Order = i + 1;
            if (layer.Unreached.Count > 0) layer.RouteNote = (layer.RouteNote.Length > 0 ? layer.RouteNote + " " : "") + "Not on the route (no walkable way to it on the drawn floors): " + string.Join(", ", layer.Unreached.Distinct()) + ".";
        }

        static double Sq(double v) => v * v;
        static string Name(MobPack p) => p.Name;

        // the order of stops (indices into dist, 1..) after start 0 that walks the least: exact (Held-Karp) up to ExactUpTo, else nearest first then 2-opt
        public static List<int> Order(double[,] dist, IList<int> stops)
        {
            int m = stops.Count;
            if (m <= 1) return stops.ToList();
            if (m <= ExactUpTo)
            {
                int full = 1 << m;
                var best = new double[full, m]; var from = new int[full, m];
                for (int s = 0; s < full; s++) for (int j = 0; j < m; j++) { best[s, j] = double.PositiveInfinity; from[s, j] = -1; }
                for (int j = 0; j < m; j++) best[1 << j, j] = dist[0, stops[j]];
                for (int s = 1; s < full; s++)
                    for (int j = 0; j < m; j++)
                    {
                        if ((s & (1 << j)) == 0 || double.IsInfinity(best[s, j])) continue;
                        for (int k = 0; k < m; k++)
                        {
                            if ((s & (1 << k)) != 0) continue;
                            double c = best[s, j] + dist[stops[j], stops[k]];
                            int t = s | (1 << k);
                            if (c < best[t, k]) { best[t, k] = c; from[t, k] = j; }
                        }
                    }
                int last = -1; double bc = double.PositiveInfinity;
                for (int j = 0; j < m; j++) if (best[full - 1, j] < bc) { bc = best[full - 1, j]; last = j; }
                if (last < 0) return Greedy(dist, stops);
                var res = new List<int>();
                for (int s = full - 1, j = last; j >= 0;) { res.Add(stops[j]); int p = from[s, j]; s &= ~(1 << j); j = p; }
                res.Reverse();
                return res;
            }
            return Greedy(dist, stops);
        }

        static List<int> Greedy(double[,] dist, IList<int> stops)
        {
            var left = stops.ToList(); var res = new List<int>(); int at = 0;
            while (left.Count > 0) { var nx = left.OrderBy(j => dist[at, j]).First(); res.Add(nx); left.Remove(nx); at = nx; }
            double Len(List<int> o) { double l = dist[0, o[0]]; for (int i = 1; i < o.Count; i++) l += dist[o[i - 1], o[i]]; return l; }
            bool better = true;
            while (better)
            {
                better = false;
                for (int i = 0; i < res.Count - 1 && !better; i++)
                    for (int j = i + 1; j < res.Count && !better; j++)
                    {
                        var c = res.Take(i).Concat(res.Skip(i).Take(j - i + 1).Reverse()).Concat(res.Skip(j + 1)).ToList();
                        if (Len(c) + 1e-6 < Len(res)) { res = c; better = true; }
                    }
            }
            return res;
        }

        // nodes -> route points: a point where the floor changes and at the corners (a point every few yards is enough to draw)
        static void AddPoints(DungeonMobLayer layer, DungeonNav nav, List<int> path)
        {
            var pts = path.Select(p => new MobRoutePoint { Floor = nav.Floor[p], X = nav.WorldX(p), Y = nav.WorldY(p), Z = nav.Z[p] }).ToList();
            if (pts.Count == 0) return;
            for (int i = 1; i < path.Count; i++) if (nav.IsLink(path[i - 1], path[i])) pts[i].Passage = true;
            var keep = new List<MobRoutePoint>();
            int s = 0;
            for (int i = 1; i <= pts.Count; i++)
            {
                if (i < pts.Count && pts[i].Floor == pts[s].Floor && !pts[i].Passage) continue;
                keep.AddRange(Simplify(pts, s, i - 1, 1.2));
                s = i;
            }
            foreach (var p in keep)
            {
                var last = layer.Route.Count > 0 ? layer.Route[layer.Route.Count - 1] : null;
                if (last != null && last.Floor == p.Floor && Math.Abs(last.X - p.X) < 0.01 && Math.Abs(last.Y - p.Y) < 0.01) continue;
                layer.Route.Add(p);
            }
        }

        // Douglas-Peucker on [a, b] inclusive
        static List<MobRoutePoint> Simplify(List<MobRoutePoint> p, int a, int b, double tol)
        {
            if (b <= a) return new List<MobRoutePoint> { p[a] };
            var keep = new bool[b - a + 1]; keep[0] = keep[b - a] = true;
            var stack = new Stack<KeyValuePair<int, int>>(); stack.Push(new KeyValuePair<int, int>(a, b));
            while (stack.Count > 0)
            {
                var seg = stack.Pop(); int i0 = seg.Key, i1 = seg.Value;
                double x0 = p[i0].X, y0 = p[i0].Y, x1 = p[i1].X, y1 = p[i1].Y, len = Math.Sqrt(Sq(x1 - x0) + Sq(y1 - y0));
                int far = -1; double fd = tol;
                for (int i = i0 + 1; i < i1; i++)
                {
                    double d = len < 1e-9 ? Math.Sqrt(Sq(p[i].X - x0) + Sq(p[i].Y - y0)) : Math.Abs((x1 - x0) * (y0 - p[i].Y) - (x0 - p[i].X) * (y1 - y0)) / len;
                    if (d > fd) { fd = d; far = i; }
                }
                if (far < 0) continue;
                keep[far - a] = true;
                stack.Push(new KeyValuePair<int, int>(i0, far)); stack.Push(new KeyValuePair<int, int>(far, i1));
            }
            var res = new List<MobRoutePoint>();
            for (int i = a; i <= b; i++) if (keep[i - a]) res.Add(p[i]);
            return res;
        }
    }

    // ============================================================================================================================
    // The service: the baseline's instance data read once per baseline (<data>\atlas\dungeonmaps\mobs.bin), the layer of a plan worked out once per plan.
    // Nothing is read or downloaded when the Classic baseline is not installed; the layer is then simply not there.
    // ============================================================================================================================
    public static class DungeonMobs
    {
        public static Func<DungeonMobData> DataForTest;                   // self-test: a fixture instead of the baseline
        public static Func<MobTemplate, bool> FriendlyForTest;
        public static event Action<int> Changed;                          // a map's layer is ready (worker thread)
        static readonly object gate = new object();
        static DungeonMobData data; static Task<DungeonMobData> loading;
        static readonly Dictionary<string, Task<DungeonMobLayer>> layers = new Dictionary<string, Task<DungeonMobLayer>>();
        public static string Problem { get; private set; } = "";

        public static string DataPath => Path.Combine(DungeonPlans.Root, "mobs.bin");

        public static void ResetForTest() { lock (gate) { data = null; loading = null; layers.Clear(); Problem = ""; } }

        // the baseline this data must come from ("" = none installed)
        static string BaselineKey()
        {
            var p = ClassicAtlas.Current?.Provenance;
            return p == null ? "" : (p.DumpName ?? "") + "|" + (p.CommitSha ?? "") + "|" + (p.BuiltUtc ?? "") + "|" + DungeonMobData.Format;
        }

        public static bool Available => DataForTest != null || BaselineKey().Length > 0;

        public static Task<DungeonMobData> Data()
        {
            lock (gate)
            {
                if (DataForTest != null) return Task.FromResult(data ?? (data = DataForTest()));
                if (data != null) return Task.FromResult(data);
                if (loading != null) return loading;
                return loading = Task.Run(() =>
                {
                    try
                    {
                        var key = BaselineKey();
                        if (key.Length == 0) { Problem = "The Classic baseline is not installed."; return null; }
                        var d = DungeonMobData.Load(DataPath);
                        if (d != null && d.Key == key) { lock (gate) data = d; return d; }
                        var prov = ClassicAtlas.Current.Provenance;
                        var dump = ClassicPaths.DumpFile(prov.DumpName ?? "");
                        if (!File.Exists(dump)) { Problem = "The Classic baseline's files are not on this PC."; return null; }
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        d = DungeonMobData.Read(dump, ClassicPaths.UpdatesDir(), key, CancellationToken.None);
                        if (d == null) return null;
                        try { d.Save(DataPath); } catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { Util.Log("dungeon mobs: could not save " + e.Message); }
                        Util.Log("dungeon mobs: read " + d.SpawnsByMap.Sum(kv => kv.Value.Count) + " spawns on " + d.SpawnsByMap.Count + " maps, " + d.Templates.Count + " creatures, " + d.Spells.Count + " spells, " + d.Encounters.Count + " encounters in " + sw.ElapsedMilliseconds + " ms");
                        lock (gate) data = d;
                        return d;
                    }
                    catch (Exception e) { Problem = e.Message; Util.Log("dungeon mobs: " + e.Message); return null; }
                    finally { lock (gate) loading = null; }
                });
            }
        }

        // the layer of a plan (null = no baseline / no data); worked out once per plan folder
        public static Task<DungeonMobLayer> Layer(DungeonPlan plan)
        {
            if (plan == null) return Task.FromResult<DungeonMobLayer>(null);
            var key = (plan.Dir ?? "") + "|" + plan.MapId;
            lock (gate)
            {
                if (layers.TryGetValue(key, out var t)) return t;
                t = Data().ContinueWith(dt =>
                {
                    var d = dt.Status == TaskStatus.RanToCompletion ? dt.Result : null;
                    if (d == null) return null;
                    try { Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; } catch (Exception) { }
                    try
                    {
                        var l = DungeonMobLayers.Build(plan.MapId, plan, d, FriendlyForTest ?? DungeonMobLayers.FriendlyToAll);
                        Util.Log("dungeon mobs " + plan.Name + ": " + l.Packs.Count + " pulls (" + l.Bosses.Count + " bosses, " + l.Packs.Count(p => p.Kind == MobPackKind.Rare) + " rares) from " + l.Spawns + " spawns (" + l.LeftOut + " left out), route " + l.RouteYards + " yards, " + l.Ms + " ms" + (l.RouteNote.Length > 0 ? "; " + l.RouteNote : ""));
                        return l;
                    }
                    catch (Exception e) { Util.Log("dungeon mobs " + plan.Name + ": " + e.Message); return null; }
                    finally { try { Changed?.Invoke(plan.MapId); } catch (Exception) { } }
                }, CancellationToken.None, TaskContinuationOptions.LongRunning, TaskScheduler.Default);
                layers[key] = t;
                return t;
            }
        }
    }
}
