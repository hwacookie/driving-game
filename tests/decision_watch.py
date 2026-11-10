#!/usr/bin/env python3
# Read the decision rings of every car WHILE a scenario runs (the ring is 100
# entries, so a finished scenario has usually forgotten its start - see the
# MaxDecisions note in DrivingGame.Sim/Car.cs). Usage:
#   CAR_API_URL=http://localhost:5011 python3 tests/decision_watch.py <seconds>
import json
import os
import sys
import urllib.request

API = os.environ.get("CAR_API_URL", "http://127.0.0.1:5001").rstrip("/")
secs = float(sys.argv[1]) if len(sys.argv) > 1 else 40.0


def get(path):
    try:
        with urllib.request.urlopen(API + path, timeout=3) as r:
            return json.loads(r.read())
    except Exception:
        return None


def main():
    seen = {}
    t = 0.0
    while t < secs:
        st = get("/state")
        if st:
            for c in st.get("cars") or []:
                uid = c.get("car_uid")
                ring = get(f"/car/{uid}/decisions")
                if isinstance(ring, dict):
                    ring = ring.get("decisions") or []
                if not isinstance(ring, list):
                    continue
                for item in ring:
                    key = json.dumps(item, sort_keys=True)
                    if seen.get(uid, set()).__contains__(key):
                        continue
                    seen.setdefault(uid, set()).add(key)
                    msg = item.get("msg") if isinstance(item, dict) else str(item)
                    tm = item.get("t") if isinstance(item, dict) else "?"
                    print(f"  uid {uid} @ t={tm}: {str(msg)[:160]}", flush=True)
        elif seen:
            print("  (no cars on the road)", flush=True)
        t += 2.0
        time = __import__("time")
        time.sleep(2.0)


if __name__ == "__main__":
    main()
