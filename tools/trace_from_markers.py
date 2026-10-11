#!/usr/bin/env python3
"""Turn a probe run's app_markers.csv (the pose the game got, with unix time) into a replayable head trace (the mock client's --replay format).
Under stock SteamVR the pose a game gets is the headset's sample unchanged (harness/steamvr_baseline.py), so this is a recording of the real head
at the probe's frame rate:   python3 tools/trace_from_markers.py <app_markers.csv> <out.csv> [--seconds 90]
The head position is the left-eye position minus the (rotated) eye offset (-31.06 mm); timestamps are the unix times in ns."""
import csv, sys
import numpy as np
src, dst = sys.argv[1], sys.argv[2]
secs = float(sys.argv[sys.argv.index("--seconds") + 1]) if "--seconds" in sys.argv else 1e9
def rot(q, v):
    x, y, z, w = q
    t = 2 * np.cross([x, y, z], v)
    return np.array(v) + w * t + np.cross([x, y, z], t)
rows = [r for r in csv.DictReader(open(src)) if r["eye"] == "0"]
t0 = int(rows[0]["wall_ns"])
out = []
for r in rows:
    q = [float(r[k]) for k in ("qx", "qy", "qz", "qw")]
    p = np.array([float(r[k]) for k in ("posx", "posy", "posz")]) - rot(q, [-0.031057563, 0.0, 0.0])
    t = (int(r["wall_ns"]) - t0) * 1e-9
    if t > secs:
        break
    out.append([f"{t:.6f}", int(r["wall_ns"]), *[f"{x:.7f}" for x in q], *[f"{x:.5f}" for x in p]])
with open(dst, "w", newline="") as f:
    w = csv.writer(f)
    w.writerow(["t", "ts_ns", "qx", "qy", "qz", "qw", "px", "py", "pz"])
    w.writerows(out)
print(dst, len(out), "rows")
