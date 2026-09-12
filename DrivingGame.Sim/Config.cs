using System.Collections.Generic;

namespace DrivingGame.Sim;

/// <summary>
/// Configuration constants + shared geometry helpers.
/// Ported from /Users/hauke/prj/car/src/config.py — keep in sync.
/// </summary>
public static class Config
{
    // --- Key names for control input (renderer-agnostic stand-ins) ---
    public const string KEY_UP = "up";
    public const string KEY_DOWN = "down";
    public const string KEY_LEFT = "left";
    public const string KEY_RIGHT = "right";
    public const string KEY_W = "w";
    public const string KEY_S = "s";
    public const string KEY_A = "a";
    public const string KEY_D = "d";
    public const string KEY_Q = "q";
    public const string KEY_E = "e";

    // --- Viewport (sim camera; the remote renderer mirrors it) ---
    public const int WINDOW_WIDTH = 1280;
    public const int WINDOW_HEIGHT = 720;

    // --- Target area: Kleinmachnow (south of Berlin), bounding box ---
    public const double BOUNDING_BOX_NORTH = 52.42382;
    public const double BOUNDING_BOX_WEST = 13.21831;
    public const double BOUNDING_BOX_SOUTH = 52.40714;
    public const double BOUNDING_BOX_EAST = 13.25033;

    // --- Projection ---
    /// <summary>Pixels per meter at zoom level 1.0.</summary>
    public const double PIXELS_PER_METER = 2;
    /// <summary>Extra paved radius (m) added on top of the widest connected
    /// road's half-width at junctions (corner-cutting area confirmed via
    /// satellite imagery). Used when rendering junction nodes and validating
    /// turning arcs stay on pavement.</summary>
    public const double JUNCTION_WIDENING_M = 4.0;
    /// <summary>Visible curb-style rounding radius (m) at road bends.</summary>
    public const double ROAD_CORNER_RADIUS_M = 6.0;
    /// <summary>Junction corner rounding (Eckausrundung): where two road
    /// EDGES meet at a degree>=3 node, the grass corner is rounded with a
    /// circular arc of this radius tangent to both edges. Fixed for all
    /// junctions (user decision 2026-08-31: 4 m). TODO: should depend on road
    /// class / design vehicle.</summary>
    public const double JUNCTION_CORNER_RADIUS_M = 4.0;
    /// <summary>Width-transition taper: where a wider road meets a narrower
    /// one at a plain (degree-2) node, the road does NOT step abruptly - it
    /// tapers. The taper lies ENTIRELY in the wider section (the narrow road
    /// keeps its width up to the node); its length is proportional to the
    /// width jump: T = WIDTH_TAPER_RATIO * (W_wide - W_narrow). Value 3 = a
    /// ~1:3 lateral:longitudinal verge, close to real road-design tapers.</summary>
    public const double WIDTH_TAPER_RATIO = 3.0;
    /// <summary>Width-transition overlap: the tapered wide ribbon does not stop
    /// flush at the transition node - it continues at the NARROW width this far
    /// past the node, ALONG the narrow neighbour's direction, so it overlaps
    /// the narrow ribbon. At an ANGLED transition this makes the wide ribbon
    /// bend through the node (no flat-cap gap) and the union merges cleanly
    /// (no notch at the bend). Metres.</summary>
    public const double WIDTH_TAPER_OVERLAP_M = 10.0;
    /// <summary>Shared slack (m) between planned-arc validation and live
    /// off-road checks.</summary>
    public const double ROAD_EDGE_TOLERANCE_M = 0.35;
    // Zoom limits: viewport width = WINDOW_WIDTH / (zoom * PIXELS_PER_METER)
    // MAX_ZOOM -> ~40 m viewport (zoomed in); MIN_ZOOM -> ~3000 m (out).
    public const double MAX_ZOOM = 16.0;    public const double MIN_ZOOM = 0.12;    public const double ZOOM_STEP = 1.15;
    // --- Car ---
    /// <summary>Top speed m/s (200 km/h, normal car).</summary>
    public const double CAR_SPEED = 55.6;    /// <summary>m/s² — 0-100 km/h in ~10 s.</summary>
    public const double CAR_ACCELERATION = 2.8;    /// <summary>m/s² — strong ABS braking (~1 g).</summary>
    public const double CAR_BRAKING = 10.0;    /// <summary>m/s² — comfortable parking brake (~0.35 g, no full braking).</summary>
    public const double PARK_BRAKING = 3.5;    /// <summary>Creep speed m/s (7 km/h) in the parking swing zone.</summary>

    // --- Sailing (coasting) -------------------------------------------------
    // With the throttle off a real car slows down on its own: rolling
    // resistance + aero drag (+ engine braking in gear). Coasting deceleration
    // of a ~1.5 t sedan: ~1.2 m/s^2 at 50 km/h, ~0.6 at 30, ~0.5 at 10 ->
    // a_roll + c_aero * v^2 with the values below.
    public const double COAST_ROLL_DECEL = 0.5;   // m/s^2 (rolling + engine drag)
    public const double COAST_AERO_COEF = 0.004;  // 1/m  (aero term: c * v^2)
    /// <summary>Coasting deceleration at speed v (m/s).</summary>
    public static double CoastDecel(double v) => COAST_ROLL_DECEL + COAST_AERO_COEF * v * v;
    /// <summary>Speed excess over the target that sailing alone sheds - above
    /// this the driver presses the brake pedal (lights on).</summary>
    public const double SAIL_BAND_MPS = 1.5;      // ~5 km/h
    public const double PARK_CREEP_SPEED_M = 2.0;    /// <summary>Degrees/second (FREE-mode arcade feel, capped below).</summary>
    public const double CAR_TURN_SPEED = 180;
    /// <summary>Mechanical minimum turning radius: wheelbase / tan(max steer
    /// angle) — the same limit the BICYCLE model uses. No car can turn tighter
    /// than this at ANY speed, so FREE mode clamps yaw rate to v / R.</summary>
    public static readonly double MIN_TURN_RADIUS_M =
        2.7 / Math.Tan(38.0 * Math.PI / 180.0); // ~3.46 m

    /// <summary>FREE mode: time for the virtual steering wheel to travel from
    /// center to full lock (and back). Instant full lock feels twitchy.</summary>
    public const double STEER_LOCK_TIME_S = 0.35;
    /// <summary>Reverse top speed m/s (30 km/h — a fast parking-lot crawl).</summary>
    public const double REVERSE_MAX_SPEED_M = 30.0 / 3.6; // ~8.33 m/s

    public const double CAR_LENGTH = 4.4;    public const double CAR_WIDTH = 1.8;
    /// <summary>Car sprite palette: names the vehicle model each car renders
    /// (the seven vehicles extracted by tools/make_car_sprites.py; the old
    /// classic red sprite was dropped - style mismatch, user decision).
    /// Default assignment for spawned cars: car N gets CAR_COLORS[(N-1) % 7];
    /// POST /teleport may override it via "color" (validated against this
    /// list).</summary>
    public static readonly string[] CAR_COLORS =
        { "blue", "silver", "police", "tan", "tractor", "pickup", "mixer" };

    // --- Per-class dynamics (user decision 2026-09-08: trucks accelerate
    // slower and are slower than normal cars - top speed, acceleration and
    // the cornering budget are vehicle-class specific) ---
    /// <summary>Dynamics of a vehicle class: (top speed m/s, longitudinal
    /// acceleration m/s², lateral-acceleration budget m/s²). Values grounded
    /// in real-world figures per class:
    ///  - car: 200 km/h sim limit (real sedans 220-250), 0-100 ~6.5 s;
    ///  - compact: 180 km/h, 0-100 ~10 s;
    ///  - pickup: 180 km/h, 0-100 ~10 s, skidpad ~0.7 g (higher CG than a
    ///    sedan -> lower cornering budget);
    ///  - truck (empty tractor unit): legally 80 km/h in DE, 0-80 ~17 s;
    ///    rollover threshold of heavy vehicles ~0.4-0.5 g (UMTRI) vs >1 g
    ///    for passenger cars -> clearly lower cornering budget;
    ///  - heavy_truck (loaded mixer): slowest and lowest cornering budget
    ///    (high CG, cargo shift).
    /// The "car" row equals the legacy globals, so sedan behavior (and all
    /// existing tests) is unchanged.</summary>
    public readonly record struct VehicleSpec(double TopSpeedMps, double AccelMps2, double LatAccelMax);

    public static readonly Dictionary<string, string> VEHICLE_CLASS = new()
    {
        ["blue"] = "car", ["police"] = "car", ["tan"] = "car",
        ["silver"] = "compact",
        ["pickup"] = "pickup",
        ["tractor"] = "truck", ["mixer"] = "heavy_truck",
    };

    //             top speed   accel    lat budget (m/s²)
    public static readonly Dictionary<string, VehicleSpec> VEHICLE_CLASS_SPECS = new()
    {
        ["car"]         = new(CAR_SPEED, CAR_ACCELERATION, 4.5),
        ["compact"]     = new(50.0,      2.5,              4.5),
        ["pickup"]      = new(50.0,      2.4,              4.0),
        ["truck"]       = new(22.2,      1.3,              3.5),
        ["heavy_truck"] = new(22.2,      1.0,              3.0),
    };

    /// <summary>Dynamics for a sprite color (unknown colors fall back to the
    /// sedan class).</summary>
    public static VehicleSpec SpecForColor(string? color) =>
        VEHICLE_CLASS_SPECS[VEHICLE_CLASS.GetValueOrDefault(color ?? "", "car")];

    // --- Vehicle footprint (width × length, metres) -------------------------
    // Single source of truth for each sprite's physical size, shared by the
    // renderer (MapRenderer stretches textures to these) and the simulation
    // (CarCollisions gap math + body boxes). Measured from the vehicle photos
    // (tools/make_car_sprites.py); trucks set to real-world dimensions.
    public static readonly Dictionary<string, (double WidthM, double LengthM)> VEHICLE_SIZES = new()
    {
        ["blue"]    = (1.80, 4.40),   // Mercedes C220d
        ["silver"]  = (1.71, 3.14),   // Fiat 500
        ["police"]  = (1.86, 4.71),   // BMW 320i police
        ["tan"]     = (1.86, 4.75),   // VW Passat B5
        ["tractor"] = (2.85, 7.00),   // tractor unit: 2.55 m body + ~15 cm mirror each side
        ["pickup"]  = (2.09, 5.25),   // Ford F-150
        ["mixer"]   = (2.80, 8.40),   // mixer: 2.50 m body + ~15 cm mirror each side
    };

    /// <summary>Footprint for a color; unknown colors fall back to the sedan box.</summary>
    public static (double WidthM, double LengthM) SizeForColor(string? color) =>
        VEHICLE_SIZES.GetValueOrDefault(color ?? "", (CAR_WIDTH, CAR_LENGTH));

    // --- Axle geometry ---
    // The kinematic bicycle model integrates the REAR AXLE: Car.x / Car.y ARE
    // the rear-axle midpoint, NOT the body centre. Everything visual must be
    // placed relative to that point (a point behind the pivot swings OUT of a
    // turn — drawing wheels symmetric about (x,y) would track wrong).
    // Proportions from assets/car_sprite.svg: front axle 62/200 of body length
    // ahead of centre, rear axle 58/200 behind, tyre centres 36/100 of width.
    public static readonly double FRONT_AXLE_OFFSET_M = CAR_LENGTH * 62 / 200;   // 1.364 m
    public static readonly double REAR_AXLE_OFFSET_M = CAR_LENGTH * 58 / 200;    // 1.276 m
    public static readonly double SPRITE_WHEELBASE_M = FRONT_AXLE_OFFSET_M + REAR_AXLE_OFFSET_M; // 2.64 m
    public static readonly double TIRE_OUTBOARD_M = CAR_WIDTH * 36 / 100;        // 0.648 m

    // --- Spawning / kerb ---
    /// <summary>Lateral gap (m) between the car's flank and the pavement edge
    /// at the kerb. Small on purpose: the car should be AT the kerb. Must match
    /// the inset the raceline corridor uses (CAR_WIDTH/2 + ROAD_EDGE_TOLERANCE_M),
    /// otherwise the car spawns nearer the kerb than its own driving line is
    /// ever allowed to go.</summary>
    public const double KERB_CLEARANCE_M = ROAD_EDGE_TOLERANCE_M;

    /// <summary>A PARKED car sits closer than a driving one (spec: "möglichst
    /// nah am rechten Rand"). Empirically closest clean park: at <=0.14 the
    /// reverse-in tuck grows past what _reverse_park_ok allows and the style
    /// falls back to a forward park ~0.6 m out.</summary>
    public const double PARK_KERB_CLEARANCE_M = 0.16;
    /// <summary>Parking lanes END this far (m) before any junction — you cannot
    /// park right in front of one (user decision: at least 5 m, visually).</summary>
    public const double PARK_LANE_END_GAP_M = 5.0;
    /// <summary>Dashed centerlines/lane dividers stop this far (m) before the
    /// node centre at a junction (half the widest arm's width plus this margin)
    /// — no paint on the crossing itself.</summary>
    public const double CENTERLINE_JUNCTION_GAP_M = 0.5;
    /// <summary>How far into a segment a car spawns, as a FRACTION of the
    /// segment length. Never 0: a node sits in the middle of the junction
    /// rounding where lane geometry is ambiguous. Fractional (not fixed metres)
    /// so it also works on very short segments — a fixed 5 m advance overshot
    /// the whole 4.16 m sliver approach and dumped the car past its junction.</summary>
    public const double SPAWN_PROGRESS = 0.10;
    /// <summary>Half-width (m) of the central-difference window used to measure
    /// a reference line's curvature. MUST be a fixed physical length, not a
    /// fraction of the route: at the old max(1.0, total*0.01) a 494 m route
    /// measured curvature over a 4.94 m window — wider than half a 9.4 m corner
    /// fillet — smearing a 4.25 m lane radius into 6.30 m so the car could not
    /// hold its own reference line and understeered wide out of every bend.</summary>
    public const double CURVATURE_WINDOW_M = 1.0;
    /// <summary>Clearance (m) kept between the car's left flank and the road
    /// centreline on a two-way road — the hard "never enter the oncoming lane"
    /// bound (lower edge of the raceline corridor). Also absorbs the
    /// controller's tracking error: the corridor constrains the reference LINE,
    /// but pure pursuit lags it (most in tight bends), and the hard rule applies
    /// to the CAR. 0.30 m kept the body clear through the test map's tightest
    /// bends; 0.50 m is the current value.</summary>
    public const double LANE_CENTRE_MARGIN_M = 0.50;
    /// <summary>Nominal lane position (m right of centreline), used only where a
    /// corridor cannot be built (very short routes).</summary>
    public const double LANE_OFFSET_DEFAULT_M = 1.75;
    // --- Offset helpers (shared by spawner and BicycleNav pull-over targets) ---

    /// <summary>Lateral offset (m) from a road's centreline to the position where
    /// the car sits flush against the right kerb, with KERB_CLEARANCE_M to spare.</summary>
    public static double KerbOffsetM(double roadWidthM) =>
        Math.Max(0.0, roadWidthM / 2.0 - CAR_WIDTH / 2.0 - KERB_CLEARANCE_M);

    /// <summary>Lateral offset (m) of a PARKED car's centre from the centreline:
    /// flush against the kerb with only PARK_KERB_CLEARANCE_M to spare.</summary>
    public static double ParkOffsetM(double roadWidthM) =>
        Math.Max(0.0, roadWidthM / 2.0 - CAR_WIDTH / 2.0 - PARK_KERB_CLEARANCE_M);

    /// <summary>Nominal driving position (m right of the centreline) for a road.
    /// Multi-lane two-way carriageways pin to the CENTRE OF THE OUTERMOST DRIVING
    /// LANE (a human keeps right — traffic drives next to the parking lane, not in
    /// it). One-way: all lanes run one way, driving strip is the full width.
    /// Plain roads keep the fixed nominal offset.</summary>
    public static double LaneBaseOffsetM(double width, int lanes = 0,
        double parkingLaneWidth = 0.0, bool oneway = false)
    {
        if (lanes > 0 && !oneway)
        {
            double d = Math.Max(0.0, width / 2.0 - parkingLaneWidth); // per side
            return Math.Max(0.0, d - (d / lanes) / 2.0);
        }
        if (lanes > 0) // one-way
        {
            double l = width / lanes;
            return Math.Max(0.0, width / 2.0 - l / 2.0);
        }
        return Math.Min(LANE_OFFSET_DEFAULT_M, KerbOffsetM(width));
    }

    // --- Road rendering ---

    public sealed record RoadType((int R, int G, int B) Color, double Width2Way, double Width1Way);

    /// <summary>Real-world widths in meters (2-way / 1-way). Levels 1-7:
    /// motorway, trunk, primary, secondary, tertiary, residential, unclassified.
    /// The per-type grays are only used for WIDTHS now — all roads render in the
    /// one uniform ROAD_COLOR.</summary>
    public static readonly Dictionary<string, RoadType> ROAD_TYPES = new()
    {
        ["motorway"]       = new((68, 68, 68),     14, 7),
        ["motorway_link"]  = new((68, 68, 68),     7, 3.5),
        ["trunk"]          = new((85, 85, 85),     10, 7),
        ["trunk_link"]     = new((85, 85, 85),     7, 3.5),
        ["primary"]        = new((102, 102, 102),  10, 7),
        ["primary_link"]   = new((102, 102, 102),  7, 3.5),
        ["secondary"]      = new((136, 136, 136),  7, 3.5),
        ["secondary_link"] = new((136, 136, 136),  7, 3.5),
        ["tertiary"]       = new((153, 153, 153),  7, 3.5),
        ["tertiary_link"]  = new((153, 153, 153),  7, 3.5),
        ["residential"]    = new((170, 170, 170),  7, 3.5),
        ["unclassified"]   = new((187, 187, 187),  7, 3.5),
        ["service"]        = new((204, 204, 204),  3.5, 3.5),
    };

    /// <summary>Drivable highway tags (subset) — levels 1-7.</summary>
    public static readonly HashSet<string> DRIVABLE_ROADS = new(ROAD_TYPES.Keys);

    // --- Colors ---
    public static readonly (int R, int G, int B) BG_COLOR = (34, 120, 34);          // grass green
    public static readonly (int R, int G, int B) ROAD_EDGE_COLOR = (50, 50, 50);    // road markings

    /// <summary>All road types are drawn in this ONE uniform asphalt color.</summary>
    public static readonly (int R, int G, int B) ROAD_COLOR = (120, 120, 124);

    /// <summary>Every two-way road at least this wide gets a dashed white
    /// centerline. Narrow service lanes (3.5 m) and one-ways don't.</summary>
    public const double CENTERLINE_MIN_WIDTH_M = 7.0;
    /// <summary>Junction dot (white circle at 3+-way nodes): physical size 30 cm
    /// in diameter, rendered in world space (1 px floor at low zoom).</summary>
    public const double JUNCTION_DOT_RADIUS_M = 0.15;
    /// <summary>The white boundary line is drawn this far INWARD from the true
    /// paved edge: a 15 cm tarmac shoulder stays outside the line. The off-road
    /// check keeps using the full paved polygon — the shoulder is still "on road".</summary>
    public const double EDGE_LINE_INSET_M = 0.15;
    /// <summary>Elevated decks (bridges) carry a sidewalk this wide on each side
    /// of the carriageway (visual only).</summary>
    public const double BRIDGE_SIDEWALK_M = 1.0;
    // Marking dash patterns (METRES). Shared with the Godot frontend via /map so
    // external renderers draw the same pattern from one source of truth.
    public const double CENTER_DASH_M = 3.0;   // two-way centerline: 3 m dash / 3 m gap
    public const double CENTER_GAP_M = 3.0;
    public const double LANE_DASH_M = 2.0;     // multi-lane lane divider: fine dashes
    public const double LANE_GAP_M = 4.0;
    public const double PARK_DASH_M = 1.0;     // parking-lane boundary: even finer
    public const double PARK_GAP_M = 1.0;
    public static readonly (int R, int G, int B) MINIMAP_BG = (20, 60, 20);
    public static readonly (int R, int G, int B) MINIMAP_CAR_COLOR = (255, 0, 0);
    public static readonly (int R, int G, int B) MINIMAP_BORDER = (80, 80, 80);

    // --- Minimap ---
    public const int MINIMAP_SIZE = 180;
    public const int MINIMAP_MARGIN = 15;
    public const double MINIMAP_XRANGE = 0.032;   // lon
    public const double MINIMAP_YRANGE = 0.0167;  // lat
}
