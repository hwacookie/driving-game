#!/usr/bin/env python3
"""Per-frame pose trace — the instrument behind the report "the reviewer's
cars jump a tiny bit from frame to frame; the live viewer was completely
smooth" (2026-10-25).

Method (reproduce-before-fix, per AGENTS.md):

  A. LIVE pass    one car on the `basic` map's straight road, camera PINNED
                  (--center/--zoom, so the car's screen motion is its real
                  motion), the client writing its per-frame pose line through
                  --motionlog, the run recorded on the tape (/tape) and saved
                  to disk.
  B. REVIEW pass  a second client opens THAT tape at 1x. --test-play presses
                  ▶ so no hand is on the keyboard; same pinned camera, same
                  --motionlog.

The motion-log line carries the PAINTED node position of every rendered frame,
so "smooth or strobing" is a distribution instead of an opinion. At 1x the
tape advances 60 substeps/s while this display paints ~144 frames/s: a window
that does NOT interpolate between adjacent substeps holds 2-3 frames and then
jumps a whole substep — a pile of ~zero deltas plus a pile of big ones, where
a smooth window shows one even, small step every frame.

Usage:
    python3 tests/pose_stutter_check.py                 # both passes
    python3 tests/pose_stutter_check.py --seconds 8     # shorter live run
    python3 tests/pose_stutter_check.py --review-only   # reuse the saved tape

Not a pass/fail suite member (tests/test_turning.py is): this prints the
numbers, a human reads the verdict. Nothing here signals Godot — both windows
are closed the project's way, by bumping the generation file they watch.
"""
import argparse
import json
import math
import os
import statistics
import subprocess
import sys
import time
import urllib.error
import urllib.request

GODOT_BIN = "/Applications/Godot_mono.app/Contents/MacOS/Godot"
GEN_FILE = "/tmp/driving_game_godot_gen"
REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# The logs are per RUN, not per name: two clients that ever shared one path
# interleave their lines (each writes at its own offset into a file the other
# truncated), and the "evidence" becomes a merged trace of two clocks — which
# is how a phantom 1.4 s playback stall looked real for one measurement.
RUN_FILE = "/tmp/pose_stutter_run.txt"
TAPE = "/tmp/pose_trace.txt"
LIVE_LOG = "/tmp/pose_live.log"
REVIEW_LOG = "/tmp/pose_review.log"
LIVE_OUT = "/tmp/pose_live_godot.log"
REVIEW_OUT = "/tmp/pose_review_godot.log"


def stamp_paths(runid):
    """Point every log of this run at its own unique path."""
    global LIVE_LOG, REVIEW_LOG, LIVE_OUT, REVIEW_OUT
    LIVE_LOG, REVIEW_LOG = f"/tmp/pose_live_{runid}.log", f"/tmp/pose_review_{runid}.log"
    LIVE_OUT, REVIEW_OUT = (f"/tmp/pose_live_{runid}_godot.log",
                            f"/tmp/pose_review_{runid}_godot.log")

# The straight road of tile (0,0) on `basic`: x = 100 m, running south from
# y = 350 m. Centre the view mid-road so the car crosses the screen for the
# whole trace; 24 px/m makes one substep of a 50 km/h car ~5.6 px — far above
# the pixel, far below a jarring step.
CENTER = (100.0, 280.0)
ZOOM = 24.0
START_POINT = "straight"
CRUISE_KMH = 50.0


def cyan(t):
    return f"\033[36m{t}\033[0m" if sys.stdout.isatty() else t


def yellow(t):
    return f"\033[33m{t}\033[0m" if sys.stdout.isatty() else t


def red(t):
    return f"\033[31m{t}\033[0m" if sys.stdout.isatty() else t


# ---------------------------------------------------------------- REST ------

def req(port, method, path, body=None, timeout=5.0):
    data = json.dumps(body).encode() if body is not None else None
    r = urllib.request.Request(
        f"http://127.0.0.1:{port}{path}", data=data, method=method,
        headers={"Content-Type": "application/json"} if data else {})
    with urllib.request.urlopen(r, timeout=timeout) as resp:
        return json.loads(resp.read())


def poll(cond, what, timeout=60.0, every=0.2, check_clients=True):
    """Poll for the actual condition — never a blind fixed sleep. And never a
    blind wait on a DEAD client either: a window that quit itself must be
    reported as that, with its log, not as a mysterious timeout. (Closed
    windows are exactly the goal in close_clients, hence the opt-out.)"""
    t0 = time.time()
    while time.time() - t0 < timeout:
        for p, out in (CLIENTS if check_clients else []):
            if p.returncode is not None or p.poll() is not None:
                tail = ""
                try:
                    tail = "\n".join(open(out, errors="replace").read().splitlines()[-6:])
                except OSError:
                    pass
                raise SystemExit(red(f"!! the client exited while waiting for "
                                     f"{what}:\n{tail}"))
        try:
            v = cond()
            if v:
                return v
        except Exception:
            pass
        time.sleep(every)
    raise SystemExit(red(f"!! timeout waiting for {what}"))


def free_port():
    import socket
    for p in range(5090, 5140):
        with socket.socket() as s:
            try:
                s.bind(("127.0.0.1", p))
                return p
            except OSError:
                continue
    raise SystemExit(red("!! no free port in 5090..5139"))


# ------------------------------------------------------------- clients ------

CLIENTS = []      # (Popen, stdout log) of the windows THIS script started


def godot_running():
    out = subprocess.run(["pgrep", "-f", GODOT_BIN], capture_output=True, text=True)
    return [p for p in out.stdout.split() if p]


def close_clients():
    """The project's clean close: bump the generation file, the windows quit
    themselves. NEVER signal Godot (a SIGTERM aborts .NET's exit path).
    Always writes a fresh token, even when nothing seems to run: a client
    baselines this file in _Ready, so an unchanged token is what tells it
    "nobody asked me to go away"."""
    with open(GEN_FILE, "w") as f:
        f.write(f"{time.time():.6f}-{os.getpid()}\n")
    if not godot_running():
        return
    print(cyan("==> closing open Godot windows via the generation file"))
    poll(lambda: not godot_running(), "Godot windows to close", timeout=30.0,
         check_clients=False)


def start_client(args, out_path):
    if godot_running():
        # One client per trace, always: a second window on the same log path
        # corrupts the measurement silently.
        raise SystemExit(red(f"!! refusing to start a window: "
                             f"{godot_running()} still alive"))
    with open(out_path, "w") as out:
        p = subprocess.Popen([GODOT_BIN, "--path", "."] + ["--"] + args,
                             cwd=REPO, stdout=out, stderr=subprocess.STDOUT)
    CLIENTS.append((p, out_path))


# ------------------------------------------------------------ the passes ----

def live_pass(seconds):
    port = free_port()
    print(cyan(f"==> A/live: client on :{port}, camera pinned at "
               f"{CENTER} zoom {ZOOM} px/m"))
    for p in (LIVE_LOG, LIVE_OUT):
        if os.path.exists(p):
            os.remove(p)
    start_client(["--source", "embedded", "--map", "basic", "--port", str(port),
                  "--center", f"{CENTER[0]:g}", f"{CENTER[1]:g}",
                  "--zoom", f"{ZOOM:g}", "--motionlog", LIVE_LOG], LIVE_OUT)
    poll(lambda: req(port, "GET", "/health"), "the embedded API", timeout=90.0)
    req(port, "POST", "/tape", {})                       # record from here on
    req(port, "POST", "/teleport", {"start_point": START_POINT,
                                    "cruise_kmh": CRUISE_KMH,
                                    "speed": CRUISE_KMH / 3.6})
    st = poll(lambda: (req(port, "GET", "/state")
                       if req(port, "GET", "/state").get("has_car") else None),
              "the car to exist", timeout=30.0)
    t0 = st["time"]
    # Pace on the SIM clock, not the wall clock: a frozen host must not burn
    # real minutes here.
    poll(lambda: True if req(port, "GET", "/state")["time"] >= t0 + seconds else None,
         f"{seconds}s of sim time", timeout=4 * seconds + 30.0, every=0.3)
    saved = req(port, "POST", "/tape/save", {"path": TAPE})
    print(f"    tape: {saved.get('written', saved)}")
    req(port, "POST", "/tape/clear", {})                 # not needed any more
    close_clients()
    CLIENTS.clear()          # this window is done; don't audit it as "dead"


def review_pass(seconds, extra=()):
    port = free_port()
    print(cyan(f"==> B/review: client on :{port} replaying {TAPE} at 1x "
               f"{' '.join(extra) if extra else '(interpolation ON)'}"))
    for p in (REVIEW_LOG, REVIEW_OUT):
        if os.path.exists(p):
            os.remove(p)
    start_client(["--source", "embedded", "--map", "basic", "--port", str(port),
                  "--review", TAPE, "--review-at", "120", "--test-play",
                  "--center", f"{CENTER[0]:g}", f"{CENTER[1]:g}",
                  "--zoom", f"{ZOOM:g}", "--motionlog", REVIEW_LOG,
                  *extra], REVIEW_OUT)
    poll(lambda: req(port, "GET", "/health"), "the review client's API",
         timeout=90.0)
    # The trace window is wall-bounded ON PURPOSE: it is the thing being
    # measured, not a condition to wait for. --test-play presses ▶ 2 s after
    # the tape loaded, and the run is VERIFIED from the log afterwards — not
    # polled for, because Godot block-buffers stdout when it is redirected,
    # so a marker line appears only when the buffer or the process ends
    # (that timeout was measured on a perfectly healthy client).
    time.sleep(2.5 + seconds)
    close_clients()
    CLIENTS.clear()
    with open(REVIEW_OUT, errors="replace") as f:
        out = f.read()
    play = [l for l in out.splitlines() if "TEST-PLAY" in l]
    if not play:
        print(red("!! the review window never started ▶ play — no trace"))
        print(out[-800:])
        raise SystemExit(1)
    print("    " + play[0].strip())


# ---------------------------------------------------------------- stats -----

def frames(path):
    """The per-frame pose lines: `ms uid x y camx camy simNow clamped rt zoom`.
    Returns the uid with the most frames and a list of (ms, screen_x, screen_y).
    """
    by = {}
    with open(path, errors="replace") as f:
        for ln in f:
            c = ln.split()
            if len(c) != 10:
                continue
            try:
                ms, uid = int(c[0]), int(c[1])
                x, y, cx, cy, zoom = (float(c[i]) for i in (2, 3, 4, 5, 9))
            except ValueError:
                continue
            by.setdefault(uid, []).append(
                (ms, (x - cx) * zoom, (y - cy) * zoom))
    if not by:
        raise SystemExit(red(f"!! no pose frames in {path}"))
    uid = max(by, key=lambda k: len(by[k]))
    return uid, by[uid]


TAPE_HZ = 60.0    # the tape's substep rate (dt_fixed = 1/60 s)
WARMUP_S = 1.0    # the first second is camera smoothing + the spawn transient


def report(name, fr):
    """Per-rendered-frame pose step. The honest number is the RATE one (px per
    wall second): a smooth window paints a near-constant world speed every
    frame, a quantised one paints zero on most frames and the whole substep on
    the rest — so its speed swings between 0 and ~N x mean. Mean/CV alone are
    useless here: one camera-settling frame at the start would drown the signal,
    hence the warmup trim and the median-based spread."""
    t0 = fr[0][0]
    # Start measuring where the picture STARTS MOVING: a review window is
    # legitimately paused until ▶ engages, and counting that stretch as stutter
    # would grade the wrong thing (Godot timers count from _Ready, so ▶ lands
    # ~2 s after a ~1.6 s startup).
    move = next((i for i in range(1, len(fr))
                 if math.hypot(fr[i][1] - fr[i - 1][1], fr[i][2] - fr[i - 1][2]) > 1e-4), 1)
    t0 = fr[move][0]
    d, v = [], []
    for i in range(1, len(fr)):
        if (fr[i][0] - t0) / 1000.0 < WARMUP_S:
            continue
        dt = (fr[i][0] - fr[i - 1][0]) / 1000.0
        if dt <= 0:
            continue
        px = math.hypot(fr[i][1] - fr[i - 1][1], fr[i][2] - fr[i - 1][2])
        d.append(px)
        v.append(px / dt)
    if len(d) < 50:
        print(red(f"  {name}: only {len(d)} frame steps — not measurable"))
        return None
    span = (fr[-1][0] - fr[0][0]) / 1000.0
    fps = len(fr) / span
    med = statistics.median(d)
    medv = statistics.median(v)
    # A frame is "held" when it paints what the frame before painted. The floor
    # is absolute (a hundredth of a pixel is no movement a eye could see),
    # because the quantised pass has median EXACTLY zero — a purely relative
    # threshold would call 0% of its frames held, which is the one thing that
    # pass is guilty of (measured with --ab).
    thr = max(0.2 * med, 0.02)
    held = 100.0 * sum(1 for x in d if x < thr) / len(d)
    jump = 100.0 * sum(1 for x in d if x > max(1.8 * med, 3 * thr)) / len(d)
    vs = (statistics.median([abs(x - medv) for x in v]) / medv) if medv else -1
    # If the pose only changes once per substep, exactly one frame in N gets a
    # step (N = fps / TAPE_HZ) and the rest are held:
    quant_held = 100.0 * (1.0 - TAPE_HZ / fps)
    print(f"  {name:<7} frames {len(d):>5}  fps {fps:5.1f}  "
          f"median Δpx {med:5.2f}  held {held:5.1f}%  jump {jump:5.1f}%  "
          f"px/s median {medv:6.1f} rel-mad {vs:5.2f}")
    print(f"          {'':<0}at {fps:.0f} fps a POSE THAT ONLY CHANGES PER SUBSTEP would hold "
          f"{quant_held:.0f}% of frames (N={fps/TAPE_HZ:.1f} frames per substep)")
    return dict(n=len(d), fps=fps, med=med, held=held, jump=jump,
                vmed=medv, vs=vs, quant_held=quant_held)


def report_ab(name, fr, extra_args):
    return report(name, fr)


def main():
    global LIVE_LOG
    ap = argparse.ArgumentParser()
    ap.add_argument("--seconds", type=float, default=10.0,
                    help="sim seconds to record in the live pass (default 10)")
    ap.add_argument("--review-seconds", type=float, default=6.0,
                    help="wall seconds to trace the review window (default 6)")
    ap.add_argument("--ab", action="store_true",
                    help="also trace the same tape with the play interpolation "
                         "switched off, i.e. the 2026-10-25 defect for comparison")
    ap.add_argument("--review-only", action="store_true",
                    help="skip the live pass, replay the tape already saved")
    a = ap.parse_args()

    runid = f"{int(time.time())}"
    stamp_paths(runid)
    if not a.review_only:
        close_clients()
        live_pass(a.seconds)
        with open(RUN_FILE, "w") as f:      # --review-only reuses this live pass
            f.write(LIVE_LOG)
    else:
        if not os.path.exists(RUN_FILE):
            raise SystemExit(red(f"!! no live trace recorded to compare against "
                                 f"({RUN_FILE}) — run without --review-only"))
        LIVE_LOG = open(RUN_FILE).read().strip()
    if not os.path.exists(TAPE):
        raise SystemExit(red(f"!! no tape at {TAPE} — run without --review-only"))

    close_clients()
    runs = [("review  (interp ON)", ())]
    if a.ab:
        # The switch that re-creates the 2026-10-25 defect on the SAME tape in
        # the SAME harness: without it, "before" and "after" would come from
        # two different tapes, two warmups and two sets of logs.
        runs.insert(0, ("review (no lerp = before)", ("--no-play-lerp",)))
    results = []
    for label, extra in runs:
        close_clients()
        review_pass(a.review_seconds, extra)
        results.append((label, frames(REVIEW_LOG)))

    print(cyan("==> per-frame pose trace (screen px, camera pinned)"))
    ul, live = frames(LIVE_LOG)
    print(f"  live car uid {ul}")
    rl = report("live", live)
    rr = None
    for label, (uid, fr) in results:
        r = report(label, fr)
        if "no lerp" not in label:
            rr = r
    print()
    print("  reading: 'held' = frames painted identically to the one before, "
          "'jump' = frames that moved >1.8x the mean step.")
    print("  a smooth window moves a little EVERY frame; a quantised one holds "
          "and then leaps a whole substep.")
    if rl and rr:
        # The live window IS the reference: same display, same content, same
        # render path. Quantised = a real population of frames that repaint
        # what the frame before painted, plus a swing in painted speed, both
        # far beyond the live pass. (The ideal 'held = 1 - 60/fps' line above
        # is the signature of a static camera; the review camera settles a
        # little, so it is printed as context, not used as a gate.)
        if rr["held"] > 3 * max(rl["held"], 0.2) and rr["vs"] > 2 * rl["vs"]:
            print(red("  VERDICT: the review window QUANTISES to the substep where "
                      "the live window interpolates smoothly — stutter reproduced."))
        elif rr["held"] < 2 * max(rl["held"], 0.2) + 3 and rr["vs"] < 1.5 * rl["vs"]:
            print(cyan("  VERDICT: both windows paint one even step per frame — "
                       "review is as smooth as live."))
        else:
            print(yellow("  VERDICT: inconclusive — read the numbers above."))


if __name__ == "__main__":
    main()
