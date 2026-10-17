using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ElansAddonHub.Services;
using ElansAddonHub.Services.DungeonMaps;
using ElansAddonHub.Services.Maps;
using ElansAddonHub.Services.WorldMap;

namespace ElansAddonHub
{
    // --selftest "dungeonmaps", part 6: the creatures on the dungeon map (DungeonMobData, DungeonMobs, DungeonNav, AtlasMap.Mobs.cs) on fixtures:
    // the baseline's tables read as SQL text, warnings only from what the spell data says, pulls, placing on the right floor, walls the route goes around,
    // passages where the plan has no floor, the boss order, the fallbacks, and the map card's chips, markers, hover text and route. The real data:
    // dungeonmapsreal.
    public partial class MainWindow
    {
        static string DmSqlTable(string name, string cols) => "CREATE TABLE `" + name + "` (" + string.Join(", ", cols.Split(',').Select(c => "`" + c + "` int")) + ");\n";
        static string DmSqlRow(string name, params object[] v) => "INSERT INTO `" + name + "` VALUES (" + string.Join(",", v.Select(x => x is string s ? "'" + s.Replace("'", "''") + "'" : Convert.ToString(x, CultureInfo.InvariantCulture))) + ");\n";

        // the tables the mob layer reads, with the given rows
        static string DmMobSql(IEnumerable<string> rows)
        {
            var sb = new StringBuilder();
            sb.Append(DmSqlTable("creature", "guid,id,map,spawnMask,position_x,position_y,position_z,orientation,spawntimesecsmin,spawntimesecsmax,spawndist,MovementType"));
            sb.Append(DmSqlTable("creature_spawn_entry", "guid,entry"));
            sb.Append(DmSqlTable("creature_movement", "Id,Point,PositionX,PositionY,PositionZ,Orientation,WaitTime,ScriptId,Comment"));
            sb.Append(DmSqlTable("creature_movement_template", "Entry,PathId,Point,PositionX,PositionY,PositionZ,Orientation,WaitTime,ScriptId,Comment"));
            sb.Append(DmSqlTable("creature_linking", "guid,master_guid,flag"));
            sb.Append(DmSqlTable("creature_linking_template", "entry,map,master_entry,flag,search_range"));
            sb.Append(DmSqlTable("creature_template", "Entry,Name,SubName,MinLevel,MaxLevel,Faction,CreatureType,NpcFlags,UnitFlags,ExtraFlags,Rank,MechanicImmuneMask,SchoolImmuneMask,SpellList"));
            sb.Append(DmSqlTable("creature_template_spells", "entry,setId,spell1,spell2,spell3,spell4,spell5,spell6,spell7,spell8,spell9,spell10"));
            sb.Append(DmSqlTable("creature_spell_list", "Id,Position,SpellId"));
            sb.Append(DmSqlTable("creature_ai_scripts", "id,creature_id,event_type,action1_type,action1_param1,action2_type,action2_param1,action3_type,action3_param1"));
            sb.Append(DmSqlTable("creature_template_addon", "entry,auras"));
            sb.Append(DmSqlTable("spell_template", "Id,Dispel,Mechanic,Effect1,Effect2,Effect3,EffectApplyAuraName1,EffectApplyAuraName2,EffectApplyAuraName3,EffectMechanic1,EffectMechanic2,EffectMechanic3,EffectImplicitTargetA1,EffectImplicitTargetA2,EffectImplicitTargetA3,EffectImplicitTargetB1,EffectImplicitTargetB2,EffectImplicitTargetB3,SpellName"));
            sb.Append(DmSqlTable("instance_dungeon_encounters", "Id,MapId,Difficulty,EncounterIndex,EncounterName"));
            sb.Append(DmSqlTable("instance_encounters", "entry,creditType,creditEntry"));
            sb.Append(DmSqlTable("areatrigger_teleport", "id,name,target_map,target_position_x,target_position_y,target_position_z"));
            foreach (var r in rows) sb.Append(r);
            return sb.ToString();
        }

        static string DmNpc(int id, string name, int level, int rank = 1, int type = 7, int extra = 0, int unitFlags = 0, int npcFlags = 0, int mechImm = 0, int schoolImm = 0) =>
            DmSqlRow("creature_template", id, name, "", level, level, 14, type, npcFlags, unitFlags, extra, rank, mechImm, schoolImm, 0);
        static string DmSpawn(int guid, int id, int map, double x, double y, double z, int movement = 0) => DmSqlRow("creature", guid, id, map, 1, x, y, z, 0, 300, 300, 0, movement);
        static string DmSpell(int id, string name, int effect, int aura, int targetA, int targetB = 0, int dispel = 0, int mechanic = 0) =>
            DmSqlRow("spell_template", id, dispel, mechanic, effect, 0, 0, aura, 0, 0, 0, 0, 0, targetA, 0, 0, targetB, 0, 0, name);
        static string DmCasts(int npc, params int[] spells) => DmSqlRow("creature_template_spells", npc, 0, spells.Length > 0 ? spells[0] : 0, spells.Length > 1 ? spells[1] : 0, spells.Length > 2 ? spells[2] : 0, spells.Length > 3 ? spells[3] : 0, spells.Length > 4 ? spells[4] : 0, spells.Length > 5 ? spells[5] : 0, 0, 0, 0, 0);

        // a plan drawn again: "Your game was updated" / "This version of the Outpost draws more" instead of "The first time", remembered after the old folders go
        void DmAgainChecks(string work, CheckFn Check)
        {
            try
            {
                DungeonPlans.RootOverride = Path.Combine(work, "cache-again"); DungeonPlans.ResetForTest();
                MapData.SetForTest(DmIndex(), "wow_classic_beta|1.60.1.70338|0123456789abcdef");
                var key = DungeonPlans.Key;
                var olderGame = Path.Combine(DungeonPlans.Root, "1.60.1.70000-aaaaaaaa_v" + DungeonPlan.Format, "900");
                var olderOutpost = Path.Combine(DungeonPlans.Root, key.Substring(0, key.LastIndexOf("_v", StringComparison.Ordinal)) + "_v1", "901");
                var halfDone = Path.Combine(DungeonPlans.Root, "1.60.1.70000-aaaaaaaa_v" + DungeonPlan.Format, "902");
                foreach (var d in new[] { olderGame, olderOutpost, halfDone }) Directory.CreateDirectory(d);
                File.WriteAllText(Path.Combine(olderGame, "plan.json"), "{}"); File.WriteAllText(Path.Combine(olderOutpost, "plan.json"), "{}");
                var st900 = new DungeonPlanState { MapId = 900, Seconds = 4, Bytes = 300000 }; var st901 = new DungeonPlanState { MapId = 901, Seconds = 4 }; var st902 = new DungeonPlanState { MapId = 902, Seconds = 4 };
                string h900 = AtlasMap.DrawingHint(st900), h901 = AtlasMap.DrawingHint(st901), h902 = AtlasMap.DrawingHint(st902);
                Check("drawn again: a plan drawn for an older game build says the game was updated, one drawn by an older Outpost (this build) says this version draws more; a folder without a finished plan (or none) is the first time",
                    DungeonPlans.Before(900) == DungeonPlans.DrawnBefore.OlderGame && DungeonPlans.Before(901) == DungeonPlans.DrawnBefore.OlderOutpost && DungeonPlans.Before(902) == DungeonPlans.DrawnBefore.Never && DungeonPlans.Before(903) == DungeonPlans.DrawnBefore.Never
                    && h900.StartsWith("Your game was updated") && h901.StartsWith("This version of the Outpost draws more") && h902.StartsWith("The first time only"), h900 + " | " + h901 + " | " + h902);
                Directory.CreateDirectory(Path.Combine(DungeonPlans.Root, key));
                int removed = DungeonPlans.RemoveOtherBuilds();
                Check("drawn again: removing the old folders keeps that note (older-plans.txt in the new folder), so the card still says why after they are gone",
                    removed == 2 && !Directory.Exists(olderGame) && !Directory.Exists(olderOutpost) && File.Exists(Path.Combine(DungeonPlans.Root, key, "older-plans.txt"))
                    && DungeonPlans.Before(900) == DungeonPlans.DrawnBefore.OlderGame && DungeonPlans.Before(901) == DungeonPlans.DrawnBefore.OlderOutpost && DungeonPlans.Before(902) == DungeonPlans.DrawnBefore.Never, "removed " + removed);
            }
            finally { DungeonPlans.ResetForTest(); MapData.ResetForTest(); }
        }

        async Task DmMobChecks(string work, CheckFn Check, List<string> notes, string dir)
        {
            // ---- the baseline's tables
            var rows = new List<string>
            {
                DmNpc(100, "Gloom Warden", 20, extra: 0x1), DmNpc(101, "Gloom Acolyte", 18), DmNpc(102, "Gloom Brute", 19), DmNpc(103, "Cellar Rat", 1, rank: 0, type: 8),
                DmNpc(104, "Gloom Trigger", 1, rank: 0, unitFlags: 0x02000000), DmNpc(105, "Lost Pilgrim", 18, rank: 0, npcFlags: 0x3), DmNpc(106, "Pale Stalker", 21, rank: 4),
                DmNpc(107, "Bone Guard", 18), DmNpc(108, "Night Prowler", 19, mechImm: 1 << 11, schoolImm: 4), DmNpc(109, "Echo Lord", 22, rank: 3), DmNpc(110, "Summoned Shade", 18),
                DmSqlRow("instance_dungeon_encounters", 1, 900, 0, 0, "Echo Lord"), DmSqlRow("instance_dungeon_encounters", 2, 900, 0, 1, "Gloom Warden"), DmSqlRow("instance_dungeon_encounters", 3, 900, 0, 2, "The Unseen"),
                DmSqlRow("instance_encounters", 1, 0, 109), DmSqlRow("instance_encounters", 2, 0, 100), DmSqlRow("instance_encounters", 3, 0, 4242),
                DmSqlRow("areatrigger_teleport", 50, "Gloomhold - Entering", 900, 5, 5, 0), DmSqlRow("areatrigger_teleport", 51, "Somewhere", 0, 1, 1, 1),
                // spells: a fear on enemies, a form on itself that silences only the caster, a poison, a sleep, a heal, a summon, an enrage
                DmSpell(9001, "Psychic Scream", 6, 7, 22, 15), DmSpell(9002, "Serpent Form", 6, 27, 1), DmSpell(9003, "Deadly Poison", 6, 3, 6, dispel: 4), DmSpell(9004, "Sleep", 6, 12, 6, mechanic: 10),
                DmSpell(9005, "Mend", 10, 0, 21), DmSpell(9006, "Call Shade", 28, 0, 1), DmSpell(9007, "Enrage", 6, 138, 1), DmSpell(9008, "Faded", 6, 16, 1),
                DmCasts(101, 9001, 9002, 9005), DmCasts(102, 9003, 9007), DmCasts(100, 9004, 9006),
                DmSqlRow("creature_ai_scripts", 1, 102, 2, 25, 0, 0, 0, 0, 0), DmSqlRow("creature_ai_scripts", 2, 107, 4, 39, 15, 0, 0, 0, 0), DmSqlRow("creature_ai_scripts", 3, 100, 4, 12, 110, 0, 0, 0, 0),
                DmSqlRow("creature_template_addon", 108, "9008"),
            };
            var data = DungeonMobData.FromText(DmMobSql(rows.Concat(new[] { DmSpawn(1, 101, 900, 10, 10, 0), DmSpawn(2, 101, 0, 10, 10, 0), DmSpawn(3, 0, 900, 12, 12, 0), DmSqlRow("creature_spawn_entry", 3, 102), DmSqlRow("creature_spawn_entry", 3, 107),
                DmSpawn(4, 106, 900, 30, 30, 0, 2), DmSqlRow("creature_movement", 4, 1, 30, 30, 0, 0, 0, 0, ""), DmSqlRow("creature_movement", 4, 2, 35, 30, 0, 0, 0, 0, ""), DmSqlRow("creature_movement", 4, 3, 35, 36, 0, 0, 0, 0, ""), DmSpawn(5, 100, 900, 50, 50, 0), DmSpawn(6, 108, 900, 60, 60, 0) })));
            var s1 = data.SpawnsOn(900);
            Check("mob data: spawns of the instance maps only (a continent's are left out); a spawn that can be one of several creatures keeps the others; a path walker keeps its points",
                s1.Count == 5 && data.SpawnsOn(0).Count == 0 && s1.First(s => s.Guid == 3).Entry == 102 && s1.First(s => s.Guid == 3).Others.SequenceEqual(new[] { 107 }) && s1.First(s => s.Guid == 4).Path.Length == 9,
                string.Join(" | ", s1.Select(s => s.Guid + ":" + s.Entry + " p" + s.Path.Length)));
            Check("mob data: the encounters in the game's order with the creature whose kill counts, and the entrance of the map (another map's is not it)",
                data.EncountersOf(900).Select(e => e.Name).SequenceEqual(new[] { "Echo Lord", "Gloom Warden", "The Unseen" }) && data.EncountersOf(900).First().CreditNpc == 109 && data.EntrancesOf(900).Single().X == 5);
            Check("mob data: what a creature casts (its spells, its script's casts), summons (its script) and does (flees, calls for help), its auras",
                data.Template(101).Spells.OrderBy(x => x).SequenceEqual(new[] { 9001, 9002, 9005 }) && data.Template(100).Summons.SequenceEqual(new[] { 110 }) && data.Template(102).Flees && data.Template(107).CallsHelp && data.Template(108).Auras.SequenceEqual(new[] { 9008 }) && data.Template(110) != null);
            var dataFile = Path.Combine(work, "mobs.bin");
            data.Save(dataFile);
            var back = DungeonMobData.Load(dataFile);
            File.WriteAllBytes(Path.Combine(work, "junk.bin"), new byte[] { 69, 68, 77, 66, 1, 2 });
            Check("mob data: the saved file reads back the same (format " + DungeonMobData.Format + "); damaged = null", back != null && back.Key == data.Key && back.SpawnsOn(900).Count == 5 && back.Spell(9004).Mechanic == 10 && back.Spell(9002).OnSelf(0) && back.Template(108).SchoolImmune == 4 && back.Encounters.Count == 3
                && DungeonMobData.Load(Path.Combine(work, "nothing.bin")) == null && DungeonMobData.Load(Path.Combine(work, "junk.bin")) == null);

            // ---- warnings: only what the data says, with the spell's name
            string W(int npc) => string.Join(" | ", MobWarnings.Of(data.Template(npc), data.Spell, data.Template).Select(w => (w.Badge ? "!" : "") + w.Text));
            var w101 = W(101); var w102 = W(102); var w100 = string.Join(" | ", MobWarnings.Of(data.Template(100), data.Spell, data.Template, true).Select(w => w.Text)); var w108 = W(108); var w107 = W(107);
            Check("warnings: a fear on enemies is a fear (Psychic Scream), a heal a heal; a form that silences only the caster is no silence", w101.Contains("!Fears: Psychic Scream") && w101.Contains("!Heals: Mend") && !w101.Contains("Silence"), w101);
            Check("warnings: a poison by its dispel type, an enrage by name, running for help from the script", w102.Contains("Poison: Deadly Poison") && w102.Contains("Enrages: Enrage") && w102.Contains("!Runs for help at low health"), w102);
            Check("warnings: a sleep incapacitates (never also called a stun); a summon names the creature it brings", w100.Contains("Incapacitates: Sleep") && !w100.Contains("Stuns") && w100.Contains("Summons: Call Shade") && w100.Contains("Summoned Shade"), w100);
            Check("warnings: stealth from an aura it always has, immunities from its masks (fear-proof creatures are not claimed); calling for help", w108.Contains("!Stealthed until it attacks (Faded)") && w108.Contains("Immune to stuns") && w108.Contains("Immune to Fire") && w107.Contains("!Calls nearby allies for help"), w108 + " / " + w107);
            Check("warnings: a creature that does nothing special gets none", MobWarnings.Of(data.Template(106), data.Spell, data.Template).Count == 0);

            // ---- the order of the stops
            var dm = new double[4, 4] { { 0, 10, 1, 12 }, { 10, 0, 9, 1 }, { 1, 9, 0, 30 }, { 12, 1, 30, 0 } };
            var ord = DungeonMobLayers.Order(dm, new[] { 1, 2, 3 });
            Check("route order: the shortest walk from the entrance past every stop (2, 1, 3: 1 + 9 + 1), not simply the nearest first", ord.SequenceEqual(new[] { 2, 1, 3 }), string.Join(",", ord));

            // ---- a level with a wall and a doorway: the route goes through the doorway
            var room = DmUnit("Walled Hall", (0, 40, 0, 40, 0, 0));
            var wgeo = new PlanGeometry { GlobalWmo = true };
            wgeo.Units.Add(room);
            void WallQuad(float x0, float y0, float x1, float y1) { wgeo.Walls.AddRange(new[] { x0, y0, 0f, x1, y1, 0f, x1, y1, 5f }); wgeo.Walls.AddRange(new[] { x0, y0, 0f, x1, y1, 5f, x0, y0, 5f }); }
            for (float y = 0; y < 32; y += 4) WallQuad(20, y, 20, y + 4);           // the wall at x 20, open from y 32 to 40
            var wplan = DungeonPlans.Make(905, "Walled", wgeo, Path.Combine(work, "mobs-walled"));
            var wnav = wplan.Nav();
            var wdata = DungeonMobData.FromText(DmMobSql(new[] { DmNpc(200, "Hall Keeper", 20, extra: 1), DmNpc(201, "Second Keeper", 20, extra: 1), DmSpawn(10, 200, 905, 30, 4, 0), DmSpawn(11, 201, 905, 6, 36, 0), DmSqlRow("areatrigger_teleport", 1, "in", 905, 4, 4, 0) }));
            var wl = DungeonMobLayers.Build(905, wplan, wdata, t => false);
            bool onGrid = wl.Route.All(pt => wnav.Nearest(pt.X, pt.Y, 0, 1.5, 3) >= 0);
            Check("route: around a wall, through the doorway (the wall stands at x 20 up to y 32; the route crosses it beyond y 32), every point on walkable ground, both bosses on it",
                wnav != null && wl.Route.Count > 2 && wl.Bosses.Count == 2 && wl.Unreached.Count == 0 && onGrid && wl.Route.Any(pt => pt.Y > 32) && wl.Route.Where((pt, i) => i > 0 && (wl.Route[i - 1].X - 20) * (pt.X - 20) < 0).All(pt => pt.Y > 31),
                string.Join(" ", wl.Route.Select(pt => "(" + pt.X.ToString("0") + "," + pt.Y.ToString("0") + ")")) + " " + wl.RouteNote);
            Check("route: a node under the wall is left out of the grid; the plan keeps the grid (nav.bin, format " + DungeonPlan.Format + ")", wnav.Nearest(20.5, 10, 0, 0.6, 2) < 0 && wnav.Nearest(20.5, 36, 0, 0.6, 2) >= 0 && wplan.NavFile == "nav.bin" && File.Exists(Path.Combine(wplan.Dir, "nav.bin")));

            // ---- passages: a gap the plan has no floor for is joined at a slope a character climbs; a cliff is not
            var pgeo = new PlanGeometry { GlobalWmo = true };
            pgeo.Units.Add(DmUnit("Low Ledge", (0, 30, 0, 20, 0, 0))); pgeo.Units.Add(DmUnit("High Ledge", (42, 72, 0, 20, 8, 8))); pgeo.Units.Add(DmUnit("Cliff Top", (0, 30, 40, 60, 0, 0))); pgeo.Units.Add(DmUnit("Cliff Top", (0, 30, 62, 80, 60, 60)));
            var pnav = DungeonNav.Build(new[] { new KeyValuePair<int, IList<PlanUnit>>(0, pgeo.Units) }, new List<float>());
            var parts = pnav.Parts(out var pc);
            int PartAt(double x, double y, double z) => parts[pnav.Nearest(x, y, z, 2, 2)];
            pnav.Dijkstra(pnav.Nearest(10, 10, 0, 2, 2), (float[])null, out var pd, out _);
            Check("passages: two ledges 12 yards apart and 8 up are joined by one passage (a ramp the plan cannot see); the cliff 60 up beside its foot is not",
                pc == 4 && pnav.LinkA.Length >= 1 && !double.IsInfinity(pd[pnav.Nearest(60, 10, 8, 2, 2)]) && double.IsInfinity(pd[pnav.Nearest(10, 70, 60, 2, 2)]) && PartAt(10, 10, 0) != PartAt(60, 10, 8),
                "parts " + pc + " links " + pnav.LinkA.Length);
            var pfile = Path.Combine(work, "nav-test.bin"); pnav.Save(pfile); var pre = DungeonNav.Load(pfile);
            Check("passages: the grid and its passages read back from nav.bin", pre != null && pre.Count == pnav.Count && pre.LinkA.SequenceEqual(pnav.LinkA) && pre.Z.SequenceEqual(pnav.Z));

            // ---- two floors: creatures on the right floor; pulls; the route up the ramp
            var geo2 = new PlanGeometry { GlobalWmo = true };
            geo2.Units.Add(DmUnit("Hall of Echoes", (0, 40, 0, 40, 0, 0), (40, 80, 0, 10, 0, 20), (80, 90, 0, 40, 20, 20)));
            geo2.Units.Add(DmUnit("Gallery", (0, 80, 15, 40, 20, 20)));
            var plan2 = DungeonPlans.Make(900, "Gloomhold", geo2, Path.Combine(work, "mobs-two"));
            int up = plan2.Floors.FindIndex(f => f.MinZ >= 19), low = 1 - up;
            var spawnRows = new[]
            {
                DmSpawn(20, 101, 900, 20, 30, 0), DmSpawn(21, 101, 900, 24, 32, 0), DmSpawn(22, 102, 900, 22, 26, 0),     // a pull of three under the gallery
                DmSpawn(23, 107, 900, 20, 30, 20),                                                                           // the same place, up on the gallery
                DmSpawn(24, 101, 900, 34, 6, 0), DmSqlRow("creature_linking", 24, 20, 1),                                    // linked to the pull's first, 25 yards off
                DmSpawn(25, 103, 900, 10, 10, 0), DmSpawn(26, 104, 900, 12, 10, 0), DmSpawn(27, 105, 900, 14, 10, 0),          // a rat, a trigger, a friendly pilgrim
                DmSpawn(28, 106, 900, 5, 35, 0, 2), DmSqlRow("creature_movement", 28, 1, 5, 35, 0, 0, 0, 0, ""), DmSqlRow("creature_movement", 28, 2, 5, 20, 0, 0, 0, 0, ""),   // a rare on patrol
                DmSpawn(29, 100, 900, 8, 34, 20),                                                                            // the warden, up on the gallery over the hall
                DmSpawn(30, 109, 900, 36, 22, 0), DmSpawn(31, 110, 900, 38, 20, 0),                                         // the echo lord (rank boss) and a shade
            };
            var data2 = DungeonMobData.FromText(DmMobSql(rows.Concat(spawnRows)));
            var lay = DungeonMobLayers.Build(900, plan2, data2, t => (t.NpcFlags & 0x3) != 0);
            MobPack PackOf(int guid) => lay.Packs.FirstOrDefault(p => p.Spawns.Any(s => s.Guid == guid));
            var pull = PackOf(20);
            Check("placing: a creature goes to the floor its position is on (the same spot under and on the gallery: two floors)", PackOf(20)?.Floor == low && PackOf(23)?.Floor == up, PackOf(20)?.Floor + " / " + PackOf(23)?.Floor + " (low " + low + ")");
            Check("pulls: creatures within 10 yards on one floor are one pull, a linked one joins its master's pull from 25 yards away; the one up on the gallery is not in it",
                pull != null && pull.Count == 4 && pull.Spawns.Any(s => s.Guid == 24) && !pull.Spawns.Any(s => s.Guid == 23) && pull.Kind == MobPackKind.Pack && pull.Members.First(m => m.Entry == 101).Count == 3, pull == null ? "none" : string.Join(",", pull.Spawns.Select(s => s.Guid)));
            Check("left out: a critter, a trigger nobody can select, a creature friendly to both sides (" + lay.LeftOut + " of " + lay.Spawns + ")", lay.LeftOut == 3 && PackOf(25) == null && PackOf(26) == null && PackOf(27) == null);
            var rare = PackOf(28);
            Check("rares and patrols: a rare is its own marker; a creature walking a path is a patrol with its walk", rare != null && rare.Kind == MobPackKind.Rare && rare.Patrol && rare.Path.Length == 6);
            Check("bosses: the encounters' creatures (and a rank-boss), each a stop; an encounter whose creature has no spawn is listed as not placed",
                lay.Bosses.Count == 2 && lay.Bosses.All(b => b.Kind == MobPackKind.Boss) && lay.Bosses.Select(b => b.Name).OrderBy(x => x).SequenceEqual(new[] { "Echo Lord", "Gloom Warden" }) && lay.BossesWithoutPlace.SequenceEqual(new[] { "The Unseen" }) && PackOf(31) == PackOf(30),
                string.Join(",", lay.Bosses.Select(b => b.Order + " " + b.Name)) + " / " + string.Join(",", lay.BossesWithoutPlace));
            var nav2 = plan2.Nav();
            bool walks = lay.Route.All(pt => nav2.Nearest(pt.X, pt.Y, pt.Floor == up ? 20 : 0, 1.5, 21) >= 0);
            Check("route: from the entrance to the lord in the hall, then up the ramp to the warden on the gallery; numbered in that order; it changes floor once and every point is walkable",
                lay.Route.Count > 2 && lay.Bosses[0].Name == "Echo Lord" && lay.Bosses[1].Name == "Gloom Warden" && lay.Bosses[0].Order == 1 && lay.Route[0].Floor == low && lay.Route.Last().Floor == up && lay.Route.Where((p, i) => i > 0 && p.Floor != lay.Route[i - 1].Floor).Count() == 1 && walks && lay.RouteYards > 60,
                lay.RouteNote + " | " + string.Join(" ", lay.Route.Select(p => p.Floor + ":(" + p.X.ToString("0") + "," + p.Y.ToString("0") + ")")));
            var noEnt = DungeonMobLayers.Build(900, plan2, DungeonMobData.FromText(DmMobSql(rows.Where(r => !r.Contains("areatrigger")).Concat(spawnRows))), t => false);
            Check("fallbacks: no entrance point known: the route starts at the first boss and says so", noEnt.Route.Count > 1 && noEnt.RouteNote.Contains("No entrance point"), noEnt.RouteNote);
            var noNav = new DungeonPlan { MapId = 900, Floors = plan2.Floors, Dir = plan2.Dir };
            var nn = DungeonMobLayers.Build(900, noNav, data2, t => false);
            Check("fallbacks: a plan without a walkable grid still places the creatures (by the plan's floors) and has no route, saying why", nn.Packs.Count > 0 && nn.Route.Count == 0 && nn.RouteNote.Contains("no walkable grid") && nn.Packs.First(p => p.Spawns.Any(s => s.Guid == 23)).Floor == up, nn.RouteNote);

            // ---- the map card
            var savedData = DungeonMobs.DataForTest; var savedFriendly = DungeonMobs.FriendlyForTest;
            var savedChips = (AtlasMap.ShowBosses, AtlasMap.ShowPulls, AtlasMap.ShowRares, AtlasMap.ShowRoute);
            try
            {
                var files = DmInstall();
                DungeonPlans.SourceForTest = id => new FuncBuildingSource(fd => files.TryGetValue(fd, out var b) ? b : null);
                DungeonPlans.RootOverride = Path.Combine(work, "cache-mobs");
                DungeonPlans.Enabled = () => true; DungeonPlans.AutoSeconds = 30; DungeonPlans.ResetForTest();
                MapData.SetForTest(DmIndex(), "wow_classic_beta|1.60.1.70338|0123456789abcdef");
                DungeonMinimap.IndexForTest = () => null;
                await DungeonPlans.Request(900);
                var cp = DungeonPlans.Cached(900);
                var cnav = cp?.Nav();
                // creatures where the fixture's floors are: two nodes of floor 0 and one of the last floor
                int Node(int floor, int k) { var l = Enumerable.Range(0, cnav.Count).Where(i => cnav.Floor[i] == floor).ToList(); return l[Math.Min(l.Count - 1, k * l.Count / 5)]; }
                int a = Node(0, 1), b = Node(0, 3), c = Node(cp.Floors.Count - 1, 2);
                var cardRows = rows.Concat(new[]
                {
                    DmSpawn(40, 109, 900, cnav.WorldX(a), cnav.WorldY(a), cnav.Z[a]), DmSpawn(41, 101, 900, cnav.WorldX(b), cnav.WorldY(b), cnav.Z[b]), DmSpawn(42, 102, 900, cnav.WorldX(b) + 2, cnav.WorldY(b), cnav.Z[b]),
                    DmSpawn(43, 100, 900, cnav.WorldX(c), cnav.WorldY(c), cnav.Z[c]),
                }).Where(r => !r.Contains("areatrigger")).Concat(new[] { DmSqlRow("areatrigger_teleport", 60, "in", 900, cnav.WorldX(a) + 4, cnav.WorldY(a), cnav.Z[a]) });
                var cardData = DungeonMobData.FromText(DmMobSql(cardRows));
                DungeonMobs.DataForTest = () => cardData; DungeonMobs.FriendlyForTest = t => false; DungeonMobs.ResetForTest();
                AtlasMap.ShowBosses = AtlasMap.ShowPulls = AtlasMap.ShowRares = AtlasMap.ShowRoute = true;
                var map = new AtlasMap();
                string clicked = null; map.LinkClicked += id => clicked = id;
                var host = new Border { Width = 892, Height = 632, Padding = new Thickness(16) };
                host.SetResourceReference(Border.BackgroundProperty, "Brush.Surface1");
                host.Child = map;
                var win = new Window { Width = 900, Height = 660, WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false, Left = -4000, Top = -4000, Content = host, ResizeMode = ResizeMode.NoResize };
                win.Show();
                try
                {
                    await Task.Delay(150);
                    map.ShowInstance(900);
                    bool ready = await WaitFor(() => map.State == AtlasMap.ViewState.Ready && map.MobLayerForTest != null && map.MobMarkersForTest.Count > 0, 20000);
                    var mk = map.MobMarkersForTest;
                    var boss = mk.FirstOrDefault(m => m.Kind == MarkerKind.Boss); var pl = mk.FirstOrDefault(m => m.Kind == MarkerKind.Pull);
                    Check("map card: the chips show; the floor's boss is a numbered marker, the pull a marker with its count and a \"!\" (its creatures fear and run for help); the entrance starts the route",
                        ready && map.MobChipsShownForTest && boss != null && boss.Number >= 1 && boss.LinkId == "npc:109" && pl != null && pl.Count == 2 && pl.Warn && mk.Any(m => m.Kind == MarkerKind.Entrance) && map.RouteSegmentsOnFloorForTest()[0] > 0,
                        map.State + " " + string.Join(",", mk.Select(m => m.Kind + ":" + m.Number + "/" + m.Count)));
                    Check("map card: the note says where the creatures come from and that the route is a suggestion, not a guide", (map.MobNoteForTest ?? "").Contains("Classic baseline") && map.MobNoteForTest.Contains("not a guide"), map.MobNoteForTest);
                    if (SelfTestClock.ShotsOn) { RenderHost(host); SnapshotSheet(host, Path.Combine(dir, "dungeonmaps-mobs.png")); }
                    var vp = map.FractionToViewport(pl.Fx, pl.Fy);
                    map.HoverAtForTest(vp);
                    var tip = map.TipTextForTest ?? "";
                    Check("map card: hovering a pull names who is there with levels and the warnings in plain words", tip.Contains("Gloom Acolyte") && tip.Contains("Fears: Psychic Scream") && tip.Contains("Runs for help") && tip.Contains("Classic baseline"), tip);
                    if (SelfTestClock.ShotsOn) { RenderHost(host); SnapshotSheet(host, Path.Combine(dir, "dungeonmaps-mobs-tip.png")); }
                    map.ClickAt(map.FractionToViewport(boss.Fx, boss.Fy));
                    Check("map card: clicking a boss opens its creature page", clicked == "npc:109", clicked);
                    map.SetMobChipForTest("pulls", false);
                    bool noPulls = !map.MobMarkersForTest.Any(m => m.Kind == MarkerKind.Pull) && map.MobMarkersForTest.Any(m => m.Kind == MarkerKind.Boss);
                    map.SetMobChipForTest("route", false);
                    bool noRoute = map.RouteSegmentsOnFloorForTest()[0] == 0 && !map.MobMarkersForTest.Any(m => m.Kind == MarkerKind.Entrance);
                    Check("map card: the Pulls chip hides the pulls (the bosses stay), the Route chip the route and the entrance; the choice holds for the session", noPulls && noRoute && !AtlasMap.ShowPulls && !AtlasMap.ShowRoute);
                    map.SetMobChipForTest("pulls", true); map.SetMobChipForTest("route", true);
                    map.SelectFloorForTest(cp.Floors.Count - 1);
                    bool other = await WaitFor(() => map.DrawnFloor == cp.Floors.Count - 1 && map.State == AtlasMap.ViewState.Ready && map.MobMarkersForTest.Any(m => m.Kind == MarkerKind.Boss && m.LinkId == "npc:100"), 8000);
                    Check("map card: another floor shows its own creatures (the warden), not the first floor's", other && !map.MobMarkersForTest.Any(m => m.LinkId == "npc:109"), string.Join(",", map.MobMarkersForTest.Select(m => m.LinkId)));
                    if (SelfTestClock.ShotsOn) { RenderHost(host); SnapshotSheet(host, Path.Combine(dir, "dungeonmaps-mobs-floor2.png")); }
                    DungeonMobs.DataForTest = () => null; DungeonMobs.ResetForTest();
                    DungeonPlans.ResetForTest();
                    map.ShowInstance(900);
                    bool plain = await WaitFor(() => map.State == AtlasMap.ViewState.Ready && map.LookShownNowForTest.Length > 0, 20000);
                    await Task.Delay(300);
                    Check("map card: without the Classic baseline the map is as before: no chips, no creature markers", plain && !map.MobChipsShownForTest && map.MobMarkersForTest.Count == 0);
                }
                finally { win.Close(); }
            }
            finally
            {
                DungeonMobs.DataForTest = savedData; DungeonMobs.FriendlyForTest = savedFriendly; DungeonMobs.ResetForTest();
                (AtlasMap.ShowBosses, AtlasMap.ShowPulls, AtlasMap.ShowRares, AtlasMap.ShowRoute) = savedChips;
                DungeonMinimap.IndexForTest = null; DungeonPlans.ResetForTest(); MapData.ResetForTest();
            }
        }
    }
}
