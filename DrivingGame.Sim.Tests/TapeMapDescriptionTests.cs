using DrivingGame.Sim;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace DrivingGame.Sim.Tests;

/// <summary>
/// DGTAP2 (issue #99): the tape describes its own world.
///
/// Before this, the file pinned the map by name+hash while the geometry and
/// signs lived only in C# code (TestMaps.BuildBasicTestMap) - a cold reader
/// had a reference to a world it could not see, so "which road is car 1 on at
/// frame N" was unanswerable from the file alone. Now Save writes one `# M`
/// line per segment and one `# S` line per sign, captured from the RoadNetwork
/// that Record() already receives: no host, no repo checkout, no TestMaps run.
/// Time-varying rules (future traffic lights) get `# R` change records via
/// NoteRuleChange - event-sourced like decisions, folded in step order, never
/// replayed from zero.
/// </summary>
public class TapeMapDescriptionTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "tape_mapdesc_" + Guid.NewGuid().ToString("N"));

    public TapeMapDescriptionTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        RunTape.Stop(); RunTape.Clear();
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    [Fact]
    public void Save_WritesOneMapRecordPerSegmentAndOnePerSign()
    {
        var net = TestMaps.BuildTestMap("basic");
        var car = new Car(0.0, 0.0, 90.0, 0, new BicycleDriver()) { Model = "blue" };

        RunTape.Start("unittest", 1.0 / 60.0, "hash42");
        RunTape.Record(100, new List<Car> { car }, new Dictionary<int, bool>(),
                       new Dictionary<int, string>(), 5.0, 1.0, net);
        var path = RunTape.Save(Path.Combine(_dir, "mapdesc.txt"));
        Assert.True(path != null);

        var lines = File.ReadAllLines(path!);
        Assert.StartsWith("# DGTAP2", lines[0]);

        var mapRecs = lines.Where(l => l.StartsWith("# M seg=")).ToList();
        var signRecs = lines.Where(l => l.StartsWith("# S seg=")).ToList();
        Assert.Equal(net.Segments.Count, mapRecs.Count);
        Assert.Equal(net.Signs.Count, signRecs.Count);

        // Spot-check segment 0 against the network it was captured from:
        // the record must carry the SAME geometry, not a re-derived one.
        var s = net.Segments[0];
        Assert.Contains(
            $"# M seg=0 name={s.Name} x1={s.X1:F2} y1={s.Y1:F2} " +
            $"x2={s.X2:F2} y2={s.Y2:F2} w={s.Width:F2} lanes={s.Lanes} " +
            $"shoulder={s.Shoulder:F2} level={s.Level} " +
            $"oneway={(s.Oneway ? 1 : 0)} parklane={s.ParkingLaneWidth:F2}",
            mapRecs);

        // Every sign record names a real node with its position.
        foreach (var l in signRecs)
        {
            Assert.Contains(" type=", l);
            Assert.DoesNotContain("x=?", l);
        }

        // The frame line is still the first non-# line after the header block:
        // map records must not disturb the frame grammar or its position.
        var frame = lines.First(l => !l.StartsWith("#"));
        Assert.StartsWith("100;", frame);
    }

    [Fact]
    public void RuleChanges_AreWrittenInStepOrder_RegardlessOfArrival()
    {
        var net = TestMaps.BuildTestMap("basic");
        var car = new Car(0.0, 0.0, 90.0, 0, new BicycleDriver()) { Model = "blue" };

        RunTape.Start("unittest", 1.0 / 60.0, "hash42");
        // Deliberately out of order: the fold at step N must see them sorted.
        RunTape.NoteRuleChange(5, "light#3 -> red");
        RunTape.NoteRuleChange(2, "light#7 -> green");
        RunTape.Record(10, new List<Car> { car }, new Dictionary<int, bool>(),
                       new Dictionary<int, string>(), 5.0, 1.0, net);
        var path = RunTape.Save(Path.Combine(_dir, "rulechange.txt"));
        Assert.True(path != null);

        var lines = File.ReadAllLines(path!);
        var r = lines.Where(l => l.StartsWith("# R step=")).ToList();
        Assert.Equal(2, r.Count);
        Assert.Contains("# R step=2 light#7 -> green", r);
        Assert.Contains("# R step=5 light#3 -> red", r);
        // Step order in the file: 2 before 5.
        Assert.True(Array.IndexOf(lines, r[0]) < Array.IndexOf(lines, r[1]));
    }

    [Fact]
    public void A_Tape_WithoutANeverSeenNetwork_WritesNoMapRecords_ButStillSaves()
    {
        // Record() may be called with network == null (the Level lookup already
        // guards for it); the map block is then simply absent, not an error.
        var car = new Car(0.0, 0.0, 90.0, 0, new BicycleDriver()) { Model = "blue" };

        RunTape.Start("unittest", 1.0 / 60.0, "hash42");
        RunTape.Record(7, new List<Car> { car }, new Dictionary<int, bool>(),
                       new Dictionary<int, string>(), 5.0, 1.0, null);
        var path = RunTape.Save(Path.Combine(_dir, "nomap.txt"));
        Assert.True(path != null);

        var lines = File.ReadAllLines(path!);
        Assert.StartsWith("# DGTAP2", lines[0]);
        Assert.DoesNotContain(lines, l => l.StartsWith("# M seg="));
        Assert.StartsWith("7;", lines.First(l => !l.StartsWith("#")));
    }
}
