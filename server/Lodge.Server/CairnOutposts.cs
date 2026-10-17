using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Lodge;

// "Connected outposts" (PROTOCOL.md "The Cairn"): the Cairn never sees an Outpost, so the Lodge tells it - per ISO week - only two
// NUMBERS: how many of its members share their Atlas with it (s), and how many received the Cairn's dataset through it (r). The weekly report
// rides along with the contribution and only when the Cairn says it understands it (/v1/status "features": ["outposts"]).
// Who was counted is kept only as salted hashes in <data>/cairn-week.json: the salt is random per week and thrown away when the week ends,
// so last week's members can't be recognised again; what stays of a finished week is its two numbers. A purge takes the member out.
public sealed class CairnWeekly
{
    readonly string path;
    readonly IClock clock;
    readonly ILogger log;
    readonly object gate = new();
    string week = "", salt = "";
    readonly HashSet<string> sharing = new(StringComparer.Ordinal), received = new(StringComparer.Ordinal);
    string prevWeek = ""; int prevS, prevR;
    public const int MaxPerWeek = 100_000;

    public CairnWeekly(string dataDir, IClock clock, ILogger log)
    {
        path = Path.Combine(dataDir, "cairn-week.json");
        this.clock = clock; this.log = log;
        try
        {
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject o)
            {
                week = (string)o["week"] ?? ""; salt = (string)o["salt"] ?? "";
                if (o["s"] is JsonArray s) foreach (var h in s) if (h is JsonValue v && v.TryGetValue<string>(out var x)) sharing.Add(x);
                if (o["r"] is JsonArray r) foreach (var h in r) if (h is JsonValue v && v.TryGetValue<string>(out var x)) received.Add(x);
                prevWeek = (string)o["prevWeek"] ?? ""; prevS = (int?)o["prevS"] ?? 0; prevR = (int?)o["prevR"] ?? 0;
            }
        }
        catch (Exception e) { log?.LogWarning("Cairn: the weekly outpost count is unreadable ({Error}) - it starts again", e.GetType().Name); week = ""; }
    }

    public static string WeekOf(DateTime utc) =>
        ISOWeek.GetYear(utc).ToString("0000", CultureInfo.InvariantCulture) + "-W" + ISOWeek.GetWeekOfYear(utc).ToString("00", CultureInfo.InvariantCulture);

    // a new week: last week's numbers stay (only the numbers), the salt and the hashes go
    void Roll()
    {
        var now = WeekOf(clock.UtcNow);
        if (now == week && salt.Length > 0) return;
        if (week.Length > 0 && week == WeekOf(clock.UtcNow.AddDays(-7))) { prevWeek = week; prevS = sharing.Count; prevR = received.Count; }
        else if (prevWeek != WeekOf(clock.UtcNow.AddDays(-7))) { prevWeek = ""; prevS = prevR = 0; }
        week = now;
        salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        sharing.Clear(); received.Clear();
        Save();
    }

    string H(string identity) => Convert.ToHexString(HMACSHA256.HashData(Convert.FromBase64String(salt), Encoding.UTF8.GetBytes(identity ?? ""))).ToLowerInvariant()[..32];

    // a member whose Atlas sharing is on was here this week
    public void NoteSharing(string identity) => Note(identity, false);
    // a member received the Cairn's dataset from this Lodge this week (they share, too: only sharers receive)
    public void NoteReceived(string identity) => Note(identity, true);

    void Note(string identity, bool got)
    {
        if (string.IsNullOrEmpty(identity)) return;
        lock (gate)
        {
            Roll();
            var h = H(identity);
            bool changed = false;
            if (sharing.Count < MaxPerWeek) changed |= sharing.Add(h);
            if (got && received.Count < MaxPerWeek) changed |= received.Add(h);
            if (changed) Save();
        }
    }

    public void Forget(string identity)
    {
        if (string.IsNullOrEmpty(identity)) return;
        lock (gate)
        {
            if (salt.Length == 0) return;
            var h = H(identity);
            if (sharing.Remove(h) | received.Remove(h)) Save();
        }
    }

    // what goes to the Cairn: last week (when it just ended) and this week, numbers only
    public JsonArray Report()
    {
        lock (gate)
        {
            Roll();
            var a = new JsonArray();
            if (prevWeek.Length > 0) a.Add(new JsonObject { ["w"] = prevWeek, ["s"] = prevS, ["r"] = prevR });
            a.Add(new JsonObject { ["w"] = week, ["s"] = sharing.Count, ["r"] = received.Count });
            return a;
        }
    }

    void Save()
    {
        try
        {
            var o = new JsonObject
            {
                ["v"] = 1, ["week"] = week, ["salt"] = salt,
                ["s"] = new JsonArray(sharing.OrderBy(x => x, StringComparer.Ordinal).Select(x => (JsonNode)x).ToArray()),
                ["r"] = new JsonArray(received.OrderBy(x => x, StringComparer.Ordinal).Select(x => (JsonNode)x).ToArray()),
                ["prevWeek"] = prevWeek, ["prevS"] = prevS, ["prevR"] = prevR,
            };
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, o.ToJsonString());
            File.Move(tmp, path, true);
        }
        catch (Exception e) { log?.LogWarning("Cairn: could not save the weekly outpost count ({Error})", e.GetType().Name); }
    }
}
