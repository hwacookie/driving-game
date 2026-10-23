# Simulationsreviewer — the review client

Status: **built and in use.** Specified 2026-10-21, implemented in
`MapRenderer.review.cs` (a second window of the same client), and revised on
2026-10-22 against an on-screen review list — see *Review-list fixes*
below. Every decision in this document is a binding user decision; where the
code has since diverged, the divergence is recorded there. The tape, the
endpoints and the per-substep fluidity it reads are built — see
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
| ⚠ three detail slots | identity → class → per-class footprint → speed/brake/blinker → pose → who holds me / whom I hold → decision trail — three real WINDOWS since 2026-10-22 (identity in the title bar, footprint on the second line of it) |
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
