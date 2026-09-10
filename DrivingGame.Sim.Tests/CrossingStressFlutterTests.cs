using DrivingGame.Sim;
using Xunit;

namespace DrivingGame.Sim.Tests;

/// <summary>
/// Deterministic reproduction of the crossing-stress scenario (the e2e
/// 'fig8_xing'): 50 cars (25 per direction, interleaved) on the 48-segment
/// signed crossing loop, 50 km/h rolling start, accelerator held, run up to
/// 15 s of sim time or the FIRST contact.
///
/// Two tests:
///   1. DecisionRate_StaysHuman - the regression test for the cap-persistence
///      fix (docs/Human Factor Model.md §3/§5): once a car engages the brake,
///      the brake decision must be held (no "gap clear" < 0.4 s after a
///      "brake:"); the old flutter released/re-engaged on 0.08 s cycles
///      (~12 decisions/s vs the ~2.5/s human ceiling, doc §1). GREEN.
///   2. NoContact_Within_15s - the e2e pass criterion (no crash). Still RED:
///      the persistence fix removed the flutter and the first crash (car 13
///      vs 37), but the run now ends when a COMMITTED yield car crosses the
///      box into a stopped priority car (measured: car 40 hit stopped car 14
///      at 13 km/h at t=4.98 s). That is a different failure mode - the
///      commit rule (R3) does not check for a blocked crossing zone (R14,
///      still [proposed]) and the v1/v2 detection misses a stopped body in
///      corner geometry - and needs its own design. The crashed pair's
///      decision logs are dumped to /tmp/flap_crash_decisions.txt on failure.
/// </summary>
public class CrossingStressTests
{
    const double Dt = SimEngine.DtFixed;
    const int NCars = 50;
    const double SpawnKmh = 50.0;
    const double RunSeconds = 15.0;

    [Fact]
    public void CrossingStress_DecisionRate_StaysHuman()
    {
        RunScenario(out _, out _, out _, out var cars);

        // THE FLUTTER, expressed as decision persistence: once a car engages
        // the brake ("brake: ..."), the brake decision must be HELD - a
        // "gap clear - resuming" less than 0.4 s later means the cap flapped
        // (the old behavior released/re-engaged on 0.08 s cycles, 12/s vs the
        // ~2.5/s human ceiling, doc §1/§3). A re-engagement AFTER a clear is
        // NOT penalized: a new threat appearing right after a release is a
        // new, legitimate decision (e.g. a car handing over from the crossing
        // rule to the car-ahead rule), not flutter.
        double minHold = double.PositiveInfinity;
        int worstCar = -1;
        foreach (var c in cars)
        {
            var entries = c.DecisionsSnapshot()
                .Where(d => d.Msg.StartsWith("brake:") || d.Msg.StartsWith("gap clear"))
                .ToList();
            for (int i = 0; i < entries.Count; i++)
            {
                if (!entries[i].Msg.StartsWith("brake:")) continue;
                for (int j = i + 1; j < entries.Count; j++)
                {
                    if (entries[j].Msg.StartsWith("gap clear"))
                    {
                        double hold = entries[j].T - entries[i].T;
                        if (hold < minHold) { minHold = hold; worstCar = c.Uid; }
                        break;   // first clear after this brake = its hold time
                    }
                }
            }
        }
        // Dump the worst car's full decision log for analysis.
        if (worstCar >= 0)
        {
            var sw = new System.Text.StringBuilder();
            foreach (var d in cars.First(c => c.Uid == worstCar).DecisionsSnapshot())
                sw.AppendLine($"t={d.T:F3} {d.Msg}");
            File.WriteAllText("/tmp/flap_worst_car.txt", sw.ToString());
        }
        Assert.True(minHold >= 0.4,
            $"car {worstCar} released the brake after only {minHold * 1000:F0} ms " +
            $"(decision persistence: the brake must be held ~0.5-1.5 s, " +
            $"docs/Human Factor Model.md §3) - the cap flutters instead of " +
            $"persisting; log in /tmp/flap_worst_car.txt");
    }

    [Fact]
    public void CrossingStress_NoContact_Within_15s()
    {
        RunScenario(out double crashT, out int crashA, out int crashB, out var cars);

        Assert.True(crashT < 0,
            $"crash at t={crashT:F2}s: car {crashA} contacted car {crashB} " +
            $"(known remaining failure mode: committed car crosses into a " +
            $"stopped body in the box - see class docs, R14)");
    }

    // --- scenario ------------------------------------------------------------

    static void RunScenario(out double crashT, out int crashA, out int crashB,
                            out List<Car> cars)
    {
        var net = TestMaps.BuildTestMap("basic");

        // The 48-segment loop of the signed crossing (start-node prefix
        // 'xing_', as the e2e scenario reads it live from the game).
        var loop = new List<int>();
        for (int i = 0; i < net.Segments.Count; i++)
            if (net.Segments[i].StartNode.StartsWith("xing_")) loop.Add(i);
        Assert.Equal(48, loop.Count);
        double perimeterM = loop.Sum(i => net.Segments[i].Length) / Config.PIXELS_PER_METER;

        // Spawn: same as the e2e - evenly spaced arc positions, alternating
        // direction (even = forward, odd = reverse), 50 km/h rolling start.
        cars = new List<Car>();
        for (int k = 0; k < NCars; k++)
        {
            bool reverse = (k % 2 == 1);
            double targetM = (k + 0.5) * perimeterM / NCars;
            int best = loop[0];
            double bestErr = double.PositiveInfinity, mid = 0.0;
            foreach (int si in loop)
            {
                double segLenM = net.Segments[si].Length / Config.PIXELS_PER_METER;
                double err = Math.Abs(mid + segLenM / 2.0 - targetM);
                if (err < bestErr) { best = si; bestErr = err; }
                mid += segLenM;
            }
            cars.Add(CreateCarAtSegment(net, best, 0.5, reverse, SpawnKmh / 3.6));
        }

        var throttle = new Dictionary<int, ControlInput>();
        foreach (var c in cars) throttle[c.Uid] = new ControlInput { Accelerate = true };
        var validator = new PhysicsValidator(enabled: true);

        crashT = -1; crashA = -1; crashB = -1;
        int substeps = (int)Math.Round(RunSeconds / Dt);
        for (int i = 1; i <= substeps; i++)
        {
            double t = i * Dt;
            var prev = new Dictionary<int, (double, double, double)>();
            foreach (var c in cars) prev[c.Uid] = (c.X, c.Y, c.Heading);

            var caps = CarCollisions.ComputeAvoidCaps(cars, net, t);
            foreach (var c in cars)
            {
                double preSpeed = c.Speed;
                var control = throttle[c.Uid];
                if (caps.TryGetValue(c.Uid, out double preCap) && preSpeed >= preCap - 0.02)
                    control = new ControlInput
                    {
                        Accelerate = false, Brake = control.Brake,
                        BlinkerLeft = control.BlinkerLeft, BlinkerRight = control.BlinkerRight,
                    };
                c.Update(Dt, net, control);
                if (caps.TryGetValue(c.Uid, out double cap))
                {
                    double ceiling = Math.Max(cap, preSpeed - Config.CAR_BRAKING * Dt);
                    if (c.Speed > ceiling) c.Speed = ceiling;
                }
            }
            var impact = new Dictionary<int, double>();
            foreach (var c in cars) impact[c.Uid] = c.Speed;
            var inContact = CarCollisions.Resolve(cars, prev, t, impact);
            foreach (var c in cars)
                validator.Check(c, Dt, net, t, inContact.Contains(c.Uid));

            if (inContact.Count > 0)
            {
                crashT = t;
                crashA = inContact.First();
                foreach (var c in cars)
                    if (c.InContact && c.Uid != crashA) { crashB = c.Uid; break; }
                // Dump the crashed pair's decision logs for analysis.
                var sbd = new System.Text.StringBuilder();
                foreach (int uid in new[] { crashA, crashB })
                {
                    sbd.AppendLine($"--- car {uid} ---");
                    foreach (var d in cars.First(c => c.Uid == uid).DecisionsSnapshot())
                        sbd.AppendLine($"t={d.T:F3} {d.Msg}");
                }
                File.WriteAllText("/tmp/flap_crash_decisions.txt", sbd.ToString());
                break;
            }
        }
    }

    /// <summary>Same spawn as SimEngine.CreateCarAtSegment (which needs an
    /// engine instance): position at `progress`, right-lane offset, optional
    /// rolling start speed.</summary>
    static Car CreateCarAtSegment(RoadNetwork net, int segIdx, double progress,
                                  bool reverse, double speedMps)
    {
        var seg = net.Segments[segIdx];
        double x = seg.X1 + (seg.X2 - seg.X1) * progress;
        double y = seg.Y1 + (seg.Y2 - seg.Y1) * progress;
        double h = PosMod(Math.Degrees(Math.Atan2(seg.X2 - seg.X1, seg.Y2 - seg.Y1)), 360.0);
        if (reverse) h = PosMod(h + 180.0, 360.0);
        double offsetM = Config.LaneBaseOffsetM(seg.Width, seg.Lanes,
                                                seg.ParkingLaneWidth, seg.Oneway);
        double rad = Math.Radians(h);
        x += Math.Cos(rad) * offsetM * Config.PIXELS_PER_METER;
        y -= Math.Sin(rad) * offsetM * Config.PIXELS_PER_METER;
        var car = new Car(x, y, h, segIdx, new BicycleDriver());
        car.Progress = progress;
        car.Forward = !reverse;
        car.LaneOffsetOverrideM = offsetM;
        car.Color = Config.CAR_COLORS[(car.Uid - 1) % Config.CAR_COLORS.Length];
        car.Speed = speedMps;
        return car;
    }

    static double PosMod(double v, double m) => ((v % m) + m) % m;
}
