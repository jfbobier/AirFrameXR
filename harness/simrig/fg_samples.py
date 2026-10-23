#!/usr/bin/env python3
"""Stamp-regime summary of simrig runs (pipeline.rs fg_hold_age): python3 harness/simrig/fg_samples.py <result dir> [...]
Per run, from the last encoder_stats event: outputs, no_sample, hold_skips, hold_bypassed, ts_jumps, in_hand share, repeats, and the judge's verdicts."""
import json, sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parent))
import judge

for d in sys.argv[1:]:
    d = Path(d)
    g = None
    for l in open(d / "debug" / "host_events.jsonl", errors="replace"):
        if '"frame_gen":{' in l:
            try: g = json.loads(l)["data"]["pipeline"]["frame_gen"]
            except Exception: pass
    if not g:
        print(d.name, "no frame_gen counters"); continue
    out = g["synths"] + g["reals"] + g["repeats"]
    j = judge.judge(str(d / "out")); mo = j.get("motion", {})
    print(f"{d.name[:16]} {d.name[24:]:16s} outputs {out:5d} no_sample {g['no_sample']:3d} hold_skips {g.get('hold_skips', '-'):>3} bypassed {g.get('hold_bypassed', '-'):>3} "
          f"switches {g.get('regime_switches', '-'):>3} in_hand {100 * g['in_hand'] / max(1, out):5.1f}% repeats {100 * g['repeats'] / max(1, out):4.1f}% hold_age {g.get('hold_age', '-')} | "
          f"{j['verdict']} {mo.get('verdict', '')} frozen {mo.get('eye0', {}).get('frozen_pct', 0):.1f}%")
