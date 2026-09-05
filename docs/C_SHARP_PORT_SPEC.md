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

### Phase 0 — Scaffolding
- [ ] Solution layout in this repo (Sim + Server projects, net8.0)
- [ ] Godot csproj: exclude Sim/ and Server/ from its compile glob
      (SDK-style projects glob all .cs under the directory!)
- [ ] Copy OSM cache file into `data/osm_cache/`
- [ ] `.gitignore` for bin/obj

### Phase 1 — Data layer
- [ ] `Config.cs` (constants + kerb/park/lane offset functions)
- [ ] `RoadSegment` / `RoadNetwork` (centerlines, junctions, levels)
- [ ] TestMaps: `build_basic_test_map` (fig8) first; other maps later
- [ ] SmoothGeometry (corner fillets, resampling, SmoothedNetwork)
- **Gate G1:** `/map` export matches Python server (float tolerance)

### Phase 2 — Raceline
- [ ] Banded solver, legal corridor, min-curvature optimization,
      per-curve memo cache
- Gate: solve outputs vs Python reference dump (~1e-9 tolerance)

### Phase 3 — BicycleNav
- [ ] SmoothCurve, RefLine (+ bisect point_at/heading_at), projection
      refinement, speed profile, pursuit controller, parking / pull-over
      maneuvers
- **Gate G2:** single-car trajectory sane; parking + pull-over features work

### Phase 4 — Car & safety systems
- [ ] Car (FREE + BICYCLE modes, gear, steering-wheel ramp), Driver classes
- [ ] LaneGuard, PhysicsValidator, Obstacles (contact stop)
- Gate: feature checks (breadcrumbs, reverse park, blinkers, u-turn)

### Phase 5 — Engine & API
- [ ] Sim engine: command queue, fixed-dt accumulator (4-substep cap),
      sim clock = substeps, state snapshot export
- [ ] Pacing: re-measure .NET `Thread.Sleep` quantization on this Mac; set
      spin window accordingly (may be smaller or unnecessary)
- [ ] REST API (all endpoints, identical payloads) in shared code
- Gate: console host endpoint parity vs Python server

### Phase 6 — Burn-in (console host)
- [ ] 576-car stress suite (unchanged scripts) → **G3**
- [ ] Capacity sweep → **G4** (record the new real-time ceiling)
- [ ] Optional A/B: Python vs C# side-by-side sanity

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

## Risks / notes

- **Pacing re-tuning:** the spin window was tuned for CPython `time.sleep`
  behavior on this Mac; .NET may need different parameters or none at all.
  Re-measure before trusting frame timing (Phase 5).
- **Determinism:** single-threaded correctness first; parallelization only in
  Phase 8 with an explicit audit of shared state.
- **Transition safety:** the Python server stays committed and runnable in
  `/Users/hauke/prj/car` as reference + fallback throughout.
