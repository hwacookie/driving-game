#!/usr/bin/env python3
"""Spawn 50 cars on the fig8_cross loop - half clockwise, half counter-
clockwise (interleaved around the loop) - and hold the accelerator on all
of them. Manual QA: two-way traffic demo / stress (2026-09-08).

Usage:  python3 tests/fig8_twoway_check.py [n_cars] [seconds]
        (server must run with --map fig8_cross)
"""
import json
import sys
import time
import urllib.request

BASE = "http://127.0.0.1:5000"
N = int(sys.argv[1]) if len(sys.argv) > 1 else 50
SECONDS = float(sys.argv[2]) if len(sys.argv) > 2 else 300.0


def post(path, payload):
    req = urllib.request.Request(
        BASE + path, data=json.dumps(payload).encode(),
        headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req) as r:
        return json.loads(r.read())


def get(path):
    with urllib.request.urlopen(BASE + path) as r:
        return json.loads(r.read())


# --- loop geometry ---------------------------------------------------------
segs, i = [], 0
while True:
    try:
        segs.append(get(f"/segment/{i}"))
    except Exception:
        break
    i += 1
perimeter = sum(s["length"] for s in segs)
print(f"loop: {len(segs)} segments, {perimeter / 2:.0f} m")

# --- spawn N cars, interleaved directions ----------------------------------
uids = []
for k in range(N):
    rev = (k % 2 == 1)
    target = (k + 0.5) * perimeter / N
    best, best_err, mid = 0, None, 0.0
    for s in segs:
        err = abs(mid + s["length"] / 2 - target)
        if best_err is None or err < best_err:
            best, best_err = s["idx"], err
        mid += s["length"]
    post("/teleport", {"segment": best, "progress": 0.5, "add": True,
                       "reverse": rev})

# Commands are processed by the sim on its tick loop - poll until all
# cars actually exist before touching them.
uids = []
for _ in range(300):   # up to 30 s
    st = get("/state")
    uids = [c["car_uid"] for c in st["cars"]]
    if len(uids) == N:
        break
    time.sleep(0.1)
assert len(uids) == N, f"expected {N} cars, got {len(uids)}"
for u in uids:
    post("/control", {"uid": u, "accelerate": True})

print(f"GO! {N} cars accelerating ({N // 2} per direction) for {SECONDS:.0f}s")
# Poll /state (each GET blocks until the server answers) and pace the
# REPORTING on the sim clock, not wall time: a status line every ~5 s of
# SIM time, exit when the sim reaches SECONDS. The only sleep is the
# <=1 s poll pacing.
last_report_sim_t = -1e9
while True:
    st = get("/state")
    t = float(st["time"])
    if t >= SECONDS:
        break
    if t - last_report_sim_t >= 5.0:
        speeds = [c["speed_kmh"] for c in st.get("cars", [])]
        n_moving = sum(1 for v in speeds if v > 3.6)
        print(f"t={t:5.0f}s  moving {n_moving}/{len(speeds)}  "
              f"min={min(speeds):3.0f} max={max(speeds):3.0f} km/h")
        last_report_sim_t = t
    time.sleep(0.5)
print("done - cars keep running; watch the window")
