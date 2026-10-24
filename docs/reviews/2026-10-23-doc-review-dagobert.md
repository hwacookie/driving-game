# Document review — dagobert

> **Dated snapshot, not a spec** (review artefact, 2026-10-23). The findings
> it shares with the sibling review were applied the same day — see
> `2026-10-23-doc-fixes.md`; the "applied / rejected" ledger is there too.

Date: 2026-10-23 · Branch: `main` (`c827710`, working tree dirty: `MapRenderer.cs`,
`MapRenderer.review.cs`) · Scope: **all 23 prose documents**
(19 in `docs/`, plus `README.md`, `AGENTS.md`, `last_context.md`,
`review_2026-10-20.md`) and the 6 `diary/` entries.

**No file was changed and no code was touched.** Every finding is an
observation with evidence (`file:line`). Verification was done by grepping
the documents against the current code, and by re-running the read-only
checks the documents claim (`dotnet build`, `dotnet test`, reading the
`tests/` run logs).

---

## 0. Verdict

The documentation set is well maintained in style and cross-referencing, but
it is **three days behind the code on facts** and **has real coverage holes**.
Four documents still print "the suite is green (23/23)" while the newest full
run (2026-10-22, `tests/run_test_T_231_…log`) is **23 pass / 3 fail of 26 run
scenarios**. `README.md` and `AGENTS.md` still call the Simulationsreviewer
"not yet built" while `MapRenderer.review.cs` (2,054 lines) is in the tree and
committed. `docs/TESTING.md` documents two things that do not exist in this
repo at all (a pytest layer and a cockpit tool). Meanwhile the API reference
documents **20 of 33 endpoints**.

Roughly: **14 wrong facts, 6 coverage gaps, 9 convention-drift items.**
Nothing in the set is dangerous or misleading about *design intent* — the
failures are all staleness and completeness, which is exactly what the project
rules ("code-verified" status headers) promise not to happen.

---

## 1. Wrong today (document says X, code/data says Y)

| # | Where | Document says | Reality (evidence) |
|---|---|---|---|
| **D1** | `docs/TESTING.md:102`, `docs/SPEC.md:826`, `docs/SPEC.md:1399`, `docs/SMOOTH_GEOMETRY_DESIGN.md:179` | "Current baseline (run 2026-10-19): **23 of 23 run scenarios pass**" | Newest full-suite run `tests/run_test_T_231_0_20261022_005925.log` (2026-10-22, `--warp 4`): **26 run + 1 skipped, 23 passed, 3 failed** — `slow_pass_priority` timed out at 240 s sim, a run never reached the end flag, `fig8_xing_plain` crashed (car 96 despawned by the R29 lockdown valve at t=15.1 s). The suite also **grew** (23 → 26/27): `s_curve`, `hairpin_entry`, `hairpin_exit`, `roundabout_from_north`, `park_6lane_right_lane`, `slow_pass_*`, `park_block_*` are not in the documented baseline. |
| **D2** | `README.md:12`, `AGENTS.md:190` | Simulationsreviewer "**specified, not yet built**" / "(status: specified, not implemented)" | `docs/SIMULATIONS_REVIEWER.md:3` says "**built and in use**"; `MapRenderer.review.cs` + `MapRenderer.review.cs.uid` exist, `--review`, `--review-name`, `--review-at` are parsed in `MapRenderer.cs:536-552`, and the last commit (`c827710` "Windows: the 5 px belt, the minimap handle, the fading scrub bar") is reviewer work. The two entry-point documents contradict the spec document **and** the code. |
| **D3** | `docs/TESTING.md:5`, `:13` | Layer 1 of testing = "headless **pytest** suites (`tests/test_obstacles.py`, `tests/test_road_network.py`)", with the copy-paste command `.venv/bin/python -m pytest tests/test_obstacles.py tests/test_road_network.py -q` | Neither file exists (`tests/*.py` = `car_line_check.py`, `light_check.py`, `test_turning.py`), and `.venv` has **no pytest** at all (`No module named pytest`). The truth is at `docs/TESTING.md:489`: those suites were in the retired `/Users/hauke/prj/car` repo; the headless layer is the C# xunit suite. The document's *first* section is fictional. |
| **D4** | `docs/TESTING.md:264-283` ("Cockpit controller (`tools/controller.py`)") | A whole section documenting a "separate window (its own process, its own **pygame**)" with `--tests` / `--drive` modes | `tools/` contains only `make_car_sprites.py`; `tools/controller.py` **never existed in this repo** (`git log -- tools/controller.py` is empty). Also conflicts with `AGENTS.md` "No pygame compatibility target" and with `docs/lights_report.md` / `docs/MULTI_CAR_PLAN.md:209`, which already name the real equivalent (the Godot client's click-to-inspect window and `--run-test`). |
| **D5** | `docs/TESTING.md:225` | "Numbering is preserved… so **TEST 7/24** identifies the same scenario" | The suite is `/27` now (see D1). Any fixed N in this doc is stale. |
| **D6** | `AGENTS.md:49` | "The **four** manual QA scripts in `tests/` already follow this pattern" | There are three `.py` files in `tests/`, two of which are manual QA (`light_check.py`, `car_line_check.py`) — matching `docs/TESTING.md:613`. |
| **D7** | `AGENTS.md:73-74` | "**Every** coordinate payload declares `"units": "meters"`" (binding rule) | `GET /obstacles` answers a **bare list with no `units` key** (`DrivingGame.Api/GameApi.cs:732`), and the tape frame (`/tape/frame`) carries x/y in metres with no `units` key — `GameApi.cs` has exactly one `"units"` occurrence, `MapPayload.cs` one, `SimEngine.cs` two. `docs/REST_API.md:125-128` quietly concedes the `/obstacles` exception; `AGENTS.md` states it as absolute, so a rule-following change gets punished by an existing violation. |
| **D8** | `docs/UNITS.md:76-81` (Audit results, dated 2026-10-19) | "leftover `…Px` names … **only** `MapRenderer.cs`'s blinker-dot radius (`rPx`)" | The working tree adds ~20 further `…Px` names: `MapRenderer.cs:125-128` (`WinGapPx`, `WinTitleBarPx`, `WinFrameSidePx`, `WinFrameBottomPx`) and `MapRenderer.review.cs:66-72` (`ReadoutFontPx`, `AxisFontPx`, `ButtonFontPx`, `PickerFontPx`, `SlotFontPx`, `StripPx`, `RowInsetPx`). They are legitimate screen-frame constants per rule 3, but the audit line now reads as an exhaustive list that is no longer exhaustive. `last_context.md` has the same drift with line numbers (`rPx` quoted as `MapRenderer.cs:1812`, now `:2709`). |
| **D9** | `AGENTS.md:163`, `docs/TESTING.md:76`, `scripts/run_e2e.sh:26` | Example `scripts/run_e2e.sh --tests corner_left --warp 4  # single scenario` | `tests/test_turning.py:165`/`:196` match names **exactly** (`t[0] in wanted_name`); the scenario is `corner_left_entry` (`docs/TESTING.md:415`). The documented example selects **zero** scenarios — a copy-paste trap in the project's own rules file. |
| **D10** | `docs/GODOT_FRONTEND.md:173`, `docs/C_SHARP_PORT_SPEC.md:352` ("`scripts/run_e2e.sh --embedded`") | A runner flag `--embedded` | `scripts/run_e2e.sh` accepts `--no-godot --tests --fast --warp --realtime --focus-car --maximized --watch --zoom --center --fullscreen` — no `--embedded` (embedded mode is now the default source in the client, selected by `--source embedded`). |
| **D11** | `docs/RIGHT_OF_WAY_NEGOTIATION.md:3` | "**Status 2026-10-20: proposed, not implemented.** Nothing in this document exists in the code yet" | §7i of the same file (2026-10-21) says "Built", and `DrivingGame.Sim/Negotiation.cs`, `Car.EmergencyRole`, `CostOfWaiting`, `CourtesyForUid` are in the tree. The header must be re-stamped; as it stands a reader stops at line 3 and reimplements a finished subsystem. |
| **D12** | `docs/DRIVING_MANEUVERS.md` §8 | The ruleset is advertised as R1…R36 (`README.md:79`) but **R32, R34 and R35 have no definition entry** — only two passing mentions of R32 (`:767`, `:783`), zero of R34/R35. R34/R35 are defined only inside `docs/RIGHT_OF_WAY_NEGOTIATION.md:173-176`. All three are **implemented** (`CarCollisions.cs:92` "R36", `Raceline` `leftReliefM`, `Negotiation.cs`). | The ruleset doc cannot be read as the ruleset. |
| **D13** | `docs/DRIVING_MANEUVERS.md:950` (R20 meta) | "Still silent: … the **not-yet-implemented rules (R9–R12, R16, R18–R19, R21–R24)**" | R12 (courtesy) and R16 (anti-starvation via `Car.StandstillS`) were built on 2026-10-21 per §7i and are in code (`SimEngine.cs:1053` `c.StandstillS`, `CarCollisions.cs:850` `c.CourtesyForUid = o.Uid`). The same file tags R12/R16 `*[proposed]*` at `:840`/`:843` while the "built" table says otherwise. |
| **D14** | `docs/EXPERIMENT-fig8-crossing.md:3-14` | "BOTH crossings now survive their window … zero contact" (2026-10-19 update) | Contradicted by the 2026-10-22 runs in the same repo: `tests/run_test_T_229_…log` shows `fig8_xing_plain` "crash at t=9.6s: car 72 contacted car 67", `run_test_T_231_…log` shows `fig8_xing` "WRONG-SIDE DRIVING DETECTED" + "unexplained centreline crossing at t=9.1s". The experiment log is honest history but its update block reads as a current claim. |
| **D15** | `docs/SPEC.md:236`, `:840`, `:1020` | Corridor rule 2 as a **hard** bound `CAR_WIDTH/2 + LANE_CENTRE_MARGIN_M`, "never enter the oncoming lane"; identifiers `BicycleNav.WHEELBASE` (2.7), `Config.CAR_WIDTH`, `PARK_TRACK_LOOKAHEAD_M` style names | Code is past both: `Raceline.LegalCorridor(…, double leftReliefM = 0.0)` (`DrivingGame.Sim/Raceline.cs:413-419`) implements the conditional relief (R36/R32), and the units sweep renamed the identifiers (`BicycleNav.WHEELBASE_M`, `Config.CAR_WIDTH_M`). SPEC.md §1016 even describes the relief as future work. |
| **D16** | `docs/MULTI_CAR_PLAN.md:56` | "seven real vehicle sprites … `assets/car_{blue,silver,police,tan,**armored**,pickup,mixer}.png`" | `assets/` holds `car_tractor.png` (`Config.CAR_COLORS` = blue, silver, police, tan, **tractor**, pickup, mixer). The contract block lower down in the same doc uses the right name, so the doc contradicts itself. |
| **D17** | `review_2026-10-20.md` §3 | All 36 cleanup items are unchecked `[ ]`, and its "Verification after each step" line still quotes the **21/23** baseline | Several items have already landed and are not ticked: **H6** (`DrivingGame.Sim/LaneGeometry.cs` exists, described as landed in §5 of the same file), **L9** (both orphan `.uid` files are gone), **L11** (zero `pygame parity` comments left in `MapRenderer.cs`/`SimEngine.cs`), **L16** (`DrivingGame.csproj.old` gone). Still open, confirmed: **L1** (7 `MINIMAP_` refs in `Config.cs`), **L10** (`DrivingGame.Sim/RoadNetwork.cs:17` still comments `X1…Y2` as "world pixel coords" — a live units-rule violation the documents promise is gone). |

---

## 2. Coverage gaps (documented-where-it-should-be, absent)

**G1 — the API reference is 60 % complete.** `DrivingGame.Api/GameApi.cs`
registers **33 routes**; `docs/REST_API.md` documents 20. Undocumented
entirely: `POST /tape`, `POST /tape/clear`, `POST /tape/frame`, `GET /tape/index`,
`GET /tape/list`, `POST /tape/load`, `POST /tape/save` (the whole record/playback
family — only `docs/RECORD_PLAYBACK.md:73-90` has it), `POST /park`
(`:189`, the R32 obstruction primitive), `POST /follow` (`:207`, multi-car
camera/primary re-point), `GET /segment/by/{name}` (`:108`). `GET /car/{uid}/decisions`
is present as prose in `docs/REST_API.md:373` but is missing from the endpoint
list at `:30`.

**G2 — no document index.** `README.md:75` lists **7 of 19** `docs/`. Four
documents are referenced by **nothing** at all: `CarVisuals.md`,
`EXPERIMENT-fig8-crossing.md`, `RIGHT_OF_WAY_NEGOTIATION.md`,
`SMOOTH_GEOMETRY_DESIGN.md` (the last two are load-bearing: R34/R35 live in one,
the geometry pipeline in the other). Nothing separates "current spec" from
"history" (that separation exists only inside individual files).

**G3 — the client's CLI surface is undocumented anywhere.** `MapRenderer.cs`
parses 23 flags (`--source --map --port --center --zoom --delay --screenshot
--focus --crash-shot --run-test --quit-on-test-done --review --review-name
--review-at --resize --test-pan --test-bar-point --test-jog --test-minimap-pan
--test-seek --test-slot-buttons --test-window-out --motionlog`). There is no
"client options" reference; each flag is scattered through three documents, and
`--review` (how you actually open the reviewer) appears in **no** document as a
usage line — README, `AGENTS.md` and `docs/TESTING.md` never say how to run the
review client at all.

**G4 — the suite's scenario list is nowhere.** `docs/TESTING.md` explains
selection but never prints the current 27 scenario names (they are discoverable
only through `--list` / `--list-json`). The documents' fixed counts (23/24/26)
are the reason this keeps breaking.

**G5 — two run modes are described but not connected to the runner.** `AGENTS.md:193`
lists the five staging steps manually; `scripts/run_e2e.sh:77` implements them as
`--watch` (`MAXIM=1; DG_FREEZE_STAGE=1`). Neither `README.md` nor `docs/TESTING.md`
mentions `--watch`, so the documented procedure and the scripted shortcut
silently diverge.

**G6 — the tape's honesty limits are stated twice, the units never.** `docs/RECORD_PLAYBACK.md`
declares the step cap `Tape.MaxSteps = 200_000` (`DrivingGame.Sim/Tape.cs:69`) but
the same document's own sizing math (100 B/car/step, 66 cars ≈ 24 MB/min) makes
the worst case **~1.3 GB at 66 cars**; nobody states that cap × size, and the
`README.md` cost figure (0.07–0.08 µs/car/substep) and `RECORD_PLAYBACK`'s 100 B/car/step
are never reconciled as the two different quantities they are.

---

## 3. Internal contradictions (inside one file)

- `docs/RIGHT_OF_WAY_NEGOTIATION.md`: header "proposed, not implemented" vs §7i "Built" (D11).
- `docs/SMOOTH_GEOMETRY_DESIGN.md:11` — "**TODO**: should depend on road class / design vehicle per RASt/RAL" is buried in a block-quote addendum, so the open item is invisible to a status scan; and §"One curve, all consumers" still draws the pre-2026-10-19 model (R = 6 m node-centred fillets) while its own addendum says `SmoothChain`/`ROAD_CORNER_RADIUS_M` replaced it.
- `docs/MULTI_CAR_PLAN.md`: "armored" vs "tractor" (D16); "Full e2e suite 21/21" (`:168`) vs "e2e suite 22/22 afterwards" (`:196`) vs today's 26-scenario suite.
- `docs/DRIVING_MANEUVERS.md`: R12/R16 tagged `[proposed]` (`:840`/`:843`) while R20's list (`:950`) calls them not-yet-implemented *and* §7i of the sibling doc says they are built — three states for two rules.
- `docs/RECORD_PLAYBACK.md:52` ("a client **never keeps its own copy** of the world", recording/playback/scrubbing "all live in the simulator") vs `docs/SIMULATIONS_REVIEWER.md:3` / `AGENTS.md:191` ("the reviewer **caches its loaded tape client-side**") vs the implementation (`MapRenderer.review.cs:29-45`, `TapeCarRev`/`TapeFrameRev` cached in the client). Two of the three sentences must be qualified.
- `docs/RECORD_PLAYBACK.md:81` documents `GET /tape/frame?step=N`; `README.md:56` and the code (`GameApi.cs:416`, `MapPost`) use `POST /tape/frame {"step": N}`.
- `docs/TESTING.md:5` (pytest layer) vs `docs/TESTING.md:489` (those suites are historical) — the same document argues with itself 484 lines later.
- `docs/GODOT_FRONTEND.md:15` "the sim … on its own 60 Hz thread" vs `docs/TESTING.md:130` "the sim is **single-threaded**, so this is not the machine's capacity" — the thread is the *host pacing* wrapper (`SimHost.cs`, `Program.cs` sleep+spin), not a second engine thread; SPEC.md:27 repeats the same claim.

---

## 4. Convention drift

- **Status headers.** 12 documents open with the project's `> **Status <date> (code-verified):**` block. The three most load-bearing current documents do **not**: `docs/REST_API.md`, `docs/DRIVING_MANEUVERS.md`, `docs/TESTING.md` (and `docs/UNITS.md`). Without a date stamp, a stale baseline reads as authoritative.
- **Dates are the only freshness signal, and they are wrong too**: 13 docs say "2026-10-19" while their own facts postdate it; `last_context.md` (dated 2026-10-19) describes `feat/unified-units` @ `2651ca2` as the current branch, five commits before `c827710`, and predates the reviewer entirely. It is untracked (deliberate) but reads like a status page.
- **Line-number citations rot.** `MapRenderer.cs:199`, `:1812`, `SimEngine.cs:119-124`, `RoadNetwork.cs:630`, `Raceline.cs:441`, `MapRenderer.cs:2614-2616` — several have already moved (e.g. `rPx` is now `:2709`, `EdgeLineMinZoom` `:1796`). Anchoring on symbols instead of lines would survive the next edit.
- **Unit discipline inside prose.** `docs/SMOOTH_GEOMETRY_DESIGN.md` still writes `radius_px` and "px" for physical lengths; `docs/MULTI_CAR_PLAN.md:144` and `docs/OBSTACLES.md:62/86` keep pygame-era screen language; `docs/lights_report.md:55` and `docs/SIMULATIONS_REVIEWER.md:170` use the px/zoom conversion correctly (these are genuine screen boundaries, allowed by `docs/UNITS.md` rule 3).
- **Root-level documents.** `review_2026-10-20.md` and `last_context.md` sit beside `README.md` and `AGENTS.md` with no pointer from anywhere; both are dated snapshots that nothing declares as snapshots.
- **Historical marking is mostly good** — `C_SHARP_PORT_SPEC.md` ("CLOSED, kept as port history only", with explicit MOVED pointers), `TURN_REWORK_PLAN.md` ("**HISTORY** … do not implement from here"), `GODOT_FRONTEND.md` (phase log), `SPEC.md` (HISTORICAL blocks at `:667`, `:691`), `docs/MULTI_CAR_PLAN.md` (two SUPERSEDED callouts) all read as history correctly. `docs/OBSTACLES.md` marks its palette sections `HISTORICAL (pygame, retired)` and `docs/CarVisuals.md` opens with "NOT implemented in this project". The weak spots are `last_context.md` and `review_2026-10-20.md` (undated in their titles as snapshots) and the D11/D14 status lines that read as present tense.

---

## 5. Adjacent findings (touched by the review, not documentation bugs)

- `docs/UNITS.md` claims "the sim never names a scale factor" — true — but `docs/REST_API.md:566` and `docs/SPEC.md` still describe client-side "px" quantities without naming the boundary, which is how the client's new `Win*Px`/`*FontPx` constants stayed legal-but-undiagnosed.
- `docs/RECORD_PLAYBACK.md`'s "three format generations" claim is implemented in the reviewer (`MapRenderer.review.cs` tape reader) but the reader lives in the **client**, while the same doc puts generation handling in the sim. Worth a line in the doc, not a code change.
- `data/raceline_reference/basic.json` (the golden fixture both `docs/UNITS.md` and `RacelineReferenceTests` depend on) is still committed after the §5 re-baseline; unit suite confirms it passes today.
- Verified clean today (so the documents' "must be empty" claims still hold): `PIXELS_PER_METER` — no hits; `pxPerM|PxPerM` — only `DrivingGame.Sim.Tests/RacelineReferenceTests.cs`.
- Unit suite still matches every document that quotes it: `dotnet test DrivingGame.Sim.Tests/` → **Failed 0, Passed 91, Skipped 2, 27 s** (skips are exactly the two the docs name). `dotnet build` clean, 0 warnings.
- `docs/OBSTACLES.md:131-153` matches the code exactly (`GET /obstacles` list, `POST /obstacles` with `type`/`color`/`x`/`y`, `DELETE /obstacles/{id}`), and the obstacle palette (`blue|yellow|white`, `Obstacles.cs:60`) is still correct — it is the **car** palette that moved to seven names. Good document.

---

## 6. Per-document scorecard

| Document | Verdict | Worst item |
|---|---|---|
| `README.md` | **stale headline** | D2 (reviewer "not yet built"); G1 (tape endpoints table incomplete), G2 (7/19 doc index) |
| `AGENTS.md` | **two wrong facts, both binding** | D6 (`--tests corner_left` selects nothing), D7 (`units` claim violated), plus D2 |
| `docs/SPEC.md` | usable as master spec, **drifted on rules** | D1 (23/23 twice), D15 (hard-bound corridor, old identifiers) |
| `docs/REST_API.md` | accurate where it speaks, **incomplete** | G1 (13 endpoints missing), D3-adjacent (no `/tape` family), `:125` "no pixel frame" is right |
| `docs/TESTING.md` | **two fictional sections** | D3 (pytest layer), D4 (cockpit tool), D5/D9 |
| `docs/UNITS.md` | **best document**, audit line stale | D8 (the "only `rPx`" audit list) |
| `docs/DRIVING_MANEUVERS.md` | authoritative behaviour doc, **rule index broken** | D12 (R32/R34/R35 undefined), D13 (R20 logging list) |
| `docs/RIGHT_OF_WAY_NEGOTIATION.md` | excellent implementation log, **header lie** | D11 |
| `docs/RECORD_PLAYBACK.md` | good, one contradiction | `:52` boundary vs client-side tape cache; `:81` GET-vs-POST |
| `docs/SIMULATIONS_REVIEWER.md` | **current and self-aware** (Review-list fixes + status table) | none material; wants a pointer from README |
| `docs/SMOOTH_GEOMETRY_DESIGN.md` | **half historical inside a current doc** | stale baseline line (`:179`), buried TODO, ASCII model predates its own addendum |
| `docs/TURN_REWORK_PLAN.md` (1,209 l) | correctly marked HISTORY | its §13 "Onboarding for a new implementer (start here)" invites readers into a superseded doc |
| `docs/GODOT_FRONTEND.md` | good, one bad flag | D10 (`--embedded`) |
| `docs/C_SHARP_PORT_SPEC.md` | closed/kept as history — well marked | "C#/.NET 8" in the header vs `net9.0` in `DrivingGame.csproj`; D10 |
| `docs/MULTI_CAR_PLAN.md` | contract still matches code (`add`, `reverse`, `color`, `/cars`) | D16 (`armored`), stale suite counts |
| `docs/OBSTACLES.md` | **accurate**; palette clearly HISTORICAL | none |
| `docs/lights_report.md` | accurate, verified (math, `HAZARD_MIN_DISPLAY_S` 5.0, `light_check.py`) | none |
| `docs/Human Factor Model.md` | accurate (`ReactionTimeS` 1.0, `ComfortBrakingMps2` 5.0, `ReleaseHoldTicks` 30+(uid·14)%31, `AlertGraceS` 1.0, `AlertClearHoldS` 0.3) | open follow-ups at the bottom are never re-checked |
| `docs/CarVisuals.md` | marked NOT implemented | orphan (G2) |
| `docs/EXPERIMENT-fig8-crossing.md` | honest experiment log | D14 (update block reads as current), orphan (G2) |
| `review_2026-10-20.md` | valuable, checkboxes rot | D17 |
| `last_context.md` | **session artifact, should be labelled as such** | five commits behind, predates the reviewer |
| `diary/2026-09-03…10` (6 files) | consistent history, `docs/UNITS.md:88` warns about the old pixel frame | nothing to do |
| `docs/StVO.pdf`, `docs/prompts.txt` | reference data, not prose | `StVO.pdf` (1.1 MB) is referenced only implicitly by rule citations |

---

## 7. Fix queue (suggested order — **not applied**)

1. **The baseline is the root cause of most of the rest.** Re-run the full suite, write one "Current baseline (date, warp): 26 run / 1 skipped, N passed, which scenarios, which errors" block, and make `docs/TESTING.md`, `docs/SPEC.md:826/:1399`, `docs/SMOOTH_GEOMETRY_DESIGN.md:179` and `README.md` all point at that **one** place instead of restating numbers. Until the three failures (esp. `park_block_priority`, which reproduces 1-in-2 on `--tests 17-20`: timeouts in `T_233`, `T_235`, `T_238`; pass in `T_240`) are understood, the honest text is "3 known-red", not "green".
2. `docs/TESTING.md`: delete the pytest layer at `:5-13` and the cockpit section at `:264-283` (replace with the client's `--run-test`/click-to-inspect equivalent), fix `--tests corner_left` → `corner_left_entry`, name the QA scripts count correctly, and document the scenario list via `--list` output rather than hard-coded counts.
3. `README.md` + `AGENTS.md`: stamp the reviewer **built** (`MapRenderer.review.cs`, `--review`), and add the one usage line that currently exists nowhere (how to open a review window on a tape).
4. `docs/REST_API.md`: add the `/tape` family, `/park`, `/follow`, `/segment/by/{name}` — or a one-paragraph pointer to `docs/RECORD_PLAYBACK.md` for tapes; and reconcile the `units` key rule with `/obstacles` + `/tape/frame` so `AGENTS.md:73` stops promising something the API does not do.
5. `docs/DRIVING_MANEUVERS.md` §8: add the missing **R32, R34, R35** entries (they exist in code), and reconcile the R20 "currently logging" list.
6. `docs/RIGHT_OF_WAY_NEGOTIATION.md:3`: re-stamp as "implemented 2026-10-21, §7b–7i are the build log".
7. Add a **document index** to `README.md` splitting *current* (SPEC, DRIVING_MANEUVERS, REST_API, TESTING, UNITS, RECORD_PLAYBACK, SIMULATIONS_REVIEWER, OBSTACLES, CarVisuals/lights_report) from *history* (C_SHARP_PORT_SPEC, TURN_REWORK_PLAN, GODOT_FRONTEND, MULTI_CAR_PLAN, SMOOTH_GEOMETRY, EXPERIMENT, review_2026-10-20, last_context, diary) — this alone disposes of half the orphan/stale-status class.
8. Mechanical: replace line-number citations with symbol names; tick or delete the landed items in `review_2026-10-20.md` §3 (H6, L9, L11, L16); label `last_context.md` as a dated snapshot; add `tests/tape_T_*.txt` to `.gitignore` or move tapes into `logs/` (100 tracked tape artifacts today — 0 of them ignored, while the sibling `run_test_*.log` files **are** ignored, so the ignore file already knows tapes are artifacts and disagrees with the tree).

Items **not** to touch, because they are correct today: `docs/UNITS.md` (except its audit list), `docs/lights_report.md`, `docs/OBSTACLES.md`, `docs/Human Factor Model.md` §7, `docs/CAR_COLORS` contract in `docs/MULTI_CAR_PLAN.md` (except "armored"), the whole `diary/`.

---

## Appendix A — what was run (read-only)

| Command | Result used above |
|---|---|
| `dotnet build DrivingGame.csproj` | Build succeeded, 0 warnings |
| `dotnet test DrivingGame.Sim.Tests/` | Failed 0, **Passed 91, Skipped 2** (27 s) |
| `grep -rn PIXELS_PER_METER --include=*.cs` | empty (doc claim holds) |
| `grep -rln "pxPerM\|PxPerM" --include=*.cs` | `RacelineReferenceTests.cs` only (doc claim holds) |
| `git ls-files '*.cs' \| grep …Px…` | ~20 new `…Px` constants in `MapRenderer.cs`/`MapRenderer.review.cs` (D8) |
| route list from `GameApi.cs` | 33 routes (vs 20 documented) |
| `ls tests/*.py`, `.venv/bin/python -m pytest …` | 3 files, **no pytest installed** (D3) |
| `git log -- tools/controller.py` | empty — never existed (D4) |
| `tests/run_test_T_231…`, `T_229…`, `T_233…241` | current pass/fail evidence (D1, D14) |
| `grep -c MINIMAP_ Config.cs` / orphan `.uid` / `*.csproj.old` / `pygame parity` | open / gone / gone / gone (D17) |
| `grep -n "world pixel"` `RoadNetwork.cs` | `:17` still "world pixel coords" (L10 still open) |
