#!/usr/bin/env bash
# scripts/merge.sh - land the PR and get main back to a buildable state
# (docs/WORKFLOW.md). Merge commit, delete the branch, ff-only pull, rebuild.
#   scripts/merge.sh [issue-number]   default: the PR of the current branch
set -euo pipefail
cd "$(dirname "$0")/.."
BR=$(git rev-parse --abbrev-ref HEAD)
N="${1:-$(printf '%s' "$BR" | sed -n 's/^fix-\([0-9][0-9]*\).*/\1/p')}"
if [ -n "${1:-}" ]; then
  BR=$(gh pr list --state open --head "fix-$1" --json headRefName -q '.[0].headRefName // empty')
  [ -n "$BR" ] || { echo "!! no open PR for issue #$1"; exit 1; }
fi
[ "$BR" != "main" ] || { echo "!! nothing to merge from main"; exit 1; }
# --- the CI gate: never land a PR that GitHub did not build and unit-test.
# .github/workflows/ci.yml provides the checks "core build + unit tests" and
# "godot client tests"; --watch blocks until they report. A PR with NO checks
# at all means the workflow has not run yet (or protection is off) - that is
# said out loud rather than treated as a pass.
FORCE="${2:-}"
if [ "$FORCE" != "--force" ]; then
  echo "==> CI gate on $BR: waiting for the required checks (build + unit tests)..."
  if ! out=$(gh pr checks "$BR" --watch --interval 20 2>&1); then
    printf '%s\n' "$out" | tail -12
    if printf '%s' "$out" | grep -qi "no checks\|none found"; then
      echo "!! CI reported no checks for this PR - the workflow did not run."
      echo "   Land it anyway with: scripts/merge.sh ${1:-} --force"
      exit 1
    fi
    echo "!! CI is not green - refusing to merge (docs/WORKFLOW.md)."
    echo "   Fix the failure on the branch; scripts/pr.sh pushes, GitHub re-runs."
    exit 1
  fi
  printf '%s\n' "$out" | tail -6
  echo "==> CI green - merging"
else
  echo "!! --force: merging without waiting for CI"
fi

gh pr merge "$BR" --merge --delete-branch
echo "==> merged $BR, branch deleted on origin"
git checkout -q main
git pull -q --ff-only origin main
echo "==> main at $(git rev-parse --short HEAD): $(git log -1 --format=%s)"
echo "==> rebuilding the merged tree ..."
dotnet build DrivingGame.csproj -v q --nologo 2>&1 | grep -oE "[0-9]+ Error\(s\)" | head -1
echo "    done - main is the tested state"
