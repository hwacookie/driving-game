using DrivingGame.Sim;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace DrivingGame.Sim.Tests;

/// <summary>
/// The tape's per-car field generations, which the car detail window's
/// `heldBy` grew (docs/CAR_DETAIL_WINDOW.md).
///
/// Two things had to stay true while adding a field:
///   1. `heldBy` goes BEFORE `capKey`, not after it. capKey is the LAST field
///      and the reader joins everything from its position to the end of the
///      record, because the fields are comma-separated while a cap key can
///      contain that same delimiter — `CarCollisions` joins an added reason
///      onto an existing key with ',' (`r32#12,passage#4` is the shape; the
///      test uses it as a constructed case, not one measured in a run).
///      Appending after capKey would have swallowed the new number into the
///      cap key — silently, in both directions: an old file read by a new
///      reader and a new file read by an old one.
///   2. A run recorded before this field existed must load with the SAME pose,
///      the same cap key and held-by "nobody", because the reviewer reads tapes
///      that were recorded before it was written.
///
/// The field itself carries the sim's contract: `held_by` names a car only
/// while a cap OWNED by a car is active — the kerb-owned creep cap
/// (`r32#passive`) leaves it 0, because a chain that named a car there would
/// invent one (set and cleared beside `ActiveCapKey` in CarCollisions).
/// </summary>
public class TapeHeldByTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "tape_heldby_" + Guid.NewGuid().ToString("N"));

    public TapeHeldByTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        RunTape.Stop(); RunTape.Clear();
        try { Directory.Delete(_dir, true); } catch { /* temp tree, best effort */ }
    }

    static Car HeldCar(int byUid, string capKey)
    {
        var c = new Car(100.0, 200.0, 90.0, 0, new BicycleDriver()) { Color = "van" };
        c.ActiveCapKey = capKey;
        c.HeldByUid = byUid;
        return c;
    }

    [Fact]
    public void HeldBy_SurvivesTheTapeRoundTrip_BesideADelimiterCapKey()
    {
        var holder = HeldCar(0, "");
        // A cap key carrying the field delimiter: the case that decides
        // where the new column may go.
        var held = HeldCar(0, "r32#12,passage#4");
        held.HeldByUid = holder.Uid;
        var wrong = new Dictionary<int, bool>();
        var names = new Dictionary<int, string> { [holder.Uid] = "holder", [held.Uid] = "held" };

        RunTape.Start("unittest", 1.0 / 60.0, "hash42");
        RunTape.Record(501, new List<Car> { holder, held }, wrong, names, 5.0, 1.0);
        var path = RunTape.Save(Path.Combine(_dir, "gen4.txt"));
        RunTape.Stop(); RunTape.Clear();
        Assert.NotNull(path);

        // The line on disk: uid,x,y,heading,speed,blinkers,brake,wrong,crashes,
        // color,lamps,heldBy,capKey — 13 fields, and 14 delimiter-separated
        // TOKENS for this car, because the cap key itself carries the
        // delimiter. That is the whole reason heldBy went before capKey and
        // not after it.
        var line = File.ReadAllLines(path!).First(l => !l.StartsWith("#"));
        var fields = line.Split(";")[3].Split("|")[1].Split(",");
        Assert.Equal(14, fields.Length);
        Assert.Equal(holder.Uid.ToString(), fields[11]);      // heldBy
        Assert.Equal("r32#12,passage#4",
                     string.Join(",", fields, 12, fields.Length - 12));  // capKey, last

        Assert.True(RunTape.Load(path!));
        var f = RunTape.FrameAt(501);
        Assert.NotNull(f);
        var back = f!.Cars.First(cf => cf.Uid == held.Uid);
        Assert.Equal(holder.Uid, back.HeldByUid);
        Assert.Equal("r32#12,passage#4", back.CapKey);
        Assert.Equal("van", back.Color);
        Assert.Equal(0, f.Cars.First(cf => cf.Uid == holder.Uid).HeldByUid);
    }

    [Fact]
    public void OlderGenerations_StillLoad_CapKeyIntactHeldByNobody()
    {
        // Generation 3 (…,color,lamps,capKey) and generation 2 (…,color,capKey):
        // written by hand here, because what matters is that a READER of today
        // accepts what a writer of yesterday produced.
        const string header = "# DGTAP1 map=unittest map_hash=h dt_fixed=0.016667 steps=2";
        var gen3 = Path.Combine(_dir, "gen3.txt");
        File.WriteAllText(gen3, header + "\n" +
            "# step;fluidity_mps_per_car;moving_share;uid,x,y,heading_deg,speed_mps," +
            "blinkers,brake,wrong,crashes,color,lamps,capKey|...;~uid=text~\n" +
            "11;5.0;1.000;7,10.000,20.000,90.00,0.000,0,1,0,0,blue,0,avoid#12\n" +
            "12;5.0;1.000;7,11.000,20.000,90.00,0.000,0,1,0,0,blue,0,avoid#12\n");
        Assert.True(RunTape.Load(gen3));
        var c3 = RunTape.FrameAt(11)!.Cars[0];
        Assert.Equal("avoid#12", c3.CapKey);
        Assert.Equal(0, c3.HeldByUid);        // nobody is claimed for an old run
        Assert.True(c3.Brake);

        var gen2 = Path.Combine(_dir, "gen2.txt");
        File.WriteAllText(gen2, header + "\n" +
            "11;5.0;1.000;7,10.000,20.000,90.00,0.000,0,1,0,0,blue,avoid#12\n" +
            "12;5.0;1.000;7,11.000,20.000,90.00,0.000,0,1,0,0,blue,avoid#12\n");
        Assert.True(RunTape.Load(gen2));
        var c2 = RunTape.FrameAt(11)!.Cars[0];
        Assert.Equal("avoid#12", c2.CapKey);
        Assert.Equal(0, c2.Lamps);
        Assert.Equal(0, c2.HeldByUid);
    }

}
