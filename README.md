# Lidarr Bandcamp Library Plugin

Indexer + download client for **music you already bought on Bandcamp**.

Lidarr searches your authenticated purchase library and can download owned releases that are not yet in your Lidarr library. It does **not** search or download the public Bandcamp catalog.

Requires Lidarr **3.1.2.4938+** on the `develop`, `nightly`, or `plugins` branch (plugins are not on stable/`master` yet).

## What this plugin does

| Capability | Behavior |
| --- | --- |
| Auth | Browser `identity` cookie (single **Identity Cookie** field; bare value OK) |
| Index / search | Paginated fan collection (`collection_summary` + `collection_items`) |
| Download | Redownload page → preferred format (FLAC when available) → Lidarr import path |
| User-Agent | Bandcamp calls use a browser-like UA via a dedicated path / dispatcher so Lidarr does **not** throw `User-Agent other than Lidarr not allowed` |

## Install

### From a GitHub release (preferred once published)

1. In Lidarr: **System → Plugins**
2. Paste this repository’s **GitHub** URL
3. **Install**, then restart Lidarr when prompted

> Lidarr’s plugin installer expects a **GitHub** URL with release assets. Until this project is published to GitHub, use the local DLL path below.

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
3. Paste into the plugin’s **Identity Cookie** field (indexer and download client)
   - Bare value is preferred (e.g. `a1B2c3…`)
   - Or paste `identity=…` / a full Cookie header
4. Click **Test** — errors now distinguish missing/wrong-shape cookies, expired sessions, bot challenges, and HTTP 401/403, and tell you to re-export

### Live smoke (optional)

See [docs/live-testing.md](docs/live-testing.md). Set `BANDCAMP_IDENTITY_COOKIE` and run the console harness. CI never requires this secret.

## Lidarr setup

### 1. Indexer

**Settings → Indexers → + → Bandcamp** (or **Bandcamp Library**)

- Paste **Identity Cookie**
- **Test**, then **Save**

### 2. Download client

**Settings → Download Clients → + → Bandcamp**

- Same **Identity Cookie**
- **Download Path** Lidarr can write (e.g. `/downloads/bandcamp`)
- **Test**, then **Save**

### 3. Delay profile (required)

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
