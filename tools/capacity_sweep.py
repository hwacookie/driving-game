#!/usr/bin/env python3
"""
Capacity sweep (Phase 6, gate G4): find the real-time ceiling of the C# sim.

Spawns N cars on the figure-8 (same pattern as test_turning.py's fig8_stress:
segments 104..151 of the `basic` map, rolling start at 50 km/h, throttle held),
then measures the sim's PACE: sim-time advanced / wall-time elapsed. Pace >= 1
means the sim holds real-time 60 Hz; below 1 it runs in slow motion (the
accumulator caps substeps, so it degrades gracefully instead of leaping).

The Python ceiling was ~100 cars (576 cars ran at ~8% pace). This records the
C# number: the largest N whose pace stays within REALTIME_TOLERANCE of 1.0.

Usage (console host must be running with --map basic):
    CAR_API_URL=http://127.0.0.1:5099 python tools/capacity_sweep.py
    ... --phases 50,100,150,200,300,400,576,800 --out data/capacity_sweep.json
"""

import argparse
import json
import math
import os
import sys
import time

import requests

API_URL = os.environ.get("CAR_API_URL", "http://127.0.0.1:5099")

# fig8 layout in the `basic` test map (same constants as test_turning.py)
FIG8_FIRST_SEGMENT = 104
FIG8_N_SEGMENTS = 48
SPAWN_KMH = 50.0          # rolling start, then coast with throttle held

WARMUP_WALL_S = 8.0       # let spawns settle + raceline cache start building
MEASURE_SIM_S = 25.0      # sim-time to accumulate for a stable pace ratio
MEASURE_WALL_CAP_S = 90.0 # hang guard per measurement window
MIN_MEASURED_SIM_S = 5.0  # below this the ratio is too noisy to trust
POLL_S = 0.25

REALTIME_TOLERANCE = 0.97  # pace >= this counts as "holds real time"


def clear_all_cars(session: requests.Session) -> bool:
    session.post(f"{API_URL}/cars", json={"action": "clear"}, timeout=5)
    deadline = time.time() + 15.0
    while time.time() < deadline:
        try:
            st = session.get(f"{API_URL}/state", timeout=2).json()
            if len(st.get("cars", [])) == 0:
                return True
        except (requests.exceptions.RequestException, ValueError):
            pass
        time.sleep(0.1)
    return False


def spawn_cars(session: requests.Session, n: int) -> list[int]:
    """Spawn n cars evenly around the loop; return their uids."""
    speed_mps = SPAWN_KMH / 3.6
    for i in range(n):
        seg = FIG8_FIRST_SEGMENT + round(i * FIG8_N_SEGMENTS / n) % FIG8_N_SEGMENTS
        session.post(f"{API_URL}/teleport", timeout=5, json={
            "segment": seg, "progress": 0.5, "speed": speed_mps, "add": True})
    deadline = time.time() + max(15.0, n * 0.05)
    uids: list[int] = []
    bad_streak = 0
    while time.time() < deadline:
        try:
            st = session.get(f"{API_URL}/state", timeout=2).json()
            bad_streak = 0
            uids = [c["car_uid"] for c in st.get("cars", [])]
            if len(uids) == n:
                return uids
        except (requests.exceptions.RequestException, ValueError):
            bad_streak += 1
            if bad_streak >= 5:
                break
        time.sleep(0.1)
    return uids


def hold_throttle(session: requests.Session, uids: list[int]) -> None:
    for uid in uids:
        try:
            session.post(f"{API_URL}/control", timeout=5,
                         json={"uid": uid, "accelerate": True})
        except requests.exceptions.RequestException:
            pass


def measure_pace(session: requests.Session) -> dict:
    """Run the measurement window; return pace + diagnostics."""
    # Warmup (wall clock): spawns settle, first raceline solves happen.
    time.sleep(WARMUP_WALL_S)

    samples: list[tuple[float, float]] = []  # (wall_s, sim_s)
    wedged = False
    wall_start = time.time()
    while True:
        try:
            st = session.get(f"{API_URL}/state", timeout=2).json()
        except (requests.exceptions.RequestException, ValueError):
            print("   ⚠️  /state failed mid-measurement - retrying")
            if time.time() - wall_start > MEASURE_WALL_CAP_S:
                wedged = True
                break
            continue
        now = time.time()
        sim_t = st.get("time")
        if isinstance(sim_t, (int, float)):
            samples.append((now, float(sim_t)))
        if len(samples) >= 2:
            d_sim = samples[-1][1] - samples[0][1]
            d_wall = samples[-1][0] - samples[0][0]
            if d_sim >= MEASURE_SIM_S or d_wall >= MEASURE_WALL_CAP_S:
                break
        time.sleep(POLL_S)

    if wedged or len(samples) < 4:
        return {"pace": None, "wedged": True}

    d_sim = samples[-1][1] - samples[0][1]
    d_wall = samples[-1][0] - samples[0][0]
    pace = d_sim / d_wall if d_wall > 0 else 0.0
    return {
        "pace": round(pace, 4),
        "sim_s": round(d_sim, 2),
        "wall_s": round(d_wall, 2),
        "reliable": d_sim >= MIN_MEASURED_SIM_S,
        "wedged": False,
    }


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--phases", default="50,100,150,200,300,400,576,800")
    ap.add_argument("--out", default=None)
    args = ap.parse_args()
    phases = [int(x) for x in args.phases.split(",") if x.strip()]

    print(f"Capacity sweep against {API_URL}")
    print(f"Phases: {phases}  (real-time criterion: pace >= {REALTIME_TOLERANCE})\n")

    results: dict[int, dict] = {}
    ceiling = None
    with requests.Session() as session:
        for n in phases:
            print(f"--- {n} cars " + "-" * max(0, 46 - len(str(n))))
            if not clear_all_cars(session):
                print("   ❌ could not clear the map - aborting")
                results[n] = {"error": "clear_failed"}
                break
            uids = spawn_cars(session, n)
            if len(uids) != n:
                print(f"   ❌ only {len(uids)}/{n} cars spawned - aborting")
                results[n] = {"error": f"spawn_{len(uids)}_of_{n}"}
                break
            hold_throttle(session, uids)
            m = measure_pace(session)
            if m["wedged"]:
                print("   ❌ sim wedged (no /state answers) - aborting")
                results[n] = {"error": "wedged"}
                break
            pace = m["pace"]
            rt = pace is not None and pace >= REALTIME_TOLERANCE
            if rt:
                ceiling = n
            flag = "real-time" if rt else "slow motion"
            rel = "" if m.get("reliable") else "  (short window - low confidence)"
            print(f"   pace {pace:.3f}  ({m['sim_s']:.1f} s sim / "
                  f"{m['wall_s']:.1f} s wall) -> {flag}{rel}")
            results[n] = {"cars": n, **m, "real_time": rt}

    print(f"\n{'=' * 60}")
    if ceiling is not None:
        print(f"REAL-TIME CEILING (pace >= {REALTIME_TOLERANCE}): {ceiling} cars")
    else:
        print("No phase held real time.")
    for n, r in results.items():
        if "error" in r:
            print(f"  {n:>5}: ERROR {r['error']}")
        else:
            p = f"{r['pace']:.3f}" if r["pace"] is not None else "  wedged"
            mark = "✓" if r.get("real_time") else "✗"
            print(f"  {n:>5}: pace {p}  {mark}")

    if args.out:
        payload = {"api_url": API_URL, "criterion": REALTIME_TOLERANCE,
                   "ceiling_cars": ceiling, "phases": results}
        with open(args.out, "w") as f:
            json.dump(payload, f, indent=2)
        print(f"\nSaved: {args.out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
