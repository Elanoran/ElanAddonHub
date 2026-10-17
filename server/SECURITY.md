# Lodge security notes

## 2.2.0 hardening (from the security review of 1.4.0)

| # | Finding | Fix |
|---|---|---|
| 1 | Authenticated `/health` was an unmetered code-check oracle | A code on `/health` goes through the same gate as `/ws` and `/files`: 401 wrong, 429 blocked, shared strike counter. Anonymous `/health` stays public `{"ok":true}` and creates no strike record. |
| 2 | Uploads and history had no total bound | `FileStore`: total bytes, file count, concurrent uploads and per-person upload rate; space reserved before writing (parallel uploads can't share the last space); partial files and reservations cleaned up on every failure; usage reconciled at startup; nothing deleted to make room (507 instead). `HistoryStore`: bounded tail load (never reads a whole huge file), per-channel record/byte cap, atomic crash-safe compaction, write failures reported to the sender and logged without message text. |
| 3 | Failed-IP records grew forever | `StrikeTable`: bounded (`LODGE_MAX_TRACKED_IPS`), expired records swept every 15 s, active blocks never evicted; when full, unknown IPs share one overflow counter. Separate per-IP HTTP rate limit (`LODGE_HTTP_PER_MINUTE`). Injectable clock for tests. |
| 4 | WebSocket floods, non-atomic admission | Per-identity token buckets (chat, control, voice packets, voice bytes) kept across reconnects; guests keyed by IP; typing coalesced; sustained floods closed. Admission check + add under one lock; a personal reconnect replaces the old connection before admission. |
| 5 | lodge-admin temp file exposure, non-atomic writes | `umask 077`, `flock` on `codes.lock` (shared with the server), private `mktemp` in the same folder, owner + 0640, atomic rename, cleanup on errors and signals. The server writes the same way and swaps in a complete immutable snapshot on reload (a failed read keeps the previous one). |
| 6 | Guest names checked before normalization | `Names.Normalize` (NFKC, control/format characters removed, whitespace collapsed, 24 chars) is applied once, before reservation checks, collision suffixes and reconnect matching. Suffixes never land on a reserved personal name. |
| 7 | Credentials and logging | `LODGE_ALLOW_QUERY_CODE=false` refuses `?code=` with a clear 401 (no strike); the header always wins. ASP.NET request logs (full URLs) are filtered out; client-chosen log fields are normalized and bounded. New codes are 128-bit; old codes keep working. |

## 2.3.0 (reactions, pins, replies)

- Reactions: fixed whitelist (anything else ignored), one per person and emoji, names normalized and compared
  case-insensitively, at most 30 per emoji; they are ordinary control messages, so they spend the per-identity Control
  budget and count towards the flood disconnect. Stored inside the history records (same atomic compaction).
- Pins: officer+ only (`Perm.PinMessages`), 25 per channel, `pins.json` written atomically, size-capped on load, failures
  logged without text. A channel the rank can't see never leaks messages, reactions or pins. Pin text is clipped to 300 chars.
- Replies: the quoted id must exist in the same channel; the snippet is normalized and bounded.

## 2.4.0 (reaction ids)

- Reactions are a fixed whitelist of 8 ids; legacy emoji are mapped through the same function (`ChatModule.CanonReaction`, length-bounded),
  everything else is ignored. Same properties as 2.3: one per person per reaction, 30 per reaction, Control budget, rank-visible channels only.
- Stored reactions are migrated on history load with the same bounds (names deduplicated case-insensitively, max 30, unknown keys dropped).

## 2.5.0 (rich presence, Appear offline)

- Rich presence fields are untrusted client input: strings go through `Names.Snippet` (NFKC, control/format characters removed,
  length-bounded), numbers are clamped, `flags` is masked to a whitelist, inconsistent combinations are dropped, non-string/number
  JSON in a field is ignored (no exception can reach the receive loop). They spend the existing Control budget; a message that
  changes nothing is not re-broadcast. A malformed `status`/`game` value no longer throws.
- Invisible members: visibility is decided in one place (`LodgeHub.CanSee`, `UserFor`, `Mutate`, `BroadcastPresence`); every roster,
  join, leave, user, typing, presence and health-count path goes through it, and `Perm.SeeInvisible` (owner only) is the only way to
  see them in full. Officers can't act on someone they can't see. The test-suite asserts rosters, broadcasts, typing, status/game,
  health count, voice masking and re-appearance. Honest limits: messages, reactions and pins carry the author's name; the voice
  list shows them while they are in a room; a guest-name collision suffix (" 2") can reveal that a name is taken; and an owner using an
  old hub sees an invisible member as an ordinary one (it doesn't know the `invisible` marker).
- Toggling visibility broadcasts at most as much as a status change and uses the same per-identity Control budget.

## 2.6.0 (avatars and profiles)

- No uploads: an avatar is one of 24 whitelisted ids (the art ships with the hub), the accent one of 8 ids. Unknown values refuse the whole
  `profile:set` and change nothing; a non-string JSON value can't throw into the receive loop. `about`/`playTimes`/character names go through
  `Names.Text` (NFKC, control and format characters removed, whitespace collapsed, hard length cut). The hub draws these as plain text.
- `profile:set` is budgeted twice: the per-identity Control budget (all non-chat messages) and a 1-per-2-s bucket; a refused set counts towards
  the flood disconnect and, being validated after the token is spent, can't be used to probe cheaply.
- Storage: `profiles.json` per personal identity, atomic write (tmp, flush, rename), tmp recovery only when the main file is missing, 8 MB
  size cap on load, at most 2000 profiles, every loaded value re-run through the same whitelists/bounds, keys must be in normal form. Guests'
  profiles live only in memory on their connection. Characters: at most 12 per member, bounded strings; a "seen again" refresh is written at most
  every 10 minutes, a new character or level change at once (bounded by the Control budget of the `game` message that causes it).
- Privacy is decided in `ProfileModule.Build`/`Lookup` only: `showChars`, hidden characters (never revealed to others, not even as a flag), and
  `visibleTo: officers`. An invisible member answers `profile:get` exactly like an offline one (no `online`, no `game`) unless the viewer has
  `SeeInvisible`; their avatar/accent changes are broadcast only to viewers who may see them. An invisible guest is "not found".
- `profile:reset` is owner-only (`Perm.ResetProfiles`), checked on the server. Logged by name only, never with profile text.
- Honest limits: avatar and accent are public (they appear in the roster and chat); the character *currently played* is in the public presence
  (`game.name`) whatever `showChars` says, and `profile:get` of a personal name tells a lodge member whether that name is an invited member
  (offline members answer with `found: true`).

## 2.7.0 (display names)

- A display name is cosmetic; the login name stays the only identity (messages, history, pins, reactions, rate limits, budgets, moderation
  all key on it). The hub shows `Display · @Login` in the hover card and profile card so the real name is never hidden.
- Anti-impersonation: a display name must not equal - after NFKC/whitespace normalization and a look-alike fold (0->o, 1/i/|/!->l, 3->e,
  5->s, separators dropped, case-insensitive) - any other personal login name (offline members included, read from the codes file at
  the time of the change), the name of a connected guest, or another member's stored display name. Format: 2-24 characters, letters/digits/
  space and `- _ ' . !` only, so no markup, `@`, `#` or zero-width tricks (those are removed by `Names`). Honest limit: the check runs at
  change time; a code created later for a name that someone already took as display name is not retroactively rejected, and homoglyphs
  from other scripts (Cyrillic `a`) are not folded - an owner can `profile:reset` the offender.
- Guests can't set one (no stored identity to keep it unique). Changes are limited to 1 per 60 s per identity on top of the profile
  bucket; refused attempts don't spend the minute but do spend the 1-per-2-s token and the Control budget, so uniqueness can't be probed cheaply.
- Stored in `profiles.json` and re-validated on load (format only); owner reset clears it. Invisible members: unchanged - the broadcast
  goes only to viewers who may see them and lookups answer like offline.

## 2.9.0 (sign-up cards, longer history, search)

- "Looking for group" cards: a chat message with `kind: "lfg"` and a structured payload. Created through the ordinary `msg` path, so it spends the Chat
  budget; join / maybe / leave / close / cancel are control messages and spend the Control budget. Everything is validated server side in one
  place (`Lfg`): title 1-60 and note up to 200 characters (cleaned like any text, `@everyone` / `@here` defused below officer), each role count a whole
  number 0-40 and at least one place, the start time within -10 minutes / +30 days, at most 3 open cards per author and channel. One entry per
  person (signing up again moves the entry; a refused move keeps the old place), at most 40 maybes, closed and cancelled cards never change again,
  and only the author or `Perm.Moderate` can close or cancel. Every change happens under the history store's lock, so two people taking the last
  place at once cannot both get it. A card in a channel the rank cannot see answers like a card that does not exist. The `text` of a card is a
  plain-text fallback that follows the state (older clients, search, pins and reply quotes show it); cards cannot be edited.

- History: `LODGE_HISTORY_MAX` (default 2000, 50-50 000) messages are kept per channel (was a fixed 200); `LODGE_HISTORY_MAX_KB` default 4096. Loading still
  reads only the tail of the file; a compaction serializes each record once and, when over the byte cap, drops the oldest until the file is at 75 % of it
  (so appends don't each cause another rewrite) - what is dropped is dropped from memory too. The welcome still carries the newest 100 per channel.
- `history:before` ({channel, before, beforeAt?, limit<=100}) answers `history:page` with up to 100 older messages (oldest first) and `hasMore`; a channel the
  rank can't see, a voice room, an unknown channel or an unknown id all answer with the same empty page. Own budget (burst 10, 1 per second) on top of Control.
- `search` ({q, channel?}) answers `search:result`: case-insensitive substring over the text and attachment names of the text channels the rank can see,
  newest first, at most 50 hits (`more` when there are others) with channel, id, author, time and a ~120 character snippet. The query is cleaned like any text,
  cut to 64 characters and needs 2. Budget of 6 per minute (a burst of 6, one more every 10 s); refused searches count towards the flood disconnect.

## 2.10.0 (one-time invites, device tokens)

The problem this removes: a permanent personal code pasted into a DM stays valid, in that chat's history, forever. Now a link lets one
person in once, and what that person keeps afterwards is a key that belongs to one device and can be revoked on its own.

| Item | How it works |
|---|---|
| Invites | Owner and officers create them (officers: ranks below their own only). 192 random bits (`i1.` + 32 base64url characters), stored only as a SHA-256 hash in `<data>/invites` with id, kind (person / guest), name, rank, creator, created, expiry (1 h to 30 d, default 7 d, `LODGE_INVITE_HOURS`), maximum uses (1; guest invites may allow up to 50) and the use count. The secret is returned once, to the creator, over the socket; the list (`admin.invites`) never carries it or its hash. Revoked, used-up and expired invites all answer the same way (410), and rows are forgotten 30 days after they end. |
| Link | `elanshub://lodge/join#<server>\|<invite>`. The secret lives in the #fragment only: a fragment is never sent to any server or proxy, and the client ignores a secret that arrives in a query or path. Old `https://site/lodge#invite=<code>` links still parse (a permanent code). |
| Exchange | `POST /exchange`, the invite in the `X-Lodge-Invite` header (or `invite` in the JSON body), never in the URL. The body (max 4 KB) names the device (PC name, editable) and carries a random device id. Under the codes lock, in this order: validate, check the device limits, create the person if the invite is for a new name, mark the invite used, write the device. Marking the invite used comes BEFORE the device exists, so a crash costs a new invite, never a free extra device; two simultaneous exchanges of one invite are serialized and exactly one wins (tested with 24 in parallel). |
| Device token | 256 random bits (`t1.` + 43 base64url characters), stored only as a SHA-256 hash in `<data>/devices` next to the person (or "guest"), device name, client device id, created, last seen and a COARSE last address (IPv4 /24, IPv6 /48). Looked up by hash, so a timing difference can't reveal anything about a random 256-bit value. Sent in `X-Lodge-Token` (`/ws`, `/files`, `/health`) and never in a URL (`?token=` is not read). A person has at most 10 devices (`LODGE_MAX_DEVICES_PER_PERSON`); joining again from the same device id replaces its old entry. |
| Revoking | `device.revoke` (your own, or - officer and owner - a lower-ranked member's; the owner may revoke anyone's) removes the row and closes that device's socket on the spot. A removal made with `lodge-admin revoke-device` is noticed by the 15 s check. Removing a person removes their devices. |
| Lockout | A wrong invite and a wrong code share the one per-IP `StrikeTable`: the 10th wrong guess is still 401, the next request 429 even with a valid invite or code. A used, expired or revoked invite is NOT a strike (somebody re-pasting an old link must not lock themselves out). |
| Retiring a code | `admin.retire` / `lodge-admin retire-code` (owner) replaces the person's code in the codes file with `!<random>`: the name, rank and devices stay, the old code can never match again (an entry that starts with `!` has no hash), and a connection still on the old code is disconnected within 15 s. Refused when the person has no device yet (would lock them out) unless forced from the shell. |
| Compatibility | Personal codes and the shared guest code work exactly as before; nobody is locked out by the update. A new Outpost feature-detects by the welcome (`tokens` feature and server >= 2.10.0) and `GET /features`; on an older server it keeps using the code. An old Outpost keeps using its code. A downgrade of the server after codes were retired leaves those people with the unguessable `!...` code (they can't sign in until the server is updated again). |
| Logs | Neither an invite, a token, a code nor any hash is ever logged; only invite and device ids, names and ranks (`Invite <id> used: new device <id> for <name>`, `<name> signed out device <id>`). The unit tests and the real-socket test scan the logs for every secret they create, their hashes and the part after the prefix. |

Honest limits: an invite is a bearer secret until it is used or expires, so send it privately and prefer short lifetimes; whoever opens
it first gets in (the creator sees "used" in the list at once). A device token is as private as the PC's Windows user (DPAPI). The coarse
address is only for recognising a device; behind a proxy it is the address the proxy reports. `lodge-admin` shows invite and device ids, never secrets.

`lodge-admin` also accepts names with any letters now (it was ASCII only, which contradicted the server's rule of `\p{L}\p{N}`), and no
longer lets a backslash into a name.

## 2.11.0 (raid / event calendar)

- Events live in their own atomic file `calendar.json` (tmp, flush, rename; a tmp is recovered only when the main file is missing; 8 MB cap on load; every loaded event needs an id, title and start; at most 800 kept). Bounds: 200 future events in total (a series that does not fit is refused whole), 40 upcoming per person, 60 sign-ups per event, repeat at most 26 weeks, start within a year, length 15 min to 24 h. Past events are dropped 30 days after their end (on load, on create and by the 15 s housekeeping).
- Permissions (`Roles.Can`): `CalendarCreate` = member and up (guests view and sign up only); edit / cancel = the organiser (a personal identity, matched by login name) or `CalendarManage` = officer and up. A guest name can never match an organiser.
- Validation is in one place (`Cal.Parse`): title and note cleaned like any text and `@everyone` / `@here` defused below officer, counts whole numbers 0-40, `tz` limited to letters, digits and `_/+-.()' ` (64 chars), `off` clamped to +-14 h. Sign-ups and every edit run under the store lock, so two people taking the last place cannot both get it; a refused move keeps the old answer; an edit cannot push a role below the people going in it.
- Budgets: create / edit / cancel spend the Chat budget (they post or refresh a chat card) on top of the Control budget every non-chat frame costs; a month fetch spends the Page budget. No new bucket. Refusals count towards the flood disconnect.
- Privacy: a sign-up is an explicit act, so the list shows the member by name even when they chose "Appear offline"; the calendar carries no status, game or online flag, and nothing in it depends on presence. The chat card is posted only to a text channel the organiser may see; guests still see the event itself through the calendar (it is a lodge-wide list), like the lodge's roster.
- Logs: event ids, counts and login names only (`X created calendar event <id> (3 date(s))`); never a title, note or comment. Tested (unit and real-socket).
- Honest limits: the calendar list is visible to every rank including guests (the chat card follows the channel's rank rule, the event does not); a guest's sign-up is keyed by the connection's name, which another guest may reuse later; the creator's zone id is stored with the event (it is not secret, it only drives recurrence).

## 2.12.0 (starting a lodge, inviting from the app, leaving)

- **Owner claim.** A brand-new lodge has no owner, so nobody can invite anybody. The server console is the proof of ownership: `install.sh` (fresh install) and
  `sudo lodge-admin claim-owner` make ONE `owner-claim` invite - 1 hour, single use, printed only to stdout with a warning, stored only as a SHA-256, never logged
  (the log says only that "an owner link was used" with the id). It exists only while there is no owner (checked at creation AND at use; a link that loses the race is burned),
  a newer link revokes the older one. The person types the name in the Outpost; Names rules apply. No permanent code is created. `claim-owner` refuses when an owner exists;
  `recover-owner NAME` / `--recover` only adds a device for an existing owner. Wrong/odd names never use the link up; a wrong secret counts towards the normal lockout.
- **Last owner.** One rule for leave, role change, remove and the console (`--force`): the lodge always keeps a Guild Master. Leaving removes the codes entry, every device and the
  pending device links of that name, and closes the sockets; messages stay as they were.
- **Web invite page.** The fragment (`#join=...`) is never sent to the server, so the page cannot log, count or leak it: it is static HTML (the lodge name and welcome text
  are HTML-encoded), one inline script allowed by a per-response nonce, `default-src 'none'` (no fetch/XHR/images/frames possible), `frame-ancestors 'none'`, `nosniff`,
  `no-referrer`, `no-store`. It never redirects by itself: the person clicks "Open in the Outpost".
- **Lodge name / welcome** are public (an invitee sees them): validated, capped at 40 / 280 characters, owner only.

## Outpost 2.40 (client): a link asks nothing

- Opening, pasting or receiving an invite link (elanshub:// click, web link, the Create-a-lodge guide) makes **no network request of any kind** until the person presses Join: the join banner shows only what is in the link (plus the lodge name and welcome text the app already knows when the link is for the lodge it is signed in to), so an arbitrary host in a link learns neither the IP address nor that the link was opened before the person consents. The `linkopen` and `invites2` self-tests assert zero requests before Join.

## Compatibility

- Wrong code on `/health` now answers **401** (before: 200 `{"ok":true}`); with an IP that's blocked, **429**.
- Uploads can now get **507** (storage full) and **429** (too many at once / too many per 10 minutes).
- Clients sending more than the budgets get messages dropped, and after sustained flooding a close with
  "Too many messages - slow down". Normal hub use stays far below.
- New invite codes are 32 hex characters (hub 2.x accepts any length of 6+ letters/digits). Existing codes work.
- The codes file stays `lodge:lodge 0640` in the data folder (the service edits it for the Guild Master panel);
  `lodge.env` stays `root:lodge 0640`. `install.sh` adds `codes.lock` (`lodge:lodge 0660`) and leaves systemd
  drop-ins alone.
- `?code=` is **off by default since 2.8** (`LODGE_ALLOW_QUERY_CODE=true` brings it back for 1.x hubs): such a request
  gets a clear 401 and is never checked.

## 2.8 hardening

- File names are cleaned on the server (`Names.FileName`) and again by the client: path parts, drive letters, the
  Windows-invalid characters `\ / : * ? " < > |`, control and format characters (bidi overrides, zero-width), leading and
  trailing dots/spaces, device names (CON, NUL, COM1...) and length. Downloads carry `Cache-Control: private, no-store` and
  `X-Content-Type-Options: nosniff`.
- Chat text loses bidi overrides/isolates and more than 3 blank lines in a row; `@everyone`/`@here` from below officer are
  defused (a zero-width space after the `@`).
- Guests always carry a " (guest)" tag and can not take a name that matches a login or display name under case, NFKC and
  the Cyrillic/Greek look-alike fold. Invite names may use any letters but not a look-alike of an existing name.
- Uploads: 2 at once per person (guests: per address), 2 minute limit per upload.
- Edit/delete need channel visibility; edit/delete of own messages match the author identity (guests: only messages from
  their current connection). Server-mute is kept per identity across reconnects. The voice room cap check and the join are
  one atomic step. A malformed message of any type is dropped and logged; the connection stays open.
- A client that pings (2.8+) and then goes silent for 75 s is closed (clients that never ping are left alone).
- History: edits/deletes/reactions are written after 5 quiet seconds and at shutdown (atomic temp file + rename).

## Backups and updates (`lodge-backup`, `lodge-update`)

- Backup archives contain the invite codes, so `/var/backups/lodge` is `root:root 0700` and each archive `root:root 0600`
  (created under `umask 077`, written as `.part` and renamed only after `tar -t` verifies it). The `lodge` user cannot
  read them. Treat any copy of an archive like the codes file itself; off-box copies belong somewhere equally private.
- The codes file is archived under the same `codes.lock` flock as `lodge-admin` and the service, so it is never captured
  half-written. `*.part` / `*.tmp` leftovers are excluded.
- `lodge-backup restore` refuses archives whose paths are not under the data folder (or contain `..`), extracts with
  `--no-same-owner`, then sets `lodge:lodge`, dirs 0750, files 0640, `codes.lock` 0660 - the same rules as `install.sh`.
  The replaced data is kept aside (it still holds codes) until the operator deletes it.
- `lodge-update` is fast-forward only; rewritten upstream history needs an explicit `--reset` plus confirmation. It never
  sends an invite code anywhere (the version is read from the source, not from an authenticated `/health`), and the
  health probe only talks to `127.0.0.1`. Systemd drop-ins and `lodge.env` ownership are left alone.

## 2.13.0 (shared flight times)

- Two messages, nothing else: `flights.share` (members and up; legs `{ "<faction>|<idA>><idB>": {t, n} }`, up to 500 per message, optional `names` `{id: name}`) and `flights.get` (anyone, guests included). Answers: `flights.shared {ok, accepted, rejected, why?}` (`why`: `guest`, `rate`, `empty`, `save`) and `flights.data {at, legs, names}`. The `flights` feature is in the welcome and in `/features`; an older server simply does not answer.
- Everything is validated server side in one place (`Flights`): faction exactly `Horde` or `Alliance`, ids 1-32 characters of letters/digits/space and `' . , -` (no quote, backslash, bracket, `|` or `>`), the two ids different, 1 <= t <= 1800 (rounded to 0.1), 1 <= n <= 50 and whole, anything not a number refused. A name is cleaned like any text (NFKC, control/format characters out), loses `" \ < > | [ ]` and is cut at 60. Entries that fail count as `rejected`; a malformed message costs nothing and never throws.
- Budgets: the per-identity `FlightShare` bucket (burst 1, one token per 10 minutes) on top of the Control budget; a refused share counts towards the flood disconnect. `flights.get` spends the Page budget (burst 10, 1 per second). Guests never contribute (`Perm.FlightShare` = member and up), they read.
- Storage: `<data>/flights.json`, atomic (tmp, flush, rename; a tmp is used only if the main file is missing), 12 MB cap on load, every loaded key/value re-validated. Bounds: 5000 distinct legs, 1500 legs per identity (one member cannot fill the table), 20 contributors per leg, 3000 names. Contributions older than 120 days are dropped (on every share and every 15 s); a value re-sent unchanged does not re-order anything and only refreshes its age after 30 days.
- Aggregate: per leg, each identity keeps ONE latest `{t, n, at}`; the merged value is the sample-weighted mean of the accepted contributions, `n` summed and capped at 50. Outliers: taken in the order they were stored, a contribution more than +-40 % from an aggregate that already holds n >= 5 is stored but held back; it joins only when another held-back identity agrees with it within 40 % (an ordinary value nearby does not rescue a stray one). The result is recomputed from the stored data alone, so it is the same after a restart. Honest limit: two colluding members can move a value together; the damage is bounded to a flight timer being off.
- Names are voted (most contributors win, ties alphabetical) and kept only for ids used by a leg.
- Logs: counts only (`X shared flight times (N stored, M refused)`); never a key, id, name or time. Tested (unit and real-socket).

## 2.14.0 (shared files)

- Who: `Perm.FilesRead` (everybody, guests included) lists and downloads; `Perm.FilesManage` (officer and owner) uploads a new slot, a new version, rolls back and deletes. The check is made server side in `FilesModule` for every upload, delete and roll back (not only in the client). `Perm.ShareFiles` (chat attachments) is unrelated.
- Transport: authenticated HTTP like `/files` (device token or code header, same wrong-credential lockout): `PUT /slots/new`, `PUT /slots/{id}`, `GET /slots/{id}/{v}`. Content-Length is required and checked against the maximum before anything is read; the body is streamed to `<id>.part` while a SHA-256 is computed (never held in memory), and must match the announced length and `X-Sha256` exactly. Only then is it renamed to its server-generated name (32 random hex characters + `.bin`) and the index (`index.json`, written tmp + atomic rename) updated; any failure deletes the partial file and changes nothing.
- File names are display-only: sanitised (`Names.FileName`: no path parts, drive letters, `: * ? " < > |`, control/format characters, device names) and never used as a path. Slot names and notes are cleaned text. Downloads are `application/octet-stream`, `Content-Disposition: attachment`, `nosniff`, `Cache-Control: private, no-store`.
- Refused: executable/script extensions (`exe dll com bat cmd ps1 psm1 vbs vbe js jse wsf wsh msi msp scr cpl jar lnk reg hta sys drv pif application appref-ms`, trailing dots/spaces ignored); a `.zip` is listed (System.IO.Compression, nothing extracted) and refused when any entry has such an extension, an absolute path, a drive letter, `:` or a `..` segment, has more than 20000 entries or isn't a valid zip. Nested archives are not looked into.
- Limits: `LODGE_FILES_MAX_MB` per file, `LODGE_FILES_TOTAL_MB` for all kept versions (space is reserved for running uploads and checked again at commit; the version that a new one pushes out counts as free), 200 slots, one running upload per person, 3 at once, `LODGE_FILES_UPLOADS_PER_10MIN` per person. The Kestrel body limit is raised to the larger of the two file limits only.
- `LODGE_FILES_ENABLED=0` makes the server stop advertising `files` and answer 403; nothing is deleted.
- Logs: who shared / deleted / rolled back and the size - never a file name, slot name, note or hash.

## 2.15.0 (the Atlas pool)

- Two messages: `atlas.share` (members and up) and `atlas.get` (anyone, guests included); answers `atlas.shared` and `atlas.data` (paged, about 200 KB). Feature `atlas` in the welcome and `/features`; `LODGE_ATLAS_ENABLED=0` stops advertising it and both messages answer `disabled`. Data is never deleted by switching it off.
- Everything is validated server side in one place (`Atlas`): ids and counts are whole numbers in fixed ranges (a string, boolean, fraction or NaN is refused), coordinates 0..1, times after 2001 and at most five minutes ahead (2.22; was one day), find sources match `npc|obj|vendor|quest:N` or `fish`, names are cleaned like any text (NFKC, control characters out) and lose `" \ < > | [ ]`, cut at 80. An entry that fails is refused whole. Unknown fields are dropped; character names are never accepted or stored, finds carry the member's Lodge name.
- Counting is cumulative and idempotent: per member and per field the server stores max(stored, received) and shows the sum over members, so repeats and replays cannot inflate anything. Plausibility bounds per member: kills per source <= 100000, all counters together <= 1000000, dropped events <= kills x 10, items per event <= 200, zones add up to <= kills, <= 6000 / 3000 / 400 sources, <= 700 finds, <= 10 points per npc. Honest limit: one member can still claim up to these bounds for himself; the pool is a convenience, not evidence.
- Budgets: a per-identity `AtlasShare` bucket (burst 3, one token per 2 minutes) on top of the Control budget; a refused share counts towards the flood disconnect; `atlas.get` spends the Page budget. Frame limit 64 KB.
- Storage: `<data>/atlas.json`, atomic (tmp, flush, rename; a tmp is used only if the main file is missing), 64 MB cap on load, every loaded value re-validated by the same rules, written at most every 30 s while busy, every 15 s housekeeping and at shutdown. It is inside the data folder, so `lodge-backup` includes it. Table bounds: 6000 vendors, 20000 quests, 8000 spawn npcs, 40000 revealed items, 2000 members.
- Logs: counts only (`X shared atlas observations (N stored, M refused)`); never an id, name or number. Tested (unit tests with a captured logger).

## 2.16.0 (the Armory)

- Four messages: `armory.put` (members and up), `armory.delete` (members and up), `armory.list` and `armory.get` (members and up; guests get an error with code `guest`); answers `armory.saved`, `armory.deleted`, `armory.chars`, `armory.char`. Feature `armory` in the welcome and `/features`; `LODGE_ARMORY_ENABLED=0` stops advertising it, `armory.put` answers why `disabled` and the rest answer an error.
- Opt-in by design: the server only holds what a member explicitly uploaded for a character they chose; it asks for nothing. A snapshot is rebuilt field by field in one place (`Armory.Clean`): unknown fields (bags, bank, gold, mail, zone ...) are dropped, text is cleaned like names, every number is clamped, ids and counts are whole numbers in fixed ranges, an item link must be the exact `|cAARRGGBB|Hitem:..|h[Name]|h|r` shape and belong to a worn item, a build code must match `ETB<n>-<base64url>`, lists are capped (400 talent nodes, 12 professions, 40 pets, 40 abilities, 100 known abilities, 24 slots, 40 links). A character is refused above 48 KB serialized (why `size`), with a key that is not `Realm-Name` shaped (why `invalid`) or with nothing to show (why `empty`).
- Ownership: the owner of a character is the identity of the connection (`ProfileRules.Key(name)`), never a field of the frame. A put with another member's key creates the caller's own character; `armory.delete` can only ever remove the caller's own (`key` or `all`); no rank (officer, owner) can read less or delete more. At most 30 characters per member (why `full`), 1500 per lodge.
- A member who leaves (`member.leave`) or is removed (`admin.remove`) loses their characters at once; the 15 s housekeeping also drops characters of identities that are no longer in the codes (skipped when the codes file lists nobody, so an unreadable file can never wipe the armory).
- Budgets: a per-identity `ArmoryPut` bucket (burst 30, one token per 8 s) on top of the Control budget, `armory.delete` spends Control, `armory.list` / `armory.get` spend the Page budget; refusals count towards the flood disconnect. Frame limit 64 KB.
- Storage: `<data>/armory.json`, atomic (tmp, flush, rename; a tmp is used only if the main file is missing), 96 MB cap on load, every loaded character re-validated by `Armory.Clean`, written at most every 10 s while busy, at once on a delete, every 15 s housekeeping and at shutdown.
- Logs: counts only (`X shared a character with the armory`, `X took N character(s) out of the armory`); never a name, guild, item or number from a snapshot. Tested with a captured logger.

## 2.17.0 (hosted datasets: the Classic baseline)

- Messages: `dataset.info` (members and up, Page budget), `dataset.delete` (officers and the owner); HTTP `PUT /datasets/{name}` (officers and the owner) and `GET /datasets/{name}` (members and up), behind the same credential gate as every route (a wrong token or code counts towards the same lockout). Guests are refused everywhere.
- The dataset name is never a path: it is looked up in a fixed list in code (`DatasetKinds`); anything else answers 404 before a byte is read. Content lives under server-generated 32-hex file names in `<data>/datasets/`, with `index.json` written by tmp + rename; startup removes `.part` / `.tmp` files and blobs nothing refers to.
- The bundle is never executed or parsed as SQL on the server. It is streamed to disk (never held in memory) with the announced `Content-Length` enforced (shorter and longer both refused), the SHA-256 checked, a size cap (`LODGE_CLASSIC_MAX_MB`, 40), and then only the zip's entry NAMES, announced sizes and the first two bytes of the dump (the gzip magic) are looked at: only the expected file names are accepted (one `ClassicDB_*.sql.gz`, the three licence files, `source.json`, `updates/*.sql` with a safe character set), no paths, no folders other than `updates/`, no duplicates, at most 2000 entries and 400 MB announced in total. Anything else is refused with 415 and nothing is kept.
- Replacing is atomic and a failed upload leaves the earlier content untouched. One upload per dataset at a time, 3 uploads at once, 5 uploads per person per 10 minutes (refused tries count), 6 downloads per member per dataset per hour, a 10 minute upload time limit.
- Downloads are always `application/octet-stream` attachments, `nosniff`, `no-store`; the Outposts verify the SHA-256 and unpack with their own name allowlist (never using an entry's path), so the server being wrong or replaced can at worst make an Outpost refuse a bundle.
- Who may replace what a whole lodge fetches is the officer rank and above (as for shared files); a malicious officer could host data that is useless but not harmful: the Outposts treat it as data (an SQL dump read by their own importer), never as something to run.
- Logs: who shared or removed a dataset and its size; never content.
## Realm status (feature `realms`)

- The Battle.net client secret exists only in the server's environment (`LODGE_BNET_CLIENT_SECRET`, in the env file with mode 600). It is sent only to `https://oauth.battle.net/token` (HTTP Basic, over TLS); the access token only as a `Bearer` header to `https://{region}.api.blizzard.com`, never in a URL. Neither is logged, put in a message to a client, or put in an error text: failures log the kind of failure (HTTP status or exception type) and nothing else. `server/Lodge.Tests/RealmsTests.cs` checks that the secret, its Basic encoding, the client id and the token never appear in logs, messages, the welcome or the feature list.
- Clients never ask for or send anything about realms, so there is no new request surface. The region is restricted to 2-3 lowercase letters and namespaces to `[a-z0-9-]`, so configuration cannot build another host name. The host names are fixed in code.
- One poll per interval for the whole lodge (not per member), 15 s timeouts, response bodies capped at 2 MB, backoff on errors. The Blizzard JSON is parsed defensively: names are cut to 60 characters, at most 16 realms, unknown shapes simply find nothing.
- The status is public information, so guests receive it too.
- Real Blizzard calls are never made by the tests (a fake HTTP handler).
## 2.22.0 (security: forgetting members, a trust model for the Atlas, caps)

From the sharing and security review. What changed, and the choices made:

- **One fan-out forgets a member** (`LodgeHub.PurgeMember`), called by `member.leave`, `admin.remove`, the owner's `admin.purge` and the console (`lodge-admin purge`, and `remove`, which queues one). Per store:
  - *Atlas pool*: **removed** - the member's kill / object / fishing counters, gathering nodes and fishing spots (the merged totals are rebuilt from everybody else), finds (the name and the time go with them), spawn points, and every vendor, quest, revealed-item and seen npc / object / quest row they authored.
  - *Flights*: **removed** - their leg values and names; the merged table is rebuilt.
  - *Calendar*: their **sign-ups are removed**; the **events they organised stay** (they belong to the lodge's calendar) and show `(former member)` as organiser. Chat event cards follow.
  - *Profile, Armory, Badges*: **deleted**.
  - *Chat history*: the **messages stay with their text** - a conversation with one side cut out makes no sense - but the **author becomes `(former member)`** (parentheses are not allowed in a login name, so nobody can ever take it). Their connection id, reactions and places on sign-up (LFG) cards are removed; quotes of their messages and pins show `(former member)`. Clients that are connected keep what they already show until they reload the history.
  - *Files*: uploaded files (chat and shared Files) **stay** - they belong to the lodge and expire on their own - but no longer name the uploader.
  - *Backups* keep what they held when they were taken until they rotate out (`LODGE_BACKUP_KEEP`).
- **Revert member (owner only).** `admin.purge { name, atlasOnly }`: `atlasOnly` (the default) takes one member's Atlas contributions out and leaves them a member - the undo for poisoned data. `atlasOnly: false` forgets everything about someone who is **no longer** a member (refused for a current member). The Atlas epoch changes, so every client fetches the whole pool again (a delta cannot express a removal). Console: `lodge-admin purge <name> [--atlas-only]` writes a request file into `<codes folder>/purge.d/` (atomic: private temp file, then rename; mode 0660; the server reads at most 20 per 15 s tick, ignores files over 512 bytes and names that are not plain names, and deletes every request it reads). Outpost: Settings > Guild Master > Members > **Revert Atlas**.
- **Authorship on every Atlas row.** Quests and seen npcs / objects / quests now record who sent them (`b` in `atlas.json`; rows saved before 2.22 have no author and cannot be reverted by member). Revealed items and seen rows stay **first-writer-wins**; a revert simply removes the author's rows and the next contributor's version goes in on their next share. A field that a later member filled in on a seen row goes with the row.
- **Times are clamped to now + 5 minutes** (was a day) for vendors, quests and finds, on the server and in the Outpost's parser (`AtlasObs`): a time far in the future could pin a vendor or quest against every real update. Nodes and fishing spots carry no time on the server.
- **The Atlas does not grow past its file limit.** `atlas.json` is only loaded up to 64 MB. A share is refused with `why: "full"` (logged, once per fill) once the file on disk is within 2 MB of that; a save that would exceed the limit is not written (the last good file stays). Revert a member to make room.
- **Guests get a coarser Atlas.** In `atlas.data`, finds show `by: "a member"` and the day only (`at` rounded down to 00:00 UTC). Members see what they saw before.
- **Chat uploads block programs and scripts** like the Files module (`exe dll bat cmd ps1 ... lnk jar`, checked on the real last extension, trailing dots and spaces ignored): HTTP 415 "Programs and scripts (.exe) can't be shared here". A zip is accepted (its content is not looked at in chat).
- **The flaky test**: `LfgTests.Many_simultaneous_joins_never_exceed_the_slots_or_double_up_a_person` failed about one run in six with "Collection was modified". A real race, not a test problem: the LFG join / leave / close handlers read the card's JSON (`Lfg.Is`, `IsAuthor`, the id for the broadcast) outside the store lock while another request was setting the card's text. All reads are under the lock now, and `Concurrent_joins_leaves_and_closes_never_throw` hammers it (it failed 1 run in 3 before the fix). Edit / delete / react still read a message outside the lock (not seen failing; same fix if they ever do).

The Outpost side (same release train): manifest folders, ids and game versions must be plain names that resolve inside AddOns / the backup folder, updates only from https GitHub hosts and the self-update needs a SHA-256, downloaded chat files open only by a picture / text / PDF extension, SavedVariables parsing is depth- and size-limited, and an `http://` lodge address (other than localhost / 127.0.0.1) is refused. See `src/CHANGELOG.md`.

## 2.23.0 (hunter field notes, feature `atlas5`)

- A new Atlas section, `fieldnotes`, validated in one place (`Atlas.ParseFieldNote`): whole numbers in fixed ranges, levels 1..80, a class from a fixed list, ability tokens that match `letters + rank`, coordinates 0..1, times after 2001 and at most five minutes ahead, names cleaned like every other name (control and `" \ < > | [ ]` characters out). An entry that fails is refused whole; unknown fields (the game's own hist / dead / data values) are dropped. No character names are accepted or stored.
- Caps: 60 npcs and 600 points per share, 600 npcs and 8000 points per member, 24 points / 10 maps / 24 taught tokens per npc and member, 20000 npcs in the pool, 40 points per npc in the merge. The share goes through the same rate bucket and the same size cap as the rest of the Atlas.
- Per member, the stored note only grows, so a resend or an old upload can never count twice. The merge is rebuilt from the members' notes, so a revert / leave (2.22) removes a member's part exactly (`PurgeResult.FieldNotes`).
- Privacy: the merge shows how many members saw a beast; members and up also get up to five Lodge display names (never the login when a display name is set, never a character name); guests get the counts, levels and positions, no names, and a time rounded down to the UTC day.

## Contribute to the Cairn (opt-in, feature `cairn`)

- Off by default. On only with `LODGE_CAIRN_ENABLED=1`, an https URL (plain http only to this machine; no user, query or fragment in it) and a key that looks like a Cairn key; otherwise it stays off and the log says why (the host only, never the key).
- What leaves the server is built from the Atlas pool's guest view and an allow-list of fields (`CairnRules.Records`): ids, counts, prices and map fractions. No names (member, character, display or login), no finds, notes, quests, seen tables or revealed items, no member counts; flight legs keyed by a node name are dropped (game text).
- The key travels only in the `Authorization` header over https, is never logged (tests check the log lines and `data/cairn-sent.json`), and can be kept in a separate file (`LODGE_CAIRN_KEY_FILE`).
- Members are told: the welcome lists `cairn` while the server contributes, and the Outpost shows it in Settings > Privacy. Members who switched Atlas sharing off contribute nothing (their data never reaches the pool); a purge lowers the totals, and the next contribution takes the difference back out of the Cairn.
- A failed or refused post changes nothing locally (only confirmed records are remembered); 401 / 429 stop the run until the next one.

## Receiving the Cairn's data (give to get, feature `cairn2`)

- Only a Lodge that contributes receives: the receiver is on exactly while the contributor is, and the Cairn itself serves the dataset only
  to a key in good standing (an accepted contribution within its grace period). A 401 / 403 from the Cairn deletes the stored copy at once
  and stops serving; the log says why (never the key), and the owner sees the Lodge's standing in `cairn.status`.
- The download is checked (SHA-256 of the file and of the JSON inside, sizes capped before and after gzip) and stored under fixed names in
  `data/cairn/`; the Lodge never parses or runs it. The signature is passed on unchanged, so members' Outposts can check it themselves.
- Handed on only to MEMBERS whose Atlas sharing is on (`atlas.sharing`, stored per identity in `data/cairn-sharing.json`; unknown = off),
  over the usual authenticated routes (`GET /cairn/manifest`, `GET /cairn/dataset`, same credential headers as everything else), never to
  guests, never anonymously, `private` caching only, 4 downloads per member per hour. The member's own statement is trusted as far as the
  Lodge can know it (a client that says "on" and uploads nothing is a matter for the owner, as with any member).

## Tests

```bash
dotnet test server/Lodge.Tests                                   # 591 unit tests (fake clock, temp folders)
dotnet build -c Release server/Lodge.Server
python server/tests/security_integration.py                     # 150 checks against local servers (python -X utf8 on Windows)
sudo bash server/tests/lodge_admin_test.sh                      # Linux/WSL, as root: 70 checks
sudo bash server/tests/lodge_ops_test.sh                        # Linux/WSL, as root: backup/restore/update/rollback, 41 checks
```

All use synthetic codes and temp folders; none talks to a real lodge.

## Remaining risks

- Homoglyphs from other scripts (e.g. Cyrillic "а" for Latin "a") are not folded by NFKC; guests still carry the
  Initiate frame and "guest" flag, so they can't take a rank, only a similar-looking name.
- The wrong-code lockout and rate limits trust the client IP from the reverse proxy; if the proxy allows spoofed
  `X-Forwarded-For`, per-IP limits weaken. Proxy/NPM settings are host-side and outside this repo. The server itself takes
  `X-Forwarded-For`/`-Proto` only from loopback and the `LODGE_TRUSTED_PROXIES` networks (`TrustedProxies`, tested with the real
  ASP.NET middleware in `TrustedProxyTests`: a neighbour of the proxy, an outsider and a spoofed chain keep their own address).
- Budgets are in memory: a restart resets them (and the strike table).
- The shared guest code, if enabled, is only as private as the people who have it; prefer personal codes or guest invites.
- Permanent codes that were never retired stay valid (that is the compatibility promise). Retire them (`lodge-admin retire-code <name>`) once a person's PCs have their own keys.
