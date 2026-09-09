using DrivingGame.Sim;
using Xunit;
using Xunit.Abstractions;

namespace DrivingGame.Sim.Tests;

/// <summary>TEMPORARY (remove after use): DATA collection for the two live
/// defects in the 50-car demo - rear-end pairs (B) and cars stopping ON the
/// crossing instead of before it (C). Clean arc-spaced spawns (no spawn
/// overlap), 240 s, logs to /tmp/fig8_fifty_data.txt:</summary>
public class Fig8FiftyDataTests
{
    const string LogPath = "/tmp/fig8_fifty_data.txt";

    private readonly ITestOutputHelper _out;
    public Fig8FiftyDataTests(ITestOutputHelper outHelper) => _out = outHelper;

    [Fact]
    public void Collect()
    {
        File.WriteAllText(LogPath, "");
        const int N = 50;
        var net = TestMaps.BuildTestMap("fig8_cross");
        var engine = new SimEngine(net, new ObstacleManager("fig8_cross"));
        double pppm = Config.PIXELS_PER_METER;
        var (cx, cy) = net.Nodes["fig8c_c"];

        // --- clean spawn: car k evenly spaced around the loop, with the
        // crossing zone (±12 m of C) EXCLUDED from the spacing - 50 cars at
        // 14.5 m spacing guarantee a spawn INSIDE the box (pigeonhole), and
        // a stationary car in the middle of the crossing gridlocks everything
        // (measured: uid38 spawned nose-past-C and locked the loop from t=0).
        double perimeter = net.Segments.Sum(s => s.Length);
        double cArc = 0;   // arc position of node C along the segment order
        for (int i = 0; i < net.Segments.Count; i++)
        {
            var sg = net.Segments[i];
            if (sg.EndNode == "fig8c_c") { cArc += sg.Length; break; }
            if (sg.StartNode == "fig8c_c") break;
            cArc += sg.Length;
        }
        const double ZoneHalfM = 12.0;
        double zStart = (cArc - ZoneHalfM + perimeter) % perimeter;
        double avail = perimeter - 2 * ZoneHalfM;
        var before = new HashSet<int>(engine.Cars.Keys);
        for (int k = 0; k < N; k++)
        {
            bool rev = k % 2 == 1;
            // Compressed arc from zStart, skipping the zone.
            double s = (k + 0.5) * avail / N;
            double aRaw = (zStart + s) % perimeter;
            double fwdFromZ = (aRaw - zStart + perimeter) % perimeter;
            double target = fwdFromZ < 2 * ZoneHalfM
                ? (aRaw + 2 * ZoneHalfM) % perimeter
                : aRaw;
            double acc = 0; int seg = 0;
            for (int i = 0; i < net.Segments.Count; i++)
            {
                if (target <= acc + net.Segments[i].Length) { seg = i; break; }
                acc += net.Segments[i].Length;
                seg = i;
            }
            double prog = Math.Clamp((target - acc) / net.Segments[seg].Length, 0.05, 0.95);
            engine.EnqueueCommand(new TeleportCommand(null, seg, prog, true, null, null, rev));
            engine.Tick(SimEngine.DtFixed);
        }
        var cars = engine.Cars.Values.Where(c => !before.Contains(c.Uid))
                                     .OrderBy(c => c.Uid).ToList();
        foreach (var c in cars)
            engine.SetControl(c.Uid, new Dictionary<string, object?> { ["accelerate"] = true });

        // --- run 240 s with logging ---
        var firstSat = new Dictionary<(int, int), (double T, double X, double Y)>();
        var satCount = 0;
        var stoppedLong = new HashSet<int>();
        var slowSince = new Dictionary<int, int>();
        // per-car history ring for post-stop analysis (last 12 s)
        var hist = new Dictionary<int, List<(double T, double DC, double V)>>();
        foreach (var c in cars) hist[c.Uid] = new();

        const int Ticks = 240 * 60;
        for (int i = 0; i < Ticks; i++)
        {
            engine.Tick(SimEngine.DtFixed);
            double t = (i + 1) * SimEngine.DtFixed;

            // Full snapshots at key moments: who is where, on which segment.
            if ((i + 1) % 60 == 0 && t <= 30.0)
                foreach (var c in cars.OrderBy(c => c.Uid))
                {
                    var (bx, by) = c.BodyCenter();
                    double dC = Math.Hypot(bx - cx, by - cy) / pppm;
                    File.AppendAllText(LogPath,
                        $"SNAP t={t:F0} uid{c.Uid}({c.Color}) seg{c.SegIdx} " +
                        $"fwd={c.Forward} v={c.Speed * 3.6:F1} " +
                        $"pos=({c.X / pppm:F0},{c.Y / pppm:F0}) dCbody={dC:F1}\n");
                }

            // Cap-rule dump around t=32.2 s: which rule braked uid28 mid-box?
            if (t >= 32.0 && t <= 32.6)
                foreach (var line in CarCollisions.DebugLog)
                    File.AppendAllText(LogPath, $"CAP t={t:F3} {line}\n");

            // Queue trace: ALL cars within 20 m of C, every tick between
            // t=15 and t=45 - the cascade window (who stops where, when).
            if (t >= 15.0 && t <= 45.0)
                foreach (var c in cars)
                {
                    var (bx, by) = c.BodyCenter();
                    double dC = Math.Hypot(bx - cx, by - cy) / pppm;
                    if (dC < 20.0)
                        File.AppendAllText(LogPath,
                            $"Q t={t:F2} uid{c.Uid}({c.Color}) seg{c.SegIdx} fwd={c.Forward} " +
                            $"dCbody={dC:F2} v={c.Speed * 3.6:F1}\n");
                }

            // B: SAT with cheap distance prefilter, every tick.
            foreach (var a in cars)
                foreach (var b in cars.Where(b => b.Uid > a.Uid))
                {
                    if (firstSat.ContainsKey((a.Uid, b.Uid))) continue;
                    double dx = a.X - b.X, dy = a.Y - b.Y;
                    if (dx * dx + dy * dy > 20.0 * 20.0 * pppm * pppm) continue;
                    if (ObstacleGeometry.BoxesIntersect(
                            ObstacleGeometry.PlayerBodyCorners(a),
                            ObstacleGeometry.PlayerBodyCorners(b)))
                    {
                        satCount++;
                        firstSat[(a.Uid, b.Uid)] = (t, (a.X + b.X) / 2 / pppm, (a.Y + b.Y) / 2 / pppm);
                        File.AppendAllText(LogPath,
                            $"SAT uid{a.Uid}({a.Color})+uid{b.Uid}({b.Color}) t={t:F1}s " +
                            $"pos=({(a.X + b.X) / 2 / pppm:F0},{(a.Y + b.Y) / 2 / pppm:F0}) " +
                            $"va={a.Speed * 3.6:F1} vb={b.Speed * 3.6:F1}\n");
                    }
                }

            // C: crossing-zone trace (every 0.5 s) + long-stop detection.
            if ((i + 1) % 30 == 0)
                foreach (var c in cars)
                {
                    var (bx, by) = c.BodyCenter();
                    double dC = Math.Hypot(bx - cx, by - cy) / pppm;
                    if (dC < 25.0)
                        File.AppendAllText(LogPath,
                            $"ZONE t={t:F1} uid{c.Uid}({c.Color}) dCbody={dC:F1} " +
                            $"v={c.Speed * 3.6:F1} fwd={c.Forward}\n");
                }
            foreach (var c in cars)
            {
                var (bx, by) = c.BodyCenter();
                double dC = Math.Hypot(bx - cx, by - cy) / pppm;
                hist[c.Uid].Add((t, dC, c.Speed * 3.6));
                if (hist[c.Uid].Count > 12 * 60) hist[c.Uid].RemoveAt(0);

                if (c.Speed < 0.3)
                {
                    int since = slowSince.GetValueOrDefault(c.Uid, i);
                    slowSince[c.Uid] = since;
                    if (i - since > 10 * 60 && !stoppedLong.Contains(c.Uid))
                    {
                        stoppedLong.Add(c.Uid);
                        double noseGap = c.LengthM / 2.0 + 4.5;   // correct stop-line distance
                        var h = hist[c.Uid];
                        string trail = string.Join(" ",
                            h.Where(s => s.T > t - 10).TakeLast(20)
                            .Select(s => $"{s.V:F0}"));
                        File.AppendAllText(LogPath,
                            $"LONGSTOP uid{c.Uid}({c.Color}) t={t:F1}s dCbody={dC:F1} " +
                            $"noseToC={(dC - c.LengthM / 2.0):F1} stopLine={noseGap:F1} " +
                            $"(ON crossing: {(dC - c.LengthM / 2.0) < noseGap}) " +
                            $"fwd={c.Forward} last10s_v[km/h]={trail}\n");
                    }
                }
                else slowSince.Remove(c.Uid);
            }
        }

        _out.WriteLine($"SAT pairs: {firstSat.Count}, long stops: {stoppedLong.Count}");
        Assert.True(true);
    }
}
