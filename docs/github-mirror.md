# Public GitHub mirror (Lidarr installer)

Lidarr’s **System → Plugins** installer only accepts **public GitHub** repository URLs with **Release assets** (plugin DLL zip). Origin alone is not enough. Auto “Source code (zip)” on a Release is **not** a plugin package.

| | |
| --- | --- |
| **Source of truth** | Origin `ryan-williamson-cardneau/libarr-bandcamp-plugin` |
| **Public mirror** | [https://github.com/cardner/lidarr-bandcamp-plugin](https://github.com/cardner/lidarr-bandcamp-plugin) |
| **Direction** | Origin → GitHub only (do not reverse) |
| **Lidarr install URL** | `https://github.com/cardner/lidarr-bandcamp-plugin` |

## Status

- [x] Public repo + `main` + tag `v0.2.0` + Release page
- [ ] Release **asset** `Lidarr.Plugin.Bandcamp.net8.0.zip` (upload manually once; then auto via Actions)
- [ ] GitHub remote includes `.github/workflows/build.yml` (must mirror Origin `main` — do not omit `.github/`)

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

## Attach zip to existing Release v0.2.0 (one-time, laptop)

Agent cannot `gh release upload` (no GitHub auth). On a machine with `gh` auth for `cardner`:

```bash
# Build (from this repo):
./scripts/build-plugin.sh --zip --version 0.2.0

# Upload to the existing Release (does NOT replace Source code zipball):
gh release upload v0.2.0 artifacts/Lidarr.Plugin.Bandcamp.net8.0.zip \
  --repo cardner/lidarr-bandcamp-plugin --clobber
```

Confirm asset under https://github.com/cardner/lidarr-bandcamp-plugin/releases/tag/v0.2.0  
Then Lidarr → **System → Plugins** → paste `https://github.com/cardner/lidarr-bandcamp-plugin`

## Ongoing sync

```bash
./scripts/mirror-to-github.sh
./scripts/mirror-to-github.sh --tag v0.2.1
```

Do **not** configure GitHub → Origin.
