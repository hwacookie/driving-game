using DrivingGame.Sim;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace DrivingGame.Sim.Tests;

/// <summary>
/// A car has a STATE (#27): driving, parking, parked.
///
/// It exists because the sim was GUESSING. The cap rule that lights the brake
/// lamps reads "speed sits at a binding cap" as "the driver has their foot on the
/// pedal" — right for a car decelerating into a queue, absurd for a car that is
/// parked with the engine off. Reproduced on the live host before the fix: a
/// parked Fiat 500 at 0.0 km/h answered braking=True for 5 samples (cap=avoid#2)
/// while another car drove past it.
///
/// The second test is the one that keeps the fix honest: a car that IS being held
/// by its own approach cap must still light. Without it, "stop the lying" is one
/// keystroke away from "stop the lamp".
/// </summary>
public class CarStateTests
{
    const double Dt = SimEngine.DtFixed;

    static SimEngine Engine() =>
        new SimEngine(TestMaps.BuildTestMap("basic"), new ObstacleManager("basic"), "basic");

    static Car Spawn(SimEngine eng, string model, double progress, double speed, bool add)
    {
        var before = new HashSet<int>(eng.Cars.Keys);
        eng.EnqueueCommand(new TeleportCommand("straight", null, progress, add, speed, model));
        eng.Tick(Dt);
        return eng.Cars.Values.First(c => !before.Contains(c.Uid));
    }

    [Fact]
    public void ASpawnedCarIsDriving_AParkedCarIsParked()
    {
        var eng = Engine();
        var car = Spawn(eng, "fiat_500", 0.30, 0.0, false);
        Assert.Equal(CarState.Driving, car.State);      // stopped, but not PARKED
        eng.EnqueueCommand(new ParkCommand(car.Uid));
        eng.Tick(Dt);
        Assert.Equal(CarState.Parked, car.State);       // engine off, no route
    }

    [Fact]
    public void StoppedIsNotParked()
    {
        // The distinction the enum exists for: a car held at a yield line is
        // DRIVING and may show brake lights; a parked car may not. Both sit at
        // 0 km/h, so only the state tells them apart.
        var eng = Engine();
        var waiting = Spawn(eng, "mercedes_c220", 0.30, 0.0, false);
        Assert.Equal(CarState.Driving, waiting.State);
        Assert.Equal(0.0, waiting.Speed);
    }

    [Fact]
    public void AParkedCarDoesNotClaimToBeBraking_WhileAnotherCarPassesIt()
    {
        // The live repro, in process: park a car in the lane, drive one past it,
        // and watch the parked car the whole time.
        var eng = Engine();
        var parked = Spawn(eng, "fiat_500", 0.30, 0.0, false);
        eng.EnqueueCommand(new ParkCommand(parked.Uid));
        eng.Tick(Dt);
        var passer = Spawn(eng, "mercedes_c220", 0.05, 12.0, true);

        int litSamples = 0;
        for (int i = 0; i < 60 * 10; i++)
        {
            eng.Tick(Dt);
            var p = eng.Cars[parked.Uid];
            if (p.IsBraking) litSamples++;
            if (!eng.Cars.TryGetValue(passer.Uid, out var q) || q.Speed < 0.01) continue;
        }
        Assert.True(parked.IsBraking == false, "the parked car is braking at the end");
        Assert.Equal(0, litSamples);
    }

    [Fact]
    public void ACarHeldByItsOwnApproachCapStillLights()
    {
        // The behaviour the cap comment cites as measured (fig8_xing): a follower
        // clamped behind a stopped leader really does have a foot on the brake.
        // If this test goes dark, the fix stopped distinguishing and started
        // switching the lamp off.
        var eng = Engine();
        var lead = Spawn(eng, "mercedes_c220", 0.40, 0.0, false);
        eng.EnqueueCommand(new ParkCommand(lead.Uid));
        eng.Tick(Dt);
        var follower = Spawn(eng, "vw_passat", 0.08, 12.0, true);

        bool everLit = false;
        for (int i = 0; i < 60 * 14; i++)
        {
            eng.Tick(Dt);
            if (eng.Cars.TryGetValue(follower.Uid, out var f) && f.IsBraking) everLit = true;
        }
        Assert.True(everLit,
            "the follower never showed brake lights while being capped behind a " +
            "stopped car — the cap-to-brake-lamp rule has been switched off, not corrected");
    }
}
