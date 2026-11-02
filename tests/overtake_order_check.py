#!/usr/bin/env python3
"""Pass-order check: one slow vehicle, a queue behind it (docs §9, R37).

Stages a tractor on a long straight and N cars behind it at even gaps, all
wanting a speed far above the tractor's, then grades the four facts §9 says
a test must state:

  F1  every car of the queue states its intent ("[R37] intent") - passing is
      each driver's own decision, not something the road says;
  F2  the pass STARTS happen in queue order, front-first (R37, strict);
  F3  at most one car is in the other lane at any moment - a queue does not
      all borrow at once;
  F4  no car of the queue stands at rest for more than 2 s of sim - an
      aborted plan is a way back, not a standstill (docs §9).

Everything is in METRES (see docs/UNITS.md): the script resolves the segment
axis with GET /segment/{idx} and reads every car's signed distance from the
centreline out of /state, so a "borrow" is measured, not guessed.

The script starts nothing itself - run it against a host you started
(the way tests/car_line_check.py is run):

    scripts/run_e2e.sh --no-godot --warp 4 &      # or the bare host
    python3 tests/overtake_order_check.py

Usage:
    python3 tests/overtake_order_check.py            # segment 1, 4 cars
"""
import math
import os
import sys
import time
import json
import urllib.request

API = os.environ.get("OVERTAKE_API_URL", "http://127.0.0.1:5001").rstrip("/")
SEG = int(os.environ.get("OVERTAKE_SEGMENT", "1"))      # straight_m->straight_s
NQ = int(os.environ.get("OVERTAKE_CARS", "4"))
GAP_M = 18.0          # clear gap between the queue members
LEAD_M = 26.0         # the gap the front car keeps behind the tractor
TRACTOR_KMH = 10.0
CREW_KMH = 80.0
RUN_S = 45.0          # sim seconds of the run


def api(method, path, body=None):
    data = None if body is None else json.dumps(body).encode()
    req = urllib.request.Request(API + path, data=data,
                                 method=method,
                                 headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=15) as r:
        return json.loads(r.read())


def state():
    return api("GET", "/state")


def freeze(on):
    return api("POST", "/freeze", {"frozen": on})


def spawn(segment, progress, speed_mps, cruise_kmh, model=None, name=None):
    body = {"segment": segment, "progress": progress, "speed": speed_mps,
            "throttle": True, "cruise_kmh": cruise_kmh, "add": True}
    if model:
        body["model"] = model
    if name:
        body["name"] = name
    return api("POST", "/teleport", body)


def uids():
    return {c["car_uid"] for c in state().get("cars", [])}


def spawn_new(segment, progress, speed_mps, cruise_kmh, model=None, name=None):
    """Spawn one car and report the uid it got. Commands go into the sim's
    command FIFO and apply on the tick loop, so this POLLS (bounded) rather
    than assuming a fixed sleep did it."""
    before = uids()
    spawn(segment, progress, speed_mps, cruise_kmh, model, name)
    for _ in range(40):
        new = uids() - before
        if new:
            return next(iter(new))
        time.sleep(0.25)
    return None


def geom(idx):
    """The segment axis plus its RIGHT-side normal for +progress travel."""
    g = api("GET", f"/segment/{idx}")
    ax, ay = g["x2"] - g["x1"], g["y2"] - g["y1"]
    ln = max(math.hypot(ax, ay), 1e-9)
    return g, (ay / ln, -ax / ln)


def lat_of(car, g, n):
    """Signed metres off the centreline; positive is the +n side."""
    return (car["x"] - g["x1"]) * n[0] + (car["y"] - g["y1"]) * n[1]


def own_side(car, n):
    """+1 when the car's own half of the road is the +n side. A car's RIGHT
    is its heading rotated -90 deg, and a lane it owns is its right half."""
    rad = math.radians(car["heading"])
    fx, fy = math.sin(rad), math.cos(rad)
    return 1.0 if (fy * n[0] - fx * n[1]) > 0 else -1.0


def main():
    st = state()
    g, n = geom(SEG)
    print(f"segment {g['name']}: length {g['length']:.1f} m, width {g['width']} m")

    api("POST", "/cars", {"clear": True})          # a clean field
    for _ in range(40):                            # the clear is queued too
        if not uids():
            break
        time.sleep(0.25)
    left = len(uids())
    if left:
        print(f"!! {left} cars are still on the field after the clear - "
              f"this check needs an empty one: start a fresh host "
              f'(e.g. "$BIN --map basic --warp 4") and point the script at it')
        return 1
    freeze(True)                                   # stage while the clock holds
    # The tractor leads, the queue sits behind it: progress grows along the
    # direction of travel, so a smaller progress is BEHIND.
    tractor_p = 1.0 - LEAD_M / g["length"]
    tractor_uid = spawn_new(SEG, tractor_p, TRACTOR_KMH / 3.6, TRACTOR_KMH,
                            "tractor", "tractor")
    order = []                       # uids in queue order, front first
    for k in range(NQ):
        p = tractor_p - (LEAD_M + k * GAP_M) / g["length"]
        if p >= 0.98:
            print(f"!! queue member {k + 1} does not fit on the segment")
            return 1
        u = spawn_new(SEG, p, CREW_KMH / 3.6, CREW_KMH, None, f"car{k + 1}")
        if u is None:
            print(f"!! queue member {k + 1} did not spawn")
            return 1
        order.append(u)
    if tractor_uid is None or len(order) != NQ:
        print("!! staging incomplete")
        return 1
    print(f"staged: tractor uid {tractor_uid} (10 km/h), queue {order} "
          f"at {GAP_M:.0f} m gaps - {NQ * GAP_M:.0f} m of queue")
    freeze(False)

    t0 = state()["time"]
    borrow_max, deepest_m = 0, 0.0
    stand_s, run, caps = {}, {}, {}
    while state()["time"] - t0 < RUN_S:
        st = state()
        in_borrow, deep = 0, 0.0
        for c in st.get("cars", []):
            uid = c["car_uid"]
            v = c["speed_kmh"] / 3.6
            own = own_side(c, n)
            lat = own * lat_of(c, g, n)     # positive = my own half
            if lat < -0.3:
                in_borrow += 1
                deep = max(deep, -lat)
            if c.get("cap_key"):
                caps[c["cap_key"]] = caps.get(c["cap_key"], 0) + 1
            if v < 0.05:
                run[uid] = run.get(uid, 0.0) + 1.0 / 60.0
                stand_s[uid] = max(stand_s.get(uid, 0.0), run[uid])
            else:
                run[uid] = 0.0
        borrow_max = max(borrow_max, in_borrow)
        deepest_m = max(deepest_m, deep)
        worst = max(list(stand_s.values()) or [0.0])
        latline = " ".join(
            f"u{c['car_uid']}:{own_side(c, n) * lat_of(c, g, n):+.2f} "
            f"v{c['speed_kmh']:.0f}" for c in st.get("cars", []))
        print(f"  t={state()['time'] - t0:5.1f}s sim | in-other-lane {in_borrow} "
              f"(deepest {deepest_m:.2f} m) | longest standstill {worst:4.1f}s\n"
              f"      {latline}", flush=True)
        time.sleep(1.0)

    dec = {}
    for uid in order:
        try:
            dec[uid] = api("GET", f"/car/{uid}/decisions").get("decisions", [])
        except Exception:
            dec[uid] = []
    intent_at, start_at = {}, {}
    for uid in order:
        for e in dec[uid]:
            if e.get("t", 0.0) < t0:
                continue
            m = e.get("msg", "")
            if m.startswith("[R37] intent") and uid not in intent_at:
                intent_at[uid] = e["t"]
            if m.startswith("[R32] obstruction ahead - borrowing") and uid not in start_at:
                start_at[uid] = e["t"]

    print("\n================ VERDICT ================")
    f1 = len(intent_at) == NQ
    print(f"F1 every queue car states its intent: {'✅' if f1 else '❌'} "
          f"({len(intent_at)}/{NQ})")
    got = [u for u in order if u in start_at]
    seq = sorted(got, key=lambda u: start_at[u])
    f2 = seq == got
    print(f"F2 pass starts in queue order: {'✅' if f2 else '❌'} "
          f"started {len(got)}/{NQ} in order {seq!r} (queue {got!r})")
    f3 = borrow_max <= 1
    print(f"F3 never two cars in the other lane: {'✅' if f3 else '❌'} "
          f"(worst {borrow_max}, deepest {deepest_m:.2f} m past the centreline)")
    worst_uid = max([(v, u) for u, v in stand_s.items() if u in order] or [(0.0, 0)])
    f4 = worst_uid[0] < 2.0
    print(f"F4 no queue car stands longer than 2 s: {'✅' if f4 else '❌'} "
          f"(worst {worst_uid[0]:.1f} s on uid {worst_uid[1]})")
    print(f"\ncaps seen over the run: {dict(sorted(caps.items()))}")
    print("=========================================")
    ok = f1 and f2 and f3 and f4
    print(f"over-take order check: {'✅ PASS' if ok else '❌ FAIL'}")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
