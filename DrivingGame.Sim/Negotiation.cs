
// Negotiation.cs
// The right-of-way auction (docs/RIGHT_OF_WAY_NEGOTIATION.md §3 + §4 + §5).
// Resources are contested stretches keyed by geometry; the award is held
// across substeps and re-awarded only past a margin.

using System;
using System.Collections.Generic;

namespace DrivingGame.Sim;

// docs/RIGHT_OF_WAY_NEGOTIATION.md §3 + §4 + §5 (R34, R12, R16, R35).
//
// The negotiation is an AUCTION OVER RESOURCES, not a tree over cars. A
// resource is a stretch of carriageway that fewer cars can occupy at once
// than want to - keyed by GEOMETRY `(segment, [s0, s1])`, never by a car
// pair, so three cars crowding one pinch are one object with one award
// rather than three objects with three awards that can contradict each
// other.
//
// Deliberately NOT implemented yet, and why: the disturbance weight (R12)
// has no measured value. The design builds the auction with disturbance =
// 0 first and gives the weight from a measurement on the stress runs;
// until that number exists the term stays at 0 and is named as such
// rather than guessed at.
public static class Negotiation
{
    /// <summary>A contested stretch: which segment, and the arc range along
    /// it that the contest is about, in metres from the segment's own start
    /// node. Quantised to 0.5 m so a car wobbling by centimetres cannot
    /// mint a new resource every substep.</summary>
    public sealed class Res {
        public int Seg;
        public int A;          // s0 in half-metres
        public int B;          // s1 in half-metres
        public Res(int seg, double s0, double s1)
        {
            Seg = seg;
            A = (int)Math.Round(s0 * 2.0);
            B = (int)Math.Round(s1 * 2.0);
            if (A > B) { var t = A; A = B; B = t; }
        }
        public int Key { get { return (Seg * 8191 + A) * 8191 + B; } }
    }

    /// <summary>The award: who holds which resource right now. Held across
    /// substeps and re-awarded only past a margin - the shape of
    /// `_releaseHold` applied to the negotiation instead of to a brake
    /// decision, so a contested stretch cannot flip owner every frame and
    /// make both drivers hesitate in turn.</summary>
    private static readonly Dictionary<int, int> _holder = new();

    /// <summary>R35 (docs §5): the ROLE a car drives with. The emergency
    /// vehicle changes PREFERENCE only - it never changes what is
    /// physically possible, so a police car does not get through a pinch
    /// that a normal car could not fit through.</summary>
    public static bool Emergency(Car c) => c.EmergencyRole;

    const double WaitWeight = 0.35;
    const double BlockingWeight = 1.5;
    const double EmergencyWeight = 4.0;
    const double FlipMargin = 0.4;   // re-award only past this

    /// <summary>The cost of NOT getting the resource, for one candidate.
    /// Every term is a quantity the sim already measures; the one term the
    /// design says must not be guessed (R12 disturbance) is held at 0 and
    /// named, so the auction is honest about what it does not know yet.</summary>
    private static double CostOfWaiting(Car me, int pinchM, double clearMps,
                                        double waitedS, int blockingOthers)
    {
        // Time I lose by waiting for the holder: the pinch at the speed I
        // could clear it at.
        var t = pinchM / Math.Max(1.0, clearMps);
        // R16 anti-starvation: how long I have already waited enters
        // monotonically, so a car that keeps losing becomes expensive
        // enough to win eventually.
        t += waitedS * WaitWeight;
        // How many cars I am blocking - the blocked-by graph R30 walks;
        // holding them still is what costs the fleet its flow.
        t += blockingOthers * BlockingWeight;
        // R12 disturbance (making an oncoming car brake): NO MEASURED
        // VALUE YET - 0 until the stress runs say what one brake costs.
        t += 0.0;
        if (Emergency(me)) t *= EmergencyWeight;
        return t;
    }

    /// <summary>Award the resource among its candidates. Returns the holder's
    /// uid, or 0 when the resource is uncontested. The caller has already
    /// dropped the infeasible candidates (a candidate that cannot clear the
    /// stretch while the others hold their course, or whose entry would make
    /// the others unable to stop in time) and the unresolvable ones (an
    /// award that would close a cycle in the blocked-by graph).</summary>
    public static int Award(int key, List<Car> candidates,
                            Dictionary<long, double> waitedS,
                            Dictionary<long, int> blocking,
                            int pinchM, double clearMps)
    {
        if (candidates.Count == 0) return 0;
        if (candidates.Count == 1) return candidates[0].Uid;
        var best = candidates[0];
        var bestCost = CostOfWaiting(best, pinchM, clearMps,
                                     waitedS.GetValueOrDefault(best.Uid, 0.0),
                                     blocking.GetValueOrDefault(best.Uid, 0));
        for (var i = 1; i < candidates.Count; i++)
        {
            var c = candidates[i];
            var cost = CostOfWaiting(c, pinchM, clearMps,
                                     waitedS.GetValueOrDefault(c.Uid, 0.0),
                                     blocking.GetValueOrDefault(c.Uid, 0));
            // R15: a genuine tie goes to the lower uid, deterministically.
            if (cost > bestCost + 1e-9 ||
                (Math.Abs(cost - bestCost) <= 1e-9 && c.Uid < best.Uid))
            {
                best = c;
                bestCost = cost;
            }
        }
        var prev = _holder.TryGetValue(key, out var h) ? h : 0;
        if (prev != 0 && prev != best.Uid)
        {
            Car prevCar = null;
            foreach (var x in candidates)
                if (x.Uid == prev) prevCar = x;
            if (prevCar is not null)
            {
                var prevCost = CostOfWaiting(prevCar, pinchM, clearMps,
                                             waitedS.GetValueOrDefault(prev, 0.0),
                                             blocking.GetValueOrDefault(prev, 0));
                // Stability: the incumbent keeps the stretch unless the
                // challenger is clearly more expensive, by FlipMargin.
                if (bestCost < prevCost + FlipMargin) return prev;
            }
        }
        _holder[key] = best.Uid;
        return best.Uid;
    }

    /// <summary>Drop the resources no contest is about any more: the
    /// obstruction moved, the stretch became free, a candidate left. Called
    /// once per substep with the keys still contested, so an award cannot
    /// survive the event that made it meaningless.</summary>
    public static void Keep(HashSet<int> liveKeys)
    {
        foreach (var k in _holder.Keys.ToList())
            if (!liveKeys.Contains(k)) _holder.Remove(k);
    }

    /// <summary>Who holds the stretch this pair is arguing about, 0 =
    /// uncontested. The pair loop asks this instead of comparing two cars
    /// itself, so the answer is the group's, not one pair's opinion.</summary>
    public static bool Holds(int key, long uid, List<Car> candidates,
                             Dictionary<long, double> waitedS,
                             Dictionary<long, int> blocking,
                             int pinchM, double clearMps)
    {
        return Award(key, candidates, waitedS, blocking, pinchM, clearMps) == uid;
    }

    public static void Reset()
    {
        _holder.Clear();
    }
}
