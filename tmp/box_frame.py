#!/usr/bin/env python3
"""Draw the last frame of the #24 tape with the acceptance box drawn in.

Everything comes from the tape's own last frame (cars) and the box the same
frame publishes as park_target, so the picture cannot disagree with a verdict.
"""
import math
from PIL import Image, ImageDraw

TAPE = "tests/tape_T_027_24_20261103_180521.txt"
WB = {"mercedes_c220": (2.700, 4.400, 1.800), "fiat_500": (2.300, 3.140, 1.710)}
OFF = 1.276      # REAR_AXLE_OFFSET_M - /state x,y is the rear axle

rows = [l for l in open(TAPE, errors="ignore") if not l.startswith("#")]
last = rows[-2].split(";")
cars = {}
for cell in last[3:]:
    f = cell.split(",")
    uid, x, y, h, v = int(f[0]), float(f[1]), float(f[2]), float(f[3]), float(f[4])
    cars[uid] = (x, y, h, v, f[9] if len(f) > 9 else "")

# the box, as published in the same run (park_target)
BX, BY, BH, BL, BW = 2091.65, 700.924, 180.0, 5.00, 2.40


def body_rect(uid):
    x, y, h, v, m = cars[uid]
    L, W = (WB[m][1], WB[m][2]) if m in WB else (4.4, 1.8)
    r = math.radians(h)
    cx, cy = x + math.sin(r) * OFF, y + math.cos(r) * OFF
    fx, fy, qx, qy = math.sin(r), math.cos(r), math.cos(r), -math.sin(r)
    pts = [(cx + fx * a + qx * b, cy + fy * a + qy * b)
           for a in (L / 2, -L / 2) for b in (W / 2, -W / 2)]
    return pts, (cx, cy, L, W)


def box_rect(cx, cy, h, L, W):
    r = math.radians(h)
    fx, fy, qx, qy = math.sin(r), math.cos(r), math.cos(r), -math.sin(r)
    return [(cx + fx * a + qx * b, cy + fy * a + qy * b)
            for a in (L / 2, -L / 2) for b in (W / 2, -W / 2)]


parked = [u for u, c in cars.items() if c[3] == 0.0]
moving = [u for u, c in cars.items() if c[3] != 0.0]
allpts = [p for u in parked + moving for p in body_rect(u)[0]] + box_rect(BX, BY, BH, BL, BW)
xs, ys = [p[0] for p in allpts], [p[1] for p in allpts]
pad = 1.5
xmin, xmax = min(xs) - pad, max(xs) + pad
ymin, ymax = min(ys) - pad, max(ys) + pad
zoom = 220.0
Wpx, Hpx = int((xmax - xmin) * zoom), int((ymax - ymin) * zoom)


def P(x, y):
    return ((x - xmin) * zoom, Hpx - (y - ymin) * zoom)


def poly(pts, fill, outline, w=2):
    order = [0, 1, 3, 2, 0]
    d.polygon([P(*pts[i]) for i in order], fill=fill)
    d.line([P(*pts[i]) for i in order], fill=outline, width=w)


im = Image.new("RGBA", (Wpx, Hpx), (26, 30, 26, 255))
d = ImageDraw.Draw(im)
for u in parked:
    pts, (cx, cy, L, W) = body_rect(u)
    poly(pts, (90, 90, 120, 210), (210, 210, 70, 255), 3)
pts_box = box_rect(BX, BY, BH, BL, BW)
poly(pts_box, (255, 200, 0, 36), (255, 210, 0, 255), 4)
for u in moving:
    pts, _ = body_rect(u)
    poly(pts, (200, 40, 40, 160), (255, 70, 70, 255), 3)
d.line([P(xmin, BY), P(xmax, BY)], fill=(0, 255, 255, 150), width=1)
im.save("/tmp/box_frame.png")

print(f"standing cars in the last frame: {len(parked)} (uids {parked}), "
      f"still moving: {moving}")
print(f"box: {BL:.2f} x {BW:.2f} m at ({BX:.2f}, {BY:.3f}), heading {BH:.1f}")
for u in sorted(cars):
    x, y, h, v, m = cars[u]
    if v != 0.0:
        continue
    _, (cx, cy, L, W) = body_rect(u)
    print(f"  parked #{u} ({m}): body {L:.2f} x {W:.2f}, centre ({cx:.2f}, {cy:.2f})")

row_x = sorted(body_rect(u)[1][0] for u in parked)
print(f"row the parked cars stand on (body-centre x): {row_x[0]:.2f}")
print(f"box centre is {row_x[0] - BX:+.2f} m toward the kerb of that row")

for u in moving:
    x, y, h, v, m = cars[u]
    _, (cx, cy, L, W) = body_rect(u)
    print(f"  arriving #{u}: body {L:.2f} x {W:.2f}, centre ({cx:.2f}, {cy:.2f}), "
          f"yaw {abs(((h - BH + 180) % 360) - 180):.1f} deg to the row")

L, W = WB["mercedes_c220"][1], WB["mercedes_c220"][2]
def fits(deg):
    r = math.radians(deg)
    return (L * math.cos(r) + W * math.sin(r) <= BL and
            L * math.sin(r) + W * math.cos(r) <= BW)
mx = max(deg / 10 for deg in range(0, 200) if fits(deg / 10))
print(f"crookedness this box allows (own corners inside): {mx:.1f} deg")
print(f"lateral room: {(BW - W) / 2:.2f} m each side;  longitudinal room: {(BL - L) / 2:.2f} m each end")
