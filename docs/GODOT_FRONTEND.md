# Godot Frontend — Architecture & Migration Plan

> Status: agreed design decisions (2026-08-31), implementation now
> COMPLETE through Phase 1 + Phase 7 (in-process sim, embedded API). Phase 2
> (planning outside the simulator) is **not** done - route, raceline and speed
> profile still live in `DrivingGame.Sim`. Companion to `REST_API.md` (the API
> this document turns into a product boundary) and
> `SMOOTH_GEOMETRY_DESIGN.md` (the geometry contract).
>
> **Current state (2026-10-23, code-verified):** ONE repo, ONE app. The Godot
> 4.7 (.NET) project at the repo root hosts the client (`MapRenderer.cs`) and,
> in `--source embedded` mode, the simulator itself (`SimHost.cs`) on its own
> 60 Hz thread, with `DrivingGame.Api` embedded for external test injection.
> The HTTP double pipeline (`--source http`, the default) to the console host
> `DrivingGame.Server` on :5001 still coexists. Pygame is gone; the Python
> reference repo is history.
>
> **The same project also hosts the second client** — the Simulationsreviewer,
> `MapRenderer.review.cs` (a `partial` half of `MapRenderer` that opens its own
> windows and draws a TAPE frame instead of the live sim). It is not a phase of
> this migration; it is specified in
> [SIMULATIONS_REVIEWER.md](SIMULATIONS_REVIEWER.md) and its launch flags are
> listed there (`--review [path]`, `--review-at`, `--review-name`). Where §6/§7
> below say "the client", read "the viewer window" unless stated otherwise.

## 1. Goal

Replace the pygame frontend with a Godot 4 frontend (project:
`/Users/hauke/prj/driving-game/`, Godot 4.7, C#): faster rendering, and a
path to oblique/3D presentation later. The simulation — from now on called
the **world simulator** — stays the single source of truth for physics and
world rules. Nothing in this plan changes simulated behavior; e2e
(`tests/test_turning.py`) must stay green throughout.

The simulator is now C# (`DrivingGame.Sim`), not Python: the 1:1 port is
recorded in the port history (`diary/` + git log; the port spec itself was
retired 2026-10-23 once it had closed) and the post-port features in
`SPEC.md`.

## 2. Target architecture: three roles, one seam

```
                      REST API (127.0.0.1:5001)
┌───────────────────────────────┐       ◄──────────────────►       ┌─────────────────────┐
│  WORLD SIMULATOR (C#)         │                                   │  GODOT CLIENT       │
│  DrivingGame.Sim              │                                   │  rendering + input  │
│  owns all state, hosts API    │                                   │  zero driving logic │
└───────────────────────────────┘                                   └─────────────────────┘
                      ▲
                      │ same REST interface (or an in-process
          ┌───────────┴───────────┐     snapshot read, embedded mode)
          │  DRIVERS (any process │
          │  that holds the wheel)│
          │  • e2e suite          │
          │  • AI driver          │
          └───────────────────────┘
```

**World simulator.** Integrates the bicycle model at a fixed 60 Hz timestep;
enforces the world rules (off-road, wrong side, contact stop, physics
validator, lane guard). Receives only primitives. In the end state it does
**no** route/raceline planning (see Phase 2).

**Drivers.** Everyone who holds the wheel — the e2e suite, a human via Godot,
the AI driver — speaks the same small primitive interface:

- *Impulses*: accelerate, brake, steer left/right
- *Intentions*: blinker left/right, U-turn request, destination flag

There is deliberately no "turn left at the next junction" command. The
interface stays primitive so that (a) every scenario remains reproducible
headless ("reproduce first" workflow), (b) the "never do physically
impossible things" rule is enforced in exactly one place — the simulator —
and (c) drivers are interchangeable: a human, a test, and a future phone app
are the same kind of client.

**Godot client.** Pure renderer + input translator: keyboard → `/control`
impulses, blinker key → blinker intention; renders whatever the simulator
reports. It never feeds anything back into physics.

### Collisions stay in the simulator

Contact handling is not mere detection — it is *resolution* (brake at full
A_BRAKE + clamp so the body box never interpenetrates,
`ObstacleManager.apply_contact_stop`, inside the physics substep). It must run
where the state lives, at substep rate, and behave identically headless and
live — e2e asserts contact behavior. Godot's Jolt would be a second source of
truth for the same world; no engine physics is used for the player car. Godot
only *depicts* collision events from state flags (the sim reports what
happened, Godot decides how it looks).

## 3. Geometry: three layers, one contract

Builds on "one curve, all consumers" (`SMOOTH_GEOMETRY_DESIGN.md`): the
angular graph is topology only; the drivable geometry lives in
`SmoothedNetwork` (Catmull-Rom splines + circular junction fillets, resampled
at 0.5 m) and is already shared by physics and rendering today.

| Layer | Content | Consumers |
|---|---|---|
| **1 — Topology** | graph: nodes, edges, connectivity, oneway, lane counts (angular) | route planning ("which edge next?") |
| **2 — Drivable geometry** | `SmoothedNetwork`: splines + fillets, lane widths/offsets, paved area | the simulator's checks, the driver's raceline, Godot's rendering |
| **3 — Render presentation** | colors, textures, camera, oblique/3D view, props, sidewalks | Godot only |

**The rule: layer 2 is the contract.** Layer 3 must project the drivable area
of layer 2 faithfully; it may embellish only *outside* it. If Godot draws a
road that looks paved where the simulator says grass (or vice versa), the
player experiences "impossible" behavior even though the math is clean.

**Oblique/3D views are a camera decision, not a new model.** The world stays
flat and its frame **is metres** (x = east, y = north — there is no pixel
frame in the model, see [UNITS.md](UNITS.md)); Godot maps it into its XZ plane
and an oblique view is just a camera angle; road width becomes ribbon/extrusion
geometry along the same layer-2 polylines. The px/m scale belongs to the camera
(`Camera2D.Zoom`, chosen at draw time and announced as `--zoom`), not to the
world. Physics and model are untouched.
(Elevation/hills would be new data the simulator does not have — flat world
until that changes.)

## 4. The API becomes a product boundary

All existing endpoints stay as they are (the e2e suite keeps working
unchanged). Additions:

1. **`GET /map`** — layer-2 export, served once per map load: merged lines as
   dense polylines (the 0.5 m resample already exists and is cached), each
   with width, lane count, parking-lane width, oneway; junction fillets;
   world bounds; named start points. This is what Godot renders from and — in
   Phase 2 — what the external AI driver plans on.
2. **Freeze/unfreeze command** — ESC-pause exists only in-process today; a
   headless simulator needs an API way to pause.
3. State already carries what Godot needs (car x/y/heading/speed, blinkers,
   hazard, wrong side, off-road, parking state, HUD label, `frame`/`time` for
   interpolation) — no new fields expected; verify at implementation time.

**Transport: REST polling first.** Localhost keep-alive gives ~1–3 ms RTT and
the then-Python API already ran threaded (the C# port uses ASP.NET Core /
Kestrel — there is no Flask in this repo). Free-mode latency = one POST + at most one
physics substep ≈ 2 frames — the same order as today's per-frame key
polling. A WebSocket push channel is added **only if measurement shows a
problem**, not beforehand.

## 5. What moves where

| Concern | Today (in-process pygame) | Target |
|---|---|---|
| Camera / zoom / pan | Python `Camera` | Godot (render concern); `camera_*` state fields retire (`--start` focus becomes a Godot-side initial view, from `/map` start points) |
| Render interpolation | `_render_state` lerp in `main.py` | Godot keeps the last two states and lerps itself (matters on 120/144 Hz displays vs. the 60 Hz sim) |
| Breadcrumb trail | `car.trail`, in-process | Godot accumulates from 60 Hz positions, or the server sends the last N points — decide at M4 |
| Input (keyboard/mouse) | pygame events | Godot → REST |
| Minimap, HUD, flags, obstacle palette, quit button | pygame drawing | Godot UI |
| "Orderly shutdown" convention | window close prints a marker | transfers to the Godot window (e2e must still distinguish deliberate close from crash) |

## 6. Phases

### Phase 1 — frontend swap (behavior-frozen) — DONE

Godot renders the existing world in **2D top-down**; the AI driver stays
inside the simulator. Zero behavior change, e2e stays green.

- **M1** — DONE: `GET /map` + Godot renders the static map
- **M2** — DONE: car appears and follows (60 Hz state polling,
  interpolation with a 100 ms render delay, minimap)
- **M3** — DONE: car sprite + blinkers + wheel breadcrumbs, test flags, HUD
  label, smooth render pipeline (sample buffer + sim-rate estimate + capped
  extrapolation, so a 60 Hz sim stays smooth on a 120/144 Hz display)
- **M4** — DONE: minimap/HUD/flags/trail; freeze overlay; the obstacle
  PALETTE was dropped (the Godot client has no palette UI - obstacle placement
  is REST-only, see docs/OBSTACLES.md)
- **M5** — DONE: pygame out of the package, sim headless at a self-paced
  60 Hz, `POST /freeze` replaces the ESC key, `GET /screenshot` removed

Parity against the retired pygame frontend was only a transition-phase
acceptance test; it is **not** a requirement any more - the e2e suite and
the visible Godot window are the standard.

### Phase 7 — one app (DONE, commit `8a57de5`)

- `DrivingGame.csproj` (the Godot project) references `DrivingGame.Sim`
  AND `DrivingGame.Api`; the Sim/Server/Api/Tests dirs are excluded from
  Godot's compile glob (SDK-style projects glob every `.cs` below the root).
- `SimHost.cs` runs the sim IN-PROCESS on its own 60 Hz thread and embeds the
  shared API on `--port`; `MapRenderer` reads `Engine.StateSnapshot()`
  directly per frame - no HTTP. The `--source http` path stays as the
  alternative, so both communication methods coexist.
- `--source embedded` (a client flag, not a runner flag — the runner starts the
  client and passes it through) and the client's Run-Test UI drive the embedded
  API. `scripts/run_e2e.sh` itself has no `--embedded` option.

### Phase 2 — planning outside (NOT done)

Route + raceline + speed profile would move out of the simulator into an
external AI-driver process; the world simulator would then receive only
impulses. Today `Raceline.cs`, `BicycleNav.part1-3` and the route builder
still live inside `DrivingGame.Sim`. Known cost if/when it moves: ~1-2
frames of control-loop latency and a re-tuning iteration at the
`ROAD_EDGE_TOLERANCE_M` (0.35 m) edge. The `/map` contract already carries
layer 2, so the external driver needs no further API work.

## 7. What the client draws today (code-verified)

`MapRenderer.cs` is pure layer-3 presentation of the `/map` payload - it
contains no driving logic:

- **Roads as their own geometry**: `road_strips` drawn as ribbons
  (`Polygon2D`/`MeshInstance2D` batches), one uniform `ROAD_COLOR`, plus
  `paved_edge_rings` as the white boundary line (drawn `EDGE_LINE_INSET_M`
  0.15 m inside the true paved edge, hidden below `EdgeLineMinZoom` 6.0 px/m).
- **Markings** from the payload: centreline dashes, lane dividers, parking-lane
  boundaries (dash patterns come from `marking_style`, in metres), halt lines
  (`stop_lines`), the crossing centre disc (`crossing_markers`), one-way arrows
  and P marks. Fine markings are batched into `MeshInstance2D` with a
  `MarkingMinZoom` 2.0 px/m cull flag.
- **Road signs** as real artwork: `assets/signs/vorfahrt_gewaehren.svg` and
  `vorfahrtsstrasse.svg`, placed from `signs` (2.5 m wide on purpose).
- **Levels**: bridge decks (`elevated_roads`/`elevated_roadways`/edge lines);
  a car's z-index is `10 + 20*level`, so it passes UNDER a deck and OVER its
  own road.
- **Cars**: per-uid sprite from `Config.VEHICLES`, stretched to the model's
  physical size, corner lights (blinkers orange 0.5 s period, headlights white,
  tail/brake lights red), green/red test pennants, small per-car HUD label
  above the nose, minimap dots, breadcrumb trail accumulated client-side from
  the 60 Hz samples.
- **UI**: minimap (top right), speed/zoom readouts, the test-run box
  (bottom left: number field + Run-Test button, driving `POST /run_test`),
  click-to-inspect car window showing `GET /car/{uid}/decisions`.
- **Camera**: `Camera2D`, zoom = px/m; `--center x y` (metres) pins the view,
  otherwise it auto-follows the bound car; `--focus <uid>` binds it to one
  car; the **crash camera** re-anchors to the first car that reports a crash
  episode at `CrashZoom` 34 px/m.
- **One-shot diagnostics**: `--screenshot <path>` (+ `--delay <s>`),
  `--crash-shot <path>` (one PNG 0.5 s after the crash camera latches),
  `--motionlog <path>`, `--quit-on-test-done`, and the clean-quit generation
  file `/tmp/driving_game_godot_gen` (the client quits ITSELF when the value
  changes - never signal Godot).
- **Review mode** (2026-10-21): with `--review` the same node builds the
  Simulationsreviewer instead of this view — its own windows (map inset, three
  car panels, readout), a scrub bar over the tape's fluidity curve with
  decision/crash/wrong-side markers, and `--review-at` / `--review-name` for
  landing on a known moment and a named car.

### Phase 2 — planning outside (goal state, §6)

See §6 above: **not done** as of 2026-10-19, and not on the current roadmap —
route, raceline and speed profile still live in `DrivingGame.Sim`.

## 8. Repos & migration

This became a **single repo** (consolidation decision 2026-09-05, completed
before the port closed): the Godot project at the root IS the frontend, the
sim lives beside it as `DrivingGame.Sim`, and the REST API is the product
boundary between them (`DrivingGame.Api`, used by both hosts). The Python
repo `/Users/hauke/prj/car` is history - it is no longer needed and nothing
here depends on it.

## 8b. Boot splash (settled, 2026-10-26)

The engine paints `assets/splash.jpeg` while the process starts — before the
main scene's `_Ready`, i.e. before this client could show anything itself.
The settings live in `project.godot` (`[application]`, `boot_splash/*`), which
is the same file the editor dialog **Project → Project Settings → Application →
Boot Splash** writes, so either way is fine. Confirmed 2026-10-26 by the user
watching the windowed launch.

**The file format matters**: inside `[application]` you write the key WITHOUT
the section prefix (`boot_splash/image=…`). Writing the full dotted name
(`application/boot_splash/image=…`) asks for
`application/application/boot_splash/image`, an unknown setting that is parsed
and silently ignored — that was the whole reason an early attempt looked
configured but painted nothing. The editor writes the image as a **uid**
(`boot_splash/image="uid://cf1tljg2q30dh"` → `assets/splash.png`), which
survives renaming the file; a `res://` path works equally.

What made it work, and what the traps are:

- **The image must be imported.** Adding `assets/splash.jpeg`/`assets/splash.png`
  and editing the settings is not enough — run
  `"…/Godot" --headless --path . --import` once, which writes the
  `.import`/`.ctex` pair next to it. Without that the boot loader has nothing
  to read and the picture stays the Godot default (the robot).
- **Do not look for the splash in a screenshot or a `--write-movie` file.** The
  capture starts at the first *presented* frame, which is already the app:
  frame 1 of a movie written at `--fixed-fps 20` came in as the finished map
  windows, and a `screencapture` ~1 s after launch showed map content too. The
  only honest witnesses are the eye (start the window, watch the first second)
  or **F5** from the editor.
- **Godot 4.7 has no `full_size` any more** (docs still name it for ≤4.5).
  The knob is `boot_splash/stretch_mode`: `0` Disabled, `1` Keep, `2` Keep
  Width, `3` Keep Height, `4` Cover, `5` Expand. We use `1 Keep` — the
  artwork carries its own dark bars and a centred title, so letterboxing reads
  as intent and Cover would crop the title out.
- **`minimum_display_time`** (ms) is the only honest way to *see* it: the rest
  of startup is a few hundred ms on this machine, so a 0-value splash is a
  2-frame flash. It is at 800 now; set 0 when the load alone should decide.
- **An export must carry the raw file next to the imported one** — a splash
  packed only as `.ctex` is not found (Godot issue #109355).

Client-side splash windows (a `TextureRect` + fade overlay inside `Main.tscn`,
the "Variant 2" pattern from the C# splash-screen snippets) are **not** what we
use: `GetTree().ChangeSceneToFile()` swaps the scene away, and `Main` *is*
the window machinery — minimap, test-info and detail windows, the remembered
window placement, the clean generation-file quit. If an animated intro is ever
wanted, it is an overlay on the same root, not a second scene.

## 9. Open questions / risks

- **Phase-2 re-tuning scope** — still unmeasured; free-mode latency was never
  the bottleneck (REST polling is ~1-3 ms RTT on localhost).
- **Trail source** — settled: the client accumulates the breadcrumb trail
  from the 60 Hz `x/y/heading` samples; the sim does NOT export it.
- **Process lifecycle** — settled: one process in embedded mode; the runner
  starts the console host + client and closes both via the generation file.
- **Oblique/3D view** — still deferred by design (a camera decision, not a
  new model); the world stays flat until elevation data exists.
