using DrivingGame.Sim;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace DrivingGame.Sim.Tests;

/// <summary>
/// The tape header's `map=` token — the name of the ground the cars were
/// recorded on, which a review window has to rebuild (#2), so it has to be the
/// map's REAL name (#11).
///
/// Two separate faults are pinned here, because they failed differently:
///   * the engine stamped the literal "engine boot" (its self-description, not a
///     map name), so any run started outside the e2e runner recorded a tape
///     whose map could not be found;
///   * the header line is space-separated `key=value` tokens and the reader
///     takes the first token after `map=`, so `map=engine boot` did not fail —
///     it read back as a map called `engine`, unbuildable, and the reviewer
///     refused to paint a single car (correctly, but confusingly).
/// A name that cannot survive the format is therefore replaced visibly, and a
/// named host names its map from the moment its engine exists.
/// </summary>
public class TapeMapNameTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "tape_mapname_" + Guid.NewGuid().ToString("N"));

    public TapeMapNameTests()
    {
        Directory.CreateDirectory(_dir);
        // The recorder is a process static: take it away so each test sees the
        // birth of its own tape, whatever a sibling test left running.
        RunTape.Stop(); RunTape.Clear();
    }

    public void Dispose()
    {
        RunTape.Stop(); RunTape.Clear();
        try { Directory.Delete(_dir, true); } catch { /* temp tree, best effort */ }
    }

    /// <summary>First header line as written to disk — the file is the contract,
    /// since that is what a reviewer of an old tape reads.</summary>
    string HeaderOf(string path) =>
        File.ReadAllLines(path).First(l => l.StartsWith("# DGTAP1"));

    [Fact]
    public void Engine_StampsTheMapNameItWasGiven()
    {
        var net = TestMaps.BuildTestMap("basic");
        var eng = new SimEngine(net, new ObstacleManager("basic"), "basic");
        Assert.Equal("basic", eng.MapName);

        var path = RunTape.Save(Path.Combine(_dir, "named.txt"));
        Assert.NotNull(path);
        var header = HeaderOf(path!);
        Assert.Contains("map=basic ", header + " ");
        // The boot label must not survive a named host: that is issue #11.
        Assert.DoesNotContain("engine boot", header);
    }

    [Fact]
    public void Engine_WithoutAName_SaysSoAndStaysReadable()
    {
        // A hand-built network in a test has no map name; the old label is honest
        // there — but it must reach the header in a shape the reader can parse.
        var net = TestMaps.BuildTestMap("basic");
        var eng = new SimEngine(net, new ObstacleManager("basic"));
        Assert.Equal("engine boot", eng.MapName);

        var path = RunTape.Save(Path.Combine(_dir, "unnamed.txt"));
        var header = HeaderOf(path!);
        Assert.Contains("map=engine_boot", header);
        Assert.DoesNotContain("map=engine boot", header);
        // What the reader actually takes out of that line:
        Assert.Equal("engine_boot", header.Split("map=")[1].Split(" ")[0]);
    }

    [Fact]
    public void MapName_TokenSeparatorsAreReplacedNotWritten()
    {
        RunTape.Start("osm west=13.4, east=13.5", 1.0 / 60.0, "hash42");
        var path = RunTape.Save(Path.Combine(_dir, "spaced.txt"));
        var header = HeaderOf(path!);
        // space, ',' and '=' all separate tokens inside the header — none of them
        // may appear inside the value, or the name silently shortens.
        Assert.Equal("osm_west_13.4__east_13.5", header.Split("map=")[1].Split(" ")[0]);
    }

    [Fact]
    public void NamedTape_LoadsAndKeepsItsMap()
    {
        var net = TestMaps.BuildTestMap("basic");
        var eng = new SimEngine(net, new ObstacleManager("basic"), "basic");
        // One recorded substep: a tape with no frames is refused by Load (by
        // design - an empty file is not a run), so the round trip needs a car.
        var car = new Car(100.0, 200.0, 90.0, 0, new BicycleDriver()) { Model = "blue" };
        RunTape.Record(1, new List<Car> { car }, new Dictionary<int, bool>(),
                       new Dictionary<int, string> { [car.Uid] = "probe" },
                       4.0, 1.0, net);
        var path = RunTape.Save(Path.Combine(_dir, "roundtrip.txt"));

        RunTape.Stop(); RunTape.Clear();
        Assert.True(RunTape.Load(path!));
        // The loaded clock is the tape's own; the NAME is what #11 is about, and
        // it is the name the review window asks TestMaps for (#2).
        Assert.Equal("basic", HeaderOf(path!).Split("map=")[1].Split(" ")[0]);
    }
}
