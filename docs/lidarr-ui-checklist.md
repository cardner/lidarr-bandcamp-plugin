# Lidarr UI E2E checklist — Bandcamp Library plugin

Concrete click-through for Ryan (or any operator). Pair with [live-testing.md](live-testing.md) for cookie export and the console harness.

**Prereqs**

- Lidarr **≥ 3.1.2.4938** on `develop` or `nightly` (plugins are not on stable)
- This plugin DLL built from `main` (see below)
- Bandcamp `identity` cookie in env as `BANDCAMP_IDENTITY_COOKIE` (never commit it)

---

## A. Build the plugin DLL

```bash
git clone --depth 1 --branch plugins https://github.com/Lidarr/Lidarr.git ext/Lidarr
sed -i -e 's/<AssemblyVersion>10\.0\.0\.\*/<AssemblyVersion>2.0.0.0/' ext/Lidarr/src/Directory.Build.props
dotnet build src/Lidarr.Plugin.Bandcamp/Lidarr.Plugin.Bandcamp.csproj -c Release -p:TreatWarningsAsErrors=false
# Output:
#   src/Lidarr.Plugin.Bandcamp/bin/Release/net8.0/Lidarr.Plugin.Bandcamp.dll
```

Or: `./scripts/build-plugin.sh` (writes `artifacts/plugin/Lidarr.Plugin.Bandcamp.dll`).

---

## B. Install into Lidarr

### Option 1 — Manual plugin folder (fastest for local DLL)

1. Stop Lidarr (or continue and restart after copy).
2. Copy **one** DLL into AppData plugins (owner/name folders; **do not** install the same DLL twice):

   ```text
   <Lidarr AppData>/plugins/cursor-project/Lidarr.Plugin.Bandcamp/Lidarr.Plugin.Bandcamp.dll
   ```

   Examples:
   - Linux native: `~/.config/Lidarr/plugins/...`
   - Docker linuxserver: `/config/plugins/...`
   - This agent’s E2E: `/tmp/lidarr-data/plugins/...`

3. Start / restart Lidarr.
4. **System → Plugins** — confirm **Bandcamp Library** (`cursor-project`) is listed.
5. **System → Status** — version ≥ 3.1.2.4938, branch develop/nightly.

### Option 2 — Docker Compose (local machine with Docker)

```bash
./scripts/build-plugin.sh
export BANDCAMP_IDENTITY_COOKIE='…'   # for optional API bootstrap only
docker compose -f docker/docker-compose.lidarr.yml up -d
# UI: http://127.0.0.1:8686
```

Compose mounts `artifacts/plugin` into `/config/plugins/cursor-project/Lidarr.Plugin.Bandcamp/`.

### Option 3 — GitHub URL install

Only after the **public GitHub mirror** has a release zip (Origin alone is not enough). See [github-mirror.md](github-mirror.md).

1. Paste `https://github.com/cardner/lidarr-bandcamp-plugin` into **System → Plugins**
2. **Install**, restart when prompted
3. Confirm **Bandcamp Library** is listed once

---

## C. Add indexer

1. **Settings → Indexers → +**
2. Under **Other**, select **Bandcamp** (implementation `BandcampIndexer`)
3. **Name:** e.g. `Bandcamp Library`
4. **Identity Cookie:** paste bare `identity` value (or `identity=…`)
5. Enable **RSS** (polls recent Bandcamp purchases by purchase date)
6. Click **Test**
   - Success: green check (cookie valid + ≥1 downloadable purchase)
   - Failure examples:
     - placeholder / too short → shape error (fix cookie paste)
     - expired / 401 / 403 / challenge → re-export cookie from browser
7. **Save**

---

## D. Add download client

1. **Settings → Download Clients → +**
2. Under **Other**, select **Bandcamp**
3. **Identity Cookie:** same value as indexer
4. **Download Path:** folder Lidarr can write (e.g. `/downloads/bandcamp`)
   - linuxserver/hotio: mount is usually `/downloads` — prefer `/downloads/bandcamp`; avoid `/downloads/downloads` unless nested on purpose ([docker-download-path.md](docker-download-path.md))
5. **Test** → green check → **Save** (failure should name the path: not creatable / not writable; doubled path may warn)

---

## E. Enable Bandcamp in Delay Profiles (required)

1. **Settings → Profiles** → scroll to **Delay Profiles**
2. Wrench on **Default** (or the profile your artists use)
3. Enable protocol **Bandcamp** (toggle / checkbox)
4. Optionally drag Bandcamp above Usenet/Torrent to prefer owned purchases
5. **Save**

Without this step, searches never grab Bandcamp results.

---

## E2. Add Import List (new purchases → Lidarr library)

1. **Settings → Import Lists → +**
2. Select **Bandcamp**
3. Same **Identity Cookie** as indexer/download client
4. Set root folder + quality / metadata profiles
5. **Enable Automatic Add** — must be on
6. **Monitor:** **Specific Album** (recommended; UI may default Entire Artist)
7. **Search for New Items** — **must be on** or added albums won’t auto-search/download
8. **Test** → **Save**
9. Trigger **Refresh** on the list once

### If purchases stay out of Lidarr

| Symptom | Likely cause | What to do |
| --- | --- | --- |
| List sync “processed N items”, album still missing | SkyHook/MB 500 or no match (older builds aborted the whole sync on one 500) | Install build with Import List MB pre-resolve. Logs: `Bandcamp import list: candidates=… mapped=… skyhookErrors=…`. Retry later or **manual Add** |
| Album in Lidarr, no download | Search for New Items off; Delay Profile; RSS off | Enable Search; enable Bandcamp protocol; Enable RSS on indexer |
| Expecting RSS alone to add new artists | Pseudo-RSS only matches monitored library | Use Import List (or manual add) first |

After a successful list add with Search on Add, Lidarr runs missing-album search for that artist — Bandcamp indexer must be enabled and the Bandcamp protocol must be on in Delay Profiles.

New artists appear only after MusicBrainz can resolve the Bandcamp title. Pseudo-RSS then grabs later purchases for already-monitored artists.

---

## F. Search an owned album

1. Add an artist you **own on Bandcamp** (MusicBrainz match) if not already in Lidarr.
2. Open an album you purchased → **Search** / Interactive search.
3. Expect releases titled like `Artist - Album [WEB] [FLAC]` (or other formats).
4. If empty:
   - Confirm the purchase is in your Bandcamp collection (console harness list).
   - Confirm Delay Profile has Bandcamp enabled.
   - Re-Test indexer.

---

## G. Grab a missing release + confirm import

1. Pick a Bandcamp result for an album **not** already on disk.
2. **Grab**.
3. **Activity → Queue** — Bandcamp download client should show progress.
4. Files land under the configured **Download Path**, then Lidarr imports into the artist folder.
5. Album shows as downloaded / tracks present.

### Failure modes

| Symptom | Likely cause | Fix |
| --- | --- | --- |
| Plugin missing | Stable branch / DLL not in plugins path / duplicate DLL crash | Nightly/develop; one DLL under `plugins/<owner>/<name>/` |
| Test: placeholder / too short | Bad paste | Paste full `identity` value |
| Test: expired / 401 / 403 / challenge | Stale cookie or bot page | Re-export from browser |
| Test: User-Agent other than Lidarr not allowed | Old plugin / dispatcher stripped | Use this build (≥ auth PR) |
| Search empty | Protocol off / not owned | Enable Bandcamp in Delay Profile; verify purchase |
| Grab fails mid-download | Cookie expired during transfer | Re-export; re-Test download client |
| Queue: Download Failed + empty tooltip | Old plugin build / path write failed silently | Upgrade plugin; re-Test Download Path; see [docker-download-path.md](docker-download-path.md) |
| Queue: `Download Path not writable: …` | Bad Docker path or PUID/PGID | Use `/downloads/bandcamp`; fix volume permissions |
| Import skipped | Path / permissions / quality | Check Download Path writable; Media Management |

---

## H. API bootstrap (optional, no GUI)

With Lidarr running and `BANDCAMP_IDENTITY_COOKIE` set:

```bash
export LIDARR_URL=http://127.0.0.1:8687
export LIDARR_API_KEY='…from config.xml…'
export BANDCAMP_IDENTITY_COOKIE='…'
export BANDCAMP_DOWNLOAD_PATH=/tmp/lidarr-downloads/bandcamp
./scripts/lidarr-api-bootstrap.sh
```

Script: enables Bandcamp on the default Delay Profile, creates indexer + download client, runs Test on both.

---

## Checklist status (agent)

| Step | Automated here | Manual (Ryan) |
| --- | --- | --- |
| Build DLL | Done | — |
| Install plugin into Lidarr | Done (local develop 3.1.6.5078 @ `:8687`) | Docker/UI on your machine |
| Plugin listed | Done | Confirm in UI |
| Delay Profile Bandcamp on | Done via API | Confirm in UI |
| Indexer/Download Test (shape validation) | Done (placeholder → expected error) | Paste real cookie + Test |
| Live Test + search + grab + import | Blocked without cookie in this VM | Click through C–G |

**Ryan’s next click:** open Lidarr → **Settings → Indexers → + → Bandcamp** → paste Identity Cookie → **Test**.
