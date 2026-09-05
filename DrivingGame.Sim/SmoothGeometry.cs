using System;
using System.Collections.Generic;
using System.Linq;

namespace DrivingGame.Sim;

/// <summary>
/// Smoothed geometry — the §10 pipeline (docs/TURN_REWORK_PLAN.md §10).
/// Ported from /Users/hauke/prj/car/src/smooth_geometry.py.
///
/// "The graph is sacred; the curve is a function of the graph". The OSM node
/// set and way topology are never touched. Each merged road line becomes a
/// centripetal Catmull-Rom spline through its ORIGINAL nodes (interpolating,
/// C1). Everything downstream consumes the SAME smoothed geometry: paved
/// polygon = buffer of the smoothed lines + fillet patches; centerline dashes
/// = the smoothed lines; lane markings = offsets; driving reference =
/// sub-curves + fillet arcs. So what is drawn is exactly what is driven and
/// what the on-road check tests.
///
/// Curvature is ALWAYS measured from geometry (central differences of
/// point_at), never from a per-sample table: the first draft stored a
/// per-sample kappa table that was corrupted at every piece junction (a
/// duplicate sample emitted ds = 0 and a heading of 0, spiking kappa to
/// ~16 1/m). The duplicate samples are gone AND curvature_at is geometric.
/// </summary>

public static class SmoothGeometry
{
    private static (double X, double Y) CrPoint(
        (double X, double Y) p0, (double X, double Y) p1,
        (double X, double Y) p2, (double X, double Y) p3,
        double t0, double t1, double t2, double t3, double t)
    {
        // One point on the centripetal Catmull-Rom piece between p1 and p2
        // (Barry-Goldman formulation; alpha=0.5 knot spacing set by caller —
        // what prevents overshoot on irregular node spacing).
        double a1x = (t1 - t) / (t1 - t0) * p0.X + (t - t0) / (t1 - t0) * p1.X;
        double a1y = (t1 - t) / (t1 - t0) * p0.Y + (t - t0) / (t1 - t0) * p1.Y;
        double a2x = (t2 - t) / (t2 - t1) * p1.X + (t - t1) / (t2 - t1) * p2.X;
        double a2y = (t2 - t) / (t2 - t1) * p1.Y + (t - t1) / (t2 - t1) * p2.Y;
        double a3x = (t3 - t) / (t3 - t2) * p2.X + (t - t2) / (t3 - t2) * p3.X;
        double a3y = (t3 - t) / (t3 - t2) * p2.Y + (t - t2) / (t3 - t2) * p3.Y;
        double b1x = (t2 - t) / (t2 - t0) * a1x + (t - t0) / (t2 - t0) * a2x;
        double b1y = (t2 - t) / (t2 - t0) * a1y + (t - t0) / (t2 - t0) * a2y;
        double b2x = (t3 - t) / (t3 - t1) * a2x + (t - t1) / (t3 - t1) * a3x;
        double b2y = (t3 - t) / (t3 - t1) * a2y + (t - t1) / (t3 - t1) * a3y;
        double cx = (t2 - t) / (t2 - t1) * b1x + (t - t1) / (t2 - t1) * b2x;
        double cy = (t2 - t) / (t2 - t1) * b1y + (t - t1) / (t2 - t1) * b2y;
        return (cx, cy);
    }

    /// <summary>
    /// A centripetal Catmull-Rom spline through 2D control points (world
    /// PIXELS), arc-length parameterized in METRES. Interpolating and
    /// C1-continuous. heading: degrees, 0 = north (+y... world Y is DOWN —
    /// matches the codebase convention forward = (sin h, cos h)), positive =
    /// clockwise. curvature: 1/m signed, positive = right turn.
    /// </summary>
    public sealed class SmoothCurve
    {
        public double Pppm { get; }
        public List<(double X, double Y)> Pts { get; }
        public double Total { get; private set; }

        internal readonly List<double> S = new();
        internal readonly List<double> Xs = new();
        internal readonly List<double> Ys = new();
        internal readonly List<double> Hdg = new();

        public SmoothCurve(IEnumerable<(double X, double Y)> pts,
            double pppm = Config.PIXELS_PER_METER, double sampleM = 0.5)
        {
            Pppm = pppm;
            Pts = pts.Select(p => (p.X, p.Y)).ToList();
            int n = Pts.Count;
            if (n < 2)
            {
                Total = 0.0;
                S.Add(0.0);
                Xs.Add(n > 0 ? Pts[0].X : 0.0);
                Ys.Add(n > 0 ? Pts[0].Y : 0.0);
                Hdg.Add(0.0);
                return;
            }

            // Centripetal knot values: alpha = 0.5.
            var t = new List<double> { 0.0 };
            for (int i = 1; i < n; i++)
            {
                double d = Math.Hypot(Pts[i].X - Pts[i - 1].X, Pts[i].Y - Pts[i - 1].Y);
                t.Add(t[^1] + Math.Pow(Math.Max(d, 1e-6), 0.5));
            }

            // Dense arc-length table. Each piece is sampled with a count
            // proportional to its chord length so the table is uniform in arc
            // length to within ~sampleM. Piece i emits k = 0..steps-1 and the
            // final endpoint exactly once — the shared piece junction is NOT
            // emitted twice (the duplicate sample of the first draft corrupted
            // its per-sample curvature table).
            double cum = 0.0;
            (double X, double Y)? prev = null;
            var p0 = default((double X, double Y)); var p1 = default((double X, double Y));
            var p2 = default((double X, double Y)); var p3 = default((double X, double Y));
            double t0 = 0, t1 = 0, t2 = 0, t3 = 0;
            for (int i = 0; i < n - 1; i++)
            {
                p1 = Pts[i]; p2 = Pts[i + 1];
                t1 = t[i]; t2 = t[i + 1];
                if (i >= 1) { p0 = Pts[i - 1]; t0 = t[i - 1]; }
                else { p0 = p1; t0 = t1 - (t2 - t1); } // virtual p0 behind p1
                if (i + 2 < n) { p3 = Pts[i + 2]; t3 = t[i + 2]; }
                else { p3 = p2; t3 = t2 + (t2 - t1); } // virtual p3 ahead of p2
                double chord = Math.Hypot(p2.X - p1.X, p2.Y - p1.Y) / pppm;
                int steps = Math.Max(8, (int)(chord / sampleM) + 1);
                for (int k = 0; k < steps; k++)
                {
                    double tt = t1 + (t2 - t1) * k / steps;
                    var (x, y) = CrPoint(p0, p1, p2, p3, t0, t1, t2, t3, tt);
                    if (prev is null)
                    {
                        // First sample: heading = the clamped start tangent
                        // (the p1->p2 chord direction).
                        Hdg.Add(Math.Atan2(p2.X - p1.X, p2.Y - p1.Y));
                    }
                    else
                    {
                        cum += Math.Hypot(x - prev.Value.X, y - prev.Value.Y) / pppm;
                        Hdg.Add(Math.Atan2(x - prev.Value.X, y - prev.Value.Y));
                    }
                    S.Add(cum); Xs.Add(x); Ys.Add(y);
                    prev = (x, y);
                }
            }
            // Final endpoint (t = end of the last piece), emitted once.
            // prev is set: the sampling loop above ran at least once (n >= 2).
            {
                var (x, y) = CrPoint(p0, p1, p2, p3, t0, t1, t2, t3, t2);
                cum += Math.Hypot(x - prev!.Value.X, y - prev!.Value.Y) / pppm;
                Hdg.Add(Math.Atan2(x - prev!.Value.X, y - prev!.Value.Y));
                S.Add(cum); Xs.Add(x); Ys.Add(y);
            }
            Total = S[^1];
        }

        private int Index(double s)
        {
            // Binary search: largest i with S[i] <= s (clamped).
            int lo = 0, hi = S.Count - 1;
            if (s <= S[0]) return 0;
            if (s >= S[hi]) return hi > 0 ? hi - 1 : 0;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (S[mid] <= s) lo = mid; else hi = mid - 1;
            }
            return lo;
        }

        public (double X, double Y) PointAt(double s)
        {
            if (Total <= 0) return (Xs[0], Ys[0]);
            s = Math.Max(0.0, Math.Min(Total, s));
            int i = Index(s);
            double s0 = S[i], s1 = S[i + 1];
            double f = s1 > s0 ? (s - s0) / (s1 - s0) : 0.0;
            return (Xs[i] + f * (Xs[i + 1] - Xs[i]),
                    Ys[i] + f * (Ys[i + 1] - Ys[i]));
        }

        public double HeadingAt(double s)
        {
            if (Total <= 0) return Hdg[0] * 180.0 / Math.PI;
            s = Math.Max(0.0, Math.Min(Total, s));
            int i = Index(s);
            double s0 = S[i], s1 = S[i + 1];
            double f = s1 > s0 ? (s - s0) / (s1 - s0) : 0.0;
            double h0 = Hdg[i], h1 = Hdg[i + 1];
            return (h0 + f * AngleDelta(h0, h1)) * 180.0 / Math.PI;
        }

        /// <summary>Signed curvature (1/m) at arc length s (positive = right
        /// turn). Geometry-based: central differences of PointAt over a fixed
        /// window. Never a per-sample table.</summary>
        public double CurvatureAt(double s)
        {
            if (Total <= 0) return 0.0;
            // Fixed physical window — see Config.CURVATURE_WINDOW_M.
            double h = Config.CURVATURE_WINDOW_M;
            double s1 = Math.Max(0.0, s - h);
            double s2 = Math.Min(Total, s + h);
            if (s2 - s1 < 1e-3) return 0.0;
            // Tangent heading at each window end from a local chord.
            double e = Math.Min(0.5, (s2 - s1) / 4.0);
            var (ax, ay) = PointAt(Math.Max(0.0, s1 - e));
            var (bx, by) = PointAt(Math.Min(Total, s1 + e));
            double h1 = Math.Atan2(bx - ax, by - ay);
            var (cx, cy) = PointAt(Math.Max(0.0, s2 - e));
            var (dx, dy) = PointAt(Math.Min(Total, s2 + e));
            double h2 = Math.Atan2(dx - cx, dy - cy);
            return AngleDelta(h1, h2) / (s2 - s1);
        }
    }

    /// <summary>Signed shortest angle delta from `from` to `to`, in
    /// [-π, π). The naive `(to - from + π) % 2π - π` breaks when two
    /// near-identical headings straddle atan2's ±π branch cut (a road running
    /// exactly due south samples as +π−δ on one side and −π+δ on the other —
    /// ulp-level x jitter decides which, and .NET's FMA contraction makes it
    /// differ from CPython): it returns ∓2π instead of ~0, spiking curvature
    /// to ±2π/m on a perfectly straight line. The two-step normalization is
    /// robust for any input.</summary>
    private static double AngleDelta(double from, double to)
    {
        double dh = (to - from) % (2 * Math.PI);
        if (dh > Math.PI) dh -= 2 * Math.PI;
        else if (dh < -Math.PI) dh += 2 * Math.PI;
        return dh;
    }

    /// <summary>Dense polyline (world pixels) of the curve, resampled every
    /// stepM of arc length. A render/eval cache — defines no new geometry.</summary>
    public static List<(double X, double Y)> ResampleCurve(SmoothCurve curve, double stepM = 0.5)
    {
        if (curve.Total <= 0) return new() { curve.PointAt(0.0) };
        int n = Math.Max(2, (int)(curve.Total / stepM) + 1);
        var pts = new List<(double X, double Y)>(n);
        for (int i = 0; i < n; i++)
            pts.Add(curve.PointAt(curve.Total * i / (n - 1)));
        return pts;
    }

    public sealed record CornerFilletResult(
        (double X, double Y) T1, (double X, double Y) T2,
        List<(double X, double Y)> Arc, double ActualRadius, double TangentDist);

    /// <summary>Circular fillet of `radius` (pixels) tangent to both edges of
    /// the corner at `vertex` — the exact math _round_polyline_corners uses
    /// (the single shared implementation). Returns null if there is no real
    /// corner (straight-through, doubled-back, or degenerate edges).</summary>
    public static CornerFilletResult? CornerFillet(
        (double X, double Y) prevPt, (double X, double Y) vertex,
        (double X, double Y) nextPt, double radius, int arcSteps = 10)
    {
        double px = prevPt.X, py = prevPt.Y;
        double vx = vertex.X, vy = vertex.Y;
        double nx = nextPt.X, ny = nextPt.Y;

        double ax = px - vx, ay = py - vy;
        double bx = nx - vx, by = ny - vy;
        double aLen = Math.Hypot(ax, ay), bLen = Math.Hypot(bx, by);
        if (aLen < 1e-9 || bLen < 1e-9) return null;
        ax /= aLen; ay /= aLen; bx /= bLen; by /= bLen;

        // Angle between the two edges (both pointing away from the vertex).
        double dot = Math.Clamp(ax * bx + ay * by, -1.0, 1.0);
        double gap = Math.Acos(dot);
        if (gap < 1e-6 || gap > Math.PI - 1e-6) return null;

        double halfGap = gap / 2;
        double tangentDist = radius / Math.Tan(halfGap);
        // Cap so it never eats more than half of either adjoining edge.
        tangentDist = Math.Min(tangentDist, Math.Min(aLen / 2, bLen / 2));
        double actualRadius = tangentDist * Math.Tan(halfGap);

        double centerDist = actualRadius / Math.Sin(halfGap);
        double bisX = ax + bx, bisY = ay + by;
        double bisLen = Math.Hypot(bisX, bisY);
        if (bisLen < 1e-9) return null;
        bisX /= bisLen; bisY /= bisLen;

        double centerX = vx + centerDist * bisX;
        double centerY = vy + centerDist * bisY;
        var t1 = (X: vx + tangentDist * ax, Y: vy + tangentDist * ay);
        var t2 = (X: vx + tangentDist * bx, Y: vy + tangentDist * by);

        double angleT1 = Math.Atan2(t1.Y - centerY, t1.X - centerX);
        double angleT2 = Math.Atan2(t2.Y - centerY, t2.X - centerX);

        // The correct arc sweep is always the EXTERIOR turn angle (pi - gap).
        // Of the two rotation directions from t1 to t2, pick whichever size
        // matches. t1 must ALWAYS be emitted before t2 — swapping self-
        // intersects the line.
        double fwdSweep = (angleT2 - angleT1) % (2 * Math.PI);
        if (fwdSweep < 0) fwdSweep += 2 * Math.PI;
        double expectedSweep = Math.PI - gap;
        int direction; double sweep;
        if (Math.Abs(fwdSweep - expectedSweep) < Math.Abs(2 * Math.PI - fwdSweep - expectedSweep))
            (direction, sweep) = (1, fwdSweep);
        else
            (direction, sweep) = (-1, 2 * Math.PI - fwdSweep);

        var arc = new List<(double X, double Y)> { t1 };
        for (int s = 1; s <= arcSteps; s++)
        {
            double a = angleT1 + direction * sweep * (s / (double)arcSteps);
            arc.Add((centerX + actualRadius * Math.Cos(a),
                     centerY + actualRadius * Math.Sin(a)));
        }
        return new CornerFilletResult(t1, t2, arc, actualRadius, tangentDist);
    }

    // --- SmoothedNetwork -----------------------------------------------------

    /// <summary>One merged road line with its spline + resampled polyline.</summary>
    public sealed record SmoothedLine(
        string Highway, double Width,
        List<(double X, double Y)> Coords,
        SmoothCurve Curve,
        List<(double X, double Y)> Resampled);

    /// <summary>(seg_idx, direction) → the exact sub-curve of a segment's
    /// line. Line is null for degenerate (zero-length/unmatched) segments,
    /// which get a trivial straight curve so callers always get one.</summary>
    public sealed class SegmentCurveRef
    {
        public SmoothedLine? Line;
        public required SmoothCurve Curve;
        public double S0;
        public double S1;
    }

    /// <summary>Eckausrundung at one junction corner: the corner point, a
    /// circular arc of JUNCTION_CORNER_RADIUS_M tangent to both road edges,
    /// and the data needed to build the paved fill between them.</summary>
    public sealed record JunctionFillet(
        string Node, int SegA, int SegB,
        (double X, double Y) Corner,
        List<(double X, double Y)> Arc,
        double RadiusPx);

    /// <summary>The §10 smoothed geometry of one RoadNetwork, built once and
    /// cached. See the Python source for the full rationale.</summary>
    public sealed class SmoothedNetwork
    {
        public const double MinGapDeg = 15.0;
        public const double MaxGapDeg = 155.0;

        public RoadNetwork Network { get; }
        public double Pppm { get; }
        public List<SmoothedLine> Lines { get; } = new();
        public Dictionary<(int SegIdx, bool Forward), SegmentCurveRef> SegmentCurve { get; }
            = new();
        public List<JunctionFillet> JunctionFillets { get; } = new();

        public SmoothedNetwork(RoadNetwork network, double resampleM = 0.5)
        {
            Network = network;
            Pppm = Config.PIXELS_PER_METER;

            // ---- per merged line: spline + resampled polyline ----
            var groups = RoadNetworkGeometry.MergeAndRoundLines(network);
            foreach (var ((highway, width), coordsList) in groups)
                foreach (var coords in coordsList)
                {
                    var curve = new SmoothCurve(coords, pppm: Pppm);
                    Lines.Add(new SmoothedLine(highway, width, coords, curve,
                        ResampleCurve(curve, resampleM)));
                }

            // ---- segment -> (curve, s range) in both directions ----
            // A segment is one chord of exactly one merged line (linemerge
            // only joins through degree-2 nodes). Keyed by segment INDEX, not
            // seg.id: in OSM data every chord of a way shares the way's id.
            //
            // Matching is PROXIMITY-based, not exact endpoint equality: corner
            // rounding removes degree-2 bend nodes from the rounded coords,
            // so the exact match misses almost every segment (the original
            // implementation left 100% of refs degenerate). Instead, find the
            // line that passes within tolerance of BOTH endpoints and spans
            // the segment. A node sits at most r/sin(θ/2) - r off its own
            // rounded line at a sharp corner θ (the fillet swings away from
            // the node), so two tolerances: the normal case (~2 fillet radii)
            // and a wide fallback for acute corners on long edges.
            double matchTolPx = Math.Max(16.0, 2 * Config.ROAD_CORNER_RADIUS_M * Pppm);
            double wideTolPx = 64.0;
            const double cell = 16.0;
            var grid = new Dictionary<(int Cx, int Cy), List<(int Line, double X, double Y)>>();
            for (int li = 0; li < Lines.Count; li++)
            {
                var res = Lines[li].Resampled;
                if (res.Count < 2) continue;
                for (int i = 0; i < res.Count; i++)
                {
                    var key = ((int)Math.Floor(res[i].X / cell), (int)Math.Floor(res[i].Y / cell));
                    if (!grid.TryGetValue(key, out var l)) grid[key] = l = new List<(int, double, double)>();
                    l.Add((li, res[i].X, res[i].Y));
                }
            }

            // Lines with a resampled point within tol of p.
            var candA = new Dictionary<int, double>();
            var candB = new Dictionary<int, double>();
            void CollectCandidates((double X, double Y) p, double tol, Dictionary<int, double> into)
            {
                into.Clear();
                int cx = (int)Math.Floor(p.X / cell), cy = (int)Math.Floor(p.Y / cell);
                int ring = (int)Math.Ceiling(tol / cell);
                for (int gx = cx - ring; gx <= cx + ring; gx++)
                    for (int gy = cy - ring; gy <= cy + ring; gy++)
                    {
                        if (!grid.TryGetValue((gx, gy), out var l)) continue;
                        foreach (var (li, qx, qy) in l)
                        {
                            double d2 = (qx - p.X) * (qx - p.X) + (qy - p.Y) * (qy - p.Y);
                            if (d2 > tol * tol) continue;
                            if (!into.ContainsKey(li)) into[li] = 0.0; // presence marker
                        }
                    }
            }

            for (int idx = 0; idx < network.Segments.Count; idx++)
            {
                var seg = network.Segments[idx];
                foreach (bool forward in new[] { true, false })
                {
                    string a = forward ? seg.StartNode : seg.EndNode;
                    string b = forward ? seg.EndNode : seg.StartNode;
                    // Fall back to the segment's own endpoints when it carries
                    // no node ids (networks built directly leave them empty).
                    var ea = forward ? (X: seg.X1, Y: seg.Y1) : (X: seg.X2, Y: seg.Y2);
                    var eb = forward ? (X: seg.X2, Y: seg.Y2) : (X: seg.X1, Y: seg.Y1);
                    var pa = network.Nodes.TryGetValue(a, out var na) ? na : ea;
                    var pb = network.Nodes.TryGetValue(b, out var nb) ? nb : eb;

                    double chordPx = Math.Hypot(pb.X - pa.X, pb.Y - pa.Y);
                    SegmentCurveRef? hit = null;
                    if (chordPx > 1e-6)
                    {
                        foreach (double tol in new[] { matchTolPx, wideTolPx })
                        {
                            CollectCandidates(pa, tol, candA);
                            CollectCandidates(pb, tol, candB);
                            double bestScore = double.PositiveInfinity;
                            foreach (var (li, _) in candA)
                            {
                                if (!candB.ContainsKey(li)) continue; // must reach both ends
                                var line = Lines[li];
                                double sa = NodeS(line.Curve, pa);
                                double sb = NodeS(line.Curve, pb);
                                var (ax, ay) = line.Curve.PointAt(sa);
                                var (bx, by) = line.Curve.PointAt(sb);
                                double da = Math.Hypot(ax - pa.X, ay - pa.Y);
                                double db = Math.Hypot(bx - pb.X, by - pb.Y);
                                if (da > tol || db > tol) continue;
                                // The line must actually span the segment: its
                                // arc between the two node projections covers
                                // most of the chord (rejects other roads that
                                // merely pass near one or both endpoints).
                                if (Math.Abs(sb - sa) < 0.4 * chordPx / Pppm) continue;
                                double score = da + db;
                                if (score < bestScore)
                                {
                                    bestScore = score;
                                    hit = new SegmentCurveRef { Line = line, Curve = line.Curve,
                                        S0 = Math.Min(sa, sb), S1 = Math.Max(sa, sb) };
                                }
                            }
                            if (hit is not null) break;
                        }
                    }
                    if (hit is null)
                        hit = new SegmentCurveRef { Line = null, Curve = new SmoothCurve(new[] { pa, pb }, pppm: Pppm), S0 = 0.0, S1 = 0.0 };
                    SegmentCurve[(idx, forward)] = hit;
                }
            }

            // ---- junction corners: Eckausrundung (degree >= 3) ----
            // Where two road EDGES meet at a node, round the grass corner:
            // compass centre r off both edge lines, arc of radius r between
            // the tangent points, paved fill = the curvilinear triangle.
            foreach (var (nodeId, connected) in network.NodeConnections)
            {
                if (connected.Count < 3) continue; // degree-2 bends are inside the line splines
                if (!network.Nodes.TryGetValue(nodeId, out var nodeXY)) continue;
                double nodeX = nodeXY.X, nodeY = nodeXY.Y;

                var spokes = new List<(double Ax, double Ay, RoadSegment Seg, int Id)>();
                foreach (int segIdx in connected)
                {
                    var seg = network.Segments[segIdx];
                    double awayDx, awayDy;
                    if (seg.StartNode == nodeId) { awayDx = seg.X2 - seg.X1; awayDy = seg.Y2 - seg.Y1; }
                    else { awayDx = seg.X1 - seg.X2; awayDy = seg.Y1 - seg.Y2; }
                    double length = Math.Hypot(awayDx, awayDy);
                    if (length < 1e-6) continue;
                    spokes.Add((awayDx / length, awayDy / length, seg, segIdx));
                }
                if (spokes.Count < 2) continue;
                spokes.Sort(Comparer<(double Ax, double Ay, RoadSegment Seg, int Id)>.Create(
                    (a, b) => Math.Atan2(a.Ay, a.Ax).CompareTo(Math.Atan2(b.Ay, b.Ax))));

                int n = spokes.Count;
                // Two valid spokes (a degree-3+ node with a degenerate
                // zero-length stub among its segments) form ONE corner, not a
                // cycle: pairing both orderings emitted the same fillet twice,
                // as two near-coincident sliver patches that broke strict
                // overlay noding.
                int pairs = n == 2 ? 1 : n;
                for (int i = 0; i < pairs; i++)
                {
                    var (ax, ay, segA, segAId) = spokes[i];
                    var (bx, by, segB, segBId) = spokes[(i + 1) % n];
                    double gap = Math.Acos(Math.Clamp(ax * bx + ay * by, -1.0, 1.0));
                    double gapDeg = gap * 180.0 / Math.PI;
                    if (gapDeg < MinGapDeg || gapDeg > MaxGapDeg) continue;

                    // Corner point C: where a's edge (on b's side of a's
                    // centreline) meets b's edge (on a's side).
                    double crossZ = ax * by - ay * bx; // != 0: gap filter keeps spokes off-parallel
                    double sa = crossZ > 0 ? 1.0 : -1.0;
                    double sb = -sa;
                    double waPx = (segA.Width / 2.0) * Pppm;
                    double wbPx = (segB.Width / 2.0) * Pppm;
                    double pax = sa * waPx * (-ay), pay = sa * waPx * ax;   // on a's edge
                    double pbx = sb * wbPx * (-by), pby = sb * wbPx * bx;   // on b's edge
                    double t = ((pbx - pax) * by - (pby - pay) * bx) / crossZ;
                    double cx = nodeX + pax + t * ax, cy = nodeY + pay + t * ay;

                    // Cap the radius so the tangent points stay on the paved edge.
                    double cornerOff = Math.Hypot(cx - nodeX, cy - nodeY);
                    double availA = Math.Max(0.0, segA.Length * Pppm - cornerOff);
                    double availB = Math.Max(0.0, segB.Length * Pppm - cornerOff);
                    double tangentDist = (Config.JUNCTION_CORNER_RADIUS_M * Pppm) / Math.Tan(gap / 2.0);
                    double rPx = Config.JUNCTION_CORNER_RADIUS_M * Pppm;
                    if (tangentDist > Math.Min(availA, availB))
                        rPx = Math.Min(availA, availB) * Math.Tan(gap / 2.0);
                    if (rPx < Pppm) continue; // < 1 m: no rounding

                    // Compass centre O: r further out, off both edge lines.
                    double paxO = pax + sa * rPx * (-ay), payO = pay + sa * rPx * ax;
                    double pbxO = pbx + sb * rPx * (-by), pbyO = pby + sb * rPx * bx;
                    t = ((pbxO - paxO) * by - (pbyO - payO) * bx) / crossZ;
                    double ox = nodeX + paxO + t * ax, oy = nodeY + payO + t * ay;

                    // Tangent points on the edges, then the arc between them
                    // (the sweep that passes through the corner direction).
                    double tax = ox - sa * rPx * (-ay), tay = oy - sa * rPx * ax;
                    double tbx = ox - sb * rPx * (-by), tby = oy - sb * rPx * bx;
                    double angA = Math.Atan2(tay - oy, tax - ox);
                    double angB = Math.Atan2(tby - oy, tbx - ox);
                    double angC = Math.Atan2(cy - oy, cx - ox);
                    double fwd = (angB - angA) % (2 * Math.PI); if (fwd < 0) fwd += 2 * Math.PI;
                    double back = (angA - angB) % (2 * Math.PI); if (back < 0) back += 2 * Math.PI;
                    int direction; double sweep;
                    double dca = (angC - angA) % (2 * Math.PI); if (dca < 0) dca += 2 * Math.PI;
                    if (dca < fwd) (direction, sweep) = (1, fwd);
                    else (direction, sweep) = (-1, back);
                    int steps = 24;
                    var arc = new List<(double X, double Y)>(steps + 1);
                    for (int s = 0; s <= steps; s++)
                    {
                        double a = angA + direction * sweep * (s / (double)steps);
                        arc.Add((ox + rPx * Math.Cos(a), oy + rPx * Math.Sin(a)));
                    }

                    JunctionFillets.Add(new JunctionFillet(nodeId, segAId, segBId,
                        (cx, cy), arc, rPx));
                }
            }
        }

        /// <summary>Arc length at which the spline passes through `node` (it
        /// does, exactly — the spline is interpolating).</summary>
        public static double NodeS(SmoothCurve curve, (double X, double Y) node)
        {
            double bestS = 0.0;
            double bestD2 = double.PositiveInfinity;
            int n = curve.S.Count;
            for (int i = 0; i < n; i += Math.Max(1, n / 2000))
            {
                double d2 = (curve.Xs[i] - node.X) * (curve.Xs[i] - node.X)
                          + (curve.Ys[i] - node.Y) * (curve.Ys[i] - node.Y);
                if (d2 < bestD2) { bestD2 = d2; bestS = curve.S[i]; }
            }
            // Refine locally.
            double lo = Math.Max(0.0, bestS - 2.0);
            double hi = Math.Min(curve.Total, bestS + 2.0);
            for (int k = 0; k <= 40; k++)
            {
                double s = lo + (hi - lo) * k / 40.0;
                var (x, y) = curve.PointAt(s);
                double d2 = (x - node.X) * (x - node.X) + (y - node.Y) * (y - node.Y);
                if (d2 < bestD2) { bestD2 = d2; bestS = s; }
            }
            return bestS;
        }
    }

    /// <summary>Convenience accessor: one SmoothedNetwork per RoadNetwork (cached).</summary>
    public static SmoothedNetwork For(RoadNetwork network)
    {
        if (network.SmoothedCache is null)
            network.SmoothedCache = new SmoothedNetwork(network);
        return network.SmoothedCache;
    }
}
