using DrivingGame.Sim;
using Xunit;

namespace DrivingGame.Sim.Tests;

/// <summary>TEMPORARY probe: replicates the frozen 50-car run at t≈32.2 s,
/// where uid28 (mixer, yield car crossing C from NE) braked to a stop INSIDE
/// the box (dCbody 1.96) and blocked the priority road. Which rule capped it?
/// Writes /tmp/fig8_probe.txt.</summary>
public class Fig8ProbeTests
{
    const double Pppm = Config.PIXELS_PER_METER;

    // Lane geometry (metres): C=(250,250); n13=(218,220.3) NW; n11=(282,279.7) SE;
    // n35=(218,279.7) SW; n37=(282,220.3) NE.
    // Toward-C unit vectors per segment: seg35 (n35->C): (0.734,-0.681);
    // seg36 (C->n37): (-0.734,0.681); seg11 (n11->C): (-0.734,-0.681);
    // seg12 (C->n13): (0.734,0.681).
    static Car Place(string color, int segIdx, bool forward,
                     double dCbody, double speed,
                     (double Ax, double Ay) towardC, bool approaching)
    {
        // Position: dCbody from C towards the far node (= -towardC direction).
        double ax = 250 - dCbody * towardC.Ax;
        double ay = 250 - dCbody * towardC.Ay;
        (double dx, double dy) dir = approaching ? towardC : (-towardC.Ax, -towardC.Ay);
        double rx = dir.dy, ry = -dir.dx;   // right of travel
        ax += 1.75 * rx; ay += 1.75 * ry;
        double h = Math.Degrees(Math.Atan2(dir.dx, dir.dy));
        var c = new Car(ax * Pppm, ay * Pppm, Math.PosDeg(h), segIdx);
        c.Color = color;
        c.Forward = forward;
        c.Speed = speed;
        return c;
    }

    [Fact]
    public void ProbeUid28MidBox()
    {
        var net = TestMaps.BuildFig8CrossTestMap();
        var sb = new System.Text.StringBuilder();

        // Configuration at t≈32.2 s (from the 50-car trace):
        var cars = new List<Car>
        {
            Place("mixer",   36, false, 2.06, 4.08,  (-0.734, 0.681), true),  // uid28 crossing from NE
            Place("mixer",   35, true,  17.5, 2.8,   ( 0.734,-0.681), true),  // uid21 approaching from SW
            Place("silver",  36, false, 8.72, 0.0,   (-0.734, 0.681), true),  // uid30 at NE line
            Place("tan",     36, false, 15.08, 0.0,  (-0.734, 0.681), true),  // uid32 queued behind
            Place("silver",  36, true,  11.0, 11.4,  (-0.734, 0.681), false), // uid23 exiting towards NE
        };
        int uid28 = cars[0].Uid;

        CarCollisions.DebugLog.Clear();
        var caps = CarCollisions.ComputeAvoidCaps(cars, net);
        sb.AppendLine("== full t=32.2 configuration ==");
        foreach (var line in CarCollisions.DebugLog) sb.AppendLine(line);
        foreach (var c in cars)
            sb.AppendLine($"cap uid{c.Uid}({c.Color}) = " +
                          (caps.TryGetValue(c.Uid, out var v) ? $"{v:F2} m/s" : "-"));

        // Isolate: uid28 alone with each other car.
        for (int i = 1; i < cars.Count; i++)
        {
            CarCollisions.DebugLog.Clear();
            var pair = new[] { cars[0], cars[i] };
            var pcaps = CarCollisions.ComputeAvoidCaps(pair, net);
            sb.AppendLine($"\n== uid28 + uid{cars[i].Uid}({cars[i].Color}) only ==");
            foreach (var line in CarCollisions.DebugLog) sb.AppendLine(line);
            sb.AppendLine("cap uid28 = " +
                          (pcaps.TryGetValue(uid28, out var v) ? $"{v:F2} m/s" : "-"));
        }

        System.IO.File.WriteAllText("/tmp/fig8_probe.txt", sb.ToString());
        Assert.True(true);
    }
}
