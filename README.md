# Lidarr Bandcamp Library Plugin

Indexer + download client + Import List for **music you already bought on Bandcamp**.

Lidarr searches your authenticated purchase library and can download owned releases that are not yet in your Lidarr library. It does **not** search or download the public Bandcamp catalog.

Requires Lidarr **3.1.2.4938+** on the `develop`, `nightly`, or `plugins` branch (plugins are not on stable/`master` yet).

## What this plugin does

| Capability | Behavior |
| --- | --- |
| Auth | Browser `identity` cookie (single **Identity Cookie** field; bare value OK) |
| Index / search | Paginated fan collection (`collection_summary` + `collection_items`) |
| Import List | Album-level purchases → Lidarr library (MusicBrainz resolve) |
| Pseudo-RSS | Enable RSS on the indexer — polls recent purchases by **purchase** date |
| Download | Redownload page → preferred format (FLAC when available) → Lidarr import path |
| User-Agent | Bandcamp calls use a browser-like UA via a dedicated path / dispatcher so Lidarr does **not** throw `User-Agent other than Lidarr not allowed` |

## Install

### From a GitHub release (preferred)

Lidarr’s plugin installer needs a **public GitHub** URL with release zip assets. Origin alone is not enough.

**Public mirror:** [https://github.com/cardner/lidarr-bandcamp-plugin](https://github.com/cardner/lidarr-bandcamp-plugin)  
Sync steps: [docs/github-mirror.md](docs/github-mirror.md)

1. In Lidarr: **System → Plugins**
2. Paste: `https://github.com/cardner/lidarr-bandcamp-plugin`
3. **Install**, then restart Lidarr when prompted

> Needs a GitHub **Release** asset named `Lidarr.Plugin.Bandcamp.net8.0.zip` (flat dll + deps.json).  
> Push a `v*` tag to the GitHub mirror (or run the workflow with a version) — CI artifact downloads are **not** the Release zip (they wrap files in an outer zip). See [docs/github-mirror.md](docs/github-mirror.md).

### From a local build

```bash
# .NET 8+ SDK required
git clone --depth 1 --branch plugins https://github.com/Lidarr/Lidarr.git ext/Lidarr
# Pin assembly major (see CONTRIBUTING / CI) then:
dotnet restore Lidarr.Plugin.Bandcamp.slnx -p:TreatWarningsAsErrors=false
dotnet build Lidarr.Plugin.Bandcamp.slnx -c Release -p:TreatWarningsAsErrors=false
```

Copy `src/Lidarr.Plugin.Bandcamp/bin/Release/net8.0/Lidarr.Plugin.Bandcamp.dll` into Lidarr’s plugins folder (or install via a GitHub release zip containing that DLL), then restart Lidarr.

## Auth (Identity Cookie)

Bandcamp does **not** offer fan OAuth for personal libraries. The only credential is the browser `identity` cookie. Auto-refresh is not possible — when Test fails or downloads stop authenticating, re-export from DevTools.

1. Log into [bandcamp.com](https://bandcamp.com)
2. Open DevTools (`F12`)
   - **Chrome:** Application → Cookies → `https://bandcamp.com` → copy **`identity`**
   - **Firefox:** Storage → Cookies → `https://bandcamp.com` → copy **`identity`**
3. Paste into the plugin’s **Identity Cookie** field (indexer, download client, and Import List)
   - Bare value is preferred (e.g. `a1B2c3…`)
   - Or paste `identity=…` / a full Cookie header
4. Click **Test** — errors now distinguish missing/wrong-shape cookies, expired sessions, bot challenges, and HTTP 401/403, and tell you to re-export

### Live smoke (optional)

See [docs/live-testing.md](docs/live-testing.md). Set `BANDCAMP_IDENTITY_COOKIE` and run the console harness. CI never requires this secret.

### Lidarr UI E2E

Step-by-step install / Test / Delay Profile / grab: [docs/lidarr-ui-checklist.md](docs/lidarr-ui-checklist.md).  
Local helpers: `./scripts/build-plugin.sh`, `./scripts/run-lidarr-local.sh`, `docker/docker-compose.lidarr.yml`, `./scripts/lidarr-api-bootstrap.sh`.

## Lidarr setup

### 1. Indexer

**Settings → Indexers → + → Bandcamp** (or **Bandcamp Library**)

- Paste **Identity Cookie**
- Enable **RSS** (pseudo-feed of recent purchases)
- **Test**, then **Save**

### 2. Download client

**Settings → Download Clients → + → Bandcamp**

- Same **Identity Cookie**
- **Download Path** Lidarr can write (e.g. `/downloads/bandcamp`)
  - Docker linuxserver/hotio: the volume is usually mounted at **`/downloads`** — use `/downloads/bandcamp`, not `/downloads/downloads` unless that nested folder exists on purpose. Details: [docs/docker-download-path.md](docs/docker-download-path.md)
- **Test**, then **Save** (Test creates the folder if needed and fails clearly when it is not writable)

### 3. Import List (new artists / albums from purchases)

**Settings → Import Lists → + → Bandcamp**

- Same **Identity Cookie**
- Root folder + quality / metadata profiles as usual
- **Enable Automatic Add** — on (required for sync to add anything)
- **Monitor:** prefer **Specific Album** (UI may default Entire Artist — change it)
- **Search for New Items** — **on** (Lidarr UI often leaves this off; without it, albums can be added but never searched/downloaded)
- **Test**, then **Save**, then click **Refresh** once

List sync is intentionally slow (≥ 6h). Bandcamp-only releases / SkyHook-MusicBrainz failures will **not** auto-add — check Lidarr logs for `SkyHook` / `500` / “Unable to communicate”. Those titles need a manual Add Artist/Album (or retry when SkyHook is healthy). Indexer **Enable RSS** only grabs for albums **already** in Lidarr.

### 4. Delay profile (required)

**Settings → Profiles → Delay Profiles** → edit the profile → enable the **Bandcamp** protocol → Save.

Without this, Lidarr will not grab Bandcamp results.

## Supported formats

FLAC, ALAC, WAV, AIFF, MP3 V0, MP3 320, OGG Vorbis, AAC — Lidarr picks from your quality profile among formats Bandcamp exposes for that purchase.

## Limits

- **Owned music only** — no public storefront search
- **Cookie auth** — manual refresh when `identity` expires
- **Unofficial fan APIs** — Bandcamp may change endpoints without notice
- **No multi-account** in v1
- Live Bandcamp E2E is manual; CI uses fixtures/mocks

## Troubleshooting

| Symptom | Fix |
| --- | --- |
| `User-Agent other than Lidarr not allowed` | This plugin registers a Bandcamp HTTP path that allows a browser UA. Rebuild/reinstall this build; do not strip the dispatcher / native HTTP client. |
| Auth / Test fails | Re-export `identity` from a logged-in browser session |
| Search returns nothing | Confirm the album is in your Bandcamp collection and the Bandcamp protocol is enabled in Delay Profiles |
| Queue: Download Failed (empty / path) | Check Download Path; Docker often needs `/downloads/bandcamp` not `/downloads/downloads`. See [docs/docker-download-path.md](docs/docker-download-path.md) |
| New purchase not grabbed | Enable indexer **RSS**; ensure artist/album is in Lidarr (Import List or manual). Pseudo-RSS only matches monitored library items. |
| Import List adds nothing | MusicBrainz could not map the Bandcamp title — add the artist manually or fix MB metadata |
| Plugin missing | Lidarr must be ≥ 3.1.2.4938 on develop/nightly/plugins; restart after install |

## Development

```bash
dotnet test Lidarr.Plugin.Bandcamp.slnx -c Release --filter "FullyQualifiedName~Bandcamp" -p:TreatWarningsAsErrors=false
```

See `LICENSE` / `NOTICE` for licensing. Core Bandcamp Lidarr plugin patterns were adapted from the MIT-licensed [jtstothard/lidarr-plugin-bandcamp](https://github.com/jtstothard/lidarr-plugin-bandcamp) work, with auth UX and fan_id / User-Agent hardening for this project.

## Real-device testing checklist

1. Lidarr nightly with plugins enabled
2. Install this plugin, restart
3. Add indexer + download client with a fresh `identity` cookie
4. Enable Bandcamp in Delay Profiles
5. Search an artist/album you own on Bandcamp and confirm releases appear
6. Grab one release not already on disk; confirm files land in Download Path and Lidarr imports them
