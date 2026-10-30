using DrivingGame.Sim;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace DrivingGame.Sim.Tests;

/// <summary>
/// A per-car option that changes DURING the run (issue #10) — a repaint at the
/// kerb, tyre lines switched on to look at a wobble.
///
/// The staged header (`caropts=`) cannot carry this: it says what the cast was
/// BORN with, and a run that paints its car twice would record one of the two
/// facts at random. So a change is its own line, keyed on a substep exactly like
/// a focus note, and a review folds the lines up to its cursor. What the format
/// has to guarantee is that the fold is unambiguous: a field that was NOT part
/// of the change writes NO token, because an absent token means "keep what the
/// car had" while `tracks=off` means "the operator turned it off".
/// </summary>
public class TapeCarOptionEventsTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "tape_carevents_" + Guid.NewGuid().ToString("N"));

    public TapeCarOptionEventsTests()
    {
        Directory.CreateDirectory(_dir);
        RunTape.Stop(); RunTape.Clear();
    }

    public void Dispose()
    {
        RunTape.Stop(); RunTape.Clear();
        try { Directory.Delete(_dir, true); } catch { /* temp tree, best effort */ }
    }

    static Car ProbeCar(string color) =>
        new Car(100.0, 200.0, 90.0, 0, new BicycleDriver()) { Model = color };

    [Fact]
    public void Change_WithoutRecording_WritesNothing()
    {
        // A sim that is not recording has no tape to append to; an event kept in
        // memory would surface on the NEXT, unrelated save.
        RunTape.NoteCarOptions(10, 1L, null, null, "#c62828");
        Assert.Empty(RunTape.CarOptionEvents);
    }

    [Fact]
    public void PaintOnlyChange_WritesOnlyThePaint()
    {
        var car = ProbeCar("blue");
        RunTape.Start("basic", 1.0 / 60.0, "hash42");
        RunTape.SetCarOptions(car.Uid, detail: true, tracks: true, color: "#2e7d32");
        RunTape.Record(60, new List<Car> { car }, new Dictionary<int, bool>(),
                       new Dictionary<int, string> { [car.Uid] = "repainted" }, 4.0, 1.0, null);
        RunTape.NoteCarOptions(601, car.Uid, detail: null, tracks: null, color: "#123456");
        var path = RunTape.Save(Path.Combine(_dir, "paint.txt"));
        Assert.NotNull(path);

        var lines = File.ReadAllLines(path!);
        var ev = lines.Single(l => l.StartsWith("# caropts "));
        Assert.Equal($"# caropts step=601 uid={car.Uid} color=123456", ev);
        // The staged header is NOT rewritten by a change: it stays the birth
        // state, so the fold (header → events) is the only place the two meet.
        Assert.Contains($"caropts={car.Uid}:detail=on+tracks=on+color=2e7d32",
                        lines.First(l => l.StartsWith("# DGTAP1")));
    }

    [Fact]
    public void Events_RoundTripInOrderAndKeepTheThreeStates()
    {
        var a = ProbeCar("blue");
        var b = ProbeCar("tan");
        RunTape.Start("basic", 1.0 / 60.0, "hash42");
        RunTape.Record(1, new List<Car> { a, b }, new Dictionary<int, bool>(),
                       new Dictionary<int, string> { [a.Uid] = "a", [b.Uid] = "b" },
                       4.0, 1.0, null);
        RunTape.NoteCarOptions(120, a.Uid, detail: null, tracks: false, color: null);
        RunTape.NoteCarOptions(300, a.Uid, detail: null, tracks: null, color: "#00aa11");
        RunTape.NoteCarOptions(480, b.Uid, detail: true, tracks: true, color: null);
        var path = RunTape.Save(Path.Combine(_dir, "three.txt"));

        RunTape.Stop(); RunTape.Clear();
        Assert.True(RunTape.Load(path!));
        var back = RunTape.CarOptionEvents;
        Assert.Equal(3, back.Count);
        Assert.Equal(120, back[0].Step);          // tape order survives
        Assert.Equal(a.Uid, back[0].Uid);
        Assert.False(back[0].Tracks);             // DECLARED off…
        Assert.Null(back[0].Color);              // …and untouched paint
        Assert.Null(back[0].Detail);              // …and nothing said about a window
        Assert.Equal("#00aa11", back[1].Color);
        Assert.True(back[2].Detail);
        Assert.True(back[2].Tracks);
        Assert.Equal(b.Uid, back[2].Uid);
    }

    [Fact]
    public void AChangeIsNotAHeaderToken_TheTwoParsesStayApart()
    {
        // The header branch reads `caropts=`; an event line is `# caropts step=…`.
        // If the event's own tokens (uid=, color=) ever reached the header parse,
        // a review would take a CHANGE for a BIRTH fact and the fold would double
        // it — so the header line is matched by name, not by substring.
        var car = ProbeCar("blue");
        RunTape.Start("basic", 1.0 / 60.0, "hash42");
        RunTape.SetCarOptions(car.Uid, detail: null, tracks: true, color: null);
        RunTape.Record(1, new List<Car> { car }, new Dictionary<int, bool>(),
                       new Dictionary<int, string> { [car.Uid] = "a" }, 4.0, 1.0, null);
        RunTape.NoteCarOptions(90, car.Uid, detail: false, tracks: null, color: "#ff0000");
        var path = RunTape.Save(Path.Combine(_dir, "apart.txt"));

        RunTape.Stop(); RunTape.Clear();
        Assert.True(RunTape.Load(path!));
        Assert.Equal(new[] { (bool?)true }, RunTape.CarOptions.Values.Select(o => o.Tracks));
        Assert.Null(RunTape.CarOptions[car.Uid].Detail);      // birth: nothing said
        Assert.Single(RunTape.CarOptionEvents);               // change: window OFF
        Assert.False(RunTape.CarOptionEvents[0].Detail);
    }
}
