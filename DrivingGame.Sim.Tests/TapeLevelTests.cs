using DrivingGame.Sim;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace DrivingGame.Sim.Tests;

/// <summary>
/// `level` on the tape (docs/RECORD_PLAYBACK.md → "The channels").
///
/// Why the field is load-bearing: the renderer OFFSETS a car by the deck it
/// drives on and z-orders it above that deck but below a higher one, so a
/// recording that cannot say which deck a car was on cannot say what the frame
/// under the cursor looked like. The reviewer used to paint `Level = 0` for
/// EVERY tape car — a bridge car at grade, in a window whose whole claim is
/// "the tape is the picture". `level` is in `/state` from the start
/// (`SimEngine.cs:1264`), it was the tape that lacked it — which is why the
/// recorder resolves it here from the SAME lookup the `/state` builder uses:
/// one source, so the two channels cannot drift apart.
///
/// Two rules the shape had to keep:
///   1. `level` goes BEFORE `capKey`. `capKey` stays LAST because it may carry
///      the field delimiter (`r32#12,passage#4`); appending after it would
///      silently split the key in both directions.
///   2. A run recorded without the field loads as ground (0) — the reviewer
///      reads tapes that were recorded before the field existed.
///
/// The deck is a REAL one: the basic test map's figure-8 puts its NE-SW branch
/// over the crossing (`TestMaps.cs:540` sets `level: 1` on segments n10..n14),
/// so the number is the sim's own, not a made-up column.
/// </summary>
public class TapeLevelTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "tape_level_" + Guid.NewGuid().ToString("N"));

    public TapeLevelTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        RunTape.Stop(); RunTape.Clear();
        try { Directory.Delete(_dir, true); } catch { /* temp tree, best effort */ }
    }

    static int FirstDeckSegment(RoadNetwork net)
    {
        for (int i = 0; i < net.Segments.Count; i++)
            if (net.Segments[i].Level >= 1) return i;
        return -1;
    }

    [Fact]
    public void DeckLevel_SurvivesTheTapeRoundTrip()
    {
        var net = TestMaps.BuildTestMap("basic");
        var deck = FirstDeckSegment(net);
        Assert.True(deck >= 0,
            "the basic test map must still have a level-1 (bridge) segment");
        var on = new Car(0.0, 0.0, 90.0, deck, new BicycleDriver()) { Vehicle = "blue" };

        RunTape.Start("unittest", 1.0 / 60.0, "hash42");
        RunTape.Record(801, new List<Car> { on }, new Dictionary<int, bool>(),
                       new Dictionary<int, string>(), 5.0, 1.0, net);
        var path = RunTape.Save(Path.Combine(_dir, "gen5.txt"));
        RunTape.Stop(); RunTape.Clear();
        Assert.True(path != null);

        // The deck is the 13th field (index 12): level went BEFORE capKey, which
        // must stay last in every generation.
        var fields = File.ReadAllLines(path!).First(l => !l.StartsWith("#"))
                        .Split(";")[3].Split("|")[0].Split(",");
        Assert.Equal(14, fields.Length);
        Assert.Equal("1", fields[12]);

        Assert.True(RunTape.Load(path!));
        var back = RunTape.FrameAt(801);
        Assert.NotNull(back);
        Assert.Equal(1, back!.Cars.First(cf => cf.Uid == on.Uid).Level);
    }

    [Fact]
    public void TheCarUnderTheSameBridge_RecordsGround()
    {
        // The other direction of the same rule: a car on the ground must not
        // pick up the deck of the segment it happens to CROSS under. The
        // figure-8's ground branch (n36) shares the crossing point with the
        // bridge, so a lookup that read the wrong segment would answer 1 here.
        var net = TestMaps.BuildTestMap("basic");
        var ground = -1;
        for (int i = 0; i < net.Segments.Count; i++)
            if (net.Segments[i].Level == 0 && net.Segments[i].Name.StartsWith("fig8_n36"))
            { ground = i; break; }
        Assert.True(ground >= 0, "the figure-8's ground branch must still be at level 0");

        var under = new Car(0.0, 0.0, 90.0, ground, new BicycleDriver()) { Vehicle = "tan" };
        RunTape.Start("unittest", 1.0 / 60.0, "hash42");
        RunTape.Record(802, new List<Car> { under }, new Dictionary<int, bool>(),
                       new Dictionary<int, string>(), 5.0, 1.0, net);
        var path = RunTape.Save(Path.Combine(_dir, "gen5_under.txt"));
        Assert.True(path != null);
        RunTape.Stop(); RunTape.Clear();

        var fields = File.ReadAllLines(path!).First(l => !l.StartsWith("#"))
                        .Split(";")[3].Split("|")[0].Split(",");
        Assert.Equal("0", fields[12]);
        Assert.True(RunTape.Load(path!));
        Assert.Equal(0, RunTape.FrameAt(802)!.Cars.First(cf => cf.Uid == under.Uid).Level);
    }

    [Fact]
    public void AnOlderGeneration_StillLoads_LevelIsGroundNotAGuess()
    {
        // Generation 4 (…,heldBy,capKey - 13 fields) written by hand, because
        // what matters is that a reader of TODAY accepts what a writer of
        // YESTERDAY produced. The cap key carries the comma here too, which is
        // the whole reason the join-from-the-end rule exists - and why `level`
        // could not simply be appended.
        const string header =
            "# DGTAP1 map=unittest map_hash=segs=4 signs=1 sum=1 fnv=1 dt_fixed=0.016667 steps=1";
        var gen4 = Path.Combine(_dir, "gen4.txt");
        // The generation-4 LEGEND - the line that says the car fields END at
        // capKey. Without it the reader cannot tell this record (14 tokens,
        // because the cap key carries a comma) from a generation-5 one, and
        // would read `r32#12` as a deck level. The legend is the point.
        File.WriteAllText(gen4, header + "\n" +
            "# step;fluidity_mps_per_car;moving_share;uid,x,y,heading_deg,speed_mps," +
            "blinkers,brake,wrong,crashes,color,lamps,heldBy,capKey|...;~uid=text~\n" +
            "7;5.0;1.000;9,10.000,20.000,90.00,0.000,0,1,0,0,blue,0,3,r32#12,passage#4\n");

        Assert.True(RunTape.Load(gen4));
        var c = RunTape.FrameAt(7)!.Cars[0];
        Assert.Equal("r32#12,passage#4", c.CapKey);   // last, joined across the comma
        Assert.Equal(3, c.HeldByUid);
        Assert.Equal(0, c.Level);                     // ground: the field never existed
    }

    [Fact]
    public void TheMapStampSurvivesTheHeaderRoundTrip()
    {
        // The stamp is a reference the client CHECKS, and the header line is
        // SPACE-separated - so the stamp may never contain a space, or the
        // read (`Split("map_hash=")[1].Split(" ")[0]`) silently loses its tail
        // and every comparison becomes a false MISMATCH. The old stamps did
        // exactly that (`segs=256` was all a reader ever saw of them), which is
        // why the wide stamp is colon-joined AND versioned: a truncated old
        // value must read as "uncomparable", never as "this map is wrong".
        var net = TestMaps.BuildTestMap("basic");
        var stamp = RoadNetwork.MapFingerprint(net);
        Assert.True(stamp.StartsWith("v2:"), "the wide stamp carries its generation");
        Assert.False(stamp.Contains(" "), "the stamp is one space-free token");

        var file = Path.Combine(_dir, "stamp.txt");
        RunTape.Start("basic", 1.0 / 60.0, stamp);
        // One car, not an empty frame: a frame with no cars is not reviewable
        // and `Load` drops it on purpose (a scrub would otherwise land on a
        // moment that shows nothing).
        var one = new Car(1.0, 2.0, 90.0, 0, new BicycleDriver()) { Vehicle = "blue" };
        RunTape.Record(11, new List<Car> { one }, new Dictionary<int, bool>(),
                       new Dictionary<int, string>(), 0.0, 0.0, net);
        var path = RunTape.Save(file);
        RunTape.Stop(); RunTape.Clear();
        Assert.True(path != null);

        Assert.True(RunTape.Load(path!));
        Assert.Equal(stamp, (string)RunTape.StatusPayload()["map_hash"]);
        Assert.Equal(11, RunTape.FrameAt(11)!.Step);   // an empty frame still loads
    }
}
