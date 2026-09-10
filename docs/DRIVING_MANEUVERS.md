# Driving Maneuvers — Specification

All maneuvers describe how the car behaves in the simulation. The reference
line (BicycleNav) controls lane position and speed; the Driver sets blinkers
and intent (throttle / brake).

---

## 1. Parking (Pull-Over)

**Trigger:** Right blinker + brake (automatically, when the distance to the
target reaches the **speed-dependent** comfortable braking distance —
`v²/(2·A_PARK)` plus a small reaction reserve; at 65 km/h ≈ 50 m, at
20 km/h only ~7 m. No fixed value. Or manually via keyboard/API).

| Phase | Description |
|-------|-------------|
| **Initiation** | Right blinker on. Throttle off, brake on. Speed drops to ~5–10 km/h. |
| **Swerve right** | The reference line blends from the normal position to the right road edge (`PARK_BLEND_START_M` → `PARK_BLEND_END_M`). The car drives a gentle right curve to the edge. The target distance to the edge is the result of a search: as close as possible, without a car corner reaching over the curb during the diagonal travel. |
| **Align parallel** | In the final section (`PARK_ALIGN_M`) the reference line is constant at the edge — the car drives straight parallel to the road edge. Here it is no longer controlled by Pure Pursuit (which arrives at the line *turning*), but with a Stanley law: steering angle = heading error + lateral offset term. Both terms go to zero together — exactly "flush with the edge and parallel to it". |
| **Stopping** | Speed goes to 0, with the deceleration tapering out (progressive braking) — no jerk at the standstill. The car stands with the wheels parallel and as close to the right edge as possible; at a target flag the front bumper stands at the flag. |
| **Blinker off** | As soon as the car is stopped (`speed < 0.1 m/s` and `dist_to_dest < 1.0 m`), the blinker is switched off automatically. |

Reproducible headless: `.venv/bin/python scripts/sim_park.py [startpoint] [--dest]`
logs the complete stopping process (phase, deceleration, remaining distance,
lateral offset, heading error) and measures at the end the curb distance and
bumper position.

### Parameters

- **Trigger distance** — speed-dependent: `v²/(2·A_PARK)` + reaction reserve
  (explicit decision: no fixed value)
- `A_PARK = 3.5 m/s²` — comfortable braking when parking (~0.35 g), no full
  braking
- `PARK_CREEP_SPEED_M = 2.0 m/s` (7 km/h) — the speed while driving slowly
  in the swerve zone (band 5–10 km/h)
- `PARK_STOP_TAU = V_C / A_PARK` (≈ 0.57 s) — time constant of the roll-out.
  In the final section the target speed is **proportional to the remaining
  distance** (`v = d / τ`), so the deceleration `a = v / τ` tapers out with
  the speed and is zero at the standstill. τ is chosen so that the
  deceleration at the start of the roll-out is exactly `A_PARK`. Below
  `PARK_ROLL_END_M_S = 0.3 m/s` a small constant deceleration
  (`PARK_ROLL_END_A = 0.6 m/s²`) takes over, otherwise the car would drive
  slowly through the exponential tail for seconds.
- **Zone geometry — derived, not freely chosen** (the drift is sized to ~3 m,
  from driving slowly at 2 m/s):
  - `PARK_ALIGN_M = 3.0` — straight section at the edge for aligning. Shorter
    was not possible: the controller lags the drift line by ~0.35 m, a car
    that still swerves while stopping stands diagonally (measured 5–12°).
  - `PARK_BLEND_END_M = max(PARK_ALIGN_M, V_C·τ)` (= 3.0 m) — swerve complete,
    constant offset until the standstill.
  - `PARK_BLEND_START_M = V_C·PARK_SWERVE_S + PARK_BLEND_END_M` (≈ 6 m) —
    with `PARK_SWERVE_S = 1.5 s`, i.e. 3 m of drift. Shorter forces the corner
    check to a parking spot further out; longer brings nothing more.
  - *(Historical: this zone used to be documented at 4.0 / 12 m, from before
    the brake-&-park plan — at 2 m/s (driving slowly) a 40 m zone would be
    ~20 s of walking pace. The values above are the current ones.)*
- **All distances are measured from the STOPPING POINT**, not from the line
  end: at the flag target the front bumper stands at the flag (reference
  point = rear axle, so `FRONT_OVERHANG_M` ahead of it), at a dead end a whole
  car length before the end of the asphalt.
- `PARK_TRACK_LOOKAHEAD_M` — short look-ahead for tight tracking of the
  reference line
- `PARK_ALIGN_GAIN = 1.0` — lateral-offset gain of the Stanley controller in
  the final section
- `PARK_ALIGN_LATERAL_M` — lateral tolerance for "on the line"

### Variant: Dead-end / route end

Same maneuver as parking, without a target flag: the route ends at a real dead
end (node with degree ≤ 1 — no road leads on). The car must **not** drift to
the center line there (old behavior), but pulls to the **right road edge** —
as far right as possible without leaving the paved area — and stops.

- **Target position**: rightmost drivable position = right road edge − half
  car width − `ROAD_EDGE_TOLERANCE_M` (0.5 m); all four wheels stay fully on
  the paved area. Bounded by the real road geometry (Shapely polygon), never
  by a fixed fraction of the road width — a narrow farm road and a wide main
  road each end at their own edge.
- **Why not the center line?** At a normal crossing the offset to the center
  line is blended, because both neighboring segments share the common node and
  the hand-off should have no lateral jump. At a dead end there is no hand-off
  to a continuing segment — the car simply stops. The natural position is the
  right curb.
- **Execution (physically plausible)**: a gentle, distance-based swerve over
  the last ~20 m (same window as the center-line blend), no lateral snap or
  teleport; the lateral control stays within what the car can do at its
  current speed. The edge blend replaces the center-line blend only at a
  *real* dead end; at any crossing with a continuing segment (executed arc,
  slide-past, or straight-worthy continuation) the center-line blend is
  unchanged.
- **Braking** to the standstill is unchanged (physics-based braking distance
  + 5 m safety reserve); only the lateral target changes from center line to
  right edge.
- **Optional: turn around at the end.** If the route is to be repeated, after
  stopping at the edge the dead-end turn-around applies (180° heading flip,
  small fixed nudge back onto the roadway, blinker off) — the nudge keeps the
  reached edge side.

### Variant: Parking-in from various start lateral positions

**Requirement:** Parking-in (back-in) must work regardless of the start
lateral position — from any lane on our side of the road. In reality, other
parked cars force you to drive further left; a parking plan must not only be
buildable for the case "starting right at the edge".

**Test (basic map):** e2e scenario family on a straight **parking avenue**
(tile 4,1, no crossings, no obstacles): two-way, per direction two 3.5 m
travel lanes plus a 2.7 m parking lane at the curb (19.4 m wide, 6 lanes
total). The center line is **solid** (crossing = oncoming traffic), in both
parking lanes there are white "P" markings every 100 m. Parking lanes end at
least **5 m before a crossing** — visually too
(`config.PARK_LANE_END_GAP_M`). Route and target flag at the right edge stay
the same in all cases; only the lateral start position varies — always on our
side of the road (offsets relative to the normal position = center of the
outer travel lane, 5.25 m from the center line):

| # | Start lateral position |
|---|---------------|
| 1 | Center of the left (overtaking) lane (−3.50) |
| 2 | Center of the right (normal) lane (0.0) |
| 3 | Center of the parking lane (+3.10) |

**Lane change before parking:** If the car starts LEFT of the normal position
(left lane), it first changes right — a human driver never parks out of the
overtaking lane. If it starts IN THE PARKING LANE, it first changes left back
into traffic: **In the parking lane you may not drive, only park** (user
rules; both rules replace the old rule from 2026-08-27 to keep the start line
until the flag).

The lane change happens **briskly and with a speed-dependent distance** (user
rule): it lasts about `LANE_CHANGE_TIME_S` (2.5 s), i.e. distance = speed ×
2.5 s (clamped to 20–90 m). The reference line blends over this distance into
the right lane and is there `MERGE_SETTLE_BEFORE_M` (40 m) before the flag;
only then does the parking sequence (pass by + back-in) take over, planned
from the normal position. **Before the lane change it always signals** (user
rule): the blinker goes on `MERGE_SIGNAL_AHEAD_M` (30 m) before the zone
start and stays on until the car has reached the new line. The blend length is
frozen as soon as the car has started the change (no re-planning under the
wheels). Starts within the travel strip (right of the normal position, but
not yet in the parking lane) keep their line.

Two pitfalls that would otherwise drag the change out again (measured):
1. The entry speed is **not** the profile: the profile is a limit, not a
   prediction — from a standstill the car only builds up A_CRUISE per meter.
   Planning uses min(profile, build-up) iteratively over the zone.
2. On straight routes the curvature solver of `min_curvature_offsets` must be
   skipped: it minimizes the second derivative of the offset profile and
   stretches a 35 m blend to ~180 m (measured). Straight = constant offset =
   zero curvature = already optimal; the solver only runs where the road
   really curves. The spawn in the parking lane (8.35 m) lies 0.37 m outside
   the legal corridor (hi = 7.98 m) and is clamped there — the car first
   slides briefly inwards, then changes lanes.

**Success criterion:** In all three cases the car completes the parking at
the flag: parallel to the edge, as close to the curb as allowed (in the
parking lane), `parked == true` — the same end result regardless of the start
lateral position.

---

## 2. Pulling Out

**Trigger:** First frame after spawn/teleport (`_pull_out_done == False`).
The Driver automatically sets **left blinker** + throttle.

| Phase | Description |
|-------|-------------|
| **Initiation** | Left blinker on. Throttle on, brake off. Speed limited to ~18 km/h. Look-ahead reduced to 1.5 m (maximum steering). |
| **Left into the lane** | The reference line blends from the right edge (`edge_offset`) to the normal position (`base_offset`). The car steers left and swerves into the right travel lane. |
| **Normal position reached** | After one frame `_pull_out_done = True`. Blinker off, speed limit off, look-ahead normal. The car accelerates to cruising speed. |

### Parameters

- `PULL_OUT_START_M = 20.0` — blend-zone end (m from route start)
- `PULL_OUT_END_M = 5.0` — blend-zone start (m from route start)
- Pull-out is active for only **one frame** — after that the car drives
  normally

---

## 3. Driving Straight

**Trigger:** No blinker active, no parking, no pulling out.

| Phase | Description |
|-------|-------------|
| **Normal position** | Reference line at `base_offset` (right travel lane, centered in the lane). The car accelerates to cruise speed (`A_CRUISE`) or brakes in curves (`A_BRAKE`). |
| **Curves** | The speed profile throttles speed before curves. Pure Pursuit control with speed-dependent look-ahead. |

---

## 4. Turning (Left / Right)

**Trigger:** Left/right blinker (manually via key or API). `IntendedTurn()`
reads `PendingTurn` from the Driver.

| Phase | Description |
|-------|-------------|
| **Initiation** | Blinker in the turn direction on. The route is rebuilt (`MaybeRebuild()`), this time over the chosen arm of the crossing. |
| **Line choice** | There is **no fixed turn offset anymore**. The driving line is the solution of an optimization (`Raceline.cs`): minimal curvature within a corridor whose boundaries are the two hard rules — never off the roadway, never onto the oncoming lane. The classic line "wide in, apex in, wide out" is **coded nowhere**; it falls out of the geometry. |
| **Curve** | The speed profile derives the curve speed directly from the curvature of the *optimized* line: `v = sqrt(a_lat / kappa)`. No special cases, no capping. |
| **Back to normal position** | Follows naturally — behind the crossing the least-curvy line is the lane center again. |
| **Blinker off** | The nav auto-clears the blinker once the turn is executed (curve-exit reached). |

### Parameters

- `A_LAT_MAX = 4.5 m/s²` (per vehicle spec, `VehicleSpec.LatAccelMax`) —
  lateral acceleration limit (understeer)
- `A_LAT_PLAN_FRACTION = 0.7` — the profile only plans with 70% of it. If you
  plan with the full value, the yaw rate is already saturated at the apex and
  the controller has **no reserve to correct**: measured, Pure Pursuit then
  ran 1 m inside its own line — enough to cross the center line with the
  flank.
- `LANE_CENTRE_MARGIN_M = 0.5` — distance of the corridor lower bound to the
  center line. Deliberately includes reserve for the controller's lag error:
  the corridor constrains the *line*, but the hard rule applies to the *car*.
- `CURVATURE_WINDOW_M = 1.0` — fixed physical window width of the curvature
  measurement.

### Crossing center (the white dot)

Going straight and when **turning right**, the node (the white dot the
renderer draws at every node of degree ≥ 3) must be **left** — that is just
"drive right" at the place where the center line ceases to exist.

When **turning left** this explicitly does **not** apply. German traffic code
(StVO) § 9 para. 4: *"Left-turning drivers must turn in front of each other,
unless the traffic situation or the design of the crossing requires passing
around each other."* The default case is **in front of** — oncoming left-
turners pass driver-side to driver-side, each turns before the center, and the
dot is thus **right**.

This is no fine point: if you force left-turners to keep the dot left, the
only remaining line is tighter than the car's turning circle (measured 1.5 m
vs. 3.46 m minimum) — the constraint was simply wrong for this maneuver.

On **one-way streets** the rule does not apply at all: there is no oncoming
lane that would need protection, and the dot is free by itself (straight and
right-turners pass it left; left-turners are excluded anyway). Applied to
one-way sections it would have pushed the line up to 0.9 m curb-side —
measured at a 3.5 m × 3.5 m one-way crossing: instead of driving straight
through, the car had to steer an S-curve at entry speed (test #10).

Additionally the bound is **eased** over `LaneEaseM / 2 = 12 m` on both
sides of the node, so a straight lane change can cross the center-line zone
right at the crossing (see §7): before the node it blends down to the exit
road's nominal position, behind the node it ramps up from 0. On constant-width
routes the eased bound stays below the nominal line — there nothing changes.

### Reachability

A blinker means "I want to turn at the next place where it *still physically
works*" (see `TURN_REWORK_PLAN.md` § 2.5). If the car is already faster than
the profile allows at its position (`REACHABLE_SPEED_TOLERANCE`), no braking
helps — then the crossing is **driven straight through** (the nav rebuilds
the route across the node) and the blinker stays on.

---

## 5. Turning Around (U-Turn On-Site)

**Trigger:** "turn around" command (API/keyboard). Left blinker on.

The system automatically chooses the strategy based on the road width:
- **Wide road** (`road width ≥ turning-circle radius ≈ 11 m`): single
  turn-around (5a)
- **Narrow road** (`road width < turning-circle radius`): multi-point turn (5b)

### 5a. Single turn-around (wide road)

| Phase | Description |
|-------|-------------|
| **Pull right** | The car swerves to the right road edge (analogous to parking, but without stopping). Speed is reduced to ~5–10 km/h. |
| **Swerve left** | Steering wheel full left. The car drives a large arc from right to left — the front-wheel track describes a half-circle across the entire road width. The rear follows with slip angle (bicycle model). |
| **Through the middle** | Heading has changed by ~180°. The car crosses the center line necessarily — this is allowed and expected when turning around. |
| **Align right** | The steering wheel is straightened or slightly corrected right, so the car ends on the now-right lane (original oncoming lane) parallel to the road edge. |
| **Continue** | Blinker off. Normal position in the new direction of travel. Throttle on. |

### 5b. Three-point turn (narrow road)

If the road is too narrow for a single arc, the car performs a classic
three-point turn:

| Step | Gear | Steering | Description |
|---------|------|---------|-------------|
| **1. Pull right** | Forward | slightly right | Slowly drive to the right edge and stop. Left blinker on. |
| **2. Left forward** | Forward | full left | Slowly forward until the front wheels are almost at the left road edge (heading ~45–60° to the left). Stop. |
| **3. Right reverse** | Reverse | full right | Back up until the car is at the right edge and the heading is turned further (~120–150°). Stop. |
| **4. Left forward** | Forward | full left | Forward gear, drive into the opposite direction and align parallel. Blinker off. |

On extremely narrow roads (e.g. ~3 m) the **four-point turn** may be needed:
steps 2–3 are simply repeated a second time — the car swings back and forth
until it is turned 180°.

### Parameters

- `TURN_AROUND_MIN_WIDTH` — minimum road width for a single turn-around (≈
  turning-circle radius)
- Speed during the maneuver: ~5–10 km/h (low enough for the tight radius,
  high enough not to stop)
- Left blinker active throughout the maneuver
- LaneGuard is temporarily suppressed during the turn (center-line crossing
  is intended)
- Reverse driving: bicycle model with negative `speed` and inverted steering
  effect

### Implementation status

The **prerequisite** — signed speed (negative = reverse) with a real gear
model (W brakes / S engages reverse, inverted yaw in reverse) — **is
implemented** (`Car.cs`; used by the back-in parking maneuver and by
FREE-mode reverse). What is **not** implemented yet is the scripted U-turn
maneuver (5a / 5b) as a distinct driver action:
- Generate the turn reference line: a U-curve back down the road (the current
  route only builds forward)
- A LaneGuard suppression flag for the maneuver (center-line crossing is
  intended)
- `TURN_AROUND_MIN_WIDTH` / strategy selection (single arc vs. multi-point)

---

## 6. Avoidance (Obstacle Avoidance)

Applies to **BICYCLE mode** (AI driving): the car avoids **static
obstacles** on its own. In **FREE mode** avoidance is the player's business;
there the car sticks to stop-on-contact (`docs/OBSTACLES.md`).

*(Note: **car-to-car** avoidance is a separate, implemented system — speed
caps + contact resolution in `CarCollisions.cs`; see the Driving Ruleset,
R2/R6/R7. This section is only about static obstacles.)*

Obstacles are placed on the road via the palette next to the minimap or via
the REST API and can be saved/loaded as a layout — `docs/OBSTACLES.md`.

### Two obstacle classes

- **Static** (this chapter): e.g. parked cars. Standing still, position known
  → pure geometry: gaps, corridor, braking distance.
- **Mobile** (later, not yet specified): other vehicles, pedestrians,
  children. They can move out of the way before the collision point and are
  often only visible at a great distance → requires the **prediction** of
  possible collisions (future occupancy instead of fixed boxes). The
  detection/decision logic is kept so that static and mobile obstacles later
  feed the same interface: "occupancy along the route".

### Detection (static obstacles)

An obstacle is **relevant** if its footprint intersects the corridor around
the reference line (corridor width = car width + reserve) and it lies ahead of
the car in the direction of travel. At the latest it is relevant when the
distance reaches the speed-dependent comfortable braking distance to a stop
behind the obstacle — `v²/(2·A_AVOID)` plus reaction reserve, as with parking
(explicit decision: no fixed value).

### Options (priority: right → left → stop)

| Option | Condition | Behavior |
|--------|-----------|-----------|
| **1. Pass right** (own lane) | Gap between obstacle and right road edge ≥ car width + `AVOID_CLEARANCE_M` (obstacle side) + `ROAD_EDGE_TOLERANCE_M` (edge side) | Reference line runs through the right gap, as close to the edge as allowed; **no blinker** |
| **2. Pass left** (cross center line) | Option 1 not possible, **and** the oncoming lane is clear within `AVOID_ONCOMING_AHEAD_M` (ahead) resp. `AVOID_ONCOMING_BEHIND_M` (behind) | Reference line crosses the center line; LaneGuard is suppressed in the avoidance area; **left blinker** on |
| **3. Stop** | No passage option is safely executable at the current position (both sides blocked, too close, too fast) | Comfortable braking with `A_AVOID` to a stop behind the obstacle, standstill gap ≥ `STOP_GAP_M` |

**Reachability as with turning (§4):** If the chosen option is no longer
physically executable, it does **not** avoid unsafely — the car brakes
(option 3). An avoidance maneuver is a maneuver like any other: nothing
impossible.

Multiple obstacles (e.g. slalom): the decision applies over the entire
relevant section — the **narrowest** free width is decisive.

### Execution

- The obstacle is fed into the raceline optimization as an **additional hard
  bound**: the corridor is shrunk by the dilated footprint of the obstacle
  (+ `AVOID_CLEARANCE_M`). The avoidance line is then simply the least-curvy
  line through the gap — the same machine as lane keeping and turning, no
  special logic.
- For option 2 the corridor lower bound (center line) is lifted in the
  avoidance area (analogous to the one-way rule in §4).
- The speed follows from the existing speed profile from the curvature of the
  avoidance line — a narrow gap automatically means slower.
- **Blinker:** The left blinker set for option 2 is held for the duration of
  the maneuver (as when turning around) and only switched off when the car is
  again fully in its own lane — the mechanical cam logic is suppressed during
  avoidance.
- **Return to the normal position:** As soon as the rear of the car has
  passed the front edge of the obstacle by `RECOVERY_MARGIN_M`, the obstacle
  bound no longer applies; the least-curvy line is the lane center again — the
  return blend follows naturally (as behind a crossing).

### Parameters (proposals — not yet implemented)

- `A_AVOID = 3.5 m/s²` — comfortable deceleration for detection and stopping
  (like `A_PARK`, no reason for emergency braking)
- `AVOID_CLEARANCE_M = 0.5` — minimum lateral distance to the obstacle when
  passing
- `AVOID_ONCOMING_AHEAD_M = 60` / `AVOID_ONCOMING_BEHIND_M = 30` — the
  oncoming lane must be clear in this range (in part 1: free of static
  obstacles; later also of predicted traffic)
- `STOP_GAP_M = 5.0` — standstill gap behind the obstacle
- `RECOVERY_MARGIN_M` — rear lead, from which the obstacle counts as "passed"

### Interaction with other maneuvers

- **Active turning** (blinker + turn blend zone): no center-line crossing for
  avoidance — only option 1 or 3.
- **Parking / turning at the target:** explicit commands take precedence; if
  the target is behind an obstacle, option 3 (stop) applies instead of driving
  over it.

---

## 7. Lane Change on Road Change (One-way ↔ Two-way)

**Trigger:** The route crosses a crossing where the road type or width changes
(e.g. a 3.5 m one-way street runs into a 7 m two-way street). The nominal
lane position jumps with it: on the 3.5 m one-way the car sits 0.5 m from the
center line (as far right as it can), on the 7 m road in the right lane at
1.75 m.

| Phase | Description |
|-------|-------------|
| **Approach** | Reference line in the nominal position of the own road — per station (`LaneBaseOffsetM`, clamped at the curb offset), not a global offset for the whole route. A global value was clamped to the narrowest segment and then forced the jump at the crossing. |
| **Transition** | A **straight diagonal** from the approach position to the exit position through the crossing — no S-curve at the node. The driver changes lanes in a straight line, not with S-curves. |
| **Exit** | Reference line in the nominal position of the exit road; from there normal straight driving. |

### Why a straight diagonal — and why only ~14 m

The transition cannot be spread out as far as one would like: on the narrow
approach road there is simply no room to sit further right — the hi bound
comes from the eroded pavement polygon and only opens up within the crossing
square. The lateral jump must therefore happen where both positions are
drivable: at the crossing. The diagonal spans `LaneEaseM = 24 m` around the
node, but is placed so that the clamping leaves no visible kink anywhere:

- **Rising** (narrow one-way → wide two-way): the diagonal starts at the last
  station where the hi bound is still below the target position — i.e. exactly
  where the pavement first gives room — and runs straight toward the exit
  nominal.
- **Falling** (wide two-way → narrow one-way): the diagonal reaches the curb
  cap of the exit road **exactly at the node**, so that what must jump can
  jump there, without a visible kink.

### Easing the center-line bound

Otherwise the "keep the dot left" bound (§4) would force a jump right at the
node: on the two-way side it applies in full strength from the first station,
but the diagonal is only there a few meters later. The bound is therefore
eased over `LaneEaseM / 2 = 12 m` on both sides of the node (see §4).
**Safety note:** in the first ~12 m after the crossing the car may temporarily
sit closer to the center line than the full 0.5 m margin — it never changes to
the other side (the diagonal stays on its own throughout). This matches the
real creeping-in behavior at crossings.

### Parameters

- `LaneEaseM = 24.0` — span of the diagonal transition (m)
- `KerbOffsetM` / `LaneBaseOffsetM` (per station) — the nominal positions

*(The old `JUNCTION_EXTRA_ROOM_M` idea is not in the current code.)*

---

## 8. Driving Ruleset (tiers & precedence)

These rules govern how cars drive — most are universal, a few (marked
*(crossing)*) apply at a signed crossing — the fig8 lemniscate crossing in
particular.

Rules are grouped into **tiers**. Precedence goes **tier over tier** (a
lower tier number beats a higher one). **Within a tier the rules are
equal** — they coexist and govern different aspects or phases, so there is
no rank between them. The rule label **R#** is only a reference (it is what
the per-car decision log cites); it is *not* a priority.

**Implementation status** per rule: *[implemented]* / *[partial]* /
*[proposed]*.

### Tier 0 — Hard invariants (a post-filter over every output)

Tier 0 is **not a set of situational decision nodes** — it is an invariant
check applied **continuously over every output** of the decision tree (a
post-filter). A maneuver that violates Tier 0 is rejected/clamped regardless
of which lower-tier rule proposed it.

- **R1 Physical validity** *[implemented]* — no motion a real car could not
  perform: no teleport, no instant heading change, no turning radius below
  the car's mechanical minimum, no position/heading desync.
- **R13 Kinematic limits** *[partial]* — bounded longitudinal and lateral
  acceleration (tire grip + comfort), not just the brake value: on the
  lemniscate's curves the lateral acceleration must stay under the
  grip/comfort limit, so a planned line that is too tight is rejected, not
  merely braked for. (Complements R1's geometry with the dynamic limits.)
  *Implemented as a planning limit:* the BicycleNav speed profile caps
  cornering speed at `v = sqrt(A_LAT_MAX / kappa)` (`BicycleNav.part2/3`),
  and steering rate is bounded by `A_LAT_MAX / v`. Not yet a post-filter that
  *rejects* a too-tight planned line — the plan is speed-limited, not refused.
- **R25 Speed limit** *(StVO §3(3))* *[implemented via config]* — never
  exceed the road's limit speed; the cap is a hard post-filter on the speed
  output. In the sim this is the per-map / per-vehicle configured maximum
  (the car's top speed is clamped to it every step).

### Tier 1 — Safety (beats every traffic rule)
- **R2 Self-protection / collision avoidance** *[implemented]* — a car never
  causes or sustains a collision; if a real body overlap is predicted it
  brakes to avoid it. Beats every traffic rule (right of way never excuses
  collision avoidance). **With multiple simultaneous threats** (e.g. a car
  ahead *and* crossing traffic), R2 and R7 agree on the **most conservative
  (strongest) required brake value** across all of them — the car brakes to
  the worst threat. On an actual contact both cars log it **with their own
  speed at impact** and switch on hazard lights.
- **R6 Staged priority response** *(crossing)* *[implemented]* — a priority
  car goes **alert** and observes instead of braking the instant a conflict
  is predicted; it brakes only if the other car fails to yield (TTC stops
  improving) for ~1 s, or it is already too late to stop. (The human-like
  application of R2 — keeps the priority car's braking stable instead of
  flickering.)
- **R7 Speed-dependent look-ahead** *[implemented]* — the avoidance
  look-ahead is at least the car's own stopping distance (reaction + braking
  + standstill gap), scaled with speed, so a fast follower stops for a
  suddenly-stopped leader. (Together with R2: the required brake is the max
  over all threats.)
- **R21 Reversing / Wenden — heightened duty (StVO § 9(5))** *[proposed]* —
  While reversing or turning around, the car must behave so no other road
  user is endangered, and move only as far as it can see (else be directed).
  It does **not** lose its formal right-of-way, but the heightened duty makes
  it **yield in practice to all traffic** it can't safely pass — including
  traffic **behind the body** (the direction of its motion) — and it bears at
  least partial fault in a collision regardless of formal priority. Its
  look-ahead / hazard detection must point along the reverse direction.

### Tier 2 — Crossing flow (coexist; each governs a phase)

Evaluated at a crossing in this order: **R4 (feasibility) → R5
(right-of-way) → R3 (commitment)** — feasibility first, then right-of-way,
then commitment.

- **R4 Enter only when sure to clear** *(crossing)* *[partial]* — enter only
  if the box can be cleared before the other diagonal's approach arrives
  (gates entry). **Evaluated before R5**: a car that cannot clear does not
  enter even if it has right-of-way. The temporal gap-acceptance test is
  implemented (`CarCollisions`, `etaS < tClearS + SignGapMarginS`) and now
  logs its own `[R4] cannot clear before car N arrives` decision, distinct
  from R5's `[R5] yield sign`.
- **R5 Right-of-way / yield** *(crossing)* *[implemented]* — the yield
  (Vorfahrt gewähren) car yields to the priority (Vorfahrt) car (for cars
  not yet in the crossing). *(proposed)* **At an unsigned crossing** (no
  priority sign) priority defaults to the vehicle **from the right** (Rechts
  vor Links, StVO §8(1)); signs 205/206/301/306 override. **At a signed
  roundabout** (215 under 205, §8(1a)), traffic **on the ring** has priority
  and entering cars yield. **At a signalized crossing** (traffic light) the
  light governs entry (red = stop, green = proceed) — *(later, not
  implemented: no signal model in the sim yet)*. *(Note: a left-turner also
  yields to oncoming straight traffic — a separate case from cross-street
  priority. Not active at the fig8, whose crossing is a through-X with no
  left turns.)*
- **R3 Committed crossing** *(crossing)* *[implemented]* — once the nose
  crosses the yield stop line the car is committed and continues; a car
  already blocking the box keeps going (else it deadlocks the cars it
  blocks). For a car already inside, R3 beats R5. **Exit:** R3 ends only
  when the **rear** (not just the nose) has left the box; the next waiting
  car may re-evaluate R4 only after that.
- **R14 Blocked-box / no entry into an occupied exit** *(crossing)*
  *[proposed]* — never enter if the crossing's exit is visibly occupied
  (e.g. a queue on the far side), **even with right-of-way** (do not block
  the intersection — derived from StVO §1(2) / §9(5)). Independent of R5;
  distinct from R4 (the *temporal* gap case) — this is the *exit blocked by
  a foreign queue* case.
- **R15 Deterministic tie-break** *(crossing)* *[proposed]* — when two cars
  cross the stop line / enter in the same frame (exact simultaneous
  conflict), resolve deterministically by arrival timestamp, then car UID —
  so the tree is reproducible at frame boundaries.

### Tier 3 — Predictability & courtesy (toward other vehicles)
- **R9 Unambiguous intent (no creeping)** *[proposed]* — a stopped or slow
  car must not creep forward ambiguously; other road users must be able to
  tell what you intend. Creeping is unpredictable and is exactly what
  triggers the brake flutter (each car assumes the other may pull away). A
  real contact is an accident (R2), so creeping that ends in contact is a
  bug.
- **R10 Braking is a [0,1] value, always logged** *[proposed]* — braking
  intensity is a continuous value `0` (coast) … `1` (maximum full brake),
  always output when a car brakes (visible, measurable intent). Pure
  telemetry — not a behavior rule. *Current state:* the car's brake is still
  a boolean (`Car.IsBraking` / `ControlInput.Brake`), not a continuous value.
- **R11 Gentle braking by default** *[partial]* — normally brake gently (the
  car behind may brake less well and needs reaction time); brake harder only
  when it becomes necessary.
- **R12 Voluntary yield (courtesy)** *(crossing)* *[proposed]* — a priority
  car may give up its right-of-way to let a long queue on the cross street
  pass. *(Optional.)*
- **R16 Anti-starvation / wait-time escalation** *(crossing)* *[proposed]* —
  independent of R12: after X s of waiting, a yield car's required gap
  **decreases stepwise** (patience decays), so a car waiting against
  sustained priority traffic does not starve indefinitely (gap-acceptance
  literature). R12 is voluntary courtesy from the priority car; R16 is the
  waiting car's own escalation.
- **R17 Signal before the maneuver** *[partial]* — blinker on a fixed
  time/distance **before** a lane change / avoidance so the intent is visible
  to others (complements R9: making intent recognizable needs a visible
  signal, not just refraining from creeping). *Implemented for lane changes*
  (`LaneChangeSignal` on from `MERGE_SIGNAL_AHEAD_M` before the merge zone)
  *and parking* (`PARK_LEAD_S` indicator lead); logs `[R17]` on the
  blinker-on transition. Not yet wired for avoidance (R8).
- **R18 Jerk limit** *[proposed]* — a maximum rate of change of the R10 brake
  value per step; adds the *temporal* component to R11's gentle braking —
  prevents abrupt brake-value jumps even when the overall value is
  "gentle".
- **R23 Zip merge / Reißverschluss** *(StVO §7(4))* *[proposed]* — when a lane
  ends or the road narrows, cars in the blocked lane merge **one-for-one
  (alternately)** with the through lane *before* the narrowing; lining up
  short and forcing the last car to jump the queue is a violation.
- **R24 No unjustified slow driving** *(StVO §3(2))* *[proposed]* — don't
  drive so slowly without reason that you impede the traffic flow.

### Tier 4 — Maneuver
- **R22 Overtaking** *(StVO §5)* *[proposed]* — overtake only **on the
  left**, only when the whole maneuver can be done without endangering or
  impeding oncoming or following traffic, at clearly higher speed, and not in
  an unclear situation or where a sign prohibits it. Signal before pulling
  out **and** before re-merging; keep adequate lateral distance (≥ 1.5 m
  inner / 2 m outer); re-merge right as soon as possible; do not impede the
  overtaken car. The **overtaken** car must not increase its speed.
- **R8 Lateral avoidance** *(last resort)* *[proposed]* — steer slightly to
  the side to get around a blocker, instead of requiring the other car to
  move first.
  **Exit:** return to the lane as soon as a free path exists. **Subject to
  R1/R2:** no avoidance that creates a new collision or violates the minimum
  turning radius.
- **R19 Retreat / stop** *[proposed]* — if lateral avoidance (R8) is
  physically impossible, retreat / stop is the second last-resort path (back
  to a clear space, or hold and wait).

### Meta — system (not a traffic rule)
- **R20 Determinism & logging** *[partial]* — every firing rule logs its
  **R# plus the triggering condition** (the per-car decision log already
  does this); required for explainability of the tree, especially at Tier-2
  equal-rank. *Currently logging:* R1 (`PhysicsValidator`), R2/R3/R4/R5/R6/R7
  (`CarCollisions`), R17 and R25 (nav / `Car`). Still silent: R13 (planned
  but not logged) and the not-yet-implemented rules (R8–R16, R18–R19,
  R21–R24).
