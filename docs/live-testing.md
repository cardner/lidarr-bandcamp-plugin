# Live testing (Bandcamp)

CI never requires Bandcamp credentials. Live smoke is **opt-in** via a local env secret.

## Secrets

| Name | Required | Description |
| --- | --- | --- |
| `BANDCAMP_IDENTITY_COOKIE` | yes for live | Bare `identity` cookie value **or** a full Cookie header containing `identity=` |
| `BANDCAMP_COOKIES` | alias | Legacy alias for the same value |
| `BANDCAMP_LIVE_DOWNLOAD` | no | Set to `1` to also download the first owned release |
| `BANDCAMP_LIVE_FORMAT` | no | Format key (default `flac`) |

Do **not** commit cookie values. Do **not** set these in CI.

### Export the cookie

1. Log into [bandcamp.com](https://bandcamp.com) in a normal browser
2. DevTools → Application/Storage → Cookies → `https://bandcamp.com`
3. Copy the **`identity`** value only
4. `export BANDCAMP_IDENTITY_COOKIE='…paste…'`

When auth fails later, re-export — there is no OAuth refresh for fan purchases.

## Console harness (recommended)

```bash
# From repo root (needs ext/Lidarr for compile — see CONTRIBUTING.md)
export BANDCAMP_IDENTITY_COOKIE='…'
dotnet run --project tests/Lidarr.Plugin.Bandcamp.ConsoleHarness -c Release

# Optional download of first owned release:
export BANDCAMP_LIVE_DOWNLOAD=1
dotnet run --project tests/Lidarr.Plugin.Bandcamp.ConsoleHarness -c Release
```

Expected success path:

1. `AUTH OK — fan_id=…`
2. `COLLECTION — N downloadable item(s)…`
3. With `BANDCAMP_LIVE_DOWNLOAD=1`: writes a zip under `./live-artifacts/`

The harness uses `BandcampNativeHttp` (browser User-Agent) so it never hits Lidarr’s `User-Agent other than Lidarr not allowed` guard.

## Optional xUnit live test

`BandcampLiveSmokeTests` no-ops (passes) when the env var is absent. With the cookie set:

```bash
export BANDCAMP_IDENTITY_COOKIE='…'
dotnet test tests/Lidarr.Plugin.Bandcamp.Test -c Release --filter "FullyQualifiedName~BandcampLiveSmoke"
```

## Lidarr UI checklist (manual E2E)

Full step-by-step (install DLL, indexer, download client, Delay Profile, search, grab, failure modes, API bootstrap):

→ **[docs/lidarr-ui-checklist.md](lidarr-ui-checklist.md)**

Quick path:

1. Build DLL (`./scripts/build-plugin.sh`) and install under `plugins/cursor-project/Lidarr.Plugin.Bandcamp/` (**one** copy only)
2. Restart Lidarr → **System → Plugins** shows **Bandcamp Library**
3. Indexer + Download Client: paste **Identity Cookie** → **Test** → Save
4. Delay Profiles → enable **Bandcamp**
5. Search/grab an owned album not on disk; confirm Download Path + import

Helpers: `docker/docker-compose.lidarr.yml`, `scripts/run-lidarr-local.sh`, `scripts/lidarr-api-bootstrap.sh`.

### What Ryan must supply for live E2E

A fresh Bandcamp `identity` cookie (`BANDCAMP_IDENTITY_COOKIE`) and either a running Lidarr nightly or use the scripts above. No other secrets.
