## 2026-09-03 23:34 (hauke-walden)
- Worked on: Diagnosing root cause of ~0.8-second-period display velocity spikes in motion capture data; analyzed sample arrival patterns, interpolation state (bracket drift, f-value), and raw position deltas around rt≈43.65
- Done: Confirmed the spikes are **not a client-side rendering artifact**—they originate in the simulation data itself. Found that samples occasionally move 1.6–12.7px in a single frame (~17ms), vs. normal 0.24px; one extreme jump of 12.7px at t=35.833 identified
- Tried & discarded: re-anchor hypothesis (|t - _simNow| > 1.0 check would require >1s offset, but jumps are only ~0.3m); also ruled out extrapolation-to-interpolation mode-switch artifacts (would be ~0.17m max on a curve, observed jumps are much larger)
- State: Found 551 "jump" samples (>1.6× median delta). Initial period analysis shows ~0.8–1.0s spacing between some jumps, suggestive of segment-boundary correlation (fig8 track ~15.5m/segment; at 17 m/s = ~0.9s/segment). Need to verify whether jumps coincide with progress/segment transitions in the sim; may indicate physics double-step or nav target snapping bug on segment boundary crossings. Next: cross-correlate jump times with segment/progress data from live /state polling at 60Hz.

## 2026-09-03 23:39–23:39 (hauke-walden)
- Worked on: Stuttering/jitter investigation in simulation and Godot display pipeline; analyzed motion log (~25 sec recorded with --motionlog); correlated /state data spikes with display jumps
- Done: Confirmed spikes originate in sim (not Godot interpolation); identified clear pattern: every ~0.5–1 s, car moves ~2× normal distance (exact multiples: 566mm vs 283mm, 601mm vs 304mm, etc.); found one HTTP timeout (1038ms, load avg 17) caused 6.5m teleport; established car's speed pulsation (5–20 m/s) is real driving behavior with held throttle, not artifact
- Tried & discarded: Loose jump threshold (1.6× median) — too many false positives due to natural acceleration spread; tightened analysis to identify true 2× anomalies only
- State: Root cause still open (suspected double physics step or nav correction at segment boundaries in main.py); last analysis output (551 candidates filtered to ~13 confirmed 2× jumps with timestamps) complete but code-level correlation not yet done; repos locally committed, awaiting test + push

## 2026-09-03 23:42–23:43 (hauke-walden)
- Worked on: analyzing the measurement data from the previous session to answer the user's hypothesis about whether the stutter is caused by the REST communication interface
- Done: diagnosed two separate phenomena: (1) periodic 2× position jumps in the simulation data (~every 0.5–1s)—confirmed to be in the payload itself (sim-side), not a transport issue; (2) one-off 1038ms stall under load average 17—delivery-side but likely OS-level scheduling of either process, not an HTTP-specific weakness
- State: question answered; identified that REST is not the bottleneck and WebSockets would carry the same double-stepped position values; next step is to hunt in the sim's step logic for the periodic anomaly (suspected: segment transition or navigation re-target every ~0.5–1s). No code changes made per user request.

## 2026-09-03 23:45–23:45 (hauke-walden)
- Worked on: Headless jump detection strategy—designed a high-frequency `/state` poller to capture consecutive sim frames (dt = exactly 1/60 s) and correlate position deltas with segment boundaries
- Done: Wrote `/tmp/poll_jumps.py` (records x,y per car; compares only true consecutive frames; flags deltas > 1.6× expected speed-based distance; reports segment change); script is ready to run against already-running game
- State: Script ready to execute. Hypothesis: jumps correlate with segment transitions (fig8's ~15.5m segments + 17 m/s → ~0.9s interval matches jump frequency cluster in historical data). Next: run poller for 30s against 1 car, confirm headless reproduction, then scale to multi-car stress test (6/10/14/18 cars) to check if frequency changes with load or car count.

## 2026-09-03 23:46–23:47 (hauke-walden)
- Worked on: Characterizing distance-jump anomaly in headless polling. Created `/tmp/poll_all.py` to dump all consecutive frame pairs (not just flagged jumps) with ratio, segment, and speed.
- Done: Confirmed jumps occur **outside Godot UI** (headless mode, single car). Full frame-by-frame ratio data captured: 6 seconds of polling shows ratio = 1.00 for ~97% of frames, then **exactly 2.00 for isolated frames** (e.g., t=21931.083, 21931.183). Jumps are sparse (~2.6% of frames), not global acceleration artifact.
- State: Pattern emerging: isolated single frames jump 2.0×, interspersed in otherwise-normal 1.0× motion. Next: extend polling window to visualize temporal pattern (e.g., every N frames? triggered by segment boundary?). Also need to test multi-car load effect.

## 2026-09-03 23:48–23:48 (hauke-walden)
- Worked on: Understanding the 2.00× distance ratio jumps in car motion data. Analyzed frame-by-frame state dumps showing d_cm/exp_cm ratios of exactly 1.00 (normal) and exactly 2.00 (jump) occurring ~every 12 frames. Discovered no segment change at jump moments and gaps varying from 6 to 54 frames. Examined main.py's loop structure.
- Done: Identified root cause: **physics accumulator in main.py (lines 618–624) executing two fixed-timestep substeps within a single frame when wall-clock time exceeds 17ms**. The accumulator advances physics by dt_fixed per step; when a frame runs slow (>34ms wall time), acc ≥ 2×dt_fixed, triggering `while _physics_accum >= dt_fixed: step()` twice, but frame counter increments only once → position advances 2× distance per frame.
- State: Root cause confirmed. Next: verify by examining whether frame timing correlates with jump frequency, or instrument main.py to log wall-time per frame and _steps_this_frame (already tracked at line 624) to confirm 2-step frames cause the jumps.

## 2026-09-03 23:48–23:48 (hauke-walden)
- Worked on: Diagnosing position-vs-time inconsistency in `main.py` physics loop (lines 618–624)
- Done: Located root cause—fixed-timestep accumulator executes 2 physics substeps when frame wall-time exceeds 17ms, but `frame += 1` happens once per iteration only → sim clock (`time = frame / 60`) decouples from actual physics executed. On high-load machine (load avg 17), frames frequently take 34ms, triggering 2-step frames every 0.1–1 seconds, producing exactly the observed stutter (position advances 2× while time advances 1×).
- Tried & discarded: Considered option B (cap accumulator to 1 step/frame to maintain slow-motion consistency)—rejected because it violates real-time fidelity; option A (tie sim clock to total substeps executed, not frame counter) is standard fixed-timestep design and preserves self-consistent data when wall-clock hiccups occur.
- State: Confirmed bug signature matches user complaint. Next: implement fix by changing `time = frame / 60` to `time = (total substeps executed) × dt_fixed` so exported clock matches actual physics. Need to read full loop context (lines 358–450) to locate where substep counter should be tracked and integrated into time export.

## 2026-09-03 23:49–23:49 (hauke-walden)
- Worked on: Analyzing physics timestep synchronization bug in main.py (lines 600–710). Identified that `time` export uses `frame/60.0` (counting loop iterations) while physics substeps use fixed dt_fixed with variable dt clamping, causing sim clock to desynchronize from actual physics advancement under load.
- Done: Diagnosed root cause: when a frame takes 34ms (dt clamped at 33ms), physics runs 2 substeps (car moves 2×) but exported `time` only advances 1/60, making position/time inconsistent. Confirmed freeze mode resets accumulator (line 615) but frame still increments, so clock was advancing during pause (incorrect). Found render interpolation alpha already computed (lines 688–702) between substeps.
- Tried & discarded: none yet
- State: Identified fix: replace `time = frame/60.0` with `time = substeps_total * dt_fixed` (maintain counter incremented per executed substep), ensuring sim clock matches physics advancement. Must update lines 836 (both branches: smoke_test and normal), check all uses of `frame` for time-dependent logic (camera, watchdog), and verify freeze behavior (clock should now stop during pause, which is correct). Next: read full render/interpolation section and implement clock fix.

## 2026-09-03 23:50–23:50 (hauke-walden)
- Worked on: Investigating position-clock divergence in headless `/state` polling; isolated the fixed-timestep accumulator in `main.py`'s game loop
- Done: Reproduced the jump pattern without UI: pure Python poller at 545 Hz over 30 seconds on a loaded machine (LA~17) with one car on Fig8 — confirmed every ~0.5–1 s exactly one frame where position jumps 2× while clock advances 1/60 (ratio: 2.00 for jump frames, 1.00 for all others; no segment change)
- State: Root cause identified — when a frame takes >17 ms wall-clock, accumulator runs 2 physics substeps but exported time increments only by 1/60 per iteration (frame counter), not per substep. Position and time are decoupled. Camera already has alpha interpolation for this ("fix-your-timestep") but exported car state does not. Proposed fix: change sim clock to `time = _sim_steps_total × dt_fixed` (substep counter instead of frame counter) for guaranteed consistency. Awaiting user approval to implement.

## 2026-09-03 23:52–23:53 (hauke-walden)
- Worked on: Fixing time export in `/state` endpoint by replacing frame-based time with substep counter. Added `_sim_steps_total` counter to track physics substeps, incrementing on each fixed-timestep iteration. Modifying `src/main.py` lines 835–862 to export `time = _sim_steps_total * dt_fixed` instead of `frame * dt_fixed` or `frame / 60.0`.
- Done: Identified the two /state export blocks (no-car and followed-car cases) where `'time'` field is computed. Verified frame variable usage across codebase (grep). Ready to implement counter initialization and increment logic.
- State: Need to initialize `_sim_steps_total = 0` at startup, increment it inside the physics loop (before or after the substep loop at line ~606–640), then update both /state branches to use `_sim_steps_total * dt_fixed` for time. After code change, will run headless poller verification and full test suite, then restart Godot for live demonstration.

## 2026-09-03 23:53–23:53 (hauke-walden)
- Worked on: Fixing time tracking in `/Users/hauke/prj/car/src/main.py`. The issue: on slow frames (>60ms wall-clock), physics runs 2+ substeps but `time` only advanced once per frame, causing position/time decoupling seen by clients.
- Done: Added `_sim_steps_total` counter (initialized to 0 after line 360), incremented inside the physics substep loop, and replaced both `time` field assignments (lines ~365 and ~375) to use `_sim_steps_total * dt_fixed` instead of frame-based calculation. Four blocks edited successfully.
- State: Changes applied. Next: check `/wait` endpoint and test suite to verify they don't rely on frame-based time assumptions, then run e2e tests to confirm position/time consistency is restored.

## 2026-09-03 23:53–23:53 (hauke-walden)
- Worked on: Unit tests verification and headless game verifier to validate the 2-step frame fix (dist/speed/dt ratio checking)
- Done: All 37 unit tests passed (excluding the deselected basic_driving test)
- State: Ready to restart the game and run the improved headless poller verifier to check: (1) dist ≤ (v_max/3.6 × dt) × 1.25 + 0.01 for all consecutive sample pairs, (2) time monotonicity, (3) sim rate ≈ wall-clock over 30 seconds. Next: execute game restart + verification run.

## 2026-09-03 23:53–23:54 (hauke-walden)
- Worked on: Post-fix verification script (`/tmp/verify_fix.py`) to validate the improved poller—checks consecutive position pairs against peak speed constraints (+25% tolerance), reports violations, and measures sim-clock vs wall-clock rate
- Done: Verification script written; ready to restart game and run against it
- State: Script ready to execute; next step is to restart game server and run `/tmp/verify_fix.py` to confirm physics violations are resolved with the improved sampling

## 2026-09-03 23:54–23:54 (hauke-walden)
- Worked on: Verified fix for collision detection in `/Users/hauke/prj/car` by restarting game, spawning car with throttle on via API, and running verifier (`/tmp/verify_fix.py`)
- Done: Verifier confirmed 0 violations in 1679 collision pair samples over 30 seconds (sim running at 0.957x speed)
- State: Fix validated; collision detection system working correctly. Project ready for next phase or deployment.

## 2026-09-03 23:55–23:55 (hauke-walden)
- Worked on: Verification of sim time/position consistency; Godot restart for visual confirmation.
- Done: Zero consistency violations in 1679 sample pairs (the periodic 2× car jumps are gone). Restarted Godot in fullscreen so you can watch the car drive smoothly. Local commit `bc006a9` with full explanation of the fix.
- State: The fix is complete and verified in headless. Godot is now running with the corrected car state feed; you can observe the car moving without the periodic glitchy 2× jumps. Ready for e2e regression suite if you want full confidence (takes ~10 min) — or ready to push to main.

## 2026-09-03 23:55–23:55 (hauke-walden)
- Worked on: Physics simulation frame timing in `car/` — replaced frame count with actual executed physics substep count (commit bc006a9)
- Done: Fix implemented and committed locally; verification: 0 violations out of 1679 frame pairs (previously 44 jumps in 30s); 37/37 unit tests passing; Godot running fullscreen with car executing figure-8 loops
- State: Awaiting user verification ("tested") before pushing; E2E test suite (10 min) available as next step if needed

