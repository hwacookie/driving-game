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
        car.X += Math.Sin(rad) * meters * Config.PIXELS_PER_METER;
        car.Y += Math.Cos(rad) * meters * Config.PIXELS_PER_METER;
    }

    /// <summary>Move the car to its right (right = heading rotated -90°).</summary>
    static void ShiftRight(Car car, double meters)
    {
        double rad = Math.Radians(car.Heading);
        car.X += Math.Cos(rad) * meters * Config.PIXELS_PER_METER;
        car.Y += -Math.Sin(rad) * meters * Config.PIXELS_PER_METER;
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
    static double PenetrationPx(List<(double X, double Y)> a, List<(double X, double Y)> b)
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

    // --- obstacle placement + auto-alignment ---------------------------------

    [Fact]
    public void Place_TwoWayRightHalf_FacesAlongFlow()
    {
        var net = BasicNet();
        var car = SpawnStraight(net);
        Assert.False(net.Segments[car.SegIdx].Oneway, "test needs a two-way segment");
        var mgr = new ObstacleManager("basic", TempDir());

        var (mx, my) = StraightMidpoint(net, car);
        var (rx, ry) = StraightRight(net, car);
        double pppm = Config.PIXELS_PER_METER;
        var ob = mgr.Place(net, "car", "blue", mx + rx * 1.5 * pppm, my + ry * 1.5 * pppm);

        var seg = net.Segments[car.SegIdx];
        double expected = PosMod(Math.Degrees(Math.Atan2(seg.X2 - seg.X1, seg.Y2 - seg.Y1)), 360.0);
        Assert.True(AngleDiffDeg(ob.Heading, expected) < 5.0,
            $"right-half obstacle faces {ob.Heading:F1}°, expected ~{expected:F1}° (along flow)");
    }

    [Fact]
    public void Place_TwoWayLeftHalf_FacesAgainstFlow()
    {
        var net = BasicNet();
        var car = SpawnStraight(net);
        var mgr = new ObstacleManager("basic", TempDir());

        var (mx, my) = StraightMidpoint(net, car);
        var (rx, ry) = StraightRight(net, car);
        double pppm = Config.PIXELS_PER_METER;
        var ob = mgr.Place(net, "car", "blue", mx - rx * 1.5 * pppm, my - ry * 1.5 * pppm);

        var seg = net.Segments[car.SegIdx];
        double alongFlow = PosMod(Math.Degrees(Math.Atan2(seg.X2 - seg.X1, seg.Y2 - seg.Y1)), 360.0);
        Assert.True(AngleDiffDeg(ob.Heading, PosMod(alongFlow + 180.0, 360.0)) < 5.0,
            $"left-half obstacle faces {ob.Heading:F1}°, expected ~{PosMod(alongFlow + 180.0, 360.0):F1}° (against flow)");
    }

    [Fact]
    public void Place_OffRoad_ThrowsPlacementError()
    {
        var net = BasicNet();
        var mgr = new ObstacleManager("basic", TempDir());
        Assert.Throws<PlacementError>(() => mgr.Place(net, "car", "blue", -5000, -5000));
    }

    [Fact]
    public void Place_UnknownTypeOrColor_Throws()
    {
        var net = BasicNet();
        var car = SpawnStraight(net);
        var mgr = new ObstacleManager("basic", TempDir());
        var (mx, my) = StraightMidpoint(net, car);
        Assert.Throws<PlacementError>(() => mgr.Place(net, "truck", "blue", mx, my));
        Assert.Throws<PlacementError>(() => mgr.Place(net, "car", "red", mx, my));
    }

    // --- stop on contact ------------------------------------------------------

    [Fact]
    public void ContactStop_CarBrakesAndRestsAgainstObstacle()
    {
        var net = BasicNet();
        var car = SpawnStraight(net);
        ShiftRight(car, Config.LaneBaseOffsetM(net.Segments[car.SegIdx].Width)); // normal driving position
        Advance(car, 10.0);                                                      // clear of the spawn node

        var mgr = new ObstacleManager("basic", TempDir());
        var (bx, by) = car.BodyCenter();
        double rad = Math.Radians(car.Heading);
        double pppm = Config.PIXELS_PER_METER;
        // Parked car 12 m ahead in the same lane.
        var ob = mgr.Place(net, "car", "blue",
            bx + Math.Sin(rad) * 12.0 * pppm, by + Math.Cos(rad) * 12.0 * pppm);

        var driver = new KeyboardDriver();
        var keys = new Dictionary<string, bool> { [Config.KEY_W] = true };
        bool everContact = false;
        for (int i = 0; i < 60 * 15; i++)   // up to 15 s at 60 Hz
        {
            double preX = car.X, preY = car.Y, preH = car.Heading;
            var control = driver.GetControl(car, net, Dt, keys);
            car.Update(Dt, net, control);
            everContact |= mgr.ApplyContactStop(car, Dt, preX, preY, preH);
        }

        Assert.True(everContact, "the car never registered contact with the obstacle");
        Assert.Equal(0.0, car.Speed);   // braked to a true rest (deadband)

        // No interpenetration: body box may touch, but not sink in.
        double pen = PenetrationPx(ObstacleGeometry.PlayerBodyCorners(car),
                                   ObstacleGeometry.ObstacleFootprint(ob));
        Assert.True(pen < 1.0 / pppm, $"body penetrates obstacle by {pen / pppm:F3} m");

        // The car stopped in front of (or at) the obstacle, not through it.
        // The obstacle is auto-aligned to the same flow direction as the car,
        // so both faces project onto the travel axis:
        var (cbx, cby) = car.BodyCenter();
        double alongX = Math.Sin(rad), alongY = Math.Cos(rad);
        double carFront = cbx * alongX + cby * alongY + Config.CAR_LENGTH * pppm / 2.0;
        double obRear = ob.X * alongX + ob.Y * alongY - Config.CAR_LENGTH * pppm / 2.0;
        Assert.True(carFront <= obRear + 0.05,
            $"car front passed the obstacle's rear face by {((carFront - obRear) / pppm):F3} m");
    }

    // --- layout save / load ----------------------------------------------------

    [Fact]
    public void SaveLoad_RoundTrip()
    {
        var dir = TempDir();
        try
        {
            var net = BasicNet();
            var car = SpawnStraight(net);
            var mgr = new ObstacleManager("basic", dir);
            var (mx, my) = StraightMidpoint(net, car);
            var (rx, ry) = StraightRight(net, car);
            double pppm = Config.PIXELS_PER_METER;
            for (double off = -1.5; off <= 1.5; off += 1.5)
                mgr.Place(net, "car", "blue", mx + rx * off * pppm, my + ry * off * pppm);

            string path = mgr.Save("a/b:c");   // sanitizes to a_b_c
            Assert.EndsWith(Path.Combine("obstacles", "basic", "a_b_c.json"), path);
            Assert.Contains("a_b_c", mgr.ListLayouts());

            var ids = mgr.Snapshot().Select(o => o.Id).OrderBy(i => i).ToArray();
            Assert.Equal(3, ids.Length);
            foreach (var id in ids) Assert.True(mgr.Remove(id));
            Assert.Empty(mgr.Snapshot());

            var (loaded, skipped) = mgr.Load("a/b:c", net);
            Assert.Equal(3, loaded);
            Assert.Equal(0, skipped);
            Assert.Equal(ids, mgr.Snapshot().Select(o => o.Id).OrderBy(i => i).ToArray());

            // Ids continue after the reloaded max.
            var next = mgr.Place(net, "car", "blue", mx, my);
            Assert.Equal(ids.Max() + 1, next.Id);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Load_SkipsMalformedOffRoadAndUnknownEntries()
    {
        var dir = TempDir();
        try
        {
            var net = BasicNet();
            var car = SpawnStraight(net);
            var mgr = new ObstacleManager("basic", dir);
            var (mx, my) = StraightMidpoint(net, car);

            string layoutDir = Path.Combine(dir, "obstacles", "basic");
            Directory.CreateDirectory(layoutDir);
            string json = $$"""
                {
                  "map": "basic",
                  "name": "handmade",
                  "saved_at": "2026-01-01T00:00:00+00:00",
                  "obstacles": [
                    {"id": 7, "type": "car", "color": "blue",   "x": {{mx}}, "y": {{my}}, "heading": 180.0},
                    {"id": 8, "type": "car", "color": "blue",   "y": {{my}}, "heading": 180.0},
                    {"id": 9, "type": "car", "color": "blue",   "x": -5000, "y": -5000, "heading": 0.0},
                    {"id": 10, "type": "car", "color": "purple","x": {{mx}}, "y": {{my}}, "heading": 90.0}
                  ]
                }
                """;
            File.WriteAllText(Path.Combine(layoutDir, "handmade.json"), json);

            var (loaded, skipped) = mgr.Load("handmade", net);
            Assert.Equal(1, loaded);
            Assert.Equal(3, skipped);
            Assert.NotNull(mgr.Get(7));
            Assert.Null(mgr.Get(8));
            Assert.Null(mgr.Get(9));
            Assert.Null(mgr.Get(10));

            // A layout whose 'map' field doesn't match is rejected outright.
            File.WriteAllText(Path.Combine(layoutDir, "foreign.json"),
                json.Replace("\"map\": \"basic\"", "\"map\": \"osm\""));
            Assert.Throws<PlacementError>(() => mgr.Load("foreign", net));
        }
        finally { Directory.Delete(dir, true); }
    }

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
        car.X += 50 * Config.PIXELS_PER_METER;   // teleport 50 m in one frame
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
