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
# `--git-common-path` is NOT a git option (measured with git 2.54.0: `git
# rev-parse --git-common-path` prints the flag back, literally
# "--git-common-path", exit 0). So `[ -f "$GITSTATE/offline-mode" ]` tested
# `-f --git-common-path/offline-mode` and never fired - in ANY tree, primary or
# worktree - which means offline mode was silently ignored by this script. The
# spelling that exists is `--git-common-dir`; the absolute form needs
# `--path-format=absolute` (plain `--git-common-dir` answers ".git" in the
# primary tree and a FILE path in a linked worktree).
GITSTATE=$(git rev-parse --path-format=absolute --git-common-dir)
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
# Flags may come in ANY position. The first fix for this ("flags come FIRST", #106) only
# moved the problem: `while [ "${1:-}" = --force ]` still dropped the flag when it followed
# the number — which is the exact form THIS script's own error message tells the user to
# type ("Land it anyway with: scripts/merge.sh 103 --force"). Measured on #113: that form
# entered the CI gate and exited rc=1 printing NOTHING (#115).
FORCE=""
ARGS=()
for a in "$@"; do
  case "$a" in
    --force) FORCE=1 ;;
    -*)
      # An unknown option is refused BY NAME. Read as a positional it became the issue
      # number: `merge.sh --froce 99` set N=--froce, built the pattern
      # "headRefName":"fix---froce…" and died in silence (#115).
      echo "!! unknown option: $a"
      echo "   usage: scripts/merge.sh [issue-number | fix-<n>-branch] [--force]"
      exit 1
      ;;
    *) ARGS+=("$a") ;;
  esac
done
set -- ${ARGS[@]+"${ARGS[@]}"}
# A bare `--force` must not merge whatever branch happens to be checked out. With no
# argument, BR comes from HEAD and N is scraped out of it, so on main there is nothing to
# land - yet the script sailed past the CI gate. (Measured while testing this very fix: a
# fake-gh self-test ran `merge.sh --force` in its own worktree and deleted its own branch.)
if [ -n "$FORCE" ] && [ "${#ARGS[@]}" -eq 0 ] && [ "$(git rev-parse --abbrev-ref HEAD)" = "main" ]; then
  echo "!! --force without a target on main: there is nothing to land here."
  echo "   usage: scripts/merge.sh <issue-number | fix-<n>-branch> --force"
  exit 1
fi
N="${1:-$(printf '%s' "$BR" | sed -n 's/^fix-\([0-9][0-9]*\).*/\1/p')}"
# The argument is an ISSUE NUMBER. A branch name given as the argument must not
# be treated as one: `merge.sh fix-62-bay-claim-as-park-target` used to build the
# pattern "fix-fix-62-…" and answer "no open PR", which cost a merge that had to
# go through `gh pr merge` by hand (measured on PR #107). If the argument already
# looks like a branch, use it as one.
if [ -n "${1:-}" ] && printf '%s' "$1" | grep -qE '^fix-[0-9]'; then
  BR="$1"
  N=$(printf '%s' "$BR" | sed -n 's/^fix-\([0-9][0-9]*\).*/\1/p')
  echo "==> branch given directly: $BR (issue #$N)"
elif [ -n "${1:-}" ]; then
  # `gh pr list --head <name>` matches the head ref EXACTLY (measured on gh
  # 2.102.0): "fix-90", "fix-90*" and "*fix-90*" all answer nothing while the
  # open PR's head is `fix-90-no-space-switch-on-launch`. So the issue number
  # cannot be turned into a branch by prefix - list the open PRs as JSON and
  # match our own naming rule (`fix-<n>-…`, or exactly `fix-<n>`) here. The
  # issues/N/pulls endpoint is NOT an alternative: it 404s even for an issue
  # whose PR was merged (measured: issues/90/pulls -> 404).
  N="$1"
  # The `{ grep … || true; }` is load-bearing, not decoration: under `set -euo pipefail`
  # a grep that matches NOTHING fails the whole substitution, and `set -e` kills the script
  # BEFORE the assignment completes — so the message below never printed and a refused merge
  # looked like a crash (measured: rc=1, empty output; #115).
  BR=$(gh pr list --state open --json headRefName,number \
       | tr -d '\n' | { grep -oE '"headRefName":"fix-'"$N"'(-[^"]*)?"' || true; } \
       | head -1 | sed 's/.*:"//; s/"$//')
  [ -n "$BR" ] || { echo "!! no open PR for issue #$N (no open branch matching fix-$N[-slug])"; exit 1; }
  echo "==> issue #$N -> open PR branch $BR"
fi
[ "$BR" != "main" ] || { echo "!! nothing to merge from main"; exit 1; }
# --- the CI gate: never land a PR that GitHub did not build and unit-test.
# .github/workflows/ci.yml provides the checks "core build + unit tests" and
# "godot client tests"; --watch blocks until they report. A PR with NO checks
# at all means the workflow has not run yet (or protection is off) - that is
# said out loud rather than treated as a pass.
if [ -z "$FORCE" ]; then
  echo "==> CI gate on $BR: waiting for the required checks (build + unit tests)..."
  # Wait for the run to REGISTER before watching. A fix branch must be current
  # with main to merge at all (branch protection refuses otherwise: "the head
  # branch is not up to date with the base branch"), so a rebase + force-push is
  # the normal path into this script - and right after such a push the new
  # workflow run has not appeared yet, `gh pr checks` answers "no checks
  # reported", exits non-zero, and the gate below would refuse a PR that turns
  # green a minute later. Poll the PLAIN form (it prints pending rows without
  # blocking) until something is reported, then hand over to --watch.
  for i in $(seq 1 12); do
    probe=$(gh pr checks "$BR" 2>&1 || true)
    if printf '%s' "$probe" | grep -qi "pending\|pass\|fail"; then
      echo "    CI registered after $((i * 5))s: $(printf '%s' "$probe" | awk -F'\t' '{printf "%s=%s ", $1, $2}')"
      break
    fi
    [ "$i" = "12" ] && echo "    no CI run seen after 60s - the gate below will say so"
    sleep 5
  done
  # The grace period has passed. Ask ONE more time in the plain form before
  # watching: `--watch` can answer "no checks reported" immediately when there is
  # nothing registered, and that must be named as "the workflow did not run"
  # rather than falling into the generic "CI is not green" (#103).
  final=$(gh pr checks "$BR" 2>&1 || true)
  if printf '%s' "$final" | grep -qi "no checks\|none found"; then
    echo "!! CI reported no checks for $BR even after the grace period - the workflow did not run."
    echo "   Push a commit or re-run it (gh run list --branch $BR); land anyway with --force."
    exit 1
  fi
  if ! out=$(gh pr checks "$BR" --watch --interval 20 2>&1); then
    printf '%s\n' "$out" | tail -12
    if printf '%s' "$out" | grep -qi "no checks\|none found"; then
      echo "!! CI reported no checks for this PR - the workflow did not run."
      echo "   Land it anyway with: scripts/merge.sh <issue> --force"
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
