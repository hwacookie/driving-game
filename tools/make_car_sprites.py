#!/usr/bin/env python3
"""Extract the seven vehicles from assets/source/vehicles_green.png (flat
green-screen top-down photo) into individual transparent PNGs:

    assets/car_<name>.png        for blue silver police tan tractor pickup mixer
    assets/car_<name>_mask.png   the painted bodywork of the same sprite (issue #10)

Method (no ML, plain numpy):
  * background = flat green (~71,244,3); a pixel's "greenness" is
    G - max(R,B). Background greenness ~ +170, vehicle pixels ~ <= 15.
  * alpha ramp: fully opaque below GREEN_LO, transparent above GREEN_HI, linear
    between (anti-aliased edges get partial alpha).
  * despill: edge pixels (partial alpha) with G > max(R,B) get G clamped to
    max(R,B) so no green fringe remains. Interior pixels are untouched.
  * the source grid cells were measured from the image (CELLS below); each cell
    contains exactly one vehicle, so no connected-component pass is needed.

THE BODY MASK — and why it is authored, not computed
----------------------------------------------------
A livery must recolour painted bodywork only: recolouring the whole sprite makes
a car's windows pink, which is the thing issue #10 exists to prevent. Three
computed routes were built and measured on these sprites, and all three failed:

  * chromaticity clustering (modal paint colour of the interior, brightness
    free): grey paint and dark glass have the SAME chromaticity, so the silver
    Fiat became one solid blob (87.7 % of the silhouette), and a white lorry
    cab declared its own engine deck as paint (tractor 45 %, mixer 83 %).
  * a brightness floor to drop glass: deleted the legitimate paint of a dark
    car (silver 0.3 % once the growth alone had to carry it) — a black bonnet is
    paint, and the police BMW is black.
  * region growing from "this is paint" seeds, with "this is glass" seeds
    subtracted: black glass adjacent to black paint has no colour boundary, so
    the two grows met and the subtraction took the paint with it (everything
    landed at 0.2-1.5 %).

What is left is the truth: which pixels are glass is a fact about the photo, so
the fact is STATED. BODY_EXCLUDE lists, per vehicle, the shapes that are NOT
paint (glazing, lamps, the plate, a convertible's canopy, the machinery of a
lorry), the body is the alpha silhouette minus those, and the tool prints the
coverage — a wrong shape shows up as a wrong percentage and as a wrong
car_<name>_mask.png, both at tool time.

Coordinates are in the sprite's own pixels (x right, y down, NOSE AT THE TOP —
after --flip). A shape is either a rect (x0, y0, x1, y1) or a polygon
[(x, y), …]; both are drawn at the sprite's native size and intersected with
alpha, then feathered so a recoloured panel has no hard 1-px seam.

Also prints the CarSizes C# table for MapRenderer.cs (physical size per
sprite in metres, scaled so the blue sedan matches the game's 1.8 m x 4.4 m car
box) - update that table when regenerating.

Usage:  python3 tools/make_car_sprites.py [--flip name …] [--no-mask]
(--flip vertically flips the listed sprites, for vehicles whose nose points
down in the source photo; the game convention is nose = texture top. The
default list below reflects the CURRENT source image - update it if the source
changes.)
"""
import argparse
import sys
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw

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

BODY_EDGE_FEATHER = 2    # px of blur on the mask, so a panel edge has no seam

# What is NOT painted bodywork, per vehicle (see the module docstring). Read off
# the 10 px block map of each sprite; every entry names a thing a human can
# verify in the picture: glazing, lamps, the number plate, a canvas canopy, the
# open bed or chassis of a truck.
BODY_EXCLUDE = {
    # 116 x 254 - Mercedes C220d, nose up.
    "blue": [
        [(18, 74), (98, 74), (96, 126), (20, 126)],   # windscreen + wipers
        [(22, 186), (94, 186), (98, 226), (18, 226)],  # rear glazing
        (28, 0, 88, 20),                               # grille, front lamps
        (18, 240, 98, 254),                            # plate and tail lamps
    ],
    # 110 x 181 - Fiat 500 (the dark canopy is a convertible's fabric top).
    "silver": [
        (16, 26, 94, 150),                             # canopy and glasshouse
        (8, 146, 40, 168), (70, 146, 102, 168),        # tail lamps
    ],
    # 120 x 272 - BMW 320i police, black body, white roof, roof light bar.
    "police": [
        (18, 22, 102, 96),                             # windscreen / glasshouse
        (14, 116, 106, 146),                           # the light bar
        (20, 190, 100, 216),                           # rear glazing
        (25, 0, 95, 18),                               # grille
        (12, 232, 46, 262), (74, 232, 108, 262),       # tail lamps
    ],
    # 120 x 274 - VW Passat B5.
    "tan": [
        (18, 62, 102, 108),                            # windscreen
        (30, 114, 92, 190),                            # sunroof
        (16, 238, 104, 262),                           # rear lamps and valance
        (24, 0, 96, 12),                               # grille
    ],
    # 135 x 303 - Ford F-150, open bed, hood louvres.
    "pickup": [
        (24, 74, 112, 106),                            # windscreen
        (34, 112, 102, 142),                           # cab rear window
        (40, 24, 80, 70),                              # hood louvres
        (26, 184, 110, 292),                           # the load bed
        (20, 0, 115, 14), (12, 290, 122, 303),         # bumpers and lamps
    ],
    # 150 x 283 - tractor unit: a cab on a chassis. Only the cab is painted.
    "tractor": [
        (44, 22, 96, 58),                              # cab rear window
        (10, 82, 140, 283),                            # chassis, tanks, tyres
        (30, 0, 120, 10),                              # roof marker lamps
    ],
    # 139 x 326 - mixer truck: cab plus drum, both painted; the chute is not.
    "mixer": [
        (48, 20, 92, 54),                              # cab rear window
        (10, 72, 130, 112),                            # hopper between cab and drum
        (30, 272, 120, 326),                           # chute and hopper
        (20, 0, 120, 8),                               # cab front
    ],
}

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


def _feather(m, px):
    """Blur a boolean mask into 0..255 (box passes): a hard 1-px mask edge reads
    as a photo-coloured hairline around a recoloured panel."""
    a = m.astype(np.float32)
    for _ in range(max(1, int(px))):
        p = np.pad(a, 1, mode="edge")
        a = (p[1:-1, 1:-1] * 4 + p[:-2, 1:-1] + p[2:, 1:-1] + p[1:-1, :-2] + p[1:-1, 2:]) / 8.0
    return (a * 255).clip(0, 255).astype(np.uint8)


def body_mask(rgba, name):
    """Painted bodywork of a vehicle sprite (issue #10): 0..255 per pixel.

    The alpha silhouette MINUS the authored BODY_EXCLUDE shapes of this vehicle.
    No colour is looked at on purpose - grey paint and dark glass are the same
    colour, and a black car's bonnet is not "dark", so every computed rule that
    tried to tell them apart failed (module docstring carries the measurements).
    A shape outside the sprite is not an error (it clips), a MISSING entry is:
    without it the whole silhouette would be paint, which is the bug this file
    exists to avoid.
    """
    if name not in BODY_EXCLUDE:
        raise KeyError(f"BODY_EXCLUDE has no entry for '{name}' - a livery would "
                       "paint its glass, so draw the exclusions first")
    h, w = rgba.shape[:2]
    keep = Image.new("L", (w, h), 255)
    d = ImageDraw.Draw(keep)
    for shape in BODY_EXCLUDE[name]:
        if len(shape) == 4 and all(isinstance(v, int) for v in shape):
            x0, y0, x1, y1 = shape
            d.rectangle([x0, y0, x1, y1], fill=0)
        else:
            d.polygon([tuple(p) for p in shape], fill=0)
    alpha = np.asarray(keep).astype(np.float32) / 255.0
    m = (rgba[..., 3] > 127) & (alpha > 0.5)
    cover = float(m.sum()) / max(1, int((rgba[..., 3] > 127).sum()))
    return _feather(m, BODY_EDGE_FEATHER), cover


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
            mask, cover = body_mask(crop, name)
            mout = OUT_DIR / f"car_{name}_mask.png"
            Image.fromarray(mask, "L").save(mout)
            excl = len(BODY_EXCLUDE[name])
            print(f"{mout.relative_to(ROOT)}  body {cover * 100:.1f}% of the "
                  f"silhouette ({excl} exclusion shape{'s' if excl != 1 else ''})")

    # Suggested C# table: physical size per sprite (blue sedan = game box).
    rw, rh = sizes[REF_NAME]
    print("\n// CarSizes for MapRenderer.cs (W x L in metres):")
    for name, (w, h) in sizes.items():
        wm = REF_W_M * w / rw
        lm = REF_L_M * h / rh
        print(f'    ["{name}"] = ({wm:.2f}f, {lm:.2f}f),')


if __name__ == "__main__":
    sys.exit(main())
