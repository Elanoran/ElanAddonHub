# Lodge protocol (v2.6)

Base URL: `https://<site>/lodge` (or a subdomain root). Opening the base URL in a browser shows a short
"you're invited" page. All access needs an invite code: a personal one (tied to a name and a rank,
`Name:code:role` in `<data>/codes`, managed from the hub's Guild Master panel or `lodge-admin`) or the optional
shared `LODGE_CODE` (rank: guest). Send codes in the `X-Lodge-Code` header, never in a URL (query strings end
up in proxy logs); the header wins when both are present. `?code=` is accepted for 1.x hubs until
`LODGE_ALLOW_QUERY_CODE=false` - then any request carrying `?code=` gets 401 "use the header" (not a strike).
Every credential check - `/health` with a code, `/ws`, `/files` - shares one wrong-code counter per client IP:
the 10th wrong code within 15 minutes is still 401, then the IP gets 429 for 15 minutes even with a valid code.
At most `LODGE_MAX_TRACKED_IPS` addresses are tracked; beyond that unknown addresses share one counter.
All HTTP requests are also limited to `LODGE_HTTP_PER_MINUTE` per IP (429). New codes are 128-bit (32 hex);
older shorter codes keep working. Invite links look like `https://<site>/lodge#invite=<code>` - the part after
`#` never reaches a server.

## HTTP

| Request | Auth | Result |
|---|---|---|
| `GET /` | none | invite landing page |
| `GET /health` | none, or a code | no code: `{"ok":true}` (no strike). With a code: 401 wrong / 429 blocked / `{ok, version, online}` |
| `POST /files` raw body, header `X-File-Name` (URL-encoded) | `X-Lodge-Code`, member+ | `{"id","name","size","mime","at","by"}`; 403 guests, 413 too big, 507 storage full (`LODGE_MAX_STORAGE_MB` / `LODGE_MAX_FILES`), 429 too many at once or > `LODGE_UPLOADS_PER_10MIN` per person |
| `GET /files/{id}` | `X-Lodge-Code` | the file (range requests supported) |

## WebSocket `GET /ws?name=<display name>&client=<hub/2.0.0>[&vis=invisible]` + header `X-Lodge-Code`

Text frames are JSON with a `t` field. A closed socket with status 1008 (policy) carries the reason the client
shows: kicked, code removed, signed in elsewhere, lodge full - clients don't reconnect after those.

### Server → client

- `welcome` `{you, name, role, server, maxFileMb, canShareFiles, canModerate, canManage, canPin, features:["reply","react","react2","pin","profile"], reactions:[id], pins:{channelId:[pin]}, channels:[channel], users:[user], history:{channelId:[msg]}}`
  - sent on connect and again whenever the channels or your rank change (treat as a full refresh)
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

### Client → server

- `msg` `{channel, text, file?:{id}, replyTo?:id}` - upload first, then send the id. Max 2000 chars, 8 per 10 s.
- `react` `{id, emoji}` - toggles your reaction; `emoji` is one of the welcome's `reactions` ids (`ready, notready, lol, love, fight, loot, wipe, epic`); legacy emoji from 2.14 hubs are mapped (✅ and 👍 -> ready, ❌ -> notready, 😂 -> lol, ❤ -> love, ⚔ -> fight; U+FE0F optional); anything else is ignored. One per person and reaction, at most 30 people per reaction. Control budget.
- officer+ (`Perm.PinMessages`): `pin` `{id}` · `unpin` `{id, channel?}` - at most 25 pins per channel, kept in `<data>/pins.json` (atomic writes) with their own copy of text/author/time, so they outlive the history window; deleting the message unpins it. Others get an error.
- `edit` `{id, text}` (own) · `delete` `{id}` (own; officer+: anyone's) · `typing` `{channel}`
- `voice` `{room}` (`room: null` leaves) · `state` `{muted, deaf}` · `status` `{status, note, visibility?}` · `ping`
- `game` `{playing, name, realm, class, classFile, level, zone, guild, race?, raceFile?, sex? (2 male, 3 female), flags?, xpPct?, rested?, groupSize?, inInstance?, instanceName?, visibility?}` or `{share: false}` - rich presence (2.5 fields below)
- officer+: `mod.kick` `{id}` · `mod.mute` `{id, on}` - lower ranks only
- guild master: `admin.members` · `admin.invite` `{name, role}` · `admin.role` `{name, role}` · `admin.remove` `{name}`
  · `admin.channel.add` `{name, type, minRole, max}` · `admin.channel.remove` `{id}`

### Limits per person (personal code) or per IP (guests), kept across reconnects

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

**Characters** are collected server-side from `game` messages (name, class, classFile, race, raceFile, level, last seen), at most 12 per member (the
most recent kept), and stored with the profile.

**Storage.** Personal codes: `<data>/profiles.json` (atomic tmp+rename, size-capped load, every value re-validated, at most 2000 profiles). Guests:
a session-only profile on the connection, never written to disk (gone when they leave).

## Ranks

`guest < member < veteran < officer < owner`. Permissions live in `Roles.Can`: files need member+, moderation and pinning
officer+, members and channels owner. Status is one of online | away | busy | dungeon | lfg (note max 40 chars).

## Voice (binary frames)

- Client → server: one Opus packet per frame (48 kHz mono, 20 ms), only while in a room and not muted.
- Server → client: `[sender id: int32 little-endian][Opus packet]`, sent to everyone else in the same room who
  isn't deafened. "Speaking" indicators are derived from incoming packets on the client.
