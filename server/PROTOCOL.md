# Lodge protocol (v1)

Base URL: `https://<site>/lodge` (or a subdomain root). All access needs the invite code.

## HTTP

| Request | Auth | Result |
|---|---|---|
| `GET /health` | none | `{"ok":true,"version":"1.0.0","online":3}` |
| `POST /files` raw body, headers `X-File-Name` (URL-encoded), `X-Lodge-Name` | header `X-Lodge-Code` | `{"id","name","size","mime","at","by"}`; 413 if too big |
| `GET /files/{id}` | header `X-Lodge-Code` or `?code=` | the file (range requests supported) |

## WebSocket `GET /ws?code=<invite>&name=<display name>&client=<hub/1.1.0>`

401 for a wrong code. Text frames are JSON with a `t` field.

Server → client

- `welcome` `{you, name, server, maxFileMb, users:[user], history:[msg]}` (`name` may get " 2" if taken)
- `join` `{user}` · `leave` `{id}` · `user` `{user}` (voice/mute/deafen changed)
- `msg` `{id, at (unix ms), from, fromId, text, file?:{id,name,size,mime}}` - also echoed to the sender
- `typing` `{id}` · `error` `{text}` · `pong`

`user` = `{id, name, voice, muted, deaf}`

Client → server

- `msg` `{text, file?:{id}}` - upload first, then send the returned id. Max 2000 chars, 8 per 10 s.
- `typing` · `voice` `{on}` (join/leave voice) · `state` `{muted, deaf}` · `ping`

## Voice (binary frames)

- Client → server: one Opus packet per frame (48 kHz mono, 20 ms), only while in voice and not muted.
- Server → client: `[sender id: int32 little-endian][Opus packet]`, sent to everyone else in voice who
  isn't deafened. "Speaking" indicators are derived from incoming packets on the client.
