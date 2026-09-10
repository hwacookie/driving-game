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

/// <summary>An obstacle cannot be placed there / at all (off-road, bad
/// type/color).</summary>
public sealed class PlacementError : Exception
{
    public PlacementError(string message) : base(message) { }
}

/// <summary>A world object with position (x, y), heading, type and color.
///
/// (x, y) is the CENTER of the footprint in world pixels; heading in
/// degrees (0 = north), auto-aligned at placement from the road geometry.
/// The id is stable for the life of the obstacle - required for removal
/// via the REST API and unambiguous in saved layouts.</summary>
public sealed record Obstacle(int Id, double X, double Y, double Heading,
                              string Type = "car", string Color = "blue")
{
    public Dictionary<string, object> ToDict() => new()
    {
        ["id"] = Id, ["type"] = Type, ["color"] = Color,
        ["x"] = X, ["y"] = Y, ["heading"] = Heading,
    };

    /// <summary>Parse a saved entry. Throws on missing/malformed fields
    /// (Python: KeyError/TypeError/ValueError) — the loader skips those.</summary>
    public static Obstacle FromJson(JsonElement d)
    {
        int id = (int)d.GetProperty("id").GetDouble();
        double x = d.GetProperty("x").GetDouble();
        double y = d.GetProperty("y").GetDouble();
        double heading = d.GetProperty("heading").GetDouble();
        string type = d.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                      ? t.GetString()! : "car";
        string color = d.TryGetProperty("color", out var c) && c.ValueKind == JsonValueKind.String
                       ? c.GetString()! : "blue";
        return new Obstacle(id, x, y, heading, type, color);
    }
}

/// <summary>Geometry helpers (world pixels, north-up frame) + the palette
/// colors. The concrete RGB values are a rendering detail
/// (docs/OBSTACLES.md): they must stay clearly distinguishable from the
/// player's red #B41E1E and from each other.</summary>
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
        double pppm = Config.PIXELS_PER_METER;
        double h = Math.Radians(headingDeg);
        double fx = Math.Sin(h), fy = Math.Cos(h);      // forward
        double rx = Math.Cos(h), ry = -Math.Sin(h);     // right
        double hl = lengthM * pppm / 2.0;
        double hw = widthM * pppm / 2.0;
        return new List<(double X, double Y)>
        {
            (cx + fx * hl + rx * hw, cy + fy * hl + ry * hw),   // front-right
            (cx + fx * hl - rx * hw, cy + fy * hl - ry * hw),   // front-left
            (cx - fx * hl - rx * hw, cy - fy * hl - ry * hw),   // rear-left
            (cx - fx * hl + rx * hw, cy - fy * hl + ry * hw),   // rear-right
        };
    }

    /// <summary>The parked car's footprint: same size as the player car.</summary>
    public static List<(double X, double Y)> ObstacleFootprint(Obstacle ob) =>
        BoxCorners(ob.X, ob.Y, ob.Heading, Config.CAR_LENGTH, Config.CAR_WIDTH);

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

/// <summary>Owns the placed obstacles. Shared by the palette UI and the REST
/// API so both paths go through the SAME placement logic (identical
/// auto-alignment, identical off-road rejection). Thread-safe: the REST
/// server thread mutates while the game loop reads every physics step.</summary>
public sealed class ObstacleManager
{
    public string MapName { get; }

    /// <summary>Layouts live under &lt;base&gt;/obstacles/&lt;map_name&gt;/ (alongside
    /// the existing data/osm_cache/). baseDir is overridable for tests.</summary>
    public string BaseDir { get; }

    private readonly List<Obstacle> _obstacles = new();
    private int _nextId = 1;
    private readonly object _lock = new();

    public ObstacleManager(string mapName, string? baseDir = null)
    {
        MapName = mapName;
        BaseDir = baseDir ?? DefaultBaseDir();
    }

    // Python: <repo root>/data derived from the source file location. C#: walk
    // up from the app base dir to the repo root (project.godot marker).
    private static string DefaultBaseDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "project.godot")))
                return Path.Combine(dir.FullName, "data");
            dir = dir.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, "data");
    }

    // --- placement (identical for palette and REST) ---

    public double AlignHeading(double x, double y, RoadNetwork network) =>
        ObstacleGeometry.LaneHeadingAt(x, y, network);

    private static void ValidatePoint(RoadNetwork network, double x, double y)
    {
        if (!network.IsOnRoad(x, y))
            throw new PlacementError(
                $"({x / Config.PIXELS_PER_METER:F1} m, " +
                $"{y / Config.PIXELS_PER_METER:F1} m) is off the paved road area");
    }

    /// <summary>Place a new obstacle; returns it (id + computed heading).</summary>
    public Obstacle Place(RoadNetwork network, string type, string color,
                          double x, double y)
    {
        if (type != "car")
            throw new PlacementError(
                $"unknown obstacle type '{type}' (Part 1 offers 'car' only)");
        if (!ObstacleGeometry.ObstacleColors.ContainsKey(color))
            throw new PlacementError(
                $"unknown color '{color}' " +
                $"(available: {string.Join(", ", ObstacleGeometry.ObstacleColors.Keys.OrderBy(k => k))})");
        ValidatePoint(network, x, y);
        double heading = ObstacleGeometry.LaneHeadingAt(x, y, network);
        lock (_lock)
        {
            var ob = new Obstacle(_nextId++, x, y, heading, type, color);
            _obstacles.Add(ob);
            return ob;
        }
    }

    /// <summary>Re-place an existing obstacle (re-aligned at the new point).</summary>
    public Obstacle Move(RoadNetwork network, int obId, double x, double y)
    {
        if (Get(obId) is null)
            throw new KeyNotFoundException($"no obstacle with id {obId}");
        ValidatePoint(network, x, y);
        double heading = ObstacleGeometry.LaneHeadingAt(x, y, network);
        lock (_lock)
        {
            for (int i = 0; i < _obstacles.Count; i++)
            {
                if (_obstacles[i].Id == obId)
                {
                    var newOb = _obstacles[i] with { X = x, Y = y, Heading = heading };
                    _obstacles[i] = newOb;
                    return newOb;
                }
            }
            throw new KeyNotFoundException($"no obstacle with id {obId}");
        }
    }

    public bool Remove(int obId)
    {
        lock (_lock)
        {
            for (int i = 0; i < _obstacles.Count; i++)
            {
                if (_obstacles[i].Id == obId)
                {
                    _obstacles.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }
    }

    public Obstacle? Get(int obId)
    {
        lock (_lock)
        {
            foreach (var o in _obstacles)
                if (o.Id == obId) return o;
            return null;
        }
    }

    /// <summary>A consistent copy of the current set (thread-safe).</summary>
    public List<Obstacle> Snapshot()
    {
        lock (_lock) return new List<Obstacle>(_obstacles);
    }

    public List<Dictionary<string, object>> SnapshotDicts()
    {
        lock (_lock) return _obstacles.Select(o => o.ToDict()).ToList();
    }

    // --- Stop on contact (per-frame, all modes) ------------------------------

    /// <summary>The first obstacle whose footprint touches the player's body box.</summary>
    public Obstacle? ContactWithCar(Car car, RoadNetwork? net = null)
    {
        var corners = ObstacleGeometry.PlayerBodyCorners(car);
        int lvl = net is null ? 0 : net.Segments[car.SegIdx].Level;
        foreach (var ob in Snapshot())
            if (ObstacleGeometry.BoxesIntersect(
                    new ObstacleGeometry.BodyBox(corners, lvl),
                    new ObstacleGeometry.BodyBox(ObstacleGeometry.ObstacleFootprint(ob), 0)))
                return ob;
        return null;
    }

    /// <summary>Stop-on-contact response (docs/OBSTACLES.md). Call after
    /// car.Update() with the position AND heading from BEFORE that step.
    ///
    /// While the body box touches an obstacle footprint:
    ///   1. the car brakes with full braking deceleration (CAR_BRAKING) until
    ///      stopped - no instant velocity zeroing from speed;
    ///   2. forward motion is clamped so the two boxes never interpenetrate
    ///      - the car rests against the obstacle, like against a wall.
    /// Returns True while in contact (so the validator can treat the motion
    /// as externally constrained).</summary>
    public bool ApplyContactStop(Car car, double dt, double preX, double preY,
                                 double? preHeading = null,
                                 RoadNetwork? net = null)
    {
        var obs = Snapshot();
        if (obs.Count == 0) return false;

        double pppm = Config.PIXELS_PER_METER;
        double h = car.Heading;
        double rad = Math.Radians(h);
        double offX = Math.Sin(rad) * Config.REAR_AXLE_OFFSET_M * pppm;
        double offY = Math.Cos(rad) * Config.REAR_AXLE_OFFSET_M * pppm;

        List<(double X, double Y)> BodyAt(double x, double y) =>
            ObstacleGeometry.BoxCorners(x + offX, y + offY, h,
                                        Config.CAR_LENGTH, Config.CAR_WIDTH);

        // 3D contact: the car's box sits at its segment's level, obstacles
        // are ground objects (level 0) - a deck car passes over them.
        int lvl = net is null ? 0 : net.Segments[car.SegIdx].Level;
        var cur = BodyAt(car.X, car.Y);
        if (obs.All(o => !ObstacleGeometry.BoxesIntersect(
                new ObstacleGeometry.BodyBox(cur, lvl),
                new ObstacleGeometry.BodyBox(ObstacleGeometry.ObstacleFootprint(o), 0))))
            return false;

        // 1) Brake to a stop - never an instant zeroing from speed. A tiny
        //    deadband zeroes the last floating-point crumbs (a residual of
        //    ~1e-15 m/s would otherwise persist forever against the
        //    sub-micron rest gap) so the car is truly at rest.
        if (car.Speed > 0.0)
        {
            car.Speed = Math.Max(0.0, car.Speed - Config.CAR_BRAKING * dt);
            if (car.Speed < 1e-6) car.Speed = 0.0;
        }
        else if (car.Speed < 0.0)
        {
            car.Speed = Math.Min(0.0, car.Speed + Config.CAR_BRAKING * dt);
            if (car.Speed > -1e-6) car.Speed = 0.0;
        }

        // 2) Clamp the motion of this step so the boxes never interpenetrate.
        var preBox = BodyAt(preX, preY);
        if (obs.All(o => !ObstacleGeometry.BoxesIntersect(
                new ObstacleGeometry.BodyBox(preBox, lvl),
                new ObstacleGeometry.BodyBox(ObstacleGeometry.ObstacleFootprint(o), 0))))
        {
            // The pre-step position was clear: find the furthest point along
            // prev -> new that is still clear (the car rests against the
            // obstacle). A step (<= ~0.85 m at top speed) cannot skip over a
            // 4.4 m-wide obstacle, so overlap along the path is contiguous
            // and bisection converges to the contact point.
            double lo = 0.0, hi = 1.0;
            for (int i = 0; i < 24; i++)
            {
                double mid = (lo + hi) / 2.0;
                double mx = preX + (car.X - preX) * mid;
                double my = preY + (car.Y - preY) * mid;
                if (obs.Any(o => ObstacleGeometry.BoxesIntersect(
                        new ObstacleGeometry.BodyBox(BodyAt(mx, my), lvl),
                        new ObstacleGeometry.BodyBox(ObstacleGeometry.ObstacleFootprint(o), 0))))
                    hi = mid;
                else
                    lo = mid;
            }
            car.X = preX + (car.X - preX) * lo;
            car.Y = preY + (car.Y - preY) * lo;
        }
        else
        {
            // The pre-step position already touches/overlaps. This is the
            // NORMAL resting state, not an error: after the bisection above
            // pins the car to the contact point, the pin is only clear by a
            // floating-point epsilon, and the next substep's heading
            // micro-adjustment (the driver always steers in tiny increments)
            // rotates the body box by ~REAR_AXLE_OFFSET*dh - enough to re-
            // register the pinned position as touching. Moving further into
            // the obstacle is impossible either way, so hold the pre-step
            // position while the braking above decays the speed: the car
            // rests against the obstacle like against a wall. (It also covers
            // an obstacle placed on top of the car - there the rollback is a
            // no-op and the car simply brakes in place.)
            car.X = preX;
            car.Y = preY;
            // Holding position is not enough while the heading still moves:
            // the body box pivots around the axle offset, and its corners
            // would grind into the obstacle (interpenetration). If the held
            // position at the NEW heading intersects, hold the heading too -
            // the car is pinned. Steering that keeps the boxes clear is left
            // alone, so a driver can still steer back away from the contact.
            if (preHeading is not null &&
                obs.Any(o => ObstacleGeometry.BoxesIntersect(
                        new ObstacleGeometry.BodyBox(BodyAt(preX, preY), lvl),
                        new ObstacleGeometry.BodyBox(ObstacleGeometry.ObstacleFootprint(o), 0))))
                car.Heading = preHeading.Value;
        }
        return true;
    }

    // --- Save / load (obstacle layouts) --------------------------------------

    private sealed record SavedObstacle(int Id, string Type, string Color,
                                        double X, double Y, double Heading);

    private sealed record SavedLayout(
        string Map,
        string Name,
        [property: JsonPropertyName("saved_at")] string SavedAt,
        List<SavedObstacle> Obstacles);

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public string LayoutDir() =>
        Path.Combine(BaseDir, "obstacles", SanitizeName(MapName));

    /// <summary>Names of the saved layouts of THIS map (a Kleinmachnow layout is
    /// not loadable onto the synthetic basic map - the directory is per-map).</summary>
    public List<string> ListLayouts()
    {
        string d = LayoutDir();
        if (!Directory.Exists(d)) return new List<string>();
        return Directory.GetFiles(d, "*.json")
                        .Select(f => Path.GetFileNameWithoutExtension(f)!)
                        .OrderBy(n => n, StringComparer.Ordinal)
                        .ToList();
    }

    /// <summary>Store the current set under `name` (overwrites same-name).</summary>
    public string Save(string name)
    {
        name = SanitizeName(name);
        if (name.Length == 0)
            throw new PlacementError("layout name must not be empty");
        string d = LayoutDir();
        Directory.CreateDirectory(d);
        string path = Path.Combine(d, $"{name}.json");
        var payload = new SavedLayout(
            MapName,
            name,
            DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:sszzz"),
            Snapshot().Select(o => new SavedObstacle(o.Id, o.Type, o.Color, o.X, o.Y, o.Heading)).ToList());
        File.WriteAllText(path, JsonSerializer.Serialize(payload, _jsonOpts));
        return path;
    }

    /// <summary>Replace the current obstacles with a saved layout. Each entry is
    /// validated against the paved polygon: an obstacle that no longer lies on
    /// the road (e.g. the map data changed) is skipped with a warning instead
    /// of being placed off-road. Returns (loaded, skipped).</summary>
    public (int Loaded, int Skipped) Load(string name, RoadNetwork network)
    {
        string path = Path.Combine(LayoutDir(), $"{SanitizeName(name)}.json");
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"no layout named '{name}' for map '{MapName}'", path);

        JsonElement payload;
        using (var fs = File.OpenRead(path))
            payload = JsonSerializer.Deserialize<JsonElement>(fs);
        if (payload.ValueKind == JsonValueKind.Undefined)
            throw new PlacementError($"layout '{name}' is empty");

        string? mapInFile = payload.TryGetProperty("map", out var m) ? m.GetString() : null;
        if (mapInFile != MapName)
            throw new PlacementError(
                $"layout '{name}' belongs to map '{mapInFile ?? "None"}', not '{MapName}'");

        var newObs = new List<Obstacle>();
        int loaded = 0, skipped = 0;
        int maxId = 0;
        if (payload.TryGetProperty("obstacles", out var obsEl) &&
            obsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in obsEl.EnumerateArray())
            {
                Obstacle ob;
                try
                {
                    ob = Obstacle.FromJson(entry);
                }
                catch (Exception)   // Python: KeyError, TypeError, ValueError
                {
                    Console.WriteLine(
                        $"⚠️  Layout '{name}': skipping malformed entry {entry.GetRawText()}");
                    skipped++;
                    continue;
                }
                if (ob.Type != "car" || !ObstacleGeometry.ObstacleColors.ContainsKey(ob.Color))
                {
                    Console.WriteLine(
                        $"⚠️  Layout '{name}': skipping unknown obstacle " +
                        $"{JsonSerializer.Serialize(ob.ToDict())}");
                    skipped++;
                    continue;
                }
                if (!network.IsOnRoad(ob.X, ob.Y))
                {
                    Console.WriteLine(
                        $"⚠️  Layout '{name}': obstacle {ob.Id} no longer lies on " +
                        "the paved road - skipping it");
                    skipped++;
                    continue;
                }
                newObs.Add(ob);
                maxId = Math.Max(maxId, ob.Id);
                loaded++;
            }
        }
        lock (_lock)
        {
            _obstacles.Clear();
            _obstacles.AddRange(newObs);
            _nextId = maxId + 1;
        }
        return (loaded, skipped);
    }

    /// <summary>Filesystem-safe layout/map name.</summary>
    public static string SanitizeName(string name)
    {
        name = Regex.Replace(name, @"[^A-Za-z0-9 _\-.]", "_").Trim();
        return name.Length > 80 ? name[..80] : name;
    }
}
