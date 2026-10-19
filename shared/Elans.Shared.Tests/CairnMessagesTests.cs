using System.Text.Json.Nodes;
using Elans.Shared;

namespace Elans.Shared.Tests;

// The typed Cairn messages (give to get, joining, notices): every message survives Write -> Read, and writes exactly the JSON the old
// hand-built dictionary / JsonObject sent (golden values copied from src/Lodge/LodgeSession.Cairn*.cs and
// server/Lodge.Server/CairnReceive.cs at 6edfb1f) - the wire must not change.
public class CairnMessagesTests
{
    static TestFields Written(Action<ILodgeWriter> write) => TestFields.Written(write);
    static void SameJson(object expected, object actual) => TestFields.SameJson(expected, actual);

    static CairnJoinMsg Join() => new CairnJoinMsg
    {
        On = true, Env = false, Atlas = true, CairnHost = "nasferatu.dk", Contributing = true, SuggestedUrl = "https://lodge.example.test", Standing = "good",
        ReceiveUntil = 1_800_000_000, Serving = true, PublicUrl = "https://lodge.example.test", State = "admitted", Reason = "", Error = "", Mode = "beta",
        Place = 0, Of = 0, EstDay = "", BetaOpenSlots = 3, HasCode = true, Admitted = 1_790_000_000, ProbationUntil = null, Settled = true, LastPost = 1_790_100_000,
        Checked = null, Signed = true,
    };

    static CairnNoticeEntry Notice() => new CairnNoticeEntry
    {
        Id = 7, Created = 1_790_000_000, Expires = 1_790_600_000, Level = "warning", To = "*", Text = "Maintenance on Sunday", Sig = "c2lnbmF0dXJl", Kid = "k1",
    };

    // ---- round trips

    [Fact]
    public void Messages_round_trip()
    {
        Assert.True(AtlasSharingMsg.Read(Written(new AtlasSharingMsg { On = true }.Write)).On);
        Assert.False(AtlasSharingMsg.Read(Written(new AtlasSharingMsg { On = false }.Write)).On);

        var s = CairnStatusMsg.Read(Written(new CairnStatusMsg
        {
            Entitled = false, Sharing = true, Why = "lodge", Text = "not receiving", Day = "2026-10-19", Seq = 41,
            Lodge = new CairnLodgeStanding { Standing = "lapsed", Serving = false, ReceiveUntil = 5, Checked = 6, Why = "old", Problem = "net" },
        }.Write));
        Assert.Equal((false, true, "lodge", "not receiving", "2026-10-19", 41L), (s.Entitled, s.Sharing, s.Why, s.Text, s.Day, s.Seq));
        Assert.Equal(("lapsed", false, 5L, 6L, "old", "net"), (s.Lodge.Standing, s.Lodge.Serving, s.Lodge.ReceiveUntil, s.Lodge.Checked, s.Lodge.Why, s.Lodge.Problem));
        var bare = CairnStatusMsg.Read(Written(new CairnStatusMsg { Entitled = true }.Write));
        Assert.Equal(((string)null, (long?)null, (CairnLodgeStanding)null), (bare.Why, bare.Seq, bare.Lodge));

        var j = CairnJoinMsg.Read(Written(Join().Write));
        Assert.Equal((true, false, true, true, true, true, true, true), (j.On, j.Env, j.Atlas, j.Contributing, j.Serving, j.HasCode, j.Settled, j.Signed));
        Assert.Equal(("nasferatu.dk", "https://lodge.example.test", "good", "admitted", "beta"), (j.CairnHost, j.SuggestedUrl, j.Standing, j.State, j.Mode));
        Assert.Equal(((long?)1_800_000_000, (long?)1_790_000_000, (long?)null, (long?)1_790_100_000, (long?)null), (j.ReceiveUntil, j.Admitted, j.ProbationUntil, j.LastPost, j.Checked));
        Assert.Equal((0, 0, (int?)3), (j.Place, j.Of, j.BetaOpenSlots));

        var n = CairnNoticesMsg.Read(Written(new CairnNoticesMsg { List = { Notice() } }.Write)).List.Single();
        Assert.Equal((7L, 1_790_000_000L, 1_790_600_000L, "warning", "*", "Maintenance on Sunday", "c2lnbmF0dXJl", "k1"), (n.Id, n.Created, n.Expires, n.Level, n.To, n.Text, n.Sig, n.Kid));
        Assert.Equal(new long[] { 7, 9 }, CairnNoticeSeenMsg.Read(Written(new CairnNoticeSeenMsg { Ids = { 7, 9 } }.Write)).Ids);

        var a = AdminCairnMsg.Read(Written(new AdminCairnMsg { On = true, PublicUrl = "https://lodge.example.test", Code = "ABCD-1234" }.Write));
        Assert.Equal((true, "https://lodge.example.test", "ABCD-1234"), (a.On, a.PublicUrl, a.Code));
        var off = AdminCairnMsg.Read(Written(new AdminCairnMsg { On = false }.Write));
        Assert.Equal((false, (string)null, (string)null), (off.On, off.PublicUrl, off.Code));
    }

    [Fact]
    public void Each_message_writes_its_own_type()
    {
        Assert.Equal("atlas.sharing", Written(new AtlasSharingMsg().Write).Str("t"));
        Assert.Equal("cairn.status", Written(new CairnStatusGetMsg().Write).Str("t"));
        Assert.Equal("cairn.status", Written(new CairnStatusMsg().Write).Str("t"));
        Assert.Equal("cairn.join.get", Written(new CairnJoinGetMsg().Write).Str("t"));
        Assert.Equal("cairn.join", Written(new CairnJoinMsg().Write).Str("t"));
        Assert.Equal("cairn.notices.get", Written(new CairnNoticesGetMsg().Write).Str("t"));
        Assert.Equal("cairn.notices", Written(new CairnNoticesMsg().Write).Str("t"));
        Assert.Equal("cairn.notice.seen", Written(new CairnNoticeSeenMsg().Write).Str("t"));
        Assert.Equal("admin.cairn", Written(new AdminCairnMsg().Write).Str("t"));
    }

    [Fact]
    public void Reading_is_lenient_like_before()
    {
        // cairn.join: a missing "atlas" is true, a missing or null number is null, a present non-number is 0 (as the Outpost's L() read it)
        var j = CairnJoinMsg.Read(new TestFields(new Dictionary<string, object> { ["admitted"] = null, ["lastPost"] = "x", ["betaOpenSlots"] = null }));
        Assert.True(j.Atlas);
        Assert.Null(j.Admitted);
        Assert.Equal(0L, j.LastPost);
        Assert.Null(j.BetaOpenSlots);
        Assert.Null(j.ReceiveUntil);
        Assert.False(CairnJoinMsg.Read(new TestFields(new Dictionary<string, object> { ["atlas"] = "yes" })).Atlas);
        // notice.seen: an entry that isn't a whole number is 0 (the server drops it), a missing list is empty
        Assert.Equal(new long[] { 3, 0, 0, 0 }, CairnNoticeSeenMsg.Read(new TestFields(new Dictionary<string, object> { ["ids"] = new List<object> { 3L, 1.5, "4", null } })).Ids);
        Assert.Empty(CairnNoticeSeenMsg.Read(new TestFields()).Ids);
        // notices: a non-object entry is skipped, wrong types are missing
        var list = CairnNoticesMsg.Read(new TestFields(new Dictionary<string, object> { ["list"] = new List<object> { "junk", new Dictionary<string, object> { ["id"] = "7", ["text"] = 5 } } })).List;
        Assert.Equal((0L, (string)null, (string)null), (list.Single().Id, list.Single().Text, list.Single().To));
        Assert.False(AtlasSharingMsg.Read(new TestFields(new Dictionary<string, object> { ["on"] = "true" })).On);
        // admin.cairn: a publicUrl that isn't a string is "not given" (the server then keeps / suggests its own), on must be a JSON true
        var a = AdminCairnMsg.Read(new TestFields(new Dictionary<string, object> { ["on"] = 1, ["publicUrl"] = 5, ["code"] = "" }));
        Assert.Equal((false, (string)null, ""), (a.On, a.PublicUrl, a.Code));
    }

    // ---- golden: what the old code sent

    [Fact]
    public void Golden_client_messages_match_the_old_Outpost_dictionaries()
    {
        SameJson(new Dictionary<string, object> { ["t"] = "atlas.sharing", ["on"] = true }, Written(new AtlasSharingMsg { On = true }.Write).D);
        SameJson(new Dictionary<string, object> { ["t"] = "cairn.join.get" }, Written(new CairnJoinGetMsg().Write).D);
        SameJson(new Dictionary<string, object> { ["t"] = "cairn.notices.get" }, Written(new CairnNoticesGetMsg().Write).D);
        SameJson(new Dictionary<string, object> { ["t"] = "cairn.notice.seen", ["ids"] = new List<object> { 7L } }, Written(new CairnNoticeSeenMsg { Ids = { 7 } }.Write).D);
        // the ask the server answers (tests and other clients send it; the Outpost waits for the answer to atlas.sharing)
        SameJson(new Dictionary<string, object> { ["t"] = "cairn.status" }, Written(new CairnStatusGetMsg().Write).D);
        // LodgeSession.SetCairnJoin: publicUrl / code only when given
        SameJson(new Dictionary<string, object> { ["t"] = "admin.cairn", ["on"] = false }, Written(new AdminCairnMsg { On = false }.Write).D);
        SameJson(new Dictionary<string, object> { ["t"] = "admin.cairn", ["on"] = true, ["publicUrl"] = "https://lodge.example.test", ["code"] = "ABCD-1234" },
            Written(new AdminCairnMsg { On = true, PublicUrl = "https://lodge.example.test", Code = "ABCD-1234" }.Write).D);
        SameJson(new Dictionary<string, object> { ["t"] = "admin.cairn", ["on"] = true, ["code"] = "ABCD-1234" }, Written(new AdminCairnMsg { On = true, Code = "ABCD-1234" }.Write).D);
    }

    [Fact]
    public void Golden_status_matches_the_old_server_JsonObject()
    {
        // CairnReceive.StatusFor: entitled with the dataset on hand
        SameJson(new JsonObject { ["t"] = "cairn.status", ["entitled"] = true, ["sharing"] = true, ["day"] = "2026-10-19", ["seq"] = 41L },
            Written(new CairnStatusMsg { Entitled = true, Sharing = true, Day = "2026-10-19", Seq = 41 }.Write).D);
        // 2.31.1, the Cairn off: { entitled: false, sharing, why: "disabled", text }, no lodge block
        SameJson(new JsonObject { ["t"] = "cairn.status", ["entitled"] = false, ["sharing"] = false, ["why"] = "disabled", ["text"] = "This Lodge doesn't hand out the Cairn's data" },
            Written(new CairnStatusMsg { Entitled = false, Sharing = false, Why = "disabled", Text = "This Lodge doesn't hand out the Cairn's data" }.Write).D);
        // the owner: the lodge block, only the parts that are known
        SameJson(new JsonObject
        {
            ["t"] = "cairn.status", ["entitled"] = false, ["sharing"] = true, ["why"] = "lodge", ["text"] = "x",
            ["lodge"] = new JsonObject { ["standing"] = "unknown", ["serving"] = false, ["checked"] = 1_790_000_000L, ["problem"] = "network" },
        }, Written(new CairnStatusMsg
        {
            Entitled = false, Sharing = true, Why = "lodge", Text = "x",
            Lodge = new CairnLodgeStanding { Standing = "unknown", Serving = false, Checked = 1_790_000_000, Problem = "network" },
        }.Write).D);
    }

    [Fact]
    public void Golden_join_matches_the_old_server_JsonObject()
    {
        // CairnReceive.JoinJson: every key, an unknown number as null
        var j = Join();
        var old = new JsonObject
        {
            ["t"] = "cairn.join", ["env"] = j.Env, ["atlas"] = j.Atlas, ["cairnHost"] = j.CairnHost, ["contributing"] = j.Contributing,
            ["suggestedUrl"] = j.SuggestedUrl, ["standing"] = j.Standing, ["receiveUntil"] = j.ReceiveUntil, ["serving"] = j.Serving,
        };
        old["on"] = j.On; old["publicUrl"] = j.PublicUrl; old["state"] = j.State; old["reason"] = j.Reason; old["error"] = j.Error;
        old["mode"] = j.Mode; old["place"] = j.Place; old["of"] = j.Of; old["estDay"] = j.EstDay; old["betaOpenSlots"] = j.BetaOpenSlots; old["hasCode"] = j.HasCode;
        old["admitted"] = j.Admitted; old["probationUntil"] = j.ProbationUntil; old["settled"] = j.Settled; old["lastPost"] = j.LastPost; old["checked"] = j.Checked;
        old["signed"] = j.Signed;
        SameJson(old, Written(j.Write).D);

        // a fresh Lodge: nothing known yet
        var fresh = new CairnJoinMsg { CairnHost = "nasferatu.dk", SuggestedUrl = "", Standing = "off", PublicUrl = "", State = "", Reason = "", Error = "", Mode = "", EstDay = "" };
        var o = new JsonObject
        {
            ["t"] = "cairn.join", ["env"] = false, ["atlas"] = true, ["cairnHost"] = "nasferatu.dk", ["contributing"] = false, ["suggestedUrl"] = "", ["standing"] = "off",
            ["receiveUntil"] = null, ["serving"] = false, ["on"] = false, ["publicUrl"] = "", ["state"] = "", ["reason"] = "", ["error"] = "", ["mode"] = "", ["place"] = 0,
            ["of"] = 0, ["estDay"] = "", ["betaOpenSlots"] = null, ["hasCode"] = false, ["admitted"] = null, ["probationUntil"] = null, ["settled"] = false,
            ["lastPost"] = null, ["checked"] = null, ["signed"] = false,
        };
        SameJson(o, Written(fresh.Write).D);
    }

    [Fact]
    public void Golden_notices_match_the_old_server_JsonObject()
    {
        // CairnNotices.Clean stores exactly these keys; NoticesJson sends the stored objects as they are
        var stored = new JsonObject
        {
            ["id"] = 7L, ["created"] = 1_790_000_000L, ["expires"] = 1_790_600_000L, ["level"] = "warning", ["to"] = "*", ["text"] = "Maintenance on Sunday",
            ["sig"] = "c2lnbmF0dXJl", ["kid"] = "k1",
        };
        SameJson(new JsonObject { ["t"] = "cairn.notices", ["list"] = new JsonArray(stored.DeepClone()) }, Written(new CairnNoticesMsg { List = { Notice() } }.Write).D);
        SameJson(stored, Written(CairnNoticeEntry.Read(TestFields.From(stored)).Write).D);
        SameJson(new JsonObject { ["t"] = "cairn.notices", ["list"] = new JsonArray() }, Written(new CairnNoticesMsg().Write).D);
    }
}
