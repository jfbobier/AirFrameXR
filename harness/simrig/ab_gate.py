#!/usr/bin/env python3
"""Frame-start gate A/B (docs/PERFORMANCE_FINDINGS.md 2026-10-23): one line per simrig result dir.

    python3 harness/simrig/ab_gate.py harness/results/<run> [<run> ...]

Per run: app fps; release -> GPU done (shim CSV, misleading when the app woke before the release); release -> submit (the app's CPU work
after the release: 0.1 ms = the layers were already waiting); frame start -> GPU done (the probe's xrWaitFrame return joined to the shim's
publish on the display time: the real render queue); how long before the shim's release xrWaitFrame returned (0 = gated); the host's hold;
the app's wait and frame time; the shim's envelope and late releases; content lag (uncapped runs); host repeats; verdicts; GPU load."""
import csv, glob, json, re, sys
from pathlib import Path
import numpy as np
sys.path.insert(0, str(Path(__file__).resolve().parent))
import judge

def pct(a, p): return float(np.percentile(a, p)) if len(a) else float("nan")

def line(d):
    d = Path(d)
    shim = sorted(glob.glob(str(d / "debug" / "shim_frames_*.csv")))[-1]
    rows = [r for r in csv.DictReader(open(shim)) if r.get("t_release") and r.get("t_publish") and r.get("t_submit") and float(r["t_release"]) > 0 and float(r["t_publish"]) > 0][100:]
    rel = np.array([float(r["t_release"]) for r in rows]); sub = np.array([float(r["t_submit"]) for r in rows]); pub = np.array([float(r["t_publish"]) for r in rows])
    inflight = (pub - rel) * 1e3; cpu = (sub - rel) * 1e3
    fps = len(rows) / (rel[-1] - rel[0]) if len(rows) > 1 else 0
    # the app's own timing: xrWaitFrame return against the shim's release of the same frame (display time matches)
    by_dt = {int(r["display_time"].split(".")[0]) if "." not in r["display_time"] else r["display_time"]: r for r in rows}
    early = []; s2d = []
    try:
        app = list(csv.DictReader(open(d / "app_timing.csv")))
        rel_by_dt = {}
        for r in rows:
            rel_by_dt[round(float(r["display_time"]), 5)] = (float(r["t_release"]), float(r["t_publish"]))
        for a in app[100:]:
            k = round(int(a["display_time_ns"]) / 1e9, 5)
            if k in rel_by_dt:
                early.append(rel_by_dt[k][0] * 1e3 - float(a["t_wait_return_ms"]))
                s2d.append(rel_by_dt[k][1] * 1e3 - float(a["t_wait_return_ms"]))
        wait = np.array([float(a["t_wait_return_ms"]) - float(b["t_end_ms"]) for a, b in zip(app[101:], app[100:-1])])
        ft = np.array([float(a["t_end_ms"]) - float(a["t_wait_return_ms"]) for a in app[100:]])
    except FileNotFoundError:
        wait = ft = np.array([])
    early = np.array(early); s2d = np.array(s2d)
    log = sorted(glob.glob(str(d / "debug" / "shim_xr_probe_*.log")))[-1]
    pac = [l for l in open(log, errors="replace") if "pacing: running start" in l]
    m = re.search(r"envelope ([0-9.]+) ms, releases (\d+) \(late (\d+)\)", pac[-1]) if pac else None
    late = 100 * int(m.group(3)) / max(1, int(m.group(2))) if m else float("nan")
    env = float(m.group(1)) if m else float("nan")
    fg = None
    for l in open(d / "debug" / "host_events.jsonl", errors="replace"):
        if '"frame_gen":{' in l:
            try: fg = json.loads(l)
            except Exception: pass
    rep = ""
    lag = float("nan")
    if fg:
        g = fg.get("data", {}).get("pipeline", {}).get("frame_gen")
        if g:
            tot = g["synths"] + g["reals"] + g["repeats"]
            rep = f"repeats {100 * g['repeats'] / max(1, tot):.1f}% holds {g['holds']}"
    fu = d / "debug" / "fg_uncapped.csv"
    if fu.exists():
        lags = [float(r["lag_ms"]) for r in csv.DictReader(open(fu)) if r.get("lag_ms")]
        lag = float(np.median(lags[200:])) if len(lags) > 200 else float("nan")
    j = judge.judge(str(d / "out"))
    mo = j.get("motion", {})
    m0 = mo.get("eye0", {})
    gpu = [r for r in csv.DictReader(open(d / "debug" / "gpu.csv")) if r.get("gpu_pct") and r.get("power_w")]
    gp = np.mean([float(r["gpu_pct"]) for r in gpu[5:]]) if len(gpu) > 5 else float("nan")
    pw = np.mean([float(r["power_w"]) for r in gpu[5:]]) if len(gpu) > 5 else float("nan")
    hf_ = [r for r in csv.DictReader(open(d / "debug" / "host_frames.csv")) if r.get("hold_ms") and r["frame"] != "-1"]
    hold = np.array([float(r["hold_ms"]) for r in hf_[300:]])
    return (f"{d.name[:16]} {d.name[24:]:14s} app {fps:5.1f} fps | release->done p50 {pct(inflight,50):5.1f} p95 {pct(inflight,95):5.1f} ms | release->submit p50 {pct(cpu,50):4.1f} ms | "
            f"frame start->GPU done p50 {pct(s2d,50):5.1f} p95 {pct(s2d,95):5.1f} ms | xrWaitFrame return before release p50 {pct(early,50):5.1f} ms | host hold p50 {pct(hold,50):4.1f} | wait p50 {pct(wait,50):4.1f} frame p50 {pct(ft,50):4.1f} ms | envelope {env:4.1f} late {late:4.0f}% | "
            f"lag {lag:5.1f} | {rep} | {j['verdict']} {mo.get('verdict','')} frozen {m0.get('frozen_pct',0):.1f}% cv {m0.get('step_cv',0):.2f} | gpu {gp:3.0f}% {pw:3.0f} W")

if __name__ == "__main__":
    for x in sys.argv[1:]:
        try: print(line(x))
        except Exception as e: print(x, "ERROR", e)
