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
    // trigger) and up to LookAheadM ahead.
    const double CorridorHalfWidthM = 1.8;
    const double LookAheadM = 40.0;

    // Comfortable standstill gap: the stopping-distance curve is measured
    // from this gap, so a following car rests ~3 m short of the lead car.
    const double StandstillGapM = 3.0;

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

    /// <summary>Per-car speed caps from the cars in each forward corridor.
    /// Called with PRE-step positions. Missing uid = no constraint.</summary>
    public static Dictionary<int, double> ComputeAvoidCaps(IEnumerable<Car> cars)
    {
        var grid = Grid.Build(cars);
        var caps = new Dictionary<int, double>();
        double pppm = Config.PIXELS_PER_METER;
        int ring = (int)Math.Ceiling(LookAheadM / CellSize) + 1;   // 3

        foreach (var c in cars)
        {
            double rad = Math.Radians(c.Heading);
            double fx = Math.Sin(rad), fy = Math.Cos(rad);     // forward
            double rx = Math.Cos(rad), ry = -Math.Sin(rad);    // right
            double best = double.PositiveInfinity;

            foreach (var o in grid.Near(c.X, c.Y, ring))
            {
                if (o.Uid == c.Uid) continue;
                double dxc = o.X - c.X, dyc = o.Y - c.Y;
                double alongPx = dxc * fx + dyc * fy;          // ahead?
                if (alongPx < 0 || alongPx > LookAheadM * pppm) continue;
                double latPx = dxc * rx + dyc * ry;
                if (Math.Abs(latPx) > CorridorHalfWidthM * pppm) continue;

                // Box gap in metres: centre distance minus one full length.
                double gapM = alongPx / pppm - Config.CAR_LENGTH;
                if (gapM <= 0.25) { best = 0.0; break; }       // touching -> stop

                // Lead speed projected onto my axis (head-on car -> 0).
                double orad = Math.Radians(o.Heading);
                double vAhead = o.Speed *
                    (Math.Sin(orad) * fx + Math.Cos(orad) * fy);
                // Fastest speed from which CAR_BRAKING still stops within
                // the gap (measured from the standstill gap).
                double cap = Math.Min(Math.Max(vAhead, 0.0),
                                      Math.Sqrt(2.0 * Config.CAR_BRAKING *
                                                Math.Max(gapM - StandstillGapM, 0.0)));
                if (cap < best) best = cap;
            }

            if (best < double.PositiveInfinity) caps[c.Uid] = best;
        }
        return caps;
    }

    /// <summary>Called with POST-step positions: find overlapping body-box
    /// pairs, roll both cars back to their pre-step pose and stop them.
    /// Returns the uids in contact this substep (the validator treats their
    /// motion as externally constrained).</summary>
    public static HashSet<int> Resolve(IEnumerable<Car> cars,
                                       Dictionary<int, (double X, double Y, double Heading)> prevPose)
    {
        var grid = Grid.Build(cars);
        var contacted = new HashSet<int>();

        foreach (var c in cars)
        {
            var boxC = Body(c);
            foreach (var o in grid.Near(c.X, c.Y, 1))
            {
                if (o.Uid <= c.Uid) continue;   // each pair once, deterministic
                if (!ObstacleGeometry.BoxesIntersect(boxC, Body(o))) continue;

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
        return contacted;
    }
}
