using DrivingGame.Sim;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace DrivingGame.Sim.Tests;

/// <summary>
/// The tape header's `caropts=` token — the two per-car VIEW options a scenario
/// declares at staging (issue #9): does this car get a detail window, does it
/// draw its own tyre lines.
///
/// The distinction the format has to survive is the one the client acts on:
///   * DECLARED on  → draw it,
///   * DECLARED off → do not draw it, even for the followed car (a test that
///     says `tyre_lines:false` means it),
///   * ABSENT       → nothing was said, and the standing behaviour applies.
/// So "off" and "absent" may not collapse into one value, and a cast nobody
/// authored must not grow the token at all — an unauthored run has to load and
/// look exactly like the run recorded before this field existed.
/// </summary>
public class TapeCarOptionsTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "tape_caropts_" + Guid.NewGuid().ToString("N"));

    public TapeCarOptionsTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        RunTape.Stop(); RunTape.Clear();
        try { Directory.Delete(_dir, true); } catch { /* temp tree, best effort */ }
    }

    static Car ProbeCar(string color) =>
        new Car(100.0, 200.0, 90.0, 0, new BicycleDriver()) { Color = color };

    [Fact]
    public void CarOptions_OnOffAndAbsent_SurviveTheHeaderRoundTrip()
    {
        var on = ProbeCar("blue");        // both options declared ON
        var off = ProbeCar("van");        // detail explicitly OFF, tracks ON
        var quiet = ProbeCar("silver");   // nothing declared about this car
        var wrong = new Dictionary<int, bool>();
        var names = new Dictionary<int, string>
        {
            [on.Uid] = "tracked", [off.Uid] = "windowless", [quiet.Uid] = "plain",
        };

        RunTape.Start("unittest", 1.0 / 60.0, "hash42");
        RunTape.SetCarOptions(on.Uid, detail: true, tracks: true, livery: "#c62828");
        RunTape.SetCarOptions(off.Uid, detail: false, tracks: true);
        // `quiet` is deliberately never declared: that is the third state.
        RunTape.Record(601, new List<Car> { on, off, quiet }, wrong, names, 4.0, 1.0, null);
        var path = RunTape.Save(Path.Combine(_dir, "caropts.txt"));
        Assert.NotNull(path);

        // The line on disk is what a reviewer of an OLD file reads, so the shape
        // is part of the contract: uid:detail=on+tracks=on+livery=rrggbb, comma
        // between cars, no '#' (the token is space- and comma-separated).
        var header = File.ReadAllLines(path!).First(l => l.StartsWith("# DGTAP1"));
        Assert.Contains(
            $"caropts={on.Uid}:detail=on+tracks=on+livery=c62828,{off.Uid}:detail=off+tracks=on",
            header);

        RunTape.Stop(); RunTape.Clear();
        Assert.True(RunTape.Load(path!));
        var back = RunTape.CarOptions;
        Assert.True(back[on.Uid].Detail);
        Assert.True(back[on.Uid].Tracks);
        Assert.Equal("#c62828", back[on.Uid].Livery);
        Assert.False(back[off.Uid].Detail);
        Assert.True(back[off.Uid].Tracks);
        Assert.Null(back[off.Uid].Livery);
        Assert.False(back.ContainsKey(quiet.Uid));    // absent ≠ off
    }

    [Fact]
    public void LiveryOnlyDeclaration_RoundTripsWithoutTouchingTheOthers()
    {
        var c = ProbeCar("pickup");
        var wrong = new Dictionary<int, bool>();
        var names = new Dictionary<int, string> { [c.Uid] = "teal_truck" };

        RunTape.Start("unittest", 1.0 / 60.0, "hash42");
        RunTape.SetCarOptions(c.Uid, detail: null, tracks: null, livery: "#4eb7cc");
        RunTape.Record(801, new List<Car> { c }, wrong, names, 3.0, 1.0, null);
        var path = RunTape.Save(Path.Combine(_dir, "livery.txt"));
        Assert.NotNull(path);

        // A paint is a decision on its own: it must not invent detail/tracks
        // facts the test never stated.
        var header = File.ReadAllLines(path!).First(l => l.StartsWith("# DGTAP1"));
        Assert.Contains($"caropts={c.Uid}:livery=4eb7cc", header);
        Assert.DoesNotContain("detail=", header);
        Assert.DoesNotContain("tracks=", header);

        RunTape.Stop(); RunTape.Clear();
        Assert.True(RunTape.Load(path!));
        var one = RunTape.CarOptions[c.Uid];
        Assert.Equal("#4eb7cc", one.Livery);
        Assert.Null(one.Detail);
        Assert.Null(one.Tracks);
    }

    [Fact]
    public void UnauthoredCast_WritesNoCaroptsToken_AndLoadsAsBefore()
    {
        var a = ProbeCar("blue");
        var b = ProbeCar("van");
        var wrong = new Dictionary<int, bool>();
        var names = new Dictionary<int, string> { [a.Uid] = "one", [b.Uid] = "two" };

        RunTape.Start("unittest", 1.0 / 60.0, "hash42");
        RunTape.Record(701, new List<Car> { a, b }, wrong, names, 4.0, 1.0, null);
        var path = RunTape.Save(Path.Combine(_dir, "plain.txt"));
        Assert.NotNull(path);

        var header = File.ReadAllLines(path!).First(l => l.StartsWith("# DGTAP1"));
        Assert.DoesNotContain("caropts=", header);
        Assert.Contains("names=", header);       // the cast is still there

        RunTape.Stop(); RunTape.Clear();
        Assert.True(RunTape.Load(path!));
        Assert.Empty(RunTape.CarOptions);        // → the client's standing behaviour
    }

    [Fact]
    public void SetCarOptions_WithoutADecision_StoresNothing()
    {
        RunTape.Start("unittest", 1.0 / 60.0, "hash42");
        RunTape.SetCarOptions(4242, detail: null, tracks: null);
        Assert.False(RunTape.CarOptions.ContainsKey(4242));
    }
}
