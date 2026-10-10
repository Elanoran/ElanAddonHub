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

## Compatibility

- Wrong code on `/health` now answers **401** (before: 200 `{"ok":true}`); with an IP that's blocked, **429**.
- Uploads can now get **507** (storage full) and **429** (too many at once / too many per 10 minutes).
- Clients sending more than the budgets get messages dropped, and after sustained flooding a close with
  "Too many messages - slow down". Normal hub use stays far below.
- New invite codes are 32 hex characters (hub 2.x accepts any length of 6+ letters/digits). Existing codes work.
- The codes file stays `lodge:lodge 0640` in the data folder (the service edits it for the Guild Master panel);
  `lodge.env` stays `root:lodge 0640`. `install.sh` adds `codes.lock` (`lodge:lodge 0660`) and leaves systemd
  drop-ins alone.
- `?code=` still works by default for 1.x hubs. Turn it off with `LODGE_ALLOW_QUERY_CODE=false` once everyone
  runs hub 2.x.

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
dotnet test server/Lodge.Tests                                   # 110 unit tests (fake clock, temp folders)
dotnet build -c Release server/Lodge.Server
python server/tests/security_integration.py                     # 48 checks against two local servers
sudo bash server/tests/lodge_admin_test.sh                      # Linux/WSL, as root: 15 checks
sudo bash server/tests/lodge_ops_test.sh                        # Linux/WSL, as root: backup/restore/update/rollback, 41 checks
```

All use synthetic codes and temp folders; none talks to a real lodge.

## Remaining risks

- Homoglyphs from other scripts (e.g. Cyrillic "а" for Latin "a") are not folded by NFKC; guests still carry the
  Initiate frame and "guest" flag, so they can't take a rank, only a similar-looking name.
- The wrong-code lockout and rate limits trust the client IP from the reverse proxy; if the proxy allows spoofed
  `X-Forwarded-For`, per-IP limits weaken. Proxy/NPM settings are host-side and outside this repo.
- Budgets are in memory: a restart resets them (and the strike table).
- The shared guest code, if enabled, is only as private as the people who have it; prefer personal codes.
