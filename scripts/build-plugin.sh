#!/usr/bin/env bash
# Build Lidarr.Plugin.Bandcamp package for Lidarr System → Plugins.
#
# Usage:
#   ./scripts/build-plugin.sh                  # stage flat files → artifacts/plugin/
#   ./scripts/build-plugin.sh --zip            # also write Lidarr.Plugin.Bandcamp.net8.0.zip
#   ./scripts/build-plugin.sh --zip --version 0.2.1
#
# Zip MUST contain files at the ARCHIVE ROOT (not nested src/... paths):
#   Lidarr.Plugin.Bandcamp.dll
#   Lidarr.Plugin.Bandcamp.deps.json   # required by Lidarr PluginLoadContext
#   Lidarr.Plugin.Bandcamp.pdb         # optional but matches Slskd layout
#
# Do NOT upload a GitHub Actions multi-file artifact as the Release asset —
# that produces a nested zip Lidarr cannot load (GetFiles is non-recursive).
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
      PLUGIN_VERSION="${2:?--version requires a value like 0.2.1}"
      shift 2
      ;;
    -h|--help)
      sed -n '2,16p' "$0"
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

OUT="src/Lidarr.Plugin.Bandcamp/bin/Release/net8.0"
DLL="$OUT/Lidarr.Plugin.Bandcamp.dll"
DEPS="$OUT/Lidarr.Plugin.Bandcamp.deps.json"
PDB="$OUT/Lidarr.Plugin.Bandcamp.pdb"

test -f "$DLL" || { echo "ERROR: missing $DLL" >&2; exit 1; }
test -f "$DEPS" || { echo "ERROR: missing $DEPS (PluginLoadContext needs it)" >&2; exit 1; }

STAGE="artifacts/plugin"
rm -rf "$STAGE"
mkdir -p "$STAGE"
cp -f "$DLL" "$DEPS" "$STAGE/"
[[ -f "$PDB" ]] && cp -f "$PDB" "$STAGE/"

echo "Staged $STAGE:"
ls -la "$STAGE"

if [[ "$MAKE_ZIP" -eq 1 ]]; then
  ZIP="artifacts/Lidarr.Plugin.Bandcamp.net8.0.zip"
  rm -f "$ZIP"
  # -j is wrong if we ever add runtimes/; use cd so entries are flat at zip root
  (cd "$STAGE" && zip -9 -r "../Lidarr.Plugin.Bandcamp.net8.0.zip" .)
  echo "Wrote $ZIP ($(wc -c < "$ZIP") bytes)"
  echo "Zip contents (must be flat — no src/ prefix):"
  unzip -l "$ZIP"
  # Guardrail: DLL must be at archive root
  unzip -Z1 "$ZIP" | grep -qx 'Lidarr.Plugin.Bandcamp.dll' \
    || { echo "ERROR: Lidarr.Plugin.Bandcamp.dll not at zip root" >&2; exit 1; }
  unzip -Z1 "$ZIP" | grep -qx 'Lidarr.Plugin.Bandcamp.deps.json' \
    || { echo "ERROR: deps.json not at zip root" >&2; exit 1; }
fi
