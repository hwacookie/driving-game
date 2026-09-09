#!/usr/bin/env python3
"""Mixed-fleet figure-8 check (manual QA, not pass/fail): spawns ALL seven
vehicle classes (one per color) evenly around the fig8_cross test track -
a lemniscate whose self-crossing is a REAL 4-way junction (no bridge) -
and lets them all start from standstill at the same time. The sedans pull
away, the trucks crawl; everyone has to brake for everyone else, and the
crossing in the middle is real right-before-left territory.

Watches the run: min pairwise gap, near-miss count (< NEAR_MISS_M),
per-car top speed / braking activity. A human should also watch the Godot
window - "does it look right" is a human call.

Usage:
    # Server must be running on the fig8_cross map (NOT basic):
    ./DrivingGame.Server/bin/Debug/net9.0/DrivingGame.Server --map fig8_cross
    /Applications/Godot_mono.app/Contents/MacOS/Godot --path . --
    python3 tests/fig8_fleet_check.py [--duration 90]

Not part of the automated DETERMINISTIC_TESTS suite (test_turning.py).
"""
import argparse
import json
import os
import sys
import time
import urllib.request

API_URL = os.environ.get("CAR_API_URL", "http://127.0.0.1:5000")

# One of each vehicle class, in palette order (Config.CAR_COLORS).
CARS = ["blue", "silver", "police", "tan", "tractor", "pickup", "mixer"]
NEAR_MISS_M = 5.0     # center-to-center distance that counts as a near miss
FIG8_SEGMENT_COUNT = 48


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


def get_segments():
    segs = []
    idx = 0
    while True:
        try:
            segs.append(_req("GET", f"/segment/{idx}"))
        except urllib.error.HTTPError as e:
            if e.code == 404:
                return segs
            raise
        idx += 1


def pick_spawn_segments(segs, n):
    """n segments whose midpoints are ~evenly spaced around the loop."""
    perimeter = sum(s["length"] for s in segs)
    picks = []
    acc = 0.0
    for k in range(n):
        target = (k + 0.5) * perimeter / n
        # find the segment whose midpoint is closest to `target`
        best, best_err = None, None
        mid = acc
        for s in segs:
            if abs(mid + s["length"] / 2 - target) < (best_err or 1e18):
                best, best_err = s, abs(mid + s["length"] / 2 - target)
            mid += s["length"]
        picks.append(best)
    return picks


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--duration", type=float, default=90.0,
                    help="seconds to run the fleet (default 90)")
    args = ap.parse_args()

    print(cyan(f"Waiting for API at {API_URL} ..."))
    if not wait_for_health():
        print("!! API never came up"); sys.exit(1)

    segs = get_segments()
    if len(segs) != FIG8_SEGMENT_COUNT:
        print(f"!! wrong map: expected {FIG8_SEGMENT_COUNT} segments "
              f"(fig8_cross), got {len(segs)}.")
        print("   Restart the server with:  --map fig8_cross"); sys.exit(1)

    picks = pick_spawn_segments(segs, len(CARS))
    perimeter = sum(s["length"] for s in segs)
    # pixels per metre, derived from the data (Config.PIXELS_PER_METER).
    s0 = segs[0]
    pppm = ((s0["x2"] - s0["x1"]) ** 2 + (s0["y2"] - s0["y1"]) ** 2) ** 0.5 / s0["length"]
    print(cyan(f"fig8_cross: {len(segs)} segments, loop ≈ {perimeter:.0f} m, "
               f"≈ {perimeter / len(CARS):.0f} m per car"))

    _req("POST", "/cars", {"action": "clear"})
    t0 = time.time()
    while time.time() - t0 < 5.0:              # poll until the clear landed
        if not _req("GET", "/state")["cars"]:
            break
        time.sleep(0.2)

    print(cyan("\nSpawning the fleet (one of each class, evenly spaced):"))
    for color, seg in zip(CARS, picks):
        _req("POST", "/teleport", {"segment": seg["idx"], "progress": 0.5,
                                   "color": color})
    # Commands are queued and processed on the sim's tick loop - poll until
    # every car actually exists before touching them.
    uids = {}
    t0 = time.time()
    while time.time() - t0 < 10.0:
        st = _req("GET", "/state")
        if {c["color"] for c in st["cars"]} == set(CARS):
            break
        time.sleep(0.2)
    for c in st["cars"]:
        uids[c["color"]] = c["car_uid"]
    if set(uids) != set(CARS):
        print(f"!! missing cars: {set(CARS) - set(uids)}"); sys.exit(1)
    print("   " + ", ".join(f"{c}={uids[c]}" for c in CARS))

    # GO - all at once (as close to simultaneous as the API allows).
    t_go_sim = float(_req("GET", "/state")["time"])
    for uid in uids.values():
        _req("POST", "/control", {"uid": uid, "accelerate": True})
    print(green(f"\nGO! All {len(CARS)} cars accelerating at t=0 - "
                f"watch the window."))

    # --- monitor ---------------------------------------------------------
    min_gap = float("inf")
    min_gap_t = None
    near_misses = 0
    top_speed = {c: 0.0 for c in CARS}
    brake_samples = {c: 0 for c in CARS}
    last_table = 0.0
    wall_start = time.time()

    # Monitor paced on the SIM clock (a frozen sim must not burn the whole
    # duration in real time); wall time only guards against a dead API.
    while time.time() - wall_start < args.duration * 3 + 30.0:
        st = _req("GET", "/state")
        t = float(st["time"]) - t_go_sim
        if t >= args.duration:
            break
        cars = st["cars"]
        by_uid = {c["car_uid"]: c for c in cars}
        # pairwise center distance in metres (state coords are pixels).
        pts = [(c["x"], c["y"]) for c in cars]
        gap_now = float("inf")
        for i in range(len(pts)):
            for j in range(i + 1, len(pts)):
                d = ((pts[i][0] - pts[j][0]) ** 2 +
                     (pts[i][1] - pts[j][1]) ** 2) ** 0.5 / pppm
                gap_now = min(gap_now, d)
        if gap_now < min_gap:
            min_gap, min_gap_t = gap_now, t
        if gap_now < NEAR_MISS_M:
            near_misses += 1
        for c in cars:
            top_speed[c["color"]] = max(top_speed[c["color"]], c["speed_kmh"])
            if c.get("braking"):
                brake_samples[c["color"]] += 1
        if t - last_table > 10.0:
            last_table = t
            ordered = sorted(cars, key=lambda c: CARS.index(c["color"]))
            row = "  ".join(f"{c['color'][:4]}={c['speed_kmh']:3.0f}"
                            for c in ordered)
            print(f"t={t:5.0f}s  min_gap={gap_now:5.1f} m   {row}")
        time.sleep(0.5)

    for uid in uids.values():
        _req("POST", "/control", {"uid": uid, "accelerate": False})

    print("\n" + cyan("=== summary ==="))
    print(f"min pairwise gap:   {min_gap:.1f} m (at t={min_gap_t:.0f}s)")
    print(f"near misses (<{NEAR_MISS_M:.0f} m): {near_misses} samples")
    print(f"{'class':8s} {'top speed':>10s} {'braking %':>10s}")
    n_samples = max(1, int(args.duration / 0.5))
    for color in CARS:
        pct = 100.0 * brake_samples[color] / n_samples
        print(f"{color:8s} {top_speed[color]:9.0f} km/h {pct:9.0f} %")
    if min_gap < NEAR_MISS_M:
        print(yellow("!! cars got within the near-miss band - check the log "
                     "and the window for what happened"))
    else:
        print(green("No pair came within the near-miss band."))


if __name__ == "__main__":
    main()
