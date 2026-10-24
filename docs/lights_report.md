# Car lights — current status (2026-10-23 re-verified; measured 2026-10-19, code-verified)

The car light system lives on both sides of the seam: the **sim** owns the
state, the **Godot client** (`MapRenderer.cs`) draws it. This document
replaces the 2026-09-07 WIP report (whose open issue predates the current
placement code — see "History" below).

## What exists now

| Light | State source (sim) | Drawn by the client |
|-------|--------------------|---------------------|
| **Blinkers** | `BicycleDriver.BlinkerLeft/Right`, hazard via `HazardCommand` (`HAZARD_MIN_DISPLAY_S` 5 s minimum display) | orange dots at all four body corners of one side, 0.5 s period (on 250 ms / off 250 ms) |
| **Brake lights** | `Car.IsBraking` (no new sim state) | bright red at the two rear corners whenever braking — **regardless of `taillights_on`** (a real brake light works with the lights off) |
| **Taillights** | `Car.TaillightsOn` (`POST /toggle {"taillights": …}`) | dark red at the two rear corners |
| **Headlights** | `Car.HeadlightsOn` (`POST /toggle {"headlights": …}`) | white at the two front corners |

`headlights_on` / `taillights_on` / `braking` are exported per car on `GET /state`
(see docs/REST_API.md), so a QA script can isolate each lamp.

## Placement math (`MapRenderer.MakeCarNode`)

Corner lights follow the **sprite's physical size** (`Config.VEHICLE_SIZES`
via `CarSizeOf(color)`), not the sedan box, so trucks get corner lights at
their own corners. `Car.x/y` is the **rear axle**, so everything visual sits
`RearAxleOffsetM` (1.276 m) behind the body centre:

```
fore        = size.L / 2 * 0.85          // fore-aft inset of the corner row
lat         = size.W / 2 * 0.75          // blinker row (inboard)
latOutboard = size.W / 2 * 0.95          // head/tail row (outboard)
frontY      = -fore - RearAxleOffsetM    // Godot y grows DOWN, nose = -y
rearY       =  fore - RearAxleOffsetM
```

`MakeLight(pos, color)` sets the corner offset as the node's **Position** and
builds the polygon's own vertices as a unit circle at the node origin; the
per-frame zoom compensation then only resizes the dot
(`rPx = clamp(Camera2D.zoom * 0.4, 2, 7)` px, scaled back out by the zoom) and
never moves it — that was the original bug (a baked-in offset shrunk by the
node's own `Scale`, so corner lights rendered ~0.3 m out instead of ~3 m).

## How it is checked

`tests/light_check.py` spawns ONE stationary car (`crossroads_from_north`,
progress 0.6, never accelerates) and cycles all six states — blinker-left →
blinker-right → hazard (5 s, `HAZARD_MIN_DISPLAY_S`) → brake → taillight →
headlight, 3 s each — driving the REST API directly against the running game.
Manual visual QA only: there is no pass/fail criterion, only "does it look
right". Launch the window with a close, fixed zoom to inspect the corner
lights, e.g. `--zoom 64`.

To measure a painted/visual length from a screenshot, scan a row/column of the
PNG with PIL and divide pixels by the announced zoom (`--zoom` is px/m).

## History (2026-09-07 report, superseded)

- **Bug found + fixed:** every corner light rendered clustered near the car's
  centre (offset baked into polygon vertices, then shrunk by the node's
  `Scale` — Godot scales relative to the node's own `Position`).
- **Open issue then:** with the car facing south, front/brake/taillights sat
  in the wrong corners (pixel scan of a 200×423 screenshot put the dots in the
  lower third of the sprite). The current code above places the rows
  symmetrically about the body centre in the sprite's own frame with the nose
  at `-y`, so the fore/aft mix-up that produced the symptom is gone; if a
  south-facing car ever misplaces its lamps again, reproduce it with
  `tests/light_check.py` at `--zoom 64` and re-measure before changing code.
