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

- [ ] `docs/godot_web_rendering_architecture.md` — untracked, **not written by
      this session** (dated 2026-10-24 20:16, a Godot web-export/streaming
      reference). Deliberately left out of the commits so far: nobody said keep
      or delete, and burying someone else's document inside an unrelated commit
      is how provenance gets lost. Keep (and commit) or delete — someone's call.
- [ ] `tests/tape_T_250_19_20261024_220134.txt` — untracked single-scenario
      tape, judged not evidence worth keeping; delete or commit.
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
