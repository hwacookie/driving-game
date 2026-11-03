# Driving Maneuvers — Specification

> **Status 2026-10-23 (code-verified):** the ruleset as the code stands at
> `c827710`. **The R-index is complete: R1–R36 all have an entry below** —
> R32, R34 and R35 were missing for rules that are implemented, and were added
> on 2026-10-23 (their design provenance is
> [RIGHT_OF_WAY_NEGOTIATION.md](RIGHT_OF_WAY_NEGOTIATION.md)). *[proposed]*
> means "written here, not in the code"; *[measured]* means "an invariant with
> a number attached, not a behaviour". Status tags were re-checked against
> `DrivingGame.Sim/` on 2026-10-23; a tag is a claim about the code, so change
> it together with the code, not afterwards.
>
> **Added 2026-11-02:** the over-taking rules the owner decision of that date
> started - **R37** (a queue passes front-first, one intent flag per car) and
> **R38** (a pass may not begin or continue at a crossing). Both are
> *[proposed]*, live only here, and [§9](#9-overtaking-a-slow-vehicle-a-queue-behind-an-obstruction)
> is their specification.
>
> **Changed 2026-11-03 (issue #73, branch `fix-73-over-taking-order`):** **R37**
> and **R39** are in the code now - their entries carry no status tag, and
> §9's "What exists now (#73)" paragraph names the fields, the log lines and
> the measured numbers. **R38** is still *[proposed]*: the one rule of the three
> with no code. The **intent decision itself** is still fed by R32's obstruction
> test, so §9 Case B (two tractors, the rear one not wanting to pass) is NOT
> honoured yet - §9's open list says exactly which part that is.
>
> **Changed 2026-11-03 (issue #78, branch `fix-78-chain-row-intent`):** **R40**
> (wanting a pass is the driver's - one disposition knob, `Car.DriverEagerness`,
> the gain a driver accepts) is in the code, and it closes the gap the note
> above names: §9 Case B works out of the model, measured as the rear tractor's
> own declined-intent line in a numbered scenario. R32's target selection
> changed too - the commitment names the car I FOLLOW, while the lateral need
> and the window stay the row's. What remains: **the PRIMARY of a scenario now
> carries a disposition too** (the suite's tuple gained field 11,
> `primary_eagerness`, sent through the same `POST /teleport` as any extra, and
> the run header prints it) - what is still open is a scenario that DISCRIMINATES
> on it (a leader close enough in speed that an eager driver passes and a patient
> one does not), and R38's no-overtake zone at a crossing.
>
> **Specified 2026-11-03 (issue #80, owner's decision):** R38 is stated, not
> implemented. Over-taking **at** a crossing is forbidden; a pass must be
> **finished before reaching the crossing** with a clearance in front of it
> (`R38_CLEARANCE_M`, open). §9, "Where a pass may live at all — a crossing
> forbids one" is the text; the R-index entry and "Still not in the code" point
> at it. One consequence is a test, not only code: `fig8_row` (position 22)
> parks its row ~29 m before the crossing, which under this rule is not a legal
> pass - that staging moves, or it becomes R38's refusal test.
>
> **Landed 2026-11-03 (issue #80, branch `fix-80-r38-…`):** **R38 is in the code**
> (its entry lost the tag). The decisions as recorded: the clearance is
> `LookAheadForSpeed(my speed)` (the driver model's own look-ahead, 40 m at 50 km/h,
> 74.6 m at 80 km/h, no new constant) and a zone comes from **every junction
> mouth**, a T-junction included. Two tests carry it: `fig8_row` (position 22)
> still passes a row — legal, since the row it passes has already crawled across
> the crossing and the pass finishes PAST it — and `fig8_row_short` (position 23)
> is the refusal: nobody borrows and R38's own line names the mouth. The 300 m
> avenue staging that the split seemed to need turned out not to: with the rule
> in the code, `fig8_row`'s pass happens on the far side of the crossing, so that
> test needed no move.

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

Reproducible: run the parking scenario and watch the trace —
`scripts/run_e2e.sh --tests park_6lane_right_lane --warp 4` (verdicts + the
per-run log under `tests/`), or `python tests/test_turning.py --only
park_6lane_right_lane straight 80` against a running host, which prints the
stopping process (phase, deceleration, remaining distance, lateral offset,
heading error) and measures the kerb distance and bumper position at the end.
There is no `scripts/sim_park.py` — the Python repro tool of the retired `car/`
repo; `--only` is the door that replaced it (see [TESTING.md](TESTING.md)).

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
- `PARK_TRACK_LOOKAHEAD_M` (2.5 m) — short look-ahead for tight tracking of
  the reference line in the final pull-over stretch
- `PARK_ALIGN_GAIN = 1.0` — cross-track gain of the Stanley controller in
  the final section; `PARK_ALIGN_HDG_GAIN = 3.0` is the heading gain
  (≈0.45 s time constant at V_C) and the cross term fades to zero over the
  last `PARK_ALIGN_CROSS_FADE_START_M` 1.0 m → `PARK_ALIGN_CROSS_FADE_END_M`
  0.4 m so the car cannot counter-steer past parallel at the standstill
- `PARK_ALIGN_LATERAL_M` (0.35 m) — lateral tolerance for "on the line"
- **Two kerb clearances, deliberately different** (`Config`): a DRIVING car
  keeps `KERB_CLEARANCE_M = ROAD_EDGE_TOLERANCE_M` (0.35 m) — the same inset
  the raceline corridor uses, so a car never spawns nearer the kerb than its
  own driving line is allowed to go. A PARKED car sits closer:
  `PARK_KERB_CLEARANCE_M` (0.16 m) — "möglichst nah am rechten Rand";
  empirically the closest clean park (at ≤0.14 m the reverse-in tuck grows
  past what the style check allows and the plan falls back to a forward park
  ~0.6 m out)
- `MAX_PARK_DRIFT_SLANT_DEG` (15.0°) — cap on the slant of the pull-over
  drift. The binding constraint on how close to the kerb a car can park is
  its FRONT CORNER, which swings kerbwards while the body is slanted, so the
  drift profile (`RefLineMath.ParkEase`) does the lateral work EARLY, where
  there is still road to spare, and arrives at the kerb almost parallel
  (a symmetric smoothstep puts the steepest slant exactly halfway to the
  kerb — the worst place for the front corner)
- `PARK_LEAD_S` (2.0 s) — indicator lead time at speed (R17: signal BEFORE
  the maneuver)
- Reserves when picking how close to the kerb to park: `PARK_LINE_MARGIN_M`
  0.05 m (line discretisation) and `PARK_TRACKING_MARGIN_M` 0.10 m (the
  controller's residual tracking error)

### 1b. Reverse-in parking (back-in) — IMPLEMENTED

Code: `BicycleNav.part1` (`DecideParkStyle`, `PlanReverseTuck`),
`BicycleNav.part3` (`StartReversePark`, `UpdateReversePark`); the suite
reads it from `GET /state` as `parking.style == "reverse"`.

| Phase | Description |
|-------|-------------|
| **Style decision** | Forward vs back-in is decided ONCE per approach, from the tuck the geometry allows. Below `PARK_REVERSE_MIN_TUCK_M` (0.10 m) the maneuver is not worth it — park forwards. |
| **Stage past the spot** | The car drives PAST the flag and stops several metres beyond it — a real back-in needs the staging room. Crossing the flag while this plan is active is NOT a "drove past" failure (docs/TESTING.md §3); the run only passes once `parking.parked == true`. |
| **Back in** | Reverse gear, signed speed, feedback parking law `delta = PSI_GAIN·psi − POS_GAIN·e_off` (`PARK_REVERSE_PSI_GAIN` 2.8 ≈ 1.2 s heading damping, `PARK_REVERSE_POS_GAIN` 1.4 ≈ 2.5 s at creep), creep speed `PARK_REVERSE_CREEP_M_S` 0.8 m/s (~3 km/h). Arc steering candidates are tried sharpest-first (`REVERSE_STEER_CANDIDATE_DEGS` 38/30/25/20/16°) — full lock is the shortest back-in. `PARK_REVERSE_TAIL_M` 2.4 m straight run-out after the arcs (must be long enough for heading and cross-track to converge AT speed; `PARK_REVERSE_MIN_TAIL_M` 0.3 m is the minimum that still settles). |
| **Hard bounds** | The nose swings toward the centreline while backing in and must stay on OUR half: `PARK_CENTRELINE_MARGIN_M` 0.10 — unlike a U-turn (§5), parking may NOT use the oncoming lane. Swept body corners keep `PARK_REVERSE_KERB_MARGIN_M` 0.05 at the kerb. The staging stop may sit `PARK_PLAN_START_MARGIN_M` 0.35 m closer to the centreline than the planned lane offset (a deeper start needs a longer back-in for the same tuck, so plan from the deeper offset). |

Two pitfalls that were measured in this branch: the entry speed is NOT the
profile (the profile is a limit, not a prediction — from a standstill the
car only builds up `A_CRUISE` per metre, so planning uses
`min(profile, build-up)` iteratively over the zone), and on straight routes
the curvature solver `min_curvature_offsets` must be SKIPPED (it minimises
the second derivative of the offset profile and stretched a 35 m blend to
~180 m — a straight line is already optimal).

### 1c. Parking in a bay — the three clearances (owner decision, 2026-11-02)

Which bays exist and what a finished park is, held down BEFORE the execution
is worked on. The shuffle through a tight row (§1b's back-in between two
parked cars) is deliberately not built yet; this rule decides the bays and the
finished pose, and it is written as three tiers because the three numbers live
on three different moments. A single clearance constant is one box for all
three — that is the grinding-contact symptom §1b describes, seen from the
other side.

| Tier | Over which moments | Rule |
|------|--------------------|------|
| **Bay capacity** — choosing a bay | before the manoeuvre, per candidate bay | `free = (gap − body length) ≥ 1.00 m`. A **sum**: the split over the two ends is free, 0.50/0.50, 0.30/0.70 and 0.70/0.30 are the same bay. |
| **During the manoeuvre** — the moving car | every step | **contact only.** The body box may come to 0.05 m of a neighbour's box and may pass between them. It may not intersect them. A near miss is expected and legal — edging past a flank is what a shuffle is. |
| **At rest** — the finished park | the final pose only | each end `≥ 0.20 m` **and** `free ≥ 1.00 m`. A car resting 0.10 m off a flank is not parked, however well the bay fit. |

All three speak the two quantities the verdicts already print — the
`bay_clearances_m` rear/front pair against the flanks' real boxes — so nothing
new is measured to grade any of them.

**Why three numbers.** `1.00 m` is the manoeuvre's room: the car needs length
to swing in, and it can spend it on either side, so capacity is a sum and not
a per-end bound. `0.20 m` at rest is a contact rule: a box may not be brought
close enough to a neighbour to read as touching. `0.05 m` during the manoeuvre
is the physical pass-by; refusing it is what makes a legal shuffle look
impossible to a planner while the executor would allow it. §1b's note that
"plan and envelope must speak ONE box" (`PARK_NEIGHBOUR_CLEAR_M`) keeps the
plan and the veto agreeing with each other; it must not be extended to the
acceptance — the rest pose keeps its own `0.20 m`, and a plan and a veto that
share that number are both applying the wrong rule.

**Comparison convention.** Capacity is `free + ε ≥ 1.00` with an explicit ε,
not a bare `>=`: the graded case sits exactly on the rule (a 5.40 m bay, a
4.40 m body → free = 1.00), and a float comparison without ε turns that
accepted bay into a refusal.

**Placement.** The finished car is **centred** in the bay. Centring is stated
because the rule above does not determine a pose — any split of a legal sum is
legal — and a test that accepts "any pose inside the band" cannot tell a car
that parked nose-to-the-flank on purpose from one that drifted there. With
the rule stated as centring, `each end ≥ 0.20 m` cannot fail on bay
selection; it can only fail on a **pose** — which is what makes an off-centre
park a graded failure instead of an invisible one.

**Worked numbers** (the model this rule was designed against). Bodies: `mercedes_c220`
4.40 × 1.80, `fiat_500` 3.14 × 1.71 (`Config.MODELS`). A row `p1 … p5` with
bays built from the sedan plus slack 2.5 / 0.2 / 1.0 / 0.5 m, i.e. bays of
**6.90, 4.60, 5.40, 4.90 m**:

| bay | c220 (needs ≥ 5.40) | fiat (needs ≥ 4.14) |
|-----|---------------------|----------------------|
| 6.90 | ✓ (free 2.50) | ✓ |
| 4.60 | ✗ (free 0.20) | ✓ (free 1.46) |
| 5.40 | ✓ (free 1.00 — the rule's edge) | ✓ |
| 4.90 | ✗ (free 0.50) | ✓ (free 0.76) |

Five cars taking bays one at a time (a new car only appears once the previous
one rests), pm1/pm2/pm4/pm5 c220, pm3 the Fiat, first bay that fits:

`p1, pm1, p2, pm3, p3, pm2, p4, p5, pm4, pm5`

The Fiat takes the 4.60 m bay the sedan had to refuse — the decision follows
the car's own box, not a bay rule. With five identical sedans the last two
bays are refused and the expected row is different; a test whose expected
layout does not move with the vehicle is not testing this rule.

**Grading.** All three tiers are readable from one run, because the tape
records every pose: minimum clearance over the manoeuvre and contact count
(tier 2), final clearances (tier 3), which bays were accepted at all (tier 1).
A test states four facts and one expected row.

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
  measurement
- `REACHABLE_SPEED_TOLERANCE = 1.15` (`BicycleNav.part1`) — how far over the
  planned corner speed the car may be and still count as able to make the
  turn (15 % slack; below it the signalled turn is unreachable and the car
  slides past)
- `JUNCTION_ENTRY_SPEED_M = 8.0` (`BicycleNav.part1`) — the speed the car
  arrives at ANY real junction (degree ≥ 3) at: slow enough that a blinker
  signalled anywhere inside the approach ramp is still physically reachable
- `JUNCTION_SUPPRESS_M = 12.0` — half the span around a junction where the
  wrong-side check is suspended (no centreline exists inside an
  intersection); this is the `LaneEaseM / 2 = 12 m` easing of §7.

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

**The blinker is the decision point.** Setting it means: *"I want to turn this
way at the next decision point where it is still physically reachable"* —
reachable meaning there is enough road left to brake to a speed whose arc radius
AND tangent distance both fit (`REACHABLE_SPEED_TOLERANCE`). It does **not**
mean "turn at the junction 3 m ahead": all braking and steering after the
blinker is a function of the distance to that tangent point, never of which
segment the car happens to be on.

When the signaled turn is no longer reachable, no amount of braking makes it
reachable, and the car:

- **drives straight through** if the road goes on (the nav rebuilds the route
  across the node) and **keeps the blinker on** — the request stands, the next
  junction may still make it;
- **crashes** into the obstacle at the end of a dead end / into the arm of a
  T-junction if there is no continuation. That is not a bug and not a validation
  failure: a crash is something a real car does. A teleport, a spot-turn, or a
  5 m radius at 60 km/h are not — those are what
  [AGENTS.md](../AGENTS.md) "never do physically impossible things" forbids, and
  what `PhysicsValidator` (R1) exists to catch.

*(This was principle 2.5 of the 2026-08 turn-planning rework; the rework is
finished and its plan document is retired, so the principle lives here — with
the geometry side in [SMOOTH_GEOMETRY_DESIGN.md](SMOOTH_GEOMETRY_DESIGN.md) and
the optimisation side in [SPEC.md](SPEC.md) "The Driving Line".)*

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

- `UTURN_SINGLE_SWING_MIN_WIDTH_M` (11.0 m) — minimum road width for a
  single turn-around (§5a vs §5b threshold, verified); the doc's earlier
  name `TURN_AROUND_MIN_WIDTH` (≈ turning-circle radius ≈ 11 m) is the same
  quantity
- `UTURN_SPEED_MAX` (2.8 m/s ≈ 10 km/h) — the maneuver's own speed cap
  (the spec's 5–10 km/h), `UTURN_TAIL_LEN_M` 15.0 m straight-out stretch,
  `UTURN_STEP1_LEN_M` 14.0 m approach blend, `UTURN_HOLD_S` 0.7 s pause at
  each full stop
- `UTURN_MAX_ENTRY_SPEED_M` (5.0 m/s ≈ 18 km/h) — above this the request is
  refused ("brake first, then retry"); a three-point turn cannot be done
  faster
- `UTURN_TH2_DEG` 60° / `UTURN_TH3_DEG` 90° — heading targets at the step
  stops of the three-point turn
- `UTURN_LINE_MARGIN_M` (0.10 m) minimum corner clearance the generated LINE
  must guarantee against the pavement edge; `UTURN_BLEND_TRACKING_MARGIN_M`
  0.30 m planning allowance for the follower's tracking error
- `UTURN_STALL_ABORT_S` (8.0 s) without progress aborts the maneuver (and
  the driver flashes the hazard lights)
- Left blinker active throughout the maneuver; LaneGuard suppressed for the
  whole turn (crossing the centreline is intended)

### Implementation status

**IMPLEMENTED** (`BicycleNav.part1` constants, `part3` `GenerateUturn` /
`StartUturn` / `UpdateUturn` / `FinishUturn`), including the strategy
choice:

- **§5a single swing** on a wide road (`road width ≥ UTURN_SINGLE_SWING_MIN_WIDTH_M`
  = 11.0 m, verified), **§5b three-point** on a narrow one
  (`UTURN_TH2_DEG` 60° heading at the first stop, `UTURN_TH3_DEG` 90° total
  at the end of the reverse step, `UTURN_STEP1_LEN_M` 14.0 m approach blend,
  `UTURN_HOLD_S` 0.7 s pause at each full stop).
- The maneuver owns the car: signed-speed profile
  (`BuildUturnProfile`, the reverse zone negative BETWEEN the stop points),
  `UTURN_SPEED_MAX` 2.8 m/s (~10 km/h, the spec's 5–10 km/h), entry only
  below `UTURN_MAX_ENTRY_SPEED_M` 5.0 m/s (~18 km/h — faster, a three-point
  turn cannot be performed), `UTURN_STALL_ABORT_S` 8.0 s without progress
  aborts and the driver flashes the hazard lights.
- The generated LINE must clear the pavement by `UTURN_LINE_MARGIN_M` 0.10 m
  for every body corner; if it cannot, the request is REFUSED with a reason
  (`UturnRejectReason`) rather than executed impossibly.
- LaneGuard is suppressed for the whole maneuver (crossing the centreline is
  intended), and the driver keeps the left blinker on until it finishes
  (`Driver.cs` reading `BicycleNav.UturnActive`).

Still open: the four-point turn for extremely narrow roads (~3 m) has not
been seen on the test map, and the reverse-direction look-ahead of R21
(§8) is not implemented yet.

---

## 6. Avoidance (Obstacle Avoidance)

Applies to **BICYCLE mode** (AI driving): the car avoids **static
obstacles** on its own. In **FREE mode** avoidance is the player's business;
there the car sticks to stop-on-contact (`docs/OBSTACLES.md`).

*(Note: **car-to-car** avoidance is a separate, implemented system — speed
caps + contact resolution in `CarCollisions.cs`; see the Driving Ruleset
R2/R6/R7 plus the R8 lateral yield and the R14 crossing-entry gate. This
section is only about static obstacles.)*

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

**The two tiers of road surface** (the premise every lateral rule below shares,
agreed in the 2026-08 turn rework and restated here because that plan document
is retired): a carriageway has a **physically drivable** area — the whole
street, both lanes and the shoulder — and a **permitted** area — our own lane.
The opposing half may be used only when it is **clear of oncoming traffic** and
only as the last resort the manoeuvre genuinely needs: R8 takes the room my own
kerb side has left, R32/R36 borrow the opposing lane inside a window that is as
short as physics allows, and the lane guard (R33) counts any crossing no
commitment asked for as an error. Never the other way round: "drivable" is not
permission, and "permitted" never overrides what the car can physically do
(R1).

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
  cornering speed at `v = sqrt(A_LAT_MAX·A_LAT_PLAN_FRACTION / kappa)`
  (`BicycleNav.part2/3`), and steering rate is bounded by `A_LAT_MAX / v`.
  `A_LAT_MAX` is per vehicle class (`Config.VEHICLE_CLASS_SPECS`: car/compact
  4.5, pickup 4.0, truck 3.5, heavy_truck 3.0 m/s²). Not yet a post-filter
  that *rejects* a too-tight planned line — the plan is speed-limited, not
  refused, and the rule is still silent in the decision log.
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
  *Queue wake-up (2026-10-19):* the v2 TTC sweep is deliberately
  conservative - each car advances at `max(current, plan)` speed, because a
  car that has the right to enter must be read as if it enters. That
  conservatism, applied to a lane pair, made a follower sitting at rest with
  the throttle held read as a projectile reaching the leader "within 1 s",
  and `GradedCap`'s stageS ≤ 1 s cliff capped it at ZERO while the leader
  drove away at 22–48 km/h with a 10–20 m gap (measured: cars 43/20/16 held
  at 0 in seconds-long blocks, the queue waking up seconds late and the delay
  cascading down the arm). A pair that is PARALLEL and **opening the gap**
  (`CarCollisions.IsParallelPair` + `OpeningGap`, only while the yielder is
  already at rest) is therefore handed to R7's route-true corridor, which
  measures the real gap and the leader's real speed; rolling and oblique
  pairs keep the v2 cap (widening the hand-over to rolling pairs gridlocked
  the signed crossing - measured, crash at t=41.6 s). Residual wake delay is
  now the decision-persistence hold (`ReleaseHoldTicks`, 0.5–1.0 s).
- **R6 Staged priority response** *(crossing)* *[implemented]* — a priority
  car goes **alert** and observes instead of braking the instant a conflict
  is predicted; it brakes only if the other car fails to yield (TTC stops
  improving) for ~1 s, or it is already too late to stop. (The human-like
  application of R2 — keeps the priority car's braking stable instead of
  flickering.)
- **R7 Speed-dependent look-ahead** *[implemented]* — the avoidance
  look-ahead is at least the car's own stopping distance (reaction + braking
  + standstill gap), scaled with speed, so a fast follower stops for a
  suddenly-stopped leader. In code: `CarCollisions.LookAheadForSpeed(v)` =
  `v·ReactionTimeS` + `v²/(2·ComfortBrakingMps2)` + gap, floored at
  `LookAheadMinM` 40 m and capped at `LookAheadMaxM` 300 m (the broadphase
  ring covers the largest per-car range: `ceil(300/CellSizeM)+1 = 16` cells
  of 20 m). (Together with R2: the required brake is the max over all
  threats.)
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
  *Open (the deadlock in the screenshot of 2026-10-19):* two committed cars
  can sit **broadside in the box**, each capped at 0 by a predicted conflict
  with a THIRD car (`[R2] predicted crossing with car 78 in 4.5 s` on both)
  - a circular wait that R3's "complete the crossing" floor does not cover
  and that no brake can resolve. The rule that must apply: once a car is
  committed and its own exit is clear, a predicted conflict whose stageS
  exceeds its time to leave the box may not cap it at 0; and if two
  committed cars still block each other, the LATER arriver concedes and
  retreats (R19). Not implemented.
- **R14 Blocked-box / no entry into an occupied exit** *(crossing)*
  *[implemented]* — never enter if the crossing's exit is visibly occupied
  (e.g. a queue on the far side), **even with right-of-way**
  (do not block the intersection — the duty is StVO §1(2): *"Wer am Verkehr
  teilnimmt hat sich so zu verhalten, dass kein Anderer geschädigt, gefährdet
  oder mehr, als nach den Umständen unvermeidbar, behindert oder belästigt
  wird."*; the §9(5) citation this rule used to carry is the *Wenden /
  Rückwärtsfahren* passage and says nothing about crossing entry — corrected
  here). Independent of R5; distinct from R4 (the *temporal* gap case) — this
  is the *exit blocked by a foreign queue* case. Implemented in `CarCollisions`
  as an **entry gate only**: a car whose nose is still before the crossing line
  caps to 0 and does not commit when its own exit LANE (a body-width band right
  of the through axis — the opposite lane of the exit arm is not my exit, which
  is what froze two diagonals against each other when ignored) or the box holds
  a body that is STANDING, CRAWLING or BRAKING (`StationaryCapMps` 2.0 m/s or
  below, or `IsBraking` — the old test only saw cars already under the cap,
  which let four cars enter behind a car still braking and wedged the box).
  Scan range `R14ExitScanM` 25 m (box + a car + its braking distance); priority
  (Vorfahrt) cars are gated identically, their right of way never buying an
  entry into an occupied crossing. A blocked car does not brake to rest
  wherever the rule fired — it creeps up and stops AT the halt line (signed
  approach) or the crossing MOUTH (unsigned), the cap being the speed that
  still brings it to rest there (`ApproachCapMps`, reaction + comfort).
  **Status 2026-10-19:** both 50-car crossing scenarios (signed and
  right-before-left) survive their 60 s window with zero contact, so the
  R3/R2 interaction that used to leave a committed yield car parked in the
  box no longer reproduces.
- **R15 Deterministic tie-break** *(crossing)* *[implemented]* — when two cars
  cross the stop line / enter in the same frame (exact simultaneous
  conflict), resolve deterministically by arrival timestamp, then car UID —
  so the tree is reproducible at frame boundaries. In code the tie-break is
  uid-only (lower uid yields) in the right-before-left pair test and in the
  R26 passage claim (`PassagePriority`); no wall-clock or run randomness is
  involved anywhere (R20).
- **R27 Blocked arms are no threat (entry fixed point)** *(crossing)*
  *[partial]* — independent of R16's patience decay: a yield car must NOT
  wait for a conflict that cannot materialise. The user rule (2026-10-19):
  at an un-signed crossing the cycle N→W→S→E→N deadlocks when every arm
  waits for an arm that can never move - if S has entered and W is itself
  held (its own exit occupied, or blocked behind another car), then the car
  at N may enter: its obligation is to the traffic that can actually reach
  the conflict point while it crosses it, evaluated in TIME (R4's gap
  acceptance), not to a hypothetical occupant of the arm on its right.
  *Implemented as:* the gap-acceptance scan skips an approaching car that is
  at standstill AND is itself capped by a reason that is not about this pair
  (`ActiveCapKey` does not name me) - such an arm is no imminent threat.
  **Placement matters:** the skip applies to the APPROACHING (eta) threat
  only. A car already at/in the crossing is a real blocker whatever reason
  holds it - skipping it there let a yield car enter an occupied box
  (measured 2026-10-19: crash at t=41 s on `fig8_xing`). Because caps are
  recomputed every substep, a chain of blocked arms unwinds over substeps
  (the fixed point by iteration, not a single solve).
- **R34 Negotiated right of way** *(crossing; implemented 2026-10-21 — the
  entry itself was missing here until 2026-10-23)* — the plan chooser itself:
  **facts → proposals → awards → application**. One decision per car, one
  award per contested resource, **committed, not converged**: an award is held
  across substeps and re-awarded only past `FlipMargin = 0.4` of cost, so a
  contested stretch cannot flip owner every frame and make both drivers
  hesitate in turn; awards with no contest left are dropped
  (`Negotiation.Keep(liveKeys)`).
  The resource is `(segment, [s0, s1])` quantised to 0.5 m — **never a car
  pair** (three cars would mean three objects and three awards for one
  stretch, the trap named in §9 of the design doc). The cost that decides is
  the cost of waiting: R16's standstill growth, R12's disturbance weight
  (still 0, deliberately) and R35's role, over facts the cars already carry
  (`ApproachCapMps`-style quantities) — feasibility is judged BEFORE the
  award, so a role can never buy an impossible maneuver.
  *In the code:* `DrivingGame.Sim/Negotiation.cs`, asked by `CarCollisions`
  wherever v1 compared two cars — the R26 head-on claim and the R32 pinch
  claim. Full design, the auction's cost terms and the build log:
  [RIGHT_OF_WAY_NEGOTIATION.md](RIGHT_OF_WAY_NEGOTIATION.md).
- **R35 Emergency priority** *(crossing; implemented 2026-10-21 — the entry
  itself was missing here until 2026-10-23)* — a role with special rights wins
  the claim on a contested resource and pays ~nothing for disturbance (the
  R12 term is what it discounts). **Feasibility is unchanged**: a priority
  vehicle still never enters an unavoidable conflict — the mechanism is the
  arbiter, not a per-role behaviour branch, so Tier 0 (R1) and Tier 1 (R2) are
  untouched by it.
  *In the code:* `Car.EmergencyRole` (`Negotiation.Emergency(car)`), set from
  the `police` sprite when the car is created (`SimEngine`); `/state` exports
  it. The counterpart a conceder shows is the courtesy signal (R12):
  `Car.CourtesyForUid` / `CourtesyKind`, rendered as the warm `bitteschön` cone
  pointing at the car being let through.

### Liveness & quality (the user's rules of 2026-10-19)
These are not traffic rules - they are the **invariants the sim must never
break** and the **measurements that show whether it does**.

- **R28 Never a total lockdown** *[measured]* — the sim must never reach a
  state where a set of cars mutually wait for each other and none can move;
  real traffic does not do that, even four cars interlocked like a swastika
  get out (reverse, or in the worst case drive around off-road). The
  measurement is GEOMETRIC, not graph-based: at a real crossing node, **>= 2
  cars with their centre in the box and every car near the node standing for
  3 s of sim time** is a lockdown, and it is logged with every member's
  pose, active cap key and last decision, so the cause is read from data
  instead of guessed (`CarCollisions.LockdownEvents`, served in /state as
  `lockdowns` / `lockdown_last`). The graph above (R14/R30) only sees cars
  BEFORE the box - a committed car is not in it - so it missed the wedged
  box the user showed; the geometric detector does not.
- **R29 Desperation valve** *[implemented, must never fire — and it DID once]*
  — if a
  lockdown persists 6 s, one car is **taken out of the equation** (despawn,
  highest uid inside the box = the last arriver) so the sim never stands
  there forever. This is an ERROR: it is logged (`!! R29 DESPERATION VALVE`)
  and the e2e suite fails on it. It exists so the sim cannot hang, not as
  an excuse for the other rules.
  *Measured status:* it fired once on record — `fig8_xing_plain`,
  `tests/run_test_T_231_…log` (2026-10-22): `R29 lockdown valve: car 96
  despawned at t=15.1s (1 car(s) removed)`. It did **not** fire in the
  2026-10-23 full run (same scenario failed differently, on a contact at
  40.2 s), so it is not a daily occurrence — but "must never fire" is a
  requirement, not a prediction, and one recorded firing is a red fact.
  Current baseline: [TESTING.md → "Current baseline"](TESTING.md#current-baseline-the-one-place-a-number-is-written).
- **R30 Keep the crossing resolvable** *[implemented]* — the prevention half:
  a car may creep onto a crossing only if the chain of standing blockers
  ahead of it **bottoms out** - `resolves(X)` = X's own exit is free (base
  case) OR every car blocking X resolves itself, recursed down the
  blocked-by chain with a cycle detector. A cycle means nobody has a
  resolution, so the entry that would create it is refused: the swastika is
  never entered, and the queue case (I follow a car that WILL move off)
  stays legal - which is what the depth-1 R14 test got wrong.
- **R31 Standstill error** *[measured]* — a car that moves **less than 1 m
  within 3 min of WALL time** is probably stuck ("we might adjust these
  values later", so both are named constants: `StandstillWindowWallS`,
  `StandstillMinM`). Wall time, so the sim window scales with the warp
  factor. Per car, non-overlapping windows (`SimEngine._sinceMeters`),
  served in /state as `standstill_errors` / `standstill_last` and shown in
  the Godot HUD.
- **R33 Every centreline crossing is explained** *[implemented]* — crossing the
  centreline is **not forbidden**: an R32 pass, a U-turn (§5), a turn swing, a
  signalled merge or a pull-out all legitimately put the car on or left of it.
  What is forbidden is a crossing **no manoeuvre asked for**. So the lane guard
  is an invariant, not a detector with exceptions: `LaneGuard.LateralIntent(car)`
  names the reason from state the driver model already owns
  (`UturnActive`, `InTurnBlendZone`, `OvertakeShiftM`, `LaneChangeSignal`,
  `PullOutFrames`), and a side change with no reason is an **error** — same class
  as R29/R31, because the `Raceline` corridor is supposed to make it unreachable.
  Explained crossings stay a quality metric (`centreline_crossings`, a pass out
  and back = 2); unexplained ones count in `unexplained_crossings` per car and
  surface in /state as `lane_guard_errors` / `lane_guard_last`, log a
  `[R33]` decision on the car and print `!!`. Reverse-in parking is deliberately
  NOT an excuse: §1 makes `PARK_CENTRELINE_MARGIN_M` a hard bound, so a park
  swing may never use the oncoming lane.
  The guard now owns the whole decision — `SimEngine` no longer pre-skips turns
  and U-turns before calling it, and the guard no longer carries a second, older
  skip for R32 passes. One owner, one question.
- **R36 The oncoming lane is used for the minimum** *[new — binding user
  decision, 2026-10-21]* — borrowing the opposite lane is a **manoeuvre with a
  beginning and an end**, not a lane a car may drive in. The window is the
  shortest stretch on which the pass physically fits:
  - **in** at `obstruction − laneChangeM(v)`, the latest point from which the
    lateral shift can still be completed before reaching the obstruction. The
    length is the repo's own speed-dependent lane-change figure
    (`v · LANE_CHANGE_TIME_S`, clamped to `MERGE_LEN_MIN_M`/`MERGE_LEN_MAX_M`)
    — the same one R17 uses for a merge, not a new constant.
  - **out** at `obstruction + (own length + obstruction length)/2 + clearance`,
    the first point where the car's body has actually cleared it.
    *(extended 2026-11-02, owner decision: when my lane holds a row of slow
    vehicles, `obstruction` is the **front** of the row the plan clears, not its
    nearest car — [§9](#9-overtaking-a-slow-vehicle-a-queue-behind-an-obstruction)
    "The obstruction is a train". Today's code follows one car, chosen by largest
    lateral need rather than by position, so a pass over a row ends mid-row.)*
  Outside that window the car belongs to its own lane, and the nav must **blend
  the shift in and out over the window** rather than lay it down from wherever
  the request first fired. The lane guard counts **metres driven left of the
  centreline outside the window** as a violation — the same class of error as
  R33's unexplained crossing, because a commitment that says "I intend to pass"
  says nothing about how long the borrow lasts.
  Measured on `park_block_overtake` before the rule: the car crossed the centreline
  **48 m before** the parked car and stayed left for **230 m and counting** —
  roughly a hundred times the borrow the manoeuvre needs — while the guard called
  the run clean. The suite is green because none of its checks measure the borrow.
- **Q1 Fleet flow (quality metric)** *[measured]* — the metres every car
  drives, summed, divided by sim time. **Only meaningful for a multi-car
  run** (a single car's flow is just its own speed), and what is compared is
  **per car**: the fleet total over the number of cars involved. A high
  number means fluid traffic, a low one a stuck fleet; unlike the "cars
  moving" count it also punishes creeping at 2 km/h, and unlike a per-frame
  speed average it counts real progress (a rolled-back pair contributes only
  the metres it kept). Measured at the substep start for EVERY car on every
  path (`SimEngine._flowMeters`), served as `flow_m_total` / `flow_m_per_s`;
  the Godot HUD shows `flow/car 6.8 m/s · 410 m per car (50 cars)` and hides
  the line for single-car runs.

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
  What IS continuous is the applied **speed cap** (`CarCollisions` decel-limits
  to `ComfortBrakingMps2` / `CAR_BRAKING` as needed) and it is logged as an
  event (`avoid#<uid>` / `sign#<node>#<uid>` cap keys), not as a value.
- **R11 Gentle braking by default** *[partial]* — normally brake gently (the
  car behind may brake less well and needs reaction time); brake harder only
  when it becomes necessary. *Implemented:* the DRIVER MODEL demands the
  comfort rate (`ComfortBrakingMps2` 5.0 m/s² ≈ 0.5 g plus a 1.0 s reaction
  interval, `ApproachCapMps`) while `CAR_BRAKING` 10 m/s² stays the physical
  maximum the sim APPLIES; the brake light comes on whenever the cap is
  actively holding the car down (steady, because the cap is a smooth function
  of gap). The jerk of the brake value itself is still unbounded (R18).
- **R12 Voluntary yield (courtesy)** *(crossing)* *[implemented 2026-10-21 as
  a term — the weight is still 0]* — a priority
  car may give up its right-of-way to let a long queue on the cross street
  pass. It is a **weight in the award** (`Negotiation.CostOfWaiting`), never a
  change to feasibility. The weight is deliberately **0** until the
  disturbance measurement behind it exists —
  [RIGHT_OF_WAY_NEGOTIATION.md](RIGHT_OF_WAY_NEGOTIATION.md) §8 names it "the
  one number that must not be guessed"; see also R34.
- **R16 Anti-starvation / wait-time escalation** *(crossing)* *[implemented
  2026-10-21]* —
  independent of R12: after X s of waiting, a yield car's required gap
  **decreases stepwise** (patience decays), so a car waiting against
  sustained priority traffic does not starve indefinitely (gap-acceptance
  literature). R12 is voluntary courtesy from the priority car; R16 is the
  waiting car's own escalation.
  *In the code:* `Car.StandstillS` enters `Negotiation.CostOfWaiting`
  monotonically, so a car that keeps losing an award eventually wins it; it
  can never make an inadmissible plan admissible.
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
  This paragraph names two things it never made decidable — **where** a pass
  is forbidden, and **which car** of a queue may start one. Those are R38 and
  R37 and live in [§9](#9-overtaking-a-slow-vehicle-a-queue-behind-an-obstruction);
  R22 stays the StVO summary.
- **R8 Lateral avoidance** *(last resort)* *[partial]* — steer slightly to
  the side to get around a blocker, instead of requiring the other car to
  move first.
  **Implemented for car-to-car frontal conflicts** (user rule: two cars on a
  head-on line do not solve it by braking alone — each pulls right as far as
  the road allows, never leaving it): `CarCollisions` measures the requested
  shift into `Car.YieldRightM` (only against a threat on my left or dead
  ahead, only by the room my own kerb side has left, `FrontalClearM` 0.4 m
  body clearance asked for) and `BicycleNav.part2` applies it on top of the
  nominal lane line, clamped to `Config.KerbOffsetM(minWidth)` of the
  narrowest route segment — so the road can never be left from either side,
  and the shift unwinds slowly (`YieldDecayMps` 0.005 m/substep) rather than
  snapping back. **Not** implemented for static obstacles (§6) — there the
  corridor bound would have to take the obstacle footprint, still a proposal.
  **Exit:** return to the lane as soon as a free path exists. **Subject to
  R1/R2:** no avoidance that creates a new collision or violates the minimum
  turning radius. Still silent in the decision log (R20).
- **R19 Retreat / stop** *[proposed]* — if lateral avoidance (R8) is
  physically impossible, retreat / stop is the second last-resort path (back
  to a clear space, or hold and wait).
  **Becomes the active half of R26 (2026-10-19):** when two cars already
  hold a single-file stretch against each other, the CONCEDER of the pair
  must reverse back to the mouth it came from (a real narrow-road manoeuvre,
  signed speed + the existing reverse gear), because neither can pass and
  neither may interpenetrate. Not yet implemented: the nav needs a
  "reverse straight back along the arm" line (the parking/U-turn reverse
  lines are the only ones built today).
- **R26 Passage negotiation (single-file stretches)** *(new, 2026-10-19)*
  *[partial]* — a stretch of road that is too narrow for a pair to pass
  (`roadWidth < wA + wB + MinPassClearanceM`) is a **single-file resource**:
  two cars meeting head-on inside it cannot both use it, and braking alone
  deadlocks them (measured on `fig8_xing_plain`: a mixer and a tractor,
  both on segment 224 of a 7 m arm, 10 m apart - v1's corridor capped BOTH
  at 0 with `[R7] car N ahead (gap -6.1 m)`, the pair wedged in the crossing
  box and the fleet's flow fell to 20/50).
  The drivers negotiate a claim, deterministically:
  1. **Claim** - a car that enters such a stretch claims it; the claim is
     per car (the segment + how far it has progressed along its own travel
     direction), so a replay claims identically (R20).
  2. **Who keeps it** - the car that has progressed FURTHER into the stretch
     (entered first) holds the passage; the other is the CONCEDER. A tie
     (both entered side by side) goes to the lower uid.
  3. **Concession, escalating** - all steps stay physically legal:
     (a) *do not enter*: the conceder holds at the stretch's mouth while the
     other clears (the best case, and what prevents most wedges);
     (b) *make room laterally*: R8 pulls both to their own right as far as
     their kerb side allows;
     (c) *retreat*: if the conceder is already inside and still cannot pass,
     R19 applies - stop, reverse to the mouth, hand the passage over.
  4. **The negotiation is visible** - the decision log names the pair and
     the role (`[R26] single-file passage: car N holds it - waiting` on the
     conceder), and the conceder flashes its hazard lights, the sim
     equivalent of a driver signalling intent to the other.
  **The same rule also decides the pair that CAN pass**: an anti-parallel
  pair on one carriageway that DOES fit both bodies plus the clearance is
  never a corridor threat at all - both go, each hugging its own right kerb
  (StVO §4: pass each other on the left). Capping such a pair at 0 is what
  produced the wedge above; the corridor test must ask "can they pass?"
  before "are they touching?".
  *Implemented:* the claim, the priority test, the cap hand-over
  (`SingleFilePair` + `PassagePriority` in the v1 corridor) and the
  "they can pass - not a threat" release. *Not yet:* step (c), the scripted
  reverse retreat.
- **R32 Careful passing of an obstruction** *(maneuver; implemented
  2026-10-20/21 — the entry itself was missing from this document until
  2026-10-23)* — an obstruction (a parked car, or one crawling far below my
  speed) sits in my lane on a carriageway too narrow to pass without leaving my
  half. I may take the free lane **only if I can clear the pinch before
  oncoming traffic reaches it, and only if that oncoming car could physically
  stop in time should it need to.** Otherwise I wait behind the obstruction.
  The lateral need is `touchM + OvertakeClearM − |lat|`, bounded by the room
  inside the carriageway; **how long** the borrow lasts is R36's window, not
  this rule's. The pinch is a resource, so the claim is decided by R34 — which
  is what stops two cars from both starting the same pass.
  *In the code:* `CarCollisions` sees the blockage and offers the cap —
  `r32#<uid>` (I hold back for that car) and `r32#passive` (the other car owns
  the pinch) — and logs `[R32] lane blocked by car N ahead …` /
  `[R32] obstruction ahead - borrowing the free lane …`; the committed pass is
  part of the raceline's identity (`BicycleNav.part2.cs`), so the borrow is a
  planned line and not a lateral impulse. Scenarios #17 `park_block_overtake`
  and #19 `slow_pass_overtake`; design + measurements:
  [RIGHT_OF_WAY_NEGOTIATION.md](RIGHT_OF_WAY_NEGOTIATION.md) §5, §7d-§7i.
- **R37 Overtake order — a queue passes front-first** *(maneuver)* — when several cars sit behind one slow vehicle, **only the one
  nearest to it may begin its pass**; a car further back does not start its own
  pass while the car it follows has an overtake intent that is not finished yet.
  Binding owner decision (2026-11-02): the order is **strict** — one car passes
  at a time, front-first, never "overtaking from behind". The rule is **not**
  a geometric test ("the car ahead of me is slow"): a driver who does not want
  to pass (the tractor behind a tractor, §9 Case B) sets nothing and the car
  behind it is free. So the rule needs **per-car driver intent** — one boolean,
  `Car.OvertakeIntent`, set **once per approach** by that car's own driver model
  (as §1b's `DecideParkStyle` is decided once) and cleared when that car's own
  pass completed. A follower **reads** the flag of the single car it follows:
  the queue costs one read per car per step, no car reasons about a driver two
  places ahead, and the decision is never made twice (R20's determinism and the
  O(pairs) cost of `CarCollisions` both argue against re-deriving it).
  The intent is not only a state, it has a **visible act**: while it is set and
  the plan is still not feasible the car may come to the left edge of its **own**
  lane and look down the oncoming one — bounded, right of the centreline, lateral
  never forward (§9, "The check is a visible act"). A plan over a row of slow
  vehicles is one object: its window ends past the **front** car of the row and
  feasibility of that whole is judged before the car starts (§9).
- **R38 A pass must be finished before a no-over-take zone** *(crossing)* —
  over-taking **at** a crossing is forbidden, full stop (owner, 2026-11-03). A
  borrow may **not begin** inside a no-over-take zone and may **not continue**
  through one: R36's window (`in`, `out`) and the return must stand
  **`LookAheadForSpeed(my speed)` before the zone's near end** — the driver
  finishes the whole manoeuvre, with room left over, before reaching it (40 m at
  50 km/h, 74.6 m at 80 km/h: the distance the driver model already reads the
  road with, no new constant). Today the zones are the **junction mouths**:
  `RoadNetwork.IsJunctionMouth` (degree ≥ 3, a T-junction included) and
  `CarCollisions.NoOvertakeZoneAheadM` (the walk along the road I actually drive;
  the zone starts `Config.CENTERLINE_SOLID_BEFORE_JUNCTION_M` before the node,
  the same stretch the paint paints solid, plus "I am standing in a crossing's
  box" from the mouth behind me). Where the room cannot hold the whole cycle
  (blend out, the row, blend back), **there is no plan**: the intent may still
  be stated and the check-act may still creep, the car never commits and gets
  its turn back. R37's order and R39's owner path are not relaxed. Signs,
  road markings and `overtaking=no` may add zones to the same set — the rules
  read the set, never the reason. Full text: §9, "Where a pass may live at all".

- **R39 The lane's own driver keeps its way** *(crossing of lanes)* — a borrower may never make the owner of the other lane give up
  that lane: oncoming traffic has the right of way here (§8's premise: "drivable"
  is not permission), so `r32#passive` as a **cap at 0** is the wrong shape for
  that party — it is what keeps a car standing where nothing holds it. What the
  owner of the lane does instead, all of it inside R1/R2/R11: it may shift to
  **its own right** as far as the road allows (R8 — it may never leave the road,
  `Config.KerbOffsetM` is the bound) and it brakes **gently** for the contact
  that would otherwise occur (`ComfortBrakingMps2`, not `CAR_BRAKING`), and the
  whole point of that is to give the would-be overtaking car the time to come
  back into its own lane. Two parties, one question — the borrower's *intent* is
  the fact both read; neither decides by the other's brake.

- **R40 Wanting a pass is the driver's, not the geometry's** *(maneuver)* — a car
  sets an over-take intent only if **that driver** accepts the pass: the gain it
  could get (`EffectiveTopMps − the leader's speed`) must exceed the gain that
  driver accepts, and what a driver accepts is its disposition
  (`Car.DriverEagerness`, 0 = "Ms. Daisy" … 1 = impatient, default 0.5). "The
  car ahead of me is slow" is R32's question — *may* I pass — and it is not the
  same question as *do I want to*; §9 Case B (a tractor behind a tractor) is the
  case where the two answers differ, and a geometric test blocks the car behind
  the row wrongly there. A car already at its own ceiling has gain 0 and wants no
  pass at any eagerness: that half is physics, and only the other half — a
  driver's wanting — is staged (`driver_eagerness` on `POST /teleport`), because
  wanting cannot be read off a car. One number, one reason: see §9, "The intent
  is the driver's".

### Meta — system (not a traffic rule)
- **R20 Determinism & logging** *[partial]* — every firing rule logs its
  **R# plus the triggering condition** (the per-car decision log already
  does this); required for explainability of the tree, especially at Tier-2
  equal-rank. A logged condition must name a REAL participant: the v1
  corridor's "touching -> stop" branch used to break out without naming the
  blocker, so every standstill logged
  `"[R7] car -1 ahead (gap 0.0 m)"` - a phantom that made a 35 s hold look
  unexplainable. Fixed by naming the car before the break (2026-10-19). *Currently logging:* R1 (`PhysicsValidator`), R2/R3/R4/R5/R6/R7
  and R14 (`CarCollisions`), R17 (nav / `LaneChangeSignal`), R25 (nav / `Car`).
  Still silent: R8 (the lateral yield fires but logs nothing), R13 (planned
  but not logged) and the not-yet-implemented rules (R9, R18–R19, R21–R24).
  R15, R26 and R27 are implemented and log their trigger; R8 fires
  silently. R32 logs `[R32]`; R34/R35 and R12/R16 are not separate log lines —
  they decide WHO holds a resource, and the result is visible as the awarding
  cap key (`passage#N`, `r32#N`) on the conceding car. Deterministic tie-breaks are uid-based (no wall-clock or run
  randomness); per-car jitter (e.g. `ReleaseHoldTicks`) is a pure function of
  the uid, so a replay stays reproducible.

---

## 9. Overtaking a Slow Vehicle (a Queue Behind an Obstruction)

**Trigger:** a car is in my lane ahead of me and much slower than my own route
wants (a crawler — a tractor, a bus that cannot turn). §6 and R32 already own
*whether* I may go around it and R36 the *length* of the borrow; this chapter
owns the one thing they do not decide: **which car of a queue behind that
vehicle gets to go when**, and where no car may go at all.

**Status 2026-11-02 (binding owner decision):** the order is **strict** — the
car nearest the obstruction passes first, one at a time, and every car behind it
waits for its turn. The *chained* order (the next car starts while the one
ahead is still mid-pass — what a convoy behind a tractor does on a country road)
is **not** chosen today; the flag described below would carry it and only R37's
release condition would move.

### The three ingredients

| # | Ingredient | Who decides it | Status |
|---|-----------|----------------|--------|
| **1. Intent** | "I want to pass the car I am sitting behind" | that car's own driver model, **once per approach** — a decision of the driver, not of the geometry | new: the flag R37 reads |
| **2. Order** | whose turn it is (R37) | the queue, by reading one flag | new, strict |
| **3. Feasibility** | the pinch is clear, the line fits, the borrow minimal | R32 (the claim against oncoming traffic), R36 (the window), R1/R13 | already in the code |

An intent that is not feasible is a **wait**, never a half-started pass: the
flag says what the driver wants, R32/R36 say whether the road allows it, and
neither borrows the other's word. That order is the one §8 states for R34
("feasibility is judged BEFORE the award, so a role can never buy an
impossible maneuver"), and Tier 0 (R1) stays the post-filter over both.

### The obstruction is a train, not one car

An overtake has one **target** — the car it sits behind, the one whose flag
R37 reads. It does not have one **obstruction**. When my lane holds a row of
slow vehicles (`t1` ← `t2` ← me), a plan that passes only the near one and
returns between the two is legal, and so is one that clears the whole row in
one move. What is forbidden is choosing **during** the pass which of the two
it is. So the row is one object for the whole length of the plan:

- the lateral need is the **max** over every car of the row the plan passes
  (R32 already takes a max over what is in my line);
- R36's `out` point is measured **past the front car the plan clears** — its
  `obstruction` is the front of the row. The plan is long, and a long plan has
  to be seen before it is made;
- the obligation to keep the borrowed lane clear (R32's claim, R2's brake, R7's
  look-ahead) spans the **whole** window, not the near car's share of it;
- feasibility of the whole is judged **before** the car starts it — §8 states
  that order for R34 ("feasibility is judged BEFORE the award"), and here it is
  the whole point: if the row plus the oncoming traffic does not fit, there is
  **no attempt**, the car stays behind the train. A plan is never discovered
  too short in the middle of a borrow.

**Where the far half of that window is observed from (owner decision,
2026-11-03, an accepted cheat).** Behind a row I cannot see the oncoming lane —
the front tractor hides it, and a real driver asks the one in front instead. The
sim does the same thing: the observation point of the far half is **the front of
the row**, not my own nose. "Asking t1" is not a new sensor: every car already
scans its own surroundings in the same substep (`CarCollisions` runs
`grid.Near(..., ceil(OvertakeScanM / CellSizeM) + 1)` with `OvertakeScanM` 60 m
per car, and `LookAheadForSpeed(v)` is per car), so the question "is there
oncoming traffic, and at what distance" is read from the row's front car, and
the answer is one fact rather than one more sweep. What the cheat costs is
stated, not hidden: a driver in the second row would not have known, and the
sim lets it plan as if it had. With a row of one there is no cheat at all —
the observation point is my own position, which is today's R32.

The **visualization does not depend on the information.** Edging to the left
edge of my own lane says "I am checking", it does not carry the result, and it
looks the same whether the answer came from my own scan or from the row's front
— which is why the two are separate rules (see the paragraph below).

### The check is a visible act (the intent's picture)

A driver whose intent is set and whose plan does not fit yet does not sit flat
in its lane and wait — it comes **toward** the overtaking lane, slowly, so it
can look down the oncoming lane and see whether the plan would fit. That is the
picture of "I want to pass, but I have to look first", and it is legal only
inside these bounds:

- **right of the centreline.** Bounded by the centreline less half my width,
  with the same margin §4 asks of its corridor (`Config.LANE_CENTRE_MARGIN_M`
  0.5 m). Crossing the line **is** the commitment, and a commitment belongs to
  a plan that was judged feasible.
- **clean by construction:** R36 counts **metres driven left of the centreline**
  outside an open window, so a shift that stays right of it is not a borrow it
  could be charged for; R33 asks for a reason on a **side** change and this is
  not one. The creep needs no exception and creates no error.
- **lateral, never forward.** R9 forbids creeping forward ambiguously — forward
  creep toward a train reads "maybe I will pass, maybe not", exactly what R9
  says other road users must be able to tell apart. Sitting at the left edge of
  its lane and asking the question is legible; easing off the brake is not.
- **it may not test the lane by entering it.** The check must not spend the
  resource it is checking for — that is R36's rule ("a manoeuvre with a
  beginning and an end") read from the other side, and the reason the
  preparation and the borrow are different acts.
- **an aborted check is silent again.** The signal belongs to the intent, and an
  intent that was given up is not an intent: the blinker goes off in the same
  step the plan is dropped, and the car drives back. A left blinker that stays
  on while the car comes back into its lane is a lie to the traffic behind.
- **the check itself is arithmetic, the creep is what makes it legible.** The sim
  has no sight model — a grep for visibility/occlusion in `DrivingGame.Sim/`
  returns the renderer and its draw order, not a decision. What "looking down
  the oncoming lane" means today is R7's stopping distance (`LookAheadForSpeed`)
  and R32's claim over the pinch. So the creep is the *picture* of the intent,
  the test stays the arithmetic that R32/R36/R2 already run.

### An aborted pass is not a stop — it is a way back

The measured wrong today (`tests/tape_T_435_21_20261103_000407.txt`,
`slow_pass_priority`, 966 frames): steps 364–702 — **5.6 s of sim** — uid 1 sits
at `x=98.250`, exactly its nominal lane line, `bl=0` (no signal at all),
`brake=1`, held at 2.5–8 m/s by the cap keys `r32#passive` and `avoid#2`; the
left blinker appears only at step 711, with the commit. There is no check phase
and no way back — when R32 drops a pass, `CarCollisions` clears the commitment,
zeroes `OvertakeShiftM` and offers a **cap of 0**, so a car that was already at
the edge of the centreline stands there.

What must happen instead, in one order:

| Moment | Signal | Motion |
|--------|--------|--------|
| intent set, plan not yet feasible | **left blinker on** (R17, wired for a pass for the first time) | a **lateral** approach to the left edge of my own lane, at the check's own slow speed |
| the plan is judged feasible | stays on | the pass: R36's window opens, the shift blends in |
| the plan is judged **not** feasible (the pass is aborted) | **off, same step** | a **driven** return to the nominal line over the same speed-dependent blend (`v · LANE_CHANGE_TIME_S`) |

The return is a lane change, not a stop: nothing in the rule set asks a car to
hold station because a plan did not start. A cap of 0 belongs to a car that must
not move (R2's brake, R14's halt line, R3's box); an aborted plan is not one of
those. The only thing allowed to hold the return down is R2/R7's braking — a car
that gives up its pass drives back at normal lane-change speed and the next car
of the queue can use its turn.

### Where a pass may live at all — a crossing forbids one (owner's decision, 2026-11-03)

**Over-taking at a crossing is forbidden, full stop.** Owner's words: "überholen
auf einer Kreuzung ist grundsätzlich verboten. Der überholende muss sicherstellen,
dass er vor Erreichen der Kreuzung den Überholvorgang abgeschlossen hat, und zwar
in einem ausreichenden Abstand vor" — a pass is not only banned *in* the box,
it must be **finished before the crossing, with room left over**.

A borrow is a place-bound manoeuvre, and the place is decided **before it starts**
(the same place R36's window is judged): there is a stretch of road between here
and the crossing, and that stretch is the whole plan's room.

| Fact | The rule | Comes from |
|------|---------|------------|
| the zone | a **stretch where passing is forbidden** (today: every junction mouth — a T-junction included — plus the solid approach the paint already draws, `Config.CENTERLINE_SOLID_BEFORE_JUNCTION_M`) | geometry, as today; signs and markings may add zones later |
| a pass may **not begin** | while any of the car is inside such a zone | hard, every member of a queue |
| a pass may **not continue** | through it: R36's `out` point and the return must stand **`LookAheadForSpeed(my speed)` before the zone** (40 m at 50 km/h, 74.6 m at 80 km/h) | R36's window, place-checked |
| the plan is one object | the room is `(crossing − clearance) − here`; if the full cycle — blend out, pass, blend back — does not fit in it, **there is no plan** | R36's minimum commitment + §9's blend |
| inside the box | the centreline does not exist (`CENTERLINE_JUNCTION_GAP_M`), so "the other lane" is undefined there and no window can be built across | the geometry already says no |
| no relaxation | R37's order (front-first, and the ban applies to every member), R39's owner path, R36's minimum commitment | unchanged rules |
| what the eye sees | a car whose plan does not fit does not commit: the check-act may creep to the lane edge (blinker on), the car never enters the borrowed lane | §9's check act, R17 |

`R38_CLEARANCE_M` is not a second measurement of the same thing as the paint's
6 m: **a driver at speed must finish earlier than one at walking pace.** Decided
2026-11-03:

| Decision | What was chosen | Consequence |
|----------|-----------------|-------------|
| the zone | **every junction mouth**, a T-junction included | the same mouths the paint goes solid at (`StyleCentrelinePiece`), not only degree-4 crossings |
| the clearance | **speed-derived**: the margin is `LookAheadForSpeed(my speed)` | the distance the driver model already uses to read the road; at 50 km/h it is the 40 m floor, at 80 km/h 74.6 m — no new constant |
| what the zone is | a **fact of the road**, not a rule's own geography | a rule must not know "junction": it must know "a stretch where passing is forbidden" |

The last line is the point of the rule's shape. Today the only way to find such
a stretch is the junction geometry. Later, more sources join the SAME set:

- **a sign** — a no-over-taking sign at the approach (`no_overtaking`, or a
  signed `overtaking=no` section);
- **road markings** — a solid or hatched centreline stretch that is not next to a
  junction, a bus lane's hatched tongue, a forbidden stretch painted on the road;
- **the map** — `overtaking=no` on the way itself, which the importer does not
  read today.

So R38 is written against a set of `NoOvertakeZone`s (each one: a stretch along
a carriageway, its reason, and its source) and every source is allowed to add to
that set; the rule never asks *why* a zone exists, only whether the whole
manoeuvre fits before it. Adding a signed zone must not mean editing R38 again.

**Why this changes a test, not only the code.** The plan's room is
`blend_out(v) + the row's extent + blend_back(v)` and it must **end**
`LookAheadForSpeed(v)` before the zone — at 50 km/h that is ~35 m + row + ~35 m
ending 40 m before the mouth, so an obstruction whose front stands closer than
~110 m to the crossing cannot be passed at that speed at all: you may not pass a
car that is standing inside the margin. Measured on this branch, both halves:

- `fig8_row` (position 22) keeps its row ~29 m before `xing_c` — but the row
  CRAWLS across the crossing at 10 km/h, so by the time the passing car is close
  enough to act, the row is past it and the pass finishes on the far side: legal,
  ✅ green. The 300 m avenue this seemed to need turned out not to be needed —
  that was the guess the rule's arithmetic made before it ran.
- `fig8_row_short` (position 23) is the refusal test: the same row, the end flag
  **before** the crossing, and the graded expectation is that nobody borrows
  while R38 names the mouth. Verdict of the run, in one tape:

```
[R37] intent: I want to pass car 3 ahead                    (the driver wants it)
[R38] a pass here would end at the junction mouth 41.9 m ahead - the plan needs
      87.4 m: not a pass                                   (the place says no)
[R32] pass taken: nobody                        (nothing entered the other lane)
✅ the flag reached, behind the row
```

### Why the intent has to be a flag

The owner's rule (2026-11-02) — *a driver anticipates that the car in front of it
probably wants to pass too, and does not start its own pass then* — is not a
prediction
in the sim. It is a fact the leader already owns, read by the follower. Three
consequences:

- **one decision per car, not per pair.** Ten cars behind a tractor ask their
  own intent once and each reads one boolean. Without the flag, the same question
  is re-derived for every pair of the queue, which is what R20 (determinism,
  one decision per car) and the cost model of `CarCollisions` argue against.
- **the chain stops at a driver that does not want to pass.** The flag is
  *intent*, so a car whose leader has none is free — and a geometric test
  ("the car ahead of me is slow") would block it wrongly. Case B below is the
  proof that the two questions differ.
- **the flag never means "I am passing".** That already exists and must not be
  duplicated. Three objects, three moments:
  `Car.OvertakeIntent` (before — the driver wants it) → `Car.PassUid` with
  `PassSegIdx`, `PassProgress`, `PassShiftM`, `PassLengthM`, `PassX`, `PassY`
  (during — the commitment) → `Car.OvertakeShiftM` (the metres). R37 reads the
  first, R32 the other two; no consumer may substitute one for the next, the
  same sentence §1c writes about its three clearances.

### The intent is the driver's — one number, two personalities (2026-11-03)

Decided the same day it was asked: **a car states an intent only if that driver
actually wants to pass**, and the personalities exist — an impatient driver and
a patient one ("Ms. Daisy"). A model, and one number: `Car.DriverEagerness`
(0 = Ms. Daisy … 1 = impatient, default 0.5 = an ordinary driver), because
exactly one thing differs between the two personalities: **what gain a driver
accepts before it pays for a pass.** Everything else about a pass — the route,
the signal, the visible check, the return, R37's order gate — is the same for
every driver, so the names are words over one knob. (R16's patience for a merge
is the same KIND of disposition and stays a separate rule: a merge answers
"may I enter", a pass answers "is a lane change worth the gain".)

The model's one question, as the code states it (`CarCollisions`) - and what is
NOT in it matters: the geometry. The driver's wish is asked once per object and
must stay independent of "is there room now", because room changes while the
object does not, and an early "no" would never be asked again (measured:
`park_block_overtake` timed out with the room folded in).

```
gainMps = c.EffectiveTopMps - whatSpeed            # what a pass could give me
wantMps = DriverModelMaxGainMps * (1 - eagerness)    # what this driver accepts
intent  = gainMps > wantMps                         # the wish; feasibility is R32's
```

`DriverModelMaxGainMps = 8.0` m/s (29 km/h) is the most a driver may ask for,
so an eager driver passes at any gain and Ms. Daisy only pays when the vehicle
ahead runs ~30 km/h below what she drives. Two things come out right without
being told: a car at its own ceiling — a tractor staged with `cruise_kmh: 10`,
any crawler — has gain 0 and wants no pass **at any eagerness**: that half is
physics, not disposition, and it is why §9 Case B works out of the model rather
than out of a staging. The other half, "this driver wants the pass", is staged
through `POST /teleport`'s `driver_eagerness` — a scenario needs it for its
Cast anyway, because wanting cannot be read off a car. The standing default is
eagerness **1.0**: fleet-wide, drivers want to pass ("they should want to pass
either way"); patience is staged, never assumed. A **parked** car is not asked
at all - it is off duty and holds no turn (measured: letting one want a pass
blocked the car behind it forever and `park_block_overtake` timed out).
A scenario stages the PRIMARY the same way: the suite's scenario tuple carries
`primary_eagerness` at field 11 and the run header prints what it staged
(`# primary driver: pushy (driver_eagerness=1)`), because a run that does not
name its drivers cannot be read as a driver experiment.

The word in the log carries the reason, so a tape says what the model asked:

```
[R37] intent: no pass wanted from car 14 ahead: a gain of 0.0 m/s is not worth a pass to this driver (it asks for 4.0)
```

### Lifecycle of the flag

| Moment | Flag | Effect on the car behind |
|--------|------|------------------------|
| the driver decides to pass its leader | set | that car may **not** start its own pass |
| waiting for the pinch — oncoming traffic owns it, R32's `r32#N` cap | still set | the follower stays blocked: a car that has not started still holds its turn |
| the pass runs (R36's window open) | set | blocked |
| the pass completed — back in own lane at R36's `out` point | **cleared** | the follower may start |

The flag is cleared at exactly the moment the commitment already dies (`PassUid`
→ -1), so the rule adds no timer and no distance of its own: R36's window
ending **is** the pass ending.

### Worked cases

**A. A tractor and ten cars.** `t` at 10 km/h, `c1 … c10` behind it, all
wanting 80 km/h. Every car sets its intent — each one's leader is slow, and
that judgement belongs to it alone. Only **c1** may start: c2's leader is c1,
intent set; c3's leader is c2, same, down the chain. Release: c2 starts when
**c1's** pass completed, c3 when c2's did. The expected record is one sentence:
*the order of the `[R37]` pass-start events equals the queue order, front-first.*
No car "chooses" another one and no decision depends on a reason two cars ahead.

**B. Two tractors and one car.** `t1` at 10 km/h ← `t2` at 12 km/h ← `c3`.
`t2`'s driver does **not** want to pass `t1` (2 km/h more is not a pass, and
that is R22's "at clearly higher speed"). `t2` sets nothing → `c3`'s leader has
no intent → **`c3` may pass `t2`**, under R32/R36 unchanged. The pair in front
stays in its lane and at its speed.

**C. Same shape, intent present.** `t1` ← `t2` at 45 km/h (its driver wants to
pass) ← `c3`. `t2` sets the flag, so `c3` waits until `t2` finished its pass of
`t1`. `c3`'s own plan **may** cover both tractors — one row, one move — and
then its window is measured past `t1`, the front of the row, and the obligation
to keep the borrowed lane clear covers that whole length, judged **before** `c3`
starts (and if it does not fit, `c3` does not start). It may equally plan to
return between the two. What it must not do is start without a plan and find
out mid-borrow which of the two it is getting past — that is the difference the
two paragraphs above make decidable, not a note on style.

### What a test states

Five facts and one expected record, all readable from one run (the tape holds
every pose and every decision line, as §1c's grading paragraph says):

1. the **order** of `[R37]` pass-start events equals the queue order (case A);
2. no car starts while its leader's intent is unfinished — measured as metres
   driven left of the centreline outside an open R36 window, R36's own metric,
   no new measurement invented;
3. `t2` never leaves its lane and never changes speed (case B);
4. `c3` passes `t2` (case B);
5. a pass over the row returns **past its front car** (case C) — read with R36's
   own metres-left-of-centreline, where a return crossing level with a tractor
   is the mid-row bug this rule forbids. Still no new measurement.

A test whose expected record does not change between A and B is not testing
this rule — the same sentence §1c writes for its bay row.

### What exists now (#80, 2026-11-03)

Branch `fix-80-r38-pass-finishes-before-a-no-overtake-zone`, `RoadNetwork.cs`,
`CarCollisions.cs`, `TestMaps.cs`, `tests/test_turning.py`:

- **the zone set has one source today**: `RoadNetwork.IsJunctionMouth(nodeId)`
  (degree ≥ 3, so a T-junction counts as the owner asked) and
  `CarCollisions.NoOvertakeZoneAheadM(net, c, seg)` — the metres to the near end
  of the next zone along the road I drive. The walk runs on past nodes that are
  NOT mouths (degree ≤ 2): the test map's figure-8 turns at such nodes every ~40 m,
  and a straight-ahead test gave up there and answered "no zone", which is what
  had let a row 29 m before a crossing pay for its pass across the crossing.
- **inside a zone already** is answered from the mouth BEHIND me (the box =
  `JUNCTION_WIDENING_M + JUNCTION_CORNER_RADIUS_M`), because "a pass may not
  begin inside" is exactly the case of a car that has just come through.
- **the place gate** sits at R32's commitment (`placeOk32`): the plan
  `past-the-row + half the row and my own length + blend back (v · LANE_CHANGE_TIME_S)`
  must end `LookAheadForSpeed(v)` before the zone. The driver's wish (R37/R40)
  stays where it is - the place never joins the intent question, exactly as §9's
  "asked once per object" note demanded, and it is a separate reason: the log
  line `[R38] a pass here would end at the junction mouth … the plan needs …: not
  a pass`, the cap reason `r38:mouth`, and no cap of 0 - the car keeps its
  following behaviour and its turn.
- **the tests**: `fig8_row` (position 22) is green unchanged, the pass it makes is
  past the crossing; `fig8_row_short` (position 23) is green as the refusal —
  nothing borrows and R38's line is present. The suite went from 31 to 32 tests,
  unit tests 168/0, the branch's red set equals `main`'s (see the PR).
- **not done**: signs, road markings and `overtaking=no` are NOT in the importer,
  so the set has one source; and the walk only follows the road straight on
  (bounded at 300 m / 8 hops), which is what a pass needs but not a route guess.

### What exists now (#78, 2026-11-03)

Branch `fix-78-chain-row-intent`, `DrivingGame.Sim/CarCollisions.cs` + `Car.cs`
+ `TestMaps.cs` + `tests/test_turning.py`:

- **R40 exists**, with the code above: `Car.DriverEagerness` (standing default
  `1.0`), `MinAcceptableGainMps`, `DriverModelMaxGainMps = 8.0`, staged through
  `driver_eagerness`; a PARKED car is not asked.
- **R32's commitment now requires the intent.** `wantM > 0 && c.OvertakeIntent &&
  !blocked && myTurn` — a driver that declined must not commit. Measured before
  the link existed: "Ms. Daisy" logged `no pass wanted` and still held `PassUid`
  of the row's front, which made the car behind read a guest in its lane and
  take no turn at all. The intent is also **asked about the thing I pass**
  (the queue leader if there is one, else R32's target), because a parked car at
  the kerb is outside the queue band and is still what a pass is about.
- **the row is a numbered scenario now** (`fig8_row`, position 22): a new map
  start `b.Start("fig8_row", "xing_n31", facing: "xing_n32")` on the self-crossing
  tile, 190 m of the priority diagonal before a real degree-4 crossing, the row's
  front ~29 m before it, the rear tractor staged as Ms. Daisy, the flag past the
  crossing. One run records both halves of the rule:

```
row_rear_tractor: [R37] intent: no pass wanted from car 2 ahead:
                  a gain of 0.2 m/s is not worth a pass to this driver (it asks for 8.0)
u1 (row_passing_car): [R37] intent: I want to pass car 3 ahead
u1: [R32] obstruction ahead - borrowing the free lane (2.67 m left)
✅ Reached the end flag - the whole row passed, next to a real crossing
```

- **no regression**: the branch's red set is the same six scenarios as `main`
  (three parking, the negative hairpin, the two fig8 crossings); unit tests
  168/0, `dotnet test` green.

### What exists now (#73, 2026-11-03)

Branch `fix-73-over-taking-order`, `DrivingGame.Sim/Car.cs` + `CarCollisions.cs`:

- **the intent flag exists.** `Car.OvertakeIntent`, `Car.IntentWithUid` (the
  leader that decision was made for, so the decision runs once per approach,
  not per tick) and `Car.OvertakeIntentLog` (a *declined* pass is a record,
  not silence). The log lines are `[R37] intent: I want to pass car N ahead` /
  `[R37] intent: no pass wanted from car N ahead`, and the order gate answers
  `[R37] car N ahead has the turn - waiting for it, not for a gap`.
- **the queue leader is the nearest same-direction car ahead in this
  carriageway, whatever it is doing.** The band for "who is my leader" is the
  carriageway half-width, not my own half: a leader that is mid-borrow is still
  the car I sit behind — §9's "the obstruction is a train" applies to my leader
  as well as to my target. The band for what I actually *pass* is unchanged.
- **the window is measured past the front of the row.** `frontAlongM`,
  `frontX32/frontY32`, `frontLen32` and the negotiation resource reach past the
  front car, so the pinch is asked for the whole row and the far half of the
  window is observed from there — the accepted observation cheat of §9, at the
  cost §9 states for it.
- **the check is visible.** While an intent is held and the plan is not
  feasible the car creeps toward the left edge of its **own** lane (the request
  is capped by the room in my own lane: `LANE_CENTRE_MARGIN_M` + half width off
  the centreline) and the left blinker is on before any commitment. Measured on
  scenario 21: `[R17] signal left before lane change` at step 125 while the car
  still sits at x = 98.250 (its nominal line); `[R32] obstruction ahead -
  borrowing` at step 366 — 4.0 s of signalled check, where the old code had
  `bl=0` through all 5.6 s of a standstill.
- **an aborted pass is driven, not stood.** The blocked branch no longer caps
  the car at 0. `r32#passive` survives only as the kerb clamp of "R32 creep"
  (`CarCollisions` v1: `if (vPass < 1.0) vPass = 1.0;  // a creep, never a hold`).
  Measured on the queue check below: the longest standstill any queue member
  had was 0.2 s, and the only cap near 0 is `avoid#N` — R7 holding a car behind
  the crawler, which is a crawl, not a wait.
- **R39's owner path exists.** `[R39] car N is passing in my lane - I own it:
  room on the right, the following rule's brake`: a guest in my lane is not an
  obstruction to me, my duty is room on my own side plus the ordinary following
  brake — no cap of 0 is put on the owner of a lane because a car is passing.

Measured on `tests/overtake_order_check.py` — a tractor at 10 km/h and four
cars at 80 km/h behind it at 18 m gaps on `straight_m→straight_s`, 45 s of
sim, the script grades the four facts §9 states for case A:

    F1 every queue car states a decision:     ok  4/4
    F2 pass starts in queue order:            ok  3 of 4 started, order 2 -> 3 -> 5
    F3 never two cars in the other lane:      ok  worst 1, deepest 1.41 m past the centreline
    F4 no queue car stands longer than 2 s:   ok  worst 0.2 s

The script is a QA check in the shape of `tests/car_line_check.py` (it drives
the live host over the REST API and grades from `/car/{uid}/decisions`), not a
numbered scenario: the map's 150 m stretch does not hold ten cars, and four at
18 m is what fits.

### Still not in the code

- ~~**R38 — nothing forbids a pass at a crossing.**~~ **closed the same day
  (#80):** the place gate is in the code, with `LookAheadForSpeed` as the margin
  and every junction mouth as a zone (see "What exists now (#80)"). What is
  still not there: the other sources of a zone - a no-over-taking **sign**,
  a **painted** stretch, `overtaking=no` from the map - none of which the
  importer reads, so the set has exactly one source today.
- ~~the intent *decision* is R32's obstruction test, not a driver preference~~ —
  **closed the same day (R40, #78):** the decision is `gain > what the driver
  accepts` (feasibility stays R32's at the commitment), `Car.DriverEagerness`
  carries the disposition, and the measured row of two tractors logs the rear
  one declining (`[R37] intent: no pass wanted … a gain of 0.2 m/s is not
  worth a pass`).
  What is still not in the code: nothing about the disposition itself -
  **the PRIMARY now carries one too** (the suite's scenario tuple has field
  11 `primary_eagerness`, which `create_car_at_start_point` sends through the
  same `POST /teleport`; a run prints `# primary driver: Ms. Daisy |
  pushy (driver_eagerness=…)`). What is still missing is a scenario that
  DISCRIMINATES on it: `fig8_row` stages its primary as pushy and proves the
  value travels, but the row's front is 19 m/s slower than the primary's own
  ceiling, so even Ms. Daisy would pass it. A discriminating cast needs a
  leader within ~8 m/s - that is a scenario of its own, and R38 (no pass at
  a crossing) is #80.
- **no queue scenario in the numbered suite** — there is now a **row-of-two
  scenario** (a second entry on `slow_pass_priority`'s named start, one tractor
  more in the row), graded on the row's own decision facts and printing who
  declined and who passed. A full ten-car queue still has no numbered scenario:
  the map's 150 m stretch does not hold one, which is what the QA script above
  is for.
