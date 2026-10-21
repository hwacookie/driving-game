# Simulationsreviewer — the review client

Status: **specified 2026-10-21, not implemented.** Every decision below is a
binding user decision; the code does not exist yet. The tape, the endpoints and
the per-substep fluidity it reads are already built — see
[RECORD_PLAYBACK.md](RECORD_PLAYBACK.md).

## Two clients, one window each

| | what it is | source | lifetime |
|---|---|---|---|
| **Simulationsviewer** | the Godot window we have today (`MapRenderer`) | the live sim | rebuilt only when the renderer changes |
| **Simulationsreviewer** | its own client, its own window | a **tape frame at a cursor** — live or loaded from disk | long-lived; never restarted for a new run |

They are independent. The reviewer holds **no simulation state** — everything
comes over the API — so a new run is a new tape, not a new process.

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
| **playback speed** | advance the cursor by `speed / dt_fixed` substeps per wall second; the rate comes from the header's `dt_fixed`, never from the wall clock, so a tape recorded at `--warp 4` replays at the correct speed |
| **single click ◀ / ▶** | exactly **one frame** |
| **hold ◀ / ▶** | the rate ramps while held and ramps back on release: `0.0 s → 1`, `0.3 s → 6`, `0.8 s → 20`, `1.5 s → 60`, cap `120` frames/s. Same curve both directions |
| **`,` and `.`** | one substep back / forward — the two keys **right of M** on a German layout. Bound by **physical position** (Godot `keycode`, not `unicode`), so the gesture survives other layouts |
| guards | the cursor never passes the **tape head** (that would invent), never below step 0 |

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
slot filters its markers onto the marker lane and leaves the curve alone.

## Three car slots

Up to **three detail panels**, the three last-clicked cars; **until anything is
clicked, the first three cars by uid** — the panels are useful before the first
click.

| panel row | content | already available |
|---|---|---|
| identity | sprite colour → vehicle class (`Config.VEHICLE_CLASS`) | yes |
| size | footprint (`Config.VEHICLE_SIZES`), wheelbase | yes |
| now | speed, braking, blinkers, pose at the cursor | on the tape |
| negotiation | **who blocks me** / **whom I block** | the cap key names the other uid (`avoid#1`, `r32#2`, `passage#4`, `sign#C#7`); the reverse is a scan over ≤ 66 cars — free |
| decisions | the decision list with a cursor marker at the current step | needs the tape (below) |

## The one data gap this exposes

`Car.Decisions` is capped at **100 entries** and lives in car memory, so a long
run overwrites its own early history — a scrub to step 200 would show a list
that no longer contains step 200, and the panel would lie.

**Fix:** record the **decision text at the event steps on the tape**. Decisions
are rare events, not per-substep data, so this is cheap; the 100-entry cap then
only protects live memory, and a run loaded from disk keeps its full history.

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

## The readout label (binding user decision, 2026-10-21)

One label, always visible, exact — never smoothed, never an estimate:

Two lines — the first addresses the moment, the second addresses the view.
Together they make a **screenshot alone** enough to reconstruct everything:

```
tape_T_146_17_20261021_202204.txt · frame 743/820 · SIM 12.383 s · ▶ 4× · head 820
cam  x 98.25  y 275.00  zoom 18 px/m   ·   view: step (exact)   ·   units: meters
```

| part | meaning |
|---|---|
| `tape_…txt` | **which run** — and through its header (`map=`, `dt_fixed=`, `steps=`) which map and which clock unit |
| `frame 743/820` | the **substep index** — the tape's clock, the one number everything else keys on |
| `SIM 12.383 s` | `frame × dt_fixed` — **sim time, not wall time**, so a `--warp 4` recording and a 1× recording are comparable on the same axis |
| `▶ 4×` | the current playback mode and rate |
| `head 820` | the live sim's newest recorded substep — the line the cursor must not pass |
| `cam x y zoom` | **the reviewer's own camera** — its centre in metres and its scale in px/m |
| `view: step (exact)` | whether the drawn pose is a real substep or a lerp between two — a pixel-level reading is only honest with this stated |
| `units: meters` | the project's rule: every coordinate payload declares its unit |

The camera line is not decoration. A screenshot is a **crop at a scale**:
without `x y zoom` there is no way to know which part of the world is shown, and
no way to convert a painted length back to a real one. With the zoom announced,
`pixels / zoom = metres` — the pattern `docs/lights_report.md` already uses for
marking widths. The viewer states its own trail instead: `live head 820 · drawn
814`, so the 100 ms it takes to hide network jitter is never hidden behind a
number that looks current.

| part | meaning |
|---|---|
| `frame 743` | the **substep index** — the tape's clock, the one number everything else keys on |
| `/ 828` | the tape length, so the position in the run is readable |
| `SIM 12.383 s` | `frame × dt_fixed` — **sim time, not wall time**. A tape recorded at `--warp 4` replays at the same sim speed as one recorded at 1×, so this number is comparable across runs |
| `▶ 4×` | the current playback mode and rate |
| `head 828` | the live sim's newest recorded substep — the line the cursor must not pass |

The **frame number is the address of a screenshot and of a question.** "Frame
743" identifies one moment in one file; no timestamp rounding, no wall clock.
The viewer gets the same label for its live head, and states its render delay
explicitly (`live head 828 · drawn 822`) so the 100 ms it trails is never
hidden behind a number that looks current.

## Screenshot of the whole reviewer (binding user decision, 2026-10-21)

A button captures the **entire reviewer window** — map panel, the three car
panels, the fluidity chart, the scrub bar, the readout label — as one PNG.

| | |
|---|---|
| name | `review_<tape-name>_f<frame>.png` — e.g. `review_tape_T_145_17_20261021_190356_f743.png` |
| where | next to the tape it belongs to (`tests/`) |
| **clipboard** | the button **also copies the PNG to the system clipboard**, so a frame can be dropped into a conversation without a path round-trip |
| why the name matters | the image is **self-describing**: the tape file, the frame, and (because the label is inside the shot) the sim time and mode. A screenshot alone can be re-derived later from the tape at that frame |

Godot's own idiom applies: the window's root renders into a texture that is
saved whole (`--screenshot` in `MapRenderer` already proves the path exists —
`SavePng` on the viewport texture). Never `--headless` for a shot: the dummy
renderer has no viewport texture.

Clipboard copy is Godot's own call (`Image.copy_to_clipboard()` on the same
image that is written to file) — one image, two destinations, no re-render.

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

## Implementation status (2026-10-21)

Built and verified on screen in `MapRenderer.review.cs`:

| feature | evidence |
|---|---|
| scrub bar + six buttons ▶ ⏸ ⏹ ● ◀ ▶▶ | inside the bar, dark strip |
| playback-rate buttons ½× 1× 2× 4× 8× | on the bar (rate was keyboard-only before) |
| two-line readout | tape · frame [cursor/len] · SIM time · map + hash · fluidity · mode / cam x y zoom · view · units |
| three detail slots | identity → class → per-class footprint → speed/brake/blinker → pose → who holds me / whom I hold → decision trail |
| decision trail | count **plus the last two texts** with their frame numbers, clipped to the panel |
| chart | cyan fleet curve, **yellow standstill blocks** (fleet-level band + per-car lane), **decision hairlines** under the curve, white cursor tick |
| run picker | six newest tapes listed under the minimap; clicking one reloads that tape, rewinds, re-points the camera |
| tape reader | accepts **three format generations** (`step;cars`, +fluidity/moving, +decisions) — old runs stay reviewable |
| camera | pinned to the tape frame centroid, or `--center`; auto-follow off in review mode |
| live UI | hidden in review mode |

Not available in this build: the **image** clipboard. `DisplayServer.ClipboardSet`
takes text only, so the screenshot's path is copied instead and the shot is
self-describing (name + frame + camera inside the image).

Known intermittent: `park_block_priority` timed out in an earlier batch run before
the cap side of the clearance relation landed (§7g of `RIGHT_OF_WAY_NEGOTIATION.md`).

## Named selection (2026-10-21)

```
--review-name slow_car        # the reviewer selects by ROLE, from the tape header
```

The named car owns **slot 1 for the whole scrub** and the camera pins on **its**
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

Slot text now **wraps inside the panel** (`AutowrapMode.WordSmart` + `ClipText`)
instead of spilling into the next slot, and the decision line carries one entry
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
