# Tapes kept as documentation

A tape is the frame-by-frame recording a run writes to `tests/tape_T_*.txt`
(the format and its channels: [RECORD_PLAYBACK.md](RECORD_PLAYBACK.md)). Those
recordings are **run artifacts and gitignored** — `docs/WORKFLOW.md` states the
rule. A file in *this* folder is the single exception: a recording kept because
it **shows** something prose cannot.

## When a tape belongs here

* it is the evidence behind a claim in a doc — a stutter, a negotiation, the
  frame a crash happened on;
* a `docs/*.md` measurement or a replaying test names it;
* **and it can be verified**: its header carries the wide `v2:` map stamp, so a
  review window can check the ground beside it and say `map check: ok`. A tape
  that cannot be verified against its ground is an anecdote, not evidence.

## Naming

`<what-it-shows>-T_<run>.txt` — e.g. `bridge-occlusion-T_303.txt`. The name
carries the reason it survived the run; the suffix keeps the link back to the
run log (`tests/run_test_T_*.log`, itself not in git).

## How to look at one

```bash
TAPE=docs/tapes/bridge-occlusion-T_303.txt
"/Applications/Godot_mono.app/Contents/MacOS/Godot" --path . -- \
      --source embedded --map basic --port 5032 \
      --review "$TAPE" --review-at 3308 --maximized
```

`--map` names the ground from the tape's own `map=` header — a review window
refuses to paint cars on a ground it cannot verify
(`docs/SIMULATIONS_REVIEWER.md` → *Launching the two windows*).

## Adding one

Deliberately, by name: `git add docs/tapes/<file>.txt`. The whole point of the
ignore rule is that `git add -A` must never carry a recording again: on
2026-10-29 a `scripts/pr.sh` sweep put a 14 MB tape into a PR nobody asked for
(PR #3, since merged). The ignore rule closes that door, an explicit `git add`
opens it for the one case that is wanted.

## What is here now

* **`deck-car-level-T_probe.txt`** — 259 substeps of one car driven onto the
  figure-8's deck (`fig8_n12→fig8_n13`), the evidence behind the `level` column
  and the map-reference checks in
  [RECORD_PLAYBACK.md](RECORD_PLAYBACK.md) and [../TODO.md](../TODO.md): on its
  own ground the car paints **on** the bridge with the deck's white edge lines
  over the road beneath it; on a foreign ground the window paints **no cars**
  (`MAP MISMATCH — refused`). It was `tests/tape_probe_v2.txt` until
  2026-10-29, when this folder's rule was written, and its five citations were
  moved with it.

Everything else is gone: the sweep of 2026-10-29 deleted all 24 run recordings
(33 MB, the two committed ones included) and `tests/tape_decisive.txt` (193
substeps of a `deck_probe`, cited by nothing) went with them as dead evidence.
A tape nobody points at is not documentation — that is the test for this shelf.
