#!/usr/bin/env python3
"""Generate road textures through the ComfyUI box - FLUX.1-schnell only.

Why FLUX and not the SD1.5 route that was tried first: sd1.5 drew wicker plating
when asked for cobblestone and a cable when asked for concrete (see the run log
in docs/generate_road_textures.txt §7), and the negative prompt could not stop
it; FLUX.1-schnell holds the MATERIAL SCALE in mind, so "extreme macro close-up
of asphalt aggregate" produces an actual fine-grain surface, no road-scale
geometry. More sampler steps do NOT buy quality on schnell (it is step-distilled
for 4): a 12-step re-run reproduced the same expansion joint. What works is
guidance and the subject framing.

Tiling is the graph's job, not ours: `SeamlessTileModelDiT` rolls the latent
per sampling step (the model-agnostic replacement for the circular-padding
trick, which only exists for convolutional U-Nets) and `MakeCircularVAEDiT`
wraps the VAE decode. The graph also saves an `OffsetImage` half-roll copy so
the seam proof ships in the same job. Nothing here patches a seam afterwards.

Usage:
  scripts/comfy_texture.py                     # the whole set (main, secondary, cobble, 2 decals)
  scripts/comfy_texture.py cobble              # one kind
  scripts/comfy_texture.py --size 1536 cobble   # bigger tile (more detail per metre)
  scripts/comfy_texture.py --guidance 1.0 main  # override guidance (the second lever)
  scripts/comfy_texture.py --steps 8 main        # only for the last-ditch tweaks

Endpoint: from `comfy/endpoint.txt` if it exists, else the constant below. The
Tailnet name changes when the box restarts (comfyStart.sh re-tunnels), so
refresh it from the box (`tailscale status --json -> .Self.DNSName`) or drive
the API on the box at http://127.0.0.1:8188.
"""
import json, os, sys, time, urllib.request as U

BASE = (open('comfy/endpoint.txt').read().strip()
        if os.path.exists('comfy/endpoint.txt') else
        'https://dagobert.adal-scala.ts.net:8188')
GRAPH = json.load(open('comfy/texture_workflow_flux.json'))

# node ids of the Flux graph
N_POS, N_NEG, N_GUIDE, N_SAMPLER, N_SIZE = '6', '5', '4', '3', '2'

# The ground negatives: the first attempt came out saturated emerald with
# magenta flower beds - "way too green and popping" (user, 2026-10-26). The
# satellite reference is a late-autumn villa district: dry olive grass, brown
# beds, dark hedges, grey gravel, overcast light.
NO_GROUND_POP = ('saturated colour, emerald, bright green, magenta, pink, purple, '
                 'flowers in bloom, vivid, neon, high contrast, painted road markings, '
                 'lane lines, arrows, text, letters, logo, perspective, vignette')
NO_ROAD_GEOMETRY = ('painted road markings, lane lines, arrows, expansion joint, seam, crack, panel, '
                    'tile grid, straight line, border, edge, frame, stitching, text, letters, logo, '
                    'vehicles, trees, plants, shadows, highlights, perspective, vignette, water, puddle')
GROUND_NEG = NO_GROUND_POP + ', ' + NO_ROAD_GEOMETRY.strip('()')

JOBS = {
    'main': dict(path='assets/textures/road_main.png', guidance=3.5, steps=6,
                 pos='extreme macro close-up photograph of dark grey asphalt, fine aggregate grains '
                     'bound in black tar, uniform field of tiny stones, orthographic top-down view, '
                     'flat diffuse lighting, low contrast, photoreal material, tileable texture'),
    'secondary': dict(path='assets/textures/road_cracked.png', guidance=3.5, steps=6,
                      pos='extreme macro close-up photograph of light grey worn asphalt, fine gravel '
                           'grain in tar, subtle pale dust patches, uniform field, orthographic top-down '
                           'view, flat diffuse lighting, low contrast, photoreal material, tileable texture'),
    'cobble': dict(path='assets/textures/road_cobble.png', guidance=3.5, steps=8,
                   pos='extreme macro close-up photograph of small grey cobblestone paving, individual '
                        'flat-topped stones with thin dark joints, tight regular layout, orthographic top-down '
                        'view, flat diffuse lighting, paving continues to the very edge of the image, '
                        'photoreal material, tileable texture'),
    # --- OFF-ROAD GROUND (docs/GODOT_FRONTEND.md, "the housing area"): the
    # satellite reference is a villa district - hedges, lawn, shrubs, gravel
    # drives between the houses. Deliberately NO buildings in the tile: those
    # become real OSM footprints later, and a tile that already drew roofs
    # would then fight the geometry it sits under.
    'ground_garden': dict(path='assets/textures/ground_garden.png', guidance=3.0, steps=8,
                 pos='aerial top-down photograph of a late-autumn suburban garden area, dry olive-green and ' 
                     'straw-coloured lawn, dark clipped yew hedges, brown flower beds with bare soil, a ' 
                     'strip of grey gravel drive, leafless shrubs, orthographic top-down view, flat overcast ' 
                     'lighting, muted desaturated palette, vegetation continues to the very edge of the image, ' 
                     'photoreal material, tileable texture'),
    'ground_lawn': dict(path='assets/textures/ground_lawn.png', guidance=3.0, steps=8,
                 pos='aerial top-down photograph of an open late-autumn lawn, dry straw-yellow and olive ' 
                     'grass with thin mowing lines, patches of bare brown soil, faded edges, orthographic top-down ' 
                     'view, flat overcast lighting, muted desaturated palette, grass continues to the very edge of ' 
                     'the image, photoreal material, tileable texture'),
    'ground_hedge': dict(path='assets/textures/ground_hedge.png', guidance=3.0, steps=8,
                 pos='aerial top-down photograph of dense late-autumn garden vegetation, dark olive and ' 
                     'grey-green clipped hedges, brown leaf litter, sparse bare branches, muted desaturated palette, ' 
                     'orthographic top-down view, flat overcast lighting, foliage continues to the very edge of ' 
                     'the image, photoreal material, tileable texture'),
    'crack1': dict(path='assets/decals/crack_01.png', guidance=2.0, steps=6,
                   pos='a single thin dark crack line, isolated, on a flat uniform grey asphalt field, '
                        'orthographic top-down view, flat lighting, photoreal material'),
    'crack2': dict(path='assets/decals/crack_02.png', guidance=2.0, steps=6,
                   pos='a short dark crack with one small tar patch repair, isolated, on a flat uniform '
                        'grey asphalt field, orthographic top-down view, flat lighting, photoreal material'),
}


def post(path, body):
    req = U.Request(BASE + path, data=json.dumps(body).encode(),
                   headers={'Content-Type':'application/json'})
    with U.urlopen(req, timeout=300) as f:
        return json.load(f)


def get(path):
    with U.urlopen(BASE + path, timeout=240) as f:
        return f.read()


def run(name, spec, size, guidance, steps):
    g = json.loads(json.dumps(GRAPH))
    g[N_POS]['inputs']['text'] = spec['pos']
    g[N_NEG]['inputs']['text'] = (GROUND_NEG if name.startswith('ground')
                             else NO_ROAD_GEOMETRY)
    g[N_GUIDE]['inputs']['guidance'] = guidance if guidance is not None else spec['guidance']
    g[N_SAMPLER]['inputs']['steps'] = steps if steps else spec['steps']
    g[N_SIZE]['inputs']['width'] = g[N_SIZE]['inputs']['height'] = size
    r = post('/prompt', {"prompt": g,
                         "outputs": [{"node": n, "type": "image"} for n in ('9', '15')]})
    pid = r['prompt_id']
    print(f'{name}: job {pid[:8]} node_errors={r.get("node_errors")}')
    files = {}
    for i in range(120):
        try:
            entry = (json.loads(get(f'/history/{pid}')) or {}).get(pid) or {}
        except Exception as e:
            print(f'  round {i + 1}: history unreadable ({e})'); time.sleep(3); continue
        outs = entry.get('outputs')
        if isinstance(outs, dict) and outs:
            for node, payload in outs.items():
                for im in payload.get('images', []):
                    # the graph saves a tile AND its half-roll copy: pick by NAME, never by order
                    files['offset' if 'offset' in im['filename'] else 'tile'] = im
            print(f'  round {i + 1}: got {sorted(files)}')
            break
        st = (entry.get('status') or {}).get('status_str')
        if st == 'error':
            for m in (entry.get('status') or {}).get('messages', []):
                if m[0] == 'execution_error':
                    print(f'{name}: FAILED node {m[1].get("node_id")} {m[1].get("node_type")} -> {str(m[1].get("exception_message"))[:200]}')
            return False
        print(f'  round {i + 1}: {st or "pending"}'); time.sleep(3)
    if 'tile' not in files:
        print(f'{name}: no tile after {120 * 3}s'); return False
    for kind, im in files.items():
        png = get(f"/view?filename={im['filename']}&subfolder={im.get('subfolder', '')}&type={im['type']}")
        dest = spec['path'] if kind == 'tile' else f'/tmp/seamproof_{name}.png'
        if kind == 'tile':
            os.makedirs(os.path.dirname(dest), exist_ok=True)
        open(dest, 'wb').write(png)
        print(f'  {kind} {im["filename"]} {len(png)} B -> {dest}')
    return True


if __name__ == '__main__':
    args = sys.argv[1:]
    size = int(args[args.index('--size') + 1]) if '--size' in args else 1024
    guidance = float(args[args.index('--guidance') + 1]) if '--guidance' in args else None
    steps = int(args[args.index('--steps') + 1]) if '--steps' in args else None
    want = [a for a in args if not a.startswith('--') and a not in ('--size', '--guidance', '--steps')]
    want = want or list(JOBS)
    print(f'endpoint {BASE} | {len(want)} jobs at {size}px | guidance/steps overrides: {guidance}/{steps}')
    ok = sum(run(n, JOBS[n], size, guidance, steps) for n in want if n in JOBS)
    for n in want:
        if n not in JOBS:
            print(f'unknown kind {n} (known: {",".join(JOBS)}); nothing generated for it')
    print(f'done: {ok}/{len(want)}')
