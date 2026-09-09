# Lights — current status (2026-09-07)

Working on the car light system in the C# Godot port. This is a WIP report of where things stand; **no fixes applied yet** for the open issue below.

## What exists now

Lights are rendered in `MapRenderer.cs` (Godot client), driven by state from
`SimEngine`/`Car` (`DrivingGame.Sim`) over the REST API:

- **Blinkers** — orange, front + rear corners on one side. Left / right via
  driver turn signal; hazard via `HazardCommand`.
- **Brake lights** — bright red at the two rear corners while `Car.IsBraking`
  (works even with taillights off).
- **Taillights** — dark red at the two rear corners while `TaillightsOn`
  (independent toggle, `/toggle`).
- **Headlights** — white at the two front corners while `HeadlightsOn`
  (independent toggle, `/toggle`).

Independent on/off for headlights + taillights was added this session so a QA
script can isolate each light. A prior bug (corner offset baked into polygon
vertices and then shrunk by per-frame zoom compensation) was already fixed:
offsets are now the node `Position`, polygons are unit circles at the node
origin, so scale only resizes the dot.

## How it's being checked

`tests/light_check.py` spawns one **stationary** car (`crossroads_from_north`,
progress 0.6, never accelerates) and cycles all six states — blinker-left →
blinker-right → hazard (5 s, `HAZARD_MIN_DISPLAY_S`) → brake → taillight →
headlight, 3 s each — driving the REST API directly against the running demo
(`light-check-server` :5000 + `light-check-godot`). Manual visual QA only; not
part of the pass/fail suite. It was run this session and completed cleanly.

## Open issue (reported by user, NOT yet fixed)

With the car **facing south** (down), the **front lights, brake lights, and
taillights are in the wrong positions**. Observed in a screenshot: the
yellowish front light and the slightly brighter-red taillight do not sit at the
correct body corners.

Pixel scan of the screenshot (`local://image-500ba18297c9bd6f.webp`, 200×423)
places the lit dots roughly in the lower third of the sprite (front light
clusters near y≈347–350, red rear clusters near y≈336–350) rather than cleanly
at the four body corners — consistent with a remaining fore/aft or side offset
in how `MakeLight` positions are computed for the front/rear rows.

Next step (pending user go-ahead): re-check the corner-offset math in
`MapRenderer.cs` (`fore`, `RearAxleOffsetM`, lateral `latOutboard`) against the
sprite's actual bounding box and correct the front/rear light placement.
