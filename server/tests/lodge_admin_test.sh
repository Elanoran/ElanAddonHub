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

# ================================================================ 2.10: one-time invites, devices, retire-code
export LODGE_ENV_FILE="$T/lodge.env"; printf 'LODGE_PUBLIC_URL=https://example.test/lodge\n' > "$LODGE_ENV_FILE"
INV="$T/invites"; DEV="$T/devices"
link="$(bash "$ADMIN" invite "Åse Ørsted" veteran 2>"$T/err")"
[[ "$link" =~ ^elanshub://lodge/join#example\.test/lodge\|i1\.[A-Za-z0-9_-]{32}$ ]] && ok "invite: link on stdout, secret in the #fragment, any-letter name" || bad "invite link format" "$link"
grep -q 'SEND THIS LINK PRIVATELY' "$T/err" && ok "invite: privacy warning goes to stderr" || bad "invite warning"
secret="${link##*|}"
! grep -q -- "$secret" "$INV" && grep -q "$(printf '%s' "$secret" | sha256sum | cut -d' ' -f1)" "$INV" \
  && ok "invite: only the hash is stored" || bad "invite storage"
[ "$(mode_owner "$INV")" = "640 root:lodgetest" ] && ok "invites file 0640 root:lodgetest" || bad "invites mode" "$(mode_owner "$INV")"
bash "$ADMIN" invite "åse ørsted" >/dev/null 2>&1 && bad "second pending invite for the same person" || ok "invite: a second pending invite for the same name is refused"
out="$(bash "$ADMIN" invites)"
echo "$out" | grep -q 'Åse Ørsted.*veteran.*pending' && ! echo "$out" | grep -q -- "$secret" && ok "invites: listed as pending, no secret shown" || bad "invites list" "$out"
id="$(echo "$out" | awk '/Ørsted/ {print $1}')"
bash "$ADMIN" revoke-invite "$id" >/dev/null && bash "$ADMIN" invites | grep -q 'revoked' && ok "revoke-invite marks it revoked" || bad "revoke-invite"
bash "$ADMIN" revoke-invite deadbeef >/dev/null 2>&1 && bad "revoke of an unknown invite" || ok "revoke-invite: unknown id refused"
bash "$ADMIN" invite Nova --days 2 >/dev/null 2>&1
row="$(awk -F'\t' '$4=="Nova"' "$INV")"
[ "$(echo "$row" | awk -F'\t' '{print $8-$7}')" -eq 172800 ] && ok "invite --days 2 expires in 48 h" || bad "invite days" "$row"
bash "$ADMIN" invite Quick --days 99 >/dev/null 2>&1 && bad "99 day invite accepted" || ok "invite: more than 30 days refused"
bash "$ADMIN" invite 'bad:name' >/dev/null 2>&1 && bad "colon name accepted" || ok "invite: ':' in a name refused"
bash "$ADMIN" invite 'bad\name' >/dev/null 2>&1 && bad "backslash name accepted" || ok "invite: backslash in a name refused"
bash "$ADMIN" invite --guest --uses 3 --days 1 >/dev/null 2>&1
awk -F'\t' '$3=="guest" && $9==3 && $5=="guest"' "$INV" | grep -q . && ok "guest invite with 3 uses" || bad "guest invite"
bash "$ADMIN" add "Zoë Ångström" member >/dev/null && grep -q '^Zoë Ångström:' "$LODGE_CODES_FILE" && ok "add accepts any letters" || bad "unicode add"

# devices: rows as the server writes them (id, token hash, name, role, device, client id, created, last seen, address)
now_s="$(date +%s)"
printf '# Lodge devices\nd1d1d1d1d1\t%s\tNova\tmember\tNova-PC\tcid1\t%s\t%s\t203.0.113.x\ne2e2e2e2e2\t%s\tZoë Ångström\tmember\tLaptop\tcid2\t%s\t%s\t198.51.100.x\n' \
  "$(printf t1 | sha256sum | cut -d' ' -f1)" "$now_s" "$now_s" "$(printf t2 | sha256sum | cut -d' ' -f1)" "$now_s" "$now_s" > "$DEV"
d_all="$(bash "$ADMIN" devices)"; d_zoe="$(bash "$ADMIN" devices "zoë ångström")"
[[ "$d_all" == *Nova-PC* && "$d_zoe" == *Laptop* && "$d_zoe" != *Nova-PC* ]] && ok "devices: listed and filtered by name" || bad "devices list" "$d_zoe"
bash "$ADMIN" add Nova >/dev/null; # Nova has no code yet: a plain member entry for the retire tests
bash "$ADMIN" retire-code Freya >/dev/null 2>&1 && bad "retire without a device" || ok "retire-code: refused when the person has no device (would lock them out)"
bash "$ADMIN" retire-code Freya --force >/dev/null 2>&1 && ok "retire-code --force works" || bad "retire force"
grep -q '^Freya:![0-9a-f]\{32\}:veteran$' "$LODGE_CODES_FILE" && ok "retire-code: code replaced by an unusable marker, rank kept" || bad "retired line" "$(grep '^Freya' "$LODGE_CODES_FILE")"
bash "$ADMIN" retire-code Nova >/dev/null && grep -q '^Nova:!' "$LODGE_CODES_FILE" && ok "retire-code: with a device it just works" || bad "retire nova"
bash "$ADMIN" retire-code Nova >/dev/null 2>&1 && bad "double retire" || ok "retire-code: already retired is refused"
s_out="$(bash "$ADMIN" show Nova 2>&1)"; l_out="$(bash "$ADMIN" list)"
[[ "$s_out" == *"no permanent code"* && "$l_out" == *"no permanent code"* ]] && ok "show/list say the code is retired" || bad "show retired" "$s_out"
bash "$ADMIN" add Nova >/dev/null 2>&1 && bad "add over a retired person" || ok "add: refuses a name that is already in"
bash "$ADMIN" revoke-device d1d1d1d1d1 >/dev/null && ! grep -q '^d1d1d1d1d1' "$DEV" && grep -q '^e2e2e2e2e2' "$DEV" && grep -q '^#' "$DEV" && ok "revoke-device removes that row only" || bad "revoke-device"
bash "$ADMIN" revoke-device zzz >/dev/null 2>&1 && bad "bad device id accepted" || ok "revoke-device: bad id refused"
bash "$ADMIN" remove "Zoë Ångström" >/dev/null && ! grep -q 'Ångström' "$DEV" && ok "remove: the person's devices go with them" || bad "remove devices"
[ -z "$(ls -A "$T" | grep '^\.codes\.')" ] && ok "no temp files left after the invite tests" || bad "temp files after invites"

# ================================================================ 2.12: owner claim, recovery, last-owner guard
orig_codes="$LODGE_CODES_FILE"
T2="$(mktemp -d)"; export LODGE_CODES_FILE="$T2/codes"; INV="$T2/invites"; DEV="$T2/devices"
# an owner exists in the main test lodge: claim-owner refuses
LODGE_CODES_FILE="$orig_codes" bash "$ADMIN" claim-owner >/dev/null 2>&1 && bad "claim-owner with an owner" || ok "claim-owner: refused while an owner exists"
# an empty lodge: it prints one link
bash "$ADMIN" add Mia member >/dev/null          # a member only, still no owner
claim="$(bash "$ADMIN" claim-owner 2>"$T2/err")"
[[ "$claim" =~ ^elanshub://lodge/join#example\.test/lodge\|i1\.[A-Za-z0-9_-]{32}$ ]] && ok "claim-owner: link on stdout, normal invite format" || bad "claim link" "$claim"
grep -q 'OWNER LINK' "$T2/err" && grep -q 'ONCE' "$T2/err" && ok "claim-owner: the warning goes to stderr" || bad "claim warning"
csecret="${claim##*|}"
row="$(awk -F'\t' '$3=="owner-claim"' "$INV")"
[ "$(echo "$row" | awk -F'\t' '{print $8-$7}')" -eq 3600 ] && [ "$(echo "$row" | awk -F'\t' '{print $9}')" -eq 1 ] && ok "claim-owner: 1 hour, single use" || bad "claim row" "$row"
! grep -q -- "$csecret" "$INV" && grep -q "$(printf '%s' "$csecret" | sha256sum | cut -d' ' -f1)" "$INV" && ok "claim-owner: only the hash is stored" || bad "claim storage"
[ "$(mode_owner "$INV")" = "640 root:lodgetest" ] && ok "claim-owner: invites file keeps 0640 root:lodgetest" || bad "claim mode" "$(mode_owner "$INV")"
claim2="$(bash "$ADMIN" claim-owner 2>/dev/null)"
[ "$(awk -F'\t' '$3=="owner-claim" && $11=="0"' "$INV" | wc -l)" -eq 1 ] && ok "claim-owner: a new link retires the older open one" || bad "one open claim"
bash "$ADMIN" claim-owner --if-needed >/dev/null 2>&1; [ "$(awk -F'\t' '$3=="owner-claim"' "$INV" | wc -l)" -eq 3 ] && ok "claim-owner makes a link again while there is no owner" || bad "claim again"
# recovery needs an owner
bash "$ADMIN" claim-owner --recover >/dev/null 2>&1 && bad "recover without an owner" || ok "claim-owner --recover: refused when there is no owner"
bash "$ADMIN" recover-owner Mia >/dev/null 2>&1 && bad "recover-owner for a member" || ok "recover-owner: refused for someone who is not an owner"
bash "$ADMIN" add Boss owner >/dev/null
bash "$ADMIN" claim-owner >/dev/null 2>&1 && bad "claim with an owner" || ok "claim-owner: refused as soon as an owner exists"
bash "$ADMIN" claim-owner --if-needed >/dev/null 2>&1 && ok "claim-owner --if-needed: quiet success when there is an owner" || bad "if-needed"
rlink="$(bash "$ADMIN" recover-owner boss 2>"$T2/err2")"
[[ "$rlink" =~ ^elanshub://lodge/join#example\.test/lodge\|i1\.[A-Za-z0-9_-]{32}$ ]] && ok "recover-owner: a link for the existing owner" || bad "recover link" "$rlink"
awk -F'\t' '$3=="owner-claim" && $4=="Boss" && $11=="0"' "$INV" | grep -q . && ok "recover-owner: bound to the owner's name" || bad "recover row"
grep -q 'NEW device' "$T2/err2" && ok "recover-owner: says it only adds a device" || bad "recover note"
[ "$(grep -c '^Boss:' "$LODGE_CODES_FILE")" -eq 1 ] && ok "recover-owner changes no codes" || bad "recover changed codes"
bash "$ADMIN" claim-owner --recover >/dev/null 2>&1 && ok "claim-owner --recover works with an owner" || bad "recover flag"
# last owner guard
bash "$ADMIN" role Boss member >/dev/null 2>&1 && bad "demoted the last owner" || ok "role: the last owner can't be demoted"
bash "$ADMIN" remove Boss >/dev/null 2>&1 && bad "removed the last owner" || ok "remove: the last owner can't be removed"
grep -q '^Boss:[0-9a-f]\{32\}:owner$' "$LODGE_CODES_FILE" && ok "the last owner is untouched" || bad "owner changed"
bash "$ADMIN" role Mia owner >/dev/null && bash "$ADMIN" role Boss officer >/dev/null && ok "with a second owner the first can step down" || bad "step down"
bash "$ADMIN" invite Mia >/dev/null 2>&1; bash "$ADMIN" remove Mia >/dev/null 2>&1 && bad "removed the last owner (Mia)" || ok "remove: Mia is now the last owner"
bash "$ADMIN" role Mia officer --force >/dev/null 2>&1 && ok "--force overrides from the console" || bad "force"
export LODGE_CODES_FILE="$orig_codes"; INV="$T/invites"; DEV="$T/devices"
rm -rf "$T2"

# ================================================================ 2.22: purge requests for the running server
T3="$(mktemp -d)"; export LODGE_CODES_FILE="$T3/codes"
bash "$ADMIN" add Keeper owner >/dev/null; bash "$ADMIN" add Poisoner >/dev/null; bash "$ADMIN" add Leaver >/dev/null
bash "$ADMIN" purge Poisoner --atlas-only >/dev/null && [ "$(grep -c . "$T3/purge.d/"*.req)" -ge 1 ] && grep -q "^atlas	Poisoner$" "$T3/purge.d/"*.req && ok "purge --atlas-only queues one atlas request and keeps the member" || bad "purge atlas"
grep -q '^Poisoner:' "$LODGE_CODES_FILE" && ok "purge --atlas-only leaves the codes entry" || bad "purge removed the member"
bash "$ADMIN" purge Poisoner >/dev/null 2>&1 && bad "full purge of a current member accepted" || ok "purge: refuses a full purge of someone who is still a member"
bash "$ADMIN" remove Leaver >/dev/null && grep -q "^all	Leaver$" "$T3/purge.d/"*.req && ok "remove queues a full purge" || bad "remove did not queue a purge"
bash "$ADMIN" purge "../etc" --atlas-only >/dev/null 2>&1 && bad "bad name accepted" || ok "purge: bad names are refused"
[ -z "$(ls -A "$T3/purge.d" | grep '^\.req\.')" ] && [ "$(stat -c '%a' "$T3/purge.d")" = "770" ] && ok "purge.d: 0770, no temp files left" || bad "purge.d mode/temp"
export LODGE_CODES_FILE="$orig_codes"; rm -rf "$T3"

rm -rf "$T" "$watch_log"
echo "lodge-admin: $pass passed, $fail failed"
[ "$fail" -eq 0 ]
