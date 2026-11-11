
## The bay rule (owner decision, 2026-11-02)

The rule for parking between other cars is §1c of
[DRIVING_MANEUVERS.md](DRIVING_MANEUVERS.md) — three tiers over three
moments: a bay exists when `free = gap − body length ≥ 1.00 m` (a SUM, the
split over the ends is free), the moving car is bounded by **contact only**,
and a finished park needs `each end ≥ 0.20 m` at rest. One clearance constant
cannot carry that split, and `PARK_NEIGHBOUR_CLEAR_M` (0.25 m) as one box for
plan and acceptance is what the sections below keep running into.

Which car gets a bay is §1d of the same section (owner decision, 2026-11-03):
a bay is claimable **only from beside it** — the claim window is the mouth minus
this car's own length, which is tier 1's `free` number again — the first car
past the mouth takes it, the order is the lane order, and **nothing is reserved
for choosing**. The claim is published as the car's own `park_target`
(`behind_uid`/`ahead_uid` name the mouth), and the one invariant kept testable is
one claimant per mouth.

Everything below is measurement history from 2026-10-31 — it predates that
rule, and where it disagrees with it, §1c is the current text.

## Bay geometry and the accepted pose (measured 2026-10-31)

Accepted end pose of a park: `|psi| <= PARK_ACCEPT_PSI_DEG` (7 deg) and
`|off| <= PARK_ACCEPT_OFF_M` (0.30 m) - owner rule: the only real limits are no contact with
either neighbour, no leaving the road, and no occupancy of the oncoming half; a final
pose up to ~7 deg off the kerb is accepted, and no intermediate angle is forbidden
(issue 39 retired the idea of an entry-angle rule as policy; the 20 deg arm limit is
documented on the constant with what both 20 and 45 deg measure).

Bay slack, computed through the sim own frame (`Car.BodyCenter()` +
`ObstacleGeometry.BoxCorners`) from the recorded poses of tape_T_386_26:

| pose | clearance to the nearer neighbour |
|---|---|
| straight, centred in the bay | 0.500 m |
| as an aborted e2e tuck ended (1.0 m outside the row line, 22.7 deg) | 0.342 m |

The bay in issue 31 scenario (neighbour centres 8.54 m apart, 3.14 m Fiat bodies) leaves
5.40 m for a 4.40 m car, i.e. about 0.50 m of total fore/aft slack. The parking envelope
inflates each neighbour by `+/- PARK_NEIGHBOUR_CLEAR_M` (0.25 m) on BOTH axes, so 0.50 m
of that slack is consumed by the guard: at the 22.7 deg pose the effective clearance is
-0.16 m and the guard is right to hold. Measured consequence: lowering the guard to
0.10 m moves the end pose by 9 cm (253/152 cm to 244/135 cm from the kerb), and lengthening
the forward unwind from 1.2 m to 3.5 m moves it by 0 cm - because in the wide-avenue case
the unwind never arms (0 unwind records while the guard holds the wheel for hundreds of
ticks at v = -0.80 m/s). The open defect is therefore arming, not geometry: a held wheel
is a stall even while the car keeps rolling, and the stall test currently requires
standstill.

## The claim, as built (2026-11-03)

§1d is implemented and observable, not theoretical: `BicycleNav.TryClaimBay` asks the row
every tick (the window in `CarCollisions` opens on the ASK, not on a latched style —
`ParkJobActive` was the reason the row stayed invisible for 45 minutes of measurement), and
`SimEngine.ParkTargetPayload` renders the claim or answers `null`. Measured on the row of the
worked numbers, one host on `:5012`, and in a graded scenario:

```text
[BAYASK] car #6 claimed: row=1 beside=1 rear=121.57 front=128.47 flank_uids=1/2
✅ pm1: sim box centre u=125.02 (1/2 flank it); rule says centre u=125.02 (Δ -0.00 m)
✅ pm1 bay: rule wants u=121.57..128.47; car spans 121.88..126.64
[BAYASK] car #7 flanks=4 row=1 beside=0 at u≈2095,743   ← the row is seen, this car is not beside a mouth
```

What is NOT done, and stays red on purpose: the shuffle through a tight bay (§1b, the known-reds
in `BayReverseInTests`). With the leader's manoeuvre unfinished the lane behind it never moves,
so pm2…pm5 never reach a claim window — `park_target: null` for them is the honest answer, and
pm1 rests 0.74 m off the centred pose while `parking.parked` stays false. A test that turned
either of those green by moving a threshold would not be testing the rule.
