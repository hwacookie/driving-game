# Document review — guru

> **Dated snapshot, not a spec** (review artefact, 2026-10-23). Its fix queue
> was executed the same day; what was applied, adjusted and left to the user
> is recorded in `2026-10-23-doc-fixes.md`.

Date: 2026-10-23 · Branch `main` @ `c827710` (working tree dirty: `MapRenderer.cs`,
`MapRenderer.review.cs`) · Scope: **all prose documents** — 19 in `docs/` plus
`README.md`, `AGENTS.md`, `last_context.md`, `review_2026-10-20.md`,
`doc_review_dagobert.md`, and the reference blobs (`docs/StVO.pdf`,
`docs/prompts.txt`, `diary/`, `tests/tape_T_*.txt`).

**No file was changed.** Every finding is an observation with evidence
(`file:line` or `code:line`), checked against the tree as it stands and against
read-only re-runs of the commands the documents claim (`dotnet build`,
`dotnet test`, the suite's own `--list`, the newest run log).

A peer review of the same corpus (`doc_review_dagobert.md`, written today) is
in the tree; §7 states where I confirm it, where its numbers are off, and what
it missed.

---

## 0. Verdict

The set is beautifully written and badly aged. Prose quality, decision
provenance ("binding user decision, 2026‑10‑21") and the habit of marking
history are genuinely good — most files tell you *why*, which is what a doc
should do. The failure is systematic and narrow: **numbers and status lines
freeze, prose keeps moving.** Every document that carries a fact also carries
a date, and the dates are older than the facts they certify.

Three things are wrong in a way that can cost a session:

1. **The e2e baseline printed in five documents is not the baseline.**
   `docs/TESTING.md:102`, `docs/SPEC.md:826`, `docs/SPEC.md:1399`,
   `docs/SMOOTH_GEOMETRY_DESIGN.md:179` and `last_context.md:114` say
   23/23 or 21/23. The suite has **27** numbered scenarios (26 run + 1 skipped)
   and the newest full run (`tests/run_test_T_231_0_20261022_005925.log`,
   `--warp 4`) is **23 passed / 3 failed**: `slow_pass_priority` timed out
   (240 s sim, flow 1.7 m/s per car), `fig8_xing` was stopped at t = 9.1 s by an
   **unexplained centreline crossing** (R33: `lane_guard_errors` 1 → 2) and
   `fig8_xing_plain` at t = 15.1 s when the **R29 desperation valve despawned
   car 96** — a rule `docs/DRIVING_MANEUVERS.md:745` labels
   *"[implemented, **must never fire**]"*. So the two "fixed" crossings are red
   again, with two different causes, while
   `docs/EXPERIMENT-fig8-crossing.md:3-14` and `docs/SPEC.md:829` still claim
   "zero contact" and "survive their full 60 s window" — and nothing has run the
   full suite since: the two commits after that log (`5133074`, `c827710`) are
   unvalidated.
2. **Two entry points deny a shipped feature.** `README.md:12` and
   `AGENTS.md:190` call the Simulationsreviewer "specified, not yet built" /
   "not implemented". `MapRenderer.review.cs` (2,054 lines, committed in
   `c827710`) *is* it, and `docs/SIMULATIONS_REVIEWER.md:3` says "built and in
   use". The two documents everyone reads first are the two that are wrong.
3. **The API reference covers 21 of 32 routes** and omits the whole record &
   playback family — the subsystem the last two commits are about. The tape is
   the project's central review artefact and `docs/REST_API.md` has not heard
   of it.

Volume: **23 wrong facts, 6 coverage gaps, 12 internal contradictions, 8
reference/convention rot items, 6 structural defects.** None of it is a design
lie; all of it is a freshness failure — which matters, because the project's
own convention is `> **Status <date> (code-verified)**`, i.e. the documents
promise exactly the thing they are not delivering.

Also true, and worth saying first: **`dotnet test DrivingGame.Sim.Tests/` →
91 passed / 2 skipped (verified today)** and `dotnet build` is **0 errors, 0
warnings** — so the unit-test claims that docs quote are solid, and the widely
repeated gate line "0 errors / **8 pre-existing warnings**"
(`docs/RIGHT_OF_WAY_NEGOTIATION.md` §7b–§7i, `last_context.md:113`) is itself
stale.

---

## 1. Wrong today (document says X, code/data says Y)

| # | Where | Says | Reality |
|---|---|---|---|
| **F1** | `TESTING.md:102`, `SPEC.md:826`, `SPEC.md:1399`, `SMOOTH_GEOMETRY_DESIGN.md:179`, `RIGHT_OF_WAY_NEGOTIATION.md:547` ("currently **20 of 24**"), `EXPERIMENT-fig8-crossing.md:3-14`, `SPEC.md:829`, `last_context.md:114` | 23/23 · 21/23 · 20/24 — "both crossings survive 60 s with zero contact" | 27 numbered (`test_turning.py --list`), 26 run + 1 skipped; newest full run **23 pass / 3 fail** — `slow_pass_priority` timeout, `fig8_xing` on an R33 unexplained crossing (t = 9.1 s), `fig8_xing_plain` on the **R29 valve firing** (t = 15.1 s, car 96 despawned). **Six different totals in one corpus: 21, 22, 23, 24, 26, 27.** No full run exists for the last two commits |
| **F2** | `README.md:12`, `AGENTS.md:184-191` | reviewer "not yet built" / "specified, not implemented" | `MapRenderer.review.cs` committed; `--review`, `--review-name`, `--review-at` parsed at `MapRenderer.cs:536-552`; `SIMULATIONS_REVIEWER.md:3` "built and in use"; last commit is reviewer work |
| **F3** | `README.md:5` "the clients are **pure API consumers**" | every client → REST | embedded mode reads `_simHost.Engine.StateSnapshot()` **in-process** (`MapRenderer.cs:2012`); the reviewer reads tape files with `System.IO.File` (`MapRenderer.review.cs:649`). The invariant that holds is "the client never simulates" — say that instead |
| **F4** | `GODOT_FRONTEND.md:102` "the world stays flat (x = east, y = north, **2 px/m**)" | a pixel frame still exists in the model | `docs/UNITS.md`: the world frame **IS metres**, `PIXELS_PER_METER` is deleted (verified: grep empty). The one sentence that still contradicts UNITS lives in the frontend architecture doc |
| **F5** | `GODOT_FRONTEND.md:173`, `C_SHARP_PORT_SPEC.md:376` | `scripts/run_e2e.sh --embedded` | the runner has no `--embedded` (its flags: `--no-godot --tests --fast --warp --realtime --focus --focus-car --maximized --watch --zoom --center --fullscreen --stress-cars --map`); embedded is the client's `--source embedded` |
| **F6** | `EXPERIMENT-fig8-crossing.md:66,113,119,178,183,185,205` | "test 23 = `fig8_xing`", "test 24 = `fig8_xing_plain`" | today **23 = `park_6lane_right_lane`**, **24 = the NEGATIVE hairpin test**, 26/27 are the crossings. `--tests 23` now drives a parking scenario; `--tests 24` runs the detector test that *passes when the car goes wrong-side* — the worst possible mismatch to leave in a copy-paste command block |
| **F7** | `TESTING.md:325` "Parking-offset scenarios (`kerb_check` = true, **tests 18-20**)" | three kerb scenarios | exactly **one** (`#23 park_6lane_right_lane`); the other two lateral starts were dropped by user decision 2026-10-19 (`tests/test_turning.py:565-568`). 18/19/20 are `park_block_priority` / `slow_pass_overtake` / `slow_pass_priority` |
| **F8** | `TESTING.md:388` "monitor window (default **30 s**; `s_curve` and roundabout **40 s**)" | durations | 23 turn scenarios are `60.0` s, parking `45.0`, `fig8_stress` `170.0` (`tests/test_turning.py` `DETERMINISTIC_TESTS`; roundabout is 60.0 at `:557`) |
| **F9** | `TESTING.md:375` "**The last scenario** in `DETERMINISTIC_TESTS` is a negative test … it runs last so a full run always ends with every real driving scenario tested first" | negative test is last | it is **#24**; `#25 fig8_stress`, `#26 fig8_xing`, `#27 fig8_xing_plain` run after it. The stated design intent is now false |
| **F10** | `TESTING.md:225` `"TEST 7/24"` | /24 | /27 |
| **F11** | `TESTING.md:119,140,141,172` `DrivingGame.Server --map basic …` as a bare command | on PATH | not on PATH; `README.md:31` correctly uses `dotnet run --project DrivingGame.Server`. Two docs give two different ways to start the same host |
| **F12** | `SPEC.md:654` **`PIXELS_PER_METER`: 10**; `:796` "REST API (Flask, 10+ endpoints)" incl. `GET /screenshot`; `:810` "[x] **Palette UI** next to the minimap: drag & drop"; `:808` "[x] 10 unit tests passing"; `:1127` "**18** named scenarios"; `:804` "(**6** scenarios)"; `:631` `assets/car_sprite.svg`; `:1402` "~19.5k lines C#" | pygame-era facts as current | no pixel frame (F4); ASP.NET Core/Kestrel, Flask gone, `GET /screenshot` removed at M5 and absent from `GameApi.cs`; palette dropped (`GODOT_FRONTEND.md` M4, `OBSTACLES.md:5-9`); 91+2 unit tests; 27 scenarios; `assets/car_sprite.svg` does not exist; tracked `.cs` = **25,388** lines (Python 3,866 ✓) |
| **F13** | `SPEC.md:236` corridor rule 2 as an unconditional **hard** bound ("never enter the oncoming lane"), `:1016-1037` per-vehicle geometry and the `lo` relaxation as **PLANNED**, `:341` `config.ROAD_EDGE_TOLERANCE_M (0.5m)` | hard bound, future relaxation | `Raceline.LegalCorridor(…, double leftReliefM)` implements the conditional relief (R36/§7b/§7f of `RIGHT_OF_WAY_NEGOTIATION.md`); tolerance is **0.35** (`Config.cs:60`) — a value `SPEC.md:692` itself states, so the doc disagrees with itself 360 lines apart |
| **F14** | `AGENTS.md:49` "The **four** manual QA scripts in `tests/`" | four | two (`tests/light_check.py`, `tests/car_line_check.py`) — `TESTING.md:613` lists exactly those two |
| **F15** | `AGENTS.md:163` and `TESTING.md:76` `scripts/run_e2e.sh --tests corner_left` | a working single-scenario example | the suite matches the start-point name **exactly** (`test_turning.py:196-199`); `corner_left` matches nothing → exit 2 with the list. Correct token: `corner_left_entry` (`TESTING.md:415`) |
| **F16** | `AGENTS.md:74` (binding) "**Every** coordinate payload declares `"units": "meters"`" | absolute | `GET /obstacles` answers a bare list with no `units` key (`GameApi.cs:732` → `Obstacles.cs:496`). `REST_API.md:125-128` concedes the exception; the binding rule does not, so the next compliant change inherits a violation |
| **F17** | `UNITS.md:70-81` "Audit results (2026-10-19, verified) — leftover `…Px` names … **only** `rPx`" | exhaustive list | the greps still pass (`PIXELS_PER_METER` empty; `pxPerM` only in `RacelineReferenceTests.cs` — both re-verified today), but the tree now has ~20 more legal `…Px` names: `MapRenderer.cs:125-128` (`WinGapPx`, `WinTitleBarPx`, …), `MapRenderer.review.cs:66-73` (`ReadoutFontPx`, `StripPx`, …). The audit reads as a closed list; it is a procedure |
| **F18** | `MULTI_CAR_PLAN.md:61` `assets/car_{…,**armored**,…}.png` | armored | `Config.CAR_COLORS` = blue, silver, police, tan, **tractor**, pickup, mixer (`Config.cs:102`; `assets/car_tractor.png`); the same doc's contract block at `:78` uses `tractor` |
| **F19** | `RIGHT_OF_WAY_NEGOTIATION.md:3-4` "**Status 2026-10-20: proposed, not implemented.** Nothing in this document exists in the code yet" | not implemented | its own §7i (`:615-640`) is a **built** table, and the code is there: `DrivingGame.Sim/Negotiation.cs`, `Car.EmergencyRole` (`SimEngine.cs:306,352,428`), `CourtesyForUid`/`CourtesyKind` exported (`SimEngine.cs:1259-1260`). A reader stops at line 3 and reimplements a finished subsystem |
| **F20** | `DRIVING_MANEUVERS.md:24` "Reproducible headless: `.venv/bin/python scripts/sim_park.py [startpoint] [--dest]`" | a runnable repro tool | `scripts/` holds `run_e2e.sh` and `setup.sh`. The flagship behaviour doc's only reproduction command cannot run — and `AGENTS.md`'s "reproduce first" rule gives no substitute |
| **F21** | `last_context.md` (dated 2026-10-19) presents itself as the live context block | current | five commits behind; `MapRenderer.cs:199` for the generation-file watch (now `:282`), `LookAheadMaxM=250.0` and "ring = 14 cells" (code: **300.0** → **16**, `CarCollisions.cs:63,543`), baseline "21/23", "Running processes: none", and it predates the review client entirely |
| **F22** | `review_2026-10-20.md` §3 (37 items, all `[ ]`) + `:387` "the 21/23 baseline" | nothing landed | already done and still unticked: **L9** (both orphan `.uid` sidecars gone), the "pygame parity" rewrite (0 occurrences in `*.cs`), **L16**'s `DrivingGame.csproj.old` gone, **H6** (`DrivingGame.Sim/LaneGeometry.cs` exists, and §5 of the same file says so). Still open, confirmed: **L1** (7 `MINIMAP_*` constants in `Config.cs`), **L10** (`RoadNetwork.cs:17` still comments `X1…Y2` as "**world pixel coords**"), and **L11 is only half done** — `MapRenderer.cs:373` still points at `docs/C_SHARP_PORT_SPEC.md (TODO, not implemented)` for a spec that closed on 2026-09-08 |
| **F23** | `SIMULATIONS_REVIEWER.md:323` — the review client's only "Known intermittent" note names **`park_block_priority`** | that is today's flaky scenario | the note is hedged ("in an earlier batch run before the cap side … landed"), but it is the corpus's only pointer at a flaky scenario and it aims at #18, which **passed** in 17.0 s in the last full run; the live timeout is **#20 `slow_pass_priority`** (`T_231`). A reader chasing "the intermittent one" opens the wrong scenario |

**Related, and the mirror image of F17:** the R7 entry in
`DRIVING_MANEUVERS.md:618-624` is **right** ("`LookAheadMaxM` 300 m … 16 cells")
while the in-code comment `CarCollisions.cs:31` says "**21 cells**" for the same
expression. A future doc-vs-code reconciliation driven by the comment would
"fix" the correct doc into a wrong one. Flagging so the next pass fixes the
comment, not the rule.

---

## 2. Coverage gaps (needed, absent)

**G1 — the API reference is two thirds complete.** `GameApi.cs` registers
**32** routes; `docs/REST_API.md:30-52` documents **21**. Missing entirely:
`GET /segment_names`, `GET /segment/by/{name}`, `POST /park` (the R32
obstruction primitive), `POST /follow`, and all seven
`/tape` endpoints — the last group documented only in
`RECORD_PLAYBACK.md:68-90`, which `REST_API.md` never links to (full set in
Appendix C).

**G2 — there is no document index.** `README.md:75` lists 7 of 19 `docs/`. Six
documents are referenced by **nothing**: `CarVisuals.md`,
`EXPERIMENT-fig8-crossing.md`, `RIGHT_OF_WAY_NEGOTIATION.md`,
`SMOOTH_GEOMETRY_DESIGN.md`, `Human Factor Model.md`, `prompts.txt`. Two of
those are load-bearing: **R34/R35 live only in `RIGHT_OF_WAY_NEGOTIATION.md`**
and the whole geometry pipeline only in `SMOOTH_GEOMETRY_DESIGN.md`. Nothing in
the corpus separates *current spec* from *history* at the set level — that
distinction exists only inside individual files, so a newcomer cannot rank them.

**G3 — the client's CLI is documented nowhere as a surface.** 23 flags are
parsed; `--review` (the only way to open the reviewer) appears in **no usage
line in any document** — not in `README.md`, not in `AGENTS.md`, not in
`TESTING.md`. `--review-at`, `--run-test`, `--test-bar-point`, `--test-jog`,
`--test-minimap-pan`, `--test-scrub-drag`, `--test-slot-buttons`,
`--test-window-out` are in no document at all. So the corpus announces a second
client without telling anyone how to start it.

**G4 — the two run modes have a scripted shortcut nobody documents.**
`AGENTS.md:196-202` spells out the five manual staging steps;
`scripts/run_e2e.sh:77` implements them as `--watch` (`MAXIM=1;
DG_FREEZE_STAGE=1`). `--watch` is named once, inside
`RIGHT_OF_WAY_NEGOTIATION.md:610`. Documented procedure and scripted
behaviour will drift, and the `--maximized`/`--zoom`/`--center` pair that
`AGENTS.md`'s own mode description depends on is absent from `TESTING.md`'s
runner-flag list (`:74-77`).

**G5 — the scenario list is never in the docs.** `TESTING.md` explains
selection mechanics but never prints the 27 names; every document hard-codes a
**count**, which is precisely why the counts rot (F1). One table generated from
`--list` (or a pointer to it) removes six stale numbers at once.

**G6 — the tape's limits are never stated in the units that matter.**
`RECORD_PLAYBACK.md:50` names `Tape.MaxSteps` (`DrivingGame.Sim/Tape.cs:69` =
**200 000**) and `:48` gives ~100 B/car/step ("66 cars ≈ 24 MB/min") — the two
numbers are never multiplied, so nobody can see the worst case (~1.3 GB at 66
cars). The cost figure (`0.07-0.08 µs/car/substep`, `README.md:72`) and the
size figure are different quantities that read like one claim. And no document
states the retention rule for the **100 committed `tests/tape_T_*.txt`** files,
while `.gitignore` ignores `tests/run_test_*.log` and `logs/` — the ignore file
already disagrees with the tree.

---

## 3. Internal contradictions (one file arguing with itself)

| # | File | The two statements |
|---|---|---|
| **C1** | `SIMULATIONS_REVIEWER.md:156` vs `:175` | the readout label's `\| part \| meaning \|` table appears **twice**, and the copies disagree: `head 820` vs `head 828`, `frame 743/820` vs `743/828`, viewer trail "`live head 820 · drawn 814`" vs "`live head 828 · drawn 822`". The doc's most exact artefact — the label that is supposed to make a screenshot self-describing — has two versions of itself |
| **C2** | `SIMULATIONS_REVIEWER.md:198` vs `:354-357` | "the button **also copies the PNG to the system clipboard**" vs a whole later section "**Clipboard: text only, settled** … copies its **path**, not the image". Code agrees with the second (`DisplayServer.ClipboardSet` of a string, `MapRenderer.cs:1419`) |
| **C3** | `SIMULATIONS_REVIEWER.md` "Display truth" section vs `:256` | the doc's central promise (every smoothed pose bracketed by two real frames) is contradicted 100 lines later by its own open defect 6: "at 8x the blue car was drawn **822 substeps stale** while the label said `frame 5752`". The promise carries no "currently broken at the head" marker |
| **C4** | `SIMULATIONS_REVIEWER.md:108` ("The one data gap this exposes", **Fix:** record decision text on the tape) | `RECORD_PLAYBACK.md:33-38` + the tape format already do it (`step;…;~uid=text`), and the reviewer parses it. The gap section is written as open work |
| **C5** | `RECORD_PLAYBACK.md:56` "a client **never keeps its own copy** of the world"; boundary table `:66`: scrub controls "**not `MapRenderer`**" | `AGENTS.md:184` "a separate client, its own window", `SIMULATIONS_REVIEWER.md:8` "a second window of the same client", and the implementation: `MapRenderer.review.cs` is `public partial class MapRenderer` and caches the whole tape client-side (`:29-45`). Three different statements of one boundary; the binding decision of 2026-10-21 ("not `MapRenderer`") is the one the code did not keep |
| **C6** | `RECORD_PLAYBACK.md:74` `GET /tape/frame?step=N` | `README.md:67` and `GameApi.cs:416` `app.MapPost("/tape/frame", {"step":N})` |
| **C7** | `TESTING.md:5-13` — headless **pytest** suites are "layer 1 … the first thing to run after a change", with the command line | `TESTING.md:486-491` — those suites "lived in `/Users/hauke/prj/car`". Neither file exists, and `.venv` has no pytest; the real headless layer is the xunit suite. The document's opening move is fiction |
| **C8** | `RIGHT_OF_WAY_NEGOTIATION.md:3` "nothing exists in the code" | `:615` §7i "Built" — plus §5's own work order step 1 "**Document R32 in §8**", never executed: `DRIVING_MANEUVERS.md` §8 has **no R32 entry** (two passing mentions at `:767`, `:783`) although `r32#passive` is a live cap key (`CarCollisions.cs`) |
| **C9** | `DRIVING_MANEUVERS.md:840` `R12 *[proposed]*`, `:843` `R16 *[proposed]*`, `:950` "the **not-yet-implemented** rules (R9–R12, R16, …)" | R12 (courtesy) and R16 (anti-starvation via `Car.StandstillS`) are implemented per §7i and in code (`Negotiation.cs`, `CarCollisions.cs` `CourtesyForUid`). Three different status claims for two rules across two files |
| **C10** | `EXPERIMENT-fig8-crossing.md:188` "runs to **300 s** of sim time" | `:41`, `:52`, `:74` of the same doc: the criterion is **60 s** since 2026-10-19; `:113`'s own log line "crash at 7.6 s … well short of the 300 s window" keeps the old number as if current |
| **C11** | `GODOT_FRONTEND.md:176` "### Phase 2 — planning outside (NOT done)" | `:223` repeats "### Phase 2 — planning outside (goal state)" **inside §7 "What the client draws today"** — duplicated content filed under an unrelated heading |
| **C12** | `EXPERIMENT-fig8-crossing.md:20-22` "This document is the shared reference … **NOT modified by the fix branches**" | lines 3-14 are exactly such a modification (the 2026-10-19 update block), and the experiment's result (Opus 4.8 vs Qwen 3.8, §5) is **recorded nowhere** — the doc is an open protocol for an experiment that is over |

---

## 4. Reference and naming rot

- **R1 — dead file paths.** 40+ path tokens in the documents name files that do
  not exist anywhere in the tree. Most are honestly labelled history
  (`SMOOTH_GEOMETRY_DESIGN.md:141-166` says "Historical (the Python files of
  the retired `car/` repo)"; `SPEC.md:743-751` likewise). The offenders **in
  live, unmarked prose**: `TESTING.md:5-13` + `:264-283` (pytest suites,
  `tools/controller.py`), `SPEC.md:122` (`src/bicycle_nav.py`), `SPEC.md:228`
  (`src/raceline.py`), `SPEC.md:1363` (`--dump`), `DRIVING_MANEUVERS.md:24`
  (`scripts/sim_park.py`), `REST_API.md:523` (`tests/test_api.py`),
  `SMOOTH_GEOMETRY_DESIGN.md:103,165` + `TURN_REWORK_PLAN.md` (×5, in the
  HISTORY doc: acceptable), `MULTI_CAR_PLAN.md:99,116,209` (step headers naming
  `src/rest_api.py` / `src/main.py` while the header claims "the rest still
  matches").
- **R2 — dead heading pointers, in the wrong language.** `SPEC.md:134` and
  `:412` cite `DRIVING_MANEUVERS.md` §1 "**Variante: Sackgassenende /
  Route-Ende**", `:319` cites §4 "**Kreuzungsmitte (der weiße Punkt)**", `:404`
  cites "**Abbiegen**" → "**Erreichbarkeit**". The target headings are English
  ("Variant: Dead-end / route end" `:112`, "Crossing center (the white dot)"
  `:275`, "Reachability" `:305`). Dead links **and** a small clash with
  `AGENTS.md`'s English-documentation rule (German domain terms are fine and
  used well — quoted headings are not).
- **R3 — the un-linkable filename.** `docs/Human Factor Model.md` is the only
  document with spaces in its name, so no reference to it is a link (plain text
  at `SPEC.md:156`, `:979`) and my link validation could not check it. Rename to
  `HUMAN_FACTORS.md` and link it — it is cited by two load-bearing rules (R7,
  R11).
- **R4 — name does not match content.** `docs/CarVisuals.md` is not about car
  visuals: it is "how to build a one-shot crash smoke puff in Godot (NOT
  implemented)". The obvious title is `CRASH_EFFECT_SMOKE.md`; today it is both
  misleading and orphaned (G2).
- **R5 — stale branch identities.** `SMOOTH_GEOMETRY_DESIGN.md:4` "implemented on
  branch `turn-planning-rework`" (branch gone); `EXPERIMENT-fig8-crossing.md`
  §5 names `fix/fig8-crossing-opus48` / `-qwen38` (both gone);
  `last_context.md:150` still calls `feat/unified-units` the current branch
  (merged). Branch names rot faster than dates.
- **R6 — a payload key that no longer exists, with a unit-breaking name.**
  `SMOOTH_GEOMETRY_DESIGN.md:28-31`: "The `junction_fillets` entries now carry
  `corner` + `arc` (+ seg_a/seg_b, **radius_px**)". `/map` has no
  `junction_fillets` key (full key set from `MapPayload.cs`: `road_strips`,
  `road_polygons`, `segments`, `paved_edge_rings`, `centerlines`,
  `lane_markings`, `stop_lines`, `crossing_markers`, `oneway_arrows`,
  `parking_marks`, `signs`, `elevated_*`, `marking_style`, `start_points`,
  `bounds`, `units`, `road_color`, `bg_color`) — and `radius_px` breaks
  `UNITS.md` rule 3 (a physical length is never `…Px`).
- **R7 — status headers are the convention and the four most-used docs skip
  it.** 12 files open with `> **Status <date> (code-verified):**`. Missing on
  `REST_API.md`, `DRIVING_MANEUVERS.md`, `TESTING.md`, `UNITS.md`, plus
  `README.md`/`AGENTS.md`/`last_context.md` which carry **no date at all**. The
  docs with the largest blast radius are the ones a reader cannot date.
- **R8 — `file:line` anchors rot faster than symbols.** `RECORD_PLAYBACK.md:26`
  cites `SimEngine.cs:119-124` for the substep clock; those lines are now the
  `_cars`/`_carFlags` dictionaries. `last_context.md:26` cites
  `MapRenderer.cs:199` for the generation-file watch (now `:282`). Anchoring on
  `SimEngine._simStepsTotal` / `MapRenderer.GodotGenFile` costs nothing and
  survives the next edit.

---

## 5. Structural defects

- **S1 — a machine sign-off leaked into a specification.**
  `RIGHT_OF_WAY_NEGOTIATION.md:562`:
  `Pacey (cool): Design doc written — implementation order, one number to measure.`
  That is an assistant's end-of-answer line sitting between §10 and §7g of a
  design document. Delete it, and consider a rule that agent chatter never
  enters a file.
- **S2 — append-at-bottom section order.** `RIGHT_OF_WAY_NEGOTIATION.md` runs
  §7, §7b-§7f, §8 (`:508`), §9 (`:529`), §10 (`:543`), then §7g (`:564`), §7h
  (`:586`), §7i (`:615`). `EXPERIMENT-fig8-crossing.md` puts §6 (`:157`) before
  §5 (`:198`). `SIMULATIONS_REVIEWER.md` places "Implementation status
  (2026-10-21)" (`:300`) *after* "Review-list fixes (2026-10-22)" (`:238`) and
  "One fixed HUD size (2026-10-22)" (`:275`), so the oldest state is read last
  and wins.
- **S3 — headings whose body was deleted.** `RECORD_PLAYBACK.md:187` "## Gates"
  is followed by two blank lines before the content; `SIMULATIONS_REVIEWER.md:225`
  "## Implementation order" likewise. Both look like a table that was cut.
- **S4 — non-prose blobs in `docs/` with no index entry.** `prompts.txt` is a raw
  German voice-note transcript (5 KB, referenced only by a diary line) and
  `StVO.pdf` (1.1 MB) is never cited by filename although the ruleset quotes
  StVO §§ throughout `DRIVING_MANEUVERS.md`. The transcript belongs in `diary/`;
  the PDF deserves one line saying which §§ it grounds.
- **S5 — review artefacts accumulate at the root with no convention.**
  `review_2026-10-20.md` and `doc_review_dagobert.md` (plus this file) sit
  beside `README.md` and `AGENTS.md`, nothing declares them snapshots, nothing
  links them, and their checkbox/state vocabulary differs. A `docs/reviews/`
  directory and a one-line "dated snapshot, not a spec" stamp would stop them
  competing with the specs — and stop two reviews disagreeing about how many
  endpoints exist (Appendix B).
- **S6 — the suite's own summary cannot be quoted honestly.** `T_231` prints
  "Total tests: 26 / ✅ Passed: 23", then per-category failures of `1 + 1`, then
  "⚠️ 3 of 26 test(s) failed" — the crash category is not among the printed
  lines. Any doc that copies the summary block will look self-inconsistent.
  Worth one line in `TESTING.md` ("read the `⚠️ N of M` footer, not the
  per-category lines") plus a suite fix.

---

## 6. Per-document scorecard

| Document | Verdict | Worst item |
|---|---|---|
| `README.md` | stale headline | F2 (reviewer "not yet built"), F3 ("pure API consumers"), G1/G2/G3 |
| `AGENTS.md` | two binding rules stated too absolutely | F15 (`--tests corner_left` matches nothing), F16 (`units` everywhere), F14, F2 |
| `docs/SPEC.md` | right master doc, drifted facts | F12 (pixel frame, Flask, `/screenshot`, palette checkbox, LOC), F13 (hard corridor, self-contradicting tolerance) |
| `docs/DRIVING_MANEUVERS.md` | best reasoning in the corpus, **broken rule index** | C8/C9 — **R32, R34, R35 have no entry** though all three are implemented; F20 (dead repro command); no status header; R1…R36 advertised with silent gaps |
| `docs/REST_API.md` | accurate where it speaks, 2/3 complete | G1 (11 routes missing, `/tape` family absent), C6-adjacent |
| `docs/TESTING.md` | **two fictional sections**, wrong counts | C7 (pytest layer), F7/F8/F9/F10/F11 |
| `docs/UNITS.md` | the model citizen | F17 (frozen audit list); add "the audit is a procedure" |
| `docs/RIGHT_OF_WAY_NEGOTIATION.md` | superb build log, **header lie** | F19, C8, S1, S2, F1 (its "20 of 24") |
| `docs/SIMULATIONS_REVIEWER.md` | current, self-aware, **duplicated inside** | C1, C2, C3, C4, G3 (`--review` unlaunched/undocumented), S2, S3 |
| `docs/RECORD_PLAYBACK.md` | good, two wrong turns | C5 (client-never-copies vs reality), C6 (GET vs POST), G6, S3 |
| `docs/GODOT_FRONTEND.md` | useful phase log | F4 ("2 px/m"), F5 (`--embedded`), C11, and it never mentions the review client it now shares a codebase with |
| `docs/SMOOTH_GEOMETRY_DESIGN.md` | half history inside a current doc | R6 (`junction_fillets`/`radius_px`), F1 (`23/23`), stale branch, buried TODO (`:11`) |
| `docs/MULTI_CAR_PLAN.md` | contract still holds | F18 (`armored`), R1 (Python step headers), stale counts |
| `docs/OBSTACLES.md` | **accurate**, palette clearly HISTORICAL | one leftover sentence: REST section still says obstacles "can be dragged/trashed in the UI" (`:151`) |
| `docs/lights_report.md` | verified accurate | none (math, `HAZARD_MIN_DISPLAY_S` 5 s, `light_check.py`, `rPx` boundary all check out) |
| `docs/Human Factor Model.md` | verified accurate | R3 (un-linkable name), no top status header, `:2` scope sentence still says "fig-8 lemniscate" only |
| `docs/CarVisuals.md` | marked NOT implemented | R4 (misleading name), orphan (G2) |
| `docs/EXPERIMENT-fig8-crossing.md` | honest log, wrong numbers | F6 (tests 23/24 → 26/27), C10 (300 s), C12, S2 |
| `docs/TURN_REWORK_PLAN.md` | correctly HISTORY | `:1038` "§13 … (**start here**)" invites readers into a superseded doc |
| `docs/C_SHARP_PORT_SPEC.md` | closed, well marked | ".NET 8" in the header vs `net9.0`; F5 |
| `review_2026-10-20.md` | valuable, rotting checkboxes | F22, S5 |
| `last_context.md` | **session snapshot masquerading as status** | F21, S5 |
| `diary/` (6), `tests/tape_*` (100), `StVO.pdf`, `prompts.txt` | history / artefacts | S4, S5, G6 (retention undocumented) |

---

## 7. Cross-check against `doc_review_dagobert.md`

**Confirmed independently** (I re-derived each from code or logs): the reviewer
status lie, the fictional pytest layer and cockpit section, `--embedded`, the
`units`-key exception at `/obstacles`, the stale `…Px` audit list,
`--tests corner_left`, the RIGHT_OF_WAY header lie, missing R32/R34/R35,
`armored`, the `GET`-vs-`POST` tape-frame mismatch, the client-caches-the-tape
contradiction, the missing doc index, the undocumented CLI, `--watch`, the tape
sizing gap, and the stale review checkboxes / `RoadNetwork.cs:17` "world pixel
coords".

**Corrections to its numbers** (mine are from the code, Appendix A/C):

- routes are **32**, documented **21** — not 33/20; and
  `GET /car/{uid}/decisions` **and** `GET /segment/{idx}` *are* in the
  `REST_API.md` table, so they are not gaps (its G1 overstates by two).
- `POST /tape/frame` **does** declare `"units": "meters"`
  (`GameApi.cs:444`) — the units-rule exception is `GET /obstacles` alone, not
  "the tape frame + obstacles".
- `--tests corner_left` fails **loudly** (suite prints the available list,
  exits 2) — a broken example, not a silent zero-selection.
- `review_2026-10-20.md` §3 has **37** items, not 36, and **L11 is only half
  landed**: `MapRenderer.cs:373` still carries the `C_SHARP_PORT_SPEC.md (TODO,
  not implemented)` reference the item also demands removing.
- "the sim on its own 60 Hz thread" (`GODOT_FRONTEND.md:169`) vs "the sim is
  single-threaded" (`TESTING.md:130`) is **not** a contradiction — one thread,
  host-paced. Drop that item.
- Build today is **0 warnings**, which invalidates both docs' repeated
  "8 pre-existing warnings" gate line.

**Additions in this review** (absent from the peer): F3, F6, F7, F8, F9, F10,
F11, F12 (the SPEC cluster), F13, F22 + the `CarCollisions.cs:31` comment trap,
C1-C5 in full detail (duplicate readout table with divergent numbers, the
clipboard contradiction, the unmarked display-truth defect, the "data gap"
section, the "not `MapRenderer`" decision vs `partial class MapRenderer`),
C10-C12, R1-R8 (dead heading pointers in German, the un-linkable filename,
`junction_fillets`/`radius_px`, stale branch names, status-header asymmetry),
S1 (the leaked `Pacey` line), S2-S6, and the fact that **no run on record
validates HEAD** — newest log is 2026-10-22 00:59, while `5133074` (right-of-way
+ review client) landed 01:34 the same night and `c827710` on 2026-10-23.

---

## 8. Fix queue (proposed order — **not applied**)

1. **One baseline, one home.** Re-run the full suite on HEAD (`--warp 4`),
   then write a single "Current baseline" block (date, warp, 26 run + 1 skipped,
   passed, **named** failures) and make `TESTING.md:102`, `SPEC.md:826`/`:1399`,
   `SMOOTH_GEOMETRY_DESIGN.md:179`, `RIGHT_OF_WAY_NEGOTIATION.md:547` and
   `README.md` point at it instead of restating numbers. Until
   `slow_pass_priority` and the two crossings are understood the honest wording
   is "**3 known-red**", not "green" — and `EXPERIMENT-fig8-crossing.md:3-14`,
   `SPEC.md:829` and `DRIVING_MANEUVERS.md`'s R14 status line must carry the
   same correction. The R29 valve firing is the loudest single fact here: a rule
   documented as "must never fire" **did** fire, so `DRIVING_MANEUVERS.md:745`
   needs a measured status line, not an aspiration.
2. **`TESTING.md`**: delete the pytest layer (`:5-13`) and the cockpit section
   (`:264-283`, replacing it with `--run-test` / click-to-inspect), renumber
   everything (`:225`, `:325`, `:375`, `:388`), give the start command in
   README's `dotnet run` form, and paste the `--list` output in place of counts.
3. **`README.md` + `AGENTS.md`**: stamp the reviewer **built** and add the one
   line that exists nowhere — how to open a review window on a tape
   (`Godot --path . -- --source embedded --review [path] [--review-name <role>]
   --center <x> <y> --zoom <px/m>`); fix `--tests corner_left` →
   `corner_left_entry`; "four manual QA scripts" → two; soften `AGENTS.md:74` to
   "every coordinate **object** declares its unit — `GET /obstacles` is a bare
   list and is metres by convention" (or add the key and keep the rule absolute).
4. **`REST_API.md`**: add the 11 missing routes (or a pointer to
   `RECORD_PLAYBACK.md` for the tape family), and open the file with a status
   header so its completeness is dated.
5. **`DRIVING_MANEUVERS.md` §8**: add entries for **R32** (implemented, cap key
   `r32#`, `r32#passive`), **R34**, **R35** (implemented in `Negotiation.cs`),
   reconcile R12/R16/R20's status tags, and say explicitly that R# numbering
   skips nothing / lists what was retired. Add a top status header and a working
   reproduction pointer in §1 in place of `scripts/sim_park.py`.
6. **`RIGHT_OF_WAY_NEGOTIATION.md`**: re-stamp the header
   ("implemented 2026-10-21, §7b–§7i are the build log"), move §7g–§7i into §7,
   delete the `Pacey` line, refresh §10's baseline reference to step 1.
7. **`SIMULATIONS_REVIEWER.md`**: collapse the duplicated readout table (one
   set of numbers), resolve the clipboard contradiction in favour of "text
   path only", mark the Display-truth section with the open 8× staleness,
   rewrite "The one data gap" as done; add a pointer from `README.md`.
8. **`RECORD_PLAYBACK.md`**: `POST /tape/frame`, reconcile "the client never
   keeps a copy of the world" with "the reviewer caches its tape" (the honest
   form: *the client may cache recorded history; it never re-simulates*),
   document the `MaxSteps` × size product and tape retention.
9. **Rename/retire**: `Human Factor Model.md` → `HUMAN_FACTORS.md`,
   `CarVisuals.md` → `CRASH_EFFECT_SMOKE.md`, move `prompts.txt` to `diary/`,
   add `docs/reviews/` for dated review snapshots, add `tests/tape_T_*.txt` to
   `.gitignore` (or move tapes under `logs/`) so the ignore file and the tree
   agree.
10. **Mechanical**: replace `file:line` with symbol anchors; fix the stale
    `CarCollisions.cs:31` cell count (16, not 21) and `RoadNetwork.cs:17`
    "world pixel coords" (both are code-comment defects the docs' greps cannot
    see); tick/annotate the landed items in `review_2026-10-20.md` and label
    `last_context.md` as a dated snapshot.

**Do not touch** — correct today (re-verified): `docs/lights_report.md`,
`docs/UNITS.md`'s rules (its two greps still pass), `docs/Human Factor Model.md`
§7 (`ReactionTimeS` 1.0, `ComfortBrakingMps2` 5.0, `ReleaseHoldTicks`
30+(uid·14)%31), `docs/OBSTACLES.md`'s REST + geometry sections (colours
`blue|yellow|white` are the *obstacle* palette and are still right),
`docs/CarVisuals.md`'s advice, and the whole `diary/`.

---

## Appendix A — what was run (all read-only)

| Command | Result used above |
|---|---|
| `dotnet test DrivingGame.Sim.Tests/` | Failed 0, **Passed 91, Skipped 2**, 27 s — docs' claim holds |
| `dotnet build DrivingGame.csproj` / `.Server` / `.Sim.Tests` | 0 errors, **0 warnings** (docs say 8) |
| `.venv/bin/python tests/test_turning.py --list` | **27** numbered scenarios (25 = `fig8_stress`, 26/27 = crossings) |
| `tests/run_test_T_231_0_20261022_005925.log` | newest full run: 26 run + 1 skipped, **23 passed, 3 failed** |
| `git log -3 --date=iso` | `c827710` 2026-10-23 · `5133074` 2026-10-22 01:34 → no run on record for HEAD |
| route extraction from `GameApi.cs` | **32** routes; `REST_API.md` table **19 rows / 21 routes** |
| `grep PIXELS_PER_METER --include=*.cs` / `pxPerM` | empty / `RacelineReferenceTests.cs` only |
| `…Px` name sweep | ~20 screen-frame constants in `MapRenderer*.cs` (all rule-3 legal) |
| `ls tests/*.py`, `grep kerb_check` | 3 scripts (2 QA); **one** `kerb_check` scenario |
| `grep -n "MINIMAP" Config.cs` / orphan `.uid` / `pygame parity` / `csproj.old` | 7 open / gone / 0 gone / gone |
| `grep -c "" <<<$(git ls-files '*.cs' \| xargs cat)` | 25,388 C# lines; 3,866 Python |

## Appendix B — the two reviews disagree about the suite's size

`doc_review_dagobert.md` counts 33 routes / 20 documented and 36 review items;
this review counts 32 / 21 and 37. Both are one grep away from the truth, which
is exactly the argument for §8 step 1 and step 4: **numbers that live in prose
must have one source**, or the reviews of the docs will need reviewing.

## Appendix C — endpoint coverage (code as authority)

| In `GameApi.cs` | In `REST_API.md` |
|---|---|
| `/health` `/state` `/map` `/start_points` `/segment/{idx}` `/segment/by/{name}` `/segment_names` `/car/{uid}/decisions` `/control` `/reset` `/teleport` `/cars` `/flags` `/label` `/toggle` `/freeze` `/wait` `/tests` `/run_test` (GET+POST) `/obstacles` (GET/POST/DELETE) | documented (21 routes; `/car/…`, `/segment/{idx}`, `/obstacles` complete incl. payload shapes) |
| `/segment_names` `/segment/by/{name}` `/park` `/follow` `/tape` `/tape/clear` `/tape/frame` `/tape/index` `/tape/list` `/tape/load` `/tape/save` | **undocumented (11)** — `/tape/*` appear only in `RECORD_PLAYBACK.md:68-90`, which `REST_API.md` does not link |
