using System.Text.Json;
using DrivingGame.Sim;
using Xunit;

namespace DrivingGame.Sim.Tests;

/// <summary>
/// Unit tests for the Phase 4 safety systems ported from the Python original
/// (car/src/obstacles.py, lane_guard.py, physics_validator.py): obstacle
/// placement + auto-alignment, stop-on-contact, layout save/load, the lane
/// guard's wrong-side detection, and the physics validator's fatal /
/// non-fatal checks.
/// </summary>
public class SafetySystemsTests
{
    const double Dt = 1.0 / 60.0;

    // --- helpers -------------------------------------------------------------

    static RoadNetwork BasicNet() => TestMaps.BuildTestMap("basic");

    static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dg_safety_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Car spawned at "straight" (north end, heading south).</summary>
    static Car SpawnStraight(RoadNetwork net)
    {
        var car = new Car(0, 0, 180.0, 0, new KeyboardDriver());
        car.TeleportToNamedPoint(net, "straight");
        return car;
    }

    /// <summary>Move the car along its heading (internal setter access).</summary>
    static void Advance(Car car, double meters)
    {
        double rad = Math.Radians(car.Heading);
        car.X += Math.Sin(rad) * meters;
        car.Y += Math.Cos(rad) * meters;
    }

    /// <summary>Move the car to its right (right = heading rotated -90°).</summary>
    static void ShiftRight(Car car, double meters)
    {
        double rad = Math.Radians(car.Heading);
        car.X += Math.Cos(rad) * meters;
        car.Y += -Math.Sin(rad) * meters;
    }

    static (double X, double Y) StraightMidpoint(RoadNetwork net, Car atStraight)
    {
        var seg = net.Segments[atStraight.SegIdx];
        return ((seg.X1 + seg.X2) / 2.0, (seg.Y1 + seg.Y2) / 2.0);
    }

    /// <summary>Right-of-travel unit vector of the straight segment.</summary>
    static (double Rx, double Ry) StraightRight(RoadNetwork net, Car atStraight)
    {
        var seg = net.Segments[atStraight.SegIdx];
        double dx = seg.X2 - seg.X1, dy = seg.Y2 - seg.Y1;
        double L = Math.Hypot(dx, dy);
        return (dy / L, -dx / L);
    }

    static double AngleDiffDeg(double a, double b) =>
        Math.Abs((a - b + 180.0) % 360.0 - 180.0);

    static double PosMod(double a, double m) => ((a % m) + m) % m;

    /// <summary>SAT penetration depth of two convex quads (px): the boxes
    /// penetrate only if they overlap on EVERY axis, in which case the depth
    /// is the SMALLEST overlap across all axes. &lt;= 0 means separated or
    /// merely touching.</summary>
    static double PenetrationM(List<(double X, double Y)> a, List<(double X, double Y)> b)
    {
        double minOverlap = double.PositiveInfinity;
        foreach (var poly in new[] { a, b })
        {
            for (int i = 0; i < poly.Count; i++)
            {
                var (x1, y1) = poly[i];
                var (x2, y2) = poly[(i + 1) % poly.Count];
                double nx = -(y2 - y1), ny = x2 - x1;
                double L = Math.Hypot(nx, ny);
                if (L < 1e-12) continue;
                nx /= L; ny /= L;
                double amin = double.MaxValue, amax = double.MinValue;
                double bmin = double.MaxValue, bmax = double.MinValue;
                foreach (var (x, y) in a) { double d = x * nx + y * ny; amin = Math.Min(amin, d); amax = Math.Max(amax, d); }
                foreach (var (x, y) in b) { double d = x * nx + y * ny; bmin = Math.Min(bmin, d); bmax = Math.Max(bmax, d); }
                minOverlap = Math.Min(minOverlap, Math.Min(amax, bmax) - Math.Max(amin, bmin));
            }
        }
        return minOverlap;
    }

    // --- obstacle placement, auto-alignment and layout persistence ------------
    // Retired with the obstacle entity (#27). These asserted ObstacleManager's own
    // rules - a placed body auto-aligning to the flow of the half-road it stands in,
    // off-road positions refused, type/colour validated, and Save/Load/ListLayouts
    // round-tripping `data/obstacles/<map>/<name>.json`. None of it survives: nothing
    // in this repo places an obstacle (no UI, no scenario, no layout file), and a
    // parked CAR is placed by /teleport with an explicit heading, so there is no
    // auto-alignment rule to keep and no layout format to persist.

    // --- stop on contact ---------------------------------------------------
    // Retired with the obstacle entity (#27). `ObstacleManager.ApplyContactStop` was
    // the only caller of that code path; a body that does not move is a parked Car now,
    // and "brake to a true rest against it, without sinking in" is already asserted on
    // the car path by CollisionTests.StationaryLeadCar_FollowerBrakesAndStopsBehind and
    // on the model-length side by ParkedBlockerContactTests.



    // --- layout save / load ----------------------------------------------------

    // --- layout persistence (Save / Load / ListLayouts) ----------------------
    // Retired with the obstacle entity (#27): `data/obstacles/<map>/<name>.json` had
    // neither a REST endpoint nor a UI, so only these tests exercised it. A parked car
    // needs no layout file - it is placed by /teleport and lives in the world, on the
    // tape and in /state like any other car.

    // --- lane guard -------------------------------------------------------------

    [Fact]
    public void LaneGuard_CorrectLane_NoViolations()
    {
        var net = BasicNet();
        var car = SpawnStraight(net);
        ShiftRight(car, Config.LaneBaseOffsetM(net.Segments[car.SegIdx].Width)); // right-lane centre
        Advance(car, 10.0);

        var guard = new LaneGuard();
        var driver = new KeyboardDriver();
        var keys = new Dictionary<string, bool> { [Config.KEY_W] = true };
        for (int i = 0; i < 60 * 2; i++)   // 2 s of straight driving
        {
            var control = driver.GetControl(car, net, Dt, keys);
            car.Update(Dt, net, control);
            guard.Check(car, Dt, net);
        }

        var stats = guard.Stats(car);
        Assert.Equal(0, stats.WrongSideFrames);
        Assert.Equal(0.0, stats.WrongSideSeconds);
    }

    [Fact]
    public void LaneGuard_NearCenterline_FlaggedAndCounted()
    {
        var net = BasicNet();
        var car = SpawnStraight(net);
        Advance(car, 10.0);
        var seg = net.Segments[car.SegIdx];
        Assert.False(seg.Oneway, "test needs a two-way segment");

        // Same threshold the guard computes (width-dependent).
        double threshold = Math.Max(0.15, Math.Min(0.9, Config.KerbOffsetM(seg.Width) - 0.9));
        // Park the body centre well inside the flag band (but on the road).
        ShiftRight(car, (threshold - 0.02) / 4.0);

        var guard = new LaneGuard();
        Assert.True(guard.Check(car, Dt, net), "body centre inside the threshold band must be flagged");
        for (int i = 0; i < 4; i++)
            Assert.True(guard.Check(car, Dt, net));

        var stats = guard.Stats(car);
        Assert.Equal(5, stats.WrongSideFrames);
        Assert.Equal(Math.Round(5 * Dt, 2), stats.WrongSideSeconds);
    }

    // --- physics validator --------------------------------------------------------

    [Fact]
    public void Validator_Jump_Throws()
    {
        var net = BasicNet();
        var car = SpawnStraight(net);
        Advance(car, 30.0);   // mid-segment, fully on the pavement

        var v = new PhysicsValidator();
        v.Check(car, Dt, net);   // prime
        car.X += 50.0;   // teleport 50 m in one frame
        Assert.Throws<PhysicsViolationException>(() => v.Check(car, Dt, net));
    }

    [Fact]
    public void Validator_OffRoad_RecordedNotFatal()
    {
        var net = BasicNet();
        var car = SpawnStraight(net);
        Advance(car, 30.0);
        ShiftRight(car, -30.0);   // 30 m left: off the pavement

        var v = new PhysicsValidator();
        v.Check(car, Dt, net);   // prime at the off-road position
        v.Check(car, Dt, net);   // detect (no motion between frames -> no jump)

        var log = v.ViolationsFor(car);
        Assert.Single(log);
        Assert.Equal("off_road", log[0].Type);
        Assert.Equal(car.SegIdx, log[0].Segment);
    }

    [Fact]
    public void Validator_TurningRadius_SuspendedWhileInContact()
    {
        var net = BasicNet();
        var car = SpawnStraight(net);
        Advance(car, 30.0);

        var v = new PhysicsValidator();
        v.Check(car, Dt, net);   // prime
        car.Heading += 90.0;     // instant 90° rotation with no movement:
                                 // implied radius 0 < 3 m -> fatal ...
        Assert.Throws<PhysicsViolationException>(() => v.Check(car, Dt, net));

        // ... unless the motion is externally constrained by a contact stop.
        v.Check(car, Dt, net, inContact: true);   // must not throw
    }
}
