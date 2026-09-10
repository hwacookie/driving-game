# C# Port of the Simulation — Spec & Plan

Date: 2026-09-04 · Status: approved (hauke), in progress
Source: `/Users/hauke/prj/car` (Python, ~10.7k lines) → this repo (C# / .NET 8)

## Goal

Port the headless simulation from Python to C# and integrate it into this repo.
Target end state (hauke decision 2026-09-04):

- **Renderer and simulation in one app (Godot):** MapRenderer reads sim state
  in-process; no HTTP for gameplay.
- **REST API (port 5001) stays embedded** so external test injection keeps
  working — all existing test scripts / stress suites run unchanged.

Built in two stages:

1. **Port:** `DrivingGame.Sim` library + standalone console host with the full
   REST API (mirrors the old Python server 1:1). All existing tests verify the
   port over HTTP; the Godot client is untouched.
2. **Integration:** Godot references the same Sim library, MapRenderer reads
   in-process snapshots (HTTP double-pipeline retired for gameplay), REST API
   embedded for external tests.

**Consolidation (2026-09-05, hauke):** this repo becomes the single home —
step by step. DONE: the e2e suite lives here as `tests/test_turning.py` (the
three config constants it imported from `src/config.py` are inlined;
`DrivingGame.Sim/Config.cs` stays the source of truth); `scripts/run_e2e.sh`
and the `/run_test` + `/tests` endpoints resolve the suite in-repo. AFTER
PHASE 7 (hauke decision): copy the behavioral docs from `car/docs/`
(SPEC.md, TESTING.md, DRIVING_MANEUVERS.md, MULTI_CAR_PLAN.md, OBSTACLES.md,
REST_API.md, GODOT_FRONTEND.md, SMOOTH_GEOMETRY_DESIGN.md,
TURN_REWORK_PLAN.md) into this repo's `docs/`. Until then the car repo stays
reference + fallback: its Python sim must remain runnable for the pending
stale-normal quirk review (deliberately done in Python first), then it is
history.

## Architecture (target)

```
driving-game/
├── project.godot, Main.tscn, MapRenderer.cs   Godot client (net8.0)
├── DrivingGame.Sim/          class library: the ported simulation
│                             (no ASP.NET dependency; standalone or embedded)
├── DrivingGame.Server/       console host: Sim + REST API on :5001
│                             (headless testing without Godot)
└── data/osm_cache/           OSM JSON cache (copied from car repo)
```

### Threading model (in Godot)

- The sim runs on a **dedicated background thread with its own 60 Hz pacing**
  (sleep + spin window, re-measured for .NET on this machine). "In one app"
  does NOT mean same thread: fixed-timestep physics must stay independent of
  render hitches (tile generation, window drags, GC) — the original reason the
  sim lived in its own process (`main.py` accumulator comment).
- Godot's main thread reads the latest immutable state snapshot (lock or
  volatile reference swap). The existing sample buffer + 100 ms interpolation
  delay stays; only the data source changes from HTTP to direct read.
- Commands (game UI + REST) go into the same thread-safe command queue.

### Concurrency-ready design (hauke requirement, 2026-09-04)

The engine must stay trivially parallelizable for later collision
avoidance / higher car counts — enforced from Phase 3 onward as a data-flow
rule, NOT by starting threads early:

> **A car update is a pure function of (own state, immutable world
> snapshot). No car ever reads another car's live state mid-substep.**

Substep structure:
1. **Pre-pass (sequential):** build spatial-hash grid from previous
   substep's positions, distribute control commands.
2. **Core (parallelizable middle):** `car.Update(dt, worldView)` for all
   cars; worldView = road network (read-only) + raceline cache (read-only)
   + all other cars' positions from substep N-1 (plain array).
3. **Post-pass (sequential):** aggregation only (validator counters,
   lane-guard stats, obstacle contact state).

Collision avoidance fits this naturally: "brake for the car ahead" reads
the leader's position from the N-1 snapshot (Jacobi-style update — the
standard multi-agent pattern; at 30 km/h the 50 cm staleness is
physically irrelevant). Turning on parallelism later is a one-line loop
change (`foreach` → `Parallel.ForEach`), no refactoring.

Why no threads from day one: (1) determinism is the verification regime —
reproducible stress runs are only trivially guaranteed single-threaded;
(2) the port itself is the project's main risk — concurrency on top of
unverified code multiplies the failure space; (3) at ≤100 cars, parallel
scheduling overhead is comparable to the work. Real threads arrive in
Phase 8 when a measurement demands it.

### REST API

Same endpoints & payloads as the Python server: `/state`, `/map`,
`/segment/<idx>`, `/control`, `/teleport`, `/cars`, `/flags`, `/label`,
`/start_points`, `/toggle`, `/freeze`, `/wait`, `/reset`, `/health`, `/tests`,
`/run_test`. Implemented once, wired by both hosts (console + Godot-embedded).

## Verification (decision 2026-09-04: invariants, NOT bit-exact)

Trajectories do **not** need to match the Python implementation cm-for-cm.
Required invariants — already encoded in the existing test suite:

- Cars never leave the road (off-road check)
- No wrong-way driving (lane guard; known fig8 noise level as baseline)
- No physically impossible position jumps (physics validator / jitter check)
- All features preserved: breadcrumbs/trails, reverse + forward parking styles,
  pull-over/pull-out, blinkers (mechanical auto-cancel), u-turns, freeze,
  teleport, multi-car colors, camera follow, levels (bridges/tunnels), flags,
  HUD labels.

Gates run via HTTP against the C# console host with unchanged scripts:

- **G1:** `/map` export equals Python output (float tolerance)
- **G2:** single-car scenario sane; parking + pull-over features work
- **G3:** 576-car stress suite passes (0 off-road, 0 jitter)
- **G4:** capacity sweep — expect a big jump over the ~100-car Python ceiling
- **G5:** Godot client visual check against the embedded sim (no stutter)

## Dependency audit (done 2026-09-04)

| Python dep | C# / .NET equivalent | Notes |
|---|---|---|
| flask | ASP.NET Core (Kestrel) | framework ref `Microsoft.AspNetCore.App`, no NuGet |
| psycopg | Npgsql (NuGet) | only for DB fetch; cache-first makes it optional |
| **shapely** | **NetTopologySuite (NuGet)** | JTS-based like shapely: Point/LineString/Polygon/Multi*, `.buffer()` (incl. negative = erosion), `unary_union`, `linemerge` (LineMerger), prepared geometry (`PreparedGeometry`). Used in road_network (paved polygons, centerlines), lane_guard (point→centerline distance), raceline (eroded safe polygon + fast contains) |
| numpy | pure C# | only 2 lines in obstacles.py (array for nearest-neighbor search) → plain loop, no package |
| math, json, hashlib, bisect, threading, dataclasses, abc, … | BCL equivalents | System.Math, System.Text.Json, SHA1, custom bisect, Thread/Tasks, records, abstract classes |

All numerics (banded solver, corridor optimization) are hand-written Python
and port directly. **No missing libraries.**

Audit note: first pass only checked top-level imports; the shapely/numpy
imports are LOCAL (inside functions) and were caught in the re-audit on
2026-09-04 while porting road_network.py.

## OSM data (keep both paths)

- **Primary:** JSON cache file (exists for Kleinmachnow: car repo
  `data/osm_cache/osm_7a1f296dee38b9db.json`, 380 KB). C# reads the same
  format; cache key = `SHA1("{north:.7f}_{south:.7f}_{west:.7f}_{east:.7f}")[:16]`
  (verified: matches the existing filename). Cache file is copied into this
  repo so the C# server is self-contained.
- **Secondary (optional, later):** Npgsql fetch from OSM-Wars Postgres
  (`localhost:5432/osm_wars`), same SQL as `osm_loader.py`, for refresh.

## Phases & To-do list

> Progress (2026-09-05): Phase 0 ✓, Phase 1 ✓, Phase 2 ✓, Phase 3 ✓ — Gate
> G1 passed for both the `basic` test map and the OSM map; Phase 2 gate passed
> with all 23 reference cases at ≤1e-9; Gate G2 passed via 5 behavioural
> integration tests (see below). **Phase 4 code complete** (Car/Driver,
> LaneGuard, PhysicsValidator, Obstacles — 90/90 unit tests green) and its
> feature gate now passes (blinker-parity bug fixed 2026-09-05 — see Phase 4
> below). **Phase 5**: sim engine + pacing done; REST API
> implemented in the console host (shared-code placement deferred to Phase 7).
> **Phase 6 complete**: G3 passed (576-car stress); G4 passed — capacity
> sweep: C# real-time ceiling ~170 cars vs Python ~90 (single-threaded, ~2×
> per-car speedup, see Phase 6 below); A/B sanity done; full e2e suite 22/22
> against a fresh C# host with the visible Godot window (`scripts/run_e2e.sh`).
>
> **Update (2026-09-07):** car-to-car collision + avoidance v1 AND v2 fully
> implemented, wired into `SimEngine.Tick`, and verified (`CarCollisions.cs`,
> `docs/C_SHARP_PORT_SPEC.md` "Collision detection & avoidance" — 3-scenario
> crossroads test suite in `CollisionTests.cs`, 94/94 tests green). Driver
> profiles + new test-map scenario still scoped, not started — all under
> Phase 9, started BEFORE G5 by user decision.

### Phase 0 — Scaffolding ✅ (2026-09-04)
- [x] Solution layout in this repo (Sim + Server projects, net8.0)
- [x] Godot csproj: exclude Sim/ and Server/ from its compile glob
      (SDK-style projects glob all .cs under the directory!)
- [x] Copy OSM cache file into `data/osm_cache/`
- [x] `.gitignore` for bin/obj

### Phase 1 — Data layer ✅ (2026-09-05)
- [x] `Config.cs` (constants + kerb/park/lane offset functions)
- [x] `RoadSegment` / `RoadNetwork` (centerlines, junctions, levels)
- [x] TestMaps: `build_basic_test_map` (fig8) first; other maps later
- [x] SmoothGeometry (corner fillets, resampling, SmoothedNetwork)
- **Gate G1: PASSED** (2026-09-05) — `/map` export matches Python server:
  `basic` map exact parity; OSM map all layers exact or within float tolerance
  (roads ≤0.44% per-polygon symdiff, total paved area Δ=4 m² / 0.0012%; the
  only deltas are degenerate sub-meter slivers that NTS drops and GEOS keeps)

### Phase 2 — Raceline ✅ (2026-09-05)
- [x] `DrivingGame.Sim/Raceline.cs` — banded solver, legal corridor,
      min-curvature optimization, per-curve memo cache (1:1 port of
      car/src/raceline.py; the Python stale-normal quirk in legal_corridor's
      t_junc is reproduced on purpose — see comment in Raceline.cs)
- [x] Reference harness: car repo `tools/raceline_reference.py` dumps 23
      routes (every code path: straight fast path, degree-2/3+ junctions,
      left/right/straight turns, one-way corridors, multi-lane + parking
      nominals, width-step diagonals, merge-right blend, 2894-station loop)
      to `data/raceline_reference/basic.json` incl. the raw paved + eroded
      safe polygons and all intermediates (K, lo/hi, base profile).
- [x] Gate harness: `DrivingGame.Sim.Tests` (xunit) — 73 tests:
      - STRICT (≤1e-9): P/N/offsets/cum + intermediates vs the dump, with
        PYTHON's polygons injected (NTS geometry built from the dump), so
        both engines solve on identical geometry. All 23 cases pass.
      - PRODUCTION PATH: C# computes its own erosion — NTS buffer with
        BufferParameters(16, Round, Round, 5.0) = shapely 2.x defaults
        (quad_segs=16). Result: ZERO offset difference vs Python on all 23
        cases (tolerance was 5 cm); erosion areas match to <0.5%.
      - Cache semantics (hit/miss counts, copy-out, FIFO) + network/
        corner-rounding parity checks.
- Perf (this machine, uncached SolveLineImpl): fig8 full loop 2894 st
  C# 54 ms vs Python 361 ms; 475-st window C# 3.1 ms vs Python 30 ms;
  cache hit 0.12 ms.
- **Gate: PASSED** (2026-09-05) — `dotnet test DrivingGame.Sim.Tests/`
  → 73/73 green.

### Raceline rework — per-vehicle dimensions (TODO, decided 2026-09-08, NOT implemented)

**Context.** Since 2026-09-08 the sprites have per-vehicle physical sizes
(the two trucks are longer/wider than the sedan box). The footprint table
lives in `Config.VEHICLE_SIZES` (single source of truth; renderer and sim
both consume it) and car-to-car collision math is per-class (see DONE
below). What still uses ONE global sedan GEOMETRY: `BicycleNav.WHEELBASE =
2.7` ("shared by every car"), width-only corridor erosion in
`Raceline.LegalCorridor` (`CAR_WIDTH/2 + ROAD_EDGE_TOLERANCE_M`), a
`SolveLine` cache key WITHOUT any vehicle geometry, and the off-road / lane
guard / bumper checks. Today a truck is rendered at its real size and
collides with other cars as itself, but the raceline corridor, corner
clearance and kinematics still assume a sedan.

**DONE (2026-09-08, part of this work): per-class DYNAMICS.** Longitudinal
and lateral dynamics are already vehicle-class specific:
`Config.VEHICLE_CLASS_SPECS` maps each sprite to a class with (top speed,
longitudinal acceleration, lateral-accel budget) - e.g. car 200 km/h /
2.8 m/s² / 4.5 m/s² vs truck 80 km/h / 1.3 m/s² / 3.5 m/s² vs heavy_truck
(loaded mixer) 80 km/h / 1.0 m/s² / 3.0 m/s² (values grounded in real
figures: DE truck speed limit, UMTRI rollover thresholds ~0.4-0.5 g vs >1 g
for cars, pickup skidpad ~0.7 g). `Car.Spec` exposes them; `BicycleNav`
uses per-instance `A_CRUISE`/`V_MAX`/`A_LAT_MAX` (set in the ctor from
`Car.Spec`) for the speed profile, corner speeds and understeer caps; FREE
mode uses the same values. Verified live: mixer accelerates at 1.0 m/s²,
profiles flat at 80 km/h, corners a 6 m fillet at ~12 km/h vs ~14.5 for
the sedan (theory ratio √(4.5/3.0) = 1.22, measured 1.2). The e2e suite
and the xunit collision tests pin spawns to the sedan class (`color:
"blue"`) so all baselines stay comparable.

**Why the wheelbase matters for the TARGET LINE (not just tracking).**
The sim point is the REAR AXLE; in a steady turn of radius R the front
axle traces √(R² + L²), i.e. off-tracking ≈ L²/2R:
- sedan (L = 2.7 m, R = 20 m): ~0.18 m → negligible, which is why the
  original width-only corridor "worked";
- extreme case (L = 30 m, axles at both ends, R = 20 m): ~16 m → the front
  swings far outside the reference line; the legal line is completely
  different (much wider entry);
- our trucks (L ≈ 7.0 / 8.4 m after the 2026-09-08 resize): ~0.6–1.2 m at
  R = 20 m → no longer negligible. Rear overhang behind the reference point
  cuts INWARD in corners (second effect of the same kind).

**Work items (when we pick this up):**
1. ~~Single source of truth for per-car geometry~~ **PARTIAL (2026-09-08):**
   footprint width/length is shared (`Config.VEHICLE_SIZES`, `Car.WidthM`/
   `LengthM`; renderer consumes the same table). Still global: Wheelbase,
   FrontAxleOffsetM, RearAxleOffsetM per car.
2. Raceline: `LegalCorridor`/`SolveLine` take the vehicle's half-width AND
   overhangs; legality must constrain the SWEPT ENVELOPE — in particular
   the derived front-axle path (√(R²+L²) per arc segment) / body corners —
   not only the reference point. `SolveCacheKey` must include the geometry.
3. ~~Collisions~~ **PARTIAL (2026-09-08):** car-to-car is per-class (v1
   corridor gap uses `(L_c + L_o)/2`, v2 prediction boxes use each car's
   real footprint, `Resolve`/`PlayerBodyCorners` use the real body). Still
   sedan-sized: off-road / lane guard / bumper checks.
4. Bicycle model: per-car wheelbase in kinematics (the corner-SPEED part is
   done - A_LAT_MAX is already per class).
5. Tests: keep the sedan as reference for existing e2e tolerances (spawns
   are pinned to `color: "blue"`); truck scenarios need adjusted
   expectations (or stay gated until this lands).

**DONE (2026-09-08, part 2): per-class car-to-car collision footprints +
fleet gridlock fix.** The fig8_cross mixed-fleet test (7 classes,
simultaneous start) reproduced two physical impossibilities: truck pairs
closing to real overlap (pickup+mixer −2.3 m, tan+tractor −1.3 m bumper
contact at t≈50/63 s — the v1 gap math subtracted ONE global `CAR_LENGTH`
instead of `(L_c + L_o)/2`) and a permanent gridlock of 6-7 cars (the v2
crossing check read a same-lane FOLLOWER as "coming from my right +
closing" and made the LEADER yield to its own tail; the closing test was
heading-only, so stationary cars kept "closing" forever). Fixed:
per-pair bumper gap in v1, per-car footprint boxes in v2 prediction,
`PlayerBodyCorners` per-vehicle, a same-lane-following filter that keeps v2
out of v1's domain, and a velocity-based (not heading-only) closing test in
right-before-left. Verified: 96/96 unit tests incl. the new `Fig8FleetTests`
regression (mixed fleet on fig8_cross, 90 s: no overlap, no standstill),
and the live run shows all 7 cars circulating with a min pairwise gap of
8.5 m centre distance and zero near-misses.

**Status: raceline/kinematics geometry NOT implemented** (user decision
2026-09-08: document first, implement later; per-class dynamics AND
car-to-car collision footprints landed the same day - see above).
Until then, non-sedan vehicles have realistic sizes, dynamics and
car-to-car collision boxes but a sedan's body envelope in corridor /
kinoematics — a known inconsistency that the "never physically impossible"
rule will eventually force us to fix properly.

### Phase 3 — BicycleNav ✅ (2026-09-05)
- [x] SmoothCurve, RefLine (+ bisect point_at/heading_at), projection
      refinement, speed profile, pursuit controller, parking / pull-over
      maneuvers — `BicycleNav.part1/2/3.cs` (~3.3k lines: route build +
      key-cached rebuilds, curvature-limited speed profile with braking ramps,
      pure-pursuit steering + Stanley law for the parking final straight,
      forward & reverse-in parking, U-turn §5a single swing / §5b three-point),
      `RefLine.cs` (arc-length line: PointAt/HeadingAt/CurvatureAt via bisect,
      ProjectS + RefineProject)
- **Gate G2: PASSED** (2026-09-05) — 5 new `BicycleNavTests` drive a real
  Car + BicycleDriver at 60 Hz on the `basic` map, all green: straight driving
  (keeps road, builds speed), 90° corner (follows the line, corner speed
  limited vs cruise), dead-end auto-park (pull-over swerve + stop, `Parked`
  latched), red-flag reverse-in park (front bumper 6 cm from the flag), full
  U-turn (single swing §5a, heading flipped ~180°, ends on road). Suite:
  78/78. Testing also caught and fixed 3 nav bugs: `Parked` never latched on
  forward stops; U-turn room check measured only to the next node instead of
  walking degree-2 continuations; reverse-in stopped at first pose convergence,
  3.7 m short of the flag.

### Phase 4 — Car & safety systems
- [x] Car (FREE + BICYCLE modes, gear, steering-wheel ramp), Driver classes
      (`Car.cs`: FREE-mode gear model with fresh-press shifts, `_steerPos`
      wheel ramp, BICYCLE dispatch to BicycleNav; `Driver.cs`: abstract base +
      Keyboard + Bicycle drivers)
- [x] LaneGuard, PhysicsValidator, Obstacles (contact stop)
      (`LaneGuard.cs`: per-car wrong-side detection + stats in the state
      snapshot; `PhysicsValidator.cs`: jump / off-road / turning-radius
      checks; `Obstacles.cs` + `ObstacleManager`: placement validation,
      contact stop, per-map layouts save/load — all wired into SimEngine and
      covered by `SafetySystemsTests`, 13 tests)
- Car-to-car collision & avoidance: **post-port feature**, started
      2026-09-06 (user decision) — v1 + v2 implemented, wired, verified
      2026-09-07; full status in Phase 9 ("Collision detection & avoidance")
- Gate: feature checks (breadcrumbs, reverse park, blinkers, u-turn)
  - **PASSED** (2026-09-05): reverse-in park ✓ (Phase 3 G2),
    u-turn ✓ (`Uturn_CompletesAndFlipsHeading`), breadcrumbs present
    (per-car toggle + state export), blinker parity ✓ — the earlier failure
    (scenarios 7/8: crossroads left/right ending on the straight-through
    segment) is fixed. Root cause: a braceless `if` in `MaybeRebuild`
    (`BicycleNav.part2.cs`) made `RebuildStraightPast()` run UNCONDITIONALLY
    after every turn rebuild — Python's indented equivalent is conditional, so
    the C# car silently replaced the signalled-turn line with a straight-past
    line on every signal (at a crossroads that IS the straight-through
    segment; at a T-junction it resolves to the wrong spoke and drives onto
    the wrong side). Fixed by bracing the block; repo-wide scan found no other
    braceless-if-with-two-statements. Verified: `test_turning.py` turning
    scenarios (corners, T-junction, Y, crossroads — 9 tests) all green on the
    C# console host.

### Phase 5 — Engine & API
- [x] Sim engine: command queue, fixed-dt accumulator (4-substep cap),
      sim clock = substeps, state snapshot export
      (`SimEngine.cs`: thread-safe command queue drained per frame, shared
      fixed-timestep accumulator clamped to 4×DtFixed, `_simStepsTotal` as the
      sim clock, `StateSnapshot()` serving GET /state)
- [x] Pacing: re-measure .NET `Thread.Sleep` quantization on this Mac; set
      spin window accordingly (may be smaller or unnecessary)
      (measured via `--measure-sleep`: ~3.3 ms mean sleep overshoot at the
      16.7 ms target; spin window set to 4 ms; production loop verified at
      ~60 Hz)
- [ ] REST API (all endpoints, identical payloads) in shared code
      (all endpoints implemented and live in `DrivingGame.Server/GameApi.cs` —
      the Python test scripts run against it unchanged; "shared code"
      placement is a Phase 7 concern)
- **Gate: PASSED** (2026-09-05) — console host endpoint parity vs Python
      server: live probe shows identical `/state` (top-level + per-car keys),
      `/map`, and `/teleport` payloads on both hosts; G1 already covered
      `/map` content; the full unchanged `test_turning.py` suite passes
      against the C# console host (22/22 incl. 576-car stress, see Phase 6).

### Phase 6 — Burn-in (console host)
- [x] 576-car stress suite (unchanged scripts) → **G3 PASSED** (2026-09-05):
      full `test_turning.py` run green — 21/21 deterministic scenarios +
      576-car fig8 stress phase: 0 jitters, 0 off-road, no crashes
      (wrong-side hits in the dense fig8 stress are the known baseline noise).
      Found & fixed on the way: a C#/Python modulo-semantics port bug —
      Python's `x % 360` is always non-negative, C#'s keeps the dividend's
      sign, so every ported angle wrap `(d + 180) % 360 - 180` misread a
      heading crossing the 360°→0° boundary as a ~360° jump. In
      `PhysicsValidator.CheckTurningRadius` that threw an unhandled
      `PhysicsViolationException` and killed the server mid-stress (implied
      radius 0.05 m from Δheading 359.92° / Δpos 314 mm). Fixed with
      `Math.WrapDeg` / `Math.PosDeg` helpers (`MathAndNtsCompat.cs`) at all
      10 wrap sites + 5 heading-update sites; no other `%`-based wraps left.
- [x] Capacity sweep → **G4 PASSED** (2026-09-05): `tools/capacity_sweep.py`
      spawns N cars on the fig8 loop and measures pace = sim-time/wall-time
      over a ≥25 s sim window (pace ≥ 0.97 = real time). Single-threaded
      (Parallel.For is Phase 8):
      - **C# real-time ceiling: ~170 cars** (170 → pace 0.992; 180 → 0.951)
      - Python ceiling: ~90 cars (50 → 1.000; 100 → 0.936)
      - Per-car speedup ≈ 2×, roughly constant from 160 to 576 cars
        (576: C# 0.301 vs Python 0.145 pace)
      - Pace falls super-linearly in N on both hosts → some loop component
        grows faster than O(N) (candidates: per-frame UpdateState dict
        allocations / GC, IsOnRoad ×2 per car per frame, spatial-hash
        rebuild per substep). Input for Phase 8.
      - Caveat: part of the high-N overhead is /state JSON serialization
        polled every 250 ms by the measurement client; that goes away in
        the Phase 7 Godot integration (in-process snapshot read), so the
        embedded ceiling should be at or above these numbers.
      Full data: `data/capacity_sweep_summary.json` (+ raw per-phase files).
- [x] Optional A/B: Python vs C# side-by-side sanity — done as part of G4:
      identical script against both hosts (C# :5099, Python :5000), same
      spawn/poll methodology, so the comparison is apples-to-apples.
- [x] Visible e2e run: `scripts/run_e2e.sh` (port of car/scripts/run_e2e.sh)
      — fresh C# host on :5000 + visible Godot window + full unchanged
      suite in the foreground. **22/22 passed** (2026-09-05), incl. 576-car
      stress phase: 0 jitters, 0 off-road.

### Phase 7 — Godot integration (target end state)
- [ ] Godot csproj references `DrivingGame.Sim`
- [ ] Sim thread inside the Godot app; MapRenderer reads in-process snapshots
      (retire HTTP double-pipeline for gameplay; keep buffer + interpolation)
- [ ] Embed REST API in the Godot app for external test injection
- **Gate G5:** visual check, no stutter; tests run against the embedded API

### Phase 8 — Scaling (optional, after everything is green)
- [ ] Switch the core loop to `Parallel.For` across cars (one-line change,
      enabled by the concurrency-ready design rule above). Verify stress
      suite still passes; confirm determinism (independent per-car updates,
      shared state confined to pre/post passes).

### Phase 9 — Traffic participants (post-port extension, NOT part of the 1:1 port)

Goal: participant types beyond a single car class — trucks, bicycles,
pedestrians, playing children. Deliberately after G5: Phases 0–8 stay a 1:1
mirror of the Python original; this is new feature work on the verified
engine. **Exception (hauke, 2026-09-06):** car-to-car collision + avoidance
and driver profiles started BEFORE G5 — see the three sections at the end of
this phase.

Design (hauke, 2026-09-05): **two orthogonal class hierarchies, composed** —
what a participant IS and how it is DRIVEN are independent axes:

- **Vehicle axis** (physical properties): `TrafficParticipant` base →
  `RoadVehicle` / `Pedestrian`. Road vehicles carry a `VehicleSpec`
  (width, v_max, a_lat_max, braking, wheelbase, edge-hug flag) — mostly data;
  subclass only when physics LOGIC differs (e.g. articulated truck). A Porsche
  is a spec instance, not a class.
- **Driver axis** (behavior policy): generalizes the existing `Driver(ABC)`
  seam (`Car(driver=...)`, Keyboard/Bicycle drivers already live here — the C#
  port carries it over as `RoadVehicle(VehicleSpec, Driver)`). New: style
  policies as driver classes/strategies — cautious vs aggressive: how close to
  the physical limit they drive (speed-profile scale), following distance,
  reaction time, lane-change aggressiveness. Same Porsche + different Driver =
  different behavior; N vehicles × M drivers without N×M classes.
- Pedestrians have no driver — their behavior model is intrinsic
  (wander/cross/wait); a playing child is a parameterized pedestrian (smaller,
  impulsive). All stochastic behavior uses seeded RNG to keep the determinism
  regime.

Raceline interaction: the cached solve stays keyed on
(route geometry, VehicleSpec) — driver style acts at follow time (scales the speed profile,
adapts gaps), so style changes never invalidate the line cache. If a style ever
needs its own line variant (e.g. cautious = no corner cutting), style joins the
cache key; the cheap speed-profile re-derivation already covers most of it.

Open item (hauke, 2026-09-05) — **truck left-turn encroachment** (Flankieren):
on narrow two-way streets a Sattelschlepper can often only negotiate a tight
corner by pulling wide into the oncoming lane early, smoothing the curve. Today
the centreline bound is a hard constraint for every vehicle; for wide/long
VehicleSpecs it must become CONDITIONALLY relaxable — two-pass solve: (1) normal
solve with the hard bound; (2) only if the result is infeasible for the class
(line radius below the vehicle's mechanical minimum, or speed profile below a
crawl floor) re-solve with `lo` relaxed into the oncoming lane, bounded by its
far curb. "Only when necessary" = gated on that feasibility check, never a style
option. Cache stays clean: the relaxation is deterministic per (geometry,
VehicleSpec). Interaction cost: an encroaching truck conflicts with real
oncoming traffic — needs yielding/right-of-way behavior from the other axis
(driver policy + collision avoidance), which is why this lands with Phase 9's
mixed-traffic work, not before.

Impacts when started: engine loop (`List<Car>` → `List<TrafficParticipant>`,
concurrency rule generalizes to "a participant update is a pure function of
(own state, immutable world snapshot)"), renderer (per-type sprites), REST API
(`/cars` semantics vs `/participants` — decide against existing test scripts),
tests (new invariants need baselines first: pedestrians never teleport, cars
brake for participants ahead).

- **Gate G6:** mixed-traffic scenario (car + truck + bicycle + pedestrian incl.
  playing child) passes the stress invariants; determinism verified
  (same seed → same run).

### Collision detection & avoidance (started 2026-09-06, ahead of G5 — user decision)

User brief: "erstmal die Kollision und einfache Kollisionsvermeidung (durch
Abbremsen)" — collision + simple avoidance by braking. Cars exert NO
collision forces on each other; they brake and rest against each other like
against a wall (same semantics as `Obstacles.ApplyContactStop`). Two layers,
both per physics substep in `SimEngine.Tick`:

1. **AVOIDANCE** — every car gets a speed cap = the fastest speed from which
   `CAR_BRAKING` still stops behind the nearest car in its forward corridor
   (plus a standstill gap). Applied as decel-limited braking AFTER the
   driver's own longitudinal logic ran → works for every driver (BICYCLE and
   FREE, incl. U-turn/reverse maneuvers) without touching their code.
2. **RESPONSE** — if two body boxes overlap after the step, both cars roll
   back to their pre-step pose and stop. A substep cannot skip over a 4.4 m
   car, so the pre-step poses were clear: no interpenetration, no teleport
   (the validator sees zero motion for them).

**v1 — implemented** (`DrivingGame.Sim/CarCollisions.cs`, 2026-09-06; wired +
fixed 2026-09-07):
- Broadphase: uniform grid, 20 m cells (look-ahead ring = 3 cells)
- Forward corridor: lateral ±1.8 m of own axis (a car in the adjacent lane
  on a 7 m road sits ~3.5 m away and must NOT trigger), look-ahead 40 m,
  standstill gap 3 m
- Cap: `v_ahead projected onto my axis + sqrt(2·CAR_BRAKING·max(gap−3 m, 0))`;
  boxes touching → cap 0. **Fixed 2026-09-07**: the original formula was
  `min(v_ahead, sqrt(...))`, which clamps the cap to `v_ahead` (0 for a
  stationary lead) the INSTANT the corridor detects it, regardless of how
  much gap is still open — forcing an immediate hard stop 40 m out instead
  of a smooth approach-and-brake. It went uncaught until now because v1 was
  never wired into `Tick` before. Corrected to the relative-motion braking
  distance (`+`, not `min`): the fastest speed from which I can still match
  the lead's speed (or stop, if it's stationary) within the remaining gap.
  Geometric corridor only (no refline following) — v1 scope: the
  curvature-limited speed profiles are conservative enough that a straight
  40 m look-ahead still gives ≥1.5 s detection in curves; refline-following
  detection deferred to v2.
- `Resolve`: SAT body-box overlap → both cars roll back + stop; returns the
  contacted uids (the validator treats their motion as externally constrained)

v1 wiring (`SimEngine.Tick`, 2026-09-07):
- [x] Two-pass restructure — loop A (car Update + obstacle contact +
      avoidance caps), then `Resolve`, then loop B (Validator + LaneGuard).
      Caps computed from a PRE-step snapshot of all cars, before any of them
      move that substep. Legacy single-pass path kept for `CollisionsEnabled
      = false` or a single car (verbatim old behavior, zero regression risk).
- [x] `CollisionsEnabled` flag on SimEngine (default true) + `--no-collisions`
      CLI arg (`DrivingGame.Server/Program.cs`)
- [x] Cost measurement: Stopwatch µs/call for `ComputeAvoidCaps` + `Resolve`,
      printed every 300 substeps (5 sim-s). Measured: 2 cars ~5-14 µs/call
      combined; 150-car stress ~41-48 µs/car for `ComputeAvoidCaps` (~6-7 ms
      total), `Resolve` ~0.6-0.7 µs/car — comfortably under the ~200 µs/car/
      substep total budget.
- [x] Two additional fixes surfaced by wiring it in for real (both silent
      until an actual multi-car `Tick` run existed to expose them):
      1. **Decel-limited braking now baselines off the PRE-`Update()` speed**,
         not the driver's post-update output. The driver still tries to
         accelerate every substep (it has no idea another car exists) and
         bumps speed up a little BEFORE avoidance runs; braking from that
         bumped value nets less than `CAR_BRAKING` of actual deceleration
         (measured: ~7.2 m/s² instead of 10), causing the follower to
         overshoot the intended ~3 m standstill gap toward ~0.
      2. **Accelerate is suppressed in the control fed to a car's `Update()`**
         when the pre-step avoidance cap already forbids exceeding its
         current speed. Without this, `BicycleNav`'s standstill-creep
         mechanic (`CREEP_SCALE`, meant to unstick a car from a stall) nudges
         `X/Y` forward every substep BEFORE the post-`Update` cap ever runs,
         creeping a "stopped" follower slowly through the lead car over many
         seconds instead of resting.
- [x] Verified: head-on pair test + 150-car stress run (no exceptions/NaN,
      cost measured above; suite gates unchanged — 94/94 tests pass)

**v2 — implemented** (`DrivingGame.Sim/CarCollisions.cs`, 2026-09-07): time-
window pair prediction for oblique/crossing conflicts (the corridor cap alone
cannot see a car that is not yet in the forward tube):
- **Hybrid two-pass avoidance:** the corridor cap (v1) still handles smooth
  continuous car-following; a symmetric pair check ADDS oblique/crossing
  detection that predicts each car's position ahead (advancing along the
  `BicycleNav.Ref` refline when available — a route exists even without an
  explicit destination — else linear extrapolation) and tests all nearby
  pairs' body boxes.
- **Continuous sweep, not 3 fixed samples** (deviation from the originally
  confirmed design): predicting at exactly `{1, 3, 5} s` and testing box
  overlap at just those three points was found — empirically, via the
  crossing test below — to miss fast-closing conflicts entirely. At cruise
  speed (~20 m/s) the future position sampled at a FIXED stage overshoots
  the actual crossing point, and the real-time window where a fixed-stage
  sample would land exactly ON a car-length-wide conflict is narrower than
  the gap between the stages themselves; two accelerating cars converging on
  a junction sailed straight through undetected until they nearly collided
  and `Resolve`'s rollback (not graceful avoidance) caught it at the last
  instant. Fixed by sweeping every 0.1 s up to 5 s and classifying the
  EARLIEST hit into the same graded 1 s / 3 s / 5 s response bands (still
  re-evaluated fresh every substep — see `GradedCap`). Each car's sweep
  trajectory is precomputed ONCE per substep (not once per pair it appears
  in) to keep the O(pairs × 50 samples) cost bounded in dense traffic.
- **Prediction boxes are LENGTH-padded (+2 m each end), not width-padded**:
  the discrete sweep can still flicker (detect/miss on alternating substeps)
  right at the decision boundary because the unpadded box's edge just clips
  in and out between consecutive 0.1 s samples; widening along the direction
  of travel closes that gap. Widening the WIDTH too was tried first and
  reverted — it pushed the effective half-width past half the standard 7 m
  road's lane separation (~1.75 m), so two cars safely 3.5 m apart in
  opposite lanes on a straight road registered a false conflict.
- **TTC stages 1 s / 3 s / 5 s, graded response:** earlier hit → stronger
  reaction (≤1 s → stop, ≤3 s → cap to a safe speed, else → mild anticipation
  at the gentler `PARK_BRAKING`).
- **Prediction velocity: `max(current speed, planned target speed)`** per car
  — conservative; avoids phantom conflicts from cars that are slow NOW but
  will be at cruising speed by the crossing.
- **Right-before-left priority** for oblique pairs: the car seeing the other
  come from its RIGHT yields. Geometric test: other's position in my right
  half-plane AND other is closing on me (dot-product test, world-frame - the
  local-frame "ahead-right" derivation was discarded as degenerate: two
  perpendicular cars can each read the other as ahead-right). Deterministic
  tie-breaker for equal-priority pairs (both or neither see the other as
  coming from their right): lower uid yields.
- `public RefLine? Ref => _ref;` accessor added to `BicycleNav` (null without
  an active route).

v2 verification: `DrivingGame.Sim.Tests/CollisionTests.cs` (2026-09-07) — a
headless `SimEngine` driven directly (no existing test did this before;
`car.Update` alone bypasses the collision system entirely, which lives only
in `Tick`), on the "basic" test map's crossroads (`cross_n/s/w/e` around
`cross_center`):
- `StationaryLeadCar_FollowerBrakesAndStopsBehind` — v1 corridor cap alone:
  a following car brakes smoothly to a stop ~2-3 m behind a stationary lead
  in the same lane, no `Resolve` rollback.
- `HeadOnSeparateLanes_NeitherBrakes` — false-positive guard: two cars
  approach head-on on the same straight road, each in its own lane (~3.5 m
  apart, outside the 1.8 m corridor half-width). Asserted against a SOLO
  baseline (each car driven alone first) rather than "speed never decreases"
  — a lone car legitimately decelerates for the real junction ahead
  regardless of the other car, so the meaningful claim is "no DIFFERENT than
  driving it alone", not "never brakes at all".
- `SimultaneousCrossing_EastYieldsToNorth_RightBeforeLeft` — the scenario
  that needs v2: car A (north→south) and car B (east→west) approach the
  crossing symmetrically (85 m out, equal speed profile). B sees A on its
  right and yields (brakes hard, well before the junction); A proceeds
  through without ever nearly stopping. Both eventually clear the junction
  (B resumes once A has passed) — no deadlock, no mutual stop (the v1-only
  failure mode this test exists to rule out).

v2 remaining: none — pair check, RBL, and cost measurement are all done and
verified above.

### Driver profiles (first Driver-axis instances, user request 2026-09-06)

Concrete first instances of the Driver-axis style policies above:
- **Cautious** ("Miss Daisy"): target speed ≈ 0.5 × profile max — drives well
  below the limit, big gaps
- **Normal**: 1.0 × (today's behavior; default)
- **Sporty**: 1.0 × but full acceleration, tries to reach the planned max as
  fast as possible, minimal margin

Implementation: per-car speed factor assigned at spawn (API param), applied
where vTarget is finalized (`BicycleNav.part3.cs`). Style acts at follow time
→ never invalidates the raceline cache (consistent with the design above).
- [ ] Spawn API param (default Normal)
- [ ] Factor in BicycleNav vTarget finalization
- [ ] Test: mixed profiles on a shared route — no collisions, stable ordering

### Test infrastructure (user request 2026-09-06)

- **New test map: fig-8 with a flat central intersection** (`TestMaps.cs`):
  the same lemniscate loop as `basic`, but BOTH crossing passages at ground
  level (level 0, no bridge) so the two branches truly interact and
  right-before-left applies at the center. Spawn points on both branches.
- **New e2e scenario:** 8 cars, mixed profiles (Cautious/Normal/Sporty),
  bidirectional traffic through the central crossing. Invariants: zero
  collision responses (`Resolve` never fires), all cars complete their routes,
  stress gates hold (0 off-road, 0 jitter).
- [ ] `TestMaps.cs`: new map builder
- [ ] `tests/test_turning.py`: new scenario (numbered after the current suite)

## Pending (user decision 2026-09-05)

- **Stale-normal quirk review — after the port, before any new work.**
  The Python `legal_corridor` t_junc uses stale nx/ny (route-end normal at
  every station); the C# port reproduces it on purpose. After Phase 8 and
  BEFORE Phase 9 / any new feature: fix in Python first, regenerate the
  reference dump, run tests + e2e scenarios, compare lines before/after;
  port the fix to C# only if it is a verified improvement. Otherwise document
  and keep the quirk.

## Risks / notes

- **Pacing re-tuning:** the spin window was tuned for CPython `time.sleep`
  behavior on this Mac; .NET may need different parameters or none at all.
  Re-measure before trusting frame timing (Phase 5).
- **Determinism:** single-threaded correctness first; parallelization only in
  Phase 8 with an explicit audit of shared state.
- **Transition safety:** after the 2026-09-05 consolidation the e2e suite
  lives in THIS repo (`tests/test_turning.py`); the behavioral docs follow
  after Phase 7 (see Consolidation note). `/Users/hauke/prj/car` is reference
  + fallback only meanwhile. Its Python sim must stay runnable until the
  stale-normal
  quirk review (after Phase 8, before any new feature work) is done — that
  experiment is deliberately run in Python first — then the car repo is
  history.
