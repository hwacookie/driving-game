#!/usr/bin/env python3
"""Generate the FLAT asphalt road tile - no GPU, no diffusion model.

The reference the operator showed (an aerial photo of an empty road) is
essentially FLAT: its "detail" is metre-scale tonal variation, not surface
grain. A diffusion model keeps fighting that - three FLUX attempts at this
came back with baked white markings and soft bands (measured: mean luminance
163-174, visible banding), which is the wrong thing to paste under markings
the renderer draws itself. A periodic value-noise field is exact instead: it
wraps at the tile border by construction, so there is no seam to fix, and its
tonal range is an explicit knob.

Tunables (the two the operator's judgement is about):
  --span  total grey levels of variation (contrast of the mottling)
  --skew  >1 puts most of the field toward the DARK end, so the bright
          patches become rare and faint - "less pronounced bright".

Usage:
  scripts/make_road_tile.py                       # the current look
  scripts/make_road_tile.py --span 14 --skew 1.6  # more, brighter variation
  scripts/make_road_tile.py --out assets/textures/road_main.png --size 1024

After writing the PNG, import it: `Godot --headless --path . --import`
(the .import keeps mipmaps on; there is no runtime sampler-repeat API in
Godot 4.7, so the import settings are the only place those live).
"""
import numpy as np
from PIL import Image
import argparse, random


def parse():
    ap = argparse.ArgumentParser()
    ap.add_argument('--out', default='assets/textures/road_main.png')
    ap.add_argument('--size', type=int, default=512)
    ap.add_argument('--base', type=float, default=118.0, help='mid grey, 0-255')
    ap.add_argument('--span', type=float, default=10.0, help='grey levels of variation')
    ap.add_argument('--skew', type=float, default=2.4, help='>1 = darker field, faint brights')
    ap.add_argument('--seed', type=int, default=11)
    return ap.parse_args()


def octave(rng, n, cells):
    """One smooth octave on a torus: a value lattice (copied at the wrap seam),
    bilinear-sampled per pixel, then box-blurred with `np.roll`, which wraps.
    Plain value noise is what looked like minecraft: every coarse cell is flat.
    Blurring half a cell turns those cells into soft patches while keeping the
    field exactly tileable - the script prints interior-step vs wrap-cost as
    proof, and refuses to pretend when they disagree."""
    step = n // cells
    g = rng.random((cells + 1, cells + 1))
    g[-1, :] = g[0, :]; g[:, -1] = g[:, 0]
    idx = np.repeat(np.arange(cells + 1), step)[:n]
    frac = np.tile(np.arange(step) / step, cells + 1)[:n]
    F = np.tile(frac[None, :], (n, 1))          # varies right
    FY = np.tile(frac[:, None], (1, n))         # varies down
    A = g[np.ix_(idx, idx)]
    B = g[np.ix_(idx, idx + 1)]
    C = g[np.ix_(idx + 1, idx)]
    D = g[np.ix_(idx + 1, idx + 1)]
    v = A * (1 - F) * (1 - FY) + B * F * (1 - FY) + C * (1 - F) * FY + D * F * FY
    r = max(1, step // 2)
    for axis in (0, 1):                          # separable torus blur
        acc = np.zeros_like(v); wsum = 0.0
        for d in range(-r, r + 1):
            w = 1.0 / (r + 1 - abs(d))
            acc += np.roll(v, d, axis=axis) * w
            wsum += w
        v = acc / wsum
    return v


def main():
    a = parse()
    n = a.size
    rng = np.random.default_rng(a.seed)
    acc = np.zeros((n, n)); amp = 1.0; tot = 0.0
    for cells in (4, 8, 16, 32):
        if n % cells:
            continue
        acc += amp * octave(rng, n, cells); tot += amp; amp *= 0.55
    lo, hi = np.percentile(acc, 0.5), np.percentile(acc, 99.5)   # percentile stretch:
    f = np.clip((acc - lo) / (hi - lo), 0.0, 1.0)                # gradient noise is
    f = f ** a.skew                                              # signed, so normalise
    v = a.base + a.span * f
    img = np.stack([v, v * 1.004, v * 1.02], axis=2).clip(0, 255).astype(np.uint8)
    Image.fromarray(img).save(a.out)
    g = img[:, :, 0].astype(float)
    nb = (abs(g[:, :-1] - g[:, 1:]).mean() + abs(g[1:, :] - g[:-1, :]).mean()) / 2
    wr = (abs(g[:, -1] - g[:, 0]).mean() + abs(g[-1, :] - g[0, :]).mean()) / 2
    print(f'{a.out} {n}x{n} | interior step {nb:.2f} | wrap {wr:.2f} | '
          f'luminance {g.min():.0f}..{g.max():.0f} mean {g.mean():.1f}')
    if nb and wr / nb > 1.6:
        print('  wrap cost is well above the interior step - this tile is NOT seamless, do not ship it')


if __name__ == '__main__':
    main()
