#!/usr/bin/env python3
"""Is every frame stamped with the timestamp of the pose it was rendered with?  Client side only: works on our pipeline's replay runs and on
the SteamVR baseline (harness/steamvr_baseline.py).

    python3 harness/stamp_check.py <result dir>

Each decoded picture carries the probe's frame number (16 blocks, top-left); the probe logged the head pose it rendered that frame with
(app_markers.csv); the client saw the frame's timestamp (frames.csv, in arrival order) and logged the pose it sent for every timestamp (poses.csv).
The angle between the pose the frame was rendered with and the client's pose at the timestamp it arrived under is the stamping error: the
client reprojects from the latter, so any error is visible as a wrong correction. Also: the decoded marker position vs where the stamped pose
puts it (pixels)."""
import csv, math, sys
from pathlib import Path
import numpy as np
sys.path.insert(0, str(Path(__file__).parent))
import frames as fr


def main(d):
    d = Path(d)
    poses = {int(r["target_ts_ns"]): np.array([float(r[k]) for k in ("qx", "qy", "qz", "qw")]) for r in csv.DictReader(open(d / "poses.csv"))}
    marks = {int(r["frame"]): np.array([float(r[k]) for k in ("qx", "qy", "qz", "qw")]) for r in csv.DictReader(open(d / "app_markers.csv")) if r["eye"] == "0"}
    rows = list(csv.DictReader(open(d / "frames.csv")))
    cts = [int(r["ts_ns"]) for r in rows]
    dec = fr.decode_stream(d / "client.hevc", len(cts))
    if not dec:
        print("decode failed")
        return
    ids, mk, (w, h) = dec
    best = None
    for off in range(-4, 5):
        errs = []
        for k, pid in enumerate(ids):
            j = k + off
            if pid == 65535 or not (0 <= j < len(cts)) or pid not in marks or cts[j] not in poses:
                continue
            errs.append(fr.qang(marks[pid], poses[cts[j]]))
        if len(errs) > 200 and (best is None or np.median(errs) < best[0]):
            best = (np.median(errs), off, errs)
    if not best:
        print("no usable pairs")
        return
    _, off, errs = best
    e = np.array(errs)
    print(f"== {d.name}: {len(e)} frames (picture <-> client row offset {off})")
    print(f"   angle between the pose the frame was rendered with and the client's pose at the frame's timestamp (deg): "
          f"p50 {np.percentile(e,50):.4f} p95 {np.percentile(e,95):.4f} p99 {np.percentile(e,99):.4f} max {e.max():.4f}")
    print(f"   exact (< 0.005 deg): {100*(e<0.005).mean():.1f}%   off by more than 0.1 deg: {100*(e>0.1).mean():.1f}%   more than 1 deg: {100*(e>1).mean():.1f}%")
    # how far off in time: which sample does the rendered pose actually belong to?
    ks = sorted(poses)
    Q = np.array([poses[k] for k in ks])
    shifts = []
    for k, pid in enumerate(ids):
        j = k + off
        if pid == 65535 or not (0 <= j < len(cts)) or pid not in marks or cts[j] not in poses:
            continue
        i0 = ks.index(cts[j]) if cts[j] in ks else None
        if i0 is None or i0 < 6 or i0 > len(ks) - 7:
            continue
        a = [fr.qang(marks[pid], Q[i0 + dd]) for dd in range(-6, 7)]
        if min(a) < 0.005 and np.ptp([fr.qang(Q[i0 + dd], Q[i0 + dd + 1]) for dd in range(-6, 6)]) > 0.05:
            shifts.append(int(np.argmin(a)) - 6)
    if shifts:
        import collections
        c = collections.Counter(shifts)
        print("   while the head moves: the rendered pose is the client's sample at timestamp offset (samples):", sorted(c.items()))


def modeled(d):
    """The display as the mock client emulates it: per shown frame, the rotation between its pose and the head at display time (the trace)."""
    d = Path(d)
    P = list(csv.DictReader(open(d / "poses.csv")))
    ks = np.array([int(r["target_ts_ns"]) for r in P], dtype=float)
    sendt = np.array([int(r["send_ns"]) * 1e-9 for r in P])
    Q = np.array([[float(r[k]) for k in ("qx", "qy", "qz", "qw")] for r in P])
    pos = {int(r["target_ts_ns"]): (np.array([float(r[k]) for k in ("qx", "qy", "qz", "qw")])) for r in P}
    shift = float(np.median(ks - sendt * 1e9))
    D = list(csv.DictReader(open(d / "display.csv")))
    # the client clock of display.csv is the same origin as poses.csv send_ns; the frames.csv arrival gives the host<->client offset 0 here
    rot, steps, last = [], [], None
    shown = []
    for r in D[200:]:
        if int(r["repeat"]):
            continue
        ts = int(r["shown_ts_ns"])
        tick = int(r["tick_ns"]) * 1e-9
        t = tick * 1e9 + shift
        i = int(np.searchsorted(ks, t))
        if i <= 0 or i >= len(ks) or ts not in pos:
            continue
        f = (t - ks[i - 1]) / (ks[i] - ks[i - 1])
        qd = fr_slerp(Q[i - 1], Q[i], f)
        rot.append(fr.qang(pos[ts], qd))
        shown.append(ts)
    st = [round((b - a) / 1e7) for a, b in zip(shown, shown[1:])]
    import collections
    c = collections.Counter(st)
    ticks = len(D) - 200
    reps = sum(int(r["repeat"]) for r in D[200:])
    print(f"   display: {ticks} ticks, repeats {100*reps/ticks:.1f}%; ts step between consecutive shown frames: {sorted(c.items())[:7]}")
    if rot:
        print(f"   rotation the client must supply at display (deg): p50 {np.percentile(rot,50):.3f} p95 {np.percentile(rot,95):.3f} p99 {np.percentile(rot,99):.3f} max {max(rot):.2f}")


def fr_slerp(a, b, f):
    if float(np.dot(a, b)) < 0:
        b = -b
    q = a * (1 - f) + b * f
    return q / np.linalg.norm(q)


if __name__ == "__main__":
    for x in sys.argv[1:]:
        main(x)
        modeled(x)
