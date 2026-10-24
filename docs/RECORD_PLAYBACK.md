# Record & playback — stepping through a scene

> **Status 2026-10-23 (code-verified):** **built 2026-10-21** — sim tape,
> endpoints and the scrub window (`MapRenderer.review.cs`) all in use. Payload
> keys, route verbs and the size figures below were re-read from
> `DrivingGame.Sim/Tape.cs` (`RunTape`) and `DrivingGame.Api/GameApi.cs`.
> Binding user decisions: **record every substep**, **keep the log for
> each test run**, size is not a constraint.

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
safe index (see `SimEngine._simStepsTotal`). Every tape record, every playback
seek and every decision index keys on **`_simStepsTotal`**. `DtFixed` is
announced in the file header so an offline reader can rebuild the seconds.

## The tape

One record per **substep**, per car:

| field | why |
|---|---|
| `x, y, heading, speed` | the pose — metres, heading degrees, m/s (`docs/UNITS.md`) |
| `blinkers, brake, wrongSide` | what the renderer and the guard saw |
| `capKey` | which rule owns this car's speed cap at this step (`avoid#12`, `r32#3`, `sign#C#4`, `r14#…`) — the "why" of a standstill |
| `heldBy` | which **car** owns it (0 = nobody: free, or the R32 creep where the kerb is what holds the car). The rule names the situation, the actor names the counterpart — a reader that recovers the uid from the key by matching its tail is wrong for `r32#passive` (names nobody) and for `sign#C#7` (names the node before the car). Written BEFORE `capKey` for exactly that reason: the fields are comma-separated while a cap key can carry that same delimiter (`r32#12,passage#4` when `CarCollisions` joins an added reason onto the one already active), so capKey must stay the last field. Added 2026-10-24 for the car detail window's chain ([CAR_DETAIL_WINDOW.md](CAR_DETAIL_WINDOW.md)) |
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
(`RunTape.MaxSteps` = **200 000 substeps** ≈ 55 min of sim at 60 Hz), not a
rolling window — a run is kept whole.

**Multiply the two numbers before trusting a long run.** The cap counts
SUBSTEPS and is shared by the whole fleet, so it bounds duration; the byte
cost bounds car count. 200 000 substeps × 66 cars × ~100 B ≈ **1.3 GB**: that
is what a 66-car run that actually reached the cap would have cost, and at the
cap recording stops with `TAPE FULL at 200000 substeps` rather than growing
without end. Today's e2e runs (50 cars, ≤ 240 s sim per scenario, one tape per
run) sit two orders of magnitude below it.

**Retention of the tapes in `tests/`** — the rule the tree actually follows:
`scripts/run_e2e.sh` saves one tape per run next to its log
(`tests/tape_T_<nnn>_…txt`); a tape is **kept while it is evidence** (a run
under review, a crash worth re-watching) and is otherwise a regenerable
artefact like the log. Nothing prunes them automatically and ~100 are
committed, which is why `.gitignore` (which ignores `tests/run_test_*.log`)
and the tree disagree. Untracking them is a repository decision, not a doc fix
— flagged for the user.

## The run's own annotations: `# focus …` and `# result …`

Two kinds of line sit after the format comment, one per announced scenario.
They are what makes a tape self-explaining to a person reviewing it days later,
with no host and no log open:

```
# focus step=8814 kind=loc x=100.00 y=275.00 zoom=26.00 uid=11 label=17%2F27 scenario=park_block_overtake desc=A%20parked%20car%20blocks%20the%20lane%20-…
# result step=9462 number=17 label=17%2F27 scenario=park_block_overtake passed=1 reason= sim=10.6 wall=10.6 xng=2 off=0
```

| field | notes |
|---|---|
| `step` | the substep it was announced at — in the file's own **step** column, which does **not** have to start at 1 (a host that was already running records steps from wherever it was). A client reads frames by **index**, so it resolves a step with a lookup (`MapRenderer.IndexOfStep`), never by arithmetic — arithmetic on it is a 60-fold error |
| `kind` | `car` / `named` / `loc` (see [TESTING.md → Camera focus](TESTING.md#camera-focus-per-test-both-clients-one-declaration)) |
| `label` | the join key between the two kinds (`"17/27"`); **percent-encoded**, like `scenario` and `desc` (a reason with spaces must stay one token and survive the round trip). It must be the number the SUITE LOOP counts, not one re-derived from the result: `(start_point, direction)` is not unique (`#3`/`#5` are both `tjunction_from_top`/`left`, `#21`/`#24` both `hairpin_exit`/`straight`) and the crossing scenarios file `direction='crossing'` where the table says `straight`. Deriving it collapsed three scenarios onto another's label — so `record_result()` takes `test_no` from the loop |
| `name_uid` | the car a `named` focus was staged as. Resolution order on replay: `name_uid` > the header's uid→name map > the note's primary `uid` — one function (`MapRenderer.FocusSubjectUid`) so the camera and the diagnostics cannot disagree about which car a note means |
| `desc`, `reason` | free text, so the reviewer can say what the test was and how it ended |

A run's parity is a grep, not a hope: one `# focus` per executed scenario and one
`# result` per verdict — **26 and 26** on a full run of the 27 scenarios
(`fig8_stress` is the one skipped under `--warp`, and it still announces its
focus, so the boundary shows on the bar as a scenario that did not run) — with
the two label sets matching one-to-one.

A re-announcement of the same `label` **replaces** the note (a retry must not
stack a second boundary), and old tapes simply have none: that reads as "no
declaration" → follow the primary car, never as an error. The reader accepts
them anywhere in the header block, so a tape saved by an older build still
loads.

> **`/tape/save` paths are resolved by the SERVER** — its working directory is
> `DrivingGame.Server/`, not the caller's, so a relative `"path"` lands somewhere
> nobody meant. A failed write says so in both places now: the HTTP answer carries
> `error` with the absolute target and the server cwd, and the server log names the
> exception (`TAPE-SAVE failed for … DirectoryNotFoundException …`). Send an
> absolute path.

## Where the work lives — the boundary (binding user decision, 2026-10-21)

**Recording, playback and scrubbing all live in the simulator.** The UI holds
only the controls. The sim is the single authority for what happened; **a
client never re-simulates.** What a client MAY hold is recorded history: the
review window caches the tape it loaded (that is what makes a run survive the
host dying, and what `AGENTS.md` prescribes), so the honest boundary is *no
second model of the car*, not *no data in the client*.

**The playback controls are a review client of their own** — its own window,
not an overlay in the map window. Reason (the user's): the map window stays
clean, and the review client can sit on another screen next to it. So:

| piece | where |
|---|---|
| the tape, the frame lookup, the event index | `DrivingGame.Sim/Tape.cs` (`RunTape`) |
| the endpoints below | `DrivingGame.Api` |
| scrub controls (⏸ ⏹ ◀◀ ◀ ▶ ▶▶, a scrub bar, a frame readout) | **its own window** — the Simulationsreviewer |

How that window is implemented changed after the decision was written: it is
`MapRenderer.review.cs`, a `partial` half of the same `MapRenderer` node that
opens its own `Window`s — not a second executable. What BINDS from the 2026-10-21
decision is "**its own window, its own controls, never an overlay on the live
map**", and that holds; "not `MapRenderer`" was the predicted mechanism, and it
is not what was built. See [SIMULATIONS_REVIEWER.md](SIMULATIONS_REVIEWER.md).

## API

| call | meaning |
|---|---|
| `POST /tape {}` | start recording (idempotent); answers `{recording, steps, first, last, dt_fixed}` |
| `POST /tape/clear` | drop the tape (a new run starts a new one) |
| `POST /tape/frame {"step": N}` | the recorded frame at substep N (`-1` = last); `ok: false` when the tape does not reach that far |
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
step;fluidity_mps_per_car;moving_share;uid,x,y,heading_deg,speed_mps,blinkers,brake,wrong,crashes,color,lamps,heldBy,capKey|uid,x,y,…;~uid=decision text~uid=…
```

The reader accepts three generations - `step;cars`, then `+fluidity;moving`,
then `+decision events` - and within the car fields the four of `…,color,capKey`,
`…,color,lamps,capKey` and `…,color,lamps,heldBy,capKey`, so a run recorded last
week is still reviewable (an old file reads `heldBy = 0`, which is the truth: an
older run never named one). It is greppable, and `tests/` already keeps per-run
text logs next to it.

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
