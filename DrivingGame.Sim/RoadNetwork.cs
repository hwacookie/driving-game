using System;
using System.Collections.Generic;
using System.Linq;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Prepared;
using NetTopologySuite.Operation.Buffer;
using NetTopologySuite.Operation.OverlayNG;
using NetTopologySuite.Operation.Union;

namespace DrivingGame.Sim;

/// <summary>A single road segment between two nodes. Ported from
/// road_network.py (mutable: SnapEndpoints rewrites the endpoints).</summary>
public class RoadSegment
{
    public int Id;
    public double X1, Y1, X2, Y2;   // world pixel coords
    public string Highway = "";
    public bool Oneway;
    public double Width;            // metres
    public string StartNode = "";   // node id at (x1, y1)
    public string EndNode = "";     // node id at (x2, y2)
    public double Length;           // metres
    /// <summary>DRIVING lanes per direction of travel; &gt; 0 marks a
    /// multi-lane carriageway. One-way: total = lanes; two-way: total = 2×lanes.</summary>
    public int Lanes;
    public double Shoulder;         // metres of stop lane on the right
    /// <summary>Vertical LEVEL: 0 = ground, 1 = bridge over the ground,
    /// 2 = bridge over a bridge, -1 = tunnel. Levels decide rendering
    /// order/style and that cars on different levels never collide.</summary>
    public int Level;
    /// <summary>&gt; 0 = the outermost right lane is a PARKING LANE (at each
    /// kerb). Drives painted P marks + parking-lane boundary line, which end
    /// &gt;= Config.PARK_LANE_END_GAP_M before any junction.</summary>
    public double ParkingLaneWidth;

    public RoadSegment Clone() => (RoadSegment)MemberwiseClone();
}

/// <summary>Parsed OSM data as returned by the loader (cache JSON or DB).</summary>
public sealed class OsmData
{
    public Dictionary<string, (double Lat, double Lon)> Nodes { get; init; } = new();
    public List<OsmWay> Ways { get; init; } = new();
}

public sealed record OsmWay(int Id, List<string> Nodes, string Highway, bool Oneway);

/// <summary>One polygon: exterior ring + holes (world pixels).</summary>
public sealed record PolygonRing(List<(double X, double Y)> Exterior,
    List<List<(double X, double Y)>> Holes);

/// <summary>A color group of road polygons for rendering.</summary>
public sealed record RoadPolygonGroup((int R, int G, int B) Color, List<PolygonRing> Rings);

/// <summary>
/// The road graph with nodes, segments and connectivity. Ported from
/// /Users/hauke/prj/car/src/road_network.py.
/// </summary>
public class RoadNetwork
{
    public Dictionary<string, (double X, double Y)> Nodes { get; private set; } = new();
    public List<RoadSegment> Segments { get; private set; } = new();
    public double OriginLat { get; init; }
    public double OriginLon { get; init; }
    public double WorldWidth { get; init; }
    public double WorldHeight { get; init; }
    public Dictionary<string, List<int>> NodeConnections { get; private set; } = new();  // node_id -> [segment_indices]
    public Dictionary<string, int> NodeDegree { get; private set; } = new();            // node_id -> connection count
    public Dictionary<string, (double HalfWidthPx, string Highway)> NodeMaxWidth { get; private set; } = new();
    /// <summary>name → (x, y, heading, seg_idx, forward, lateral_offset_m).</summary>
    public Dictionary<string, (double X, double Y, double Heading, int SegIdx, bool Forward, double LateralOffsetM)> StartPoints { get; } = new();

    // Lazy caches (the road network never changes at runtime).
    public SmoothGeometry.SmoothedNetwork? SmoothedCache { get; set; }
    /// <summary>Eroded paved polygon for the raceline corridor (mirrors
    /// raceline.py's _raceline_safe). Built once on first use by
    /// <see cref="Raceline.LegalCorridor"/>; tests may inject a pre-built one.</summary>
    public Geometry? RacelineSafe { get; set; }
    /// <summary>Prepared (fast contains) form of <see cref="RacelineSafe"/>.
    /// Mirrors raceline.py's _raceline_safe_prep.</summary>
    public IPreparedGeometry? RacelineSafePrep { get; set; }
    private List<RoadPolygonGroup>? _roadPolygonsCache;
    private (Geometry? Deck, Geometry? Roadway)? _elevatedGeomCache;
    private List<List<(double X, double Y)>>? _elevatedCenterlinesCache;
    private List<List<(double X, double Y)>>? _elevatedEdgeLinesCache;
    private Geometry? _pavedPolygonCache;
    private List<List<(double X, double Y)>>? _centerlinesCache;
    private List<List<(double X, double Y)>>? _markingCenterlinesCache;
    private List<(string Style, List<(double X, double Y)> Coords, double WidthM)>? _laneMarkingsCache;
    private List<List<(double X, double Y)>>? _onewayArrowsCache;
    private List<List<(double X, double Y)>>? _parkingMarksCache;

    public RoadNetwork() { }

    /// <summary>Look up a named, deterministic start point (defined by
    /// synthetic test maps). Positive lateral offset = toward the right kerb.</summary>
    public (double X, double Y, double Heading, int SegIdx, bool Forward, double LateralOffsetM) GetStartPoint(string name)
    {
        if (!StartPoints.TryGetValue(name, out var sp))
            throw new KeyNotFoundException($"No start point named '{name}'. Available: " +
                (StartPoints.Count > 0 ? string.Join(", ", StartPoints.Keys.OrderBy(k => k)) : "(none defined)"));
        return sp;
    }

    // --- Construction ---------------------------------------------------------

    /// <summary>Build a RoadNetwork from parsed OSM data.</summary>
    public static RoadNetwork FromOsmData(OsmData data, double north, double south, double west, double east)
    {
        double pppm = Config.PIXELS_PER_METER;

        // Project all nodes.
        var nodes = new Dictionary<string, (double X, double Y)>();
        foreach (var (nid, n) in data.Nodes)
            nodes[nid] = RoadNetworkGeometry.LatLonToWorld(n.Lat, n.Lon, south, west, pppm);

        // Build segments with node references.
        var segments = new List<RoadSegment>();
        var segNodeIds = new List<(string N1, string N2)>();
        foreach (var way in data.Ways)
        {
            for (int i = 0; i < way.Nodes.Count - 1; i++)
            {
                string n1 = way.Nodes[i], n2 = way.Nodes[i + 1];
                if (!nodes.TryGetValue(n1, out var p1) || !nodes.TryGetValue(n2, out var p2))
                    continue;

                double width;
                if (Config.ROAD_TYPES.TryGetValue(way.Highway, out var roadCfg))
                    width = way.Oneway ? roadCfg.Width1Way : roadCfg.Width2Way;
                else
                    width = 3.5;

                double segLength = Math.Hypot(p2.X - p1.X, p2.Y - p1.Y) / pppm;

                segments.Add(new RoadSegment
                {
                    Id = way.Id,
                    X1 = p1.X, Y1 = p1.Y, X2 = p2.X, Y2 = p2.Y,
                    Highway = way.Highway,
                    Oneway = way.Oneway,
                    Width = width,
                    StartNode = n1,
                    EndNode = n2,
                    Length = segLength,
                });
                segNodeIds.Add((n1, n2));
            }
        }

        // Snap dangling endpoints onto nearby roads.
        int snapped = RoadNetworkGeometry.SnapEndpoints(segments, pppm);
        if (snapped > 0)
            Console.WriteLine($"  Snapped {snapped} dangling endpoints");

        // Recompute node positions from snapped endpoints.
        var nodeSum = new Dictionary<string, (double Sx, double Sy, int C)>();
        var nodeDegree = new Dictionary<string, int>();
        for (int i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            foreach (var (nid, x, y) in new[]
            {
                (segNodeIds[i].N1, seg.X1, seg.Y1),
                (segNodeIds[i].N2, seg.X2, seg.Y2),
            })
            {
                var s = nodeSum.TryGetValue(nid, out var cur) ? cur : (Sx: 0.0, Sy: 0.0, C: 0);
                nodeSum[nid] = (s.Sx + x, s.Sy + y, s.C + 1);
                nodeDegree[nid] = nodeDegree.GetValueOrDefault(nid, 0) + 1;
            }
        }
        foreach (var (nid, (sx, sy, c)) in nodeSum)
            nodes[nid] = (sx / c, sy / c);

        // Build node_connections: which segment indices touch each node.
        var nodeConnections = new Dictionary<string, List<int>>();
        for (int idx = 0; idx < segments.Count; idx++)
        {
            var seg = segments[idx];
            if (!nodeConnections.TryGetValue(seg.StartNode, out var l1)) nodeConnections[seg.StartNode] = l1 = new List<int>();
            l1.Add(idx);
            if (!nodeConnections.TryGetValue(seg.EndNode, out var l2)) nodeConnections[seg.EndNode] = l2 = new List<int>();
            l2.Add(idx);
        }

        // Junction info: widest road at each node (for rendering). Skip
        // degree-2 nodes (straight road continuation).
        var nodeInfo = new Dictionary<string, (double HalfWidthPx, string Highway)>();
        foreach (var seg in segments)
        {
            double half = (seg.Width / 2) * pppm;
            foreach (string nid in new[] { seg.StartNode, seg.EndNode })
            {
                if (!nodeInfo.TryGetValue(nid, out var cur) || half > cur.HalfWidthPx)
                    nodeInfo[nid] = (half, seg.Highway);
            }
        }
        nodeInfo = nodeInfo.Where(kv => nodeDegree.GetValueOrDefault(kv.Key, 0) != 2).ToDictionary(kv => kv.Key, kv => kv.Value);

        double worldWidth = RoadNetworkGeometry.LatLonToWorld(south, east, south, west, pppm).X;
        double worldHeight = RoadNetworkGeometry.LatLonToWorld(north, west, south, west, pppm).Y;

        return CreateFromParts(nodes, segments, south, west,
            worldWidth, worldHeight, nodeConnections, nodeDegree, nodeInfo);
    }

    internal static RoadNetwork CreateFromParts(
        Dictionary<string, (double X, double Y)> nodes, List<RoadSegment> segments,
        double originLat, double originLon, double worldWidth, double worldHeight,
        Dictionary<string, List<int>> nodeConnections, Dictionary<string, int> nodeDegree,
        Dictionary<string, (double HalfWidthPx, string Highway)> nodeMaxWidth)
    {
        var net = new RoadNetwork
        {
            OriginLat = originLat, OriginLon = originLon,
            WorldWidth = worldWidth, WorldHeight = worldHeight,
        };
        net.Nodes = nodes;
        net.Segments = segments;
        net.NodeConnections = nodeConnections;
        net.NodeDegree = nodeDegree;
        net.NodeMaxWidth = nodeMaxWidth;
        return net;
    }

    // --- Graph queries --------------------------------------------------------

    public List<int> GetConnectedSegments(string nodeId) =>
        NodeConnections.TryGetValue(nodeId, out var l) ? l : new List<int>();

    /// <summary>Turning angle (degrees, negative=left, positive=right) when
    /// going from one segment to another at a shared node.</summary>
    public double GetExitAngle(int fromSegIdx, int toSegIdx)
    {
        var fromSeg = Segments[fromSegIdx];
        var toSeg = Segments[toSegIdx];

        string? shared = null;
        foreach (string n in new[] { fromSeg.StartNode, fromSeg.EndNode })
            if (n == toSeg.StartNode || n == toSeg.EndNode) { shared = n; break; }
        if (shared is null) return 0.0;

        // Entry direction: towards the shared node (along from_seg).
        var fn = fromSeg.StartNode != shared ? Nodes[fromSeg.StartNode] : Nodes[fromSeg.EndNode];
        var fs = Nodes[shared];
        double fromVx = fs.X - fn.X, fromVy = fs.Y - fn.Y;

        // Exit direction: away from shared node (along to_seg).
        var tn = toSeg.StartNode != shared ? Nodes[toSeg.StartNode] : Nodes[toSeg.EndNode];
        var ts = Nodes[shared];
        double toVx = tn.X - ts.X, toVy = tn.Y - ts.Y;

        double fromAngle = Math.Atan2(fromVx, fromVy) * 180.0 / Math.PI;
        double toAngle = Math.Atan2(toVx, toVy) * 180.0 / Math.PI;
        double diff = toAngle - fromAngle;
        // Normalize to (-180, 180].
        while (diff > 180) diff -= 360;
        while (diff <= -180) diff += 360;
        return diff;
    }

    /// <summary>Check if there's a road coming from the right at this junction
    /// (rechts vor links).</summary>
    public bool HasRightOfWayConflict(int fromSegIdx, string nodeId)
    {
        var connected = GetConnectedSegments(nodeId);
        if (connected.Count <= 2) return false; // not a real junction

        foreach (int idx in connected)
        {
            if (idx == fromSegIdx) continue;
            double angle = GetExitAngle(fromSegIdx, idx);
            // "From the right" means angle between -45° and -135°.
            if (angle >= -135 && angle <= -45) return true;
        }
        return false;
    }

    /// <summary>Choose the next segment when reaching a node.
    /// turnDirection: 'left', 'right', or 'straight'. Returns segment index
    /// or null if no suitable segment found.</summary>
    public int? ChooseNextSegment(int fromSegIdx, string nodeId, string turnDirection)
    {
        var connected = GetConnectedSegments(nodeId);
        if (connected.Count <= 1)
            return connected.Count == 1 ? fromSegIdx : null; // dead end / continuation

        var candidates = new List<(int Idx, double Angle)>();
        foreach (int idx in connected)
        {
            if (idx == fromSegIdx) continue;
            candidates.Add((idx, GetExitAngle(fromSegIdx, idx)));
        }
        if (candidates.Count == 0) return null;

        // For oneway roads, respect direction: a oneway segment may only be
        // ENTERED at its own start_node (entering at end_node would mean
        // driving it backward — broke badly on a roundabout's all-oneway ring).
        var filtered = candidates.Where(c =>
        {
            var seg = Segments[c.Idx];
            return !(seg.Oneway && seg.StartNode != nodeId);
        }).ToList();
        if (filtered.Count == 0) return null;

        if (turnDirection == "left")
        {
            var best = filtered.MinBy(c => c.Angle); // most negative (sharp left preferred)
            if (best.Angle < -10) return best.Idx;
        }
        else if (turnDirection == "right")
        {
            var best = filtered.MaxBy(c => c.Angle); // most positive (sharp right preferred)
            if (best.Angle > 10) return best.Idx;
        }
        else
        {
            var best = filtered.MinBy(c => Math.Abs(c.Angle)); // straight: smallest |angle|
            if (Math.Abs(best.Angle) < 30) return best.Idx;
        }

        // Fallback: if preferred turn not available, just go straight.
        return filtered.MinBy(c => Math.Abs(c.Angle)).Idx;
    }

    // --- Spatial queries ------------------------------------------------------

    /// <summary>Check if a world position is on any road. Uses the exact same
    /// paved-area polygon that gets rendered (rounded bends and junction
    /// fillets included) — not a cruder rectangle+circle approximation.</summary>
    public bool IsOnRoad(double wx, double wy)
    {
        double tolerancePx = Config.ROAD_EDGE_TOLERANCE_M * Config.PIXELS_PER_METER;
        return GetPavedPolygon().Distance(new Point(wx, wy)) <= tolerancePx;
    }

    /// <summary>Check if a 4-wheel car (not just its center point) is on the
    /// road: all FOUR CORNERS of the body box against the same paved polygon.
    /// North-up frame: heading 0 = north, forward = (sin h, cos h),
    /// right = (cos h, -sin h).</summary>
    public bool IsCarOnRoad(double wx, double wy, double headingDeg,
        double lengthM = 4.5, double widthM = 1.8)
    {
        double tolerancePx = Config.ROAD_EDGE_TOLERANCE_M * Config.PIXELS_PER_METER;
        double h = headingDeg * Math.PI / 180.0;
        double fx = Math.Sin(h), fy = Math.Cos(h);   // forward
        double rx = Math.Cos(h), ry = -Math.Sin(h);  // right
        double pppm = Config.PIXELS_PER_METER;
        double halfL = (lengthM / 2.0) * pppm;
        double halfW = (widthM / 2.0) * pppm;
        var poly = GetPavedPolygon();
        foreach (double sfx in new[] { 1.0, -1.0 })
            foreach (double srx in new[] { 1.0, -1.0 })
            {
                double cx = wx + (sfx * fx * halfL + srx * rx * halfW);
                double cy = wy + (sfx * fy * halfL + srx * ry * halfW);
                if (poly.Distance(new Point(cx, cy)) > tolerancePx)
                    return false;
            }
        return true;
    }

    // --- Paved-area geometry (shared by physics AND the renderer, so the
    // drivable area always exactly matches what's painted) ---

    /// <summary>Build (color, rings) groups for drawing. Holes matter for
    /// closed-loop roads (roundabout island). Cached.</summary>
    public List<RoadPolygonGroup> GetRoadPolygonsByColor() =>
        _roadPolygonsCache ??= RoadNetworkGeometry.BuildRoadPolygons(this);

    private (Geometry? Deck, Geometry? Roadway) ElevatedGeometry()
    {
        if (_elevatedGeomCache is not null) return _elevatedGeomCache.Value;

        // 1) Walk the elevated segments into ordered chains.
        var byNode = new Dictionary<string, List<int>>();
        for (int idx = 0; idx < Segments.Count; idx++)
        {
            if (Segments[idx].Level >= 1)
            {
                if (!byNode.TryGetValue(Segments[idx].StartNode, out var l1)) byNode[Segments[idx].StartNode] = l1 = new List<int>();
                l1.Add(idx);
                if (!byNode.TryGetValue(Segments[idx].EndNode, out var l2)) byNode[Segments[idx].EndNode] = l2 = new List<int>();
                l2.Add(idx);
            }
        }
        var seenSeg = new HashSet<int>();
        var chains = new List<(List<string> Ids, List<(double X, double Y)> Nodes, double MaxWidth)>();
        for (int startIdx = 0; startIdx < Segments.Count; startIdx++)
        {
            if (Segments[startIdx].Level < 1 || seenSeg.Contains(startIdx)) continue;
            var seg = Segments[startIdx];
            var ids = new List<string> { seg.StartNode, seg.EndNode };
            var widths = new List<double> { seg.Width };
            seenSeg.Add(startIdx);
            // Extend the chain in both directions while elevated.
            foreach (int direction in new[] { 0, 1 })
            {
                string cur = direction == 0 ? seg.StartNode : seg.EndNode;
                while (true)
                {
                    // First unused elevated segment at cur, or -1. (Neither
                    // FirstOrDefault nor FindIndex works: the former's default
                    // of 0 splices in segment 0 when all candidates are seen
                    // — reachable on degree-2 cycles; the latter returns the
                    // LIST POSITION, not the segment index.)
                    int i2 = -1;
                    if (byNode.TryGetValue(cur, out var l))
                        foreach (var j in l)
                            if (!seenSeg.Contains(j)) { i2 = j; break; }
                    if (i2 < 0) break;
                    var s2 = Segments[i2];
                    string other = s2.StartNode == cur ? s2.EndNode : s2.StartNode;
                    if (direction == 0) ids.Insert(0, other); else ids.Add(other);
                    widths.Add(s2.Width);
                    seenSeg.Add(i2);
                    cur = other;
                }
            }
            chains.Add((ids, ids.Select(i => Nodes[i]).ToList(), widths.Max()));
        }

        // 2) For each chain build a centripetal Catmull-Rom curve through the
        //    chain PLUS one context node at each end (so the interior tangents
        //    match the network spline), then slice between the chain's own end
        //    nodes' arc lengths.
        var adj = new Dictionary<string, HashSet<string>>();
        foreach (var s in Segments)
        {
            if (!adj.TryGetValue(s.StartNode, out var a1)) adj[s.StartNode] = a1 = new HashSet<string>();
            a1.Add(s.EndNode);
            if (!adj.TryGetValue(s.EndNode, out var a2)) adj[s.EndNode] = a2 = new HashSet<string>();
            a2.Add(s.StartNode);
        }

        var deckParts = new List<Geometry>();
        var roadParts = new List<Geometry>();
        var centerlines = new List<List<(double X, double Y)>>();
        var edgeLines = new List<List<(double X, double Y)>>();
        foreach (var (ids, chainNodes, width) in chains)
        {
            var ext = chainNodes.ToList();
            // One context neighbour at each end (the one NOT in the chain).
            foreach (var (nid, end) in new[] { (ids[0], 0), (ids[^1], -1) })
            {
                if (adj.TryGetValue(nid, out var nbrs))
                    foreach (string nbr in nbrs)
                        if (!ids.Contains(nbr))
                        {
                            if (end == 0) ext.Insert(0, Nodes[nbr]); else ext.Add(Nodes[nbr]);
                            break;
                        }
            }
            double pppm = Config.PIXELS_PER_METER;
            var curve = new SmoothGeometry.SmoothCurve(ext, pppm: pppm);
            double s0 = SmoothGeometry.SmoothedNetwork.NodeS(curve, chainNodes[0]);
            double s1 = SmoothGeometry.SmoothedNetwork.NodeS(curve, chainNodes[^1]);
            if (s0 > s1) (s0, s1) = (s1, s0);
            double halfWPx = (width / 2.0) * pppm;
            int n = Math.Max(2, (int)((s1 - s0) / (0.5 * pppm)) + 1);
            var pts = new List<(double X, double Y)>(n);
            for (int i = 0; i < n; i++)
                pts.Add(curve.PointAt(s0 + (s1 - s0) * i / (n - 1)));

            var line = ToLineString(pts);
            // Two surfaces from the same spline: full deck (carriageway +
            // BRIDGE_SIDEWALK_M per side, concrete) and the carriageway itself.
            deckParts.Add(line.Buffer(halfWPx + Config.BRIDGE_SIDEWALK_M * pppm,
                new BufferParameters(8, EndCapStyle.Flat, JoinStyle.Round, 5.0)));
            roadParts.Add(line.Buffer(halfWPx,
                new BufferParameters(8, EndCapStyle.Flat, JoinStyle.Round, 5.0)));
            centerlines.Add(pts);

            // Open white edge lines: centreline offset by (width/2 - inset),
            // one polyline per side, OPEN at the ends.
            double off = (width / 2.0 - Config.EDGE_LINE_INSET_M) * pppm;
            var left = new List<(double X, double Y)>(n);
            var right = new List<(double X, double Y)>(n);
            for (int i = 0; i < n; i++)
            {
                var (x0, y0) = pts[Math.Max(0, i - 1)];
                var (x1, y1) = pts[Math.Min(n - 1, i + 1)];
                double tx = x1 - x0, ty = y1 - y0;
                double tl = Math.Hypot(tx, ty); if (tl == 0) tl = 1.0;
                double nx = -ty / tl, ny = tx / tl;
                left.Add((pts[i].X + nx * off, pts[i].Y + ny * off));
                right.Add((pts[i].X - nx * off, pts[i].Y - ny * off));
            }
            edgeLines.Add(left); edgeLines.Add(right);
        }

        _elevatedCenterlinesCache = centerlines;
        _elevatedEdgeLinesCache = edgeLines;
        _elevatedGeomCache = deckParts.Count > 0
            ? (UnionAll(deckParts), UnionAll(roadParts))
            : (null, null);
        return _elevatedGeomCache.Value;
    }

    /// <summary>Unary union of many geometries. OverlayNGRobust handles the
    /// full road-polygon set (~1000 rings, 172k vertices) in ~1 s with the
    /// same result as the classic algorithm (which takes ~115 s); plain
    /// OverlayNG throws TopologyException on the near-coincident sliver
    /// edges at junctions. Falls back to UnaryUnionOp if anything fails.</summary>
    internal static Geometry UnionAll(IEnumerable<Geometry> geoms)
    {
        var list = geoms.ToList();
        if (list.Count == 0) return new GeometryCollection(Array.Empty<Geometry>());
        try
        {
            return OverlayNGRobust.Union(list);
        }
        catch (Exception)
        {
            return new UnaryUnionOp(list).Union();
        }
    }

    private static List<PolygonRing> RingsOf(Geometry? geom)
    {
        var result = new List<PolygonRing>();
        if (geom is null || geom.IsEmpty) return result;
        var polys = geom.Geometries().Length > 0 ? geom.Geometries() : new[] { geom };
        foreach (Geometry g in polys)
        {
            if (!(g is Polygon p) || p.IsEmpty) continue;
            result.Add(new PolygonRing(
                p.ExteriorRing.Coordinates.Select(c => (c.X, c.Y)).ToList(),
                p.InteriorRings.Select(r => r.Coordinates.Select(c => (c.X, c.Y)).ToList()).ToList()));
        }
        return result;
    }

    /// <summary>Full bridge DECKS incl. sidewalks (world pixels).</summary>
    public List<PolygonRing> GetElevatedPolygons() => RingsOf(ElevatedGeometry().Deck);

    /// <summary>The asphalt CARRIAGEWAY of every bridge (world pixels).</summary>
    public List<PolygonRing> GetElevatedRoadwayPolygons() => RingsOf(ElevatedGeometry().Roadway);

    /// <summary>Open white edge lines for the decks (world pixels).</summary>
    public List<List<(double X, double Y)>> GetElevatedEdgeLines()
    {
        ElevatedGeometry();
        return _elevatedEdgeLinesCache ?? new();
    }

    /// <summary>Centreline polylines of the elevated decks (world pixels).</summary>
    public List<List<(double X, double Y)>> GetElevatedCenterlines()
    {
        GetElevatedPolygons();
        return _elevatedCenterlinesCache ?? new();
    }

    /// <summary>A single unioned polygon covering the entire drivable paved
    /// area — the authoritative geometry for on-road checks, built from the
    /// exact same per-color polygons used for rendering. Cached.</summary>
    public Geometry GetPavedPolygon()
    {
        if (_pavedPolygonCache is not null) return _pavedPolygonCache;

        var polys = new List<Geometry>();
        foreach (var group in GetRoadPolygonsByColor())
            foreach (var ring in group.Rings)
                polys.Add(ToPolygon(ring));
        Geometry unioned = polys.Count > 0
            ? UnionAll(polys)
            : new GeometryCollection(Array.Empty<Geometry>());

        // unary_union leaves zero-area SLIVER rings at junctions (degenerate
        // rings whose vertices collapse onto a line). Invisible as fill, but
        // the paved-edge outline draws every ring — drop degenerate outer
        // parts and degenerate holes alike.
        var geoms = unioned.GeomType() == "MultiPolygon" ? unioned.Geometries() : new[] { unioned };
        var kept = new List<Geometry>();
        foreach (Geometry g in geoms)
        {
            if (!(g is Polygon p) || p.Area < 1.0) continue;
            var holes = p.InteriorRings
                .Where(r => ToPolygon(new PolygonRing(
                    r.Coordinates.Select(c => (c.X, c.Y)).ToList(), new())).Area >= 1.0)
                .Select(r => (LinearRing)r)
                .ToArray();
            kept.Add(new Polygon(p.Shell, holes));
        }
        _pavedPolygonCache = kept.Count > 1
            ? new MultiPolygon(kept.Cast<Polygon>().ToArray())
            : (kept.Count == 1 ? kept[0] : new GeometryCollection(Array.Empty<Geometry>()));
        return _pavedPolygonCache;
    }

    /// <summary>Point + heading at `distanceM` along the ACTUAL road path from
    /// a degree-1 node. Spawn placement must follow the corner-rounded merged
    /// centreline — not the segment's chord (at a sharp U-turn the two diverge
    /// by tens of metres). Returns null if no merged line ends within 30 m of
    /// the node.</summary>
    public (double X, double Y, double HeadingDeg)? SpawnPathPoint(int segIdx, (double X, double Y) nodeXy, double distanceM)
    {
        var groups = RoadNetworkGeometry.MergeAndRoundLines(this);
        double nx = nodeXy.X, ny = nodeXy.Y;
        List<(double X, double Y)>? bestLine = null;
        double bestD2 = double.PositiveInfinity;
        foreach (var lines in groups.Values)
            foreach (var coords in lines)
            {
                if (coords.Count < 2) continue;
                foreach (var end in new[] { coords[0], coords[^1] })
                {
                    double d2 = (end.X - nx) * (end.X - nx) + (end.Y - ny) * (end.Y - ny);
                    if (d2 < bestD2) { bestD2 = d2; bestLine = new List<(double X, double Y)>(coords); }
                }
            }
        if (bestLine is null || bestD2 > Math.Pow(30.0 * Config.PIXELS_PER_METER, 2))
            return null;
        if (bestLine[0].X != nx || bestLine[0].Y != ny)
            bestLine.Reverse(); // walk from the end at the node INTO the road

        double target = Math.Max(0.0, distanceM) * Config.PIXELS_PER_METER;
        double x = bestLine[0].X, y = bestLine[0].Y;
        double heading = 0.0;
        double remaining = target;
        for (int i = 0; i < bestLine.Count - 1; i++)
        {
            var (x0, y0) = bestLine[i];
            var (x1, y1) = bestLine[i + 1];
            double segLen = Math.Hypot(x1 - x0, y1 - y0);
            if (segLen < 1e-9) continue;
            if (remaining <= segLen || i == bestLine.Count - 2)
            {
                double t = Math.Min(1.0, remaining / segLen);
                x = x0 + (x1 - x0) * t;
                y = y0 + (y1 - y0) * t;
                heading = Math.Atan2(x1 - x0, y1 - y0) * 180.0 / Math.PI;
                break;
            }
            remaining -= segLen;
        }
        return (x, y, heading);
    }

    /// <summary>Merged, corner-rounded road CENTERLINES (no buffering), ALL
    /// two-way roads. Used by the LaneGuard. Cached.</summary>
    public List<List<(double X, double Y)>> GetCenterlines() =>
        _centerlinesCache ??= RoadNetworkGeometry.BuildCenterlines(this);

    /// <summary>Centerlines that actually GET a dashed white line drawn: only
    /// roads at least CENTERLINE_MIN_WIDTH_M wide. Cached.</summary>
    public List<List<(double X, double Y)>> GetMarkingCenterlines()
    {
        if (_markingCenterlinesCache is not null) return _markingCenterlinesCache;
        var groups = RoadNetworkGeometry.MergeAndRoundLines(this, onlyTwoWay: true,
            skipMultiLane: true, stopAtJunctions: true);
        var out_ = new List<List<(double X, double Y)>>();
        foreach (var ((_, w), lines) in groups)
        {
            if (w < Config.CENTERLINE_MIN_WIDTH_M) continue;
            foreach (var coords in lines)
            {
                // No paint on the crossing itself: stop the dashes just short
                // of the fillet corner at 3+-way junctions.
                var (t0, t1) = RoadNetworkGeometry.JunctionMarkingTrimPx(this, coords);
                var trimmed = RoadNetworkGeometry.TrimEnds(coords.ToList(), t0, t1);
                if (trimmed.Count > 0) out_.Add(trimmed);
            }
        }
        return _markingCenterlinesCache = out_;
    }

    /// <summary>Lane markings for multi-lane carriageways (lanes &gt; 0), per
    /// the German RQ 31 layout. Returns (style, coords, width_m) tuples. Cached.</summary>
    public List<(string Style, List<(double X, double Y)> Coords, double WidthM)> GetLaneMarkings() =>
        _laneMarkingsCache ??= RoadNetworkGeometry.BuildLaneMarkings(this);

    /// <summary>Painted direction arrows for one-way roads. Cached.</summary>
    public List<List<(double X, double Y)>> GetOnewayArrows() =>
        _onewayArrowsCache ??= RoadNetworkGeometry.BuildOnewayArrows(this);

    /// <summary>Painted 'P' letters in parking lanes. Cached.</summary>
    public List<List<(double X, double Y)>> GetParkingMarks() =>
        _parkingMarksCache ??= RoadNetworkGeometry.BuildParkingMarks(this);

    private readonly Random _rng = new();

    /// <summary>Return a random (x, y, heading, seg_idx, node_id) on any road
    /// segment. Heading in degrees, 0=up.</summary>
    public (double X, double Y, double Heading, int SegIdx, string NodeId) RandomRoadPoint()
    {
        int segIdx = _rng.Next(Segments.Count);
        var seg = Segments[segIdx];
        double t = _rng.NextDouble();
        double x = seg.X1 + t * (seg.X2 - seg.X1);
        double y = seg.Y1 + t * (seg.Y2 - seg.Y1);
        double heading = Math.Atan2(seg.X2 - seg.X1, seg.Y2 - seg.Y1) * 180.0 / Math.PI;
        return (x, y, heading, segIdx, seg.StartNode);
    }

    public (double Left, double Top, double Right, double Bottom) Bounds =>
        (0, 0, WorldWidth, WorldHeight);

    // --- NTS helpers ----------------------------------------------------------

    internal static LineString ToLineString(IEnumerable<(double X, double Y)> pts)
    {
        var arr = pts.Select(p => new Coordinate(p.X, p.Y)).ToArray();
        return new LineString(arr);
    }

    internal static Polygon ToPolygon(PolygonRing ring)
    {
        var exterior = NtsCompat.RingOf(ring.Exterior.Select(p => new Coordinate(p.X, p.Y)).ToArray());
        var holes = ring.Holes
            .Select(h => h.Select(p => new Coordinate(p.X, p.Y)).ToArray())
            .Where(h => h.Length >= 3)
            .Select(NtsCompat.RingOf)
            .ToArray();
        return holes.Length > 0 ? new Polygon(exterior, holes) : new Polygon(exterior);
    }
}
