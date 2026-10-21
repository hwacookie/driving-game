// Lane structure as DATA, not as geometry to search (2026-10-20).
//
// The lane guard used to answer "how far is this car from a centreline?" by
// walking every centreline vertex of the whole map for every car every
// substep (measured: 405 us/car/step - 57 % of the sim step, and flat in car
// count, because it does not depend on the traffic at all; see
// review_2026-10-20.md §4). A car can only ever be in trouble with the road it
// is ON, and the car already knows which road that is: Car.SegIdx.
//
// So this class states, per segment, the lane structure the segment DECLARES
// (RoadSegment.Width / Lanes / ParkingLaneWidth / Oneway) as lateral offsets
// from the centreline. Asking "which lane am I in, and which side of the
// centreline" is then arithmetic on those offsets - no search, no polygon,
// no vertex list. The painted lines (RoadNetworkGeometry.GetLaneMarkings)
// are built from the same segment fields, so what is enforced is what is
// drawn.
//
// Sign convention: every offset here is in the segment's OWN start->end
// frame, positive = to the RIGHT of start->end travel. A car driving the
// segment backwards (Car.Forward == false) negates it. This matches
// Config.LaneBaseOffsetM, which also measures right of the centreline.

namespace DrivingGame.Sim;

/// <summary>The declared lane structure of one road segment.</summary>
public sealed record LaneStrip(bool Oneway, int LanesPerSide, double HalfWidthM,
                               double DrivingOuterM, double LaneWidthM,
                               double ParkingLaneWidthM)
{
    /// <summary>Half the carriageway (m) - the kerb line, either side.</summary>
    /// <summary>Outer edge of the DRIVING strip (m right of the centreline);
    /// beyond it lies the parking lane, inside it the driving lanes. 0 when
    /// the whole carriageway is parking.</summary>
    /// <summary>Width of one driving lane (m).</summary>

    // Lane-bound ENFORCEMENT (stay inside your lane unless the move is
    // intentional) is not wired yet: the guard still asks only the centreline
    // question it always asked, it just asks it locally and cheaply. These
    // offsets are what that rule will read when it lands - they are declared
    // road data, derived once per network from RoadSegment's own fields.
}

/// <summary>Per-network lane data, indexed by segment index.</summary>
public sealed class LaneGeometry
{
    public List<LaneStrip> Strips { get; }

    public LaneStrip Of(int segIdx) =>
        segIdx >= 0 && segIdx < Strips.Count ? Strips[segIdx] : Strips[0];

    public LaneGeometry(RoadNetwork network)
    {
        Strips = new List<LaneStrip>(network.Segments.Count);
        for (int i = 0; i < network.Segments.Count; i++)
            Strips.Add(StripOf(network.Segments[i]));
    }

    private static LaneStrip StripOf(RoadSegment seg)
    {
        double halfWidth = Math.Max(0.0, seg.Width / 2.0);
        double parking = Math.Max(0.0, Math.Min(seg.ParkingLaneWidth, halfWidth));

        if (seg.Oneway)
        {
            // A one-way carriageway has no centreline: the lanes span the
            // whole width, counted from the LEFT kerb.
            int lanes = Math.Max(1, seg.Lanes);
            double usable = Math.Max(0.0, halfWidth * 2.0 - parking);
            return new LaneStrip(true, lanes, halfWidth, usable,
                                 usable / lanes, parking);
        }

        // Two-way: the centreline is 0, driving lanes run outward to
        // DrivingOuterM, the parking lane sits between it and the kerb.
        double drivingOuter = Math.Max(0.0, halfWidth - parking);
        // Lanes counts lanes PER SIDE (see Config.LaneBaseOffsetM); a road
        // that declares no lane count is one lane per side.
        int lanesPerSide = Math.Max(1, seg.Lanes);
        return new LaneStrip(false, lanesPerSide, halfWidth, drivingOuter,
                             drivingOuter / lanesPerSide, parking);
    }
}
