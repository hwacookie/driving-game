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
    /// the uid by the main loop; "red" is the player default.</summary>
    public string Color { get; set; } = "red";

    // Position and orientation (world pixels; heading degrees, 0 = north)
    public double X { get; internal set; }
    public double Y { get; internal set; }
    public double Heading { get; internal set; }

    // Speed (m/s; signed in FREE mode / reverse maneuvers)
    public double Speed { get; internal set; }
    public double TargetSpeed { get; internal set; }

    // Engaged gear (FREE mode, like a real shifter): null = neutral,
    // 'fwd' or 'rev'. Set by a FRESH key press at a standstill.
    private string? _gear;
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
            else if (accel) Speed += Config.CAR_ACCELERATION * dt;
            if (Speed < 0) Speed = 0.0;        // braking ends exactly at zero
        }
        else if (Speed < 0)                    // moving backward
        {
            if (accel) Speed += Config.CAR_BRAKING * dt;   // W is the brake in reverse
            else if (brake) Speed -= Config.CAR_ACCELERATION * dt; // S is the throttle in reverse
            if (Speed > 0) Speed = 0.0;        // ...and ends exactly at zero
        }
        else                                   // standstill: shifting + throttle
        {
            if (control.BrakePressed) _gear = "rev";
            else if (control.AcceleratePressed) _gear = "fwd";
            if (_gear == "rev" && brake) Speed -= Config.CAR_ACCELERATION * dt;
            else if (_gear == "fwd" && accel) Speed += Config.CAR_ACCELERATION * dt;
        }

        Speed = Math.Max(-Config.REVERSE_MAX_SPEED_M, Math.Min(Config.CAR_SPEED, Speed));
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
            double turnFactor = Math.Max(0.3, 1.0 - speedAbs / Config.CAR_SPEED * 0.7);
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
        X += Math.Sin(rad) * Speed * dt * Config.PIXELS_PER_METER;
        Y += Math.Cos(rad) * Speed * dt * Config.PIXELS_PER_METER;

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
        double latPx = Math.Hypot(X - projX, Y - projY);
        double halfWidthPx = (seg.Width / 2.0 + 1.0) * Config.PIXELS_PER_METER;
        if (t < -0.15 || t > 1.15 || latPx > halfWidthPx) SnapToRoad(network);
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

    /// <summary>Geometric centre of the car body, in world pixels.
    /// X/Y are the REAR AXLE (the bicycle model's pivot), so the body sits
    /// Config.REAR_AXLE_OFFSET_M ahead of them along the heading. Anything
    /// that draws or measures the car's BODY (sprite, four-corner on-road
    /// box, blinkers) must use this, not (X, Y).</summary>
    public (double X, double Y) BodyCenter()
    {
        double rad = Math.Radians(Heading);
        double off = Config.REAR_AXLE_OFFSET_M * Config.PIXELS_PER_METER;
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
        double off = Config.REAR_AXLE_OFFSET_M * Config.PIXELS_PER_METER;
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
