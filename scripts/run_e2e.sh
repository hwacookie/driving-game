#!/usr/bin/env bash
# Run the e2e test suite against a FRESH C# console host (DrivingGame.Server).
# Port of ../car/scripts/run_e2e.sh for the C# sim (Phase 6 of the Python->C#
# port, closed 2026-09-08; the port record is diary/ + git history).
#
# Workflow:
#   1. Kill any stale game processes: C# host, old Python src.main (a stale
#      Python server on :5001 silently answers curls meant for .NET), and
#      any running Godot instance (cleanly - via the generation file,
#      never a signal; see below).
#   2. Build + start the C# console host with the test map, REST API on :5001
#   3. Wait until /health is up
#   4. Launch the Godot client (VISIBLE window unless --no-godot); the
#      runner closes the window again on exit (clean self-quit - the
#      client watches a generation file, never a signal).
#   5. Launch the suite through the host's POST /run_test (server spawns
#      test_turning.py, logs to tests/run_test_N_*.log); stream that log to
#      the console with tail -F and poll GET /run_test until it finishes.
#   6. Stop the C# host (plain dotnet process - SIGTERM is clean) and close
#      the Godot window. On failure the host stays up so the frozen crash
#      scene can be analysed (stop it later: pkill -f DrivingGame.Server).
#
# Usage:
#   scripts/run_e2e.sh                                  # full suite, visible
#   scripts/run_e2e.sh --no-godot                       # headless (no window)
#   scripts/run_e2e.sh --tests corner_left_entry        # single scenario
#   scripts/run_e2e.sh --tests 26 --watch                 # watch one, staged frozen
#   scripts/run_e2e.sh --tests fig8_stress --stress-cars 250
#   scripts/run_e2e.sh --tests 21-24                        # resume a range
#
# Logs:
#   game process:   /tmp/game.log
#   godot client:   /tmp/godot.log
#   test output:    tests/run_test_<N>_<timestamp>.log (streamed to console)
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

# Test-suite Python: prefer the repo venv (docs/TESTING.md convention).
# The C# host honours $PYTHON for POST /run_test; without it the suite
# runs on PATH python3, which may lack third-party deps (e.g. requests).
if [ -x "$ROOT/.venv/bin/python" ]; then
  export PYTHON="$ROOT/.venv/bin/python"
fi

GAME_LOG=/tmp/game.log
GODOT_LOG=/tmp/godot.log
API=http://127.0.0.1:5001
GODOT_BIN="/Applications/Godot_mono.app/Contents/MacOS/Godot"

RUN_GODOT=1
TEST_SELECTOR=""
EXTRA_ARGS=()
HOST_ARGS=()   # --fast / --warp N go to the C# host, not to the suite
PACING=""      # empty = the default below (--warp 4)
FOCUS_UID=""   # --focus-car <uid>: the client binds to that car
MAXIM=0        # --maximized: the Godot window fills the screen (borderless)
# --watch: the TWO-MODE rule (AGENTS.md). Watching is not racing: the window
# fills the screen, the camera pins on the named hotspot, and the harness
# HALTS the sim while it stages the cars, spawns every one of them AT SPEED,
# polls until every uid exists, and only then unfreezes at the requested
# pace. An e2e run (--warp 4, smaller window, named scenarios) never does.
DG_FREEZE_STAGE="${DG_FREEZE_STAGE:-0}"; export DG_FREEZE_STAGE
# --crash-shot <path>: one PNG 0.5 s after the crash camera latches (diagnostic).
# Read from the environment (set it when invoking the runner); initialised empty
# so `set -u` does not die on an unset variable.
CRASH_SHOT="${CRASH_SHOT:-}"
while [ $# -gt 0 ]; do
  case "$1" in
    --no-godot) RUN_GODOT=0; shift ;;
    --tests) TEST_SELECTOR="$2"; shift 2 ;;
    --fast) HOST_ARGS+=(--fast); PACING=1; shift ;;
    --warp) HOST_ARGS+=(--warp "$2"); PACING=1; shift 2 ;;
    --realtime) PACING=1; shift ;;   # explicit: real time, no warp
    --focus-car) FOCUS_UID="$2"; shift 2 ;;   # watch that car (the last crashed uid)
    --maximized) MAXIM=1; shift ;;   # Godot window at full screen size
    --watch) MAXIM=1; DG_FREEZE_STAGE=1; export DG_FREEZE_STAGE; shift ;;
    --zoom) CAM_ZOOM="$2"; shift 2 ;;
    --center) CAM_CENTER="$2 $3"; shift 3 ;;
    *) EXTRA_ARGS+=("$1"); shift ;;
  esac
done
# Default pacing: --warp 4. The suite's guardrails are in SIM time and its
# poll resolution scales with the announced warp, so a 4x run gives the same
# verdicts ~3x faster on the wall clock. --realtime opts out (and is what
# the capacity probe needs).
if [ -z "$PACING" ]; then HOST_ARGS+=(--warp 4); PACING="default --warp 4"; fi

GAME_PID=""
GODOT_PID=""
TAIL_PID=""
# Clean Godot close: the client (MapRenderer) watches this file and quits
# itself when the value changes. We NEVER signal Godot - a SIGTERM kills
# .NET's exit path into an abort and pops a macOS crash-report window.
GODOT_GEN_FILE=/tmp/driving_game_godot_gen
HOLD_OPEN=0   # set to 1 on failure: leave host + Godot up for analysis
cleanup() {
  [ -n "$TAIL_PID" ] && kill "$TAIL_PID" 2>/dev/null || true
  # Close our Godot client window CLEANLY (bump the generation file - the
  # client quits itself; never a signal). On failure the C# host stays up,
  # so the frozen scene can be re-opened in a new window.
  if [ -n "$GODOT_PID" ] && kill -0 "$GODOT_PID" 2>/dev/null; then
    echo ""
    echo "==> Closing Godot window (pid $GODOT_PID)..."
    echo "$RANDOM$RANDOM" > "$GODOT_GEN_FILE"
    for i in $(seq 1 10); do
      kill -0 "$GODOT_PID" 2>/dev/null || break
      sleep 1
    done
    if kill -0 "$GODOT_PID" 2>/dev/null; then
      echo "!! Godot window (pid $GODOT_PID) did not close - close it"
      echo "   manually (no signal: it pops a macOS crash-report window)."
    fi
  fi
  # On failure, keep the C# host alive too so the frozen crash scene stays
  # interactive (click cars, /state, /car/uid/decisions). Stop it with:
  #   pkill -f DrivingGame.Server
  if [ "$HOLD_OPEN" -eq 1 ]; then
    echo "!! Test failed - C# host left running (pid $GAME_PID) for analysis."
    echo "   Stop it when done:  pkill -f DrivingGame.Server"
  else
    [ -n "$GAME_PID" ] && kill "$GAME_PID" 2>/dev/null || true
  fi
}
trap cleanup EXIT

echo "==> Killing any running game processes (C# host + stale Python src.main + Godot)..."
pkill -f "DrivingGame.Server" 2>/dev/null || true
pkill -f "src.main" 2>/dev/null || true
sleep 1

# Close any stale Godot window CLEANLY: never signal Godot (a SIGTERM
# kills .NET's exit path into an abort and pops a macOS crash-report
# window). Bump the generation file - the client quits itself - then wait.
# Bounded: if it does not exit within 10 s, abort rather than pile on.
if G_PIDS=$(pgrep -f "$GODOT_BIN"); then
  echo "==> Closing stale Godot instance(s): $(echo $G_PIDS | tr '\n' ' ')"
  echo "$RANDOM$RANDOM" > "$GODOT_GEN_FILE"
  for i in $(seq 1 10); do
    pgrep -f "$GODOT_BIN" > /dev/null || break
    sleep 1
  done
  if pgrep -f "$GODOT_BIN" > /dev/null 2>&1; then
    echo "!! Godot did not close - close the window manually and re-run."
    exit 1
  fi
fi

echo "==> Building DrivingGame.Server..."
dotnet build DrivingGame.Server/DrivingGame.Server.csproj -v q --nologo

# Godot client assembly: the Godot project (root DrivingGame.csproj) must
# be built too - Godot loads MapRenderer & friends from
# .godot/mono/temp/bin/Debug/, which a Server-only build never touches.
# Without it the window comes up EMPTY ("Cannot instantiate C# script").
echo "==> Building Godot project assembly..."
dotnet build DrivingGame.csproj -v q --nologo

# Godot asset import (.godot/imported is gitignored): a fresh clone or a
# wiped .godot dir has no imported textures, and the window then renders
# without signs/car sprites (and MapRenderer NREs on the null textures).
# Idempotent - a few seconds when the cache is current.
if [ -x "$GODOT_BIN" ]; then
  echo "==> Importing Godot resources (headless)..."
  "$GODOT_BIN" --headless --path "$ROOT" --import > /dev/null 2>&1 || {
    echo "!! Godot asset import failed - the window would render without textures."
    exit 1; }
fi

BIN="$ROOT/DrivingGame.Server/bin/Debug/net9.0/DrivingGame.Server"
if [ ! -x "$BIN" ]; then
  echo "!! Build output not found at $BIN"
  exit 1
fi

echo "==> Starting C# console host (map=basic, API :5001) -> $GAME_LOG"
if [ ${#HOST_ARGS[@]} -gt 0 ]; then
  echo "    pacing: ${HOST_ARGS[*]} (the suite paces on SIM time, so the"
  echo "    run goes as fast as the CPU; the capacity probe is skipped)"
fi
nohup "$BIN" --map basic ${HOST_ARGS[@]+"${HOST_ARGS[@]}"} > "$GAME_LOG" 2>&1 &
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
  elif [[ "$TEST_SELECTOR" =~ ^[0-9]+(-[0-9]+)?(,[0-9]+(-[0-9]+)?)*$ ]]; then
    # Numeric multi/range spec (e.g. "21-24", "3,7-9"): the /run_test
    # endpoint only takes ONE number, so hand the spec through to the
    # suite's own --tests parser instead (original numbering is kept).
    EXTRA_ARGS+=(--tests "$TEST_SELECTOR")
    TEST_NUMBER=""
  else
    # ${PYTHON:-python3}: test_turning.py imports requests at module level,
    # so --list-json needs the venv interpreter (PATH python3 may lack it).
    TEST_NUMBER=$(${PYTHON:-python3} - "$TEST_SELECTOR" <<'EOF'
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

# Camera for the visible window. Default: FOLLOW the car at a driving zoom,
# so every maneuver is actually visible (user decision 2026-10-18: "start
# zoomed on the car, in follow mode"). Crossing scenarios (50 cars) keep the
# pinned view of the junction instead - following one car among 50 shows less.
GODOT_CAM_ARGS=()
# Godot's OWN options go BEFORE the "--" separator (everything after it is the
# client's). --fullscreen maximizes the window: the project keeps stretch
# DISABLED (viewport == window, see project.godot), so the overlays - minimap,
# zoom label - are laid out from GetVisibleRect() and follow the new size.
GODOT_WIN_ARGS=()
if [ "$MAXIM" -eq 1 ] && [ "$RUN_GODOT" -eq 1 ]; then
  GODOT_WIN_ARGS=(--fullscreen)
  echo "==> Window: MAXIMIZED (--fullscreen) - the map fills the screen"
  [ "${DG_FREEZE_STAGE:-0}" = "1" ] && echo "==> Staging: sim FROZEN while cars are placed, unfrozen at the announced pace"
fi
if [ "$RUN_GODOT" -eq 1 ]; then
  GODOT_CAM_ARGS=(--zoom "${CAM_ZOOM:-14}")
  echo "==> Camera: follow mode at zoom ${CAM_ZOOM:-14} (px/m) - the window tracks the car"
fi
# The camera for a single-scenario run comes from the SCENARIO, not from a
# table in this script: tests/test_turning.py resolves its own SCENARIO_FOCUS
# against the live map (POST /focus does the same for a full-suite run, and the
# tape records it for the reviewer). Two kinds reach the client: a PLACE
# (--center, no car followed) and a CAR (--focus <name>). Cosmetic: a failure
# here must never abort a run.
if [ -n "$TEST_NUMBER" ] && [ "$RUN_GODOT" -eq 1 ]; then
  FOCUS_JSON=$(${PYTHON:-python3} tests/test_turning.py --focus-json "$TEST_NUMBER" 2>/dev/null) || FOCUS_JSON=""
  if [ -n "$FOCUS_JSON" ]; then
    export FOCUS_JSON
    CAM=$(${PYTHON:-python3} <<'PYFO' 2>/dev/null
import json, os
try:
    f = json.loads(os.environ.get("FOCUS_JSON") or "{}")
except Exception:
    f = {}
if f.get("kind") == "loc" and "x" in f:
    # A PLACE: no car is followed — the scenario's subject does not move.
    print(f"--center {f['x']:.2f} {f['y']:.2f} --zoom {f.get('zoom') or 26:g}")
elif f.get("kind") == "named" and f.get("name"):
    print(f"--focus {f['name']} --zoom {f.get('zoom') or 16:g}")
PYFO
)
    if [ -n "$CAM" ]; then
      # An explicit --center/--zoom on the runner's own command line still wins:
      # the operator outranks the scenario (the same rule the client applies).
      read -r -a CAM_ARGS <<<"$CAM"
      [ -n "$CAM_CENTER" ] && CAM_ARGS=(--center $CAM_CENTER)
      [ -n "$CAM_ZOOM" ] && CAM_ARGS=("${CAM_ARGS[@]/--zoom*}" --zoom "$CAM_ZOOM")
      GODOT_CAM_ARGS=("${CAM_ARGS[@]}")
      case "${CAM_ARGS[0]}" in
        --center) SAY="(a PLACE - no car is followed)" ;;
        *)        SAY="(a named car)" ;;
      esac
      echo "==> Camera per the scenario: ${GODOT_CAM_ARGS[*]} $SAY"
    fi
  fi
fi

if [ -n "$FOCUS_UID" ] && [ "$RUN_GODOT" -eq 1 ]; then
  GODOT_CAM_ARGS+=(--focus "$FOCUS_UID")
  echo "==> Camera: bound to car $FOCUS_UID @ 34 px/m - that car is what you watch"
fi
if [ -n "$CRASH_SHOT" ] && [ "$RUN_GODOT" -eq 1 ]; then
  GODOT_CAM_ARGS+=(--crash-shot "$CRASH_SHOT")
  echo "==> Diagnostic: one PNG 0.5 s after the crash camera latches -> $CRASH_SHOT"
fi

if [ "$RUN_GODOT" -eq 1 ]; then
  if [ ! -x "$GODOT_BIN" ]; then
    echo "!! Godot not found at $GODOT_BIN - continuing without the window"
    RUN_GODOT=0
  else
    echo "==> Launching Godot client (visible window) -> $GODOT_LOG"
    # The runner closes this window again on exit (clean self-quit via the
    # generation file - never a signal). On failure the C# host stays up,
    # so the scene can be re-opened in a new Godot window.
    nohup "$GODOT_BIN" --path "$ROOT" ${GODOT_WIN_ARGS[@]+"${GODOT_WIN_ARGS[@]}"} -- ${GODOT_CAM_ARGS[@]+"${GODOT_CAM_ARGS[@]}"} > "$GODOT_LOG" 2>&1 &
    GODOT_PID=$!
    sleep 3   # let the window come up before the first car teleports in
  fi
fi

# The run's TAPE (docs/RECORD_PLAYBACK.md): every substep of every car, kept
# for the whole run, so a failure report can be re-watched frame by frame.
# Started here - before any scenario stages a car - so the tape covers the run
# from the first substep.
echo "==> Starting the substep tape ..."
TAPE_START=$(curl -sf -X POST "$API/tape" -H "Content-Type: application/json" \
                 -d '{"map": "basic"}')
if [ -n "$TAPE_START" ]; then
  echo "==> Tape on: $(echo "$TAPE_START" | tr -d ' ' | cut -c1-120)"
else
  echo "!! tape start failed - the run works, there is just no tape"
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
  # Keep host + window up so the frozen crash can be analysed (visible mode).
  [ "$RUN_GODOT" -eq 1 ] && HOLD_OPEN=1
fi

# Save the tape NEXT TO the run log: one file per run, same naming, so a
# report and its frames stay a pair months later.
TAPE_FILE=$(echo "$LOG_FILE" | sed 's|run_test_|tape_|; s|\.log$|.txt|')
if [ -n "$TAPE_FILE" ]; then
  TAPE_SAVE=$(curl -sf -X POST "$API/tape/save" -H "Content-Type: application/json" \
                 -d "{\"path\": \"$TAPE_FILE\"}")
  if [ -n "$TAPE_SAVE" ]; then
    echo "==> Tape saved: $(echo "$TAPE_SAVE" | tr -d ' ' | cut -c1-160)"
  else
    echo "!! tape save failed"
  fi
fi

echo "==> Stopping host..."
exit $TEST_EXIT
