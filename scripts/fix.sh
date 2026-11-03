#!/usr/bin/env bash
# scripts/fix.sh - the branch AND the directory for one issue (docs/WORKFLOW.md).
#   scripts/fix.sh 31  [slug]   -> branch fix-31-slug off origin/main in a new
#                                  worktree <repo-dir>-31 (the main tree stays
#                                  where it is: one issue, one branch, one
#                                  directory — see docs/WORKFLOW.md "One issue,
#                                  one branch, one directory")
# Refuses a dirty tree: a half-saved edit must not ride into the fix.
set -euo pipefail
cd "$(dirname "$0")/.."
if [ $# -lt 1 ]; then sed -n '2,7p' "$0" | sed 's/^# \{0,1\}//'; exit 2; fi
N="$1"; SLUG="${2:-}"
if [ "${1:-}" = "--allow-dirty" ]; then echo "!! skipping the clean-tree check" ; shift; N="${1:-$N}"; fi
# The clean-tree check ignores UNTRACKED issue records: scripts/offline.sh issue
# writes issues/NNN-slug.md and it would be absurd for the record of a problem to
# block the branch that fixes it. Anything else dirty still stops the branch.
DIRTY=$(git status --porcelain | grep -v '^?? issues/' || true)
if [ -n "$DIRTY" ] && [ "${1:-}" != "--allow-dirty" ]; then
  echo "!! working tree is dirty - commit or stash first (or pass --allow-dirty)"; git status --short | head; exit 1
fi
# --- one issue, one branch, ONE directory --------------------------------
# A `git checkout -b` is a move of the TREE, not of my attention: with other
# sessions working other issues in their own worktrees, moving the main tree
# moves the files they are building (measured: the main tree sat on fix-62-…
# while a driving-game-62 worktree was alive next to it, and a build in that
# window compiled a tree that was not the one I was reading). So the fix
# branch gets its own directory, named after the repo plus the issue number.
PRIMARY=$(git worktree list --porcelain | awk '/^worktree /{print substr($0,10); exit}')
WT_DIR="${PRIMARY}-${N}"
wt_existing() {   # the worktree already carrying $1 (a branch name), if any
  git worktree list --porcelain |
    awk -v br="$1" '/^worktree /{w=substr($0,10)} /^branch /{if (substr($0,19)==br) print w}' |
    head -1
}

# Offline work (#L<nnn> records, or the .git/offline-mode marker) never touches
# the network and never branches off a stale origin/main: it branches off the
# `offline` branch, where the local landings accumulate (docs/WORKFLOW.md).
if [ -f "$(git rev-parse --git-common-path)/offline-mode" ] || [ "${N#L}" != "$N" ]; then
  [ "${N#L}" != "$N" ] || N="L$N"
  WT_DIR="${PRIMARY}-${N}"
  BASE=HEAD
  if git show-ref --verify --quiet refs/heads/offline; then BASE=offline; fi
  if [ -z "$SLUG" ]; then
    rec=$(ls "issues/${N#L}"-*.md 2>/dev/null | head -1 || true)
    [ -n "$rec" ] && SLUG=$(basename "$rec" | sed "s/^[0-9]*-//; s/\.md$//")
  fi
  BR="fix-$N${SLUG:+-$SLUG}"
  if git show-ref --verify --quiet "refs/heads/$BR"; then
    WT=$(wt_existing "$BR")
    if [ -n "$WT" ]; then echo "==> branch $BR already exists, worktree $WT - work there"; WT_DIR="$WT";
    else
      echo "==> branch $BR already exists (offline), no worktree - creating $WT_DIR on it"
      git worktree add -q "$WT_DIR" "$BR"
    fi
  else
    git worktree add -q -b "$BR" "$WT_DIR" "$BASE"
    echo "==> OFFLINE: branch $BR off $BASE ($(git rev-parse --short "$BASE")) in worktree $WT_DIR - no fetch, no GitHub"
  fi
  cd "$WT_DIR"
  # the record belongs to the fix branch, not to main (main stays a mirror)
  rec_files=$(git status --porcelain | sed -n 's/^?? \(issues\/.*\.md\)$/\1/p' || true)
  if [ -n "$rec_files" ]; then
    printf '%s\n' "$rec_files" | xargs git add
    git commit -q -m "issue: local record ${N} for $(basename $(printf '%s' "$rec_files" | head -1) .md)"
    echo "==> committed the record on $BR ($(printf '%s\n' "$rec_files" | tr '\n' ' '))"
  fi
  rec2=$(ls "issues/${N#L}"-*.md 2>/dev/null | head -1 || true)
  [ -n "$rec2" ] && sed -i '' "s|- branch: (not started.*|- branch: $BR|" "$rec2" && \
    git add "$rec2" && git commit -q --amend --no-edit
  echo "    record: ${rec2:-none}   land with: scripts/offline.sh land $N"
  exit 0
fi
git fetch -q origin
if [ -z "$SLUG" ]; then
  SLUG=$(gh issue view "$N" --json title -q .title | tr 'A-Z' 'a-z' | tr -cs 'a-z0-9' '-' | cut -c1-32 | sed 's/-*$//')
fi
BR="fix-$N${SLUG:+-$SLUG}"
if git show-ref --verify --quiet "refs/heads/$BR"; then
  WT=$(wt_existing "$BR")
  if [ -n "$WT" ]; then
    echo "==> branch $BR already exists, worktree $WT - work there"; WT_DIR="$WT"
  else
    echo "==> branch $BR already exists, no worktree - creating $WT_DIR on it"
    git worktree add -q "$WT_DIR" "$BR"
  fi
else
  git worktree add -q -b "$BR" "$WT_DIR" origin/main
  echo "==> branch $BR off origin/main ($(git rev-parse --short origin/main)) in worktree $WT_DIR"
fi
echo "    issue: https://github.com/hwacookie/driving-game/issues/$N"
echo "    work in: cd \"$WT_DIR\""
echo "    fix, test (scripts/run_e2e.sh), then: scripts/pr.sh   (from that directory)"
