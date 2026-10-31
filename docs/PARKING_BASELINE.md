
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
