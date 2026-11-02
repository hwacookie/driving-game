using DrivingGame.Sim;
using System.Collections.Generic;
using Xunit;

namespace DrivingGame.Sim.Tests;

/// <summary>
/// §1d (docs/DRIVING_MANEUVERS.md, owner decision 2026-11-03): a car may claim
/// a bay only FROM BESIDE IT — its own body inside the mouth, rear past the
/// front of the parked car before it, front before the rear of the parked car
/// after it. The window that leaves is [mouth_rear + L/2, mouth_front − L/2],
/// and its width is exactly tier 1's `free`: the claim is tier 1 evaluated at
/// the one position where it is legal to evaluate it. The first mouth in
/// driving order wins, and the order is the lane order — nothing is reserved
/// for choosing.
///
/// The numbers are the row the live run made (tests/bay_row_check.py, 2026-11-03
/// on the basic map, segment pkw_n→pkw_s): parked cars at u = 120.00, 130.04,
/// 137.78, 146.32, 154.36 with bodies of 3.14 m — a 6.90 / 4.60 / 5.40 / 4.90
/// m row of mouths — and pm1, a mercedes_c220, measured stopped with centre
/// u=133.0. Under this rule pm1 names no bay, and the picture agreed: it stood
/// in the driving lane 6.7 m past the nearest window, not beside any mouth.
/// </summary>
public class BayClaimTests
{
    /// The measured row, built from the models rather than typed twice.
    static List<(double CentreU, double LengthM)> Row(string parkedModel,
        double startCentreU, params double[] gapsBetween)
    {
        double len = Config.ModelFor(parkedModel).LengthM;
        var l = new List<(double, double)>();
        double c = startCentreU;
        l.Add((c, len));
        foreach (var g in gapsBetween)
        {
            c += len + g;
            l.Add((c, len));
        }
        return l;
    }

    private static (double RearU, double FrontU, double FreeM)? Claim(
        string model, double centreU)
    {
        return BicycleNav.ClaimBayBeside(centreU, Config.ModelFor(model).LengthM,
            Row("fiat_500", 120.00, 6.90, 4.60, 5.40, 4.90));
    }

    /// What a claim answered, as three plain doubles — a nullable tuple keeps
    /// its member names out of reach of the compiler, and no test here reads
    /// a null answer through a pattern.
    private static bool Claimed(string model, double centreU,
        out double rearU, out double frontU, out double freeM)
    {
        rearU = frontU = freeM = double.NaN;
        if (Claim(model, centreU) is not (double r, double f, double fr))
            return false;
        (rearU, frontU, freeM) = (r, f, fr);
        return true;
    }

    [Fact]
    public void OutsideEveryWindow_ACarClaimsNothing_Tier1WouldStillNameABay()
    {
        // pm1, measured: stopped at u=133.0 in the driving lane.
        Assert.False(Claimed("mercedes_c220", 133.0, out _, out _, out _));
        // The row does hold a bay for that car — which is exactly the fact a
        // claim must NOT stand on. PickKerbBay answers gaps; §1d answers
        // claims, and the difference is the position of the car.
        Assert.NotNull(BicycleNav.PickKerbBay(
            Config.ModelFor("mercedes_c220").LengthM,
            Row("fiat_500", 120.00, 6.90, 4.60, 5.40, 4.90)));
    }

    [Fact]
    public void BesideTheFirstMouth_TheSedanClaimsIt_ACarPastTheWindowDoesNot()
    {
        // The mouth is 121.57..128.47, the sedan's window 123.77..126.27.
        Assert.True(Claimed("mercedes_c220", 125.0, out var rear1,
            out var front1, out var free1));
        Assert.Equal(121.57, rear1, 6);
        Assert.Equal(128.47, front1, 6);
        Assert.Equal(2.50, free1, 6);
        Assert.False(Claimed("mercedes_c220", 126.5, out _, out _, out _));
        Assert.False(Claimed("mercedes_c220", 123.5, out _, out _, out _));
    }

    [Fact]
    public void FromBesideTheTightBay_TheSedanRefuses_TheFiatClaimsIt()
    {
        // Mouth 2 is 131.61..136.21: the sedan needs 5.40 m of it and has 4.60,
        // so no position beside it exists at all — the window would be negative,
        // not merely missed.
        for (var u = 131.0; u <= 137.01; u += 0.5)
            Assert.False(Claimed("mercedes_c220", u, out _, out _, out _));
        // The Fiat's window in the same mouth is 133.18..134.64, free 1.46.
        Assert.True(Claimed("fiat_500", 133.9, out var rearU, out var frontU,
            out var freeM));
        Assert.Equal(131.61, rearU, 6);
        Assert.Equal(136.21, frontU, 6);
        Assert.Equal(1.46, freeM, 6);
    }

    [Fact]
    public void AQueueAtTheRow_NamesEachMouthOnce()
    {
        // A lawful queue: three cars beside three different mouths, their bodies
        // apart. No mouth is named twice — the invariant the decision to keep
        // the claim on the car rather than in a list gives up, so it is checked
        // here instead of assumed downstream.
        var mouths = new List<double>();
        foreach (var (model, u) in new (string, double)[] {
                ("mercedes_c220", 125.0), ("fiat_500", 133.9),
                ("mercedes_c220", 142.05) })
        {
            Assert.True(Claimed(model, u, out var rearEnd, out var frontEnd,
                out var free));
            mouths.Add(rearEnd);
        }
        foreach (var mouth in new[] { 121.57, 131.61, 139.35 })
        {
            var named = 0;
            foreach (var r in mouths)
            {
                var d = r - mouth;
                if (d < 0) d = -d;
                if (d < 1e-6) named++;
            }
            Assert.Equal(1, named);
        }
    }
}
