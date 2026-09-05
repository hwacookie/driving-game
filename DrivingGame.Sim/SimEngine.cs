// Sim engine — port of the headless game loop in car/src/main.py (M5).
// Physics + AI driver + safety systems, paced by the host thread at 60 Hz.
// REST API and (later) the Godot client both drive this engine: commands go
// through the queue, state comes back as a /state-shaped dict each frame.

namespace DrivingGame.Sim;

// --- Commands (typed; REST layer + Godot UI both enqueue these) -------------

public abstract record SimCommand;

/// <summary>Teleport: default REPLACES the current car(s) (legacy behavior -
/// e2e suite, cockpit); Add=true ADDS a fresh car alongside them (parallel
/// test runs). Segment mode = arbitrary-segment spawn (stress tests, dev).</summary>
public sealed record TeleportCommand(string? StartPoint, int? Segment,
                                     double? Progress, bool Add, double? Speed) : SimCommand;

/// <summary>Multi-car cleanup: Action "clear" (all) or "remove" (one Uid).</summary>
public sealed record CarsCommand(string Action, int? Uid) : SimCommand;

/// <summary>"validator" is global; "breadcrumbs"/"mode" are per-car when a
/// Uid is given (omit = primary/followed car).</summary>
public sealed record ToggleCommand(bool? Breadcrumbs, bool? Validator,
                                   string? Mode, int? Uid) : SimCommand;

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
    public bool FlagRedNav = true;
    public string? HudLabel;
}

// --- Engine -------------------------------------------------------------------

public sealed class SimEngine
{
    public const double DtFixed = 1.0 / 60.0;

    public RoadNetwork Network { get; }
    public ObstacleManager Obstacles { get; }
    public PhysicsValidator Validator { get; }
    public LaneGuard LaneGuard { get; }
    public Camera Camera { get; }

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

    /// <summary>SIM CLOCK = total physics substeps executed (NOT the frame
    /// counter). A slow wall-clock frame runs 2 substeps in one iteration; if
    /// the exported clock only advanced per iteration, position and time would
    /// decouple. Tying the clock to the substeps keeps them consistent by
    /// construction.</summary>
    private long _simStepsTotal;
    private double _physicsAccum;
    private readonly Dictionary<int, (double X, double Y, double Heading)> _prevRender = new();

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
    public IReadOnlyDictionary<int, Car> Cars => _cars;

    public SimEngine(RoadNetwork network, ObstacleManager obstacles)
    {
        Network = network;
        Obstacles = obstacles;
        Validator = new PhysicsValidator(enabled: true);
        LaneGuard = new LaneGuard(enabled: true);
        Camera = new Camera(Config.WINDOW_WIDTH, Config.WINDOW_HEIGHT);

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
        rx += Math.Cos(rad) * offsetM * Config.PIXELS_PER_METER;
        ry -= Math.Sin(rad) * offsetM * Config.PIXELS_PER_METER;

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
            rx += Math.Cos(rad) * offsetM * Config.PIXELS_PER_METER;
            ry -= Math.Sin(rad) * offsetM * Config.PIXELS_PER_METER;
        }
        else
        {
            rx += Math.Sin(rad) * advanceM * Config.PIXELS_PER_METER;
            ry += Math.Cos(rad) * advanceM * Config.PIXELS_PER_METER;
        }
        return (rx, ry, rh, segIdx, fwd, offsetM);
    }

    /// <summary>Create a fresh Car at a random road point or named start.
    /// `progress` (0..1) places the car at that fraction along the start
    /// segment from the named node; e2e tests use 0.5 so every scenario starts
    /// mid-segment instead of hugging the junction node.</summary>
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
        car.Color = Config.CAR_COLORS[(car.Uid - 1) % Config.CAR_COLORS.Length];
        return car;
    }

    /// <summary>Create a car at `progress` (0..1) along segment `segIdx`,
    /// driving start_node -> end_node in the normal driving position.
    /// Chord-based placement: exact on smooth tracks, but do NOT use it for
    /// sharp-corner segments - see SpawnPosition's note on why chord spawns
    /// sit off-pavement near hairpins. Used by POST /teleport {"segment": N}.</summary>
    public Car CreateCarAtSegment(int segIdx, double progress = 0.5)
    {
        var seg = Network.Segments[segIdx];
        double x = seg.X1 + (seg.X2 - seg.X1) * progress;
        double y = seg.Y1 + (seg.Y2 - seg.Y1) * progress;
        double h = PosMod(Math.Degrees(Math.Atan2(seg.X2 - seg.X1, seg.Y2 - seg.Y1)), 360.0);
        double offsetM = Config.LaneBaseOffsetM(seg.Width, seg.Lanes,
                                                seg.ParkingLaneWidth, seg.Oneway);
        double rad = Math.Radians(h);
        x += Math.Cos(rad) * offsetM * Config.PIXELS_PER_METER;
        y -= Math.Sin(rad) * offsetM * Config.PIXELS_PER_METER;
        var car = new Car(x, y, h, segIdx, new BicycleDriver());
        car.Progress = progress;
        car.Forward = true;
        car.LaneOffsetOverrideM = offsetM;
        car.TrailEnabled = true;
        car.Color = Config.CAR_COLORS[(car.Uid - 1) % Config.CAR_COLORS.Length];
        return car;
    }

    private void RemoveCar(int uid)
    {
        // Remove one car + its flags; re-point the follow at the newest
        // remaining car if the followed one goes.
        _cars.Remove(uid);
        _carFlags.Remove(uid);
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
                    newCar = CreateCarAtSegment(tp.Segment.Value, tp.Progress ?? 0.5);
                else
                {
                    if (!tp.Add)
                        foreach (var uid in _cars.Keys.ToList()) RemoveCar(uid);
                    newCar = CreateCar(tp.StartPoint, tp.Progress);
                }
                // Optional rolling start (m/s): the running turn tests spawn
                // already moving instead of accelerating from a standstill.
                if (tp.Speed is not null) newCar.Speed = tp.Speed.Value;
                _cars[newCar.Uid] = newCar;
                _carFlags[newCar.Uid] = new TestFlags();
                // Unaddressed flags/label (legacy clients that set them before
                // any car existed) are adopted by the new car.
                if (_unaddressedFlags is not null)
                {
                    _carFlags[newCar.Uid] = _unaddressedFlags;
                    _unaddressedFlags = null;
                }
                _followUid = newCar.Uid;
                Camera.SnapTo(newCar.X, newCar.Y, Network.WorldWidth, Network.WorldHeight);
                Console.WriteLine(
                    $"\n🔄 {(tp.Add ? "Added" : "New")} car #{newCar.Uid} ({newCar.Color}) at segment " +
                    $"{newCar.SegIdx}, heading {newCar.Heading:F1}°" +
                    $"{(tp.Speed is not null ? " (rolling start)" : "")}\n");
                break;
            }

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
                // ALL cars step together in the same substep (shared
                // accumulator): every car advances exactly DtFixed per
                // substep, so a multi-car run is deterministic like the
                // single-car one.
                foreach (var c in _cars.Values)
                {
                    _prevRender[c.Uid] = (c.X, c.Y, c.Heading);
                    double preX = c.X, preY = c.Y, preH = c.Heading;
                    c.Update(DtFixed, Network, carControl.GetValueOrDefault(c.Uid, ControlInput.Empty));

                    // Stop-on-contact with obstacles (ALL modes): brake at
                    // full braking and clamp so the body box never
                    // interpenetrates an obstacle - the car rests against it.
                    bool inContact = Obstacles.ApplyContactStop(c, DtFixed, preX, preY, preH);

                    // Physics validation (independent check). While in contact
                    // the motion is externally constrained by a solid object,
                    // so the validator suspends the turning-radius invariant
                    // for that frame - jump/snap/off-road checks keep running.
                    Validator.Check(c, DtFixed, Network, inContact: inContact);

                    // Lane guard: wrong-side driving (skipped during active
                    // turns - lateral offset from the incoming centreline is
                    // expected; and for the whole U-turn, where crossing the
                    // centreline is INTENDED).
                    var nav = c.BicycleNav;
                    bool inTurn = nav is not null && nav.InTurnBlendZone(nav.S);
                    bool uturnNow = nav is not null && nav.UturnActive;
                    bool onWrongSide = false;
                    if (!inTurn && !uturnNow)
                        onWrongSide = LaneGuard.Check(c, DtFixed, Network);

                    // Off-road check (FREE mode only): stop the car + warning.
                    bool freeOffRoad = false;
                    if (c.Driver!.GetName() == "FREE")
                    {
                        freeOffRoad = !c.IsOnRoad(Network);
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
                }
            }
        }

        // --- Camera follow position (fix-your-timestep interpolation) ----
        var followed = _followUid is not null ? _cars.GetValueOrDefault(_followUid.Value) : null;
        double rx, ry;
        if (_frozen || followed is null)
        {
            rx = Camera.X; ry = Camera.Y;   // no car / paused: hold the view
        }
        else if (_prevRender.TryGetValue(followed.Uid, out var prev))
        {
            double px = prev.X, py = prev.Y;
            double cx = followed.X, cy = followed.Y;
            if ((cx - px) * (cx - px) + (cy - py) * (cy - py) > 100.0)
            {
                rx = cx; ry = cy;           // Teleport / snap: no lerping across the jump.
            }
            else
            {
                double alpha = _physicsAccum / DtFixed;
                rx = px + (cx - px) * alpha;
                ry = py + (cy - py) * alpha;
            }
        }
        else
        {
            rx = followed.X; ry = followed.Y;
        }

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
                // Truncate the reference line at the centreline point so the
                // car parks AT the flag, not at whatever dead end the route
                // happens to reach beyond it.
                Console.WriteLine($"[FLAGDBG] car #{c.Uid} dest set seg={fseg} prog={fprog:F2} px=({fx:F0},{fy:F0})");
                c.BicycleNav.SetDestination(fx, fy);
            }
            else
            {
                Console.WriteLine($"[FLAGDBG] car #{c.Uid} visual-only flag seg={fseg} prog={fprog:F2} px=({fx:F0},{fy:F0})");
            }
            // The route point sits on the segment CENTRELINE; shift it onto
            // the right kerb (the same offset the car uses) so the renderer's
            // grass offset lands the pennant fully off the carriageway.
            double frad = Math.Radians(fh);
            double foff = Config.KerbOffsetM(Network.Segments[fseg].Width);
            tf.FlagRed = new[]
            {
                fx + Math.Cos(frad) * foff,
                fy - Math.Sin(frad) * foff,
                fh,
            };
            tf.FlagRedPending = null;
        }

        // Camera follow (only when moving and not frozen) - follows the
        // INTERPOLATED position. Advance the follow-lag per PHYSICS STEP, not
        // per frame: alpha = 1-(1-f)**steps keeps the camera's sim-time rate
        // uniform when a frame contains two substeps.
        if (!_frozen)
        {
            double camAlpha = 1.0 - Math.Pow(1.0 - Camera.LerpFactor, stepsThisFrame);
            Camera.Update(rx, ry, Network.WorldWidth, Network.WorldHeight,
                          follow: followed is not null && Math.Abs(followed.Speed) > 0.1,
                          alpha: camAlpha);
        }

        UpdateState(followed, stepsThisFrame);
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
    };

    // Python's lane_guard.stats() returns a dict - keep the same key names.
    private static Dictionary<string, object?> LgStatsPayload(LaneGuard.LaneGuardStats s) => new()
    {
        ["wrong_side_frames"] = s.WrongSideFrames,
        ["wrong_side_seconds"] = s.WrongSideSeconds,
    };

    private void UpdateState(Car? followed, int stepsThisFrame)
    {
        // Multi-car: the top-level fields keep describing the PRIMARY
        // (followed) car - legacy consumers (e2e suite, cockpit) are
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
                ["progress"] = c.Progress,
                ["on_road"] = c.IsOnRoad(Network),
                ["wrong_side"] = pcs.OnWrongSide,
                ["blinker_left"] = c.Driver is BicycleDriver bl ? bl.BlinkerLeft : false,
                ["blinker_right"] = c.Driver is BicycleDriver br ? br.BlinkerRight : false,
                ["hazard"] = hazard,
                ["color"] = c.Color,
                ["flags"] = FlagsPayload(_carFlags.GetValueOrDefault(c.Uid)),
                ["hud_label"] = _carFlags.TryGetValue(c.Uid, out var tf) ? tf.HudLabel : null,
            });
        }

        Dictionary<string, object?> state;
        if (followed is null)
        {
            // No car on the map yet (test maps don't auto-spawn): report
            // that, keep camera info current.
            state = new Dictionary<string, object?>
            {
                ["frame"] = Frame,
                ["time"] = _simStepsTotal * DtFixed,
                ["has_car"] = false,
                ["frozen"] = _frozen,
                ["validator_enabled"] = Validator.Enabled,
                ["validator_violations"] = 0,
                ["flags"] = FlagsPayload(null),
                ["hud_label"] = null,
                ["camera_x"] = Camera.X,
                ["camera_y"] = Camera.Y,
                ["camera_zoom"] = Camera.Zoom,
                ["cars"] = carsPayload,
            };
        }
        else
        {
            var car = followed;
            var nav = car.BicycleNav;
            var pcs = _perCarState.GetValueOrDefault(car.Uid);
            state = new Dictionary<string, object?>
            {
                ["frame"] = Frame,
                ["time"] = _simStepsTotal * DtFixed,
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
                ["segment"] = car.SegIdx,
                // Vertical level of the current segment (0 = ground, 1 =
                // bridge): the renderer z-orders the car above its own deck
                // but below any higher level.
                ["level"] = Network.Segments[car.SegIdx].Level,
                ["progress"] = car.Progress,
                ["segment_length"] = Network.Segments[car.SegIdx].Length,
                ["forward"] = car.Forward,
                ["distance_to_junction"] = DistanceToJunction(car, Network),
                ["on_road"] = car.IsOnRoad(Network),
                // Dashboard lamps for the cockpit controller.
                ["braking"] = car.IsBraking,
                ["accelerating"] = car.IsAccelerating,
                ["wrong_side"] = pcs.OnWrongSide,
                ["driver"] = car.Driver!.GetName(),
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
                ["color"] = car.Color,
                ["cars"] = carsPayload,
                // (The breadcrumb trail is NOT exported here: it is a pure
                // visual and the frontend records it client-side from the
                // x/y/heading samples.)
                ["flags"] = FlagsPayload(_carFlags.GetValueOrDefault(car.Uid)),
                ["hud_label"] = _carFlags.TryGetValue(car.Uid, out var tf2) ? tf2.HudLabel : null,
                ["camera_x"] = Camera.X,
                ["camera_y"] = Camera.Y,
                ["camera_zoom"] = Camera.Zoom,
            };
        }

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
