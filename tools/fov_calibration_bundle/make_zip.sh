#!/usr/bin/env bash
# Builds private/dist/AirFrameXR-fov-calibration.zip: the probe from the builder (build\probe\xr_probe.exe, run.py build probe) as fov_sweep.exe,
# the launcher and the instructions. The zip is for the people who run the calibration (docs/FOV_CALIBRATION.md); not committed.
set -euo pipefail
cd "$(dirname "$0")/../.."
tmp="$(mktemp -d)"
bash tools/builder.sh get build/probe/xr_probe.exe "$tmp/xr_probe.exe"
python3 - "$tmp" <<'PY'
import sys, zipfile
tmp = sys.argv[1]
with zipfile.ZipFile("private/dist/AirFrameXR-fov-calibration.zip", "w", zipfile.ZIP_DEFLATED) as z:
    z.write(f"{tmp}/xr_probe.exe", "calibration/fov_sweep.exe")
    z.write("tools/fov_calibration_bundle/run_sweep.bat", "calibration/run_sweep.bat")
    z.write("tools/fov_calibration_bundle/README.txt", "calibration/README.txt")
print("private/dist/AirFrameXR-fov-calibration.zip")
PY
