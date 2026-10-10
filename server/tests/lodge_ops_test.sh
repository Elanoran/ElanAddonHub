#!/usr/bin/env bash
# lodge-backup / lodge-update tests - run as root on Linux (e.g. WSL): sudo bash server/tests/lodge_ops_test.sh
# Everything lives in a temp folder: fake data dir, fake git origin/checkout, fake systemctl/curl on PATH and a fake
# install.sh. Never touches /var/lib/lodge, /opt, /etc or a real service; synthetic codes only.
set -uo pipefail
umask 0022
HERE="$(cd "$(dirname "$0")" && pwd)"
DEPLOY="$HERE/../deploy"
T="$(mktemp -d)"
trap 'rm -rf "$T"' EXIT
groupadd -f lodgetest
export GIT_AUTHOR_NAME=t GIT_AUTHOR_EMAIL=t@example.invalid GIT_COMMITTER_NAME=t GIT_COMMITTER_EMAIL=t@example.invalid
pass=0; fail=0
ok()  { echo "PASS $1"; pass=$((pass+1)); }
bad() { echo "FAIL $1 ${2:-}"; fail=$((fail+1)); }
mode_owner() { stat -c '%a %U:%G' "$1"; }

# ---- sandbox
mkdir -p "$T/bin" "$T/data/history" "$T/data/files"
cat > "$T/bin/systemctl" <<'EOF'
#!/bin/bash
echo "systemctl $*" >> "$LOG"
if [ "$1" = stop ]; then echo stopped > "$STATE"; fi
if [ "$1" = start ]; then echo running > "$STATE"; fi
exit 0
EOF
cat > "$T/bin/curl" <<'EOF'
#!/bin/bash
# healthy unless the service is stopped or the checked-out Hub.cs is "BROKEN"
echo "curl $*" >> "$LOG"
[ "$(cat "$STATE" 2>/dev/null)" = stopped ] && exit 7
grep -q BROKEN "$SRC/server/Lodge.Server/Hub.cs" 2>/dev/null && exit 22
echo '{"ok":true}'
EOF
chmod +x "$T/bin/systemctl" "$T/bin/curl"
export LOG="$T/log" STATE="$T/state" SRC="$T/src"
: > "$LOG"; echo running > "$STATE"
cat > "$T/lodge.env" <<EOF
LODGE_URLS=http://127.0.0.1:5999
LODGE_PATHBASE=/lodge
EOF
export LODGE_ENV_FILE="$T/lodge.env" LODGE_DATA="$T/data" LODGE_BACKUP_DIR="$T/backups" LODGE_OWNER="root:lodgetest"
export LODGE_SYSTEMCTL="$T/bin/systemctl" LODGE_CURL="$T/bin/curl" LODGE_HEALTH_TRIES=3 LODGE_HEALTH_SLEEP=0
unset LODGE_BACKUP_KEEP LODGE_BACKUP_FILES
BK="$DEPLOY/lodge-backup"; UP="$DEPLOY/lodge-update"

echo 'Alice:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa:owner' > "$T/data/codes"
touch "$T/data/codes.lock"
echo '{"x":1}' > "$T/data/profiles.json"
echo '{"msg":"hi"}' > "$T/data/history/general.jsonl"
echo upload > "$T/data/files/f1.bin"
echo half > "$T/data/files/f2.bin.part"
echo temp > "$T/data/pins.json.tmp"
chown -R root:lodgetest "$T/data"; chmod 640 "$T/data/codes"; chmod 660 "$T/data/codes.lock"

# ================= backup
LODGE_BACKUP_STAMP=20260101-0415 bash "$BK" >/dev/null 2>&1 && ok "backup: runs" || bad "backup run"
A="$T/backups/lodge-20260101-0415.tar.gz"
[ -f "$A" ] && ok "backup: lodge-YYYYmmdd-HHMM.tar.gz created" || bad "backup file name" "$(ls "$T/backups")"
[ "$(mode_owner "$A")" = "600 root:root" ] && ok "backup: archive 0600 root:root" || bad "archive mode" "$(mode_owner "$A")"
[ "$(mode_owner "$T/backups")" = "700 root:root" ] && ok "backup: folder 0700 root:root" || bad "folder mode" "$(mode_owner "$T/backups")"
L="$(tar -tzf "$A")"
echo "$L" | grep -q 'data/codes$' && echo "$L" | grep -q 'data/profiles.json$' && echo "$L" | grep -q 'data/history/general.jsonl$' \
  && ok "backup: codes, profiles and history included" || bad "contents" "$L"
echo "$L" | grep -q 'data/files/f1.bin$' && ok "backup: files/ included by default" || bad "files default"
! echo "$L" | grep -qE '\.part$|\.tmp$' && ok "backup: .part and .tmp files excluded" || bad "temp files in archive"
[ -z "$(ls "$T/backups" | grep '\.part')" ] && ok "backup: no .part left behind" || bad "part left"

LODGE_BACKUP_FILES=0 LODGE_BACKUP_STAMP=20260101-0500 bash "$BK" >/dev/null 2>&1
! tar -tzf "$T/backups/lodge-20260101-0500.tar.gz" | grep -q 'data/files/f1.bin' && ok "backup: LODGE_BACKUP_FILES=0 skips uploads" || bad "files not skipped"
printf 'LODGE_BACKUP_FILES=0\n' >> "$T/lodge.env"
LODGE_BACKUP_STAMP=20260101-0501 bash "$BK" >/dev/null 2>&1
! tar -tzf "$T/backups/lodge-20260101-0501.tar.gz" | grep -q 'data/files/f1.bin' && ok "backup: setting read from lodge.env" || bad "env file setting"
sed -i '/LODGE_BACKUP_FILES/d' "$T/lodge.env"

# the codes lock is really held while archiving: a lodge-admin-style locker must wait
( exec 8<"$T/data/codes.lock"; flock 8; sleep 2 ) & holder=$!
sleep 0.3; s=$SECONDS
LODGE_BACKUP_STAMP=20260101-0600 bash "$BK" >/dev/null 2>&1
[ $((SECONDS-s)) -ge 1 ] && ok "backup: waits for the codes.lock holder" || bad "did not wait for the lock"
wait $holder

# rotation
rm -f "$T"/backups/*
for h in 01 02 03 04 05 06 07; do LODGE_BACKUP_KEEP=3 LODGE_BACKUP_STAMP=20260102-${h}00 bash "$BK" >/dev/null 2>&1; done
n="$(ls "$T"/backups/lodge-*.tar.gz | wc -l)"
[ "$n" -eq 3 ] && ls "$T/backups" | head -1 | grep -q 0500 && ls "$T/backups" | tail -1 | grep -q 0700 \
  && ok "rotation: keeps the newest 3 of 7" || bad "rotation" "$(ls "$T/backups" | tr '\n' ' ')"
LODGE_BACKUP_KEEP=abc bash "$BK" >/dev/null 2>&1 && bad "bad KEEP accepted" || ok "backup: invalid LODGE_BACKUP_KEEP fails"
[ "$(ls "$T"/backups/lodge-*.tar.gz | wc -l)" -eq 3 ] && ok "rotation: failed run deletes nothing" || bad "failed run rotated"

# failure -> non-zero and nothing half-written
LODGE_DATA="$T/nope" bash "$BK" >/dev/null 2>&1 && bad "missing data dir accepted" || ok "backup: missing data folder exits non-zero"

# ================= restore round trip
LODGE_BACKUP_STAMP=20260103-0415 bash "$BK" >/dev/null 2>&1
R="$T/backups/lodge-20260103-0415.tar.gz"
echo 'Mallory:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb:member' >> "$T/data/codes"
echo '{"x":2}' > "$T/data/profiles.json"
rm -f "$T/data/history/general.jsonl"
: > "$LOG"
echo no | bash "$BK" restore "$R" >/dev/null 2>&1 && bad "restore without confirmation" || ok "restore: declined prompt changes nothing"
grep -q Mallory "$T/data/codes" && ok "restore: data untouched after 'no'" || bad "data changed after no"
echo yes | bash "$BK" restore "$R" >"$T/restore.out" 2>&1 && ok "restore: confirmed run succeeds" || bad "restore failed" "$(tail -3 "$T/restore.out")"
! grep -q Mallory "$T/data/codes" && grep -q '"x":1' "$T/data/profiles.json" && [ -f "$T/data/history/general.jsonl" ] \
  && ok "restore: contents back to the backup state" || bad "restore contents"
[ "$(mode_owner "$T/data/codes")" = "640 root:lodgetest" ] && [ "$(mode_owner "$T/data/codes.lock")" = "660 root:lodgetest" ] \
  && [ "$(mode_owner "$T/data")" = "750 root:lodgetest" ] && ok "restore: owner and modes fixed (codes 640, lock 660, dir 750)" || bad "restore modes" "$(mode_owner "$T/data/codes") $(mode_owner "$T/data/codes.lock") $(mode_owner "$T/data")"
aside="$(ls -d "$T"/data.before-restore-* 2>/dev/null | head -1)"
[ -n "$aside" ] && grep -q Mallory "$aside/codes" && ok "restore: previous data moved aside, not deleted" || bad "aside missing"
grep -n 'systemctl' "$LOG" | sed 's/^[0-9]*://' | tr '\n' ';' | grep -q 'systemctl stop lodge;.*systemctl start lodge' && ok "restore: stopped then started the service" || bad "service order" "$(cat "$LOG")"
grep -q 'health check OK' "$T/restore.out" && ok "restore: health checked" || bad "restore health"
printf 'x' > "$T/evil.tar.gz"; tar -czf "$T/evil.tar.gz" -C "$T" bin
bash "$BK" restore "$T/evil.tar.gz" --yes >/dev/null 2>&1 && bad "foreign archive accepted" || ok "restore: archive not made of the data folder is refused"
[ -f "$T/data/codes" ] && ok "restore: refused archive left data in place" || bad "data gone after refusal"

# ================= update
mkdir -p "$T/origin.git"; git init -q --bare -b main "$T/origin.git"
git clone -q "$T/origin.git" "$T/work" 2>/dev/null
mkdir -p "$T/work/server/Lodge.Server"
setver() { printf 'class H { public const string Version = "%s"; }\n' "$1" > "$T/work/server/Lodge.Server/Hub.cs"; }
setver 1.0.0; git -C "$T/work" add -A; git -C "$T/work" commit -qm v1; git -C "$T/work" push -q origin HEAD:main 2>/dev/null
git clone -q -b main "$T/origin.git" "$SRC" 2>/dev/null
cat > "$T/install.sh" <<'EOF'
#!/bin/bash
echo "install $(git -C "$SRC" rev-parse --short HEAD)" >> "$LOG"
echo running > "$STATE"
EOF
chmod +x "$T/install.sh"
export LODGE_SRC="$SRC" LODGE_INSTALL_SH="$T/install.sh" LODGE_BACKUP_BIN="$BK"
rm -rf "$T"/backups/* "$T"/data.before-restore-*

# up to date
bash "$UP" >"$T/up.out" 2>&1; grep -q 'Already up to date' "$T/up.out" && ok "update: nothing new -> already up to date" || bad "up to date" "$(cat "$T/up.out")"

# fast-forward
setver 1.1.0; git -C "$T/work" commit -qam v11; git -C "$T/work" push -q origin HEAD:main 2>/dev/null
new="$(git -C "$T/work" rev-parse --short HEAD)"; : > "$LOG"
bash "$UP" >"$T/up.out" 2>&1 && ok "update: fast-forward succeeds" || bad "ff update" "$(tail -5 "$T/up.out")"
grep -qE "OK  1\.0\.0 \([0-9a-f]+\) -> 1\.1\.0 \($new\)" "$T/up.out" && ok "update: prints old -> new version" || bad "version line" "$(tail -2 "$T/up.out")"
[ "$(git -C "$SRC" rev-parse --short HEAD)" = "$new" ] && grep -q "install $new" "$LOG" && ok "update: checkout moved and install.sh ran on the new commit" || bad "ff state"
[ "$(ls "$T"/backups/lodge-*.tar.gz 2>/dev/null | wc -l)" -ge 1 ] && ok "update: backup taken before installing" || bad "no backup"

# non fast-forward (history rewritten upstream)
git -C "$T/work" reset -q --hard HEAD~1; setver 1.2.0; git -C "$T/work" commit -qam rewritten
git -C "$T/work" push -q -f origin HEAD:main 2>/dev/null
before="$(git -C "$SRC" rev-parse HEAD)"; : > "$LOG"
bash "$UP" >"$T/up.out" 2>&1; rc=$?
[ "$rc" -eq 2 ] && grep -q 'history was rewritten' "$T/up.out" && grep -q -- '--reset' "$T/up.out" && ok "update: non-fast-forward detected, --reset offered (exit 2)" || bad "nff detect" "rc=$rc $(tail -3 "$T/up.out")"
[ "$(git -C "$SRC" rev-parse HEAD)" = "$before" ] && ! grep -q install "$LOG" && ok "update: nothing changed or installed on non-fast-forward" || bad "nff changed state"
echo no | bash "$UP" --reset >/dev/null 2>&1 && bad "--reset without confirmation" || ok "update --reset: declined prompt aborts"
[ "$(git -C "$SRC" rev-parse HEAD)" = "$before" ] && ok "update --reset: declined -> untouched" || bad "reset applied without yes"
echo yes | bash "$UP" --reset >"$T/up.out" 2>&1 && grep -q -- '-> 1.2.0' "$T/up.out" && [ "$(git -C "$SRC" rev-parse HEAD)" = "$(git -C "$T/work" rev-parse HEAD)" ] \
  && ok "update --reset: confirmed -> hard-reset to origin/main and installed" || bad "reset path" "$(tail -3 "$T/up.out")"

# health failure -> rollback
good="$(git -C "$SRC" rev-parse HEAD)"
setver BROKEN; git -C "$T/work" commit -qam broken; git -C "$T/work" push -q origin HEAD:main 2>/dev/null
: > "$LOG"; nb="$(ls "$T"/backups | wc -l)"
bash "$UP" >"$T/up.out" 2>&1; rc=$?
[ "$rc" -eq 1 ] && grep -q 'ROLLED BACK' "$T/up.out" && ok "update: unhealthy new version -> ROLLED BACK, exit 1" || bad "rollback report" "rc=$rc $(tail -4 "$T/up.out")"
[ "$(git -C "$SRC" rev-parse HEAD)" = "$good" ] && ok "update: checkout restored to the recorded commit" || bad "rollback commit"
[ "$(grep -c '^install' "$LOG")" -eq 2 ] && tail -1 "$LOG" | grep -q . && grep '^install' "$LOG" | tail -1 | grep -q "install $(git -C "$SRC" rev-parse --short HEAD)" \
  && ok "update: install.sh ran again for the old commit" || bad "rollback install" "$(cat "$LOG")"
[ "$(ls "$T"/backups | wc -l)" -gt "$nb" ] && ok "update: a backup was taken before the failed attempt" || bad "no backup before failed update"

# backup failure aborts before anything changes
git -C "$T/work" reset -q --hard HEAD~1; setver 1.3.0; git -C "$T/work" commit -qam v13; git -C "$T/work" push -q -f origin HEAD:main 2>/dev/null
printf '#!/bin/bash\nexit 1\n' > "$T/badbackup"; chmod +x "$T/badbackup"; before="$(git -C "$SRC" rev-parse HEAD)"; : > "$LOG"
echo yes | LODGE_BACKUP_BIN="$T/badbackup" bash "$UP" --reset >"$T/up.out" 2>&1 && bad "update continued after backup failure" || ok "update: failing backup aborts the update"
[ "$(git -C "$SRC" rev-parse HEAD)" = "$before" ] && ! grep -q '^install' "$LOG" && ok "update: aborted before moving the checkout or installing" || bad "abort state"

echo "lodge-backup / lodge-update: $pass passed, $fail failed"
[ "$fail" -eq 0 ]
