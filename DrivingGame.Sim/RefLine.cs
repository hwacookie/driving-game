// Arc-length-parameterized route geometry for BicycleNav.
// 1:1 port of the module-level helpers in car/src/bicycle_nav.py
// (RefLine, project_s, _refine_project, _offset_polyline_right[_varying],
// _park_ease[_slope]).

namespace DrivingGame.Sim;

public sealed class RefLine
{
    /// <summary>Polyline vertices, world pixels.</summary>
    public List<(double X, double Y)> Pts { get; }
    public List<double> SegLen { get; } = new();
    public List<double> Cum { get; } = new() { 0.0 };
    public double Total { get; private set; }

    public RefLine(List<(double X, double Y)> pts)
    {
        Pts = pts;
        for (int i = 0; i < pts.Count - 1; i++)
        {
            double d = Math.Hypot(pts[i + 1].X - pts[i].X, pts[i + 1].Y - pts[i].Y) / RefLineMath.PPPM;
            SegLen.Add(d);
            Cum.Add(Cum[^1] + d);
        }
        Total = Cum.Count > 0 ? Cum[^1] : 0.0;
    }

    /// <summary>World-pixel position at arc length s (clamped).</summary>
    public (double X, double Y) PointAt(double s)
    {
        s = Math.Max(0.0, Math.Min(Total, s));
        // Bisect over the monotone cumulative lengths (O(log n)): this is
        // called ~60x per car per substep from ProjectS, and the old linear
        // scan dominated multi-car frame time at hundreds of cars.
        int i = Math.Max(0, Math.Min(BisectRight(Cum, s) - 1, SegLen.Count - 1));
        double t = SegLen[i] > 0 ? (s - Cum[i]) / SegLen[i] : 0.0;
        return (Pts[i].X + t * (Pts[i + 1].X - Pts[i].X),
                Pts[i].Y + t * (Pts[i + 1].Y - Pts[i].Y));
    }

    public double HeadingAt(double s)
    {
        s = Math.Max(0.0, Math.Min(Total - 1e-6, s));
        int i = MathMax0Min(BisectRight(Cum, s) - 1, SegLen.Count - 1);
        double dx = Pts[i + 1].X - Pts[i].X;
        double dy = Pts[i + 1].Y - Pts[i].Y;
        return Math.Degrees(Math.Atan2(dx, dy));
    }

    /// <summary>Signed curvature (1/m) at arc length s (positive = right turn).</summary>
    public double CurvatureAt(double s)
    {
        // Fixed physical window - see Config.CURVATURE_WINDOW_M. A window
        // proportional to route length blurs short corners away entirely.
        double h = Config.CURVATURE_WINDOW_M;
        double s1 = Math.Max(0.0, s - h);
        double s2 = Math.Min(Total, s + h);
        if (s2 - s1 < 1e-3) return 0.0;
        double h1 = Math.Radians(HeadingAt(s1));
        double h2 = Math.Radians(HeadingAt(s2));
        // Two-step normalization, NOT the naive `(h2-h1+π) % 2π - π`: .NET's
        // float % keeps the dividend's sign (CPython's keeps the divisor's),
        // so when two near-identical headings straddle atan2's ±π branch cut
        // (a road running exactly due south samples as +π−δ on one side and
        // −π+δ on the other - ulp-level x jitter decides which, and .NET's FMA
        // contraction makes it differ from CPython) the naive form returns ∓2π
        // instead of ~0, spiking curvature to ±2π/m on a perfectly straight
        // line. That corrupted the speed profile (the 1 m worst-curvature cap
        // read the spike) and made the reachability check in MaybeRebuild
        // declare signalled turns unreachable -> the car slid past junctions.
        double dh = (h2 - h1) % (2 * Math.PI);
        if (dh > Math.PI) dh -= 2 * Math.PI;
        else if (dh < -Math.PI) dh += 2 * Math.PI;
        return dh / (s2 - s1);
    }

    private static int MathMax0Min(int a, int b) => Math.Max(0, Math.Min(a, b));

    /// <summary>Python bisect.bisect_right over a monotone list.</summary>
    public static int BisectRight(List<double> list, double value)
    {
        int lo = 0, hi = list.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (list[mid] <= value) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }
}

public static class RefLineMath
{
    public const double PPPM = Config.PIXELS_PER_METER;

    /// <summary>How much closer an off-route segment must be before it
    /// displaces the route we are actually following (see
    /// BicycleNav.SyncSegment).</summary>
    public const double ROUTE_STICKINESS_PX = 3.0 * PPPM;

    /// <summary>Per-frame parking / route-cut tracing. Off by default: these
    /// fire on EVERY frame of a pull-over and drowned the console (and any
    /// test log) during the manoeuvre. Set CAR_DEBUG_PARK=1 to get them back.</summary>
    public static readonly bool PARK_DEBUG =
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CAR_DEBUG_PARK"));

    /// <summary>Offset a polyline to the right by a PER-POINT offset (metres).
    /// Same tangent averaging as OffsetPolylineRight, but each point uses its
    /// own offset, so the lane position can vary smoothly along the line.</summary>
    public static List<(double X, double Y)> OffsetPolylineRightVarying(
        List<(double X, double Y)> pts, List<double> offsetsM)
    {
        int n = pts.Count;
        if (n < 2) return new List<(double X, double Y)>(pts);
        var outPts = new List<(double X, double Y)>(n);
        for (int i = 0; i < n; i++)
        {
            double dx, dy;
            if (i == 0)
            {
                dx = pts[1].X - pts[0].X; dy = pts[1].Y - pts[0].Y;
            }
            else if (i == n - 1)
            {
                dx = pts[i].X - pts[i - 1].X; dy = pts[i].Y - pts[i - 1].Y;
            }
            else
            {
                double dxIn = pts[i].X - pts[i - 1].X, dyIn = pts[i].Y - pts[i - 1].Y;
                double dxOut = pts[i + 1].X - pts[i].X, dyOut = pts[i + 1].Y - pts[i].Y;
                double lin = Math.Hypot(dxIn, dyIn); if (lin == 0) lin = 1.0;
                double lout = Math.Hypot(dxOut, dyOut); if (lout == 0) lout = 1.0;
                dx = dxIn / lin + dxOut / lout;
                dy = dyIn / lin + dyOut / lout;
            }
            double length = Math.Hypot(dx, dy); if (length == 0) length = 1.0;
            double rx = dy / length, ry = -dx / length;   // right vector
            double offPx = offsetsM[i] * PPPM;
            outPts.Add((pts[i].X + rx * offPx, pts[i].Y + ry * offPx));
        }
        return outPts;
    }

    /// <summary>Offset a polyline to the RIGHT by offsetM meters, perpendicular
    /// to the local driving direction (right-hand traffic / lane keeping).
    /// The direction at each point is the average of the incoming and outgoing
    /// segment unit-vectors, so the offset is smoothed across corners instead
    /// of jumping. The right vector for a tangent (dx, dy) in the north-up
    /// frame (heading 0 = north, forward = (sin h, cos h)) is (dy, -dx)
    /// normalized - i.e. the forward vector rotated 90 deg clockwise.</summary>
    public static List<(double X, double Y)> OffsetPolylineRight(
        List<(double X, double Y)> pts, double offsetM)
    {
        int n = pts.Count;
        if (n < 2 || offsetM == 0) return new List<(double X, double Y)>(pts);
        double offPx = offsetM * PPPM;
        var outPts = new List<(double X, double Y)>(n);
        for (int i = 0; i < n; i++)
        {
            double dx, dy;
            if (i == 0)
            {
                dx = pts[1].X - pts[0].X; dy = pts[1].Y - pts[0].Y;
            }
            else if (i == n - 1)
            {
                dx = pts[i].X - pts[i - 1].X; dy = pts[i].Y - pts[i - 1].Y;
            }
            else
            {
                double dxIn = pts[i].X - pts[i - 1].X, dyIn = pts[i].Y - pts[i - 1].Y;
                double dxOut = pts[i + 1].X - pts[i].X, dyOut = pts[i + 1].Y - pts[i].Y;
                double lin = Math.Hypot(dxIn, dyIn); if (lin == 0) lin = 1.0;
                double lout = Math.Hypot(dxOut, dyOut); if (lout == 0) lout = 1.0;
                dx = dxIn / lin + dxOut / lout;
                dy = dyIn / lin + dyOut / lout;
            }
            double length = Math.Hypot(dx, dy); if (length == 0) length = 1.0;
            double rx = dy / length, ry = -dx / length;   // right vector
            outPts.Add((pts[i].X + rx * offPx, pts[i].Y + ry * offPx));
        }
        return outPts;
    }

    /// <summary>Lateral profile of the pull-over drift, t = 0 at the start of
    /// the swerve, 1 where the car is at the kerb offset. NOT a symmetric
    /// smoothstep: the binding constraint on how close to the kerb a car can
    /// park is its FRONT CORNER, which swings kerbwards whenever the body is
    /// slanted - and a symmetric profile puts its steepest slant exactly where
    /// the line is already halfway to the kerb, the worst possible place for
    /// it. This profile does the lateral work EARLY, while there is still road
    /// to spare, and arrives at the kerb almost parallel.</summary>
    public static double ParkEase(double t) => t * t * (3.0 - 2.0 * t);

    /// <summary>d/dt of ParkEase (per unit of normalised drift length).</summary>
    public static double ParkEaseSlope(double t) => 6.0 * t * (1.0 - t);

    /// <summary>Trisect [a, b] down to ~1 cm for the closest point on the line.</summary>
    public static double RefineProject(RefLine refLine, double x, double y, double a, double b)
    {
        for (int i = 0; i < 24; i++)
        {
            if (b - a < 0.01) break;
            double m1 = a + (b - a) / 3.0;
            double m2 = b - (b - a) / 3.0;
            var p1 = refLine.PointAt(m1);
            var p2 = refLine.PointAt(m2);
            double d1 = (p1.X - x) * (p1.X - x) + (p1.Y - y) * (p1.Y - y);
            double dd2 = (p2.X - x) * (p2.X - x) + (p2.Y - y) * (p2.Y - y);
            if (d1 < dd2) b = m2; else a = m1;
        }
        return 0.5 * (a + b);
    }

    /// <summary>Project a world point onto the reference line → arc length (m).
    /// Only points within ±window metres of sHint are considered. On a plain
    /// route that is generous; on a folded line (U-turn) the window must be
    /// tight, because the spatially nearest point can belong to a DIFFERENT
    /// branch of the line than the one the car is actually driving on.</summary>
    public static double ProjectS(RefLine refLine, double x, double y, double sHint,
                                  double window = 30.0, bool globalFallback = true,
                                  bool refine = false)
    {
        double bestS = sHint;
        double bestD2 = double.PositiveInfinity;
        double lo = Math.Max(0.0, sHint - window);
        double hi = Math.Min(refLine.Total, sHint + window);
        int steps = 60;
        double step = (hi - lo) / steps;
        for (int k = 0; k <= steps; k++)
        {
            double s = lo + step * k;
            var (px, py) = refLine.PointAt(s);
            double d2 = (px - x) * (px - x) + (py - y) * (py - y);
            if (d2 < bestD2) { bestD2 = d2; bestS = s; }
        }
        if (globalFallback && Math.Sqrt(bestD2) > 25.0 * PPPM)
        {
            for (int k = 0; k < 200; k++)
            {
                double s = refLine.Total * k / 200;
                var (px, py) = refLine.PointAt(s);
                double d2 = (px - x) * (px - x) + (py - y) * (py - y);
                if (d2 < bestD2) { bestD2 = d2; bestS = s; }
            }
        }
        // Optional refinement to centimetres. The coarse scan resolves the line
        // only to ~1 m (30 m window / 60 steps), and that quantisation is not
        // harmless: for the parking plan a staircase in s becomes a staircase
        // in the brake demand; for steering it lets the pursuit target land
        // behind the car in tight corners. Refinement is OFF for the steering
        // projection on purpose - that is a behaviour change for every
        // manoeuvre on the map and is not part of this fix.
        if (refine && step > 0.0)
            bestS = RefineProject(refLine, x, y, Math.Max(0.0, bestS - step),
                                   Math.Min(refLine.Total, bestS + step));
        return bestS;
    }
}
