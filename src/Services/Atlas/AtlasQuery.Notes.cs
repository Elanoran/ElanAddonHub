using System;
using System.Collections.Generic;
using System.Linq;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services.Maps;

namespace ElansAddonHub.Services.Atlas
{
    // Hunter field notes on the Atlas (Hunter Helper 1.26.11 notes, shared through the Lodge since server 2.23): "Seen by Lodge hunters: level 35, last seen 2 h ago, 3 members"
    // plus this PC's own notes ("Seen by you"), the abilities a tame taught, and the places the beast was seen. Used by the creature page and the world map's rare creatures.
    public static partial class AtlasQuery
    {
        // this PC's own field notes (npc -> note), null when Hunter Helper has none
        public static IReadOnlyDictionary<int, AtlasObs.FieldNote> OwnNotes()
        {
            try { return Context.OwnFieldNotes?.Invoke(); } catch (Exception) { return null; }
        }

        // "bite4" -> "Bite 4"
        public static string TokenLabel(string tok)
        {
            if (string.IsNullOrEmpty(tok)) return "";
            int i = 0; while (i < tok.Length && char.IsLetter(tok[i])) i++;
            var name = tok.Substring(0, i);
            if (name.Length == 0) return tok;
            name = char.ToUpperInvariant(name[0]) + name.Substring(1);
            return i < tok.Length ? name + " " + tok.Substring(i) : name;
        }

        // facts + map markers of a creature page; true when anything was added
        static bool NoteFacts(AtlasPageModel m, int id, LodgeAtlas L, PlanBuilder plan)
        {
            var now = Context.NowUtc();
            var ln = L?.Note(id);
            IReadOnlyDictionary<int, AtlasObs.FieldNote> ownAll = OwnNotes();
            AtlasObs.FieldNote own = null;
            if (ownAll != null) ownAll.TryGetValue(id, out own);
            bool any = false;
            if (ln != null)
            {
                var d = AtlasObs.NoteDetail(ln, now, true);
                if (d.Length > 0) { m.Facts.Add(new AtlasFact(AtlasObs.LodgeSeenLabel, d, AtlasLabel.Forever)); any = true; }
                // the details live on the Lodge tab; the one-line summary above stays in the facts
                var sec = Section(m, "lodgenotes", AtlasObs.LodgeSeenLabel);
                if (ln.By.Count > 0) sec.Rows.Add(new AtlasRow { Key = "notes:by", Label = AtlasLabel.Forever, Tier = 0, Type = "Seen by", Title = string.Join(", ", ln.By), Detail = ln.Members > ln.By.Count ? "and " + (ln.Members - ln.By.Count) + " more" : "" });
                if (ln.Taught.Count > 0) sec.Rows.Add(new AtlasRow { Key = "notes:taught", Label = AtlasLabel.Forever, Tier = 0, Type = "Taught", Title = string.Join(", ", ln.Taught.OrderBy(t => t.Key, StringComparer.Ordinal).Select(t => TokenLabel(t.Key) + (ln.Members > 1 ? " (" + t.Value + "/" + ln.Members + ")" : ""))), Detail = "when tamed" });
                if (ln.N > 0) sec.Rows.Add(new AtlasRow { Key = "notes:n", Label = AtlasLabel.Forever, Tier = 0, Type = "Sightings", Title = AtlasFormat.Count(ln.N, "sighting", "sightings"), Detail = AtlasFormat.Count(ln.Members, "hunter", "hunters") });
                foreach (var kv in ln.Pts.OrderBy(k => k.Key))
                    foreach (var p in kv.Value) plan?.Spot(kv.Key, p.X, p.Y, MarkerKind.Spawn, m.Title, AtlasObs.LodgeSeenLabel + " · " + AtlasLabel.Forever, "npc:" + id);
                AddLayer(m, "lodge");
            }
            if (own != null)
            {
                var d = AtlasObs.NoteDetail(own, now, false);
                if (d.Length > 0) { m.Facts.Add(new AtlasFact("Seen by you", d, AtlasLabel.Forever)); any = true; }
                foreach (var kv in own.Pts.OrderBy(k => k.Key))
                    foreach (var p in kv.Value) plan?.Spot(kv.Key, p.X, p.Y, MarkerKind.Spawn, m.Title, "Seen by you · " + AtlasLabel.Forever, "npc:" + id);
            }
            return any;
        }

        // rare / rare elite beasts hunters noted where they stood (the Lodge's merge first, then this PC's own notes for npcs the Lodge has none on)
        public static IEnumerable<AtlasObs.FieldNote> RareNotes()
        {
            var seen = new HashSet<int>();
            var l = Lodge();
            if (l != null) foreach (var n in l.AllNotes) if (n.IsRare && n.Pts.Count > 0 && seen.Add(n.Id)) yield return n;
            var own = OwnNotes();
            if (own != null) foreach (var n in own.Values) if (n.IsRare && n.Pts.Count > 0 && seen.Add(n.Id)) yield return n;
        }

        // the world map's rare creatures: the Lodge note's words for a tooltip ("Seen by Lodge hunters: level 35, last seen 2 h ago, 3 members"), "" when there is none
        public static string NoteTip(int npcId)
        {
            var now = Context.NowUtc();
            var parts = new List<string>();
            var ln = Lodge()?.Note(npcId);
            if (ln != null) { var l = AtlasObs.NoteLine(ln, now); if (l.Length > 0) parts.Add(l); }
            var own = OwnNotes();
            if (own != null && own.TryGetValue(npcId, out var o)) { var d = AtlasObs.NoteDetail(o, now, false); if (d.Length > 0) parts.Add("Seen by you: " + d); }
            return string.Join(" · ", parts);
        }
    }
}
