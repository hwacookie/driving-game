# Project Rules

## Language

Regardless of whether the user writes in German or English, **respond in
English** and **write all documentation in English** (docs/, comments,
diary). This is the default: it only changes if the user gives an explicit
instruction to use a different language for a specific artifact.

## Follow explicit user instructions exactly

When the user gives a direct instruction ("do X", "don't do Y"), do ONLY that —
in one step. No side investigation, no extra analysis, no "actually let me check
first" preamble. Explicit instructions override whatever work is in progress;
stop and do exactly what was asked. If more work seems warranted, ask after
delivering what was requested.

## Debugging workflow: reproduce first, fix second

When the user reports a problem (visual glitch, wrong behavior, stutter,
broken maneuver): **do not start designing or writing code changes.**
First step is always:

1. **Reproduce** it deterministically (headless sim, `--only`/`--tests`
   single-scenario run, or a scripted REST drive on the live game).
2. **Log** what actually happens (state trace: speed, position, heading,
   blinkers, segments over time) at high enough resolution to see the
   failure.
3. **Understand** the root cause from the data - only then discuss or
   implement a fix.

No speculative fixes based on guessing what the user saw.

## Waiting for results: poll, don't sleep

In test scripts AND in my own shell commands: **never wait with a long
fixed sleep** ("sleep 25 until the server is probably up"). Instead,
poll for the actual condition and stop when it is met:

- Server/API up? -> `until curl -s /state; do sleep 1; done` (bounded).
- Cars spawned after /teleport? -> poll /state until all expected uids
  exist (commands are queued and processed on the sim's tick loop).
- Run finished / duration reached? -> pace on the SIM clock from /state
  (`st["time"]`), not wall time; a frozen sim must not burn real time.
- Short sleeps (<= 1 s) as pacing BETWEEN polls are fine - what is banned
  is the blind fixed wait whose length guesses at when something "should"
  be done.

The four manual QA scripts in `tests/` already follow this pattern;
keep new ones consistent with them.

## General rule: never do physically impossible things

The simulation must never produce motion that no real car could perform:
no teleportation, no instant heading changes, no turning radius below a
real car's mechanical minimum, no position/heading updates that fall out
of sync. This applies to game logic, physics, fallbacks, and edge cases
(junctions, dead ends, segment hand-offs) alike.

When a desired behavior would require physically impossible motion, fix
the underlying logic (e.g. brake earlier, blend offsets, smooth the
heading) instead of weakening or bypassing the checks that catch it.

This rule may ONLY be violated if the user explicitly asks for it.

## Decisions: explicit user decisions are binding

When the user makes an explicit decision or instruction (e.g. "this value
must be variable, not fixed"), it is **binding**. You may propose
alternatives and point out consequences, but you must NOT silently override
the decision with your own judgment ("actually, simpler: keep it fixed").
If you believe the decision has a problem, state the conflict explicitly
and ask before deviating. Acknowledging a correction and then implementing
the old way anyway is a violation of this rule.

## E2E tests

The test suite in `tests/test_turning.py` is the project's **end-to-end
(e2e) test**: it drives the live game (real physics, real rendering, real
road network) over the REST API on :5000. See `docs/TESTING.md` for the
full workflow. The canonical runner here is `scripts/run_e2e.sh`, which
handles the whole cycle: kills stale processes (C# host AND old Python
`src.main` - a stale Python server on :5000 silently answers curls meant
for .NET), builds + starts the C# console host (`DrivingGame.Server`) with
the test map, launches the visible Godot window (unless `--no-godot`),
launches the suite through `POST /run_test`, and streams the log to the
console:

```bash
scripts/run_e2e.sh                                  # full suite, visible
scripts/run_e2e.sh --no-godot                       # headless (no window)
scripts/run_e2e.sh --tests corner_left              # single scenario
```

When the user asks to run the e2e tests:

0. **Restart the game first.** The runner kills any running game process
   and starts a fresh one - no stale state from previous runs or debugging.
1. **Map visible.** Run WITHOUT `--no-godot` - the Godot window (with the
   map) must be open so the user can watch the car drive. (`--no-godot` is
   only for CI or when the user explicitly asks for it.)
2. **Results visible while running.** The runner streams the suite output
   to the console as it happens - no backgrounding, no discarding output.
   The per-run log file (`tests/run_test_<N>_<timestamp>.log`) can be
   re-read or grepped afterwards (long runs can exceed the tool output
   limit).

(Godot self-terminates via `--quit-on-test-done`; if it is still open
afterwards, say so - never kill it with a signal, that pops a macOS
crash-report window.)
