namespace DrivingGame.Sim;

public sealed partial class BicycleNav
{
    // ====================================================================
    // Reverse-in parking (docs §1b)
    // ====================================================================

    public bool ReverseParkActive => _reversePark is not null;

    /// <summary>Body corner positions relative to the rear axle, (along, right)
    /// in metres, in the car's own frame.</summary>
    private (double Along, double Right)[] ParkCornerOffsets()
    {
        double front = WHEELBASE + (Config.CAR_LENGTH / 2.0 - Config.FRONT_AXLE_OFFSET_M);
        double rear = Config.CAR_LENGTH / 2.0 - Config.REAR_AXLE_OFFSET_M;
        double half = Config.CAR_WIDTH / 2.0;
        return new[] { (front, half), (front, -half), (-rear, half), (-rear, -half) };
    }

    /// <summary>Two-arc reverse path in ROAD-LOCAL coordinates.
    /// (u = along the direction of travel, v = to the car's right, psi = heading
    /// relative to the road axis, + = nose towards the kerb.) The car reverses
    /// on `delta` lock one way, then `delta` the other, and ends parallel to the
    /// road (psi = 0) exactly dv further towards the kerb. Returns (points, ok)
    /// with points = [(u, v, psi, steer)] and u &lt;= 0 throughout (the car moves
    /// backwards). Closed form for the swing angle: with R = 1/k the lateral gain
    /// of the pair of arcs is R*(cos(psi0) + 1 - 2*cos(psi0 - th)), so
    ///     th = psi0 + acos((cos(psi0) + 1 - dv/R) / 2).</summary>
    private (List<(double U, double V, double Psi, double Steer)> Pts, bool Ok)
        ReverseParkPath(double v0, double psi0, double dv, double ds = 0.02,
                        double? delta = null)
    {
        if (delta is null) delta = MAX_STEER;
        double k = Math.Tan(delta.Value) / WHEELBASE;               // 1/R
        double R = 1.0 / k;
        double arg = (Math.Cos(psi0) + 1.0 - dv / R) / 2.0;
        if (arg < -1.0 || arg > 1.0) return (new List<(double, double, double, double)>(), false);
        double th = psi0 + Math.Acos(arg);
        if (th <= 1e-4) return (new List<(double, double, double, double)>(), false);
        double psiMin = psi0 - th;
        var pts = new List<(double U, double V, double Psi, double Steer)>();
        double u = 0.0, v = v0, psi = psi0;
        pts.Add((u, v, psi, MAX_STEER));
        // Arc 1: reversing with the wheels on full RIGHT lock swings the rear
        // towards the kerb and the nose away from it (signed-speed bicycle
        // kinematics: dpsi/dt = (v/L) tan(delta), v < 0).
        while (psi > psiMin)
        {
            psi -= k * ds;
            u -= Math.Cos(psi) * ds;
            v -= Math.Sin(psi) * ds;
            pts.Add((u, v, psi, MAX_STEER));
        }
        // Arc 2: counter-lock, straightening back out to parallel.
        while (psi < 0.0)
        {
            psi += k * ds;
            u -= Math.Cos(psi) * ds;
            v -= Math.Sin(psi) * ds;
            pts.Add((u, v, psi, -MAX_STEER));
        }
        return (pts, true);
    }

    /// <summary>Every body corner of the whole manoeuvre stays on the pavement
    /// AND on our own half of the road (see PARK_CENTRELINE_MARGIN_M).</summary>
    private bool ReverseParkOk(
        List<(double U, double V, double Psi, double Steer)> pts, double width)
    {
        double kerbLim = width / 2.0 - PARK_REVERSE_KERB_MARGIN_M;
        double centreLim = -PARK_CENTRELINE_MARGIN_M;
        var corners = ParkCornerOffsets();
        foreach (var (u, v, psi, st) in pts)
        {
            foreach (var (lf, lr) in corners)
            {
                double lat = v + lf * Math.Sin(psi) + lr * Math.Cos(psi);
                if (lat > kerbLim || lat < centreLim) return false;
            }
        }
        return true;
    }

    /// <summary>How close to the kerb the FORWARD pull-over can get on a straight
    /// road of this width, starting from lane offset `oCar`. The same wheel-sweep
    /// rule ApplyEndBlends applies to the real line (only the wheels must stay on
    /// the pavement), evaluated analytically so the parking style can be decided
    /// long before the drift is built.</summary>
    public double ForwardDriftTarget(double width, double oCar)
    {
        double driftLen = Math.Max(0.1, PARK_BLEND_START_M - PARK_BLEND_END_M);
        double limitLat = width / 2.0 - PARK_LINE_MARGIN_M - PARK_TRACKING_MARGIN_M;
        double frontReach = WHEELBASE;
        double lateralReach = Config.TIRE_OUTBOARD_M;
        double cand = Config.ParkOffsetM(width);
        while (cand > oCar)
        {
            double dlat = Math.Abs(cand - oCar);
            double worst = 0.0;
            for (int k_ = 0; k_ <= 40; k_++)
            {
                double t_ = k_ / 40.0;
                double off_ = oCar + (cand - oCar) * RefLineMath.ParkEase(t_);
                double th_ = Math.Atan(dlat * RefLineMath.ParkEaseSlope(t_) / driftLen);
                worst = Math.Max(worst, off_ + frontReach * Math.Sin(th_) +
                                          lateralReach * Math.Cos(th_));
            }
            if (worst <= limitLat) return cand;
            cand -= 0.05;
        }
        return oCar;
    }

    /// <summary>How much of the pull-over is left for the reverse tuck.
    /// `oForward` is how close to the centreline the FORWARD swerve can get on
    /// this road (the pull-over search's own answer). The tuck is the rest of
    /// the way to the kerb - reduced, if need be, until the swept body corners
    /// stay on the pavement and off the oncoming lane. Returns (tuckM, stageM):
    /// the lateral distance the reverse covers, and how far PAST the parking
    /// spot the car stops first. null when reversing buys nothing here.
    /// Searches over arc steering as well as tuck: a gentler lock gives a deeper
    /// feasible tuck (less nose swing into the oncoming half) at the price of a
    /// longer back-in, so the deepest park wins regardless of which lock makes
    /// it legal.</summary>
    public (double TuckM, double StageM)? PlanReverseTuck(double width, double oForward)
    {
        double oPark = Config.ParkOffsetM(width);
        double start = Math.Max(0.0, oForward - PARK_PLAN_START_MARGIN_M);
        (double tuck, double stage)? best = null;
        foreach (double deltaDeg in REVERSE_STEER_CANDIDATE_DEGS)
        {
            double delta = Math.Radians(deltaDeg);
            double tuck = oPark - start;
            while (tuck >= PARK_REVERSE_MIN_TUCK_M - 1e-9)
            {
                var (pts, ok) = ReverseParkPath(oPark - tuck, 0.0, tuck, delta: delta);
                if (ok && ReverseParkOk(pts, width))
                {
                    // u is negative (backwards); add the straight run-out.
                    double stage = -pts[^1].U + PARK_REVERSE_TAIL_M;
                    if (best is null || tuck > best.Value.tuck + 1e-9)
                        best = (tuck, stage);
                    break;
                }
                tuck -= 0.05;
            }
        }
        return best;
    }

    /// <summary>Generate and arm the reverse-in tuck from the car's ACTUAL pose.
    /// The car is standing at the staging point, roughly parallel, a little short
    /// of the kerb. Solve the two-arc path from where it really is (residual
    /// lateral and heading error included) to flush at the kerb; if the full depth
    /// is not reachable from here, take the deepest one that is - parking a few
    /// centimetres further out is fine, clipping the kerb or straddling the
    /// centreline is not.</summary>
    private bool StartReversePark()
    {
        var car = _car;
        var seg = _network.Segments[car.SegIdx];
        double tx, ty;
        if (car.Forward) { tx = seg.X2 - seg.X1; ty = seg.Y2 - seg.Y1; }
        else { tx = seg.X1 - seg.X2; ty = seg.Y1 - seg.Y2; }
        double tl = Math.Hypot(tx, ty);
        if (tl < 1e-6) return false;
        tx /= tl; ty /= tl;
        double rx = ty, ry = -tx;
        double roadH = Math.PosDeg(Math.Degrees(Math.Atan2(tx, ty)));
        double psi0 = Math.Radians(Math.WrapDeg(car.Heading - roadH));
        if (Math.Abs(psi0) > Math.Radians(20.0)) return false;
        // Car position in the road frame (origin: the centreline point abeam of
        // the car).
        double v0 = ((car.X - seg.X1) * rx + (car.Y - seg.Y1) * ry) / RefLineMath.PPPM;
        double oPark = Config.ParkOffsetM(seg.Width);
        double dvFull = oPark - v0;
        if (dvFull < PARK_REVERSE_MIN_TUCK_M) return false;
        // How far back the car has to travel in total: exactly the distance it
        // was staged past its parking spot, so the front bumper ends up on the
        // flag again.
        double stage = _parkStageM;
        // Search tuck AND arc steering: from the full lane offset a deep tuck is
        // only legal with a gentler lock (less nose swing into the oncoming half).
        // The deepest feasible park wins; among equals the sharpest (shortest)
        // back-in is kept.
        (double dvTry, List<(double U, double V, double Psi, double Steer)> pts)? best = null;
        foreach (double deltaDeg in REVERSE_STEER_CANDIDATE_DEGS)
        {
            double delta = Math.Radians(deltaDeg);
            double dvT = oPark - v0;
            while (dvT >= PARK_REVERSE_MIN_TUCK_M - 1e-9)
            {
                var (cand, ok) = ReverseParkPath(v0, psi0, dvT, delta: delta);
                // The arcs must leave room for a straight run-out: the follower
                // arrives at the end of an arc still rotating (feed-forward assumes
                // perfect tracking), and without a straight stretch to settle on,
                // the car stopped 2.5 deg nose-in.
                if (ok && ReverseParkOk(cand, seg.Width) &&
                    -cand[^1].U <= stage - PARK_REVERSE_MIN_TAIL_M)
                {
                    if (best is null || dvT > best.Value.dvTry + 1e-9)
                        best = (dvT, cand);
                    break;
                }
                dvT -= 0.05;
            }
        }
        double dv;
        List<(double U, double V, double Psi, double Steer)> pts;
        if (best is not null)
        {
            dv = best.Value.dvTry;
            pts = best.Value.pts;
        }
        else
        {
            pts = new List<(double, double, double, double)>();
            // No feasible tuck from here after all. Still back up: the car is
            // standing PAST its destination (it drove there to stage the
            // manoeuvre), so reversing straight to the spot is the only way to
            // end up at the flag. It parks where the forward pull-over left it
            // laterally.
            pts.Add((0.0, v0, psi0, 0.0));
            dv = 0.0;
            Console.WriteLine("↩️  reverse tuck not feasible from here - backing " +
                              "straight up to the destination");
        }
        // Straight run-out: fills the remaining staging distance, and gives
        // heading and cross-track error somewhere to converge.
        double uEnd = pts[^1].U, vEnd = pts[^1].V;
        double tail = Math.Max(PARK_REVERSE_TAIL_M, stage + uEnd);   // u_end < 0
        int nTail = Math.Max(1, (int)(tail / 0.02));
        for (int i_ = 1; i_ <= nTail; i_++)
            pts.Add((uEnd - i_ * tail / nTail, vEnd, 0.0, 0.0));
        // To world pixels. The path is anchored at the car's rear axle, so the
        // line starts exactly under the car - no lateral step to absorb.
        double p0x = car.X, p0y = car.Y;
        var line = pts.Select(p => (p0x + (tx * p.U + rx * (p.V - v0)) * RefLineMath.PPPM,
                                    p0y + (ty * p.U + ry * (p.V - v0)) * RefLineMath.PPPM))
                      .ToList();
        var refLine = new RefLine(line);
        _ref = refLine;
        _reversePark = new ReverseParkState
        {
            Steer = pts.Select(p => p.Steer).ToArray(),
            Psi = pts.Select(p => p.Psi).ToArray(),
            RoadH = roadH,
            VTarget = oPark - (oPark - v0 - dv),   // reached kerb offset
            OPark = oPark,
            Tx = tx, Ty = ty, Rx = rx, Ry = ry,
            Seg = car.SegIdx,
        };
        S = 0.0;
        Console.WriteLine($"\n↩️  REVERSE-IN parking: {dv:F2} m tuck over " +
                          $"{refLine.Total:F2} m of reverse, target {oPark:F2} m from the " +
                          "centreline\n");
        return true;
    }

    /// <summary>Per-frame execution of the reverse-in tuck (replaces the normal
    /// update body while it runs). Steering is a pure FEEDBACK parking law -
    /// steer from (heading error, offset error) only, the way a driver does it.
    /// The planned two-arc path is used for feasibility and staging distance,
    /// not for steering: following its full-lock feed-forward with correction
    /// loops oscillated on this tight manoeuvre. State (road frame of the parking
    /// segment): e_off = rear-axle offset - o_park (+ = too far towards kerb);
    /// psi = nose relative to road axis (+ = nose towards kerb). Reversing
    /// kinematics (v &lt; 0): a right steer (delta &gt; 0) rotates the nose AWAY
    /// from the kerb, which swings the rear TOWARDS it - hence
    /// delta = +PSI_GAIN*psi - POS_GAIN*e_off.</summary>
    private void UpdateReversePark(double dt)
    {
        var car = _car;
        var refLine = _ref!;
        var st = _reversePark!;
        S = RefLineMath.ProjectS(refLine, car.X, car.Y, S,
                                 window: 1.0, globalFallback: false, refine: true);
        double dRem = Math.Max(0.0, refLine.Total - S);

        // --- state ---
        double psiCar = Math.Radians(Math.WrapDeg(car.Heading - st.RoadH));
        var seg = _network.Segments[st.Seg];
        double vCar = ((car.X - seg.X1) * st.Rx + (car.Y - seg.Y1) * st.Ry) / RefLineMath.PPPM;
        double eOff = vCar - st.OPark;
        bool converged = Math.Abs(eOff) < 0.06 && Math.Abs(psiCar) < Math.Radians(1.5);

        // --- longitudinal: creep backwards; the progressive roll-out (dRem/tau)
        // brakes to a stop exactly at the end of the line. Do NOT stop on
        // `converged` alone: with a shallow tuck the pose converges at the END
        // OF THE ARC, and stopping there skips the straight run-out - which is
        // exactly the stretch that carries the nose to the flag (measured: 3.7 m
        // short). The run-out exists so heading/cross-track error has somewhere
        // to converge; drive it.
        double vTarget;
        if (dRem <= 0.05) vTarget = 0.0;
        else vTarget = -Math.Min(PARK_REVERSE_CREEP_M_S, dRem / PARK_STOP_TAU);
        double brakeRate = A_PARK;
        if (Math.Abs(car.Speed) < PARK_ROLL_END_M_S)
            brakeRate = Math.Min(brakeRate, PARK_ROLL_END_A);
        if (car.Speed > vTarget + 0.02)
            car.Speed = Math.Max(vTarget, car.Speed - brakeRate * dt);
        else if (car.Speed < vTarget - 0.02)
            car.Speed = Math.Min(vTarget, car.Speed + A_CRUISE * dt);
        car.IsBraking = false;
        car.TargetSpeed = car.Speed;

        // --- steering: feedback parking law + envelope guard ---
        double delta = PARK_REVERSE_PSI_GAIN * psiCar - PARK_REVERSE_POS_GAIN * eOff;
        delta = Math.Max(-MAX_STEER, Math.Min(MAX_STEER, delta));
        // The feedback trajectory is NOT the checked two-arc path, so enforce
        // the SAME envelope it was validated against (body corners on the
        // pavement and off the oncoming half) with a one-step look-ahead: if
        // integrating this delta for dt would push any corner outside, ease off
        // the wheel until it won't - exactly what a driver does when the nose
        // swings too close to oncoming traffic.
        if (delta != 0.0 && car.Speed < -1e-3)
        {
            double kerbLim = seg.Width / 2.0 - PARK_REVERSE_KERB_MARGIN_M;
            double centreLim = -PARK_CENTRELINE_MARGIN_M;
            var corners = ParkCornerOffsets();
            bool Inside(double d)
            {
                double rate = (car.Speed / WHEELBASE) * Math.Tan(d);
                double psiN = psiCar + rate * dt;
                double vN = vCar + Math.Sin(psiCar) * car.Speed * dt;
                foreach (var (lf, lr) in corners)
                {
                    double lat = vN + lf * Math.Sin(psiN) + lr * Math.Cos(psiN);
                    if (lat > kerbLim || lat < centreLim) return false;
                }
                return true;
            }
            foreach (double f in new[] { 1.0, 0.75, 0.5, 0.35, 0.2, 0.0 })
            {
                if (Inside(delta * f)) { delta *= f; break; }
            }
        }
        car.SteerAngle = delta;

        // --- bicycle kinematics (signed speed) ---
        // The rotation rate is proportional to speed by construction
        // (rate = v/L * tan(delta)): a slowing car rotates slower, and at a
        // standstill it does not rotate at all - there is no in-place turn to
        // guard against.
        double maxRate = A_LAT_MAX / Math.Max(Math.Abs(car.Speed), 0.1);
        double desiredRate = (car.Speed / WHEELBASE) * Math.Tan(delta);
        double rate = Math.Max(-maxRate, Math.Min(maxRate, desiredRate));
        car.Heading = Math.PosDeg(car.Heading + Math.Degrees(rate) * dt);
        double rad = Math.Radians(car.Heading);
        car.X += Math.Sin(rad) * car.Speed * dt * RefLineMath.PPPM;
        car.Y += Math.Cos(rad) * car.Speed * dt * RefLineMath.PPPM;
        SyncSegment();

        ParkPhase = "reverse";
        if (RefLineMath.PARK_DEBUG)
        {
            // Also watch the swept body corners: the feedback trajectory is NOT
            // the checked path, so verify it stays inside the same envelope.
            double kerbLim = seg.Width / 2.0 - PARK_REVERSE_KERB_MARGIN_M;
            double centreLim = -PARK_CENTRELINE_MARGIN_M;
            var corners = ParkCornerOffsets();
            double worstK = double.PositiveInfinity, worstO = double.NegativeInfinity;
            foreach (var (lf, lr) in corners)
            {
                double lat = vCar + lf * Math.Sin(psiCar) + lr * Math.Cos(psiCar);
                worstK = Math.Min(worstK, lat);
                worstO = Math.Max(worstO, lat);
            }
            if (worstK < centreLim || worstO > kerbLim)
                Console.WriteLine($"[RVWARN] corner out of envelope: [{worstK:F2}, {worstO:F2}] " +
                                  $"lims [{centreLim:F2}, {kerbLim:F2}]");
        }
        if ((converged || dRem <= 0.05) && Math.Abs(car.Speed) < PARK_STANDSTILL_M_S)
        {
            car.Speed = 0.0;
            _reversePark = null;
            Parked = true;
            ParkPhase = "stopped";
            Console.WriteLine("✅ Reverse-in parking complete - standing at the kerb\n");
        }
    }

    // ====================================================================
    // U-turn (Wenden) - docs/DRIVING_MANEUVERS.md §5
    // ====================================================================

    public bool UturnActive => _uturnActive;

    /// <summary>Generate the U-turn reference line from the car's current state.
    /// Returns (ptsPx, stopsS, reverseZone, kappaPts, hdgPts) or null when the
    /// maneuver cannot be executed safely here. NEVER returns a line whose body
    /// corners would leave the pavement - an infeasible U-turn is aborted, not
    /// driven (the hard rules apply to the car, so they apply to the planned
    /// line with a small margin on top). Local frame: t_hat = direction of travel
    /// along the segment, r_hat = its right side. The road is treated as a
    /// straight strip of width seg.Width centred on the segment.</summary>
    /// <summary>Why the last U-turn request was rejected (diagnostics / HUD).</summary>
    public string UturnRejectReason { get; private set; } = "";

    private (List<(double X, double Y)> PtsPx, List<double> StopsS,
             (double A, double B)? RevZone, double[] Kappa, double[] Hdg)? GenerateUturn()
    {
        var net = _network;
        var car = _car;
        var seg = net.Segments[car.SegIdx];
        double tx, ty;
        if (car.Forward) { tx = seg.X2 - seg.X1; ty = seg.Y2 - seg.Y1; }
        else { tx = seg.X1 - seg.X2; ty = seg.Y1 - seg.Y2; }
        double tl = Math.Hypot(tx, ty);
        if (tl < 1e-6) { UturnRejectReason = "degenerate segment"; return null; }
        tx /= tl; ty /= tl;
        double rx = ty, ry = -tx;                      // right vector (t rotated 90 deg cw)
        double hTravel = Math.Atan2(tx, ty);           // heading along the travel dir
        // The car must be roughly aligned with the road to turn around on it.
        double hdgErr = Math.Abs(Math.WrapDeg(car.Heading - Math.Degrees(hTravel)));
        if (hdgErr > 45.0) { UturnRejectReason = $"misaligned ({hdgErr:F0} deg off the road)"; return null; }
        double width = seg.Width;
        double c = Config.KerbOffsetM(width);          // kerb position of the car CENTRE
        double lane0 = Math.Min(LANE_OFFSET_M, c);
        // Local origin: the point on the CENTERLINE closest to the car, so that
        // pt(lat, along)'s lat is measured from the centerline (the car itself
        // sits at lat = lat0, which is NOT zero). Work in metres, convert at end.
        double p0x = car.X / RefLineMath.PPPM, p0y = car.Y / RefLineMath.PPPM;
        double lat0 = ((car.X - seg.X1) * rx + (car.Y - seg.Y1) * ry) / RefLineMath.PPPM;
        p0x -= rx * lat0;
        p0y -= ry * lat0;

        (double X, double Y) Pt(double lat, double along) =>
            (p0x + tx * along + rx * lat, p0y + ty * along + ry * lat);

        // Body corners relative to the rear axle (metres) - same geometry as the
        // sprite / on-road box (config axle offsets).
        double frontBumper = WHEELBASE + (Config.CAR_LENGTH / 2.0 - Config.FRONT_AXLE_OFFSET_M);
        double rearBumper = Config.CAR_LENGTH / 2.0 - Config.REAR_AXLE_OFFSET_M;
        double halfWid = Config.CAR_WIDTH / 2.0;

        bool CornersOk(double x, double y, double h)
        {
            double fx = Math.Sin(h), fy = Math.Cos(h);
            double rxn = Math.Cos(h), ryn = -Math.Sin(h);
            foreach (double lf in new[] { frontBumper, -rearBumper })
                foreach (double lr in new[] { halfWid, -halfWid })
                {
                    double cx_ = x + fx * lf + rxn * lr;
                    double cy_ = y + fy * lf + ryn * lr;
                    // Lateral distance from the segment CENTERLINE (not from P0,
                    // which itself sits off-center on the kerb side).
                    double lat = ((cx_ * RefLineMath.PPPM - seg.X1) * rx +
                                  (cy_ * RefLineMath.PPPM - seg.Y1) * ry) / RefLineMath.PPPM;
                    if (Math.Abs(lat) > width / 2.0 - UTURN_LINE_MARGIN_M) return false;
                }
            return true;
        }

        bool single = width >= UTURN_SINGLE_SWING_MIN_WIDTH_M;
        double k = Math.Tan(MAX_STEER) / WHEELBASE;    // rad per metre
        const double arcDs = 0.025;

        double WorstCornerLat(List<(double X, double Y, double H)> linePts)
        {
            double worst = 0.0;
            foreach (var (px_, py_, ph) in linePts)
            {
                double fx = Math.Sin(ph), fy = Math.Cos(ph);
                double rxn = Math.Cos(ph), ryn = -Math.Sin(ph);
                foreach (double lf in new[] { frontBumper, -rearBumper })
                    foreach (double lr in new[] { halfWid, -halfWid })
                    {
                        double cx_ = px_ + fx * lf + rxn * lr;
                        double cy_ = py_ + fy * lf + ryn * lr;
                        double lat = ((cx_ * RefLineMath.PPPM - seg.X1) * rx +
                                      (cy_ * RefLineMath.PPPM - seg.Y1) * ry) / RefLineMath.PPPM;
                        worst = Math.Max(worst, Math.Abs(lat));
                    }
            }
            return worst;
        }

        // Step-1 lateral blend to s0, the swing arc(s), and the tail. Returns
        // (pts, stopIdx, reverseZone, n1, nT). The blends carry their TRUE
        // tangent heading: a car on a slanted path points along the tangent, and
        // its front corner swings out further than any nominal-heading check
        // would see - planning with nominal headings silently produces lines
        // that clip the kerb.
        (List<(double X, double Y, double H)> Pts, List<int> StopIdx,
         (int A, int B)? RevZone, int N1, int NT) BuildLine(double s0)
        {
            double l1 = UTURN_STEP1_LEN_M;
            double dlat = s0 - lat0;
            bool blend = Math.Abs(dlat) >= 0.3;
            int n1 = (int)(l1 / 0.25);
            var line = new List<(double X, double Y, double H)>();
            var stopIdx = new List<int>();
            for (int i = 0; i <= n1; i++)
            {
                double d = i * 0.25;
                double t = blend ? Math.Min(1.0, d / l1) : 0.0;
                double sm = t * t * (3.0 - 2.0 * t);             // smoothstep
                var p = Pt(lat0 + dlat * sm, d);
                line.Add((p.X, p.Y, hTravel));
            }
            var (x, y) = Pt(s0, l1);
            double h = hTravel;
            if (!single) stopIdx.Add(line.Count - 1);   // spec 5b step 1: stop at kerb

            (double X, double Y, double H) ArcForward(double x_, double y_, double h_, double dhTarget)
            {
                double travelled = 0.0;
                while (travelled < dhTarget / k - 1e-9)
                {
                    h_ -= k * arcDs;                     // left turn: heading decreases
                    x_ += Math.Sin(h_) * arcDs;
                    y_ += Math.Cos(h_) * arcDs;
                    travelled += arcDs;
                    line.Add((x_, y_, h_));
                }
                return (x_, y_, h_);
            }

            (double X, double Y, double H) ArcReverse(double x_, double y_, double h_,
                                                      double dhTotalTarget, double dhTotalNow)
            {
                while (dhTotalNow < dhTotalTarget - 1e-9)
                {
                    double dh = k * arcDs;
                    // reverse + right steer keeps rotating LEFT (signed-v bicycle
                    // kinematics: dh/ds_path = sign(v) * tan(d)/L)
                    h_ -= dh;
                    x_ -= Math.Sin(h_) * arcDs;          // moving BACKWARD
                    y_ -= Math.Cos(h_) * arcDs;
                    dhTotalNow += dh;
                    line.Add((x_, y_, h_));
                }
                return (x_, y_, h_);
            }

            (int A, int B)? revZone = null;
            if (single)
            {
                // §5a: one continuous full-left arc across the road to 180 deg.
                (x, y, h) = ArcForward(x, y, h, Math.PI);
            }
            else
            {
                // §5b three-point: F(60) stop, R(to 90 total) stop, F(to 180).
                double th2 = Math.Radians(UTURN_TH2_DEG);
                double th3 = Math.Radians(UTURN_TH3_DEG);
                (x, y, h) = ArcForward(x, y, h, th2);
                int sA2 = line.Count - 1;
                stopIdx.Add(sA2);
                (x, y, h) = ArcReverse(x, y, h, th3, th2);
                int sA3 = line.Count - 1;
                stopIdx.Add(sA3);
                (x, y, h) = ArcForward(x, y, h, Math.PI - th3);
                revZone = (sA2, sA3);
            }

            // --- tail: straight out in the NEW direction, blend to lane offset ---
            double fx = Math.Sin(h), fy = Math.Cos(h);     // new travel direction
            double rnx = fy, rny = -fx;                    // new right vector
            double latNow = (x - p0x) * rnx + (y - p0y) * rny;
            int nT = (int)(UTURN_TAIL_LEN_M / 0.25);
            for (int i = 1; i <= nT; i++)
            {
                double d = i * 0.25;
                double t = Math.Min(1.0, d / 6.0);
                double sm = t * t * (3.0 - 2.0 * t);
                double lat = latNow + (lane0 - latNow) * sm;
                // A4 already sits at lateral lat_now; the offset from A4 is
                // (lat - lat_now), not lat.
                line.Add((x + fx * d + rnx * (lat - latNow),
                          y + fy * d + rny * (lat - latNow), h));
            }

            // True-tangent headings on the blends (see above).
            for (int i_ = 0; i_ < line.Count - 1; i_++)
            {
                if (i_ <= n1 || i_ >= line.Count - nT)
                {
                    double dx_ = line[i_ + 1].X - line[i_].X;
                    double dy_ = line[i_ + 1].Y - line[i_].Y;
                    line[i_] = (line[i_].X, line[i_].Y, Math.Atan2(dx_, dy_));
                }
            }
            return (line, stopIdx, revZone, n1, nT);
        }

        List<(double X, double Y, double H)> pts;
        List<int> stopIdx;
        (int A, int B)? reverseZone;
        int n1, nT;
        if (single)
        {
            // §5a: the 180 deg swing shifts the car exactly 2R laterally, so
            // starting the arc too far out clips the approach blend while
            // starting it too far in clips the tail blend. Search for the
            // arc-start lateral position that maximizes the minimum corner
            // clearance - an honest plan keeps margin, a line whose corners hug
            // the kerb WILL be clipped by the follower.
            double limitLat = width / 2.0 - UTURN_LINE_MARGIN_M - UTURN_BLEND_TRACKING_MARGIN_M;
            (double worst, List<(double X, double Y, double H)> cand, int cn1, int cnt)? best = null;
            var bounds = new[] { lat0, c };
            double s0Lo = Math.Min(bounds[0], bounds[1]), s0Hi = Math.Max(bounds[0], bounds[1]);
            const int nCand = 15;
            for (int j = 0; j <= nCand; j++)
            {
                double s0 = s0Lo + (s0Hi - s0Lo) * (j / (double)nCand);
                var (cand, _si, _rz, cn1, cnt) = BuildLine(s0);
                double w_ = WorstCornerLat(cand);
                if (w_ <= limitLat && (best is null || w_ < best.Value.worst))
                    best = (w_, cand, cn1, cnt);
            }
            if (best is null)
            {
                UturnRejectReason = $"no single-swing line fits: width={width:F1} m, " +
                                    $"lat0={lat0:F2}, kerb c={c:F2}, limit {limitLat:F2} m";
                return null;
            }
            pts = best.Value.cand; stopIdx = new List<int>(); reverseZone = null;
            n1 = best.Value.cn1; nT = best.Value.cnt;
        }
        else
        {
            // §5b: the swing must start at the kerb (spec 5b).
            (pts, stopIdx, reverseZone, n1, nT) = BuildLine(c);
        }

        // --- longitudinal room: the arcs extend ahead of P0 (original travel
        // direction), the tail extends behind it. Both must fit the ROAD, which
        // continues straight through degree-2 nodes (a corner or junction ends
        // the usable strip - the line is planned as a straight road).
        double RoomAlong(bool ahead)
        {
            double dxs = seg.X2 - seg.X1, dys = seg.Y2 - seg.Y1;
            double l2 = dxs * dxs + dys * dys;
            double tCar = l2 > 1e-9
                ? Math.Clamp(((car.X - seg.X1) * dxs + (car.Y - seg.Y1) * dys) / l2, 0.0, 1.0)
                : 0.5;
            double dist = (ahead ? 1.0 - tCar : tCar) * seg.Length;
            string node = ahead ? seg.EndNode : seg.StartNode;
            int cameFromId = seg.Id;   // NOTE: conns holds LIST INDICES, not Ids
            for (int hops = 0; hops < 8; hops++)
            {
                if (!net.NodeConnections.TryGetValue(node, out var conns) || conns.Count != 2)
                    break;   // dead end or junction: the strip ends here
                int nextIdx = -1;
                foreach (int ci in conns)
                    if (net.Segments[ci].Id != cameFromId) { nextIdx = ci; break; }
                if (nextIdx < 0) break;
                var next = net.Segments[nextIdx];
                bool storedAway = next.StartNode == node;
                double nx = storedAway ? next.X2 - next.X1 : next.X1 - next.X2;
                double ny = storedAway ? next.Y2 - next.Y1 : next.Y1 - next.Y2;
                double nl = Math.Hypot(nx, ny);
                if (nl < 1e-9) break;
                nx /= nl; ny /= nl;
                // Only a straight-on continuation extends the strip.
                if ((nx * tx + ny * ty) * (ahead ? 1.0 : -1.0) < 0.9) break;
                // A one-way against our direction of travel ends the usable road.
                double sl = Math.Hypot(next.X2 - next.X1, next.Y2 - next.Y1);
                if (sl > 1e-9)
                {
                    double sx = (next.X2 - next.X1) / sl, sy = (next.Y2 - next.Y1) / sl;
                    bool withStored = sx * tx * (ahead ? 1.0 : -1.0) +
                                      sy * ty * (ahead ? 1.0 : -1.0) > 0.0;
                    if (next.Oneway && !withStored) break;
                }
                dist += next.Length;
                node = storedAway ? next.EndNode : next.StartNode;
                cameFromId = next.Id;
            }
            return dist;
        }
        double roomAhead = RoomAlong(true);
        double roomBehind = RoomAlong(false);
        var alongs = pts.Select(p => (p.X - p0x) * tx + (p.Y - p0y) * ty).ToList();
        if (alongs.Max() + 1.0 > roomAhead)
        {
            UturnRejectReason = $"not enough road ahead ({roomAhead:F0} m < {alongs.Max() + 1.0:F0} m needed)";
            return null;
        }
        if (-alongs.Min() + CAR_LENGTH_M > roomBehind)
        {
            UturnRejectReason = $"not enough road behind ({roomBehind:F0} m < {-alongs.Min() + CAR_LENGTH_M:F0} m needed)";
            return null;
        }

        // --- hard-rule check: every body corner of the whole line must stay on
        // the pavement (with a small planning margin on top).
        foreach (var (px_, py_, ph) in pts)
            if (!CornersOk(px_, py_, ph))
            {
                UturnRejectReason = $"body corner leaves the pavement (width={width:F1} m)";
                return null;
            }

        // Per-point curvature (rad/m along the traversal). The polyline's spatial
        // direction flips 180 deg at every cusp, so RefLine's windowed curvature
        // reads garbage there - but we generated this line and know its exact
        // shape: -k on every arc (heading decreases), 0 elsewhere.
        var kappaPts = new double[pts.Count];
        int sA2 = reverseZone?.A ?? 0, sA3 = reverseZone?.B ?? 0;
        if (single)
        {
            for (int i = n1 + 1; i <= pts.Count - nT - 1; i++) kappaPts[i] = -k;
        }
        else
        {
            foreach (var (lo, hi) in new[] { (n1 + 1, sA2), (sA2 + 1, sA3), (sA3 + 1, pts.Count - nT - 1) })
                for (int i = lo; i <= hi; i++) kappaPts[i] = -k;
        }

        // Required NOSE heading at every point: the polyline's true tangent, plus
        // 180 deg on the reverse branch (there the nose points AWAY from the
        // direction of travel). Recomputed from the geometry so that the lateral
        // blends (step 1, tail) carry their real tangent.
        var hdgPts = new double[pts.Count];
        for (int i_ = 0; i_ < pts.Count - 1; i_++)
        {
            double dx_ = pts[i_ + 1].X - pts[i_].X;
            double dy_ = pts[i_ + 1].Y - pts[i_].Y;
            hdgPts[i_] = Math.Atan2(dx_, dy_);
        }
        hdgPts[^1] = hdgPts[^2];
        if (!single)
            for (int i_ = sA2 + 1; i_ <= sA3; i_++) hdgPts[i_] += Math.PI;

        var ptsPx = pts.Select(p => (p.X * RefLineMath.PPPM, p.Y * RefLineMath.PPPM)).ToList();
        var refLine = new RefLine(ptsPx);
        // Map the stop point indices to arc lengths (metres).
        var stopsS = stopIdx.Select(i => refLine.Cum[i]).ToList();
        (double A, double B)? revZoneS = reverseZone is null
            ? null : (refLine.Cum[reverseZone.Value.A], refLine.Cum[reverseZone.Value.B]);
        return (ptsPx, stopsS, revZoneS, kappaPts, hdgPts);
    }

    /// <summary>SIGNED v_max per metre for the U-turn line. Negative on the
    /// reverse step. The cap is UTURN_SPEED_MAX on the maneuver itself (the spec's
    /// 5-10 km/h), ramping up along the tail by the car's own acceleration; the
    /// curvature cap and the forward braking-reachability pass are the same as
    /// for normal routes.</summary>
    private double[] BuildUturnProfile(RefLine refLine, (double A, double B)? reverseZone)
    {
        int n = Math.Max(2, (int)refLine.Total + 1);
        double sTailStart = refLine.Total - UTURN_TAIL_LEN_M;
        var prof = new double[n];
        for (int i = 0; i < n; i++)
        {
            double v = Math.Min(V_MAX, Math.Sqrt(
                UTURN_SPEED_MAX * UTURN_SPEED_MAX +
                2 * A_CRUISE * Math.Max(0.0, i - sTailStart)));
            double k_ = Math.Abs(refLine.CurvatureAt(Math.Min(refLine.Total, i + 0.5)));
            if (k_ > 1e-4)
                v = Math.Min(v, Math.Sqrt(A_LAT_MAX * A_LAT_PLAN_FRACTION / k_));
            prof[i] = v;
        }
        for (int i = n - 2; i >= 0; i--)
            prof[i] = Math.Min(prof[i], Math.Sqrt(prof[i + 1] * prof[i + 1] + 2 * A_BRAKE));
        if (reverseZone is not null)
        {
            // Index boundaries: negative EXACTLY between the stop points (int()
            // would start up to 1 m early and end up to 1 m late, which flips the
            // car's direction before/after the cusps).
            int a = Math.Max(0, (int)Math.Ceiling(reverseZone.Value.A));
            int b = Math.Min(n, (int)Math.Floor(reverseZone.Value.B) + 1);
            for (int i = a; i < b; i++) prof[i] = -Math.Abs(prof[i]);
        }
        return prof;
    }

    private bool StartUturn()
    {
        if (Math.Abs(_car.Speed) > UTURN_MAX_ENTRY_SPEED_M)
        {
            Console.WriteLine($"\n⚠️  U-turn requested but the car is moving too fast " +
                              $"({_car.Speed * 3.6:0} km/h) - brake first, then retry\n");
            return false;
        }
        var gen = GenerateUturn();
        if (gen is null)
        {
            Console.WriteLine($"\n⚠️  U-turn requested but not feasible here: " +
                              $"{UturnRejectReason} - ignoring the request\n");
            return false;
        }
        UturnRejectReason = "";
        var (ptsPx, stopsS, revZone, kappaPts, hdgPts) = gen.Value;
        _ref = new RefLine(ptsPx);
        _uturnProfile = BuildUturnProfile(_ref, revZone);
        _uturnStops = stopsS;
        _uturnKappa = kappaPts;
        _uturnHdg = hdgPts;
        // Arc boundaries (where the stored curvature jumps): pursuit and heading
        // alignment must never aim PAST one of these, or the car pre-turns into a
        // minimum-radius arc it cannot correct on (full lock is already required
        // to stay on it). For a single swing this is what keeps the entry clean -
        // there are no stops to clamp to.
        var jumpS = new List<double>();
        for (int i_ = 1; i_ < kappaPts.Length; i_++)
            if (kappaPts[i_] != kappaPts[i_ - 1]) jumpS.Add(_ref.Cum[i_]);
        _uturnLookaheadClamps = stopsS.Concat(jumpS).Distinct().OrderBy(v => v).ToList();
        _uturnStopPtr = 0;
        _uturnState = "drive";
        _uturnHoldT = 0.0;
        _uturnApproachDir = 0;
        _uturnMode = "fwd";
        _uturnStallT = 0.0;
        _uturnReleaseForce = 0.0;
        S = 0.0;
        _uturnActive = true;   // UturnRejectReason was cleared on success above
        string kind = stopsS.Count == 0 ? "single swing (§5a)" : "three-point (§5b)";
        Console.WriteLine($"\n🔄 U-TURN (Wenden) started: {kind}, line {_ref.Total:F1} m, " +
                          $"{stopsS.Count} full stop(s)\n");
        return true;
    }

    /// <summary>Hand back to normal driving: the car is now on the same road,
    /// facing the opposite way, in its lane - a plain route rebuild picks up from
    /// there (car.Forward has flipped, so the route just mirrors).</summary>
    private void FinishUturn()
    {
        Console.WriteLine("\n✅ U-turn complete - resuming normal driving\n");
        _uturnActive = false;
        _ref = null;
        _route = new List<string>();
        _routeKey = null;
        _profile = Array.Empty<double>();
        S = 0.0;
    }

    /// <summary>Per-frame U-turn execution (replaces the normal update body).</summary>
    private void UpdateUturn(double dt, ControlInput control)
    {
        var car = _car;
        var refLine = _ref!;
        // Tight window: the U-turn line folds back on itself, and the spatially
        // nearest point can be on a different branch than the one the car is
        // driving (that mis-projection deadlocks the stops).
        S = RefLineMath.ProjectS(refLine, car.X, car.Y, S,
                                 window: 0.3, globalFallback: false);
        var prof = _uturnProfile;
        double vTarget = prof[(int)Math.Max(0.0, Math.Min(prof.Length - 1, S))];

        // --- full stops between the steps (spec: "Stoppen") ---
        // A stop is consumed ONLY by actually holding at it (below). Safety net:
        // if we have clearly overshot one (missed brake), skip it so we don't keep
        // targeting a point that is behind us.
        while (_uturnStopPtr < _uturnStops.Count &&
               S >= _uturnStops[_uturnStopPtr] + 1.0)
            _uturnStopPtr++;
        double? ns = _uturnStopPtr < _uturnStops.Count ? _uturnStops[_uturnStopPtr] : null;

        // Brake by PHYSICAL distance to the stop point, not by s-distance: on a
        // folded line the projection can slide onto another branch and s lies, but
        // the car's real position does not.
        bool brakingToStop = false;
        if (ns is not null && _uturnState == "drive" && _uturnReleaseForce == 0.0)
        {
            var (spx, spy) = refLine.PointAt(ns.Value);
            double dStop = Math.Hypot(car.X - spx, car.Y - spy) / RefLineMath.PPPM;
            // 1 m margin: the car may sit up to ~0.5-0.8 m OFF the line laterally,
            // and d_stop is measured to a point - with A_BRAKE = 10 m/s^2 the bare
            // v^2/2a + 0.3 threshold (0.69 m at 2.8 m/s) would never trigger
            // against that offset.
            brakingToStop = dStop <= car.Speed * car.Speed / (2.0 * A_BRAKE) + 1.0;
        }

        if (_uturnState == "holding")
        {
            vTarget = 0.0;
            _uturnHoldT += dt;
            if (_uturnHoldT >= UTURN_HOLD_S)
            {
                // Released. The car is ON the cusp (the creep phase drove it there),
                // so the line PAST this stop is where we go next. (A1: forward; A2:
                // reverse; A3: forward.) The profile may lag the cusp by up to ~0.3 m
                // (it switches at integer s), so force the release direction until
                // the LOCAL profile agrees with it and sustains it on its own.
                int iPast = Math.Min(prof.Length - 1, (int)ns!.Value + 2);
                _uturnReleaseForce = prof[iPast] < 0.0 ? -0.5 : +0.5;
                _uturnApproachDir = 0;
                _uturnStopPtr++;
                _uturnState = "drive";
            }
        }
        else if (brakingToStop)
        {
            vTarget = 0.0;
            if (_uturnApproachDir == 0)
                _uturnApproachDir = car.Speed >= 0 ? 1 : -1;
            if (Math.Abs(car.Speed) < 0.1)
            {
                // Actually stopped now. If we are still SHORT of the stop point, creep
                // onto it first: each step of a three-point turn must start EXACTLY at
                // its cusp - the arcs are minimum radius, so starting 1 m early traces
                // a different circle and misses the next stop entirely. (A real driver
                // does exactly this: brake, inch up to the edge, stop.)
                if (ns is not null && S < ns.Value - 0.05)
                {
                    _uturnState = "creeping";
                    _uturnHoldT = 0.0;
                }
                else
                {
                    _uturnState = "holding";
                    _uturnHoldT = 0.0;
                }
            }
        }
        else if (_uturnState == "creeping")
        {
            // Inch up to the cusp in the direction we were approaching it.
            vTarget = 0.5 * _uturnApproachDir;
            if (ns is not null && S >= ns.Value - 0.05)
            {
                _uturnState = "holding";
                _uturnHoldT = 0.0;
            }
        }

        // Forced release direction right after a stop, until the local speed profile
        // agrees with it (see above).
        if (_uturnReleaseForce != 0.0)
        {
            if (_uturnReleaseForce < 0.0) vTarget = Math.Min(vTarget, _uturnReleaseForce);
            else vTarget = Math.Max(vTarget, _uturnReleaseForce);
            int iNow = (int)Math.Max(0.0, Math.Min(prof.Length - 1, S));
            if ((_uturnReleaseForce < 0.0 && prof[iNow] < 0.0) ||
                (_uturnReleaseForce > 0.0 && prof[iNow] > 0.0))
                _uturnReleaseForce = 0.0;
        }

        // --- pursuit mode from the sign of the target speed (the zero zone between
        // stops keeps the last mode, so there is no flip-flop) ---
        if (vTarget < -0.05) _uturnMode = "rev";
        else if (vTarget > 0.05) _uturnMode = "fwd";

        // --- steering: curvature feed-forward on arcs, pure pursuit on straights.
        // The arcs are MINIMUM radius: a car on the line needs EXACTLY MAX_STEER
        // there, so point-chasing has zero margin to correct with. Worse, in reverse
        // the pursuit target sits along the direction of motion, so the law reads
        // ~0-25 deg - BLENDING it in would under-steer the arc and let the nose fall
        // behind (measured: 13 deg by A3 -> 1.4 m outside the line on step 4).
        // delta = atan(L*kappa) makes a kinematic bicycle trace an arc of radius
        // 1/kappa exactly (in reverse the sign mirrors); a constant cusp-entry heading
        // offset then only produces a parallel arc a few cm away. On straights
        // kappa=0 and plain pursuit trims lateral error.
        int i = RefLine.BisectRight(refLine.Cum, S);
        double kappa = _uturnKappa[Math.Max(0, Math.Min(_uturnKappa.Length - 1, i))];
        double delta;
        if (Math.Abs(kappa) > 0.05)
        {
            delta = Math.Atan(WHEELBASE * kappa);
            if (_uturnMode == "rev") delta = -delta;
        }
        else
        {
            // Never aim PAST the next cusp (stop point): a target on the next branch
            // (the arc after A1) makes the car start turning early, entering the
            // minimum-radius arc with a heading error it can never correct (full lock
            // is already required to stay on it). Use the nearest stop ahead, NOT ns -
            // after a release the pointer has already advanced past the cusp that
            // still lies in front of the car.
            double lookahead = 2.0 + 0.15 * Math.Abs(car.Speed);
            foreach (double st_ in _uturnLookaheadClamps)
            {
                if (st_ > S) { lookahead = Math.Min(lookahead, Math.Max(0.1, st_ - S)); break; }
            }
            var (tx, ty) = refLine.PointAt(S + lookahead);
            double dx = (tx - car.X) / RefLineMath.PPPM;
            double dy = (ty - car.Y) / RefLineMath.PPPM;
            double h = Math.Radians(car.Heading);
            double deltaPp;
            if (_uturnMode == "fwd")
            {
                double localF = dx * Math.Sin(h) + dy * Math.Cos(h);
                if (localF < 0.5) localF = 0.5;
                double localR = dx * Math.Cos(h) - dy * Math.Sin(h);
                deltaPp = Math.Atan2(localR, localF);
            }
            else
            {
                // Reversing: aim the REAR of the car at the target point. In reverse a
                // right steer swings the rear to the LEFT (bicycle kinematics with
                // signed v), so the pursuit law mirrors.
                double localF = -(dx * Math.Sin(h) + dy * Math.Cos(h));
                if (localF < 0.5) localF = 0.5;
                double localR = -(dx * Math.Cos(h) - dy * Math.Sin(h));
                deltaPp = -Math.Atan2(localR, localF);
            }
            // Blend in heading alignment to the line's own target nose heading: at
            // maneuver speeds plain pursuit limit-cycles on a straight (measured ~5 deg
            // wobble at the A1 entry), and the arcs inherit whatever heading error is
            // left. The sample point is clamped BEFORE the next cusp - sampling past it
            // makes the car pre-turn into the arc while still on the straight.
            double sH = S + 0.3;
            foreach (double st_ in _uturnLookaheadClamps)
            {
                if (st_ > S) { sH = Math.Min(sH, st_ - 0.05); break; }
            }
            int iH = RefLine.BisectRight(refLine.Cum, Math.Max(0.0, sH));
            double hTarget = Math.PosDeg(Math.Degrees(_uturnHdg[
                Math.Max(0, Math.Min(_uturnHdg.Length - 1, iH))]));
            double deltaAlign = Math.WrapDeg(hTarget - car.Heading);
            delta = 0.5 * deltaPp + 0.5 * deltaAlign;
        }
        delta = Math.Max(-MAX_STEER, Math.Min(MAX_STEER, delta));

        // --- longitudinal: throttle/brake toward the SIGNED profile ---
        // The steering-angle accel gate is deliberately NOT applied here: the
        // profile already caps every arc at UTURN_SPEED_MAX, and with it the car
        // would stall on full-lock arcs (gate closed -> no throttle).
        if (car.Speed > vTarget + 0.05)
        {
            car.Speed = Math.Max(vTarget, car.Speed - A_BRAKE * dt);
            car.IsBraking = true;
        }
        else if (car.Speed < vTarget - 0.05)
        {
            if (vTarget > 0) car.Speed = Math.Min(vTarget, car.Speed + A_CRUISE * dt);
            else car.Speed = Math.Max(vTarget, car.Speed - A_CRUISE * dt);
            car.IsBraking = false;
        }
        else car.IsBraking = false;
        car.Speed = Math.Max(-5.0, Math.Min(V_MAX, car.Speed));
        car.TargetSpeed = car.Speed;

        // --- bicycle kinematics (signed speed: reverse falls out for free) ---
        double maxRate = Math.Abs(car.Speed) > 0.3 ? A_LAT_MAX / Math.Abs(car.Speed) : 0.0;
        double desiredRate = (car.Speed / WHEELBASE) * Math.Tan(delta);
        double rate = Math.Max(-maxRate, Math.Min(maxRate, desiredRate));
        car.Heading = Math.PosDeg(car.Heading + Math.Degrees(rate) * dt);
        double rad = Math.Radians(car.Heading);
        car.X += Math.Sin(rad) * car.Speed * dt * RefLineMath.PPPM;
        car.Y += Math.Cos(rad) * car.Speed * dt * RefLineMath.PPPM;

        SyncSegment();

        // --- completion / stall guard ---
        if (S >= refLine.Total - 1.0) { FinishUturn(); return; }
        if (_uturnState == "drive" && Math.Abs(car.Speed) < 0.05 && Math.Abs(vTarget) > 0.2)
        {
            _uturnStallT += dt;
            if (_uturnStallT > UTURN_STALL_ABORT_S)
            {
                Console.WriteLine($"⚠️  U-turn stalled (no progress for " +
                                  $"{UTURN_STALL_ABORT_S:0} s) - aborting the maneuver");
                // The car recognises it cannot continue: make that VISIBLE (hazard
                // lights, min 5 s display, logged) instead of just silently falling
                // back to normal driving.
                _car.Driver?.SetHazard(true, reason: "U-turn stalled - cannot continue");
                FinishUturn();
            }
        }
        else _uturnStallT = 0.0;
    }

    // ====================================================================
    // Per-frame update
    // ====================================================================

    public void Update(double dt, ControlInput control)
    {
        var car = _car;
        if (_uturnActive) { UpdateUturn(dt, control); return; }
        // Reverse-in parking (docs §1b) owns the car while it runs.
        if (_reversePark is not null) { UpdateReversePark(dt); return; }
        // Parked: stay parked. The destination has been reached and the car is
        // standing at the kerb - it does not roll off again on its own.
        if (Parked)
        {
            if (control.Brake || car.Speed != 0.0) car.Speed = 0.0;
            ParkPhase = "stopped";
            LaneChangeSignal = null;
            _mergeEpisode = null;
            car.TargetSpeed = 0.0;
            return;
        }
        // U-turn request (one-shot flag on the driver, set by keyboard/API).
        var d = car.Driver;
        if (d is BicycleDriver bdd && bdd.UteturnRequested)
        {
            bdd.UteturnRequested = false;
            StartUturn();
            if (_uturnActive) return;
        }
        // Pull-out mode: first ~2 seconds after spawn → max left steer + slow speed.
        bool pullingOut = PullOutFrames > 0;
        if (pullingOut) PullOutFrames--;

        // Brake & park plan (spec §1): stateless per-tick evaluation of
        // (distance to stop point, current speed). When active it owns the
        // longitudinal target; pulling_over derives from its phase (the reference
        // line shifts to the kerb for the swerve zone). The stop point is expressed
        // for the car's REFERENCE POINT (the rear axle, which is what S tracks): at
        // a red flag the car's FRONT BUMPER rests on the flag (spec §1), so the axle
        // stops the front overhang short of it; at a dead end the whole car stops a
        // car length short of the pavement edge so the front corners stay on the road.
        ParkPhase = "none";
        (string Phase, double? VTarget)? plan = null;
        double? dStop = null;
        DecideParkStyle();
        if (HasDestination() && !pullingOut && _ref is not null)
        {
            double stopS = _ref.Total - StopMargin();
            dStop = stopS - ParkS();
            plan = ParkingPlan(dStop.Value, car.Speed);
            if (plan is not null)
            {
                // Stopped at the destination: blinker off (spec §1).
                bool nowStopped = dStop <= 0.5 && car.Speed < 0.3;
                if (ParkStyle != "reverse")
                {
                    ParkPhase = nowStopped ? "stopped" : plan.Value.Phase;
                    // Latch the parked state for consumers (HUD / API): the reverse-in
                    // path sets it itself, and the forward stop used to leave it false.
                    if (nowStopped) Parked = true;
                }
                else
                {
                    // Reverse-in: this stop is only the STAGING point - the car still
                    // has to back into the spot. Latching here would freeze it short
                    // of the flag (the "Parked" early-return swallows the request).
                    ParkPhase = plan.Value.Phase;
                }
            }
            // Standing at the staging point: back into the space (§1b). The roll-out
            // can come to rest up to ~0.3 m SHORT of the stop point (the tail's
            // constant decel engages below 0.3 m/s, i.e. while d/tau still says
            // "keep rolling"), so the threshold must swallow that variance - measured
            // live: a stop at d_stop=0.22 sat forever two centimetres outside a 0.2
            // gate and the car never reversed. The reverse path is anchored to the
            // car's ACTUAL pose (it re-solves the tuck from here), so starting slightly
            // early just means a marginally longer back-up.
            if (ParkStyle == "reverse" && plan is not null &&
                dStop <= 0.5 && Math.Abs(car.Speed) < 0.05)
            {
                if (StartReversePark())
                {
                    UpdateReversePark(dt);
                    return;
                }
                // Not reachable from here after all: stay where we are, parked
                // forwards (the pull-over has already been driven).
                ParkStyle = "forward";
                _parkStyleLocked = true;
                Parked = true;
                ParkPhase = "stopped";
                car.Speed = 0.0;
                return;
            }
        }
        bool pullingOver = plan is not null &&
                           plan.Value.Phase is "decel" or "swerve" or "final";

        // Lane-change blinker (user rule: ALWAYS signal before changing lanes): on
        // from MERGE_SIGNAL_AHEAD_M before the merge zone starts, off once the car
        // has settled onto the new line (past s1).
        LaneChangeSignal = null;
        if (_mergeEpisode is not null && _ref is not null)
        {
            var (s0, s1, direction) = _mergeEpisode.Value;
            if (s0 - MERGE_SIGNAL_AHEAD_M <= S && S < s1)
                LaneChangeSignal = direction;
        }

        MaybeRebuild(pullingOver: pullingOver, pullingOut: pullingOut);
        var refLine = _ref;
        if (refLine is null || refLine.Total < 1e-3) return;

        // Project onto the reference line.
        S = RefLineMath.ProjectS(refLine, car.X, car.Y, S);

        // --- steering: pure pursuit (computed BEFORE the longitudinal step so the
        // throttle can see the steering angle) ---
        // A SMALL base lookahead is critical for tight corners: if the lookahead
        // point lands beyond the corner (e.g. 10 m ahead on a 9 m 90-degree arc),
        // the car aims past the apex and cuts the corner (swings wide). A 2.5 m base
        // keeps the aim point inside the corner at low speed; the 0.2*speed term
        // still stretches it out on straights for stability.
        double lookahead = 2.0 + 0.15 * car.Speed;
        // Tighten lookahead further in high-curvature zones to prevent corner-cutting
        // on sharp junction fillets.
        double localK = Math.Abs(refLine.CurvatureAt(S));
        if (localK > 0.05) lookahead = Math.Min(lookahead, 0.5);
        // Pull-over: track the reference line TIGHTLY (short lookahead) for the whole
        // drift-to-edge maneuver (the blend + the final straight). The long default
        // lookahead makes the car cut the drift curve and stay well inside the edge,
        // and in the final straight it aims at the clamped endpoint (off the approach
        // path), freezing the heading a few degrees off parallel. A tight aim point
        // follows the curve to the edge and keeps the wheels parallel to the curb.
        double dToStop = dStop ?? (refLine.Total - S);
        if (pullingOver && dToStop < PARK_BLEND_START_M)
            lookahead = Math.Min(lookahead, PARK_TRACK_LOOKAHEAD_M);
        // Pull-out: very short lookahead for sharp left turn into lane.
        if (pullingOut) lookahead = Math.Min(lookahead, 1.5);

        var (tx, ty) = refLine.PointAt(S + lookahead);
        double dx = (tx - car.X) / RefLineMath.PPPM;
        double dy = (ty - car.Y) / RefLineMath.PPPM;
        double h = Math.Radians(car.Heading);
        double localRight = dx * Math.Cos(h) - dy * Math.Sin(h);
        double localForward = dx * Math.Sin(h) + dy * Math.Cos(h);
        double delta;
        if (localForward <= 0.0)
        {
            // Aim point at or BEHIND us: happens in tight corners where the s-projection
            // (coarse window scan, ~0.5-1 m resolution) lags the car while the
            // high-curvature lookahead (0.5 m) is shorter than that lag. Aiming at a
            // point behind commands a hard counter-steer and oscillates the car in place
            // until it leaves the road (measured: roundabout entry - heading sawed
            // 190 -> 178 -> 210 deg at 9 km/h while s sat on a quantisation step). Align
            // with the line's tangent instead: that is what a driver does when the aim
            // point slips behind.
            double lh = Math.Radians(refLine.HeadingAt(S));
            delta = Math.Atan2(Math.Sin(lh - h), Math.Cos(lh - h));
        }
        else
        {
            if (localForward < 0.5) localForward = 0.5;
            delta = Math.Atan2(localRight, localForward);
        }
        delta = Math.Max(-MAX_STEER, Math.Min(MAX_STEER, delta));

        // Pull-over final straight (spec §1 "parallel ausrichten"): the line is a
        // constant offset here, so the job is no longer to chase a point ahead but to
        // END UP ON the line, parallel to it. Pure pursuit cannot do that: it aims at
        // a point, so it arrives at the line still rotating and the car froze a few
        // degrees nose-in (measured 5-12 deg). Use a Stanley-type law instead - steer
        // by the heading error PLUS a cross-track term that vanishes as the car reaches
        // the line. Both terms go to zero together, which is exactly "flush with the
        // kerb and parallel to it". This is a real turn: the car is still rolling and
        // the lateral-accel clamp below keeps the implied radius above the physical
        // minimum (no in-place rotation).
        if (pullingOver && dToStop < PARK_BLEND_END_M)
        {
            var (refX, refY) = refLine.PointAt(S);
            double lineHeading = refLine.HeadingAt(S);
            double lh = Math.Radians(lineHeading);
            // Right-hand normal of the line, in screen coordinates
            // (x = sin(h), y = cos(h) is "forward").
            double rnx = Math.Cos(lh), rny = -Math.Sin(lh);
            double eRight = ((car.X - refX) * rnx + (car.Y - refY) * rny) / RefLineMath.PPPM;
            double headingErr = Math.Radians(Math.WrapDeg(lineHeading - car.Heading));
            double cross = Math.Atan2(PARK_ALIGN_GAIN * eRight, Math.Max(car.Speed, 1.0));
            // Fade the cross term out over the last metre (see constant): only the
            // heading correction survives into the final roll-out.
            if (dToStop < PARK_ALIGN_CROSS_FADE_START_M)
                cross *= Math.Max(0.0, Math.Min(1.0,
                    (dToStop - PARK_ALIGN_CROSS_FADE_END_M) /
                    (PARK_ALIGN_CROSS_FADE_START_M - PARK_ALIGN_CROSS_FADE_END_M)));
            // Heading term with PARK_ALIGN_HDG_GAIN (see constant): the 1:1 match is
            // too slow for this window at creep speed.
            delta = Math.Max(-MAX_STEER, Math.Min(MAX_STEER,
                                                  PARK_ALIGN_HDG_GAIN * headingErr - cross));
        }

        // --- longitudinal: throttle/brake toward the speed profile ---
        // The profile ALREADY encodes the cruising speed on straights and the (much
        // lower) corner speed at bends, with a braking ramp into each corner. The
        // accelerator means "go as fast as the profile allows" - it must NOT override
        // the corner limit (that is exactly what made the car barrel into corners at
        // cruise and swing wide).
        bool accel = control.Accelerate;
        bool brake = control.Brake;
        double brakeRate = A_BRAKE;
        double vTarget;
        if (plan is not null)
        {
            // Inside the plan the deceleration never exceeds A_PARK (a real driver
            // modulates the pedal); full A_BRAKE only as an emergency when even A_PARK
            // can no longer stop in time.
            var (phase, vPlan) = plan.Value;
            // The emergency only makes sense while the car still carries real speed. At
            // creep speed a few centimetres of overshoot past the stop point used to
            // flip this to A_BRAKE and slam the car to a standstill from 2 km/h - the
            // exact jerk spec §1 forbids, and nothing was gained by it.
            brakeRate = dStop is not null &&
                        car.Speed > PARK_SWERVE_SPEED_M &&
                        dStop < car.Speed * car.Speed / (2.0 * A_PARK) - 0.5
                ? A_BRAKE : A_PARK;
            if (phase == "lead")
            {
                // Lead phase: HOLD the approach speed (table phase 1) - unless the normal
                // profile (a corner ahead) demands less.
                vTarget = Math.Min(car.Speed, TargetSpeed(S));
            }
            else vTarget = vPlan!.Value;

            // Braking for a CORNER is not part of the parking manoeuvre and must not be
            // slowed down by its comfort limit. The plan can engage 100 m before the
            // destination - across a junction the car still has to turn at - and capping
            // the pedal at A_PARK there meant it could no longer make the junction entry
            // speed: it arrived at a T-junction at 59 km/h instead of 29 and cut the
            // corner off the road (measured, tests 3/4).
            double vProf = TargetSpeed(S);
            if (vProf <= vTarget + 1e-3)
            {
                vTarget = Math.Min(vTarget, vProf);
                brakeRate = A_BRAKE;
            }
            if (phase == "final" && car.Speed < PARK_ROLL_END_M_S)
            {
                // Tail of the roll-out: v = d/tau decays exponentially and would creep
                // for another second at walking-pace/10. Close it out with a small
                // CONSTANT deceleration - still a sixth of A_PARK, so the pedal is
                // easing off, not stamping - which reaches zero in ~0.5 s and stays there.
                vTarget = 0.0;
                brakeRate = Math.Min(brakeRate, PARK_ROLL_END_A);
            }
            if (phase == "final")
            {
                // Hands off the throttle for the roll-out. The target shrinks with the
                // remaining distance, and the projection of that distance jitters by a
                // centimetre or two, so an active throttle chased it: the last second of
                // the stop sawed between 0.2 and 0.4 km/h at +-3 m/s^2. A driver coming
                // to a stop is not on the gas.
                accel = false;
            }
        }
        else
        {
            // No active plan. Pedal held (suite protocol / human on W): cruise at the
            // profile speed - including keeping hard after a target while off line, which
            // is the recovery behaviour we want DURING a scenario. Pedal released (after
            // reset_controls() once the test is finished): no throttle - ease down to a
            // stop at the comfort rate (engine braking + rolling resistance), like a real
            // car left in gear with the foot off the gas, so a leftover test car comes to
            // rest instead of rolling forever.
            if (accel) vTarget = TargetSpeed(S);
            else
            {
                vTarget = 0.0;
                brakeRate = Math.Min(brakeRate, A_PARK);
            }
        }
        if (brake)
        {
            // External brake (keyboard S / API safety net) always wins.
            vTarget = 0.0;
        }
        // Pull-out: cap speed so curve is visible (~18 km/h, not 80).
        if (pullingOut) vTarget = Math.Min(vTarget, 5.0);

        // No flooring it mid-corner: after the apex the profile jumps back to cruise
        // speed while the car is still rotating (heading lags the reference line). A real
        // driver keeps the throttle off until the wheels are nearly straight, so scale
        // the acceleration rate by the steering angle (full below ~5 deg, zero above
        // ~25 deg).
        double deltaDeg = Math.Degrees(Math.Abs(delta));
        double accelScale = Math.Max(0.0, Math.Min(1.0, 1.0 - (deltaDeg - 5.0) / 20.0));
        // Standstill creep (see CREEP_SPEED): let the car roll forward while steering so a
        // high steering demand at spawn can't deadlock it. ...but never while parking: the
        // plan's roll-out target keeps falling, and the creep boost pushed against it every
        // other frame (measured: the speed sawing between 0.2 and 0.4 km/h with +-3 m/s^2
        // for the last second - a visible shudder at the kerb).
        if (accel && car.Speed < CREEP_SPEED && plan is null)
            accelScale = Math.Max(accelScale, CREEP_SCALE);

        string? dbgPlanPhase = plan is { } pp ? pp.Phase : null;     // TEMPORARY debug
        DbgState = (S, _ref?.Total ?? -1.0, dbgPlanPhase,
                    Math.Degrees(Math.Abs(delta)), TargetSpeed(S),
                    accel, vTarget, accelScale);                     // TEMPORARY debug

        // Ease speed toward the target (accel / brake rates).
        bool braked = false;   // real brake pedal applied this tick (lights)
        if (accel && car.Speed < vTarget)
            car.Speed = Math.Min(vTarget, car.Speed + A_CRUISE * accelScale * dt);
        else if (car.Speed > vTarget)
        {
            double gap = car.Speed - vTarget;
            // SAILING: shed speed with the throttle OFF - rolling resistance +
            // aero drag, no pedal, no lights. A driver sails when coasting alone
            // keeps up with the profile (steady state, gentle ramps) and presses
            // the brake (lights on) only when it doesn't (sharp corner entry).
            // The old code "braked" whenever Speed was 5 cm/s above target, so
            // the brake light flickered at tick rate in every braking ramp.
            double sLook = Math.Min(S + car.Speed * SAIL_LOOKAHEAD_S,
                                    _ref?.Total ?? double.PositiveInfinity);
            double reqDecel = Math.Max(0.0,
                (car.Speed - TargetSpeed(sLook)) / SAIL_LOOKAHEAD_S);
            // U-turns are excluded like plans: their cusp stops need the full
            // A_BRAKE (0.1 m stopping distance at creep) - sailing would
            // overshoot by ~2 m and the stop-skip safety net would drop them.
            bool sail = !brake && plan is null && !_uturnActive &&
                        (!accel ||
                         (gap <= Config.SAIL_BAND_MPS &&
                          reqDecel <= Config.CoastDecel(car.Speed)));
            if (sail)
                car.Speed = Math.Max(vTarget, car.Speed - Config.CoastDecel(car.Speed) * dt);
            else
            {
                car.Speed = Math.Max(vTarget, car.Speed - brakeRate * dt);
                braked = true;
            }
        }
        car.Speed = Math.Max(0.0, Math.Min(V_MAX, car.Speed));

        // End of the parking roll-out: the distance-proportional target decays
        // exponentially and never reaches zero, so drop the last 0.2 km/h once the car
        // is at (or past) the stop point.
        if (plan is not null && plan.Value.Phase == "final" && dStop is not null &&
            dStop <= 0.3 && car.Speed < PARK_STANDSTILL_M_S)
            car.Speed = 0.0;
        car.TargetSpeed = car.Speed;
        // The brake light follows the PEDAL, not the speed error: sailing
        // (throttle off, drag does the work) keeps it dark.
        car.IsBraking = brake || braked;

        // End-of-stop wheel straightening (parking only): below the yaw-clamp threshold
        // (0.3 m/s, see the kinematics below) the car physically cannot rotate, so a large
        // held steering command only reads as "fighting the wheel" while it rolls straight
        // to its stop (measured: -10 deg held over the last 0.5 s). Fade the command to zero
        // across [0.3, 0.5] m/s while the parking plan is active - continuous, and free above
        // 0.5 m/s where the rotation budget actually lives. Other maneuvers (U-turn,
        // reverse-in) keep their full commands.
        if (plan is not null && car.Speed < 0.5)
            delta *= Math.Max(0.0, Math.Min(1.0, (car.Speed - 0.3) / 0.2));

        // Expose the steering angle for the driver's mechanical blinker auto-off
        // (steered in + steered back = indicator cancels itself).
        car.SteerAngle = delta;

        // --- bicycle kinematics ---
        // Lateral-accel cap (understeer): limit the heading rate.
        double maxRate = car.Speed > 0.3 ? A_LAT_MAX / car.Speed : 0.0;
        double desiredRate = (car.Speed / WHEELBASE) * Math.Tan(delta);
        double rate = Math.Max(-maxRate, Math.Min(maxRate, desiredRate));
        car.Heading = Math.PosDeg(car.Heading + Math.Degrees(rate) * dt);

        double rad = Math.Radians(car.Heading);
        car.X += Math.Sin(rad) * car.Speed * dt * RefLineMath.PPPM;
        car.Y += Math.Cos(rad) * car.Speed * dt * RefLineMath.PPPM;

        DbgSpeedAtEnd = car.Speed;   // TEMPORARY debug

        // --- keep seg_idx / progress / forward in sync (for the API) ---
        SyncSegment();
    }

    private void SyncSegment()
    {
        var car = _car;
        var net = _network;
        int bestSeg = car.SegIdx;
        double bestDist = double.PositiveInfinity;
        double bestT = car.Progress;
        // Track the nearest segment that is on our ROUTE separately.
        int? routeSeg = null; double routeDist = double.PositiveInfinity; double routeT = car.Progress;
        for (int idx = 0; idx < net.Segments.Count; idx++)
        {
            var seg = net.Segments[idx];
            double dxs = seg.X2 - seg.X1, dys = seg.Y2 - seg.Y1;
            double lengthSq = dxs * dxs + dys * dys;
            if (lengthSq == 0) continue;
            double t = Math.Max(0.0, Math.Min(1.0, ((car.X - seg.X1) * dxs + (car.Y - seg.Y1) * dys) / lengthSq));
            double projX = seg.X1 + t * dxs, projY = seg.Y1 + t * dys;
            double dist = Math.Hypot(car.X - projX, car.Y - projY);
            if (dist < bestDist) { bestDist = dist; bestSeg = idx; bestT = t; }
            if (RouteSegSet.Contains(idx) && dist < routeDist)
            {
                routeDist = dist; routeSeg = idx; routeT = t;
            }
        }
        // Prefer the route when it is a near-tie. At a fork the branches are nearly
        // equidistant, so plain nearest-segment can pick the one we are NOT taking - and
        // because leaving the route set triggers a rebuild, that mis-assignment re-plans
        // the car onto the wrong branch and yanks the reference line out from under it.
        // Measured on the Y-junction: 1.3 m before the node the car flipped to the far
        // fork and was off the road 0.5 s later. We know which way we intend to go; a
        // couple of centimetres of projection noise should not overrule it.
        if (routeSeg is not null && routeDist <= bestDist + RefLineMath.ROUTE_STICKINESS_PX)
        {
            bestSeg = routeSeg.Value; bestT = routeT;
        }
        car.SegIdx = bestSeg;
        car.Progress = bestT;
        var best = net.Segments[bestSeg];
        double bDxs = best.X2 - best.X1, bDys = best.Y2 - best.Y1;
        double segHeading = Math.Degrees(Math.Atan2(bDxs, bDys));
        car.Forward = Math.Abs(Math.WrapDeg(car.Heading - segHeading)) < 90;
    }

    // ---- reset (after a teleport) ----

    public void Reset()
    {
        _ref = null;
        _route = new List<string>();
        _routeKey = null;
        _profile = Array.Empty<double>();
        S = 0.0;
        PullOutFrames = 0;   // spawn is in the driving position (see constructor)
        _uturnActive = false;
        _uturnProfile = Array.Empty<double>();
        _uturnStops = new List<double>();
        _uturnStopPtr = 0;
        _uturnState = "drive";
        _uturnHoldT = 0.0;
        _uturnApproachDir = 0;
        _uturnMode = "fwd";
        _uturnStallT = 0.0;
        _uturnKappa = Array.Empty<double>();
        _uturnHdg = Array.Empty<double>();
        _uturnLookaheadClamps = new List<double>();
        _uturnReleaseForce = 0.0;
        _reversePark = null;
        Parked = false;
    }
}
