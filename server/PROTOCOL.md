# Lodge protocol

This describes the Lodge server as of 2.31.2; the sections below say which server version added what.

Base URL: `https://<site>/lodge` (or a subdomain root). Opening the base URL in a browser shows a short
"you're invited" page. All access needs an invite code: a personal one (tied to a name and a rank,
`Name:code:role` in `<data>/codes`, managed from the hub's Guild Master panel or `lodge-admin`) or the optional
shared `LODGE_CODE` (rank: guest). Send codes in the `X-Lodge-Code` header, never in a URL (query strings end
up in proxy logs); the header wins when both are present. `?code=` is refused by default
(2.8; `LODGE_ALLOW_QUERY_CODE=true` accepts it for 1.x hubs) - a request carrying `?code=` gets 401 "use the header" (not a strike).
Every credential check - `/health` with a code, `/ws`, `/files` - shares one wrong-code counter per client address
(an IPv4 address, or an IPv6 /64: 2.31.1): the 10th wrong code within 15 minutes is still 401, then the address gets 429
for 15 minutes even with a valid code. At most `LODGE_MAX_TRACKED_IPS` addresses are tracked; beyond that unknown addresses
share one counter, and while only that shared counter is blocked a valid device token still gets in.
All HTTP requests are also limited to `LODGE_HTTP_PER_MINUTE` per address (429). New codes are 128-bit (32 hex);
older shorter codes keep working. Invite links look like `https://<site>/lodge#invite=<code>` - the part after
`#` never reaches a server.

## HTTP

| Request | Auth | Result |
|---|---|---|
| `GET /` | none | invite landing page |
| `GET /health` | none, or a code | no code: `{"ok":true}` (no strike). With a code: 401 wrong / 429 blocked / `{ok, version, online}` |
| `POST /files` raw body, header `X-File-Name` (URL-encoded) | `X-Lodge-Code`, member+ | `{"id","name","size","mime","at","by"}`; 403 guests, 413 too big, 507 storage full (`LODGE_MAX_STORAGE_MB` / `LODGE_MAX_FILES`), 429 too many at once or > `LODGE_UPLOADS_PER_10MIN` per person |
| `GET /files/{id}` | `X-Lodge-Code` | the file (range requests supported); 429 `rate` over the download budget (`LODGE_DOWNLOADS_PER_HOUR`, `LODGE_DOWNLOAD_MB_PER_HOUR`: per person and hour, guests per address, chat and shared files together) |

## WebSocket `GET /ws?name=<display name>&client=<hub/2.0.0>[&vis=invisible]` + header `X-Lodge-Code`

Text frames are JSON with a `t` field. A closed socket with status 1008 (policy) carries the reason the client
shows: kicked, code removed, signed in elsewhere, lodge full - clients don't reconnect after those.

### Server → client

- `welcome` `{you, name, role, server, maxFileMb, canShareFiles, canModerate, canManage, canPin, features:["reply","react","react2","pin","profile"], reactions:[id], pins:{channelId:[pin]}, channels:[channel], users:[user], history:{channelId:[msg]}}`
  - sent on connect and again whenever the channels or your rank change (treat as a full refresh)
  - Typed (stage 2): `WelcomeMsg` in `shared/Elans.Shared/Lodge/WelcomeMessages.cs`, composed of the classes the other messages use -
    `users` (`LodgeUser`), `channels` (`LodgeChannelView`), `history` (`WelcomeHistory`: the stored records passed through as they are, read by
    the Outpost as `ChatMessage`s), `pins` (`WelcomePins`: the stored pins, read as `ChatPin`s), `calendar` (`CalWelcome`), `profile`
    (`ProfileView`), `badges` (`BadgesStateMsg`), `lodge` (`LodgeInfoView`) - plus `device` (`WelcomeDevice`, JSON null without a device token)
    and `names`. Every part may be missing from an older server and is then read as before (a 1.x server's one-list history included). Which
    channels, members, history and permission flags a rank gets stays in `Hub.cs`. The wire is unchanged, key order included (a server test
    pins the key set and order for the owner, a member on a device token and a guest).
- `join` `{user}` · `leave` `{id}` · `user` `{user}`
- `msg` `{channel, id, at (unix ms), from, fromId, text, file?:{id,name,size,mime}, replyTo?:{id,from,text,by,snippet}, reactions?:{reactionId:[name]}, edited?}`
- `edited` `{channel, id, text}` · `deleted` `{channel, id}`
- `react` `{channel, id, reaction, emoji, users:[name], by, on}` - `reaction` is the canonical id, `emoji` a representative emoji for 2.14 hubs; the complete reactor list for that reaction on that message (idempotent); `users: []` = none left
- `pin` `{channel, pin:{id, text, by, at, pinnedBy, pinnedAt, file?}}` (also re-sent when a pinned message is edited) · `unpin` `{channel, id}`
- `typing` `{id, channel}` · `error` `{text}` · `pong`
- `admin.members` `{members:[{name, role, online}], channels:[channel]}` · `admin.invited` `{name, role, code}`

`channel` = `{id, name, type: text|voice, minRole, max}` (only the ones your rank may see)
`user` = `{id, name, guest, role, avatarId?, accent?, room, voice, muted, deaf, serverMuted, status, note, game?}`
`game` = `{playing, name, realm, class, classFile, level, zone, guild, race?, raceFile?, sex?, flags?, xpPct?, rested?, groupSize?, inInstance?, instanceName?}` - what they play (null = not shared). `user.invisible: true` appears only in the entry of yourself and for ranks with `SeeInvisible` (see 2.5).
Typed (stage 2): `join` / `leave` / `user` with the `user` entry (`LodgeUser`), `status`, `game` (and `user.game`, `LodgeGame`) - see the 2.30
additions for the list of classes.

### Client → server

- `msg` `{channel, text, file?:{id}, replyTo?:id}` - upload first, then send the id. Max 2000 chars, 8 per 10 s.
- `react` `{id, emoji}` - toggles your reaction; `emoji` is one of the welcome's `reactions` ids (`ready, notready, lol, love, fight, loot, wipe, epic`); legacy emoji from 2.14 hubs are mapped (✅ and 👍 -> ready, ❌ -> notready, 😂 -> lol, ❤ -> love, ⚔ -> fight; U+FE0F optional); anything else is ignored. One per person and reaction, at most 30 people per reaction. Control budget.
- officer+ (`Perm.PinMessages`): `pin` `{id}` · `unpin` `{id, channel?}` - at most 25 pins per channel, kept in `<data>/pins.json` (atomic writes) with their own copy of text/author/time, so they outlive the history window; deleting the message unpins it. Others get an error.
- `edit` `{id, text}` (own) · `delete` `{id}` (own; officer+: anyone's) · `typing` `{channel}`
- Typed (stage 2): the chat messages are classes in `shared/Elans.Shared/Lodge/ChatMessages.cs`, used by both sides like the LFG ones:
  `msg` from the client (`ChatSendMsg`; an LFG request's `lfg` is `LfgRequest`), the stored and broadcast `msg` (`ChatMessage`, with `ChatFile`,
  `ChatReply`, `ChatReaction`, the `LfgCard` and the event card `CalCard`; one view for the live frame, the welcome's history and `history:page`),
  `edit` / `edited` (`EditMsg` / `EditedMsg`), `delete` / `deleted` (`DeleteMsg` / `DeletedMsg`), `react` both ways (`ReactMsg` / `ReactionMsg`),
  `pin` both ways (`PinMsg` / `PinnedMsg` with its `ChatPin`, also `welcome.pins`) and `unpin` both ways (`UnpinMsg` `{id, channel}` /
  `UnpinnedMsg` `{channel, id}`). Every rule stays in the server: the text cleaning (`Names.MessageText`), the 2000-character cut, the defanged
  group pings, the reply quote, who may edit / delete / pin, the reaction set and caps; the Outpost cleans what it shows again (`SafeFile.Text`,
  `SafeFile.Name`). The stored record is the frame: edits, reactions, sign-ups and purges change it in place. The server still validates an LFG
  request from the raw JSON (`Lfg.Create`). The wire is unchanged, key order included (a server test pins the key sets of `msg` - plain, reply
  with attachment, LFG and event card, after an edit - `edited`, `deleted`, `react`, `pin` and `unpin`).
- `voice` `{room}` (`room: null` leaves) · `state` `{muted, deaf}` · `status` `{status, note, visibility?}` · `ping`
- Typed (stage 2): `voice` `{room, on?}` (`VoiceMsg`; `on` is the 1.x switch, only a JSON bool counts), `state` (`VoiceStateMsg`), `typing` both
  ways (`TypingMsg` `{channel}`, `MemberTypingMsg` `{id, channel}`) are classes in `shared/Elans.Shared/Lodge/VoiceMessages.cs`, used by both
  sides like the LFG ones; your voice state comes back in `user` (presence, see the 2.30 additions). Only the JSON control messages: the voice
  audio stays binary frames. The wire is unchanged, key order included.
- `game` `{playing, name, realm, class, classFile, level, zone, guild, race?, raceFile?, sex? (2 male, 3 female), flags?, xpPct?, rested?, groupSize?, inInstance?, instanceName?, visibility?}` or `{share: false}` - rich presence (2.5 fields below)
- officer+: `mod.kick` `{id}` · `mod.mute` `{id, on}` - lower ranks only
- guild master: `admin.members` · `admin.invite` `{name, role}` · `admin.role` `{name, role}` · `admin.remove` `{name}`
  · `admin.channel.add` `{name, type, minRole, max}` · `admin.channel.remove` `{id}`
- Typed (stage 2): the Guild Master and officer messages - `mod.kick`, `mod.mute`, every `admin.*` above and in the 2.10 / 2.12 / 2.22 additions
  (`admin.members` both ways, `admin.invite` / `admin.invited`, `admin.role`, `admin.remove`, `admin.purge` / `admin.purged`, `admin.channel.*`,
  `admin.invite.create` / `admin.invite.created`, `admin.invites` both ways, `admin.invite.revoke`, `admin.retire`, `admin.lodge.set`) and `lodge.info`
  (its `lodge`, also `welcome.lodge`; a `channel`, also `welcome.channels`) are classes in `shared/Elans.Shared/Lodge/AdminMessages.cs`
  (`AdminMembersMsg`, `AdminMember`, `LodgeChannelView`, `InviteView`, `AdminPurgedMsg`, `LodgeInfoMsg`, ...), used by both sides like the LFG ones.
  `admin.invite.created` and `admin.purged` keep `t` after their fields, as before. The server still reads `admin.purge`'s `atlasOnly` from the raw
  JSON (only a JSON `false` turns it off). Not here: `admin.badges` (badges), `admin.cairn` (the Cairn), `admin.devices` (devices).

### Limits per person (personal code) or per address (guests; an IPv6 /64 is one address), kept across reconnects

chat 8 per 10 s ("Slow down" error) · other text messages burst 30 then 5/s (dropped) · voice 60 packets/s burst
150 and 24 KB/s (dropped quietly; a 20 ms Opus stream uses 50 packets and ~4 KB/s) · "typing" is passed on at most
every 2 s per channel · more than 200 refused messages in a minute closes the connection ("Too many messages").
Joins are admitted atomically up to `LODGE_MAX_USERS`; a personal code signing in again replaces its old
connection first, so it can reconnect when the lodge is full. Display names are normalized (NFKC, control and
format characters removed, whitespace collapsed, 24 characters) before any comparison; a guest whose name matches
a personal name becomes "Name (guest)".

1.x clients still work: a `msg` without `channel` goes to the default text channel, `voice {on:true}` joins the
first voice room.

## 2.3 additions and compatibility

- `msg.replyTo` is only added when the quoted id exists in the same channel's history; `snippet`/`text` are normalized
  (control characters removed, whitespace collapsed) and cut at 80 characters. `from`/`text` stay for 2.0-2.2 hubs,
  `by`/`snippet` carry the same values.
- `msg` records in history can carry `reactions` (`{emoji:[names]}`); they are rewritten with the same atomic compaction as edits.
- Older hubs ignore the unknown types (`react`, `pin`, `unpin`) and fields; new hubs hide reaction/pin UI when the
  welcome has no `features`. Unread counters and mentions are purely client-side.
- Uploads (paste, drag and drop) use the existing `POST /files` path with the same limits.

## 2.4 additions and compatibility

- Reactions are canonical ids, not emoji (hubs draw them as vector icons). The welcome has feature `react2`; a hub that doesn't see it
  (server 2.3) shows the old six emoji. Stored emoji reactions in history are rewritten to ids on load (merged, deduplicated, capped at 30,
  non-whitelisted ones dropped; idempotent), then persisted by the next compaction.
- `react` broadcasts carry `reaction` (id) and `emoji` (fallback: ✅ ❌ 😂 ❤️ ⚔️ 💰 💀 💎). A 2.14 hub shows the fallback emoji for live
  reactions but sees ids in `welcome.reactions`/history, so it should be updated.

## 2.5 additions and compatibility

**Rich presence.** The `game` message (and the `game` object of every `user`) gains optional fields, all validated and
bounded by the server (`PresenceModule.BuildGame`); a client that doesn't send them gets neutral values, an older hub
ignores them.

| field | meaning | server rule |
|---|---|---|
| `zone` | zone text | normalized (NFKC, no control/format characters), max 40 |
| `flags` | bit set: 1 in combat, 2 dead/ghost, 4 AFK, 8 resting, 16 in an instance, 32 raid instance, 64 party instance, 128 in a group | masked to those 8 bits; 16/32/64 cleared without an instance, 128 follows `groupSize` |
| `xpPct` | 0-100, or absent when the client could not read it | clamped; absent -> `null` |
| `rested` | rested XP | bool |
| `groupSize` | people in my group (0 = solo) | clamped 0-40 |
| `inInstance`, `instanceName` | inside a dungeon/raid/battleground, and its name | name dropped when not in an instance, max 40 |

Same budget as before (Control: burst 30, then 5 per s). A `game` message that sanitizes to exactly what is already stored is
not broadcast again. Nothing here is secret in the game: it is what the Hub reads from the companion's status strip.

**Appear offline.** `status` and `game` messages may carry `visibility: "visible" | "invisible"` (anything else, or absent,
leaves it unchanged - so old hubs never change it), and a hub can connect invisible with `&vis=invisible` so no join is ever
announced. No new message types: an invisible member is simply never mentioned to old or new hubs that may not see them.

- They stay connected and receive everything (messages, history, voice they join).
- Left out for everyone without `SeeInvisible`: the `welcome` `users` list, `join`/`leave`/`user` broadcasts about them, `typing`,
  status/note/game updates (stored, so they are current when the member re-appears), and the public `/health` `online` count.
- Going invisible sends a normal `leave` to those who could see them; going visible a normal `join` with current presence.
- Their messages and reactions still show their name (that is expected and the hub says so the first time).
- Voice: while in a room they appear in that room's member list to people who may see the room - a masked entry (id, name, rank,
  room, mute/deaf only; status `online`, empty note, `game: null`). They disappear again when they leave the room.
- `SeeInvisible` (owner by default, `Roles.Can`): the owner receives the full entry plus `"invisible": true`, and the Guild Master
  panel's online flag counts them; officers can't see or moderate them (`mod.*` answers "They're not online" unless they are in
  a voice room the officer can see). The invisible member's own entry carries `invisible: true` too.

## 2.6 additions and compatibility (avatars and profiles)

Old hubs ignore every new field and message; a hub that gets no `avatarId` draws its initials/class-colour default.

**Avatars.** `user` entries (welcome, join, user) carry `avatarId` (one of 24 ids `av.wolf, av.bear, av.raptor, av.owl, av.boar, av.lion,
av.serpent, av.spider, av.sword, av.shield, av.bow, av.staff, av.hammer, av.axe, av.skull, av.gem, av.potion, av.campfire, av.banner,
av.moon, av.sun, av.flame, av.fish, av.chicken`, or null) and `accent` (`gold, crimson, emerald, teal, azure, violet, rose, slate`, or null).
Avatars are drawn by the hub (vector art shipped with it); the server only stores the id - there are no uploads.
`welcome.profile` is the member's own full profile (below). A change of avatar/accent is broadcast as
`profile` `{id, name, avatarId, accent}` to everyone who can see that member.

**Profile** (`profile:data.profile`):
`{name, found, guest, role, online, restricted, avatarId, accent, about, playTimes, main, game?, chars:[{name, class, classFile, race, raceFile, level, seen, main, hidden? (self only)}], showChars? (self), visibleTo? (self)}`.
`found: false` = nobody of that name (only name and `found` are returned).

Client -> server:
- `profile:set` `{avatarId?, accent?, about?, playTimes?, main?, hidden?, showChars?, visibleTo?}` - own profile. Every field is optional (absent =
  unchanged); `null`/`""` clears avatarId, accent, main. `avatarId`/`accent` must be whitelisted ids, `showChars` is `all|main|none`, `visibleTo`
  is `everyone|officers`, `about` max 140 and `playTimes` max 40 characters (normalized like names: NFKC, control/format characters out,
  whitespace collapsed, hard cut), `main` must be one of your seen characters (case-insensitive), `hidden` a list of your seen characters
  (unknown names dropped; the main character is never hidden). Any invalid field refuses the whole message with an `error` and changes nothing.
  Rate limit: 1 per 2 s per identity (refusals count towards the flood limit), plus the Control budget. The answer is `profile:data` with your own view.
- `profile:get` `{name}` -> `profile:data` `{profile}` - another member by name (online or not; personal members only when offline).
  Respects privacy: `showChars` (all = every character except hidden ones, main = only the main character, none = no characters and no main),
  `visibleTo: officers` (anyone below officer gets only `name/role/avatarId/accent`, `restricted: true`), and invisible members: for everyone
  without `SeeInvisible` an invisible member answers exactly like an offline one (`online: false`, no `game`); an invisible guest is `found: false`.
  `game` (what they play now) is included only for visible online members - the same data their `user` entry already carries.
- owner only (`Perm.ResetProfiles`): `profile:reset` `{name, clearText?}` - avatar and accent back to the default (optionally the texts too).
  The member gets a `profile:data`, the lodge a `profile` broadcast.
- Typed (stage 2): `profile:set`, `profile:get`, `profile:reset`, `profile:data` (its `profile`, also `welcome.profile`) and the `profile`
  broadcast are classes in `shared/Elans.Shared/Lodge/ProfileMessages.cs` (`ProfileSetMsg` ... `ProfileMsg`, `ProfileView`, `ProfileChar`), used by
  both sides like the LFG ones. `game` inside a profile stays a free-form child object, passed through as the hub sent it. The server still
  validates `profile:set` from the raw JSON (a wrong-typed field refuses the message; absent and `null` differ).

**Characters** are collected server-side from `game` messages (name, class, classFile, race, raceFile, level, last seen), at most 12 per member (the
most recent kept), and stored with the profile.

**Storage.** Personal codes: `<data>/profiles.json` (atomic tmp+rename, size-capped load, every value re-validated, at most 2000 profiles). Guests:
a session-only profile on the connection, never written to disk (gone when they leave).

## 2.7 additions and compatibility (display names)

Old hubs ignore the new field and keep showing login names; a new hub talking to an old server (no `displayname` feature in
`welcome.features`) hides the field.

The **login name** (from the invite code) stays the identity: messages, history, pins, reactions, mentions on the wire and
`profile:get` all use it. A profile may add a **display name** that hubs show instead of it. `user` entries (welcome, join, user) carry
`displayName` (string, `""` = none), the `profile` broadcast is `{id, name, avatarId, accent, displayName}` and `profile:data.profile`
has `displayName` too (public, also while `visibleTo: officers`). The server has no mention logic; hubs treat `@LoginName` and
`@DisplayName` (normalized, case-insensitive) as mentioning that person.

`profile:set {displayName}` (absent = unchanged, `""`/`null` clears):
- normalized like names (NFKC, control/format characters out, whitespace collapsed); 2-24 characters after that (too long is an error,
  not a cut); only letters of any script, digits, space and `- _ ' . !`; at least one letter or digit.
- unique: refused with `That display name is taken` when its *fold* equals the fold of another person's login name (every personal
  code, plus names of connected guests) or of another member's display name (stored profiles, online or not). The fold is
  lower case, `0->o 1/i/|/!->l 3->e 5->s`, and separators (space `- _ . '`) dropped, so `E1an` and `El an` collide with `Elan`. Your own
  login name is allowed.
- guests are refused (`Guests can't set a display name`); they keep their `(guest)` naming.
- changing it is limited to 1 per 60 s per identity (kept across reconnects); saving the same name again, and refused attempts, cost
  nothing. Any refusal rejects the whole `profile:set`.
- owner `profile:reset` also clears it. A change is broadcast like avatar/accent (only to viewers who may see the member).
- Server 2.7.0 adds `"displayname"` to `welcome.features`.

## Ranks

`guest < member < veteran < officer < owner`. Permissions live in `Roles.Can`: files need member+, moderation and pinning
officer+, members and channels owner. Status is one of online | away | busy | dungeon | lfg (note max 40 chars).

## Voice (binary frames)

- Client → server: one Opus packet per frame (48 kHz mono, 20 ms), only while in a room and not muted.
- Server → client: `[sender id: int32 little-endian][Opus packet]`, sent to everyone else in the same room who
  isn't deafened. "Speaking" indicators are derived from incoming packets on the client.

## 2.8 additions (all optional for clients; older clients ignore them)

- `welcome.names`: `{ login: displayName }` for every member who set a display name (also offline ones). Profile updates
  (`profile`) keep carrying `name` + `displayName`.
- `error` may carry `code` (`rate`, `room-full`) and `room`. A full voice room answers `error` with `code: "room-full"`
  and the room id; the join is confirmed by the `user` message with your own `room`.
- Typed (stage 2): `error` `{text, code?, room?}` is one class, `LodgeErrorMsg` in `shared/Elans.Shared/Lodge/ErrorMessages.cs`: every error
  the server sends goes through `LodgeHub.Error`, the Outpost reads it in one place. `LodgeErrorMsg.Codes` lists every code the server sends
  (a shared test checks the server's calls against it): `rate`, `disabled`, `guest`, `locked`, `invalid`, `notfound`, `noreps`, `cal`,
  `cal-full`, `lfg`, `lfg-full`, `room-full` (with `room`), `last-owner`, `address`, `atlas`. Many errors carry no code (the text only).
- `ping` every 20 s from the client; the server answers `pong` and closes a pinging connection that was silent for 75 s
  with close code 1001. Clients that never ping are not closed for silence.
- Typed (stage 2): `ping` and `pong` (`{t}` only) are `PingMsg` / `PongMsg` in `shared/Elans.Shared/Lodge/ConnectionMessages.cs`. The Outpost
  writes its ping frame once and sends the same bytes, `{"t":"ping"}`, every time (the self-test checks them); the wire is unchanged.
- Guest names always end in " (guest)". `@everyone`/`@here` from below officer arrive with a zero-width space after the `@`.
- `profile:data` leaves out a character's `seen` when the member is invisible to the viewer.

## 2.9 additions (sign-up cards, older messages, search; features `lfg`, `history2`, `search`; optional for clients)

`server/Lodge.Server/Lfg.cs`, `ChatLfg.cs`, `ChatHistory.cs`. Only channels the asker's rank may see are ever looked at: a channel they
can't see answers exactly like one that doesn't exist.

**Looking for group.** A card is an ordinary chat message with `kind: "lfg"`, a structured `lfg` payload and a plain-text `text` fallback
(what older clients, history, search, pins and reply quotes show; the server rewrites it on every change).

- Client -> server, to make one: `msg` `{channel, kind: "lfg", text, lfg: {title (max 60), now?, start? (unix ms, up to 10 minutes past and
  30 days ahead; absent or `now: true` = now), note? (max 200), slots: {tank, healer, dps} (0-40 each, at least one)}}`. Anyone who may write
  in the channel; at most 3 open cards per author; `@everyone` / `@here` below officer are defanged. Chat budget. The stored and broadcast
  card is `lfg = {title, start, now, note, status: open|closed|cancelled, by, slots: {tank, healer, dps}, signups: [{n: name, r: tank|healer|dps|maybe}]}`.
  A card can't be `edit`ed (error code `lfg`).
- Client -> server, on a card (`id` = the message id; Control budget; a card that doesn't exist or isn't visible is ignored):
  `lfg:join` `{id, role: tank|healer|dps|maybe}` (one entry per person: signing up again moves you) · `lfg:maybe` `{id}` (= join as maybe,
  at most 40) · `lfg:leave` `{id}` · `lfg:close` `{id}` (the group is set) · `lfg:cancel` `{id}` (it won't happen). Close and cancel: the
  organiser or an officer (`Perm.Moderate`). A closed or cancelled card can't change.
- Server -> client: `lfg` `{channel, id, lfg, text}` to everyone who sees the channel, after every change. Refusals are `error` with code `lfg`
  ("That sign-up is closed", "Only the organiser or an officer can do that", a bad role, too many open cards, a bad card) or `lfg-full`.
- Typed (stage 2 pilot): the five client messages, the `lfg` update and the card are classes in `shared/Elans.Shared/Lodge/LfgMessages.cs`
  (`LfgJoinMsg` ... `LfgUpdateMsg`, `LfgCard`), written and read by both sides through `ILodgeFields` / `ILodgeWriter` (`LodgeFields.cs`;
  one adapter for both, `LodgeJsonFields` in `shared/Elans.Shared/Lodge/LodgeJson.cs`). The create request is a typed `msg` (`ChatSendMsg`
  with an `LfgRequest`, see the chat note above), but `Lfg.cs` still validates it from the raw JSON and builds and changes the stored card itself.

**Older messages** (scrolling up past the welcome's history):

- `history:before` `{channel, before: <message id>, beforeAt?: <unix ms of that message, used when it is gone>, limit? (1-100, default 100)}`
  (client -> server; text channels; Page budget, burst 10 then 1 per s, error code `rate`).
- `history:page` `{channel, before, messages: [oldest ... newest], hasMore}` (server -> client): the messages just older than `before`, in the
  stored `msg` shape. An unknown or invisible channel answers an empty page.

**Search:**

- `search` `{q, channel?}` (client -> server; any rank, guests too): a case-insensitive substring search over the text and attachment names of
  the text channels the asker may see (`channel` narrows it to one), newest first. `q` is normalized and cut to 64 characters; fewer than 2
  characters finds nothing. Search budget: 6 per minute (error code `rate`).
- `search:result` `{q, hits: [{channel, id, from, at, snippet}], more}` (server -> client): at most 50 hits; `snippet` is about 120 characters
  around the match (whitespace collapsed, `…` where it was cut); `more` = there were more hits than were sent.
- Typed (stage 2): `history:before` (`HistoryBeforeMsg`), `history:page` (`HistoryPageMsg`: its `messages` are the stored records passed
  through as they are, read by the Outpost as `ChatMessage`s), `search` (`SearchMsg`) and `search:result` (`SearchResultMsg` with its `SearchHit`s)
  are classes in `shared/Elans.Shared/Lodge/ChatMessages.cs`, used by both sides like the LFG ones. The server still reads `beforeAt` from the raw
  JSON (only a whole JSON number counts). The budgets, the visibility rule, the page size and the query cleaning stay in `ChatHistory.cs`. The wire
  is unchanged, key order included (a server test pins the key sets of `history:page`, its entries, `search:result` and its hits).

## 2.10 additions (one-time invites, device tokens; all optional for clients)

Detect: the welcome's `features` has `"tokens"`, `"invites"`, `"devices"` and `server` is >= 2.10.0; or `GET /features` (public, no
auth) answers `{"server":"2.10.0","features":["tokens","invites","devices"]}` (older servers answer 404). Without them, keep using codes.

### HTTP

| Request | Auth | Result |
|---|---|---|
| `GET /features` | none | `{server, features}` |
| `POST /exchange` header `X-Lodge-Invite: <invite>` (or `X-Lodge-Code: <old code>` to upgrade a device, or `invite` in the body), JSON body `{"device":"PC name","deviceId":"<random>"}` max 4 KB | the invite / code itself | 200 `{token, deviceId, device, name, role, guest, server}` (`Cache-Control: no-store`) |

Errors are `{error, reason}`: 401 `wrong` (unknown invite or wrong code - counts towards the lockout), 410 `used-or-expired` (used up, expired or
revoked - no strike), 429 `blocked` (the same per-IP lockout as wrong codes), 409 `too-many-devices` / `name-taken`, 403 `guest` (the shared
guest code can't be upgraded), 400 (nothing sent; an invite in the query string is not read), 413 (body too large), 500 `storage`.
`/ws`, `/files/*`, `/health` also accept `X-Lodge-Token: <token>` (it wins over `X-Lodge-Code`); a wrong token is 401 `{reason:"device-revoked"}` and counts towards the lockout.
A secret is never accepted in a URL. Link format: `elanshub://lodge/join#<server>|<invite>` (server = `host/path`, or `http://host:port/path`).

### WebSocket (client to server)

- `admin.invite.create` `{kind: "person"|"guest", name, role, hours (1-720; 0 = server default 168), uses (guest, 1-50)}` - owner and officer (officers: ranks below their own, and only for a name that doesn't exist yet; for an existing name the invite adds a device with that person's current rank - the Guild Master only since 2.31.2, an officer gets `error` `{code: "owner-only"}`; see the 2.31.2 additions)
- `admin.invites` · `admin.invite.revoke` `{id}` · `admin.devices` `{name?}` - owner and officer (2.31.2: an officer's `admin.devices` lists only the devices of ranks below their own - members, veterans, guests; the Guild Master sees all; your own are always in `devices`)
- `device.list` - your own devices (a guest sees only theirs) · `device.revoke` `{id}` - your own, or a lower rank's (owner: anyone's)
- `admin.retire` `{name, force?}` - owner: the person's permanent code stops working (`admin.members` entries carry `hasCode` and `devices`)

### WebSocket (server to client)

- `welcome` adds `canInvite`, `canRetire`, `device: {id, name}` (tokens only)
- `admin.invite.created` `{id, kind, name, role, by, created, expires (unix ms), max, uses, used, expired, revoked, pending, secret}` - the only place the secret appears
- `admin.invites` `{now, invites:[{...as above, no secret}]}` · `devices` / `admin.devices` `{name, now, devices:[{id, name, role, device, created, lastSeen, ip (coarse), current, online, addedBy? (2.31.2)}]}`
- A revoked device's socket is closed (1008, "This device was signed out"); clients don't reconnect after that.
- Typed (stage 2): `device.list`, `device.revoke`, `admin.devices` (the ask and the answer), `devices`, and the 2.12 `member.leave` / `member.left`
  are classes in `shared/Elans.Shared/Lodge/DeviceMessages.cs` (`DeviceListMsg`, `DeviceRevokeMsg`, `AdminDevicesGetMsg`, `DevicesMsg`,
  `AdminDevicesMsg`, `DeviceView`, `MemberLeaveMsg`, `MemberLeftMsg`), used by both sides like the LFG ones; the invite messages are in
  `AdminMessages.cs` (see the first list). A device is named by its id; no token (or its hash) is in any of them. `join` / `leave` / `user`
  (presence, carrying the member's `user` entry) are not part of this group.


## 2.31.2 additions (device links for existing members; feature `devicenotice`; optional for clients)

Detect: the welcome's `features` has `"devicenotice"`. An Outpost that doesn't see it hides the notice UI; an older Outpost ignores the new frame and field.

- **Owner only.** `admin.invite.create` `{kind: "person", name}` for a name that already exists (it adds a device to that member's account) is the
  Guild Master's alone. An officer gets `error` `{code: "owner-only", text}` and nothing is made. Officers still invite NEW people below their rank,
  and guest links, as before. Every other rule is unchanged.
- **Added by.** A device that an invite made by someone else adds to an existing member's account records who made the invite (their login
  name; `console` for a `lodge-admin` owner-recovery link) and when (the device's `created`). The device rows of `devices` / `admin.devices`
  carry it as `addedBy` (left out for a member's own device: their own invite, a link they made for themselves, an upgraded code, a new person).
- **The member is told.** When such a device is first used (`POST /exchange`), the member's other connected devices get
  `device.added` `{id, name, by, at}` - `id` the new device's id (to sign it out with `device.revoke`), `name` its device name, `by` who added it,
  `at` unix ms. When none of their other devices is connected, the Lodge keeps the notice (`<data>/device-notices`, at most 20 per member) and sends
  the same frame right after the `welcome` of their next connect from any other device (never to the new device itself). Each notice is sent once,
  then forgotten; leaving or being removed drops it. No token, invite or hash is in it.
- Typed: `DeviceAddedMsg` and `DeviceView.AddedBy` in `shared/Elans.Shared/Lodge/DeviceMessages.cs`.

## 2.11 additions (raid / event calendar; all optional for clients)

Detect: the welcome's `features` has `"calendar"` (and `server` >= 2.11.0), or `GET /features` lists it. A client that does not see it hides the calendar.
Old clients ignore every new frame and see the chat card as plain text (see below).

**Event** (`{...}`, in every frame below): `{id, series?, title (<=60), start (ms, UTC), end (ms, 0 = none), note (<=200), status: open|cancelled, by, roles (bool),
slots:{tank,healer,dps} (0-40 each; at least one when roles), signups:[{n, s: going|maybe|declined, r: tank|healer|dps|"", c (<=80)}], channel?, msg?, tz, off, rep?:{weeks, open}}`.
`roles: false` is a social event with no role slots. `rep` is on every event of a weekly series (`weeks` 0 = until cancelled).

Server to client:
- `welcome.calendar` = `{now, events:[event]}`: events still running or starting within the next 30 days (cancelled ones included).
- `cal:events` `{events:[event]}`: upserts, to everyone (a new event, a changed sign-up list, an edit, a cancellation, new dates of an open-ended series).
- `cal:range` `{from, to, events}`: the answer to a month fetch.
- the chat card: a normal `msg` with `kind: "event"`, `event: {id, title, start, end, by, status, weekly}` (a snapshot) and `text` = `[Event] Molten Core - Sat 17 Oct 18:00 UTC - Tank 0/1, Healer 0/3, DPS 0/5 - weekly - note (open the Calendar in Elan's Outpost to sign up)`.
  Changes arrive as `edited` `{channel, id, text, card: true}` (fill, cancelled), which is also what older clients show; the card itself cannot be edited by hand (`error code "cal"`).

Client to server (errors are `error` with `code: "cal"`, `"cal-full"` or `"rate"`):
- `cal:create` `{title, start, durationMin? (15-1440) | end?, roles?, slots:{tank,healer,dps}, note?, tz?, off?, repeat?: {weeks: 1-26} | {open: true}, post? (default true), channel? (default: the first public text channel)}` - member and up (guests: error). Start within -10 min / +1 year. `tz` is the creator's IANA or Windows zone id, `off` its UTC offset in minutes now.
  A repeat makes one event per week, all sharing a `series` id (a repeat of 2-26, or `open` = until cancelled, kept 26 weeks ahead). One chat card is posted (for the first date). Costs the Chat budget.
- `cal:edit` `{id, scope?: "this"|"following", ...the create fields (not repeat/channel)}` - organiser, or officer and up. `following` also changes the later dates of the series, keeping their date and giving them the new local wall-clock time. A role count below the people going in it is refused. Costs the Chat budget.
- `cal:cancel` `{id, scope?}` - organiser or officer and up; `following` also ends an open-ended series. Costs the Chat budget.
- `cal:signup` `{id, status: going|maybe|declined, role?, comment?}` - everyone, guests included. `going` needs a role (when the event has roles) with a free place; `maybe` may name a role without taking a place; one entry per person (answering again moves it; a refused move keeps the old answer). Closed once the event is over (its end, or start + 3 h) or cancelled.
- `cal:leave` `{id}` - remove your entry.
- `cal:range` `{from, to}` (ms, at most 62 days) -> `cal:range`. Costs the Page budget (burst 10, 1 per s).
- Typed (stage 2): every `cal:*` message, `welcome.calendar`, the event and the card snapshot are classes in
  `shared/Elans.Shared/Lodge/CalendarMessages.cs` (`CalCreateMsg` ... `CalEventsMsg`, `CalRangeMsg`, `CalWelcome`, `CalEvent`, `CalCard`), used by
  both sides like the LFG ones. The server still validates the event fields of `cal:create` / `cal:edit` (`Cal.Parse`) and the bounds of a
  `cal:range` request from the raw JSON: a wrong-typed count or bound is refused there, not read as 0. Events go out exactly as they are stored.

**Time zones.** Instants are UTC. A weekly series is expanded on the server in the creator's zone: date i = first local date + 7 i days at the same local wall-clock time, converted back to UTC, so a 20:00 raid stays 20:00 across a DST change (the UTC instant moves by an hour). A time that does not exist (spring gap) moves forward an hour, an ambiguous one takes its first occurrence. An unknown `tz` falls back to the fixed `off`, and without it UTC. Viewers always see their own local time; the stored zone is only used for expansion.

## 2.12 additions (starting a lodge, inviting from the app, leaving)

Detect: the welcome's `features` has `"leave"` / `"lodgeinfo"`, or `GET /features` (public) lists them; `GET /features` also carries `lodge: {name, welcome}`.

**Owner claim (a new lodge).** `sudo lodge-admin claim-owner` (and `install.sh` at the end of a fresh install) writes an invite of kind `owner-claim`
(role owner, 1 hour, one use) and prints the normal link `elanshub://lodge/join#<server>|<invite>` to the console only. It exists only while the lodge has
no owner. `POST /exchange` takes the same invite header plus **`"name"`** in the body: the name the person wants (Names rules). Without a valid name the answer is
400 `{reason: "name-required"}` and the link is NOT used up. A claim creates the owner (a retired code marker, no permanent code) and a device token;
409 `owner-exists` (an owner appeared first; the link is burned), 409 `name-taken`. `sudo lodge-admin recover-owner NAME` / `claim-owner --recover` make the same kind of
link for a locked-out owner: it only adds a device for an existing owner (409 `not-owner` for anyone else).

**Lodge name and welcome text.** `admin.lodge.set` `{name (max 40), welcome (max 280 chars, 4 lines)}` - owner; both are cleaned like names (control and bidi characters out)
and an empty value clears it. The server answers every client with `lodge.info` `{lodge: {name, welcome}}`; the welcome frame carries `lodge`.

**Leaving.** `member.leave` - any member: the server removes their codes entry, all their devices and their pending device links (messages stay), sends `member.left`,
closes their sockets. The last Guild Master is refused: `error` `{code: "last-owner", text: "Transfer ownership first"}`. A guest only signs the device out.
Transfer = `admin.role` `{name, role: "owner"}` for someone else (owner). The last-owner rule is one function (`Auth.EnsureOtherOwner`) used by leave, `admin.role`,
`admin.remove` and `lodge-admin role|remove` (`--force` from the console only).

**Web invite page.** `GET /` shows the lodge name and welcome. A web link is `https://<server>/#join=<server>|<invite>` (e.g. `https://example.org/lodge/#join=example.org/lodge|i1....`):
the fragment never reaches the server; an inline script (CSP nonce) turns it into the `elanshub://` link behind an "Open in the Outpost" button. Headers: strict CSP
(`default-src 'none'`, nonce script and style, `frame-ancestors 'none'`), `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, `Cache-Control: no-store`.

## 2.13 additions (shared flight times; optional for clients)

Feature `flights` (welcome `features` and `GET /features`). A client on an older server sends nothing.

- `flights.share` `{legs: {"<Horde|Alliance>|<idA>><idB>": {t: seconds (1-1800), n: samples (1-50)}}, names?: {id: name}}` - member and up, at most 500 legs (and 300 names) per message, at most one per 10 minutes per identity, frame limit 64 KB. Answer `flights.shared` `{ok, accepted, rejected, why?}`; `why` is `guest` (guests cannot contribute), `rate`, `empty` or `save`. Each identity's latest value per leg replaces its previous one.
- `flights.get` `{}` - answer `flights.data` `{at (unix s), legs: {key: {t, n}}, names: {id: name}}`: per leg the weighted average over contributors with n summed and capped at 50, outliers (+-40 % from an aggregate with n >= 5) held back until another outlier confirms them. Guests may read. Page budget.

### Learned flight paths (feature `flightroutes`; optional for clients; the server version is set at release)

Forever flies some flight paths differently from the client's files; the Outpost records the real line and shares it here. A client sends nothing unless the welcome lists `flightroutes`, and uploads only while the member shares flight times.

- `flights.routes.share` `{routes: {"<fromNode>><toNode>": {m: continent, p: [x, y, z|null, ...] (2-64 points, world yards), t: seconds (0 or 5-1800), n: agreeing flights (1-50)} | {m, g: 1}}}` - member and up, at most 100 routes per message, one per 10 minutes per identity (`why: rate`), frame limit 64 KB. `{m, g: 1}` = "my newest flight of this leg flew the game's route again" (kept only for a leg somebody reported). The server checks: numeric node ids (1-9999999, different), |x|, |y| <= 20000, |z| <= 5000, straight distance >= 50, length <= 3 x straight + 2000 and <= 30000, speed 3-100 yards/s when t > 0. Answer `flights.routes.shared` `{ok, accepted, rejected, why?}` (`guest`, `rate`, `empty`, `save`). Each identity's latest entry per leg replaces its previous one; 3000 legs, 500 per identity, 20 contributors per leg, 120 days.
- `flights.routes.get` `{}` - answer `flights.routes.data` `{at (unix s), routes: {key: {m, p (48 points), t, n, k}}}`: a leg is published when contributions that agree with each other (both resampled to 32 points; mean distance <= 25 and largest <= 75 yards, both ways) add up to n >= 2 (one member's two agreeing flights, or two members), unless as many members said `g: 1` after the newest of them. `p` = the average of the agreeing lines, `t` = their n-weighted time, `k` = how many members. Guests may read. Page budget. The client checks every route again (the ends near its own flight masters, a sane length, that it differs from its game route) before using it.
- Typed (stage 2): the flight-time and the learned-route messages are classes in `shared/Elans.Shared/Lodge/FlightMessages.cs` (`FlightsShareMsg`,
  `FlightsSharedMsg`, `FlightsDataMsg`, `FlightsRoutesShareMsg`, `FlightsRoutesSharedMsg`, `FlightsRoutesDataMsg`, the two `...GetMsg`; a leg is a
  `FlightLeg`, a route a `FlightRoute`; the maps keep their order), used by both sides like the LFG ones. Their numbers are strict (missing or not a
  JSON number = null, a route's points null unless every entry is a number or null) because both ends validate with them: the server still checks
  `flights.share` / `flights.routes.share` from the raw JSON (`Flights.TryLeg`, `FlightRoutes.Parse`), the Outpost re-checks `flights.data` /
  `flights.routes.data` (`FlightShare.ParseServer`, `LearnedFlights.ParseServer`).
- A member who leaves or is removed takes their routes along (counted with `flights` in `admin.purged`). Not offered to the Cairn (yet).

## 2.14 additions (shared files; optional for clients)

Feature `files` (welcome `features` and `GET /features`; absent when the owner set `LODGE_FILES_ENABLED=0`). Welcome also carries `canManageFiles` (officer and up) and `filesMaxMb`.

A slot is `{id, name (1-60), createdBy, versions: [{v, fileName, size, sha256, uploadedBy, at (ms), note (0-200)}]}`, versions ascending, at most the newest 3 kept; version numbers are never reused.

- `files.list` `{}` -> `files.data` `{maxMb, totalMb, usedBytes, keep, canManage, slots: [...]}` (everybody; Page budget).
- `files.delete` `{slot}` and `files.rollback` `{slot, v}` (officer and up; rolling back removes the versions newer than `v`) -> `files.result` `{op, ok, slot, why?}`, `why`: `forbidden`, `missing`, `disabled`, `already-newest`, `save`.
- Every change is announced to everyone: `files.changed` `{action: new|version|rollback|delete, slot, name, by, at, v?}`; clients refresh with `files.list`.
- Upload (HTTP, same credential headers as `/files`, officer and up): `PUT /slots/new` (header `X-Slot-Name`) or `PUT /slots/{id}` (a new version) with headers `X-File-Name`, `X-Note` (URL-encoded), `X-Sha256` (hex of the whole body), a `Content-Length`, the raw body. Answer 200 with the slot JSON, or `{error, reason}` with 400 (`sha-mismatch`, `length`, `bad-sha`, `bad-name`, `empty`), 403 (`forbidden`, `disabled`), 404 (`missing`), 409 (`name-taken`), 411, 413 (`too-big`), 415 (`blocked-type`, `blocked-zip`), 429 (`rate`, `busy`), 507 (`full`, `too-many`).
- Download: `GET /slots/{id}/{v}` (everybody, guests included) - the raw bytes as an attachment; the client checks size and SHA-256 against the list. 429 `rate` over the download budget (the same one as `GET /files/{id}`).
- Typed (stage 2): the socket messages are classes in `shared/Elans.Shared/Lodge/FileMessages.cs` (`FilesListMsg`, `FilesDeleteMsg`,
  `FilesRollbackMsg`, `FilesDataMsg`, `FilesChangedMsg`, `FilesResultMsg`; a slot is a `FileSlot`, a version a `FileVersion` - the upload's
  HTTP answer is the same `FileSlot`), used by both sides like the LFG ones. The bytes stay on HTTP; the rules (who may, the limits) stay in
  `FilesModule`.

## 2.15 additions (the Atlas pool; optional for clients)

Feature `atlas` (welcome `features` and `GET /features`; absent when the owner set `LODGE_ATLAS_ENABLED=0`). A client on an older server sends nothing. Members pool what Elan's Toolkit observed (see ARCHITECTURE.md, `atlas.obs` v1); everybody downloads the aggregate. Ids are positive whole numbers (npc, object, quest 1-9999999; item 1-999999; uiMapId 1-99999), JSON object keys carry them as plain decimal text, `x`/`y` are map fractions 0..1 (rounded to 3 decimals), `at` is unix seconds (after 2001, at most 1 day ahead).

- `atlas.share` `{kills?, objects?, fishing?, vendors?, quests?, finds?, spawns?, items?, seenNpcs?, seenObjects?, seenQuests?}` - member and up (guests: `why: "guest"`), a burst of 3 then one per 2 minutes per identity, frame limit 64 KB. Per message at most 200 kill sources, 100 objects, 50 fishing zones, 60 vendors, 200 quests, 500 finds, 200 spawn npcs (20 points each), 200 revealed items. An entry that fails any check is refused as a whole (`rejected`); a malformed message never throws and an empty one (`why: "empty"`) costs no token.
  - `kills` `{npc: C}`, `objects` `{obj: C}`, `fishing` `{uiMapId: C}`, with `C = {n, items?: {item: {n, c}}, z?: {uiMapId: n}}` (fishing has no `z`): **cumulative** totals of THIS member since its first record - `n` looted kills / opens / casts, per item `n` times dropped and `c` total count, per zone the kills there. Checks: `n` 0..100000 (a new source needs 1+), item `n` <= `n` x 10, `c` between `n` and `n` x 200, at most 150 items and 40 zones per source, zones add up to no more than `n`, one member's counters total at most 1000000, at most 6000 / 3000 / 400 sources per member.
  - `vendors` `{npc: {at, z, x, y, items: {item: {p (copper 0..100000000), s (stack 1..1000), l (limit -1..1000)}}}}` (<= 300 items) - the newest `at` per npc wins.
  - `quests` `{quest: {at, rewards?: [item...], choices?: [item...]}}` (<= 10 each) - the newest `at` wins.
  - `finds` `[{i item, c count 1..1000, src: "npc:N"|"obj:N"|"vendor:N"|"quest:N"|"fish", at, z?, x?, y?, gb?}]` - the newest 2000 are kept, one member at most 700 of them; the same find twice is stored once. `gb` (optional, 1..9,999,999) = the game build it was found in; a bad value drops only the build; it is not part of the find's identity.
  - `spawns` `{npc: [{z, x, y}]}` - up to 50 points per npc (one member at most 10); a point within 0.01 of a known one in the same zone is the same point.
  - `items` `{item: {n name (<= 80), q 0..7, il?, rl?, cls?, sub?, slot?, st?: [[statType, value]] (<= 10)}}` - details of an item the client revealed; the first description of an item stays.
  Answer `atlas.shared` `{ok, accepted, unchanged, rejected, rev, epoch, why?}`; `why`: `guest`, `rate`, `empty`, `disabled`, `save`.
- **Gathering nodes (sub-feature `atlas3`, server 2.18.0)**: a server that also takes gathering node positions advertises `atlas3` next to `atlas` (welcome `features` and `GET /features`; absent when `atlas` is). A client sends `nodes` only to a server that lists `atlas3`; an older server would ignore the field, an older client never sends it.
  `nodes` = `{ "<objectId 1..9999999>": { "<uiMapId 1..99999>": [ { "x": 0..1, "y": 0..1, "n": 1..100000 } ] } }`: where a member looted an object (x / y fractions of the map, rounded to 4 decimals, n = times looted there, CUMULATIVE like the counters). An object entry is refused whole (`rejected`) when any part is malformed;
  at most 50 objects and 1000 points per message, 40 maps per object, 150 points per object and map and member, 400 objects and 20,000 points per member. A point of the member's own within 0.008 (Euclidean) of a stored one is the same node: `n` = max(stored, sent) (idempotent, a lower value is `unchanged`).
  The `atlas.data` answer has `nodes` `{ "<objectId>": { "<uiMapId>": [ { x, y, n } ] } }` = the merge for everybody: nodes of one object on one map within 0.008 are ONE node (greedy, most looted first; the position of the heaviest point), `n` = per member the larger `n`, summed over members,
  at most 300 nodes per object and map (most looted first). One object's record is replaced as a whole when its revision moves. Storage: `members.<identity>.nodes` and `nodeRev` in `atlas.json`. A member's nodes stay when they leave, like the rest of their pool data.
- **Seen details (sub-feature `atlas2`)**: a server that also takes what members' game clients have SEEN (creature / game object / quest caches) advertises the feature `atlas2` next to `atlas` (welcome `features` and `GET /features`; always together, absent when `atlas` is). A client sends the three sections below only to a server that lists `atlas2`; an older server (no `atlas2`) would ignore them, an older client simply never sends them and its other sections work as before. The sections are named `seenNpcs` / `seenObjects` / `seenQuests` because `objects` and `quests` already carry loot counters and quest rewards. Each entry is refused whole when it fails a check (`rejected`); a section of the wrong shape counts as one rejected; strings must be text without control characters (newline and tab only in `objectives`), at most the stated length.
  - `seenNpcs` `{npc: {name (1..80), title? (<= 80), type? 0..100, family? 0..1000, rank? 0..20}}` - at most 300 per message, 40000 in the pool.
  - `seenObjects` `{obj: {name (1..80), type? 0..1000}}` - at most 300 per message, 40000 in the pool.
  - `seenQuests` `{quest: {title (1..160), level? -1..255 (-1 = any level), minLevel? 0..255, objectives? (<= 1000 chars)}}` - at most 100 per message, 20000 in the pool.
  - Merging (not cumulative, no per-member state): the first good description of an id stays; a later one only fills fields that are still empty (empty title / objectives, type / family / rank / level / minLevel of 0) and counts as `accepted` when it filled something, otherwise `unchanged`. A new id past the cap is `rejected`, an existing id can still be filled. Same share budget and frame limit as the rest of `atlas.share` (a seen-only message counts as content; an empty section is `why: "empty"`).
  - `atlas.data` carries them as `seenNpcs` / `seenObjects` / `seenQuests` (always present, possibly `{}`; each record complete: npc `{name, title, type, family, rank}`, object `{name, type}`, quest `{title, level, minLevel, objectives}`), revision-ordered and paged together with everything else, included in `full` answers and persisted in `atlas.json` (`seenNpcs` / `seenObjects` / `seenQuests`, each record with its revision `r`). Clients that do not know the sections ignore the keys.
- `atlas.get` `{since?, epoch?}` - everybody (guests too), Page budget. Answer `atlas.data` `{epoch, rev, at, full?, more?, kills, objects, fishing, vendors, quests, spawns, items, finds, seenNpcs, seenObjects, seenQuests}`: every record changed after revision `since`, in revision order, cut into pages of about 200 KB (`more: true` -> ask again with `since` = the answer's `rev`). `kills`/`objects`/`fishing` records are the aggregate over all members `{n, m (members), items: {item: {n, c}}, z}` and replace the client's record of that source; `vendors`/`quests`/`items` are as shared, `spawns` is `{npc: [{z, x, y}]}` (the whole list), `finds` is `[{s (revision), i, c, src, at, by (display name, or the login name), z?, x?, y?, gb?}]` (the client keeps the newest 2000 by `s`). `full: true` (first page only): the client's state cannot be continued (`since` 0, another `epoch`, or a `since` beyond this server's revision, e.g. after a restore) - replace everything held.
- Idempotency: nothing is added up on the server. Per member and per field the stored value is max(stored, received); the aggregate is the sum over members of those. Resending a batch, a lost answer, or an old upload record cannot count anything twice, and a lower value never lowers a total.
- Typed (stage 2): the envelopes are classes in `shared/Elans.Shared/Lodge/AtlasMessages.cs` (`AtlasShareMsg`, `AtlasSharedMsg`, `AtlasGetMsg`,
  `AtlasDataMsg`; `AtlasSections.All` is the one list of section names, in the order `atlas.data` writes them), used by both sides like the LFG
  ones. The sections themselves are free-form: each has its own shape and strict validator, so they pass through as raw children
  (`ILodgeFields.RawChild` / `RawList`): the server still checks `atlas.share` from the raw JSON (`AtlasStore.HasContent` / `Share`) and reads
  `atlas.get`'s `since` raw (only a whole JSON number above 0 counts); the Outpost re-checks every entry of `atlas.data` (`LodgeAtlas.Apply`,
  `AtlasObs.Parse*`). `atlas.data` keeps its key order (envelope, every section, `seenMembers`, `full?`, `more?`).

## 2.16 additions (the Armory; optional for clients)

Feature `armory` (welcome `features` and `GET /features`; absent when the owner set `LODGE_ARMORY_ENABLED=0`). A client on an older server sends nothing. Members choose characters on their own PC and share a snapshot of each (gear with item links, stats, talents, professions, hunter pets - never bags, bank, gold or mail); everybody in the lodge can browse them. Members and up only: guests neither share nor browse.

- `armory.put` `{key, char}` - `key` is the character's `Realm-Name` (3-80 characters, a dash in it); `char` `{name, realm?, class (file name, upper case: HUNTER), classText?, race?, raceFile?, sex? (2|3), faction?, guild?, level (1-100), ilvl?, at, gear?: {at, ilvl?, slots: {"1": {i, q, ic?, il?}}, links?: {"<itemId>": "<item link of a worn item>"}, stats?: {health, power, str, agi, sta, int, spi, armor, ap, rap, crit, rcrit, dodge, parry, block, speed, powerType, melee?: {min, max, speed}, ranged?}}, talents?: {at, api?, spent, unspent, build? ("ETB1-..."), nodes: [{id, r, m?, s?, ic?, x?, y?, n?}]} (only nodes with points), professions?: {at, list: [{name, skill, max, ic?, kind: primary|secondary}]}, pets?: {at, active?, pets: [{key, name, family?, familyId?, loyalty, loyaltyText?, level, stable, happiness, ic?, tpTotal?, tpSpent?, abilities: [{id?, name?, rank}]}], known?: {abilityId: rank}}}`. Rebuilt field by field by the server, at most 48 KB per character, 30 characters per member (1500 per lodge), a burst of 30 then one per 8 seconds per identity. Answer `armory.saved` `{ok, key?, stored, why?}`; `stored` is false when it was identical to what the lodge already had; `why`: `guest`, `disabled`, `invalid`, `empty`, `size`, `rate`, `full`, `save`.
- `armory.delete` `{key}` or `{all: true}` - always the caller's own characters (the owner is the connection's identity, any `owner` in the frame is ignored); deleting what is not there is fine. Answer `armory.deleted` `{ok, removed, key?, why?}`.
- `armory.list` `{}` -> `armory.chars` `{chars: [{owner, by, key, up, mine?, name, realm?, class, classText?, race?, raceFile?, sex?, faction?, guild?, level, ilvl?, at, professions: [{name, skill, max}]}]}` (Page budget). `owner` is the member's identity key (use it for `armory.get`), `by` the name the lodge sees (display name, else the login), `up` when the server stored it (ms), `mine` is true for the caller's own.
- `armory.get` `{owner, key}` -> `armory.char` `{owner, by, key, char}` (the full snapshot as stored) or an error with code `notfound` (Page budget). Errors for the feature: code `guest` (the shared code), `disabled`, `rate`.
- Typed (stage 2): every Armory message (including `armory.crafters` and `armory.standing` below) is a class in
  `shared/Elans.Shared/Lodge/ArmoryMessages.cs` (`ArmoryPutMsg` ... `ArmoryStandingMsg`; a list line is an `ArmorySummary`, the rows an `ArmoryCrafter` /
  `ArmoryStanding`), used by both sides like the LFG ones. The character sheet (`char` of `armory.put` / `armory.char`) stays a free-form child passed
  through as it is: the server rebuilds it from the raw JSON (`Armory.Clean`), the Characters page reads it with its own checks. The server still
  validates `armory.crafters`' spells and `armory.standing`'s faction from the raw JSON (whole JSON numbers only); the Outpost reads the spells of an
  `armory.crafters` answer from the raw frame the same way.
- **Learned recipes and "who can craft this" (sub-feature `armory2`, server 2.19.0)**: `armory2` is advertised next to `armory`. A client sends the extra fields only to a server that lists it (an older server would drop them). In `armory.put`'s `char.professions`: `list[].line?` (skill line id, 1-1000000) and `known?: {"<skillLineId>": {at, partial, n?, ids: [spellId, ...]}}`. The server keeps `known` only as positive whole numbers: at most 12 professions and 2,000 ids per character in total (sorted, no duplicates); a missing `partial` counts as partial; if the snapshot would exceed 48 KB with it, `known` is dropped and the character is still stored. `armory.list` summaries are unchanged. `armory.crafters` `{spell}` or `{spells: [..]}` (1-16 positive integers; Page budget; members and up) -> `armory.crafters` `{spells: [..], who: [{spell, owner, by, key, name, class, level, profession, skill, max, partial, mine?}]}` (best skill first, at most 80 rows; `profession`/`skill`/`max` come from the character's profession list entry with the same `line`, else the scan's own name and 0). Errors: `invalid` (no usable spell), `guest`, `disabled`, `rate`.
- A member who leaves or is removed takes their characters with them.

## 2.17 additions (hosted datasets: the Classic baseline; optional for clients)

Feature `classic` (welcome `features` and the welcome field `canShareClassic`; absent when the owner set `LODGE_CLASSIC_ENABLED=0`). The lodge stores one small reference dataset once and every member's Outpost fetches it from the lodge instead of the internet. The mechanism is generic (`DatasetsModule`: a fixed list of dataset names, each with a size limit and a cheap check of its bundle); `classic` is the first and only one. A client on an older server sends nothing and uses the public source.

The content is a single file (for `classic`: a zip holding the ORIGINAL upstream files unchanged - the `ClassicDB_*.sql.gz` dump, `LICENSE.md`, `COPYRIGHT.md`, `AUTHORS`, `source.json` and `updates/*.sql`). The server never executes or parses it; each Outpost unpacks it and builds its own index, so an Outpost of any version works and an index change never needs a server change.

- Who: members and up (member, veteran, officer, owner) read and download; **officers and the owner** upload, replace and delete. Guests (the shared code) do neither.
- `PUT /datasets/{name}` (HTTP, the usual `X-Lodge-Token` / `X-Lodge-Code`): headers `X-Version` (URL-encoded, 1-200 characters, opaque to the server: the client's idea of "which upstream release"), `X-Sha256` (64 hex), `Content-Length` required; the body is streamed to a `.part` file and never held in memory. Refused with `{error, reason}`: 404 `unknown` (a name not on the server's list), 403 `disabled` / `forbidden`, 400 `bad-sha` / `bad-version` / `empty` / `length` (shorter or longer than announced) / `sha-mismatch`, 411 `length`, 413 `too-big` (`LODGE_CLASSIC_MAX_MB`, 40), 415 `bad-bundle` (not a zip, a name that doesn't belong, two dumps, a dump that isn't gzip, a licence file missing...), 429 `rate` (5 uploads per 10 minutes) / `busy`, 500 `save`. An upload replaces the previous one atomically (content under a server-generated name, index by tmp + rename, then the old content goes); a failed one changes nothing. Answer: `{name, available, version, sha256, size, uploadedBy, at}`.
- `GET /datasets/{name}`: the bytes as an attachment (`application/octet-stream`, `nosniff`, `no-store`), answered with `X-Sha256` and `X-Version` (URL-encoded). 403 for guests, 404 `missing`, 429 `rate` (`LODGE_CLASSIC_DOWNLOADS_PER_HOUR`, 6 per member per hour).
- `dataset.info` `{name}` -> `dataset.data` `{name, available, version?, sha256?, size?, uploadedBy? (display name), at?, canManage, maxMb}` (Page budget). Errors: code `guest`, `disabled`, `rate`.
- `dataset.delete` `{name}` (officers and the owner) -> `dataset.result` `{op: "delete", name, ok, why?}`; `why`: `forbidden`, `missing`, `disabled`, `unknown`, `save`.
- `dataset.changed` `{name, action: new|replace|delete, available, version?, sha256?, size?, uploadedBy?, at?}` goes to every member (not to guests) when it changes.
- Outposts compare `version` strings (`<commit date>|<dump name>|<commit>` for `classic`) to decide whether to fetch, and check the downloaded bytes against `sha256` before unpacking.
- Typed (stage 2): the socket messages are classes in `shared/Elans.Shared/Lodge/DatasetMessages.cs` (`DatasetInfoMsg`, `DatasetDeleteMsg`,
  `DatasetDataMsg`, `DatasetChangedMsg`, `DatasetResultMsg`; the shared fields are a `DatasetView`, also the HTTP upload's answer), used by both
  sides like the LFG ones. `dataset.data` and `dataset.changed` keep their key order: the view's fields first, then `t` and their own.

## Realm status (feature `realms`; the server version is set at merge, 2.20.0 here; optional for clients)

The Lodge asks the Blizzard Game Data API (client credentials from its own environment, never sent to a client) and pushes the status of the WoW Forever realms. Feature `realms` is in the welcome `features` only while `LODGE_BNET_CLIENT_ID` and `LODGE_BNET_CLIENT_SECRET` are set. Nothing is asked of the server: the client sends no message for this. Everybody connected (members and guests: it is public information) receives:

- `realms.status { available, region, realms: [ { id, name, slug, ruleset, status, namespace, population?, hasQueue? } ], checkedAt, why? }` right after the welcome (once the first poll has finished), whenever something changes, and as a heartbeat every 5 minutes so "checked N min ago" stays honest. `status` is `UP`, `DOWN` or `UNKNOWN`; `ruleset` is `Normal`, `PvP`, `RP`, `RP-PvP`, `Hardcore` or `""` (from the realm's type, with hints in its name or category winning); `id` is the connected realm id; `checkedAt` is unix seconds of the last poll that reached the API. `available: false` means the Forever realms were not found (`why: "Forever realms not found in the Blizzard API"`) or the API could not be reached for three polls in a row (`why` says so); `realms` is then empty. After three failed polls known realms are sent as `UNKNOWN`.
- `realms.back { realms: [names], checkedAt }`, once, when every realm that was down is up again. The client decides whether to say anything (the Outpost: only if the member turned that on).
- Typed (stage 2): both are classes in `shared/Elans.Shared/Lodge/RealmMessages.cs` (`RealmsStatusMsg` with its `RealmView` rows, `RealmsBackMsg`),
  used by both sides like the LFG ones (a row is written `id, name, slug, ruleset, status, namespace, population?, hasQueue?`). The Outpost still
  cleans and caps what it shows (16 realms, 8 names, short texts).

How the realms are found: the server probes `dynamic-{region}`, `dynamic-classic-{region}`, `dynamic-classic1x-{region}` (or `LODGE_REALMS_NAMESPACES`) on `https://{region}.api.blizzard.com` (connected-realm index, then each connected realm), keeps the realms whose name, slug or category says "Forever", and from then on only fetches those. Polls: `LODGE_REALMS_POLL_SECONDS` (60-900, default 120), one per interval for the whole lodge; 15 minutes while nothing is found; backoff on errors and `Retry-After` on HTTP 429 (at most 15 minutes).
## Badges and the ladder (feature `badges`; the server version is set at merge; optional for clients)

Fun milestones for what a member shares, and a monthly ladder. **Nothing new is collected from the game**: every number is derived from what the lodge already holds. Feature `badges` in the welcome `features` and a welcome field `badges: { enabled, locked }` (`enabled` = the Guild Master's switch, `locked` = `LODGE_BADGES_ENABLED=0` on the server; the feature is advertised only while both allow it). A client on an older server sends nothing and shows nothing. Quiet by design: no push of any kind when a badge is earned.

- Who: members and up (a personal code with a rank above guest). **Guests have no badges** and never appear; they may read the ladder.
- The definitions live in ONE table on the server (`Badges.Table`): `id` (`<family>.<tier>`, hidden ones a plain word), `family`, `tier` (1 bronze, 2 silver, 3 gold, 4 legendary), `name`, `flavour`, a metric with a threshold, `hidden`. Clients render whatever `badges.mine` sends, so a new badge needs no client update; an unknown family just gets a default icon. Earned badges are kept by id and never taken back (not when the data they were earned from is pruned), and go only with the member (leaving or being removed).
- `badges.mine {}` -> `badges.mine { ok, you, hide, new, points, rank, season, seasonPoints, seasonRank, earned, total, badges: [ { id, family, familyName, tier, name, flavour, how, hidden, earned, at? (unix s), value?, target? } ] }`. Every badge is listed; an unearned secret has `name` and `how` `"???"`, an empty `flavour` and no `value` / `target`. `value` / `target` (progress, `value` is capped at `target`) are present when the badge is earned from a number. `new` = badges earned since the member last said `badges.seen`.
- `badges.seen {}`: the member has looked at their badges (the "new" dot goes). No answer.
- `badges.hide { hide: bool }`: "Hide me from the ladder" (off by default). Hidden members are left out of everybody else's ladder (they see their own row marked `hidden`); their badges still show on their profile. Answers `badges.mine`.
- `badges.member { name }` -> `badges.member { ok, name, count, badges: [ { id, family, tier, name, at } ] }`: that member's EARNED badges (highest tier first), nothing about progress.
- `ladder.get { season: "2026-10" | "all" }` (an unknown or malformed value means the current month) -> `ladder.data { ok, season, current, seasons: [ "2026-10", "2026-09", ... ], rows: [ { rank, name (the Lodge login; clients show display names as usual), points, top: [ up to 3 earned badges ], you?, hidden? } ], you: { rank, points, hidden } | null }`. At most 100 rows; equal points share a rank; only members with points are listed. Past seasons are the final standings kept for 12 months.
- `admin.badges { on: bool }`: the Guild Master switches the whole feature on or off (persisted). Everybody receives `badges.state { enabled, locked }`. Refused with code `locked` when the server setting is off.
- Errors (never shown by the Outpost): code `disabled` ("Badges are switched off on this lodge"), `guest`, `rate`.
- Limits: `badges.*` and `ladder.get` share one budget per person (a burst of 10, then one per 2 seconds). The numbers are computed on a timer (every 10 minutes) and when a member asks (at most every 30 seconds).
- Typed (stage 2): every message above, `welcome.badges` and `badges.state` are classes in `shared/Elans.Shared/Lodge/BadgesMessages.cs`
  (`BadgesMineGetMsg` ... `AdminBadgesMsg`, `BadgesMineMsg`, `BadgesMemberMsg`, `LadderDataMsg`, `BadgesStateMsg`; a badge in either form is a
  `BadgeView`, a ladder row a `LadderRow`), used by both sides like the LFG ones. The server still reads `admin.badges`' `on` from the raw
  JSON (missing or wrong-typed = on).

**Data sources** (per member; a counter that no store keeps per member is a small tally in `data/badges.json`, counted from the day the feature went live):

| Number | From |
|---|---|
| kills, nodes and chests, catches, finds | Atlas pool counters per member (`atlas.json`) |
| vendors, item details revealed | records in the Atlas pool that carry their author |
| zones covered | zones in the member's kill, fishing and spawn records |
| quest rewards, creatures / objects / quests seen | tally of the records a share added (Atlas tables carry no author) |
| rare drops recorded first | drops under 1 % (source with 200+ kills) that only this member recorded |
| flight legs | the member's current flight contributions (`flights.json`) and learned flight routes (`flightroutes.json`) |
| characters shared | the Armory |
| files shared, events created, event sign-ups (once per event), group posts, hosting the Classic data | tally |
| days active in chat (days with a message, not messages), late nights (00:00-05:00 UTC) | tally |
| days in the lodge | the member's oldest device, else the first time the server noticed them |

**Points**: each category is `Weight * log2(1 + value / Unit)`, at most 120 per category (the table is `Badges.Metrics`), plus 10 / 25 / 50 / 100 for every bronze / silver / gold / legendary badge. The season is the calendar month (UTC): it counts the growth since the first of the month (the numbers at the start of the month are its baseline) and the badges earned in it; all-time counts everything.

## 2.22 additions (forgetting members; optional for clients)

- Welcome: `canPurge` (bool) - the owner may use `admin.purge`.
- `admin.purge { name, atlasOnly? }` (owner only; `atlasOnly` defaults to true) -> `admin.purged { name, atlasOnly, atlas, flights, signups, organised, profile, armory, badges, messages, reactions, uploads, total }` (counts of what was removed). `atlasOnly: true` takes one member's Atlas contributions out and leaves them a member; `false` forgets all stored data of someone who is no longer a member (an error for a current member: "They are still a member ..."). Everybody's next `atlas.get` returns `full: true` with a new `epoch`.
- `member.leave` and `admin.remove` now do the same fan-out for the person who leaves / is removed: Atlas, flights, calendar sign-ups (events stay, organiser becomes `(former member)`), profile, armory, badges are removed; chat messages stay with `from: "(former member)"`; uploaded files stay without an uploader name.
- `atlas.shared`: a new `why: "full"` (ok false) when the Atlas file reached its size cap; nothing was stored. Times (`at`) in vendors, quests and finds may be at most 5 minutes ahead (was one day).
- `atlas.data` for a guest: finds carry `by: "a member"` and `at` rounded down to the UTC day.
- `POST /files` (chat uploads) answers 415 `{ error }` for programs and scripts (the Files module's list), like the shared Files module.

## 2.23 additions (hunter field notes; feature `atlas5`)

- `atlas.share` takes a section `fieldnotes { "<npcId>": { name?, min?, max?, n, last?, class?, fam?, taught?, pts? } }` (members and up, same limits and rate as the rest of the share). Per npc, the member's CUMULATIVE note: `n` sightings (0..100000), `min` / `max` level (1..80, `max` needs `min`), `last` time (unix, at most 5 minutes ahead), `class` one of `normal elite rare rareelite worldboss`, `fam` and `name` (cleaned text), `taught` an array of at most 24 ability tokens (`bite4`: letters then a rank), `pts` `{ "<uiMapId>": [ { x, y } ] }` map fractions (4 decimals), at most 10 maps and 24 points per npc. A note with nothing in it is refused. At most 60 npcs and 600 points per share, 600 npcs per member, 20000 npcs in the pool. An entry that fails is refused whole; unknown fields are dropped. A resend never counts twice: the stored note only grows (min down, max up, n up, last up, taught and points added).
- `atlas.data` carries `fieldnotes { "<npcId>": { name?, min?, max?, n, m, last?, class?, fam?, taught? { token: members }, pts? { map: [ { x, y, m } ] }, by? } }`: levels = lowest min / highest max, `n` = the sum of the members' counts, `m` = members with a note, `last` = newest, class / family / name by majority, points within 1% merged (`m` = members who saw one there), at most 40 points per npc. `by` = up to five Lodge display names, newest first; **not sent to guests, whose `last` is also rounded down to the UTC day**.
- The 2.22 purge (member leaves, is removed, owner reverts) takes a member's notes out and rebuilds the merges; `admin.purged.atlas` counts them.

## 2.24 additions (instance entrances; feature `atlas6`)

- `atlas.share` takes a section `entrances { "<instanceMapId>": [ { z, x, y, n } ] }` (members and up, same limits and rate as the rest of the share): per instance (its Map id, 1..99999) the member's CUMULATIVE spots outside it that they entered from, recorded by Elan's Toolkit 0.5.7 - `z` the zone map (uiMapId), `x` / `y` map fractions (4 decimals), `n` times entered (1..100000). At most 8 spots per instance, 60 instances and 300 spots per share, 300 instances per member. Two spots on the same zone map within 0.01 are one; a resend never counts twice (the stored `n` only grows). An instance whose list fails is refused whole.
- `atlas.data` carries `entrances { "<instanceMapId>": [ { z, x, y, n, m } ] }`: the members' spots merged (same zone map within 0.01), `n` = the sum of the members' counts, `m` = members who entered from there, at most 8 per instance, most members first. Guests get the same (no names are involved).
- The 2.22 purge takes a member's entrance spots out and rebuilds the merges; `admin.purged.atlas` counts them.

## 2.25 additions (seen details with the game build; feature `atlas7`)

- A server that takes the verified details of the members' client caches advertises `atlas7` next to `atlas` / `atlas2`. A client sends the fields below only to a server that lists `atlas7` (and is 2.25 or newer); an older server ignores them, an older client never sends them.
- `seenNpcs` entries may carry `build` (the game build the cache was written by, 0..9999999), `tf` (the creature type flags, 0..4294967295: 0x1 tameable, 0x4 boss level) and `kc` `[creature, creature]` (the creatures it counts as for quests, 0..9999999 each).
- `seenObjects` entries may carry `build` and `lock` (the Lock id a chest-type object opens with: herbs, ore veins, chests; 0..9999999).
- `seenQuests` entries may carry `build`, `sort` (-100000..100000), `type` (0..100), `info` (0..10000), `flags` (32-bit), `group` (0..100), `next` (quest), `money` (copper, >= 0), `spell`, `start` (item), `races` `[low 32 bits, high 32 bits]` of the race mask (ChrRaces.PlayableRaceBit bits), `rewards` / `provided` (at most 4) and `choices` (at most 6) as `[[item, count 1..1000], ...]`, `rep` (at most 5 faction ids), `goals` (at most 16) as `[[type 0..255, object, amount, "text" <= 255], ...]`, `desc` (<= 3000), `area` (<= 600) and `done` (<= 1000) texts (newline and tab allowed). An entry with any field out of range or of the wrong shape is refused whole.
- Merge per id: a record of a NEWER build replaces the stored one as a whole (and becomes its author for the purge); the same build only fills fields that are still empty; an older build changes nothing. A resend is `unchanged`.
- `atlas.data` returns the records with every field they have, and carries `seenMembers`: how many members' caches the seen tables came from (a count only; guests get it too; no names anywhere). `atlas.json` keeps the identities (`seenMembers`) so the count survives a restart; the 2.22 purge takes the member out of it.

## 2.26 additions (where npcs really stand; feature `atlas8`; counter extras, feature `atlas9` below)

- A server that takes npc spots advertises `atlas8`. A client sends `npcspots` only to a server that lists `atlas8` (and is 2.26 or newer); an older server ignores the section, an older client never sends it.
- `atlas.share` takes a section `npcspots { "<npcId>": [ { z, x, y, k, n } ] }` (members and up, same limits and rate as the rest of the share): per creature id (1..9999999) the member's CUMULATIVE spots, recorded by Elan's Toolkit 0.5.10 - `z` the zone map (uiMapId), `x` / `y` map fractions (4 decimals), `k` the kind (1 = a window opened for it: gossip, quest, vendor, trainer, flight master, bank, stable ...; 2 = a looted corpse; 3 = a minimap vignette, a rare), `n` times seen there (1..100000). At most 10 spots per npc, 400 npcs and 2000 spots per share, 3000 npcs per member, 30000 npcs in the pool. Two spots of the same kind on the same zone map within 0.01 are one; a resend never counts twice (the stored `n` only grows). An npc whose list fails is refused whole.
- `atlas.data` carries `npcspots { "<npcId>": [ { z, x, y, k, n, m } ] }`: the members' spots merged (same kind, same zone map, within 0.01; the position is the members' points weighted by their `n`), `n` = the sum of the members' counts, `m` = members who saw it there; at most 12 per npc: the best spot of every kind first, then the most members, then the most sightings. Guests get the same (only numbers, no names).
- The 2.22 purge takes a member's npc spots out and rebuilds the merges; `admin.purged.atlas` counts them.

### 2.26 counter extras and new counters (feature `atlas9`)

- A server that takes them advertises `atlas9`; a client sends the parts below only to a server that lists `atlas9` (2.26 or newer). Everything is additive: a counter without them is taken as before, and a malformed part is left out without refusing its counter.
- New counter sections, the same shape and merge rules as `kills` (cumulative per member, per field the larger value): `pickpocket`, `skinning`, `mining`, `herbalism`, `salvage` (keyed by npc id, with zones) and `contained` (keyed by the item that was opened - a clam, a lockbox - 1..999999, no zones). 3000 per member each (1000 for `contained`), 100 per share each.
- Any counter may carry `x { b { "<build>": n }, q { "<questId>": kills while that quest still wanted an item }, lv [lo, hi], cl { "0".."6": n }, fp [ corpse fingerprints 1..2147483647, at most 200 ] }`; items may carry `q` (the quest the item is for), `sx { "<random suffix id>": drops }` (negative ids allowed) and `b { "<build>": drops }`. Values are bounded by the counter's / item's own n; caps: 8 builds, 20 quests, 40 suffixes.
- Merge per member: per key the larger value, `lv` the wider range, `fp` the union (newest 200). `atlas.data` sums the members' extras and adds `dup` to a counter: kills that were a corpse another member counted too (the same fingerprint from several members), so a client can count each corpse once. Fingerprints never leave the server; they are numbers the Toolkit hashes from the corpse's server and spawn id only.
- Finds may name `src` `item:<itemId>` (found inside an opened item).

## Reputations in the Armory (feature `reputations`; the server version is set at merge; optional for clients)

Feature `reputations` in the welcome `features`, next to `armory` (absent when the owner set `LODGE_REPUTATIONS_ENABLED=0` or the Armory is off). A client sends reputations only to a server that lists it, and only for characters whose member switched **Share reputations** on in the Outpost (off by default); an older server would drop the field, an older client never sends it.
- `armory.put`'s `char` may carry `reps: {at, list: [{id, s, v, min?, max?}]}` - `id` a faction id (1-1000000), `s` the standing 1 Hated .. 8 Exalted, `v` the reputation value, `min` / `max` the bounds of the current standing (whole numbers within +-100,000,000). At most 300 per character, sorted by id, one per faction (the first wins); names are never stored (every client names factions from its own game data). An entry that fails a check is left out; garbage never fails the put. Reputations are the first part dropped when a snapshot would exceed 48 KB. `armory.list` summaries are unchanged; `armory.get` returns `reps` as stored.
- `armory.standing` `{faction}` (Page budget; members and up) -> `armory.standing` `{faction, who: [{faction, owner, by, key, name, class, level, s, v, min?, max?, at, mine?}]}`: every shared character with a reputation for that faction, best standing first (then the higher value), at most 80 rows. Nothing else of the character is in a row. Errors: `invalid` (no usable faction id), `guest`, `disabled` (the Armory is off), `noreps` (reputations are off), `rate`.
- Switching the feature off keeps what is stored on disk but never serves it (`armory.get` leaves `reps` out, `armory.standing` answers `noreps`); new snapshots are stored without it. Taking a character out, `armory.delete {all}`, leaving and the purge take its reputations with it.

## 2.30 additions (live map positions between members; feature `liveposition`; optional for clients)

Feature `liveposition` is always in the welcome `features` (no server setting, no Guild Master switch): privacy rests on each member's own
opt-in, off by default, and the scope they pick. A client sends positions only to a server that lists it; an older server ignores the unknown
type, an older client ignores `memberpos`. `game` is unchanged.

- Client -> server `position` `{mapId, fx, fy, move?, valid, scope}` about once a second while its member shares (the Outpost's
  **Share my map position with the Lodge**, off by default), only when it changed. `mapId` is the game's uiMap id (1..100000), `fx` / `fy`
  the fractions on that map (clamped to 0..1, rounded to 4 places; NaN / Infinity / non-numbers refuse the position), `move` the trail's
  movement bits (4 flight, 8 indoors, 16 swimming, 32 mounted, 64 pet out, 128 dead; everything else masked off), `scope` `everyone` |
  `officers` (anything else is `everyone`). `{share:false}` or `valid:false` (or a refused position) withdraws it.
- Who may share: a personal code of rank member or higher (`Perm.LivePosShare`) whose `game` is shared with `playing` and a `zone`: a
  position is never looser than the zone. A later `game` without a zone (or `share:false`) withdraws the position. Guests' positions are ignored.
- Budget: `position` costs a text-message token like any other plus its own bucket (burst 10, 2 per second); over it the position is dropped
  quietly (it counts as a refused message, no error). A withdrawal is never refused.
- Server -> client `memberpos` `{id, mapId, fx, fy, move, valid:true}` and `{id, valid:false}` (the dot is gone). The scope is not sent. It goes to:
  the sharer; for an invisible ("Appear offline") sharer only `Perm.SeeInvisible` (the owner) - not the voice-room viewers who see them masked;
  otherwise every personal member+ who can see the sharer (`scope: everyone`) or only officers and the owner (`scope: officers`). Never to guests.
- An unchanged position is not sent again; a newcomer gets the current positions it may see right after its welcome. A viewer who loses the right
  (the scope narrowed, a rank changed, the sharer opted out or hid their zone) gets `valid:false`; one who loses sight of the member altogether
  (they went invisible, left) gets the usual `leave`, and clients drop the dot on it. Positions are memory only: never stored, never logged.
- Typed (stage 2): the presence messages are classes in `shared/Elans.Shared/Lodge/PresenceMessages.cs`, used by both sides like the LFG ones:
  `status` (`StatusMsg`), `game` (`GameMsg`; the Outpost says which parts it leaves out), `position` (`PositionMsg`), `join` / `user`
  (`JoinMsg` / `UserMsg`, carrying a `LodgeUser`: the member entry, the same shape `welcome.users` uses), `leave` (`LeaveMsg`) and `memberpos`
  (`MemberPosMsg`). `user.game` is the object `PresenceModule.BuildGame` cleaned, its shape `LodgeGame` (every key always there); it is passed
  through as it is, and the Outpost reads it through `LodgeGame`. The server still cleans `game` (`BuildGame`) and `position`
  (`BuildPosition`) from the raw JSON: their strict number / string / JSON-`false` checks are the validation. The wire is unchanged, key
  order included (a server test pins the key sets of `join`, `user`, `user.game`, the invisible and the masked entry, `leave` and `memberpos`).

## The Cairn (Atlas commons; Lodge features `cairn` and `cairn2`, opt-in; Outpost 2.86, give to get 2.87)

Lodge 2.29: when the Cairn's manifest offers a "what changed" delta from the dataset this Lodge holds (`latest.deltas[]`, matched by
`fromSha` = the held `contentSha256`), the Lodge downloads that instead of the full file, applies it (CairnDelta.cs), and keeps the result
only if its SHA-256 equals the new `contentSha256` (the signed one); otherwise it downloads the full file as before. The Lodge packs the
result itself, so the `sha256` / `bytes` it hands to members describe its own file; `contentSha256` and `sig` are the Cairn's.

The Cairn is a separate service that merges the pooled Atlas numbers of many Lodges into one daily dataset. Lodge servers contribute (opt-in).
Since Lodge 2.28 a Lodge joins with its own key pair (below, "Joining"); before, each had a revocable bearer key from the Cairn's admin (still
works). **Give to get**: the dataset is not public - it is for the Lodges that contribute and,
through them, for their members who share. The Cairn serves it only to a Lodge in good standing (an accepted contribution within the
Cairn's grace period, 14 days by default); the Lodge hands it on only to its own members whose Atlas sharing is on, never to guests. An Outpost
never talks to the Cairn and has no Cairn address. Paths in "Lodge <-> Cairn" are relative to the Cairn's base URL. The Lodge server knows
the public Cairn's base URL (`https://nasferatu.dk/cairn`) as its default; `LODGE_CAIRN_URL` points it at another (self-hosted) Cairn.

**Members are told**: a Lodge that contributes lists `cairn` in the welcome `features` (while it contributes - the Guild Master's switch with
the Cairn's admission, or `LODGE_CAIRN_ENABLED=1` with a valid https URL and key - and the Atlas is on) and `cairn2` next to it (it receives
the dataset and hands it to members who share). The Outpost says so in Settings > Privacy.

### Joining (Lodge 2.28 / Cairn 1.4; Lodge feature `cairnjoin`; `server/Lodge.Server/CairnLink.cs`)

**The Lodge's key pair.** On first need the Lodge makes an ECDSA P-256 key pair, keeps the private key in `data/cairn-key.pem` (PKCS#8 PEM,
mode 600, never logged; `lodge-backup` takes it along with the data folder) and sends only the public key: SubjectPublicKeyInfo DER,
base64url. Its key id is the first 16 hex characters of SHA-256(SPKI DER). There is no shared secret: the Cairn stores public keys only.

**Signed requests.** Every Lodge -> Cairn request of a joined Lodge carries `X-Cairn-Key: <key id>`, `X-Cairn-Time: <unix seconds>`,
`X-Cairn-Nonce: <16..64 base64url characters, random>`, `X-Cairn-Signature: <ES256, IEEE P1363 (64 bytes), base64url>` over
`"elans-cairn-req/1\n<METHOD>\n<path?query>\n<time>\n<nonce>\n<sha256 hex of the body as sent>"`; `<path?query>` is below the Cairn's base
URL (`/v1/contribute`), the body hash is over the bytes on the wire (the gzip for a contribution; the empty string for a GET). The Cairn takes
a time within 300 s of its clock (401 `clock` with `serverTime` otherwise) and each nonce once per key within that window (401 `replay`); a
wrong signature is 401 `signature` and counts like a wrong key. No `Authorization` header is sent with a signed request.

**The Guild Master's switch.** Officers and the Guild Master ask `cairn.join.get`; the Lodge answers (and announces to them on every change)
`cairn.join {on, env, atlas, cairnHost, contributing, signed, suggestedUrl, publicUrl, state, reason, error, mode, place, of, estDay,
betaOpenSlots, hasCode, admitted, probationUntil, settled, lastPost, checked, standing, receiveUntil, serving}` - never the key, never a code.
`state`: `""` (not asked) / `waiting` / `admitted` / `refused` / `revoked` / `error` (then `reason`: `invite-needed`, `beta-full`, `code-invalid`,
`code-expired`, `code-used`, `code-revoked`, `code-blocked`, `unverified`, `duplicate-url`, `waiting-full`, `address`, `network`, `unsupported`,
...). `env: true` = the Lodge contributes through `LODGE_CAIRN_*` (the switch is locked). The Guild Master (only) sends
`admin.cairn {on, publicUrl?, code?}`: `publicUrl` must be https, a public host, port 443 or >= 1024, no user / query / fragment (normalized:
lowercase host, no trailing `/`); errors come as `error` with `code` `address` / `locked` / `atlas`. The address can't be changed once the
Cairn has it (waiting or admitted). The code is kept in `data/cairn-join.json` (mode 600) only until the Cairn took or refused it. Off = no
contribution and no data; the key pair and the admission stay, so switching on again resumes. `suggestedUrl` = `LODGE_PUBLIC_URL` when set
(otherwise the Outpost suggests the address it connects to).

**Asking** (Lodge -> Cairn):
- `GET /v1/join/info` (public) -> `{ok, mode: "beta"|"open", open, probationDays, server, betaOpenSlots? (beta), waiting?, admitPerDay? (open)}`.
  In the beta without a code the Lodge doesn't ask at all (state `error`, reason `invite-needed`).
- `POST /v1/join` `{name, publicUrl, publicKey, code?}`, signed by that key (at most 4 KB). The Cairn then calls the Lodge back:
  `GET <publicUrl>/cairn/challenge?nonce=<random>` -> `{v: 1, keyId, publicUrl, nonce, sig}` with `sig` = ES256 (P1363, base64url) over
  `"elans-cairn-challenge/1\n<nonce>\n<publicUrl>"`. The Lodge answers only while the switch is on and its key pair exists (404 otherwise; at
  most 30 a minute); the prefix and a nonce without line breaks keep an answer from ever passing for a request signature. The Cairn checks the
  key id, that the address the Lodge names is the one it was given, and the signature. Answers: 200 with the state (as `/v1/join/status`);
  403 `invite-needed` ("beta: an invite code from the Cairn's operator is needed"), `beta-full`, `code-invalid` / `code-expired` / `code-used`
  / `code-revoked`, `refused`; 429 `code-blocked` / `join-rate` (`Retry-After`); 409 `duplicate-url` / `duplicate-key`; 422 `unverified`
  (`error` says why); 503 `waiting-full`; 400 `address` / `join`; 404 `closed`.
- `GET /v1/join/status` (signed) -> `{state: "waiting", place, of, asked, estDay?, admitPerDay?}` / `{state: "admitted", id, admitted, standing,
  lastContribution, receiveUntil, probationDays, probationUntil, settled, lastPost}` / `{state: "refused"}` / `{state: "revoked"}`; 404 `unknown`.
  The Lodge polls it hourly while waiting, every 6 hours once admitted.
- Beta (the Cairn's default): a code admits at once (after the check), up to the beta's cap; there is no waiting list. Open: a verified request
  waits; a few are admitted automatically per UTC day, oldest first; a code is a fast lane past the list.
- Probation: an admitted Lodge receives right after its first accepted contribution (give to get as before), but its numbers count toward the
  Cairn's agreement rule and its "active Lodges" only after `probationDays` (7) of contributing (`probationUntil` = first contribution + 7 days).

**The bearer-key Lodge moves over** (`LODGE_CAIRN_*`, nothing to change in lodge.env): when `/v1/status` lists `"keys"` in `features`, the Lodge
sends `POST /v1/keys {publicKey}` with its bearer key AND signed by the new key, once. From then on it signs; the Cairn answers the old key with
401 `retired` (not counted as a wrong key). A Lodge that gets `retired` while it still sends the bearer key switches to signing; a signed
env-path Lodge the Cairn does not know (401 `key`) falls back to the bearer key and registers again. `admin/stats` shows `cairn.via`:
`env` / `env-signed` / `guild-master` / `off`.

### Lodge -> Cairn (`server/Lodge.Server/Cairn.cs`; settings `LODGE_CAIRN_*` in deploy/lodge.env.example)

- `GET /v1/status` signed (or with `Authorization: Bearer <key>`) -> `{ok, id, epoch, lastSeq, minLodges, maxBodyBytes, maxJsonBytes, maxRecords, dataset, server,
  standing, lastContribution, receiveUntil, graceDays, features, signed, probationUntil, settled}`. `standing`: `good` (receives), `new` (no accepted contribution yet), `lapsed` (the last
  one is older than `graceDays`); a revoked key gets 401 `revoked`.
  A different `epoch` than the one the Lodge remembers = the Cairn forgot this Lodge's data: everything is sent again. The Lodge's `seq`
  continues after `lastSeq`.
- `POST /v1/contribute` with the same header, `Content-Type: application/json`, `Content-Encoding: gzip`; body
  `{ v: 1, seq, gb, drops?: { <kind>: { "<id>": { n, i?: { "<itemId>": [times, count] } } | null } }, vendors?: { "<npcId>": { "<itemId>": [price, stack, limit, extended] } | null },
  spawns?: { "<npcId>": [[uiMapId, x, y, n]] | null }, nodes?: { "<objectId>": [[uiMapId, x, y, n]] | null }, flights?: { "<Horde|Alliance>|<nodeId>><nodeId>": [seconds, samples] | null } }`.
  Kinds: `kill object fishing pickpocket skinning mining herbalism salvage contained` (fishing keys are uiMapIds, contained keys item ids).
  Every record carries the Lodge's CURRENT total (resends never count twice; a lower total takes data back out); `null` = the Lodge no
  longer has it. `seq` grows with every try. Answer `{ok, seq, duplicate, accepted, unchanged, rejected, epoch, contribution, standing, receiveUntil}`
  (`contribution`: this post counted as an accepted contribution - not a duplicate `seq`, at least one valid record); 401 `key` / `revoked`,
  413, 415, 429 (`rate`, `budget`, `blocked`, with `Retry-After`). A record that fails a check is rejected alone; an unknown field refuses
  the whole post (400 `schema`).
- What a Lodge sends is built from its pool's guest view: no names, no characters, no members, no finds, notes, quests, seen tables or
  revealed items; flight legs only between numbered taxi nodes; coordinates rounded to 4 decimals; posts of about 1.5 MB of JSON.

- **Connected outposts** (`server/Lodge.Server/CairnOutposts.cs`): the Cairn never sees an Outpost, so the Lodge tells it two numbers per ISO
  week - `s` = members whose Atlas sharing is on and who were here that week (online, `atlas.sharing {on}`, or fetched the manifest), `r` =
  members who received the dataset from this Lodge that week (`GET /cairn/dataset` answered 200). They go with the contribution as
  `outposts: [{w: "2026-W42", s, r}]` (the week that just ended once more, then the current one), and only when the Cairn's `/v1/status`
  lists `"outposts"` in `features` (an older Cairn would refuse the unknown field) and the numbers changed since the Cairn last took them
  (alone in a post of their own when no record changed - such a post is not a contribution). Who was counted is kept only as HMAC-SHA-256
  hashes with a random per-week salt in `data/cairn-week.json`; the salt and the hashes are dropped when the week ends, a purge takes the
  member out. No names, no ids, no Outpost addresses ever leave the Lodge.
### Lodge <- Cairn (`server/Lodge.Server/CairnReceive.cs`, give to get)

- The same key, only while the standing is `good`; otherwise 403 `{reason: "contribute", standing: "new"|"lapsed", graceDays, error}` (or 401
  `key` / `revoked`). Answers are `private` (never for a shared cache).
- `GET /v1/manifest.json` (ETag, `If-None-Match` -> 304; `private, no-cache`) -> `{format: "elans-cairn-manifest", v: 1, license: "contributors",
  kid, latest: {day, seq, file, bytes, contentBytes, sha256, contentSha256, sig, kid, built}, history: [...]}`. The Lodge asks at most every
  `LODGE_CAIRN_FETCH_HOURS` (6), and soon after a contribution that (re)opened its access.
- `GET /v1/datasets/<file>` (`cairn-YYYY-MM-DD-<seq>.json.gz`; a few downloads per Lodge per day, then 429 `downloads`) -> gzip of
  `{format: "elans-cairn", v: 1, license: "contributors", day, seq, minLodges, lodges, builds: [...],
  drops: { <kind>: { "<id>": { n, m, b, i: { "<itemId>": [times, count, m, kills?] } } } }, vendors: { "<npcId>": { m, i: { "<itemId>": [price, stack, limit, extended, m] } } },
  spawns: { "<npcId>": [[uiMapId, x, y, n, m]] }, nodes: { "<objectId>": [[uiMapId, x, y, n, m]] }, flights: { "<leg>": [seconds, samples, m] }}`.
  `m` = the number of Lodges behind a figure; `kills` (4th item number) only when that item's rate stands on fewer kills than the record's `n`.
  Readers ignore unknown keys.
- The Lodge checks `sha256` of the file and `contentSha256` of the JSON inside, keeps one copy (`data/cairn/dataset.json.gz` + `held.json`),
  and on a 401 / 403 deletes it and stops serving (the log says why; the owner sees it in `cairn.status`, below). It starts again with the next
  accepted contribution.
- Signature: `sig` = ECDSA P-256 / SHA-256 (IEEE P1363, base64) over `"elans-cairn/1\n<day>\n<seq>\n<contentSha256>"`, by the Cairn's key
  (`GET /v1/key`: `{alg: "ES256", crv: "P-256", kid, x, y}`, public). The Lodge passes it on unchanged, so an Outpost that pins the key can
  tell that its Lodge did not change the numbers.
- Merging on the Cairn: a record and every item / vendor item / spot / leg in it needs `minLodges` independent Lodges (2 by default);
  outliers (drop rates far from the median across Lodges, flight times > 40 % off) are left out; no Lodge weighs more than all others
  together; records are kept per game build and old builds age out.

### Outpost <- its own Lodge (feature `cairn2`; `src/Lodge/LodgeSession.Cairn.cs`, `src/Services/Atlas/CairnData.cs`)

Only for a Lodge that lists `cairn2`; an older Lodge (or one that doesn't contribute) gets none of this, and an older Outpost never sends it.
Guests never get the data.

- `atlas.sharing {on}` (client -> server, members and up, Page budget): the member's Atlas sharing switch ("Share my Atlas observations with
  this Lodge"). The Lodge can't see the switch otherwise (sharing off simply means nothing is uploaded), so the Outpost says it after every
  welcome and whenever the switch changes. Stored per identity in `data/cairn-sharing.json` (unknown = off; a full purge forgets it). A guest's
  message is answered but never stored. Answer: `cairn.status`.
- `cairn.status` (client -> server: ask; server -> client: the answer, and sent unasked to the members who share - and the owner - when a new
  dataset arrives or serving stops) `{entitled, sharing, why?, text?, day?, seq?, lodge?}`. `why` when not entitled: `share` (sharing is off:
  "Share to receive"), `guest`, `lodge` (this Lodge is not receiving: it has to contribute to receive), `disabled` (not handed out), `not-yet`
  (no copy on hand yet - try later). `day` / `seq` of the dataset on hand when entitled. `lodge` only for the owner: `{standing, serving,
  receiveUntil?, checked?, why?, problem?}` - the Lodge's own standing at the Cairn and why it receives nothing.
- 2.31.1: a Lodge that doesn't contribute (so doesn't hand out the data) still answers both: `atlas.sharing` and `cairn.status` get
  `cairn.status {entitled: false, sharing, why: "disabled", text}` (no `lodge` block) instead of no answer at all. The sharing switch isn't
  stored then. A current Outpost doesn't ask such a Lodge (no `cairn2`), but a client that does now hears why.
- `GET /cairn/manifest` (the usual credential headers, like `/datasets/...`) -> the manifest of the copy on hand `{format:
  "elans-cairn-manifest", v: 1, license: "contributors", kid, latest: {day, seq, file, bytes, contentBytes, sha256, contentSha256, sig, kid,
  built}}` (no history, no Cairn address), `ETag` = `"<first 32 hex of contentSha256>"`, `If-None-Match` -> 304, `private, no-cache`.
- `GET /cairn/dataset` -> the `.json.gz` as received (`X-Sha256`; `private, no-store`); 4 per member per hour, then 429 `rate`.
- Refusals of both routes: `{error, reason}` with 404 `disabled`, 403 `guest`, 403 `share`, 403 `lodge`, 503 `not-yet`. The Outpost deletes its
  copy on `share`, `guest`, `lodge`, `disabled` (and 401); on `not-yet`, 429 or a network error it keeps it. It also deletes it at once when
  the user switches sharing off, has no Lodge, or the Lodge doesn't list `cairn2`. It checks both SHA-256s and, once it pins the Cairn's key,
  the signature; it asks at most every 12 hours, and at once when `cairn.status` names a newer `seq`.

### Notices from the Cairn (Lodge 2.31 / Cairn 1.6.2; Lodge feature `cairnnotices`; `server/Lodge.Server/CairnNotices.cs`)

The Cairn's operator can send a short notice (security issue, maintenance, news) to every Lodge or one Lodge. The Cairn adds the notices meant
for a Lodge to its `/v1/status` and contribution answers (`notices: [{id, created, expires, level, to, text, sig, kid}]`); the Lodge also checks
`/v1/status` every 15 minutes. `level` is `info`, `warning` or `critical`; `text` is plain text, at most 500 characters; `to` is `*` or the
Lodge's id. `sig` is ES256 (base64, r||s) with the Cairn's dataset key over
`"elans-cairn-notice/1\n<id>\n<created>\n<expires>\n<level>\n<to>\n<text>"` (unix seconds). The Lodge keeps the current list in
`data/cairn-notices.json`, logs each new one, and passes them on unchanged - it can't write or alter one.

- `cairn.notices {list}` (server -> client): after `cairn.notices.get` and whenever the list changes. The Guild Master and officers get every
  notice, other members only `critical` ones, guests none. An empty list = withdrawn.
- `cairn.notices.get {}` (client -> server, Page budget): ask for the list; the Outpost sends it after the welcome.
- `cairn.notice.seen {ids: [..]}` (client -> server, Page budget): the user closed these. Only an officer's or the Guild Master's counts: the
  Lodge tells the Cairn once (`POST /v1/notices/seen {ids}`, signed like the other Lodge -> Cairn calls), so the operator sees the notice landed.
  Ids the Cairn did not send are ignored.

The Outpost checks every notice against the Cairn key it pins (`CairnAtlas.PinnedKeyX/Y`) and drops unsigned, wrongly signed, expired or odd
ones; it shows the most urgent one not closed on this PC.

Typed (stage 2): `atlas.sharing`, `cairn.status` (the ask and the answer with its `lodge` block), `cairn.join.get`, `cairn.join`,
`cairn.notices.get`, `cairn.notices` and `cairn.notice.seen` are classes in `shared/Elans.Shared/Lodge/CairnMessages.cs` (`AtlasSharingMsg`,
`CairnStatusMsg`, `CairnJoinMsg`, `CairnNoticesMsg` / `CairnNoticeEntry`, `CairnNoticeSeenMsg`, ...), used by both sides like the LFG ones. They
only map fields: the notices' signatures are still checked by the Outpost (`CairnFormats.Notice` + `Es256`), and who gets what stays in
`CairnReceive.cs`. The Guild Master's `admin.cairn` `{on, publicUrl?, code?}` is `AdminCairnMsg` there too; the address and code rules stay in
`CairnReceive.SetJoin` (`CairnSign.TryNormalizeUrl`), and the code never comes back (`cairn.join` only says `hasCode`).

## Operator stats (read-only; `GET /admin/stats`, `sudo lodge-admin stats`)

Off unless `LODGE_ADMIN_TOKEN` (>= 24 characters) is set. Answers only a connection whose own address is loopback (checked before the
forwarded headers are applied) with no `X-Forwarded-For` / `Forwarded` / `X-Real-IP` header, carrying `X-Lodge-Admin: <token>`; anything
else gets 404 (like an unknown path). `no-store`. For the server's operator on the server itself (`lodge-admin stats`) or through an ssh
tunnel to `127.0.0.1:<port>` - never through the reverse proxy.

`{server, now, startedAt, uptimeSeconds, members: {total, roles: {owner, officer, ...}, online, guestsOnline, connections, active7d, maxUsers},
devices, invitesPending, messages: {channels, kept}, files: {uploads, uploadBytes, slots, slotBytes}, features: [...], cairn: {enabled,
standing, serving, receiveUntil, dataset: {day, seq, bytes}, lastCheck, problem, sharingOn, outposts: [{w, s, r}]}}`. Counts only: no names,
codes, tokens, IPs or message text. `active7d` = members online now or with a device seen in the last 7 days.
2.31 adds `security: {day, counts: {kind: n}, recent: [{at, kind, detail?}]}` - today's security events (`wrong-code`, `lockout`, `blocked`,
`flood`, `too-big`, `admin-refused`, `http-rate`), without addresses. The addresses are only in the server's log (journald), one line per kind
and address per 10 minutes, then a sum per minute and a summary per day (`security: <kind> from <ip>`).

## Typed messages

Every message is a class in `shared/Elans.Shared/Lodge/<Group>Messages.cs`, used by the Lodge server and the Outpost alike; the
"Typed (stage 2)" notes in the sections above name them. Neither side builds or reads a frame by field name anywhere else.

One JSON engine and one adapter for both sides (`shared/Elans.Shared/Lodge/LodgeJson.cs`): System.Text.Json nodes. `LodgeJson.Parse` turns
a received frame into its `JsonObject`, `LodgeJson.Serialize` / `SerializeUtf8` writes one, `LodgeJson.Build(new XMsg { .. }.Write)` builds
one from its class, and `LodgeJsonFields` is the `ILodgeFields` / `ILodgeWriter` the classes read and write through (lenient: a missing or
wrong-typed field is null / 0 / false). The rules are the same on both sides and live there too: a frame is skipped as a whole unless it
is strict JSON with an object at the top, at most 64 levels deep (`MaxDepth`, the frame included), with no key twice in one object, valid
UTF-8 and no lone surrogate in any string or key, at most 20,000 items in any one array or keys in any one object (`MaxItems`),
1,000,000 values (`MaxValues`) and 100,000 objects and arrays (`MaxContainers`) in all - all checked while parsing (the counts before
anything is built), so a frame that parses never throws when a field is read.
Frames are at most 64 KB to the Lodge (`MaxFrameToLodge`, a bigger one closes the connection) and 8 MB from it
(`MaxFrameFromLodge`: the worst `flights.routes.data` is about 4.5 MB, `atlas.data` comes in pages of about 200 KB); the Outpost stops
reading a bigger one at the cap and reconnects (`LodgeFrames.ReadAsync`), and reconnects too when more than 2000 frames (or 64 MB) wait for
its UI (`LodgeInbox`). A Lodge's HTTP JSON answers (`/exchange`, uploads, errors) are read to at most 64 KB (`MaxHttpAnswer`). Where a free-form part feeds a validator written for plain dictionaries (an
Atlas section, a character sheet), `LodgeJson.ToPlain` / `FromPlain` convert it at that one place.
`shared/Elans.Shared.Tests/JsonLeniencyTests.cs` pins how tricky input reads, against frozen copies of the two old readers.

### How to add a message

1. Add its type to `LodgeMsg` (`shared/Elans.Shared/Lodge/LodgeMsg.cs`), in its group.
2. Write its class in `shared/Elans.Shared/Lodge/<Group>Messages.cs`: `public const string Type = LodgeMsg.X`, a `Write` that sets `t`
   first, and a `Read` that is lenient like its neighbours (a missing or wrong-typed field reads as null / 0 / false, never throws). A
   message with only an id or nothing at all derives from `LodgeIdMsg` / `LodgeEmptyMsg`. A message both ways has one class each way.
   Keep limits and validation on the receiving side, as the neighbours do.
3. Add a golden and a round-trip test in `shared/Elans.Shared.Tests/<Group>MessagesTests.cs`: what `Write` gives, key order included,
   and that `Read` gets it back (also from an old or malformed frame).
4. Handle it in the server module through the class, and add a server test in `server/Lodge.Tests` that pins its key set and order.
5. Send and read it in the Outpost through the class (`LodgeJson.Build(new XMsg { .. }.Write)`, `XMsg.Read(new LodgeJsonFields(m))`) - the same
   calls as on the server.
6. Document it in this file: its fields in its feature's section, and its type in the index below.
7. A new capability goes behind a flag in `welcome.features` (and `GET /features` when an app must know before signing in): the Outpost
   sends nothing a Lodge doesn't list, the Lodge ignores what it doesn't know, so old Outposts and old Lodges keep working with new ones.

When a step is missing, `dotnet test shared/Elans.Shared.Tests` fails (`LodgeMsgTests`): a type string not in `LodgeMsg`
(`No_raw_type_strings_in_the_Lodge_code`), a type without its class or with a class too many (`Every_type_has_its_typed_class_one_per_direction`),
a class writing another type (`Every_typed_class_writes_its_own_type`), a frame built or read by field name on either side
(`No_frame_is_built_or_read_by_field_name_outside_the_typed_classes`), a type not described here (`Every_type_is_described_in_PROTOCOL_md`)
or missing from the index (`Every_type_has_its_typed_class_one_per_direction`), a type the server neither handles nor sends
(`Every_type_is_used_by_the_server`).
No test can see a missing golden test (step 3) or features flag (step 7): the review does.

## Every message type (index)

The one list is `shared/Elans.Shared/Lodge/LodgeMsg.cs`: the Lodge and the Outpost take every `t` from it, and
`shared/Elans.Shared.Tests/LodgeMsgTests.cs` fails when a type is missing here or a raw type string is used in either side's Lodge code.
"both" = the client asks with that type and the server answers (or broadcasts) with the same one. The sections above describe the fields.
Not Lodge messages (never on this socket): the Atlas world-map page's own messages inside the Outpost (`goto`, `layer`, `init`, `toast`, ...).

| Feature | client -> server | server -> client | both |
|---|---|---|---|
| Connection | `ping` | `pong` · `welcome` · `error` | - |
| Members and presence | `status` · `game` · `position` | `join` · `leave` · `user` · `memberpos` | - |
| Voice | `voice` · `state` | - | - |
| Chat | `edit` · `delete` · `history:before` · `search` | `edited` · `deleted` · `history:page` · `search:result` | `msg` · `react` · `pin` · `unpin` · `typing` |
| Looking for group | `lfg:join` · `lfg:maybe` · `lfg:leave` · `lfg:close` · `lfg:cancel` | `lfg` | - |
| Profiles | `profile:get` · `profile:set` · `profile:reset` | `profile` · `profile:data` | - |
| Guild Master and officer tools, devices, leaving | `admin.invite` · `admin.role` · `admin.remove` · `admin.purge` · `admin.channel.add` · `admin.channel.remove` · `admin.invite.create` · `admin.invite.revoke` · `admin.retire` · `admin.lodge.set` · `mod.kick` · `mod.mute` · `device.list` · `device.revoke` · `member.leave` | `admin.invited` · `admin.purged` · `admin.invite.created` · `lodge.info` · `devices` · `member.left` · `device.added` | `admin.members` · `admin.invites` · `admin.devices` |
| Calendar | `cal:create` · `cal:edit` · `cal:cancel` · `cal:signup` · `cal:leave` | `cal:events` | `cal:range` |
| Shared flight times and learned flight paths | `flights.share` · `flights.get` · `flights.routes.share` · `flights.routes.get` | `flights.shared` · `flights.data` · `flights.routes.shared` · `flights.routes.data` | - |
| The Atlas pool | `atlas.share` · `atlas.get` | `atlas.shared` · `atlas.data` | - |
| The Cairn | `atlas.sharing` · `cairn.join.get` · `admin.cairn` · `cairn.notices.get` · `cairn.notice.seen` | `cairn.join` · `cairn.notices` | `cairn.status` |
| The Armory | `armory.put` · `armory.delete` · `armory.list` · `armory.get` | `armory.saved` · `armory.deleted` · `armory.chars` · `armory.char` | `armory.crafters` · `armory.standing` |
| Shared files | `files.list` · `files.delete` · `files.rollback` | `files.data` · `files.changed` · `files.result` | - |
| Hosted datasets | `dataset.info` · `dataset.delete` | `dataset.data` · `dataset.changed` · `dataset.result` | - |
| Badges and the ladder | `badges.seen` · `badges.hide` · `ladder.get` · `admin.badges` | `badges.state` · `ladder.data` | `badges.mine` · `badges.member` |
| Realm status | - | `realms.status` · `realms.back` | - |

Routing: every module answers only the types it handles (2.31.1: `AdminModule` no longer claims every `admin.` / `mod.` type - `admin.badges`
and `admin.cairn` belong to the badges and Cairn modules), so the order of the hub's module list doesn't matter; `server/Lodge.Tests/AdminRoutingTests.cs`
checks it.
