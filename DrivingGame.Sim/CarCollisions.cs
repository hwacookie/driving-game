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
    // Broadphase grid cell size (m). With LookAheadMaxM = 300 and
    // PairCheckRadiusM = 150 (below) the per-car look-ahead ring is
    // ceil(LookAheadMaxM / CellSizeM) + 1 = 16 cells wide, the pair-check
    // ring ceil(PairCheckRadiusM / CellSizeM) + 1 = 9. 20 m keeps both
    // counts small enough that dense traffic stays cheap, while staying
    // coarse enough that a cell holds few cars. (The ring radii live in
    // DRIVING_MANEUVERS.md R7 - if you change one of the constants here,
    // recompute both numbers there.)
    const double CellSizeM = 20.0;

    // Forward corridor for avoidance: a car counts as "ahead of me" while
    // its centre is laterally within (my width + its width)/2 of my axis -
    // i.e. only when the two BODIES would actually touch. A car in the
    // adjacent (opposite) lane of a 7 m road sits ~3.5 m out: two sedans
    // (1.8 m) clear it at a 1.8 m band, two 3 m trucks clear it at 3.0 m -
    // they pass with ~0.5 m to spare, which is what a driver does, so the
    // band must NOT be widened beyond the body sum (measured: a 0.3 m
    // clearance term made a pickup and a sedan on opposite arms of the same
    // road hold each other - "[R14] exit blocked by car 29" while both sat
    // in their own lane with a whole lane width between them).
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
    // (comfort stopping distance + reaction + gap: ~300 m at the map's legal
    // top speed of ~130 km/h; the 200 km/h figure would need 368 m but no
    // road here allows it). 20 m cells -> ring 16 (was 14 at the 1 g figure).
    const double LookAheadMaxM = 300.0;

    // Comfortable standstill gap: the stopping-distance curve is measured
    // from this gap, so a following car rests ~3 m short of the lead car.
    const double StandstillGapM = 3.0;
    // Lateral clearance a pair asks for when it PASSES each other (R8 and
    // the R26 single-file test). A stretch that cannot give it is single-file
    // for that pair: they physically cannot drive past one another, so the
    // pair must negotiate the claim (docs/DRIVING_MANEUVERS.md §8 R26).
    const double MinPassClearanceM = 0.5;
    // Lateral clearance asked for BESIDE an obstruction - not a constant, and
    // not two constants any more. The old shape used 0.5 twice: once as the
    // DEMAND ("I want half a metre beside it") and once as a VETO ("under half
    // a metre this pair can never pass at all"). A demand and a veto set to the
    // same number can only both hold if the delivery is exact - and it was not
    // (measured: the car passed with 0.46 m and the veto stopped it).
    // A human's side gap depends on how fast they go past it: centimetres are
    // fine at walking pace, nobody takes 50 km/h past a parked car with a
    // hand's width to spare. Road-design guidance (RASt / seitliche Abstand
    // bei Überholen) puts the comfortable clearance at ~0.3 m at a standstill
    // and ~1.0-1.25 m at 50 km/h - the line below. The BODY part of the demand
    // was already per class (touchM from Config.VEHICLE_SIZES), so a mixer pair
    // asks for more than two sedans without any of this curve.
    const double SideClearPerMpsM = 0.05;   // m of extra gap per m/s of closing speed
    public static double SideClearM(double vMps) =>
        MinPassClearanceM + Math.Max(0.0, vMps) * SideClearPerMpsM;
    /// <summary>One owner for "am I mid-pass?" - read by the corridor bound,
    /// by v1's forward corridor and by R33's intent (docs/RIGHT_OF_WAY_NEGOTIATION.md
    /// §7c: three consumers, one object).
    /// R36 (binding user decision 2026-10-21): the opposite lane is used for the
    /// MINIMUM. The commitment is not alive "until I am past the obstruction" -
    /// it is alive over the shortest stretch on which the pass physically fits:
    ///   IN  at laneChangeM(v) before the obstruction - the latest point from
    ///       which the lateral shift can still be completed. The length is the
    ///       repo's own speed-dependent lane-change figure (v · LANE_CHANGE_TIME_S
    ///       clamped to MERGE_LEN_MIN_M/MERGE_LEN_MAX_M), the one R17 uses for a
    ///       merge - not a new constant.
    ///   OUT once the body has cleared the obstruction: half of both lengths plus
    ///       the clearance the pair asks for.
    /// Before IN the car belongs to its own lane (measured before this rule: it
    /// crossed the centreline 48 m before a parked car 53 m ahead and stayed left
    /// for 230 m), after OUT the shift decays back (measured: it never returned
    /// at all). Progress runs along the SEGMENT (X1->X2), so the signed distance
    /// to the obstruction flips with my direction.</summary>
    /// <summary>The RECORD behind PassCommitted: "there is an obstruction I
    /// intend to pass and I have not cleared it yet". Alive at any distance,
    /// because R32's lateral request exists only while the obstruction is in
    /// my LINE - a record that dies with the request dies 32 m before the pass
    /// and leaves the crossing unexplained. The WINDOW (PassCommitted) decides
    /// over which stretch the borrow happens; the two must stay separate.</summary>
    /// <summary>The RECORD behind PassCommitted: "there is an obstruction I
    /// intend to pass and the manoeuvre has not finished". Alive at any distance
    /// ahead, and past the obstruction for as far as the RETURN needs - the
    /// intent must cover the whole manoeuvre, out AND back. Ending it where the
    /// body clears the obstruction released the shift 20 m too early and left
    /// the return crossing unexplained (measured: [R33] at t=9.08 s, side +1,
    /// 0.34 m off the line, no intent).</summary>
    public static bool PassPending(Car c, RoadNetwork net)
    {
        if (c.PassUid < 0 || net is null) return false;
        if (PassAlongM(c, net) > PassOutM(c, net)) return true;
        // Past the window and still in the OTHER half: the return leg is running.
        // The shift is released at the window's end, so the car comes back AFTER
        // it - and the intent must cover that crossing, or R33 calls the return
        // unexplained (measured at t=12.68 s). The manoeuvre ends when the car
        // is back in its own lane, which is what this tests.
        var seg = net.Segments[c.SegIdx];
        double ax = seg.X2 - seg.X1, ay = seg.Y2 - seg.Y1;
        double al = Math.Max(Math.Hypot(ax, ay), 1e-9);
        double sgn = c.Forward ? 1.0 : -1.0;
        double tx = ax / al * sgn, ty = ay / al * sgn;
        double offM = (c.X - seg.X1) * ty + (c.Y - seg.Y1) * -tx;   // + = my right half
        return offM < 0.0;
    }
    /// <summary>Signed metres AHEAD of me to the obstruction I am passing,
    /// projected on my heading axis. A world point, not a (segment, progress)
    /// pair: the pass may straddle a node (park_block_priority parks its car
    /// exactly on straight_m), and a segment-keyed distance dies at the hand-off.
    /// On a straight road the projection IS the along-track distance.</summary>
    private static double PassAlongM(Car c, RoadNetwork net)
    {
        double rad = Math.Radians(c.Heading);
        return (c.PassX - c.X) * Math.Sin(rad) + (c.PassY - c.Y) * Math.Cos(rad);
    }
    /// <summary>Where the borrow ends: the body has cleared the obstruction AND
    /// the car has come back - the return takes the same speed-dependent lane-change
    /// length as the way out.</summary>
    private static double PassOutM(Car c, RoadNetwork net)
    {
        double laneM = Math.Clamp(c.Speed * BicycleNav.LANE_CHANGE_TIME_S,
                                  BicycleNav.MERGE_LEN_MIN_M,
                                  BicycleNav.MERGE_LEN_MAX_M);
        return -((c.LengthM + c.PassLengthM) * 0.5 + SideClearM(c.Speed)) - laneM;
    }
    public static bool PassCommitted(Car c, RoadNetwork net)
    {
        if (c.PassUid < 0 || net is null) return false;
        double alongM = PassAlongM(c, net);
        // IN: the latest point from which the shift can still be completed.
        double laneInM = Math.Clamp(c.Speed * BicycleNav.LANE_CHANGE_TIME_S,
                                    BicycleNav.MERGE_LEN_MIN_M,
                                    BicycleNav.MERGE_LEN_MAX_M);
        if (alongM > laneInM) return false;      // not yet time to leave my lane
        return alongM > PassOutM(c, net);        // still inside the borrow
    }
    // Extra margin demanded when the target is STATIONARY: the plain
    // stop formula sqrt(2a*(G - StandstillGapM)) is the exact RECOVERABLE
    // boundary - a car at that speed stops exactly at the standstill gap,
    // zero margin, and the substep discretization (driver accelerates
    // before the cap runs, cap applied after) eats the remainder: T_025
    // car 10 at 21 m/s, 25 m from standing car 8, cap 0.4 m/s under speed
    // - rear-ended at 20 km/h. Stopping StopMarginM further out keeps a
    // physical clearance in the fatal band.
    const double StopMarginM = 3.0;
    // R7 + R11: the decel a driver DEMANDS for a following distance - the
    // brake used routinely, not the emergency limit. CAR_BRAKING (10 m/s²
    // ≈ 1 g) is the ABS panic-stop limit: real, but the physical maximum
    // the car can still EXECUTE, and holding it every tick is what made
    // every rear-end on the 50-car runs look "unavoidable on paper".
    // 5 m/s² ≈ 0.5 g is the service brake a driver actually holds in
    // traffic (docs/HUMAN_FACTORS.md §4 reaction + comfort).
    const double ComfortBrakingMps2 = 5.0;
    // NOTE: with ApproachCapMps in play the v1 corridor no longer adds
    // StopMarginM on top - the reaction interval IS the slack the old
    // synthetic margin patched for (both exist to fight the same zero-slack
    // boundary), and stacking them parks the follower 6.2 m behind a
    // stationary lead instead of at the ~3 m standstill gap.
    // A target slower than this is EFFECTIVELY STATIONARY for stop-cap
    // purposes (T_030: car 13 crept at ~0.3 m/s behind standing car 15;
    // the v1 margin branch (vl < 0.1) never fired, car 11 got the
    // zero-margin moving-leader formula and arrived at 12 m/s 6 m out).
    // Below this speed the target's own forward motion is too small to
    // count against the follower's stopping distance - treat it as a
    // wall and demand the full StopMarginM.
    const double StationaryCapMps = 2.0;

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
    // Minimum speed used for the TTC sweep. A sweep at the REAL speed
    // collapses to zero the moment a cap brakes the car (the driver's
    // TargetSpeed drops too), which "clears" the conflict and releases the
    // cap - the brake/creep flicker (fig8_xing: car 29 creeping into
    // standing car 5 for 15 s, 2026-09-10). With a floor, a body within
    // MinSweepSpeedMps*TtcSweepMaxS (=8 m) ahead along the sweep stays a
    // threat until it actually moves - the car stops short of it and holds
    // instead of flicker-creeping into contact.
    // The reach must stay NEAR (8 m, ~one car length + standstill gap):
    // at 25 m (5 m/s floor) every car in a dense jam has a standing car in
    // its sweep and the whole fleet deadlocks (fig8_xing T_014: 0/50
    // moving for 200 s). Beyond 8 m the graded caps of a moving sweep
    // govern - the car creeps up to the 8 m boundary and stops there.
    // NOTE: the sweep is deliberately CONSERVATIVE - each car advances at
    // max(current, plan) speed, so a car that is slow now but about to
    // cruise still reads as a cruise car. That is what the right-before-left
    // ENTRY decision needs (a slow car with the right to enter must be
    // treated as if it enters); predicting the honest acceleration ramp
    // instead made the un-signed crossing crash (measured 2026-10-19: car
    // 76 at 61 km/h hit car 83 at 10 km/h at t=31 s, because 83 no longer
    // read as an incoming car for 76). The wake-up problem of a queue is
    // fixed where it actually lives instead - see the OpeningGap test.
    const double MinSweepSpeedMps = 1.6;
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
    // How long a crossing conflict must be ABSENT before the staged alert
    // clears. The v2 conflict flickers out for single ticks when the swept
    // boxes graze at a crossing; clearing the alert on a single-tick
    // absence reset the grace clock on every re-alert and delayed the
    // self-protection brake (T_015: car 10 cleared/re-alerted on car 34
    // three times within 0.5 s, braked ~1 s after first alert - too late
    // at 65 km/h).
    const double AlertClearHoldS = 0.3;

    /// <summary>R7 + R11: the fastest approach speed that still gets the
    /// car to REST inside `roomM` of the obstacle, allowing a reaction
    /// interval first and braking at the rate a driver actually uses.
    /// v·t_react + v²/(2·a_comfort) <= roomM (+ the leader's own motion
    /// allowance vl²/(2·a_comfort)). The old curve used CAR_BRAKING (~1 g,
    /// the ABS panic-stop limit) with ZERO reaction: it is the exact
    /// recoverable boundary, so a follower could sit ON it and cross it
    /// while still accelerating - then no legal braking saves it, which is
    /// what made every rear-end on the 50-car runs "unavoidable on paper"
    /// while a human driver would simply not close at 1 g (measured: car
    /// 30 holding 1.02 g from 5.80 s, contact at 45.6 km/h). CAR_BRAKING
    /// stays the physical maximum the car can still EXECUTE (the decel the
    /// sim applies); it is no longer what the driver model demands.</summary>
    static double ApproachCapMps(double vl, double roomM)
    {
        double a = ComfortBrakingMps2, tr = ReactionTimeS;
        double rhs = roomM + vl * vl / (2.0 * a);
        if (rhs <= 0.0) return 0.0;
        return Math.Max(0.0, Math.Sqrt(a * a * tr * tr + 2.0 * a * rhs) - a * tr);
    }

    /// <summary>R26/R27 - the pair meets HEAD-ON on one carriageway (same
    /// segment, within 30 deg of opposite). Returns that segment, or null
    /// when the pair is not such a conflict.</summary>
    private static RoadSegment? HeadOnSameRoad(Car c, Car o, RoadNetwork net)
    {
        if (net is null || o.SegIdx != c.SegIdx) return null;
        var segC = net.Segments[c.SegIdx];
        double hdiff = Math.Abs(((c.Heading - o.Heading + 540.0) % 360.0) - 180.0);
        if (hdiff < 150.0) return null;          // not head-on
        return segC;
    }

    /// <summary>R27 - is this standing car NO imminent threat for `c`? True
    /// when it is at standstill AND is itself held by a reason that does not
    /// name c (every cap key ends with the other car's uid: avoid#N,
    /// sign#node#N, r14#node#N, passage#N). A car that cannot move is not
    /// going to reach the conflict point while c crosses it, so c's yield
    /// obligation toward it does not exist (docs §8 R27, the user rule of
    /// 2026-10-19: N may enter when W - the arm on its right - is itself
    /// blocked). Caps are recomputed every substep, so a chain of blocked
    /// arms unwinds over substeps - the fixed point by iteration.</summary>
    private static bool NoImminentThreat(Car o, Car c)
    {
        if (o.Speed > 0.3) return false;
        var key = o.ActiveCapKey;
        if (key is null || key.Length == 0) return false;  // free: may enter
        return !key.EndsWith("#" + c.Uid);
    }

    /// <summary>R26 - who holds the passage? The car that has progressed
    /// FURTHER along its own direction of travel inside the stretch (i.e.
    /// entered first) keeps the claim; the other concedes. Progress is
    /// measured on the shared segment's chord in each car's own travel
    /// direction, so it is deterministic (R20) and needs no per-frame
    /// bookkeeping. A tie (both entered side by side) goes to the lower uid.
    /// Returns >0 when `c` holds the passage, <0 when `o` does.</summary>
    private static int PassagePriority(Car c, Car o, RoadSegment seg)
    {
        double ax = seg.X2 - seg.X1, ay = seg.Y2 - seg.Y1;
        double al = Math.Max(Math.Hypot(ax, ay), 1e-9);
        double uC = ((c.X - seg.X1) * ax + (c.Y - seg.Y1) * ay) / al;
        double uO = ((o.X - seg.X1) * ax + (o.Y - seg.Y1) * ay) / al;
        // Progress along each car's own travel direction (0 at its own mouth).
        double pC = c.Forward ? uC : al - uC;
        double pO = o.Forward ? uO : al - uO;
        if (pC > pO + 0.5) return 1;
        if (pO > pC + 0.5) return -1;
        return c.Uid < o.Uid ? 1 : -1;           // deterministic tie-break
    }

    /// <summary>R30 (docs §8) - does the chain of standing blockers in front
    /// of `uid` bottom out? Base case: the car has NO standing blocker, so it
    /// can drive away. Otherwise every blocker must resolve itself. A cycle
    /// (uid already on the path) means nobody has a resolution - the total
    /// lockdown the user forbids - so the answer is false. Depth-bounded by
    /// the number of cars that can crowd one crossing; no memoisation, the
    /// graph is tiny and this runs per substep.</summary>
    private static bool Resolvable(int uid, Dictionary<int, List<int>> blockers,
                                   List<int> visiting, int maxDepth)
    {
        if (visiting.Contains(uid)) return false;   // cycle: no base case
        if (maxDepth <= 0) return false;            // give up: not resolvable
        var bs = blockers.TryGetValue(uid, out var l) ? l : null;
        if (bs is null || bs.Count == 0) return true;   // base case
        visiting.Add(uid);
        bool ok = true;
        foreach (var b in bs)
        {
            if (!Resolvable(b, blockers, visiting, maxDepth - 1)) { ok = false; break; }
        }
        visiting.Remove(uid);
        return ok;
    }

    /// <summary>R29 (docs §8) - walk the blocked-by chain from `uid` and
    /// return the CYCLE it sits on, but only when every member of that cycle
    /// is at standstill (a total lockdown). Null when there is no cycle or
    /// when any member is still moving (it can get out of the way).</summary>
    private static List<int>? FindLockdownCycle(int uid,
                                                Dictionary<int, List<int>> blockers,
                                                Dictionary<int, Car> carByUid)
    {
        var path = new List<int>();
        var cur = uid;
        while (true)
        {
            var at = path.IndexOf(cur);
            if (at >= 0)
            {
                var cyc = new List<int>();
                for (var i = at; i < path.Count; i++) cyc.Add(path[i]);
                foreach (var m in cyc)
                {
                    var car = carByUid.GetValueOrDefault(m);
                    if (car is null || car.Speed > 0.3) return null;   // can move
                }
                return cyc;
            }
            if (path.Count >= 16) return null;
            path.Add(cur);
            var bs = blockers.TryGetValue(cur, out var l) ? l : null;
            if (bs is null || bs.Count == 0) return null;   // no cycle here
            cur = bs[0];
        }
    }

    /// <summary>R29 - cars the sim asked to be removed (an ERROR path, see
    /// docs §8 R29 and tests/test_turning.py). Cleared on every
    /// ComputeAvoidCaps call; SimEngine consumes it and despawns.</summary>
    public static List<int> DespawnRequests = new();
    /// <summary>R28 - total lockdowns the sim had to REPORT (an ERROR path:
    /// prevention must keep this empty). Served in /state as 'lockdowns' +
    /// 'lockdown_last'; the e2e suite fails when the count grows.</summary>
    public static List<string> LockdownEvents = new();
    private static Dictionary<string, int> _lockdownStood = new();
    private static Dictionary<string, int> _lockdownRun = new();
    // How long a standstill cycle must persist before the valve opens (substeps
    // at 1/60 s). Prevention should keep this unreachable; a one-frame flicker
    // must never delete a car.
    private const int LockdownValveTicks = 360;   // 6.0 s of standstill
    private const int LockdownReportTicks = 180;  // 3.0 s -> the R28 event

    sealed class Grid
    {
        readonly Dictionary<long, List<Car>> _cells = new();

        static long Cell(double v) => (long)Math.Floor(v / CellSizeM);
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

    static ObstacleGeometry.BodyBox Body(Car c, int level) =>
        new(ObstacleGeometry.PlayerBodyCorners(c), level);

    // Shrunken body for the RESOLVE (rollback) decision: BoxesIntersect uses
    // strict inequalities, so two boxes merely TOUCHING (resting nose-to-nose)
    // read as "intersecting". Without this tolerance a pair that came into
    // contact once is rolled back + stopped EVERY tick forever - the driver
    // pushes forward, the boxes touch again, rollback, repeat: a permanent
    // collision lock that gridlocked the whole fig8_cross fleet (measured:
    // uids 11 x 38 pinned at contact for 140+ s). Real penetration (>= 5 cm
    // total) still triggers the rollback; mere contact does not.
    const double ResolvePenetrationEpsM = 0.025;
    static ObstacleGeometry.BodyBox BodyForResolve(Car c, int level)
    {
        var (bx, by) = c.BodyCenter();
        return new ObstacleGeometry.BodyBox(
            ObstacleGeometry.BoxCorners(bx, by, c.Heading,
                Math.Max(0.1, c.LengthM - 2 * ResolvePenetrationEpsM),
                Math.Max(0.1, c.WidthM - 2 * ResolvePenetrationEpsM)), level);
    }

    // TEMPORARY debug hook (remove after use)
    public static List<string> DebugLog = new();

    // Decision-log debounce: pending threat replacements per car uid and how
    // many CONSECUTIVE substeps they have won the cap (see the final pass in
    // ComputeAvoidCaps). Grows/shrinks with the car population; a cleared car
    // just leaves a small stale entry (uids are monotonic, never reused).
    static readonly Dictionary<int, (string Key, int Ticks)> _capCandidate = new();
    const int PERSIST_TICKS = 15;   // 0.25 s of sim time at 60 Hz

    // Cap DECISION persistence (docs/HUMAN_FACTORS.md §3/§5): while a
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
    // Minimum speed for a car that is ALREADY inside the crossing box (RBL
    // yielder): it must finish the crossing, never stop in the middle of it
    // (a stop there blocks the other car's path - see the v2 RBL logic).
    const double RblCompleteCrossingMps = 4.0;
    // How far out the gap acceptance scans for APPROACHING cars on the
    // crossing diagonal. Must cover t_clear+margin at max speed: a car at
    // 100 m needs >= 6 s to arrive, longer than any t_clear (~4 s) +
    // margin (1.5 s), so a gap open at commit time stays open.
    const double GapScanRangeM = 100.0;
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
        DespawnRequests.Clear();
        var grid = Grid.Build(carList);
        var caps = new Dictionary<int, double>();
        // WHY each car is capped: (cap, stable key, human text, the car that
        // owns the situation). The key identifies the situation (rule + other
        // car), so a continuously re-evaluated cap logs ONE event, and a
        // changing threat (car 26 -> car 28) logs a new one. `By` is the SAME
        // uid the rule just used to build that key, carried as a value instead
        // of being parsed back out of the string - a client that pattern-matches
        // `EndsWith(uid)` gets `r32#passive` and `sign#C#7` wrong (the reviewer
        // did until docs/CAR_DETAIL_WINDOW.md retired it).
        var reasons = new Dictionary<int, (double Cap, string Key, string Text, int By)>();
        void OfferCap(int uid, double cap, string key, string text, int by = 0)
        {
            if (reasons.TryGetValue(uid, out var r) && r.Cap <= cap) return;
            reasons[uid] = (cap, key, text, by);
        }
        // Broadphase ring covers the LARGEST per-car look-ahead (top speed);
        // the per-car filter below narrows it to each car's own range.
        int ring = (int)Math.Ceiling(LookAheadMaxM / CellSizeM) + 1;

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
            double dToNodeM = Math.Hypot(c.X - nxy.X, c.Y - nxy.Y);
            if (dToNodeM > SignActRangeM) return false;
            if (net.Signs.TryGetValue((c.SegIdx, aheadNode), out var s) &&
                s == SignType.Yield)
                return false;   // I'm on the yield arm, not the priority arm
            return true;
        }
        // (c) A YIELD car still BEFORE its stop line (not yet committed) is
        // YIELDING, not a threat to a priority car: the stop line is set so
        // a stopped yield car's body clears the priority lane
        // (YieldStopNoseGapM). Self-protecting for it is what parked
        // priority car 5 in its own crossing box and blocked the very yield
        // car it watched (fig8_xing T_013, 2026-09-10). Once the other car
        // commits (nose past the line) it is a real threat again.
        bool IsYieldingBeforeLine(Car o, RoadNetwork n, HashSet<string> signed)
        {
            var seg = n.Segments[o.SegIdx];
            string ahead = o.Forward ? seg.EndNode : seg.StartNode;
            if (!signed.Contains(ahead)) return false;
            if (!n.Signs.TryGetValue((o.SegIdx, ahead), out var sg) ||
                sg != SignType.Yield)
                return false;
            if (o.CrossingCommittedNode == ahead) return false;
            if (!n.Nodes.TryGetValue(ahead, out var nxy)) return false;
            double stopGapM = o.LengthM * 0.5 +
                             Config.REAR_AXLE_OFFSET_M + YieldStopNoseGapM;
            return Math.Hypot(o.X - nxy.X, o.Y - nxy.Y) > stopGapM;
        }
        // R3's other half: has this car already ENTERED the crossing it is
        // heading for (nose past its stop line)? R3 says a car once in the
        // crossing keeps going - "a car already blocking the box must keep
        // going, else it deadlocks the cars it blocks". Without this test the
        // "complete the crossing" floor below only applied to the last car
        // length of the traversal, so a committed yield car was still free to
        // stop in the MIDDLE of it (fig8_xing: car 32 committed at t=0.467,
        // GradedCap's stageS<1 s cliff capped it to 0 at t~0.9, it parked
        // 5.13 m past C for 1.5 s blocking the whole priority lane, and car
        // 8 hit it at 31 km/h).
        bool EnteredCrossing(Car y)
        {
            if (net is null) return false;
            var yseg = net.Segments[y.SegIdx];
            string yAhead = y.Forward ? yseg.EndNode : yseg.StartNode;
            if (y.CrossingCommittedNode == yAhead) return true;
            if (net.NodeDegree.GetValueOrDefault(yAhead, 0) < 4) return false;
            if (!net.Nodes.TryGetValue(yAhead, out var yxy)) return false;
            double yStopM = y.LengthM * 0.5 + Config.REAR_AXLE_OFFSET_M + YieldStopNoseGapM;
            return Math.Hypot(y.X - yxy.X, y.Y - yxy.Y) <= yStopM;
        }
        // Most-immediate crossing conflict per PRIORITY car this tick (uid ->
        // the car it is watching, the TTC, and its own distance to the meet
        // point). Filled by the v2 pair loop, consumed after it.
        var pConflicts = new Dictionary<int, (int Other, double Ttc, double DistP)>();
        // Most-immediate v2 conflict for EVERY car (priority or not): the
        // commit check (R3b) needs "is there a stationary body in my path"
        // regardless of right-of-way.
        var immediateConflict = new Dictionary<int, (int Other, double Ttc, bool OtherStationary)>();

        // --- route-true forward corridor (the curved-arm fix) -------------
        // (see the note above BuildRoadAhead). Cached per car every
        // RouteRebuildS of sim time - the walk itself is O(look-ahead), not
        // O(1), so rebuilding it every substep at 50 cars starved the sim.
        var routeAhead = new Dictionary<int, (double S, double X, double Y)[]>();
        var routeOff = new Dictionary<int, double>();   // my own offset from the axis
        if (net is not null)
            foreach (var c in carList)
            {
                double builtAt = _roadPathAt.ContainsKey(c.Uid) ? _roadPathAt[c.Uid] : -1e9;
                bool fresh = _roadPath.ContainsKey(c.Uid) &&
                             simTime >= builtAt && simTime - builtAt < RouteRebuildS;
                if (fresh)
                {
                    routeAhead[c.Uid] = _roadPath[c.Uid];
                    routeOff[c.Uid] = _roadOff[c.Uid];
                    continue;
                }
                var pts = BuildRoadAhead(c, net, Math.Min(LookAheadForSpeed(c.Speed), RoadPathMaxM));
                if (pts is null || pts.Length < 2) continue;
                routeAhead[c.Uid] = pts;
                _roadPath[c.Uid] = pts;
                _roadPathAt[c.Uid] = simTime;
                _roadOff[c.Uid] = RoutePointOff(pts, c.X, c.Y).Off;
                routeOff[c.Uid] = _roadOff[c.Uid];
            }

        // --- the auction's inputs (docs/RIGHT_OF_WAY_NEGOTIATION.md §4) ---
        // One pass over the fleet per substep, both maps O(cars): what each
        // car has already paid in standing time (R16), and how many cars
        // name it as their blocker (the blocked-by graph R30 walks, read
        // from last substep's cap keys - a cost input, not a safety check,
        // so a one-substep lag is honest).
        var waitedS = new Dictionary<long, double>(carList.Count);
        var blocking = new Dictionary<long, int>(carList.Count);
        foreach (var cc in carList) waitedS[cc.Uid] = cc.StandstillS;
        foreach (var cc in carList)
        {
            var k = cc.ActiveCapKey ?? "";
            var hash = k.LastIndexOf('#');
            if (hash < 0) continue;
            var byUid = long.TryParse(k.Substring(hash + 1), out var bv) ? bv : 0L;
            if (byUid > 0) blocking[byUid] = blocking.GetValueOrDefault(byUid, 0) + 1;
        }
        var liveKeys = new HashSet<int>();

        foreach (var c in carList)
        {
            double rad = Math.Radians(c.Heading);
            double fx = Math.Sin(rad), fy = Math.Cos(rad);     // forward
            double rx = Math.Cos(rad), ry = -Math.Sin(rad);    // right
            double best = double.PositiveInfinity;
            int _v1By = -1; double _v1Along = 0, _v1Lat = 0, _v1Gap = 0;   // TEMPORARY
            string _v1Rule = "R7";   // which rule owns the binding corridor cap
            bool passC1 = PassCommitted(c, net);   // mid-pass: the named car is not my lead
            // This car's own range: fast cars see (and must react to) the
            // leader much further out so they can still stop in time.
            double lookAheadM = LookAheadForSpeed(c.Speed);
            int lvlC = net is null ? 0 : net.Segments[c.SegIdx].Level;

            foreach (var o in grid.Near(c.X, c.Y, ring))
            {
                if (o.Uid == c.Uid) continue;
                int lvlO = net is null ? 0 : net.Segments[o.SegIdx].Level;
                // Staged PRIORITY response: while this car is WATCHING car o
                // (alert set by last tick's v2), the staged logic below is the
                // single brake authority for o (it now includes a gap-based
                // term - see the self-protection branch below). Skipping v1
                // here keeps one cap source per situation (the flutter fix);
                // once the alert clears, v1 resumes in full.
                if (c.AlertOtherUid == o.Uid) continue;
                double dxc = o.X - c.X, dyc = o.Y - c.Y;
                double rayAlong = dxc * fx + dyc * fy;      // ahead at all?
                if (rayAlong < 0.0) continue;               // beside/behind me
                // The route arc ahead of me is never SHORTER than the
                // projection onto my heading, so a leader whose projection
                // already exceeds my look-ahead can be dropped before the
                // (more expensive) road walk - this filter is what kept the
                // per-pair scan affordable.
                if (rayAlong > lookAheadM) continue;
                double rayLat = dxc * rx + dyc * ry;
                // A car I am COMMITTED to pass is not my lead - but only once
                // I am actually BESIDE it. While my body still sits behind its
                // own, the pass has not started and the following-distance rule
                // must hold (measured: exempting the pair outright let a
                // follower drive up to a stationary lead in CollisionTests,
                // because R32 calls any stopped car ahead an obstruction).
                // Slow down, move over, then go: that is what a driver does.
                if (passC1 && o.Uid == c.PassUid)
                {
                    if (Math.Abs(rayLat) > (c.WidthM + o.WidthM) * 0.5)
                        continue;   // beside: the pass has started, not my lead
                    // Not beside yet: the pass is COMMITTED but has not
                    // started, so the pair is still single-file - and a
                    // standstill here can never start a pass (a stopped car
                    // cannot become "beside" one it is stopped behind). The
                    // missing half of the clearance relation is the cap side:
                    // clearance = MinPass + v*PerMps inverted, so the speed
                    // at which the pair CAN pass with the room available is
                    // the speed I may creep to. Same constants as the demand
                    // side - demand and feasibility can never contradict.
                    var segP = (o.SegIdx >= 0 && o.SegIdx < net.Segments.Count)
                        ? net.Segments[o.SegIdx] : null;
                    if (segP != null && segP.Width > 0.0 && !segP.Oneway)
                    {
                        double axP = segP.X2 - segP.X1, ayP = segP.Y2 - segP.Y1;
                        double lenP = Math.Sqrt(axP * axP + ayP * ayP);
                        if (lenP > 1e-9)
                        {
                            double txP = axP / lenP, tyP = ayP / lenP;
                            // The room a borrow can use is the carriageway's
                            // KERB-TO-KERB travel, not what happens to be left
                            // at the car's current lateral position: a car that
                            // already drifted toward the oncoming lane still has
                            // the whole pass available to it, it just has to
                            // come off the kerb it is hugging (measured: the
                            // primary of park_block_priority held at 0 km/h at
                            // x 99.14 with 0.4 m read as "room left", while the
                            // passing runs squeeze past at x 101.0).
                            double roomP = segP.Width - c.WidthM;
                            if (roomP > MinPassClearanceM + 0.01)
                            {
                                double vPass = (roomP - MinPassClearanceM) / SideClearPerMpsM;
                                if (vPass < 1.0) vPass = 1.0;   // a creep, never a hold
                                if (vPass < best)
                                {
                                    best = vPass;
                                    _v1Rule = "R32 creep";
                                    // The car that makes the stretch tight IS
                                    // known here - naming it costs nothing and
                                    // keeps `avoid#-1` (the old sentinel: a cap
                                    // whose holder was nobody) out of the log,
                                    // the tape and the held-by chain.
                                    _v1By = o.Uid;
                                    c.ActiveCapKey = "r32#passive";
                                    // The KERB holds this car, not a driver:
                                    // the creep cap has no actor, and a chain
                                    // that named one would be inventing it.
                                    c.HeldByUid = 0;
                                }
                            }
                        }
                    }
                }
                // Cheap path first: on a straight road the heading ray IS the
                // lane line, so the band test settles without walking the
                // network. The band is the pair's BODY SUM, not the sedan
                // rule - a 3 m truck beside a 3 m truck needs 3 m each side
                // of my axis, and the old 1.8 m ray band dropped that pair
                // before the real test ever ran (measured: a mixer braked
                // panic-late into the rear of a stopped tractor 2.9 m to its
                // side, which the corridor never saw).
                double along, lat;
                double latBandM = (c.WidthM + o.WidthM) * 0.5;
                if (Math.Abs(rayLat) <= latBandM && rayAlong <= lookAheadM)
                {
                    along = rayAlong;
                    lat = rayLat;
                }
                else if (routeAhead.TryGetValue(c.Uid, out var line))
                {
                    var (sLead, offLead) = RoutePointOff(line, o.X, o.Y, rayAlong);
                    along = sLead;
                    lat = offLead - routeOff[c.Uid];
                }
                else { continue; }
                if (along < 0 || along > lookAheadM) continue;
                if (Math.Abs(lat) > latBandM)
                    continue;

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
                // 3D: the corridor and the body must share a level - a deck
                // car's corridor ignores ground traffic under the bridge.
                if (hdiff >= 60.0 &&
                    !ObstacleGeometry.BoxesIntersect(
                        ForwardCorridorBox(c, lvlC), Body(o, lvlO)))
                    continue;

                // Bumper gap in metres: centre distance minus HALF of each
                // car's REAL length (per-class footprints - a truck pair must
                // register contact at true bumper touch, not sedan-box math;
                // the old single CAR_LENGTH_M let mixed-length pairs close to
                // physical overlap before "touching" triggered).
                double gapM = along - (c.LengthM + o.LengthM) * 0.5;

                // --- R26/R27: the head-on pair on one carriageway ---------
                // Ask "can they pass?" BEFORE "are they touching?": an
                // anti-parallel pair that fits both bodies plus the clearance
                // is not a corridor threat at all - both go, each hugging its
                // own right kerb (StVO §4, pass each other on the left).
                // Capping such a pair at 0 is what wedged a mixer and a
                // tractor on one 7 m arm of fig8_xing_plain (2026-10-19).
                // When the stretch CANNOT fit both, it is single-file for
                // that pair and the claim decides: whoever entered further
                // (PassagePriority) holds the passage and is not capped by
                // this pair; the other waits with the R26 reason (R19 retreat
                // would be the next step once it is already inside).
                var hoSeg = HeadOnSameRoad(c, o, net);
                if (hoSeg is not null)
                {
                    if (hoSeg.Width >= c.WidthM + o.WidthM + MinPassClearanceM)
                        continue;   // they can pass side by side
                    // The stretch is a RESOURCE, the pair are its candidates,
                    // and the award decides - not this pair's opinion of
                    // which of two cars entered first. Grouping matters:
                    // three cars on one pinch would otherwise argue as three
                    // pairwise claims that can each be right and jointly
                    // impossible (docs §3).
                    double axH = hoSeg.X2 - hoSeg.X1, ayH = hoSeg.Y2 - hoSeg.Y1;
                    double alH = Math.Max(Math.Hypot(axH, ayH), 1e-9);
                    double uCH = ((c.X - hoSeg.X1) * axH + (c.Y - hoSeg.Y1) * ayH) / alH;
                    double uOH = ((o.X - hoSeg.X1) * axH + (o.Y - hoSeg.Y1) * ayH) / alH;
                    var resH = new Negotiation.Res(c.SegIdx,
                                                   Math.Min(uCH, uOH) - 6.0,
                                                   Math.Max(uCH, uOH) + 6.0);
                    liveKeys.Add(resH.Key);
                    var candsH = new List<Car> { c, o };
                    if (Negotiation.Holds(resH.Key, c.Uid, candsH, waitedS,
                                          blocking, (resH.B - resH.A) / 2,
                                          Math.Max(c.Speed, 2.0)))
                        continue;   // the auction says I hold the stretch
                    best = 0.0; _v1By = o.Uid; _v1Along = along;
                    _v1Lat = lat; _v1Gap = gapM;
                    _v1Rule = "R26";
                    // bitteschön (docs §6): the flash belongs to the car that
                    // CONCEDES - the one WITHOUT the right - and is aimed at
                    // the car whose passage it grants. Not the winner's.
                    c.CourtesyForUid = o.Uid;
                    c.CourtesyKind = "passage";
                    break;
                }

                if (gapM <= 0.25)
                {
                    // Touching -> stop. Name the blocker BEFORE breaking:
                    // leaving _v1By at its -1 sentinel logged every standstill
                    // as "[R7] car -1 ahead (gap 0.0 m)", which made a 35 s
                    // hold look unexplainable (R20: a logged rule must name
                    // its trigger).
                    best = 0.0; _v1By = o.Uid; _v1Along = along;
                    _v1Lat = lat; _v1Gap = gapM;
                    break;
                }

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
                // R7 + R11 (see ApproachCapMps): demand the stop behind the
                // leader's FINAL rest position, after a reaction interval,
                // braking at the comfort rate - not the 1 g panic limit from
                // the leader's CURRENT position. A leader that is BRAKING is
                // already becoming that wall (its brake lights are on), so from
                // the moment it brakes its remaining stop distance vl²/(2·a) -
                // with THE LEADER'S OWN capability a (#21: a laden mixer needs more
                // road than a Golf, and crediting both the follower's 10 m/s²
                // promises room that is not there) - counts as room, speed as 0.
                // Measured, fig8_xing: car 30 at 96 km/h behind car 28
                // braking from 57 km/h, 29.4 m of bumper gap when the old
                // curve first bound - max braking from there needs 35 m,
                // contact at 45.6 km/h into the rested body.
                bool leadStopping = o.IsBraking && vl < c.Speed;
                double roomM = gapM - StandstillGapM;
                if (leadStopping)
                    roomM += vl * vl / (2.0 * o.BrakeMps2);
                double cap = ApproachCapMps(leadStopping ? 0.0 : vl, roomM);
                if (cap < best) { best = cap; _v1By = o.Uid;
                    _v1Along = along; _v1Lat = lat;
                    _v1Gap = gapM; }   // TEMPORARY
                if (o.Speed < 0.1 && c.Speed >= 5.0 && gapM <= 60.0)   // TEMPORARY
                    Console.WriteLine($"DBGV1 t={simTime:F2} uid{c.Uid} lead{o.Uid} gap={gapM:F1} cap={cap:F2} best={best:F2}");
            }

            if (best < double.PositiveInfinity)
            {
                OfferCap(c.Uid, best,
                    _v1Rule == "R26" ? $"passage#{_v1By}" : $"avoid#{_v1By}",
                    _v1Rule == "R26"
                        ? $"[R26] single-file passage: car {_v1By} holds it - waiting" +
                          $" (stretch {c.SegIdx}, my room {c.WidthM:F2} m)"
                        : $"[R7] car {_v1By} ahead (gap {_v1Gap:F1} m)",
                    _v1By < 0 ? 0 : _v1By);
                DebugLog.Add($"v1 uid{c.Uid}={best:F2} along={_v1Along:F1} lat={_v1Lat:F2} by {_v1By}");   // TEMPORARY
            }
        }

        // --- R8 (partial): yield right against a frontal conflict ---------
        // User rule: two cars on a head-on line do not solve it by braking
        // alone - each pulls right as far as the road allows, never leaving
        // it. Only a pair that would actually touch laterally gets a shift,
        // and only by the room its own kerb side has left, so on a 7 m arm
        // the two keep their halves and pass. The nav clamps the request to
        // the kerb line of the narrowest segment on the route again, so the
        // road can never be left from either side.
        const double FrontalClearM = 0.4;      // body clearance asked for
        const double YieldDecayMps = 0.005;    // m per substep back to the line
        foreach (var c in carList)
        {
            double wantM = 0.0;
            var segC = net is null ? null : net.Segments[c.SegIdx];
            double radC = Math.Radians(c.Heading);
            double fxC = Math.Sin(radC), fyC = Math.Cos(radC);
            double rxC = Math.Cos(radC), ryC = -Math.Sin(radC);
            foreach (var o in grid.Near(c.X, c.Y, (int)Math.Ceiling(60.0 / CellSizeM) + 1))
            {
                if (o.Uid == c.Uid) continue;
                // hdiff ~180 deg = head-on (the same convention v1 uses).
                double hdiff = Math.Abs(((c.Heading - o.Heading + 540.0) % 360.0) - 180.0);
                if (hdiff < 150.0) continue;
                double dxc = o.X - c.X, dyc = o.Y - c.Y;
                double along = dxc * fxC + dyc * fyC;
                if (along < 0.0 || along > 60.0) continue;   // only a real threat ahead
                double lat = dxc * rxC + dyc * ryC;
                // A right shift only helps against a threat on my LEFT (or
                // dead ahead): pulling right away from a body that already
                // sits on my right would CLOSE the gap - that case is left
                // to the speed rules (R2/RBL), never to the wheel.
                if (lat > 0.0) continue;
                double touchM = (c.WidthM + o.WidthM) * 0.5;
                if (Math.Abs(lat) >= touchM + FrontalClearM) continue;   // already clear
                wantM = Math.Max(wantM, touchM + FrontalClearM - Math.Abs(lat));
            }
            if (wantM <= 0.0)
            {
                // No threat: unwind slowly, not a snap back onto the line.
                c.YieldRightM = Math.Max(0.0, c.YieldRightM - YieldDecayMps);
                continue;
            }
            if (segC is null) continue;
            // Room to the RIGHT of this car inside its own road.
            double ax = segC.X2 - segC.X1, ay = segC.Y2 - segC.Y1;
            double al = Math.Max(Math.Hypot(ax, ay), 1e-9);
            double sgn = c.Forward ? 1.0 : -1.0;
            double tx = ax / al * sgn, ty = ay / al * sgn;
            double offM = (c.X - segC.X1) * ty + (c.Y - segC.Y1) * -tx;
            double roomM = segC.Width / 2.0 - c.WidthM / 2.0 - offM;
            c.YieldRightM = Math.Min(wantM, Math.Max(0.0, roomM));
        }

        // --- R32: pass an obstruction in my lane (docs §8) ---------------
        // A car STOPPED (parked) or much slower inside my own lane blocks the
        // lane, and on a two-lane road the only way past is the OTHER lane.
        // The rule in the driver's own words: never enter the shared lane
        // unless it is clear for the whole pass, and never take it against a
        // car that is already coming through the pinch from the other side -
        // the stretch around the obstruction is a single-file resource, so
        // the claim (R26) decides who may enter and the other waits behind.
        const double OvertakeScanM = 60.0;   // how far ahead a car looks
        foreach (var c in carList)
        {
            if (net is null) continue;
            var segC32 = net.Segments[c.SegIdx];
            if (segC32.Oneway) continue;      // no oncoming lane to borrow
            double radC32 = Math.Radians(c.Heading);
            double fxC32 = Math.Sin(radC32), fyC32 = Math.Cos(radC32);
            double rxC32 = Math.Cos(radC32), ryC32 = -Math.Sin(radC32);
            double wantM = 0.0;
            bool blocked = false;
            int byUid32 = -1;
            int passUid32 = -1; double passProg32 = -1.0; double passLen32 = 0.0;
            double passX32 = 0.0, passY32 = 0.0;
            foreach (var o in grid.Near(c.X, c.Y, (int)Math.Ceiling(OvertakeScanM / CellSizeM) + 1))
            {
                if (o.Uid == c.Uid) continue;
                if (o.SegIdx != c.SegIdx &&
                    !(c.SegIdx >= 0 && c.SegIdx < net.SegmentNeighbours.Count &&
                      net.SegmentNeighbours[c.SegIdx].Contains(o.SegIdx)))
                    continue;   // same carriageway (a pass may straddle a node)
                double dxc = o.X - c.X, dyc = o.Y - c.Y;
                double along = dxc * fxC32 + dyc * fyC32;
                if (along <= 0.5 || along > OvertakeScanM)
                    continue;
                double lat = dxc * rxC32 + dyc * ryC32;
                double touchM = (c.WidthM + o.WidthM) * 0.5;
                if (Math.Abs(lat) > touchM + SideClearM(c.Speed))
                    continue;
                double hdiff = Math.Abs(((c.Heading - o.Heading + 540.0) % 360.0) - 180.0);
                bool sameDir = hdiff < 45.0;
                if (!sameDir)
                    continue;
                // An obstruction: stopped, or crawling far below my speed.
                if (o.Speed > 0.5 && o.Speed + 4.0 > Math.Max(c.Speed, 8.0))
                    continue;
                // A car that is STOPPED but NOT parked is a queue lead, not an
                // obstruction: it has a route and may pull away at any moment,
                // so borrowing the other lane past it is not a safe commitment -
                // v1's following rule owns that pair. (Measured: committing to
                // it let a follower overtake a stationary lead in
                // CollisionTests.StationaryLeadCar_FollowerBrakesAndStopsBehind.)
                // Crawling traffic (Speed > 0.5, the line above) is still fair
                // game - a tractor, a bus that cannot turn.
                if (o.Speed <= 0.5 && o.BicycleNav?.Parked != true)
                    continue;
                // How far LEFT must I sit to clear it?
                // How far LEFT must I sit to clear it? The margin is the speed
                // curve, so the demand and the pass speed below speak the same
                // relation - two constants set to the same value (demand 0.5,
                // veto 0.5) can never both hold when the delivery is off by
                // centimetres, which is what pinned the car at 0.46 m.
                double needM = touchM + SideClearM(c.Speed) - Math.Abs(lat);
                if (needM > wantM)
                {
                    wantM = needM; passUid32 = o.Uid; passProg32 = o.Progress;
                    passLen32 = o.LengthM; passX32 = o.X; passY32 = o.Y;
                }
                // The pinch: the stretch from here to just beyond the
                // obstruction. Anything already coming through it from the
                // other side owns it - the claim decides (R26).
                foreach (var o2 in grid.Near(c.X, c.Y, (int)Math.Ceiling(OvertakeScanM / CellSizeM) + 1))
                {
                    if (o2.Uid == c.Uid || o2.Uid == o.Uid) continue;
                    if (o2.SegIdx != c.SegIdx) continue;
                    double h2 = Math.Abs(((c.Heading - o2.Heading + 540.0) % 360.0) - 180.0);
                    if (h2 < 150.0) continue;         // not oncoming
                    double dx2 = o2.X - c.X, dy2 = o2.Y - c.Y;
                    double along2 = dx2 * fxC32 + dy2 * fyC32;
                    // In the pinch zone (between me and past the obstruction)?
                    // The zone reaches as far as an inbound driver can no longer
                    // avoid entering it: its own look-ahead (reaction + braking
                    // distance, LookAheadForSpeed) - not a fixed 8 m, which let
                    // a car commit to the pass while an oncoming car was already
                    // committed to the same lane (measured: the primary and the
                    // oncoming car of park_block_priority contacted head-on at
                    // t=11.47 s, both inside the shared lane).
                    if (along2 < -6.0 ||
                        along2 > along + c.LengthM + LookAheadForSpeed(o2.Speed)) continue;
                    double lat2 = dx2 * rxC32 + dy2 * ryC32;
                    // Only a car that would actually use the lane I need.
                    if (lat2 < -touchM - SideClearM(c.Speed)) continue;
                    // The claim: a driver that has to borrow the shared lane
                    // (that is what wantM means for me) never owns it against a
                    // driver who can stay in their own. Whoever entered further
                    // only decides between two drivers that BOTH need it (R26).
                    if (o2.OvertakeShiftM <= 0.05)
                    {
                        blocked = true; byUid32 = o2.Uid;
                        break;
                    }
                    // Same resource, same auction: the borrow window from
                    // here to past the obstruction is a single-file stretch
                    // that both I and the oncoming car want.
                    double axP = segC32.X2 - segC32.X1, ayP = segC32.Y2 - segC32.Y1;
                    double alP = Math.Max(Math.Hypot(axP, ayP), 1e-9);
                    double uCP = ((c.X - segC32.X1) * axP + (c.Y - segC32.Y1) * ayP) / alP;
                    double uOP = ((o2.X - segC32.X1) * axP + (o2.Y - segC32.Y1) * ayP) / alP;
                    var resP = new Negotiation.Res(c.SegIdx,
                                                   Math.Min(uCP, uOP) - 4.0,
                                                   Math.Max(uCP, uOP) + 4.0);
                    liveKeys.Add(resP.Key);
                    var candsP = new List<Car> { c, o2 };
                    if (Negotiation.Holds(resP.Key, c.Uid, candsP, waitedS,
                                          blocking, (resP.B - resP.A) / 2,
                                          Math.Max(c.Speed, 2.0)))
                        continue;  // the auction says I hold the window
                    blocked = true; byUid32 = o2.Uid;
                    break;
                }
                if (blocked) break;
            }
            // --- the commitment (R32) ------------------------------------
            // wantM exists only while the obstruction is still in my line; the
            // PASS is not over at that moment, so the intent is held by an
            // object that names WHICH car I am passing and stays alive while
            // that one is ahead of me. Read by the corridor bound, by v1 and
            // by R33 - one object, three consumers.
            if (wantM > 0.0 && !blocked && passUid32 >= 0)
            {
                c.PassUid = passUid32;
                c.PassSegIdx = c.SegIdx;
                c.PassProgress = passProg32;
                c.PassLengthM = passLen32;
                c.PassX = passX32; c.PassY = passY32;
                // A commitment never shrinks mid-pass: the demand is a floor,
                // not a value that jitters as the geometry changes.
                c.PassShiftM = Math.Max(c.PassShiftM, wantM);
            }
            else if (c.PassUid >= 0 && !PassPending(c, net))
            {
                c.PassUid = -1; c.PassSegIdx = -1; c.PassProgress = -1.0;
                c.PassLengthM = 0.0;
                c.PassShiftM = 0.0;
            }
            if (blocked)
            {
                // The shared lane belongs to the other car: wait behind the
                // obstruction until it has come through (R26's claim).
                c.PassUid = -1; c.PassSegIdx = -1; c.PassProgress = -1.0;
                c.PassLengthM = 0.0;
                c.PassShiftM = 0.0;
                c.OvertakeShiftM = 0.0;
                OfferCap(c.Uid, 0.0, $"r32#{byUid32}",
                         $"[R32] lane blocked by car {byUid32} ahead - " +
                         "waiting behind the obstruction", byUid32);
                continue;
            }
            // Outside the window the car belongs to its own lane: the shift is
            // RELEASED, not decayed. The nav re-solves the line when the pass
            // phase flips, and a decay that has barely started would leave that
            // solve reading the OLD shift - measured: the car parked itself on
            // the centreline (offR +0.01) for 90 m after the pass and the guard
            // called it wrong-side, because the line it followed was still
            // built from a 2.2 m borrow that never got released.
            if (!PassCommitted(c, net))
            {
                c.OvertakeShiftM = 0.0;
                continue;
            }
            if (wantM <= 0.0) wantM = c.PassShiftM;   // held inside the window
            // Room to the LEFT inside the same carriageway: the shift may
            // never put the car off the road (the nav clamps again against
            // the narrowest segment on the route, this is the local bound).
            double ax32 = segC32.X2 - segC32.X1, ay32 = segC32.Y2 - segC32.Y1;
            double al32 = Math.Max(Math.Hypot(ax32, ay32), 1e-9);
            double sgn32 = c.Forward ? 1.0 : -1.0;
            double tx32 = ax32 / al32 * sgn32, ty32 = ay32 / al32 * sgn32;
            double off32 = (c.X - segC32.X1) * ty32 + (c.Y - segC32.Y1) * -tx32;
            double roomLeftM = off32 + segC32.Width / 2.0 - c.WidthM / 2.0;
            // The bound is the carriageway's KERB-TO-KERB travel, not the room
            // that happens to be left at the car's CURRENT lateral position.
            // Measured on park_block_priority: a primary that had already
            // drifted 0.8 m left of the centreline read roomLeftM as 0.5 m,
            // so the 2.7 m the pass needs never got asked for, R32 stayed
            // silent and v1's following rule pinned the car at 0 km/h behind
            // a car parked ON the centreline (8.8 m of body on a 7 m road -
            // the pass only fits by hugging the far kerb, which is exactly
            // what the passing runs do at x 101.0). The nav still clamps the
            // line against the road edge, so the shift cannot leave the road.
            double kerbRoomM = segC32.Width - c.WidthM;
            wantM = Math.Min(wantM, Math.Max(0.0, Math.Max(roomLeftM, kerbRoomM)));
            if (blocked)
            {
                // The shared lane belongs to the other car: wait behind the
                // obstruction until it has come through (R26's claim).
                c.PassUid = -1; c.PassSegIdx = -1; c.PassProgress = -1.0;
                c.PassShiftM = 0.0;
                c.OvertakeShiftM = 0.0;
                OfferCap(c.Uid, 0.0, $"r32#{byUid32}",
                         $"[R32] lane blocked by car {byUid32} ahead - " +
                         "waiting behind the obstruction", byUid32);
                continue;
            }
            double prevShift32 = c.OvertakeShiftM;
            c.OvertakeShiftM = wantM;
            if (wantM > prevShift32 + 0.05)
                c.RecordDecision(simTime,
                    $"[R32] obstruction ahead - borrowing the free lane " +
                    $"({wantM:F2} m left)");
        }

        // An award survives only as long as a contest is about the stretch.
        Negotiation.Keep(liveKeys);

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
        // #36: hand the car that is tucking into a bay the list of bodies beside it.
        // Scoped to that case deliberately — at 50 cars a per-tick neighbour map for
        // everyone is exactly the cost this file has already paid once — and rebuilt
        // from the grid it already has, so the parking envelope never invents its own
        // spatial query to disagree with the one that decides contacts.
        foreach (var c in carList)
        {
            // Filled for every car with a parking job, not only a running tuck: the
            // planner must see the neighbours BEFORE it commits (#36) — waiting for
            // the tuck to be armed asked the question one step too late.
            if (c.BicycleNav is not { ParkJobActive: true })
            {
                c.ParkingNeighbours = null;
                continue;
            }
            var near = new List<Car>();
            foreach (var o in grid.Near(c.X, c.Y, (int)Math.Ceiling(14.0 / CellSizeM) + 1))
                if (o.Uid != c.Uid && Math.Hypot(o.X - c.X, o.Y - c.Y) <= 14.0)
                    near.Add(o);
            c.ParkingNeighbours = near;
        }
        foreach (var c in carList)
        {
            var arr = new (double, double, double, double)[nSweepSteps];
            // The body's lateral offset from the line it follows, measured ONCE per
            // car and tick (the sweep is 50 steps; doing it per step was the obvious
            // first draft and this file's history says plainly how much a per-step
            // cost here matters).
            double offRefM = SweepOffsetFromRefM(c);
            for (int i = 0; i < nSweepSteps; i++)
                arr[i] = PredictPos(c, (i + 1) * TtcSweepStepS, offRefM);
            sweeps[c.Uid] = arr;
        }

        int pairRing = (int)Math.Ceiling(PairCheckRadiusM / CellSizeM) + 1;
        foreach (var c in carList)
        {
            foreach (var o in grid.Near(c.X, c.Y, pairRing))
            {
                if (o.Uid <= c.Uid) continue;   // each pair once, deterministic
                // Different levels (bridge over ground at the fig8 crossing):
                // the 3D boxes never overlap at any sweep stage - no crossing
                // conflict can occur, so neither car must yield for the other.
                if (net is not null &&
                    net.Segments[c.SegIdx].Level != net.Segments[o.SegIdx].Level)
                    continue;
                double dist = Math.Hypot(o.X - c.X, o.Y - c.Y);
                if (dist > PairCheckRadiusM) continue;

                // Same-lane following pairs are v1's domain: if one car sits
                // directly behind the other within the corridor half-width,
                // the forward-corridor cap already governs the follower -
                // v2 must not turn the LEADER into a yielder for its own tail.
                // (The old pair check read a follower slightly right of the
                // leader's axis as "coming from my right + closing" and made
                // leaders stop for their followers at every queue - the fig8
                // fleet gridlock.)
                // Same-lane following pair. The old blanket filter skipped
                // these here on the assumption that v1's forward corridor
                // owns them - but v1's corridor is a STRAIGHT 1.8 m strip on
                // my axis, and on a curve the leader sits laterally outside
                // it at exactly the distance where the cap must start
                // binding: T_033 car 11 (20.7 m/s) and leader car 13, 18 m
                // apart on the curve - outside v1's corridor (lat ~2 m),
                // filtered out here (lat ~1.5 m on the LEADER's axis) -
                // capped by nobody until the gap was 6 m, rear-end at 26 km/h.
                // Same-lane pairs therefore ALWAYS get the v2 cap, borne by
                // the REAR car - never by the leader (that is what the old
                // right-before-left misread did: leaders stopping for their
                // own tails, the fig8 fleet gridlock).
                int? rearUid = null;
                // IsDirectlyBehind(me, other) = "other is behind ME" - so the
                // rear car is the OTHER one in each case.
                if (IsDirectlyBehind(c, o)) rearUid = o.Uid;
                else if (IsDirectlyBehind(o, c)) rearUid = c.Uid;
                if (Math.Min(c.Speed, o.Speed) < 6.0 &&               // TEMPORARY
                    Math.Max(c.Speed, o.Speed) >= 10.0 &&
                    dist <= 40.0)
                    Console.WriteLine($"DBGF t={simTime:F2} {c.Uid}({c.Speed:F1})/{o.Uid}({o.Speed:F1}) d={dist:F1} rear={rearUid}");

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
                                                net is null ? 0 : net.Segments[c.SegIdx].Level,
                                                net is null ? 0 : net.Segments[o.SegIdx].Level,
                                                padM: anyStationary ? 0.0 : PredictionPadM);
                if (Math.Min(c.Speed, o.Speed) < 6.0 &&                 // TEMPORARY
                    Math.Max(c.Speed, o.Speed) >= 10.0 &&
                    dist <= 40.0)
                    Console.WriteLine($"DBGV2x t={simTime:F2} {c.Uid}({c.Speed:F1})/{o.Uid}({o.Speed:F1}) d={dist:F1} " +
                        (conflict is null ? "NULL" : $"s={conflict.Value.TimeS:F1}"));
                if (conflict is null) continue;
                var (stageS, distC, distO) = conflict.Value;
                if (!immediateConflict.TryGetValue(c.Uid, out var imC) ||
                    stageS < imC.Ttc)
                    immediateConflict[c.Uid] = (o.Uid, stageS, o.Speed < StationaryEps);
                if (!immediateConflict.TryGetValue(o.Uid, out var imO) ||
                    stageS < imO.Ttc)
                    immediateConflict[o.Uid] = (c.Uid, stageS, c.Speed < StationaryEps);

                // Right-before-left decides which of the two bears the cap.
                // NOTE: a PRIORITY-road car is NOT exempt from this - right
                // of way never excuses collision avoidance (user rule): if a
                // real overlap is predicted, the priority car brakes too. The
                // sign only ADDS an obligation on the yield car (the v3 rule
                // below), it does not remove the physical avoidance for the
                // other side.
                bool cYields = ComesFromMyRight(c, o);
                bool oYields = ComesFromMyRight(o, c);
                // A yield car BRAKING BEFORE its stop line is holding at the
                // line (its body clears the priority lane by design) - not
                // stopping in anyone's path: keep the RBL/gap treatment, or
                // the priority car would brake for a car that will stop 6 m
                // clear of its lane (the T_013 box-parking deadlock).
                var fbsSlow = c.Speed >= o.Speed ? (Car)o : (Car)c;
                bool fbs = (ClosingFastVsBrakingSlow(c, o) ||
                            ClosingFastVsBrakingSlow(o, c)) &&
                           (net is null ||
                            !IsYieldingBeforeLine(fbsSlow, net, signedNodes));
                int yieldUid;
                if (rearUid is not null)
                {
                    // Same-lane: the rear car bears the cap (see the filter
                    // note above). Right-before-left is meaningless here -
                    // on a curve it reads the closing tail as an "incoming"
                    // car from the leader's right and caps the wrong one.
                    yieldUid = rearUid.Value;
                }
                else if (anyStationary)
                {
                    // The mover bears the cap: a stopped body cannot move away,
                    // and ComesFromMyRight reads stationary cars as never
                    // closing (right tie-break would coin-flip the cap onto
                    // the car that cannot do anything about it).
                    yieldUid = c.Speed < StationaryEps ? o.Uid : c.Uid;
                }
                else if (fbs)
                {
                    // Fast car closing on a BRAKING slow car: the slow one is
                    // becoming a wall (stopping ahead), so the FAST car bears
                    // the cap. RBL caps the slower one here and the faster
                    // arrives above the margin curve (T_035: car 36 at 20.6
                    // m/s, car 34 braking to a stop 36 m out - rear-end at
                    // 16 km/h).
                    yieldUid = c.Speed >= o.Speed ? c.Uid : o.Uid;
                }
                else if (cYields != oYields)
                    yieldUid = cYields ? c.Uid : o.Uid;
                else
                    yieldUid = Math.Min(c.Uid, o.Uid);  // tie -> lower uid yields
                double dMeet = yieldUid == c.Uid ? distC : distO;
                // A stationary other: continuous stop formula with
                // StopMarginM. The GradedCap's stageS<=1 s -> 0 cliff would
                // cap a fast car at ZERO the moment it is within 1 s of a
                // stopped body - already inside the unavoidable band (the
                // T_025 rear-end); the margin formula keeps braking smooth
                // and 3 m further out.
                // dMeet is the distance to the OVERLAP point (my front
                // touches the other's rear) = bumper gap + MY half length;
                // subtract it so the formula zeroes at the same bumper gap
                // as the v1 corridor formula (T_026: the 3.2 m mismatch
                // let the v2 cap target 6 m/s at a 4.6 m bumper gap while
                // v1 already demanded 0 - the car hit the stopped lead at
                // 2 m/s when the IsDirectlyBehind filter handed the pair
                // over mid-approach on the curve).
                // The margin formula applies when the TARGET (the car we
                // stop for) is effectively stationary (< StationaryCapMps),
                // not just fully stopped - a creeping target (< 2 m/s) gives
                // the GradedCap's zero-margin/1 s-cliff treatment and the
                // follower arrives inside the unavoidable band (T_030 car 11
                // vs creeping car 13).
                var targetCar = yieldUid == c.Uid ? o : c;
                // The margin formula also applies to a BRAKING slow target
                // (fbs): it stops ahead and becomes an obstacle, so the
                // yielder must be able to stop short of it with the full
                // margin - the GradedCap's recoverable-boundary math assumes
                // the target keeps moving.
                double cap = (targetCar.Speed < StationaryCapMps || fbs)
                    ? Math.Sqrt(2.0 * (yieldUid == c.Uid ? c : o).BrakeMps2 *
                          Math.Max(dMeet - (yieldUid == c.Uid ? c : o).LengthM * 0.5
                                   - StandstillGapM - StopMarginM, 0.0))
                    : GradedCap(stageS, dMeet, (yieldUid == c.Uid ? c : o).BrakeMps2);

                // RBL yield semantics (T_020/T_021, 2026-09-10): the raw
                // GradedCap let a yielder ROLL INTO the crossing box while
                // the other car was already closing (the cap only bound
                // late), then decelerated it as stageS shrank until
                // stageS < 1 s set the cap to ZERO INSIDE the box - the
                // yielder stopped 2-3 m past the node, blocking the
                // priority car's path, and got rear-ended at 65 km/h (2 of
                // 3 runs on the unsigned map). A driver commits: the gap is
                // good and they clear the box, or they hold BEFORE entering.
                // Never half-in.
                var yieldCar = yieldUid == c.Uid ? c : o;
                double yieldHalfL = yieldCar.LengthM * 0.5;
                if (rearUid is null && !fbs &&
                    targetCar.Speed >= StationaryCapMps &&
                    (dMeet <= yieldHalfL || EnteredCrossing(yieldCar)))
                {
                    // Nose at/past the meet point - INSIDE the crossing:
                    // complete it (once committed, keep going - user rule).
                    // A stop in the middle of the box guarantees a block;
                    // finishing the crossing is the only forward move.
                    // (A SLOW/STATIONARY target is never "completed": keep
                    // the raw cap - hold short of the body, no creeping in;
                    // forcing 4 m/s into a 2 m gap rear-ends it.)
                    cap = Math.Max(cap, RblCompleteCrossingMps);
                }
                else if (rearUid is null && !fbs && dMeet > yieldHalfL)
                {
                    // Still BEFORE the crossing: may enter only if it can
                    // CLEAR the box before the other car arrives (same
                    // feasibility test as the signed rule). Otherwise hold
                    // before the box - full stop, no rolling entry. (With a
                    // stationary other this reads as "the box is blocked -
                    // hold", which is the correct move.)
                    //
                    // The hold only applies while the other car is ACTUALLY
                    // approaching: a standing car is never "arriving" - the
                    // stageS computed against its 1.6 m/s floor sweep is
                    // fictitious. Holding for standing cars deadlocked all
                    // four mouths of the unsigned crossing (T_023: every
                    // approach car held for the standing car on the crossing
                    // diagonal; nobody moved, box empty, 0/50 for 200 s).
                    // With a standing other, keep the raw (mild) GradedCap:
                    // both cars enter, the closing-motion RBL test resolves
                    // the pair as they approach, and the in-box rule finishes
                    // the crossing.
                    var otherCar = yieldUid == c.Uid ? o : c;
                    double tClearS = (dMeet + SignCrossClearM) /
                                     Math.Max(yieldCar.Speed, 3.0);
                    if (otherCar.Speed >= OtherYieldingMinSpeedMps &&
                        stageS < tClearS + SignGapMarginS)
                    {
                        // The user rule: holding does NOT mean braking to rest
                        // wherever the rule happened to fire - the car creeps
                        // up to the crossing MOUTH and stops there (there is
                        // no painted line at an unsigned crossing, but the
                        // mouth is where the box begins). The cap is the
                        // speed that still brings it to rest at the mouth,
                        // measured with reaction + comfort, so a car far out
                        // keeps driving and only rests AT the line.
                        double roomToMouthM = dMeet - SignCrossClearM;
                        cap = roomToMouthM <= 0.0 ? 0.0
                            : ApproachCapMps(0.0, roomToMouthM);
                    }
                }

                int otherUid = yieldUid == c.Uid ? o.Uid : c.Uid;
                if (Math.Min(c.Speed, o.Speed) < 6.0 &&                  // TEMPORARY
                    Math.Max(c.Speed, o.Speed) >= 10.0 && dMeet <= 40.0)
                    Console.WriteLine($"DBGV2 t={simTime:F2} y{yieldUid}({(yieldUid == c.Uid ? c.Speed : o.Speed):F1})/{otherUid}({(yieldUid == c.Uid ? o.Speed : c.Speed):F1}) dMeet={dMeet:F1} stage={stageS:F2} cap={cap:F2}");
                string v2Text = anyStationary
                    ? $"[R2] car {otherUid} stopped in my path"
                    : $"[R2] predicted crossing with car {otherUid} in {stageS:F1} s";
                // --- OpeningGap: the queue wake-up fix ----------------------
                // A PAIR-UP that is opening the gap (the other car drives
                // away along my axis faster than I go) has no unavoidable
                // conflict. v2 still reports one because every car is swept
                // at max(current, plan) speed: a follower sitting at 0 with
                // the throttle held is predicted as a projectile that reaches
                // the leader within 1 s, and GradedCap's stageS<=1 s cliff
                // caps it at ZERO while that leader drives away at 22-48 km/h
                // with a 10-20 m gap. Measured on fig8_xing (2026-10-19):
                // cars 43/20/16 held at 0 in seconds-long blocks - the queue
                // woke up seconds late and the delay cascaded down the arm.
                // v1's route-true corridor owns exactly this situation (it
                // measures the real gap and the leader's real speed), so for
                // a parallel pair that is opening the gap v2 must not add a
                // cap. Oblique/crossing pairs keep it: there the conservative
                // sweep is what the right-before-left ENTRY decision needs
                // (a slow car that may enter must read as if it enters).
                // Narrow trigger: only a car ALREADY AT REST is handed over,
                // because that is the wake-up case; a rolling car keeps v2's
                // cap (widening it to rolling pairs gridlocked the signed
                // crossing - measured 2026-10-19, crash at t=41.6 s).
                var oCar2 = yieldUid == c.Uid ? o : c;
                // Same key family as v1 ("avoid#uid"): both are "car X is in
                // my path" - the cap values oscillate between the two models
                // tick-to-tick, and a per-model key would log a new event on
                // every flip instead of one per situation.
                // Staged PRIORITY response: a priority-road car (Vorfahrt) does
                // NOT brake the instant a crossing conflict is predicted - it
                // observes first. So the immediate v2 cap is withheld for it and
                // the staged logic below decides its brake instead. Non-priority
                // cars keep the immediate cap (right-before-left / stopped body).
                bool wakeCase = yieldCar.Speed < 0.5 &&
                    IsParallelPair(yieldCar, oCar2) && OpeningGap(yieldCar, oCar2);
                if (!wakeCase)
                {
                    if (!IsPriorityCar(yieldCar))
                        OfferCap(yieldUid, cap, $"avoid#{otherUid}", v2Text, otherUid);
                }
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
                if (c.AlertOtherUid != 0 &&
                    simTime - c.AlertLastSeenT >= AlertClearHoldS)
                {
                    // Conflict absent for AlertClearHoldS - genuinely gone
                    // (see the constant for the flicker this guards).
                    int gone = c.AlertOtherUid;
                    c.ClearAlert();
                    c.RecordDecision(simTime, $"[R6] car {gone} cleared - proceeding");
                }
                continue;
            }
            c.AlertLastSeenT = simTime;
            int otherUid = pc.Other;
            double ttc = pc.Ttc;
            double brakingTime = c.Speed > 0.01 ? c.Speed / c.BrakeMps2 : double.MaxValue;

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
                // growth is my own action, docs/HUMAN_FACTORS.md §3).
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
                // (c) A yield car still before its stop line is yielding,
                // not a threat - see IsYieldingBeforeLine. Keep watching;
                // if it commits, the brake fires on the next tick.
                if (net is not null &&
                    carByUid.TryGetValue(otherUid, out var yc) &&
                    IsYieldingBeforeLine(yc, net, signedNodes))
                {
                    c.AlertLastTtc = ttc;
                    continue;
                }
                // The cap is the TIGHTER of:
                //  (a) the meeting-point cap (GradedCap) - assumes a CLEAR
                //      meeting zone: stop StandstillGap short of it; and
                //  (b) the actual GAP to the other car's body (the v1
                //      formula): the meeting zone can be OCCUPIED - a
                //      stationary car sitting at the meeting point would be
                //      clipped by (a) alone, which stops me 3 m short of the
                //      point = INSIDE the other car (measured: the fig8_xing
                //      car 13 vs 37 crash, both stopped nose-on-tail).
                double cap = GradedCap(Math.Max(ttc, 1.01), pc.DistP, c.BrakeMps2);
                if (carByUid.TryGetValue(otherUid, out var oCar))
                {
                    double radC = Math.Radians(c.Heading);
                    double fx2 = Math.Sin(radC), fy2 = Math.Cos(radC);
                    double alongM = (oCar.X - c.X) * fx2 + (oCar.Y - c.Y) * fy2;
                    double gapM = Math.Max(alongM - (c.LengthM + oCar.LengthM) * 0.5, 0.0);
                    double orad = Math.Radians(oCar.Heading);
                    double vl = Math.Max(oCar.Speed *
                        (Math.Sin(orad) * fx2 + Math.Cos(orad) * fy2), 0.0);
                    cap = Math.Min(cap, Math.Sqrt(vl * vl +
                        2.0 * c.BrakeMps2 * Math.Max(gapM - StandstillGapM, 0.0)));
                }
                OfferCap(c.Uid, cap, $"avoid#{otherUid}",
                         $"[R2] self-protection vs car {otherUid} (TTC {ttc:F1} s)", otherUid);
            }
            // else: within grace, not too late - keep watching, no brake.
        }

        // --- R14: blocked box / no entry into an occupied exit ------------
        // docs/DRIVING_MANEUVERS.md §8 R14, from StVO §1 Absatz 2 (docs/StVO.pdf
        // §1(2)): "Wer am Verkehr teilnimmt hat sich so zu verhalten, dass
        // kein Anderer geschädigt, gefährdet oder mehr, als nach den
        // Umständen unvermeidbar, behindert oder belästigt wird." Driving
        // INTO a crossing one cannot clear parks a body in the box and
        // impedes every user of the other arms, so ENTRY is gated on the
        // EXIT being free: neither the box itself nor my own exit lane may
        // hold a body that is not driving away. Independent of right of
        // way - a Vorfahrt car holds too, priority never buys an entry into
        // an occupied crossing (that is what the "even with right-of-way"
        // clause of R14 means). Only the ENTRY decision: once committed the
        // user rule still applies (R3: a car already in the crossing keeps
        // going, else it deadlocks the cars it blocks).
        //
        // The exit test is LANE-true, not arm-true: a car on the OTHER arm
        // driving towards the node sits in the opposite lane of my exit arm,
        // ~3.5 m to my left, and is not on my exit - counting it held the
        // two diagonals against each other and froze an empty box.
        const double R14ExitScanM = 25.0;    // box + a car + its braking distance
        // uid -> the car blocking my exit (the blocker is also the cap's
        // reason, so the commit block below can name it).
        var r14Blocked = new Dictionary<int, int>();
        // R30 needs the WHOLE chain, not the first blocker: every standing
        // body in my box/exit, plus where my stop line is.
        var r14Blockers = new Dictionary<int, List<int>>();
        var r14Room = new Dictionary<int, double>();
        var r14Node = new Dictionary<int, string>();
        if (net is not null)
            foreach (var c in carList)
            {
                var seg = net.Segments[c.SegIdx];
                string aheadNode = c.Forward ? seg.EndNode : seg.StartNode;
                // A real crossing: two roads CROSS (degree >= 4) - the same
                // test as the centre marker; a T where one road merely joins
                // has no box to keep clear.
                if (net.NodeDegree.GetValueOrDefault(aheadNode, 0) < 4) continue;
                if (!net.Nodes.TryGetValue(aheadNode, out var nxy)) continue;
                double rad14 = Math.Radians(c.Heading);
                double fx14 = Math.Sin(rad14), fy14 = Math.Cos(rad14);   // forward
                double rx14 = Math.Cos(rad14), ry14 = -Math.Sin(rad14);  // right
                // c.X/c.Y is the REAR axle - the nose sits REAR_AXLE_OFFSET_M
                // + half a body ahead, so "before the box" is measured from
                // the nose, same as the yield stop line.
                // The line a car must stop AT: the painted yield stop line
                // where the approach is signed, otherwise the crossing MOUTH
                // - nothing is drawn at an unsigned crossing, but the box
                // edge is still the line that decides (user rule: creep up
                // and stop THERE, not 40 m short).
                bool yieldSigned14 = net.Signs.TryGetValue((c.SegIdx, aheadNode), out var sgn14)
                                     && sgn14 == SignType.Yield;
                double noseGapM = yieldSigned14 ? YieldStopNoseGapM
                                                : Math.Max(1.0, net.Segments[c.SegIdx].Width * 0.5);
                double stopGapM = c.LengthM * 0.5 + Config.REAR_AXLE_OFFSET_M + noseGapM;
                double dToNodeM = Math.Hypot(c.X - nxy.X, c.Y - nxy.Y);
                if (dToNodeM > SignActRangeM) continue;              // not approaching yet
                if (dToNodeM <= stopGapM) continue;                  // entered - R3 owns it
                if (c.CrossingCommittedNode == aheadNode) continue;  // committed: keep going
                // Must actually drive AT the node, else this crossing is
                // beside me and not ahead of me.
                double odxM = nxy.X - c.X, odyM = nxy.Y - c.Y;
                if (fx14 * odxM + fy14 * odyM < 0.5 * dToNodeM) continue;
                // MY exit is the LANE that continues to the right of the
                // through axis, not the axis itself: a car waiting in the
                // OPPOSITE lane of the exit arm is not on my exit - it sits
                // one lane width to the left and passes me with room to
                // spare (measured: a pickup held at its stop line "exit
                // blocked by car 29" while car 29 sat in the other lane).
                double exitLaneOffM = Config.LaneBaseOffsetM(
                    net.Segments[c.SegIdx].Width, net.Segments[c.SegIdx].Lanes);
                var bs14 = new List<int>();
                double r14RoomM = dToNodeM - stopGapM;
                foreach (var o in carList)
                {
                    if (o.Uid == c.Uid) continue;
                    if (net.Segments[o.SegIdx].Level != net.Segments[c.SegIdx].Level) continue;
                    double ox = o.X - nxy.X, oy = o.Y - nxy.Y;
                    double dOM = Math.Hypot(ox, oy);
                    bool inBox = dOM <= SignCrossClearM;
                    double along = ox * fx14 + oy * fy14, lat = ox * rx14 + oy * ry14;
                    bool onExit = !inBox && along > 0.0 && along <= R14ExitScanM &&
                                  Math.Abs(lat - exitLaneOffM) <= (c.WidthM + o.WidthM) * 0.5;
                    if (!inBox && !onExit) continue;
                    // A body that drives away is clearing the crossing. A body
                    // that is STANDING, CRAWLING or BRAKING is not: it will
                    // rest here, so it blocks my exit. The old test only saw
                    // one already below StationaryCapMps, which let four cars
                    // enter a crossing each behind a car that was still
                    // braking - and the box then held a circular wait (measured:
                    // the signed crossing wedged with four cars, each nose in
                    // the next one's exit, nobody able to move).
                    if (o.Speed > StationaryCapMps && !o.IsBraking) continue;
                    bs14.Add(o.Uid);
                }
                if (bs14.Count == 0) continue;
                r14Blocked[c.Uid] = bs14[0];
                r14Blockers[c.Uid] = bs14;
                r14Room[c.Uid] = r14RoomM;
                r14Node[c.Uid] = aheadNode;
            }

        // --- R30: the crossing must stay RESOLVABLE (recursive) -----------
        // docs/DRIVING_MANEUVERS.md §8 R30 (the user rule of 2026-10-19):
        // creeping onto a crossing is allowed EXACTLY when the chain of
        // standing blockers ahead of me has a base case - a car that can
        // drive away. "If that car doesn't have a resolution for leaving the
        // crossing yet, then it must make sure that the car that this car is
        // blocked by has a resolution and so on and so on ... down all the
        // way" = recurse down the blocked-by chain. A cycle in that chain
        // means nobody has a resolution, which is the total lockdown the user
        // forbids ("it should never ever happen that the cars stand there
        // forever"). R14 above is the depth-1 snapshot of the same rule;
        // R30 is its recursive form: it still refuses the swastika (four
        // cars, each nose in the next one's exit, no base case anywhere) but
        // it lets a car follow a queue member that WILL move off, which is
        // what the depth-1 test got wrong.
        foreach (var e14 in r14Blocked)
        {
            var visiting30 = new List<int>();
            if (Resolvable(e14.Key, r14Blockers, visiting30, 16))
                continue;   // the chain bottoms out - creeping in is legal
            double roomM = r14Room[e14.Key];
            OfferCap(e14.Key, roomM <= 0.0 ? 0.0 : ApproachCapMps(0.0, roomM),
                     $"r14#{r14Node[e14.Key]}#{e14.Value}",
                     $"[R30] crossing would not stay resolvable (chain vs car {e14.Value}) - holding before the crossing",
                     e14.Value);
        }

        // --- R29: the desperation valve (must never fire) -----------------
        // A cycle in the blocked-by chain whose members are ALL at standstill
        // is a total lockdown. Prevention (R30) should make it unreachable -
        // if it is not, the sim must not stand there forever, so one car is
        // taken out of the equation and the event is logged as an ERROR (the
        // e2e suite fails on it, see tests/test_turning.py). The victim is
        // the cycle member that entered LAST (highest uid: the one whose
        // entry created the cycle), and the valve only opens after the
        // lockdown has persisted LOCKDOWN_VALVE_S - a single-frame flicker
        // must not delete a car.
        foreach (var e29 in r14Blocked)
        {
            var cycle = FindLockdownCycle(e29.Key, r14Blockers, carByUid);
            if (cycle is null) continue;
            var key = string.Join(",", cycle);
            _lockdownRun[key] = _lockdownRun.GetValueOrDefault(key, 0) + 1;
            if (_lockdownRun[key] < LockdownValveTicks) continue;
            var victim = cycle.Max();
            if (!DespawnRequests.Contains(victim))
            {
                DespawnRequests.Add(victim);
                _lockdownRun.Remove(key);
                var msg = $"[R29] LOCKDOWN broken: car {victim} removed - " +
                          $"cycle {key} stood {LockdownValveTicks / 60.0:F1} s";
                DebugLog.Add(msg);
                Console.WriteLine($"!! R29 DESPERATION VALVE (an error, must never " +
                                  $"happen): {msg}");
            }
        }

        // --- R28: the liveness invariant, MEASURED (geometric) ------------
        // docs/DRIVING_MANEUVERS.md §8 R28: the sim must NEVER reach a total
        // lockdown. The graph above only sees cars BEFORE the box (R14/R30
        // are ENTRY decisions - a committed car is not in it), so it missed
        // the situation the user showed on 2026-10-19: three cars wedged
        // INSIDE the crossing, each capped at 0 by a predicted conflict, and
        // nobody able to move. The measurement is therefore GEOMETRIC:
        // >= 2 cars with their centre in the box and every car near the node
        // standing for LOCKDOWN_REPORT_S of sim time IS a lockdown. The event
        // names every member with its pose, its active cap key and its last
        // decision, so the cause is read from data, not guessed.
        var lockCars = new Dictionary<string, List<Car>>();
        if (net is not null)
            foreach (var c in carList)
            {
                var seg = net.Segments[c.SegIdx];
                string aheadNode28 = c.Forward ? seg.EndNode : seg.StartNode;
                if (net.NodeDegree.GetValueOrDefault(aheadNode28, 0) < 4) continue;
                if (!net.Nodes.TryGetValue(aheadNode28, out var n28)) continue;
                if (Math.Hypot(c.X - n28.X, c.Y - n28.Y) > SignCrossClearM + 14.0)
                    continue;
                if (!lockCars.TryGetValue(aheadNode28, out var ml))
                    lockCars[aheadNode28] = ml = new List<Car>();
                ml.Add(c);
            }
        foreach (var kv28 in lockCars)
        {
            var members = kv28.Value;
            if (!net.Nodes.TryGetValue(kv28.Key, out var nxy28)) continue;
            int inside28 = 0;
            bool allStanding = members.Count >= 2;
            foreach (var c in members)
            {
                if (Math.Hypot(c.X - nxy28.X, c.Y - nxy28.Y) <= SignCrossClearM) inside28++;
                if (c.Speed > 0.3) allStanding = false;
            }
            if (!allStanding || inside28 < 2) { _lockdownStood.Remove(kv28.Key); continue; }
            var stood28 = _lockdownStood.GetValueOrDefault(kv28.Key, 0) + 1;
            _lockdownStood[kv28.Key] = stood28;
            if (stood28 == LockdownReportTicks)
            {
                var parts = new List<string>();
                foreach (var c in members)
                {
                    var dec = c.DecisionsSnapshot();
                    var why = dec.Count > 0 ? dec[dec.Count - 1].Msg : "(no decision)";
                    parts.Add($"car {c.Uid} seg {c.SegIdx} v {c.Speed * 3.6:F1} km/h " +
                              $"at ({c.X:F2},{c.Y:F2}) head {c.Heading:F0} " +
                              $"cap {c.ActiveCapKey ?? "-"} why {why}");
                }
                var msg28 = $"[R28] LOCKDOWN at node {kv28.Key}: {members.Count} cars " +
                            $"({inside28} in the box) stood {LockdownReportTicks / 60.0:F1} s - " +
                            string.Join(" | ", parts);
                LockdownEvents.Add(msg28);
                DebugLog.Add(msg28);
                Console.WriteLine($"!! R28 LOCKDOWN (an error - prevention failed): {msg28}");
            }
            if (stood28 >= LockdownValveTicks)
            {
                // R29 on the GEOMETRIC detection as well: the graph valve
                // above cannot see committed cars, so a wedged box would
                // otherwise stand there forever. The victim is the car with
                // the highest uid INSIDE the box (the last one to arrive).
                Car? victim28 = null;
                foreach (var m in members)
                    if (Math.Hypot(m.X - nxy28.X, m.Y - nxy28.Y) <= SignCrossClearM &&
                        (victim28 is null || m.Uid > victim28.Uid))
                        victim28 = m;
                if (victim28 is not null && !DespawnRequests.Contains(victim28.Uid))
                {
                    DespawnRequests.Add(victim28.Uid);
                    _lockdownStood.Remove(kv28.Key);
                    Console.WriteLine($"!! R29 DESPERATION VALVE (an error, must never " +
                                      $"happen): car {victim28.Uid} removed at node " +
                                      $"{kv28.Key} after {LockdownValveTicks / 60.0:F1} s " +
                                      "of standstill");
                }
            }
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
                double dToNodeM = Math.Hypot(c.X - nxy.X, c.Y - nxy.Y);
                if (dToNodeM > SignActRangeM)
                    continue;   // not approaching yet
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
                    // 3D: a car on another level (bridge branch at the same
                    // physical point) never conflicts with this one.
                    if (net.Segments[o.SegIdx].Level != net.Segments[c.SegIdx].Level)
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
                    double dOM = Math.Hypot(o.X - nxy.X, o.Y - nxy.Y);
                    // Scan by DISTANCE to the node, not by segment: limiting
                    // the scan to the node's own segments was blind to a fast
                    // car on the segment BEHIND the arm (T_017: car 27
                    // committed at t=36.4 while car 26 closed from ~45 m out
                    // on the segment behind the arm - invisible to the
                    // segment filter). That commit deadlocked the box: car
                    // 27's EXIT arm was occupied by car 26 (head-on, 4 m
                    // out), and car 26 held for car 27 in the box - circular
                    // wait, whole fleet frozen. The hdiff + pointing + eta
                    // filters below exclude non-threats.
                    if (dOM > GapScanRangeM)
                        continue;
                    if (dOM < PriorityProximityM)
                    { blocked = true; blockedBy = o.Uid; break; }   // already at the crossing
                    // Approaching from its arm? Forward vector must point at C.
                    double odxM = nxy.X - o.X, odyM = nxy.Y - o.Y;
                    double orad2 = Math.Radians(o.Heading);
                    if (Math.Sin(orad2) * odxM + Math.Cos(orad2) * odyM < 0.3 * dOM)
                        continue;   // exiting / crossing sideways - not a threat
                    // R27: an arm whose car is standing AND is itself held by
                    // a reason that does not name me cannot arrive while I
                    // cross - it is no imminent threat, so it must not keep
                    // me waiting (the cycle N->W->S->E->N otherwise
                    // deadlocks). Applied to the APPROACHING (eta) threat
                    // ONLY: a car already at/in the crossing above is a real
                    // blocker regardless of why it is held - skipping it
                    // there let a yield car enter an occupied box (crash at
                    // t=41 s on fig8_xing, 2026-10-19).
                    if (NoImminentThreat(o, c))
                        continue;
                    double etaS = dOM / Math.Max(o.Speed, 3.0);
                    if (etaS < tClearS + SignGapMarginS)
                    { blocked = true; blockedBy = o.Uid; byFeasibility = true; break; }
                }
                // The user rule: crossing the stop line is only allowed when
                // the car believes it can actually clear the crossing. A bad
                // gap therefore does NOT mean stopping 40 m short (the old
                // flat 0.0) and does NOT commit the car - it creeps to the
                // line and rests THERE, and only a clear gap lets the nose
                // cross it. A predicted real overlap is still the hard net
                // below (R2), which brakes even a committed car.
                if (blocked)
                {
                    // HOLD THE LINE (full stop) while the gap is bad. The old
                    // "fastest speed that still stops at the line" cap let a
                    // SLOW car roll straight through: it could technically stop
                    // (2.5 m/s < 6.5 m/s cap) so the cap never bound, it crossed
                    // the line and committed into the path of the car it was
                    // supposed to yield to (T_015: car 34 at 8 km/h committed
                    // at t=4.35 while car 10 closed at 65 km/h, 0.7 s out). A
                    // driver at a yield sign holds the line when the gap is
                    // bad; the commit block releases the hold once the gap
                    // opens (or the box blocker moves - see (b) above).
                    // The user rule: a car that may not enter does NOT brake to
                    // rest dozens of metres before the crossing - it holds the
                    // STOP LINE. The cap is the speed that still brings it to
                    // rest with its nose at the line (reaction + comfort), so it
                    // creeps up and stops AT the line; only at the line itself
                    // does it rest. The old flat 0.0 parked cars at whatever
                    // distance the rule happened to fire from (measured: "[R4]
                    // cannot clear ... (31 m out)" - a whole car park short).
                    double roomToLineM = dToNodeM - stopGapM;
                    OfferCap(c.Uid, roomToLineM <= 0.0 ? 0.0 : ApproachCapMps(0.0, roomToLineM),
                             $"sign#{aheadNode}#{blockedBy}",
                             byFeasibility
                             ? $"[R4] cannot clear before car {blockedBy} arrives " +
                               ($"({dToNodeM:F0} m out)")
                             : $"[R5] yield sign: car {blockedBy} at the crossing " +
                               ($"({dToNodeM:F0} m out)"),
                             blockedBy);
                    continue;   // held at the line - NOT committed
                }
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
                    // (b') R14: my exit lane or the box holds a body that is
                    // not clearing - the legal move is to hold before the
                    // line, so the crossing never gets committed into.
                    if (c.CrossingCommittedNode != aheadNode &&
                        r14Blocked.TryGetValue(c.Uid, out var r14By))
                        continue;   // the R14 cap above already holds the car
                    // (b) Do not commit into a BLOCKED box: a stationary
                    // body in my path means the crossing is occupied - the
                    // legal move is to hold at the stop line. The old
                    // unconditional commit put cars in the box behind the
                    // blocker, where the only way out was a creep into
                    // contact (fig8_xing: car 29 committed at t=4.3 into a
                    // box car 5 had occupied since t~2.5, then crept for
                    // 15 s, 2026-09-10). Once committed the user rule still
                    // applies: keep going.
                    if (c.CrossingCommittedNode != aheadNode &&
                        immediateConflict.TryGetValue(c.Uid, out var blk) &&
                        blk.OtherStationary)
                    {
                        // Full stop at the line until the blocker moves or
                        // clears. (A non-priority car already carries the v2
                        // "stopped in my path" cap; a priority car's staged
                        // response has no immediate cap, so hold it here.)
                        OfferCap(c.Uid, 0.0, $"avoid#{blk.Other}",
                                 $"[R3] holding before line - box blocked by car {blk.Other}",
                                 blk.Other);
                        continue;
                    }
                    if (c.CrossingCommittedNode != aheadNode)
                    {
                        c.CrossingCommittedNode = aheadNode;
                        c.RecordDecision(simTime,
                                         "[R3] entered crossing - committed, continuing");
                    }
                    continue;   // entered the crossing - committed, free to go
                }
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
                    c.HeldByUid = r.By;      // the actor behind the rule
                    // R26: the concession is VISIBLE to the other driver -
                    // hazard lights are the real-world "I signal my intent"
                    // (the driver enforces a 5 s minimum display, so a
                    // negotiation cannot strobe).
                    if (r.Key.StartsWith("passage#"))
                        (c.Driver as BicycleDriver)?.SetHazard(true,
                            "single-file passage - yielding the stretch");
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
                        if (c.ActiveCapKey.StartsWith("passage#") &&
                            !r.Key.StartsWith("passage#"))
                            (c.Driver as BicycleDriver)?.SetHazard(false);
                        c.ActiveCapKey = r.Key;
                        c.HeldByUid = r.By;
                        if (r.Key.StartsWith("passage#"))
                            (c.Driver as BicycleDriver)?.SetHazard(true,
                                "single-file passage - yielding the stretch");
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
                    if (c.ActiveCapKey.StartsWith("passage#"))
                        (c.Driver as BicycleDriver)?.SetHazard(false);
                    c.RecordDecision(simTime, "gap clear - resuming");
                    c.ActiveCapKey = null;
                    c.HeldByUid = 0;         // free: nobody holds me any more
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
        // TEMPORARY: per-tick applied-cap trace for fast cars
        foreach (var c in carList)
            if (c.Speed >= 5.0 && caps.TryGetValue(c.Uid, out var ap) &&
                ap < c.Speed - 0.5)
                Console.WriteLine($"DBGCAP t={simTime:F2} uid{c.Uid} v={c.Speed:F1} cap={ap:F2}");
        return caps;
    }

    /// <summary>Predict `car`'s position `t` seconds out: advances along its
    /// BicycleNav refline when one is active (clamped to Total - a car does
    /// not predict past its route), else linear extrapolation from its
    /// current heading. Prediction speed is max(current, planned target) -
    /// conservative, so a car that is slow now but about to cruise doesn't
    /// read as harmless (the right-before-left ENTRY decision needs that;
    /// see the constant block above). Returns the predicted pose plus the
    /// arc-length the car will have covered by then (used as its own
    /// braking distance if it turns out to be the one that must yield).</summary>
    /// <summary>How far the body stands to the RIGHT of its own reference line,
    /// signed, in metres (#32) — but ONLY for a car that is going to STAY where it
    /// stands: a parked one.
    ///
    /// A car is not its lane. A vehicle parked in a parking bay has a real lateral
    /// position that differs from the line its nav last followed — and a neighbour
    /// predicting it must see the body, not the stripe of road it happens to be
    /// nearest. Measured before this existed: a Fiat parked 8.35 m from the
    /// centreline (3.10 m beside a sedan's lane) was swept at its reference line, i.e.
    /// IN that lane, and every car driving past braked to a standstill for a vehicle
    /// that was never in its path (`cap=avoid#…`, min speed 0.0).
    ///
    /// A car UNDER WAY keeps the line, deliberately. First draft applied the offset
    /// to every sweep and the 50-car crossing lost a new pair at t=7.6 s (fig8_xing,
    /// which on main fails differently, at the centreline at t=8.7 s): a body's
    /// CURRENT offset is the wrong prediction for a car whose offset is changing,
    /// because a lane change in progress converges on the line, and the old
    /// line-sweep was conservatively right about that. Standing off-line is the case
    /// where the body is the better prediction; moving is not.
    ///
    /// Clamped rather than trusted at arm's length: a body far off its line means the
    /// line and the car have lost track of each other, and swinging a sweep 20 m
    /// sideways along a curve is a worse prediction than the standing behaviour.</summary>
    const double MaxSweepRefOffsetM = 6.0;

    static double SweepOffsetFromRefM(Car car)
    {
        if (car.State != CarState.Parked) return 0.0;   // see above: moving = the line
        var nav = car.BicycleNav;
        var refLine = nav?.Ref;
        if (refLine is null) return 0.0;
        var (bx, by) = refLine.PointAt(nav!.S);
        double rb = Math.Radians(refLine.HeadingAt(nav.S));
        // The sim's lateral convention, inverted: an offset O to the line's right
        // moves a point by (cos·O, -sin·O) — see SimEngine's spawn placement — so
        // the offset of a body from the line is its delta dotted onto that normal.
        double off = (car.X - bx) * Math.Cos(rb) - (car.Y - by) * Math.Sin(rb);
        return Math.Clamp(off, -MaxSweepRefOffsetM, MaxSweepRefOffsetM);
    }

    static (double X, double Y, double HeadingDeg, double DistM) PredictPos(Car car, double t,
                                                                            double refOffsetM = 0.0)
    {
        double v = Math.Max(MinSweepSpeedMps,
                            Math.Max(car.Speed, car.TargetSpeed));
        var nav = car.BicycleNav;
        var refLine = nav?.Ref;
        if (refLine is not null)
        {
            double s0 = nav!.S;
            double sPred = Math.Min(s0 + v * t, refLine.Total);
            var (px, py) = refLine.PointAt(sPred);
            double hPred = refLine.HeadingAt(sPred);
            // Follow the LINE for progress, keep the CAR for lateral position: this
            // is the whole fix, and it is why the offset is applied at the predicted
            // heading (on a curve, "3 m right of the line" is a different direction
            // three metres on).
            if (refOffsetM != 0.0)
            {
                double rp = Math.Radians(hPred);
                px += Math.Cos(rp) * refOffsetM;
                py -= Math.Sin(rp) * refOffsetM;
            }
            return (px, py, hPred, sPred - s0);
        }
        double rad = Math.Radians(car.Heading);
        double dM = v * t;
        return (car.X + Math.Sin(rad) * dM, car.Y + Math.Cos(rad) * dM,
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
        int lvlC, int lvlO,
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
            if (ObstacleGeometry.BoxesIntersect(
                    new ObstacleGeometry.BodyBox(boxC, lvlC),
                    new ObstacleGeometry.BodyBox(boxO, lvlO)))
                return ((i + 1) * TtcSweepStepS, cd, od);
        }
        return null;
    }

    /// <summary>Speed-dependent look-ahead in metres: how far ahead a car
    /// must see a stopped leader to still be able to stop in time =
    /// reaction distance (v·t_react) + full braking distance (v²/2a) +
    /// standstill gap, floored at LookAheadMinM. See the constant block for
    /// why the range must be at least the car's own stopping distance.</summary>
    // Sampling step of the road polyline ahead of a car (v1's route-true
    // corridor): 8 m keeps the per-pair scan cheap (a 200 m walk is 25
    // points) while a 4.4 m body can never hide between two samples.
    const double RouteSampleStepM = 8.0;
    // How far the road walk extends. The comfort demand at the map's legal
    // top speed (~130 km/h) wants ~300 m of look-ahead; the walk covers 200
    // m and straight-road pairs are handled by the cheap ray test, which
    // covers the rest.
    const double RoadPathMaxM = 200.0;
    // The walk is rebuilt at most every RouteRebuildS of SIM time per car
    // (measured: rebuilding it every substep at 50 cars drove the sim to
    // 0.01x real time - the host needed 2 wall minutes to reach sim t=1 s).
    // 0.1 s of sim moves a car at 130 km/h by 3.6 m, well inside the 4 m
    // sample step's error, and the corridor's own demand is a gap, not a
    // centimetre.
    const double RouteRebuildS = 0.1;
    private static readonly Dictionary<int, (double S, double X, double Y)[]> _roadPath = new();
    private static readonly Dictionary<int, double> _roadPathAt = new();
    private static readonly Dictionary<int, double> _roadOff = new();

    private static readonly List<int> _noSegs = new();

    /// <summary>The lane path ahead of a car, walked through the road
    /// network: my position -> the node I am heading for -> the connected
    /// segment that best continues my travel direction -> ... Sampled every
    /// RouteSampleStepM, s measured from the car. Deliberately NOT the car's
    /// nav line: that ends at the car's destination, and a leader beyond the
    /// end read 2-16 m "to the side" and went invisible to the corridor
    /// (measured: car 20 vs car 18, no cap offered for 2.8 s = 66 m of blind
    /// travel at 85 km/h). A continuation is only taken while it stays
    /// roughly forward (dot > -0.6), so the walk never doubles back.
    /// Returns null when the walk cannot start.</summary>
    static (double S, double X, double Y)[] BuildRoadAhead(Car c, RoadNetwork net, double rangeM)
    {
        var seg = net.Segments[c.SegIdx];
        bool fwd = c.Forward;
        // The path starts at MY OWN projection onto the segment axis, so s
        // measures from here and both cars' offsets from the axis are
        // comparable (see RoutePointOff): starting the polyline AT the car
        // instead made the first leg a chord from the lane to the node and
        // re-introduced the axis offset as a fake lateral gap.
        double ax1 = seg.X1, ay1 = seg.Y1, ax2 = seg.X2, ay2 = seg.Y2;
        double adx = ax2 - ax1, ady = ay2 - ay1;
        double alen = Math.Max(Math.Hypot(adx, ady), 1e-9);
        double u = Math.Clamp(((c.X - ax1) * adx + (c.Y - ay1) * ady) / (alen * alen), 0.0, 1.0);
        double projX = ax1 + u * adx, projY = ay1 + u * ady;
        double toEndM = fwd ? (1.0 - u) * seg.Length : u * seg.Length;
        string node = fwd ? seg.EndNode : seg.StartNode;
        if (!net.Nodes.TryGetValue(node, out var nxy)) return null;
        double curX = nxy.X, curY = nxy.Y;
        double inX = (fwd ? seg.X2 - seg.X1 : seg.X1 - seg.X2);
        double inY = (fwd ? seg.Y2 - seg.Y1 : seg.Y1 - seg.Y2);
        double inL = Math.Max(Math.Hypot(inX, inY), 1e-9);
        inX /= inL; inY /= inL;                       // travel dir into the node
        var px = new List<double> { projX, curX };
        var py = new List<double> { projY, curY };
        double travelled = Math.Max(toEndM, 0.0);
        int prev = c.SegIdx;
        int steps = 0;
        while (travelled < rangeM && steps++ < 64)   // hard bound: a zero-length
        {                                            // or looping continuation
            // must never spin the walk (measured: the host stopped advancing
            // the sim entirely, /state rate 0.0x)
            int bestIdx = -1; double bestDot = -0.6, bestX = 0.0, bestY = 0.0;
            bool bestFwd = false; string bestFar = "";
            foreach (var ni in net.NodeConnections.GetValueOrDefault(node, _noSegs))
            {
                if (ni == prev) continue;
                var ns = net.Segments[ni];
                if (ns.Length <= 0.1) continue;      // degenerate - cannot advance
                bool awayFwd = ns.StartNode == node;
                string far = awayFwd ? ns.EndNode : ns.StartNode;
                if (far == node || !net.Nodes.TryGetValue(far, out var fxy)) continue;
                double dx = fxy.X - curX, dy = fxy.Y - curY;
                double dl = Math.Max(Math.Hypot(dx, dy), 1e-9);
                double dot = dx / dl * inX + dy / dl * inY;
                if (dot > bestDot)
                {
                    bestDot = dot; bestIdx = ni; bestFwd = awayFwd;
                    bestX = fxy.X; bestY = fxy.Y; bestFar = far;
                }
            }
            if (bestIdx < 0) break;                   // dead end
            px.Add(bestX); py.Add(bestY);
            travelled += net.Segments[bestIdx].Length;
            double nx = bestX - curX, ny = bestY - curY;
            double nl = Math.Max(Math.Hypot(nx, ny), 1e-9);
            inX = nx / nl; inY = ny / nl;
            node = bestFar; curX = bestX; curY = bestY; prev = bestIdx;
        }
        var out_ = new List<(double, double, double)> { (0.0, px[0], py[0]) };
        double s = 0.0;
        for (int i = 0; i + 1 < px.Count; i++)
        {
            double ax = px[i], ay = py[i], bx = px[i + 1], by = py[i + 1];
            double segLen = Math.Max(Math.Hypot(bx - ax, by - ay), 1e-9);
            for (double t = RouteSampleStepM; t <= segLen + 1e-9; t += RouteSampleStepM)
            {
                double f = Math.Min(t / segLen, 1.0);
                out_.Add((s + t, ax + (bx - ax) * f, ay + (by - ay) * f));
            }
            s += segLen;
        }
        return out_.ToArray();
    }

    /// <summary>(arc distance along the path, SIGNED perpendicular offset
    /// from it - positive to the path's right) of a point: nearest sampled
    /// path point, refined against the two pieces around it. The corridor
    /// compares two cars' offsets, not their distance from the road axis -
    /// both sit ~2.5 m right of the axis in their lane, and measuring from
    /// the axis hid every same-lane leader (lat read 2.0-2.9 m, outside the
    /// 1.8 m band, i.e. invisible until contact).</summary>
    static (double S, double Off) RoutePointOff(
        (double S, double X, double Y)[] line, double x, double y, double sHint = 0.0)
    {
        // Samples are evenly spaced in s, so the leader's arc position is
        // near its ray projection: scan a +-96 m window around that instead
        // of the whole walk (the full scan was the whole cost of the sim at
        // 0.11x realtime with 50 cars on one loop).
        double step = line.Length > 1 ? line[1].S - line[0].S : RouteSampleStepM;
        int mid = (int)Math.Round(Math.Clamp(sHint, 0.0, line[line.Length - 1].S) / step);
        int lo = Math.Max(0, mid - 12), hi = Math.Min(line.Length - 1, mid + 12);
        int best = lo;
        double bestD2 = double.PositiveInfinity;
        for (int i = lo; i <= hi; i++)
        {
            double ddx = line[i].X - x, ddy = line[i].Y - y;
            double d2 = ddx * ddx + ddy * ddy;
            if (d2 < bestD2) { bestD2 = d2; best = i; }
        }
        double bestD = Math.Sqrt(bestD2);
        double bestS = line[best].S;
        double bestOff = 0.0;
        for (int i = Math.Max(0, best - 1); i < Math.Min(line.Length - 1, best + 1); i++)
        {
            double ax = line[i].X, ay = line[i].Y;
            double dx = line[i + 1].X - ax, dy = line[i + 1].Y - ay;
            double len2 = dx * dx + dy * dy;
            if (len2 <= 1e-9) continue;
            double t = Math.Clamp(((x - ax) * dx + (y - ay) * dy) / len2, 0.0, 1.0);
            double qx = ax + t * dx, qy = ay + t * dy;
            double d = Math.Hypot(x - qx, y - qy);
            if (d >= bestD) continue;
            bestD = d;
            bestS = line[i].S + t * (line[i + 1].S - line[i].S);
            double tl = Math.Sqrt(len2);
            bestOff = (x - qx) * (dy / tl) + (y - qy) * (-dx / tl);   // right positive
        }
        return (bestS, bestOff);
    }

    static double LookAheadForSpeed(double v)
    {
        // R7: the look-ahead must cover the DISTANCE THE DRIVER MODEL DEMANDS
        // (ApproachCapMps), not the panic-stop distance. With CAR_BRAKING
        // (~1 g) a 76 km/h car saw ahead only 46 m out, so the pair entered
        // the corridor at a 17.6 m gap - by then the follower was at 96 km/h
        // and no legal braking could save it (measured, fig8_xing car 30 vs
        // car 28: the required comfort cap was 47-80 km/h from a 45 m gap,
        // invisible to the old range).
        double brakingDist = v * v / (2.0 * ComfortBrakingMps2);
        return Math.Max(LookAheadMinM, v * ReactionTimeS + brakingDist + StandstillGapM);
    }

    /// <summary>My body extended from my centre to my speed-dependent
    /// look-ahead past my front bumper, at my REAL width - the strip of road
    /// I actually need to be clear to proceed. Used by v1's oblique-pair
    /// gate (see there).</summary>
    static ObstacleGeometry.BodyBox ForwardCorridorBox(Car c, int level)
    {
        double rad = Math.Radians(c.Heading);
        double fx = Math.Sin(rad), fy = Math.Cos(rad);
        var (bx, by) = c.BodyCenter();
        double s1M = c.LengthM * 0.5 + LookAheadForSpeed(c.Speed); // centre -> front+lookahead
        double scM = s1M / 2.0;
        return new ObstacleGeometry.BodyBox(
            ObstacleGeometry.BoxCorners(bx + fx * scM, by + fy * scM,
                                        c.Heading, s1M, c.WidthM), level);
    }

    /// <summary>True if `other` sits directly BEHIND `me` within the corridor
    /// half-width - a same-lane following configuration that v1's forward-
    /// corridor cap already governs (see the pair-loop filter).</summary>
    /// <summary>True if `fast` is closing on `slow` with at least DOUBLE the
    /// speed while `slow` is actively braking (TargetSpeed below current
    /// speed) - i.e. `slow` is stopping ahead and becoming an obstacle. In
    /// that configuration the fast car must be the one that adapts; the
    /// right-before-left reading (T_035 car 36/34) caps the slower car and
    /// lets the faster one arrive inside the unavoidable band.</summary>
    static bool ClosingFastVsBrakingSlow(Car fast, Car slow)
    {
        if (slow.Speed < 0.05 || fast.Speed < 2.0 * slow.Speed) return false;
        if (slow.TargetSpeed >= slow.Speed - 0.5) return false;   // not braking
        double dx = slow.X - fast.X, dy = slow.Y - fast.Y;
        double frad = Math.Radians(fast.Heading);
        double srad = Math.Radians(slow.Heading);
        double rvx = Math.Sin(frad) * fast.Speed - Math.Sin(srad) * slow.Speed;
        double rvy = Math.Cos(frad) * fast.Speed - Math.Cos(srad) * slow.Speed;
        return dx * rvx + dy * rvy > 0;   // closing distance
    }

    static bool IsDirectlyBehind(Car me, Car other)
    {
        double rad = Math.Radians(me.Heading);
        double fx = Math.Sin(rad), fy = Math.Cos(rad);     // forward
        double rx = Math.Cos(rad), ry = -Math.Sin(rad);    // right
        double dx = other.X - me.X, dy = other.Y - me.Y;
        if (dx * fx + dy * fy >= 0) return false;          // not behind
        double lat = Math.Abs(dx * rx + dy * ry);
        // Body sum, not the sedan rule: a 3 m truck behind a 3 m truck is
        // "directly behind" at a 2.9 m lateral offset even though a sedan
        // corridor (1.8 m) would call it a pass.
        return lat <= (me.WidthM + other.WidthM) * 0.5;
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
    /// <summary>True while the two cars run PARALLEL (same or opposite
    /// direction within 35 deg) - i.e. they are a lane pair, not a crossing
    /// pair. Only those may be handed over to v1's corridor when the gap
    /// opens (see the OpeningGap note at the call site).</summary>
    private static bool IsParallelPair(Car a, Car b)
    {
        double hdiff = Math.Abs(((a.Heading - b.Heading + 540.0) % 360.0) - 180.0);
        return hdiff < 35.0 || hdiff > 145.0;
    }

    /// <summary>True while `b` is moving AWAY from `a` along a's axis faster
    /// than a itself goes - the gap between the two grows, so no conflict can
    /// be unavoidable and a's brake must come from the corridor rule that
    /// measures the real gap (v1), not from a plan-speed sweep artifact.</summary>
    private static bool OpeningGap(Car a, Car b)
    {
        double ar = Math.Radians(a.Heading);
        double fx = Math.Sin(ar), fy = Math.Cos(ar);
        double br = Math.Radians(b.Heading);
        double vProj = b.Speed * (Math.Sin(br) * fx + Math.Cos(br) * fy);
        return vProj > a.Speed + 0.5;
    }

    /// <summary>The staged comfort cap. `brakeMps2` is the braking capability of
    /// the car being capped (#21) — a 24 t mixer must not be planned against a
    /// passenger car's 10 m/s², or the curve says it can stop where it cannot.</summary>
    static double GradedCap(double stageSeconds, double dM, double brakeMps2)
    {
        if (stageSeconds <= 1.0) return 0.0;
        double gap = Math.Max(dM - StandstillGapM, 0.0);
        double decel = stageSeconds <= 3.0 ? brakeMps2 : Config.PARK_BRAKING;
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
                                       Dictionary<int, double>? impactSpeed = null,
                                       RoadNetwork? net = null)
    {
        var grid = Grid.Build(cars);
        var contacted = new HashSet<int>();
        var partner = new Dictionary<int, int>();   // uid -> uid it is touching

        foreach (var c in cars)
        {
            int lvlC = net is null ? 0 : net.Segments[c.SegIdx].Level;
            var boxC = BodyForResolve(c, lvlC);
            foreach (var o in grid.Near(c.X, c.Y, 1))
            {
                if (o.Uid <= c.Uid) continue;   // each pair once, deterministic
                // 3D contact: cars on different levels (bridge over ground at
                // the fig8 crossing) never touch - the x/y overlap under the
                // deck is not a crash.
                if (net is not null &&
                    net.Segments[c.SegIdx].Level != net.Segments[o.SegIdx].Level)
                    continue;
                if (!ObstacleGeometry.BoxesIntersect(
                        boxC,
                        BodyForResolve(o, net is null ? 0 : net.Segments[o.SegIdx].Level)))
                    continue;

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
                    c.CrashEpisodes++;   // monotonic: the transient flag below
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
                                         impactSpeed is null ? null : impactSpeed.GetValueOrDefault(o.Uid),
                                         net);
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
