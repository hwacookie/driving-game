#!/usr/bin/env bash
# Run the e2e test suite against a FRESH C# console host (DrivingGame.Server).
# Port of ../car/scripts/run_e2e.sh for the C# sim (docs/C_SHARP_PORT_SPEC.md,
# Phase 6 burn-in / gate G5 prep).
#
# Workflow:
#   1. Kill any stale game processes (C# host AND old Python src.main - a
#      stale Python server on :5000 silently answers curls meant for .NET)
#   2. Build + start the C# console host with the test map, REST API on :5000
#   3. Wait until /health is up
#   4. Launch the Godot client (VISIBLE window - MapRenderer polls :5000, so
#      you can watch the car while the suite runs) unless --no-godot
#   5. Run the e2e suite (tests/test_turning.py, lives in this repo since
#      the 2026-09-05 consolidation) in the foreground: output goes to the
#      console AND /tmp/test_run.log via tee
#   6. Stop host + Godot on exit
#
# Usage:
#   scripts/run_e2e.sh                                  # full suite, visible
#   scripts/run_e2e.sh --no-godot                       # headless (no window)
#   scripts/run_e2e.sh --tests corner_left              # single scenario
#
# Logs:
#   game process:  /tmp/game.log
#   godot client:  /tmp/godot.log
#   test output:   /tmp/test_run.log
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

GAME_LOG=/tmp/game.log
GODOT_LOG=/tmp/godot.log
TEST_LOG=/tmp/test_run.log
API_URL=http://127.0.0.1:5000/health
GODOT_BIN="/Applications/Godot_mono.app/Contents/MacOS/Godot"

RUN_GODOT=1
EXTRA_ARGS=()
for a in "$@"; do
  case "$a" in
    --no-godot) RUN_GODOT=0 ;;
    *) EXTRA_ARGS+=("$a") ;;
  esac
done

GAME_PID=""
GODOT_PID=""
cleanup() {
  [ -n "$GODOT_PID" ] && kill "$GODOT_PID" 2>/dev/null || true
  [ -n "$GAME_PID" ] && kill "$GAME_PID" 2>/dev/null || true
}
trap cleanup EXIT

echo "==> Killing any running game processes (C# host + stale Python src.main)..."
pkill -f "DrivingGame.Server" 2>/dev/null || true
pkill -f "src.main" 2>/dev/null || true
sleep 1

echo "==> Building DrivingGame.Server..."
dotnet build DrivingGame.Server/DrivingGame.Server.csproj -v q --nologo

BIN="$ROOT/DrivingGame.Server/bin/Debug/net9.0/DrivingGame.Server"
if [ ! -x "$BIN" ]; then
  echo "!! Build output not found at $BIN"
  exit 1
fi

echo "==> Starting C# console host (map=basic, API :5000) -> $GAME_LOG"
nohup "$BIN" --map basic > "$GAME_LOG" 2>&1 &
GAME_PID=$!

echo "==> Waiting for API at $API_URL ..."
ready=0
for i in $(seq 1 60); do
  if curl -sf "$API_URL" > /dev/null 2>&1; then
    echo "==> API ready after ${i}s"
    ready=1
    break
  fi
  if ! kill -0 "$GAME_PID" 2>/dev/null; then
    echo "!! Host process died during startup. Last log lines:"
    tail -30 "$GAME_LOG"
    exit 1
  fi
  sleep 1
done
if [ "$ready" -ne 1 ]; then
  echo "!! API never came up. Last log lines:"
  tail -30 "$GAME_LOG"
  exit 1
fi

if [ "$RUN_GODOT" -eq 1 ]; then
  if [ ! -x "$GODOT_BIN" ]; then
    echo "!! Godot not found at $GODOT_BIN - continuing without the window"
  else
    echo "==> Launching Godot client (visible window) -> $GODOT_LOG"
    nohup "$GODOT_BIN" --path "$ROOT" > "$GODOT_LOG" 2>&1 &
    GODOT_PID=$!
    sleep 3   # let the window come up before the first car teleports in
  fi
fi

echo "==> Running e2e suite (log: $TEST_LOG)..."
# -u: unbuffered stdout, so output appears on the console immediately
#     (piping through tee would otherwise block-buffer it until the end)
python3 -u "$ROOT/tests/test_turning.py" "${EXTRA_ARGS[@]}" 2>&1 | tee "$TEST_LOG"
TEST_EXIT=${PIPESTATUS[0]}

echo "==> Stopping host + Godot..."
if [ -n "$GAME_PID" ] && ! kill -0 "$GAME_PID" 2>/dev/null; then
  if grep -qiE "Unhandled exception|Fatal error" "$GAME_LOG" 2>/dev/null; then
    echo ""
    echo "!! Host process CRASHED (stack in $GAME_LOG). Last lines:"
    tail -20 "$GAME_LOG"
  fi
fi

exit $TEST_EXIT
