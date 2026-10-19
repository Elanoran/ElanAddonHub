using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Elans.Shared;

namespace Lodge;

// Voice rooms: join/leave, mute/deafen, and the Opus relay to everyone in the same room. The JSON control messages are typed in
// shared/Elans.Shared/Lodge/VoiceMessages.cs (VoiceMsg, VoiceStateMsg); the audio is binary frames (Relay) and never goes through them.
public class VoiceModule : IModule
{
    const int MaxVoicePacket = 4000;
    readonly LodgeHub hub;

    public VoiceModule(LodgeHub hub) { this.hub = hub; }

    public async Task<bool> Handle(Member me, string type, JsonObject m)
    {
        switch (type)
        {
            case VoiceMsg.Type:
            {
                var vm = VoiceMsg.Read(new LodgeJsonFields(m));        // a non-string room is "leave", a non-bool on is "not sent", as before
                string room = vm.Room;
                var on = vm.On;
                if (on != null) room = on == true ? room ?? hub.Channels.DefaultVoice(me.Role)?.Id : null; // 1.x hubs
                Channel ch = null;
                if (room != null)
                {
                    ch = hub.Channels.Get(room);
                    if (ch == null || ch.Type != "voice" || !ch.VisibleTo(me.Role)) { await hub.Error(me, "You can't join that room"); return true; }
                }
                // the cap check and the join are one step under a lock: simultaneous joins can't both take the last place.
                // An invisible member in a room is shown (masked) to that room's viewers.
                bool full = false;
                await hub.MutateIf(me, () =>
                {
                    lock (hub.RoomGate)
                    {
                        if (ch != null && ch.Max > 0 && me.Room != room && hub.Members.Count(x => x.Room == room) >= ch.Max) { full = true; return false; }
                        me.Room = room;
                        return true;
                    }
                });
                if (full) await hub.Error(me, $"{ch.Name} is full ({ch.Max})", "room-full", room);
                return true;
            }
            case VoiceStateMsg.Type:
            {
                var st = VoiceStateMsg.Read(new LodgeJsonFields(m));
                me.Muted = st.Muted;
                me.Deaf = st.Deaf;
                await hub.BroadcastUser(me);
                return true;
            }
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

// Status + note, game presence, live map position (typed: Elans.Shared PresenceMessages.cs).
public class PresenceModule : IModule
{
    readonly LodgeHub hub;
    public PresenceModule(LodgeHub hub) { this.hub = hub; }

    public async Task<bool> Handle(Member me, string type, JsonObject m)
    {
        // game and position are cleaned from the raw JSON (BuildGame / BuildPosition: their strict checks are the validation); the shapes are
        // Elans.Shared GameMsg / LodgeGame and PositionMsg / MemberPosMsg (PresenceMessages.cs)
        if (type == GameMsg.Type) { await Game(me, m); return true; }
        if (type == PositionMsg.Type) { await Position(me, m); return true; }
        if (type != StatusMsg.Type) return false;
        var sm = StatusMsg.Read(new LodgeJsonFields(m));          // a non-string status / note / visibility is "missing", as before
        var st = sm.Status;
        me.Status = st is "online" or "away" or "busy" or "dungeon" or "lfg" ? st : "online";
        me.Note = Names.Text(sm.Note, 40);                         // the names' cleaning: control, bidi and zero-width characters out
        if (!await ApplyVisibility(me, sm.Visibility)) await hub.BroadcastPresence(me);
        return true;
    }

    // optional "visibility": "visible" | "invisible" (hub 2.17+). Absent or anything else = unchanged, so old hubs never change it.
    // true when it changed (the change itself already told everyone who needs to know)
    async Task<bool> ApplyVisibility(Member me, string s)
    {
        if (s is not ("visible" or "invisible")) return false;
        bool inv = s == "invisible";
        if (inv == me.Invisible) return false;
        hub.Log.LogInformation("{Name} is now {State}", me.Name, inv ? "invisible" : "visible");
        await hub.Mutate(me, () => me.Invisible = inv);
        await hub.BroadcastLivePos(me);   // a live position follows the stricter rule of an invisible member at once (and comes back with "visible")
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
        // the cleaned object, stored on the member and passed on as user.game (its shape: Elans.Shared LodgeGame, every key always there)
        return LodgeJson.Build(new LodgeGame
        {
            Playing = Bool(m, "playing"),
            Name = Clip(m, "name", 24), Realm = Clip(m, "realm", 32), Class = Clip(m, "class", 20),
            ClassFile = Clip(m, "classFile", 20), Level = level, Zone = Clip(m, "zone"), Guild = Clip(m, "guild"),
            Race = Clip(m, "race", 24), RaceFile = Clip(m, "raceFile", 20), Sex = sex,
            Flags = flags, XpPct = xp < 0 ? null : xp, Rested = Bool(m, "rested"), GroupSize = groupSize,
            InInstance = inInstance, InstanceName = instanceName.Length == 0 ? null : instanceName,
        }.Write);
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
        bool vis = await ApplyVisibility(me, GameMsg.Read(new LodgeJsonFields(m)).Visibility);
        // a live position is never looser than the zone: no game or no zone shared any more = the position is withdrawn
        if (me.LivePos != null && !LodgeHub.MayShareLivePos(me)) await hub.BroadcastLivePos(me);
        // identical to what everyone already has: nothing to tell them (keeps a chatty addon from costing broadcasts)
        if (!vis && (before == null ? me.Game == null : me.Game != null && JsonNode.DeepEquals(before, me.Game))) return;
        if (!vis) await hub.BroadcastPresence(me);
    }

    // ---- live map position (2.30, feature "liveposition"). The client sends
    //   {"t":"position","mapId":1429,"fx":0.41,"fy":0.62,"move":0,"valid":true,"scope":"everyone"|"officers"}
    // about once a second while its member chose to share, and {"t":"position","share":false} the moment they stop. Only a personal member+ who
    // shares their zone may share; everything else is ignored. See LodgeHub.BroadcastLivePos for who sees it.
    public const int MaxMapId = 100000;
    public const int MoveMask = 4 | 8 | 16 | 32 | 64 | 128;   // flight, indoors, swimming, mounted, pet out, dead (the trail's LiveMove bits)

    static double? Frac(JsonObject m, string key)
    {
        try { if (m[key] is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d)) return Math.Round(Math.Clamp(d, 0, 1), 4); } catch { }
        return null;
    }

    // a sanitized position, or null when it is not one (share:false, valid:false, no map, no or broken fractions): null clears
    public static JsonObject BuildPosition(JsonObject m)
    {
        if (m["share"] is JsonValue sv && sv.TryGetValue<bool>(out var share) && !share) return null;
        if (!(m["valid"] is JsonValue vv && vv.TryGetValue<bool>(out var valid) && valid)) return null;
        int mapId = Int(m, "mapId", 0, MaxMapId);
        double? fx = Frac(m, "fx"), fy = Frac(m, "fy");
        if (mapId <= 0 || fx == null || fy == null) return null;
        var scope = m["scope"] is JsonValue cv && cv.TryGetValue<string>(out var c) && c == "officers" ? "officers" : "everyone";
        return new JsonObject { ["mapId"] = mapId, ["fx"] = fx.Value, ["fy"] = fy.Value, ["move"] = Int(m, "move", 0, int.MaxValue) & MoveMask, ["scope"] = scope };
    }

    async Task Position(Member me, JsonObject m)
    {
        var pos = BuildPosition(m);
        // withdrawing always goes through (it must never be lost to the budget); a new position costs its own small budget, dropped quietly when empty
        if (pos != null)
        {
            if (!LodgeHub.MayShareLivePos(me)) { NotePosition(me, "refused: " + LodgeHub.WhyNoLivePos(me)); pos = null; }
            else if (!me.Budget.LivePos.TryTake()) { me.CountDrop(hub.Clock); return; }
            else NotePosition(me, "sharing (" + ((string)pos["scope"] ?? "everyone") + ")");
        }
        else NotePosition(me, "stopped");
        if (pos == null && me.LivePos == null && me.LivePosViewers.Count == 0) return;
        me.LivePos = pos;
        await hub.BroadcastLivePos(me);
    }

    // 2.30.1: the owner's log says when someone starts or stops sharing their map position, or why the Lodge does not pass it on - once per change,
    // never a position
    void NotePosition(Member me, string note)
    {
        if (me.LivePosNote == note) return;
        bool first = me.LivePosNote.Length == 0;
        me.LivePosNote = note;
        if (first && note == "stopped") return;          // a client that never shared says nothing
        hub.Log.LogInformation("{Name}: map position {Note}", me.Name, note);
    }
}
