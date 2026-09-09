#!/usr/bin/env python3
"""Deterministic repro + trace of the two-way fig8_cross deadlock.

Spawns N cars EXACTLY like fig8_twoway_check.py (same spawn loop, same
command order), holds the accelerator, and logs a per-car state trace
(every /state poll) to a JSONL file:
    t, frame, uid, x, y, heading, speed_kmh, segment, progress, braking,
    hazard
Detects a full stop: when ALL cars are below DEADLOCK_KMH for
DEADLOCK_HOLD_S of SIM time the run is declared deadlocked and the script
exits (so a frozen sim does not burn wall time).

Usage (server must run with --map fig8_cross):
    python3 tests/fig8_twoway_repro.py [n_cars] [max_sim_seconds]
"""
import json
import sys
import time
import urllib.request
from datetime import datetime

BASE = "http://127.0.0.1:5000"
N = int(sys.argv[1]) if len(sys.argv) > 1 else 50
MAX_SIM_S = float(sys.argv[2]) if len(sys.argv) > 2 else 300.0

DEADLOCK_KMH = 0.2
DEADLOCK_HOLD_S = 5.0
POLL_S = 0.25
WALL_CAP_S = MAX_SIM_S * 3 + 60.0     # hang guard against a dead API

LOG_PATH = f"tests/twoway_repro_{datetime.now().strftime('%Y%m%d_%H%M%S')}.jsonl"


def post(path, payload):
    req = urllib.request.Request(
        BASE + path, data=json.dumps(payload).encode(),
        headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=5) as r:
        return json.loads(r.read())


def get(path):
    with urllib.request.urlopen(BASE + path, timeout=5) as r:
        return json.loads(r.read())


# --- loop geometry (identical to fig8_twoway_check.py) ---------------------
segs, i = [], 0
while True:
    try:
        segs.append(get(f"/segment/{i}"))
    except Exception:
        break
    i += 1
perimeter = sum(s["length"] for s in segs)
print(f"loop: {len(segs)} segments, {perimeter:.0f} m")

# --- spawn N cars, interleaved directions (identical to twoway check) ------
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

t0_sim = float(get("/state")["time"])
print(f"GO! {N} cars. Tracing -> {LOG_PATH}")

logf = open(LOG_PATH, "w")
last_all_stopped_t = None   # sim time when every car went below DEADLOCK_KMH
deadlocked_at = None
wall_start = time.time()

while time.time() - wall_start < WALL_CAP_S:
    st = get("/state")
    t = float(st["time"]) - t0_sim
    cars = st["cars"]
    for c in cars:
        logf.write(json.dumps({
            "t": round(t, 3), "frame": st.get("frame"),
            "uid": c["car_uid"],
            "x": round(c["x"], 2), "y": round(c["y"], 2),
            "h": round(c["heading"], 1),
            "v": round(c["speed_kmh"], 2),
            "seg": c["segment"], "pr": round(c["progress"], 4),
            "brk": 1 if c.get("braking") else 0,
            "hz": 1 if c.get("hazard") else 0,
        }) + "\n")
    logf.flush()

    all_stopped = all(c["speed_kmh"] < DEADLOCK_KMH for c in cars)
    if all_stopped:
        if last_all_stopped_t is None:
            last_all_stopped_t = t
        elif t - last_all_stopped_t >= DEADLOCK_HOLD_S:
            deadlocked_at = last_all_stopped_t
            break
    else:
        last_all_stopped_t = None

    if t >= MAX_SIM_S:
        break
    time.sleep(POLL_S)

logf.close()

# --- summary -----------------------------------------------------------------
st = get("/state")
t = float(st["time"]) - t0_sim
near = [(c["car_uid"], round(c["x"]), round(c["y"]),
         round(c["speed_kmh"], 1), c["segment"])
        for c in st["cars"] if (c["x"] - 500) ** 2 + (c["y"] - 500) ** 2 < 60 ** 2]
print(f"stopped at t={t:.1f}s  (all cars <{DEADLOCK_KMH} km/h for "
      f"{DEADLOCK_HOLD_S}s => DEADLOCK at t≈{deadlocked_at:.1f}s)" if deadlocked_at
      else f"ran to t={t:.1f}s without full stop")
print(f"cars within 60 px of the crossing (px 500,500): {len(near)}")
for uid, x, y, v, seg in sorted(near):
    print(f"  uid {uid:3d}  ({x},{y})  seg {seg}  {v} km/h")
print(f"trace: {LOG_PATH}")
