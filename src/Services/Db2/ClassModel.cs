using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace ElansAddonHub.Services.Db2
{
    // ============================================================================================================================
    // Atlas: classes and races (ChrClasses, ChrRaces, CharBaseInfo ...) and the per-spell facts the spell pages and the class page use (the level a spell is
    // learned at, its cost, its cooldown), read from the player's OWN game files. Pure model + parsing here (bytes in, model out) so the self-test can feed
    // it synthetic tables; ClassData is the service around it.
    // ============================================================================================================================

    public sealed class ClassInfo
    {
        public int Id, IconId, Flags, StartingLevel, DisplayPower, Roles;
        public int ColorR, ColorG, ColorB;
        public string Name = "", File = "", Description = "";
        public int[] PowerTypes = new int[0];
        public int Mask => Id > 0 && Id < 31 ? 1 << (Id - 1) : 0;                 // the bit of this class in every ClassMask of the client
        public string ColorHex => "#" + ColorR.ToString("X2", CultureInfo.InvariantCulture) + ColorG.ToString("X2", CultureInfo.InvariantCulture) + ColorB.ToString("X2", CultureInfo.InvariantCulture);
        public bool HasColor => (ColorR | ColorG | ColorB) != 0;
        public const int RoleTank = 2, RoleHealer = 4, RoleDamage = 8;
        public string RoleText
        {
            get
            {
                var l = new List<string>();
                if ((Roles & RoleTank) != 0) l.Add("Tank");
                if ((Roles & RoleHealer) != 0) l.Add("Healer");
                if ((Roles & RoleDamage) != 0) l.Add("Damage");
                return string.Join(", ", l);
            }
        }
        public override string ToString() => Id + " " + Name;
    }

    public sealed class RaceInfo
    {
        public const int Alliance = 0, Horde = 1;
        public int Id, Flags, FactionTemplate, StartingLevel, Order, Bit, Side, BaseLanguage, DefaultClass;
        public string Name = "", LoreName = "", ClientFile = "", Description = "";
        public bool Playable;                                                       // appears in CharBaseInfo: a player can create it
        public ulong BitMask => Bit >= 0 && Bit < 64 ? 1UL << Bit : 0;
        public string SideName => Side == Alliance ? "Alliance" : Side == Horde ? "Horde" : "Neutral";
        public override string ToString() => Id + " " + Name + (Playable ? "" : " (not playable)");
    }

    // one row of SkillRaceClassInfo: which classes / races a skill line is for, and from which level
    public struct SkillClassRow
    {
        public int Skill, ClassMask, Flags, Availability, MinLevel;
        public ulong Races;
        public bool ForClass(int mask) => ClassMask == -1 || (ClassMask & mask) != 0;
        public bool ForRace(ulong bit) => Races == ulong.MaxValue || (Races & bit) != 0;
    }

    public struct SpellLevelInfo
    {
        public int Base, Spell;                    // SpellLevels.BaseLevel (the level a player can learn it at), SpellLevels.SpellLevel (the spell's own level)
        public override string ToString() => Base + "/" + Spell;
    }

    public struct SpellCostInfo
    {
        public int Type;                           // power type: 0 mana, 1 rage, 2 focus, 3 energy, 4 combo points; -2 health
        public int Amount;                         // as stored (rage is stored x10)
        public float Pct;                          // percent of base mana / health (0 = a fixed amount)
        public override string ToString() => Type + ":" + Amount + "/" + Pct;
    }

    public struct SpellCooldownInfo
    {
        public int Recovery, Category, Global;     // ms: the spell's own cooldown, its category's cooldown, the global cooldown it triggers
        public int Ms => Recovery > 0 ? Recovery : Category;
    }

    public sealed class ClassSnapshot
    {
        public readonly Dictionary<int, ClassInfo> Classes = new Dictionary<int, ClassInfo>();
        public readonly Dictionary<int, RaceInfo> Races = new Dictionary<int, RaceInfo>();
        public readonly HashSet<long> Pairs = new HashSet<long>();                                    // race << 8 | class
        public readonly List<SkillClassRow> SkillRows = new List<SkillClassRow>();
        public readonly Dictionary<int, SpellLevelInfo> Levels = new Dictionary<int, SpellLevelInfo>();
        public readonly Dictionary<int, SpellCostInfo> Costs = new Dictionary<int, SpellCostInfo>();
        public readonly Dictionary<int, SpellCooldownInfo> Cooldowns = new Dictionary<int, SpellCooldownInfo>();
        public bool HasSkillRows, HasLevels, HasCosts, HasCooldowns;

        public static long PairKey(int race, int cls) => ((long)race << 8) | (uint)cls;
        public bool CanBe(int race, int cls) => Pairs.Contains(PairKey(race, cls));

        public ClassInfo Class(int id) => Classes.TryGetValue(id, out var c) ? c : null;
        public RaceInfo Race(int id) => Races.TryGetValue(id, out var r) ? r : null;
        public ClassInfo ClassByFile(string file) => Classes.Values.FirstOrDefault(c => string.Equals(c.File, file, StringComparison.OrdinalIgnoreCase));
        public ClassInfo ClassByName(string name) => Classes.Values.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        public RaceInfo RaceByName(string name)
            => Races.Values.Where(r => r.Playable).FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase) || string.Equals(r.LoreName, name, StringComparison.OrdinalIgnoreCase))
               ?? Races.Values.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));

        // the classes by id, the playable races by side, then the client's display order, then name
        public List<ClassInfo> ClassList() => Classes.Values.OrderBy(c => c.Id).ToList();
        IEnumerable<RaceInfo> Sorted(IEnumerable<RaceInfo> l) => l.OrderBy(r => r.Side).ThenBy(r => r.Order == 0 ? 99 : r.Order).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Id);
        public List<RaceInfo> PlayableRaces() => Sorted(Races.Values.Where(r => r.Playable)).ToList();
        public List<RaceInfo> RacesOf(int cls) => Sorted(Races.Values.Where(r => r.Playable && CanBe(r.Id, cls))).ToList();
        public List<ClassInfo> ClassesOf(int race) => Classes.Values.Where(c => CanBe(race, c.Id)).OrderBy(c => c.Id).ToList();

        // ---- skill lines of a class / race
        public const int CatWeapon = 6, CatClass = 7, CatArmor = 8, CatSecondary = 9, CatLanguage = 10;

        // the lines of the spellbook tabs of one class: class skills (and the class' own secondary skills, like the rogue's Poisons) whose class mask is exactly this class
        public List<int> TabSkills(int cls, Func<int, int> categoryOf)
        {
            var c = Class(cls);
            var res = new List<int>();
            if (c == null) return res;
            foreach (var r in SkillRows)
            {
                if (r.ClassMask != c.Mask || res.Contains(r.Skill)) continue;
                int cat = categoryOf(r.Skill);
                if (cat == CatClass || cat == CatSecondary) res.Add(r.Skill);
            }
            return res;
        }

        // weapon / armor skill lines the class can use, with the lowest level any row of it opens at
        public Dictionary<int, int> Proficiencies(int cls, int category, Func<int, int> categoryOf)
        {
            var c = Class(cls);
            var res = new Dictionary<int, int>();
            if (c == null) return res;
            foreach (var r in SkillRows)
            {
                if (r.ClassMask == -1 || (r.ClassMask & c.Mask) == 0 || categoryOf(r.Skill) != category) continue;
                if (!res.TryGetValue(r.Skill, out var lvl) || r.MinLevel < lvl) res[r.Skill] = r.MinLevel;
            }
            return res;
        }

        // rows of skill lines of one category that name this race (a race bit in the mask, not everyone): racial abilities, languages, riding skills
        public List<SkillClassRow> RaceRows(RaceInfo race, Func<int, int> categoryOf, int category)
            => SkillRows.Where(r => r.Races != ulong.MaxValue && r.Races != 0 && (r.Races & race.BitMask) != 0 && categoryOf(r.Skill) == category).ToList();

        // ---- per-spell facts
        public SpellLevelInfo? Level(int spell) => Levels.TryGetValue(spell, out var l) ? l : (SpellLevelInfo?)null;

        public static string PowerName(int type)
        {
            switch (type)
            {
                case 0: return "Mana"; case 1: return "Rage"; case 2: return "Focus"; case 3: return "Energy"; case 4: return "Combo Points";
                case -2: return "Health";
                default: return "Power " + type.ToString(CultureInfo.InvariantCulture);
            }
        }

        // "50 Mana", "15 Rage", "4% of base mana" (the game's own wording); null when the spell costs nothing or the cost table is not there
        public string CostText(int spell)
        {
            if (!Costs.TryGetValue(spell, out var c)) return null;
            if (c.Amount > 0)
            {
                int amount = c.Type == 1 ? c.Amount / 10 : c.Amount;
                return amount <= 0 ? null : amount.ToString(CultureInfo.InvariantCulture) + " " + PowerName(c.Type);
            }
            if (c.Pct > 0.0001f)
            {
                var p = c.Pct.ToString("0.##", CultureInfo.InvariantCulture);
                return c.Type == 0 ? p + "% of base mana" : c.Type == -2 ? p + "% of base health" : p + "% of base " + PowerName(c.Type).ToLowerInvariant();
            }
            return null;
        }

        public int CooldownMs(int spell) => Cooldowns.TryGetValue(spell, out var c) ? c.Ms : 0;
        public static string CooldownText(int ms)
        {
            if (ms <= 0) return null;
            var inv = CultureInfo.InvariantCulture;
            if (ms >= 3600000 && ms % 3600000 == 0) return (ms / 3600000).ToString(inv) + " hr cooldown";
            if (ms >= 60000) { double m = ms / 60000.0; return (Math.Abs(m - Math.Round(m)) < 0.001 ? ((int)Math.Round(m)).ToString(inv) : m.ToString("0.#", inv)) + " min cooldown"; }
            return (ms / 1000.0).ToString("0.##", inv) + " sec cooldown";
        }
    }

    public sealed class ClassSet
    {
        public ClassSnapshot Data = new ClassSnapshot();
        public readonly Dictionary<string, string> Problems = new Dictionary<string, string>();      // table -> why it is unavailable ("all" = the whole layer)
        public readonly List<string> Notes = new List<string>();
        public readonly Dictionary<string, uint> LayoutsSeen = new Dictionary<string, uint>();
    }

    public sealed class ClassRaw
    {
        public Dictionary<string, byte[]> Tables = new Dictionary<string, byte[]>();
        public byte[] Get(SpellTables.Def d) => Tables.TryGetValue(d.Name, out var b) ? b : null;
    }

    // ============================================================================================================================ the parsing
    public static class ClassParse
    {
        sealed class Tab { public Wdc5 T; public bool Ok => T != null; }

        static Tab Open(ClassRaw raw, SpellTables.Def def, ClassSet set, bool required)
        {
            var o = SpellTables.Open(raw.Get(def), def);
            if (o.Layout != 0) set.LayoutsSeen[def.Name] = o.Layout;
            if (!o.Ok)
            {
                if (required) { if (!set.Problems.ContainsKey("all")) set.Problems["all"] = o.Problem; }
                else set.Problems[def.Name] = o.Problem;
                return new Tab();
            }
            if (o.Table.EncryptedRecords > 0) set.Notes.Add(def.Name + ": " + o.Table.EncryptedRecords.ToString(CultureInfo.InvariantCulture) + " rows are key-locked in the game files and skipped");
            return new Tab { T = o.Table };
        }

        public static ClassSet All(ClassRaw raw)
        {
            var set = new ClassSet();
            var s = set.Data;
            // required: classes, races, the race x class pairs. The first one missing / unknown makes the layer unavailable, with its reason.
            var cls = Open(raw, ClassTables.Classes, set, true);
            var rac = Open(raw, ClassTables.Races, set, true);
            var cb = Open(raw, ClassTables.CharBase, set, true);
            if (!cls.Ok || !rac.Ok || !cb.Ok) return set;

            for (int i = 0; i < cls.T.Count; i++)
            {
                var t = cls.T;
                int id = t.IdAt(i);
                if (id <= 0) continue;
                s.Classes[id] = new ClassInfo
                {
                    Id = id, Name = (t.String(i, ClassTables.ClsName) ?? "").Trim(), File = (t.String(i, ClassTables.ClsFile) ?? "").Trim().ToUpperInvariant(), Description = (t.String(i, ClassTables.ClsDescription) ?? "").Trim(),
                    IconId = SpellTables.Int(t, i, ClassTables.ClsIcon), Flags = SpellTables.Int(t, i, ClassTables.ClsFlags), StartingLevel = SpellTables.Int(t, i, ClassTables.ClsStartLevel),
                    DisplayPower = SpellTables.Int(t, i, ClassTables.ClsDisplayPower), Roles = SpellTables.Int(t, i, ClassTables.ClsRoles),
                    ColorR = SpellTables.Int(t, i, ClassTables.ClsColorR) & 255, ColorG = SpellTables.Int(t, i, ClassTables.ClsColorG) & 255, ColorB = SpellTables.Int(t, i, ClassTables.ClsColorB) & 255,
                };
            }
            for (int i = 0; i < cb.T.Count; i++)
            {
                int race = SpellTables.Int(cb.T, i, 0), c = SpellTables.Int(cb.T, i, 1);
                if (race > 0 && c > 0) s.Pairs.Add(ClassSnapshot.PairKey(race, c));
            }
            var playable = new HashSet<int>(s.Pairs.Select(p => (int)(p >> 8)));
            for (int i = 0; i < rac.T.Count; i++)
            {
                var t = rac.T;
                int id = t.IdAt(i);
                if (id <= 0) continue;
                s.Races[id] = new RaceInfo
                {
                    Id = id, Name = (t.String(i, ClassTables.RaceName) ?? "").Trim(), LoreName = (t.String(i, ClassTables.RaceLoreName) ?? "").Trim(), ClientFile = (t.String(i, ClassTables.RaceClientFile) ?? "").Trim(),
                    Description = (t.String(i, ClassTables.RaceLoreDescription) ?? "").Trim(), Flags = SpellTables.Int(t, i, ClassTables.RaceFlags), FactionTemplate = SpellTables.Int(t, i, ClassTables.RaceFaction),
                    StartingLevel = SpellTables.Int(t, i, ClassTables.RaceStartLevel), Order = SpellTables.Int(t, i, ClassTables.RaceOrder), Bit = SpellTables.Int(t, i, ClassTables.RaceBit),
                    Side = SpellTables.Int(t, i, ClassTables.RaceAlliance), BaseLanguage = SpellTables.Int(t, i, ClassTables.RaceLanguage), DefaultClass = SpellTables.Int(t, i, ClassTables.RaceDefaultClass),
                    Playable = playable.Contains(id),
                };
            }

            // ---- optional tables: each one only drops its own feature
            var pw = Open(raw, ClassTables.ClassPower, set, false);
            if (pw.Ok)
            {
                var by = new Dictionary<int, List<int>>();
                for (int i = 0; i < pw.T.Count; i++)
                {
                    int c = pw.T.Foreign(i);
                    if (!s.Classes.ContainsKey(c)) continue;
                    if (!by.TryGetValue(c, out var l)) by[c] = l = new List<int>();
                    int type = SpellTables.Int(pw.T, i, 0);
                    if (!l.Contains(type)) l.Add(type);
                }
                foreach (var kv in by) s.Classes[kv.Key].PowerTypes = kv.Value.OrderBy(x => x).ToArray();
            }
            var sr = Open(raw, ClassTables.SkillRaceClass, set, false);
            if (sr.Ok)
            {
                s.HasSkillRows = true;
                for (int i = 0; i < sr.T.Count; i++)
                {
                    var t = sr.T;
                    uint lo = (uint)t.FieldArr(i, ClassTables.SrRaces, 0, 2), hi = (uint)t.FieldArr(i, ClassTables.SrRaces, 1, 2);
                    s.SkillRows.Add(new SkillClassRow
                    {
                        Skill = SpellTables.Int(t, i, ClassTables.SrSkill), ClassMask = SpellTables.Int(t, i, ClassTables.SrClassMask), Flags = SpellTables.Int(t, i, ClassTables.SrFlags),
                        Availability = SpellTables.Int(t, i, ClassTables.SrAvailability), MinLevel = SpellTables.Int(t, i, ClassTables.SrMinLevel), Races = ((ulong)hi << 32) | lo,
                    });
                }
            }
            var lv = Open(raw, ClassTables.SpellLevels, set, false);
            if (lv.Ok)
            {
                s.HasLevels = true;
                for (int i = 0; i < lv.T.Count; i++)
                {
                    if (SpellTables.Int(lv.T, i, ClassTables.LvDifficulty) != 0) continue;
                    int spell = lv.T.Foreign(i);
                    if (spell <= 0) continue;
                    s.Levels[spell] = new SpellLevelInfo { Base = SpellTables.Int(lv.T, i, ClassTables.LvBase), Spell = SpellTables.Int(lv.T, i, ClassTables.LvSpell) };
                }
            }
            var pc = Open(raw, ClassTables.SpellPower, set, false);
            if (pc.Ok)
            {
                s.HasCosts = true;
                var bestOrder = new Dictionary<int, int>();
                for (int i = 0; i < pc.T.Count; i++)
                {
                    int spell = pc.T.Foreign(i);
                    if (spell <= 0) continue;
                    int order = SpellTables.Int(pc.T, i, ClassTables.PwOrder);
                    if (bestOrder.TryGetValue(spell, out var o) && o <= order) continue;          // the first cost of the spell (order 0) is the one the tooltip shows
                    bestOrder[spell] = order;
                    s.Costs[spell] = new SpellCostInfo { Type = SpellTables.Int(pc.T, i, ClassTables.PwType), Amount = SpellTables.Int(pc.T, i, ClassTables.PwCost), Pct = SpellTables.Flt(pc.T, i, ClassTables.PwPct) };
                }
            }
            var cd = Open(raw, ClassTables.SpellCooldowns, set, false);
            if (cd.Ok)
            {
                s.HasCooldowns = true;
                for (int i = 0; i < cd.T.Count; i++)
                {
                    if (SpellTables.Int(cd.T, i, ClassTables.CdDifficulty) != 0) continue;
                    int spell = cd.T.Foreign(i);
                    if (spell <= 0) continue;
                    s.Cooldowns[spell] = new SpellCooldownInfo { Category = SpellTables.Int(cd.T, i, ClassTables.CdCategory), Recovery = SpellTables.Int(cd.T, i, ClassTables.CdRecovery), Global = SpellTables.Int(cd.T, i, ClassTables.CdStart) };
                }
            }
            return set;
        }
    }

    // ============================================================================================================================ the saved index
    public static class ClassIndexFile
    {
        public const int FormatVersion = 1;
        const int Magic = 0x4C534345;       // "ECSL"
        public sealed class Saved { public int Format; public string Build; public ClassSet Set; }

        static void Count(int n, int max) { if (n < 0 || n > max) throw new InvalidDataException("count"); }

        public static bool TryWrite(string path, string build, ClassSet set)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var tmp = path + ".tmp";
                var mem = new MemoryStream(1 << 20);
                using (var w = new BinaryWriter(mem, Encoding.UTF8, true))
                {
                    var s = set.Data;
                    w.Write(Magic); w.Write(FormatVersion); w.Write(build ?? "");
                    w.Write(set.Problems.Count); foreach (var kv in set.Problems) { w.Write(kv.Key); w.Write(kv.Value ?? ""); }
                    w.Write(set.Notes.Count); foreach (var n in set.Notes) w.Write(n);
                    w.Write(set.LayoutsSeen.Count); foreach (var kv in set.LayoutsSeen) { w.Write(kv.Key); w.Write(kv.Value); }
                    w.Write(s.HasSkillRows); w.Write(s.HasLevels); w.Write(s.HasCosts); w.Write(s.HasCooldowns);
                    w.Write(s.Classes.Count);
                    foreach (var c in s.Classes.Values)
                    {
                        w.Write(c.Id); w.Write(c.Name); w.Write(c.File); w.Write(c.Description); w.Write(c.IconId); w.Write(c.Flags); w.Write(c.StartingLevel); w.Write(c.DisplayPower); w.Write(c.Roles);
                        w.Write(c.ColorR); w.Write(c.ColorG); w.Write(c.ColorB); w.Write((byte)c.PowerTypes.Length); foreach (var p in c.PowerTypes) w.Write(p);
                    }
                    w.Write(s.Races.Count);
                    foreach (var r in s.Races.Values)
                    {
                        w.Write(r.Id); w.Write(r.Name); w.Write(r.LoreName); w.Write(r.ClientFile); w.Write(r.Description); w.Write(r.Flags); w.Write(r.FactionTemplate); w.Write(r.StartingLevel); w.Write(r.Order);
                        w.Write(r.Bit); w.Write(r.Side); w.Write(r.BaseLanguage); w.Write(r.DefaultClass); w.Write(r.Playable);
                    }
                    w.Write(s.Pairs.Count); foreach (var p in s.Pairs) w.Write(p);
                    w.Write(s.SkillRows.Count);
                    foreach (var r in s.SkillRows) { w.Write(r.Skill); w.Write(r.ClassMask); w.Write(r.Flags); w.Write(r.Availability); w.Write(r.MinLevel); w.Write(r.Races); }
                    w.Write(s.Levels.Count); foreach (var kv in s.Levels) { w.Write(kv.Key); w.Write(kv.Value.Base); w.Write(kv.Value.Spell); }
                    w.Write(s.Costs.Count); foreach (var kv in s.Costs) { w.Write(kv.Key); w.Write(kv.Value.Type); w.Write(kv.Value.Amount); w.Write(kv.Value.Pct); }
                    w.Write(s.Cooldowns.Count); foreach (var kv in s.Cooldowns) { w.Write(kv.Key); w.Write(kv.Value.Recovery); w.Write(kv.Value.Category); w.Write(kv.Value.Global); }
                }
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var gz = new GZipStream(fs, CompressionMode.Compress))
                    gz.Write(mem.GetBuffer(), 0, (int)mem.Length);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                return true;
            }
            catch (Exception e) { Util.Log("class data: cannot save the index: " + e.Message); return false; }
        }

        // null for a missing, damaged or foreign file (the caller then reads the game files)
        public static Saved TryRead(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var mem = new MemoryStream(1 << 20);
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var gz = new GZipStream(fs, CompressionMode.Decompress))
                    gz.CopyTo(mem);
                mem.Position = 0;
                using (var r = new BinaryReader(mem, Encoding.UTF8))
                {
                    if (r.ReadInt32() != Magic) return null;
                    var sv = new Saved { Format = r.ReadInt32(), Build = r.ReadString(), Set = new ClassSet() };
                    if (sv.Format != FormatVersion) return null;
                    var set = sv.Set; var s = set.Data;
                    int n = r.ReadInt32(); Count(n, 1000); for (int i = 0; i < n; i++) set.Problems[r.ReadString()] = r.ReadString();
                    n = r.ReadInt32(); Count(n, 100000); for (int i = 0; i < n; i++) set.Notes.Add(r.ReadString());
                    n = r.ReadInt32(); Count(n, 1000); for (int i = 0; i < n; i++) set.LayoutsSeen[r.ReadString()] = r.ReadUInt32();
                    s.HasSkillRows = r.ReadBoolean(); s.HasLevels = r.ReadBoolean(); s.HasCosts = r.ReadBoolean(); s.HasCooldowns = r.ReadBoolean();
                    n = r.ReadInt32(); Count(n, 1000);
                    for (int i = 0; i < n; i++)
                    {
                        var c = new ClassInfo { Id = r.ReadInt32(), Name = r.ReadString(), File = r.ReadString(), Description = r.ReadString(), IconId = r.ReadInt32(), Flags = r.ReadInt32(), StartingLevel = r.ReadInt32(), DisplayPower = r.ReadInt32(), Roles = r.ReadInt32() };
                        c.ColorR = r.ReadInt32(); c.ColorG = r.ReadInt32(); c.ColorB = r.ReadInt32();
                        int pc = r.ReadByte(); c.PowerTypes = new int[pc]; for (int k = 0; k < pc; k++) c.PowerTypes[k] = r.ReadInt32();
                        s.Classes[c.Id] = c;
                    }
                    n = r.ReadInt32(); Count(n, 100000);
                    for (int i = 0; i < n; i++)
                    {
                        var x = new RaceInfo { Id = r.ReadInt32(), Name = r.ReadString(), LoreName = r.ReadString(), ClientFile = r.ReadString(), Description = r.ReadString(), Flags = r.ReadInt32(), FactionTemplate = r.ReadInt32(), StartingLevel = r.ReadInt32(), Order = r.ReadInt32() };
                        x.Bit = r.ReadInt32(); x.Side = r.ReadInt32(); x.BaseLanguage = r.ReadInt32(); x.DefaultClass = r.ReadInt32(); x.Playable = r.ReadBoolean();
                        s.Races[x.Id] = x;
                    }
                    n = r.ReadInt32(); Count(n, 1000000); for (int i = 0; i < n; i++) s.Pairs.Add(r.ReadInt64());
                    n = r.ReadInt32(); Count(n, 1000000);
                    for (int i = 0; i < n; i++) s.SkillRows.Add(new SkillClassRow { Skill = r.ReadInt32(), ClassMask = r.ReadInt32(), Flags = r.ReadInt32(), Availability = r.ReadInt32(), MinLevel = r.ReadInt32(), Races = r.ReadUInt64() });
                    n = r.ReadInt32(); Count(n, 5000000); for (int i = 0; i < n; i++) s.Levels[r.ReadInt32()] = new SpellLevelInfo { Base = r.ReadInt32(), Spell = r.ReadInt32() };
                    n = r.ReadInt32(); Count(n, 5000000); for (int i = 0; i < n; i++) s.Costs[r.ReadInt32()] = new SpellCostInfo { Type = r.ReadInt32(), Amount = r.ReadInt32(), Pct = r.ReadSingle() };
                    n = r.ReadInt32(); Count(n, 5000000); for (int i = 0; i < n; i++) s.Cooldowns[r.ReadInt32()] = new SpellCooldownInfo { Recovery = r.ReadInt32(), Category = r.ReadInt32(), Global = r.ReadInt32() };
                    return sv;
                }
            }
            catch (Exception) { return null; }
        }
    }
}
