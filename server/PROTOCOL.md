# Lodge protocol (v2)

Base URL: `https://<site>/lodge` (or a subdomain root). Opening the base URL in a browser shows a short
"you're invited" page. All access needs an invite code: a personal one (tied to a name and a rank,
`Name:code:role` in `<data>/codes`, managed from the hub's Guild Master panel or `lodge-admin`) or the optional
shared `LODGE_CODE` (rank: guest). Send codes in the `X-Lodge-Code` header, never in a URL (query strings end
up in proxy logs). Wrong codes answer 401 `{"error"}`; 10 wrong codes from one IP within 15 minutes block it
for 15 minutes (429). Invite links look like `https://<site>/lodge#invite=<code>` - the part after `#` never
reaches a server.

## HTTP

| Request | Auth | Result |
|---|---|---|
| `GET /` | none | invite landing page |
| `GET /health` | none / optional code | `{"ok":true}`; with a valid code also `version`, `online` |
| `POST /files` raw body, header `X-File-Name` (URL-encoded) | `X-Lodge-Code`, member+ | `{"id","name","size","mime","at","by"}`; 403 for guests, 413 too big |
| `GET /files/{id}` | `X-Lodge-Code` | the file (range requests supported) |

## WebSocket `GET /ws?name=<display name>&client=<hub/2.0.0>` + header `X-Lodge-Code`

Text frames are JSON with a `t` field. A closed socket with status 1008 (policy) carries the reason the client
shows: kicked, code removed, signed in elsewhere, lodge full - clients don't reconnect after those.

### Server → client

- `welcome` `{you, name, role, server, maxFileMb, canShareFiles, canModerate, canManage, channels:[channel], users:[user], history:{channelId:[msg]}}`
  - sent on connect and again whenever the channels or your rank change (treat as a full refresh)
- `join` `{user}` · `leave` `{id}` · `user` `{user}`
- `msg` `{channel, id, at (unix ms), from, fromId, text, file?:{id,name,size,mime}, replyTo?:{id,from,text}, edited?}`
- `edited` `{channel, id, text}` · `deleted` `{channel, id}`
- `typing` `{id, channel}` · `error` `{text}` · `pong`
- `admin.members` `{members:[{name, role, online}], channels:[channel]}` · `admin.invited` `{name, role, code}`

`channel` = `{id, name, type: text|voice, minRole, max}` (only the ones your rank may see)
`user` = `{id, name, guest, role, room, voice, muted, deaf, serverMuted, status, note}`

### Client → server

- `msg` `{channel, text, file?:{id}, replyTo?:id}` - upload first, then send the id. Max 2000 chars, 8 per 10 s.
- `edit` `{id, text}` (own) · `delete` `{id}` (own; officer+: anyone's) · `typing` `{channel}`
- `voice` `{room}` (`room: null` leaves) · `state` `{muted, deaf}` · `status` `{status, note}` · `ping`
- officer+: `mod.kick` `{id}` · `mod.mute` `{id, on}` - lower ranks only
- guild master: `admin.members` · `admin.invite` `{name, role}` · `admin.role` `{name, role}` · `admin.remove` `{name}`
  · `admin.channel.add` `{name, type, minRole, max}` · `admin.channel.remove` `{id}`

1.x clients still work: a `msg` without `channel` goes to the default text channel, `voice {on:true}` joins the
first voice room.

## Ranks

`guest < member < veteran < officer < owner`. Permissions live in `Roles.Can`: files need member+, moderation
officer+, members and channels owner. Status is one of online | away | busy | dungeon | lfg (note max 40 chars).

## Voice (binary frames)

- Client → server: one Opus packet per frame (48 kHz mono, 20 ms), only while in a room and not muted.
- Server → client: `[sender id: int32 little-endian][Opus packet]`, sent to everyone else in the same room who
  isn't deafened. "Speaking" indicators are derived from incoming packets on the client.
