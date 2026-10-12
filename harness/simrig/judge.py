#!/usr/bin/env python3
"""The simrig's judge: does the picture the viewer displayed tremble?

    python3 harness/simrig/judge.py <simrig result dir> [--thresholds hf=0.04,p99=0.15] [--json out.json]
    python3 harness/simrig/judge.py --selftest

The viewer (nvenc/hostlib/simviewer.cpp) writes dots.csv: for every display tick and eye, the centre (pixels, in the eye image) of each white rectangle it found
on the equator row of what it displayed, and the yaw of the pose it displayed for (the corrected image is exactly that pose's view of a world-fixed ring of
rectangles every `dot_deg` degrees). meta.json carries the eye width and the FoV tangents. For each tick the measured dot angles are compared with the
analytic ones; the residual series (degrees) says how far the picture is from a perfect rotation:
  rms / p99     the size of the error      hf   its high-frequency part (the residual minus a 9-tick moving average): what the eye sees as trembling
  disparity     the right eye's residual minus the left's: a stereo inconsistency (the two eyes rendered from different poses)
Verdict: TREMBLE if hf rms > the hf threshold (default 0.04 deg, about 0.7 px on the Vision Pro) or p99 |residual| > the p99 threshold (0.15 deg)."""
import csv, json, math, sys
from pathlib import Path
import numpy as np


def load(d):
    d = Path(d)
    meta = json.load(open(d / "meta.json"))
    rows = list(csv.reader(open(d / "dots.csv")))
    hdr, rows = rows[0], rows[1:]
    ticks = {}
    for r in rows:
        tick, t_s, ts, reproj, yaw, eye, n = int(r[0]), float(r[1]), int(r[2]), int(r[3]), float(r[4]), int(r[5]), int(r[6])
        xs = [float(x) for x in r[7:7 + n]]
        ticks.setdefault(tick, {"t": t_s, "ts": ts, "reproj": reproj, "yaw": yaw})[eye] = xs
    return meta, ticks


def residuals(meta, ticks):
    W = meta["eye_w"]
    tl, tr = meta["tl"], meta["tr"]
    ppt = W / (tl + tr)
    D = meta["dot_deg"]
    out = {0: [], 1: []}
    tt = []
    for k in sorted(ticks):
        tk = ticks[k]
        row = {}
        for eye in (0, 1):
            xs = tk.get(eye)
            if not xs:
                continue
            # per eye FoV: the right eye's tangents differ; meta may carry both
            etl = meta["tl_eye"][eye] if "tl_eye" in meta else tl
            eppt = meta["ppt_eye"][eye] if "ppt_eye" in meta else ppt
            meas = [math.degrees(math.atan(x / eppt - etl)) for x in xs]
            res = []
            for a in meas:
                # the nearest dot of the ring for this yaw: world angle kD, seen at kD - yaw
                e = (round((a + tk["yaw"]) / D) * D) - tk["yaw"]
                res.append(a - e)
            row[eye] = float(np.median(res)) if res else None
        if row:
            tt.append(k)
            for eye in (0, 1):
                out[eye].append(row.get(eye))
    return tt, out


def hf(series, w=9):
    s = np.array(series, dtype=float)
    if len(s) < w + 2:
        return s * 0
    k = np.ones(w) / w
    sm = np.convolve(np.pad(s, (w // 2, w // 2), mode="edge"), k, mode="valid")
    return s - sm[:len(s)]


def judge(d, thr=None):
    thr = thr or {"hf": 0.04, "p99": 0.15}
    meta, ticks = load(d)
    tt, res = residuals(meta, ticks)
    out = {"ticks": len(tt), "reprojected_share": float(np.mean([ticks[k]["reproj"] for k in tt])) if tt else 0}
    allr = []
    for eye in (0, 1):
        r = np.array([x for x in res[eye] if x is not None])
        if len(r) < 20:
            continue
        h = hf(r)
        out[f"eye{eye}"] = {"rms_deg": float(np.sqrt(np.mean(r ** 2))), "p99_deg": float(np.percentile(np.abs(r - np.median(r)), 99)), "hf_rms_deg": float(np.sqrt(np.mean(h ** 2))), "bias_deg": float(r.mean())}
        allr.append(r)
    both = [(a, b) for a, b in zip(res[0], res[1]) if a is not None and b is not None]
    if both:
        dsp = np.array([b - a for a, b in both])
        out["disparity_rms_deg"] = float(np.sqrt(np.mean((dsp - dsp.mean()) ** 2)))
    worst_hf = max((out[f"eye{e}"]["hf_rms_deg"] for e in (0, 1) if f"eye{e}" in out), default=0)
    worst_p99 = max((out[f"eye{e}"]["p99_deg"] for e in (0, 1) if f"eye{e}" in out), default=0)
    out["verdict"] = "INVALID (no dots measured: the scene or the frames are wrong)" if not allr else "TREMBLE" if (worst_hf > thr["hf"] or worst_p99 > thr["p99"]) else "CLEAN"
    out["thresholds"] = thr
    return out


def selftest():
    import tempfile
    rng = np.random.default_rng(1)
    meta = {"eye_w": 1760, "tl": 1.787, "tr": 1.020, "dot_deg": 10.0}
    ppt = meta["eye_w"] / (meta["tl"] + meta["tr"])

    def make(noise_deg, eye_off_deg=0.0):
        d = Path(tempfile.mkdtemp())
        (d / "meta.json").write_text(json.dumps(meta))
        with open(d / "dots.csv", "w", newline="") as f:
            w = csv.writer(f)
            w.writerow(["tick", "t_s", "shown_ts", "reproj", "yaw_deg", "eye", "n", "x..."])
            for k in range(400):
                yaw = 90.0 * k * 0.01
                for eye in (0, 1):
                    err = rng.normal(0, noise_deg) + (eye_off_deg * rng.normal() if eye else 0)
                    xs = []
                    for j in range(-12, 13):
                        a = ((round(yaw / 10.0) + j) * 10.0) - yaw + err     # dots of the ring seen at kD - yaw (+ the error)
                        if -50 < a < 30:
                            xs.append((math.tan(math.radians(a)) + meta["tl"]) * ppt)
                    w.writerow([k, k * 0.01, k, 1, yaw, eye, len(xs)] + [f"{x:.2f}" for x in xs])
        return d
    results = {}
    for name, nz, eo in (("ideal", 0.0, 0.0), ("quant_only", 0.02, 0.0), ("tremble_0.1deg", 0.10, 0.0), ("stereo_inconsistent", 0.0, 0.15)):
        results[name] = judge(make(nz, eo))
    ok = (results["ideal"]["verdict"] == "CLEAN" and results["quant_only"]["verdict"] == "CLEAN"
          and results["tremble_0.1deg"]["verdict"] == "TREMBLE" and results["stereo_inconsistent"]["verdict"] == "TREMBLE")
    for k, v in results.items():
        print(f"  {k:22s} {v['verdict']:8s} eye0 hf {v.get('eye0',{}).get('hf_rms_deg',0):.4f} p99 {v.get('eye0',{}).get('p99_deg',0):.4f}  eye1 hf {v.get('eye1',{}).get('hf_rms_deg',0):.4f}  disparity {v.get('disparity_rms_deg',0):.4f}")
    print("selftest", "PASS" if ok else "FAIL")
    return 0 if ok else 1


if __name__ == "__main__":
    a = sys.argv[1:]
    if not a or a[0] == "--selftest":
        sys.exit(selftest())
    thr = None
    if "--thresholds" in a:
        thr = {k: float(v) for k, v in (x.split("=") for x in a[a.index("--thresholds") + 1].split(","))}
        thr = {"hf": thr.get("hf", 0.04), "p99": thr.get("p99", 0.15)}
    out = judge(a[0], thr)
    print(json.dumps(out, indent=1))
    if "--json" in a:
        Path(a[a.index("--json") + 1]).write_text(json.dumps(out, indent=1))
    sys.exit(0 if out["verdict"] == "CLEAN" else 1)
