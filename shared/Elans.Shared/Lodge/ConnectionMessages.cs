namespace Elans.Shared
{
    // The connection's keep-alive (server/PROTOCOL.md, 2.8 hardening): both carry nothing but their type. The rules (a client that pings
    // and then goes silent is dropped, the Outpost reconnects after 45 s without any frame once the server is known to answer) stay in
    // the Lodge server (Hub.cs) and the Outpost (Services/LodgeClient.cs). Not here: welcome (typed on its own), error (ErrorMessages.cs).

    // client -> server: an app-level keep-alive, every 20 s; answered with PongMsg. The Outpost writes it once and sends those same bytes
    // ({"t":"ping"}) every time.
    public sealed class PingMsg : LodgeEmptyMsg<PingMsg> { public const string Type = LodgeMsg.Ping; protected override string MsgType => Type; }

    // server -> client: the answer to ping
    public sealed class PongMsg : LodgeEmptyMsg<PongMsg> { public const string Type = LodgeMsg.Pong; protected override string MsgType => Type; }
}
