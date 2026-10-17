# Public GitHub mirror (Lidarr installer)

Lidarr’s **System → Plugins** installer only accepts **public GitHub** repository URLs with release zip assets. The Origin remote alone is not enough.

**Source of truth:** Origin (`ryan-williamson-cardneau/libarr-bandcamp-plugin`).  
**Mirror:** public GitHub (Origin → GitHub only; do not reverse the direction).

## Blocker (agent session)

This Cloud Agent has **no GitHub auth** (`gh` not logged in; no `GH_TOKEN` / `GITHUB_TOKEN`). Ryan must create the public repo and first push from a machine with GitHub credentials.

## One-time setup (Ryan)

Replace `YOUR_GITHUB_USER` with your GitHub username (account tied to `driffterillustrations@gmail.com` or whichever you prefer).

Recommended public name: **`lidarr-bandcamp-plugin`** (clear for Lidarr). Alternate: `libarr-bandcamp-plugin` (matches Origin).

```bash
# 1) Auth (pick one)
gh auth login
# — or —
export GH_TOKEN='ghp_…'   # classic PAT with repo scope; never commit this

# 2) Create public empty repo (skip if it already exists)
gh repo create YOUR_GITHUB_USER/lidarr-bandcamp-plugin \
  --public \
  --description "Lidarr plugin: Bandcamp purchase library indexer + download client" \
  --disable-wiki

# 3) From a clone of Origin main (this repo)
git fetch origin main
git checkout main
git pull origin main

git remote add github https://github.com/YOUR_GITHUB_USER/lidarr-bandcamp-plugin.git
# if remote already exists: git remote set-url github https://github.com/YOUR_GITHUB_USER/lidarr-bandcamp-plugin.git

# 4) Push main (mirror refs for a clean first sync)
git push github main:main

# 5) Tag a release so Lidarr can install (CI builds zip on v* tags)
git tag -a v0.2.0 -m "Bandcamp Library plugin v0.2.0"
git push github v0.2.0
# Wait for .github/workflows/build.yml "Create GitHub Release" on the GitHub remote.
# Confirm: https://github.com/YOUR_GITHUB_USER/lidarr-bandcamp-plugin/releases

# 6) Point plugin metadata + README at the real URL, then push Origin and re-mirror
#    Edit src/Lidarr.Plugin.Bandcamp/Plugin.cs → GithubUrl
#    Edit README.md install section
git add src/Lidarr.Plugin.Bandcamp/Plugin.cs README.md docs/
git commit -m "Set public GitHub URL for Lidarr plugin installer"
git push origin HEAD
./scripts/mirror-to-github.sh
```

### Lidarr install path (after release exists)

1. Lidarr → **System → Plugins**
2. Paste: `https://github.com/YOUR_GITHUB_USER/lidarr-bandcamp-plugin`
3. **Install**, restart when prompted
4. Confirm **Bandcamp Library** appears under plugins (`Owner` is still `cursor-project` until you change `Plugin.Owner`)

Manual DLL fallback remains:  
`<Lidarr AppData>/plugins/cursor-project/Lidarr.Plugin.Bandcamp/Lidarr.Plugin.Bandcamp.dll`

## Ongoing sync (Origin → GitHub)

After merges land on Origin `main`:

```bash
./scripts/mirror-to-github.sh
# optional release:
./scripts/mirror-to-github.sh --tag v0.2.1
```

Requires remote name `github` (see setup) and push credentials for that remote.

### Optional: GitHub Actions on Origin only

Do **not** configure GitHub → Origin. Prefer pushing from Origin (or CI with an Origin checkout that has a `github` remote + deploy key/PAT stored as a secret outside the repo).

## Checklist after first mirror

- [ ] Public repo exists and is not empty
- [ ] `main` matches Origin `main` tip
- [ ] At least one GitHub **Release** with `Lidarr.Plugin.Bandcamp.net8.0.zip`
- [ ] `Plugin.GithubUrl` and README cite the real URL
- [ ] Lidarr **System → Plugins** install succeeds from that URL
