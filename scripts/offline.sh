#!/usr/bin/env bash
# scripts/offline.sh - the same loop without GitHub (docs/WORKFLOW.md "Offline
# mode"). For the train, the plane, or a dead Tailnet: you still get an issue
# record, a branch, a tested merge and a commit that says what was measured -
# and when the network is back, `replay` turns those records into real issues
# and real PRs, so main never has to be pushed to or rewritten.
#
#   scripts/offline.sh status              am I in offline mode? (+ a probe)
#   scripts/offline.sh on|off              force the mode on/off
#   scripts/offline.sh issue TITLE [BODY]  local issue -> issues/NNN-slug.md, prints #L<NNN>
#   scripts/offline.sh land [N] ["title"]  run the CI-equivalent checks, merge the
#                                          current branch into `offline`, keep the record
#   scripts/offline.sh replay              online again: publish records as issues + PRs
#
# Local issue numbers carry an L (#L12) so they can never be mistaken for a
# GitHub number, and offline work lands on the branch `offline` - never on
# local main, which stays a mirror of origin/main. Nothing here force-pushes.
set -euo pipefail
cd "$(dirname "$0")/.."
MARK=".git/offline-mode"
REC="issues"

have_net() {
  # A 3 s probe of the remote itself; anything else stays local.
  GIT_TERMINAL_PROMPT=0 git ls-remote --exit-code --heads origin main >/dev/null 2>&1
}

mode() {
  if [ -f "$MARK" ]; then echo offline; else echo online; fi
}

need_online() {
  if [ "$(mode)" = "offline" ]; then
    echo "!! offline mode is on ($MARK) - 'scripts/offline.sh off' when the network is back"; exit 1
  fi
}

next_local_no() {
  mkdir -p "$REC"
  local max=0 f n
  for f in "$REC"/[0-9][0-9][0-9]-*.md; do
    [ -e "$f" ] || continue
    n=$(basename "$f" | cut -c1-3); [ "$n" -gt "$max" ] && max=$n
  done
  printf '%03d' $((10#$max + 1))
}

case "${1:-status}" in
  status)
    m=$(mode)
    echo "mode: $m$([ "$m" = offline ] && echo " (forced by $MARK)")"
    if have_net; then echo "remote: origin reachable"; else echo "remote: origin UNREACHABLE"; [ "$m" = online ] && echo "        -> scripts/offline.sh on"; fi
    echo "local issues: $(ls "$REC"/[0-9][0-9][0-9]-*.md 2>/dev/null | wc -l | tr -d ' ') recorded, $(grep -l '^gh-issue:' "$REC"/*.md 2>/dev/null | wc -l | tr -d ' ') already published"
    echo "offline branch: $(git show-ref --verify --quiet refs/heads/offline && git rev-parse --short offline || echo 'not created yet')"
    ;;

  on)  touch "$MARK"; echo "==> offline mode ON - issues and merges stay local until 'scripts/offline.sh replay'"; ;;
  off) rm -f "$MARK"; echo "==> offline mode OFF"; have_net && echo "    origin reachable" || echo "    !! origin still unreachable - the helpers will fail if they need it"; ;;

  issue)
    shift
    TITLE="${1:-}"; BODY="${2:-}"
    [ -n "$TITLE" ] || { sed -n '10,12p' "$0" | sed 's/^# \{0,1\}//'; exit 2; }
    NO=$(next_local_no)
    SLUG=$(printf '%s' "$TITLE" | tr 'A-Z' 'a-z' | tr -cs 'a-z0-9' '-' | cut -c1-40 | sed 's/-*$//')
    F="$REC/$NO-$SLUG.md"
    {
      echo "# L$NO · $TITLE"
      echo
      echo "- recorded: $(date '+%Y-%m-%d %H:%M') locally, no GitHub number yet"
      echo "- branch: (not started - scripts/fix.sh $NO)"
      echo
      if [ -n "$BODY" ]; then printf '%s\n' "$BODY"; else
        printf '## Problem\n\n\n## How to reproduce\n\n\n## Expected\n\n'
      fi
    } > "$F"
    echo "==> local issue #L$NO -> $F"
    echo "    next: scripts/fix.sh $NO $SLUG"
    ;;

  land)
    shift
    N="${1:-}"; TITLE="${2:-offline fix}"
    BR=$(git rev-parse --abbrev-ref HEAD)
    [ "$BR" != "main" ] || { echo "!! on main - offline work lands from a fix branch into 'offline'"; exit 1; }
    [ -n "$(git status --porcelain)" ] && { echo "!! dirty tree - commit first (offline commits are fine: scripts/pr.sh --offline is not needed)"; exit 1; }
    # The CI-equivalent: the very commands .github/workflows/ci.yml runs, minus
    # the runner. Labelled as local in the commit footer - never claimed as CI.
    echo "==> local build (Release, same projects as CI) ..."
    dotnet build DrivingGame.Server/DrivingGame.Server.csproj -c Release --nologo -v q 2>&1 | grep -oE "[0-9]+ Error\(s\)" | head -1
    dotnet build DrivingGame.Sim.Tests/DrivingGame.Sim.Tests.csproj -c Release --nologo -v q 2>&1 | grep -oE "[0-9]+ Error\(s\)" | head -1
    echo "==> local unit tests ..."
    dotnet test DrivingGame.Sim.Tests/DrivingGame.Sim.Tests.csproj -c Release --no-build --nologo \
      --results-directory TestResults --logger "trx;LogFileName=unit.trx" 2>&1 \
      | grep -E "Passed!|Failed!|error" | head -3 | tee /tmp/offline_test.txt
    grep -q "Failed:     0" /tmp/offline_test.txt || { echo "!! unit tests not green - not landing"; exit 1; }
    if ! git show-ref --verify --quiet refs/heads/offline; then
      git branch offline
      echo "==> created branch 'offline' at $(git rev-parse --short HEAD)"
    fi
    BASE=$(git rev-parse --short offline)
    git checkout -q offline
    git merge --no-ff -q "$BR" -m "$TITLE

Local-issue: ${N:+#$N}${N:-none}
Branch: $BR
Local-CI: core build + unit tests run on $(hostname) $(date '+%Y-%m-%d %H:%M') - $(cat /tmp/offline_test.txt | head -1)
Verdicts:
${PR_VERDICTS:-_paste the e2e verdict lines here (docs/WORKFLOW.md)_}

Rebased-onto: $BASE"
    git checkout -q "$BR"
    echo "==> landed $BR into 'offline' ($(git rev-parse --short offline)); local main untouched"
    [ -n "$N" ] && [ -f "$REC/${N}-"*.md ] 2>/dev/null && \
      printf -- '- landed: %s into offline as %s\n' "$BR" "$(git rev-parse --short offline)" >> "$REC/${N}-"*.md
    echo "    when online: scripts/offline.sh replay"
    ;;

  replay)
    need_online
    shopt -s nullglob
    files=("$REC"/[0-9][0-9][0-9]-*.md)
    [ ${#files[@]} -gt 0 ] || { echo "no local issue records in $REC/"; exit 0; }
    echo "==> records to publish:"
    for f in "${files[@]}"; do
      pub=$(grep -m1 '^gh-issue:' "$f" | cut -d' ' -f2 || true)
      printf '  %s  %s\n' "$(basename "$f")" "${pub:+already #$pub}${pub:-not published}"
    done
    echo
    echo "Replay is deliberate, not automatic: for each record it creates the"
    echo "issue, branches the recorded commits off current origin/main, opens the"
    echo "PR and lets GitHub's CI judge it - offline work never skips that gate."
    echo "Run it per record:  scripts/offline.sh replay-one <NNN>"
    ;;

  replay-one)
    need_online
    NO="${2:-}"
    [ -n "$NO" ] || { echo "usage: scripts/offline.sh replay-one <NNN>"; exit 2; }
    f=$(ls "$REC/$NO"-*.md 2>/dev/null | head -1)
    [ -n "$f" ] || { echo "!! no record $NO in $REC/"; exit 1; }
    grep -q '^gh-issue:' "$f" && { echo "!! $f already published: $(grep -m1 '^gh-issue:' "$f")"; exit 1; }
    TITLE=$(head -1 "$f" | sed 's/^# L[0-9]* · //')
    BODY=$(tail -n +2 "$f")
    url=$(gh issue create --title "$TITLE" --body "$BODY" --json number,url --jq '"#\(.number) \(.url)"')
    echo "==> published $f as $url"
    printf '\ngh-issue: %s\n' "${url%% *}" >> "$f"
    echo "    now: scripts/fix.sh ${url:#}  then cherry-pick / re-apply the recorded change,"
    echo "    and let scripts/pr.sh + scripts/merge.sh run it through real CI."
    ;;

  *) sed -n '2,20p' "$0" | sed 's/^# \{0,1\}//'; exit 2 ;;
esac
