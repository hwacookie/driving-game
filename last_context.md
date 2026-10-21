# Carried-forward context (compacted)

Snapshot of where the work stands — carry this block forward as the new
context. Written 2026-10-19 on branch `feat/unified-units` (pushed).

## Goal

Make the driving-game's intersections visually correct (German halt lines,
no centreline paint inside crossings, solid approach paint, a centre dot)
— **done** — then unify the project's mixed metre/pixel coordinate
systems — **done, all three steps plus the final naming sweep.**

## Constraints & Preferences

- Respond in English; all docs/comments English.
- Explicit user instructions are binding — do exactly what is asked, no
  side work, no silent overrides.
- Debugging: reproduce deterministically → log state → understand root
  cause → then fix. No speculative fixes.
- Poll for conditions; never blind long sleeps (≤1 s pacing between polls
  is fine).
- Never physically impossible motion; fix logic instead of weakening checks.
- No pygame parity requirements — Godot idioms win.
- **Never signal Godot** (SIGTERM → .NET abort + macOS crash window). Close
  a window by bumping `/tmp/driving_game_godot_gen` and polling until it
  exits; the client watches that file (`MapRenderer.cs:199`) and calls
  `GetTree().Quit()`. Signalling the .NET console host IS fine.
- E2E: `scripts/run_e2e.sh`, default `--warp 4`, Godot window visible
  unless `--no-godot`, stream output.

## Key Decisions

- Sim owns geometry: placement lives in `RoadNetwork`/`RoadNetworkGeometry`;
  `MapRenderer` only paints the payload.
- Halt line = right lane only, perpendicular to the arm, from the centreline
  to `halfW - STOP_LINE_EDGE_GAP_M`; no shark's teeth (sign 205 carries the
  rule).
- Band offset: the band is drawn centred on its geometry line, so
  `GetStopLines()` adds `+ STOP_LINE_WIDTH_M/2` — its near edge IS the
  mouth line, which is where centreline paint ends (user instruction).
- Centreline cut at each arm's own mouth line via `MouthCross` (polyline ×
  mouth-line intersection), never "a mouth distance outside the vertex run";
  a chain ending at the node is pulled back to its own mouth; closed loops
  pass a crossing twice → **all** passes are cut; cut pieces with `SubArc`,
  not `TrimEnds`.
- Crossing centre dot is not a German rule — a user-requested marker, degree
  ≥ 4 nodes only.
- Zoom culling: one `Visible` flag per mesh batch (`MarkingMinZoom` 2.0 vs
  `EdgeLineMinZoom` 6.0 px/m).
- **Units: the `…M`/`…Px` naming discipline was chosen over wrapper types**
  (user picked "(a)"). Wrapper types measured 2.1–2.8× slower on a
  `CarCollisions`-shaped loop, C# has no operator overloading, NetTopologySuite
  takes doubles.
- **The frame flip is binding**: the sim's world frame IS metres.
  `Config.PIXELS_PER_METER` is deleted, not kept at 1. Pixels exist only at
  the Godot drawing boundary (`Camera2D.zoom` = px/m) and at the raceline
  fixture boundary.
- Naming rule (docs/UNITS.md): `…M` = metres, **no suffix = the world frame**
  (which IS metres), `…Px` only at a genuine screen/asset boundary.

## Progress

### Done

- **Git.** `fix/fig8-smooth-corners` → `c211ec8` pushed. Merged into `main`
  as `1b68895` — one conflict in `RoadNetwork.cs`: kept **both** main's
  `_roundedLinesCache` and the branch's new `(style, coords)`
  `_markingCenterlinesCache`. Branch **`feat/unified-units`** off merged main,
  **now pushed** (`origin/feat/unified-units` @ `2651ca2`).
- **(1) /state → metres** — commit `64b350a`: `SimEngine.Meters(px)` at the
  payload boundary; payload declares `["units"] = "meters"`; `/wait position_reached`
  threshold in metres; `MapRenderer`'s `Ppm` deleted.
- **(2) naming discipline** — commit `f31bac4`: one scale-factor name
  `pxPerM`/`PxPerM` (238 old `pppm`/`PPPM`/`Pppm` spellings gone), conversion
  sites name both sides, `JunctionTrimM` → `JunctionTrimPx`, `docs/UNITS.md`
  + AGENTS.md rule.
- **(3) the frame IS metres** — commit `0daef98`:
  - `Config.PIXELS_PER_METER` deleted; nothing in `DrivingGame.Sim/**` converts.
  - `pxPerM` parameters/fields/identity multiplications removed from
    `SmoothGeometry`, `RefLine`, `BicycleNav.part1-3`, `RoadNetwork(Geometry)`,
    `Raceline`, `CarCollisions`, `Car`, `LaneGuard`, `PhysicsValidator`,
    `Obstacles`, `TestMaps`/`MapBuilder`, `SimEngine`, `MapPayload`.
  - `CarCollisions.CellSize` → `CellSizeM = 20.0` (genuinely 20 m now); the
    stale "3 cells" comment replaced with the real counts: look-ahead ring
    `ceil(250/20)+1 = 14`, pair ring `ceil(150/20)+1 = 9`.
  - `data/raceline_reference/basic.json` keeps its own declared frame
    (`"pxPerM"`, legacy `"pppm"` = 2). `RacelineReferenceTests` converts at
    the boundary via `FixturePxPerM` + `FramePts` + scaled
    `BuildGeometry`/`ToPolygon`, and writes frame values back. NOT converted
    (frame-independent): `offsets`, `lo`/`hi`, `base_prof`, `cum` (already
    metres), props widths, `K` (rad/m), unit normals `N`.
  - `/obstacles`, `/segment/{idx}`, `/start_points` answer metres;
    `docs/REST_API.md` and `docs/UNITS.md` state the one-dialect rule.
  - `tests/test_turning.py`, `tests/car_line_check.py`, `scripts/run_e2e.sh`
    dropped their `PX_PER_M` / `ppm = 2.0` conversions.
- **(4) final naming sweep** — commit `2651ca2`: the sim has no `…Px`
  sim-frame names left.
  - `RoadNetworkGeometry.PointToSegment` returned `(Dist, Px, Py)` →
    `(Dist, ProjX, ProjY)` (the projected point is a frame value; no caller
    used the `.Px`/`.Py` accessors — all destructure or take `.Dist`).
  - `MouthCross` comment said "at distance `gapPx`"; the parameter is `gap`,
    in metres.
  - `BicycleNav.part3.GenerateUturn` returned `PtsPx` → `WorldPts`, matching
    its own doc comment and the caller's destructure name.
  - The other `…Px` locals listed as open in the previous snapshot
    (`SmoothGeometry` `chord,r`; `RoadNetworkGeometry` `gap,half,length,s,width`;
    `RoadNetwork` `d,half,len,r,s0`; `RefLine` `off`) had already lost their
    `Px` when the `pxPerM` multiplications went away in `0daef98` — the audit
    found nothing left to rename there.
  - `MapRenderer`'s `rPx` (line 1812) stays: it is the client's own screen
    frame (blinker radius in Godot pixels, divided back out by
    `Camera2D.zoom`) — allowed by docs/UNITS.md rule 3.
- **Verified after the sweep:** `dotnet build` clean (0 errors, 8 pre-existing
  warnings), unit tests **91/91** (2 skip, baseline), e2e `--warp 4` →
  **21/23** — the same two multi-car crossing-stress crashes as baseline
  (`fig8_xing_plain`: car 86 contacted car 84 @ t=7.6 s; the other @ t=3.1 s,
  car 26 contacted car 52). Log:
  `tests/run_test_T_023_0_20261019_001526.log`.
- **Host stopped:** the suite leaves the C# host on `:5001` for crash
  analysis; `pkill -f DrivingGame.Server` cleared it, `:5001` now answers
  nothing.

### Open

- [ ] Nothing on the units work — steps 1–4 are done and pushed.
- [ ] The two multi-car crossing-stress crashes (`fig8_stress` skipped by
      design, 21/23 otherwise) are the standing baseline gap, not a
      regression. Only worth opening if the user asks about multi-car
      collisions.
- [ ] `last_context.md` is untracked (deliberate — it is a session artifact).

### Blocked

- None.

## Critical Context

- Root `/Users/hauke/prj/driving-game`; branches `main`,
  `fix/fig8-smooth-corners` (pushed), `feat/unified-units` (current,
  `2651ca2`, **pushed**).
- Key files: `DrivingGame.Sim/{Config,RoadNetwork,RoadNetworkGeometry,Car,CarCollisions,LaneGuard,Obstacles,SimEngine,SmoothGeometry,Raceline,RefLine,TestMaps,BicycleNav.part1-3,PhysicsValidator}.cs`,
  `DrivingGame.Api/{MapPayload,GameApi}.cs`, `MapRenderer.cs`, `SimHost.cs`.
- Constants: `STOP_LINE_WIDTH_M=0.5`, `STOP_LINE_EDGE_GAP_M=0.5`,
  `CENTERLINE_JUNCTION_GAP_M=0.5`, `CENTERLINE_SOLID_BEFORE_JUNCTION_M=6.0`,
  `CROSSING_MARKER_DIAMETER_M=0.3`, `EdgeLineMinZoom=6.0f`,
  `MarkingMinZoom=2.0f`, `CellSizeM=20.0`, `LookAheadMaxM=250.0`,
  `PairCheckRadiusM=150.0`, `Raceline.SampleM=0.5`.
- Basic-map crossings (metres): `(750,720)`, `(1250,720)`, `(250,1750)`,
  `(2250,1220)`, `(2250,1720)`, `(1250,1750)` = signed `xing_c`,
  `(1750,1750)` = unsigned `xingp_c`; three figure-8 loops (`fig8_*`,
  `xing_*`, `xingp_*`). Map bounds `(0,0),(3000,1950)` m.
- Build: `dotnet build DrivingGame.csproj -v q --nologo`; unit tests:
  `dotnet test DrivingGame.Sim.Tests/` (91 pass / 2 skip).
- Audit for frame crossings (should list ONLY the fixture boundary):
  `grep -rn "pxPerM\|PxPerM" --include=*.cs .` → only
  `RacelineReferenceTests`; `grep -rn "PIXELS_PER_METER" --include=*.cs .` →
  empty. Both verified clean 2026-10-19.
- Audit for leftover `…Px` names (verified 2026-10-19 — only the two
  legitimate `MapRenderer.cs:1812` lines):
  `git ls-files '*.cs' | grep -v /obj/ | xargs grep -nE "(^|[^A-Za-z0-9_])[A-Za-z_][A-Za-z0-9_]*Px([^A-Za-z0-9_]|$)" | grep -vE "pxPerM|PxPerM"`
  — plus a separate check for bare `Px`/`Py` names, which that pattern misses.
- Screenshot recipe (works; `--headless` fails with `Parameter "t" is null`;
  use a free port, `:5007`):

  ```bash
  rm -f /tmp/shot.png /tmp/godot_shot.log
  nohup "/Applications/Godot_mono.app/Contents/MacOS/Godot" --path . -- \
        --source embedded --map basic --port 5007 --center 1250 1750 --zoom 18 \
        --screenshot /tmp/shot.png --delay 2 > /tmp/godot_shot.log 2>&1 &
  until [ -s /tmp/shot.png ] || grep -q "SavePng failed" /tmp/godot_shot.log; do sleep 1; done
  ```

- Measuring a painted length from the PNG: scan a row/column with PIL and
  divide pixels by the announced zoom (`docs/lights_report.md` pattern).
- Running processes: none. `:5001` is free.
