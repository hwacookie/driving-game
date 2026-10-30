using DrivingGame.Sim;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Xunit;

namespace DrivingGame.Sim.Tests;

/// <summary>
/// A parked-car obstacle gets a MODEL (#22). It always had a <c>color</c> — and
/// that word is honest: the palette maps its three entries to RGB triples, so it
/// is a paint (#17 checked this before renaming anything). What was missing is
/// the other half of the owner's model: WHICH vehicle sits there, because that
/// decides how many metres of road the obstruction owns.
///
/// Before this, every obstacle was the sedan box "regardless of its color"
/// (docs/OBSTACLES.md' own words), so a parked mixer — 8.40 m — blocked 4.40 m
/// and a car computed a gap through it that did not exist.
/// </summary>
public class ObstacleModelTests
{
    static Obstacle Parked(string model, double heading = 0.0) =>
        new Obstacle(1, 98.0, 250.0, heading, "car", "blue", model);

    static (double Length, double Width) BoxOf(Obstacle ob)
    {
        var pts = ObstacleGeometry.ObstacleFootprint(ob);
        double Edge(int i) => Math.Sqrt(
            Math.Pow(pts[i].X - pts[(i + 1) % pts.Count].X, 2) +
            Math.Pow(pts[i].Y - pts[(i + 1) % pts.Count].Y, 2));
        return (Math.Max(Edge(0), Edge(1)), Math.Min(Edge(0), Edge(1)));
    }

    [Theory]
    [InlineData("mercedes_c220", 4.40, 1.80)]     // what every layout always was
    [InlineData("fiat_500", 3.14, 1.71)]
    [InlineData("bmw_police", 4.71, 1.86)]
    [InlineData("vw_passat", 4.75, 1.86)]
    [InlineData("tractor_unit", 7.00, 2.85)]
    [InlineData("ford_f150", 5.25, 2.09)]
    [InlineData("mixer_truck", 8.40, 2.80)]
    public void Footprint_IsTheModelsMetres(string model, double length, double width)
    {
        var (L, W) = BoxOf(Parked(model));
        Assert.Equal(length, L, 3);
        Assert.Equal(width, W, 3);
    }

    [Fact]
    public void ALongObstructionIsNotSedanSizedAnymore()
    {
        // The point of the issue, stated as one number: the parked mixer owns
        // nearly twice the road the code used to give it.
        var (sedan, _) = BoxOf(Parked("mercedes_c220"));
        var (mixer, _) = BoxOf(Parked("mixer_truck"));
        Assert.True(mixer > 1.8 * sedan,
            $"expected the mixer to own far more road than the sedan box ({mixer:F2} vs {sedan:F2})");
    }

    [Fact]
    public void ALegacyLayoutEntry_WithoutAModel_IsTheSedanItAlwaysWas()
    {
        // Files on disk predate the field. Loading them must not silently resize
        // an obstruction — that would move a collision the layout was authored
        // against — so a missing `model` IS the sedan, and the round trip keeps
        // the model that was written.
        using var legacy = JsonDocument.Parse(
            """{"id": 7, "type": "car", "color": "yellow", "x": 98.0, "y": 250.0, "heading": 0.0}""");
        var ob = Obstacle.FromJson(legacy.RootElement);
        Assert.Equal("mercedes_c220", ob.Model);
        Assert.Equal("yellow", ob.Color);           // the paint stays a paint
        Assert.Equal(4.40, BoxOf(ob).Length, 3);

        using var modern = JsonDocument.Parse(
            """{"id": 8, "type": "car", "color": "white", "model": "mixer_truck", "x": 98.0, "y": 250.0, "heading": 0.0}""");
        Assert.Equal("mixer_truck", Obstacle.FromJson(modern.RootElement).Model);
    }

    [Fact]
    public void PhotoAliases_ResolveHereToo_BecauseTheyResolveEverywhere()
    {
        // "mixer" is a photo word from the old vocabulary; Config.ModelFor is the
        // one crossing point, so a caller that speaks either era parks the same
        // truck. The stored value is always canonical (the layout file and
        // GET /obstacles answer a model name, not a colour).
        var net = TestMaps.BuildTestMap("basic");
        var dir = Path.Combine(Path.GetTempPath(), "obs_model_" + Guid.NewGuid().ToString("N"));
        var mgr = new ObstacleManager("basic", dir);
        var ob = mgr.Place(net, "car", "white", 98.0, 250.0, "mixer");
        Assert.Equal("mixer_truck", ob.Model);
        Assert.Equal(8.40, BoxOf(ob).Length, 3);
        try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }

    [Fact]
    public void Place_RejectsAnUnknownModel_InsteadOfQuietlyParkingASedan()
    {
        var net = TestMaps.BuildTestMap("basic");
        var dir = Path.Combine(Path.GetTempPath(), "obs_model_" + Guid.NewGuid().ToString("N"));
        var mgr = new ObstacleManager("basic", dir);
        var ex = Assert.Throws<PlacementError>(() => mgr.Place(net, "car", "blue", 98.0, 250.0, "spaceship"));
        Assert.Contains("unknown model 'spaceship'", ex.Message);
        Assert.Contains("mercedes_c220", ex.Message);      // the message names what IS allowed
        try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }
}
