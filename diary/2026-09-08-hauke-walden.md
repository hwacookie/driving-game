# Diary — 2026-09-08

## 2026-09-08 (hauke-walden)
- Worked on: settable car color via API + replacing the tinted sprite set with
  seven real vehicle sprites extracted from a top-down photo.
- Done:
  - **Settable car color (user decision):** `POST /teleport` accepts
    `"color"` (validated against `Config.CAR_COLORS`, 400 on unknown).
    Explicitly NOT a respawn-until-color-matches hack in the QA script —
    color is a first-class feature of the spawned car (`TeleportCommand.Color`,
    applied in `SimEngine.ApplyCommand` after creation; validation at the API
    layer because commands apply async and bad ones are only logged).
  - **New sprites:** user supplied a top-down photo of 7 vehicles, first on
    asphalt (hard to segment), then the same scene on flat green screen
    (`assets/source/vehicles_green.png`). `tools/make_car_sprites.py`
    (numpy, no ML) extracts each vehicle: greenness metric
    (`G - max(R,B)`) → alpha ramp (smooth edges), green despill on partial-
    alpha rim pixels only. Output: `assets/car_{blue,silver,police,tan,
    armored,pickup,mixer}.png` (RGBA, nose up; the blue sedan was nose-down
    in the photo → `DEFAULT_FLIPS = ["blue"]` so plain re-runs stay correct).
    Old tinted PNGs (`car_64x128_{blue,yellow,white}`) deleted; classic red
    kept. All 7 visually verified (cutout quality + orientation per vehicle).
  - **Renderer:** `_carTextures` now loads all 8; new `CarSizes` table keeps
    photo proportions (blue sedan = the 1.8×4.4 m car box, mixer 2.16×5.65 m);
    sprite scale uses texture pixel size; corner lights AND the HUD label now
    follow each sprite's real W/L instead of the sedan constants (verified on
    the mixer: headlights at cab corners, taillights at the drum end).
    `CarColors` minimap dots hand-picked for distinguishability.
  - **Palette:** `Config.CAR_COLORS` = 8 model names, default assignment
    `(uid-1) % 8`; "color" now names a vehicle model, not a tint. Docs updated
    (`REST_API.md`, dated note in `MULTI_CAR_PLAN.md` superseding the old
    palette decision), `light_check.py --color` choices follow.
- Pitfall learned (cost ~20 min): Godot 4.7 silently falls back to the last
  good assembly in `.godot/mono/temp` when the startup compile of the root
  project FAILS — no obvious error, just stale code + `KeyNotFoundException`s
  for new dictionary keys. And `dotnet build DrivingGame.Server/...` does NOT
  compile `MapRenderer.cs` (root Godot project only). Rule: after any
  MapRenderer change, build the ROOT `DrivingGame.csproj` and check
  `/tmp/godot.log` for "Cannot instantiate C# script". (The actual bug was a
  value-tuple vs Nullable mix-up in the HUD-label edit.)
- State: fresh server + Godot window running with the new sprites; all eight
  vehicle types verified in-game (rendering, rotation, proportions, light
  corners). The lights-report open issue (front/rear light placement with the
  car facing south) is still open — re-checkable on any sprite via
  `tests/light_check.py --color <name>`.

## 2026-09-08 (continued, hauke-walden)
- Done:
  - **Classic red dropped** (user decision: style mismatch with the photo
    vehicles): palette is now the 7 photo vehicles, `red` → 400 at /teleport,
    all fallbacks/defaults default to `blue`, `car_64x128.png` deleted.
  - **`armored` → `tractor`** (user correction: the sprite is a
    Sattelschlepper tractor unit / engine part, not an armored vehicle):
    asset, renderer tables, Config, tool script, docs all renamed; verified
    (`armored` → 400).
  - **Truck sizes set to real dimensions** (photo drew them too short):
    tractor 2.55 × 7.0 m, mixer 2.5 × 8.4 m in `CarSizes` (sedan stays the
    1.8 × 4.4 m reference). Renderer stretches textures to these sizes;
    side-by-side screenshot confirms both now clearly read bigger than the
    sedan.
  - **Raceline rework documented, NOT implemented** (user decision):
    new section in `docs/C_SHARP_PORT_SPEC.md` after Phase 2 — per-vehicle
    dimensions are missing from the sim (global sedan box everywhere);
    wheelbase DOES change the legal target line (off-tracking ≈ L²/2R:
    sedan 0.18 m @ R=20 vs ~16 m for a 30 m extreme truck); work items: per-
    car geometry source of truth, swept-envelope corridor (front-axle path
    √(R²+L²) constraint), cache key, collision boxes, bicycle wheelbase.
- State: server + Godot running with the 7-vehicle palette and resized
  trucks. Run-Test button issue still open (repro done: clicks DO start
  tests; defect = stale `running` state after a finished run → 409 on the
  next click; abort-and-restart not implemented).

## 2026-09-08 (continued 2, hauke-walden)
- Done:
  - **`tests/car_line_check.py`** (manual QA like light_check.py): lines up
    ALL 7 vehicle types on a long straight road, picks the segment itself
    via GET /segment/{idx}, verifies positions vs /state (±0.5 m). Both
    manual scripts now documented in docs/TESTING.md.
  - **Per-class dynamics** (user: truck accel + top speed + cornering speed
    must be car-class specific): `Config.VEHICLE_CLASS_SPECS` —
    car (blue/police/tan) 200 km/h, 2.8 m/s², 4.5 m/s² lat; compact
    (silver) 180/2.5/4.5; pickup 180/2.4/4.0; truck (tractor) 80/1.3/3.5;
    heavy_truck (mixer) 80/1.0/3.0 — grounded in real figures (DE 80 km/h
    truck limit, UMTRI rollover thresholds, pickup skidpad ~0.7 g).
    `Car.Spec` + per-instance A_CRUISE/V_MAX/A_LAT_MAX in BicycleNav
    (ctor from Car.Spec); FREE mode uses the same values. Verified live:
    mixer accel 1.00 m/s² (blue 2.79), profile flat at class V_MAX, corner
    speed ~12 km/h vs sedan ~14.5 (theory ratio √(4.5/3.0)=1.22 ✓).
  - **Side finding:** a no-destination car on a dead-end road plans to park
    at the road end (brake & park plan, A_PARK=3.5) — looks like a speed
    "cap" but is intended behavior; that's why the mixer never reaches its
    80 km/h plateau on the 300 m test straight.
  - **Test fallout fixed:** xunit CollisionTests + e2e suite now pin spawns
    to `color: "blue"` (sedan class) — without it the uid-based palette
    cycle would hand truck classes to later scenarios and break all sedan-
    calibrated baselines. 93/93 unit tests green; e2e sanity run
    (corner_right_entry, tjunction_from_top, oneway_entry) all PASSED.
  - Spec section "Raceline rework — per-vehicle dimensions" updated:
    dynamics done, geometry (wheelbase/corridor/collisions) still open.
- State: fresh e2e-driven game running; Godot window self-terminates after
  test runs. Run-Test button issue still open.
