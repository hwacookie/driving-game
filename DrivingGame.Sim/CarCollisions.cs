// CarCollisions.cs
// Car-to-car collisions + simple collision avoidance (2026-09-06).
//
// Two layers, both per physics substep in SimEngine.Tick:
//
// 1. AVOIDANCE ("Kollisionsvermeidung durch Abbremsen"): every car gets a
//    speed cap = the fastest speed from which it can still stop behind the
//    nearest car in its forward corridor (plus a standstill gap). SimEngine
//    applies it as decel-limited braking (CAR_BRAKING) AFTER the driver's
//    own longitudinal logic ran - so it works for every driver (BICYCLE and
//    FREE, incl. U-turn/reverse maneuvers) without touching their code.
//
// 2. RESPONSE: if two body boxes overlap after the step, both cars roll
//    back to their pre-step pose and stop. A substep cannot skip over a
//    4.4 m car, so the pre-step poses were clear - no interpenetration, no
//    teleport (the validator sees zero motion for them).
//
// Cars exert no collision forces on each other; they brake and rest against
// each other like against a wall (same semantics as Obstacles.ApplyContactStop).

using System;
using System.Collections.Generic;

namespace DrivingGame.Sim;

public static class CarCollisions
{
    // Broadphase grid cell size. 20 m keeps the look-ahead ring at 3 cells
    // and the collision neighborhood (3x3) cheap even with dense traffic.
    const double CellSize = 20.0;

    // Forward corridor for avoidance: a car counts as "ahead of me" while
    // its centre is laterally within CorridorHalfWidth of my axis (a car in
    // the adjacent lane sits ~3.5 m away on a 7 m road and must NOT
    // trigger) and up to LookAheadForSpeed(my speed) ahead - see below.
    const double CorridorHalfWidthM = 1.8;
    // SPEED-DEPENDENT look-ahead (the fast-follower rear-end fix). A
    // follower at speed v is only safe if it first sees a stopped leader at
    // or before G = v²/(2a) + StandstillGap - exactly where the stopping-
    // curve cap drops to v. A FIXED range shorter than the stopping distance
    // let a fast follower enter the reaction zone already OVER the cap, an
    // unrecoverable rear-end (the 110 km/h / 43 m crash on fig8). So the
    // range is reaction distance (v·t_react) + full braking distance
    // (v²/2a) + standstill gap, floored at LookAheadMinM so slow cars keep
    // the old short range.
    const double LookAheadMinM = 40.0;
    const double ReactionTimeS = 1.0;
    // Broadphase ring radius: must cover the LARGEST per-car look-ahead
    // (stopping distance at top speed + reaction + gap ≈ 214 m).
    const double LookAheadMaxM = 250.0;

    // Comfortable standstill gap: the stopping-distance curve is measured
    // from this gap, so a following car rests ~3 m short of the lead car.
    const double StandstillGapM = 3.0;

    // v2 - oblique/crossing pair prediction (right-before-left): a car not
    // yet in anyone's forward corridor can still be on a collision course at
    // an angle. Predict both cars' positions ahead and test their body
    // boxes; graded response by how soon the earliest overlap occurs.
    // Swept finely (not 3 fixed samples): at cruise speed the "predicted
    // position at exactly 1/3/5 s" sample overshoots the actual crossing
    // point between one real-time substep and the next - the window where
    // a fixed-future-time sample lands ON a car-length-wide conflict is only
    // a few car-lengths of TRAVEL wide, easily narrower than the gap between
    // 1 s/3 s/5 s themselves at 15-20 m/s. A continuous sweep still reports
    // the graded 1 s/3 s/5 s bands (GradedCap), just found reliably.
    const double TtcSweepStepS = 0.1;
    const double TtcSweepMaxS = 5.0;
    // Neighbor search radius for the pair check (current positions) - wider
    // than the corridor's look-ahead because the conflict may still be many
    // seconds off for two cars converging on a shared point from the side.
    const double PairCheckRadiusM = 150.0;
    // Staged priority response: how long a priority car watches the other car
    // (giving it a chance to yield) before it brakes for self-protection.
    // A human doesn't slam the brakes the instant a conflict is predicted -
    // they look to see if the other driver will yield, and brake only if they
    // don't (or if it's already too late to stop).
    const double AlertGraceS = 1.0;

    sealed class Grid
    {
        readonly Dictionary<long, List<Car>> _cells = new();

        static long Cell(double v) => (long)Math.Floor(v / CellSize);
        static long Key(long cx, long cy) => (cx << 32) ^ (cy & 0xFFFFFFFFL);

        public void Insert(Car c)
        {
            long k = Key(Cell(c.X), Cell(c.Y));
            if (!_cells.TryGetValue(k, out var l)) _cells[k] = l = new List<Car>();
            l.Add(c);
        }

        public static Grid Build(IEnumerable<Car> cars)
        {
            var g = new Grid();
            foreach (var c in cars) g.Insert(c);
            return g;
        }

        /// <summary>All cars whose cell is within `radius` cells (square).</summary>
        public List<Car> Near(double x, double y, int radius)
        {
            var res = new List<Car>();
            long cx = Cell(x), cy = Cell(y);
            for (long dx = -radius; dx <= radius; dx++)
                for (long dy = -radius; dy <= radius; dy++)
                    if (_cells.TryGetValue(Key(cx + dx, cy + dy), out var l))
                        res.AddRange(l);
            return res;
        }
    }

    static List<(double X, double Y)> Body(Car c) =>
        ObstacleGeometry.PlayerBodyCorners(c);

    // Shrunken body for the RESOLVE (rollback) decision: BoxesIntersect uses
    // strict inequalities, so two boxes merely TOUCHING (resting nose-to-nose)
    // read as "intersecting". Without this tolerance a pair that came into
    // contact once is rolled back + stopped EVERY tick forever - the driver
    // pushes forward, the boxes touch again, rollback, repeat: a permanent
    // collision lock that gridlocked the whole fig8_cross fleet (measured:
    // uids 11 x 38 pinned at contact for 140+ s). Real penetration (>= 5 cm
    // total) still triggers the rollback; mere contact does not.
    const double ResolvePenetrationEpsM = 0.025;
    static List<(double X, double Y)> BodyForResolve(Car c)
    {
        var (bx, by) = c.BodyCenter();
        return ObstacleGeometry.BoxCorners(bx, by, c.Heading,
            Math.Max(0.1, c.LengthM - 2 * ResolvePenetrationEpsM),
            Math.Max(0.1, c.WidthM - 2 * ResolvePenetrationEpsM));
    }

    // TEMPORARY debug hook (remove after use)
    public static List<string> DebugLog = new();

    // Decision-log debounce: pending threat replacements per car uid and how
    // many CONSECUTIVE substeps they have won the cap (see the final pass in
    // ComputeAvoidCaps). Grows/shrinks with the car population; a cleared car
    // just leaves a small stale entry (uids are monotonic, never reused).
    static readonly Dictionary<int, (string Key, int Ticks)> _capCandidate = new();
    const int PERSIST_TICKS = 15;   // 0.25 s of sim time at 60 Hz

    // Cap DECISION persistence (docs/Human Factor Model.md §3/§5): while a
    // car's cap reason is absent, the brake decision is HELD for a minimum
    // duration instead of releasing immediately - an immediate release on a
    // boundary situation (both cars slow and close) flutters the cap on/off
    // every few substeps (the car's own braking grows the TTC, which the
    // staged logic reads as "yielding"), and the car creeps into the threat.
    static readonly Dictionary<int, double> _lastCap = new();      // uid -> last cap value
    static readonly Dictionary<int, int> _releaseHold = new();     // uid -> absent substeps

    /// <summary>Minimum substeps a cap's reason must be ABSENT before the
    /// brake decision releases: base 0.5 s + deterministic per-uid jitter
    /// 0..+0.5 s (cars release out of sync, no run-to-run randomness - R20).
    /// The whole 0.5-1.0 s range sits in the doc's 0.5-1.5 s persistence band
    /// AND above the ~0.3 s period of box-boundary flicker (a shorter hold
    /// would release right into the next flicker cycle and the car would
    /// creep forward every cycle - measured: fig8_xing car 40 creeping into
    /// stopped car 14 at ~2 Hz).</summary>
    static int ReleaseHoldTicks(int uid) => 30 + (uid * 14) % 31;

    /// <summary>Minimum speed at which "the other car is yielding" is a
    /// valid read of a growing TTC. Below this the other car is standing
    /// still and the TTC growth is the watcher's own braking (the flutter).
    /// A car genuinely clearing a crossing moves at several m/s.</summary>
    const double OtherYieldingMinSpeedMps = 0.5;

    // --- Road signs (yield at signed approaches) ----------------------------
    const double SignActRangeM = 60.0;        // the sign acts from this distance out
    const double PriorityProximityM = 25.0;   // a priority car "at the crossing" within this
    const double SignCrossClearM = 5.0;       // crossing-zone half-width + margin, for t_clear
    const double SignGapMarginS = 1.5;        // extra time margin demanded of the gap
    // NOSE gap of the yield stop line: the front bumper stops this far before
    // the node. Measured (Fig8StopLineDiagTests SAT sweep): a stopped yield car
    // needs its nose >= ~4.0-4.5 m from C so that priority traffic passing
    // through keeps >= 0.5 m body clearance for EVERY class - at the old 1.0 m
    // the nose sat INSIDE the crossing and blocked the priority lane, which is
    // exactly the deadlock mode (priority leader brakes for the stopped yield
    // car, which waits for the priority car: circular wait).
    // 6.0, not 4.5: the yield lane axis crosses the priority lane axis ~2.6 m
    // before C (lane offsets of 1.75 m each side at a ~94 deg crossing), so a
    // nose 4.5 m out still leaves the front corner inside a passing pickup's
    // body - measured SAT overlap on the 50-car run (pickup at 103 km/h, legal
    // class speed, clipped a stopped yield car at its line). 6.0 puts the
    // nearest corner ~2 m clear of the priority lane for every class.
    const double YieldStopNoseGapM = 6.0;

    /// <summary>Per-car speed caps from the cars in each forward corridor.
    /// Called with PRE-step positions. Missing uid = no constraint.
    /// `net` enables road-sign rules (yield at signed approaches).
    /// `simTime` timestamps the per-car DECISION log entries (Car.Decisions):
    /// each car gets one entry when the REASON for its cap changes - the
    /// detail window uses that to show why a car is braking / held.</summary>
    public static Dictionary<int, double> ComputeAvoidCaps(IEnumerable<Car> cars,
                                                          RoadNetwork? net = null,
                                                          double simTime = 0.0)
    {
        DebugLog.Clear();   // TEMPORARY: per-call log (tests read it after)
        var carList = new List<Car>(cars);
        var grid = Grid.Build(carList);
        var caps = new Dictionary<int, double>();
        // WHY each car is capped: (cap, stable key, human text). The key
        // identifies the situation (rule + other car), so a continuously
        // re-evaluated cap logs ONE event, and a changing threat (car 26 ->
        // car 28) logs a new one.
        var reasons = new Dictionary<int, (double Cap, string Key, string Text)>();
        void OfferCap(int uid, double cap, string key, string text)
        {
            if (reasons.TryGetValue(uid, out var r) && r.Cap <= cap) return;
            reasons[uid] = (cap, key, text);
        }
        double pppm = Config.PIXELS_PER_METER;
        // Broadphase ring covers the LARGEST per-car look-ahead (top speed);
        // the per-car filter below narrows it to each car's own range.
        int ring = (int)Math.Ceiling(LookAheadMaxM / CellSize) + 1;

        // --- Staged PRIORITY response (human-like) -------------------------
        // A node is a SIGNED CROSSING if at least one approach to it is
        // YIELD-signed. A car is a PRIORITY car there if it approaches on a
        // NON-yield arm (it has Vorfahrt). Only priority cars get the staged
        // response: on a predicted crossing conflict they become alert and
        // watch the other car (no immediate brake), and brake only if the
        // other car fails to yield for AlertGraceS or it is too late to stop.
        // Holding this stable state is what stops the tick-to-tick brake/
        // resume flutter that gridlocked the signed crossing.
        var signedNodes = new HashSet<string>();
        if (net is not null)
            foreach (var kv in net.Signs)
                if (kv.Value == SignType.Yield)
                    signedNodes.Add(kv.Key.Item2);
        bool IsPriorityCar(Car c)
        {
            if (net is null || signedNodes.Count == 0) return false;
            var seg = net.Segments[c.SegIdx];
            string aheadNode = c.Forward ? seg.EndNode : seg.StartNode;
            if (!signedNodes.Contains(aheadNode)) return false;
            if (!net.Nodes.TryGetValue(aheadNode, out var nxy)) return false;
            double dToNodeM = Math.Hypot(c.X - nxy.X, c.Y - nxy.Y) / pppm;
            if (dToNodeM > SignActRangeM) return false;
            if (net.Signs.TryGetValue((c.SegIdx, aheadNode), out var s) &&
                s == SignType.Yield)
                return false;   // I'm on the yield arm, not the priority arm
            return true;
        }
        // Most-immediate crossing conflict per PRIORITY car this tick (uid ->
        // the car it is watching, the TTC, and its own distance to the meet
        // point). Filled by the v2 pair loop, consumed after it.
        var pConflicts = new Dictionary<int, (int Other, double Ttc, double DistP)>();

        foreach (var c in carList)
        {
            double rad = Math.Radians(c.Heading);
            double fx = Math.Sin(rad), fy = Math.Cos(rad);     // forward
            double rx = Math.Cos(rad), ry = -Math.Sin(rad);    // right
            double best = double.PositiveInfinity;
            int _v1By = -1; double _v1Along = 0, _v1Lat = 0, _v1Gap = 0;   // TEMPORARY
            // This car's own range: fast cars see (and must react to) the
            // leader much further out so they can still stop in time.
            double lookAhead = LookAheadForSpeed(c.Speed);

            foreach (var o in grid.Near(c.X, c.Y, ring))
            {
                if (o.Uid == c.Uid) continue;
                // Staged PRIORITY response: while this car is WATCHING car o
                // (alert set by last tick's v2), the staged logic below is the
                // single brake authority for o (it now includes a gap-based
                // term - see the self-protection branch below). Skipping v1
                // here keeps one cap source per situation (the flutter fix);
                // once the alert clears, v1 resumes in full.
                if (c.AlertOtherUid == o.Uid) continue;
                double dxc = o.X - c.X, dyc = o.Y - c.Y;
                double alongPx = dxc * fx + dyc * fy;          // ahead?
                if (alongPx < 0 || alongPx > lookAhead * pppm) continue;
                double latPx = dxc * rx + dyc * ry;
                if (Math.Abs(latPx) > CorridorHalfWidthM * pppm) continue;

                // Oblique/crossing pair (heading differs a lot): the centre-in-
                // corridor test is too coarse for them - a car stopped at its
                // yield line on the crossing arm sits < 1.8 m off my axis but
                // its BODY clears my lane by a metre or more, and capping me
                // for it deadlocks the signed crossing (it cannot move while I
                // am within its sign range, and I would stop for it). For such
                // pairs require their body to actually intersect my forward
                // corridor: then they are in my path and I brake; otherwise
                // they pass beside me (v2/Resolve own the rest).
                double hdiff = Math.Abs(((c.Heading - o.Heading + 540.0) % 360.0) - 180.0);
                if (hdiff >= 60.0 &&
                    !ObstacleGeometry.BoxesIntersect(ForwardCorridorBox(c, pppm), Body(o)))
                    continue;

                // Bumper gap in metres: centre distance minus HALF of each
                // car's REAL length (per-class footprints - a truck pair must
                // register contact at true bumper touch, not sedan-box math;
                // the old single CAR_LENGTH let mixed-length pairs close to
                // physical overlap before "touching" triggered).
                double gapM = alongPx / pppm - (c.LengthM + o.LengthM) * 0.5;
                if (gapM <= 0.25) { best = 0.0; break; }       // touching -> stop

                // Lead speed projected onto my axis (head-on car -> 0).
                double orad = Math.Radians(o.Heading);
                double vAhead = o.Speed *
                    (Math.Sin(orad) * fx + Math.Cos(orad) * fy);
                // Fastest speed that keeps the pair RECOVERABLE if the lead
                // brakes at full CAR_BRAKING from this instant: with equal
                // braking limits the gap can only hold if
                //   G >= StandstillGap + vr*vl/a + vr^2/(2a)
                // which solves to exactly vc = sqrt(vl^2 + 2a*(G - gap)).
                // The old additive form (vl + sqrt(2a*(G-gap))) let speed
                // differentials build up while the lead braked - measured on
                // the 50-car loop: a follower at 44 km/h closed to 4.5 m
                // behind a braking leader and no legal braking could save it,
                // seeding the rear-end cascade that gridlocked the fleet.
                double vl = Math.Max(vAhead, 0.0);
                double cap = Math.Sqrt(vl * vl +
                             2.0 * Config.CAR_BRAKING *
                             Math.Max(gapM - StandstillGapM, 0.0));
                if (cap < best) { best = cap; _v1By = o.Uid;
                    _v1Along = alongPx / pppm; _v1Lat = latPx / pppm;
                    _v1Gap = gapM; }   // TEMPORARY
            }

            if (best < double.PositiveInfinity)
            {
                OfferCap(c.Uid, best, $"avoid#{_v1By}",
                         $"[R7] car {_v1By} ahead (gap {_v1Gap:F1} m)");
                DebugLog.Add($"v1 uid{c.Uid}={best:F2} along={_v1Along:F1} lat={_v1Lat:F2} by {_v1By}");   // TEMPORARY
            }
        }

        // --- v2: oblique/crossing pair prediction + right-before-left ---
        // A car not yet inside anyone's forward corridor (e.g. approaching a
        // crossing at 90 degrees) is invisible to the loop above. Predict
        // both cars' positions at each TTC stage; the earliest stage whose
        // predicted body boxes overlap decides the response. Only the
        // car that must YIELD (right-before-left) gets capped - the other
        // proceeds, so two cars converging on a crossing do not both stop.
        //
        // Each car's sweep trajectory is precomputed ONCE here (not inside
        // the pair loop): a car near a cluster of traffic can be a candidate
        // for many neighbors, and PredictPos (a RefLine bisect + trig) is not
        // free - recomputing it per PAIR is O(pairs), per CAR is O(cars).
        int nSweepSteps = (int)Math.Round(TtcSweepMaxS / TtcSweepStepS);
        var sweeps = new Dictionary<int, (double X, double Y, double H, double Dist)[]>();
        foreach (var c in carList)
        {
            var arr = new (double, double, double, double)[nSweepSteps];
            for (int i = 0; i < nSweepSteps; i++)
                arr[i] = PredictPos(c, (i + 1) * TtcSweepStepS, pppm);
            sweeps[c.Uid] = arr;
        }

        int pairRing = (int)Math.Ceiling(PairCheckRadiusM / CellSize) + 1;
        foreach (var c in carList)
        {
            foreach (var o in grid.Near(c.X, c.Y, pairRing))
            {
                if (o.Uid <= c.Uid) continue;   // each pair once, deterministic
                double distPx = Math.Hypot(o.X - c.X, o.Y - c.Y);
                if (distPx > PairCheckRadiusM * pppm) continue;

                // Same-lane following pairs are v1's domain: if one car sits
                // directly behind the other within the corridor half-width,
                // the forward-corridor cap already governs the follower -
                // v2 must not turn the LEADER into a yielder for its own tail.
                // (The old pair check read a follower slightly right of the
                // leader's axis as "coming from my right + closing" and made
                // leaders stop for their followers at every queue - the fig8
                // fleet gridlock.)
                if (IsDirectlyBehind(c, o, pppm) || IsDirectlyBehind(o, c, pppm)) continue;

                // A pair with a stationary member is NOT skipped: a car driving
                // at full speed towards a body that really blocks its path must
                // brake for it (the old blanket skip let a 103 km/h pickup clip
                // a stopped yield car - nothing capped the mover). But test such
                // pairs with UNPADDED boxes: the 2 m length pad alone used to
                // fabricate conflicts against yield cars resting at their lines
                // (real clearance ~1 m), and with the sign rule pinning the yield
                // car that deadlocked both sides. Unpadded = real overlap only.
                const double StationaryEps = 0.1;      // m/s
                bool anyStationary = c.Speed < StationaryEps || o.Speed < StationaryEps;

                var conflict = EarliestConflict(c, o, sweeps[c.Uid], sweeps[o.Uid],
                                                padM: anyStationary ? 0.0 : PredictionPadM);
                if (conflict is null) continue;
                var (stageS, distC, distO) = conflict.Value;

                // Right-before-left decides which of the two bears the cap.
                // NOTE: a PRIORITY-road car is NOT exempt from this - right
                // of way never excuses collision avoidance (user rule): if a
                // real overlap is predicted, the priority car brakes too. The
                // sign only ADDS an obligation on the yield car (the v3 rule
                // below), it does not remove the physical avoidance for the
                // other side.
                bool cYields = ComesFromMyRight(c, o);
                bool oYields = ComesFromMyRight(o, c);
                int yieldUid;
                if (anyStationary)
                {
                    // The mover bears the cap: a stopped body cannot move away,
                    // and ComesFromMyRight reads stationary cars as never
                    // closing (right tie-break would coin-flip the cap onto
                    // the car that cannot do anything about it).
                    yieldUid = c.Speed < StationaryEps ? o.Uid : c.Uid;
                }
                else if (cYields != oYields)
                    yieldUid = cYields ? c.Uid : o.Uid;
                else
                    yieldUid = Math.Min(c.Uid, o.Uid);  // tie -> lower uid yields
                double dMeet = yieldUid == c.Uid ? distC : distO;
                double cap = GradedCap(stageS, dMeet);

                int otherUid = yieldUid == c.Uid ? o.Uid : c.Uid;
                string v2Text = anyStationary
                    ? $"[R2] car {otherUid} stopped in my path"
                    : $"[R2] predicted crossing with car {otherUid} in {stageS:F1} s";
                // Same key family as v1 ("avoid#uid"): both are "car X is in
                // my path" - the cap values oscillate between the two models
                // tick-to-tick, and a per-model key would log a new event on
                // every flip instead of one per situation.
                // Staged PRIORITY response: a priority-road car (Vorfahrt) does
                // NOT brake the instant a crossing conflict is predicted - it
                // observes first. So the immediate v2 cap is withheld for it and
                // the staged logic below decides its brake instead. Non-priority
                // cars keep the immediate cap (right-before-left / stopped body).
                var yieldCar = yieldUid == c.Uid ? c : o;
                if (!IsPriorityCar(yieldCar))
                    OfferCap(yieldUid, cap, $"avoid#{otherUid}", v2Text);
                DebugLog.Add($"v2 uid{yieldUid}={cap:F2} pair({c.Uid},{o.Uid}) s={stageS:F1}");   // TEMPORARY

                // Record the most-immediate crossing conflict (smallest TTC) for
                // EACH priority car in the pair, so the staged logic after this
                // loop can drive its alert state machine (observe, then brake
                // only if the other car doesn't yield in time or it's too late).
                foreach (var car in new[] { c, o })
                {
                    if (!IsPriorityCar(car)) continue;
                    double distP = car == c ? distC : distO;
                    int otherUid2 = car == c ? o.Uid : c.Uid;
                    if (!pConflicts.TryGetValue(car.Uid, out var pc) || stageS < pc.Ttc)
                        pConflicts[car.Uid] = (otherUid2, stageS, distP);
                }
            }
        }

        // --- Staged PRIORITY response: drive the per-car alert state machine
        // Uses each car's most-immediate crossing conflict (if any):
        //   first detection -> become alert (watch, NO brake)
        //   TTC growing     -> the other car is yielding - keep watching
        //   TTC flat + (too late OR grace expired) -> self-protection brake
        //   conflict gone   -> clear the alert, resume normal behaviour
        var carByUid = carList.ToDictionary(k => k.Uid);
        foreach (var c in carList)
        {
            if (!pConflicts.TryGetValue(c.Uid, out var pc))
            {
                if (c.AlertOtherUid != 0)
                {
                    int gone = c.AlertOtherUid;
                    c.ClearAlert();
                    c.RecordDecision(simTime, $"[R6] car {gone} cleared - proceeding");
                }
                continue;
            }
            int otherUid = pc.Other;
            double ttc = pc.Ttc;
            double brakingTime = c.Speed > 0.01 ? c.Speed / Config.CAR_BRAKING : double.MaxValue;

            if (c.AlertOtherUid == 0)
            {
                c.AlertOtherUid = otherUid;
                c.AlertStartT = simTime;
                c.AlertLastTtc = ttc;
                c.RecordDecision(simTime,
                    $"[R6] alert: watching car {otherUid} (crossing in {ttc:F1} s)");
                continue;
            }
            if (c.AlertOtherUid != otherUid)
            {
                if (ttc >= c.AlertLastTtc) continue;   // nearer conflict wins
                c.AlertOtherUid = otherUid;
                c.AlertStartT = simTime;
                c.AlertLastTtc = ttc;
                c.RecordDecision(simTime,
                    $"[R6] alert: watching car {otherUid} (crossing in {ttc:F1} s)");
                continue;
            }
            if (ttc > c.AlertLastTtc + 0.05)
            {
                // "Yielding" is only a valid read if the OTHER car is
                // actually moving (clearing the crossing). A standing-still
                // other car grows the TTC purely because I am braking -
                // releasing on that is the brake/creep flutter (the TTC
                // growth is my own action, docs/Human Factor Model.md §3).
                if (carByUid.TryGetValue(otherUid, out var other) &&
                    other.Speed >= OtherYieldingMinSpeedMps)
                {
                    c.AlertLastTtc = ttc;   // other car yielding - keep watching
                    continue;
                }
                // stationary other: do NOT release - fall through to the
                // brake condition below.
            }
            if (ttc < brakingTime || (simTime - c.AlertStartT) > AlertGraceS)
            {
                // The cap is the TIGHTER of:
                //  (a) the meeting-point cap (GradedCap) - assumes a CLEAR
                //      meeting zone: stop StandstillGap short of it; and
                //  (b) the actual GAP to the other car's body (the v1
                //      formula): the meeting zone can be OCCUPIED - a
                //      stationary car sitting at the meeting point would be
                //      clipped by (a) alone, which stops me 3 m short of the
                //      point = INSIDE the other car (measured: the fig8_xing
                //      car 13 vs 37 crash, both stopped nose-on-tail).
                double cap = GradedCap(Math.Max(ttc, 1.01), pc.DistP);
                if (carByUid.TryGetValue(otherUid, out var oCar))
                {
                    double radC = Math.Radians(c.Heading);
                    double fx2 = Math.Sin(radC), fy2 = Math.Cos(radC);
                    double alongM = ((oCar.X - c.X) * fx2 + (oCar.Y - c.Y) * fy2) / pppm;
                    double gapM = Math.Max(alongM - (c.LengthM + oCar.LengthM) * 0.5, 0.0);
                    double orad = Math.Radians(oCar.Heading);
                    double vl = Math.Max(oCar.Speed *
                        (Math.Sin(orad) * fx2 + Math.Cos(orad) * fy2), 0.0);
                    cap = Math.Min(cap, Math.Sqrt(vl * vl +
                        2.0 * Config.CAR_BRAKING * Math.Max(gapM - StandstillGapM, 0.0)));
                }
                OfferCap(c.Uid, cap, $"avoid#{otherUid}",
                         $"[R2] self-protection vs car {otherUid} (TTC {ttc:F1} s)");
            }
            // else: within grace, not too late - keep watching, no brake.
        }

        // --- v3: road signs -------------------------------------------------
        // A car approaching a node on a YIELD-signed approach must be able to
        // stop before it while any PRIORITY-road car is near that node. The
        // sign makes the crossing deterministic: right-before-left alone
        // deadlocks under dense two-way flow (leaders stop at the mouth with
        // followers packed behind, each waiting for the other). Priority cars
        // are never capped by this rule - they keep flowing through.
        if (net is not null && net.Signs.Count > 0)
            foreach (var c in carList)
            {
                var seg = net.Segments[c.SegIdx];
                string aheadNode = c.Forward ? seg.EndNode : seg.StartNode;
                if (!net.Signs.TryGetValue((c.SegIdx, aheadNode), out var sign) ||
                    sign != SignType.Yield)
                    continue;
                if (!net.Nodes.TryGetValue(aheadNode, out var nxy)) continue;
                // Per-vehicle stop line: front bumper YieldStopNoseGapM before
                // the node (see constant), so a stopped yield car's body is
                // clear of the priority lane for every vehicle class.
                // c.X/c.Y is the REAR axle, so add the rear-axle offset -
                // without it the nose stops 1.28 m closer to C than intended.
                double stopGapM = c.LengthM * 0.5 + Config.REAR_AXLE_OFFSET_M + YieldStopNoseGapM;
                double dToNodeM = Math.Hypot(c.X - nxy.X, c.Y - nxy.Y) / pppm;
                if (dToNodeM > SignActRangeM)
                    continue;   // not approaching yet
                // COMMITTED ONCE THE NOSE CROSSES THE STOP LINE: a car with
                // dToNodeM <= stopGapM has entered the crossing, and the
                // sign must not hold it - only v1/v2 (a real predicted
                // overlap) may brake it, and they release as soon as the
                // conflict clears, so a committed car always keeps going.
                // (User rule 2026-09-09: once you START the crossing you
                // continue; a car that is already blocking the other cars'
                // way must keep going.)
                //
                // The old rule HELD a blocked car at 0 between the stop line
                // and a rear-bumper clear point. That was exactly the
                // deadlock mode: the held car sat with its nose in the box,
                // the priority car braked for the stopped body, and the body
                // waited for the priority car - circular wait (measured on
                // fig8_cross: uid 42 held at 3.5 m from C by the sign while
                // uid 26 braked for it; whole loop frozen from t~30 s).
                if (dToNodeM <= stopGapM)
                {
                    if (c.CrossingCommittedNode != aheadNode)
                    {
                        c.CrossingCommittedNode = aheadNode;
                        c.RecordDecision(simTime,
                                         "[R3] entered crossing - committed, continuing");
                    }
                    continue;   // entered the crossing - committed, free to go
                }

                // GAP ACCEPTANCE (time-based, like a human driver): the car may
                // enter only if it can CLEAR the crossing before any APPROACHING
                // car on the other diagonal arrives. A pure proximity snapshot
                // (old rule) let a yield car enter while a priority car 25+ m
                // out closed at ~14 m/s - they collided inside the crossing and
                // Resolve locked both, gridlocking the whole fleet.
                var navC = c.BicycleNav;
                double profV = navC is null ? 10.0 : navC.SpeedProfileAt(navC.S);
                // Assume the car crosses at its CURRENT speed (floor 3 m/s:
                // from a standstill it accelerates through the box faster than
                // that on average). The old flat 4 m/s made t_clear 3x too long
                // for cars entering at cruise, so they queued at the line even
                // when the crossing was empty.
                double crossAssumedV = Math.Min(profV, Math.Max(c.Speed, 3.0));
                double distToClearM = Math.Max(dToNodeM - c.LengthM * 0.5, 0.0) + SignCrossClearM;
                double tClearS = distToClearM / crossAssumedV;

                bool blocked = false;
                int blockedBy = -1;
                // Which rule gated entry: R4 = cannot CLEAR before an
                // approaching car arrives (temporal gap); R5 = a priority car
                // is already at the crossing.
                bool byFeasibility = false;
                foreach (var o in carList)
                {
                    if (o.Uid == c.Uid || o.SegIdx == c.SegIdx) continue;
                    var oseg = net.Segments[o.SegIdx];
                    if (oseg.StartNode != aheadNode && oseg.EndNode != aheadNode)
                        continue;
                    // Only CROSSING pairs threaten: a car on my OWN road's
                    // other arm (parallel/anti-parallel heading) passes beside
                    // me in its own lane - counting it deadlocked the yield
                    // diagonal against itself under dense flow (both arms
                    // queued at their lines, each waiting for the other, with
                    // the priority road empty). The crossing angle here is
                    // ~94 deg; allow a wide band around perpendicular.
                    double hdiff = Math.Abs(((c.Heading - o.Heading + 540.0) % 360.0) - 180.0);
                    if (hdiff < 40.0 || hdiff > 140.0)
                        continue;   // same road - no conflict
                    double dO = Math.Hypot(o.X - nxy.X, o.Y - nxy.Y) / pppm;
                    if (dO < PriorityProximityM)
                    { blocked = true; blockedBy = o.Uid; break; }   // already at the crossing
                    // Approaching from its arm? Forward vector must point at C.
                    double odx = (nxy.X - o.X) / pppm, ody = (nxy.Y - o.Y) / pppm;
                    double orad2 = Math.Radians(o.Heading);
                    if (Math.Sin(orad2) * odx + Math.Cos(orad2) * ody < 0.3 * dO)
                        continue;   // exiting / crossing sideways - not a threat
                    double etaS = dO / Math.Max(o.Speed, 3.0);
                    if (etaS < tClearS + SignGapMarginS)
                    { blocked = true; blockedBy = o.Uid; byFeasibility = true; break; }
                }
                if (!blocked) continue;

                // Fastest speed that still stops at the stop line.
                double ycap = Math.Sqrt(2.0 * Config.CAR_BRAKING *
                                        Math.Max(dToNodeM - stopGapM, 0.0));
                OfferCap(c.Uid, ycap, $"sign#{aheadNode}#{blockedBy}",
                         byFeasibility
                         ? $"[R4] cannot clear before car {blockedBy} arrives " +
                           ($"({dToNodeM:F0} m out)")
                         : $"[R5] yield sign: car {blockedBy} at the crossing " +
                           ($"({dToNodeM:F0} m out)"));
                DebugLog.Add($"sign uid{c.Uid}={ycap:F2} node={aheadNode} d={dToNodeM:F1}");   // TEMPORARY
            }

        // --- Cap decision persistence + decision log -------------------------
        // The APPLIED cap follows the tightest reason every substep (the 60 Hz
        // physical net, doc §5.1). The brake DECISION persists: a release
        // requires the reason to be ABSENT for a minimum hold (0.5 s ± 0.25 s
        // per-uid jitter), and while holding the LAST cap value keeps applying
        // - so a boundary situation (both cars slow and close, or a stopped
        // body in the path) brakes to rest instead of fluttering: the old
        // release-immediate behavior flipped the cap on/off every ~5 substeps
        // (12 decisions/s vs the ~2.5/s human ceiling, doc §1) and the car
        // crept a few cm into the threat on every "gap clear".
        // A DIFFERENT threat still replaces the logged one only after
        // PERSIST_TICKS consecutive substeps: near a cluster of stopped cars
        // the binding cap oscillates between them tick-to-tick (44 then 42
        // then 44 ...), and without the debounce the log would flip every
        // 0.05 s. The first threat is logged immediately.
        foreach (var c in carList)
        {
            if (reasons.TryGetValue(c.Uid, out var r))
            {
                caps[c.Uid] = r.Cap;
                _lastCap[c.Uid] = r.Cap;
                _releaseHold.Remove(c.Uid);
                if (c.ActiveCapKey is null)
                {
                    _capCandidate.Remove(c.Uid);
                    c.RecordDecision(simTime, "brake: " + r.Text);
                    c.ActiveCapKey = r.Key;
                }
                else if (c.ActiveCapKey != r.Key)
                {
                    if (_capCandidate.TryGetValue(c.Uid, out var cand) &&
                        cand.Key == r.Key)
                        _capCandidate[c.Uid] = (r.Key, cand.Ticks + 1);
                    else
                        _capCandidate[c.Uid] = (r.Key, 1);
                    if (_capCandidate[c.Uid].Ticks >= PERSIST_TICKS)
                    {
                        _capCandidate.Remove(c.Uid);
                        c.RecordDecision(simTime, "brake: " + r.Text);
                        c.ActiveCapKey = r.Key;
                    }
                }
                else
                {
                    _capCandidate.Remove(c.Uid);
                }
            }
            else if (c.ActiveCapKey is not null)
            {
                // Reason absent: hold the brake decision for the minimum
                // duration before declaring the situation clear.
                int hold = _releaseHold.GetValueOrDefault(c.Uid, 0) + 1;
                if (hold >= ReleaseHoldTicks(c.Uid))
                {
                    _releaseHold.Remove(c.Uid);
                    _lastCap.Remove(c.Uid);
                    _capCandidate.Remove(c.Uid);
                    c.RecordDecision(simTime, "gap clear - resuming");
                    c.ActiveCapKey = null;
                }
                else
                {
                    _releaseHold[c.Uid] = hold;
                    // Keep braking with the last cap value - the car rests
                    // instead of creeping toward the (still-near) threat.
                    caps[c.Uid] = _lastCap.GetValueOrDefault(c.Uid, 0.0);
                }
            }
        }
        return caps;
    }

    /// <summary>Predict `car`'s position `t` seconds out: advances along its
    /// BicycleNav refline when one is active (clamped to Total - a car does
    /// not predict past its route), else linear extrapolation from its
    /// current heading. Prediction speed is max(current, planned target) -
    /// conservative, so a car that is slow now but about to cruise doesn't
    /// read as harmless. Returns the predicted pose plus the arc-length the
    /// car will have covered by then (used as its own braking distance if it
    /// turns out to be the one that must yield).</summary>
    static (double X, double Y, double HeadingDeg, double DistM) PredictPos(Car car, double t, double pppm)
    {
        double v = Math.Max(0.0, Math.Max(car.Speed, car.TargetSpeed));
        var nav = car.BicycleNav;
        var refLine = nav?.Ref;
        if (refLine is not null)
        {
            double s0 = nav!.S;
            double sPred = Math.Min(s0 + v * t, refLine.Total);
            var (px, py) = refLine.PointAt(sPred);
            return (px, py, refLine.HeadingAt(sPred), sPred - s0);
        }
        double rad = Math.Radians(car.Heading);
        double dM = v * t;
        return (car.X + Math.Sin(rad) * dM * pppm, car.Y + Math.Cos(rad) * dM * pppm,
                car.Heading, dM);
    }

    // LENGTH padding added to each predicted body box before the overlap
    // test (prediction only - Resolve's actual-contact check uses the real
    // car size). Widens the detection window in TIME by (2*pad)/closingSpeed,
    // which must clear the gap between sweep samples - at ~20 m/s closing
    // and a 0.1 s step that gap is ~2 m, so 2 m of padding keeps detection
    // continuous instead of flickering on/off as the sweep just clips the
    // unpadded box's edge from one substep to the next. LENGTH only, NOT
    // width: padding width too pushes the effective half-width (0.9 + pad)
    // past half the lane separation on a 7 m road (~1.75 m), so two cars
    // safely 3.5 m apart in their own lanes would falsely "overlap" - the
    // sampling gap this compensates for is purely about how far a car
    // travels ALONG its own heading between samples, which only needs
    // length, not width.
    const double PredictionPadM = 2.0;

    /// <summary>Earliest time (up to TtcSweepMaxS) at which the two cars'
    /// (length-)padded predicted body boxes overlap, or null if they never
    /// do within the window. Each box uses that car's REAL footprint
    /// (per-class length + pad, real width - no width padding). Takes each
    /// car's PRECOMPUTED sweep (one entry per TtcSweepStepS, same length for
    /// every car). Returns each car's predicted travel distance (m) by then
    /// - the braking distance for whichever one yields.</summary>
    static (double TimeS, double DistC, double DistO)? EarliestConflict(
        Car c, Car o,
        (double X, double Y, double H, double Dist)[] sweepC,
        (double X, double Y, double H, double Dist)[] sweepO,
        double padM = PredictionPadM)
    {
        for (int i = 0; i < sweepC.Length; i++)
        {
            var (cx, cy, ch, cd) = sweepC[i];
            var (ox, oy, oh, od) = sweepO[i];
            var boxC = ObstacleGeometry.BoxCorners(cx, cy, ch,
                c.LengthM + 2.0 * padM, c.WidthM);
            var boxO = ObstacleGeometry.BoxCorners(ox, oy, oh,
                o.LengthM + 2.0 * padM, o.WidthM);
            if (ObstacleGeometry.BoxesIntersect(boxC, boxO))
                return ((i + 1) * TtcSweepStepS, cd, od);
        }
        return null;
    }

    /// <summary>Speed-dependent look-ahead in metres: how far ahead a car
    /// must see a stopped leader to still be able to stop in time =
    /// reaction distance (v·t_react) + full braking distance (v²/2a) +
    /// standstill gap, floored at LookAheadMinM. See the constant block for
    /// why the range must be at least the car's own stopping distance.</summary>
    static double LookAheadForSpeed(double v)
    {
        double brakingDist = v * v / (2.0 * Config.CAR_BRAKING);
        return Math.Max(LookAheadMinM, v * ReactionTimeS + brakingDist + StandstillGapM);
    }

    /// <summary>My body extended from my centre to my speed-dependent
    /// look-ahead past my front bumper, at my REAL width - the strip of road
    /// I actually need to be clear to proceed. Used by v1's oblique-pair
    /// gate (see there).</summary>
    static List<(double X, double Y)> ForwardCorridorBox(Car c, double pppm)
    {
        double rad = Math.Radians(c.Heading);
        double fx = Math.Sin(rad), fy = Math.Cos(rad);
        var (bx, by) = c.BodyCenter();
        double s1 = c.LengthM * 0.5 + LookAheadForSpeed(c.Speed);  // centre -> front+lookahead
        double sc = s1 / 2.0;
        return ObstacleGeometry.BoxCorners(bx + fx * sc * pppm, by + fy * sc * pppm,
                                           c.Heading, s1, c.WidthM);
    }

    /// <summary>True if `other` sits directly BEHIND `me` within the corridor
    /// half-width - a same-lane following configuration that v1's forward-
    /// corridor cap already governs (see the pair-loop filter).</summary>
    static bool IsDirectlyBehind(Car me, Car other, double pppm)
    {
        double rad = Math.Radians(me.Heading);
        double fx = Math.Sin(rad), fy = Math.Cos(rad);     // forward
        double rx = Math.Cos(rad), ry = -Math.Sin(rad);    // right
        double dx = other.X - me.X, dy = other.Y - me.Y;
        if (dx * fx + dy * fy >= 0) return false;          // not behind
        double latPx = Math.Abs(dx * rx + dy * ry);
        return latPx <= CorridorHalfWidthM * pppm;
    }

    /// <summary>Right-before-left (Rechts-vor-Links): true if `other` is
    /// coming from `me`'s right AND is actually closing on me, not driving
    /// away. A local-frame "ahead-right" derivation is degenerate here - two
    /// perpendicular cars can each read the other as ahead-right - so this
    /// tests the world-frame half-plane + closing-velocity instead. The
    /// closing test uses the other car's ACTUAL velocity: a stationary car
    /// (queued, parked) is never "closing" - the heading-only test used to
    /// read any car merely facing my direction as an incoming threat, which
    /// made stopped cars yield to each other forever.</summary>
    static bool ComesFromMyRight(Car me, Car other)
    {
        double rad = Math.Radians(me.Heading);
        double rightX = Math.Cos(rad), rightY = -Math.Sin(rad);
        double dx = other.X - me.X, dy = other.Y - me.Y;
        if (dx * rightX + dy * rightY <= 0) return false;      // not on my right

        double orad = Math.Radians(other.Heading);
        double ovx = Math.Sin(orad) * other.Speed, ovy = Math.Cos(orad) * other.Speed;
        return ovx * -dx + ovy * -dy > 0;                      // other closing on me
    }

    /// <summary>Graded avoidance cap for the yielding car of an oblique pair:
    /// 1 s TTC -> stop, 3 s -> the fastest speed CAR_BRAKING can still stop
    /// within `dM` (minus the standstill gap), 5 s -> mild anticipation at
    /// the gentler parking deceleration.</summary>
    static double GradedCap(double stageSeconds, double dM)
    {
        if (stageSeconds <= 1.0) return 0.0;
        double gap = Math.Max(dM - StandstillGapM, 0.0);
        double decel = stageSeconds <= 3.0 ? Config.CAR_BRAKING : Config.PARK_BRAKING;
        return Math.Sqrt(2.0 * decel * gap);
    }

    /// <summary>Called with POST-step positions: find overlapping body-box
    /// pairs, roll both cars back to their pre-step pose and stop them.
    /// Returns the uids in contact this substep (the validator treats their
    /// motion as externally constrained).
    /// A FRESH contact (crash) also logs a decision on BOTH cars - including
    /// each one's own speed at impact (the post-step speed, just before the
    /// rollback zeroes it) - and switches both cars' hazard lights on.
    /// </summary>
    public static HashSet<int> Resolve(IEnumerable<Car> cars,
                                       Dictionary<int, (double X, double Y, double Heading)> prevPose,
                                       double simTime = 0.0,
                                       Dictionary<int, double>? impactSpeed = null)
    {
        var grid = Grid.Build(cars);
        var contacted = new HashSet<int>();
        var partner = new Dictionary<int, int>();   // uid -> uid it is touching

        foreach (var c in cars)
        {
            var boxC = BodyForResolve(c);
            foreach (var o in grid.Near(c.X, c.Y, 1))
            {
                if (o.Uid <= c.Uid) continue;   // each pair once, deterministic
                if (!ObstacleGeometry.BoxesIntersect(boxC, BodyForResolve(o))) continue;

                partner[c.Uid] = o.Uid;
                partner[o.Uid] = c.Uid;

                // Both roll back to the pre-step pose (clear - a step cannot
                // skip over a car) and stop. If the pre-step poses already
                // overlapped (e.g. spawned nose-to-nose) they simply rest in
                // contact at zero speed - static, no churn.
                foreach (var k in new[] { c.Uid, o.Uid })
                {
                    var p = prevPose[k];
                    var car = k == c.Uid ? c : o;
                    car.X = p.X; car.Y = p.Y; car.Heading = p.Heading;
                    car.Speed = 0.0;
                    contacted.Add(k);
                }
            }
        }

        // Crash bookkeeping: one decision entry + hazard lights per contact
        // episode (a pinned pair re-triggers the overlap every substep, but
        // the episode is logged once, on the first substep of it).
        var carByUid = cars.ToDictionary(k => k.Uid);
        foreach (var c in cars)
        {
            if (contacted.Contains(c.Uid))
            {
                int other = partner.GetValueOrDefault(c.Uid, -1);
                if (!c.InContact)
                {
                    double myKmh = impactSpeed is not null &&
                                   impactSpeed.TryGetValue(c.Uid, out var s)
                                   ? s * 3.6 : c.Speed * 3.6;
                    c.RecordDecision(simTime,
                        $"[R2] CRASH: contact with car {other} (my speed {myKmh:F1} km/h)");
                    (c.Driver as BicycleDriver)?.SetHazard(true, $"crash with car {other}");
                    // Persist the forensic trail: the in-memory decision log
                    // would otherwise die with the process. Once per pair
                    // (uid ordering) - a chain crash dumps each pair once.
                    if (other > 0 && c.Uid < other &&
                        carByUid.TryGetValue(other, out var o))
                        CrashDumper.Dump(c, o, simTime,
                                         impactSpeed is null ? null : impactSpeed.GetValueOrDefault(c.Uid),
                                         impactSpeed is null ? null : impactSpeed.GetValueOrDefault(o.Uid));
                }
                c.InContact = true;
                c.ContactWith = other;
            }
            else
            {
                c.InContact = false;
            }
        }
        return contacted;
    }
}
