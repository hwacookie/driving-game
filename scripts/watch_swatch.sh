#!/usr/bin/env bash
# watch_swatch.sh — drive ONE car through every material of the swatch map,
# in a window that stays open for the eye.
#
#   scripts/watch_swatch.sh                 # real time, the whole serpentine
#   scripts/watch_swatch.sh --zoom 26       # closer in
#   scripts/watch_swatch.sh --road-tile 4   # one tile scale for every material
#
# Procedure (the review/watch recipe, so the first seconds of the action are
# not lost while the scene is being staged): open the window, FREEZE the clock,
# stage the car while frozen, poll until it exists, hand the camera to it, set
# the end flag at the far end of the last leg, then unfreeze at real time and
# print the drive as it happens. Nothing here closes the window.
set -u
PORT=${PORT:-5001}
API="http://127.0.0.1:${PORT}"
ARGS=("$@")
ZOOM=20
for i in "${!ARGS[@]}"; do
  if [ "${ARGS[$i]}" = "--zoom" ]; then ZOOM="${ARGS[$((i+1))]:-20}"; fi
done

echo "==> opening the swatch window (map swatch, materials on)"
scripts/open.sh --map swatch --zoom "$ZOOM" --road-materials "${ARGS[@]}" || exit 1

j(){ curl -s -m 5 -H 'content-type: application/json' -d "$1" "$API/$2"; }
get(){ curl -s -m 5 "$API/$1"; }

echo "==> freezing the sim clock while the car is staged"
j '{"frozen":true}' "freeze" >/dev/null

echo "==> spawning one car at the first leg (frozen: it waits for the go signal)"
j '{"start_point":"swatch_drive","speed":11.1,"cruise_kmh":40,"name":"swatch_car"}' "teleport" >/dev/null
CARUID=""
for i in $(seq 1 20); do
  CARUID=$(get state | python3 -c "
import json,sys
s=json.load(sys.stdin)
for c in s.get('cars',[]):
    if c.get('hud_label')=='swatch_car': print(c['car_uid']); break
" 2>/dev/null)
  [ -n "$CARUID" ] && break
  sleep 1
done
if [ -z "$CARUID" ]; then echo "!! the car never appeared - not driving anything"; exit 1; fi
echo "car uid $CARUID"

echo "==> handing the camera to it"
j "{\"uid\":$UID}" "follow" >/dev/null

echo "==> end flag at the far end of the last leg (nav drives to it)"
IDX=$(get segment_names | python3 -c "
import json,sys
n=json.load(sys.stdin)
cand=[v for k,v in n.items() if k.startswith('sw9_')]
print(max(cand) if cand else '')" 2>/dev/null)
if [ -n "$IDX" ]; then j "{\"red\":[$IDX,0.95]}" "flags" >/dev/null; echo "red flag on segment $IDX at 95%";
else echo "!! no end segment found - nav picks its own destination"; fi

echo "==> unfreezing at real time; the drive streams below"
j '{"frozen":false}' "freeze" >/dev/null
LAST=""
for i in $(seq 1 300); do
  LINE=$(get state | python3 -c "
import json,sys
s=json.load(sys.stdin)
c=[x for x in s.get('cars',[]) if x.get('car_uid')==int('$CARUID')]
if not c: print('GONE'); raise SystemExit()
x=c[0]
print(f\"{x.get('segment_name','?')}  {round(100*(x.get('progress') or 0))}% | {x.get('speed_kmh',0):.0f} km/h\")" 2>/dev/null || echo "?")
  case "$LINE" in
    GONE) echo "car gone - stop"; break;;
    *) [ "$LINE" != "$LAST" ] && { printf '%s  t+%ds\n' "$LINE" "$i"; LAST="$LINE"; };;
  esac
  echo "$LINE" | grep -qE " 9[0-9]% \|" && { echo "reached the end flag"; break; }
  sleep 1
done
echo "==> window stays open. close it with: scripts/open.sh --close"
