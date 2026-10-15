# Lodge protocol (v2.11)

Base URL: `https://<site>/lodge` (or a subdomain root). Opening the base URL in a browser shows a short
"you're invited" page. All access needs an invite code: a personal one (tied to a name and a rank,
`Name:code:role` in `<data>/codes`, managed from the hub's Guild Master panel or `lodge-admin`) or the optional
shared `LODGE_CODE` (rank: guest). Send codes in the `X-Lodge-Code` header, never in a URL (query strings end
up in proxy logs); the header wins when both are present. `?code=` is refused by default
(2.8; `LODGE_ALLOW_QUERY_CODE=true` accepts it for 1.x hubs) - a request carrying `?code=` gets 401 "use the header" (not a strike).
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
- `ping` every 20 s from the client; the server answers `pong` and closes a pinging connection that was silent for 75 s
  with close code 1001. Clients that never ping are not closed for silence.
- Guest names always end in " (guest)". `@everyone`/`@here` from below officer arrive with a zero-width space after the `@`.
- `profile:data` leaves out a character's `seen` when the member is invisible to the viewer.

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

- `admin.invite.create` `{kind: "person"|"guest", name, role, hours (1-720; 0 = server default 168), uses (guest, 1-50)}` - owner and officer (officers: ranks below their own; for an existing name the invite adds a device with that person's current rank)
- `admin.invites` · `admin.invite.revoke` `{id}` · `admin.devices` `{name?}` - owner and officer
- `device.list` - your own devices (a guest sees only theirs) · `device.revoke` `{id}` - your own, or a lower rank's (owner: anyone's)
- `admin.retire` `{name, force?}` - owner: the person's permanent code stops working (`admin.members` entries carry `hasCode` and `devices`)

### WebSocket (server to client)

- `welcome` adds `canInvite`, `canRetire`, `device: {id, name}` (tokens only)
- `admin.invite.created` `{id, kind, name, role, by, created, expires (unix ms), max, uses, used, expired, revoked, pending, secret}` - the only place the secret appears
- `admin.invites` `{now, invites:[{...as above, no secret}]}` · `devices` / `admin.devices` `{name, now, devices:[{id, name, role, device, created, lastSeen, ip (coarse), current, online}]}`
- A revoked device's socket is closed (1008, "This device was signed out"); clients don't reconnect after that.


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

## 2.14 additions (shared files; optional for clients)

Feature `files` (welcome `features` and `GET /features`; absent when the owner set `LODGE_FILES_ENABLED=0`). Welcome also carries `canManageFiles` (officer and up) and `filesMaxMb`.

A slot is `{id, name (1-60), createdBy, versions: [{v, fileName, size, sha256, uploadedBy, at (ms), note (0-200)}]}`, versions ascending, at most the newest 3 kept; version numbers are never reused.

- `files.list` `{}` -> `files.data` `{maxMb, totalMb, usedBytes, keep, canManage, slots: [...]}` (everybody; Page budget).
- `files.delete` `{slot}` and `files.rollback` `{slot, v}` (officer and up; rolling back removes the versions newer than `v`) -> `files.result` `{op, ok, slot, why?}`, `why`: `forbidden`, `missing`, `disabled`, `already-newest`, `save`.
- Every change is announced to everyone: `files.changed` `{action: new|version|rollback|delete, slot, name, by, at, v?}`; clients refresh with `files.list`.
- Upload (HTTP, same credential headers as `/files`, officer and up): `PUT /slots/new` (header `X-Slot-Name`) or `PUT /slots/{id}` (a new version) with headers `X-File-Name`, `X-Note` (URL-encoded), `X-Sha256` (hex of the whole body), a `Content-Length`, the raw body. Answer 200 with the slot JSON, or `{error, reason}` with 400 (`sha-mismatch`, `length`, `bad-sha`, `bad-name`, `empty`), 403 (`forbidden`, `disabled`), 404 (`missing`), 409 (`name-taken`), 411, 413 (`too-big`), 415 (`blocked-type`, `blocked-zip`), 429 (`rate`, `busy`), 507 (`full`, `too-many`).
- Download: `GET /slots/{id}/{v}` (everybody, guests included) - the raw bytes as an attachment; the client checks size and SHA-256 against the list.

## 2.15 additions (the Atlas pool; optional for clients)

Feature `atlas` (welcome `features` and `GET /features`; absent when the owner set `LODGE_ATLAS_ENABLED=0`). A client on an older server sends nothing. Members pool what Elan's Toolkit observed (see ARCHITECTURE.md, `atlas.obs` v1); everybody downloads the aggregate. Ids are positive whole numbers (npc, object, quest 1-9999999; item 1-999999; uiMapId 1-99999), JSON object keys carry them as plain decimal text, `x`/`y` are map fractions 0..1 (rounded to 3 decimals), `at` is unix seconds (after 2001, at most 1 day ahead).

- `atlas.share` `{kills?, objects?, fishing?, vendors?, quests?, finds?, spawns?, items?, seenNpcs?, seenObjects?, seenQuests?}` - member and up (guests: `why: "guest"`), a burst of 3 then one per 2 minutes per identity, frame limit 64 KB. Per message at most 200 kill sources, 100 objects, 50 fishing zones, 60 vendors, 200 quests, 500 finds, 200 spawn npcs (20 points each), 200 revealed items. An entry that fails any check is refused as a whole (`rejected`); a malformed message never throws and an empty one (`why: "empty"`) costs no token.
  - `kills` `{npc: C}`, `objects` `{obj: C}`, `fishing` `{uiMapId: C}`, with `C = {n, items?: {item: {n, c}}, z?: {uiMapId: n}}` (fishing has no `z`): **cumulative** totals of THIS member since its first record - `n` looted kills / opens / casts, per item `n` times dropped and `c` total count, per zone the kills there. Checks: `n` 0..100000 (a new source needs 1+), item `n` <= `n` x 10, `c` between `n` and `n` x 200, at most 150 items and 40 zones per source, zones add up to no more than `n`, one member's counters total at most 1000000, at most 6000 / 3000 / 400 sources per member.
  - `vendors` `{npc: {at, z, x, y, items: {item: {p (copper 0..100000000), s (stack 1..1000), l (limit -1..1000)}}}}` (<= 300 items) - the newest `at` per npc wins.
  - `quests` `{quest: {at, rewards?: [item...], choices?: [item...]}}` (<= 10 each) - the newest `at` wins.
  - `finds` `[{i item, c count 1..1000, src: "npc:N"|"obj:N"|"vendor:N"|"quest:N"|"fish", at, z?, x?, y?}]` - the newest 2000 are kept, one member at most 700 of them; the same find twice is stored once.
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
- `atlas.get` `{since?, epoch?}` - everybody (guests too), Page budget. Answer `atlas.data` `{epoch, rev, at, full?, more?, kills, objects, fishing, vendors, quests, spawns, items, finds, seenNpcs, seenObjects, seenQuests}`: every record changed after revision `since`, in revision order, cut into pages of about 200 KB (`more: true` -> ask again with `since` = the answer's `rev`). `kills`/`objects`/`fishing` records are the aggregate over all members `{n, m (members), items: {item: {n, c}}, z}` and replace the client's record of that source; `vendors`/`quests`/`items` are as shared, `spawns` is `{npc: [{z, x, y}]}` (the whole list), `finds` is `[{s (revision), i, c, src, at, by (display name, or the login name), z?, x?, y?}]` (the client keeps the newest 2000 by `s`). `full: true` (first page only): the client's state cannot be continued (`since` 0, another `epoch`, or a `since` beyond this server's revision, e.g. after a restore) - replace everything held.
- Idempotency: nothing is added up on the server. Per member and per field the stored value is max(stored, received); the aggregate is the sum over members of those. Resending a batch, a lost answer, or an old upload record cannot count anything twice, and a lower value never lowers a total.

## 2.16 additions (the Armory; optional for clients)

Feature `armory` (welcome `features` and `GET /features`; absent when the owner set `LODGE_ARMORY_ENABLED=0`). A client on an older server sends nothing. Members choose characters on their own PC and share a snapshot of each (gear with item links, stats, talents, professions, hunter pets - never bags, bank, gold or mail); everybody in the lodge can browse them. Members and up only: guests neither share nor browse.

- `armory.put` `{key, char}` - `key` is the character's `Realm-Name` (3-80 characters, a dash in it); `char` `{name, realm?, class (file name, upper case: HUNTER), classText?, race?, raceFile?, sex? (2|3), faction?, guild?, level (1-100), ilvl?, at, gear?: {at, ilvl?, slots: {"1": {i, q, ic?, il?}}, links?: {"<itemId>": "<item link of a worn item>"}, stats?: {health, power, str, agi, sta, int, spi, armor, ap, rap, crit, rcrit, dodge, parry, block, speed, powerType, melee?: {min, max, speed}, ranged?}}, talents?: {at, api?, spent, unspent, build? ("ETB1-..."), nodes: [{id, r, m?, s?, ic?, x?, y?, n?}]} (only nodes with points), professions?: {at, list: [{name, skill, max, ic?, kind: primary|secondary}]}, pets?: {at, active?, pets: [{key, name, family?, familyId?, loyalty, loyaltyText?, level, stable, happiness, ic?, tpTotal?, tpSpent?, abilities: [{id?, name?, rank}]}], known?: {abilityId: rank}}}`. Rebuilt field by field by the server, at most 48 KB per character, 30 characters per member (1500 per lodge), a burst of 30 then one per 8 seconds per identity. Answer `armory.saved` `{ok, key?, stored, why?}`; `stored` is false when it was identical to what the lodge already had; `why`: `guest`, `disabled`, `invalid`, `empty`, `size`, `rate`, `full`, `save`.
- `armory.delete` `{key}` or `{all: true}` - always the caller's own characters (the owner is the connection's identity, any `owner` in the frame is ignored); deleting what is not there is fine. Answer `armory.deleted` `{ok, removed, key?, why?}`.
- `armory.list` `{}` -> `armory.chars` `{chars: [{owner, by, key, up, mine?, name, realm?, class, classText?, race?, raceFile?, sex?, faction?, guild?, level, ilvl?, at, professions: [{name, skill, max}]}]}` (Page budget). `owner` is the member's identity key (use it for `armory.get`), `by` the name the lodge sees (display name, else the login), `up` when the server stored it (ms), `mine` is true for the caller's own.
- `armory.get` `{owner, key}` -> `armory.char` `{owner, by, key, char}` (the full snapshot as stored) or an error with code `notfound` (Page budget). Errors for the feature: code `guest` (the shared code), `disabled`, `rate`.
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

## Realm status (feature `realms`; the server version is set at merge, 2.20.0 here; optional for clients)

The Lodge asks the Blizzard Game Data API (client credentials from its own environment, never sent to a client) and pushes the status of the WoW Forever realms. Feature `realms` is in the welcome `features` only while `LODGE_BNET_CLIENT_ID` and `LODGE_BNET_CLIENT_SECRET` are set. Nothing is asked of the server: the client sends no message for this. Everybody connected (members and guests: it is public information) receives:

- `realms.status { available, region, realms: [ { id, name, slug, ruleset, status, population?, hasQueue?, namespace } ], checkedAt, why? }` right after the welcome (once the first poll has finished), whenever something changes, and as a heartbeat every 5 minutes so "checked N min ago" stays honest. `status` is `UP`, `DOWN` or `UNKNOWN`; `ruleset` is `Normal`, `PvP`, `RP`, `RP-PvP`, `Hardcore` or `""` (from the realm's type, with hints in its name or category winning); `id` is the connected realm id; `checkedAt` is unix seconds of the last poll that reached the API. `available: false` means the Forever realms were not found (`why: "Forever realms not found in the Blizzard API"`) or the API could not be reached for three polls in a row (`why` says so); `realms` is then empty. After three failed polls known realms are sent as `UNKNOWN`.
- `realms.back { realms: [names], checkedAt }`, once, when every realm that was down is up again. The client decides whether to say anything (the Outpost: only if the member turned that on).

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

**Data sources** (per member; a counter that no store keeps per member is a small tally in `data/badges.json`, counted from the day the feature went live):

| Number | From |
|---|---|
| kills, nodes and chests, catches, finds | Atlas pool counters per member (`atlas.json`) |
| vendors, item details revealed | records in the Atlas pool that carry their author |
| zones covered | zones in the member's kill, fishing and spawn records |
| quest rewards, creatures / objects / quests seen | tally of the records a share added (Atlas tables carry no author) |
| rare drops recorded first | drops under 1 % (source with 200+ kills) that only this member recorded |
| flight legs | the member's current flight contributions (`flights.json`) |
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

## 2.26 additions (where npcs really stand; feature `atlas8`)

- A server that takes npc spots advertises `atlas8`. A client sends `npcspots` only to a server that lists `atlas8` (and is 2.26 or newer); an older server ignores the section, an older client never sends it.
- `atlas.share` takes a section `npcspots { "<npcId>": [ { z, x, y, k, n } ] }` (members and up, same limits and rate as the rest of the share): per creature id (1..9999999) the member's CUMULATIVE spots, recorded by Elan's Toolkit 0.5.10 - `z` the zone map (uiMapId), `x` / `y` map fractions (4 decimals), `k` the kind (1 = a window opened for it: gossip, quest, vendor, trainer, flight master, bank, stable ...; 2 = a looted corpse; 3 = a minimap vignette, a rare), `n` times seen there (1..100000). At most 10 spots per npc, 400 npcs and 2000 spots per share, 3000 npcs per member, 30000 npcs in the pool. Two spots of the same kind on the same zone map within 0.01 are one; a resend never counts twice (the stored `n` only grows). An npc whose list fails is refused whole.
- `atlas.data` carries `npcspots { "<npcId>": [ { z, x, y, k, n, m } ] }`: the members' spots merged (same kind, same zone map, within 0.01; the position is the members' points weighted by their `n`), `n` = the sum of the members' counts, `m` = members who saw it there; at most 12 per npc: the best spot of every kind first, then the most members, then the most sightings. Guests get the same (only numbers, no names).
- The 2.22 purge takes a member's npc spots out and rebuilds the merges; `admin.purged.atlas` counts them.
