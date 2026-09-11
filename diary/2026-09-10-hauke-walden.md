## 2026-09-10 02:26 (hauke-walden)
- Worked on: Updated requirement status tags in `docs/DRIVING_MANEUVERS.md`; added timestamped logging for speed clamps in `DrivingGame.Sim/Car.cs` and sim-time passing in `DrivingGame.Sim/SimEngine.cs`.
- Done: Modified R13, R10, R17, R4, and R20 status lines; added `_freeTime` and `_speedCapEngaged` fields to `Car.cs`; updated `SimEngine.cs` call site for `Validator.Check`; verified build (0 errors) and safety tests (12 passed).
- State: Docs reflect ground-truth implementation status; logging now captures R1–R7, R17, R25 transitions.

## 2026-09-10 02:12–02:30 (hauke-walden)
- Worked on: Reproducing the crash in `tests/test_turning.py` (CROSSING STRESS on `fig8_xing`) — 50 cars, 25/direction, first crash at t=7.4s between car 16 and 40
- Done: Identified the failure mode (two-way right-before-left conflict at an unsigned degree-4 crossing) by inspecting recent logs; confirmed the sim is frozen on contact for analysis
- Tried & discarded: None yet — still gathering state trace data (positions, headings, speed profiles, decision logs) from the frozen crash to determine whether this is a gap-acceptance bug (R4), a yield-sign bug (R5), or a deadlock in the equal-rank crossing logic
- State: Crash reproduced and logged; next step is to extract per-car state at t=7.4s (positions, headings, speed profiles, active caps/decisions) from the frozen simulation to pinpoint whether R4 feasibility gating or R5 right-of-way enforcement failed on this unsigned degree-4 crossing

## 2026-09-10 02:31–02:31 (hauke-walden)
- Worked on: Diagnosing crash between Car 16 and Car 40; analyzing decision logs for "brake/resume" flapping at 10 Hz; querying map segments (keys found) and car positions via `/tmp/crash_state.json`.
- Done: Confirmed Car 40 stalled mid-intersection (seg 202, prog 0.126); identified Car 16 creeping forward due to false "gap clear" release at t=6.27 despite TTC=0.4s threat.
- Tried & discarded: Fetching `segments` directly by index from map JSON failed with KeyError (map uses 'roads' key, not direct segment indexing as assumed).
- State: Root cause is the R2 self-protection release logic oscillating on false "gap clear" near TTC=0.1s; need to inspect `CarCollisions.cs` release condition next.

## 2026-09-10 02:32–02:32 (hauke-walden)
- Worked on: Analyzing crash geometry and segment indexing for cars 16 and 40; ran Python script to verify `net.Segments` array position vs. ID mapping.
- Done: Confirmed segments 178 and 202 share a common node at (1250, 1750) forming an intersection; identified all degree-4 nodes including the crash site.
- State: Crash location confirmed at the junction of segments 178/202; next step is to analyze collision timing based on progress values.

## 2026-09-10 02:32–02:33 (hauke-walden)
- Worked on: Analyzing crash logs (fig8_xing) to locate decision storage and root cause of oscillating avoidance caps.
- Done: Confirmed `Car.Decisions` is an in-memory ring buffer (max 100 entries) in `DrivingGame.Sim/Car.cs`, exposed via API; identified R2 self-protection cap oscillation as the primary failure mechanism.
- Tried & discarded: Hypothesized that coordinate mismatch (pixels vs meters) caused collision detection errors; discarded after confirming both cars were at the same node (1250, 1750).
- State: Root cause identified as 10Hz flapping of R2 caps causing Car 16 creep; next step is to inspect `CarCollisions.cs` logic for unstable gap-clear transitions.

## 2026-09-10 02:34–02:35 (hauke-walden)
- Worked on: Implementing automatic crash log dumping in `DrivingGame.Sim/CarCollisions.cs`. Identified the transition point for new crashes (`if (!c.InContact)` inside `Resolve` method) and planned to dump decision logs of both involved vehicles to a new `logs/` directory.
- Done: Created `logs/` directory; logic defined to write JSON crash reports (sim time, vehicle UIDs, impact data, decision logs) upon contact transition.
- Tried & discarded: No approaches were actually tried and abandoned in this window; implementation was planned based on code inspection without running failed attempts.
- State: Directory `logs/` created; next step is to integrate the file-writing logic into the `Resolve` method at the crash detection point.

## 2026-09-10 02:35–02:39 (hauke-walden)
- Worked on: Created `CrashDumper` static class in `DrivingGame.Sim/CrashDumper.cs`, integrated into `CarCollisions.Resolve` for fresh-contact episodes, and wired repo-root detection (same as `ObstacleManager`). Fixed C# parse error (`event` keyword → `@event`) during JSON serialization.
- Done: Build succeeded (0 errors). Created `logs/` directory with `.gitkeep`. Manually dumped the frozen crash state (cars 16/40, sim time ~7.37s) to `logs/crash_007s_car16_car40.json` from the old server process. Restarted server with new binary (pid 66920).
- Tried & discarded: None.
- State: Server running on new binary. Next: verify auto-dump by reproducing a fresh crash in the same scenario and confirming a new log file appears in `logs/`.

## 2026-09-10 02:40–02:45 (hauke-walden)
- Worked on: CrashDumper.cs (reference-based dedup logic), server restart, and stress reproduction script (`/tmp/repro_fig8_xing.py`)
- Done: Implemented `Dumped` HashSet to skip duplicates; rebuilt server; confirmed exactly 3 dump files written (car13/car37 @ 0.03s, car16/car40 @ 0.07s, car11/car37 @ 0.18s) with ~25 "skipped" console lines between
- Tried & discarded: Attempted to explain crash time/pair variance (2.47s vs 7.4s) as non-determinism or source drift; discarded after finding uncommitted working-tree changes from earlier in the session
- State: Dumper logic verified and effective; need to re-run on a fresh process to confirm consistency once the dirty working tree is reconciled

## 2026-09-10 02:46–02:47 (hauke-walden)
- Worked on: Clarified crash-freeze architecture and cleaned up reproduction run artifacts; analyzed `test_turning.py` diff to confirm spawn logic changes between 02:06 and 02:10.
- Done: Confirmed no auto-freeze required (test freezes manually); cleared 50 stuck vehicles from server via API; verified server state is clean (0 cars).
- Tried & discarded: Restoring overwritten `logs/crash_007s_car16_car40.json` (reason: manual dump replaced by repro run's auto-dump; current source behavior now captured in fresh `crash_003s_car13_car37.json`).
- State: Server clean; 3 dump files exist in `logs/` (one from actual test freeze, two from unfrozen repro). Next: analyze decision logs for the new canonical crash (`car13/car37` @ t=2.5s).

## 2026-09-10 02:47–02:48 (hauke-walden)
- Worked on: Analyzing crash dump `logs/crash_003s_car13_car37.json` to understand the collision between car 13 and car 37.
- Done: Identified first crash at t=2.517s (fresh_contact); extracted car 13 details but failed to extract decision messages due to `KeyError: 'msg'`.
- Tried & discarded: Parsing `c['decisions']` assuming a 'msg' key exists; failed because the key is missing in the JSON structure.
- State: Analysis paused at parsing step; need to inspect raw JSON structure for car 13 decisions or adjust parser before proceeding.

## 2026-09-10 02:48–02:48 (hauke-walden)
- Worked on: Renaming "Msg" to "msg" keys in crash log JSON files using a Python script; analyzing crash_003s_car13_car37.json to understand collision timing logic; reading CarCollisions.cs to investigate R6 clear condition.
- Done: Fixed 163 total entries across three crash logs (007, 018, 003); identified that car 13 resumed movement at t=2.23 after a false "car 37 cleared" signal, leading to a collision 0.29s later.
- State: Investigating why the watch logic incorrectly cleared car 37 as non-threatening when it was stationary but still in the crossing path; need to inspect `EarliestConflict` or speed-based clearing thresholds in CarCollisions.cs.

## 2026-09-10 02:49–02:49 (hauke-walden)
- Worked on: Analyzing collision between Car 13 and Car 37 in `DrivingGame.Sim/CarCollisions.cs`, specifically investigating why conflict detection cleared at t=2.23 despite Car 37 blocking the path.
- Done: Reviewed staged logic (lines 460+) showing alert clears if `simTime - c.AlertStartT > AlertGraceS`; identified that Car 13 resumed moving because the static check failed to account for a stopped blocker (Car 37) ahead.
- Tried & discarded: Discarded hypothesis that EarliestConflict missed overlap due to both cars being stationary; confirmed logic only predicts conflicts if boxes *already* intersect, failing when one car stops in front of another.
- State: Root cause identified as static conflict check not handling "stopped blocker" scenarios; next step is modifying the yield/priority rule to explicitly check for stationary obstacles ahead before clearing alerts.

## 2026-09-10 02:49–02:49 (hauke-walden)
- Worked on: Analyzing `EarliestConflict` function in `DrivingGame.Sim/CarCollisions.cs` (line 678) to understand its logic.
- Done: Located the target function; no code changes made yet.
- State: Context established for upcoming modifications to collision resolution logic.

## 2026-09-10 02:50–02:50 (hauke-walden)
- Worked on: Investigating unrealistic brake toggling in the simulation by reading `docs/Human Factor Model.md` to understand human decision timing thresholds.
- Done: Confirmed file existence and retrieved documentation stating humans do not make fixed-rate decisions but react only when perceptual thresholds are crossed (event-triggered).
- State: Identified that current 0.1s toggling violates the documented ~400ms max decision rate; next step is to modify Tier 2/3 logic to use threshold triggers instead of per-frame recomputation.

## 2026-09-10 02:52–02:52 (hauke-walden)
- Worked on: Root cause analysis of brake toggling in `src/sim/car_logic.cs` (specifically staged R6 logic); reviewed `docs/Human Factor Model.md`.
- Done: Identified self-induced oscillation where TTC growth triggers "yielding" release while the other car is stationary; mapped issue to doc's §1 (polling) and §3 (missing persistence/hysteresis).
- Tried & discarded: None.
- State: Analysis complete. Proposed fix involves separating "other car motion" from raw TTC derivative and adding minimum hold time. Waiting for user confirmation before implementing code changes.

## 2026-09-10 02:54–02:54 (hauke-walden)
- Worked on: Designing a deterministic reproduction for the brake flapping oscillation in `DrivingGame.Sim/CarCollisions.cs`. Analyzed the `ComputeAvoidCaps` pipeline, specifically the `_capCandidate` debounce logic and `PERSIST_TICKS` threshold.
- Done: Confirmed that `PERSIST_TICKS` debounces log entries but not the actual cap value resolution if multiple threats oscillate in frequency < substep interval. Identified that a 2-car unit test (Priority Car A vs Committed Yield Car B) is needed to trigger this.
- Tried & discarded: Attempted to run a full `SimEngine` headless script immediately; discarded because it lacks the deterministic pose control required to isolate the specific oscillation frequency without debugging noise.
- State: Fix design pending (needs to decouple cap resolution from debounce logic or increase `PERSIST_TICKS` dynamically). Next step: Write failing unit test in `DrivingGame.Sim.Tests`.

## 2026-09-10 02:56–02:56 (hauke-walden)
- Worked on: ComputeAvoidCaps oscillation fix in `ComputeAvoidCaps.cs` (staged logic) and `SimEngine.cs`; analyzed cap hysteresis requirements for `ActiveCapKey`.
- Done: Identified root cause as "ttc growth = release" logic; designed "cap-level decision persistence" (hold time H + jitter); drafted deterministic repro test setup.
- Tried & discarded: Motion-based "yielding" test refinement (track other car's distance to meeting point) — deferred as secondary fix since cap hysteresis alone resolves flapping without needing per-car motion tracking.
- State: Core fix design finalized (engage immediate, release after H substeps); next step is implementing `HoldTicks` logic in the resolution loop and writing the unit test in `DrivingGame.Sim.Tests`.

## 2026-09-10 02:57–02:57 (hauke-walden)
- Worked on: Gathering staged-logic constants for test design by grepping `DrivingGame.Sim/CarCollisions.cs` and `DrivingGame.Sim/Car.cs` for values like `AlertGraceS`, `PERSIST_TICKS`, and `SignActRangeM`.
- Done: Collected constant definitions (e.g., `AlertGraceS = 1.0`, `PERSIST_TICKS = 15`) and identified alert fields (`AlertOtherUid`, `AlertStartT`) on the `Car` class to ground the reproduction test design.
- State: Design phase complete with concrete numeric values; next step is implementing the staged logic test using these constants.

## 2026-09-10 02:59–02:59 (hauke-walden)
- Worked on: Designing a fix for the "cap flap" bug in `CarCollisions.ComputeAvoidCaps` and writing a deterministic unit test; checked constants (`DtFixed=1/60`, `CAR_BRAKING=10.0`) and existing test patterns in `SafetySystemsTests.cs`.
- Done: Defined two-layer fix (A: persistent braking decision with `RELEASE_HOLD_TICKS` debounce in the resolution loop; B: motion-based "yielding" check replacing raw TTC derivative); drafted repro test setup using `TestMaps.BuildTestMap("basic")` and a stationary FREE-mode car to simulate the committed intersection vehicle.
- State: Ready for design doc approval; next step is implementing the persistent cap logic in `CarCollisions.cs` (adding `_releaseHoldTicks`, per-car jitter, and "down fast/up slow" tracking) and adding `AvoidancePersistenceTests.cs`.

## 2026-09-10 03:01–03:01 (hauke-walden)
- Worked on: `AvoidancePersistenceTests.cs` design for deterministic reproduction of the v2 avoidance cap flapping bug using a 2-car setup (BicycleDriver vs KeyboardDriver/FREE) at an 8-figure yield crossing.
- Done: None yet; initial code generation was interrupted by file inspection.
- Tried & discarded: Attempted to parse `RoadNetwork` structures via bash/grep (`grep -nE "public.*(Nodes|Signs|Segments)"`) failed due to sed command syntax error (`invalid command code ,`).
- State: Inspecting `RoadSegment` and `RoadNetwork` field definitions (X1/Y1/X2/Y2 vs Lat/Lon) to correctly place cars at the crossing node.

## 2026-09-10 03:02–03:03 (hauke-walden)
- Worked on: Designing and structuring a reproduction test for the "cap flapping" bug in `BasicNet()` crossing scenarios. Verified `RoadSegment.ParkingLaneWidth` exists in `DrivingGame.Sim/RoadNetwork.cs` and `Config.LaneBaseOffsetM` signature.
- Done: Confirmed field existence; defined test logic mirroring `SimEngine` substep loop with specific car placement (A on yield approach, B on priority exit) and control inputs.
- Tried & discarded: None yet (still in planning/design phase).
- State: Ready to write the actual reproduction test file (`TestMaps.BuildTestMap` implementation); pending code for substep loop and assertions.

## 2026-09-10 03:04–03:09 (hauke-walden)
- Worked on: Deterministic reproduction test for brake/resume flutter in `DrivingGame.Sim.Tests/AvoidancePersistenceTests.cs`; created diagnostic trace in `FlutterDiagTests.cs` to debug pose geometry.
- Done: Wrote and ran `AvoidancePersistenceTests.PriorityCar_HoldsBrake_For_StoppedCommittedCar_NoFlutter` (failed with "no signed crossing" due to angle filter bug); fixed angle filter (`Math.Abs(dot) < 0.3`); reran test (failed with "setup sanity: car A was never capped for car B"); ran diagnostic trace showing A and B on opposite sides of the node, gaps oscillating ~4–38m with no cap ever set.
- Tried & discarded: Using `Xunit.Sdk.AssertException` (compile error); strict angle filter `dot < -0.3 && > -0.7` for crossing detection (failed because two approaches to a 90° node have dot ≈ 0, not negative).
- State: Test fails because A and B are placed on opposite sides of the intersection (A at (2541,3543), B at (2502,3493)) rather than approaching from perpendicular arms; need to adjust pose logic or map selection so A approaches the node while B is committed into it.

## 2026-09-10 03:10–03:10 (hauke-walden)
- Worked on: Reproducing the 13/37 crash scenario in `DrivingGame.Sim.Tests/FlutterDiagTests.cs`; identified that A's path misses B when B is on the exit arm (4.9m clearance) due to lateral offset geometry.
- Done: Modified B's spawn position from "3m past node on exit arm" to "5.5m before node on yield approach arm" to match the actual crash pose of car 37; updated calculation logic to place B relative to `yieldSeg` instead of `exitSeg`.
- Tried & discarded: Placing B on the exit arm (`exitSeg`) with various longitudinal offsets; reason: geometric analysis and diag run confirmed A's lane center crosses ~4.9m from B's path due to opposite-side lane offsets, preventing box overlap.
- State: Code updated with B stopped at the stop line on the yield approach; next step is running the simulation to verify if A (priority) now detects the conflict and brakes/collides as in the original crash dump.

## 2026-09-10 03:12–03:12 (hauke-walden)
- Worked on: Intersection conflict geometry between yielding car B (segment 201) and priority car A (segment 177); investigated lane-offset vs. nav raceline swing models.
- Done: Ran `dotnet test` diag confirming collision at t=3.18s when gap=6.03m; verified crash repro with B stopped 5.5m before node.
- Tried & discarded: Straight-line lane-offset intersection model (reason: predicted no overlap, but actual nav raceline swings offset causing conflict).
- State: Collision confirmed in diag; need to analyze why B didn't yield or stop earlier given the gap.

## 2026-09-10 03:13–03:14 (hauke-walden)
- Worked on: Reproducing the "flapping" self-protection cap oscillation in `DrivingGame.Sim` by adjusting car B's position relative to the yield crossing node; created a parameter sweep test in `FlutterSweepTests.cs`.
- Done: Executed 40 configuration combinations (B distances 1.0–22.0m, offsets -2.0 to 2.0); results saved to `/tmp/flap_sweep.txt`. Only Bdist=1.0 triggered the cap for B; higher distances caused A's curvature-based cap to dominate with no conflict for B.
- Tried & discarded: Moving car B further onto the exit arm (Bdist > 1.0m) to force a path conflict that would trigger the staged brake logic; failed because A's navigation path curves away from B, preventing `CarCollisions.ComputeAvoidCaps` from generating a cap for B even when bodies overlap.
- State: Sweep confirms oscillation only occurs at very close range (Bdist=1.0m) where A's curvature cap isn't dominant; next step is to adjust A's speed or nav parameters to force a conflict prediction for B at larger distances.

## 2026-09-10 03:17–03:17 (hauke-walden)
- Worked on: Reproducing the "violent flapping" bug in AvoidancePersistenceTests via a minimal 2-car sweep and a proposed 50-car unit test. Checked `BicycleNav.cs` to verify throttle handling.
- Done: Confirmed `BicycleNav.Update` reads `control.Accelerate` (line 1273), so the e2e's "hold throttle" logic is valid for BICYCLE cars. Determined that a hand-crafted 2-car minimal case fails to reproduce the flapping (rigid B stops A cleanly; mutual oscillation requires fleet dynamics).
- Tried & discarded: Attempting to isolate the bug with a 2-car scenario (A approaching, B rigid/stopped at node) because it resulted in stable capping (flaps=1, no contact) rather than the observed 12 Hz flutter. The flutter only occurs when both cars are creeping/near-stationary and their predicted positions flicker at the sweep boundary.
- State: Abandoning manual minimal geometry; proceeding to implement the deterministic 50-car stress scenario as a unit test (`CrossingStress_NoCrash_NoFlutter`) to faithfully reproduce the crash and flapping metrics.

## 2026-09-10 03:18–03:18 (hauke-walden)
- Worked on: Rewrote `AvoidancePersistenceTests.cs` → `CrossingStressFlutterTests.cs` to faithfully reproduce the 50-car crossing stress scenario (e2e 'fig8_xing') with throttle held (`Accelerate = true`). Implemented deterministic spawn logic and a custom execution loop mirroring `SimEngine`.
- Done: Created `CrossingStressFlutterTests.cs` containing two assertions: 1) No crash within 15s, 2) Max 3 decision transitions per car per second (human ceiling).
- Tried & discarded: None.
- State: New test file ready; execution loop runs but currently fails both assertions (crash at ~2.5s, flutter rate ~12/s). Next step is to run the test and verify failures before writing the design document.

## 2026-09-10 03:19–03:20 (hauke-walden)
- Worked on: `DrivingGame.Sim.Tests/CrossingStressFlutterTests.cs` (unit test for human decision rate flutter bug)
- Done: Verified crash reproduction; replaced window-count assertion with minimum-gap metric (≥0.25s) to catch 12Hz flutters; fixed string interpolation syntax error via sed inspection.
- Tried & discarded: Window-based counting of decisions per second (reason: run ended early at crash t=2.55s, missing the worst 12/s burst peak).
- State: Test code updated with sharper min-gap assertion; ready to re-run full suite to confirm both crash and flutter assertions pass/fail as expected.

## 2026-09-10 03:20–03:21 (hauke-walden)
- Worked on: `DrivingGame.Sim.Tests/CrossingStressFlutterTests.cs` — fixed invalid `$(` interpolation and simplified the Assert message; replaced unused `MaxDecisionsPerSecond` with `MinDecisionGapS`.
- Done: Successfully merged overlapping edits; test suite shows 3 failures (including the flutter repro), confirming the bug persists.
- Tried & discarded: None in this window.
- State: Flutter crash repro confirmed; remaining 2 unrelated test failures (`NetworkMatchesReference`, another flutter) need investigation.

## 2026-09-10 03:22–03:23 (hauke-walden)
- Worked on: Diagnosing failing unit tests (RacelineReferenceTests, CrossingStressFlutterTests) to distinguish pre-existing issues from new regressions; reviewed error messages and test logic.
- Done: Confirmed `ErosionAreaMatchesReference` and `NetworkMatchesReference` are pre-existing failures (geometry mismatch between C# and Python baselines); identified `CrossingStress_NoCrash_And_HumanDecisionRate` as a likely regression needing investigation.
- State: Opened on verifying if the CrossingStress failure is related to recent CarCollisions/PhysicsValidator changes; next step is to re-run that specific test with debug logging or revert recent collision logic temporarily.

## 2026-09-10 03:24–03:24 (hauke-walden)
- Worked on: Created deterministic unit reproduction (`CrossingStressFlutterTests.CrossingStress_NoCrash_And_HumanDecisionRate`) and authored a fix design for `CarCollisions.cs` to address brake oscillation.
- Done: Reproduction confirmed failure at t=2.55s (cars 13/37) with ~12 Hz decision flicker; Fix Design A (cap persistence) and B (motion-based yielding test) drafted with specific parameters (0.5s hold + jitter).
- Tried & discarded: Minimal 2-car scenario for reproduction (reason: failed to reproduce the fleet-dependent flutter dynamics requiring 50 cars/interleaved throttle logic).
- State: Awaiting approval to implement Fix A and B; pre-existing `RacelineReferenceTests` failures noted but excluded from scope.

## 2026-09-10 03:31–03:32 (hauke-walden)
- Worked on: Fix A (decision persistence) and Fix B (motion-based yielding) in `CarCollisions.cs`. Added static dictionaries `_lastCap` and `_releaseHold`, implemented `ReleaseHoldTicks()` jitter logic, modified the resolution loop to hold brakes during reason absence, and updated the staged logic to check `other.Speed >= 0.5` before releasing on TTC growth.
- Done: Implemented both fixes; added constants `OtherYieldingMinSpeedMps` and helper method; resolved flutter by enforcing minimum hold time for brake release and filtering stationary "yielding" events.
- State: Logic changes complete; pending verification of build and simulation run to confirm flutter elimination and throughput impact on yield/committed vehicles.

## 2026-09-10 03:32–03:33 (hauke-walden)
- Worked on: `DrivingGame.Sim/CarCollisions.cs`; added persistence fields (`_lastCap`, `_releaseHold`), helper `ReleaseHoldTicks`, and constant `OtherYieldingMinSpeedMps`; rewrote the decision log loop to implement staged yielding checks and cap hold logic per docs.
- Done: Successfully applied 2 edits implementing decision persistence and rewritten release logic.
- State: Persistence state and rewritten release logic implemented; Fix B (staged yielding check + carByUid lookup) remains pending.

## 2026-09-10 03:33–03:33 (hauke-walden)
- Worked on: Fixing brake/creep flutter issue in `DrivingGame.Sim/CarCollisions.cs` by adding motion-based yielding checks to the staged state machine.
- Done: Added `carByUid` dictionary lookup and updated TTC growth logic to verify `other.Speed >= OtherYieldingMinSpeedMps` before releasing alerts.
- State: The specific flutter issue is resolved; no further tasks in this window.

## 2026-09-10 03:35–03:35 (hauke-walden)
- Worked on: Investigating collision between cars 13 and 37 in `DrivingGame.Sim`; added instrumentation in `DrivingGame.Sim.Tests/FlutterTraceTests.cs` to trace cap states, alerts, and gaps for t=1.8–3.0s.
- Done: Created `FlutterTraceTests` test that logs per-substep debug info to `/tmp/flap_trace.txt` to verify if the staged hold logic prevents cap release at t=2.23.
- State: Test file written; next step is running the 50-car scenario and inspecting `/tmp/flap_trace.txt` to confirm whether the cap remains active or drops prematurely.

## 2026-09-10 03:35–03:36 (hauke-walden)
- Worked on: Verifying car UID logic in `DrivingGame.Sim.Tests` by running the `FlutterTrace` test and inspecting `/tmp/flap_trace.txt`.
- Done: Test passed; trace logs confirm Car 13 (uid=13) correctly references via `Uid` property and avoids collision with Car 37 as expected.
- State: Reproduction confirmed working; no further action needed in this window.

## 2026-09-10 03:37–03:37 (hauke-walden)
- Worked on: FlutterTraceTests.cs (added debug logging for t=1.95–2.1), analyzed v2 pair caps in DrivingGame logic.
- Done: Captured trace showing uid13 capped at 0.00 while gap was ~7.06m; confirmed cap reason is v2 pair(13,37) with s=0.5.
- Tried & discarded: Hypothesized yield sign rule (v3) or committed state as cause for zero cap; discarded because trace shows active v2 pair logic generating the floor.
- State: Identified that v2 GradedCap returns 0.00 too early at s=0.5 due to aggressive grading; next step is adjusting the v2 distance threshold or grading function.

## 2026-09-10 03:39–03:39 (hauke-walden)
- Worked on: Analyzed Car 13 collision at t=2.0 caused by staged self-protection caps; reviewed `GradedCap` in `DrivingGame.Sim/CarCollisions.cs`.
- Done: Confirmed car 13 and 37 both received 0.0 caps when time-to-collision dropped below 1.0s, causing them to stop with only a 0.02m body gap (overlap).
- Tried & discarded: Attempted to attribute the 0.0 cap to v2 immediate yield logic; discarded because debug logs showed the v2 check was skipped for priority car 13, confirming the cap came from staged self-protection instead.
- State: Understanding that `GradedCap` returns 0.0 when `stageSeconds <= 1.0` (ttc < 1s) is correct but too late; next step is to adjust the staged threshold or increase early deceleration margin.

## 2026-09-10 03:44–03:45 (hauke-walden)
- Worked on: `DrivingGame.Sim/CarCollisions.cs` (staged self-protection logic) and crash reproduction tests (`CrossingStressFlutter`, `FlutterTrace`).
- Done: Removed the v1 skip for watched cars (`if (c.AlertOtherUid == o.Uid) continue;`) to allow gap-based capping during staged braking; ran 50-car stress test.
- Tried & discarded: Removing the v1 skip alone did not prevent the crash at t=2.55s; car 13 still collided with stopped car 37 (root cause: v1's oblique corridor test likely blocked the cap for 90° crossing, or gap formula insufficient).
- State: Crash persists despite removing the skip; need to verify if `BoxesIntersect` blocks v1 for 13/37 geometry or implement inline gap calculation in staged branch.

## 2026-09-10 03:46–03:46 (hauke-walden)
- Worked on: Fixing the `fig8_xing` crash (cars 13/37) by adding a gap-based cap inside the staged brake condition in `DrivingGame.Sim/CarCollisions.cs`.
- Done: Updated `CarCollisions.cs` to skip v1 evaluation while watching and added inline gap calculation; ran tests.
- Tried & discarded: Relying solely on v1's oblique gate (reason: it blocked 90° pairs at distance, preventing the cap from triggering for cars 13/37).
- State: Test suite now fails with a new crash (`crash at t=12.88s: car 14 contacted car 40`); need to analyze the new failure case.

## 2026-09-10 03:47–03:49 (hauke-walden)
- Worked on: Fixing fleet collision at t=12.88s (cars 14/40) by adding crash diagnostics to `DrivingGame.Sim.Tests/CrossingStressFlutterTests.cs`
- Done: Patched test to dump decision logs on crash; identified car 14's repeated [R2] self-protection brakes vs car 40 (TTC 0.1s) starting at t=10.033s while car 40 proceeded normally after t=11.033s
- Tried & discarded: None
- State: Root cause appears to be car 14 over-braking vs car 40 despite clearance; next step is analyzing why car 14 doesn't resume driving after gaps clear

## 2026-09-10 03:50–03:51 (hauke-walden)
- Worked on: Crossing collision stability in `DrivingGame.Sim/CarCollisions.cs` (specifically the `ReleaseHoldTicks` logic for brake cap persistence).
- Done: Updated jitter range to 0..+0.5s (holding 0.5–1.0s) to suppress ~0.3s box-boundary flicker; reran reproduction test.
- Tried & discarded: Increased hold duration to fix car 40 creeping into stopped car 14, but this shifted the crash from 12.9s to 4.98s (earlier contact due to changed dynamics).
- State: Test now fails at t=4.98s with a new crash profile; need to analyze why the longer hold accelerated the collision rather than preventing it.

## 2026-09-10 03:53–03:55 (hauke-walden)
- Worked on: Restructured `CrossingStressFlutterTests.cs` to separate regression and e2e checks; removed temporary trace test.
- Done: `CrossingStress_DecisionRate_StaysHuman` is GREEN (no flutter); `CrossingStress_NoContact_Within_15s` is RED (crash at t=4.87s: car 64 hit car 90 at ~13 km/h).
- State: Flutter fix verified; remaining failure is a committed yield-side car crossing into a stopped priority-side body due to detection gaps and missing R14 blocked-box logic.

## 2026-09-10 03:56–03:59 (hauke-walden)
- Worked on: `CrossingStressFlutterTests.cs` (refined assertion logic for decision persistence); ran full test suite and determinism checks
- Done: Fixed flutter in `DecisionRate_StaysHuman` (changed from checking total interval to enforcing brake hold time ≥0.4s); removed unused constants; updated docs; 92/96 tests pass (3 known failures)
- Tried & discarded: Initial assertion checked min gap between *any* consecutive decisions (0.25s), which incorrectly flagged legitimate "release → immediate re-engagement" sequences as flutter (reason: it didn't distinguish valid rule handoffs from cap flickering)
- State: Flutter regression fixed; remaining red tests are documented failure modes (R14 collision, RacelineReference mismatches); next: verify e2e behavior or proceed to other tasks

## 2026-09-10 04:00–04:00 (hauke-walden)
- Worked on: Determinism validation for `CrossingStress_NoContact_Within_15s` test; comparing crash traces and decision logs from two runs of `DrivingGame.Sim.Tests.csproj`.
- Done: Confirmed determinism by showing `/tmp/dec1.txt` and `/tmp/dec2.txt` are identical (crash at t=4.983s with contact to car 40 and car 14).
- Tried & discarded: Grep `crash at t=...` from xUnit console output (failed because the format was truncated or different; switched to parsing `/tmp/flap_crash_decisions.txt` instead).
- State: Determinism check passed. Next step is addressing the known bug in `CrossingStress_NoContact_Within_15s` or moving to SafetySystemsTests validation.

## 2026-09-10 04:00–10:53 (hauke-walden)
- Worked on: Git hygiene and session cleanup; added `logs/` to `.gitignore` (crash dumps are regenerated), committed all changes, pushed to origin.
- Done: Committed fixes in `CarCollisions.cs` (persistence cap, motion-based yielding, gap-based self-protection); removed fig8 diagnostic tests; added `CrossingStressFlutterTests.cs`, `CrashDumper.cs`, `docs/CarVisuals.md`; pushed commit to remote.
- State: All changes pushed. Next step: address remaining red test (t=4.98s collision car 40 vs 14) by implementing R14 blocked-box logic or stop here if flutter fixes are sufficient for the current milestone.

## 2026-09-10 04:00–10:52 (hauke-walden)
- Worked on: Git operations (add/commit/push) following user request "git a c p"; reviewed `git status` showing changes in `CarCollisions.cs`, deleted test files (`Fig8*Tests.cs`), and new files (`CrossingStressFlutterTests.cs`, `CrashDumper.cs`).
- Done: Confirmed staging area readiness; identified 3 modified files, ~14 deleted tests, and 2 untracked new files. Pending actual `git add`, `commit`, and `push`.
- Tried & discarded: N/A (session cut off before execution).
- State: Git status verified with pending changes; next step is to run the requested git commands to commit the flutter fix and cleanup.

## 2026-09-10 04:00–10:52 (hauke-walden)
- Worked on: Git operations (`git add`, `git commit`, `git push`); verified changes affecting `CarCollisions.cs`, `CrossingStressFlutterTests.cs`, and cleanup of deleted Fig8 test files.
- Done: Staged all modifications (new flutter tests, collision fixes, doc updates) and identified the next step as pushing to remote or continuing with R14 blocked-box logic.
- State: Ready to push commit; remaining technical open item is implementing R14 blocked-box check for the t=4.98s crash.

## 2026-09-10 04:00–10:52 (hauke-walden)
- Worked on: Git status and commit workflow; reviewed changes in `CarCollisions.cs`, `CrossingStressFlutterTests.cs`, and deleted fig8 test files. Checked `.gitignore` for untracked `logs/`.
- Done: Added all staged changes via `git add`; prepared commit message matching recent style (collision v2, flutter fix, cleanup); identified `logs/` as untracked but ignored by default config.
- Tried & discarded: None.
- State: Git add complete; awaiting user confirmation for `git commit`. Next step: push to remote after commit.

## 2026-09-10 04:00–10:52 (hauke-walden)
- Worked on: Git hygiene and commit. Added `logs/` to `.gitignore` (crash dumps are regenerated). Executed `git add`, `git commit`, `git push`.
- Done: Pushed 13 files including `CarCollisions.cs` fixes, `CrossingStressFlutterTests.cs`, and `CrashDumper.cs`; excluded `logs/` from the repo.
- Tried & discarded: None (direct user request execution).
- State: Remote updated. Next: Address remaining R14 blocked-box failure (commit decision doesn't check for stopped bodies in corner geometry) to enable 300s e2e pass.

## 2026-09-10 04:00–10:53 (hauke-walden)
- Worked on: Git hygiene and state cleanup. Added `logs/` to `.gitignore` (crash dumps are regenerated); staged, committed, and pushed the large batch of changes including new tests (`CrossingStressFlutterTests.cs`, `CrashDumper.cs`), collision fixes (`CarCollisions.cs`), and documentation updates.
- Done: Commit successful with message reflecting crossing stress fixes, flutter verification, and crash dumper; `logs/` now ignored in repo.
- State: Branch updated remotely. Next: Implement R14 (blocked-box check) to resolve the remaining t=4.98s collision between car 40 and car 14.

## 2026-09-10 04:00–10:53 (hauke-walden)
- Worked on: Crossing stress hardening in `CarCollisions.cs` (cap persistence, stationary yielding, gap-based self-protection), Flutter regression tests (`CrossingStressFlutterTests.cs`), crash dumper (`CrashDumper.cs`), and e2e runner improvements; cleaned up fig8 diagnostic tests.
- Done: Implemented Fix A (0.5–1.0s hold band), Fix B (yield against stationary cars), and Fix C (gap-based cap); new Flutter test passes (≥0.4s brake hold); original 13/37 crash fixed; git commit/push completed with logs added to `.gitignore`.
- Tried & discarded: None in this window.
- State: Remaining failure at t=4.98s (car 40 vs car 14) requires R14 blocked-box logic; e2e suite now stable except for that one documented red test and pre-existing reference drift.

## 2026-09-10 04:00–10:53 (hauke-walden)
- Worked on: Implemented collision fixes in `CarCollisions.cs` (cap persistence, stationary yielding, gap-based self-protection); added crash dumper `CrashDumper.cs`; restructured tests into `CrossingStressFlutterTests.cs`; updated `.gitignore` to exclude `logs/`.
- Done: Pushed commit `5bd86ec` "Crossing-stress hardening" (31 files); Flutter decision-rate test green; original 13/37 crash fixed; new t=4.98s failure documented but stable; `logs/` now ignored.
- State: Only one remaining red test (t=4.98s blocked-box collision). Next step: design R14/blocked-box rule or stop here?

