#!/usr/bin/env bash
# Build Lidarr.Plugin.Bandcamp.dll (+ optional zip for GitHub Release upload).
# Usage:
#   ./scripts/build-plugin.sh           # DLL only → artifacts/plugin/
#   ./scripts/build-plugin.sh --zip     # also writes Lidarr.Plugin.Bandcamp.net8.0.zip
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

MAKE_ZIP=0
PLUGIN_VERSION=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --zip)
      MAKE_ZIP=1
      shift
      ;;
    --version)
      PLUGIN_VERSION="${2:?--version requires a value like 0.2.0}"
      shift 2
      ;;
    -h|--help)
      sed -n '2,6p' "$0"
      exit 0
      ;;
    *)
      echo "Unknown arg: $1" >&2
      exit 1
      ;;
  esac
done

if [[ ! -f ext/Lidarr/src/NzbDrone.Core/Lidarr.Core.csproj ]]; then
  echo "Cloning Lidarr plugins branch into ext/Lidarr ..."
  git clone --depth 1 --branch plugins https://github.com/Lidarr/Lidarr.git ext/Lidarr
  sed -i -e 's/<AssemblyVersion>10\.0\.0\.\*/<AssemblyVersion>2.0.0.0/' ext/Lidarr/src/Directory.Build.props
fi

BUILD_ARGS=(
  src/Lidarr.Plugin.Bandcamp/Lidarr.Plugin.Bandcamp.csproj
  -c Release
  -p:TreatWarningsAsErrors=false
)
if [[ -n "$PLUGIN_VERSION" ]]; then
  BUILD_ARGS+=("-p:Version=${PLUGIN_VERSION}")
fi

dotnet build "${BUILD_ARGS[@]}"

DLL="src/Lidarr.Plugin.Bandcamp/bin/Release/net8.0/Lidarr.Plugin.Bandcamp.dll"
mkdir -p artifacts/plugin
cp -f "$DLL" artifacts/plugin/
echo "Wrote artifacts/plugin/Lidarr.Plugin.Bandcamp.dll ($(wc -c < artifacts/plugin/Lidarr.Plugin.Bandcamp.dll) bytes)"

if [[ "$MAKE_ZIP" -eq 1 ]]; then
  ZIP="artifacts/Lidarr.Plugin.Bandcamp.net8.0.zip"
  rm -f "$ZIP"
  (cd artifacts/plugin && zip -9 "../Lidarr.Plugin.Bandcamp.net8.0.zip" Lidarr.Plugin.Bandcamp.dll)
  echo "Wrote $ZIP ($(wc -c < "$ZIP") bytes)"
fi
