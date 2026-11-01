# Resting bodies as terrain (occupancy design)

> **STATUS: NOT IMPLEMENTED.** This is a design agreed with the owner on 2026-11-01,
> written down *before* code, deliberately. Today a resting car is a full participant in
> the simulation loop and pays for it — measured: `50 moving = pace 0.894` (real time),
> `1000 parked alone = pace 0.016`, i.e. 62 s of wall per sim-second. The work item is
> issue **58**; the tooling that measures it (`--bench-parked`) is already on `main`.
> TODO: implement §4–§6, gated by §7. Nothing below describes current behaviour except
> where a section says "today".

## 1. The owner's framing

A car that rests, a concrete bollard and a flower pot are the same thing to the simulation:
**an area a vehicle may not enter and may not touch.** They should cost nothing, because
nobody has to ask them anything. A car that is *standing in traffic* is not one of them —
that distinction is the whole design (§3), and getting it wrong is not a performance bug
but a map that deletes itself.

## 2. Two questions, two structures

| question | structure | cost today | cost with this design |
|---|---|---|---|
| **"Is there road here?"** | sparse **occupancy index**, precomputed from the map + resting bodies | a full grid rebuild per car set, twice per substep (`CarCollisions.cs:530`, `:2582`), so O(all bodies) × 2 × 60/s | one insert per body **for its whole lifetime**; reads are cell probes on a corridor |
| **"Is someone moving toward me?"** | **traffic index**, holds only things that move | same grid, same rebuild | O(traffic), which is the number that is allowed to scale |

Consequences of the split:

- **Occupancy changes cost, never topology.** Parking somewhere must not trigger re-routing:
  a cell flips between free/occupied, the road graph is untouched. Otherwise a car pulling
  over downtown causes a re-route storm — the opposite of free.
- **A resting body is *derived*, not authoritative.** One `Car`, one pose, one identity. The
  cell entry is generated *from* the car and destroyed when it wakes. Issue **27** ("a parked
  car is a car: retire the Obstacle entity") is honoured by this rule and violated by any
  variant where the terrain owns a position of its own. See §5.
- **Resting bodies keep a name.** Terrain entries carry `ownerUid` (null for a bollard),
  because `/state.cars`, the tape's `names=` legend, window 0, the minimap labels and the
  bay acceptance box (`park_target.behind_uid` / `ahead_uid`, issue 31) all must still be
  able to say *which* car sits there. Displaying them is not the same as simulating them.

## 3. Rest is a state, never a speed

This table is the safety-critical part. `speed == 0` must never appear on the left column.

| situation | authoritative signals | index | what a following car does |
|---|---|---|---|
| resting in a bay / kerb row | `nav.Parked` **and** `!HasDestination()` | occupancy, **hard** cell (that area is not road) | parks beside it; never routes over it |
| stopped in a queue / at a light | state `Driving`, **has** a route, `heldBy` set | traffic | brakes to a gap and **waits** — the car ahead will move |
| deliberately standing in a lane (stalled, obstruction) | `nav.Parked` **and** inside the carriageway | occupancy, **soft** cell (road, occupied now) | waits for an overtaking gap, or gives up visibly (`park_block_overtake`, `slow_pass_priority` are exactly this) |
| crossing pedestrian | own mover class, slow tick | traffic (slow lane of it) | yields per priority rules |

Why the middle row must not be terrain: a jam would then read as "no road here", routes
would dissolve, cars would try to drive around the queue they belong to, and the collision
code would reach a lockdown. Today one check already encodes the rule correctly
(`CarCollisions.cs:1040`: `o.Speed <= 0.5 && o.BicycleNav?.Parked != true`) — the danger is a
new fast path that classifies by speed because it is convenient.

`heldBy` (already in the collision code *and* in the tape format) **is** the queue link;
`HasDestination()` is the "intends to go on" flag. No new state is needed.

## 4. The hard/soft flag, and why both are needed

The owner's answer on 2026-11-01: a car parked in a driving lane must stay possible (a
traffic jam, a breakdown). So:

- **hard** — never drivable: kerb, planter, bollard, sidewalk, a resting car in a bay. Nobody
  may plan into it. A parking job may still *name* one as its destination (§6, risk 2).
- **soft** — drivable in principle, occupied right now: a car stopped in a lane. It stays
  **in the route graph**, so the "wait / squeeze past / give up" behaviour survives. Making
  everything hard silently deletes behaviour the e2e suite guards.

Both are one cell each, so the performance property does not depend on the flag.

## 5. Invariants

1. **One Car, one pose.** The occupancy cell is generated from the car at park time and
   removed at wake time. No second position anywhere (issue 27).
2. **Resting bodies appear in no per-substep loop.** Not the driver's `GetControl`, not the
   fleet-flow / standstill bookkeeping, not `PhysicsValidator`, not the lane guard, not the
   off-road check, not the avoidance caps, not the tape's per-car record. Today `Step()`
   contains **10** `foreach (var c in _cars)` passes; each must gain the inert skip, and the
   tape needs "unchanged since last substep" instead of re-writing identical bytes 60×/s.
3. **Resting bodies appear in every read.** `/state`, the tape, the client, the acceptance
   box. "Inert" is a physics statement, not a visibility one.
4. **`LockdownEvents` stays the tripwire.** A stale cell (a body removed but still in the
   index) blocks from the dead; the lockdown counter is an error path the suite already fails
   on. Tests: park → wake → despawn → re-park, each asserting the cell count.

## 6. Known risks to design for, not discover

1. **A woken car must not blind-start.** Occupancy removal + traffic insert, then *force* its
   corridor refresh before its first frame of driving, or it drives one frame with no idea
   who is around it.
2. **"No road there" vs "my bay is there".** A parking destination lives inside an area that
   is otherwise hard terrain. The nav must be able to reach a destination *it owns* and must
   not conclude "unreachable" and surrender. Needs a test.
3. **Route reachability is unchanged by occupancy** (§2), so a lane-blocking car never makes
   a destination unreachable — it makes it *slow*. If a future map needs a real detour, that
   is a different feature and must not be smuggled in here.
4. **Pedestrians/cyclists** are movers, not terrain: they belong to the traffic index with a
   slower tick, and they make cells soft, never hard.

## 7. Acceptance criteria (both are measurements, not opinions)

- **Flat in N.** `--bench 50 --bench-parked 0,500,2000` → pace within a few percent across
  the parked counts, and `1000 parked` alone at **≥ 1.0×** (today 0.016×).
- **World-size independence.** 50 moving cars cost the same on a full map and in a four-
  segment box: the corridor scan reads a constant number of cells regardless of how much is
  parked.
- **No behaviour loss.** The e2e bay verdicts, `park_block_*`, `slow_pass_*`, unit suite
  (165/0/2) and the shimmy canary stay green; `lockdowns` stays 0.

## 8. Shape of the query, decided in the same conversation

- Candidates are **geometric**, relevance is **directional**: cache one candidate set per car
  (versioned by the cell it lives in), then classify by projection — ahead / oncoming /
  conflict / mirror — at the moment of decision. Baking direction into the *stored* list
  invalidates it whenever anyone overtakes anybody.
- Corridor, not circle: cells along my path for a **braking horizon capped at a fixed
  maximum** (agreed 2026-11-01), plus the conflict cells of the junction ahead.
- **Mirror view is lazy**: computed only when a car changes something (lane change, overtake,
  reversing, U-turn), not every substep for everyone.
- Cells are **coarse** (order 20 m) and **split by kind at insert** (traffic / resting /
  scenery), so a query picks a bucket instead of filtering a mixed list.
