// Obstacles (docs/OBSTACLES.md, Part 1) — 1:1 port of car/src/obstacles.py.
// Static parked-car obstacles: the shared placement logic used by BOTH the
// palette UI and the REST API (identical auto-alignment, identical off-road
// rejection), lane-direction auto-alignment from road geometry, the
// stop-on-contact collision response, and JSON save/load of obstacle layouts
// per map.

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DrivingGame.Sim;

/// <summary>Geometry helpers (world metres, north-up frame) + the palette
/// colors. The concrete RGB values are a rendering detail
/// (docs/OBSTACLES.md): they must stay clearly distinguishable from the
/// player's red #B41E1E and from each other.</summary>
/// <summary>Thrown when a pose is not on the road network. Named for what it always
/// meant - a placement that cannot be resolved - and it survives the obstacle entity
/// (#27) because the geometry helpers still answer "is this point on a road at all"
/// (LaneHeadingAt).</summary>
public sealed class PlacementError : Exception
{
    public PlacementError(string message) : base(message) { }
}

public static class ObstacleGeometry
{
    public static readonly Dictionary<string, (int R, int G, int B)> ObstacleColors = new()
    {
        ["blue"]   = (65, 105, 220),
        ["yellow"] = (235, 195, 45),
        ["white"]  = (238, 238, 238),
    };

    /// <summary>Ghost tint for an invalid (off-road) drop target.</summary>
    public static readonly (int R, int G, int B) GhostInvalidRgb = (230, 60, 60);

    /// <summary>Four corners of an oriented rectangle centered at (cx, cy).</summary>
    public static List<(double X, double Y)> BoxCorners(double cx, double cy,
                                                        double headingDeg,
                                                        double lengthM, double widthM)
    {
        double h = Math.Radians(headingDeg);
        double fx = Math.Sin(h), fy = Math.Cos(h);      // forward
        double rx = Math.Cos(h), ry = -Math.Sin(h);     // right
        double halfLen = lengthM / 2.0;
        double halfWid = widthM / 2.0;
        return new List<(double X, double Y)>
        {
            (cx + fx * halfLen + rx * halfWid, cy + fy * halfLen + ry * halfWid),   // front-right
            (cx + fx * halfLen - rx * halfWid, cy + fy * halfLen - ry * halfWid),   // front-left
            (cx - fx * halfLen - rx * halfWid, cy - fy * halfLen - ry * halfWid),   // rear-left
            (cx - fx * halfLen + rx * halfWid, cy - fy * halfLen + ry * halfWid),   // rear-right
        };
    }

    /// <summary>The parked car's footprint: the MODEL's metres (#22), not the
    /// player car's. Before this every obstruction was the sedan box "regardless
    /// of its color" — so a parked mixer (8.40 m) blocked a sedan's (4.40 m) of
    /// road, and a car squeezing past it computed a gap that did not exist.</summary>


    /// <summary>The player car's body box - the same four-corner geometry the
    /// on-road check uses (centered on the body centre, not the rear axle).
    /// Uses the car's REAL per-class footprint (Config.VEHICLE_SIZES).</summary>
    public static List<(double X, double Y)> PlayerBodyCorners(Car car)
    {
        var (bx, by) = car.BodyCenter();
        return BoxCorners(bx, by, car.Heading, car.LengthM, car.WidthM);
    }

    private static (double Min, double Max) ProjectOnto(
        List<(double X, double Y)> corners, double nx, double ny)
    {
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        foreach (var (x, y) in corners)
        {
            double d = x * nx + y * ny;
            if (d < min) min = d;
            if (d > max) max = d;
        }
        return (min, max);
    }

    /// <summary>3D body box: 2D footprint corners + z level. A car on level L
    /// occupies the z interval [L, L+1); a ground obstacle occupies [0, 1).
    /// Collision requires EQUAL levels AND footprint overlap - the x/y SAT is
    /// unchanged, the level is an additional filter: a bridge car (level 1)
    /// passes over ground traffic (level 0) at the fig8 self-crossing without
    /// contact, and vice versa. Levels come from RoadSegment.Level (0 ground,
    /// 1 deck, 2 deck-on-deck, -1 tunnel).</summary>
    public readonly struct BodyBox
    {
        public List<(double X, double Y)> Corners { get; }
        public int Level { get; }
        public BodyBox(List<(double X, double Y)> corners, int level)
        {
            Corners = corners;
            Level = level;
        }
    }

    /// <summary>3D collision test: levels must match, then the 2D SAT.</summary>
    public static bool BoxesIntersect(BodyBox a, BodyBox b) =>
        a.Level == b.Level && Intersects2D(a.Corners, b.Corners);

    /// <summary>SAT overlap test for two convex quads. Touching edges count as
    /// contact (separation must be strict), so a car resting flush against an
    /// obstacle still registers as in contact.</summary>
    public static bool Intersects2D(List<(double X, double Y)> a,
                                    List<(double X, double Y)> b)
    {
        foreach (var poly in new[] { a, b })
        {
            int n = poly.Count;
            for (int i = 0; i < n; i++)
            {
                var (x1, y1) = poly[i];
                var (x2, y2) = poly[(i + 1) % n];
                double nx = -(y2 - y1), ny = x2 - x1;      // edge normal
                double length = Math.Hypot(nx, ny);
                if (length < 1e-12) continue;
                nx /= length; ny /= length;
                var (amin, amax) = ProjectOnto(a, nx, ny);
                var (bmin, bmax) = ProjectOnto(b, nx, ny);
                if (amax < bmin || bmax < amin)
                    return false;                    // separating axis found
            }
        }
        return true;
    }

    /// <summary>Point-in-convex-quad test (cross products all on one side).</summary>
    public static bool PointInBox(double px, double py,
                                  List<(double X, double Y)> corners)
    {
        int n = corners.Count;
        bool? first = null;
        for (int i = 0; i < n; i++)
        {
            var (x1, y1) = corners[i];
            var (x2, y2) = corners[(i + 1) % n];
            double cr = (x2 - x1) * (py - y1) - (y2 - y1) * (px - x1);
            if (Math.Abs(cr) < 1e-9)
                continue;                            // on an edge: inside
            bool sign = cr > 0;
            if (first is null) first = sign;
            else if (sign != first.Value) return false;
        }
        return true;
    }

    // --- Auto-alignment ------------------------------------------------------

    private static double PosMod(double a, double m) => ((a % m) + m) % m;

    /// <summary>(index, segment) of the road chord closest to world point
    /// (x, y), or (-1, null) if the network has no usable segments.</summary>
    private static (int Idx, RoadSegment? Seg) NearestSegment(double x, double y,
                                                              RoadNetwork network)
    {
        int bestIdx = -1;
        double bestDist = double.PositiveInfinity;
        for (int idx = 0; idx < network.Segments.Count; idx++)
        {
            var seg = network.Segments[idx];
            double dx = seg.X2 - seg.X1;
            double dy = seg.Y2 - seg.Y1;
            double lengthSq = dx * dx + dy * dy;
            if (lengthSq < 1e-9) continue;
            double t = ((x - seg.X1) * dx + (y - seg.Y1) * dy) / lengthSq;
            t = Math.Clamp(t, 0.0, 1.0);
            double px = seg.X1 + t * dx - x;
            double py = seg.Y1 + t * dy - y;
            double d = px * px + py * py;
            if (d < bestDist)
            {
                bestDist = d;
                bestIdx = idx;
            }
        }
        return bestIdx < 0 ? (-1, null) : (bestIdx, network.Segments[bestIdx]);
    }

    /// <summary>Fallback alignment: the nearest segment's start->end direction,
    /// flipped for the oncoming half of a two-way road. Only used when no
    /// smoothed-centerline index is available (see LaneHeadingAt).</summary>
    private static double LaneHeadingChord(double x, double y, RoadNetwork network)
    {
        var (idx, seg) = NearestSegment(x, y, network);
        if (idx < 0 || seg is null)
            throw new PlacementError("no road found at drop point");
        double dx = seg.X2 - seg.X1;
        double dy = seg.Y2 - seg.Y1;
        double headingFwd = PosMod(Math.Degrees(Math.Atan2(dx, dy)), 360.0);
        if (seg.Oneway) return headingFwd;
        double lengthSq = dx * dx + dy * dy;
        double t = ((x - seg.X1) * dx + (y - seg.Y1) * dy) / lengthSq;
        t = Math.Clamp(t, 0.0, 1.0);
        double px = x - (seg.X1 + t * dx);
        double py = y - (seg.Y1 + t * dy);
        double h = Math.Radians(headingFwd);
        double side = px * Math.Cos(h) + py * (-Math.Sin(h));     // > 0: right half
        return side >= 0 ? headingFwd : PosMod(headingFwd + 180.0, 360.0);
    }

    private sealed class AlignmentSamples
    {
        public List<(double X, double Y)> Pts = new();
        public List<(double Tx, double Ty)> Tans = new();
    }

    // Cached per network (mirrors Python's cache on the network object).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        RoadNetwork, AlignmentSamples?> AlignmentCache = new();

    /// <summary>Nearest-point index over the SMOOTHED centerlines (§10 geometry -
    /// the same splines and junction fillets that define the pavement), cached
    /// on the network. One sample per ~0.5 m of road plus the (4x-subdivided)
    /// junction fillet arcs — or null when nothing could be built.
    ///
    /// The local direction of travel at any paved point is the tangent here,
    /// which is what makes parked cars follow curves and rounded corners
    /// instead of snapping to one of the straight segment chords.</summary>
    private static AlignmentSamples? AlignmentIndex(RoadNetwork network) =>
        AlignmentCache.GetOrAdd(network, net =>
        {
            try
            {
                var sm = SmoothGeometry.For(net);
                var idx = new AlignmentSamples();
                foreach (var line in sm.Lines)
                {
                    var res = line.Resampled;          // ~every 0.5 m of arc length
                    int n = res.Count;
                    if (n < 2) continue;
                    var curve = line.Curve;
                    double total = curve.Total;
                    for (int i = 0; i < n; i++)
                    {
                        var (px, py) = res[i];
                        double s = total * i / (n - 1);   // same s resample_curve used
                        double h = Math.Radians(curve.HeadingAt(s));
                        idx.Pts.Add((px, py));
                        idx.Tans.Add((Math.Sin(h), Math.Cos(h)));
                    }
                }
                foreach (var fillet in sm.JunctionFillets)
                {
                    var arc = fillet.Arc;              // ordered along the turn
                    if (arc.Count < 3) continue;
                    var sub = new List<(double X, double Y)>();
                    for (int i = 0; i < arc.Count - 1; i++)
                    {
                        var (x0, y0) = arc[i];
                        var (x1, y1) = arc[i + 1];
                        for (int k = 0; k < 4; k++)
                        {
                            double f = k / 4.0;
                            sub.Add((x0 + (x1 - x0) * f, y0 + (y1 - y0) * f));
                        }
                    }
                    sub.Add(arc[^1]);
                    int m = sub.Count;
                    for (int i = 0; i < m; i++)
                    {
                        int j0 = Math.Max(0, i - 1), j1 = Math.Min(m - 1, i + 1);
                        double tx = sub[j1].X - sub[j0].X;
                        double ty = sub[j1].Y - sub[j0].Y;
                        double L = Math.Hypot(tx, ty);
                        if (L < 1e-9) continue;
                        idx.Pts.Add(sub[i]);
                        idx.Tans.Add((tx / L, ty / L));
                    }
                }
                return idx.Pts.Count >= 2 ? idx : null;
            }
            catch
            {
                return null;
            }
        });

    /// <summary>Direction of travel (degrees) of the lane under world point
    /// (x, y).
    ///
    /// The local direction is the tangent of the smoothed centerline at the
    /// nearest paved point (§10 geometry), so on curves and rounded corners
    /// the parked car follows the street. On a two-way road the side of the
    /// centerline decides which way it faces: dropped in the right half it
    /// faces along the tangent, in the left/oncoming half against it - like a
    /// car stopped in traffic in that lane. On a one-way every lane flows the
    /// legal direction, so the tangent is oriented to match the nearest
    /// segment's start->end flow regardless of side.</summary>
    public static double LaneHeadingAt(double x, double y, RoadNetwork network)
    {
        var (idxSeg, seg) = NearestSegment(x, y, network);
        if (idxSeg < 0 || seg is null)
            throw new PlacementError("no road found at drop point");

        var index = AlignmentIndex(network);
        if (index is not null)
        {
            // Nearest sample (linear scan — Python's numpy argmin).
            int iBest = 0;
            double d2Best = double.PositiveInfinity;
            for (int i = 0; i < index.Pts.Count; i++)
            {
                double ddx = index.Pts[i].X - x;
                double ddy = index.Pts[i].Y - y;
                double d2 = ddx * ddx + ddy * ddy;
                if (d2 < d2Best)
                {
                    d2Best = d2;
                    iBest = i;
                }
            }
            double qx = index.Pts[iBest].X, qy = index.Pts[iBest].Y;
            double tx = index.Tans[iBest].Tx, ty = index.Tans[iBest].Ty;
            double sgn;
            if (seg.Oneway)
            {
                double fx = seg.X2 - seg.X1;
                double fy = seg.Y2 - seg.Y1;
                double L = Math.Hypot(fx, fy);
                sgn = 1.0;
                if (L > 1e-9 && tx * (fx / L) + ty * (fy / L) < 0)
                    sgn = -1.0;                     // face the legal flow
            }
            else
            {
                double rx = ty, ry = -tx;           // right of forward (tx, ty)
                double side = (x - qx) * rx + (y - qy) * ry;
                sgn = side >= 0 ? 1.0 : -1.0;       // > 0: right half
            }
            double h = PosMod(Math.Degrees(Math.Atan2(sgn * tx, sgn * ty)), 360.0);
            return h >= 360.0 - 1e-9 ? 0.0 : h;
        }

        return LaneHeadingChord(x, y, network);
    }
}
