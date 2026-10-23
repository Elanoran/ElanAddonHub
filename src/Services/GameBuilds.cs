using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Maps;
using ElansAddonHub.Services.Talents;

namespace ElansAddonHub.Services
{
    // What changed in the game's own files from one game build to the next (Settings > About > Game patches, 3.3). Every reader keeps one saved copy
    // of its data per game build; after a patch it reads the new game files while the copy of the previous build is still in hand. That moment is
    // the only one where both builds are there, so the reader hands both to this class, which compares them (items, spells, talent trees, maps,
    // zones, flight points) and keeps a short summary per build in gamebuilds.json: counts plus up to RowCap examples per kind. Only the shipped
    // tables are compared (the server's hotfix rows and the items this client happened to reveal differ per player), and nothing leaves the PC.
    public static class GameBuilds
    {
        public const int RowCap = 300;                    // examples kept per kind and build (the counts are always complete)
        public const int BuildCap = 40;                   // builds kept in the file
        public static string FilePath = Path.Combine(Util.DataDir, "gamebuilds.json");
        public static event Action Changed;               // a build or a comparison was recorded (any thread)

        public const string Added = "+", Removed = "-", Modified = "~";

        public sealed class Row
        {
            public string Op = Modified;                  // Added / Removed / Modified
            public int Id;
            public string Name = "", Detail = "", Link;   // Link: an Atlas id ("item:12", "spell:6150") or null
        }

        public sealed class KindChanges
        {
            public string Kind;                           // "items", "spells", "talents", "maps", "areas", "flights", "hotfixes"
            public int Added, Removed, Modified;
            public List<Row> Rows = new List<Row>();
            public int Total => Added + Removed + Modified;
        }

        public sealed class Entry
        {
            public string Build = "";                     // the whole build string (product|version|build key, ItemData.GameBuild)
            public string From;                           // the build it was compared with (null = no comparison yet)
            public string Seen;                           // the day this Outpost first saw it (yyyy-MM-dd, local time)
            public List<KindChanges> Changes = new List<KindChanges>();
            public string Version => VersionOf(Build);
            public int Number => NumberOf(Build);
            public KindChanges Of(string kind) => Changes.FirstOrDefault(c => c.Kind == kind);
        }

        public static readonly string[] Kinds = { "talents", "spells", "items", "maps", "areas", "flights", "hotfixes" };
        public static bool Compared(string kind) => kind != "hotfixes";   // the others compare two builds' game files; hotfixes are what the server sent at this build

        public static string LabelOf(string kind)
        {
            switch (kind)
            {
                case "items": return "Items";
                case "spells": return "Spells";
                case "talents": return "Talents";
                case "maps": return "Dungeons, raids and maps";
                case "areas": return "Zones and areas";
                case "flights": return "Flight points";
                case "hotfixes": return "Server hotfixes";
                default: return kind;
            }
        }

        // "wow_classic_beta|1.60.1.70532|ad18..." -> "1.60.1.70532" / 70532
        public static string VersionOf(string build)
        {
            var p = (build ?? "").Split('|');
            return p.Length > 1 && p[1].Length > 0 ? p[1] : build ?? "";
        }

        public static int NumberOf(string build)
        {
            var v = VersionOf(build);
            int dot = v.LastIndexOf('.');
            return dot >= 0 && int.TryParse(v.Substring(dot + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
        }

        // ---------------------------------------------------------------------------------------------------------------------
        // the history

        static readonly object gate = new object();
        static List<Entry> cache;
        static string cachePath;

        // newest build first
        public static List<Entry> All()
        {
            lock (gate) return Load().OrderByDescending(e => e.Number).ThenByDescending(e => e.Seen, StringComparer.Ordinal).ToList();
        }

        public static Entry Find(string build) { lock (gate) return Load().FirstOrDefault(e => e.Build == build); }

        // this Outpost sees the game at this build (the tab and the readers call it): listed from now on, with the day
        public static void Seen(string build)
        {
            if (string.IsNullOrEmpty(build)) return;
            lock (gate)
            {
                var list = Load();
                if (list.Any(e => e.Build == build)) return;
                list.Add(new Entry { Build = build, Seen = Today() });
                Save(list);
            }
            Raise();
        }

        // one reader compared its data of build `from` with the new build `to`
        public static void Record(string from, string to, KindChanges changes)
        {
            if (string.IsNullOrEmpty(to) || changes == null) return;
            lock (gate)
            {
                var list = Load();
                var e = list.FirstOrDefault(x => x.Build == to);
                if (e == null) { e = new Entry { Build = to, Seen = Today() }; list.Add(e); }
                var had = e.Of(changes.Kind);
                if (had != null && Same(had, changes) && (from == null || e.From != null)) return;     // the hotfixes are noted on every spell load: only a change is written
                // the first comparison names the build compared with (the hotfixes compare nothing: from = null)
                if (e.From == null) e.From = from;
                e.Changes.RemoveAll(c => c.Kind == changes.Kind);
                e.Changes.Add(changes);
                Save(list);
            }
            if (changes.Kind == "hotfixes") Util.Log("game builds: " + VersionOf(to) + ": server hotfixes for " + changes.Total + " spells");
            else Util.Log("game builds: " + VersionOf(from) + " -> " + VersionOf(to) + ": " + LabelOf(changes.Kind).ToLowerInvariant() + " " + changes.Added + " new, " + changes.Modified + " changed, " + changes.Removed + " removed");
            Raise();
        }

        static bool Same(KindChanges a, KindChanges b) =>
            a.Added == b.Added && a.Removed == b.Removed && a.Modified == b.Modified && a.Rows.Count == b.Rows.Count
            && a.Rows.Zip(b.Rows, (x, y) => x.Op == y.Op && x.Id == y.Id && x.Name == y.Name && x.Detail == y.Detail).All(z => z);

        // the spells the server's hotfix rows (DBCache.bin) change, as the spell reader laid them over the game files of this build; returns the set
        public static AtlasSet NoteHotfixes(AtlasSet set, string build)
        {
            try { var c = HotfixChanges(set); if (c != null) Record(null, build, c); }
            catch (Exception e) { Util.Log("game builds: noting the hotfixes: " + e.Message); }
            return set;
        }

        public static KindChanges HotfixChanges(AtlasSet set)
        {
            var h = set?.Hotfixes;
            if (h == null || set.Spells == null) return null;
            var res = new KindChanges { Kind = "hotfixes" };
            var rows = new List<Row>();
            foreach (var kv in h.Spells.OrderBy(k => k.Key))
            {
                var p = kv.Value;
                set.Spells.Spells.TryGetValue(kv.Key, out var sp);
                var name = sp == null ? "" : string.IsNullOrEmpty(sp.Rank) ? sp.Name ?? "" : sp.Name + " (" + sp.Rank + ")";
                var row = new Row { Id = kv.Key, Name = name, Link = p.HasFlag(HotfixParts.Removed) ? null : AtlasIds.Spell(kv.Key) };
                if (p.HasFlag(HotfixParts.Removed)) { row.Op = Removed; res.Removed++; }
                else if (p.HasFlag(HotfixParts.New)) { row.Op = Added; res.Added++; }
                else
                {
                    row.Op = Modified; res.Modified++;
                    var what = new List<string>();
                    if (p.HasFlag(HotfixParts.Name)) what.Add("name");
                    if (p.HasFlag(HotfixParts.Text)) what.Add("description");
                    if (p.HasFlag(HotfixParts.Numbers)) what.Add("cast time, duration or range");
                    if (p.HasFlag(HotfixParts.Effects)) what.Add("effects");
                    row.Detail = what.Count == 0 ? "" : "changes the " + string.Join(", ", what);
                }
                rows.Add(row);
            }
            foreach (var r in rows.Where(r => r.Op == Added).Concat(rows.Where(r => r.Op == Modified)).Concat(rows.Where(r => r.Op == Removed))) { if (res.Rows.Count >= RowCap) break; res.Rows.Add(r); }
            return res;
        }

        // a reader's comparison that must never stop the reader
        public static void Compare<T>(string kind, string from, string to, T old, T fresh, Func<T, T, KindChanges> diff) where T : class
        {
            if (old == null || fresh == null || string.IsNullOrEmpty(from) || from == to) return;
            try { var c = diff(old, fresh); if (c != null) { c.Kind = kind; Record(from, to, c); } }
            catch (Exception e) { Util.Log("game builds: comparing " + kind + ": " + e.Message); }
        }

        static void Raise() { try { Changed?.Invoke(); } catch { } }

        public static Func<DateTime> Now = () => DateTime.Now;      // self-test: a fixed day
        static string Today() => Now().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        public static void ResetForTest() { lock (gate) { cache = null; cachePath = null; } }

        static List<Entry> Load()
        {
            if (cache != null && cachePath == FilePath) return cache;
            cachePath = FilePath;
            cache = new List<Entry>();
            try
            {
                if (!File.Exists(FilePath)) return cache;
                var root = Json.Obj(File.ReadAllText(FilePath));
                foreach (var o in root.List("builds"))
                {
                    if (!(o is Dictionary<string, object> d) || string.IsNullOrEmpty(d.Str("build"))) continue;
                    var e = new Entry { Build = d.Str("build"), From = d.Str("from"), Seen = d.Str("seen") };
                    foreach (var co in d.List("changes"))
                    {
                        if (!(co is Dictionary<string, object> c) || string.IsNullOrEmpty(c.Str("kind"))) continue;
                        var k = new KindChanges { Kind = c.Str("kind"), Added = c.Int("added"), Removed = c.Int("removed"), Modified = c.Int("changed") };
                        foreach (var ro in c.List("rows"))
                            if (ro is Dictionary<string, object> r)
                                k.Rows.Add(new Row { Op = r.Str("op") ?? Modified, Id = r.Int("id"), Name = r.Str("name") ?? "", Detail = r.Str("detail") ?? "", Link = r.Str("link") });
                        e.Changes.Add(k);
                    }
                    cache.Add(e);
                }
            }
            catch (Exception e) { Util.Log("game builds: " + FilePath + " could not be read (" + e.Message + "); starting a new list"); cache = new List<Entry>(); }
            return cache;
        }

        static void Save(List<Entry> list)
        {
            if (list.Count > BuildCap) { var keep = list.OrderByDescending(e => e.Number).Take(BuildCap).ToList(); list.Clear(); list.AddRange(keep); }
            var builds = new List<object>();
            foreach (var e in list)
            {
                var changes = new List<object>();
                foreach (var c in e.Changes)
                    changes.Add(new Dictionary<string, object>
                    {
                        ["kind"] = c.Kind, ["added"] = c.Added, ["removed"] = c.Removed, ["changed"] = c.Modified,
                        ["rows"] = c.Rows.Select(r => (object)new Dictionary<string, object> { ["op"] = r.Op, ["id"] = r.Id, ["name"] = r.Name, ["detail"] = r.Detail, ["link"] = r.Link }).ToList(),
                    });
                builds.Add(new Dictionary<string, object> { ["build"] = e.Build, ["from"] = e.From, ["seen"] = e.Seen, ["changes"] = changes });
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, Json.Write(new Dictionary<string, object> { ["version"] = 1, ["builds"] = builds }));
                File.Copy(tmp, FilePath, true); File.Delete(tmp);
            }
            catch (Exception e) { Util.Log("game builds: saving: " + e.Message); }
        }

        // ---------------------------------------------------------------------------------------------------------------------
        // the comparisons. Each thing becomes a list of (label, value) facts; a changed fact is named in the row's detail ("item level 20 -> 22").

        sealed class Facts
        {
            public int Id;
            public string Name = "", Link;
            public readonly List<KeyValuePair<string, string>> List = new List<KeyValuePair<string, string>>();
            public Facts Add(string label, object v) { List.Add(new KeyValuePair<string, string>(label, Text(v))); return this; }
        }

        static string Text(object v)
        {
            switch (v)
            {
                case null: return "";
                case float f: return f.ToString("0.##", CultureInfo.InvariantCulture);
                case double d: return d.ToString("0.##", CultureInfo.InvariantCulture);
                case IFormattable n: return n.ToString(null, CultureInfo.InvariantCulture);
                default: return v.ToString();
            }
        }

        static KindChanges Diff(IEnumerable<Facts> before, IEnumerable<Facts> after)
        {
            var a = new Dictionary<int, Facts>(); foreach (var f in before) a[f.Id] = f;
            var b = new Dictionary<int, Facts>(); foreach (var f in after) b[f.Id] = f;
            var res = new KindChanges();
            var added = new List<Row>(); var removed = new List<Row>(); var changed = new List<Row>();
            foreach (var kv in b.OrderBy(k => k.Key))
            {
                if (!a.TryGetValue(kv.Key, out var old)) { res.Added++; added.Add(new Row { Op = Added, Id = kv.Key, Name = kv.Value.Name, Link = kv.Value.Link }); continue; }
                var what = new List<string>();
                var oldFacts = old.List.ToDictionary(p => p.Key, p => p.Value);
                if (old.Name != kv.Value.Name) what.Add("renamed from " + old.Name);
                foreach (var p in kv.Value.List)
                {
                    if (!oldFacts.TryGetValue(p.Key, out var was)) { if (p.Value.Length > 0) what.Add(p.Key + " " + Short(p.Value)); continue; }
                    if (was == p.Value) continue;
                    what.Add(Short(was).Length + Short(p.Value).Length > 60 || was.Length == 0 || p.Value.Length == 0 ? p.Key : p.Key + " " + Short(was) + " → " + Short(p.Value));
                }
                if (what.Count == 0) continue;
                res.Modified++;
                changed.Add(new Row { Op = Modified, Id = kv.Key, Name = kv.Value.Name, Link = kv.Value.Link, Detail = string.Join(", ", what.Take(4)) + (what.Count > 4 ? " and " + (what.Count - 4) + " more" : "") });
            }
            foreach (var kv in a.OrderBy(k => k.Key))
                if (!b.ContainsKey(kv.Key)) { res.Removed++; removed.Add(new Row { Op = Removed, Id = kv.Key, Name = kv.Value.Name, Link = null }); }
            // the examples: new things first, then changes, then what went away (each within the cap)
            foreach (var r in added.Concat(changed).Concat(removed)) { if (res.Rows.Count >= RowCap) break; res.Rows.Add(r); }
            return res;
        }

        static string Short(string s) => s.Length <= 40 ? s : s.Substring(0, 39).TrimEnd() + "…";

        // ---- items: the client's own item table (not the items this client revealed from the server's cache)
        public static KindChanges DiffItems(ItemSet before, ItemSet after) => Diff(ItemFacts(before), ItemFacts(after));

        static IEnumerable<Facts> ItemFacts(ItemSet s)
        {
            foreach (var it in s.Client)
            {
                var f = new Facts { Id = it.Id, Name = it.Name ?? "", Link = AtlasIds.Item(it.Id) };
                f.Add("quality", it.Quality).Add("item level", it.ItemLevel).Add("required level", it.RequiredLevel).Add("slot", it.InventoryType)
                 .Add("type", it.ClassId + "/" + it.SubclassId).Add("binding", it.Bonding).Add("stack", it.Stackable).Add("speed", it.DelayMs).Add("bag slots", it.ContainerSlots)
                 .Add("sell price", it.SellPrice).Add("buy price", it.BuyPrice).Add("stats", it.Stats == null ? "?" : string.Join(",", it.Stats)).Add("damage spread", it.DmgVariance)
                 .Add("item set", it.ItemSetId).Add("classes", it.AllowableClass).Add("races", it.AllowableRace).Add("skill", it.RequiredSkill + "/" + it.RequiredSkillRank)
                 .Add("reputation", it.MinFactionId + "/" + it.MinReputation).Add("PvP rank", it.RequiredPvpRank).Add("description", it.Description);
                yield return f;
            }
        }

        // ---- spells: the shipped spell tables (the server's hotfix rows are laid over later and differ per client)
        public static KindChanges DiffSpells(AtlasSet before, AtlasSet after) =>
            before?.Spells == null || after?.Spells == null ? null : Diff(SpellFacts(before.Spells), SpellFacts(after.Spells));

        static IEnumerable<Facts> SpellFacts(SpellSnapshot s)
        {
            foreach (var sp in s.Spells.Values)
            {
                var f = new Facts { Id = sp.Id, Name = string.IsNullOrEmpty(sp.Rank) ? sp.Name ?? "" : sp.Name + " (" + sp.Rank + ")", Link = AtlasIds.Spell(sp.Id) };
                f.Add("description", sp.Desc).Add("aura text", sp.Aura).Add("icon", sp.IconId).Add("cast time", sp.CastMs).Add("duration", sp.DurationMs).Add("range", sp.RangeMax);
                foreach (var e in sp.Effects ?? SpellInfo.NoEffects)
                    f.Add("effect " + (e.Index + 1), e.Effect + "/" + e.Aura + " " + Text(e.Points) + (e.PeriodMs > 0 ? " every " + e.PeriodMs : "") + (e.Coef != 0 ? " x" + Text(e.Coef) : "") + (e.Radius != 0 ? " r" + Text(e.Radius) : ""));
                yield return f;
            }
        }

        // ---- talents: the nodes of each class's trees (id = the node), named after their spell
        public static Func<TalentNode, string> TalentName = n => { try { return TalentData.NodeName(n); } catch { return null; } };

        public static KindChanges DiffTalents(TalentSet before, TalentSet after) => Diff(TalentFacts(before), TalentFacts(after));

        static IEnumerable<Facts> TalentFacts(TalentSet s)
        {
            foreach (var t in s.Trees)
                foreach (var n in t.Nodes)
                {
                    var name = TalentName(n);
                    if (string.IsNullOrEmpty(name)) name = "talent " + n.Id;
                    var spec = n.Spec >= 0 && n.Spec < t.SpecNames.Length ? t.SpecNames[n.Spec] : "tree " + (n.Spec + 1);
                    var f = new Facts { Id = n.Id, Name = (string.IsNullOrEmpty(t.ClassName) ? "" : t.ClassName + ": ") + name, Link = n.SpellId > 0 ? AtlasIds.Spell(n.SpellId) : null };
                    f.Add("tree", spec).Add("place", "row " + (n.Row + 1) + " column " + (n.Col + 1)).Add("ranks", n.MaxRanks).Add("spell", n.SpellId)
                     .Add("needs", string.Join(",", t.Incoming(n.Id).Where(e => e.Type != TalentEdge.Visual).Select(e => e.From).OrderBy(x => x)) + "|" + string.Join(",", n.Conds.Select(c => c.Type + ":" + c.Spent).OrderBy(x => x, StringComparer.Ordinal)))
                     .Add("rank amounts", string.Join(";", (n.Entry?.Curves ?? new List<EffectCurve>()).Select(c => c.EffectIndex + "=" + string.Join(",", c.ByRank.Select(v => Text(v))))));
                    yield return f;
                }
        }

        // ---- maps: the game's maps (continents, dungeons, raids), the zones and areas, the flight points
        public static KindChanges DiffMaps(MapSet before, MapSet after) =>
            !before.HasMaps || !after.HasMaps ? null : Diff(before.Maps.Select(MapFacts), after.Maps.Select(MapFacts));

        static Facts MapFacts(GameMapInfo m)
        {
            string kind = m.InstanceType == 1 ? "dungeon" : m.InstanceType == 2 ? "raid" : m.InstanceType == 3 ? "battleground" : m.InstanceType == 4 ? "arena" : m.InstanceType == 0 ? "world" : "instance " + m.InstanceType;
            return new Facts { Id = m.Id, Name = m.Name ?? "", Link = m.InstanceType == 1 || m.InstanceType == 2 ? "instance:" + m.Id.ToString(CultureInfo.InvariantCulture) : null }
                .Add("kind", kind).Add("players", m.MaxPlayers).Add("folder", m.Directory).Add("area", m.AreaTableId);
        }

        public static KindChanges DiffAreas(MapSet before, MapSet after) =>
            !before.HasAreas || !after.HasAreas ? null : Diff(before.Areas.Select(AreaFacts), after.Areas.Select(AreaFacts));

        static Facts AreaFacts(AreaInfo a) => new Facts { Id = a.Id, Name = a.Name ?? "" }.Add("part of", a.ParentId).Add("map", a.ContinentId);

        public static KindChanges DiffFlights(MapSet before, MapSet after) =>
            !before.HasTaxi || !after.HasTaxi ? null : Diff(before.TaxiNodes.Select(FlightFacts), after.TaxiNodes.Select(FlightFacts));

        static Facts FlightFacts(TaxiNodeInfo t) => new Facts { Id = t.Id, Name = t.Name ?? "" }.Add("map", t.MapId).Add("place", Math.Round(t.X) + "," + Math.Round(t.Y)).Add("flags", t.Flags);
    }
}
