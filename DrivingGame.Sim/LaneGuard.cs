// Lane Guard — detects wrong-side (oncoming lane) driving.
// 1:1 port of car/src/lane_guard.py.
// Softer than PhysicsValidator: warns and tracks stats, never crashes.

namespace DrivingGame.Sim;

/// <summary>Checks whether a car crosses into the opposing lane.
///
/// On every two-way road segment the guard measures the distance from the
/// car's center to the segment's centerline. If that distance drops below
/// half the car's width, part of the car is on the wrong side of the
/// centerline.
///
/// Unlike PhysicsValidator this NEVER raises — it only logs warnings and
/// accumulates statistics for test reports.</summary>
public sealed class LaneGuard
{
    private const double Epsilon = 0.02;   // no false positives at the exact boundary

    public bool Enabled { get; private set; } = true;

    // Per-car state: uid -> last lateral offset (m)
    private readonly Dictionary<int, double> _lastOffset = new();

    // Statistics - PER CAR (uid -> value): each new car starts at zero, which
    // is what parallel test runs (one car per test in the same world) need.
    // Stale entries for destroyed cars are harmless.
    private readonly Dictionary<int, int> _violationsByCar = new();       // frames on wrong side
    private readonly Dictionary<int, double> _violationTimeByCar = new(); // seconds on wrong side
    // Centreline CROSSINGS per car (the quality metric the results table
    // wants): how often the car moved from one side of the centreline to the
    // other. A legal pass of a parked car crosses it once out and once back;
    // a car that never leaves its lane crosses it zero times.
    private readonly Dictionary<int, int> _crossingsByCar = new();
    // Which side of the centreline the car was on last (sign of the SIGNED
    // right offset; 0 = undecided, inside the dead zone). Counting a crossing
    // needs a SIDE, and the plain distance the guard uses is unsigned - it
    // cannot see a crossing at all.
    private readonly Dictionary<int, int> _lastSide = new();
    // A side only counts once the car is clearly off the line: at 0.3 m the
    // wheels straddle it, and jitter around the line must not count twice.
    private const double CrossSideMinM = 0.3;

    // --- R33: unexplained crossings (the error side of the invariant) ----
    // A crossing WITH a named intent is a quality metric (a legal pass).
    // A crossing WITHOUT one is an error - the same class as R29/R31: the
    // corridor was supposed to make it impossible, so it firing means the
    // construction that guarantees legality has been broken by whatever feeds
    // it. Counted per car, kept as a bounded forensic list (unlike
    // SimEngine.StandstillErrors, which grows without bound).
    private const int MaxErrorReports = 100;
    private readonly Dictionary<int, int> _unexplainedByCar = new();
    private readonly List<string> _errors = new();
    private int _unexplainedTotal;
    private string _lastError = "";

    // The centreline the guard measures against is the road the car is ON -
    // not the nearest centreline anywhere on the map. Car.SegIdx says which
    // road that is, and SmoothedNetwork.SegmentCurve[(segIdx, forward)] says
    // which rounded curve that road has, so the query is local: a short
    // window of samples around the car's own arc position. (The old whole-map
    // MultiLineString scan cost 405 us/car/step - 57 % of the sim step - and
    // could answer with the OTHER road's line at a crossing or under a bridge
    // deck; see docs/reviews/2026-10-20-component-review.md §4.)
    public LaneGeometry Lanes(RoadNetwork network) =>
        network.LaneGeometryCache ??= new LaneGeometry(network);

    public LaneGuard(bool enabled = true) => Enabled = enabled;

    public void Enable() { Enabled = true; Console.WriteLine("✅ Lane guard ENABLED"); }
    public void Disable() { Enabled = false; Console.WriteLine("❌ Lane guard DISABLED"); }

    /// <summary>R33 (docs §8) - the lateral intents that legitimately put the
    /// car on or over the centreline. THE INVARIANT, not a carve-out list:
    /// a crossing is legal only if the system already NAMED a reason for it,
    /// and every reason here is state the driver model owns - a U-turn, a turn
    /// swing, an R32 pass, a deliberate merge, a pull-out. Nothing here is
    /// inferred from geometry, and no new manoeuvre needs a new exception in
    /// the guard: it declares its intent where the intent already lives.
    ///
    /// Returns the reason, or null when the car is where it is with no stated
    /// reason - which is the error. Reverse-in parking is deliberately NOT
    /// listed: docs §1 says a park may not use the oncoming lane at all
    /// (PARK_CENTRELINE_MARGIN_M is a hard bound), so a park swing is no
    /// excuse for being left of the line.</summary>
    private static string? LateralIntent(Car car, RoadNetwork network)
    {
        var nav = car.BicycleNav;
        if (nav is null) return null;          // FREE mode: no plan to consult
        if (nav.UturnActive) return "uturn (§5: crossing is intended)";
        if (nav.InTurnBlendZone(nav.S)) return "turn blend";
        if (CarCollisions.PassPending(car, network))
            return "R32 pass (borrowing the other lane, car " + car.PassUid + ")";
        if (nav.LaneChangeSignal is not null) return "R17 merge (signalled lane change)";
        if (nav.PullOutFrames > 0) return "pull-out from the kerb";
        return null;
    }

    /// <summary>Return True if the car is currently ON THE WRONG SIDE without
    /// a stated reason - the verdict the /state payload and the e2e suite read.
    /// A car that is left of the centreline BECAUSE it named a reason is not
    /// wrong-side; it is doing the manoeuvre it said it was doing.</summary>
    public bool Check(Car car, double dt, RoadNetwork network, double simTime = 0.0)
    {
        if (!Enabled) return false;

        var seg = network.Segments[car.SegIdx];

        // One-way streets have no opposing lane - skip silently.
        if (seg.Oneway) return false;

        // One measurement, used twice: the distance test needs the magnitude,
        // the crossing counter needs the side. (The old code asked the geometry
        // question TWICE per car per substep.)
        var (rightM, known) = CentrelineOffset(car, network);
        if (!known) return false;   // this road has no centreline: undecided,
                                    // NOT "on the line" - do not call it a violation
        double offsetM = Math.Abs(rightM);

        // R33: the reason, asked ONCE and consulted everywhere. The old code
        // suppressed the check in three different places with three different
        // mechanisms (SimEngine skipped turns and U-turns before calling the
        // guard, the guard itself skipped R32 passes) - one list, one owner.
        var intent = LateralIntent(car, network);

        // --- centreline crossings: explained or an error ------------------
        // A side only counts once the car is clearly off the line: at 0.3 m
        // the wheels straddle it, and jitter around the line must not count
        // twice.
        if (Math.Abs(rightM) >= CrossSideMinM)
        {
            int side = rightM > 0.0 ? 1 : -1;      // +1 = right of centreline
            var was = _lastSide.TryGetValue(car.Uid, out var prev) ? prev : 0;
            if (was != 0 && side != was)
            {
                if (intent is null)
                {
                    // THE INVARIANT BROKE: the car crossed the centreline with
                    // no manoeuvre asking for it. That is not a quality metric
                    // like a legal pass - it is an error, the same class as
                    // R29/R31, and the e2e suite must fail on it.
                    _unexplainedByCar[car.Uid] = _unexplainedByCar.GetValueOrDefault(car.Uid) + 1;
                    _unexplainedTotal++;
                    _lastError = $"[R33] unexplained centreline crossing: car {car.Uid} " +
                                 $"crossed to side {side} on segment {car.SegIdx} " +
                                 $"({offsetM:F2} m off the line, no intent) at t={simTime:F2} s";
                    if (_errors.Count < MaxErrorReports) _errors.Add(_lastError);
                    car.RecordDecision(simTime, _lastError);
                    Console.WriteLine($"!! {_lastError}");
                }
                else
                {
                    // A legal pass of the centreline - the quality metric the
                    // results table wants (a pass out and back = 2).
                    _crossingsByCar[car.Uid] = _crossingsByCar.GetValueOrDefault(car.Uid) + 1;
                }
            }
            _lastSide[car.Uid] = side;
        }

        // Wrong-side threshold, relative to the road width:
        //   7 m and wider: 0.9 m (half car width) - the left edge crossing
        //       the centreline is already wrong-side driving.
        //   narrow two-way roads (3.5-4 m service lanes): the lane is
        //       NARROWER than the car, so correct driving sits at only
        //       ~0.5-0.75 m from the centreline and the fixed 0.9 m test
        //       fired constantly (crashing BICYCLE runs on OSM maps).
        //       There, wrong-side means the car's CENTRE has crossed into
        //       the opposing half: threshold 0.15 m (noise margin).
        // Same number as Config.KerbOffsetM(seg.Width) - 0.9, stated from the
        // segment's declared lane strip so the road width is read once per
        // network instead of recomputed per car per substep.
        var strip = Lanes(network).Of(car.SegIdx);
        double kerbOffsetM = Math.Max(0.0,
            strip.HalfWidthM - Config.CAR_WIDTH_M / 2.0 - Config.KERB_CLEARANCE_M);
        double threshold = Math.Max(0.15, Math.Min(0.9, kerbOffsetM - 0.9));

        bool encroaching = offsetM < (threshold - Epsilon);
        bool isWrong = encroaching && intent is null;

        bool wasWrong = _lastOffset.TryGetValue(car.Uid, out var last) &&
                        last < (threshold - Epsilon);

        if (isWrong)
        {
            _violationsByCar[car.Uid] = _violationsByCar.GetValueOrDefault(car.Uid) + 1;
            _violationTimeByCar[car.Uid] = _violationTimeByCar.GetValueOrDefault(car.Uid) + dt;
            // Only print once per encroachment event (not every frame).
            if (!wasWrong)
                Console.WriteLine(
                    $"\n⚠️  WRONG-SIDE DRIVING! distance to centerline " +
                    $"{offsetM:F2} m (threshold = {threshold:F2} m, " +
                    $"road width {seg.Width} m) on segment {car.SegIdx}\n");
        }

        _lastOffset[car.Uid] = offsetM;
        return isWrong;
    }

    /// <summary>Signed lateral offset (m, positive = RIGHT of the car's own
    /// travel direction) from the centreline of the segment the car is on,
    /// and whether it is known at all. <c>Known = false</c> means this road
    /// has no centreline to measure against (the smoothed network had no
    /// curve for this segment) - that is NOT "on the line", and the caller
    /// must not read it as a violation.
    ///
    /// Local by construction: the curve is looked up by (SegIdx, Forward), so
    /// the sign follows the road the car is actually on - a chord flips inside
    /// a bend, and a whole-map nearest-point search can answer with the OTHER
    /// road's line at a crossing or under a bridge deck.
    ///
    /// The search is a window around the car's own arc position (Progress
    /// along the segment mapped onto the segment's span of the rounded curve),
    /// widened once only when the car really is far off the line. Cost is
    /// bounded by the SEGMENT's length, never by the map's.</summary>
    private static (double OffsetM, bool Known) CentrelineOffset(Car car,
                                                                RoadNetwork network)
    {
        var sm = SmoothGeometry.For(network);
        var cref = sm.SegmentCurve.GetValueOrDefault((car.SegIdx, car.Forward));
        if (cref is null) return (0.0, false);
        var curve = cref.Curve;
        // S is the curve's arc-length table (0.5 m samples); the segment owns
        // the sub-range [S0, S1] of it.
        var S = curve.S;
        if (S.Count < 2 || curve.Total <= 0) return (0.0, false);

        int iLo = IndexOfArc(S, cref.S0);
        int iHi = IndexOfArc(S, cref.S1);
        if (iHi < iLo) { var t = iLo; iLo = iHi; iHi = t; }
        // The scan works over sample PAIRS, so the last usable pair starts
        // one before the end of the range.
        iHi = Math.Min(iHi, S.Count - 2);
        if (iHi < iLo) return (0.0, false);

        var (cx, cy) = car.BodyCenter();

        double bestD2 = double.PositiveInfinity;
        double best = 0.0;
        void Scan(int from, int to)
        {
            int lo = Math.Max(iLo, from), hi = Math.Min(iHi, to);
            for (int i = lo; i <= hi; i++)
            {
                double ax = curve.Xs[i], ay = curve.Ys[i];
                double bx = curve.Xs[i + 1], by = curve.Ys[i + 1];
                double dx = bx - ax, dy = by - ay;
                double lenSq = dx * dx + dy * dy;
                if (lenSq <= 1e-12) continue;
                double t = Math.Clamp(((cx - ax) * dx + (cy - ay) * dy) / lenSq, 0.0, 1.0);
                double px = ax + t * dx, py = ay + t * dy;
                double d2 = (cx - px) * (cx - px) + (cy - py) * (cy - py);
                if (d2 >= bestD2) continue;
                bestD2 = d2;
                // Right-hand normal of the LOCAL tangent: forward = (sin h,
                // cos h) in this frame, so right = (cos h, -sin h) - the same
                // convention the body boxes use. The curve for (SegIdx,
                // Forward) already runs in the car's direction, so no extra
                // flip is needed here.
                double len = Math.Sqrt(lenSq);
                double tx = dx / len, ty = dy / len;
                best = (cx - px) * ty + (cy - py) * (-tx);
            }
        }

        double frac = Math.Clamp(car.Progress, 0.0, 1.0);
        double sGuess = cref.S0 + (cref.S1 - cref.S0) * frac;
        int mid = IndexOfArc(S, sGuess);

        const double WindowM = 16.0;
        const double StepM = 0.5;              // SmoothCurve's sample spacing
        int w = (int)Math.Ceiling(WindowM / StepM);
        Scan(mid - w, mid + w);
        // A car that far off the line (off-road, or a corner swing the window
        // missed) gets the whole segment - still bounded by the segment's own
        // length, never by the map's.
        if (bestD2 == double.PositiveInfinity || Math.Sqrt(bestD2) > 3.0)
            Scan(iLo, iHi);
        if (bestD2 == double.PositiveInfinity) return (0.0, false);
        return (best, true);
    }

    /// <summary>Index of the first arc-length entry >= <c>s</c> in the
    /// monotone sample table.</summary>
    private static int IndexOfArc(List<double> S, double s)
    {
        int lo = 0, hi = S.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (S[mid] < s) lo = mid + 1;
            else hi = mid;
        }
        return Math.Max(0, Math.Min(S.Count - 1, lo));
    }

    /// <summary>Clear stored state after a teleport.</summary>
    public void Reset(Car car)
    {
        _lastOffset.Remove(car.Uid);
        _lastSide.Remove(car.Uid);
        _crossingsByCar.Remove(car.Uid);
        _unexplainedByCar.Remove(car.Uid);
    }

    /// <summary>The most recent unexplained crossing, "" if none. Forensics
    /// for the /state payload and the HUD - the per-car count says how often,
    /// this says when and where.</summary>
    public string LastUnexplained() => _lastError;

    /// <summary>The recorded unexplained-crossing messages (bounded - see
    /// <see cref="MaxErrorReports"/>; the count is total, this is the tail).</summary>
    public List<string> UnexplainedLog() => new List<string>(_errors);

    /// <summary>Unexplained centreline crossings (per car, or the lifetime
    /// total with no argument).</summary>
    public int UnexplainedCrossings(Car? car = null) =>
        car is null ? _unexplainedTotal
                    : _unexplainedByCar.GetValueOrDefault(car.Uid);

    /// <summary>A summary suitable for test reports. With a car: that car's
    /// counters (zero for a new/unknown car). Without: the whole-lifetime
    /// total over all cars (legacy behaviour).</summary>
    public LaneGuardStats Stats(Car? car = null)
    {
        int frames; double secs; int crossings; int unexplained;
        if (car is null)
        {
            frames = _violationsByCar.Values.Sum();
            secs = _violationTimeByCar.Values.Sum();
            crossings = _crossingsByCar.Values.Sum();
            unexplained = _unexplainedTotal;
        }
        else
        {
            frames = _violationsByCar.GetValueOrDefault(car.Uid);
            secs = _violationTimeByCar.GetValueOrDefault(car.Uid);
            crossings = _crossingsByCar.GetValueOrDefault(car.Uid);
            unexplained = _unexplainedByCar.GetValueOrDefault(car.Uid);
        }
        return new LaneGuardStats(frames, Math.Round(secs, 2), crossings, unexplained);
    }

    /// <summary>Wrong-side counters (per car or lifetime total), the number of
    /// centreline crossings that a named intent EXPLAINED (a quality metric -
    /// a legal pass out and back counts 2), and the number that had NO intent
    /// at all (an error: the corridor should have made it unreachable).</summary>
    public sealed record LaneGuardStats(int WrongSideFrames,
                                        double WrongSideSeconds,
                                        int CentrelineCrossings,
                                        int UnexplainedCrossings);
}
