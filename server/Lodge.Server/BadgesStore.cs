using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Lodge;

// data/badges.json = { v, enabled, season, members: { identity: { name, first, hide, seen, earned: { id: unixSec }, tally: { key: n }, chatDay, nightDay, signed: [eventId], base: { metric: n }, pts, spts } },
//                      history: { "2026-09": [ { k, n, p } ] } }
// Atomic (tmp, flush, rename; a leftover tmp is used only when the main file is missing), size-capped on load, every loaded value validated. An earned badge is
// kept by id and never taken away (not when the data it was earned from is pruned, not when a counter shrinks); it goes only with the member. The tallies
// are the few numbers no other store keeps per member (files shared, events created, days in chat...) and count from the day the feature went live.
// Never logs anything but counts.
public sealed class BadgesStore
{
    const long MaxFileBytes = 8L * 1024 * 1024;
    public const int MaxMembers = 2000;
    public const int MaxSigned = 300;
    public static readonly string[] TallyKeys = { "files", "host", "events", "signups", "lfg", "chatdays", "nightdays", "quests", "seen" };

    public sealed class Rec
    {
        public string Name = "";
        public long First, SeenMs;                       // First: unix seconds this member was first noticed; SeenMs: when they last looked at their badges
        public bool Hide;
        public Dictionary<string, long> Earned = new(StringComparer.Ordinal);
        public Dictionary<string, long> Tally = new(StringComparer.Ordinal);
        public long ChatDay = -1, NightDay = -1;         // days since 1970 of the last counted chat day / late night
        public List<string> Signed = new();
        public Dictionary<string, long> Base = new(StringComparer.Ordinal);   // the metrics at the start of the season
        public long Points, SeasonPoints;                // as of the last run
        public long Of(string tally) => Tally.TryGetValue(tally, out var v) ? v : 0;
    }

    public sealed record HistRow(string Key, string Name, long Points);

    readonly string path, tmp;
    readonly ILogger log;
    public readonly object Gate = new();
    readonly Dictionary<string, Rec> members = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<HistRow>> history = new(StringComparer.Ordinal);
    public bool Dirty { get; private set; }
    public bool Enabled { get; private set; } = true;     // the owner's switch (the server's LODGE_BADGES_ENABLED is separate)
    public string Season { get; private set; } = "";

    public BadgesStore(string dataDir, ILogger log)
    {
        this.log = log;
        path = Path.Combine(dataDir, "badges.json");
        tmp = path + ".tmp";
        try
        {
            if (File.Exists(tmp)) { if (!File.Exists(path)) File.Move(tmp, path); else File.Delete(tmp); }
            if (File.Exists(path) && new FileInfo(path).Length <= MaxFileBytes) Load(JsonNode.Parse(File.ReadAllText(path))?.AsObject());
        }
        catch (Exception e) { log?.LogWarning("Badges: load failed: {Error}", e.GetType().Name); }
    }

    static long L(JsonNode n) => n is JsonValue v && v.TryGetValue<long>(out var l) ? l : 0;
    static string S(JsonNode n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    void Load(JsonObject root)
    {
        if (root == null) return;
        if (root["enabled"] is JsonValue ev && ev.TryGetValue<bool>(out var en)) Enabled = en;
        var season = S(root["season"]);
        Season = Badges.ValidSeason(season) && season != "all" ? season : "";
        if (root["members"] is JsonObject mo)
            foreach (var (key, node) in mo)
            {
                if (members.Count >= MaxMembers) break;
                if (key.Length == 0 || key.Length > 64 || node is not JsonObject d) continue;
                var r = new Rec
                {
                    Name = Names.Cut(S(d["name"]) ?? "", Names.Max), First = Math.Max(0, L(d["first"])), SeenMs = Math.Max(0, L(d["seen"])),
                    Hide = d["hide"] is JsonValue hv && hv.TryGetValue<bool>(out var h) && h,
                    ChatDay = d["chatDay"] != null ? L(d["chatDay"]) : -1, NightDay = d["nightDay"] != null ? L(d["nightDay"]) : -1,
                    Points = Math.Max(0, L(d["pts"])), SeasonPoints = Math.Max(0, L(d["spts"])),
                };
                if (d["earned"] is JsonObject eo) foreach (var (id, at) in eo) if (Badges.Find(id) != null && L(at) > 0) r.Earned[id] = L(at);
                if (d["tally"] is JsonObject to) foreach (var (k, v) in to) if (Array.IndexOf(TallyKeys, k) >= 0) r.Tally[k] = Math.Max(0, L(v));
                if (d["base"] is JsonObject bo) foreach (var (k, v) in bo) if (Badges.MetricOf(k) != null) r.Base[k] = Math.Max(0, L(v));
                if (d["signed"] is JsonArray sa) foreach (var s in sa.Take(MaxSigned)) if (S(s) is { Length: > 0 and <= 40 } id) r.Signed.Add(id);
                members[key] = r;
            }
        if (root["history"] is JsonObject ho)
            foreach (var (season2, arr) in ho)
            {
                if (!Badges.ValidSeason(season2) || season2 == "all" || arr is not JsonArray a || history.Count >= Badges.MaxSeasonsKept) continue;
                var rows = new List<HistRow>();
                foreach (var n in a.Take(Badges.MaxHistoryRows))
                    if (n is JsonObject o && S(o["k"]) is { Length: > 0 } k) rows.Add(new HistRow(k, Names.Cut(S(o["n"]) ?? "", Names.Max), Math.Max(0, L(o["p"]))));
                history[season2] = rows;
            }
    }

    // ---- reading (callers hold Gate)
    public Rec Get(string identity) => members.TryGetValue(identity, out var r) ? r : null;
    public IEnumerable<KeyValuePair<string, Rec>> All => members;
    public IReadOnlyList<string> Seasons => history.Keys.OrderByDescending(k => k, StringComparer.Ordinal).ToList();
    public List<HistRow> HistoryOf(string season) => history.TryGetValue(season, out var h) ? h : null;

    // ---- changing (callers hold Gate; Dirty tells the module to save)
    public Rec Ensure(string identity, string name, long nowSec, long firstHint)
    {
        if (!members.TryGetValue(identity, out var r))
        {
            if (members.Count >= MaxMembers) return null;
            r = new Rec { Name = name, First = firstHint > 0 ? Math.Min(firstHint, nowSec) : nowSec };
            members[identity] = r; Dirty = true;
        }
        else
        {
            if (r.Name != name) { r.Name = name; Dirty = true; }
            if (firstHint > 0 && firstHint < r.First) { r.First = firstHint; Dirty = true; }
        }
        return r;
    }

    public void SetEnabled(bool on) { if (Enabled != on) { Enabled = on; Dirty = true; } }
    public void SetSeason(string key) { if (Season != key) { Season = key; Dirty = true; } }
    public void Touch() => Dirty = true;

    public void Archive(string season, List<HistRow> rows)
    {
        history[season] = rows.OrderByDescending(r => r.Points).ThenBy(r => r.Key, StringComparer.Ordinal).Take(Badges.MaxHistoryRows).ToList();
        foreach (var old in history.Keys.OrderByDescending(k => k, StringComparer.Ordinal).Skip(Badges.MaxSeasonsKept).ToList()) history.Remove(old);
        Dirty = true;
    }

    // tally counters: only ever grow
    public void Add(string identity, string key, long n)
    {
        if (n <= 0 || Array.IndexOf(TallyKeys, key) < 0 || !members.TryGetValue(identity, out var r)) return;
        r.Tally[key] = Math.Min(10_000_000, r.Of(key) + n); Dirty = true;
    }

    public bool Remove(string identity)
    {
        bool had = members.Remove(identity);
        foreach (var rows in history.Values) had |= rows.RemoveAll(x => x.Key == identity) > 0;
        if (had) Dirty = true;
        return had;
    }

    // ---- saving
    public bool Save()
    {
        try
        {
            var mo = new JsonObject();
            foreach (var (key, r) in members)
            {
                var d = new JsonObject
                {
                    ["name"] = r.Name, ["first"] = r.First, ["seen"] = r.SeenMs, ["hide"] = r.Hide, ["chatDay"] = r.ChatDay, ["nightDay"] = r.NightDay,
                    ["pts"] = r.Points, ["spts"] = r.SeasonPoints,
                };
                var eo = new JsonObject(); foreach (var (id, at) in r.Earned) eo[id] = at; d["earned"] = eo;
                var to = new JsonObject(); foreach (var (k, v) in r.Tally) to[k] = v; d["tally"] = to;
                var bo = new JsonObject(); foreach (var (k, v) in r.Base) bo[k] = v; d["base"] = bo;
                d["signed"] = new JsonArray(r.Signed.Select(s => (JsonNode)JsonValue.Create(s)).ToArray());
                mo[key] = d;
            }
            var ho = new JsonObject();
            foreach (var (s, rows) in history)
                ho[s] = new JsonArray(rows.Select(x => (JsonNode)new JsonObject { ["k"] = x.Key, ["n"] = x.Name, ["p"] = x.Points }).ToArray());
            var root = new JsonObject { ["v"] = 1, ["enabled"] = Enabled, ["season"] = Season, ["members"] = mo, ["history"] = ho };
            var bytes = Encoding.UTF8.GetBytes(root.ToJsonString());
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
            Dirty = false;
            return true;
        }
        catch (Exception e)
        {
            log?.LogWarning("Badges: save failed: {Error}", e.GetType().Name);
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            return false;
        }
    }
}
