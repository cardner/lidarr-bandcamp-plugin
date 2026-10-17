#!/usr/bin/env bash
# Mirror Origin source-of-truth → public GitHub (for Lidarr plugin installer).
# Usage:
#   ./scripts/mirror-to-github.sh              # push main → github/main
#   ./scripts/mirror-to-github.sh --tag v0.2.1 # also create/push annotated tag
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

REMOTE="${GITHUB_REMOTE:-github}"
BRANCH="${MIRROR_BRANCH:-main}"
TAG=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --tag)
      TAG="${2:?--tag requires a value like v0.2.1}"
      shift 2
      ;;
    --remote)
      REMOTE="${2:?}"
      shift 2
      ;;
    --branch)
      BRANCH="${2:?}"
      shift 2
      ;;
    -h|--help)
      sed -n '2,6p' "$0"
      exit 0
      ;;
    *)
      echo "Unknown arg: $1" >&2
      exit 1
      ;;
  esac
done

if ! git remote get-url "$REMOTE" >/dev/null 2>&1; then
  cat >&2 <<EOF
ERROR: git remote '$REMOTE' is not configured.

One-time setup (see docs/github-mirror.md):
  git remote add github https://github.com/cardner/lidarr-bandcamp-plugin.git
EOF
  exit 1
fi

echo "Fetching origin/$BRANCH…"
git fetch origin "$BRANCH"

echo "Pushing origin/$BRANCH → $REMOTE/$BRANCH…"
git push "$REMOTE" "refs/remotes/origin/$BRANCH:refs/heads/$BRANCH"

if [[ -n "$TAG" ]]; then
  if git rev-parse "$TAG" >/dev/null 2>&1; then
    echo "Tag $TAG already exists locally; pushing to $REMOTE…"
  else
    TIP="$(git rev-parse "refs/remotes/origin/$BRANCH")"
    echo "Creating annotated tag $TAG at $TIP…"
    git tag -a "$TAG" -m "Lidarr Bandcamp Library plugin $TAG" "$TIP"
  fi
  git push "$REMOTE" "refs/tags/$TAG"
  echo "Pushed tag $TAG — wait for GitHub Actions release zip if workflow is enabled on $REMOTE."
fi

echo "Done. Install in Lidarr: System → Plugins → paste the public GitHub repo URL."
