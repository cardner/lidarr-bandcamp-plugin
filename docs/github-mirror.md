# Public GitHub mirror (Lidarr installer)

Lidarr’s **System → Plugins** installer only accepts **public GitHub** repository URLs with release zip assets. The Origin remote alone is not enough.

| | |
| --- | --- |
| **Source of truth** | Origin `ryan-williamson-cardneau/libarr-bandcamp-plugin` |
| **Public mirror** | [https://github.com/cardner/lidarr-bandcamp-plugin](https://github.com/cardner/lidarr-bandcamp-plugin) |
| **Direction** | Origin → GitHub only (do not reverse) |

## Status

- [x] Public repo created (`cardner/lidarr-bandcamp-plugin`)
- [ ] First push of Origin `main` + tag `v0.2.0` (agent VM has no GitHub credentials — Ryan runs below on his laptop)
- [ ] GitHub Actions release zip present
- [x] `Plugin.GithubUrl` / README cite the real URL (this branch)

## First push + release (Ryan laptop)

Repo exists but is still **empty**. From a clone of this Origin repo (with `gh` / git auth for `cardner`):

```bash
git fetch origin main
git checkout main
git pull origin main

git remote add github https://github.com/cardner/lidarr-bandcamp-plugin.git
# if remote already exists:
# git remote set-url github https://github.com/cardner/lidarr-bandcamp-plugin.git

git push github main:main

git tag -a v0.2.0 -m "Bandcamp Library plugin v0.2.0"
git push github v0.2.0

# Wait for .github/workflows/build.yml "Create GitHub Release"
# Confirm: https://github.com/cardner/lidarr-bandcamp-plugin/releases
```

Or, after `github` remote exists: `./scripts/mirror-to-github.sh --tag v0.2.0`

## Lidarr install (after release zip exists)

1. Lidarr → **System → Plugins**
2. Paste: `https://github.com/cardner/lidarr-bandcamp-plugin`
3. **Install**, restart when prompted
4. Confirm **Bandcamp Library** (`Owner` is still `cursor-project` until you change `Plugin.Owner`)

Manual DLL fallback:  
`<Lidarr AppData>/plugins/cursor-project/Lidarr.Plugin.Bandcamp/Lidarr.Plugin.Bandcamp.dll`

## Ongoing sync (Origin → GitHub)

After merges land on Origin `main`:

```bash
./scripts/mirror-to-github.sh
# optional release:
./scripts/mirror-to-github.sh --tag v0.2.1
```

Requires remote name `github` and push credentials for that remote.

Do **not** configure GitHub → Origin.
