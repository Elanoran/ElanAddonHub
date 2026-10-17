using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using ElansAddonHub.Services.Atlas.Classic;

namespace ElansAddonHub.Services.DungeonMaps
{
    // ============================================================================================================================
    // The creatures inside instances, read from the Classic baseline the player already downloaded (cMaNGOS classic-db, GPL-3.0: the dump and its update
    // scripts in <data>\atlas\classic\). The Classic index keeps only what the Atlas pages need; the dungeon map needs more (heights, patrol paths, linked
    // packs, the spells a creature casts, the bosses in the game's order, where the entrance puts you), so this reads its own tables from the same files
    // with the same SQL reader and keeps only the maps that are not the two continents. Saved as one small file per baseline (DungeonMobStore).
    //   creature (+ creature_spawn_entry)       spawns: map, x, y, z, movement (0 still, 1 wanders, 2 walks a path), wander distance
    //   creature_movement / _template           patrol paths (by spawn guid, else by creature, path 0)
    //   creature_linking / _template            "pulls with": a spawn follows its master into combat
    //   creature_template                       name, level, rank, type, flags (unit, extra: 0x1 = kill binds the instance = a boss), immunities, spell list
    //   creature_template_spells, creature_spell_list, creature_ai_scripts (EventAI)
    //                                           the spells it casts (action 11), the creatures it summons (12, 32), fleeing for help (25), calling for help (39)
    //   creature_template_addon                 auras it always has (stealth)
    //   spell_template                          name, dispel type, mechanic, effects and their auras: what a warning may claim
    //   instance_dungeon_encounters + instance_encounters   the bosses of a map in the game's order and the creature whose kill counts
    //   areatrigger_teleport                    where the entrance puts a player inside the instance
    // ============================================================================================================================
    public sealed class MobTemplate
    {
        public int Id, MinLevel, MaxLevel, Rank, Type, NpcFlags, UnitFlags, ExtraFlags, Faction, MechanicImmune, SchoolImmune;
        public string Name = "", SubName = "";
        public int[] Spells = new int[0], Summons = new int[0], Auras = new int[0];
        public bool Flees, CallsHelp;
        public bool BindsInstance => (ExtraFlags & 0x1) != 0;
    }

    public sealed class MobSpawn
    {
        public int Guid, Entry, Map, Movement;
        public int[] Others = new int[0];          // the other creatures this spawn can be (creature_spawn_entry)
        public float X, Y, Z, Wander;
        public float[] Path = new float[0];         // x, y, z per point (Movement 2)
    }

    public sealed class MobSpell
    {
        public int Id, Dispel, Mechanic;
        public string Name = "";
        public int[] Effect = new int[3], Aura = new int[3], EffectMechanic = new int[3], TargetA = new int[3], TargetB = new int[3];
        public bool OnSelf(int k) => TargetA[k] == 1 && TargetB[k] == 0;       // the caster only (a buff, a form)
    }

    public sealed class MobEncounter { public int Map, Index, CreditNpc; public string Name = ""; }
    public sealed class MobEntrance { public int Map; public float X, Y, Z; public string Name = ""; }
    public sealed class MobLinkTemplate { public int Entry, Map, Master; public float Range; }

    public sealed class DungeonMobData
    {
        public const int Format = 2;
        public string Key = "";                                                   // the baseline it was read from
        public readonly Dictionary<int, MobTemplate> Templates = new Dictionary<int, MobTemplate>();
        public readonly Dictionary<int, List<MobSpawn>> SpawnsByMap = new Dictionary<int, List<MobSpawn>>();
        public readonly Dictionary<int, int> LinkMaster = new Dictionary<int, int>();   // spawn guid -> master spawn guid
        public readonly List<MobLinkTemplate> LinkTemplates = new List<MobLinkTemplate>();
        public readonly Dictionary<int, MobSpell> Spells = new Dictionary<int, MobSpell>();
        public readonly List<MobEncounter> Encounters = new List<MobEncounter>();
        public readonly List<MobEntrance> Entrances = new List<MobEntrance>();

        public IReadOnlyList<MobSpawn> SpawnsOn(int map) => SpawnsByMap.TryGetValue(map, out var l) ? l : (IReadOnlyList<MobSpawn>)new MobSpawn[0];
        public MobTemplate Template(int id) => Templates.TryGetValue(id, out var t) ? t : null;
        public MobSpell Spell(int id) => Spells.TryGetValue(id, out var s) ? s : null;
        public IEnumerable<MobEncounter> EncountersOf(int map) => Encounters.Where(e => e.Map == map).OrderBy(e => e.Index);
        public IEnumerable<MobEntrance> EntrancesOf(int map) => Entrances.Where(e => e.Map == map);

        // ---------------------------------------------------------------- reading the baseline's SQL
        static ClassicSchema.Spec S(string name, string pk, string cols) => new ClassicSchema.Spec { Name = name, Pk = pk.Split(','), Cols = cols.Split(',') };

        static readonly Dictionary<string, ClassicSchema.Spec> Tables = new[]
        {
            S("creature", "guid", "guid,id,map,position_x,position_y,position_z,spawndist,movementtype"),
            S("creature_spawn_entry", "guid,entry", "guid,entry"),
            S("creature_movement", "id,point", "id,point,positionx,positiony,positionz"),
            S("creature_movement_template", "entry,pathid,point", "entry,pathid,point,positionx,positiony,positionz"),
            S("creature_linking", "guid", "guid,master_guid,flag"),
            S("creature_linking_template", "entry,map", "entry,map,master_entry,flag,search_range"),
            S("creature_template", "entry", "entry,name,subname,minlevel,maxlevel,faction,creaturetype,npcflags,unitflags,extraflags,rank,mechanicimmunemask,schoolimmunemask,spelllist"),
            S("creature_template_spells", "entry,setid", "entry,setid,spell1,spell2,spell3,spell4,spell5,spell6,spell7,spell8,spell9,spell10"),
            S("creature_spell_list", "id,position", "id,position,spellid"),
            S("creature_ai_scripts", "id", "id,creature_id,action1_type,action1_param1,action2_type,action2_param1,action3_type,action3_param1"),
            S("creature_template_addon", "entry", "entry,auras"),
            S("spell_template", "id", "id,dispel,mechanic,effect1,effect2,effect3,effectapplyauraname1,effectapplyauraname2,effectapplyauraname3,effectmechanic1,effectmechanic2,effectmechanic3,effectimplicittargeta1,effectimplicittargeta2,effectimplicittargeta3,effectimplicittargetb1,effectimplicittargetb2,effectimplicittargetb3,spellname"),
            S("instance_dungeon_encounters", "id", "id,mapid,difficulty,encounterindex,encountername"),
            S("instance_encounters", "entry", "entry,credittype,creditentry"),
            S("areatrigger_teleport", "id", "id,name,target_map,target_position_x,target_position_y,target_position_z"),
        }.ToDictionary(x => x.Name);

        public static bool KeepsMap(int map) => map != 0 && map != 1;           // everything but the two continents

        // the dump (.sql.gz) and the update scripts -> the data; null = cancelled
        public static DungeonMobData Read(string dumpGz, string updatesDir, string key, CancellationToken ct)
        {
            var exec = new ClassicSqlExec(Tables);
            var rep = new ClassicImportReport();
            using (var fs = new FileStream(dumpGz, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var gz = new GZipStream(fs, CompressionMode.Decompress))
            using (var sr = new StreamReader(gz, new UTF8Encoding(false), false, 1 << 18))
                ClassicImporter.Run(sr, exec, "dump", ct, null, rep, true);
            if (ct.IsCancellationRequested) return null;
            var files = new List<KeyValuePair<string, Func<TextReader>>>();
            if (updatesDir != null && Directory.Exists(updatesDir))
                foreach (var f in Directory.GetFiles(updatesDir, "*.sql")) { var path = f; files.Add(new KeyValuePair<string, Func<TextReader>>(Path.GetFileName(f), () => new StreamReader(path, new UTF8Encoding(false), true, 1 << 16))); }
            ClassicImporter.ApplyUpdates(exec, files, rep, null, ct, 0, 1);
            if (ct.IsCancellationRequested) return null;
            var d = From(exec);
            d.Key = key ?? "";
            return d;
        }

        // for tests: SQL as text
        public static DungeonMobData FromText(string sql, string key = "test")
        {
            var exec = new ClassicSqlExec(Tables);
            using (var sr = new StringReader(sql)) ClassicImporter.Run(sr, exec, "dump", CancellationToken.None, null, new ClassicImportReport(), true);
            var d = From(exec); d.Key = key; return d;
        }

        static int I(object[] r, int i) => (int)SqlVal.ToLong(r[i]);
        static float F(object[] r, int i) => (float)SqlVal.ToDouble(r[i]);
        static string Str(object[] r, int i) => SqlVal.ToStr(r[i]) ?? "";

        static DungeonMobData From(ClassicSqlExec exec)
        {
            var T = exec.Tables;
            var d = new DungeonMobData();

            // spawns on the kept maps
            var extra = new Dictionary<int, List<int>>();
            { var t = T["creature_spawn_entry"]; int g = t.Idx["guid"], e = t.Idx["entry"]; foreach (var r in t.Rows) { int guid = I(r, g); if (!extra.TryGetValue(guid, out var l)) extra[guid] = l = new List<int>(); l.Add(I(r, e)); } }
            var byGuid = new Dictionary<int, MobSpawn>();
            {
                var t = T["creature"];
                int g = t.Idx["guid"], id = t.Idx["id"], m = t.Idx["map"], x = t.Idx["position_x"], y = t.Idx["position_y"], z = t.Idx["position_z"], sd = t.Idx["spawndist"], mt = t.Idx["movementtype"];
                foreach (var r in t.Rows)
                {
                    int map = I(r, m); if (!KeepsMap(map)) continue;
                    int guid = I(r, g), entry = I(r, id);
                    var others = extra.TryGetValue(guid, out var ol) ? ol.Where(o => o > 0 && o != entry).Distinct().ToArray() : new int[0];
                    if (entry <= 0) { if (others.Length == 0) continue; entry = others[0]; others = others.Skip(1).ToArray(); }
                    var s = new MobSpawn { Guid = guid, Entry = entry, Others = others, Map = map, X = F(r, x), Y = F(r, y), Z = F(r, z), Wander = F(r, sd), Movement = I(r, mt) };
                    byGuid[guid] = s;
                    if (!d.SpawnsByMap.TryGetValue(map, out var l)) d.SpawnsByMap[map] = l = new List<MobSpawn>();
                    l.Add(s);
                }
            }
            var entries = new HashSet<int>(byGuid.Values.SelectMany(s => new[] { s.Entry }.Concat(s.Others)));

            // patrol paths
            {
                var t = T["creature_movement"]; int id = t.Idx["id"], p = t.Idx["point"], x = t.Idx["positionx"], y = t.Idx["positiony"], z = t.Idx["positionz"];
                foreach (var grp in t.Rows.Where(r => byGuid.ContainsKey(I(r, id))).GroupBy(r => I(r, id)))
                    byGuid[grp.Key].Path = grp.OrderBy(r => I(r, p)).SelectMany(r => new[] { F(r, x), F(r, y), F(r, z) }).ToArray();
                var tt = T["creature_movement_template"]; int e = tt.Idx["entry"], pid = tt.Idx["pathid"], tp = tt.Idx["point"], tx = tt.Idx["positionx"], ty = tt.Idx["positiony"], tz = tt.Idx["positionz"];
                var byEntry = tt.Rows.Where(r => I(r, pid) == 0 && entries.Contains(I(r, e))).GroupBy(r => I(r, e))
                    .ToDictionary(gr => gr.Key, gr => gr.OrderBy(r => I(r, tp)).SelectMany(r => new[] { F(r, tx), F(r, ty), F(r, tz) }).ToArray());
                foreach (var s in byGuid.Values) if (s.Movement == 2 && s.Path.Length == 0 && byEntry.TryGetValue(s.Entry, out var path)) s.Path = path;
            }

            // linked spawns
            { var t = T["creature_linking"]; int g = t.Idx["guid"], mg = t.Idx["master_guid"]; foreach (var r in t.Rows) if (byGuid.ContainsKey(I(r, g)) && byGuid.ContainsKey(I(r, mg))) d.LinkMaster[I(r, g)] = I(r, mg); }
            {
                var t = T["creature_linking_template"]; int e = t.Idx["entry"], m = t.Idx["map"], me = t.Idx["master_entry"], sr = t.Idx["search_range"];
                foreach (var r in t.Rows) if (KeepsMap(I(r, m)) && entries.Contains(I(r, e))) d.LinkTemplates.Add(new MobLinkTemplate { Entry = I(r, e), Map = I(r, m), Master = I(r, me), Range = F(r, sr) });
            }

            // creatures: what they cast, summon and do
            var spellLists = new Dictionary<int, List<int>>();
            { var t = T["creature_spell_list"]; int id = t.Idx["id"], sp = t.Idx["spellid"]; foreach (var r in t.Rows) { if (!spellLists.TryGetValue(I(r, id), out var l)) spellLists[I(r, id)] = l = new List<int>(); l.Add(I(r, sp)); } }
            var tmplSpells = new Dictionary<int, List<int>>();
            {
                var t = T["creature_template_spells"]; int e = t.Idx["entry"];
                foreach (var r in t.Rows)
                {
                    if (!tmplSpells.TryGetValue(I(r, e), out var l)) tmplSpells[I(r, e)] = l = new List<int>();
                    for (int k = 1; k <= 10; k++) { int s = I(r, t.Idx["spell" + k]); if (s > 0) l.Add(s); }
                }
            }
            var ai = new Dictionary<int, List<int[]>>();      // creature -> { action, param }
            {
                var t = T["creature_ai_scripts"]; int c = t.Idx["creature_id"];
                foreach (var r in t.Rows)
                {
                    if (!ai.TryGetValue(I(r, c), out var l)) ai[I(r, c)] = l = new List<int[]>();
                    for (int k = 1; k <= 3; k++) { int a = I(r, t.Idx["action" + k + "_type"]); if (a != 0) l.Add(new[] { a, I(r, t.Idx["action" + k + "_param1"]) }); }
                }
            }
            var auras = new Dictionary<int, int[]>();
            {
                var t = T["creature_template_addon"]; int e = t.Idx["entry"], a = t.Idx["auras"];
                foreach (var r in t.Rows) auras[I(r, e)] = Str(r, a).Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => int.TryParse(x, out var v) ? v : 0).Where(v => v > 0).ToArray();
            }
            var allTemplates = T["creature_template"];
            var tIdx = allTemplates.Idx;
            var rowsByEntry = new Dictionary<int, object[]>();
            foreach (var r in allTemplates.Rows) rowsByEntry[I(r, tIdx["entry"])] = r;
            var want = new Queue<int>(entries);
            var seen = new HashSet<int>();
            while (want.Count > 0)
            {
                int id = want.Dequeue();
                if (!seen.Add(id) || !rowsByEntry.TryGetValue(id, out var r)) continue;
                var m = new MobTemplate
                {
                    Id = id, Name = Str(r, tIdx["name"]), SubName = Str(r, tIdx["subname"]), MinLevel = I(r, tIdx["minlevel"]), MaxLevel = I(r, tIdx["maxlevel"]), Rank = I(r, tIdx["rank"]),
                    Type = I(r, tIdx["creaturetype"]), NpcFlags = I(r, tIdx["npcflags"]), UnitFlags = I(r, tIdx["unitflags"]), ExtraFlags = I(r, tIdx["extraflags"]), Faction = I(r, tIdx["faction"]),
                    MechanicImmune = I(r, tIdx["mechanicimmunemask"]), SchoolImmune = I(r, tIdx["schoolimmunemask"]),
                };
                var spells = new List<int>(); var summons = new List<int>();
                if (tmplSpells.TryGetValue(id, out var ts)) spells.AddRange(ts);
                if (spellLists.TryGetValue(I(r, tIdx["spelllist"]), out var sl)) spells.AddRange(sl);
                if (ai.TryGetValue(id, out var acts))
                    foreach (var a in acts)
                    {
                        if (a[0] == 11 && a[1] > 0) spells.Add(a[1]);
                        else if ((a[0] == 12 || a[0] == 32) && a[1] > 0) { if (a[0] == 12) summons.Add(a[1]); }
                        else if (a[0] == 25) m.Flees = true;
                        else if (a[0] == 39) m.CallsHelp = true;
                    }
                m.Spells = spells.Distinct().ToArray();
                m.Summons = summons.Distinct().ToArray();
                if (auras.TryGetValue(id, out var au)) m.Auras = au;
                d.Templates[id] = m;
                foreach (var s in m.Summons) want.Enqueue(s);
            }

            // the spells they use
            var spellIds = new HashSet<int>(d.Templates.Values.SelectMany(t => t.Spells.Concat(t.Auras)));
            {
                var t = T["spell_template"];
                foreach (var r in t.Rows)
                {
                    int id = I(r, t.Idx["id"]); if (!spellIds.Contains(id)) continue;
                    var s = new MobSpell { Id = id, Dispel = I(r, t.Idx["dispel"]), Mechanic = I(r, t.Idx["mechanic"]), Name = Str(r, t.Idx["spellname"]) };
                    for (int k = 0; k < 3; k++) { s.Effect[k] = I(r, t.Idx["effect" + (k + 1)]); s.Aura[k] = I(r, t.Idx["effectapplyauraname" + (k + 1)]); s.EffectMechanic[k] = I(r, t.Idx["effectmechanic" + (k + 1)]); s.TargetA[k] = I(r, t.Idx["effectimplicittargeta" + (k + 1)]); s.TargetB[k] = I(r, t.Idx["effectimplicittargetb" + (k + 1)]); }
                    d.Spells[id] = s;
                }
            }

            // bosses in the game's order, and the entrances
            {
                var credit = new Dictionary<int, int>();
                var t = T["instance_encounters"]; foreach (var r in t.Rows) if (I(r, t.Idx["credittype"]) == 0) credit[I(r, t.Idx["entry"])] = I(r, t.Idx["creditentry"]);
                var e = T["instance_dungeon_encounters"];
                foreach (var r in e.Rows)
                {
                    int id = I(r, e.Idx["id"]);
                    d.Encounters.Add(new MobEncounter { Map = I(r, e.Idx["mapid"]), Index = I(r, e.Idx["encounterindex"]), Name = Str(r, e.Idx["encountername"]), CreditNpc = credit.TryGetValue(id, out var c) ? c : 0 });
                }
                var a = T["areatrigger_teleport"];
                foreach (var r in a.Rows)
                {
                    int map = I(r, a.Idx["target_map"]); if (!KeepsMap(map)) continue;
                    d.Entrances.Add(new MobEntrance { Map = map, X = F(r, a.Idx["target_position_x"]), Y = F(r, a.Idx["target_position_y"]), Z = F(r, a.Idx["target_position_z"]), Name = Str(r, a.Idx["name"]) });
                }
            }
            return d;
        }

        // ---------------------------------------------------------------- the saved file ("EDMB", format, key, then the tables; deflated)
        static readonly byte[] Magic = { (byte)'E', (byte)'D', (byte)'M', (byte)'B' };

        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(Magic, 0, 4);
                using (var z = new DeflateStream(fs, CompressionLevel.Optimal))
                using (var w = new BinaryWriter(z, Encoding.UTF8))
                {
                    w.Write(Format); w.Write(Key);
                    w.Write(Templates.Count);
                    foreach (var t in Templates.Values)
                    {
                        w.Write(t.Id); w.Write(t.Name); w.Write(t.SubName); w.Write(t.MinLevel); w.Write(t.MaxLevel); w.Write(t.Rank); w.Write(t.Type); w.Write(t.NpcFlags); w.Write(t.UnitFlags);
                        w.Write(t.ExtraFlags); w.Write(t.Faction); w.Write(t.MechanicImmune); w.Write(t.SchoolImmune); w.Write(t.Flees); w.Write(t.CallsHelp);
                        Ints(w, t.Spells); Ints(w, t.Summons); Ints(w, t.Auras);
                    }
                    var spawns = SpawnsByMap.Values.SelectMany(l => l).ToList();
                    w.Write(spawns.Count);
                    foreach (var s in spawns)
                    {
                        w.Write(s.Guid); w.Write(s.Entry); w.Write(s.Map); w.Write(s.Movement); w.Write(s.X); w.Write(s.Y); w.Write(s.Z); w.Write(s.Wander);
                        Ints(w, s.Others); w.Write(s.Path.Length); foreach (var f in s.Path) w.Write(f);
                    }
                    w.Write(LinkMaster.Count); foreach (var kv in LinkMaster) { w.Write(kv.Key); w.Write(kv.Value); }
                    w.Write(LinkTemplates.Count); foreach (var l in LinkTemplates) { w.Write(l.Entry); w.Write(l.Map); w.Write(l.Master); w.Write(l.Range); }
                    w.Write(Spells.Count);
                    foreach (var s in Spells.Values) { w.Write(s.Id); w.Write(s.Name); w.Write(s.Dispel); w.Write(s.Mechanic); for (int k = 0; k < 3; k++) { w.Write(s.Effect[k]); w.Write(s.Aura[k]); w.Write(s.EffectMechanic[k]); w.Write(s.TargetA[k]); w.Write(s.TargetB[k]); } }
                    w.Write(Encounters.Count); foreach (var e in Encounters) { w.Write(e.Map); w.Write(e.Index); w.Write(e.CreditNpc); w.Write(e.Name); }
                    w.Write(Entrances.Count); foreach (var e in Entrances) { w.Write(e.Map); w.Write(e.X); w.Write(e.Y); w.Write(e.Z); w.Write(e.Name); }
                    w.Write(0x424D4445);       // end marker
                }
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        static void Ints(BinaryWriter w, int[] a) { w.Write(a.Length); foreach (var v in a) w.Write(v); }
        static int[] Ints(BinaryReader r) { int n = Count(r); var a = new int[n]; for (int i = 0; i < n; i++) a[i] = r.ReadInt32(); return a; }
        static int Count(BinaryReader r) { int n = r.ReadInt32(); if (n < 0 || n > 5000000) throw new InvalidDataException("count " + n); return n; }

        // null = missing, damaged or another format
        public static DungeonMobData Load(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var m = new byte[4];
                    if (fs.Read(m, 0, 4) != 4 || !m.SequenceEqual(Magic)) return null;
                    using (var z = new DeflateStream(fs, CompressionMode.Decompress))
                    using (var r = new BinaryReader(z, Encoding.UTF8))
                    {
                        if (r.ReadInt32() != Format) return null;
                        var d = new DungeonMobData { Key = r.ReadString() };
                        for (int i = 0, n = Count(r); i < n; i++)
                        {
                            var t = new MobTemplate
                            {
                                Id = r.ReadInt32(), Name = r.ReadString(), SubName = r.ReadString(), MinLevel = r.ReadInt32(), MaxLevel = r.ReadInt32(), Rank = r.ReadInt32(), Type = r.ReadInt32(), NpcFlags = r.ReadInt32(),
                                UnitFlags = r.ReadInt32(), ExtraFlags = r.ReadInt32(), Faction = r.ReadInt32(), MechanicImmune = r.ReadInt32(), SchoolImmune = r.ReadInt32(), Flees = r.ReadBoolean(), CallsHelp = r.ReadBoolean(),
                            };
                            t.Spells = Ints(r); t.Summons = Ints(r); t.Auras = Ints(r);
                            d.Templates[t.Id] = t;
                        }
                        for (int i = 0, n = Count(r); i < n; i++)
                        {
                            var s = new MobSpawn { Guid = r.ReadInt32(), Entry = r.ReadInt32(), Map = r.ReadInt32(), Movement = r.ReadInt32(), X = r.ReadSingle(), Y = r.ReadSingle(), Z = r.ReadSingle(), Wander = r.ReadSingle() };
                            s.Others = Ints(r);
                            int pn = Count(r); s.Path = new float[pn]; for (int k = 0; k < pn; k++) s.Path[k] = r.ReadSingle();
                            if (!d.SpawnsByMap.TryGetValue(s.Map, out var l)) d.SpawnsByMap[s.Map] = l = new List<MobSpawn>();
                            l.Add(s);
                        }
                        for (int i = 0, n = Count(r); i < n; i++) d.LinkMaster[r.ReadInt32()] = r.ReadInt32();
                        for (int i = 0, n = Count(r); i < n; i++) d.LinkTemplates.Add(new MobLinkTemplate { Entry = r.ReadInt32(), Map = r.ReadInt32(), Master = r.ReadInt32(), Range = r.ReadSingle() });
                        for (int i = 0, n = Count(r); i < n; i++)
                        {
                            var s = new MobSpell { Id = r.ReadInt32(), Name = r.ReadString(), Dispel = r.ReadInt32(), Mechanic = r.ReadInt32() };
                            for (int k = 0; k < 3; k++) { s.Effect[k] = r.ReadInt32(); s.Aura[k] = r.ReadInt32(); s.EffectMechanic[k] = r.ReadInt32(); s.TargetA[k] = r.ReadInt32(); s.TargetB[k] = r.ReadInt32(); }
                            d.Spells[s.Id] = s;
                        }
                        for (int i = 0, n = Count(r); i < n; i++) d.Encounters.Add(new MobEncounter { Map = r.ReadInt32(), Index = r.ReadInt32(), CreditNpc = r.ReadInt32(), Name = r.ReadString() });
                        for (int i = 0, n = Count(r); i < n; i++) d.Entrances.Add(new MobEntrance { Map = r.ReadInt32(), X = r.ReadSingle(), Y = r.ReadSingle(), Z = r.ReadSingle(), Name = r.ReadString() });
                        if (r.ReadInt32() != 0x424D4445) return null;
                        return d;
                    }
                }
            }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is EndOfStreamException || e is UnauthorizedAccessException || e is ArgumentException) { return null; }
        }
    }
}
