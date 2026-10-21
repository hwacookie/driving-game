# Record & playback — stepping through a scene

Status: **built 2026-10-21** (sim tape + API; the window scrub is the last
piece). Binding user decisions: **record every substep**, **keep the log for
each test run**, size is not a constraint.

## Why it exists

Reviewing a manoeuvre (a pass of a parked car, a crossing negotiation) by
watching it once at 1× loses the moment: the eye has not found the cars yet
while the sim is already ticking. A tape turns "show me again" into
"step through it", and — because the tape carries the rule that owns each
car's speed — it turns "why did it stop" into a question you can ask **at a
frame** instead of reconstructing from a decision log.

## The clock — the one thing that must not be guessed

The sim clock is **substeps**, not render frames:

```
simTime = _simStepsTotal * DtFixed
```

A slow wall-clock frame runs 2 substeps in one iteration, so `Frame` is not a
safe index (see `SimEngine.cs:119-124`). Every tape record, every playback
seek and every decision index keys on **`_simStepsTotal`**. `DtFixed` is
announced in the file header so an offline reader can rebuild the seconds.

## The tape

One record per **substep**, per car:

| field | why |
|---|---|
| `x, y, heading, speed` | the pose — metres, heading degrees, m/s (`docs/UNITS.md`) |
| `blinkers, brake, wrongSide` | what the renderer and the guard saw |
| `capKey` | which rule owns this car's speed cap at this step (`avoid#12`, `r32#3`, `sign#C#4`, `r14#…`) — the "why" of a standstill |
| `crashEpisodes` | so a contact is findable by scrubbing, not only by the crash dump |
| **decision text** | recorded **only on the substep where a new decision pops up** — the driver's own reasoning at that frame |

The decision column is sparse by construction: measured on `park_block_overtake`
(820 substeps, 82 KB), **9 lines carry a decision** — about 1% growth, not one
line per car per step. The 100-entry `Car.Decisions` cap only protects live
memory now; a run loaded from disk keeps its full history, which is what the
reviewer's car panels need — a scrub to an early frame would otherwise show a
list that no longer contains that frame.

Memory: ~100 B/car/step → **66 cars ≈ 24 MB/min**. The user said size is no
problem; the guard against runaway memory is a generous step cap
(`Tape.MaxSteps`), not a rolling window — a run is kept whole.

## Where the work lives — the boundary (binding user decision, 2026-10-21)

**Recording, playback and scrubbing all live in the simulator.** The UI holds
only the controls. The sim is the single authority for what happened; a client
never re-simulates and never keeps its own copy of the world.

**The playback controls are a separate client of their own** — its own window,
not an overlay in the map window. Reason (the user's): the map window stays
clean, and the review client can sit on another screen next to it. So:

| piece | where |
|---|---|
| the tape, the frame lookup, the event index | `DrivingGame.Sim/Tape.cs` (the sim) |
| the endpoints below | `DrivingGame.Api` |
| scrub controls (⏸ ⏹ ◀◀ ◀ ▶ ▶▶, a scrub bar, a frame readout) | **a separate review client window**, not `MapRenderer` |

## API

| call | meaning |
|---|---|
| `POST /tape {}` | start recording (idempotent); answers `{recording, steps, first, last, dt_fixed}` |
| `POST /tape/clear` | drop the tape (a new run starts a new one) |
| `GET /tape/frame?step=N` | the recorded frame at substep N (`-1` = last) |
| `GET /tape/index` | step range + the steps that carry an event (decision, crash) |
| `POST /tape/save {"path": …}` | write the whole tape to a text file, one line per substep |
| `GET /tape/list` | the recorded runs on disk — path, name, bytes, mtime, map, dt, steps (newest first) |
| `POST /tape/load {"path": …}` | load a recorded run into memory for scrubbing; **recording stays off** — a loaded tape is under review, the live sim keeps its own |

`/tape/list` + `/tape/load` are what make an old run re-watchable: pick it from
the list, load it, scrub it. No scenario re-run, no dependence on the map still
being in the state it was.

The file format is line-based on purpose - the CURRENT generation, one line
per substep:

```
step;fluidity_mps_per_car;moving_share;uid,x,y,heading_deg,speed_mps,blinkers,brake,wrong,crashes,color,lamps,capKey|uid,x,y,…;~uid=decision text~uid=…
```

The reader accepts three generations - `step;cars`, then `+fluidity;moving`,
then `+decision events` - and within the car fields the three of `…,color,capKey`
and `…,color,lamps,capKey`, so a run recorded last week is still reviewable.
It is greppable, and `tests/` already keeps per-run text logs next to it.

## Pause, stop, and scrubbing behind a live sim (corrected 2026-10-21)

The tape is readable **while it is being written** — `FrameAt` takes the same
lock the recorder writes under — so the tape head is always ahead of the
playback cursor. That makes three genuinely different things, and conflating
them was the bug:

| button | what it does | sim state |
|---|---|---|
| **▶ play** | the cursor follows the live head — normal viewing | running |
| **⏸ pause** | the **cursor** stops. The sim keeps ticking, the tape keeps growing. Scrubbing moves inside `[0, head]` — you watch the story so far while the world runs on behind you | **running** |
| **⏹ stop** | `POST /freeze {"frozen": true}` — the simulation itself halts; the head stops advancing | frozen |
| **● record / resume** | `POST /freeze {"frozen": false}` — continues from the halted state, nothing re-simulated | running |

So pause is a **client-side cursor**, not a sim command at all. Stop is the only
thing that freezes. That is the feature the user asked for: reviewing the last
seconds of a manoeuvre *while the run is still going*, instead of only after it
ended — a live sim plus a rewindable history in the same window.

Two properties make it honest and both must stay true:

- `/freeze` stops the **substep loop**, not the world and not the clock — so
  resuming continues the same run;
- the tape is read under the recorder's lock, so a scrub never sees a torn frame.

## Fluidity is stored per substep (binding user decision, 2026-10-21)

Every tape frame carries the fleet's fluidity **at that substep**, because the
point of the tape is to **compare two runs curve against curve** — a cumulative
number cannot show how fluidity changed over time.

| field | definition |
|---|---|
| `fluidity_mps_per_car` | metres the fleet moved over the last `FluidityWindowS` (5 s sim) divided by (cars × window) — **m/s per car**, the same unit as Q1's flow/car |
| `moving_share` | fraction of cars going anywhere (speed > 0.5 m/s) at that substep — a stuck fleet keeps flow while its share collapses, and that is exactly what a comparison needs to see |

Both are in the file header's column line, in every line, and in `/tape/frame`.

## Per test run

`scripts/run_e2e.sh` starts the tape after the host is up and saves it when the
suite finishes, next to the run log:

```
tests/run_test_T_<nnn>_<sel>_<stamp>.log     (the suite's text log)
tests/tape_T_<nnn>_<sel>_<stamp>.txt         (the substep tape of the same run)
# a line gains a trailing ";~uid=decision text~uid=decision text" only on the
# substeps where a decision was made
```

So a failure report can be re-watched frame by frame months later, without
re-running the scenario.

## Decisions get a frame number

`CarDecision` carries `T` (seconds) **and** `Step` (the substep index). The
timestamp stays for humans; the step index is what a scrub and a tape lookup
must use — `round(T / DtFixed)` re-introduces exactly the float rounding the
clock comment above warns about.

## Watch mode vs e2e mode (AGENTS.md)

- **watch/review**: fullscreen client, camera on the hotspot, **`/freeze` while
  staging**, spawn every car (at speed, with `cruise_kmh` for a slow vehicle),
  poll until all uids exist, then `/freeze false` and run at the requested
  pace. Playback afterwards scrubs the tape.
- **e2e**: small window, `--warp 4`, verdicts as fast as possible.
- If a request could be either, **ask**.

## Measured (2026-10-21)

`StepCost` gained a stage (`tape`, fleet) and the recording was measured, not
assumed:

| run | tape cost |
|---|---|
| 2 cars, `park_block_overtake` | **1.61 µs/car/step** (97 µs/car/s) |
| **50 cars**, random spawn, warp 4 | **0.07–0.08 µs/car/step** (4–5 µs/car/s) |

Measured AFTER the per-substep fluidity columns were added, so the number covers
the whole record (pose + cap key + fluidity + moving share). For context the
same 50-car run reports `flow: 0.04 µs/car/step` — the tape costs about as much
as the flow metric it stores.

Against the ~200 µs/car/substep budget that is **0.04% at capacity** — per-substep
recording is free where it matters. (The 2-car figure is higher because the
per-step list allocation dominates when the fleet is tiny.)

The **review client** is specified separately:
see [SIMULATIONS_REVIEWER.md](SIMULATIONS_REVIEWER.md).

## Gates



`dotnet build` clean · `dotnet test DrivingGame.Sim.Tests/` at **91 passed /
2 skipped** · `scripts/run_e2e.sh --warp 4 --tests <relevant>` unchanged ·
tape cost measured against the ~200 µs/car/substep budget with `StepCost`
before committing to per-substep recording.

## The cast list (2026-10-21)

The tape header carries `names=<uid>:<role>,...`. A run has a cast and the
cast has roles, authored at staging — so they belong to the TAPE, once, not
to all 800+ frames. A car that is re-labelled mid-run keeps its first name:
the tape describes who drove, not who said so.

## Lamps on the tape (2026-10-21)

The tape carries the lamp state per car per substep as one bit field
(`lamps`: bit0 headlights, bit1 taillights), between `color` and `capKey`:

```
uid,x,y,heading_deg,speed_mps,blinkers,brake,wrong,crashes,color,lamps,capKey
```

The reviewer feeds `_carMeta` from the recorded frame, so the renderer's
head dots, tail dots and blinkers describe the frame under the cursor
instead of whatever live car happened to share the uid. Without it every
lamp of every tape car stayed in its construction state — white dots,
black tail dots (the tail lights are BUILT black and re-coloured red per
frame), amber blinkers — which read as holes in the car.

Verified against a scripted drive that toggles the lamps mid-run
(`data/tape_lamps_test.txt` - kept out of `tests/` so the run picker does
not list it as a run; recipe: start the host, `POST /tape`, `POST /teleport`
with `"name":"lamp_car"`, `POST /toggle {"uid":1,"headlights":true,
"taillights":true}`, drive ~3 s, `POST /tape/save`):

| frame | `lamps` | on screen |
|---|---|---|
| 1 | `0` | no dots at all |
| 201 | `3` | white front dots, red rear dots |
