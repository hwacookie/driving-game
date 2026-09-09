using DrivingGame.Sim;
using Xunit;
using Xunit.Abstractions;

namespace DrivingGame.Sim.Tests;

/// <summary>Regression test (2026-09-08): mixed vehicle classes on the
/// fig8_cross map, simultaneous start. Before per-class footprints in
/// CarCollisions, the sedan-box gap math let truck pairs close to physical
/// overlap (pickup+mixer at t=50.5s, tan+tractor at t=63.4s in the 90 s
/// headless repro), and Resolve then pinned both cars of each overlapping
/// pair at zero speed forever - a permanent gridlock of 5-7 cars. An
/// all-sedan control run never overlapped, isolating the footprint math as
/// the root cause. Asserts: no real-body overlap and no full standstill.</summary>
public class Fig8FleetTests
{
    private readonly ITestOutputHelper _out;
    public Fig8FleetTests(ITestOutputHelper outHelper) => _out = outHelper;

    // Real per-class lengths (Config.VEHICLE_SIZES) for the overlap metric.
    static double Len(string c) => Config.SizeForColor(c).LengthM;

    (SimEngine Engine, List<Car> Cars) SpawnFleet(string[] colors)
    {
        var net = TestMaps.BuildTestMap("fig8_cross");
        var engine = new SimEngine(net, new ObstacleManager("fig8_cross"));
        double perimeter = net.Segments.Sum(s => s.Length);
        var before = new HashSet<int>(engine.Cars.Keys);
        for (int k = 0; k < colors.Length; k++)
        {
            double target = (k + 0.5) * perimeter / colors.Length;
            int best = 0; double bestErr = double.PositiveInfinity, mid = 0;
            for (int i = 0; i < net.Segments.Count; i++)
            {
                double err = Math.Abs(mid + net.Segments[i].Length / 2 - target);
                if (err < bestErr) { best = i; bestErr = err; }
                mid += net.Segments[i].Length;
            }
            engine.EnqueueCommand(new TeleportCommand(null, best, 0.5, true, null, colors[k]));
            engine.Tick(SimEngine.DtFixed);
        }
        var cars = engine.Cars.Values.Where(c => !before.Contains(c.Uid))
                                     .OrderBy(c => c.Uid).ToList();
        foreach (var c in cars)
            engine.SetControl(c.Uid, new Dictionary<string, object?> { ["accelerate"] = true });
        return (engine, cars);
    }

    static double OverlapM(Car a, Car b)
    {
        // Centre distance minus half of each car's real length. Same-axis
        // approximation is sufficient: the queue pairs are same-direction,
        // and crossing pairs at these speeds register on centre distance too.
        double d = Math.Hypot(a.X - b.X, a.Y - b.Y) / Config.PIXELS_PER_METER;
        return (Len(a.Color) + Len(b.Color)) * 0.5 - d;
    }

    (double MaxOverlapM, int MovingCars, List<Car> Cars) Run(string[] colors, int ticks)
    {
        var (engine, cars) = SpawnFleet(colors);
        double maxOverlap = 0;
        for (int i = 0; i < ticks; i++)
        {
            engine.Tick(SimEngine.DtFixed);
            for (int a = 0; a < cars.Count; a++)
                for (int b = a + 1; b < cars.Count; b++)
                    maxOverlap = Math.Max(maxOverlap, OverlapM(cars[a], cars[b]));
        }
        int moving = cars.Count(c => c.Speed > 0.3);   // > ~1 km/h
        foreach (var c in cars)
            _out.WriteLine($"  {c.Color,-8} v={c.Speed * 3.6,5:F1} km/h " +
                           $"pos=({c.X / Config.PIXELS_PER_METER:F0},{c.Y / Config.PIXELS_PER_METER:F0})");
        return (maxOverlap, moving, cars);
    }

    [Fact]
    public void MixedFleet_NoOverlapsNoGridlock()
    {
        string[] mixed = { "blue", "silver", "police", "tan", "tractor", "pickup", "mixer" };
        var (maxOverlap, moving, _) = Run(mixed, 5400);   // 90 s at 60 Hz
        _out.WriteLine($"  max overlap: {maxOverlap:F2} m, moving cars: {moving}/7");
        Assert.True(maxOverlap <= 0.05,
            $"physical overlap of {maxOverlap:F2} m between mixed-class vehicles");
        Assert.True(moving >= 1, "full gridlock: every car stopped");
    }

    [Fact]
    public void AllSedanControl_NoOverlaps()
    {
        var (maxOverlap, _, _) = Run(
            new string[] { "blue", "blue", "blue", "blue", "blue", "blue", "blue" }, 5400);
        Assert.True(maxOverlap <= 0.05, $"overlap of {maxOverlap:F2} m (sedan control)");
    }
}
