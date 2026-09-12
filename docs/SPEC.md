# Car Game — Specification

## Overview

A 2D top-down driving game rendered with **Pygame**, where the playable world is built from **real road network data extracted from OpenStreetMap (OSM)**. The player drives a car along actual streets in **Kleinmachnow** (south of Berlin).

## Core Concept

- Load the road network from the **OSM-Wars PostgreSQL database** (PostGIS, `road_geometry` table)
- Parse the data into a graph of **nodes** (intersections/points) and **segments** (road pieces)
- Render roads as **2D polygons** with real-world widths (no simple lines)
- Two driving modes: **FREE** (manual steering) and **BICYCLE** (intent-based AI executed by the kinematic bicycle model). How the car executes each maneuver (parking, turning, U-turn, ...) is specified in [DRIVING_MANEUVERS.md](DRIVING_MANEUVERS.md)

## Technical Stack

| Layer | Technology |
|-------|-----------|
| Game engine | **Pygame 2.6.1** |
| Road geometry | **Shapely** (line merging, corner rounding, buffering into paved-area polygons - shared by rendering AND on-road physics checks) |
| OSM data source | **OSM-Wars PostgreSQL DB** (PostGIS, `road_geometry` table, Brandenburg schema) |
| DB access | **psycopg3** |
| Language | **Python 3.14** |
| Region | **Kleinmachnow** (south of Berlin) |

## Architecture

### Class Structure

```
Main Game Loop (src/main.py)
  ├─ RoadNetwork (spatial queries, graph)
  ├─ Camera (viewport, zoom, pan)
  ├─ Renderer (roads, HUD, minimap)
  ├─ Car (physics, state)
  │   ├─ Driver (control interface)
  │   │   ├─ KeyboardDriver (FREE mode)
  │   │   └─ BicycleDriver (intent: gas / brake / blinker)
  │   └─ BicycleNav (kinematic bicycle model + pure pursuit)
  │       └─ raceline (corridor + minimum-curvature line)
  ├─ PhysicsValidator (constraint checking)
  └─ GameAPI (REST server, optional)

Driver → Car.update(control_input) → PhysicsValidator.check()
```

**Separation of Concerns:**
- **Car**: Pure physics and state (no input handling)
- **Driver**: Control logic (keyboard or AI)
- **BicycleNav**: Kinematic bicycle model, reference line, speed profile
- **raceline**: The driving line as a constrained optimisation (see below)
- **PhysicsValidator**: Constraint validation (teleportation, snaps, off-road)
- **GameAPI**: Remote control interface (optional, thread-safe)

### Visual Layout

```
┌─────────────────────────────────────────────┐
│                  Pygame Window               │
│  ┌─────────────────────────────────────────┐ │
│  │      HUD (bottom-left)                  │ │
│  │      - Speed, Mode, Indicators          │ │
│  └─────────────────────────────────────────┘ │
│  ┌─────────────────────────────────────────┐ │
│  │           Camera / Viewport             │ │
│  │  ┌───────────────────────────────────┐  │ │
│  │  │   Road Network (polygons)         │  │ │
│  │  │   - Direct rendering per frame    │  │ │
│  │  │   - Rounded caps on segments      │  │ │
│  │  └───────────────────────────────────┘  │ │
│  │  ┌───────────────────────────────────┐  │ │
│  │  │         Car Entity                │  │ │
│  │  │   - Headlights (2px)              │  │ │
│  │  │   - Taillights (2px, red)         │  │ │
│  │  │   - Blinkers (3px, orange)        │  │ │
│  │  └───────────────────────────────────┘  │ │
│  └─────────────────────────────────────────┘ │
│  ┌─────────────────────────────────────────┐ │
│  │      Minimap (top-right)                │ │
│  │      - Full area view                   │ │
│  │      - Yellow viewport indicator        │ │
│  └─────────────────────────────────────────┘ │
└─────────────────────────────────────────────┘
         ┌──────────────────────┐
         │  RoadNetwork (graph) │
         │  - nodes + segments  │
         │  - node_degree       │
         │  - node_connections  │
         └──────────────────────┘
```

## Driving Modes

Maneuver-level behavior — how the car parks, pulls out, turns, does U-turns,
avoids obstacles — is specified in [DRIVING_MANEUVERS.md](DRIVING_MANEUVERS.md).
This section only defines the two drivers and their inputs.

### FREE Mode (Manual Steering)
- **W/↑**: Accelerate
- **S/↓**: Brake (immediate, 10 m/s²); fresh press at a standstill engages reverse
- **A/←**: Steer left
- **D/→**: Steer right
- **Q / E**: Left / right blinker (momentary: held = on)
- **Release W**: Speed maintained (cruise control, no friction)
- **Off-road**: Car stops immediately
- **Map edge**: Car stops at boundary

### BICYCLE Mode (Intent-Based AI)
The player gives high-level intent; the car executes it with the kinematic
bicycle model (`src/bicycle_nav.py`: reference line + speed profile + pure pursuit).
- **W/↑ / S/↓**: Gas / brake (manual override of the speed plan)
- **A/← / D/→**: Toggle **left / right blinker** → turn that way at the next
  junction where it is still physically possible (DRIVING_MANEUVERS.md §4)
- **U**: Request a U-turn (Wenden, DRIVING_MANEUVERS.md §5)
- **Features**:
  - Car follows the road automatically (stays on the right lane)
  - Cornering speed derived from the curvature of the driving line
    (`v = sqrt(a_lat/kappa)`) — no angle-based speed tables
  - Blinker auto-off is mechanical (steering-cam logic: steered in, then back to
    centre), plus per-maneuver rules in DRIVING_MANEUVERS.md
  - Dead ends / route end: car eases to the **right edge** of the road and stops
    there (DRIVING_MANEUVERS.md §1, "Variante: Sackgassenende / Route-Ende")
  - Smooth segment transitions (no teleportation)

### Mode Switching
- Press **TAB** to toggle between FREE ↔ BICYCLE (REST API: `?mode=free` / `?mode=bicycle`)
- When switching to BICYCLE: car snaps to the nearest road segment
- Start mode: **BICYCLE**

## Physics

### Speed & Acceleration
- **Max speed**: 180 km/h (50 m/s)
- **Acceleration**: 2.8 m/s² (0–100 km/h in ~10 seconds, normal car)
- **Braking**: 10 m/s² (full ABS braking, ~1g)
- **Cruise control**: W-release maintains speed (no automatic deceleration)

### Automatic Braking (BICYCLE mode)

There is no angle-based speed table. The speed profile derives the allowed
speed from the curvature of the driving line (`v = sqrt(a_lat/kappa)` — see
"Speed-Based Turning Radius" below). When a signaled turn is not feasible at
the current speed, the car brakes or drives straight through with the blinker
still on — specified in [DRIVING_MANEUVERS.md](DRIVING_MANEUVERS.md) §4.

### Realistic Turning Physics

**Core Rule**: The car must **ALWAYS stay completely on the road** with all four tires. No cutting corners, no off-road driving.

#### Real Car Dynamics
- **Pivot point**: Rear axle (like a real car)
- **Turning radius**: Depends on speed and steering angle
- **Centripetal force**: `F = mv²/r` → faster speed requires wider radius

#### Speed-Based Turning Radius

**Design constraint** (not validated after the fact): Maximum lateral acceleration determines turning radius.

| Speed | Required Turning Radius |
|-------|-------------------------|
| 20-30 km/h | 5-10 m (tight turn) |
| 40-60 km/h | 15-25 m (medium turn) |
| 80+ km/h | 30-50 m (wide turn) |

**Formula**: `radius = k × speed²` (where k is tuned for realistic feel)

**Physics**: Centripetal force `F = mv²/r` means faster speed requires wider radius to keep lateral acceleration within design limits.

**Note**: This is a **design constraint** for the turning system (how we calculate turns), NOT a validation check. External forces (collisions, explosions) CAN exceed this limit—that's physically possible!

**Cornering speed is not a table.** It follows from the geometry of the
line the car is actually driving:

```
v(s) = sqrt( A_LAT_MAX * A_LAT_PLAN_FRACTION / kappa(s) )
```

There is no per-turn-angle lookup and no special case for tight corners.
An earlier version had both, plus a hard 1.2 m/s ceiling for
`kappa > 0.05`. Together those drove a 6 m junction fillet at **4.3 km/h**,
about a quarter of the ~17 km/h a real car takes an ordinary local-road
corner at (0.38 g, measured). They were treating a symptom: the car cut
corners because of where its reference line ran, not because the speed
formula was wrong, so capping the speed only made it cut them slowly.

Two things make the formula trustworthy:

- **Curvature is measured over a fixed 1 m window** (`CURVATURE_WINDOW_M`),
  never a fraction of route length. A window proportional to the route was
  4.94 m on a 494 m route - wider than half a 9.4 m fillet - which smeared a
  4.25 m lane radius into a reported 6.30 m. The profile then handed the car
  a speed needing 2.96 m/s2 against a 2.0 limit, so it could not hold its
  own reference line and understeered wide out of every bend.
- **The profile plans against only 70 % of the limit**
  (`A_LAT_PLAN_FRACTION`). Planning at the full value saturates the heading
  rate at the apex and leaves the controller no authority to correct with;
  the reserve is what lets it pull back onto the line.

#### The Driving Line (`src/raceline.py`)

The car does not follow a fixed lane offset. The line is the solution to
the driving rules, stated as an optimisation:

| rule | how it is enforced |
|------|--------------------|
| 1. never leave the pavement | upper corridor bound: the paved polygon eroded by half the car's width plus `ROAD_EDGE_TOLERANCE_M` (so corner rounding is respected automatically) |
| 2. never enter the oncoming lane | lower corridor bound: `CAR_WIDTH/2 + LANE_CENTRE_MARGIN_M` right of the centreline; lifted on one-way carriageways |
| 3. be as fast as possible | the objective - since `v = sqrt(a_lat/kappa)`, fastest means straightest, so minimise curvature |
| 4. use a racing line | not a feature; it is what (3) produces |

Rules 1 and 2 are **hard bounds**, so they cannot be traded away for
speed: a legal line is guaranteed by construction rather than detected
afterwards by a validator.

The classic outside-apex-outside line is encoded nowhere. It emerges: on
a right-hand bend the solver drives the offset to the centreline side on
entry, to the kerb at the apex, and back out on exit - and mirrors itself
for a left-hander.

**Why an optimiser and not a formula.** Offsetting a path laterally by
`o(s)` changes its curvature by roughly `-o''`, so a local "swing out,
cut in" bump buys radius at the apex and pays for it with sharper
curvature on both shoulders - measured, the net line was 2-4x *tighter*.
The gain only appears when the whole approach and exit reshape together,
which is what the optimiser does and a bump function cannot.

Minimising `sum(kappa^2)` subject to box bounds gives normal equations
that are pentadiagonal, so a banded solve handles a 500-station route in
about 20 ms.

#### Where a car drives — and where it does not (the mental model)

The single most useful model for reasoning about "why did the car drive
*there* and not *here*" is that route choice and lateral placement are **two
separate layers**:

1. **WHICH road — the graph.** The route (the ordered sequence of nodes /
   segments) comes from the **road graph** (topology). The raceline does
   **no** free-space pathfinding over paved area: it never invents a shortcut
   across an open surface, and it cannot "discover" a connection the graph
   does not have. Change the route only by changing the graph or the turn
   the driver signals.
2. **HOW the car sits on that road — the paved geometry.** Along the chosen
   route, the driving line is a lateral offset optimised inside a corridor
   whose outer bound is the **actual paved polygon** (via the `Fits`/`Reach`
   probe) and whose inner bound is the centreline margin. This layer is
   **agnostic to WHY the tarmac is there** — road, junction fillet, or an
   extra patch: the probe just tests the paved polygon and uses whatever
   tarmac it finds.

**So which tarmac gets used?** Exactly the tarmac that is
*connected + adjacent to the route + on the legal side + within the
corridor's lateral reach*:

- Tarmac that **widens the road / fills a corner** along the route → **used**
  (the corridor grows into it; the line hugs the edge / cuts the corner a
  little tighter). This is why a correctly-connected junction fillet lets the
  car round the corner — and why a *disconnected* fillet (grass gap in
  between) is, for the driver, as if it were not there at all (the probe
  stops at the gap).
- Tarmac that is **disconnected** (a loose patch, or separated by a sliver of
  grass), on the **oncoming half** of a two-way road, or **beyond the lateral
  reach** → **ignored**.

**Worked example — a "W"-shaped road, first V's interior fully paved.** Told
to drive the whole W, the car still follows the graph: down the first arm to
the valley node and back up the second arm — it does **not** cut straight
across the paved V (no graph edge does). But at the sharp valley it **rounds
the apex** using the inside tarmac: with the V filled it drives a smoother,
wider (faster) racing line through the bottom than it could without it. Two
limits still hold: on a two-way road the centreline margin forbids using the
oncoming half (an *one-way* arm could use the whole inside), and only tarmac
within the corridor's reach along the route counts — not an arbitrarily large
triangle far from the line.

The takeaway for debugging "why here / not there": if a car will not use a
surface, ask (a) is it on the planned graph route at all, (b) is it
*connected* to the route's pavement with no grass gap, and (c) is it on the
legal (own-lane) side within reach. A "looks paved but never driven" spot is
almost always a **connectivity** bug (disconnected island) — fix the geometry
so drawn-tarmac == drivable-tarmac, never paint over the gap (that would
fabricate road the planner still won't use, or use in a way a human can't
predict).

#### Junction Centre: the White Dot

The renderer paints a white dot at every node of degree >= 3. Where the
driving line must pass relative to that dot (straight/right: left of the
dot; left turn: *voreinander*, i.e. right of it) is specified in
[DRIVING_MANEUVERS.md](DRIVING_MANEUVERS.md) §4 ("Kreuzungsmitte (der weiße Punkt)").

#### On-Road Check: Single Source of Truth

The rendered road, the turn-planner's arc validation, and the live
per-frame off-road check used to be **three separate, independently
approximated** implementations (in `road_network.py`, `car.py`, and
`turning_system.py` respectively) that could disagree with each other -
e.g. an arc that visibly followed the smooth rendered curve through a
bend could still get flagged as off-road, because the physics-side
checks only knew about sharp-cornered rectangles and a cruder
fixed-radius junction circle.

This is now unified:
- `RoadNetwork.get_paved_polygon()` builds (and caches) **one** Shapely
  polygon covering the entire drivable paved area - the exact same
  geometry used for rendering (rounded bends and junction fillets
  included).
- `RoadNetwork.is_on_road(x, y)` and `Car.is_on_road()` (which now just
  delegates to it) both test against this polygon.
- `raceline.legal_corridor()` derives the driving corridor from the
  same polygon, so the line cannot be planned off the pavement.
- All three share one tolerance constant, `config.ROAD_EDGE_TOLERANCE_M`
  (0.5m), instead of the planner silently allowing more slack than the
  live check honored (which used to cause plans that passed validation
  to immediately register as off-road once actually driven).

#### TODO / idea — two footprints: body box vs. four tire contact points

The on-road check (`IsCarOnRoad` and the raceline corridor's `Fits`/`Reach`)
tests **four points** of the car against the paved polygon. Today those four
points are the **body-box corners** (±L/2, ±W/2 of the 4.5 × 1.8 m box) — the
outermost points of the vehicle. That is structurally already a four-point
check, i.e. essentially "four tires", but sampled at the wrong place: real
tires sit **inboard** of the body (from the sprite: front axle ~0.31·L ahead,
rear axle ~0.29·L behind centre, tires ~0.36·W outboard), so the current
check is **stricter than reality** — it forbids a bumper corner overhanging
grass even when all four tires are still on tarmac.

Proposed (small change, not yet implemented — deliberate, needs e2e
re-verification because it changes the driving line): keep **two** footprints
with distinct jobs —
- **Body box** (4 corners): CRASH / collision detection (car-to-car, obstacle
  contact) — the physical extent that must not interpenetrate.
- **Four tire contact points** (inboard, real axle/track offsets): the
  ROAD-CONTACT test (on-road + raceline corridor). Only the tires must stay
  on pavement; the body may overhang grass, as a careful real driver does.
Moving the four road-contact points from body corners to tire positions lets
the line hug edges / cut corners a little tighter (bumpers overhang), and
reduces how much junction fillet is strictly required. Per-vehicle later
(trucks have different axle offsets). Until then: body-box corners for both.

#### Width transitions (tapers)

Where a road changes width along its length (a plain degree-2 node between a
wider and a narrower section), the paved area must **not** step abruptly.
Earlier that was two flat-capped rectangles merely abutting along a line,
which the union renders degenerately (a spike to the centreline + the wide
cap drawn as a white bar across the road). Instead, a width-varying road is
built as **one continuous variable-width ribbon**
(`RoadNetworkGeometry.BuildWidthVaryingRibbons`): the centreline is chained
through the width-change nodes (regardless of width), splined
(centripetal Catmull-Rom, so bends are smooth), resampled, and offset by a
half-width that follows each section's width with a **smoothstep** (ease-in-
out, `3u²−2u³`) taper.

Rules for the taper (all in `Config`):
- **Lies entirely in the wider section** — the narrow road keeps its width up
  to the node; the wide road tapers down to meet it.
- **Length ∝ width jump**: `T = WIDTH_TAPER_RATIO · (W_wide − W_narrow)`
  (≈ a 1:3 verge).
- The ribbon follows the **curved** centreline, so an angled transition
  (a gentle "V") tapers smoothly around the bend with no notch and no bulge.

Consumers stay consistent: the same chain spline feeds the paved polygon,
the on-road check (via the paved polygon), and the **dashed centreline**
(`GetMarkingCenterlines` draws the chain spline and skips the per-width
pieces via `excludeSegs`), so the dashes sit on the paved centre. The
continuous-ribbon geometry only replaces width-varying chains; constant-width
roads still use the per-(highway,width) buffer.

#### Turn Feasibility & Missed Turns

How a signaled turn is validated against the current speed and executed — or
missed (drive straight through, blinker stays on, retry at the next junction)
— is specified in [DRIVING_MANEUVERS.md](DRIVING_MANEUVERS.md) §4 ("Abbiegen"
→ "Erreichbarkeit"). The feasibility input is the curvature of the optimised
driving line, not a pre-computed circular arc.

### Route-End / Dead-End Approach (Right Edge)

Pulling over to the right curb at a genuine dead end or an explicit
destination and stopping there is a parking variant — specified in
[DRIVING_MANEUVERS.md](DRIVING_MANEUVERS.md) §1 ("Variante: Sackgassenende / Route-Ende").

### Turning
- **FREE mode**: Turn rate depends on speed (slower at high speed)
- **BICYCLE mode**: Heading follows the reference line automatically

### Teleportation Watchdog
- **Purpose**: Detect unphysical position jumps (bugs)
- **Threshold**: >50 meters per frame
- **Action**: Exception with stack trace + debug info
- **Skip**: First 5 frames (allows initial positioning)

## HUD (Heads-Up Display)

Located in **bottom-left corner**, shows:

### Speed Display
- **Large number**: Current speed in km/h
- **Color coding**:
  - White: 0–50 km/h
  - Yellow: 50–100 km/h
  - Red: 100+ km/h
- **Unit**: "km/h" label
- **Speedometer arc**: 0–180 km/h circular gauge (green → yellow → red)

### Mode Indicator
- **Text**: "FREE" or "BICYCLE"
- **Color**: Blue (100,150,255) for FREE, Green (0,200,100) for BICYCLE
- **Hint**: "(TAB)" to switch

### Status Indicators
- **B** (red circle): Braking active
- **A** (green circle): Accelerating
- **L** (orange circle): Left blinker on
- **R** (orange circle): Right blinker on

## Road Network

### Data Source
- **Database**: OSM-Wars PostgreSQL, schema `brandenburg`
- **Table**: `road_geometry` (EPSG:3857 projected coordinates)
- **Area**: Kleinmachnow bounding box
  - North: 52.42382°, West: 13.21831°
  - South: 52.40714°, East: 13.25033°
- **Segments loaded**: 1970 road segments
- **Nodes**: 1909 unique nodes

### Drivable Road Types (highway_id filter)
Only roads with these highway types are loaded:

| highway_id | OSM tag | Description |
|------------|---------|-------------|
| 1 | motorway | Highway/Autobahn |
| 2 | trunk | Major trunk road |
| 3 | primary | Primary road |
| 4 | secondary | Secondary road |
| 5 | tertiary | Tertiary road |
| 6 | residential | Residential street |
| 7 | service | Service road/driveway |
| 8 | unclassified | Unclassified road |
| 9 | motorway_link | Motorway ramp |
| 10 | trunk_link | Trunk ramp |
| 11 | primary_link | Primary ramp |
| 12 | secondary_link | Secondary ramp |
| 14 | tertiary_link | Tertiary ramp |

### Road Widths (meters)

| Highway Type | 2-way width | 1-way width | Color |
|--------------|-------------|-------------|-------|
| motorway | 14.0 m | 7.0 m | #444444 |
| trunk | 10.0 m | 7.0 m | #555555 |
| primary | 10.0 m | 7.0 m | #666666 |
| secondary | 7.0 m | 3.5 m | #888888 |
| tertiary | 7.0 m | 3.5 m | #999999 |
| residential | 7.0 m | 3.5 m | #aaaaaa |
| service | 3.5 m | 3.5 m | #cccccc |

**Special case**: Service roads always use 1-way width (3.5 m) regardless of `oneway` tag.

### Rendering
- **Style**: Direct polygon rendering (no texture scaling)
- **Geometry**: Real Shapely-based paved-area polygons, not plain
  per-segment rectangles:
  - Contiguous same-width/same-highway segments are merged
    (`shapely.ops.linemerge`) into one continuous line wherever they
    pass through a plain degree-2 node (an ordinary bend, not a real
    junction).
  - Before buffering, the merged line's own **centerline corners are
    rounded** with a real tangent arc (`RoadNetwork._round_polyline_corners`,
    radius `config.ROAD_CORNER_RADIUS_M`, default 6m) - this is
    deliberately done to the centerline itself, not just relied on via
    a buffer's "round join", because a round join only curves the
    *outer/convex* edge of a bend and leaves the inner edge a perfectly
    sharp mitre. Rounding the centerline first means **both** edges of
    the road curve smoothly through a bend, like a real paved corner.
  - The (now smooth) line is then buffered by half the road width with
    round joins/caps (`resolution=8`) into the final fillable polygon.
  - At real junctions (degree ≥ 3: T-junctions, Y-intersections,
    crossroads), `linemerge` can't merge across the branching node, so
    each adjacent pair of roads (by angle, skipping near-straight
    pass-throughs and near-duplicate spokes) gets its own small virtual
    3-point polyline through the junction node, rounded and buffered
    the same way, to fillet that corner too.
  - All of the above is built **once** and cached
    (`RoadNetwork.get_road_polygons_by_color()` / `.get_paved_polygon()`)
    since the road network never changes at runtime.
- **Per frame**: Roads drawn as vector polygons (no pixelation on zoom)
- **Performance**: 109 FPS with 1970 segments at 4× zoom

### Graph Structure
- **Segments**: List of `RoadSegment` objects with:
  - Coordinates (x1, y1, x2, y2)
  - Highway type, oneway flag, width
  - Start/end node IDs
  - Length in meters
- **Nodes**: Dict of node_id → (x, y) positions
- **Node connections**: Dict of node_id → [segment indices]
- **Node degree**: Dict of node_id → connection count (for junction detection)

### Endpoint Snapping
- **Threshold**: 8 meters
- **Purpose**: Close OSM mapping gaps
- **Result**: 73 endpoints snapped in Kleinmachnow

## Camera

### Controls
- **Mouse wheel**: Zoom in/out
- **Left mouse button + drag** (on empty map area): Pan map manually
- **+/- keys**: Zoom in/out
- **C key**: Snap camera to car position

### Behavior
- **Follow mode**: Camera follows car when `speed > 0.1 m/s`
- **Zoom range**: 0.64× to 16× (configurable)
- **Smooth follow**: Interpolated camera movement
- **Manual override**: Left-mouse drag disables follow temporarily

## Minimap

- **Location**: Top-right corner
- **Size**: 200×200 px
- **Content**: 
  - Full road network (scaled down)
  - Car position (blue dot)
  - Current viewport (yellow rectangle, Y-axis aligned with main view)
  - Speed text overlay

## Car Visual

### Body
- **Size**: 4.5 m × 2.0 m (length × width)
- **Color**: Red (#B41E1E body, #D73C3C front strip)
- **Rotation**: Smooth heading (0° = north/up)

### Lights
- **Headlights**: 2× white circles (2px) at front corners
- **Taillights**: 2× red circles (2px) at rear corners
  - Bright red when braking
  - Dark red otherwise
- **Blinkers** (both modes: Q/E in FREE, AI-controlled in BICYCLE): 3× orange circles (3px) at side
  - Flash with 0.5s period (on 0.25s, off 0.25s)
  - Left or right depending on signal


> **C# port note (2026-09-07, `driving-game` repo):** implemented in
> `MapRenderer.cs` (Godot client) + `SimEngine`/`Car` (`DrivingGame.Sim`).
> - **Headlights** (white, front corners) and **taillights** (dark red idle /
>   bright red while braking, rear corners) are independently toggleable via
>   `POST /toggle {"uid": N, "headlights": true|false}` /
>   `{"taillights": true|false}` - test/QA only, no gameplay effect. Per-car
>   state exposed on `/state` as `headlights_on` / `taillights_on`.
> - **Brake lights** piggyback on the existing `Car.IsBraking` (no new sim
>   state): bright red at the rear corners whenever `IsBraking` is true,
>   regardless of `taillights_on` (a real brake light works even with the
>   headlights off).
> - **Bug found + fixed**: every corner light (blinkers included) rendered
>   clustered near the car's centre instead of at its corners. Root cause:
>   `MakeLight` baked the corner offset into the `Polygon2D`'s own vertex
>   coordinates, then the per-frame zoom-compensation code set that same
>   node's `Scale` to keep the dot's SCREEN size constant - Godot scales a
>   node's rendering relative to its OWN `Position` (origin (0,0) if unset),
>   so that `Scale` ALSO shrank the baked-in offset toward the origin. At
>   the small scale factors zoom compensation actually uses (~0.1), a light
>   meant to sit ~3 m from the body centre rendered ~0.3 m out. Fixed by
>   setting the corner offset as the node's `Position` and building the
>   polygon's own vertices as a plain unit circle centred at 0,0, so `Scale`
>   only resizes the dot and leaves its position untouched. Confirmed
>   visually (screenshot, corner lights now sit exactly at the 4 body
>   corners) - see diary 2026-09-07.
> - **Visual QA script**: `tests/light_check.py` spawns one stationary car
>   and cycles blinker-left → blinker-right → hazard → brake → taillight →
>   headlight, 3 s each (hazard needs 5 s - `BicycleDriver.HAZARD_MIN_DISPLAY_S`
>   enforces a minimum display time), for a human to eyeball in the Godot
>   window. Not part of the automated `test_turning.py` suite - there is no
>   pass/fail criterion, only "does it look right".

## Breadcrumb Trail (Debug Overlay)

Toggleable with the **B** key (see Controls). The trail records the car's
position and heading every 0.1 s (max 500 points, oldest dropped first).

### Rendering

- **Continuous line**: one continuous line from one recorded position on
  the road to the next — i.e. a single polyline through all recorded
  points (no gaps, no discrete arrow shapes).
- **Paint buckets on the wheels**: at each recorded position, a small
  filled marker (a "paint bucket") is placed at each of the car's four
  tire positions — the tire centers from the car sprite
  (`assets/car_sprite.svg`: front axle 62/200 of the length ahead of
  center, rear axle 58/200 behind, all tires 36/100 of the width
  outboard), transformed with the recorded center and heading:
  - **Front tires** (front-left, front-right): **yellow**
  - **Rear tires** (rear-left, rear-right): **blue**
  - Bucket diameter = tire width (~0.2 m) in screen pixels, so each
    bucket is exactly the contact patch of one tire (scales with zoom,
    1 px minimum)

  The buckets show the car's actual wheel footprint at each recorded
  moment, so the width of the swept area is visible at a glance.
- **Rainbow arrows: removed.** The old chevron ("v") arrows with the
  50-step violet→red recency gradient (`Renderer._RECENT_RAINBOW`) are
  no longer drawn.

## Coordinate System

- **World space**: EPSG:3857 projection (meters)
- **Screen space**: Pixels
- **Y-axis**: 
  - World: Y increases northward
  - Screen: Y increases downward
  - Camera handles transform
- **PIXELS_PER_METER**: 10 (configurable)

## Configuration (`config.py`)

### Window
```python
WINDOW_WIDTH = 1280
WINDOW_HEIGHT = 720
BG_COLOR = (34, 34, 34)
```

### Car Physics
```python
CAR_SPEED = 50          # m/s = 180 km/h
CAR_ACCELERATION = 2.8  # m/s² (0-100 in ~10s)
CAR_BRAKING = 10.0      # m/s² (ABS braking)
CAR_TURN_SPEED = 180    # degrees/second
CAR_LENGTH = 4.5        # meters
CAR_WIDTH = 2.0         # meters
```

### Minimap
```python
MINIMAP_SIZE = 200
MINIMAP_MARGIN = 10
MINIMAP_BG = (20, 20, 20, 150)
MINIMAP_BORDER = (100, 100, 100)
MINIMAP_CAR_COLOR = (100, 150, 255)
```

### Road Geometry
```python
JUNCTION_WIDENING_M = 4.0      # curb-radius flare added at real (degree 3+) junctions
ROAD_CORNER_RADIUS_M = 6.0     # visible curb-style rounding radius at road bends
ROAD_EDGE_TOLERANCE_M = 0.5    # shared slack between planned-arc validation and live on-road checks
```

### Bounding Box (Kleinmachnow)
```python
BOUNDING_BOX = {
    "north": 52.42382,
    "west": 13.21831,
    "south": 52.40714,
    "east": 13.25033,
}
```

## Project Structure

```
car/
├── docs/
│   └── SPEC.md              # This file
├── src/
│   ├── __init__.py
│   ├── main.py              # Entry point, game loop
│   ├── config.py            # Settings & constants
│   ├── osm_loader.py        # PostgreSQL data fetcher
│   ├── road_network.py      # Graph + spatial queries
│   ├── renderer.py          # Pygame drawing (roads, HUD, minimap)
│   ├── car.py               # Car physics & modes
│   └── camera.py            # Camera / viewport logic
├── tests/
│   ├── __init__.py
│   └── test_road_network.py  # 10 unit tests (projection, spatial)
├── requirements.txt
├── .gitignore
└── README.md
```

## Implementation Status

### ✅ Completed Features

#### Core Systems
- [x] PostgreSQL OSM data loader (Brandenburg schema)
- [x] Road network graph with node degrees & connections
- [x] Direct polygon rendering (no pixelation on zoom)
- [x] Camera follow & manual pan/zoom
- [x] Minimap with viewport indicator
- [x] Service road width (3.5m fixed)
- [x] Endpoint snapping (8m threshold)

#### Car & Physics
- [x] **Driver class architecture** (Keyboard, AI drivers)
- [x] **Car class** (pure physics, no input handling)
- [x] Car physics with two modes (FREE/BICYCLE)
- [x] Automatic road following (BICYCLE mode)
- [x] Turn signals & intelligent blinker logic
- [x] Automatic braking with physics-based distance calculation
- [x] Dead-end handling (180° turn)
- [x] Off-road detection (FREE mode)
- [x] **raceline module** (driving line as constrained optimisation)

#### Visual & UI
- [x] **Professional SVG car sprite** (windows, mirrors, wheels)
- [x] **Sprite scaling** based on zoom and car dimensions
- [x] **HUD with PIL text rendering** (speed, mode, indicators)
- [x] Speedometer arc (0-180 km/h)
- [x] Blinker visualization (flashing orange)
- [x] **Breadcrumb trail** (toggleable with B key)
- [x] **Breadcrumb trail v2** (continuous line + yellow/blue wheel paint buckets, replaces the rainbow chevron arrows — see "Breadcrumb Trail (Debug Overlay)")
- [x] Mode indicator (BICYCLE/FREE)

#### Debugging & Validation
- [x] **PhysicsValidator class** (independent "physics judge")
  - Teleportation detection (>50m jumps)
  - Instant heading snap detection (>30° per frame)
  - Off-road detection (BICYCLE mode)
  - Toggleable with V key
- [x] Debug keys (B=breadcrumbs, R=random location, V=validator)

#### Testing & API
- [x] **REST API** for remote control (Flask, 10+ endpoints)
  - GET /state - real-time game state
  - POST /control - send input commands
  - POST /teleport - move car
  - GET /screenshot - capture frame
  - Full thread-safe integration
- [x] **Automated test suite**
  - test_api.py - basic API tests
  - test_turning.py - comprehensive turn tests (6 scenarios)
  - Instant snap detection (>30° changes)
  - Off-road violation detection
  - Screenshot capture on failure
- [x] 10 unit tests passing
- [x] **Obstacle system, Part 1** (docs/OBSTACLES.md)
  - Palette UI next to the minimap: drag & drop parked cars (blue/yellow/white)
  - Paved-area-only placement; auto-alignment to lane direction — on curves
    and junctions it follows the local tangent of the smoothed centerline
  - Save/load obstacle layouts per map (`data/obstacles/<map>/<name>.json`)
  - REST API: `GET/POST /obstacles`, `DELETE /obstacles/<id>`
  - Stop on contact: full braking + no interpenetration, all driving modes
  - `tests/test_obstacles.py` (36 headless tests) +
    `scripts/verify_obstacles_live.py` (live REST verification)

### 🚧 In Progress
- [ ] **Realistic turning physics** (circular arc with validation)
  - Speed-dependent turning radius ✅ (implemented)
  - Arc geometry calculation ✅ (implemented)
  - Generous junction buffer ✅ (implemented)
  - **Issue**: Arcs planned but instant snaps still occur
  - **Test status**: 5/6 pass, 1/6 fail (118.4° snap)
  - Need to debug why arc execution isn't happening

### 🐛 Known Issues
- **Instant heading snaps**: Detected by tests (>30° in one frame)
  - Occurs at segment transitions
  - Arc system exists but not being used consistently
  - Test suite successfully detects violations
- **Pygame font module broken**: Worked around with PIL text rendering
- **Pygame PNG support missing**: Worked around with PIL image loading

### 🔮 Future Enhancements
- Obstacle system Part 2+: movable obstacles (other cars, pedestrians) and
  the Ausweichen avoidance maneuver itself (docs/OBSTACLES.md,
  DRIVING_MANEUVERS.md §6)
- Multiple AI cars (infrastructure ready - Driver class supports it)
- Building footprints from OSM `building` polygons
- Road surface textures (asphalt, cobblestone)
- Road markings (center lines, crosswalks)
- Traffic signs
- TrafficPolice class (speed limits, traffic rules)
- Day/night cycle with dynamic lighting
- Sound effects (engine, brakes)
- Anti-aliasing via pygame.gfxdraw

## Multi-Vehicle, Collisions & Traffic (C# / Godot)

These features were added on the verified C# engine AFTER the 1:1 port from
Python was complete (the port itself is recorded in
[C_SHARP_PORT_SPEC.md](C_SHARP_PORT_SPEC.md), now closed). Anything the
Python original already had is described elsewhere in this spec; this section
is the home for the post-port additions and the forward roadmap.

### Vehicle classes & per-class dynamics — DONE

Seven real vehicle sprites (`blue` sedan, `silver`, `police`, `tan`,
`tractor`, `pickup`, `mixer`), assigned per car as `CAR_COLORS[(uid-1) % 7]`
(see [MULTI_CAR_PLAN.md](MULTI_CAR_PLAN.md)). Each has its own physical
footprint (`Config.VEHICLE_SIZES`, single source of truth for renderer + sim)
and its own longitudinal/lateral dynamics (`Config.VEHICLE_CLASS_SPECS`):
e.g. car 200 km/h / 2.8 m/s² / 4.5 m/s² lat vs truck 80 km/h / 1.3 / 3.5 vs
heavy_truck (loaded mixer) 80 / 1.0 / 3.0. `BicycleNav` uses the per-instance
`A_CRUISE`/`V_MAX`/`A_LAT_MAX` from `Car.Spec` for the speed profile, corner
speeds and understeer caps. The e2e suite and collision tests pin spawns to
the sedan (`color: "blue"`) so baselines stay comparable.

### Car-to-car collision & avoidance — DONE

Cars exert NO collision forces on each other; they brake and rest against one
another like against a wall (same semantics as `Obstacles.ApplyContactStop`).
Implemented in `DrivingGame.Sim/CarCollisions.cs`, run per physics substep in
`SimEngine.Tick`; see the Driving Ruleset R2/R6/R7 in
[DRIVING_MANEUVERS.md](DRIVING_MANEUVERS.md) §8. Two layers:

1. **Avoidance (speed cap).** Every car gets a cap = the fastest speed from
   which `CAR_BRAKING` still stops behind the nearest car ahead (plus a
   standstill gap), applied as decel-limited braking AFTER the driver's own
   longitudinal logic — so it works for every driver (BICYCLE and FREE,
   incl. U-turn/reverse) without touching their code.
   - **v1** — geometric forward corridor (±1.8 m of own axis, 40 m look-ahead,
     3 m gap); cap = `v_ahead·axis + sqrt(2·CAR_BRAKING·max(gap−3 m, 0))`.
     Broadphase = uniform 20 m grid. Handles smooth car-following.
   - **v2** — time-window pair prediction for oblique/crossing conflicts:
     each car's future path is swept every 0.1 s up to 5 s (along the
     `BicycleNav.Ref` refline when routed, else linear), earliest body-box
     overlap classified into graded TTC bands (≤1 s → stop, ≤3 s → cap,
     else mild anticipation). Prediction boxes are length-padded (+2 m/end).
     **Right-before-left**: the car seeing the other come from its right
     yields (world-frame dot-product closing test; lower-uid tie-break).
2. **Response (contact resolution).** If two body boxes overlap after the
   step, both cars roll back to their pre-step pose and stop — no
   interpenetration, no teleport.

Per-class footprints are used throughout collision math (per-pair bumper gap
in v1, per-car prediction boxes in v2, real body corners in `Resolve`).
Verified by `DrivingGame.Sim.Tests/CollisionTests.cs` and the mixed-fleet
fig8 regression; 96/96 sim tests green. `CollisionsEnabled` flag +
`--no-collisions` CLI arg exist for isolation.

### Flat crossing maps & right-of-way stress — DONE

`TestMaps.cs` carries two ground-level degree-4 fig-8 crossings on the
`basic` map — tile (2,3) "Right-of-way figure-8" (WITH Vorfahrt signs) and
tile (3,3) "un-signed figure-8" (right-before-left) — so the two branches
truly interact at grade. e2e scenarios `fig8_xing` (signed) and
`fig8_xing_plain` (no signs) run 50 cars (25/direction) for up to 300 s of
sim time or until the first crash. (The older single-branch density probe
`fig8_stress` is currently skipped pending a spawn-placement fix.)

### Driver profiles (Cautious / Normal / Sporty) — PLANNED

First instances of the Driver-axis style policies (below). A per-car speed
factor assigned at spawn, applied where `vTarget` is finalized
(`BicycleNav.part3.cs`) — style acts at follow time, so it never invalidates
the raceline cache:
- **Cautious** ("Miss Daisy"): target ≈ 0.5× profile max, big gaps.
- **Normal**: 1.0× (today's behavior; default).
- **Sporty**: 1.0× but full acceleration, minimal margin.
Work: spawn API param (default Normal); factor in the vTarget finalization;
test mixed profiles on a shared route (no collisions, stable ordering).

### Per-vehicle raceline & kinematics geometry — PLANNED

Today non-sedan vehicles have realistic sizes, dynamics and car-to-car
collision boxes but still plan the driving line and steer with a **sedan's**
body envelope: `BicycleNav.WHEELBASE` is a global `2.7`, `Raceline`'s
corridor erosion uses the global `Config.CAR_WIDTH`, and `SolveCacheKey`
carries no vehicle geometry. This is a known inconsistency the "never
physically impossible" rule will eventually force us to fix. For a sedan
at R = 20 m the front-axle off-tracking is ~0.18 m (negligible, which is why
the width-only corridor "worked"); for the ~7–8.4 m trucks it is ~0.6–1.2 m.
Work items:
1. Single source of truth for per-car wheelbase + front/rear axle offsets
   (width/length already shared via `Config.VEHICLE_SIZES`).
2. `LegalCorridor`/`SolveLine` take the vehicle's half-width AND overhangs
   and constrain the SWEPT ENVELOPE (derived front-axle path, body corners),
   not just the reference point; add the geometry to `SolveCacheKey`.
3. Make off-road / lane-guard / bumper checks per-vehicle (still sedan-sized).
4. Per-car wheelbase in the bicycle-model kinematics (corner SPEED is already
   per-class via `A_LAT_MAX`).
5. Keep the sedan as reference for existing e2e tolerances; truck scenarios
   need adjusted expectations.

### Traffic participants: trucks, bicycles, pedestrians — PLANNED

Participant types beyond a single car class, via **two orthogonal, composed
hierarchies** — what a participant IS vs how it is DRIVEN:
- **Vehicle axis** (physical): `TrafficParticipant` → `RoadVehicle` /
  `Pedestrian`; road vehicles carry a `VehicleSpec` (mostly data — subclass
  only when physics LOGIC differs, e.g. articulated truck).
- **Driver axis** (behavior): the existing `Driver` seam, extended with style
  policies (cautious/aggressive: limit fraction, following distance, reaction
  time, lane-change aggressiveness). N vehicles × M drivers, no N×M classes.
- Pedestrians have no driver (intrinsic wander/cross/wait); a playing child
  is a parameterized pedestrian. All stochastic behavior uses seeded RNG to
  preserve determinism.

The cached raceline solve stays keyed on (route geometry, VehicleSpec);
driver style acts at follow time, so it never invalidates the line cache.

**Truck left-turn encroachment (Flankieren):** on narrow two-way streets a
Sattelschlepper can often only take a tight corner by pulling wide into the
oncoming lane early. The centreline bound must become CONDITIONALLY relaxable
for wide/long specs — a two-pass solve gated on a feasibility check (relax
`lo` into the oncoming lane, bounded by its far curb, ONLY when the normal
solve is infeasible for the class), never a style option. It needs
yielding/right-of-way from the driver axis + collision avoidance, so it lands
with the mixed-traffic work.

**Acceptance (Gate G6):** a mixed-traffic scenario (car + truck + bicycle +
pedestrian incl. playing child) passes the stress invariants; determinism
verified (same seed → same run).

### Parallelism — PLANNED (optional)

The engine is deliberately concurrency-ready: a car update is a pure function
of (own state, immutable world snapshot), with shared state confined to the
sequential pre/post passes. Turning on parallelism is a one-line change
(`foreach` → `Parallel.For` across cars) — done only when a measurement
demands it, with an explicit shared-state audit and a determinism re-check.

## Performance

- **FPS**: 109 fps @ 4× zoom with 1970 segments
- **Rendering**: Vector polygons drawn per frame
- **Database query**: <1 second for Kleinmachnow area
- **Memory**: ~50 MB for full network

## Testing

### Unit Tests (headless, no game process needed)
```bash
python -m pytest tests/test_road_network.py tests/test_obstacles.py
```
- `test_road_network.py`: 10 unit tests — lat/lon projection, spatial
  queries, snapping logic.
- `test_obstacles.py`: 36 tests — placement validation and lane alignment
  (incl. corners/roundabout), SAT collision geometry, stop-on-contact
  physics, layout save/load, `/obstacles` REST handlers, headless palette
  drag handling.

The full two-layer testing setup (headless pytest + live REST-driven e2e)
is documented in `docs/TESTING.md`.

### REST API Tests
```bash
# Terminal 1: Start game with API
python -m src.main --api

# Terminal 2: Run tests
python tests/test_api.py
```

Tests:
- Health check
- Basic driving (accelerate, brake, coast)
- Turn monitoring (off-road detection)
- Screenshot capture

### Comprehensive Turn Tests
```bash
# Terminal 1: Start game with API (synthetic test map)
python -m src.main --map basic --api

# Terminal 2: Run turn tests
python tests/test_turning.py
```

The default mode runs the **deterministic suite**: 18 named scenarios on the
synthetic `basic` test map (90° corners, T-junction, Y-intersection, 4-way
crossroads, one-way street, S-curve, hairpin, sweeping curve, roundabout,
sliver junction). Each test:
- Teleports to a KNOWN start point (exact position + heading)
- Accelerates to target speed
- Activates the turn signal (or none, for `straight`)
- Monitors every ~50 ms for: off-road violations, instant heading snaps
  (>30° per frame), teleports/unexpected jumps, and arrival at the exact
  expected end segment (then drives to the segment's far end and stops)
- Captures a screenshot on violation

### Obstacle Verification (live)
```bash
# Terminal 1: Start game with API (synthetic test map)
python -m src.main --map basic --api

# Terminal 2: Place a parked car in the lane via POST /obstacles,
# drive into it, assert stop-on-contact
python scripts/verify_obstacles_live.py
```

`--random` instead teleports to random locations on whatever map is loaded
(works with real OSM data too); `--only <start_point> <direction> <speed>`
runs a single scenario for fast debugging.

#### Turn Test Output

Each scenario prints, at the start:
- **What is running** — a one-line human description, e.g.
  `🧪 Test: Following a zig-zag S-curve road`
- **Last run** — whether this exact scenario passed the previous time it ran
  (`Yes - passed (...)`), or not, with the human-readable failure reason, e.g.
  `No - cut the corner and drove off the road (2026-08-18T12:00:00)`.
  First-ever runs show `never run before`.

At the end, a colored verdict:
- green `✅ PASSED`
- red `❌ FAIL: <reason>` — e.g. `cut the corner and drove off the road`,
  `took the wrong route (ended on segment 13, expected 14)`,
  `game process ended mid-test (window closed or physics watchdog crash)`.

Colors are ANSI and auto-disabled when stdout is not a TTY (e.g. when the
output is redirected to a file).

#### Results File (`tests/turning_results.json`)

Every scenario result is saved to `tests/turning_results.json` (next to the
test script) immediately after the scenario finishes, so a run that is
interrupted mid-way never loses completed results. Structure:

```json
{
  "updated": "2026-08-18T12:00:00",
  "last": {
    "s_curve|straight": {
      "passed": false,
      "reason": "took the wrong route (ended on segment 19, expected 20)",
      "timestamp": "2026-08-18T12:00:00",
      "final_segment": 19,
      "expected_end_segment": 20
    }
  },
  "history": [ { "scenario": "s_curve|straight", "passed": false, "...": "..." } ]
}
```

- `last` is keyed by `"<start_point>|<direction>"` and is what the next run
  reads for the "Last run" line.
- `history` keeps the most recent 500 entries (capped).
- Random-location runs have no stable scenario key and are not persisted.

#### Interruption Handling (Ctrl-C / Window Closed)

The test script detects user interruption and stops cleanly instead of
falling through all remaining scenarios with connection errors:
- **Ctrl-C** — a SIGINT handler sets a flag and prints a notice; the suite
  finishes the in-flight scenario, saves results, and exits with code 130.
- **Game window closed** — the game process dies, so the API stops answering;
  the in-flight scenario records `game process ended mid-test`, and the suite
  detects the dead API between scenarios and stops scheduling new tests.

Either way, all completed scenario results are already saved to
`tests/turning_results.json` (each scenario is persisted as soon as it
finishes), and the exit code is 130 (conventional "terminated by signal").

### REST API Endpoints

Start with `--api` flag:
```bash
python -m src.main --api
```

API runs on http://localhost:5001

Endpoints:
- `GET /health` - Health check
- `GET /state` - Current game state (position, speed, on_road, etc.)
- `POST /control` - Send control inputs (accelerate, brake, steer, blinkers)
- `POST /reset` - Reset all controls
- `POST /teleport` - Move car (random or specific location)
- `POST /toggle` - Toggle features (breadcrumbs, validator, mode)
- `GET /screenshot` - Capture current frame as PNG
- `POST /wait` - Wait for condition (segment change, speed, etc.)

See `docs/REST_API.md` for full documentation.

### Smoke Test
```bash
python -m src.main --smoke 60  # Run 60 frames headless
```

### Manual Testing
```bash
python -m src.main
```

## Controls Summary

| Key | FREE Mode | BICYCLE Mode |
|-----|-----------|--------------|
| W/↑ | Accelerate | Gas (manual override) |
| S/↓ | Brake (fresh press at standstill = reverse) | Brake (manual override) |
| A/← | Steer left | Toggle left blinker → turn left where still possible |
| D/→ | Steer right | Toggle right blinker → turn right where still possible |
| Q / E | Left / right blinker (momentary) | — |
| U | — | Request U-turn (Wenden) |
| TAB | → BICYCLE mode | → FREE mode |
| C | Snap camera to car | Snap camera to car |
| B | Toggle breadcrumb trail | Toggle breadcrumb trail |
| R | Random location (teleport) | Random location (teleport) |
| V | Toggle physics validator | Toggle physics validator |
| ESC | Emergency stop + screenshot to clipboard | Emergency stop + screenshot to clipboard |
| +/- | Zoom in/out | Zoom in/out |
| Scroll | Zoom in/out | Zoom in/out |
| Middle mouse | Pan map | Pan map |

## Game Loop (60 FPS)

1. **Input**: Process keyboard & mouse events
2. **Physics**: Update car position, speed, heading (mode-dependent)
3. **Collisions**: Check off-road, map edges (FREE mode only)
4. **Watchdog**: Detect teleportation (>50m jumps)
5. **Camera**: Update viewport (smooth follow if moving)
6. **Render**:
   - Roads (direct polygon drawing)
   - Car (sprite + lights + blinkers)
   - HUD (speed, mode, indicators)
   - Minimap
7. **Display**: Flip buffer (vsync at 60 Hz)

## Physics Validator ("Physics Judge")

**Independent validation system** that runs separately from car physics to detect **hard physics constraint violations**.

### Philosophy: Hard Invariants Only

PhysicsValidator checks **only constraints that can NEVER be violated** by the laws of physics:

✅ **Checked** (always impossible):
- Position discontinuity (teleportation)
- Rotation discontinuity (instant heading snap)
- Solid object overlap (collisions)
- Going through solid boundaries (off-road)

❌ **NOT Checked** (can be exceeded by external forces):
- Maximum lateral acceleration (lorry crash can exceed this!)
- Maximum speed (external forces can push car faster)
- Speed limits (traffic rule, not physics)
- Lane discipline (design preference, not physics law)

**Key insight**: If a lorry crashes into a car sideways, the car CAN experience huge lateral acceleration. This is physically possible! So it's NOT a validation check—it's a **design constraint** used during turning calculation.

### Architecture
- **Separate class** (`PhysicsValidator`) - not embedded in `Car`
- **Toggleable** with V key (enabled by default during development)
- **Per-car state tracking** - supports multiple cars
- **Performance-friendly** - disable for proven-good cars

### Current Checks
1. **Teleportation detection**: Position jumps > 50m
2. **Instant heading changes**: Rotations > 30° in one frame (BICYCLE mode)
3. **Off-road violations**: Car leaving road in BICYCLE mode
4. **Collision detection**: (TODO) Two cars occupying same space

### Future: TrafficPolice Class

For **traffic rules** (not physics), we'll create a separate `TrafficPolice` class:
- Speed limit violations 🚔
- Red light running 🚦
- Stop sign violations 🛑
- One-way street violations ➡️
- Lane discipline checks

These are **legal constraints**, not physics constraints.

### Usage Pattern
```python
validator = PhysicsValidator(enabled=True)

# Game loop:
car.update(dt, network, control_input)
validator.check(car, dt, network)  # Independent check

# After intentional teleport:
car.teleport_random(network)
validator.reset_car_state(car)  # Skip next 5 frames
```

### Benefits
- ✅ Separation of concerns (physics vs validation)
- ✅ Can be disabled for performance
- ✅ Re-enable when experimenting with new features
- ✅ Works with multiple cars
- ✅ Easy to extend with new checks

## Debug Features

### Teleportation Watchdog Output
```
======================================================================
TELEPORTATION DETECTED!
======================================================================
Old position: (1280.9, 1566.7)
New position: (4351.6, 3267.1)
Distance: 1755.0m (max allowed: 50.0m)
Speed: 0.0 m/s (0 km/h)
Mode: bicycle
Segment: 0, Progress: 0.500, Forward: True
dt: 0.0170s
======================================================================
[Stack trace follows]
```

### Frame Dump
```bash
python -m src.main --dump  # Saves frame 30 to /tmp/car_frame.bmp
```

## Technical Decisions

### Why Direct Polygon Rendering?
- **No pixelation** on zoom (vector vs raster)
- Real-time width scaling with zoom
- Smooth rounded caps on segments
- 109 FPS with 1970 segments (fast enough)

### Why Two Driving Modes?
- **FREE**: Classic driving game feel, full control
- **BICYCLE**: Relaxed driving — the player only gives intent (gas/brake/blinker), the kinematic bicycle model executes it; focus on route planning and realistic turn signals

### Why Teleportation Watchdog?
- Catches segment transition bugs early
- Provides detailed debug info
- Prevents game-breaking jumps

---

### Why Shapely-Based Paved-Area Polygons (not per-segment rectangles + ad-hoc fillets)?
- Hand-rolled trigonometric "fillet" patches for junction corners went through several buggy iterations (wrong-side circles, self-intersecting arcs from a `cos`/`sin` mix-up, reflex-angle loops) before landing on the current approach
- Buffering an already-corner-rounded centerline with Shapely is the standard, well-tested way to get a smooth curve on **both** edges of a bend, not just the outer one
- Building one authoritative polygon and sharing it between rendering AND the on-road physics checks eliminates an entire class of "looks fine but registers as off-road" (or vice versa) bugs from independently-approximated geometry

---

**Last Updated**: 2026-08-25  
**Status**: ✅ Actively maintained - road corner rendering + on-road physics unified and calibrated against real-world turning behavior  
**Version**: 0.96 (fully playable, comprehensive test suite, professional sprite, REST API, Shapely-based road geometry)  
**Test Results**: `corner_right_entry` right-turn test passing (smooth arc, stays on road, realistic ~15 km/h corner speed)  
**Lines of Code**: ~6000+ lines (including test infrastructure)
