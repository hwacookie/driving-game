using DrivingGame.Sim;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace DrivingGame.Sim.Tests;

/// <summary>
/// A car parked IN the driving lane stops the driver behind it, and the stop distance
/// comes from the parked car's OWN model length (#22's rule, now stated with a car
/// instead of an Obstacle — issue #27 retires the obstacle entity).
///
/// These three facts used to live in `ObstacleContactTests`, placed there by
/// `ObstacleManager.Place()`. They are not about obstacles: they are about a body that
/// does not move sitting in a lane. The only thing an Obstacle added was a way to put
/// one there, which `/teleport` + `/park` do just as well — and those cars then appear
/// in /state, on the tape and in a replay, which an Obstacle never does (#27).
/// </summary>
public class ParkedBlockerContactTests : IDisposable
{
    const double Dt = SimEngine.DtFixed;
    /// <summary>Set by the last FrontReached() call: did either body ever register
    /// contact (CrashEpisodes grew)? Read by the verdicts below.</summary>
    static readonly bool[] ContactSeen = new bool[1];
    /// <summary>Where a normal spawn sits across this road (measured on `basic`:
    /// 19.4 m wide, driving lane centred at 5.25 m). Same value the sibling test file
    /// uses for "the driver really is in the lane".</summary>
    const double DrivingLaneM = 5.25;

    public void Dispose()
    {
        // Nothing to clean up beyond the engine's own temp dirs; kept for symmetry
        // with the other tape-touching fixtures.
    }

    /// <summary>Spawn a car on the straight, park a car of the given model 40 m AHEAD
    /// of it in its own lane, and drive. Returns how far the driver's FRONT reached,
    /// in metres along the direction it actually travels.</summary>
    static double FrontReached(string model, double startProgress = 0.05)
    {
        var net = TestMaps.BuildTestMap("basic");
        var eng = new SimEngine(net, "basic");

        // Both cars are placed with an explicit LATERAL offset, the way
        // OffLaneParkedCarTests does it: a car spawned without one lands on the
        // segment's reference line, and since #32 a parked car is swept along its own
        // body rather than that line - so "on the ref line" is not "in the driving
        // lane", and the driver simply steers around it (measured: the gap closed to
        // 5.8 m and then grew back to 26 m as the car passed). DrivingLaneM is where a
        // normal spawn sits on this road.
        int segIdx = net.SegmentByName["straight_n→straight_m"];
        eng.EnqueueCommand(new TeleportCommand(null, segIdx, startProgress, false, 15.0,
            Model: "fiat_500", Name: "driver", LatOffsetM: DrivingLaneM));
        eng.Tick(Dt);                                   // the spawn applies on a tick
        var car = eng.Cars.Values.Single();
        double x0 = car.X, y0 = car.Y;

        // Direction of travel, measured — not derived from a heading convention.
        eng.Tick(Dt);
        var moved = eng.Cars.First().Value;
        double dx = moved.X - x0, dy = moved.Y - y0;
        double len = Math.Sqrt(dx * dx + dy * dy);
        Assert.True(len > 1e-6, "the spawned car did not move — nothing to measure");
        double ux = dx / len, uy = dy / len;

        // The blocker: a CAR, parked IN the driver's lane, at a known distance ahead.
        // Progress on this segment is measured from its own start node, while the
        // driver was itself spawned at `startProgress` - so the two must be added or
        // the gap is not what the verdict says it is (measured: asking for 40 m and
        // getting 32 m, because progress 0.05 on a 150 m segment is already 7.5 m).
        var seg = net.Segments[segIdx];
        Assert.Equal(segIdx, car.SegIdx);   // the driver really is where we put it
        double targetProgress = startProgress + AlongM / seg.Length;
        var before = new System.Collections.Generic.HashSet<int>(eng.Cars.Keys);
        eng.EnqueueCommand(new TeleportCommand(null, car.SegIdx, targetProgress, false, 0.0,
            model, LatOffsetM: DrivingLaneM));
        eng.Tick(Dt);
        var blocker = eng.Cars.Values.Single(c => !before.Contains(c.Uid));
        eng.EnqueueCommand(new ParkCommand(blocker.Uid));
        eng.Tick(Dt);
        Assert.Equal(CarState.Parked, blocker.State);
        // The blocker must really sit on the driver's path, not beside it: same
        // segment, no lateral offset, so its body is in the driving lane.
        Assert.Equal(car.SegIdx, blocker.SegIdx);
        // Same side of the road, within a car width - otherwise this test measures a
        // pass, not a stop. On this north-south straight, x is the cross-road axis.
        double acrossM = blocker.X - car.X;
        Assert.True(Math.Abs(acrossM) < 1.0,
            $"the blocker sits {acrossM:F2} m across from the driver (x is the cross-road " +
            "axis on this north-south straight) - it must be IN the driving lane for a " +
            "stop to be expected");

        double furthest = 0.0;
        bool everTouched = false;
        for (int i = 0; i < 60 * 25; i++)
        {
            eng.Tick(Dt);
            var c = eng.Cars.First().Value;
            double a = (c.X - x0) * ux + (c.Y - y0) * uy;
            furthest = Math.Max(furthest, a + c.LengthM / 2.0);
            // Contact is the only thing that is FORBIDDEN here. docs/DRIVING_MANEUVERS.md
            // ("During the manoeuvre"): "The body box may come to 0.05 m of a neighbour's
            // box and may pass between them. It may not intersect them." Measured on this
            // scene: the driver steers AROUND the parked car (x moves 94.8 -> 100.1 across
            // it) and only then eases to rest - a lane change past a stationary body is
            // legal driving, so "stopped in front of it" is NOT what this can assert.
            if (c.CrashEpisodes > 0 || blocker.CrashEpisodes > 0) everTouched = true;
            if (c.Speed < 0.05 && i > 90) break;        // at rest: the run is over
        }
        ContactSeen[0] = everTouched;
        return furthest;
    }

    /// <summary>Where the parked car's near face sits, along the same axis: its centre
    /// is `along` m ahead of where the driver started, half its model length back
    /// toward the driver. Both numbers come from the same constant the staging uses.</summary>
    const double AlongM = 40.0;
    static double FaceAt(string model) =>
        AlongM - Config.ModelFor(model).LengthM / 2.0;

    // What these three assert is NOT the obstacle path's number. `ApplyContactStop`
    // was a geometric clamp: it bisected the step and pinned the body against the
    // obstruction, so a car came to rest AT its face (0 m gap). A parked CAR is handled
    // by the driver's own braking model instead (CarCollisions), which stops with a
    // standstill margin - CollisionTests pins that at 1.5..6.0 m behind a stationary
    // lead, deliberately widened in commit bcd1f22 ("stop-margin caps"). So the honest
    // statement here is "the driver is stopped by the body and never reaches it", plus
    // the fact these tests exist for: the stop distance FOLLOWS the blocker's model
    // length. The absolute face-to-bumper match the obstacle path gave is gone with it,
    // and nothing in this repo needs it (nothing placed obstacles).

    [Fact]
    public void AParkedCarInTheDrivingLaneIsNeverDrivenThrough()
    {
        // The fact that survives the entity going away: a body that does not move in
        // my lane is real. It may be passed (legal, docs above) but never intersected,
        // and it must actually change how the driver behaves - both are checked, the
        // second because a blocker nobody reacts to is the phantom-obstacle failure in
        // reverse. The obstacle path used to pin the body against the obstruction at
        // 0 m; the car path owns this now, through CarCollisions' contact rule.
        double front = FrontReached("mercedes_c220");
        Assert.False(ContactSeen[0],
            "the driver contacted a car parked in its own lane ahead (CrashEpisodes grew)");
        Assert.True(front > 30.0,
            $"the driver only reached {front:F2} m toward a blocker 40 m away - it reacted " +
            "to nothing; a parked car must be visible to the driving model");
    }

    [Fact]
    public void ALongerParkedCarStopsTheDriverEarlier_ByItsModelLength()
    {
        double sedanFront = FrontReached("mercedes_c220");      // 4.40 m box
        double mixerFront = FrontReached("mixer_truck");        // 8.40 m box
        // Same CENTRE, so the mixer's near face sits (8.40-4.40)/2 = 2.00 m further
        // back along the road. The driver must therefore rest no later than that - and
        // it may rest earlier, because its own stopping margin grows with the box it is
        // avoiding. What must NOT happen is the mixer being treated as a sedan: then the
        // two fronts would agree within the model-length difference.
        double faceShift = (Config.ModelFor("mixer_truck").LengthM
                            - Config.ModelFor("mercedes_c220").LengthM) / 2.0;   // 2.00
        double delta = sedanFront - mixerFront;
        Assert.True(delta >= faceShift - 0.4,
            $"the sedan stopped the driver at {sedanFront:F2} m and the mixer at " +
            $"{mixerFront:F2} m (delta {delta:F2} m): the mixer's extra {faceShift:F2} m of " +
            "body is not coming back as an earlier stop - its length is not being read");
    }

    [Fact]
    public void ASmallParkedCarDoesNotOwnMoreRoadThanItHas()
    {
        // The other half of #22: before it, every stationary body was the sedan box, so
        // a Fiat 500 (3.14 m) blocked 4.40 m of road and a driver braked for metres of
        // tarmac that were free. Measured as a DIFFERENCE against the sedan case: the
        // Fiat's near face is (4.40-3.14)/2 = 0.63 m further along, so the driver must
        // get at least that much further before it rests.
        double fiatFront = FrontReached("fiat_500");
        double sedanFront = FrontReached("mercedes_c220");
        double expected = (Config.ModelFor("mercedes_c220").LengthM
                           - Config.ModelFor("fiat_500").LengthM) / 2.0;      // 0.63
        double delta = fiatFront - sedanFront;
        Assert.True(delta > expected - 0.4,
            $"the driver reached {fiatFront:F2} m against a 3.14 m parked car and " +
            $"{sedanFront:F2} m against a 4.40 m one (delta {delta:F2} m, expected at least "
            + $"~{expected:F2} m) - the small parked car still owns the sedan's road");
    }
}
