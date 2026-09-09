using DrivingGame.Sim;
using Xunit;
using Xunit.Abstractions;

namespace DrivingGame.Sim.Tests;

/// <summary>TEMPORARY (remove after use): 50-car two-way fig8 - dump which
/// cap rule holds the stopped leaders at the crossing.</summary>
public class Fig8SignDiagTests
{
    private readonly ITestOutputHelper _out;
    public Fig8SignDiagTests(ITestOutputHelper outHelper) => _out = outHelper;

    [Fact(Skip = "TEMPORARY diag for the crossing deadlock - re-enable once fixed")]
    public void DumpStuckLeaders()
    {
        const int N = 50;
        var net = TestMaps.BuildTestMap("fig8_cross");
        var engine = new SimEngine(net, new ObstacleManager("fig8_cross"));
        double perimeter = net.Segments.Sum(s => s.Length);
        var before = new HashSet<int>(engine.Cars.Keys);

        for (int k = 0; k < N; k++)
        {
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
        var (cx, cy) = net.Nodes["fig8c_c"];
        var prevSpeed = new Dictionary<int, double>();
        for (int i = 0; i < 9600; i++)   // 160 s
        {
            engine.Tick(SimEngine.DtFixed);
            foreach (var c in cars)
            {
                double ps = prevSpeed.TryGetValue(c.Uid, out var v) ? v : 0.0;
                if (ps >= 0.5 && c.Speed < 0.1 &&
                    Math.Hypot(c.X - cx, c.Y - cy) / pppm < 20 &&
                    c.BicycleNav is { } nav)
                {
                    var d = nav.DbgState;
                    double dC = Math.Hypot(c.X - cx, c.Y - cy) / pppm;
                    _out.WriteLine($"STOP t={i * SimEngine.DtFixed:F1}s uid{c.Uid}({c.Color}) " +
                        $"dC={dC:F1} S={d.S:F1}/{d.Total:F1} plan={d.PlanPhase ?? "-"} " +
                        $"delta={d.DeltaDeg:F1}deg profV={d.ProfileV:F2} v={c.Speed:F3}");
                }
                prevSpeed[c.Uid] = c.Speed;
                // TEMPORARY: trace the crossing pairs while they are near C.
                {
                    double D(int a, int b) => Math.Hypot(engine.Cars[a].X - engine.Cars[b].X,
                                                        engine.Cars[a].Y - engine.Cars[b].Y) / pppm;
                    if ((D(14, 37) < 9.0 || D(11, 40) < 9.0) && (i % 20 == 0 || D(14, 37) < 6.5 || D(11, 40) < 6.5))
                    {
                        var scaps = CarCollisions.ComputeAvoidCaps(engine.Cars.Values, net);
                        string C(int u) => scaps.TryGetValue(u, out var cv) ? cv.ToString("F2") : "-";
                        _out.WriteLine($"PAIR t={(i * SimEngine.DtFixed):F1} " +
                            $"d14_37={D(14, 37):F1} d11_40={D(11, 40):F1} | " +
                            $"u14 v={engine.Cars[14].Speed:F1} cap={C(14)} u37 v={engine.Cars[37].Speed:F1} cap={C(37)} | " +
                            $"u11 v={engine.Cars[11].Speed:F1} cap={C(11)} u40 v={engine.Cars[40].Speed:F1} cap={C(40)}");
                    }
                }
                // TEMPORARY: per-tick longitudinal trace of the two pinned cars.
                if (i >= 9480 && i % 5 == 0 && c.Uid is 37 or 38 &&
                    c.BicycleNav is { } tn)
                {
                    var d = tn.DbgState;
                    _out.WriteLine($"T t={(i * SimEngine.DtFixed):F1} uid{c.Uid} v={c.Speed:F4} " +
                        $"vEndUpd={tn.DbgSpeedAtEnd:F4} accIn={d.AccelIn} vT={d.VTargetUsed:F2} " +
                        $"scale={d.AccelScaleFinal:F3} delta={d.DeltaDeg:F1} profV={d.ProfileV:F2}");
                }
            }
        }
        _out.WriteLine($"moving={cars.Count(c => c.Speed > 1.0)}/{cars.Count}");

        // TEMPORARY: longitudinal state of every stopped car near C.
        foreach (var c in cars.Where(c => c.Speed < 0.3 &&
                     Math.Hypot(c.X - cx, c.Y - cy) / pppm < 15))
        {
            if (c.BicycleNav is not { } nav) continue;
            var d = nav.DbgState;
            double dC = Math.Hypot(c.X - cx, c.Y - cy) / pppm;
            var (lx, ly) = nav.DbgPointAtS;
            double offM = Math.Hypot(c.X - lx, c.Y - ly) / pppm;
            _out.WriteLine($"NAV uid{c.Uid}({c.Color}) dC={dC:F1} S={d.S:F1}/{d.Total:F1} " +
                $"plan={d.PlanPhase ?? "-"} delta={d.DeltaDeg:F1}deg profV={d.ProfileV:F2} " +
                $"v={c.Speed:F3} acc={engine.GetControlFor(c.Uid)["accelerate"]}");
            _out.WriteLine($"     curv={nav.DbgCurvAtS:F4} (R={1.0 / Math.Max(Math.Abs(nav.DbgCurvAtS), 1e-6):F1}m) " +
                $"lineH={nav.DbgHeadingAtS:F0} carH={c.Heading:F0} offLine={offM:F2}m " +
                $"route=[{string.Join(",", nav.DbgRoute)}]");
            if (c.Uid is 37 or 38 && nav.Ref is { } rl)
            {
                var samp = new List<string>();
                for (double s = 25.0; s <= 60.01; s += 2.5)
                    samp.Add($"S{s:F0}:H{rl.HeadingAt(s):F0}/R{1.0 / Math.Max(Math.Abs(rl.CurvatureAt(s)), 1e-6):F0}/v{nav.SpeedProfileAt(s):F1}");
                _out.WriteLine("     " + string.Join(" ", samp));
                var pts = new List<string>();
                for (double s = 37.0; s <= 47.01; s += 0.5)
                {
                    var (qx, qy) = rl.PointAt(s);
                    pts.Add($"S{s:F1}=({qx / pppm:F1},{qy / pppm:F1})");
                }
                _out.WriteLine("     " + string.Join(" ", pts));
            }
        }

        // Padded-box view of v2: which stopped pairs near C actually intersect?
        var stuck = cars.Where(c =>
                     c.Speed < 0.3 && Math.Hypot(c.X - cx, c.Y - cy) / pppm < 15)
                       .ToList();
        foreach (var c in stuck)
            foreach (var o in stuck.Where(o => o.Uid > c.Uid))
            {
                var bc = ObstacleGeometry.BoxCorners(c.X, c.Y, c.Heading,
                    c.LengthM + 4.0, c.WidthM);
                var bo = ObstacleGeometry.BoxCorners(o.X, o.Y, o.Heading,
                    o.LengthM + 4.0, o.WidthM);
                if (ObstacleGeometry.BoxesIntersect(bc, bo))
                    _out.WriteLine($"PADDED-BOX-INTERSECT uid{c.Uid} x uid{o.Uid} " +
                        $"d={Math.Hypot(c.X - o.X, c.Y - o.Y) / pppm:F1}m");
            }

        // TEMPORARY: TRUE (unpadded) body overlaps in the final state - what
        // CarCollisions.Resolve would roll back + stop.
        foreach (var c in cars)
            foreach (var o in cars.Where(o => o.Uid > c.Uid))
            {
                if (Math.Hypot(c.X - o.X, c.Y - o.Y) / pppm > 20) continue;
                if (ObstacleGeometry.BoxesIntersect(
                        ObstacleGeometry.PlayerBodyCorners(c),
                        ObstacleGeometry.PlayerBodyCorners(o)))
                    _out.WriteLine($"TRUE-OVERLAP uid{c.Uid} x uid{o.Uid} " +
                        $"d={Math.Hypot(c.X - o.X, c.Y - o.Y) / pppm:F2}m");
            }

        CarCollisions.DebugLog.Clear();
        var caps = CarCollisions.ComputeAvoidCaps(cars, net);
        foreach (var c in stuck)
        {
            double dC = Math.Hypot(c.X - cx, c.Y - cy) / pppm;
            var mine = CarCollisions.DebugLog.Where(l => l.Contains($"uid{c.Uid}=")).ToList();
            _out.WriteLine($"{c.Color,-8} uid={c.Uid} seg={c.SegIdx} fwd={c.Forward} " +
                           $"pos=({c.X / pppm:F1},{c.Y / pppm:F1}) dC={dC:F1}m " +
                           $"target={c.TargetSpeed:F2} :: {string.Join(" | ", mine)}");
        }
        Assert.True(true);
    }
}
