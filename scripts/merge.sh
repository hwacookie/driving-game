#!/usr/bin/env bash
# scripts/merge.sh - land the PR and get main back to a buildable state
# (docs/WORKFLOW.md). Merge commit, delete the branch, ff-only pull, rebuild.
#   scripts/merge.sh [issue-number]   default: the PR of the current branch
set -euo pipefail
cd "$(dirname "$0")/.."
HERE=$(pwd)
# In a git worktree `.git` is a FILE, not a directory - so a marker test like
# `[ -f .git/offline-mode ]` silently never fires in a worktree, and a fix
# worktree would pretend the repo is online (or offline) while the primary tree
# says the opposite. Ask git where its state lives; the marker is repo-wide, so
# the shared git dir is the right place (docs/WORKFLOW.md, worktree habit).
GITSTATE=$(git rev-parse --git-common-path)
if [ -f "$GITSTATE/offline-mode" ]; then
  echo "!! offline mode is on (.git/offline-mode) - this step needs GitHub."
  echo "   Land the fix locally instead: scripts/offline.sh land  (docs/WORKFLOW.md)"
  echo "   Or scripts/offline.sh off if the network is back."
  exit 1
fi
BR=$(git rev-parse --abbrev-ref HEAD)
# The landing happens in the PRIMARY worktree: this script may be run from a
# fix worktree (the one whose PR is being landed), and `main` is not a fix
# branch's home (docs/WORKFLOW.md, "One issue, one branch, one directory").
PRIMARY=$(git worktree list --porcelain | awk '/^worktree /{print substr($0,10); exit}')
if [ "$PRIMARY" != "$HERE" ]; then
  cd "$PRIMARY"
  echo "==> landing in the primary worktree: $PWD"
fi
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

# A landed issue leaves no directory behind: the fix worktree was the fix's
# home, the fix is now in main. The worktree has to be found BEFORE the merge:
# gh's --delete-branch deletes the branch first, and a worktree whose branch is
# gone no longer answers that lookup (measured: the removal line never printed,
# the directory survived only because git happened to prune it itself).
WT=$(git worktree list --porcelain |
    awk -v br="$BR" '/^worktree /{w=substr($0,10)} /^branch /{if (substr($0,19)==br) print w}' |
    head -1)
gh pr merge "$BR" --merge --delete-branch
echo "==> merged $BR, branch deleted on origin"
if [ -n "$WT" ] && [ -d "$WT" ]; then
  git worktree remove --force "$WT" && echo "==> removed the fix worktree $WT" ||
    echo "!! worktree $WT is still there - remove it by hand: git worktree remove $WT"
elif [ -n "$WT" ]; then
  echo "==> the fix worktree $WT is gone (gh's branch delete pruned it)"
fi
git branch -D "$BR" 2>/dev/null || true
git switch -q main
git pull -q --ff-only origin main
echo "==> main at $(git rev-parse --short HEAD): $(git log -1 --format=%s)"
echo "==> rebuilding the merged tree ..."
dotnet build DrivingGame.csproj -v q --nologo 2>&1 | grep -oE "[0-9]+ Error\(s\)" | head -1
echo "    done - main is the tested state"
