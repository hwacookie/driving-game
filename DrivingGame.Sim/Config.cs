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

    // --- Target area: Kleinmachnow (south of Berlin), bounding box ---
    public const double BOUNDING_BOX_NORTH = 52.42382;
    public const double BOUNDING_BOX_WEST = 13.21831;
    public const double BOUNDING_BOX_SOUTH = 52.40714;
    public const double BOUNDING_BOX_EAST = 13.25033;

    // --- World frame: the sim works in METRES (x east, y north). There is
    // no pixel frame left; the Godot client picks its own px/m at draw time.
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

    public const double CAR_LENGTH_M = 4.4;    public const double CAR_WIDTH_M = 1.8;
    // --- Models: one kind of vehicle, plus everything about it that is neither
    // identity nor paint (issue #18, owner decision 2026-10-30) -------------
    /// <summary>A MODEL: max speed, acceleration, brake rate and the footprint —
    /// plus the cornering budget the sim already used and the photo the sprite
    /// was cut from.
    ///
    /// This table replaces three that disagreed about what a vehicle is:
    /// `VEHICLES` (seven PHOTO names), `VEHICLE_CLASS` (photo → class) and
    /// `VEHICLE_CLASS_SPECS` / `VEHICLE_SIZES` (class → dynamics, photo → size).
    /// A model's own numbers sat up to two dictionary hops away, and the
    /// footprint hung off a picture: `blue` and `silver` were never two kinds of
    /// vehicle, and `blue`/`tan`/`police` are three different sedans.
    ///
    /// Every value is TRANSCRIBED, not retuned: top speed, acceleration and
    /// lateral budget are the old per-class numbers resolved through the old
    /// photo → class map; width × length are the old per-photo sizes; the brake
    /// rate is today's single CAR_BRAKING for all seven, because per-model
    /// braking is new physics and needs its own change with its own e2e verdicts.
    /// So this commit must not move a car by one centimetre — if a scenario turns
    /// differently afterwards, the transcription is wrong, not the world.
    /// </summary>
    public readonly record struct Model(
        string Name, string Photo,
        double MaxSpeedMps, double AccelMps2, double BrakeMps2, double LatAccelMps2,
        double WidthM, double LengthM);

    public static readonly Dictionary<string, Model> MODELS = new()
    {
        //                name            photo     max speed  accel  brake  lat   W × L
        ["mercedes_c220"] = new("mercedes_c220", "blue",
                                CAR_SPEED, CAR_ACCELERATION, CAR_BRAKING, 4.5, 1.80, 4.40),
        ["fiat_500"]      = new("fiat_500", "silver",
                                50.0, 2.5, CAR_BRAKING, 4.5, 1.71, 3.14),
        ["bmw_police"]    = new("bmw_police", "police",
                                CAR_SPEED, CAR_ACCELERATION, CAR_BRAKING, 4.5, 1.86, 4.71),
        ["vw_passat"]     = new("vw_passat", "tan",
                                CAR_SPEED, CAR_ACCELERATION, CAR_BRAKING, 4.5, 1.86, 4.75),
        ["tractor_unit"]  = new("tractor_unit", "tractor",
                                22.2, 1.3, CAR_BRAKING, 3.5, 2.85, 7.00),   // empty unit: 80 km/h legally in DE
        ["ford_f150"]     = new("ford_f150", "pickup",
                                50.0, 2.4, CAR_BRAKING, 4.0, 2.09, 5.25),   // higher CG than a sedan
        ["mixer_truck"]   = new("mixer_truck", "mixer",
                                22.2, 1.0, CAR_BRAKING, 3.0, 2.80, 8.40),   // loaded: slowest, lowest budget
    };

    /// <summary>Model names in the order the round robin hands them out. The
    /// order is the OLD `VEHICLES` order on purpose: car N has always got the
    /// N-th entry, so shuffling it would restyle every existing scenario and
    /// move which car is the heavy one in a cap-key test.</summary>
    public static readonly string[] MODEL_NAMES =
        { "mercedes_c220", "fiat_500", "bmw_police", "vw_passat",
          "tractor_unit", "ford_f150", "mixer_truck" };

    /// <summary>The seven photo names are not model names — but they are what
    /// every existing client, cockpit script, scenario and recorded tape says.
    /// They stay valid as aliases, resolved here, in ONE place, so no caller has
    /// to know which era a string came from. (The wire flip that gives `color`
    /// back to paint is #18's second slice; until then `color` on the wire still
    /// names the vehicle and arrives here.)</summary>
    public static readonly Dictionary<string, string> MODEL_ALIASES = new()
    {
        ["blue"] = "mercedes_c220", ["silver"] = "fiat_500",
        ["police"] = "bmw_police", ["tan"] = "vw_passat",
        ["tractor"] = "tractor_unit", ["pickup"] = "ford_f150",
        ["mixer"] = "mixer_truck",
    };

    /// <summary>Resolve anything that names a vehicle — a model name, an old
    /// photo alias, or nothing at all — to a Model. Unknown and empty answer the
    /// sedan, exactly as the old class fallback did.</summary>
    public static Model ModelFor(string? name)
    {
        string key = (name ?? "").Trim();
        if (MODEL_ALIASES.TryGetValue(key, out var alias)) key = alias;
        return MODELS.GetValueOrDefault(key, MODELS["mercedes_c220"]);
    }

    /// <summary>Anything the table accepts (model name or alias). The API checks
    /// this before spawning, so "bogus" is a 400 and never a silent sedan.</summary>
    public static bool IsModel(string? name)
    {
        string key = (name ?? "").Trim();
        return MODEL_ALIASES.ContainsKey(key) || MODELS.ContainsKey(key);
    }

    /// <summary>Dynamics in the shape `Car.Spec` has always carried. Kept so the
    /// driving code does not churn in the same commit as the vocabulary; it
    /// retires when its last caller does.</summary>
    public readonly record struct VehicleSpec(double TopSpeedMps, double AccelMps2,
                                              double LatAccelMax);

    public static VehicleSpec SpecForVehicle(string? name)
    {
        var m = ModelFor(name);
        return new VehicleSpec(m.MaxSpeedMps, m.AccelMps2, m.LatAccelMps2);
    }

    public static (double WidthM, double LengthM) SizeForVehicle(string? name)
    {
        var m = ModelFor(name);
        return (m.WidthM, m.LengthM);
    }

    // --- Liveries (issue #10: the paint, not the vehicle) -----------------
    /// <summary>Named paints, so a scenario and a tape read as intentions
    /// ("crimson") instead of as a hex code. A livery changes what a car LOOKS
    /// like and NOTHING else - never its dynamics class, never its footprint,
    /// which is exactly the entanglement `color` created (asking for another
    /// colour today means replacing a sedan with an 8.4 m truck).</summary>
    public static readonly Dictionary<string, string> LIVERIES = new()
    {
        ["crimson"]  = "#c62828", ["amber"]    = "#ef6c00",
        ["ochre"]    = "#a8842c", ["moss"]     = "#4b7f3f",
        ["teal"]     = "#0f7c7c", ["cobalt"]   = "#2451b3",
        ["plum"]     = "#6a2c91", ["graphite"] = "#3b4046",
        ["sand"]     = "#c9b48a", ["chalk"]    = "#e8e6df",
        ["rose"]     = "#e05b8c", ["slate"]    = "#5b6b7c",
    };

    /// <summary>A livery request to `#rrggbb`, or null for "no livery" (the car
    /// keeps its photographed colour). Accepted: a name from <see cref="LIVERIES"/>
    /// (case-insensitive), `#rgb`/`#rrggbb`, or `hue:&lt;0..360&gt;` at a fixed
    /// saturation and lightness for experiments. Unresolvable input is null
    /// rather than a guess - the API answers 400 so a typo is heard about, not
    /// driven around in a random colour.</summary>
    public static string? ResolveLivery(string? want)
    {
        if (string.IsNullOrWhiteSpace(want)) return null;
        var w = want.Trim();
        if (LIVERIES.TryGetValue(w.ToLowerInvariant(), out var named)) return named;
        if (w.StartsWith("#"))
        {
            var h = w.Substring(1);
            if (h.Length == 3) h = string.Concat(h[0], h[0], h[1], h[1], h[2], h[2]);
            if (h.Length != 6) return null;
            foreach (var ch in h)
                if (!Uri.IsHexDigit(ch)) return null;
            return $"#{h.ToLowerInvariant()}";
        }
        if (w.StartsWith("hue:") &&
            double.TryParse(w.Substring(4), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var deg))
        {
            if (double.IsNaN(deg)) return null;
            deg = ((deg % 360.0) + 360.0) % 360.0;
            // Fixed saturation and lightness: a hue alone must stay readable on
            // asphalt AND on grass, which the extremes (near-black, near-white)
            // are not.
            const double s = 0.62, v = 0.80;
            double c = v * s, x = c * (1 - Math.Abs((deg / 60.0) % 2 - 1)), m = v - c;
            (double r, double g, double b) = deg switch
            {
                < 60 => (c, x, 0.0), < 120 => (x, c, 0.0), < 180 => (0.0, c, x),
                < 240 => (0.0, x, c), < 300 => (x, 0.0, c), _ => (c, 0.0, x),
            };
            string Hex(double d) =>
                ((int)Math.Round(Math.Clamp(d + m, 0.0, 1.0) * 255.0)).ToString("x2");
            return $"#{Hex(r)}{Hex(g)}{Hex(b)}";
        }
        return null;
    }

    // --- Axle geometry ---
    // The kinematic bicycle model integrates the REAR AXLE: Car.x / Car.y ARE
    // the rear-axle midpoint, NOT the body centre. Everything visual must be
    // placed relative to that point (a point behind the pivot swings OUT of a
    // turn — drawing wheels symmetric about (x,y) would track wrong).
    // Proportions of the original car sprite (the retired assets/car_sprite.svg;
    // today's PNGs share the proportions): front axle 62/200 of body length
    // ahead of centre, rear axle 58/200 behind, tyre centres 36/100 of width.
    public static readonly double FRONT_AXLE_OFFSET_M = CAR_LENGTH_M * 62 / 200;   // 1.364 m
    public static readonly double REAR_AXLE_OFFSET_M = CAR_LENGTH_M * 58 / 200;    // 1.276 m
    public static readonly double SPRITE_WHEELBASE_M = FRONT_AXLE_OFFSET_M + REAR_AXLE_OFFSET_M; // 2.64 m
    public static readonly double TIRE_OUTBOARD_M = CAR_WIDTH_M * 36 / 100;        // 0.648 m

    // --- Spawning / kerb ---
    /// <summary>Lateral gap (m) between the car's flank and the pavement edge
    /// at the kerb. Small on purpose: the car should be AT the kerb. Must match
    /// the inset the raceline corridor uses (CAR_WIDTH_M/2 + ROAD_EDGE_TOLERANCE_M),
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
    /// <summary>Length (m) of UNBROKEN centreline paint in front of a
    /// junction mouth. A dashed line would read as "change lanes here";
    /// approaching a crossing the paint is solid for this stretch, then
    /// resumes its dash pattern further out.</summary>
    public const double CENTERLINE_SOLID_BEFORE_JUNCTION_M = 6.0;
    // A halt line is a band of white across the YIELDING LANE at the mouth
    // of a yielding approach - far thicker than a line marking.
    /// Width of PAINTED road lines (centre line, lane dashes, kerb line),
    /// metres. German markings are 0.10-0.12 m; anything above that reads as a
    /// heavy border around a 3.5 m lane. The renderer reads THIS - there is no
    /// second paint-width number in the client (user decision 2026-10-26: an
    /// outer white line must never be wider than the centre line).
    public const double ROAD_PAINT_WIDTH_M = 0.08;
    public const double STOP_LINE_WIDTH_M = 0.5;
    /// <summary>Gap (m) the halt line keeps from the road's RIGHT edge: the
    /// paint belongs to the yielding lane only, so it must not run into the
    /// kerb. Its other end MAY touch the centreline (the lane's left
    /// boundary) - that is where the right lane starts.</summary>
    public const double STOP_LINE_EDGE_GAP_M = 0.5;
    /// <summary>Diameter (m) of the small white disc painted in the middle of
    /// a real crossing (two roads crossing, not a T). NOT a German rule - a
    /// visual marker the user asked for, so the crossing's centre is visible
    /// even where nothing else is painted.</summary>
    public const double CROSSING_MARKER_DIAMETER_M = 0.3;
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
        Math.Max(0.0, roadWidthM / 2.0 - CAR_WIDTH_M / 2.0 - KERB_CLEARANCE_M);

    /// <summary>Lateral offset (m) of a PARKED car's centre from the centreline:
    /// flush against the kerb with only PARK_KERB_CLEARANCE_M to spare.</summary>
    public static double ParkOffsetM(double roadWidthM) =>
        Math.Max(0.0, roadWidthM / 2.0 - CAR_WIDTH_M / 2.0 - PARK_KERB_CLEARANCE_M);

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
