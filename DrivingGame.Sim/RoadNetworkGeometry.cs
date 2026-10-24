using System;
using System.Collections.Generic;
using System.Linq;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Buffer;
using NetTopologySuite.Operation.Linemerge;

namespace DrivingGame.Sim;

/// <summary>Module-level road-geometry functions. Ported from the free
/// functions in /Users/hauke/prj/car/src/road_network.py.</summary>
public static class RoadNetworkGeometry
{
    // --- Projection helpers ---------------------------------------------------

    /// <summary>Convert lat/lon to world-metre coordinates (equirectangular).</summary>
    public static (double X, double Y) LatLonToWorld(double lat, double lon,
        double refLat, double refLon)
    {
        double metersPerLatDeg = 111132.9 - 566.0 * Math.Cos(2 * Rad(lat)) + 1.2 * Math.Cos(4 * Rad(lat));
        double metersPerLonDeg = 111320 * Math.Cos(Rad(lat));

        double dxM = (lon - refLon) * metersPerLonDeg;
        double dyM = (lat - refLat) * metersPerLatDeg;

        return (dxM, dyM);
    }

    private static double Rad(double deg) => deg * Math.PI / 180.0;

    /// <summary>(distance, proj_x, proj_y) from point to line segment.</summary>
    public static (double Dist, double ProjX, double ProjY) PointToSegment(
        double px, double py, double x1, double y1, double x2, double y2)
    {
        double dx = x2 - x1, dy = y2 - y1;
        double lengthSq = dx * dx + dy * dy;

        if (lengthSq == 0)
            return (Math.Hypot(px - x1, py - y1), x1, y1);

        // Both terms divided by lengthSq (operator precedence!): the ported
        // Python is ((px-x1)*dx + (py-y1)*dy) / length_sq.
        double t = Math.Clamp(((px - x1) * dx + (py - y1) * dy) / lengthSq, 0, 1);
        double projX = x1 + t * dx;
        double projY = y1 + t * dy;

        return (Math.Hypot(px - projX, py - projY), projX, projY);
    }

    public static double PointToSegmentDistance(
        double px, double py, double x1, double y1, double x2, double y2) =>
        PointToSegment(px, py, x1, y1, x2, y2).Dist;

    // --- Merging / rounding ----------------------------------------------------

    /// <summary>Chain segments into maximal polylines, continuing through a
    /// node ONLY if it is a plain degree-2 bend. Shapely's linemerge would also
    /// fuse arms that meet at a real junction (same group, shared endpoint),
    /// producing lines that run straight across the crossing — fine for the
    /// pavement union, wrong for painted markings, which must stop before the
    /// intersection (user decision).</summary>
    private static List<List<(double X, double Y)>> ChainSegments(
        List<RoadSegment> segs, RoadNetwork network)
    {
        var byNode = new Dictionary<string, List<int>>();
        for (int i = 0; i < segs.Count; i++)
        {
            var s = segs[i];
            if (!byNode.TryGetValue(s.StartNode, out var l1)) byNode[s.StartNode] = l1 = new List<int>();
            l1.Add(i);
            if (!byNode.TryGetValue(s.EndNode, out var l2)) byNode[s.EndNode] = l2 = new List<int>();
            l2.Add(i);
        }
        var used = new bool[segs.Count];
        var chains = new List<List<(double X, double Y)>>();

        void Extend(List<(double X, double Y)> pts, string fromNode, bool prepend)
        {
            string cur = fromNode;
            var last = prepend ? pts[0] : pts[^1];
            var newPts = new List<(double X, double Y)>();
            while (network.NodeDegree.GetValueOrDefault(cur, 0) == 2)
            {
                if (!byNode.TryGetValue(cur, out var cands)) break;
                // First unused segment at cur, or -1. (Neither FirstOrDefault
                // nor FindIndex works: the former's default of 0 splices in
                // segment 0 when all candidates are used — reachable on a
                // degree-2 cycle; the latter returns the LIST POSITION, not
                // the segment index.)
                int j = -1;
                foreach (var k in cands)
                    if (!used[k]) { j = k; break; }
                if (j < 0) break;
                var t = segs[j];
                used[j] = true;
                (double X, double Y) nxtPt;
                if ((t.X1, t.Y1) == last) { nxtPt = (t.X2, t.Y2); cur = t.EndNode; }
                else { nxtPt = (t.X1, t.Y1); cur = t.StartNode; }
                newPts.Add(nxtPt);
                last = nxtPt;
            }
            if (prepend)
            {
                newPts.Reverse();
                pts.InsertRange(0, newPts);
            }
            else
            {
                pts.AddRange(newPts);
            }
        }

        for (int i0 = 0; i0 < segs.Count; i0++)
        {
            if (used[i0]) continue;
            var s = segs[i0];
            used[i0] = true;
            var pts = new List<(double X, double Y)> { (s.X1, s.Y1), (s.X2, s.Y2) };
            Extend(pts, s.EndNode, prepend: false);
            Extend(pts, s.StartNode, prepend: true);
            chains.Add(pts);
        }
        return chains;
    }

    /// <summary>A merged chain as a smooth curve THROUGH its shape points:
    /// centripetal Catmull-Rom resampled every 0.5 m - the same spline the
    /// width-varying ribbons already use. This replaces the fixed
    /// ROAD_CORNER_RADIUS_M fillet, whose curvature came from a constant: a
    /// gentle chain (a figure-8 lobe sampled by 30 m chords, 22 deg at each
    /// node) kinked 0.12 m at every node instead of curving at its own
    /// ~115 m radius.
    ///
    /// A bend sharper than MAX_CURVE_TURN_DEG is NOT a sampled curve but a
    /// real corner, and keeps the fixed-radius fillet: a pure spline there
    /// interpolates with tangents blended 45 deg off the legs and swung
    /// 18.5 m outside the test map's 90 deg corner (250 m legs), which the
    /// RightTurn unit test caught. The rule is therefore: many small bends
    /// = recover the curve, one big bend = fillet the corner.
    ///
    /// Closed chains (a loop: first point == last) are continued past the
    /// seam with the loop's own next nodes and cut back where the loop
    /// closes, so the curve stays continuous there - but only when the seam
    /// is itself a gentle bend: a seam that is a real corner (a figure-8
    /// lobe closing at its crossing node) keeps the rule above and stays
    /// sharp. Open chains keep their end tangents along the first/last
    /// chord (SmoothCurve's virtual knots), so a chain still meets a
    /// junction exactly where the network says it does.</summary>
    public static List<(double X, double Y)> SmoothChain(
        List<(double X, double Y)> coords)
    {
        if (coords.Count < 3)
            return new List<(double X, double Y)>(coords);

        // Real corners: a bend sharper than this is not a sampled curve.
        const double maxCurveTurnRad = 30.0 * Math.PI / 180.0;
        var hard = new List<int>();
        for (var i = 1; i < coords.Count - 1; i++)
        {
            var gap = CornerGap(coords, i);
            if (gap >= 0.0 && gap < Math.PI - maxCurveTurnRad) hard.Add(i);
        }

        if (hard.Count == 0)
        {
            var closed = Math.Hypot(coords[0].X - coords[^1].X,
                                    coords[0].Y - coords[^1].Y) < 1e-6;
            // A closed chain's seam is a vertex like any other, so it gets
            // the same rule. A lobe of a figure-8 closes AT its crossing
            // node: an ~86 deg turn there. Continued through the loop's own
            // nodes, the spline blends that corner and hooks the arriving
            // arm 29 deg / 3.2 m off its chord while the leaving arm stays
            // exact - two asymmetric arms, and the crossing reads as a
            // pinch. A real corner at the seam stays sharp: the chain ends
            // along its own chords, which is what the network says. Only a
            // gentle seam (a roundabout ring, where the seam is an
            // arbitrary point on a curve) is worth continuing through.
            var seamGap = SeamGap(coords);
            if (closed && seamGap >= 0.0 && seamGap < Math.PI - maxCurveTurnRad)
                return SplineThrough(coords);

            var ctrl = new List<(double X, double Y)>(coords);
            if (closed)
                for (var i = 1; i <= 2 && i < coords.Count - 1; i++)
                    ctrl.Add(coords[i]);   // past the seam: the loop's own next nodes
            return SplineThrough(ctrl, closed ? coords[0] : null);
        }

        // Mixed chain: smooth the runs between corners, fillet the corners.
        var chain = new List<(double X, double Y)>();
        var hasResume = false;
        var resume = default((double X, double Y));
        var runStart = 0;
        foreach (var h in hard)
        {
            var run = new List<(double X, double Y)>();
            if (hasResume) run.Add(resume);
            for (var i = runStart; i < h; i++) run.Add(coords[i]);

            var gap = CornerGap(coords, h);
            SmoothGeometry.CornerFilletResult? fillet = null;
            if (gap >= 0.0)
            {
                // One arc vertex per sample step, so a fillet is never
                // coarser than the chain around it.
                var arcSteps = Math.Max(8,
                    (int)(Config.ROAD_CORNER_RADIUS_M * (Math.PI - gap) / 0.5));
                fillet = SmoothGeometry.CornerFillet(
                    coords[h - 1], coords[h], coords[h + 1],
                    Config.ROAD_CORNER_RADIUS_M, arcSteps);
            }
            if (fillet is null)
            {
                // Degenerate (doubled back): keep the vertex, no arc to cut.
                run.Add(coords[h]);
                chain.AddRange(SplineThrough(run));
                hasResume = false;
                runStart = h + 1;
                continue;
            }
            run.Add(fillet.T1);          // the arc takes over at the tangent
            chain.AddRange(SplineThrough(run));
            chain.AddRange(fillet.Arc);
            resume = fillet.T2;
            hasResume = true;
            runStart = h + 1;
        }
        var tail = new List<(double X, double Y)>();
        if (hasResume) tail.Add(resume);
        for (var i = runStart; i < coords.Count; i++) tail.Add(coords[i]);
        chain.AddRange(SplineThrough(tail));
        return chain;
    }

    /// <summary>The turn at a closed chain's seam (coords[0] == coords[^1]),
    /// between its last leg and its first leg — the vertex the chain
    /// closes on, which CornerGap cannot see because it is both ends.</summary>
    private static double SeamGap(List<(double X, double Y)> coords) =>
        CornerGap(new List<(double X, double Y)>
            { coords[^2], coords[0], coords[1] }, 1);

    /// <summary>Angle (radians) between the two edges at coords[i], both
    /// pointing away from the vertex: pi = straight through, 0 = doubled
    /// back. -1 for a degenerate vertex.</summary>
    private static double CornerGap(List<(double X, double Y)> coords, int i)
    {
        var (px, py) = coords[i - 1];
        var (vx, vy) = coords[i];
        var (nx, ny) = coords[i + 1];
        double ax = px - vx, ay = py - vy, bx = nx - vx, by = ny - vy;
        double aLen = Math.Hypot(ax, ay), bLen = Math.Hypot(bx, by);
        if (aLen < 1e-9 || bLen < 1e-9) return -1.0;
        double dot = Math.Clamp((ax * bx + ay * by) / (aLen * bLen), -1.0, 1.0);
        return Math.Acos(dot);
    }

    /// <summary>The chain as a Catmull-Rom through `ctrl`, resampled every
    /// 0.5 m. `seamTo` (a closed chain's start point) cuts the output at the
    /// SECOND pass through that point instead of the spline's end.</summary>
    private static List<(double X, double Y)> SplineThrough(
        List<(double X, double Y)> ctrl, (double X, double Y)? seamTo = null)
    {
        if (ctrl.Count < 2)
            return new List<(double X, double Y)>(ctrl);
        var curve = new SmoothGeometry.SmoothCurve(ctrl);
        if (curve.Total <= 0)
            return new List<(double X, double Y)>(ctrl);

        var endS = curve.Total;
        if (seamTo is not null)
        {
            // Arc length of the SECOND pass through the seam point: coarse
            // scan of the back half, then a local refine.
            var (sx, sy) = seamTo.Value;
            double bestS = curve.Total * 0.5, bestD2 = double.PositiveInfinity;
            for (var k = 0; k <= 400; k++)
            {
                var s = curve.Total * (0.5 + 0.5 * k / 400.0);
                var (x, y) = curve.PointAt(s);
                var d2 = (x - sx) * (x - sx) + (y - sy) * (y - sy);
                if (d2 < bestD2) { bestD2 = d2; bestS = s; }
            }
            for (var k = -20; k <= 20; k++)
            {
                var s = Math.Max(0.0, Math.Min(curve.Total, bestS + k * 0.05));
                var (x, y) = curve.PointAt(s);
                var d2 = (x - sx) * (x - sx) + (y - sy) * (y - sy);
                if (d2 < bestD2) { bestD2 = d2; bestS = s; }
            }
            endS = bestS;
        }

        const double sampleM = 0.5;   // same arc-length step as the ribbons
        var n = Math.Max(2, (int)(endS / sampleM) + 1);
        var pts = new List<(double X, double Y)>(n);
        for (var i = 0; i < n; i++)
            pts.Add(curve.PointAt(endS * i / (n - 1.0)));
        return pts;
    }

    /// <summary>Group segments by (highway, width), merge contiguous ones
    /// through plain degree-2 nodes, and round each merged line's own corners.
    /// Shared by BuildRoadPolygons (buffers these into fillable polygons) and
    /// BuildCenterlines (draws them directly as the dashed lane-marking line)
    /// so the two always agree exactly on where the road curves.
    ///
    /// onlyTwoWay: skip oneway segments entirely (a single-lane one-way street
    /// has no opposing lane to divide). stopAtJunctions: chain manually and
    /// never continue through a degree-3+ node — for painted markings, which
    /// must end at the crossing.</summary>
    public static Dictionary<(string Highway, double Width), List<List<(double X, double Y)>>>
        MergeAndRoundLines(RoadNetwork network, bool onlyTwoWay = false,
            bool skipMultiLane = false, bool stopAtJunctions = false,
            HashSet<int>? excludeSegs = null)
    {
        var groups = new Dictionary<(string, double), List<RoadSegment>>();
        for (int si = 0; si < network.Segments.Count; si++)
        {
            var seg = network.Segments[si];
            // Consumed by a width-varying ribbon/centreline (drawn from the
            // continuous chain spline instead) - skip the per-width piece.
            if (excludeSegs != null && excludeSegs.Contains(si)) continue;
            if (onlyTwoWay && seg.Oneway) continue;
            // Multi-lane carriageways draw their OWN centerline (solid) — the
            // plain dashed one would double it.
            if (skipMultiLane && seg.Lanes > 0) continue;
            double length = Math.Hypot(seg.X2 - seg.X1, seg.Y2 - seg.Y1);
            if (length < 1e-6) continue;
            var key = (seg.Highway, seg.Width);
            if (!groups.TryGetValue(key, out var l)) groups[key] = l = new List<RoadSegment>();
            l.Add(seg);
        }

        var result = new Dictionary<(string, double), List<List<(double X, double Y)>>>();
        foreach (var (key, segs) in groups)
        {
            List<List<(double X, double Y)>> raw;
            if (stopAtJunctions)
            {
                raw = ChainSegments(segs, network);
            }
            else
            {
                var lines = segs.Select(s => RoadNetwork.ToLineString(
                    new[] { (s.X1, s.Y1), (s.X2, s.Y2) })).ToList();
                List<Geometry> merged;
                if (lines.Count > 1)
                {
                    var lm = new LineMerger();
                    foreach (var l in lines) lm.Add(l);
                    merged = lm.GetMergedLineStrings().ToList();
                }
                else
                {
                    merged = new List<Geometry> { lines[0] };
                }
                raw = merged
                    .Select(g => ((LineString)g).Coordinates
                        .Select(c => (c.X, c.Y)).ToList())
                    .ToList();
            }
            result[key] = raw.Select(coords => SmoothChain(coords)).ToList();
        }
        return result;
    }

    /// <summary>Flatten MergeAndRoundLines()'s per-group coordinate lists into
    /// one plain list of polylines. One-way segments excluded.</summary>
    public static List<List<(double X, double Y)>> BuildCenterlines(RoadNetwork network)
    {
        // Width-varying roads: draw ONE dashed centreline from the same chain
        // spline the paved ribbon uses (so the dashes sit on the paved centre),
        // and skip the per-width pieces that would otherwise diverge from it.
        var chains = ComputeWidthVaryingChains(network);
        var covered = new HashSet<int>();
        foreach (var wc in chains)
            foreach (var si in wc.SegIdx) covered.Add(si);

        var groups = MergeAndRoundLines(network, onlyTwoWay: true, excludeSegs: covered);
        var lines = groups.Values.SelectMany(l => l).ToList();

        foreach (var wc in chains)
        {
            if (!wc.TwoWay) continue;
            var curve = new SmoothGeometry.SmoothCurve(wc.Chain);
            if (curve.Total <= 1e-6) continue;
            lines.Add(SmoothGeometry.ResampleCurve(curve, 0.5));
        }
        return lines;
    }

    /// <summary>(start_trim, end_trim) in metres: PARK_LANE_END_GAP_M where the
    /// line's end sits at a real junction (node degree &gt;= 3), else 0.</summary>
    public static (double Start, double End) JunctionTrim(RoadNetwork network,
        List<(double X, double Y)> coords)
    {
        double gap = Config.PARK_LANE_END_GAP_M;

        double TrimAt(double px, double py)
        {
            foreach (var (nid, nxy) in network.Nodes)
            {
                if (network.NodeDegree.GetValueOrDefault(nid, 0) < 3) continue;
                if ((nxy.X - px) * (nxy.X - px) + (nxy.Y - py) * (nxy.Y - py) <= 0.5 * 0.5)
                    return gap;
            }
            return 0.0;
        }

        return (TrimAt(coords[0].X, coords[0].Y), TrimAt(coords[^1].X, coords[^1].Y));
    }

    /// <summary>The mouth of a junction per approach: the fillet tangent
    /// points that sit on that approach's two road edges. A normal approach
    /// gets two of them - one from each adjacent corner - and the line
    /// through them is exactly where a stop line runs across the
    /// carriageway, and how far the centreline paint has to back off. Keyed
    /// by (node, segment index leading into it), built from the same
    /// SmoothedNetwork.junction_fillets that paint the corners, so the
    /// traffic rule and the tarmac cannot disagree.</summary>
    public static Dictionary<(string Node, int SegIdx), List<(double X, double Y)>>
        BuildCrossingMouths(RoadNetwork network)
    {
        var smNet = SmoothGeometry.For(network);
        var result = new Dictionary<(string, int), List<(double X, double Y)>>();
        void Put(string node, int seg, (double X, double Y) p)
        {
            if (seg < 0 || seg >= network.Segments.Count) return;
            if (!result.TryGetValue((node, seg), out var l)) result[(node, seg)] = l = new();
            l.Add(p);
        }
        foreach (var f in smNet.JunctionFillets)
        {
            if (f.Arc.Count < 2) continue;
            // The arc starts at the tangent point on SegA's edge and ends on
            // SegB's.
            Put(f.Node, f.SegA, f.Arc[0]);
            Put(f.Node, f.SegB, f.Arc[^1]);
        }
        return result;
    }

    /// <summary>(start_trim, end_trim) in metres for PAINTED lane markings:
    /// where a line's end sits at a real junction, the paint stops half the
    /// WIDEST arm's width plus CENTERLINE_JUNCTION_GAP_M before the node
    /// centre - further still when the crossing is signed, because a stop
    /// line is painted at the mouth of the approach and paint belongs
    /// behind it.</summary>
    public static (double Start, double End) JunctionMarkingTrim(RoadNetwork network,
        List<(double X, double Y)> coords)
    {
        var mouths = network.GetCrossingMouths();

        double GapAt(double px, double py, double dirx, double diry)
        {
            foreach (var (nid, nxy) in network.Nodes)
            {
                if (network.NodeDegree.GetValueOrDefault(nid, 0) < 3) continue;
                if ((nxy.X - px) * (nxy.X - px) + (nxy.Y - py) * (nxy.Y - py) > 0.5 * 0.5)
                    continue;
                var connected = network.GetConnectedSegments(nid);
                double widest = connected.Max(i => network.Segments[i].Width);
                double gap = widest / 2.0 + Config.CENTERLINE_JUNCTION_GAP_M;

                // Which arm of the junction this line belongs to: the
                // connected segment pointing most along the line's own way
                // out of the node.
                int bestSeg = -1; double bestDot = 0.2;
                foreach (int i in connected)
                {
                    var s = network.Segments[i];
                    double ax = s.X1 - nxy.X, ay = s.Y1 - nxy.Y;
                    if (Math.Hypot(ax, ay) < 1e-6) { ax = s.X2 - nxy.X; ay = s.Y2 - nxy.Y; }
                    double l = Math.Max(Math.Hypot(ax, ay), 1e-9);
                    double dot = ax / l * dirx + ay / l * diry;
                    if (dot > bestDot) { bestDot = dot; bestSeg = i; }
                }
                if (bestSeg >= 0 && mouths.TryGetValue((nid, bestSeg), out var pts))
                    foreach (var (tx, ty) in pts)
                        gap = Math.Max(gap, (tx - nxy.X) * dirx + (ty - nxy.Y) * diry);
                return gap;
            }
            return 0.0;
        }

        if (coords.Count < 2) return (0.0, 0.0);
        var (d0x, d0y) = Unit(coords[1].X - coords[0].X, coords[1].Y - coords[0].Y);
        var (d1x, d1y) = Unit(coords[^2].X - coords[^1].X, coords[^2].Y - coords[^1].Y);
        return (GapAt(coords[0].X, coords[0].Y, d0x, d0y),
                GapAt(coords[^1].X, coords[^1].Y, d1x, d1y));
    }

    /// <summary>A point at an arc distance (metres) along a polyline.</summary>
    public static (double X, double Y) PointAtArc(
        List<(double X, double Y)> coords, double s)
    {
        double acc = 0.0;
        for (int i = 0; i < coords.Count - 1; i++)
        {
            var (ax, ay) = coords[i];
            var (bx, by) = coords[i + 1];
            double segLen = Math.Hypot(bx - ax, by - ay);
            if (acc + segLen >= s && segLen > 0)
            {
                double t = (s - acc) / segLen;
                return (ax + t * (bx - ax), ay + t * (by - ay));
            }
            acc += segLen;
        }
        return coords[^1];
    }

    /// <summary>The sub-polyline between two arc positions (metres from the
    /// start), endpoints interpolated and vertices OUTSIDE the range dropped
    /// - unlike TrimEnds, which only shortens the ends and keeps every
    /// interior vertex, this really cuts a line into pieces.</summary>
    public static List<(double X, double Y)> SubArc(
        List<(double X, double Y)> coords, double from, double to)
    {
        if (coords.Count < 2) return new List<(double X, double Y)>();
        double total = 0.0;
        for (int i = 0; i < coords.Count - 1; i++)
            total += Math.Hypot(coords[i + 1].X - coords[i].X,
                                coords[i + 1].Y - coords[i].Y);
        from = Math.Max(0.0, Math.Min(from, total));
        to = Math.Max(0.0, Math.Min(to, total));
        if (to - from < 0.01) return new List<(double X, double Y)>();
        if (from <= 0.0 && to >= total) return new List<(double X, double Y)>(coords);
        var out_ = new List<(double X, double Y)> { PointAtArc(coords, from) };
        double acc = 0.0;
        for (int i = 1; i < coords.Count; i++)
        {
            acc += Math.Hypot(coords[i].X - coords[i - 1].X,
                              coords[i].Y - coords[i - 1].Y);
            if (acc > from && acc < to) out_.Add(coords[i]);
        }
        out_.Add(PointAtArc(coords, to));
        return out_.Count >= 2 ? out_ : new List<(double X, double Y)>();
    }

    /// <summary>A centreline is not painted across a junction: cut it
    /// wherever it passes through a real one, so every piece ENDS at the
    /// mouth of an approach - the same place its halt line sits, or would
    /// sit on an un-signed arm (the cut therefore does not depend on signs,
    /// only on the paved corners). Reports which piece ends are junction
    /// mouths, because the paint on an approach turns solid, not dashed,
    /// on the way in. Returns (coords, solidAtStart, solidAtEnd) pieces in
    /// line order.</summary>
    public static List<(List<(double X, double Y)>, bool, bool)>
        SplitCentrelineAtJunctions(RoadNetwork network, List<(double X, double Y)> coords)
    {
        var pieces = new List<(List<(double X, double Y)>, bool, bool)>();
        if (coords.Count < 2) return pieces;
        var mouths = network.GetCrossingMouths();

        double total = 0.0;
        var acc = new List<double>(coords.Count);
        for (int i = 0; i < coords.Count; i++)
        {
            acc.Add(total);
            if (i + 1 < coords.Count)
                total += Math.Hypot(coords[i + 1].X - coords[i].X,
                                    coords[i + 1].Y - coords[i].Y);
        }

        // How far the mouth of the arm of `nid` pointing along (dirx,diry)
        // is from the node: the halt line's distance, so the paint stops
        // where the line does. Half the widest arm plus a margin is the
        // fallback for a junction without painted corners; the wider of
        // the two wins, so paint never sits inside the crossing box.
        double MouthAt(string nid, double dirx, double diry)
        {
            if (!network.Nodes.TryGetValue(nid, out var nxy)) return 0.0;
            var connected = network.GetConnectedSegments(nid);
            double gap = connected.Max(i => network.Segments[i].Width) / 2.0
                       + Config.CENTERLINE_JUNCTION_GAP_M;
            int bestSeg = -1; double bestDot = 0.2;
            foreach (int i in connected)
            {
                var s = network.Segments[i];
                double ax = s.X1 - nxy.X, ay = s.Y1 - nxy.Y;
                if (Math.Hypot(ax, ay) < 1e-6)
                { ax = s.X2 - nxy.X; ay = s.Y2 - nxy.Y; }
                double l = Math.Max(Math.Hypot(ax, ay), 1e-9);
                double dot = ax / l * dirx + ay / l * diry;
                if (dot > bestDot) { bestDot = dot; bestSeg = i; }
            }
            if (bestSeg >= 0 && mouths.TryGetValue((nid, bestSeg), out var pts))
                foreach (var (tx, ty) in pts)
                    gap = Math.Max(gap, (tx - nxy.X) * dirx + (ty - nxy.Y) * diry);
            return gap;
        }

        var zones = new List<(double From, double To)>();
        // The place nearest vertex `idx` where the line crosses the mouth
        // LINE of the arm pointing (dirx,diry) - the line perpendicular to
        // that arm at distance `gap` (metres) from the node, where that arm's halt
        // line is drawn. Every crossing of that line is a sign change of
        // the projection; the one nearest the pass is the mouth that bounds
        // this paint.
        double MouthCross(int idx, double dirx, double diry, double gap,
                          double nx, double ny)
        {
            double best = -1.0, bestDist = double.MaxValue;
            double prev = (coords[0].X - nx) * dirx + (coords[0].Y - ny) * diry;
            double accLen = 0.0;
            for (int i = 0; i + 1 < coords.Count; i++)
            {
                var (ax, ay) = coords[i];
                var (bx, by) = coords[i + 1];
                double segLen = Math.Hypot(bx - ax, by - ay);
                double cur = (bx - nx) * dirx + (by - ny) * diry;
                if ((prev - gap) * (cur - gap) < 0.0 && cur != prev)
                {
                    double f = (gap - prev) / (cur - prev);
                    double at = accLen + f * segLen;
                    double px = ax + f * (bx - ax), py = ay + f * (by - ay);
                    double dd = Math.Hypot(px - coords[idx].X, py - coords[idx].Y);
                    if (dd < bestDist) { bestDist = dd; best = at; }
                }
                prev = cur;
                accLen += segLen;
            }
            return best >= 0.0 ? best : acc[Math.Min(idx, coords.Count - 1)];
        }

        // The line's own extent: a junction node outside it cannot cut it,
        // and testing that first keeps this off the per-vertex loop for
        // every junction of a big OSM network.
        double bx0 = coords[0].X, bx1 = bx0, by0 = coords[0].Y, by1 = by0;
        foreach (var (qx, qy) in coords)
        {
            bx0 = Math.Min(bx0, qx); bx1 = Math.Max(bx1, qx);
            by0 = Math.Min(by0, qy); by1 = Math.Max(by1, qy);
        }
        foreach (var (nid, nxy) in network.Nodes)
        {
            if (network.NodeDegree.GetValueOrDefault(nid, 0) < 3) continue;
            var connected = network.GetConnectedSegments(nid);
            double widest = connected.Max(i => network.Segments[i].Width);
            // How close a line has to get to this node to be inside its
            // crossing at all: as far out as the farthest painted mouth.
            double reach = widest / 2.0 + Config.CENTERLINE_JUNCTION_GAP_M;
            foreach (int i in connected)
                if (mouths.TryGetValue((nid, i), out var pts))
                    foreach (var (tx, ty) in pts)
                        reach = Math.Max(reach, Math.Hypot(tx - nxy.X, ty - nxy.Y));
            if (nxy.X < bx0 - reach || nxy.X > bx1 + reach ||
                nxy.Y < by0 - reach || nxy.Y > by1 + reach) continue;

            // Every pass of the line through this junction - a self-crossing
            // is passed twice by one loop, so the closest approach alone is
            // not enough. Vertices inside the crossing box group into runs;
            // one run is one pass, and its two ends say which arm the line
            // comes from and which it leaves on.
            int runStart = -1;
            for (int i = 0; i <= coords.Count; i++)
            {
                bool inside = i < coords.Count &&
                    Math.Hypot(coords[i].X - nxy.X, coords[i].Y - nxy.Y) <= reach;
                if (!inside)
                {
                    if (runStart >= 0)
                    {
                        int runEnd = i - 1;
                        // Direction of travel at both ends of the pass -
                        // measured against the PREVIOUS vertex when an end
                        // is the line's own end and has no next one.
                        double tx0, ty0;
                        if (runStart + 1 < coords.Count)
                        {
                            var (ux, uy) = Unit(coords[runStart + 1].X - coords[runStart].X,
                                                coords[runStart + 1].Y - coords[runStart].Y);
                            tx0 = ux; ty0 = uy;
                        }
                        else
                        {
                            var (ux, uy) = Unit(coords[runStart].X - coords[runStart - 1].X,
                                                coords[runStart].Y - coords[runStart - 1].Y);
                            tx0 = ux; ty0 = uy;
                        }
                        double tx1, ty1;
                        if (runEnd + 1 < coords.Count)
                        {
                            var (ux, uy) = Unit(coords[runEnd + 1].X - coords[runEnd].X,
                                                coords[runEnd + 1].Y - coords[runEnd].Y);
                            tx1 = ux; ty1 = uy;
                        }
                        else
                        {
                            var (ux, uy) = Unit(coords[runEnd].X - coords[runEnd - 1].X,
                                                coords[runEnd].Y - coords[runEnd - 1].Y);
                            tx1 = ux; ty1 = uy;
                        }
                        // Where the paint stops at each end of the pass:
                        // always at the mouth line of the ARM THE LINE
                        // LIES ON there - the same place that arm's halt
                        // line is drawn, signed or not.
                        double entryCut, exitCut;
                        if (runStart > 0)
                        {
                            // The line comes in over this arm: its paint
                            // stops at that arm's mouth.
                            double gapIn = MouthAt(nid, -tx0, -ty0);
                            entryCut = MouthCross(runStart, -tx0, -ty0, gapIn,
                                                 nxy.X, nxy.Y);
                        }
                        else entryCut = acc[0];   // the line starts inside
                        if (runEnd + 1 < coords.Count)
                        {
                            // The line leaves over another arm: its paint
                            // stops at that arm's mouth.
                            double gapOut = MouthAt(nid, tx1, ty1);
                            exitCut = MouthCross(runEnd, tx1, ty1, gapOut,
                                                nxy.X, nxy.Y);
                        }
                        else
                        {
                            // The line ENDS in the crossing (its chain was
                            // already cut at the junction): nothing beyond
                            // the end is painted, so the zone runs to the
                            // end of the line and the mouth of the arm it
                            // lies on - taken at the start of the pass -
                            // bounds it.
                            exitCut = acc[coords.Count - 1];
                        }
                        zones.Add((entryCut, exitCut));
                        runStart = -1;
                    }
                    continue;
                }
                if (runStart < 0) runStart = i;
            }
        }

        // Subtract the zones from the line: what is left is paint, and a
        // boundary that is not the line's own end is a junction mouth.
        zones.Sort((a, b) => a.From.CompareTo(b.From));
        void Add(double from, double to, bool solidStart, bool solidEnd)
        {
            if (to - from < 0.01) return;
            var piece = SubArc(coords, from, to);
            if (piece.Count >= 2) pieces.Add((piece, solidStart, solidEnd));
        }
        double cursor = 0.0; bool cutBefore = false;
        for (int i = 0; i < zones.Count;)
        {
            double zf = zones[i].From, zt = zones[i].To;
            int k = i;
            while (k + 1 < zones.Count && zones[k + 1].From <= zt)
            { zt = Math.Max(zt, zones[k + 1].To); k++; }
            if (zf > cursor) Add(cursor, Math.Min(zf, total), cutBefore, zf < total);
            cursor = Math.Max(cursor, zt);
            cutBefore = true;
            i = k + 1;
        }
        if (cursor < total) Add(cursor, total, cutBefore, false);
        return pieces;
    }

    /// <summary>How one centreline piece gets painted: solid for the stretch
    /// in front of a junction mouth (the approach line is continuous, it
    /// does not invite a lane change), dashed beyond. Ends that are not
    /// mouths (a road end) stay dashed. Returns (style, coords) in line
    /// order.</summary>
    public static List<(string, List<(double X, double Y)>)>
        StyleCentrelinePiece(List<(double X, double Y)> coords, bool solidStart,
        bool solidEnd)
    {
        var out_ = new List<(string, List<(double X, double Y)>)>();
        if (coords.Count < 2) return out_;
        double total = 0.0;
        for (int i = 0; i + 1 < coords.Count; i++)
            total += Math.Hypot(coords[i + 1].X - coords[i].X,
                                coords[i + 1].Y - coords[i].Y);
        double solidM = Math.Min(Config.CENTERLINE_SOLID_BEFORE_JUNCTION_M, total);
        double a = solidStart ? solidM : 0.0;
        double b = solidEnd ? total - solidM : total;
        if (b < a) b = a;   // shorter than two solid stretches: all solid
        void Add(double from, double to, string style)
        {
            if (to - from < 0.01) return;
            out_.Add((style, SubArc(coords, from, to)));
        }
        if (a > 0.0) Add(0.0, a, "solid");
        if (b > a) Add(a, b, "dashed");
        if (b < total) Add(b, total, "solid");
        return out_;
    }

    /// <summary>Drop `startM` / `endM` metres of arc length from a polyline's
    /// ends, inserting exact cut points. Returns [] if nothing is left.</summary>
    public static List<(double X, double Y)> TrimEnds(
        List<(double X, double Y)> coords, double startM, double endM)
    {
        if (coords.Count < 2 || (startM <= 0 && endM <= 0))
            return new List<(double X, double Y)>(coords);
        double total = 0;
        for (int i = 0; i < coords.Count - 1; i++)
            total += Math.Hypot(coords[i + 1].X - coords[i].X, coords[i + 1].Y - coords[i].Y);
        if (startM + endM >= total)
            return new List<(double X, double Y)>();

        (double X, double Y) CutAt(List<(double X, double Y)> pts, double s)
        {
            double acc = 0.0;
            for (int i = 0; i < pts.Count - 1; i++)
            {
                var (ax, ay) = pts[i];
                var (bx, by) = pts[i + 1];
                double segLen = Math.Hypot(bx - ax, by - ay);
                if (acc + segLen >= s && segLen > 0)
                {
                    double t = (s - acc) / segLen;
                    return (ax + t * (bx - ax), ay + t * (by - ay));
                }
                acc += segLen;
            }
            return pts[^1];
        }

        var head = CutAt(coords, startM);
        var reversed = coords.AsEnumerable().Reverse().ToList();
        var tail = CutAt(reversed, endM);
        var out_ = new List<(double X, double Y)> { head };
        for (int i = 1; i < coords.Count - 1; i++)
        {
            var p = coords[i];
            if ((p.X - head.X) * (p.X - head.X) + (p.Y - head.Y) * (p.Y - head.Y) > 1e-9 &&
                (p.X - tail.X) * (p.X - tail.X) + (p.Y - tail.Y) * (p.Y - tail.Y) > 1e-9)
                out_.Add(p);
        }
        out_.Add(tail);
        return out_.Count >= 2 ? out_ : new List<(double X, double Y)>();
    }

    // --- Offset curves (shapely offset_curve replacement) ----------------------

    /// <summary>Offset a polyline by a signed distance. Positive = LEFT in the
    /// standard math convention (normal (-dy, dx) of travel direction) — same
    /// convention as shapely's LineString.offset_curve, so call sites keep the
    /// same (negated-for-y-down-world) distances as the Python code.
    ///
    /// Round joins: at a convex corner an arc of radius |dist| centred on the
    /// ORIGINAL vertex connects the two offset edges; at a concave corner the
    /// intersection point of the two offset edges is inserted. Returns a list
    /// of polylines (normally one).</summary>
    public static List<List<(double X, double Y)>> OffsetCurve(
        List<(double X, double Y)> coords, double dist)
    {
        if (coords.Count < 2 || Math.Abs(dist) < 1e-12)
            return coords.Count >= 2 ? new() { new List<(double X, double Y)>(coords) } : new();

        double d = dist;
        var out_ = new List<(double X, double Y)>();

        // Start point: offset of the first vertex along the first edge's normal.
        {
            var (ux, uy) = Unit(coords[1].X - coords[0].X, coords[1].Y - coords[0].Y);
            out_.Add((coords[0].X + d * -uy, coords[0].Y + d * ux));
        }

        for (int i = 1; i < coords.Count - 1; i++)
        {
            var v = coords[i];
            var (u1x, u1y) = Unit(v.X - coords[i - 1].X, v.Y - coords[i - 1].Y);
            var (u2x, u2y) = Unit(coords[i + 1].X - v.X, coords[i + 1].Y - v.Y);
            double cross = u1x * u2y - u1y * u2x;
            double dot = u1x * u2x + u1y * u2y;

            if (Math.Abs(cross) < 1e-12)
            {
                // Collinear: single offset point.
                out_.Add((v.X + d * -u1y, v.Y + d * u1x));
                continue;
            }

            double theta = Math.Atan2(cross, dot); // signed turn angle u1 -> u2
            double sgn = d > 0 ? 1.0 : -1.0;
            double n1x = -u1y, n1y = u1x;   // left normal of incoming edge
            double n2x = -u2y, n2y = u2x;   // left normal of outgoing edge
            double t1x = v.X + d * n1x, t1y = v.Y + d * n1y;
            double t2x = v.X + d * n2x, t2y = v.Y + d * n2y;

            if (theta * sgn < 0)
            {
                // Convex in the offset direction: arc of radius |d| centred on
                // the original vertex, from T1 to T2.
                double r = Math.Abs(d);
                int steps = Math.Max(2, (int)Math.Round(Math.Abs(theta) / (Math.PI / 16.0)));
                double a1 = Math.Atan2(t1y - v.Y, t1x - v.X);
                double a2 = Math.Atan2(t2y - v.Y, t2x - v.X);
                double sweep = theta; // signed: direction follows the turn
                for (int k = 0; k <= steps; k++)
                {
                    double a = a1 + sweep * (k / (double)steps);
                    out_.Add((v.X + r * Math.Cos(a), v.Y + r * Math.Sin(a)));
                }
            }
            else
            {
                // Concave: intersection of the two offset lines.
                // T1 + α·u1 = T2 + β·u2
                double det = -cross; // |u1x -u2x; u1y -u2y|
                double rx = t2x - t1x, ry = t2y - t1y;
                double alpha = (-rx * u2y + u2x * ry) / det;
                out_.Add((t1x + alpha * u1x, t1y + alpha * u1y));
            }
        }

        // End point: offset of the last vertex along the last edge's normal.
        {
            var (ux, uy) = Unit(coords[^1].X - coords[^2].X, coords[^1].Y - coords[^2].Y);
            out_.Add((coords[^1].X + d * -uy, coords[^1].Y + d * ux));
        }

        return new() { out_ };
    }

    private static (double X, double Y) Unit(double x, double y)
    {
        double l = Math.Hypot(x, y);
        if (l < 1e-12) return (0.0, 1.0);
        return (x / l, y / l);
    }

    // --- Lane markings ---------------------------------------------------------

    /// <summary>Offset lane-marking lines for multi-lane carriageways (lanes &gt; 0).
    /// One-way (RQ 31 layout, right-hand traffic, from the median outward):
    ///   solid +W/2 (left edge), dashed +S/2 (between driving lanes),
    ///   solid -(W/2-S) (between travel lane and stop lane).
    /// Two-way: solid centreline 0; dashed ±k·l dividers; p_dash ±(W/2-P)
    /// parking-lane boundaries ending before junctions.
    ///
    /// NOTE on signs: shapely's offset_curve uses the standard math convention
    /// where a positive offset lands on the LEFT of the line's direction — but
    /// our world has Y pointing DOWN (south), which mirrors that convention, so
    /// a positive offset distance actually lands on the RIGHT of travel here.
    /// The calls below therefore use negated distances.</summary>
    public static List<(string Style, List<(double X, double Y)> Coords, double WidthM)>
        BuildLaneMarkings(RoadNetwork network)
    {
        var groups = new Dictionary<(string, double, int, double, bool, double), List<RoadSegment>>();
        foreach (var seg in network.Segments)
        {
            if (seg.Lanes <= 0) continue;
            if (Math.Hypot(seg.X2 - seg.X1, seg.Y2 - seg.Y1) < 1e-6) continue;
            var key = (seg.Highway, seg.Width, seg.Lanes, seg.Shoulder, seg.Oneway, seg.ParkingLaneWidth);
            if (!groups.TryGetValue(key, out var l)) groups[key] = l = new List<RoadSegment>();
            l.Add(seg);
        }

        var markings = new List<(string, List<(double X, double Y)>, double)>();
        foreach (var ((_, widthM, lanes, shoulderM, oneway, parkWM), segs) in groups)
        {
            double width = widthM, shoulder = shoulderM, park = parkWM;
            // Chain through plain bends only (never across a real junction).
            var raw = ChainSegments(segs, network);

            var oriented = new List<List<(double X, double Y)>>();
            foreach (var coords0 in raw)
            {
                var coords = SmoothChain(coords0);
                // linemerge does not preserve our direction of travel — re-
                // orient the line so its first point is a segment START.
                if (!segs.Any(s => Math.Abs(s.X1 - coords[0].X) < 1e-6 &&
                                   Math.Abs(s.Y1 - coords[0].Y) < 1e-6))
                    coords.Reverse();
                oriented.Add(coords);
            }

            List<List<(double X, double Y)>> OffsetOf(List<(double X, double Y)> coords, double dist) =>
                OffsetCurve(coords, dist);

            foreach (var coords in oriented)
            {
                // Paint stops before the crossing; guardrails keep full length.
                var (t0, t1) = JunctionMarkingTrim(network, coords);
                var paint = (t0 == 0 && t1 == 0) ? new List<(double X, double Y)>(coords)
                    : TrimEnds(new List<(double X, double Y)>(coords), t0, t1);
                if (paint.Count == 0) continue; // too short to fit paint away from both junctions

                if (oneway)
                {
                    foreach (var c in OffsetOf(paint, -width / 2)) markings.Add(("solid", c, 0.15));
                    foreach (var c in OffsetOf(paint, -shoulder / 2)) markings.Add(("dashed", c, 0.15));
                    foreach (var c in OffsetOf(paint, width / 2 - shoulder)) markings.Add(("solid", c, 0.30));
                }
                else
                {
                    // Solid centreline: crossing it = oncoming lane.
                    markings.Add(("solid", new List<(double X, double Y)>(paint), 0.15));
                    double D = width / 2 - park;            // driving strip per side
                    double l = D / lanes;            // driving-lane width
                    for (int k = 1; k < lanes; k++)
                    {
                        double dd = k * l;
                        foreach (var c in OffsetOf(paint, dd)) markings.Add(("dashed", c, 0.15));
                        foreach (var c in OffsetOf(paint, -dd)) markings.Add(("dashed", c, 0.15));
                    }
                    if (park > 0)
                    {
                        var (pt0, pt1) = JunctionTrim(network, coords);
                        var trimmed = TrimEnds(new List<(double X, double Y)>(coords), pt0, pt1);
                        if (trimmed.Count > 0)
                        {
                            foreach (var c in OffsetOf(trimmed, D)) markings.Add(("p_dash", c, 0.15));
                            foreach (var c in OffsetOf(trimmed, -D)) markings.Add(("p_dash", c, 0.15));
                        }
                    }
                }
            }

            // Central median: two carriageways of this group that run roughly
            // parallel within a couple of metres form a median strip; put a
            // light-gray crash barrier on each carriageway's edge of it.
            for (int i = 0; i <oriented.Count; i++)
                for (int j = i + 1; j < oriented.Count; j++)
                {
                    if (LineDistance(oriented[i], oriented[j]) >= 15.0) continue;
                    foreach (var (a, b) in new[] { (oriented[i], oriented[j]), (oriented[j], oriented[i]) })
                    {
                        double s = TowardSign(a, Midpoint(b));
                        foreach (var c in OffsetOf(a, s * (width / 2)))
                            markings.Add(("guardrail", c, 0.15));
                    }
                }
        }
        return markings;
    }

    // --- Oneway arrows / parking marks -----------------------------------------

    /// <summary>Painted direction arrows for one-way roads: one arrow per lane
    /// every 100 m (50 + k·100, kept &gt;= 20 m short of the far end), centred
    /// on the lane. Segments shorter than ~70 m get no arrow.</summary>
    public static List<List<(double X, double Y)>> BuildOnewayArrows(RoadNetwork network)
    {
        var arrows = new List<List<(double X, double Y)>>();
        foreach (var seg in network.Segments)
        {
            if (!seg.Oneway) continue;
            double dx = seg.X2 - seg.X1, dy = seg.Y2 - seg.Y1;
            double length = Math.Hypot(dx, dy);
            if (length < 1e-6) continue;
            double fx = dx / length, fy = dy / length; // unit direction of travel
            double rx = fy, ry = -fx;                      // right-hand side of it
            int nLanes = Math.Max(1, seg.Lanes == 0 ? 1 : seg.Lanes);
            double laneW = seg.Width / nLanes;             // metres
            for (int i = 0; i < nLanes; i++)
            {
                double offM = seg.Width / 2.0 - laneW * (i + 0.5); // right of centreline
                double sM = 50.0;
                while (sM <= seg.Length - 20.0)
                {
                    double cx = seg.X1 + fx * sM + rx * offM;
                    double cy = seg.Y1 + fy * sM + ry * offM;
                    arrows.Add(ArrowPolygon(cx, cy, fx, fy, rx, ry));
                    sM += 100.0;
                }
            }
        }
        return arrows;
    }

    /// <summary>A small painted direction arrow (3 m long): a 1.8 m shaft
    /// (0.6 m wide) with a 1.2 m head (1.4 m wide), centred on (cx, cy).</summary>
    private static List<(double X, double Y)> ArrowPolygon(
        double cx, double cy, double fx, double fy, double rx, double ry)
    {
        (double X, double Y) Pt(double sM, double offM) =>
            (cx + fx * sM + rx * offM, cy + fy * sM + ry * offM);
        return new()
        {
            Pt(-1.5, -0.3),  // tail, left
            Pt(0.3, -0.3),   // shaft -> head, left
            Pt(0.3, -0.7),   // head wing, left
            Pt(1.5, 0.0),    // tip
            Pt(0.3, 0.7),    // head wing, right
            Pt(0.3, 0.3),    // shaft -> head, right
            Pt(-1.5, 0.3),   // tail, right
        };
    }

    /// <summary>A blocky painted 'P' (2.0 m tall × 1.35 m wide), readable by
    /// traffic travelling along f: stem on the driver's left, bowl to the
    /// right. Four overlapping rectangles — no polygon holes needed.</summary>
    private static List<List<(double X, double Y)>> PPolygons(
        double cx, double cy, double fx, double fy, double rx, double ry)
    {
        List<(double X, double Y)> Rect(double s0, double s1, double o0, double o1) => new()
        {
            (cx + fx * s0 + rx * o0, cy + fy * s0 + ry * o0),
            (cx + fx * s1 + rx * o0, cy + fy * s1 + ry * o0),
            (cx + fx * s1 + rx * o1, cy + fy * s1 + ry * o1),
            (cx + fx * s0 + rx * o1, cy + fy * s0 + ry * o1),
        };
        // 5 × 7 pixel grid scaled to 1.35 m × 2.0 m (col w 0.27, row h ~0.286)
        return new()
        {
            Rect(-1.0, 1.0, -0.675, -0.405),    // stem (full height, left)
            Rect(0.714, 1.0, -0.675, 0.405),    // top bar
            Rect(0.143, 1.0, 0.405, 0.675),     // right side of the bowl
            Rect(-0.143, 0.143, -0.675, 0.405), // middle bar (closes the bowl)
        };
    }

    /// <summary>Painted P marks for parking lanes.</summary>
    public static List<List<(double X, double Y)>> BuildParkingMarks(RoadNetwork network)
    {
        double gap = Config.PARK_LANE_END_GAP_M;
        var marks = new List<List<(double X, double Y)>>();
        foreach (var seg in network.Segments)
        {
            if (seg.ParkingLaneWidth <= 0 || seg.Lanes <= 0) continue;
            double dx = seg.X2 - seg.X1, dy = seg.Y2 - seg.Y1;
            double length = Math.Hypot(dx, dy);
            if (length < 1e-6) continue;
            double fx = dx / length, fy = dy / length; // unit direction of travel
            double rx = fy, ry = -fx;                      // right-hand side of it
            // Parking lanes end >= PARK_LANE_END_GAP_M before a junction.
            double sMin = network.NodeDegree.GetValueOrDefault(seg.StartNode, 0) >= 3 ? gap : 0.0;
            double sMax = network.NodeDegree.GetValueOrDefault(seg.EndNode, 0) >= 3
                ? seg.Length - gap
                : seg.Length;
            // Centre of the parking lane, right of the centreline. Two-way
            // roads have one at each kerb; the left one faces the opposite way.
            double offM = seg.Width / 2.0 - seg.ParkingLaneWidth / 2.0;
            var sides = new List<(double Sign, double Fx, double Fy, double Rx, double Ry)>
            {
                (1.0, fx, fy, rx, ry),
            };
            if (!seg.Oneway)
                sides.Add((-1.0, -fx, -fy, -rx, -ry));
            foreach (var (sideSign, sfx, sfy, srx, sry) in sides)
            {
                double s = 50.0;
                while (s <= sMax - 20.0)
                {
                    if (s >= sMin + 1e-6)
                    {
                        double cx = seg.X1 + fx * s + rx * offM * sideSign;
                        double cy = seg.Y1 + fy * s + ry * offM * sideSign;
                        marks.AddRange(PPolygons(cx, cy, sfx, sfy, srx, sry));
                    }
                    s += 100.0;
                }
            }
        }
        return marks;
    }

    // --- Road polygons ----------------------------------------------------------

    private static (double X, double Y) Midpoint(List<(double X, double Y)> coords) =>
        coords[coords.Count / 2];

    /// <summary>+1 or -1: which offset-curve side of `coords` points toward
    /// `target`. The positive-offset normal in coordinate space is (-dy, dx)
    /// of the line direction.</summary>
    private static double TowardSign(List<(double X, double Y)> coords, (double X, double Y) target)
    {
        int i = Math.Max(1, coords.Count / 2 - 1);
        double dx = coords[i + 1].X - coords[i].X;
        double dy = coords[i + 1].Y - coords[i].Y;
        double l = Math.Hypot(dx, dy); if (l == 0) l = 1.0;
        double nx = -dy / l, ny = dx / l;
        var (mx, my) = Midpoint(coords);
        return nx * (target.X - mx) + ny * (target.Y - my) > 0 ? 1 : -1;
    }

    /// <summary>Mean distance from sample points of polyline c1 to polyline c2
    /// (small iff the two polylines run parallel close together).</summary>
    private static double LineDistance(List<(double X, double Y)> c1, List<(double X, double Y)> c2)
    {
        int n = Math.Max(4, c1.Count / 5);
        double total = 0.0;
        for (int k = 0; k < n; k++)
        {
            var (px, py) = c1[(int)(k * (c1.Count - 1) / (double)(n - 1))];
            double best = double.PositiveInfinity;
            for (int a = 0; a < c2.Count - 1; a++)
                best = Math.Min(best, PointToSegment(px, py, c2[a].X, c2[a].Y, c2[a + 1].X, c2[a + 1].Y).Dist);
            total += best;
        }
        return total / n;
    }

    /// <summary>Build road polygons from the §10 smoothed geometry (Catmull-Rom
    /// splines through the merged lines) instead of direct buffer on rounded
    /// polylines. The spline passes through every original node including
    /// degree-2 bends, so sharp zig-zag corners become slightly rounded curves.
    /// Grouped by (highway type, width) for coloring; holes included (e.g.
    /// roundabout islands). Junction fillets at degree-3+ nodes added via
    /// SmoothedNetwork.junction_fillets.</summary>
    public static List<RoadPolygonGroup> BuildRoadPolygons(RoadNetwork network)
    {
        var smNet = SmoothGeometry.For(network);

        // Width-varying roads (a highway chain that changes width along its
        // length) are built as ONE continuous variable-width ribbon: the
        // centreline is splined through the width-change nodes (bends rounded)
        // and offset by a smoothstep-tapered half-width. This is the only clean
        // way to get BOTH smooth bends AND smooth width transitions - separate
        // abutting/overlapping ribbons leave a notch or a bulge at an angled
        // transition. See docs/SPEC.md width-taper note.
        var (ribbons, covered) = BuildWidthVaryingRibbons(network);

        // smNet line -> its segment indices, so lines consumed by a ribbon can
        // be skipped below (the paved area must not be drawn twice).
        var lineSegs = new Dictionary<SmoothGeometry.SmoothedLine, HashSet<int>>();
        foreach (var (skey, cref) in smNet.SegmentCurve)
        {
            if (cref.Line is null) continue;
            if (!lineSegs.TryGetValue(cref.Line, out var set)) lineSegs[cref.Line] = set = new();
            set.Add(skey.SegIdx);
        }

        // Group the REMAINING (constant-width) lines by (highway, width).
        var groups = new Dictionary<(string, double), List<List<(double X, double Y)>>>();
        foreach (var line in smNet.Lines)
        {
            if (lineSegs.TryGetValue(line, out var lsegs) && lsegs.Count > 0 && lsegs.All(covered.Contains))
                continue;   // consumed by a width-varying ribbon
            var key = (line.Highway, line.Width);
            if (!groups.TryGetValue(key, out var l)) groups[key] = l = new List<List<(double X, double Y)>>();
            l.Add(line.Resampled);
        }

        var result = new List<RoadPolygonGroup>();
        foreach (var ((highway, width), allCoords) in groups)
        {
            double halfW = width / 2;
            var bufferedParts = allCoords.Select(coords =>
                (Geometry)RoadNetwork.ToLineString(coords).Buffer(halfW,
                    new BufferParameters(8, EndCapStyle.Flat, JoinStyle.Round, 5.0))).ToList();
            Geometry buffered = RoadNetwork.UnionAll(bufferedParts);

            var color = Config.ROAD_TYPES.TryGetValue(highway, out var rt) ? rt.Color : ((150, 150, 150));
            var exteriors = new List<PolygonRing>();
            foreach (Geometry g in buffered.Geometries().Length > 0 ? buffered.Geometries() : new[] { buffered })
            {
                if (!(g is Polygon p) || p.IsEmpty) continue;
                exteriors.Add(new PolygonRing(
                    p.ExteriorRing.Coordinates.Select(c => (c.X, c.Y)).ToList(),
                    p.InteriorRings.Select(r => r.Coordinates.Select(c => (c.X, c.Y)).ToList()).ToList()));
            }
            result.Add(new RoadPolygonGroup(color, exteriors));
        }

        result.AddRange(ribbons);

        // Junction corner roundings (Eckausrundung patches).
        result.AddRange(BuildSmoothedJunctionFillets(network, smNet));
        return result;
    }

    // --- Road paint (strokes) --------------------------------------------------

    /// <summary>One road = one ribbon: the two boundary polylines offset from
    /// the smoothed centreline, plus the fills that are NOT constant-width
    /// ribbons (width-varying chains, junction corner roundings).
    ///
    /// The renderer fills these directly instead of filling ONE unioned
    /// polygon per colour, so the paint needs no holes and no background
    /// punch: a roundabout island is simply the area no ribbon covers, and a
    /// road that crosses itself (a figure-8) paints its crossing twice in the
    /// same colour - which is what a crossing looks like. The unioned paved
    /// polygon stays for the KERB LINE (a kerb follows the true outer
    /// perimeter, not each road's own edge) and for the physics on-road test
    /// - see RoadNetwork.GetPavedPolygon.</summary>
    public static (List<RoadStroke> Strips, List<RoadPolygonGroup> Fills)
        BuildRoadPaint(RoadNetwork network)
    {
        var smNet = SmoothGeometry.For(network);
        var (ribbons, covered) = BuildWidthVaryingRibbons(network);

        // smNet line -> its segment indices, so lines consumed by a ribbon can
        // be skipped below (the paved area must not be painted twice).
        var lineSegs = new Dictionary<SmoothGeometry.SmoothedLine, HashSet<int>>();
        foreach (var (skey, cref) in smNet.SegmentCurve)
        {
            if (cref.Line is null) continue;
            if (!lineSegs.TryGetValue(cref.Line, out var set)) lineSegs[cref.Line] = set = new();
            set.Add(skey.SegIdx);
        }

        var strips = new List<RoadStroke>();
        foreach (var line in smNet.Lines)
        {
            if (lineSegs.TryGetValue(line, out var lsegs) && lsegs.Count > 0 && lsegs.All(covered.Contains))
                continue;   // painted as a width-varying ribbon below
            var (left, right) = Ribbon(line.Resampled, line.Width / 2);
            if (left.Count >= 2) strips.Add(new RoadStroke(left, right));
        }

        var fills = new List<RoadPolygonGroup>();
        fills.AddRange(ribbons);
        fills.AddRange(BuildSmoothedJunctionFillets(network, smNet));
        return (strips, fills);
    }

    /// <summary>Offset a densely resampled polyline by ±halfW per vertex.
    /// The vertex normal is the bisector of the adjacent edge directions,
    /// normalised, so the offset sits exactly halfWidth off the centreline
    /// (a miter join). Resampled splines turn a fraction of a degree per
    /// vertex, so this is as accurate as OffsetCurve - and the two edges stay
    /// vertex-paired, which is what a triangle strip needs.</summary>
    private static (List<(double X, double Y)> Left, List<(double X, double Y)> Right)
        Ribbon(List<(double X, double Y)> coords, double halfW)
    {
        var left = new List<(double X, double Y)>();
        var right = new List<(double X, double Y)>();
        if (coords.Count < 2) return (left, right);
        for (int i = 0; i < coords.Count; i++)
        {
            var (ax, ay) = i > 0 ? coords[i - 1] : coords[i];
            var (bx, by) = coords[i];
            var (cx, cy) = i + 1 < coords.Count ? coords[i + 1] : coords[i];
            var (ux, uy) = Unit(cx - ax, cy - ay);      // travel direction
            left.Add((bx - uy * halfW, by + ux * halfW));
            right.Add((bx + uy * halfW, by - ux * halfW));
        }
        return (left, right);
    }

    /// <summary>One road's paint: two vertex-paired boundary polylines.</summary>
    public sealed record RoadStroke(List<(double X, double Y)> Left,
        List<(double X, double Y)> Right);

    /// <summary>Build ONE continuous variable-width ribbon per highway chain
    /// that changes width along its length. The chain is splined through the
    /// width-change nodes (bends rounded), then offset by a half-width that
    /// follows the per-segment width with a smoothstep taper in the WIDER side
    /// of each change (length T = WIDTH_TAPER_RATIO*(W-w)). Returns the ribbon
    /// polygons + the segment indices they consume (so the constant-width
    /// buffer path can skip them). Constant-width chains are left to that
    /// path.</summary>
    /// <summary>A highway chain (segments joined through degree-2 nodes,
    /// regardless of width) whose width changes along its length. Shared
    /// source so the paved ribbon AND the dashed centreline use the exact same
    /// spline.</summary>
    private sealed record WidthChain(
        string Highway, List<int> SegIdx, List<(double X, double Y)> Chain,
        double[] EdgeW, bool TwoWay);

    private static List<WidthChain> ComputeWidthVaryingChains(RoadNetwork network)
    {
        var result = new List<WidthChain>();
        static long K(double v) => (long)Math.Round(v * 16.0);
        var segByEnds = new Dictionary<(long, long, long, long), int>();
        for (int i = 0; i < network.Segments.Count; i++)
        {
            var s = network.Segments[i];
            segByEnds[(K(s.X1), K(s.Y1), K(s.X2), K(s.Y2))] = i;
            segByEnds[(K(s.X2), K(s.Y2), K(s.X1), K(s.Y1))] = i;
        }
        var byHw = new Dictionary<string, List<RoadSegment>>();
        foreach (var s in network.Segments)
        {
            if (Math.Hypot(s.X2 - s.X1, s.Y2 - s.Y1) < 1e-6) continue;
            if (!byHw.TryGetValue(s.Highway, out var l)) byHw[s.Highway] = l = new();
            l.Add(s);
        }
        foreach (var (highway, segs) in byHw)
        {
            foreach (var chain in ChainSegments(segs, network))
            {
                if (chain.Count < 2) continue;
                var edgeW = new double[chain.Count - 1];
                var segIdx = new List<int>(chain.Count - 1);
                bool ok = true, hasChange = false, twoWay = true;
                for (int i = 0; i < chain.Count - 1; i++)
                {
                    if (!segByEnds.TryGetValue((K(chain[i].X), K(chain[i].Y),
                            K(chain[i + 1].X), K(chain[i + 1].Y)), out int si)) { ok = false; break; }
                    segIdx.Add(si);
                    edgeW[i] = network.Segments[si].Width;
                    if (network.Segments[si].Oneway) twoWay = false;
                    if (i > 0 && Math.Abs(edgeW[i] - edgeW[i - 1]) > 1e-6) hasChange = true;
                }
                if (!ok || !hasChange) continue;
                result.Add(new WidthChain(highway, segIdx, chain, edgeW, twoWay));
            }
        }
        return result;
    }

    /// <summary>Paved ribbons for the width-varying chains + the set of segment
    /// indices they consume (so the constant-width buffer path skips them).</summary>
    private static (List<RoadPolygonGroup> Ribbons, HashSet<int> Covered)
        BuildWidthVaryingRibbons(RoadNetwork network)
    {
        var ribbons = new List<RoadPolygonGroup>();
        var covered = new HashSet<int>();
        foreach (var wc in ComputeWidthVaryingChains(network))
        {
            var ring = WidthVaryingRibbon(wc.Chain, wc.EdgeW);
            if (ring is null) continue;
            try
            {
                Geometry poly = RoadNetwork.ToPolygon(new PolygonRing(ring, new()));
                if (!poly.IsValid) poly = poly.Buffer(0);
                if (poly.IsEmpty) continue;
                var color = Config.ROAD_TYPES.TryGetValue(wc.Highway, out var rt) ? rt.Color : ((150, 150, 150));
                var rings = new List<PolygonRing>();
                foreach (Geometry g in poly.Geometries().Length > 0 ? poly.Geometries() : new[] { poly })
                    if (g is Polygon p && !p.IsEmpty)
                        rings.Add(new PolygonRing(
                            p.ExteriorRing.Coordinates.Select(c => (c.X, c.Y)).ToList(),
                            p.InteriorRings.Select(r => r.Coordinates.Select(c => (c.X, c.Y)).ToList()).ToList()));
                if (rings.Count == 0) continue;
                ribbons.Add(new RoadPolygonGroup(color, rings));
                foreach (var si in wc.SegIdx) covered.Add(si);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[width-ribbon] skip ({wc.Highway}): {ex.Message}");
            }
        }
        return (ribbons, covered);
    }

    /// <summary>For width-varying chains: the segment indices they consume
    /// (so per-width dashed centrelines can be skipped) + for the TWO-WAY ones
    /// a single dashed centreline = the SAME chain spline the paved ribbon
    /// uses (so the dashes sit on the paved centre), with the chain's max width
    /// (for the min-width-to-draw-a-centreline test).</summary>
    public static (HashSet<int> Covered,
        List<(List<(double X, double Y)> Coords, double MaxWidthM)> TwoWay)
        WidthVaryingCenterlines(RoadNetwork network)
    {
        var covered = new HashSet<int>();
        var twoWay = new List<(List<(double X, double Y)>, double)>();
        foreach (var wc in ComputeWidthVaryingChains(network))
        {
            foreach (var si in wc.SegIdx) covered.Add(si);
            if (!wc.TwoWay) continue;
            var curve = new SmoothGeometry.SmoothCurve(wc.Chain);
            if (curve.Total <= 1e-6) continue;
            twoWay.Add((SmoothGeometry.ResampleCurve(curve, 0.5), wc.EdgeW.Max()));
        }
        return (covered, twoWay);
    }

    /// <summary>Offset a splined, resampled chain centreline by a half-width
    /// that follows the per-edge widths with a smoothstep taper in the wider
    /// side of each interior width change. Returns the closed ribbon ring.</summary>
    private static List<(double X, double Y)>? WidthVaryingRibbon(
        List<(double X, double Y)> chain, double[] edgeW)
    {
        var curve = new SmoothGeometry.SmoothCurve(chain);
        double total = curve.Total;
        if (total <= 1e-6) return null;
        var pts = SmoothGeometry.ResampleCurve(curve, 0.5);
        int n = pts.Count;
        if (n < 2) return null;

        var nodeS = new double[chain.Count];
        for (int i = 0; i < chain.Count; i++)
            nodeS[i] = SmoothGeometry.SmoothedNetwork.NodeS(curve, chain[i]);

        double ratio = Config.WIDTH_TAPER_RATIO;
        static double Smooth(double u) { u = Math.Clamp(u, 0.0, 1.0); return u * u * (3.0 - 2.0 * u); }
        double HalfWidthAt(double s)
        {
            int e = 0;
            for (int i = 0; i < edgeW.Length; i++) if (s >= nodeS[i]) e = i;
            double w = edgeW[Math.Clamp(e, 0, edgeW.Length - 1)];
            for (int j = 1; j < chain.Count - 1 && j < edgeW.Length; j++)
            {
                double wl = edgeW[j - 1], wr = edgeW[j];
                if (Math.Abs(wl - wr) < 1e-6) continue;
                double wide = Math.Max(wl, wr), narrow = Math.Min(wl, wr);
                double T = ratio * (wide - narrow);
                if (T <= 0) continue;
                if (wl > wr)
                {
                    if (s >= nodeS[j] - T && s <= nodeS[j])
                        w = Math.Min(w, narrow + (wide - narrow) * Smooth((nodeS[j] - s) / T));
                }
                else
                {
                    if (s >= nodeS[j] && s <= nodeS[j] + T)
                        w = Math.Min(w, narrow + (wide - narrow) * Smooth((s - nodeS[j]) / T));
                }
            }
            return w / 2.0;
        }

        var left = new List<(double X, double Y)>(n);
        var right = new List<(double X, double Y)>(n);
        for (int i = 0; i < n; i++)
        {
            double s = total * i / (n - 1);
            int a = Math.Max(0, i - 1), b = Math.Min(n - 1, i + 1);
            double tx = pts[b].X - pts[a].X, ty = pts[b].Y - pts[a].Y;
            double tl = Math.Hypot(tx, ty);
            if (tl < 1e-9) { tx = 1; ty = 0; tl = 1; }
            tx /= tl; ty /= tl;
            double nx = -ty, ny = tx;
            double h = HalfWidthAt(s);
            left.Add((pts[i].X + nx * h, pts[i].Y + ny * h));
            right.Add((pts[i].X - nx * h, pts[i].Y - ny * h));
        }
        var ring = new List<(double X, double Y)>(2 * n + 1);
        ring.AddRange(left);
        for (int i = n - 1; i >= 0; i--) ring.Add(right[i]);
        ring.Add(left[0]);
        return ring;
    }
    /// <summary>Build the Eckausrundung patches: at every junction corner,
    /// the curvilinear triangle between the corner point and the rounding arc
    /// - the paved fill that lets a car swing from one road into the other
    /// without leaving the pavement - closed back at the junction node so the
    /// patch is part of the paved area rather than a neighbour of it. The
    /// arcs come precomputed from SmoothedNetwork.junction_fillets.</summary>
    public static List<RoadPolygonGroup> BuildSmoothedJunctionFillets(
        RoadNetwork network, SmoothGeometry.SmoothedNetwork smNet)
    {
        var extras = new List<RoadPolygonGroup>();
        foreach (var fillet in smNet.JunctionFillets)
        {
            // The patch must be PART of the paved area, not merely touch it.
            // Closed at the corner point it is bounded by the two road EDGES,
            // so at an X crossing - where the corner wedge sits between two
            // arms whose edges meet exactly on those lines - it shares only
            // a line with the tarmac: zero overlap, and the paved union keeps
            // it as its own ring, which draws as a kerb outline floating in
            // the grass (measured: 0.0 % overlap east/west, 3.7-4.5 % where
            // the corner geometry happened to bite into the road). Closing
            // the patch at the junction node - which is inside the road -
            // gives it a real overlap; the extra triangle is tarmac the
            // junction already paints, so nothing is painted that was not
            // painted before.
            var ring = new List<(double X, double Y)>(fillet.Arc);
            if (network.Nodes.TryGetValue(fillet.Node, out var nxy))
                ring.Add((nxy.X, nxy.Y));
            var color = Config.ROAD_TYPES.TryGetValue(
                network.Segments[fillet.SegA].Highway, out var rt) ? rt.Color : ((150, 150, 150));
            extras.Add(new RoadPolygonGroup(color, new() { new PolygonRing(ring, new()) }));
        }
        return extras;
    }

    // --- Corner rounding ---------------------------------------------------------

    /// <summary>Per-vertex tangent-distance allowance, sharing each edge between
    /// the two corners that use it in proportion to what they actually need.
    /// (The simple half-edge rule is badly over-conservative when a corner's
    /// neighbour needs little: on the sliver junction it produced a 2.06 m
    /// fillet radius — inside the car's 3.46 m minimum turning radius, i.e. a
    /// reference line no car could follow.)</summary>
    private static List<double> CornerTangentBudget(
        List<(double X, double Y)> coords, double radius)
    {
        int n = coords.Count;
        var want = new double[n];
        for (int i = 1; i < n - 1; i++)
        {
            double ax = coords[i - 1].X - coords[i].X, ay = coords[i - 1].Y - coords[i].Y;
            double bx = coords[i + 1].X - coords[i].X, by = coords[i + 1].Y - coords[i].Y;
            double la = Math.Hypot(ax, ay), lb = Math.Hypot(bx, by);
            if (la < 1e-9 || lb < 1e-9) continue;
            double dot = Math.Clamp((ax * bx + ay * by) / (la * lb), -1.0, 1.0);
            double gap = Math.Acos(dot);
            if (gap < 1e-6 || gap > Math.PI - 1e-6) continue;
            want[i] = radius / Math.Tan(gap / 2);
        }

        var budget = new List<double>(new double[n].Select(_ => double.PositiveInfinity));
        for (int i = 0; i < n - 1; i++)
        {
            double le = Math.Hypot(coords[i + 1].X - coords[i].X, coords[i + 1].Y - coords[i].Y);
            double d1 = want[i], d2 = want[i + 1];
            if (d1 + d2 > le && (d1 + d2) > 1e-9)
            {
                double scale = le / (d1 + d2);
                d1 *= scale; d2 *= scale;
            }
            budget[i] = Math.Min(budget[i], d1 > 0 ? d1 : double.PositiveInfinity);
            budget[i + 1] = Math.Min(budget[i + 1], d2 > 0 ? d2 : double.PositiveInfinity);
        }
        return budget;
    }

    /// <summary>Replace every interior vertex of a polyline with a circular arc
    /// of the given radius, tangent to both adjoining edges — actually round
    /// the line's own corners. Endpoints untouched.
    /// fitEdges: share each edge between its two corners in proportion to demand
    /// (used for the driving line, where an over-tight fillet is unfollowable).
    /// Rendering keeps the half rule so road shapes are unchanged.</summary>
    public static List<(double X, double Y)> RoundPolylineCorners(
        List<(double X, double Y)> coords, double radius, int arcSteps = 10, bool fitEdges = false)
    {
        if (coords.Count < 3 || radius <= 0)
            return new List<(double X, double Y)>(coords);
        var budget = fitEdges ? CornerTangentBudget(coords, radius) : null;

        var result = new List<(double X, double Y)> { coords[0] };
        for (int i = 1; i < coords.Count - 1; i++)
        {
            var (px, py) = coords[i - 1];
            var (vx, vy) = coords[i];
            var (nx2, ny2) = coords[i + 1];

            double ax = px - vx, ay = py - vy;
            double bx = nx2 - vx, by = ny2 - vy;
            double aLen = Math.Hypot(ax, ay), bLen = Math.Hypot(bx, by);
            if (aLen < 1e-9 || bLen < 1e-9) { result.Add((vx, vy)); continue; }
            ax /= aLen; ay /= aLen; bx /= bLen; by /= bLen;

            // Angle between the two edges (both pointing away from the vertex).
            double dot = Math.Clamp(ax * bx + ay * by, -1.0, 1.0);
            double gap = Math.Acos(dot);
            if (gap < 1e-6 || gap > Math.PI - 1e-6)
            {
                // Straight-through (or fully doubled-back) — no real corner.
                result.Add((vx, vy));
                continue;
            }

            double halfGap = gap / 2;
            double tangentDist = radius / Math.Tan(halfGap);
            // Cap so it never eats more than half of either adjoining edge.
            if (budget is not null)
                tangentDist = Math.Min(tangentDist, budget[i]);
            else
                tangentDist = Math.Min(tangentDist, Math.Min(aLen / 2, bLen / 2));
            double actualRadius = tangentDist * Math.Tan(halfGap);

            double centerDist = actualRadius / Math.Sin(halfGap);
            double bisX = ax + bx, bisY = ay + by;
            double bisLen = Math.Hypot(bisX, bisY);
            if (bisLen < 1e-9) { result.Add((vx, vy)); continue; }
            bisX /= bisLen; bisY /= bisLen;

            double centerX = vx + centerDist * bisX;
            double centerY = vy + centerDist * bisY;
            double t1x = vx + tangentDist * ax, t1y = vy + tangentDist * ay;
            double t2x = vx + tangentDist * bx, t2y = vy + tangentDist * by;

            double angleT1 = Math.Atan2(t1y - centerY, t1x - centerX);
            double angleT2 = Math.Atan2(t2y - centerY, t2x - centerX);

            // The correct arc sweep is always the EXTERIOR turn angle (pi-gap).
            // t1 must ALWAYS be emitted before t2 — swapping self-intersects.
            double fwdSweep = (angleT2 - angleT1) % (2 * Math.PI);
            if (fwdSweep < 0) fwdSweep += 2 * Math.PI;
            double expectedSweep = Math.PI - gap;
            int direction; double sweep;
            if (Math.Abs(fwdSweep - expectedSweep) < Math.Abs(2 * Math.PI - fwdSweep - expectedSweep))
                (direction, sweep) = (1, fwdSweep);
            else
                (direction, sweep) = (-1, 2 * Math.PI - fwdSweep);

            result.Add((t1x, t1y));
            for (int s = 1; s < arcSteps; s++)
            {
                double a = angleT1 + direction * sweep * (s / (double)arcSteps);
                result.Add((centerX + actualRadius * Math.Cos(a), centerY + actualRadius * Math.Sin(a)));
            }
            result.Add((t2x, t2y));
        }
        result.Add(coords[^1]);
        return result;
    }

    // --- Snapping -----------------------------------------------------------------

    /// <summary>Snap dangling segment endpoints onto nearby roads. Returns the
    /// number of snapped endpoints.</summary>
    public static int SnapEndpoints(List<RoadSegment> segments, double snapM = 8.0)
    {
        double cell = Math.Max(snapM, 1.0);
        var grid = new Dictionary<(int Cx, int Cy), List<int>>();

        for (int i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            double minx = Math.Min(seg.X1, seg.X2), maxx = Math.Max(seg.X1, seg.X2);
            double miny = Math.Min(seg.Y1, seg.Y2), maxy = Math.Max(seg.Y1, seg.Y2);
            for (int cx = (int)Math.Floor(minx / cell); cx <= (int)Math.Floor(maxx / cell); cx++)
                for (int cy = (int)Math.Floor(miny / cell); cy <= (int)Math.Floor(maxy / cell); cy++)
                {
                    if (!grid.TryGetValue((cx, cy), out var l)) grid[(cx, cy)] = l = new List<int>();
                    l.Add(i);
                }
        }

        int snapped = 0;
        for (int i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            foreach (bool isStart in new[] { true, false })
            {
                double px = isStart ? seg.X1 : seg.X2;
                double py = isStart ? seg.Y1 : seg.Y2;
                int cx = (int)Math.Floor(px / cell), cy = (int)Math.Floor(py / cell);
                double bestD = snapM;
                (double X, double Y)? bestPt = null;
                for (int gx = cx - 1; gx <= cx + 1; gx++)
                    for (int gy = cy - 1; gy <= cy + 1; gy++)
                    {
                        if (!grid.TryGetValue((gx, gy), out var cellSegs)) continue;
                        foreach (int j in cellSegs)
                        {
                            if (j == i) continue;
                            var o = segments[j];
                            var (d, qx, qy) = PointToSegment(px, py, o.X1, o.Y1, o.X2, o.Y2);
                            if (d < bestD) { bestD = d; bestPt = (qx, qy); }
                        }
                    }
            if (bestPt is not null && bestD > 0.5)
            {
                if (isStart) { seg.X1 = bestPt.Value.X; seg.Y1 = bestPt.Value.Y; }
                else { seg.X2 = bestPt.Value.X; seg.Y2 = bestPt.Value.Y; }
                snapped++;
            }
            }
        }
        return snapped;
    }
}
