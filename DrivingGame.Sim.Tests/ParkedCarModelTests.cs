using DrivingGame.Sim;
using System;
using System.Linq;
using Xunit;

namespace DrivingGame.Sim.Tests;

/// <summary>
/// A parked car gets a MODEL, and that model decides how many metres of road it owns.
///
/// This is the parked-car form of `ObstacleModelTests` (#27 retires the obstacle
/// entity). The facts worth keeping are about BODIES, not about obstacles:
///   - the footprint comes from the vehicle's own model table, per class;
///   - a long vehicle owns correspondingly more road (before #22 everything was the
///     sedan box, so a parked mixer — 8.40 m — blocked 4.40 m);
///   - a photo alias ("mixer") and a canonical name ("mixer_truck") park the same truck,
///     because Config.ModelFor is the one crossing point;
///   - an unknown model is refused instead of quietly parking a sedan.
/// What is NOT ported, deliberately: `Obstacle.FromJson`'s legacy-entry default and the
/// layout-file round trip. Both described a file format nothing in this repo writes or
/// reads (data/obstacles/ does not exist, no REST endpoint, no UI), and it goes with the
/// entity.
/// </summary>
public class ParkedCarModelTests
{
    /// <summary>A parked car of the given model, read back from the sim itself.</summary>
    static Car ParkedModel(string model)
    {
        var net = TestMaps.BuildTestMap("basic");
        var eng = new SimEngine(net, "basic");
        int seg = net.SegmentByName["straight_n→straight_m"];
        eng.EnqueueCommand(new TeleportCommand(null, seg, 0.30, false, 0.0, model));
        eng.Tick(SimEngine.DtFixed);
        var car = eng.Cars.Values.Single();
        eng.EnqueueCommand(new ParkCommand(car.Uid));
        eng.Tick(SimEngine.DtFixed);
        Assert.Equal(CarState.Parked, car.State);
        return car;
    }

    [Theory]
    [InlineData("mercedes_c220", 4.40, 1.80)]     // what every obstacle layout always was
    [InlineData("fiat_500", 3.14, 1.71)]
    [InlineData("bmw_police", 4.71, 1.86)]
    [InlineData("vw_passat", 4.75, 1.86)]
    [InlineData("tractor_unit", 7.00, 2.85)]
    [InlineData("ford_f150", 5.25, 2.09)]
    [InlineData("mixer_truck", 8.40, 2.80)]
    public void Footprint_IsTheModelsMetres(string model, double length, double width)
    {
        var car = ParkedModel(model);
        Assert.Equal(length, car.LengthM, 3);
        Assert.Equal(width, car.WidthM, 3);
    }

    [Fact]
    public void ALongParkedVehicleOwnsItsRoad_NotTheSedanBox()
    {
        // The point of #22, stated as one number: the parked mixer owns nearly twice
        // the road the code used to give every stationary body.
        var sedan = ParkedModel("mercedes_c220");
        var mixer = ParkedModel("mixer_truck");
        Assert.True(mixer.LengthM > 1.8 * sedan.LengthM,
            $"expected the mixer to own far more road than the sedan box " +
            $"({mixer.LengthM:F2} vs {sedan.LengthM:F2})");
    }

    [Fact]
    public void PhotoAliases_ResolveHereToo_BecauseTheyResolveEverywhere()
    {
        // "mixer" is a photo word from the old vocabulary; Config.ModelFor is the one
        // crossing point, so a caller that speaks either era parks the same truck.
        var alias = ParkedModel("mixer");
        Assert.Equal("mixer_truck", alias.Model);
        Assert.Equal(8.40, alias.LengthM, 3);
    }

    [Fact]
    public void AnUnknownModelIsRefused_InsteadOfQuietlyParkingASedan()
    {
        // The obstacle path raised PlacementError from Place(). The car path answers
        // the same question one layer down, where every caller resolves a name:
        // Config.IsModel is what the API checks BEFORE spawning, so "bogus" is a 400
        // and never a silent sedan (Config.ModelFor does fall back to the sedan - that
        // is why the check has to happen at the door, and this pins that it exists).
        Assert.False(Config.IsModel("spaceship"));
        Assert.True(Config.IsModel("mixer"));           // the alias is accepted too
        Assert.True(Config.IsModel("mixer_truck"));     // and the canonical name
        // The fallback IS a sedan, so an unchecked name would park one silently -
        // which is exactly what IsModel at the door prevents.
        Assert.Equal("mercedes_c220", Config.ModelFor("spaceship").Name);
    }
}
