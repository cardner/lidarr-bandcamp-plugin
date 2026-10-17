#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

if [[ ! -f ext/Lidarr/src/NzbDrone.Core/Lidarr.Core.csproj ]]; then
  echo "Cloning Lidarr plugins branch into ext/Lidarr ..."
  git clone --depth 1 --branch plugins https://github.com/Lidarr/Lidarr.git ext/Lidarr
  sed -i -e 's/<AssemblyVersion>10\.0\.0\.\*/<AssemblyVersion>2.0.0.0/' ext/Lidarr/src/Directory.Build.props
fi

dotnet build src/Lidarr.Plugin.Bandcamp/Lidarr.Plugin.Bandcamp.csproj -c Release -p:TreatWarningsAsErrors=false
mkdir -p artifacts/plugin
cp -f src/Lidarr.Plugin.Bandcamp/bin/Release/net8.0/Lidarr.Plugin.Bandcamp.dll artifacts/plugin/
echo "Wrote artifacts/plugin/Lidarr.Plugin.Bandcamp.dll ($(wc -c < artifacts/plugin/Lidarr.Plugin.Bandcamp.dll) bytes)"
