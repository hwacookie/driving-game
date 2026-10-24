# Smoothed geometry — design notes
> Status: **implemented and merged to `main`.** This file was the design
> companion to the turn-planning rework (written on the since-merged branch
> `turn-planning-rework`, in the days when the turn-rework plan document
> was the live plan (retired 2026-10-23, the rework is finished). It records
> the non-obvious decisions and the verification evidence, and is now the
> home of the geometry decisions themselves.
>
> **Open item, stated up front because it hides in the addendum below:**
> `Config.JUNCTION_CORNER_RADIUS_M` is a **fixed 4 m** for every corner. Per
> RASt/RAL it should depend on road class / design vehicle ("nach Art und Lage
> der Straße"). That is still true on 2026-10-23 — see the 2026-08-31 addendum.
>
> **Verification numbers in this file are dated measurements, not the current
> verdict.** The suite's live state is
> [TESTING.md → "Current baseline"](TESTING.md#current-baseline-the-one-place-a-number-is-written).
>
> **Addendum 2026-08-31 — junction corners replaced by Eckausrundung:**
> the node-centred fillet arcs described below (R = 6 m through the node,
> buffered by a road half-width) are REPLACED. At every degree>=3 corner
> the grass corner where two road EDGES meet is now rounded with a circular
> arc of `config.JUNCTION_CORNER_RADIUS_M` (4 m, fixed; TODO: should depend
> on road class / design vehicle per RASt/RAL "nach Art und Lage der
> Straße"), tangent to both edges - the standard Eckausrundung
> (de.wikipedia.org/wiki/Eckausrundung). The paved fill is the curvilinear
> triangle between corner point and arc, built directly (no buffering).
>
> Why: the buffered node-centred capsules bulged up to ~5.4 m past the
> kerbs where road widths differ (measured at the 3.5 m one-way × 7 m
> crossing, test map tile (2,1)): two of the four corners were buffered by
> the wide road's half-width and two by the narrow one's (seg_a = first
> spoke in angle order), producing asymmetric lobes - visually "the wide
> road bending into the narrow one". Rendering and drivable area were
> affected alike (same shared polygons).
>
> Unchanged: the driving reference line still rounds route corners with
> `BicycleNav.CORNER_RADIUS_M` (6 m through the node); e2e 19/19 green
> with the new paved geometry, so the two remain compatible.
> *(2026-10-23 note: the paragraph below is the **Python-era** payload —
> `junction_fillets`, `radius_px` and `obstacles.py` do not exist here
> anymore. Today `/map` answers `road_strips`, `road_polygons`, `segments`,
> `paved_edge_rings`, `centerlines`, `lane_markings`, `stop_lines`,
> `crossing_markers`, `oneway_arrows`, `parking_marks`, `signs`,
> `elevated_*`, `marking_style`, `start_points`, `bounds`, `units`,
> `road_color`, `bg_color` (see `MapPayload.cs`), and a junction corner is
> served through `paved_edge_rings`/`crossing_markers` - the fillet arcs are
> built in the sim (`SmoothGeometry.For(network).JunctionFillets`) and are
> not a separate key. A physical length never carries a `…Px` name -
> [UNITS.md](UNITS.md) rule 3.)*
> The `junction_fillets` entries then carried `corner` + `arc` (+ seg_a/seg_b,
> radius_px) instead of t1/t2; consumers: `_build_smoothed_junction_fillets`
> (paved fill) and `obstacles.py` (parked-car alignment along the arc).
>
> **Addendum 2026-10-19 — the route line follows the same chain, and the
> 6 m fillet only applies at REAL corners:** `RoadNetworkGeometry.SmoothChain`
> replaced the fixed-radius fillet for gentle bends. A chain is splined
> (centripetal Catmull-Rom, resampled at 0.5 m) through its shape points
> UNLESS a bend is sharper than 30° (`RoadNetworkGeometry.SmoothChain`'s
> `maxCurveTurnRad`; the doc comment above it calls the rule
> `MAX_CURVE_TURN_DEG`) — then it is a
> real corner and keeps the `ROAD_CORNER_RADIUS_M` (6 m) circular fillet
> (`SmoothGeometry.CornerFillet`). Why: a pure spline through a sampled
> curve (a figure-8 lobe, 30 m chords, 22° per node) kinked 0.12 m at every
> node instead of curving at its own ~115 m radius, while a pure spline at a
> genuine 90° corner with 250 m legs interpolated with tangents blended 45°
> off the legs and swung 18.5 m OUTSIDE the test map's corner (the
> `RightTurn_FollowsCornerAndLimitsSpeed` unit test caught it). Rule of
> thumb: **many small bends = recover the curve, one big bend = fillet the
> corner.** Closed chains (a loop) continue past the seam through the loop's
> own nodes and cut back where it closes — but only when the seam is itself
> a gentle bend (a roundabout ring); a seam that is a real corner (a figure-8
> lobe closing at its crossing node, ~86°) stays sharp, otherwise the crossing
> reads as a pinch with asymmetric arms.
>
> `BicycleNav` no longer fillets its reference line at a fixed radius either
> (the `CORNER_RADIUS_M` the 2026-08-31 addendum above still mentions is gone
> from the nav): it builds the route from the SAME smoothed chain the pavement
> is built from, so nav and pavement agree by construction.

## The founding principle: the graph is sacred, the curve is a function of the graph

Raw OSM geometry is a **polyline**: every connection between consecutive nodes
is a straight chord. So κ = 0 on every chord and a kink (κ = ∞) at every node —
which is why a speed profile reads "straight road" across a bend and never
brakes (the roundabout chord-curvature bug was exactly this in miniature), why
pure pursuit gets kinked targets, and why the paved polygon and the driving line
can disagree about the same corner.

The answer is a rule about **where geometry may live**:

- The OSM node set and way topology are **untouched** — no inserted graph nodes,
  no way splits, no added shape points. The graph stays the single source of
  truth for routing, junction logic and the renderer.
- Each way's centerline **becomes a centripetal Catmull-Rom spline through its
  original nodes** — interpolating (the curve passes exactly through every node,
  so it stays faithful to the data) and C¹ (no kinks).
  *Why Catmull-Rom:* the tangent at a node follows the road's overall direction
  (previous node → next node), so the curve flows through the nodes with no
  zigzag and no overshoot; the support is local (moving one node affects only
  its two neighbouring pieces); cubic, cheap, standard.
  *Why centripetal:* OSM node spacing is irregular; the uniform variant can
  overshoot on uneven spacing, the centripetal variant cannot.
- **Nothing is inserted.** The spline is a *function defined by the original
  nodes*; position/heading/κ(s) are evaluated on demand. The only internal
  artifact is the arc-length lookup table — a cache of the arithmetic, which
  defines nothing and touches no graph node.
- The consequence that matters for driving: **a road whose OSM nodes form a
  chain of degree-2 bends is no longer a zigzag**. The spline reveals its true
  continuous curvature — the S-bends are real geometry the polyline was hiding,
  and κ(s) says exactly how fast each bend allows.

```
OSM way (nodes N0..Nk, irregular spacing)
    -> centripetal Catmull-Rom (interpolating, C1, no kinks)
    -> C(s) through N0..Nk
    -> position / heading / kappa(s)  evaluated on demand
         -> speed profile · pure pursuit · paved polygon · painted markings
```

The same rule generalises beyond this file: **the road is one continuous
surface, not a list of segments** — a segment is an implementation detail, and
the car reasons in "distance to the point where I must act", never in "which
segment am I on". That is why every consumer of the geometry (renderer,
on-road check, lane guard, driving line) reads the *same* curve — and why the
blinker means "the next place where the turn is still reachable", see
[DRIVING_MANEUVERS.md](DRIVING_MANEUVERS.md) §4 "Reachability" and
[SPEC.md](SPEC.md) "The Driving Line".

## One curve, all consumers

```
RoadNetwork (graph: nodes + segments, UNTOUCHED)
    |
    |  _merge_and_round_lines: linemerge through degree-2 nodes
    |  (merged line = chain of original OSM nodes; junction nodes are
    |   the line ENDS - the graph's degree>=3 nodes)
    v
SmoothedNetwork (built once, cached)
    |
    +-- per merged line: SmoothCurve = centripetal Catmull-Rom THROUGH
    |   the original nodes (interpolating, C1, clamped endpoints so the
    |   end tangent = last-chord direction, per §10)
    |
    +-- per degree>=3 junction: circular fillet arc (R = 6 m, capped by
    |   adjacent edge lengths) between the SAME tangent points the old
    |   _round_polyline_corners used -> tangent-continuous with the
    |   splines (the spline end tangent IS the chord direction)
    |
    +-- resampled dense polylines (0.5 m) of curves + fillets
            |
            +--> paved polygon        (buffer of the SAME lines, as before)
            +--> centerline dashes    (the SAME lines)
            +--> lane markings        (offsets of the SAME lines)
            +--> driving reference    (sub-curves of the SAME lines +
                 line (BicycleNav)         the SAME fillet arcs, lane-offset)
```

The resampled polylines are evaluation caches of the curve functions
(§10: "the only internal artifact is an arc-length lookup table"). No new
graph nodes, no way splits.

## Curvature: geometry-based, never table-based

`SmoothCurve.curvature_at(s)` is a central difference of `point_at`
(window `max(1.0 m, 1% of total)`, same formula the old `RefLine` used).
The per-sample `kap` table of the first `SmoothCurve` draft is GONE — it
was corrupted at every piece junction (duplicate sample → ds=0 → heading
0.0 spike → κ spikes up to ~16 1/m). With the duplicate samples removed
from the arc-length table AND curvature computed from geometry, both
failure modes are impossible.

## Why circular fillets (not the proposed Bézier)

`scripts/visualize_junction_fillets.py` measured the driven line's peak
curvature for the five test-map junction types:

| junction | circular fillet | single cubic Bézier |
|---|---|---|
| 90° corner | R = 7.47 m ok | R = 7.71 m ok |
| T-junction | R = 7.47 m ok | R = 7.71 m ok |
| Y-junction | R = 4.24 m ok | **R = 1.88 m — infeasible** |
| crossroads | R = 7.38 m ok | R = 7.71 m ok |
| sliver | R = 0.31 m (clamped by 4.2 m stub) | R = 0.34 m (same) |

A single cubic Bézier between the fillet tangent points overshoots the
arc's peak curvature for turn angles above ~100° (the Y-junction fork is
~120°). §10's "chained Bézier/arc for large angles" is therefore not
worth the complexity: the circular arc is already tangent-continuous with
the splines (the spline's end tangent is the last-chord direction, exactly
what the fillet is tangent to), feasible wherever the paved corner is,
and is what the renderer already draws. The Bézier idea is retired; the
circular fillet IS the §10 junction connection.

## Feasibility of the driving line

`BicycleNav.MIN_TURN_RADIUS_M = WHEELBASE / tan(MAX_STEER) = 2.7 /
tan(38°) ≈ 3.46 m`.

The driving line's max curvature is checked at every (re)build
(`_check_line_feasibility`, printed once per distinct value). Lines whose
peak κ exceeds 1/3.46 m are inherently tight — the only case on the test
map is the sliver junction, whose paved fillet is clamped by the 4.16 m
approach stub (tangent distance ≤ half the stub). The car's ACTUAL motion
stays feasible: the speed profile caps v at sqrt(A_LAT_MAX/κ) and the
bicycle model's lateral-accel cap + MAX_STEER clamp make the car cut the
corner at its 3.46 m limit (verified: all three sliver tests pass, no
validator violations). Clamping the reference line itself to 3.46 m would
push it OUT of the paved polygon (the pavement is tighter than the car
can turn) — i.e. the "fix" would make the car leave the road. So the
check reports, it does not silently reshape the line; the validator stays
the wall.

## What changed where

> Historical (the Python files of the retired `car/` repo). The C# equivalents
> are `DrivingGame.Sim/SmoothGeometry.cs` (`SmoothCurve`, `ResampleCurve`,
> `CornerFillet`, `SmoothedNetwork` with per-segment sub-curve refs and
> `JunctionFillets`), `RoadNetworkGeometry.cs` (`MergeAndRoundLines`,
> `SmoothChain`, width-varying ribbons, markings, endpoint snapping) and
> `BicycleNav.part1-3.cs` (the route line built from the same chain).

- `src/smooth_geometry.py` (NEW): `SmoothCurve` (kap table removed,
  duplicate samples removed, geometry-based `curvature_at`),
  `resample_curve`, `corner_fillet` (extracted from
  `_round_polyline_corners`), `SmoothedNetwork` (per-line curves +
  junction fillets + resampled polylines, cached).
- `src/bicycle_nav.py`: `SmoothCurve` moved out; `RefLine` now wraps a
  `SmoothCurve` (public interface unchanged: `total`, `point_at`,
  `heading_at`, `curvature_at`); `_maybe_rebuild` builds the route line
  from `SmoothedNetwork` sub-curves + fillets instead of
  `_round_polyline_corners` on raw chords; feasibility check added.
- `src/road_network.py`: `_merge_and_round_lines` /
  `_build_road_polygons` / `_build_centerlines` / `_build_lane_markings`
  / `_build_multiway_junction_fillets` consume `SmoothedNetwork` instead
  of `_round_polyline_corners`; `_round_polyline_corners` removed.
- `scripts/visualize_junction_fillets.py`: imports updated.

## Verification

Dated log of the runs that validated this geometry — the numbers are the suite
**of that date**, not today's verdict (that is the baseline link in the header).

- 18/18 deterministic tests green (0 off-road, 0 snaps, 0 teleports,
  0 wrong segment) — see `tests/turning_results.json`.
- `tests/test_road_network.py` green *(the Python suite of that date; its C#
  equivalents are `DrivingGame.Sim.Tests/`)*.
- Curvature sanity: sampled κ of every merged-line curve on the `basic`
  map is smooth (no spikes at piece junctions); the roundabout ring reads
  the true ring curvature (R ≈ 100 m) uniformly instead of 0-on-chord-
  middles / spike-at-kinks.
- Same-curve proof: renderer, on-road check and driving all consume
  `SmoothedNetwork` (single cached instance per `RoadNetwork`).

**Current (measured 2026-10-19, superseded as a verdict):** the numbered e2e
suite was 23/23 and the C# unit
suite 91/91 (2 skipped), which re-validated all of the above on the new
`SmoothChain` geometry (the current verdict is the baseline link above); the
same-curve proof is now structural —
`RoadNetworkGeometry` builds pavement, markings and the nav's route line from
`SmoothChain`, and `SmoothGeometry.For(network)` caches one
`SmoothedNetwork` per network (segment → sub-curve refs, junction fillets).

## One curve, all consumers

```
RoadNetwork (graph: nodes + segments, UNTOUCHED)
    |
    |  _merge_and_round_lines: linemerge through degree-2 nodes
    |  (merged line = chain of original OSM nodes; junction nodes are
    |   the line ENDS - the graph's degree>=3 nodes)
    v
SmoothedNetwork (built once, cached)
    |
    +-- per merged line: SmoothCurve = centripetal Catmull-Rom THROUGH
    |   the original nodes (interpolating, C1, clamped endpoints so the
    |   end tangent = last-chord direction, per §10)
    |
    +-- per degree>=3 junction: circular fillet arc (R = 6 m, capped by
    |   adjacent edge lengths) between the SAME tangent points the old
    |   _round_polyline_corners used -> tangent-continuous with the
    |   splines (the spline end tangent IS the chord direction)
    |
    +-- resampled dense polylines (0.5 m) of curves + fillets
            |
            +--> paved polygon        (buffer of the SAME lines, as before)
            +--> centerline dashes    (the SAME lines)
            +--> lane markings        (offsets of the SAME lines)
            +--> driving reference    (sub-curves of the SAME lines +
                 line (BicycleNav)         the SAME fillet arcs, lane-offset)
```

The resampled polylines are evaluation caches of the curve functions
(§10: "the only internal artifact is an arc-length lookup table"). No new
graph nodes, no way splits.

## Curvature: geometry-based, never table-based

`SmoothCurve.curvature_at(s)` is a central difference of `point_at`
(window `max(1.0 m, 1% of total)`, same formula the old `RefLine` used).
The per-sample `kap` table of the first `SmoothCurve` draft is GONE — it
was corrupted at every piece junction (duplicate sample → ds=0 → heading
0.0 spike → κ spikes up to ~16 1/m). With the duplicate samples removed
from the arc-length table AND curvature computed from geometry, both
failure modes are impossible.

## Why circular fillets (not the proposed Bézier)

`scripts/visualize_junction_fillets.py` measured the driven line's peak
curvature for the five test-map junction types:

| junction | circular fillet | single cubic Bézier |
|---|---|---|
| 90° corner | R = 7.47 m ok | R = 7.71 m ok |
| T-junction | R = 7.47 m ok | R = 7.71 m ok |
| Y-junction | R = 4.24 m ok | **R = 1.88 m — infeasible** |
| crossroads | R = 7.38 m ok | R = 7.71 m ok |
| sliver | R = 0.31 m (clamped by 4.2 m stub) | R = 0.34 m (same) |

A single cubic Bézier between the fillet tangent points overshoots the
arc's peak curvature for turn angles above ~100° (the Y-junction fork is
~120°). §10's "chained Bézier/arc for large angles" is therefore not
worth the complexity: the circular arc is already tangent-continuous with
the splines (the spline's end tangent is the last-chord direction, exactly
what the fillet is tangent to), feasible wherever the paved corner is,
and is what the renderer already draws. The Bézier idea is retired; the
circular fillet IS the §10 junction connection.

## Feasibility of the driving line

`BicycleNav.MIN_TURN_RADIUS_M = WHEELBASE / tan(MAX_STEER) = 2.7 /
tan(38°) ≈ 3.46 m`.

The driving line's max curvature is checked at every (re)build
(`_check_line_feasibility`, printed once per distinct value). Lines whose
peak κ exceeds 1/3.46 m are inherently tight — the only case on the test
map is the sliver junction, whose paved fillet is clamped by the 4.16 m
approach stub (tangent distance ≤ half the stub). The car's ACTUAL motion
stays feasible: the speed profile caps v at sqrt(A_LAT_MAX/κ) and the
bicycle model's lateral-accel cap + MAX_STEER clamp make the car cut the
corner at its 3.46 m limit (verified: all three sliver tests pass, no
validator violations). Clamping the reference line itself to 3.46 m would
push it OUT of the paved polygon (the pavement is tighter than the car
can turn) — i.e. the "fix" would make the car leave the road. So the
check reports, it does not silently reshape the line; the validator stays
the wall.

## What changed where

> Historical (the Python files of the retired `car/` repo). The C# equivalents
> are `DrivingGame.Sim/SmoothGeometry.cs` (`SmoothCurve`, `ResampleCurve`,
> `CornerFillet`, `SmoothedNetwork` with per-segment sub-curve refs and
> `JunctionFillets`), `RoadNetworkGeometry.cs` (`MergeAndRoundLines`,
> `SmoothChain`, width-varying ribbons, markings, endpoint snapping) and
> `BicycleNav.part1-3.cs` (the route line built from the same chain).

- `src/smooth_geometry.py` (NEW): `SmoothCurve` (kap table removed,
  duplicate samples removed, geometry-based `curvature_at`),
  `resample_curve`, `corner_fillet` (extracted from
  `_round_polyline_corners`), `SmoothedNetwork` (per-line curves +
  junction fillets + resampled polylines, cached).
- `src/bicycle_nav.py`: `SmoothCurve` moved out; `RefLine` now wraps a
  `SmoothCurve` (public interface unchanged: `total`, `point_at`,
  `heading_at`, `curvature_at`); `_maybe_rebuild` builds the route line
  from `SmoothedNetwork` sub-curves + fillets instead of
  `_round_polyline_corners` on raw chords; feasibility check added.
- `src/road_network.py`: `_merge_and_round_lines` /
  `_build_road_polygons` / `_build_centerlines` / `_build_lane_markings`
  / `_build_multiway_junction_fillets` consume `SmoothedNetwork` instead
  of `_round_polyline_corners`; `_round_polyline_corners` removed.
- `scripts/visualize_junction_fillets.py`: imports updated.

## Verification

Dated log of the runs that validated this geometry — the numbers are the suite
**of that date**, not today's verdict (that is the baseline link in the header).

- 18/18 deterministic tests green (0 off-road, 0 snaps, 0 teleports,
  0 wrong segment) — see `tests/turning_results.json`.
- `tests/test_road_network.py` green *(the Python suite of that date; its C#
  equivalents are `DrivingGame.Sim.Tests/`)*.
- Curvature sanity: sampled κ of every merged-line curve on the `basic`
  map is smooth (no spikes at piece junctions); the roundabout ring reads
  the true ring curvature (R ≈ 100 m) uniformly instead of 0-on-chord-
  middles / spike-at-kinks.
- Same-curve proof: renderer, on-road check and driving all consume
  `SmoothedNetwork` (single cached instance per `RoadNetwork`).

**Current (measured 2026-10-19, superseded as a verdict):** the numbered e2e
suite was 23/23 and the C# unit
suite 91/91 (2 skipped), which re-validated all of the above on the new
`SmoothChain` geometry (the current verdict is the baseline link above); the
same-curve proof is now structural —
`RoadNetworkGeometry` builds pavement, markings and the nav's route line from
`SmoothChain`, and `SmoothGeometry.For(network)` caches one
`SmoothedNetwork` per network (segment → sub-curve refs, junction fillets).
