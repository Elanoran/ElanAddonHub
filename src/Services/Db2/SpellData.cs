using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ElansAddonHub.Services.Db2
{
    // ============================================================================================================================
    // Atlas layers A2 / A4 (and the item details that need spells): spells, skills and professions, recipes and reagents, factions and their templates,
    // armor / weapon damage / item effects / item sets. Read from the player's OWN WoW install, read-only, in the background, exactly like ItemData:
    //   * the tables come out of the local CASC storage (SpellTables lists them and the layouts this version knows)
    //   * the parsed result is kept in the Outpost's own data folder (cache\spelldata.bin), keyed by the game build, so the next start does not parse again
    //   * a table with a layout this version does not know is unavailable on its own; the rest still works; nothing is guessed
    // It starts and stops together with ItemData (same off switch, same WoW folder), see ItemData.Start.
    // ============================================================================================================================
    public static class SpellData
    {
        public enum State { NotStarted, Disabled, Loading, Ready, Unavailable }

        public const int FormatVersion = 3;          // bump when the parsing, the table layouts or the cache file change meaning (2: + WorldLookups, 3: + SpellLookups for the hotfix overlay)

        public static bool AutoStart = true;         // off in the self-test (it must not read the real game)
        public static string CacheFile = Path.Combine(Util.DataDir, "cache", "spelldata.bin");
        public static event Action Changed;          // on a worker thread, whenever the data or the state changed

        static readonly object gate = new object();
        static volatile AtlasSet data;               // what the Atlas uses: the shipped tables with the server's hotfix rows laid over them
        static volatile AtlasSet shipped;            // the shipped tables alone (what the index file keeps); DBCache.bin changes only re-lay the hotfixes
        static string hotfixStamp;                   // DBCache.bin's size and time when the hotfixes were last read
        static volatile State state = State.NotStarted;
        static string reason, builtFor;
        static bool running, again;
        public static long LastLoadMs { get; private set; }
        public static string LastLoadFrom { get; private set; }     // "cache file" | "game files"
        public static string Build { get; private set; }            // the game build the data was read from (the stamp the layers record)
        public static int StartCountForTest;

        public static State Status => state;
        public static string Reason => reason;
        public static AtlasSet Data => data;
        public static bool Ready => state == State.Ready && data != null;
        public static SpellSnapshot Spells => data?.Spells;
        public static FactionSnapshot Factions => data?.Factions;
        public static ItemDetails Details => data?.Details;
        public static WorldLookups World => data?.World;
        public static HotfixInfo Hotfixes => data?.Hotfixes ?? HotfixInfo.Empty;

        public static int SpellCount => Spells?.SpellCount ?? 0;
        public static int RecipeCount => Spells?.RecipeCount ?? 0;

        // ---------------------------------------------------------------------------------------------------------------- the lookups
        // a spell with its description filled in (null when the spell data is not read or does not know the id)
        public static SpellView Spell(int id)
        {
            var sn = Spells;
            var s = sn?.Get(id);
            if (s == null) return null;
            Func<int, SpellInfo> look = sn.Get;
            return new SpellView
            {
                Id = s.Id, Name = s.Name, Rank = s.Rank, IconId = s.IconId, CastMs = s.CastMs, DurationMs = s.DurationMs, RangeMax = s.RangeMax,
                Description = SpellText.Expand(s.Desc, s, look), AuraDescription = SpellText.Expand(s.Aura, s, look),
                CostText = ClassData.CostText(s.Id), CooldownMs = ClassData.CooldownMs(s.Id), Hotfix = Hotfixes.Spell(s.Id),
            };
        }

        // the same, with some effect amounts replaced (effect index 0-based -> base amount): the rank of a talent changes them (Atlas layer G)
        public static SpellView Spell(int id, IDictionary<int, float> effectPoints)
        {
            if (effectPoints == null || effectPoints.Count == 0) return Spell(id);
            var sn = Spells;
            var s = sn?.Get(id);
            if (s == null) return null;
            var copy = new SpellInfo
            {
                Id = s.Id, Name = s.Name, Rank = s.Rank, Desc = s.Desc, Aura = s.Aura, IconId = s.IconId, CastMs = s.CastMs, DurationMs = s.DurationMs, RangeMax = s.RangeMax,
                Effects = s.Effects.Select(e => new EffectInfo
                {
                    Index = e.Index, Effect = e.Effect, Aura = e.Aura, PeriodMs = e.PeriodMs, Chain = e.Chain, ItemType = e.ItemType, Misc = e.Misc, Trigger = e.Trigger,
                    Points = effectPoints.TryGetValue(e.Index, out var p) ? p : e.Points, Coef = e.Coef, Radius = e.Radius,
                }).ToArray(),
            };
            Func<int, SpellInfo> look = x => x == id ? copy : sn.Get(x);
            return new SpellView
            {
                Id = copy.Id, Name = copy.Name, Rank = copy.Rank, IconId = copy.IconId, CastMs = copy.CastMs, DurationMs = copy.DurationMs, RangeMax = copy.RangeMax,
                Description = SpellText.Expand(copy.Desc, copy, look), AuraDescription = SpellText.Expand(copy.Aura, copy, look),
                CostText = ClassData.CostText(copy.Id), CooldownMs = ClassData.CooldownMs(copy.Id), Hotfix = Hotfixes.Spell(copy.Id),
            };
        }

        public static SkillInfo Skill(int id) => Spells != null && Spells.Skills.TryGetValue(id, out var s) ? s : null;

        // a profession / secondary skill by its name ("Alchemy"); other skills (weapons, languages) only when no profession has the name
        public static SkillInfo SkillByName(string name)
        {
            var sn = Spells;
            if (sn == null || string.IsNullOrWhiteSpace(name)) return null;
            var hits = sn.Skills.Values.Where(s => string.Equals(s.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            return hits.Where(s => s.IsProfession).OrderByDescending(s => sn.AbilitiesOf(s.Id).Count).ThenBy(s => s.Id).FirstOrDefault() ?? hits.OrderBy(s => s.Id).FirstOrDefault();
        }

        // what a skill teaches, lowest rank first
        public static List<SkillAbility> SpellsOfSkill(int skillId) => Spells == null ? new List<SkillAbility>() : Spells.AbilitiesOf(skillId);

        // the recipes that make an item / that use an item up
        public static List<Recipe> RecipesProducing(int itemId) => Spells == null ? new List<Recipe>() : Spells.Producing(itemId);
        public static List<Recipe> ReagentFor(int itemId) => Spells == null ? new List<Recipe>() : Spells.UsingReagent(itemId);

        // recipes of a skill (profession), lowest rank first
        public static List<Recipe> RecipesOfSkill(int skillId)
        {
            var sn = Spells;
            var res = new List<Recipe>();
            if (sn == null) return res;
            foreach (var a in sn.AbilitiesOf(skillId)) { var r = sn.RecipeOf(a.Spell); if (r != null && r.SkillId == skillId) res.Add(r); }
            return res;
        }

        // ---------------------------------------------------------------------------------------------------------------- the status text
        // what the Settings "Game data" line adds after the item counts: ", 31,000 spells, 1,200 recipes" (or why there are none)
        public static string StatusExtra
        {
            get
            {
                var d = data;
                switch (state)
                {
                    case State.Ready:
                        if (d == null) return "";
                        var parts = new List<string>();
                        if (d.Spells != null) parts.Add(d.Spells.SpellCount.ToString("N0", CultureInfo.InvariantCulture) + " spells");
                        if (d.Spells != null && d.Spells.RecipeCount > 0) parts.Add(d.Spells.RecipeCount.ToString("N0", CultureInfo.InvariantCulture) + " recipes");
                        if (d.Factions != null) parts.Add(d.Factions.Factions.Count.ToString("N0", CultureInfo.InvariantCulture) + " factions");
                        if (d.Hotfixes != null && d.Hotfixes.Rows > 0) parts.Add(d.Hotfixes.Rows.ToString("N0", CultureInfo.InvariantCulture) + " server hotfix rows");
                        var s = parts.Count == 0 ? "" : ", " + string.Join(", ", parts);
                        if (d.Spells == null && d.Problems.TryGetValue("spells", out var why)) s += ", spells unavailable (" + why + ")";
                        return s;
                    case State.Unavailable: return ", spell data unavailable" + (string.IsNullOrEmpty(reason) ? "" : " (" + reason + ")");
                    default: return "";
                }
            }
        }

        // ---------------------------------------------------------------------------------------------------------------- the service
        static bool SafeEnabled() { try { return ItemData.Enabled(); } catch { return true; } }

        public static void Start()
        {
            Interlocked.Increment(ref StartCountForTest);
            if (!AutoStart) return;
            if (!SafeEnabled()) { Disable(); return; }
            lock (gate)
            {
                again = true;
                if (running) return;
                running = true;
            }
            if (data == null) SetState(State.Loading, null);
            Task.Factory.StartNew(Worker, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        public static void Disable()
        {
            data = null; shipped = null; hotfixStamp = null; ItemDetails.Current = null; Build = null; builtFor = null;
            SetState(State.Disabled, null);
        }

        static void Worker()
        {
            try { Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; } catch { }
            while (true)
            {
                lock (gate) { if (!again) { running = false; return; } again = false; }
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    if (!SafeEnabled()) { Disable(); continue; }
                    LoadOnce();
                }
                catch (Exception e)
                {
                    Util.Log("spell data: " + e);
                    Publish(null, State.Unavailable, "could not be read: " + e.Message, null);
                }
                LastLoadMs = sw.ElapsedMilliseconds;
            }
        }

        static void Publish(AtlasSet s, State st, string why, string build)
        {
            lock (gate) { data = st == State.Ready ? s : null; reason = why; state = st; Build = st == State.Ready ? build : null; builtFor = Build; }
            ItemDetails.Current = st == State.Ready && s != null ? s.Details : null;
            try { Changed?.Invoke(); } catch { }
        }

        static void SetState(State st, string why)
        {
            lock (gate) { state = st; reason = why; }
            try { Changed?.Invoke(); } catch { }
        }

        // test hook: use this set as the loaded data
        public static void SetForTest(AtlasSet s, State st = State.Ready, string why = null)
        {
            lock (gate) { data = s; state = st; reason = why; Build = st == State.Ready ? "test" : null; builtFor = Build; }
            ItemDetails.Current = st == State.Ready && s != null ? s.Details : null;
        }

        public static void ResetForTest()
        {
            lock (gate) { data = null; shipped = null; hotfixStamp = null; state = State.NotStarted; reason = null; running = false; again = false; Build = null; builtFor = null; }
            ItemDetails.Current = null;
        }

        static void LoadOnce()
        {
            var root = ItemData.WowRoot();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) { Publish(null, State.Unavailable, "the WoW folder is not set", null); return; }
            var flavor = ItemData.Flavor() ?? "_classic_beta_";
            var build = ItemData.GameBuild(root, flavor);
            if (build == null) { Publish(null, State.Unavailable, "no .build.info in the WoW folder", null); return; }
            // a refresh that came because DBCache.bin changed: the tables of this build are already here, only the server's hotfix rows are read again
            var adb = ItemData.AdbFile(root, flavor);
            var stamp = HotfixStamp(adb);
            var have = shipped;
            if (state == State.Ready && data != null && have != null && builtFor == build)
            {
                if (stamp == hotfixStamp) return;
                hotfixStamp = stamp;
                Publish(Overlay(have, adb, build), State.Ready, null, build);
                return;
            }
            var set = Obtain(SpellIndexFile.TryRead(CacheFile), build, CacheFile, () =>
            {
                SpellRaw raw;
                try { raw = ReadCasc(root, flavor); }
                catch (Exception e) { Util.Log("spell data: game files: " + e.Message); var f = new AtlasSet(); f.Problems["all"] = "the game files could not be read: " + e.Message; return f; }
                return SpellParse.All(raw);
            }, out var from);
            if (set.Problems.ContainsKey("all")) { Util.Log("spell data: unavailable: " + set.Problems["all"]); Publish(null, State.Unavailable, set.Problems["all"], null); return; }
            foreach (var kv in set.Problems) Util.Log("spell data: " + kv.Key + ": " + kv.Value);
            foreach (var n in set.Notes) Util.Log("spell data: " + n);
            if (set.Spells == null && set.Factions == null && (set.Details == null)) { Publish(null, State.Unavailable, set.Problems.Values.FirstOrDefault() ?? "no table could be read", null); return; }
            LastLoadFrom = from;
            shipped = set; hotfixStamp = stamp;
            Publish(Overlay(set, adb, build), State.Ready, null, build);
        }

        static string HotfixStamp(string adbFile)
        {
            try { if (adbFile == null || !File.Exists(adbFile)) return ""; var fi = new FileInfo(adbFile); return fi.Length + "|" + fi.LastWriteTimeUtc.Ticks; }
            catch { return ""; }
        }

        // the shipped tables with the server's hotfix rows of DBCache.bin laid over them (ServerHotfixes); without a readable cache they stay as shipped
        public static AtlasSet Overlay(AtlasSet set, string adbFile, string build)
        {
            if (set == null) return null;
            if (string.IsNullOrEmpty(adbFile) || !File.Exists(adbFile)) return ServerHotfixes.Apply(set, null);
            try
            {
                var d = ServerHotfixes.Decode(DbCache.ReadAllShared(adbFile), set.LayoutsSeen, ServerHotfixes.BuildNumber(build));
                var o = ServerHotfixes.Apply(set, d);
                foreach (var n in d.Notes) Util.Log("spell data: " + n);
                if (d.Rows > 0) Util.Log("spell data: " + d.Rows + " server hotfix rows laid over the game files (" + o.Hotfixes.Spells.Count + " spells, " + o.Hotfixes.NewQuests.Count + " new quests, " + o.Hotfixes.Locks.Count + " locks, " + o.Hotfixes.Templates.Count + " faction templates)");
                return o;
            }
            catch (Exception e)
            {
                Util.Log("spell data: DBCache.bin: " + e.Message);
                var o = ServerHotfixes.Apply(set, null);
                o.Hotfixes.Notes.Add("DBCache.bin: " + e.Message);
                return o;
            }
        }

        // The decision: the saved index when it is of this game build (the shipped tables cannot have changed), otherwise the game files, which are saved.
        public static AtlasSet Obtain(SpellIndexFile.Saved saved, string build, string cacheFile, Func<AtlasSet> readGame, out string from)
        {
            if (saved != null && saved.Build == build && saved.Format == FormatVersion) { from = "cache file"; return saved.Set; }
            var fresh = readGame();
            if (!fresh.Problems.ContainsKey("all") && (fresh.Spells != null || fresh.Factions != null || fresh.Details != null)) SpellIndexFile.TryWrite(cacheFile, build, fresh);
            from = "game files";
            return fresh;
        }

        public static SpellRaw ReadCasc(string root, string flavor)
        {
            var raw = new SpellRaw();
            using (var casc = CascStorage.Open(root, flavor))
            {
                casc.ZeroFillEncrypted = true;     // key-locked parts read as zeros; the table reader skips those sections
                var all = SpellTables.All.Concat(WorldTables.All).ToArray();
                var keys = casc.ResolveFileIds(new HashSet<int>(all.Select(d => d.FileId)));
                foreach (var def in all)
                {
                    try { if (keys.TryGetValue(def.FileId, out var ck)) raw.Tables[def.Name] = casc.ReadByContentKey(ck); }
                    catch (Exception e) { Util.Log("spell data: " + def.Name + ".db2: " + e.Message); }
                }
            }
            return raw;
        }
    }

    // ============================================================================================================================
    // the compact copy of the parsed data in the Outpost's data folder (gzip of a small binary format), keyed by the game build
    // ============================================================================================================================
    public static class SpellIndexFile
    {
        public sealed class Saved { public int Format; public string Build; public AtlasSet Set; }
        const int Magic = 0x4C505345;       // "ESPL"

        public static bool TryWrite(string path, string build, AtlasSet set)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var tmp = path + ".tmp";
                var mem = new MemoryStream(1 << 22);
                using (var w = new BinaryWriter(mem, Encoding.UTF8, true))
                {
                    w.Write(Magic); w.Write(SpellData.FormatVersion); w.Write(build ?? "");
                    w.Write(set.Problems.Count);
                    foreach (var kv in set.Problems) { w.Write(kv.Key); w.Write(kv.Value ?? ""); }
                    w.Write(set.Notes.Count);
                    foreach (var n in set.Notes) w.Write(n);
                    w.Write(set.LayoutsSeen.Count);
                    foreach (var kv in set.LayoutsSeen) { w.Write(kv.Key); w.Write(kv.Value); }
                    WriteSpells(w, set.Spells);
                    WriteFactions(w, set.Factions);
                    WriteDetails(w, set.Details);
                    WorldLookups.Write(w, set.World);
                }
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var gz = new GZipStream(fs, CompressionMode.Compress))
                    gz.Write(mem.GetBuffer(), 0, (int)mem.Length);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                return true;
            }
            catch (Exception e) { Util.Log("spell data: cannot save the index: " + e.Message); return false; }
        }

        // null for a missing, damaged or foreign file (the caller then reads the game files)
        public static Saved TryRead(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var mem = new MemoryStream(1 << 22);
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var gz = new GZipStream(fs, CompressionMode.Decompress))
                    gz.CopyTo(mem);
                mem.Position = 0;
                using (var r = new BinaryReader(mem, Encoding.UTF8))
                {
                    if (r.ReadInt32() != Magic) return null;
                    var s = new Saved { Format = r.ReadInt32(), Build = r.ReadString(), Set = new AtlasSet() };
                    if (s.Format != SpellData.FormatVersion) return null;
                    int n = r.ReadInt32(); Count(n, 1000);
                    for (int i = 0; i < n; i++) s.Set.Problems[r.ReadString()] = r.ReadString();
                    n = r.ReadInt32(); Count(n, 100000);
                    for (int i = 0; i < n; i++) s.Set.Notes.Add(r.ReadString());
                    n = r.ReadInt32(); Count(n, 1000);
                    for (int i = 0; i < n; i++) s.Set.LayoutsSeen[r.ReadString()] = r.ReadUInt32();
                    s.Set.Spells = ReadSpells(r);
                    s.Set.Factions = ReadFactions(r);
                    s.Set.Details = ReadDetails(r);
                    s.Set.World = WorldLookups.Read(r);
                    return s;
                }
            }
            catch (Exception) { return null; }
        }

        static void Count(int n, int max) { if (n < 0 || n > max) throw new InvalidDataException("count"); }

        // ---- spells, skills, abilities, reagents
        static void WriteSpells(BinaryWriter w, SpellSnapshot s)
        {
            w.Write(s != null);
            if (s == null) return;
            w.Write(s.Spells.Count);
            foreach (var p in s.Spells.Values)
            {
                w.Write(p.Id); w.Write(p.Name ?? ""); w.Write(p.Rank ?? ""); w.Write(p.Desc ?? ""); w.Write(p.Aura ?? "");
                w.Write(p.IconId); w.Write(p.CastMs); w.Write(p.DurationMs); w.Write(p.RangeMax);
                w.Write((byte)p.Effects.Length);
                foreach (var e in p.Effects)
                {
                    w.Write((byte)e.Index); w.Write(e.Effect); w.Write(e.Aura); w.Write(e.PeriodMs); w.Write(e.Chain); w.Write(e.ItemType); w.Write(e.Misc); w.Write(e.Trigger);
                    w.Write(e.Points); w.Write(e.Coef); w.Write(e.Radius);
                }
            }
            w.Write(s.Skills.Count);
            foreach (var k in s.Skills.Values) { w.Write(k.Id); w.Write(k.Name ?? ""); w.Write(k.Description ?? ""); w.Write(k.Category); w.Write(k.IconId); w.Write(k.ParentId); w.Write(k.Flags); w.Write(k.CanLink); }
            w.Write(s.Abilities.Count);
            foreach (var a in s.Abilities) { w.Write(a.Id); w.Write(a.Skill); w.Write(a.Spell); w.Write(a.MinRank); w.Write(a.Green); w.Write(a.Gray); w.Write(a.Acquire); w.Write(a.Flags); w.Write(a.ClassMask); w.Write(a.Supersedes); }
            w.Write(s.Reagents.Count);
            foreach (var kv in s.Reagents) { w.Write(kv.Key); w.Write((byte)kv.Value.Length); foreach (var v in kv.Value) w.Write(v); }
            var lk = s.Lookups;
            void Ints(Dictionary<int, int> d) { w.Write(d.Count); foreach (var kv in d) { w.Write(kv.Key); w.Write(kv.Value); } }
            void Floats(Dictionary<int, float> d) { w.Write(d.Count); foreach (var kv in d) { w.Write(kv.Key); w.Write(kv.Value); } }
            Ints(lk.CastMs); Ints(lk.DurationMs); Floats(lk.RangeMax); Floats(lk.Radius); Ints(lk.MiscRows);
            w.Write(lk.EffectRows.Count); foreach (var kv in lk.EffectRows) { w.Write(kv.Key); w.Write(kv.Value); }
        }

        static SpellSnapshot ReadSpells(BinaryReader r)
        {
            if (!r.ReadBoolean()) return null;
            int n = r.ReadInt32(); Count(n, 5000000);
            var spells = new List<SpellInfo>(n);
            for (int i = 0; i < n; i++)
            {
                var p = new SpellInfo { Id = r.ReadInt32(), Name = r.ReadString(), Rank = r.ReadString(), Desc = r.ReadString(), Aura = r.ReadString() };
                p.IconId = r.ReadInt32(); p.CastMs = r.ReadInt32(); p.DurationMs = r.ReadInt32(); p.RangeMax = r.ReadSingle();
                int ec = r.ReadByte();
                if (ec > 0)
                {
                    p.Effects = new EffectInfo[ec];
                    for (int k = 0; k < ec; k++)
                        p.Effects[k] = new EffectInfo
                        {
                            Index = r.ReadByte(), Effect = r.ReadInt32(), Aura = r.ReadInt32(), PeriodMs = r.ReadInt32(), Chain = r.ReadInt32(), ItemType = r.ReadInt32(), Misc = r.ReadInt32(), Trigger = r.ReadInt32(),
                            Points = r.ReadSingle(), Coef = r.ReadSingle(), Radius = r.ReadSingle(),
                        };
                }
                spells.Add(p);
            }
            n = r.ReadInt32(); Count(n, 1000000);
            var skills = new List<SkillInfo>(n);
            for (int i = 0; i < n; i++) skills.Add(new SkillInfo { Id = r.ReadInt32(), Name = r.ReadString(), Description = r.ReadString(), Category = r.ReadInt32(), IconId = r.ReadInt32(), ParentId = r.ReadInt32(), Flags = r.ReadInt32(), CanLink = r.ReadInt32() });
            n = r.ReadInt32(); Count(n, 5000000);
            var abil = new List<SkillAbility>(n);
            for (int i = 0; i < n; i++) abil.Add(new SkillAbility { Id = r.ReadInt32(), Skill = r.ReadInt32(), Spell = r.ReadInt32(), MinRank = r.ReadInt32(), Green = r.ReadInt32(), Gray = r.ReadInt32(), Acquire = r.ReadInt32(), Flags = r.ReadInt32(), ClassMask = r.ReadInt32(), Supersedes = r.ReadInt32() });
            n = r.ReadInt32(); Count(n, 5000000);
            var rg = new Dictionary<int, int[]>(n);
            for (int i = 0; i < n; i++) { int id = r.ReadInt32(); int c = r.ReadByte(); var a = new int[c]; for (int k = 0; k < c; k++) a[k] = r.ReadInt32(); rg[id] = a; }
            var lk = new SpellLookups();
            void Ints(Dictionary<int, int> d) { int m = r.ReadInt32(); Count(m, 5000000); for (int i = 0; i < m; i++) d[r.ReadInt32()] = r.ReadInt32(); }
            void Floats(Dictionary<int, float> d) { int m = r.ReadInt32(); Count(m, 5000000); for (int i = 0; i < m; i++) d[r.ReadInt32()] = r.ReadSingle(); }
            Ints(lk.CastMs); Ints(lk.DurationMs); Floats(lk.RangeMax); Floats(lk.Radius); Ints(lk.MiscRows);
            n = r.ReadInt32(); Count(n, 5000000);
            for (int i = 0; i < n; i++) lk.EffectRows[r.ReadInt32()] = r.ReadInt64();
            return new SpellSnapshot(spells, skills, abil, rg, lk);
        }

        // ---- factions
        static void WriteFactions(BinaryWriter w, FactionSnapshot f)
        {
            w.Write(f != null);
            if (f == null) return;
            w.Write(f.Factions.Count);
            foreach (var x in f.Factions.Values) { w.Write(x.Id); w.Write(x.Name ?? ""); w.Write(x.Description ?? ""); w.Write(x.ParentId); w.Write(x.ReputationIndex); w.Write(x.Flags); }
            w.Write(f.Templates.Count);
            foreach (var t in f.Templates.Values)
            {
                w.Write(t.Id); w.Write(t.FactionId); w.Write(t.Flags); w.Write(t.Group); w.Write(t.FriendGroup); w.Write(t.EnemyGroup);
                w.Write((byte)t.Enemies.Length); foreach (var v in t.Enemies) w.Write(v);
                w.Write((byte)t.Friends.Length); foreach (var v in t.Friends) w.Write(v);
            }
        }

        static FactionSnapshot ReadFactions(BinaryReader r)
        {
            if (!r.ReadBoolean()) return null;
            int n = r.ReadInt32(); Count(n, 1000000);
            var fs = new List<FactionInfo>(n);
            for (int i = 0; i < n; i++) fs.Add(new FactionInfo { Id = r.ReadInt32(), Name = r.ReadString(), Description = r.ReadString(), ParentId = r.ReadInt32(), ReputationIndex = r.ReadInt32(), Flags = r.ReadInt32() });
            n = r.ReadInt32(); Count(n, 1000000);
            var ts = new List<FactionTemplateInfo>(n);
            for (int i = 0; i < n; i++)
            {
                var t = new FactionTemplateInfo { Id = r.ReadInt32(), FactionId = r.ReadInt32(), Flags = r.ReadInt32(), Group = r.ReadInt32(), FriendGroup = r.ReadInt32(), EnemyGroup = r.ReadInt32() };
                int c = r.ReadByte(); t.Enemies = new int[c]; for (int k = 0; k < c; k++) t.Enemies[k] = r.ReadInt32();
                c = r.ReadByte(); t.Friends = new int[c]; for (int k = 0; k < c; k++) t.Friends[k] = r.ReadInt32();
                ts.Add(t);
            }
            return new FactionSnapshot(fs, ts);
        }

        // ---- item details
        static void WriteMap(BinaryWriter w, Dictionary<int, float[]> m)
        {
            w.Write(m != null);
            if (m == null) return;
            w.Write(m.Count);
            foreach (var kv in m) { w.Write(kv.Key); w.Write((byte)kv.Value.Length); foreach (var v in kv.Value) w.Write(v); }
        }

        static Dictionary<int, float[]> ReadMap(BinaryReader r)
        {
            if (!r.ReadBoolean()) return null;
            int n = r.ReadInt32(); Count(n, 5000000);
            var m = new Dictionary<int, float[]>(n);
            for (int i = 0; i < n; i++) { int k = r.ReadInt32(); int c = r.ReadByte(); var a = new float[c]; for (int j = 0; j < c; j++) a[j] = r.ReadSingle(); m[k] = a; }
            return m;
        }

        static void WriteDetails(BinaryWriter w, ItemDetails d)
        {
            w.Write(d != null);
            if (d == null) return;
            WriteMap(w, d.ArmorQuality); WriteMap(w, d.ArmorTotal); WriteMap(w, d.ArmorShield); WriteMap(w, d.ArmorLocation);
            foreach (var m in d.Damage) WriteMap(w, m);
            w.Write(d.Effects != null);
            if (d.Effects != null) { w.Write(d.Effects.Count); foreach (var kv in d.Effects) { w.Write(kv.Key); w.Write((byte)kv.Value.Length); foreach (var v in kv.Value) w.Write(v); } }
            w.Write(d.Sets != null);
            if (d.Sets != null)
            {
                w.Write(d.Sets.Count);
                foreach (var s in d.Sets.Values)
                {
                    w.Write(s.Id); w.Write(s.Name ?? "");
                    w.Write((byte)s.Items.Count); foreach (var i in s.Items) w.Write(i);
                    w.Write((byte)s.Bonuses.Count); foreach (var b in s.Bonuses) { w.Write(b.Key); w.Write(b.Value); }
                }
            }
        }

        static ItemDetails ReadDetails(BinaryReader r)
        {
            if (!r.ReadBoolean()) return null;
            var d = new ItemDetails();
            d.ArmorQuality = ReadMap(r); d.ArmorTotal = ReadMap(r); d.ArmorShield = ReadMap(r); d.ArmorLocation = ReadMap(r);
            for (int k = 0; k < d.Damage.Length; k++) d.Damage[k] = ReadMap(r);
            if (r.ReadBoolean())
            {
                int n = r.ReadInt32(); Count(n, 5000000);
                d.Effects = new Dictionary<int, int[]>(n);
                for (int i = 0; i < n; i++) { int id = r.ReadInt32(); int c = r.ReadByte(); var a = new int[c]; for (int j = 0; j < c; j++) a[j] = r.ReadInt32(); d.Effects[id] = a; }
            }
            if (r.ReadBoolean())
            {
                int n = r.ReadInt32(); Count(n, 1000000);
                d.Sets = new Dictionary<int, ItemSetInfo>(n);
                for (int i = 0; i < n; i++)
                {
                    var s = new ItemSetInfo { Id = r.ReadInt32(), Name = r.ReadString() };
                    int c = r.ReadByte(); for (int j = 0; j < c; j++) { int item = r.ReadInt32(); s.Items.Add(item); d.SetOfItem[item] = s.Id; }
                    c = r.ReadByte(); for (int j = 0; j < c; j++) s.Bonuses.Add(new KeyValuePair<int, int>(r.ReadInt32(), r.ReadInt32()));
                    d.Sets[s.Id] = s;
                }
            }
            return d;
        }
    }
}
