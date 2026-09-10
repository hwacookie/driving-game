#!/usr/bin/env python3
"""Visual car-line check (manual QA, not pass/fail): places ALL vehicle
types one after another on a long straight road segment so a human can
compare them side by side in the Godot window - sprites, relative sizes,
orientation.

The script picks a suitable segment itself (first segment that is long
enough for the whole line and wide enough for the widest vehicle), then
places each vehicle at an exact progress value computed from its physical
length (CARS below) plus GAP_M between vehicles. Chord placement is exact
on straight segments, so the printed table can be checked against /state
positions.

Usage:
    # Server + Godot already running (e.g. via scripts/run_e2e.sh or manual):
    python3 tests/car_line_check.py

Not part of the automated DETERMINISTIC_TESTS suite (test_turning.py): no
pass/fail criterion, only "does it look right" - a human call.
"""
import os
import sys
import time
import urllib.request
import json

API_URL = os.environ.get("CAR_API_URL", "http://127.0.0.1:5001")

# (color, width_m, length_m) - MUST mirror Config.CAR_COLORS
# (DrivingGame.Sim/Config.cs) and CarSizes in MapRenderer.cs.
# Order = line-up order.
CARS = [
    ("blue",    1.80, 4.40),
    ("silver",  1.71, 3.14),
    ("police",  1.86, 4.71),
    ("tan",     1.86, 4.75),
    ("tractor", 2.55, 7.00),
    ("pickup",  2.09, 5.25),
    ("mixer",   2.50, 8.40),
]
GAP_M = 3.0      # clear gap between consecutive vehicles
MARGIN_M = 5.0   # keep off the junction ends of the segment


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


def find_segment(needed_m, min_width_m):
    """First segment long enough for the whole line and wide enough for the
    widest vehicle (a bit of kerb clearance included)."""
    for idx in range(500):
        try:
            seg = _req("GET", f"/segment/{idx}")
        except urllib.error.HTTPError as e:
            if e.code == 404:
                break
            raise
        if seg["length"] >= needed_m + 2 * MARGIN_M and seg["width"] >= min_width_m:
            return idx, seg
    raise RuntimeError(f"no segment long enough (>= {needed_m + 2 * MARGIN_M:.0f} m) "
                       f"and wide enough (>= {min_width_m:.1f} m)")


def main():
    print(cyan(f"Waiting for API at {API_URL} ..."))
    if not wait_for_health():
        print("!! API never came up"); sys.exit(1)

    lengths = [l for _, _, l in CARS]
    needed_m = sum(lengths) + GAP_M * (len(CARS) - 1)
    min_width_m = max(w for _, w, _ in CARS) + 0.5
    idx, seg = find_segment(needed_m, min_width_m)
    print(cyan(f"Using segment #{idx}: {seg['length']:.0f} m long, "
               f"{seg['width']} m wide, ({seg['x1']},{seg['y1']}) -> ({seg['x2']},{seg['y2']})"))

    # Centre offsets of each vehicle along the segment (m from the start
    # node), then progress = offset / length.
    plan = []
    d = MARGIN_M + lengths[0] / 2.0
    for i, (name, w, l) in enumerate(CARS):
        if i > 0:
            d += lengths[i - 1] / 2.0 + GAP_M + l / 2.0
        plan.append((name, l, d / seg["length"]))

    _req("POST", "/cars", {"action": "clear"})
    t0 = time.time()
    while time.time() - t0 < 5.0:              # poll until the clear landed
        if not get_state().get("cars", []):
            break
        time.sleep(0.2)

    print(cyan("\nPlacing the line (from the segment start node towards the end):"))
    for name, l, p in plan:
        _req("POST", "/teleport", {"segment": idx, "progress": p,
                                   "color": name, "add": True})

    # Verify: every car present with the right color and at the planned
    # longitudinal position (chord placement is exact on straight segments;
    # the lane offset only shifts laterally). Commands are queued and
    # processed on the sim's tick loop - poll until all cars exist.
    wanted = {name for name, _, _ in CARS}
    t0 = time.time()
    while time.time() - t0 < 10.0:
        cars = {c["color"]: c for c in get_state().get("cars", [])}
        if set(cars) == wanted:
            break
        time.sleep(0.2)
    dx, dy = seg["x2"] - seg["x1"], seg["y2"] - seg["y1"]
    norm2 = dx * dx + dy * dy
    print(f"\n{'#':>2} {'color':8s} {'len':>5s} {'planned m':>10s} {'actual m':>9s}  pos (x, y)")
    ok = True
    for i, (name, l, p) in enumerate(plan, start=1):
        c = cars.get(name)
        if c is None:
            print(f"!! missing car: {name}")
            ok = False
            continue
        along = ((c["x"] - seg["x1"]) * dx + (c["y"] - seg["y1"]) * dy) / norm2 * seg["length"]
        planned = p * seg["length"]
        mark = "  " if abs(along - planned) < 0.5 else "!!"
        ok = ok and abs(along - planned) < 0.5
        print(f"{i:>2} {name:8s} {l:5.2f} {planned:10.1f} {along:9.1f}{mark}  "
              f"({c['x']:.0f}, {c['y']:.0f})")

    if not ok:
        print(yellow("\n!! position check failed - see marks above"))
        sys.exit(1)
    print(green(f"\nDone - {len(CARS)} vehicles lined up on segment #{idx} "
                f"({needed_m:.0f} m of the {seg['length']:.0f} m road)."))
    print(yellow("The camera follows the LAST vehicle (mixer) - scroll / zoom out "
                 "in the Godot window to see the whole line."))


if __name__ == "__main__":
    main()
