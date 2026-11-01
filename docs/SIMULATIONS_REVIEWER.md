# Simulationsreviewer — the review client

> **Status 2026-10-23 (code-verified):** **built and in use.** Specified
> 2026-10-21, implemented in `MapRenderer.review.cs`, revised 2026-10-22
> against an on-screen review list. The label format, the flag list and the
> clipboard behaviour below were re-read from `MapRenderer.review.cs` /
> `MapRenderer.cs` on 2026-10-23; every decision in this document is a binding
> user decision, and where the code has since diverged the divergence is named
> inline. The tape, the endpoints and the per-substep fluidity it reads are
> built — see [RECORD_PLAYBACK.md](RECORD_PLAYBACK.md).
>
> **Status 2026-10-25:** the two windows are **one client** by decision (see
> *One client, one data source* below). `▶ play`'s frame-snap is fixed and the
> fix is measured — `python3 tests/pose_stutter_check.py --ab`.
>
> **Reading order** (sections were appended, not merged): *Controls* →
> *Readout label* → *Review-list fixes (2026-10-22)* → *One fixed HUD size
> (2026-10-22)* are current; *Implementation status (2026-10-21)* near the end
> is the **oldest** state in the file — where it disagrees with the 2026-10-22
> sections, those win.

## Launching the two windows

Both are the same Godot project; what you pass after `--` decides which window
appears (`OS.GetCmdlineUserArgs()` in `MapRenderer.cs`).

```bash
# the Simulationsviewer, HTTP mode against a host on :5001
"/Applications/Godot_mono.app/Contents/MacOS/Godot" --path . -- --zoom 14
# the Simulationsviewer, one process (sim embedded + embedded REST API)
"/Applications/Godot_mono.app/Contents/MacOS/Godot" --path . -- \
      --source embedded --map basic --port 5001 --zoom 14
# the Simulationsreviewer — a second window on a tape. The path is OPTIONAL:
# without one it opens the newest tape in tests/ (docs/tapes/<name>.txt works
# too, and needs no host: the ground is built in-process from the tape header).
TAPE=$(ls -t tests/tape_T_*.txt | head -1)
"/Applications/Godot_mono.app/Contents/MacOS/Godot" --path . -- \
      --review "$TAPE" --review-at 3308 --maximized
#   attach to a host instead (a ground on another machine, or the live game
#   beside you): add `--source http`, which then asks that host for /map.
#   `--map` names the ground the tape was recorded on (its `map=` header) — a
#   review window refuses to paint cars on a ground it cannot verify, and says
#   `NO GROUND` when it cannot build the one it names; the substep of
#   `--review-at` belongs to THAT tape, omit it to open at frame 0.
#   Recordings are evidence of ONE run, not fixtures: the sweep of 2026-10-29
#   deleted every tape then on disk (24 files, 33 MB, the two committed ones
#   included), so the tape to review is the one the next run writes - or a
#   kept one under docs/tapes/, which `--review` also takes.
```

Use a **free `--port`** for the second window — the embedded API will not bind
a busy one (`embedded REST API FAILED … Address already in use`), and the
reviewer then sits on "no connection". A **reviewer binds nothing**: `--review`
implies `--source embedded --port 0` (issue #2), so two review windows and a
live game can all be open at once without a port argument anywhere.

Every flag the client parses (all after `--`):

| flag | meaning |
|---|---|
| `--source <http\|embedded>` | state source (default `http` against :5001) — **a review window defaults to `embedded` with `--port 0`**: the ground is built in-process, no host, no API, no sim thread (issue #2) |
| `--map <name>` / `--port <p>` | test map + embedded API port (`--port 0` = map source only: no API, no thread). In review mode the map comes from the tape's `map=` header unless `--map` is given |
| `--zoom <px/m>` / `--center <x> <y>` | initial scale; pin the camera (metres, x east y north — auto-follow off) |
| `--focus <name\|role>` | bind the camera to a car by `"name"` from `/teleport`, or by role (`parked`, `slow`) |
| `--review [path]` | open the **reviewer** instead of the viewer (`NewestTape` in `tests/` when no path); implies `--source embedded --port 0` — the tape's own `map=` names the ground |
| `--review-at <step>` | start the cursor at that substep |
| `--review-name <name>` | the camera aims at the car with that spawn name (from the tape header `names=`). It opens **no detail window** — that is a known gap, [TODO.md §5](../TODO.md) |
| `--screenshot <path>` / `--delay <s>` | one-shot viewport PNG `--delay` seconds after the map is ready, then quit (never with `--headless`: the dummy renderer has no viewport texture) |
| `--crash-shot <path>` | one PNG 0.5 s after the crash camera latches |
| `--run-test <number>` / `--quit-on-test-done` | launch one scenario through `POST /run_test`; close the window when a test run finishes |
| `--motionlog <path>` | per-frame motion log (the `--test-*` diagnostics read this) |
| `--test-pan`, `--test-seek <frac>`, `--test-scrub-drag <n>`, `--test-bar-point <x> <y>`,
`--test-slot-buttons` (the car detail window: open, follow, resize, seek —
see [CAR_DETAIL_WINDOW.md](CAR_DETAIL_WINDOW.md)), `--test-jog`, `--test-window-out`, `--test-minimap-pan <x> <y>` | scripted interactions, for reproducing a window/HUD defect without a hand on the mouse |
| `--test-play` | scripted `▶`: presses play 2 s after `_Ready` (Godot timers count *from* `_Ready` — a trace must not score the paused stretch before it as stutter) and then prints `cursor / step / playPos / simNow / renderTarget` once a second. `tests/pose_stutter_check.py` drives it |
| `--no-play-lerp` | A/B switch for the smoothness measurement: advance the play clock but paint the raw recorded substep, i.e. reproduce the 2026-10-25 frame-snap on the same tape (`--ab`) |
| `--resize <w> <h>` | grow the window shortly after start — the only scripted way to reproduce "HUD after maximize" |
| `--test-bar-ui` | scripted probe for the bar's new layers: lands the cursor inside a scenario, zooms the range, hovers and clicks a marker, then prints the tooltip, the Test panel and `focus at cursor` — the deterministic check for boundaries/tooltip/zoom (Godot does not deliver **pushed** wheel or bare-motion events to handlers, so those two go through the same entry points a real event feeds) |
| `--test-pan-probe` | scripted **pan** probe: the same grab at five window heights, then a window's CENTRE, then the grab again, printing `cam / zoom / _reviewPan / _mmAsk / which pin obeys` around each — the instrument that separated "panning is broken" from "panning is shadowed by the pin" (2026-10-24) |

Godot's **own** options go *before* the `--` (that is where `--fullscreen`
lives; `scripts/run_e2e.sh --maximized` / `--watch` translates to it).

## Two clients, one window each

| | what it is | source | lifetime |
|---|---|---|---|
| **Simulationsviewer** | the Godot window we have today (`MapRenderer`) | the live sim | rebuilt only when the renderer changes |
| **Simulationsreviewer** | its own client, its own window | a **tape frame at a cursor** — live or loaded from disk | long-lived; never restarted for a new run |

They are independent. The reviewer holds **no simulation state** — it reads
recorded frames (over the API or from the tape file on disk) and draws them; it
never re-simulates. A new run is a new tape, not a new process.

### What the client actually reads today *(as-built, 2026-10-25)*

The sentence above is the *intent*; these three lines are the *reality*. There
are exactly three ingest sites in the client (`MapRenderer.cs`), and the tape
never reaches any of them from the host:

| channel | how the client gets it | viewer | reviewer |
|---|---|---|---|
| **world** — roads, markings, obstacles | `GET /map` (`FetchMap` → `OnMapResponse`), or the same `MapPayload.Build` **in-process** in embedded | every start | every start |
| **cars, live** | `GET /state` — pipelined poll (`IngestState`), or `_simHost.Engine.StateSnapshot()` in embedded `_Process` | **yes — this *is* the viewer's picture** | poll still runs; `ReviewFeed` then throws its result away |
| **cars, recorded** | **`File.ReadAllText` of a `res://tests/tape_*.txt`**, parsed client-side into `_tape[]` (`LoadTape`) | never | **yes — this is the reviewer's picture** |

So "direct access to the tape" is direct **to the file on disk**, not to the
recorder: no client code ever calls `/tape/frame`, `/tape/list`, `/tape/save`
or `/tape/load` (`MapRenderer.review.cs:233` even says the picker reads the
directory *rather than* `/tape/list`). The host's `RunTape` is a **third copy**
of the run that neither window looks at — `POST /tape/save` is what moves it
onto disk so the reviewer can find it. That is also why a tape that is still
growing on a live host is invisible to every client: the file is written once,
at the end of a run.

Two consequences for the unification *(measured, see TODO §7)* — the second
is a live defect the width of one field, and both are closed by 2026-10-25:

- **The world has no tape — by decision, and the decision is checked.** Geometry
  comes from the **map side**: from a host (`GET /map`), or — since issue #2 was
  closed on 2026-10-29 — from the client's **own in-process build** of the map the
  tape names (`--review` implies `--source embedded --port 0`, the same
  `MapPayload.Build`, the same fingerprint, no API bound). So "the reviewer needs
  no host" is now true for the ground as well as the cars; what never changed is
  that the window must **verify the ground against the tape's stamp** (`REVIEW-MAP
  … match | MISMATCH | unverifiable (pre-wide stamp)`, `MAP MISMATCH — refused`,
  `NO GROUND (cannot build this map) — cars not painted`, `--ignore-map-hash`
  to force). Measured: the same tape on its own map paints the deck car ON the
  bridge; the same tape on the client's default OSM ground paints **no cars at
  all** (`REVIEW-REFUSED … no cars painted`) — a tape painted on the wrong
  ground is not a review, it is a lie about where those cars drove.
- **The tape is no longer car-only where it matters.** `level` is on every frame
  (closed 2026-10-25 — it was the live lie that a bridge car drew at grade) and
  the actor KIND is in the header (`kinds=uid:kind`, `car` implied). What is
  still missing is the **ground's own state** — obstacle placement/removal
  (`GameApi.cs:823/849`) moves the ground during a run and no tape record
  covers it — and the **light phases** the sim will grow. Any field the client
  draws is a tape field, or the HUD must say `live-only`.
- **Display source and control target are two addresses today** (and must stay
  two): `PostFreeze`, `SendCommands` and the whole click-through UI talk to the
  **live host**, so a review window that ⏸'s "the simulation" has always paused
  the sim *next to* the tape, not the tape. Unifying the picture must not unify
  that. *(The live poll during review is wasteful but provably harmless: `ReviewFeed`
  clears `_carBuf[uid]` before writing its one/two recorded samples —
  "review mode owns its history: no live leftovers".)*

### What belongs to the tape, what to the map (binding, 2026-10-25)

The user closed the open question: the map is static per run, so the tape
references it — and the sim **will** grow dynamic ground (traffic lights, a
policeman directing the traffic, …). Three classes follow, and each has one
channel. Deciding a field's channel by its *frequency*, not by its kind:

| thing | changes during a run? | channel | today |
|---|---|---|---|
| road network, markings, **yield/priority signs**, bridge decks, colours | no — per map | **map reference** (`map=` + `map_hash=`) — **built and checked** | `POST /tape` stamps `RoadNetwork.MapFingerprint`, and `GET /map` answers the SAME function as `map_fingerprint` (`MapPayload.cs:204`), so a client can check a tape against the ground beside it (`MapRenderer.review.cs:CheckTapeAgainstMap`). The stamp is wide (`v2:`): segment endpoints/width/oneway **plus `level`, lanes, parking-lane width, shoulder**, every sign's **type and node**, every start point, three counts — the old stamp mixed **`net.Signs.Count` only**, so a yield traded for a priority hashed IDENTICAL (fixed with the reference, closed 2026-10-25; the unit fact `MapFingerprint_SeesSignTypePositionAndTheStartCast` pins it). Geometry still comes from the map side, which is what a reference means. |
| obstacle **placement/removal** (`/obstacles`), a light's **phase**, a gate | yes, rarely | **tape events** — record at the substep of the change, replay onto the cursor to rebuild state | nothing: `GameApi.cs:820/823/849` move the ground, the tape has no ground record at all |
| cars — and any actor that moves: **a cop**, a directing policeman, a walking pedestrian | yes, every substep | **tape frames** | `TapeFrame.Cars` is *car*-only (`Tape.cs`), so an actor that is not a car has nowhere to be recorded; the channel has to be an ACTOR channel before the first cop is simulated |

Consequences I will not defer, since the classes are settled now:

- **Do not store the map in the tape.** A tape names its ground (`map=` +
  a geometry hash that covers what is drawn); the client rebuilds the ground
  from the name (embedded) or asks the host (http) and REFUSES the overlay when
  the hashes differ. A few hundred KB of duplicated road polygons per run buys
  nothing, and it rots the moment the map data changes on disk.
- **A sparse channel is not a slow channel.** A light phase every substep is
  noise; the phase *change* plus the rules that count between changes (`*
  Config.cs` timings) is the truth — the same shape the driver decisions already
  use (`TapeEvent`, `docs/RECORD_PLAYBACK.md`). Never extrapolate the countdown:
  a review window paints a phase only if the tape recorded the change that
  started it.

## The viewer IS the reviewer at runtime (`--tape-live`, wired 2026-11-01, issue 52)

`scripts/run_e2e.sh` now launches the watch window with `--tape-live`: its PICTURE comes from the
host's growing tape (`POST /tape/tip` ships the head in hops of up to 24 substeps) while `/state`
still feeds the readout, the flow and the runner. What the owner gets is the reviewer's
instrument on a run that has not finished: the scrub bar with the fluidity curve, `←`/`→` and a
drag to any substep, the rate dropdown — and the test keeps driving in the background, because
the cursor owns only the picture. The tape is started **before** the window for this reason:
`_tapeLive` deliberately has no fallback (`"the live view is never without a picture"` holds only
if the recorder was already running), and the runner used to switch the tape on after the launch.

`DG_TAPE_LIVE=0` returns to the old direct-live surface, e.g. to compare the two renderers.

## One client, one data source (binding user decisions, 2026-10-25)

The viewer and the reviewer **are the same thing**. What differs is the data
source the window displays: the live simulation, or a tape addressed through
the scrub bar. Because every run is recorded anyway, the live case is to be
read as *the tip of a tape that is still being written* — one renderer, one
clock, one set of overlays — and `--review` shrinks to "open this tape instead
of the newest one".

Four answers closed the open questions:

| | decision |
|---|---|
| **(a) every run** | is recorded, always. Recording is not a diagnostic you remember to switch on |
| **(b) durability** | the tape file is **appended from the first substep**, flushed **on every collision and at the latest every second** — never rewritten. A suite run's tape is 8–12 MB, so "save every second" would be ~12 MB/s of the wrong verb |
| **(c) never jump** | playback keeps going at the selected rate even when it falls behind the head. The lag grows honestly, the label says so, and only a hand rejoins |
| **(d) `/state`** | stays — the e2e suite asserts on it — but stops being the renderer's source once the live view reads the tape tip |

Two rules follow, and both are about the clock:

**The dropdown is the only rate control.** `1×` means one **sim**-second of
tape per wall second; the ruler is the header's `dt_fixed`, so a `--warp 4`
recording replays at the pace the run would have had in reality, and `4×` is
how you watch it at the rate the host produced it. There is no `LIVE` speed:
a cursor that outruns the head **waits** at it (`⏳ waiting for the run`) — it
neither skips ahead nor extrapolates past recorded truth.

**The clock belongs to the source.** The live ingest *estimates* frame time
(samples land at network rate, hence the free-running `_simNow` and its
bounded servo pull at `MapRenderer.cs:3140`); a source that **states** a time —
a tape cursor — *is* that time. `_sourceOwnsClock` (`MapRenderer.cs:262`) is
that seam, and it is the first real piece of "the one refactor both share"
below: an estimate running on top of a stated value puts two clocks in one
frame, disagreeing by a frame's worth of drift.

**And the clock is continuous.** The cursor's *position in tape time* is a
real number (`_playPos`); only the *read* quantises, to the two substeps that
bracket it. A held scrub arrow/button therefore **releases** `▶ play` — a step
sequence and a lerp cannot share one cursor.

Still open, in order: (1) fill the tape's field gaps — **`level` first**: `/state`
carries `Network.Segments[…].Level`, the tape does not, so the review pass paints
`Level = 0` and a car on a bridge is drawn at grade today; the rule is *every
field the client draws or displays is a tape field, or the HUD marks it
live-only*. (2) Let the tape carry its own map geometry, so a review survives the
host and not merely the run. (3) Move the live view onto the tape tip and delete
`_review`.

### Smoothness is measurable (`tests/pose_stutter_check.py`)

The report was: "in the reviewer the cars jump a tiny bit from frame to frame;
the live viewer was completely smooth." The cause was not a mystery and not a
frame-rate fact — `ReviewFeed` interpolated the pose with `_scrubAcc2`, a
variable that was declared, reset to `0.0` in three places, **never written**.
So `▶ play` painted the raw recorded substep and leapt a whole 1/60 s per step:
on a ~130 fps display that is 2 frames of nothing and then a leap.

The harness traces the **painted** node position every rendered frame in both
windows (camera pinned with `--center`, `--motionlog`, `--test-play` presses
`▶`) and scores each pass on `held` (frames repainting their predecessor) and
`jump` (frames moving > 1.8× the typical step). `--ab` re-runs the same tape
with the interpolation switched off (`--no-play-lerp`), so before and after
differ by one switch, not by two worlds. Measured on one tape, one machine:

| pass | median step/frame | held | jump | speed rel-mad |
|---|---|---|---|---|
| live viewer (the reference) | 1.44 px | 0.1 % | 7.9 % | 0.20 |
| review, **before** (raw substep) | **0.00 px** | **67.0 %** | **33.0 %** | median 0 |
| review, **after** (continuous clock) | 1.44 px | 0.0 % | 4.5 % | 0.17 |

Reading the first line of that pair: a typical rendered frame used to paint
*nothing new*, a third of them leapt. Now the review median step equals the
live one and its painted speed is steadier than live — the tape's cursor needs
no starvation pull, the live stream does.

## The one refactor both share

The renderer must be able to draw an arbitrary **world snapshot** instead of
reaching for the live cars:

```
draw(world_snapshot, interpolate: bool)
   viewer            → live state,   interpolate: true
   reviewer ▶ play   → tape[cursor], interpolate: true
   reviewer step     → tape[cursor], interpolate: false
```

Without it the reviewer would mirror the live sim and the rewind would be a lie.

> **2026-10-25:** no `world_snapshot` type exists yet, but the reviewer already
> reached the same renderer by feeding `CarSample`s into the live buffer — and
> the two halves that were missing are now built: the source **owns its clock**
> (`_sourceOwnsClock`) and that clock is **continuous** (`_playPos`). What is
> still missing is the interface itself: `level` on the tape, geometry in the
> tape, and a live view that reads the tape tip.

## Display truth — the viewer's mechanism split in two

The viewer's smoothness is two different things bolted together, and only one
belongs in replay:

| part | purpose | in the reviewer |
|---|---|---|
| `RenderDelaySec = 0.10` | hides network jitter (the newest sample may be mid-flight) | **dropped** — the tape is complete, a permanent 100 ms offset would make the frame readout lie |
| the lerp between samples | smoothness | **kept in ▶ play** |

Honest because in replay the endpoints are **adjacent recorded substeps**, so
every smoothed pose is bracketed by two real ones. In the live view the
endpoints can be far apart, which is why the same lerp is only an estimate
there. **Step mode never smooths**, at any ramp speed.

> ⚠ **This promise is currently broken at the head of a fast tape** — open
> defect 6 in *Review-list fixes*: at 8× a car was drawn **822 substeps stale**
> while the label said `frame 5752`. The mechanism (bracketing) is right, the
> cursor-to-picture link is not. Do not read a fast-playback frame as evidence
> until that is fixed — step mode and 1× are safe.

## Controls

```
▶ play     ⏸ pause (cursor only)     ⏹ stop (freeze host)     ● record (resume host)
◀ hold → ramp back        ▶ hold → ramp forward
,  .  single substep back / forward (exact)
0.5× 1× 2× 4× 8×  playback speed
```

| control | decision |
|---|---|
| **⏸ pause** | only the **cursor** stops. The sim keeps ticking, the tape keeps growing — you fall behind the head and scrub through the story so far. Pause is **not** a sim command |
| **⏹ stop** | `POST /freeze {"frozen": true}` — the simulation itself halts, the head stops advancing |
| **● record** | `POST /freeze {"frozen": false}` — continues from the halted state, nothing re-simulated |
| **playback speed** | the dropdown is the ONLY rate control: `1×` = one **sim**-second of tape per wall second, so a tape recorded at `--warp 4` replays at the pace the run would have had in reality. The rate comes from the header's `dt_fixed`, never from the wall clock. If the head advances more slowly than the cursor, the cursor **waits at the head** — never a jump, never an extrapolation (user decision 2026-10-25) |
| **single click ◀ / ▶** | exactly **one frame** |
| **hold ◀ / ▶** | the rate ramps while held and ramps back on release: `0.0 s → 1`, `0.3 s → 6`, `0.8 s → 20`, `1.5 s → 60`, cap `120` frames/s. Same curve both directions. A held scrub **releases `▶ play`** (like a bar drag and `,`/`.`): a step sequence and a lerp cannot share one cursor |
| **`,` and `.`** | one substep back / forward — the two keys **right of M** on a German layout. Bound by **physical position** (Godot `keycode`, not `unicode`), so the gesture survives other layouts |
| **right-click a car** | opens the **car detail window** menu: one entry per open window (picking one hands it the clicked car) plus `New slot`. The ONLY gesture that opens or re-points a window — a left press does not select anything any more. [CAR_DETAIL_WINDOW.md](CAR_DETAIL_WINDOW.md) |
| **right-click an event row** | copies that event's whole address to the clipboard: `tape → #uid [role] → #step  seconds  text`, so a line you want to ask about carries its run and its car with it |
| **↑ / ↓ / ◎ under the list** | page the event list through the car's history and back to the event it is acting under at this frame — paging is in the status line because a footer at either end of a centred list is off-screen |
| **bar click** | a click or a **drag on the scrub bar IS a seek**: x maps linearly onto the tape's substeps, so the moment you aim at is the moment you get. It **pauses** ▶ play — a lerp one frame past your click is not that frame (`ReviewScrubZone` → `ReviewSeek`) |
| **playback rate box** | an `OptionButton` (0.5x … 8x). Choosing a rate sets the rate **only** — the old row of five buttons each did `_playRate = r; _playing = true`, so asking "what does 4x look like" started the tape running. ▶ is what plays |
| **guards** | the cursor never passes the **tape head** (that would invent), never below step 0 |

## Scrub bar and the chart above it

The chart shows the fleet's **fluidity over time** — the per-substep value the
tape carries (`fluidity_mps_per_car`, m/s per car). The curve is what makes two
runs comparable; the markers are what makes one run explainable.

| marker | source | shape |
|---|---|---|
| **blue line** — wrong lane | the per-car `wrong_side` flag on every tape frame (the guard's own verdict, R33) | a line where a car entered it, thick while it stayed |
| **red line** — crash | `crash_episodes` incrementing between adjacent frames | a line at the step of the fresh contact |
| **yellow block** — longer standstill | a car's `speed_mps` below threshold across consecutive frames | a **block** — a standstill has a duration, the other two are instants |
| decision steps | the tape's event index | faint hairlines under the curve, so the three categories sit above a decision timeline |

**Standstill block (chart definition, not a rule):** a maximal stretch where a
car's speed stayed below **0.5 m/s** for at least **2 s of sim time**. Named
constants in the reviewer, labelled in the legend as a *chart reading* — the
sim's own R31 standstill error uses a 180 s wall window and must not be
confused with it.

The curve is **fleet-level**; the markers are **per-car**, so clicking a car
opening a car detail window filters its markers onto the marker lane and
leaves the curve alone (the marker lane and the chart draw for the cars that
have a window — `_selected` is derived from the open windows).

## Three car slots

> **RETIRED 2026-10-24.** The three-slot row is gone; the reviewer opens a
> **car detail window** per car (`CarDetailWindow.cs`) — unlimited windows,
> right-click to open, the real chain, a scrolling event list, resizable with
> only the list growing. Spec and measured evidence:
> [CAR_DETAIL_WINDOW.md](CAR_DETAIL_WINDOW.md). The section below is kept as the
> history of the instrument it replaced.

| panel row | content | already available |
|---|---|---|
| identity | sprite colour → vehicle class (`Config.VEHICLE_CLASS`) | yes |
| size | footprint (`Config.VEHICLE_SIZES`), wheelbase | yes |
| now | speed, braking, blinkers, pose at the cursor | on the tape |
| negotiation | **who blocks me** / **whom I block** | the cap key names the other uid (`avoid#1`, `r32#2`, `passage#4`, `sign#C#7`); the reverse is a scan over ≤ 66 cars — free |
| decisions | the decision list with a cursor marker at the current step | needs the tape (below) |

## The data gap this exposed (closed 2026-10-22)

`Car.Decisions` is capped at **100 entries** and lives in car memory, so a long
run overwrites its own early history — a scrub to step 200 would show a list
that no longer contains step 200, and the panel would lie.

**Done:** the decision text is recorded **on the tape** at the event steps
(`…;~uid=decision text~…`, `RunTape.Save`), exported by `POST /tape/frame` as
`events[]`, and parsed by the reviewer's tape reader (third format generation).
The 100-entry cap now only protects live memory; a run loaded from disk keeps
its full history. See [RECORD_PLAYBACK.md](RECORD_PLAYBACK.md).

## Per-scenario framing: the recording re-claims the view at every boundary

The review camera's precedence is **per scenario, not per session**:

1. the hand — a map drag, a minimap pin, a panel CENTRE, **and a panel's
   follow toggle**. Following is not a tier of its own: it IS the hand's pin,
   re-anchored to that car's pose at every frame. That is what makes it beat a
   place the hand pinned before it, keeps the operator's drag as an OFFSET on
   top of the car instead of losing it, and stops the view jumping when the
   follow is released. (Before this it was tier 1 *above* the pin and merely
   cleared it — measured 2026-10-24: with the readout's **pin** checkbox held, `▶`
   moved the tape but not the camera and the follow did nothing at all:
   `src=… cam=… · cam: fixed (pinned) · view: tape pin`.)
2. `--center` / `--review-name` from the command line,
3. **what the scenario declared** (`# focus …` on the tape),
4. the cast centroid (a tape with no declarations).

### Zoom has its own order (#12)

The list above says **what the camera follows**; the SCALE is a separate axis,
and it had a bug: the reviewer's default car-fit (`FitViewOnCar`) wrote
`_cam.Zoom` unconditionally, so an operator who asked for a scale lost it
without a word. The order, as the code now holds it:

1. a declared focus that carries its **own** `zoom=` — framing written into the
   recording, so it outranks the command line,
2. `--zoom`, or the crash scale `--focus <car>` implies (34 px/m),
3. the default car-fit (`viewport / CarFocusViewM`, clamped),
4. #7's opening whole-map fit — which already stood aside for an asked `--zoom`.

The hand stays above all of them *in time*: the fit is applied once per focus
label (`ApplyFocusZoom` returns early for one already framed), so a wheel zoom
or a drag after it is never undone. Measured on `tests/tape_T_315` at frame
1100, inside a `kind=car` focus that declares no zoom, and at frame 2200 inside
`kind=loc … zoom=26.00`:

| asked | camera answered | before the fix |
|---|---|---|
| `--zoom 6` (car focus, no declared zoom) | `zoom=6.0` | **23.4** — the fit won |
| nothing | `zoom=23.4` | 23.4 (unchanged) |
| `--focus 2` (implies crash scale) | `zoom=34.0` | 23.4 — same fault, same line |
| `--zoom 6` at a focus declaring `zoom=26.00` | `zoom=26.0` | 26.0 (unchanged, correctly) |

Crossing a boundary runs `FocusHandoff()`: the hand's pin, its drag-pan and the
command-line centre are **released**, the declared zoom is applied once, and the
follow item becomes the new scenario's subject. It used to latch: one stray map
drag pinned the view for the rest of the session and no later scenario's focus
or car was ever applied again (measured live: `src=declared PLACE '17/27'` →
`src=the hand (pin)` → never back). The hand still wins for as long as the
operator is looking at *that* moment.

Two rules the reports of 2026-10-24 forced into the open:

* **The declared zoom is a framing, applied once at the handoff.** The
  declaration used to write `_cam.Zoom` on EVERY frame, which is what "zoom in
  the main window sometimes does not work. I need to zoom in the minimap, and
  only after that, it also works" describes exactly: a wheel notch was
  overwritten 16 ms later, and a minimap zoom happens to PIN the view (hand >
  declaration), which stopped the re-assertion and "unlocked" the map wheel. The
  subject's POSITION is still re-derived every substep — that is what a follow is.
* **A drag anchors a pin.** A grab used to add a pan offset while the state
  still read `FOLLOWING uid=22`, so the picture showed the car 140 px off centre
  under a label that promised it was centred ("the followed car is NOT in the
  center of the screen"). Now a drag makes the view the hand's (`src=the hand
  (pin)`), and "following ⇒ the subject is centred" holds as an invariant until
  the next boundary hands it back.

`--test-focus-probe` scripts the whole chain — play across a boundary, wheel-zoom
the map, drag through the real input path — and prints `offCentre=<px>` next to
`src=`, `pan=`, `mmAsk=`, `zoom=` for every step. While a follow drives the view
it reads `offCentre=0 px`; a non-zero value appears only once `src` says the hand
has it.

## The Test panel — what is being watched, and how it ended

One more window (`test`, an OS window like the readout and the car detail windows). Its
content follows **the scenario the cursor is inside**, so scrubbing into the next
test turns the page:

```
Test 17/27 · park_block_overtake
✅ PASSED · 10.6 s sim · 2.5 s wall · 2 centreline crossing(s) · 0 off-road
A parked car blocks the lane - drive around it (R32 overtake: out into the free
lane, past the obstruction, back to the right)
focus: the PLACE (100, 275) m @ 26 px/m — no car followed
```

It sits in the **top-right column, stacked under the minimap** (both are
transient OS windows of their own, and nothing orders them: parked in the
top-left they came out buried under the readout and a car window — a window whose
whole job is to be read, unreadable).

Two different nothings are named apart, because they call for different actions:
`Between scenarios — no test at this frame` (scroll a little) versus
`Test (this tape records no scenarios)` (a run recorded before the feature).

All four lines come from the **tape** (`# focus …` + `# result …`, see
[RECORD_PLAYBACK.md](RECORD_PLAYBACK.md)) — no host, no log file, no re-run. It
rewrites only when the scenario or the verdict changes: the text is a paragraph,
and re-laying it out at 60 Hz is the per-frame churn this client has already been
burned by. With no declarations on the tape (an older run) it says so rather than
showing a wrong scenario.

## Scenario boundaries on the bar

A 26-scenario run is ONE curve, and without boundaries "the crossing test" has no
place on it. The bar now carries **a tick per announced scenario at its top edge**,
labelled with the scenario number where the pixels allow it (46 px apart minimum —
26 labels across a wide bar would be white noise):

```
17 |              18 |                    19 |
   ── curve ──────────  decision hairlines ──  standstill bands ──
```

The ticks are **children of the instrument group**, so they fade out with the bar
and rebuild with it. (They were first added to the bare HUD layer; the screenshot
showed three ticks floating over grass with no bar under them.)

## Zooming the bar (mouse wheel)

**Wheel over the bar zooms the bar's TIME RANGE** — stretching it, so a decision
hairline that was 0.3 px wide becomes 4 px and two events 200 substeps apart
separate. The MAP's zoom is a different question and is untouched: a wheel
outside the strip still zooms the map.

| gesture | effect |
|---|---|
| wheel up over the bar | range ×⅟₁.₃₅ about the pointer (more detail) |
| wheel down over the bar | range ×1.35 (less) |
| **double-click** the bar | back to the whole run |
| minimum range | 1/400 of the run (≈ 1 s of a 43 s tape) |

The point under the cursor stays under the cursor while the range stretches.
Everything on the bar is drawn through ONE mapping (`BarXOf` / `BarIndexAtX` /
`BarTapeFracAtX`) — curve, cursor tick, head tick, marker lane, axis ticks,
boundary ticks and the seek zone — because a zoom that leaves one layer on the
unzoomed axis points at the wrong second. The axis note names what is on screen
(`2.4–26.1 s of 43 s sim · 12 substeps/tick · wheel=out, double-click=whole run`),
and if the cursor has fallen outside the range the readout says which way to go
instead of letting a clamped tick pretend (`cursor 11352 is RIGHT of this range`).

The curve is also **sampled through the visible range** with a stride of about one
point per half pixel, so zooming in buys resolution instead of drawing 35 000
invisible vertices.

## Event markers: tooltip, and a hit area you can actually aim at

The markers (decision hairlines, crash and wrong-side marks, per-car and
fleet-level standstill bands) now answer **"what happened there"**:

* **Hover** shows a tooltip above the bar with the tape's own decision text, the
  frame and the sim time — `car 97: brake: [R7] car 83 ahead (gap 25.1 m) · frame
  21 402 · 356.70 s SIM — click to jump there`.
* **Click** moves the cursor to that substep (and stops playback).
* **The hit area is padded `MarkerHitPadPx` = 7 px on either side.** A 1.5 px
  hairline is correct to *draw* and impossible to *hit*; the picture stays thin and
  the target grows.

Two subtleties the measurements caught: the hit ranges and the quads are built in
the **same pass and the same space** (a hit list computed against the unzoomed axis
would jump to a different second than the mark you clicked), and `_GuiInput`
reports **window-local** coordinates while the bar is laid out in the main
window's space — the 16 px difference was exactly enough for a click dead-centre
on a marker to seek instead of jumping (`Gx`/`G()` add it back).

## Connection layer (both clients)

A dead host is a **connection state, not a process state** — neither client
restarts.

| client | while disconnected |
|---|---|
| viewer | nothing to draw — overlay, last frame greyed |
| reviewer | **keeps working**: it caches the tape it loaded client-side, so a loaded run survives the host dying |

Overlay text (English, per the project's language rule):

```
⚠  No connection to Sim Engine
   retrying in 2 s …        [retry]  [load a saved run]
```

- **backoff, not spam**: poll `/health` on a bounded schedule (1 s → 2 s → 5 s,
  capped); wait for the condition, never a blind fixed sleep
- **the port may change**: reconnect means re-discovering the host, not
  assuming the old port — the runner takes a free port for a second client
- **its own generation file**: `scripts/run_e2e.sh` closes Godot windows by
  bumping `/tmp/driving_game_godot_gen`; the reviewer must not watch the same
  one, or every e2e run would shut it down. Never a signal — a SIGTERM pops a
  macOS crash report

## The readout label (binding user decision, 2026-10-21; format re-read from the code 2026-10-23)

One label, always visible, exact — never smoothed, never an estimate.
**Three lines** (`ReviewReadout` in `MapRenderer.review.cs`) that together make
a **screenshot alone** enough to reconstruct everything — this is the one place
the format is written down, built from the format strings:

```
tape_T_240_0_20261022_032831.txt · frame 743 [42/828] · SIM 12.383 s
cam x 98.25 m  y 275.00 m · zoom 18.0 px/m · view: step (exact substep) · units: meters
map basic · hash: segs=256 signs=4 … · fluidity 6.93 m/s/car (window 5.00/5.00 s) · ⏸ step
```

| part | meaning |
|---|---|
| `tape_…txt` | **which run** — and through its header (`map=`, `dt_fixed=`, `steps=`) which map and which clock unit |
| `frame 743` | the **substep index** — the tape's clock, the one number everything else keys on. It is the tape's own `step`, 1-based like its range |
| `[42/828]` | cursor position / tape length, so "how far into the run" is readable without a calculator. Both are 1-based, so `frame 1 [1/3768]` cannot be misread as index 0 of 3767 |
| `SIM 12.383 s` | `frame × dt_fixed` — **sim time, not wall time**, so a `--warp 4` recording and a 1× recording are comparable on the same axis |
| `cam x … m y … m` | the reviewer's **own** camera centre, in **sim** metres (Godot's 2D y grows down, so the label flips it back — the number must be quotable into `/state`) |
| `zoom … px/m` | the scale of the crop (see below) |
| `view: step (exact substep)` / `play (lerp between recorded frames)` | whether the drawn pose is a real substep or a blend of two — a pixel-level reading is only honest with this stated |
| `units: meters` | the project's rule: every coordinate payload declares its unit |
| `map … · hash: …` | the map the tape was recorded against — a tape and a different network are meaningless together |
| `fluidity … m/s/car (window w/5.00 s)` | the frame's fleet fluidity **and the window it actually averaged** — at the head of a tape there is no 5 s of history yet, so the window shrinks instead of the number reading 0.00 for a fleet at 49.9 km/h |
| `▶ 4x` / `⏸ step` | the current playback mode and rate |

The **head of the tape is a tick on the scrub bar, not a number in the label**
(`_headTick`) — the newest recorded substep is where the grey tick sits, and the
cursor must not pass it. Putting it in the text would have made one label carry
two clocks.

The camera line is not decoration. A screenshot is a **crop at a scale**:
without `x y zoom` there is no way to know which part of the world is shown, and
no way to convert a painted length back to a real one. With the zoom announced,
`pixels / zoom = metres` — the pattern [lights_report.md](lights_report.md)
already uses for marking widths.

The **viewer** carries no such label: its 100 ms render delay
(`RenderDelaySec`) is an internal target, visible only in the
`--motionlog` line (`… _simNow rtClamped renderTarget …`), which is the way to
measure how far the live picture trails. That asymmetry is deliberate — in the
reviewer every number on screen describes a file that is not changing.

The **frame number is the address of a screenshot and of a question.** "Frame
743" identifies one moment in one file; no timestamp rounding, no wall clock.

## Screenshot of the whole reviewer (binding user decision, 2026-10-21)

A button captures the **entire reviewer window** — map panel, the three car
panels, the fluidity chart, the scrub bar, the readout label — as one PNG.

| | |
|---|---|
| name | `review_<tape-name>_f<frame>.png` — e.g. `review_tape_T_145_17_20261021_190356_f743.png` |
| where | next to the tape it belongs to (`tests/`) |
| **clipboard** | the button copies the PNG's **path** — text only, see *Clipboard: text only, settled* below. The image itself is NOT on the clipboard (the image clipboard is not reachable from this toolchain) |
| why the name matters | the image is **self-describing**: the tape file, the frame, and (because the label is inside the shot) the sim time and mode. A screenshot alone can be re-derived later from the tape at that frame |

Godot's own idiom applies: the window's root renders into a texture that is
saved whole (`--screenshot` in `MapRenderer` already proves the path exists —
`SavePng` on the viewport texture). Never `--headless` for a shot: the dummy
renderer has no viewport texture.

## Why the tape is the better artefact for discussion

A screenshot shows one moment; the tape answers the question behind it. Both
together are what a situation needs:

| from the tape | what it gives |
|---|---|
| every substep, every car | exact poses — no interpolation, no 100 ms delay |
| `capKey` per car | **why** a car was held, and by whom — the negotiation is readable, not inferred |
| `fluidity_mps_per_car` + `moving_share` | how the fleet behaved over time, and how it changed |
| decision text at event steps (once on the tape) | the driver's own reasoning at that frame |
| the header (`map`, `dt_fixed`, `steps`) | the file is self-describing offline |

So: pass a tape and a frame number, and the situation can be reconstructed
exactly — the screenshot picks the view, the tape supplies the truth.

## Implementation order



1. `draw(snapshot, interpolate)` in the renderer
2. tape carries decision text at event steps
3. the **readout label** — frame number + sim time + head — because every later
   step addresses moments by it
4. the reviewer client: controls, three slots, chart + markers, tape cache,
   connection overlay
5. the whole-window **screenshot** button, named against the tape and the frame
6. the viewer's connection overlay (same layer)

## Review-list fixes (2026-10-22)

The user reviewed a screenshot of `tape_T_240` and named what was wrong. Five
items plus the marker lane, all fixed, each verified on a screenshot:

| # | what was wrong | the cause | the fix |
|---|---|---|---|
| 1 | the top of the screen: two areas overlapping, unreadable | **two** `Label`s with their own translucent boxes — the wrapped three-liner at y=12 and the camera line at y=34, so the second sat ON the first (measured (21,43,25) over (29,63,29) over grass) and the box height was a guess (4 × font + 14) | ONE `Label`, THREE fixed lines, ONE box sized from the laid-out text; the font shrinks only as far as the **widest line** needs to end before the map window — measured with `Font.GetStringSize`, after a 0.50 em/char estimate proved ~15 % optimistic |
| 2 | the map inset did not fill its window | `MinimapNode` was a `Node2D` that drew a fixed 360 px square at `(vp.X − 360 − 15, 15)` of the viewport it rendered in — inside its own window that left a 43 px empty band and stretched a 2470 × 1950 m world into a square (two different axis scales) | `MinimapNode` is a `Control` that draws into **its own rect**, and the window is sized **from the map's aspect** (`MmBoxFor`: `BoxSize` on the long side). The view marker now measures the MAIN window (`GetTree().Root`), not the subwindow's 390 px viewport — the old reading made the rect ~7× too small, so it always fell through to the crosshair |
| 3 | the scrub bar was read-only — no way to jump to a position | the curve had no input surface at all: only the buttons and `,`/`.` moved the cursor | `ReviewScrubZone` (`MouseFilter.Stop`) over the curve band, below the button row: click seeks, drag scrubs. Verified with `--test-seek 0.25`, which pushes a real `InputEventMouseButton` through the viewport: `REVIEW-SEEK click frac=0.250 -> frame 1439/5752` |
| 4 | buttons of five different sizes; "open a tape" in words; the speed buttons started playback | font 38 / 38 / 24 / 28 and three different box heights in one row; `Text = "open a tape"` (the eject glyph is not in the font); each rate button did `_playing = true` | one row, one box (66 × 58 × `hudS`), one font (32 × `hudS`), `📂` with a tooltip, and an `OptionButton` for the rate that only calls `SetPlayRate`. `FocusMode = None` on the transport buttons, so a click does not steal the scrub keys |
| 5 | the car-info box was not a real window | the slots were `Panel`s — and the earlier attempt at windows had placed their children in MAIN-window coordinates *inside* the subwindow's viewport, which is why they "came out scattered" and "opened empty" | three real `Window`s, title bar naming the car (`#9 slow_car · compact · 3.14 × 1.71 m`), each closable (its × stops that slot until a new car is picked), draggable, resizable — the text reflows because it is anchored inside the window and sized from `Window.GetVisibleRect()` |
| 8 | the marker lane floated mid-screen, over the map | `ReviewMarkers` cached on the **selection** alone, so after a maximize the quads kept the `barBase` of the 900 px window the run started in (measured at y≈832 of 1558) | the cache key carries the viewport size; and the whole HUD is rebuilt when the window changes size (`RebuildReviewHud`, logged: `REVIEW-HUD rebuilt for (2400, 1300) (hudS 1.59)`), which also fixes the stale `_hudS` that made a maximized window print 12 px text |

Still open from that review (NOT fixed):

| # | defect |
|---|---|
| 6 | the picture is not the frame the readout claims: at 8x the blue car was drawn 822 substeps stale while the label said `frame 5752` — the display-truth promise broken at the head |
| 7 | `_selected` is sticky: when the selected uid despawns, nothing re-seeds it, so a slot reads `#1 (not on this frame)` for the rest of the run while cars are on screen — and nothing shows WHICH car is selected |

Two things the same pass surfaced and fixed because they sat in the code the
items above touch:

- **`--review` without a path loaded nothing.** `NewestTape`/`ListRuns` returned
  `res://tests/…` and `LoadTape` reads with `System.IO.File`, which does not
  know Godot's prefix → "no tape readable", and every entry of the run picker
  was a click that loaded nothing. Both now return `GlobalizePath` paths.
- **the map window did not follow a resize** — its `Position` is absolute, so
  after a resize it sat in the middle of the top edge, on top of the readout it
  must not cover. `PinMapWindow` re-pins the corner on a size change (and only
  then: a window dragged sideways keeps its spot at a size that does not change).
- **the labels stacked under the inset were placed outside their own window**
  (`LayoutUnderMinimap` asked the MAIN viewport for its width while the labels
  are children of the map window), so whatever they had to say never reached
  the screen. Coordinates are window-local now, hanging under the caption row.

## One fixed HUD size (binding user decision, 2026-10-22)

**The reviewer draws its HUD at ONE size, whatever the window is.** No
`hudS` any more: the review client has no scale factor read off the window
height, and every HUD number is a named constant (`ReadoutFontPx` 28,
`AxisFontPx` 20, `ButtonFontPx` 32 + a 66 × 58 box, `PickerFontPx` 23,
`SlotFontPx` 26). A maximized window shows the same readable text as the
900 px one, sitting in the corners instead of filling the screen.

The rows above that mention `× hudS` are history — they describe the build
that introduced the scale, which this decision supersedes.

The one number that may still vary is the **readout font, and it may only
shrink**: its three lines do not wrap (`AutowrapMode.Off`), so in a window
narrow enough that the widest line would run under the map window the font
gives in until the box ends before that frame. Growth is capped at
`ReadoutFontPx`, never beyond it — it is a collision guard, not a scale
(logged as `REVIEW-FONT size=… want=28 fixed`).

Dev hooks added on the way: `--resize <w> <h>` (grow the window a moment after
start — the only scripted way to reproduce "HUD after maximize") and
`--test-seek <frac>` (push a click into the bar), both in the family of the
existing `--test-pan`.


## Implementation status (2026-10-21)

Built and verified on screen in `MapRenderer.review.cs` (as of 2026-10-22 the
rows marked ⚠ describe the state at that date — the *Review-list fixes* table
above says what replaced them):

| feature | evidence |
|---|---|
| scrub bar + six buttons ▶ ⏸ ⏹ ● ◀ ▶▶ | inside the bar, dark strip |
| ⚠ playback-rate buttons ½× 1× 2× 4× 8× | on the bar (rate was keyboard-only before) — replaced by the rate dropdown 2026-10-22 |
| ⚠ two-line readout | tape · frame [cursor/len] · SIM time · map + hash · fluidity · mode / cam x y zoom · view · units — three lines in ONE box since 2026-10-22 |
| ⚠ three detail slots | identity → class → per-class footprint → speed/brake/blinker → pose → who holds me / whom I hold → decision trail — three real WINDOWS since 2026-10-22 (identity in the title bar, footprint on the second line of it). **RETIRED 2026-10-24** — replaced by [CAR_DETAIL_WINDOW.md](CAR_DETAIL_WINDOW.md): window per car, no cap, right-click to open, footprint dropped, the `held_by` chain instead of the EndsWith guess, a scrolling event LIST instead of one collapsed line. Session memory (geometry, cursor, camera) is the open part |
| decision trail | count **plus the last two texts** with their frame numbers, clipped to the panel |
| chart | cyan fleet curve, **yellow standstill blocks** (fleet-level band + per-car lane), **decision hairlines** under the curve, white cursor tick |
| run picker | six newest tapes listed under the minimap; clicking one reloads that tape, rewinds, re-points the camera |
| tape reader | accepts **three format generations** (`step;cars`, +fluidity/moving, +decisions) — old runs stay reviewable |
| camera | pinned to the tape frame centroid, or `--center`; auto-follow off in review mode |
| live UI | hidden in review mode |

Not available in this build: the **image** clipboard. `DisplayServer.ClipboardSet`
takes text only, so the screenshot's path is copied instead and the shot is
self-describing (name + frame + camera inside the image).

Known intermittent: **#20 `slow_pass_priority`** — the live timeout in every
full run on record (`T_231`, `T_242`: 240 s of sim, 1.7 m/s per car, never
reaches the end flag). An earlier batch run also had `park_block_priority`
(#18) timing out, before the cap side of the clearance relation landed (§7g of
`RIGHT_OF_WAY_NEGOTIATION.md`); #18 has passed since (17.0 s in `T_242`), so
do not chase that one. See [TESTING.md → "Current baseline"](TESTING.md#current-baseline-the-one-place-a-number-is-written).

## Named selection (2026-10-21)

```
--review-name slow_car        # the reviewer selects by ROLE, from the tape header
```

The named car is what **the camera pins on for the whole scrub** — owning no
slot (the slots are retired, see [CAR_DETAIL_WINDOW.md](CAR_DETAIL_WINDOW.md)),
it only aims **its**
pose rather than the cast centroid — a review of "the pass of the parked car"
stays on the parked car at every frame. Same handle as the live client's
`--focus <name>`; the reviewer resolves it from the header's `names=` map
instead of from the live state.

Verified:

```
REVIEW-CAM godot=(98.25,-232.50) zoom=14.0 tape-centroid=(98.25,232.50) cars=3 frame 5001
#9 slow_car silver · compact        #10 oncoming_car police · car
3.14 × 1.71 m                       4.71 × 1.86 m
speed 10.0 km/h · brake -           speed 80.9 km/h · brake -
x 98.25  y 232.50  h 180.0°         x 101.75  y 143.90  h 0.0°
holds me: car 8 (avoid#9)           holds me: -
I hold:   -                         I hold:   -
```

Slot text once **wrapped inside the panel** (`AutowrapMode.WordSmart` +
`ClipText`) instead of spilling into the next slot — the car detail window
replaced wrapping with a **scrolling list** (2026-10-24), so a long decision
line scrolls sideways instead of pushing the layout. And the decision line
carries one entry
plus the count.

## Clipboard: text only, settled (2026-10-21)

The screenshot copies its **path** to the clipboard, not the image. That is
not a missing binding to work around - the image clipboard is not reachable
from this toolchain, and the attempt log says so:

| attempt | result |
|---|---|
| `DisplayServer.ClipboardSetImage` / `SetClipboardImage` | not in this build's C# bindings |
| `swiftc` helper using Cocoa's `Clipboard` class | `Cocoa/Clipboard.h` is **not in the macOS SDK** (`find` over the SDK returns nothing); `NSClipboard`/`Clipboard` are not in Swift's `Cocoa` module |
| `osascript … read (file …) as picture` | fails: `Can't make file … into type file` (-1700); the PICT path was removed from modern macOS |
| `pyobjc` | not installed |

The shot is therefore **self-describing** instead: tape name, frame, camera
and the label live inside the PNG, so the path on the clipboard plus the
image is enough to reconstruct the situation exactly.
