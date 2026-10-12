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

## Tests

```bash
dotnet test server/Lodge.Tests                                   # 261 unit tests (fake clock, temp folders)
dotnet build -c Release server/Lodge.Server
python server/tests/security_integration.py                     # 118 checks against local servers (python -X utf8 on Windows)
sudo bash server/tests/lodge_admin_test.sh                      # Linux/WSL, as root: 41 checks
sudo bash server/tests/lodge_ops_test.sh                        # Linux/WSL, as root: backup/restore/update/rollback, 41 checks
```

All use synthetic codes and temp folders; none talks to a real lodge.

## Remaining risks

- Homoglyphs from other scripts (e.g. Cyrillic "а" for Latin "a") are not folded by NFKC; guests still carry the
  Initiate frame and "guest" flag, so they can't take a rank, only a similar-looking name.
- The wrong-code lockout and rate limits trust the client IP from the reverse proxy; if the proxy allows spoofed
  `X-Forwarded-For`, per-IP limits weaken. Proxy/NPM settings are host-side and outside this repo.
- Budgets are in memory: a restart resets them (and the strike table).
- The shared guest code, if enabled, is only as private as the people who have it; prefer personal codes or guest invites.
- Permanent codes that were never retired stay valid (that is the compatibility promise). Retire them (`lodge-admin retire-code <name>`) once a person's PCs have their own keys.
