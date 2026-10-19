using System.Collections.Generic;

namespace Elans.Shared
{
    // The Atlas pool (server/PROTOCOL.md, 2.15 and the atlas2..atlas9 additions): the envelopes of atlas.share / atlas.shared / atlas.get /
    // atlas.data live only here. The classes only map fields; every rule stays where it was.
    // The sections themselves (kills, objects, ... - objects keyed by id, and finds - an array) are genuinely free-form: each has its own
    // shape and its own strict validator on both ends, so they travel as raw children in each side's own JSON model (ILodgeFields.RawChild /
    // RawList): the Outpost builds them (AtlasObs.MakeBatch) and re-checks every entry it receives (LodgeAtlas.Apply, AtlasObs.Parse*),
    // the server validates atlas.share from the raw JSON (AtlasStore.HasContent / Share) and builds atlas.data's sections (AtlasStore.Get).
    // atlas.get's "since" is also still read by the server from the raw JSON: only a whole JSON number above 0 counts.

    public static class AtlasSections
    {
        // every section, in the order atlas.data writes them; "finds" is the one array, the others are objects keyed by id
        public static readonly string[] All =
        {
            "kills", "objects", "fishing", "vendors", "quests", "spawns", "items", Finds, "seenNpcs", "seenObjects", "seenQuests", "nodes", "fishspots",
            "fieldnotes", "entrances", "npcspots", "pickpocket", "skinning", "mining", "herbalism", "salvage", "contained",
        };

        public const string Finds = "finds";
    }

    // client -> server: (members) your Atlas observations, only the sections that changed { <section>: {...}, finds?: [...] }
    // (answered with AtlasSharedMsg)
    public sealed class AtlasShareMsg
    {
        public const string Type = LodgeMsg.AtlasShare;
        // the object sections to send, in this order (raw objects in this side's own JSON model); a section not listed is not sent
        public List<KeyValuePair<string, object>> Sections = new List<KeyValuePair<string, object>>();
        public object Finds;           // the finds (a raw array); null = not sent

        public void Write(ILodgeWriter w)
        {
            w.Set("t", Type);
            foreach (var s in Sections) w.SetRawChild(s.Key, s.Value);
            if (Finds != null) w.SetRawList(AtlasSections.Finds, Finds);
        }

        // the sections that are there as objects (anything else is left out), in AtlasSections order
        public static AtlasShareMsg Read(ILodgeFields f)
        {
            var m = new AtlasShareMsg { Finds = f.RawList(AtlasSections.Finds) };
            foreach (var name in AtlasSections.All)
                if (name != AtlasSections.Finds && f.RawChild(name) is object o) m.Sections.Add(new KeyValuePair<string, object>(name, o));
            return m;
        }
    }

    // server -> client: the answer to atlas.share { ok, accepted, unchanged, rejected, rev, epoch, why? }
    // why: disabled | guest | empty | rate | full | save
    public sealed class AtlasSharedMsg
    {
        public const string Type = LodgeMsg.AtlasShared;
        public bool Ok;
        public int Accepted, Unchanged, Rejected;
        public long Rev;
        public string Epoch, Why;      // Why: null = not sent

        public void Write(ILodgeWriter w)
        {
            w.Set("t", Type); w.Set("ok", Ok); w.Set("accepted", Accepted); w.Set("unchanged", Unchanged); w.Set("rejected", Rejected);
            w.Set("rev", Rev); w.Set("epoch", Epoch);
            if (Why != null) w.Set("why", Why);
        }

        public static AtlasSharedMsg Read(ILodgeFields f) => new AtlasSharedMsg
        {
            Ok = f.Bool("ok"), Accepted = f.Int("accepted"), Unchanged = f.Int("unchanged"), Rejected = f.Int("rejected"), Rev = f.Long("rev"), Epoch = f.Str("epoch"), Why = f.Str("why"),
        };
    }

    // client -> server: the pool, or what changed since a revision of the same epoch { since?, epoch? } (answered with AtlasDataMsg, or an
    // error with code disabled / rate)
    public sealed class AtlasGetMsg
    {
        public const string Type = LodgeMsg.AtlasGet;
        public long? Since;            // null = not sent (everything)
        public string Epoch;           // null = not sent

        public void Write(ILodgeWriter w)
        {
            w.Set("t", Type);
            if (Since is long s) w.Set("since", s);
            if (Epoch != null) w.Set("epoch", Epoch);
        }

        public static AtlasGetMsg Read(ILodgeFields f) => new AtlasGetMsg { Since = f.Has("since") ? f.Long("since") : (long?)null, Epoch = f.Str("epoch") };
    }

    // server -> client: one page of the pool { epoch, rev, at (unix s), <every section>, seenMembers, full?, more? }
    // full = replace everything held first; more = ask again with since = rev. The Outpost's cache file has the same shape.
    public sealed class AtlasDataMsg
    {
        public const string Type = LodgeMsg.AtlasData;
        public string Epoch;
        public long Rev, At;
        // the object sections (raw objects in this side's own JSON model), keyed by section name; written in AtlasSections order, a missing one is not written
        public Dictionary<string, object> Sections = new Dictionary<string, object>();
        public object Finds;           // the finds (a raw array); null = not sent
        public int? SeenMembers;       // how many members' game caches the seen tables come from; null = not sent (reading: clamped to an int)
        public bool Full, More;        // written only when true

        public object Section(string name) => Sections.TryGetValue(name, out var o) ? o : null;

        public void Write(ILodgeWriter w)
        {
            w.Set("t", Type); w.Set("epoch", Epoch); w.Set("rev", Rev); w.Set("at", At);
            foreach (var name in AtlasSections.All)
            {
                if (name == AtlasSections.Finds) { if (Finds != null) w.SetRawList(name, Finds); }
                else if (Sections.TryGetValue(name, out var o) && o != null) w.SetRawChild(name, o);
            }
            if (SeenMembers is int n) w.Set("seenMembers", n);
            if (Full) w.Set("full", true);
            if (More) w.Set("more", true);
        }

        public static AtlasDataMsg Read(ILodgeFields f)
        {
            var m = new AtlasDataMsg
            {
                Epoch = f.Str("epoch"), Rev = f.Long("rev"), At = f.Long("at"), Finds = f.RawList(AtlasSections.Finds),
                SeenMembers = f.Has("seenMembers") ? (int)System.Math.Clamp(f.Long("seenMembers"), int.MinValue, int.MaxValue) : (int?)null, Full = f.Bool("full"), More = f.Bool("more"),
            };
            foreach (var name in AtlasSections.All)
                if (name != AtlasSections.Finds && f.RawChild(name) is object o) m.Sections[name] = o;
            return m;
        }
    }
}
