# C# Port of the Simulation — Spec & Plan

Date: 2026-09-04 · Status: approved (hauke), in progress
Source: `/Users/hauke/prj/car` (Python, ~10.7k lines) → this repo (C# / .NET 8)

## Goal

Port the headless simulation from Python to C# and integrate it into this repo.
Target end state (hauke decision 2026-09-04):

- **Renderer and simulation in one app (Godot):** MapRenderer reads sim state
  in-process; no HTTP for gameplay.
- **REST API (port 5000) stays embedded** so external test injection keeps
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
├── DrivingGame.Server/       console host: Sim + REST API on :5000
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
mirror of the Python original; this is new feature work on the verified engine.

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
