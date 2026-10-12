#!/usr/bin/env python3
"""Timing profile for the simrig from a real debug session (a headset session of our pipeline):

    python3 harness/simrig/make_profile.py logs/pilot4/20261011-195422 harness/simrig/profiles/real_cyberpunk.json [--game Cyberpunk]

The simrig (host feature `simrig`, see docs/DEVELOPMENT.md) replaces ALVR, the network and the headset by timers and a viewer. Each stage's delay is drawn
from this profile: an empirical resample of the measured values (or a gaussian with the measured mean and sd when a stage has few), always with a seed
so a run can be repeated exactly. Stages (all in milliseconds unless noted):
  uplink      head sample arrival delay beyond its own timestamp (the headset's uplink jitter, FIFO: a late sample holds the ones behind it)
  encode      the host's encode time        network   ALVR's transport latency        decode   the client's decode time
  queue       the time a decoded frame waits for the display (the client's vsync queue)
  tick        the client's display period (ns) and its tick jitter (sd, ns)
  omega_noise the head angular-velocity noise (deg/s sd) that the client's prediction multiplies by its horizon (from a still stretch of the recording)
"""
import csv, json, math, sys
from pathlib import Path
import numpy as np


def rd(p):
    try:
        return list(csv.DictReader(open(p)))
    except Exception:
        return []


def dist(v, keep=3000):
    v = np.array([x for x in v if x == x], dtype=float)
    if len(v) == 0:
        return {"n": 0, "mean": 0.0, "sd": 0.0, "samples": []}
    step = max(1, len(v) // keep)
    return {"n": int(len(v)), "mean": float(v.mean()), "sd": float(v.std()), "p50": float(np.median(v)), "p95": float(np.percentile(v, 95)),
            "samples": [round(float(x), 4) for x in v[::step]]}


def main():
    a = sys.argv[1:]
    d, out = Path(a[0]), Path(a[1])
    hs = rd(d / "head_samples.csv")
    ts = np.array([int(r["ts_ns"]) for r in hs])
    t = np.array([float(r["t"]) for r in hs])
    q = np.array([[float(r[k]) for k in ("qx", "qy", "qz", "qw")] for r in hs])
    ok = np.linalg.norm(q, axis=1) > 0.5
    ts, t, q = ts[ok], t[ok], q[ok]
    # one connection: the longest run without a 0.5 s hole
    gaps = np.where(np.diff(t) > 0.5)[0]
    starts, ends = [0] + list(gaps + 1), list(gaps + 1) + [len(t)]
    s, e = max(zip(starts, ends), key=lambda x: x[1] - x[0])
    ts, t, q = ts[s:e], t[s:e], q[s:e]
    rel = (t - ts * 1e-9)
    uplink = (rel - np.median(rel)) * 1e3
    prof = {"source": str(d), "uplink_ms": dist(uplink)}
    d_ts = np.diff(ts).astype(float)
    d_ts = d_ts[(d_ts > 4e6) & (d_ts < 25e6)]
    period = float(np.median(d_ts))
    prof["tick"] = {"period_ns": period, "jitter_ns_sd": float(np.std(d_ts[np.abs(d_ts - period) < 2e5]))}
    # angular velocity noise: the second difference of the orientation where the head is (nearly) still
    def qang(a_, b_):
        x1, y1, z1, w1 = a_
        x2, y2, z2, w2 = -b_[0], -b_[1], -b_[2], b_[3]
        vx = w1 * x2 + x1 * w2 + y1 * z2 - z1 * y2
        vy = w1 * y2 - x1 * z2 + y1 * w2 + z1 * x2
        vz = w1 * z2 + x1 * y2 - y1 * x2 + z1 * w2
        w = w1 * w2 - x1 * x2 - y1 * y2 - z1 * z2
        return math.degrees(2 * math.atan2(math.sqrt(vx * vx + vy * vy + vz * vz), abs(w)))
    sec, spd = [], []
    for i in range(2, len(q) - 2):
        dt = (ts[i + 2] - ts[i - 2]) * 1e-9
        if dt <= 0 or dt > 0.08:
            continue
        spd.append(qang(q[i - 2], q[i + 2]) / dt)
        m = (q[i - 1] + q[i + 1]) / 2
        m /= np.linalg.norm(m)
        sec.append(qang(q[i], m))
    spd, sec = np.array(spd), np.array(sec)
    still = spd < 5
    # the 2nd difference of a quantity with white velocity noise s (per sample interval T) has sd ~ s*T*sqrt(1.5): s = sd / (T*sqrt(1.5))
    T = period * 1e-9
    prof["omega_noise_dps"] = float(np.std(sec[still]) / (T * math.sqrt(1.5))) if still.sum() > 50 else 3.0
    prof["head"] = {"speed_dps": dist(spd, 500), "still_share": float(still.mean())}
    # host side
    game = a[a.index("--game") + 1] if "--game" in a else ""
    pipe = [r for r in rd(d / "host_pipeline.csv") if int(r["frame"]) >= 0]
    prof["encode_ms"] = dist([float(r["encode_ms"]) for r in pipe])
    prof["bytes"] = dist([float(r["bytes"]) for r in pipe], 1500)
    cg = rd(d / "client_graph.csv")
    prof["network_ms"] = dist([float(r["network_ms"]) for r in cg])
    prof["decode_ms"] = dist([float(r["decode_ms"]) for r in cg])
    prof["queue_ms"] = dist([float(r["decoder_queue_ms"]) + float(r["vsync_queue_ms"]) for r in cg])
    prof["total_ms"] = dist([float(r["total_ms"]) for r in cg])
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(prof))
    print(f"{out}: uplink sd {prof['uplink_ms']['sd']:.2f} ms (p95 {prof['uplink_ms']['p95']:.1f}), tick {period/1e6:.3f} ms +-{prof['tick']['jitter_ns_sd']:.0f} ns, "
          f"omega noise {prof['omega_noise_dps']:.2f} deg/s, encode {prof['encode_ms']['mean']:.2f} ms, network {prof['network_ms']['mean']:.1f} ms, decode {prof['decode_ms']['mean']:.1f} ms, "
          f"queue {prof['queue_ms']['mean']:.1f} ms")


if __name__ == "__main__":
    main()
