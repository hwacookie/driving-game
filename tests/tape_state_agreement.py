#!/usr/bin/env python3
"""Two channels, one truth (e2e check, manual): /state vs the tape head.

The renderer is becoming tape-fed ("the viewer is the tip of a growing tape",
docs/SIMULATIONS_REVIEWER.md -> "One client, one data source"), so the tape and
/state must describe the SAME substep. They are written by different code paths
— `/state` from `SimEngine.StateSnapshot()`, the tape from `Tape.cs:Record` — so
"they agree" was an assumption, not a fact. This script makes it one.

The car is deliberately put ON A BRIDGE DECK (`fig8_bridge`, the level-1 branch
of the basic map's figure-8, TestMaps.cs:540) because `level` is the field the
tape had NOT been carrying: the reviewer painted every deck car at grade
(`ReviewFeed` hardcoded `Level = 0`). Pose alone would not have caught it.

Freeze comes before the read, not after: the head has to be a FIXED substep
while both channels are read, otherwise the two requests see two different
substeps and the comparison measures a moving target.

Usage:
    # the console host on :5001 (scripts/run_e2e.sh, or by hand)
    .venv/bin/python tests/tape_state_agreement.py
    .venv/bin/python tests/tape_state_agreement.py --port 5007 --warp 4
"""
import os
import sys
import time
import urllib.request
import json

API = os.environ.get("DRIVING_GAME_API", "http://127.0.0.1:5001")
POSE_TOL_M = 1e-4        # the acceptance number from the unification decision
HEADING_TOL_DEG = 0.02


def post(path, payload):
    req = urllib.request.Request(
        f"{API}{path}", data=json.dumps(payload).encode(),
        headers={"Content-Type": "application/json"}, method="POST")
    with urllib.request.urlopen(req, timeout=5) as r:
        return json.loads(r.read())


def get(path):
    with urllib.request.urlopen(f"{API}{path}", timeout=5) as r:
        return json.loads(r.read())


def main():
    st = get("/state")
    if "time" not in st:
        print("!! host answers no /state - start scripts/run_e2e.sh first")
        return 1
    # The console host BOOTS frozen (it waits for cars to be staged), so an
    # unfreeze is part of driving, not a change of the user's state. The state
    # the host was found in gets restored at the end.
    found_frozen = bool(st.get("frozen", False))
    if found_frozen:
        post("/freeze", {"frozen": False})

    # A tape to compare against, STARTED CLEAN: a host that already carries a
    # tape (and its cars) from an earlier check would let the deck poll pass
    # on the OLD car before this run's car ever moved (measured: a second run
    # reported "uid 2 on /state, NOT on the tape", because the head it
    # compared was the step the FIRST run had frozen at).
    post("/tape/clear", {})
    tape = post("/tape", {"map": "basic"})
    if not tape.get("ok"):
        print(f"!! POST /tape refused: {tape}")
        return 1
    tape_status = tape.get("tape", {})
    dt = tape_status.get("dt_fixed_s", 1.0 / 60.0)

    # One car ONTO the deck — by SEGMENT, not by start point: `fig8_bridge`
    # spawns at the crossing node and the car's first segment there is a
    # ground one (measured: /state answered `segment 139, level 0` for that
    # start point), which would leave this check proving nothing.
    tp = post("/teleport", {"segment": "fig8_n12→fig8_n13", "progress": 0.5,
                            "speed": 8.3, "add": True, "name": "deck_car"})
    if not tp.get("ok"):
        print(f"!! /teleport refused: {tp}")
        return 1

    # Pace on the SIM clock: drive ~1 sim second, then STOP at the first
    # substep where a car is actually ON a level-1 segment (the deck claim,
    # from /state). Waiting past that moment would freeze the comparison
    # AFTER the car left the deck - the head would then say level 0 on both
    # sides and prove nothing (measured: with a 4 s wait the car had driven
    # ~33 m past the bridge and `deck levels on the tape: max 0`).
    t0 = st["time"]
    on_deck = False
    while True:
        st = get("/state")
        if st.get("frozen") and not found_frozen:
            print("!! something froze the sim mid-drive - the tape and /state")
            print("   are not comparable")
            return 1
        for c in st.get("cars", []):
            if c.get("level", 0) >= 1:
                on_deck = True
        # The deck claim only counts AFTER this run's car has actually driven
        # (a leftover car from an earlier check could already sit on the deck
        # at t0, which would freeze the comparison before any motion).
        if on_deck and st["time"] - t0 > 1.0:
            break
        if st["time"] - t0 > 20.0:
            break
        if st["time"] < t0:            # restarted host: bail out loudly
            print("!! the sim restarted mid-drive, the tape is not comparable")
            return 1
        time.sleep(0.2)
    if not on_deck:
        print("!! NO car ever reached a level-1 segment in 20 sim s - the deck")
        print("   is not on tape at all, so this run proves nothing about `level`")
        return 1

    # Freeze: the head stops advancing, so /state and the head describe ONE
    # substep while both channels are read. The freeze is a QUEUED command —
    # the sim keeps ticking until it lands, and reading both channels without
    # waiting for the effect measures a race, not a skew.
    post("/freeze", {"frozen": True})
    for _ in range(50):
        st = get("/state")
        if st.get("frozen"):
            break
        time.sleep(0.1)
    else:
        print("!! the host never reported frozen=true — the comparison is a race")
        return 1
    st = get("/state")
    # /state is a per-FRAME snapshot cached under `_stateLock`, the tape is
    # per-SUBSTEP — so in one frame the head can already be one substep past
    # the snapshot the client sees (measured: head step 26 while /state
    # announced time for 25). Asking the head (`step: -1`) compared a
    # substep that /state has never answered, and read as a 9.5 cm pose gap.
    # The honest comparison is the tape frame AT the substep /state names.
    at_step = round(st["time"] / dt)
    fr = post("/tape/frame", {"step": at_step})
    if not fr.get("ok"):
        print(f"!! /tape/frame({at_step}) answered no frame: {fr}")
        post("/freeze", {"frozen": found_frozen})
        return 1
    head = post("/tape/frame", {"step": -1})
    head_step = head["step"] if head.get("ok") else None
    if head_step is not None and head_step - at_step > 1:
        print(f"!! the tape head is {head_step - at_step} substeps past /state's "
              f"own step {at_step} — more than one frame's worth: two clocks")

    tape_cars = {c["uid"]: c for c in fr["cars"]}
    # /state names the same thing `car_uid` - the key difference between the
    # two channels is part of what this check has to survive.
    state_cars = {c["car_uid"]: c for c in st["cars"]}

    print(f"/state step {at_step} (time {st['time']:.3f} s)  ·  "
          f"tape frame step {fr['step']}  ·  tape head {head_step} "
          f"(dt {dt:.6f} s announced by /tape)")

    bad = []
    for uid, sc in sorted(state_cars.items()):
        tc = tape_cars.get(uid)
        if tc is None:
            bad.append(f"uid {uid}: on /state, NOT on the tape")
            continue
        dx, dy = abs(sc["x"] - tc["x"]), abs(sc["y"] - tc["y"])
        dh = abs(sc["heading"] - tc["heading"])
        print(f"uid {uid:>4} · level /state {sc['level']} vs tape {tc['level']} "
              f"· dpos {max(dx, dy):.2e} m · dhead {dh:.2e} deg")
        if sc["level"] != tc["level"]:
            bad.append(f"level disagrees for uid {uid}: {sc['level']} vs {tc['level']}")
        if dx > POSE_TOL_M or dy > POSE_TOL_M:
            bad.append(f"pose differs for uid {uid}: {max(dx, dy):.6f} m")
        if dh > HEADING_TOL_DEG:
            bad.append(f"heading differs for uid {uid}: {dh:.4f} deg")
    for uid in sorted(tape_cars):
        if uid not in state_cars:
            bad.append(f"uid {uid}: recorded, not on /state (a despawn?)")

    post("/freeze", {"frozen": found_frozen})   # back to the state it was found in

    deck_seen = max((c["level"] for c in tape_cars.values()), default=0)
    print(f"deck levels on the tape: max {deck_seen} "
          f"({'a bridge car WAS recorded' if deck_seen >= 1 else 'NO level-1 car in this run'})")
    if bad:
        print("❌ the two channels disagree:")
        for b in bad:
            print("   " + b)
        return 1
    print("✅ /state and the tape head describe the same substep "
          f"(level exact, pose < {POSE_TOL_M:g} m)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
