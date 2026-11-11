#!/usr/bin/env bash
# scripts/issue.sh - open the issue that starts the loop (docs/WORKFLOW.md).
#   scripts/issue.sh "Blinker stays on" "reproduce: run_e2e --tests 12 ..."
#   scripts/issue.sh "<title>" --body-file <path>  # long body, no shell quoting
# Prints "#<n> <url>"; that number is what scripts/fix.sh takes.
set -euo pipefail
cd "$(dirname "$0")/.."
if [ $# -lt 1 ] || [ "$1" = "-h" ] || [ "$1" = "--help" ]; then
  sed -n '2,5p' "$0" | sed 's/^# \{0,1\}//'; exit 2
fi
if [ "$1" = "-b" ] || [ "$1" = "--body-file" ]; then
  echo "!! the body is an argument, not a flag: issue.sh \"<title>\" \"<body>\" (or --body-file <path>)" >&2
  exit 2
fi
if [ $# -ge 3 ] && { [ "$2" = "-b" ] || [ "$2" = "--body-file" ]; }; then
  [ -r "$3" ] || { echo "!! cannot read body file '$3'" >&2; exit 2; }
  TITLE="$1"; BODY="$(cat "$3")"
elif [ $# -ge 2 ]; then
  TITLE="$1"; BODY="$2"
else
  TITLE="$1"; BODY=""
fi
if [ -z "$BODY" ]; then
  BODY=$'## Problem\n\n\n## How to reproduce\n\n\n## Expected\n\n'
  echo "!! no body given - filed with the empty template; say 'describe #<n>' and I will fill it" >&2
fi
# `gh issue create` has no --json/--jq (and no --dry-run) in the installed gh 2.102.0,
# so the old call died with "unknown flag: --json" (#43). The plain command writes
# exactly one line to stdout - the new issue's URL, ending in its number - measured:
#   https://github.com/hwacookie/driving-game/issues/118
# Parse that instead of asking gh to format it.
url=$(gh issue create --title "$TITLE" --body "$BODY")
num=${url##*/}
case "$num" in
  ''|*[!0-9]*) echo "!! could not read an issue number from: $url" >&2; exit 1 ;;
esac
echo "==> issue #$num $url"
echo "    next: scripts/fix.sh $num"
