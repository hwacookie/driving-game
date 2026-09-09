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


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--flip", nargs="*", default=DEFAULT_FLIPS,
                    help="sprite names to flip vertically (nose-down in source)")
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

    # Suggested C# table: physical size per sprite (blue sedan = game box).
    rw, rh = sizes[REF_NAME]
    print("\n// CarSizes for MapRenderer.cs (W x L in metres):")
    for name, (w, h) in sizes.items():
        wm = REF_W_M * w / rw
        lm = REF_L_M * h / rh
        print(f'    ["{name}"] = ({wm:.2f}f, {lm:.2f}f),')


if __name__ == "__main__":
    sys.exit(main())
