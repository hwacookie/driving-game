// Lane Guard — detects wrong-side (oncoming lane) driving.
// 1:1 port of car/src/lane_guard.py.
// Softer than PhysicsValidator: warns and tracks stats, never crashes.

using NetTopologySuite.Geometries;

namespace DrivingGame.Sim;

/// <summary>Checks whether a car crosses into the opposing lane.
///
/// On every two-way road segment the guard measures the distance from the
/// car's center to the segment's centerline. If that distance drops below
/// half the car's width, part of the car is on the wrong side of the
/// centerline.
///
/// Unlike PhysicsValidator this NEVER raises — it only logs warnings and
/// accumulates statistics for test reports.</summary>
public sealed class LaneGuard
{
    private const double Epsilon = 0.02;   // no false positives at the exact boundary

    public bool Enabled { get; private set; } = true;

    // Per-car state: uid -> last lateral offset (m)
    private readonly Dictionary<int, double> _lastOffset = new();

    // Statistics - PER CAR (uid -> value): each new car starts at zero, which
    // is what parallel test runs (one car per test in the same world) need.
    // Stale entries for destroyed cars are harmless.
    private readonly Dictionary<int, int> _violationsByCar = new();       // frames on wrong side
    private readonly Dictionary<int, double> _violationTimeByCar = new(); // seconds on wrong side

    // Cached centreline geometry (the same merged, corner-rounded lines the
    // dashed markings are drawn from, so the guard tests exactly the line the
    // player can see). One-way roads are absent by construction - they have
    // no oncoming lane. Keyed by network instance (mirrors Python's cache on
    // the network object).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<RoadNetwork, MultiLineString?> CentrelineCache
        = new();

    public LaneGuard(bool enabled = true) => Enabled = enabled;

    public void Enable() { Enabled = true; Console.WriteLine("✅ Lane guard ENABLED"); }
    public void Disable() { Enabled = false; Console.WriteLine("❌ Lane guard DISABLED"); }

    /// <summary>Return True if the car is currently on the wrong side.</summary>
    public bool Check(Car car, double dt, RoadNetwork network)
    {
        if (!Enabled) return false;

        var seg = network.Segments[car.SegIdx];

        // One-way streets have no opposing lane — skip silently.
        if (seg.Oneway) return false;

        double offsetM = LateralOffset(car, seg, network);

        // Wrong-side threshold, relative to the road width:
        //   7 m and wider: 0.9 m (half car width) - the left edge crossing
        //       the centreline is already wrong-side driving.
        //   narrow two-way roads (3.5-4 m service lanes): the lane is
        //       NARROWER than the car, so correct driving sits at only
        //       ~0.5-0.75 m from the centreline and the fixed 0.9 m test
        //       fired constantly (crashing BICYCLE runs on OSM maps).
        //       There, wrong-side means the car's CENTRE has crossed into
        //       the opposing half: threshold 0.15 m (noise margin).
        double threshold = Math.Max(0.15, Math.Min(0.9,
                                                   Config.KerbOffsetM(seg.Width) - 0.9));

        bool wasWrong = _lastOffset.TryGetValue(car.Uid, out var last) &&
                        last < (threshold - Epsilon);
        bool isWrong = offsetM < (threshold - Epsilon);

        if (isWrong)
        {
            _violationsByCar[car.Uid] = _violationsByCar.GetValueOrDefault(car.Uid) + 1;
            _violationTimeByCar[car.Uid] = _violationTimeByCar.GetValueOrDefault(car.Uid) + dt;
            // Only print once per crossing event (not every frame).
            if (!wasWrong)
                Console.WriteLine(
                    $"\n⚠️  WRONG-SIDE DRIVING! distance to centerline " +
                    $"{offsetM:F2} m (threshold = {threshold:F2} m, " +
                    $"road width {seg.Width} m) on segment {car.SegIdx}\n");
        }

        _lastOffset[car.Uid] = offsetM;
        return isWrong;
    }

    /// <summary>Lateral distance (metres) from the car center to the
    /// segment's centerline. Always positive — it's just how far off-center
    /// the car is.</summary>
    private static double LateralOffset(Car car, RoadSegment seg, RoadNetwork network)
    {
        double dx = seg.X2 - seg.X1;
        double dy = seg.Y2 - seg.Y1;
        double lengthSq = dx * dx + dy * dy;
        if (lengthSq == 0) return 0.0;

        // Measure from the BODY centre, not car.X/Y - those are the rear
        // axle (the bicycle model's pivot), which in a bend sits on a
        // different lateral offset than the body it is supposed to stand
        // for.
        var (cx, cy) = car.BodyCenter();

        // Distance to the REAL centreline, which curves through bends -
        // not to the straight chord between the segment's endpoints. At a
        // rounded bend the two diverge by more than the margin being
        // measured, so the chord version reported violations for a car
        // sitting correctly in its lane (and would miss real ones).
        var geom = CentrelineGeom(network);
        if (geom is not null)
            return geom.Distance(new Point(cx, cy)) / Config.PIXELS_PER_METER;

        double t = Math.Clamp(((cx - seg.X1) * dx + (cy - seg.Y1) * dy) / lengthSq, 0.0, 1.0);
        double projX = seg.X1 + t * dx;
        double projY = seg.Y1 + t * dy;
        return Math.Hypot(cx - projX, cy - projY) / Config.PIXELS_PER_METER;
    }

    private static MultiLineString? CentrelineGeom(RoadNetwork network) =>
        CentrelineCache.GetOrAdd(network, net =>
        {
            var lines = net.GetCenterlines()
                           .Where(c => c.Count >= 2)
                           .Select(c => new LineString(c.Select(p => new Coordinate(p.X, p.Y)).ToArray()))
                           .ToArray();
            return lines.Length > 0 ? new MultiLineString(lines) : null;
        });

    /// <summary>Clear stored state after a teleport.</summary>
    public void Reset(Car car) => _lastOffset.Remove(car.Uid);

    /// <summary>A summary suitable for test reports. With a car: that car's
    /// counters (zero for a new/unknown car). Without: the whole-lifetime
    /// total over all cars (legacy behaviour).</summary>
    public LaneGuardStats Stats(Car? car = null)
    {
        int frames; double secs;
        if (car is null)
        {
            frames = _violationsByCar.Values.Sum();
            secs = _violationTimeByCar.Values.Sum();
        }
        else
        {
            frames = _violationsByCar.GetValueOrDefault(car.Uid);
            secs = _violationTimeByCar.GetValueOrDefault(car.Uid);
        }
        return new LaneGuardStats(frames, Math.Round(secs, 2));
    }

    /// <summary>Wrong-side counters (per car or lifetime total).</summary>
    public sealed record LaneGuardStats(int WrongSideFrames, double WrongSideSeconds);
}
