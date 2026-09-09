## 2026-09-07 16:21–20:36 (hauke-walden)
- Worked on: Review of committed work from previous session — collision detection v1 (`CarCollisions.cs`), API split, SimHost, end-to-end no-signal flow, and spec updates all pushed to `csharp-port` branch (`8a57de5`)
- Done: Confirmed all prior work committed and pushed; ready for next phase
- State: Two paths available per spec: (1) v1 wiring with SimEngine two-pass + `--no-collisions` flag, or (2) driver profiles implementation. Awaiting direction on which to prioritize next.


## 2026-09-07 (continued, hauke-walden)
- Worked on: v1 wiring + v2 implementation + comprehensive collision test suite,
  per user priority (three crossroads scenarios on the "basic" map).
- Done:
  - `SimEngine.Tick` two-pass restructure (loop A: Update+obstacle+avoidance
    cap; `Resolve`; loop B: Validator+LaneGuard on final post-resolution
    state); `CollisionsEnabled` flag + `--no-collisions` CLI; Stopwatch cost
    measurement (printed every 5s).
  - v2: oblique/crossing pair prediction + right-before-left in
    `CarCollisions.ComputeAvoidCaps`; `public RefLine? Ref` accessor added to
    `BicycleNav`.
  - Found + fixed 3 real bugs surfaced ONLY by actually wiring v1 into a real
    multi-car `Tick` run for the first time (it had never been driven through
    the engine before): (1) corridor cap formula used `min(vAhead, brakeCap)`
    instead of `vAhead + brakeCap`, clamping a follower to a full stop the
    instant a stationary lead entered the corridor regardless of remaining
    gap; (2) decel-limited braking baselined off the post-driver-Update
    speed instead of pre-Update, netting less than full `CAR_BRAKING`
    deceleration (driver's own accelerate logic partially fights the brake
    every substep) and overshooting the 3m standstill gap; (3) BicycleNav's
    standstill-creep mechanic slips a car forward through `Update()` before
    the post-hoc avoidance cap ever runs, so a "stopped" follower crept
    through the lead over ~30s — fixed by suppressing `accelerate` in the
    control fed to `Update()` once the pre-step cap already forbids exceeding
    current speed.
  - v2's originally-confirmed design (3 discrete TTC stages 1/3/5s) missed a
    fast-closing (~20 m/s) crossing conflict entirely — the fixed-future-time
    sample overshoots the actual conflict point at cruise speed, and the
    window where a sample lands exactly on it is narrower than the gap
    between stages. Replaced with a continuous 0.1s sweep (still graded into
    the same 1s/3s/5s response bands), length-padded ±2m (NOT width-padded —
    that caused a false positive for the head-on-separate-lanes scenario,
    since padding both dimensions pushed the effective half-width past half
    the 3.5m lane separation). Precompute each car's sweep once per substep
    (not once per pair) to bound cost in dense traffic.
  - `DrivingGame.Sim.Tests/CollisionTests.cs`: 3 scenarios driving a real
    headless `SimEngine` (no existing test did this — collision logic lives
    only in `Tick`, `car.Update` alone bypasses it entirely). Stationary lead
    (v1 only), head-on separate lanes (false-positive guard, asserted against
    a solo-drive baseline rather than "never brakes" — a lone car legitimately
    decelerates for the real junction ahead), simultaneous crossing with RBL
    (v2 required — v1 alone reproduced the exact mutual-stop failure mode the
    user wanted avoided, confirmed via diagnostic trace before the sweep fix).
  - Verified: 94/94 tests pass (91 pre-existing + 3 new); 150-car stress
    smoke run clean (no exceptions/NaN); cost ~41-48 µs/car for
    `ComputeAvoidCaps` at 150 cars after precompute optimization (was
    ~75-95 µs/car before), `Resolve` ~0.6-0.7 µs/car — both diagnostic/stress
    test files removed after verification (scratch, not permanent suite).
  - `docs/C_SHARP_PORT_SPEC.md` updated with final v1/v2 status, the 3 bug
    fixes, and the sweep-vs-3-stages design deviation + why.
- State: Collision avoidance v1+v2 complete, wired, tested, documented. Next
  candidate: driver profiles (Cautious/Normal/Sporty) or the fig-8 multi-car
  e2e scenario, both still scoped but not started (see Phase 9 in spec).

## 2026-09-07 (continued 2, hauke-walden)
- Worked on: Fixing the reported blinker mis-position + adding brake lights,
  per user request while visually reviewing the collision demo in Godot.
- Done:
  - Copied all of `car/docs/*` into `driving-game/docs/` (user request) -
    `SPEC.md` now lives locally; its "Lights" section documents the original
    headlight/taillight/blinker design (2px/3px circles, bright/dark red
    brake distinction) that the C# Godot port had never fully implemented.
  - Added brake lights (`Car.IsBraking` -> bright red at rear corners) and,
    going further per the user's explicit ask for an isolated "one light at
    a time" visual test, independent `HeadlightsOn`/`TaillightsOn` flags
    (`Car.cs`, `SimEngine.ToggleCommand`/`ApplyCommand`, `/toggle` API,
    `/state` payload `headlights_on`/`taillights_on`) plus a `ClearBlinker`
    toggle exposing the driver's existing (but previously unwired)
    `ClearTurnSignal()`.
  - Root-caused the ACTUAL reported bug via a real screenshot (multi-display
    scan + PIL crop - `osascript`/System Events lacked accessibility
    permission, so window enumeration wasn't possible, but `screencapture
    -D <n>` across all 4 displays found the Godot window): `MakeLight`
    baked each corner's offset into the `Polygon2D`'s own vertex
    coordinates, then the per-frame zoom-compensation code set that node's
    `Scale` - which Godot applies relative to the node's OWN `Position`
    (unset, so origin) - meaning the SAME scale-down that keeps the dot's
    on-screen SIZE constant also shrank the baked-in corner OFFSET toward
    the car's centre. At typical zoom-compensation factors (~0.1), a light
    meant to sit ~3 m out rendered ~0.3 m out - every corner light
    (blinkers, and the new brake/headlights) clustered near the middle of
    the sprite instead of at its corners. This had been true since the
    blinker code was first written; wiring the new brake/headlights simply
    made it visible enough to investigate.
  - Fix: set the corner offset as the light node's `Position`; build its
    `Polygon` as a plain unit circle centred at the node's own origin. Scale
    now only resizes the dot; position is untouched. Confirmed by
    screenshot: brake (red) lights sit exactly at the two rear corners,
    headlights (white) at the two front corners, blinker (orange) visible
    at the correct side corner on a lucky frame within its 0.5 s blink
    cycle.
  - `tests/light_check.py`: spawns one stationary car, cycles
    blinker-left/right/hazard/brake/taillight/headlight 3 s each (hazard 5 s
    - `HAZARD_MIN_DISPLAY_S` is a real game constraint) driving the REST API
    directly; not part of the automated `test_turning.py` pass/fail suite -
    it exists purely for a human to eyeball the Godot window.
  - `docs/SPEC.md` updated with a dated C# port note under "Lights"
    documenting all of the above (new toggle fields, the bug, the fix, the
    QA script).
  - Verified: full `dotnet test` suite still 93/93 green (this was a
    Godot-renderer + small API-surface change, no Sim collision logic
    touched); visual confirmation via screenshot as described above.
- State: Blinker/brake/headlight/taillight rendering fixed and extended;
  demo processes (`light-check-server`, `light-check-godot`) still running
  for the user to inspect interactively if desired.
