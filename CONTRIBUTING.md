# Contributing

## Build

.NET 8 SDK (or newer). Lidarr sources are required as a compile-time reference:

```bash
git clone --depth 1 --branch plugins https://github.com/Lidarr/Lidarr.git ext/Lidarr

# Pin Lidarr AssemblyVersion major away from 10.x so plugin ALC binding works
# (see .github/workflows/build.yml)
sed -i -e 's/<AssemblyVersion>10\.0\.0\.\*/<AssemblyVersion>2.0.0.0/' ext/Lidarr/src/Directory.Build.props

dotnet restore Lidarr.Plugin.Bandcamp.slnx -p:TreatWarningsAsErrors=false
dotnet build Lidarr.Plugin.Bandcamp.slnx -c Release -p:TreatWarningsAsErrors=false
dotnet test Lidarr.Plugin.Bandcamp.slnx -c Release --filter "FullyQualifiedName~Bandcamp" -p:TreatWarningsAsErrors=false
```

Do not commit `ext/Lidarr` (gitignored).

## PRs

Keep changes focused. Prefer fixtures/mocks over live Bandcamp credentials in CI.
