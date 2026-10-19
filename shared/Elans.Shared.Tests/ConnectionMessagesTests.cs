using System.Text.Json.Nodes;
using Elans.Shared;

namespace Elans.Shared.Tests;

// The typed keep-alive: ping and pong survive Write -> Read and write exactly what the old code sent (golden values from
// src/Services/LodgeClient.cs, a hand-written "{\"t\":\"ping\"}", and server/Lodge.Server/Hub.cs at c712015) - the wire must not change.
public class ConnectionMessagesTests
{
    static TestFields Written(Action<ILodgeWriter> write) => TestFields.Written(write);

    [Fact]
    public void Ping_and_pong_round_trip_and_write_their_own_type()
    {
        Assert.NotNull(PingMsg.Read(Written(new PingMsg().Write)));
        Assert.NotNull(PongMsg.Read(Written(new PongMsg().Write)));
        Assert.Equal("ping", Written(new PingMsg().Write).Str("t"));
        Assert.Equal("pong", Written(new PongMsg().Write).Str("t"));
    }

    [Fact]
    public void Golden_ping_and_pong_match_the_old_frames()
    {
        TestFields.SameJson(JsonNode.Parse("{\"t\":\"ping\"}"), Written(new PingMsg().Write).D);
        TestFields.SameJson(new JsonObject { ["t"] = "pong" }, Written(new PongMsg().Write).D);
        // nothing but the type, so even the bytes are the same: {"t":"ping"} / {"t":"pong"}
        Assert.Equal(new[] { "t" }, Written(new PingMsg().Write).D.Keys);
        Assert.Equal(new[] { "t" }, Written(new PongMsg().Write).D.Keys);
    }
}
