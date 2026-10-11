using System.Globalization;
using System.Text.Json.Nodes;

namespace Lodge;

// "Looking for group" sign-up cards (2.9). A card is an ordinary chat message with kind = "lfg", a structured
// payload in "lfg" and a plain-text fallback in "text" (what older clients show, and what history/search/pins use).
//
//   lfg = { title, start (ms since epoch; the card's creation time for "now"), now (bool), note, status: open|closed|cancelled,
//           by, slots: { tank, healer, dps }, signups: [ { n: name, r: tank|healer|dps|maybe } ] }
//
// All rules live here as pure functions over the JSON, so the chat module only has to hold the store lock around them:
// one entry per person, role limits, closed/cancelled cards can't change, every field bounded.
public static class Lfg
{
    public const string Kind = "lfg";
    public const int MaxRole = 40;       // slots per role
    public const int MaxTitle = 60;
    public const int MaxNote = 200;
    public const int MaxMaybe = 40;
    public const int MaxOpenPerAuthor = 3;
    public static readonly string[] RoleIds = { "tank", "healer", "dps" };
    static readonly TimeSpan PastGrace = TimeSpan.FromMinutes(10);
    static readonly TimeSpan MaxAhead = TimeSpan.FromDays(30);

    public static bool Is(JsonObject msg) => msg != null && (string)msg["kind"] == Kind && msg["lfg"] is JsonObject;
    public static string Status(JsonObject lfg) => J.Str(lfg, "status") ?? "open";
    public static bool IsOpen(JsonObject lfg) => Status(lfg) == "open";

    // validates the request and builds the stored payload; null + error text when anything is off
    public static JsonObject Create(JsonObject req, string author, long nowMs, bool allowGroupPings, out string error)
    {
        error = null;
        if (req == null) { error = "That sign-up card is incomplete"; return null; }
        var title = Names.Text(J.Str(req, "title") ?? "", MaxTitle);
        if (title.Length == 0) { error = "Give the group a title or an instance"; return null; }
        var note = Names.Text(J.Str(req, "note") ?? "", MaxNote);
        if (!allowGroupPings) { title = Names.DefangGroupPings(title); note = Names.DefangGroupPings(note); }

        var slots = new JsonObject();
        int total = 0;
        var src = req["slots"] as JsonObject ?? req;
        foreach (var role in RoleIds)
        {
            if (!TryCount(src[role], out var n)) { error = $"The {role} count must be a whole number from 0 to {MaxRole}"; return null; }
            slots[role] = n; total += n;
        }
        if (total == 0) { error = "A group needs at least one open slot"; return null; }

        long start = nowMs;
        bool now = J.Bool(req, "now") || !(req["start"] is JsonValue sv && sv.TryGetValue<long>(out var s0) && s0 > 0);
        if (!now)
        {
            start = req["start"]!.GetValue<long>();
            if (start < nowMs - (long)PastGrace.TotalMilliseconds) { error = "That start time has already passed"; return null; }
            if (start > nowMs + (long)MaxAhead.TotalMilliseconds) { error = "Pick a start time within the next 30 days"; return null; }
        }
        return new JsonObject
        {
            ["title"] = title, ["start"] = start, ["now"] = now, ["note"] = note, ["status"] = "open", ["by"] = author,
            ["slots"] = slots, ["signups"] = new JsonArray(),
        };
    }

    static bool TryCount(JsonNode node, out int n)
    {
        n = 0;
        if (node == null) return true; // missing = 0
        if (node is not JsonValue v) return false;
        if (v.TryGetValue<int>(out var i)) { n = i; return i >= 0 && i <= MaxRole; }
        if (v.TryGetValue<double>(out var d) && d == Math.Floor(d) && d >= 0 && d <= MaxRole) { n = (int)d; return true; }
        return false;
    }

    public static int SlotsOf(JsonObject lfg, string role) => lfg["slots"] is JsonObject s ? J.Int(s, role) : 0;
    public static int Filled(JsonObject lfg, string role) => lfg["signups"] is JsonArray a ? a.Count(x => (string)x?["r"] == role) : 0;

    static JsonObject Entry(JsonObject lfg, string name) =>
        lfg["signups"] is JsonArray a ? a.OfType<JsonObject>().FirstOrDefault(x => Names.Same((string)x["n"], name)) : null;

    public enum Result { Changed, Unchanged, Closed, Full, Denied }

    // role: tank | healer | dps | maybe. One entry per person: signing up again moves them (atomically - a refused
    // move leaves the old place alone).
    public static Result Join(JsonObject lfg, string name, string role)
    {
        if (!IsOpen(lfg)) return Result.Closed;
        role = (role ?? "").ToLowerInvariant();
        if (role != "maybe" && Array.IndexOf(RoleIds, role) < 0) return Result.Denied;
        var arr = lfg["signups"] as JsonArray ?? (JsonArray)(lfg["signups"] = new JsonArray());
        var mine = Entry(lfg, name);
        if (mine != null && (string)mine["r"] == role) return Result.Unchanged;
        if (role == "maybe") { if (Filled(lfg, "maybe") >= MaxMaybe) return Result.Full; }
        else if (Filled(lfg, role) >= SlotsOf(lfg, role)) return Result.Full;
        if (mine != null) mine["r"] = role;
        else arr.Add(new JsonObject { ["n"] = name, ["r"] = role });
        return Result.Changed;
    }

    public static Result Leave(JsonObject lfg, string name)
    {
        if (!IsOpen(lfg)) return Result.Closed;
        var mine = Entry(lfg, name);
        if (mine == null) return Result.Unchanged;
        ((JsonArray)lfg["signups"]).Remove(mine);
        return Result.Changed;
    }

    public static Result SetStatus(JsonObject lfg, string status)
    {
        if (!IsOpen(lfg)) return Result.Closed;
        lfg["status"] = status;
        return Result.Changed;
    }

    // the line older clients (and history, search, pins, reply quotes) show
    public static string Fallback(JsonObject lfg)
    {
        var sb = new System.Text.StringBuilder("[Looking for group] ");
        sb.Append(J.Str(lfg, "title"));
        var status = Status(lfg);
        if (status != "open") sb.Append(status == "cancelled" ? " (cancelled)" : " (closed)");
        sb.Append(" - ");
        if (J.Bool(lfg, "now")) sb.Append("now");
        else sb.Append(DateTimeOffset.FromUnixTimeMilliseconds(lfg["start"]!.GetValue<long>()).UtcDateTime.ToString("ddd d MMM HH:mm 'UTC'", CultureInfo.InvariantCulture));
        sb.Append(" - ");
        bool first = true;
        foreach (var (role, label) in new[] { ("tank", "Tank"), ("healer", "Healer"), ("dps", "DPS") })
        {
            if (SlotsOf(lfg, role) == 0) continue;
            if (!first) sb.Append(", ");
            sb.Append(label).Append(' ').Append(Filled(lfg, role)).Append('/').Append(SlotsOf(lfg, role));
            first = false;
        }
        var maybe = Filled(lfg, "maybe");
        if (maybe > 0) sb.Append(", ").Append(maybe).Append(" maybe");
        var note = J.Str(lfg, "note");
        if (!string.IsNullOrEmpty(note)) sb.Append(" - ").Append(note);
        if (status == "open") sb.Append(" (sign up in Elan's Outpost)");
        return sb.ToString();
    }
}
