# Public GitHub mirror (Lidarr installer)

Lidarr’s **System → Plugins** installer only accepts **public GitHub** repository URLs with **Release assets** (plugin DLL zip). Origin alone is not enough. Auto “Source code (zip)” on a Release is **not** a plugin package.

| | |
| --- | --- |
| **Source of truth** | Origin `ryan-williamson-cardneau/libarr-bandcamp-plugin` |
| **Public mirror** | [https://github.com/cardner/lidarr-bandcamp-plugin](https://github.com/cardner/lidarr-bandcamp-plugin) |
| **Direction** | Origin → GitHub only (do not reverse) |
| **Lidarr install URL** | `https://github.com/cardner/lidarr-bandcamp-plugin` |

## Status

- [x] Public repo + `main` + tag `v0.2.0` + Release page + Actions workflow
- [ ] Release asset must be a **flat** `Lidarr.Plugin.Bandcamp.net8.0.zip` (dll + deps.json + pdb at zip root). Nested / zip-in-zip assets leave a folder on disk but Lidarr never loads the plugin.

## GitHub Action (auto release)

Workflow: [`.github/workflows/build.yml`](../.github/workflows/build.yml)

| Trigger | Behavior |
| --- | --- |
| Push `v*` tag | Build, test, create/update GitHub Release, attach `Lidarr.Plugin.Bandcamp.net8.0.zip` |
| `release` published | Same publish path (useful if Release already exists) |
| Push/PR to `main` | Build + upload CI artifacts (no Release) |

**After this lands on Origin `main`, push Origin → GitHub** so Actions exist on `cardner/lidarr-bandcamp-plugin`:

```bash
git fetch origin main && git checkout main && git pull origin main
git remote add github https://github.com/cardner/lidarr-bandcamp-plugin.git 2>/dev/null || \
  git remote set-url github https://github.com/cardner/lidarr-bandcamp-plugin.git
git push github main:main --force   # if histories diverged; Origin is SoT
# Future tags:
git tag -a v0.2.1 -m "…" && git push github v0.2.1
# or: ./scripts/mirror-to-github.sh --tag v0.2.1
```

## Package shape (critical)

Lidarr installs by extracting the Release zip into `plugins/<github-owner>/<repo>/`, then loads only:

```text
plugins/<owner>/<repo>/Lidarr.Plugin.*.dll   # non-recursive GetFiles
```

| Good (Slskd-like) | Bad (current broken v0.2.0 asset) |
| --- | --- |
| `Lidarr.Plugin.Bandcamp.dll` at zip **root** | DLL under `src/.../bin/Release/net8.0/` |
| `Lidarr.Plugin.Bandcamp.deps.json` at root | Nested `Lidarr.Plugin.Bandcamp.net8.0.zip` inside the asset |
| optional `.pdb` | Actions multi-file artifact re-uploaded as Release |

Symptom of a bad zip: plugin **folder** exists after install/restart, but Indexers/Download Clients show no Bandcamp types; logs:

```text
Warn|DownloadClientRepository|Skipping provider of unknown type BandcampDownloadSettings
```

(No `PluginService: Found plugin … Bandcamp` load of types — assembly never entered PluginLoadContext.)

**Do not** download the GitHub Actions artifact zip and attach it as the Release asset. Use `./scripts/build-plugin.sh --zip` or the workflow’s single `Lidarr.Plugin.Bandcamp.net8.0.zip` artifact.

## Replace Release asset (laptop)

```bash
# Prefer a new tag after the packaging fix lands:
./scripts/build-plugin.sh --zip --version 0.2.1
gh release upload v0.2.1 artifacts/Lidarr.Plugin.Bandcamp.net8.0.zip \
  --repo cardner/lidarr-bandcamp-plugin --clobber
# Or clobber v0.2.0:
# gh release upload v0.2.0 artifacts/Lidarr.Plugin.Bandcamp.net8.0.zip \
#   --repo cardner/lidarr-bandcamp-plugin --clobber

# Verify flat layout:
unzip -l artifacts/Lidarr.Plugin.Bandcamp.net8.0.zip
# must list Lidarr.Plugin.Bandcamp.dll and .deps.json with NO directory prefixes
```

Then in Lidarr Docker (persistent `/config`):

1. Delete `plugins/cardner/lidarr-bandcamp-plugin/` (and any `cursor-project/Lidarr.Plugin.Bandcamp` leftover)
2. System → Plugins → install `https://github.com/cardner/lidarr-bandcamp-plugin`
3. Restart → Bandcamp should appear under Indexers and Download Clients

## Ongoing sync

```bash
./scripts/mirror-to-github.sh
./scripts/mirror-to-github.sh --tag v0.2.1
```

Do **not** configure GitHub → Origin.
