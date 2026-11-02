using DrivingGame.Sim;
using System.Collections.Generic;
using Xunit;

namespace DrivingGame.Sim.Tests;

/// <summary>
/// §1c tier 1 (docs/DRIVING_MANEUVERS.md, owner decision 2026-11-02): a gap
/// between parked cars is a bay FOR THIS CAR only when the free space along the
/// road — the gap minus this car's own length — reaches 1.00 m, and the row is
/// scanned in driving order, taking the first gap that fits.
///
/// The numbers are the row the owner designed: five parked cars leaving bays of
/// 6.90, 4.60, 5.40 and 4.90 m (the sedan's 4.40 m plus slack 2.5 / 0.2 / 1.0
/// / 0.5 m). The point of the rule is that the answer moves with the vehicle:
/// the Fiat takes the 4.60 m bay the sedan had to refuse, and a row whose
/// expected layout does not change with the model is not testing this.
///
/// This is the SELECTION half only. The shuffle through a tight bay (§1b's
/// execution) is deliberately not built yet, and none of this touches it.
/// </summary>
public class BayChoiceTests
{
    static List<(double CentreU, double LengthM)> RowWith(string model,
        params double[] gapsBetween)
    {
        double len = Config.ModelFor(model).LengthM;
        var l = new List<(double CentreU, double LengthM)>();
        double c = 0.0;
        l.Add((c, len));
        foreach (var g in gapsBetween)
        {
            c += len / 2.0 + g + len / 2.0;
            l.Add((c, len));
        }
        return l;
    }

    // A car already standing inside a bay becomes a flank: centred, so it splits
    // the bay into the leftovers of its own free space.
    static List<(double CentreU, double LengthM)> WithOccupant(
        List<(double CentreU, double LengthM)> flanks, string model,
        double rearEndM, double frontEndM)
    {
        double len = Config.ModelFor(model).LengthM;
        double centre = (rearEndM + frontEndM) / 2.0;   // the placement rule: centred
        var l = new List<(double CentreU, double LengthM)>(flanks);
        l.Add((centre, len));
        return l;
    }

    // The bay as three plain doubles: a nullable tuple keeps its member names
    // out of reach of the compiler, and the values below are what the rule is
    // stated in (free space along the road, plus the bay's two ends).
    private static bool Pick(string model, List<(double CentreU, double LengthM)> flanks,
        out double rearU, out double frontU, out double freeM)
    {
        rearU = frontU = freeM = double.NaN;
        var bay = BicycleNav.PickKerbBay(Config.ModelFor(model).LengthM, flanks);
        if (bay is not (double r, double f, double fr)) return false;
        (rearU, frontU, freeM) = (r, f, fr);
        return true;
    }

    [Fact]
    public void FirstBayThatFits_IsTaken()
    {
        var row = RowWith("fiat_500", 6.90, 4.60, 5.40, 4.90);
        Assert.True(Pick("mercedes_c220", row, out _, out _, out var free));
        Assert.Equal(2.50, free, 6);
    }

    [Fact]
    public void TwentyCentimetresFree_TheSedanRefusesIt()
    {
        var row = RowWith("fiat_500", 4.60);
        Assert.False(Pick("mercedes_c220", row, out _, out _, out _));
    }

    [Fact]
    public void SameBayTheFiatTakes_TheSedanRefusedIt()
    {
        var row = RowWith("fiat_500", 4.60);
        Assert.True(Pick("fiat_500", row, out _, out _, out var free));
        Assert.Equal(1.46, free, 6);
    }

    [Fact]
    public void ExactlyOnTheRule_IsAccepted()
    {
        // 5.40 m bay, 4.40 m body: free = 1.00, exactly PARK_BAY_MIN_FREE_M.
        // Without the ε this is the one case a float comparison refuses.
        Assert.True(Pick("mercedes_c220", RowWith("fiat_500", 5.40),
            out _, out _, out var free));
        Assert.Equal(1.00, free, 6);
    }

    [Fact]
    public void FiftyCentimetresFree_TheSedanRefusesIt()
    {
        Assert.False(Pick("mercedes_c220", RowWith("fiat_500", 4.90),
            out _, out _, out _));
    }

    [Fact]
    public void TheRowFillsIn_DrivingOrder()
    {
        var row = RowWith("fiat_500", 6.90, 4.60, 5.40, 4.90);

        // pm1 (sedan): the 6.90 m bay.
        Assert.True(Pick("mercedes_c220", row, out var rear1, out var front1,
            out var free1));
        Assert.Equal(2.50, free1, 6);
        row = WithOccupant(row, "mercedes_c220", rear1, front1);

        // pm2 (sedan): 4.60 m refused (0.20 free), so the 5.40 m bay — the rule's
        // edge, accepted.
        Assert.True(Pick("mercedes_c220", row, out var rear2, out var front2,
            out var free2));
        Assert.Equal(1.00, free2, 6);
        row = WithOccupant(row, "mercedes_c220", rear2, front2);

        // pm3 (Fiat): the 4.60 m bay the sedans had to refuse, 1.46 m free.
        Assert.True(Pick("fiat_500", row, out var rear3, out var front3,
            out var free3));
        Assert.Equal(1.46, free3, 6);
        row = WithOccupant(row, "fiat_500", rear3, front3);

        // pm4 (sedan): 4.90 m refused (0.50 free), nothing else left — no bay.
        Assert.False(Pick("mercedes_c220", row, out _, out _, out _));
    }
}
