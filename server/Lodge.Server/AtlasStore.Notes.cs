using System.Globalization;
using System.Text.Json.Nodes;

namespace Lodge;

// Hunter field notes in the store (2.23, see AtlasNotes.cs for the rules): one note per member and npc, the merge per npc kept in noteAggs (rebuilt on load).
public sealed partial class AtlasStore
{
    // the merged note of one npc
    public sealed class NoteAgg { public Atlas.NoteSum Sum = new(); public long Rev; }

    readonly Dictionary<int, NoteAgg> noteAggs = new();
    public int NoteCount { get { lock (Gate) return noteAggs.Count; } }

    static void LoadNotes(MemberData md, JsonObject no, long now)
    {
        foreach (var (k, v) in no)
        {
            if (md.Notes.Count >= Atlas.MaxNotesPerMember || !Atlas.Key(k, Atlas.MaxId, out var id) || !Atlas.ParseFieldNote(v, now, out var n)) continue;
            if (md.NotePoints + n.PtCount > Atlas.MaxNotePtsPerMember) continue;
            md.Notes[id] = n; md.NotePoints += n.PtCount;
        }
    }

    NoteAgg SumNote(int npc, long rev) =>
        new() { Sum = Atlas.AggregateNotes(members.Where(kv => kv.Value.Notes.ContainsKey(npc)).Select(kv => (kv.Key, kv.Value.Notes[npc]))), Rev = rev };

    void RebuildNotes(JsonObject noteRevs, ref long maxRev)
    {
        foreach (var id in members.Values.SelectMany(m => m.Notes.Keys).Distinct().ToList())
        {
            long rr = 1;
            if (noteRevs?[id.ToString(CultureInfo.InvariantCulture)] is JsonValue nv && nv.TryGetValue<long>(out var nl) && nl > 0) rr = nl;
            maxRev = Math.Max(maxRev, rr);
            noteAggs[id] = SumNote(id, rr);
        }
    }

    void FieldNotes(ShareResult r, MemberData md, JsonNode sect, long now)
    {
        if (sect == null) return;
        if (sect is not JsonObject so) { r.Rejected++; return; }
        int seen = 0, points = 0;
        foreach (var (key, node) in so)
        {
            if (++seen > Atlas.MaxNotesPerShare) { r.Rejected++; continue; }
            if (!Atlas.Key(key, Atlas.MaxId, out var id) || !Atlas.ParseFieldNote(node, now, out var inc) || points + inc.PtCount > Atlas.MaxNotePtsPerShare) { r.Rejected++; continue; }
            points += inc.PtCount;
            bool isNew = !md.Notes.TryGetValue(id, out var mine);
            if (isNew && md.Notes.Count >= Atlas.MaxNotesPerMember) { r.Rejected++; continue; }
            if (isNew && !noteAggs.ContainsKey(id) && noteAggs.Count >= Atlas.MaxNotes) { r.Rejected++; continue; }
            var have = isNew ? new Atlas.FieldNote() : mine.Clone();
            int ptsBefore = have.PtCount;
            bool changed = Atlas.MergeFieldNote(have, inc, out var full);
            if (md.NotePoints + (have.PtCount - ptsBefore) > Atlas.MaxNotePtsPerMember) { r.Rejected++; continue; }
            if (!changed) { if (full) r.Rejected++; else r.Unchanged++; continue; }
            md.NotePoints += have.PtCount - ptsBefore; md.Notes[id] = have;
            noteAggs[id] = SumNote(id, ++rev);
            r.Accepted++; r.Changed = true;
        }
    }

    // the wire / storage shape of one member's note
    static JsonObject NoteStoreJson(Atlas.FieldNote n)
    {
        var o = new JsonObject { ["n"] = n.N };
        if (n.Name.Length > 0) o["name"] = n.Name;
        if (n.Min > 0) { o["min"] = n.Min; o["max"] = n.Max; }
        if (n.Last > 0) o["last"] = n.Last;
        if (n.Class.Length > 0) o["class"] = n.Class;
        if (n.Fam.Length > 0) o["fam"] = n.Fam;
        if (n.Taught.Count > 0) o["taught"] = new JsonArray(n.Taught.Select(t => (JsonNode)JsonValue.Create(t)).ToArray());
        if (n.Pts.Count > 0)
        {
            var po = new JsonObject();
            foreach (var (map, l) in n.Pts.OrderBy(k => k.Key)) po[map.ToString(CultureInfo.InvariantCulture)] = new JsonArray(l.Select(p => (JsonNode)new JsonObject { ["x"] = p.X, ["y"] = p.Y }).ToArray());
            o["pts"] = po;
        }
        return o;
    }

    // What everybody gets: the merge. Members and up also get up to five Lodge display names (newest first); a guest gets neither names nor a time finer than the UTC day.
    JsonObject NoteJson(NoteAgg a, Func<string, string> nameOf, bool guest)
    {
        var s = a.Sum;
        var o = new JsonObject { ["n"] = s.N, ["m"] = s.Members };
        if (s.Name.Length > 0) o["name"] = s.Name;
        if (s.Min > 0) { o["min"] = s.Min; o["max"] = s.Max; }
        if (s.Last > 0) o["last"] = guest ? s.Last - s.Last % 86_400 : s.Last;
        if (s.Class.Length > 0) o["class"] = s.Class;
        if (s.Fam.Length > 0) o["fam"] = s.Fam;
        if (s.Taught.Count > 0)
        {
            var to = new JsonObject();
            foreach (var (t, c) in s.Taught.OrderBy(k => k.Key, StringComparer.Ordinal)) to[t] = c;
            o["taught"] = to;
        }
        if (s.Pts.Count > 0)
        {
            var po = new JsonObject();
            foreach (var (map, l) in s.Pts.OrderBy(k => k.Key)) po[map.ToString(CultureInfo.InvariantCulture)] = new JsonArray(l.Select(p => (JsonNode)new JsonObject { ["x"] = p.X, ["y"] = p.Y, ["m"] = p.M }).ToArray());
            o["pts"] = po;
        }
        if (!guest && s.Who.Count > 0)
            o["by"] = new JsonArray(s.Who.Select(w => nameOf(members.TryGetValue(w.Who, out var wm) && wm.Who.Length > 0 ? wm.Who : w.Who)).Where(x => !string.IsNullOrEmpty(x)).Distinct(StringComparer.Ordinal).Take(Atlas.MaxNoteBy).Select(x => (JsonNode)JsonValue.Create(x)).ToArray());
        return o;
    }

    // purge (2.22 rules): the member's notes go, the merges are rebuilt from the other members. Returns the number of notes removed.
    int PurgeNotes(MemberData md)
    {
        var ids = md.Notes.Keys.ToList();
        foreach (var id in ids)
        {
            if (members.Values.Any(m => m.Notes.ContainsKey(id))) noteAggs[id] = SumNote(id, ++rev);
            else noteAggs.Remove(id);
        }
        return ids.Count;
    }
}
