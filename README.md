# Driving Game

> **Status 2026-10-23 (code-verified)** — checked against `c827710` plus a
> comment-only documentation pass; routes read from `DrivingGame.Api/GameApi.cs`,
> commands re-run. Suite numbers live in
> [docs/TESTING.md → "Current baseline"](docs/TESTING.md#current-baseline-the-one-place-a-number-is-written),
> not here.

A traffic simulation — cars with a human-factors driving model on OpenStreetMap
and synthetic road networks, rendered by a Godot 4 C# client. The simulation is
the authority: **no client ever simulates.** The viewer talks to the host over
REST (`--source http`) or reads the engine's state snapshot in-process
(`--source embedded`, one process, same application code), and the reviewer
reads a recorded tape — over the API or straight from its file. All three
sources are *recorded or live state*, never a second model of the car.

## Two clients

| | |
|---|---|
| **Simulationsviewer** | the Godot window we run today (`Main.tscn` + `MapRenderer.cs`). It shows the **live** sim: the map, the cars, the camera following the car or pinned on a hotspot |
| **Simulationsreviewer** | a **separate client, its own window** — **built** (`MapRenderer.review.cs`, a `partial` half of the same `MapRenderer` node, specified 2026-10-21, in use since). It shows a **tape frame at a cursor**: play, pause, stop, record, rewind, fast-forward, its own zoom and crop, a scrub bar with the fluidity chart and its event markers, a **car detail window** per car (right-click to open, no cap, its own event list and holding chain, right-click an event row to copy its address, plus a **readout label** naming the tape, the frame, the sim time and the camera (`tape_T_…txt · frame 743/828 · SIM 12.383 s · ▶ 4× · head 828` / `cam x y zoom · view · units`) and a **whole-window screenshot** named against the tape and the frame. See [docs/SIMULATIONS_REVIEWER.md](docs/SIMULATIONS_REVIEWER.md) |

They are independent and long-lived: a new run is a new tape, not a new process.
Neither client restarts because the sim host restarted — a dead host is a
connection state, and both clients show
`⚠ No connection to Sim Engine — retrying …` and re-acquire the host when one
answers. Only a change to the renderer forces a rebuild. The reviewer may
**cache the tape it is reviewing** (that is what makes a run survivable when the
host dies) — what it never does is re-simulate.

The reviewer's whole point is that the two views are not the same picture: the
viewer lerps and trails the sim by 100 ms to stay stutter-free, the reviewer
shows recorded substeps — smoothed in ▶ play (bracketed by two real frames),
exact in step mode.

## Running it

```bash
dotnet build DrivingGame.Server/DrivingGame.Server.csproj   # the sim + API host
dotnet run  --project DrivingGame.Server -- --map basic --warp 4   # REST API on :5001
```

```bash
# the viewer (default: HTTP mode, follows the car at zoom 14 px/m)
"/Applications/Godot_mono.app/Contents/MacOS/Godot" --path . -- --zoom 14
# one process: sim embedded in the client + embedded REST API
"/Applications/Godot_mono.app/Contents/MacOS/Godot" --path . -- \
      --source embedded --map basic --port 5001 --zoom 14
# THE REVIEWER — a second window on a tape (path optional: without one it
# opens the newest tape in tests/). --review-at parks the cursor on a step,
# --review-name aims the camera at the car with that spawn name.
TAPE=$(ls -t tests/tape_T_*.txt | head -1)   # or docs/tapes/<what-it-shows>.txt
"/Applications/Godot_mono.app/Contents/MacOS/Godot" --path . -- \
      --review "$TAPE" --review-at 3308 --maximized
#   A reviewer needs NO host: `--review` implies `--source embedded --port 0`,
#   so the ground is built IN-PROCESS from the map named in the tape's `map=`
#   header (the same MapPayload.Build the /map endpoint serves, so the
#   fingerprint check means the same thing) and no API port is bound at all.
#   `--map <name>` outranks the header; `--source http` attaches to a host
#   instead - the way to review a tape whose ground lives on another machine;
#   a map this client cannot build says `NO GROUND … cars not painted` rather
#   than drawing cars on nothing. The substep of `--review-at` belongs to THAT
#   tape; omit it to open at frame 0.
```

Every flag the client parses is listed in
[docs/SIMULATIONS_REVIEWER.md → "Launching the two windows"](docs/SIMULATIONS_REVIEWER.md#launching-the-two-windows)
— they are `OS.GetCmdlineUserArgs()` arguments, i.e. everything after `--`.

```bash
scripts/run_e2e.sh --warp 4 --tests 17-18     # e2e: small window, verdicts fast
scripts/run_e2e.sh --realtime --maximized --zoom 5 --center 100 220 --tests 18
scripts/run_e2e.sh --watch --center 1250 1750 --zoom 18 --tests 26   # stage frozen, then run
```

Two run modes, never mixed (see `AGENTS.md`):

- **watch / review** — fullscreen, camera on the hotspot, **`/freeze` while
  staging**, spawn every car, poll until all uids exist, then unfreeze. Staging
  while the sim ticks burns the first seconds of the manoeuvre.
- **e2e tests** — smaller window, `--warp 4`, through the named scenarios as
  fast as possible.

If a request could be either, **ask**.

## The record & playback layer (built)

Every e2e run also writes a **tape**: every substep, every car — pose, blinkers,
braking, the wrong-side verdict, crash count, the rule that owns that car's speed
cap, and the fleet's **fluidity** at that substep.

```
tests/run_test_T_<nnn>_<sel>_<stamp>.log    the suite's verdicts
tests/tape_T_<nnn>_<sel>_<stamp>.txt        the substep tape of the same run
```

The clock is **substeps**, never render frames (`simTime = _simStepsTotal * DtFixed`
— a slow wall-clock frame runs 2 substeps). Timestamps in the tape are **sim
time**, so a tape recorded at `--warp 4` replays at the correct speed; the warp
only changed how fast sim time advanced on the wall clock.

| endpoint | meaning |
|---|---|
| `POST /tape` | start recording (idempotent) |
| `GET /tape/index` | step range + the steps that carry an event |
| `POST /tape/frame {"step": N}` | one recorded substep (`-1` = last) — **POST**, not GET |
| `GET /tape/list` | the recorded runs on disk, newest first |
| `POST /tape/load {"path": …}` | load a saved run for scrubbing (recording stays off) |
| `POST /tape/save {"path": …}` | write the whole tape to a file |
| `POST /tape/clear` | drop the recorded frames, keep the recorder running |

Measured, not assumed: the tape costs **0.07–0.08 µs/car/substep** at 50 cars —
about 0.04% of the ~200 µs/car/substep budget. That is CPU cost, not memory:
the size limit is `RunTape.MaxSteps` (200 000 substeps ≈ 55 min of sim) and the
tape grows ~100 B per car per substep — multiply the two before assuming a long
multi-car run fits (see [docs/RECORD_PLAYBACK.md](docs/RECORD_PLAYBACK.md)).

## Documentation

**Current spec** (what the code does today — these carry a `Status <date>
(code-verified)` header):

| | |
|---|---|
| [docs/SPEC.md](docs/SPEC.md) | the master document: the model, the roadmap, the done-list |
| [docs/DRIVING_MANEUVERS.md](docs/DRIVING_MANEUVERS.md) | the driving ruleset (R1…R36) — including R36, "the oncoming lane is used for the minimum" |
| [docs/RIGHT_OF_WAY_NEGOTIATION.md](docs/RIGHT_OF_WAY_NEGOTIATION.md) | how two cars settle a conflict without a traffic light (R34/R35 and the courtesy rules live ONLY here) |
| [docs/SMOOTH_GEOMETRY_DESIGN.md](docs/SMOOTH_GEOMETRY_DESIGN.md) | the geometry pipeline: fillets, ribbons, lane markings |
| [docs/UNITS.md](docs/UNITS.md) | which unit which name carries |
| [docs/REST_API.md](docs/REST_API.md) | the API surface **and the multi-car contract** (`uid`, primary/followed car, per-car channels, the `cars` array) |
| [docs/TESTING.md](docs/TESTING.md) | the e2e workflow, the scenario list, **the baseline** |
| [docs/SIMULATIONS_REVIEWER.md](docs/SIMULATIONS_REVIEWER.md) | **the review client** — controls, scrub bar, the fluidity chart with its event markers, the car detail windows, the connection layer |
| [docs/CAR_DETAIL_WINDOW.md](docs/CAR_DETAIL_WINDOW.md) | the **per-car inspector** both clients share — right-click to open, unlimited windows, the held-by chain, the event list, session memory. **Reviewer half built 2026-10-24** (`CarDetailWindow.cs`, `--test-slot-buttons`); the viewer, the peek card and the session file are open. Supersedes the three car slots |
| [docs/RECORD_PLAYBACK.md](docs/RECORD_PLAYBACK.md) | the tape, the clock, the endpoints, the per-substep fluidity |
| [docs/GODOT_FRONTEND.md](docs/GODOT_FRONTEND.md) | the client's architecture (its phase log below the header is history) |
| [docs/OBSTACLES.md](docs/OBSTACLES.md) | static obstructions: placement rules, contact stop, REST |
| [docs/HUMAN_FACTORS.md](docs/HUMAN_FACTORS.md) | the human-factor model behind reaction, comfort braking and cap persistence (R7, R11) |
| [docs/godot_web_rendering_architecture.md](docs/godot_web_rendering_architecture.md) | **reference note, not a spec**: the two ways to put a Godot sim in a browser — WASM web export (with the `SharedArrayBuffer` headers threads need) vs server-side WebRTC pixel streaming. Added 2026-10-24; neither path is built or decided |
| [docs/lights_report.md](docs/lights_report.md) | measured light geometry (blinkers, hazard, stop lines) |
| [docs/CRASH_EFFECT_SMOKE.md](docs/CRASH_EFFECT_SMOKE.md) | how a one-shot crash smoke puff would be built in Godot — **NOT implemented** |
| [AGENTS.md](AGENTS.md) | the project rules that bind every change |

**Open work**: [TODO.md](TODO.md) — what is known to be unfinished, dated, with
the state of the tree when it was written (it is a list, not a spec: the specs
above stay authoritative and every entry points at the one that governs it).

**History** (kept for provenance; describes an earlier state on purpose):
`diary/` (session logs, incl. the raw `prompts.txt` transcript),
`docs/StVO.pdf` (the German traffic law the §§-quotes in the ruleset rest on),
and [docs/reviews/](docs/reviews/) (dated review snapshots — not specs).

Renamed 2026-10-23: `Human Factor Model.md` → `HUMAN_FACTORS.md` (a name with
spaces can never be a working link), `CarVisuals.md` → `CRASH_EFFECT_SMOKE.md`
(the file is about a crash effect, not about car visuals).

**Retired 2026-10-23** (four transitional documents whose work is finished; the
full text stays in git history at `c827710`, and everything they alone
recorded was moved first):

| deleted | where its content went |
|---|---|
| `docs/MULTI_CAR_PLAN.md` | [docs/REST_API.md → "Multi-car"](docs/REST_API.md) — the five binding semantics (replace-by-default `POST /teleport` + `add`, primary/followed car incl. the highest-uid fallback, per-`uid` addressing, `/cars clear/remove`, top-level + `cars` array) |
| `docs/TURN_REWORK_PLAN.md` | the two-tier road surface + blinker reachability ("next place where it still physically works", slide past / crash is legal, teleport is not) → [docs/DRIVING_MANEUVERS.md](docs/DRIVING_MANEUVERS.md) §4 & §8; "the graph is sacred, the curve is a function of it" + why centripetal Catmull-Rom → [docs/SMOOTH_GEOMETRY_DESIGN.md](docs/SMOOTH_GEOMETRY_DESIGN.md); the driving-line optimisation was already in [docs/SPEC.md](docs/SPEC.md) |
| `docs/C_SHARP_PORT_SPEC.md` | closed 2026-09-08 — phase record is in `diary/` and git; forward-looking parts had already been MOVED to `SPEC.md` |
| `docs/EXPERIMENT-fig8-crossing.md` | the criterion, the spawn discipline and the dated measured history of #26/#27 (2026-09-11 → 2026-10-23, incl. the R29 valve firing) → [docs/TESTING.md](docs/TESTING.md) "The crossing-stress scenarios" |
