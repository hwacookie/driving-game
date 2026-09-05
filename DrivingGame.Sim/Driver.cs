// Driver classes — abstract base and implementations for controlling cars.
// 1:1 port of car/src/driver.py.

namespace DrivingGame.Sim;

public abstract class Driver
{
    /// <summary>Return control inputs for the car.</summary>
    public abstract ControlInput GetControl(Car car, RoadNetwork network, double dt,
                                            IDictionary<string, bool> keys);

    /// <summary>Driver type name for display ("FREE" / "BICYCLE").</summary>
    public abstract string GetName();

    // Turn-signal intent (BicycleDriver only; the nav reads/clears it).
    public virtual string? PendingTurn { get; protected set; }
    public virtual void SignalTurn(string direction) { }
    public virtual void ClearTurnSignal() { }
    public virtual void SetHazard(bool on, string reason = "") { }

    protected static bool Key(IDictionary<string, bool> keys, string k)
        => keys.TryGetValue(k, out var v) && v;
}

/// <summary>Human player controlling via keyboard (FREE mode).
/// Keys: WASD/arrows drive, Q/E blinkers (held = on, like holding the
/// stalk). Gears work like a real car: while moving forward, S brakes to a
/// stop - pressing S AGAIN engages reverse. While reversing, W brakes to a
/// stop - pressing W AGAIN drives forward. Holding a brake key through zero
/// never shifts gears; a fresh press is required.</summary>
public sealed class KeyboardDriver : Driver
{
    public bool BlinkerLeft { get; private set; }
    public bool BlinkerRight { get; private set; }
    // Previous frame's W/S state - used to detect FRESH key-downs, which are
    // what engage a gear at a standstill.
    private bool _lastW, _lastS;

    public override ControlInput GetControl(Car car, RoadNetwork network, double dt,
                                            IDictionary<string, bool> keys)
    {
        // Momentary blinkers (held = on). Stored on the driver too - the
        // renderer and HUD read them for the lights.
        BlinkerLeft = Key(keys, Config.KEY_Q);
        BlinkerRight = Key(keys, Config.KEY_E);

        bool w = Key(keys, Config.KEY_UP) || Key(keys, Config.KEY_W);
        bool s = Key(keys, Config.KEY_DOWN) || Key(keys, Config.KEY_S);
        var control = new ControlInput
        {
            Accelerate = w,
            Brake = s,
            // Fresh key-down edges (see Car.UpdateFreeMode): a new S press at
            // a standstill engages reverse, a new W press drives forward.
            // Holding the brake through zero must NOT shift.
            AcceleratePressed = w && !_lastW,
            BrakePressed = s && !_lastS,
            SteerLeft = Key(keys, Config.KEY_LEFT) || Key(keys, Config.KEY_A),
            SteerRight = Key(keys, Config.KEY_RIGHT) || Key(keys, Config.KEY_D),
            BlinkerLeft = BlinkerLeft,
            BlinkerRight = BlinkerRight,
        };
        _lastW = w;
        _lastS = s;
        return control;
    }

    public override string GetName() => "FREE";
}

/// <summary>Autonomous driver for BICYCLE mode. Provides high-level intent
/// (accelerate / brake / which way to turn, from the keyboard or the REST
/// API). The car executes it with the kinematic bicycle model
/// (BicycleNav).</summary>
public sealed class BicycleDriver : Driver
{
    // Destination parking (spec §1): the nav evaluates the stateless brake &
    // park plan every tick from (distance to stop point, speed) and owns the
    // deceleration itself - no trigger distance or brake latch here. This
    // driver only mirrors the indicator from the plan's phase (nav.ParkPhase).
    public BicycleDriver() { }

    // "left"/"right"/null - set on the inherited property (protected setter).
    public bool BlinkerLeft { get; private set; }
    public bool BlinkerRight { get; private set; }
    private bool _lastLeft, _lastRight;

    /// <summary>One-shot U-turn (Wenden) request, set by the 'u' key or the
    /// REST API; consumed by BicycleNav on the next frame.</summary>
    public bool UteturnRequested { get; set; }
    private bool _uturnWasActive;

    // Remember whether we were just pulling out of the kerb, so the pull-out
    // blinker can be switched off exactly once.
    private bool _wasPullingOut;

    // Which indicator WE set for an active lane change (merge before
    // parking) - null when no lane-change signal of ours is on. Used to
    // switch it off exactly once when the merge settles, without touching
    // user-signaled or turn indicators.
    private string? _laneChangeSide;

    // Hazard lights (Warnblinkanlage): all four corner blinkers flash.
    // Turned on automatically when the car recognises it cannot continue
    // (e.g. U-turn stall) or manually via the REST API. They stay on for at
    // least HAZARD_MIN_DISPLAY_S so a stuck state is visible, not just a crash.
    public bool Hazard { get; private set; }
    public string HazardReason { get; private set; } = "";
    private double? _hazardOnAt;

    public const double HAZARD_MIN_DISPLAY_S = 5.0;

    // NOTE: the old steering-cam auto-off is gone: it cleared the intent on
    // any steering DIP, which broke multi-decision-point maneuvers (measured
    // on the roundabout). pending_turn is now cleared by the nav when the car
    // actually PASSES the junction the signal was for; ClearTurnSignal does
    // both the light and the intent.

    /// <summary>Turn the hazard lights on/off (with logging). Off is ignored
    /// while the minimum display time is still running - a stuck state must
    /// stay visible for at least 5 seconds.</summary>
    public override void SetHazard(bool on, string reason = "")
    {
        double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        if (on && !Hazard)
        {
            Hazard = true;
            HazardReason = string.IsNullOrEmpty(reason) ? "unspecified" : reason;
            _hazardOnAt = now;
            Console.WriteLine($"\n🚨 HAZARD LIGHTS ON - {HazardReason}\n");
        }
        else if (!on && Hazard)
        {
            double elapsed = _hazardOnAt is double t ? now - t : 0.0;
            if (elapsed < HAZARD_MIN_DISPLAY_S)
            {
                Console.WriteLine($"🚨 Hazard lights stay on: minimum display time " +
                    $"({HAZARD_MIN_DISPLAY_S:0} s) still running ({elapsed:F1} s elapsed)");
                return;
            }
            Hazard = false;
            HazardReason = "";
            _hazardOnAt = null;
            Console.WriteLine("\n🚨 Hazard lights OFF\n");
        }
    }

    public override ControlInput GetControl(Car car, RoadNetwork network, double dt,
                                            IDictionary<string, bool> keys)
    {
        // Update blinkers based on A/D keys
        bool left = Key(keys, Config.KEY_LEFT) || Key(keys, Config.KEY_A);
        bool right = Key(keys, Config.KEY_RIGHT) || Key(keys, Config.KEY_D);

        // Toggle blinkers
        if (left && !_lastLeft)
        {
            BlinkerLeft = !BlinkerLeft;
            if (BlinkerLeft) { BlinkerRight = false; PendingTurn = "left"; }
            else PendingTurn = null;
        }
        if (right && !_lastRight)
        {
            BlinkerRight = !BlinkerRight;
            if (BlinkerRight) { BlinkerLeft = false; PendingTurn = "right"; }
            else PendingTurn = null;
        }

        _lastLeft = left;
        _lastRight = right;

        // W/S for speed control (manual override)
        bool accel = Key(keys, Config.KEY_UP) || Key(keys, Config.KEY_W);
        bool brake = Key(keys, Config.KEY_DOWN) || Key(keys, Config.KEY_S);

        // Destination parking (spec §1): mirror the indicator from the nav's
        // brake & park plan phase. The plan owns the deceleration; nothing
        // here latches a brake.
        var nav = car.BicycleNav;
        bool uturnNow = nav is { UturnActive: true };
        if (_uturnWasActive && !uturnNow)
        {
            // Maneuver just finished: indicator off (spec §5: "Blinker aus").
            BlinkerLeft = false;
        }
        _uturnWasActive = uturnNow;
        if (uturnNow)
        {
            // U-turn in progress: left blinker on for the WHOLE maneuver,
            // throttle on, and the parking/pull-out logic below is suspended
            // (the nav's signed speed profile drives everything).
            BlinkerLeft = true;
            BlinkerRight = false;
            Console.WriteLine($"[TBLINK] CLEAR uturn-active");
            PendingTurn = null;
            return new ControlInput
            {
                Accelerate = true,
                Brake = false,
                SteerLeft = false,
                SteerRight = false,
                BlinkerLeft = true,
                BlinkerRight = false,
            };
        }

        string parkPhase = nav?.ParkPhase ?? "none";
        bool parking = parkPhase is "lead" or "decel" or "swerve" or "final" or "reverse";

        // An explicit turn signal for a REAL branch at the next junction must
        // survive the parking block: on short streets ending in a cul-de-sac,
        // the plan can go active before the car reaches the junction, and
        // wiping pending_turn here used to rebuild the route straight through.
        bool parkingBlockedByTurn = SignalWouldTurn(car, network);

        // Lane change before parking (docs §1 variant, user rule): ALWAYS
        // signal BEFORE changing lanes. The nav owns the timing - on from
        // MERGE_SIGNAL_AHEAD_M before the merge zone starts, off once the car
        // has settled onto the new line. While active it wins over the
        // parking/pull-out signals; when it drops we switch off only the
        // indicator WE set (a user-signaled or turn signal must survive).
        string? laneChange = nav?.LaneChangeSignal;
        if (laneChange is null && _laneChangeSide is not null)
        {
            if (_laneChangeSide == "right") BlinkerRight = false;
            else BlinkerLeft = false;
            _laneChangeSide = null;
        }
        if (laneChange == "right")
        {
            BlinkerRight = true;
            BlinkerLeft = false;
            Console.WriteLine($"[TBLINK] CLEAR lanechange-right");
            PendingTurn = null;
            _laneChangeSide = "right";
        }
        else if (laneChange == "left")
        {
            BlinkerLeft = true;
            BlinkerRight = false;
            Console.WriteLine($"[TBLINK] CLEAR lanechange-left");
            PendingTurn = null;
            _laneChangeSide = "left";
        }
        else if (parking && !parkingBlockedByTurn)
        {
            BlinkerRight = true;
            BlinkerLeft = false;
            Console.WriteLine($"[TBLINK] CLEAR parking phase={parkPhase}");
            PendingTurn = null;
            accel = false;
        }
        else if (nav is not null && nav.PullOutFrames > 0)
        {
            // Pulling out from the right edge: signal LEFT (into lane)
            BlinkerLeft = true;
            BlinkerRight = false;
            Console.WriteLine($"[TBLINK] CLEAR pullout");
            PendingTurn = null;
            accel = true;
            brake = false;
        }
        else if (parkPhase == "stopped")
        {
            // Stopped at the destination: switch the parking blinker off
            // (spec §1: "Blinker aus").
            BlinkerRight = false;
        }
        else if (BlinkerLeft && nav is not null && _wasPullingOut && nav.PullOutFrames <= 0)
        {
            // Pull-out just finished: switch the pull-out blinker off.
            // (Only after an actual pull-out - a user-signaled left turn must
            // stay on until the turn is executed.)
            BlinkerLeft = false;
        }

        _wasPullingOut = nav is not null && nav.PullOutFrames > 0;

        return new ControlInput
        {
            Accelerate = accel,
            Brake = brake,
            SteerLeft = false,   // AI controls steering via road following
            SteerRight = false,
            BlinkerLeft = BlinkerLeft,
            BlinkerRight = BlinkerRight,
        };
    }

    /// <summary>True if the currently pending signal would actually take a
    /// branch at the NEXT junction (a real turn exists there).
    /// Computed here - not from the nav's current route - because the parking
    /// block below runs BEFORE the nav rebuilds for the new signal: reading a
    /// post-rebuild flag would race and wipe a signal set this very frame.
    /// The naive test used to be "chosen != straight": but at a plain
    /// T-junction the stem has no straight-ahead ROAD at all, so
    /// ChooseNextSegment's straight fallback picks whichever branch is
    /// geometrically closest to straight ahead - which can be the very same
    /// segment 'right' or 'left' resolves to. The real test is whether a road
    /// called 'straight' exists here at all: if it does not, ANY signalled
    /// branch is a genuine, deliberate turn.</summary>
    private bool SignalWouldTurn(Car car, RoadNetwork network)
    {
        if (PendingTurn is not ("left" or "right")) return false;
        var seg = network.Segments[car.SegIdx];
        string jnode = car.Forward ? seg.EndNode : seg.StartNode;
        if (!network.NodeDegree.TryGetValue(jnode, out int deg) || deg < 3)
            return false;    // no junction ahead at all
        int? chosen = network.ChooseNextSegment(car.SegIdx, jnode, PendingTurn!);
        if (chosen is null) return false;
        bool hasStraight = false;
        foreach (int idx in network.GetConnectedSegments(jnode))
        {
            if (idx != car.SegIdx && Math.Abs(network.GetExitAngle(car.SegIdx, idx)) < 30.0)
            {
                hasStraight = true;
                break;
            }
        }
        Console.WriteLine($"[TBLINK] SWT seg={car.SegIdx} fwd={car.Forward} jnode={jnode} deg={deg} chosen={chosen} hasStraight={hasStraight}");
        if (!hasStraight) return true;   // no straight road here: any turn is real
        int? straight = network.ChooseNextSegment(car.SegIdx, jnode, "straight");
        Console.WriteLine($"[TBLINK] SWT straight={straight} -> {(chosen != straight)}");
        return chosen != straight;
    }

    /// <summary>Arm a turn signal (used by the REST API one-shot commands and
    /// by BicycleNav's roundabout-exit re-arm). The nav clears it again when
    /// the car passes the junction the signal was for.</summary>
    public override void SignalTurn(string direction)
    {
        Console.WriteLine($"[TBLINK] SIGNAL {direction} (was: {PendingTurn})");
        PendingTurn = direction;
        if (direction == "left") { BlinkerLeft = true; BlinkerRight = false; }
        else { BlinkerRight = true; BlinkerLeft = false; }
    }

    /// <summary>Clear light + intent (called by the nav when the car passes
    /// the junction the signal was for).</summary>
    public override void ClearTurnSignal()
    {
        Console.WriteLine($"[TBLINK] CLEAR nav-auto-off (was: {PendingTurn})");
        BlinkerLeft = false;
        BlinkerRight = false;
        PendingTurn = null;
    }

    public override string GetName() => "BICYCLE";
}
