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
gh pr merge --merge --delete-branch --subject "$BR"
echo "==> merged $BR, branch deleted on origin"
git checkout -q main
git pull -q --ff-only origin main
echo "==> main at $(git rev-parse --short HEAD): $(git log -1 --format=%s)"
echo "==> rebuilding the merged tree ..."
dotnet build DrivingGame.csproj -v q --nologo 2>&1 | grep -oE "[0-9]+ Error\(s\)" | head -1
echo "    done - main is the tested state"
