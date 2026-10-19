namespace Elans.Shared
{
    // Every Lodge WebSocket message type: the "t" value of a JSON text frame on the Lodge socket (/ws), for the Outpost (src/Lodge,
    // src/Services/LodgeClient.cs) and the Lodge server (server/Lodge.Server). One list for both sides: a type is added here first,
    // documented in server/PROTOCOL.md, and only then used. shared/Elans.Shared.Tests/LodgeMsgTests.cs checks that every value is unique,
    // is described in PROTOCOL.md, and that no raw type string is left in either side's Lodge code.
    // Not here: the Atlas world-map WebView messages (goto, layer, init, toast, ...), which never go over the Lodge socket.
    // In the Lodge/ subfolder on purpose: shared/sync_cairn.py copies only the top-level files into the Cairn, which doesn't speak this.
    public static class LodgeMsg
    {
        // ---- connection (Hub.cs) ----
        public const string Ping = "ping";                          // client -> server: app-level keep-alive (2.x)
        public const string Pong = "pong";                          // server -> client: the answer to ping
        public const string Welcome = "welcome";                    // server -> client: the first frame - who you are, channels, members, features
        public const string Error = "error";                        // server -> client: a refusal or problem { text, code?, room? }

        // ---- members and presence (Hub.cs, Voice.cs PresenceModule, Hub.LivePos.cs) ----
        public const string Join = "join";                          // server -> client: a member came online (or became visible to you)
        public const string Leave = "leave";                        // server -> client: a member went away (or out of your sight)
        public const string User = "user";                          // server -> client: a member's presence/voice state changed
        public const string Status = "status";                      // client -> server: status, note, visibility
        public const string Game = "game";                          // client -> server: what you're playing (or share:false)
        public const string Position = "position";                  // client -> server: your live map position (2.30)
        public const string MemberPos = "memberpos";                // server -> client: a member's live map position (2.30)

        // ---- voice (Voice.cs) ----
        public const string Voice = "voice";                        // client -> server: join / leave a voice room
        public const string State = "state";                        // client -> server: muted / deafened

        // ---- chat (Chat.cs, ChatHistory.cs, ChatLfg.cs, ChatCalendar.cs) ----
        public const string Msg = "msg";                            // both: a message (client sends, server relays with id/at/from)
        public const string Edit = "edit";                          // client -> server: change your message
        public const string Edited = "edited";                      // server -> client: a message was changed
        public const string Delete = "delete";                      // client -> server: delete a message
        public const string Deleted = "deleted";                    // server -> client: a message was deleted
        public const string React = "react";                        // both: toggle a reaction / the new reaction list
        public const string Pin = "pin";                            // both: pin a message / a message was pinned
        public const string Unpin = "unpin";                        // both: unpin a message / a message was unpinned
        public const string Typing = "typing";                      // both: you are typing / someone is typing
        public const string HistoryBefore = "history:before";       // client -> server: older messages of a channel (2.18)
        public const string HistoryPage = "history:page";           // server -> client: the answer to history:before
        public const string Search = "search";                      // client -> server: search the chat history
        public const string SearchResult = "search:result";         // server -> client: the answer to search

        // ---- looking for group (Chat.cs, ChatLfg.cs) ----
        public const string Lfg = "lfg";                            // server -> client: an LFG card changed
        public const string LfgJoin = "lfg:join";                   // client -> server: sign up with a role
        public const string LfgMaybe = "lfg:maybe";                 // client -> server: sign up as maybe
        public const string LfgLeave = "lfg:leave";                 // client -> server: take your sign-up back
        public const string LfgClose = "lfg:close";                 // client -> server: the organiser closes the group
        public const string LfgCancel = "lfg:cancel";               // client -> server: the organiser cancels the group

        // ---- profiles (Profiles.cs) ----
        public const string Profile = "profile";                    // server -> client: a member's avatar / accent / display name changed
        public const string ProfileGet = "profile:get";             // client -> server: ask for a member's profile
        public const string ProfileSet = "profile:set";             // client -> server: change your profile
        public const string ProfileReset = "profile:reset";         // client -> server: (Perm.ResetProfiles) reset a member's profile
        public const string ProfileData = "profile:data";           // server -> client: a profile

        // ---- Guild Master and officer tools, devices, leaving (Admin.cs) ----
        public const string AdminMembers = "admin.members";         // both: ask for / the member list
        public const string AdminInvite = "admin.invite";           // client -> server: make a personal code
        public const string AdminInvited = "admin.invited";         // server -> client: the new personal code
        public const string AdminRole = "admin.role";               // client -> server: change a member's rank
        public const string AdminRemove = "admin.remove";           // client -> server: remove a member
        public const string AdminPurge = "admin.purge";             // client -> server: forget a member / revert their Atlas (2.22)
        public const string AdminPurged = "admin.purged";           // server -> client: what admin.purge removed
        public const string AdminChannelAdd = "admin.channel.add";  // client -> server: add a channel
        public const string AdminChannelRemove = "admin.channel.remove"; // client -> server: remove a channel
        public const string AdminInviteCreate = "admin.invite.create";   // client -> server: a one-time invite (2.10)
        public const string AdminInviteCreated = "admin.invite.created"; // server -> client: the new invite
        public const string AdminInvites = "admin.invites";         // both: ask for / the open invites
        public const string AdminInviteRevoke = "admin.invite.revoke";   // client -> server: withdraw an invite
        public const string AdminDevices = "admin.devices";         // both: ask for / every member's devices
        public const string AdminRetire = "admin.retire";           // client -> server: retire a member's old code
        public const string AdminLodgeSet = "admin.lodge.set";      // client -> server: the Lodge's name and welcome text (2.12)
        public const string LodgeInfo = "lodge.info";               // server -> client: the Lodge's name and welcome text changed
        public const string ModKick = "mod.kick";                   // client -> server: disconnect a member
        public const string ModMute = "mod.mute";                   // client -> server: server-mute a member in voice
        public const string DeviceList = "device.list";             // client -> server: your own devices (2.10)
        public const string DeviceRevoke = "device.revoke";         // client -> server: sign a device out
        public const string Devices = "devices";                    // server -> client: the answer to device.list
        public const string MemberLeave = "member.leave";           // client -> server: leave the Lodge (2.12)
        public const string MemberLeft = "member.left";             // server -> client: you have left (or this device was signed out)

        // ---- calendar (CalendarModule.cs) ----
        public const string CalCreate = "cal:create";               // client -> server: a new event (2.11)
        public const string CalEdit = "cal:edit";                   // client -> server: change an event
        public const string CalCancel = "cal:cancel";               // client -> server: cancel an event (this one or following)
        public const string CalSignup = "cal:signup";               // client -> server: sign up (going / maybe / ...)
        public const string CalLeave = "cal:leave";                 // client -> server: take your sign-up back
        public const string CalRange = "cal:range";                 // both: ask for / the events in a time range
        public const string CalEvents = "cal:events";               // server -> client: events changed

        // ---- shared flight times and learned flight paths (FlightModule.cs, Flights.cs, FlightRoutes.cs) ----
        public const string FlightsShare = "flights.share";         // client -> server: your measured flight times (2.13)
        public const string FlightsShared = "flights.shared";       // server -> client: the answer to flights.share
        public const string FlightsGet = "flights.get";             // client -> server: ask for the Lodge's flight times
        public const string FlightsData = "flights.data";           // server -> client: the Lodge's flight times
        public const string FlightsRoutesShare = "flights.routes.share";   // client -> server: your learned flight paths
        public const string FlightsRoutesShared = "flights.routes.shared"; // server -> client: the answer to flights.routes.share
        public const string FlightsRoutesGet = "flights.routes.get";       // client -> server: ask for the Lodge's flight paths
        public const string FlightsRoutesData = "flights.routes.data";     // server -> client: the Lodge's flight paths

        // ---- the Atlas pool (AtlasModule.cs, AtlasStore.cs) ----
        public const string AtlasShare = "atlas.share";             // client -> server: your Atlas observations (2.15)
        public const string AtlasShared = "atlas.shared";           // server -> client: the answer to atlas.share
        public const string AtlasGet = "atlas.get";                 // client -> server: ask for the pool (from a revision)
        public const string AtlasData = "atlas.data";               // server -> client: the pool (or what changed since a revision)

        // ---- the Cairn (CairnReceive.cs, CairnLink.cs, CairnNotices.cs) ----
        public const string AtlasSharing = "atlas.sharing";         // client -> server: your Atlas sharing on/off (answered with cairn.status)
        public const string CairnStatus = "cairn.status";           // both: ask for / whether you receive the Cairn's data
        public const string CairnJoinGet = "cairn.join.get";        // client -> server: (leaders) the Lodge's Cairn joining state
        public const string CairnJoin = "cairn.join";               // server -> client: the Lodge's Cairn joining state
        public const string AdminCairn = "admin.cairn";             // client -> server: (owner) contribute to the Cairn on/off, invite code, address
        public const string CairnNoticesGet = "cairn.notices.get";  // client -> server: ask for the Cairn's notices
        public const string CairnNotices = "cairn.notices";         // server -> client: the Cairn's signed notices
        public const string CairnNoticeSeen = "cairn.notice.seen";  // client -> server: notices closed (only an officer's or the owner's counts)

        // ---- the Armory (ArmoryModule.cs) ----
        public const string ArmoryPut = "armory.put";               // client -> server: share a character (2.16)
        public const string ArmorySaved = "armory.saved";           // server -> client: the answer to armory.put
        public const string ArmoryDelete = "armory.delete";         // client -> server: take a character (or all) off
        public const string ArmoryDeleted = "armory.deleted";       // server -> client: the answer to armory.delete
        public const string ArmoryList = "armory.list";             // client -> server: ask for the shared characters
        public const string ArmoryChars = "armory.chars";           // server -> client: the shared characters
        public const string ArmoryGet = "armory.get";               // client -> server: ask for one character
        public const string ArmoryChar = "armory.char";             // server -> client: one character
        public const string ArmoryCrafters = "armory.crafters";     // both: who can craft these recipes
        public const string ArmoryStanding = "armory.standing";     // both: everyone's standing with a faction

        // ---- shared files (FilesModule.cs) ----
        public const string FilesList = "files.list";               // client -> server: ask for the shared files (2.14)
        public const string FilesData = "files.data";               // server -> client: the shared files
        public const string FilesChanged = "files.changed";         // server -> client: a file slot changed
        public const string FilesDelete = "files.delete";           // client -> server: delete a slot
        public const string FilesRollback = "files.rollback";       // client -> server: make an older version current
        public const string FilesResult = "files.result";           // server -> client: the answer to files.list/delete/rollback

        // ---- hosted datasets (DatasetsModule.cs) ----
        public const string DatasetInfo = "dataset.info";           // client -> server: what the Lodge hosts (2.17)
        public const string DatasetData = "dataset.data";           // server -> client: the answer to dataset.info
        public const string DatasetChanged = "dataset.changed";     // server -> client: a hosted dataset changed
        public const string DatasetDelete = "dataset.delete";       // client -> server: stop hosting a dataset
        public const string DatasetResult = "dataset.result";       // server -> client: the answer to dataset.delete

        // ---- badges and the ladder (BadgesModule.cs) ----
        public const string BadgesState = "badges.state";           // server -> client: badges on/off, locked
        public const string BadgesMine = "badges.mine";             // both: ask for / your badges
        public const string BadgesSeen = "badges.seen";             // client -> server: your new badges were seen
        public const string BadgesHide = "badges.hide";             // client -> server: hide you from the ladder
        public const string BadgesMember = "badges.member";         // both: ask for / another member's badges
        public const string LadderGet = "ladder.get";               // client -> server: ask for the ladder of a season
        public const string LadderData = "ladder.data";             // server -> client: the ladder
        public const string AdminBadges = "admin.badges";           // client -> server: (owner) badges on/off

        // ---- realm status (Realms.cs) ----
        public const string RealmsStatus = "realms.status";         // server -> client: the realms' status
        public const string RealmsBack = "realms.back";             // server -> client: realms that came back up
    }
}
