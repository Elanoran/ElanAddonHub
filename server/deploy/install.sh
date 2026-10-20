#!/usr/bin/env bash
# Install or update the Lodge server on Ubuntu (systemd). Safe to run again for updates.
#   git clone https://github.com/Elanoran/ElanAddonHub /opt/lodge-src
#   sudo /opt/lodge-src/server/deploy/install.sh
# Updates later:  sudo lodge-update   (backup, fast-forward, install, health check, automatic rollback)
#   (this script installs lodge-update, lodge-backup and the daily backup timer; run it by hand the first time)
set -euo pipefail

REPO_DIR="$(cd "$(dirname "$0")/../.." && pwd)"
APP_DIR=/opt/lodge
DATA_DIR=/var/lib/lodge
ENV_FILE=/etc/lodge/lodge.env

if [ "$(id -u)" -ne 0 ]; then echo "Run with sudo"; exit 1; fi

# The build needs the .NET SDK whose major version the server targets (Lodge.Server.csproj, net10.0 since Lodge 2.27).
# The result is self-contained: the service itself needs no .NET runtime on the machine.
NET_MAJOR="$(grep -oE '<TargetFramework>net[0-9]+' "$REPO_DIR/server/Lodge.Server/Lodge.Server.csproj" | grep -oE '[0-9]+$' || true)"
NET_MAJOR="${NET_MAJOR:-10}"
have_sdk() { command -v dotnet >/dev/null 2>&1 && dotnet --list-sdks 2>/dev/null | grep "^${NET_MAJOR}\." >/dev/null; }
if ! have_sdk; then
  echo "== Installing the .NET ${NET_MAJOR} SDK (dotnet-sdk-${NET_MAJOR}.0)"
  apt-get update -y || true
  if ! apt-get install -y "dotnet-sdk-${NET_MAJOR}.0" || ! have_sdk; then
    echo "install.sh: the .NET ${NET_MAJOR} SDK is missing and 'apt-get install dotnet-sdk-${NET_MAJOR}.0' did not provide it." >&2
    echo "  Ubuntu 26.04 carries it in its own archive; on an Ubuntu release that doesn't, add the .NET backports" >&2
    echo "  (sudo add-apt-repository ppa:dotnet/backports) or Microsoft's package feed, then run this again." >&2
    echo "  Nothing was changed - the running Lodge is untouched." >&2
    exit 1
  fi
fi

id lodge >/dev/null 2>&1 || useradd --system --home-dir "$DATA_DIR" --shell /usr/sbin/nologin lodge
mkdir -p "$APP_DIR" "$DATA_DIR" /etc/lodge
chown lodge:lodge "$DATA_DIR"
chmod 750 "$DATA_DIR"

echo "== Building"
# 2.31.1: the build never runs as root. Restoring packages and compiling can run code from the repo and its dependencies,
# so it happens as the unprivileged user lodge-build in its own folder; root only copies the finished app into place.
# The source is the committed tree (git archive of HEAD: server/ and shared/), never stray files in the checkout.
BUILD_USER=lodge-build
BUILD_HOME=/var/cache/lodge-build
id "$BUILD_USER" >/dev/null 2>&1 || useradd --system --home-dir "$BUILD_HOME" --shell /usr/sbin/nologin "$BUILD_USER"
install -d -o "$BUILD_USER" -g "$BUILD_USER" -m 750 "$BUILD_HOME"
rm -rf "$BUILD_HOME/src" "$BUILD_HOME/out" "$APP_DIR.new"
install -d -o "$BUILD_USER" -g "$BUILD_USER" -m 750 "$BUILD_HOME/src"
git -C "$REPO_DIR" archive HEAD server shared | tar -x -C "$BUILD_HOME/src"
chown -R "$BUILD_USER:$BUILD_USER" "$BUILD_HOME/src"
# from server/ so its global.json (any 10.0.x SDK) applies, not the repo root's (the Windows app's SDK pin)
runuser -u "$BUILD_USER" -- env -i PATH="$PATH" HOME="$BUILD_HOME" DOTNET_CLI_HOME="$BUILD_HOME" NUGET_PACKAGES="$BUILD_HOME/nuget" \
  DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 \
  bash -c 'cd "$1/src/server" && dotnet publish Lodge.Server -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o "$1/out" --nologo -v q' _ "$BUILD_HOME"
[ -f "$BUILD_HOME/out/lodge" ] || { echo "install.sh: the build did not produce $BUILD_HOME/out/lodge - nothing was changed" >&2; exit 1; }
cp -a "$BUILD_HOME/out" "$APP_DIR.new"
chown -R root:root "$APP_DIR.new"
rm -rf "$APP_DIR.old"
if [ -d "$APP_DIR" ]; then mv "$APP_DIR" "$APP_DIR.old"; fi
mv "$APP_DIR.new" "$APP_DIR"
chmod 755 "$APP_DIR/lodge"

if [ ! -f "$ENV_FILE" ]; then
  cp "$REPO_DIR/server/deploy/lodge.env.example" "$ENV_FILE"
fi
chown root:lodge "$ENV_FILE"
chmod 640 "$ENV_FILE"

# personal invite codes live in the data folder (the hub's Guild Master panel edits them);
# 1.x kept them in /etc/lodge/codes - move them once
install -m 755 "$REPO_DIR/server/deploy/lodge-admin" /usr/local/bin/lodge-admin
install -m 755 "$REPO_DIR/server/deploy/lodge-backup" /usr/local/bin/lodge-backup
install -m 755 "$REPO_DIR/server/deploy/lodge-update" /usr/local/bin/lodge-update
CODES="$DATA_DIR/codes"
if [ -f /etc/lodge/codes ] && [ ! -f "$CODES" ]; then
  mv /etc/lodge/codes "$CODES"
  echo "== Moved invite codes to $CODES"
fi
umask 077
touch "$CODES" "$CODES.lock" "$DATA_DIR/invites" "$DATA_DIR/devices"
chown lodge:lodge "$CODES" "$CODES.lock" "$DATA_DIR/invites" "$DATA_DIR/devices"   # the service edits the codes, the one-time
chmod 640 "$CODES" "$DATA_DIR/invites" "$DATA_DIR/devices"                         # invites (only hashes) and the device list (token
chmod 660 "$CODES.lock"                                                            # hashes); lodge-admin and the service share codes.lock
umask 022

# only the main unit file is replaced: drop-ins in /etc/systemd/system/lodge.service.d/ are left alone
install -m 644 "$REPO_DIR/server/deploy/lodge.service" /etc/systemd/system/lodge.service
# daily backup (04:15 + random delay). The archives hold the invite codes: root-only folder and files.
install -d -m 700 -o root -g root /var/backups/lodge
chmod 700 /var/backups/lodge
install -m 644 "$REPO_DIR/server/deploy/lodge-backup.service" /etc/systemd/system/lodge-backup.service
install -m 644 "$REPO_DIR/server/deploy/lodge-backup.timer" /etc/systemd/system/lodge-backup.timer
systemctl daemon-reload
systemctl enable --now lodge-backup.timer >/dev/null
systemctl enable lodge >/dev/null
systemctl restart lodge

PORT="$(grep -E '^LODGE_URLS=' "$ENV_FILE" | sed -E 's/.*:([0-9]+).*/\1/')"
BASE="$(grep -E '^LODGE_PATHBASE=' "$ENV_FILE" | cut -d= -f2-)"
# the service needs a moment to start: try quietly for up to 15 s
for i in $(seq 1 15); do
  sleep 1
  if curl -fs "http://127.0.0.1:${PORT:-5280}${BASE}/health" >/dev/null 2>&1; then
    echo "== Lodge is running"
    # A brand-new lodge has no Guild Master yet: print the one-time OWNER LINK (to this console only - it is never logged).
    # Updates (lodge-update sets LODGE_SKIP_CLAIM) and lodges that already have an owner print nothing.
    if [ "${LODGE_SKIP_CLAIM:-}" != 1 ]; then
      /usr/local/bin/lodge-admin claim-owner --if-needed || echo "   (run  sudo lodge-admin claim-owner  to make the owner link)"
    fi
    exit 0
  fi
done
echo "== Lodge did not answer - check: journalctl -u lodge -n 50"
exit 1
