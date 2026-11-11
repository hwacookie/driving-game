using DrivingGame.Sim;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace DrivingGame.Sim.Tests;

/// <summary>
/// THE PATTERN, measured as AREA (owner's method, 2026-11-06).
///
/// The picture the owner asked for: treat the car as ONE RECTANGLE and push it along the
/// manoeuvre like a piece of chalk — the union of all its positions IS the road area the
/// back-in occupies. That number, not a verdict string, is what makes one pattern better
/// than another: "maximal wenig Flächenverbrauch".
///
/// The order is his too, and it is the reason this file exists before any bay test:
///   1. get the PATTERN on free road (no neighbours to hold the wheel, no bay to be
///      refused) — which lock and which swing park with the least road;
///   2. only then TRANSLATE that pattern between two parked cars.
/// Measured on bay 24 before this existed: the reversing phase sweeps 37.82 m^2 = 4.77x
/// the car's own standing footprint (7.92 m^2), peak swing 49.9°, and the rest pose stands
/// 23° off parallel — so most of that area is the EXECUTOR correcting, not the pattern.
/// A pattern cannot be tuned against a number that includes its own execution errors,
/// hence: free road first.
/// </summary>
public class ParkingPatternTests
{
    const double Dt = SimEngine.DtFixed;
    const string Avenue = "pkw_n→pkw_s";
    const double MarkProgress = 0.50;

    // The scenario's primary car, so the areas here are comparable with the bay verdicts.
    const double LengthM = 4.40, WidthM = 1.80;
    const double WheelbaseM = 2.70;
    const double RearAxleM = LengthM * 58 / 200.0;      // Config.REAR_AXLE_OFFSET_M

    static double AreaM2(IEnumerable<(double U, double V, double Psi)> path)
    {
        // The rectangle swept by the body: a 5 cm grid over every pose's own bounding box,
        // point-in-quad tested. Cheap (the quads are small), and it is the same definition
        // the painted PNG in /tmp/rev_sweep.py uses, so picture and number agree.
        const double Cell = 0.05;
        var cells = new HashSet<(int, int)>();
        double hf = LengthM - RearAxleM, hr = RearAxleM, hw = WidthM / 2.0;
        foreach (var (u, v, psi) in path)
        {
            double fx = Math.Cos(psi), fy = Math.Sin(psi);
            double rx = -Math.Sin(psi), ry = Math.Cos(psi);
            // u,v carry the REAR AXLE (the engine's own convention); the body centre sits
            // half a body ahead of it along the car's axis.
            double cx = u + fx * (hf - hr) / 2.0, cy = v + fy * (hf - hr) / 2.0;
            var q = new (double X, double Y)[4];
            int i = 0;
            foreach (var (a, b) in new[] { (hf, hw), (hf, -hw), (-hr, -hw), (-hr, hw) })
                q[i++] = (cx + fx * a + rx * b, cy + fy * a + ry * b);
            double minx = q.Min(p => p.X), maxx = q.Max(p => p.X);
            double miny = q.Min(p => p.Y), maxy = q.Max(p => p.Y);
            // Math.Floor, NOT a cast: the path's u is NEGATIVE (the car reverses), and a
            // cast truncates toward zero, which turns a range like [-82,-79] into
            // [-163,-158] and makes the loop body never run — measured as `swept 0.00 m^2`.
            for (int ix = (int)Math.Floor(minx / Cell); ix <= (int)Math.Floor(maxx / Cell); ix++)
                for (int iy = (int)Math.Floor(miny / Cell); iy <= (int)Math.Floor(maxy / Cell); iy++)
                {
                    double px = (ix + 0.5) * Cell, py = (iy + 0.5) * Cell;
                    // The corners above are listed FRONT-RIGHT, FRONT-LEFT, REAR-LEFT,
                    // REAR-RIGHT, which is CLOCKWISE in this frame - so the edge cross
                    // products are <= 0 inside and > 0 outside. Testing `< 0` instead asked
                    // for a counter-clockwise quad and found no cell inside ANY pose: the
                    // first measurement of this file reported `swept 0.00 m^2` that way.
                    bool inside = true;
                    for (int k = 0; k < 4 && inside; k++)
                    {
                        var A = q[k]; var B = q[(k + 1) % 4];
                        if ((B.X - A.X) * (py - A.Y) - (B.Y - A.Y) * (px - A.X) > 0) inside = false;
                    }
                    if (inside) cells.Add((ix, iy));
                }
        }
        return cells.Count * Cell * Cell;
    }

    /// <summary>The engine's own two-arc back-in, rebuilt from the closed form documented in
    /// docs/DRIVING_MANEUVERS.md §1b and implemented in `ReverseParkPath`: R = L/tan(delta),
    /// swing th = psi0 + acos((cos psi0 + 1 - dv/R)/2). Rebuilt rather than called because
    /// the pattern question is a SWEEP over locks, and only this closed form lets the sweep
    /// run without driving a car 40 times.</summary>
    static List<(double U, double V, double Psi)>? ArcPath(double dv, double lockDeg,
                                                           double psi0 = 0.0, double ds = 0.02)
    {
        double k = Math.Tan(Math.Radians(lockDeg)) / WheelbaseM;
        double R = 1.0 / k;
        double arg = (Math.Cos(psi0) + 1.0 - dv / R) / 2.0;
        if (arg < -1.0 || arg > 1.0) return null;
        double th = psi0 + Math.Acos(arg);
        if (th <= 1e-4) return null;
        var pts = new List<(double, double, double)>();
        double u = 0, v = 0, psi = psi0;
        pts.Add((u, v, psi));
        double psiMin = psi0 - th;
        while (psi > psiMin)
        {
            psi -= k * ds; u -= Math.Cos(psi) * ds; v -= Math.Sin(psi) * ds;
            pts.Add((u, v, psi));
        }
        while (psi < 0.0)
        {
            psi += k * ds; u -= Math.Cos(psi) * ds; v -= Math.Sin(psi) * ds;
            pts.Add((u, v, psi));
        }
        return pts;
    }

    /// <summary>The pattern on free road: for one depth to the kerb, which lock sweeps the
    /// least road? The answer is not "the sharpest lock that fits" — it is the shallowest
    /// swing that still delivers the depth, and area is how that is seen.</summary>
    [Theory]
    [InlineData(3.10)]      // the bay-24 depth: v 5.25 m in the lane to the row's 8.35 m
    public void FreeRoadPattern_LeastAreaPerLock(double dvM)
    {
        var rows = new List<string>();
        // The numbers as DATA, not re-parsed out of the report text: this test's product is
        // the ranking, so a formatting change must not be able to break it.
        var areasByLock = new List<double>();
        double? bestArea = null; double bestLock = double.NaN, bestSwing = double.NaN;
        foreach (double lockDeg in new[] { 16.0, 20.0, 25.0, 30.0, 38.0 })
        {
            var path = ArcPath(dvM, lockDeg);
            if (path is null) { rows.Add($"{lockDeg,5:F0}°  no geometry"); continue; }
            double area = AreaM2(path);
            double swing = Math.Abs(Math.Degrees(path.Min(p => p.Psi)));
            double len = path.Skip(1).Select((p, i) =>
                Math.Hypot(p.U - path[i].U, p.V - path[i].V)).Sum();
            rows.Add($"{lockDeg,5:F0}°  swing {swing,5:F1}°  back {len,5:F2} m  swept {area,6:F2} m^2"
                     + $"  = {area / (LengthM * WidthM),4:F2} x footprint");
            areasByLock.Add(area);
            if (bestArea is null || area < bestArea.Value)
            {
                bestArea = area; bestLock = lockDeg; bestSwing = swing;
            }
        }
        // The whole sweep belongs to the test output: this file's product IS the numbers.
        Console.WriteLine($"free-road back-in, depth {dvM:F2} m " +
                          $"(car {LengthM:F2} x {WidthM:F2} m = {LengthM * WidthM:F2} m^2 footprint):");
        foreach (var r in rows) Console.WriteLine("  " + r);
        Assert.NotNull(bestArea);
        Console.WriteLine($"  least road: {bestLock:F0}° lock, swing {bestSwing:F1}°, " +
                          $"{bestArea:F2} m^2 = {bestArea.Value / (LengthM * WidthM):F2} x footprint");

        // What the sweep actually says, and it is NOT the intuition I came in with. The
        // areas were predicted to FALL with a shallower lock; measured they RISE slightly
        // (34.40 m^2 at 38° → 36.38 m^2 at 16°), because a shallower lock buys its smaller
        // swing by backing up further (6.86 m → 10.96 m) and the longer ribbon covers more
        // ground than the narrower one saves. The spread is only 5.7% of the footprint, so:
        //   * AREA IS NOT THE DISCRIMINATOR between single-arc patterns. Any lock that
        //     delivers the depth sweeps ~4.3-4.6 x footprint, and no pattern wins by a few
        //     square metres. Chasing "least area" here is chasing noise.
        //   * What the sweep does rank is REACH: a 16° lock needs 10.96 m of staging to
        //     deliver the same 3.10 m that costs 6.86 m at full lock. That is the axis the
        //     bays are actually constrained on - staging length - and it is why the bay-24
        //     plan takes the sharpest lock it can.
        //   * So the 37.82 m^2 measured on bay 24 (vs this pattern's 34.40 m^2 for the same
        //     depth) is only 10% worse than the best single-arc plan: the manoeuvre is not
        //     wasteful, and the -16 cm miss is NOT caused by a fat pattern. The difference
        //     between "planned" and "driven" is where the loss lives (rest pose 23° off
        //     parallel, wheel held by the envelope guard).
        var areas = areasByLock;
        Assert.True(areas.Count >= 3, "too few locks produced geometry to rank at all");
        double spread = areas.Max() - areas.Min();
        Console.WriteLine($"  area spread across the whole ladder: {spread:F2} m^2 " +
                          $"({spread / (LengthM * WidthM) * 100:F0}% of one footprint) -> area " +
                          "cannot rank these patterns; staging length can");
        Assert.True(spread < LengthM * WidthM,
            $"the swept areas differ by {spread:F2} m^2 - more than a whole footprint, so the "
            + "ladder is not comparable and the measure needs reviewing");
    }

    /// <summary>The pattern's own cost floor: the same depth reached by driving past the bay
    /// and reversing STRAIGHT in is impossible (no lateral gain), so the cheapest real
    /// manoeuvre is the arc above. This test pins the reference number the bay tests are
    /// compared against, so a bay verdict can say "the pattern needs 2.6x footprint, this
    /// one swept 4.8x" instead of only naming a miss.</summary>
    [Theory]
    [InlineData(3.10)]
    public void FreeRoadPattern_AreaIsReported(double dvM)
    {
        var path = ArcPath(dvM, 25.0);
        Assert.NotNull(path);
        double area = AreaM2(path!);
        // Sanity bounds with a physical meaning, not a tuned threshold: the swept area can
        // never be smaller than the body's own footprint (the car exists throughout), and it
        // can never exceed the corridor the path drives in - depth x length travelled.
        Assert.True(area >= LengthM * WidthM - 0.01,
            $"swept {area:F2} m^2 is below the body's own footprint {LengthM * WidthM:F2} m^2");
        double len = path!.Skip(1).Select((p, i) =>
            Math.Hypot(p.U - path[i].U, p.V - path[i].V)).Sum();
        Assert.True(area <= dvM * len + LengthM * WidthM + 0.01,
            $"swept {area:F2} m^2 exceeds the corridor it drove in " +
            $"({dvM:F2} m deep x {len:F2} m long plus one footprint)");
        Console.WriteLine($"pattern reference: depth {dvM:F2} m at 25° lock sweeps {area:F2} m^2 "
                          + $"= {area / (LengthM * WidthM):F2} x footprint over {len:F2} m of travel");
    }
}
