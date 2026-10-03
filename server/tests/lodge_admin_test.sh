#!/usr/bin/env bash
# lodge-admin safety tests - run as root on Linux (e.g. WSL): sudo bash server/tests/lodge_admin_test.sh
# Uses a temp folder, a test group and synthetic names; never touches /var/lib/lodge or real codes,
# and never prints a code.
set -uo pipefail
umask 0022
HERE="$(cd "$(dirname "$0")" && pwd)"
ADMIN="${LODGE_ADMIN_BIN:-$HERE/../deploy/lodge-admin}"
T="$(mktemp -d)"
groupadd -f lodgetest
export LODGE_CODES_FILE="$T/codes" LODGE_OWNER="root:lodgetest"
pass=0; fail=0
ok()  { echo "PASS $1"; pass=$((pass+1)); }
bad() { echo "FAIL $1 ${2:-}"; fail=$((fail+1)); }
mode_owner() { stat -c '%a %U:%G' "$1"; }
codes() { grep -cv '^[[:space:]]*#' "$LODGE_CODES_FILE" 2>/dev/null || echo 0; }

# --- add creates the file 0640 root:lodgetest, 128-bit code, nothing else readable
out="$(bash "$ADMIN" add Elan owner)"
[ "$(mode_owner "$LODGE_CODES_FILE")" = "640 root:lodgetest" ] && ok "add: file is 0640 root:lodgetest" || bad "add mode" "$(mode_owner "$LODGE_CODES_FILE")"
[[ "$out" =~ \(owner\):\ [0-9a-f]{32}$ ]] && ok "add: 32 hex character code" || bad "add output format"
printf '# a comment that must survive\n' >> "$LODGE_CODES_FILE"
bash "$ADMIN" add "Bob the Hunter" officer >/dev/null
bash "$ADMIN" add Freya >/dev/null

# --- role / remove keep unrelated entries, comments, owner and mode
before_elan="$(grep '^Elan:' "$LODGE_CODES_FILE")"
bash "$ADMIN" role freya veteran >/dev/null
grep -q '^Freya:[0-9a-f]\{32\}:veteran$' "$LODGE_CODES_FILE" && ok "role: case-insensitive name, rank set" || bad "role"
bash "$ADMIN" remove "bob the hunter" >/dev/null
! grep -qi '^bob the hunter:' "$LODGE_CODES_FILE" && ok "remove: entry gone" || bad "remove"
[ "$(grep '^Elan:' "$LODGE_CODES_FILE")" = "$before_elan" ] && grep -q '^# a comment that must survive$' "$LODGE_CODES_FILE" \
  && ok "unrelated entries and comments kept" || bad "unrelated entries changed"
[ "$(mode_owner "$LODGE_CODES_FILE")" = "640 root:lodgetest" ] && ok "role/remove keep 0640 root:lodgetest" || bad "mode after edits"
[ "$(bash "$ADMIN" show elan | wc -c)" -eq 33 ] && ok "show prints the code (32 hex + newline)" || bad "show"
bash "$ADMIN" list | grep -q '^Elan  *owner$' && ok "list shows names and ranks" || bad "list"

# --- failed commands leave the file intact and no temp files
sum="$(sha256sum "$LODGE_CODES_FILE")"
bash "$ADMIN" role Nobody officer >/dev/null 2>&1; bash "$ADMIN" remove Nobody >/dev/null 2>&1; bash "$ADMIN" add Elan >/dev/null 2>&1
bash "$ADMIN" add 'x;rm -rf /' >/dev/null 2>&1
[ "$(sha256sum "$LODGE_CODES_FILE")" = "$sum" ] && ok "failed commands change nothing" || bad "failed command changed the file"
[ -z "$(ls -A "$T" | grep '^\.codes\.')" ] && ok "no temp files left after failures" || bad "temp files left"

# --- under umask 0022, no file in the folder is ever readable by others, and readers never see a partial list
watch_log="$T.watch"; : > "$watch_log"
( end=$((SECONDS+6)); while [ $SECONDS -lt $end ]; do
    find "$T" -maxdepth 1 -type f -perm -o=r >> "$watch_log" 2>/dev/null
    if [ -f "$LODGE_CODES_FILE" ] && awk -F: '!/^#/ && NF && NF!=3 {bad=1} END{exit !bad}' "$LODGE_CODES_FILE" 2>/dev/null; then echo PARTIAL >> "$watch_log"; fi
  done ) &
watcher=$!
for i in $(seq 1 20); do bash "$ADMIN" add "Para$i" member >/dev/null & done   # 20 at once
wait $(jobs -p | grep -v "^$watcher$") 2>/dev/null
for i in $(seq 1 10); do bash "$ADMIN" role "Para$i" veteran >/dev/null & bash "$ADMIN" remove "Para$((i+10))" >/dev/null & done
wait $(jobs -p | grep -v "^$watcher$") 2>/dev/null
wait $watcher
[ ! -s "$watch_log" ] && ok "no world-readable temp file, no partial list seen (umask 0022)" || bad "watcher saw" "$(sort -u "$watch_log" | head -3 | tr '\n' ' ')"
n_para="$(grep -c '^Para' "$LODGE_CODES_FILE")"; n_vet="$(grep -c '^Para[0-9]*:[0-9a-f]*:veteran$' "$LODGE_CODES_FILE")"
[ "$n_para" -eq 10 ] && [ "$n_vet" -eq 10 ] && ok "20 parallel adds + 20 parallel edits: no lost updates" || bad "concurrency" "para=$n_para veteran=$n_vet"
[ "$(mode_owner "$LODGE_CODES_FILE")" = "640 root:lodgetest" ] && ok "mode/owner intact after concurrency" || bad "final mode"
[ "$(stat -c '%a' "$LODGE_CODES_FILE.lock")" = "660" ] && ok "lock file 0660 for the service" || bad "lock mode"

# --- signal during a change: the old file stays, the temp file is removed
sum="$(sha256sum "$LODGE_CODES_FILE")"
( exec 9<"$LODGE_CODES_FILE.lock"; flock 9; sleep 3 ) & holder=$!    # keep the lock busy
sleep 0.3
bash "$ADMIN" add Interrupted >/dev/null 2>&1 & victim=$!
sleep 0.5; kill -TERM $victim 2>/dev/null; wait $victim 2>/dev/null; wait $holder
[ "$(sha256sum "$LODGE_CODES_FILE")" = "$sum" ] && [ -z "$(ls -A "$T" | grep '^\.codes\.')" ] \
  && ok "interrupted command: file intact, no temp left" || bad "interrupted command"

rm -rf "$T" "$watch_log"
echo "lodge-admin: $pass passed, $fail failed"
[ "$fail" -eq 0 ]
