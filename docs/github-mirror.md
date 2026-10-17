# Public GitHub mirror (Lidarr installer)

Lidarr’s **System → Plugins** installer only accepts a **public GitHub** repository URL and downloads a **Release asset** whose name contains `net8.0.zip`. Origin alone is not enough. Auto “Source code (zip)” on a Release is **not** a plugin package.

| | |
| --- | --- |
| **Source of truth** | Origin `ryan-williamson-cardneau/libarr-bandcamp-plugin` |
| **Public mirror** | [https://github.com/cardner/lidarr-bandcamp-plugin](https://github.com/cardner/lidarr-bandcamp-plugin) |
| **Direction** | Origin → GitHub only (do not reverse) |
| **Lidarr install URL** | `https://github.com/cardner/lidarr-bandcamp-plugin` |

## What Lidarr needs

A GitHub **Release** with asset:

```text
Lidarr.Plugin.Bandcamp.net8.0.zip
```

Zip **root** (flat — no folders, no zip-in-zip):

| File | Required |
| --- | --- |
| `Lidarr.Plugin.Bandcamp.dll` | yes |
| `Lidarr.Plugin.Bandcamp.deps.json` | yes (`PluginLoadContext`) |
| `Lidarr.Plugin.Bandcamp.pdb` | recommended |

Lidarr extracts into `plugins/<owner>/<repo>/` and loads only `Lidarr.Plugin.*.dll` via **non-recursive** `GetFiles`. Nested paths or zip-of-zip → folder exists, providers missing:

```text
Warn|DownloadClientRepository|Skipping provider of unknown type BandcampDownloadSettings
```

## CI artifact vs Release asset (do not confuse)

| Source | What you get | Use for Lidarr? |
| --- | --- | --- |
| **GitHub Release** asset `Lidarr.Plugin.Bandcamp.net8.0.zip` | Flat plugin zip | **Yes** — System → Plugins URL |
| **Actions / Origin CI download** | Always an *outer* zip wrapper | **No** as a plugin zip |

GitHub/Origin always wrap artifact downloads. If CI uploaded a `.zip`, the download becomes **zip-of-zip** (e.g. `Lidarr Bandcamp Plugin 8.0.zip` → inner `Lidarr.Plugin.Bandcamp.net8.0.zip`) and Lidarr rejects it.

This repo’s workflow uploads **loose files** (`dll` + `deps.json` + `pdb`) as the CI artifact `Lidarr.Plugin.Bandcamp-files`. After unzipping the CI wrapper once you have files for **manual** copy into `plugins/.../`. For normal install, use the **Release**.

## Create a Release (required for Lidarr)

Workflow: [`.github/workflows/build.yml`](../.github/workflows/build.yml)

| Trigger | Behavior |
| --- | --- |
| Push tag `v*` to **GitHub** | Build, test, create/update Release, upload flat `Lidarr.Plugin.Bandcamp.net8.0.zip` |
| `workflow_dispatch` with `version` (e.g. `0.2.1`) | Same — creates tag + Release on GitHub |
| `release` published | Re-upload asset onto that Release |
| Push/PR to `main` | Build + CI file artifact only (**no** Release) |

Pushing Origin `main` alone does **not** create a Release. You must push a tag (or run workflow_dispatch with a version) **on the GitHub remote**.

### Exact steps (Ryan)

```bash
# 1) Merge packaging/release PR on Origin, then sync SoT → GitHub
git fetch origin main && git checkout main && git pull origin main
git remote add github https://github.com/cardner/lidarr-bandcamp-plugin.git 2>/dev/null || true
git push github main:main

# 2) Tag on the commit you want released and push the tag to GitHub
git tag -a v0.2.1 -m "Flat plugin package for Lidarr"
git push github v0.2.1
# or: ./scripts/mirror-to-github.sh --tag v0.2.1

# 3) Wait for Actions on cardner/lidarr-bandcamp-plugin → Release v0.2.1
# Verify asset name ends with net8.0.zip and unzip -l is flat:
gh release view v0.2.1 --repo cardner/lidarr-bandcamp-plugin
gh release download v0.2.1 -p '*.net8.0.zip' --repo cardner/lidarr-bandcamp-plugin
unzip -l Lidarr.Plugin.Bandcamp.net8.0.zip
```

Optional without local tag: GitHub → Actions → **Build and Release Plugin** → Run workflow → set `version` to `0.2.1`.

### Lidarr reinstall

1. Delete `plugins/cardner/lidarr-bandcamp-plugin/` (clear nested/broken extract)
2. System → Plugins → `https://github.com/cardner/lidarr-bandcamp-plugin` → Install
3. Restart container (persistent `/config` volume)

## Local build / manual upload

```bash
./scripts/build-plugin.sh --zip --version 0.2.1
unzip -l artifacts/Lidarr.Plugin.Bandcamp.net8.0.zip   # flat dll + deps.json + pdb
gh release upload v0.2.1 artifacts/Lidarr.Plugin.Bandcamp.net8.0.zip \
  --repo cardner/lidarr-bandcamp-plugin --clobber
```

## Ongoing sync

```bash
./scripts/mirror-to-github.sh
./scripts/mirror-to-github.sh --tag v0.2.1
```

Do **not** configure GitHub → Origin.
