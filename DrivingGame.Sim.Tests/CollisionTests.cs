using DrivingGame.Sim;
using Xunit;

namespace DrivingGame.Sim.Tests;

/// <summary>Car-to-car collision avoidance (CarCollisions v1 corridor cap +
/// v2 oblique/crossing pair prediction with right-before-left), exercised
/// through a REAL headless SimEngine (not a bare Car.Update loop): the
/// avoidance/response pipeline lives only inside SimEngine.Tick's two-pass
/// collision substep, so these tests must drive the actual engine to mean
/// anything. All three scenarios sit at the "basic" test map's crossroads
/// (cross_n/s/w/e around cross_center - see TestMaps.cs).</summary>
public class CollisionTests
{
    const double Dt = SimEngine.DtFixed;
    const double Pppm = Config.PIXELS_PER_METER;

    static (SimEngine Engine, RoadNetwork Net) NewEngine()
    {
        var net = TestMaps.BuildTestMap("basic");
        var obs = new ObstacleManager("basic");
        return (new SimEngine(net, obs), net);
    }

    /// <summary>Spawns a car at a named start point (Add=true - alongside any
    /// existing cars) and returns the new Car reference, identified by the
    /// uid that appeared after the spawning tick (uids are a process-wide
    /// monotonic counter, not reset per test). The color is pinned to the
    /// sedan class on purpose: without it the default uid-based palette
    /// cycle would hand out truck classes (lower top speed/accel/cornering
    /// budget - see Config.VEHICLE_CLASS_SPECS) depending on how many cars
    /// earlier tests spawned, and these scenarios compare runs against solo
    /// baselines that must use identical dynamics.</summary>
    static Car SpawnAndGet(SimEngine engine, string startPoint, double progress,
                           string color = "blue")
    {
        var before = new HashSet<int>(engine.Cars.Keys);
        engine.EnqueueCommand(new TeleportCommand(startPoint, null, progress, true, null, color));
        engine.Tick(Dt);
        return engine.Cars.Values.First(c => !before.Contains(c.Uid));
    }

    static void HoldAccelerate(SimEngine engine, Car car) =>
        engine.SetControl(car.Uid, new Dictionary<string, object?> { ["accelerate"] = true });

    static double CenterM(Car a, Car b) => Math.Hypot(a.X - b.X, a.Y - b.Y) / Pppm;

    // ------------------------------------------------------------------
    // Scenario 1: stationary lead car - v1 corridor cap alone suffices.
    // ------------------------------------------------------------------

    [Fact]
    public void StationaryLeadCar_FollowerBrakesAndStopsBehind()
    {
        var (engine, _) = NewEngine();
        // Both on the north spoke (cross_n -> cross_center), heading south.
        // Higher progress = closer to the centre = "ahead"; A never gets an
        // accelerate signal, so it stays put (confirmed: a BICYCLE car with
        // no destination and no accelerate eases to and stays at Speed=0).
        var a = SpawnAndGet(engine, "crossroads_from_north", 0.7);   // stationary lead
        var b = SpawnAndGet(engine, "crossroads_from_north", 0.3);   // follower, 72 m behind
        HoldAccelerate(engine, b);

        double minGapM = double.PositiveInfinity;
        for (int i = 0; i < 1800; i++)   // 30 s
        {
            engine.Tick(Dt);
            minGapM = Math.Min(minGapM, CenterM(a, b) - Config.CAR_LENGTH);
        }

        Assert.Equal(0.0, a.Speed);
        Assert.True(b.Speed < 0.3, $"follower should have braked to a stop, speed={b.Speed:F2} m/s");
        double finalGapM = CenterM(a, b) - Config.CAR_LENGTH;
        Assert.InRange(finalGapM, 1.5, 5.5);              // ~3 m standstill gap
        Assert.True(minGapM > 0.5, $"cars nearly touched (avoidance should stop it smoothly): min gap={minGapM:F2} m");
    }

    // ------------------------------------------------------------------
    // Scenario 2: head-on, separate lanes, no risk - must NOT false-positive.
    // ------------------------------------------------------------------

    /// <summary>Speed trace of a single car driven alone (no other traffic)
    /// - the baseline a two-car run must match if the other car caused it
    /// no extra braking. A solo car still legitimately decelerates for the
    /// real junction ahead, so "neither brakes" can't mean "speed never
    /// decreases" - it means "no different than driving it alone".</summary>
    static double[] SoloSpeedTrace(string startPoint, double progress, int ticks)
    {
        var (engine, _) = NewEngine();
        var car = SpawnAndGet(engine, startPoint, progress);
        HoldAccelerate(engine, car);
        var trace = new double[ticks];
        for (int i = 0; i < ticks; i++)
        {
            engine.Tick(Dt);
            trace[i] = car.Speed;
        }
        return trace;
    }

    [Fact]
    public void HeadOnSeparateLanes_NeitherBrakes()
    {
        const int ticks = 1500;   // 25 s - plenty to close 170 m and pass
        var soloA = SoloSpeedTrace("crossroads_from_west", 0.5, ticks);
        var soloB = SoloSpeedTrace("crossroads_from_east", 0.5, ticks);

        var (engine, _) = NewEngine();
        // West and east spokes are the same straight road, split at the
        // junction node; each car sits in its own lane (~1.75 m either side
        // of the centreline, ~3.5 m apart) - outside CorridorHalfWidthM=1.8.
        var a = SpawnAndGet(engine, "crossroads_from_west", 0.5);   // heading east
        var b = SpawnAndGet(engine, "crossroads_from_east", 0.5);   // heading west
        HoldAccelerate(engine, a);
        HoldAccelerate(engine, b);

        const double eps = 0.3;   // m/s slack (discrete-step + tiny profile jitter)
        double minGapM = double.PositiveInfinity;
        for (int i = 0; i < ticks; i++)
        {
            engine.Tick(Dt);
            Assert.True(a.Speed >= soloA[i] - eps,
                $"A braked at t={i * Dt:F2}s vs solo: {a.Speed:F2} < solo {soloA[i]:F2} m/s");
            Assert.True(b.Speed >= soloB[i] - eps,
                $"B braked at t={i * Dt:F2}s vs solo: {b.Speed:F2} < solo {soloB[i]:F2} m/s");
            minGapM = Math.Min(minGapM, CenterM(a, b));
        }

        Assert.True(minGapM > 3.0,
            $"cars got closer than the lane separation allows: {minGapM:F2} m (expected ~3.5 m)");
    }

    // ------------------------------------------------------------------
    // Scenario 3: simultaneous crossing - right-before-left (v2 required).
    // v1 corridor alone would brake BOTH cars to a mutual standstill at the
    // junction; v2's pair prediction + RBL must make only the car seeing the
    // other on its right (B, from the east) yield, while A (from the north,
    // on B's right) proceeds through unimpeded.
    // ------------------------------------------------------------------

    [Fact]
    public void SimultaneousCrossing_EastYieldsToNorth_RightBeforeLeft()
    {
        var (engine, net) = NewEngine();
        var center = net.Nodes["cross_center"];
        // Symmetric 85 m-from-centre spawns (north spoke is 180 m; progress
        // is measured from cross_n, so 85 m short of the centre is progress
        // 1 - 85/180).
        double northProgress = 1.0 - 85.0 / 180.0;
        var a = SpawnAndGet(engine, "crossroads_from_north", northProgress);   // heading south
        var b = SpawnAndGet(engine, "crossroads_from_east", 0.5);             // heading west
        HoldAccelerate(engine, a);
        HoldAccelerate(engine, b);

        double minSpeedANearCenter = double.PositiveInfinity;
        double minSpeedBBeforeCenter = double.PositiveInfinity;
        double minPairGapM = double.PositiveInfinity;

        for (int i = 0; i < 2700; i++)   // 45 s
        {
            engine.Tick(Dt);
            double distAM = Math.Hypot(a.X - center.X, a.Y - center.Y) / Pppm;
            double distBM = Math.Hypot(b.X - center.X, b.Y - center.Y) / Pppm;
            if (distAM < 30.0) minSpeedANearCenter = Math.Min(minSpeedANearCenter, a.Speed);
            if (distBM > 0.5) minSpeedBBeforeCenter = Math.Min(minSpeedBBeforeCenter, b.Speed);
            minPairGapM = Math.Min(minPairGapM, CenterM(a, b));
        }

        // A has the right-of-way (B sees it on B's right) and must pass
        // through the crossing without ever nearly stopping for it.
        Assert.True(minSpeedANearCenter > 3.0,
            $"A (right-of-way) should proceed through the crossing without stopping, " +
            $"min speed near centre={minSpeedANearCenter:F2} m/s");
        // B must yield: brake to a near-stop before entering the crossing -
        // this is the signature that distinguishes v2+RBL from v1-only
        // (which would make BOTH cars stop).
        Assert.True(minSpeedBBeforeCenter < 1.0,
            $"B should yield (right-before-left) and brake to a near-stop before the " +
            $"crossing, min speed={minSpeedBBeforeCenter:F2} m/s");
        Assert.True(minPairGapM > 2.0, $"cars got too close: {minPairGapM:F2} m");

        // Both eventually clear the junction (A goes first, B resumes once
        // A is clear) - no deadlock.
        double aPastCenterM = (center.Y - a.Y) / Pppm;     // south = -Y
        double bPastCenterM = (center.X - b.X) / Pppm;     // west = -X
        Assert.True(aPastCenterM > 20.0,
            $"A should have driven through and well past the crossing: {aPastCenterM:F1} m past centre");
        Assert.True(bPastCenterM > 5.0,
            $"B should have resumed and cleared the crossing once A passed: {bPastCenterM:F1} m past centre");
    }
}
