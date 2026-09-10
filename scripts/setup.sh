#!/usr/bin/env bash
# Post-clone environment setup. Run this once after cloning (and whenever a
# prerequisite changes):
#
#   scripts/setup.sh
#
# It verifies the system prerequisites and prepares everything that lives
# in the repo:
#   1. System checks: Godot (.NET build), .NET 9 SDK, Python 3.
#      (No Xcode needed - the Godot .NET build is self-contained.)
#   2. .venv with the Python deps tests/ and tools/ need
#      (requests, numpy, pillow).
#   3. Godot asset import (.godot/imported is gitignored, so a fresh clone
#      has no imported textures and the window would render without signs
#      and car sprites).
#   4. C# build (verifies the toolchain compiles all projects).
#   5. Port 5001 check (the game API port - we deliberately do NOT use
#      5000, where macOS AirPlay Receiver squats).
#
# System prerequisites that are missing cannot be installed by this script
# (they need admin rights / a download); it tells you exactly what to do.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

fail=0
ok()   { echo "  [ok]   $1"; }
warn() { echo "  [warn] $1"; }
err()  { echo "  [FAIL] $1"; fail=1; }

echo "==> 1/5 System prerequisites"

# --- Godot (.NET/Mono build; the runner expects 4.7.x) ---
GODOT_BIN="${GODOT_BIN:-/Applications/Godot_mono.app/Contents/MacOS/Godot}"
if [ -x "$GODOT_BIN" ]; then
  GODOT_VER=$("$GODOT_BIN" --version 2>/dev/null | head -1 || echo "version unknown")
  ok "Godot: $GODOT_BIN ($GODOT_VER)"
else
  err "Godot .NET build not found at: $GODOT_BIN"
  echo "       Download the .NET (Mono) build from https://godotengine.org/download"
  echo "       (4.7.x), install it to /Applications, or point GODOT_BIN at it."
fi

# --- .NET SDK (projects target net9.0) ---
if command -v dotnet >/dev/null 2>&1; then
  DOTNET_VER="$(dotnet --version 2>/dev/null || true)"
  case "$DOTNET_VER" in
    9.*|1[0-9].*|2[0-9].*) ok ".NET SDK: $DOTNET_VER" ;;
    *) err ".NET SDK: '${DOTNET_VER:-unusable}' (need 9.x - https://dotnet.microsoft.com/download/dotnet/9.0)" ;;
  esac
else
  err ".NET SDK not found (need 9.x - https://dotnet.microsoft.com/download/dotnet/9.0)"
fi

# --- Python 3 ---
if command -v python3 >/dev/null 2>&1; then
  ok "python3: $(python3 --version 2>&1 | awk '{print $2}')"
else
  err "python3 not found"
fi

echo "==> 2/5 Python venv (.venv)"
if [ ! -x "$ROOT/.venv/bin/python" ]; then
  if python3 -m venv .venv; then
    ok "created .venv"
  else
    err "python3 -m venv .venv failed"
  fi
else
  ok ".venv present"
fi
if [ -x "$ROOT/.venv/bin/python" ]; then
  if .venv/bin/python -c "import requests, numpy, PIL" >/dev/null 2>&1; then
    ok "python deps present (requests, numpy, pillow)"
  elif .venv/bin/python -m pip install --quiet requests numpy pillow; then
    ok "installed requests, numpy, pillow into .venv"
  else
    err "pip install of requests/numpy/pillow failed"
  fi
fi

echo "==> 3/5 Godot asset import (.godot/imported)"
if [ -x "$GODOT_BIN" ]; then
  if "$GODOT_BIN" --headless --path "$ROOT" --import >/dev/null 2>&1; then
    N_IMPORTED="$(ls "$ROOT/.godot/imported" 2>/dev/null | wc -l | tr -d ' ')"
    if [ "$N_IMPORTED" -gt 0 ]; then
      ok "$N_IMPORTED imported assets"
    else
      err "import finished but .godot/imported is empty"
    fi
  else
    err "Godot --import failed"
  fi
else
  warn "skipped (no Godot found)"
fi

echo "==> 4/5 C# build"
if command -v dotnet >/dev/null 2>&1; then
  if dotnet build DrivingGame.Server/DrivingGame.Server.csproj -v q --nologo && \
     dotnet build DrivingGame.csproj -v q --nologo; then
    ok "build OK (DrivingGame.Server + Godot project)"
  else
    warn "build failed - see the errors above (run the same dotnet build commands to retry)"
  fi
else
  warn "skipped (no .NET SDK found)"
fi

echo "==> 5/5 Port 5001 (game API)"
if lsof -nP -iTCP:5001 -sTCP:LISTEN >/dev/null 2>&1; then
  HOLDER="$(lsof -nP -iTCP:5001 -sTCP:LISTEN 2>/dev/null | awk 'NR==2 {print $1}')"
  warn "port 5001 is held by '${HOLDER:-?}' - the game API cannot start."
  echo "       Stop that process before running the game."
else
  ok "port 5001 free"
fi

echo ""
if [ "$fail" -eq 0 ]; then
  echo "==> Setup complete. You can now run: scripts/run_e2e.sh"
else
  echo "==> Setup finished with errors (see [FAIL] lines above)."
  exit 1
fi
