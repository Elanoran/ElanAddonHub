#!/usr/bin/env bash
# Install or update the Lodge server on Ubuntu (systemd). Safe to run again for updates.
#   git clone https://github.com/Elanoran/ElanAddonHub /opt/lodge-src
#   sudo /opt/lodge-src/server/deploy/install.sh
# Updates later:  cd /opt/lodge-src && sudo git pull && sudo server/deploy/install.sh
set -euo pipefail

REPO_DIR="$(cd "$(dirname "$0")/../.." && pwd)"
APP_DIR=/opt/lodge
DATA_DIR=/var/lib/lodge
ENV_FILE=/etc/lodge/lodge.env

if [ "$(id -u)" -ne 0 ]; then echo "Run with sudo"; exit 1; fi

if ! command -v dotnet >/dev/null 2>&1 || ! dotnet --list-sdks 2>/dev/null | grep -q '^8\.'; then
  echo "== Installing .NET 8 SDK"
  apt-get update -y
  apt-get install -y dotnet-sdk-8.0
fi

id lodge >/dev/null 2>&1 || useradd --system --home-dir "$DATA_DIR" --shell /usr/sbin/nologin lodge
mkdir -p "$APP_DIR" "$DATA_DIR" /etc/lodge
chown lodge:lodge "$DATA_DIR"
chmod 750 "$DATA_DIR"

echo "== Building"
rm -rf "$APP_DIR.new"
DOTNET_CLI_TELEMETRY_OPTOUT=1 dotnet publish "$REPO_DIR/server/Lodge.Server" -c Release -r linux-x64 \
  --self-contained true -p:PublishSingleFile=true -o "$APP_DIR.new" --nologo -v q
rm -rf "$APP_DIR.old"
if [ -d "$APP_DIR" ]; then mv "$APP_DIR" "$APP_DIR.old"; fi
mv "$APP_DIR.new" "$APP_DIR"
chmod 755 "$APP_DIR/lodge"

if [ ! -f "$ENV_FILE" ]; then
  cp "$REPO_DIR/server/deploy/lodge.env.example" "$ENV_FILE"
fi
chown root:lodge "$ENV_FILE"
chmod 640 "$ENV_FILE"

# personal invite codes + the command to manage them
install -m 755 "$REPO_DIR/server/deploy/lodge-admin" /usr/local/bin/lodge-admin
touch /etc/lodge/codes
chown root:lodge /etc/lodge/codes
chmod 640 /etc/lodge/codes
if ! grep -q '[^[:space:]]' /etc/lodge/codes && ! grep -qE '^LODGE_CODE=.{6,}' "$ENV_FILE"; then
  echo "== No invite codes yet. Make one per friend:  sudo lodge-admin add <name>"
fi

install -m 644 "$REPO_DIR/server/deploy/lodge.service" /etc/systemd/system/lodge.service
systemctl daemon-reload
systemctl enable lodge >/dev/null
systemctl restart lodge

PORT="$(grep -E '^LODGE_URLS=' "$ENV_FILE" | sed -E 's/.*:([0-9]+).*/\1/')"
BASE="$(grep -E '^LODGE_PATHBASE=' "$ENV_FILE" | cut -d= -f2-)"
for i in 1 2 3 4 5 6 7 8 9 10; do
  if curl -fsS "http://127.0.0.1:${PORT:-5280}${BASE}/health"; then echo; echo "== Lodge is running"; exit 0; fi
  sleep 1
done
echo "== Lodge did not answer - check: journalctl -u lodge -n 50"
exit 1
