#!/usr/bin/env bash
# scripts/fix.sh - the branch for one issue (docs/WORKFLOW.md).
#   scripts/fix.sh 31  [slug]     -> checkout -b fix-31-slug off origin/main
# Refuses a dirty tree: a half-saved edit must not ride into the fix.
set -euo pipefail
cd "$(dirname "$0")/.."
if [ $# -lt 1 ]; then sed -n '2,4p' "$0" | sed 's/^# \{0,1\}//'; exit 2; fi
N="$1"; SLUG="${2:-}"
if [ "${1:-}" = "--allow-dirty" ]; then echo "!! skipping the clean-tree check" ; shift; N="${1:-$N}"; fi
if [ -n "$(git status --porcelain)" ] && [ "${1:-}" != "--allow-dirty" ]; then
  echo "!! working tree is dirty - commit or stash first (or pass --allow-dirty)"; git status --short | head; exit 1
fi
# Offline work (#L<nnn> records, or the .git/offline-mode marker) never touches
# the network and never branches off a stale origin/main: it branches off the
# `offline` branch, where the local landings accumulate (docs/WORKFLOW.md).
if [ -f .git/offline-mode ] || [ "${N#L}" != "$N" ]; then
  [ "${N#L}" != "$N" ] || N="L$N"
  BASE=HEAD
  if git show-ref --verify --quiet refs/heads/offline; then BASE=offline; fi
  if [ -z "$SLUG" ]; then
    rec=$(ls "issues/${N#L}"-*.md 2>/dev/null | head -1 || true)
    [ -n "$rec" ] && SLUG=$(basename "$rec" | sed "s/^[0-9]*-//; s/\.md$//")
  fi
  BR="fix-$N${SLUG:+-$SLUG}"
  if git show-ref --verify --quiet "refs/heads/$BR"; then
    echo "==> branch $BR already exists (offline) - switching to it"; git checkout -q "$BR"
  else
    git checkout -q -b "$BR" "$BASE"
    echo "==> OFFLINE: branch $BR off $BASE ($(git rev-parse --short "$BASE")) - no fetch, no GitHub"
  fi
  echo "    record: issues/${N#L}-*.md   land with: scripts/offline.sh land $N"
  exit 0
fi
git fetch -q origin
if [ -z "$SLUG" ]; then
  SLUG=$(gh issue view "$N" --json title -q .title | tr 'A-Z' 'a-z' | tr -cs 'a-z0-9' '-' | cut -c1-32 | sed 's/-*$//')
fi
BR="fix-$N${SLUG:+-$SLUG}"
if git show-ref --verify --quiet "refs/heads/$BR"; then
  echo "==> branch $BR already exists - switching to it"; git checkout -q "$BR"
else
  git checkout -q -b "$BR" origin/main
  echo "==> branch $BR off origin/main ($(git rev-parse --short origin/main))"
fi
echo "    issue: https://github.com/hwacookie/driving-game/issues/$N"
echo "    fix, test (scripts/run_e2e.sh), then: scripts/pr.sh"
