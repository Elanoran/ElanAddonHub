using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ElansAddonHub.Services
{
    // Elan's Hub data layer, Outpost side (see ARCHITECTURE.md): reads ElansHubData from SavedVariables\ElansHub.lua.
    //   ElansHubData = { schema = 1, chars = { ["Realm-Name"] = { at = ..., mods = { core = { v, at, by, ver, data } } } }, account = { mods = { ... } } }
    // The file is parsed once per change (path + mtime + size). A module whose version is newer than this app understands (Supported) is marked
    // TooNew and its data is not exposed: the UI says "Update Elan's Outpost" instead of guessing. Nothing here writes anything.
    public class HubModule
    {
        public string Name;
        public int V;                                   // schema version of Data
        public long At;                                 // unix seconds the addon stored it
        public string By, Ver;                          // the addon that published it and its version
        public bool TooNew;                             // V is newer than Supported[Name]
        public Dictionary<string, object> Data;         // null when TooNew
    }

    public class HubChar
    {
        public string Key;                              // "Realm-Name"
        public long At;
        public Dictionary<string, HubModule> Modules = new Dictionary<string, HubModule>(StringComparer.Ordinal);

        // core v1, or null when the module is missing, too new, or another version
        public HubCore Core => HubData.CoreOf(Modules.TryGetValue("core", out var m) ? m : null);
    }

    public class HubModel
    {
        public int Schema;
        public bool Found;                              // an ElansHubData table was there
        public string SourceFile;
        public List<HubChar> Characters = new List<HubChar>();   // newest first
        public Dictionary<string, HubModule> AccountModules = new Dictionary<string, HubModule>(StringComparer.Ordinal);

        public HubChar Char(string key) => Characters.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));
    }

    // core v1 (Hub, per character)
    public class HubCore
    {
        public string Name, Realm, Class, ClassFile, Race, RaceFile, Faction, Guild, Zone, Subzone;
        public int Sex;                                 // 2 male, 3 female, 0 unknown
        public int Level;
        public long Xp, XpMax, Rested, Money;           // Money in copper
        public long Updated;                            // unix seconds
    }

    public static class HubData
    {
        public const string FileName = "ElansHub.lua";

        // module -> highest schema version this app understands. A module not listed is shown as stored (never TooNew), but nothing reads it yet.
        public static readonly Dictionary<string, int> Supported = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["core"] = 1, ["flight"] = 1,                                                                         // flight: account module, FlightOf below
            ["bags"] = 1, ["bank"] = 1, ["guildbank"] = 1, ["gear"] = 1, ["talents"] = 1, ["professions"] = 1,     // typed readers: HubModules.cs
            ["hunter.pets"] = 1,                                                                                  // Hunter Helper 1.26.5: HubModules.PetsOf
            ["health"] = 1,                                                                                       // account module from Elan's Toolkit's Bug Trap: AddonHealth.cs (HealthReader)
        };

        static readonly object gate = new object();
        static string cacheKey = "";
        static HubModel cached = new HubModel();

        public static HubModel Parse(string luaText)
        {
            try { return string.IsNullOrEmpty(luaText) ? new HubModel() : FromGlobals(LuaData.ReadGlobals(luaText)); }
            catch (Exception e) { Util.Log("hub data: parse failed (" + e.GetType().Name + ")"); return new HubModel(); }
        }

        // the same from an already parsed file (SavedVarsCache); the tables are shared, so the model keeps references into them and nobody may write to them
        public static HubModel FromGlobals(Dictionary<string, object> globals)
        {
            var model = new HubModel();
            try
            {
                if (globals == null) return model;
                if (!globals.TryGetValue("ElansHubData", out var v) || !(v is Dictionary<string, object> root)) return model;
                model.Found = true;
                model.Schema = root.Int("schema");
                var chars = root.Child("chars");
                if (chars != null)
                    foreach (var kv in chars)
                    {
                        if (!(kv.Value is Dictionary<string, object> c) || kv.Key.Length == 0) continue;
                        var ch = new HubChar { Key = kv.Key, At = c.Long("at") };
                        ReadModules(c.Child("mods"), ch.Modules);
                        model.Characters.Add(ch);
                    }
                model.Characters = model.Characters.OrderByDescending(c => c.At).ThenBy(c => c.Key, StringComparer.Ordinal).ToList();
                ReadModules(root.Child("account")?.Child("mods"), model.AccountModules);
            }
            catch (Exception e) { Util.Log("hub data: parse failed (" + e.GetType().Name + ")"); return new HubModel(); }
            return model;
        }

        static void ReadModules(Dictionary<string, object> mods, Dictionary<string, HubModule> into)
        {
            if (mods == null) return;
            foreach (var kv in mods)
            {
                if (!(kv.Value is Dictionary<string, object> e)) continue;
                var m = new HubModule { Name = kv.Key, V = e.Int("v"), At = e.Long("at"), By = e.Str("by"), Ver = e.Str("ver") };
                if (m.V < 1) continue;                                           // not a module entry
                m.TooNew = Supported.TryGetValue(m.Name, out var max) && m.V > max;
                m.Data = m.TooNew ? null : (e.Child("data") ?? new Dictionary<string, object>());
                into[kv.Key] = m;
            }
        }

        // core v1 from a module; null when absent, too new, or another version
        public static HubCore CoreOf(HubModule m)
        {
            if (m == null || m.TooNew || m.V != 1 || m.Data == null) return null;
            var d = m.Data;
            return new HubCore
            {
                Name = d.Str("name"), Realm = d.Str("realm"), Class = d.Str("class"), ClassFile = d.Str("classFile"), Race = d.Str("race"), RaceFile = d.Str("raceFile"),
                Sex = d.Int("sex"), Faction = d.Str("faction"), Level = d.Int("level"), Xp = d.Long("xp"), XpMax = d.Long("xpMax"), Rested = d.Long("rested"),
                Money = d.Long("money"), Guild = d.Str("guild"), Zone = d.Str("zone"), Subzone = d.Str("subzone"), Updated = d.Long("updated"),
            };
        }

        // account module `flight` v1 as the same validated shape FlightShare.Parse gives for Hunter Helper's file; null when absent, too new or another version
        public static FlightShare.Local FlightOf(HubModel model)
        {
            if (model == null || !model.AccountModules.TryGetValue("flight", out var m) || m == null || m.TooNew || m.V != 1 || m.Data == null) return null;
            return FlightShare.FromTables(m.Data.Child("legs"), m.Data.Child("names"));
        }

        // the newest ElansHub.lua among all flavors / accounts. Parsed again only when the path, mtime or size changed; a failed read keeps the last good model.
        public static HubModel Read(string root)
        {
            string file = null; DateTime stamp = DateTime.MinValue;
            foreach (var f in SavedVarsFiles.For(root, FileName))
            {
                try
                {
                    var fi = new FileInfo(f);
                    if (file == null || fi.LastWriteTimeUtc > stamp) { file = f; stamp = fi.LastWriteTimeUtc; }
                }
                catch { }
            }
            lock (gate)
            {
                if (file == null) { cacheKey = ""; cached = new HubModel(); return cached; }
                var p = SavedVarsCache.Load(file);                               // one parse per change, shared with the other readers; a locked file keeps the last good parse
                if (p == null) return cached;                                    // never readable yet: keep the last good model, try again next time
                if (p.Key == cacheKey) return cached;
                try
                {
                    var model = FromGlobals(p.Globals);
                    model.SourceFile = file;
                    cached = model; cacheKey = p.Key;
                }
                catch (Exception e) { Util.Log("hub data: read failed (" + e.GetType().Name + ")"); }
                return cached;
            }
        }

        public static void ResetCache() { lock (gate) { cacheKey = ""; cached = new HubModel(); } }
    }
}
