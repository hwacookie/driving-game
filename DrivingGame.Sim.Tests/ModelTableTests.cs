using DrivingGame.Sim;
using System;
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
    [InlineData("tractor_unit",  "tractor", 22.2, 1.3,  6.0, 3.5, 2.85, 7.00)]
    [InlineData("ford_f150",     "pickup",  50.0, 2.4, 10.0, 4.0, 2.09, 5.25)]
    [InlineData("mixer_truck",   "mixer",   22.2, 1.0,  6.8, 3.0, 2.80, 8.40)]
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
    public void BrakeRate_PassengersAtTheAnchor_HeavyVehiclesBelowIt()
    {
        // #21 replaced "one rate for all seven" with cited figures. The band each
        // model sits in is the contract, so a retune has to break this on purpose:
        //   passengers + pickup  10.0  (ADAC measured 10.6/10.5 for Golf/X3 at
        //                               80→0 km/h laden, dry — kept at the lower
        //                               in-service anchor ADAC itself asks for;
        //                               a 2021 F-150 measured 10.9 from 70 mph,
        //                               so the pickup gets no differential)
        //   mixer_truck           6.8  (ADAC: DAF XF 460 FT, 40 t laden, 36.2 m)
        //   tractor_unit          6.0  (estimate inside R13's 5.0 floor and
        //                               CLEPA's 7.0 light-truck maximum)
        foreach (var name in new[] { "mercedes_c220", "fiat_500", "bmw_police",
                                     "vw_passat", "ford_f150" })
            Assert.Equal(Config.CAR_BRAKING, Config.ModelFor(name).BrakeMps2, 6);
        Assert.Equal(6.8, Config.ModelFor("mixer_truck").BrakeMps2, 6);
        Assert.Equal(6.0, Config.ModelFor("tractor_unit").BrakeMps2, 6);
        // …and the heavy pair is actually BELOW the anchor, which is the point:
        Assert.True(Config.ModelFor("mixer_truck").BrakeMps2 < Config.CAR_BRAKING - 2.0);
        Assert.True(Config.ModelFor("tractor_unit").BrakeMps2 >= 5.0,   // R13 floor
                    "below the UNECE R13 minimum for a commercial vehicle");
    }

    [Fact]
    public void StoppingDistance_FollowsTheModel_SoAMixerNeedsMoreRoadThanACityCar()
    {
        // v²/(2a) is arithmetic; what needs proving is that the ARITHMETIC IS THE
        // SIM's — that a braking car actually covers the metres its model implies,
        // through Car.Update and the collision caps, not just in the table.
        // 15 m/s (54 km/h): 11.25 m at 10.0, 16.5 m at 6.8.
        double fiat = MetersToStop("fiat_500"), mixer = MetersToStop("mixer_truck");
        Assert.True(fiat > 10.5 && fiat < 12.5,
            $"the Fiat stopped from 54 km/h in {fiat:F1} m (expected ~11.3 m at 10 m/s²)");
        Assert.True(mixer > 15.0 && mixer < 18.0,
            $"the mixer stopped from 54 km/h in {mixer:F1} m (expected ~16.5 m at 6.8 m/s²)");
        Assert.True(mixer - fiat > 4.0,
            $"the mixer needed only {(mixer - fiat):F1} m more than the Fiat — the " +
            "heavy vehicle's weaker brakes are not reaching the motion");
    }

    /// <summary>Metres a car covers from 15 m/s to a standstill with the brake
    /// pedal HELD, on a bare Car driven by KeyboardDriver.
    ///
    /// Deliberately not the engine with SetControl: a car on a route has an AI
    /// driver, and my first version of this measured 31.9 m for the Fiat because
    /// the nav was still deciding the throttle — i.e. it measured coasting and
    /// called it braking. The pedal is the thing the model's brake_rate governs.</summary>
    static double MetersToStop(string model, double startSpeedMps = 15.0)
    {
        var net = TestMaps.BuildTestMap("basic");
        var car = new Car(100.0, 250.0, 180.0, 0, new KeyboardDriver()) { Model = model };
        car.Speed = startSpeedMps;
        var keys = new Dictionary<string, bool> { [Config.KEY_S] = true };
        var driver = new KeyboardDriver();
        double travelled = 0.0, px = car.X, py = car.Y;
        for (int i = 0; i < 60 * 12; i++)
        {
            var control = driver.GetControl(car, net, SimEngine.DtFixed, keys);
            car.Update(SimEngine.DtFixed, net, control);
            travelled += Math.Sqrt(Math.Pow(car.X - px, 2) + Math.Pow(car.Y - py, 2));
            px = car.X; py = car.Y;
            if (car.Speed < 0.02) break;
        }
        return travelled;
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

    // ---- issue 42: per-model geometry in the planner ----------------------------------

    [Fact]
    public void WheelbaseIsPerModelAndKeepsTheReferenceCarExact()
    {
        // The estimate must not move the car every existing verdict was measured with:
        // mercedes_c220 keeps the sim's 2.70 m exactly.
        Assert.Equal(2.7, Config.WheelbaseM("mercedes_c220"), 9);
        // ... and a shorter body must plan shorter.
        Assert.True(Config.WheelbaseM("fiat_500") < Config.WheelbaseM("mercedes_c220"),
            $"a 3.14 m Fiat got wheelbase {Config.WheelbaseM("fiat_500"):.2f} m, not below " +
            $"the 4.40 m sedan s {Config.WheelbaseM("mercedes_c220"):.2f} m");
        Assert.True(Config.WheelbaseM("vw_passat") > Config.WheelbaseM("mercedes_c220"));
        // An unknown name falls back to the default model, not to a zero radius.
        Assert.Equal(2.7, Config.WheelbaseM("does_not_exist"), 9);
        Assert.True(Config.WheelbaseM(null) >= 1.0);
    }

    [Fact]
    public void PlannerRadiusFollowsTheCarAndNotTheReferenceSedan()
    {
        var net = TestMaps.BuildTestMap("basic");
        var dSedan = new BicycleDriver();
        var sedan = new Car(0, 0, 180.0, 0, dSedan) { Model = "mercedes_c220" };
        sedan.BicycleNav ??= new BicycleNav(sedan, net);
        var dSmall = new BicycleDriver();
        var small = new Car(0, 0, 180.0, 0, dSmall) { Model = "fiat_500" };
        small.BicycleNav ??= new BicycleNav(small, net);

        // The number the planner curvature is built from - R = wheelbase / tan(max steer).
        double sedanRef = BicycleNav.WHEELBASE_M / Math.Tan(Config.MaxSteerRad());
        Assert.Equal(sedanRef, sedan.BicycleNav!.MinTurnRadiusM, 6);
        Assert.True(small.BicycleNav!.MinTurnRadiusM < sedanRef - 0.5,
            $"the Fiat was planned with radius {small.BicycleNav.MinTurnRadiusM:F2} m - the " +
            $"sedan s {sedanRef:F2} m is what issue 42 forbids for a 3.14 m body");
    }
}
