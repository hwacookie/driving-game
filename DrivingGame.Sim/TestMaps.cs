using System;
using System.Collections.Generic;

namespace DrivingGame.Sim;

/// <summary>
/// Synthetic test maps — deterministic, hand-crafted road networks for
/// reproducible testing. Ported from /Users/hauke/prj/car/src/test_maps.py.
/// Selected by name (the Python server's --map flag).
///
/// COORDINATE SYSTEM (shared with the OSM maps): X grows EAST, Y grows NORTH
/// (higher y = further north). Heading is in degrees, 0 = north (+y),
/// positive = right/east, and a car's forward vector is (sin h, cos h).
/// Every scenario is laid out in that frame, so the synthetic maps and the
/// real OSM map use the exact same convention and the same physics/geometry
/// code works on both.
/// </summary>

/// <summary>Helper to construct a RoadNetwork from named nodes given in
/// METRES. Coordinates are in the SHARED world frame (X east, Y NORTH); a
/// node with a larger y is further north. Coordinates may be negative;
/// Build() shifts everything so the world starts at (0, 0).</summary>
public sealed class MapBuilder
{
    private readonly Dictionary<string, (double X, double Y)> _nodesM = new();
    private readonly List<RoadSegment> _segments = new();
    private readonly List<(int SegIdx, string NodeId, SignType Type)> _signs = new();
    private int _nextSegId = 1;
    // name -> (node_id, lateral_offset_m, facing); degree-1 nodes or loops
    // with an explicit facing neighbour.
    private readonly Dictionary<string, (string NodeId, double LateralOffsetM, string? Facing)> _startPoints = new();

    /// <summary>Define a node position in meters (X east, Y north).</summary>
    public void Node(string nodeId, double xM, double yM) => _nodesM[nodeId] = (xM, yM);

    /// <summary>Add a road segment between two named nodes.
    /// width (metres) overrides the ROAD_TYPES-derived width. lanes &gt; 0
    /// marks the segment as a multi-lane carriageway with `lanes` DRIVING
    /// lanes per direction (one-way: total = lanes; two-way: 2 x lanes).
    /// `shoulder` metres of stop lane on the right apply to one-way
    /// layouts. `level`: 0 = ground, 1 = bridge over the ground, -1 =
    /// tunnel. Top-down view has no z axis; levels decide rendering
    /// order/style and keep cars on different levels from colliding.</summary>
    public void Road(string n1, string n2, string highway = "residential",
        bool oneway = false, double? width = null, int lanes = 0,
        double shoulder = 0.0, double parkingLaneWidth = 0.0, int level = 0)
    {
        var (x1M, y1M) = _nodesM[n1];
        var (x2M, y2M) = _nodesM[n2];

        if (width is null)
        {
            if (Config.ROAD_TYPES.TryGetValue(highway, out var roadCfg))
                width = oneway ? roadCfg.Width1Way : roadCfg.Width2Way;
            else
                width = 3.5;
        }

        _segments.Add(new RoadSegment
        {
            Id = _nextSegId++,
            X1 = x1M, Y1 = y1M,
            X2 = x2M, Y2 = y2M,
            Highway = highway,
            Oneway = oneway,
            Width = width.Value,
            StartNode = n1,
            EndNode = n2,
            ParkingLaneWidth = parkingLaneWidth,
            Length = Math.Sqrt(Math.Pow(x2M - x1M, 2) + Math.Pow(y2M - y1M, 2)),
            Lanes = lanes,
            Shoulder = shoulder,
            Level = level,
        });
    }

    /// <summary>Place a road sign: cars on the segment between `fromNode`
    /// and `toNode` that drive TOWARD `toNode` must obey it before entering
    /// the node (yield to priority-road traffic, or carry the priority
    /// marker themselves).</summary>
    public void Sign(string fromNode, string toNode, SignType type)
    {
        for (int i = 0; i < _segments.Count; i++)
        {
            var s = _segments[i];
            if ((s.StartNode == fromNode && s.EndNode == toNode) ||
                (s.StartNode == toNode && s.EndNode == fromNode))
            {
                _signs.Add((i, toNode, type));
                return;
            }
        }
        throw new ArgumentException($"Sign: no segment between '{fromNode}' and '{toNode}'");
    }

    /// <summary>Register a named, deterministic start point at a node. The
    /// node must have exactly one connected segment (a scenario's entry
    /// point) so the car's initial heading and direction of travel are
    /// unambiguous. On a closed loop every node has TWO segments: pass
    /// `facing` = the id of the neighbour node the car faces (drives
    /// toward). lateral_offset_m shifts the spawn laterally from the NORMAL
    /// DRIVING POSITION (right-lane centre): positive = toward the right
    /// kerb, negative = toward the left side of the road.</summary>
    public void Start(string name, string nodeId, double lateralOffsetM = 0.0, string? facing = null)
        => _startPoints[name] = (nodeId, lateralOffsetM, facing);

    /// <summary>Finalize and return the RoadNetwork.</summary>
    public RoadNetwork Build(double marginM = 50.0)
    {
        // ── Shift so world starts at (0, 0) — handle negative coords ──
        // Must happen FIRST, before any coordinate computation.
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        foreach (var (x, y) in _nodesM.Values)
        {
            minX = Math.Min(minX, x); minY = Math.Min(minY, y);
        }
        if (minX < 0 || minY < 0)
        {
            var shifted = new Dictionary<string, (double X, double Y)>();
            foreach (var kv in _nodesM)
                shifted[kv.Key] = (kv.Value.X - minX, kv.Value.Y - minY);
            _nodesM.Clear();
            foreach (var kv in shifted) _nodesM[kv.Key] = kv.Value;
            // Shift segment endpoints (stored in the world frame). Same
            // direction as the node shift above: new = old - min.
            double sdx = minX, sdy = minY;
            foreach (var seg in _segments)
            {
                seg.X1 -= sdx; seg.Y1 -= sdy;
                seg.X2 -= sdx; seg.Y2 -= sdy;
            }
        }

        var nodes = new Dictionary<string, (double X, double Y)>();
        foreach (var kv in _nodesM)
            nodes[kv.Key] = (kv.Value.X, kv.Value.Y);

        // node_connections + node_degree
        var nodeConnections = new Dictionary<string, List<int>>();
        var nodeDegree = new Dictionary<string, int>();
        for (int idx = 0; idx < _segments.Count; idx++)
        {
            var seg = _segments[idx];
            if (!nodeConnections.TryGetValue(seg.StartNode, out var l1)) nodeConnections[seg.StartNode] = l1 = new List<int>();
            l1.Add(idx);
            if (!nodeConnections.TryGetValue(seg.EndNode, out var l2)) nodeConnections[seg.EndNode] = l2 = new List<int>();
            l2.Add(idx);
            nodeDegree[seg.StartNode] = nodeDegree.GetValueOrDefault(seg.StartNode, 0) + 1;
            nodeDegree[seg.EndNode] = nodeDegree.GetValueOrDefault(seg.EndNode, 0) + 1;
        }

        // node_max_width (skip degree-2 straight nodes, like FromOsmData does)
        var nodeInfo = new Dictionary<string, (double HalfWidth, string Highway)>();
        foreach (var seg in _segments)
        {
            double half = seg.Width / 2.0;
            foreach (string nid in new[] { seg.StartNode, seg.EndNode })
            {
                if (!nodeInfo.TryGetValue(nid, out var cur) || half > cur.HalfWidth)
                    nodeInfo[nid] = (half, seg.Highway);
            }
        }
        nodeInfo = nodeInfo.Where(kv => nodeDegree.GetValueOrDefault(kv.Key, 0) != 2)
                           .ToDictionary(kv => kv.Key, kv => kv.Value);

        // Bounds: extent of all nodes + margin
        double maxX = 1000, maxY = 1000;
        if (_nodesM.Count > 0)
        {
            double mx = double.NegativeInfinity, my = double.NegativeInfinity;
            foreach (var (x, y) in _nodesM.Values)
            {
                mx = Math.Max(mx, x); my = Math.Max(my, y);
            }
            maxX = mx + marginM;
            maxY = my + marginM;
        }

        // Resolve named start points: (x, y, heading, seg_idx, forward, lateral_offset_m).
        // The named node must have exactly one connected segment so the
        // direction of travel (heading, forward flag) is unambiguous.
        var startPoints = new Dictionary<string, (double X, double Y, double Heading, int SegIdx, bool Forward, double LateralOffsetM)>();
        foreach (var (name, (nodeId, lateralOffsetM, facing)) in _startPoints)
        {
            var connected = nodeConnections.GetValueOrDefault(nodeId) ?? new List<int>();
            int segIdx;
            if (connected.Count == 1)
            {
                segIdx = connected[0];
            }
            else if (connected.Count == 2 && facing is not null)
            {
                // Closed loop: pick the segment toward `facing`.
                segIdx = -1;
                for (int i = 0; i < connected.Count; i++)
                {
                    var s = _segments[connected[i]];
                    if (s.StartNode == facing || s.EndNode == facing) { segIdx = connected[i]; break; }
                }
                if (segIdx < 0)
                    throw new ArgumentException($"Start point '{name}' -> node '{nodeId}': 'facing' " +
                                                $"node '{facing}' is not a neighbour");
            }
            else
            {
                throw new ArgumentException($"Start point '{name}' -> node '{nodeId}' must have exactly " +
                                            $"1 connected segment (or 2 + facing on a loop), found {connected.Count}");
            }

            var seg = _segments[segIdx];
            bool forward = seg.StartNode == nodeId;
            double x = forward ? seg.X1 : seg.X2;
            double y = forward ? seg.Y1 : seg.Y2;
            double dx = forward ? seg.X2 - seg.X1 : seg.X1 - seg.X2;
            double dy = forward ? seg.Y2 - seg.Y1 : seg.Y1 - seg.Y2;
            double heading = Math.Atan2(dx, dy) * 180.0 / Math.PI;
            startPoints[name] = (x, y, heading, segIdx, forward, lateralOffsetM);
        }

        var net = RoadNetwork.CreateFromParts(nodes, _segments, 0.0, 0.0,
            maxX, maxY, nodeConnections, nodeDegree, nodeInfo);
        foreach (var kv in startPoints) net.StartPoints[kv.Key] = kv.Value;
        foreach (var (idx, nodeId, type) in _signs) net.Signs[(idx, nodeId)] = type;
        return net;
    }
}

/// <summary>Registry of all available test maps + named-map builder
/// (the Python server's --map flag).</summary>
public static class TestMaps
{
    private static readonly Dictionary<string, Func<RoadNetwork>> Registry = new()
    {
        ["basic"] = BuildBasicTestMap,
        ["swatch"] = BuildSwatchMap,
    };

    /// <summary>Build a named synthetic test map.</summary>
    public static RoadNetwork BuildTestMap(string name)
    {
        if (!Registry.TryGetValue(name, out var build))
            throw new ArgumentException($"Unknown test map '{name}'. Available: " +
                string.Join(", ", Registry.Keys.OrderBy(k => k)));
        return build();
    }

    /// <summary>A comprehensive synthetic test track with known geometry.
    /// A grid of ~500 m tiles, each holding one specific road situation.
    /// All coordinates are in the shared world frame (X east, Y NORTH).
    ///   Tile (0,0): Straight road (baseline / acceleration test)
    ///   Tile (1,0): 90 deg RIGHT turn (approach heading south)
    ///   Tile (2,0): 90 deg LEFT turn (approach heading south)
    ///   Tile (3,0): T-junction (3-way, perpendicular)
    ///   Tile (0,1): Y-intersection (3-way, shallow diverging angles)
    ///   Tile (1,1): 4-way intersection (crossroads)
    ///   Tile (2,1): One-way street through a 4-way junction
    ///   Tile (3,1): S-curve (gentle degree-2 bends)
    ///   Tile (0,2): Dead-end
    ///   Tile (1,2): Tight hairpin turn (~150 deg direction change)
    ///   Tile (2,2): Wide sweeping curve (gentle single bend)
    ///   Tile (3,2): Roundabout (one-way ring, 4 two-way spokes)
    ///   Tile (0,3): Sliver junction - a very short approach into a 4-way
    ///               with a near-straight, a sharp-right and a sharp-left
    ///               exit (mirrors the real-world 815 -> 1008 layout).
    ///   Tile (4,0): Widths road (four 50 m sections: 13/9/7/4 m)
    ///   Tile (5,0): WWW zig-zag (own tile since 2026-09-11)
    ///   Tile (4,1): Parking avenue (2 driving + 1 parking lane per side)
    ///   Tile (4,2): One-way INTO a 4-way junction
    ///   Tile (4,3): One-way OUT of a 4-way junction
    ///   Tile (1,3): Figure-8 - ONE continuous loop crossing itself
    ///   Tile (2,3): Right-of-way figure-8 - REAL degree-4 crossing with
    ///               Vorfahrt signs (Priority SE-NW, Yield SW-NE)
    ///   Tile (3,3): Un-signed figure-8 - REAL degree-4 crossing with NO
    ///               signs (right-before-left / Rechts vor Links governs)</summary>
    /// <summary>SWATCH map: one road per MATERIAL, driven as one route.
    /// Ten parallel legs, each a different OSM road type, joined by alternating
    /// semicircular connectors so the whole thing is one continuous serpentine:
    /// a car can be started at one end and drive through every material. That
    /// is the shape a generated texture is judged in — the question "does this
    /// contrast work against the ground and a car" only has an answer while the
    /// material is under traffic at real metre scale, not next to a PNG.
    ///
    /// Layout (metres, x east, y north): legs at x = i*PITCH from y=0..LEG,
    /// connectors alternate above (odd) and below (even), radius PITCH/2. PITCH
    /// is 60 m so even a 14 m motorway keeps a 23 m inner radius on the bend.
    /// The types are the ones ROAD_MATERIALS in the renderer knows: the map
    /// states the road type, the renderer decides its material.</summary>
    public static RoadNetwork BuildSwatchMap()
    {
        var b = new MapBuilder();
        const int LEGS = 10;
        const double PITCH = 60.0, LEG = 400.0, R = PITCH / 2.0;
        string[] types = ["primary_link", "secondary", "secondary_link", "tertiary",
            "tertiary_link", "residential", "unclassified", "service", "motorway_link",
            "trunk_link"];   // every leg at NORMAL width (7 m, service 3.5 m) - a
                            // 14 m motorway reads as a different thing than the
                            // material being judged on an ordinary street

        static string Leg(int i, int k) => $"sw{i}_{k}";

        for (int i = 0; i < LEGS; i++)
        {
            var x = i * PITCH;
            bool up = i % 2 == 0;                        // even legs run north
            var y0 = up ? 0.0 : LEG;
            var y1 = up ? LEG : 0.0;
            b.Node(Leg(i, 0), x, y0);
            b.Node(Leg(i, 1), x, y1);
            b.Road(Leg(i, 0), Leg(i, 1), highway: types[i]);
        }

        // The connectors: a semicircular chain of nodes between the EXIT of leg
        // i (always its Leg(i,1) node, since legs are built in driving order)
        // and the ENTRY of leg i+1 (always Leg(i+1,0)). Even legs exit in the
        // north, so their join bulges up; odd joins bulge down.
        const int ARC = 8;
        for (int i = 0; i + 1 < LEGS; i++)
        {
            var x0 = i * PITCH;
            var x1 = (i + 1) * PITCH;
            bool topJoin = i % 2 == 0;
            var cy = topJoin ? LEG : 0.0;
            var cx = (x0 + x1) / 2.0;
            var prev = Leg(i, 1);
            for (int k = 1; k < ARC; k++)               // k == ARC is the entry node itself
            {
                var a = Math.PI * (1.0 - (double)k / ARC);          // 180deg -> 0deg
                var px = cx + R * Math.Cos(a);
                var py = cy + (topJoin ? R : -R) * Math.Sin(a);
                var nid = $"swj{i}_{k}";
                b.Node(nid, px, py);
                b.Road(prev, nid, highway: types[i + 1]);   // the bend belongs to the road it leads into
                prev = nid;
            }
            b.Road(prev, Leg(i + 1, 0), highway: types[i + 1]);
        }

        b.Start("swatch_drive", Leg(0, 0));             // one drive through every material
        return b.Build();
    }

    public static RoadNetwork BuildBasicTestMap()
    {
        var b = new MapBuilder();
        const double TILE = 500.0; // pitch between tiles, meters

        static (double X, double Y) Origin(int col, int row) => (col * TILE, row * TILE);

        // --- Tile (0,0): Straight road (north-south) ---
        // Split into TWO segments (2026-10-19): the obstruction tests (R32
        // overtake + single-file priority) need a car to start on one stretch
        // and pass an obstruction on the NEXT one, so arrival can be judged
        // by segment - on a single 300 m segment "reached the end segment"
        // would be true at the spawn.
        var (ox, oy) = Origin(0, 0);
        b.Node("straight_n", ox + 100, oy + 350);   // north (large y)
        b.Node("straight_m", ox + 100, oy + 200);   // mid node (splits the road)
        b.Node("straight_s", ox + 100, oy + 50);    // south (small y)
        b.Road("straight_n", "straight_m");
        b.Road("straight_m", "straight_s");
        b.Start("straight", "straight_n");           // spawn at north, heading south
        b.Start("straight_reverse", "straight_s");   // spawn at south, heading north
        // The R32 obstruction scenarios start here (same north end, heading
        // south); they get their OWN names so the suite can select them by
        // name and keep their results apart from the plain straight run.
        b.Start("park_block_overtake", "straight_n");
        b.Start("park_block_priority", "straight_n");
        b.Start("slow_pass_overtake", "straight_n");
        b.Start("slow_pass_priority", "straight_n");

        // --- Tile (4,0): Widths road - four 50 m sections, one width each ---
        // Straight dead-end street, north to south: 13 m, 9 m, 7 m, 4 m.
        // U-turn scenarios run in the widest section; the narrow sections are
        // for future "too tight -> back up and retry" tests.
        (ox, oy) = Origin(4, 0);
        // Transitions are ANGLED (not all straight) to test the taper on
        // bent width changes: widths_139 = 180 deg (straight), widths_97 =
        // 150 deg (30 deg bend, right), widths_74 = 120 deg (60 deg bend,
        // back left). Node coords follow 50 m segments at those headings.
        b.Node("widths_n", ox + 100, oy + 300);      // north dead end (13 m)
        b.Node("widths_139", ox + 100, oy + 250);    // 13 -> 9  (180 deg)
        b.Node("widths_97", ox + 100, oy + 200);     // 9 -> 7   (150 deg bend)
        b.Node("widths_74", ox + 125, oy + 156.7);   // 7 -> 4   (120 deg bend)
        b.Node("widths_s", ox + 100, oy + 113.4);    // south dead end (4 m)
        b.Road("widths_n", "widths_139", width: 13.0);
        b.Road("widths_139", "widths_97", width: 9.0);
        b.Road("widths_97", "widths_74", width: 7.0);
        b.Road("widths_74", "widths_s", width: 4.0);
        b.Start("widths", "widths_n");             // spawn at north, heading south

        // --- Tile (1,0): 90 deg RIGHT turn (approach heading south) ---
        // Come down from the north, turn right (WEST) at the corner.
        (ox, oy) = Origin(1, 0);
        b.Node("cornerR_n", ox + 350, oy + 350);
        b.Node("cornerR_c", ox + 350, oy + 100);
        b.Node("cornerR_w", ox + 100, oy + 100);
        b.Road("cornerR_n", "cornerR_c");
        b.Road("cornerR_c", "cornerR_w");
        b.Start("corner_right_entry", "cornerR_n");
        b.Start("corner_right_exit", "cornerR_w");

        // --- Tile (2,0): 90 deg LEFT turn (approach heading south) ---
        // Come down from the north, turn left (EAST) at the corner.
        (ox, oy) = Origin(2, 0);
        b.Node("cornerL_n", ox + 100, oy + 350);
        b.Node("cornerL_c", ox + 100, oy + 100);
        b.Node("cornerL_e", ox + 350, oy + 100);
        b.Road("cornerL_n", "cornerL_c");
        b.Road("cornerL_c", "cornerL_e");
        b.Start("corner_left_entry", "cornerL_n");
        b.Start("corner_left_exit", "cornerL_e");

        // --- Tile (3,0): T-junction (3-way, perpendicular) ---
        // Stem comes down from the north onto a west-east bar.
        (ox, oy) = Origin(3, 0);
        b.Node("tjunc_top", ox + 250, oy + 350);
        b.Node("tjunc_center", ox + 250, oy + 100);
        b.Node("tjunc_w", ox + 80, oy + 100);
        b.Node("tjunc_e", ox + 420, oy + 100);
        b.Road("tjunc_top", "tjunc_center");
        b.Road("tjunc_center", "tjunc_w");
        b.Road("tjunc_center", "tjunc_e");
        b.Start("tjunction_from_top", "tjunc_top");
        b.Start("tjunction_from_west", "tjunc_w");
        b.Start("tjunction_from_east", "tjunc_e");

        // --- Tile (0,1): Y-intersection (shallow diverging angles) ---
        // Stem comes down from the north, forks to the south-west and
        // south-east (a shallow "Y").
        (ox, oy) = Origin(0, 1);
        b.Node("y_stem", ox + 250, oy + 400);
        b.Node("y_center", ox + 250, oy + 220);
        b.Node("y_sw", ox + 100, oy + 50);
        b.Node("y_se", ox + 400, oy + 50);
        b.Road("y_stem", "y_center");
        b.Road("y_center", "y_sw");
        b.Road("y_center", "y_se");
        b.Start("y_from_stem", "y_stem");
        b.Start("y_from_sw", "y_sw");
        b.Start("y_from_se", "y_se");

        // --- Tile (1,1): 4-way intersection (crossroads) ---
        (ox, oy) = Origin(1, 1);
        b.Node("cross_center", ox + 250, oy + 220);
        b.Node("cross_n", ox + 250, oy + 400);
        b.Node("cross_s", ox + 250, oy + 50);
        b.Node("cross_w", ox + 80, oy + 220);
        b.Node("cross_e", ox + 420, oy + 220);
        b.Road("cross_n", "cross_center");
        b.Road("cross_center", "cross_s");
        b.Road("cross_w", "cross_center");
        b.Road("cross_center", "cross_e");
        b.Start("crossroads_from_north", "cross_n");
        b.Start("crossroads_from_south", "cross_s");
        b.Start("crossroads_from_west", "cross_w");
        b.Start("crossroads_from_east", "cross_e");

        // --- Tile (2,1): One-way street through a 4-way junction ---
        // East-west road is one-way (west -> east only); north-south is two-way.
        (ox, oy) = Origin(2, 1);
        b.Node("ow_center", ox + 250, oy + 220);
        b.Node("ow_w", ox + 80, oy + 220);
        b.Node("ow_e", ox + 420, oy + 220);
        b.Node("ow_n", ox + 250, oy + 400);
        b.Node("ow_s", ox + 250, oy + 50);
        b.Road("ow_w", "ow_center", oneway: true);
        b.Road("ow_center", "ow_e", oneway: true);
        b.Road("ow_n", "ow_center");
        b.Road("ow_center", "ow_s");
        b.Start("oneway_entry", "ow_w");                // legal: flows with the one-way
        b.Start("oneway_wrong_way", "ow_e");            // illegal: against the one-way
        b.Start("oneway_cross_from_north", "ow_n");
        b.Start("oneway_cross_from_south", "ow_s");

        // --- Tile (3,1): S-curve (gentle degree-2 bends) ---
        (ox, oy) = Origin(3, 1);
        b.Node("s_p0", ox + 100, oy + 400);
        b.Node("s_p1", ox + 150, oy + 300);
        b.Node("s_p2", ox + 280, oy + 220);
        b.Node("s_p3", ox + 350, oy + 120);
        b.Node("s_p4", ox + 400, oy + 50);
        b.Road("s_p0", "s_p1");
        b.Road("s_p1", "s_p2");
        b.Road("s_p2", "s_p3");
        b.Road("s_p3", "s_p4");
        b.Start("s_curve", "s_p0");
        b.Start("s_curve_reverse", "s_p4");

        // --- Tile (0,2): Dead-end ---
        (ox, oy) = Origin(0, 2);
        b.Node("dead_start", ox + 250, oy + 350);
        b.Node("dead_end", ox + 250, oy + 100);
        b.Road("dead_start", "dead_end");
        b.Start("dead_end_approach", "dead_start");

        // --- Tile (1,2): Tight hairpin turn (~150 deg direction change) ---
        // Come down from the north, then fold back up to the east.
        (ox, oy) = Origin(1, 2);
        b.Node("hair_a", ox + 100, oy + 350);
        b.Node("hair_corner", ox + 100, oy + 100);
        b.Node("hair_b", ox + 160, oy + 340);
        b.Road("hair_a", "hair_corner");
        b.Road("hair_corner", "hair_b");
        b.Start("hairpin_entry", "hair_a");
        b.Start("hairpin_exit", "hair_b");

        // --- Tile (2,2): EMPTY (2026-10-19) ---
        // The wide sweeping-curve track was deleted with its test (#15): the
        // scenarios that need a bend now run on the hairpin and the S-curve,
        // and the two-lane obstruction tests run on the straight road. The
        // tile stays empty so nothing shifts; segment indices are irrelevant
        // anyway now that tests anchor on segment NAMES.

        // --- Tile (3,2): Roundabout (one-way ring, 4 two-way spokes) ---
        (ox, oy) = Origin(3, 2);
        double cx = ox + 250, cy = oy + 220;
        const double R = 100.0;
        const double SPOKE = 150.0; // distance from ring out to each approach's far end

        // 64-node ring (every 5.625 deg) for a smooth curve. One-way,
        // COUNTER-CLOCKWISE in this north-up frame (N -> W -> S -> E), the
        // correct direction for right-hand traffic (Germany): the central
        // island stays on your LEFT as you go around. Many nodes = very short
        // straight chords = the curvature is detected on nearly every chord,
        // so the speed profile slows the car down for the ring.
        const int N_RING = 64;
        var ringNodes = new List<string>(N_RING);
        for (int i = 0; i < N_RING; i++)
        {
            // Start at north (90 deg) and go counter-clockwise (increasing
            // angle in standard math = counter-clockwise in north-up frame).
            double ang = Math.PI / 180.0 * (90 + i * (360.0 / N_RING));
            string name = $"rb_r{i}";
            b.Node(name, cx + R * Math.Cos(ang), cy + R * Math.Sin(ang));
            ringNodes.Add(name);
        }
        for (int i = 0; i < N_RING; i++)
            b.Road(ringNodes[i], ringNodes[(i + 1) % N_RING], oneway: true);
        // Four two-way spokes, one per cardinal ring node. With 64 ring nodes
        // (every 5.625 deg), the cardinal nodes are rb_r0 (north, 90 deg),
        // rb_r16 (west, 180 deg), rb_r32 (south, 270 deg), rb_r48 (east, 0 deg).
        b.Node("rb_north_far", cx, cy + R + SPOKE);
        b.Node("rb_east_far", cx + R + SPOKE, cy);
        b.Node("rb_south_far", cx, cy - R - SPOKE);
        b.Node("rb_west_far", cx - R - SPOKE, cy);
        b.Road("rb_north_far", "rb_r0");
        b.Road("rb_east_far", "rb_r48");
        b.Road("rb_south_far", "rb_r32");
        b.Road("rb_west_far", "rb_r16");

        b.Start("roundabout_from_north", "rb_north_far");
        b.Start("roundabout_from_east", "rb_east_far");
        b.Start("roundabout_from_south", "rb_south_far");
        b.Start("roundabout_from_west", "rb_west_far");

        // --- Tile (0,3): Sliver junction (the real-world 815 -> 1008 layout) ---
        // A very SHORT approach (4.16 m) into a 4-way junction. The junction
        // has a near-straight continuation, a sharp-right exit and a sharp-left
        // exit - all 7 m wide. The approach is far too short to plan a turn in
        // advance, which is exactly what made the rail model crash here.
        (ox, oy) = Origin(0, 3);
        cx = ox + 250; cy = oy + 250;
        b.Node("sliv_ap", cx - 0.73, cy + 4.16);    // sliver approach (north)
        b.Node("sliv_junc", cx, cy);                // the 4-way junction
        b.Node("sliv_str", cx + 0.74, cy - 4.93);   // straight continuation (south)
        b.Node("sliv_w", cx - 36.2, cy - 5.4);      // sharp-right exit (west)
        b.Node("sliv_e", cx + 19.6, cy + 2.7);      // sharp-left exit (east)
        b.Road("sliv_ap", "sliv_junc");
        b.Road("sliv_junc", "sliv_str");
        b.Road("sliv_w", "sliv_junc");
        b.Road("sliv_junc", "sliv_e");
        b.Start("sliver_approach", "sliv_ap");   // spawn on the sliver, heading for the junction
        b.Start("sliver_from_west", "sliv_w");   // spawn on the sharp-right exit
        b.Start("sliver_from_east", "sliv_e");   // spawn on the sharp-left exit

        // --- Tile (1,3): Figure-8 - ONE continuous loop crossing itself ---
        // A single closed circuit shaped like an 8: it goes around the left
        // lobe, crosses at P = (750, 1750), goes around the right lobe,
        // crosses again and closes. The crossing is modelled with TWO nodes
        // at the SAME point: n12 on the NE-SW branch, n36 on the NW-SE
        // branch. Every node has degree 2 (no junction logic needed - the
        // driver simply drives straight through), yet the whole figure-8 is
        // ONE connected cycle. The NE-SW branch (through n12) is a BRIDGE:
        // segments 10..13 are level 1, so a car on that branch passes OVER
        // the other branch.
        (ox, oy) = Origin(1, 3);
        double px = ox + 250, py = oy + 250;       // the crossing point P
        // The loop is a sampled LEMNISCATE (Gerono curve): x = cos t,
        // y = sin t * cos t - smooth everywhere, crosses itself at exactly
        // one point with a 90-degree angle. 48 samples keep every chord
        // short enough that the spline stays smooth. The two crossing
        // passages are separate nodes n12 (t=90deg) and n36 (t=270deg) at
        // the SAME point P - both degree 2, so no junction logic is needed,
        // yet the whole figure-8 is one connected cycle.
        const int N_FIG8 = 48;
        for (int i = 0; i < N_FIG8; i++)
        {
            double t = 2 * Math.PI * i / N_FIG8;
            b.Node($"fig8_n{i}",
                   Math.Round(px + 245 * Math.Cos(t), 1),
                   Math.Round(py + 230 * Math.Sin(t) * Math.Cos(t), 1));
        }
        // The full cycle: n_i -> n_{i+1} (and n47 -> n0). Segments 10..13
        // (n10->n11 ... n13->n14) form the NE-SW branch through n12 =
        // the BRIDGE: the two crossing pieces plus one segment each side.
        for (int i = 0; i < N_FIG8; i++)
            b.Road($"fig8_n{i}", $"fig8_n{(i + 1) % N_FIG8}",
                   level: i is >= 10 and <= 13 ? 1 : 0);
        b.Start("fig8", "fig8_n24", facing: "fig8_n25");   // leftmost point, heading north
        // ~19 m before the crossing on the GROUND branch (t=262.5deg): spawns
        // right before the car dives under the bridge deck.
        b.Start("fig8_under", "fig8_n35", facing: "fig8_n36");
        // Both at the crossing point P itself: n36 is the GROUND branch node
        // (car renders UNDER the deck - hidden), n12 the ELEVATED one (car on
        // top of the deck - visible). A/B pair for the occlusion check.
        b.Start("fig8_deck", "fig8_n36", facing: "fig8_n37");
        b.Start("fig8_bridge", "fig8_n12", facing: "fig8_n13");

        // --- Tile (5,0): EMPTY (2026-10-19) ---
        // The WWW zig-zag track is deleted: its sharp kinks are covered by the
        // corners and the hairpin, and the smoothed-geometry claim they were
        // meant to show has the S-curve and the sweeping bends elsewhere.

        // --- Tile (4,1): Parking avenue (2 driving + 1 parking lane per side) ---
        // 300 m straight two-way street, 19.4 m wide: per direction two 3.5 m
        // driving lanes plus a 2.7 m parking lane at the kerb (6 lanes total).
        // Solid centreline (crossing it = oncoming lane), painted P marks in
        // both parking lanes. Offsets are relative to the normal driving
        // position (centre of the outermost driving lane, 5.25 m right of the
        // centreline), positive = toward the right kerb:
        //   -3.50  -> middle of the left (overtaking) driving lane
        //     0.0  -> middle of the right (normal) driving lane
        //   +3.10  -> middle of the parking lane (8.35 m from centreline)
        (ox, oy) = Origin(4, 1);
        b.Node("pkw_n", ox + 100, oy + 350);
        b.Node("pkw_s", ox + 100, oy + 50);
        b.Road("pkw_n", "pkw_s", lanes: 2, width: 19.4, parkingLaneWidth: 2.7);
        b.Start("park_6lane_left_lane", "pkw_n", lateralOffsetM: -3.50);
        b.Start("park_6lane_right_lane", "pkw_n");
        b.Start("park_6lane_parking", "pkw_n", lateralOffsetM: +3.10);

        // --- Tile (4,2): One-way INTO a 4-way junction ---
        // W spoke is one-way heading INTO the junction (travel west->east);
        // N/E/S spokes are two-way. Entering from the west, the car should
        // swing slightly right across the junction so it ends up in its own
        // (right) lane on the two-way east side.
        (ox, oy) = Origin(4, 2);
        b.Node("mixin_center", ox + 250, oy + 220);
        b.Node("mixin_w", ox + 80, oy + 220);
        b.Node("mixin_e", ox + 420, oy + 220);
        b.Node("mixin_n", ox + 250, oy + 400);
        b.Node("mixin_s", ox + 250, oy + 50);
        b.Road("mixin_w", "mixin_center", oneway: true);   // into the junction
        b.Road("mixin_center", "mixin_e");                 // two-way exit (test target)
        b.Road("mixin_n", "mixin_center");
        b.Road("mixin_center", "mixin_s");
        b.Start("mixed_from_west", "mixin_w");

        // --- Tile (4,3): One-way OUT of a 4-way junction ---
        // W spoke is one-way heading AWAY from the junction (travel
        // east->west); N/E/S spokes are two-way. Entering from the east
        // (two-way), the car crosses the junction and eases onto the narrow
        // one-way exit, where there is no oncoming lane to keep clear of.
        (ox, oy) = Origin(4, 3);
        b.Node("mixout_center", ox + 250, oy + 220);
        b.Node("mixout_w", ox + 80, oy + 220);
        b.Node("mixout_e", ox + 420, oy + 220);
        b.Node("mixout_n", ox + 250, oy + 400);
        b.Node("mixout_s", ox + 250, oy + 50);
        b.Road("mixout_center", "mixout_w", oneway: true); // one-way exit (test target)
        b.Road("mixout_e", "mixout_center");               // two-way approach from the east
        b.Road("mixout_n", "mixout_center");
        b.Road("mixout_center", "mixout_s");
        b.Start("mixed_from_east", "mixout_e");

        // --- Tile (2,3): Right-of-way figure-8 (real degree-4 crossing) ---
        // Same planar lemniscate as the standalone fig8_cross map, but placed
        // as a tile on the basic map so the right-of-way (Vorfahrt) scenario
        // lives alongside every other track. The self-crossing C is a REAL
        // 4-way junction: the SE-NW diagonal is the priority road, the SW-NE
        // diagonal must yield. Start point "fig8_xing" spawns at the loop's
        // leftmost point heading north.
        (ox, oy) = Origin(2, 3);
        AddPlanarFig8(b, "xing_", "fig8_xing", rightOfWay: true,
                      px: ox + 250, py: oy + 250);

        // --- Tile (3,3): Un-signed figure-8 (real degree-4 crossing) ---
        // Same geometry as tile (2,3) but with NO signs at the crossing, so
        // the engine's default right-before-left (Rechts vor Links) governs
        // the self-crossing. Start point "fig8_xing_plain" spawns at the
        // loop's leftmost point heading north.
        (ox, oy) = Origin(3, 3);
        AddPlanarFig8(b, "xingp_", "fig8_xing_plain", rightOfWay: false,
                      px: ox + 250, py: oy + 250);

        return b.Build();
    }

    /// <summary>Add a planar figure-8 (real degree-4 crossing) to an existing
    /// builder, centred on the crossing point (px, py). The self-crossing is a
    /// REAL degree-4 junction (all segments ground level): a driver with no
    /// destination and throttle held takes the straight continuation at the
    /// crossing, so it drives the full figure-8 loop forever - both lobes,
    /// crossing in the middle like real traffic. 48 segments; the four spokes
    /// of the crossing are n11->C, C->n13, n35->C, C->n37. When rightOfWay is
    /// true, adds the Vorfahrt signs (SE-NW diagonal = Priority, SW-NE =
    /// Yield); false leaves the crossing un-signalled so the engine's
    /// right-before-left (ComesFromMyRight) governs. Used by the basic map's
    /// right-of-way tile (2,3).</summary>
    static void AddPlanarFig8(MapBuilder b, string prefix, string startName,
        bool rightOfWay, double px, double py)
    {
        const int N = 48;
        for (int i = 0; i < N; i++)
        {
            if (i == 12 || i == 36) continue;   // both replaced by C below
            double t = 2 * Math.PI * i / N;
            b.Node($"{prefix}n{i}",
                   Math.Round(px + 245 * Math.Cos(t), 1),
                   Math.Round(py + 230 * Math.Sin(t) * Math.Cos(t), 1));
        }
        string c = $"{prefix}c";
        b.Node(c, px, py);
        string Id(int i) => i is 12 or 36 ? c : $"{prefix}n{i}";
        for (int i = 0; i < N; i++)
            b.Road(Id(i), Id((i + 1) % N));
        if (rightOfWay)
        {
            // Road signs at the self-crossing: the SE-NW diagonal (arms
            // n11/n13) is the PRIORITY road; cars entering C from the SW-NE
            // diagonal (arms n35/n37) must YIELD. Without this, dense two-way
            // flow deadlocks at C (right-before-left alone cannot untangle
            // leaders stopped at the mouth with followers packed behind).
            b.Sign($"{prefix}n11", c, SignType.Priority);
            b.Sign($"{prefix}n13", c, SignType.Priority);
            b.Sign($"{prefix}n35", c, SignType.Yield);
            b.Sign($"{prefix}n37", c, SignType.Yield);
        }
        b.Start(startName, $"{prefix}n24", facing: $"{prefix}n25");   // leftmost point
    }
}
