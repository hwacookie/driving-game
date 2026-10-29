#!/usr/bin/env bash
# open.sh - THE short way to a viewer window. Nothing is tested, nothing is
# run: the sim starts, the window opens WHERE YOU LEFT IT LAST (its geometry
# lives in user://window.ini - docs/TESTING.md §1b) and stays open until YOU
# close it.
#
#   scripts/open.sh                    # live session, embedded sim, API :5001
#   scripts/open.sh --zoom 20          # closer in
#   scripts/open.sh --map hills        # another map (see DrivingGame.Sim/TestMaps.cs)
#   scripts/open.sh --review "$(ls -t tests/tape_T_*.txt | head -1)"  # the reviewer instead
#     A review window needs NO host: --review builds its ground in-process from
#     the tape's own map= header and binds no API port (issue #2). To review
#     against a host instead (a ground on another machine), add --source http.
#   scripts/open.sh --external         # attach to a host you started yourself (HTTP mode)
#   scripts/open.sh --close            # close the window this script opened (clean, no signal)
#
# Quit rule of the project (AGENTS.md): Godot is NEVER sent a signal - a
# SIGTERM kills .NET's exit path into an abort and pops a macOS crash report.
# `--close` bumps /tmp/driving_game_godot_gen, which the client watches and
# answers by quitting itself.

set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
GODOT="${GODOT_BIN:-/Applications/Godot_mono.app/Contents/MacOS/Godot}"
GEN=/tmp/driving_game_godot_gen
PIDFILE=/tmp/driving_game_client_pid
LOG=/tmp/godot_open.log

MAP=basic
PORT=5001
ZOOM=14
MODE=embedded
REVIEW=""
CLOSE_ONLY=0
EXTRA=()

usage() { sed -n '2,20p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0; }

while [ $# -gt 0 ]; do
  case "$1" in
    --map) MAP="$2"; shift 2 ;;
    --port) PORT="$2"; shift 2 ;;
    --zoom) ZOOM="$2"; shift 2 ;;
    --review) MODE=review; REVIEW="$2"; shift 2 ;;
    --external) MODE=http; shift ;;
    --close) CLOSE_ONLY=1; shift ;;
    -h|--help) usage ;;
    *) EXTRA+=("$1"); shift ;;
  esac
done

client_running() {
  local pid; pid=$(cat "$PIDFILE" 2>/dev/null || true)
  [ -n "${pid:-}" ] && kill -0 "$pid" 2>/dev/null && { echo "$pid"; return 0; }
  return 1
}

if [ "$CLOSE_ONLY" -eq 1 ]; then
  if pid=$(client_running); then
    echo "==> closing the viewer (pid $pid) by generation bump - it quits itself"
    echo $RANDOM > "$GEN"
    for _ in $(seq 1 20); do kill -0 "$pid" 2>/dev/null || { echo "==> closed"; exit 0; }; sleep 0.5; done
    echo "!! pid $pid is still up after 10 s - it is not watching $GEN (a run_e2e.sh client, or one started by hand); close it from its own window"
    exit 1
  fi
  echo "==> no viewer of mine is running"; exit 0
fi

if pid=$(client_running); then
  echo "==> already open (pid $pid) - one window per project, use 'scripts/open.sh --close' first"
  exit 0
fi

[ -x "$GODOT" ] || { echo "!! no Godot at $GODOT (set GODOT_BIN)"; exit 1; }

ARGS=(--path "$ROOT" --)
if [ "$MODE" = review ]; then
  [ -f "$REVIEW" ] || { echo "!! no tape at '$REVIEW'"; exit 1; }
  ARGS+=(--review "$REVIEW" --zoom "$ZOOM")
else
  if [ "$MODE" = embedded ]; then
    # A busy port means something else owns :5001 - most often a host from a
    # run_e2e.sh run. Saying so beats the client's own silent-ish failure
    # ("embedded REST API FAILED ... Address already in use") plus a hang.
    if curl -s -m 1 "http://127.0.0.1:$PORT/health" >/dev/null; then
      echo "!! port $PORT is answered by something already running - use --external to attach to it, or --port 5011"
      exit 1
    fi
    ARGS+=(--source embedded --map "$MAP" --port "$PORT" --zoom "$ZOOM")
  else
    ARGS+=(--source http --zoom "$ZOOM")
  fi
fi
[ ${#EXTRA[@]} -gt 0 ] && ARGS+=("${EXTRA[@]}")

echo "==> opening the $MODE window (map $MAP, zoom $ZOOM) - log: $LOG"
: > "$LOG"
nohup "$GODOT" "${ARGS[@]}" > "$LOG" 2>&1 &
pid=$!; echo "$pid" > "$PIDFILE"

# Poll for the condition, never a fixed sleep (AGENTS.md): the map line is the
# moment the picture is really there.
for _ in $(seq 1 60); do grep -q "Map ready" "$LOG" && break; kill -0 "$pid" 2>/dev/null || break; sleep 0.5; done

grep -E "WINDOW-PLACEMENT|Map ready|REVIEW of|FAILED|ERROR" "$LOG" | head -4
if kill -0 "$pid" 2>/dev/null; then
  echo "==> viewer open (pid $pid), it stays open - close it with 'scripts/open.sh --close'"
else
  echo "!! the client exited - last lines of $LOG:"; tail -5 "$LOG"; exit 1
fi
