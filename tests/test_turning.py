#!/usr/bin/env python3
"""
Comprehensive Turn Testing via REST API
Tests both left and right turns with detailed monitoring
"""

import math
import os
import requests
import time
import sys
import json
import signal
from datetime import datetime
from pathlib import Path

# The braking-distance math below must use the game's actual deceleration,
# so these mirror DrivingGame.Sim/Config.cs (single source of truth -
# updated from src/config.py when this suite moved into this repo).
CAR_BRAKING = 10.0          # m/s² (the game's ABS braking)
CAR_WIDTH = 1.8             # m
SPRITE_WHEELBASE_M = 2.64   # m (FRONT_AXLE_OFFSET_M + REAR_AXLE_OFFSET_M)


API_URL = os.environ.get("CAR_API_URL", "http://127.0.0.1:5001")  # explicit IPv4: 'localhost' may resolve to ::1 (and :5000 is squatted on by macOS AirPlay Receiver anyway)

# The suite speaks METRES end to end, and now so does the whole API:
# /state, /map, /segment and /start_points all answer in metres
# (docs/UNITS.md). Nothing here converts any more.

# Results are persisted next to this script, keyed by "start_point|direction",
# so the next run can report whether each scenario passed the last time it ran
# (and why it failed, if it didn't). See docs/SPEC.md ("Turn Test Output").
RESULTS_FILE = Path(__file__).resolve().parent / "turning_results.json"
HISTORY_LIMIT = 500  # cap the per-scenario history so the file can't grow unbounded
# Signal the turn (blinker) only this many metres before the junction -
# like a real driver, not the instant the test starts.
SIGNAL_DISTANCE_M = 50.0
# A scenario counts as "stopped at the end flag" if the car comes to rest
# within this distance of the 50% point - anticipatory braking plus ~20 Hz
# polling and brake response cannot stop with infinite precision.
#
# The runner's brake-point model (v^2/2a + 0.15*v slack) systematically
# under-predicts the real stopping distance by a few metres (polling
# latency, brake propagation, autopilot modulation), so the car typically
# comes to rest 2.5-3.5 m SHORT of the flag (measured on corner_left,
# where runs landed at 2.5 m and 3.35 m). The tolerance must cover that
# systematic shortfall - worst case ~0.15*v + latency travel ~= 6 m at
# approach speed - or pass/fail becomes a coin flip on the boundary.
STOP_AT_FLAG_TOLERANCE_M = 8.0

# Strict arrival criterion (docs/TESTING.md §3, rule 1): a scenario passes
# only if the car comes to rest AT the end flag - driving PAST it while
# moving is a failure ("drove past the end flag"). Sole exception: crossing
# at crawl speed counts as stopping at the flag - a real driver parking at
# a destination often rolls over the line at a few km/h before coming to
# rest, which is indistinguishable from stopping right at it.
FLAG_CRAWL_KMH = 5.0

# Parking-offset scenarios (docs/DRIVING_MANEUVERS.md §1 variant): the
# arrival at the flag is not enough - the car must also have PARKED, i.e.
# BOTH right-hand wheels end within this distance of the right kerb.
# Measured at the body flank (CAR_WIDTH/2) at both wheel stations: a
# perfectly parked car sits PARK_KERB_CLEARANCE_M (0.16 m) out, so 30 cm
# means "parked properly", not "touched the kerb".
KERB_PASS_MAX_M = 0.30
# Width of the parking avenue in src/test_maps.py (tile (4,1): two driving
# + one parking lane per side) - the right-kerb line for the check above is
# derived from it.
PARK_AVENUE_WIDTH_M = 19.4

# Running turn tests (docs/TESTING.md): the scenario drives THROUGH the
# corner - no stopping, no parking (parking is covered by the dedicated
# parking-offset scenarios). The car spawns in the LAST 20% of the start
# segment (progress TURN_SPAWN_PROGRESS), already rolling at
# RUNNING_START_KMH, and the test ends when it crosses the red end flag,
# which marks the FIRST 20% of the expected end segment. Pass criterion:
# the turn was executed correctly - stayed on the road and did not drive
# into the oncoming lane (lane guard).
TURN_SPAWN_PROGRESS = 0.8
END_FLAG_PROGRESS = 0.2
# Radius within which the car's centre must pass the red end flag for the
# scenario to count as "reached the flag". Must cover one polling step at
# max test speed (50 km/h @ ~20 Hz ≈ 0.7 m) plus the largest lane offset
# on a running-test road (kerb_offset of a 7 m road = 2.25 m), while
# staying far below any real miss distance.
FLAG_REACH_RADIUS_M = 3.0

# How far off the flag's own lane a car may be and still count as arriving AT
# the flag (binding decision 2026-10-25: the arrival event is the car's body
# crossing the pennant's line; the ONLY thing that has to keep a car on a
# different road from firing it is this lane band). Half a lane: a 2.6 m lane
# reaches 1.3 m either side of its centre, so 2 m covers 'our lane plus a small
# driving wobble' and stops well before the neighbour lane's centre.
FLAG_LANE_HALF_TOL_M = 2.0

# The parking scenario's finish tolerance, measured on the car's FRONT end
# against the pennant's foot (which the sim now places at the parked car's
# bumper line, SimEngine.cs): a real driver parks with the nose AT the mark,
# so tens of centimetres - not the 8 m of STOP_AT_FLAG_TOLERANCE_M, which was
# a braking-distance allowance for the car's CENTRE and would call a car parked
# most of a lane-length away a pass.
NOSE_TOUCH_M = 0.5
RUNNING_START_KMH = 50.0


# --- Colored console output (auto-disabled when stdout is not a TTY) ---

def _c(text: str, code: str) -> str:
    if not sys.stdout.isatty():
        return text
    return f"\033[{code}m{text}\033[0m"


def green(text: str) -> str:  return _c(text, "32")
def red(text: str) -> str:    return _c(text, "31")
def yellow(text: str) -> str: return _c(text, "33")
def cyan(text: str) -> str:   return _c(text, "36")
def dim(text: str) -> str:    return _c(text, "2")


def _poll_dt(state: dict, prev_sim, prev_wall: float, now_wall: float) -> float:
    """Seconds of SIM time between the previous /state read and this one.

    The teleport/jump bounds ask "could this speed have covered that
    distance in that time" - the distance is driven in SIM time, so the
    time must be SIM time too. Under --warp/--fast the sim advances more
    than wall time, and a wall-clock bound would call normal driving a
    jump. Wall time is only the fallback for streams without 'time'.
    """
    t = state.get('time')
    if isinstance(t, (int, float)) and isinstance(prev_sim, (int, float)):
        return max(t - prev_sim, 1e-6)
    return max(now_wall - prev_wall, 1e-6)


# --- Result persistence (tests/turning_results.json) ---

def _parse_test_spec(spec_str: str):
    """Parse a scenario selection string into (indices, names).

    Tokens are comma-separated: '3' (one number), '7-9' (inclusive range)
    or a start-point name (every scenario at that point)."""
    wanted_idx: set[int] = set()
    wanted_name: set[str] = set()
    for tok in spec_str.split(','):
        tok = tok.strip()
        if not tok:
            continue
        if '-' in tok and all(p.strip().isdigit() for p in tok.split('-', 1)):
            a, b = (int(p) for p in tok.split('-', 1))
            wanted_idx.update(range(a, b + 1))
        elif tok.isdigit():
            wanted_idx.add(int(tok))
        else:
            wanted_name.add(tok)
    return wanted_idx, wanted_name


def select_tests(tests: list, spec: str | None = None) -> list:
    """Pick which scenarios to run, keeping their ORIGINAL numbering.

    With `spec` given (used by the cockpit controller), it is parsed like
        3              one scenario
        3,7,12         several
        3-6            an inclusive range
        tjunction_from_top      every scenario at that start point
    Without it, the selection is read from the command line: --tests with
    the same syntax, or --failed for whatever failed on the last recorded
    run.

    Returns [(original_index, test), ...] so the printed "TEST 7/18" still
    identifies the same scenario however the suite is filtered - the point
    being to jump straight back to one that failed without renumbering it.
    """
    numbered = list(enumerate(tests, 1))

    if spec is not None:
        wanted_idx, wanted_name = _parse_test_spec(spec)
        picked = [(i, t) for i, t in numbered
                  if i in wanted_idx or t[0] in wanted_name]
        if not picked:
            print(f"test selection '{spec}' matched nothing. Available:")
            for i, t in numbered:
                print(f"   {i:>2}  {t[0]} {t[1]}")
            sys.exit(2)
        return picked

    if '--failed' in sys.argv:
        last = load_results().get('last', {})
        failed = {k for k, v in last.items() if not v.get('passed')}
        if not failed:
            print("--failed: nothing failed on the last run; nothing to do.")
            return []
        picked = [(i, t) for i, t in numbered
                  if f"{t[0]}|{t[1]}" in failed]
        print(f"--failed: re-running {len(picked)} previously failing "
              f"scenario(s): {', '.join(sorted(failed))}")
        return picked

    if '--tests' not in sys.argv:
        return numbered

    idx = sys.argv.index('--tests')
    if idx + 1 >= len(sys.argv):
        print("--tests needs a value, e.g. --tests 3,7-9,tjunction_from_top")
        sys.exit(2)

    wanted_idx, wanted_name = _parse_test_spec(sys.argv[idx + 1])

    picked = [(i, t) for i, t in numbered
              if i in wanted_idx or t[0] in wanted_name]
    if not picked:
        print(f"--tests '{sys.argv[idx + 1]}' matched nothing. Available:")
        for i, t in numbered:
            print(f"   {i:>2}  {t[0]} {t[1]}")
        sys.exit(2)
    return picked


def load_results() -> dict:
    """Load the persisted turn-test results (empty structure if missing/corrupt)."""
    try:
        with open(RESULTS_FILE) as f:
            data = json.load(f)
        if isinstance(data, dict) and isinstance(data.get("last"), dict):
            return data
    except (OSError, ValueError):
        pass
    return {"last": {}, "history": []}


def save_results(data: dict) -> None:
    """Persist results. Best-effort: a write failure must never kill the run."""
    data["updated"] = datetime.now().isoformat(timespec="seconds")
    data["history"] = data.get("history", [])[-HISTORY_LIMIT:]
    try:
        with open(RESULTS_FILE, "w") as f:
            json.dump(data, f, indent=2)
            f.write("\n")
    except OSError as e:
        print(yellow(f"⚠️  Could not save results to {RESULTS_FILE}: {e}"))


def record_result(data: dict, result: dict, test_no: int = 0) -> None:
    """Record one scenario result (updates 'last' + appends to 'history').

    `test_no` is the scenario's number in DETERMINISTIC_TESTS as the suite
    loop counts it. It is NOT derivable from the result dict: the pair
    (start_point, direction) is not unique (#3/#5 are both
    'tjunction_from_top'/'left', #21/#24 both 'hairpin_exit'/'straight'), and
    the crossing scenarios report direction='crossing' where the table says
    'straight'. Deriving it by matching gave label 3/27 twice and no 5/27,
    6/27 or 24/27 at all - and a verdict whose label differs from its focus
    note can never be joined to it in the reviewer
    (docs/TESTING.md "Camera focus")."""
    if not result.get("start_point"):
        return  # random-location runs have no stable scenario key
    key = f"{result['start_point']}|{result['direction']}"
    entry = {
        "passed": bool(result["passed"]),
        "reason": describe_failure(result),
        "timestamp": datetime.now().isoformat(timespec="seconds"),
        "final_segment": result.get("final_segment"),
        "expected_end_segment": result.get("expected_end_segment"),
    }
    # Stress-scenario extras: the per-phase jitter sum (Anzahl Ruckler
    # pro Szenario) and the worst single jump.
    for extra in ("jitters", "worst_jump_m"):
        if result.get(extra) is not None:
            entry[extra] = result[extra]
    # ...and told to the game, so the TAPE carries the verdict as well: a run
    # reviewed later (host long gone) must still be able to answer "did the suite
    # call this scenario green?" next to the frame under the cursor.
    try:
        num = test_no or next((i for i, t in enumerate(DETERMINISTIC_TESTS, 1)
                               if t[0] == result["start_point"]
                               and t[1] == result["direction"]), 0)
        requests.post(f"{API_URL}/test_result", json={
            "number": num,
            "label": f"{num}/{len(DETERMINISTIC_TESTS)}" if num else key,
            "scenario": result["start_point"],
            "passed": bool(result["passed"]),
            # describe_failure() answers None for a clean pass — the panel
            # shows the verdict, not a reason, in that case.
            "reason": (entry.get("reason") or "")[:160],
            # The SAME numbers the run's results table prints, from the same
            # dict — 'duration' is wall seconds, 'sim_duration_s' the sim window
            # it watched, and the crossing/off-road counts are the per-scenario
            # totals (docs/TESTING.md). One source, so the review panel can
            # never disagree with the log.
            "sim_s": result.get("sim_duration_s") or 0.0,
            "wall_s": result.get("duration") or 0.0,
            "crossings": result.get("centreline_crossings") or 0,
            "off_road": result.get("off_road_hits") or 0,
        }, timeout=3)
    except requests.exceptions.RequestException:
        pass        # cosmetic: a verdict still lands in the results file
    data["last"][key] = entry
    data.setdefault("history", []).append({"scenario": key, **entry})


def last_result_for(data: dict, start_point: str, direction: str):
    """Return the previous result entry for a scenario, or None."""
    if not start_point:
        return None
    return data["last"].get(f"{start_point}|{direction}")


# --- Human-readable failure reasons ---

def describe_failure(result: dict) -> str | None:
    """One-line human-readable reason for a failed result (None if it passed).

    This is the text shown under "Last run: No - <reason>" at the start of the
    next run of the same scenario, and after "FAIL: " when a test fails.
    """
    if result["passed"]:
        return None
    if result.get("aborted"):
        return "aborted by user (test jump from the cockpit)"
    if result.get("game_crashed"):
        return "game process ended mid-test (window closed or physics watchdog crash)"
    if result.get("teleport_detected"):
        return "teleported / unexpected jump detected"
    if result.get("instant_snap_detected"):
        return "instant heading snap (unrealistic rotation)"
    if result.get("expect_wrong_side") and not result.get("wrong_side_detected"):
        return ("expected wrong-side driving but none occurred - the car "
                "stayed in its own lane (negative detector test did not fire)")
    if result.get("wrong_side_detected") and not result.get("expect_wrong_side"):
        return "drove into the oncoming lane (wrong side of the centreline)"
    # off_road_detected already accounts for the validator log, measured as
    # a DELTA over the scenario. Re-testing the cumulative count here would
    # reintroduce the same latch.
    if result.get("off_road_detected"):
        return "cut the corner and drove off the road"
    if result.get("passed_flag"):
        return (f"drove past the end flag at "
                f"{result.get('passed_flag_speed_kmh', 0):.0f} km/h - the "
                f"scenario requires a stop AT the flag")
    if result.get("segment_changed") and not result.get("reached_expected_segment"):
        final = result.get("final_segment")
        expected = result.get("expected_end_segment")
        if final == expected:
            # On the right segment but the end flag was never reached -
            # calling that "the wrong route" is a lie (it cost real debugging
            # time to decode). Usually: approach too slow for the window, or
            # the stop landed just short of the flag tolerance.
            target_pct = 50 if result.get("kerb_check") \
                else result.get("end_flag_progress", END_FLAG_PROGRESS) * 100
            return (f"timed out on the correct segment {final} before "
                    f"reaching the end flag ({target_pct:.0f}% point)")
        return (f"took the wrong route (ended on segment {final}, "
                f"expected {expected})")
    if result.get("reached_expected_segment") and not result.get("stopped_at_end"):
        return "reached the destination but never came to a clean stop"
    if result.get("kerb_check") and result.get("reached_expected_segment") \
            and result.get("stopped_at_end"):
        gaps = result.get("kerb_gaps_m")
        if gaps is not None:
            return (f"parked too far from the kerb: right flank "
                    f"{gaps[1] * 100:.0f} cm front / {gaps[0] * 100:.0f} cm rear "
                    f"(limit {KERB_PASS_MAX_M * 100:.0f} cm)")
    return (f"timed out: never reached expected segment "
            f"{result.get('expected_end_segment')}")


# --- User-interruption handling (Ctrl-C / game window closed) ---

_INTERRUPTED = False


def _on_sigint(signum, frame):
    global _INTERRUPTED
    _INTERRUPTED = True
    print("\n" + yellow("⚠️  Ctrl-C received - finishing current scenario, then saving results..."))


def game_alive() -> bool:
    """True while the game's API still answers (window closed => not alive)."""
    try:
        requests.get(f"{API_URL}/health", timeout=1)
        return True
    except requests.exceptions.RequestException:
        return False


def interrupted() -> bool:
    """True if the user interrupted (Ctrl-C) or the game window was closed."""
    return _INTERRUPTED or not game_alive()

# Sequential, 1-based position of each named start point's map tile,
# counted from the TOP-LEFT of the minimap, left-to-right then
# top-to-bottom (1 = top-left, 2 = top row/second from left, etc.) -
# used only to show a short number in the HUD via POST /label while a
# test runs, purely a visual aid to see where on the map the current
# test is happening. The descriptive start-point names themselves are
# unaffected.
#
# NOTE: the minimap draws with north (small world y) at the BOTTOM and
# south (large world y) at the TOP (Renderer.draw_minimap flips y), so
# this numbering is the reverse of the tiles' internal world-grid row
# (see src/test_maps.py:build_basic_test_map's docstring) - world-grid
# row 0 appears at the bottom of the minimap (numbers 9-12) and
# world-grid row 2 at the top (numbers 1-4).
START_POINT_NUMBER = {
    'dead_end_approach': 1,
    'hairpin_entry': 2, 'hairpin_exit': 2,
    'sweeping_curve': 3, 'sweeping_curve_reverse': 3,
    'roundabout_from_north': 4, 'roundabout_from_east': 4,
    'roundabout_from_south': 4, 'roundabout_from_west': 4,
    'y_from_stem': 5, 'y_from_sw': 5, 'y_from_se': 5,
    'crossroads_from_north': 6, 'crossroads_from_south': 6,
    'crossroads_from_east': 6, 'crossroads_from_west': 6,
    'oneway_entry': 7, 'oneway_wrong_way': 7,
    'oneway_cross_from_north': 7, 'oneway_cross_from_south': 7,
    's_curve': 8, 's_curve_reverse': 8,
    'straight': 9, 'straight_reverse': 9,
    'corner_right_entry': 10, 'corner_right_exit': 10,
    'corner_left_entry': 11, 'corner_left_exit': 11,
    'tjunction_from_top': 12, 'tjunction_from_west': 12, 'tjunction_from_east': 12,
    'sliver_approach': 13, 'sliver_from_west': 13, 'sliver_from_east': 13,
    'park_6lane_left_lane': 14, 'park_6lane_right_lane': 14,
    'park_6lane_parking': 14,
    'mixed_from_west': 15, 'mixed_from_east': 16,
}


# The deterministic scenario table (see the long comment on it). Kept at
# module level so the cockpit controller (tools/controller.py) can import
# it to build its row of numbered test buttons without running the suite.
# Default monitor window: 30 s. Passing tests exit ON ARRIVAL, so this only
# bounds how long a FAILING run takes - but it must comfortably exceed the
# slowest legitimate arrival. Running turn tests end at the flag crossing
# (first 20% of the end segment - no stop), so arrivals are quick except
# on the roundabout: the ring's chord segments delay the route horizon and
# the car has to circle most of the ring before reaching the west exit,
# hence its longer window. The parking-offset scenarios keep the full
# parking approach (parking ramp + kerb drift + back-in + stop at the
# flag), which is slower - 45 s covers them.
# ---------------------------------------------------------------------------
# Per-scenario CAMERA FOCUS — the single source of truth (docs/TESTING.md
# "Camera focus for the obstruction scenarios"). What a scenario is ABOUT
# decides what the window shows while it runs, and only the scenario author
# knows that. Three kinds:
#
#   {"kind": "car"}                     → follow the PRIMARY car; fit the view to it
#   {"kind": "named", "name": …}        → follow that named car of the cast
#   {"kind": "loc", "x":…, "y":…, …}    → a PLACE: no car is followed at all
#
# A scenario that declares no focus gets "car". "loc" is what the crossing and
# the pass tests need: following one car among 50 shows less than watching the
# junction, and the pass of a parked car happens AT the obstruction, so a car
# that drives away would carry the camera out of the story (user decision
# 2026-10-24: if the test's focus is not a car, focus the LOCATION — never a
# car on top of it).
#
# The two location derivations are the map's own vocabulary, so they survive a
# rebuilt network: "node_prefix" averages the start points of every segment
# that LEAVES a node of that family (the crossing of a fig-8, the mouth of a
# T), "segment"+"t" is a fraction along a named segment (where the scenario
# parks or slows its car). Both resolve through GET /segment_names +
# /segment/{idx}, i.e. in metres.
#
# This table used to live in scripts/run_e2e.sh as two shell-embedded python
# dicts, which meant (a) the focus could only ever be handed to the client as
# ONE command-line argument for the whole window — so a full-suite run kept
# showing the first scenario's framing — and (b) the Simulationsreviewer, which
# has no runner, could not know it at all. It is scenario metadata now: the
# suite resolves it per test, announces it with POST /focus, the sim exports it
# in /state.focus and the tape records it, so the viewer follows it live and the
# reviewer replays it frame-accurately.
SCENARIO_FOCUS = {
    # The pair meets AT the node — the junction is the subject, not one car.
    'tjunction_from_top':  {"kind": "loc", "node_prefix": "tjunc_"},
    # Both crossings: 50 cars through one degree-4 box.
    'fig8_xing':           {"kind": "loc", "node_prefix": "xing_"},
    'fig8_xing_plain':     {"kind": "loc", "node_prefix": "xingp_"},
    # The pass happens at the parked car / at the crawler's start spot, on the
    # two-lane straight — a place, because the subject never moves (17) or
    # because the box must hold the whole squeeze (20).
    'park_block_overtake': {"kind": "loc", "segment": "straight_n\u2192straight_m", "t": 0.5},
    'slow_pass_priority':  {"kind": "loc", "segment": "straight_n\u2192straight_m", "t": 0.70},
    # A CAR is the subject, but NOT the primary one: the camera stays on the
    # obstruction / on the crawler the primary has to squeeze past.
    'park_block_priority': {"kind": "named", "name": "parked_car"},
    'slow_pass_overtake':  {"kind": "named", "name": "slow_car"},
}

# px/m the client holds at a declared focus. A PLACE gets the wide view (the
# whole crossing has to be in the box); a NAMED car the close one; the default
# "car" focus has no number here at all — the client fits the view to the car
# (MapRenderer.CarFocusViewM).
FOCUS_ZOOM_PXM = {"loc": 26.0, "named": 16.0}

# --- The cast's ROLES: one symbolic name per car --------------------------
# A scenario authors its cast, so it knows what every car IS. That role —
# not a uid, not a sprite colour — is what the camera (`POST /focus`), the
# car detail window (`#65 oncoming_car`) and the tape header (`names=`)
# address it by, and it travels WITH the spawn (`POST /teleport {"name": …}`)
# so the sim sets it before the car's first tick (docs/TESTING.md "Named cars").
#
# The extras name themselves inline in the scenario tuple (field 10, 'name'):
# they are staged per scenario and their role only exists in that cast.
# The PRIMARY car's role is the maneuver it performs, so it is keyed here by
# (start_point, direction) — that pair IS the maneuver, and the T-junction
# turns appear twice in the table (empty bar, bar with an oncoming car) while
# the car plays the same role in both. The NEGATIVE hairpin test is the one
# scenario where the same maneuver plays a different role; its expect_wrong_side
# flag says so (see run_deterministic_test).
#
# A scenario that names NOBODY is a big-cast run (the fig-8 tests: 51 cars
# around a loop, 50 through a crossing). No car of that fleet has a role, so
# they get the fallback `<vehicle_class>-<uid>` instead (see _cast_fallback_names).
PRIMARY_CAR_ROLE = {
    ('corner_right_entry', 'right'): 'right_turning_car',
    ('corner_left_entry', 'left'): 'left_turning_car',
    ('tjunction_from_top', 'left'): 'left_turning_car',
    ('tjunction_from_top', 'right'): 'right_turning_car',
    ('y_from_stem', 'left'): 'left_turning_car',      # the south-east fork
    ('y_from_stem', 'right'): 'right_turning_car',    # the south-west fork
    ('crossroads_from_north', 'left'): 'left_turning_car',
    ('crossroads_from_north', 'right'): 'right_turning_car',
    ('crossroads_from_north', 'straight'): 'straight_through_car',
    ('oneway_entry', 'straight'): 'one_way_entry_car',
    ('mixed_from_west', 'straight'): 'straight_through_car',
    ('mixed_from_east', 'straight'): 'straight_through_car',
    ('s_curve', 'straight'): 's_curve_car',
    ('hairpin_entry', 'straight'): 'hairpin_entry_car',
    ('hairpin_exit', 'straight'): 'hairpin_exit_car',  # NEGATIVE twin: overridden
    ('park_block_overtake', 'straight'): 'overtaking_car',
    ('park_block_priority', 'straight'): 'blocked_car',  # waits for the gap
    ('slow_pass_overtake', 'straight'): 'overtaking_car',
    ('slow_pass_priority', 'straight'): 'overtaking_car',  # after waiting
    ('roundabout_from_north', 'right'): 'roundabout_exit_car',
    ('park_6lane_right_lane', 'straight'): 'parking_car',
}

# sprite color -> vehicle class, mirroring Config.cs' VEHICLE_CLASS (the suite
# spawns by sprite color, the class is what the car's dynamics and footprint
# come from). Only the big-cast fallback naming needs it: `car-118`,
# `heavy_truck-124`. An unknown color is a car (Config.SpecForColor's fallback).
CAR_CLASS_OF_COLOR = {
    'blue': 'car', 'police': 'car', 'tan': 'car',
    'silver': 'compact', 'pickup': 'pickup',
    'tractor': 'truck', 'mixer': 'heavy_truck',
}

DETERMINISTIC_TESTS = [
    # --- Symbolic anchors -------------------------------------------------
    # expected_end_segment is a segment NAME ("startNode→endNode" - the
    # arrow character, see RoadSegment.Name and GET /segment_names), NOT a
    # positional index. run_deterministic_test resolves names to indices once
    # at startup and fails fast when a name is unknown, so adding or deleting
    # a track can never silently move what a scenario anchors to (the old
    # "+48 index shift" hazard). An index in a test is a coincidence, a name
    # is an intention.
    #
    # Tuple fields (see run_deterministic_test):
    #   0 start_point  1 direction  2 target km/h  3 expected end segment
    #   4 duration s   5 description 6 start_progress 7 kerb_check
    #   8 end_flag_progress  9 expect_wrong_side (NEGATIVE test)
    #  10 extra_cars: list of dicts {start_point|segment, progress, speed_mps,
    #      throttle, expect_end, name} - other cars in the same world. throttle=False
    #      leaves a car where it was put: that is how a PARKED car is staged.
    #      EVERY extra names its role ('name'); the primary car of the scenario
    #      gets its role from PRIMARY_CAR_ROLE above.
    # 90-degree corners (the classic reported bug)
    ('corner_right_entry', 'right', 50, 'cornerR_c→cornerR_w', 60.0,
     "Taking a sharp 90° right corner"),
    ('corner_left_entry', 'left', 50, 'cornerL_c→cornerL_e', 60.0,
     "Taking a sharp 90° left corner"),
    # T-junction (perpendicular 3-way)
    ('tjunction_from_top', 'left', 50, 'tjunc_center→tjunc_e', 60.0,
     "T-junction: turning left off the stem"),
    ('tjunction_from_top', 'right', 50, 'tjunc_center→tjunc_w', 60.0,
     "T-junction: turning right off the stem"),
    # SAME two maneuvers WITH ONCOMING TRAFFIC on the bar: a second car
    # drives the cross street directly against the turning car's path. The
    # pair must not touch at the node - the turn has to be taken with the
    # gap that is actually there, not by hoping the bar is empty.
    ('tjunction_from_top', 'left', 50, 'tjunc_center→tjunc_e', 60.0,
     "T-junction left turn WITH an oncoming car on the bar (must not collide "
     "at the node)",
     TURN_SPAWN_PROGRESS, False, END_FLAG_PROGRESS, False,
     [{'start_point': 'tjunction_from_east', 'progress': 0.5,
       'speed_mps': 50 / 3.6, 'throttle': True, 'name': 'oncoming_car'}]),
    ('tjunction_from_top', 'right', 50, 'tjunc_center→tjunc_w', 60.0,
     "T-junction right turn WITH an oncoming car on the bar (must not collide "
     "at the node)",
     TURN_SPAWN_PROGRESS, False, END_FLAG_PROGRESS, False,
     [{'start_point': 'tjunction_from_west', 'progress': 0.5,
       'speed_mps': 50 / 3.6, 'throttle': True, 'name': 'oncoming_car'}]),
    # Y-intersection (shallow ~40 degree diverging angles)
    # The stem runs NORTH->fork (y_stem 250,400 -> y_center 250,220), so the car
    # enters HEADING SOUTH: its left hand is EAST, its right hand is WEST. The
    # fork arms are named by compass quarter, not by turn - y_se (400,50) is
    # south-EAST and y_sw (100,50) is south-WEST. A left turn therefore ends on
    # y_center->y_se, a right turn on y_center->y_sw.
    ('y_from_stem', 'left', 50, 'y_center→y_se', 60.0,
     "Y-intersection: taking the left fork (the south-east arm)"),
    ('y_from_stem', 'right', 50, 'y_center→y_sw', 60.0,
     "Y-intersection: taking the right fork (the south-west arm)"),
    # 4-way crossroads
    ('crossroads_from_north', 'left', 50, 'cross_center→cross_e', 60.0,
     "4-way crossroads: turning left"),
    ('crossroads_from_north', 'right', 50, 'cross_w→cross_center', 60.0,
     "4-way crossroads: turning right"),
    ('crossroads_from_north', 'straight', 50, 'cross_center→cross_s', 60.0,
     "4-way crossroads: going straight through"),
    # One-way street (legal direction)
    ('oneway_entry', 'straight', 50, 'ow_center→ow_e', 60.0,
     "Entering a one-way street in the legal direction"),
    # Mixed one-way/two-way crossroads (tile (4,2)): W spoke is one-way
    # INTO the junction; entering from the west, the car must swing
    # slightly right across the junction into its own lane on the two-way
    # east side (the keep-centre-on-left rule does not apply on the
    # one-way approach - no oncoming traffic there).
    ('mixed_from_west', 'straight', 50, 'mixin_center→mixin_e', 60.0,
     "One-way into a two-way crossroads: straight through"),
    # Mixed one-way/two-way crossroads (tile (4,3)): W spoke is one-way
    # OUT of the junction; entering from the east (two-way), the car
    # crosses the junction and eases onto the narrow one-way exit.
    ('mixed_from_east', 'straight', 50, 'mixout_center→mixout_w', 60.0,
     "Two-way into a one-way exit: straight through"),
    # Simple curves (degree-2 nodes, no blinker needed). Under the running
    # protocol only the corner ENTRY is covered (last 20% of the approach
    # + first 20% of the target segment) - the long S-curve traversal the
    # old stop-at-50% protocol measured is out of scope now.
    ('s_curve', 'straight', 50, 's_p3→s_p4', 60.0,
     "Following a zig-zag S-curve road"),
    # Test 16 overrides: spawn at 50% of the approach segment AND finish
    # at the 50% point of the end segment (tuple fields:
    # start_progress=0.5, kerb_check=False, end_flag_progress=0.5).
    ('hairpin_entry', 'straight', 50, 'hair_corner→hair_b', 60.0,
     "Driving a hairpin bend (entry side)", 0.5, False, 0.5),
    # --- R32 obstruction tests (the two-lane straight road, tile (0,0)) ----
    # The road is 7 m = ONE driving lane per direction (two lanes in total),
    # now split at straight_m so arrival can be judged by segment. A car is
    # parked INSIDE the driving lane of the second stretch, so the lane is
    # blocked and the only way past is the other lane.
    ('park_block_overtake', 'straight', 50, 'straight_n→straight_m', 60.0,
     "A parked car blocks the lane - drive around it (R32 overtake: out into "
     "the free lane, past the obstruction, back to the right)",
     0.15, False, 0.85, False,
     [{'segment': 'straight_n→straight_m', 'progress': 0.5,
       'speed_mps': 0.0, 'throttle': False, 'park': True,
       'name': 'parked_car'}]),
    # Same road, same obstruction, but traffic comes from BOTH ends at once:
    # the car whose lane is clear may take the shared lane first, the one
    # that is blocked must wait for it - the single-file passage claim (R26)
    # decides, and the two must never touch.
    ('park_block_priority', 'straight', 50, 'straight_m→straight_s', 60.0,
     "Parked car in one lane + a car from the opposite end: the UNBLOCKED "
     "car gets the passage, the blocked one waits (R26/R32)",
     # The end flag sits at 85% of the WHOLE straight, not of one segment
     # (user decision 2026-10-21): straight_n (y=350) to straight_s (y=50) is
     # 300 m, so 85% is 255 m along - y=95, which is 70% of segment 1.
     # The primary therefore has to drive THROUGH the node straight_m (where
     # the parked car now stands) and stop in the far segment.
     0.15, False, 0.7, False,
     [{'segment': 'straight_n→straight_m', 'progress': 1.0,
       # 50% of the WHOLE straight, not of this segment: straight_n (y=350)
       # to straight_s (y=50) is 300 m, so the middle is the node straight_m
       # at y=200 - the junction between the two segments. The obstruction
       # now sits where the route hands over, so the pass has to work ACROSS
       # a segment change (user decision 2026-10-21).
       'speed_mps': 0.0, 'throttle': False, 'park': True,
       'name': 'parked_car'},
      {'start_point': 'straight_reverse', 'progress': 0.15,
       'speed_mps': 50 / 3.6, 'throttle': True,
       'expect_end': 'straight_n→straight_m', 'name': 'oncoming_car'}]),
    # A SLOW VEHICLE (10 km/h) in my lane, not a parked one: the obstruction
    # MOVES, so the pass window moves with it and the overtake has to be
    # re-evaluated instead of aimed at a fixed spot. R32 calls a crawler an
    # obstruction (a queue lead that merely happens to be at 0 it must not
    # pass); R36 keeps the borrow of the oncoming lane at the minimum.
    ('slow_pass_overtake', 'straight', 50, 'straight_n→straight_m', 60.0,
     "A slow (10 km/h) car ahead at 35% - overtake it and come back "
     "(R32 pass of a crawler, R36 minimal borrow)",
     0.15, False, 0.85, False,
     [{'segment': 'straight_n→straight_m', 'progress': 0.35,
       'speed_mps': 10 / 3.6, 'throttle': True, 'cruise_kmh': 10,
       'name': 'slow_car'}]),
    # The SAME crawler with traffic coming from the OTHER end. The shared lane
    # is a single-file resource, so the overtaking car may NOT enter it while
    # an oncoming car owns it. What a driver does - and what this scenario
    # expects (user decision 2026-10-21): brake, CRAWL behind the slow car,
    # WAIT for the oncoming car to clear the pinch, and only THEN overtake.
    # Not: stop and stay stopped, not: dive past the crawler head-on.
    ('slow_pass_priority', 'straight', 50, 'straight_n→straight_m', 60.0,
     "Slow car ahead + oncoming traffic: brake, crawl behind it, wait, then "
     "overtake (R26 claim, R32, R36)",
     0.15, False, 0.85, False,
     [{'segment': 'straight_n→straight_m', 'progress': 0.70,
       # 35% of the WHOLE straight, not of this segment: straight_n (y=350)
       # to straight_s (y=50) is 300 m, so 35% is 105 m along - y=245, which
       # is 70% of segment 0 (user decision 2026-10-21).
       'speed_mps': 10 / 3.6, 'throttle': True, 'cruise_kmh': 10,
       'name': 'slow_car'},
      {'start_point': 'straight_reverse', 'progress': 0.15,
       'speed_mps': 50 / 3.6, 'throttle': True, 'name': 'oncoming_car'}]),
    # Hairpin, entered from the opposite end (reverse direction)
    ('hairpin_exit', 'straight', 50, 'hair_a→hair_corner', 60.0,
     "Driving a hairpin bend (exit side)", 0.5, False, 0.5),
    # Roundabout (one-way ring, 4 two-way spokes). 'straight' (or 'left')
    # at the entry just merges onto the ring and keeps circling it FOREVER
    # - a one-way loop has no "next different segment" to naturally stop at
    # unless the car actually exits onto a spoke. 'right' does that here
    # (verified: exits west, the rb_west_far spoke) - a real, completed
    # roundabout maneuver instead of an endless circle.
    # The ring is COUNTER-CLOCKWISE (correct for Germany / right-hand
    # traffic: the island stays on your left). Entering from the north and
    # going counter-clockwise, the first exit encountered is WEST.
    # Monitoring keeps running THROUGH the intermediate ring segments
    # instead of stopping at the first one, since only the west exit counts
    # as arrival. Takes longer than a normal turn (~25 s to go most of the
    # way around before exiting), hence the longer duration override.
    ('roundabout_from_north', 'right', 40, 'rb_west_far→rb_r16', 60.0,
     "Circling the roundabout and taking the first exit (west)"),
    # Park from different lateral start positions (docs/DRIVING_MANEUVERS.md
    # §1 variant). Straight parking avenue (tile (4,1)): two-way, 19.4 m
    # = per side two 3.5 m driving lanes + a 2.7 m parking lane at the kerb
    # (solid centreline, painted P marks). The car starts at 15% of the
    # segment and must park at the flag (90%) with BOTH right-hand wheels
    # within 30 cm of the right kerb (the extra tuple fields:
    # start_progress, kerb_check) - i.e. INSIDE the parking lane.
    # (2026-10-19, user decision: the two other lateral starts - from the
    # overtaking lane and from inside the parking lane - are out of the
    # suite; the plain back-in park from the normal lane stays.)
    ('park_6lane_right_lane', 'straight', 80, 'pkw_n→pkw_s', 45.0,
     "Back-in park from the right (normal) driving lane", 0.15, True),
    # NEGATIVE TEST (wrong-side detector) - runs LAST on purpose: the old
    # test-14 config. Spawn at 80% of the hairpin approach, i.e. right at the
    # hairpin fillet, already rolling at 50 km/h. The car cannot make the
    # ~166-degree turn in time and slides across the centreline into the
    # oncoming lane at the tip of the V. This scenario is EXPECTED to drive
    # wrong-side: it PASSES when the suite detects the violation (live
    # 'wrong_side' state or the lane-guard frame counter) and FAILS if the
    # car somehow stays in its own lane - it proves the detection works
    # instead of testing driving quality.
    ('hairpin_exit', 'straight', 50, 'hair_a→hair_corner', 60.0,
     "NEGATIVE: spawn too close to the hairpin corner at 50 km/h - must "
     "slide into the oncoming lane and trigger the wrong-side detection",
     0.8, False, 0.5, True),
    # MULTI-CAR STRESS (docs/TESTING.md 'fig8_stress'): N cars drive the figure-8
    # loop at once for 30 s (default phase: 150 cars - realtime-safe). No
    # flags - the metric is how often a car gets displaced jerkily (a
    # "ruckler"): per poll, any car that moves more than the suite's
    # teleport/jump bound max(speed, 50 km/h)*dt*1.5 + 1 m counts one
    # jitter; the SUM over all cars is stored per phase in the results file
    # as "jitters" (Anzahl Ruckler pro Szenario). Pass = all phases ran
    # without a game crash and without off-road/wrong-side - the jitter
    # count itself is informational: it shows at how many cars the sim
    # starts stuttering. Dispatched by start_point name, not through
    # monitor_turn (see run_multi_car_stress_test).
    ('fig8_stress', 'straight', 0, None, 170.0,
     "Multi-car stress: 51 cars on the figure-8 for 30 s of sim time "
     "(drivable density on the 723 m loop, odd count - see STRESS_PHASE_CARS; "
     "--stress-cars N overrides)"),
    # RIGHT-OF-WAY CROSSING STRESS: 50 cars (25 per direction, interleaved)
    # on the basic map's real degree-4 figure-8 crossing, accelerator held,
    # run for up to CROSSING_MAX_SIM_SECONDS (60 s, user decision 2026-10-19:
    # "no crash within one minute" = success) of sim time or until the FIRST
    # crash (two cars pinned in contact). Two variants: the signed crossing
    # (tile (2,3), Vorfahrt signs) and the un-signed crossing (tile (3,3),
    # right-before-left). Dispatched by start_point name, not through
    # monitor_turn.
    ('fig8_xing', 'straight', 0, None, 60.0,
     "Right-of-way crossing (WITH signs): 50 cars, 25/direction, 60 s or "
     "first crash"),
    ('fig8_xing_plain', 'straight', 0, None, 60.0,
     "Right-of-way crossing (NO signs, right-before-left): 50 cars, "
     "25/direction, 60 s or first crash"),
]

# Scenarios the NUMBERED suite skips (start_point -> reason). The raw --only
# path still runs them - that is the debug door while a scenario is under
# review. (2026-09-10, user decision: the fig8_stress 51-car spawn placement
# is being reworked - the phase fails on off-road/wrong-side before any
# crash, so it is out of the green baseline for now.)
SKIPPED_TESTS = {
    'fig8_stress': ("51-car spawn placement under review - the phase fails "
                    "on off-road/wrong-side before any crash; "
                    "run it raw with --only fig8_stress ... while debugging"),
}


class TurnTester:
    """Automated turn tester with detailed violation reporting."""
    
    def __init__(self):
        self.test_results: list = []
        # The scenario the suite loop is running RIGHT NOW, 1-based (0 =
        # outside a numbered run). record_result's label comes from here
        # because (start_point, direction) is not unique - see there.
        self.test_no = 0
        self._time_warp = None   # announced by /state, fetched once
        # One persistent (keep-alive) connection for the stress scenario.
        # Flask serves each request on its own thread, so back-to-back
        # POSTs over SEPARATE connections can be processed OUT OF ORDER -
        # a phase's trailing /cars clear once landed mid-spawn of the next
        # phase and wiped freshly spawned cars. Keep-alive on ONE
        # connection makes the server handle requests strictly in arrival
        # order.
        self._api_session = requests.Session()
    
    def health_check(self) -> bool:
        """Verify API is available."""
        try:
            response = requests.get(f"{API_URL}/health", timeout=1)
            data = response.json()
            print("✅ API health check passed")
            return True
        except requests.exceptions.RequestException as e:
            print(f"❌ API not available: {e}")
            print("\n🚨 Start the game first:")
            print("   python -m src.main --api\n")
            return False
    
    def reset_controls(self):
        """Reset all control inputs (no-op if the game is no longer reachable)."""
        try:
            requests.post(f"{API_URL}/reset")
        except requests.exceptions.RequestException:
            pass
    
    def get_state(self):
        """Get current game state."""
        response = requests.get(f"{API_URL}/state")
        return response.json()
    
    def get_segment_geometry(self, idx: int) -> dict | None:
        """One road segment's geometry, in METRES (0-based).

        length and width were always metres; the coordinates now are too.
        """
        try:
            r = requests.get(f"{API_URL}/segment/{idx}", timeout=2)
            if r.ok:
                return r.json()
        except requests.exceptions.RequestException:
            pass
        return None
    
    def send_control(self, **kwargs):
        """Send control inputs."""
        requests.post(f"{API_URL}/control", json=kwargs)
    
    def _wait_for_new_car(self, old_uid):
        """Wait until the game has processed a teleport (a car with a NEW uid).

        has_car alone can't confirm this: it is already True while the OLD
        car still exists, so reading state right after /teleport used to
        return the PREVIOUS car's segment/position as this scenario's
        "initial" values. When that stale segment equals the expected end
        segment, monitor_turn's `segment != initial_segment` gate skips the
        entire arrival logic on the target segment and the test times out
        with a confusing "wrong route (ended on X, expected X)" - this is
        what flaked corner_left after an aborted run and broke oneway/
        hairpin when the previous scenario parked on the target segment.
        Car.uid is monotonic per instance, so a changed uid is an
        unambiguous teleport ack (polling instead of a blind sleep -
        usually lands in ~50 ms).
        """
        deadline = time.time() + 2.0
        while time.time() < deadline:
            try:
                st = self.get_state()
            except requests.exceptions.RequestException:
                return   # game unreachable - the caller deals with it
            if st.get('has_car') and st.get('car_uid') != old_uid:
                return
            self._psleep(0.05)

    def create_car_at_random(self):
        """Replace car with a fresh one at random location."""
        try:
            old_uid = self.get_state().get('car_uid')
        except requests.exceptions.RequestException:
            old_uid = None
        # color pinned to the sedan class: dynamics are per vehicle class
        # (Config.VEHICLE_CLASS_SPECS) and every baseline in this suite was
        # calibrated with sedan numbers.
        requests.post(f"{API_URL}/teleport", json={'random': True, 'color': 'blue'})
        self._wait_for_new_car(old_uid)

    def create_car_at_start_point(self, name: str, progress: float = 0.5,
                                  speed_mps: float | None = None,
                                  color: str = "blue", role: str | None = None):
        """Replace car with a fresh one at named start point.

        progress: fraction along the start segment (from the node). The
        suite standard is TURN_SPAWN_PROGRESS - running turn tests use
        only the LAST 20% of the start segment (see docs/TESTING.md).
        speed_mps: optional rolling start in m/s (running turn tests spawn
        already moving; parking tests start from rest).
        color: pinned to the sedan class by default on purpose - dynamics
        are per vehicle class (Config.VEHICLE_CLASS_SPECS) and every
        baseline in this suite was calibrated with sedan numbers; without
        an explicit color the uid-based palette cycle would hand out truck
        classes (80 km/h top, 1 m/s^2) to later scenarios.
        role: the car's ROLE in this scenario ('left_turning_car', …) - sent
        WITH the spawn, so the label exists before the car's first tick (see
        PRIMARY_CAR_ROLE and docs/TESTING.md "Named cars").
        """
        try:
            old_uid = self.get_state().get('car_uid')
        except requests.exceptions.RequestException:
            old_uid = None
        payload = {'start_point': name, 'progress': progress, 'color': color}
        if speed_mps is not None:
            payload['speed'] = speed_mps
        if role:
            payload['name'] = role
        requests.post(f"{API_URL}/teleport", json=payload)
        # Wait until the game loop has actually processed the teleport and
        # the state reflects the NEW car (see _wait_for_new_car).
        self._wait_for_new_car(old_uid)
    
    def set_hud_label(self, text: str | None):
        """Show (or clear) a short text label in the game's HUD."""
        requests.post(f"{API_URL}/label", json={'text': text})

    # --- Camera focus of THIS scenario (SCENARIO_FOCUS, docs/TESTING.md) ----
    def resolve_focus(self, key: str, seg_names: dict | None = None) -> dict:
        """The scenario's focus as CONCRETE numbers (metres, px/m).

        A declaration says WHAT the camera should hold on; only the loaded map
        knows where that is. Resolution goes through the same symbolic anchors
        the scenarios address their routes by (GET /segment_names,
        GET /segment/{idx}) — so a rebuilt network moves the focus with the
        road it names instead of silently pointing at grass."""
        spec = dict(SCENARIO_FOCUS.get(key) or {"kind": "car"})
        kind = spec.get("kind", "car")
        out = {"kind": kind, "scenario": key,
               "zoom": spec.get("zoom", FOCUS_ZOOM_PXM.get(kind))}
        if kind == "named":
            out["name"] = spec.get("name", "")
            return out
        if kind != "loc":
            out.pop("zoom", None)     # "car": the client fits the view itself
            return out
        seg_names = seg_names if seg_names is not None else self.segment_names()
        try:
            if spec.get("segment"):
                idx = seg_names.get(spec["segment"])
                if idx is None:
                    raise KeyError(spec["segment"])
                sg = requests.get(f"{API_URL}/segment/{idx}", timeout=3).json()
                t = float(spec.get("t", 0.5))
                out["x"] = round(sg["x1"] + (sg["x2"] - sg["x1"]) * t, 2)
                out["y"] = round(sg["y1"] + (sg["y2"] - sg["y1"]) * t, 2)
            elif spec.get("node_prefix"):
                # The mean of every segment that LEAVES a node of that family:
                # for a fig-8 or a T that is the crossing / mouth itself.
                pre = spec["node_prefix"]
                xs = ys = n = 0.0
                for i in range(len(seg_names) + 1024):
                    try:
                        r = requests.get(f"{API_URL}/segment/{i}", timeout=3)
                        if r.status_code != 200:
                            break
                        sg = r.json()
                    except (requests.exceptions.RequestException, ValueError):
                        break
                    if str(sg.get("start_node", "")).startswith(pre):
                        xs += sg["x1"]; ys += sg["y1"]; n += 1
                if n:
                    out["x"] = round(xs / n, 2); out["y"] = round(ys / n, 2)
        except (requests.exceptions.RequestException, KeyError, TypeError, ValueError):
            pass
        if "x" not in out:
            # An unresolvable place is not a reason to hijack the camera onto a
            # car — fall back to the scenario's own start point, which IS a
            # place, and say so in the log.
            out["kind"] = "car" if not spec.get("node_prefix") and not spec.get("segment") else "loc"
            print(f"   ⚠ camera focus for '{key}': the road/prefix it names "
                  f"could not be resolved against this map (no host, or it is "
                  f"not on it) — falling back to following the primary car")
            out["kind"] = "car"
            out.pop("zoom", None)
        return out

    def segment_names(self) -> dict:
        try:
            return requests.get(f"{API_URL}/segment_names", timeout=3).json() or {}
        except (requests.exceptions.RequestException, ValueError):
            return {}

    def announce_focus(self, key: str, label: str | None = None,
                       seg_names: dict | None = None,
                       desc: str | None = None,
                       name_uid: int | None = None) -> dict:
        """Tell the game (and thereby every client + the tape) what THIS test is
        about to look at. One POST per scenario, at its start: the viewer
        re-aims when it sees it, the recorder stamps the substep so the
        reviewer can replay the same framing frame-accurately."""
        f = self.resolve_focus(key, seg_names)
        if label:
            f["label"] = label
        # The scenario's own sentence, so a review window can say WHAT is being
        # watched and not only WHERE: "Parked car in one lane + a car from the
        # opposite end: the UNBLOCKED car gets the passage…".
        if desc:
            f["desc"] = desc
        # The PRIMARY car's uid, for whoever cannot ask (the reviewer reads a
        # file, not a live /state). "the primary car" is a live binding in the
        # viewer and a recorded fact on the tape — without it the review client
        # could not tell the followed car from the 49 others.
        if name_uid:
            # WHICH car the role name belongs to. The scenario stages its cast
            # AFTER the first announcement, so this arrives as a refinement of
            # the same label (the tape replaces a re-announcement instead of
            # stacking a second boundary): without it a replay has to look the
            # name up, and roles are reused from test to test — '19/27' resolved
            # to a slow_car from a later scenario and followed nothing.
            f["name_uid"] = int(name_uid)
        st = self.get_state()
        if st and st.get("car_uid") is not None:
            f["uid"] = st["car_uid"]
        try:
            requests.post(f"{API_URL}/focus", json=f, timeout=3)
        except requests.exceptions.RequestException:
            pass        # cosmetic: a suite must never fail over a camera
        return f

    # --- Multi-car stress scenario (fig8_stress, docs/TESTING.md) ---
    # Default phase is 51 cars: the figure-8 loop is only 723 m, and with
    # car-car collisions enabled (unlike the original pass-through design)
    # more than ~50 cars on it is a physical standstill - 150 cars meant
    # 4.8 m spacing for 4.4 m cars, i.e. an instant pile-up.
    # The count is ODD on purpose: the figure-8 crosses itself at grade
    # (one node visited twice per lap), and an EVEN uniform fleet always
    # has a diametrically opposite pair that reaches that node
    # simultaneously - a guaranteed head-on on the first pass. With an odd
    # count the closest pair is offset by half a spacing (~7 m / 0.5 s at
    # this count), so the fleet drives the full window; the close pass
    # still exercises the crossing yield logic. Higher counts
    # stay available via --stress-cars for real-time performance probing
    # (capacity sweep 2026-09-05 - C# ceiling ~170, Python ~90; anything
    # above 200 runs in slow motion and just stretches wall time; expect
    # the fleet to jam/collide at those counts - that is the geometry, not
    # a placement bug). Each phase drives the figure-8 loop for
    # STRESS_PHASE_SECONDS of SIM time. Cars pass through each other (no
    # car-car collisions), so any count is legal - the metric is how often
    # a car gets displaced JERKILY: per poll, movement beyond the suite's
    # teleport/jump bound max(speed, 50 km/h)*dt*1.5 + 1 m counts one
    # jitter ("Ruckler"). The sum over all cars is stored per phase in
    # turning_results.json as "jitters" - it shows at how many cars the
    # sim starts stuttering.
    # At high car counts the sim may fall behind wall clock (its
    # accumulator caps substeps -> slow motion, never a leap); the phase
    # duration is therefore measured in SIM time so every phase covers the
    # same driving distance no matter how slow the wall clock gets.
    # Realtime ceiling on this machine is ~200 cars (0.83x); 150 keeps a
    # safety margin (measured 1.00x). Higher counts via --stress-cars.
    STRESS_PHASE_CARS = (51,)
    STRESS_PHASE_SECONDS = 30.0
    STRESS_POLL_S = 0.1
    # Positional indices into network.segments - fragile if the map
    # changes (same NOTE as the scenario table): fig8 occupies 104..151.
    FIG8_FIRST_SEGMENT = 104
    FIG8_N_SEGMENTS = 48
    STRESS_SPAWN_KMH = RUNNING_START_KMH   # rolling start, then coast

    def _clear_all_cars(self) -> bool:
        """POST /cars clear and WAIT until the map is actually empty.

        Returns False on timeout. Uses the keep-alive session so the clear
        can't be reordered against other in-flight requests (see
        __init__). Transient /state timeouts under load are retried; only
        a sustained unresponsive sim (~15 s) counts as failure.
        """
        try:
            self._api_session.post(f"{API_URL}/cars",
                                   json={'action': 'clear'}, timeout=5)
        except requests.exceptions.RequestException:
            return False
        deadline = time.time() + 10.0
        bad_streak = 0
        while time.time() < deadline:
            try:
                st = self._api_session.get(f"{API_URL}/state",
                                           timeout=2).json()
                bad_streak = 0
            except (requests.exceptions.RequestException, ValueError):
                bad_streak += 1
                if bad_streak >= 5:      # ~10 s of no answer -> give up
                    return False
                self._psleep(0.5)
                continue
            if len(st.get('cars', [])) == 0:
                return True
            self._psleep(0.05)
        return False

    def _cast_fallback_names(self) -> int:
        """Name every still-unnamed car of a big cast `<class>-<uid>`.

        The fig-8 scenarios stage 51/150 cars around a loop or 50 through a
        crossing: no car of that fleet has a ROLE - "the oncoming one" is not
        a fact about one of 50 cars, it is true of 25 of them. They still must
        not read `no-name` in a window, so they get the fallback the operator
        asked for: the car's vehicle class plus its uid (`car-118`,
        `heavy_truck-124`) - unique, and it says what kind of body made a
        crash or a squeeze, which is what those runs measure.

        A spawn name cannot carry it (the uid only exists once the sim
        processed the teleport), so this goes through POST /label with an
        explicit uid, sent right after the spawn poll and BEFORE the throttle -
        the tape header keeps the FIRST name a car ever gets, so the cast is
        named from its first moving frame. Cars that already carry a role are
        left alone. Returns how many cars were named.
        """
        try:
            cars = self._api_session.get(f"{API_URL}/state", timeout=5).json()
        except requests.exceptions.RequestException:
            return 0
        named = 0
        for c in cars.get('cars', []):
            if c.get('hud_label'):
                continue          # already a role from the spawn
            uid = c.get('car_uid')
            cls = CAR_CLASS_OF_COLOR.get(c.get('color') or '', 'car')
            try:
                self._api_session.post(f"{API_URL}/label", timeout=5,
                                       json={'uid': uid, 'text': f"{cls}-{uid}"})
                named += 1
            except requests.exceptions.RequestException:
                pass
        return named

    def _stress_scenario_result(self, phases: dict, overall_passed: bool,
                                crashed: bool, aborted: bool) -> dict:
        """The scenario-level result dict (print_summary's shape)."""
        return {
            'start_point': 'fig8_stress',
            'direction': 'stress',
            'passed': overall_passed and not aborted,
            'aborted': aborted,
            'off_road_detected': any(p['off_road_hits'] > 0
                                     for p in phases.values()),
            'instant_snap_detected': False,
            'teleport_detected': False,
            'game_crashed': crashed,
            # Not a failure category here (see the phase pass criteria):
            # the per-phase counts stay in 'stress_phases' + the results
            # file, print_summary just must not count them as failures.
            'wrong_side_detected': False,
            'segment_changed': True,
            'reached_expected_segment': True,   # no end flag in this test
            'expect_wrong_side': False,
            'final_segment': None,
            'expected_end_segment': None,
            'stress_phases': phases,
        }

    @staticmethod
    def _stress_phases_from_argv() -> "tuple | None":
        """Optional CLI override of the stress phases (like --tests):

            --stress-cars 576          only the 576-car phase
            --stress-cars 288,576      those two

        No flag -> None = run all STRESS_PHASE_CARS. Read from sys.argv
        here (not in main()) so every entry point that reaches this runner
        - CLI suite, /run_test subprocess, `--tests fig8_stress` - honours
        it without threading a parameter through.
        """
        if '--stress-cars' not in sys.argv:
            return None
        idx = sys.argv.index('--stress-cars')
        raw = sys.argv[idx + 1] if idx + 1 < len(sys.argv) else ''
        try:
            vals = sorted({int(p) for p in raw.split(',') if p.strip()})
        except ValueError:
            vals = []
        if not vals or any(v < 1 for v in vals):
            print("--stress-cars needs a comma list of positive counts, "
                  "e.g. --stress-cars 576 or --stress-cars 288,576")
            sys.exit(2)
        return tuple(vals)

    def _arc_uniform_spawns(self, segments: list, n_cars: int,
                            offset: float = 0.0) -> list:
        """(segment idx, progress) per car, evenly spaced BY ARC LENGTH
        around a closed loop.

        The old quantization (round(i * N_SEGS / n) + progress 0.5) put
        several cars at the SAME segment midpoint - 150 cars on 48
        segments meant 3 cars per spot with identical pose, which crashed
        on the first sim step. Arc-length placement gives every car a
        unique spot (spacing = perimeter / n_cars)."""
        perimeter = sum(s['length'] for s in segments)
        spawns = []
        for k in range(n_cars):
            target = (offset + (k + 0.5) * perimeter / n_cars) % perimeter
            acc = 0.0
            for s in segments:
                if acc + s['length'] >= target:
                    p = (target - acc) / s['length']
                    # never ON a node (segment hand-off territory)
                    spawns.append((s['idx'], min(max(p, 0.02), 0.98)))
                    break
                acc += s['length']
        return spawns

    def _crossing_safe_offset(self, segments: list, n_cars: int) -> float:
        """Spawn phase offset (arc length) so no car starts within half a
        spacing of a self-crossing node.

        On a figure-8 the crossing node is visited twice per lap, and a
        uniform fleet always has a pair of cars whose node arrivals are
        separated by 2 x (distance of the nearest car to the node) / v.
        With an unlucky offset (e.g. the loop's arc origin next to the
        node) that pair is born at the node and crashes at t=0. Maximising
        the nearest-car distance (grid search) bounds the pair arrival
        difference to >= one full car spacing (~1 s at spawn speed)."""
        perimeter = sum(s['length'] for s in segments)
        spacing = perimeter / n_cars
        # Self-crossing node(s): touched by 4 segments, 2 of which END
        # there - their cumulative end positions are the visit arcs.
        visits = []
        acc = 0.0
        end_counts: dict = {}
        for s in segments:
            end_counts[s['end_node']] = end_counts.get(s['end_node'], 0) + 1
        acc = 0.0
        for s in segments:
            acc += s['length']
            if end_counts.get(s['end_node'], 0) >= 2:
                visits.append(acc % perimeter)
        if not visits:
            return 0.0   # no self-crossing - any offset is safe
        best_d, best_off = -1.0, 0.0
        for gi in range(200):
            off = gi * perimeter / 200
            min_d = min(
                min(min(abs(v - c), perimeter - abs(v - c))
                    for v in visits
                    for c in ((off + (k + 0.5) * spacing) % perimeter
                              for k in range(n_cars)))
                for _ in [0])
            if min_d > best_d:
                best_d, best_off = min_d, off
        return best_off

    def _time_warp_announced(self) -> float:
        """The host's pacing switch as announced by /state (1.0 = real
        time, 0.0 = --fast/unbounded, absent = an older host: 1.0)."""
        try:
            st = self._api_session.get(f"{API_URL}/state", timeout=2).json()
        except (requests.exceptions.RequestException, ValueError):
            return 1.0
        w = st.get('time_warp')
        return float(w) if isinstance(w, (int, float)) else 1.0

    def _time_warp_cached(self) -> float:
        """The announced warp, fetched once (0.0 = --fast/unbounded)."""
        if self._time_warp is None:
            self._time_warp = self._time_warp_announced()
        return self._time_warp

    def _psleep(self, sim_s: float) -> None:
        """Sleep between two looks at the sim.

        The monitors watch the sim at a fixed SIM-time resolution (sim_s of
        driving per look). When the host runs warped (--warp N), that
        resolution has to come from more looks per wall second - with
        wall-clock sleeps the car covers metres between two polls and the
        stop/control commands arrive far too late. --fast announces 0
        (unbounded): then poll at the transport floor and accept that the
        resolution is what it is.
        """
        w = self._time_warp_cached()
        time.sleep(0.005 if w <= 0 else max(sim_s / w, 0.005))

    def run_multi_car_stress_test(self, results: dict | None = None,
                                  abort_check: "callable | None" = None,
                                  label: str | None = None,
                                  phases: "tuple | None" = None) -> dict:
        """Run the multi-car figure-8 stress scenario.

        Returns a result dict in monitor_turn's shape (so print_summary
        keeps working) with the per-phase breakdown in 'stress_phases'.
        Each phase is ALSO recorded in the results file under its own key
        (fig8_stress|2cars, ...), carrying that phase's jitter sum.

        phases: which car counts to run. None = the --stress-cars CLI
        override if present, else all STRESS_PHASE_CARS.
        """
        if results is None:
            results = load_results()
        if phases is None:
            phases = self._stress_phases_from_argv() or self.STRESS_PHASE_CARS
        phase_results: dict = {}
        overall_passed = True
        crashed = False

        for n_cars in phases:
            key = f"fig8_stress|{n_cars}cars"
            print(f"\n{'-'*60}")
            print(f"STRESS PHASE: {n_cars} cars on the figure-8 "
                  f"for {self.STRESS_PHASE_SECONDS:.0f} s of sim time")
            print(f"{'-'*60}")

            # Defensively UNFREEZE first: a previous scenario (e.g. a
            # crossing stress crash) may have left the sim frozen, and a
            # frozen sim never processes the queued teleports.
            try:
                self._api_session.post(f"{API_URL}/freeze", timeout=5,
                                       json={'frozen': False})
            except requests.exceptions.RequestException:
                pass

            # 1. Clean slate (also drops any car a previous scenario left).
            #    Waits until the map is REALLY empty, so no stale clear can
            #    still be in flight when we spawn.
            if not self._clear_all_cars():
                print("   ❌ Could not clear the map - phase fails")
                return self._stress_scenario_result(
                    phase_results, overall_passed=False, crashed=True,
                    aborted=False)

            # 2. Spawn n_cars evenly around the loop (rolling start, no
            #    throttle afterwards - they coast at ~RUNNING_START_KMH).
            #    Arc-length uniform (see _arc_uniform_spawns): the old
            #    round(i * 48 / n) + progress 0.5 stacked 3+ cars per
            #    segment midpoint, and they crashed on the first step.
            loop = []
            for i in range(self.FIG8_FIRST_SEGMENT,
                           self.FIG8_FIRST_SEGMENT + self.FIG8_N_SEGMENTS):
                try:
                    s = self._api_session.get(f"{API_URL}/segment/{i}",
                                              timeout=5).json()
                except requests.exceptions.RequestException:
                    s = None
                if s is None or s.get('length') is None:
                    print(f"   ❌ /segment/{i} unavailable - phase fails")
                    return self._stress_scenario_result(
                        phase_results, overall_passed=False, crashed=True,
                        aborted=False)
                loop.append(s)

            spawn_speed_mps = self.STRESS_SPAWN_KMH / 3.6
            for seg, prog in self._arc_uniform_spawns(loop, n_cars):
                # Sedan class for every car: mixed classes would change the
                # calibrated capacity baselines (see create_car_at_start_point).
                self._api_session.post(f"{API_URL}/teleport", json={
                    'segment': seg, 'progress': prog,
                    'speed': spawn_speed_mps, 'add': True, 'color': 'blue'}, timeout=5)
            # Wait until all cars actually exist (queued teleports land
            # one per sim frame - ~17 ms each). Proportional budget: at
            # hundreds of cars a fixed 15 s would time out spuriously if
            # the tick degrades under load.
            deadline = time.time() + max(15.0, n_cars * 0.03)
            uids: list[int] = []
            bad_streak = 0
            while time.time() < deadline:
                try:
                    st = self._api_session.get(f"{API_URL}/state",
                                               timeout=2).json()
                    bad_streak = 0
                except (requests.exceptions.RequestException, ValueError):
                    # A single slow /state under load is NOT a crash -
                    # retry until the budget runs out (sustained silence
                    # then just means "not all spawned" -> phase fails).
                    bad_streak += 1
                    if bad_streak >= 5:      # ~10 s of no answer
                        break
                    self._psleep(0.5)
                    continue
                uids = [c['car_uid'] for c in st.get('cars', [])]
                if len(uids) == n_cars:
                    break
                self._psleep(0.05)
            all_spawned = len(uids) == n_cars
            if not all_spawned:
                print(f"   ❌ Only {len(uids)}/{n_cars} cars appeared - "
                      f"phase fails")

            # A fleet this big has no roles - nobody is "the oncoming car" of
            # 150. Name the cast `<class>-<uid>` so a window on any of them
            # still reads an identity (and says which body size drove), sent
            # BEFORE the throttle so the tape names the cast from frame one.
            n_named = self._cast_fallback_names()
            if n_named:
                print(f"   🏷️  {n_named} cars named <class>-<uid> "
                      f"(a cast this big has no roles)")

            # 3. Hold the throttle for every car (BICYCLE mode: without
            #    accelerate the nav's target speed is 0 and it brakes -
            #    the input persists in the sim's control bucket until the
            #    phase's /cars clear removes the cars).
            for uid in uids:
                try:
                    self._api_session.post(
                        f"{API_URL}/control", timeout=5,
                        json={'uid': uid, 'accelerate': True})
                except requests.exceptions.RequestException:
                    pass

            # 4. Run the phase: poll every STRESS_POLL_S, count jitters.
            prev: dict[int, tuple] = {}   # uid -> (sim_t, x_m, y_m)
            jitters = 0
            worst_jump_m = 0.0
            off_road_hits = 0
            wrong_side_hits = 0
            max_cars_seen = 0
            phase_crashed = False
            aborted = False
            # Phase duration in SIM time (the /state 'time' field, total
            # physics substeps x dt_fixed): under load the sim runs slow
            # motion (accumulator cap), so a fixed wall-clock window would
            # cover less driving at high car counts. The wall deadline is
            # only a hang guard for a wedged sim - 20x budget, because at
            # 576 cars a full phase legitimately takes ~400 s of wall time
            # (~8% pace); a truly wedged sim shows ratio -> 0 long before.
            t0_sim = None
            wall_start = time.time()
            wall_deadline = wall_start + self.STRESS_PHASE_SECONDS * 20.0
            while True:
                if abort_check is not None and abort_check():
                    aborted = True
                    break
                try:
                    st = self._api_session.get(f"{API_URL}/state",
                                               timeout=2).json()
                except (requests.exceptions.RequestException, ValueError) as e:
                    print(f"\n   ❌ Game crashed mid-phase: {e}")
                    phase_crashed = True
                    break
                cars = st.get('cars', [])
                max_cars_seen = max(max_cars_seen, len(cars))
                now = time.time()
                # The SIM clock, not the wall clock: the jitter bound below
                # counts "what could this speed cover per SIM second", so it
                # stays the same under --warp/--fast.
                sim_t = st.get('time')
                if not isinstance(sim_t, (int, float)):
                    sim_t = now            # pre-clock streams: wall fallback
                for c in cars:
                    uid = c['car_uid']
                    x, y = c['x'], c['y']
                    if not c.get('on_road', True):
                        off_road_hits += 1
                    if c.get('wrong_side'):
                        wrong_side_hits += 1
                    if uid in prev:
                        t0, x0, y0 = prev[uid]
                        poll_dt = sim_t - t0
                        if poll_dt > 0:
                            moved_m = math.hypot(x - x0, y - y0)
                            # The suite's teleport/jump bound (see
                            # _confirm_stop): movement beyond what the car's
                            # speed can cover in this wall-clock interval,
                            # plus margin.
                            max_plausible_m = (max(c['speed_kmh'] / 3.6,
                                                   50.0) * poll_dt * 1.5
                                               + 1.0)
                            if moved_m > max_plausible_m:
                                jitters += 1
                                worst_jump_m = max(worst_jump_m, moved_m)
                                print(f"   ⚡ JITTER car #{uid}: "
                                      f"{moved_m:.2f} m in {poll_dt*1000:.0f} ms "
                                      f"(max plausible {max_plausible_m:.2f} m, "
                                      f"{c['speed_kmh']:.0f} km/h)")
                    prev[uid] = (sim_t, x, y)
                if t0_sim is None and isinstance(sim_t, (int, float)):
                    t0_sim = sim_t
                if t0_sim is not None:
                    elapsed = sim_t - t0_sim
                else:
                    # Sim without a 'time' field (pre clock-fix): fall back
                    # to the old wall-clock window.
                    elapsed = now - wall_start
                if elapsed >= self.STRESS_PHASE_SECONDS:
                    break
                if now > wall_deadline:
                    print(f"   ⚠️  Wall-clock hang guard hit "
                          f"({self.STRESS_PHASE_SECONDS * 20:.0f} s) with only "
                          f"{elapsed:.1f} s of sim time elapsed - "
                          f"ending the phase early")
                    break
                self._psleep(self.STRESS_POLL_S)

            # 5. Tear the phase down before the next one - and WAIT for it,
            #    so this clear can't race against the next phase's spawns.
            if not self._clear_all_cars():
                print("   ⚠️  Phase teardown: map still has cars after "
                      "10 s (continuing anyway)")

            # Pass criteria: all cars spawned, no crash, no OFF-ROAD (a real
            # physical violation). Wrong-side is REPORTED but not failing:
            # the lane guard has no "in-turn" suppression on junction-free
            # continuous curves like the figure-8, so even a PERFECT single
            # car trips it occasionally (~1 hit per 30 s, measured) - the
            # multi-car run inherits that per-car noise.
            passed = (all_spawned and not phase_crashed and aborted is False
                      and off_road_hits == 0)
            if aborted:
                print("   ⏭️  Phase aborted (user interrupt)")
            overall_passed = overall_passed and passed and not phase_crashed
            crashed = crashed or phase_crashed
            phase_results[key] = {
                'passed': passed,
                'jitters': jitters,
                'worst_jump_m': round(worst_jump_m, 2),
                'off_road_hits': off_road_hits,
                'wrong_side_hits': wrong_side_hits,
                'max_cars_seen': max_cars_seen,
            }
            print(f"   Phase {n_cars} cars: "
                  f"{'✅ PASSED' if passed else '❌ FAILED'} - "
                  f"jitters (Ruckler): {jitters}, "
                  f"worst jump {worst_jump_m:.2f} m, "
                  f"off-road hits {off_road_hits}, "
                  f"wrong-side hits {wrong_side_hits}")

            # Record this phase in the results file under its own key so
            # the jitter sum is stored per scenario (Anzahl Ruckler pro
            # Szenario).
            record_result(results, {
                'start_point': 'fig8_stress',
                'direction': f'{n_cars}cars',
                'passed': passed,
                'aborted': aborted,
                'jitters': jitters,
                'worst_jump_m': round(worst_jump_m, 2),
                'final_segment': None,
                'expected_end_segment': None,
            }, self.test_no)
            save_results(results)

            if phase_crashed or aborted:
                print("   ⏹  Stopping the stress scenario "
                      f"({ 'game crash' if phase_crashed else 'user interrupt' })")
                break

        # The scenario-level result (what print_summary consumes).
        return self._stress_scenario_result(phase_results, overall_passed,
                                            crashed, aborted)

    # Right-of-way crossing stress: 50 cars, half per direction, run for up
    # to CROSSING_MAX_SIM_SECONDS of SIM time or until the FIRST crash.
    # User decision 2026-10-19: the run counts as SUCCESS once no car has
    # crashed within ONE MINUTE of sim time - the old 300 s window ran
    # "forever" on the wall clock (and at --warp 4 still minutes per
    # scenario) without adding signal: every crash seen so far happened
    # within the first ~8-16 s, and a fleet that survives a full minute of
    # dense two-way flow through one crossing is the behaviour we want.
    CROSSING_N_CARS = 50
    CROSSING_MAX_SIM_SECONDS = 60.0
    CROSSING_POLL_S = 0.1

    @staticmethod
    def _crossing_trace_path_from_argv() -> "str | None":
        """Optional CLI `--trace <path>`: dump one JSONL line per /state poll
        of the crossing-stress runners, so a crash's APPROACH can be read at
        the poll's own resolution (pose, speed, braking, segment, progress
        per car) instead of only the post-impact decision dump in logs/.
        Read from sys.argv here (not in main()) so every entry point - CLI
        suite, /run_test subprocess, `--tests 23` - honours it without
        threading a parameter through. None = no tracing (default).
        """
        if '--trace' not in sys.argv:
            return None
        idx = sys.argv.index('--trace')
        raw = sys.argv[idx + 1] if idx + 1 < len(sys.argv) else ''
        if not raw.strip():
            print("--trace needs a file path, e.g. --trace /tmp/xing_trace.jsonl")
            return None
        return raw

    # start_point name -> the loop's node-id prefix on the basic map.
    # One line each, for the window that says what is being watched
    # (the reviewer's Test panel). The DETERMINISTIC_TESTS tuple carries the
    # same sentence for the numbered scenarios; the crossing runners are
    # dispatched by start point and never see that tuple.
    CROSSING_DESCRIPTIONS = {
        'fig8_xing': "Right-of-way crossing WITH signs: 50 cars, 25 per "
                     "direction, first crash or 60 s",
        'fig8_xing_plain': "Crossing with NO signs (right before left): 50 "
                           "cars, 25 per direction, first crash or 60 s",
    }

    CROSSING_NODE_PREFIX = {
        'fig8_xing': 'xing_',        # tile (2,3): WITH Vorfahrt signs
        'fig8_xing_plain': 'xingp_', # tile (3,3): NO signs (right-before-left)
    }

    def _crossing_loop_segments(self, prefix: str) -> list:
        """Segment indices whose START node id begins with `prefix` (the
        48 segments of one figure-8 crossing loop), read live from the
        game so it never depends on positional indices."""
        segs, i = [], 0
        while True:
            try:
                r = self._api_session.get(f"{API_URL}/segment/{i}", timeout=5)
            except requests.exceptions.RequestException:
                break
            if r.status_code != 200:
                break
            s = r.json()
            if str(s.get('start_node', '')).startswith(prefix):
                segs.append(s)
            i += 1
        return segs

    def _crossing_result(self, start_point: str, passed: bool, aborted: bool,
                         game_crashed: bool, crash_info: str | None,
                         metrics: dict | None = None) -> dict:
        """Scenario-level result in print_summary's shape."""
        m = metrics or {}
        return {
            'start_point': start_point,
            'direction': 'crossing',
            'passed': passed and not aborted,
            'aborted': aborted,
            'off_road_detected': False,
            'centreline_crossings': m.get('crossings', 0),
            'off_road_hits': m.get('off_road_hits', 0),
            'wall_duration_s': m.get('wall_s', 0.0),
            'sim_duration_s': m.get('sim_s', 0.0),
            'flow_m_per_s': m.get('flow_per_car_s', 0.0),
            'driving_cars': m.get('cars', 0),
            'warp': m.get('warp'),
            'instant_snap_detected': False,
            'teleport_detected': False,
            'game_crashed': game_crashed,
            'wrong_side_detected': False,
            'segment_changed': True,
            'reached_expected_segment': True,   # no end flag in this test
            'expect_wrong_side': False,
            'final_segment': None,
            'expected_end_segment': None,
            'crash_info': crash_info,
        }

    def run_crossing_stress_test(self, start_point: str,
                                 results: dict | None = None,
                                 abort_check: "callable | None" = None,
                                 label: str | None = None) -> dict:
        """Spawn 50 cars (25 per direction, interleaved) on one figure-8
        crossing loop, hold the accelerator on all of them, and run for up
        to CROSSING_MAX_SIM_SECONDS (60 s) of sim time or until the FIRST
        crash (a car reporting 'in_contact' / a crash episode). Passes once
        the fleet survives that whole minute without a crash.
        """
        if results is None:
            results = load_results()
        prefix = self.CROSSING_NODE_PREFIX[start_point]
        n_cars = self.CROSSING_N_CARS
        print(f"\n{'-'*60}")
        print(f"CROSSING STRESS ({start_point}): {n_cars} cars, "
              f"{n_cars // 2}/direction, up to "
              f"{self.CROSSING_MAX_SIM_SECONDS:.0f} s or first crash")
        print(f"{'-'*60}")
        # The crossing is the subject of this scenario, so the window belongs
        # on the crossing box for the WHOLE run — announced here, once, exactly
        # like the deterministic scenarios announce theirs.
        self.announce_focus(start_point, label=label,
                            desc=self.CROSSING_DESCRIPTIONS.get(start_point, ""))

        # Defensively UNFREEZE first: a previous crossing run may have left
        # the sim frozen on a crash (see the crash branch below).
        try:
            self._api_session.post(f"{API_URL}/freeze", timeout=5,
                                   json={'frozen': False})
        except requests.exceptions.RequestException:
            pass

        # 1. Clean slate.
        if not self._clear_all_cars():
            print("   ❌ Could not clear the map - scenario fails")
            return self._crossing_result(start_point, passed=False,
                                         aborted=False, game_crashed=True,
                                         crash_info=None)

        # 2. Identify the loop and spawn evenly around it, alternating
        #    direction (even = forward, odd = reverse).
        loop = self._crossing_loop_segments(prefix)
        if len(loop) != 48:
            print(f"   ❌ Expected 48 crossing segments ({prefix}), "
                  f"got {len(loop)} - is the server on the basic map?")
            return self._crossing_result(start_point, passed=False,
                                         aborted=False, game_crashed=False,
                                         crash_info=None)
        spawn_speed_mps = self.STRESS_SPAWN_KMH / 3.6
        # Cycle through ALL vehicle classes (Config.CAR_COLORS) so the run
        # exercises every per-class collision footprint, not just the sedan.
        # Arc-length uniform spawn (see _arc_uniform_spawns) - the old
        # "nearest segment midpoint" quantization could put two cars on the
        # same midpoint when n_cars > len(loop).
        colors = ['blue', 'silver', 'police', 'tan', 'tractor', 'pickup', 'mixer']
        offset = self._crossing_safe_offset(loop, n_cars)
        # The spawn discipline (user rule): the fleet must not move AT ALL
        # while it is being placed. A rolling start meant the first cars were
        # already at speed when the last one appeared, so the whole fleet
        # arrived at the crossing at once and locked it. Freeze the sim,
        # place every car at rest, then release the brake.
        try:
            self._api_session.post(f"{API_URL}/freeze", timeout=5,
                                   json={'frozen': True})
        except requests.exceptions.RequestException:
            pass
        for k, (seg, prog) in enumerate(
                self._arc_uniform_spawns(loop, n_cars, offset=offset)):
            reverse = (k % 2 == 1)
            self._api_session.post(f"{API_URL}/teleport", timeout=5, json={
                'segment': seg, 'progress': prog, 'speed': 0.0,
                'add': True, 'color': colors[k % len(colors)],
                'reverse': reverse})

        # Wait until all cars exist (queued teleports land ~1 per frame).
        deadline = time.time() + max(15.0, n_cars * 0.03)
        uids: list[int] = []
        bad_streak = 0
        while time.time() < deadline:
            try:
                st = self._api_session.get(f"{API_URL}/state", timeout=2).json()
                bad_streak = 0
            except (requests.exceptions.RequestException, ValueError):
                bad_streak += 1
                if bad_streak >= 5:
                    break
                self._psleep(0.5)
                continue
            uids = [c['car_uid'] for c in st.get('cars', [])]
            if len(uids) == n_cars:
                break
            self._psleep(0.05)
        if len(uids) != n_cars:
            print(f"   ❌ Only {len(uids)}/{n_cars} cars appeared - fails")
            return self._crossing_result(start_point, passed=False,
                                         aborted=False, game_crashed=False,
                                         crash_info=None)

        # No roles in a 50-car fleet either: name the cast `<class>-<uid>`. Still
        # while FROZEN, so every car carries its name before the first moving
        # frame and the tape header writes the cast once, not per frame.
        n_named = self._cast_fallback_names()
        if n_named:
            print(f"   🏷️  {n_named} cars named <class>-<uid> "
                  f"(a cast this big has no roles)")

        # 3. Hold the throttle for every car - and only now unfreeze: the
        #    fleet accelerates once ALL cars exist (see the spawn discipline
        #    above).
        for uid in uids:
            try:
                self._api_session.post(f"{API_URL}/control", timeout=5,
                                       json={'uid': uid, 'accelerate': True})
            except requests.exceptions.RequestException:
                pass
        try:
            self._api_session.post(f"{API_URL}/freeze", timeout=5,
                                   json={'frozen': False})
        except requests.exceptions.RequestException:
            pass

        # 4. Run: poll /state, fail on the FIRST crash, stop at 300 s sim.
        print(f"   GO! {n_cars} cars accelerating - watching for crashes")
        # --trace <path>: keep every poll's full fleet state (see the
        # crash's approach, not just its aftermath).
        trace_path = self._crossing_trace_path_from_argv()
        trace_fh = None
        if trace_path:
            try:
                trace_fh = open(trace_path, 'w')
            except OSError as e:
                print(f"   ⚠️  --trace {trace_path} could not open: {e}")
                trace_path = None

        def _trace_line(sim_t, cars):
            if trace_fh is None:
                return
            keep = ('car_uid', 'x', 'y', 'heading', 'speed_kmh', 'segment',
                    'progress', 'braking', 'hazard', 'in_contact',
                    'contact_with', 'level')
            trace_fh.write(json.dumps(
                {'time': sim_t,
                 'cars': [{k: c.get(k) for k in keep} for c in cars]},
                separators=(',', ':')) + '\n')

        t0_sim = None
        aborted = game_crashed = False
        crash_info = None
        expected_uids: set[int] | None = None
        flow_base: float | None = None
        lock_base: int | None = None
        last_report = -1e9
        wall_deadline = time.time() + self.CROSSING_MAX_SIM_SECONDS * 20.0
        # Crash episodes are counted per car (monotonic, GET /state
        # 'crash_episodes'); baseline right before the run so a car that
        # somehow arrives already crashed does not read as a new contact.
        crash_base: dict[int, int] = {}
        # R33 (docs §8): the lane guard's ERROR channel, not its detector
        # channel. A centreline crossing the guard cannot attribute to a
        # named intent is a driving error even when the per-car wrong-side
        # flag stayed false - the flag answers "is it on the wrong side",
        # the invariant answers "was the crossing explained". Read as a
        # delta over the scenario so a lifetime count cannot latch.
        guard_base: int | None = None
        # Quality metrics for the run's results table (docs §8 "Liveness &
        # quality"): metres the fleet drove, sim/wall time it covered, and
        # how often any car crossed the centreline. Baselines come from the
        # FIRST poll, so a scenario reports its own window, not the game's
        # lifetime.
        wall_start = time.time()
        flow_base_m: float | None = None
        sim_base: float | None = None
        cross_base: int | None = None
        flow_end_m, sim_end, cross_end = 0.0, 0.0, 0
        while True:
            if abort_check is not None and abort_check():
                aborted = True
                break
            try:
                st = self._api_session.get(f"{API_URL}/state", timeout=2).json()
            except (requests.exceptions.RequestException, ValueError) as e:
                print(f"\n   ❌ Game crashed mid-run: {e}")
                game_crashed = True
                break
            cars = st.get('cars', [])
            sim_t = st.get('time')
            _trace_line(sim_t, cars)
            if isinstance(sim_t, (int, float)):
                flow_end_m = st.get('flow_m_total') or 0.0
                sim_end = sim_t
                cross_end = sum((c.get('lane_guard_stats') or {})
                                .get('centreline_crossings', 0) for c in cars)
                if flow_base_m is None:
                    flow_base_m = flow_end_m
                    sim_base = sim_t
                    cross_base = cross_end
            for c in cars:
                crash_base.setdefault(c['car_uid'], c.get('crash_episodes', 0))
            if guard_base is None:
                guard_base = st.get('lane_guard_errors', 0)
            if t0_sim is None and isinstance(sim_t, (int, float)):
                t0_sim = sim_t
            elapsed = (sim_t - t0_sim) if t0_sim is not None else 0.0

            # R33: an unexplained crossing is an error, whatever the per-car
            # detector says.
            if guard_base is not None and st.get('lane_guard_errors', 0) > guard_base:
                print(f"\n   ❌ unexplained centreline crossing at t={elapsed:.1f}s "
                      f"(lane_guard_errors {guard_base} -> {st.get('lane_guard_errors', 0)})")
                crash_info = "unexplained centreline crossing (R33 invariant)"
                break

            # R28 (docs §8): a total lockdown is an ERROR - prevention must
            # keep it unreachable, so the run fails the moment the sim had
            # to report one.
            lk = st.get('lockdowns') or 0
            if lock_base is None:
                lock_base = lk
            elif lk > lock_base and elapsed >= 1.0:
                crash_info = (f"R28 LOCKDOWN at t={elapsed:.1f}s "
                              f"({lk - lock_base} event(s)): "
                              f"{st.get('lockdown_last', '')[:400]}")
                print(f"\n   ❌ {crash_info}")
                break

            # R29 (docs §8): a car taken out of the equation is an ERROR.
            # Prevention (R30) must make the lockdown valve unreachable, so
            # a shrinking fleet here is a failure - it must never read as
            # "survived" with a smaller denominator.
            if expected_uids is None and len(cars) >= self.CROSSING_N_CARS:
                expected_uids = {c['car_uid'] for c in cars}
            elif expected_uids is not None and elapsed >= 5.0:
                gone = expected_uids - {c['car_uid'] for c in cars}
                if gone:
                    crash_info = (f"R29 lockdown valve: car {min(gone)} "
                                  f"despawned at t={elapsed:.1f}s "
                                  f"({len(gone)} car(s) removed)")
                    print(f"\n   ❌ {crash_info}")
                    break

            # The FIRST crash is the first NEW contact episode, not the first
            # poll that happens to see the transient in_contact flag: a pinned
            # pair clears inside one 0.1 s poll, and the flag was already
            # false when the next poll ran (measured: a run whose own crash
            # dumps recorded contacts at t=6.9/7.8/20.7 s while every poll
            # read in_contact=false and the suite called the first crash at
            # 84.8 s). in_contact stays as a fallback for older hosts.
            hit = next((c for c in cars
                        if c.get('crash_episodes', 0) > crash_base.get(c['car_uid'], 0)),
                       None) or next((c for c in cars if c.get('in_contact')), None)
            if hit is not None:
                crash_info = (f"crash at t={elapsed:.1f}s: car "
                              f"{hit['car_uid']} contacted car "
                              f"{hit.get('contact_with')}")
                print(f"\n   ❌ {crash_info}")
                break

            if elapsed - last_report >= 10.0:
                speeds = [c['speed_kmh'] for c in cars]
                moving = sum(1 for v in speeds if v > 3.6)
                # Quality metric (the user's, 2026-10-19): metres driven by
                # the WHOLE fleet per second of sim time over this run window.
                # High = fluid traffic, low = the fleet is stuck. Unlike the
                # 'moving' snapshot it also punishes creeping at 2 km/h.
                ft = st.get('flow_m_total') or 0.0
                if flow_base is None:
                    flow_base = ft
                flow_win = (ft - flow_base) / elapsed if elapsed > 0 else 0.0
                # The numbers only mean anything for a MULTI-car run, and what
                # matters is PER CAR: the fleet total over the cars involved.
                per_car = flow_win / max(len(cars), 1)
                # R31 (docs §8): cars that moved < 1 m within the wall-time
                # window - a warning, not a verdict (the thresholds are still
                # being tuned; the crossing run is far shorter than 3 min).
                stuck = st.get('standstill_errors') or 0
                print(f"   t={elapsed:5.0f}s  no crash  moving {moving}/"
                      f"{len(cars)}  flow/car {per_car:.1f} m/s "
                      f"({flow_win:.0f} m/s fleet)"
                      f"{'  stuck ' + str(stuck) if stuck else ''}")
                last_report = elapsed

            if elapsed >= self.CROSSING_MAX_SIM_SECONDS:
                print(f"   ✅ Survived {self.CROSSING_MAX_SIM_SECONDS:.0f} s "
                      f"clean")
                break
            if time.time() > wall_deadline:
                print("   ⚠️  Wall-clock hang guard hit - ending early")
                break
            self._psleep(self.CROSSING_POLL_S)

        if trace_fh is not None:
            trace_fh.close()
            print(f"   🧵 state trace: {trace_path} "
                  f"({'%.1f s' % ((sim_t - t0_sim) if (t0_sim is not None and isinstance(sim_t, (int, float))) else 0.0)} of sim, "
                  f"{self.CROSSING_POLL_S:.2f} s resolution)")

        if crash_info is not None:
            # FREEZE the sim on the crash and LEAVE the cars in place so the
            # Godot window shows the exact crash pose for analysis (the
            # runner no longer quits Godot / kills the host on failure).
            try:
                self._api_session.post(f"{API_URL}/freeze", timeout=5,
                                       json={'frozen': True})
                print("   ❄️  Sim frozen on the crash - cars left in place "
                      "for analysis")
            except requests.exceptions.RequestException:
                pass
        else:
            self._clear_all_cars()
        passed = (crash_info is None and not game_crashed and not aborted)
        record_result(results, {
            'start_point': start_point,
            'direction': 'crossing',
            'passed': passed,
            'aborted': aborted,
            'final_segment': None,
            'expected_end_segment': None,
        }, self.test_no)
        save_results(results)
        # Quality metrics of this run (docs §8): the metres the fleet drove
        # over its own window, per car that was supposed to drive.
        _wall = time.time() - wall_start
        _sim = max(0.0, sim_end - (sim_base or sim_end))
        _flow = max(0.0, flow_end_m - (flow_base_m or flow_end_m))
        _metrics = {
            'wall_s': _wall,
            'sim_s': _sim,
            'crossings': max(0, cross_end - (cross_base or cross_end)),
            'flow_per_car_s': (_flow / max(_sim, 1e-9)) / max(n_cars, 1),
            'cars': n_cars,
            'warp': self._time_warp_cached(),
        }
        return self._crossing_result(start_point, passed=passed,
                                     aborted=aborted, game_crashed=game_crashed,
                                     crash_info=crash_info, metrics=_metrics)

    def _right_side_kerb_gaps_m(self, state: dict, start_point: str):
        """Gap (m) between the car's RIGHT FLANK at both wheel stations and
        the right kerb line of the scenario's road; positive = on the road.

        The basic map is deterministic, so the kerb line comes from the
        start point's centreline position + heading (GET /start_points):
        a straight line PARK_AVENUE_WIDTH_M/2 to the right of the legal
        direction of travel. The car pose is its rear axle (state x/y) +
        heading; the wheel stations are the rear axle and the front axle
        (SPRITE_WHEELBASE_M ahead), measured at the body flank
        (CAR_WIDTH/2 outboard).
        """
        sp = self.get_start_points().get(start_point)
        if sp is None:
            return None
        h = math.radians(sp['heading'])
        fx, fy = math.sin(h), math.cos(h)          # direction of travel
        rx, ry = fy, -fx                           # right-hand side of it
        half_w_m = PARK_AVENUE_WIDTH_M / 2.0
        cx, cy = sp['x'] + rx * half_w_m, sp['y'] + ry * half_w_m  # kerb line
        hd = math.radians(state['heading'])
        fcx, fcy = math.sin(hd), math.cos(hd)      # car forward
        rcx, rcy = fcy, -fcx                       # car right side
        x, y = state['x'], state['y']              # rear axle (metres)
        wb_m = SPRITE_WHEELBASE_M
        flank_m = CAR_WIDTH / 2.0
        gaps = []
        for wx, wy in ((x + rcx * flank_m, y + rcy * flank_m),            # rear right
                       (x + fcx * wb_m + rcx * flank_m,
                        y + fcy * wb_m + rcy * flank_m)):                 # front right
            # signed distance from the kerb line into the road (n = -r)
            gaps.append((wx - cx) * (-rx) + (wy - cy) * (-ry))
        return gaps

    def get_start_points(self) -> dict:
        """Deterministic start points of the loaded map, in METRES."""
        response = requests.get(f"{API_URL}/start_points")
        return response.json()
    
    def _drive_to_segment_end_and_stop(self, target_segment: int, start_time: float,
                                        max_extra_time: float = 25.0,
                                        abort_check: "callable | None" = None):
        """Having just arrived on the designated target segment, keep
        actually driving it (don't just release controls mid-segment) -
        brake as we approach ITS far end and confirm the car really comes
        to a full stop there, same as a human driver pulling in and
        parking rather than abandoning the car halfway down the road.
        
        Returns (final_position, stopped_cleanly: bool, details: dict).
        details may include off_road/instant_snap/teleport/game_crashed
        flags and a violation_details dict, same shape as monitor_turn's
        own violation reporting, for the caller to fold in.
        """
        details = {}
        last_heading = None
        last_pos = None
        last_time = time.time()
        last_sim = None
        braking_commanded = False
        deadline = time.time() + max_extra_time
        
        while time.time() < deadline:
            if abort_check is not None and abort_check():
                details['aborted'] = True
                return (last_pos or (0, 0)), False, details
            try:
                state = self.get_state()
            except requests.exceptions.RequestException as e:
                details['game_crashed'] = True
                details['violation_details'] = {'type': 'game_crashed', 'error': str(e)}
                return (last_pos or (0, 0)), False, details
            
            pos = (state['x'], state['y'])
            if last_heading is not None:
                heading_diff = abs((state['heading'] - last_heading + 180) % 360 - 180)
                if heading_diff > 30.0:
                    details['instant_snap'] = True
                    details['violation_details'] = {'type': 'instant_heading_snap', 'position': pos}
                    return pos, False, details
            last_heading = state['heading']
            
            if not state['on_road']:
                details['off_road'] = True
                details['violation_details'] = {
                    'type': 'off_road', 'position': pos,
                    'time': time.time() - start_time,
                    'speed_kmh': state['speed_kmh'],
                    'heading': state['heading'],
                }
                return pos, False, details
            
            now = time.time()
            if last_pos is not None:
                poll_dt = _poll_dt(state, last_sim, now, last_time)
                moved_m = ((pos[0] - last_pos[0]) ** 2 + (pos[1] - last_pos[1]) ** 2) ** 0.5
                max_plausible_m = max(state['speed_kmh'] / 3.6, 50.0) * poll_dt * 1.5 + 1.0
                if moved_m > max_plausible_m:
                    details['teleport'] = True
                    details['violation_details'] = {'type': 'teleport', 'position': pos, 'distance_m': moved_m}
                    return pos, False, details
            last_pos = pos
            last_time = now
            last_sim = state.get('time', last_sim)
            
            if state['segment'] != target_segment:
                # Drove clean through and out the other side without ever
                # needing to brake (e.g. a very short target segment) -
                # that's fine, nothing to stop for; treat as parked.
                return pos, True, details
            
            progress = state.get('progress', 0.5)
            forward = state.get('forward', True)
            near_end = progress >= 0.92 if forward else progress <= 0.08
            
            if near_end or state['speed_kmh'] < 1.0:
                braking_commanded = True
                self.send_control(accelerate=False, brake=True)
            elif not braking_commanded:
                self.send_control(accelerate=True, brake=False)
            
            if braking_commanded and state['speed_kmh'] < 1.0:
                self.send_control(accelerate=False, brake=False)
                return pos, True, details
            
            self._psleep(0.05)
        
        # Ran out of time without coming to a stop on the target segment.
        return (last_pos or (0, 0)), False, details
    
    def _stop_after_verdict(self, max_wait: float = 8.0) -> bool:
        """Brake to a standstill AFTER the verdict has been filed.

        Passing the red flag ENDS a running test - at speed, that is the
        criterion (docs/TESTING.md §3) - but a car that drives out of the
        picture leaves the watcher asking whether the run is over, which is the
        user's request of 2026-10-25: *"es wäre schön, wenn sie nach dem
        Überfahren der roten Flagge dann auch bremsen würden bis zum Stillstand,
        damit man sieht, der Test ist fertig"*. So the flag still ends the test
        and the stillstand is the period at the end of the sentence.

        Three rules make this an epilogue and not a second test:
        · it runs AFTER `POST /test_result`, so nothing it does can move a
          verdict that is already filed and in `self.test_results`;
        · braking is a latched `brake` through `POST /control` - the car loses
          speed the way its physics says it may (AGENTS.md: never do physically
          impossible things), the speed is never written to zero;
        · a LONE car only. Standing still in traffic would drop a sudden
          obstacle in front of whoever follows and hand the reviewer a crash the
          scenario never tested.

        Answers whether the car stands (also True when it was standing
        already), and stays quiet about a standstill that did not happen in
        time: the brake stays latched, the next scenario's `reset()` clears it.
        """
        t0 = time.time()
        try:
            st = self.get_state()
        except requests.exceptions.RequestException:
            return False
        v0 = float(st.get('speed_kmh') or 0.0)
        ncars = len(st.get('cars') or [])
        if ncars > 1:
            print(dim(f"   \U0001f6d1 skipped the stop: {ncars} cars on the field - "
                      f"standing still in traffic is not what this tests"))
            return False
        if v0 < 1.0:
            return True                       # parking tests already stand
        print(dim(f"   \U0001f6d1 test over - braking to a standstill from {v0:.0f} km/h "
                  f"so the picture says it ended"))
        self.send_control(accelerate=False, brake=True)
        v = v0
        while time.time() - t0 < max_wait:
            try:
                st = self.get_state()
            except requests.exceptions.RequestException:
                return False
            v = float(st.get('speed_kmh') or 0.0)
            if v < 0.3:
                print(dim(f"      stands still after {time.time() - t0:.1f} s "
                          f"(sim {st.get('time', 0):.1f} s) - brake stays latched"))
                return True
            time.sleep(0.15)
        print(dim(f"      still {v:.0f} km/h after {max_wait:.0f} s - verdict is "
                  f"already filed, this changed nothing but the show"))
        return False

    def _brake_to_stop(self, start_time: float, max_wait: float = 10.0,
                       abort_check: "callable | None" = None):
        """Brake and wait for the car to come to a FULL stop right where it
        is - the scenario's destination (the red end flag). The brake stays
        LATCHED on purpose: releasing it would let the autopilot pull
        straight off again; the next scenario's reset_controls() clears the
        latch.
        
        Returns (final_position, stopped_cleanly: bool, details: dict),
        same shape as _drive_to_segment_end_and_stop, for the caller to
        fold into the result.
        """
        details = {}
        last_heading = None
        last_pos = None
        last_time = time.time()
        last_sim = None
        deadline = time.time() + max_wait
        
        self.send_control(accelerate=False, brake=True)
        
        while time.time() < deadline:
            if abort_check is not None and abort_check():
                details['aborted'] = True
                return (last_pos or (0, 0)), False, details
            try:
                state = self.get_state()
            except requests.exceptions.RequestException as e:
                details['game_crashed'] = True
                details['violation_details'] = {'type': 'game_crashed', 'error': str(e)}
                return (last_pos or (0, 0)), False, details
            
            pos = (state['x'], state['y'])
            if last_heading is not None:
                heading_diff = abs((state['heading'] - last_heading + 180) % 360 - 180)
                if heading_diff > 30.0:
                    details['instant_snap'] = True
                    details['violation_details'] = {'type': 'instant_heading_snap', 'position': pos}
                    return pos, False, details
            last_heading = state['heading']
            
            if not state['on_road']:
                details['off_road'] = True
                details['violation_details'] = {
                    'type': 'off_road', 'position': pos,
                    'time': time.time() - start_time,
                    'speed_kmh': state['speed_kmh'],
                    'heading': state['heading'],
                }
                return pos, False, details
            
            now = time.time()
            if last_pos is not None:
                poll_dt = _poll_dt(state, last_sim, now, last_time)
                moved_m = ((pos[0] - last_pos[0]) ** 2 + (pos[1] - last_pos[1]) ** 2) ** 0.5
                max_plausible_m = max(state['speed_kmh'] / 3.6, 50.0) * poll_dt * 1.5 + 1.0
                if moved_m > max_plausible_m:
                    details['teleport'] = True
                    details['violation_details'] = {'type': 'teleport', 'position': pos, 'distance_m': moved_m}
                    return pos, False, details
            last_pos = pos
            last_time = now
            last_sim = state.get('time', last_sim)
            
            if state['speed_kmh'] < 1.0:
                # Parked - keep the brake latched (see docstring).
                return pos, True, details
            
            self._psleep(0.05)
        
        # Ran out of time without coming to a stop.
        return (last_pos or (0, 0)), False, details
    
    def _aborted_result(self, start_point: str | None, direction: str,
                        target_speed: float, results: dict | None) -> dict:
        """Build + report the result of a scenario the user ABORTED (a test
        jump from the cockpit controller).

        An aborted run is NOT recorded in turning_results.json: it was never
        actually tested, so it must not overwrite the last real result (and
        must not show up in --failed). It IS kept in self.test_results so
        the summary can report it as "aborted" instead of dropping it.
        """
        print(f"\n   ⏭  ABORTED - user jumped to another test from the cockpit")
        try:
            self.reset_controls()
        except requests.exceptions.RequestException:
            pass
        result = {
            'start_point': start_point,
            'direction': direction,
            'target_speed_kmh': target_speed,
            'frames_checked': 0,
            'duration': 0.0,
            'initial_segment': -1,
            'expected_end_segment': None,
            'final_segment': -1,
            'start_position': (0.0, 0.0),
            'end_position': (0.0, 0.0),
            'segment_changed': False,
            'reached_expected_segment': False,
            'stopped_at_end': False,
            'off_road_detected': False,
            'instant_snap_detected': False,
            'teleport_detected': False,
            'game_crashed': False,
            'max_heading_change_per_frame': 0.0,
            'violation_details': None,
            'positions': [],
            'aborted': True,
            'passed': False,
        }
        self.test_results.append(result)
        print(f"{'─'*60}\n")
        return result

    def monitor_turn(self, direction: str, duration: float = 15.0, target_speed: float = 50.0,
                      start_point: str | None = None, expected_end_segment: int | None = None,
                      description: str | None = None, results: dict | None = None,
                      abort_check: "callable | None" = None,
                      label: str | None = None, start_progress: float = 0.5,
                      kerb_check: bool = False,
                      end_flag_progress: float = END_FLAG_PROGRESS,
                      expect_wrong_side: bool = False,
                      spawn_speed_kmh: float | None = None,
                      car_name: str | None = None,
                      extra_cars: list[dict] | None = None) -> dict:
        """Monitor a turn for violations.
        
        Args:
            direction: "left" or "right"
            duration: Maximum time to monitor (seconds)
            target_speed: Target speed in km/h
            start_point: If given, teleport to this deterministic named start
                point (synthetic test map) instead of a random location.
            expected_end_segment: If given, the test only PASSES if the car
                ends up on exactly this segment - not just "any segment
                change" (which would silently pass even if e.g. a LEFT
                turn actually went RIGHT, as long as it went somewhere -
                this is exactly the kind of bug that slipped through
                before the API-blinker-routing fix). Monitoring keeps
                running across MULTIPLE segment changes (e.g. a
                roundabout: entry -> ring -> ring -> exit) until the car
                has driven to the 50% point of this exact segment
                (mid-segment, mirroring the mid-segment START - the far
                end / parking tail is NOT part of a turn test), a
                violation occurs, or duration runs out. If None (only used
                for the legacy random-location suite, where there's no way
                to know the correct answer in advance), falls back to the
                old "any change + drive to the far end and stop" behavior.
            start_progress: Where along the start segment to spawn (0..1
                from the named node). The suite standard is 0.5; the
                parking-offset scenarios use 0.15 so there is ~100 m of
                approach between spawn and the flag at 50%.
            kerb_check: Parking-offset scenarios only (docs/
                DRIVING_MANEUVERS.md §1 variant). The arrival at the flag
                is not enough - BOTH right-hand wheels must end within
                KERB_PASS_MAX_M of the right kerb or the scenario fails.
            end_flag_progress: Where along the expected end segment the
                red end flag sits (0..1 from the segment's first node).
                Suite standard is END_FLAG_PROGRESS (first 20%); a
                scenario may override it (e.g. test 12 finishes at the
                50% point of the end segment).
            abort_check: Optional zero-arg callable, polled throughout setup
                and monitoring. If it returns truthy, the scenario is
                ABORTED right away (the cockpit controller sets this when
                the user clicks a different test button): inputs are
                cleared and an 'aborted' result is returned - NOT recorded
                in turning_results.json, since the scenario was never
                actually tested.
        
            car_name: the ROLE this scenario gives its primary car
                ('left_turning_car', 'blocked_car', 'parking_car', …),
                carried WITH the spawn so the label exists before the car's
                first tick. None leaves the car unnamed - only the big-cast
                runners (fig8_*) do that now, and they name their whole fleet
                `<class>-<uid>` (see _cast_fallback_names).
        Returns:
            dict with test results
        """
        def _aborted() -> bool:
            return abort_check is not None and bool(abort_check())
        # --- What is this test? / what happened last time? ---
        # description: one-line human summary of the scenario (passed in by the
        # suite); last: the previous persisted result for this exact scenario,
        # so a known-broken corner shows WHY it failed last time right up front.
        print(f"\n{'='*60}")
        if description:
            print(cyan(f"🧪 Test: {description}"))
        else:
            print(cyan(f"🧪 Test: {direction.upper()} turn" + (f" @ '{start_point}'" if start_point else " (random location)")))
        if start_point and results is not None:
            last = last_result_for(results, start_point, direction)
            if last is None:
                print(dim("   Last run:  never run before"))
            elif last.get("passed"):
                when = f" ({last['timestamp']})" if last.get("timestamp") else ""
                print(f"   Last run:  {green('Yes')} - passed{when}")
            else:
                print(f"   Last run:  {red('No')} - {last.get('reason') or 'unknown failure'}" +
                      (f" ({last['timestamp']})" if last.get("timestamp") else ""))
        print(f"{'='*60}")
        
        # Reset and enable breadcrumbs for visual debugging
        self.reset_controls()
        requests.post(f"{API_URL}/toggle", json={'breadcrumbs': True})

        # WATCH MODE (docs/AGENTS.md, user decision 2026-10-21): staging must
        # not race the sim. Every car is placed AT SPEED while the world is
        # halted, the harness polls until all uids exist, and only then does
        # the run start at the requested pace - otherwise the first second of
        # the manoeuvre under review is spent driving into a half-staged
        # world, which is what "loses half the action".
        watch = os.environ.get("DG_FREEZE_STAGE") == "1"
        if watch:
            requests.post(f"{API_URL}/freeze", json={'frozen': True})
        
        # Create a fresh car at the given start point or a random location.
        # Deterministic scenarios start MID-segment (50%) so the maneuver
        # begins on open road, not hugging the junction node.
        if start_point:
            print(f"📍 Creating new car at '{start_point}' "
                  f"({start_progress * 100:.0f}% of segment)...")
            # Running turn tests spawn ALREADY MOVING (rolling start, no
            # standstill-to-cruise phase); parking tests start from rest.
            if kerb_check:
                rolling = None
            elif spawn_speed_kmh is not None:
                rolling = spawn_speed_kmh / 3.6
            else:
                rolling = RUNNING_START_KMH / 3.6
            self.create_car_at_start_point(start_point, progress=start_progress,
                                           speed_mps=rolling, role=car_name)
            # Label under the minimap: the current test number ("10/27"),
            # when running as part of a suite; standalone runs fall back
            # to the track number / start-point name. Only when the scenario
            # named NOBODY: TestFlags.HudLabel is ONE field, the same one the
            # role travels in, and a number written over a role would put the
            # car back into `no-name` in /state, in the window and on the tape.
            # The scenario number still travels in the focus note (POST /focus
            # carries the same label), so nothing loses "which test is running"
            # except a raw run of a scenario that has no role to give.
            if car_name is None:
                self.set_hud_label(label if label is not None
                                   else str(START_POINT_NUMBER.get(start_point)
                                           or start_point))
            # What the window should look at for THIS scenario (SCENARIO_FOCUS):
            # announced before the cast is staged, so the camera is already
            # aimed when the first frame of the test exists.
            # (resolve_focus queries /segment_names itself when the caller has
            # no map snapshot at hand — monitor_turn has none.)
            # Kept so the cast staging below can REFINE it with the uid each
            # role went to (announce_focus → POST /focus, docs/TESTING.md).
            self._focus_decl = self.announce_focus(start_point, label=label,
                                                   desc=description)

            # --- other cars in the same world (multi-car scenarios) --------
            # docs/DRIVING_MANEUVERS.md §8: the T-junction pair runs with a
            # second car on the bar, the obstruction tests with a car that is
            # PARKED inside a lane. throttle=False leaves a car exactly where
            # the teleport put it (the nav never gets a signal to move), which
            # is how a parked car is staged - the spawn itself is frozen
            # discipline: speed 0, no throttle afterwards.
            extra_uids: list[int] = []
            throttle_uids: list[int] = []
            crash_base_extra: dict[int, int] = {}
            primary_uid = self.get_state().get('car_uid')
            if extra_cars:
                for spec in extra_cars:
                    before = {c['car_uid'] for c in self.get_state().get('cars', [])}
                    payload = {'add': True}
                    if 'start_point' in spec:
                        payload['start_point'] = spec['start_point']
                    else:
                        payload['segment'] = spec['segment']
                    payload['progress'] = spec.get('progress', 0.5)
                    payload['speed'] = spec.get('speed_mps', 0.0)
                    if spec.get('cruise_kmh') is not None:
                        # A slow vehicle: the km/h this car chooses to drive at
                        # (its class ceiling still applies) - staging real
                        # slow traffic rather than a stopped car.
                        payload['cruise_kmh'] = spec['cruise_kmh']
                    # The car's ROLE, carried WITH the spawn (the sim sets
                    # the label atomically - a second request would race the
                    # first tick). The client can then address "the oncoming
                    # car" by what it IS instead of by what it looks like,
                    # and the tape header names the cast once for the review.
                    if spec.get('name'):
                        payload['name'] = spec['name']
                    requests.post(f"{API_URL}/teleport", json=payload)
                    got = None
                    for _ in range(60):
                        now_cars = {c['car_uid'] for c in self.get_state().get('cars', [])}
                        new = now_cars - before
                        if new:
                            got = min(new)
                            break
                        time.sleep(0.1)
                    if got is None:
                        print("   ❌ extra car never spawned - aborting scenario")
                        return self._aborted_result(start_point, direction, target_speed, results)
                    extra_uids.append(got)
                    # Tell the game which car the ROLE went to, as soon as it
                    # exists: the focus announcement at the scenario's start
                    # could not know it (the cast is staged here, after it).
                    if spec.get('name') and getattr(self, '_focus_decl', None):
                        self._focus_decl = self.announce_focus(
                            start_point, label=self._focus_decl.get('label'),
                            desc=self._focus_decl.get('desc'), name_uid=got)
                    if spec.get('park'):
                        # A REAL parked car (docs §8 'Parked car'): the sim
                        # puts it into the nav's parked state - engine off,
                        # no route, it stays put. A car that merely has no
                        # throttle is not parked: it still has a route and
                        # may roll off, so the obstruction the scenario tests
                        # would be a coincidence, not a state.
                        requests.post(f"{API_URL}/park", json={'uid': got})
                    if spec.get('throttle'):
                        throttle_uids.append(got)
                    print(f"   👤 extra car uid {got} "
                          f"({'driving' if spec.get('throttle') else 'PARKED'})")
                st0 = self.get_state()
                crash_base_extra = {c['car_uid']: c.get('crash_episodes', 0)
                                    for c in st0.get('cars', [])}
                # Every teleport re-points the PRIMARY car (the one /state's
                # top level describes) to the newest spawn - a scenario that
                # monitors its own car has to say so after staging the extras.
                if primary_uid is not None:
                    requests.post(f"{API_URL}/follow", json={'uid': primary_uid})
                    # Commands are QUEUED and applied on the sim's tick loop,
                    # so reading the state right away can still describe the
                    # newest extra - every later baseline (start position,
                    # entry progress, violation counters) would then belong to
                    # the WRONG car (measured: the obstruction test read the
                    # parked car's pose as its own start). Poll for the
                    # re-point instead of assuming it landed.
                    for _ in range(60):
                        if (self.get_state() or {}).get('car_uid') == primary_uid:
                            break
                        time.sleep(0.05)
        else:
            print("📍 Creating new car at random location...")
            self.create_car_at_random()
            self.set_hud_label(None)

        if watch:
            requests.post(f"{API_URL}/freeze", json={'frozen': False})

        if _aborted():
            return self._aborted_result(start_point, direction, target_speed, results)
        
        # If the game dies during setup (window closed, or the teleport
        # watchdog crashing the process right after the teleport), treat it
        # exactly like a crash mid-monitor: record game_crashed for this
        # scenario and skip straight to the summary instead of raising.
        setup_crashed = False
        setup_aborted = False
        # Baseline for the cumulative validator log - taken IMMEDIATELY
        # after the teleport, BEFORE the accelerate phase: a spawn that
        # sits off the pavement (e.g. chord vs corner-rounded road) would
        # otherwise log its violations during setup and be baked into the
        # baseline, making them invisible to the monitor's delta check.
        violations_at_start = 0
        ws_frames_at_start = 0
        ws_secs_at_start = 0.0
        cross_at_start = 0
        flow_at_start = 0.0
        sim_at_start = 0.0
        try:
            state = self.get_state()
            initial_segment = state['segment']
            initial_pos = (state['x'], state['y'])
            initial_heading = state['heading']
            violations_at_start = state.get('validator_violations', 0)
            ws_frames_at_start = (state.get('lane_guard_stats') or {}) \
                .get('wrong_side_frames', 0)
            ws_secs_at_start = (state.get('lane_guard_stats') or {}) \
                .get('wrong_side_seconds', 0.0)
            # Quality metrics baselines (docs §8 "Liveness & quality"): the
            # counters are lifetime values, so a scenario reports its OWN
            # delta - centreline crossings and the fleet flow over the
            # scenario window.
            cross_at_start = (state.get('lane_guard_stats') or {}) \
                .get('centreline_crossings', 0)
            flow_at_start = state.get('flow_m_total') or 0.0
            sim_at_start = state.get('time') or 0.0
            print(f"   Starting at segment {initial_segment}")
            print(f"   Position: ({state['x']:.0f}, {state['y']:.0f})")
            print(f"   Heading: {state['heading']:.1f}°")

            # Green start flag at exactly where this scenario begins, and
            # the RED end flag at the EXPECTED destination: [segment, 50%
            # progress] - the game loop resolves that to a map position
            # once the route covers the segment, so the end marker is
            # visible from the START of the test (and sits on the right of
            # the road even for backward-traversed segments). Setting 'red'
            # also replaces any stale end flag from a previous scenario;
            # random-location runs have no known destination.
            try:
                # Parking tests: the flag (50% of the end segment) is the
                # car's NAVIGATION destination - it parks at it. Running
                # turn tests: visual-only marker at end_flag_progress of
                # the end segment (suite standard: FIRST 20%) - crossing
                # it ends the test, no parking.
                red_flag = None
                flag_point = None   # metres, position of the red flag (reach check)
                if expected_end_segment is not None:
                    prog_flag = 0.5 if kerb_check else end_flag_progress
                    seg = self.get_segment_geometry(expected_end_segment)
                    if seg is not None:
                        # Running tests: measure the flag from the NODE SIDE
                        # of the exit arm - where the car actually enters
                        # it. Some arms are parameterised the other way
                        # round (crossroads seg 17: a right-turning car
                        # enters at its 100% end), and a raw-parameterisation
                        # flag then sits where the car drives AWAY from -
                        # the old 'prog >= threshold' latch fired on entry,
                        # ending the test before the maneuver was even done
                        # (tests 8 + 16: "passed" without reaching the flag).
                        # Parking tests keep their explicit 50% destination.
                        if not kerb_check:
                            if expected_end_segment == initial_segment:
                                # SAME-segment scenario (the obstruction tests
                                # drive along one stretch and finish further
                                # along it): which side the car enters from is
                                # NOT a guess here - the spawn says it, via the
                                # car's own direction of travel. The nearest-
                                # endpoint heuristic below picks the wrong side
                                # when the spawn sits mid-segment, which put
                                # the flag at 45% for a destination stated at
                                # 85% (a run that "passed" 42 m short of its
                                # goal, 2026-10-19).
                                if not state.get('forward', True):
                                    prog_flag = 1.0 - prog_flag
                            else:
                                d0 = ((seg['x1'] - state['x']) ** 2
                                      + (seg['y1'] - state['y']) ** 2)
                                d1 = ((seg['x2'] - state['x']) ** 2
                                      + (seg['y2'] - state['y']) ** 2)
                                if d1 < d0:   # car enters at the far end
                                    prog_flag = 1.0 - prog_flag
                        flag_point = (seg['x1'] + prog_flag * (seg['x2'] - seg['x1']),
                                      seg['y1'] + prog_flag * (seg['y2'] - seg['y1']))
                    else:
                        print("   ⚠️  Could not resolve the end segment's "
                              "geometry - the flag-reach check is disabled, "
                              "this scenario will time out")
                    red_flag = [expected_end_segment, prog_flag]
                requests.post(f"{API_URL}/flags", json={
                    'green': [state['x'], state['y'], state['heading']],
                    'red': red_flag,
                    # The red flag is a NAV destination only where the scenario
                    # actually parks (`kerb_check`); for every other flagged
                    # scenario it is a VISUAL marker - the test ends when the
                    # car passes it (binding user decision 2026-10-25: passing
                    # the red flag at any speed ends the test, the car may keep
                    # driving on at full or intended speed; ONLY the parking
                    # scenario requires a complete standstill AT the flag, and
                    # a destination here would make the nav stage a parking
                    # manoeuvre inside a turn test).
                    'red_nav': bool(kerb_check),
                }, timeout=2)
            except requests.exceptions.RequestException:
                pass

            # Accelerate to target speed (no blinker yet - we signal only
            # once we are actually close to the junction, like a real driver)
            print(f"🚗 Accelerating to {target_speed:.0f} km/h...")
            self.send_control(accelerate=True)
            
            # Wait to reach speed
            for _ in range(50):  # 5 seconds max
                if _aborted():
                    break
                state = self.get_state()
                if state['speed_kmh'] >= target_speed * 0.9:
                    break
                self._psleep(0.1)
            
            print(f"   Reached {state['speed_kmh']:.0f} km/h")
            
            if _aborted():
                setup_aborted = True
            elif direction != 'straight':
                # Wait until we are SIGNAL_DISTANCE_M or less before the
                # junction, then signal the turn.
                blinker_key = 'blinker_left' if direction == 'left' else 'blinker_right'
                dist = state.get('distance_to_junction')
                for _ in range(100):  # 10 seconds max
                    if _aborted():
                        break
                    if dist is not None and dist <= SIGNAL_DISTANCE_M:
                        break
                    state = self.get_state()
                    dist = state.get('distance_to_junction')
                    self._psleep(0.1)
                if dist is not None and dist <= SIGNAL_DISTANCE_M:
                    self.send_control(**{blinker_key: True})
                    print(f"   {direction.upper()} blinker activated at {dist:.0f} m before the junction")
                else:
                    print(f"   ⚠️  Never got within {SIGNAL_DISTANCE_M:.0f} m of the junction - no blinker sent")
        except requests.exceptions.RequestException as e:
            setup_crashed = True
            initial_segment = -1
            initial_pos = (0.0, 0.0)
            initial_heading = 0.0
            state = {'x': 0.0, 'y': 0.0, 'heading': 0.0, 'segment': -1,
                     'speed_kmh': 0.0, 'on_road': True}
            print(f"\n   ❌ GAME PROCESS CRASHED / CONNECTION LOST during setup!")
            print(f"      Likely cause: an internal teleportation-watchdog violation "
                  f"(see the game's own console output/log)")
            print(f"      Error: {e}")
        if setup_aborted:
            return self._aborted_result(start_point, direction, target_speed, results)
        # WHERE the destination is on the target segment: the red flag is
        # placed at end_flag_progress, so "arrived" must mean the car crossed
        # THAT mark - a hardcoded 0.5 let a scenario whose flag sits at 85%
        # pass while the car was still stuck at half the segment (measured on
        # the obstruction test: the car crept past 50% behind a parked car
        # and the suite called it an arrival, 35 m short of its destination).
        arrival_prog = 0.5 if kerb_check else end_flag_progress
        if expected_end_segment is not None:
            print(f"   Expected end segment: {expected_end_segment}"
                  f" - destination at {arrival_prog * 100:.0f}% of it")
        print(f"\n🔍 Monitoring turn for {duration}s...")
        print(f"   Checking for:")
        print(f"     - Off-road violations")
        print(f"     - Instant heading snaps (>30° per frame)")
        print(f"     - Smooth circular arc progression")
        print(f"     - Arriving at the {'expected' if expected_end_segment is not None else 'designated'} end segment")
        
        # Monitor turn
        start_time = time.time()
        frames_checked = 0
        segment_changed = False
        reached_expected_segment = False
        # Progress along the target segment when the car FIRST entered it -
        # tells us which side of the 50% mark it has to cross.
        end_entry_progress: float | None = None
        passed_flag = False          # crossed the flag while moving (violation)
        flag_crossed = False         # the body reached the pennant's line (arrival)
        mark_seen = False            # /state stated the mark at least once
        no_mark_warned = False       # the old centreline fallback, printed once
        passed_flag_speed_kmh = 0.0
        off_road_detected = False
        instant_snap_detected = False
        teleport_detected = False
        game_crashed = False
        wrong_side_detected = False
        stopped_ok = False
        # Multi-car scenarios: contact between ANY pair in the world (the
        # primary's own crash check cannot see a second car hitting a third),
        # and when an extra reached its expected end segment (the priority
        # verdict: who got the single-file passage first).
        collision_detected = False
        extra_arrival: dict[int, float] = {}
        # SIM time at which the PRIMARY car reached its destination - the
        # priority verdict compares it against an extra's arrival.
        primary_arrival_sim: float | None = None
        braking_for_end = False
        violation_details = None
        positions = []
        # (violations_at_start was captured right after the teleport -
        # see the setup block above.)
        final_pos = initial_pos
        last_heading = initial_heading
        max_heading_change_per_frame = 0.0
        last_poll_pos = initial_pos
        last_poll_speed_kmh = 0.0
        last_poll_time = time.time()
        last_poll_sim = None
        
        if setup_crashed:
            game_crashed = True
            violation_details = {
                'type': 'game_crashed',
                'time': 0.0,
                'error': 'connection lost during setup (teleport/accelerate phase)',
            }
        
        while not setup_crashed and not _aborted() \
                and time.time() - start_time < duration:
            # A teleport-watchdog violation inside the game crashes that
            # process outright (a deliberate hard invariant - see
            # PhysicsValidator / docs/SPEC.md's "Physics Judge"
            # philosophy) - which would otherwise take the WHOLE test
            # suite down with an unhandled connection error. Treat losing
            # the connection mid-test as its own violation (failing just
            # this one test) instead of letting it kill everything after.
            try:
                state = self.get_state()
            except requests.exceptions.RequestException as e:
                game_crashed = True
                violation_details = {
                    'type': 'game_crashed',
                    'time': time.time() - start_time,
                    'error': str(e),
                }
                print(f"\n   ❌ GAME PROCESS CRASHED / CONNECTION LOST!")
                print(f"      Time: {violation_details['time']:.2f}s")
                print(f"      Likely cause: an internal teleportation-watchdog violation "
                      f"(see the game's own console output/log)")
                print(f"      Error: {e}")
                break
            frames_checked += 1
            current_heading = state['heading']

            # --- multi-car extras -----------------------------------------
            # Throttle for the extras that are supposed to drive; a PARKED
            # car is left alone (no signal = it stays where it was put).
            for uid in throttle_uids:
                try:
                    requests.post(f"{API_URL}/control",
                                  json={'uid': uid, 'accelerate': True},
                                  timeout=2)
                except requests.exceptions.RequestException:
                    pass
            for c in state.get('cars') or []:
                if c.get('in_contact'):
                    collision_detected = True
                elif c.get('crash_episodes', 0) > crash_base_extra.get(
                        c['car_uid'], c.get('crash_episodes', 0)):
                    collision_detected = True
            for spec, uid in zip(extra_cars or [], extra_uids):
                exp = spec.get('expect_end')
                if exp is None or uid in extra_arrival:
                    continue
                if any(cc['car_uid'] == uid and cc.get('segment_name') == exp
                       for cc in state.get('cars') or []):
                    extra_arrival[uid] = state.get('time') or 0.0
            
            # Client-side teleport/jump check: an independent, coarser
            # safety net alongside the game's own internal (per-physics-
            # frame) teleportation watchdog - catches large jumps between
            # POLLS too, using the actual measured wall-clock gap (not an
            # assumed fixed frame dt, since polling isn't frame-locked).
            now = time.time()
            poll_dt = _poll_dt(state, last_poll_sim, now, last_poll_time)
            moved_m = ((state['x'] - last_poll_pos[0]) ** 2 +
                       (state['y'] - last_poll_pos[1]) ** 2) ** 0.5
            # Generous margin: highest plausible speed (~50 m/s cruise)
            # times the actual poll gap, plus slack for polling jitter
            # and the accelerate-from-a-stop ramp-up.
            max_plausible_m = max(last_poll_speed_kmh / 3.6, 50.0) * poll_dt * 1.5 + 1.0
            if frames_checked > 1 and moved_m > max_plausible_m:
                teleport_detected = True
                violation_details = {
                    'type': 'teleport',
                    'time': now - start_time,
                    'from_position': last_poll_pos,
                    'to_position': (state['x'], state['y']),
                    'distance_m': moved_m,
                    'max_plausible_m': max_plausible_m,
                    'speed_kmh': state['speed_kmh'],
                    'segment': state['segment'],
                }
                print(f"\n   ❌ TELEPORTATION / UNEXPECTED JUMP DETECTED!")
                print(f"      Time: {violation_details['time']:.2f}s")
                print(f"      From: ({last_poll_pos[0]:.0f}, {last_poll_pos[1]:.0f}) "
                      f"→ To: ({state['x']:.0f}, {state['y']:.0f})")
                print(f"      Distance: {moved_m:.1f}m (max plausible: {max_plausible_m:.1f}m "
                      f"over {poll_dt:.3f}s)")
                print(f"      Speed: {violation_details['speed_kmh']:.0f} km/h")
                break
            last_poll_pos = (state['x'], state['y'])
            last_poll_speed_kmh = state['speed_kmh']
            last_poll_time = now
            last_poll_sim = state.get('time', last_poll_sim)
            
            # Calculate heading change (handle 360° wrap). Skip the very
            # first poll: `last_heading` was initialised to the TELEPORT
            # heading, but the car has already driven (and turned smoothly)
            # during the unmonitored "accelerate to speed" phase, so
            # comparing the first poll to the teleport heading would flag a
            # legitimate accumulated turn as an instant snap. The teleport/
            # jump position check below already skips the first poll for the
            # same reason (frames_checked > 1).
            if frames_checked > 1:
                heading_diff = abs((current_heading - last_heading + 180) % 360 - 180)
                max_heading_change_per_frame = max(max_heading_change_per_frame, heading_diff)
            else:
                heading_diff = 0.0
            
            # Record position
            positions.append({
                'time': time.time() - start_time,
                'x': state['x'],
                'y': state['y'],
                'heading': state['heading'],
                'heading_change': heading_diff,
                'speed_kmh': state['speed_kmh'],
                'segment': state['segment'],
                'on_road': state['on_road']
            })
            
            # Check for instant heading snap (>30° in one frame at 60fps = 0.016s)
            if frames_checked > 1 and heading_diff > 30.0:
                instant_snap_detected = True
                violation_details = {
                    'type': 'instant_heading_snap',
                    'time': time.time() - start_time,
                    'position': (state['x'], state['y']),
                    'old_heading': last_heading,
                    'new_heading': current_heading,
                    'heading_change': heading_diff,
                    'speed_kmh': state['speed_kmh'],
                    'segment': state['segment'],
                    'frame': frames_checked
                }
                
                print(f"\n   ❌ INSTANT HEADING SNAP DETECTED!")
                print(f"      Time: {violation_details['time']:.2f}s")
                print(f"      Old heading: {last_heading:.1f}°")
                print(f"      New heading: {current_heading:.1f}°")
                print(f"      Change: {heading_diff:.1f}° (max allowed: 30°)")
                print(f"      Position: ({violation_details['position'][0]:.0f}, {violation_details['position'][1]:.0f})")
                print(f"      Speed: {violation_details['speed_kmh']:.0f} km/h")

                
                break
            
            # Check for off-road violation (live check + validator log).
            # The validator's violation log is CUMULATIVE for the whole game
            # process and is never cleared, so it must be compared against
            # its value when this scenario started - not against zero. Doing
            # the latter latched: one violation anywhere made every later
            # scenario report off-road at t=0.00s, turning a single real
            # failure into thirteen false ones.
            if not state['on_road'] or \
                    state.get('validator_violations', 0) > violations_at_start:
                off_road_detected = True
                violation_details = {
                    'type': 'off_road',
                    'time': time.time() - start_time,
                    'position': (state['x'], state['y']),
                    'heading': state['heading'],
                    'speed_kmh': state['speed_kmh'],
                    'segment': state['segment'],
                    'frame': frames_checked
                }
                
                print(f"\n   ❌ OFF-ROAD VIOLATION DETECTED!")
                print(f"      Time: {violation_details['time']:.2f}s")
                print(f"      Position: ({violation_details['position'][0]:.0f}, {violation_details['position'][1]:.0f})")
                print(f"      Heading: {violation_details['heading']:.1f}°")
                print(f"      Speed: {violation_details['speed_kmh']:.0f} km/h")
                print(f"      Segment: {violation_details['segment']}")

                
                break
            
            # Wrong-side driving (oncoming lane): the game only WARNS on a
            # violation (state flag + cumulative lane-guard stats); failing
            # the run is the suite's job (docs/TESTING.md §3). /state
            # reports 'wrong_side' on every frame the car sits on the wrong
            # side, so this poll catches it live; the cumulative counter
            # below is the backstop for hits between polls.
            if state.get('wrong_side'):
                wrong_side_detected = True
                violation_details = {
                    'type': 'wrong_side',
                    'time': time.time() - start_time,
                    'position': (state['x'], state['y']),
                    'heading': state['heading'],
                    'speed_kmh': state['speed_kmh'],
                    'segment': state['segment'],
                    'frame': frames_checked
                }
                
                print(f"\n   ❌ WRONG-SIDE DRIVING DETECTED!")
                print(f"      Time: {violation_details['time']:.2f}s")
                print(f"      Position: ({state['x']:.0f}, {state['y']:.0f})")
                print(f"      Heading: {state['heading']:.1f}°")
                print(f"      Speed: {state['speed_kmh']:.0f} km/h")
                print(f"      Segment: {state['segment']}")

                
                break
            
            # RUNNING TURN TEST arrival (docs/TESTING.md): the scenario is
            # done when the car physically drives THROUGH the red end flag.
            # Geometric, not 'prog >= threshold' - the old latch fired on
            # the first poll for arms the car enters "from behind" (reverse-
            # parameterised exits: crossroads right turn, hairpin exit),
            # ending the test before the maneuver was even finished and
            # never actually reaching the flag. Checked BEFORE the segment-
            # change gate on purpose: near a node the reported segment can
            # still be the approach arm while the car already passes the
            # flag point (hairpin: its projection onto the exit arm is
            # closer than the diagonal it really drives).
            # RUNNING TURN TEST arrival (docs/TESTING.md §3, binding user
            # decision 2026-10-25): the SIM states the mark - `flags.red_seg`/
            # `red_prog` (segment + progress along the route) and `flags.red`
            # (the pennant's FOOT, placed in the line the car has to arrive in:
            # `Config.LaneBaseOffsetM` for a running test). The scenario is done
            # the moment the car's BODY reaches that line - it drove AGAINST the
            # flag. No radius at all: a 3 m circle around a point in a ~2.6 m
            # lane reaches into the next lane, and the old code measured a point
            # on the CENTRELINE while the car drove 1.7 m off it, so the real
            # along-window was only ±2.4 m - and on the 19.4 m avenue
            # (pkw_n→pkw_s, lateral 7.27 m) the circle could never be reached
            # at all. The lane condition below (≤ 2 m perpendicular) is what
            # keeps a car on a DIFFERENT road from counting as an arrival.
            fl = state.get('flags') or {}
            mseg, mprog = fl.get('red_seg'), fl.get('red_prog')
            mark = fl.get('red')
            if not kerb_check and mseg is not None and mseg >= 0 \
                    and mprog is not None and mprog >= 0 and mark:
                mark_seen = True
                fh = math.radians(float(mark[2]))
                tdir = (math.sin(fh), math.cos(fh))       # route direction (0°=N)
                cs = state.get('body_corners') or [[state['x'], state['y']]]
                along = [(p[0] - mark[0]) * tdir[0] + (p[1] - mark[1]) * tdir[1]
                         for p in cs]
                perp = [abs((p[0] - mark[0]) * -tdir[1] + (p[1] - mark[1]) * tdir[0])
                        for p in cs]
                front_s = max(along)
                in_lane = min(perp) <= FLAG_LANE_HALF_TOL_M
                if front_s >= 0.0 and in_lane and not flag_crossed:
                    flag_crossed = True
                    reached_expected_segment = True
                    primary_arrival_sim = state.get('time') or 0.0
                    prog_pct = (state.get('progress') or 0.0) * 100
                    print(f"\n   ✅ Reached the end flag - the car's front is "
                          f"{front_s:.2f} m past the pennant's foot on segment "
                          f"{state['segment']} (at {prog_pct:.0f}%, target: "
                          f"{end_flag_progress * 100:.0f}%)")
                    print(f"      Time: {time.time() - start_time:.2f}s")
                    print(f"      Max heading change per frame: {max_heading_change_per_frame:.1f}°")
                    final_pos = (state['x'], state['y'])
                    stopped_ok = True   # running test: passing the mark ends it
                    print(f"      Start: ({initial_pos[0]:.0f}, {initial_pos[1]:.0f}) seg {initial_segment} "
                          f"→ End: ({final_pos[0]:.0f}, {final_pos[1]:.0f}) seg {state['segment']}")
                    print(f"      Distance traveled: {((final_pos[0] - initial_pos[0])**2 + (final_pos[1] - initial_pos[1])**2)**0.5:.0f} m")
                    try:
                        self.reset_controls()   # release the throttle latch
                    except requests.exceptions.RequestException:
                        pass
                    break
            elif not kerb_check and flag_point is not None:
                # FALLBACK only - and it belongs to the END of the run, not to
                # the first polls: the sim resolves the flag once the ROUTE
                # covers that segment, so early polls legitimately carry no
                # `red_seg` yet (measured on T_273: this ⚠️ printed while the
                # new mark-based test still fired 0.09 m later in the same run).
                # Warning here said "the host is old" when it was only "not
                # resolved yet". The real fact is checked after the loop.
                fdx = state['x'] - flag_point[0]
                fdy = state['y'] - flag_point[1]
                if (fdx * fdx + fdy * fdy) ** 0.5 <= FLAG_REACH_RADIUS_M:
                    reached_expected_segment = True
                    primary_arrival_sim = state.get('time') or 0.0
                    prog_pct = (state.get('progress') or 0.0) * 100
                    print(f"\n   ✅ Crossed the end flag at {prog_pct:.0f}% "
                          f"of segment {state['segment']} "
                          f"(target: {end_flag_progress * 100:.0f}%, node side)")
                    print(f"      Time: {time.time() - start_time:.2f}s")
                    print(f"      Max heading change per frame: {max_heading_change_per_frame:.1f}°")
                    final_pos = (state['x'], state['y'])
                    stopped_ok = True   # running test: no stop required
                    print(f"      Start: ({initial_pos[0]:.0f}, {initial_pos[1]:.0f}) seg {initial_segment} "
                          f"→ End: ({final_pos[0]:.0f}, {final_pos[1]:.0f}) seg {state['segment']}")
                    print(f"      Distance traveled: {((final_pos[0] - initial_pos[0])**2 + (final_pos[1] - initial_pos[1])**2)**0.5:.0f} m")
                    try:
                        self.reset_controls()
                    except requests.exceptions.RequestException:
                        pass
                    break
            
            # Check if segment changed. If we have a SPECIFIC expected
            # end segment, only that counts as arrival - a mere change to
            # some OTHER segment (e.g. a wrong turn) is noted but keeps
            # monitoring (the car might still be mid-maneuver, as on a
            # roundabout: entry segment -> ring -> ring -> exit segment -
            # only the last of those is "done"). Without an expected
            # segment (legacy random-location mode only), any change at
            # all is accepted, same as before.
            # The parking-offset scenarios START and END on the same
            # segment (the car parks where it was driving), so "segment
            # changed" can never be their arrival trigger - being ON the
            # expected segment is enough.
            on_target_segment = (expected_end_segment is not None
                                 and state['segment'] == expected_end_segment)
            if state['segment'] != initial_segment or on_target_segment:
                if state['segment'] != initial_segment and not segment_changed:
                    segment_changed = True
                    print(f"\n   ℹ️  Segment changed: {initial_segment} → {state['segment']} "
                          f"(t={time.time() - start_time:.2f}s)")
                if on_target_segment:
                    # Deterministic scenarios END at the 50% point of the
                    # target segment (mid-segment, mirroring the start) -
                    # the car does NOT drive on to the far end / dead end,
                    # because parking is a separate maneuver with its own
                    # scenario, not the tail of every turn test.
                    if end_entry_progress is None:
                        end_entry_progress = state.get('progress')
                    prog = state.get('progress')
                    entry = end_entry_progress
                    if not kerb_check:
                        # RUNNING TURN TEST (docs/TESTING.md): arrival is
                        # handled by the geometric flag check above - the
                        # car drives THROUGH the red flag and keeps going,
                        # no stop, no parking. This guard exists so the
                        # STOP-AT-FLAG logic below only applies to parking
                        # (kerb_check) scenarios.
                        pass
                    crossed_now = (prog is not None and entry is not None and (
                        (entry < arrival_prog and prog >= arrival_prog)
                        or (entry > arrival_prog
                            and prog <= arrival_prog)))
                    # Reverse-in parking (§1b): the car deliberately drives
                    # PAST the flag to stage the back-in, stops ~3 m beyond
                    # it, then reverses into the spot at the kerb. The
                    # crossing is expected and the staging stop is NOT an
                    # arrival - only the nav's 'parked' flag (manoeuvre
                    # complete) counts.
                    parking = state.get('parking') or {}
                    reverse_planned = parking.get('style') == 'reverse'
                    reverse_done = not reverse_planned \
                        or bool(parking.get('parked'))
                    # STRICT arrival criterion (docs/TESTING.md §3, rule 1):
                    # the scenario passes only if the car comes to rest AT
                    # the flag. Crossing it while moving is "drove past the
                    # destination" - a FAILURE, not an arrival (see
                    # FLAG_CRAWL_KMH for the crawl-speed exception). A
                    # planned reverse-in crosses on purpose - exempt until
                    # it has parked.
                    if crossed_now and state['speed_kmh'] >= FLAG_CRAWL_KMH \
                            and not reverse_planned:
                        passed_flag = True
                        passed_flag_speed_kmh = state['speed_kmh']
                        print(f"\n   ❌ DROVE PAST THE END FLAG at "
                              f"{state['speed_kmh']:.0f} km/h - the scenario "
                              f"requires a STOP AT the flag")
                    crossed_half = (crossed_now and state['speed_kmh'] < FLAG_CRAWL_KMH) \
                        or (prog is not None and entry is not None
                            and abs(entry - arrival_prog) < 0.02)
                    if crossed_half and not reverse_done:
                        crossed_half = False   # staging, not arrival
                    if passed_flag:
                        final_pos = (state['x'], state['y'])
                        print(f"      Time: {time.time() - start_time:.2f}s")
                        print(f"      Max heading change per frame: {max_heading_change_per_frame:.1f}°")
                        break
                    # SAFETY NET only: the nav parks at the red flag itself
                    # (it is the car's destination - parking ramp + kerb
                    # drift + stop, see BicycleNav.set_destination). This
                    # latch only fires if the car is still rolling toward
                    # the flag within braking distance, i.e. the nav's park
                    # approach failed.
                    seg_len = state.get('segment_length')
                    if prog is not None and seg_len:
                        dist_flag_m = abs(prog - arrival_prog) * seg_len
                        speed_ms = state['speed_kmh'] / 3.6
                        brake_dist_m = (speed_ms ** 2) / (2.0 * CAR_BRAKING) \
                            + max(1.0, speed_ms * 0.15)   # polling/reaction slack
                        # (Never during a reverse-in staging approach - the
                        # car is supposed to keep rolling past the flag.)
                        if not braking_for_end and not crossed_half \
                                and reverse_done \
                                and dist_flag_m <= brake_dist_m:
                            braking_for_end = True
                            self.send_control(accelerate=False, brake=True)
                            print(f"\n   🛑 Braking for the end flag "
                                  f"({dist_flag_m:.0f} m ahead)")
                        # Arrival = at rest within tolerance of the flag,
                        # regardless of who latched the brake (normally the
                        # nav's parking block, sometimes the safety net).
                        # A reverse-in only counts once 'parked' - the
                        # staging stop ~3 m past the flag must not latch.
                        if not crossed_half \
                                and reverse_done \
                                and state['speed_kmh'] < 1.0 \
                                and dist_flag_m <= STOP_AT_FLAG_TOLERANCE_M:
                            crossed_half = True   # stopped right at the flag
                    if crossed_half:
                        reached_expected_segment = True
                        primary_arrival_sim = state.get('time') or 0.0
                        print(f"\n   ✅ Reached end segment {state['segment']} "
                              f"at {prog * 100:.0f}% (target: 50%)")
                        print(f"      Time: {time.time() - start_time:.2f}s")
                        print(f"      Max heading change per frame: {max_heading_change_per_frame:.1f}°")
                        final_pos = (state['x'], state['y'])
                        # (The red end flag has been up since setup - it
                        # marks exactly this destination.) The scenario is
                        # DONE here: come to a full stop AT the flag and
                        # hold the brake, like a driver parking at their
                        # destination.
                        cross_pos = final_pos
                        final_pos, stopped_ok, stop_details = self._brake_to_stop(
                            start_time, abort_check=abort_check)
                        if stop_details.get('aborted'):
                            return self._aborted_result(start_point, direction, target_speed, results)
                        if not stopped_ok:
                            off_road_detected = off_road_detected or stop_details.get('off_road', False)
                            instant_snap_detected = instant_snap_detected or stop_details.get('instant_snap', False)
                            teleport_detected = teleport_detected or stop_details.get('teleport', False)
                            game_crashed = game_crashed or stop_details.get('game_crashed', False)
                            if stop_details.get('violation_details'):
                                violation_details = stop_details['violation_details']
                        rolled_m = ((final_pos[0] - cross_pos[0]) ** 2 + (final_pos[1] - cross_pos[1]) ** 2) ** 0.5
                        # THE PARKING FINISH, on the mark the SIM states (binding
                        # decision 2026-10-25: with reverse-in ordered, the car
                        # first drives PAST the flag, sets back, then pulls
                        # forward until its NOSE touches the pennant's foot -
                        # `NOSE_TOUCH_M` at rest). The 8 m centre-tolerance above
                        # stays as the arrival gate (it decides WHO is at the
                        # flag at all); this is what decides whether the parking
                        # job actually finished at its mark.
                        try:
                            st_end = self.get_state()
                        except requests.RequestException:
                            st_end = None
                        fl_p = (st_end or state).get('flags') or {}
                        mk = fl_p.get('red')
                        if mk and st_end:
                            fh = math.radians(float(mk[2]))
                            tdir = (math.sin(fh), math.cos(fh))
                            cs = st_end.get('body_corners') or [[st_end['x'], st_end['y']]]
                            nose = max((p[0] - mk[0]) * tdir[0] + (p[1] - mk[1]) * tdir[1]
                                       for p in cs)
                            perp = min(abs((p[0] - mk[0]) * -tdir[1] + (p[1] - mk[1]) * tdir[0])
                                       for p in cs)
                            if stopped_ok and (abs(nose) > NOSE_TOUCH_M or
                                               perp > FLAG_LANE_HALF_TOL_M):
                                stopped_ok = False
                                print(f"      ⚠️  nose {nose:+.2f} m off the mark "
                                      f"(perp {perp:.2f} m) - outside the "
                                      f"±{NOSE_TOUCH_M:.2f} m bumper touch")
                            elif stopped_ok:
                                print(f"      ✅ nose touches the mark "
                                      f"({nose:+.2f} m, perp {perp:.2f} m)")
                        if stopped_ok:
                            where = (f"{rolled_m:.1f} m after crossing it"
                                     if rolled_m > 0.5 else "right at the flag")
                            print(f"      🅿️  Stopped at the end flag ({where})")
                        else:
                            print(f"      ⚠️  Did NOT come to a clean stop at the end flag")
                        print(f"      Start: ({initial_pos[0]:.0f}, {initial_pos[1]:.0f}) seg {initial_segment} "
                              f"\u2192 End: ({final_pos[0]:.0f}, {final_pos[1]:.0f}) seg {state['segment']}")
                        print(f"      Distance traveled: {((final_pos[0] - initial_pos[0])**2 + (final_pos[1] - initial_pos[1])**2)**0.5:.0f} m")
                        break
                elif expected_end_segment is None:
                    # Legacy random-location mode: any segment change counts;
                    # drive to the far end of it and stop there.
                    reached_expected_segment = True
                    primary_arrival_sim = state.get('time') or 0.0
                    print(f"\n   ✅ Reached designated end segment {state['segment']}!")
                    print(f"      Time: {time.time() - start_time:.2f}s")
                    print(f"      Max heading change per frame: {max_heading_change_per_frame:.1f}°")
                    final_pos, stopped_ok, stop_details = self._drive_to_segment_end_and_stop(
                        state['segment'], start_time, abort_check=abort_check)
                    if stop_details.get('aborted'):
                        return self._aborted_result(start_point, direction, target_speed, results)
                    if not stopped_ok:
                        off_road_detected = off_road_detected or stop_details.get('off_road', False)
                        instant_snap_detected = instant_snap_detected or stop_details.get('instant_snap', False)
                        teleport_detected = teleport_detected or stop_details.get('teleport', False)
                        game_crashed = game_crashed or stop_details.get('game_crashed', False)
                        if stop_details.get('violation_details'):
                            violation_details = stop_details['violation_details']
                    print(f"      Start: ({initial_pos[0]:.0f}, {initial_pos[1]:.0f}) seg {initial_segment} "
                          f"\u2192 End: ({final_pos[0]:.0f}, {final_pos[1]:.0f}) seg {state['segment']}"
                          f"{' (stopped)' if stopped_ok else ' (did NOT stop cleanly)'}")
                    print(f"      Distance traveled: {((final_pos[0] - initial_pos[0])**2 + (final_pos[1] - initial_pos[1])**2)**0.5:.0f} m")
                    break
            
            last_heading = current_heading
            self._psleep(0.05)  # Check at ~20 FPS
        else:
            final_pos = (state['x'], state['y'])

        if watch:
            requests.post(f"{API_URL}/freeze", json={'frozen': False})

        if _aborted():
            return self._aborted_result(start_point, direction, target_speed, results)

        # Scenario end: on a clean deterministic finish the brake is
        # deliberately HELD - the car stays parked AT the end flag, like a
        # driver who has reached their destination (the next scenario's
        # reset_controls() clears the latch). Every other exit (failure,
        # timeout, legacy mode) gets its inputs cleared here.
        try:
            if not (expected_end_segment is not None and reached_expected_segment
                    and stopped_ok):
                self.reset_controls()
        except requests.exceptions.RequestException:
            pass
        
        # Parking-offset scenarios (kerb_check): the arrival above only
        # proves the car stopped AT the flag - the pass criterion is how
        # CLOSE to the right kerb it parked: both right-hand wheels within
        # KERB_PASS_MAX_M. Measured on a fresh state after the final stop.
        kerb_gaps = None
        if kerb_check and reached_expected_segment and stopped_ok:
            try:
                kerb_gaps = self._right_side_kerb_gaps_m(
                    self.get_state(), start_point)
            except requests.exceptions.RequestException:
                kerb_gaps = None
        kerb_failed = (kerb_gaps is not None
                       and (max(kerb_gaps) > KERB_PASS_MAX_M
                            or min(kerb_gaps) <= 0.0))

        # Backstop for wrong-side hits that fell BETWEEN API polls: the
        # lane guard's frame counter is cumulative for the whole game
        # process, so compare it against its value when this scenario
        # started (same delta logic as validator_violations).
        if not wrong_side_detected:
            ws_now = (state.get('lane_guard_stats') or {}) \
                .get('wrong_side_frames', 0)
            if ws_now > ws_frames_at_start:
                wrong_side_detected = True
                print(f"\n   ❌ WRONG-SIDE DRIVING DETECTED (between polls: "
                      f"{ws_now - ws_frames_at_start} frame(s) on the wrong "
                      f"side per lane-guard stats)")
        
        # A scenario only passes if it ENDS with the car stopped at its
        # destination - the end flag for deterministic scenarios, the far
        # end of the target segment in legacy random mode. Driving on past
        # the destination is a different maneuver, not this test.
        # EXCEPTION: negative detector tests (expect_wrong_side) PASS when
        # the violation fires - they exist to prove the detection works.
        # --- multi-car verdicts (docs §8) --------------------------------
        # A contact between ANY pair in the world fails the scenario: the
        # primary's own crash check cannot see a second car hitting a third.
        # The priority scenarios additionally say which extra must have had
        # the passage FIRST - it has to reach its end before the primary,
        # because the car whose lane is blocked waits (R26/R32).
        collision_detected = collision_detected or any(
            c.get('in_contact') or
            c.get('crash_episodes', 0) > crash_base_extra.get(
                c['car_uid'], c.get('crash_episodes', 0))
            for c in [state] + (state.get('cars') or [])
            if c.get('car_uid') is not None)
        priority_failed = False
        if extra_cars and primary_arrival_sim is not None:
            for spec, uid in zip(extra_cars, extra_uids):
                if spec.get('expect_end') is None:
                    continue
                t = extra_arrival.get(uid)
                if t is None or not (t < primary_arrival_sim):
                    priority_failed = True
                    print(f"\n   ❌ PRIORITY: extra car {uid} did not get the "
                          f"passage before the primary "
                          f"(extra {t if t is not None else 'never'} s vs "
                          f"primary {primary_arrival_sim:.2f} s of sim time)")
        if expect_wrong_side:
            passed = wrong_side_detected
        else:
            passed = (
                reached_expected_segment
                and stopped_ok
                and not passed_flag
                and not off_road_detected
                and not instant_snap_detected
                and not teleport_detected
                and not game_crashed
                and not kerb_failed
                and not wrong_side_detected
                and not collision_detected
                and not priority_failed
            )
        
        # The run is over: if the whole run never stated a mark, the fallback
        # circle WAS used the entire time - that is a fact worth saying once,
        # here rather than in the early polls where "not yet resolved" is
        # normal (see the T_273 measurement in the block above).
        if not kerb_check and expected_end_segment is not None \
                and flag_point is not None and not mark_seen:
            print("   ⚠️  /state NEVER carried flags.red_seg/red_prog in this "
                  "run - the arrival used the old centreline circle "
                  f"(radius {FLAG_REACH_RADIUS_M:.0f} m) for the whole test")

        # Prepare results
        result = {
            'start_point': start_point,
            'direction': direction,
            'target_speed_kmh': target_speed,
            'frames_checked': frames_checked,
            'duration': time.time() - start_time,
            'initial_segment': initial_segment,
            'expected_end_segment': expected_end_segment,
            'final_segment': state['segment'],
            'start_position': initial_pos,
            'end_position': final_pos,
            'segment_changed': segment_changed,
            'reached_expected_segment': reached_expected_segment,
            'stopped_at_end': stopped_ok,
            'passed_flag': passed_flag,
            'passed_flag_speed_kmh': passed_flag_speed_kmh,
            'off_road_detected': off_road_detected,
            'instant_snap_detected': instant_snap_detected,
            'teleport_detected': teleport_detected,
            'game_crashed': game_crashed,
            'wrong_side_detected': wrong_side_detected,
            'expect_wrong_side': expect_wrong_side,
            'kerb_check': kerb_check,
            'end_flag_progress': end_flag_progress,
            'kerb_gaps_m': kerb_gaps,
            'max_heading_change_per_frame': max_heading_change_per_frame,
            # Quality metrics (docs §8): centreline crossings of the primary,
            # and the fleet's metres-per-second over the scenario window -
            # normalised to the cars that were SUPPOSED to drive (a parked
            # car contributes 0 m and must not dilute the number).
            'centreline_crossings': max(0, (state.get('lane_guard_stats') or {})
                                        .get('centreline_crossings', 0) - cross_at_start),
            'flow_m_per_s': ((state.get('flow_m_total') or 0.0) - flow_at_start) /
                            max((state.get('time') or 0.0) - sim_at_start, 1e-9),
            'driving_cars': 1 + len(throttle_uids),
            'world_cars': 1 + len(extra_uids),
            'collision_detected': collision_detected,
            'priority_failed': priority_failed,
            'extra_arrivals': extra_arrival,
            # For the run's results table: SIM time the scenario covered and
            # how many off-road hits the validator logged in THIS scenario
            # (the counter is a lifetime log, so it is the delta again).
            'sim_duration_s': max(0.0, (state.get('time') or 0.0) - sim_at_start),
            'off_road_hits': max(0, state.get('validator_violations', 0)
                                  - violations_at_start),
            'violation_details': violation_details,
            'positions': positions,
            'aborted': False,
            'passed': passed
        }
        
        # Summary
        print(f"\n{'─'*60}")
        print(f"   Frames checked: {frames_checked}")
        print(f"   Duration: {result['duration']:.2f}s")
        print(f"   Max heading change per frame: {result['max_heading_change_per_frame']:.1f}°")
        
        print(f"   Start: ({initial_pos[0]:.0f}, {initial_pos[1]:.0f}) seg {initial_segment}  "
              f"End: ({final_pos[0]:.0f}, {final_pos[1]:.0f}) seg {state['segment']}"
              + (f"  (expected seg {expected_end_segment})" if expected_end_segment is not None else ""))

        # Validator violations (off-road caught between API polls) -
        # PER-TEST delta against the post-spawn baseline: the game's
        # counter is a lifetime log, printing it raw read as "this test
        # logged N" when it was really everything since game start.
        vv = max(0, state.get('validator_violations', 0) - violations_at_start)
        if vv > 0:
            print(red(f"   ⚠️  Validator: {vv} off-road violation(s) logged"))

        # Lane guard stats (per-test delta, same reason)
        lg = state.get('lane_guard_stats', {})
        wrong_frames = max(0, lg.get('wrong_side_frames', 0) - ws_frames_at_start)
        wrong_secs = max(0.0, lg.get('wrong_side_seconds', 0.0) - ws_secs_at_start)
        if wrong_frames > 0:
            print(red(f"   ⚠️  Wrong-side driving: {wrong_frames} frames ({wrong_secs}s)"))
        else:
            print(green("   ✅ Lane guard: no wrong-side driving detected"))

        if kerb_gaps is not None:
            ok = not kerb_failed
            txt = (f"   Kerb check: right flank {kerb_gaps[1] * 100:.0f} cm "
                   f"(front) / {kerb_gaps[0] * 100:.0f} cm (rear) from the "
                   f"kerb (limit {KERB_PASS_MAX_M * 100:.0f} cm)")
            print(green(f"   ✅ {txt}") if ok else red(f"   ❌ {txt}"))

        # Colored one-line verdict: green "passed" or red "fail: <reason>".
        reason = describe_failure(result)
        if reason is None:
            print(green("   ✅ PASSED"))
            if result.get('expect_wrong_side'):
                print(dim("      Wrong-side driving occurred and was detected - "
                          "exactly what this negative detector test expects"))
            elif result.get('expected_end_segment') is None:
                print(dim("      Reached designated end segment, drove to its end and "
                          "stopped there, stayed on road, no violations"))
            else:
                print(dim("      Reached the 50% point of the expected end segment and "
                          "stopped at the end flag, stayed on road, no violations"))
        else:
            print(red(f"   ❌ FAIL: {reason}"))
        
        print(f"{'─'*60}\n")
        
        self.test_results.append(result)
        if results is not None:
            record_result(results, result, self.test_no)
            save_results(results)

        # The verdict is OUT (posted to the tape, appended, saved) - from here on
        # the car is only allowed to make the picture clearer, never the result
        # different. See _stop_after_verdict for why it is a lone-car epilogue.
        self._stop_after_verdict()

        return result
    
    def run_random_test(self, results: dict | None = None):
        """Run full test suite with multiple speeds and directions,
        teleporting to random locations on whatever map is loaded
        (real OSM data or a synthetic test map)."""
        print("\n" + "="*60)
        print("RANDOM-LOCATION TURN TESTING")
        print("="*60)
        print("\nTesting turns at different speeds:")
        print("  - Low speed:  30 km/h")
        print("  - Medium speed: 50 km/h")
        print("  - High speed: 80 km/h")
        print("\nEach test will:")
        print("  1. Teleport to random location")
        print("  2. Accelerate to target speed")
        print("  3. Activate turn signal")
        print("  4. Monitor turn execution")
        print("  5. Check for off-road violations")
        print("\n" + "="*60)
        
        tests = [
            # Low speed turns
            ('right', 30),
            ('left', 30),
            # Medium speed turns
            ('right', 50),
            ('left', 50),
            # High speed turns
            ('right', 80),
            ('left', 80),
        ]
        
        for i, (direction, speed) in enumerate(tests, 1):
            # User interrupted (Ctrl-C) or closed the game window: stop now.
            if interrupted():
                print(yellow(f"\n⏸️  Stopping after test {i-1}/{len(tests)} - "
                             f"run interrupted (Ctrl-C or game window closed)."))
                break

            print(f"\n\n{'#'*60}")
            print(f"# TEST {i}/{len(tests)}: {direction.upper()} turn at {speed} km/h")
            print(f"{'#'*60}")
            
            result = self.monitor_turn(direction, duration=15.0, target_speed=speed)
            
            # Brief pause between tests
            if i < len(tests):
                print("\n⏸️  Pausing 2s before next test...")
                time.sleep(2)
        
        # Final summary
        self.print_summary()
    
    def run_deterministic_test(self, results: dict | None = None, spec: str | None = None,
                               abort_check: "callable | None" = None,
                               take_jump: "callable | None" = None,
                               on_event: "callable | None" = None):
        """Run the turn test suite against KNOWN, reproducible scenarios
        from the 'basic' synthetic test map (see src/test_maps.py).
        Requires the game to be started with: --map basic --api

        Cockpit hooks (all optional; the CLI path uses none of them):
          spec:         scenario selection string for select_tests (e.g. "1-3")
          abort_check:  zero-arg callable, polled inside monitor_turn; if it
                        returns truthy the current scenario is aborted
          take_jump:    zero-arg callable, polled between scenarios; returns
                        the 1-based number of a scenario to start next and
                        consumes the request - set by a cockpit button click
          on_event:     callback for {'type': 'start'|'done'|'jump', ...}
                        events so a UI can follow the suite's progress
        """
        print("\n" + "="*60)
        print("DETERMINISTIC TURN TESTING (synthetic 'basic' map)")
        print("="*60)

        available = self.get_start_points()
        if not available:
            print("\n❌ No named start points reported by the API.")
            print("   Start the game with: python -m src.main --map basic --api\n")
            sys.exit(1)

        # Symbolic segment anchors (docs/DRIVING_MANEUVERS.md §8 "Segment
        # names"): a scenario says WHICH road it must end on
        # ("startNode→endNode"), and the suite resolves that to the map's
        # positional index once. Adding or deleting a track can therefore
        # never silently move an anchor - and an anchor that no longer exists
        # fails fast here instead of comparing against a shifted index.
        try:
            seg_names = requests.get(f"{API_URL}/segment_names", timeout=3).json()
        except (requests.exceptions.RequestException, ValueError):
            seg_names = {}

        def _resolve_seg(v: "int | str | None") -> "int | None":
            if isinstance(v, str):
                if v in seg_names:
                    return int(seg_names[v])
                print(f"\n❌ Unknown segment anchor '{v}' - the map has no such "
                      f"start→end pair (renamed or deleted track?).")
                sys.exit(1)
            return v

        print(f"\n{len(available)} named start points available on this map.")
        print("\nEach test will:")
        print("  1. Teleport to a KNOWN start point (exact position + heading)")
        print("  2. Accelerate to target speed")
        print("  3. Activate turn signal (or none, for 'straight')")
        print("  4. Monitor turn execution")
        print("  5. Check for off-road violations and instant heading snaps")
        print("\n" + "="*60)

        tests = DETERMINISTIC_TESTS

        if results is None:
            results = load_results()
        print(f"\n📄 Results file: {RESULTS_FILE}")
        print(f"   (each scenario's outcome is saved there; next run shows its last result)")

        selected = select_tests(tests, spec)
        if not selected:
            return
        if len(selected) != len(tests):
            print(f"\n▶  Running {len(selected)} of {len(tests)} scenarios: "
                  f"{', '.join(str(i) for i, _ in selected)}")

        def _emit(event: dict):
            if on_event is not None:
                on_event(event)

        pos = 0
        while pos < len(selected):
            i, test = selected[pos]

            # User interrupted (Ctrl-C) or closed the game window: stop
            # scheduling more tests now - results so far are already saved.
            if interrupted():
                print(yellow(f"\n⏸️  Stopping after test {i-1}/{len(tests)} - "
                             f"run interrupted (Ctrl-C or game window closed)."))
                break

            # Cockpit jump: a scenario button was clicked in the controller
            # window. The current test was already aborted by abort_check
            # inside monitor_turn (or hasn't started yet); start the
            # requested one instead, then continue with the rest after it.
            if take_jump is not None:
                k = take_jump()
                if k is not None:
                    if k in {idx for idx, _ in selected}:
                        print(f"\n⏭️  JUMP: starting test {k} (requested from the cockpit)")
                        _emit({'type': 'jump', 'from': i, 'to': k})
                        pos = next(p for p, (idx, _) in enumerate(selected) if idx == k)
                        continue
                    print(f"   ⚠️  Jump target {k} is not in the selected set - ignoring")

            start_point, direction, speed = test[0], test[1], test[2]
            expected_end_segment = _resolve_seg(test[3])
            duration = test[4] if len(test) > 4 else 15.0
            description = test[5] if len(test) > 5 else None
            start_progress = (test[6] if len(test) > 6
                              else TURN_SPAWN_PROGRESS)
            kerb_check = bool(test[7]) if len(test) > 7 else False
            end_flag_progress = test[8] if len(test) > 8 else END_FLAG_PROGRESS
            expect_wrong_side = bool(test[9]) if len(test) > 9 else False
            extra_cars = test[10] if len(test) > 10 else None
            # The primary car's ROLE: keyed by the maneuver (PRIMARY_CAR_ROLE).
            # The NEGATIVE hairpin test drives the SAME maneuver as the
            # positive one and is the car EXPECTED to end up wrong side - the
            # expect_wrong_side flag is what tells the two apart, so it names
            # this one instead of the lookup.
            car_name = ('wrong_side_car' if expect_wrong_side
                        else PRIMARY_CAR_ROLE.get((start_point, direction)))
            print(f"\n\n{'#'*60}")
            print(f"# TEST {i}/{len(tests)}: '{start_point}' -> {direction.upper()} @ {speed} km/h")
            print(f"{'#'*60}")

            skip_reason = SKIPPED_TESTS.get(start_point)
            if start_point == 'fig8_stress' and self._time_warp_cached() != 1.0:
                # (user decision 2026-10-18): the capacity probe answers
                # "how many cars can we run/render at max" - a REAL-TIME
                # question. A warped run outruns the wall clock by design,
                # which is exactly the metric it measures, so it only runs
                # unwarped.
                skip_reason = ("capacity probe needs real time - this run is "
                               "warped (--warp/--fast); run it without the "
                               "switch to measure cars/second")
            if skip_reason:
                print(f"\n⏭  SKIPPED: '{start_point}' - {skip_reason}\n")
                # Result in monitor_turn's shape (summary/exit-code keys),
                # marked skipped so neither counts it as a failure.
                result = {'passed': False, 'aborted': False, 'skipped': True,
                          'start_point': start_point, 'direction': direction,
                          'target_speed_kmh': speed,
                          'off_road_detected': False,
                          'instant_snap_detected': False}
                self.test_results.append(result)
                _emit({'type': 'done', 'index': i, 'passed': False,
                       'aborted': False, 'skipped': True})
                pos += 1
                continue

            self.test_no = i
            _emit({'type': 'start', 'index': i, 'total': len(tests),
                   'start_point': start_point, 'direction': direction,
                   'speed_kmh': speed, 'description': description})

            if start_point == 'fig8_stress':
                # Multi-car stress scenario: its own runner (phases of
                # 18..576 cars on the figure-8, see run_multi_car_stress_
                # test), not monitor_turn.
                # The per-phase results are already recorded in the
                # results file inside the runner; append the scenario-level
                # result here (monitor_turn normally does this itself).
                result = self.run_multi_car_stress_test(
                    results=results, abort_check=abort_check,
                    label=f"{i}/{len(tests)}")
                self.test_results.append(result)
            elif start_point in ('fig8_xing', 'fig8_xing_plain'):
                # Right-of-way crossing stress: its own runner (50 cars,
                # two-way, 300 s or first crash), not monitor_turn.
                result = self.run_crossing_stress_test(
                    start_point, results=results, abort_check=abort_check,
                    label=f"{i}/{len(tests)}")
                self.test_results.append(result)
            else:
                result = self.monitor_turn(direction, duration=duration, target_speed=speed, start_point=start_point,
                                           expected_end_segment=expected_end_segment,
                                           description=description, results=results,
                                           abort_check=abort_check,
                                           label=f"{i}/{len(tests)}",
                                           start_progress=start_progress,
                                           kerb_check=kerb_check,
                                           end_flag_progress=end_flag_progress,
                                           expect_wrong_side=expect_wrong_side,
                                           car_name=car_name,
                                           extra_cars=extra_cars)

            _emit({'type': 'done', 'index': i, 'passed': result['passed'],
                   'aborted': bool(result.get('aborted'))})

            if '--failfast' in sys.argv and not result['passed'] \
                    and not result.get('aborted'):
                print(yellow(
                    f"\n⏹  --failfast: stopping at the first failure "
                    f"(test {i}/{len(tests)})."))
                break

            pos += 1
            if pos < len(selected):
                print("\n⏸️  Pausing 1s before next test...")
                time.sleep(1)

        save_results(results)
        self.print_summary()
    
    def print_summary(self):
        """Print summary of all tests."""
        print("\n" + "="*60)
        print("FINAL SUMMARY")
        print("="*60)
        
        # Aborted scenarios (user jumped to another test from the cockpit)
        # were never actually tested - report them separately, don't count
        # them as failures.
        aborted = [r for r in self.test_results if r.get('aborted')]
        skipped = [r for r in self.test_results if r.get('skipped')]
        runnable = [r for r in self.test_results
                    if not r.get('aborted') and not r.get('skipped')]
        passed = sum(1 for r in runnable if r['passed'])
        failed_offroad = sum(1 for r in runnable if r['off_road_detected'])
        failed_snap = sum(1 for r in runnable if r['instant_snap_detected'])
        failed_teleport = sum(1 for r in runnable if r.get('teleport_detected'))
        failed_crashed = sum(1 for r in runnable if r.get('game_crashed'))
        failed_wrongside = sum(1 for r in runnable
                               if r.get('wrong_side_detected')
                               and not r.get('expect_wrong_side'))
        failed_passed_flag = sum(1 for r in runnable if r.get('passed_flag'))
        failed_wrong_route = sum(
            1 for r in runnable
            if r['segment_changed'] and not r['reached_expected_segment']
            and r.get('final_segment') != r.get('expected_end_segment')
            and not r['off_road_detected'] and not r['instant_snap_detected']
            and not r.get('teleport_detected') and not r.get('game_crashed')
            and not r.get('passed_flag')
            and not r.get('expect_wrong_side')
        )
        # Timeouts include "timed out ON the correct segment before reaching
        # the flag" - that is an arrival problem, not a routing error.
        timeout = sum(
            1 for r in runnable
            if not r['reached_expected_segment'] and not r['off_road_detected']
            and not r['instant_snap_detected'] and not r.get('teleport_detected')
            and not r.get('game_crashed') and not r.get('passed_flag')
            and (not r['segment_changed']
                 or r.get('final_segment') == r.get('expected_end_segment'))
            and not r.get('expect_wrong_side')
        )
        # Hard criterion: a scenario that never reaches its red target flag
        # is a failure, whatever else may have happened on the way (this
        # overlaps with the categories above - it is the union of all
        # "never arrived" cases, not an extra one).
        failed_no_flag = sum(1 for r in runnable
                             if not r['reached_expected_segment']
                             and not r.get('expect_wrong_side'))
        
        def line(count, fail_label, pass_label):
            if count > 0:
                return f"  ❌ Failed ({fail_label}): {count}"
            return f"  ✅ Passed: no {pass_label}"

        print(f"\nTotal tests: {len(runnable)}"
              + (f"  (+{len(aborted)} aborted by user)" if aborted else "")
              + (f"  (+{len(skipped)} skipped: "
                 + ", ".join(f"'{r['start_point']}'" for r in skipped) + ")"
                 if skipped else ""))
        print(f"  ✅ Passed: {passed}")
        print(line(failed_offroad, "off-road", "off-road violations"))
        print(line(failed_snap, "instant snap", "instant snaps"))
        print(line(failed_teleport, "teleport/jump", "teleport/jump"))
        print(line(failed_crashed, "game crashed", "game crashes"))
        print(line(failed_wrongside, "wrong-side driving", "wrong-side driving"))
        print(line(failed_passed_flag, "drove past the end flag",
                   "runs past the end flag"))
        print(line(failed_wrong_route, "wrong end segment", "wrong end segments"))
        print(line(timeout, "timeout (never arrived)", "timeouts"))
        print(line(failed_no_flag, "never reached the end flag",
                   "missed end flags"))
        
        # Show detailed violations
        snap_violations = [r for r in self.test_results if r['instant_snap_detected']]
        offroad_violations = [r for r in self.test_results if r['off_road_detected']]
        
        if snap_violations:
            print(f"\n{'─'*60}")
            print(f"INSTANT HEADING SNAP VIOLATIONS: {len(snap_violations)}")
            print(f"{'─'*60}")
            for i, r in enumerate(snap_violations, 1):
                v = r['violation_details']
                label = f" @ '{r['start_point']}'" if r.get('start_point') else ""
                print(f"\n{i}. {r['direction'].upper()} turn{label} @ {r['target_speed_kmh']:.0f} km/h")
                print(f"   Time: {v['time']:.2f}s")
                print(f"   Heading change: {v['old_heading']:.1f}° → {v['new_heading']:.1f}° ({v['heading_change']:.1f}°)")
                print(f"   Position: ({v['position'][0]:.0f}, {v['position'][1]:.0f})")
                print(f"   Speed: {v['speed_kmh']:.0f} km/h")
                if v.get('screenshot'):
                    print(f"   Screenshot: {v['screenshot']}")
        
        if offroad_violations:
            print(f"\n{'─'*60}")
            print(f"OFF-ROAD VIOLATIONS: {len(offroad_violations)}")
            print(f"{'─'*60}")
            for i, r in enumerate(offroad_violations, 1):
                v = r.get('violation_details')
                label = f" @ '{r['start_point']}'" if r.get('start_point') else ""
                speed = r.get('target_speed_kmh', 0)
                print(f"\n{i}. {r.get('direction', '?').upper()} turn{label} @ {speed:.0f} km/h")
                if v is None:
                    # Multi-car scenario (stress/crossing): there is no
                    # single violation record - show the aggregates.
                    extra = f" (crash: {r['crash_info']})" if r.get('crash_info') else ""
                    print(f"   off-road hits: {r.get('off_road_hits', '?')}{extra}")
                    continue
                print(f"   Time: {v['time']:.2f}s")
                print(f"   Position: ({v['position'][0]:.0f}, {v['position'][1]:.0f})")
                print(f"   Speed: {v['speed_kmh']:.0f} km/h")
                print(f"   Heading: {v['heading']:.1f}°")
                if v.get('screenshot'):
                    print(f"   Screenshot: {v['screenshot']}")
        
        # --- the run's results table (docs §8 "Liveness & quality") ------
        # One line per scenario: how often it crossed the centreline, how
        # many off-road hits it logged, wall time, SIM time, the warp factor
        # it ran at, and the fluidity figure (metres per second per car that
        # was supposed to drive). The flow column is blank where the metric
        # does not apply (a single car's flow is just its own speed).
        print(f"\n{'─'*60}")
        print("RESULTS TABLE (crossings · off-road · wall · sim · warp · "
              "flow m/s per car)")
        print(f"{'─'*60}")
        hdr = (f"{'#':>3} {'scenario':<26} {'xng':>4} {'off':>4} "
               f"{'wall s':>8} {'sim s':>8} {'warp':>5} {'flow/car':>9}")
        print(hdr)
        for n, r in enumerate(self.test_results, 1):
            if r.get('aborted'):
                continue
            warp = r.get('warp') or self._time_warp_cached()
            single = (r.get('world_cars') or 1) <= 1
            flow = ("" if single or r.get('flow_m_per_s') is None
                    else f"{r['flow_m_per_s']:.1f}")
            print(f"{n:>3} {str(r.get('start_point') or '?'):<26} "
                  f"{r.get('centreline_crossings', 0):>4} "
                  f"{r.get('off_road_hits', 0):>4} "
                  f"{r.get('duration', 0.0):>8.1f} "
                  f"{r.get('sim_duration_s', 0.0):>8.1f} "
                  f"{(f'{warp:.1f}x' if isinstance(warp, float) else '-'):>5} "
                  f"{(flow if flow else '-'):>9}")
        print(f"{'─'*60}")
        print("="*60)

        if passed == len(runnable):
            print(green("🎉 ALL TESTS PASSED! Smooth turns, no violations."))
        else:
            print(red(f"⚠️  {len(runnable) - passed} of {len(runnable)} "
                      f"test(s) failed. Review details above."))
        
        print("="*60 + "\n")
        
        return passed == len(self.test_results)


def main():
    """Run turn tests.
    
    By default, runs the DETERMINISTIC suite against the synthetic
    'basic' test map (start the game with: --map basic --api).
    
    Pass --tests <spec> to run a subset by number/name (see select_tests),
    e.g. `--tests fig8_stress` for just the multi-car stress scenario.

    The stress scenario additionally honours --stress-cars N[,N...] to run
    only specific car counts (e.g. `--stress-cars 576`); each phase then
    covers STRESS_PHASE_SECONDS of sim time, so a slow-motion sim simply
    takes longer in wall clock.

    Pass --random to instead teleport to random locations on whatever
    map is currently loaded (real OSM data or a test map).
    
    Pass --only <start_point> <direction> <speed_kmh> [end_segment] to run
    a SINGLE scenario directly instead of the whole suite (much faster when
    debugging one known-failing case):

        python tests/test_turning.py --only corner_right_entry right 120 6

    With end_segment, the red end flag is set at that segment's first 20%
    point as a VISUAL-ONLY marker (the running turn-test protocol, exactly
    like the full suite) - without it the nav has no destination and
    therefore no parking plan (no reverse-in).
    """
    # Ctrl-C handling: the SIGINT handler just sets a flag and tells the user
    # we're wrapping up; the suite loop then stops scheduling new tests, saves
    # the results collected so far, and exits cleanly (no traceback).
    signal.signal(signal.SIGINT, _on_sigint)

    # --list / --list-json: print the numbered scenario list and exit.
    # No game needed - used by humans and by the sim's GET /tests endpoint.
    if '--focus-json' in sys.argv:
        # --focus-json <number>: resolve ONE scenario's camera focus against the
        # running map and print it. scripts/run_e2e.sh used to own this
        # arithmetic in two embedded python blocks; the scenario does now, so
        # the reviewer (no runner) and a human get the same answer. Exit 0 even
        # for "no declaration": the default is a car focus, and that IS an
        # answer.
        i = sys.argv.index('--focus-json')
        num = int(sys.argv[i + 1])
        if not 1 <= num <= len(DETERMINISTIC_TESTS):
            print(json.dumps({"error": f"no scenario {num}"}))
            sys.exit(2)
        # A real TurnTester: it owns the keep-alive session the resolution
        # queries need, exactly as in a normal run.
        print(json.dumps(TurnTester().resolve_focus(DETERMINISTIC_TESTS[num - 1][0])))
        return

    if '--list' in sys.argv or '--list-json' in sys.argv:
        rows = []
        for i, t in enumerate(DETERMINISTIC_TESTS, 1):
            desc = t[5] if len(t) > 5 else ''
            rows.append({'number': i, 'key': t[0], 'direction': t[1],
                         'description': desc})
        if '--list-json' in sys.argv:
            print(json.dumps(rows))
        else:
            for r in rows:
                print(f"{r['number']:>2}  {r['key']} {r['direction']}"
                      f"{' - ' + r['description'] if r['description'] else ''}")
        return

    tester = TurnTester()

    if not tester.health_check():
        sys.exit(1)

    # ONE shared results dict for the whole run: every mode records into it
    # and every save writes the same live object, so a late save can never
    # clobber results recorded earlier (see the 2026-08-18 bug where the
    # except-handler saved a stale dict and wiped 15 scenario results).
    results = load_results()

    try:
        if '--only' in sys.argv:
            idx = sys.argv.index('--only')
            start_point = sys.argv[idx + 1]
            direction = sys.argv[idx + 2]
            speed = float(sys.argv[idx + 3])
            end_seg = int(sys.argv[idx + 4]) if len(sys.argv) > idx + 4 \
                else None
            # Optional: spawn speed in km/h (default: RUNNING_START_KMH
            # rolling start). Pass 0 to start from a standstill.
            spawn_kmh = float(sys.argv[idx + 5]) if len(sys.argv) > idx + 5 \
                else None
            # Show the same "i/total" test number as a suite run does.
            test_no = next((n for n, t in enumerate(DETERMINISTIC_TESTS, 1)
                            if t[0] == start_point and t[1] == direction),
                           None)
            tester.monitor_turn(direction, duration=60.0, target_speed=speed,
                               start_point=start_point, results=results,
                               expected_end_segment=end_seg,
                               label=f"{test_no}/{len(DETERMINISTIC_TESTS)}"
                                     if test_no else None,
                               spawn_speed_kmh=spawn_kmh,
                               car_name=PRIMARY_CAR_ROLE.get((start_point,
                                                              direction)))
            save_results(results)
            tester.print_summary()
        elif '--random' in sys.argv:
            tester.run_random_test(results=results)
        else:
            tester.run_deterministic_test(results=results)

        if interrupted():
            # User interrupted (Ctrl-C) or closed the game window mid-run:
            # results so far are already saved per scenario, exit cleanly.
            print(yellow("\n⏹  Run interrupted - results saved to " + str(RESULTS_FILE)))
            sys.exit(130)

        # Exit code: 0 if all passed, 1 if any failed (aborted scenarios
        # don't count - they were never actually tested).
        runnable = [r for r in tester.test_results
                    if not r.get('aborted') and not r.get('skipped')]
        all_passed = all(r['passed'] for r in runnable)
        sys.exit(0 if all_passed else 1)

    except KeyboardInterrupt:
        # Fallback for Ctrl-C landing between the handler and the loop checks.
        print(yellow("\n⏹  Run interrupted by user - results saved to " + str(RESULTS_FILE)))
        save_results(results)
        sys.exit(130)
    except Exception as e:
        print(f"\n❌ Test failed with error: {e}")
        import traceback
        traceback.print_exc()
        save_results(results)
        tester.reset_controls()
        sys.exit(1)


if __name__ == '__main__':
    main()
