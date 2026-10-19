namespace Elans.Shared
{
    // Voice rooms and typing (server/PROTOCOL.md, WebSocket): the JSON control messages only - the voice AUDIO is binary frames, never
    // these classes. The field names live only here; every rule (who may join which room, the room cap, server-mute, the relay, the typing
    // coalescing and who sees it) stays in the Lodge server (Voice.cs VoiceModule, Chat.cs). Your own voice state comes back in the
    // presence message user (PresenceMessages.cs); a full room is the error frame with code "room-full" (ErrorMessages.cs).
    // Hot path: plain field copies, nothing else.

    // ---------------------------------------------------------------- client -> server

    // join a voice room, or leave with room null: { room, on }. "on" is for 1.x servers (true = the default room when room is missing,
    // false = leave); the Outpost always sends both.
    public sealed class VoiceMsg
    {
        public const string Type = LodgeMsg.Voice;
        public string Room;            // null = leave (written as JSON null)
        public bool? On;               // null = not sent; read side: null unless a JSON bool

        public void Write(ILodgeWriter w) { w.Set("t", Type); w.Set("room", Room); if (On is bool on) w.Set("on", on); }
        public static VoiceMsg Read(ILodgeFields f) => new VoiceMsg { Room = f.Str("room"), On = f.BoolOrNull("on") };
    }

    // my mute / deafen: { muted, deaf }
    public sealed class VoiceStateMsg
    {
        public const string Type = LodgeMsg.State;
        public bool Muted, Deaf;

        public void Write(ILodgeWriter w) { w.Set("t", Type); w.Set("muted", Muted); w.Set("deaf", Deaf); }
        public static VoiceStateMsg Read(ILodgeFields f) => new VoiceStateMsg { Muted = f.Bool("muted"), Deaf = f.Bool("deaf") };
    }

    // I am typing in a channel: { channel } (null = the default text channel)
    public sealed class TypingMsg
    {
        public const string Type = LodgeMsg.Typing;
        public string Channel;

        public void Write(ILodgeWriter w) { w.Set("t", Type); w.Set("channel", Channel); }
        public static TypingMsg Read(ILodgeFields f) => new TypingMsg { Channel = f.Str("channel") };
    }

    // ---------------------------------------------------------------- server -> client

    // someone is typing in a channel you can see: { id, channel } - at most once per person and channel every 2 s, never an invisible member
    public sealed class MemberTypingMsg
    {
        public const string Type = LodgeMsg.Typing;
        public int Id;
        public string Channel;

        public void Write(ILodgeWriter w) { w.Set("t", Type); w.Set("id", Id); w.Set("channel", Channel); }
        public static MemberTypingMsg Read(ILodgeFields f) => new MemberTypingMsg { Id = f.Int("id"), Channel = f.Str("channel") };
    }
}
