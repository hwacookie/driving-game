using DrivingGame.Sim;
using System.Linq;
using Xunit;

namespace DrivingGame.Sim.Tests;

/// <summary>
/// The decision log records CHANGES of reason, not ticks that mention a reason
/// (docs/CAR_DETAIL_WINDOW.md's event list is what this is read for).
///
/// Measured in `hairpin_exit` on tape_T_249: one car logged 269 decisions and
/// 268 of them were the identical line `[R1] off-road at (579, 1128)` — the
/// validator re-reports every substep at 60 Hz while the car sits in the same
/// metre of grass, and the list an operator reads became a wall of duplicates.
/// </summary>
public class DecisionLogTests
{
    static Car NewCar(long step)
    {
        var c = new Car(0, 0, 0, 0, new BicycleDriver()) { SimStep = step };
        return c;
    }

    [Fact]
    public void SameTextTwice_KeepsTheFirstEntry_AndItsStep()
    {
        var c = NewCar(100);
        c.RecordDecision(100 / 60.0, "[R1] off-road at (579, 1128)");
        c.SimStep = 101;
        c.RecordDecision(101 / 60.0, "[R1] off-road at (579, 1128)");

        var log = c.DecisionsSnapshot();
        Assert.Single(log);
        Assert.Equal(100, log[0].Step);            // the step the situation BEGAN
        Assert.Equal(100, c.LastDecisionStep);     // and the tape is not told twice
    }

    [Fact]
    public void DifferentText_IsANewDecision()
    {
        var c = NewCar(10);
        c.RecordDecision(10 / 60.0, "brake: [R7] car 5 ahead (gap 30.3 m)");
        c.SimStep = 40;
        c.RecordDecision(40 / 60.0, "brake: [R7] car 5 ahead (gap 12.1 m)");
        Assert.Equal(2, c.DecisionsSnapshot().Count);   // a changed gap IS new
    }

    [Fact]
    public void AlternatingReasons_StillLogEveryTransition()
    {
        // Only a REPEAT of the newest entry is a duplicate: brake / release /
        // brake is a real story, and the flutter regression test counts on it.
        var c = NewCar(1);
        c.RecordDecision(0.016, "brake: [R2] car 4 stopped in my path");
        c.RecordDecision(0.35, "gap clear - resuming");
        c.RecordDecision(0.70, "brake: [R2] car 4 stopped in my path");
        Assert.Equal(3, c.DecisionsSnapshot().Count);
        // The first word of each line, colon and all — the point is the
        // alternation, three entries where a naive "have I said this before?"
        // rule would have kept two.
        Assert.Equal(new[] { "brake:", "gap", "brake:" },
                     c.DecisionsSnapshot().Select(d => d.Msg.Split(' ')[0]).ToArray());
    }
}
