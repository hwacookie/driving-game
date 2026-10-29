#!/usr/bin/env bash
# scripts/issue.sh - open the issue that starts the loop (docs/WORKFLOW.md).
#   scripts/issue.sh "Blinker stays on after the turn" "reproduce: run_e2e --tests 12 ..."
# Prints "#<n> <url>"; that number is what scripts/fix.sh takes.
set -euo pipefail
cd "$(dirname "$0")/.."
if [ $# -lt 1 ] || [ "$1" = "-h" ] || [ "$1" = "--help" ]; then
  sed -n '2,5p' "$0" | sed 's/^# \{0,1\}//'; exit 2
fi
TITLE="$1"; BODY="${2:-}"
if [ -z "$BODY" ]; then
  BODY=$'## Problem\n\n\n## How to reproduce\n\n\n## Expected\n\n'
  echo "!! no body given - filed with the empty template; say 'describe #<n>' and I will fill it" >&2
fi
out=$(gh issue create --title "$TITLE" --body "$BODY" --json number,url --jq '"#\(.number) \(.url)"')
echo "==> issue $out"
echo "    next: scripts/fix.sh ${out%% *}"
