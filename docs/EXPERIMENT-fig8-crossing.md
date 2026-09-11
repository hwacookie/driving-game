# Experiment — Fig-8 Crossing Deadlock / Collision Fix

> Created on `main` (commit baseline below) so BOTH experiment branches
> (`fix/fig8-crossing-opus48`, `fix/fig8-crossing-qwen38`) start from an
> identical state and success criterion. This document is the shared
> reference: problem, baseline measurement, and the pass criterion. It is
> NOT modified by the fix branches (they only change code).

## 1. The problem

On the `basic` test map's two **ground-level degree-4 figure-8 crossings**
— tile (2,3) `fig8_xing` (WITH Vorfahrt signs) and tile (3,3)
`fig8_xing_plain` (NO signs → right-before-left) — a dense fleet driving
through the shared crossing at grade does not behave correctly:

- Cars **block each other** on the junction (mutual yielding / gridlock:
  two or more cars stop and neither resolves, or the whole crossing wedges).
- **Accidents happen**: two body boxes end up in contact
  (`in_contact` / the collision `Resolve` rollback fires), which the test
  treats as a crash.

Both failure modes come from the crossing right-of-way + avoidance logic in
`DrivingGame.Sim/CarCollisions.cs` (Driving Ruleset R2–R7, R14–R16 in
`docs/DRIVING_MANEUVERS.md` §8) under real simultaneous multi-car load — the
single-pair and small-fleet tests pass, but 50 cars / 25 per direction on one
shared crossing still produce contact and/or standstill.

## 2. How it is measured (test 23 & 24)

`tests/test_turning.py` scenarios, driven via the live REST API by
`scripts/run_e2e.sh --tests <23|24>`:

- **Test 23 — `fig8_xing`**: signed crossing (Vorfahrt), 50 cars,
  25/direction, interleaved, all vehicle classes, accelerator held.
- **Test 24 — `fig8_xing_plain`**: un-signed crossing (right-before-left),
  same fleet setup.

Run mechanics (`run_crossing_stress_test`): spawn 50 cars evenly around the
48-segment crossing loop (alternating direction), hold the throttle on every
car, then poll `GET /state` and run for **up to 300 s of sim time** or until
the **FIRST crash** (any car reporting `in_contact`). Every 10 sim-s it logs
`moving X/N` (cars faster than 3.6 km/h) — the gridlock indicator.

## 3. Success criterion ("the bug is fixed")

A fix is accepted only when BOTH of the following hold, for BOTH test 23 and
test 24:

1. **No crash for the full window.** The fleet drives the entire
   300 s of sim time with **zero** `in_contact` events (the scenario's own
   pass condition: `passed == true`, no `Resolve` rollback). This is the
   hard, test-enforced criterion.
2. **No sustained gridlock.** Traffic keeps flowing through the crossing —
   the `moving X/N` count must not collapse to a small number and stay there
   (no permanent standstill of the fleet). Concretely: after the initial
   spin-up, `moving` stays a clear majority of the fleet for the rest of the
   window (cars may briefly stop to yield, but the crossing must not wedge).

Additionally (must not regress):

3. **No new physics violations** — no off-road, no wrong-side beyond the
   known fig-8 baseline noise, no jitter/teleport (the standard stress
   invariants the suite already checks).
4. **Existing suite stays green** — tests 1–21 unaffected by the change.

The experiment compares how long each model (Opus 4.8 vs Qwen 3.8) takes to
reach criteria 1+2 on both crossings from the identical baseline below.

## 4. Baseline (BEFORE any fix)

Captured on `main` before the two experiment branches diverge.

- **Prior recorded result** (`tests/turning_results.json`, 2026-09-10):
  `fig8_xing|crossing` → `passed: false` ("timed out: never reached
  expected segment None").

### 4.1 Fresh baseline run — test 23 (`fig8_xing`, WITH signs)

Run 2026-09-11 (`scripts/run_e2e.sh --tests 23`, fresh C# host, 50 cars).
- **Result: FAIL** (crash).
- **First crash at: t = 7.6 s** — car 8 contacted car 32.
- Moving count trajectory: t=0 s → 50/50; crash before the first 10 s report.
- Log: `tests/run_test_T_001_23_20260911_233304.log`.

### 4.2 Fresh baseline run — test 24 (`fig8_xing_plain`, right-before-left)

Run 2026-09-11 (`scripts/run_e2e.sh --tests 24`, fresh C# host, 50 cars).
- **Result: FAIL** (crash).
- **First crash at: t = 15.6 s** — car 18 contacted car 20.
- Moving count trajectory: t=0 s → 50/50; t=10 s → 32/50 (fleet already
  bunching/yielding at the crossing) → crash at 15.6 s.
- Log: `tests/run_test_T_002_24_20260911_233333.log`.

### 4.3 Baseline summary

| Test | Crossing | Result | First crash | Moving @10 s |
|------|----------|--------|-------------|--------------|
| 23 `fig8_xing` | signed (Vorfahrt) | FAIL | 7.6 s (car 8↔32) | n/a (crashed <10 s) |
| 24 `fig8_xing_plain` | unsigned (RBL) | FAIL | 15.6 s (car 18↔20) | 32/50 |

Both crossings crash within ~8–16 s of sim time, well short of the 300 s
window. The signed crossing fails FASTER than the unsigned one. Neither
reaches sustained flow. This is the state both fix branches start from.

## 5. Experiment protocol

1. Baseline captured on `main` (this document + committed).
2. Two branches off the identical `main` baseline:
   `fix/fig8-crossing-opus48` (Opus 4.8) and `fix/fig8-crossing-qwen38`
   (Qwen 3.8).
3. Each branch attempts the fix independently; success = criteria in §3 met
   for both test 23 and test 24.
4. Compare wall-clock effort / iterations to reach success.
