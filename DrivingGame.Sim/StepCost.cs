// Per-car-per-step cost accounting (the user's, 2026-10-20).
//
// The sim's performance budget is stated PER CAR PER SUBSTEP ("~200 us/car/
// substep total", see the note in SimEngine and docs/DRIVING_MANEUVERS.md §8),
// and the capacity ceiling is found by watching how many cars still hold real
// time. So a cost report has to speak that same unit: microseconds per car per
// 60 Hz substep, and - for people reading a log at a glance - the equivalent
// per car per SECOND (x60), which is the wall time one car spends in a stage
// while the sim runs in real time.
//
// The existing ReportCollisionCost covers two stages (avoid, resolve) in us/
// call, which is us per SUBSTEP for the whole fleet - the wrong unit as soon
// as the car count is not 1. This class covers every stage of the fixed-
// timestep loop and reports both units.
//
// OFF BY DEFAULT. Timing a stage costs two Stopwatch.GetTimestamp() calls
// (~20-30 ns each); at 50 cars x 9 stages x 60 substeps that is ~0.5 ms of
// wall per sim second of pure measurement - a few percent of the very number
// being measured, and enough to distort a capacity sweep. So the trace is
// gated by an env switch, read ONCE at class init (same idiom as
// RefLineMath.PARK_DEBUG): set DRIVING_GAME_COST=1 to get the table.
// When on, the printed figures are an UPPER bound that includes the
// measurement's own cost - see the self-cost line at the bottom.

namespace DrivingGame.Sim;

public static class StepCost
{
    /// <summary>Trace on/off, decided once at process start.</summary>
    public static readonly bool Enabled =
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DRIVING_GAME_COST"));

    // --- Stage ids (fixed slots: no string keys in the hot loop) ----------
    // PER_CAR stages get one Add() call per car per substep, so us/call IS
    // us/car/substep. FLEET stages get one Add() call per substep covering
    // the whole fleet, so the report divides by the car count.
    public const int Flow = 0;        // per car: fleet-flow + R31 standstill scan
    public const int Avoid = 1;       // fleet: CarCollisions.ComputeAvoidCaps
    public const int Upd = 2;         // per car: Car.Update (driver + BicycleNav)
    public const int Contact = 3;     // per car: Obstacles.ApplyContactStop
    public const int Caps = 4;        // per car: avoidance cap + brake light
    public const int Resolve = 5;     // fleet: CarCollisions.Resolve
    public const int Validate = 6;    // per car: PhysicsValidator.Check
    public const int Guard = 7;       // per car: LaneGuard.Check
    public const int Road = 8;        // per car: off-road check (FREE mode)
    public const int Payload = 9;     // per FRAME: /state payload build (see note)
    public const int Tape = 10;       // per substep: the pose tape (fleet stage)
    public const int Stages = 11;

    private static readonly string[] StageName =
        { "flow", "avoid", "update", "contact", "cap", "resolve",
          "validate", "guard", "road", "payload*", "tape" };

    private static readonly bool[] PerCar =
        { false, false, true, true, true, false, true, true, true, false, false };

    // Accumulators for one reporting window (reset on every Report). Plain
    // fields, no lock: the sim runs on ONE dedicated thread (SimHost.SimLoop,
    // Program.cs main loop) and only that thread touches these - the same
    // assumption the existing _avoidCapsTicks/_resolveTicks pair already makes.
    private static readonly long[] _ticks = new long[Stages];
    private static readonly long[] _calls = new long[Stages];

    /// <summary>Close one stage: pass the timestamp taken before the work.
    /// A per-car stage counts one unit (that car); a fleet stage counts the
    /// cars the call covered, so the report can divide them out.</summary>
    public static void Add(int stage, long t0, int carsCovered)
    {
        _ticks[stage] += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        _calls[stage]++;
        _carsSeen[stage] = carsCovered;
    }

    private static readonly int[] _carsSeen = new int[Stages];

    /// <summary>Print the window and reset it. Called on the same cadence as
    /// the existing collision-cost report (<c>CostReportEverySteps</c>
    /// substeps = 5 sim-seconds at 60 Hz).</summary>
    public static void Report(long substeps, double dtFixed)
    {
        double freq = System.Diagnostics.Stopwatch.Frequency;
        double perSecond = 1.0 / dtFixed;          // substeps per sim second
        // The fleet size of the window - the largest car count any fleet
        // stage was called with.
        int cars = 1;
        for (int s = 0; s < Stages; s++)
            if (PerCar[s] is false && _calls[s] > 0)
                cars = Math.Max(cars, _carsSeen[s]);

        double totalUsPerCarStep = 0.0;
        long totalTicks = 0;
        var lines = new List<string>(Stages);

        for (int s = 0; s < Stages; s++)
        {
            if (_calls[s] == 0) continue;
            double usTotal = _ticks[s] / freq * 1_000_000.0;
            // Per-car-per-substep: a per-car stage has one call per car per
            // substep, so us/call is already the per-car figure. A fleet
            // stage has one call per substep for the whole fleet.
            double usPerCarStep = PerCar[s]
                ? usTotal / _calls[s]
                : usTotal / _calls[s] / Math.Max(_carsSeen[s], 1);
            totalUsPerCarStep += usPerCarStep;
            totalTicks += _ticks[s];
            lines.Add($"{StageName[s]}: {usPerCarStep:F2} us/car/step " +
                      $"({usPerCarStep * perSecond:F0} us/car/s)");
        }

        Console.WriteLine("[step cost] per-car-per-substep (60 Hz), " +
                          $"{cars} cars over {substeps} substeps:");
        foreach (var l in lines)
            Console.WriteLine($"  {l}");
        Console.WriteLine($"  TOTAL: {totalUsPerCarStep:F2} us/car/step " +
                          $"({totalUsPerCarStep * perSecond:F0} us/car/s) " +
                          $"- budget ~200 us/car/step");
        // payload* is per FRAME, not per substep: at --warp N a frame runs N
        // substeps, so its per-substep share is lower by that factor. Kept in
        // the table because the payload build is a real per-car cost, but it
        // must not be read at the same rate as the substep stages.
        Console.WriteLine("  (* payload is per frame, not per substep)");

        // The measurement's own cost, so the reader knows how much of the
        // number above is the stopwatch: one Add() per stage per car per
        // substep, plus the timestamp pair at each call site.
        long marks = 0;
        for (int s = 0; s < Stages; s++) marks += _calls[s];
        double selfUsPerCarStep = marks * 2.0 * 0.030   // ~30 ns per GetTimestamp
                                  / Math.Max(cars, 1) / Math.Max(substeps, 1.0);
        Console.WriteLine($"  self-cost (stopwatch pair x {marks} marks): " +
                          $"{selfUsPerCarStep:F3} us/car/step");

        Reset();
    }

    private static void Reset()
    {
        for (int s = 0; s < Stages; s++)
        {
            _ticks[s] = 0;
            _calls[s] = 0;
            _carsSeen[s] = 0;
        }
    }
}
