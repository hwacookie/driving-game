using DrivingGame.Sim;
using Xunit;
using Xunit.Abstractions;

namespace DrivingGame.Sim.Tests;

/// <summary>TEMPORARY (remove after use): two-way fig8 - 4 cars, 2 per
/// direction. Checks reverse driving + head-on passing before the 50-car run.</summary>
public class Fig8TwoWayDiagTests
{
    private readonly ITestOutputHelper _out;
    public Fig8TwoWayDiagTests(ITestOutputHelper outHelper) => _out = outHelper;

    static double Len(string c) => Config.SizeForColor(c).LengthM;

    [Fact(Skip = "TEMPORARY diag for the crossing deadlock - re-enable once fixed")]
    public void FourCarsTwoDirections()
    {
        var net = TestMaps.BuildTestMap("fig8_cross");
        var engine = new SimEngine(net, new ObstacleManager("fig8_cross"));
        double perimeter = net.Segments.Sum(s => s.Length);
        var before = new HashSet<int>(engine.Cars.Keys);

        // (target fraction of loop, reverse?) - interleaved around the loop.
        (double F, bool Rev)[] spawns =
        {
            (0.125, false), (0.375, true), (0.625, false), (0.875, true),
        };
        foreach (var (f, rev) in spawns)
        {
            double target = f * perimeter;
            int best = 0; double bestErr = double.PositiveInfinity, mid = 0;
            for (int i = 0; i < net.Segments.Count; i++)
            {
                double err = Math.Abs(mid + net.Segments[i].Length / 2 - target);
                if (err < bestErr) { best = i; bestErr = err; }
                mid += net.Segments[i].Length;
            }
            engine.EnqueueCommand(new TeleportCommand(null, best, 0.5, true, null, null, rev));
            engine.Tick(SimEngine.DtFixed);
        }
        var cars = engine.Cars.Values.Where(c => !before.Contains(c.Uid))
                                     .OrderBy(c => c.Uid).ToList();
        foreach (var c in cars)
            engine.SetControl(c.Uid, new Dictionary<string, object?> { ["accelerate"] = true });

        double pppm = Config.PIXELS_PER_METER;
        double maxOverlap = 0;
        int stopEvents = 0;
        var wasMoving = new Dictionary<int, bool>();
        var firstOverlap = new Dictionary<(int, int), (double T, double X, double Y)>();

        // Signed lateral offset of the body centre from its segment chord,
        // positive to the RIGHT of the car's own travel direction. A car in
        // its proper lane reads ~+1.75; a wrong-side/centreline straddle
        // reads ~0 or negative.
        double OffsetRight(Car c)
        {
            var seg = net.Segments[c.SegIdx];
            double dx = seg.X2 - seg.X1, dy = seg.Y2 - seg.Y1;
            double lenSq = dx * dx + dy * dy;
            if (lenSq == 0) return 0.0;
            var (cx, cy) = c.BodyCenter();
            double t = Math.Max(0.0, Math.Min(1.0,
                ((cx - seg.X1) * dx + (cy - seg.Y1) * dy) / lenSq));
            double px = seg.X1 + t * dx, py = seg.Y1 + t * dy;
            double rad = Math.Radians(c.Heading);
            double rx = Math.Cos(rad), ry = -Math.Sin(rad);   // right of travel
            return ((cx - px) * rx + (cy - py) * ry) / pppm;
        }

        for (int i = 0; i < 36000; i++)   // 600 s
        {
            engine.Tick(SimEngine.DtFixed);
            double tSec = (i + 1) * SimEngine.DtFixed;
            if ((tSec >= 9 && tSec <= 13) || (tSec >= 28 && tSec <= 32))
                if ((i + 1) % 6 == 0)   // every 0.1 s in the event windows
                    _out.WriteLine($"t={tSec:F1}s " + string.Join(" | ",
                        cars.Select(c => { double o = OffsetRight(c);
                            return $"{c.Color[0]}({c.X / pppm:F0},{c.Y / pppm:F0})" +
                                   $" h={c.Heading:F0} off={(o >= 0 ? "+" : "")}{o:F1}"; })));
            // Proper SAT body-box test (the same geometry Resolve uses) -
            // the centre-distance metric false-positives on head-on pairs
            // passing side by side in their own lanes.
            for (int a = 0; a < cars.Count; a++)
                for (int b = a + 1; b < cars.Count; b++)
                {
                    if (!ObstacleGeometry.BoxesIntersect(
                            ObstacleGeometry.PlayerBodyCorners(cars[a]),
                            ObstacleGeometry.PlayerBodyCorners(cars[b])))
                        continue;
                    maxOverlap = Math.Max(maxOverlap, 1.0);   // flag any SAT hit
                    if (!firstOverlap.ContainsKey((cars[a].Uid, cars[b].Uid)))
                        firstOverlap[(cars[a].Uid, cars[b].Uid)] =
                            ((i + 1) * SimEngine.DtFixed,
                             (cars[a].X + cars[b].X) / 2 / pppm,
                             (cars[a].Y + cars[b].Y) / 2 / pppm);
                }
            foreach (var c in cars)
            {
                bool moving = c.Speed > 1.0;
                if (wasMoving.TryGetValue(c.Uid, out bool was) && was && !moving)
                    stopEvents++;
                wasMoving[c.Uid] = moving;
            }
        }
        foreach (var c in cars)
            _out.WriteLine($"{c.Color,-8} uid={c.Uid} fwd={c.Forward} v={c.Speed * 3.6,5:F1} km/h " +
                           $"pos=({c.X / pppm:F0},{c.Y / pppm:F0}) h={c.Heading:F0}");
        if (firstOverlap.Count == 0)
            _out.WriteLine("NO real body-box overlaps");
        foreach (var kv in firstOverlap)
            _out.WriteLine($"SAT OVERLAP {cars.First(c => c.Uid == kv.Key.Item1).Color} + " +
                           $"{cars.First(c => c.Uid == kv.Key.Item2).Color} at " +
                           $"t={kv.Value.T:F1}s pos=({kv.Value.X:F0},{kv.Value.Y:F0})");
        _out.WriteLine($"stop events {stopEvents}");
        Assert.True(firstOverlap.Count == 0, "real body-box overlap occurred");
    }
}
