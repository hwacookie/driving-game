// Car — pure physics and state. No input handling; controlled by Driver
// classes. 1:1 port of car/src/car.py.

namespace DrivingGame.Sim;

/// <summary>Per-frame control inputs (the Python control dict).</summary>
public sealed class ControlInput
{
    public bool Accelerate;
    public bool Brake;
    public bool SteerLeft;      // FREE mode only
    public bool SteerRight;     // FREE mode only
    public bool BlinkerLeft;    // BICYCLE mode, from driver state
    public bool BlinkerRight;   // BICYCLE mode, from driver state
    public bool AcceleratePressed;  // fresh W key-down (FREE gear shift)
    public bool BrakePressed;       // fresh S key-down (FREE gear shift)

    public static ControlInput Empty => new();
}

public class Car
{
    /// <summary>A car with physics and state. Controlled by a Driver.
    /// (Rendering lives in the remote frontend since M5.)</summary>

    // Monotonic per-instance identity. NOT id(): .NET can reuse addresses,
    // so anything keying per-car state on identity must use this uid.
    private static int _nextUid = 0;

    public int Uid { get; }

    /// <summary>Display color (multi-car): assigned deterministically from
    /// the uid by the main loop; "blue" (the sedan) is the default.</summary>
    public string Color { get; set; } = "blue";

    /// <summary>Dynamics of this car's vehicle class (top speed, accel,
    /// cornering budget) - see Config.VEHICLE_CLASS_SPECS.</summary>
    public Config.VehicleSpec Spec => Config.SpecForColor(Color);
    public double TopSpeedMps => Spec.TopSpeedMps;
    /// <summary>What this car CHOOSES to drive at, m/s (0 = no choice, use the
    /// class limit). The class top speed stays the physical ceiling - a mixer
    /// still cannot do 180 km/h - but a driver may hold far below it: a tractor
    /// at 10 km/h, a van hunting a parking spot. R32 reads the difference: a
    /// crawler is an obstruction to pass, a queue lead that merely happens to
    /// be at 0 is not.</summary>
    public double CruiseCapMps { get; internal set; } = 0.0;
    /// <summary>The speed that actually applies: the driver's choice inside the
    /// class ceiling.</summary>
    public double EffectiveTopMps => CruiseCapMps > 0.0
        ? Math.Min(TopSpeedMps, CruiseCapMps) : TopSpeedMps;
    public double AccelMps2 => Spec.AccelMps2;
    public double LatAccelMax => Spec.LatAccelMax;

    /// <summary>Physical footprint of this car's sprite class (width × length,
    /// metres) - the same table the renderer draws from (Config.VEHICLE_SIZES).</summary>
    public (double WidthM, double LengthM) Size => Config.SizeForColor(Color);
    public double WidthM => Size.WidthM;
    public double LengthM => Size.LengthM;

    // Position and orientation (world metres; heading degrees, 0 = north)
    public double X { get; internal set; }
    public double Y { get; internal set; }
    public double Heading { get; internal set; }

    // Speed (m/s; signed in FREE mode / reverse maneuvers)
    public double Speed { get; internal set; }
    public double TargetSpeed { get; internal set; }

    // Engaged gear (FREE mode, like a real shifter): null = neutral,
    // 'fwd' or 'rev'. Set by a FRESH key press at a standstill.
    private string? _gear;
    // Sim time accumulated from dt (FREE mode), so the R25 speed-limit clamp
    // can timestamp its decision-log entry. Cars are stepped at the fixed dt.
    private double _freeTime;
    // True while the top-speed clamp is actively biting, so R25 logs once on
    // the transition (cap engaged) rather than every tick at top speed.
    private bool _speedCapEngaged;
    // Virtual steering wheel position (FREE mode), -1..+1. Ramps toward the
    // demanded direction at a finite rate (Config.STEER_LOCK_TIME_S).
    private double _steerPos;

    /// <summary>Current steering angle in RADIANS, set by the navigation
    /// model each physics step (+ = right). The driver's mechanical blinker
    /// auto-off (real-car steering cam) reads this.</summary>
    public double SteerAngle { get; internal set; }

    // Road following state (segment index + progress along it)
    public int SegIdx { get; internal set; }
    public double Progress { get; internal set; } = 0.5;   // 0..1 along segment
    public bool Forward { get; internal set; } = true;

    // Driver controlling this car
    public Driver? Driver { get; set; }

    // Bicycle-model navigation (created lazily on first use - see Update)
    public BicycleNav? BicycleNav { get; set; }

    /// <summary>Nominal lateral line override (m right of the centreline),
    /// set at spawn for named start points / segment spawns. null = the nav
    /// uses the road's normal driving position.</summary>
    public double? LaneOffsetOverrideM { get; set; }

    // Visual state
    public bool IsBraking { get; internal set; }
    public bool IsAccelerating { get; internal set; }
    /// <summary>Headlights (front, white) on/off - test + REST /toggle only,
    /// no gameplay effect. Independent of TaillightsOn so a QA script can
    /// isolate each light.</summary>
    public bool HeadlightsOn { get; set; }
    /// <summary>Taillights (rear, dark red) on/off - independent of
    /// HeadlightsOn. Shows bright red instead while IsBraking, regardless
    /// of this flag (a real brake light works even with the lights off).</summary>
    public bool TaillightsOn { get; set; }

    // --- Negotiation state (docs/RIGHT_OF_WAY_NEGOTIATION.md) -------------
    /// <summary>R35 (docs §5): the ROLE this car drives with. The emergency
    /// vehicle changes PREFERENCE only - it never changes what is physically
    /// possible, so it does not get through a pinch a normal car could not
    /// fit through. Default follows the sprite: a police car is on duty.</summary>
    public bool EmergencyRole { get; set; }
    /// <summary>Seconds this car has been standing still (speed below the
    /// crawl line), reset the moment it rolls. R16 anti-starvation reads it:
    /// a car that keeps losing a contested stretch becomes more expensive
    /// until it wins it.</summary>
    public double StandstillS { get; internal set; }
    /// <summary>The COURTESY signal (docs §6, the user's word: bitteschön).
    /// Set by the car that CONCEDES a right-of-way resource - the one
    /// WITHOUT the right - and aimed at the car whose passage it grants,
    /// named by the resource's target. A display of an award that must
    /// already be correct, never a decision of its own.</summary>
    public long CourtesyForUid { get; internal set; }
    public string CourtesyKind { get; internal set; }   // sign# / r14# / passage# / r32#

    // --- Driver decision log -------------------------------------------------
    // EVENTS, not per-frame samples: one entry each time the reason this car
    // is being held back CHANGES (new threat, threat gone, ...). The
    // frontend's car-detail window shows the tail of this list, so a stuck
    // car always answers "why am I stopped?".
    public sealed class CarDecision
    {
        public double T;      // sim time (s)
        public long Step;     // SUBSTEP index - the tape's clock (see docs/RECORD_PLAYBACK.md)
        public string Msg = "";
    }
    /// <summary>The text of the MOST RECENT decision this car made, and the
    /// substep it was made on. The tape reads these two together at the moment
    /// of recording, so a decision reaches the tape the instant it happens -
    /// the 100-entry Decisions cap only protects live memory and can never
    /// truncate what the tape already kept.</summary>
    public string LastDecisionMsg { get; internal set; } = "";
    private readonly object _decLock = new();
    public const int MaxDecisions = 100;
    public List<CarDecision> Decisions { get; } = new();

    /// <summary>The substep this car was last updated on. Set by SimEngine at
    /// every substep. The tape and the decision index need it: `simTime` is a
    /// double derived from the step count, and round(t / DtFixed) re-introduces
    /// exactly the float rounding that clock comment warns about.</summary>
    public long SimStep { get; internal set; }
    /// <summary>The substep of the MOST RECENT decision (0 = none yet) - what
    /// lets the tape mark the substeps worth scrubbing to.</summary>
    public long LastDecisionStep { get; internal set; }

    public void RecordDecision(double simTime, string msg)
    {
        lock (_decLock)
        {
            Decisions.Add(new CarDecision { T = simTime, Step = SimStep, Msg = msg });
            LastDecisionStep = SimStep;
            LastDecisionMsg = msg;
            if (Decisions.Count > MaxDecisions) Decisions.RemoveAt(0);
        }
    }

    /// <summary>Copy for API consumers - the sim thread mutates the list at
    /// 60 Hz while HTTP threads read it.</summary>
    public List<CarDecision> DecisionsSnapshot()
    {
        lock (_decLock) return new List<CarDecision>(Decisions);
    }

    /// <summary>Key of the avoidance cap active on the PREVIOUS substep
    /// ("avoid#12" = car 12 in my path - following, head-on or predicted
    /// crossing; "sign#C#12" = yield sign, car 12 at the crossing);
    /// null = free. Maintained by CarCollisions.ComputeAvoidCaps, which
    /// logs a decision only when this key TRANSITIONS.</summary>
    public string? ActiveCapKey { get; internal set; }

    /// <summary>Node id of the last crossing this car committed to (nose
    /// past the yield stop line) - logs one decision entry per crossing,
    /// not per tick. Set by CarCollisions.ComputeAvoidCaps.</summary>
    public string? CrossingCommittedNode { get; internal set; }

    /// <summary>True while this car is in body contact with another car
    /// (car-to-car Resolve). Lets Resolve log the crash + flash the hazard
    /// lights ONCE per contact episode instead of every 60 Hz substep while
    /// a pair stays pinned in contact.</summary>
    public bool InContact { get; internal set; }
    public int ContactWith { get; internal set; }
    /// <summary>How far (m) the driver model currently asks the nav to sit
    /// RIGHT of the nominal lane line - the R8 lateral avoidance of a
    /// frontal conflict (user rule: yield to the right as far as the road
    /// allows, never leaving it). Clamped by the nav to the kerb line of the
    /// narrowest segment on the route; decays back to the lane line when no
    /// frontal threat is seen.</summary>
    public double YieldRightM { get; internal set; }
    /// <summary>How far (m) the driver model asks the nav to sit LEFT of the
    /// nominal lane line - the R32 overtake of an obstruction (a parked or
    /// much slower car) in my own lane. Positive = borrow the other lane;
    /// the nav clamps it to the far kerb, so the car never leaves the road,
    /// and it decays back to the lane line once the pass is done (docs
    /// §8 R32). Distinct from YieldRightM, which is the R8 yield AGAINST a
    /// frontal conflict - that one only ever moves right.</summary>
    public double OvertakeShiftM { get; internal set; }
    /// <summary>The obstruction this car is COMMITTED to pass (R32): which
    /// car, on which segment, at what progress along it, and how many metres
    /// of the other lane the pass borrows.
    /// The lateral REQUEST alone is not a commitment. R32 asks only while the
    /// obstruction is still "in my line", so the moment the car has shifted
    /// far enough the obstruction leaves the line, wantM drops to 0 and the
    /// shift decays back - measured on park_block_overtake, where the car
    /// crossed the centreline at t=1.07 s with the shift already near zero,
    /// so R33 called the crossing unexplained and v1 still named the parked
    /// car as my lead. This says WHICH car I am passing and stays alive while
    /// that one is still ahead of me on the same segment - no timer, no magic
    /// distance. PassUid = -1 means no commitment.</summary>
    public int PassUid { get; internal set; } = -1;
    public int PassSegIdx { get; internal set; } = -1;
    public double PassProgress { get; internal set; } = -1.0;   // 0..1 along PassSegIdx
    public double PassShiftM { get; internal set; } = 0.0;      // metres borrowed
    public double PassLengthM { get; internal set; } = 0.0;     // the obstruction's own length
    /// <summary>Where the obstruction I am passing STANDS (metres, world).
    /// The segment+progress pair broke at a segment change: park_block_priority
    /// now parks its car exactly on the node straight_m, so the pass straddles
    /// the hand-off and the moment the car reached segment 1 the record died
    /// (SegIdx != PassSegIdx) - the borrow was still running and the guard called
    /// it wrong-side. A world point has no such seam.</summary>
    public double PassX { get; internal set; }
    public double PassY { get; internal set; }
    /// <summary>Monotonic count of CRASH EPISODES this car has been part of
    /// (one per contact episode, the same event that logs the "[R2] CRASH"
    /// decision). InContact is transient - it is only set while the pair is
    /// pinned, and a 0.1 s poll can miss the whole episode (measured: a
    /// 50-car run logged contacts at t=6.9/7.8/20.7 s while every poll read
    /// in_contact=false and the suite called the first crash at 84.8 s).
    /// This counter never resets, so a test can detect the FIRST crash
    /// reliably instead of the first crash the polling happens to see.</summary>
    public int CrashEpisodes { get; internal set; }

    // --- Staged (human-like) PRIORITY response --------------------------
    // A priority-road car that detects a predicted crossing conflict does NOT
    // brake immediately: it becomes alert and watches AlertOtherUid. It only
    // brakes if that car fails to yield (the TTC stops improving) for the
    // grace period, or if it is already too late to stop (self-protection).
    // Holding a stable state instead of flickering is what stops the
    // tick-to-tick brake/resume flutter that gridlocked the signed crossing.
    /// <summary>Uid of the car this car is watching (0 = not alert).</summary>
    public int AlertOtherUid { get; internal set; }
    public double AlertStartT { get; internal set; }
    public double AlertLastTtc { get; internal set; }
    /// <summary>Sim time the watched conflict was last SEEN; the alert only
    /// clears once it has been absent for AlertClearHoldS (see CarCollisions).</summary>
    public double AlertLastSeenT { get; internal set; }
    internal void ClearAlert()
    {
        AlertOtherUid = 0;
        AlertStartT = 0.0;
        AlertLastTtc = 0.0;
        AlertLastSeenT = 0.0;
    }

    // Debug trail (breadcrumbs)
    public List<(double X, double Y, double Heading)> Trail { get; } = new();
    private double _trailTimer;
    public bool TrailEnabled { get; set; }

    public Car(double x, double y, double heading, int segIdx, Driver? driver = null)
    {
        _nextUid++;
        Uid = _nextUid;
        X = x;
        Y = y;
        Heading = heading;
        Speed = 0.0;
        TargetSpeed = 0.0;
        SegIdx = segIdx;
        Driver = driver;
    }

    // --- Main Update ---

    public void Update(double dt, RoadNetwork network, ControlInput control)
    {
        // Update visual state
        IsBraking = control.Brake;
        IsAccelerating = control.Accelerate;

        // Choose update mode based on driver type
        string name = Driver?.GetName() ?? "FREE";
        if (name == "BICYCLE")
        {
            BicycleNav ??= new BicycleNav(this, network);
            BicycleNav.Update(dt, control);
        }
        else
        {
            UpdateFreeMode(dt, control, network);
        }

        // Update trail
        if (TrailEnabled)
        {
            _trailTimer += dt;
            if (_trailTimer >= 0.1)
            {
                _trailTimer = 0.0;
                Trail.Add((X, Y, Heading));
                if (Trail.Count > 500) Trail.RemoveAt(0);
            }
        }
    }

    // --- FREE Mode Physics ---

    /// <summary>Manual steering mode with a real-car gear model.
    /// Speed is signed: positive = forward, negative = reverse. A real car
    /// must brake through zero before it can back up - you cannot jump from
    /// +v straight to -v:
    ///   moving forward  : S brakes to a stop, W accelerates
    ///   moving backward : W brakes to a stop, S accelerates (reverse)
    ///   at a standstill : a FRESH press of S engages reverse, a fresh
    ///                     press of W drives forward. Holding the brake key
    ///                     through zero never shifts gears.</summary>
    private void UpdateFreeMode(double dt, ControlInput control, RoadNetwork? network = null)
    {
        bool accel = control.Accelerate;       // W held
        bool brake = control.Brake;            // S held
        bool steerLeft = control.SteerLeft;
        bool steerRight = control.SteerRight;

        // Speed control (signed, explicit gear state like a real car)
        if (Speed > 0)                         // moving forward
        {
            if (brake) Speed -= Config.CAR_BRAKING * dt;
            else if (accel) Speed += AccelMps2 * dt;
            if (Speed < 0) Speed = 0.0;        // braking ends exactly at zero
        }
        else if (Speed < 0)                    // moving backward
        {
            if (accel) Speed += Config.CAR_BRAKING * dt;   // W is the brake in reverse
            else if (brake) Speed -= AccelMps2 * dt; // S is the throttle in reverse
            if (Speed > 0) Speed = 0.0;        // ...and ends exactly at zero
        }
        else                                   // standstill: shifting + throttle
        {
            if (control.BrakePressed) _gear = "rev";
            else if (control.AcceleratePressed) _gear = "fwd";
            if (_gear == "rev" && brake) Speed -= AccelMps2 * dt;
            else if (_gear == "fwd" && accel) Speed += AccelMps2 * dt;
        }

        _freeTime += dt;
        // R25 (StVO §3(3)) speed limit: hard post-filter clamping the car to
        // its configured top speed every step. Log once when the cap engages.
        // The driver's own cruise choice (CruiseCapMps) sits INSIDE the class
        // ceiling and is reported as such, so a slow vehicle says why it is
        // slow instead of looking like a capped sports car.
        bool capBites = Speed > EffectiveTopMps;
        if (capBites && !_speedCapEngaged)
            RecordDecision(_freeTime,
                $"[R25] speed capped at limit {EffectiveTopMps:F1} m/s"
                + (CruiseCapMps > 0.0 ? " (cruise choice)" : ""));
        _speedCapEngaged = capBites;
        Speed = Math.Max(-Config.REVERSE_MAX_SPEED_M, Math.Min(EffectiveTopMps, Speed));
        // Deadband so the car doesn't oscillate around 0 when neither gear
        // input is held.
        if (!accel && !brake && Math.Abs(Speed) < 0.05) Speed = 0.0;
        TargetSpeed = Speed;

        // Brake lights: S while moving forward, W while reversing.
        IsBraking = (Speed > 0 && brake) || (Speed < 0 && accel);

        // Steering (only when moving). The wheel position ramps toward the
        // demanded direction at a finite rate and eases back to center on
        // release. In reverse the yaw goes the OTHER way for the same
        // steering input (bicycle model: omega = v*tan(d)/L with signed v).
        double steerTarget = 0.0;
        if (steerLeft) steerTarget -= 1.0;
        if (steerRight) steerTarget += 1.0;
        double wheelRate = dt / Config.STEER_LOCK_TIME_S;
        if (_steerPos < steerTarget) _steerPos = Math.Min(steerTarget, _steerPos + wheelRate);
        else if (_steerPos > steerTarget) _steerPos = Math.Max(steerTarget, _steerPos - wheelRate);

        if (Math.Abs(Speed) > 0 && Math.Abs(_steerPos) > 1e-6)
        {
            double speedAbs = Math.Abs(Speed);
            double turnFactor = Math.Max(0.3, 1.0 - speedAbs / TopSpeedMps * 0.7);
            double turnRate = Config.CAR_TURN_SPEED * turnFactor * dt;
            // A real car's yaw rate at FULL lock is v / R_min (the bicycle
            // model): the fixed arcade rate above would imply a turning
            // radius below the mechanical minimum at low speed - physically
            // impossible, and exactly what the validator rejects. Cap the
            // per-frame heading change with the same limit BICYCLE mode has.
            double maxRate = Math.Degrees(speedAbs / Config.MIN_TURN_RADIUS_M) * dt;
            // Partial lock scales the yaw proportionally (the cap above is
            // the full-lock value).
            turnRate = Math.Min(turnRate, maxRate) * Math.Abs(_steerPos);
            double sign = Speed >= 0 ? 1.0 : -1.0;
            Heading = Math.PosDeg(Heading + _steerPos * sign * turnRate);
        }

        // Movement (signed speed handles reverse)
        double rad = Math.Radians(Heading);
        X += Math.Sin(rad) * Speed * dt;
        Y += Math.Cos(rad) * Speed * dt;

        // Keep seg_idx/progress/forward current: the lane guard (wrong-side
        // check) and API state read them, but FREE mode never updates them
        // on its own. Cheap per frame (one projection); the full nearest-
        // segment search only runs when the car has left the current segment.
        if (network is not null) RefreshSegmentIfLeft(network);
    }

    private void RefreshSegmentIfLeft(RoadNetwork network)
    {
        var seg = network.Segments[SegIdx];
        double dx = seg.X2 - seg.X1, dy = seg.Y2 - seg.Y1;
        double lengthSq = dx * dx + dy * dy;
        if (lengthSq < 1e-9) { SnapToRoad(network); return; }
        double t = ((X - seg.X1) * dx + (Y - seg.Y1) * dy) / lengthSq;
        double projX = seg.X1 + t * dx, projY = seg.Y1 + t * dy;
        double lat = Math.Hypot(X - projX, Y - projY);
        double halfWidth = seg.Width / 2.0 + 1.0;
        if (t < -0.15 || t > 1.15 || lat > halfWidth) SnapToRoad(network);
    }

    // --- Utility ---

    /// <summary>Snap car to nearest road segment.</summary>
    public void SnapToRoad(RoadNetwork network)
    {
        double bestSeg = 0, bestDist = double.PositiveInfinity, bestT = 0.5;
        for (int idx = 0; idx < network.Segments.Count; idx++)
        {
            var seg = network.Segments[idx];
            double dx = seg.X2 - seg.X1, dy = seg.Y2 - seg.Y1;
            double lengthSq = dx * dx + dy * dy;
            if (lengthSq == 0) continue;
            double t = Math.Max(0, Math.Min(1, ((X - seg.X1) * dx + (Y - seg.Y1) * dy) / lengthSq));
            double projX = seg.X1 + t * dx, projY = seg.Y1 + t * dy;
            double dist = Math.Hypot(X - projX, Y - projY);
            if (dist < bestDist) { bestDist = dist; bestSeg = idx; bestT = t; }
        }
        SegIdx = (int)bestSeg;
        Progress = bestT;
        var snapped = network.Segments[SegIdx];
        double segHeading = Math.Degrees(Math.Atan2(snapped.X2 - snapped.X1, snapped.Y2 - snapped.Y1));
        Forward = Math.Abs(Math.WrapDeg(Heading - segHeading)) < 90;
    }

    /// <summary>Teleport to random road location. Caller should notify
    /// PhysicsValidator.SkipNextFrames() if using validation.</summary>
    public void TeleportRandom(RoadNetwork network)
    {
        var (rx, ry, rh, segIdx, _) = network.RandomRoadPoint();
        X = rx; Y = ry; Heading = rh;
        SegIdx = segIdx;
        Progress = 0.5;
        Speed = 0; TargetSpeed = 0;
        var seg = network.Segments[segIdx];
        double segHeading = Math.Degrees(Math.Atan2(seg.X2 - seg.X1, seg.Y2 - seg.Y1));
        Forward = Math.Abs(Math.WrapDeg(Heading - segHeading)) < 90;
        BicycleNav?.Reset();
        Trail.Clear();
    }

    /// <summary>Teleport to a deterministic named start point (synthetic
    /// test maps only). Caller should notify PhysicsValidator.ResetCarState()
    /// if using validation.</summary>
    public void TeleportToNamedPoint(RoadNetwork network, string name)
    {
        var (x, y, heading, segIdx, forward, _) = network.GetStartPoint(name);
        X = x; Y = y; Heading = heading;
        SegIdx = segIdx;
        Progress = forward ? 0.0 : 1.0;
        Forward = forward;
        Speed = 0; TargetSpeed = 0;
        BicycleNav?.Reset();
        Trail.Clear();
    }

    /// <summary>Geometric centre of the car body, in world metres.
    /// X/Y are the REAR AXLE (the bicycle model's pivot), so the body sits
    /// Config.REAR_AXLE_OFFSET_M ahead of them along the heading. Anything
    /// that draws or measures the car's BODY (sprite, four-corner on-road
    /// box, blinkers) must use this, not (X, Y).</summary>
    public (double X, double Y) BodyCenter()
    {
        double rad = Math.Radians(Heading);
        double off = Config.REAR_AXLE_OFFSET_M;
        return (X + Math.Sin(rad) * off, Y + Math.Cos(rad) * off);
    }

    // Interpolated render position (x, y, heading), set by the main loop
    // every frame. Physics runs in fixed 1/60 s substeps; a rendered frame
    // can contain 0 or 2 of them, which without interpolation makes the car
    // freeze for a frame and then jump - visible as a periodic 2-3 px hop.
    // null = draw the live state (frozen / before first step).
    public (double X, double Y, double Heading)? RenderState { get; set; }

    /// <summary>BodyCenter() at the interpolated render position. Only for
    /// DRAWING. Physics and validation keep using the exact state.</summary>
    public (double X, double Y) RenderBodyCenter()
    {
        if (RenderState is null) return BodyCenter();
        var (x, y, h) = RenderState.Value;
        double rad = Math.Radians(h);
        double off = Config.REAR_AXLE_OFFSET_M;
        return (x + Math.Sin(rad) * off, y + Math.Cos(rad) * off);
    }

    /// <summary>Heading at the interpolated render position (drawing only).</summary>
    public double RenderHeading() => RenderState?.Heading ?? Heading;

    /// <summary>Check if the car (all four corners, not just its center) is
    /// on any road. Delegates to RoadNetwork.IsCarOnRoad(), which tests the
    /// car's four bounding-box corners against the exact same paved-area
    /// polygon that gets rendered.</summary>
    public bool IsOnRoad(RoadNetwork network)
    {
        var (bx, by) = BodyCenter();
        return network.IsCarOnRoad(bx, by, Heading);
    }
}
