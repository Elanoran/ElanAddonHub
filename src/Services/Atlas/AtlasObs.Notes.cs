using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ElansAddonHub.Services.Atlas;

namespace ElansAddonHub.Services
{
    // Hunter field notes (Hunter Helper 1.26.11, Hub account module `hunter.fieldnotes` v1; Lodge server 2.23, feature "atlas5"). Hunter Helper notes, per tameable beast,
    // what the game showed: the level, where it stood (percent of the map), how often and when it was seen, rare / elite status, creature family and which abilities
    // it taught when tamed. This file reads the module with the same rules as the server (levels 1..80, classes from a list, ability tokens, coordinates 0..1,
    // lists capped; a bad entry is dropped and counted), builds the `fieldnotes` section of the atlas.share batch (a member's CUMULATIVE note per npc; no character names,
    // the module's own first / hist / dead / data values stay home) and parses the Lodge's merge (`atlas.data` fieldnotes) with the same checks again.
    public static partial class AtlasObs
    {
        public const string NotesModuleName = "hunter.fieldnotes";
        public const int NotesModuleVersion = 1;
        public static readonly Version NotesServerVersion = new Version(2, 23, 0);   // sub-feature "atlas5": hunter field notes
        public const int MaxNotes = 600, MaxNoteLevel = 80, MaxNoteFamily = 32, MaxNoteTaught = 24, MaxNoteMaps = 10, MaxNotePtsPerNpc = 24;
        public const int MaxNotesPerShare = 60, MaxNotePointsPerShare = 600, MaxNoteBy = 5, MaxLodgeNotes = 20000;
        public const double NoteMerge = 0.01;
        public static readonly string[] NoteClasses = { "normal", "elite", "rare", "rareelite", "worldboss" };
        static readonly Regex NoteToken = new Regex(@"^[a-z]{1,24}[0-9]{1,2}$", RegexOptions.CultureInvariant);

        public struct NotePoint { public double X, Y; public int M; }

        // one npc's note: a member's own (Members 0, Taught counts 1 each) or the Lodge's merge (Members = how many, Taught = ability -> members who saw it taught)
        public class FieldNote
        {
            public int Id, Min, Max, Members;                                  // Min / Max 0 = no level seen
            public long N, Last;                                               // sightings, newest sighting (unix s)
            public string Name = "", Class = "", Fam = "";
            public Dictionary<string, int> Taught = new Dictionary<string, int>(StringComparer.Ordinal);
            public Dictionary<int, List<NotePoint>> Pts = new Dictionary<int, List<NotePoint>>();   // uiMapId -> points (map fractions)
            public List<string> By = new List<string>();                       // (lodge) Lodge display names, newest first; empty for guests
            public bool IsRare => Class == "rare" || Class == "rareelite";
            public bool HasLevel => Min > 0;
            public int PointCount => Pts.Values.Sum(l => l.Count);
        }

        static bool NoteEmpty(FieldNote n) => n.N == 0 && n.Min == 0 && n.Taught.Count == 0 && n.Pts.Count == 0;

        // ---------------------------------------------------------------- reading the Hub module

        // The `hunter.fieldnotes` v1 account module as validated notes; null when absent, too new or another version. An empty list is a real answer: the hunter wiped their notes.
        public static Dictionary<int, FieldNote> NotesOf(HubModel model)
        {
            if (model == null || !model.AccountModules.TryGetValue(NotesModuleName, out var m) || m == null || m.TooNew || m.V != NotesModuleVersion || m.Data == null) return null;
            int dropped = 0;
            return NotesFromTables(m.Data, ref dropped);
        }

        public static Dictionary<int, FieldNote> NotesFromTables(Dictionary<string, object> d, ref int dropped)
        {
            var res = new Dictionary<int, FieldNote>();
            var no = d == null ? null : d.Child("notes");
            if (no == null) return res;
            var all = new List<FieldNote>();
            foreach (var kv in no)
            {
                var o = kv.Value as Dictionary<string, object>;
                if (o == null || !Key(kv.Key, MaxId, out var id)) { dropped++; continue; }
                var n = ParseOwnNote(id, o, ref dropped);
                if (n != null) all.Add(n);
            }
            if (all.Count > MaxNotes) { dropped += all.Count - MaxNotes; all = all.OrderByDescending(n => n.Last).ThenBy(n => n.Id).Take(MaxNotes).ToList(); }
            foreach (var n in all) res[n.Id] = n;
            return res;
        }

        static string NoteText(object o, int max)
        {
            var s = o as string;
            if (s == null) return "";
            s = CleanName(s);
            return s.Length > max ? s.Substring(0, max).TrimEnd() : s;
        }

        static FieldNote ParseOwnNote(int id, Dictionary<string, object> o, ref int dropped)
        {
            if (!o.TryGetValue("n", out var nv) || !Whole(nv, 0, MaxCount, out var n)) { dropped++; return null; }
            var x = new FieldNote { Id = id, N = n, Name = NoteText(Field(o, "name"), MaxNameLength), Fam = NoteText(Field(o, "fam"), MaxNoteFamily) };
            if (Field(o, "min") != null && Whole(Field(o, "min"), 1, MaxNoteLevel, out var mn))
            {
                x.Min = (int)mn;
                x.Max = Field(o, "max") != null && Whole(Field(o, "max"), mn, MaxNoteLevel, out var mx) ? (int)mx : x.Min;
            }
            else if (Field(o, "min") != null) dropped++;
            if (Field(o, "last") != null) { if (Whole(Field(o, "last"), MinUnix, MaxAt, out var at)) x.Last = at; else dropped++; }
            var cls = Field(o, "class") as string;
            if (cls != null) { if (Array.IndexOf(NoteClasses, cls) >= 0) x.Class = cls; else dropped++; }
            if (o.TryGetValue("taught", out var tv))
                foreach (var e in ArrayValues(tv))
                {
                    var tok = (e as string ?? "").ToLowerInvariant();
                    if (x.Taught.Count >= MaxNoteTaught || !NoteToken.IsMatch(tok)) { dropped++; continue; }
                    x.Taught[tok] = 1;
                }
            if (o.TryGetValue("pts", out var pv))
                foreach (var e in ArrayValues(pv))
                {
                    var po = e as Dictionary<string, object>;
                    if (po == null || !Whole(Field(po, "map"), 1, MaxMapId, out var map)) { dropped++; continue; }
                    if (!x.Pts.TryGetValue((int)map, out var l))
                    {
                        if (x.Pts.Count >= MaxNoteMaps) { dropped++; continue; }
                        l = new List<NotePoint>();
                    }
                    var p = ArrayValues(Field(po, "p")).ToList();                       // percent: x, y, x, y ...
                    for (int i = 0; i + 1 < p.Count; i += 2)
                    {
                        if (!Num(p[i], out var px) || !Num(p[i + 1], out var py) || px < 0 || px > 100 || py < 0 || py > 100) { dropped++; continue; }
                        double fx = Math.Round(px / 100, 4, MidpointRounding.AwayFromZero), fy = Math.Round(py / 100, 4, MidpointRounding.AwayFromZero);
                        if (l.Any(q => (q.X - fx) * (q.X - fx) + (q.Y - fy) * (q.Y - fy) <= NoteMerge * NoteMerge + 1e-12)) continue;
                        if (x.PointCount >= MaxNotePtsPerNpc) { dropped++; continue; }
                        l.Add(new NotePoint { X = fx, Y = fy });
                    }
                    if (l.Count > 0) x.Pts[(int)map] = l;
                }
            if (NoteEmpty(x)) { dropped++; return null; }
            return x;
        }

        // ---------------------------------------------------------------- the wire

        public static string Sig(FieldNote n)
        {
            var sb = new StringBuilder();
            sb.Append(n.Name).Append('|').Append(I(n.Min)).Append('|').Append(I(n.Max)).Append('|').Append(I(n.N)).Append('|').Append(I(n.Last)).Append('|').Append(n.Class).Append('|').Append(n.Fam).Append('|');
            sb.Append(string.Join(",", n.Taught.Keys.OrderBy(k => k, StringComparer.Ordinal))).Append('|');
            foreach (var m in n.Pts.OrderBy(k => k.Key)) sb.Append(I(m.Key)).Append('=').Append(string.Join(",", m.Value.Select(p => D4(p.X) + ":" + D4(p.Y)).OrderBy(s => s, StringComparer.Ordinal))).Append(';');
            return Hash(sb.ToString());
        }

        // the member's note as the server takes it (the same shape the Lodge cache keeps for the merge, less the member counts)
        internal static Dictionary<string, object> NoteWire(FieldNote n)
        {
            var o = new Dictionary<string, object> { ["n"] = n.N };
            if (n.Name.Length > 0) o["name"] = n.Name;
            if (n.Min > 0) { o["min"] = n.Min; o["max"] = n.Max; }
            if (n.Last > 0) o["last"] = n.Last;
            if (n.Class.Length > 0) o["class"] = n.Class;
            if (n.Fam.Length > 0) o["fam"] = n.Fam;
            if (n.Taught.Count > 0) o["taught"] = n.Taught.Keys.OrderBy(k => k, StringComparer.Ordinal).Select(k => (object)k).ToList();
            if (n.Pts.Count > 0)
            {
                var po = new Dictionary<string, object>();
                foreach (var m in n.Pts.OrderBy(k => k.Key)) po[I(m.Key)] = m.Value.OrderBy(p => p.X).ThenBy(p => p.Y).Select(p => (object)new Dictionary<string, object> { ["x"] = p.X, ["y"] = p.Y }).ToList();
                o["pts"] = po;
            }
            return o;
        }

        // the Lodge's merge as `atlas.data` and the cache file carry it
        internal static Dictionary<string, object> NoteLodgeNode(FieldNote n)
        {
            var o = NoteWire(n);
            o["m"] = n.Members;
            if (n.Taught.Count > 0) { var to = new Dictionary<string, object>(); foreach (var t in n.Taught.OrderBy(k => k.Key, StringComparer.Ordinal)) to[t.Key] = t.Value; o["taught"] = to; }
            if (n.Pts.Count > 0)
            {
                var po = new Dictionary<string, object>();
                foreach (var m in n.Pts.OrderBy(k => k.Key)) po[I(m.Key)] = m.Value.OrderBy(p => p.X).ThenBy(p => p.Y).Select(p => (object)new Dictionary<string, object> { ["x"] = p.X, ["y"] = p.Y, ["m"] = p.M }).ToList();
                o["pts"] = po;
            }
            if (n.By.Count > 0) o["by"] = n.By.Select(b => (object)b).ToList();
            return o;
        }

        // The Lodge's merged note on one npc, checked again here whatever the server says; null when it does not hold together.
        public static FieldNote ParseLodgeNote(int id, object node, ref int dropped)
        {
            var o = node as Dictionary<string, object>;
            if (o == null || !o.TryGetValue("n", out var nv) || !Whole(nv, 0, MaxAggregateCount, out var n)) { dropped++; return null; }
            var x = new FieldNote { Id = id, N = n, Name = NoteText(Field(o, "name"), MaxNameLength), Fam = NoteText(Field(o, "fam"), MaxNoteFamily) };
            if (Field(o, "m") != null && Whole(Field(o, "m"), 0, 100_000, out var m)) x.Members = (int)m;
            if (Field(o, "min") != null && Whole(Field(o, "min"), 1, MaxNoteLevel, out var mn))
            {
                x.Min = (int)mn;
                x.Max = Field(o, "max") != null && Whole(Field(o, "max"), mn, MaxNoteLevel, out var mx) ? (int)mx : x.Min;
            }
            if (Field(o, "last") != null && Whole(Field(o, "last"), MinUnix, 4_000_000_000L, out var at)) x.Last = at;
            var cls = Field(o, "class") as string;
            if (cls != null && Array.IndexOf(NoteClasses, cls) >= 0) x.Class = cls;
            var to = Field(o, "taught") as Dictionary<string, object>;
            if (to != null)
                foreach (var kv in to)
                {
                    if (x.Taught.Count >= MaxNoteTaught * 2 || !NoteToken.IsMatch(kv.Key) || !Whole(kv.Value, 1, 100_000, out var c)) { dropped++; continue; }
                    x.Taught[kv.Key] = (int)c;
                }
            var po = Field(o, "pts") as Dictionary<string, object>;
            if (po != null)
                foreach (var mk in po)
                {
                    if (x.Pts.Count >= MaxNoteMaps * 4 || !Key(mk.Key, MaxMapId, out var map)) { dropped++; continue; }
                    var l = new List<NotePoint>();
                    foreach (var e in ArrayValues(mk.Value))
                    {
                        var eo = e as Dictionary<string, object>;
                        if (eo == null || !Coord4(Field(eo, "x"), out var px) || !Coord4(Field(eo, "y"), out var py)) { dropped++; continue; }
                        long pm = 1; if (Field(eo, "m") != null && !Whole(Field(eo, "m"), 1, 100_000, out pm)) pm = 1;
                        if (l.Count < 40) l.Add(new NotePoint { X = px, Y = py, M = (int)pm });
                    }
                    if (l.Count > 0) x.Pts[map] = l;
                }
            foreach (var b in ArrayValues(Field(o, "by")))
            {
                var name = NoteText(b, MaxNameLength);
                if (name.Length > 0 && x.By.Count < MaxNoteBy) x.By.Add(name);
            }
            if (NoteEmpty(x)) { dropped++; return null; }
            return x;
        }

        // ---------------------------------------------------------------- words

        public static string NoteLevelText(FieldNote n) => n == null || n.Min <= 0 ? "" : n.Min == n.Max || n.Max <= 0 ? "level " + I(n.Min) : "level " + I(n.Min) + "-" + I(n.Max);

        public static string NoteClassText(string cls)
        {
            switch (cls) { case "rare": return "Rare"; case "rareelite": return "Rare elite"; case "elite": return "Elite"; case "worldboss": return "World boss"; default: return ""; }
        }

        // "level 35, last seen 2 h ago, 3 members" (the Lodge's merge) or "level 35, last seen 5 min ago, 12 sightings" (a hunter's own note)
        public static string NoteDetail(FieldNote n, DateTime nowUtc, bool lodge)
        {
            if (n == null) return "";
            var parts = new List<string>();
            var lv = NoteLevelText(n); if (lv.Length > 0) parts.Add(lv);
            var ago = AtlasFormat.Ago(n.Last, nowUtc); if (ago.Length > 0) parts.Add("last seen " + ago);
            if (lodge) { if (n.Members > 0) parts.Add(AtlasFormat.Count(n.Members, "member", "members")); }
            else if (n.N > 0) parts.Add(AtlasFormat.Count(n.N, "sighting", "sightings"));
            return string.Join(", ", parts);
        }

        public const string LodgeSeenLabel = "Seen by Lodge hunters";
        public static string NoteLine(FieldNote n, DateTime nowUtc) { var d = NoteDetail(n, nowUtc, true); return d.Length == 0 ? "" : LodgeSeenLabel + ": " + d; }
    }
}
