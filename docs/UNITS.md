# Units: one dialect per boundary

The project has always mixed two length units, and that mix is the most
common source of silent bugs (a car 2× off the road, a halt line twice as
wide). This file states what speaks which unit, and the naming rule that
keeps it visible in the code.

## The dialects

| Where | Unit | Declared by |
|---|---|---|
| the sim (`DrivingGame.Sim/**`) | **world pixels** | `Config.PIXELS_PER_METER` (2 px/m) |
| `GET /map`, `GET /state` | **metres** | `"units": "meters"` in the payload |
| `GET /segment/{idx}`, `GET /start_points`, `GET /obstacles` | world pixels | stated in `docs/REST_API.md` |
| the Godot client's world (`MapRenderer`, `Camera2D` position) | **metres** | `Camera2D.zoom` carries px/m |
| the Python suites (`tests/**`) | **metres** | `PX_PER_M` at the two pixel readers |

The sim's frame is the only place pixels exist. Everything a human or
another program can see is metres.

## The naming rule

A name says its unit, so a unit mistake is a name mistake you can see:

1. **`…M`** — metres. Config constants of a length carry `_M`
   (`STOP_LINE_WIDTH_M`, `CAR_LENGTH_M`), and so do locals and
   parameters (`sideOffsetM`, `segLength`).
2. **`…Px`** — a value in the sim frame that sits **on a conversion line**,
   i.e. it was just produced from metres (`double halfWPx = widthM / 2 *
   pxPerM;`). The suffix marks the crossing, not a different unit.
3. **no suffix** — the sim frame, inside the sim. The frame is one global
   frame, so plain names (`x`, `X1`, `Width` of a segment in px) are
   unambiguous there. Data-model fields (`RoadSegment.X1`) never carry a
   suffix; they are frame values.
4. **the scale factor is always named `pxPerM`** (locals) or `PxPerM`
   (fields/constants). Never `pppm`, `PPPM`, `scale`, `k`.

Direction of a conversion is part of the name, too: `…M * pxPerM → …Px`
and `…Px / pxPerM → …M`.

## Auditing a change

The crossings are the whole job — and they are greppable:

```bash
grep -rn "pxPerM\|PxPerM" --include=*.cs .      # every frame crossing
grep -rn "PIXELS_PER_METER" --include=*.cs .    # where the frame is defined
```

A new use of a metre value inside the sim must appear in that first list.
If it does not, either it is unit-free (an angle, a ratio, a speed) or it
is a bug.

## The plan

The sim is being moved to metres (see the goal notes): pixels then exist
only at the drawing boundary, `pxPerM` disappears from the sim, and the
grep above shrinks to the renderer. Until that step, the rule above is
what keeps the two dialects apart.
