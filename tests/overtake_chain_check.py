#!/usr/bin/env python3
"""The row as one obstruction: t1 -> t2 -> c3, with oncoming traffic
(docs/DRIVING_MANEUVERS.md §9 Case B + C, issue #78).

A SEPARATE test from tests/overtake_order_check.py (the queue behind ONE slow
vehicle, no oncoming traffic): here the obstruction is a row of two tractors and
the other half of the road is busy, so the test grades the two facts the row is
about and the one behaviour this issue is allowed to be red about:

  G1  t2 - a tractor behind a tractor - states a DECLINED intent ("[R37] intent:
      no pass wanted from …"): the intent is the driver's, not "the car ahead is
      slow". RED today: nothing decides a driver's preference yet (§9's open
      list), so t2 takes the turn and c3 never gets one.
  G2  t2 never leaves its lane and does not change speed (§9 Case B).
  G3  c3 passes t2 - the car behind gets its turn because t2 left it open.
  G4  c3's window belongs to the whole row: it returns PAST its front car t1,
      never level with t2 - measured with R36's own metres-left-of-the-
      centreline at a position, no new measurement (§9 Case C).
  G5a c3 signals before it commits and comes to the left edge of its OWN lane
      while it checks (§9, "the check is a visible act").
  G5b nobody stands: no car of the row and none of the queue is at rest for
      more than 2 s of sim - an aborted plan is a way back (R39's lane owner
      yields to its own right and brakes, it never gives up the lane).

The oncoming car makes the window real: it comes at c3 head-on, so a pass
started too early has to be given up. Everything is METRES (docs/UNITS.md); the
road side is derived from GET /segment/{idx} and each car's own right-hand half
from its heading.

Starts nothing - run it against a fresh host (a full field of leftover cars is
refused):

    "$BIN" --map basic --warp 4 &          # DrivingGame.Server/bin/…
    python3 tests/overtake_chain_check.py
"""
import math
import os
import sys
import time
import json
import urllib.request

API = os.environ.get("CHAIN_API_URL", "http://127.0.0.1:5001").rstrip("/")
SEG = int(os.environ.get("CHAIN_SEGMENT", "0"))          # straight_n->straight_m
RUN_S = 45.0
TRAIL = 22.0      # the gap c3 keeps behind t2
ROW_GAP = 22.0    # the gap t2 keeps behind t1
TR1_Y = 300.0     # the row front, metres: the straights run y 350 -> 50, so the
                  # row has the open 100 m of segment 0 plus segment 1 ahead of
                  # it - not the map end, where a car braking for the pennant
                  # would be blamed on R37 (docs/TESTING.md, the end-flag brake)
ONC_Y = 210.0     # the oncoming car starts south of the row and comes at it


def api(method, path, body=None):
    data = None if body is None else json.dumps(body).encode()
    req = urllib.request.Request(API + path, data=data, method=method,
                                 headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=15) as r:
        return json.loads(r.read())


def state():
    return api("GET", "/state")


def uids():
    return {c["car_uid"] for c in state().get("cars", [])}


def spawn_new(progress, speed, cruise, model=None, reverse=False):
    before = uids()
    body = {"segment": SEG, "progress": progress, "speed": speed,
            "throttle": True, "cruise_kmh": cruise, "add": True}
    if model:
        body["model"] = model
    if reverse:
        body["reverse"] = True
    api("POST", "/teleport", body)
    for _ in range(40):                    # commands apply on the tick loop
        new = uids() - before
        if new:
            return next(iter(new))
        time.sleep(0.25)
    return None


def geom(idx):
    g = api("GET", f"/segment/{idx}")
    ax, ay = g["x2"] - g["x1"], g["y2"] - g["y1"]
    ln = max(math.hypot(ax, ay), 1e-9)
    return g, (ay / ln, -ax / ln)          # the road's RIGHT for +progress


def signed_lat(car, g, n):
    return (car["x"] - g["x1"]) * n[0] + (car["y"] - g["y1"]) * n[1]


def own_lat(car, g, n):
    """Signed metres off the centreline, POSITIVE = the car's own half."""
    rad = math.radians(car["heading"])
    own = 1.0 if (math.cos(rad) * n[0] - math.sin(rad) * n[1]) > 0 else -1.0
    return own * signed_lat(car, g, n)


def main():
    g, n = geom(SEG)
    print(f"segment {g['name']}: y {g['y1']} -> {g['y2']}, {g['length']:.0f} m, "
          f"width {g['width']} m")
    resp = api("POST", "/cars", {"action": "clear"})   # the API's spelling
    print(f"clear field: {str(resp)[:90]}")
    for _ in range(40):
        if not uids():
            break
        time.sleep(0.5)
    if uids():
        print("!! the field is not empty after clear - run against a fresh host")
        return 1

    def y_to_p(y):
        return (g["y1"] - y) / g["length"]

    api("POST", "/freeze", {"frozen": True})
    tr1 = spawn_new(y_to_p(TR1_Y), 10 / 3.6, 10.0, "tractor")
    tr2 = spawn_new(y_to_p(TR1_Y + ROW_GAP), 10 / 3.6, 10.0, "tractor")
    c3 = spawn_new(y_to_p(TR1_Y + ROW_GAP + TRAIL), 80 / 3.6, 80.0)
    onc = spawn_new(y_to_p(ONC_Y), 40 / 3.6, 40.0, reverse=True)
    if None in (tr1, tr2, c3, onc):
        print("!! staging incomplete")
        return 1
    print(f"staged: row t1={tr1} t2={tr2} (10 km/h), c3={c3} (80 km/h), "
          f"oncoming={onc} (40 km/h, opposite lane)")
    api("POST", "/freeze", {"frozen": False})

    t0 = state()["time"]
    watch = {tr1, tr2, c3, onc}
    stands, run = {}, {}
    lat_min = {}                 # most past the centreline (negative = borrowed)
    nominal = {}                 # first sight of each car = its own nominal line
    creep = {}                   # metres moved toward the centreline
    sign_back = {}               # (sim_t, y_c3, y_t1) at a borrowed -> own crossing
    caps = {}
    prev_t = t0
    # Car.Decisions is a 100-entry ring (Car.cs, MaxDecisions = 100): a 45 s run
    # pushes the row's first decisions out of it, and those ARE the G1 evidence.
    # Collect them while the run goes.
    dec, seen_steps = {}, set()

    def harvest(st):
        for c in st.get("cars", []):
            u = c["car_uid"]
            try:
                rows = api("GET", f"/car/{u}/decisions").get("decisions", [])
            except Exception:
                continue
            for e in rows:
                k = (u, e.get("step"))
                if k in seen_steps:
                    continue
                seen_steps.add(k)
                dec.setdefault(u, []).append(e)

    while state()["time"] - t0 < RUN_S:
        st = state()
        now = st["time"]
        dt = max(now - prev_t, 0.0)   # stands are counted in SIM seconds, not
        prev_t = now                  # one increment per poll (the polls are 1 s
        pos = {}                      # apart, 1/60 per poll undercounted by 60x)
        c3_speed_kmh = 0.0
        for c in st.get("cars", []):
            u = c["car_uid"]
            lat = own_lat(c, g, n)
            pos[u] = (c["y"], lat)
            nominal.setdefault(u, lat)
            if u == c3:
                c3_speed_kmh = c["speed_kmh"]
            if u in watch:
                lat_min[u] = min(lat_min.get(u, 9.9), lat)
                creep[u] = max(creep.get(u, 0.0), nominal[u] - lat)
            if c.get("cap_key"):
                caps[c["cap_key"]] = caps.get(c["cap_key"], 0) + 1
            if c["speed_kmh"] / 3.6 < 0.05:
                run[u] = run.get(u, 0.0) + dt
                stands[u] = max(stands.get(u, 0.0), run[u])
            else:
                run[u] = 0.0
        # a borrowed -> own crossing of c3, with where the row front was then
        y3, lat3 = pos[c3]
        if c3 not in sign_back:
            sign_back[c3] = []
        if lat3 < -0.3:
            sign_back[c3].append(["out", y3])
        elif lat3 > 0.3 and sign_back[c3] and sign_back[c3][-1][0] == "out":
            sign_back[c3].append(["back", y3, pos[tr1][0]])
        print(f"  t={now - t0:5.1f}s | c3 y={y3:6.1f} lat={lat3:+.2f} "
              f"v={c3_speed_kmh:.0f} | row t1 y={pos[tr1][0]:6.1f} "
              f"lat={pos[tr1][1]:+.2f} | worst stand "
              f"{max(list(stands.values()) or [0.0]):.1f}s", flush=True)
        harvest(st)
        time.sleep(1.0)

    harvest(state())    # the tail between the last poll and the end
    for u in (tr1, tr2, c3, onc):
        dec.setdefault(u, [])

    def first_decision(u, pat, t_from):
        for e in dec[u]:
            if e.get("t", 0.0) >= t_from and pat in e.get("msg", ""):
                return e["t"]
        return None

    t_declined = first_decision(tr2, "[R37] intent: no pass wanted", t0)
    t_tr2_intent = first_decision(tr2, "[R37] intent:", t0)
    t_c3_commit = first_decision(c3, "[R32] obstruction ahead - borrowing", t0)
    t_c3_signal = first_decision(c3, "[R17]", t0)
    t_onc_owner = first_decision(tr1, "[R39]", t0) or first_decision(tr2, "[R39]", t0)

    g1 = t_declined is not None
    g2 = lat_min.get(tr2, 0.0) > -0.3
    g3 = t_c3_commit is not None and lat_min.get(c3, 9.9) < -0.3
    crossings = [x for x in sign_back.get(c3, []) if x[0] == "back"]
    # c3 may come off the row only after it is past its front: its y must be
    # south of t1's y minus both half-lengths (south = smaller y on this road).
    g4 = bool(crossings) and all(x[1] < x[2] - 6.0 for x in crossings)
    g5a = (t_c3_signal is not None and t_c3_commit is not None
           and t_c3_signal < t_c3_commit and creep.get(c3, 0.0) > 0.5)
    worst = max([(v, u) for u, v in stands.items() if u in watch] or [(0.0, 0)])
    g5b = worst[0] < 2.0

    print("\n================ VERDICT ================")
    print(f"G1 t2 declines a pass in words: {'✅' if g1 else '❌'} "
          f"(declined at t={t_declined!r}; t2's own intent line at {t_tr2_intent!r})")
    print(f"G2 t2 never leaves its lane: {'✅' if g2 else '❌'} "
          f"(deepest {lat_min.get(tr2, 0):+.2f} m past the centreline)")
    print(f"G3 c3 passes the row: {'✅' if g3 else '❌'} "
          f"(commit at t={t_c3_commit!r}, deepest {lat_min.get(c3, 0):+.2f} m)")
    print(f"G4 the return is past the row front: {'✅' if g4 else '❌'} "
          f"({len(crossings)} borrowed->own crossings, "
          f"each needs c3 y < t1 y - 6 m: {[(round(a[1],1), round(a[2],1)) for a in crossings]})")
    print(f"G5a c3 signals and creeps before committing: {'✅' if g5a else '❌'} "
          f"(signal t={t_c3_signal!r} vs commit t={t_c3_commit!r}, creep {creep.get(c3,0):.2f} m)")
    print(f"G5b nobody stands over 2 s: {'✅' if g5b else '❌'} "
          f"(worst {worst[0]:.1f} s on uid {worst[1]})")
    print(f"the lane owner's R39 line appears: {'✅' if t_onc_owner else 'no'} "
          f"(t={t_onc_owner!r})")
    print(f"caps seen: {dict(sorted(caps.items()))}")
    hard = g2 and g3 and g4 and g5a and g5b
    print(f"chain mechanics (G2,G3,G4,G5a,G5b): {'✅ PASS' if hard else '❌ FAIL'}"
          f"   intent case B (G1): {'✅' if g1 else '❌ open - no driver preference exists'}")
    return 0 if (hard and g1) else 1


if __name__ == "__main__":
    sys.exit(main())
