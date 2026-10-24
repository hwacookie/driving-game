# Units: one dialect — metres

> **Status 2026-10-23 (code-verified):** the greps below were re-run on this
> tree and still pass. This file is the rule; `AGENTS.md` carries it as a
> binding project rule, and [REST_API.md](REST_API.md) states the one payload
> that predates it (`GET /obstacles`, a bare list).

There used to be two length units in the project (world pixels inside the
sim, metres everywhere else), and that mix was the most common source of
silent bugs (a car 2× off the road, a halt line twice as wide). It is
gone: **the project speaks metres, everywhere.**

## The dialects

| Where | Unit | Declared by |
|---|---|---|
| the sim (`DrivingGame.Sim/**`) | **metres** | the world frame itself — there is no scale factor |
| every API endpoint (`/map`, `/state`, `/segment`, `/start_points`, `/obstacles`) | **metres** | `"units": "meters"` in the payload |
| the Godot client's world (`MapRenderer`, `Camera2D` position) | **metres** | `Camera2D.zoom` IS px/m, the client's own view |
| the Python suites (`tests/**`) | **metres** | nothing to convert |

Metres mean the same thing everywhere: x east, y north, origin at the
map's south-west corner, one metre = one metre. `Config.PIXELS_PER_METER`
no longer exists — if you feel the need to multiply a length by something,
that is a bug unless you are at a real boundary (see below).

## The naming rule

A name says its unit, so a unit mistake is a name mistake you can see:

1. **`…M`** — metres. Config constants of a length carry `_M`
   (`STOP_LINE_WIDTH_M`, `CAR_LENGTH_M`), and so do locals and
   parameters (`sideOffsetM`, `segLengthM`).
2. **no suffix** — a length in the world frame. Since the world frame IS
   metres, plain names (`x`, `X1`, `Width` of a segment) are frame values
   and need no suffix. Data-model fields never carry one.
3. **`…Px`** — survives only at a genuine boundary: a value that has just
   crossed into a *screen* or *asset* frame (Godot pixels, a sprite atlas,
   a minimap). It is never a sim value.
4. **the scale factor is always named `pxPerM`** (locals) or `PxPerM`
   (fields/constants) — never `pppm`, `PPPM`, `scale`, `k`. It exists only
   where a real boundary converts (the client's zoom, a fixture's declared
   scale).

Direction of a conversion is part of the name too: `…M * pxPerM → …Px`
and `…Px / pxPerM → …M`.

## The real boundaries

Only these convert lengths:

- **the Godot client**: `Camera2D.zoom` is px/m and `MapRenderer` derives
  its own from the viewport and the map bounds. The sim never sees it.
- **`data/raceline_reference/basic.json`**: a golden fixture from the
  retired Python reference that declares its own scale (`"pxPerM"`,
  legacy `"pppm"`). `RacelineReferenceTests` reads it through
  `FixturePxPerM` and divides every frame-valued field on load —
  lengths that are not frame values (`offsets`, `lo`/`hi`, `base_prof`,
  `cum`, props widths) and frame-independent scalars (`K` = rad/m, unit
  normals) are read as they are.

## Auditing a change

The crossings are the whole job — and they are greppable:

```bash
grep -rn "pxPerM\|PxPerM" --include=*.cs .      # every frame crossing
grep -rn "PIXELS_PER_METER" --include=*.cs .    # must be empty
```

The first list should contain only the boundaries above. A new use of a
metre value that appears there is either a boundary you are adding or a
bug.

## Audit results — a PROCEDURE, re-run it, do not trust a snapshot

The lists below are what the greps returned on **2026-10-19**; they are an
example of a clean result, **not a closed set of names**. New screen-frame
constants appear every time the HUD changes (on 2026-10-23 the client also has
`WinGapPx`, `WinTitleBarPx`, `ReadoutFontPx`, `StripPx`, … in
`MapRenderer.cs` / `MapRenderer.review.cs` — all rule-3 legal, none of them
listed below). Re-run the commands; a hit is only a finding if the value is a
**physical** length.

- `PIXELS_PER_METER` — empty (re-verified 2026-10-23).
- `pxPerM`/`PxPerM` — only `DrivingGame.Sim.Tests/RacelineReferenceTests.cs`
  (the fixture boundary above; re-verified 2026-10-23). The Godot client does
  not name a scale factor
  at all: `Camera2D.zoom` IS px/m and is used directly.
- leftover `…Px` names (`git ls-files '*.cs' | xargs grep -nE
  "(^|[^A-Za-z0-9_])[A-Za-z_][A-Za-z0-9_]*Px([^A-Za-z0-9_]|$)"` minus
  `pxPerM|PxPerM`, plus a separate check for bare `Px`/`Py` names) — only
  `MapRenderer.cs`'s blinker-dot radius (`rPx`, divided back out by
  `Camera2D.zoom`), which is the client's own screen frame and allowed by
  rule 3.

## History

The frame flip happened in `feat/unified-units` (2026-09 → 2026-10): `Config.PIXELS_PER_METER`
went from 2 to 1 and then away entirely, the sim's world frame became
metres, and the `pxPerM` parameters threaded through the geometry and
navigation code were dropped. The `diary/` still describes the old
pixel frame — that is history, not a current fact. The follow-up naming
sweep (`2651ca2`) removed the leftover `…Px` sim-frame names, and the
docs (`SPEC.md`, `REST_API.md`, `TESTING.md`) were reconciled against the
code on 2026-10-19.
