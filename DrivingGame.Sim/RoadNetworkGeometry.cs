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

    /// <summary>Convert lat/lon to world pixel coordinates (equirectangular).</summary>
    public static (double X, double Y) LatLonToWorld(double lat, double lon,
        double refLat, double refLon, double pppm)
    {
        double metersPerLatDeg = 111132.9 - 566.0 * Math.Cos(2 * Rad(lat)) + 1.2 * Math.Cos(4 * Rad(lat));
        double metersPerLonDeg = 111320 * Math.Cos(Rad(lat));

        double dxM = (lon - refLon) * metersPerLonDeg;
        double dyM = (lat - refLat) * metersPerLatDeg;

        return (dxM * pppm, dyM * pppm);
    }

    private static double Rad(double deg) => deg * Math.PI / 180.0;

    /// <summary>(distance, proj_x, proj_y) from point to line segment.</summary>
    public static (double Dist, double Px, double Py) PointToSegment(
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
            bool skipMultiLane = false, bool stopAtJunctions = false)
    {
        double pppm = Config.PIXELS_PER_METER;
        double cornerRadiusPx = Config.ROAD_CORNER_RADIUS_M * pppm;

        var groups = new Dictionary<(string, double), List<RoadSegment>>();
        foreach (var seg in network.Segments)
        {
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
            result[key] = raw.Select(coords => RoundPolylineCorners(coords, cornerRadiusPx)).ToList();
        }
        return result;
    }

    /// <summary>Flatten MergeAndRoundLines()'s per-group coordinate lists into
    /// one plain list of polylines. One-way segments excluded.</summary>
    public static List<List<(double X, double Y)>> BuildCenterlines(RoadNetwork network)
    {
        var groups = MergeAndRoundLines(network, onlyTwoWay: true);
        return groups.Values.SelectMany(lines => lines).ToList();
    }

    /// <summary>(start_trim, end_trim) in pixels: PARK_LANE_END_GAP_M where the
    /// line's end sits at a real junction (node degree &gt;= 3), else 0.</summary>
    public static (double Start, double End) JunctionTrimM(RoadNetwork network,
        List<(double X, double Y)> coords)
    {
        double pppm = Config.PIXELS_PER_METER;
        double gap = Config.PARK_LANE_END_GAP_M * pppm;

        double TrimAt(double px, double py)
        {
            foreach (var (nid, nxy) in network.Nodes)
            {
                if (network.NodeDegree.GetValueOrDefault(nid, 0) < 3) continue;
                if ((nxy.X - px) * (nxy.X - px) + (nxy.Y - py) * (nxy.Y - py) <= Math.Pow(0.5 * pppm, 2))
                    return gap;
            }
            return 0.0;
        }

        return (TrimAt(coords[0].X, coords[0].Y), TrimAt(coords[^1].X, coords[^1].Y));
    }

    /// <summary>(start_trim, end_trim) in pixels for PAINTED lane markings:
    /// where a line's end sits at a real junction, the paint stops half the
    /// WIDEST arm's width plus CENTERLINE_JUNCTION_GAP_M before the node centre.</summary>
    public static (double Start, double End) JunctionMarkingTrimPx(RoadNetwork network,
        List<(double X, double Y)> coords)
    {
        double pppm = Config.PIXELS_PER_METER;

        double GapAt(double px, double py)
        {
            foreach (var (nid, nxy) in network.Nodes)
            {
                if (network.NodeDegree.GetValueOrDefault(nid, 0) < 3) continue;
                if ((nxy.X - px) * (nxy.X - px) + (nxy.Y - py) * (nxy.Y - py) <= Math.Pow(0.5 * pppm, 2))
                {
                    var connected = network.GetConnectedSegments(nid);
                    double widest = connected.Max(i => network.Segments[i].Width);
                    return (widest / 2.0 + Config.CENTERLINE_JUNCTION_GAP_M) * pppm;
                }
            }
            return 0.0;
        }

        return (GapAt(coords[0].X, coords[0].Y), GapAt(coords[^1].X, coords[^1].Y));
    }

    /// <summary>Drop `startM` / `endM` pixels of arc length from a polyline's
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
        double pppm = Config.PIXELS_PER_METER;
        double cornerRadiusPx = Config.ROAD_CORNER_RADIUS_M * pppm;

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
        foreach (var ((_, widthM, lanes, shoulderM, oneway, parkW), segs) in groups)
        {
            double W = widthM * pppm;
            double S = shoulderM * pppm;
            double P = parkW * pppm;
            // Chain through plain bends only (never across a real junction).
            var raw = ChainSegments(segs, network);

            var oriented = new List<List<(double X, double Y)>>();
            foreach (var coords0 in raw)
            {
                var coords = RoundPolylineCorners(coords0, cornerRadiusPx);
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
                var (t0, t1) = JunctionMarkingTrimPx(network, coords);
                var paint = (t0 == 0 && t1 == 0) ? new List<(double X, double Y)>(coords)
                    : TrimEnds(new List<(double X, double Y)>(coords), t0, t1);
                if (paint.Count == 0) continue; // too short to fit paint away from both junctions

                if (oneway)
                {
                    foreach (var c in OffsetOf(paint, -W / 2)) markings.Add(("solid", c, 0.15));
                    foreach (var c in OffsetOf(paint, -S / 2)) markings.Add(("dashed", c, 0.15));
                    foreach (var c in OffsetOf(paint, W / 2 - S)) markings.Add(("solid", c, 0.30));
                }
                else
                {
                    // Solid centreline: crossing it = oncoming lane.
                    markings.Add(("solid", new List<(double X, double Y)>(paint), 0.15));
                    double D = W / 2 - P;            // driving strip per side
                    double l = D / lanes;            // driving-lane width
                    for (int k = 1; k < lanes; k++)
                    {
                        double dd = k * l;
                        foreach (var c in OffsetOf(paint, dd)) markings.Add(("dashed", c, 0.15));
                        foreach (var c in OffsetOf(paint, -dd)) markings.Add(("dashed", c, 0.15));
                    }
                    if (P > 0)
                    {
                        var (pt0, pt1) = JunctionTrimM(network, coords);
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
                    if (LineDistance(oriented[i], oriented[j]) >= 15.0 * pppm) continue;
                    foreach (var (a, b) in new[] { (oriented[i], oriented[j]), (oriented[j], oriented[i]) })
                    {
                        double s = TowardSign(a, Midpoint(b));
                        foreach (var c in OffsetOf(a, s * (W / 2)))
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
        double pppm = Config.PIXELS_PER_METER;
        var arrows = new List<List<(double X, double Y)>>();
        foreach (var seg in network.Segments)
        {
            if (!seg.Oneway) continue;
            double dx = seg.X2 - seg.X1, dy = seg.Y2 - seg.Y1;
            double lengthPx = Math.Hypot(dx, dy);
            if (lengthPx < 1e-6) continue;
            double fx = dx / lengthPx, fy = dy / lengthPx; // unit direction of travel
            double rx = fy, ry = -fx;                      // right-hand side of it
            int nLanes = Math.Max(1, seg.Lanes == 0 ? 1 : seg.Lanes);
            double laneW = seg.Width / nLanes;             // metres
            for (int i = 0; i < nLanes; i++)
            {
                double offM = seg.Width / 2.0 - laneW * (i + 0.5); // right of centreline
                double s = 50.0;
                while (s <= seg.Length - 20.0)
                {
                    double cx = seg.X1 + fx * s * pppm + rx * offM * pppm;
                    double cy = seg.Y1 + fy * s * pppm + ry * offM * pppm;
                    arrows.Add(ArrowPolygon(cx, cy, fx, fy, rx, ry, pppm));
                    s += 100.0;
                }
            }
        }
        return arrows;
    }

    /// <summary>A small painted direction arrow (3 m long): a 1.8 m shaft
    /// (0.6 m wide) with a 1.2 m head (1.4 m wide), centred on (cx, cy).</summary>
    private static List<(double X, double Y)> ArrowPolygon(
        double cx, double cy, double fx, double fy, double rx, double ry, double pppm)
    {
        (double X, double Y) Pt(double sM, double offM) =>
            (cx + fx * sM * pppm + rx * offM * pppm, cy + fy * sM * pppm + ry * offM * pppm);
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
        double cx, double cy, double fx, double fy, double rx, double ry, double pppm)
    {
        List<(double X, double Y)> Rect(double s0, double s1, double o0, double o1) => new()
        {
            (cx + fx * s0 * pppm + rx * o0 * pppm, cy + fy * s0 * pppm + ry * o0 * pppm),
            (cx + fx * s1 * pppm + rx * o0 * pppm, cy + fy * s1 * pppm + ry * o0 * pppm),
            (cx + fx * s1 * pppm + rx * o1 * pppm, cy + fy * s1 * pppm + ry * o1 * pppm),
            (cx + fx * s0 * pppm + rx * o1 * pppm, cy + fy * s0 * pppm + ry * o1 * pppm),
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
        double pppm = Config.PIXELS_PER_METER;
        double gap = Config.PARK_LANE_END_GAP_M * pppm;
        var marks = new List<List<(double X, double Y)>>();
        foreach (var seg in network.Segments)
        {
            if (seg.ParkingLaneWidth <= 0 || seg.Lanes <= 0) continue;
            double dx = seg.X2 - seg.X1, dy = seg.Y2 - seg.Y1;
            double lengthPx = Math.Hypot(dx, dy);
            if (lengthPx < 1e-6) continue;
            double fx = dx / lengthPx, fy = dy / lengthPx; // unit direction of travel
            double rx = fy, ry = -fx;                      // right-hand side of it
            // Parking lanes end >= PARK_LANE_END_GAP_M before a junction.
            double sMin = network.NodeDegree.GetValueOrDefault(seg.StartNode, 0) >= 3 ? gap : 0.0;
            double sMax = network.NodeDegree.GetValueOrDefault(seg.EndNode, 0) >= 3
                ? seg.Length * pppm - gap
                : seg.Length * pppm;
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
                double s = 50.0 * pppm;
                while (s <= sMax - 20.0 * pppm)
                {
                    if (s >= sMin + 1e-6)
                    {
                        double cx = seg.X1 + fx * s + rx * offM * sideSign * pppm;
                        double cy = seg.Y1 + fy * s + ry * offM * sideSign * pppm;
                        marks.AddRange(PPolygons(cx, cy, sfx, sfy, srx, sry, pppm));
                    }
                    s += 100.0 * pppm;
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
        double pppm = Config.PIXELS_PER_METER;
        var smNet = SmoothGeometry.For(network);

        // Group lines by (highway, width).
        var groups = new Dictionary<(string, double), List<List<(double X, double Y)>>>();
        foreach (var line in smNet.Lines)
        {
            var key = (line.Highway, line.Width);
            if (!groups.TryGetValue(key, out var l)) groups[key] = l = new List<List<(double X, double Y)>>();
            l.Add(line.Resampled);
        }

        var result = new List<RoadPolygonGroup>();
        foreach (var ((highway, width), allCoords) in groups)
        {
            double halfWPx = (width / 2) * pppm;

            // Buffer each line's spline and union them together.
            var bufferedParts = allCoords.Select(coords =>
                RoadNetwork.ToLineString(coords).Buffer(halfWPx,
                    new BufferParameters(8, EndCapStyle.Flat, JoinStyle.Round, 5.0))
            ).ToList();
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

        // Junction corner roundings (Eckausrundung patches).
        result.AddRange(BuildSmoothedJunctionFillets(network, smNet));
        return result;
    }

    /// <summary>Build the Eckausrundung patches: at every junction corner, the
    /// curvilinear triangle between the corner point and the rounding arc —
    /// the paved fill that lets a car swing from one road into the other
    /// without leaving the pavement. The arcs come precomputed from
    /// SmoothedNetwork.junction_fillets.</summary>
    public static List<RoadPolygonGroup> BuildSmoothedJunctionFillets(
        RoadNetwork network, SmoothGeometry.SmoothedNetwork smNet)
    {
        var extras = new List<RoadPolygonGroup>();
        foreach (var fillet in smNet.JunctionFillets)
        {
            var ring = new List<(double X, double Y)> { fillet.Corner };
            ring.AddRange(fillet.Arc);
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
    public static int SnapEndpoints(List<RoadSegment> segments, double pppm, double snapM = 8.0)
    {
        double snapPx = snapM * pppm;
        double cell = Math.Max(snapPx, 1.0);
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
                double bestD = snapPx;
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
