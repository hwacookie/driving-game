using DrivingGame.Sim;
using Xunit;
using Xunit.Abstractions;

namespace DrivingGame.Sim.Tests;

/// <summary>TEMPORARY (remove after use): how far back must the yield stop
/// line at fig8_cross C be so that a STOPPED yield car's body is clear of
/// the priority lane? For each vehicle class as stopped yield car (on both
/// yield arms) at dC = 2.0..8.0 m from C, sweep every priority class along
/// BOTH priority directions through the crossing and report the minimum box
/// clearance. Pick the smallest nose gap G such that all pairs keep >= 0.5
/// m; then stopGapM = LengthM/2 + G.</summary>
public class Fig8StopLineDiagTests
{
    private readonly ITestOutputHelper _out;
    public Fig8StopLineDiagTests(ITestOutputHelper outHelper) => _out = outHelper;

    static double PosMod(double a, double m) => ((a % m) + m) % m;

    [Fact(Skip = "TEMPORARY diag for the crossing deadlock - re-enable once fixed")]
    public void SweepClearance()
    {
        var net = TestMaps.BuildTestMap("fig8_cross");
        double pppm = Config.PIXELS_PER_METER;
        var (cx, cy) = net.Nodes["fig8c_c"];
        string[] colors = { "blue", "silver", "police", "tan", "pickup", "tractor", "mixer" };

        double LenM(int i) => net.Segments[i].Length / pppm;

        // Mirror of SimEngine.CreateCarAtSegment placement (chord point +
        // right-hand lane offset relative to the ACTUAL heading).
        void Place(int segIdx, double p, bool reverse,
                   out double x, out double y, out double h)
        {
            var seg = net.Segments[segIdx];
            double px = seg.X1 + (seg.X2 - seg.X1) * p;
            double py = seg.Y1 + (seg.Y2 - seg.Y1) * p;
            double hd = PosMod(Math.Degrees(Math.Atan2(seg.X2 - seg.X1, seg.Y2 - seg.Y1)), 360.0);
            if (reverse) hd = PosMod(hd + 180.0, 360.0);
            double off = Config.LaneBaseOffsetM(seg.Width, seg.Lanes,
                                                seg.ParkingLaneWidth, seg.Oneway);
            double rad = Math.Radians(hd);
            x = px + Math.Cos(rad) * off * pppm;
            y = py - Math.Sin(rad) * off * pppm;
            h = hd;
        }

        // Priority sweep: at chord distance d from C, all four lane positions
        // (both directions, approach + exit sides of the crossing).
        var positions = new List<(double X, double Y, double H)>();
        for (double d = 40.0; d >= -40.0; d -= 1.0)
        {
            if (d >= 0)
            {
                Place(11, 1.0 - d / LenM(11), false, out var x1, out var y1, out var h1); // A approach
                Place(12, d / LenM(12), true,      out var x2, out var y2, out var h2); // B approach
                positions.Add((x1, y1, h1));
                positions.Add((x2, y2, h2));
            }
            else
            {
                double e = -d;
                Place(12, e / LenM(12), false, out var x3, out var y3, out var h3); // A exit
                Place(11, 1.0 - e / LenM(11), true, out var x4, out var y4, out var h4); // B exit
                positions.Add((x3, y3, h3));
                positions.Add((x4, y4, h4));
            }
        }

        // worst[color][dC bin 2.0 + 0.5*i] = min clearance over all priority
        // classes, both directions, both yield arms (px -> m below).
        double[,] worst = new double[colors.Length, 13];
        for (int ci = 0; ci < colors.Length; ci++)
            for (int bi = 0; bi < 13; bi++)
                worst[ci, bi] = double.PositiveInfinity;

        for (int ci = 0; ci < colors.Length; ci++)
        {
            var (yw, yl) = Config.SizeForColor(colors[ci]);
            foreach (var (ySeg, yRev) in new[] { (35, false), (36, true) })
            {
                for (double dTarget = 2.0; dTarget <= 8.01; dTarget += 0.5)
                {
                    // The lane offset shifts the measured distance from C, so
                    // scan progress for the placement closest to dTarget.
                    double bestP = 0.5, bestErr = double.PositiveInfinity;
                    for (int s = 0; s <= 400; s++)
                    {
                        double p = 1.0 - (s / 400.0) * 9.0 / LenM(ySeg); // ~9 m before C
                        Place(ySeg, p, yRev, out var sx, out var sy, out _);
                        double err = Math.Abs(Math.Hypot(sx - cx, sy - cy) / pppm - dTarget);
                        if (err < bestErr) { bestErr = err; bestP = p; }
                    }
                    Place(ySeg, bestP, yRev, out var yx, out var yy, out var yh);
                    double dC = Math.Hypot(yx - cx, yy - cy) / pppm;
                    int bin = (int)Math.Round((dC - 2.0) / 0.5);
                    if (bin < 0 || bin > 12) continue;

                    var ybox = ObstacleGeometry.BoxCorners(yx, yy, yh, yl, yw);
                    foreach (var pc in colors)
                    {
                        var (pw, pl) = Config.SizeForColor(pc);
                        foreach (var pos in positions)
                        {
                            var pbox = ObstacleGeometry.BoxCorners(pos.X, pos.Y, pos.H, pl, pw);
                            double clr = Clearance(pbox, ybox) / pppm;
                            if (clr < worst[ci, bin]) worst[ci, bin] = clr;
                        }
                    }
                }
            }
        }

        _out.WriteLine("min clearance (m) of a STOPPED yield car vs every priority class");
        _out.WriteLine("swept through C in both directions (OVERLAP = bodies intersect):");
        _out.WriteLine(string.Join("\t", "dC",
            colors.Select(c => c.Substring(0, Math.Min(4, c.Length))).ToArray()));
        for (int ci = 0; ci < colors.Length; ci++)
        {
            var (w, l) = Config.SizeForColor(colors[ci]);
            var row = new List<string> { $"{colors[ci]}({w:F2}x{l:F2})" };
            for (int bi = 0; bi < 13; bi++)
            {
                double v = worst[ci, bi];
                row.Add(v < 0 ? "OVERLAP" : $"{v:F2}");
            }
            _out.WriteLine(string.Join("\t", row));
        }

        // A uniform NOSE gap G gives centre distance L/2 + G; report per class
        // the minimum dC with >= 0.5 m clearance and the resulting nose gap.
        _out.WriteLine(" ");
        foreach (var c in colors)
        {
            int ci = Array.IndexOf(colors, c);
            var (w, l) = Config.SizeForColor(c);
            double minDc = -1;
            for (int bi = 0; bi < 13; bi++)
                if (worst[ci, bi] >= 0.5 && minDc < 0) minDc = 2.0 + 0.5 * bi;
            double nose = minDc > 0 ? minDc - l / 2 : -1;
            _out.WriteLine($"{c,-8} needs centre >= {minDc,4:F1} m from C  " +
                           $"(nose gap G >= {nose,4:F2} m)");
        }
    }

    static double Clearance(List<(double X, double Y)> a, List<(double X, double Y)> b)
    {
        if (ObstacleGeometry.BoxesIntersect(a, b)) return -1;
        double best = double.PositiveInfinity;
        for (int i = 0; i < a.Count; i++)
            for (int j = 0; j < b.Count; j++)
                best = Math.Min(best, SegSegDist(
                    a[i], a[(i + 1) % a.Count], b[j], b[(j + 1) % b.Count]));
        return best;
    }

    static double PtSegDist((double X, double Y) p, (double X, double Y) a,
                            (double X, double Y) b)
    {
        double abx = b.X - a.X, aby = b.Y - a.Y;
        double t = Math.Clamp(((p.X - a.X) * abx + (p.Y - a.Y) * aby) /
                              (abx * abx + aby * aby), 0.0, 1.0);
        return Math.Hypot(p.X - (a.X + t * abx), p.Y - (a.Y + t * aby));
    }

    static bool SegIntersect((double X, double Y) p1, (double X, double Y) p2,
                             (double X, double Y) p3, (double X, double Y) p4)
    {
        double D = (p2.X - p1.X) * (p4.Y - p3.Y) - (p2.Y - p1.Y) * (p4.X - p3.X);
        if (Math.Abs(D) < 1e-12) return false;
        double t = ((p3.X - p1.X) * (p4.Y - p3.Y) - (p3.Y - p1.Y) * (p4.X - p3.X)) / D;
        double u = ((p3.X - p1.X) * (p2.Y - p1.Y) - (p3.Y - p1.Y) * (p2.X - p1.X)) / D;
        return t >= 0 && t <= 1 && u >= 0 && u <= 1;
    }

    static double SegSegDist((double X, double Y) a1, (double X, double Y) a2,
                             (double X, double Y) b1, (double X, double Y) b2)
    {
        if (SegIntersect(a1, a2, b1, b2)) return 0;
        return Math.Min(Math.Min(PtSegDist(a1, b1, b2), PtSegDist(a2, b1, b2)),
                        Math.Min(PtSegDist(b1, a1, a2), PtSegDist(b2, a1, a2)));
    }
}
