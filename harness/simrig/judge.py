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
Verdict: TREMBLE if hf rms > the hf threshold (default 0.04 deg, about 0.7 px on the Vision Pro) or p99 |residual| > the p99 threshold (0.15 deg).

In-scene motion (frame generation, docs/FRAME_GENERATION.md): with XR_PROBE_MOVING the probe draws a rectangle swinging smoothly in world yaw on a row below
the ring and the viewer writes moving.csv. Its world angle per tick (measured angle + displayed yaw) should move smoothly whatever the game's frame rate; a
repeated frame freezes it for a tick (a step of ~0.4 deg at 40 deg/s and 100 Hz) and a generated frame should not. Reported under "motion": hf rms (deg),
p99 of the hf residual, frozen display periods (a new frame whose marker did not move, or a timestamp gap the host did not fill) and the spread of
step / speed (0 = perfectly regular), all measured per distinct shown frame against its timestamp (the rig's viewer wakes up late); verdict
field "motion": SMOOTH if hf rms <= the motion threshold (default 0.10 deg: the generator-off 90 fps reference on the 3080 Ti rig reads 0.07) and
frozen <= 5 %, else JUDDER. Independent of the TREMBLE/CLEAN verdict."""
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


def motion(d, meta, skip=150, thr_hf=0.10, thr_frozen=5.0):
    """The moving marker's smoothness (see the module doc). None when the run had no marker.

    Measured per distinct shown frame against the frame's own timestamp, not per viewer tick: the rig's viewer wakes up 4-5 ms late on
    average (GPU contention), so what it catches at a tick is a matter of luck (the ideal 90 fps control read 5.5 % frozen and 46 double
    steps that way, 2026-10-14). Frozen display periods: a new frame whose marker did not move (a host repeat), and a timestamp gap of
    k periods (k-1 periods the headset would have repeated), unless the host did send frames inside the gap (../debug/host_frames.csv: the
    viewer missed them, counted as viewer_missed). A residual over 1.5 deg is a measurement glitch (the marker never moves that far in a
    period) and is counted apart."""
    f = Path(d) / "moving.csv"
    if not f.exists():
        return None
    rows = list(csv.reader(open(f)))[1:]
    sent = None
    hf_file = Path(d).parent / "debug" / "host_frames.csv"
    if hf_file.exists():
        sent = np.array(sorted(int(r["ts_ns"]) for r in csv.DictReader(open(hf_file)) if r.get("ts_ns")))
    W = meta["eye_w"]
    tl, tr = meta["tl"], meta["tr"]
    ppt = W / (tl + tr)
    out = {}
    worst_hf, worst_frozen = 0.0, 0.0
    any_eye = False
    for eye in (0, 1):
        etl = meta["tl_eye"][eye] if "tl_eye" in meta else tl
        eppt = meta["ppt_eye"][eye] if "ppt_eye" in meta else ppt
        first, total = {}, 0
        t0 = t1 = None
        for r in rows:
            tick, shown, yaw, e, n = int(r[0]), int(r[2]), float(r[4]), int(r[5]), int(r[6])
            if e != eye or tick < skip:
                continue
            total += 1
            if t0 is None:
                t0 = (tick, shown)
            t1 = (tick, shown)
            if n != 1 or shown in first:
                continue
            first[shown] = math.degrees(math.atan(float(r[7]) / eppt - etl)) + yaw   # world angle of the marker
        if len(first) < 40 or t1[0] == t0[0]:
            continue
        any_eye = True
        period = (t1[1] - t0[1]) / (t1[0] - t0[0])       # one display period in timestamp units (the viewer runs at the display rate)
        ts = np.array(sorted(first))
        a = np.array([first[k] for k in ts])
        t = (ts - ts[0]) / period                        # time in display periods
        n = len(a)
        sm, vel = np.empty(n), np.empty(n)
        for i in range(n):                                # local quadratic in time: follows the swing's curvature, unbent by gaps
            lo, hi = max(0, i - 4), min(n, i + 5)
            x = t[lo:hi] - t[i]
            p = np.polyfit(x, a[lo:hi], 2) if hi - lo >= 4 else np.array([0.0, 0.0, a[i]])
            sm[i], vel[i] = p[2], p[1]
        h = a - sm
        glitch = np.abs(h) > 1.5
        hc = h[~glitch]
        dt = np.diff(t)
        steps = np.abs(np.diff(a))
        speed = np.abs(vel[1:]) * dt                      # how much the marker should have moved between the two frames
        ok = ~glitch[1:] & ~glitch[:-1]
        med = float(np.median(speed[ok])) if ok.sum() else 0.0
        moving = ok & (speed > 0.5 * med)                 # the ends of the swing (speed near 0) say nothing about repeats
        adjacent = dt < 1.5
        content_frozen = int(np.sum(moving & adjacent & (steps < 0.2 * speed))) if med > 0 else 0
        gap_periods, viewer_missed = 0, 0
        for i in np.nonzero(~adjacent)[0]:
            k = int(round(dt[i])) - 1
            if sent is not None:
                inside = int(np.sum((sent > ts[i]) & (sent < ts[i + 1])))
                viewer_missed += min(inside, k)
                k -= min(inside, k)
            gap_periods += max(k, 0)
        periods = n + gap_periods + viewer_missed
        frozen = (content_frozen + gap_periods) / max(1, periods) * 100
        cv = float(np.std(steps[moving & adjacent] / speed[moving & adjacent])) if (moving & adjacent).sum() > 20 else 0.0
        out[f"eye{eye}"] = {"frames": int(n), "periods": int(periods), "glitches": int(glitch.sum()), "content_frozen": content_frozen, "gap_periods": gap_periods,
                            "viewer_missed": viewer_missed, "hf_rms_deg": float(np.sqrt(np.mean(hc ** 2))) if len(hc) else 0.0,
                            "hf_p99_deg": float(np.percentile(np.abs(hc), 99)) if len(hc) else 0.0, "step_median_deg": med, "frozen_pct": float(frozen),
                            "step_cv": cv, "visible_pct": float(len(first)) / max(1, len({int(r[2]) for r in rows if int(r[5]) == eye and int(r[0]) >= skip})) * 100}
        worst_hf = max(worst_hf, out[f"eye{eye}"]["hf_rms_deg"])
        worst_frozen = max(worst_frozen, frozen)
    if not any_eye:
        out["verdict"] = "NO MARKER (moving.csv has no single-rectangle rows: XR_PROBE_MOVING off, or the marker out of view)"
        return out
    out["verdict"] = "SMOOTH" if (worst_hf <= thr_hf and worst_frozen <= thr_frozen) else "JUDDER"
    out["thresholds"] = {"hf": thr_hf, "frozen_pct": thr_frozen}
    return out


def judge(d, thr=None, skip=150):
    thr = thr or {"hf": 0.04, "p99": 0.15}
    meta, ticks = load(d)
    ticks = {k: v for k, v in ticks.items() if k >= skip}
    late = 0
    tf = Path(d) / "ticks.csv"
    if tf.exists():   # ticks where the viewer thread itself woke up late (an OS hiccup of the rig, not of the pipeline) are not measurements
        rows = {int(r["tick"]): float(r.get("late_ms") or 0) for r in csv.DictReader(open(tf))}
        keep = {k: v for k, v in ticks.items() if rows.get(k, 0) <= 1.0}
        late = len(ticks) - len(keep)
        ticks = keep   # the first 1.5 s: the app is still starting (its first frames are irregular)
    tt, res = residuals(meta, ticks)
    out = {"late_viewer_ticks_dropped": late, "ticks": len(tt), "reprojected_share": float(np.mean([ticks[k]["reproj"] for k in tt])) if tt else 0}
    allr = []
    for eye in (0, 1):
        r = np.array([x for x in res[eye] if x is not None])
        if len(r) < 20:
            continue
        h = hf(r)
        spikes = np.abs(h) > 0.3   # isolated glitches (a dot lost at the image edge, one skipped tick): counted apart, they do not make a tremble by themselves
        hc = h[~spikes]
        out[f"eye{eye}"] = {"rms_deg": float(np.sqrt(np.mean(r ** 2))), "p99_deg": float(np.percentile(np.abs(r - np.median(r)), 99)), "hf_rms_deg": float(np.sqrt(np.mean(hc ** 2))),
                            "bias_deg": float(r.mean()), "spikes": int(spikes.sum()), "spike_rate": float(spikes.mean())}
        allr.append(r)
    both = [(a, b) for a, b in zip(res[0], res[1]) if a is not None and b is not None]
    if both:
        dsp = np.array([b - a for a, b in both])
        out["disparity_rms_deg"] = float(np.sqrt(np.mean((dsp - dsp.mean()) ** 2)))
    worst_hf = max((out[f"eye{e}"]["hf_rms_deg"] for e in (0, 1) if f"eye{e}" in out), default=0)
    worst_p99 = max((out[f"eye{e}"]["p99_deg"] for e in (0, 1) if f"eye{e}" in out), default=0)
    spiky = any(out.get(f"eye{e}", {}).get("spike_rate", 0) > 0.01 for e in (0, 1))   # more than 1 tick in 100 off by 0.3 deg or more
    worst_p99 = 0 if not spiky and worst_hf <= thr["hf"] else worst_p99
    out["verdict"] = "INVALID (no dots measured: the scene or the frames are wrong)" if not allr else "TREMBLE" if (worst_hf > thr["hf"] or spiky or worst_p99 > thr["p99"]) else "CLEAN"
    out["thresholds"] = thr
    m = motion(d, meta, skip)
    if m is not None:
        out["motion"] = m
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
    # the moving marker (head still, yaw 0): a 25 deg / 0.25 Hz swing sampled every tick, or every other tick repeated (a 50 fps game on a 100 Hz display)
    def make_moving(d, repeat_every=0, noise_deg=0.01):
        with open(d / "moving.csv", "w", newline="") as f:
            w = csv.writer(f)
            w.writerow(["tick", "t_s", "shown_ts", "reproj", "yaw_deg", "eye", "n", "x..."])
            for k in range(600):
                kk = k - (k % repeat_every) if repeat_every else k
                a = 25.0 * math.sin(2 * math.pi * 0.25 * kk * 0.01) + rng.normal(0, noise_deg)
                for eye in (0, 1):
                    w.writerow([k, k * 0.01, kk, 1, 0.0, eye, 1, f"{(math.tan(math.radians(a)) + meta['tl']) * ppt:.2f}"])
    results = {}
    for name, nz, eo in (("ideal", 0.0, 0.0), ("quant_only", 0.02, 0.0), ("tremble_0.1deg", 0.10, 0.0), ("stereo_inconsistent", 0.0, 0.15)):
        results[name] = judge(make(nz, eo))
    d = make(0.0); make_moving(d); results["motion_smooth"] = judge(d)
    d = make(0.0); make_moving(d, repeat_every=2); results["motion_repeat2"] = judge(d)
    ok = (results["ideal"]["verdict"] == "CLEAN" and results["quant_only"]["verdict"] == "CLEAN"
          and results["tremble_0.1deg"]["verdict"] == "TREMBLE" and results["stereo_inconsistent"]["verdict"] == "TREMBLE"
          and results["motion_smooth"]["motion"]["verdict"] == "SMOOTH" and results["motion_repeat2"]["motion"]["verdict"] == "JUDDER")
    for k, v in results.items():
        m = v.get("motion") or {}
        print(f"  {k:22s} {v['verdict']:8s} eye0 hf {v.get('eye0',{}).get('hf_rms_deg',0):.4f} p99 {v.get('eye0',{}).get('p99_deg',0):.4f}  eye1 hf {v.get('eye1',{}).get('hf_rms_deg',0):.4f}  disparity {v.get('disparity_rms_deg',0):.4f}"
              + (f"  motion {m.get('verdict')} hf {m.get('eye0',{}).get('hf_rms_deg',0):.4f} frozen {m.get('eye0',{}).get('frozen_pct',0):.1f}%" if m else ""))
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
    out = judge(a[0], thr, int(a[a.index("--skip") + 1]) if "--skip" in a else 150)
    print(json.dumps(out, indent=1))
    if "--json" in a:
        Path(a[a.index("--json") + 1]).write_text(json.dumps(out, indent=1))
    sys.exit(0 if out["verdict"] == "CLEAN" else 1)
