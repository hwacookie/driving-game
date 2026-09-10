namespace DrivingGame.Sim;

public sealed partial class BicycleNav
{
    // ====================================================================
    // Route building & reference line (re)build
    // ====================================================================

    /// <summary>Build a node route: the node BEHIND the car, then the current
    /// segment, then the signaled turn at the next junction, then straight on
    /// for a few segments. Starting at the behind-node (not the junction
    /// ahead) is essential: the reference line must extend forward from the
    /// car's own position, otherwise the braking profile - which propagates
    /// each corner's low speed backward along the line - would read as ~0
    /// right where the car is, stalling it before it ever reaches the corner.</summary>
    private List<string> BuildRoute()
    {
        var net = _network;
        var car = _car;
        var seg = net.Segments[car.SegIdx];
        // The node we are heading TOWARD (the upcoming junction) and the node
        // BEHIND us (the route's start, so the line extends forward).
        string junction = car.Forward ? seg.EndNode : seg.StartNode;
        string behind = car.Forward ? seg.StartNode : seg.EndNode;
        var route = new List<string> { behind };
        int curSeg = car.SegIdx;
        string curNode = junction;
        // First hop: the signaled turn at the upcoming junction.
        string turn = IntendedTurn();
        // A roundabout exit that entered the horizon this build (node + turn
        // direction), so MaybeRebuild can re-arm the driver's signal for it.
        _routeExitDir = null;
        _routeExitNode = null;
        for (int hop = 0; hop <= HORIZON_SEGMENTS; hop++)
        {
            route.Add(curNode);
            int? nxt = hop == 0
                ? net.ChooseNextSegment(curSeg, curNode, turn)
                : NextAfterFirst(curSeg, curNode, turn);
            if (nxt is null || nxt == curSeg) break;   // dead end or no further road
            var nseg = net.Segments[nxt.Value];
            // Leaving a one-way ring via a non-oneway spoke: remember the
            // exit's geometric direction (negative=left, positive=right).
            if (hop > 0 && !nseg.Oneway && net.Segments[curSeg].Oneway)
            {
                double angle = net.GetExitAngle(curSeg, nxt.Value);
                _routeExitDir = angle >= 0 ? "right" : "left";
                _routeExitNode = curNode;
            }
            // The node on the far side of the next segment.
            curNode = nseg.StartNode == curNode ? nseg.EndNode : nseg.StartNode;
            curSeg = nxt.Value;
        }
        // Off-by-one: the loop's last hop RESOLVES one more segment (its `nxt`)
        // but never appends that segment's far node - so the final decision in
        // the horizon was known but not driven: the line ended at the junction
        // with no fillet, no corridor and no braking ramp for it.
        if (curNode != route[^1]) route.Add(curNode);
        return route;
    }

    /// <summary>Segment to take at a junction AFTER the first one. Normally
    /// the straight-ahead continuation. On a roundabout (a one-way ring with
    /// two-way exit spokes) the car leaves at the first exit spoke it reaches
    /// going around the ring - the ring's one-way direction fixes the exit
    /// order, so the geometric turn direction of the exit is not a reliable
    /// signal. This is what makes the car actually leave the roundabout
    /// instead of circling forever.</summary>
    private int? NextAfterFirst(int curSeg, string curNode, string turn)
    {
        var net = _network;
        var candidates = new List<(int Idx, double Angle, bool Oneway)>();
        foreach (int idx in net.GetConnectedSegments(curNode))
        {
            if (idx == curSeg) continue;
            double angle = net.GetExitAngle(curSeg, idx);
            candidates.Add((idx, angle, net.Segments[idx].Oneway));
        }
        if (candidates.Count == 0) return null;
        // Roundabout exit: a non-oneway branch off a one-way ring. The ring
        // segments are oneway, so a two-way candidate is an exit spoke. Take
        // the first one we reach.
        bool ringOneway = candidates.Any(c => c.Oneway);
        if (ringOneway)
        {
            var exits = candidates.Where(c => !c.Oneway).ToList();
            if (exits.Count > 0)
                return exits.MinBy(e => Math.Abs(e.Angle)).Idx;
        }
        // Otherwise: straight continuation (smallest |angle|).
        return candidates.MinBy(c => Math.Abs(c.Angle)).Idx;
    }

    /// <summary>(Re)build the route + reference line when needed. The route
    /// must stay STABLE while the car follows it. In particular it must NOT
    /// be rebuilt every time the car crosses a node (changes seg_idx):
    /// rebuilding re-anchors the line at the new behind-node, which on a
    /// U-turn / hairpin drops the part of the line the car has already driven
    /// and makes the projected s jump, corrupting both the speed profile and
    /// the steering. We rebuild only when: there is no line yet, the intended
    /// turn changed (driver signaled a new turn), or the car has reached the
    /// end of the line (extend ahead).</summary>
    private void MaybeRebuild(bool pullingOver = false, bool pullingOut = false)
    {
        var car = _car;
        string turn = IntendedTurn();
        var key = (car.SegIdx, turn, pullingOver, pullingOut);
        if (_ref is not null)
        {
            if (_routeKey == key)
            {
                // Same segment + same intent: only extend if we've reached the
                // end of the line. Once close to a destination it never needs
                // extending any more - its end IS the stop point, and re-solving
                // the raceline every frame inside the last few metres used to
                // cost ~120 ms per frame (the whole parking zone ran in slow
                // motion). "Close" matters: freezing extension the MOMENT a
                // distant destination is set forces the very first build to
                // solve the raceline for the ENTIRE route in one shot - on a
                // roundabout that one-shot route produced a malformed line right
                // at the ring-exit corner. Only freeze once actually near the
                // flag, so everywhere else the route builds and extends exactly
                // like a normal one.
                bool nearDest = _dest is not null &&
                                _ref.Total - S < PARK_BLEND_START_M + 15.0;
                if (nearDest) return;
                if (S < _ref.Total - 15.0) return;
            }
            else if (_routeKey is not null &&
                     _routeKey.Value.Turn == turn &&
                     _routeKey.Value.PullingOver == pullingOver &&
                     _routeKey.Value.PullingOut == pullingOut &&
                     RouteSegSet.Contains(car.SegIdx))
            {
                // We advanced to a segment that is part of the current route
                // (normal node crossing) with the same intent: keep the line,
                // just extend if near the end. pulling_over/pulling_out must be
                // part of this test: when the driver starts braking for the dead
                // end, the key CHANGES even though nothing about the route did -
                // and that change must rebuild the line immediately (it is what
                // adds the drift-to-kerb blend).
                bool nearDest = _dest is not null &&
                                _ref.Total - S < PARK_BLEND_START_M + 15.0;
                if (nearDest || S < _ref.Total - 15.0) return;
            }
        }

        Console.WriteLine($"[TBLINK] REBUILD key=({car.SegIdx},{turn}) s={S:F1} v={car.Speed * 3.6:F0}km/h oldKey={(_routeKey is null ? "null" : _routeKey.ToString()!)} pending={IntendedTurn()}");
        List<string> route = BuildRoute();
        if (route.Count < 2)
        {
            // Degenerate (isolated node) - fall back to the current segment's
            // two endpoints so we still have a line to follow.
            var seg = _network.Segments[car.SegIdx];
            route = new List<string> { seg.StartNode, seg.EndNode };
        }
        _route = route;
        var raw = route.Select(n => _network.Nodes[n]).ToList();
        // Round corners far more finely than the renderer does. The line is
        // resampled every 0.5 m and its curvature read over a 1 m window, so
        // the arc's own vertices must be much closer than that.
        var rounded = RoadNetworkGeometry.RoundPolylineCorners(
            raw, CORNER_RADIUS_M * RefLineMath.PPPM,
            arcSteps: CORNER_ARC_STEPS, fitEdges: true);
        if (_dest is not null)
        {
            // Cut BEFORE solve_line: the pull-over drift blend in
            // ApplyEndBlends anchors to the line's END, so the end must already
            // be the destination - cutting only afterwards would leave the
            // blend stranded on the chopped-off tail and the car would never
            // drift to the kerb.
            rounded = CutPolylineAt(rounded, _dest.Value.X, _dest.Value.Y,
                                    extendM: Math.Max(_parkStageM, CORRIDOR_RUNOUT_M));
        }
        // Shift the centerline into the driving lane (right-hand traffic). The
        // offset must be small enough that the car's right side stays on the
        // road for the NARROWEST segment in the route.
        double minWidth = RouteSegments().Count > 0
            ? RouteSegments().Min(i => _network.Segments[i].Width)
            : 7.0;
        double maxOffset = Config.KerbOffsetM(minWidth);
        double baseOffset = LaneBaseOffset(maxOffset);
        // A spawn lateral override pins the scalar line instead - the parking
        // scenarios must hold their initial line exactly.
        bool autoBase = _car.LaneOffsetOverrideM is null;

        // Pass 1 - provisional line WITHOUT the merge blend: the speed profile
        // below must belong to THIS route, because the merge length is speed
        // dependent (user rule) and planning it against the previous rebuild's
        // profile read a dead-end braking ramp as "entry speed" and stretched
        // the change to the 90 m cap.
        var sol = Raceline.SolveLine(_network, rounded, RouteSegments().ToList(),
                                     baseOffset: autoBase ? null : baseOffset,
                                     autoBase: autoBase);
        var lane = ApplyEndBlends(sol.Points, sol.Normals, sol.Offsets, sol.Cum,
                                  edgeOffset: maxOffset,
                                  pullingOver: pullingOver, pullingOut: pullingOut);
        if (_dest is not null)
        {
            // Red-flag destination: the line ENDS there - parking ramp, kerb
            // drift and stop all anchor to the truncated end.
            lane = CutPolylineAt(lane, _dest.Value.X, _dest.Value.Y,
                                 extendM: _parkStageM);
        }
        _ref = new RefLine(lane);
        if (RefLineMath.PARK_DEBUG)
            Console.WriteLine($"[REBUILD] key={key} old={_routeKey} " +
                              $"car=({_car.X:F0},{_car.Y:F0}) v={_car.Speed * 3.6:0} " +
                              $"phase={ParkPhase} merge={_mergeEpisode}");

        // --- Turn-signal lifecycle -------------------------------------
        // Re-arm the signal for a roundabout exit that just entered the
        // horizon: after the entry turn is executed, pending_turn is cleared
        // (see below), but the exit still needs its intent - without it
        // IntendedTurn() is 'straight', which disables the junction approach
        // cap at the exit node. Re-flicking for the exit is what a driver does
        // too.
        var d = car.Driver;
        if (_routeExitDir is not null && d is not null && d.PendingTurn is null)
        {
            d.SignalTurn(_routeExitDir);
            _turnSignalTarget = _routeExitNode;
            // Keep the key consistent with the re-armed intent, or every
            // following frame would see a "turn change" and rebuild.
            key = (car.SegIdx, _routeExitDir, pullingOver, pullingOut);
        }
        string? pending = d?.PendingTurn;
        // A fresh signal (API/keyboard, not the re-arm above): remember WHICH
        // junction it is for - the one ahead of the car now.
        if (pending is not null && _prevPendingTurn is null && _turnSignalTarget is null)
        {
            var seg = _network.Segments[car.SegIdx];
            _turnSignalTarget = car.Forward ? seg.EndNode : seg.StartNode;
        }
        // Auto-off: once the car has PASSED the junction the signal was for,
        // clear light + intent. (The old steering-cam version cleared on any
        // steering dip and killed multi-stage maneuvers mid-corner.)
        if (pending is not null)
        {
            if (_turnSignalTarget is not null && !route.Skip(1).Contains(_turnSignalTarget))
            {
                d!.ClearTurnSignal();
                _turnSignalTarget = null;
            }
        }
        else
        {
            _turnSignalTarget = null;
        }
        _prevPendingTurn = d?.PendingTurn;
        _routeKey = key;
        RouteSegSet = RouteSegments();
        _profile = BuildSpeedProfile();
        if (turn != "straight")
        {
            var lv = new System.Text.StringBuilder();
            for (int i = 0; i < _ref.Total; i += 10)
            {
                var (px, py) = _ref.PointAt(i);
                double kv = Math.Abs(_ref.CurvatureAt(Math.Min(_ref.Total, i)));
                lv.Append($"[{i}=({px:F0},{py:F0}) k={kv:F4} v={_profile[i]:F1}] ");
            }
            var fine = new System.Text.StringBuilder();
            for (double ss = 136.0; ss <= 145.0; ss += 0.5)
            {
                var (fx, fy) = _ref.PointAt(ss);
                double fkv = Math.Abs(_ref.CurvatureAt(ss));
                fine.Append($"[{ss:F1}=({fx:F2},{fy:F2}) k={fkv:F4}] ");
            }
            var rawv = new System.Text.StringBuilder();
            for (int ri = 0; ri < _ref.Cum.Count - 1; ri++)
                if (_ref.Cum[ri] >= 140.0 && _ref.Cum[ri] <= 146.0)
                    rawv.Append($"[{ri}]({_ref.Pts[ri].X:F2},{_ref.Pts[ri].Y:F2}) len={(_ref.Cum[ri + 1] - _ref.Cum[ri]):F3} ");
            Console.WriteLine($"[TBLINK] RAW {rawv}");
            Console.WriteLine($"[TBLINK] FINE {fine}");
            Console.WriteLine($"[TBLINK] LINE total={_ref.Total:F1} route=[{string.Join(",", _route)}] lastDeg={_network.NodeDegree.GetValueOrDefault(_route[^1])} dest={(_dest is null ? "null" : $"({_dest.Value.X:F0},{_dest.Value.Y:F0})")} {lv}");
        }

        // Pass 2 - the merge blend, planned against the FRESH profile. A
        // lateral blend does not change a straight road's curvature, so pass
        // 1's profile stays valid for the re-solved line.
        var merge = MergeParams(rounded, maxOffset);
        if (merge is not null)
        {
            var mv = merge.Value;
            sol = Raceline.SolveLine(_network, rounded, RouteSegments().ToList(),
                                     baseOffset: autoBase ? null : baseOffset,
                                     autoBase: autoBase,
                                     mergeFromM: mv.FromM,
                                     mergeS0: mv.S0, mergeS1: mv.S1);
            lane = ApplyEndBlends(sol.Points, sol.Normals, sol.Offsets, sol.Cum,
                                  edgeOffset: maxOffset,
                                  pullingOver: pullingOver, pullingOut: pullingOut);
            if (_dest is not null)
                lane = CutPolylineAt(lane, _dest.Value.X, _dest.Value.Y,
                                     extendM: _parkStageM);
            _ref = new RefLine(lane);
        }

        // Re-project the car onto the (new) reference line.
        S = RefLineMath.ProjectS(_ref!, car.X, car.Y, S);

        // Is the signalled turn actually still REACHABLE from here? The speed
        // profile already encodes that: its braking pass guarantees the profile
        // can be followed from any point where the car is at or below it. So if
        // we are ALREADY faster than the profile allows at our own position, no
        // amount of braking gets us round the corner - the turn is out of reach.
        // Without this the route was rebuilt to take the turn regardless: the
        // car could not physically make it, understeered across the junction and
        // left the road - it must instead SLIDE PAST and try again at the next
        // junction (the blinker stays on).
        if (turn != "straight" && _profile.Length > 0)
        {
            double vAllowed = TargetSpeed(S);
            if (car.Speed > vAllowed * REACHABLE_SPEED_TOLERANCE)
            {
                Console.WriteLine($"[TBLINK] UNREACHABLE: v={car.Speed * 3.6:F1} > vAllowed={vAllowed * 3.6:F1} * {REACHABLE_SPEED_TOLERANCE} at S={S:F1} -> straight past");
                RebuildStraightPast(pullingOver, pullingOut);
            }
        }
    }

    /// <summary>Re-plan through the upcoming junction WITHOUT the signalled
    /// turn. The blinker deliberately stays on: the intent is not cancelled,
    /// it is deferred to the next junction where the turn is reachable.</summary>
    private void RebuildStraightPast(bool pullingOver, bool pullingOut)
    {
        var car = _car;
        List<string> saved = _route;
        List<string> route = BuildRouteStraight();
        if (route.Count < 2 || route.SequenceEqual(saved)) return;
        _route = route;
        var raw = route.Select(n => _network.Nodes[n]).ToList();
        var rounded = RoadNetworkGeometry.RoundPolylineCorners(
            raw, CORNER_RADIUS_M * RefLineMath.PPPM,
            arcSteps: CORNER_ARC_STEPS, fitEdges: true);
        if (_dest is not null)
        {
            // Cut before solve_line - see MaybeRebuild.
            rounded = CutPolylineAt(rounded, _dest.Value.X, _dest.Value.Y,
                                    extendM: Math.Max(_parkStageM, CORRIDOR_RUNOUT_M));
        }
        double minWidth = RouteSegments().Count > 0
            ? RouteSegments().Min(i => _network.Segments[i].Width)
            : 7.0;
        var merge = MergeParams(rounded, Config.KerbOffsetM(minWidth));
        bool autoBase = _car.LaneOffsetOverrideM is null;
        var sol = Raceline.SolveLine(_network, rounded, RouteSegments().ToList(),
                                     baseOffset: autoBase ? null : LaneBaseOffset(Config.KerbOffsetM(minWidth)),
                                     autoBase: autoBase,
                                     mergeFromM: merge?.FromM,
                                     mergeS0: merge?.S0 ?? 0.0,
                                     mergeS1: merge?.S1 ?? 0.0);
        var lane = ApplyEndBlends(sol.Points, sol.Normals, sol.Offsets, sol.Cum,
                                  edgeOffset: Config.KerbOffsetM(minWidth),
                                  pullingOver: pullingOver, pullingOut: pullingOut);
        if (_dest is not null)
        {
            // Safety net: keep the endpoint exactly at the destination even
            // though the blend shifted the last metres laterally.
            lane = CutPolylineAt(lane, _dest.Value.X, _dest.Value.Y,
                                 extendM: _parkStageM);
            if (RefLineMath.PARK_DEBUG)
                Console.WriteLine($"[PARKDBG] ref.total={new RefLine(lane).Total:F1} " +
                                  $"(pre-cut {sol.Cum[^1]:F1})");
        }
        _ref = new RefLine(lane);
        RouteSegSet = RouteSegments();
        _profile = BuildSpeedProfile();
        S = RefLineMath.ProjectS(_ref, car.X, car.Y, S);
    }

    /// <summary>BuildRoute(), but taking the straight continuation at the
    /// first junction instead of the signalled turn.</summary>
    private List<string> BuildRouteStraight()
    {
        var net = _network;
        var car = _car;
        var seg = net.Segments[car.SegIdx];
        string junction = car.Forward ? seg.EndNode : seg.StartNode;
        string behind = car.Forward ? seg.StartNode : seg.EndNode;
        var route = new List<string> { behind };
        int curSeg = car.SegIdx;
        string curNode = junction;
        for (int hop = 0; hop <= HORIZON_SEGMENTS; hop++)
        {
            route.Add(curNode);
            int? nxt = hop == 0
                ? net.ChooseNextSegment(curSeg, curNode, "straight")
                : NextAfterFirst(curSeg, curNode, "straight");
            if (nxt is null || nxt == curSeg) break;
            var nseg = net.Segments[nxt.Value];
            curNode = nseg.StartNode == curNode ? nseg.EndNode : nseg.StartNode;
            curSeg = nxt.Value;
        }
        return route;
    }

    /// <summary>Arc length (m) of the point on P/cum closest to _dest. Used
    /// instead of cum[^1] whenever the polyline may have extra run-out past
    /// the destination (see CORRIDOR_RUNOUT_M): cum[^1] is then the end of
    /// that run-out, not the stop point.</summary>
    private double DestArcS(List<(double X, double Y)> P, double[] cum)
    {
        var (dx, dy) = _dest!.Value;
        int bestI = 0; double bestD2 = double.PositiveInfinity;
        for (int i = 0; i < P.Count; i++)
        {
            double d2 = (P[i].X - dx) * (P[i].X - dx) + (P[i].Y - dy) * (P[i].Y - dy);
            if (d2 < bestD2) { bestD2 = d2; bestI = i; }
        }
        return cum[bestI];
    }

    /// <summary>Lay the manoeuvre blends over the racing line and build the
    /// line. `offsets` is the fastest legal line from Raceline.SolveLine. Two
    /// manoeuvres override it near the route's ends, because they are about
    /// where the car STOPS or STARTS rather than how fast it can get round a
    /// bend:
    ///   pullingOver - drift to the kerb approaching the destination. The last
    ///       PARK_BLEND_END_M are a CONSTANT offset so the car comes to rest
    ///       parallel to the kerb, not at an angle.
    ///   pullingOut  - the mirror image, leaving the kerb at the start.
    /// Also records where the route passes through real junctions, which is
    /// the only place LaneGuard has to be suppressed (see InTurnBlendZone).</summary>
    private List<(double X, double Y)> ApplyEndBlends(
        List<(double X, double Y)> P, List<(double X, double Y)> N,
        double[] offsets, double[] cum,
        double edgeOffset = 0.0, bool pullingOver = false, bool pullingOut = false)
    {
        var offs = (double[])offsets.Clone();

        // Reverse-ONLY parking (user decision 2026-08-26): a reverse-in does NO
        // forward pull-over drift at all - the line keeps its normal lane offset
        // to the staging stop, so the car drives past the spot in its lane and
        // the reverse covers the full lateral distance to the kerb (the classic
        // back-in).
        if (pullingOver && edgeOffset > 0 && ParkStyle != "reverse")
        {
            // Everything below is measured from the STOP POINT, not from the
            // line's end: the car halts a car length short of a dead end (and
            // its front overhang short of a flag), so anchoring the drift
            // geometry at the line end put the whole "align parallel" stretch
            // BEYOND the place where the car stops. When the CENTRELINE was
            // given extra run-out past the flag, cum[^1] is NOT the stop point
            // any more - anchor to the actual destination position instead
            // whenever there is one.
            double sEnd = _dest is not null
                ? DestArcS(P, cum) - StopMargin()
                : cum[^1] - StopMargin();

            // Anchor the drift at the car's CURRENT position on this route.
            // Pulling-over activates when the driver starts braking for the dead
            // end - which can be well inside the nominal blend zone. Blending
            // from the nominal start point would then step the line laterally
            // under the car; pursuit overshoots such a step and clips the kerb.
            // The anchor is remembered once per pull-over episode, stored as a
            // WORLD POINT (not an arc length): the route drops segments behind
            // the car, so `s` is re-zeroed while pulling over.
            (double sCar, double oCar) AnchorHere()
            {
                int bestI = 0; double bestD2 = double.PositiveInfinity;
                for (int i = 0; i < P.Count; i++)
                {
                    double d2 = (P[i].X - _car.X) * (P[i].X - _car.X) +
                                (P[i].Y - _car.Y) * (P[i].Y - _car.Y);
                    if (d2 < bestD2) { bestD2 = d2; bestI = i; }
                }
                double s_ = cum[bestI];
                // P is in pixels, offsets are in metres - convert.
                double o_ = ((_car.X - P[bestI].X) * N[bestI].X +
                             (_car.Y - P[bestI].Y) * N[bestI].Y) / RefLineMath.PPPM;
                _pullOverAnchor = (_car.X, _car.Y, o_);
                return (s_, o_);
            }

            double sCar = 0.0, oCar = 0.0;
            bool anchored = false;
            if (_pullOverAnchor is not null)
            {
                var (ax, ay, oA) = _pullOverAnchor.Value;
                int bestI = 0; double bestD2 = double.PositiveInfinity;
                for (int i = 0; i < P.Count; i++)
                {
                    double d2 = (P[i].X - ax) * (P[i].X - ax) + (P[i].Y - ay) * (P[i].Y - ay);
                    if (d2 < bestD2) { bestD2 = d2; bestI = i; }
                }
                // Usable only if the anchor point is still ON this line and
                // still far enough from the end to blend over.
                if (bestD2 <= (5.0 * RefLineMath.PPPM) * (5.0 * RefLineMath.PPPM) &&
                    cum[bestI] <= sEnd - PARK_BLEND_END_M - 1.0)
                {
                    sCar = cum[bestI]; oCar = oA;
                    anchored = true;
                }
            }
            if (!anchored) (sCar, oCar) = AnchorHere();

            // The blend covers the plan's swerve zone: the last PARK_BLEND_START_M
            // before the stop point. The car's own position only anchors the
            // drift when it is ALREADY inside that zone - then starting anywhere
            // else would step the line laterally under the wheels. While the car
            // is still approaching, the drift must start from the line's OWN
            // offset at the zone entry.
            if (sCar <= sEnd - PARK_BLEND_START_M)
            {
                double sStart = sEnd - PARK_BLEND_START_M;
                int iStart = RefLine.BisectRight(cum.ToList(), sStart);
                iStart = Math.Max(0, Math.Min(offs.Length - 1, iStart));
                sCar = cum[iStart]; oCar = offs[iStart];
            }
            double dCar = Math.Max(PARK_BLEND_END_M + 1.0,
                                   Math.Min(PARK_BLEND_START_M, sEnd - sCar));

            // Park as close to the kerb as the remaining distance allows: the
            // smoothstep drift's peak slant is 1.5*|dlat|/drift_len and at
            // mid-drift the front wheel contact patch reaches
            // off + WHEELBASE*sin(th) + TIRE_OUTBOARD*cos(th). Search for the
            // largest target offset that keeps every WHEEL on the pavement -
            // only the wheels need to stay on the road; the body may overhang
            // the kerb. Half the road, minus what the manoeuvre must keep in
            // hand (the Stanley alignment ends within 0.06 m of its own line).
            double limitLat = edgeOffset + Config.CAR_WIDTH / 2.0 +
                              Config.KERB_CLEARANCE_M -
                              PARK_LINE_MARGIN_M - PARK_TRACKING_MARGIN_M;
            // Wheel contact patch, not body corner: rear axle -> front axle
            // along the body, TIRE_OUTBOARD_M outboard (the paint points). The
            // nose overhang beyond the front axle may swing past the kerb.
            double frontReach = WHEELBASE;
            double lateralReach = Config.TIRE_OUTBOARD_M;
            double driftLen = dCar - PARK_BLEND_END_M;

            double[] BlendOffs(double offTarget)
            {
                var outO = (double[])offs.Clone();
                for (int i = 0; i < cum.Length; i++)
                {
                    double d_ = sEnd - cum[i];
                    if (d_ <= PARK_BLEND_END_M) outO[i] = offTarget;
                    else if (d_ < dCar)
                    {
                        double t_ = (dCar - d_) / driftLen;
                        outO[i] = oCar + (offTarget - oCar) * RefLineMath.ParkEase(t_);
                    }
                }
                return outO;
            }

            double DriftWorst(double offTarget)
            {
                var cand = BlendOffs(offTarget);
                double worst = 0.0;
                // ONLY the stretch the blend actually changes. Scanning the whole
                // line made the corner the car had just driven veto the pull-over:
                // through a left turn the racing line legally swings wide, its
                // "reach" exceeds the kerb limit, and since that value does not
                // depend on the candidate offset, EVERY candidate was rejected.
                for (int i = 1; i < P.Count - 1; i++)
                {
                    if (cum[i] < sEnd - dCar) continue;
                    double dx_ = (P[i + 1].X + N[i + 1].X * cand[i + 1]) -
                                 (P[i - 1].X + N[i - 1].X * cand[i - 1]);
                    double dy_ = (P[i + 1].Y + N[i + 1].Y * cand[i + 1]) -
                                 (P[i - 1].Y + N[i - 1].Y * cand[i - 1]);
                    double thT = Math.Atan2(dx_, dy_);
                    double thA = Math.Atan2(P[i + 1].X - P[i - 1].X,
                                            P[i + 1].Y - P[i - 1].Y);
                    double slant = Math.Abs(thT - thA);
                    double reach = cand[i] + frontReach * Math.Sin(slant) +
                                   lateralReach * Math.Cos(slant);
                    worst = Math.Max(worst, reach);
                }
                // ...plus an analytic scan of the drift profile itself. The
                // geometric tangent above under-reads the slant whenever the
                // drift is shorter than the polyline's sampling window, so walk
                // the profile directly: at each point the kerb-side front corner
                // reaches offset + front_reach*sin(th) + (W/2)*cos(th).
                double dlat_ = Math.Abs(offTarget - oCar);
                if (driftLen > 0.0)
                {
                    int n_ = 40;
                    for (int k_ = 0; k_ <= n_; k_++)
                    {
                        double t_ = (double)k_ / n_;
                        double off_ = oCar + (offTarget - oCar) * RefLineMath.ParkEase(t_);
                        double th_ = Math.Atan(dlat_ * RefLineMath.ParkEaseSlope(t_) / driftLen);
                        worst = Math.Max(worst, off_ + frontReach * Math.Sin(th_) +
                                                  lateralReach * Math.Cos(th_));
                    }
                }
                return worst;
            }

            // Both feasibility constraints are monotone in the target offset
            // (bigger offset -> steeper slant AND bigger corner reach), so a
            // single downward search from the kerb finds the best park position
            // that satisfies both.
            double maxSlant = Math.Radians(MAX_PARK_DRIFT_SLANT_DEG);
            // Floor of the search: where the car already is - but never further
            // out than the kerb it is aiming for. On a road that NARROWS towards
            // the dead end the car's current offset can be larger than the target
            // kerb offset; without the clamp the floor kept the line at 1.85 m
            // and the car parked off the pavement.
            double oTarget = Math.Min(Math.Max(oCar, 0.0), edgeOffset);
            // The pull-over aims closer to the kerb than the driving line is ever
            // allowed to go: a parked car is not tracking anything. When the last
            // stretch is done in reverse (docs §1b), the forward swerve stops
            // short by exactly that tuck.
            double parkEdge = edgeOffset + (Config.KERB_CLEARANCE_M - Config.PARK_KERB_CLEARANCE_M);
            double candT = parkEdge;
            while (candT > oTarget + 1e-6)
            {
                double dlat_ = Math.Abs(candT - oCar);
                bool slantOk = driftLen <= 0.0 ||
                               Math.Atan(1.5 * dlat_ / driftLen) <= maxSlant;
                if (slantOk && DriftWorst(candT) <= limitLat)
                {
                    oTarget = candT;
                    break;
                }
                candT -= 0.05;
            }
            // Remember how close the FORWARD swerve can actually get: the
            // reverse-in planner needs exactly that number.
            _parkFwdTarget = oTarget;
            offs = BlendOffs(oTarget);
            if (RefLineMath.PARK_DEBUG)
                Console.WriteLine($"[PARKDBG] s_end={sEnd:F1} s_car={sCar:F1} " +
                                  $"o_car={oCar:F2} edge={edgeOffset:F2} d_car={dCar:F2} " +
                                  $"-> o_target={oTarget:F2}");
        }

        // Forget the drift anchor only when the whole parking episode is over.
        // Clearing it on every rebuild that happens to run with pullingOver=false
        // (the plan's 'lead' phase, or a re-plan past a junction) threw the anchor
        // away mid-manoeuvre, and the next rebuild re-anchored the drift at the
        // moving car - the drift then restarted under the wheels on every frame.
        if (!pullingOver && ParkPhase == "none") _pullOverAnchor = null;

        if (pullingOut && edgeOffset > 0)
        {
            for (int i = 0; i < cum.Length; i++)
            {
                double s = cum[i];
                if (s <= PULL_OUT_END_M) offs[i] = edgeOffset;
                else if (s < PULL_OUT_START_M)
                {
                    double t = (s - PULL_OUT_END_M) / (PULL_OUT_START_M - PULL_OUT_END_M);
                    t = t * t * (3.0 - 2.0 * t);
                    offs[i] = edgeOffset + (offs[i] - edgeOffset) * t;
                }
            }
        }

        _junctionZones = FindJunctionZones(P, cum);
        return Raceline.PointsFromOffsets(P, N, offs);
    }

    /// <summary>Arc-length spans where the route crosses a real (degree >= 3)
    /// junction. Inside a junction there is no meaningful centreline to stay
    /// right of, so the wrong-side check cannot apply there.</summary>
    private List<(double A, double B)> FindJunctionZones(
        List<(double X, double Y)> rounded, double[] cum)
    {
        var zones = new List<(double A, double B)>();
        foreach (string node in _route)
        {
            if (_network.NodeDegree.GetValueOrDefault(node) < 3) continue;
            if (!_network.Nodes.TryGetValue(node, out var nxy)) continue;
            int bestI = 0; double bestD2 = double.PositiveInfinity;
            for (int i = 0; i < rounded.Count; i++)
            {
                double d2 = (rounded[i].X - nxy.X) * (rounded[i].X - nxy.X) +
                            (rounded[i].Y - nxy.Y) * (rounded[i].Y - nxy.Y);
                if (d2 < bestD2) { bestD2 = d2; bestI = i; }
            }
            double s = cum[bestI];
            zones.Add((s - JUNCTION_SUPPRESS_M, s + JUNCTION_SUPPRESS_M));
        }
        return zones;
    }

    /// <summary>True where the wrong-side (LaneGuard) check must be suppressed:
    /// only inside a real junction, where there is no centreline to stay right
    /// of.</summary>
    public bool InTurnBlendZone(double s)
    {
        foreach (var (a, b) in _junctionZones)
            if (a <= s && s <= b) return true;
        return false;
    }

    /// <summary>Segment indices covered by the current node route.</summary>
    private HashSet<int> RouteSegments()
    {
        var net = _network;
        var idxs = new HashSet<int>();
        for (int i = 0; i + 1 < _route.Count; i++)
        {
            string a = _route[i], b = _route[i + 1];
            for (int j = 0; j < net.Segments.Count; j++)
            {
                var seg = net.Segments[j];
                if ((seg.StartNode == a && seg.EndNode == b) ||
                    (seg.StartNode == b && seg.EndNode == a))
                {
                    idxs.Add(j);
                    break;
                }
            }
        }
        return idxs;
    }

    /// <summary>v_max at each 1 m arc-length point, from curvature + a braking
    /// look-back constraint. The constraint is FORWARD reachability: the speed
    /// at point i must be low enough that the car can still reach the (possibly
    /// lower) speed at point i+1 - i.e. it can brake from v[i] down to v[i+1]
    /// within the distance d. Equivalently, v[i] may be at most
    /// sqrt(v[i+1]^2 + 2*a_brake*d) (the speed from which a full brake over d
    /// still lands at v[i+1]). This is what creates the braking ramp INTO a
    /// corner.</summary>
    private double[] BuildSpeedProfile()
    {
        var refLine = _ref!;
        int n = Math.Max(2, (int)refLine.Total + 1);
        double d = 1.0;
        var profile = new double[n];
        for (int i = 0; i < n; i++) profile[i] = _cruise;

        // Curvature cap: v = sqrt(a_lat_max / k), applied to the LOCAL curvature
        // at each point. Take the WORST curvature within this metre, not the
        // value at its left edge: sampling one point per metre stepped right over
        // fillet peaks and handed the car a corner speed its own lateral limit
        // forbids, so it understeered wide.
        for (int i = 0; i < n; i++)
        {
            double k = 0.0;
            for (int f = 0; f < 4; f++)
                k = Math.Max(k, Math.Abs(refLine.CurvatureAt(Math.Min(refLine.Total, i + f * 0.25))));
            if (k > 1e-4)
            {
                profile[i] = Math.Min(profile[i], Math.Sqrt(
                    A_LAT_MAX * A_LAT_PLAN_FRACTION / k));
            }
        }

        // Junction approach cap: a braking ramp into every real junction
        // (degree >= 3) on this line, arriving at JUNCTION_ENTRY_SPEED_M.
        // Only while a turn is actually SIGNALLED: a real driver cruises through
        // junctions they intend to go straight past and brakes only once they
        // signal; capping EVERY junction unconditionally made the car pulse
        // accelerate-brake-accelerate through dense town grids.
        var zones = IntendedTurn() != "straight" ? _junctionZones : new List<(double, double)>();
        if (zones.Count > 0)
        {
            for (int i = 0; i < n; i++)
            {
                double sI = i * d;
                foreach (var (za, zb) in zones)     // sorted by s; first ahead wins
                {
                    if (za > sI + 0.5)
                    {
                        profile[i] = Math.Min(profile[i], Math.Sqrt(
                            JUNCTION_ENTRY_SPEED_M * JUNCTION_ENTRY_SPEED_M +
                            2 * A_BRAKE * (za - sI)));
                        break;
                    }
                }
            }
        }

        // Forward reachability (braking) pass, from the end backward.
        for (int i = n - 2; i >= 0; i--)
        {
            double vNext = profile[i + 1];
            double vReach = Math.Sqrt(vNext * vNext + 2 * A_BRAKE * d);
            profile[i] = Math.Min(profile[i], vReach);
        }
        return profile;
    }

    private double TargetSpeed(double s)
    {
        int i = (int)Math.Max(0.0, Math.Min(_profile.Length - 1, s));
        return _profile[i];
    }
}
