using DrivingGame.Sim;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace DrivingGame.Sim.Tests;

/// <summary>
/// The MODEL table (issue #18): a car is uid + color + model, and a model carries
/// max_speed, acceleration, brake_rate and the footprint.
///
/// Option 1 was chosen by the owner: ONE MODEL PER PHOTO, seven models named for
/// what they are. That choice exists so the vocabulary can change without the
/// physics changing — the old tables said `blue` is a 1.80×4.40 "car" class and
/// `silver` a 1.71×3.14 "compact"; the new table says the same numbers about a
/// Mercedes C220d and a Fiat 500. These tests are the seam: every value here was
/// read off the tables that were deleted, so a later edit that retunes a car has
/// to break one of them on purpose.
/// </summary>
public class ModelTableTests
{
    [Theory]
    //  name            photo      max    accel  brake  lat   W      L
    [InlineData("mercedes_c220", "blue",    55.6, 2.8, 10.0, 4.5, 1.80, 4.40)]
    [InlineData("fiat_500",      "silver",  50.0, 2.5, 10.0, 4.5, 1.71, 3.14)]
    [InlineData("bmw_police",    "police",  55.6, 2.8, 10.0, 4.5, 1.86, 4.71)]
    [InlineData("vw_passat",     "tan",     55.6, 2.8, 10.0, 4.5, 1.86, 4.75)]
    [InlineData("tractor_unit",  "tractor", 22.2, 1.3, 10.0, 3.5, 2.85, 7.00)]
    [InlineData("ford_f150",     "pickup",  50.0, 2.4, 10.0, 4.0, 2.09, 5.25)]
    [InlineData("mixer_truck",   "mixer",   22.2, 1.0, 10.0, 3.0, 2.80, 8.40)]
    public void Model_TranscribesTheNumbersTheOldTablesHeld(
        string name, string photo, double max, double accel, double brake,
        double lat, double w, double l)
    {
        var m = Config.ModelFor(name);
        Assert.Equal(name, m.Name);
        Assert.Equal(photo, m.Photo);                    // which picture shows it
        Assert.Equal(max, m.MaxSpeedMps, 3);
        Assert.Equal(accel, m.AccelMps2, 3);
        Assert.Equal(brake, m.BrakeMps2, 3);
        Assert.Equal(lat, m.LatAccelMps2, 3);
        Assert.Equal(w, m.WidthM, 3);
        Assert.Equal(l, m.LengthM, 3);
    }

    [Theory]
    [InlineData("blue", "mercedes_c220")]      [InlineData("silver", "fiat_500")]
    [InlineData("police", "bmw_police")]       [InlineData("tan", "vw_passat")]
    [InlineData("tractor", "tractor_unit")]    [InlineData("pickup", "ford_f150")]
    [InlineData("mixer", "mixer_truck")]
    public void PhotoWords_StillResolve(string photo, string model)
    {
        // Every scenario, cockpit script and recorded tape in existence names the
        // vehicle with a colour word. They are not model names — but they stay
        // legal, resolved in one place, so no caller has to know which era a
        // string came from, and an old tape still reviews with the right car.
        Assert.Equal(model, Config.ModelFor(photo).Name);
        Assert.True(Config.IsModel(photo));
        Assert.Equal(Config.ModelFor(model).LengthM, Config.ModelFor(photo).LengthM, 6);
    }

    [Fact]
    public void UnknownWord_FallsBackToTheSedan_AsTheClassMapDid()
    {
        var m = Config.ModelFor("something_nobody_named");
        Assert.Equal("mercedes_c220", m.Name);
        Assert.Equal(Config.CAR_WIDTH_M, m.WidthM, 6);      // the old SizeForVehicle
        Assert.Equal(Config.CAR_LENGTH_M, m.LengthM, 6);    // fallback was this box
        Assert.False(Config.IsModel("something_nobody_named"));
        Assert.False(Config.IsModel(null));
        Assert.False(Config.IsModel(""));
    }

    [Fact]
    public void RoundRobin_HandsOutTheOldOrder_SoNoCastRestyles()
    {
        // Car N has always got the N-th entry. Shuffling the array would restyle
        // every existing scenario AND move which car is the heavy one in a
        // cap-key/collision test — a silent change to what a green test means.
        Assert.Equal(new[] { "mercedes_c220", "fiat_500", "bmw_police", "vw_passat",
                             "tractor_unit", "ford_f150", "mixer_truck" }, Config.MODEL_NAMES);
        Assert.Equal(new[] { "blue", "silver", "police", "tan", "tractor", "pickup", "mixer" },
                     Config.MODEL_NAMES.Select(n => Config.ModelFor(n).Photo));
    }

    [Fact]
    public void BrakeRate_IsTheOneLegacyConstant_ForEveryModel_Today()
    {
        // brake_rate is a MODEL property from now on, but no model brakes
        // differently yet: that is new physics (stopping distances move, and the
        // suite's guardrails are in sim time) and it belongs to its own change.
        foreach (var name in Config.MODEL_NAMES)
            Assert.Equal(Config.CAR_BRAKING, Config.ModelFor(name).BrakeMps2, 6);
    }

    [Fact]
    public void Car_ReportsItsModelBraking_NotAGlobal()
    {
        var car = new Car(100.0, 200.0, 90.0, 0, new BicycleDriver()) { Model = "mixer_truck" };
        Assert.Equal(Config.ModelFor("mixer_truck").BrakeMps2, car.BrakeMps2, 6);
        // And the footprint the collisions and the renderer both read:
        Assert.Equal(2.80, car.Size.WidthM, 3);
        Assert.Equal(8.40, car.Size.LengthM, 3);
        Assert.Equal(22.2, car.TopSpeedMps, 3);
    }
}

/// <summary>The two behaviours the rename touches in the engine: what a spawned
/// car STORES, and what still makes a car an emergency vehicle (R35). Driven
/// through the real command queue and one tick, the way CollisionTests does —
/// the spawn path is where a word becomes a car, so testing the resolver alone
/// would prove nothing about it.</summary>
public class ModelSpawnTests
{
    const double Dt = SimEngine.DtFixed;

    static SimEngine NewEngine() =>
        new SimEngine(TestMaps.BuildTestMap("basic"), new ObstacleManager("basic"), "basic");

    static Car Spawn(SimEngine engine, string model, bool add)
    {
        var before = new HashSet<int>(engine.Cars.Keys);
        engine.EnqueueCommand(new TeleportCommand("straight", null, 0.2, add, 0.0, model));
        engine.Tick(Dt);
        return engine.Cars.Values.First(c => !before.Contains(c.Uid));
    }

    [Fact]
    public void Spawn_CanonicalisesTheModel_WhicheverEraTheWordComesFrom()
    {
        var eng = NewEngine();
        // A request from the old vocabulary: `tan` is a PHOTO word, not a model.
        Assert.Equal("vw_passat", Spawn(eng, "tan", false).Model);   // car HAS a model (#18)
        // …and the new vocabulary arrives unchanged:
        Assert.Equal("ford_f150", Spawn(eng, "ford_f150", true).Model);
    }

    [Fact]
    public void EmergencyRole_FollowsTheModel_NotAColourWord()
    {
        var eng = NewEngine();
        var pol = Spawn(eng, "police", false);
        Assert.Equal("bmw_police", pol.Model);
        Assert.True(pol.EmergencyRole);          // R35: the police car has right of way
        // Another sedan, same class, no duty:
        var passat = Spawn(eng, "vw_passat", true);
        Assert.Equal("vw_passat", passat.Model);
        Assert.False(passat.EmergencyRole);
    }

    /// <summary>A role is DECLARED, not read off a picture (#18): R35's right of
    /// way used to follow from `color == "police"`. Null keeps the legacy inference
    /// so an existing cast is unaffected; either word overrides it.</summary>
    static Car SpawnWithRole(SimEngine eng, string model, string? role, bool add)
    {
        var before = new HashSet<int>(eng.Cars.Keys);
        eng.EnqueueCommand(new TeleportCommand("straight", null, 0.2, add, 0.0, model,
            Role: role));
        eng.Tick(SimEngine.DtFixed);
        return eng.Cars.Values.First(c => !before.Contains(c.Uid));
    }

    [Fact]
    public void Role_OverridesWhatTheModelWouldHaveInferred()
    {
        var eng = NewEngine();
        // Nothing declared: the legacy inference still puts a police car on duty.
        Assert.True(SpawnWithRole(eng, "bmw_police", null, false).EmergencyRole);
        // Declared off — an unmarked car in the same livery-less shape.
        Assert.False(SpawnWithRole(eng, "bmw_police", "civilian", true).EmergencyRole);
        // Declared on — a sedan with a blue light.
        Assert.True(SpawnWithRole(eng, "vw_passat", "emergency", true).EmergencyRole);
    }
}
