#!/usr/bin/env python3
"""Stage ONE claimed bay on a live host, freeze it, so the client can be screenshotted.

The geometry here is explicit and printed, so the drawn frame can be checked in metres:
two parked flanks leave a 5.40 m mouth on pkw_n->pkw_s, the ask (green flag) sits at
the mouth, one sedan approaches in a driving lane. Polls conditions, prints what it
sees; no blind waits.

Usage:
    BAY_STAGE_URL=http://127.0.0.1:5013 python3 tests/bay_frame_stage.py
"""
import urllib.request
import urllib.parse
import json
import math
import time
import os

API = os.environ.get("BAY_STAGE_URL", "http://127.0.0.1:5013")
SEG = "pkw_n\u2192pkw_s"
PARK_LAT_M = 8.35
DRIVE_LAT_M = 5.25
U_REAR = 143.53                 # parked flank centres -> mouth 145.10..150.50 = 5.40 m
U_FRONT = 152.07
APPROACH_U = 135.0
APPROACH_MPS = 3.0

_ax = _ay = _dx = _dy = 1.0    # segment frame, filled by geom()


def u_of(x, y):
    """Arc position (m) along the segment."""
    return (x - _ax) * _dx + (y - _ay) * _dy


def req(method, path, body=None, timeout=6):
    data = json.dumps(body).encode() if body is not None else None
    hdr = {"Content-Type": "application/json"} if data else {}
    r = urllib.request.Request(API + path, data=data, method=method, headers=hdr)
    with urllib.request.urlopen(r, timeout=timeout) as f:
        return json.load(f)


def state():
    return req("GET", "/state")


def cars():
    return state().get("cars", [])


def car_uids():
    return {c["car_uid"] for c in cars()}


def parked_of():
    return {c["uid"]: c for c in cars() if c.get("state") == "parked"}


def poll(what, pred, tries=25, step=1.0):
    for i in range(tries):
        try:
            v = pred()
            if v:
                return v
        except Exception as e:
            print(f"  [{what}] unreadable: {e}", flush=True)
        print(f"  [{what}] {i + 1}s …", flush=True)
        time.sleep(step)
    print(f"  [{what}] NOT reached — that is the finding", flush=True)
    return None


def main():
    global _ax, _ay, _dx, _dy
    g = req("GET", "/segment/by/" + urllib.parse.quote(SEG, safe=""))
    L = math.hypot(g["x2"] - g["x1"], g["y2"] - g["y1"])
    _ax, _ay, _dx, _dy = g["x1"], g["y1"], (g["x2"] - g["x1"]) / L, (g["y2"] - g["y1"]) / L
    print(f"segment idx={g['idx']} {SEG}: {L:.0f} m, width {g['width']}")
    print(f"  parked flanks at u={U_REAR:.2f} / {U_FRONT:.2f}  (mouth "
          f"{U_REAR + 3.14 / 2:.2f}..{U_FRONT - 3.14 / 2:.2f} = {U_FRONT - U_REAR - 3.14:.2f} m)")
    req("POST", "/cars", {"action": "clear"})
    if poll("field empty", lambda: len(cars()) == 0) is None:
        return
    for name, u in (("p_rear", U_REAR), ("p_front", U_FRONT)):
        before = car_uids()
        req("POST", "/teleport", {"segment": g["idx"], "progress": u / L,
                                  "lat_m": PARK_LAT_M, "add": True,
                                  "vehicle": "fiat_500", "name": name, "speed": 0.0})
        uid = poll("new car appears", lambda: (car_uids() - before) or None)
        if not uid:
            return
        uid = sorted(uid)[0]
        req("POST", "/park", {"uid": uid})
        print(f"  parked {name} -> uid {uid} at u={u:.2f}", flush=True)
    if poll("both flanks parked",
            lambda: len([c for c in cars() if c.get("state") == "parked"]) == 2) is None:
        return
    ask_x, ask_y = g["x1"] + _dx * 150.0, g["y1"] + _dy * 150.0
    req("POST", "/flags", {"green": [ask_x, ask_y]})
    print(f"  ask (green flag) at ({ask_x:.2f},{ask_y:.2f}) = u=150.00", flush=True)
    before = car_uids()
    req("POST", "/teleport", {"segment": g["idx"], "progress": APPROACH_U / L,
                              "lat_m": DRIVE_LAT_M, "add": True,
                              "speed": APPROACH_MPS, "vehicle": "mercedes_c220",
                              "name": "arriving"})
    new = poll("arriving car appears", lambda: (car_uids() - before) or None)
    if not new:
        return
    mover = sorted(new)[0]
    req("POST", "/follow", {"uid": mover})
    print(f"  arriving mercedes_c220 uid={mover} at u={APPROACH_U:.2f}, "
          f"{APPROACH_MPS * 3.6:.0f} km/h", flush=True)

    print("polling until the car claims a bay (expect park_target with the two flanks)", flush=True)
    claim = None
    for i in range(60):
        time.sleep(1)
        st = state()
        for c in st.get("cars", []):
            t = c.get("park_target")
            if isinstance(t, dict) and t.get("lengthM", 0) > 0:
                claim = (c, t)
                break
        if claim:
            c, t = claim
            print(f"  CLAIM after {i + 1}s: car uid={c['car_uid']} x={c['x']:.2f} y={c['y']:.2f} "
                  f"u={u_of(c['x'], c['y']):.2f} v={c.get('speed_kmh', 0):.1f} km/h state={c.get('state')}")
            print(f"  park_target: {json.dumps(t)}")
            break
        if i % 5 == 4:
            print("  t=%ds " % (i + 1) + ", ".join(
                f"{c['car_uid']}:{c.get('state')}/{(c.get('parking') or {}).get('phase', '?')}"
                f"@u={u_of(c['x'], c['y']):.1f}" for c in st.get("cars", [])), flush=True)
    if not claim:
        print("NO CLAIM — stop here, that is the finding", flush=True)
        return

    req("POST", "/freeze", {"frozen": True})
    t0 = state().get("time", 0)
    poll("clock frozen", lambda: state().get("time", 0) == t0, tries=8, step=0.5)
    st = state()
    c = next(x for x in st.get("cars", []) if x["car_uid"] == claim[0]["car_uid"])
    t = c.get("park_target")
    fl = [x for x in st["cars"] if x.get("state") == "parked"]
    print(f"\nTHE FROZEN FRAME (what the client must draw):", flush=True)
    print(f"  car   uid={c['car_uid']} at ({c['x']:.2f},{c['y']:.2f}) u={u_of(c['x'], c['y']):.2f}")
    for x in fl:
        print(f"  flank uid={x['car_uid']} at ({x['x']:.2f},{x['y']:.2f}) u={u_of(x['x'], x['y']):.2f} "
              f"len_m={x.get('length_m')}")
    print(f"  box   centre ({t['xM']:.2f},{t['yM']:.2f}) u={u_of(t['xM'], t['yM']):.2f} "
          f"lengthM={t['lengthM']} widthM={t['widthM']} headingDeg={t['headingDeg']} inside={t['inside']}")
    print("The client draws this at zoom Z px/m -> the box must measure "
          f"{t['lengthM'] * 18:.0f} x {t['widthM'] * 18:.0f} px at Z=18.")


main()
