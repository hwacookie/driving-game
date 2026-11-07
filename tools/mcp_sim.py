#!/usr/bin/env python3
"""Standalone MCP server: agent access to the live driving-game sim and its
recorded past (#94).

Proxies the game host's REST API over stdio - no engine changes, no shared
memory, no new protocol. The data path is exactly the one the test suite and
the clients already use:

    pi  --stdio-->  this file  --HTTP-->  DrivingGame.Server :5001  -->  sim

If nothing listens on the port, every tool answers "no game on <base>" instead
of throwing. The base URL defaults to http://127.0.0.1:5001 and is overridable
via DRIVING_GAME_API (e.g. a screenshot host on :5007).

Requires the project venv with `mcp<2` installed (the stable v1 FastMCP API).

Register (user level, works from any cwd):
    pi mcp add sim -- /Users/hauke/prj/driving-game/.venv/bin/python \
        /Users/hauke/prj/driving-game/tools/mcp_sim.py
"""
import functools
import json
import os
import urllib.error
import urllib.request

from mcp.server.fastmcp import FastMCP

BASE = os.environ.get("DRIVING_GAME_API", "http://127.0.0.1:5001").rstrip("/")
TIP_LIMIT_MAX = 600          # the host's hard cap on /tape/tip (10 sim-s at 60 Hz)
TRACE_ROWS_MAX = 150         # keep one tool answer readable

mcp = FastMCP("driving-game-sim")


class NoGame(Exception):
    """Nothing is listening on the port - a state, not an error to raise."""


def _http(method: str, path: str, body=None):
    req = urllib.request.Request(
        BASE + path,
        data=json.dumps(body).encode() if body is not None else None,
        headers={"Content-Type": "application/json"},
        method=method)
    try:
        with urllib.request.urlopen(req, timeout=5) as r:
            return json.loads(r.read().decode())
    except (urllib.error.URLError, ConnectionError, TimeoutError) as e:
        raise NoGame(f"no game on {BASE} ({e.__class__.__name__})") from e


def _guard(fn):
    """Every tool answers the no-game state as a STRING: the agent reads it and
    moves on, instead of a tool error that looks like our bug."""
    @functools.wraps(fn)   # name + docstring + signature: FastMCP builds the
    def wrapper(*a, **kw):  # tool's identity and schema from all three
        try:
            return fn(*a, **kw)
        except NoGame as e:
            return str(e)
    return wrapper


# --- tape line grammar (docs/RECORD_PLAYBACK.md "one frame, one grammar") ----
# step;fluidity;moving_share;<cars>|<cars>...[;~uid=decision~...]
# car = 14 fields: uid,x,y,heading_deg,speed_mps,blinkers,brake,wrong,
#       crashes,model,lamps,heldBy,level,capKey
# (capKey is empty when no rule holds the car - the line then ENDS with a
# comma and split() returns "" for it, so a strict 15-field check would drop
# every free-rolling car: measured against a live tip line)


def _parse_line(line: str):
    """One tape line -> (step, {uid: fields}) or None for a non-frame line."""
    if not line or line.startswith("#"):
        return None
    parts = line.split(";")
    try:
        step = int(parts[0])
    except ValueError:
        return None
    cars = {}
    if len(parts) > 3 and parts[3]:
        for car in parts[3].split("|"):
            f = car.split(",")
            if len(f) < 14 or not f[0].isdigit():
                continue
            cars[int(f[0])] = {
                "x": float(f[1]), "y": float(f[2]),
                "hdg": float(f[3]), "v": float(f[4]),
                "brake": f[6] == "1", "crashes": int(f[7]),
                "cap": f[13],
            }
    return step, cars


def _fetch_lines(since: int, limit: int):
    """Tape lines with step in (since, since+limit] - the host's own window."""
    d = _http("POST", "/tape/tip", {"since": since, "limit": limit})
    return d.get("lines", []), d.get("head_step", -1), d.get("first_step", -1)


# --- tools -------------------------------------------------------------------

@mcp.tool()
@_guard
def sim_state() -> str:
    """Compact live snapshot of the running game: sim time, frozen flag and a
    per-car table (uid, position in metres, heading deg, speed km/h, state,
    crashes). This is /state reduced to what an eye or a model actually reads."""
    st = _http("GET", "/state")
    cars = st.get("cars", [])
    lines = [f"t={st.get('time', 0):.1f}s frozen={st.get('frozen')} "
             f"cars={len(cars)} warp={st.get('time_warp', 1)}"]
    for c in cars:
        lines.append(
            f"  uid={c['car_uid']} ({c['x']:.1f},{c['y']:.1f}) "
            f"hdg={c.get('heading', 0):.0f} v={c.get('speed_kmh', 0):.1f}km/h "
            f"{c.get('state', '')} crashes={c.get('crash_episodes', 0)}")
    return "\n".join(lines)


@mcp.tool()
@_guard
def car_trace(uid: int, from_step: int = -1, to_step: int = -1,
              max_rows: int = TRACE_ROWS_MAX) -> str:
    """One car's recorded poses from the tape (the host's in-memory history;
    it survives until the host dies, and on disk via the autosave since #92).

    from_step/to_step are absolute substeps (-1 = newest); the range is capped
    at 600 frames per hop and subsampled to max_rows. Each row: step, position
    (m), heading (deg), speed (km/h), cumulative distance driven (m) and the
    active speed-cap key - enough to answer 'how far did it drive' and 'where
    was it at step N' without parsing a file."""
    max_rows = min(max(1, max_rows), TRACE_ROWS_MAX)
    lines, head, first = _fetch_lines(-1, 1)
    if head < 0:
        return "no tape recorded (host has no frames yet)"
    to = to_step if to_step >= 0 else head
    frm = from_step if from_step >= 0 else max(first, to - 600)
    frm, to = max(frm, first), min(to, head)
    if frm > to:
        return f"empty range (tape covers {first}..{head})"

    rows = []
    dist = None
    span = to - frm + 1
    stride = max(1, span // max_rows)
    for hop_start in range(frm, to + 1, TIP_LIMIT_MAX):
        hop_end = min(hop_start + TIP_LIMIT_MAX - 1, to)
        hl, _, _ = _fetch_lines(hop_start - 1, hop_end - hop_start + 1)
        for line in hl:
            parsed = _parse_line(line)
            if parsed is None:
                continue
            step, cars = parsed
            if step % stride:
                continue
            if uid not in cars:
                continue
            c = cars[uid]
            if dist is not None and rows:
                px, py = rows[-1][1], rows[-1][2]
                dist += ((c["x"] - px) ** 2 + (c["y"] - py) ** 2) ** 0.5
            else:
                dist = 0.0
            rows.append((step, c["x"], c["y"], c["hdg"], c["v"] * 3.6,
                         dist, c["cap"]))
    if not rows:
        return f"uid={uid} does not appear in steps {frm}..{to}"
    out = [f"uid={uid} steps {rows[0][0]}..{rows[-1][0]} "
           f"(stride {stride}, tape {first}..{head})"]
    for r in rows:
        cap = f" cap={r[6]}" if r[6] else ""
        out.append(f"  {r[0]:>6} ({r[1]:8.2f},{r[2]:8.2f}) "
                   f"hdg={r[3]:5.1f} v={r[4]:5.1f}km/h d={r[5]:7.2f}m{cap}")
    return "\n".join(out)


@mcp.tool()
def tape_file(path: str, uid: int = -1, from_step: int = -1,
              to_step: int = -1, max_rows: int = TRACE_ROWS_MAX) -> str:
    """Cold query on a RECORDED tape file - no host required (#96). Since #92
    the file is the durable record: this answers 'what was going on at frame N
    of run T_196' long after the host that recorded it is gone.

    uid >= 0: that car's pose rows (step, x, y, heading deg, speed km/h,
    cumulative distance m, active cap key) over [from_step, to_step] (absolute
    substeps, -1 = last 600), subsampled to max_rows; the car's own decision
    events ride along as '!' rows. uid == -1: a snapshot of ALL cars at one
    step (to_step, default newest) plus that step's events - the cast picture
    of a frame. Header facts (map, dt, cast names/kinds) and any focus/result
    notes are always shown first.

    Works on any file in tests/ or docs/tapes/, including aborted runs."""
    import re as _re
    try:
        with open(path, "r", encoding="utf-8") as fh:
            raw = fh.read()
    except OSError as e:
        return f"cannot read {path}: {e}"

    meta, aux, frames = {}, [], []
    for line in raw.splitlines():
        if not line:
            continue
        if line.startswith("# DGTAP1"):
            for kv in line.split()[1:]:
                if "=" in kv:
                    k, v = kv.split("=", 1)
                    meta[k] = v
        elif line.startswith("#"):
            if line.startswith("# step;"):   # the legend - grammar, not content
                continue
            aux.append(line[2:].strip())   # focus / result / caropt notes
            continue
        parsed = _parse_line(line)
        if parsed is not None:
            frames.append(parsed)
    if not frames:
        return f"no frame lines in {path} (is it a tape?)"

    first, head = frames[0][0], frames[-1][0]
    cast = meta.get("names", "")
    out = [f"tape: {path.split('/')[-1]} map={meta.get('map', '?')} "
           f"dt={meta.get('dt_fixed', '?')} steps {first}..{head}"
           + (f" cast={cast}" if cast else "")]
    for a in aux[:3]:   # a full-suite tape carries dozens of notes; the pose
        out.append(f"  note: {a[:120]}")   # rows are the answer, not the header

    # events per step, from the '~uid=msg~' tail of frame lines
    events = {}
    for line in raw.splitlines():
        if line.startswith("#") or ";~" not in line:
            continue
        try:
            st = int(line.split(";", 1)[0])
        except ValueError:
            continue
        for ev in line.rsplit(";", 1)[1].lstrip(";").split("~"):
            if "=" in ev:
                euid, _, msg = ev.partition("=")
                if euid.isdigit():
                    events.setdefault(st, []).append((int(euid), msg))

    if uid < 0:
        step = to_step if to_step >= 0 else head
        row = next(((s, c) for s, c in frames if s == step), None)
        if row is None:
            return f"step {step} not in tape (covers {first}..{head})"
        _, cars = row
        out.append(f"--- all cars at step {step}:")
        names = dict(p.split(":", 1) for p in cast.split(",") if ":" in p)
        for u in sorted(cars):
            c = cars[u]
            name = names.get(str(u), "")
            out.append(f"  uid={u} ({c['x']:.2f},{c['y']:.2f}) hdg={c['hdg']:.1f}"
                       f" v={c['v']*3.6:.1f}km/h crashes={c['crashes']}"
                       + (f" cap={c['cap']}" if c["cap"] else "")
                       + (f" [{name}]" if name else ""))
        for euid, msg in events.get(step, []):
            out.append(f"  ! uid={euid}: {msg}")
        return "\n".join(out)

    to = to_step if to_step >= 0 else head
    frm = from_step if from_step >= 0 else max(first, to - 600)
    frm, to = max(frm, first), min(to, head)
    if frm > to:
        return f"empty range (tape covers {first}..{head})"
    span = to - frm + 1
    stride = max(1, span // max(1, max_rows))
    rows, dist, prev = [], None, None
    for s, cars in frames:
        if not (frm <= s <= to) or s % stride or uid not in cars:
            continue
        c = cars[uid]
        if prev is not None and dist is not None:
            dist += ((c["x"] - prev[0]) ** 2 + (c["y"] - prev[1]) ** 2) ** 0.5
        else:
            dist = 0.0
        prev = (c["x"], c["y"])
        rows.append(f"  {s:>6} ({c['x']:8.2f},{c['y']:8.2f}) hdg={c['hdg']:5.1f} "
                    f"v={c['v']*3.6:5.1f}km/h d={dist:7.2f}m"
                    + (f" cap={c['cap']}" if c["cap"] else ""))
        for euid, msg in events.get(s, []):
            if euid == uid:
                rows.append(f"  ! {s}: {msg}")
    if not rows:
        return f"uid={uid} does not appear in steps {frm}..{to}"
    first_step_shown = rows[0].split()[0]
    header = (f"--- uid={uid} steps {first_step_shown}..{to} "
              f"(stride {stride}, tape {first}..{head}):")
    return "\n".join(out + [header] + rows)


@mcp.tool()
@_guard
def car_decisions(uid: int) -> str:
    """A car's decision log (the [PARK]/[PARKEND]/avoidance lines the driver
    wrote): sim time, substep and message. This is what explains WHY a car did
    what it did - pair with car_trace for the geometry."""
    d = _http("GET", f"/car/{uid}/decisions")
    if "error" in d:
        return str(d)
    ds = d.get("decisions", [])
    if not ds:
        return f"uid={uid}: no decisions recorded"
    out = [f"uid={uid}: {len(ds)} decisions"]
    for x in ds:
        out.append(f"  t={x['t']:.1f}s step={x.get('step', '?')}: {x['msg']}")
    return "\n".join(out)


@mcp.tool()
@_guard
def run_status() -> str:
    """Whether a test suite is running on the host: scenario number, log file,
    and the exit code once done. The log file itself can be read like any file
    for the per-scenario verdicts."""
    d = _http("GET", "/run_test")
    if d.get("running"):
        return (f"RUNNING test #{d.get('number')} "
                f"(log: {d.get('log_file', '?')})")
    rc, num, log = d.get("returncode"), d.get("number"), d.get("log_file")
    if rc is None and num is None:
        return "idle - no run started on this host yet"
    return (f"idle - last run: test #{num} rc={rc} "
            f"(log: {log or '?'})")


@mcp.tool()
@_guard
def tape_status() -> str:
    """The recorder's state: whether it is recording, the frame range in memory
    (first..head substep), the map stamp and - since #92 - the durable autosave
    path if one is registered."""
    d = _http("POST", "/tape/tip", {"since": -1, "limit": 1})
    head, first = d.get("head_step", -1), d.get("first_step", -1)
    n = (head - first + 1) if head >= 0 else 0
    return (f"recording={d.get('recording')} frames={n} "
            f"(steps {first}..{head}) map={d.get('map')}")


if __name__ == "__main__":
    mcp.run()
