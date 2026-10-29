#!/usr/bin/env bash
# scripts/pr.sh - push the fix branch and open its PR (docs/WORKFLOW.md).
#   scripts/pr.sh ["title"]
# Commits anything still uncommitted with the title, pushes, then creates the
# PR against main with "Fixes #<n>" taken from the branch name (fix-<n>-...).
# Put the measured verdict lines into the body with:
#   PR_VERDICTS="$(tail -5 tests/run_test_*.log)" scripts/pr.sh
set -euo pipefail
cd "$(dirname "$0")/.."
if [ -f .git/offline-mode ]; then
  echo "!! offline mode is on (.git/offline-mode) - this step needs GitHub."
  echo "   Land the fix locally instead: scripts/offline.sh land  (docs/WORKFLOW.md)"
  echo "   Or scripts/offline.sh off if the network is back."
  exit 1
fi
BR=$(git rev-parse --abbrev-ref HEAD)
if [ "$BR" = "main" ]; then echo "!! on main - scripts/fix.sh <issue> first"; exit 1; fi
N=$(printf '%s' "$BR" | sed -n 's/^fix-\([0-9][0-9]*\).*/\1/p')
TITLE="${1:-fix #$N ${BR#fix-$N-}}"
if [ -n "$(git status --porcelain)" ]; then
  git add -A
  git commit -q -m "$TITLE"
  echo "==> committed the working tree as: $TITLE"
fi
git push -q -u origin "$BR"
echo "==> pushed $BR ($(git rev-list --count "origin/main..$BR") commit(s) ahead of main)"
BODY="Issue: #${N:-?}"
[ -n "$N" ] && BODY="Fixes #${N}

## What changed

$(git log --format='- %s' "origin/main..$BR")

## Verdicts / measurements

${PR_VERDICTS:-_paste the ✅/❌ lines and measured numbers here - a PR without them is not done (docs/WORKFLOW.md)_}
"
url=$(gh pr create --base main --head "$BR" --title "$TITLE" --body "$BODY")
echo "==> PR $url"
echo "    review, then: scripts/merge.sh"
