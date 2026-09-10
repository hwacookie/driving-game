// Physics Validator - independent "physics judge" for detecting violations.
// 1:1 port of car/src/physics_validator.py.

namespace DrivingGame.Sim;

/// <summary>Raised when a car performs an impossible motion (position jump,
/// impossibly tight turning radius). The heading-snap and off-road checks
/// are NON-fatal: they print / record instead.</summary>
public sealed class PhysicsViolationException : Exception
{
    public PhysicsViolationException(string message) : base(message) { }
}

/// <summary>Independent physics constraint checker for cars.
///
/// Detects violations:
/// - Impossible position jumps (speed exceeds max)        → throws
/// - Instant heading changes (&gt;30° in one frame)         → prints
/// - Rotating with an impossibly tight implied turning radius → throws
/// - Off-road driving                                      → records
///
/// State is tracked by car id — when a car is destroyed and replaced, the
/// old entry simply goes stale. No skip/reset plumbing needed.
///
/// Violations are PER CAR (uid -> log): each new car starts with an empty
/// counter, which is what parallel test runs (one car per test in the same
/// world) need.</summary>
public sealed class PhysicsValidator
{
    public const double HeadingEpsilonDeg = 0.05;
    public const double MinRealisticRadiusM = 3.0;

    public bool Enabled { get; private set; } = true;

    // uid -> log of that car's violations
    private readonly Dictionary<int, List<Violation>> _violationsByCar = new();
    // uid -> (x, y, heading)
    private readonly Dictionary<int, (double X, double Y, double Heading)> _lastState = new();

    public PhysicsValidator(bool enabled = true) => Enabled = enabled;

    /// <summary>This car's violation log (empty for a new/unknown car).</summary>
    public IReadOnlyList<Violation> ViolationsFor(Car car) =>
        _violationsByCar.TryGetValue(car.Uid, out var log) ? log : Array.Empty<Violation>();

    /// <summary>Number of violations logged for this car.</summary>
    public int Count(Car car) => ViolationsFor(car).Count;

    public void Enable() { Enabled = true; Console.WriteLine("✅ Physics validator ENABLED"); }
    public void Disable() { Enabled = false; Console.WriteLine("❌ Physics validator DISABLED"); }

    /// <summary>Run all physics checks on a car. Called after car.Update().
    ///
    /// inContact: the car is pressed against an obstacle (stop-on-contact,
    /// docs/OBSTACLES.md). A contact stop is EXPECTED behavior, not a
    /// violation - but while resting against a solid object the motion is
    /// externally constrained (like being pushed by a truck), so the
    /// implied-turning-radius invariant is suspended for that frame. The
    /// jump / heading-snap / off-road checks keep running: the stop itself
    /// must stay physical (no teleport, no instant snap, no penetration).</summary>
    public void Check(Car car, double dt, RoadNetwork network,
                      double simTime = 0.0, bool inContact = false)
    {
        if (!Enabled) return;

        if (!_lastState.TryGetValue(car.Uid, out var old))
        {
            _lastState[car.Uid] = (car.X, car.Y, car.Heading);
            return;
        }

        CheckJump(car, old.X, old.Y, dt, simTime);
        CheckHeadingSnap(car, old.Heading, simTime);
        if (!inContact)
            CheckTurningRadius(car, old.X, old.Y, old.Heading, simTime);
        CheckOffRoad(car, network, simTime);

        _lastState[car.Uid] = (car.X, car.Y, car.Heading);
    }

    /// <summary>One recorded (non-fatal) violation.</summary>
    public sealed record Violation(string Type, int Car, double X, double Y,
                                   double Speed, int Segment);

    private void CheckJump(Car car, double oldX, double oldY, double dt, double simTime)
    {
        // Impossible position jump.
        double distanceM = Math.Hypot(car.X - oldX, car.Y - oldY) / Config.PIXELS_PER_METER;
        // abs(): speed is signed (negative = reverse gear); the bound is on
        // how far the car can travel per frame in EITHER direction.
        double maxAllowed = Math.Abs(car.Speed) * dt + 0.1 * dt + 0.01;

        if (distanceM > maxAllowed)
        {
            car.RecordDecision(simTime,
                $"[R1] impossible jump {distanceM:F1} m (max {maxAllowed:F1} m)");
            throw new PhysicsViolationException(
                $"\n{'=' * 70}\n" +
                $"⚠️  IMPOSSIBLE JUMP!\n" +
                $"{'=' * 70}\n" +
                $"Old: ({oldX:F1}, {oldY:F1}) → New: ({car.X:F1}, {car.Y:F1})\n" +
                $"Distance: {distanceM:F1}m (max: {maxAllowed:F1}m)\n" +
                $"Speed: {car.Speed:F1} m/s | Segment: {car.SegIdx}\n" +
                $"{'=' * 70}\n");
        }
    }

    private void CheckHeadingSnap(Car car, double oldHeading, double simTime)
    {
        // Instant heading change.
        double diff = Math.Abs(Math.WrapDeg(car.Heading - oldHeading));

        if (diff > 30)
        {
            car.RecordDecision(simTime,
                $"[R1] instant heading change {diff:F1} deg");
            Console.WriteLine(
                $"\n{'=' * 70}\n" +
                $"⚠️  INSTANT HEADING CHANGE!\n" +
                $"{'=' * 70}\n" +
                $"{oldHeading:F1}° → {car.Heading:F1}° (Δ{diff:F1}°)\n" +
                $"Speed: {car.Speed:F1} m/s | Segment: {car.SegIdx}\n" +
                $"{'=' * 70}\n");
        }
    }

    private void CheckOffRoad(Car car, RoadNetwork network, double simTime)
    {
        // Off-road driving.
        if (!car.IsOnRoad(network))
        {
            car.RecordDecision(simTime,
                $"[R1] off-road at ({car.X:F0}, {car.Y:F0})");
            Console.WriteLine(
                $"\n{'=' * 70}\n" +
                $"⚠️  OFF-ROAD!\n" +
                $"{'=' * 70}\n" +
                $"({car.X:F1}, {car.Y:F1}) | Speed: {car.Speed:F1} m/s\n" +
                $"Segment: {car.SegIdx}\n" +
                $"{'=' * 70}\n");
            _violationsByCar.TryGetValue(car.Uid, out var log);
            log ??= new List<Violation>();
            _violationsByCar[car.Uid] = log;
            log.Add(new Violation("off_road", car.Uid, car.X, car.Y, car.Speed, car.SegIdx));
        }
    }

    private void CheckTurningRadius(Car car, double oldX, double oldY, double oldHeading, double simTime)
    {
        // Heading change requires proportional movement.
        double diffDeg = Math.Abs(Math.WrapDeg(car.Heading - oldHeading));

        if (diffDeg <= HeadingEpsilonDeg) return;

        double distM = Math.Hypot(car.X - oldX, car.Y - oldY) / Config.PIXELS_PER_METER;
        double radiusM = distM / Math.Radians(diffDeg);

        if (radiusM < MinRealisticRadiusM)
        {
            car.RecordDecision(simTime,
                $"[R1] impossible turning radius {radiusM:F2} m (min {MinRealisticRadiusM} m)");
            throw new PhysicsViolationException(
                $"\n{'=' * 70}\n" +
                $"⚠️  IMPOSSIBLE TURNING RADIUS!\n" +
                $"{'=' * 70}\n" +
                $"Δheading: {diffDeg:F2}° | Δpos: {distM * 1000:F1}mm\n" +
                $"Implied radius: {radiusM:F3}m (min: {MinRealisticRadiusM}m)\n" +
                $"Segment: {car.SegIdx}\n" +
                $"{'=' * 70}\n");
        }
    }
}
