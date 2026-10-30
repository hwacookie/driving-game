// Sim engine — port of the headless game loop in car/src/main.py (M5).
// Physics + AI driver + safety systems, paced by the host thread at 60 Hz.
// REST API and (later) the Godot client both drive this engine: commands go
// through the queue, state comes back as a /state-shaped dict each frame.

namespace DrivingGame.Sim;

// --- Commands (typed; REST layer + Godot UI both enqueue these) -------------

public abstract record SimCommand;

/// <summary>Teleport: default REPLACES the current car(s) (legacy behavior -
/// the e2e suite relies on it); Add=true ADDS a fresh car alongside them (parallel
/// test runs). Segment mode = arbitrary-segment spawn (stress tests, dev).
/// Vehicle (validated against Config.MODEL_NAMES at the API layer) overrides
/// the default uid-based color of the spawned car. Reverse=true spawns the
/// car facing the OTHER way along the segment (two-way traffic); it starts
/// in its own right-hand lane and drives the loop backwards.</summary>
public sealed record TeleportCommand(string? StartPoint, int? Segment,
                                     double? Progress, bool Add, double? Speed,
                                     string? Model = null, bool Reverse = false,
                                     double? CruiseKmh = null,
                                     // The car's NAME (docs/TESTING.md): a
                                     // scenario authors its cast, so it knows
                                     // that this car IS the oncoming one - a
                                     // name is an intention, and it beats the
                                     // predicates the client would otherwise
                                     // have to guess ("the slowest rolling
                                     // car" named the primary at staging time
                                     // and the camera flapped all run).
                                     // Set WITH the spawn, not in a second
                                     // request: a staged car must carry its
                                     // identity before its first tick.
                                     string? Name = null,
                                     // The two per-car VIEW options of a staged
                                     // cast (issue #9): a detail window for this
                                     // car, and its own tyre lines. null = not
                                     // declared, so the standing behaviour
                                     // stands and an old client sees today's
                                     // picture. Like the name, they travel WITH
                                     // the spawn: the fact exists before the
                                     // first tick and can be stamped into the
                                     // tape's header for the reviewer.
                                     bool? DetailWindow = null,
                                     bool? TyreLines = null,
                                     // The car's PAINT, resolved to `#rrggbb` by the
                                     // API (#18: the word `color` belongs to this,
                                     // not to the type). null = show the photographed
                                     // colour of the model. Visual only - never
                                     // dynamics, never the footprint.
                                     string? Color = null,
                                     // WHY a car is on duty: "emergency" | "civilian".
                                     // R35 used to read it off the sprite name
                                     // (`car.color == "police"`), which made a
                                     // picture decide a right of way. The role is
                                     // declared from now on; null keeps the old
                                     // inference for a cast that says nothing (#18).
                                     string? Role = null) : SimCommand;

/// <summary>Multi-car cleanup: Action "clear" (all) or "remove" (one Uid).</summary>
public sealed record CarsCommand(string Action, int? Uid) : SimCommand;

/// <summary>Change one car's per-car options WHILE IT DRIVES (issue #10): its
/// paint, its own tyre lines, whether a detail window belongs to it.
///
/// The SPAWN carries the same three (TeleportCommand), because a staged car has
/// to own its facts before its first tick. This command is for the change that
/// comes later — a repaint at the kerb, lines switched on to look at a wobble —
/// and it therefore demands a UID: "the primary car" is not a fact the engine
/// may guess, it is a conclusion the client would have to re-derive every frame.
/// Nothing set is a no-op; an unknown uid is dropped, because the car may have
/// completed between the request and the tick that runs it. Every accepted
/// change is written to the tape as a substep-keyed event, so a review replays
/// the MOMENT rather than only the result.</summary>
public sealed record CarOptsCommand(int Uid, string? Color, bool? TyreLines,
                                    bool? DetailWindow) : SimCommand;

/// <summary>"validator" is global; "breadcrumbs"/"mode" are per-car when a
/// Uid is given (omit = primary/followed car).</summary>
public sealed record ToggleCommand(bool? Breadcrumbs, bool? Validator,
                                   string? Mode, int? Uid,
                                   bool? Headlights = null, bool? Taillights = null,
                                   bool? ClearBlinker = null) : SimCommand;

/// <summary>Short per-car HUD text (e.g. "2/3"); Text=null clears.</summary>
public sealed record LabelCommand(int? Uid, string? Text) : SimCommand;

/// <summary>Freeze / resume the simulation; several queued freezes resolve
/// to the LAST one (commands apply in arrival order).</summary>
public sealed record FreezeCommand(bool Frozen) : SimCommand;

/// <summary>Test confirmation flags (PER CAR): green at the scenario start
/// (world px [x, y, heading], immediate), red at its end given as
/// [segment, progress] - resolved to a map position by the engine once the
/// car's route covers that segment. Has* distinguish "key absent" from
/// "explicit null" (clear).</summary>
public sealed record FlagsCommand(int? Uid,
                                  bool HasGreen, double[]? Green,
                                  bool HasRed, (int SegIdx, double Progress)? Red,
                                  bool? RedNav) : SimCommand;

/// <summary>Hazard lights (Warnblinkanlage): explicit on/off, per-car via
/// Uid (omit = primary).</summary>
public sealed record HazardCommand(bool On, int? Uid) : SimCommand;

/// <summary>Re-point the PRIMARY (followed) car - the /state top level
/// describes this car. Every teleport re-points it to the newest
/// car, so a multi-car scenario that spawned its extras AFTER its own car has
/// to say which one it is monitoring (POST /follow).</summary>
public sealed record FollowCommand(int Uid) : SimCommand;

/// <summary>Put a car into the PARKED state where it stands (POST /park,
/// docs §8 'Parked car'): engine off, no route, it stays put and is an
/// obstruction other cars must pass. The car must already exist - a scenario
/// teleports it into a lane first, then parks it.</summary>
public sealed record ParkCommand(int Uid) : SimCommand;

// --- Per-car test pennants + HUD label --------------------------------------

/// <summary>Server-side state of the test pennants + HUD label (port of
/// main.py's TestFlags). Lives in the sim since M5: the remote frontend
/// draws them from /state, the sim only owns their positions. flag_red is
/// given as [segment, progress] and resolved to a map position by the loop
/// once the car's route covers that segment.</summary>
public sealed class TestFlags
{
    public double[]? FlagGreen;                       // [x, y, heading_deg] world px
    public double[]? FlagRed;                         // resolved [x, y, heading_deg]
    public (int SegIdx, double Progress)? FlagRedPending;
    /// <summary>The mark's ALONG position on the route - the number the arrival
    /// test uses (the suite no longer re-derives the flag from segment
    /// endpoints, one flag has one place in sim, picture and test).
    public int FlagRedSeg = -1;
    public double FlagRedProg = -1.0;
    public bool FlagRedNav = true;
    public string? HudLabel;
}

// --- Engine -------------------------------------------------------------------

public sealed class SimEngine
{
    public const double DtFixed = 1.0 / 60.0;

    /// <summary>How much SIM time the host advances per wall second (1.0 =
    /// real time, 4.0 = four times faster). Purely pacing: the physics step
    /// stays DtFixed, the host just runs more of them per wall second. It is
    /// published in /state so the client and the e2e suite can pace on SIM
    /// time at any warp.</summary>
    public double TimeWarp { get; set; } = 1.0;

    public RoadNetwork Network { get; }
    /// <summary>The name this engine's map is known by - stamped into every tape
    /// it starts, so a review can rebuild the ground the cars were recorded on
    /// (#11). "engine boot" for a network nobody named.</summary>
    public string MapName { get; }
    public ObstacleManager Obstacles { get; }
    public PhysicsValidator Validator { get; }
    public LaneGuard LaneGuard { get; }

    /// <summary>External key state fed to drivers each frame (headless
    /// default: nothing pressed — REST control drives the car via
    /// GetControlFor). Smoke tests set this directly.</summary>
    public Dictionary<string, bool>? ExternalKeys { get; set; }

    private static readonly Dictionary<string, bool> NoKeys = new();

    private readonly Dictionary<int, Car> _cars = new();          // uid -> car
    private readonly Dictionary<int, TestFlags> _carFlags = new();
    private TestFlags? _unaddressedFlags;   // legacy clients without a uid;
                                            // adopted by the next spawned car
    private int? _followUid;                                       // PRIMARY car
    private bool _frozen;

    /// <summary>Master switch for car-to-car collision detection + avoidance.
    /// OFF = the two-pass collision machinery is skipped entirely (the legacy
    /// single-pass loop). Toggled via --no-collisions.</summary>
    public bool CollisionsEnabled = true;

    /// <summary>SIM CLOCK = total physics substeps executed (NOT the frame
    /// counter). A slow wall-clock frame runs 2 substeps in one iteration; if
    /// the exported clock only advanced per iteration, position and time would
    /// decouple. Tying the clock to the substeps keeps them consistent by
    /// construction.</summary>
    private long _simStepsTotal;

    // Collision cost measurement (spec: microseconds/call, printed every 5s;
    // budget context ~200 us/car/substep total). Stopwatch ticks accumulated
    // per call, printed + reset every 300 substeps (5 sim-seconds at 60 Hz).
    private long _avoidCapsTicks, _resolveTicks;
    // Quality metric (the user's, 2026-10-19): total metres driven by every
    // car since the sim started. Divided by sim time it is the fleet's
    // fluidity in m/s - see docs/DRIVING_MANEUVERS.md §8 R28 measurement.
    private double _flowMeters = 0.0;
    // R31 standstill error (docs §8): a car that moves less than this many
    // metres within this much WALL time is probably stuck (the user's
    // definition of 2026-10-19, values adjustable later).
    private const double StandstillWindowWallS = 180.0;
    private const double StandstillMinM = 1.0;
    // The PER-SUBSTEP fluidity the tape carries (docs/RECORD_PLAYBACK.md):
    // _flowMeters is cumulative, so it dilutes and cannot show how fluidity
    // CHANGED over a run. A curve needs a rolling window - metres the fleet
    // moved over the last FluidityWindowS sim-seconds, divided by
    // (cars x window) = m/s per car, the same unit as Q1's flow/car.
    private const double FluidityWindowS = 5.0;
    private readonly List<double> _windowMetres = new();
    private double _windowSumM = 0.0;
    private readonly Dictionary<int, (double X, double Y)> _flowPrev = new();
    private readonly Dictionary<int, double> _sinceMeters = new();
    private readonly Dictionary<int, double> _sinceTime = new();
    public static List<string> StandstillErrors = new();
    private int _avoidCapsCalls, _resolveCalls;
    private const long CostReportEverySteps = 300;
    private double _physicsAccum;

    // Per-car frame outputs (wrong side etc.) for /state.
    private readonly Dictionary<int, (bool OnWrongSide, bool FreeOffRoad)> _perCarState = new();

    // Control inputs per car: key = uid; null = "unaddressed" - legacy clients
    // that don't send a uid, applied to the primary (followed) car only.
    private static readonly string[] ControlKeys =
        { "accelerate", "brake", "steer_left", "steer_right",
          "blinker_left", "blinker_right", "uturn" };
    private readonly object _controlLock = new();
    private readonly Dictionary<int, Dictionary<string, bool>> _controlInput = new();
    private Dictionary<string, bool>? _unaddressedControl;   // null-uid bucket

    // Pending commands: a SINGLE global FIFO in strict arrival order (per-key
    // lists used to lose cross-key ordering — a "clear then spawn" burst must
    // apply the clear FIRST even when both land within one frame batch).
    private readonly object _cmdLock = new();
    private List<SimCommand> _commands = new();

    private readonly object _stateLock = new();

    /// <summary>Last /state payload (merged per key, like Python's
    /// game_state.update()). Served by GET /state via StateSnapshot.</summary>
    public Dictionary<string, object?> GameState { get; } = new();

    /// <summary>A consistent copy of the last /state payload (thread-safe;
    /// served by GET /state and polled by POST /wait).</summary>
    public Dictionary<string, object?> StateSnapshot()
    {
        lock (_stateLock) return new Dictionary<string, object?>(GameState);
    }

    /// <summary>Named start points (static map data; served by
    /// GET /start_points). Kept OUT of /state on purpose.</summary>
    public Dictionary<string, Dictionary<string, object?>> StartPoints { get; } = new();

    public long Frame { get; private set; }
    public bool Frozen => _frozen;
    public int? FollowUid => _followUid;

    /// <summary>What the CURRENT scenario asked the window to look at, as
    /// announced by the suite over POST /focus (docs/TESTING.md "Camera focus
    /// for the obstruction scenarios"). It is run state, not client state: the
    /// viewer reads it from /state and re-aims when it changes, and the same
    /// note is recorded on the tape so the reviewer replays the identical
    /// framing. A focus of kind "loc" means NO car is followed — the place is
    /// the subject (user decision 2026-10-24).</summary>
    public FocusNote? CurrentFocus { get; private set; }

    /// <summary>Verdicts the suite filed for this run, newest last.</summary>
    public List<TestResultNote> TestResults { get; } = new();

    /// <summary>File the current scenario's camera focus: live for the viewer,
    /// stamped on the tape for the reviewer.</summary>
    public void SetFocus(FocusNote note)
    {
        CurrentFocus = note with { Step = _simStepsTotal };
        RunTape.NoteFocus(CurrentFocus.Value);
    }

    /// <summary>File one scenario's verdict (POST /test_result).</summary>
    public void NoteTestResult(TestResultNote r)
    {
        var withStep = r with { Step = _simStepsTotal };
        lock (TestResults)
        {
            int at = TestResults.FindLastIndex(x => x.Label == withStep.Label);
            if (at >= 0) TestResults[at] = withStep; else TestResults.Add(withStep);
        }
        RunTape.NoteTestResult(withStep);
    }

    /// <summary>The /state projection of the focus (null when the run never
    /// declared one — every scenario then means "follow the primary car").</summary>
    /// <remarks>public: the REST layer answers it in POST /focus.</remarks>
    public Dictionary<string, object?>? FocusPayload()
    {
        var f = CurrentFocus;
        if (f == null) return null;
        return new Dictionary<string, object?>
        {
            ["kind"] = f.Value.Kind, ["scenario"] = f.Value.Scenario,
            ["label"] = f.Value.Label, ["desc"] = f.Value.Desc,
            ["x"] = f.Value.X, ["y"] = f.Value.Y, ["zoom"] = f.Value.ZoomPxM,
            ["name"] = f.Value.Name, ["uid"] = f.Value.Uid, ["step"] = f.Value.Step,
            ["name_uid"] = f.Value.NameUid,
        };
    }
    public IReadOnlyDictionary<int, Car> Cars => _cars;

    public SimEngine(RoadNetwork network, ObstacleManager obstacles, string mapName = "")
    {
        Network = network;
        Obstacles = obstacles;
        // The name the tape will carry. A host knows which map it was started
        // with; a hand-built network in a unit test does not, and for that the
        // old label stays honest (issue #11: "engine boot" was never a map name,
        // it was what an unnamed engine said about itself).
        MapName = string.IsNullOrWhiteSpace(mapName) ? "engine boot" : mapName;
        // A tape belongs to the SIM, not to a client that happens to ask for
        // one (the user's decision of 2026-10-25: every run is recorded, so
        // live viewing is the tip of a tape that is still being written -
        // docs/RECORD_PLAYBACK.md -> "Recording policy and the growing tape").
        // The recorder is a process static, so an engine that comes up beside
        // one already under way does NOT restart it: a second engine would
        // otherwise erase the run the first one is writing. The stamp rides
        // with the birth - it is the SAME function the /map payload stamps
        // (`MapPayload.cs`), which is what lets the client compare a tape's
        // reference against the ground it drew (docs/SIMULATIONS_REVIEWER.md
        // "map reference must cover the drawn ground").
        if (!RunTape.Recording)
            RunTape.Start(MapName, DtFixed, RoadNetwork.MapFingerprint(Network));
        Validator = new PhysicsValidator(enabled: true);
        LaneGuard = new LaneGuard(enabled: true);

        foreach (var (name, (x, y, h, seg, fwd, off)) in network.StartPoints)
            StartPoints[name] = new Dictionary<string, object?>
            {
                ["x"] = x, ["y"] = y, ["heading"] = h, ["segment"] = seg,
                ["forward"] = fwd, ["lateral_offset_m"] = off,
            };
    }

    // --- Car creation (ports of main.py's _create_car / _spawn_position) ------

    /// <summary>World position/heading where a car spawned at `startPoint` will
    /// sit: normal driving position (right-lane centre) plus the start point's
    /// lateral offset plus `progress` (0..1) along the start segment from the
    /// named node.</summary>
    public (double X, double Y, double Heading, int SegIdx, bool Forward, double OffsetM)
        SpawnPosition(string startPoint, double? progress = null)
    {
        var (rx, ry, rh, segIdx, fwd, latOff) = Network.GetStartPoint(startPoint);
        double nodeX = rx, nodeY = ry;   // the named (degree-1) node itself
        var seg = Network.Segments[segIdx];
        double rad = Math.Radians(rh);

        // Sit in the NORMAL DRIVING POSITION (right-lane centre), not at the
        // kerb: e2e scenarios start in traffic, not parked. Road-aware normal
        // position: on multi-lane carriageways that is the centre of the
        // outermost DRIVING lane - see Config.LaneBaseOffsetM.
        double offsetM = Config.LaneBaseOffsetM(seg.Width, seg.Lanes,
                                                seg.ParkingLaneWidth, seg.Oneway) + latOff;
        rx += Math.Cos(rad) * offsetM;
        ry -= Math.Sin(rad) * offsetM;

        // Advance a fraction of the way in, so short segments work too.
        double progress0 = progress ?? Config.SPAWN_PROGRESS;
        double advanceM = progress0 * seg.Length;
        // The chord above is only the segment's LOGICAL connection. Where the
        // road actually runs - the corner-rounded centreline the pavement is
        // built from - can diverge from it by tens of metres near sharp
        // corners, and a chord-based spawn there sits off the pavement. Place
        // on the real path instead; on straight segments the two agree exactly.
        var rounded = Network.SpawnPathPoint(segIdx, (nodeX, nodeY), advanceM);
        if (rounded is not null)
        {
            rx = rounded.Value.X; ry = rounded.Value.Y; rh = rounded.Value.HeadingDeg;
            rad = Math.Radians(rh);   // local tangent, not the chord heading
            rx += Math.Cos(rad) * offsetM;
            ry -= Math.Sin(rad) * offsetM;
        }
        else
        {
            rx += Math.Sin(rad) * advanceM;
            ry += Math.Cos(rad) * advanceM;
        }
        return (rx, ry, rh, segIdx, fwd, offsetM);
    }

    /// <summary>Create a fresh Car at a random road point or named start.
    /// `progress` (0..1) places the car at that fraction along the start
    /// segment from the named node; e2e tests use 0.5 so every scenario starts
    /// mid-segment instead of hugging the junction node.</summary>
    /// <summary>R35's right-of-way subject. Today it is still INFERRED, but from
    /// the resolved MODEL ("bmw_police") rather than from the word "police": a
    /// car that arrived as `tan` cannot become a police car by spelling, and an
    /// alias still gets the duty it had before. The honest end state is an
    /// explicit `role` on the request — #18 records that as the next slice, and
    /// until it lands this is the one place the inference lives.</summary>
    static bool IsEmergency(string model) => Config.ModelFor(model).Name == "bmw_police";

    public Car CreateCar(string? startPoint = null, double? progress = null)
    {
        Car car;
        if (startPoint is not null)
        {
            var (rx, ry, rh, segIdx, fwd, spawnOffsetM) = SpawnPosition(startPoint, progress);
            car = new Car(rx, ry, rh, segIdx, new BicycleDriver());
            // progress runs start_node -> end_node, so a car travelling
            // backwards along the segment starts near 1.0, not near 0.0.
            double progress0 = progress ?? Config.SPAWN_PROGRESS;
            car.Progress = fwd ? progress0 : 1.0 - progress0;
            car.Forward = fwd;
            // The nav's nominal lane offset follows the SPAWN position, so the
            // car holds its initial lateral line up to the flag and parks from
            // it instead of re-centering.
            car.LaneOffsetOverrideM = spawnOffsetM;
        }
        else
        {
            var (rx, ry, rh, segIdx, _) = Network.RandomRoadPoint();
            car = new Car(rx, ry, rh, segIdx, new BicycleDriver());
            car.Progress = 0.5;
            var seg = Network.Segments[segIdx];
            double segH = Math.Degrees(Math.Atan2(seg.X2 - seg.X1, seg.Y2 - seg.Y1));
            car.Forward = Math.Abs(Math.WrapDeg(rh - segH)) < 90;
        }
        // Breadcrumbs on from the start: the tyre tracks are the main way to
        // see what the car actually did on its way here.
        car.TrailEnabled = true;
        // Multi-car color: deterministic per uid, red first, then the old
        // pygame obstacle palette colors.
        car.Model = Config.MODEL_NAMES[(car.Uid - 1) % Config.MODEL_NAMES.Length];
        car.EmergencyRole = IsEmergency(car.Model);   // R35: the police car is on duty
        return car;
    }

    /// <summary>Create a car at `progress` (0..1) along segment `segIdx`,
    /// driving start_node -> end_node in the normal driving position.
    /// Placement follows the corner-rounded path, NOT the segment's chord:
    /// the chord is only the LOGICAL connection, and where two chords meet at
    /// a node their corner point plus a chord-perpendicular lane offset sits
    /// metres off the pavement and points ~10° off the road's real direction.
    /// The fig8_stress phase spawns 1 m before a corner node (segment 108,
    /// progress 0.959); from that chord pose the car left the pavement within
    /// 0.05 s at 50 km/h and stayed off-road for the rest of the phase, while
    /// the SAME bend entered two segments earlier kept to 2 m of the centre.
    /// On straight segments the two placements agree exactly. Used by
    /// POST /teleport {"segment": N}.</summary>
    public Car CreateCarAtSegment(int segIdx, double progress = 0.5, bool reverse = false)
    {
        var seg = Network.Segments[segIdx];
        double x = seg.X1 + (seg.X2 - seg.X1) * progress;
        double y = seg.Y1 + (seg.Y2 - seg.Y1) * progress;
        double h = PosMod(Math.Degrees(Math.Atan2(seg.X2 - seg.X1, seg.Y2 - seg.Y1)), 360.0);
        // Same rule as SpawnPosition: when the road actually runs somewhere
        // else than the chord, place on it and use its local tangent.
        var rounded = Network.SegmentPathPoint(segIdx, progress * seg.Length);
        if (rounded is not null)
        {
            x = rounded.Value.X;
            y = rounded.Value.Y;
            h = rounded.Value.HeadingDeg;
        }
        // Reverse spawn: face the other way along the same two-way road.
        // The lane offset below is applied relative to the ACTUAL heading,
        // so the car still starts in its own right-hand lane.
        if (reverse) h = PosMod(h + 180.0, 360.0);
        double offsetM = Config.LaneBaseOffsetM(seg.Width, seg.Lanes,
                                                seg.ParkingLaneWidth, seg.Oneway);
        double rad = Math.Radians(h);
        x += Math.Cos(rad) * offsetM;
        y -= Math.Sin(rad) * offsetM;
        var car = new Car(x, y, h, segIdx, new BicycleDriver());
        car.Progress = progress;
        car.Forward = !reverse;
        car.LaneOffsetOverrideM = offsetM;
        car.TrailEnabled = true;
        car.Model = Config.MODEL_NAMES[(car.Uid - 1) % Config.MODEL_NAMES.Length];
        car.EmergencyRole = IsEmergency(car.Model);   // R35: the police car is on duty
        return car;
    }

    private void RemoveCar(int uid)
    {
        // Remove one car + its flags; re-point the follow at the newest
        // remaining car if the followed one goes.
        _cars.Remove(uid);
        _carFlags.Remove(uid);
        _flowPrev.Remove(uid);
        _sinceMeters.Remove(uid);
        _sinceTime.Remove(uid);
        if (_followUid == uid)
            _followUid = _cars.Count > 0 ? _cars.Keys.Max() : null;
    }

    private TestFlags GetOrAddFlags(int? uid) =>
        uid is null
            ? _unaddressedFlags ??= new TestFlags()
            : _carFlags.TryGetValue(uid.Value, out var tf)
                ? tf : _carFlags[uid.Value] = new TestFlags();

    // --- Command queue ----------------------------------------------------------

    public void EnqueueCommand(SimCommand command)
    {
        lock (_cmdLock) _commands.Add(command);
    }

    private void ProcessCommands()
    {
        List<SimCommand> commands;
        lock (_cmdLock)
        {
            commands = _commands;
            _commands = new List<SimCommand>();
        }
        // Payloads apply in arrival order. Any bad command is logged and
        // skipped - one broken client must never kill the whole simulation
        // (multi-car test runs).
        foreach (var cmd in commands)
        {
            try
            {
                ApplyCommand(cmd);
            }
            catch (Exception e)
            {
                Console.WriteLine($"⚠️  API command error (skipped, sim continues): {e}");
            }
        }
    }

    private void ApplyCommand(SimCommand cmd)
    {
        switch (cmd)
        {
            case TeleportCommand tp:
            {
                Car newCar;
                if (tp.Segment is not null)
                    newCar = CreateCarAtSegment(tp.Segment.Value, tp.Progress ?? 0.5,
                                                tp.Reverse);
                else
                {
                    if (!tp.Add)
                        foreach (var uid in _cars.Keys.ToList()) RemoveCar(uid);
                    newCar = CreateCar(tp.StartPoint, tp.Progress);
                }
                // Optional rolling start (m/s): the running turn tests spawn
                // already moving instead of accelerating from a standstill.
                if (tp.Speed is not null) newCar.Speed = tp.Speed.Value;
                // Explicit color override (validated at the API layer);
                // otherwise the uid-based palette assignment above stands.
                if (!string.IsNullOrEmpty(tp.Model))
                    // Store the CANONICAL model, not the word that arrived: a car
                    // HAS a model (#18), so /state.model answers `vw_passat` even
                    // when the request said `tan`. The old word stays legal on the
                    // way in (Config.MODEL_ALIASES) and never escapes as an answer.
                    newCar.Model = Config.ModelFor(tp.Model).Name;
                newCar.EmergencyRole = tp.Role switch
                {
                    "emergency" => true,
                    "civilian" => false,
                    // Nothing declared: the legacy inference, and only here.
                    _ => IsEmergency(newCar.Model),
                };   // R35
                // A SLOW VEHICLE (km/h): the class top speed stays what the
                // sprite's class says, this car just chooses to drive slower -
                // a tractor, a delivery van, a car hunting a parking spot. The
                // test scenarios need it to stage real slow traffic instead of
                // a stopped car (R32 treats a crawler as an obstruction, a
                // queue lead it must not pass).
                if (tp.CruiseKmh is not null && tp.CruiseKmh.Value > 0.0)
                    newCar.CruiseCapMps = tp.CruiseKmh.Value / 3.6;
                _cars[newCar.Uid] = newCar;
                _carFlags[newCar.Uid] = new TestFlags();
                // Unaddressed flags/label (legacy clients that set them before
                // any car existed) are adopted by the new car.
                if (_unaddressedFlags is not null)
                {
                    _carFlags[newCar.Uid] = _unaddressedFlags;
                    _unaddressedFlags = null;
                }
                // A name given WITH the spawn wins over an adopted label: the
                // scenario asked for THIS car to be the parked one, and the
                // legacy label belongs to whatever car the client was thinking
                // of (measured: the suite's /label for the test number made
                // every staged extra adopt it, so the cast came out unnamed).
                if (!string.IsNullOrEmpty(tp.Name))
                {
                    var fl = _carFlags.TryGetValue(newCar.Uid, out var f0)
                        ? f0 : new TestFlags();
                    fl.HudLabel = tp.Name;
                }
                // The per-car view options of the cast (issue #9): on the car for
                // /state (the live window reads them from the frame it already
                // polls), and into the tape header so a review of the run opens
                // with the same windows and the same tracks the test asked for.
                newCar.OptDetailWindow = tp.DetailWindow;
                newCar.OptTyreLines = tp.TyreLines;
                newCar.Color = tp.Color;
                RunTape.SetCarOptions(newCar.Uid, tp.DetailWindow, tp.TyreLines, tp.Color);
                _followUid = newCar.Uid;
                Console.WriteLine(
                    $"\n🔄 {(tp.Add ? "Added" : "New")} car #{newCar.Uid} ({newCar.Model}) at segment " +
                    $"{newCar.SegIdx}, heading {newCar.Heading:F1}°" +
                    $"{(tp.Speed is not null ? " (rolling start)" : "")}\n");
                break;
            }

            case FollowCommand fc:
                if (_cars.ContainsKey(fc.Uid))
                {
                    _followUid = fc.Uid;
                    Console.WriteLine($"🎯 primary car set to #{fc.Uid}");
                }
                break;

            case ParkCommand pc:
                if (_cars.TryGetValue(pc.Uid, out var pcar))
                {
                    // The nav only exists once the car has TICKED (Car.Update
                    // creates it lazily). A car staged while the sim is FROZEN
                    // has never ticked, so parking it used to be a silent no-op
                    // - and an unparked stopped car is a queue lead, which R32
                    // refuses to pass by design, which is what pinned the car
                    // behind the "parked" one at 0 km/h for a whole minute
                    // (measured: 16,145 R32 rejections reading "stopped NOT
                    // parked"). Create the nav the same way the first tick
                    // would, then park it.
                    if (pcar.Driver is BicycleDriver && pcar.BicycleNav is null)
                        pcar.BicycleNav = new BicycleNav(pcar, Network);
                    pcar.BicycleNav?.ParkNow();
                    Console.WriteLine($"🅿  car #{pcar.Uid} PARKED where it stands " +
                                      $"(seg {pcar.SegIdx}, {pcar.X:F1},{pcar.Y:F1})");
                }
                break;

            case CarsCommand cc:
                if (cc.Action == "clear")
                {
                    foreach (var uid in _cars.Keys.ToList()) RemoveCar(uid);
                    Console.WriteLine("API: all cars removed");
                }
                else if (cc.Action == "remove" && cc.Uid is not null)
                    RemoveCar(cc.Uid.Value);
                break;

            case ToggleCommand tg:
            {
                // Per-car part resolves an optional uid; omit = primary car;
                // "validator" stays global.
                int? tuid = tg.Uid ?? _followUid;
                if (tuid is null || !_cars.ContainsKey(tuid.Value)) tuid = _followUid;
                Car? tcar = tuid is not null ? _cars.GetValueOrDefault(tuid.Value) : null;
                if (tg.Breadcrumbs is not null && tcar is not null)
                {
                    tcar.TrailEnabled = tg.Breadcrumbs.Value;
                    Console.WriteLine($"API: Breadcrumbs {(tcar.TrailEnabled ? "ON" : "OFF")} (car #{tuid})");
                }
                if (tg.Headlights is not null && tcar is not null)
                {
                    tcar.HeadlightsOn = tg.Headlights.Value;
                    Console.WriteLine($"API: Headlights {(tcar.HeadlightsOn ? "ON" : "OFF")} (car #{tuid})");
                }
                if (tg.Taillights is not null && tcar is not null)
                {
                    tcar.TaillightsOn = tg.Taillights.Value;
                    Console.WriteLine($"API: Taillights {(tcar.TaillightsOn ? "ON" : "OFF")} (car #{tuid})");
                }
                if (tg.ClearBlinker == true && tcar?.Driver is BicycleDriver clearDrv)
                {
                    clearDrv.ClearTurnSignal();
                    Console.WriteLine($"API: Blinker cleared (car #{tuid})");
                }
                if (tg.Validator is not null)
                {
                    if (tg.Validator.Value) Validator.Enable();
                    else Validator.Disable();
                }
                if (tg.Mode is not null && tcar is not null)
                {
                    if (tg.Mode == "bicycle" && !(tcar.Driver is BicycleDriver))
                    {
                        tcar.Driver = new BicycleDriver();
                        tcar.BicycleNav = null;   // Car.Update recreates it fresh
                        Console.WriteLine($"API: Switched to BICYCLE mode (car #{tuid})");
                    }
                    else if (tg.Mode == "free" && !(tcar.Driver is KeyboardDriver))
                    {
                        tcar.Driver = new KeyboardDriver();
                        Console.WriteLine($"API: Switched to FREE mode (car #{tuid})");
                    }
                }
                break;
            }

            case CarOptsCommand co:
            {
                if (!_cars.TryGetValue(co.Uid, out var oc)) break;   // gone by now
                bool any = false;
                if (!string.IsNullOrEmpty(co.Color) && oc.Color != co.Color)
                { oc.Color = co.Color; any = true; }
                if (co.TyreLines.HasValue && oc.OptTyreLines != co.TyreLines)
                { oc.OptTyreLines = co.TyreLines; any = true; }
                if (co.DetailWindow.HasValue && oc.OptDetailWindow != co.DetailWindow)
                { oc.OptDetailWindow = co.DetailWindow; any = true; }
                if (!any) break;      // a change that changes nothing records nothing
                RunTape.NoteCarOptions(_simStepsTotal, oc.Uid,
                                       co.DetailWindow, co.TyreLines, co.Color);
                Console.WriteLine($"\n🎨 API: car #{oc.Uid} options changed — " +
                                  (co.Color != null ? $"color {co.Color} " : "") +
                                  (co.TyreLines.HasValue ? $"tyre_lines {(co.TyreLines == true ? "on" : "off")} " : "") +
                                  (co.DetailWindow.HasValue ? $"detail_window {(co.DetailWindow == true ? "on" : "off")}" : "") +
                                  $"(step {_simStepsTotal})");
                break;
            }

            case LabelCommand ld:
            {
                int? luid = ld.Uid ?? _followUid;
                if (luid is null || !_cars.ContainsKey(luid.Value))
                    luid = null;     // unaddressed: adopted at next spawn
                GetOrAddFlags(luid).HudLabel = ld.Text;
                break;
            }

            case FreezeCommand fz:
                _frozen = fz.Frozen;
                Console.WriteLine(_frozen ? "\n⏸️  API: simulation FROZEN"
                                          : "▶️  API: simulation resumed");
                break;

            case FlagsCommand fl:
            {
                int? fuid = fl.Uid ?? _followUid;
                if (fuid is null || !_cars.ContainsKey(fuid.Value))
                    fuid = null;     // unaddressed: adopted at next spawn
                var tf = GetOrAddFlags(fuid);
                Car? fcar = fuid is not null ? _cars.GetValueOrDefault(fuid.Value) : null;
                // Only update the keys that are present, so setting the end
                // flag doesn't wipe the start flag (and vice versa).
                if (fl.HasGreen) tf.FlagGreen = fl.Green;
                if (fl.HasRed)
                {
                    if (fl.Red is null)
                    {
                        tf.FlagRed = null;
                        tf.FlagRedPending = null;
                        tf.FlagRedSeg = -1;
                        tf.FlagRedProg = -1.0;
                        if (fcar?.BicycleNav is not null)
                            fcar.BicycleNav.ClearDestination();
                    }
                    else
                    {
                        var (segIdx, prog) = fl.Red.Value;
                        tf.FlagRed = null;
                        tf.FlagRedPending = (segIdx, prog);
                        // red_nav (default True): the flag becomes the car's
                        // navigation destination (park at it). Running turn
                        // tests send red_nav=False - the flag is a visual
                        // end-of-test marker only.
                        tf.FlagRedNav = fl.RedNav ?? true;
                    }
                }
                break;
            }

            case HazardCommand hz:
            {
                int? huid = hz.Uid ?? _followUid;
                Car? hcar = huid is not null ? _cars.GetValueOrDefault(huid.Value) : null;
                if (hcar is not null && hcar.Driver is BicycleDriver bdd)
                    bdd.SetHazard(hz.On,
                        hz.On ? "manual (REST API)" : "manual off (REST API)");
                break;
            }
        }
    }

    // --- Control input (multi-car) ----------------------------------------------

    /// <summary>Merge/replace control flags for one car. Returns the full
    /// bucket (like Python's /control response).</summary>
    public Dictionary<string, bool> SetControl(int? uid, IDictionary<string, object?> flags)
    {
        lock (_controlLock)
        {
            var bucket = ResolveControlBucket(uid, create: true)!;
            foreach (var key in ControlKeys)
                if (flags.TryGetValue(key, out var v) && v is bool bv)
                    bucket[key] = bv;
            return new Dictionary<string, bool>(bucket);
        }
    }

    private Dictionary<string, bool>? ResolveControlBucket(int? uid, bool create)
    {
        if (uid is null)
            return _unaddressedControl ??= create ? EmptyControl() : null;
        if (_controlInput.TryGetValue(uid.Value, out var b)) return b;
        return create ? _controlInput[uid.Value] = EmptyControl() : null;
    }

    private static Dictionary<string, bool> EmptyControl() =>
        ControlKeys.ToDictionary(k => k, _ => false);

    /// <summary>Control inputs for one car (called from the game loop).
    /// An explicit bucket for `uid` wins; otherwise the unaddressed (null)
    /// bucket applies to the PRIMARY car only.</summary>
    public Dictionary<string, bool> GetControlFor(int uid, bool isPrimary = false)
    {
        lock (_controlLock)
        {
            var bucket = ResolveControlBucket(uid, create: false);
            if (bucket is null && isPrimary)
                bucket = _unaddressedControl;
            return bucket is null ? EmptyControl() : new Dictionary<string, bool>(bucket);
        }
    }

    /// <summary>Clear a single control flag (one-shot semantics, e.g. a
    /// blinker that was applied and should not re-trigger on the next frame).
    /// Same bucket resolution as GetControlFor.</summary>
    public void ClearControl(string key, int uid, bool isPrimary = false)
    {
        lock (_controlLock)
        {
            var bucket = ResolveControlBucket(uid, create: false);
            if (bucket is null && isPrimary)
                bucket = _unaddressedControl;
            if (bucket is not null) bucket[key] = false;
        }
    }

    /// <summary>Reset control inputs to default (all false, all cars).</summary>
    public void ResetControls()
    {
        lock (_controlLock)
        {
            foreach (var bucket in _controlInput.Values)
                foreach (var key in ControlKeys)
                    bucket[key] = false;
            if (_unaddressedControl is not null)
                foreach (var key in ControlKeys)
                    _unaddressedControl[key] = false;
        }
    }

    // --- The frame tick -----------------------------------------------------------

    /// <summary>One iteration of the 60 Hz loop. `wallDtSeconds` is the real
    /// elapsed time since the previous call (the host paces; a slow frame
    /// simply runs more substeps, capped).</summary>
    public void Tick(double wallDtSeconds)
    {
        Frame++;

        // Clamp the physics step. If a frame stalls (route rebuild, GC), the
        // real elapsed time can be hundreds of milliseconds; integrating that
        // in one go teleports the car metres downroad, straight through any
        // geometry in between. Better to run briefly in slow motion than to
        // take an unphysical leap.
        double dt = Math.Min(wallDtSeconds, 1.0 / 30.0);

        ProcessCommands();

        // --- Per-car control input (driver + API merge), once per frame; the
        // fixed-timestep substeps below reuse it. ---
        var carControl = new Dictionary<int, ControlInput>();
        if (!_frozen && _cars.Count > 0)
        {
            foreach (var c in _cars.Values)
            {
                var ci = c.Driver!.GetControl(c, Network, dt, ExternalKeys ?? NoKeys);
                bool isPrimary = c.Uid == _followUid;
                var apiC = GetControlFor(c.Uid, isPrimary);
                // API control overrides keyboard for specific keys.
                if (apiC["accelerate"]) ci.Accelerate = true;
                if (apiC["brake"]) ci.Brake = true;
                if (apiC["steer_left"]) ci.SteerLeft = true;
                if (apiC["steer_right"]) ci.SteerRight = true;
                if (apiC["blinker_left"]) ci.BlinkerLeft = true;
                if (apiC["blinker_right"]) ci.BlinkerRight = true;

                // BicycleDriver's turn choice comes from the one-shot blinker:
                // apply once, then consume the flag so it doesn't re-trigger
                // every frame. The driver's own blinker state persists and is
                // cleared by the car via the mechanical auto-off.
                if (apiC["blinker_left"])
                {
                    c.Driver.SignalTurn("left");
                    ClearControl("blinker_left", c.Uid, isPrimary);
                }
                else if (apiC["blinker_right"])
                {
                    c.Driver.SignalTurn("right");
                    ClearControl("blinker_right", c.Uid, isPrimary);
                }
                // U-turn (Wenden) is a one-shot command, like a blinker.
                if (apiC["uturn"] && c.Driver is BicycleDriver bdd)
                {
                    bdd.UteturnRequested = true;
                    ClearControl("uturn", c.Uid, isPrimary);
                }
                carControl[c.Uid] = ci;
            }
        }

        // --- Physics: FIXED-timestep substeps (only when not frozen) ----
        int stepsThisFrame = 0;
        if (_frozen || _cars.Count == 0)
        {
            _physicsAccum = 0.0;          // don't simulate the pause / empty map
        }
        else
        {
            _physicsAccum += Math.Min(dt, 0.25);
            if (_physicsAccum > 4 * DtFixed)
                _physicsAccum = 4 * DtFixed;   // slow motion, never a leap
            while (_physicsAccum >= DtFixed)
            {
                _physicsAccum -= DtFixed;
                stepsThisFrame++;
                _simStepsTotal++;
                // --- Quality metrics (the user's, 2026-10-19) -------------
                // (a) FLEET FLOW: the total metres every car drives, summed -
                // divided by sim time that is the fleet's fluidity in m/s
                // (high = traffic flows, low = it is stuck). Measured here,
                // at the substep START, so every car is counted on EVERY
                // path (the collision branch is skipped for a single car).
                // (b) R31 STANDSTILL ERROR (docs §8): a car that moves less
                // than StandstillMinM within StandstillWindowWallS of WALL
                // time is probably stuck - "something is wrong". Wall time,
                // so the sim window scales with the warp factor.
                // --- the per-substep FLUIDITY the tape carries -------------
                // The cumulative flow_m_total cannot show how fluidity CHANGED
                // over a run - it dilutes. A curve needs a window: metres the
                // fleet moved over the last FluidityWindowS sim-seconds,
                // divided by (cars x window) = m/s per car. Same unit as Q1's
                // flow/car, so a comparison of two runs is curve against curve
                // (docs/RECORD_PLAYBACK.md).
                var simNow31 = _simStepsTotal * DtFixed;
                var winSim31 = StandstillWindowWallS * Math.Max(TimeWarp, 1.0);
                long tFlow0 = StepCost.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
                double movedSum = 0.0;
                int movingCount = 0;
                foreach (var c in _cars.Values)
                {
                    // The substep this car is being updated on - the tape's and
                    // the decision log's clock. Set BEFORE the `continue` below
                    // so a car seen for the first time still carries it.
                    c.SimStep = _simStepsTotal;
                    if (c.Speed > 0.5) movingCount++;
                    if (!_flowPrev.TryGetValue(c.Uid, out var fp))
                    {
                        _flowPrev[c.Uid] = (c.X, c.Y);
                        _sinceTime[c.Uid] = simNow31;
                        continue;
                    }
                    double movedM = Math.Hypot(c.X - fp.X, c.Y - fp.Y);
                    _flowPrev[c.Uid] = (c.X, c.Y);
                    _flowMeters += movedM;
                    movedSum += movedM;
                    _sinceMeters[c.Uid] = _sinceMeters.GetValueOrDefault(c.Uid, 0.0) + movedM;
                    var wStart = _sinceTime.GetValueOrDefault(c.Uid, simNow31);
                    if (simNow31 - wStart < winSim31) continue;
                    // The window is over: did the car go anywhere?
                    if (_sinceMeters[c.Uid] < StandstillMinM)
                    {
                        var msg31 = $"[R31] STANDSTILL ERROR: car {c.Uid} moved " +
                                    $"{_sinceMeters[c.Uid]:F2} m in {StandstillWindowWallS:F0} s wall " +
                                    $"({simNow31 - wStart:F0} s sim) at ({c.X:F1},{c.Y:F1}) " +
                                    $"seg {c.SegIdx} v {c.Speed * 3.6:F1} km/h";
                        StandstillErrors.Add(msg31);
                        Console.WriteLine($"!! {msg31}");
                    }
                    _sinceTime[c.Uid] = simNow31;
                    _sinceMeters[c.Uid] = 0.0;
                }
                if (StepCost.Enabled) StepCost.Add(StepCost.Flow, tFlow0, _cars.Count);
                // Close the rolling window and derive the two fleet numbers the
                // tape stores per substep.
                _windowMetres.Add(movedSum);
                _windowSumM += movedSum;
                int windowSteps = (int)Math.Ceiling(FluidityWindowS / DtFixed);
                while (_windowMetres.Count > windowSteps)
                {
                    _windowSumM -= _windowMetres[0];
                    _windowMetres.RemoveAt(0);
                }
                double elapsedS = _simStepsTotal * DtFixed;
                double windowS = Math.Min(FluidityWindowS, elapsedS);
                double fluidityMpsPerCar = _cars.Count > 0 && windowS > 0.0
                    ? _windowSumM / (_cars.Count * windowS) : 0.0;
                double movingShare = _cars.Count > 0
                    ? (double)movingCount / _cars.Count : 0.0;
                if (CollisionsEnabled && _cars.Count > 1)
                {
                    // TWO-PASS collision substep. Loop A advances every car and
                    // applies the avoidance speed cap + obstacle contact; then
                    // Resolve rolls back any overlapping pair to its pre-step
                    // pose and stops it; loop B validates + lane-guards the
                    // FINAL post-resolution state (the validator must never see
                    // a transient overlap - single-pass was discarded for this).
                    var prevPose = new Dictionary<int, (double X, double Y, double Heading)>(
                        _cars.Count);
                    foreach (var c in _cars.Values)
                    {
                        prevPose[c.Uid] = (c.X, c.Y, c.Heading);
                    }
                    long tAvoid0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    var avoidCaps = CarCollisions.ComputeAvoidCaps(_cars.Values, Network,
                                                                   _simStepsTotal * DtFixed);
                    _avoidCapsTicks += System.Diagnostics.Stopwatch.GetTimestamp() - tAvoid0;
                    _avoidCapsCalls++;
                    if (StepCost.Enabled) StepCost.Add(StepCost.Avoid, tAvoid0, _cars.Count);
                    // R29 (docs §8): the desperation valve. A total lockdown
                    // should be unreachable - prevention (R30) refuses the
                    // entry that would create one. If a cycle of standstill
                    // cars nevertheless persisted, CarCollisions asks for one
                    // car to be taken out of the equation so the sim never
                    // stands there forever. That is an ERROR: it is logged
                    // here and the e2e suite fails on it.
                    if (CarCollisions.DespawnRequests.Count > 0)
                        foreach (var victim in CarCollisions.DespawnRequests)
                            if (_cars.ContainsKey(victim))
                            {
                                RemoveCar(victim);
                                Console.WriteLine($"!! R29: car {victim} DESPAWNED to break a " +
                                                  "standstill cycle (an error - see §8 R29)");
                            }
                    foreach (var c in _cars.Values)
                    {
                        double preX = c.X, preY = c.Y, preH = c.Heading;
                        double preSpeed = c.Speed;
                        var control = carControl.GetValueOrDefault(c.Uid, ControlInput.Empty);
                        // If avoidance already forbids exceeding my CURRENT
                        // speed, don't hand the driver an accelerate signal
                        // at all: Update() advances X/Y using whatever speed
                        // IT sets internally (e.g. BicycleNav's standstill
                        // creep, which nudges forward while accelerate is
                        // held even at the speed cap) BEFORE the cap below
                        // ever runs - so a persistent throttle would creep
                        // the car forward every substep instead of resting.
                        if (avoidCaps.TryGetValue(c.Uid, out double preCap) && preSpeed >= preCap - 0.02)
                            control = new ControlInput
                            {
                                Accelerate = false, Brake = control.Brake,
                                SteerLeft = control.SteerLeft, SteerRight = control.SteerRight,
                                BlinkerLeft = control.BlinkerLeft, BlinkerRight = control.BlinkerRight,
                                AcceleratePressed = control.AcceleratePressed,
                                BrakePressed = control.BrakePressed,
                            };
                        long tUpd0 = StepCost.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
                        c.Update(DtFixed, Network, control);
                        if (StepCost.Enabled) StepCost.Add(StepCost.Upd, tUpd0, 1);
                        if (c.Uid is 37 or 38 && Frame is >= 845 and <= 852)   // TEMPORARY bisection
                            Console.WriteLine($"POST-UPD uid{c.Uid} v={c.Speed:F4}");

                        // Stop-on-contact with obstacles (ALL modes): brake at
                        // full braking and clamp so the body box never
                        // interpenetrates an obstacle - the car rests against it.
                        long tContact0 = StepCost.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
                        Obstacles.ApplyContactStop(c, DtFixed, preX, preY, preH, Network);
                        if (StepCost.Enabled) StepCost.Add(StepCost.Contact, tContact0, 1);
                        if (c.Uid is 37 or 38 && Frame is >= 845 and <= 852)   // TEMPORARY bisection
                            Console.WriteLine($"POST-CONTACT uid{c.Uid} v={c.Speed:F4}");

                        // Car-to-car avoidance: cap to the fastest speed from
                        // which CAR_BRAKING still stops behind the nearest car
                        // in my forward corridor (pre-step positions). Applied
                        // AFTER the driver's own longitudinal logic ran, so it
                        // works for every driver without touching their code.
                        // Decel-limited from the PRE-UPDATE speed, not the
                        // driver's output: the driver still thinks it can
                        // accelerate (it doesn't know about the other car) and
                        // adds a small amount BEFORE this runs; braking from
                        // its bumped-up output nets less than CAR_BRAKING of
                        // actual deceleration and overshoots the standstill
                        // gap. Braking from the pre-substep speed guarantees
                        // the full CAR_BRAKING rate whenever the cap binds.
                        long tCap0 = StepCost.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
                        if (avoidCaps.TryGetValue(c.Uid, out double cap))
                        {
                            double ceiling = Math.Max(cap, preSpeed - Config.CAR_BRAKING * DtFixed);
                            if (c.Speed > ceiling) c.Speed = ceiling;
                            // The cap IS this car's brake: when it actively holds
                            // the car down - clamping speed, or holding it at rest
                            // against a stopped leader / yield line - the driver
                            // has their foot on the brake, so the light comes on.
                            // The cap is a smooth function of gap, so the light
                            // stays steady (no tick-rate flicker); free flow has
                            // no binding cap and stays dark.
                            bool held = c.Speed >= ceiling - 0.02;
                            if (held && (preSpeed > ceiling + 0.02 || ceiling < 1.0))
                                c.IsBraking = true;
                        }
                        if (StepCost.Enabled) StepCost.Add(StepCost.Caps, tCap0, 1);
                        if (c.Uid is 37 or 38 && Frame is >= 845 and <= 852)   // TEMPORARY bisection
                            Console.WriteLine($"POST-CAP uid{c.Uid} v={c.Speed:F4}");
                    }
                    // Speed of every car AFTER the step + cap but BEFORE
                    // Resolve's rollback zeroes it - this is the impact speed
                    // the crash decision log reports.
                    var impactSpeed = new Dictionary<int, double>(_cars.Count);
                    foreach (var c in _cars.Values) impactSpeed[c.Uid] = c.Speed;
                    long tResolve0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    HashSet<int> inContact = CarCollisions.Resolve(_cars.Values, prevPose,
                                                                   _simStepsTotal * DtFixed,
                                                                   impactSpeed, Network);
                    if ((inContact.Contains(37) || inContact.Contains(38)) && Frame is >= 845 and <= 852)   // TEMPORARY
                        Console.WriteLine($"RESOLVE-STOPPED 37/38: {string.Join(",", inContact)}");
                    _resolveTicks += System.Diagnostics.Stopwatch.GetTimestamp() - tResolve0;
                    _resolveCalls++;
                    if (StepCost.Enabled) StepCost.Add(StepCost.Resolve, tResolve0, _cars.Count);

                    foreach (var c in _cars.Values)
                    {
                        long tV0 = StepCost.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
                        Validator.Check(c, DtFixed, Network,
                                        simTime: _simStepsTotal * DtFixed,
                                        inContact: inContact.Contains(c.Uid));
                        if (StepCost.Enabled) StepCost.Add(StepCost.Validate, tV0, 1);
                    }
                    foreach (var c in _cars.Values)
                    {
                        long tGuard0 = StepCost.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
                        // Lane guard: the guard owns the whole decision now -
                        // it asks the R33 intent (turn swing, U-turn, R32 pass,
                        // merge, pull-out) itself, so the caller no longer
                        // pre-skips some maneuvers and the guard carries a
                        // second, older skip. One owner, one question.
                        bool onWrongSide = LaneGuard.Check(c, DtFixed, Network,
                                                          simTime: _simStepsTotal * DtFixed);

                        // Off-road check (FREE mode only): stop the car + warning.
                        bool freeOffRoad = false;
                        if (c.Driver!.GetName() == "FREE")
                        {
                            long tRoad0 = StepCost.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
                            freeOffRoad = !c.IsOnRoad(Network);
                            if (StepCost.Enabled) StepCost.Add(StepCost.Road, tRoad0, 1);
                            if (freeOffRoad) c.Speed = 0;
                            // Map edge check.
                            if (c.X < 0 || c.X > Network.WorldWidth ||
                                c.Y < 0 || c.Y > Network.WorldHeight)
                            {
                                c.Speed = 0;
                                c.X = Math.Clamp(c.X, 0, Network.WorldWidth);
                                c.Y = Math.Clamp(c.Y, 0, Network.WorldHeight);
                            }
                        }
                        _perCarState[c.Uid] = (onWrongSide, freeOffRoad);
                        if (StepCost.Enabled) StepCost.Add(StepCost.Guard, tGuard0, 1);
                    }
                }
                else
                {
                    // Legacy single pass (collisions disabled or one car).
                    foreach (var c in _cars.Values)
                    {
                        double preX = c.X, preY = c.Y, preH = c.Heading;
                        long tUpd0 = StepCost.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
                        c.Update(DtFixed, Network, carControl.GetValueOrDefault(c.Uid, ControlInput.Empty));
                        if (StepCost.Enabled) StepCost.Add(StepCost.Upd, tUpd0, 1);

                        // Stop-on-contact with obstacles (ALL modes): brake at
                        // full braking and clamp so the body box never
                        // interpenetrates an obstacle - the car rests against it.
                        long tContact0 = StepCost.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
                        bool inContact = Obstacles.ApplyContactStop(c, DtFixed, preX, preY, preH, Network);
                        if (StepCost.Enabled) StepCost.Add(StepCost.Contact, tContact0, 1);
                        long tValidate0 = StepCost.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;

                        // Physics validation (independent check). While in contact
                        // the motion is externally constrained by a solid object,
                        // so the validator suspends the turning-radius invariant
                        // for that frame - jump/snap/off-road checks keep running.
                        Validator.Check(c, DtFixed, Network, inContact: inContact);
                        if (StepCost.Enabled) StepCost.Add(StepCost.Validate, tValidate0, 1);

                        // Lane guard: the guard owns the whole decision now -
                        // it asks the R33 intent (turn swing, U-turn, R32 pass,
                        // merge, pull-out) itself, so the caller no longer
                        // pre-skips some maneuvers and the guard carries a
                        // second, older skip. One owner, one question.
                        long tGuard0 = StepCost.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
                        bool onWrongSide = LaneGuard.Check(c, DtFixed, Network,
                                                          simTime: _simStepsTotal * DtFixed);

                        // Off-road check (FREE mode only): stop the car + warning.
                        bool freeOffRoad = false;
                        if (c.Driver!.GetName() == "FREE")
                        {
                            long tRoad0 = StepCost.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
                            freeOffRoad = !c.IsOnRoad(Network);
                            if (StepCost.Enabled) StepCost.Add(StepCost.Road, tRoad0, 1);
                            if (freeOffRoad) c.Speed = 0;
                            // Map edge check.
                            if (c.X < 0 || c.X > Network.WorldWidth ||
                                c.Y < 0 || c.Y > Network.WorldHeight)
                            {
                                c.Speed = 0;
                                c.X = Math.Clamp(c.X, 0, Network.WorldWidth);
                                c.Y = Math.Clamp(c.Y, 0, Network.WorldHeight);
                            }
                        }
                        _perCarState[c.Uid] = (onWrongSide, freeOffRoad);
                        if (StepCost.Enabled) StepCost.Add(StepCost.Guard, tGuard0, 1);
                    }
                }
                // --- the tape (docs/RECORD_PLAYBACK.md) -------------------
                // One record per SUBSTEP per car, at the substep END: the pose
                // is what the renderer drew, and the cap key is the rule that
                // owns this car's speed - so a standstill is explainable AT a
                // frame when scrubbing, not only after the fact.
                // R16 input: how long each car has been standing. One
                // addition per car per substep, reset when it rolls.
                foreach (var c in _cars.Values)
                    c.StandstillS = c.Speed < 0.5 ? c.StandstillS + DtFixed : 0.0;

                if (RunTape.Recording)
                {
                    long tTape0 = StepCost.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
                    var wrongNow = new Dictionary<int, bool>(_cars.Count);
                    foreach (var e in _perCarState)
                        wrongNow[e.Key] = e.Value.OnWrongSide;
                    var namesNow = new Dictionary<int, string>(_cars.Count);
                    foreach (var kvf in _carFlags)
                        if (kvf.Value.HudLabel is not null && kvf.Value.HudLabel != "")
                            namesNow[kvf.Key] = kvf.Value.HudLabel;
                    RunTape.Record(_simStepsTotal, _cars.Values.ToList(), wrongNow,
                                   namesNow,
                                   fluidityMpsPerCar, movingShare, Network);
                    if (StepCost.Enabled) StepCost.Add(StepCost.Tape, tTape0, _cars.Count);
                }
                // Cost accounting: same window as the collision-cost report
                // (CostReportEverySteps substeps = 5 sim-seconds at 60 Hz).
                // Outside the if/else so a single-car or --no-collisions run
                // still gets a table.
                if (_simStepsTotal % CostReportEverySteps == 0 && StepCost.Enabled)
                    StepCost.Report(CostReportEverySteps, DtFixed);
            }
        }

        // The PRIMARY car: commands that omit an explicit uid apply to it.
        // (The view is a client feature - see MapRenderer's Camera2D.)
        var followed = _followUid is not null ? _cars.GetValueOrDefault(_followUid.Value) : null;

        // Resolve each car's pending RED end flag once ITS route covers that
        // segment (it may lie beyond the initial route horizon).
        foreach (var c in _cars.Values.ToList())
        {
            var tf = _carFlags.GetValueOrDefault(c.Uid);
            if (tf is null || tf.FlagRedPending is null || c.BicycleNav is null) continue;
            var (fseg, fprog) = tf.FlagRedPending.Value;
            if (!c.BicycleNav.RouteSegSet.Contains(fseg)) continue;
            var pos = FlagPositionOnRoute(c.BicycleNav, fseg, fprog);
            if (pos is null) continue;
            double fx = pos[0], fy = pos[1], fh = pos[2];
            if (tf.FlagRedNav)
            {
                // Truncate the reference line at the car's stop line so the
                // car ends AT the flag, not at whatever dead end the route
                // happens to reach beyond it. The flag itself marks the
                // NOSE-STOP line of the parking spot (decision 2026-10-25), so
                // the destination - the car's CENTRE - sits one half-length
                // behind the mark: parking "at the flag" then means what the
                // picture shows, the front bumper touching the pennant's foot.
                double drad = Math.Radians(fh);
                // The mark is the parked car's FRONT-BUMPER line (decision
                // 2026-10-25: after the reverse-in staging the car pulls
                // forward until its nose touches the pennant's foot), while
                // `SetDestination` wants the car's CENTRE - so the destination
                // sits half a car length BEHIND the mark along the travel
                // direction (forward in the sim's y-north frame is
                // (sin h, cos h), same convention `Car.cs:445` drives by).
                double noseBackM = Config.CAR_LENGTH_M / 2.0;
                double destX = fx - Math.Sin(drad) * noseBackM;
                double destY = fy - Math.Cos(drad) * noseBackM;
                Console.WriteLine($"[FLAGDBG] car #{c.Uid} dest set seg={fseg} prog={fprog:F2} " +
                                  $"mark=({fx:F2},{fy:F2}) centre=({destX:F2},{destY:F2})");
                c.BicycleNav.SetDestination(destX, destY);
            }
            else
            {
                Console.WriteLine($"[FLAGDBG] car #{c.Uid} visual-only flag seg={fseg} prog={fprog:F2} px=({fx:F0},{fy:F0})");
            }
            // ONE flag, ONE place (the user's decision of 2026-10-25): the
            // pennant's foot IS the arrival mark - the centre of the line the
            // car has to reach. A running turn test arrives in its DRIVING
            // line (`LaneBaseOffsetM`: 1.75 m right of the centreline on a
            // plain road, the outermost driving lane on a wide one); the
            // parking scenario's mark is the parked car's front bumper, so it
            // sits in the PARKING line (`ParkOffsetM`). It used to be three
            // different places for one flag: the suite tested a point on the
            // CENTRELINE (a 3 m circle around it), the pennant was drawn at
            // the KERB (`KerbOffsetM`), and the car drove in its LANE - which
            // is why reaching the mark cost a lane offset out of the radius
            // (√(3²-1.7²) = ±2.4 m of real window) and could not happen at all
            // on a wide carriageway (measured: `pkw_n→pkw_s`, 19.4 m, 2 lanes
            // → 7.27 m of lateral offset, i.e. the 3 m circle never reached
            // the driving line). Putting the mark IN the line makes the along-
            // track test width-independent: the suite's arrival test is now
            // the car's NOSE crossing the mark's plane, so no radius and no
            // lane offset enters it any more.
            var fseg2 = Network.Segments[fseg];
            double foff = tf.FlagRedNav ? Config.ParkOffsetM(fseg2.Width)
                : Config.LaneBaseOffsetM(fseg2.Width, fseg2.Lanes,
                    fseg2.ParkingLaneWidth, fseg2.Oneway);
            double frad = Math.Radians(fh);
            tf.FlagRed = new[]
            {
                fx + Math.Cos(frad) * foff,
                fy - Math.Sin(frad) * foff,
                fh,
            };
            tf.FlagRedSeg = fseg;
            tf.FlagRedProg = fprog;
            tf.FlagRedPending = null;
        }

        long tPayload0 = StepCost.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
        UpdateState(followed, stepsThisFrame);
        if (StepCost.Enabled) StepCost.Add(StepCost.Payload, tPayload0, _cars.Count);
    }

    /// <summary>Stopwatch-based cost of the collision system, printed every
    /// CostReportEverySteps substeps (5 sim-seconds at 60 Hz) then reset.
    /// Budget context: ~200 us/car/substep total (spec).</summary>
    private void ReportCollisionCost()
    {
        double freq = System.Diagnostics.Stopwatch.Frequency;
        double usAvoid = _avoidCapsCalls > 0
            ? _avoidCapsTicks / freq * 1_000_000.0 / _avoidCapsCalls : 0.0;
        double usResolve = _resolveCalls > 0
            ? _resolveTicks / freq * 1_000_000.0 / _resolveCalls : 0.0;
        Console.WriteLine($"[collision cost] ComputeAvoidCaps {usAvoid:F1} us/call, " +
                          $"Resolve {usResolve:F1} us/call ({_cars.Count} cars, " +
                          $"{_avoidCapsCalls} substeps)");
        _avoidCapsTicks = 0; _resolveTicks = 0;
        _avoidCapsCalls = 0; _resolveCalls = 0;
    }

    // --- State export (the /state payload) ------------------------------------------

    private static double DistanceToJunction(Car car, RoadNetwork net)
    {
        var seg = net.Segments[car.SegIdx];
        return (car.Forward ? (1.0 - car.Progress) : car.Progress) * seg.Length;
    }

    /// <summary>World position + travel heading at `progress` along segment
    /// `segIdx`, following the direction the CURRENT ROUTE traverses that
    /// segment (keeps the flag on the right side even for backward-traversed
    /// segments). Null if the segment isn't in the route's node path.</summary>
    private double[]? FlagPositionOnRoute(BicycleNav nav, int segIdx, double progress)
    {
        var route = nav.Route;
        var seg = Network.Segments[segIdx];
        for (int i = 0; i + 1 < route.Count; i++)
        {
            string a = route[i], b = route[i + 1];
            if (a == b) continue;
            bool aIn = a == seg.StartNode || a == seg.EndNode;
            bool bIn = b == seg.StartNode || b == seg.EndNode;
            if (aIn && bIn)
            {
                var (ax, ay) = Network.Nodes[a];
                var (bx, by) = Network.Nodes[b];
                double x = ax + (bx - ax) * progress;
                double y = ay + (by - ay) * progress;
                double h = PosMod(Math.Degrees(Math.Atan2(bx - ax, by - ay)), 360.0);
                return new[] { x, y, h };
            }
        }
        return null;
    }

    private static Dictionary<string, object?> FlagsPayload(TestFlags? tf) => new()
    {
        ["green"] = tf?.FlagGreen,
        ["red"] = tf?.FlagRed,
        // The mark's ALONG position on the route (segment index + progress
        // 0..1) - the arrival test reads THIS instead of re-deriving a point
        // from the segment endpoints, which is how one flag used to mean three
        // different places (docs/TESTING.md §3).
        ["red_seg"] = tf?.FlagRedSeg,
        ["red_prog"] = tf?.FlagRedProg,
        ["units"] = "meters",
    };

    // Python's lane_guard.stats() returns a dict - keep the same key names.
    private static Dictionary<string, object?> LgStatsPayload(LaneGuard.LaneGuardStats s) => new()
    {
        ["wrong_side_frames"] = s.WrongSideFrames,
        ["wrong_side_seconds"] = s.WrongSideSeconds,
        // Quality metric for the results table: how often the car crossed the
        // centreline (a legal pass of an obstruction = out and back = 2).
        ["centreline_crossings"] = s.CentrelineCrossings,
        // R33: how often it crossed with NO manoeuvre asking for it. Not a
        // quality metric - an error. The corridor makes it unreachable, so a
        // non-zero count means whatever feeds the corridor broke its bounds.
        ["unexplained_crossings"] = s.UnexplainedCrossings,
    };

    private void UpdateState(Car? followed, int stepsThisFrame)
    {
        // Multi-car: the top-level fields keep describing the PRIMARY
        // (followed) car - legacy consumers (the e2e suite) are
        // untouched - while `cars` carries ALL cars; the Godot frontend
        // prefers that array.
        var carsPayload = new List<Dictionary<string, object?>>();
        foreach (var c in _cars.Values)
        {
            var pcs = _perCarState.GetValueOrDefault(c.Uid);
            bool hazard = c.Driver is BicycleDriver bh && bh.Hazard;
            carsPayload.Add(new Dictionary<string, object?>
            {
                ["car_uid"] = c.Uid,
                ["x"] = c.X,
                ["y"] = c.Y,
                ["heading"] = c.Heading,
                ["speed_kmh"] = c.Speed * 3.6,
                ["level"] = Network.Segments[c.SegIdx].Level,
                ["segment"] = c.SegIdx,
                // Symbolic anchor (docs §8 "Segment names"): readable traces.
                ["segment_name"] = Network.Segments[c.SegIdx].Name,
                // The per-car view options AS DECLARED: null (="not said") is
                // what an undeclared cast answers, so the client keeps its
                // standing behaviour instead of guessing (issue #9).
                ["detail_window"] = c.OptDetailWindow,
                ["tyre_lines"] = c.OptTyreLines,
                // The PAINT, under the word the model reserves for it (#18):
                // `#rrggbb` when the car is repainted, null when it shows the
                // photographed colour of its model. `livery` stays answered as the
                // alias #10 introduced, so a client written last week still paints.
                ["color"] = c.Color,
                ["livery"] = c.Color,
                ["progress"] = c.Progress,
                ["on_road"] = c.IsOnRoad(Network),
                ["wrong_side"] = pcs.OnWrongSide,
                // Per-car lane-guard counters so a MULTI-car run can measure
                // centreline crossings and wrong-side time for every car, not
                // just the primary (results table, docs §8 "Liveness & quality").
                ["lane_guard_stats"] = LgStatsPayload(LaneGuard.Stats(c)),
                // Collision contact: true while this car is pinned against
                // another by collision resolution (a crash). The multi-car
                // crossing scenario fails the instant any car reports it.
                // Transient - a contact episode can clear inside one poll,
                // which is what crash_episodes below counts.
                ["in_contact"] = c.InContact,
                ["contact_with"] = c.InContact ? c.ContactWith : (int?)null,
                // Monotonic count of crash episodes this car took part in
                // (R20: the event is already logged as a "[R2] CRASH"
                // decision; this is the machine-readable form so a test
                // polling /state cannot miss the FIRST contact).
                ["crash_episodes"] = c.CrashEpisodes,
                // The exact box used for crash detection (per-class body
                // footprint, LengthM x WidthM centred on the body centre):
                // four [x, y] METRE corners front-right, front-left,
                // rear-left, rear-right. The client uses it as the car's
                // click/hit area.
                ["body_corners"] = ObstacleGeometry.PlayerBodyCorners(c)
                    .Select(p => new[] { p.X, p.Y }).ToList(),
                ["blinker_left"] = c.Driver is BicycleDriver bl ? bl.BlinkerLeft : false,
                ["blinker_right"] = c.Driver is BicycleDriver br ? br.BlinkerRight : false,
                ["hazard"] = hazard,
                ["braking"] = c.IsBraking,
                ["headlights_on"] = c.HeadlightsOn,
                ["taillights_on"] = c.TaillightsOn,
                // The TYPE is `model` (#18). `vehicle` stays answered as #10's
                // transitional name; `color` does NOT name the type any more - that
                // is the flip this slice exists for, and it is why /teleport still
                // ACCEPTS a model word under `color` (with a DEPRECATED line): the
                // way in is wide, the way out is one meaning per key.
                ["model"] = c.Model,
                ["vehicle"] = c.Model,
                // WHY this car is slow right now, as DATA (docs/CAR_DETAIL_WINDOW.md):
                // `cap_key` is the rule (`avoid#12`, `r32#passive`), `held_by`
                // the CAR that owns it (0 = nobody). The viewer's car detail
                // window needs both, and the reader must not have to parse the
                // uid out of the key — `r32#passive` names nobody and
                // `sign#C#7` names the node before the car.
                ["cap_key"] = c.ActiveCapKey ?? "",
                ["held_by"] = c.HeldByUid,
                // Driver class for the renderer's click-to-inspect info window.
                ["driver"] = c.Driver?.GetName() ?? "NONE",
                // The COURTESY signal (docs §6, bitteschön): set by the car
                // that concedes a right-of-way resource, aimed at the car
                // whose passage it grants. The renderer draws the flash from
                // the conceder toward that target - a display of an award
                // that must already be correct, never a decision.
                ["courtesy_for"] = c.CourtesyForUid,
                ["courtesy_kind"] = c.CourtesyKind,
                // Parking state, in the SAME shape the other /state builder
                // answers (the one at the followed-car branch). Without it the
                // client could not tell a parked obstruction from a queue
                // lead, and a camera asked to watch "the parked car" found
                // nothing (measured: /state answered no 'parking' key at all
                // for a staged park_block_priority cast).
                ["parking"] = new Dictionary<string, object?>
                {
                    ["style"] = c.BicycleNav?.ParkStyle,
                    ["phase"] = c.BicycleNav?.ParkPhase ?? "none",
                    ["parked"] = c.BicycleNav?.Parked == true,
                    ["reversing"] = c.BicycleNav?.ReverseParkActive == true,
                },
                ["flags"] = FlagsPayload(_carFlags.GetValueOrDefault(c.Uid)),
                ["hud_label"] = _carFlags.TryGetValue(c.Uid, out var tf) ? tf.HudLabel : null,
            });
        }

        Dictionary<string, object?> state;
        if (followed is null)
        {
            // No car on the map yet (test maps don't auto-spawn): report
            // that; the fleet payload stays empty.
            state = new Dictionary<string, object?>
            {
                // The unit every coordinate in this payload uses - the same
                // declaration /map carries. The sim and the API are one dialect
                // now: metres.
                ["units"] = "meters",
                ["frame"] = Frame,
                ["time"] = _simStepsTotal * DtFixed,
                // How much sim time runs per wall second (the host's pacing
                // switch, --warp / --fast). The e2e suite and the client
                // pace on SIM time, so they need to know it: 1.0 = real time.
                ["time_warp"] = TimeWarp,
                ["has_car"] = false,
                ["frozen"] = _frozen,
                ["validator_enabled"] = Validator.Enabled,
                ["validator_violations"] = 0,
                ["flags"] = FlagsPayload(null),
                ["hud_label"] = null,
                ["cars"] = carsPayload,
                // R28 (docs §8): total lockdowns the sim had to report.
                // Prevention must keep this at 0; the e2e suite fails when
                // it grows (see tests/test_turning.py).
                ["lockdowns"] = CarCollisions.LockdownEvents.Count,
                ["lockdown_last"] = CarCollisions.LockdownEvents.Count > 0
                    ? CarCollisions.LockdownEvents.Last() : "",
                // Quality metric (the user's): metres driven per second of
                // sim time by the WHOLE fleet (see §8 R28 measurement).
                ["flow_m_total"] = _flowMeters,
                ["flow_m_per_s"] = _flowMeters /
                    Math.Max(_simStepsTotal * DtFixed, 1e-9),
                // R31 (docs §8): cars that moved < StandstillMinM within
                // StandstillWindowWallS of wall time. 0 = nothing stuck.
                ["standstill_errors"] = StandstillErrors.Count,
                ["standstill_last"] = StandstillErrors.Count > 0
                    ? StandstillErrors.Last() : "",
                // R33 (docs §8): centreline crossings with NO manoeuvre asking
                // for them. 0 = the corridor held. Not a quality metric - an
                // error, and the e2e suite fails when it grows.
                ["lane_guard_errors"] = LaneGuard.UnexplainedCrossings(),
                ["lane_guard_last"] = LaneGuard.LastUnexplained(),
            };
        }
        else
        {
            var car = followed;
            var nav = car.BicycleNav;
            var pcs = _perCarState.GetValueOrDefault(car.Uid);
            state = new Dictionary<string, object?>
            {
                ["units"] = "meters",
                ["frame"] = Frame,
                ["time"] = _simStepsTotal * DtFixed,
                ["time_warp"] = TimeWarp,
                ["has_car"] = true,
                // Monotonic per-car identity: the teleport ack. has_car alone
                // can't confirm a NEW car - it is already True while the old
                // one still exists.
                ["car_uid"] = car.Uid,
                ["x"] = car.X,
                ["y"] = car.Y,
                ["heading"] = car.Heading,
                ["speed"] = car.Speed,
                ["speed_kmh"] = car.Speed * 3.6,
                ["driver"] = car.Driver?.GetName() ?? "NONE",
                ["segment"] = car.SegIdx,
                ["segment_name"] = Network.Segments[car.SegIdx].Name,
                // Vertical level of the current segment (0 = ground, 1 =
                // bridge): the renderer z-orders the car above its own deck
                // but below any higher level.
                ["level"] = Network.Segments[car.SegIdx].Level,
                ["progress"] = car.Progress,
                ["segment_length"] = Network.Segments[car.SegIdx].Length,
                ["forward"] = car.Forward,
                ["distance_to_junction"] = DistanceToJunction(car, Network),
                ["on_road"] = car.IsOnRoad(Network),
                ["in_contact"] = car.InContact,
                ["contact_with"] = car.InContact ? car.ContactWith : (int?)null,
                ["crash_episodes"] = car.CrashEpisodes,
                ["body_corners"] = ObstacleGeometry.PlayerBodyCorners(car)
                    .Select(p => new[] { p.X, p.Y }).ToList(),
                // Lamps + longitudinal state: what the client's car inspector shows.
                ["braking"] = car.IsBraking,
                ["accelerating"] = car.IsAccelerating,
                ["wrong_side"] = pcs.OnWrongSide,
                ["driver"] = car.Driver!.GetName(),
                ["courtesy_for"] = car.CourtesyForUid,
                ["courtesy_kind"] = car.CourtesyKind,
                // Parking state for the e2e suite: a reverse-in park
                // deliberately crosses the flag to stage the back-in, so the
                // suite must wait for 'parked' instead of latching arrival.
                ["parking"] = new Dictionary<string, object?>
                {
                    ["style"] = nav?.ParkStyle,
                    ["phase"] = nav?.ParkPhase ?? "none",
                    ["parked"] = nav is not null && nav.Parked,
                    ["reversing"] = nav is not null && nav.ReverseParkActive,
                },
                ["trail_enabled"] = car.TrailEnabled,
                ["validator_enabled"] = Validator.Enabled,
                // Per-car counters (each new car starts at zero).
                ["validator_violations"] = Validator.Count(car),
                ["hazard"] = car.Driver is BicycleDriver hz && hz.Hazard,
                ["hazard_reason"] = car.Driver is BicycleDriver hr ? hr.HazardReason : "",
                ["frozen"] = _frozen,
                ["blinker_left"] = car.Driver is BicycleDriver bl2 ? bl2.BlinkerLeft : false,
                ["blinker_right"] = car.Driver is BicycleDriver br2 ? br2.BlinkerRight : false,
                ["lane_guard_stats"] = LgStatsPayload(LaneGuard.Stats(car)),
                // Multi-car: display color + the full car list.
                ["model"] = car.Model,      // the type (#18)
                ["vehicle"] = car.Model,    // #10's transitional alias
                ["color"] = car.Color,      // the PAINT — no longer the type
                // The same two keys the fleet builder answers — a client must
                // not get a different contract because the map held one car
                // instead of fifty.
                ["cap_key"] = car.ActiveCapKey ?? "",
                ["held_by"] = car.HeldByUid,
                ["cars"] = carsPayload,
                // (The breadcrumb trail is NOT exported here: it is a pure
                // visual and the frontend records it client-side from the
                // x/y/heading samples.)
                ["flags"] = FlagsPayload(_carFlags.GetValueOrDefault(car.Uid)),
                ["hud_label"] = _carFlags.TryGetValue(car.Uid, out var tf2) ? tf2.HudLabel : null,
                ["lockdowns"] = CarCollisions.LockdownEvents.Count,
                ["lockdown_last"] = CarCollisions.LockdownEvents.Count > 0
                    ? CarCollisions.LockdownEvents.Last() : "",
                ["flow_m_total"] = _flowMeters,
                ["flow_m_per_s"] = _flowMeters /
                    Math.Max(_simStepsTotal * DtFixed, 1e-9),
                ["standstill_errors"] = StandstillErrors.Count,
                ["standstill_last"] = StandstillErrors.Count > 0
                    ? StandstillErrors.Last() : "",
                // R33 (docs §8): centreline crossings with NO manoeuvre asking
                // for them. 0 = the corridor held. Not a quality metric - an
                // error, and the e2e suite fails when it grows.
                ["lane_guard_errors"] = LaneGuard.UnexplainedCrossings(),
                ["lane_guard_last"] = LaneGuard.LastUnexplained(),
            };
        }

        // The scenario's camera focus + the verdicts filed so far, added once
        // for BOTH builders above: a client that follows the focus must see it
        // whether the map holds one car or fifty (the fleet branch and the
        // followed-car branch differ in everything else — this is run state,
        // not car state).
        state["focus"] = FocusPayload();
        lock (TestResults)
            state["test_results"] = TestResults.Select(r => new Dictionary<string, object?>
            {
                ["number"] = r.Number, ["label"] = r.Label, ["scenario"] = r.Scenario,
                ["passed"] = r.Passed, ["reason"] = r.Reason,
                ["sim_s"] = r.SimS, ["wall_s"] = r.WallS,
                ["crossings"] = r.Crossings, ["off_road"] = r.OffRoad,
            }).ToList();

        // Merge per key (Python: game_state.update(state)) - keys set by an
        // earlier variant (e.g. 'x' while a car existed) survive.
        lock (_stateLock)
            foreach (var (k, v) in state)
                GameState[k] = v;
    }

    /// <summary>Spawn the initial car and make it primary (port of main.py's
    /// startup: OSM maps get a random spawn; test maps spawn none - the e2e
    /// suite teleports one in).</summary>
    public Car SpawnInitialCar(string? startPoint)
    {
        var car = CreateCar(startPoint);
        _cars[car.Uid] = car;
        _carFlags[car.Uid] = new TestFlags();
        _followUid = car.Uid;
        return car;
    }

    private static double PosMod(double a, double m) => ((a % m) + m) % m;
}
