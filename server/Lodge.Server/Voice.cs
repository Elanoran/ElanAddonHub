using System.Net.WebSockets;
using System.Text.Json.Nodes;

namespace Lodge;

// Voice rooms: join/leave, mute/deafen, and the Opus relay to everyone in the same room.
public class VoiceModule : IModule
{
    const int MaxVoicePacket = 4000;
    readonly LodgeHub hub;

    public VoiceModule(LodgeHub hub) { this.hub = hub; }

    public async Task<bool> Handle(Member me, string type, JsonObject m)
    {
        switch (type)
        {
            case "voice":
                string room = (string)m["room"];
                if (m["on"] != null) room = m["on"]!.GetValue<bool>() ? room ?? hub.Channels.DefaultVoice(me.Role)?.Id : null; // 1.x hubs
                if (room != null)
                {
                    var ch = hub.Channels.Get(room);
                    if (ch == null || ch.Type != "voice" || !ch.VisibleTo(me.Role)) { await hub.Error(me, "You can't join that room"); return true; }
                    if (ch.Max > 0 && me.Room != room && hub.Members.Count(x => x.Room == room) >= ch.Max)
                    {
                        await hub.Error(me, $"{ch.Name} is full ({ch.Max})");
                        return true;
                    }
                }
                me.Room = room;
                await hub.BroadcastUser(me);
                return true;
            case "state":
                me.Muted = m["muted"]?.GetValue<bool>() == true;
                me.Deaf = m["deaf"]?.GetValue<bool>() == true;
                await hub.BroadcastUser(me);
                return true;
        }
        return false;
    }

    // client sends [opus packet]; everyone else in the same room (not deafened) gets [sender id: int32 LE][opus packet]
    public async Task Relay(Member me, byte[] packet)
    {
        if (me.Room == null || me.Muted || me.ServerMuted || packet.Length == 0 || packet.Length > MaxVoicePacket) return;
        var outBuf = new byte[4 + packet.Length];
        BitConverter.TryWriteBytes(outBuf.AsSpan(0, 4), me.Id);
        packet.CopyTo(outBuf, 4);
        var targets = hub.Members.Where(x => x.Id != me.Id && x.Room == me.Room && !x.Deaf);
        await Task.WhenAll(targets.Select(t => hub.SendRaw(t, outBuf, WebSocketMessageType.Binary)));
    }
}

// Status + note.
public class PresenceModule : IModule
{
    readonly LodgeHub hub;
    public PresenceModule(LodgeHub hub) { this.hub = hub; }

    public async Task<bool> Handle(Member me, string type, JsonObject m)
    {
        if (type != "status") return false;
        var st = (string)m["status"];
        me.Status = st is "online" or "away" or "busy" or "dungeon" or "lfg" ? st : "online";
        var note = new string(((string)m["note"] ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        me.Note = note.Length > 40 ? note[..40] : note;
        await hub.BroadcastUser(me);
        return true;
    }
}
