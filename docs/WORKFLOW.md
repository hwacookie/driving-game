# Issue → branch → fix → test → PR → main

The standing loop for any change to this repo (binding user decision,
2026-10-26). It exists so that a reported problem cannot silently turn into a
commit on `main` without a record of what was wrong, what was tried and what
proved the fix.

GitHub: <https://github.com/hwacookie/driving-game>. Tooling: `gh` (logged in
as the repo owner, token scope `repo`), git over ssh.

## The loop

| # | step | command |
|---|------|---------|
| 1 | the user states the problem | (conversation) |
| 2 | file it as an issue | `scripts/issue.sh "short title" "what is wrong, how to reproduce, expected"` |
| 3 | start the branch off current `origin/main` | `scripts/fix.sh <issue-number> [slug]` |
| 4 | fix it on that branch | edit, `dotnet build` |
| 5 | test it | `scripts/run_e2e.sh --tests <n> --warp 4` (reproduce first, then re-run the named scenario), unit suite `dotnet test DrivingGame.Sim.Tests/...` |
| 6 | open the PR | `scripts/pr.sh` |
| 7 | merge it back and clean up | `scripts/merge.sh` |
| 8 | close the loop | the merge commit references `#<issue>`, GitHub closes the issue |

One issue → one branch → one PR. A second problem is a second issue, even when
it is found while fixing the first (note it in the PR, then file it).

## When the full loop is NOT required

Cosmetic-only changes that no test can express and no player can misbehave:
typo in a comment or doc, a log-line wording, renaming a local variable. Those
go straight to `main` with a short commit message. Anything that changes
behaviour, a verdict, a default, a file format, or a UI affordance takes the
loop — when in doubt, take the loop.

## Rules inside the loop

- **Reproduce before fixing** (project rule): the issue body carries the exact
  command that shows the failure; the PR says which scenario now passes.
- **`main` is never force-pushed, and no branch rebases a pushed commit.**
- **Build gate before any run**: `dotnet build DrivingGame.csproj` must report
  `0 Error(s)` before `run_e2e.sh` is trusted (a stale binary on screen makes a
  whole watched run worthless).
- **Never signal Godot** (`SIGTERM` kills .NET's exit path into an abort):
  close windows through the generation file / `scripts/open.sh --close`.
- **The PR body states the verdict lines verbatim** (the `✅ PASSED` /
  `❌ FAILED` / measured numbers), not "tested".
- Merge style: **merge commit** (`gh pr merge --merge`) so the branch history
  survives; the branch is deleted afterwards.
- `tests/tape_T_*.txt` for the run that proves the fix is committed with the PR
  (that is the repo's existing habit — a reviewer can replay the exact frames).

## Helper scripts

- `scripts/issue.sh TITLE [BODY]` — create the issue, print its number and URL.
- `scripts/fix.sh N [SLUG]` — `git fetch`, branch `fix-N-slug` off
  `origin/main`, refuse a dirty tree.
- `scripts/pr.sh [TITLE]` — commit staged work if any, push, open the PR
  against `main` with a body linking `Fixes #N`.
- `scripts/merge.sh [N]` — merge the PR for the current branch, delete the
  branch, return to `main`, `git pull --ff-only`, then rebuild so the tree the
  next step runs on is the merged one.

All four refuse to act on a dirty working tree unless told (`--allow-dirty`),
because a half-saved edit must never ride into a PR unnoticed.
