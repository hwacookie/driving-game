# How Testing Works Here

> **Status 2026-10-23 (code-verified):** every command below was run against
> the tree at `c827710` + the comment-only doc pass; scenario numbers, id.
> durations and runner flags are read out of `tests/test_turning.py` and
> `scripts/run_e2e.sh`, the baseline is the full-suite log
> `tests/run_test_T_242_0_20261023_231042.log`. The scenario list in §2a is
> generated from `tests/test_turning.py --list` - regenerate it instead of
> editing numbers by hand.

Testing has three layers:

1. **Headless unit / integration tests** (`DrivingGame.Sim.Tests/`, xunit on
   .NET) - no game process, no window, no REST server: they build the road
   network and the car in-process and assert on placement rules, collision
   geometry, stop-on-contact physics, layout save/load, the lane guard and the
   raceline solver against its golden fixture. They run in ~25 s and are the
   first thing to run after a change:
   ```bash
   dotnet test DrivingGame.Sim.Tests/          # 102 passed / 2 skipped
   ```
   (The Python pytest suites of the original `car/` repo -
   `tests/test_obstacles.py`, `tests/test_road_network.py`,
   `tests/test_api.py` - were retired with that repo; these xunit suites are
   their equivalents. See §6.)
2. **Live e2e suite** (the rest of this document) - the running game
   exposes a REST API, and test scripts drive the actual game (actual
   physics, actual rendering, actual road network) from the outside,
   exactly like a human or another program would: `tests/test_turning.py`
   for the turning scenarios.
3. **Manual visual QA scripts** (`tests/light_check.py`,
   `tests/car_line_check.py`) - no pass/fail, they stage a situation for the
   eye (see the section at the end).
4. **Measurement scripts** (`tests/pose_stutter_check.py`,
   `tests/tape_state_agreement.py`) - no eye either: they read the client's own
   `--motionlog`, or the sim's own two channels, and print a distribution / a
   verdict, so "is the playback smooth?" and "do the channels agree?" become
   numbers that can be argued with.

`tests/tape_state_agreement.py` holds `/state` and the tape against each other
at ONE frozen substep. Two things it found are worth keeping as facts, because
both look like bugs and neither is:

- **`/teleport` by SEGMENT, not by start point**, is what puts a car on the
  bridge: the `fig8_bridge` START point spawns at the crossing node and the
  car's first segment there is a ground one (`/state` answered `segment 139,
  level 0`) — so a check that teleports by name proves nothing about `level`.
- **`/state` is a per-FRAME snapshot, the tape is per-SUBSTEP.** Freeze the
  sim, then ask the tape for the frame AT the step `/state` names
  (`round(time / dt_fixed)`) — not for the head. The head can sit one substep
  past the snapshot a client sees; comparing the two answered a 9.5 cm pose
  gap that was a clock question, not a disagreement about the same world.

The map stamps are checked by hand with the one-shot client recipe from
`AGENTS.md` (`--review <tape>` on its own map: `map check: ok`; on the
embedded default OSM ground: `MAP MISMATCH` + `REVIEW-REFUSED … no cars
painted`), and `--ignore-map-hash` is the documented escape. The A/B that
proves `level` drives the PICTURE is the same tape with its level column
zeroed: ON the deck versus hidden UNDER it, at one cursor and one camera.

See [REST_API.md](REST_API.md) for the endpoint reference; this document
focuses on the *testing workflow* itself.

## Fast mode (warped runs)

A refactoring run does not need real-time playback, and the sim is
fixed-step, so it can run faster than the wall clock:

```bash
scripts/run_e2e.sh --fast            # as many 60 Hz steps as the CPU can do
scripts/run_e2e.sh --warp 4          # exactly 4x (for a visible, sped-up run)
scripts/run_e2e.sh --fast --no-godot # CI-shaped: no window at all
```

The switch is the host's (`DrivingGame.Server --warp N` / `--fast`); it
announces itself in `/state` as `time_warp`. Nothing about the physics
changes - the step stays 1/60 s, the host just runs more of them per
wall second - and the guardrails follow the same unit: every duration and
the teleport/jump bound are measured in **SIM time** (`/state.time`), so
"what could this speed cover in that time" stays a physical check at any
warp. **Default pacing is `--warp 4`** - `scripts/run_e2e.sh` adds it when
you pass neither `--warp` nor `--fast`/`--realtime`, because the suite's
guardrails are in SIM time and its poll resolution scales with the
announced warp, so a 4x run gives the same verdicts ~3x faster on the
wall clock (measured: 4m41s → 1m34s for the full suite). Two
consequences are deliberate:

- the **capacity probe** (`fig8_stress`, "how many cars can we run/render
  at max?") is skipped in a warped run - it measures real-time throughput,
  which a warped run breaks by construction. Run it without the switch
  (`--realtime`).
- a visible window at high warp is a sped-up playback, not a smooth one
  (the client renders 60 frames/s). Use `--warp 2..4` when watching, and
  `--fast --no-godot` when only the results matter.

## The e2e runner (`scripts/run_e2e.sh`)

The canonical runner owns the whole cycle: it kills stale processes (C# host
AND any old Python `src.main` - a stale Python server on :5001 silently
answers curls meant for .NET), closes any stale Godot window **cleanly**
(bumping `/tmp/driving_game_godot_gen`, never a signal), builds BOTH the
console host and the Godot project assembly (a Server-only build leaves the
window up EMPTY - "Cannot instantiate C# script"), imports Godot assets
headless (`.godot/imported` is gitignored, so a fresh clone would render
without signs and car sprites), starts the host on :5001, polls `/health`,
launches the visible window, starts the suite through `POST /run_test`,
streams that log to the console, and closes the window on exit.

```bash
scripts/run_e2e.sh --warp 4                         # the default: visible, ~4x
scripts/run_e2e.sh                                  # same (warp 4 is the default)
scripts/run_e2e.sh --realtime                       # real time (only when asked)
scripts/run_e2e.sh --no-godot --warp 4              # headless (CI only)
scripts/run_e2e.sh --tests corner_left_entry --warp 4   # one scenario by name
scripts/run_e2e.sh --tests 2 --warp 4               # ...or by its number
scripts/run_e2e.sh --tests 21-24 --warp 4           # a range (goes to the suite's
                                                     # own --tests parser)
scripts/run_e2e.sh --tests fig8_stress --stress-cars 250
scripts/run_e2e.sh --watch --center 1250 1750 --zoom 18   # the "I want to see it" mode
scripts/run_e2e.sh --focus-car 37                   # bind the camera to that car
CRASH_SHOT=/tmp/crash.png scripts/run_e2e.sh --tests 26   # the signed crossing
```

Runner flags (`scripts/run_e2e.sh`): `--no-godot`, `--tests <name|number|spec>`,
`--fast`, `--warp N`, `--realtime`, `--focus-car <uid>` (the client binds to
that car - e.g. the last crashed uid - at `CrashZoom` 34 px/m),
`--maximized` / `--watch` (see the two run modes below), `--zoom <px/m>`,
`--center <x> <y>` (metres - pins the camera, auto-follow off),
`--focus <name|role>`, `--map <name>`, and anything else, which is passed
through to the suite (e.g. `--stress-cars 250`). Env vars: `CRASH_SHOT`
(one PNG 0.5 s after the crash camera latches, diagnostic) and
`DG_FREEZE_STAGE=1` (set by `--watch`: stage every car while the sim is
frozen, then unfreeze).

**The two run modes** (binding user decision, see `AGENTS.md`) are both the
runner's, not a manual recipe: an e2e run is `--warp 4` in a normal window;
"I want to watch it" is `--watch` (window maximized via Godot's own
`--fullscreen`, sim frozen while the scenario stages its cars, every car
spawned at speed, unfrozen at the announced pace) - typically with
`--center`/`--zoom` on the hotspot or `--focus <name>` on a named car.

**Camera defaults** (user decision 2026-10-18): the window follows the car
at `--zoom 14` px/m so every maneuver is actually visible. Crossing
scenarios (`fig8_xing`, `fig8_xing_plain`) instead PIN the camera on the
crossing (`--center <mean of the loop's nodes> --zoom 26`) - following one
car among 50 shows less, and the crash is in the box.

**On failure the host AND the window stay up** so the frozen crash scene can
be analysed (click a car, read its decision window, `GET /state`); stop the
host later with `pkill -f DrivingGame.Server`. The suite itself freezes the
sim on a crash and leaves the cars in place.

## Current baseline (the one place a number is written)

> **Baseline 2026-10-31 (evening), `--warp 4`, parking lineage branch
`fix-39-the-reverse-solver-refuses-a-bay-on-an` at `03dd955` + docs, supersedes the block
below it.**
- C# unit suite: **165 passed / 0 failed / 2 skipped** (`dotnet test`, includes main
  `ec9e412`). All four bay rungs are green and named in the output:
  `BayReverseInTests(clearanceM: 2.5 / 1.5 / 1 / 0.5)` plus
  `ABayWithNoNeighboursIsStillParkedIn`. Passing that ladder requires zero contacts, no
  `GAVE UP` in the car's own decision record, hazard off, and a recorded verdict (the
  tightened instrument rejects an empty notice).
  Causal check: `PARK_SHIMMY_TRIES=0` makes exactly one test red,
  `BayReverseInTests(clearanceM: 1.5)` - the forward unwind in
  `UpdateReversePark` is what closes that bay, so the row can fail and names itself.
- e2e suite: **4 of 29 failed**. `park_6lane_right_lane` is GREEN (its old row "reached
  the destination but never came to a clean stop", nose -2.29 m / perp 0.80 m, is gone);
  the bay variant at 2.5 m clearance is GREEN (right flank 6 cm / 10 cm from the kerb,
  limit 30 cm); the bay variants at 1.0 m and 0.5 m clearance FAIL at 4.9 m / 6.6 m off
  the kerb - tracked by issue 31, they end through the staging-gate claim rather than a
  tuck (issue 38/39); and `fig8_xing` / `fig8_xing_plain` remain the two pre-existing
  out-of-scope failures from the baseline below.
- CI is green for the same tree (`core build + unit tests`, `godot client build`): the
  deliberately red instrument row that blocked merging all day no longer exists.

> **Baseline 2026-10-31, `--warp 4`, branch `fix-36-a-car-reversing-into-a-bay-is-held`
> at `59eb1a2` (`main` = `ee5e81f`); log `tests/run_test_T_355_0_20261031_131638.log`.**
> This is the current source of the suite's numbers, recorded to make "no worse"
> a fact for the parking work (issues 36, 31, 37). The 2026-10-23 block under it is
> the superseded history - keep it for the lineage of the numbers, not as the gate.

- **27 numbered scenarios** run + 1 skipped (`fig8_stress`, the capacity probe,
  skipped in any warped run by design): **24 passed / 3 failed**.
- Named failures:

  | scenario | how it failed | sim s |
  |---|---|---|
  | `park_6lane_right_lane` | `reached the destination but never came to a clean stop` — the state trace is correct (`driving → parking → parked`) but it stops `nose -2.29 m off the mark (perp 0.80 m)`, outside the ±0.50 m bumper touch | 30.0 |
  | `fig8_xing` | `unexplained centreline crossing at t=8.8s (lane_guard_errors 1 -> 2)` — an R33 lane-guard hit, not a contact | 8.8 |
  | `fig8_xing_plain` | `crash at t=9.6s: car 103 contacted car None` | 9.6 |

  `slow_pass_priority`, red here since 2026-10-23, is **green** again.
- C# unit suite: **160 passed / 3 failed / 2 skipped** (`dotnet test
  DrivingGame.Sim.Tests/`, ~30 s). The three failures are the bay-clearance ladder
  of issue 36 — `BayReverseInTests.ACarReversesIntoABayBetweenTwoParkedCars` at
  clearance **1.5 m** (gives up honestly: 16.5° off the kerb, hazard on), **1.0 m**
  and **0.5 m** (never complete). The same theory **passes at 2.5 m** (accepted at
  4.2°) and **with no neighbours**, and **no test reports contacting a parked
  neighbour any more**. They stay red on purpose: they are the instrument, and
  "never merge a red behaviour change" does not mean "delete the thermometer".
- Skips unchanged: `CrossingStressTests.CrossingStress_NoContact_Within_15s`,
  `RacelineReferenceTests.RebaselineReferenceFile`.
- **Not reproducible bit-for-bit — read before blaming your change.** The stress
  scenarios move with the warp factor because command arrival moves with it (the
  crossing has been seen at 8.7 s and 8.8 s, the contact at 9.6 s). One further
  member is genuinely **flaky**: the *negative* `hairpin_exit` wrong-side detector
  (test 25) failed in two runs on 2026-10-31 and passed in the run recorded above.
  So the gate for a parking PR is a **set comparison**, not a count:
  1. no failure outside `{park_6lane_right_lane, fig8_xing, fig8_xing_plain}`
     plus the flaky `hairpin_exit` wrong-side;
  2. **no contact/crash verdict anywhere except `fig8_xing_plain`** — a parking
     scenario that touches a neighbour fails hard even if the set otherwise matches;
  3. `park_6lane_right_lane` is inside issue 36's scope, so it may *leave* the set;
     nothing else may enter or leave.
- Re-measure with the two commands above; a failed scenario leaves the host running
  for analysis (`pkill -f DrivingGame.Server` when done, never a signal to Godot).

> **Historical baseline 2026-10-23 (superseded by the block above), `--warp 4`, HEAD `c827710` + comment-only doc pass**
> (`tests/run_test_T_242_0_20261023_231042.log`). **That convention still stands** - the current
> numbers are in the block above, and every other document links to this section
> instead of restating them. Regenerate it from a full run's log; never edit the numbers
> by hand.

- **27 numbered scenarios**: 26 run + 1 skipped (`fig8_stress`, the capacity
  probe - skipped in any warped run by design).
- Verdict of that run: **23 passed / 3 failed of 26 run** (`returncode 1`).
- Named failures (three, all multi-car or long-hold scenarios - every single-
  car scenario 1-24 is green):
  | # | scenario | how it failed | sim s |
  |---|---|---|---|
  | 20 | `slow_pass_priority` | `timed out: never reached expected segment` - the fleet crawls at **1.7 m/s per car** and never completes the pass | 240.1 |
  | 26 | `fig8_xing` | `unexplained centreline crossing at t=9.1s (lane_guard_errors 1 -> 2)` - an R33 lane-guard hit, not a contact | 9.1 |
  | 27 | `fig8_xing_plain` | `crash at t=40.2s: car 86 contacted car None` - flow decayed 46→33→25→14 moving over the 40 s before it (`logs/T_242_crash_623s_car84_car86.json`) | 40.2 |
  So the suite is **3 known-red**, not green - the same three scenarios were
  red in the previous full run (`T_231`, 2026-10-22), with #27 failing
  differently there (the R29 lockdown valve despawned car 96 at t=15.1 s).
  Read the log's own footer for the verdict of any newer run.
- **Reproduced 2026-10-25** (`--warp 4`, `tests/run_test_T_258_0_20261025_140722.log`):
  the same three red, each with the same first reason — #20 timed out after
  240.1 s of sim, #26 the R33 lane-guard hit at 9.1 s, #27 a contact at 9.7 s
  (again the pair 100/95). That run sat on the client working tree of that day;
  re-running #26 and #27 **with those client changes reverted** (`--no-godot`,
  `T_259`, `T_260`) reproduced both — so the render-path work of 2026-10-25
  changed no verdict.
- **Reproduced 2026-10-24** (`--warp 4`, first run whose tape carries the
  `heldBy` column — `tests/run_test_T_249_0_20261024_204743.log`): the same
  three red, same first-reason for each (20 timed out, 26 the R33 hit at
  t=9.1 s, 27 a contact — 9.8 s this time vs 40.2 s there, same pair 100/95),
  and the same `❌ WRONG-SIDE` marker inside #24 that every full run since
  T_231 prints without counting it as a failed test.
- C# unit suite: **102 passed / 2 skipped** (`dotnet test DrivingGame.Sim.Tests/`,
  ~23 s; 98/2 before the map reference widened, 93/2 before the tape generation
  of 2026-10-25 put `level` on the frames, 91/2 before the tape-generation tests
  of 2026-10-24) - skipped are
  `CrossingStressTests.CrossingStress_NoContact_Within_15s`
  (the crossing bug in unit-test form) and
  `RacelineReferenceTests.RebaselineReferenceFile` (a manual re-baseline tool).
- Build: `dotnet build DrivingGame.csproj --no-incremental` → **0 errors**.
  Quote the warnings as "pre-existing nullable-annotation noise (CS86xx) + one
  CA2255", never as a fixed count: a full rebuild of the Godot project prints
  25, of the console host 5, and an *incremental* build prints 0 because
  nothing recompiles.

## Capacity benchmark (the sim's own throughput)

This measures the SIM, not the client, so it lives on the C# side: the console
host runs it in-process, no REST API, no Godot, no HTTP round-trips. It
replaces `tools/capacity_sweep.py`, which measured the same thing over the
wire and paid the client's cost on top.

```bash
DrivingGame.Server --map basic --bench 25,50,75,100,150
DrivingGame.Server --map basic --bench --bench-loop xingp_n   # another loop
```

Per phase it builds a fresh engine, puts N cars evenly around the loop it
finds by node-name prefix (`fig8_n` by default - the benchmark prints which
loop and how many segments it got), holds the throttle, warms up 8 s of sim
so the raceline cache builds, then runs as many fixed 60 Hz steps as the CPU
can do for 25 s of sim time and reports `pace = sim time / wall time`.
`pace >= 0.97` counts as holding real time; below that the sim runs in slow
motion (the accumulator caps substeps, so it degrades instead of leaping).

Read the number as **one core's speed**: the sim is single-threaded, so this
is not the machine's capacity. Measured on this machine (2026-10-18, all cars
on the `fig8_n` loop): real time holds to ~50 cars; 150 cars runs at 0.33x.
With the Godot window open in the same process (embedded mode) the same
scenario holds to ~45 cars - the renderer costs about 10% of the budget.

## 1. Start the game with the API enabled

```bash
dotnet run --project DrivingGame.Server -- --map basic
    # console host, REST API on :5001
dotnet run --project DrivingGame.Server -- --map basic --warp 4
    # warped pacing (see Fast mode above)
dotnet run --project DrivingGame.Server -- --map basic --fast --no-collisions
    # CI pacing, collisions off
```

`DrivingGame.Server` is a project, not a command on `PATH` - the built binary
only exists under `DrivingGame.Server/bin/Debug/net9.0/` (that is the path
`scripts/run_e2e.sh` launches after its own build step).

- `--map basic` loads the synthetic **test map**
  (`DrivingGame.Sim/TestMaps.cs`) - a small, fully known/deterministic road
  network purpose-built for testing every kind of turn/junction (see "The
  test map" below). Without `--map`, the game loads real OSM data instead
  (from `data/osm_cache/`), which is fine for manual play but has no fixed,
  named start points.
- The API is always on: the console host embeds `DrivingGame.Api` on
  `http://127.0.0.1:5001` (**not** 5000 - macOS AirPlay Receiver squats
  there). Other host flags: `--start <name>`, `--port <p>`, `--smoke <frames>`,
  `--bench [phases]` / `--bench-loop <prefix>`, `--measure-sleep`.
- The sim is **always headless** - there is no pygame anywhere in the
  package. The 60 Hz loop paces itself with a hybrid sleep+spin (`.NET`
  `Thread.Sleep(16.7 ms)` overshoots ~3.3 ms on this Mac, measured with
  `--measure-sleep`; the spin window is 4 ms, the smallest that stays on
  target). All visualization lives in the Godot client, which reads state
  either over HTTP (`--source http`, default) or IN-PROCESS
  (`--source embedded`, see §1a below). A test script hitting the API is
  remote-controlling the same live sim the window renders.

### 1a. Watching a run

**Just want a window? `scripts/open.sh`.** It opens the viewer with an embedded
sim (API on :5001), keeps it open, and closes it again with `scripts/open.sh
--close` - clean self-quit through the generation file, never a signal. Flags:
`--map`, `--zoom`, `--port`, `--external` (attach to a host you started),
`--review <tape>` (the reviewer instead). It refuses to double-open ("already
open (pid N)") and refuses a port something else already answers - the two ways
a hand-launched client silently misbehaves.

For a test run, prefer the runner (`scripts/run_e2e.sh`) - it starts host +
window together and closes both cleanly. The manual pair, if you want a
long-running session to poke at by hand:

```bash
# terminal 1 - console host
dotnet run --project DrivingGame.Server -- --map basic --warp 4
# terminal 2 - Godot client, HTTP mode (default)
"/Applications/Godot_mono.app/Contents/MacOS/Godot" --path . -- --zoom 14
# or one process: sim embedded in the client + embedded REST API
"/Applications/Godot_mono.app/Contents/MacOS/Godot" --path . -- \
      --source embedded --map basic --port 5001 --zoom 14
```

The window shows the map (roads as their own geometry, markings, halt lines,
signs), the car (sprite, blinkers, wheel breadcrumbs), the test flags, the
HUD label and the minimap - and follows the car unless `--center` pins it.

### 1b. Where the window opens (it remembers)

The viewer **opens where it was last closed** - position, size and the monitor
it sat on. Godot does not do this for you, so the client implements the
[docs' own recipe](https://docs.godotengine.org/en/stable/tutorials/ui/creating_applications.html#remembering-window-position-and-size-across-sessions):
`MapRenderer.RestoreWindowPlacement()` reads `user://window.ini`
(`~/Library/Application Support/Godot/app_userdata/DrivingGame/window.ini`) and
states the rect **once, in `_Ready`, before the first frame is drawn** - which
is the whole point: the old path opened the window at the project default and
grew/moved it from a timer a second later, and the user watched that jump
(binding decision 2026-10-25: *"kein Resize, sondern direkt"*).
`SaveWindowPlacement()` writes it back on `_ExitTree` **and** every 0.5 s
whenever the rect changed, because the runner closes this client from outside
and an exit-path-only save would lose the last drag. Every write logs one line
(`WINDOW-PLACEMENT saved #n: pos … size …`), so "did my move reach the file?" is
answerable from the log instead of by theory - a hand-drag that was on screen
for half a second IS in the file, a hard kill inside that half second is not.

```
WINDOW-PLACEMENT saved #3: pos (1252, 484) size (3754, 2168) screen 2 -> user://window.ini
WINDOW-PLACEMENT remembered: asked pos (1252, 484) size (3754, 2168) -> got (1252, 484) (3754, 2168) screen 2
```
The word after `WINDOW-PLACEMENT` names the AUTHORITY that ruled this start -
`remembered`, `default`, or `--window` - which is what tells a wrong rect apart
from a right rect that merely looks like the default. Measured 2026-10-25: a
rect produced by hand (1252, 484 · 3754x2168) was restored byte-exact by the
next run (screenshot 3754x2168).

Three ways to say "not there":

| what | effect |
|---|---|
| drag or resize the window, then close | the next start opens at that rect (this is the normal way to aim it) |
| `--window <x> <y> <w> <h>` | one run at an explicit rect (scripted shots); it becomes the new memory |
| `--window-default` | ignore the memory once → 2000x1000 at (100, 100) |

A stored rect that no longer overlaps any connected screen by at least
200x120 px is dropped with a log line (an unplugged monitor must not leave the
title bar - the only handle to drag it back - out of reach), and `run_e2e.sh
--watch` therefore passes **no** geometry at all; `DG_WINDOW_RECT="x y w h"`
overrides it for one run. Measured on 2026-10-25: explicit `--window 300 250
1200 800` opened at exactly that rect (screenshot 1200x800) and the next run
with no argument restored it in one step.

## 2. Run the test suite against it

In a second terminal (the game keeps running in the first):

```bash
python tests/test_turning.py
```

This is the main test script. It:
1. Confirms the API is reachable (`GET /health`).
2. Fetches the map's named start points (`GET /start_points`).
3. For each test scenario: teleports a fresh car to a known start point
   (`POST /teleport {"start_point": ..., "progress": 0.8, "speed": ...}`)
   - in the LAST 20% of the start segment, laterally in the middle of the
   rightmost lane, already rolling at 50 km/h (the running turn-test
   protocol, see §3) - arms a turn signal or none (`POST /control
   {"blinker_left"/"blinker_right": true}`), holds the accelerator down
   (`POST /control {"accelerate": true}`), and then polls `GET /state`
   every ~50ms until the car crosses the red end flag (first 20% of the
   expected end segment) or the monitor window expires, watching for:
   - **off-road violations** (`state['on_road'] == False`)
   - **wrong-side driving** (`state['wrong_side'] == True` - the car is on
     the oncoming half of a two-way road; fails the scenario immediately,
     see §3)
   - **instant heading snaps** (>30° heading change in one frame - a
     physics bug, not a real turn)
   - whether the car actually changed to the expected next road segment
4. Prints a pass/fail summary.

### Selecting scenarios

```bash
python tests/test_turning.py --tests 3            # one, by number
python tests/test_turning.py --tests 3,7-9        # several, or a range
python tests/test_turning.py --tests y_from_stem  # every scenario there
python tests/test_turning.py --failed             # whatever failed last run
python tests/test_turning.py --failfast           # stop at the first failure
python tests/test_turning.py --list               # the numbered scenario list
python tests/test_turning.py --list-json          # ...as JSON (GET /tests uses this)
```

Numbering is preserved when filtering, so "TEST 7/27" always identifies the
same scenario - the point being to jump straight back to one that failed.
The selector must match the start-point name EXACTLY (`corner_left_entry`,
not `corner_left`); an unknown token exits 2 and prints the available list.

### 2a. The scenario list (generated, do not retype)

The suite owns the numbering; `--list` is the only honest copy of it.
Regenerate this table with `.venv/bin/python tests/test_turning.py --list`
whenever a scenario is added, dropped or re-ordered - the count that used to
live in prose here rotted three times.

| # | start point | dir | monitor window | scenario |
|---|---|---|---|---|
| 1 | `corner_right_entry` | right | 60 s | sharp 90° right corner |
| 2 | `corner_left_entry` | left | 60 s | sharp 90° left corner |
| 3 | `tjunction_from_top` | left | 60 s | T-junction, turning left off the stem |
| 4 | `tjunction_from_top` | right | 60 s | T-junction, turning right off the stem |
| 5 | `tjunction_from_top` | left | 60 s | same turn WITH an oncoming car on the bar |
| 6 | `tjunction_from_top` | right | 60 s | same turn WITH an oncoming car on the bar |
| 7 | `y_from_stem` | left | 60 s | Y-intersection, left fork (south-east arm) |
| 8 | `y_from_stem` | right | 60 s | Y-intersection, right fork (south-west arm) |
| 9 | `crossroads_from_north` | left | 60 s | 4-way crossroads, turning left |
| 10 | `crossroads_from_north` | right | 60 s | 4-way crossroads, turning right |
| 11 | `crossroads_from_north` | straight | 60 s | 4-way crossroads, straight through |
| 12 | `oneway_entry` | straight | 60 s | entering a one-way legally |
| 13 | `mixed_from_west` | straight | 60 s | one-way into a two-way 4-way, straight |
| 14 | `mixed_from_east` | straight | 60 s | two-way into a one-way exit, straight |
| 15 | `s_curve` | straight | 60 s | zig-zag S-curve |
| 16 | `hairpin_entry` | straight | 60 s | hairpin bend, entry side |
| 17 | `park_block_overtake` | straight | 60 s | drive around a parked car (R32) |
| 18 | `park_block_priority` | straight | 60 s | blocked lane + oncoming car (R26/R32) |
| 19 | `slow_pass_overtake` | straight | 60 s | overtake a 10 km/h crawler (R32/R36) |
| 20 | `slow_pass_priority` | straight | 60 s | crawler + oncoming: wait, then pass |
| 21 | `hairpin_exit` | straight | 60 s | hairpin bend, exit side |
| 22 | `roundabout_from_north` | right | 60 s | roundabout, first exit (west) |
| 23 | `park_6lane_right_lane` | straight | **45 s** | back-in park from the right lane |
| 24 | `hairpin_exit` | straight | 60 s | **NEGATIVE** - must go wrong-side (§3) |
| 25 | `fig8_stress` | straight | **170 s** | capacity probe, 51 cars (**skipped when warped**) |
| 26 | `fig8_xing` | straight | 60 s | 50-car crossing WITH signs |
| 27 | `fig8_xing_plain` | straight | 60 s | 50-car crossing, right-before-left |

Test maps do NOT auto-spawn a car: the sim starts with no car
(`GET /state` reports `has_car: false`) and the suite's setup teleport
creates the car at the scenario's start point before any driving. An
explicit `--start <name>` still places a car at that named point (e.g.
for manual observation); real OSM data keeps its random spawn.

### Running a single scenario

Debugging one specific case is much faster than the whole suite:

```bash
python tests/test_turning.py --only corner_right_entry right 80
python tests/test_turning.py --only corner_right_entry right 80 6   # + destination
python tests/test_turning.py --only corner_right_entry right 80 6 0 # + spawn km/h
```

`--only <start_point> <direction> <speed_kmh> [end_segment] [spawn_kmh]` teleports
straight to that one start point, arms that one turn direction (or
`straight`), and monitors just that one turn. `speed_kmh` is only "how
fast to get up to before we start watching" (the driver always tries to
accelerate to top speed and brakes only as needed for the upcoming turn -
see the speed profile in `DrivingGame.Sim/BicycleNav.part2.cs`, which derives
cornering speed from the curvature of the driving line); it does not force a
specific cruising or cornering speed. `spawn_kmh` overrides the rolling-start
speed (pass `0` to start from a standstill).

The optional `[end_segment]` sets the red end flag at that segment's FIRST
20% point as a VISUAL-ONLY marker (`red_nav: false`) - the car drives
through it and the run ends on the crossing, exactly like the full
suite's running turn tests. Without it the nav has no destination at all:
the car just drives through. To watch a parking maneuver live in the
Godot window, run one of the parking-offset scenarios from the suite instead
(`python tests/test_turning.py --tests park_6lane_right_lane`): only those
set the flag as the nav's destination and trigger the full parking
approach (including reverse-in where the geometry calls for it).

### Starting a scenario from the window (there is no cockpit any more)

The pygame "cockpit controller" (`tools/controller.py`) was retired with the
pygame frontend. What replaces its two uses:

- **Run a scenario and watch it** - the runner: `scripts/run_e2e.sh --tests <n>
  --watch` (window maximized, camera pinned, cars staged while frozen). The
  client can also launch one itself: `--run-test <number>` posts
  `POST /run_test` once the map is up, and `--quit-on-test-done` closes the
  window when the run finishes (that is how the runner closes it without ever
  sending a signal).
- **Drive by hand** - what `_Input` in `MapRenderer.cs` binds today (checked
  2026-10-25, one `Key.` branch per meaning): **arrow keys / WASD** steer
  (`Key.A/D/W/S` + arrows into the drive vector), **`Tab`** cycles the camera
  binding, **`F`** toggles follow, **`T`** hides/shows the test window, mouse
  drag pans, wheel zooms. The pygame-era keys the earlier version of this list
  claimed (`Q`/`E` blinker, `H` hazard, `T` nav-mode, `+`/`-`, `C`) have **no
  handler** in the client - drive commands go through the same
  `POST /control` channel the suite uses, which is the way to send them.
- **Read a finished car** - click it: the inspector window shows speed, pose,
  nav state and the rule log from `GET /car/{uid}/decisions`.

`python tests/test_turning.py` against a running host remains the headless/CI
way to run the suite.

### Random-location mode

```bash
python tests/test_turning.py --random
```

Teleports to random points on whichever map is actually loaded (works
against real OSM data too, unlike the deterministic named-start-point
suite) and repeats the same monitoring at a few speeds. Useful as a
broader smoke test once the deterministic suite passes.

## 3. Pass criteria (deterministic scenarios)

A deterministic scenario is a tuple `(start_point, direction, target_speed,
expected_end_segment, duration)` from `DETERMINISTIC_TESTS`. There are two
protocols:

**Running turn tests** (every scenario except the parking-offset ones):
the intent is to see whether the car does the TURN correctly - nothing
else. The runner teleports a fresh car into the LAST 20% of the start
segment (progress `TURN_SPAWN_PROGRESS` = 0.8), laterally in the middle of
the rightmost lane, already ROLLING at `RUNNING_START_KMH` (50 km/h) - no
standstill-to-cruise phase. The red end flag marks the FIRST 20% of
`expected_end_segment` (`END_FLAG_PROGRESS`) and is **VISUAL ONLY**: the
`POST /flags` payload carries `red_nav: false`, so the flag does NOT become
the nav's destination - no parking plan, no stop. The test ends when the
car passes the flag, AT ANY SPEED, and the car may keep driving on at full
or intended speed past it. (Binding user decision 2026-10-25, correcting an
earlier same-day proposal that every car must stop at the red flag: what a
scenario tests is its OWN manoeuvre - driving through the curve, not whether
the car can park; parking is tested by the dedicated parking scenarios. A
red-flag stop would also make the nav stage a parking manoeuvre inside a
turn test, which the user explicitly does not want. Exception stated by the
same decision: ONLY the parking scenario requires a complete standstill AT
the red flag.) Decision 2026-08-27 ("tests must be fast, parking is covered
by the dedicated parking scenarios - not bolted onto every turn") says the
same thing and stays the anchor for this.

**Parking-offset scenario** (`kerb_check` = true, exactly ONE scenario today:
**#23 `park_6lane_right_lane`**; DRIVING_MANEUVERS.md §1 "Variant: Dead-end /
route end"): the only scenario that still parks. The runner spawns the car at
15% of the segment in the lateral position baked into that named start point
(always on our own side of the road), and the red flag at 50% is the nav's
DESTINATION (`red_nav: true`): the car must
execute the full parking approach (parking ramp + kerb drift, plus reverse-in
where the geometry calls for it) and end with BOTH right-hand wheels within
`KERB_PASS_MAX_M` (30 cm) of the right kerb - i.e. INSIDE the parking lane.
(The two other lateral starts on the parking avenue - `park_6lane_left_lane`
and `park_6lane_parking` - are still *named start points* on the map, but
their scenarios were dropped by user decision 2026-10-19; #18-#20 are
right-of-way scenarios, not parking ones.)

A scenario **passes** only if ALL of these hold:

1. **Arrival at the red flag - ONE place, ONE event** (binding user decision
   2026-10-25, agreed after measuring the old rule: the pennant was drawn ~7 m
   off the centreline, the suite tested a 3 m circle around a CENTRELINE point,
   and the car drove 1.75 m off that point - so the real along-window was only
   ±2.4 m, and on the 19.4 m avenue (`pkw_n→pkw_s`, 2 lanes, lateral 7.27 m) the
   circle could never be reached at all. A radius around a point in a ~2.6 m
   lane also reaches into the next lane, which is exactly what the user rejected.)
   The **sim states the mark**: `flags.red` is the pennant's FOOT in the line the
   car must arrive in - `Config.LaneBaseOffsetM` (the driving line) for a running
   test, `Config.ParkOffsetM` (the parked car's front-bumper line) for the
   parking scenario - and `flags.red_seg`/`flags.red_prog` carry the along
   position on the route (metres, `"units": "meters"`). The **client draws the
   pennant exactly there** (`MapRenderer.DrawFlag` puts the pole's foot at the
   point; the cloth leans right, the mark stays). The **arrival event is the
   car's body crossing the mark's transverse line**: the front-most corner of
   `body_corners` projected onto the mark's route direction passes zero; the one
   extra condition is that the car is inside the mark's lane band
   (`FLAG_LANE_HALF_TOL_M` = 2 m perpendicular), so a car on a different road
   cannot fire it. No radius enters the criterion.
   - **Running tests:** the crossing ENDS the test at any speed - the car drove
     against the flag; it may keep rolling on at full or intended speed.
   - **The parking scenario (#23, reverse-in ordered):** the car first drives
     PAST the flag to stage the back-in, sets back, then pulls forward until its
     **nose touches the mark** within `NOSE_TOUCH_M` (0.5 m) at rest. The 8 m
     `STOP_AT_FLAG_TOLERANCE_M` only decides WHO is at the flag at all; the kerb
     verdict (`KERB_PASS_MAX_M`, 30 cm) still applies.

   **Reverse-in exception.** When the nav plans a back-in park (`/state`
   reports `parking.style == "reverse"`), the car deliberately drives PAST
   the flag: it stages the manoeuvre by stopping several metres beyond it,
   then reverses into the spot at the kerb (DRIVING_MANEUVERS.md §1b). While
   that plan is active and not yet complete, crossing the flag is NOT a
   "drove past" failure, and the staging stop does NOT count as an arrival:
   the run only passes once `/state` reports `parking.parked == true` with
   the car at rest at the flag. The suite reads this from the `parking`
   field of `GET /state` (`{style, phase, parked, reversing}`, sourced from
   the nav in `DrivingGame.Sim/BicycleNav.part1.cs`, exported by
   `SimEngine.UpdateState`) - note it must be read from the nav object,
   not the driver, which does not carry these attributes.
2. **Correct route.** The car ends on exactly `expected_end_segment` (a left
   turn that actually went right fails even if it "arrives" somewhere).
3. **No off-road / no wrong-side.** No validator violations (the car must
   stay on the carriageway), and no crossing of the centerline into the
   oncoming lane. The game only WARNS on a wrong-side hit (once per
   crossing) and reports it via `GET /state` (`wrong_side` true on every
   affected frame + cumulative `lane_guard_stats`); failing the run is the
   suite's job: it fails the scenario immediately when a poll sees
   `wrong_side`, with the lane-guard frame counter (delta over the
   scenario) as backstop for hits between polls. The guard itself stays
   silent where crossing the centerline is INTENDED by the maneuver:
   active turn blend zones and on-site U-turns (DRIVING_MANEUVERS.md §5:
   crossing the Mittellinie is "erlaubt und erwartet"). A scenario that
   must cross for a reason (e.g. passing a parked car on a narrow road)
   needs an explicit per-scenario opt-out - not the default.

   **Negative detector test.** Scenario **#24** is a negative test: it
   replays the old 80%-spawn `hairpin_exit` config,
   where the car provably slides into the oncoming lane at the tip of the
   V (too close + too fast to make the ~166° turn). It PASSES when the
   wrong-side detection fires and FAILS if the car stays in its own lane -
   it proves the detector works. It is **not** the last scenario: #25
   `fig8_stress`, #26 `fig8_xing` and #27 `fig8_xing_plain` were appended
   after it, so a full run ends with the three multi-car scenarios and a
   green #24 says nothing about them.
4. **No instant heading snaps.** ≤ 30° per frame (physically impossible
   rotation).
5. **No teleport/jump.** Movement between polls is physically plausible:
   distance ≤ speed × time + margin.
6. **Game stays alive.** The API answers throughout the scenario.
7. **Within the window.** Arrival within the scenario's monitor window - the
   5th field of each `DETERMINISTIC_TESTS` tuple: **60 s** for all 23 turn
   scenarios, **45 s** for `park_6lane_right_lane`, **170 s** for
   `fig8_stress` (whose phases are `STRESS_PHASE_SECONDS` = 30 s of sim time
   each). Passing
   tests exit on arrival, so the window only bounds how long a FAILING run
   takes - which is why a `slow_pass_priority` timeout costs the full 60 s.

A run aborted from outside (Ctrl-C on the suite, or the runner tearing the
host down) is neither pass nor fail and is not persisted to
`tests/turning_results.json`.

For parking tests, criterion 1 is strict on purpose, and it is satisfied by
the CAR itself: the red flag is the nav's destination
(`BicycleNav.set_destination`), so the car parks at the flag with the
documented parking approach (parking ramp + kerb drift + stop). The
runner's anticipatory brake latch is only a safety net for when that
approach fails. If the car crosses the flag anyway, that is a real driving
defect - e.g. the nav ignoring the hard brake while in pull-over mode (this
is exactly what used to happen on `s_curve`, which parked at the dead end
beyond the flag) - not a test artifact.

**The standstill after the flag is a period, not a second criterion** (binding
user decision 2026-10-25: *"es wäre schön, wenn sie nach dem Überfahren der
roten Flagge dann auch bremsen würden bis zum Stillstand, damit man sieht, der
Test ist fertig"*). Passing the mark at speed still ENDS the test - unchanged.
What was added is `_stop_after_verdict()` in `tests/test_turning.py`, called at
the very end of `monitor_turn`, **after** `POST /test_result` and after the
result is appended and saved, so it can clarify the picture but never move a
verdict. It latches `brake` through `POST /control` (a real deceleration -
never a written zero, AGENTS.md "never do physically impossible things") and
waits for `speed_kmh < 0.3`, up to 8 s:

```
   ✅ Reached the end flag - the car's front is 0.58 m past the pennant's foot on segment 9
   ✅ PASSED
   🛑 test over - braking to a standstill from 43 km/h so the picture says it ended
      stands still after 3.4 s (sim 12.7 s) - brake stays latched        [real time; 0.9 s at --warp 4]
```

Two things it deliberately does not do: it **skips a field with more than one
car** (saying so - `🛑 skipped the stop: 3 cars on the field`), because standing
still in traffic would drop a sudden obstacle in front of a following car and
put a crash the scenario never tested into the tape; and it **does not re-drive
a car that already stands** (the parking scenarios end at rest anyway). The
latched brake is cleared by the next scenario's `POST /reset`.

## 4. The test map (`DrivingGame.Sim/TestMaps.cs`)

`TestMaps.BuildBasicTestMap()` builds a synthetic grid of ~500 m tiles
(`TILE = 500.0` m, map bounds (0,0)-(3000,1950) m), one deliberately named
road situation per tile:

| Tile | Situation | Named start points |
|------|-----------|--------------------|
| (0,0) | straight road | `straight`, `straight_reverse` |
| (1,0) | 90° right corner | `corner_right_entry`, `corner_right_exit` |
| (2,0) | 90° left corner | `corner_left_entry`, `corner_left_exit` |
| (3,0) | T-junction | `tjunction_from_top/_west/_east` |
| (0,1) | Y-intersection | `y_from_stem/_sw/_se` |
| (1,1) | 4-way crossroads | `crossroads_from_north/_south/_west/_east` |
| (2,1) | one-way street through a 4-way | `oneway_entry`, `oneway_wrong_way`, `oneway_cross_from_*` |
| (3,1) | S-curve (degree-2 bends) | `s_curve`, `s_curve_reverse` |
| (0,2) | dead end | `dead_end_approach` |
| (1,2) | tight hairpin (~150°) | `hairpin_entry`, `hairpin_exit` |
| (2,2) | wide sweeping curve | `sweeping_curve`, `sweeping_curve_reverse` |
| (3,2) | roundabout (64-node one-way ring, CCW, 4 spokes) | `roundabout_from_north/_east/_south/_west` |
| (0,3) | sliver junction (4.16 m stub, the real-world 815→1008 layout) | `sliver_approach`, `sliver_from_west/_east` |
| (4,0) | widths road (13/9/7/4 m, ANGLED transitions) | `widths` |
| (5,0) | WWW zig-zag (own tile since 2026-09-11 - it used to be crammed into (4,0) and wrecked the T-junction tile) | `www_entry`, `www_exit` |
| (4,1) | parking avenue: 19.4 m, 2 driving + 1 parking lane per side, solid centreline, P marks | `park_6lane_left_lane`, `park_6lane_right_lane`, `park_6lane_parking` |
| (4,2) | one-way INTO a 4-way | `mixed_from_west` |
| (4,3) | one-way OUT of a 4-way | `mixed_from_east` |
| (1,3) | figure-8, ONE loop crossing itself; the NE-SW branch is a BRIDGE (level 1) | `fig8`, `fig8_under`, `fig8_deck`, `fig8_bridge` |
| (2,3) | right-of-way figure-8: REAL degree-4 crossing WITH Vorfahrt signs (SE-NW Priority, SW-NE Yield) | `fig8_xing` |
| (3,3) | un-signed figure-8: REAL degree-4 crossing, right-before-left governs | `fig8_xing_plain` |

Every scenario has one or more **named start points**
(`MapBuilder.Start(name, nodeId, lateralOffsetM, facing)`) - a fixed
`(x, y, heading, segment, forward, lateral_offset_m)` tuple, which is what
makes `--only <start_point> ...` and the deterministic suite reproducible.
`facing` is needed on a closed loop (every node has two segments).
`lateralOffsetM` shifts the spawn laterally from the NORMAL driving position
(positive = toward the right kerb) - that is how the parking-offset
scenarios vary only the start line.

`GET /start_points` returns all of them:

```bash
curl -s http://127.0.0.1:5001/start_points | python3 -m json.tool
```

Road signs are map data too: `MapBuilder.Sign(fromNode, toNode, SignType.Yield|Priority)`
fills `RoadNetwork.Signs[(segIdx, nodeId)]`, exported by `GET /map` and obeyed
by `CarCollisions` (see DRIVING_MANEUVERS.md §8 R4/R5/R14).

## 5. Showing which test is running in the HUD

`tests/test_turning.py` also calls `POST /label` (see [REST_API.md](REST_API.md))
with the **current test number** (`"10/27"`), shown in the Godot window
- purely a visual aid so a human watching it can tell at a glance which
scenario is currently being driven.
Standalone runs (no suite context) fall back to the start point's map-tile
number: `START_POINT_NUMBER` in `test_turning.py` maps each start point
name to that number (1 = top-left tile of the minimap, counted left-to-right
then top-to-bottom - note this is the *visual* minimap position, which is
vertically flipped from the map's internal world-grid row; see the comment
above `START_POINT_NUMBER` for why).

**The test-info window (binding user decision 2026-10-25): "do we not have a
window that says which test runs, why, and how far along it is? If not, we
should have."** The number alone answers neither WHAT the window is showing nor
whether the goal has been reached, which is exactly what a watching human needs
to know - without it a finished test looks alive, because a running turn test
ends by the CAR PASSING the flag at speed and the sim is not stopped
(docs/TESTING.md §3 above).

The same day the user ruled on its shape: *"make it a window like the car detail
window - moveable, bigger fonts, more structured, not text-only"*. So it is a
**real OS window** (`BuildTestInfo` / `TestInfoUpdate` in `MapRenderer.cs`,
`Window` + `RegisterWindow`, the pattern the reviewer's readout uses), not text
painted into the map and not a fixed corner box: its title bar carries the
test's identity, it drags, and the shared `EnforceWindows` guard keeps it 5 px
inside the map window and clear of the minimap and the readout. The body is
structured - 24 px rows (the user's "die Fonts sind zu klein" of the same day,
up from 19) and a colour **chip** that answers without reading
(amber ▶ running, green ✓ passed, red ✗ failed):

```
title bar   test 2/27
            Taking a sharp 90° left corner        <- the scenario's own description
            watch: the car                        <- what the camera follows
            [✓] passed                            <- the chip: the glanceable state
            wall 9.2 s · crossings 0 · off-road 0 <- the verdict's numbers
```

Same day, second ruling: *"integriere das Test-Panel unten mit in das
Test-Fenster, das die Ergebnisse anzeigt"* - so the run controls that used to
float bottom-left **on the map** (`Test-Nr:` field, `Run Test` button, the
run-status line) live in this window too, under the verdict they belong to:

```
title bar   test 2/27
            Taking a sharp 90° left corner
            watch: the car
            [✓] passed
            wall 9.2 s · crossings 0 · off-road 0
            [ 3 · T-junction: turning left off the stem ]  [Run]
            list: GET /tests · T hides/shows this window
            test #3 done ✅ (log: /Users/hauke/prj/driving-game/tests/ …
```

Consequences of putting them there, all deliberate:

- **one place per job**: start a scenario and read what it did in the same
  square centimetres; the old split (type bottom-left, read a window top-left)
  made you hunt for the verdict.
- **the window is always open** - it carries a control now, so it cannot wait
  for a first focus; with no test context its rows read `no test running` /
  `idle` behind a grey chip. `T` hides it, the next focus or `--run-test` brings
  it back; ✕ hides it too (same reason).
- **the map is the map again**: the row was positioned from the viewport size at
  build time, so a resize left it in the wrong corner; inside a window the
  layout is in the window's own coordinates.
- rows and the status line **elide with "…"** instead of running under the frame
  - and they need an explicit `Size` for that: a trim mode on an auto-sized
    label rendered the rows *completely empty* while `TEST-INFO` still printed
    their text (measured 2026-10-25 - the log said fine, the screenshot said
    nothing; only the screenshot is a picture).
- **starting a test from inside the window** is the dropdown: it holds every
  scenario as `<number> · <description>` straight from `GET /tests` (so the
  window can never disagree with the generated list in §2a), and `Run` beside it
  starts the selected one. `--run-test <n>` fires the same entry point
  (`StartTestNumber`) and moves the dropdown to that number; a click that
  arrives while a request is in flight queues (`_clickPendingNum`) instead of
  being lost. While the list has not arrived yet the button says so rather than
  doing nothing - an empty dropdown must read as "not loaded", not as "no
  tests". A client that starts before its host retries `GET /tests` up to 8
  times, 2 s apart, and names the code if it stays unavailable.  is attached to: the client posts to `RunTestUrlOf()` (its own host, from the
  resolved `_apiPort`), and the host hands the suite a `CAR_API_URL` pointing at
  the address the request arrived on. Before that, `RunTestUrl` was hardcoded to
  `:5001` and an embedded session on another port answered `Fehler: ` with an
  empty body - because the host threw on `File.Open` of a `RunnerPath` that was
  guessed by counting four `..` (wrong depth for the Godot assembly), outside the
  try/catch that would have named the reason. Both fixed, both in
  `GameApi.ResolveRunnerPath()` and `POST /run_test`.


- **name + purpose**: `GET /state` already answers them - `focus.scenario`,
  `focus.label` and `focus.desc` (`SimEngine.cs:FocusPayload`, announced per
  test by the suite through `POST /focus`, recorded on the tape as a `# focus`
  note). No new channel: the window reads what the camera focus already carries.
- **state**: `test_results` in `GET /state` (`number`, `label`, `scenario`,
  `passed`, `reason`, `sim_s`, `wall_s`, `crossings`, `off_road` - the verdict
  the suite files through `POST /test_result`, also on the tape as a `# result`
  note). Until such an entry matches the CURRENT focus label the state row
  reads `running` behind the amber chip; when it matches, the chip turns green
  `✓ passed` or red `✗` with the reason and the numbers drop into the last row.
  A window that would keep saying `running` after the verdict is the
  lie this decision is about, so the match is on the label, not on position in
  the list.
- **what happens after the goal**: the suite proceeds to the next test on its
  own (`run_e2e.sh` runs the selector's list; the window follows the new focus),
  and for a single watched test the runner closes the viewer at the suite's end
  (`--quit-on-test-done`, in-app, no signal - see the never-signal rule). Only
  an explicit `--keep-window` keeps it open, and then the window's `✓ passed`
  row is what says the run is over while the car is allowed to drive on.
- **measured** (2026-10-25, `--tests 2 --watch --realtime`): the client logs
  `TEST-INFO '2/27': title=test 2/27 | purpose=Taking a sharp 90° left corner |
  watch: the car | state=▶ running` and, a poll later,
  `state=✓ passed | nums=wall 9.2 s · crossings 0 · off-road 0`; the screenshot
  of the doubled window shows the title bar, the four rows and the green chip.

## 6. Other test scripts and suites

- **C# unit tests** (`DrivingGame.Sim.Tests/`, headless xunit - run these
  first after a change):
  ```bash
  dotnet test DrivingGame.Sim.Tests/                    # 91 pass / 2 skip
  dotnet test DrivingGame.Sim.Tests/ --filter FullyQualifiedName~CollisionTests
  dotnet test DrivingGame.Sim.Tests/ --filter "FullyQualifiedName~CrossingStress"
  ```
  Suites: `RacelineReferenceTests` (the banded solver + corridor against the
  golden fixture `data/raceline_reference/basic.json`), `BicycleNavTests`
  (straight/corner/dead-end park/flag park/U-turn driven at 60 Hz),
  `SafetySystemsTests` (obstacle placement + alignment, contact stop,
  layout save/load, LaneGuard, PhysicsValidator), `CollisionTests`
  (follower stops behind a stationary lead, head-on in separate lanes nobody
  brakes, simultaneous crossing right-before-left), `CrossingStressFlutterTests`
  (decision rate stays human; the no-contact fact is the skipped known-red).
- **`tests/test_turning.py`** (live e2e, REST-driven) - the turning suite
  described above.
- **Manual visual QA scripts** (no pass/fail, see the section below):
  `tests/light_check.py`, `tests/car_line_check.py`.

Historical (Python original, retired): `tests/test_obstacles.py`,
`tests/test_road_network.py`, `tests/test_api.py` and
`scripts/verify_obstacles_live.py` lived in `/Users/hauke/prj/car`; their
equivalents here are the xunit suites above.

## 7. Typical debugging loop

This is the actual workflow used throughout this project's development:

1. Start the sim + Godot frontend (see "Watching a run" above)
2. Run one failing scenario: `python tests/test_turning.py --only <name> <direction> <speed>`
3. Watch the Godot window while it runs, and/or inspect the printed physics
   debug output (turn planning, off-road/teleportation watchdog messages,
   hazard announcements - see `PhysicsValidator`, `LaneGuard`,
   `BicycleNav` and `CarCollisions` in `DrivingGame.Sim/`).
4. If it fails, `GET /state` polled by hand (`curl http://127.0.0.1:5001/state`)
   is usually enough to pin down *where* and *at what exact frame* things
   went wrong - and `GET /car/{uid}/decisions` says WHY (the per-car rule log).
5. Fix the code, restart the game process (state like cached geometry
   is built once at startup, so a code change needs a fresh process),
   and re-run the same `--only` scenario before moving on to the full
   suite.

## 8. Parallel test runs (multiple cars)

The sim supports several cars at once (semantics in
[REST_API.md → "Multi-car"](REST_API.md)), so
scenarios can run in parallel instead of sequentially:

- `POST /teleport {"start_point": ..., "add": true}` spawns a car
  ALONGSIDE the existing ones (default without `add` is replace - what
  this sequential suite uses).
- Every per-car endpoint takes an optional `"uid"`: `/control`,
  `/flags`, `/label`, `/toggle` (omit = primary car, i.e. the most
  recently spawned one).
- Each car carries its own test flags + HUD label in `/state.cars[]`; the
  Godot frontend draws per-car pennants, labels and colors (red, blue,
  yellow, white by uid).
- `POST /cars {"action": "clear"}` removes all cars between rounds.
- Cars DO interact now: `CarCollisions` caps speeds and resolves contact
  (rollback + stop) per substep, so a parallel fleet is a right-of-way test,
  not just a rendering test. `--no-collisions` on the host turns the whole
  two-pass machinery off for isolation runs.

The sequential suite in §2 is unchanged: it teleports without `add`
(replace) and reads the top-level `/state` fields, which always describe
the primary car.

### The multi-car stress scenario (`fig8_stress`) — SKIPPED

`fig8_stress` is in the suite's `SKIPPED_TESTS` (2026-09-10 user decision):
the 51-car spawn placement is under review - the phase fails on off-road /
wrong-side before any crash, so it is out of the green baseline. The raw
`--only fig8_stress ...` path still runs it (that is the debug door while a
scenario is under review).

What it measures (unchanged): a performance probe, phases from
`STRESS_PHASE_CARS = (51,)` on the 723 m figure-8 loop, 30 s of SIM time
(`STRESS_PHASE_SECONDS`) each, polled at `STRESS_POLL_S` 0.1 s. Add
`--stress-cars N[,N...]` to run specific counts instead (e.g. `--stress-cars
288,576`). Cars are spawned evenly around the loop by ARC LENGTH
(`_arc_uniform_spawns` - the old "nearest-segment midpoint" quantization put
two cars on one midpoint when n_cars > len(loop)), alternating direction
(`reverse: true` for odd k), cycling through all seven vehicle classes, and
**the fleet is placed while the sim is FROZEN** (see the spawn discipline
below): freeze → place every car at rest (`speed: 0`) → hand out the
throttle → unfreeze. A rolling start put the first cars at speed before the
last one existed, so the whole fleet arrived at the crossing at once and
locked it. Between phases the map is cleared and waited-for-empty.

The phase duration is measured in sim time (the `/state` `time` field), NOT
wall clock: at high car counts the sim may fall behind real time (its
accumulator caps substeps -> slow motion, never a leap), and a wall-clock
window would then cover less driving. A 20x wall-clock hang guard ends a
wedged phase early.

Per phase it stores in `tests/turning_results.json` (key
`fig8_stress|<N>cars`) the **jitter sum** (`jitters`, "Anzahl Ruckler pro
Szenario") - position jumps beyond what the car's speed can cover between
polls, plus margin - and `worst_jump_m`. Pass criteria: all cars spawned,
no crash, no off-road. Wrong-side is reported but not failing (the lane guard
has no in-turn suppression on the junction-free figure-8, so even a perfect
single car trips it occasionally).

## The crossing-stress scenarios (`fig8_xing`, `fig8_xing_plain`)

50 cars (25 per direction, interleaved, all seven vehicle classes) on one
REAL degree-4 figure-8 crossing at grade, throttle held, run for up to
**60 s of SIM time** (user decision 2026-10-19: surviving one minute =
success; the old 300 s window ran "forever" on the wall clock) or until the
FIRST crash. Pass = the whole fleet survives that window with zero contact.

- Crash detection reads `crash_episodes` (monotonic per car), not the
  transient `in_contact` flag: a contact episode can clear inside one 0.1 s
  poll, and a measured 50-car run logged contacts at t=6.9/7.8/20.7 s while
  every poll read `in_contact: false` - the suite then called the first crash
  at 84.8 s.
- Every 10 s of sim time it prints `moving X/50` (cars faster than 3.6 km/h)
  - the gridlock indicator. A healthy run keeps a clear majority moving.
- `--trace <path>` dumps one JSONL line per `/state` poll of the crossing
  runners (full fleet pose/speed/braking/segment/progress), so a crash's
  APPROACH can be read at the poll's own resolution instead of only the
  post-impact decision dump in `logs/`.
- On a crash the suite FREEZES the sim and leaves the cars in place, so the
  Godot window shows the exact impact pose; the runner keeps host + window
  open for analysis (`--focus-car <uid>` re-watches the crashed car,
  `CRASH_SHOT=<png>` takes one PNG 0.5 s after the crash camera latches).

Baseline 2026-10-23 (`--warp 4`, see **Current baseline** above): **both
crossings are red** - `fig8_xing` dies at 9.1 s on an unexplained lane-guard
crossing, `fig8_xing_plain` after 40 s of flow decay on a contact.

**Measured history of these two scenarios** (the reason the criterion is 60 s
and the reason nobody should call them solved):

| date | both crossings | evidence |
|---|---|---|
| 2026-09-11 | crash in 8-16 s: signed `car 8 contacted car 32` at **7.6 s**; un-signed `car 18 contacted car 20` at **15.6 s** (moving already down to 32/50 at t=10 s) | first head-to-head baseline of the crossing experiment (`T_001`, `T_002`) |
| 2026-10-19 | **survive 60 s, zero contact** - `moving` 43→34→29→35→40 of 50 signed, 50→47→43→35→26 un-signed | the run that set today's pass criterion |
| 2026-10-22 (`T_231`) | red: #26 unexplained crossing 9.1 s; #27 the **R29 lockdown valve despawned car 96** at 15.1 s | `tests/run_test_T_231_…log` |
| 2026-10-23 (`T_242`) | red: #26 the same failure at the same 9.1 s; #27 a **contact at 40.2 s** after the flow decayed | `tests/run_test_T_242_…log`, `logs/T_242_crash_623s_car84_car86.json` |

The 2026-09 experiment that chased this (two model branches against one shared
criterion, `fix/fig8-crossing-opus48` vs `-qwen38`) ended without a head-to-head
verdict: both branches merged into `main` in pieces, so what survived is the
engineering (route-true corridor, R8 lateral yield, the widened R14 exit gate,
stop-line discipline, the frozen spawn discipline) rather than a comparison.
The 2026-10-19 success did not hold.

## Crash forensics

- `logs/<run-tag>_crash_<t>s_carNN_carMM.json` - written by `CrashDumper.cs`
  on a fresh contact: both cars' decision logs, poses (the PRE-step
  post-rollback pose) and impact speeds. Each car is dumped at most once.
- The run tag (`RunTag.cs`, `T_001`, `T_002`, …) is persisted in
  `logs/run_counter` because `scripts/run_e2e.sh` restarts the host on every
  run; the suite log (`tests/run_test_T_042_…`) and every crash dump of that
  run share the prefix. `T_000` = no run active (unit tests, manual drives).
- `GET /car/{uid}/decisions` returns the per-car rule log (one entry per
  CHANGE of the reason a car is held back, tagged `[R1]`…`[R25]`); the Godot
  client shows it in a click-to-inspect window under the minimap.

## Manual visual QA scripts (no pass/fail)

These drive the running game over the REST API and set up a situation for
a human to look at in the Godot window. They are NOT part of the automated
suites above.

```bash
python3 tests/light_check.py      # one stationary car, zoomed to its lights
python3 tests/car_line_check.py   # ALL vehicle types lined up on a road,
                                  # side by side (picks a long-enough segment
                                  # itself, verifies positions vs /state)
python3 tests/pose_stutter_check.py --ab
                                  # is the PAINTED motion smooth? traces the
                                  # per-frame node position in the live window
                                  # and in the reviewer at 1x on one tape, and
                                  # scores held / jump / speed-spread. --ab
                                  # re-runs the same tape with the play lerp
                                  # switched off, so before and after differ by
                                  # one flag (--no-play-lerp), not by two runs
```

## Notes / gotchas

- The game process must be restarted after any code change - there's no
  hot-reload; `--only` runs are fast specifically so this loop stays
  quick.
- `POST /reset` clears all control inputs (accelerate/brake/blinkers)
  back to `False`; `test_turning.py` calls this before every test.
- **`POST /freeze {"frozen": true}`** pauses the simulation. The crossing
  runners use it DELIBERATELY to place a fleet without moving it (see the
  spawn discipline above), and the suite freezes on a crash to hold the
  impact pose. What stays dangerous is freezing WHILE a turn plan is
  mid-flight: a stale plan targeting nonzero speed can raise a
  teleportation-watchdog exception and kill the host. The suite unfreezes
  defensively at the start of every crossing run for that reason.

## Camera focus: per test, both clients, one declaration

**The scenario says what the window should look at** (`SCENARIO_FOCUS` in
`tests/test_turning.py`). Three kinds, and the difference between the last two
is the whole point:

| kind | what the camera does | who uses it |
|---|---|---|
| `car` (default) | follow the **primary** car and **fit the view to it** (`MapRenderer.CarFocusViewM` = 120 m across the window) | every scenario that declares nothing |
| `named` | follow that named car of the cast (`POST /teleport {"name": …}`) at a declared px/m | #18 `parked_car`, #19 `slow_car` — the subject is not the primary car |
| `loc` | a **place**: centre it, hold the declared px/m, and **follow no car at all** | #3–6 (the T-junction node), #17/#20 (the spot on the straight where the pass happens), #26/#27 (the crossing box) |

> **The rule (user decision 2026-10-24):** if a test's focus is not a car, the
> window gets a **location**, not "a car plus a location". Following one car out
> of 50 loses the crossing; tailing a car that drives away loses the obstruction
> the whole test is about.

### Where it is declared, and how it reaches each client

One table, in the file that owns the scenarios — it used to be two python dicts
embedded in `scripts/run_e2e.sh`, which meant the focus could only be handed to
the client as ONE command-line argument for the whole window (so a full-suite run
kept showing the first scenario's framing) and the reviewer could not know it at
all.

```
SCENARIO_FOCUS (tests/test_turning.py)
  ├─ resolved per test against the live map  (GET /segment_names, /segment/{idx})
  ├─ POST /focus at the scenario's start     → /state.focus  → the VIEWER re-aims once per test
  │                                          → RunTape       → "# focus step=… …" in the tape
  └─ --focus-json <n>                        → scripts/run_e2e.sh → --center x y --zoom z / --focus name --zoom z
```

The verdict travels the same way: `POST /test_result` when the suite files a
scenario, exported in `/state.test_results` and recorded as `# result …` on the
tape, so a review can show **"17/27 · ✅ PASSED · 10.6 s sim · 2 crossings"** next
to the frame under the cursor ([RECORD_PLAYBACK.md](RECORD_PLAYBACK.md)).

Locations are derived from the map's own vocabulary so they survive a rebuilt
network: `node_prefix` averages the start points of every segment leaving a node
of that family (the crossing of a fig-8, the mouth of a T), `segment`+`t` is a
fraction along a named segment. `--focus-json` prints the resolved numbers:

```bash
.venv/bin/python tests/test_turning.py --focus-json 26
{"kind":"loc","scenario":"fig8_xing","zoom":26.0,"x":1250.0,"y":1750.0,"uid":…}
```

The tape of any run proves the plumbing:

```bash
grep -c '^# focus'  tests/tape_….txt   # 26 on a full run (one per executed scenario)
grep -c '^# result' tests/tape_….txt   # 26 as well (27 scenarios, fig8_stress skipped)
diff <(grep '^# result' tests/tape_….txt | grep -oE 'label=[^ ]*' | sort -u) \
     <(grep '^# focus'  tests/tape_….txt | grep -oE 'label=[^ ]*' | sort -u)   # the join must hold
```

It answers `{"kind":"car"}` (with a warning line) when no host is up or the named
road is missing — the camera is cosmetic and must never fail a run.

### The hand always outranks the run

`--center`, `--zoom`, `--focus`, a map drag or `F` set `_tFocusManual`: from then
on the announced focus does not re-aim the window. In the reviewer the same precedence
applies — **per scenario**: the hand (map drag, minimap pin, panel CENTRE, or a
panel's **follow**, which is that pin riding the car) > `--center`/`--review-name`
> **the scenario's declaration** > the cast centroid, and **a test boundary
releases the hand** so
the next scenario's focus and follow item take over
([SIMULATIONS_REVIEWER.md → Per-scenario framing](SIMULATIONS_REVIEWER.md#per-scenario-framing-the-recording-re-claims-the-view-at-every-boundary)).

### Verifying it

```bash
# scripted (no hand needed): boundaries, tooltip, marker click, bar zoom, Test panel
"/Applications/Godot_mono.app/Contents/MacOS/Godot" --path . --fullscreen -- \
      --source embedded --map basic --port 5037 --review tests/tape_focusdemo.txt \
      --test-bar-ui --test-bar-point 1200 2780 --screenshot /tmp/bar.png
grep -E "BAR-UI|BAR-ZOOM|FOCUS test" /tmp/godot.log
```

`BAR-UI test panel:` prints the panel's text, `BAR-UI click -> frame N` proves a
marker click jumps to the right substep, `BAR-ZOOM in/out` prints the visible
range. Note: Godot does not deliver **pushed** wheel or bare-motion events to
input handlers in this build, so those two paths are exercised by calling the
same entry points (`BarZoom`, `ReviewMarkerHover`) — a real wheel/hand goes
through the identical code.

## Roles: the fallback when a cast was never named (2026-10-21, history)

Superseded by the section above for everything the scenario author knows up
front (it does know: `SCENARIO_FOCUS` names the car). What stays is the client's
ROLE fallback, for a cast that was never named — the scenario stages its cast
itself, so the uid is not knowable before the run:

```
--focus parked    # the car parked in the lane  (test 18 park_block_priority)
--focus slow      # the slowest car still rolling (test 19 slow_pass_overtake)
```

The role latches ONCE onto the first car that answers it and stays there —
re-picking the per-frame minimum made the window flap between the crawler
and the primary every time the crawler stopped for a pass. `slow` only
latches when the pick is slow RELATIVE to the fleet (≤ 0.6 × the fastest),
otherwise staging time — when only the primary rolls — would name the
primary as "the slow car".

Verified in the window log:

```
CAM-FOCUS role=parked -> uid 2      # test 18, the parked car
CAM-FOCUS role=slow   -> uid 2      # test 19, the 10 km/h crawler
```

Supporting fix: `/state`'s multi-car payload had no `parking` key at all
(the builder at the followed-car branch had it, the fleet branch did not),
so no client could tell a parked obstruction from a queue lead. Both
builders now answer the same shape.

## Named cars (2026-10-21)

A scenario authors its cast, so it knows that a car IS the oncoming one —
naming beats the predicates the client would otherwise guess.

| layer | the handle |
|---|---|
| `POST /teleport` | `"name": "slow_car"` — set **with the spawn**, before the first tick |
| sim | `TestFlags.HudLabel` per uid (a spawn name wins over an adopted label) |
| `/state` | per car `"hud_label": "parked_car"` |
| tape header | `names=1:19/27,2:slow_car` — once per run, not per frame |
| client | `--focus slow_car` (a name), `--focus parked` (a role fallback) |
| reviewer | a car detail window's title bar: `#2 slow_car` (a car the scenario never named reads `#2 no-name`) |

```
CAM-FOCUS name=parked_car -> uid 2      # test 18
CAM-FOCUS name=slow_car   -> uid 2      # test 19
```

The predicate roles stay as a fallback for casts that were never named;
`slow` only latches when the pick is slow RELATIVE to the fleet, which is
exactly the ambiguity a name removes.
