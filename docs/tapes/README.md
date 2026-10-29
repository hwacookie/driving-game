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

Nothing — every tape was deleted on 2026-10-29 (24 files, 33 MB, including the
two that had been committed). The folder exists so the next recording worth
keeping has an agreed home instead of `tests/`.
