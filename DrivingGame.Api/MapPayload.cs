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

        var roads = new List<Dictionary<string, object?>>();
        foreach (var group in net.GetRoadPolygonsByColor())
            foreach (var ring in group.Rings)
                roads.Add(new Dictionary<string, object?>
                {
                    ["exterior"] = M(ring.Exterior),
                    ["holes"] = ring.Holes.Select(h => M(h)).ToList(),
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

        var junctions = new List<Dictionary<string, object?>>();
        foreach (var (nid, deg) in net.NodeDegree)
        {
            if (deg < 3 || !net.Nodes.TryGetValue(nid, out var xy)) continue;
            junctions.Add(new Dictionary<string, object?>
            {
                ["id"] = nid,
                ["x"] = Math.Round(xy.X / pppm, 2),
                ["y"] = Math.Round(xy.Y / pppm, 2),
            });
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
            ["roads"] = roads,
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
            ["centerlines"] = net.GetMarkingCenterlines().Select(M).ToList(),
            ["lane_markings"] = net.GetLaneMarkings().Select(lm => new Dictionary<string, object?>
            {
                ["style"] = lm.Style,
                ["width_m"] = lm.WidthM,
                ["pts"] = M(lm.Coords),
            }).ToList(),
            ["oneway_arrows"] = net.GetOnewayArrows().Select(M).ToList(),
            ["parking_marks"] = net.GetParkingMarks().Select(M).ToList(),
            ["junctions"] = junctions,
            ["signs"] = signs,
            ["junction_dot_radius_m"] = Config.JUNCTION_DOT_RADIUS_M,
            ["marking_style"] = new Dictionary<string, object?>
            {
                ["center_dash_m"] = Config.CENTER_DASH_M,
                ["center_gap_m"] = Config.CENTER_GAP_M,
                ["lane_dash_m"] = Config.LANE_DASH_M,
                ["lane_gap_m"] = Config.LANE_GAP_M,
                ["park_dash_m"] = Config.PARK_DASH_M,
                ["park_gap_m"] = Config.PARK_GAP_M,
            },
            ["start_points"] = startPoints,
        };
    }
}
