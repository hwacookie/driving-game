#!/usr/bin/env python3
"""Extract the seven vehicles from assets/source/vehicles_green.png (flat
green-screen top-down photo) into individual transparent PNGs:

    assets/car_<name>.png   for name in blue silver police tan tractor pickup mixer
(tractor = the Sattelschlepper tractor unit / engine part, top-right-middle cell)

Method (no ML, plain numpy):
  * background = flat green (~71,244,3); a pixel's "greenness" is
    G - max(R,B). Background greenness ~ +170, vehicle pixels ~ <= 15.
  * alpha ramp: fully opaque below GREEN_LO, transparent above GREEN_HI,
    linear between (anti-aliased edges get partial alpha).
  * despill: edge pixels (partial alpha) with G > max(R,B) get G clamped to
    max(R,B) so no green fringe remains. Interior pixels are untouched.

The source grid cells were measured from the image (see CELLS below); each
cell contains exactly one vehicle, so no connected-component pass is needed.

Also prints the CarSizes C# table for MapRenderer.cs (physical size per
sprite in metres, scaled so the blue sedan matches the game's 1.8 m x
4.4 m car box) - update that table when regenerating.

Usage:  python3 tools/make_car_sprites.py [--flip name [name ...]]
(--flip vertically flips the listed sprites, for vehicles whose nose points
down in the source photo; the game convention is nose = texture top.
The default list below reflects the CURRENT source image - update it if
the source changes.)
"""
import argparse
import sys
from pathlib import Path

import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "assets" / "source" / "vehicles_green.png"
OUT_DIR = ROOT / "assets"

# (name, y0, y1, x0, x1) - grid cells measured from the 498x1024 source.
CELLS = [
    ("blue",    40, 315,  24, 161),
    ("silver",  40, 315, 195, 306),
    ("police",  40, 315, 333, 485),
    ("tan",     358, 648,  24, 161),
    ("tractor", 358, 648, 333, 485),
    ("pickup",  677, 1006,  24, 161),
    ("mixer",   677, 1006, 333, 485),
]

GREEN_LO = 10.0    # greenness below this -> fully opaque
GREEN_HI = 90.0    # greenness above this -> fully transparent

# --- Body mask (issue #10) ------------------------------------------------
# Tolerances of `body_mask`. A bad mask shows up in the printed coverage and in
# the written car_<name>_mask.png, so they are tuned here, once, looking at the
# output - not inside a shader later.
BODY_VALUE_FLOOR = 0.30      # × the brightest interior pixel: below = glass/tyre
BODY_CHROMA_TOL = 0.045      # radius in normalised (r,g) space around the paint
BODY_CHROMA_BINS = 32        # coarse bins for the modal-paint vote
BODY_INTERIOR_EROSION = 3    # px: keep the green-screen rim out of the sampling

# Vehicles that point nose-DOWN in assets/source/vehicles_green.png.
DEFAULT_FLIPS = ["blue"]

# Physical reference: the blue sedan maps to the game's car box.
REF_NAME = "blue"
REF_W_M, REF_L_M = 1.8, 4.4


def extract(name, y0, y1, x0, x1):
    a = np.asarray(Image.open(SRC).convert("RGB")).astype(np.int32)
    cell = a[y0:y1, x0:x1]
    r, g, b = cell[..., 0], cell[..., 1], cell[..., 2]
    greenness = g - np.maximum(r, b)
    alpha = np.clip((GREEN_HI - greenness) / (GREEN_HI - GREEN_LO), 0.0, 1.0)

    # Despill only where the pixel is a car/bg blend (partial alpha).
    edge = (alpha > 0.0) & (alpha < 1.0)
    spill = edge & (g > np.maximum(r, b))
    g = np.where(spill, np.maximum(r, b), g)

    rgb_u8 = np.stack([r, g, b], axis=-1).clip(0, 255).astype(np.uint8)
    rgba = np.dstack([rgb_u8, (alpha * 255).astype(np.uint8)])
    ys, xs = np.where(alpha > 0.02)   # drop fully transparent specks
    crop = rgba[ys.min():ys.max() + 1, xs.min():xs.max() + 1]
    return crop


def _erode(m, px):
    """Shrink a boolean region by `px` (numpy only): the anti-aliased rim of the
    silhouette is a blend of car and green screen, and sampling the body colour
    there would teach the mask the background's chromaticity."""
    out = m.copy()
    for _ in range(max(0, int(px))):
        sh = out.copy()
        sh[1:, :] &= out[:-1, :]
        sh[:-1, :] &= out[1:, :]
        sh[:, 1:] &= out[:, :-1]
        sh[:, :-1] &= out[:, 1:]
        out = sh
    return out


def _fill_holes(m):
    """Close every hole the mask enclosed (numpy-only flood from the border).
    A badge, a door handle or a wheel-arch shadow sits INSIDE painted metal; an
    open hole there would show a photo-coloured island in the middle of a
    recoloured car."""
    free = ~m
    reach = np.zeros_like(m)
    reach[0, :] = free[0, :]
    reach[-1, :] = free[-1, :]
    reach[:, 0] = free[:, 0]
    reach[:, -1] = free[:, -1]
    while True:
        prev = reach
        reach = prev | (free & np.roll(prev, 1, axis=0)) | (free & np.roll(prev, -1, axis=0)) \
                    | (free & np.roll(prev, 1, axis=1)) | (free & np.roll(prev, -1, axis=1))
        if reach.sum() == prev.sum():
            return m | ~reach


def body_mask(rgba):
    """Painted bodywork of a vehicle sprite (issue #10): 0..255 per pixel.

    The source is a flat green-screen photo, so `extract` already gives the
    silhouette of the WHOLE vehicle — glass, tyres and chrome included, all of
    which keep their own pixels when a livery is applied. Inside that silhouette
    the discriminator is CHROMATICITY: divide the brightness out and a door and a
    roof are the same colour, because a roof is the same paint, only brighter. So
    brightness may vary freely (that is what preserves the photo's shading) while
    hue/saturation may not, and a brightness floor drops dark glass and black
    tyres. The reference colour is the modal coarse bin of the interior, so a grey
    Fiat and a blue Mercedes each get THEIR paint — a fixed "is it bluish" rule
    could never serve both.

    Returns (mask, coverage of the silhouette): the tool prints the coverage, so
    a bad mask is visible at tool time rather than on screen a week later.
    """
    r = rgba[..., 0].astype(np.float64)
    g = rgba[..., 1].astype(np.float64)
    b = rgba[..., 2].astype(np.float64)
    alpha = rgba[..., 3] > 127
    val = np.maximum(np.maximum(r, g), b)
    denom = (r + g + b).clip(1.0)
    cr, cg = r / denom, g / denom

    interior = _erode(alpha, BODY_INTERIOR_EROSION)
    if not interior.any():
        return np.zeros(alpha.shape, np.uint8), 0.0
    bright = max(1.0, float(val[interior].max()))
    cand = interior & (val >= BODY_VALUE_FLOOR * bright)
    if not cand.any():
        return np.zeros(alpha.shape, np.uint8), 0.0

    keys = ((cr[cand] * BODY_CHROMA_BINS).astype(np.int64) * 1024
            + (cg[cand] * BODY_CHROMA_BINS).astype(np.int64) * 32
            + (val[cand] // 32).clip(0, 31).astype(np.int64))
    vals, counts = np.unique(keys, return_counts=True)
    top = vals[counts.argmax()]
    sel = keys == top
    body_cr = float(cr[cand][sel].mean())
    body_cg = float(cg[cand][sel].mean())

    m = alpha & (np.abs(cr - body_cr) <= BODY_CHROMA_TOL) \
            & (np.abs(cg - body_cg) <= BODY_CHROMA_TOL) \
            & (val >= BODY_VALUE_FLOOR * bright)
    m = _fill_holes(m)
    cover = float(m.sum()) / max(1, int(alpha.sum()))
    return m.astype(np.uint8) * 255, cover


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--flip", nargs="*", default=DEFAULT_FLIPS,
                    help="sprite names to flip vertically (nose-down in source)")
    ap.add_argument("--no-mask", action="store_true",
                    help="skip the body masks (issue #10) - sprites only")
    args = ap.parse_args()

    sizes = {}
    for name, y0, y1, x0, x1 in CELLS:
        crop = extract(name, y0, y1, x0, x1)
        if name in args.flip:
            crop = crop[::-1]
        h, w = crop.shape[:2]
        out = OUT_DIR / f"car_{name}.png"
        Image.fromarray(crop, "RGBA").save(out)
        sizes[name] = (w, h)
        print(f"{out.relative_to(ROOT)}  {w}x{h}px")
        if not args.no_mask:
            mask, cover = body_mask(crop)
            mout = OUT_DIR / f"car_{name}_mask.png"
            Image.fromarray(mask, "L").save(mout)
            print(f"{mout.relative_to(ROOT)}  body {cover * 100:.1f}% of the silhouette")

    # Suggested C# table: physical size per sprite (blue sedan = game box).
    rw, rh = sizes[REF_NAME]
    print("\n// CarSizes for MapRenderer.cs (W x L in metres):")
    for name, (w, h) in sizes.items():
        wm = REF_W_M * w / rw
        lm = REF_L_M * h / rh
        print(f'    ["{name}"] = ({wm:.2f}f, {lm:.2f}f),')


if __name__ == "__main__":
    sys.exit(main())
