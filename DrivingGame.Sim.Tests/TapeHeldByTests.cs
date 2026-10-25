using DrivingGame.Sim;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace DrivingGame.Sim.Tests;

/// <summary>
/// The tape's per-car field generations, which the car detail window's
/// `heldBy` grew (docs/CAR_DETAIL_WINDOW.md) and the reviewer's bridge picture
/// grew (`level`, 2026-10-25 — docs/RECORD_PLAYBACK.md → "The channels").
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
        RunTape.Record(501, new List<Car> { holder, held }, wrong, names, 5.0, 1.0,
                       // The DECK lookup: Record resolves a car's level from
                       // the network the car is on, the way /state does. Null
                       // means "no network to resolve against" - the level
                       // stays 0, which for these test cars (SegIdx 0 on a
                       // hand-made network) is the truth we also assert below.
                       null);
        var path = RunTape.Save(Path.Combine(_dir, "gen5.txt"));
        RunTape.Stop(); RunTape.Clear();
        Assert.NotNull(path);

        // The line on disk: uid,x,y,heading,speed,blinkers,brake,wrong,crashes,
        // color,lamps,heldBy,LEVEL,capKey — 14 fields, and 15 delimiter-separated
        // TOKENS for this car, because the cap key itself carries the
        // delimiter. That is the whole reason heldBy and level went before capKey
        // and not after it.
        var line = File.ReadAllLines(path!).First(l => !l.StartsWith("#"));
        var fields = line.Split(";")[3].Split("|")[1].Split(",");
        Assert.Equal(15, fields.Length);
        Assert.Equal(holder.Uid.ToString(), fields[11]);      // heldBy
        Assert.Equal("0", fields[12]);                        // level (no deck here)
        Assert.Equal("r32#12,passage#4",
                     string.Join(",", fields, 13, fields.Length - 13));  // capKey, last

        Assert.True(RunTape.Load(path!));
        var f = RunTape.FrameAt(501);
        Assert.NotNull(f);
        var back = f!.Cars.First(cf => cf.Uid == held.Uid);
        Assert.Equal(holder.Uid, back.HeldByUid);
        Assert.Equal("r32#12,passage#4", back.CapKey);
        Assert.Equal("van", back.Color);
        Assert.Equal(0, f.Cars.First(cf => cf.Uid == holder.Uid).HeldByUid);
        // The deck survives the round trip - the field that decides whether the
        // reviewer draws a bridge car ON the deck or AT grade.
        Assert.Equal(0, back.Level);
    }

    [Fact]
    public void Level_SurvivesTheTapeRoundTrip_OnABridgeDeck()
    {
        // The field that decides whether the reviewer draws a bridge car ON
        // the deck or AT grade. The basic test map's figure-8 has a real
        // bridge (`TestMaps.cs:540`: segments n10..n14 carry `level: 1`), so
        // this is a real deck, not a made-up number.
        var net = TestMaps.BuildTestMap("basic");
        var deck = -1;
        for (int i = 0; i < net.Segments.Count; i++)
            if (net.Segments[i].Level >= 1) { deck = i; break; }
        Assert.True(deck >= 0, "the basic test map must still have a level-1 (bridge) segment");

        var on = new Car(750.0, 1750.0, 90.0, deck, new BicycleDriver()) { Color = "blue" };
        var wrong = new Dictionary<int, bool>();
        var names = new Dictionary<int, string> { [on.Uid] = "deck_car" };

        RunTape.Start("unittest", 1.0 / 60.0, "hash42");
        RunTape.Record(701, new List<Car> { on }, wrong, names, 5.0, 1.0, net);
        var path = RunTape.Save(Path.Combine(_dir, "gen5_level.txt"));
        RunTape.Stop(); RunTape.Clear();
        Assert.True(path != null);

        // On disk the deck is the 13th field, before capKey - the position is
        // the point of the whole generation table.
        var line = File.ReadAllLines(path!).First(l => !l.StartsWith("#"));
        Assert.Equal("1", line.Split(";")[3].Split("|")[0].Split(",")[12]);

        Assert.True(RunTape.Load(path!));
        var back = RunTape.FrameAt(701);
        Assert.NotNull(back);
        Assert.Equal(1, back.Cars.First(cf => cf.Uid == on.Uid).Level);
    }

    [Fact]
    public void MapFingerprint_SeesSignTypePositionAndTheStartCast()
    {
        // The reference the tape header carries has to be a reference: a map
        // that DRAWS differently must HASH differently. The old stamp mixed
        // only segment endpoints/width/oneway plus the SIGN COUNT - so the
        // same roads with a yield traded for a priority, or a spawn moved,
        // hashed identical and a tape could be painted under a right-of-way
        // that is not the run's (docs/RECORD_PLAYBACK.md -> "The channels").
        var a = TestMaps.BuildTestMap("basic");
        var h0 = RoadNetwork.MapFingerprint(a);
        Assert.True(h0.StartsWith("v2:"), "the wide stamp announces its generation");

        // Flip EVERY sign's type: the roads, the sign count and the endpoints
        // stay, the right-of-way does not.
        var net2 = TestMaps.BuildTestMap("basic");
        foreach (var k in net2.Signs.Keys.ToList())
            net2.Signs[k] = net2.Signs[k] == SignType.Yield ? SignType.Priority : SignType.Yield;
        var h1 = RoadNetwork.MapFingerprint(net2);
        Assert.NotEqual(h0, h1);
        Assert.Equal(a.Signs.Count, net2.Signs.Count);   // the count alone cannot see it

        // Move one start point by 10 m: the drawn map is the same, the run's
        // cast is not - and a tape's frames would land somewhere else.
        var net3 = TestMaps.BuildTestMap("basic");
        var spk = net3.StartPoints.Keys.First();
        var sp = net3.StartPoints[spk];
        net3.StartPoints[spk] = (sp.X + 10.0, sp.Y, sp.Heading, sp.SegIdx, sp.Forward,
                                 sp.LateralOffsetM);
        var h2 = RoadNetwork.MapFingerprint(net3);
        Assert.NotEqual(h0, h2);
    }

    [Fact]
    public void OlderGenerations_StillLoad_CapKeyIntactHeldByNobody()
    {
        // Generation 3 (…,color,lamps,capKey) and generation 2 (…,color,capKey):
        // written by hand here, because what matters is that a READER of today
        // accepts what a writer of yesterday produced — including `level`, which
        // an older run never named and must therefore read as "ground".
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
        Assert.Equal(0, c3.Level);            // ground: the field did not exist
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
