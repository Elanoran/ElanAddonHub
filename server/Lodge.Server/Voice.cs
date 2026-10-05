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
                await hub.Mutate(me, () => me.Room = room); // an invisible member in a room is shown (masked) to that room's viewers
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
        if (type == "game") { await Game(me, m); return true; }
        if (type != "status") return false;
        var st = m["status"] is JsonValue sv && sv.TryGetValue<string>(out var s0) ? s0 : null;
        me.Status = st is "online" or "away" or "busy" or "dungeon" or "lfg" ? st : "online";
        var note = new string((m["note"] is JsonValue nv && nv.TryGetValue<string>(out var n0) ? n0 : "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        me.Note = note.Length > 40 ? note[..40] : note;
        if (!await ApplyVisibility(me, m)) await hub.BroadcastPresence(me);
        return true;
    }

    // optional "visibility": "visible" | "invisible" (hub 2.17+). Absent or anything else = unchanged, so old hubs never change it.
    // true when it changed (the change itself already told everyone who needs to know)
    async Task<bool> ApplyVisibility(Member me, JsonObject m)
    {
        if (m["visibility"] is not JsonValue v || !v.TryGetValue<string>(out var s) || s is not ("visible" or "invisible")) return false;
        bool inv = s == "invisible";
        if (inv == me.Invisible) return false;
        hub.Log.LogInformation("{Name} is now {State}", me.Name, inv ? "invisible" : "visible");
        await hub.Mutate(me, () => me.Invisible = inv);
        return true;
    }

    static string Clip(JsonObject m, string key, int max = 40)
    {
        // a non-string value is ignored (never throws); NFKC, control/format characters out, whitespace collapsed
        var s = Names.Snippet(m[key] is JsonValue v && v.TryGetValue<string>(out var raw) ? raw : "", max);
        return s.Length == 0 ? null : s;
    }

    // what they're playing: WoW running (live) + character from the companion addon. "share": false clears it.
    // sanitized copy of a client's game message (raceFile/sex are optional pass-through fields for avatars)
    // 2.5 flag bits of "flags" (everything else is dropped): 1 in combat, 2 dead/ghost, 4 AFK, 8 resting, 16 in an instance,
    // 32 raid instance, 64 party instance, 128 in a group
    public const int FlagMask = 0xFF;
    const int FInstance = 16, FRaid = 32, FParty = 64, FGroup = 128;

    static int Int(JsonObject m, string key, int min, int max, int fallback = 0)
    {
        try
        {
            if (m[key] is JsonValue v)
            {
                if (v.TryGetValue<int>(out var i)) return Math.Clamp(i, min, max);
                if (v.TryGetValue<double>(out var d) && !double.IsNaN(d)) return (int)Math.Clamp(d, min, max);
            }
        }
        catch { }
        return fallback;
    }

    static bool Bool(JsonObject m, string key) => m[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    public static JsonObject BuildGame(JsonObject m)
    {
        int level = Int(m, "level", 0, 100), sex = Int(m, "sex", 0, 3);
        // 2.5: zone / status flags / xp / group / instance. Optional; every value bounded, flags whitelisted, and the
        // pieces made consistent (an instance name without an instance, a group flag without a group are dropped)
        int flags = Int(m, "flags", 0, int.MaxValue) & FlagMask;
        int groupSize = Int(m, "groupSize", 0, 40);
        bool inInstance = Bool(m, "inInstance") || (flags & FInstance) != 0;
        string instanceName = inInstance ? Names.Snippet(m["instanceName"] is JsonValue iv && iv.TryGetValue<string>(out var i0) ? i0 : "", 40) : "";
        if (inInstance) flags |= FInstance; else flags &= ~(FInstance | FRaid | FParty);
        if (groupSize > 0) flags |= FGroup; else flags &= ~FGroup;
        int xp = Int(m, "xpPct", -1, 100, -1);
        var g = new JsonObject
        {
            ["playing"] = Bool(m, "playing"),
            ["name"] = Clip(m, "name", 24), ["realm"] = Clip(m, "realm", 32), ["class"] = Clip(m, "class", 20),
            ["classFile"] = Clip(m, "classFile", 20), ["level"] = level, ["zone"] = Clip(m, "zone"), ["guild"] = Clip(m, "guild"),
            ["race"] = Clip(m, "race", 24), ["raceFile"] = Clip(m, "raceFile", 20), ["sex"] = sex,
            ["flags"] = flags, ["xpPct"] = xp < 0 ? null : xp, ["rested"] = Bool(m, "rested"), ["groupSize"] = groupSize,
            ["inInstance"] = inInstance, ["instanceName"] = instanceName.Length == 0 ? null : instanceName,
        };
        return g;
    }

    // the character that is being played is remembered in the member's profile (bounded, newest 12)
    void NoteCharacter(Member me)
    {
        var g = me.Game;
        if (g == null || g["playing"]?.GetValue<bool>() != true || (string)g["name"] is not { Length: > 0 } name) return;
        var ci = ProfileRules.CleanChar(name, (string)g["class"], (string)g["classFile"], (string)g["race"], (string)g["raceFile"],
            g["level"]?.GetValue<int>() ?? 0, new DateTimeOffset(hub.Clock.UtcNow).ToUnixTimeSeconds());
        if (ci != null) hub.Profiles.NoteCharacter(me.Profile, me.Personal, ci);
    }

    async Task Game(Member me, JsonObject m)
    {
        var before = me.Game;
        if (m["share"] is JsonValue sv && sv.TryGetValue<bool>(out var share) && !share) me.Game = null;
        else me.Game = BuildGame(m);
        NoteCharacter(me);
        bool vis = await ApplyVisibility(me, m);
        // identical to what everyone already has: nothing to tell them (keeps a chatty addon from costing broadcasts)
        if (!vis && (before == null ? me.Game == null : me.Game != null && JsonNode.DeepEquals(before, me.Game))) return;
        if (!vis) await hub.BroadcastPresence(me);
    }
}
