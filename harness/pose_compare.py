#!/usr/bin/env python3
"""What head pose does the game get, relative to the trace the headset (mock client) sent?  Works for our pipeline's replay runs and for the
SteamVR baseline (harness/steamvr_baseline.py): both leave poses.csv (every sample the client sent, with its unix send time) and app_markers.csv
(the pose the probe located for every frame, with its unix time).

    python3 harness/pose_compare.py <result dir> [<result dir> ...]

Per app frame: the probe's head orientation is compared with the samples sent shortly before. If it equals one (to float noise) the runtime passed
a sample through: its age is reported. If it equals none, the runtime transformed the pose (extrapolated, interpolated, smoothed): the trajectory
is searched (slerp between samples) for the time whose pose is closest, and the offset from the newest sent sample is the extrapolation."""
import csv, math, sys
from pathlib import Path
import numpy as np


def qang(a, b):
    x1, y1, z1, w1 = a
    x2, y2, z2, w2 = -b[0], -b[1], -b[2], b[3]
    vx = w1 * x2 + x1 * w2 + y1 * z2 - z1 * y2
    vy = w1 * y2 - x1 * z2 + y1 * w2 + z1 * x2
    vz = w1 * z2 + x1 * y2 - y1 * x2 + z1 * w2
    w = w1 * w2 - x1 * x2 - y1 * y2 - z1 * z2
    return math.degrees(2 * math.atan2(math.sqrt(vx * vx + vy * vy + vz * vz), abs(w)))


def slerp(a, b, f):
    d = float(np.dot(a, b))
    if d < 0:
        b, d = -b, -d
    if d > 0.9995:
        q = a * (1 - f) + b * f
        return q / np.linalg.norm(q)
    th = math.acos(min(1, d))
    return (math.sin((1 - f) * th) * a + math.sin(f * th) * b) / math.sin(th)


def pct(v, p):
    v = sorted(v)
    return v[min(len(v) - 1, int(p * len(v)))] if v else float("nan")


def stats(v):
    return f"n={len(v)} p05 {pct(v,.05):8.2f} p50 {pct(v,.5):8.2f} p95 {pct(v,.95):8.2f} max {max(v) if v else float('nan'):8.2f}"


def analyse(d):
    d = Path(d)
    poses = list(csv.DictReader(open(d / "poses.csv")))
    marks = [r for r in csv.DictReader(open(d / "app_markers.csv")) if r["eye"] == "0"]
    if "wall_ns" not in poses[0] or "wall_ns" not in marks[0]:
        print(d, ": poses.csv / app_markers.csv have no wall_ns (older run)")
        return
    T = np.array([int(r["wall_ns"]) for r in poses]) * 1e-9
    Q = np.array([[float(r[k]) for k in ("qx", "qy", "qz", "qw")] for r in poses])
    order = np.argsort(T)
    T, Q = T[order], Q[order]
    t0 = T[0] + 3.0                    # skip the connection / restart warm-up
    exact_age, ext, resid, speed = [], [], [], []
    n_exact = n_other = 0
    for r in marks:
        ta = int(r["wall_ns"]) * 1e-9
        if ta < t0 or ta > T[-1]:
            continue
        aq = np.array([float(r[k]) for k in ("qx", "qy", "qz", "qw")])
        hi = int(np.searchsorted(T, ta))      # samples sent so far are T[:hi]
        lo = max(0, hi - 80)
        if hi - lo < 5:
            continue
        angs = np.array([qang(aq, Q[i]) for i in range(lo, hi)])
        j = int(np.argmin(angs))
        sp = qang(Q[max(lo, lo + j - 1)], Q[min(hi - 1, lo + j + 1)]) / max(1e-6, (T[min(hi - 1, lo + j + 1)] - T[max(lo, lo + j - 1)]))
        speed.append(sp)
        if angs[j] < 0.01:
            n_exact += 1
            exact_age.append((ta - T[lo + j]) * 1e3)
        else:
            n_other += 1
            # the closest point of the (slerped) trajectory, searched from 100 ms before the best sample to 300 ms after the newest
            best = (1e9, 0.0)
            for tt in np.arange(T[lo + j] - 0.1, T[hi - 1] + 0.3, 0.0005):
                if tt <= T[0] or tt >= T[-1]:
                    continue
                i = int(np.searchsorted(T, tt))
                f = (tt - T[i - 1]) / (T[i] - T[i - 1]) if T[i] > T[i - 1] else 0.0
                e = qang(aq, slerp(Q[i - 1], Q[i], f))
                if e < best[0]:
                    best = (e, tt)
            resid.append(best[0])
            ext.append((best[1] - T[hi - 1]) * 1e3)     # >0: ahead of the newest sample the client had sent (extrapolated)
    tot = n_exact + n_other
    print(f"== {d.name}: {tot} app frames compared with {len(T)} sent samples")
    print(f"   pose equals a sent sample (passed through): {n_exact} ({100*n_exact/max(1,tot):.1f}%)  -> its age at the app (ms): {stats(exact_age)}")
    print(f"   pose equals none ({n_other}, {100*n_other/max(1,tot):.1f}%): the runtime transformed it; residual to the closest point of the trajectory (deg): {stats(resid)}")
    print(f"      where on the trajectory it sits relative to the newest sent sample (ms, > 0 = extrapolated ahead): {stats(ext)}")
    print(f"   head speed during the run (deg/s): {stats(speed)}")


if __name__ == "__main__":
    for x in sys.argv[1:]:
        analyse(x)
