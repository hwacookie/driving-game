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
| 7 | merge it back and clean up | `scripts/merge.sh` (waits for CI, refuses to land a red build) |
| 8 | close the loop | the merge commit references `#<issue>`, GitHub closes the issue |

One issue → one branch → one PR. A second problem is a second issue, even when
it is found while fixing the first (note it in the PR, then file it).

## Offline mode: the same loop with no GitHub

`scripts/offline.sh` exists for the train, the plane and the dead Tailnet. The
loop does not change shape — issue, branch, fix, test, merge — only where the
records live.

| online | offline | where it goes |
|--------|---------|---------------|
| `scripts/issue.sh` | `scripts/offline.sh issue TITLE [BODY]` | `issues/NNN-slug.md`, numbered **#L001** |
| `scripts/fix.sh 31` | `scripts/fix.sh L1` (or any call while the marker is on) | branch `fix-L1-slug` off **`offline`**, never off a stale `origin/main` |
| `scripts/pr.sh` + `scripts/merge.sh` | `scripts/offline.sh land L1 "title"` | merge commit into the branch **`offline`** |
| GitHub Actions | the same commands, run locally, *labelled as local* | merge-commit footer `Local-CI: … on <host> <time>` |
| the issue + PR | `scripts/offline.sh replay-one <NNN>` when back online | real issue, real branch, real PR, real CI |

Three properties that make this safe rather than convenient:

- **Local `main` is a mirror of `origin/main`.** Offline work never lands there,
  so coming back online is a pull and a replay, never a rebase, never a
  force-push, never "what did I do on the train".
- **The `L` prefix keeps the two numbering systems apart** forever; a record
  that has been published carries a `gh-issue: #<n>` line, so nothing is filed
  twice.
- **The CI gate is not skipped, only relocated.** `land` runs the Release build
  of `DrivingGame.Server` + `DrivingGame.Sim.Tests` and `dotnet test` (the very
  commands `ci.yml` runs), refuses to land on a red unit run, and writes what it
  ran and where into the merge commit. GitHub's CI still judges the change when
  the PR is replayed — that is the point of replay rather than a push.

`scripts/pr.sh` and `scripts/merge.sh` refuse to run while the marker
(`.git/offline-mode`) exists and point at `land` instead, so a half-online
attempt fails loudly instead of pushing to the wrong place. `scripts/offline.sh
status` shows the mode, whether `origin` answers, how many records exist and how
many are already published.

## CI: nothing lands that GitHub has not built and unit-tested

`.github/workflows/ci.yml` runs on every PR to `main` (and on pushes to `main`)
with two jobs whose names are the required status checks:

| check | what it does |
|-------|--------------|
| `core build + unit tests` | `dotnet restore/build` of `DrivingGame.Sim`, `DrivingGame.Api`, `DrivingGame.Server` and the test project in Release, then `dotnet test DrivingGame.Sim.Tests` (xUnit) with a `.trx` uploaded as an artifact and the counts written to the job summary |
| `godot client build` | `dotnet build DrivingGame.csproj` in Release - the Godot C# client must compile. `Godot.NET.Sdk` and `GodotSharp` come from NuGet, so no editor is installed on the runner |

**CI deliberately does not run the e2e suite** (`scripts/run_e2e.sh`): it drives a
visible Godot window against a live sim at real pacing, which a runner cannot
show, and its verdicts would be meaningless without the eye. The loop therefore
has two halves: **CI proves the build and the unit tests**, the **PR body carries
the measured e2e verdict lines** (`PR_VERDICTS=... scripts/pr.sh`), and
`scripts/merge.sh` refuses to land a PR whose checks are not green
(`--force` exists and says so loudly).

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
