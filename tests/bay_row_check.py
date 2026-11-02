#!/usr/bin/env python3
"""The bay-row run (owner design, 2026-11-02) — docs/DRIVING_MANEUVERS.md §1c.

A kerb row of five parked cars leaves FOUR bays of different slack. Five arriving
cars — four sedans and one Fiat — are put on the road at once, spaced behind each
other, and all roll toward the row. Nothing is ordered: no car waits for the
previous one to rest (the owner dropped that condition; a following car meeting a
stopped one is exactly what makes this run interesting — see the OBSERVATION
section it prints).

What is GRADED is §1c tier 1 — which bay a car may use. A gap is a bay FOR THIS
CAR when the free space along the road (the gap minus the car's OWN length) is a
SUM of at least 1.00 m; the split fore/aft is free; the row is scanned in
driving order and the FIRST fitting gap is taken. The script computes that choice
itself, from the rule and the reported model lengths, and compares it with what
the sim did — a bay decision that does not change with the vehicle is not testing
the rule.

What is OBSERVED, not graded: the reverse-in execution through a tight bay. That
part is postponed (the shuffle through a tight bay, §1b); the run says plainly
which cars reached `parked` and which gave up, with the numbers.

Usage:
    scripts/open.sh --map basic                 # the window, API on :5001
    python3 tests/bay_row_check.py              # this run against it
    BAY_API_URL=http://127.0.0.1:5002 python3 tests/bay_row_check.py
"""
import os
import sys
import time
import math
import json
import urllib.request
import urllib.parse

API_URL = os.environ.get("BAY_API_URL", "http://127.0.0.1:5012")

SEG = "pkw_n→pkw_s"          # the 300 m parking avenue (19.4 m: driving 0→7.0, parking 7.0→9.7)
PARK_LAT_M = 8.35            # the parking lane, measured (0.45 m to the kerb)
ROW_START_M = 120.0          # centre of the first parked car, metres along the avenue
BAY_GAPS_M = [6.90, 4.60, 5.40, 4.90]   # edge-to-edge: c220 (4.40) + slack 2.5 / 0.2 / 1.0 / 0.5
FLANK_MODEL = "fiat_500"     # 3.14 m — the row's own cars; only its reported length is used

# The rule (§1c tier 1, owner decision 2026-11-02). A SUM, with an explicit ε:
# the graded case sits exactly on the rule (a 5.40 m bay, a 4.40 m body).
MIN_FREE_M = 1.00
FREE_EPS_M = 1e-6

# (label, model, metres behind the first arriving car) — pm3 is the Fiat on foot:
# it must take the bay the sedans had to refuse, or the rule is not being tested.
ARRIVING = [("pm1", "mercedes_c220", 0.0),
            ("pm2", "mercedes_c220", -8.0),
            ("pm3", "fiat_500",      -16.0),
            ("pm4", "mercedes_c220", -24.0),
            ("pm5", "mercedes_c220", -32.0)]
APPROACH_MPS = 5.5           # slow past the row — a driver looking for a gap
SIM_LIMIT_S = float(os.environ.get("BAY_LIMIT_S", "150"))  # sim seconds before unfinished
POLL_S = 0.2

FLANK_LEN_M = 3.14           # only for the SPACING print; every measurement uses
                             # the sim's own reported length_m.
SPAWN_S = 14.0               # the head car starts this far before the row
ROW_MARK_PROGRESS = 0.46     # the mark, at the middle of the row: the ASK. The
                             # sim's anchor is what turns that into a gap.


def _c(t, code):
    return f"\033[{code}m{t}\033[0m" if sys.stdout.isatty() else t


def cyan(t): return _c(t, "36")
def green(t): return _c(t, "32")
def red(t): return _c(t, "31")
def yellow(t): return _c(t, "33")
def dim(t): return _c(t, "2")


def _req(method, path, body=None):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(API_URL + path, data=data, method=method,
                                headers={"Content-Type": "application/json"} if data else {})
    try:
        with urllib.request.urlopen(req, timeout=5) as r:
            return json.loads(r.read())
    except Exception:
        return None


def get_state():
    try:
        with urllib.request.urlopen(API_URL + "/state", timeout=5) as r:
            return json.loads(r.read())
    except Exception:
        return None


def segment_geom():
    g = _req("GET", "/segment/by/" + urllib.parse.quote(SEG))
    if not isinstance(g, dict):
        return None
    return g


def bay_choice(car_len_m, flanks_u, flanks_len):
    """§1c tier 1 as the SCRIPT reads it: flanks sorted along the driving
    direction, first gap whose free space (gap − this car's own length) reaches
    MIN_FREE_M. Returns (index, rear_end_m, front_end_m, free_m) or None."""
    order = sorted(range(len(flanks_u)), key=lambda i: flanks_u[i])
    for a, b in zip(order, order[1:]):
        rear_end = flanks_u[a] + flanks_len[a] / 2.0
        front_end = flanks_u[b] - flanks_len[b] / 2.0
        free = front_end - rear_end - car_len_m
        if free + FREE_EPS_M >= MIN_FREE_M:
            return (len([x for x in order if x <= a]), rear_end, front_end, free)
    return None


def extent_along(c, geom):
    """A car's fore/aft span along the avenue, yaw included (the metric the
    suite uses — a crooked car reaches further along the axis than its length
    suggests). Lengths and width come from the sim, not from this file."""
    x1, y1 = float(geom['x1']), float(geom['y1'])
    L = math.hypot(float(geom['x2']) - x1, float(geom['y2']) - y1)
    ux, uy = (float(geom['x2']) - x1) / L, (float(geom['y2']) - y1) / L
    seg_h = math.degrees(math.atan2(ux, uy))
    s = (c['x'] - x1) * ux + (c['y'] - y1) * uy
    head = float(c.get('heading_deg', c.get('heading', 0.0)))
    psi = math.radians((head - seg_h + 180.0) % 360.0 - 180.0)
    ln, wd = c.get('length_m'), c.get('width_m')
    if ln is None or wd is None:
        return None
    half = (ln / 2.0) * abs(math.cos(psi)) + (wd / 2.0) * abs(math.sin(psi))
    return (s - half, s + half)


def wait_for_api():
    """Bounded poll that says what it sees — never a blind fixed wait."""
    for i in range(30):
        st = get_state()
        if st:
            print(cyan(f"API up: {API_URL} (sim clock {st.get('time', 0):.1f} s)"))
            return st
        time.sleep(0.5)
        print(dim(f"  waiting for {API_URL}/state … "), flush=True)
    print(red("no API at " + API_URL))
    sys.exit(1)


def set_freeze(frozen):
    _req("POST", "/freeze", {"frozen": frozen})


def along(c, geom):
    """A car's position along the avenue in metres (the row and the bays are
    addressed along the road, not in world x/y)."""
    x1, y1 = float(geom['x1']), float(geom['y1'])
    L = math.hypot(float(geom['x2']) - x1, float(geom['y2']) - y1)
    ux, uy = (float(geom['x2']) - x1) / L, (float(geom['y2']) - y1) / L
    return (c['x'] - x1) * ux + (c['y'] - y1) * uy


def focus_row(geom, centres):
    """Aim the window at the row (the camera is cosmetic and must never fail a
    run — but a run nobody can see is not a run)."""
    x1, y1 = float(geom['x1']), float(geom['y1'])
    L = math.hypot(float(geom['x2']) - x1, float(geom['y2']) - y1)
    ux, uy = (float(geom['x2']) - x1) / L, (float(geom['y2']) - y1) / L
    mid = sum(centres) / len(centres)
    _req("POST", "/focus", {"kind": "loc", "x": x1 + ux * mid, "y": y1 + uy * mid,
                            "zoom": 12, "label": "bay_row", "scenario": "bay_row_check",
                            "desc": "five parked cars, four bays, five arriving cars"})


def clear_cars():
    """Start from an empty map: this run is about the row, not about whatever a
    previous live session left driving. /cars clear is a command on the same
    FIFO as the teleports, so the order the sim applies is the order here."""
    _req("POST", "/cars", {"action": "clear"})
    for _ in range(20):
        st = get_state()
        if st and not st.get('cars'):
            return True
        time.sleep(0.2)
    print(yellow("map did not empty — the run continues with whatever is there"))
    return False


def place_flanks(geom):
    """Put the row up, one /teleport each, then POST /park. Positions are
    computed from the reported model length, so retuning Config.MODELS moves
    the row exactly as much as the bays should move."""
    L = math.hypot(float(geom['x2']) - float(geom['x1']),
                   float(geom['y2']) - float(geom['y1']))
    u = ROW_START_M
    uids, centres = [], []
    for i in range(5):
        if i:
            u += FLANK_LEN_M / 2.0 + BAY_GAPS_M[i - 1] + FLANK_LEN_M / 2.0
        before = {c['car_uid'] for c in (get_state() or {}).get('cars', [])}
        _req("POST", "/teleport", {"segment": geom['idx'],
                                   "progress": u / L, "lat_m": PARK_LAT_M,
                                   "add": True, "vehicle": FLANK_MODEL,
                                   "name": f"p{i + 1}"})
        uid = None
        for _ in range(10):        # commands are queued: poll until the uid exists
            st = get_state()
            new = [c for c in (st or {}).get('cars', [])
                   if c['car_uid'] not in before]
            if new:
                uid = new[0]['car_uid']
                break
            time.sleep(0.2)
        if uid is None:
            print(red(f"  p{i + 1} did not appear"))
            return None, None
        _req("POST", "/park", {"uid": uid})
        uids.append(uid)
        centres.append(u)
        print(dim(f"  p{i + 1} parked at u={u:.2f} m (uid {uid})"), flush=True)
    return uids, centres


def spawn_arriving(geom):
    L = math.hypot(float(geom['x2']) - float(geom['x1']),
                   float(geom['y2']) - float(geom['y1']))
    head = ROW_START_M - SPAWN_S
    out = {}
    for name, model, back in ARRIVING:
        before = {c['car_uid'] for c in (get_state() or {}).get('cars', [])}
        _req("POST", "/teleport", {"segment": geom['idx'],
                                   "progress": (head + back) / L,
                                   "speed": APPROACH_MPS, "add": True,
                                   "vehicle": model, "name": name})
        for _ in range(10):
            st = get_state()
            new = [c for c in (st or {}).get('cars', [])
                   if c['car_uid'] not in before]
            if new:
                out[name] = new[0]['car_uid']
                break
            time.sleep(0.2)
        if name in out:
            _req("POST", "/label", {"uid": out[name], "text": name})
            # The job: "park at the row". The mark is only the ASK — §1c says the
            # spot is the gap the parked cars define, and the anchor moves this
            # mark to that gap (docs/DRIVING_MANEUVERS.md §1c).
            _req("POST", "/flags", {"uid": out[name],
                                    "red": [geom['idx'], ROW_MARK_PROGRESS],
                                    "red_nav": True})
        print(dim(f"  {name} ({model}) at u={head + back:.1f} m "
                  f"→ uid {out.get(name, '?')}"), flush=True)
    return out


def expected_choices(geom):
    """The rule's answer for every arriving car, computed on the EMPTY row first
    and then with each earlier choice taken — the row fills one car at a time,
    because a car already standing in a bay is a flank for the next one."""
    flanks_u = list(ROW_START_M + i * 0 for i in range(5))
    # real centres, from the same arithmetic as place_flanks
    flanks_u, u = [], ROW_START_M
    for i in range(5):
        if i:
            u += FLANK_LEN_M / 2.0 + BAY_GAPS_M[i - 1] + FLANK_LEN_M / 2.0
        flanks_u.append(u)
    lens = [FLANK_LEN_M] * 5
    taken = {}
    out = {}
    for name, model, back in ARRIVING:
        ln = 4.40 if model == "mercedes_c220" else 3.14
        got = bay_choice(ln, flanks_u, lens)
        if got:
            _, rear_end, front_end, free = got
            out[name] = (rear_end, front_end, free)
            # the car becomes a flank, centred (the placement rule of §1c)
            flanks_u.append((rear_end + front_end) / 2.0)
            lens.append(ln)
        else:
            out[name] = None
    return out, flanks_u


def main():
    st = wait_for_api()
    geom = segment_geom()
    if not geom:
        print(red(f"segment {SEG} not found — this run needs the basic map"))
        sys.exit(1)
    print(cyan(f"segment {SEG}: {float(math.hypot(geom['x2']-geom['x1'], geom['y2']-geom['y1'])):.0f} m"))

    print(cyan("stage: sim FROZEN while the row and the arriving cars are placed"))
    clear_cars()
    set_freeze(True)
    flank_uids, centres = place_flanks(geom)
    if not flank_uids:
        sys.exit(1)
    pm_uids = spawn_arriving(geom)
    expected, _ = expected_choices(geom)
    focus_row(geom, centres)
    set_freeze(False)
    print(cyan("running at 1x"))

    for name, uid in pm_uids.items():
        e = expected.get(name)
        if e:
            print(green(f"  rule says {name} → bay at u={e[0]:.2f}..{e[1]:.2f} "
                        f"({e[2]:.2f} m free for its own box)"))
        else:
            print(yellow(f"  rule says {name} → NO bay (every gap too tight)"))

    dead = 0
    started = (get_state() or {}).get('time', 0.0)
    last_report = 0.0
    seen_snap, seen_contact = {}, {}
    while True:
        st = get_state()
        if not st:
            dead += 1
            print(yellow(f"  /state unreadable ({dead}/5)"), flush=True)
            if dead >= 5:
                print(red("the host stopped answering"))
                sys.exit(1)
            time.sleep(POLL_S)
            continue
        dead = 0
        t = st.get('time', 0.0)
        cars = {c['car_uid']: c for c in st.get('cars', [])}
        line = []
        settled = 0
        for name, uid in pm_uids.items():
            c = cars.get(uid)
            if not c:
                line.append(f"{name}:gone")
                settled += 1
                continue
            pk = c.get('parking') or {}
            state = 'parked' if pk.get('parked') else (
                'parking' if c.get('state') in ('parking', 'reversing') else
                'stopped' if float(c.get('speed_kmh') or 0.0) < 0.5 else
                c.get('state', 'driving'))
            if state in ('parked', 'gone'):
                settled += 1
            if c.get('instant_snap'):
                seen_snap[name] = seen_snap.get(name, 0) + 1
            if c.get('in_contact'):
                seen_contact[name] = seen_contact.get(name, 0) + 1
            ph = (pk.get('phase') or '')[:8]
            held = c.get('held_by') or 0
            line.append(f"{name}:{state}{('/' + ph) if ph else ''}"
                        f"@u={along(c, geom):.1f} held_by={held}")
        if t - started > SIM_LIMIT_S:
            print(red(f"sim clock passed {SIM_LIMIT_S} s — stopping"))
            break
        if t - last_report >= 2.0:
            last_report = t
            print(dim(f"[t={t - started:6.1f} s] ") + " ".join(line), flush=True)
        if all((cars.get(uid) or {}).get('parking', {}).get('parked')
               for uid in pm_uids.values()) or settled >= len(pm_uids):
            print(green("all arriving cars have settled"), flush=True)
            break
        time.sleep(POLL_S)

    report(geom, flank_uids, pm_uids, expected, seen_snap, seen_contact)


def report(geom, flank_uids, pm_uids, expected, seen_snap, seen_contact):
    st = get_state()
    if not st:
        sys.exit(1)
    cars = {c['car_uid']: c for c in st.get('cars', [])}
    print()
    print(cyan("=== verdicts ==="))
    ok = True
    for name, uid in pm_uids.items():
        c = cars.get(uid)
        if not c:
            print(red(f"❌ {name}: gone"))
            ok = False
            continue
        pk = c.get('parking') or {}
        got = extent_along(c, geom)
        e = expected.get(name)
        if e and got:
            d_r = min(abs(got[0] - e[0]), abs(got[1] - e[1]))
            hit = d_r < 0.75
            what = (f"{name} bay: rule wants u={e[0]:.2f}..{e[1]:.2f}; "
                    f"car spans {got[0]:.2f}..{got[1]:.2f}")
            print((green("✅ " + what) if hit else red("❌ " + what)))
            ok = ok and hit
        elif e is None:
            print(yellow(f"ℹ️ {name}: rule refused every bay; "
                         f"sim says parked={bool(pk.get('parked'))}"))
        else:
            print(red(f"❌ {name}: no position"))
            ok = False
    print()
    print(cyan("=== the bay the SIM chose (park_target) vs the rule ==="))
    for name, uid in pm_uids.items():
        c = cars.get(uid)
        if not c:
            continue
        pt = c.get('park_target')
        if not isinstance(pt, dict):
            print(yellow(f"  {name}: no park_target (no bracketed bay near the spot)"))
            continue
        box_c = along({'x': pt['xM'], 'y': pt['yM'], 'heading': pt.get('headingDeg', 0)}, geom)
        half = float(pt.get('lengthM') or 0) / 2.0
        e = expected.get(name)
        if e:
            mid_rule = (e[0] + e[1]) / 2.0
            d = box_c - mid_rule
            mark = "✅" if abs(d) < 0.75 else "❌"
            print(f"{mark} {name}: sim box centre u={box_c:.2f} "
                  f"({pt.get('behind_uid')}/{pt.get('ahead_uid')} flank it); "
                  f"rule says centre u={mid_rule:.2f} (Δ {d:+.2f} m)")
        else:
            print(f"ℹ️ {name}: rule refused every bay; the sim still named a "
                  f"box at u={box_c:.2f} ({pt.get('behind_uid')}/{pt.get('ahead_uid')})")
    print()
    print(cyan("=== clearances against the row (the two numbers §1c names) ==="))
    for name, uid in pm_uids.items():
        c = cars.get(uid)
        if not c or not c.get('parking', {}).get('parked'):
            continue
        mine = extent_along(c, geom)
        if not mine:
            continue
        near = sorted(flank_uids, key=lambda u: abs(
            (cars[u]['x'] - c['x']) * 0 + math.hypot(cars[u]['x'] - c['x'],
                                                     cars[u]['y'] - c['y'])))[:2]
        ext = sorted([extent_along(cars[u], geom) for u in near if
                      extent_along(cars[u], geom)], key=lambda x: x[0])
        if len(ext) < 2:
            continue
        rear = mine[0] - ext[0][1]
        front = ext[1][0] - mine[1]
        tot = rear + front
        good = rear >= 0.20 - 1e-6 and front >= 0.20 - 1e-6 and \
            tot + FREE_EPS_M >= MIN_FREE_M
        print((green if good else red)(
            f"{'✅' if good else '❌'} {name}: rear {rear:+.2f} m / front "
            f"{front:+.2f} m (total {tot:.2f} m — needs ≥ {MIN_FREE_M:.2f} m "
            "and ≥ 0.20 m each end)"))
    print()
    print(cyan("=== OBSERVATION (not graded): what the postponed execution did ==="))
    for name, uid in pm_uids.items():
        c = cars.get(uid)
        if not c:
            continue
        pk = c.get('parking') or {}
        print(f"  {name}: parked={bool(pk.get('parked'))} style={pk.get('style')} "
              f"phase={pk.get('phase')} speed={c.get('speed', 0):.2f} m/s "
              f"snaps={seen_snap.get(name, 0)} contact-ticks={seen_contact.get(name, 0)}")
    print(f"\ntotal contacts across the run: {sum(seen_contact.values())}")
    print("did any car pass a stopped one? watch the window — the row above shows "
          "the lateral outcome, not the intent.")
    print()
    print(green("row rule holds") if ok else red("bay choices differ from the rule"))
    sys.exit(0 if ok else 2)


if __name__ == "__main__":
    main()
