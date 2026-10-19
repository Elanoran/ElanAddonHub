using System.Text;
using System.Text.Json.Nodes;
using Elans.Shared;

namespace Lodge;

// data/armory.json = { v: 1, members: { identity: { name, chars: { key: { up, c: <clean snapshot> } } } } }
// Atomic (tmp, flush, rename; a leftover tmp is used only when the main file is missing), size-capped on load, every loaded character goes through the
// same Armory.Clean as a put. Owned by the member: only the identity that put a character can replace or delete it. Never logs any content.
public sealed class ArmoryStore
{
    const long MaxFileBytes = 96L * 1024 * 1024;

    sealed class Entry { public JsonObject C; public long Up; }
    sealed class Owner { public string Name; public readonly Dictionary<string, Entry> Chars = new(StringComparer.Ordinal); }

    readonly string path, tmp;
    readonly ILogger log;
    readonly IClock clock;
    public readonly object Gate = new();
    readonly Dictionary<string, Owner> owners = new(StringComparer.Ordinal);
    DateTime lastSave;
    public bool Dirty { get; private set; }

    public ArmoryStore(string dataDir, ILogger log, IClock clock)
    {
        this.log = log; this.clock = clock;
        path = Path.Combine(dataDir, "armory.json");
        tmp = path + ".tmp";
        lastSave = clock.UtcNow;
        try
        {
            if (File.Exists(tmp)) { if (!File.Exists(path)) File.Move(tmp, path); else File.Delete(tmp); }
            if (File.Exists(path) && new FileInfo(path).Length <= MaxFileBytes) Load(JsonNode.Parse(File.ReadAllText(path))?.AsObject());
        }
        catch (Exception e) { log?.LogWarning("Armory: load failed: {Error}", e.GetType().Name); }
    }

    long NowMs => new DateTimeOffset(clock.UtcNow).ToUnixTimeMilliseconds();

    void Load(JsonObject root)
    {
        if (root?["members"] is not JsonObject mo) return;
        int total = 0;
        foreach (var (id, v) in mo)
        {
            if (id.Length == 0 || id.Length > 64 || v is not JsonObject d || d["chars"] is not JsonObject chars) continue;
            var ow = new Owner { Name = Names.Text(J.Str(d, "name") ?? id, 64) };
            foreach (var (key, node) in chars)
            {
                if (ow.Chars.Count >= Armory.MaxCharsPerMember || total >= Armory.MaxCharsTotal) break;
                if (!Armory.ValidKey(key) || node is not JsonObject e || e["c"] is not JsonObject c) continue;
                var clean = Armory.Clean(c, out _, true);       // reputations stay on disk; whether they are served is the module's call (feature switch)
                if (clean == null) continue;
                long up = e["up"] is JsonValue uv && uv.TryGetValue<long>(out var ul) && ul > 0 ? ul : 0;
                ow.Chars[key] = new Entry { C = clean, Up = up };
                total++;
            }
            if (ow.Chars.Count > 0) owners[id] = ow;
        }
    }

    public int MemberCount { get { lock (Gate) return owners.Count; } }
    public int CharCount { get { lock (Gate) return owners.Values.Sum(o => o.Chars.Count); } }
    public int CountOf(string identity) { lock (Gate) return owners.TryGetValue(identity, out var o) ? o.Chars.Count : 0; }

    public enum PutResult { Stored, Unchanged, Full, Invalid }

    // the caller holds Gate
    public PutResult Put(string identity, string login, string key, JsonObject clean)
    {
        if (!Armory.ValidKey(key) || clean == null) return PutResult.Invalid;
        if (!owners.TryGetValue(identity, out var ow)) owners[identity] = ow = new Owner();
        ow.Name = Names.Text(login, 64);
        if (ow.Chars.TryGetValue(key, out var old))
        {
            if (old.C.ToJsonString() == clean.ToJsonString()) return PutResult.Unchanged;
        }
        else
        {
            if (ow.Chars.Count >= Armory.MaxCharsPerMember || owners.Values.Sum(o => o.Chars.Count) >= Armory.MaxCharsTotal)
            {
                if (ow.Chars.Count == 0) owners.Remove(identity);
                return PutResult.Full;
            }
        }
        ow.Chars[key] = new Entry { C = clean, Up = NowMs };
        Dirty = true;
        return PutResult.Stored;
    }

    // the caller holds Gate. Returns how many characters went; a deletion is written to disk at once
    public int Delete(string identity, string key)
    {
        if (!owners.TryGetValue(identity, out var ow)) return 0;
        int n = 0;
        if (key == null) { n = ow.Chars.Count; ow.Chars.Clear(); }
        else if (ow.Chars.Remove(key)) n = 1;
        if (ow.Chars.Count == 0) owners.Remove(identity);
        if (n > 0) { Dirty = true; Save(); }
        return n;
    }

    // everything of members who are no longer in the Lodge (the caller holds Gate)
    public int Prune(Func<string, bool> stillMember)
    {
        int n = 0;
        foreach (var id in owners.Keys.ToList())
            if (!stillMember(id)) { n += owners[id].Chars.Count; owners.Remove(id); }
        if (n > 0) { Dirty = true; Save(); }
        return n;
    }

    public string NameOf(string identity) => owners.TryGetValue(identity, out var o) ? o.Name : null;

    public JsonObject Get(string identity, string key)
    {
        if (key == null || !owners.TryGetValue(identity, out var ow) || !ow.Chars.TryGetValue(key, out var e)) return null;
        return e.C;
    }

    // the list: one summary per character (the caller holds Gate); `shown` turns a login into the name the Lodge sees (display name, else the login), `me` marks the caller's own
    public List<ArmorySummary> List(Func<string, string, string> shown, string me)
    {
        var list = new List<ArmorySummary>();
        foreach (var (id, ow) in owners.OrderBy(k => k.Key, StringComparer.Ordinal))
            foreach (var (key, e) in ow.Chars.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                var s = Armory.Summary(e.C);
                s.Owner = id; s.By = shown(id, ow.Name); s.Key = key; s.Up = e.Up; s.Mine = id == me;
                list.Add(s);
            }
        return list;
    }

    // "who can craft this" (the caller holds Gate): rows of every shared character that learned any of `spells`, best skill first, capped
    public List<ArmoryCrafter> Crafters(IReadOnlySet<int> spells, Func<string, string, string> shown, string me)
    {
        var rows = new List<ArmoryCrafter>();
        foreach (var (id, ow) in owners.OrderBy(k => k.Key, StringComparer.Ordinal))
            foreach (var (key, e) in ow.Chars.OrderBy(k => k.Key, StringComparer.Ordinal))
                foreach (var r in Armory.CraftersOf(e.C, spells))
                {
                    r.Owner = id; r.By = shown(id, ow.Name); r.Key = key; r.Mine = id == me;
                    rows.Add(r);
                }
        return rows.OrderByDescending(x => x.Skill).ThenBy(x => x.Name, StringComparer.Ordinal).Take(Armory.MaxCrafterRows).ToList();
    }

    // "standing with a faction" (the caller holds Gate): one row per shared character that has a reputation with it, best standing first, capped
    public List<ArmoryStanding> Standings(int faction, Func<string, string, string> shown, string me)
    {
        var rows = new List<ArmoryStanding>();
        foreach (var (id, ow) in owners.OrderBy(k => k.Key, StringComparer.Ordinal))
            foreach (var (key, e) in ow.Chars.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                var r = Armory.StandingOf(e.C, faction);
                if (r == null) continue;
                r.Owner = id; r.By = shown(id, ow.Name); r.Key = key; r.Mine = id == me;
                rows.Add(r);
            }
        return rows.OrderByDescending(x => x.S).ThenByDescending(x => x.V).ThenBy(x => x.Name, StringComparer.Ordinal).Take(Armory.MaxStandingRows).ToList();
    }

    public bool Save()
    {
        try
        {
            var members = new JsonObject();
            foreach (var (id, ow) in owners)
            {
                var chars = new JsonObject();
                foreach (var (key, e) in ow.Chars) chars[key] = new JsonObject { ["up"] = e.Up, ["c"] = e.C.DeepClone() };
                members[id] = new JsonObject { ["name"] = ow.Name, ["chars"] = chars };
            }
            var root = new JsonObject { ["v"] = 1, ["members"] = members };
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var bytes = Encoding.UTF8.GetBytes(root.ToJsonString());
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);
            }
            File.Move(tmp, path, true);
            Dirty = false; lastSave = clock.UtcNow;
            return true;
        }
        catch (Exception e) { log?.LogWarning("Armory: save failed: {Error}", e.GetType().Name); return false; }
    }

    public bool SaveIfDirty(TimeSpan minGap)
    {
        if (!Dirty) return true;
        if (clock.UtcNow - lastSave < minGap) return true;
        return Save();
    }
}
