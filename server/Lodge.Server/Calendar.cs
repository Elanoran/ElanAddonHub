using System.Globalization;
using System.Text.Json.Nodes;

namespace Lodge;

// Raid / event calendar (2.11). Events are small JSON objects kept by CalendarStore; every rule lives here as pure
// functions over that JSON (like Lfg), so the module only has to hold the store lock around them.
//
//   event = { id, series?, title, start (ms, UTC), end (ms, 0 = none), note, status: open|cancelled, by,
//             roles (bool: false = a social event with no role slots), slots: { tank, healer, dps },
//             signups: [ { n: name, s: going|maybe|declined, r: tank|healer|dps|"", c: comment } ],
//             channel?, msg? (the chat card), tz, off (creator's zone id and its UTC offset in minutes at creation),
//             rep?: { weeks (0 = until cancelled), open (bool) } }
//
// Time zones: instants are stored in UTC. A weekly series is expanded in the CREATOR's time zone: occurrence i is the first
// occurrence's local date + 7*i days at the same local wall-clock time, converted back to UTC. So a 20:00 raid stays at 20:00
// local across a DST change (the UTC instant moves by an hour); each viewer then sees it in their own zone.
public static class Cal
{
    public const string Kind = "event";
    public const int MaxTitle = 60;
    public const int MaxNote = 200;
    public const int MaxComment = 80;
    public const int MaxRole = 40;
    public const int MaxSignups = 60;
    public const int MaxFuture = 200;           // future events kept in total
    public const int MaxWeeks = 26;             // recurrence bound
    public const int MaxOpenPerAuthor = 40;     // future, not cancelled
    public const int PruneAfterDays = 30;       // past events are dropped this long after their end
    public const int WelcomeDays = 30;
    public const int MaxRangeDays = 62;
    public static readonly string[] RoleIds = { "tank", "healer", "dps" };
    public static readonly string[] Statuses = { "going", "maybe", "declined" };
    static readonly long PastGraceMs = 10 * 60_000L;
    static readonly long MaxAheadMs = 365L * 86_400_000;
    static readonly long MaxDurationMs = 24L * 3_600_000;
    static readonly long DefaultLengthMs = 3L * 3_600_000; // how long an event without an end counts as "running"

    public static bool IsCancelled(JsonObject e) => J.Str(e, "status") == "cancelled";
    public static long Start(JsonObject e) => e["start"] is JsonValue v && v.TryGetValue<long>(out var l) ? l : 0;
    public static long End(JsonObject e) => e["end"] is JsonValue v && v.TryGetValue<long>(out var l) ? l : 0;
    // when sign-ups stop: its end, or start + 3 h
    public static long Over(JsonObject e) => End(e) > 0 ? End(e) : Start(e) + DefaultLengthMs;
    // pruning looks at the end only (an event without one ends when it starts)
    public static long Finish(JsonObject e) => End(e) > 0 ? End(e) : Start(e);

    // ---- time zones

    // The creator's zone: IANA ("Europe/Berlin") or Windows ("W. Europe Standard Time") id, whichever the server's OS knows
    // (ICU converts between the two); unknown ids fall back to the fixed UTC offset the client sent (no DST handling), and
    // when that is missing too the zone is UTC.
    public static TimeZoneInfo ResolveTz(string id, int offsetMinutes)
    {
        if (!string.IsNullOrEmpty(id))
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch { }
            try { if (TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var iana)) return TimeZoneInfo.FindSystemTimeZoneById(iana); } catch { }
            try { if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var win)) return TimeZoneInfo.FindSystemTimeZoneById(win); } catch { }
        }
        if (offsetMinutes != 0 && Math.Abs(offsetMinutes) <= 14 * 60)
            return TimeZoneInfo.CreateCustomTimeZone("fixed", TimeSpan.FromMinutes(offsetMinutes), "fixed", "fixed");
        return TimeZoneInfo.Utc;
    }

    public static DateTime ToLocal(long utcMs, TimeZoneInfo tz) =>
        TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(utcMs), tz).DateTime;

    // local wall clock -> UTC ms. A time that does not exist (spring-forward gap) moves forward by an hour; an ambiguous one
    // (autumn overlap) takes its first occurrence.
    public static long FromLocal(DateTime local, TimeZoneInfo tz)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        TimeSpan off;
        if (tz.IsInvalidTime(local)) { local = local.AddHours(1); off = tz.GetUtcOffset(local); }
        else if (tz.IsAmbiguousTime(local)) off = tz.GetAmbiguousTimeOffsets(local).Max();
        else off = tz.GetUtcOffset(local);
        return new DateTimeOffset(local, off).ToUnixTimeMilliseconds();
    }

    // the first start plus `count - 1` more, one local week apart at the same wall-clock time
    public static List<long> Occurrences(long firstStart, TimeZoneInfo tz, int count)
    {
        var first = ToLocal(firstStart, tz);
        var list = new List<long> { firstStart };
        for (int i = 1; i < count; i++) list.Add(FromLocal(first.AddDays(7 * i), tz));
        return list;
    }

    // ---- validation

    public sealed class Spec
    {
        public string Title, Note, Tz, Channel;
        public long Start, Duration;      // Duration in ms, 0 = none
        public bool Roles = true, Post = true;
        public int Tank, Healer, Dps;
        public int Off;
        public int Weeks;                  // total occurrences incl. the first (1 = no repeat)
        public bool OpenEnded;             // repeat until cancelled
        public int Slots(string role) => role == "tank" ? Tank : role == "healer" ? Healer : Dps;
    }

    // `edit`: an existing event is being changed (start may be in the past, repeat is ignored)
    public static Spec Parse(JsonObject req, long nowMs, bool allowGroupPings, bool edit, out string error)
    {
        error = null;
        if (req == null) { error = "That event is incomplete"; return null; }
        var s = new Spec();
        s.Title = Names.Text(J.Str(req, "title") ?? "", MaxTitle);
        if (s.Title.Length == 0) { error = "Give the event a title or an instance"; return null; }
        s.Note = Names.Text(J.Str(req, "note") ?? "", MaxNote);
        if (!allowGroupPings) { s.Title = Names.DefangGroupPings(s.Title); s.Note = Names.DefangGroupPings(s.Note); }

        if (!(req["start"] is JsonValue sv && sv.TryGetValue<long>(out var start) && start > 0)) { error = "Pick a start date and time"; return null; }
        s.Start = start;
        if (!edit)
        {
            if (start < nowMs - PastGraceMs) { error = "That start time has already passed"; return null; }
            if (start > nowMs + MaxAheadMs) { error = "Pick a start within the next year"; return null; }
        }
        else if (start > nowMs + MaxAheadMs) { error = "Pick a start within the next year"; return null; }

        // length: "durationMin" (15 min to 24 h) or an "end" instant
        if (req["durationMin"] is JsonValue dv && dv.TryGetValue<long>(out var dm))
        {
            if (dm < 0 || dm > 1440 || (dm > 0 && dm < 15)) { error = "An event lasts from 15 minutes to 24 hours"; return null; }
            s.Duration = dm * 60_000;
        }
        else if (req["end"] is JsonValue ev && ev.TryGetValue<long>(out var end) && end > 0)
        {
            if (end <= start || end - start > MaxDurationMs) { error = "The end must be after the start, within 24 hours"; return null; }
            s.Duration = end - start;
        }

        s.Roles = J.BoolOrNull(req, "roles") ?? true;
        if (s.Roles)
        {
            var src = req["slots"] as JsonObject ?? req;
            foreach (var role in RoleIds)
            {
                if (!TryCount(src[role], out var n)) { error = $"The {role} count must be a whole number from 0 to {MaxRole}"; return null; }
                if (role == "tank") s.Tank = n; else if (role == "healer") s.Healer = n; else s.Dps = n;
            }
            if (s.Tank + s.Healer + s.Dps == 0) { error = "An event with roles needs at least one open slot (or choose 'No roles')"; return null; }
        }

        s.Tz = Names.Text(J.Str(req, "tz") ?? "", 64);
        foreach (var c in s.Tz) if (!(char.IsLetterOrDigit(c) || " _/+-.()'".Contains(c))) { s.Tz = ""; break; }
        s.Off = Math.Clamp(J.Int(req, "off"), -14 * 60, 14 * 60);

        s.Weeks = 1;
        if (!edit && req["repeat"] is JsonObject rep)
        {
            if (J.Bool(rep, "open")) { s.OpenEnded = true; s.Weeks = MaxWeeks; }
            else
            {
                int w = J.Int(rep, "weeks", 1);
                if (w < 1 || w > MaxWeeks) { error = $"A weekly event can repeat for 2 to {MaxWeeks} weeks"; return null; }
                s.Weeks = w;
            }
        }
        s.Post = J.BoolOrNull(req, "post") ?? true;
        s.Channel = J.Str(req, "channel");
        return s;
    }

    static bool TryCount(JsonNode node, out int n)
    {
        n = 0;
        if (node == null) return true;
        if (node is not JsonValue v) return false;
        if (v.TryGetValue<int>(out var i)) { n = i; return i >= 0 && i <= MaxRole; }
        if (v.TryGetValue<double>(out var d) && d == Math.Floor(d) && d >= 0 && d <= MaxRole) { n = (int)d; return true; }
        return false;
    }

    // the events a create request makes: one, or one per week (all sharing a series id)
    public static List<JsonObject> Build(Spec s, string author, long nowMs, Func<string> newId)
    {
        var tz = ResolveTz(s.Tz, s.Off);
        var starts = s.Weeks > 1 ? Occurrences(s.Start, tz, s.Weeks) : new List<long> { s.Start };
        var series = s.Weeks > 1 ? newId() : null;
        var list = new List<JsonObject>();
        foreach (var st in starts)
        {
            var e = new JsonObject
            {
                ["id"] = newId(), ["title"] = s.Title, ["start"] = st, ["end"] = s.Duration > 0 ? st + s.Duration : 0L,
                ["note"] = s.Note, ["status"] = "open", ["by"] = author, ["roles"] = s.Roles,
                ["slots"] = new JsonObject { ["tank"] = s.Tank, ["healer"] = s.Healer, ["dps"] = s.Dps },
                ["signups"] = new JsonArray(), ["tz"] = s.Tz, ["off"] = s.Off, ["created"] = nowMs,
            };
            if (series != null) { e["series"] = series; e["rep"] = new JsonObject { ["weeks"] = s.OpenEnded ? 0 : s.Weeks, ["open"] = s.OpenEnded }; }
            list.Add(e);
        }
        return list;
    }

    // the next occurrence after `last` for an open-ended series (a copy of the template with no sign-ups)
    public static JsonObject Next(JsonObject last, Func<string> newId)
    {
        var tz = ResolveTz(J.Str(last, "tz"), J.Int(last, "off"));
        var st = FromLocal(ToLocal(Start(last), tz).AddDays(7), tz);
        var e = (JsonObject)last.DeepClone();
        e["id"] = newId(); e["start"] = st; e["end"] = End(last) > 0 ? st + (End(last) - Start(last)) : 0L;
        e["status"] = "open"; e["signups"] = new JsonArray(); e.Remove("msg"); e.Remove("channel");
        return e;
    }

    // ---- sign-ups

    public static int SlotsOf(JsonObject e, string role) => e["slots"] is JsonObject s ? J.Int(s, role) : 0;
    public static int Going(JsonObject e, string role) => Entries(e).Count(x => J.Str(x, "s") == "going" && J.Str(x, "r") == role);
    public static int Count(JsonObject e, string status) => Entries(e).Count(x => J.Str(x, "s") == status);
    public static IEnumerable<JsonObject> Entries(JsonObject e) => e["signups"] is JsonArray a ? a.OfType<JsonObject>() : Enumerable.Empty<JsonObject>();
    public static JsonObject Entry(JsonObject e, string name) => Entries(e).FirstOrDefault(x => Names.Same(J.Str(x, "n"), name));

    public enum Result { Changed, Unchanged, Closed, Full, Denied }

    // One entry per person. going takes a place in the role (when the event has roles; a full role refuses and leaves the old
    // entry alone); maybe may name a preferred role without taking a place; declined has none.
    public static Result SignUp(JsonObject e, string name, string status, string role, string comment, long nowMs, bool allowGroupPings)
    {
        if (IsCancelled(e) || Over(e) < nowMs) return Result.Closed;
        status = (status ?? "").ToLowerInvariant();
        if (Array.IndexOf(Statuses, status) < 0) return Result.Denied;
        role = (role ?? "").ToLowerInvariant();
        bool roles = J.BoolOrNull(e, "roles") ?? true;
        if (!roles || status == "declined") role = "";
        else if (role.Length > 0 && Array.IndexOf(RoleIds, role) < 0) return Result.Denied;
        if (roles && status == "going" && role.Length == 0) return Result.Denied;
        comment = Names.Text(comment ?? "", MaxComment);
        if (!allowGroupPings) comment = Names.DefangGroupPings(comment);

        var arr = e["signups"] as JsonArray ?? (JsonArray)(e["signups"] = new JsonArray());
        var mine = Entry(e, name);
        if (mine != null && J.Str(mine, "s") == status && (J.Str(mine, "r") ?? "") == role && (J.Str(mine, "c") ?? "") == comment) return Result.Unchanged;
        if (status == "going" && roles)
        {
            int taken = Entries(e).Count(x => J.Str(x, "s") == "going" && J.Str(x, "r") == role && !ReferenceEquals(x, mine));
            if (taken >= SlotsOf(e, role)) return Result.Full;
        }
        if (mine == null && arr.Count >= MaxSignups) return Result.Full;
        if (mine == null) { mine = new JsonObject { ["n"] = name }; arr.Add(mine); }
        mine["s"] = status; mine["r"] = role; mine["c"] = comment;
        return Result.Changed;
    }

    public static Result Leave(JsonObject e, string name)
    {
        if (IsCancelled(e)) return Result.Closed;
        var mine = Entry(e, name);
        if (mine == null) return Result.Unchanged;
        ((JsonArray)e["signups"]).Remove(mine);
        return Result.Changed;
    }

    // an edit may not push a role count below the people who are going in it
    public static string CheckSlotsHold(JsonObject e, Spec s)
    {
        if (!s.Roles) return null;
        foreach (var role in RoleIds)
        {
            int going = Going(e, role);
            if (going > s.Slots(role)) return $"{going} already signed up as {role} - the {role} count can't go below that";
        }
        return null;
    }

    // applies edited fields; `start` replaces the start only when `timeToo`
    public static void Apply(JsonObject e, Spec s, long start, bool timeToo)
    {
        e["title"] = s.Title; e["note"] = s.Note; e["roles"] = s.Roles;
        e["slots"] = new JsonObject { ["tank"] = s.Tank, ["healer"] = s.Healer, ["dps"] = s.Dps };
        if (timeToo) e["start"] = start;
        e["end"] = s.Duration > 0 ? Start(e) + s.Duration : 0L;
        if (!string.IsNullOrEmpty(s.Tz)) { e["tz"] = s.Tz; e["off"] = s.Off; }
        // roles switched off: places no longer mean anything; switched on: "going" without a role becomes "maybe"
        foreach (var x in Entries(e).ToList())
        {
            if (!s.Roles) x["r"] = "";
            else if (J.Str(x, "s") == "going" && string.IsNullOrEmpty(J.Str(x, "r"))) x["s"] = "maybe";
        }
    }

    // ---- the line older clients (and history, search, pins, reply quotes) show for the chat card

    public static string Fallback(JsonObject e)
    {
        var sb = new System.Text.StringBuilder("[Event] ");
        sb.Append(J.Str(e, "title")).Append(IsCancelled(e) ? " (cancelled)" : "").Append(" - ");
        sb.Append(DateTimeOffset.FromUnixTimeMilliseconds(Start(e)).UtcDateTime.ToString("ddd d MMM HH:mm 'UTC'", CultureInfo.InvariantCulture));
        sb.Append(" - ");
        if (J.BoolOrNull(e, "roles") ?? true)
        {
            bool first = true;
            foreach (var (role, label) in new[] { ("tank", "Tank"), ("healer", "Healer"), ("dps", "DPS") })
            {
                if (SlotsOf(e, role) == 0) continue;
                if (!first) sb.Append(", ");
                sb.Append(label).Append(' ').Append(Going(e, role)).Append('/').Append(SlotsOf(e, role));
                first = false;
            }
        }
        else sb.Append(Count(e, "going")).Append(" going");
        var maybe = Count(e, "maybe");
        if (maybe > 0) sb.Append(", ").Append(maybe).Append(" maybe");
        if (e["rep"] is JsonObject) sb.Append(" - weekly");
        var note = J.Str(e, "note");
        if (!string.IsNullOrEmpty(note)) sb.Append(" - ").Append(note);
        if (!IsCancelled(e)) sb.Append(" (open the Calendar in Elan's Outpost to sign up)");
        return sb.ToString();
    }
}
