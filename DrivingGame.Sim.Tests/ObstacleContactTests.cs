using DrivingGame.Sim;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace DrivingGame.Sim.Tests;

/// <summary>
/// Does a parked obstruction actually stop a DRIVING car — and does the face it
/// stops it at come from the obstruction's MODEL (#22)?
///
/// Written as a headless engine test because the question must have one answer.
/// Two earlier attempts of mine were wrong in ways the code could not show: a REST
/// measurement asserted on `y &gt; obstacleY + 3` while this car travels toward
/// DECREASING y (so it "passed through" on tick 0, having met nothing), and the
/// geometry helper alone has its own coverage, which proves nothing about the tick
/// loop. Here the direction of travel is MEASURED from the first tick, and the
/// only claim left is the one #22 is about: how many metres of road the parked
/// vehicle owns.
/// </summary>
public class ObstacleContactTests : IDisposable
{
    const double Dt = SimEngine.DtFixed;
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "obs_contact_" + Guid.NewGuid().ToString("N"));

    public ObstacleContactTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    /// <summary>Spawn a car on the straight, park an obstruction of the given
    /// model 40 m AHEAD of it in its own lane, and drive. Returns how far the
    /// car's FRONT reached, in metres along the direction it actually travels.</summary>
    static double FrontReached(string model, double startProgress = 0.05)
    {
        var net = TestMaps.BuildTestMap("basic");
        var dir = Path.Combine(Path.GetTempPath(), "obs_contact_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var mgr = new ObstacleManager("basic", dir);
        var eng = new SimEngine(net, mgr, "basic");

        eng.EnqueueCommand(new TeleportCommand("straight", null, startProgress, false, 15.0, "fiat_500"));
        eng.Tick(Dt);                                   // the spawn applies on a tick
        var car = eng.Cars.Values.Single();
        double x0 = car.X, y0 = car.Y;

        // Direction of travel, measured — not derived from a heading convention.
        eng.Tick(Dt);
        var moved = eng.Cars.Values.First();
        double dx = moved.X - x0, dy = moved.Y - y0;
        double len = Math.Sqrt(dx * dx + dy * dy);
        Assert.True(len > 1e-6, "the spawned car did not move — nothing to measure");
        double ux = dx / len, uy = dy / len;

        const double along = 40.0;
        var ob = mgr.Place(net, "car", "blue", x0 + ux * along, y0 + uy * along, model);

        double furthest = 0.0;
        for (int i = 0; i < 60 * 25; i++)
        {
            eng.Tick(Dt);
            var c = eng.Cars.First().Value;
            double a = (c.X - x0) * ux + (c.Y - y0) * uy;
            // `a` is the REAR-AXLE-ish reference position; the front is half a
            // body length further along.
            furthest = Math.Max(furthest, a + c.LengthM / 2.0);
            if (c.Speed < 0.05 && i > 90) break;        // at rest: the run is over
        }
        return furthest;
    }

    /// <summary>Where the obstruction's near face sits, along the same axis:
    /// centre at 40 m, half its model length back toward the car.</summary>
    static double FaceAt(string model) =>
        40.0 - Config.ModelFor(model).LengthM / 2.0;

    [Fact]
    public void ADrivingCarIsStoppedByAParkedObstructionInItsLane()
    {
        double front = FrontReached("mercedes_c220");
        double face = FaceAt("mercedes_c220");
        Assert.True(front <= face + 0.6,
            $"the car's front reached {front:F2} m of 40 — the obstruction's face is at " +
            $"{face:F2} m, so a parked car did not stop it");
        Assert.True(front > face - 4.0,
            $"the car stopped {face - front:F2} m short of a parked car — it should drive " +
            "up to it, not brake at a distance (a phantom obstruction)");
    }

    [Fact]
    public void ALongerObstructionStopsTheCarEarlier_ByItsModelLength()
    {
        double sedanFront = FrontReached("mercedes_c220");      // 4.40 m box
        double mixerFront = FrontReached("mixer_truck");        // 8.40 m box
        // Same centre, so the mixer's near face sits 2.0 m further back along the
        // road — and the car must meet it 2.0 m sooner.
        double expected = (Config.ModelFor("mixer_truck").LengthM
                           - Config.ModelFor("mercedes_c220").LengthM) / 2.0;   // 2.00
        double delta = sedanFront - mixerFront;
        Assert.True(Math.Abs(delta - expected) < 0.8,
            $"the sedan stopped the car at {sedanFront:F2} m and the mixer at {mixerFront:F2} m " +
            $"(delta {delta:F2} m, expected {expected:F2} m from the model lengths) — " +
            "the obstruction's length is not coming from its model");
    }

    [Fact]
    public void ASmallObstructionDoesNotOwnMoreRoadThanItHas()
    {
        // The other half of #22: before it, EVERY obstruction was the sedan box,
        // so a Fiat 500 (3.14 m) blocked 4.40 m of road and a car braked for
        // metres of tarmac that were free.
        //
        // Measured as a DIFFERENCE, not against the face: a driver's own stopping
        // margin sits between the two, and an absolute front position cannot tell
        // "stopped for a smaller box" from "stopped early anyway". The Fiat's near
        // face is (4.40 - 3.14)/2 = 0.63 m further along the road than the
        // sedan's, so the car must get ~0.63 m further along before it rests.
        double fiatFront = FrontReached("fiat_500");
        double sedanFront = FrontReached("mercedes_c220");
        double expected = (Config.ModelFor("mercedes_c220").LengthM
                           - Config.ModelFor("fiat_500").LengthM) / 2.0;      // 0.63
        double delta = fiatFront - sedanFront;
        Assert.True(delta > expected - 0.4,
            $"the car reached {fiatFront:F2} m against a 3.14 m obstruction and " +
            $"{sedanFront:F2} m against a 4.40 m one (delta {delta:F2} m, expected " +
            $"~{expected:F2} m) — the small parked car still owns the sedan's road");
    }
}
