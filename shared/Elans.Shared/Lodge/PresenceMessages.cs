namespace Elans.Shared
{
    // Members and presence (server/PROTOCOL.md: WebSocket, 2.5 and 2.30 additions): the field names live only here. The classes only map
    // fields; every rule (who sees whom, the masked entry of an invisible member in a voice room, what a status / note / game / position is
    // cleaned to, who may share or see a live position) stays in the Lodge server (Hub.cs, Voice.cs PresenceModule, Hub.LivePos.cs).
    // Hot path: presence goes out every few seconds and positions about once a second, so these are plain field copies - no lists, no LINQ.
    // The server still cleans "game" and "position" from the raw JSON (PresenceModule.BuildGame / BuildPosition): their strict number and
    // string checks are the validation (see server/PROTOCOL.md, "Typed (stage 2)" in the 2.30 section).

    // ---------------------------------------------------------------- the member entry

    // a member as everyone who may see them gets them: in join / user, and in welcome.users
    //   { id, name, guest, role, room, voice, muted, deaf, serverMuted, avatarId, accent, displayName, status, note, game, invisible? }
    // An invisible member's entry carries invisible: true only for themselves and Perm.SeeInvisible; a voice room's other viewers get the
    // masked entry (status "online", note "", game null) - the server decides which.
    public sealed class LodgeUser
    {
        public int Id;
        public string Name, Role, Room, AvatarId, Accent, DisplayName, Status, Note;
        public bool Guest, Voice, Muted, Deaf, ServerMuted;
        public bool Invisible;          // written only when true
        public bool HasDisplayName;     // read side: the key was there (a 2.7+ server); absent from an older one
        // what they play: the presence object passed through as it is (a raw JsonObject - see
        // ILodgeFields.RawChild); null = not shared (written as JSON null). LodgeGame.Read gives the typed view of it.
        public object Game;

        public void Write(ILodgeWriter w)
        {
            w.Set("id", Id); w.Set("name", Name); w.Set("guest", Guest); w.Set("role", Role);
            w.Set("room", Room); w.Set("voice", Voice); w.Set("muted", Muted); w.Set("deaf", Deaf); w.Set("serverMuted", ServerMuted);
            w.Set("avatarId", AvatarId); w.Set("accent", Accent); w.Set("displayName", DisplayName);
            w.Set("status", Status); w.Set("note", Note);
            if (Game != null) w.SetRawChild("game", Game); else w.Set("game", (string)null);
            if (Invisible) w.Set("invisible", true);
        }

        // null for a missing entry (a join / user without its user object)
        public static LodgeUser Read(ILodgeFields f) => f == null ? null : new LodgeUser
        {
            Id = f.Int("id"), Name = f.Str("name"), Guest = f.Bool("guest"), Role = f.Str("role"),
            Room = f.Str("room"), Voice = f.Bool("voice"), Muted = f.Bool("muted"), Deaf = f.Bool("deaf"), ServerMuted = f.Bool("serverMuted"),
            AvatarId = f.Str("avatarId"), Accent = f.Str("accent"), DisplayName = f.Str("displayName"), HasDisplayName = f.Has("displayName"),
            Status = f.Str("status"), Note = f.Str("note"), Game = f.RawChild("game"), Invisible = f.Bool("invisible"),
        };
    }

    // the game presence as the server sends it (user.game, cleaned by PresenceModule.BuildGame; every key always there)
    //   { playing, name, realm, class, classFile, level, zone, guild, race, raceFile, sex, flags, xpPct (null = not shared), rested, groupSize,
    //     inInstance, instanceName }
    // flags: 1 in combat, 2 dead / ghost, 4 AFK, 8 resting, 16 in an instance, 32 raid instance, 64 party instance, 128 in a group
    public sealed class LodgeGame
    {
        public bool Playing, Rested, InInstance;
        public string Name, Realm, Class, ClassFile, Zone, Guild, Race, RaceFile, InstanceName;
        public int Level, Sex, Flags, GroupSize;
        public int? XpPct;

        public void Write(ILodgeWriter w)
        {
            w.Set("playing", Playing);
            w.Set("name", Name); w.Set("realm", Realm); w.Set("class", Class); w.Set("classFile", ClassFile); w.Set("level", Level);
            w.Set("zone", Zone); w.Set("guild", Guild); w.Set("race", Race); w.Set("raceFile", RaceFile); w.Set("sex", Sex);
            w.Set("flags", Flags);
            if (XpPct is int xp) w.Set("xpPct", xp); else w.Set("xpPct", (string)null);      // an int, as BuildGame stored it
            w.Set("rested", Rested); w.Set("groupSize", GroupSize);
            w.Set("inInstance", InInstance); w.Set("instanceName", InstanceName);
        }

        // null when there is no game object (not shared). xpPct: only a number counts (anything else = not shared).
        public static LodgeGame Read(ILodgeFields f) => f == null ? null : new LodgeGame
        {
            Playing = f.Bool("playing"), Name = f.Str("name"), Realm = f.Str("realm"), Class = f.Str("class"), ClassFile = f.Str("classFile"),
            Level = f.Int("level"), Zone = f.Str("zone"), Guild = f.Str("guild"), Race = f.Str("race"), RaceFile = f.Str("raceFile"), Sex = f.Int("sex"),
            Flags = f.Int("flags"), XpPct = f.Num("xpPct") != null ? f.Int("xpPct") : (int?)null, Rested = f.Bool("rested"), GroupSize = f.Int("groupSize"),
            InInstance = f.Bool("inInstance"), InstanceName = f.Str("instanceName"),
        };
    }

    // ---------------------------------------------------------------- client -> server

    // status and note (and, 2.5, visibility "visible" | "invisible"; anything else leaves it unchanged): { status, note, visibility }
    public sealed class StatusMsg
    {
        public const string Type = LodgeMsg.Status;
        public string Status, Note, Visibility;

        public void Write(ILodgeWriter w) { w.Set("t", Type); w.Set("status", Status); w.Set("note", Note); w.Set("visibility", Visibility); }
        public static StatusMsg Read(ILodgeFields f) => new StatusMsg { Status = f.Str("status"), Note = f.Str("note"), Visibility = f.Str("visibility") };
    }

    // what I play (2.5): { playing, visibility, name.., zone?, flags.., instanceName?, xpPct?, rested? } or { share: false, visibility }.
    // The Outpost leaves a part out when its sharing is off (zone / instance, XP / rested) or unknown; which parts are there is said by the Has*
    // switches, so a null value is sent as null (as before) and an absent part stays absent.
    public sealed class GameMsg
    {
        public const string Type = LodgeMsg.Game;
        public bool Share = true;               // false = { share: false, visibility } (nothing else)
        public string Visibility;
        public bool Playing;
        public bool HasCharacter;               // name, realm, class, classFile, level, guild, race, raceFile, sex
        public string Name, Realm, Class, ClassFile, Guild, Race, RaceFile;
        public int Level, Sex;
        public bool HasZone; public string Zone;
        public bool HasLive;                    // flags, groupSize, inInstance (and instanceName when not null)
        public int Flags, GroupSize; public bool InInstance; public string InstanceName;
        public bool HasXp;                      // rested (and xpPct when not null)
        public int? XpPct; public bool Rested;

        public void Write(ILodgeWriter w)
        {
            w.Set("t", Type);
            if (!Share) { w.Set("share", false); w.Set("visibility", Visibility); return; }
            w.Set("playing", Playing); w.Set("visibility", Visibility);
            if (HasCharacter)
            {
                w.Set("name", Name); w.Set("realm", Realm); w.Set("class", Class); w.Set("classFile", ClassFile);
                w.Set("level", Level); w.Set("guild", Guild); w.Set("race", Race); w.Set("raceFile", RaceFile); w.Set("sex", Sex);
            }
            if (HasZone) w.Set("zone", Zone);
            if (HasLive)
            {
                w.Set("flags", Flags); w.Set("groupSize", GroupSize); w.Set("inInstance", InInstance);
                if (InstanceName != null) w.Set("instanceName", InstanceName);
            }
            if (HasXp) { if (XpPct is int xp) w.Set("xpPct", xp); w.Set("rested", Rested); }
        }

        // the typed view (the server cleans the raw message itself, PresenceModule.BuildGame); share: only a JSON false stops sharing
        public static GameMsg Read(ILodgeFields f)
        {
            var g = new GameMsg { Share = !f.IsFalse("share"), Visibility = f.Str("visibility") };
            if (!g.Share) return g;
            g.Playing = f.Bool("playing");
            g.HasCharacter = f.Has("name");
            g.Name = f.Str("name"); g.Realm = f.Str("realm"); g.Class = f.Str("class"); g.ClassFile = f.Str("classFile");
            g.Level = f.Int("level"); g.Guild = f.Str("guild"); g.Race = f.Str("race"); g.RaceFile = f.Str("raceFile"); g.Sex = f.Int("sex");
            g.HasZone = f.Has("zone"); g.Zone = f.Str("zone");
            g.HasLive = f.Has("flags"); g.Flags = f.Int("flags"); g.GroupSize = f.Int("groupSize"); g.InInstance = f.Bool("inInstance"); g.InstanceName = f.Str("instanceName");
            g.HasXp = f.Has("rested"); g.XpPct = f.Num("xpPct") != null ? f.Int("xpPct") : (int?)null; g.Rested = f.Bool("rested");
            return g;
        }
    }

    // my live map position (2.30): { mapId, fx, fy (0..1, 4 decimals), move, valid: true, scope: "everyone" | "officers" } about once a second
    // while I share, { valid: false } when there is none right now (an instance, a loading screen), { share: false } the moment I stop
    public sealed class PositionMsg
    {
        public const string Type = LodgeMsg.Position;
        public bool Share = true;               // false = { share: false }
        public bool Valid;                      // false = { valid: false }
        public int MapId, Move;
        public double Fx, Fy;
        public string Scope;

        public void Write(ILodgeWriter w)
        {
            w.Set("t", Type);
            if (!Share) { w.Set("share", false); return; }
            if (!Valid) { w.Set("valid", false); return; }
            w.Set("mapId", MapId); w.Set("fx", Fx); w.Set("fy", Fy); w.Set("move", Move); w.Set("valid", true); w.Set("scope", Scope);
        }

        // the typed view (the server cleans the raw message itself, PresenceModule.BuildPosition)
        public static PositionMsg Read(ILodgeFields f) => new PositionMsg
        {
            Share = !f.IsFalse("share"),
            Valid = f.Bool("valid"), MapId = f.Int("mapId"), Fx = f.Num("fx") ?? 0, Fy = f.Num("fy") ?? 0, Move = f.Int("move"), Scope = f.Str("scope"),
        };
    }

    // ---------------------------------------------------------------- server -> client

    // a member came online, or became visible to you: { user }
    public sealed class JoinMsg
    {
        public const string Type = LodgeMsg.Join;
        public LodgeUser User;

        public void Write(ILodgeWriter w) { w.Set("t", Type); User.Write(w.Child("user")); }
        public static JoinMsg Read(ILodgeFields f) => new JoinMsg { User = LodgeUser.Read(f.Child("user")) };
    }

    // a member's presence or voice state changed (also your own, as the server confirms it): { user }
    public sealed class UserMsg
    {
        public const string Type = LodgeMsg.User;
        public LodgeUser User;

        public void Write(ILodgeWriter w) { w.Set("t", Type); User.Write(w.Child("user")); }
        public static UserMsg Read(ILodgeFields f) => new UserMsg { User = LodgeUser.Read(f.Child("user")) };
    }

    // a member went away, or out of your sight: { id }
    public sealed class LeaveMsg
    {
        public const string Type = LodgeMsg.Leave;
        public int Id;

        public void Write(ILodgeWriter w) { w.Set("t", Type); w.Set("id", Id); }
        public static LeaveMsg Read(ILodgeFields f) => new LeaveMsg { Id = f.Int("id") };
    }

    // a member's live map position (2.30): { id, mapId, fx, fy, move, valid: true }, or { id, valid: false } when it is gone (or no longer
    // yours to see). Only ever sent to who may see it (LodgeHub.MaySeeLivePos).
    public sealed class MemberPosMsg
    {
        public const string Type = LodgeMsg.MemberPos;
        public int Id, MapId, Move;
        public bool Valid;
        public double? Fx, Fy;                  // read side: null when not a number (the dot is dropped)

        public void Write(ILodgeWriter w)
        {
            w.Set("t", Type); w.Set("id", Id);
            if (Valid) { w.Set("mapId", MapId); w.Set("fx", Fx ?? 0); w.Set("fy", Fy ?? 0); w.Set("move", Move); }
            w.Set("valid", Valid);
        }

        public static MemberPosMsg Read(ILodgeFields f) => new MemberPosMsg
        {
            Id = f.Int("id"), Valid = f.Bool("valid"), MapId = f.Int("mapId"), Fx = f.Num("fx"), Fy = f.Num("fy"), Move = f.Int("move"),
        };
    }
}
