// Bicycle-model road navigation for one car.
// 1:1 port of car/src/bicycle_nav.py (BicycleNav class + module helpers in
// RefLine.cs).
//
// The car is a FREE PARTICLE on the road surface, not a train locked to the
// OSM graph:
//
//     state = (x, y, heading, v, steering_delta)
//     heading 0 = north (+y), forward = (sin h, cos h), positive steer = right
//
//     v          += (throttle - brake) * dt
//     d(heading) = (v / L) * tan(delta) * dt        (bicycle kinematics)
//     x         += v * sin(heading) * dt
//     y         += v * cos(heading) * dt
//
// Understeer is EMERGENT: the heading rate is capped by a lateral-accel
// limit (a_lat = v * |d(heading)/dt| <= A_LAT_MAX), so at speed the car
// cannot turn as sharply and swings wide instead of teleporting.

namespace DrivingGame.Sim;

/// <summary>State of an active reverse-in parking manoeuvre.</summary>
public sealed class ReverseParkState
{
    public required double[] Steer { get; init; }
    public required double[] Psi { get; init; }
    public required double RoadH { get; init; }
    public required double VTarget { get; init; }
    public required double OPark { get; init; }
    public required double Tx { get; init; }
    public required double Ty { get; init; }
    public required double Rx { get; init; }
    public required double Ry { get; init; }
    public required int Seg { get; init; }
}

public sealed partial class BicycleNav
{
    // ====================================================================
    // Constants (Python class attributes - shared by every car)
    // ====================================================================

    public const double WHEELBASE = 2.7;           // m
    public const double CAR_LENGTH_M = 4.5;        // m (front bumper to rear bumper)
    /// <summary>Distance from the car's reference point (rear axle - what S
    /// tracks) to the front bumper. Used to park with the bumper AT the
    /// destination flag rather than the axle on it (spec §1).</summary>
    public static readonly double FRONT_OVERHANG_M =
        WHEELBASE + Config.CAR_LENGTH / 2.0 - Config.FRONT_AXLE_OFFSET_M;

    public static readonly double MAX_STEER = Math.Radians(38.0);   // mechanical steering limit

    /// <summary>Lateral-accel budget. Sets BOTH the cornering speed the
    /// profile plans (v = sqrt(A_LAT_MAX / kappa)) and the understeer cap on
    /// the heading rate, so the car is never handed a speed it cannot hold.
    /// Well under the ~8 m/s^2 the tyres could actually give.</summary>
    public const double A_LAT_MAX = 4.5;           // m/s^2 lateral-accel cap (understeer)

    /// <summary>The speed profile plans against only a FRACTION of that cap,
    /// leaving the controller authority to correct with: any tracking error
    /// becomes permanent at the full value.</summary>
    public const double A_LAT_PLAN_FRACTION = 0.7;

    public static readonly double A_CRUISE = Config.CAR_ACCELERATION;   // m/s^2 (2.8)
    public static readonly double A_BRAKE = Config.CAR_BRAKING;         // m/s^2 (10.0)
    public static readonly double V_MAX = Config.CAR_SPEED;             // m/s (55.6)

    /// <summary>Speed the car arrives at any real junction (degree >= 3).
    /// Slow enough that a turn signaled ANYWHERE inside the approach ramp is
    /// still physically reachable.</summary>
    public const double JUNCTION_ENTRY_SPEED_M = 8.0;

    // --- Brake & park plan (spec §1) ---
    public static readonly double A_PARK = Config.PARK_BRAKING;              // comfortable parking decel
    public static readonly double PARK_SWERVE_SPEED_M = Config.PARK_CREEP_SPEED_M;   // V_C, swerve speed
    public const double PARK_LEAD_S = 2.0;                 // indicator lead time at speed
    /// <summary>Drift duration at V_C. Sized to keep the drift ~8 m long at
    /// the 2 m/s creep speed.</summary>
    public const double PARK_SWERVE_S = 1.5;
    /// <summary>PARK_STOP_TAU: chosen so the deceleration at the START of
    /// the roll-out is exactly A_PARK: tau = V_C / A_PARK.</summary>
    public static readonly double PARK_STOP_TAU = PARK_SWERVE_SPEED_M / Config.PARK_BRAKING;

    /// <summary>Extra road the CENTRELINE keeps past the destination,
    /// regardless of parking style (SOLVER INPUT ONLY - the actual stop
    /// point is unaffected).</summary>
    public const double CORRIDOR_RUNOUT_M = 30.0;

    public const double PARK_STANDSTILL_M_S = 0.02;   // drop the last 0.2 km/h
    public const double PARK_ROLL_END_M_S = 0.3;      // hand-over to constant decel
    public const double PARK_ROLL_END_A = 0.6;

    /// <summary>Maximum slant of the pull-over drift (peak tangent angle).
    /// Cap the slant and park a bit further out instead.</summary>
    public const double MAX_PARK_DRIFT_SLANT_DEG = 15.0;

    // Standstill creep: allow a minimum accel scale while the car is
    // (nearly) stopped so it can creep forward and break the deadlock.
    public const double CREEP_SPEED = 1.0;            // m/s - creep applies below this speed
    public const double CREEP_SCALE = 0.3;            // minimum accel scale while creeping

    /// <summary>Corner-rounding radius for the reference line: the same
    /// radius as the renderer's paved fillet (6 m) so the reference line
    /// follows the same corner the car is actually allowed to occupy.</summary>
    public static readonly double CORNER_RADIUS_M = Config.ROAD_CORNER_RADIUS_M;  // 6.0
    public const int CORNER_ARC_STEPS = 48;      // must out-resolve Raceline.SampleM
    public const int HORIZON_SEGMENTS = 6;       // how many segments ahead to build the route

    /// <summary>Right-hand-traffic lane offset: quarter of the road width for
    /// the test map's 7 m two-way roads.</summary>
    public const double LANE_OFFSET_M = 1.75;

    /// <summary>Half-length of the span around a real junction where the
    /// wrong-side check is suspended (no centreline exists inside an
    /// intersection).</summary>
    public const double JUNCTION_SUPPRESS_M = 12.0;

    /// <summary>How far over the planned corner speed the car may be and
    /// still be considered able to make the turn.</summary>
    public const double REACHABLE_SPEED_TOLERANCE = 1.15;

    // Swerve-zone geometry, DERIVED from the plan (not free parameters).
    public const double PARK_ALIGN_M = 3.0;
    public static readonly double PARK_BLEND_END_M =
        Math.Max(PARK_ALIGN_M, PARK_SWERVE_SPEED_M * PARK_STOP_TAU);
    public static readonly double PARK_BLEND_START_M =
        PARK_SWERVE_SPEED_M * PARK_SWERVE_S + PARK_BLEND_END_M;   // ≈ 7.9 m

    // Pull-out: from right edge into normal lane (symmetric to park).
    public const double PULL_OUT_START_M = 20.0;
    public const double PULL_OUT_END_M = 5.0;

    // Lane change before parking (docs §1 variant on multi-lane roads).
    public const double LANE_CHANGE_TIME_S = 2.5;
    public const double MERGE_LEN_MIN_M = 20.0;        // crawl-speed floor for the blend length
    public const double MERGE_LEN_MAX_M = 90.0;        // high-speed cap
    public const double MERGE_SETTLE_BEFORE_M = 40.0;  // blend must end this far before the flag
    /// <summary>The blinker comes on this far BEFORE the lane change starts
    /// (user rule: always signal before changing lanes).</summary>
    public const double MERGE_SIGNAL_AHEAD_M = 30.0;

    /// <summary>Lookahead (m) used to track the straight edge line in the
    /// final pull-over stretch. Kept short so the car corrects its lateral
    /// offset onto the edge quickly and holds the wheels parallel.</summary>
    public const double PARK_TRACK_LOOKAHEAD_M = 2.5;

    /// <summary>Lateral error (m) below which the car is considered "lined
    /// up" on the edge line.</summary>
    public const double PARK_ALIGN_LATERAL_M = 0.35;

    /// <summary>Cross-track gain of the Stanley law used in that final
    /// straight: 1.0 m/s of correction per metre of offset.</summary>
    public const double PARK_ALIGN_GAIN = 1.0;

    /// <summary>Heading-error gain of the same law. x3 gives a 0.45 s time
    /// constant at V_C: an 8 deg entry error is down to &lt;1 deg before the
    /// roll-out decay starts.</summary>
    public const double PARK_ALIGN_HDG_GAIN = 3.0;

    // Cross-track fade of the same law: the cross term fades to zero over
    // [PARK_ALIGN_CROSS_FADE_END_M, PARK_ALIGN_CROSS_FADE_START_M] of
    // remaining distance to the stop point.
    public const double PARK_ALIGN_CROSS_FADE_START_M = 1.0;
    public const double PARK_ALIGN_CROSS_FADE_END_M = 0.4;

    // Reserves kept when picking how close to the kerb to park: line
    // discretisation, and the controller's residual tracking error.
    public const double PARK_LINE_MARGIN_M = 0.05;
    public const double PARK_TRACKING_MARGIN_M = 0.10;

    // ---- Reverse-in parking (docs/DRIVING_MANEUVERS.md §1b) ----
    public const double PARK_REVERSE_CREEP_M_S = 0.8;        // ~3 km/h while reversing
    /// <summary>The nose swings towards the centreline while backing in, and
    /// must stay on our own half of the road: unlike a U-turn, parking may
    /// NOT use the oncoming lane.</summary>
    public const double PARK_CENTRELINE_MARGIN_M = 0.10;
    /// <summary>Reverse-in arc steering candidates, sharpest first (degrees).
    /// Full lock is tried first because it is the shortest back-in.</summary>
    public static readonly double[] REVERSE_STEER_CANDIDATE_DEGS = { 38.0, 30.0, 25.0, 20.0, 16.0 };
    /// <summary>Planning margin at the kerb for the swept body corners.</summary>
    public const double PARK_REVERSE_KERB_MARGIN_M = 0.05;
    /// <summary>Below this the manoeuvre is not worth it - park forwards.</summary>
    public const double PARK_REVERSE_MIN_TUCK_M = 0.10;
    // Gains of the reverse-in follower (feedback parking law):
    //   delta = PSI_GAIN * psi - POS_GAIN * e_off      (radians)
    public const double PARK_REVERSE_PSI_GAIN = 2.8;     // heading damping, ~1.2 s time constant
    public const double PARK_REVERSE_POS_GAIN = 1.4;     // offset correction, ~2.5 s at creep
    /// <summary>Straight run-out after the arcs. Must be long enough for
    /// heading and cross-track error to converge AT SPEED.</summary>
    public const double PARK_REVERSE_TAIL_M = 2.4;
    /// <summary>The staging stop can sit this much closer to the centreline
    /// than the planned lane offset; a deeper start needs a longer back-in
    /// for the same tuck, so plan from the deeper offset.</summary>
    public const double PARK_PLAN_START_MARGIN_M = 0.35;
    /// <summary>...but if the staging turned out shorter than planned, this
    /// much is still enough to settle on.</summary>
    public const double PARK_REVERSE_MIN_TAIL_M = 0.3;

    // ---- U-turn (Wenden) - docs/DRIVING_MANEUVERS.md §5 ----
    public const double UTURN_SPEED_MAX = 2.8;             // m/s (~10 km/h), spec: "5-10 km/h"
    public const double UTURN_SINGLE_SWING_MIN_WIDTH_M = 11.0;   // §5a vs §5b threshold (verified)
    public const double UTURN_TH2_DEG = 60.0;              // step-2 heading change before the stop
    public const double UTURN_TH3_DEG = 90.0;              // total heading change at end of step 3
    /// <summary>Approach/brake stretch to the kerb (the lateral blend length
    /// is derived at generation time from the clearance constraint).</summary>
    public const double UTURN_STEP1_LEN_M = 14.0;
    /// <summary>Planning allowance for the follower's tracking error: plan
    /// like a driver keeps margin, don't let the planned path touch the kerb.</summary>
    public const double UTURN_BLEND_TRACKING_MARGIN_M = 0.30;
    public const double UTURN_TAIL_LEN_M = 15.0;           // straight-out stretch after the turn
    public const double UTURN_HOLD_S = 0.7;                // pause at each full stop (spec: "Stoppen")
    public const double UTURN_STALL_ABORT_S = 8.0;         // no progress this long -> abort maneuver
    /// <summary>~18 km/h - faster than this, a three-point turn cannot be
    /// performed.</summary>
    public const double UTURN_MAX_ENTRY_SPEED_M = 5.0;
    /// <summary>Minimum corner clearance the generated LINE must guarantee
    /// against the pavement edge (the binding constraint on 7 m roads).</summary>
    public const double UTURN_LINE_MARGIN_M = 0.10;

    // ====================================================================
    // State
    // ====================================================================

    private readonly Car _car;
    private readonly RoadNetwork _network;

    private RefLine? _ref;
    private List<string> _route = new();
    private (int SegIdx, string Turn, bool PullingOver, bool PullingOut)? _routeKey;
    public HashSet<int> RouteSegSet { get; private set; } = new();

    /// <summary>The current node route (ordered) — Python's nav._route.
    /// Used by the main loop to resolve [segment, progress] end flags onto
    /// the route's traversal direction.</summary>
    public IReadOnlyList<string> Route => _route;
    private string? _routeExitDir;
    private string? _routeExitNode;

    private string? _turnSignalTarget;
    private string? _prevPendingTurn;

    private double[] _profile = Array.Empty<double>();
    /// <summary>The car's arc position on the reference line (m).</summary>
    public double S { get; private set; }

    /// <summary>Frames of pull-out left (0 = not pulling out). Spawn is in
    /// the driving position, so this starts at 0 - the machinery stays for
    /// parity with the Python original.</summary>
    public int PullOutFrames { get; private set; } = 0;

    // U-turn state (see StartUturn / UpdateUturn)
    private bool _uturnActive;
    private double[] _uturnProfile = Array.Empty<double>();   // SIGNED v_max per metre
    private List<double> _uturnStops = new();                 // arc length of full stops
    private int _uturnStopPtr;
    private string _uturnState = "drive";             // drive | creeping | holding
    private double _uturnHoldT;
    private int _uturnApproachDir;                     // +1/-1: how we approach a stop
    private string _uturnMode = "fwd";                 // pursuit mode: fwd | rev
    private double _uturnStallT;
    private double[] _uturnKappa = Array.Empty<double>();   // per-point curvature (rad/m)
    private double[] _uturnHdg = Array.Empty<double>();     // per-point nose heading (rad)
    private List<double> _uturnLookaheadClamps = new();     // s values pursuit may not cross
    private double _uturnReleaseForce;               // signed forced speed off a stop

    // Reverse-in parking (docs §1b): style of the current approach, how much
    // of the pull-over is left to the reverse tuck, and how far ahead of the
    // final spot the car stops first.
    public string ParkStyle { get; private set; } = "forward";   // forward | reverse
    private bool _parkStyleLocked;      // decided once per destination
    private double _parkTuck;
    private double _parkStageM;
    private bool _parkStageShort;
    private ReverseParkState? _reversePark;   // active manoeuvre state, or null
    /// <summary>Standing at the destination.</summary>
    public bool Parked { get; private set; }

    // Lane-change episode (merge before parking): the zone [s0, s1] on the
    // current reference line, its direction, and the speed-dependent blend
    // length (frozen once committed - see MergeParams).
    private (double S0, double S1, string Dir)? _mergeEpisode;
    private (double DestX, double DestY, double OSpan)? _mergeKey;
    private double? _mergeLen;

    /// <summary>Blinker state for the driver: which indicator to show while a
    /// lane change is pending/active (null = no lane-change signal).</summary>
    public string? LaneChangeSignal { get; private set; }

    // Cruise at the car's top speed on straights.
    private readonly double _cruise = V_MAX;

    // Explicit destination (world coords, e.g. the red end flag).
    private (double X, double Y)? _dest;

    // Where the route passes through real junctions (LaneGuard suppression).
    private List<(double A, double B)> _junctionZones = new();

    // Anchor of the pull-over drift (world point + offset), remembered once
    // per pull-over episode.
    private (double X, double Y, double O)? _pullOverAnchor;

    // How close the FORWARD swerve can actually get (set by ApplyEndBlends).
    private double _parkFwdTarget;

    /// <summary>Current phase of the brake & park plan: 'none' | 'lead' |
    /// 'decel' | 'swerve' | 'final' | 'reverse' | 'stopped'.</summary>
    public string ParkPhase { get; private set; } = "none";

    public BicycleNav(Car car, RoadNetwork network)
    {
        _car = car;
        _network = network;
    }

    // ====================================================================
    // Route / reference line access
    // ====================================================================

    private string IntendedTurn()
    {
        var d = _car.Driver;
        return d?.PendingTurn ?? "straight";
    }

    /// <summary>True if the current route ends at a dead end (the car's
    /// destination - there is no road beyond it).</summary>
    private bool RouteEndsDeadEnd()
    {
        if (_route.Count == 0) return false;
        return _network.NodeDegree.GetValueOrDefault(_route[^1]) <= 1;
    }

    /// <summary>Set an explicit destination (world coordinates, e.g. the red
    /// end flag). The reference line is truncated there and the car parks at
    /// it with the same machinery as a dead-end route. Forces a route
    /// rebuild on the next update.</summary>
    public void SetDestination(double x, double y)
    {
        _dest = (x, y);
        _routeKey = null;   // invalidate -> MaybeRebuild re-cuts
        ParkStyle = "forward";
        _parkStyleLocked = false;
        _parkTuck = 0.0;
        _parkStageM = 0.0;
        _parkStageShort = false;
        _reversePark = null;
        Parked = false;
    }

    /// <summary>Remove an explicit destination (the red flag was cleared).
    /// The line must be rebuilt to extend past the old cut point again.</summary>
    public void ClearDestination()
    {
        _dest = null;
        _routeKey = null;
        ParkStyle = "forward";
        _parkStyleLocked = false;
        _parkTuck = 0.0;
        _parkStageM = 0.0;
        _reversePark = null;
        Parked = false;
    }

    /// <summary>True if the route has a place to stop: it ends at a dead end,
    /// or an explicit destination (red flag) was set.</summary>
    public bool HasDestination() => RouteEndsDeadEnd() || _dest is not null;

    /// <summary>Distance (m) along the reference line to the destination;
    /// null if the route has no destination.</summary>
    public double? DistanceToDestination()
    {
        if (_ref is null || !HasDestination()) return null;
        return Math.Max(0.0, _ref.Total - ParkS());
    }

    // ====================================================================
    // Lane offset / merge planning
    // ====================================================================

    /// <summary>Nominal lane offset for this run (m right of the centreline).
    /// The SETTLED line: the road's normal position - centre of the outermost
    /// DRIVING lane on multi-lane carriageways, fixed 1.75 m elsewhere -
    /// clamped to what the route allows. If the car was SPAWNED at/right of
    /// that position, the spawn line is the nominal one: the car holds it up
    /// to the flag and parks from it.</summary>
    private double LaneBaseOffset(double maxOffset)
    {
        var seg = DestSegment() ?? _network.Segments[_car.SegIdx];
        double baseOff = Math.Min(Config.LaneBaseOffsetM(seg.Width, seg.Lanes,
                                                          seg.ParkingLaneWidth, seg.Oneway),
                                  maxOffset);
        double? ovr = _car.LaneOffsetOverrideM;
        if (ovr is not null)
        {
            double oSpawn = Math.Max(0.0, Math.Min(ovr.Value, maxOffset));
            // The parking lane is for PARKING, not for travelling (user
            // rule): a car spawned in it re-joins the driving lane first and
            // parks from there - the settled line stays at the normal
            // position, NOT the spawn line. Spawned inside the DRIVING strip:
            // hold the spawn line (docs §1 variant).
            double driveEdge = seg.Width / 2.0 - seg.ParkingLaneWidth;
            if (!(oSpawn > driveEdge + 0.25))
                baseOff = Math.Max(baseOff, oSpawn);
        }
        return baseOff;
    }

    /// <summary>Lane-change blend for solve_line (docs §1 variant on
    /// multi-lane roads): (fromM, s0, s1) or null when the car holds its
    /// line. Applies to a spawned lateral offset OUTSIDE the normal driving
    /// lane with a flag destination ahead: LEFT of it (overtaking lane) the
    /// car changes lanes RIGHT onto the normal line; INSIDE THE PARKING LANE
    /// it changes lanes LEFT back into traffic.</summary>
    private (double FromM, double S0, double S1)? MergeParams(
        List<(double X, double Y)> rounded, double maxOffset)
    {
        double? ovr = _car.LaneOffsetOverrideM;
        if (ovr is null || _dest is null)
        {
            if (RefLineMath.PARK_DEBUG)
                Console.WriteLine($"[MERGE] none: ovr={ovr} dest={_dest}");
            return null;
        }
        var seg = DestSegment();
        if (seg is null)
        {
            if (RefLineMath.PARK_DEBUG) Console.WriteLine("[MERGE] none: no dest segment");
            return null;
        }
        double oNorm = Math.Min(Config.LaneBaseOffsetM(seg.Width, seg.Lanes,
                                                       seg.ParkingLaneWidth, seg.Oneway),
                                maxOffset);
        double oSpawn = Math.Max(0.0, Math.Min(ovr.Value, maxOffset));
        double driveEdge = seg.Width / 2.0 - seg.ParkingLaneWidth;
        if (!(oSpawn < oNorm - 0.25 || oSpawn > driveEdge + 0.25))
        {
            _mergeEpisode = null;
            if (RefLineMath.PARK_DEBUG)
                Console.WriteLine($"[MERGE] hold: o_spawn={oSpawn:F2} o_norm={oNorm:F2} " +
                                  $"drive_edge={driveEdge:F2}");
            return null;                      // inside the driving strip: hold
        }
        double? sDest = DestArcSOn(rounded);
        if (sDest is null || sDest < 30.0)
        {
            _mergeEpisode = null;
            if (RefLineMath.PARK_DEBUG) Console.WriteLine($"[MERGE] none: s_dest={sDest}");
            return null;                      // not on this line / too close
        }
        double s1 = Math.Max(20.0, sDest.Value - MERGE_SETTLE_BEFORE_M);
        string direction = oSpawn < oNorm ? "right" : "left";

        // Speed-dependent blend length (user rule): the lane change takes
        // ~LANE_CHANGE_TIME_S, so L scales with the speed DURING the blend -
        // i.e. at zone entry, not at the end. The profile alone is the wrong
        // estimate: it is a CAP, not a prediction. Iterate (s0 = s1 - L,
        // v = entry(s0), L = v*T); both terms are monotone in s0 before the
        // braking ramp, so it settles fast.
        double vRef = _car.Speed;
        if (_profile.Length > 0)
        {
            int last = _profile.Length - 1;
            double lG = MERGE_LEN_MIN_M;
            double vEntry = 0.0;
            for (int i = 0; i < 3; i++)
            {
                double s0g = Math.Max(0.0, s1 - lG);
                double vBuild = Math.Sqrt(_car.Speed * _car.Speed +
                                          2.0 * A_CRUISE * Math.Max(0.0, s0g - S));
                vEntry = Math.Min(TargetSpeed(Math.Min(s0g, last)), vBuild);
                lG = Math.Min(MERGE_LEN_MAX_M,
                              Math.Max(MERGE_LEN_MIN_M, vEntry * LANE_CHANGE_TIME_S));
            }
            // The live speed guards against planning from far away (speed
            // ~0); the estimate is stable per route, so L is too.
            vRef = Math.Max(vRef, vEntry);
        }
        var key = (_dest!.Value.X, _dest.Value.Y, Math.Round(oSpawn, 2));
        if (_mergeKey != key)
        {
            _mergeKey = key;
            _mergeLen = null;           // new episode: recompute L
        }
        double? l = _mergeLen;
        if (l is null)
        {
            l = Math.Min(MERGE_LEN_MAX_M,
                         Math.Max(MERGE_LEN_MIN_M, vRef * LANE_CHANGE_TIME_S));
            // Freeze once the car has committed to the change (at/near the
            // zone): re-planning the length under the wheels would step the
            // reference line laterally and pursuit would overshoot it.
            if (s1 - S <= l + 15.0) _mergeLen = l;
        }
        double s0 = Math.Max(0.0, s1 - l.Value);
        if (RefLineMath.PARK_DEBUG)
            Console.WriteLine($"[MERGE] plan: o_spawn={oSpawn:F2} o_norm={oNorm:F2} " +
                              $"s_dest={sDest:F1} s1={s1:F1} v_ref={vRef:F1} " +
                              $"L={(l is null ? "?" : l.Value.ToString("F1"))} s0={s0:F1}");
        if (s1 - s0 < 5.0)
        {
            _mergeEpisode = null;
            return null;
        }
        _mergeEpisode = (s0, s1, direction);
        return (oSpawn, s0, s1);
    }

    /// <summary>Arc length (m) of the destination along `pts`, or null when
    /// it is not on this line (same 10 m guard as CutPolylineAt).</summary>
    private double? DestArcSOn(List<(double X, double Y)> pts)
    {
        if (_dest is null || pts.Count < 2) return null;
        var (x, y) = _dest.Value;
        int bestI = 0; double bestT = 0.0, bestD2 = double.PositiveInfinity;
        for (int i = 0; i < pts.Count - 1; i++)
        {
            double x0 = pts[i].X, y0 = pts[i].Y;
            double vx = pts[i + 1].X - x0, vy = pts[i + 1].Y - y0;
            double l2 = vx * vx + vy * vy;
            if (l2 < 1e-12) continue;
            double t = Math.Max(0.0, Math.Min(1.0, ((x - x0) * vx + (y - y0) * vy) / l2));
            double d2 = (x0 + t * vx - x) * (x0 + t * vx - x) + (y0 + t * vy - y) * (y0 + t * vy - y);
            if (d2 < bestD2) { bestI = i; bestT = t; bestD2 = d2; }
        }
        if (bestD2 > (10.0 * RefLineMath.PPPM) * (10.0 * RefLineMath.PPPM)) return null;
        double s = 0.0;
        for (int i = 0; i < bestI; i++)
            s += Math.Hypot(pts[i + 1].X - pts[i].X, pts[i + 1].Y - pts[i].Y);
        double ex = pts[bestI + 1].X - pts[bestI].X, ey = pts[bestI + 1].Y - pts[bestI].Y;
        return (s + bestT * Math.Hypot(ex, ey)) / RefLineMath.PPPM;
    }

    // ====================================================================
    // Parking style decision & plan
    // ====================================================================

    /// <summary>Forwards, or forwards-then-reverse (docs §1b)? Decided ONCE
    /// per approach, before the plan engages, because the answer changes the
    /// route line itself (it has to run past the parking spot) and the
    /// pull-over's target offset. Reversing needs three things: a real
    /// destination (a flag), road beyond it to stage the manoeuvre, and a
    /// tuck worth making.</summary>
    private void DecideParkStyle()
    {
        if (Parked || _reversePark is not null) return;
        if (_parkStyleLocked) return;
        if (_dest is null || _ref is null)
        {
            if (ParkStyle != "forward")
            {
                ParkStyle = "forward"; _parkTuck = 0.0; _parkStageM = 0.0;
                _routeKey = null;
            }
            return;
        }
        if (ParkStyle == "reverse")
        {
            // Already committed. Only give up if the line turned out too
            // short to stage on (set by CutPolylineAt) - and then LATCH the
            // choice. Re-deciding it every frame flip-flopped the style, and
            // since every decision invalidates the route key, the reference
            // line was rebuilt on EVERY frame of the approach: the car was
            // following a line that moved under it and left the road.
            if (_parkStageShort)
            {
                Console.WriteLine("↩️  reverse-in parking dropped: not enough road " +
                                  "beyond the destination - parking forwards");
                ParkStyle = "forward"; _parkTuck = 0.0; _parkStageM = 0.0;
                _parkStageShort = false;
                _parkStyleLocked = true;
                _routeKey = null;
            }
            return;
        }
        // Not committed yet: decide while the destination is still far enough
        // away that changing the line costs nothing.
        double? dStop = DistanceToDestination();
        if (dStop is null || dStop < PARK_BLEND_START_M + 5.0) return;

        // The width that matters is the one AT THE DESTINATION, not under the
        // car: the decision is taken far upstream.
        double width = DestSegmentWidth();
        // The forward position the tuck is planned FROM is where this car
        // actually drives (its nominal line - possibly a spawn offset).
        double oLane = LaneBaseOffset(Config.KerbOffsetM(width));
        // Reverse-ONLY parking: the forward phase does NOT pull over. The car
        // drives straight past the spot in its lane and stops there; the
        // reverse covers the FULL lateral distance from lane to kerb - the
        // classic back-in.
        double oFwd = oLane;
        var plan = PlanReverseTuck(width, oFwd);
        if (plan is null) return;
        (double tuck, double stage) = plan.Value;
        ParkStyle = "reverse";
        _parkTuck = tuck;
        _parkStageM = stage;
        _parkStageShort = false;
        _routeKey = null;          // re-cut the line past the flag
        Console.WriteLine($"↩️  parking plan: reverse-in, {tuck:F2} m tuck, " +
                          $"staging {stage:F2} m past the flag " +
                          $"[car at ({_car.X:F0},{_car.Y:F0}) " +
                          $"v={_car.Speed * 3.6:0} km/h, in_turn=" +
                          $"{InTurnBlendZone(S)}]");
    }

    /// <summary>The road segment the destination sits on (null if no
    /// destination or it is not on the current route).</summary>
    private RoadSegment? DestSegment()
    {
        if (_dest is null) return null;
        var (dx, dy) = _dest.Value;
        RoadSegment? best = null; double bestD2 = double.PositiveInfinity;
        var idxs = RouteSegSet.Count > 0 ? RouteSegSet
                                         : Enumerable.Range(0, _network.Segments.Count);
        foreach (int idx in idxs)
        {
            var seg = _network.Segments[idx];
            double ax = seg.X2 - seg.X1, ay = seg.Y2 - seg.Y1;
            double l2 = ax * ax + ay * ay;
            if (l2 < 1e-9) continue;
            double t = Math.Max(0.0, Math.Min(1.0, ((dx - seg.X1) * ax + (dy - seg.Y1) * ay) / l2));
            double px = seg.X1 + t * ax, py = seg.Y1 + t * ay;
            double d2 = (px - dx) * (px - dx) + (py - dy) * (py - dy);
            if (d2 < bestD2) { bestD2 = d2; best = seg; }
        }
        return best;
    }

    /// <summary>Width (m) of the road segment the destination sits on.</summary>
    private double DestSegmentWidth()
    {
        var seg = DestSegment();
        return seg is not null ? seg.Width : _network.Segments[_car.SegIdx].Width;
    }

    /// <summary>Distance (m) from the end of the reference line back to where
    /// the car's reference point (rear axle) comes to rest: the front
    /// overhang at a destination flag (bumper AT the flag, spec §1), a whole
    /// car length at a dead end (front corners stay on the road).</summary>
    private double StopMargin() => _dest is not null ? FRONT_OVERHANG_M : CAR_LENGTH_M;

    /// <summary>The car's arc position, refined to centimetres. The steering
    /// projection (S) is a 1 m-resolution scan and that is deliberately left
    /// alone, but the parking plan turns the distance-to-stop into a brake
    /// demand, so a metre of quantisation there becomes a pulsing brake pedal
    /// and a stop point missed by up to a metre.</summary>
    private double ParkS()
    {
        if (_ref is null) return 0.0;
        return RefLineMath.ProjectS(_ref, _car.X, _car.Y, S,
                                    window: 2.0, globalFallback: false, refine: true);
    }

    /// <summary>Brake & park plan (spec §1). Stateless - evaluated every tick
    /// from the distance to the stop point and the current speed.
    /// Returns (phase, vTarget) with phase in 'lead' | 'decel' | 'swerve' |
    /// 'final', or null while d_stop &gt; d_total(v) (cruise). vTarget is the
    /// speed a plan-following car has at that point; 'lead' carries null (keep
    /// following the normal profile), and 'decel' never asks for MORE speed
    /// than the car already has.</summary>
    private (string Phase, double? VTarget)? ParkingPlan(double dStop, double v)
    {
        double vc = PARK_SWERVE_SPEED_M;
        double a = A_PARK;
        // The longitudinal controller holds speed within a ±0.05 m/s deadband
        // of its target, so "reached V_C" must be tested with a tolerance
        // wider than that band - otherwise the car can rest at V_C + 0.02 and
        // never leave the decel phase.
        double vcTol = vc + 0.1;
        double dDecel = Math.Max(0.0, (v * v - vc * vc) / (2.0 * a));
        double dBrake = dDecel + PARK_BLEND_START_M;
        if (v > vcTol && dStop <= dBrake)
        {
            // Decel phase: brake at A_PARK; the plan-following speed at
            // distance d_stop reaches V_C exactly at the swerve start.
            double curve = Math.Sqrt(vc * vc + 2.0 * a *
                                     Math.Max(0.0, dStop - PARK_BLEND_START_M));
            return ("decel", Math.Min(v, curve));
        }
        // The roll-out zone is fixed by the CREEP speed, not by the current
        // one: sizing it as v*tau made the zone shrink as the car slowed.
        double dFinal = Math.Max(v, vc) * PARK_STOP_TAU;
        if (dStop <= dFinal)
        {
            // Final roll-out: target speed proportional to the remaining
            // distance, so the deceleration eases off to zero at the stop
            // point (spec §1: "kein Rucken beim Stillstand").
            return ("final", Math.Max(0.0, dStop) / PARK_STOP_TAU);
        }
        if (dStop <= PARK_BLEND_START_M)
        {
            // Swerve zone: hold V_C (throttle up if approaching slower).
            return ("swerve", vc);
        }
        if (dStop <= v * PARK_LEAD_S + dBrake)
        {
            // Lead phase: indicator on, hold speed for T_LEAD seconds.
            return ("lead", null);
        }
        return null;
    }

    /// <summary>Truncate `pts` at the point on the polyline closest to (x, y),
    /// keeping `extendM` metres of road BEYOND it. The extension is what makes
    /// reverse-in parking possible: the car has to drive past its parking spot
    /// before it can back into it, so the reference line must not end at the
    /// flag. If the destination is not on this line at all (farther than the
    /// 10 m guard), `pts` is returned unchanged - a later rebuild, once the
    /// route covers it, will cut properly.</summary>
    private List<(double X, double Y)> CutPolylineAt(
        List<(double X, double Y)> pts, double x, double y, double extendM = 0.0)
    {
        double guard2 = (10.0 * RefLineMath.PPPM) * (10.0 * RefLineMath.PPPM);
        int bestI = 0; double bestT = 0.0, bestD2 = double.PositiveInfinity;
        for (int i = 0; i < pts.Count - 1; i++)
        {
            double x0 = pts[i].X, y0 = pts[i].Y;
            double vx = pts[i + 1].X - x0, vy = pts[i + 1].Y - y0;
            double l2 = vx * vx + vy * vy;
            if (l2 < 1e-12) continue;
            double t = Math.Max(0.0, Math.Min(1.0, ((x - x0) * vx + (y - y0) * vy) / l2));
            double px = x0 + t * vx, py = y0 + t * vy;
            double d2 = (px - x) * (px - x) + (py - y) * (py - y);
            if (d2 < bestD2) { bestI = i; bestT = t; bestD2 = d2; }
        }
        // A two-point polyline is cuttable too: a single straight segment
        // arrives here as exactly two points, and refusing to cut it left the
        // centreline running on to the far dead end.
        if (bestD2 > guard2 || pts.Count < 2)
        {
            if (RefLineMath.PARK_DEBUG)
                Console.WriteLine($"[CUTDBG] NO CUT: closest={Math.Sqrt(bestD2) / RefLineMath.PPPM:F1} m " +
                                  $"(guard 10 m), n_pts={pts.Count}");
            return pts;
        }
        if (RefLineMath.PARK_DEBUG)
            Console.WriteLine($"[CUTDBG] cut: {pts.Count} -> {bestI + 2} pts, " +
                              $"closest={Math.Sqrt(bestD2) / RefLineMath.PPPM:F2} m");
        double cx = pts[bestI].X + bestT * (pts[bestI + 1].X - pts[bestI].X);
        double cy = pts[bestI].Y + bestT * (pts[bestI + 1].Y - pts[bestI].Y);
        var outPts = new List<(double X, double Y)>(pts.Take(bestI + 1)) { (cx, cy) };
        if (extendM > 0.0)
        {
            // Walk on along the original polyline from the cut point.
            double left = extendM * RefLineMath.PPPM;
            double px = cx, py = cy;
            int j = bestI + 1;
            while (left > 1e-6 && j < pts.Count)
            {
                var (sx, sy) = pts[j];
                double d = Math.Hypot(sx - px, sy - py);
                if (d <= 1e-9) { j++; continue; }
                if (d >= left)
                {
                    outPts.Add((px + (sx - px) * left / d, py + (sy - py) * left / d));
                    left = 0.0;
                    break;
                }
                outPts.Add((sx, sy));
                left -= d;
                px = sx; py = sy;
                j++;
            }
            if (left > 1e-6)
            {
                // Not enough road beyond the flag to stage the manoeuvre.
                _parkStageShort = true;
            }
        }
        return outPts;
    }
}
