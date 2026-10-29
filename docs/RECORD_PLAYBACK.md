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

## Recording policy and the growing tape (binding user decisions, 2026-10-25)

The tape stops being a recording you make and becomes **the picture**. The
viewer and the reviewer are one window; what differs is which end of a tape it
reads, and "live" is to be read as *the tip of a tape that is still being
written* (see [SIMULATIONS_REVIEWER.md](SIMULATIONS_REVIEWER.md) → *"One
client, one data source"*). Four decisions came out of that, with the state of
the tree marked honestly:

| | decision | status 2026-10-25 |
|---|---|---|
| **(a)** | **every run is recorded** — the recording starts with the sim, not with a `POST /tape` someone remembers | **decided, not built**: `RunTape.Start` is still reached only from `POST /tape`, which today only `scripts/run_e2e.sh` issues |
| **(b)** | the file is **appended from the first substep** and flushed **on every collision and at the latest every second** — never rewritten; `POST /tape/save` becomes "close it and name it" | **decided, not built**: `RunTape.Save` still writes the whole file in one go |
| **(c)** | playback **never jumps**: a cursor that outruns the head waits at it, the lag grows honestly, and only a hand rejoins | built in the reviewer; the following case arrives with the tape-tip source |
| **(d)** | `/state` stays (the suite asserts on it) but stops being the renderer's source | **decided, not built** |

Why "append" and not "save every second": a suite tape is 8–12 MB
(`tests/tape_T_246_full.txt`: 35 907 substeps), so a per-second full save would
rewrite ~12 MB/s; appending one line per substep is ~15–20 KB/s with one flush
per second. "On every crash" needs disambiguating too: `CrashDumper.Dump` is
called from `CarCollisions.cs:2562` — that is a **collision inside the sim**, a
good moment to flush (the evidence is on disk the instant cars touch) but not
process death. There is no `UnhandledException`/exit handler in the tree, and a
SIGTERM aborts .NET's exit path (AGENTS.md, the never-signal rule), so **the
≤ 1 s flush is the actual guarantee** and the collision hook is its fast path.

Three consequences that come with (a) and (b):

**Memory rolls, the file stays whole.** `MaxSteps` (200 000 substeps ≈ 55 min
at 60 Hz) currently *stops recording* — `TAPE FULL at 200000 substeps`. While
the tape was evidence, that cost evidence; once the tape is what the window
paints, the same line means **the view goes blank mid-session**. So the cap
becomes a rolling window over the same absolute step numbering (`_firstStep` as
the new floor, `FrameAt(step)` unchanged inside the retained range) and the
appended file remains the complete run — scrub depth then costs disk, not RAM.

**The header's `steps=` becomes advisory.** Written before the run ends it
cannot know the length; readers already count the lines they got (the review
loader does exactly that), and `dt_fixed`, `map_hash`, `names=`, `# focus` and
`# result` are unaffected.

**`level` on every frame (2026-10-25, closing the first field gap):** the
per-car line gained `level` as the 13th field, before `capKey`:

```
uid,x,y,heading_deg,speed_mps,blinkers,brake,wrong,crashes,color,lamps,heldBy,LEVEL,capKey
```

**`capKey` stays LAST** — a cap key can carry the comma (an added reason is
joined onto an existing key with `,`, `r32#12,passage#4` is the shape), so both
new fields went BEFORE it; appending after `capKey` would have split every old
key silently, in both directions. The car fields therefore have **FIVE**
generations, and the readers accept all of them (`Tape.cs:Load`, the client's
`MapRenderer.review.cs:LoadTape` — both count fields and join `capKey` from the
END): `…,color,capKey` → `…,color,lamps,capKey` → `…,color,lamps,heldBy,capKey`
→ `…,color,lamps,heldBy,level,capKey`. An older file reads `level = 0`, which is
the truth an older run never named — and it is also why an older file's map
stamp reads back truncated (see the table under *"The header's map stamp…"*).

The header gained a `kinds=uid:kind` map beside `names=` (the ACTOR channel: an
actor's kind is authored once at staging, never per substep, so it costs nothing
on the frames). Nothing writes it today — only cars can be staged — so the
header is unchanged for every current run. It is format support, not a hook.

**`level` was the first field gap — it is closed (2026-10-25).** The renderer
stacks a car on its own bridge deck from `/state`'s `level`
(`Network.Segments[car.SegIdx].Level`, `SimEngine.cs:1264,1410`); `TapeCar` had
no such field, so `ReviewFeed` hard-coded `Level = 0` and a car on a deck was
drawn at grade **in the reviewer**. Fixed: `Record` now takes the `Network` and
resolves the level **exactly as `/state` resolves it** (`Tape.cs` reads the same
`Network.Segments[c.SegIdx].Level`), so the two channels cannot *disagree*
about which bridge a car is on. The rule it instantiated is the
load-bearing wall of the whole design: *every field the client draws or displays is
a tape field, or the HUD marks it `live-only`.*

The fix was proven in four places, not one:

- `DrivingGame.Sim.Tests/TapeLevelTests.cs` — a car on the **basic map's real
  figure-8 bridge** (`TestMaps.cs:540` puts `level: 1` on segments n10..n14)
  records level 1, survives `Save`→`Load`, and lands in the 13th column before
  `capKey`; a car on the figure-8's **ground** branch — which shares the same
  crossing point — records 0, so the lookup cannot borrow the deck above it.
- `docs/tapes/deck-car-level-T_probe.txt` (a scripted REST drive onto `fig8_n12→fig8_n13`):
  **level 1 on all 259 substeps.**
- `tests/tape_state_agreement.py` — `/state` and the tape frame at the SAME
  frozen substep: **level exact, pose 0.0 m.**
- The picture itself, A/B: the same tape at the same cursor and camera renders
  the car **on** the bridge with its recorded level and **hidden under the deck**
  with the column hand-zeroed — so the field is what selects the deck, not
  cosmetics. (Two screenshots, `/tmp/mmatch.png` vs `/tmp/flat.png`.)

## The header's map stamp and the client's check (built 2026-10-25)

```
# DGTAP1 map=basic map_hash=v2:segs=256:signs=4:starts=48:sum=5C2E143F:fnv=626AA1A6 dt_fixed=0.016667 steps=259 names=1:deck_car
```

`map_hash` is a **reference**, not a copy: a tape carries no road polygons, and
the ground comes from the map side (embedded: the client's own engine, http:
the host). It is only honest because it is **checked** — measured, both ways,
on `docs/tapes/deck-car-level-T_probe.txt`:

| probe | verdict | what the window shows |
|---|---|---|
| `--source embedded --map basic --review …` | `REVIEW-MAP … → match`, readout `map check: ok` | the deck car ON the bridge (level 1 recorded on all 259 substeps) |
| `--source embedded --review …` (the client's default OSM map beside a `basic` tape) | `REVIEW-MAP … → MISMATCH` + `REVIEW-REFUSED … no cars painted` | a field with **no tape cars on it** — painting them there would claim they drove somewhere they never did |
| a pre-`v2:` tape (any older run) | `unverifiable (pre-wide stamp)` | loads and paints: "unknown" is not "wrong" |

`level` rides with it: the recorder resolves a car's deck exactly as `/state`
does (`Network.Segments[c.SegIdx].Level`), and `tests/tape_state_agreement.py`
holds the two channels against each other at ONE frozen substep — level exact,
pose within 1e-4 m. The same script also names the honest clock skew it found:
`/state` is a per-FRAME snapshot while the tape is per-SUBSTEP, so the head can
sit one substep past the snapshot the client sees; asking the head (`step: -1``)
instead of the frame `/state` names measures a race, not a disagreement (the
first run read a 9.5 cm gap that way).

## What was measured, and the one thing the suite CANNOT show (2026-10-25)

Three witnesses for the two new tape fields, all of them reproducible:

1. **the deck drives the picture.** The SAME tape rendered twice, at the same
   cursor, camera and zoom: `docs/tapes/deck-car-level-T_probe.txt` (level column = 1 on all
   259 substeps) paints the car **on** the bridge, with the deck's white edge
   lines crossing over the road beneath it. Its copy `/tmp/tape_flat.txt`, with
   the level column zeroed by hand, paints **no car at all** — the car is at
   grade and the deck's fill covers it. One field, two pictures: the difference
   is not cosmetic, and the level is provably what selects the deck.
2. **the two channels agree at one substep.** `tests/tape_state_agreement.py`
   freezes the sim and puts `/state` next to the tape frame the substep names:
   level exact, pose 0.0 m, per uid, both directions.
3. **the map reference is checked.** three probes on one tape: its own map
   (`match`, readout `map check: ok`), a foreign embedded ground
   (`MISMATCH` + `REVIEW-REFUSED`, an empty field), and an older tape whose
   spaced stamp read back truncated (`unverifiable (pre-wide stamp)`, painted).

**No suite scenario drives the bridge**, so this is worth saying before someone
reads a suite tape and "corrects" it: every car record in a full-suite tape
(`tests/tape_T_265_0_20261025_155532.txt`: 126 167 records) carries
`level = 0`, because the two figure-8 scenarios are the PLANAR pair
(`TestMaps.cs:633` — "REAL degree-4 junction (**all segments ground level**)"),
while the elevated loop's start points (`fig8_bridge`, `fig8_deck`, `fig8_under`,
`TestMaps.cs:549-551`) are used by NOTHING in `tests/test_turning.py` (one grep,
zero hits). The reviewer's old `Level = 0` lie was therefore never visible in a
suite tape at all — it takes a scripted drive onto the deck, which is exactly
what the probe above does. Closing that gap also means the suite cannot be the
witness for the deck field; the probe is.

## The channels: what is a frame, what is an event, what is the map (binding, 2026-10-25)

The user closed the class question — the sim **will** grow dynamic ground
(traffic lights, a policeman directing the traffic, …), so the channels are
decided **before** the first of them is built, once, not retrofitted per
feature. Today's state is one grep: no light, cop or pedestrian exists in
`DrivingGame.Sim` (`docs/HUMAN_FACTORS.md:116`: "no traffic lights yet"), and
the only dynamic ground we have is an obstacle — `GameApi.cs:820/823/849`
`GET/POST/DELETE /obstacles` — which **nothing on the tape records**, so a
scrubbed frame cannot today show where the parked car stood. (`level` went the
same turn: it is a tape field now — see *"Recording policy…"* above.)

| class | example | channel | why |
|---|---|---|---|
| static ground, per map | road network, markings, **yield/priority signs**, bridge decks, colours | **the map reference** (`map=` + `map_hash=`) — **built and checked 2026-10-25** | The tape names its ground; `GET /map` answers `map_fingerprint` (`MapPayload.cs:204`) beside the geometry, and the client compares the two stamps (`MapRenderer.review.cs:CheckTapeAgainstMap`) — a v2-vs-v2 mismatch **refuses the picture** (`ReviewFeed` returns, the readout says `MAP MISMATCH — refused`; `--ignore-map-hash` overrides). One `RoadNetwork.MapFingerprint` on BOTH sides, so the check cannot drift by construction. The stamp is `v2` and wide: segment endpoints/width/oneway **plus `level`, lanes, parking-lane width, shoulder**, every sign's **type and node**, every start point (position/heading/segment/lateral offset + name) and the three counts — because the old stamp mixed **`net.Signs.Count` only**, so the same roads with a yield traded for a priority hashed IDENTICAL and a tape could be painted under a right-of-way that is not the run's. Two traps, both measured: the stamp joins its parts with **':' never ' '` (the header line is space-separated, so a spaced value was truncated to `v2` by both readers and turned every comparison into a false verdict), and a stamp **without** the `v2:` prefix is *unknown*, not wrong — those older tapes load with `map check: pre-wide stamp, unverifiable` instead of a refusal. |

Measured on the stamps themselves, both directions (the header parse is
`Split("map_hash=")[1].Split(" ")[0]` in BOTH readers — `Tape.cs:Load` and
`MapRenderer.review.cs:LoadTape`):

| stamp | as written | as read back |
|---|---|---|
| the wide one | `v2:segs=256:signs=4:starts=48:sum=5C2E143F:fnv=626AA1A6` | whole — ':' keeps it ONE token |
| the old one | `segs=256 signs=4 sum=56AAB638 fnv=301F63CD` | **`segs=256`** — the spaces ended the value, so an old stamp never even reached the client whole |

The same trap swallowed `map=` a year later, from the other side (#11): the
engine stamped its own self-description, `"engine boot"`, so an ad-hoc run
recorded `map=engine boot` and the reader's `Split("map=")[1].Split(" ")[0]`
handed the review window a map called **`engine`** — which does not exist, so
since #2 it correctly refused to paint a single car. Two rules came out of it:
**a host names its map** (`SimEngine.MapName`, handed in by `SimHost` / the
console host, which both know it from `--map`), and **a name that cannot stand
in the header is replaced, visibly** (`RunTape.SanitiseMapName`: space, `,` and
`=` become `_`, with a `TAPE-MAP-NAME` line on the console) — because writing a
name the reader will shorten is worse than writing an ugly one.

That row is why the wide stamp had to change format rather than just gain
fields: a spaced fingerprint was already lossy in the file, so a spaced `v2`
would have compared `v2` against the full drawn hash and answered MISMATCH on
the SAME map. The old truncated value is kept as read (no re-record of history)
and is what the client's `pre-wide stamp, unverifiable` branch is built for.
| sparse state, per change | light **phase**, gate open/closed, obstacle **placed/removed** | **tape events** (`TapeEvent`) | the transition step + the new state; a review window replays events up to the cursor to rebuild the ground as it was there. The **countdown is not stored** — it is derived from the transition and `Config.cs`, and a phase whose start the tape never saw is not painted (never extrapolate). |
| dense actors, per substep | cars — and **anything that moves**: a cop, a directing policeman, a walking pedestrian | **tape frames** — the kind is a header map (`kinds=uid:kind`), the pose is per substep | `TapeFrame.Cars` is a `List<TapeCar>` whose extra fields (`color`, `capKey`, `heldBy`, `lamps`) describe a **car**. The channel became an ACTOR channel the cheap way: a kind is authored once at staging and never changes per substep, so it lives in the header beside `names=` and costs one token per actor instead of one column per car per frame. Absent means `car` (every actor today — one grep: nothing but cars can be staged). `GET /tape/frame` answers `kind`; the review client SKIPS a kind it has no sprite for (`REVIEW-KIND uid … not painted as a car`, with its pose and level in the line) rather than guessing a sedan for a policeman. |

**Frequency picks the channel, not kind.** A light's *state* changes rarely →
event. A policeman's *position* changes every substep → frame. That single
rule is what keeps a traffic light from putting 87 kB per hour onto the tape
and keeps a directing cop from being invisible at the cursor.

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

    /// A run's parity is a grep, not a hope: one `# focus` per executed scenario
    /// and one `# result` per verdict — **26 and 26** on a full run of the 27
    /// scenarios (`fig8_stress` is the one skipped under `--warp`, and it still
    /// announces its focus, so the boundary shows on the bar as a scenario that
    /// did not run) — with the two label sets matching one-to-one. The same
    /// parity holds for the annotations in a saved tape: `grep -c '^# focus '
    /// tests/tape_*.txt` equals the executed-scenario count.

## `--map basic` and the stamp of the map side (the reference, 2026-10-25)

Both probe tapes above were recorded with `--map basic`, and the deck probe
repeats the point: the recorder resolves a car's deck from `Network.Segments[c.SegIdx].Level`
— the SAME lookup `/state` uses — so a car ON the figure-8 bridge records level
1 for all 259 of its substeps, and the ground branch UNDER the same crossing
records 0. The recorder reads the segment the CAR is on, not the deck that
happens to pass above it. That field is what decides whether the reviewer draws
a bridge car ON the deck or AT grade (see *"Recording policy and the growing
tape"* → `level`).

## The two channels agree at one substep (checked 2026-10-25)

`/state` and the tape are written by different code paths, so "the tape is the
picture" only holds if the two describe the SAME substep. `tests/tape_state_agreement.py`
makes that a fact: it teleports a car ONTO the figure-8 bridge **by segment**
(a start point at the crossing lands on a ground segment, which proves nothing),
polls the SIM clock until a car is actually at `level >= 1` — the deck claim,
from `/state` — freezes, then compares `/state`'s cars against the tape frame
**at the substep `/state` names** (not at the head: `/state` is a per-FRAME
snapshot cached under `_stateLock` while the tape is per-SUBSTEP, so the head
can sit one substep past the snapshot a client sees — asking the head measured
that skew as a 9.5 cm gap, which is a race, not a disagreement). Verdict: level
exact, pose within 1e-4 m, per uid, both directions (a recorded uid missing from
`/state` and the reverse are both errors).

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
| `POST /tape {}` | start recording (idempotent); answers `{recording, steps, first, last, dt_fixed}` — and the map **reference** the run belongs to: `{map, map_hash}` (`map_hash` is `v2:segs=…:signs=…:starts=…:sum=…:fnv=…`, the same `RoadNetwork.MapFingerprint` `GET /map` answers as `map_fingerprint`, so a review client can check a tape against the ground beside it — docs/RECORD_PLAYBACK.md → *"The header's map stamp and the client's check"*) |
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
step;fluidity_mps_per_car;moving_share;uid,x,y,heading_deg,speed_mps,blinkers,brake,wrong,crashes,color,lamps,heldBy,level,capKey|uid,x,y,…;~uid=decision text~uid=…
```

The reader accepts three generations - `step;cars`, then `+fluidity;moving`,
then `+decision events` - and within the car fields **five**: `…,color,capKey`,
`…,color,lamps,capKey`, `…,color,lamps,heldBy,capKey` and
`…,color,lamps,heldBy,`**`level`**`,capKey` (added 2026-10-25), so a run recorded
last week is still reviewable (an old file reads `level = 0`, `heldBy = 0` — the
truth an older run never named). **`capKey` stays LAST in every generation:** the
record is comma-separated and a cap key can carry that delimiter (an added
reason is joined with `,`), which is why `level` and `heldBy` were put BEFORE it
— appending a field after `capKey` would silently split every old key. Both the
writer (`Tape.cs:Save`, one `FrameLine` for the file AND for `POST /tape/tip`)
and both readers (`Tape.cs:Load`, the client's `LoadTape`) — but the car fields
are decided by the file's OWN `# step;…` legend, not by counting: a generation-4
record whose cap key carries the delimiter (`r32#12,passage#4`, the shape
`CarCollisions` joins an added reason with) has 14 tokens, exactly what a
generation-5 record has, so counted, an old key would be read as a deck level
plus a truncated key — SILENTLY, in a window whose claim is to be evidence.
Counting stays the fallback for the pre-legend files that never wrote one
(where the ambiguity is inherited, not introduced). A generation bump is a parse
change, never a re-record.

`level` is **the deck a car drives on** (0 = ground, 1 = bridge…) — the level of
the segment it was on at that substep, resolved exactly as `/state` resolves it
(`SimEngine.cs:1264` reads `Network.Segments[c.SegIdx].Level`; `Tape.cs:Record`
now takes the `Network` and reads the same, so the two channels cannot
*disagree* about which bridge a car is on). It is part of the PICTURE, not a
diagnostic: the renderer offsets and occludes a car by it (`MapRenderer.cs:3772`
reads `CarSample.Level`, decks draw at z 20), and the reviewer used to paint
`Level = 0` for every tape car — **a car on a bridge was drawn at grade.** Fixed
2026-10-25 with the rule it instantiated: *every field the client draws is a
tape field, or the HUD has to say `live-only`* (binding, 2026-10-25). Old tapes
keep their lie — a pre-fix file still reads 0 — so review clients announce the
generation they loaded (`REVIEW-LOAD … map basic #<hash>`) and a run under
review today is re-recorded, not re-read from an old file.

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

> **2026-10-25, the rate rule that goes with it:** the cursor advances at the
> **dropdown's** rate (`1×` = one sim-second per wall second, the ruler being
> `dt_fixed`), and when that outruns the head it **waits at the head** — no
> jump, no extrapolation past recorded truth, and only a keypress rejoins.
> There is deliberately no `LIVE` entry in the picker: following the head is
> what `1×` does when the run is not warped, and a warped run is watched at its
> own pace unless you ask for the host's.

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

## The cast list (2026-10-21), and the actor kinds beside it (2026-10-25)

The tape header carries `names=<uid>:<role>,...`. A run has a cast and the
cast has roles, authored at staging — so they belong to the TAPE, once, not
to all 800+ frames. A car that is re-labelled mid-run keeps its first name:
the tape describes who drove, not who said so.

Beside it, `kinds=<uid>:<kind>,...` is the **actor** channel: an actor's kind is
authored once at staging and never changes per substep, so — like the roles — it
lives in the header and the frames stay a per-actor pose list. Nothing writes it
today: only cars can be staged, and `car` is implied when a uid is absent (older
runs never named kinds). It exists because the class decision of 2026-10-25 says
the sim **will** grow actors that are not cars — a policeman directing the
traffic moves every substep, so he is a frame, and `TapeCar`'s other fields
(`color`, `capKey`, `heldBy`, `lamps`) mean nothing to him. A frame entry whose
kind the client has no sprite for is NOT drawn as a car: an unknown kind is
displayed as "not paintable here", never guessed.

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
