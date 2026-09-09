#!/usr/bin/env python3
"""Visual light check (manual QA, not pass/fail): spawns ONE stationary car
and cycles through every light state - left blinker, right blinker, hazard,
brake, taillight, headlight - 3 seconds each (hazard needs 5s: the game
enforces a minimum display time so a stuck hazard stays visible), so a human
watching the Godot window can confirm each one looks right.

Usage:
    # Server + Godot already running (e.g. via scripts/run_e2e.sh or manual):
    python3 tests/light_check.py
    python3 tests/light_check.py --color blue

    # Godot should be launched with a close, fixed zoom to inspect corner
    # lights clearly, e.g.:
    #   $GODOT_BIN --path . -- --zoom 64
    # (--zoom only sets the INITIAL view + pins the follow-zoom; the camera
    # still auto-follows the spawned car's position.)

Not part of the automated DETERMINISTIC_TESTS suite (test_turning.py): there
is no pass/fail criterion here, only "does it look right" - a human call.
"""
import os
import sys
import time
import urllib.request
import json

API_URL = os.environ.get("CAR_API_URL", "http://127.0.0.1:5000")
# Must mirror Config.CAR_COLORS (DrivingGame.Sim/Config.cs).
Config_COLORS = ["blue", "silver", "police", "tan", "tractor", "pickup", "mixer"]
START_POINT = "crossroads_from_north"
PROGRESS = 0.6
PHASE_S = 3.0
HAZARD_MIN_DISPLAY_S = 5.0   # BicycleDriver.HAZARD_MIN_DISPLAY_S - server enforces this


def _c(text, code):
    return f"\033[{code}m{text}\033[0m" if sys.stdout.isatty() else text


def cyan(t): return _c(t, "36")
def green(t): return _c(t, "32")
def yellow(t): return _c(t, "33")


def _req(method, path, body=None):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(
        API_URL + path, data=data, method=method,
        headers={"Content-Type": "application/json"} if data else {})
    with urllib.request.urlopen(req, timeout=5) as r:
        return json.loads(r.read())


def wait_for_health(timeout=30.0):
    t0 = time.time()
    while time.time() - t0 < timeout:
        try:
            _req("GET", "/health")
            return True
        except Exception:
            time.sleep(0.5)
    return False


def get_state():
    return _req("GET", "/state")


def spawn_stationary_car(color=""):
    """Spawn one stationary car at START_POINT.

    If `color` is given, it is passed to /teleport and the spawned car gets
    that color (validated server-side against Config.CAR_COLORS: red, blue,
    yellow, white) instead of the default uid-based assignment."""
    _req("POST", "/cars", {"action": "clear"})
    t0 = time.time()
    while time.time() - t0 < 5.0:              # poll until the clear landed
        if not get_state().get("cars", []):
            break
        time.sleep(0.2)
    before = {c["car_uid"] for c in get_state().get("cars", [])}
    body = {"start_point": START_POINT, "progress": PROGRESS, "add": True, "speed": None}
    if color:
        body["color"] = color
    _req("POST", "/teleport", body)
    t0 = time.time()
    while time.time() - t0 < 3.0:
        for c in get_state().get("cars", []):
            if c["car_uid"] not in before:
                return c["car_uid"]
        time.sleep(0.05)
    raise TimeoutError("car did not spawn")


def label(text):
    _req("POST", "/label", {"text": text})


def control(uid, **flags):
    _req("POST", "/control", {"uid": uid, **flags})


def toggle(uid, **flags):
    _req("POST", "/toggle", {"uid": uid, **flags})


def run_phase(uid, name, duration, on, off=None):
    print(cyan(f"\n=== {name} ({duration:.0f}s) ==="))
    label(name)
    t0 = float(get_state()["time"])   # sim clock at phase start
    on()
    # Show the light for `duration` of SIM time (poll; wall time only guards
    # against a frozen sim / dead API).
    deadline_wall = time.time() + duration * 3 + 10.0
    while time.time() < deadline_wall:
        if float(get_state()["time"]) - t0 >= duration:
            break
        time.sleep(0.2)
    if off:
        off()


def main():
    import argparse
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--color", default="",
                    choices=["", *Config_COLORS],
                    help="vehicle sprite of the spawned car via /teleport "
                         "(default: uid-based cycle, red on a fresh server)")
    args = ap.parse_args()

    print(cyan(f"Waiting for API at {API_URL} ..."))
    if not wait_for_health():
        print("!! API never came up"); sys.exit(1)

    uid = spawn_stationary_car(args.color)
    print(green(f"Spawned stationary car #{uid} at {START_POINT} (progress={PROGRESS}) - never accelerates."))
    print(yellow("Watch the Godot window now. Zoom in / press F to steady the camera if needed."))
    time.sleep(1.0)

    run_phase(uid, "blinker_left", PHASE_S,
               on=lambda: control(uid, blinker_left=True))
    run_phase(uid, "blinker_right", PHASE_S,
               on=lambda: control(uid, blinker_right=True),
               off=lambda: toggle(uid, clear_blinker=True))
    run_phase(uid, "hazard", HAZARD_MIN_DISPLAY_S,
               on=lambda: control(uid, hazard=True),
               off=lambda: control(uid, hazard=False))
    run_phase(uid, "brake", PHASE_S,
               on=lambda: control(uid, brake=True),
               off=lambda: control(uid, brake=False))
    run_phase(uid, "rear light (taillight)", PHASE_S,
               on=lambda: toggle(uid, taillights=True),
               off=lambda: toggle(uid, taillights=False))
    run_phase(uid, "front light (headlight)", PHASE_S,
               on=lambda: toggle(uid, headlights=True),
               off=lambda: toggle(uid, headlights=False))

    label("")
    print(green("\nDone - all six light states shown for 3s each (hazard 5s)."))


if __name__ == "__main__":
    main()
