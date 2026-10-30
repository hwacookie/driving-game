using DrivingGame.Sim;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace DrivingGame.Sim.Tests;

/// <summary>
/// A car parked OUTSIDE the driving lane must not stop the lane it stands beside
/// (issue #32).
///
/// Found by the parking-bay test (#31): with two cars parked in the parking lane
/// either side of a bay, the parking car braked to a standstill behind the rear
/// neighbour and stayed there — `cap=avoid#… heldBy=…` while the two bodies were
/// 1.35 m apart laterally and in different lanes.
///
/// The cause is not the corridor band, which is body-accurate on purpose
/// ("(my width + its width)/2 … the band must NOT be widened beyond the body sum").
/// It is the PREDICTION: `PredictPos` walks the car's reference line and returns the
/// point ON it, so a car that is standing 3.1 m off its line is swept as if it stood
/// on the line — a parked Fiat in the parking lane becomes, in the prediction, a
/// Fiat in the driving lane dead ahead. The sweep predicts the LANE, not the car.
/// </summary>
public class OffLaneParkedCarTests
{
    const double Dt = SimEngine.DtFixed;
    const string Avenue = "pkw_n→pkw_s";
    // Measured on this road (19.4 m = driving 0→7.0 m + parking 7.0→9.7 m):
    // 8.35 is the parking-lane centre, 5.25 where an ordinary spawn stands.
    const double ParkingLaneM = 8.35;
    const double DrivingLaneM = 5.25;

    static SimEngine Engine(out int segIdx)
    {
        var net = TestMaps.BuildTestMap("basic");
        segIdx = net.SegmentByName[Avenue];
        return new SimEngine(net, new ObstacleManager("basic"), "basic");
    }

    /// <summary>Park one car at a lateral offset, then send a driver down the
    /// driving lane behind it. Returns (driver, neighbour).</summary>
    static (Car driver, Car neighbour) Stage(SimEngine eng, int segIdx, double neighbourLatM)
    {
        var before = new HashSet<int>(eng.Cars.Keys);
        eng.EnqueueCommand(new TeleportCommand(null, segIdx, 0.50, false, 0.0,
            Model: "fiat_500", Name: "neighbour", LatOffsetM: neighbourLatM));
        eng.Tick(Dt);
        var neighbour = eng.Cars.Values.Single(c => !before.Contains(c.Uid));
        eng.EnqueueCommand(new ParkCommand(neighbour.Uid));
        eng.Tick(Dt);

        before = new HashSet<int>(eng.Cars.Keys);
        eng.EnqueueCommand(new TeleportCommand(null, segIdx, 0.40, true, 8.0,
            Model: "mercedes_c220", Name: "driver", LatOffsetM: DrivingLaneM));
        eng.Tick(Dt);
        var driver = eng.Cars.Values.Single(c => !before.Contains(c.Uid));
        return (driver, neighbour);
    }

    [Fact]
    public void AParkedCarStandsWhereItWasPut_NotOnItsOldReferenceLine()
    {
        // The precondition of the bug: the parked car's body is laterally off the
        // line its nav still points at. If this ever reads ~0, the scene below is
        // no longer testing what it says it tests.
        var eng = Engine(out var seg);
        var (driver, neighbour) = Stage(eng, seg, ParkingLaneM);
        Assert.Equal(CarState.Parked, neighbour.State);

        var nav = neighbour.BicycleNav;
        Assert.NotNull(nav?.Ref);
        var (lx, ly) = nav!.Ref!.PointAt(nav.S);
        double heading = Math.PI / 180.0 * nav.Ref.HeadingAt(nav.S);
        // right-hand normal, the convention the whole sim uses for a lateral offset
        double offM = (neighbour.X - lx) * Math.Cos(heading)
                    - (neighbour.Y - ly) * Math.Sin(heading);
        Assert.True(Math.Abs(offM) > 2.0,
            $"the parked car reads only {offM:F2} m off its reference line - the " +
            "scene is not the off-lane case this file exists for");
    }

    [Fact]
    public void ACarInTheDrivingLanePassesACarParkedInTheParkingLane()
    {
        var eng = Engine(out var seg);
        var (driver, neighbour) = Stage(eng, seg, ParkingLaneM);

        bool heldByNeighbour = false;
        string heldByCap = "";
        double minSpeed = driver.Speed;
        bool passed = false;
        for (int i = 0; i < 60 * 12; i++)
        {
            eng.Tick(Dt);
            if (driver.Speed < minSpeed) minSpeed = driver.Speed;
            if (driver.HeldByUid == neighbour.Uid)
            {
                heldByNeighbour = true;
                if (string.IsNullOrEmpty(heldByCap)) heldByCap = driver.ActiveCapKey ?? "";
            }
            // The avenue runs south; "passed" = its nose is beyond the neighbour.
            if (driver.Y < neighbour.Y - 6.0) { passed = true; break; }
        }

        Assert.False(heldByNeighbour,
            $"the driver was capped by a car parked 3.1 m beside its lane " +
            $"(cap {heldByCap}, min speed {minSpeed:F1} m/s, passed={passed}): the " +
            "prediction is sweeping the neighbour's REFERENCE LINE instead of the " +
            "body that is actually standing there");
        Assert.True(passed,
            $"the driver never got past the parked car (min speed {minSpeed:F1} m/s) " +
            "— it braked for a body that is not in its path");
    }

    [Fact]
    public void ACarParkedInTheDrivingLaneStillStopsTheDriver()
    {
        // The guard against over-fixing: same scene, same code, but the neighbour
        // really occupies the lane. That is `park_block_overtake`, and a driver must
        // still brake for it — if this test goes bright, the fix stopped speaking
        // for the lane instead of for the body.
        var eng = Engine(out var seg);
        var (driver, neighbour) = Stage(eng, seg, DrivingLaneM);

        bool heldByNeighbour = false;
        for (int i = 0; i < 60 * 12; i++)
        {
            eng.Tick(Dt);
            if (driver.HeldByUid == neighbour.Uid) { heldByNeighbour = true; break; }
        }
        Assert.True(heldByNeighbour,
            "a car parked IN the driving lane no longer caps the driver behind it — " +
            "the fix threw out real obstruction avoidance with the phantom one");
    }
}
