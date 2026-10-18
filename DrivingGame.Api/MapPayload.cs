// /map payload builder — shared by BOTH hosts (Phase 7): the console host
// serves it via GET /map, the Godot app's embedded mode builds the same
// dict in-process for its renderer (no HTTP round-trip). Extracted from
// GameApi.cs on 2026-09-05. The payload shape is the renderer contract
// (MapRenderer.BuildMap) and must stay identical across hosts.

using DrivingGame.Sim;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Buffer;
using Math = System.Math;

namespace DrivingGame.Api;

public static class MapPayload
{
    public static Dictionary<string, object?> Build(RoadNetwork net)
    {
        double pppm = Config.PIXELS_PER_METER;

        List<List<double>> M(IEnumerable<(double X, double Y)> pts) =>
            pts.Select(p => new List<double> { Math.Round(p.X / pppm, 2), Math.Round(p.Y / pppm, 2) })
               .ToList();

        // Road PAINT, not the paved area: one ribbon per road (the two
        // boundary polylines) plus the fills that are not ribbons. The
        // renderer fills these directly, so the tarmac needs no holes and no
        // background punch (see RoadNetworkGeometry.BuildRoadPaint).
        var roadStrips = new List<Dictionary<string, object?>>();
        foreach (var s in net.GetRoadStrips())
            roadStrips.Add(new Dictionary<string, object?>
            {
                ["left"] = M(s.Left),
                ["right"] = M(s.Right),
            });
        var roadPolys = new List<Dictionary<string, object?>>();
        foreach (var group in net.GetRoadFillPolygons())
            foreach (var ring in group.Rings)
                roadPolys.Add(new Dictionary<string, object?>
                {
                    ["exterior"] = M(ring.Exterior),
                });

        // Paved-edge rings: the UNIONED paved polygon (unary_union), so
        // shared/interior buffer edges inside junctions are gone — only
        // the true outer perimeter + island holes remain. The rings are
        // offset INWARD by EDGE_LINE_INSET_M so a 15 cm tarmac shoulder
        // stays outside the white line (ALL roads).
        var pavedBuf = net.GetPavedPolygon().Buffer(
            -Config.EDGE_LINE_INSET_M * pppm,
            new BufferParameters(16, EndCapStyle.Round, JoinStyle.Round, 5.0));
        var pavedPolys = pavedBuf is MultiPolygon mp
            ? mp.Geometries.Cast<Polygon>().ToList()
            : new List<Polygon> { (Polygon)pavedBuf };
        var pavedEdgeRings = new List<List<List<double>>>();
        foreach (var p in pavedPolys)
        {
            pavedEdgeRings.Add(M(p.ExteriorRing.Coordinates.Select(c => (c.X, c.Y))));
            foreach (var hole in p.InteriorRings)
                pavedEdgeRings.Add(M(hole.Coordinates.Select(c => (c.X, c.Y))));
        }

        // Road signs: precomputed screen position (metres) + approach
        // heading so the renderer just draws. Placed 8 m before the signed
        // node, OUTSIDE the curb on the right of the direction of travel -
        // signs are painted on the side of the road, never on the pavement
        // (a sign in the driving lane reads as an obstacle).
        var signs = new List<Dictionary<string, object?>>();
        foreach (var ((segIdx, nodeId), type) in net.Signs)
        {
            if (!net.Nodes.TryGetValue(nodeId, out var nxy)) continue;
            var s = net.Segments[segIdx];
            // Direction of travel toward the signed node.
            double dx = (nodeId == s.EndNode ? s.X2 - s.X1 : s.X1 - s.X2) / pppm;
            double dy = (nodeId == s.EndNode ? s.Y2 - s.Y1 : s.Y1 - s.Y2) / pppm;
            double len = Math.Max(Math.Sqrt(dx * dx + dy * dy), 1e-6);
            double fx = dx / len, fy = dy / len;          // forward (toward node)
            double rx = fy, ry = -fx;                     // right of travel
            // CurbGapM keeps the (oversized, ~2.5 m wide) sign fully OFF the
            // pavement: half road width + gap - signHalfWidth > 0.
            const double BeforeNodeM = 8.0, CurbGapM = 1.5;
            // Right of the curb: half the road width plus a small gap onto
            // the shoulder, so the sign sits beside the lane, not in it.
            double sideOffsetM = s.Width / 2.0 + CurbGapM;
            double x = nxy.X / pppm - fx * BeforeNodeM + rx * sideOffsetM;
            double y = nxy.Y / pppm - fy * BeforeNodeM + ry * sideOffsetM;
            signs.Add(new Dictionary<string, object?>
            {
                ["x"] = Math.Round(x, 2), ["y"] = Math.Round(y, 2),
                ["heading_deg"] = Math.Round(Math.Atan2(fx, fy) * 180.0 / Math.PI, 1),
                ["type"] = type == SignType.Yield ? "yield" : "priority",
            });
        }

        var startPoints = new Dictionary<string, object?>();
        foreach (var (name, (x, y, hdg, seg, fwd, lat)) in net.StartPoints)
            startPoints[name] = new Dictionary<string, object?>
            {
                ["x"] = Math.Round(x / pppm, 2), ["y"] = Math.Round(y / pppm, 2),
                ["heading_deg"] = hdg, ["seg"] = seg,
                ["forward"] = fwd, ["lateral_offset_m"] = lat,
            };

        // Halt lines: a band of white across the yielding LANE at the mouth
        // of every yielding approach (see RoadNetwork.GetStopLines).
        var stopLines = new List<List<double>>();
        foreach (var (x1, y1, x2, y2) in net.GetStopLines())
            stopLines.Add(new List<double>
            {
                Math.Round(x1 / pppm, 2), Math.Round(y1 / pppm, 2),
                Math.Round(x2 / pppm, 2), Math.Round(y2 / pppm, 2),
            });

        // The small white disc in the middle of a real crossing (see
        // RoadNetwork.GetCrossingMarkers) - [x, y, radius] in metres.
        var crossingMarkers = new List<List<double>>();
        foreach (var (x, y, r) in net.GetCrossingMarkers())
            crossingMarkers.Add(new List<double>
            {
                Math.Round(x / pppm, 2), Math.Round(y / pppm, 2),
                Math.Round(r / pppm, 3),
            });

        // Painted centrelines: cut at every junction the line passes (no
        // paint across a crossing), solid in front of a mouth, dashed beyond
        // - the style says which, so the renderer paints what the network
        // decided.
        var centerlines = new List<Dictionary<string, object?>>();
        foreach (var (style, coords) in net.GetMarkingCenterlines())
            centerlines.Add(new Dictionary<string, object?>
            {
                ["style"] = style,
                ["pts"] = M(coords),
            });

        var (rcR, rcG, rcB) = Config.ROAD_COLOR;
        var (bgR, bgG, bgB) = Config.BG_COLOR;
        return new Dictionary<string, object?>
        {
            ["units"] = "meters",
            ["bounds"] = new List<double>
            {
                0.0, 0.0,
                Math.Round(net.WorldWidth / pppm, 2),
                Math.Round(net.WorldHeight / pppm, 2),
            },
            ["road_color"] = new List<int> { rcR, rcG, rcB },
            ["bg_color"] = new List<int> { bgR, bgG, bgB },
            ["road_strips"] = roadStrips,
            ["road_polygons"] = roadPolys,
            // Raw segment list (metres) for screen-space consumers like
            // the minimap. level: 0 = ground, 1 = bridge over the ground.
            ["segments"] = net.Segments.Select(s => new List<object?>
            {
                Math.Round(s.X1 / pppm, 2), Math.Round(s.Y1 / pppm, 2),
                Math.Round(s.X2 / pppm, 2), Math.Round(s.Y2 / pppm, 2),
                s.Width, s.Level,
            }).ToList(),
            // Bridge decks: buffered smoothed surface of all level>=1
            // segments (metres).
            ["elevated_roads"] = net.GetElevatedPolygons().Select(r => new Dictionary<string, object?>
            {
                ["exterior"] = M(r.Exterior),
                ["holes"] = r.Holes.Select(h => M(h)).ToList(),
            }).ToList(),
            ["elevated_roadways"] = net.GetElevatedRoadwayPolygons().Select(r => new Dictionary<string, object?>
            {
                ["exterior"] = M(r.Exterior),
                ["holes"] = r.Holes.Select(h => M(h)).ToList(),
            }).ToList(),
            ["elevated_edge_lines"] = net.GetElevatedEdgeLines().Select(M).ToList(),
            // Deck centrelines (metres): draw as dashes ABOVE the deck.
            ["elevated_centerlines"] = net.GetElevatedCenterlines().Select(M).ToList(),
            ["paved_edge_rings"] = pavedEdgeRings,
            ["centerlines"] = centerlines,
            ["lane_markings"] = net.GetLaneMarkings().Select(lm => new Dictionary<string, object?>
            {
                ["style"] = lm.Style,
                ["width_m"] = lm.WidthM,
                ["pts"] = M(lm.Coords),
            }).ToList(),
            ["oneway_arrows"] = net.GetOnewayArrows().Select(M).ToList(),
            ["parking_marks"] = net.GetParkingMarks().Select(M).ToList(),
            ["signs"] = signs,
            ["stop_lines"] = stopLines,
            ["crossing_markers"] = crossingMarkers,
            ["marking_style"] = new Dictionary<string, object?>
            {
                ["center_dash_m"] = Config.CENTER_DASH_M,
                ["center_gap_m"] = Config.CENTER_GAP_M,
                ["lane_dash_m"] = Config.LANE_DASH_M,
                ["lane_gap_m"] = Config.LANE_GAP_M,
                ["park_dash_m"] = Config.PARK_DASH_M,
                ["park_gap_m"] = Config.PARK_GAP_M,
                ["stop_line_width_m"] = Config.STOP_LINE_WIDTH_M,
            },
            ["start_points"] = startPoints,
        };
    }
}
