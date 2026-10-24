# Right-of-way negotiation — design plan

> **Status 2026-10-23 (code-verified): implemented.** Proposed 2026-10-20
> (see `docs/reviews/2026-10-20-component-review.md` §4/§5 for the measured
> baseline this rests on), built 2026-10-21. The subsystem is in the tree:
> `DrivingGame.Sim/Negotiation.cs` (the resource object, the award,
> `FlipMargin`, `CostOfWaiting`, `Emergency`), `Car.EmergencyRole`,
> `Car.CourtesyForUid`/`CourtesyKind` exported in `/state` and rendered as the
> `bitteschön` cone. **§7b–§7i are the build log** — they are dated entries in
> the order they happened, appended after §10, so they are read out of order on
> purpose; a numbered § is design prose, a §7<letter> is "what the measurement
> said". Baseline numbers quoted there are the suite state **at that date**
> (scenario numbering changed since — see
> [TESTING.md §2a](TESTING.md#2a-the-scenario-list-generated-do-not-retype)).
> §5's rule set is documented where the rules live:
> [DRIVING_MANEUVERS.md](DRIVING_MANEUVERS.md) §8 has **R32, R34 and R35**
> entries since 2026-10-23 (step 1 of §7 was finally executed then).
>
> What it supersedes / connects:
> - **R32** (obstruction pass) — implemented in `CarCollisions.cs`; the §8
>   entry this document promised was written 2026-10-23.
> - **R33** (every centreline crossing is explained) — **implemented** 2026-10-20
>   in `LaneGuard.cs`. This document states how the corridor and the negotiation
>   feed it, instead of the three scattered suppressions it replaced.
> - **R26** (passage negotiation, single-file stretches) — the same object,
>   scoped to one case. Generalised here.
> - **R30** (keep the crossing resolvable) — the same graph test, scoped to
>   crossings. Generalised here.
> - **R12** (voluntary yield / courtesy) and **R16** (anti-starvation) — both
>   `[proposed]`, no mechanism. Given one here.

---

## 1. Why the current model cannot do it

The `Raceline` corridor makes rule 2 a **hard bound**: on a two-way road the
left bound is `centreLimit = CAR_WIDTH_M/2 + LANE_CENTRE_MARGIN_M`
(`Raceline.LegalCorridor`), relaxed only near junction nodes (`fNear`). A legal
line is guaranteed by construction.

Careful passing needs that bound to be **conditional**, not absolute: entering
the oncoming lane is legal when the obstruction can be cleared before oncoming
traffic reaches the pinch. Today `CarCollisions` asks for the lateral shift
(`OvertakeShiftM`) and the corridor clamps it back. That is why
`park_block_overtake` and `park_block_priority` (e2e 17/18) sit behind the
parked car and time out.

So the corridor is not wrong — it is right for the wrong question. Rule 2 and
careful passing are not "rule + exception". They are one rule with a condition,
and the condition needs a fact the sim does not currently produce: **who holds
the pinch.**

---

## 2. The model

Three passes per substep. None of them mutates state that a later car reads.

```
facts      (per step, over pairs)   who is where, gaps, TTC, obstructions,
                                    clearances, sight line
   ↓
proposals  (per car, per resource)  "I want resource R; feasible?; cost of
                                    waiting vs. cost of taking" — a VALUE,
                                    no action, no authority
   ↓
awards     (per contested resource) one winner, chosen from a SET of proposals
   ↓
application (per car)               OvertakeShiftM, the speed cap, the
                                    corridor bound, the courtesy signal
```

### The invariants (binding)

1. **Parameters change preference, never feasibility.** A role may stop caring
   about courtesy; it may not enter a position where a conflict is unavoidable.
   Every weight lives in the award comparison; the filter is physical and
   identical for all roles.
2. **Safety is a filter, progress is the objective.** Among admissible plans
   only, minimise cost. No number makes an inadmissible plan win. This mirrors
   the `Raceline` header ("Rules 1 and 2 are HARD BOUNDS — the optimiser cannot
   trade them away for speed") at the plan level.
3. **One decision per car.** Others read the *published award*, they do not
   re-plan. A second planner on the same resource is the bug, not an
   inefficiency: two independent plans can disagree and both drive into the
   pinch.
4. **Awards are per resource, from a set.** Never per car, never by iteration
   order. A car cannot bulldoze the others because it was computed first —
   there is no "first" in a set.
5. **Only contested resources are auctioned.** A resource with 0 or 1 candidate
   has no decision. Resources are **derived from conflicts**, never enumerated
   from the map.
6. **Commit, don't converge.** No fixed point, no iterated best response. An
   award is held until an event invalidates it; re-award only past a margin.
7. **The visual displays, it never drives.** The courtesy signal is a rendering
   of the award. Nothing reads it. If award and signal can disagree, that is a
   bug to be visible, not a mechanism.

---

## 3. The resource object

A **resource** is a stretch of carriageway that fewer cars can occupy at once
than want to. Not a lane, not a segment — a *stretch with a capacity*.

| aspect | decision |
|---|---|
| key | `(segment index, arc range [s0, s1])` — geometry, so identity is stable when the cars around it change. **Not** `(carA, carB)`: three cars on one pinch would appear as three objects with three awards. |
| capacity | 1 for a single-file stretch (`roadWidth < wA + wB + MinPassClearanceM` — R26's test, already computed) |
| candidates | the conflicting pairs the grid already found, grouped by key |
| states | `free` → `claimed(by uid)` → `released` |
| invalidation | obstruction moved, resource became free, a candidate became infeasible |

**Grouping is a correctness requirement, not an optimisation.** `PassagePriority`
compares a scalar (progress into the stretch, tie → uid), so pairwise happens to
agree with the group today. It stops agreeing the moment feasibility is joint:
A-vs-B clearable, A-vs-C clearable, B-vs-C clearable, and all three together
**not**. A pairwise view cannot see that, and cannot see a cycle that only exists
across the group.

Cost: no new asymptotic term. Conflicts come from the pair loop that already
runs; grouping is a hash over a handful of keys; auctions run on sets of size
~2. On the fig8 stress runs that is a handful of crossings and pinches, not 50.

---

## 4. The award

For a contested resource with candidates:

1. **drop the infeasible.** A candidate is infeasible if it cannot clear the
   resource while the others hold their course, *or* if the others cannot stop
   in time while it takes it. The physical test is `ApproachCapMps(vl, roomM)`
   — "fastest approach speed that still gets the car to rest inside `roomM`,
   allowing a reaction interval first and braking at the rate a driver actually
   uses" — applied to the **other** car. That direction is new; the function is
   not.
2. **drop the unresolvable.** Awarding this resource must not create a cycle in
   the blocked-by graph. `CarCollisions.Resolvable(uid, blockers, visiting,
   maxDepth)` is already that test (R30), scoped to crossings.
3. **compare the survivors** by cost — the one whose **cost of not getting it**
   is larger wins.
4. **tie → lower uid** (R15), genuine ties only.

Stability: an award is held until invalidated, and re-awarded to the other side
only past a margin — the shape of `_releaseHold` / `AlertClearHoldS`, applied to
the negotiation instead of to a brake decision.

### The cost terms, and where each number comes from

| term | source | status |
|---|---|---|
| time I lose by waiting | length of the pinch ÷ speed I can clear it at, vs. the time the holder needs | derived, exists |
| how long I have already waited | R31 window machinery (`_sinceTime`, `_sinceMeters`) | exists |
| how many cars I am blocking | the blocked-by graph R30 walks | exists, one structure two uses |
| metres made toward the destination | Q1 fleet flow (`_flowMeters`) measures the symptom, not the cause | needs the per-car term |
| **disturbance**: making an oncoming car brake | **nothing** | the only unfitted weight — see §8 |

The wait-time term is what makes the award fair rather than merely
deterministic: it enters the cost monotonically, so a car that keeps losing
becomes more expensive until it wins. That is **R16 anti-starvation**, and the
award is where it belongs.

---

## 5. Rules to write into §8

- **R32 Careful passing of an obstruction** *[implemented, undocumented]* —
  an obstruction (stopped, or crawling far below my speed) in my lane on a
  carriageway too narrow to pass without leaving my half. I may take the free
  lane **only if I can clear the pinch before oncoming traffic reaches it, and
  only if the oncoming car can physically stop in time should it need to.**
  Otherwise I wait behind the obstruction. The lateral need is
  `touchM + OvertakeClearM − |lat|`, bounded by the room inside the carriageway.
  The pinch is a resource; the claim decides who holds it.
- **R12 Voluntary yield (courtesy)** *[proposed → implemented]* — the concession
  has a cost: making another car brake is legal but discourteous. It is a
  **weight in the award**, never a change to feasibility.
- **R16 Anti-starvation** *[proposed → implemented]* — the cost of waiting grows
  with time waited, so a car that repeatedly loses an award eventually wins it.
  Monotone; it can never make an inadmissible plan admissible.
- **R30 Keep the resource resolvable** *[implemented, scope widened]* — from
  "crossing" to any resource. The graph test is unchanged.
- **R34 Negotiated right of way** *[new]* — the plan chooser itself: facts →
  proposals → awards → application, one decision per car, awards per contested
  resource, committed not converged.
- **R35 Emergency priority** *[new]* — a role with special rights wins the claim
  on a contested resource and pays ~nothing for disturbance. **Feasibility is
  unchanged**: a priority vehicle still never enters an unavoidable conflict.
  The mechanism is the arbiter, not a per-role behaviour branch.

---

## 6. The courtesy signal (bitteschön)

Real-world German practice: a driver who **gives up** their right of way briefly
flashes their headlights — a **`bitteschön`** gesture, "please, after you, I am
letting you through". Not a thank-you.

| | role | in the data |
|---|---|---|
| **actor** — flashes | the one without the right, conceding | the car holding the award-imposed cap |
| **target** — receives "go ahead" | the one being granted | the `#12` in `ActiveCapKey = "r32#12"` |

The gesture is **directed outward** — toward the other car and the stretch being
handed to it. Not at my own path.

**Trigger: a cap of a right-of-way family, newly engaged.** Not "a cap exists":

| cap key | meaning | flash |
|---|---|---|
| `avoid#26` | ordinary following distance | **no** — no right was given up |
| `sign#C#12` | yield sign, car 12 at the crossing | **yes** |
| `r14#node#12` | blocked exit, must not enter | **yes** |
| `passage#12` | single-file stretch, I conceded the claim | **yes** |
| `r32#12` | pinch past an obstruction, waiting behind | **yes** |

Mechanics: **one-shot, on the concession event, fixed short duration** — the
shape of the hazard lights (`HAZARD_MIN_DISPLAY_S`, on for at least N s then off
exactly once), not a per-substep recomputation. Per-tick would flicker, which is
the same problem `_releaseHold` / `AlertGraceS` exist to fight.

Ride the existing channel: `headlights_on` is already exported and rendered. The
new part is the **spatial** element — the contested stretch. `ActiveCapKey` names
the *other car*, not the *stretch*; the cone needs the two arc positions, which
the R32 block already computes (`along`, `OvertakeScanM`, the obstruction's extent)
and does not publish.

---

## 7. Implementation order

Deliberately ordered so that every number is measured before it is fitted.

1. **Document R32 in §8** (and R26's generalisation) — **done 2026-10-23**:
   [DRIVING_MANEUVERS.md](DRIVING_MANEUVERS.md) §8 now carries R32, R34 and
   R35. The rule is stated before
   it is built; the code already implements half of it.
2. **Resource object + key.** `(segment, [s0, s1])`, built from the conflicts the
   pair loop already finds. No auction yet — just the object and its identity.
3. **Generalise R26's claim to the resource.** `PassagePriority` becomes the
   award for any resource, not only head-on single-file. Verify against the
   existing `fig8_xing_plain` and `park_block_*` scenarios.
4. **Feasibility in the other direction.** Apply `ApproachCapMps` to the
   conceding car. This is the step that makes the pinch honest.
5. **Conditional corridor bound.** `LegalCorridor`'s left bound relaxes while a
   pass is **committed** — keyed on the award, not on `OvertakeShiftM`. This is
   the step that should turn e2e 17/18 from timeout into a pass. **Stop and
   measure here** before adding any weight.
6. **R34 plan chooser**, disturbance term = 0 (i.e. pass whenever feasible and
   resolvable). Measure how often it disturbs others on the stress runs.
7. **R12 disturbance weight**, given a value from that measurement.
8. **R16 wait-time escalation.**
9. **R35 role profile** (police exists as a colour and a class; the role is new).
10. **R33 corridor/claim coupling** — the intent R33 reads becomes the award, so
    the invariant and the negotiation share one object.
11. **The courtesy signal** last: it is a display of an object that must already
    be correct. Export `active_cap_key` + resource extent in the per-car payload,
    add `courtesy_flash`, draw the cone in `MapRenderer`.

Step 5 is the first one with a visible payoff; steps 1–4 are plumbing and
evidence-free. If 5 does not fix 17/18, stop before 6 — the corridor is the
thing that was believed to be the wall, and being wrong about it invalidates the
rest of the plan.

---

## 7b. Step 5 measured (2026-10-20) — the premise is half right

Step 5 was implemented and measured before anything else was built. The probe
passes `leftReliefM: _car.OvertakeShiftM` from `BicycleNav` into
`LegalCorridor`, which now reads
`lft = max(-left, centreLimit*fNear - leftReliefM)`, and `leftReliefM` was added
to `SolveCacheKey` (a cached solve from a non-passing run must not be reused for
a passing one).

Reproduction (`--map basic`, primary teleported at 15 % of segment 0 at 50 km/h,
throttle held; a car parked at 50 % of segment 0):

| | before step 5 | after step 5 |
|---|---|---|
| lateral offset, right of centreline | pinned at **+1.38 m** | moves to **−0.58 m** (left of the line) |
| where it stops | 6.8 m before the obstruction | 6.8 m before the obstruction |
| verdict | timeout | timeout |

**The corridor was a wall, and relaxing it works** — the car leaves its lane by
2 m and crosses the centreline. `+1.38` is exactly `centreLimit = CAR_WIDTH_M/2 +
LANE_CENTRE_MARGIN_M = 1.4`, so the pin was the bound, not a coincidence.

**It is not the only wall.** Three more, each with evidence:

1. **The lane band.** `SolveLineImpl`'s `const double LaneBandM = 0.5` confines
   the line to ±0.5 m of `baseOffset`, and `baseOffset = max(nominal − shift,
   −maxOffset) = 1.75 − 2.25 = −0.5`. The realised offset is −0.51 — the car sits
   on `baseOffset`, not on a line that clears the obstruction. Clearing a 1.8 m
   wide parked car with a 1.8 m half-width plus margin needs ≈ −0.8 m. **The
   relief arithmetic is measured from the wrong reference**: `OvertakeShiftM` is
   `needM` relative to *my axis*, but it is applied to the *nominal lane offset*.
2. **The caps still stop it.** `[R2] car 1 stopped in my path` at t=0.35 and
   `[R7] car 1 ahead (gap 35.1 m)` at t=1.35 — the longitudinal rules still see
   car 1 in the path, because the line does not clear it (see 1).
3. **R33 fires.** `[R33] unexplained centreline crossing: car 2 crossed to side
   -1 on segment 0 (0.30 m off the line, no intent) at t=1.25 s`. The commitment
   stand-in decays: the R32 log shows the request collapsing
   (`2.25 m → 0.24 m → 0.42 m → …`), so at the moment of the crossing
   `LateralIntent` returns null. **This is why step 10 cannot be deferred** —
   R33 must read the award, not `OvertakeShiftM`. A decaying lateral request is
   not a commitment.

Gates after step 5: build 0 errors / 8 pre-existing warnings, unit suite still
**91 / 2**, e2e 17/18 still timeout — no new failures, none fixed.

**Conclusion for the plan.** The corridor bound is real and must become
conditional, but the plan's next step is not 6. It is a new step 4b: make the
lateral request and the lane band speak the same reference, so the line the
solver produces actually clears the obstruction. Until that is right, the
longitudinal caps will keep reading the obstruction as "in my path" and no
amount of corridor relief will move the car.

## 7c. The clearance curve (2026-10-20) — demand fixed, veto still wrong

`OvertakeClearM = 0.5` (a constant) replaced by `SideClearM(vMps) =
MinPassClearanceM + vMps * SideClearPerMpsM`, `SideClearPerMpsM = 0.05`, used at
all three R32 sites (the "not in my line" filter, the demand, the pinch test).
The body part of the demand was already per class (`touchM` from
`Config.VEHICLE_SIZES`); only the margin was fixed.

Same reproduction as §7b (`park_block_overtake`, 50 km/h, parked car at 50 % of
segment 0):

| | before | after §7b relief | after the curve |
|---|---|---|---|
| R32 demand | 2.25 m left | 2.25 m left | **2.95 m left** |
| realised lateral offset | +1.38 m (pinned) | −0.51 m | **−1.21 m** |
| centre-to-centre to the parked car | — | 2.26 m | **2.96 m** |
| side clearance | 0.46 m | 0.46 m | **1.16 m** |
| where it stops | 6.8 m short | 6.8 m short | **6.8 m short** |
| verdict | timeout | timeout | timeout |

**The curve does its job**: the demand now scales with speed, the car takes
1.16 m beside the obstruction — more than double the 0.5 m the veto asks for —
and it still stops.

**The remaining blocker is not lateral.** The car holds at a bumper gap of
3.6 m against `StandstillGapM = 3.0`, and the last named cap reason is
`[R7] car 1 ahead` — v1's forward corridor. So the longitudinal rule still
names the parked car as **my lead**, even though I am now a full lane to its
left. The pass is committed laterally and invisible longitudinally.

That is the "which one owns this situation?" contradiction the docs warn about,
made concrete: `CarCollisions` has a lateral rule that says *pass*, and a
longitudinal rule that says *stop behind*, both firing on the same pair, and
the longitudinal one wins.

**Next step is therefore not another clearance.** It is making v1's corridor
lane-true rather than road-true for a committed pass — the same missing object
as the R33 report at t=1.07 s (`unexplained centreline crossing … no intent`).
One commitment, read by three consumers: the corridor bound (§7b, works), the
longitudinal cap (broken), and R33's intent (broken, because the stand-in decays).

Gates after the curve: build 0 errors / 8 pre-existing warnings, unit suite still
**91 / 2**, e2e 17/18 still timeout. No new failures, none fixed.

## 7d. The commitment (2026-10-20) — one object, four consumers, 17/25 PASSES

Four changes, each one measured before the next:

**1. The commitment object.** `Car.PassUid / PassSegIdx / PassProgress /
PassShiftM`, and one owner `CarCollisions.PassCommitted(car, net)`. Alive while
the named obstruction is still ahead on the named segment, and — once past it —
while the car is still in the OTHER half. The return leg is part of the same
manoeuvre; ending the commitment at the obstruction's centre left the return
crossing unexplained (measured: wrong-side at t=1.47 s just past the parked
car, and a second unexplained crossing on the NEXT segment).

**2. The root cause of "the car never left its lane" — found with a probe, not
guessed.** A temporary `Console.WriteLine` at the nav's solve site printed ONE
line for the whole run: `shift=0.000 base=1.750 autoBase=False`. The raceline is
solved ONCE per `(SegIdx, Turn, PullingOver, PullingOut)` and the lateral request
is only read at that solve. A driver model that asks for 2.25 m left after the
line exists never gets it into the line. Fix: the pass state joins the rebuild
key (`BicycleNav.part1.cs:242`, `part2` key + comparison). Two rebuilds per pass,
not one per substep.

**3. v1's forward corridor reads the commitment.** A car I am mid-pass of is not
my lead — but only once my body is actually beside it (`|rayLat| > (wA+wB)/2`).
Exempting the pair outright let a follower drive up to a stationary lead
(`CollisionTests.StationaryLeadCar_FollowerBrakesAndStopsBehind`), so the
exemption is gated on the pass having started. Slow down, move over, then go.

**4. An obstruction is not any stopped car.** R32 now ignores a car that is
stopped but NOT parked: it has a route and may pull away, so borrowing past it
is not a safe commitment — v1 owns that pair. Crawling traffic (a tractor) is
still fair game. That is what the same unit test caught.

**5. The claim, made concrete.** A driver who must borrow the shared lane never
owns it against a driver who can stay in their own (`o2.OvertakeShiftM <= 0.05`
→ I wait). `PassagePriority`'s "entered further" only decides between two drivers
that BOTH need the resource. And the pinch zone extends to the inbound driver's
own look-ahead (`LookAheadForSpeed`) instead of a fixed 8 m — with the fixed
8 m, both cars entered the shared lane and contacted head-on at t=11.47 s.

### Measured, park_block_overtake (suite order: primary uid 1, parked uid 2)

| | before | after the commitment |
|---|---|---|
| lateral offset | +1.75 m, never moved | +1.75 → +0.90 → −0.56 → −0.34 |
| past the obstruction | no (stopped 6.8 m short) | **yes** |
| verdict | timeout | **✅ PASSED** — "reached the end flag, stayed on road, no violations" |
| lane guard | 0 crossings | 1 crossing, **0 unexplained** |

### Measured, park_block_priority (the claim)

No contact (0 CRASH lines, `crash_episodes = 0` on both cars). The behaviour now
matches the scenario's own words: the oncoming car — whose lane is clear — takes
the whole passage and drives to its destination; the primary waits behind the
parked car at prog 0.454, in its own lane, `wrong_side=false`, no crossings.

**Still a timeout**, and the reason is now precise: the primary waits correctly
but never resumes. It is held at the v1 standstill gap with speed 0, and a
lateral move needs motion — a stopped car cannot become "beside" the obstruction.
That is the missing second half of the clearance relation: while a pass is
committed and not yet started, v1 must cap to the speed at which the pair CAN
pass with the room available, not to the standstill hold.

Gates: build 0 errors / 8 pre-existing warnings · unit suite **91 / 2** ·
e2e 17/25 **PASSED** (first of the two to ever pass), 18/25 still timeout,
no new failures, no crashes.

## 7e. R36 — the borrow has a window (2026-10-21, binding user decision)

Decision recorded as **R36** in `docs/DRIVING_MANEUVERS.md` §8: usage of the
opposite lane is reduced to a minimum. Implementation:

- `PassPending` (the RECORD: an obstruction I intend to pass, alive until the
  manoeuvre ends) and `PassCommitted` (the WINDOW: over which stretch the borrow
  happens) are **separate predicates**. Merging them is what deleted the intent
  32 m before the pass and made R33 call the crossing unexplained.
- The window's IN bound is `v · LANE_CHANGE_TIME_S` clamped to
  `MERGE_LEN_MIN_M`/`MERGE_LEN_MAX_M` — the repo's own speed-dependent lane-change
  length (the one R17 uses for a merge), not a new constant.
- Outside the window the shift is **released, not decayed**: a decay leaves the
  nav's next line solve reading the old shift, which is what parked the car on
  the centreline for 90 m.
- `LaneGuard.LateralIntent` reads `PassPending` (the manoeuvre, out AND back);
  the corridor relief and v1's exemption read `PassCommitted` (the borrow itself).

### Measured, park_block_overtake at warp 4

| | before R36 | after R36 |
|---|---|---|
| crosses out | 48 m before the obstruction | **10.5 m before** |
| metres of oncoming lane | ~220 m | **79 m** |
| max excursion | 0.87 m | 1.19 m |
| clearance at the pass | 0.47 m at 57 km/h | **0.98 m at 39 km/h** |
| back in own lane | never | **yes** |
| verdict | PASSED (bad drive) | **PASSED** |

The drive is now in the "a human would do this" column for the OUT leg and the
clearance; the IN leg is still late.

### Two gaps the measurement exposes, both open

1. **The return is 68 m late** (back at 120.7 m along, obstruction at 52.2). The
   window ends 33 m past the obstruction, but the shift is a step, so the car
   comes back only after the line is re-solved. Closing this needs the **ramp**:
   the line's offsets must taper to nominal over the return leg INSIDE the window,
   which is nav work (`ApplyEndBlends`-style), not a CarCollisions constant.
2. **The suite does not check `lane_guard_errors`.** The sim reports
   `unexplained_crossings = 1` for the return crossing and the scenario still
   passes, because the suite's lane-guard check reads the per-car `wrong_side`
   field only. R33's error is therefore not enforced end-to-end — the suite
   should fail on a non-zero delta of `unexplained_crossings`.

Gates: build 0 errors / 8 pre-existing warnings · unit suite **91 / 2** ·
e2e 17/25 **PASSED**, no wrong-side frames flagged by the suite.

## 7f. R36 closed with a line ramp — "return as soon as possible" (binding decision)

Second binding decision: the passing car returns to its own lane **as soon as
possible**. The window alone could not do it: the solve hands back ONE lateral
offset for the whole route, so the borrow is a step and the car comes back only
where the next solve happens (measured: back 68 m past, 79 m of the other lane).

Fix: `BicycleNav.RampPassBorrow` re-writes the offsets **along the line** and
rebuilds the points from the centreline (`centre = point − normal·offset`) — one
pass over the line, no second solve. The shape:

- **out** over `laneChangeM(v)` before the obstruction — the ramp finishes as the
  car reaches it, so the shift is complete by then;
- **held** while the bodies still overlap lengthwise;
- **back** over `laneChangeM(v)` starting at the first point where the return is
  physically possible — the car's rear clear of the obstruction's front.
  "As soon as possible" IS that point; it is not a constant.

`laneChangeM(v)` is the repo's own speed-dependent lane-change length
(`v · LANE_CHANGE_TIME_S` clamped to `MERGE_LEN_MIN_M`/`MERGE_LEN_MAX_M`), the
one R17 uses for a merge. The clearance term is `CarCollisions.SideClearM(v)`,
now public — one definition, read by the demand, the veto and the nav.

### Measured, park_block_overtake at warp 4

| | original | after §7e window | after the ramp |
|---|---|---|---|
| crosses out | 48 m before | 10.5 m before | **1.9 m before** |
| metres of oncoming lane | ~220 m | 79 m | **12.1 m** |
| back | never | 68 m past | **10.2 m past** |
| clearance at the pass | 0.47 m at 57 km/h | 0.98 m at 39 km/h | **0.79 m at 31 km/h** |
| max heading change / frame | — | — | **5.1°** (limit 30°) |
| lane guard | 1 crossing, 1 unexplained | 1 crossing, 1 unexplained | **2 crossings, 0 unexplained, 0 wrong-side frames** |
| verdict | timeout | PASSED | **PASSED** |

12.1 m of borrowed lane for a 4.4 m car passing a 4.4 m obstruction with 0.79 m
beside it is the minimum the geometry allows — the car's own length plus the
longitudinal overlap, and nothing more.

Gates: build 0 errors / 8 pre-existing warnings · unit suite **91 / 2** ·
e2e 17/25 **PASSED**, 18/25 still timeout (the wait-then-resume claim, untouched
by this change).

## 8. The one number that must not be guessed

**R12's disturbance weight has no measurement behind it yet.** Nothing in the
repo says what it costs to make an oncoming car brake from 14 m/s. It is the only
term in the whole award that is not derived from a physical quantity, and it is
the term that decides whether the traffic-jam case actually triggers a pass.

The repo's history is what happens when the *wrong* things get fitted:
`YieldStopNoseGapM` moved 4.5 → 6.0 because a measured SAT overlap said so;
`MinSweepSpeedMps` is 1.6 with a comment arguing 8 m against 25 m. Those are
physics and geometry constants that got fitted, and each fit needed a measurement
to lean on. Here there is no measurement to lean on yet.

So: **build the auction with disturbance = 0 first** — pass whenever it is
feasible and resolvable — and count, on the stress runs, how often a pass forces
an oncoming car to brake and how far. That number is the evidence. Only then does
the weight get a value. If a role ever needs a *physical* number changed, the
design is wrong, not the profile.

---

## 9. Traps, named so they can be recognised

| trap | why it bites |
|---|---|
| a loop over cars that writes a decision | the order leak: low uids become systematically greedy. It will not crash — it will look like the fleet insists on its right of way. |
| pairwise feasibility on a shared resource | three cars, each pair clearable, the triple not. The filter is wrong, not slow. |
| re-deciding every 16 ms | the known flutter family (`_releaseHold`, `AlertGraceS`, cap flicker). Commit instead. |
| a role that changes feasibility | an ambulance that may enter an unavoidable conflict. Roles weight preference only. |
| the visual becoming load-bearing | oncoming cars yielding because they saw a cone. The award decides; the cone displays. |
| fitting R12 before measuring | the one weight with no evidence, fitted to make a demo look nice. |
| resource keyed by car pair | three cars → three objects → three awards for one stretch. |

---

## 10. Verification gates

After each step: `dotnet build` clean (0 errors; the warnings that remain are
pre-existing nullable-annotation noise — quote the gate as "0 errors", not as a
fixed warning count, see [TESTING.md → "Current baseline"](TESTING.md#current-baseline-the-one-place-a-number-is-written)),
`dotnet test DrivingGame.Sim.Tests/` at **91 passed / 2 skipped**, and
`scripts/run_e2e.sh --warp 4` — **three known-red** as of 2026-10-23:
`slow_pass_priority` (#20, timeout), `fig8_xing` (#26, unexplained centreline
crossing) and `fig8_xing_plain` (#27, contact after flow decay). The counts
this section carried while the plan was written ("20 of 24", then "17/25")
are the suite of those dates — the scenario list is
[TESTING.md §2a](TESTING.md#2a-the-scenario-list-generated-do-not-retype) and
the current verdict is the baseline block above it.

New assertions this plan should add, in order of the steps:

- a resource is awarded at most once per substep, and the award is a function of
  the candidate set (replay-identical per R20)
- `lane_guard_errors` (R33) stays 0 — the corridor relaxation must not open an
  unexplained crossing
- `unexplained_crossings` is the machine-readable form of the same
- a `park_block_*` scenario reports a `[R32]` decision and reaches its end
  segment
- no `passage#`/`r32#` award survives a step where its resource is free

## 7g. The cap side of the clearance relation (2026-10-21)

The clearance curve (§7c) states demand: `clearance = MinPass + v·PerMps`.
Demand alone left one half missing — the **cap**. Measured on `park_block_priority`
in a batch run: the primary crept to 7.6 m behind the parked car and then held at
0 km/h for the rest of the scenario. A standstill can never *start* a pass, so the
pair stays single-file and the scenario times out.

`CarCollisions` v1 now closes the relation. When a pass is committed but the
bodies are **not yet beside** each other, the cap is the same relation inverted:

```
vPass = (room − MinPassClearanceM) / SideClearPerMpsM     (floor 1 m/s — a creep, never a hold)
```

`room` is the segment's left room for the car, the same quantity R32's demand
side reads — so demand and feasibility are derived from one function and cannot
contradict. The cap is recorded as `r32#passive` / rule `R32 creep`.

Measured after the fix: `park_block_priority` passes standalone **and** in
batches (three consecutive `--tests 17-20` batches, 4/4 each).

## 7h. Room is a property of the carriageway, not of the car's drift (2026-10-21)

Two measured findings, both from `park_block_priority` staged with the
obstruction ON the centreline (x 98.25 on a 7 m road):

1. **R32's room clamp read the car's CURRENT lateral position.** A primary
   that had already drifted 0.9 m left of the centreline computed
   `roomLeftM = 0.4 m`, so the 2.7 m the pass needs was never asked for,
   R32 stayed silent, and v1's following rule pinned the car at 0 km/h.
   The bound is now the carriageway's **kerb-to-kerb travel**
   (`seg.Width − car.Width`), with the nav's own road-edge clamp still
   keeping the line on the road.
2. **The same measure in v1's creep cap** (§7g): the creep's `roomP` used
   the current-position room and fell below the feasibility floor for a
   drifted car. It now uses the kerb-to-kerb measure too.

Physical note recorded by the data: a 4.4 m car parked ON the centreline of
a 7 m road leaves 8.8 m of body for 7 m of road — the pass only fits by
hugging the far kerb, and the passing runs do that at x 101.0 (left edge
1.5 m over the painted edge, inside the paved polygon the validator uses).
That is a real squeeze, not a licence to drive off the road.

Measured after both changes: `--tests 17-20` **4/4 in three consecutive
batches**, `--tests 1-16` **16/16**, unit suite **91 / 2**. Watch mode
(`--watch`, sim frozen while staging) passes 18 standalone; in a four-
scenario watch batch it still fails intermittently — the primary commits
(`r32#passive`) but its own line leaves it at x 99.14 instead of advancing
into the borrow. Unresolved; the nav's line, not the cap, holds it.

## 7i. The auction, the role, the courtesy (2026-10-21)

Built, in the order the plan allows:

| step | what | where |
|---|---|---|
| 2 | the resource object — `(segment, [s0, s1])` quantised to 0.5 m, never a car pair | `DrivingGame.Sim/Negotiation.cs` |
| 3 | the award generalised to any resource; the pair loop asks `Negotiation.Holds` instead of comparing two cars — at the R26 head-on claim and the R32 pinch claim | `CarCollisions` sites |
| 4 | feasibility both ways: the cost reads `ApproachCapMps`-style quantities already on the cars; the conceding car keeps its cap | existing, plus the creep cap of §7g |
| 6 | R34 plan chooser with **disturbance = 0** — pass whenever feasible and resolvable | `CostOfWaiting` |
| 7 | R12 disturbance weight: **still 0, named as such** — the measurement that would give it has not been taken | `CostOfWaiting` |
| 8 | R16 anti-starvation: `Car.StandstillS` enters the cost monotonically | `Car`, `SimEngine` |
| 9 | R35 role profile: `Car.EmergencyRole` (a police sprite is on duty); the role multiplies PREFERENCE only, never feasibility | `CostOfWaiting` |
| 11 | the courtesy signal: the **conceder** sets `CourtesyForUid`/`CourtesyKind` aimed at the car whose passage it grants; `/state` exports both; the renderer draws a warm cone in the WORLD layer pointing at the target, not at the nose | `CarCollisions`, `SimEngine`, `MapRenderer` |

Stability: an award is held across substeps and re-awarded only past
`FlipMargin = 0.4` of cost, so a contested stretch cannot flip owner every
frame and make both drivers hesitate in turn. Awards are dropped by
`Negotiation.Keep(liveKeys)` once no contest is about the stretch any more.

Measured after the change: unit **91 / 2**, `--tests 1-16` **16/16**,
`--tests 17-20` **4/4**, `--tests 21-27` as baseline (the known
`fig8_xing` crash). The `[R32] lane blocked by car N ahead` decisions — the
exact site that sets the courtesy — fire in the stress runs, so the signal
is being produced; **the cone is not yet verified on screen**, and the tape
does not carry the courtesy, so a review cannot show it yet.
