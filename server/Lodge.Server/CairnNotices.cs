using System.Text.Json.Nodes;

namespace Lodge;

// 2.31 (feature "cairnnotices"): notices from the Cairn's operator - a security issue, maintenance, news. The Cairn hands this Lodge the notices
// meant for it with every status / contribution answer (CairnContributor.NoticesArrived; checked every 15 minutes too). Each one is signed with the
// Cairn's dataset key, and the Lodge passes it on unchanged (id, times, level, text, signature), so the Outpost checks it against the key it pins:
// a Lodge can't make one up. The Guild Master and officers get them (and every member for a critical one) as "cairn.notices"; when an officer has
// seen one, the Lodge tells the Cairn (POST /v1/notices/seen), so its operator knows the warning landed.
// Kept in <data>/cairn-notices.json (the list as the Cairn last sent it, and which ids were reported as seen).
public sealed class CairnNotices
{
    public const int MaxText = 500, MaxNotices = 20;
    static readonly string[] Levels = { "info", "warning", "critical" };
    readonly string path;
    readonly ILogger log;
    readonly object gate = new();
    List<JsonObject> list = new();
    readonly HashSet<long> reported = new();
    public event Action Changed;

    public CairnNotices(string dataDir, ILogger log)
    {
        this.log = log;
        path = Path.Combine(dataDir, "cairn-notices.json");
        try
        {
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject o)
            {
                list = Clean(o["notices"] as JsonArray);
                foreach (var n in (o["reported"] as JsonArray) ?? new JsonArray()) if (n is JsonValue v && v.TryGetValue<long>(out var id)) reported.Add(id);
            }
        }
        catch (Exception e) { log?.LogWarning("Cairn: the notices file is unreadable ({Error}) - they come again with the next check", e.GetType().Name); }
    }

    // only what a notice is made of, checked (the Cairn is trusted, but nothing odd is passed on to the members)
    static List<JsonObject> Clean(JsonArray a)
    {
        var res = new List<JsonObject>();
        foreach (var n in a ?? new JsonArray())
        {
            if (n is not JsonObject o || res.Count >= MaxNotices) continue;
            // Cairn 1.6.4: "v" is the signed format's version (missing = 1); a v2 notice's "to" is "*" or this Lodge's public address, so it may
            // be as long as an address (Elans.Shared.PublicUrl.MaxLength). Both are passed on as they came: the Outpost checks them.
            long id = L(o["id"]), created = L(o["created"]), expires = L(o["expires"]), v = L(o["v"]);
            string level = S(o["level"]), to = S(o["to"]) ?? "*", text = S(o["text"]), sig = S(o["sig"]), kid = S(o["kid"]);
            if (id <= 0 || created <= 0 || expires <= created || !Levels.Contains(level) || string.IsNullOrEmpty(text) || text.Length > MaxText
                || string.IsNullOrEmpty(sig) || sig.Length > 200 || string.IsNullOrEmpty(kid) || kid.Length > 40 || to.Length > Elans.Shared.PublicUrl.MaxLength
                || v < 0 || v > 99) continue;
            var c = new JsonObject { ["id"] = id, ["created"] = created, ["expires"] = expires, ["level"] = level, ["to"] = to, ["text"] = text, ["sig"] = sig, ["kid"] = kid };
            if (v > 0) c["v"] = (int)v;   // an int, as CairnNoticeEntry reads it (an in-memory long would not read as an int)
            res.Add(c);
        }
        return res;
    }
    static long L(JsonNode n) => n is JsonValue v && v.TryGetValue<long>(out var x) ? x : 0;
    static string S(JsonNode n) => n is JsonValue v && v.TryGetValue<string>(out var x) ? x : null;

    // the Cairn's current list (an empty list = none any more); true = it changed
    public bool Update(JsonArray fromCairn, long nowS)
    {
        var fresh = Clean(fromCairn).Where(n => L(n["expires"]) > nowS).ToList();
        lock (gate)
        {
            var before = string.Join(",", list.Select(n => n.ToJsonString()));
            var after = string.Join(",", fresh.Select(n => n.ToJsonString()));
            if (before == after) return false;
            foreach (var n in fresh.Where(n => list.All(o => L(o["id"]) != L(n["id"]))))
                log?.LogWarning("Cairn notice #{Id} ({Level}): {Text}", L(n["id"]), S(n["level"]), S(n["text"]));
            list = fresh;
            reported.IntersectWith(list.Select(n => L(n["id"])));
            Save();
        }
        Changed?.Invoke();
        return true;
    }

    public List<JsonObject> Current(long nowS) { lock (gate) return list.Where(n => L(n["expires"]) > nowS).Select(n => (JsonObject)n.DeepClone()).ToList(); }

    public bool AnyCritical(long nowS) => Current(nowS).Any(n => S(n["level"]) == "critical");

    // ids an officer has seen that the Cairn has not been told about yet (and only ones it sent us)
    public List<long> ToReport(IEnumerable<long> seen)
    {
        lock (gate)
        {
            var known = new HashSet<long>(list.Select(n => L(n["id"])));
            return seen.Distinct().Where(id => known.Contains(id) && !reported.Contains(id)).Take(20).ToList();
        }
    }

    public void MarkReported(IEnumerable<long> ids) { lock (gate) { reported.UnionWith(ids); Save(); } }

    void Save()
    {
        try
        {
            var o = new JsonObject
            {
                ["notices"] = new JsonArray(list.Select(n => (JsonNode)n.DeepClone()).ToArray()),
                ["reported"] = new JsonArray(reported.OrderBy(x => x).Select(x => (JsonNode)x).ToArray()),
            };
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, o.ToJsonString());
            File.Move(tmp, path, true);
        }
        catch (Exception e) { log?.LogWarning("Cairn: the notices could not be saved ({Error})", e.GetType().Name); }
    }
}
