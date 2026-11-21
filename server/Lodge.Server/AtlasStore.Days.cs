using System.Globalization;
using System.Text.Json.Nodes;

namespace Lodge;

// 2.40: what each source gave per day. Counters carry no times (cumulative totals), but the Lodge sees each counter grow per share, and
// a share follows the loot within minutes to hours (the Toolkit publishes at login, reload and ~10 s after a change). So the growth is
// added to the share's day (UTC, days since 1970): days[kind][source][day] = { n = looted kills / opens / casts, i = { item: times dropped } }.
// Its use: "this item drops only during a holiday" needs the days the source was looted WITHOUT the item, not only the days it dropped.
// A source the member shares for the first time with more than MaxFirstDayKills is not counted (a backlog from before the Lodge, of
// unknown days). Per member, so the member forget takes it along. Not sent to the Outposts yet (the item records come next).
public sealed partial class AtlasStore
{
    public sealed class DayTally { public long N; public Dictionary<int, long> I = new(); }

    public const int MaxDaysPerSource = 64, MaxItemsPerDay = 40, MaxDayEntriesPerMember = 50_000, MaxFirstDayKills = 5;

    static int DayOf(long unixSec) => (int)(unixSec / 86_400);

    void NoteDay(MemberData md, int k, int id, Atlas.Ctr old, Atlas.Ctr merged)
    {
        if (old == null && merged.N > MaxFirstDayKills) return;
        long dn = merged.N - (old?.N ?? 0);
        var di = new List<(int Item, long N)>();
        foreach (var (item, v) in merged.I)
        {
            long before = old != null && old.I.TryGetValue(item, out var ov) ? ov[0] : 0;
            if (v[0] > before) di.Add((item, v[0] - before));
        }
        if (dn <= 0 && di.Count == 0) return;
        if (!md.Days[k].TryGetValue(id, out var days)) md.Days[k][id] = days = new SortedDictionary<int, DayTally>();
        int day = DayOf(NowSec);
        if (!days.TryGetValue(day, out var t))
        {
            if (md.DayEntries >= MaxDayEntriesPerMember) return;
            days[day] = t = new DayTally(); md.DayEntries++;
            while (days.Count > MaxDaysPerSource) { days.Remove(days.Keys.First()); md.DayEntries--; }
        }
        t.N += Math.Max(0, dn);
        foreach (var (item, n) in di.OrderByDescending(x => x.N))
        {
            if (t.I.TryGetValue(item, out var have)) t.I[item] = have + n;
            else if (t.I.Count < MaxItemsPerDay) t.I[item] = n;
        }
    }

    // for tests and the item records: one member's tally of a source on a day (null = none)
    public DayTally DayOfSource(string identity, int kind, int id, int day)
    {
        lock (Gate)
            return members.TryGetValue(identity, out var md) && md.Days[kind].TryGetValue(id, out var days) && days.TryGetValue(day, out var t) ? t : null;
    }
    public int DayEntryCount { get { lock (Gate) return members.Values.Sum(m => m.DayEntries); } }

    static JsonObject DaysStoreJson(MemberData md)
    {
        var o = new JsonObject();
        for (int k = 0; k < KindCount; k++)
        {
            if (md.Days[k].Count == 0) continue;
            var ko = new JsonObject();
            foreach (var (id, days) in md.Days[k])
            {
                var so = new JsonObject();
                foreach (var (day, t) in days)
                {
                    var io = new JsonObject();
                    foreach (var (item, n) in t.I) io[item.ToString(CultureInfo.InvariantCulture)] = n;
                    so[day.ToString(CultureInfo.InvariantCulture)] = new JsonArray(t.N, io);
                }
                ko[id.ToString(CultureInfo.InvariantCulture)] = so;
            }
            o[KindName(k)] = ko;
        }
        return o;
    }

    void LoadDays(MemberData md, JsonObject o, long now)
    {
        int today = DayOf(now) + 1;
        for (int k = 0; k < KindCount; k++)
        {
            if (o[KindName(k)] is not JsonObject ko) continue;
            foreach (var (key, node) in ko)
            {
                if (!Atlas.Key(key, KeyMax(k), out var id) || node is not JsonObject so) continue;
                var days = new SortedDictionary<int, DayTally>();
                foreach (var (dk, dv) in so)
                {
                    if (md.DayEntries >= MaxDayEntriesPerMember) break;
                    if (!int.TryParse(dk, NumberStyles.None, CultureInfo.InvariantCulture, out var day) || day < 1 || day > today) continue;
                    if (dv is not JsonArray a || a.Count != 2 || !Atlas.Whole(a[0], 0, Atlas.MaxMemberTotal, out var n) || a[1] is not JsonObject io) continue;
                    var t = new DayTally { N = n };
                    foreach (var (ik, iv) in io)
                        if (t.I.Count < MaxItemsPerDay && Atlas.Key(ik, Atlas.MaxItemId, out var item) && Atlas.Whole(iv, 1, Atlas.MaxMemberTotal, out var c)) t.I[item] = c;
                    days[day] = t; md.DayEntries++;
                }
                while (days.Count > MaxDaysPerSource) { days.Remove(days.Keys.First()); md.DayEntries--; }
                if (days.Count > 0) md.Days[k][id] = days;
            }
        }
    }
}
