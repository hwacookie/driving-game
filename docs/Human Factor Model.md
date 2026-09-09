# Human Factor Model — Driver Decision Timing & Experience Parameters

Supplementary notes for the fig-8 lemniscate crossing simulation ruleset. This document covers how to make car behavior feel human: decision frequency, perception-reaction latency, decision persistence, and experience-based parameterization (novice vs. expert drivers).

## 1. How often does a human "decide"?

There is no fixed clock rate for human driving decisions — real decisions are **event-triggered**, not polled on a fixed schedule. This is the core idea behind the best-known psycho-physical car-following model (Wiedemann, used in VISSIM): a driver does not continuously re-evaluate at a fixed frequency, but reacts only once a perceptual threshold is crossed (e.g. a noticeable change in relative speed/distance to the vehicle ahead); between such threshold crossings the driver simply keeps doing what they were doing ([Wiedemann model calibration study](https://ascelibrary.org/doi/abs/10.1061/JTEPBS.0000264)).

**Implication for the simulation:** the safety/reflex layer (Tier 0/1: R1, R2, R6, R7) should run every frame at 60 Hz, since it is the hard physical safety net, not "thinking." The tactical decision layer (Tier 2/3: R3–R5, R9, R12) should be re-evaluated on a much coarser, threshold-triggered basis — not recomputed from scratch every frame.

Still, there are citable upper bounds on human decision rate:

- For the **simplest possible** decisions (no ambiguity, single stimulus), human processing tops out at roughly **2.5 correct decisions per second** — i.e. one decision every ~400 ms, and that is already the theoretical maximum under lab conditions ([Debecker & Desmedt, cited in Cognitive Workload and the Driver](https://www.diva-portal.org/smash/get/diva2:196947/FULLTEXT01.pdf)).
- The initial perception phase (stimulus recognition) begins as early as 80–100 ms after stimulus onset, but a full cognitive cycle (perception → decision) takes considerably longer than this initial impulse ([The Timing of the Cognitive Cycle](https://pmc.ncbi.nlm.nih.gov/articles/PMC3081809/)).
- Michon's hierarchical model of driving (strategic / tactical / operational) maps well onto the rule tiers: operational corrections (steering, throttle/brake modulation) run quasi-continuously, tactical decisions (right-of-way, entering a crossing, avoidance maneuvers) operate on the order of seconds, and strategic decisions (route choice) on the order of minutes ([Michon, 1985](https://www.jamichon.nl/jam_writings/1985_criticial_view.pdf)).

**Recommendation:** re-evaluate Tier 2/3 decisions roughly every 200–500 ms, or better, on perceptual-threshold triggers, while Tier 0/1 runs at the full 60 Hz simulation rate.

## 2. Perception-reaction time (how long until a reaction starts)

Solid numbers exist here, but with a wide range depending on how expected the event is:

- **Expected/familiar event** (e.g. brake lights on a car you were already watching): 85–95% of drivers respond within ~1.5 s ([TRL PPR313](https://www.trl.co.uk/uploads/trl/documents/PPR313_new.pdf)).
- **Unexpected event**: reported values range from 0.9 s (trained/athletic drivers) to 1.6 s (95th-percentile drivers) ([NTL/DOT study](https://rosap.ntl.bts.gov/view/dot/33780/dot_33780_DS1.pdf)).
- The **conservative design value** used in traffic engineering standards (AASHTO) is 2.5 s — deliberately a worst-case value covering nearly all drivers in nearly all situations, not an average.

**Recommendation:** don't use a single fixed reaction time. Sample from a distribution: median ~0.8–1.0 s for expected situations (a crossing conflict the driver was already tracking), with a right-skewed tail extending to 2–2.5 s for surprising events (a vehicle suddenly appearing, or a misjudged R4 entry decision).

## 3. Decision persistence (how long a decision is held before reconsidering)

Again, no fixed timer in the literature — persistence is **threshold-based**: a driver keeps their current decision until a new, noticeable discrepancy appears (Wiedemann action points). The existing R6 rule (staged priority response, ~1 s hysteresis before a priority car brakes) already reflects exactly this principle.

**Recommendation:** generalize this pattern to all Tier 2/3 decisions, not just R6: **decision + minimum hold time (~0.5–1.5 s) + threshold-triggered re-evaluation afterward**, instead of re-rolling the decision every frame.

## 4. Novice vs. experienced drivers

A hazard-perception study provides a concrete, citable contrast ([The neural basis of hazard perception differences between novice and experienced drivers](https://pmc.ncbi.nlm.nih.gov/articles/PMC7257253/)):

| Metric | Experienced drivers | Novice drivers |
|---|---|---|
| Hazard reaction time | 1.32 ± 1.09 s | 3.58 ± 1.45 s |
| Missed hazards (miss rate) | 11.4% | 39.7% |

That's roughly a **2.7×** difference in reaction time and nearly a **4×** difference in miss rate — a surprisingly large but well-documented gap.

**Recommendation:** derive a single per-driver "experience" parameter (0–1) that jointly scales several things, rather than only reaction time:

- **Reaction time distribution**: mean from ~1.3 s (expert) to ~3.5 s (novice), with novices also showing higher variance.
- **Perception threshold** (how large a discrepancy must be before a situation is even recognized as relevant, relevant to R4/R9): higher threshold for novices → they notice conflicts later and then react more abruptly (feeds into higher braking intensity per R10/R11 when detected late).
- **Miss probability**: with some probability, a novice driver misses a hazard entirely at first, sliding closer to a Tier-1 situation that R6/R7 then must catch — a good stress test for the safety layer.
- **Decision stability under load**: under cognitive overload (multiple simultaneously relevant rules/conflicts), studies show extended reaction times and a narrowing of attention onto the most salient stimulus ([Cognitive load and task switching in drivers](https://www.sciencedirect.com/science/article/pii/S1369847824003073)). This is a nice additional justification for the tier system: it can double as a model of what an overloaded driver actually perceives, not just a priority-resolution mechanism — under high load, only Tier 0/1 conflicts may register at all, while Tier 3 courtesy behavior (R12) is the first thing to drop.

## 5. Proposed 60 Hz architecture

1. **Physics/safety layer (R1, R2, R7)**: every frame (60 Hz) — this is not "thinking," it's the hard simulation boundary.
2. **Tactical decision layer (R3–R6, R9, R12)**: base update interval ~200–500 ms, additionally threshold-triggered (Wiedemann-style) on noticeable changes in TTC/distance/speed — no fixed clock.
3. **Reaction time as a delay, not an update rate**: insert a stochastic latency between "situation recognized" and "action begins" (distribution as above, scaled by experience).
4. **Decision persistence**: minimum hold time ~0.5–1.5 s (as in the existing R6), plus per-vehicle jitter so cars don't all "change their mind" in sync — otherwise the fleet looks robotic and uniform.
5. **Experience parameter** per vehicle, jointly scaling reaction-time mean/variance, perception threshold, and miss probability, anchored at the two extremes above (1.3 s / 11% vs. 3.5 s / 40%).
