# Project Rules

## Language

Regardless of whether the user writes in German or English, **respond in
English** and **write all documentation in English** (docs/, comments,
diary). This is the default: it only changes if the user gives an explicit
instruction to use a different language for a specific artifact — a
one-off request ("answer this in German") changes THAT answer only, not the
rule. A permanent switch needs an explicit standing instruction ("from now
on always X"), and if the two conflict, the newest explicit wording wins.

## Follow explicit user instructions exactly

When the user gives a direct instruction ("do X", "don't do Y"), do ONLY that —
in one step. No side investigation, no extra analysis, no "actually let me check
first" preamble. Explicit instructions override whatever work is in progress;
stop and do exactly what was asked. If more work seems warranted, ask after
delivering what was requested.

## Debugging workflow: reproduce first, fix second

When the user reports a problem (visual glitch, wrong behavior, stutter,
broken maneuver): **do not start designing or writing code changes.**
First step is always:

1. **Reproduce** it deterministically (headless sim, `--only`/`--tests`
   single-scenario run, or a scripted REST drive on the live game).
2. **Log** what actually happens (state trace: speed, position, heading,
   blinkers, segments over time) at high enough resolution to see the
   failure.
3. **Understand** the root cause from the data - only then discuss or
   implement a fix.

No speculative fixes based on guessing what the user saw.

## Waiting for results: poll, don't sleep

In test scripts AND in my own shell commands: **never wait with a long
fixed sleep** ("sleep 25 until the server is probably up"). Instead,
poll for the actual condition and stop when it is met:

- Server/API up? -> `until curl -s /state; do sleep 1; done` (bounded).
- Cars spawned after /teleport? -> poll /state until all expected uids
  exist (commands are queued and processed on the sim's tick loop).
- Run finished / duration reached? -> pace on the SIM clock from /state
  (`st["time"]`), not wall time; a frozen sim must not burn real time.
- Short sleeps (<= 1 s) as pacing BETWEEN polls are fine - what is banned
  is the blind fixed wait whose length guesses at when something "should"
  be done.

The two manual QA scripts in `tests/` (`light_check.py`, `car_line_check.py`)
already follow this pattern;
keep new ones consistent with them.

## General rule: never do physically impossible things

The simulation must never produce motion that no real car could perform:
no teleportation, no instant heading changes, no turning radius below a
real car's mechanical minimum, no position/heading updates that fall out
of sync. This applies to game logic, physics, fallbacks, and edge cases
(junctions, dead ends, segment hand-offs) alike.

When a desired behavior would require physically impossible motion, fix
the underlying logic (e.g. brake earlier, blend offsets, smooth the
heading) instead of weakening or bypassing the checks that catch it.

This rule may ONLY be violated if the user explicitly asks for it.


## Units: names declare their unit

The sim and the API speak different length units (see `docs/UNITS.md`).
Any identifier that holds a length must say which one it holds: `…M`
for metres, `…Px` for a value that has crossed into a **pixel/screen** frame
(the client's draw frame - the sim's own frame IS metres, see
`docs/UNITS.md`), no suffix for plain sim-frame values, and the scale
factor is always `pxPerM`/`PxPerM` - never `pppm`, `PPPM` or `scale`.
Every coordinate **object** answered by the API declares `"units":
"meters"` (the one exception predates the rule: `GET /obstacles` answers a
bare list, metres by convention - `docs/REST_API.md` says so; adding the key
there would retire the exception). Before touching
units, grep `pxPerM` - that list IS the set of frame crossings.

## No pygame compatibility target — Godot idioms win

The pygame frontend is retired and the transition to Godot is complete. **
Nothing has to stay compatible with, equal to, or parity-checked against
pygame.** Historical mentions (the `diary/`, the phase log in
`docs/GODOT_FRONTEND.md`, old `// … (pygame parity) …` comments) may stay
as history, but they are **not requirements** and must not steer new work.

For future development, follow Godot's own design: use the features and
idioms Godot suggests — its scene tree, nodes, layers and draw primitives,
its geometry and asset pipeline — instead of re-implementing a workaround
that only exists because pygame lacked the primitive. Ask "what does Godot
do for this?" before copying an old renderer's habit. Example: a road
ribbon is not a solid polygon whose interior is re-painted in the
background colour because the old renderer could not fill holes — that
workaround and its layering side effects are dropped, and roads are drawn
as their own geometry.

## Decisions: explicit user decisions are binding

When the user makes an explicit decision or instruction (e.g. "this value
must be variable, not fixed"), it is **binding**. You may propose
alternatives and point out consequences, but you must NOT silently override
the decision with your own judgment ("actually, simpler: keep it fixed").
If you believe the decision has a problem, state the conflict explicitly
and ask before deviating. Acknowledging a correction and then implementing
the old way anyway is a violation of this rule.

## Screenshots: how to look at the map

Visual verification of a rendering change (a crossing, a marking, a car) goes
through the Godot client itself: `MapRenderer` takes `--screenshot <path>`,
builds the map, shoots the viewport a moment later and **quits itself** - a
one-shot run, nothing to kill from outside (see the never-signal rule below).

```bash
rm -f /tmp/shot.png /tmp/godot_shot.log
nohup "/Applications/Godot_mono.app/Contents/MacOS/Godot" --path . -- \
      --source embedded --map basic --port 5007 \
      --center 1250 1750 --zoom 18 --screenshot /tmp/shot.png --delay 2 \
      > /tmp/godot_shot.log 2>&1 &
until [ -s /tmp/shot.png ] || grep -q "SavePng failed\|ERROR" /tmp/godot_shot.log; do sleep 1; done
```

Then `read /tmp/shot.png` (the image comes back to me) and check the log's
`Map ready: bounds …, zoom …` line for the view that was actually taken.

Three traps, all of them silent failures:

- **Never `--headless` for a screenshot.** Headless uses Godot's dummy
  renderer (`servers/rendering/dummy/…`), `GetViewport().GetTexture()` is
  null, `SaveShot` throws before it reaches `GetTree().Quit()` - no PNG and
  a process that hangs until killed. The windowed one-shot run opens a
  window and closes itself.
- **Use a free `--port`.** The embedded API will not bind a busy port
  ("embedded REST API FAILED … Address already in use"), so with a game
  already on :5001 the map never arrives and the run hangs. `--port 5007`
  leaves the running game untouched.
- **Aim the camera at the thing under test.** `--center <x> <y>` is in
  metres (x east, y north) and pins the view - it also turns off
  auto-follow, so the shot stays on the crossing instead of snapping to a
  spawned car. `--zoom <px/m>` sets the scale (`--delay <s>` is the wait
  between map-ready and the shot).

When a painted length matters (marking widths, clearances), measure the PNG
rather than eyeball it: a small PIL scan across a row/column of the image
converts pixels back to metres by dividing by the announced zoom - that is
the pattern `docs/lights_report.md` uses.

## E2E tests

The test suite in `tests/test_turning.py` is the project's **end-to-end
(e2e) test**: it drives the live game (real physics, real rendering, real
road network) over the REST API on :5001. See `docs/TESTING.md` for the
full workflow. The canonical runner here is `scripts/run_e2e.sh`, which
handles the whole cycle: kills stale processes (C# host AND old Python
`src.main` - a stale Python server on :5001 silently answers curls meant
for .NET), builds + starts the C# console host (`DrivingGame.Server`) with
the test map, launches the visible Godot window (unless `--no-godot`),
launches the suite through `POST /run_test`, and streams the log to the
console:

```bash
scripts/run_e2e.sh --warp 4                         # the default: visible, ~4x
scripts/run_e2e.sh                                  # real time (only when asked)
scripts/run_e2e.sh --no-godot --warp 4              # headless
scripts/run_e2e.sh --tests corner_left_entry --warp 4   # single scenario
```

**Default pacing: `--warp 4`.** Any e2e run I start (full suite, subset, or a
single scenario) gets `--warp 4` unless the user asks for real time or a
different factor. Reason: the suite's guardrails are in SIM time and its poll
resolution scales with the announced warp, so a 4x run is ~3x faster on the
wall clock with the same verdicts (measured: 4m41s → 1m34s for the full
suite). Two things stay honest at 4x: the capacity probe (`fig8_stress`) is
skipped by design, and a warped run is not a bit-identical replay of the
real-time one (control commands land at different points in the sim).

**"I want to watch it" means `--realtime`.** When the user says they want to
watch a run (any wording to that effect), run it at real time — the 4x default
exists to save wall-clock time in CI-like loops, not to make a spectacle faster
than the eye can follow. Same for a maximized window: `--maximized` fills the
screen, `--zoom <px/m>` and `--center <x> <y>` (metres) aim it.

## Two clients — never conflate them (binding user decision, 2026-10-21)

**Simulationsviewer** = the Godot window we have (`MapRenderer`), always the
live sim. **Simulationsreviewer** = a separate client, its own window, showing a
tape frame at a cursor. Independent and long-lived: a new run is a new tape,
a dead host is a connection state (overlay + backoff re-acquire), and only a
renderer change forces a rebuild. The reviewer caches its loaded tape client-side
so a run under review survives the host dying.

Full specification: `docs/SIMULATIONS_REVIEWER.md` (status: **built and in
use** - `MapRenderer.review.cs`, opened with the client's `--review [path]`,
see the "Launching the two windows" section there). The tape and its endpoints
it reads are built: `docs/RECORD_PLAYBACK.md`.

## Two run modes — never mix them (binding user decision, 2026-10-21)

**"review" / "watch" mode** — the user wants to SEE the manoeuvre:
1. start the client **fullscreen** (`--maximized`),
2. load the map, **focus the camera on the named hotspot** (`--center`/`--zoom`
   on the obstruction or junction, or `--focus-car <uid>` on a named car),
3. **freeze the sim clock** (`POST /freeze {"frozen": true}`),
4. spawn every car while frozen — at their real starting speeds
   (`/teleport` with `speed`, `cruise_kmh` for a slow vehicle),
5. only once ALL cars exist, unfreeze and run at the requested pace.

Why the freeze matters: without it the sim is already ticking while the
scenario is still staging, so the first seconds of the action happen before
the eye finds the cars — "I'll lose half the action".

Steps 1-4 above are exactly what `scripts/run_e2e.sh --watch` does
(`--maximized` + `DG_FREEZE_STAGE=1`: stage every car while frozen, poll until
all uids exist, then unfreeze at the announced pace) — prefer the flag over
hand-staging, so the procedure and the script cannot drift apart. The manual
sequence stays right for a live session that the runner does not drive.

**"run the e2e tests"** — the opposite: a **smaller window**, `--warp 4`, and
get through the named scenarios as fast as possible. Nobody is watching;
verdicts matter.

**If it is unclear which mode a request means, ASK before running.** Do not
guess: one loses the spectacle, the other wastes four minutes.

**A scenario aims its own camera** (user decision 2026-10-24): each test declares
`car` / `named` / `loc` in `SCENARIO_FOCUS`, and both clients honour it - the
viewer per test off `POST /focus`, the reviewer from the tape. So the `--center` /
`--zoom` / `--focus-car` of the recipe above are what you type when you want to
overrule the scenario, not what you owe it. The rule inside the declaration: **if
a test's focus is not a car, it gets a location and follows no car at all** - and
the hand outranks both (a drag, a slot toggle, `--center` latches).
Details: `docs/TESTING.md` ("Camera focus"), `docs/SIMULATIONS_REVIEWER.md`.

When the user asks to run the e2e tests:

0. **Restart the game first.** The runner kills any running game process
   and starts a fresh one - no stale state from previous runs or debugging.
1. **Map visible.** Run WITHOUT `--no-godot` - the Godot window (with the
   map) must be open so the user can watch the car drive. (`--no-godot` is
   only for CI or when the user explicitly asks for it.)
2. **Results visible while running.** The runner streams the suite output
   to the console as it happens - no backgrounding, no discarding output.
   The per-run log file (`tests/run_test_T_<nnn>_<N>_<timestamp>.log`) can be
   re-read or grepped afterwards (long runs can exceed the tool output
   limit).

(Godot self-terminates via `--quit-on-test-done`; if it is still open
afterwards, say so - **never send Godot a signal**: a SIGTERM kills .NET's
exit path into an abort and pops a macOS crash-report window. The runner
closes windows cleanly instead: the client watches
`/tmp/driving_game_godot_gen` and quits itself when `scripts/run_e2e.sh`
bumps it.)
