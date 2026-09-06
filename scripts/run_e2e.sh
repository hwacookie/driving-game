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
#   4. Launch the Godot client (VISIBLE window unless --no-godot) with
#      --quit-on-test-done: it QUITS IN-APP when the run finishes, so it
#      never has to be killed from outside (any kill signal pops a macOS
#      crash-report window - SIGTERM even aborts .NET's exit path).
#   5. Launch the suite through the host's POST /run_test (server spawns
#      test_turning.py, logs to tests/run_test_N_*.log); stream that log to
#      the console with tail -F and poll GET /run_test until it finishes.
#   6. Stop only the C# host (plain dotnet process - SIGTERM is clean).
#
# Usage:
#   scripts/run_e2e.sh                                  # full suite, visible
#   scripts/run_e2e.sh --no-godot                       # headless (no window)
#   scripts/run_e2e.sh --tests corner_left              # single scenario
#   scripts/run_e2e.sh --tests fig8_stress --stress-cars 250
#
# Logs:
#   game process:   /tmp/game.log
#   godot client:   /tmp/godot.log
#   test output:    tests/run_test_<N>_<timestamp>.log (streamed to console)
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

GAME_LOG=/tmp/game.log
GODOT_LOG=/tmp/godot.log
API=http://127.0.0.1:5000
GODOT_BIN="/Applications/Godot_mono.app/Contents/MacOS/Godot"

RUN_GODOT=1
TEST_SELECTOR=""
EXTRA_ARGS=()
while [ $# -gt 0 ]; do
  case "$1" in
    --no-godot) RUN_GODOT=0; shift ;;
    --tests) TEST_SELECTOR="$2"; shift 2 ;;
    *) EXTRA_ARGS+=("$1"); shift ;;
  esac
done

GAME_PID=""
GODOT_PID=""
TAIL_PID=""
cleanup() {
  [ -n "$TAIL_PID" ] && kill "$TAIL_PID" 2>/dev/null || true
  # Godot: NEVER killed here. It self-terminates via --quit-on-test-done;
  # if it is still around (crash/hang), tell the user instead of popping a
  # crash-report window with a signal.
  if [ -n "$GODOT_PID" ] && kill -0 "$GODOT_PID" 2>/dev/null; then
    echo ""
    echo "!! Godot window still open (pid $GODOT_PID) - close it manually."
  fi
  [ -n "$GAME_PID" ] && kill "$GAME_PID" 2>/dev/null || true
}
trap cleanup EXIT

echo "==> Killing any running game processes (C# host + stale Python src.main)..."
pkill -f "DrivingGame.Server" 2>/dev/null || true
pkill -f "src.main" 2>/dev/null || true
sleep 1

if pgrep -f "Godot_mono.app/Contents/MacOS/Godot" > /dev/null 2>&1; then
  echo "!! A Godot instance is already running - close it first (we never"
  echo "   kill Godot with a signal: that pops macOS crash-report windows)."
  exit 1
fi

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

echo "==> Waiting for API at $API/health ..."
ready=0
for i in $(seq 1 60); do
  if curl -sf "$API/health" > /dev/null 2>&1; then
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

# Resolve --tests <name|number> to a number (the /run_test endpoint takes
# numbers; names are mapped via test_turning.py's own --list-json).
if [ -n "$TEST_SELECTOR" ]; then
  if [[ "$TEST_SELECTOR" =~ ^[0-9]+$ ]]; then
    TEST_NUMBER="$TEST_SELECTOR"
  else
    TEST_NUMBER=$(python3 - "$TEST_SELECTOR" <<'EOF'
import json, subprocess, sys
rows = json.loads(subprocess.check_output(
    [sys.executable, "tests/test_turning.py", "--list-json"]))
name = sys.argv[1].lower()
hits = [r for r in rows if r["key"].lower() == name]
if not hits:
    print(f"!! unknown test '{sys.argv[1]}'", file=sys.stderr)
    sys.exit(2)
print(hits[0]["number"])
EOF
    ) || exit 1
  fi
else
  TEST_NUMBER=""   # empty -> full suite (no --tests arg)
fi

if [ "$RUN_GODOT" -eq 1 ]; then
  if [ ! -x "$GODOT_BIN" ]; then
    echo "!! Godot not found at $GODOT_BIN - continuing without the window"
    RUN_GODOT=0
  else
    echo "==> Launching Godot client (visible window) -> $GODOT_LOG"
    nohup "$GODOT_BIN" --path "$ROOT" -- --quit-on-test-done > "$GODOT_LOG" 2>&1 &
    GODOT_PID=$!
    sleep 3   # let the window come up before the first car teleports in
  fi
fi

echo "==> Launching test suite via POST /run_test ..."
# Build the JSON body: {"number": N, "args": [...]} (number omitted = full suite)
BODY=$(python3 - "${TEST_NUMBER}" ${EXTRA_ARGS[@]+"${EXTRA_ARGS[@]}"} <<'EOF'
import json, sys
argv = sys.argv[1:]
num = argv[0]
extra = argv[1:]
body = {"args": extra}
if num:
    body["number"] = int(num)
print(json.dumps(body))
EOF
)
RESP=$(curl -sf -X POST "$API/run_test" \
  -H "Content-Type: application/json" -d "$BODY") || {
    echo "!! POST /run_test failed: $RESP"; exit 1; }
echo "==> $(echo "$RESP" | python3 -c 'import json,sys; d=json.load(sys.stdin); print(f"test #{d[\"number\"]} started (pid {d[\"pid\"]})")' 2>/dev/null || echo "$RESP")"

LOG_FILE=$(echo "$RESP" | python3 -c 'import json,sys;print(json.load(sys.stdin)["log_file"])')
( tail -n 0 -F "$LOG_FILE" 2>/dev/null ) &
TAIL_PID=$!

echo "==> Waiting for the suite to finish (Ctrl-C aborts)..."
TEST_EXIT=1
while :; do
  ST=$(curl -sf "$API/run_test") || { sleep 2; continue; }
  RUNNING=$(echo "$ST" | python3 -c 'import json,sys;print(json.load(sys.stdin)["running"])')
  if [ "$RUNNING" = "False" ]; then
    # NOTE: `or 1` would turn a legit rc=0 into 1 (0 is falsy in Python).
    TEST_EXIT=$(echo "$ST" | python3 -c 'import json,sys;rc=json.load(sys.stdin).get("returncode");print(1 if rc is None else rc)')
    break
  fi
  sleep 2
done
kill "$TAIL_PID" 2>/dev/null || true

echo ""
if [ "$TEST_EXIT" -eq 0 ]; then
  echo "==> Suite finished: rc=0 ✅ (log: $LOG_FILE)"
else
  echo "==> Suite finished: rc=$TEST_EXIT ❌ (log: $LOG_FILE)"
fi

# Godot quits in-app within ~2 s of the run finishing; give it a moment.
if [ -n "$GODOT_PID" ]; then
  for i in $(seq 1 15); do
    kill -0 "$GODOT_PID" 2>/dev/null || break
    sleep 1
  done
fi

echo "==> Stopping host..."
exit $TEST_EXIT
