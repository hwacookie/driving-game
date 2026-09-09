using DrivingGame.Sim;
using Xunit;
using Xunit.Abstractions;

namespace DrivingGame.Sim.Tests;

/// <summary>TEMPORARY (remove after use): 50 cars on fig8_cross, half per
/// direction, 900 s - SAT overlap + standstill check before the live demo.</summary>
public class Fig8FiftyDiagTests
{
    private readonly ITestOutputHelper _out;
    public Fig8FiftyDiagTests(ITestOutputHelper outHelper) => _out = outHelper;

    [Fact(Skip = "TEMPORARY diag for the crossing deadlock - re-enable once fixed")]
    public void FiftyCarsTwoDirections()
    {
        const int N = 50;
        var net = TestMaps.BuildTestMap("fig8_cross");
        var engine = new SimEngine(net, new ObstacleManager("fig8_cross"));
        double perimeter = net.Segments.Sum(s => s.Length);
        var before = new HashSet<int>(engine.Cars.Keys);

        for (int k = 0; k < N; k++)
        {
            // Interleaved: even k forward, odd k reverse, evenly around loop.
            bool rev = k % 2 == 1;
            double target = (k + 0.5) * perimeter / N;
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
        int satHits = 0;
        var firstSat = new Dictionary<(int, int), double>();
        var stoppedLong = new HashSet<int>();
        var slowSince = new Dictionary<int, int>();
        for (int i = 0; i < 54000; i++)   // 900 s
        {
            engine.Tick(SimEngine.DtFixed);
            if ((i + 1) % 30 == 0)        // every 0.5 s: SAT all pairs (grid-free, 1225 pairs)
                foreach (var c in cars)
                    foreach (var o in cars.Where(o => o.Uid > c.Uid))
                        if (ObstacleGeometry.BoxesIntersect(
                                ObstacleGeometry.PlayerBodyCorners(c),
                                ObstacleGeometry.PlayerBodyCorners(o)))
                        {
                            satHits++;
                            firstSat.TryAdd((c.Uid, o.Uid), (i + 1) * SimEngine.DtFixed);
                        }
            foreach (var c in cars)
            {
                if (c.Speed < 0.3)
                {
                    int since = slowSince.GetValueOrDefault(c.Uid, i);
                    slowSince[c.Uid] = since;
                    if (i - since > 60 * 20 && !stoppedLong.Contains(c.Uid))   // >20 s at ~0
                        stoppedLong.Add(c.Uid);
                }
                else slowSince.Remove(c.Uid);
            }
        }
        int fwd = cars.Count(c => c.Forward);
        _out.WriteLine($"fwd={fwd} rev={N - fwd}, SAT hits {satHits}, " +
                       $"cars stopped >20s: {stoppedLong.Count}");
        foreach (var kv in firstSat)
            _out.WriteLine($"  first SAT uid{kv.Key.Item1}+uid{kv.Key.Item2} at t={kv.Value:F1}s");
        var speeds = cars.Select(c => c.Speed * 3.6).OrderByDescending(v => v).ToList();
        _out.WriteLine($"speeds km/h: " + string.Join(" ", speeds.Take(8)) + " ... " +
                       string.Join(" ", speeds.Skip(N - 4)));
        Assert.True(satHits == 0, $"SAT overlaps: {satHits}");
    }
}
