# REST API Documentation

> **Status 2026-10-24 (code-verified):** complete against
> `DrivingGame.Api/GameApi.cs` — **34 registered handlers over 32 route
> templates** (`GET`+`POST /run_test`, `GET`/`POST /obstacles`,
> `DELETE /obstacles/{obId}`), every one of them in the table below. Two were
> added on 2026-10-24 for the per-test camera focus (`POST /focus`,
> `POST /test_result`) — see "Camera focus". Route names and payload keys were read from the source,
> not from an older copy of this file; the record & playback family is
> summarised here and specified in [RECORD_PLAYBACK.md](RECORD_PLAYBACK.md).

## Overview

The game includes an optional REST API for remote control and automated testing. This allows you to:
- Control the car programmatically
- Monitor game state in real-time
- Automate testing scenarios
- Freeze / resume the simulation
- Toggle features remotely

## Starting the API

The API is **always on** in both hosts - there is no `--api` flag any more:

```bash
dotnet run --project DrivingGame.Server -- --map basic   # console host, REST on 127.0.0.1:5001
dotnet run --project DrivingGame.Server -- --port 5007   # a free port (e.g. for a screenshot run)

# or embedded in the Godot client (one process, sim in-process):
"/Applications/Godot_mono.app/Contents/MacOS/Godot" --path . -- \
      --source embedded --map basic --port 5001
```

The API listens on `127.0.0.1:<port>` (localhost only, no auth). Port 5000 is
deliberately unused - macOS AirPlay Receiver squats there. A squatted port is
reported (`embedded REST API FAILED … Address already in use`) and does NOT
kill the game, but external test injection is then unavailable in that process.

## Endpoints

Overview (implemented once in `DrivingGame.Api/GameApi.cs`, wired by both
hosts):

| Method | Path | Purpose |
|--------|------|---------|
| GET | `/health` | liveness |
| GET | `/state` | full state of all cars + sim clock + pacing |
| GET | `/map` | layer-2 geometry export (see the payload below) |
| GET | `/start_points` | named start points (test maps) |
| GET | `/segment_names` | the `{name: index}` map of symbolic segment anchors |
| GET | `/segment/by/{name}` | resolve one anchor by name (400 `ambiguous` + candidates, 404 unknown) |
| GET | `/segment/{idx}` | one segment's endpoints/length/width/oneway/nodes |
| GET | `/car/{uid}/decisions` | that car's rule log (events, newest last) |
| POST | `/control` | sustained keys + one-shot `hazard` |
| POST | `/reset` | all control inputs false |
| POST | `/teleport` | spawn/replace cars (`add`, `segment`, `speed`, `color`, `reverse`) |
| POST | `/park` | park an existing car where it stands — the R32 obstruction primitive |
| POST | `/follow` | re-point the PRIMARY (followed) car at another uid |
| POST | `/cars` | `clear` / `remove` by uid |
| POST | `/flags` | per-car green/red test pennants (`red_nav`) |
| POST | `/label` | per-car HUD text |
| POST | `/toggle` | breadcrumbs / validator / mode / headlights / taillights / clear_blinker |
| POST | `/freeze` | pause / resume the simulation |
| POST | `/wait` | block until a condition holds or timeout |
| GET | `/tests` | the numbered scenario list (shells out to the runner) |
| POST | `/run_test` | launch the external test runner |
| GET | `/run_test` | status of the running/last test |
| GET | `/obstacles` | the placed obstructions (a bare list — see the units note) |
| POST | `/obstacles` | place one (`type`, `color`, `x`, `y` — or `random`) |
| DELETE | `/obstacles/{obId}` | remove one by id |
| POST | `/focus` | what the CURRENT scenario wants the window to look at (a car or a place) |
| POST | `/test_result` | the suite's verdict for the scenario that just ended (recorded on the tape) |
| POST | `/tape` | start recording the run's substep tape (idempotent); answers `{recording, steps, first, last, dt_fixed, map, map_hash}` — the reference to the ground the run belongs to |
| POST | `/tape/clear` | drop recorded frames, keep the recorder running |
| POST | `/tape/tip` | the frames a client has not received yet: `{"since": N, "limit": M}` (N = `-1` → since the run's start) → `{first_step, head_step, from_step, dt_fixed_s, map, map_hash, legend, lines[]}` — **the lines are the tape file's own text grammar** (`Tape.cs:FrameLine`), so a tip feed and a tape file parse with one parser, not two that can drift |
| POST | `/tape/frame` | one recorded substep: `{"step": N}`, `-1` = last — per car: uid, x, y, heading, **speed (m/s)**, blinkers, braking, wrong-side, crashes, `cap_key`/`held_by`, **`level`** (its deck), **`kind`** (its actor kind, `car` implied) |
| GET | `/tape/index` | step range + the steps that carry an event |
| GET | `/tape/list` | the tapes on disk, newest first (with map + hash) |
| POST | `/tape/load` | load a saved tape for review (`{"path": …}`) |
| POST | `/tape/save` | write the tape to a file (`{"path": …}`) |

### Camera focus — `/focus` and `/test_result`

A scenario knows what it is ABOUT. `tests/test_turning.py` declares it in
`SCENARIO_FOCUS` (keyed by scenario, [TESTING.md → "Camera focus"](TESTING.md#camera-focus-per-test-both-clients-one-declaration)),
announces it with one `POST /focus` per test, and the sim keeps it as run state
so **both** consumers get the same answer: the viewer reads `/state.focus` and
re-aims when it changes; `RunTape` stamps it on the tape so the reviewer
replays the identical framing from the file alone.

```bash
# a PLACE (the crossing tests) — and a loc focus means NO car is followed
curl -s -X POST localhost:5001/focus -H 'Content-Type: application/json' \
  -d '{"kind":"loc","x":1750,"y":1750,"zoom":26,"label":"26/27",
       "scenario":"fig8_xing","desc":"Right-of-way crossing WITH signs: 50 cars"}'
# a named car of the cast (the pass tests)
curl -s -X POST localhost:5001/focus -H 'Content-Type: application/json' \
  -d '{"kind":"named","name":"parked_car","zoom":16,"label":"18/27"}'
# the default: the primary car, view fitted (no x/y/name/zoom needed)
curl -s -X POST localhost:5001/focus -H 'Content-Type: application/json' -d '{"kind":"car"}'
```

| field | meaning |
|---|---|
| `kind` | `car` (the primary car — `/state`'s followed car), `named` (the car whose spawn name is `name`), `loc` (a place; `x`/`y` in metres) |
| `x`, `y` | the place, **metres** (`"units": "meters"` applies to `/state.focus` too); required for `kind:"loc"` (400 without) |
| `zoom` | px/m the client should hold there; `0`/absent for `car` = fit the car (`MapRenderer.CarFocusViewM`, 120 m across) |
| `name` | the cast name for `kind:"named"` (`POST /teleport {"name": …}`) |
| `name_uid` | the uid of the car that name was given to. **A role name is not a key** — `parked_car`/`slow_car` recur scenario after scenario, so a replay that resolves by name lands on the wrong car. The suite stages the cast a moment after announcing, so it refines the same `label` with this field (a re-announcement replaces the note) |
| `uid` | the primary car's uid at the moment of the announcement — for a client with no live `/state` (the reviewer) |
| `label`, `scenario`, `desc` | `"17/27"`, the scenario key, and its one-line description (the reviewer's Test panel shows them) |

`400` on an unknown `kind` or a `loc` without coordinates; `{"ok":true,
"focus":{…}}` otherwise, and `/state.focus` carries the same object from the
next tick (it is `null` while no scenario has announced one, which every client
reads as "follow the primary car"). An unresolvable declaration (the named road
is not on this map) is resolved to a car focus by the suite and says so in its
log — the camera is cosmetic and must never fail a run.

`POST /test_result` files a scenario's verdict:

```bash
curl -s -X POST localhost:5001/test_result -H 'Content-Type: application/json' \
  -d '{"number":17,"label":"17/27","scenario":"park_block_overtake",
       "passed":true,"reason":"","sim_s":10.6,"wall_s":2.5,"crossings":2,"off_road":0}'
```

| field | meaning |
|---|---|
| `number`, `label`, `scenario` | the scenario's place in the list, its `"17/27"` label (the join key against the focus notes) and its start point |
| `passed`, `reason` | the verdict, and `describe_failure()`'s text for a red one (empty for a green one) |
| `sim_s`, `wall_s`, `crossings`, `off_road` | the SAME numbers the run's results table prints, from the same dict — so the review panel can never disagree with the log |

Both land in `/state` (`focus`, `test_results`) and on the tape as `# focus …` /
`# result …` lines ([RECORD_PLAYBACK.md](RECORD_PLAYBACK.md)). Re-announcing the
same `label` replaces the note instead of stacking a second one.

### The tape family in one paragraph

`POST /tape/tip` streams the tip of a tape that is still being written — the
live window's picture is that stream (docs/RECORD_PLAYBACK.md → *"One client,
one data source"*); `POST /tape` starts recording, every substep of every car
lands in memory,
`POST /tape/save` writes it next to the run log. `POST /tape/frame` answers
**one** recorded substep — pose, blinkers, braking, wrong-side verdict, crash
count, **the deck it drives on (`level`: 0 = ground, 1 = bridge, — the same
number `/state` answers, resolved from the same segment: the two channels
cannot disagree about which bridge a car is on)**, the cap key and the car that
owns it (`held_by`), plus the frame's `fluidity_mps_per_car`, `moving_share` and
event texts — and answers `ok: false`
(it does not invent a frame) when the tape does not reach that step.
`/tape/load` reads a file back for review and
leaves recording OFF, `/tape/list` finds the runs on disk, `/tape/clear` drops
the frames. Clock = substeps, unit = metres (`"units": "meters"` on the frame
payload). The format, the limits and the review client that reads them:
[RECORD_PLAYBACK.md](RECORD_PLAYBACK.md).

### Health Check
```bash
GET /health
```

Returns:
```json
{
  "status": "ok",
  "timestamp": 1234567890.123
}
```

### Get Game State
```bash
GET /state
```

Returns current game state:
```json
{
  "units": "meters",
  "frame": 123,
  "time": 2.05,
  "x": 1234.5,
  "y": 2345.6,
  "heading": 45.0,
  "speed": 10.5,
  "speed_kmh": 37.8,
  "segment": 42,
  "progress": 0.65,
  "forward": true,
  "on_road": true,
  "driver": "BICYCLE",
  "parking": {
    "style": "reverse",
    "phase": "stopped",
    "parked": true,
    "reversing": false
  },
  "trail_enabled": false,
  "validator_enabled": true,
  "time_warp": 1.0,
  "flow_m_total": 20520.4,
  "flow_m_per_s": 342.0,
  "lockdowns": 0,
  "lockdown_last": "",
  "standstill_errors": 0,
  "standstill_last": ""
}
```

**Quality metrics (docs/DRIVING_MANEUVERS.md §8 "Liveness & quality").**
`flow_m_total` is the metres every car in the fleet has driven since the sim
started, `flow_m_per_s` the same over sim time - a high number means fluid
traffic, a low one a stuck fleet (it punishes creeping, unlike a "cars
moving" count). `lockdowns` counts total lockdowns the sim had to report
(R28 - prevention must keep it at 0, the e2e suite fails when it grows), and
`standstill_errors` counts cars that moved < 1 m within 3 min of WALL time
(R31). The `*_last` fields carry the full event text (poses, cap keys,
last decisions) so a failure can be read from data instead of guessed. The
Godot HUD shows the flow **per car** (`flow/car 6.8 m/s · 410 m per car
(50 cars)`) plus `!! stuck: N` when R31 fires, and hides the line for
single-car runs - a lone car's flow is just its own speed.

**Units.** Every coordinate anywhere in this API - `x`, `y`, the `body_corners`
of a car, the `flags` positions, `GET /map`, `GET /segment/{idx}`, `GET /start_points`,
`POST /obstacles` - is in **metres** (x east, y north, origin at the map's
south-west corner). `speed` is m/s, `speed_kmh` km/h. There is no pixel
frame left anywhere: the sim works in metres and the Godot client picks
its own px/m at draw time. Payloads that are objects carry `"units":
"meters"`; `GET /obstacles` answers a bare list and is metres by the
section below. The project-wide rule for units and their naming is
`docs/UNITS.md`.

`parking` reflects the nav's parking state (fields are null when no
parking plan is active): `style` is `"forward"` or `"reverse"`, `phase`
tracks the approach (`lead/decel/swerve/final/reverse/stopped`), and
`parked` becomes `true` only when the maneuver is complete - this is what
the e2e suite waits for on reverse-in scenarios, because a back-in park
deliberately crosses the end flag to stage the reverse (see
`docs/TESTING.md` §3).

**Time warp (`--warp N` / `--fast` on the host):** `time_warp` says how
much SIM time runs per wall second (1.0 = real time, `0.0` = `--fast`,
which is unbounded and must be measured). The physics step never changes
- the host just runs more 60 Hz steps per wall second - so every consumer
paces on `time` and scales its bounds by `time_warp`. The Godot client
and the e2e suite do; the visible window at a high warp is a sped-up
playback, not a smooth one.

**Multi-car — the semantics (binding, and the whole contract):** several cars
can be on the map at once. These five rules are what every client and every
test relies on:

1. **`POST /teleport` REPLACES by default** — that is the legacy behaviour the
   sequential suite depends on. `"add": true` **adds** alongside the existing
   cars; parallel scenarios opt in explicitly, nothing else changes.
2. **The PRIMARY (followed) car** is the most recently spawned one, re-pointed
   by every teleport; `POST /follow {"uid": N}` says so explicitly when a
   scenario spawned its extras afterwards. **ALL top-level `/state` fields
   describe the primary car**, so single-car clients keep working untouched.
   Removing the followed car falls back to the **highest remaining uid**
   (`SimEngine._followUid = _cars.Keys.Max()`), and no cars at all is the
   existing `has_car: false` branch.
3. **Per-car addressing:** `/control`, `/flags`, `/label`, `/toggle` and the
   hazard command take an optional `"uid"`; omitted = the primary car (legacy).
4. **Cleanup:** `POST /cars {"action": "clear"}` / `{"action": "remove",
   "uid": N}`.
5. **The `/state` shape:** the legacy top-level fields stay **and** a `cars`
   array is always exported (empty when no car is on the map); the Godot
   frontend prefers the array automatically.

Cars DO interact now: `CarCollisions` caps speeds and resolves contact per
substep (the retired plan's "no car–car collisions" decision was superseded
2026-09-06); `--no-collisions` on the host is the isolation switch.

The top-level fields always describe the PRIMARY car; ALL cars are additionally
exported in a `cars` array:
```json
"cars": [
  {"car_uid": 1, "x": ..., "y": ..., "heading": ..., "speed_kmh": ...,
   "level": 0, "segment": 42, "progress": 0.65, "on_road": true,
   "wrong_side": false, "blinker_left": false, "blinker_right": true,
   "hazard": false, "color": "blue",
   "in_contact": false, "crash_episodes": 0, "contact_with": 0,
   "body_corners": [[x, y], [x, y], [x, y], [x, y]],
   "driver": "BICYCLE",
   "flags": {"green": [x, y, h] | null, "red": [x, y, h] | null},
   "hud_label": "1/24" | null}
]
```
`color` names the car's vehicle sprite: `blue`, `silver`, `police`,
`tan`, `tractor` (Sattelschlepper engine unit), `pickup`, `mixer` -
assigned deterministically as
`CAR_COLORS[(uid-1) % 7]`.

Other per-car fields worth knowing:

- `in_contact` is TRUE only while a pair is pinned by collision resolution
  (a crash) - it is TRANSIENT and can clear inside one poll. `crash_episodes`
  is the monotonic count of crash episodes that car took part in; a test that
  must catch the FIRST crash reads the counter, not the flag.
- `cap_key` is the RULE holding this car back right now (`avoid#12`,
  `sign#C#7`, `r32#passive`, `""` = free) and `held_by` the **car** that owns it
  (`0` = nobody - free, or an R32 creep where the kerb is what holds the car).
  Both are on the tape per substep (see
  [RECORD_PLAYBACK.md](RECORD_PLAYBACK.md)) and answer in BOTH `/state` payload
  builders, so a client does not get two contracts depending on whether the map
  holds one car or fifty. They are what the car detail window draws as its chain
  ([CAR_DETAIL_WINDOW.md](CAR_DETAIL_WINDOW.md)) - a client must not recover the
  uid from the key, which is why `held_by` exists.
- `body_corners` is the exact box used for crash detection: four `[x, y]`
  **metre** corners (front-right, front-left, rear-left, rear-right) of that
  car's per-class body. The client uses it as the car's click/hit area.
- `level` is the vertical level of the current segment (0 = ground, 1 =
  bridge): the renderer z-orders the car above its own deck but below any
  higher level, and cars on different levels never collide.
- `driver` names the Driver class (`BICYCLE` / `FREE`) for the client's
  click-to-inspect window.

### Send Control Inputs
```bash
POST /control
Content-Type: application/json

{
  "uid": 2,              # optional; omit = primary (followed) car
  "accelerate": true,
  "brake": false,
  "steer_left": false,
  "steer_right": false,
  "blinker_left": false,
  "blinker_right": true
}
```

All fields are optional. Only specified fields are updated.
`"hazard": true|false` is a one-shot command (both on and off explicit).
With several cars, address them by `uid`; without it the input goes to
the primary car.

### Reset Controls
```bash
POST /reset
```

Sets all control inputs to `false`.

### Teleport Car
```bash
POST /teleport
Content-Type: application/json

# Random location (replaces the current car(s))
{"random": true}

# Named start point, mid-segment, rolling start - and ADD alongside the
# existing cars instead of replacing them (parallel test runs):
{"start_point": "www_entry", "progress": 0.5, "speed": 8.3, "add": true}

# Arbitrary segment (stress tests / dev): placement follows the corner-
# rounded PATH the pavement is built from, not the segment's chord - a
# chord pose near a sharp corner sits metres off the pavement and points
# ~10 deg off the road's real direction:
{"segment": 128, "progress": 0.5, "speed": 8.3, "add": true}

# Face the OTHER way along the same two-way road (two-way traffic in the
# crossing stress runs; the lane offset is applied relative to the actual
# heading, so the car still starts in its own right-hand lane):
{"segment": 128, "progress": 0.5, "reverse": true, "add": true}

# Choose the vehicle sprite (validated against Config.CAR_COLORS; 400 on
# an unknown name):
{"start_point": "www_entry", "color": "mixer", "add": true}
```
Default behavior is REPLACE: all current cars are removed and one fresh
car appears (the e2e suite and cockpit rely on this). With
`"add": true` a new car joins the existing ones - each teleport spawns a
new uid, and the primary-car state switches to the newest. The view is
not part of the API at all: the client owns its `Camera2D` and follows
whichever car it is bound to (its own `--center`/`--zoom` and Tab/F keys).
Named start points come from `GET /start_points` (synthetic test maps).

**Command ordering:** all command endpoints (`/teleport`, `/cars`,
`/control`, `/flags`, `/label`, `/toggle`, `/freeze`, `/hazard`) feed ONE
global FIFO in the sim - commands apply in strict arrival order, even
when several land within the same 16.7 ms frame (a `clear` followed by
teleports is never applied before them).

### Manage Cars (multi-car)
```bash
POST /cars
Content-Type: application/json

{"action": "clear"}              # remove all cars
{"action": "remove", "uid": 3}   # remove one car
```
Cleanup for parallel test runs (clear between rounds) and dev.

### Set HUD Label
```bash
POST /label
Content-Type: application/json

{"uid": 2, "text": "4"}
```
Shows (or, with `{"text": null}` / `{}`, clears) a short text label for
one car - the big HUD readout below the minimap (primary car's label)
and a small per-car label above its nose. `uid` is optional; omit =
primary car. Purely a visual aid - e.g. `tests/test_turning.py` uses it
to show which map tile/scenario the currently running test is on.

### Set Test Flags (per car)
```bash
POST /flags
Content-Type: application/json

{
  "uid": 2,                                   # optional; omit = primary
  "green": [x, y, heading_deg] | null,        # start pennant (world px)
  "red": [segment_idx, progress] | null,      # end marker, resolved by the sim
  "red_nav": true                             # true: red flag = parking destination
}
```
The green flag is a world position (usually the car's spawn). The red
flag is given as segment + progress; the sim resolves it to a map
position once THAT car's route covers the segment. With `red_nav`
defaulting to true the red flag becomes the car's navigation destination
(parking); running turn tests send `red_nav: false` (visual marker only).
See `docs/TESTING.md`.

### Toggle Features
```bash
POST /toggle
Content-Type: application/json

{
  "uid": 2,             # optional; omit = primary car
                        # ("validator" stays GLOBAL, the rest are per-car)
  "breadcrumbs": true,
  "validator": false,
  "mode": "bicycle",    # or "free"
  "headlights": true,   # white front-corner lights (test/QA only)
  "taillights": true,   # dark red rear-corner lights (test/QA only)
  "clear_blinker": true # clear BOTH turn indicators once (one-shot)
}
```
`headlights_on` / `taillights_on` are exposed per car on `/state`. Brake
lights piggyback on `Car.IsBraking` (no new sim state) and show bright red at
the rear corners regardless of `taillights_on` - a real brake light works with
the lights off.

### Freeze / Resume
```bash
POST /freeze
Content-Type: application/json

{"frozen": true}   # or false to resume
```

Pauses the simulation (replaces the old ESC key in the pygame window).
`GET /state` reports `"frozen": true` while paused.

### Run a Test by Number

The test scenarios live OUTSIDE the sim - `tests/test_turning.py` is an
external program that drives this very API. These endpoints let a frontend
(e.g. the Godot window's "Run Test" button) start one by number:

```bash
GET /tests
# -> [{number, key, direction, description}, ...]
#    numbers are stable: they are what the suite prints as "TEST i/N"

POST /run_test
Content-Type: application/json

{"number": 22}   # launch `tests/test_turning.py --tests 22` in its own process
# -> {ok, number, pid, log_file}   (409 while a run is in progress,
#                                   404 for an unknown number)

GET /run_test
# -> {running, number, pid, started_at, finished_at, returncode, log_file}
```

The sim spawns the runner as a detached subprocess (one at a time); its
stdout goes to a timestamped log under `tests/`. The runner talks back
through this API like any other client, so the running test is fully
visible in the frontend.

Two things the spawn has to get right, both learned from a silent failure
(2026-10-25):

- **the suite is told which host launched it** - `CAR_API_URL` is set to
  `http://{request host}`, i.e. the address this very `POST /run_test` arrived
  on. Inheriting the parent environment left it at the suite's default `:5001`,
  so a host on another port started a suite that drove - and waited on - some
  other server.
- **the suite's path is found by walking up** from the assembly until
  `tests/test_turning.py` exists (`GameApi.ResolveRunnerPath()`), not by
  counting four `..`: the console host sits four levels under the repo, the
  Godot client's assembly five (`.godot/mono/temp/bin/Debug/`), so the
  embedded client's `Run Test` button resolved to a non-existent
  `.godot/tests/…`. `DRIVING_GAME_TEST_RUNNER` still overrides both.

### Wait for Condition
```bash
POST /wait
Content-Type: application/json

{
  "condition": "segment_changed",  # or "speed_reached", "position_reached"
  "value": 42,  # depends on condition
  "timeout": 5.0  # seconds
}
```

Blocks until condition is met or timeout. For
`"position_reached"`, `value` is `{"x": …, "y": …, "threshold": …}` in
**metres** (the same unit `GET /state` reports); `threshold` defaults to
5 m.

### Per-car decision log
```bash
GET /car/7/decisions
```
Returns that car's rule log - EVENTS, not per-frame samples:

```json
{"uid": 7,
 "decisions": [{"t": 12.34, "step": 742, "msg": "[R5] yield sign: car 26 at the crossing (19 m out)"},
               {"t": 13.10, "step": 788, "msg": "[R3] entered crossing - committed, continuing"}]}
```

`step` is the substep the decision was made on - the same number the tape's
first column carries, so a row of the car detail window looks the same whether
it came from a live host or from a file
([CAR_DETAIL_WINDOW.md](CAR_DETAIL_WINDOW.md)).

One entry each time the REASON a car is held back changes (new threat,
threat gone, rule fired), tagged with the Driving-Ruleset number (see
`docs/DRIVING_MANEUVERS.md` §8). The newest (last) entry is why the car
behaves as it does now; the Godot client shows the tail of the list in a
click-to-inspect window, so a stuck car always answers "why am I stopped?".
The log holds at most 100 entries per car (`Car.MaxDecisions`).

### Map export (GET /map)

The layer-2 geometry contract, served once per map load - this is what the
client renders and what an external planner would plan on. Payload keys
(`DrivingGame.Api/MapPayload.cs`):

```json
{
  "units": "meters",
  "bounds": [x0, y0, x1, y1],
  "road_color": [r,g,b], "bg_color": [r,g,b],
  "road_strips":  [{"left": [[x,y]…], "right": [[x,y]…]}],   // road as its own ribbon
  "road_polygons": [{"color": [r,g,b], "rings": [{"exterior": …, "holes": …}]}],
  "segments": [[x1,y1,x2,y2,width,oneway,…]],
  "paved_edge_rings": …,          // the paved polygon's edges (off-road check)
  "centerlines": …, "lane_markings": [{"style": …, "width_m": …, "pts": …}],
  "stop_lines": [[x1,y1,x2,y2]],  // halt lines at yield-signed mouths
  "crossing_markers": [[x,y,r]],  // the white centre disc at degree>=4 crossings
  "oneway_arrows": …, "parking_marks": …,
  "signs": [{"x": …, "y": …, "heading_deg": …, "type": "yield|priority"}],
  "elevated_roads": …, "elevated_roadways": …, "elevated_edge_lines": …,
  "elevated_centerlines": …,
  "marking_style": {"center_dash_m": 3, "center_gap_m": 3, "lane_dash_m": 2,
                    "lane_gap_m": 4, "park_dash_m": 1, "park_gap_m": 1,
                    "stop_line_width_m": 0.5},
  "start_points": {name: [x, y, heading_deg, seg, forward, lateral_offset_m]},
  "map_fingerprint": "v2:segs=256:signs=4:starts=48:sum=5C2E143F:fnv=626AA1A6"
}
```

Every coordinate is in metres (x east, y north, origin at the map's south-west
corner); dash patterns are declared in metres so an external renderer draws the
same paint from one source of truth.

`map_fingerprint` — a string, so no `"units"` needed — is the **stamp of the
ground this endpoint serves**: one `RoadNetwork.MapFingerprint` (`RoadNetwork.cs:985`)
over segment geometry plus the deck level and lane/parking profile, every sign's
type and node, and the whole start cast. A tape names its ground with the SAME
string (`map_hash=` in its header), which is what lets a review client refuse to
paint a tape on a foreign map (docs/RECORD_PLAYBACK.md → *"The header's map stamp
and the client's check"*). The value carries **no spaces**: the tape header is
space-separated, and a spaced stamp was silently truncated to its first word by
both readers.

### Obstacles (docs/OBSTACLES.md)

The game has a static obstacle system (parked cars). Placement and removal
go through the same logic as the palette UI in the game window.

**List obstacles**
```bash
GET /obstacles
```
Returns `[{id, type, color, x, y, heading}, ...]` — `x`/`y` in **metres**
(the API's one dialect), `heading` in degrees (0 = north). Empty list if
none are placed.

**Place an obstacle**
```bash
POST /obstacles
Content-Type: application/json

{"type": "car", "color": "blue", "x": 194, "y": 300}
```
- `type`: currently only `"car"` (a parked car, same size as the player car)
- `color`: `blue`, `yellow` or `white`
- `x`, `y`: metres (the API's one dialect)

The heading is NOT an input: it is computed from the lane direction under the
drop point (right half of the road faces forward, left/oncoming half faces
back; on curves and in junctions it follows the local road tangent).
Returns `201` + the created obstacle (with its stable `id`). Returns `400`
if the point is off the paved area or the request is invalid.

**Remove an obstacle**
```bash
DELETE /obstacles/1
```
Returns `200` (`{"ok": true, "id": 1}`) or `404` for an unknown id.

## Example Usage

### Python (requests)

```python
import requests
import time

API = "http://localhost:5001"

# Check health
response = requests.get(f"{API}/health")
print(response.json())

# Get state
state = requests.get(f"{API}/state").json()
print(f"Speed: {state['speed_kmh']:.0f} km/h")

# Accelerate
requests.post(f"{API}/control", json={"accelerate": True})
time.sleep(2)

# Check speed again
state = requests.get(f"{API}/state").json()
print(f"New speed: {state['speed_kmh']:.0f} km/h")

# Stop
requests.post(f"{API}/reset")
```

### curl

```bash
# Health check
curl http://localhost:5001/health

# Get state
curl http://localhost:5001/state | jq

# Accelerate
curl -X POST http://localhost:5001/control \
  -H "Content-Type: application/json" \
  -d '{"accelerate": true}'

# Teleport randomly
curl -X POST http://localhost:5001/teleport \
  -H "Content-Type: application/json" \
  -d '{"random": true}'

# Enable breadcrumbs
curl -X POST http://localhost:5001/toggle \
  -H "Content-Type: application/json" \
  -d '{"breadcrumbs": true}'

# Freeze the simulation (e.g. to inspect state)
curl -X POST http://localhost:5001/freeze \
  -H "Content-Type: application/json" -d '{"frozen": true}'
```

## Automated Testing

The e2e suite is `tests/test_turning.py` in THIS repo; run it through
`scripts/run_e2e.sh` (see docs/TESTING.md). The old Python `tests/test_api.py`
smoke tests lived in the retired `car/` repo; endpoint parity here is covered
by the C# xunit suite plus the live suite.

## Use Cases

### 1. Automated Turn Testing

```python
def test_turn_stays_on_road():
    # Teleport to junction
    requests.post(f"{API}/teleport", json={"random": True})
    
    # Accelerate with right blinker
    requests.post(f"{API}/control", json={
        "accelerate": True,
        "blinker_right": True
    })
    
    # Monitor for 10 seconds
    for i in range(100):
        state = requests.get(f"{API}/state").json()
        assert state['on_road'], "Car went off-road!"
        time.sleep(0.1)
```

### 2. Performance Profiling

```python
states = []
for i in range(600):  # 10 seconds at 60 FPS
    state = requests.get(f"{API}/state").json()
    states.append(state)
    time.sleep(1/60)

# Analyze positions, speeds, etc.
```

### 3. State Snapshots

Since M5 the sim is headless - there is no server-side rendering to
screenshot. Visual verification happens in the Godot frontend; for
automated checks, snapshot `GET /state` (position, heading, segment,
flags) instead of pixels.

## Architecture

The API is one `DrivingGame.Api` project wired by BOTH hosts - the console
host (`DrivingGame.Server/Program.cs`) and the Godot app (`SimHost.cs`,
`--source embedded`).

- The sim runs on a **dedicated background thread** with its own 60 Hz pacing
  (sleep + 4 ms spin window), independent of render hitches; "in one app" does
  NOT mean same thread.
- `GET /state` serves `SimEngine.StateSnapshot()` - a consistent copy of the
  last payload under a lock; the sim thread mutates it at 60 Hz while HTTP
  threads read it.
- All one-shot commands (`/teleport`, `/cars`, `/flags`, `/label`, `/toggle`,
  `/freeze`, hazard) go into ONE global FIFO of `SimCommand` records, applied
  in strict arrival order per frame batch. Sustained keys (`/control`) live in
  per-uid control buckets; a missing uid means the "unaddressed" bucket, which
  applies to the PRIMARY (followed) car only.
- Game loop per frame: drain the command queue → per-car control merge
  (driver + API) → fixed-timestep substeps (two-pass collision: advance + cap
  + obstacle contact, then `Resolve` rollback, then validator + lane guard on
  the FINAL state) → resolve pending red flags → export state.

## Security Note

The API listens on `127.0.0.1:5001` (localhost only) by default.
**Do not expose to public networks** - there is no authentication!
