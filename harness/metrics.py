"""Chain-independent metrics from the mock client's artifacts (poses.csv, frames.csv, display.csv): the same numbers for our chain and for stock
ALVR + SteamVR, so the two can be compared 1:1.  python3 harness/metrics.py <result dir> [<result dir> ...]

All times are on the client's clock (the mock headset): a pose is "sent" at poses.csv send_ns; the frame rendered for it carries that pose's target
timestamp; it arrives (frames.csv arrival_ns), is decoded (decoded_ns) and is first shown at a display tick (display.csv tick_ns).
  pose->arrival  : head motion to the frame's last packet at the headset (host: wait for the game's frame, render, encode, send, network)
  pose->decoded  : plus the decoder
  pose->display  : plus the client's frame queue and the wait for the display tick (the motion-to-photon latency of the picture, before the client's own
                   late rotation correction)
  rotation       : the angle between the pose the shown picture was rendered with and the head at the display tick (what the headset's reprojection
                   must supply: smaller is better)"""
import csv, math, sys
from pathlib import Path
import numpy as np


def _rows(p):
    return list(csv.DictReader(open(p)))


def pct(v, q):
    return float(np.percentile(v, q)) if len(v) else float("nan")


def qang(a, b):
    x1, y1, z1, w1 = a
    x2, y2, z2, w2 = -b[0], -b[1], -b[2], b[3]
    vx = w1 * x2 + x1 * w2 + y1 * z2 - z1 * y2
    vy = w1 * y2 - x1 * z2 + y1 * w2 + z1 * x2
    vz = w1 * z2 + x1 * y2 - y1 * x2 + z1 * w2
    w = w1 * w2 - x1 * x2 - y1 * y2 - z1 * z2
    return math.degrees(2 * math.atan2(math.sqrt(vx * vx + vy * vy + vz * vz), abs(w)))


def slerp(a, b, f):
    if float(np.dot(a, b)) < 0:
        b = -b
    q = a * (1 - f) + b * f
    return q / np.linalg.norm(q)


def metrics(d, skip=200):
    d = Path(d)
    P = _rows(d / "poses.csv")
    F = _rows(d / "frames.csv")
    D = _rows(d / "display.csv")
    ks = np.array([int(r["target_ts_ns"]) for r in P], dtype=float)
    send = {int(r["target_ts_ns"]): int(r["send_ns"]) for r in P}
    quat = {int(r["target_ts_ns"]): np.array([float(r[k]) for k in ("qx", "qy", "qz", "qw")]) for r in P}
    Q = np.array([quat[int(k)] for k in ks])
    sendt = np.array([int(r["send_ns"]) for r in P], dtype=float)
    shift = float(np.median(ks - sendt))       # target timestamp clock -> client clock
    out = {"frames_received": len(F)}
    arr, dec, arr_t = [], [], []
    for r in F[skip // 2:]:
        ts = int(r["ts_ns"])
        if ts in send and r.get("arrival_ns"):
            a = (int(r["arrival_ns"]) - send[ts]) / 1e6
            arr.append(a)
            arr_t.append(int(r["arrival_ns"]))
            if r.get("decoded_ns"):
                dec.append((int(r["decoded_ns"]) - send[ts]) / 1e6)
    out["pose_to_arrival_ms"] = {"p50": pct(arr, 50), "p95": pct(arr, 95), "p99": pct(arr, 99)}
    out["pose_to_decoded_ms"] = {"p50": pct(dec, 50), "p95": pct(dec, 95), "p99": pct(dec, 99)}
    if len(arr_t) > 10:
        span = (arr_t[-1] - arr_t[0]) / 1e9
        out["frames_per_s"] = len(arr_t) / span if span > 0 else float("nan")
        gaps = np.diff(arr_t) / 1e6
        out["arrival_gap_ms"] = {"p50": pct(gaps, 50), "p95": pct(gaps, 95), "p99": pct(gaps, 99), "over_2x_p50": int((gaps > 2 * np.median(gaps)).sum())}
    # display: first showing of each timestamp, repeats, rotation to supply
    p2d, rot, shown, reps, ticks = [], [], [], 0, 0
    seen = set()
    for r in D[skip:]:
        ticks += 1
        if int(r["repeat"]):
            reps += 1
            continue
        ts = int(r["shown_ts_ns"])
        tick = int(r["tick_ns"])
        shown.append(ts)
        if ts in send and ts not in seen:
            p2d.append((tick - send[ts]) / 1e6)
            seen.add(ts)
        t = tick + shift
        i = int(np.searchsorted(ks, t))
        if 0 < i < len(ks) and ts in quat:
            qd = slerp(Q[i - 1], Q[i], (t - ks[i - 1]) / (ks[i] - ks[i - 1]))
            rot.append(qang(quat[ts], qd))
    out["display"] = {"ticks": ticks, "repeat_pct": 100.0 * reps / max(1, ticks)}
    out["pose_to_display_ms"] = {"p50": pct(p2d, 50), "p95": pct(p2d, 95), "p99": pct(p2d, 99)}
    out["rotation_deg"] = {"p50": pct(rot, 50), "p95": pct(rot, 95), "p99": pct(rot, 99)}
    raw = np.diff(np.array(shown)) if len(shown) > 1 else np.array([])
    steps = raw / np.median(raw) if len(raw) else raw   # in display periods (the median step)
    out["ts_step_periods"] = {"dup(0)": int((np.round(steps) == 0).sum()), "skip(>=2)": int((np.round(steps) >= 2).sum()), "n": len(steps)}
    return out


def line(name, m):
    a, p, r = m["pose_to_arrival_ms"], m["pose_to_display_ms"], m["rotation_deg"]
    return (f"{name:34s} fps {m.get('frames_per_s', float('nan')):5.1f}  p2arr p50 {a['p50']:5.1f} p95 {a['p95']:5.1f}  p2disp p50 {p['p50']:5.1f} p95 {p['p95']:5.1f}  "
            f"repeats {m['display']['repeat_pct']:4.1f}%  rot p50 {r['p50']:.2f} p95 {r['p95']:.2f}  dup/skip {m['ts_step_periods']['dup(0)']}/{m['ts_step_periods']['skip(>=2)']}")


if __name__ == "__main__":
    for x in sys.argv[1:]:
        print(line(Path(x).name[:34], metrics(x)))
