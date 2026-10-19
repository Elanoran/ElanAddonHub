using System.Text.Json.Nodes;
using Elans.Shared;

namespace Elans.Shared.Tests;

// The typed Atlas pool envelopes: every message survives Write -> Read, and writes exactly the JSON the old hand-built dictionary /
// JsonObject sent (golden values copied from src/Lodge/LodgeSession.Atlas.cs, src/Services/Atlas/AtlasObs.cs (MakeBatch),
// server/Lodge.Server/AtlasModule.cs and AtlasStore.Get at c712015) - the wire must not change. The sections are free-form and pass through as they are.
public class AtlasMessagesTests
{
    static TestFields Written(Action<ILodgeWriter> write) => TestFields.Written(write);
    static void SameJson(object expected, object actual) => TestFields.SameJson(expected, actual);

    static Dictionary<string, object> Kill70() => new() { ["70"] = new Dictionary<string, object> { ["n"] = 40L, ["items"] = new Dictionary<string, object> { ["100"] = new Dictionary<string, object> { ["n"] = 4L, ["c"] = 4L } } } };
    static Dictionary<string, object> Vendor9() => new() { ["9"] = new Dictionary<string, object> { ["at"] = 5L, ["z"] = 11L, ["x"] = 0.5, ["y"] = 0.25, ["items"] = new Dictionary<string, object>() } };
    static List<object> Finds() => new() { new Dictionary<string, object> { ["i"] = 104L, ["c"] = 1L, ["src"] = "npc:1", ["at"] = 1_790_000_000L, ["z"] = 11L, ["x"] = 0.3, ["y"] = 0.3 } };

    // ---- the section list

    [Fact]
    public void The_sections_are_the_ones_both_sides_know_once_each()
    {
        Assert.Equal(22, AtlasSections.All.Length);
        Assert.Equal(AtlasSections.All.Length, AtlasSections.All.Distinct().Count());
        Assert.Equal("finds", AtlasSections.All[7]);              // AtlasStore.Get's section index 7 is the finds array
        foreach (var s in new[] { "kills", "objects", "fishing", "vendors", "quests", "spawns", "items", "seenNpcs", "seenObjects", "seenQuests", "nodes", "fishspots",
            "fieldnotes", "entrances", "npcspots", "pickpocket", "skinning", "mining", "herbalism", "salvage", "contained" })
            Assert.Contains(s, AtlasSections.All);
    }

    // ---- round trips

    [Fact]
    public void Client_messages_round_trip()
    {
        var share = new AtlasShareMsg { Finds = Finds() };
        share.Sections.Add(new("vendors", Vendor9()));
        share.Sections.Add(new("kills", Kill70()));
        var r = AtlasShareMsg.Read(Written(share.Write));
        Assert.Equal(new[] { "kills", "vendors" }, r.Sections.Select(s => s.Key));        // read back in AtlasSections order
        SameJson(Kill70(), r.Sections[0].Value);
        SameJson(Finds(), r.Finds);
        Assert.Null(AtlasShareMsg.Read(Written(new AtlasShareMsg().Write)).Finds);

        var g = AtlasGetMsg.Read(Written(new AtlasGetMsg { Since = 42, Epoch = "e1" }.Write));
        Assert.Equal(((long?)42, "e1"), (g.Since, g.Epoch));
        var all = AtlasGetMsg.Read(Written(new AtlasGetMsg().Write));
        Assert.Equal(((long?)null, (string)null), (all.Since, all.Epoch));
    }

    [Fact]
    public void Server_messages_round_trip()
    {
        var s = AtlasSharedMsg.Read(Written(new AtlasSharedMsg { Ok = true, Accepted = 3, Unchanged = 2, Rejected = 1, Rev = 99, Epoch = "e1" }.Write));
        Assert.Equal((true, 3, 2, 1, 99L, "e1", (string)null), (s.Ok, s.Accepted, s.Unchanged, s.Rejected, s.Rev, s.Epoch, s.Why));
        Assert.Equal("rate", AtlasSharedMsg.Read(Written(new AtlasSharedMsg { Why = "rate" }.Write)).Why);

        var d = new AtlasDataMsg { Epoch = "e1", Rev = 8, At = 1_790_000_000, Finds = Finds(), SeenMembers = 3, Full = true, More = true };
        d.Sections["kills"] = Kill70(); d.Sections["vendors"] = Vendor9();
        var back = AtlasDataMsg.Read(Written(d.Write));
        Assert.Equal(("e1", 8L, 1_790_000_000L, (int?)3, true, true), (back.Epoch, back.Rev, back.At, back.SeenMembers, back.Full, back.More));
        SameJson(Vendor9(), back.Section("vendors"));
        Assert.Null(back.Section("objects"));
        SameJson(Finds(), back.Finds);
    }

    [Fact]
    public void Each_message_writes_its_own_type()
    {
        Assert.Equal("atlas.share", Written(new AtlasShareMsg().Write).Str("t"));
        Assert.Equal("atlas.shared", Written(new AtlasSharedMsg().Write).Str("t"));
        Assert.Equal("atlas.get", Written(new AtlasGetMsg().Write).Str("t"));
        Assert.Equal("atlas.data", Written(new AtlasDataMsg().Write).Str("t"));
    }

    [Fact]
    public void Reading_is_lenient_like_before()
    {
        // a section that is not an object (finds: not an array) is left out; seenMembers is null only when missing, and clamped to an int
        var d = AtlasDataMsg.Read(new TestFields(new Dictionary<string, object>
        {
            ["epoch"] = 5, ["kills"] = new List<object>(), ["vendors"] = "x", ["finds"] = new Dictionary<string, object>(), ["seenMembers"] = 1L << 40, ["full"] = "true",
        }));
        Assert.Equal(((string)null, 0, (object)null, int.MaxValue, false), (d.Epoch, d.Sections.Count, d.Finds, d.SeenMembers.Value, d.Full));
        Assert.Null(AtlasDataMsg.Read(new TestFields()).SeenMembers);
        Assert.Equal(0, AtlasDataMsg.Read(new TestFields(new Dictionary<string, object> { ["seenMembers"] = "3" })).SeenMembers);
        var s = AtlasShareMsg.Read(new TestFields(new Dictionary<string, object> { ["kills"] = 3, ["nodes"] = new Dictionary<string, object>(), ["unknown"] = new Dictionary<string, object>() }));
        Assert.Equal(new[] { "nodes" }, s.Sections.Select(x => x.Key));
    }

    // ---- golden: what the old code sent

    [Fact]
    public void Golden_client_messages_match_the_old_Outpost_dictionaries()
    {
        // AtlasObs.MakeBatch: { t, <sections in the order they filled>, finds? }
        var share = new AtlasShareMsg { Finds = Finds() };
        share.Sections.Add(new("kills", Kill70()));
        share.Sections.Add(new("vendors", Vendor9()));
        var old = new Dictionary<string, object> { ["t"] = "atlas.share", ["kills"] = Kill70(), ["vendors"] = Vendor9(), ["finds"] = Finds() };
        SameJson(old, Written(share.Write).D);
        Assert.Equal(old.Keys, Written(share.Write).D.Keys);
        var one = new AtlasShareMsg(); one.Sections.Add(new("vendors", Vendor9()));
        SameJson(new Dictionary<string, object> { ["t"] = "atlas.share", ["vendors"] = Vendor9() }, Written(one.Write).D);
        // RequestAtlas: since + epoch only once something is held
        SameJson(new Dictionary<string, object> { ["t"] = "atlas.get" }, Written(new AtlasGetMsg().Write).D);
        SameJson(new Dictionary<string, object> { ["t"] = "atlas.get", ["since"] = 42L, ["epoch"] = "e1" }, Written(new AtlasGetMsg { Since = 42, Epoch = "e1" }.Write).D);
    }

    [Fact]
    public void Golden_server_messages_match_the_old_JsonObjects()
    {
        // AtlasModule.Reply
        SameJson(new JsonObject { ["t"] = "atlas.shared", ["ok"] = true, ["accepted"] = 3, ["unchanged"] = 2, ["rejected"] = 1, ["rev"] = 99L, ["epoch"] = "e1" },
            Written(new AtlasSharedMsg { Ok = true, Accepted = 3, Unchanged = 2, Rejected = 1, Rev = 99, Epoch = "e1" }.Write).D);
        SameJson(new JsonObject { ["t"] = "atlas.shared", ["ok"] = false, ["accepted"] = 0, ["unchanged"] = 0, ["rejected"] = 0, ["rev"] = 4L, ["epoch"] = "e1", ["why"] = "guest" },
            Written(new AtlasSharedMsg { Rev = 4, Epoch = "e1", Why = "guest" }.Write).D);

        // AtlasStore.Get: t, epoch, rev, at, every section (finds between items and seenNpcs), seenMembers, full?, more?
        JsonObject Old(bool full, bool more)
        {
            var o = new JsonObject { ["t"] = "atlas.data", ["epoch"] = "e1", ["rev"] = 8L, ["at"] = 1_790_000_000L };
            foreach (var s in AtlasSections.All) o[s] = s == "finds" ? new JsonArray() : s == "kills" ? JsonNode.Parse("{\"70\":{\"n\":40}}") : new JsonObject();
            o["seenMembers"] = 2;
            if (full) o["full"] = true;
            if (more) o["more"] = true;
            return o;
        }
        AtlasDataMsg New(bool full, bool more)
        {
            var d = new AtlasDataMsg { Epoch = "e1", Rev = 8, At = 1_790_000_000, Finds = new List<object>(), SeenMembers = 2, Full = full, More = more };
            foreach (var s in AtlasSections.All) if (s != "finds") d.Sections[s] = s == "kills" ? new Dictionary<string, object> { ["70"] = new Dictionary<string, object> { ["n"] = 40L } } : new Dictionary<string, object>();
            return d;
        }
        foreach (var (full, more) in new[] { (false, false), (true, false), (false, true), (true, true) })
        {
            SameJson(Old(full, more), Written(New(full, more).Write).D);
            Assert.Equal(Old(full, more).Select(kv => kv.Key), Written(New(full, more).Write).D.Keys);
        }
        Assert.Equal(new[] { "t", "ok", "accepted", "unchanged", "rejected", "rev", "epoch", "why" }, Written(new AtlasSharedMsg { Why = "x" }.Write).D.Keys);
    }
}
