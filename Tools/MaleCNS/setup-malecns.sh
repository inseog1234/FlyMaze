#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
CACHE="$ROOT/Library/MaleCNS"
VENV="$CACHE/venv"
mkdir -p "$CACHE"
python3 -m venv "$VENV"
"$VENV/bin/python" -m pip install --disable-pip-version-check --upgrade pip
"$VENV/bin/python" -m pip install --disable-pip-version-check numpy scipy pandas pyarrow
"$VENV/bin/python" "$ROOT/Tools/MaleCNS/build_malecns_v1.py" --data "$CACHE"
echo "MaleCNS v1.0 is ready. Return to Unity and press Play."
