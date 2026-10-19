using System.Text.Json.Nodes;
using Elans.Shared;

namespace Elans.Shared.Tests;

// The typed voice and typing control messages: every message survives Write -> Read, and writes exactly the JSON the old hand-built
// dictionary / JsonObject sent (golden values copied from src/Lodge/LodgeSession.cs and server/Lodge.Server/Chat.cs at c712015) - the wire
// must not change. The voice audio is binary and not part of this.
public class VoiceMessagesTests
{
    static TestFields Written(Action<ILodgeWriter> write) => TestFields.Written(write);
    static void SameJson(object expected, object actual) => TestFields.SameJson(expected, actual);

    [Fact]
    public void Messages_round_trip()
    {
        var v = VoiceMsg.Read(Written(new VoiceMsg { Room = "v1", On = true }.Write));
        Assert.Equal(("v1", (bool?)true), (v.Room, v.On));
        var leave = VoiceMsg.Read(Written(new VoiceMsg { Room = null, On = false }.Write));
        Assert.Equal(((string)null, (bool?)false), (leave.Room, leave.On));
        Assert.Null(VoiceMsg.Read(Written(new VoiceMsg { Room = "v1" }.Write)).On);
        var st = VoiceStateMsg.Read(Written(new VoiceStateMsg { Muted = true, Deaf = false }.Write));
        Assert.Equal((true, false), (st.Muted, st.Deaf));
        Assert.Equal("general", TypingMsg.Read(Written(new TypingMsg { Channel = "general" }.Write)).Channel);
        var mt = MemberTypingMsg.Read(Written(new MemberTypingMsg { Id = 7, Channel = "general" }.Write));
        Assert.Equal((7, "general"), (mt.Id, mt.Channel));
    }

    [Fact]
    public void Each_message_writes_its_own_type()
    {
        Assert.Equal("voice", Written(new VoiceMsg().Write).Str("t"));
        Assert.Equal("state", Written(new VoiceStateMsg().Write).Str("t"));
        Assert.Equal("typing", Written(new TypingMsg().Write).Str("t"));
        Assert.Equal("typing", Written(new MemberTypingMsg().Write).Str("t"));
    }

    [Fact]
    public void Reading_is_lenient_like_before()
    {
        // the server's J.Str / J.BoolOrNull / J.Bool: a wrong type is "missing"
        var v = VoiceMsg.Read(new TestFields(new Dictionary<string, object> { ["room"] = 5, ["on"] = "yes" }));
        Assert.Equal(((string)null, (bool?)null), (v.Room, v.On));
        foreach (var notBool in new object[] { 1, 0, "true", null, new Dictionary<string, object>(), new List<object>() })
            Assert.Null(VoiceMsg.Read(new TestFields(new Dictionary<string, object> { ["on"] = notBool })).On);
        Assert.Null(VoiceMsg.Read(new TestFields()).On);
        var st = VoiceStateMsg.Read(new TestFields(new Dictionary<string, object> { ["muted"] = "x", ["deaf"] = 5 }));
        Assert.Equal((false, false), (st.Muted, st.Deaf));
        Assert.Null(TypingMsg.Read(new TestFields(new Dictionary<string, object> { ["channel"] = 9 })).Channel);
        var mt = MemberTypingMsg.Read(new TestFields(new Dictionary<string, object> { ["id"] = "7" }));
        Assert.Equal((0, (string)null), (mt.Id, mt.Channel));
    }

    // ---- golden: what the old code sent

    [Fact]
    public void Golden_client_messages_match_the_old_Outpost_dictionaries()
    {
        // LodgeSession: SendVoice, LeaveVoice, SetMuteDeaf / the state after joining, Typing
        SameJson(new Dictionary<string, object> { ["t"] = "voice", ["room"] = "v1", ["on"] = true }, Written(new VoiceMsg { Room = "v1", On = true }.Write).D);
        SameJson(new Dictionary<string, object> { ["t"] = "voice", ["room"] = null, ["on"] = false }, Written(new VoiceMsg { Room = null, On = false }.Write).D);
        SameJson(new Dictionary<string, object> { ["t"] = "state", ["muted"] = true, ["deaf"] = true }, Written(new VoiceStateMsg { Muted = true, Deaf = true }.Write).D);
        SameJson(new Dictionary<string, object> { ["t"] = "typing", ["channel"] = "general" }, Written(new TypingMsg { Channel = "general" }.Write).D);
        SameJson(new Dictionary<string, object> { ["t"] = "typing", ["channel"] = null }, Written(new TypingMsg().Write).D);
        // key order as before
        Assert.Equal(new[] { "t", "room", "on" }, Written(new VoiceMsg { On = true }.Write).D.Keys);
        Assert.Equal(new[] { "t", "muted", "deaf" }, Written(new VoiceStateMsg().Write).D.Keys);
        Assert.Equal(new[] { "t", "channel" }, Written(new TypingMsg().Write).D.Keys);
    }

    [Fact]
    public void Golden_server_messages_match_the_old_JsonObjects()
    {
        // ChatModule: the coalesced typing broadcast
        var w = Written(new MemberTypingMsg { Id = 7, Channel = "general" }.Write);
        SameJson(new JsonObject { ["t"] = "typing", ["id"] = 7, ["channel"] = "general" }, w.D);
        Assert.Equal(new[] { "t", "id", "channel" }, w.D.Keys);
    }
}
