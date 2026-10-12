#!/usr/bin/env python3
"""A/B summary of simrig runs: python3 harness/simrig/ab.py <result dir> [<result dir> ...] -- one line each: judge hf, streamer CPU (cores), boundary
interval tail, send time after the boundary, encode, viewer repeats. Used to compare optimisations (VISIONALVR_SIMEXE builds, --env knobs)."""
import csv, glob, json, sys
from pathlib import Path
import numpy as np
sys.path.insert(0, str(Path(__file__).parent))
import judge


def line(d):
    d = Path(d)
    j = judge.judge(str(d / "out"))
    cpu = json.loads((d / "cpu.json").read_text() or "{}") if (d / "cpu.json").exists() else {}
    vs = json.loads((d / "out" / "viewer_summary.json").read_text())
    rows = [r for r in csv.DictReader(open(glob.glob(str(d / "debug" / "host_frames.csv"))[0])) if r["frame"] != "-1" and r.get("t_send")][300:]
    tb = np.array([float(r["t_boundary"]) for r in rows]); ts = np.array([float(r["t_send"]) for r in rows]); en = np.array([float(r["encode_ms"]) for r in rows])
    dtb = np.diff(tb) * 1e3
    hf = max(j.get("eye0", {}).get("hf_rms_deg", 0), j.get("eye1", {}).get("hf_rms_deg", 0))
    return (f"{d.name[:22]:22s} {j['verdict'][:7]:7s} hf {hf:.3f}  cpu {cpu.get('cpu_s', 0) / max(1, cpu.get('wall_s', 1)):.2f} cores  "
            f"boundary p50 {np.median(dtb):.2f} p99 {np.percentile(dtb, 99):.2f} p99.9 {np.percentile(dtb, 99.9):.2f}  send-after-boundary p50 {np.median((ts - tb) * 1e3):.2f} p95 {np.percentile((ts - tb) * 1e3, 95):.2f}  "
            f"encode p50 {np.median(en):.2f} p95 {np.percentile(en, 95):.2f}  viewer repeats {100 * vs['repeats'] / max(1, vs['ticks']):.1f}%")


if __name__ == "__main__":
    for x in sys.argv[1:]:
        print(line(x))
