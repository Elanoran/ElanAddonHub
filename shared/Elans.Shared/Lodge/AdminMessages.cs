using System.Collections.Generic;

namespace Elans.Shared
{
    // Guild Master and officer tools (server/PROTOCOL.md: the 2.0 list, 2.10, 2.12 and 2.22 additions): the field names live only here.
    // The classes only map fields; every rule (who may do what, officers only below their own rank, the last-owner rule, name and channel
    // checks, invite lifetimes and use counts, what a purge removes) stays in the Lodge server (Admin.cs, Auth*.cs, Channels.cs, LodgeInfo.cs).
    // The server reads admin.purge's "atlasOnly" from the raw JSON: a missing or wrong-typed value means true there (only a JSON false
    // turns it off), which the lenient reading below can't tell from false.
    // Secrets: admin.invited's "code" (a permanent code) and admin.invite.created's "secret" (a one-time invite) go to the officer or Guild
    // Master who asked, in that one reply; admin.invites lists invites without them.
    // Not here: admin.badges (BadgesMessages.cs), admin.cairn (the Cairn's switch, CairnReceive.cs; not typed yet), admin.devices and the
    // device / leave messages (DeviceMessages.cs).

    // ---------------------------------------------------------------- client -> server

    // (Perm.ManageMembers) the member list and the channels (answered with AdminMembersMsg)
    public sealed class AdminMembersGetMsg : LodgeEmptyMsg<AdminMembersGetMsg> { public const string Type = LodgeMsg.AdminMembers; protected override string MsgType => Type; }

    // (Perm.ManageMembers) a permanent code for a new name (answered with AdminInvitedMsg, then AdminMembersMsg)
    public sealed class AdminInviteMsg
    {
        public const string Type = LodgeMsg.AdminInvite;
        public string Name, Role;

        public void Write(ILodgeWriter w) { w.Set("t", Type); w.Set("name", Name); w.Set("role", Role); }
        public static AdminInviteMsg Read(ILodgeFields f) => new AdminInviteMsg { Name = f.Str("name"), Role = f.Str("role") };
    }

    // (Perm.ManageMembers) change a member's rank; { name, role: "owner" } for someone else is the ownership transfer
    public sealed class AdminRoleMsg
    {
        public const string Type = LodgeMsg.AdminRole;
        public string Name, Role;

        public void Write(ILodgeWriter w) { w.Set("t", Type); w.Set("name", Name); w.Set("role", Role); }
        public static AdminRoleMsg Read(ILodgeFields f) => new AdminRoleMsg { Name = f.Str("name"), Role = f.Str("role") };
    }

    // (Perm.ManageMembers) remove a member (everything they stored goes with them, see the 2.22 purge)
    public sealed class AdminRemoveMsg
    {
        public const string Type = LodgeMsg.AdminRemove;
        public string Name;

        public void Write(ILodgeWriter w) { w.Set("t", Type); w.Set("name", Name); }
        public static AdminRemoveMsg Read(ILodgeFields f) => new AdminRemoveMsg { Name = f.Str("name") };
    }

    // (owner) revert one member's Atlas contributions (atlasOnly, the default), or forget a former member's stored data (answered with AdminPurgedMsg)
    public sealed class AdminPurgeMsg
    {
        public const string Type = LodgeMsg.AdminPurge;
        public string Name;
        public bool AtlasOnly = true;

        public void Write(ILodgeWriter w) { w.Set("t", Type); w.Set("name", Name); w.Set("atlasOnly", AtlasOnly); }
        public static AdminPurgeMsg Read(ILodgeFields f) => new AdminPurgeMsg { Name = f.Str("name"), AtlasOnly = !f.Has("atlasOnly") || f.Bool("atlasOnly") };   // lenient; the server reads it raw (see above)
    }

    // (Perm.ManageChannels) add a text or voice channel; max = a voice room's size (0 = no limit)
    public sealed class AdminChannelAddMsg
    {
        public const string Type = LodgeMsg.AdminChannelAdd;
        public string Name, ChannelType, MinRole;
        public int Max;

        public void Write(ILodgeWriter w) { w.Set("t", Type); w.Set("name", Name); w.Set("type", ChannelType); w.Set("minRole", MinRole); w.Set("max", Max); }
        public static AdminChannelAddMsg Read(ILodgeFields f) => new AdminChannelAddMsg { Name = f.Str("name"), ChannelType = f.Str("type"), MinRole = f.Str("minRole"), Max = f.Int("max") };
    }

    // (Perm.ManageChannels) remove a channel by id (the last text channel stays)
    public sealed class AdminChannelRemoveMsg : LodgeIdMsg<AdminChannelRemoveMsg> { public const string Type = LodgeMsg.AdminChannelRemove; protected override string MsgType => Type; }

    // (Perm.Invite, 2.10) a one-time invite link: kind "person" (name + role) or "guest" (uses = how many people may use it);
    // hours 1-720, 0 = the server's default (answered with AdminInviteCreatedMsg, then AdminInvitesMsg)
    public sealed class AdminInviteCreateMsg
    {
        public const string Type = LodgeMsg.AdminInviteCreate;
        public string Kind, Name, Role;
        public int Hours;
        public int Uses = 1;           // reading: 1 when missing or not a number (as the server always read it)
        public int Days;               // older clients: days instead of hours (read when hours is not set); written only when > 0

        public void Write(ILodgeWriter w)
        {
            w.Set("t", Type); w.Set("kind", Kind); w.Set("name", Name); w.Set("role", Role); w.Set("hours", Hours); w.Set("uses", Uses);
            if (Days > 0) w.Set("days", Days);
        }

        public static AdminInviteCreateMsg Read(ILodgeFields f) => new AdminInviteCreateMsg
        {
            Kind = f.Str("kind"), Name = f.Str("name"), Role = f.Str("role"), Hours = f.Int("hours"), Uses = f.Num("uses") != null ? f.Int("uses") : 1, Days = f.Int("days"),
        };
    }

    // (Perm.Invite) the open and recent invites (answered with AdminInvitesMsg)
    public sealed class AdminInvitesGetMsg : LodgeEmptyMsg<AdminInvitesGetMsg> { public const string Type = LodgeMsg.AdminInvites; protected override string MsgType => Type; }

    // (Perm.Invite) withdraw an invite by id (answered with AdminInvitesMsg)
    public sealed class AdminInviteRevokeMsg : LodgeIdMsg<AdminInviteRevokeMsg> { public const string Type = LodgeMsg.AdminInviteRevoke; protected override string MsgType => Type; }

    // (Perm.RetireCodes) a member's permanent code stops working (they must have a device first, unless force); answered with AdminMembersMsg
    public sealed class AdminRetireMsg
    {
        public const string Type = LodgeMsg.AdminRetire;
        public string Name;
        public bool Force;             // written only when true

        public void Write(ILodgeWriter w) { w.Set("t", Type); w.Set("name", Name); if (Force) w.Set("force", true); }
        public static AdminRetireMsg Read(ILodgeFields f) => new AdminRetireMsg { Name = f.Str("name"), Force = f.Bool("force") };
    }

    // (Perm.ManageMembers, 2.12) the Lodge's name (max 40) and welcome text (max 280 characters, 4 lines); empty clears (answered with a LodgeInfoMsg broadcast)
    public sealed class AdminLodgeSetMsg
    {
        public const string Type = LodgeMsg.AdminLodgeSet;
        public string Name, Welcome;

        public void Write(ILodgeWriter w) { w.Set("t", Type); w.Set("name", Name); w.Set("welcome", Welcome); }
        public static AdminLodgeSetMsg Read(ILodgeFields f) => new AdminLodgeSetMsg { Name = f.Str("name"), Welcome = f.Str("welcome") };
    }

    // (Perm.Moderate) disconnect a member by connection id (lower ranks only)
    public sealed class ModKickMsg
    {
        public const string Type = LodgeMsg.ModKick;
        public int Id;

        public void Write(ILodgeWriter w) { w.Set("t", Type); w.Set("id", Id); }
        public static ModKickMsg Read(ILodgeFields f) => new ModKickMsg { Id = f.Int("id") };
    }

    // (Perm.Moderate) server-mute a member in voice, or lift it (lower ranks only)
    public sealed class ModMuteMsg
    {
        public const string Type = LodgeMsg.ModMute;
        public int Id;
        public bool On;

        public void Write(ILodgeWriter w) { w.Set("t", Type); w.Set("id", Id); w.Set("on", On); }
        public static ModMuteMsg Read(ILodgeFields f) => new ModMuteMsg { Id = f.Int("id"), On = f.Bool("on") };
    }

    // ---------------------------------------------------------------- server -> client

    // the member list and every channel { members: [AdminMember], channels: [LodgeChannelView] }
    public sealed class AdminMembersMsg
    {
        public const string Type = LodgeMsg.AdminMembers;
        public List<AdminMember> Members = new List<AdminMember>();
        public List<LodgeChannelView> Channels = new List<LodgeChannelView>();

        public void Write(ILodgeWriter w)
        {
            w.Set("t", Type);
            var ml = w.List("members"); foreach (var m in Members) m.Write(ml.Add());
            var cl = w.List("channels"); foreach (var c in Channels) c.Write(cl.Add());
        }

        public static AdminMembersMsg Read(ILodgeFields f)
        {
            var r = new AdminMembersMsg();
            foreach (var m in f.List("members")) r.Members.Add(AdminMember.Read(m));
            foreach (var c in f.List("channels")) r.Channels.Add(LodgeChannelView.Read(c));
            return r;
        }
    }

    // a member on the Guild Master's list { name, role, hasCode?, devices? (2.10), online }
    public sealed class AdminMember
    {
        public string Name, Role;
        public bool? HasCode;          // still has a permanent code; null = not sent (before 2.10)
        public int? Devices;           // devices on a token; null = not sent (before 2.10)
        public bool Online;            // online and visible to the viewer

        public void Write(ILodgeWriter w)
        {
            w.Set("name", Name); w.Set("role", Role);
            if (HasCode is bool h) w.Set("hasCode", h);
            if (Devices is int d) w.Set("devices", d);
            w.Set("online", Online);
        }

        public static AdminMember Read(ILodgeFields f) => new AdminMember
        {
            Name = f.Str("name"), Role = f.Str("role"), HasCode = f.Has("hasCode") ? f.Bool("hasCode") : (bool?)null,
            Devices = f.Has("devices") ? f.Int("devices") : (int?)null, Online = f.Bool("online"),
        };
    }

    // a channel { id, name, type: "text"|"voice", minRole, max } (admin.members.channels, also welcome.channels)
    public sealed class LodgeChannelView
    {
        public string Id, Name, ChannelType, MinRole;
        public int Max;                // voice rooms: 0 = no limit

        public void Write(ILodgeWriter w) { w.Set("id", Id); w.Set("name", Name); w.Set("type", ChannelType); w.Set("minRole", MinRole); w.Set("max", Max); }
        public static LodgeChannelView Read(ILodgeFields f) => new LodgeChannelView { Id = f.Str("id"), Name = f.Str("name"), ChannelType = f.Str("type"), MinRole = f.Str("minRole"), Max = f.Int("max") };
    }

    // the new permanent code { name, role, code } - a secret, only to whoever asked
    public sealed class AdminInvitedMsg
    {
        public const string Type = LodgeMsg.AdminInvited;
        public string Name, Role, Code;

        public void Write(ILodgeWriter w) { w.Set("t", Type); w.Set("name", Name); w.Set("role", Role); w.Set("code", Code); }
        public static AdminInvitedMsg Read(ILodgeFields f) => new AdminInvitedMsg { Name = f.Str("name"), Role = f.Str("role"), Code = f.Str("code") };
    }

    // an invite as the lists show it (never with its secret)
    //   { id, kind, name ("" for a guest link), role, by, created, expires (unix ms), max, uses, used, expired, revoked, pending }
    public sealed class InviteView
    {
        public string Id, Kind, Name, Role, By;
        public long Created, Expires;
        public int Max, Uses;
        public bool Used, Expired, Revoked, Pending;

        public void Write(ILodgeWriter w)
        {
            w.Set("id", Id); w.Set("kind", Kind); w.Set("name", Name); w.Set("role", Role); w.Set("by", By);
            w.Set("created", Created); w.Set("expires", Expires); w.Set("max", Max); w.Set("uses", Uses);
            w.Set("used", Used); w.Set("expired", Expired); w.Set("revoked", Revoked); w.Set("pending", Pending);
        }

        public static InviteView Read(ILodgeFields f) => new InviteView
        {
            Id = f.Str("id"), Kind = f.Str("kind"), Name = f.Str("name"), Role = f.Str("role"), By = f.Str("by"),
            Created = f.Long("created"), Expires = f.Long("expires"), Max = f.Int("max"), Uses = f.Int("uses"),
            Used = f.Bool("used"), Expired = f.Bool("expired"), Revoked = f.Bool("revoked"), Pending = f.Bool("pending"),
        };
    }

    // the new one-time invite: the InviteView fields, then t and the secret (the only place it appears; key order as the server always sent it)
    public sealed class AdminInviteCreatedMsg
    {
        public const string Type = LodgeMsg.AdminInviteCreated;
        public InviteView Invite = new InviteView();
        public string Secret;

        public void Write(ILodgeWriter w) { Invite.Write(w); w.Set("t", Type); w.Set("secret", Secret); }
        public static AdminInviteCreatedMsg Read(ILodgeFields f) => new AdminInviteCreatedMsg { Invite = InviteView.Read(f), Secret = f.Str("secret") };
    }

    // the invites { now (unix ms), invites: [InviteView] }
    public sealed class AdminInvitesMsg
    {
        public const string Type = LodgeMsg.AdminInvites;
        public long Now;
        public List<InviteView> Invites = new List<InviteView>();

        public void Write(ILodgeWriter w) { w.Set("t", Type); w.Set("now", Now); var l = w.List("invites"); foreach (var i in Invites) i.Write(l.Add()); }

        public static AdminInvitesMsg Read(ILodgeFields f)
        {
            var r = new AdminInvitesMsg { Now = f.Long("now") };
            foreach (var i in f.List("invites")) r.Invites.Add(InviteView.Read(i));
            return r;
        }
    }

    // what admin.purge removed (counts), then t (key order as the server always sent it)
    public sealed class AdminPurgedMsg
    {
        public const string Type = LodgeMsg.AdminPurged;
        public string Name;
        public bool AtlasOnly;
        public int Atlas, Flights, Signups, Organised, Profile, Armory, Badges, Messages, Reactions, Uploads, Total;

        public void Write(ILodgeWriter w)
        {
            w.Set("name", Name); w.Set("atlasOnly", AtlasOnly); w.Set("atlas", Atlas); w.Set("flights", Flights); w.Set("signups", Signups); w.Set("organised", Organised);
            w.Set("profile", Profile); w.Set("armory", Armory); w.Set("badges", Badges); w.Set("messages", Messages); w.Set("reactions", Reactions); w.Set("uploads", Uploads);
            w.Set("total", Total);
            w.Set("t", Type);
        }

        public static AdminPurgedMsg Read(ILodgeFields f) => new AdminPurgedMsg
        {
            Name = f.Str("name"), AtlasOnly = f.Bool("atlasOnly"), Atlas = f.Int("atlas"), Flights = f.Int("flights"), Signups = f.Int("signups"), Organised = f.Int("organised"),
            Profile = f.Int("profile"), Armory = f.Int("armory"), Badges = f.Int("badges"), Messages = f.Int("messages"), Reactions = f.Int("reactions"), Uploads = f.Int("uploads"),
            Total = f.Int("total"),
        };
    }

    // the Lodge's name and welcome text changed (to everybody) { lodge: LodgeInfoView }
    public sealed class LodgeInfoMsg
    {
        public const string Type = LodgeMsg.LodgeInfo;
        public LodgeInfoView Lodge;

        public void Write(ILodgeWriter w) { w.Set("t", Type); if (Lodge != null) Lodge.Write(w.Child("lodge")); }
        public static LodgeInfoMsg Read(ILodgeFields f) { var c = f.Child("lodge"); return new LodgeInfoMsg { Lodge = c == null ? null : LodgeInfoView.Read(c) }; }
    }

    // { name, welcome } ("" = not set) - lodge.info.lodge, also welcome.lodge; public by design (the invite page shows it)
    public sealed class LodgeInfoView
    {
        public string Name, Welcome;

        public void Write(ILodgeWriter w) { w.Set("name", Name); w.Set("welcome", Welcome); }
        public static LodgeInfoView Read(ILodgeFields f) => new LodgeInfoView { Name = f.Str("name"), Welcome = f.Str("welcome") };
    }
}
