using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Atlas.Classic;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Maps;

namespace ElansAddonHub
{
    // --selftest "combatlog" (2.95): the game's combat log files for the Atlas (CombatLogData). Reading a line (quoted names, creatures only, a player's pet or
    // totem is not the creature, the advanced block of the unit it describes), reading the files on from where the last pass stopped (a line the game is still
    // writing waits, a file started again is read from the start, the pass budget), keeping it (save / load), the positions on the zone map (the log's x / y order
    // is checked against the map it names) and the creature page: "Seen using", its health, "Fought" spots in "Seen at". Fixture lines and maps only.
    public partial class MainWindow
    {
        const string ClTime = "10/18/2026 20:15:33.123-4  ";

        // advanced block of a unit: info GUID, owner, hp, max hp, ap, sp, armor, absorb, power type, power, max power, cost, 2 more, x, y, ui map, facing, level
        static string ClAdv(string guid, long maxHp, double x, double y, int ui, int level) =>
            guid + ",0000000000000000," + maxHp + "," + maxHp + ",0,0,0,0,0,0,0,0,0,0," + x.ToString(System.Globalization.CultureInfo.InvariantCulture) + "," + y.ToString(System.Globalization.CultureInfo.InvariantCulture) + "," + ui + ",1.57," + level;

        const string ClWolf = "Creature-0-4170-0-12-466-0000ABCD01", ClWolf2 = "Creature-0-4170-0-12-466-0000ABCD02", ClBoar = "Creature-0-4170-0-12-113-0000ABCD03";
        const string ClPlayer = "Player-4170-00ABCDEF", ClTotem = "Creature-0-4170-0-12-2630-0000ABCD04";

        async Task<string> CombatLogTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            void Check(string what, bool cond, string detail = null) { SelfTestClock.Mark(what); notes.Add((cond ? "ok   " : "FAIL ") + what + (!cond && detail != null ? " [" + detail + "]" : "")); ok &= cond; }
            var savedCl = AtlasQuery.Context.CombatLog; var savedLodge = AtlasQuery.Context.Lodge; var savedOwn = AtlasQuery.Context.OwnNpcSpots;
            var work = Path.Combine(dir, "combatlog");
            try
            {
                if (Directory.Exists(work)) Util.DeleteDir(work);
                Directory.CreateDirectory(work);

                // ---- one line
                var f = CombatLogData.Fields(ClTime + "SPELL_CAST_SUCCESS," + ClWolf + ",\"Wolf, \"\"Old\"\" Grey\",0xa48,0x0," + ClPlayer + ",\"Elan\",0x511,0x0,133,\"Fireball\",0x4");
                Check("a line: the fields after the time, a quoted name with a comma and quotes is one field", f != null && f.Count == 12 && f[0] == "SPELL_CAST_SUCCESS" && f[2] == "Wolf, \"Old\" Grey" && f[9] == "133", f == null ? "null" : string.Join("|", f));
                Check("a line with no time stamp is no log line", CombatLogData.Fields("garbage") == null);
                Check("a creature GUID gives its npc id and world map; players and pets are no creature",
                    CombatLogData.CreatureOf(ClWolf, out var nid, out var mid) && nid == 466 && mid == 0 && !CombatLogData.CreatureOf(ClPlayer, out _, out _) && !CombatLogData.CreatureOf("Pet-0-4170-0-12-416-0100ABCD", out _, out _) && !CombatLogData.CreatureOf("Creature-0-1", out _, out _));

                var st = new CombatLogData.Store();
                var ls = new CombatLogData.LineState();
                bool R(string rest) => CombatLogData.Read(ClTime + rest, ls, st);
                R("SPELL_CAST_SUCCESS," + ClWolf + ",\"Wolf\",0xa48,0x0," + ClPlayer + ",\"Elan\",0x511,0x0,133,\"Fireball\",0x4");
                R("SPELL_CAST_START," + ClWolf2 + ",\"Wolf\",0xa48,0x0,0000000000000000,nil,0x80000000,0x80000000,133,\"Fireball\",0x4");
                R("SPELL_DAMAGE," + ClWolf + ",\"Wolf\",0xa48,0x0," + ClPlayer + ",\"Elan\",0x511,0x0,4000,\"Bite\",0x1,120,0,1,0,0,0,nil,nil,nil");
                R("SPELL_AURA_REMOVED," + ClWolf + ",\"Wolf\",0xa48,0x0," + ClPlayer + ",\"Elan\",0x511,0x0,129,\"Howl\",0x1,DEBUFF");
                R("SPELL_CAST_SUCCESS," + ClPlayer + ",\"Elan\",0x511,0x0," + ClWolf + ",\"Wolf\",0xa48,0x0,171,\"Arcane\",0x40");
                R("SPELL_CAST_SUCCESS," + ClTotem + ",\"Searing Totem\",0x2111,0x0," + ClWolf + ",\"Wolf\",0xa48,0x0,3606,\"Attack\",0x4");
                Check("spells: a creature's casts, cast starts and damage count per npc (two wolves are one npc); an aura that ends, a player's cast and a player's totem do not",
                    st.Npcs.Count == 1 && st.Npcs[466].Spells.Count == 2 && st.Npcs[466].Spells[133] == 2 && st.Npcs[466].Spells[4000] == 1,
                    string.Join("; ", st.Npcs.Select(k => k.Key + ":" + string.Join(",", k.Value.Spells.Select(s => s.Key + "x" + s.Value)))));
                R("SWING_DAMAGE," + ClWolf + ",\"Wolf\",0xa48,0x0," + ClPlayer + ",\"Elan\",0x511,0x0," + ClAdv(ClWolf, 300, -500, 2000, 1429, 12) + ",31,0,-1,1,0,0,0,nil,nil,nil");
                Check("without advanced logging the advanced block is not looked at", st.Npcs[466].Positions.Count == 0);
                R("COMBAT_LOG_VERSION,20,ADVANCED_LOG_ENABLED,1,BUILD_VERSION,1.60.1,PROJECT_ID,2");
                Check("COMBAT_LOG_VERSION with ADVANCED_LOG_ENABLED 1 switches the block on", ls.Advanced);
                R("SWING_DAMAGE," + ClWolf + ",\"Wolf\",0xa48,0x0," + ClPlayer + ",\"Elan\",0x511,0x0," + ClAdv(ClWolf, 300, -500, 2000, 1429, 12) + ",31,0,-1,1,0,0,0,nil,nil,nil");
                R("SWING_DAMAGE," + ClWolf2 + ",\"Wolf\",0xa48,0x0," + ClPlayer + ",\"Elan\",0x511,0x0," + ClAdv(ClWolf2, 320, -504, 2003, 1429, 13) + ",31,0,-1,1,0,0,0,nil,nil,nil");
                R("SPELL_CAST_SUCCESS," + ClBoar + ",\"Boar\",0xa48,0x0," + ClPlayer + ",\"Elan\",0x511,0x0,133,\"Fireball\",0x4," + ClAdv(ClBoar, 150, 2000, -500, 1429, 8));
                R("SPELL_CAST_SUCCESS," + ClPlayer + ",\"Elan\",0x511,0x0," + ClWolf + ",\"Wolf\",0xa48,0x0,171,\"Arcane\",0x40," + ClAdv(ClPlayer, 900, 10, 10, 1429, 60));
                var w = st.Npcs[466];
                Check("advanced block: a creature's position, its zone map and its world map; two places within 10 yards are one (seen twice); a player's block is not kept",
                    w.Positions.Count == 1 && w.Positions[0].N == 2 && w.Positions[0].Ui == 1429 && w.Positions[0].Map == 0 && Math.Abs(w.Positions[0].X + 502) < 0.01 && st.Npcs.Values.Sum(n => n.Positions.Count) == 2,
                    string.Join("; ", w.Positions.Select(p => p.Map + "/" + p.Ui + " " + p.X + "," + p.Y + " x" + p.N)));
                Check("advanced block: the health at each level it was met at", w.Health.Count == 2 && w.Health[12] == 300 && w.Health[13] == 320 && st.Npcs[113].Health[8] == 150);
                Check("nothing of a player is kept: no name, no player GUID in the store", !st.Npcs.ContainsKey(0) && st.Npcs.Keys.All(k => k == 466 || k == 113));

                // ---- the zone map: (x, y) as logged when that map shows it, else the other way round; none = left out
                var set = new MapSet { HasMaps = true, HasAssignments = true, HasAreas = true };
                set.Maps.Add(new GameMapInfo { Id = 0, Name = "Eastern Kingdoms", Directory = "Azeroth", InstanceType = 0 });
                set.UiMaps.Add(new UiMapInfo { Id = 1415, Name = "Eastern Kingdoms", Type = UiMapInfo.Continent });
                set.UiMaps.Add(new UiMapInfo { Id = 1429, Name = "Elwynn Forest", ParentId = 1415, Type = UiMapInfo.Zone });
                set.Assignments.Add(new UiAssignment { Id = 1, UiMapId = 1429, MapId = 0, AreaId = 12, UiMinX = 0, UiMinY = 0, UiMaxX = 1, UiMaxY = 1, MinX = -1000, MaxX = 1000, MinY = 0, MaxY = 4000 });
                set.Areas.Add(new AreaInfo { Id = 12, Name = "Elwynn Forest", ContinentId = 0 });
                var idx = new MapIndex(set);
                var wolfSpots = CombatLogData.Spots(w, idx);
                var boarSpots = CombatLogData.Spots(st.Npcs[113], idx);
                var direct = idx.WorldToMap(0, -502, 2001.5, 1429);
                Check("zone map: the position as logged when the named map shows it (kind \"fought here\", seen twice)",
                    wolfSpots.Count == 1 && wolfSpots[0].Z == 1429 && wolfSpots[0].K == AtlasObs.NpcSpotCombat && wolfSpots[0].N == 2 && direct != null && Math.Abs(wolfSpots[0].X - direct.Value.Fx) < 0.001 && Math.Abs(wolfSpots[0].Y - direct.Value.Fy) < 0.001,
                    string.Join("; ", wolfSpots.Select(s => s.Z + " " + s.X + "," + s.Y)));
                Check("zone map: a position the map only shows the other way round is turned (the log's x / y order is checked, not assumed)",
                    boarSpots.Count == 1 && Math.Abs(boarSpots[0].X - idx.WorldToMap(0, -500, 2000, 1429).Value.Fx) < 0.001, string.Join("; ", boarSpots.Select(s => s.X + "," + s.Y)));
                var lost = new CombatLogData.Npc(); lost.Positions.Add(new CombatLogData.Position { Map = 0, Ui = 1429, X = 9000, Y = 9000, N = 1 }); lost.Positions.Add(new CombatLogData.Position { Map = 0, Ui = 0, X = -500, Y = 2000, N = 1 });
                Check("zone map: a point no map shows and a line with no zone map give no spot", CombatLogData.Spots(lost, idx).Count == 0);
                Check("NPC spot kind text: fought here", AtlasObs.NpcSpotKindText(AtlasObs.NpcSpotCombat) == "fought here");

                // ---- the files
                var logs = Path.Combine(work, "Logs");
                Directory.CreateDirectory(logs);
                var file = Path.Combine(logs, "WoWCombatLog-101826_201533.txt");
                string L(string rest) => ClTime + rest + "\r\n";
                File.WriteAllText(file, L("COMBAT_LOG_VERSION,20,ADVANCED_LOG_ENABLED,1,BUILD_VERSION,1.60.1,PROJECT_ID,2")
                    + L("SPELL_CAST_SUCCESS," + ClWolf + ",\"Wolf\",0xa48,0x0," + ClPlayer + ",\"Elan\",0x511,0x0,133,\"Fireball\",0x4," + ClAdv(ClWolf, 300, -500, 2000, 1429, 12))
                    + ClTime + "SPELL_CAST_SUCCESS," + ClBoar + ",\"Boar\",0xa48,0x0," + ClPlayer, new UTF8Encoding(false));     // the game is still writing this line
                File.WriteAllText(Path.Combine(logs, "notes.txt"), L("SPELL_CAST_SUCCESS," + ClBoar + ",\"Boar\",0xa48,0x0,x,y,0,0,133,\"F\",0x4"));
                var fs = new CombatLogData.Store();
                long n1 = CombatLogData.Scan(logs, fs);
                var mark = fs.Files.Values.Single();
                Check("files: a WoWCombatLog file is read to its last whole line (the line the game is still writing waits), other files are not read",
                    fs.Npcs.Count == 1 && fs.Npcs[466].Spells[133] == 1 && fs.Npcs[466].Positions.Count == 1 && mark.Advanced && mark.Offset == n1 && n1 < new FileInfo(file).Length && fs.Files.Count == 1);
                File.AppendAllText(file, ",\"Elan\",0x511,0x0,133,\"Fireball\",0x4\r\n");
                long n2 = CombatLogData.Scan(logs, fs);
                Check("files: the next pass reads on from where the last one stopped (the finished line, nothing twice; still advanced)",
                    fs.Npcs.ContainsKey(113) && fs.Npcs[113].Spells[133] == 1 && fs.Npcs[466].Spells[133] == 1 && mark.Offset == new FileInfo(file).Length && n2 > 0);
                Check("files: a pass with nothing new reads nothing", CombatLogData.Scan(logs, fs) == 0);
                File.WriteAllText(file, L("SPELL_CAST_SUCCESS," + ClWolf + ",\"Wolf\",0xa48,0x0," + ClPlayer + ",\"Elan\",0x511,0x0,4000,\"Bite\",0x1"));
                CombatLogData.Scan(logs, fs);
                Check("files: a file that got shorter (the game started it again) is read from the start, and its advanced flag starts over",
                    fs.Npcs[466].Spells.ContainsKey(4000) && !fs.Files.Values.Single().Advanced);
                var big = Path.Combine(logs, "WoWCombatLog-101826_210000.txt");
                var sb = new StringBuilder();
                for (int i = 0; i < 200; i++) sb.Append(L("SPELL_CAST_SUCCESS," + ClBoar + ",\"Boar\",0xa48,0x0," + ClPlayer + ",\"Elan\",0x511,0x0,129,\"Howl\",0x1"));
                File.WriteAllText(big, sb.ToString());
                long part = CombatLogData.Scan(logs, fs, 4000);
                long howls1 = fs.Npcs[113].Spells.TryGetValue(129, out var h1) ? h1 : 0;
                for (int i = 0; i < 100 && CombatLogData.Scan(logs, fs, 4000) > 0; i++) { }
                Check("files: a pass reads at most its budget (whole lines), the next passes go on until the end, every line once",
                    part <= 4000 && howls1 > 0 && howls1 < 200 && fs.Npcs[113].Spells[129] == 200, part + " / " + howls1 + " / " + fs.Npcs[113].Spells[129]);

                // ---- keeping it
                var path = Path.Combine(work, "combatlog.txt");
                CombatLogData.Save(fs, path);
                var back = CombatLogData.Load(path);
                Check("save / load: files, spells, positions, health come back the same",
                    back.Files.Count == fs.Files.Count && back.Files.All(k => fs.Files[k.Key].Offset == k.Value.Offset) && back.Npcs.Count == fs.Npcs.Count
                    && back.Npcs[466].Spells.Count == fs.Npcs[466].Spells.Count && back.Npcs[466].Positions.Count == 1 && back.Npcs[466].Health[12] == 300 && back.Npcs[113].Spells[129] == 200);
                File.WriteAllText(path, "something else\n");
                Check("load: a file of another kind is an empty store, not an error", CombatLogData.Load(path).Npcs.Count == 0);
                Check("load: no file is an empty store", CombatLogData.Load(Path.Combine(work, "none.txt")).Npcs.Count == 0);
                Check("the logs folder of the Forever client (_classic_beta_ in the beta)", CombatLogData.LogsDir(work, "_classic_beta_") == Path.Combine(work, "_classic_beta_", "Logs"));

                // ---- the creature page
                ItemData.SetForTest(AtlasTestItems(), ItemData.State.Ready);
                SpellData.SetForTest(AtlasTestSpells());
                MapData.SetForTest(idx);
                ClassicAtlas.Use(null);
                AtlasQuery.Context.Lodge = () => null;
                AtlasQuery.Context.OwnNpcSpots = () => null;
                AtlasQuery.Context.CombatLog = () => st;
                var page = AtlasQuery.Build("npc:466");
                var using_ = page.Sections.FirstOrDefault(s => s.Key == "seenusing");
                Check("NPC page: \"Seen using\" lists the spells most seen first, linked, labelled as your own, with the note where it comes from",
                    using_ != null && using_.Rows.Count == 2 && using_.Rows[0].Link == "spell:133" && using_.Rows[0].Title == "Fireball" && using_.Rows[0].Detail == "seen 2 times" && using_.Rows[1].Detail == "seen once"
                    && using_.Rows[0].SourceKey == DataSources.You && using_.Note == AtlasQuery.SeenUsingNote, using_ == null ? "no section" : string.Join(" | ", using_.Rows));
                Check("NPC page: its health at the levels it was met at", page.Facts.Any(x => x.Label == "Health" && x.Value == "320 at level 13") && page.Facts.Any(x => x.Label == "Also" && x.Value == "300 at level 12"), string.Join("; ", page.Facts));
                var seenAt = page.Sections.FirstOrDefault(s => s.Key == "seenat");
                Check("NPC page: where it was fought is a \"Fought\" spot in \"Seen at\", said to be from your combat log, and on its map",
                    seenAt != null && seenAt.Rows.Count == 1 && seenAt.Rows[0].Type == "Fought" && seenAt.Note.Contains("combat log") && page.Map.MarkerCount >= 1, seenAt == null ? "none" : string.Join(" | ", seenAt.Rows) + " / " + seenAt.Note);
                Check("world map: the fought spots are in the NPC spot layer too", AtlasQuery.AllNpcSpots().TryGetValue(466, out var all) && all.Any(x => x.Kind == AtlasObs.NpcSpotCombat));
                var plain = AtlasQuery.Build("npc:70");
                Check("NPC page of a creature the log never saw: no section, no health", !plain.Sections.Any(s => s.Key == "seenusing") && !plain.Facts.Any(x => x.Label == "Health"));
                AtlasQuery.Context.CombatLog = () => null;
                Check("no combat log at all: no section", !AtlasQuery.Build("npc:466").Sections.Any(s => s.Key == "seenusing"));
                Check("Data sources: \"You\" names the combat log", DataSources.What(DataSources.You).Contains("combat log"), DataSources.What(DataSources.You));
            }
            catch (Exception e) { Check("combat log test ran", false, e.ToString()); }
            finally
            {
                AtlasQuery.Context.CombatLog = savedCl; AtlasQuery.Context.Lodge = savedLodge; AtlasQuery.Context.OwnNpcSpots = savedOwn;
            }
            await Task.CompletedTask;
            return "combatlog (2.95, the game's combat log for the Atlas): " + (ok ? "OK" : "FAILURES") + "\r\n" + string.Join("\r\n", notes);
        }
    }
}
