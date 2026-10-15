using System.Text.RegularExpressions;
using System.Text.Json.Nodes;

namespace Lodge;

// Hunter field notes (2.23, sub-feature "atlas5"). Elan's Hunter Helper notes, per tameable beast, what the game showed: the level, where it stood, how often it was
// seen and when, rare / elite status, the creature family and which abilities it taught when tamed. The Outpost shares a member's CUMULATIVE note per npc:
//   fieldnotes: { "<npcId>": { name?, min?, max?, n, last?, class?, fam?, taught?: [ "bite4", ... ], pts?: { "<uiMapId>": [ { x, y }, ... ] } } }
// (x, y = fractions of the map, 4 decimals). The server keeps one note per member and npc and shows everybody the merge:
//   * level range = lowest min and highest max over members; sightings (n) = the sum of every member's own count; last seen = the newest;
//   * class and family = the majority over members (a tie goes to the more recent member); the name likewise;
//   * taught abilities = a set, each with the number of members who saw it taught;
//   * sighting points of one map within NoteMerge (Euclidean, map fraction) are ONE point, counted by members;
//   * members = how many members have a note on the npc (and, for members and up, up to five Lodge display names, newest first; guests get no names and a day-rounded time).
// Idempotency: a member's own note only ever grows (min down, max up, n up, last up, taught and points added), so a resend never counts twice.
public static partial class Atlas
{
    public const double NoteMerge = 0.01;
    public const int MaxNoteLevel = 80, MaxNoteFamily = 32, MaxNoteTaught = 24;
    public const int MaxNotesPerMember = 600, MaxNotes = 20000, MaxNotesPerShare = 60;
    public const int MaxNoteMapsPerNpc = 10, MaxNotePtsPerNpcPerMember = 24, MaxNotePtsPerMember = 8000, MaxNotePtsPerShare = 600;
    public const int MaxNotePtsPerMapAgg = 20, MaxNotePtsAgg = 40, MaxNoteBy = 5;
    public static readonly string[] NoteClasses = { "normal", "elite", "rare", "rareelite", "worldboss" };

    [GeneratedRegex(@"^[a-z]{1,24}[0-9]{1,2}\z", RegexOptions.CultureInvariant)]
    private static partial Regex NoteTokenRx();

    public sealed class NotePt
    {
        public double X, Y;
        public NotePt Clone() => new() { X = X, Y = Y };
    }

    // one member's note on one npc
    public sealed class FieldNote
    {
        public string Name = "", Class = "", Fam = "";
        public int Min, Max;                       // 0 = no level seen
        public long N, Last;
        public SortedSet<string> Taught = new(StringComparer.Ordinal);
        public Dictionary<int, List<NotePt>> Pts = new();
        public int PtCount => Pts.Values.Sum(l => l.Count);

        public FieldNote Clone()
        {
            var c = new FieldNote { Name = Name, Class = Class, Fam = Fam, Min = Min, Max = Max, N = N, Last = Last, Taught = new SortedSet<string>(Taught, StringComparer.Ordinal) };
            foreach (var (m, l) in Pts) c.Pts[m] = l.Select(p => p.Clone()).ToList();
            return c;
        }

        public bool SameAs(FieldNote o) =>
            o != null && Name == o.Name && Class == o.Class && Fam == o.Fam && Min == o.Min && Max == o.Max && N == o.N && Last == o.Last && Taught.SetEquals(o.Taught)
            && Pts.Count == o.Pts.Count && Pts.All(kv => o.Pts.TryGetValue(kv.Key, out var l) && l.Count == kv.Value.Count && kv.Value.Zip(l).All(z => z.First.X == z.Second.X && z.First.Y == z.Second.Y));
    }

    public static bool SameNotePt(NotePt a, double x, double y) => (a.X - x) * (a.X - x) + (a.Y - y) * (a.Y - y) <= NoteMerge * NoteMerge + 1e-12;

    static bool NoteString(JsonNode n, int max, out string s)
    {
        s = "";
        if (n == null) return true;
        if (!SeenText(n, max, false, false, out var raw)) return false;
        s = CleanName(raw);
        return true;
    }

    // One npc's note; all-or-nothing. `now` bounds `last` (no further than 5 minutes ahead).
    public static bool ParseFieldNote(JsonNode node, long now, out FieldNote note)
    {
        note = null;
        if (node is not JsonObject o || !Whole(o["n"], 0, MaxCount, out var n)) return false;
        var x = new FieldNote { N = n };
        if (!NoteString(o["name"], MaxNameLength, out x.Name) || !NoteString(o["fam"], MaxNoteFamily, out x.Fam)) return false;
        if (o["min"] != null)
        {
            if (!Whole(o["min"], 1, MaxNoteLevel, out var mn)) return false;
            long mx = mn;
            if (o["max"] != null && !Whole(o["max"], mn, MaxNoteLevel, out mx)) return false;
            x.Min = (int)mn; x.Max = (int)mx;
        }
        else if (o["max"] != null) return false;
        if (o["last"] != null)
        {
            if (!Time(o["last"], now, out var at)) return false;
            x.Last = at;
        }
        if (o["class"] != null)
        {
            if (o["class"] is not JsonValue cv || !cv.TryGetValue<string>(out var cls) || Array.IndexOf(NoteClasses, cls) < 0) return false;
            x.Class = cls;
        }
        if (o["taught"] != null)
        {
            if (o["taught"] is not JsonArray ta || ta.Count > MaxNoteTaught) return false;
            foreach (var t in ta)
            {
                if (t is not JsonValue tv || !tv.TryGetValue<string>(out var tok) || !NoteTokenRx().IsMatch(tok)) return false;
                x.Taught.Add(tok);
            }
        }
        if (o["pts"] != null)
        {
            if (o["pts"] is not JsonObject po || po.Count > MaxNoteMapsPerNpc) return false;
            int total = 0;
            foreach (var (mk, arr) in po)
            {
                if (!Key(mk, MaxMapId, out var map) || arr is not JsonArray a || a.Count == 0 || a.Count > MaxNotePtsPerNpcPerMember) return false;
                var l = new List<NotePt>();
                foreach (var e in a)
                {
                    if (e is not JsonObject eo || !Coord4(eo["x"], out var px) || !Coord4(eo["y"], out var py)) return false;
                    if (!l.Any(p => SameNotePt(p, px, py))) l.Add(new NotePt { X = px, Y = py });
                }
                total += l.Count;
                if (total > MaxNotePtsPerNpcPerMember) return false;
                x.Pts[map] = l;
            }
        }
        if (x.N == 0 && x.Min == 0 && x.Taught.Count == 0 && x.Pts.Count == 0) return false;   // nothing to say
        note = x;
        return true;
    }

    // Folds an incoming note into the member's stored one (in place). Only ever grows. `full` = something did not fit (points / taught cap).
    public static bool MergeFieldNote(FieldNote have, FieldNote inc, out bool full)
    {
        full = false;
        var before = have.Clone();
        if (have.Name.Length == 0 && inc.Name.Length > 0) have.Name = inc.Name;
        if (inc.Min > 0)
        {
            if (have.Min == 0 || inc.Min < have.Min) have.Min = inc.Min;
            if (have.Max == 0 || inc.Max > have.Max) have.Max = inc.Max;
        }
        if (inc.N > have.N) have.N = inc.N;
        if (inc.Last > have.Last)
        {
            have.Last = inc.Last;
            if (inc.Class.Length > 0) have.Class = inc.Class;
            if (inc.Fam.Length > 0) have.Fam = inc.Fam;
        }
        else
        {
            if (have.Class.Length == 0) have.Class = inc.Class;
            if (have.Fam.Length == 0) have.Fam = inc.Fam;
        }
        foreach (var t in inc.Taught) { if (have.Taught.Count >= MaxNoteTaught && !have.Taught.Contains(t)) { full = true; continue; } have.Taught.Add(t); }
        foreach (var (map, pts) in inc.Pts)
        {
            if (!have.Pts.TryGetValue(map, out var l))
            {
                if (have.Pts.Count >= MaxNoteMapsPerNpc) { full = true; continue; }
                l = new List<NotePt>();
            }
            foreach (var p in pts)
            {
                if (l.Any(h => SameNotePt(h, p.X, p.Y))) continue;
                if (have.PtCount >= MaxNotePtsPerNpcPerMember) { full = true; continue; }
                l.Add(p.Clone());
                have.Pts[map] = l;
            }
        }
        return !have.SameAs(before);
    }

    // what all members' notes on one npc add up to
    public sealed class NoteSum
    {
        public string Name = "", Class = "", Fam = "";
        public int Min, Max, Members;
        public long N, Last;
        public Dictionary<string, int> Taught = new(StringComparer.Ordinal);
        public Dictionary<int, List<(double X, double Y, int M)>> Pts = new();
        public List<(string Who, long Last)> Who = new();   // newest first, then by login
    }

    static string Majority(IEnumerable<(string who, long last, string v)> votes)
    {
        var best = votes.Where(v => v.v.Length > 0).GroupBy(v => v.v, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count()).ThenByDescending(g => g.Max(v => v.last)).ThenBy(g => g.Key, StringComparer.Ordinal).FirstOrDefault();
        return best?.Key ?? "";
    }

    // Deterministic. A member's note is one vote for its class / family / name.
    public static NoteSum AggregateNotes(IEnumerable<(string who, FieldNote note)> notes)
    {
        var list = notes.OrderBy(n => n.who, StringComparer.Ordinal).ToList();
        var s = new NoteSum { Members = list.Count };
        foreach (var (who, n) in list)
        {
            s.N = Math.Min(MaxCount * MaxNotes, s.N + n.N);
            if (n.Min > 0) { s.Min = s.Min == 0 ? n.Min : Math.Min(s.Min, n.Min); s.Max = Math.Max(s.Max, n.Max); }
            s.Last = Math.Max(s.Last, n.Last);
            foreach (var t in n.Taught) s.Taught[t] = s.Taught.TryGetValue(t, out var c) ? c + 1 : 1;
        }
        s.Name = Majority(list.Select(l => (l.who, l.note.Last, l.note.Name)));
        s.Class = Majority(list.Select(l => (l.who, l.note.Last, l.note.Class)));
        s.Fam = Majority(list.Select(l => (l.who, l.note.Last, l.note.Fam)));
        s.Who = list.Select(l => (l.who, l.note.Last)).OrderByDescending(w => w.Last).ThenBy(w => w.who, StringComparer.Ordinal).ToList();
        foreach (var map in list.SelectMany(l => l.note.Pts.Keys).Distinct().OrderBy(m => m))
        {
            var seeds = new List<(double X, double Y)>(); var holders = new List<HashSet<string>>();
            foreach (var (who, n) in list)
            {
                if (!n.Pts.TryGetValue(map, out var pts)) continue;
                foreach (var p in pts.OrderBy(p => p.X).ThenBy(p => p.Y))
                {
                    int idx = seeds.FindIndex(sd => (sd.X - p.X) * (sd.X - p.X) + (sd.Y - p.Y) * (sd.Y - p.Y) <= NoteMerge * NoteMerge + 1e-12);
                    if (idx < 0) { seeds.Add((p.X, p.Y)); holders.Add(new HashSet<string>(StringComparer.Ordinal)); idx = seeds.Count - 1; }
                    holders[idx].Add(who);
                }
            }
            var l2 = seeds.Select((sd, i) => (sd.X, sd.Y, M: holders[i].Count)).OrderByDescending(p => p.M).ThenBy(p => p.X).ThenBy(p => p.Y).Take(MaxNotePtsPerMapAgg).ToList();
            if (l2.Count > 0) s.Pts[map] = l2;
        }
        // the whole npc keeps at most MaxNotePtsAgg points: maps in order, the best supported points of each first
        int budget = MaxNotePtsAgg;
        foreach (var map in s.Pts.Keys.OrderBy(m => m).ToList())
        {
            var l = s.Pts[map];
            if (budget <= 0) { s.Pts.Remove(map); continue; }
            if (l.Count > budget) s.Pts[map] = l.Take(budget).ToList();
            budget -= s.Pts[map].Count;
        }
        return s;
    }
}
