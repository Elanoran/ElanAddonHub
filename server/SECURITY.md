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

## Tests

```bash
dotnet test server/Lodge.Tests                                   # 41 unit tests (fake clock, temp folders)
dotnet build -c Release server/Lodge.Server
python server/tests/security_integration.py                     # 25 checks against two local servers
sudo bash server/tests/lodge_admin_test.sh                      # Linux/WSL, as root: 15 checks
```

All use synthetic codes and temp folders; none talks to a real lodge.

## Remaining risks

- Homoglyphs from other scripts (e.g. Cyrillic "а" for Latin "a") are not folded by NFKC; guests still carry the
  Initiate frame and "guest" flag, so they can't take a rank, only a similar-looking name.
- The wrong-code lockout and rate limits trust the client IP from the reverse proxy; if the proxy allows spoofed
  `X-Forwarded-For`, per-IP limits weaken. Proxy/NPM settings are host-side and outside this repo.
- Budgets are in memory: a restart resets them (and the strike table).
- The shared guest code, if enabled, is only as private as the people who have it; prefer personal codes.
