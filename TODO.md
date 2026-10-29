# To-do — open work

> **Written 2026-10-24 (Sat, ~23:40), mid-session.** This is a list of what is
> known to be open, with the state of the tree at that moment so it can be
> picked up cold. It is not a spec: the specs stay authoritative
> ([docs/](docs/), index in [README.md](README.md)) — every entry here points
> at the one that governs it.

## 0. Done since this list was written (2026-10-24, later that night)

- **The decision dedup** (`Car.RecordDecision` drops a line that repeats the
  newest entry verbatim, so the first entry keeps the step the situation started
  on) is **built, tested and committed** — `DecisionLogTests` (3 tests),
  suite **96 passed / 0 failed / 2 skipped**, build 0 errors. The one red test
  this list recorded was a bug in my own assertion (`"brake"` where the line
  reads `"brake:"`), not in the behaviour.
- Why it existed: `hairpin_exit` in `tape_T_249` logged 269 decisions for one
  car, 268 of them the identical `[R1] off-road at (579, 1128)` — the validator
  re-reports at 60 Hz while the car sits in the same metre of grass.
- **Still true:** a tape recorded before it (every tape in `tests/`) keeps its
  duplicates, so an old run never looks deduplicated in the reviewer. New
  evidence of the collapse wants a fresh run.

## 1. The live viewer has no car detail windows

`CarDetailWindow` exists and only the reviewer uses it; the viewer still shows
just its readout box. Spec:
[docs/CAR_DETAIL_WINDOW.md](docs/CAR_DETAIL_WINDOW.md) — "one window for both
clients, identical behaviour" is a binding decision (2026-10-24).

- [ ] Feed the window from live `/state` + `GET /car/{uid}/decisions`
      (`held_by`, `cap_key`, `step` are already exported — see
      [REST_API.md](docs/REST_API.md)).
- [ ] Right-click menu in the viewer, same rules as the reviewer.
- [ ] Step rows **inert** in the viewer: there is no tape to seek in. The
      window already has a `Seekable` flag — the number must be drawn as text,
      not as a link that does nothing.
- [ ] Markers / chart / fade band / window guard were tuned around the
      reviewer's layout; the viewer needs the same check.
- [ ] `GODOT_FRONTEND.md` § "What the client draws today" afterwards.

## 2. The peek card (left-press) — not built

Spec: [CAR_DETAIL_WINDOW.md § "The peek card"](docs/CAR_DETAIL_WINDOW.md).

- [ ] Borderless card: the static section + **one** event line, no list, no
      status line, gone when the button is released, never persisted.
- [ ] The hand-over decision "**c**": card on press, retired past
      `PeekGiveUpPx` (starting 6 px), the pan takes the rest — never both,
      never suppressed.
- [ ] Probe `--test-peek-pan`: press on a car, walk 0 / 4 / 8 / 40 px, print
      `card visible · pan active · which took the press`.

## 3. Session memory — nothing is stored at all

Verified 2026-10-24: no `ConfigFile`, no `user://`, no
`NOTIFICATION_WM_CLOSE_REQUEST` handler anywhere in the C#. Position, size,
cursor and camera reset every launch (`--review-at` is the only thing that
moves the cursor). Spec:
[CAR_DETAIL_WINDOW.md § "Geometry and session memory"](docs/CAR_DETAIL_WINDOW.md).

- [ ] Write `user://car_windows.cfg` on quit (and on window create/close/
      resize-end, tape load, scrub pause — not per frame); CrashDumper gets the
      same snapshot so a crash does not lose the session.
- [ ] Store: tape path + `map`/`map_hash`/frame count, cursor **step**, one
      entry per open window (`uid` + position + size), a uid-free `geometry`
      template, camera + zoom.
- [ ] Restore rules: same tape → cursor, camera and every window whose uid is
      really in that tape (skip and print the rest); different/missing tape →
      geometry and camera only, and say why the windows did not come back; a
      restored camera is a **hand-pin**, so the recording stops re-claiming the
      view.
- [ ] Live client: `windows`, `camera`, `zoom` in a second section.

## 4. Built but never measured, and never looked at by a human

- [ ] **Press-and-hold a chain id → red box on the map.** Implemented
      (`SetMapHighlight`, the hover-latched `HighlightUid`), never driven by a
      probe or a hand.
- [ ] Paging deeper than one page (`page = −42`) and the **↓ later** arrow —
      the probe only presses `↑ earlier` and `◎ now`.
- [ ] The lamp dots: right size, right colours, learnable? (operator's eye)
- [ ] A menu opened near the right or bottom edge: does it land fully on
      screen?

## 5. Behaviour that changed and wants a ruling

- [ ] `--review-name <role>` still aims the camera but opens **no window**
      since the three slots died. Arguably it should open one for that car.
- [ ] `BuildDetailData` calls `HasLockdownNote` — a scan of that car's whole
      event list — every frame per window. Free at 6 events, wasteful at 953;
      cache it per (uid, history length).
- [ ] The probe flag is still called `--test-slot-buttons` (it drives the new
      window). Rename to something honest — the doc already says so.
- [ ] Rows *after* the anchor were tinted blue, the colour of a clickable
      address; now grey. Confirm that reads as "future" and not "disabled".

## 6. Loose ends that are not about the window

- [x] ~~`docs/godot_web_rendering_architecture.md` — keep or delete?~~ **Kept
      and committed on its own** (2026-10-24, operator's instruction), with a
      README index row calling it what it is: a reference note on browser
      delivery, neither path built or decided. It was not written by this
      session — that is why it went in alone rather than inside an unrelated
      commit.
- [x] ~~`tests/tape_T_250_19_20261024_220134.txt` — untracked single-scenario
      tape, judged not evidence worth keeping; delete or commit.~~ **Deleted**
      with every other tape on 2026-10-29 (operator's instruction): the whole
      `tests/tape_T_*.txt` set went — 24 files, 33 MB, the committed `T_303`
      and `T_308` included — so no unverifiable pre-wide-stamp recording is
      left to inherit, and the reviewer's evidence is the tape of the run
      under test.
- [ ] e2e baseline: **3 red** — #20 `slow_pass_priority` (times out crawling),
      #26 `fig8_xing` (unexplained R33 centreline crossing at 9.1 s), #27
      `fig8_xing_plain` (contact 100/95 at ~10 s), plus the `❌ WRONG-SIDE`
      marker inside #24 that every full run prints and does not count. Current
      numbers live in one place:
      [TESTING.md → "Current baseline"](docs/TESTING.md#current-baseline-the-one-place-a-number-is-written).
- [ ] The 2026-10-24 tape scan found one car deciding at **3.94/s** (953 events
      over 242 s, all fig8 stress tapes from 2026-10-21/22) while
      [HUMAN_FACTORS.md](docs/HUMAN_FACTORS.md) §1 states a ~2.5/s human
      ceiling and `PERSIST_TICKS = 15` allows 4/s. Old data, `fig8_stress` is
      skipped in warped runs — reproduce it on a fresh real-time stress run
      before believing it is a live defect.
- [x] ~~`feat/car_detail_window` is not pushed~~ — pushed with upstream set;
      `origin/main` is still at `63bd2e6`, so nothing of this branch is on
      `main` until it is merged.

## 7. One client, one source — decided 2026-10-25, still to build

Spec: [docs/SIMULATIONS_REVIEWER.md → *"One client, one data source"*](docs/SIMULATIONS_REVIEWER.md)
and [docs/RECORD_PLAYBACK.md → *"Recording policy and the growing tape"*](docs/RECORD_PLAYBACK.md).
(a)–(d) are the user's binding decisions; the list is the tree's distance from them.

- [x] **`level` on the tape** — done 2026-10-25: `TapeCar.Level` is resolved
      from the SAME lookup `/state` uses (`Network.Segments[c.SegIdx].Level`),
      written before `capKey` (generation 5: `…,heldBy,level,capKey`) and read
      by both readers, so `ReviewFeed` paints the recorded deck instead of
      `Level = 0`. Verified end to end: `tests/tape_probe_v2.txt` carries level
      1 on all 259 substeps of a car placed on the figure-8 bridge, and
      `tests/tape_state_agreement.py` holds `/state` and the tape frame at ONE
      frozen substep against each other (level exact, pose 0.0 m). Two C# unit
      facts cover the round trip (`DrivingGame.Sim.Tests/TapeLevelTests.cs`),
      one on the deck and one for the car UNDER it — the figure-8's ground
      branch shares the crossing point, so a lookup that read the wrong
      segment would say 1 there. The old `ReviewFeed`-only gap is closed for
      NEW tapes; a pre-fix file still reads level 0 (documented, not relit).
- [ ] **the frame channel is car-shaped** — `TapeFrame.Cars` is a `List<TapeCar>`
      with car-only fields (`color`, `capKey`, `heldBy`, `wrongSide`). The sim
      **will** have moving non-cars (a cop, a directing policeman, a pedestrian)
      — decided 2026-10-25 — so this becomes an **ACTOR** channel (kind tag +
      the pose every actor has) **before** the first one is simulated, not
      retrofitted after (`docs/RECORD_PLAYBACK.md` → *"The channels"*).
- [ ] **ground STATE is an event channel, and it is empty** — traffic-light
      phases, gates, obstacle placement (`GameApi.cs:820/823/849` move the ground
      today) have no tape record, so a scrubbed frame cannot show where the
      parked car stood. Frequency picks the channel: state → event, position →
      frame.
- [ ] **`RunTape.Start` with the sim** (decision a): every run records. Only
      `scripts/run_e2e.sh` asks for it now, so a manual session is unrecorded —
      and once the view reads the tape, *blind*.
- [ ] **append + flush** (decision b): one line per substep to the file, flushed
      on collision (`CarCollisions.cs:2562`, where `CrashDumper` already lives)
      and at the latest every second; `POST /tape/save` becomes "close it and
      name it". Header written first, so its `steps=` turns advisory.
- [ ] **rolling memory, whole file**: `MaxSteps` currently *stops recording*
      (`TAPE FULL at 200000 substeps`) — once the tape is the picture that line
      means **the window goes blank** after ~55 min. Absolute step numbering
      stays, `_firstStep` becomes the floor, older frames come from the file.
- [x] **the tape carries its map** — done 2026-10-25 as a **reference**, not a
      copy: the header's `map_hash=` is `v2:segs=…:signs=…:starts=…:sum=…:fnv=…`
      (one `RoadNetwork.MapFingerprint`, wide enough to see decks, lane/parking
      profiles, every sign's type+node and every start point), `GET /map`
      answers the same stamp as `map_fingerprint` (`MapPayload.cs:204`), and
      `MapRenderer.review.cs:CheckTapeAgainstMap` compares them: a v2-vs-v2
      **mismatch refuses the picture** (`REVIEW-REFUSED … no cars painted`,
      readout `MAP MISMATCH — refused`, `--ignore-map-hash` overrides), and a
      stamp without the `v2:` prefix loads with `map check: pre-wide stamp,
      unverifiable` because "unknown" is not "wrong". Measured all three ways on
      `tests/tape_probe_v2.txt` (match: the deck car ON the bridge; foreign
      ground: an empty field; hand-corrupted hash: refused). Geometry still
      comes from the map side, which is what the reference says. The decision
      of 2026-10-25 is **reference, not embedding** — the tape names its ground
      and the client checks the name; two things make a reference real:
    - [ ] **the hash must cover what the client draws** — `RoadNetwork.MapFingerprint`
          (`RoadNetwork.cs:985`) mixes **`net.Signs.Count` only**: sign **type
          and position are not in it**, so today a tape could be painted under
          flipped yield signs without a trace. A reference that cannot see the
          right-of-way is not a reference.
    - [ ] **the client must VERIFY the reference** — it already parses
          `_tapeMap`/`_tapeHash` (`MapRenderer.review.cs:100/821/832`) and prints
          them (`649`, `1233`), but never compares them to the ground it drew:
          `map mismatch` must refuse the overlay, not repaint it.
- [ ] **live view reads the tape tip** — `/tape/frame` with the head as the
      clock, behind a flag next to `/state`, diffed frame by frame. Acceptance:
      the two channels paint the same pose (< 1e-4 m) for one run.
- [ ] **then delete `_review`** — 13 guards in the viewer and a 3 200-line
      partial that are a *mode* today and should be a *source*.

Built on 2026-10-25 out of the same discussion: **`▶ play`'s frame-snap**. The
reviewer painted the raw recorded substep because the variable that should have
lerped between two of them (`_scrubAcc2`) was declared, reset in three places
and never written. Now the cursor is a continuous position (`_playPos`) and the
source owns the clock (`_sourceOwnsClock`), which also stops the live estimate
from fighting the tape's stated time. Measured before/after on one tape with
`tests/pose_stutter_check.py --ab`: **held 67 % → 0 %, jump 33 % → 4.5 %**, the
painted speed as steady as the live viewer's (rel-mad 0.17 vs its 0.20).
