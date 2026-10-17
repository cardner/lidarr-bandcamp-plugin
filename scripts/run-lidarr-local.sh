#!/usr/bin/env bash
# Download Lidarr develop linux-core-x64 and run with this plugin (no Docker).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
LIDARR_VER="${LIDARR_VER:-3.1.6.5078}"
LIDARR_DIR="${LIDARR_DIR:-/tmp/lidarr-e2e}"
DATA_DIR="${DATA_DIR:-/tmp/lidarr-data}"
PORT="${PORT:-8687}"

"$ROOT/scripts/build-plugin.sh"

if [[ ! -x "$LIDARR_DIR/Lidarr/Lidarr" ]]; then
  mkdir -p "$LIDARR_DIR"
  curl -fsSL -o /tmp/lidarr.tgz \
    "https://github.com/Lidarr/Lidarr/releases/download/v${LIDARR_VER}/Lidarr.develop.${LIDARR_VER}.linux-core-x64.tar.gz"
  tar -xzf /tmp/lidarr.tgz -C "$LIDARR_DIR"
fi

mkdir -p "$DATA_DIR/plugins/cursor-project/Lidarr.Plugin.Bandcamp"
cp -f "$ROOT/artifacts/plugin/Lidarr.Plugin.Bandcamp.dll" \
  "$DATA_DIR/plugins/cursor-project/Lidarr.Plugin.Bandcamp/"

if [[ ! -f "$DATA_DIR/config.xml" ]]; then
  cat > "$DATA_DIR/config.xml" <<EOF
<Config>
  <BindAddress>*</BindAddress>
  <Port>${PORT}</Port>
  <SslPort>6868</SslPort>
  <EnableSsl>False</EnableSsl>
  <LaunchBrowser>False</LaunchBrowser>
  <AuthenticationMethod>None</AuthenticationMethod>
  <AuthenticationRequired>DisabledForLocalAddresses</AuthenticationRequired>
  <Branch>develop</Branch>
  <LogLevel>Info</LogLevel>
  <AnalyticsEnabled>False</AnalyticsEnabled>
</Config>
EOF
fi

echo "Starting Lidarr on http://127.0.0.1:${PORT} (data=$DATA_DIR)"
echo "Install only ONE copy of the plugin DLL under plugins/ (duplicates break provider Test)."
cd "$LIDARR_DIR/Lidarr"
exec ./Lidarr -nobrowser -data="$DATA_DIR"
