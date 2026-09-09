using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Prepared;
using NetTopologySuite.Operation.Buffer;

namespace DrivingGame.Sim;

/// <summary>
/// Racing line — the fastest legal line through a route.
///
/// Ported 1:1 from /Users/hauke/prj/car/src/raceline.py (keep in sync).
/// The driving line is not a fixed lane offset. It is the solution to a
/// constrained optimisation, stated exactly as the game's driving rules:
///
///   1. never leave the pavement          -> upper corridor bound
///   2. never enter the oncoming lane     -> lower corridor bound
///   3. be as fast as possible            -> the objective
///   4. use a racing line                 -> what (3) produces, not a feature
///
/// Since v = sqrt(a_lat / kappa), "as fast as possible" means "as straight
/// as possible": minimise the curvature of the line, subject to staying
/// inside the corridor that rules 1 and 2 define. Rules 1 and 2 are HARD
/// BOUNDS — the optimiser cannot trade them away for speed, so a legal
/// line is guaranteed by construction rather than detected afterwards by a
/// validator.
///
/// Why an optimiser and not a formula: the intuitive "swing out before the
/// corner, cut the apex" line CANNOT be had by nudging the centreline
/// sideways with a bump function. Offsetting a path laterally by o(s)
/// changes its curvature by roughly -o''(s), so a local bump buys radius
/// at the apex and pays for it with sharper curvature on both shoulders —
/// the net effect is a TIGHTER line (measured: peak curvature 2-4x worse).
/// The gain only appears when the whole approach and exit are free to
/// reshape together, which is what the optimiser does and a formula cannot.
/// </summary>
public static class Raceline
{
    private const double PPPM = Config.PIXELS_PER_METER;

    /// <summary>Half-bandwidth of the biharmonic normal equations.</summary>
    public const int BW = 2;
    /// <summary>Station spacing of the driving line (m).</summary>
    public const double SampleM = 0.5;
    /// <summary>Spacing at which the pavement is actually probed (m).</summary>
    private const double CorridorProbeM = 2.0;
    /// <summary>Extra outside room inside an intersection (m).</summary>
    private const double JunctionExtraRoomM = 1.5;
    /// <summary>Distance over which the nominal lane position transitions
    /// across a road change — a STRAIGHT diagonal, not an S-curve: the
    /// pavement only allows the lateral shift inside/around the
    /// intersection square, so the line simply goes from the approach lane
    /// to the exit lane in one straight stroke.</summary>
    public const double LaneEaseM = 24.0;

    /// <summary>Per-station road properties: (oneway, width, lanes,
    /// parking_lane_width) — see <see cref="StationSegments"/>.</summary>
    public readonly record struct StationProps(bool Oneway, double Width, int Lanes, double Parking);

    /// <summary>One solved driving line: dense uniform points + normals
    /// (world px), lateral offsets (m) and arc lengths (m).</summary>
    public sealed class SolveOutput
    {
        public required List<(double X, double Y)> Points { get; init; }
        public required List<(double X, double Y)> Normals { get; init; }
        public required double[] Offsets { get; init; }
        public required double[] Cum { get; init; }
    }

    // ----------------------------------------------------------------------
    // banded linear algebra
    // ----------------------------------------------------------------------
    // Minimising sum((kappa0 - o'')^2) gives normal equations whose matrix
    // is the square of a second-difference operator: symmetric, positive
    // definite (with regularisation) and PENTADIAGONAL. Exploiting that
    // band makes each solve O(n) instead of O(n^3), which is what keeps a
    // whole route affordable — a dense solve on a 250-station route needs
    // ~5M operations per active-set iteration and is far too slow.

    /// <summary>Solve A x = b for a symmetric positive-definite banded
    /// matrix. A is stored band-wise: A[i][k] is the matrix element
    /// (i, i - BW + k), with out-of-range entries ignored. No pivoting is
    /// needed (the system is SPD thanks to the regularisation term added
    /// by the caller).</summary>
    public static double[] BandSolve(double[][] A, double[] b)
    {
        int n = b.Length;
        var a = new double[n][];
        for (int i = 0; i < n; i++) a[i] = (double[])A[i].Clone();
        var x = (double[])b.Clone();

        // Forward elimination, touching only the band.
        for (int i = 0; i < n; i++)
        {
            double piv = a[i][BW];
            if (Math.Abs(piv) < 1e-14)
                piv = a[i][BW] = 1e-14;
            int rEnd = Math.Min(n, i + BW + 1);
            for (int r = i + 1; r < rEnd; r++)
            {
                int k = BW - (r - i);                 // column i as seen from row r
                double f = a[r][k] / piv;
                if (f == 0.0) continue;
                for (int c = 0; c <= BW; c++)         // upper half of row i
                {
                    int rc = k + c;
                    if (rc <= 2 * BW)
                        a[r][rc] -= f * a[i][BW + c];
                }
                x[r] -= f * x[i];
            }
        }

        // Back substitution.
        for (int i = n - 1; i >= 0; i--)
        {
            double s = x[i];
            for (int c = 1; c <= BW; c++)
            {
                int j = i + c;
                if (j < n)
                    s -= a[i][BW + c] * x[j];
            }
            x[i] = s / a[i][BW];
        }
        return x;
    }

    /// <summary>A nominal lane position: either a SCALAR (same at every
    /// station) or a per-station PROFILE. `base` in raceline.py terms.</summary>
    public sealed class BaseNominal
    {
        public static readonly BaseNominal None = new(null, null);
        private BaseNominal(double? scalar, double[]? profile) { Scalar = scalar; Profile = profile; }
        public static BaseNominal OfScalar(double v) => new(v, null);
        public static BaseNominal OfProfile(double[] p) => new(null, p);
        public double? Scalar { get; }
        public double[]? Profile { get; }
        /// <summary>base[i] — None when no nominal is given at all.</summary>
        public double? At(int i) => Profile is not null ? Profile[i] : Scalar;
    }

    /// <summary>Lateral offsets minimising line curvature inside [lo, hi].
    ///
    /// Minimise  sum_i ( kappa0_i - (o_{i-1} - 2 o_i + o_{i+1}) / ds^2 )^2
    /// subject to lo_i <= o_i <= hi_i.
    ///
    /// Box constraints are handled by an active set: solve, clamp whatever
    /// escaped its bound with a stiff penalty (which preserves the band, so
    /// every round stays O(n)), and repeat until the solution is feasible.
    ///
    /// `base` (the nominal lane offset) breaks the tie on STRAIGHT sections,
    /// where the objective is flat and any constant offset is optimal: the
    /// line settles at `base` instead of drifting to the corridor centre. On
    /// a wide one-way (no centreline bound) the corridor centre IS the middle
    /// of the road — which would make the car abandon its lane position.
    /// `base` may also be a per-station PROFILE (same length as the
    /// stations): each station then settles at its own nominal offset — used
    /// for the merge-right blend before parking (docs §1 variant).</summary>
    public static double[] MinCurvatureOffsets(double[] kappa0, double[] lo, double[] hi,
        double ds, int maxRounds = 40, BaseNominal? baseNominal = null)
    {
        int n = kappa0.Length;
        if (n < 5)
        {
            var oSmall = new double[n];
            for (int i = 0; i < n; i++)
            {
                double? b = baseNominal?.At(i);
                oSmall[i] = Math.Max(lo[i], Math.Min(hi[i], b ?? 0.5 * (lo[i] + hi[i])));
            }
            return oSmall;
        }

        double inv = 1.0 / (ds * ds);
        int[] stencilD = { -1, 0, 1 };
        double[] stencilC = { -inv, 2.0 * inv, -inv };
        const double LAMBDA = 1e-6;   // keeps the (otherwise singular) system definite
        const double PENALTY = 1e6;   // stiffness used to pin an active constraint

        var pinned = new Dictionary<int, double>();
        var o = new double[n];
        for (int round = 0; round < maxRounds; round++)
        {
            var A = new double[n][];
            for (int i = 0; i < n; i++) A[i] = new double[2 * BW + 1];
            var b = new double[n];
            // Regularise toward the nominal lane position at EVERY station.
            // On curves the curvature term dominates by many orders of
            // magnitude, so this only matters where the objective is flat:
            // it pins the line at `base` (spec §3's normal position) instead
            // of leaving it to the solver's whim. Pulling ONLY the endpoints
            // is not enough: the stencil chain then solves a long straight to
            // a LINEAR profile between them — dipping through the middle of
            // the road (measured: 1.67 m at both ends, -0.11 m mid-line on a
            // 300 m one-way). base=None reproduces the old pull-toward-zero.
            for (int i = 0; i < n; i++)
            {
                A[i][BW] += LAMBDA;
                double? bb = baseNominal?.At(i);
                b[i] += LAMBDA * Math.Max(lo[i], Math.Min(hi[i], bb ?? 0.0));
            }
            // One residual row per interior station.
            for (int i = 1; i < n - 1; i++)
            {
                for (int da = 0; da < 3; da++)
                {
                    int ia = i + stencilD[da];
                    double ca = stencilC[da];
                    b[ia] += ca * kappa0[i];
                    for (int db = 0; db < 3; db++)
                    {
                        int ib = i + stencilD[db];
                        int k = BW + (ib - ia);
                        if (k >= 0 && k <= 2 * BW)
                            A[ia][k] += ca * stencilC[db];
                    }
                }
            }
            // Pin the active constraints.
            foreach (var (i, v) in pinned)
            {
                A[i][BW] += PENALTY;
                b[i] += PENALTY * v;
            }
            // Endpoints are free to sit anywhere legal but must not float
            // away from the nominal lane position, which would tilt the whole
            // line (on straights this is what pins the line at `base`).
            foreach (int i in new[] { 0, n - 1 })
            {
                if (!pinned.ContainsKey(i))
                {
                    A[i][BW] += 1e-3;
                    double? bb = baseNominal?.At(i);
                    double tgt = bb is not null
                        ? Math.Max(lo[i], Math.Min(hi[i], bb.Value))
                        : 0.5 * (lo[i] + hi[i]);
                    b[i] += 1e-3 * tgt;
                }
            }

            o = BandSolve(A, b);

            int worstI = -1; double worstGap = 1e-9, worstV = 0.0;
            for (int i = 0; i < n; i++)
            {
                if (pinned.ContainsKey(i)) continue;
                if (o[i] < lo[i] - 1e-9 && (lo[i] - o[i]) > worstGap)
                {
                    worstI = i; worstGap = lo[i] - o[i]; worstV = lo[i];
                }
                else if (o[i] > hi[i] + 1e-9 && (o[i] - hi[i]) > worstGap)
                {
                    worstI = i; worstGap = o[i] - hi[i]; worstV = hi[i];
                }
            }
            if (worstI < 0) break;
            pinned[worstI] = worstV;
        }

        for (int i = 0; i < n; i++)
            o[i] = Math.Max(lo[i], Math.Min(hi[i], o[i]));
        return o;
    }

    // ----------------------------------------------------------------------
    // the legal corridor (rules 1 and 2)
    // ----------------------------------------------------------------------

    /// <summary>Resample a polyline (world px) at ~ds metres; return points + arc len.</summary>
    public static (List<(double X, double Y)> P, List<double> S, double Total)
        Resample(List<(double X, double Y)> pts, double ds)
    {
        var cum = new List<double> { 0.0 };
        for (int i = 0; i < pts.Count - 1; i++)
            cum.Add(cum[^1] + Math.Hypot(pts[i + 1].X - pts[i].X,
                                         pts[i + 1].Y - pts[i].Y) / PPPM);
        double total = cum[^1];
        if (total < 1e-6)
            return (new List<(double X, double Y)> { pts[0] }, new List<double> { 0.0 }, 0.0);
        int n = Math.Max(2, (int)(total / ds) + 1);
        var outP = new List<(double X, double Y)>(n);
        var S = new List<double>(n);
        int j = 0;
        for (int i = 0; i < n; i++)
        {
            double s = Math.Min(total, i * total / (n - 1));
            while (j < cum.Count - 2 && cum[j + 1] < s) j++;
            double span = cum[j + 1] - cum[j];
            double t = span > 1e-9 ? (s - cum[j]) / span : 0.0;
            outP.Add((pts[j].X + t * (pts[j + 1].X - pts[j].X),
                      pts[j].Y + t * (pts[j + 1].Y - pts[j].Y)));
            S.Add(s);
        }
        return (outP, S, total);
    }

    /// <summary>Right normals + curvature per station. Curvature is the
    /// wrapped heading change over one ds window (endpoints copy their
    /// neighbour).</summary>
    public static (List<(double X, double Y)> N, double[] K)
        NormalsAndCurvature(List<(double X, double Y)> P, double ds)
    {
        int n = P.Count;
        var N = new List<(double X, double Y)>(n);
        var K = new double[n];
        for (int i = 0; i < n; i++)
        {
            var a = P[Math.Max(0, i - 1)];
            var b = P[Math.Min(n - 1, i + 1)];
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double L = Math.Hypot(dx, dy);
            if (L == 0.0) L = 1.0;
            N.Add((dy / L, -dx / L));          // right normal
            if (i > 0 && i < n - 1)
            {
                double h1 = Math.Atan2(P[i].X - P[i - 1].X, P[i].Y - P[i - 1].Y);
                double h2 = Math.Atan2(P[i + 1].X - P[i].X, P[i + 1].Y - P[i].Y);
                K[i] = WrapPi(h2 - h1) / ds;
            }
            else
            {
                K[i] = 0.0;
            }
        }
        if (n > 1)
        {
            K[0] = K[1];
            K[^1] = K[^2];
        }
        return (N, K);
    }

    /// <summary>Python-style float modulo (result has the divisor's sign).</summary>
    private static double Mod(double x, double m)
    {
        double r = x % m;
        return r < 0 ? r + m : r;
    }

    /// <summary>Wrap an angle to (-pi, pi].</summary>
    private static double WrapPi(double a) => Mod(a + Math.PI, 2 * Math.PI) - Math.PI;

    /// <summary>Nearest junction centre (degree &gt;= 3 node) for each
    /// station, or null.
    ///
    /// That node is the white dot the renderer paints at every real
    /// intersection. Going STRAIGHT or turning RIGHT it must stay on our
    /// left, which is just keep-right restated at the one place the
    /// centreline stops existing.
    ///
    /// Turning LEFT it must not. StVO 9(4): "Linksabbieger muessen einander
    /// voreinander abbiegen, sofern nicht die Verkehrslage oder die Gestaltung
    /// der Kreuzung ein Umeinanderfahren erfordert." The German default is
    /// VOREINANDER — opposing left-turners pass in front of one another,
    /// driver's side to driver's side, each turning before it reaches the
    /// centre. That puts the dot on their RIGHT. Umeinander (around the
    /// centre, dot on the left) is the exception, not the rule.
    ///
    /// Which is why a left turn could not satisfy a dot-on-the-left
    /// constraint without contorting to a 1.5 m radius: the constraint was
    /// simply wrong for that manoeuvre.
    ///
    /// Returns the node only where the route does NOT turn left there, so
    /// the caller constrains exactly the cases the rule covers.</summary>
    public static List<(double X, double Y)?> JunctionNodePerStation(
        RoadNetwork network, List<(double X, double Y)> P, double radiusM = 14.0)
    {
        double r2 = (radiusM * PPPM) * (radiusM * PPPM);
        var nodes = new List<(double X, double Y)>();
        foreach (var (nid, xy) in network.Nodes)
            if (network.NodeDegree.TryGetValue(nid, out int deg) && deg >= 3)
                nodes.Add(xy);

        bool TurnsLeftAt(int k)
        {
            // Signed heading change of the route across this node (+ = right).
            int a = Math.Max(0, k - 20), b = Math.Min(P.Count - 1, k + 20);
            if (b - a < 4) return false;
            double h1 = Math.Atan2(P[a + 1].X - P[a].X, P[a + 1].Y - P[a].Y);
            double h2 = Math.Atan2(P[b].X - P[b - 1].X, P[b].Y - P[b - 1].Y);
            return WrapPi(h2 - h1) < Math.PI * -30.0 / 180.0;
        }

        var outN = new List<(double X, double Y)? >(P.Count);
        for (int k = 0; k < P.Count; k++)
        {
            (double X, double Y)? best = null;
            double bd = r2;
            foreach (var q in nodes)
            {
                double d2 = (P[k].X - q.X) * (P[k].X - q.X) + (P[k].Y - q.Y) * (P[k].Y - q.Y);
                if (d2 <= bd)
                {
                    bd = d2;
                    best = q;
                }
            }
            outN.Add(best is not null && TurnsLeftAt(k) ? null : best);
        }
        return outN;
    }

    /// <summary>Per-station [lo, hi] lateral offsets that satisfy rules 1
    /// and 2.
    ///
    /// hi (rule 1): the furthest RIGHT the car can sit with its whole body
    /// still on pavement. Taken from the paved polygon eroded by half the
    /// car's width plus the shared edge tolerance, so it automatically
    /// respects corner rounding — the exact thing a fixed lane offset
    /// ignores, and the reason the car used to clip the kerb on the way
    /// into a bend.
    ///
    /// lo (rule 2): the furthest LEFT the car can sit without any part of
    /// it crossing the centreline. On a one-way carriageway there is no
    /// oncoming lane, so the bound falls back to the pavement edge.</summary>
    public static (double[] Lo, double[] Hi) LegalCorridor(
        RoadNetwork network,
        List<(double X, double Y)> P,
        List<(double X, double Y)> N,
        StationProps[] stationProps,
        List<(double X, double Y)? >? junctionNodes = null)
    {
        // Cache the eroded pavement on the network: it never changes, and
        // both the erosion (a buffer over the whole map) and the repeated
        // contains() queries are far too slow to redo on every route
        // rebuild — that cost showed up as a 0.3 s frame hitch, which moved
        // the car ~9 m in a single physics step and read as a teleport.
        // (Mirrors raceline.py's _raceline_safe / _raceline_safe_prep.)
        Geometry safe;
        if (network.RacelineSafe is not null)
        {
            safe = network.RacelineSafe;
        }
        else
        {
            double inset = (Config.CAR_WIDTH / 2.0 + Config.ROAD_EDGE_TOLERANCE_M) * PPPM;
            // Buffer parameters match shapely 2.x defaults (quad_segs=16,
            // round cap/join) so the eroded set agrees with the Python
            // reference within buffer discretisation.
            safe = network.GetPavedPolygon().Buffer(
                -inset, new BufferParameters(16, EndCapStyle.Round, JoinStyle.Round, 5.0));
            network.RacelineSafe = safe;
        }
        IPreparedGeometry safePrep = network.RacelineSafePrep
            ?? (network.RacelineSafePrep = PreparedGeometryFactory.Prepare(safe));
        // Ground-truth fallback (see Reach4): the exact rule the live
        // off-road check enforces — four corners within tolerance of the
        // RAW pavement.
        Geometry rawPav = network.GetPavedPolygon();
        double tolPx = Config.ROAD_EDGE_TOLERANCE_M * PPPM;

        double? Reach(double px, double py, double nx, double ny, double sign)
        {
            // Bisect outward along +-normal for the last on-pavement offset.
            // null means "no usable measurement here", NOT "no room". This
            // happens routinely at a route's ends: a dead-end node sits at
            // the very tip of the carriageway, and eroding by half a car
            // width pulls the end cap back behind the first stations.
            // Reporting 0 there collapsed the corridor to zero width and
            // pinned the line to the centreline for the first few metres, so
            // it then had to jump ~2 m sideways to rejoin the lane — a kink
            // of apparent radius 1.9 m, tighter than the car can steer, at
            // the start of a route.
            if (!safePrep.Contains(new Point(px, py)))
                return null;
            double good = 0.0, bad = 8.0;
            for (int it = 0; it < 9; it++)        // ~0.015 m precision
            {
                double mid = 0.5 * (good + bad);
                var q = new Point(px + nx * sign * mid * PPPM, py + ny * sign * mid * PPPM);
                if (safePrep.Contains(q)) good = mid;
                else bad = mid;
            }
            return good;
        }

        // A ray reading this small is never a real kerb: approaching a
        // genuine edge the reach tapers smoothly, it does not cliff to
        // centimetres. That pattern means the eroded set has a NOTCH at the
        // probe point — measured at the roundabout entry, where the spoke's
        // round end-cap meets the narrow ring band: 7 cm reported although a
        // full car fits 1.5 m further out (four-corner test). The pin that
        // caused the line to kink to a 1.6 m radius — tighter than the car
        // can steer.
        const double SuspiciousReachM = 0.3;

        double? Reach4(double px, double py, double nx, double ny, double sign)
        {
            // Re-measure one direction with the game's actual rule: bisect
            // over offsets while testing whether a full car (four corners
            // within tolerance of the raw pavement — exactly what
            // IsCarOnRoad checks) fits with its rear axle at P + o*N,
            // heading along the tangent. ~13x slower than the ray, so only
            // used for suspicious readings.
            double h = Math.Atan2(-ny, nx);       // forward = (sin h, cos h) = tangent
            double fx = Math.Sin(h), fy = Math.Cos(h);
            double rx = Math.Cos(h), ry = -Math.Sin(h);
            double hl = 4.5 / 2.0 * PPPM;         // IsCarOnRoad's body defaults
            double hw = Config.CAR_WIDTH / 2.0 * PPPM;

            bool Fits(double o)
            {
                double cx = px + nx * sign * o * PPPM;
                double cy = py + ny * sign * o * PPPM;
                foreach (double sfx in new[] { 1.0, -1.0 })
                {
                    foreach (double srx in new[] { 1.0, -1.0 })
                    {
                        var corner = new Point(cx + sfx * fx * hl + srx * rx * hw,
                                               cy + sfx * fy * hl + srx * ry * hw);
                        if (rawPav.Distance(corner) > tolPx)
                            return false;
                    }
                }
                return true;
            }
            if (!Fits(0.0))
                return null;                  // car doesn't even fit here: keep ray
            double good = 0.0, bad = 8.0;
            for (int it = 0; it < 9; it++)
            {
                double mid = 0.5 * (good + bad);
                if (Fits(mid)) good = mid;
                else bad = mid;
            }
            return good;
        }

        double centreLimit = Config.CAR_WIDTH / 2.0 + Config.LANE_CENTRE_MARGIN_M;

        // Probe the pavement only every `stride` stations and interpolate
        // between: road width changes slowly, but each probe costs a
        // bisection (~18 point-in-polygon tests), and doing that at every
        // station of a dense line dominated the whole route rebuild.
        int n = P.Count;
        int stride = Math.Max(1, (int)(CorridorProbeM / Math.Max(1e-6, SampleM)));
        var knots = new List<int>();
        for (int i = 0; i < n; i += stride) knots.Add(i);
        if (knots[^1] != n - 1) knots.Add(n - 1);

        // tJunc uses the STATION'S OWN normal (N[i]): "keep the junction
        // centre on our left" means the node's lateral position relative to
        // THIS station. The Python original read a stale variable here (the
        // route-END normal at every station) - a bug, not a design choice:
        // on routes whose end direction differs from the approach direction
        // it projects the vector-to-node onto the wrong axis and pushes the
        // line out by up to half a metre for a single station right before
        // the node (measured +0.49 m spike at fig8_cross C). That kink cut
        // the speed profile to ~2.7 m/s, stalled slow cars mid-crossing via
        // the steering-demand throttle, and gridlocked the signed 50-car
        // two-way demo. Fidelity to the reference bug is not worth a
        // deadlock - fixed 2026-09-08.

        var probe = new Dictionary<int, (double?, double?)>();
        foreach (int i in knots)
        {
            double px = P[i].X, py = P[i].Y;
            double nx = N[i].X, ny = N[i].Y;
            (double?, double?) r = (Reach(px, py, nx, ny, +1.0), Reach(px, py, nx, ny, -1.0));
            // Suspicious reading -> ground-truth re-measurement of that side
            // only (a few knots per route at most; ~1 ms each).
            for (int k = 0; k < 2; k++)
            {
                double? v0 = r.Item1, v1 = r.Item2;
                double? vk = k == 0 ? v0 : v1;
                if (vk is not null && vk.Value < SuspiciousReachM)
                {
                    double? v = Reach4(px, py, nx, ny, k == 0 ? +1.0 : -1.0);
                    if (v is not null)
                        r = k == 0 ? (v, v1) : (v0, v);
                }
            }
            probe[i] = r;
        }

        double Probed(int i, int k, double fallback)
        {
            var (r0, r1) = probe[i];
            double? v = k == 0 ? r0 : r1;
            return v ?? fallback;
        }

        var lo = new double[n];
        var hi = new double[n];
        var nodeIdxCache = new Dictionary<(double X, double Y), int>();
        for (int i = 0; i < n; i++)
        {
            int a = knots[Math.Max(0, Math.Min(knots.Count - 2, i / stride))];
            int b = knots[Math.Min(knots.Count - 1, knots.IndexOf(a) + 1)];
            // Cap by the CARRIAGEWAY, not by whatever tarmac happens to be
            // reachable along the normal. At a T-junction the perpendicular
            // runs straight down the crossing road, so an uncapped probe
            // reports several metres of "corridor" out into the intersection
            // and the optimiser happily routes the line through it — which is
            // both illegal and, being a huge lateral excursion, far tighter
            // than the car can steer.
            var (onewayI, widthI, _lanesI, _parkI) = stationProps[i];
            double laneCap = Config.KerbOffsetM(widthI);
            double fNear = 1.0;   // centreline-bound ease factor near a junction node
            // Inside a junction the carriageway cap does not apply: the paved
            // area is the whole intersection, and a left-turner NEEDS that
            // outside room to get around the centre dot rather than cutting
            // across it. Capping to a single road's width here (while also
            // relaxing the inside bound) had it exactly inverted — it squeezed
            // the car towards the middle, the one place it must not go.
            (double X, double Y)? jn = junctionNodes is not null ? junctionNodes[i] : null;
            bool atJunction = jn is not null;
            if (atJunction)
                laneCap += JunctionExtraRoomM;
            // Take the MINIMUM of the bracketing probes, never the average: an
            // interpolated bound may not be a real one, and overstating the
            // corridor by even a few centimetres puts a wheel off the pavement.
            // Where a probe gave no reading, fall back to the nominal
            // carriageway — the probe refines the lane geometry, it is not the
            // only source of it.
            double right = Math.Min(Math.Min(Probed(a, 0, laneCap), Probed(b, 0, laneCap)), laneCap);
            double left = Math.Min(Math.Min(Probed(a, 1, laneCap), Probed(b, 1, laneCap)), laneCap);
            double hgt = right;
            // Keep the junction centre on our LEFT. The node's lateral position
            // relative to this station is t (positive = node lies to the
            // right); the car sits at lateral o, so the node is on our left
            // exactly when o > t. Add half the car's width plus a margin so it
            // is the whole body that clears it, not just the centreline.
            //
            // This protects against ONCOMING traffic, so it only applies where
            // there is some: on a one-way carriageway there is no oncoming
            // lane, and the node is kept clear naturally (straight and right
            // turns pass it on the left; left turns are already excluded by
            // JunctionNodePerStation). Applying it on one-way stations used to
            // force the line ~0.9 m kerbward within 14 m of every junction —
            // measured on the 3.5 m oneway x oneway crossing (test #10): base
            // offset 0.5 m -> forced 1.4 m, an S-bend the car had to steer
            // through at junction-entry speed instead of driving straight.
            //
            // The bound EASES over LANE_EASE_M/2 on both sides of the node: a
            // straight-through lane change (narrow one-way <-> wide two-way)
            // has to cross the centreline region right at the crossing — the
            // pavement only allows the shift inside the intersection square —
            // so a full-strength bound stepping in exactly at the node would
            // force a visible kink there. Before the node it eases down to the
            // far side's nominal position (the diagonal's arrival value), after
            // it ramps up from 0; on constant-width routes the eased bound
            // stays below the nominal line, so nothing changes there.
            bool atJuncTwoWay = jn is not null && !onewayI;
            double tJunc = 0.0;
            if (atJuncTwoWay)
            {
                var (qx, qy) = jn!.Value;   // non-null: atJuncTwoWay implies it
                tJunc = ((qx - P[i].X) * N[i].X + (qy - P[i].Y) * N[i].Y) / PPPM;
                if (!nodeIdxCache.TryGetValue((qx, qy), out int kq))
                {
                    kq = NodeRouteIndex((qx, qy), P);
                    nodeIdxCache[(qx, qy)] = kq;
                }
                double dAfter = (i - kq) * SampleM;
                double ease = LaneEaseM / 2.0;
                if (dAfter >= -ease && dAfter < 0)
                {
                    // approach side: blend full -> far side's nominal position
                    var (owF, wF, lF, pF) = stationProps[Math.Min(n - 1, kq + 1)];
                    double floor = Math.Min(Math.Min(
                        Config.LaneBaseOffsetM(wF, lF, pF, owF),
                        Config.KerbOffsetM(wF)), centreLimit);
                    fNear = 1.0 - (1.0 - floor / centreLimit) * (1.0 + dAfter / ease);
                }
                else if (dAfter >= 0 && dAfter < ease)
                {
                    // exit side: ramp up from 0
                    fNear = dAfter / ease;
                }
            }
            // One-way roads have no oncoming lane, so the centreline bound does
            // not apply; everywhere else it does, scaled by f_near near a node.
            double lft = onewayI ? -left : centreLimit * fNear;
            if (atJuncTwoWay)
                lft = Math.Max(lft, tJunc + centreLimit * fNear);
            if (lft > hgt)                          // degenerate / very narrow
            {
                double v = Math.Max(0.0, Math.Min(lft, hgt));
                lft = hgt = v;
            }
            lo[i] = lft;
            hi[i] = hgt;
        }
        return (lo, hi);
    }

    /// <summary>Station index nearest to junction node q along the route.</summary>
    private static int NodeRouteIndex((double X, double Y) q, List<(double X, double Y)> P)
    {
        int best = 0;
        double bd = double.PositiveInfinity;
        for (int k = 0; k < P.Count; k++)
        {
            double d2 = (P[k].X - q.X) * (P[k].X - q.X) + (P[k].Y - q.Y) * (P[k].Y - q.Y);
            if (d2 < bd)
            {
                bd = d2;
                best = k;
            }
        }
        return best;
    }

    /// <summary>Per-station nominal lane offset: each station's own road's
    /// normal position (LaneBaseOffsetM clamped to its kerb offset), with a
    /// STRAIGHT diagonal across every point where the nominal changes — a
    /// human changes lanes on a straight line through the crossing, not in
    /// an S-curve at the node. On constant-width routes this is just a
    /// constant (= the old global base_offset), so behaviour there is
    /// unchanged.
    ///
    /// The diagonal is placed so clamping never leaves a step: when the
    /// approach position W sits below the far side's lower bound (the
    /// centreline protection), it starts where the pavement first allows
    /// rising and runs straight on to the exit nominal; when W sits above
    /// the far side's cap, it reaches that cap exactly at the node, where
    /// the bound may then step without a visible kink.</summary>
    public static double[] AutoBaseProfile(StationProps[] props, List<double> S,
        double[]? lo = null, double[]? hi = null)
    {
        int n = S.Count;
        var raw = new double[n];
        for (int i = 0; i < n; i++)
        {
            var (ow, w, l, p) = props[i];
            raw[i] = Math.Min(Config.LaneBaseOffsetM(w, l, p, ow), Config.KerbOffsetM(w));
        }
        var outP = (double[])raw.Clone();
        double half = LaneEaseM / 2.0;
        for (int i = 1; i < n; i++)
        {
            if (Math.Abs(raw[i] - raw[i - 1]) < 1e-9) continue;
            double s0 = S[i] - half, s1 = S[i] + half;
            double W = raw[i - 1], E = raw[i];
            double farLo = -1e9;
            if (lo is not null)
                for (int j = 0; j < n; j++)
                    if (S[i] <= S[j] && S[j] < s1) farLo = Math.Max(farLo, lo[j]);
            double farHi = 1e9;
            if (hi is not null)
                for (int j = 0; j < n; j++)
                    if (S[i] <= S[j] && S[j] < s1) farHi = Math.Min(farHi, hi[j]);
            double sa = s0, sb = s1, vEnd = E;
            if (W < farLo)              // must rise across the crossing...
            {
                for (int j = i - 1; j >= 0; j--)
                {
                    if (S[j] < s0) break;
                    if (hi is not null && hi[j] < farLo - 1e-9)
                    {
                        sa = S[j];      // ...starting where room first exists
                        break;
                    }
                }
            }
            else if (W > farHi)         // must descend to the far cap...
            {
                // Tolerance: hi comes from the eroded-pavement probe, which
                // reports a few cm INSIDE the nominal kerb cap that E is
                // clamped to — compare with slack, not equality.
                if (E <= farHi + 0.25)
                    sb = S[i];          // ...reached exactly at the node
            }
            for (int j = 0; j < n; j++)
            {
                if (sa <= S[j] && S[j] < sb)
                {
                    double t = (S[j] - sa) / Math.Max(1e-9, sb - sa);
                    outP[j] = W + (vEnd - W) * t;
                }
            }
        }
        return outP;
    }

    // --- Route-geometry memoization ---------------------------------------
    // SolveLine is expensive (25 ms steady-state, ~250 ms cold) but its
    // result depends only on the route GEOMETRY + nominal profile — never
    // on the car. On a fixed map the set of distinct route windows is small
    // (fig8: ~48 nine-segment windows), so after warm-up every rebuild is a
    // cache hit and the per-car steady-state tax (~30 ms every ~10 s) drops
    // to tens of microseconds. Spawn bursts collapse too: 576 cars x 250 ms
    // = ~2.5 min of one-shot work becomes one solve per distinct window.
    // Callers may mutate the returned lists freely — the cache hands out
    // copies (the stored originals are owned by the cache).

    private const int SolveCacheMax = 1024;
    private static readonly object _cacheLock = new();
    private static readonly Dictionary<SolveCacheKey, SolveOutput> _solveCache = new();
    private static readonly Queue<SolveCacheKey> _solveCacheOrder = new();   // FIFO eviction
    private static int _hits, _misses;

    /// <summary>{'hits': n, 'misses': m, 'size': k} — for perf diagnostics.</summary>
    public static (int Hits, int Misses, int Size) CacheStats()
    {
        lock (_cacheLock) return (_hits, _misses, _solveCache.Count);
    }

    public static void ClearCache()
    {
        lock (_cacheLock)
        {
            _solveCache.Clear();
            _solveCacheOrder.Clear();
            _hits = 0;
            _misses = 0;
        }
    }

    /// <summary>Cache key: quantised route geometry + nominal profile.
    /// Quantize geometry to ~0.5 mm (world px). The rounded polyline is
    /// deterministic per map; quantizing only guards against float drift
    /// between rebuild paths that should produce identical geometry.</summary>
    private sealed class SolveCacheKey : IEquatable<SolveCacheKey>
    {
        public long[] Q { get; }
        public double Ds { get; }
        public double? BaseOffset { get; }
        public bool AutoBase { get; }
        public double? MergeFromM { get; }
        public double MergeS0 { get; }
        public double MergeS1 { get; }

        public SolveCacheKey(List<(double X, double Y)> rounded, double ds,
            double? baseOffset, bool autoBase, double? mergeFromM,
            double mergeS0, double mergeS1)
        {
            Q = new long[rounded.Count * 2];
            for (int i = 0; i < rounded.Count; i++)
            {
                // Math.Round's default is ToEven — Python's round() too.
                Q[2 * i] = (long)Math.Round(rounded[i].X * 2048.0);
                Q[2 * i + 1] = (long)Math.Round(rounded[i].Y * 2048.0);
            }
            Ds = ds;
            BaseOffset = baseOffset is null ? null : Math.Round(baseOffset.Value, 3);
            AutoBase = autoBase;
            MergeFromM = mergeFromM is null ? null : Math.Round(mergeFromM.Value, 3);
            MergeS0 = Math.Round(mergeS0, 3);
            MergeS1 = Math.Round(mergeS1, 3);
        }

        public bool Equals(SolveCacheKey? other)
        {
            if (other is null) return false;
            return Ds == other.Ds && AutoBase == other.AutoBase
                && BaseOffset == other.BaseOffset && MergeFromM == other.MergeFromM
                && MergeS0 == other.MergeS0 && MergeS1 == other.MergeS1
                && Q.SequenceEqual(other.Q);
        }

        public override bool Equals(object? obj) => Equals(obj as SolveCacheKey);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = Q.Length;
                foreach (long v in Q) h = (h * 397) ^ (int)(v ^ (v >> 32));
                h ^= BitConverter.DoubleToInt64Bits(Ds).GetHashCode();
                h ^= AutoBase ? 1 : 0;
                if (BaseOffset is not null) h ^= BitConverter.DoubleToInt64Bits(BaseOffset.Value).GetHashCode() + 19;
                if (MergeFromM is not null) h ^= BitConverter.DoubleToInt64Bits(MergeFromM.Value).GetHashCode() + 23;
                h ^= BitConverter.DoubleToInt64Bits(MergeS0).GetHashCode();
                h ^= BitConverter.DoubleToInt64Bits(MergeS1).GetHashCode();
                return h;
            }
        }
    }

    /// <summary>The fastest legal line for a route, as a dense uniform
    /// polyline.
    ///
    /// Memoized on (route geometry, nominal profile) — see the cache above:
    /// the solve is a property of the CURVE, not of any car, so every car
    /// sharing a route window reuses one solution. Returns fresh copies.
    ///
    /// `baseOffset` overrides the nominal lane offset used to pin straight
    /// sections (see MinCurvatureOffsets); default is the normal right-lane
    /// centre. The nav passes the car's spawn lateral position here so the
    /// car holds its initial line instead of re-centering (docs §1 variant).
    ///
    /// `autoBase`: per-station nominal instead — each station settles at its
    /// own road's normal position, eased over LANE_EASE_M across road changes
    /// (see AutoBaseProfile). Used for normal driving so that a one-way ->
    /// two-way crossing shifts lanes gradually instead of in a step at the
    /// node.
    ///
    /// Merge-right blend (docs §1 variant on multi-lane roads): with
    /// `mergeFromM` given, stations before `mergeS0` settle at `mergeFromM`
    /// (the spawn lane), stations from `mergeS1` on at `baseOffset`, and
    /// between them a smoothstep — the human-like "change lanes right first,
    /// then park" instead of holding the overtaking lane all the way to the
    /// kerb.
    ///
    /// Returns points + normals + offsets + cum sampled every ~ds metres.</summary>
    public static SolveOutput SolveLine(
        RoadNetwork network,
        List<(double X, double Y)> rounded,
        List<int> routeSegIdx,
        double ds = SampleM,
        double? baseOffset = null,
        bool autoBase = false,
        double? mergeFromM = null,
        double mergeS0 = 0.0,
        double mergeS1 = 0.0)
    {
        var key = new SolveCacheKey(rounded, ds, baseOffset, autoBase,
                                    mergeFromM, mergeS0, mergeS1);
        lock (_cacheLock)
        {
            if (_solveCache.TryGetValue(key, out var hit))
            {
                _hits++;
                return CopyOf(hit);
            }
        }
        var result = SolveLineImpl(network, rounded, routeSegIdx, ds, baseOffset,
                                   autoBase, mergeFromM, mergeS0, mergeS1);
        lock (_cacheLock)
        {
            _misses++;
            if (_solveCache.Count >= SolveCacheMax)
                _solveCache.Remove(_solveCacheOrder.Dequeue());   // FIFO eviction
            _solveCache[key] = result;
            _solveCacheOrder.Enqueue(key);
        }
        return CopyOf(result);
    }

    private static SolveOutput CopyOf(SolveOutput r) => new()
    {
        Points = new List<(double X, double Y)>(r.Points),
        Normals = new List<(double X, double Y)>(r.Normals),
        Offsets = (double[])r.Offsets.Clone(),
        Cum = (double[])r.Cum.Clone(),
    };

    /// <summary>The fastest legal line for a route — the uncached workhorse
    /// (see SolveLine for the parameter documentation).
    ///
    /// Sampling uniformly and densely is not cosmetic. The reference line is
    /// a polyline, and curvature is measured over a fixed 1 m window: if the
    /// vertices are ~1 m apart and the lateral offset shifts appreciably
    /// between them, each vertex reads as a kink whose apparent radius is far
    /// tighter than the line really is — tight enough to fall below the car's
    /// 3.46 m mechanical minimum and make the speed profile crawl. Dense
    /// uniform stations keep every vertex-to-vertex bend small.</summary>
    public static SolveOutput SolveLineImpl(
        RoadNetwork network,
        List<(double X, double Y)> rounded,
        List<int> routeSegIdx,
        double ds = SampleM,
        double? baseOffset = null,
        bool autoBase = false,
        double? mergeFromM = null,
        double mergeS0 = 0.0,
        double mergeS1 = 0.0)
    {
        // How far the driven line may deviate from the nominal lane position.
        // The curvature objective alone lets the solver abandon its lane by a
        // metre or more wherever that widens an arc (measured on the
        // roundabout entry: offset swung +1.2 -> -0.48 across a 7 m corner —
        // 1.9 m of lateral swing in one turn, which no pure-pursuit controller
        // can follow at entry speed; the car drifted ~1 m inside and clipped
        // the kerb). A human stays in their lane through the crossing and lets
        // the corner be a metre or two tighter; that is what this band encodes.
        const double LaneBandM = 0.5;

        var (P, S, total) = Resample(rounded, ds);
        var props = StationSegments(network, routeSegIdx, P);
        var (N, K) = NormalsAndCurvature(P, ds);
        var junction = JunctionNodePerStation(network, P);
        var (lo, hi) = LegalCorridor(network, P, N, props, junction);

        double[] baseProf;
        BaseNominal baseNom;
        if (autoBase)
        {
            baseProf = AutoBaseProfile(props, S, lo, hi);
            baseNom = BaseNominal.OfProfile(baseProf);
        }
        else if (baseOffset is not null)
        {
            // e2e spawns set lane_offset_override_m so the car holds its
            // spawn line — a SCALAR nominal that does not follow road changes.
            baseProf = new double[S.Count];
            Array.Fill(baseProf, baseOffset.Value);
            baseNom = BaseNominal.OfScalar(baseOffset.Value);
        }
        else
        {
            baseProf = new double[S.Count];
            Array.Fill(baseProf, Math.Min(Config.LANE_OFFSET_DEFAULT_M,
                                          Config.KerbOffsetM(MinWidth(network, routeSegIdx))));
            baseNom = BaseNominal.OfScalar(baseProf[0]);
        }
        // Band the corridor around the nominal profile BEFORE solving: the
        // optimiser then stays smooth (it eases along a bound it cannot
        // cross), whereas clamping the solved offsets afterwards kinks the
        // line wherever the solution crosses the band edge (measured: R = 2.1 m
        // kink on the roundabout entry). Feasibility guard: where base +/- band
        // lies outside the corridor (a wide-road spawn nominal on a narrow
        // ring), fall back to the corridor itself instead of inverting it.
        if (mergeFromM is null)
        {
            for (int i = 0; i < lo.Length; i++)
            {
                lo[i] = Math.Max(lo[i], Math.Min(hi[i], baseProf[i] - LaneBandM));
                hi[i] = Math.Min(hi[i], Math.Max(lo[i], baseProf[i] + LaneBandM));
            }
        }
        double settled = baseNom.Profile is not null ? baseNom.Profile[^1] : baseNom.Scalar!.Value;
        if (mergeFromM is not null && Math.Abs(mergeFromM.Value - settled) > 1e-6
            && mergeS1 > mergeS0)
        {
            double m0 = mergeS0, m1 = mergeS1;
            double span = Math.Max(1e-6, m1 - m0);
            double oEnd = settled;      // the settled value at the line's end
            var prof = new double[S.Count];
            for (int i = 0; i < S.Count; i++)
            {
                double s_ = S[i];
                if (s_ <= m0) prof[i] = mergeFromM.Value;
                else if (s_ >= m1) prof[i] = oEnd;
                else
                {
                    double t = (s_ - m0) / span;
                    t = t * t * (3.0 - 2.0 * t);
                    prof[i] = mergeFromM.Value + (oEnd - mergeFromM.Value) * t;
                }
            }
            baseNom = BaseNominal.OfProfile(prof);
        }
        if (total < 4.0 || P.Count < 5)
        {
            var o = baseNom.Profile is not null
                ? (double[])baseNom.Profile.Clone()
                : new double[P.Count];
            if (baseNom.Profile is null)
                Array.Fill(o, baseNom.Scalar!.Value);
            return new SolveOutput { Points = P, Normals = N, Offsets = o, Cum = S.ToArray() };
        }
        // A straight route needs no curvature solving: a constant offset has
        // ZERO curvature, which is already the optimum. Running the solver
        // there would SMOOTH a per-station base profile — it minimises the
        // profile's own second derivative, stretching the merge blend from its
        // planned ~35 m zone to ~180 m and turning a brisk lane change into a
        // crawl (measured: 8.35->5.25 over [75,110] came back as 7.7@45,
        // 6.2@110, still converging at 120). The solver only earns its keep
        // where the road actually curves.
        double maxK = 0.0;
        foreach (double k in K) maxK = Math.Max(maxK, Math.Abs(k));
        if (maxK < 1e-4)
        {
            var o = new double[K.Length];
            for (int i = 0; i < K.Length; i++)
            {
                double? bb = baseNom.At(i);
                o[i] = Math.Max(lo[i], Math.Min(hi[i], bb ?? 0.5 * (lo[i] + hi[i])));
            }
            return new SolveOutput { Points = P, Normals = N, Offsets = o, Cum = S.ToArray() };
        }
        var o2 = MinCurvatureOffsets(K, lo, hi, ds, baseNominal: baseNom);
        return new SolveOutput { Points = P, Normals = N, Offsets = o2, Cum = S.ToArray() };
    }

    /// <summary>Lay offsets onto their stations -> the driven polyline (world px).</summary>
    public static List<(double X, double Y)> PointsFromOffsets(
        List<(double X, double Y)> P, List<(double X, double Y)> N, double[] offsets)
    {
        var outP = new List<(double X, double Y)>(P.Count);
        for (int i = 0; i < P.Count; i++)
            outP.Add((P[i].X + N[i].X * offsets[i] * PPPM,
                      P[i].Y + N[i].Y * offsets[i] * PPPM));
        return outP;
    }

    /// <summary>Minimum road width (m) over the route segments.</summary>
    public static double MinWidth(RoadNetwork network, List<int> routeSegIdx)
    {
        double w = double.PositiveInfinity;
        foreach (int i in routeSegIdx) w = Math.Min(w, network.Segments[i].Width);
        return w == double.PositiveInfinity ? 7.0 : w;
    }

    /// <summary>Nearest route segment for each station ->
    /// (oneway, width, lanes, parking_lane_width) tuples.</summary>
    public static StationProps[] StationSegments(
        RoadNetwork network, List<int> routeSegIdx, List<(double X, double Y)> P)
    {
        var segs = new List<RoadSegment>();
        foreach (int i in routeSegIdx) segs.Add(network.Segments[i]);
        if (segs.Count == 0) segs.AddRange(network.Segments);

        var outP = new StationProps[P.Count];
        for (int i = 0; i < P.Count; i++)
        {
            double px = P[i].X, py = P[i].Y;
            RoadSegment? best = null;
            double bd = double.PositiveInfinity;
            foreach (var sg in segs)
            {
                double dx = sg.X2 - sg.X1, dy = sg.Y2 - sg.Y1;
                double L2 = dx * dx + dy * dy;
                if (L2 < 1e-9) continue;
                double t = Math.Max(0.0, Math.Min(1.0,
                    ((px - sg.X1) * dx + (py - sg.Y1) * dy) / L2));
                double d = Math.Hypot(px - (sg.X1 + t * dx), py - (sg.Y1 + t * dy));
                if (d < bd)
                {
                    bd = d;
                    best = sg;
                }
            }
            outP[i] = best is not null
                ? new StationProps(best.Oneway, best.Width, best.Lanes, best.ParkingLaneWidth)
                : new StationProps(false, 7.0, 0, 0.0);
        }
        return outP;
    }
}
