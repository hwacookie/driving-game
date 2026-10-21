# Driving Game

A traffic simulation — cars with a human-factors driving model on OpenStreetMap
and synthetic road networks, rendered by a Godot 4 C# client. The simulation is
the authority; the clients are pure API consumers.

## Two clients

| | |
|---|---|
| **Simulationsviewer** | the Godot window we run today (`Main.tscn` + `MapRenderer.cs`). It shows the **live** sim: the map, the cars, the camera following the car or pinned on a hotspot |
| **Simulationsreviewer** | a **separate client, its own window** — specified, not yet built. It shows a **tape frame at a cursor**: play, pause, stop, record, rewind, fast-forward, its own zoom and crop, a scrub bar with the fluidity chart and its event markers, detail panels for up to three cars, a **readout label** naming the tape, the frame, the sim time and the camera (`tape_T_…txt · frame 743/820 · SIM 12.383 s · ▶ 4× · head 820` / `cam x y zoom · view · units`) and a **whole-window screenshot** named against the tape and the frame. See [docs/SIMULATIONS_REVIEWER.md](docs/SIMULATIONS_REVIEWER.md) |

They are independent and long-lived: a new run is a new tape, not a new process.
Neither client restarts because the sim host restarted — a dead host is a
connection state, and both clients show
`⚠ No connection to Sim Engine — retrying …` and re-acquire the host when one
answers. Only a change to the renderer forces a rebuild.

The reviewer's whole point is that the two views are not the same picture: the
viewer lerps and trails the sim by 100 ms to stay stutter-free, the reviewer
shows recorded substeps — smoothed in ▶ play (bracketed by two real frames),
exact in step mode.

## Running it

```bash
dotnet build DrivingGame.Server/DrivingGame.Server.csproj   # the sim + API host
dotnet run  --project DrivingGame.Server --map basic --warp 4   # REST API on :5001
```

```bash
scripts/run_e2e.sh --warp 4 --tests 17-18     # e2e: small window, verdicts fast
scripts/run_e2e.sh --realtime --maximized --zoom 5 --center 100 220 --tests 18
```

Two run modes, never mixed (see `AGENTS.md`):

- **watch / review** — fullscreen, camera on the hotspot, **`/freeze` while
  staging**, spawn every car, poll until all uids exist, then unfreeze. Staging
  while the sim ticks burns the first seconds of the manoeuvre.
- **e2e tests** — smaller window, `--warp 4`, through the named scenarios as
  fast as possible.

If a request could be either, **ask**.

## The record & playback layer (built)

Every e2e run also writes a **tape**: every substep, every car — pose, blinkers,
braking, the wrong-side verdict, crash count, the rule that owns that car's speed
cap, and the fleet's **fluidity** at that substep.

```
tests/run_test_T_<nnn>_<sel>_<stamp>.log    the suite's verdicts
tests/tape_T_<nnn>_<sel>_<stamp>.txt        the substep tape of the same run
```

The clock is **substeps**, never render frames (`simTime = _simStepsTotal * DtFixed`
— a slow wall-clock frame runs 2 substeps). Timestamps in the tape are **sim
time**, so a tape recorded at `--warp 4` replays at the correct speed; the warp
only changed how fast sim time advanced on the wall clock.

| endpoint | meaning |
|---|---|
| `POST /tape` | start recording (idempotent) |
| `GET /tape/index` | step range + the steps that carry an event |
| `POST /tape/frame {"step": N}` | one recorded substep (`-1` = last) |
| `GET /tape/list` | the recorded runs on disk, newest first |
| `POST /tape/load {"path": …}` | load a saved run for scrubbing |
| `POST /tape/save {"path": …}` | write the whole tape to a file |

Measured, not assumed: the tape costs **0.07–0.08 µs/car/substep** at 50 cars —
about 0.04% of the ~200 µs/car/substep budget.

## Documentation

| | |
|---|---|
| [docs/SIMULATIONS_REVIEWER.md](docs/SIMULATIONS_REVIEWER.md) | **the review client, full specification** — controls, scrub bar, the fluidity chart with its event markers, the three car slots, the connection layer |
| [docs/RECORD_PLAYBACK.md](docs/RECORD_PLAYBACK.md) | the tape, the clock, the endpoints, the per-substep fluidity |
| [docs/DRIVING_MANEUVERS.md](docs/DRIVING_MANEUVERS.md) | the driving ruleset (R1…R36) — including R36, "the oncoming lane is used for the minimum" |
| [docs/TESTING.md](docs/TESTING.md) | the e2e workflow |
| [docs/REST_API.md](docs/REST_API.md) | the API surface |
| [docs/UNITS.md](docs/UNITS.md) | which unit which name carries |
| [AGENTS.md](AGENTS.md) | the project rules that bind every change |
