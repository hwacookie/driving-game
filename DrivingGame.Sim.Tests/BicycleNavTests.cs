using DrivingGame.Sim;
using Xunit;

namespace DrivingGame.Sim.Tests;

/// <summary>
/// Behavioural integration tests for the bicycle-model navigation controller
/// (BicycleNav, parts 1-3): route following, corner speed limiting, destination
/// parking (forward and reverse-in) and U-turns. Drives a real Car + BicycleDriver
/// on the synthetic "basic" test map at 60 Hz and asserts the car stays on the
/// road and reaches its goal.
/// </summary>
public class BicycleNavTests
{
    const double Dt = 1.0 / 60.0;

    static (Car Car, BicycleDriver Driver, RoadNetwork Net) Spawn(string start)
    {
        var net = TestMaps.BuildTestMap("basic");
        var driver = new BicycleDriver();
        var car = new Car(0, 0, 180.0, 0, driver);
        car.TeleportToNamedPoint(net, start);
        return (car, driver, net);
    }

    static Dictionary<string, bool> Keys(params string[] pressed) =>
        pressed.ToDictionary(k => k, _ => true);

    /// <summary>Lateral offset of the car from its current segment's centreline (m).</summary>
    static double LatOffsetM(Car car, RoadNetwork net)
    {
        var seg = net.Segments[car.SegIdx];
        double dx = seg.X2 - seg.X1, dy = seg.Y2 - seg.Y1;
        double l2 = dx * dx + dy * dy;
        if (l2 < 1e-9) return double.PositiveInfinity;
        double t = Math.Clamp(((car.X - seg.X1) * dx + (car.Y - seg.Y1) * dy) / l2, 0.0, 1.0);
        return Math.Hypot(car.X - (seg.X1 + t * dx), car.Y - (seg.Y1 + t * dy)) / Config.PIXELS_PER_METER;
    }

    record DriveResult(double Time, double MaxLat, double MaxSpeed);

    /// <summary>Drive up to maxS seconds (or until done(car)); track the worst
    /// lateral offset and peak speed along the way.</summary>
    static DriveResult Drive(Car car, RoadNetwork net, BicycleDriver driver,
                             IDictionary<string, bool> keys, double maxS,
                             Func<Car, bool>? done = null)
    {
        double t = 0.0, maxLat = 0.0, maxSpeed = 0.0;
        while (t < maxS)
        {
            var control = driver.GetControl(car, net, Dt, keys);
            car.Update(Dt, net, control);
            t += Dt;
            maxLat = Math.Max(maxLat, LatOffsetM(car, net));
            maxSpeed = Math.Max(maxSpeed, Math.Abs(car.Speed));
            if (done is not null && done(car)) break;
        }
        return new DriveResult(t, maxLat, maxSpeed);
    }

    // ------------------------------------------------------------------
    // route following
    // ------------------------------------------------------------------

    [Fact]
    public void StraightDrive_KeepsRoadAndBuildsSpeed()
    {
        var (car, driver, net) = Spawn("straight");
        var r = Drive(car, net, driver, Keys("w"), 12.0);
        var nav = car.BicycleNav!;

        Assert.True(nav.S > 100.0, $"only reached s={nav.S:F1} m after {r.Time:F1} s");
        Assert.True(r.MaxSpeed > 15.0, $"peak speed only {r.MaxSpeed * 3.6:F0} km/h");
        var seg = net.Segments[car.SegIdx];
        Assert.True(r.MaxLat < seg.Width / 2.0 + 0.5,
                    $"left the road: max lateral offset {r.MaxLat:F2} m on a {seg.Width} m road");
    }

    [Fact]
    public void RightTurn_FollowsCornerAndLimitsSpeed()
    {
        var (car, driver, net) = Spawn("corner_right_entry");
        // cornerR_c: tile (1,0), (500+350, 100) m -> px (1700, 200)
        const double cx = 1700.0, cy = 200.0;

        double t = 0.0, maxLat = 0.0, peak = 0.0, cornerMax = 0.0;
        while (t < 45.0)
        {
            var control = driver.GetControl(car, net, Dt, Keys("w"));
            car.Update(Dt, net, control);
            t += Dt;
            maxLat = Math.Max(maxLat, LatOffsetM(car, net));
            peak = Math.Max(peak, Math.Abs(car.Speed));
            if (Math.Hypot(car.X - cx, car.Y - cy) < 20.0 * Config.PIXELS_PER_METER)
                cornerMax = Math.Max(cornerMax, Math.Abs(car.Speed));
            // Stop once the car is well onto the west leg.
            if (car.X < cx - 50.0 * Config.PIXELS_PER_METER && Math.Abs(car.Y - cy) < 4.0 * Config.PIXELS_PER_METER)
                break;
        }

        // Completed the turn: ended up on the west leg, well west of the corner.
        Assert.True(car.X < cx - 50.0 * Config.PIXELS_PER_METER,
                    $"car did not reach the west leg (x={car.X / 2:F0} m, corner x={cx / 2:F0} m)");
        Assert.True(Math.Abs(car.Y - cy) < 4.0 * Config.PIXELS_PER_METER, "not on the west leg's line");
        // Never left the pavement through the turn.
        var seg = net.Segments[car.SegIdx];
        Assert.True(maxLat < seg.Width / 2.0 + 0.6,
                    $"left the road in the corner: max lateral offset {maxLat:F2} m");
        // The profile must have let it cruise up AND braked for the corner.
        Assert.True(peak > 15.0, $"never cruised (peak {peak * 3.6:F0} km/h)");
        Assert.True(cornerMax < Math.Max(8.0, 0.6 * peak),
                    $"corner speed {cornerMax * 3.6:F0} km/h not limited (peak {peak * 3.6:F0} km/h)");
    }

    // ------------------------------------------------------------------
    // destination parking
    // ------------------------------------------------------------------

    [Fact]
    public void DeadEndApproach_ParksAtTheDeadEnd()
    {
        var (car, driver, net) = Spawn("dead_end_approach");
        bool parked = false;
        Drive(car, net, driver, Keys("w"), 90.0,
              done: c => { parked = c.BicycleNav!.Parked; return parked; });
        var nav = car.BicycleNav!;

        Assert.True(parked,
            $"not parked after 90 s (phase={nav.ParkPhase}, style={nav.ParkStyle}, " +
            $"d_dest={nav.DistanceToDestination()?.ToString("F1") ?? "?"} m)");
        // dead_end node: tile (0,2), (250, 100+1000) m -> px (500, 2200). The rear
        // axle stops a car length short of the pavement edge.
        double d = Math.Hypot(car.X - 500.0, car.Y - 2200.0) / Config.PIXELS_PER_METER;
        Assert.True(d < 7.0, $"stopped {d:F1} m from the dead-end node");
        // The parked latch fires below 0.3 m/s; allow the latching frame itself.
        Assert.True(Math.Abs(car.Speed) < 0.35, $"still rolling at {car.Speed * 3.6:F1} km/h");
        var seg = net.Segments[car.SegIdx];
        Assert.True(LatOffsetM(car, net) < seg.Width / 2.0 + 0.5, "parked off the road");
    }

    [Fact]
    public void DestinationFlag_ParksAtTheFlag()
    {
        var (car, driver, net) = Spawn("straight");
        // Flag 150 m south of spawn, at the right-kerb parking position
        // (ParkOffsetM(7) ~ 2.4 m left of centreline when heading south):
        // straight_n is at (100, 350) m -> px (200, 700); flag at y = 700 - 150*2.
        double fx = 2.0 * (100.0 - Config.ParkOffsetM(7.0));
        const double fy = 400.0;
        car.BicycleNav ??= new BicycleNav(car, net);
        car.BicycleNav.SetDestination(fx, fy);

        bool parked = false;
        Drive(car, net, driver, Keys("w"), 90.0,
              done: c => { parked = c.BicycleNav!.Parked; return parked; });
        var nav = car.BicycleNav!;

        Assert.True(parked,
            $"not parked after 90 s (phase={nav.ParkPhase}, style={nav.ParkStyle})");
        // Spec §1: the FRONT BUMPER rests on the flag. X/Y is the rear axle, so
        // measure the bumper.
        double rad = Math.Radians(car.Heading);
        double bx = car.X + Math.Sin(rad) * BicycleNav.FRONT_OVERHANG_M * Config.PIXELS_PER_METER;
        double by = car.Y + Math.Cos(rad) * BicycleNav.FRONT_OVERHANG_M * Config.PIXELS_PER_METER;
        double d = Math.Hypot(bx - fx, by - fy) / Config.PIXELS_PER_METER;
        Assert.True(d < 2.5, $"front bumper {d:F1} m from the flag");
    }

    // ------------------------------------------------------------------
    // U-turn (Wenden)
    // ------------------------------------------------------------------

    [Fact]
    public void Uturn_CompletesAndFlipsHeading()
    {
        var (car, driver, net) = Spawn("widths");
        double heading0 = car.Heading;

        // Drive south ~25 m first: at the dead-end node itself there is no room
        // behind for the U-turn tail (correctly rejected), and the entry speed cap
        // (18 km/h) needs a stop anyway. Coast to a standstill, then request.
        Drive(car, net, driver, Keys("w"), 6.0);
        Drive(car, net, driver, Keys(), 20.0, done: c => Math.Abs(c.Speed) < 0.3);
        Assert.True(Math.Abs(car.Speed) < 0.3, "car did not coast to a stop");
        driver.UteturnRequested = true;

        bool wasActive = false, finished = false;
        Drive(car, net, driver, Keys("w"), 90.0, done: c =>
        {
            var nav = c.BicycleNav!;
            if (nav.UturnActive) wasActive = true;
            finished = wasActive && !nav.UturnActive;
            return finished;
        });

        Assert.True(wasActive, "U-turn never started (request rejected or ignored)");
        Assert.True(finished, "U-turn did not finish within 90 s");

        double deltaH = (car.Heading - heading0 + 360.0) % 360.0;
        double dh = Math.Abs(((deltaH - 180.0 + 540.0) % 360.0) - 180.0);
        Assert.True(dh < 25.0, $"heading change {deltaH:F0} deg, expected ~180");
        var seg = net.Segments[car.SegIdx];
        Assert.True(LatOffsetM(car, net) < seg.Width / 2.0 + 0.6, "U-turn ended off the road");
    }
}
